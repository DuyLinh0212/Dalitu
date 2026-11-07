from __future__ import annotations

import argparse
import copy
import json
import platform
import statistics
import sys
from datetime import UTC, datetime
from pathlib import Path
from time import perf_counter
from typing import Any

import matplotlib
import numpy as np
import pandas as pd

matplotlib.use("Agg")
from matplotlib import pyplot as plt  # noqa: E402

SERVICE_ROOT = Path(__file__).resolve().parents[1]
if str(SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(SERVICE_ROOT))

from app.data.mapping import build_mappings, encode_interactions  # noqa: E402
from app.data.movielens import load_ml1m, load_ml100k  # noqa: E402
from app.recommenders.batch_incremental_item_cf import (  # noqa: E402
    BatchIncrementalItemBasedCFPartialTopK,
)
from app.recommenders.item_cf import ItemBasedCF  # noqa: E402
from app.recommenders.similarity import select_positive_neighbors  # noqa: E402

_CHART_SERIES = (
    ("full_rebuild", "Full rebuild", "#64748b", "full_rebuild_seconds"),
    (
        "batch_gram_partial_topk",
        "Batch Gram + partial Top-K",
        "#16a34a",
        "batch_gram_partial_topk_seconds",
    ),
)


def _copy_model(
    model: BatchIncrementalItemBasedCFPartialTopK,
) -> BatchIncrementalItemBasedCFPartialTopK:
    # Copy outside the timed region: this represents an already-resident model.
    return copy.deepcopy(model)


def _timed_full_rebuild(
    snapshot: pd.DataFrame,
    *,
    user_count: int,
    item_count: int,
    neighbor_count: int,
) -> tuple[ItemBasedCF, float]:
    started = perf_counter()
    model = ItemBasedCF.fit(
        snapshot,
        user_count=user_count,
        item_count=item_count,
        neighbor_count=neighbor_count,
        interaction_mode="rating",
        similarity="cosine",
    )
    return model, perf_counter() - started


def _measure_update(
    base: BatchIncrementalItemBasedCFPartialTopK,
    batch: pd.DataFrame,
    *,
    repeats: int,
) -> tuple[float, BatchIncrementalItemBasedCFPartialTopK]:
    samples: list[float] = []
    final_model: BatchIncrementalItemBasedCFPartialTopK | None = None
    for _ in range(repeats):
        model = _copy_model(base)
        started = perf_counter()
        model.update_batch(batch)
        samples.append(perf_counter() - started)
        final_model = model
    assert final_model is not None
    return statistics.median(samples), final_model


def _dense_array_memory_mib(
    *,
    users: int,
    items: int,
    neighbors: int,
    affected_users: int,
    dirty_items: int,
    strategy: str,
) -> float:
    """Estimate peak dense NumPy array bytes; excludes Python/BLAS internals."""
    ui = users * items
    pair = items * items
    interaction = ui * np.dtype(np.float32).itemsize
    observed = ui * np.dtype(np.bool_).itemsize
    gram_or_similarity = pair * np.dtype(np.float32).itemsize
    neighbor_state = items * neighbors * (
        np.dtype(np.int64).itemsize + np.dtype(np.float32).itemsize
    )
    vectors = items * 3 * np.dtype(np.float32).itemsize
    if strategy == "full_rebuild":
        matrix_build_peak = 3 * interaction + observed
        cosine_peak = interaction + observed + 3 * gram_or_similarity + neighbor_state + vectors
        peak = max(matrix_build_peak, cosine_peak)
    else:
        model_state = (
            interaction + observed + 2 * gram_or_similarity + neighbor_state + vectors
        )
        dirty_update = 3 * dirty_items * items * np.dtype(np.float32).itemsize
        if strategy.startswith("batch_gram"):
            gram_temporaries = (
                2 * affected_users * items * np.dtype(np.float32).itemsize
                + 3 * gram_or_similarity
            )
            topk_scratch = items * (16 if strategy.endswith("partial_topk") else 48)
            update_peak = max(gram_temporaries, dirty_update + topk_scratch)
        else:
            update_peak = dirty_update + items * 48
        peak = model_state + update_peak
    return float(peak) / (1024**2)


def _selection_work(
    similarity: np.ndarray,
    rows: np.ndarray,
    *,
    neighbor_count: int,
    partial: bool,
) -> int:
    if partial:
        return int(
            len(rows)
            * (similarity.shape[1] + neighbor_count * np.log2(max(2, neighbor_count)))
        )
    counts = np.count_nonzero(similarity[rows] > 0.0, axis=1)
    return int(sum(int(count) * np.log2(max(2, int(count))) for count in counts))


