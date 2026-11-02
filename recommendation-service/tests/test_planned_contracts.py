"""Additional boundary/negative evidence for B-PYAPI and B-PYCF plans.

No live R2 credentials are inherited. Live storage remains a separate scenario.
"""
from __future__ import annotations

import hashlib
import io
import time
import zipfile

import numpy as np
import pandas as pd
import pytest
from test_api import _client

from app.model_registry import load_artifact_directory
from app.recommenders.item_cf import ItemBasedCF
from app.recommenders.similarity import compute_similarity
from training.cohorts import temporal_train_test_split
from training.quality_gate import validate_metrics
from training.train_hutube import normalize_score_aggregation, train_csv_bytes


def test_B_PYAPI_01_04_health_and_authorized_model_status(tmp_path):
    with _client(tmp_path) as client:
        client.app.state.settings.recommender_admin_token = "admin-secret"
        assert client.get("/health").status_code == 200
        assert client.get("/ready").status_code == 200
        response = client.get("/internal/model", headers={"X-Model-Admin-Token": "admin-secret"})
        assert response.status_code == 200
        assert "admin-secret" not in response.text and "test-token" not in response.text
        client.app.state.registry.loaded = None
        assert client.get("/health").status_code == 200
        assert client.get("/ready").status_code == 503


@pytest.mark.parametrize("payload", [{}, {"csvKey": "x", "csvSha256": "bad"},
                                     {"csvKey": None, "csvSha256": "0" * 64}])
def test_B_PYAPI_05_invalid_training_schema_cannot_start_job(tmp_path, payload):
    with _client(tmp_path) as client:
        client.app.state.settings.recommender_admin_token = "admin-secret"
        response = client.post("/internal/model/train", json=payload,
                               headers={"X-Model-Admin-Token": "admin-secret"})
    assert response.status_code == 422


@pytest.mark.parametrize("job", ["missing", "..%2F..%2Fprivate", "%2e%2e%5cprivate"])
def test_B_PYAPI_06_unknown_and_traversal_job_ids_cannot_expose_files(tmp_path, job):
    marker = "private-artifact-marker"
    (tmp_path / "private").write_text(marker)
    with _client(tmp_path) as client:
        client.app.state.settings.recommender_admin_token = "admin-secret"
        response = client.get(f"/internal/model/jobs/{job}",
                              headers={"X-Model-Admin-Token": "admin-secret"})
    assert response.status_code == 404
    assert marker not in response.text and str(tmp_path) not in response.text


def _train(raw, path):
    return train_csv_bytes(raw, path, csv_key="collaborative_cf/planned.csv",
                           csv_sha256=hashlib.sha256(raw).hexdigest())


@pytest.mark.parametrize("limit,status", [(0, 422), (1, 200), (100, 200), (101, 422)])
def test_B_PYAPI_03_PYCF_04_http_limit_boundaries(tmp_path, limit, status):
    with _client(tmp_path) as client:
        response = client.post("/internal/recommendations",
                               headers={"X-Service-Token": "test-token"},
                               json={"userId": "u1", "limit": limit, "excludeItemIds": []})
    assert response.status_code == status
    if status == 200:
        body = response.json()
        ids = [item["itemId"] for item in body["items"]]
        assert len(ids) <= limit and len(ids) == len(set(ids))
        assert not {"i1", "i2"}.intersection(ids)
        assert body["modelVersion"] == "test-v1"


@pytest.mark.parametrize("token", [None, "wrong-token"])
def test_B_PYAPI_03_PYCF_10_invalid_service_credentials(tmp_path, token):
    with _client(tmp_path) as client:
        response = client.post("/internal/recommendations",
                               headers={} if token is None else {"X-Service-Token": token},
                               json={"userId": "u1", "limit": 1})
    assert response.status_code == 401
    assert response.json()["detail"]["code"] == "INVALID_SERVICE_TOKEN"
    assert "wrong-token" not in response.text


@pytest.mark.parametrize("route", ["/internal/model", "/internal/model/jobs/missing"])
@pytest.mark.parametrize("token", [None, "wrong-admin"])
def test_B_PYAPI_04_06_admin_credentials_do_not_leak(tmp_path, route, token):
    with _client(tmp_path) as client:
        client.app.state.settings.recommender_admin_token = "admin-secret"
        headers = {} if token is None else {"X-Model-Admin-Token": token}
        response = client.get(route, headers=headers)
    assert response.status_code == 401
    assert response.json()["detail"]["code"] == "INVALID_ADMIN_TOKEN"
    assert "admin-secret" not in response.text
    assert "wrong-admin" not in response.text


