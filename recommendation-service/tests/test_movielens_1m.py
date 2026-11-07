from __future__ import annotations

from pathlib import Path

import pytest

from app.data.movielens import load_ml1m, validate_ml1m


def test_load_ml1m_parses_double_colon_files_and_normalizes_interactions(
    tmp_path: Path,
) -> None:
    data_dir = tmp_path / "ml-1m"
    data_dir.mkdir()
    (data_dir / "ratings.dat").write_text(
        "1::10::5::946684800\n1::20::3::946684810\n2::10::4::946684820\n",
        encoding="ascii",
    )
    (data_dir / "movies.dat").write_text(
        "10::Action Film (2000)::Action|Drama\n"
        "20::Comedy Film (2001)::Comedy\n"
        "30::Unrated Film (2002)::Drama\n",
        encoding="latin-1",
    )

    dataset = load_ml1m(data_dir, strict_counts=False)

    assert dataset.source_summary.as_dict() == {
        "ratings": 3,
        "users": 2,
        "items": 3,
        "genres": 18,
    }
    assert dataset.active_summary.as_dict() == {
        "ratings": 3,
        "users": 2,
        "items": 2,
        "genres": 18,
    }
    assert dataset.interactions["source"].eq("MOVIELENS").all()
    assert dataset.interactions["rating"].tolist() == [5.0, 3.0, 4.0]
    assert dataset.interactions["user_id"].tolist() == ["1", "1", "2"]
    assert dataset.items["item_id"].tolist() == ["10", "20"]
    assert dataset.items.loc[dataset.items["item_id"] == "10", "Action"].item() == 1
    assert dataset.items.loc[dataset.items["item_id"] == "20", "Comedy"].item() == 1


@pytest.mark.dataset
def test_local_movielens_1m_has_official_counts(project_root: Path) -> None:
    data_dir = project_root / "data/raw/movielens/ml-1m"
    if not data_dir.exists():
        pytest.skip("Local MovieLens 1M dataset is intentionally absent from CI.")

    assert validate_ml1m(data_dir).as_dict() == {
        "ratings": 1_000_209,
        "users": 6_040,
        "items": 3_883,
        "genres": 18,
    }
