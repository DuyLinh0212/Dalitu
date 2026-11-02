"""Map actual live TRX executions and measurements to the eight scenario IDs."""
from __future__ import annotations

import collections
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET

from blocked_environment import OUT, ROOT


def main():
    run = json.loads((OUT / "live-run.json").read_text())
    assert "cleanup" in run
    metrics_path = OUT / "live-measurements.json"
    metrics = json.loads(metrics_path.read_text()) if metrics_path.exists() else {}
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    tree = ET.parse(OUT / "live-current.trx")
    cases, executions = [], []
    descriptions = {
        "B-MEDIA.03": "Production R2ObjectStorageService chạy mạng thật: bytes/SHA-256, signed URL, hết hạn 403, xóa rồi 404; prefix test riêng.",
        "B-MEDIA.04": "Theo phạm vi người dùng chốt: Google provider/UI chuyển E2E web. Backend nhận verified identity; Pickup .eml/link thật, session/JWT dùng được ở host khác, link tài khoản, retry không trùng user/session, invalid identity và origin sai bị chặn. Không tuyên bố đã chạy Google OAuth thật.",
        "B-MEDIA.05": "Kestrel + PostgreSQL → export CSV → CF Python thật → R2 thật → recommendation/feed; dataset biết trước và loại seen. Webhook SePay tổng hợp ký HMAC qua HTTP, không chuyển tiền; chữ ký sai/token thiếu bị chặn.",
        "B-CACHE.14": "Hai Kestrel/DI/L1 độc lập, chung DB và Redis thật: đọc chung L2, cách ly user, cập nhật/invalidation và logout ở host A → đọc host B; ghi riêng trạng thái ngay lập tức/sau TTL.",
        "B-PYCF.11": "CF train từ R2 thật; model/version/hash từ dataset biết trước. CSV thiếu và CSV tải dở/hash mismatch không publish, active model cũ giữ nguyên.",
        "B-PERF.01": "1 VU cold/warm cache bật/tắt trên cùng DB/user/dataset; cache thực tế là notifications. Ghi p50/p95/p99, bytes và SQL bằng DbCommandInterceptor chỉ đếm request, assert response hash không đổi.",
        "B-PERF.09": "Matrix preview/export, model job thật: đo acceptance riêng completion, lấy progress/working set; simulation cancel theo cùng ID, không tự tạo job mới.",
        "B-PERF.10": "Dưới 3 reader đồng thời: socket Redis/CF timeout thật, storage connection refused và PostgreSQL lock timeout có kiểm soát; assert fallback, giới hạn thời gian/số request và phục hồi.",
    }
    for test in tree.findall("t:Results/t:UnitTestResult", ns):
        name = test.attrib["testName"]
        matching = [case_id for case_id in descriptions if case_id.replace("-", "_").replace(".", "_") + "_" in name]
        if not matching:
            continue
        case_id = matching[0]
        status = "Pass" if test.attrib["outcome"] == "Passed" else "Fail" if test.attrib["outcome"] == "Failed" else "Blocked"
        error = test.find("t:Output/t:ErrorInfo/t:Message", ns)
        actual = descriptions[case_id]
        details = metrics.get(case_id, {}).get("details", {})
        if case_id == "B-CACHE.14" and details:
            actual += (f" Invalidation ngay: {details['immediateInvalidation']}; sau 5,5 giây: {details['afterLocalTtl']}; "
                       f"logout → host B HTTP {details['afterLogout']}. Lỗi là notification còn cũ trong TTL L1, không phải token logout được chấp nhận.")
        elif case_id == "B-PERF.01" and details:
            stages = {s["label"]: s for s in details["stages"]}
            on, off = stages["on-warm"], stages["off-warm"]
            actual += (f" 30 request warm: cache on {on['sqlQueries']} SQL, p95 {on['p95ms']:.2f} ms; "
                       f"cache off {off['sqlQueries']} SQL, p95 {off['p95ms']:.2f} ms; bytes/hash bằng nhau.")
        elif case_id == "B-PERF.09" and details:
            actual += (f" Acceptance {details['acceptanceMs']:.1f} ms; completion {details['completionMs']/1000:.2f} giây; "
                       f"{len(details['samples'])} mẫu progress/working set của process test chứa 3 Kestrel; cancel={details['cancelStatus']}.")
        elif case_id == "B-PERF.10" and details:
            actual += " " + "; ".join(f"{m['dependency']}: {m['ms']/1000:.2f}s" for m in details["measurements"])
            actual += (f". Sau cooldown, Redis có {details['redisRecoveredCalls']} outbound request, CF có {details['cfRecoveredCalls']}; "
                       "API phục hồi HTTP 200.")
        elif case_id == "B-MEDIA.03" and details:
            actual += f" {details['bytes']} bytes; signed GET/expired/deleted HTTP {details['immediate']}/{details['expired']}/{details['deleted']}."
        if error is not None:
            actual += " Không đạt: " + error.text[:1800]
        evidence = ["live-current.trx", "live-run.json"]
        if case_id in metrics:
            evidence.append("live-measurements.json")
        case = {"id": case_id, "status": status, "actual": actual,
            "endedAt": test.attrib["endTime"], "tests": [name], "evidence": evidence,
            "measurements": metrics.get(case_id)}
        cases.append(case)
        executions.append({"name": name, "status": status, "duration": test.attrib.get("duration"),
            "endedAt": test.attrib["endTime"], "layer": "Live Kestrel/PostgreSQL/R2/Redis/CF",
            "file": "live-current.trx", "actual": actual})
    assert len(cases) == 8 and len({c["id"] for c in cases}) == 8
    counters = tree.find("t:ResultSummary/t:Counters", ns).attrib
    suite = {"name": "8 formerly blocked scenarios – live integration", "file": "live-current.trx",
        "total": int(counters["total"]), "passed": int(counters["passed"]), "failed": int(counters["failed"]),
        "skipped": int(counters["notExecuted"]), "endedAt": run["endedAt"]}
    report = {"runId": run["runId"].upper(), "updatedAt": run["endedAt"], "cases": cases, "executions": executions,
        "suites": [suite], "counts": dict(collections.Counter(c["status"] for c in cases)),
        "head": subprocess.check_output(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, text=True).strip(),
        "notes": [run["scopeChange"], "R2 bucket creation returned 403; every fixed CF key was rebased below a newly owned test prefix using real S3 requests. No production active manifest or existing CSV cleanup is allowed.",
            "Tests use real Redis with unique cache prefix and newly created session IDs. No FLUSHDB/FLUSHALL.",
            "attempt-01/02 preserve earlier evidence. Their B-PERF.01 assertion used an incomplete SQL counter: HybridCache suppresses HTTP ExecutionContext. The final counter includes notification SQL and verifies cache-off flags; only live-current.trx is the final result.",
            "Latency is a local development diagnostic, not production capacity.", run["cleanup"]]}
    paths = list((ROOT / "scripts/testing").glob("*blocked*.py")) + [ROOT / "scripts/testing/live_cf_bootstrap.py",
        ROOT / "backend/tests/HuTube.IntegrationTests/LivePlannedFixture.cs", ROOT / "backend/tests/HuTube.IntegrationTests/LivePlannedTests.cs"]
    report["sourceHashes"] = {str(p.relative_to(ROOT)).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}
    (OUT / "case-results.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# Chạy lại 8 ca từng BLOCKED — 23/10/2026", "", f"Run `{report['runId']}`; HEAD `{report['head']}` + working tree có SHA-256 trong case-results.json.", "",
        "Kết quả: " + ", ".join(f"{n} {status}" for status, n in report["counts"].items()) + ".", "",
        "## Phạm vi và môi trường", "", *["- " + n for n in report["notes"]], "",
        "## Kết quả theo ID", "", "| Case | Kết quả | Thực tế |", "|---|---|---|"]
    for case in sorted(cases, key=lambda c: c["id"]):
        lines.append(f"| {case['id']} | {case['status']} | {case['actual'].replace('|', '/').replace(chr(10), ' ')} |")
    lines += ["", "## Chạy lại", "", "```powershell", "& 'recommendation-service/.venv/Scripts/python.exe' scripts/testing/run_blocked_cases.py", "```", "",
        "Cần PostgreSQL test 127.0.0.1:55441 (user hutube_test), cấu hình R2/Redis trong các file môi trường đang có; runner không in credentials. Chỉ các test opt-in LivePlannedTests được chạy. Google xác thực thật không nằm trong phạm vi backend sau điều chỉnh của người dùng.", "",
        "Bộ 440 execution trước nằm ở ../2026-10-23-planned; không coi 8 execution mới là chạy lại toàn bộ 530 ID."]
    (OUT / "README.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
    print(json.dumps(report["counts"]))


if __name__ == "__main__":
    main()
