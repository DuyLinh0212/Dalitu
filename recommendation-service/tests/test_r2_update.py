from __future__ import annotations

import hashlib
import json
import time

import pytest
from botocore.exceptions import ClientError
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from app.model_registry import ModelRegistry
from app.r2_store import R2Store


class FakeR2(R2Store):
    def __init__(self) -> None:
        self.objects: dict[str, bytes] = {}

    def read(self, key: str) -> bytes:
        return self.objects[key]

    def read_optional(self, key: str) -> bytes | None:
        return self.objects.get(key)

    def write(self, key: str, content: bytes, content_type: str) -> None:
        self.objects[key] = content

    def read_json(self, key: str) -> dict | None:
        raw = self.read_optional(key)
        return json.loads(raw) if raw is not None else None

    def write_json(self, key: str, value: dict) -> None:
        self.write(key, json.dumps(value).encode(), "application/json")

    def list_keys(self, prefix: str) -> list[str]:
        return sorted(key for key in self.objects
                      if key.startswith(prefix) and key.endswith(".json"))

    def read_versioned_json(self, key: str):
        raw = self.read_optional(key)
        return (
            (json.loads(raw), hashlib.sha256(raw).hexdigest())
            if raw is not None
            else (None, None)
        )

    def compare_write_json(self, key: str, value: dict, etag: str | None) -> str:
        _, current = self.read_versioned_json(key)
        if current != etag:
            raise ClientError({"Error": {"Code": "PreconditionFailed"}}, "PutObject")
        self.write_json(key, value)
        return self.read_versioned_json(key)[1]


def test_r2_training_restart_and_failed_update_keep_active_model(tmp_path, monkeypatch) -> None:
    store = FakeR2()
    monkeypatch.setattr("app.model_registry.R2Store", lambda _: store)
    monkeypatch.setattr("app.model_statistics.R2Store", lambda _: store)
    settings = Settings(model_storage_path=tmp_path, app_env="production",
                        recommender_service_token="infer-secret",
                        recommender_admin_token="admin-secret",
                        r2_account_id="test", r2_access_key_id="test",
                        r2_secret_access_key="test", r2_bucket_name="test")
    csv_key = "collaborative_cf/2026/09/26/interactions_test.csv"
    csv = (b"user_id,video_id,score\n"
           b"u1,v1,1\nu1,v2,0.8\nu2,v1,0.8\nu2,v3,1\nu3,v2,1\nu3,v3,0.8\n")
    store.write(csv_key, csv, "text/csv")
    digest = hashlib.sha256(csv).hexdigest()
    registry = ModelRegistry(settings)
    requested_job_id = "97d80366-6dfb-436e-aeb9-046ca50a1d36"
    with TestClient(create_app(settings, registry)) as client:
        assert client.post(
            "/internal/model/train", json={"csvKey": csv_key, "csvSha256": digest}
        ).status_code == 401
        for token in ("infer-secret", "stale-admin-secret"):
            rejected = client.post(
                "/internal/model/train",
                headers={"X-Model-Admin-Token": token},
                json={"csvKey": csv_key, "csvSha256": digest},
            )
            assert rejected.status_code == 401
            assert rejected.json()["detail"]["code"] == "INVALID_ADMIN_TOKEN"
        assert "collaborative_cf/active.json" not in store.objects
        started = client.post(
            "/internal/model/train",
            headers={"X-Model-Admin-Token": "admin-secret"},
            json={"jobId": requested_job_id, "csvKey": csv_key, "csvSha256": digest},
        )
        assert started.status_code == 202
        job_id = started.json()["jobId"]
        assert job_id == requested_job_id
        for _ in range(100):
            progress = client.get(f"/internal/model/jobs/{job_id}",
                                  headers={"X-Model-Admin-Token": "admin-secret"}).json()
            if progress["status"] not in ("running", "publishing"):
                break
            time.sleep(.05)
        assert progress["status"] == "completed", progress
        # The job record is persisted just before the background coroutine exits.
        # Wait for that final cleanup so the next update is not rejected as busy.
        assert registry.update_task is not None
        for _ in range(100):
            if registry.update_task.done():
                break
            time.sleep(.01)
        assert registry.update_task.done()
        replay = client.post(
            "/internal/model/train",
            headers={"X-Model-Admin-Token": "admin-secret"},
            json={"jobId": job_id, "csvKey": csv_key, "csvSha256": digest},
        )
        assert replay.status_code == 200
        assert replay.json()["status"] == "completed"
        conflict = client.post(
            "/internal/model/train",
            headers={"X-Model-Admin-Token": "admin-secret"},
            json={"jobId": job_id, "csvKey": csv_key + "other", "csvSha256": digest},
        )
        assert conflict.status_code == 409
        manifest = store.read_json("collaborative_cf/active.json")
        assert manifest is not None
        assert manifest["csvKey"] == csv_key
        assert manifest["csvSha256"] == digest
        assert manifest["artifactKey"] in store.objects
        response = client.post(
            "/internal/recommendations",
            headers={"X-Service-Token": "infer-secret"},
            json={"userId": "u1", "limit": 10, "excludeItemIds": []},
        )
        assert response.status_code == 200
        assert [item["itemId"] for item in response.json()["items"]] == ["v3"]

        invalid_key = "collaborative_cf/2026/09/26/invalid.csv"
        invalid = b"user_id,video_id,score\n"
        store.write(invalid_key, invalid, "text/csv")
        bad = client.post(
            "/internal/model/train",
            headers={"X-Model-Admin-Token": "admin-secret"},
            json={"csvKey": invalid_key, "csvSha256": hashlib.sha256(invalid).hexdigest()},
        )
        assert bad.status_code == 202
        for _ in range(100):
            failed = client.get(f"/internal/model/jobs/{bad.json()['jobId']}",
                                headers={"X-Model-Admin-Token": "admin-secret"}).json()
            if failed["status"] != "running":
                break
            time.sleep(.05)
        assert failed["status"] == "failed"
        assert store.read_json("collaborative_cf/active.json") == manifest
        assert registry.loaded is not None
        assert registry.loaded.metadata["modelVersion"] == manifest["modelVersion"]

    restarted = ModelRegistry(settings)
    restarted.load()
    assert restarted.loaded is not None
    assert restarted.loaded.metadata["modelVersion"] == manifest["modelVersion"]
    assert restarted.get_job(job_id)["status"] == "completed"


