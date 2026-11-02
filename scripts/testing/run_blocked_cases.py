"""Execute the eight formerly blocked scenarios against isolated live services.

Remote object cleanup is restricted to the recorded, newly owned prefix. No
production settings, active model, existing object or Redis key is overwritten.
"""
from __future__ import annotations

import argparse
import json
import os
import secrets
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import httpx
from blocked_environment import ROOT, WORK, configuration, discover, r2_client

OUT = ROOT / "docs/testing/runs/2026-10-23-blocked"


class DelayedHandler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", 0)))
        time.sleep(8)
        try:
            self.send_response(503)
            self.end_headers()
            self.wfile.write(b'{"error":"controlled test outage"}')
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass

    do_GET = do_POST


def main(args):
    global OUT
    if args.output_dir:
        OUT = ROOT / args.output_dir
        OUT.mkdir(parents=True, exist_ok=True)
    manifest = discover()
    assert manifest["redis"]["available"] and manifest["r2"]["available"]
    config = configuration()
    env = os.environ.copy()
    prefix = manifest["prefix"]
    assert prefix.startswith("collaborative_cf/test-") and prefix.endswith("/")
    env.update({
        "LIVE_PLANNED": "1", "LIVE_WORK": str(WORK), "LIVE_OUT": str(OUT),
        "LIVE_R2_ACCOUNT": config["Storage__R2__AccountId"],
        "LIVE_R2_ACCESS": config["Storage__R2__AccessKeyId"],
        "LIVE_R2_SECRET": config["Storage__R2__SecretAccessKey"],
        "LIVE_R2_BUCKET": manifest["bucket"], "LIVE_R2_PREFIX": prefix,
        "LIVE_REDIS_URL": config["UPSTASH_REDIS_REST_URL"],
        "LIVE_REDIS_TOKEN": config["UPSTASH_REDIS_REST_TOKEN"],
        "LIVE_REDIS_PREFIX": manifest["runId"] + ":cache:",
        "LIVE_CF_TOKEN": secrets.token_hex(24), "LIVE_CF_ADMIN": secrets.token_hex(24),
        "TEST_DATABASE_CONNECTION": env.get("TEST_DATABASE_CONNECTION") or
            "Host=127.0.0.1;Port=55441;Username=hutube_test;Database=postgres"
    })
    env.update({"R2_ACCOUNT_ID": env["LIVE_R2_ACCOUNT"], "R2_ACCESS_KEY_ID": env["LIVE_R2_ACCESS"],
        "R2_SECRET_ACCESS_KEY": env["LIVE_R2_SECRET"], "R2_BUCKET_NAME": env["LIVE_R2_BUCKET"],
        "RECOMMENDER_SERVICE_TOKEN": env["LIVE_CF_TOKEN"], "RECOMMENDER_ADMIN_TOKEN": env["LIVE_CF_ADMIN"],
        "MODEL_STORAGE_PATH": str(WORK / "cf-models"), "APP_ENV": "test", "ALLOW_BENCHMARK_MODEL": "false"})
    started = datetime.now(timezone.utc).isoformat()
    fault = ThreadingHTTPServer(("127.0.0.1", 55354), DelayedHandler)
    threading.Thread(target=fault.serve_forever, daemon=True).start()
    log = (OUT / "cf-live.log").open("w", encoding="utf-8")
    process = subprocess.Popen([sys.executable, str(ROOT / "scripts/testing/live_cf_bootstrap.py")],
        cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
    result = None
    cleanup = []
    try:
        for _ in range(100):
            try:
                if httpx.get("http://127.0.0.1:55352/health", timeout=2).status_code == 200:
                    break
            except httpx.HTTPError:
                pass
            if process.poll() is not None:
                raise RuntimeError("CF exited; inspect cf-live.log")
            time.sleep(.3)
        else:
            raise RuntimeError("CF listener did not start")
        command = ["dotnet", "test", "backend/tests/HuTube.IntegrationTests/HuTube.IntegrationTests.csproj",
            "--configuration", "Release", "--no-restore", "--filter", args.filter,
            "--logger", "trx;LogFileName=live-current.trx", "--results-directory", str(OUT),
            "--verbosity", "minimal"]
        result = subprocess.run(command, cwd=ROOT, env=env, check=False)
    finally:
        process.terminate()
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
        log.close()
        fault.shutdown()
        client = r2_client(config)
        keys = [item["Key"] for page in client.get_paginator("list_objects_v2").paginate(
            Bucket=manifest["bucket"], Prefix=prefix) for item in page.get("Contents", [])]
        for key in keys:
            assert key.startswith(prefix)
            client.delete_object(Bucket=manifest["bucket"], Key=key)
            cleanup.append(key)
        assert not client.list_objects_v2(Bucket=manifest["bucket"], Prefix=prefix).get("Contents")
        (OUT / "live-run.json").write_text(json.dumps({"runId": manifest["runId"],
            "startedAt": started, "endedAt": datetime.now(timezone.utc).isoformat(),
            "exitCode": result.returncode if result else None, "filter": args.filter,
            "isolation": manifest["r2"]["isolation"], "deletedR2Keys": cleanup,
            "cleanup": "CF and fault listener stopped; owned R2 prefix verified empty; API fixture drops its isolated database and Redis keys",
            "scopeChange": "User moved actual Google authentication to web E2E; backend tests begin at verified identity"}, indent=2), encoding="utf-8")
    return result.returncode


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--filter", default="FullyQualifiedName~LivePlannedTests")
    parser.add_argument("--output-dir", default="")
    raise SystemExit(main(parser.parse_args()))
