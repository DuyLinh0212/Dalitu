from __future__ import annotations

import json

import httpx

from app.ranking_cache import UpstashRankingCache


def test_ranking_ids_round_trip_through_upstash_with_ttl_and_hashed_scope() -> None:
    commands: list[list[str]] = []
    values: dict[str, str] = {}
    authorization_headers: list[str | None] = []

    def handle(request: httpx.Request) -> httpx.Response:
        authorization_headers.append(request.headers.get("Authorization"))
        command = json.loads(request.content)
        commands.append(command)
        if command[0] == "GET":
            result = values.get(command[1])
        else:
            assert command[0] == "SET"
            values[command[1]] = command[2]
            result = "OK"
        return httpx.Response(200, json={"result": result})

    client = httpx.Client(transport=httpx.MockTransport(handle))
    cache = UpstashRankingCache(
        "https://redis.example.test", "test-token", "Production", client=client
    )
    exclusions = ["video-b", "video-a"]

    assert cache.get("model-1", "user-123", exclusions) is None
    cache.set("model-1", "user-123", exclusions, ["video-1", "video-2"])
    assert cache.get("model-1", "user-123", ["video-a", "video-b"]) == ["video-1", "video-2"]

    key = commands[0][1]
    assert key.startswith("dalitu:production:cache:recommendation:rank:v1:")
    assert "user-123" not in key
    assert commands == [
        ["GET", key],
        ["SET", key, '["video-1","video-2"]', "EX", "30"],
        ["GET", key],
    ]
    assert authorization_headers == ["Bearer test-token"] * 3
    cache.close()
    client.close()


def test_missing_upstash_configuration_disables_cache_without_process_fallback() -> None:
    calls: list[httpx.Request] = []
    client = httpx.Client(transport=httpx.MockTransport(
        lambda request: calls.append(request) or httpx.Response(500)
    ))
    cache = UpstashRankingCache("", "", "test", client=client)

    assert cache.get("model-1", "user-123", []) is None
    cache.set("model-1", "user-123", [], ["video-1"])
    assert calls == []
    cache.close()
    client.close()


def test_redis_failure_is_a_cache_miss() -> None:
    client = httpx.Client(transport=httpx.MockTransport(
        lambda request: httpx.Response(503)
    ))
    cache = UpstashRankingCache(
        "https://redis.example.test", "test-token", "test", client=client
    )

    assert cache.get("model-1", "user-123", []) is None
    cache.set("model-1", "user-123", [], ["video-1"])
    cache.close()
    client.close()
