from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import pandas as pd

EXPECTED_RATINGS = 100_000
EXPECTED_USERS = 943
EXPECTED_ITEMS = 1_682
EXPECTED_GENRES = 19
REQUIRED_FILES = ("u.data", "u.item", "u.genre", "u.info")
EXPECTED_ML1M_RATINGS = 1_000_209
EXPECTED_ML1M_USERS = 6_040
EXPECTED_ML1M_ITEMS = 3_883
ML1M_GENRES = (
    "Action",
    "Adventure",
    "Animation",
    "Children's",
    "Comedy",
    "Crime",
    "Documentary",
    "Drama",
    "Fantasy",
    "Film-Noir",
    "Horror",
    "Musical",
    "Mystery",
    "Romance",
    "Sci-Fi",
    "Thriller",
    "War",
    "Western",
)
ML1M_REQUIRED_FILES = ("ratings.dat", "movies.dat")
BEHAVIOR_COLUMNS = ("like", "dislike", "comment", "share", "watch_ratio")


@dataclass(frozen=True, slots=True)
class MovieLensSummary:
    ratings: int
    users: int
    items: int
    genres: int

    def as_dict(self) -> dict[str, int]:
        return {
            "ratings": self.ratings,
            "users": self.users,
            "items": self.items,
            "genres": self.genres,
        }


@dataclass(slots=True)
class MovieLensDataset:
    interactions: pd.DataFrame
    items: pd.DataFrame
    genre_names: list[str]
    source_summary: MovieLensSummary
    active_summary: MovieLensSummary


def _require_files(data_dir: Path) -> None:
    missing = [name for name in REQUIRED_FILES if not (data_dir / name).is_file()]
    if missing:
        raise FileNotFoundError(
            f"MovieLens 100K is incomplete at {data_dir}. Missing: {', '.join(missing)}"
        )


def _read_genres(data_dir: Path) -> list[str]:
    rows: list[tuple[int, str]] = []
    for line in (data_dir / "u.genre").read_text(encoding="latin-1").splitlines():
        if not line.strip():
            continue
        name, index = line.rsplit("|", 1)
        rows.append((int(index), name))
    return [name for _index, name in sorted(rows)]


def _read_items(data_dir: Path, genres: list[str]) -> pd.DataFrame:
    columns = [
        "item_id",
        "title",
        "release_date",
        "video_release_date",
        "imdb_url",
        *genres,
    ]
    items = pd.read_csv(
        data_dir / "u.item",
        sep="|",
        names=columns,
        encoding="latin-1",
        engine="python",
    )
    items["item_id"] = items["item_id"].astype(str)
    for genre in genres:
        items[genre] = items[genre].astype("int8")
    return items


def validate_ml100k(data_dir: str | Path, strict_counts: bool = True) -> MovieLensSummary:
    path = Path(data_dir).expanduser().resolve()
    _require_files(path)
    ratings = pd.read_csv(
        path / "u.data",
        sep="\t",
        names=["user_id", "item_id", "rating", "timestamp_epoch"],
        usecols=[0, 1, 2, 3],
    )
    genres = _read_genres(path)
    items = _read_items(path, genres)
    summary = MovieLensSummary(
        ratings=len(ratings),
        users=int(ratings["user_id"].nunique()),
        items=int(items["item_id"].nunique()),
        genres=len(genres),
    )
    if strict_counts:
        expected = MovieLensSummary(
            ratings=EXPECTED_RATINGS,
            users=EXPECTED_USERS,
            items=EXPECTED_ITEMS,
            genres=EXPECTED_GENRES,
        )
        if summary != expected:
            raise ValueError(
                f"Unexpected MovieLens 100K counts. Expected {expected.as_dict()}, "
                f"got {summary.as_dict()}."
            )
    return summary


def load_ml100k(
    data_dir: str | Path,
    *,
    strict_counts: bool = True,
    max_users: int | None = None,
) -> MovieLensDataset:
    path = Path(data_dir).expanduser().resolve()
    source_summary = validate_ml100k(path, strict_counts=strict_counts)
    genres = _read_genres(path)
    items = _read_items(path, genres)
    interactions = pd.read_csv(
        path / "u.data",
        sep="\t",
        names=["user_id", "item_id", "rating", "timestamp_epoch"],
        dtype={
            "user_id": "int64",
            "item_id": "int64",
            "rating": "float32",
            "timestamp_epoch": "int64",
        },
    )
    if max_users is not None:
        if max_users < 1:
            raise ValueError("max_users must be positive when provided.")
        selected = sorted(interactions["user_id"].unique().tolist())[:max_users]
        interactions = interactions[interactions["user_id"].isin(selected)].copy()

    interactions["user_id"] = interactions["user_id"].astype(str)
    interactions["item_id"] = interactions["item_id"].astype(str)
    interactions["timestamp"] = pd.to_datetime(
        interactions.pop("timestamp_epoch"),
        unit="s",
        utc=True,
    )
    interactions["source"] = "MOVIELENS"
    for column in BEHAVIOR_COLUMNS:
        interactions[column] = None
    interactions = interactions[
        [
            "user_id",
            "item_id",
            "rating",
            "like",
            "dislike",
            "comment",
            "share",
            "watch_ratio",
            "source",
            "timestamp",
        ]
    ].sort_values(["user_id", "timestamp", "item_id"], kind="stable")
    interactions.reset_index(drop=True, inplace=True)

    active_item_ids = set(interactions["item_id"])
    items = items[items["item_id"].isin(active_item_ids)].copy()
    active_summary = MovieLensSummary(
        ratings=len(interactions),
        users=int(interactions["user_id"].nunique()),
        items=int(items["item_id"].nunique()),
        genres=len(genres),
    )
    return MovieLensDataset(
        interactions=interactions,
        items=items,
        genre_names=genres,
        source_summary=source_summary,
        active_summary=active_summary,
    )


