from __future__ import annotations

from contextlib import contextmanager
from threading import Event

import httpx
import pandas as pd
import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.data.mapping import build_mappings, build_seen_csr, encode_interactions
from app.main import create_app
from app.model_registry import LoadedModel, ModelRegistry
from app.recommenders.item_cf import ItemBasedCF


def _client(tmp_path) -> TestClient:
    interactions = pd.DataFrame(
        [
            {"user_id": "u1", "item_id": "i1", "rating": 5.0},
            {"user_id": "u1", "item_id": "i2", "rating": 4.0},
            {"user_id": "u2", "item_id": "i2", "rating": 5.0},
            {"user_id": "u2", "item_id": "i3", "rating": 4.0},
        ]
    )
    mappings = build_mappings(interactions, item_ids=["i1", "i2", "i3", "i4"])
    encoded = encode_interactions(interactions, mappings)
    model = ItemBasedCF.fit(
        encoded,
        user_count=len(mappings.user_to_index),
        item_count=len(mappings.item_to_index),
        neighbor_count=2,
        interaction_mode="rating",
    )
    indptr, indices = build_seen_csr(encoded, mappings)
    settings = Settings(
        recommender_service_token="test-token",
        allow_benchmark_model=True,
        model_storage_path=tmp_path,
        model_type="item_based",
        r2_account_id="", r2_access_key_id="", r2_secret_access_key="", r2_bucket_name="",
    )
    registry = ModelRegistry(settings)
    registry.loaded = LoadedModel(
        model=model,
        model_type="item_based",
        mappings=mappings,
        index_to_item=mappings.index_to_item,
        seen_indptr=indptr,
        seen_indices=indices,
        metadata={
            "modelVersion": "test-v1",
            "source": "MOVIELENS",
            "deployable": False,
        },
        item_metadata={},
        artifact_dir=tmp_path,
    )
    return TestClient(create_app(settings, registry))


@contextmanager
def _direct_feed_client(tmp_path, handler):
    with _client(tmp_path) as client:
        content_client = httpx.AsyncClient(transport=httpx.MockTransport(handler))
        client.app.state.content_client = content_client
        try:
            yield client
        finally:
            client.portal.call(content_client.aclose)


def test_direct_feed_authenticates_live_session_and_resolves_ranked_ids(tmp_path) -> None:
    import json

    calls = []

    def content_api(request):
        calls.append(request.url.path)
        assert request.headers["Authorization"] == "Bearer user-access-token"
        # The internal token is added only to the service-to-service data request.
        assert request.headers["X-Service-Token"] == "test-token"
        assert "X-Recommendation-Client-IP" in request.headers
        if request.url.path.endswith("/recommendation-context"):
            return httpx.Response(200, json={
                "userId": "u1", "enabled": True, "excludeItemIds": [],
            })
        assert request.url.path.endswith("/resolve")
        body = json.loads(request.content)
        assert body["page"] == 2 and body["pageSize"] == 5
        assert "i1" not in body["videoIds"] and "i2" not in body["videoIds"]
        assert "i3" in body["videoIds"]
        return httpx.Response(200, json={"items": [{"videoId": "i3"}],
                                         "page": 2, "pageSize": 5, "total": 8})

    with _direct_feed_client(tmp_path, content_api) as client:
        # A caller cannot override the identity that was validated by the content API.
        response = client.get("/api/v1/feed/recommended?page=2&pageSize=5&userId=u2",
                              headers={"Authorization": "Bearer user-access-token"})
    assert response.status_code == 200
    assert response.headers["Cache-Control"] == "no-store"
    assert response.json()["items"] == [{"videoId": "i3"}]
    assert calls == ["/api/v1/feed/recommendation-context", "/api/v1/feed/resolve"]


