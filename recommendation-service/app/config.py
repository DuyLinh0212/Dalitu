from __future__ import annotations

from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import AliasChoices, Field, HttpUrl
from pydantic_settings import BaseSettings, SettingsConfigDict

SERVICE_ROOT = Path(__file__).resolve().parents[1]


class Settings(BaseSettings):
    app_name: str = "HuTube Pure Collaborative Filtering Service"
    app_env: str = "development"
    model_storage_path: Path = Path("data/artifacts")
    model_version: str = "benchmark-latest"
    model_type: Literal[
        "user_based",
        "item_based",
        "user_based_cosine",
        "user_based_jaccard",
        "user_based_pearson",
        "item_based_cosine",
        "item_based_jaccard",
        "item_based_pearson",
    ] = "item_based"
    recommender_service_token: str = ""
    recommender_admin_token: str = ""
    upstash_redis_rest_url: str = ""
    upstash_redis_rest_token: str = ""
    hutube_api_base_url: HttpUrl = "http://127.0.0.1:5080/api/v1"
    cors_allowed_origins: list[str] = [
        "http://localhost:4200", "http://127.0.0.1:4200",
        "https://hutube-beta.vercel.app",
    ]
    r2_account_id: str = Field(default="", validation_alias=AliasChoices(
        "r2_account_id", "Storage__R2__AccountId"))
    r2_access_key_id: str = Field(default="", validation_alias=AliasChoices(
        "r2_access_key_id", "Storage__R2__AccessKeyId"))
    r2_secret_access_key: str = Field(default="", validation_alias=AliasChoices(
        "r2_secret_access_key", "Storage__R2__SecretAccessKey"))
    r2_bucket_name: str = Field(default="", validation_alias=AliasChoices(
        "r2_bucket_name", "Storage__R2__BucketName"))
    r2_endpoint_url: str = ""
    allow_benchmark_model: bool = False

    model_config = SettingsConfigDict(
        env_file=SERVICE_ROOT / ".env",
        env_file_encoding="utf-8",
        case_sensitive=False,
        extra="ignore",
    )

    @property
    def missing_r2_settings(self) -> list[str]:
        return [name for name, value in (
            ("R2_ACCOUNT_ID", self.r2_account_id),
            ("R2_ACCESS_KEY_ID", self.r2_access_key_id),
            ("R2_SECRET_ACCESS_KEY", self.r2_secret_access_key),
            ("R2_BUCKET_NAME", self.r2_bucket_name),
        ) if not value.strip()]

    @property
    def resolved_model_storage_path(self) -> Path:
        path = self.model_storage_path
        return path if path.is_absolute() else (SERVICE_ROOT / path).resolve()


@lru_cache
def get_settings() -> Settings:
    return Settings()