@pytest.mark.parametrize("raw", [
    b"user_id,video_id,score\n", b"user_id,video_id,score\nu1,v1,1\n",
    b"user_id,video_id,score\nu1,v1,1\nu1,v1,0.5\nu2,v2,1\n",
    b"user_id,video_id,score\n,v1,1\nu1,v2,0.5\nu2,v1,1\n",
    b"user_id,score\nu1,1\nu2,0.5\n",
])
def test_B_PYCF_01_invalid_matrix_never_publishes(tmp_path, raw):
    with pytest.raises(ValueError):
        _train(raw, tmp_path)
    assert not list(tmp_path.rglob("metadata.json"))


@pytest.mark.parametrize("score", ["-0.1", "1.1", "NaN", "Inf", "-Inf", "1e309"])
def test_B_PYCF_02_nonfinite_or_out_of_range_scores_are_rejected(tmp_path, score):
    raw = f"user_id,video_id,score\nu1,v1,{score}\nu1,v2,0.8\nu2,v1,1\n".encode()
    with pytest.raises(ValueError):
        _train(raw, tmp_path)
    assert not list(tmp_path.rglob("metadata.json"))


@pytest.mark.parametrize("weights", [{"rating": -1}, {"rating": float("nan")},
                                     {"rating": float("inf")}, {"rating": 0}])
def test_B_PYCF_02_invalid_weights_or_zero_denominator_are_rejected(weights):
    with pytest.raises(ValueError):
        normalize_score_aggregation({"mode": "weighted", "weights": weights})


@pytest.mark.parametrize("metric", ["cosine", "jaccard", "pearson"])
def test_B_PYCF_03_known_identical_orthogonal_and_zero_vectors(metric):
    vectors = np.array([[1, 2, 3, 0, 0, 0], [1, 2, 3, 0, 0, 0],
                        [0, 0, 0, 1, 2, 3], [0, 0, 0, 0, 0, 0]], dtype=np.float32)
    actual = compute_similarity(vectors, metric)
    assert np.isfinite(actual).all()
    np.testing.assert_allclose(actual, actual.T, atol=1e-6)
    np.testing.assert_allclose(np.diag(actual), 0)
    assert actual[0, 1] == pytest.approx(1, abs=1e-6)
    assert actual[0, 2] == pytest.approx(0, abs=1e-6)
    np.testing.assert_allclose(actual[3], 0)


@pytest.mark.parametrize("neighbors", [0, 1, 100])
def test_B_PYCF_04_05_neighbor_boundaries_and_cold_user(synthetic_interactions, neighbors):
    if neighbors == 0:
        with pytest.raises(ValueError):
            ItemBasedCF.fit(synthetic_interactions, user_count=5, item_count=7, neighbor_count=0)
        return
    model = ItemBasedCF.fit(synthetic_interactions, user_count=5, item_count=7,
                            neighbor_count=neighbors)
    cold = model.recommend(4, limit=100)
    assert len(cold) <= 7
    assert all(0 <= item < 7 and np.isfinite(score) for item, score in cold)
    assert len({item for item, _ in cold}) == len(cold)
    with pytest.raises(ValueError, match="limit must be positive"):
        model.recommend(4, limit=0)
    with pytest.raises((ValueError, IndexError)):
        model.recommend(99, limit=1)


def test_B_PYCF_06_latest_canary_cannot_leak_into_training(synthetic_interactions):
    data = synthetic_interactions.copy()
    train, test = temporal_train_test_split(data, test_fraction=0.25)
    train_pairs = set(zip(train.user_id, train.item_id, strict=True))
    test_pairs = set(zip(test.user_id, test.item_id, strict=True))
    assert train_pairs.isdisjoint(test_pairs)
    assert len(train) + len(test) == len(data)
    for user, group in data.groupby("user_id"):
        canary = group.sort_values("timestamp").iloc[-1]
        assert (user, canary.item_id) in test_pairs
        assert (train.loc[train.user_id == user, "timestamp"].max()
                <= test.loc[test.user_id == user, "timestamp"].min())


def test_B_PYCF_07_repeat_training_preserves_ranking_and_configuration(tmp_path):
    raw = b"user_id,video_id,score\nu1,v1,1\nu1,v2,0.8\nu2,v1,0.8\nu2,v3,1\n"
    first = load_artifact_directory(_train(raw, tmp_path / "a"), model_type="item_based")
    second = load_artifact_directory(_train(raw, tmp_path / "b"), model_type="item_based")
    np.testing.assert_allclose(first.model.similarities, second.model.similarities)
    assert first.model.recommend(0, limit=3) == second.model.recommend(0, limit=3)
    assert first.metadata["csvSha256"] == second.metadata["csvSha256"]
    assert first.metadata["modelAlgorithm"] == second.metadata["modelAlgorithm"]
    assert first.metadata["scoreAggregation"] == second.metadata["scoreAggregation"]


