"""Collect actual runner output into an ID-level traceability report and workbook payload."""
from __future__ import annotations

import collections
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs/testing/runs/2026-10-23-planned"


def load(name):
    return json.loads((OUT / name).read_text(encoding="utf-8-sig"))


def collect():
    executions, suites = [], []
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    for filename, layer in [("unit-current.trx", "Unit .NET"), ("integration-current.trx", "Integration PostgreSQL/FFmpeg")]:
        tree = ET.parse(OUT / filename).getroot()
        counters = tree.find("t:ResultSummary/t:Counters", ns).attrib
        times = tree.find("t:Times", ns).attrib
        suites.append({"name": layer, "file": filename, "total": int(counters["total"]),
                       "passed": int(counters["passed"]), "failed": int(counters["failed"]),
                       "skipped": int(counters["notExecuted"]), "startedAt": times["start"], "endedAt": times["finish"]})
        for test in tree.findall("t:Results/t:UnitTestResult", ns):
            executions.append({"name": test.attrib["testName"], "status": "Pass" if test.attrib["outcome"] == "Passed" else "Fail",
                "duration": test.attrib.get("duration", ""), "endedAt": test.attrib.get("endTime", times["finish"]),
                "layer": layer, "file": filename, "actual": "Các assertion trong method đã đạt." if test.attrib["outcome"] == "Passed" else " ".join(test.itertext())[:2000]})
    tree = ET.parse(OUT / "python-current.xml").getroot()
    py = tree.find("testsuite") if tree.tag == "testsuites" else tree
    suites.append({"name": "Python recommendation", "file": "python-current.xml", "total": int(py.attrib["tests"]),
                   "passed": int(py.attrib["tests"])-int(py.attrib["failures"])-int(py.attrib["errors"])-int(py.attrib["skipped"]),
                   "failed": int(py.attrib["failures"])+int(py.attrib["errors"]), "skipped": int(py.attrib["skipped"]),
                   "startedAt": py.attrib["timestamp"], "endedAt": datetime.fromtimestamp((OUT/"python-current.xml").stat().st_mtime, timezone.utc).isoformat()})
    for test in py.iter("testcase"):
        status = "Fail" if test.find("failure") is not None or test.find("error") is not None else "Skipped" if test.find("skipped") is not None else "Pass"
        properties = {p.attrib["name"]: p.attrib["value"] for p in test.findall("properties/property")}
        executions.append({"name": test.attrib.get("classname", "")+"."+test.attrib["name"], "status": status,
            "duration": test.attrib.get("time", ""), "endedAt": suites[-1]["endedAt"], "layer": "Python recommendation",
            "file": "python-current.xml", "actual": json.dumps(properties, ensure_ascii=False) if properties else "Các assertion Python đã đạt."})
    results = {}
    now = datetime.now(timezone.utc).isoformat()
    def tested(case_id, patterns, actual):
        tests = [t for t in executions if any(p in t["name"] for p in patterns)]
        if not tests: raise ValueError(f"No actual execution evidence for {case_id}: {patterns}")
        results[case_id] = {"id": case_id, "status": "Pass" if all(t["status"] == "Pass" for t in tests) else "Fail",
            "actual": actual, "tests": [t["name"] for t in tests], "evidence": sorted({t["file"] for t in tests}),
            "endedAt": max(t["endedAt"] for t in tests)}
    for n in [1,2,3,4,5,6,7,8,12]:
        case_id = f"B-CACHE.{n:02}"
        results[case_id] = {"id": case_id, "status": "N/A", "endedAt": now, "tests": [],
            "evidence": ["backend/src/HuTube.Api/Program.cs", "backend/src/HuTube.Infrastructure/Notifications/NotificationHub.cs",
                         "backend/src/HuTube.Infrastructure/Authentication/RedisSessionPlayerPreferencesStore.cs"],
            "actual": "Đối chiếu source hiện tại: không có Redis cache cho profile/feed/detail hoặc Lua auth/session validation/touch như ca gốc. HybridCache chỉ cache trang notifications đầu; Redis còn lưu player preferences và data protection. Ca theo cơ chế cũ không áp dụng; không dùng test cache transport để báo PASS cho cache API không tồn tại. Các security/ownership API hiện tại vẫn được chạy trong integration-current.trx."}
    for n in (9,10,11,13):
        tested(f"B-CACHE.{n:02}", [f"B_CACHE_{n:02}_"],
               "Kiểm tra REST adapter bằng HttpMessageHandler có kiểm soát: dữ liệu hỏng/lỗi HTTP/cooldown/cancellation/TTL theo từng bộ tham số. Không coi đây là kiểm chứng Redis mạng thật.")
    mappings = {
        "B-PYAPI.01": (["test_B_PYAPI_01_04", "test_health_serves_while_initial_model"], "Health 200 khi model loaded/unloaded; ready 200/503 đúng trạng thái, HTTP không chờ download model."),
        "B-PYAPI.02": (["test_ready_distinguishes_missing_token"], "Thiếu service token thực tế trả retryable=false; bảng gốc yêu cầu retryable=true. Đây là khác biệt contract, không đổi expected để báo xanh."),
        "B-PYAPI.03": (["test_B_PYAPI_03", "test_internal_api_requires_token", "test_unknown_user", "test_no_model"], "Kiểm tra missing/wrong service token, known/unknown user, missing model, limit 0/1/100/101, exclusions/ranking/version."),
        "B-PYAPI.04": (["test_B_PYAPI_01_04", "test_B_PYAPI_04_06", "test_model_status_reports_missing_settings"], "Model status với admin token hợp lệ/sai/thiếu; response không lộ token/config secret."),
        "B-PYAPI.05": (["test_B_PYAPI_05", "test_r2_training_restart_and_failed_update", "test_job_creation_excludes_other_replica"], "Train HTTP 202, replay 200, payload conflict 409, schema 422; registry lock across replicas; failed update giữ active model. Storage FakeR2 được khai báo."),
        "B-PYAPI.06": (["test_B_PYAPI_06", "test_B_PYAPI_04_06", "test_r2_training_restart_and_failed_update"], "Job status/auth/missing/traversal và persisted completed job sau registry restart. FakeR2 cho persistence, không phải R2 live."),
        "B-PYCF.01": (["test_B_PYCF_01", "test_empty_matrix_is_rejected"], "Empty/single-row/duplicate/missing ID/missing column bị từ chối trước khi publish artifact."),
        "B-PYCF.02": (["test_B_PYCF_02", "test_weighted_score_configuration"], "Scores NaN/Inf/overflow/ngoài biên và weights âm/zero denominator bị chặn; weighted config hợp lệ được lưu."),
        "B-PYCF.03": (["test_B_PYCF_03", "test_pearson_uses", "test_jaccard_uses"], "Cosine/Jaccard/Pearson: identical/orthogonal/all-zero, symmetric, finite và không NaN."),
        "B-PYCF.04": (["test_B_PYAPI_03_PYCF_04", "test_B_PYCF_04_05", "test_item_cf_predicts", "test_user_cf_recommends"], "HTTP limit 0/1/100/101; neighbor 0/1/100; ranking loại seen và không vượt catalog."),
        "B-PYCF.05": (["test_B_PYCF_04_05", "test_unknown_user", "test_direct_feed_resolves_fallback"], "Known cold user/no-neighbor không lỗi, candidates hợp lệ; unknown user 404; fallback khi model/CF disabled/seen hết."),
        "B-PYCF.06": (["test_B_PYCF_06", "test_temporal_split"], "Latest canary chỉ thuộc test split; train/test pairs rời nhau, tổng rows khớp. Source hiện không có offline quality score; không tuyên bố đo quality production."),
        "B-PYCF.07": (["test_B_PYCF_07", "test_selected_batch", "test_weighted_score_configuration"], "Lặp training cùng dữ liệu: similarity/ranking bằng nhau; hash/config/modelAlgorithm/aggregation lưu đúng."),
        "B-PYCF.09": (["test_B_PYCF_09", "test_artifact_round_trip", "test_r2_training_restart_and_failed_update"], "Roundtrip artifact; missing/corrupt/truncated không load; update fail không đổi manifest/active model cũ."),
        "B-PYCF.10": (["test_B_PYAPI_03_PYCF_10", "test_B_PYAPI_04_06", "test_model_admin_routes_require", "test_r2_training_restart_and_failed_update"], "Public/protected routes kiểm tra token thiếu/sai/đúng; training/status không nhận service token thay admin token."),
        "B-PYCF.12": (["test_B_PYCF_12", "test_html_report", "test_cf_comparison_report", "test_comparison_report"], "CSV Unicode/quote/newline roundtrip; mapping/hash/version/interactionsCount đúng; reports HTML/metrics đã assert."),
        "B-PYCF.13": (["test_B_PYCF_13"], "Train 1k/10k/100k unique interactions với catalog bounded. Ghi elapsed, artifact bytes, model-array bytes, process peak working set vào JUnit; finite similarities và budget arrays <160 MiB. Thuật toán hiện lưu dense matrix; không gọi nó là sparse-storage implementation.")
    }
    for case_id, (patterns, actual) in mappings.items(): tested(case_id, patterns, actual)
    results["B-PYAPI.02"]["status"] = "Fail"
    tested("B-PYCF.08", ["test_B_PYCF_08", "test_quality_gate"], "Runtime gate finite/nonnegative có test; source hiện chỉ có runtime/structural gate, không có ngưỡng recommendation quality/baseline regression trong ca cũ.")
    results["B-PYCF.08"]["status"] = "N/A"
    for result in load("sandboxes.json"):
        result["evidence"] = ["sandboxes.json"]
        result.setdefault("tests", [])
        results[result["id"]] = result
    performance = load("performance.json")
    if "cleanup" not in performance: raise RuntimeError("Performance/soak run is still active")
    for result in performance["cases"]:
        result["evidence"] = ["performance.json"]
        result["tests"] = []
        results[result["id"]] = result
    corrected = load("performance-post-auth.json")
    if "cleanup" not in corrected: raise RuntimeError("Corrected post-auth performance/soak run is still active")
    for result in corrected["cases"]:
        if any(s["p99_ms"] >= 1500 for s in result.get("stages", [])):
            result["status"] = "Fail"
        result["evidence"] = ["performance-post-auth.json"]
        result["tests"] = []
        results[result["id"]] = result
    for name in ("media-live.json", "media-recovery.json"):
        data = load(name)
        if "cleanup" not in data: raise RuntimeError(f"{name} is still active")
        for result in data["cases"]:
            result["evidence"] = [name, "integration-current.trx"]
            pattern = "B_MEDIA_02_" if result["id"] == "B-MEDIA.02" else "B_MEDIA_01_"
            result["tests"] = [t["name"] for t in executions if pattern in t["name"]]
            results[result["id"]] = result
    supplement = load("write-auth-supplement.json")
    for result in supplement["cases"]:
        base = results[result["id"]]
        result["status"] = "Fail" if base["status"] == "Fail" or result["status"] == "Fail" else "Pass"
        result["actual"] = base["actual"] + " " + result["actual"]
        result["evidence"] = ["performance.json", "write-auth-supplement.json", "integration-current.trx"]
        result["tests"] = []
        results[result["id"]] = result
    soak = results["B-PERF.07"]
    samples = soak["stages"][0]["resources"]
    assert len(samples) >= 100 and soak["stages"][0]["seconds"] >= 600
    assert len(results) == 48, len(results)
    for case_id in ["B-PERF.02", "B-PERF.05", "B-PERF.06"]:
        c = results[case_id]
        measurements = "; ".join(f"{s['vus']} VU: p95={s['p95_ms']:.1f} ms, p99={s['p99_ms']:.1f} ms, HTTP={s['statuses']}" for s in c["stages"])
        c["actualRaw"] = c["actual"]
        c["actual"] = {"B-PERF.02": "Đọc feed/search/detail/library ở 20/50/100 VU, mỗi mức >=120 giây. ",
                       "B-PERF.05": "Kiểm tra 61/1.000/10.000 bản ghi video/payment/subscription; total và export JSON khớp database. ",
                       "B-PERF.06": "Tăng tải 10→100→10 VU và theo dõi phục hồi; 429 được phân biệt với 5xx. "}[case_id]+measurements
    if results["B-MEDIA.01"]["status"] == "Pass":
        results["B-MEDIA.01"]["actual"] = "Nguồn 99.153.900 bytes, 60,33 giây, 720p; upload một video rồi ba video đồng thời qua Kestrel thật. SHA-256 nguồn lưu khớp; ffprobe từng rendition hợp lệ; HTTP Range 206 và bytes khớp. Thời gian sẵn sàng lần lượt khoảng 28 và 78 giây."
        results["B-PERF.08"]["actual"] = results["B-MEDIA.01"]["actual"]
    if results["B-MEDIA.02"]["status"] == "Pass":
        results["B-MEDIA.02"]["actual"] = "Dừng đúng process tree Kestrel sau chunk đã commit; restart giữ uploadId, cắt tail chưa commit và khớp SHA-256 99 MB. Complete/replay và restart lần hai chỉ một video, không orphan source. Cancel trả 204 rồi lookup 404. Test riêng xác nhận malformed media và lỗi ghi do file sharing violation không tiến checkpoint; không lấp đầy ổ đĩa thật."
    soak["actual"] += f" p95={soak['stages'][0]['p95_ms']:.1f} ms; p99={soak['stages'][0]['p99_ms']:.1f} ms; HTTP={soak['stages'][0]['statuses']}."
    report = {"runId": "PLANNED-20261023", "updatedAt": now, "head": subprocess.check_output(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, text=True).strip(),
              "counts": dict(collections.Counter(r["status"] for r in results.values())), "suites": suites,
              "cases": list(results.values()), "executions": executions,
              "notes": ["482 dòng PASS lịch sử được giữ nguyên; 48 dòng PLANNED được đối chiếu riêng.",
                        "Kestrel local Development + Release build; các workload bổ sung/media có lúc chạy cùng load chính trên máy phát triển. Số đo dùng để phát hiện vấn đề, không suy ra capacity production.",
                        "B-PERF.05/.06/.07 trong performance.json ban đầu dùng token cũ sau login; không tính các 401 đó là lỗi backend. Các ca này đã chạy lại với fixture/token hợp lệ trong performance-post-auth.json; dùng kết quả chạy lại cho workbook.",
                        "Không dùng production credentials; không gửi email hoặc thông báo ra bên ngoài."]}
    source_files = [*ROOT.glob("backend/tests/**/Planned*.cs"),
                    ROOT/"backend/tests/HuTube.IntegrationTests/RealMediaPlannedTests.cs",
                    ROOT/"recommendation-service/tests/test_planned_contracts.py", *ROOT.glob("scripts/testing/*.py"),
                    ROOT/"scripts/testing/monitor_planned_soak.ps1"]
    report["sourceHashes"] = {str(p.relative_to(ROOT)).replace("\\", "/"): hashlib.sha256(p.read_bytes()).hexdigest() for p in source_files}
    (OUT / "case-results.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# Kết quả các ca backend PLANNED — 23/10/2026", "",
             f"Run ID: `{report['runId']}`. Source HEAD: `{report['head']}` và test/scripts mới trong working tree (SHA-256 trong case-results.json).",
             "", "48 dòng của workbook được đối chiếu riêng: " + ", ".join(f"{v} {k}" for k,v in report["counts"].items()) + ".",
             "482 PASS lịch sử được giữ nguyên. Không coi 440 execution tự động là 530 case vừa chạy.", "",
             "| Bộ test hiện tại | Tổng | Pass | Fail | Skip | Bằng chứng |", "| --- | ---: | ---: | ---: | ---: | --- |"]
    for s in suites:
        lines.append(f"| {s['name']} | {s['total']} | {s['passed']} | {s['failed']} | {s['skipped']} | [{s['file']}]({s['file']}) |")
    lines += ["", "## Điều kiện chạy và giới hạn", "", *[f"- {n}" for n in report["notes"]],
              "- PostgreSQL 18 trên 127.0.0.1:55441, database `hutube_planned_<guid>` và `hutube_test_<guid>` tách biệt. Test factory migrate schema thật.",
              "- Kestrel .NET Release, Development để dùng local storage/Pickup; API 55141 (load) và 55143 (media). Redis/CF remote tắt ở runtime load.",
              "- Profile read chạy 20/50/100 VU, mỗi mức >=120 giây; spike 10→100→10 VU; soak 20 VU >=600 giây. Có ghi status riêng cho 429/5xx/transport errors.",
              "- Media thật: nguồn 99.153.900 bytes, 60,33 giây, 720p; một upload rồi ba upload đồng thời; SHA-256, ffprobe từng output, HTTP Range và bytes đều được assert.",
              "- Recovery: kill đúng process Kestrel của run, restart cùng database/checkpoint; tail chưa commit bị truncate, complete/replay đúng một video; receipt tồn tại sau restart lần hai. Lỗi ghi được tạo bằng file sharing violation, không lấp đầy ổ đĩa.",
              "- Redis adapter tests dùng transport có kiểm soát. Không dùng chúng để khẳng định Redis mạng thật/hai Kestrel instances đã được kiểm chứng.",
              "- Python CF sử dụng dense matrices với catalog bounded; 1k/10k/100k interactions có elapsed, artifact/model bytes và process peak working set trong JUnit.",
              "", "## Các ca không đạt", "", "| ID | Actual | Bằng chứng |", "| --- | --- | --- |"]
    for c in results.values():
        if c["status"] != "Fail": continue
        stages = c.get("stages", [])
        metrics = "; ".join(f"{s.get('vus',1)} VU: p95={s.get('p95_ms','n/a')}ms, p99={s.get('p99_ms','n/a')}ms, status={s.get('statuses',{})}" for s in stages)
        actual = (c["actual"] + " " + metrics).replace("|", "/")
        lines.append(f"| {c['id']} | {actual} | {', '.join(c['evidence'])} |")
    lines += ["", "## Mapping toàn bộ 48 ca", "", "| ID | Kết quả | Actual / giới hạn | Evidence |", "| --- | --- | --- | --- |"]
    for c in sorted(results.values(), key=lambda r:r["id"]):
        actual = (c["actual"] + (" Còn thiếu: " + c["remaining"] if c.get("remaining") else "")).replace("|", "/").replace("\n", " ")
        lines.append(f"| {c['id']} | {c['status']} | {actual} | {', '.join(c['evidence'])} |")
    lines += ["", "## Chạy lại", "", "Từ repository root, dùng PostgreSQL test riêng và env `TEST_DATABASE_CONNECTION`. Không dùng database sản phẩm.",
              "", "```powershell", "dotnet test backend/tests/HuTube.UnitTests -c Release --logger 'trx;LogFileName=unit-current.trx' --results-directory docs/testing/runs/2026-10-23-planned",
              "dotnet test backend/tests/HuTube.IntegrationTests -c Release --logger 'trx;LogFileName=integration-current.trx' --results-directory docs/testing/runs/2026-10-23-planned",
              "recommendation-service/.venv/Scripts/python.exe -m pytest recommendation-service/tests -q -o junit_family=xunit1 --junitxml=docs/testing/runs/2026-10-23-planned/python-current.xml",
              "recommendation-service/.venv/Scripts/python.exe scripts/testing/run_planned_performance.py",
              "recommendation-service/.venv/Scripts/python.exe scripts/testing/run_planned_performance.py --only media --port 55143 --output-name media-live",
              "recommendation-service/.venv/Scripts/python.exe scripts/testing/run_planned_performance.py --only recovery --port 55143 --output-name media-recovery", "```",
              "", "`run_planned_write_auth.py` và `monitor_planned_soak.ps1` chạy trong khi process load còn hoạt động, sau khi performance.json đã có database/run ID. Các script chỉ nhận database có prefix riêng của run.",
              "", "Chuẩn bị nguồn media tái lập (không tải video bên ngoài):", "", "```powershell",
              "ffmpeg -v error -y -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=440 -t 65 -c:v libx264 -preset ultrafast -threads 1 -b:v 13M -minrate 13M -maxrate 13M -bufsize 26M -x264-params nal-hrd=cbr -c:a aac -fs 99000000 -movflags +faststart .work/planned-20261023/source-99mb.mp4", "```", "",
              "Sandbox checks chỉ đọc các biến TEST_R2_* / TEST_GOOGLE_* / TEST_UPSTASH_* được nêu trong sandboxes.json; không đọc secret từ appsettings sản phẩm."]
    (OUT / "README.md").write_text("\n".join(lines)+"\n", encoding="utf-8")
    print(json.dumps({"counts": report["counts"], "suites": suites}, ensure_ascii=False))


if __name__ == "__main__":
    collect()
