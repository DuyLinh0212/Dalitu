"""Supplement hot-resource writes and refresh while the local soak server is up."""
from __future__ import annotations

import asyncio
import collections
import json
import time
import uuid
from datetime import datetime, timezone

import httpx
import numpy as np
from run_planned_performance import OUT, PASSWORD, sql


async def main():
    parent = json.loads((OUT / "performance.json").read_text())
    database = parent["database"]
    assert database.startswith("hutube_planned_")
    rows = sql(database, "SELECT email FROM users WHERE email LIKE '%@example.invalid' ORDER BY email;").splitlines()
    assert len(rows) == 5
    video = sql(database, "SELECT video_id FROM videos WHERE title='Planned performance video';")
    results = {"parentRunId": parent["runId"], "startedAt": datetime.now(timezone.utc).isoformat(), "cases": []}
    async with httpx.AsyncClient(base_url="http://127.0.0.1:55141", timeout=30, trust_env=False) as client:
        users = []
        for address in rows:
            response = await client.post("/api/v1/auth/login", json={"email": address, "password": PASSWORD,
                "platform": "mobile", "deviceId": str(uuid.uuid4())})
            assert response.status_code == 200
            users.append(response.json())
        stages = []
        for count in (5, 10):
            latency, statuses = [], collections.Counter()
            deadline = time.perf_counter() + 120
            async def writer(index, deadline=deadline, latency=latency, statuses=statuses):
                actor = users[index % 5]
                header = {"Authorization": "Bearer " + actor["accessToken"]}
                iteration = 0
                while time.perf_counter() < deadline:
                    started = time.perf_counter()
                    if iteration % 3 == 0:
                        response = await client.put(f"/api/v1/videos/{video}/reaction", headers=header,
                                                    json={"type": "like"})
                    else:
                        response = await client.patch("/api/v1/account/profile", headers=header,
                            json={"displayName": "Concurrent display"} if iteration % 3 == 1 else {"bio": "Concurrent bio"})
                    latency.append((time.perf_counter() - started) * 1000)
                    statuses[str(response.status_code)] += 1
                    iteration += 1
                    await asyncio.sleep(.1)
            await asyncio.gather(*(writer(i) for i in range(count)))
            stages.append({"vus": count, "seconds": 120, "requests": len(latency), "statuses": dict(statuses),
                           "p95_ms": float(np.percentile(latency, 95)), "p99_ms": float(np.percentile(latency, 99))})
        reaction_count = int(sql(database, f"SELECT count(*) FROM video_reactions WHERE video_id='{video}';"))
        assert reaction_count == 5, f"Expected one reaction per actor, got {reaction_count}"
        for actor in users:
            profile = (await client.get("/api/v1/account/profile", headers={"Authorization": "Bearer " + actor["accessToken"]})).json()
            assert profile["displayName"] == "Concurrent display" and profile["bio"] == "Concurrent bio"
        bad = any(set(s["statuses"]) != {"200"} or s["p95_ms"] >= 1000 for s in stages)
        results["cases"].append({"id": "B-PERF.03", "status": "Fail" if bad else "Pass", "stages": stages,
            "actual": "Supplement: 5/10 VU reaction/profile race; exactly five reactions for five actors, concurrent profile fields retained. Combine with committed-comment cardinality in performance.json.",
            "endedAt": datetime.now(timezone.utc).isoformat()})
        started = time.perf_counter()
        latency, statuses = [], collections.Counter()
        async def refresh(actor):
            current = actor
            while time.perf_counter() - started < 60:
                before = time.perf_counter()
                response = await client.post("/api/v1/auth/refresh", json={"refreshToken": current["refreshToken"]})
                statuses[str(response.status_code)] += 1
                assert response.status_code == 200, response.text
                new = response.json()
                assert current["refreshToken"] != new["refreshToken"]
                current = new
                assert (await client.get("/api/v1/auth/me", headers={"Authorization": "Bearer " + current["accessToken"]})).status_code == 200
                latency.append((time.perf_counter()-before)*1000)
                await asyncio.sleep(.2)
        await asyncio.gather(*(refresh(u) for u in users))
        async with httpx.AsyncClient(base_url="http://127.0.0.1:55141", trust_env=False,
                                    headers={"X-HuTube-Client": "web", "Origin": "http://localhost:4200"}) as web:
            response = await web.post("/api/v1/auth/login", json={"email": rows[0], "password": PASSWORD, "platform": "web"})
            assert response.status_code == 200
            cookie = response.headers["set-cookie"].lower()
            assert "httponly" in cookie and "samesite=lax" in cookie
            assert response.json()["refreshToken"] is None
            assert (await web.post("/api/v1/auth/refresh", json={})).status_code == 200
            del web.headers["X-HuTube-Client"]
            assert (await web.post("/api/v1/auth/refresh", json={})).status_code == 401
        p95 = float(np.percentile(latency, 95))
        results["cases"].append({"id": "B-PERF.04", "status": "Pass" if p95 < 1000 else "Fail",
            "actual": "Five users refresh serially per session for 60s; rotate tokens and read /me. Actual web HttpOnly/SameSite cookie and CSRF-header rejection checked under concurrent soak.",
            "p95_ms": p95, "statuses": dict(statuses), "endedAt": datetime.now(timezone.utc).isoformat()})
    results["endedAt"] = datetime.now(timezone.utc).isoformat()
    (OUT / "write-auth-supplement.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"cases": [(x["id"], x["status"]) for x in results["cases"]]}))


if __name__ == "__main__":
    asyncio.run(main())