@pytest.mark.parametrize("duration,valid", [(-0.001, False), (0.0, True), (0.001, True),
                                          (float("nan"), False), (float("inf"), False)])
def test_B_PYCF_08_runtime_gate_boundaries(duration, valid):
    errors = validate_metrics({"runtime": {"models": {"item_based": {"fit_seconds": duration}}}})
    assert (not errors) == valid


@pytest.mark.parametrize("damage", ["missing", "corrupt", "partial"])
def test_B_PYCF_09_broken_artifact_cannot_be_loaded(tmp_path, damage):
    raw = b"user_id,video_id,score\nu1,v1,1\nu1,v2,0.8\nu2,v1,0.8\nu2,v3,1\n"
    artifact = _train(raw, tmp_path)
    model = artifact / "item_based_cosine.npz"
    if damage == "missing":
        for candidate in artifact.glob("item_based*.npz"):
            candidate.unlink()
    elif damage == "corrupt":
        model.write_bytes(b"not numpy")
    else:
        model.write_bytes(model.read_bytes()[:20])
    with pytest.raises((ValueError, OSError, KeyError, EOFError, zipfile.BadZipFile)):
        load_artifact_directory(artifact, model_type="item_based")


def test_B_PYCF_12_unicode_quote_newline_csv_and_metadata_roundtrip(tmp_path):
    frame = pd.DataFrame([{"user_id": "người,\"một\"", "video_id": "video\nx", "score": 1},
                          {"user_id": "người,\"một\"", "video_id": "video hai", "score": .8},
                          {"user_id": "người hai", "video_id": "video\nx", "score": .6},
                          {"user_id": "người hai", "video_id": "video hai", "score": 1}])
    raw = frame.to_csv(index=False).encode("utf-8")
    pd.testing.assert_frame_equal(frame, pd.read_csv(io.BytesIO(raw)))
    artifact = _train(raw, tmp_path)
    loaded = load_artifact_directory(artifact, model_type="item_based")
    assert set(loaded.mappings.item_to_index) == set(frame.video_id)
    assert set(loaded.mappings.user_to_index) == set(frame.user_id)
    assert loaded.metadata["csvSha256"] == hashlib.sha256(raw).hexdigest()
    assert loaded.metadata["interactionsCount"] == 4


@pytest.mark.parametrize("rows", [1000, 10000, 100000])
def test_B_PYCF_13_record_size_time_and_model_budget(tmp_path, rows, record_property):
    # Sparse observations in a bounded catalog; the current model stores dense arrays.
    users, items = 1000, 500
    indices = np.arange(rows)
    frame = pd.DataFrame({"user_id": [f"u{x % users}" for x in indices],
                          "video_id": [f"v{(x // users + (x % users) * 17) % items}"
                                       for x in indices],
                          "score": 0.5 + (indices % 2) * 0.5})
    assert len(frame.drop_duplicates(["user_id", "video_id"])) == rows
    started = time.perf_counter()
    artifact = _train(frame.to_csv(index=False).encode(), tmp_path)
    loaded = load_artifact_directory(artifact, model_type="item_based")
    elapsed = time.perf_counter() - started
    size = sum(p.stat().st_size for p in artifact.iterdir() if p.is_file())
    record_property("rows", rows)
    record_property("observation_density", rows/(users*items))
    record_property("elapsed_seconds", round(elapsed, 4))
    record_property("artifact_bytes", size)
    model_bytes = sum(getattr(loaded.model, name).nbytes for name in
                      ("interactions", "similarities", "neighbor_indices", "neighbor_similarities"))
    record_property("model_array_bytes", model_bytes)
    if __import__("sys").platform == "win32":
        import ctypes
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD)] + [
                (name, ctypes.c_size_t) for name in ("peak_working", "working", "peak_paged",
                "paged", "peak_nonpaged", "nonpaged", "pagefile", "peak_pagefile")]
        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        kernel = ctypes.windll.kernel32
        kernel.GetCurrentProcess.restype = wintypes.HANDLE
        get_memory = ctypes.windll.psapi.GetProcessMemoryInfo
        get_memory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
        assert get_memory(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb)
        record_property("pytest_process_peak_working_set_bytes", counters.peak_working)
    assert model_bytes < 160 * 1024 * 1024
    assert loaded.metadata["interactionsCount"] == rows
    assert size < 160 * 1024 * 1024
    assert np.isfinite(loaded.model.similarities).all()
    assert elapsed < 120
