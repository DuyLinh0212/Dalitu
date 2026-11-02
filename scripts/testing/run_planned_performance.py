"""Local Kestrel/PostgreSQL load evidence. Never points at an existing application DB.

Run with recommendation-service/.venv/Scripts/python.exe. Each VU reuses seeded
accounts. The temporary database and the spawned API process are cleaned in finally.
External Redis/R2/OAuth and model publication need explicit sandbox configuration.
"""
from __future__ import annotations

import argparse
import asyncio
import collections
import email
import email.policy
import hashlib
import json
import os
import re
import secrets
import ssl
import subprocess
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

import httpx
import numpy as np
import sniffio

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs/testing/runs/2026-10-23-planned"
WORK = ROOT / ".work/planned-20261023/performance"
BASE = "http://127.0.0.1:55141"
PASSWORD = "PlannedLocalOnly42!"
PSQL = Path(os.environ.get("PLANNED_PSQL", "D:/PostgreSQL/18/bin/psql.exe"))
LOAD_TLS_CONTEXT = ssl.create_default_context()


def sql(database, query):
    result = subprocess.run([str(PSQL), "-h", "127.0.0.1", "-p", "55441", "-U", "hutube_test",
                             "-d", database, "-X", "-v", "ON_ERROR_STOP=1", "-At"],
                            input=query, capture_output=True, text=True, encoding="utf-8", timeout=60, check=False)
    if result.returncode:
        raise RuntimeError(result.stderr[-2000:])
    return result.stdout.strip()


def process_memory(process_id):
    import ctypes
    from ctypes import wintypes
    class Counters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in ("peak", "working", "peak_paged", "paged", "peak_nonpaged", "nonpaged", "pagefile", "peak_pagefile", "private")]
    kernel = ctypes.windll.kernel32
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x410, False, process_id)
    assert handle, "Could not inspect the owned Kestrel process"
    try:
        counters = Counters(); counters.cb = ctypes.sizeof(counters)
        read = ctypes.windll.psapi.GetProcessMemoryInfo
        read.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
        assert read(handle, ctypes.byref(counters), counters.cb)
        return {"workingSetBytes": counters.working, "privateBytes": counters.private}
    finally: kernel.CloseHandle(handle)


async def stage(client, users, routes, vus, duration, *, write=False, login=False, resource_pid=None, database=None):
    # Each virtual user owns its connection, like independent browser sessions.
    # A shared httpcore pool scans all 100 connections on each acquisition/release;
    # on Windows that saturated the generator while server GET p95 stayed ~20 ms.
    vu_clients = [httpx.AsyncClient(base_url=client.base_url, timeout=client.timeout,
        trust_env=False, verify=LOAD_TLS_CONTEXT,
        limits=httpx.Limits(max_connections=1, max_keepalive_connections=1)) for _ in range(vus)]
    start = time.perf_counter()
    cpu_start = time.process_time()
    deadlines = start + duration
    latency, statuses, response_bytes = [], collections.Counter(), 0
    per_route = collections.defaultdict(list)
    resources = []

    async def worker(index):
        nonlocal response_bytes
        vu_client = vu_clients[index]
        user = users[index % len(users)]
        sequence = 0
        while time.perf_counter() < deadlines:
            route = routes[(index + sequence) % len(routes)]
            before = time.perf_counter()
            try:
                if login:
                    response = await vu_client.post("/api/v1/auth/login", json={"email": user["email"],
                        "password": PASSWORD, "platform": "mobile", "deviceId": user["device"]})
                    if response.status_code == 200:
                        user["headers"] = {"Authorization": "Bearer " + response.json()["accessToken"]}
                elif write:
                    response = await vu_client.post(route, headers=user["headers"],
                        json={"content": f"Performance {index} {sequence}", "parentCommentId": None})
                else:
                    response = await vu_client.get(route, headers=user["headers"])
                statuses[str(response.status_code)] += 1
                response_bytes += len(response.content)
            except httpx.HTTPError as exc:
                statuses[type(exc).__name__] += 1
            elapsed_ms = (time.perf_counter() - before) * 1000
            latency.append(elapsed_ms)
            per_route[route].append(elapsed_ms)
            sequence += 1
            # Bounded arrival rate: no uncontrolled tight loop or unbounded writes.
            await asyncio.sleep(.1 if write else .2)

    async def monitor():
        while time.perf_counter() < deadlines:
            memory = process_memory(resource_pid)
            memory.update({"seconds": round(time.perf_counter()-start, 3), "utc": datetime.now(timezone.utc).isoformat(),
                           "databaseConnections": int(await asyncio.to_thread(sql, database, f"SELECT count(*) FROM pg_stat_activity WHERE datname='{database}';"))})
            resources.append(memory)
            await asyncio.sleep(5)
    try:
        await asyncio.gather(*(worker(i) for i in range(vus)), *([monitor()] if resource_pid else []))
        elapsed = time.perf_counter() - start
    finally:
        await asyncio.gather(*(vu_client.aclose() for vu_client in vu_clients))
    errors = sum(v for k, v in statuses.items() if not k.isdigit() or int(k) >= 500)
    return {"vus": vus, "seconds": round(elapsed, 3), "requests": len(latency),
            "p50_ms": round(float(np.percentile(latency, 50)), 3),
            "p95_ms": round(float(np.percentile(latency, 95)), 3),
            "p99_ms": round(float(np.percentile(latency, 99)), 3), "statuses": dict(statuses),
            "error_fraction": errors / max(1, len(latency)), "response_bytes": response_bytes,
            "requests_per_second": round(len(latency) / elapsed, 3), "resources": resources,
            "client_cpu_seconds": round(time.process_time() - cpu_start, 3),
            "client_connection_model": "One independent HTTP client and pooled connection per VU",
            "per_route": {route: {"requests": len(values),
                "p95_ms": round(float(np.percentile(values, 95)), 3),
                "p99_ms": round(float(np.percentile(values, 99)), 3)} for route, values in per_route.items()}}


