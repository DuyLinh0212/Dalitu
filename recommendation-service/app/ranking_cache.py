from __future__ import annotations

import hashlib
import json
import logging

import httpx

logger = logging.getLogger("uvicorn.error")


class UpstashRankingCache:
    """Small distributed cache for ranked video IDs; no process-local copy is kept."""

    ttl_seconds = 30

    def __init__(
        self,
        endpoint: str,
        token: str,
        environment: str,
        client: httpx.Client | None = None,
    ) -> None:
        self._endpoint = endpoint.strip()
        self._token = token.strip()
        self._environment = environment.strip().lower() or "development"
        self._configured = self._endpoint.startswith("https://") and bool(self._token)
        self._owns_client = client is None
        self._client = client or (httpx.Client(timeout=1.0) if self._configured else None)
        self._prefix = f"dalitu:{self._environment}:cache:recommendation:rank:v1:"

    @staticmethod
    def _digest(model_version: str, user_id: str, exclude_item_ids: list[str]) -> str:
        dimensions = json.dumps(
            {
                "modelVersion": model_version,
                "userId": user_id,
                "excludeItemIds": sorted(exclude_item_ids),
            },
            ensure_ascii=True,
            separators=(",", ":"),
            sort_keys=True,
        )
        return hashlib.sha256(dimensions.encode("utf-8")).hexdigest()

    def _key(self, model_version: str, user_id: str, exclude_item_ids: list[str]) -> str:
        return self._prefix + self._digest(model_version, user_id, exclude_item_ids)

    def get(
        self, model_version: str, user_id: str, exclude_item_ids: list[str]
    ) -> list[str] | None:
        if not self._configured or self._client is None:
            return None
        try:
            response = self._client.post(
                self._endpoint,
                json=["GET", self._key(model_version, user_id, exclude_item_ids)],
                headers={"Authorization": f"Bearer {self._token}"},
            )
            response.raise_for_status()
            value = response.json().get("result")
            if not isinstance(value, str):
                return None
            items = json.loads(value)
            if isinstance(items, list) and all(isinstance(item, str) for item in items):
                return items
            return None
        except (httpx.HTTPError, ValueError):
            logger.warning("Upstash ranking cache read failed; ranking will be recomputed.")
            return None

    def set(
        self,
        model_version: str,
        user_id: str,
        exclude_item_ids: list[str],
        item_ids: list[str],
    ) -> None:
        if not self._configured or self._client is None:
            return
        try:
            response = self._client.post(
                self._endpoint,
                json=[
                    "SET",
                    self._key(model_version, user_id, exclude_item_ids),
                    json.dumps(item_ids, ensure_ascii=True, separators=(",", ":")),
                    "EX",
                    str(self.ttl_seconds),
                ],
                headers={"Authorization": f"Bearer {self._token}"},
            )
            response.raise_for_status()
        except (httpx.HTTPError, ValueError):
            logger.warning("Upstash ranking cache write failed; serving computed ranking.")

    def close(self) -> None:
        if self._owns_client and self._client is not None:
            self._client.close()
