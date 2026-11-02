"""Explicit sandbox checks. Never inherits production application credentials."""
from __future__ import annotations

import json
import os
import uuid
from datetime import datetime, timezone
from pathlib import Path

import boto3
import httpx
from botocore.exceptions import BotoCoreError, ClientError

OUT = Path(__file__).resolve().parents[2] / "docs/testing/runs/2026-10-23-planned"


def main():
    results = []
    names = ["TEST_R2_ENDPOINT", "TEST_R2_ACCESS_KEY_ID", "TEST_R2_SECRET_ACCESS_KEY", "TEST_R2_BUCKET"]
    missing = [key for key in names if not os.environ.get(key)]
    now = datetime.now(timezone.utc).isoformat()
    if missing:
        results.append({"id": "B-MEDIA.03", "status": "Blocked", "endedAt": now,
                        "actual": "Chưa cấu hình sandbox R2 riêng: " + ", ".join(missing)})
        results.append({"id": "B-PYCF.11", "status": "Blocked", "endedAt": now,
                        "actual": "Thiếu bucket R2 sandbox để train/update thật. FakeR2 unit tests không thay thế bằng chứng mạng thật."})
    else:
        endpoint, access, secret, bucket = [os.environ[key] for key in names]
        key = "test-planned/" + uuid.uuid4().hex + "/payload.bin"
        content = os.urandom(4096)
        client = boto3.client("s3", endpoint_url=endpoint, aws_access_key_id=access,
                              aws_secret_access_key=secret, region_name="auto")
        try:
            client.put_object(Bucket=bucket, Key=key, Body=content, ContentType="application/octet-stream")
            assert client.get_object(Bucket=bucket, Key=key)["Body"].read() == content
            signed = client.generate_presigned_url("get_object", Params={"Bucket": bucket, "Key": key}, ExpiresIn=1)
            assert httpx.get(signed, timeout=15).content == content
            import time
            time.sleep(3)
            assert httpx.get(signed, timeout=15).status_code == 403
            results.append({"id": "B-MEDIA.03", "status": "Pass", "endedAt": datetime.now(timezone.utc).isoformat(),
                            "actual": "R2 sandbox upload/read/signed URL/expiry kiểm tra bytes thật; cleanup chỉ key của run."})
        except (AssertionError, BotoCoreError, ClientError, httpx.HTTPError) as exc:
            results.append({"id": "B-MEDIA.03", "status": "Fail", "endedAt": datetime.now(timezone.utc).isoformat(),
                            "actual": "R2 sandbox request failed: " + type(exc).__name__})
        finally:
            client.delete_object(Bucket=bucket, Key=key)
        results.append({"id": "B-PYCF.11", "status": "Blocked", "endedAt": now,
                        "actual": "Bucket transport checked; model train/update cần chạy registry với active manifest riêng trong bucket test."})
    google = [key for key in ["TEST_GOOGLE_CLIENT_ID", "TEST_GOOGLE_ID_TOKEN"] if not os.environ.get(key)]
    results.append({"id": "B-MEDIA.04", "status": "Blocked", "endedAt": now,
                    "actual": "Pickup .eml được kiểm tra riêng; chưa có Google OAuth sandbox cho expired/cancel/link/retry. Thiếu: " + ", ".join(google)})
    results.append({"id": "B-MEDIA.05", "status": "Blocked", "endedAt": now,
                    "actual": "Webhook HMAC qua HTTP và CF lifecycle có test doubles; chưa có CF service + R2 sandbox thật xuyên train/update/feed."})
    redis = [key for key in ["TEST_UPSTASH_REDIS_REST_URL", "TEST_UPSTASH_REDIS_REST_TOKEN"] if not os.environ.get(key)]
    results.append({"id": "B-CACHE.14", "status": "Blocked", "endedAt": now,
                    "actual": "Hai HybridCache providers chia sẻ transport đã kiểm tra; thiếu REST Redis sandbox cho hai Kestrel instances: " + ", ".join(redis)})
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "sandboxes.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"cases": len(results), "statuses": [r["status"] for r in results]}))


if __name__ == "__main__":
    main()