async def fixed_volume_read(client, users, routes, request_count, vus, resource_pid, database):
    """Run an exact, read-only HTTP volume against this runner's isolated local API."""
    if request_count <= 0 or vus <= 0:
        raise ValueError("request_count and vus must be positive")
    vu_clients = [httpx.AsyncClient(base_url=client.base_url, timeout=client.timeout,
        trust_env=False, verify=LOAD_TLS_CONTEXT,
        limits=httpx.Limits(max_connections=1, max_keepalive_connections=1)) for _ in range(vus)]
    run_key = uuid.uuid4().hex
    started = time.perf_counter()
    latency, statuses, response_bytes = [], collections.Counter(), 0
    per_route = [list() for _ in routes]
    per_route_status = [collections.Counter() for _ in routes]
    base, remainder = divmod(request_count, vus)
    stop_monitor = asyncio.Event()
    resources = []

    async def monitor():
        while not stop_monitor.is_set():
            sample = process_memory(resource_pid)
            sample.update({
                "seconds": round(time.perf_counter() - started, 3),
                "utc": datetime.now(timezone.utc).isoformat(),
                "databaseConnections": int(await asyncio.to_thread(
                    sql, database,
                    f"SELECT count(*) FROM pg_stat_activity WHERE datname='{database}';")),
            })
            resources.append(sample)
            try:
                await asyncio.wait_for(stop_monitor.wait(), timeout=5)
            except asyncio.TimeoutError:
                pass

    async def worker(index):
        nonlocal response_bytes
        vu_client = vu_clients[index]
        user = users[index % len(users)]
        count = base + (1 if index < remainder else 0)
        first = index * base + min(index, remainder)
        for offset in range(count):
            request_index = first + offset
            route_index = request_index % len(routes)
            route = routes[route_index]
            separator = "&" if "?" in route else "?"
            url = f"{route}{separator}benchRun={run_key}-{request_index}"
            before = time.perf_counter()
            try:
                response = await vu_client.get(url, headers=user["headers"])
                status = response.status_code
                statuses[str(status)] += 1
                per_route_status[route_index][str(status)] += 1
                response_bytes += len(response.content)
            except httpx.HTTPError as exc:
                status = None
                error = type(exc).__name__
                statuses[error] += 1
                per_route_status[route_index][error] += 1
            latency_ms = (time.perf_counter() - before) * 1000
            latency.append(latency_ms)
            per_route[route_index].append(latency_ms)

    monitor_task = asyncio.create_task(monitor())
    try:
        await asyncio.gather(*(worker(index) for index in range(vus)))
    finally:
        stop_monitor.set()
        await monitor_task
        await asyncio.gather(*(vu_client.aclose() for vu_client in vu_clients))
    elapsed = time.perf_counter() - started
    successful = statuses.get("200", 0)
    errors = request_count - successful
    endpoint_results = {}
    for index, route in enumerate(routes):
        values = per_route[index]
        endpoint_results[route] = {
            "requests": len(values),
            "p50_ms": round(float(np.percentile(values, 50)), 3),
            "p95_ms": round(float(np.percentile(values, 95)), 3),
            "p99_ms": round(float(np.percentile(values, 99)), 3),
            "statuses": dict(per_route_status[index]),
        }
    return {
        "vus": vus,
        "seconds": round(elapsed, 3),
        "requests": len(latency),
        "p50_ms": round(float(np.percentile(latency, 50)), 3),
        "p95_ms": round(float(np.percentile(latency, 95)), 3),
        "p99_ms": round(float(np.percentile(latency, 99)), 3),
        "statuses": dict(statuses),
        "error_fraction": errors / max(1, request_count),
        "response_bytes": response_bytes,
        "requests_per_second": round(request_count / elapsed, 3),
        "resources": resources,
        "client_connection_model": "One independent HTTP client and pooled connection per VU",
        "per_route": endpoint_results,
    }