def _draw_runtime_chart(axis: Any, rows: list[dict[str, Any]]) -> None:
    batch_sizes = [int(row["new_interactions"]) for row in rows]
    for _strategy, label, color, metric in _CHART_SERIES:
        axis.plot(
            batch_sizes,
            [float(row[metric]) for row in rows],
            marker="o",
            linewidth=2,
            color=color,
            label=label,
        )
    axis.set_xscale("log")
    axis.set_yscale("log")
    axis.set_xticks(batch_sizes, [f"{size:,}" for size in batch_sizes])
    axis.set_title("Runtime per update or rebuild")
    axis.set_xlabel("New ratings in batch (log scale)")
    axis.set_ylabel("Median wall-clock seconds (log scale)")
    axis.grid(which="both", alpha=0.25)
    axis.legend()


def _draw_memory_chart(axis: Any, rows: list[dict[str, Any]]) -> None:
    batch_sizes = [int(row["new_interactions"]) for row in rows]
    costs_by_stage = [
        {str(cost["strategy"]): cost for cost in row["computation_costs"]}
        for row in rows
    ]
    for strategy, label, color, _metric in _CHART_SERIES:
        axis.plot(
            batch_sizes,
            [
                float(costs[strategy]["estimated_peak_array_mib"])
                for costs in costs_by_stage
            ],
            linewidth=2,
            color=color,
            label=label,
            marker="o",
            zorder=3,
        )
    axis.set_xscale("log")
    axis.set_xticks(batch_sizes, [f"{size:,}" for size in batch_sizes])
    axis.set_title("Estimated peak NumPy-array memory")
    axis.set_xlabel("New ratings in batch (log scale)")
    axis.set_ylabel("Estimated peak MiB")
    axis.grid(which="both", alpha=0.25)
    axis.legend()


def _write_update_charts(rows: list[dict[str, Any]], report_dir: Path) -> dict[str, str]:
    chart_specs = (
        ("runtime_vs_new_ratings.png", _draw_runtime_chart),
        ("memory_cost_vs_new_ratings.png", _draw_memory_chart),
    )
    outputs: dict[str, str] = {}
    for filename, draw in chart_specs:
        figure, axis = plt.subplots(figsize=(10, 5.5))
        draw(axis, rows)
        figure.tight_layout()
        figure.savefig(report_dir / filename, dpi=180, bbox_inches="tight")
        plt.close(figure)
        outputs[filename.removesuffix(".png")] = filename

    figure, axes = plt.subplots(1, 2, figsize=(16, 6))
    _draw_runtime_chart(axes[0], rows)
    _draw_memory_chart(axes[1], rows)
    figure.suptitle("Incremental Item-Based CF: new-data update cost", fontsize=15)
    figure.tight_layout()
    combined = "runtime_and_memory_comparison.png"
    figure.savefig(report_dir / combined, dpi=180, bbox_inches="tight")
    plt.close(figure)
    outputs["combined"] = combined
    return outputs