@pytest.mark.parametrize("fallback", ["not_ready", "unknown_user", "disabled", "watched"])
def test_direct_feed_resolves_fallback_without_reentering_internal_inference(tmp_path, fallback):
    import json

    def content_api(request):
        if request.url.path.endswith("/recommendation-context"):
            return httpx.Response(200, json={
                "userId": "missing" if fallback == "unknown_user" else "u1",
                "enabled": fallback != "disabled",
                "excludeItemIds": ["i3", "i4"] if fallback == "watched" else [],
            })
        assert request.url.path.endswith("/resolve")
        assert json.loads(request.content)["videoIds"] == []
        return httpx.Response(200, json={"items": [], "page": 1, "pageSize": 20, "total": 0})

    with _direct_feed_client(tmp_path, content_api) as client:
        if fallback == "not_ready":
            client.app.state.registry.loaded = None
        response = client.get("/api/v1/feed/recommended",
                              headers={"Authorization": "Bearer user-token"})
    assert response.status_code == 200


def test_direct_feed_rejects_anonymous_and_revoked_sessions_before_inference(tmp_path):
    calls = []

    def content_api(request):
        calls.append(request.url.path)
        return httpx.Response(401, json={"code": "SESSION_EXPIRED", "message": "Expired"})

    with _direct_feed_client(tmp_path, content_api) as client:
        anonymous = client.get("/api/v1/feed/recommended",
                               headers={"X-Service-Token": "test-token"})
        assert anonymous.status_code == 401 and calls == []
        revoked = client.get("/api/v1/feed/recommended",
                             headers={"Authorization": "Bearer revoked-token"})
    assert revoked.status_code == 401
    assert revoked.json()["code"] == "SESSION_EXPIRED"
    assert calls == ["/api/v1/feed/recommendation-context"]


@pytest.mark.parametrize("origin", [
    "http://localhost:4200", "https://hutube-beta.vercel.app",
])
def test_direct_feed_cors_allows_web_bearer_without_credentials_or_internal_tokens(
    tmp_path, origin,
):
    with _client(tmp_path) as client:
        headers = {"Origin": origin, "Access-Control-Request-Method": "GET",
                   "Access-Control-Request-Headers": "Authorization,X-HuTube-Client"}
        allowed = client.options("/api/v1/feed/recommended", headers=headers)
        denied = client.options("/api/v1/feed/recommended",
                                headers={**headers, "Origin": "https://other.example"})
        internal = client.options("/internal/recommendations", headers={
            **headers, "Access-Control-Request-Method": "POST",
            "Access-Control-Request-Headers": "X-Service-Token",
        })
    assert allowed.status_code == 200
    assert allowed.headers["Access-Control-Allow-Origin"] == origin
    assert "Access-Control-Allow-Credentials" not in allowed.headers
    assert denied.status_code == 400 and internal.status_code == 400


def test_direct_feed_returns_content_service_failure_and_validates_page_limits(tmp_path):
    def content_api(request):
        raise httpx.ConnectError("Offline", request=request)

    with _direct_feed_client(tmp_path, content_api) as client:
        unavailable = client.get("/api/v1/feed/recommended",
                                 headers={"Authorization": "Bearer user-token"})
        invalid = client.get("/api/v1/feed/recommended?pageSize=101",
                             headers={"Authorization": "Bearer user-token"})
    assert unavailable.status_code == 503
    assert unavailable.json()["code"] == "CONTENT_API_UNAVAILABLE"
    assert invalid.status_code == 422


def test_internal_api_requires_token_and_excludes_seen(tmp_path) -> None:
    with _client(tmp_path) as client:
        unauthorized = client.post(
            "/internal/recommendations",
            json={"userId": "u1", "limit": 3, "excludeItemIds": []},
        )
        response = client.post(
            "/internal/recommendations",
            headers={"X-Service-Token": "test-token"},
            json={"userId": "u1", "limit": 3, "excludeItemIds": ["i3"]},
        )

    assert unauthorized.status_code == 401
    assert response.status_code == 200
    assert response.json()["source"] == "MOVIELENS"
    item_ids = {item["itemId"] for item in response.json()["items"]}
    assert "i1" not in item_ids
    assert "i2" not in item_ids
    assert "i3" not in item_ids


def test_unknown_user_returns_404(tmp_path) -> None:
    with _client(tmp_path) as client:
        response = client.post(
            "/internal/recommendations",
            headers={"X-Service-Token": "test-token"},
            json={"userId": "missing", "limit": 3, "excludeItemIds": []},
        )

    assert response.status_code == 404
    assert response.json()["detail"]["code"] == "USER_NOT_IN_MODEL"