def test_training_lease_excludes_replicas_and_fences_old_owner():
    store = FakeR2()
    first = store.acquire_training_lease()
    with pytest.raises(RuntimeError):
        store.acquire_training_lease()
    store.release_training_lease(*first)
    second = store.acquire_training_lease()
    with pytest.raises(ClientError):
        store.renew_training_lease(*first)
    store.release_training_lease(*second)


def test_job_creation_excludes_other_replica_and_recovers_default_aggregation(
    tmp_path, monkeypatch
):
    store = FakeR2()
    monkeypatch.setattr("app.model_registry.R2Store", lambda _: store)
    settings = Settings(model_storage_path=tmp_path)
    first, second = ModelRegistry(settings), ModelRegistry(settings)
    job_id = "97d80366-6dfb-436e-aeb9-046ca50a1d37"
    job = {"jobId": job_id, "status": "running", "csvKey": "collaborative_cf/input.csv",
           "csvSha256": "a" * 64, "scoreAggregation": {"mode": "average", "weights": {}}}
    assert first.create_job(job_id, job)
    assert not second.create_job(job_id, {**job, "csvKey": "other.csv"})
    assert second.get_job(job_id) == job
    from training.train_hutube import normalize_score_aggregation
    active = {"csvKey": job["csvKey"], "csvSha256": job["csvSha256"],
              "modelVersion": "published-before-restart",
              "scoreAggregation": normalize_score_aggregation(None)}
    store.write_json("collaborative_cf/active.json", active)
    restarted = ModelRegistry(settings)
    assert restarted.get_job(job_id)["status"] == "completed"
    assert restarted.get_job(job_id)["manifest"] == active