def run_benchmark(
    *,
    data_dir: Path,
    report_root: Path,
    dataset_name: str = "ml100k",
    train_fraction: float = 0.8,
    neighbor_count: int = 50,
    repeats: int = 3,
    max_batch_size: int | None = None,
) -> Path:
    dataset_specs = {
        "ml100k": ("MovieLens 100K", load_ml100k),
        "ml1m": ("MovieLens 1M", load_ml1m),
    }
    if dataset_name not in dataset_specs:
        raise ValueError(f"Unsupported MovieLens dataset: {dataset_name}")
    dataset_label, dataset_loader = dataset_specs[dataset_name]
    dataset = dataset_loader(data_dir, strict_counts=True)
    mappings = build_mappings(
        dataset.interactions,
        item_ids=dataset.items["item_id"].astype(str).tolist(),
    )
    encoded = encode_interactions(dataset.interactions, mappings)
    encoded = encoded.sort_values("timestamp", kind="stable").reset_index(drop=True)
    if encoded.duplicated(["user_index", "item_index"]).any():
        raise ValueError(
            "MovieLens contains duplicate user-item pairs; "
            "this run expects unique pairs."
        )

    split_at = int(len(encoded) * train_fraction)
    train = encoded.iloc[:split_at].copy()
    incoming = encoded.iloc[split_at:].copy().reset_index(drop=True)
    initial_partial_started = perf_counter()
    base_partial = BatchIncrementalItemBasedCFPartialTopK.fit(
        train,
        user_count=len(mappings.user_to_index),
        item_count=len(mappings.item_to_index),
        neighbor_count=neighbor_count,
        interaction_mode="rating",
        similarity="cosine",
    )
    initial_partial_seconds = perf_counter() - initial_partial_started
    base_dense, base_dense_seconds = _timed_full_rebuild(
        train,
        user_count=len(mappings.user_to_index),
        item_count=len(mappings.item_to_index),
        neighbor_count=neighbor_count,
    )
    base_similarity_max_abs = float(
        np.max(np.abs(base_partial.similarities - base_dense.similarities))
    )
    base_partial_topk_agreement = float(
        np.mean(base_partial.neighbor_indices == base_dense.neighbor_indices) * 100
    )

    available_batch_size = len(incoming)
    if max_batch_size is not None:
        if max_batch_size < 1:
            raise ValueError("max_batch_size must be positive when provided.")
        available_batch_size = min(available_batch_size, max_batch_size)
    if available_batch_size < 1:
        raise ValueError("The chronological split has no incoming ratings to benchmark.")
    sizes = sorted(
        {
            size
            for size in (1, 10, 100, 1_000, 5_000, 20_000, 100_000, 500_000)
            if size <= available_batch_size
        }
        | {available_batch_size}
    )
    rows: list[dict[str, Any]] = []
    for batch_index, size in enumerate(sizes, start=1):
        print(
            f"[{batch_index}/{len(sizes)}] Measuring batch size {size:,}...",
            flush=True,
        )
        batch = incoming.iloc[:size]
        snapshot = pd.concat([train, batch], ignore_index=True)
        partial_topk_seconds, partial_topk_model = _measure_update(
            base_partial, batch, repeats=repeats
        )
        dense_samples: list[float] = []
        dense_model: ItemBasedCF | None = None
        for _ in range(repeats):
            dense_model, elapsed = _timed_full_rebuild(
                snapshot,
                user_count=len(mappings.user_to_index),
                item_count=len(mappings.item_to_index),
                neighbor_count=neighbor_count,
            )
            dense_samples.append(elapsed)
        assert dense_model is not None

        partial_abs_diff = np.abs(partial_topk_model.similarities - dense_model.similarities)
        partial_max_abs = float(partial_abs_diff.max(initial=0.0))
        neighbor_matches = int(
            np.count_nonzero(
                partial_topk_model.neighbor_indices == dense_model.neighbor_indices
            )
        )
        neighbor_slots = int(dense_model.neighbor_indices.size)
        partial_sets = [
            set(row[row >= 0].tolist()) for row in partial_topk_model.neighbor_indices
        ]
        dense_sets = [set(row[row >= 0].tolist()) for row in dense_model.neighbor_indices]
        overlap_percent = 100.0 * np.mean(
            [
                len(left & right) / max(1, len(right))
                for left, right in zip(partial_sets, dense_sets, strict=True)
            ]
        )
        rounded_partial, _ = select_positive_neighbors(
            np.round(partial_topk_model.similarities, 5), neighbor_count
        )
        rounded_dense, _ = select_positive_neighbors(
            np.round(dense_model.similarities, 5), neighbor_count
        )
        partial_rounded_matches = 100.0 * np.mean(rounded_partial == rounded_dense)
        rebuild_seconds = statistics.median(dense_samples)
        affected_users = np.unique(batch["user_index"].to_numpy(dtype=np.int64))
        dirty_items: set[int] = set()
        for user in affected_users:
            dirty_items.update(np.flatnonzero(base_partial.interactions[user] != 0.0).tolist())
            dirty_items.update(
                np.flatnonzero(partial_topk_model.interactions[user] != 0.0).tolist()
            )
        dirty_rows = np.asarray(sorted(dirty_items), dtype=np.int64)

        item_count = len(mappings.item_to_index)
        user_count = len(mappings.user_to_index)
        all_rows = np.arange(item_count, dtype=np.int64)
        dense_gflops = 2.0 * user_count * item_count**2 / 1e9
        gram_delta_gflops = 4.0 * len(affected_users) * item_count**2 / 1e9
        model_metrics = []
        for name, model, elapsed, strategy, gemm_gflops, partial in [
            (
                "full_rebuild",
                dense_model,
                rebuild_seconds,
                "full_rebuild",
                dense_gflops,
                False,
            ),
            (
                "batch_gram_partial_topk",
                partial_topk_model,
                partial_topk_seconds,
                "batch_gram_partial_topk",
                gram_delta_gflops,
                True,
            ),
        ]:
            refreshed = all_rows if strategy == "full_rebuild" else dirty_rows
            positive_candidates = int(np.count_nonzero(model.similarities[refreshed] > 0.0))
            model_metrics.append(
                {
                    "strategy": name,
                    "new_interactions": size,
                    "affected_users": int(len(affected_users)),
                    "dirty_item_rows_refreshed": int(len(refreshed)),
                    "estimated_dense_gemm_gflops": float(gemm_gflops),
                    "similarity_scalars_recomputed": int(len(refreshed) * item_count),
                    "topk_candidates_scanned": int(len(refreshed) * item_count),
                    "positive_candidates_sorted_or_selected": positive_candidates,
                    "estimated_topk_selection_comparisons": _selection_work(
                        model.similarities,
                        refreshed,
                        neighbor_count=neighbor_count,
                        partial=partial,
                    ),
                    "estimated_peak_array_mib": _dense_array_memory_mib(
                        users=user_count,
                        items=item_count,
                        neighbors=neighbor_count,
                        affected_users=len(affected_users),
                        dirty_items=len(dirty_rows),
                        strategy=strategy,
                    ),
                    "update_or_rebuild_seconds": float(elapsed),
                }
            )
        rows.append(
            {
                "new_interactions": size,
                "cumulative_ratings": len(snapshot),
                "affected_users": int(len(affected_users)),
                "batch_gram_dirty_item_rows": len(dirty_items),
                "full_rebuild_seconds": rebuild_seconds,
                "batch_gram_partial_topk_seconds": partial_topk_seconds,
                "batch_gram_partial_topk_ms_per_interaction": (
                    partial_topk_seconds * 1000 / size
                ),
                "batch_gram_partial_topk_speedup_vs_full_rebuild": rebuild_seconds
                / partial_topk_seconds
                if partial_topk_seconds > 0
                else None,
                "batch_gram_partial_topk_similarity_max_abs_error": partial_max_abs,
                "raw_top_k_position_agreement_percent": (
                    100 * neighbor_matches / neighbor_slots if neighbor_slots else 100.0
                ),
                "average_top_k_set_overlap_percent": float(overlap_percent),
                "partial_topk_agreement_after_similarity_rounding_5dp_percent": float(
                    partial_rounded_matches
                ),
                "touched_item_count": len(
                    np.flatnonzero(
                        np.any(
                            partial_topk_model.interactions != base_partial.interactions,
                            axis=0,
                        )
                    )
                ),
                "computation_costs": model_metrics,
            }
        )

    stamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    report_dir = report_root / f"item_cf_update_comparison_{dataset_name}_{stamp}"
    report_dir.mkdir(parents=True, exist_ok=False)
    charts = _write_update_charts(rows, report_dir)
    payload: dict[str, Any] = {
        "protocol": {
            "models": [
                "ItemBasedCF full rebuild",
                "BatchIncrementalItemBasedCFPartialTopK Gram delta + argpartition",
            ],
            "similarity": "cosine",
            "interaction_mode": "rating",
            "neighbor_count": neighbor_count,
            "dataset": dataset_label,
            "split": "chronological timestamp split",
            "train_fraction": train_fraction,
            "source_ratings": len(encoded),
            "users": len(mappings.user_to_index),
            "items": len(mappings.item_to_index),
            "initial_train_ratings": len(train),
            "new_ratings_available": len(incoming),
            "largest_tested_batch_size": sizes[-1],
            "incoming_ratings_not_benchmarked": len(incoming) - sizes[-1],
            "repeats": repeats,
            "reported_time": "median wall-clock seconds, update/rebuild only",
            "initial_dense_build_seconds": base_dense_seconds,
            "initial_batch_gram_partial_topk_build_seconds": initial_partial_seconds,
            "initial_similarity_max_abs_error": base_similarity_max_abs,
            "initial_partial_topk_exact_position_agreement_percent": base_partial_topk_agreement,
            "memory_metric": (
                "estimated peak dense NumPy array working set MiB; "
                "excludes Python heap and BLAS internals"
            ),
            "charts": charts,
            "operation_metric": (
                "GEMM FLOPs estimate, similarity scalars, Top-K candidate scans "
                "and selection comparison proxy"
            ),
            "platform": platform.platform(),
            "python": platform.python_version(),
            "limitations": [
                "All user and item IDs are mapped from the complete dataset; "
                "this measures warm-start rating arrivals.",
                "MovieLens user-item pairs are unique; update batches therefore "
                "add ratings rather than duplicate events.",
                "This is a local runtime experiment, not a production deployment "
                "or online load test.",
            ],
        },
        "stages": rows,
        "cost_estimates": [
            model_cost
            for stage in rows
            for model_cost in stage["computation_costs"]
        ],
    }
    (report_dir / "benchmark.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    pd.DataFrame(
        [{key: value for key, value in row.items() if key != "computation_costs"} for row in rows]
    ).to_csv(report_dir / "benchmark.csv", index=False)
    pd.DataFrame(payload["cost_estimates"]).to_csv(
        report_dir / "complexity_cost.csv", index=False
    )

    markdown = [
        f"# Incremental Item-Based CF — {dataset_label}",
        "",
        "This experiment compares full Item-Based cosine rebuilds with "
        "Batch Gram + partial Top-K updates.",
        "",
        "## Protocol",
        "",
        (
            f"- Dataset: {len(encoded):,} ratings, "
            f"{len(mappings.user_to_index):,} users, "
            f"{len(mappings.item_to_index):,} movies."
        ),
        (
            f"- Split: chronological; {len(train):,} initial ratings, "
            f"{len(incoming):,} later ratings available."
        ),
        (
            f"- Similarity: cosine; Top-K: {neighbor_count}; "
            f"update/rebuild timings are medians of {repeats} runs."
        ),
        "- Initialization is a full build for both approaches; stage timings below "
        "measure applying new ratings versus rebuilding the current snapshot.",
        "- User and item IDs are mapped from all MovieLens rows so the experiment "
        "isolates warm-start interaction updates; it does not measure cold-start users or movies.",
        (
            "- The incoming partition is tested cumulatively against the same initial "
            f"snapshot at batch sizes from 1 up to {sizes[-1]:,} ratings. "
            f"{len(incoming) - sizes[-1]:,} available incoming ratings are outside this run."
        ),
        "- Memory is an estimated peak NumPy-array footprint, not process RSS; "
        "Python objects and BLAS workspaces are excluded.",
        "",
        "## Runtime and memory charts",
        "",
        "![Runtime by update batch size](runtime_vs_new_ratings.png)",
        "",
        "![Estimated memory by update batch size](memory_cost_vs_new_ratings.png)",
        "",
        "![Runtime and memory comparison](runtime_and_memory_comparison.png)",
        "",
        "## Initial build",
        "",
        f"- Full Item-Based build: {base_dense_seconds:.6f} s.",
        (
            f"- Batch Gram with partial Top-K build: "
            f"{payload['protocol']['initial_batch_gram_partial_topk_build_seconds']:.6f} s."
        ),
        f"- Initial similarity max absolute error: {base_similarity_max_abs:.3g}.",
        "",
        "## New-data update versus full rebuild",
        "",
        "| New ratings | Affected users | Dirty item rows | Full rebuild (s) | "
        "Batch Gram partial-Top-K (s) | Partial speedup vs rebuild |",
        "|---:|---:|---:|---:|---:|---:|",
    ]
    for row in rows:
        markdown.append(
            "| {new_interactions:,} | {affected_users:,} | {batch_gram_dirty_item_rows:,} | "
            "{full_rebuild_seconds:.6f} | {batch_gram_partial_topk_seconds:.6f} | "
            "{batch_gram_partial_topk_speedup_vs_full_rebuild:.2f}× |".format(**row)
        )
    last_stage = rows[-1]
    final_costs = last_stage["computation_costs"]
    rounded_topk_agreement = last_stage[
        "partial_topk_agreement_after_similarity_rounding_5dp_percent"
    ]
    strategy_labels = {
        "full_rebuild": "Full rebuild",
        "batch_gram_partial_topk": "Batch Gram + partial Top-K",
    }
    markdown.extend(
        [
            "",
            "## Computational cost and memory for the largest batch",
            "",
            "| Model | Dense GEMM estimate (GFLOP) | "
            "Similarity values recalculated | Top-K candidates scanned | "
            "Top-K selection work proxy | Estimated peak array MiB |",
            "|---|---:|---:|---:|---:|---:|",
            *[
                "| {label} | {gemm:.3f} | {similarity:,} | "
                "{candidates:,} | {selection:,} | {memory:.2f} |".format(
                    label=strategy_labels[cost["strategy"]],
                    gemm=cost["estimated_dense_gemm_gflops"],
                    similarity=cost["similarity_scalars_recomputed"],
                    candidates=cost["topk_candidates_scanned"],
                    selection=cost["estimated_topk_selection_comparisons"],
                    memory=cost["estimated_peak_array_mib"],
                )
                for cost in final_costs
            ],
            "",
            "MiB is an estimated peak dense NumPy-array working set; it excludes "
            "Python heap objects and BLAS internal buffers. GEMM FLOPs count "
            "multiply/add work for dense matrix products. Top-K selection work is "
            "a comparison proxy, not a hardware instruction count. "
            "`complexity_cost.csv` includes these measures for every batch size.",
            "",
            "## Similarity and Top-K agreement",
            "",
            "- Batch Gram partial-Top-K max similarity error: "
            f"{last_stage['batch_gram_partial_topk_similarity_max_abs_error']:.3g}.",
            (
                "- Top-K agreement between partial-Top-K and full rebuild after "
                "rounding similarities to 5 decimal places: "
                f"{rounded_topk_agreement:.2f}%."
            ),
            "",
            "## Interpretation and limits",
            "",
            "- The initial build still computes all item-item statistics. Incremental "
            "updates trade that repeated work for maintained pair-dot and norm statistics.",
            "- BatchIncrementalItemBasedCFPartialTopK computes old/new Gram contributions "
            "once per affected-user batch, then refreshes neighbor lists with "
            "partial Top-K selection.",
            "- The implementation keeps dense item-item dot and similarity matrices, "
            "so memory remains quadratic in the number of movies. It currently "
            "supports cosine updates for already mapped users/items.",
            "- Exact Top-K lists are refreshed for every item endpoint whose similarity "
            "changed. This is necessary because a changed pair can alter both items' "
            "neighbor lists.",
            "- Incremental dot products and full-rebuild matrix multiplication can differ "
            "by floating-point round-off. Raw Top-K order and set-overlap diagnostics "
            "remain in `benchmark.json`.",
            "- The memory scale is an array-footprint estimate, not measured process RSS; "
            "it omits Python sort objects, allocator fragmentation, and BLAS workspaces.",
            "- New users or movies require expanding mappings and model state; deletions, "
            "production persistence, concurrent update safety, and periodic full-rebuild "
            "reconciliation are outside this experiment.",
            "- Runtime measurements are machine-specific and do not include CSV download, "
            "artifact upload, or deployment time.",
            "",
            "## Files",
            "",
            "- `benchmark.csv`: stage-level timings and correctness comparison.",
            "- `benchmark.json`: protocol, environment, and raw metrics.",
            "- `complexity_cost.csv`: operation-count proxies and estimated peak MiB "
            "per model and batch size.",
            "- `runtime_vs_new_ratings.png`: update and rebuild wall-clock comparison.",
            "- `memory_cost_vs_new_ratings.png`: estimated peak array memory comparison.",
            "- `runtime_and_memory_comparison.png`: combined runtime and memory view.",
            "",
        ]
    )
    (report_dir / "report.md").write_text("\n".join(markdown), encoding="utf-8")
    return report_dir


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Benchmark Incremental Item-Based CF on MovieLens 100K or 1M"
    )
    parser.add_argument("--dataset", choices=("ml100k", "ml1m"), default="ml100k")
    parser.add_argument(
        "--data-dir",
        type=Path,
        default=None,
    )
    parser.add_argument("--report-root", type=Path, default=SERVICE_ROOT / "reports")
    parser.add_argument("--train-fraction", type=float, default=0.8)
    parser.add_argument("--neighbor-count", type=int, default=50)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument(
        "--max-batch-size",
        type=int,
        default=None,
        help="Cap the largest tested batch (default: all ratings in the incoming split).",
    )
    args = parser.parse_args()
    dataset_dir = "ml-100k" if args.dataset == "ml100k" else "ml-1m"
    data_dir = args.data_dir or SERVICE_ROOT / "data" / "raw" / "movielens" / dataset_dir
    output = run_benchmark(
        data_dir=data_dir.resolve(),
        report_root=args.report_root.resolve(),
        dataset_name=args.dataset,
        train_fraction=args.train_fraction,
        neighbor_count=args.neighbor_count,
        repeats=args.repeats,
        max_batch_size=args.max_batch_size,
    )
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