def test_invalid_limit_returns_422(tmp_path) -> None:
    with _client(tmp_path) as client:
        response = client.post(
            "/internal/recommendations",
            headers={"X-Service-Token": "test-token"},
            json={"userId": "u1", "limit": 101, "excludeItemIds": []},
        )

    assert response.status_code == 422


def test_model_admin_routes_require_separate_token(tmp_path) -> None:
    with _client(tmp_path) as client:
        status = client.get("/internal/model", headers={"X-Service-Token": "test-token"})
        train = client.post("/internal/model/train", headers={"X-Service-Token": "test-token"},
                            json={"csvKey": "collaborative_cf/test.csv", "csvSha256": "0" * 64})
        old_reload = client.post("/internal/reload")

    assert status.status_code == 401
    assert train.status_code == 401
    assert old_reload.status_code == 404


def test_no_model_returns_503(tmp_path) -> None:
    settings = Settings(
        recommender_service_token="test-token",
        allow_benchmark_model=True,
        model_storage_path=tmp_path,
    )
    registry = ModelRegistry(settings)
    registry.load_error = "No artifact"
    with TestClient(create_app(settings, registry)) as client:
        response = client.post(
            "/internal/recommendations",
            headers={"X-Service-Token": "test-token"},
            json={"userId": "u1", "limit": 3, "excludeItemIds": []},
        )

    assert response.status_code == 503
    assert response.json()["detail"]["code"] == "MODEL_NOT_READY"


def test_health_serves_while_initial_model_is_loading(tmp_path, monkeypatch) -> None:
    client = _client(tmp_path)
    registry = client.app.state.registry
    loaded_model = registry.loaded
    registry.loaded = None
    started, release, finished = Event(), Event(), Event()

    def slow_load():
        started.set()
        release.wait(timeout=3)
        registry.loaded = loaded_model
        finished.set()

    monkeypatch.setattr(registry, "load", slow_load)
    try:
        with client:
            assert started.wait(timeout=1)
            assert not finished.is_set(), "HTTP startup waited for the model download"
            assert client.get("/health").status_code == 200
            pending = client.get("/ready")
            assert pending.status_code == 503
            assert pending.json()["detail"]["retryable"] is True
            assert pending.json()["detail"]["message"] == "Recommendation model is loading."
            inference = client.post(
                "/internal/recommendations",
                headers={"X-Service-Token": "test-token"},
                json={"userId": "u1", "limit": 3, "excludeItemIds": []},
            )
            assert inference.status_code == 503
            assert inference.json()["detail"]["retryable"] is True
            release.set()
            assert finished.wait(timeout=1)
            assert client.get("/ready").status_code == 200
    finally:
        release.set()


def test_ready_distinguishes_missing_token_from_loading_model(tmp_path) -> None:
    client = _client(tmp_path)
    client.app.state.settings.recommender_service_token = ""
    with client:
        response = client.get("/ready")
    assert response.status_code == 503
    assert response.json()["detail"]["retryable"] is False
    assert response.json()["detail"]["message"] == "RECOMMENDER_SERVICE_TOKEN is not configured."


@pytest.mark.parametrize(
    "loaded,configured", [(False, False), (False, True), (True, False), (True, True)]
)
def test_B_PYAPI_02_readiness_retryability_matches_recoverable_state(tmp_path, loaded, configured):
    client = _client(tmp_path)
    client.app.state.registry.refresh = lambda: None
    if not loaded:
        client.app.state.registry.loaded = None
        client.app.state.registry.load_error = "Recommendation model is loading."
    client.app.state.settings.recommender_service_token = "test-token" if configured else ""
    with client:
        response = client.get("/ready")
    if loaded and configured:
        assert response.status_code == 200
        assert response.json()["status"] == "ready"
    else:
        assert response.status_code == 503
        assert response.json()["detail"]["code"] == "SERVICE_NOT_READY"
        assert response.json()["detail"]["retryable"] is configured
    assert "test-token" not in response.text