async def media_profile(client, owner, channel, database, runtime, results, save):
    source = ROOT / ".work/planned-20261023/source-99mb.mp4"
    if not source.exists():
        raise RuntimeError("Generate the documented 99 MB / 60 second 720p source first; see run report.")
    probe = json.loads(await asyncio.to_thread(subprocess.check_output, ["ffprobe", "-v", "error", "-show_entries", "format=size,duration", "-of", "json", str(source)]))
    assert source.stat().st_size >= 98_000_000 and float(probe["format"]["duration"]) >= 60
    source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    batches = []
    try:
        for concurrency in (1, 3):
            started = time.perf_counter()
            async def upload(index, concurrency=concurrency):
                with source.open("rb") as stream:
                    response = await client.post("/api/v1/videos", headers={**owner["headers"], "Idempotency-Key": str(uuid.uuid4())},
                        files={"video": ("source-99mb.mp4", stream, "video/mp4")},
                        data={"channelId": channel, "title": f"Real media {concurrency} {index}",
                              "visibility": "private", "sourceQuality": "720p", "duration": "61"}, timeout=180)
                assert response.status_code == 201, f"Upload returned {response.status_code}: {response.text[:800]}"
                return response.json()["videoId"]
            ids = await asyncio.gather(*(upload(i) for i in range(concurrency)))
            for video in ids:
                deadline = time.perf_counter() + 1200
                while time.perf_counter() < deadline:
                    rows = json.loads(sql(database, f"SELECT coalesce(json_agg(r),'[]') FROM video_renditions r WHERE video_id='{video}';"))
                    if len(rows) >= 3 and all(row["status"] == "ready" for row in rows): break
                    if any(row["status"] == "failed" for row in rows): raise AssertionError("Real transcoding failed")
                    await asyncio.sleep(2)
                else: raise AssertionError("Real transcoding exceeded 20-minute deadline")
                metadata = (await client.get(f"/api/v1/videos/{video}", headers=owner["headers"])).json()
                assert metadata["fileSize"] == source.stat().st_size
                from urllib.parse import urlparse
                source_row = max(rows, key=lambda row: row["height"])
                stored_source = runtime / urlparse(source_row["file_url"]).path.lstrip("/")
                assert hashlib.sha256(stored_source.read_bytes()).hexdigest() == source_hash
                for row in rows:
                    path = runtime / urlparse(row["file_url"]).path.lstrip("/")
                    assert path.resolve().is_relative_to(runtime.resolve())
                    assert path.stat().st_size == row["file_size"]
                    output_probe = json.loads(await asyncio.to_thread(subprocess.check_output, ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=height:format=duration", "-of", "json", str(path)]))
                    assert output_probe["streams"][0]["height"] <= 720
                    assert abs(float(output_probe["format"]["duration"]) - float(probe["format"]["duration"])) < 2
                    response = await client.get(urlparse(row["file_url"]).path, headers={"Range": "bytes=0-1023"})
                    assert response.status_code == 206 and len(response.content) == 1024
                    assert response.content == path.read_bytes()[:1024]
            batches.append({"concurrent_uploads": concurrency, "videos": ids,
                            "time_to_ready_seconds": round(time.perf_counter()-started, 3), "source_bytes": source.stat().st_size})
        actual = f"Local storage + real FFmpeg/ffprobe + Kestrel/PostgreSQL. Source {source.stat().st_size} bytes, {probe['format']['duration']} seconds; upload 1 then 3, stored source SHA256 equal, every output probed and byte-range read."
        for case_id in ("B-MEDIA.01", "B-PERF.08"):
            results["cases"].append({"id": case_id, "status": "Pass", "actual": actual, "batches": batches,
                                    "endedAt": datetime.now(timezone.utc).isoformat()})
    except (AssertionError, RuntimeError, OSError, ValueError, httpx.HTTPError, subprocess.SubprocessError) as exc:
        for case_id in ("B-MEDIA.01", "B-PERF.08"):
            results["cases"].append({"id": case_id, "status": "Fail", "actual": f"{type(exc).__name__}: {str(exc)[:1500]}", "batches": batches,
                                    "endedAt": datetime.now(timezone.utc).isoformat()})
    save()


async def recovery_profile(client, owner, channel, database, runtime, results, save, restart):
    source = ROOT / ".work/planned-20261023/source-99mb.mp4"
    upload_id = str(uuid.uuid4())
    request = {"uploadId": upload_id, "channelId": channel, "fileName": source.name,
               "contentType": "video/mp4", "fileSize": source.stat().st_size, "duration": 61, "sourceQuality": "720p"}
    root = runtime / "temp/hutube-user-video-chunks"
    actual = []
    try:
        response = await client.post("/api/v1/videos/upload-sessions", json=request, headers=owner["headers"])
        assert response.status_code == 200, response.text
        chunk_size = response.json()["chunkSize"]
        with source.open("rb") as stream:
            chunk = stream.read(chunk_size)
            response = await client.post(f"/api/v1/videos/upload-sessions/{upload_id}/chunks/0",
                headers=owner["headers"], files={"chunk": ("part.bin", chunk)})
            assert response.status_code == 200 and response.json()["nextChunk"] == 1
            await restart()
            path = root / (upload_id.replace("-", "") + ".upload")
            with path.open("ab") as interrupted: interrupted.write(b"uncommitted-tail")
            response = await client.post("/api/v1/videos/upload-sessions", json=request, headers=owner["headers"])
            assert response.status_code == 200 and response.json()["nextChunk"] == 1
            index = 1
            while chunk := stream.read(chunk_size):
                response = await client.post(f"/api/v1/videos/upload-sessions/{upload_id}/chunks/{index}",
                    headers=owner["headers"], files={"chunk": ("part.bin", chunk)})
                assert response.status_code == 200 and response.json()["nextChunk"] == index+1
                index += 1
        assert hashlib.sha256(path.read_bytes()).digest() == hashlib.sha256(source.read_bytes()).digest()
        fields = {"channelId": channel, "title": "Recovery media", "visibility": "private"}
        first = await client.post(f"/api/v1/videos/upload-sessions/{upload_id}/complete", headers=owner["headers"], data=fields, timeout=180)
        assert first.status_code == 201, first.text
        video = first.json()["videoId"]
        second = await client.post(f"/api/v1/videos/upload-sessions/{upload_id}/complete", headers=owner["headers"], data=fields)
        assert second.status_code == 200 and second.json()["videoId"] == video
        assert int(sql(database, f"SELECT count(*) FROM videos WHERE idempotency_key='{upload_id}';")) == 1
        assert not path.exists()
        await restart()
        receipt = await client.get(f"/api/v1/videos/upload-sessions/{upload_id}", headers=owner["headers"])
        assert receipt.status_code == 200 and receipt.json()["videoId"] == video
        actual.append("Killed owned Kestrel tree after committed chunk; restart recovered same uploadId; uncommitted tail truncated; 99 MB SHA256 matched; completion replay and second process restart returned one persisted video and no source orphan.")
        cancellation = {**request, "uploadId": str(uuid.uuid4())}
        assert (await client.post("/api/v1/videos/upload-sessions", json=cancellation, headers=owner["headers"])).status_code == 200
        assert (await client.delete(f"/api/v1/videos/upload-sessions/{cancellation['uploadId']}", headers=owner["headers"])).status_code == 204
        assert (await client.get(f"/api/v1/videos/upload-sessions/{cancellation['uploadId']}", headers=owner["headers"])).status_code == 404
        actual.append("Cancel removed session and lookup returns 404. Malformed media and write-failure checks are in integration-current.trx.")
        status = "Pass"
    except (AssertionError, RuntimeError, OSError, ValueError, httpx.HTTPError, subprocess.SubprocessError) as exc:
        status = "Fail"
        actual.append(f"{type(exc).__name__}: {str(exc)[:1000]}")
    finally:
        await client.delete(f"/api/v1/videos/upload-sessions/{upload_id}", headers=owner["headers"])
    results["cases"].append({"id": "B-MEDIA.02", "status": status, "actual": " ".join(actual),
                            "endedAt": datetime.now(timezone.utc).isoformat()})
    save()


async def execute(args):
    global BASE, OUT
    BASE = f"http://127.0.0.1:{args.port}"
    if args.output_dir:
        OUT = ROOT / args.output_dir
    WORK.mkdir(parents=True, exist_ok=True)
    OUT.mkdir(parents=True, exist_ok=True)
    run = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    database = "hutube_planned_" + uuid.uuid4().hex
    runtime = WORK / run
    runtime.mkdir()
    pickup = runtime / "mail"
    pickup.mkdir()
    (runtime / "temp").mkdir()
    env = os.environ.copy()
    # Empty content root prevents reading production appsettings or credential files.
    env.update({"ASPNETCORE_ENVIRONMENT": "Development", "ASPNETCORE_CONTENTROOT": str(runtime),
        "ASPNETCORE_URLS": BASE, "ConnectionStrings__Database": f"Host=127.0.0.1;Port=55441;Username=hutube_test;Database={database}",
        "Jwt__SigningKey": secrets.token_hex(48), "Email__Mode": "Pickup", "Email__PickupDirectory": str(pickup),
        "Redis__Enabled": "false", "VideoProcessing__Enabled": "true", "VideoProcessing__Threads": "1",
        "Features__ModerationEnabled": "false", "Features__PlanEnforcementEnabled": "true",
        "Recommendation__Enabled": "false", "GeoIp__Enabled": "false",
        "Auth__AccessTokenMinutes": "60", "Auth__WebBaseUrl": "http://localhost:4200", "Auth__AdminBaseUrl": "http://localhost:4201",
        "Logging__LogLevel__Default": "Warning"})
    if args.only == "read-diagnostic":
        env["Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information"
    env["TEMP"] = env["TMP"] = str(runtime / "temp")
    # Do not inherit external service credentials from the invoking shell.
    for key in list(env):
        if any(x in key.upper() for x in ("UPSTASH", "R2_", "STORAGE__R2", "RECOMMENDER_", "STORAGE__API", "STORAGE__CLOUD")):
            del env[key]
    for policy in ("Auth", "Feed", "Search", "Playback", "Upload", "Admin", "Moderation"):
        env[f"RateLimit__{policy}PermitLimit"] = "1000000"
    dll = ROOT / "backend/src/HuTube.Api/bin/Release/net10.0/HuTube.Api.dll"
    results = {"runId": run, "startedAt": datetime.now(timezone.utc).isoformat(),
               "runtime": "Release .NET 10 Kestrel + isolated PostgreSQL 18; local storage; Redis/CF disabled",
               "database": database, "cases": [], "clientAsyncBackend": sniffio.current_async_library()}
    api = None
    log = (OUT / f"{args.output_name}-kestrel.log").open("w", encoding="utf-8")
    def save():
        (OUT / f"{args.output_name}.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    def record(case_id, stages, actual, *, incomplete=""):
        bad = any(s["error_fraction"] >= .01 or s["p95_ms"] >= (1000 if case_id in ("B-PERF.03", "B-PERF.04") else 500)
                  or (case_id not in ("B-PERF.03", "B-PERF.04") and s["p99_ms"] >= 1500)
                  or any(k.isdigit() and 400 <= int(k) < 500 and k != "429" for k in s["statuses"]) for s in stages)
        results["cases"].append({"id": case_id, "status": "Fail" if bad else "Blocked" if incomplete else "Pass",
            "actual": actual, "remaining": incomplete, "stages": stages, "endedAt": datetime.now(timezone.utc).isoformat()})
        save()
        print(case_id, results["cases"][-1]["status"], flush=True)
    try:
        sql("postgres", f"CREATE DATABASE {database};")
        migration = await asyncio.to_thread(subprocess.run, ["dotnet", str(dll), "--migrate"], cwd=runtime, env=env,
                                   stdout=log, stderr=subprocess.STDOUT, timeout=120, check=False)
        if migration.returncode:
            raise RuntimeError("Local migration failed; inspect kestrel.log")
        api = await asyncio.to_thread(subprocess.Popen, ["dotnet", str(dll)], cwd=runtime, env=env, stdout=log, stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NO_WINDOW)
        async with httpx.AsyncClient(base_url=BASE, timeout=30, trust_env=False,
                                    limits=httpx.Limits(max_connections=130, max_keepalive_connections=120)) as client:
            for _ in range(120):
                try:
                    if (await client.get("/health")).status_code == 200:
                        break
                except httpx.HTTPError:
                    pass
                if api.poll() is not None:
                    raise RuntimeError("Kestrel exited; inspect kestrel.log")
                await asyncio.sleep(.5)
            else:
                raise RuntimeError("Kestrel startup timeout")
            users = []
            for i in range(5):
                name = "planned_" + uuid.uuid4().hex[:16]
                address = name + "@example.invalid"
                response = await client.post("/api/v1/auth/register", json={"username": name, "email": address,
                    "displayName": f"Planned user {i}", "password": PASSWORD})
                assert response.status_code == 201, response.text
                token = None
                for path in pickup.glob("*.eml"):
                    message = email.message_from_bytes(path.read_bytes(), policy=email.policy.default)
                    if address not in str(message["To"]): continue
                    content = message.get_body(preferencelist=("plain",)).get_content()
                    match = re.search(r"[?&]token=([^\s<>]+)", content)
                    if match: token = match.group(1)
                assert token, "Pickup verification link missing"
                from urllib.parse import unquote
                assert (await client.post("/api/v1/auth/verify-email", json={"token": unquote(token)})).status_code == 200
                if i == 0:
                    sql(database, f"UPDATE users SET role_id=(SELECT role_id FROM roles WHERE code='super_admin') WHERE email='{address}';")
                device = str(uuid.uuid4())
                login = await client.post("/api/v1/auth/login", json={"email": address, "password": PASSWORD,
                    "platform": "mobile", "deviceId": device})
                assert login.status_code == 200, login.text
                users.append({"email": address, "device": device, "headers": {"Authorization": "Bearer " + login.json()["accessToken"]}})
            owner = users[0]
            response = await client.post("/api/v1/channels", headers=owner["headers"],
                json={"name": "Performance fixture", "handle": "perf_" + uuid.uuid4().hex[:12], "description": "Isolated local test"})
            assert response.status_code == 201, response.text
            channel = response.json()["channelId"]
            if args.only == "media":
                await media_profile(client, owner, channel, database, runtime, results, save)
                return
            if args.only == "recovery":
                async def restart():
                    nonlocal api
                    # This PID is the process spawned by this runner, including its FFmpeg children.
                    await asyncio.to_thread(subprocess.run, ["taskkill", "/PID", str(api.pid), "/T", "/F"], capture_output=True, check=True)
                    await asyncio.to_thread(api.wait, timeout=15)
                    api = await asyncio.to_thread(subprocess.Popen, ["dotnet", str(dll)], cwd=runtime, env=env,
                        stdout=log, stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
                    for _ in range(90):
                        try:
                            if (await client.get("/health")).status_code == 200: return
                        except httpx.HTTPError: pass
                        await asyncio.sleep(.5)
                    raise RuntimeError("Restarted Kestrel did not become healthy")
                await recovery_profile(client, owner, channel, database, runtime, results, save, restart)
                return
            user_id = sql(database, f"SELECT user_id FROM users WHERE email='{owner['email']}';")
            video_id = str(uuid.uuid4())
            sql(database, f"""INSERT INTO videos(video_id,channel_id,uploaded_by_user_id,title,video_url,duration,file_size,
                visibility,status,published_at,moderation_status,allow_comments) VALUES
                ('{video_id}','{channel}','{user_id}','Planned performance video','/uploads/planned.mp4',2,1024,
                'public','published',now(),'approved',true);""")
            reads = ["/api/v1/feed/home", "/api/v1/feed/explore?q=Planned", f"/api/v1/videos/{video_id}", "/api/v1/library/history"]
            if args.only == "million-read":
                stage_result = await fixed_volume_read(
                    client, users, reads, args.million_requests, args.million_vus, api.pid, database)
                record("B-PERF.02-1M", [stage_result],
                       f"Exactly {args.million_requests:,} GET requests total across home, explore, video detail, and library history; "
                       f"{args.million_vus} VU; local isolated Kestrel/PostgreSQL; no production traffic or writes.")
                return
            if args.only == "read-diagnostic":
                results["diagnosticOnly"] = True
                record("B-PERF.02", [await stage(client, users, reads, vu, args.diagnostic_seconds)
                       for vu in args.diagnostic_vus],
                       "Short diagnostic only; not accepted as final >=120-second SLO evidence.")
                return
            before = after = 0
            if args.only == "failures":
                record("B-PERF.02", [await stage(client, users, reads, vu, args.stage_seconds) for vu in (20, 50, 100)],
                       "Fixed read fixture; 20/50/100 VU, each >=120 seconds; unchanged read SLO.")
                # Match the original spike/soak fixture rather than silently
                # benchmarking an empty comments table after skipping WRITE.
                before = int(sql(database, "SELECT count(*) FROM comments;"))
                sql(database, f"""INSERT INTO comments(user_id,video_id,content,status)
                    SELECT '{user_id}','{video_id}','Prior WRITE fixture '||g,'visible'
                    FROM generate_series(1,9149)g;""")
                after = int(sql(database, "SELECT count(*) FROM comments;"))
                assert after-before == 9149
            elif args.only != "post-auth":
                baseline = [await stage(client, users, reads, 1, 5) for _ in range(2)]
                record("B-PERF.01", baseline, "Cold/warm reads on real Kestrel and PostgreSQL.",
                       incomplete="Redis on/off comparison and SQL query instrumentation require an isolated REST Redis endpoint.")
                record("B-PERF.02", [await stage(client, users, reads, vu, args.stage_seconds) for vu in (20, 50, 100)],
                       "20/50/100 VU feed/search/detail/library; fixed fixtures and per-response status/latency.")
                before = int(sql(database, "SELECT count(*) FROM comments;"))
                writes = [await stage(client, users, [f"/api/v1/videos/{video_id}/comments"], vu, args.stage_seconds, write=True) for vu in (5, 10)]
                after = int(sql(database, "SELECT count(*) FROM comments;"))
                committed = sum(s["statuses"].get("201", 0) for s in writes)
                assert after - before == committed, "Successful writes differ from committed rows"
                record("B-PERF.03", writes, f"Hot-video comments: {committed} HTTP 201 and exactly {after-before} committed rows.",
                       incomplete="Reaction/profile races and lock-wait sampling are not covered by the comment workload.")
                record("B-PERF.04", [await stage(client, users, ["/api/v1/auth/login"], 5, 30, login=True)],
                       "Login reuses five verified accounts and stable device IDs; no registration inside workload.",
                       incomplete="Web cookies and serial refresh plus refresh race are recorded by integration tests, not this load profile.")
            admin = []
            for target in (61, 1000, 10000):
                count = int(sql(database, "SELECT count(*) FROM videos;"))
                sql(database, f"""INSERT INTO videos SELECT (jsonb_populate_record(NULL::videos,
                    to_jsonb(v)||jsonb_build_object('video_id',gen_random_uuid(),'title','Planned '||g,'idempotency_key',NULL))).*
                    FROM videos v CROSS JOIN generate_series(1,{target-count})g WHERE video_id='{video_id}';""")
                payment_count = int(sql(database, "SELECT count(*) FROM payments;"))
                sql(database, f"""INSERT INTO payments(user_id,plan_id,amount,payment_method,transaction_code,status,paid_at,expires_at)
                    SELECT '{user_id}',(SELECT plan_id FROM plans LIMIT 1),1000,'sepay','TEST-'||gen_random_uuid(),'paid',now(),now()+interval '1 day'
                    FROM generate_series(1,{target-payment_count});""")
                history_count = int(sql(database, "SELECT count(*) FROM plan_histories;"))
                sql(database, f"""INSERT INTO plan_histories SELECT (jsonb_populate_record(NULL::plan_histories,
                    to_jsonb(h)||jsonb_build_object('plan_history_id',gen_random_uuid(),'status','expired','auto_renew',false))).*
                    FROM plan_histories h CROSS JOIN generate_series(1,{target-history_count})g WHERE plan_history_id=(SELECT plan_history_id FROM plan_histories LIMIT 1);""")
                for route in ("/api/v1/admin/payments/export", "/api/v1/admin/subscriptions/export"):
                    exported = await client.get(route, headers=owner["headers"])
                    assert exported.status_code == 200, f"Admin fixture auth failed: {exported.status_code}"
                    assert isinstance(exported.json(), list) and len(exported.json()) == target, f"Export total mismatch: {route}"
                page = await client.get("/api/v1/admin/videos?pageSize=20", headers=owner["headers"])
                assert page.status_code == 200 and page.json()["total"] == target
                stage_result = await stage(client, [owner], ["/api/v1/admin/videos?page=1&pageSize=20", "/api/v1/admin/users?page=1&pageSize=20", "/api/v1/admin/videos/statistics", "/api/v1/admin/payments/export", "/api/v1/admin/subscriptions/export"], 1, 5)
                stage_result["metadata_rows"] = target
                admin.append(stage_result)
            record("B-PERF.05", admin, "61/1000/10000 video/payment/subscription records; totals/export JSON checked against database; no duplicated media files. Export API contract is JSON, not CSV.")
            spike = [await stage(client, users, reads, vu, seconds) for vu, seconds in [(10, 30), (100, 60), (10, 30)]]
            record("B-PERF.06", spike, "10 to 100 VU spike and recovery; 429 recorded separately from 5xx.")
            if args.only == "failures":
                results["soakWarmup"] = await stage(client, users, reads, 20, 120, resource_pid=api.pid, database=database)
                results["soakWarmup"]["note"] = "120-second 20-VU warmup before measuring 600-second growth; no forced GC or process restart."
                save()
            soak = await stage(client, users, reads, 20, args.soak_seconds, resource_pid=api.pid, database=database)
            soak["pg_connections"] = int(sql(database, f"SELECT count(*) FROM pg_stat_activity WHERE datname='{database}';"))
            growth = (soak["resources"][-1]["workingSetBytes"]-soak["resources"][0]["workingSetBytes"])/soak["resources"][0]["workingSetBytes"]
            soak["working_set_growth_fraction"] = growth
            record("B-PERF.07", [soak], f"20 VU 600-second fixed-data soak; working set growth {growth:.1%}, {len(soak['resources'])} memory/connection samples. No user/video creation during load.")
            if growth > .10: results["cases"][-1]["status"] = "Fail"; save()
            for case_id, reason in ({} if args.only == "failures" else {
                "B-PERF.08": "Requires the specified 99 MB media fixture and real upload/transcode workload (the functional FFmpeg suite uses a 2-second generated source).",
                "B-PERF.09": "Model publication requires a dedicated R2 sandbox. No sandbox configured; production credentials are not used.",
                "B-PERF.10": "Redis/CF/storage outage and controlled DB failure profiles need dedicated fault targets; no such targets configured. Unit fault tests are recorded separately."
            }).items():
                record(case_id, [], "Prerequisites inspected on isolated local runtime.", incomplete=reason)
            results["manifest"] = {"userCount": len(users), "channelId": channel, "videoId": video_id, "metadataRows": 10000,
                                   "comments": after-before, "apiPid": api.pid}
    except Exception as exc:
        results["error"] = f"{type(exc).__name__}: {exc}"
        raise
    finally:
        if api is not None and api.poll() is None:
            api.terminate()
            try: api.wait(timeout=15)
            except subprocess.TimeoutExpired: api.kill(); api.wait(timeout=5)
        log.close()
        sql("postgres", f"DROP DATABASE IF EXISTS {database} WITH (FORCE);")
        results["endedAt"] = datetime.now(timezone.utc).isoformat()
        results["cleanup"] = "Spawned Kestrel stopped; isolated test database dropped. Pickup files remain only in ignored .work."
        save()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage-seconds", type=int, default=120)
    parser.add_argument("--soak-seconds", type=int, default=600)
    parser.add_argument("--only", choices=["all", "media", "recovery", "post-auth", "failures", "read-diagnostic", "million-read"], default="all")
    parser.add_argument("--port", type=int, default=55141)
    parser.add_argument("--output-name", default="performance")
    parser.add_argument("--output-dir", default="")
    parser.add_argument("--diagnostic-seconds", type=int, default=15)
    parser.add_argument("--diagnostic-vus", nargs="+", type=int, default=[20, 50, 100])
    parser.add_argument("--million-requests", type=int, default=1_000_000)
    parser.add_argument("--million-vus", type=int, default=64)
    args = parser.parse_args()
    if args.stage_seconds < 120 or args.soak_seconds < 600:
        parser.error("Documented performance evidence requires stages >=120s and soak >=600s.")
    asyncio.run(execute(args))
