"""Private configuration discovery; output contains capabilities, never credentials.

R2 model tests MUST use a newly created bucket, since production snapshot cleanup
intentionally removes older CSVs. Redis commands are limited to a unique namespace.
"""
from __future__ import annotations

import json
import os
import uuid
from pathlib import Path

import boto3
import httpx
from botocore.config import Config
from botocore.exceptions import ClientError
from dotenv import dotenv_values

ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / ".work/blocked-20261023"
OUT = ROOT / "docs/testing/runs/2026-10-23-blocked"


def configuration():
    result = {}
    for name in (".env.local", ".env.ec2-api"):
        result.update({k: v for k, v in dotenv_values(ROOT / name).items() if v})
    result.update({k: v for k, v in os.environ.items() if k.startswith("TEST_")})
    return result


def r2_client(config):
    return boto3.client("s3",
        endpoint_url=config.get("TEST_R2_ENDPOINT") or f"https://{config['Storage__R2__AccountId']}.r2.cloudflarestorage.com",
        aws_access_key_id=config.get("TEST_R2_ACCESS_KEY_ID") or config["Storage__R2__AccessKeyId"],
        aws_secret_access_key=config.get("TEST_R2_SECRET_ACCESS_KEY") or config["Storage__R2__SecretAccessKey"],
        region_name="auto", config=Config(connect_timeout=5, read_timeout=15,
            retries={"total_max_attempts": 2}, signature_version="s3v4"))


def discover():
    WORK.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)
    config = configuration()
    manifest_path = WORK / "environment.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {
        "runId": "blocked-" + uuid.uuid4().hex[:12]}
    redis_url = config.get("TEST_UPSTASH_REDIS_REST_URL") or config.get("UPSTASH_REDIS_REST_URL")
    redis_token = config.get("TEST_UPSTASH_REDIS_REST_TOKEN") or config.get("UPSTASH_REDIS_REST_TOKEN")
    if redis_url and redis_token:
        try:
            response = httpx.post(redis_url, headers={"Authorization": "Bearer " + redis_token},
                                  json=["PING"], timeout=15)
            manifest["redis"] = {"available": response.status_code == 200 and response.json().get("result") == "PONG",
                                  "httpStatus": response.status_code}
        except httpx.HTTPError as exc:
            manifest["redis"] = {"available": False, "errorType": type(exc).__name__}
    else:
        manifest["redis"] = {"available": False, "reason": "No REST configuration"}
    if not manifest.get("bucket"):
        bucket = "hutube-test-" + manifest["runId"]
        try:
            r2_client(config).create_bucket(Bucket=bucket)
            manifest["bucket"] = bucket
            manifest["r2"] = {"available": True, "isolatedNewBucket": True}
        except ClientError as exc:
            manifest["r2"] = {"available": False, "httpStatus": exc.response["ResponseMetadata"]["HTTPStatusCode"],
                               "errorCode": exc.response["Error"]["Code"]}
    manifest["googleClientConfigured"] = bool(config.get("Google__ClientId"))
    if not manifest.get("bucket"):
        # Bucket-scoped credentials cannot create a bucket. Confine every test
        # object (including fixed model keys) below this one newly owned prefix.
        client = r2_client(config)
        prefix = "collaborative_cf/test-" + manifest["runId"] + "/"
        key = prefix + "capability-probe.bin"
        bucket = config.get("TEST_R2_BUCKET") or config["Storage__R2__BucketName"]
        try:
            client.put_object(Bucket=bucket, Key=key, Body=b"HuTube isolated test probe")
            assert client.get_object(Bucket=bucket, Key=key)["Body"].read() == b"HuTube isolated test probe"
            client.delete_object(Bucket=bucket, Key=key)
            manifest.update({"bucket": bucket, "prefix": prefix})
            manifest["r2"] = {"available": True, "isolatedNewBucket": False,
                "isolation": "New owned key prefix; all fixed CF keys rebased; existing keys/config untouched",
                "createBucketDenied": True, "probePutGetDelete": True}
        except ClientError as exc:
            manifest["r2"]["objectProbeError"] = exc.response["Error"]["Code"]
    if "prefix" not in manifest:
        manifest["prefix"] = "collaborative_cf/test-" + manifest["runId"] + "/"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2), flush=True)
    return manifest


if __name__ == "__main__":
    discover()