def _require_ml1m_files(data_dir: Path) -> None:
    missing = [name for name in ML1M_REQUIRED_FILES if not (data_dir / name).is_file()]
    if missing:
        raise FileNotFoundError(
            f"MovieLens 1M is incomplete at {data_dir}. Missing: {', '.join(missing)}"
        )


def _read_ml1m_ratings(data_dir: Path) -> pd.DataFrame:
    return pd.read_csv(
        data_dir / "ratings.dat",
        sep=r"::",
        engine="python",
        names=["user_id", "item_id", "rating", "timestamp_epoch"],
        dtype={
            "user_id": "int64",
            "item_id": "int64",
            "rating": "float32",
            "timestamp_epoch": "int64",
        },
    )


def _read_ml1m_items(data_dir: Path) -> pd.DataFrame:
    items = pd.read_csv(
        data_dir / "movies.dat",
        sep=r"::",
        engine="python",
        names=["item_id", "title", "genres"],
        encoding="latin-1",
        dtype={"item_id": "int64", "title": "str", "genres": "str"},
    )
    known_genres = set(ML1M_GENRES)
    genre_sets = items["genres"].map(
        lambda value: set() if value == "(no genres listed)" else set(value.split("|"))
    )
    unknown_genres = sorted(set().union(*genre_sets.tolist()) - known_genres)
    if unknown_genres:
        raise ValueError(f"MovieLens 1M contains unknown genres: {', '.join(unknown_genres)}")

    items["item_id"] = items["item_id"].astype(str)
    for genre in ML1M_GENRES:
        items[genre] = genre_sets.map(
            lambda values, genre_name=genre: int(genre_name in values)
        ).astype("int8")
    return items[["item_id", "title", "genres", *ML1M_GENRES]]


def validate_ml1m(data_dir: str | Path, strict_counts: bool = True) -> MovieLensSummary:
    path = Path(data_dir).expanduser().resolve()
    _require_ml1m_files(path)
    ratings = _read_ml1m_ratings(path)
    items = _read_ml1m_items(path)
    summary = MovieLensSummary(
        ratings=len(ratings),
        users=int(ratings["user_id"].nunique()),
        items=int(items["item_id"].nunique()),
        genres=len(ML1M_GENRES),
    )
    if strict_counts:
        expected = MovieLensSummary(
            ratings=EXPECTED_ML1M_RATINGS,
            users=EXPECTED_ML1M_USERS,
            items=EXPECTED_ML1M_ITEMS,
            genres=len(ML1M_GENRES),
        )
        if summary != expected:
            raise ValueError(
                f"Unexpected MovieLens 1M counts. Expected {expected.as_dict()}, "
                f"got {summary.as_dict()}."
            )
    return summary


def load_ml1m(data_dir: str | Path, *, strict_counts: bool = True) -> MovieLensDataset:
    path = Path(data_dir).expanduser().resolve()
    source_summary = validate_ml1m(path, strict_counts=strict_counts)
    interactions = _read_ml1m_ratings(path)
    items = _read_ml1m_items(path)

    interactions["user_id"] = interactions["user_id"].astype(str)
    interactions["item_id"] = interactions["item_id"].astype(str)
    interactions["timestamp"] = pd.to_datetime(
        interactions.pop("timestamp_epoch"),
        unit="s",
        utc=True,
    )
    interactions["source"] = "MOVIELENS"
    for column in BEHAVIOR_COLUMNS:
        interactions[column] = None
    interactions = interactions[
        [
            "user_id",
            "item_id",
            "rating",
            "like",
            "dislike",
            "comment",
            "share",
            "watch_ratio",
            "source",
            "timestamp",
        ]
    ].sort_values(["user_id", "timestamp", "item_id"], kind="stable")
    interactions.reset_index(drop=True, inplace=True)

    active_item_ids = set(interactions["item_id"])
    items = items[items["item_id"].isin(active_item_ids)].copy()
    active_summary = MovieLensSummary(
        ratings=len(interactions),
        users=int(interactions["user_id"].nunique()),
        items=int(items["item_id"].nunique()),
        genres=len(ML1M_GENRES),
    )
    return MovieLensDataset(
        interactions=interactions,
        items=items,
        genre_names=list(ML1M_GENRES),
        source_summary=source_summary,
        active_summary=active_summary,
    )


def export_unified_csv(interactions: pd.DataFrame, path: str | Path) -> Path:
    output = Path(path)
    output.parent.mkdir(parents=True, exist_ok=True)
    interactions.to_csv(output, index=False)
    return output
