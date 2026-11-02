"""Run the unmodified CF app with real R2 transport in a test-only key namespace.

Only S3 request parameters are rebased; there is no fake storage or fake training.
This also isolates the app's fixed active manifest, lease, jobs and catalog keys.
"""
import os
import sys
from pathlib import Path

import boto3
import uvicorn

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "recommendation-service"))
original_client = boto3.client
prefix = os.environ["LIVE_R2_PREFIX"]
assert prefix.startswith("collaborative_cf/test-") and prefix.endswith("/")


def namespaced_client(*args, **kwargs):
    client = original_client(*args, **kwargs)
    if args and args[0] == "s3":
        def rebase(params, **_):
            if "Key" in params:
                params["Key"] = prefix + params["Key"]
            if "Prefix" in params:
                params["Prefix"] = prefix + params["Prefix"]
        client.meta.events.register("before-parameter-build.s3", rebase)
        def unbase(parsed, **_):
            for item in parsed.get("Contents", []):
                assert item["Key"].startswith(prefix)
                item["Key"] = item["Key"][len(prefix):]
        client.meta.events.register("after-call.s3.ListObjectsV2", unbase)
    return client


boto3.client = namespaced_client
from app.config import Settings
from app.main import create_app

settings = Settings(_env_file=None)
app = create_app(settings)
uvicorn.run(app, host="127.0.0.1", port=55352, log_level="warning")
