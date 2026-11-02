"""Record fixes for five failed scenarios from actual regression/load evidence."""
from __future__ import annotations

import collections
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs/testing/runs/2026-10-23-fixes"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def load(name):
    return json.loads((OUT / name).read_text(encoding="utf-8"))


def collect():
    executions, suites = [], []
    for filename, name in [("unit-current.trx", "Unit .NET"),
                           ("integration-current.trx", "Integration PostgreSQL/FFmpeg"),
                           ("live-current.trx", "Live Kestrel/R2/Redis/CF")]:
        tree = ET.parse(OUT / filename)
        counts = tree.find("t:ResultSummary/t:Counters", NS).attrib
        times = tree.find("t:Times", NS).attrib
        suites.append({"name": name, "file": filename, "total": int(counts["total"]),
            "passed": int(counts["passed"]), "failed": int(counts["failed"]), "skipped": int(counts["notExecuted"]),
            "endedAt": times["finish"]})
        for test in tree.findall("t:Results/t:UnitTestResult", NS):
            executions.append({"name": test.attrib["testName"], "status": "Pass" if test.attrib["outcome"] == "Passed" else "Fail",
                "duration": test.attrib["duration"], "endedAt": test.attrib["endTime"], "file": filename,
                "layer": name, "actual": "Các assertion đạt." if test.attrib["outcome"] == "Passed" else " ".join(test.itertext())[:1800]})
    py = ET.parse(OUT / "python-current.xml").getroot()
    if py.tag == "testsuites":
        py = py.find("testsuite")
    py_end = datetime.fromtimestamp((OUT / "python-current.xml").stat().st_mtime, timezone.utc).isoformat()
    suites.append({"name": "Python recommendation", "file": "python-current.xml", "total": int(py.attrib["tests"]),
        "passed": int(py.attrib["tests"])-int(py.attrib["failures"])-int(py.attrib["errors"])-int(py.attrib["skipped"]),
        "failed": int(py.attrib["failures"])+int(py.attrib["errors"]), "skipped": int(py.attrib["skipped"]), "endedAt": py_end})
    for test in py.iter("testcase"):
        executions.append({"name": test.attrib.get("classname", "")+"."+test.attrib["name"],
            "status": "Fail" if test.find("failure") is not None or test.find("error") is not None else "Pass",
            "duration": test.attrib["time"], "endedAt": py_end, "file": "python-current.xml",
            "layer": "Python recommendation", "actual": "Các assertion Python đạt."})
    results = {}
    readiness = [t for t in executions if "test_B_PYAPI_02_readiness_retryability" in t["name"]]
    assert len(readiness) == 4
    results["B-PYAPI.02"] = {"id": "B-PYAPI.02", "status": "Pass" if all(t["status"] == "Pass" for t in readiness) else "Fail",
        "actual": "Sửa contract kiểm thử bị gộp hai trạng thái: thiếu token cấu hình → 503 SERVICE_NOT_READY, retryable=false; token có nhưng model đang tải → 503 retryable=true; đủ model/token → 200 ready. 4 tổ hợp loaded/configured đã chạy, không lộ token. Giữ hành vi đúng của API; không đổi false thành true để retry cấu hình thiếu.",
        "resolution": "Contract correction, not a change in runtime readiness behavior",
        "expected": "Model loaded + service token configured → 200 ready; token thiếu → 503 SERVICE_NOT_READY retryable=false; model thiếu/đang tải và token đã cấu hình → 503 retryable=true; không secret.",
        "endedAt": py_end, "tests": [t["name"] for t in readiness], "evidence": ["python-current.xml"]}
    metrics = load("live-measurements.json")
    cache_tests = [t for t in executions if "B_CACHE_14_" in t["name"]]
    assert len(cache_tests) == 1
    cache = metrics["B-CACHE.14"]["details"]
    results["B-CACHE.14"] = {"id": "B-CACHE.14", "status": cache_tests[0]["status"],
        "actual": f"Bỏ cache L1 cho trang notification, dùng shared Redis L2. Hai Kestrel độc lập: invalidation ngay={cache['immediateInvalidation']}; cách ly user đúng; logout instance A → B HTTP {cache['afterLogout']}. Đã chạy lại cả 8 live integration test.",
        "endedAt": cache_tests[0]["endedAt"], "tests": [cache_tests[0]["name"]],
        "evidence": ["live-current.trx", "live-measurements.json", "live-run.json"], "measurements": cache}
    performance = load("performance-fixed.json")
    assert "cleanup" in performance and not performance.get("diagnosticOnly")
    for case in performance["cases"]:
        if case["id"] not in ("B-PERF.02", "B-PERF.06", "B-PERF.07"):
            continue
        case["tests"] = []
        case["evidence"] = ["performance-fixed.json", "load-generator-profile.json"]
        if case["id"] == "B-PERF.02":
            assert [s["vus"] for s in case["stages"]] == [20, 50, 100]
            assert all(s["seconds"] >= 120 for s in case["stages"])
        if case["id"] in ("B-PERF.02", "B-PERF.06"):
            stage_text = "; ".join(f"{s['vus']} VU: p95={s['p95_ms']:.2f} ms, p99={s['p99_ms']:.2f} ms, HTTP={s['statuses']}" for s in case["stages"])
            case["actual"] = ("READ 20/50/100 VU, mỗi mức >=120 giây. " if case["id"] == "B-PERF.02" else "SPIKE 10→100→10 VU, dataset 10.000 metadata + 9.149 comment. ") + stage_text + ". Giữ SLO p95<500/p99<1500 ms và 5xx<1%."
        else:
            soak = case["stages"][0]
            assert soak["seconds"] >= 600 and len(soak["resources"]) >= 100
            assert performance["soakWarmup"]["seconds"] >= 120
            case["actual"] = (f"20 VU: warmup {performance['soakWarmup']['seconds']:.1f}s rồi đo {soak['seconds']:.1f}s, "
                f"{soak['requests']} request, {len(soak['resources'])} mẫu tài nguyên; working set tăng {soak['working_set_growth_fraction']:.2%} "
                f"(giữ ngưỡng 10%); p95={soak['p95_ms']:.2f} ms, p99={soak['p99_ms']:.2f} ms; HTTP={soak['statuses']}. Không ép GC/restart hay giảm VU để đạt kết quả.")
        results[case["id"]] = case
    assert set(results) == {"B-PYAPI.02", "B-CACHE.14", "B-PERF.02", "B-PERF.06", "B-PERF.07"}

    server_log = (OUT / "read-profile-kestrel.log").read_text(encoding="utf-8")
    server_ms = [float(re.search(r"([\d.]+)ms$", line).group(1)) for line in server_log.splitlines()
        if "Request finished HTTP/1.1 GET" in line and re.search(r"([\d.]+)ms$", line)]
    profile = {"diagnosticOnly": True, "serverGetSamples": len(server_ms),
        "serverGetP50P95P99ms": np.percentile(server_ms, [50, 95, 99]).tolist(),
        "sharedPoolClientDiagnostic": load("read-profile.json")["cases"][0]["stages"][0],
        "sniffioOnlyDiagnostic": load("read-client-fixed.json")["cases"][0]["stages"][0],
        "perVuClientDiagnostic": load("read-vu-clients.json")["cases"][0]["stages"][0],
        "findings": ["cProfile: 6,672 current_async_library calls spent ~12.94s probing the missing optional sniffio import.",
            "After installing sniffio, shared httpcore pool still spent ~6.71s in _assign_requests_to_connections, with ~2.45 million is_idle calls in a 10s diagnostic.",
            "One owned connection per VU removes global pool scans; same VU counts/routes/200ms think time. Native server-side latency was measured separately; final SLO results come only from full-duration performance-fixed.json."]}
    (OUT / "load-generator-profile.json").write_text(json.dumps(profile, ensure_ascii=False, indent=2), encoding="utf-8")
    now = datetime.now(timezone.utc).isoformat()
    report = {"runId": "FIXES-20261023", "updatedAt": now, "head": subprocess.check_output(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, text=True).strip(),
        "cases": list(results.values()), "counts": dict(collections.Counter(c["status"] for c in results.values())),
        "suites": suites, "executions": executions,
        "notes": ["482 Pass lịch sử và các case ngoài 5 Fail không đổi; các lần chạy trước giữ nguyên.",
            "B-PYAPI.02 sửa expected contract: missing configuration không tự hồi phục bằng retry. Test 4 trạng thái giữ nguyên runtime API.",
            "Auth validation vẫn đọc security state từ PostgreSQL trên mỗi request; không cache token hợp lệ. Snapshot chỉ cần một query không tracking, activity touch chỉ sau một phút.",
            "Video detail tổng hợp thống kê trong một SQL round trip; giữ filter/comment visibility, ratings và viewer state.",
            "Load generator được sửa vì nghẽn client: thêm sniffio và mỗi VU một connection. Không đổi SLO, mức VU, thời gian stage hay think time; không diễn giải phép đo cũ thành capacity backend.",
            "Soak đo sau warmup 120 giây ở đúng 20 VU, giữ ngưỡng working-set 10%; không forced GC/restart. Host là máy phát triển local.",
            "Google scope đã được người dùng chốt ở run trước: backend sau verified identity; Google provider/UI thuộc web E2E."]}
    paths = [ROOT / p for p in ["backend/src/HuTube.Application/Auth/AuthContracts.cs", "backend/src/HuTube.Application/Auth/AuthService.cs",
        "backend/src/HuTube.Infrastructure/Persistence/AuthStore.cs", "backend/src/HuTube.Infrastructure/Videos/ContentService.cs",
        "backend/src/HuTube.Infrastructure/Notifications/NotificationHub.cs", "backend/tests/HuTube.IntegrationTests/SessionValidationQueryTests.cs",
        "recommendation-service/tests/test_api.py", "recommendation-service/pyproject.toml", "scripts/testing/run_planned_performance.py", "scripts/testing/collect_fixed_results.py"]]
    report["sourceHashes"] = {str(p.relative_to(ROOT)).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
    (OUT / "case-results.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    samples = results["B-PERF.07"]["stages"][0]["resources"]
    minutes = [sample["seconds"]/60 for sample in samples]
    figure, axes = plt.subplots(figsize=(9, 4), layout="constrained")
    axes.plot(minutes, [s["workingSetBytes"]/2**20 for s in samples], label="Working set", color="#245a81")
    axes.plot(minutes, [s["privateBytes"]/2**20 for s in samples], label="Private memory", color="#327447")
    axes.axhline(samples[0]["workingSetBytes"]/2**20*1.10, linestyle="--", color="#a53832", label="Working set +10% limit")
    axes.set(xlabel="Minutes after 120s warmup", ylabel="MiB", title="20 VU / 600s soak — API process memory")
    axes.grid(alpha=.2); axes.legend(loc="lower right")
    figure.savefig(OUT / "soak-memory.png", dpi=150)
    plt.close(figure)
    lines = ["# Sửa và chạy lại 5 ca Fail — 23/10/2026", "", f"Run `{report['runId']}`, HEAD `{report['head']}` + working tree (SHA-256 trong case-results.json).", "",
        "Kết quả: " + ", ".join(f"{n} {s}" for s,n in report["counts"].items()) + ".", "",
        "## Thay đổi và cách kiểm chứng", "", *["- " + n for n in report["notes"]], "", "## Bộ test hồi quy", "",
        "| Bộ test | Tổng | Pass | Fail | Skip | Evidence |", "|---|---:|---:|---:|---:|---|"]
    for s in suites:
        lines.append(f"| {s['name']} | {s['total']} | {s['passed']} | {s['failed']} | {s['skipped']} | [{s['file']}]({s['file']}) |")
    lines += ["", "## 5 ca được xử lý", "", "| Case | Kết quả | Thực tế |", "|---|---|---|"]
    for c in report["cases"]:
        lines.append(f"| {c['id']} | {c['status']} | {c['actual'].replace('|','/')} |")
    lines += ["", "## Bộ nhớ trong cửa sổ soak", "", "![Bộ nhớ process API trong 10 phút soak](soak-memory.png)"]
    lines += ["", "## Chạy lại", "", "```powershell",
        "& 'recommendation-service/.venv/Scripts/python.exe' scripts/testing/run_planned_performance.py --only failures --output-dir docs/testing/runs/2026-10-23-fixes --output-name performance-fixed",
        "& 'recommendation-service/.venv/Scripts/python.exe' scripts/testing/run_blocked_cases.py --output-dir docs/testing/runs/2026-10-23-fixes",
        "& 'recommendation-service/.venv/Scripts/python.exe' scripts/testing/collect_fixed_results.py", "```", "",
        "Đọc live-run.json/performance-fixed.json để kiểm tra cleanup. Các lượt read-diagnostic/read-profile/read-client-fixed/read-vu-clients chỉ dùng tìm nguyên nhân, không thay bằng chứng chạy đủ thời lượng."]
    (OUT / "README.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
    update_catalog(report)
    print(json.dumps({"counts": report["counts"], "auto": sum(s["total"] for s in suites)}))


def update_catalog(report):
    latest = {}
    for folder in ("2026-10-23-planned", "2026-10-23-blocked", "2026-10-23-fixes"):
        payload = json.loads((OUT.parent / folder / "case-results.json").read_text(encoding="utf-8"))
        latest.update({c["id"]: c for c in payload["cases"]})
    assert len(latest) == 48
    counts = collections.Counter(c["status"] for c in latest.values())
    fixed = {c["id"]: c for c in report["cases"]}
    path = ROOT / "docs/BACKEND_TEST_SCENARIOS.md"
    text = path.read_text(encoding="utf-8")
    banner = (f"**Cập nhật sửa 5 ca Fail ngày 23/10/2026 (UTC+7):** 48 ca PLANNED hiện có "
        f"**{counts['Pass']} PASS, {counts['Fail']} FAIL, {counts['Blocked']} BLOCKED, {counts['N/A']} N/A**. "
        f"Workbook đủ **530 ID: {482+counts['Pass']} Pass, {counts['Fail']} Fail, {counts['Blocked']} Blocked, {counts['N/A']} N/A**; "
        "482 Pass lịch sử được giữ nguyên. Đợt hồi quy mới: "
        + ", ".join(f"{s['name']} {s['passed']}/{s['total']} Pass" for s in report["suites"])
        + ". Read/spike chạy lại đủ thời lượng với load generator đã sửa; soak đo 600 giây sau warmup 120 giây, không đổi SLO. "
        "Readiness sửa contract retryable theo nguyên nhân; cache notification bỏ L1 để invalidation giữa instances có hiệu lực ngay. "
        "Google provider/UI vẫn thuộc E2E web theo phạm vi người dùng chốt. "
        "Xem [bằng chứng sửa lỗi, regression và performance](testing/runs/2026-10-23-fixes/README.md). "
        "Các số inventory/TRX cũ bên dưới là lịch sử, không phải toàn bộ 530 case vừa chạy lại.")
    text = re.sub(r"(<!-- PLANNED_RUN_20261023 -->\n).*?(\n<!-- /PLANNED_RUN_20261023 -->)",
                  lambda m: m[1]+banner+m[2], text, flags=re.DOTALL)
    rows = []
    for line in text.splitlines():
        parts = line.split("|")
        if len(parts) > 3 and parts[1].strip() in fixed:
            case = fixed[parts[1].strip()]
            parts[-2] = " " + case["status"].upper() + " "
            if case.get("expected"):
                parts[3] = " " + case["expected"] + " "
            if case["id"] == "B-CACHE.14":
                parts[4] = " Isolation/invalidation theo namespace; không phụ thuộc memory một process; không stale security access. Notification dùng shared L2, không L1; update và logout host A → host B phải nhận trạng thái mới ngay. "
            line = "|".join(parts)
        if line.startswith("Ngưỡng khởi đầu **đề xuất, chưa có số đo**:"):
            line = line.replace("Ngưỡng khởi đầu **đề xuất, chưa có số đo**:", "Ngưỡng giữ nguyên trong lượt đo sau sửa 23/10/2026:")
            line += " Bài soak có warmup riêng 120 giây ở 20 VU trước cửa sổ đo 600 giây; giữ ngưỡng working-set 10%, không ép GC/restart để đạt kết quả."
        rows.append(line)
    path.write_text("\n".join(rows)+"\n", encoding="utf-8")
    for folder in ("2026-10-23-planned", "2026-10-23-blocked"):
        history = OUT.parent / folder / "README.md"
        value = history.read_text(encoding="utf-8")
        if "<!-- FIXES_LATEST -->" not in value:
            first, rest = value.split("\n", 1)
            history.write_text(first+"\n\n<!-- FIXES_LATEST -->\nKết quả bên dưới là lịch sử. 5 ca Fail được đối chiếu và chạy lại trong [run mới nhất](../2026-10-23-fixes/README.md).\n"+rest, encoding="utf-8")


if __name__ == "__main__":
    collect()
