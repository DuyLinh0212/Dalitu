"""Create an offline HTML view for the latest CI Playwright reports.

Usage: python coverage/export_html_report.py --latest web --latest rbac
"""
import argparse
import html
import json
import os
from pathlib import Path
from urllib.parse import quote


ROOT = Path(__file__).resolve().parents[1]
SUITE_LABELS = {"web": "Web User và Admin", "rbac": "Ma trận quyền Admin"}
STATUS_LABELS = {
    "passed": "Đạt",
    "failed": "Lỗi",
    "blocked": "Chặn",
    "reused": "Dùng lại",
}


def safe_text(value):
    return html.escape(str(value or ""), quote=True)


def screenshot_href(value, report_path):
    if not isinstance(value, str) or not value.strip():
        return None
    screenshot_path = Path(value)
    if not screenshot_path.is_absolute():
        screenshot_path = ROOT / screenshot_path
    screenshot_path = screenshot_path.resolve()
    root = ROOT.resolve()
    if not screenshot_path.is_relative_to(root) or not screenshot_path.is_file():
        return None
    relative = Path(os.path.relpath(screenshot_path, start=report_path.parent)).as_posix()
    return quote(relative, safe="/._-")


def render_case(case, report_path):
    case_id = safe_text(case.get("id", "—"))
    name = safe_text(case.get("name", "Chưa có tên ca kiểm thử"))
    application = str(case.get("application") or "").lower()
    app_label = {"user": "User", "admin": "Admin", "all": "Xuyên ứng dụng"}.get(application, "Khác")
    module = safe_text(case.get("module") or case.get("flow") or "—")
    status = str(case.get("status") or "unknown").lower()
    status_label = STATUS_LABELS.get(status, "Khác")
    duration = case.get("duration_ms")
    duration_label = f"{duration:,} ms" if isinstance(duration, (int, float)) else "—"
    href = screenshot_href(case.get("screenshot"), report_path)
    evidence = f'<a href="{safe_text(href)}">Mở ảnh</a>' if href else "—"
    search_text = safe_text(" ".join((str(case.get("id", "")), str(case.get("name", "")),
                                      app_label, str(case.get("module", "")), str(case.get("flow", "")))).lower())
    status_class = status if status in STATUS_LABELS else "other"
    return (
        f'<tr data-status="{safe_text(status)}" data-app="{safe_text(app_label)}" '
        f'data-search="{search_text}">'
        f"<td><code>{case_id}</code></td><td>{name}</td><td>{safe_text(app_label)}</td>"
        f"<td>{module}</td><td><span class=\"status {status_class}\">{safe_text(status_label)}</span></td>"
        f"<td>{safe_text(duration_label)}</td><td>{evidence}</td></tr>"
    )


def render_report(report_path, report):
    cases = report.get("cases") if isinstance(report.get("cases"), list) else []
    counts = {status: sum(1 for case in cases if case.get("status") == status)
              for status in STATUS_LABELS}
    total = len(cases)
    rows = "\n".join(render_case(case, report_path) for case in cases)
    if not rows:
        rows = '<tr><td colspan="7" class="empty">Lượt chạy chưa ghi nhận ca kiểm thử nào.</td></tr>'
    suite = str(report.get("suite") or "")
    suite_label = SUITE_LABELS.get(suite, suite or "Playwright")
    run_id = safe_text(report.get("run_id") or report_path.parent.name)
    browser = safe_text(report.get("browser_version") or "—")
    started = safe_text(report.get("started_at") or "—")
    finished = safe_text(report.get("finished_at") or "—")
    summary = "".join(
        f'<article class="metric"><strong>{counts[status]:,}</strong><span>{label}</span></article>'
        for status, label in (("passed", "Đạt"), ("failed", "Lỗi"), ("blocked", "Chặn"), ("reused", "Dùng lại"))
    )
    template = """<!doctype html>
<html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Báo cáo Playwright — HuTube</title>
<style>
:root{font-family:Segoe UI,Arial,sans-serif;color:#182230;background:#f4f7fb}body{margin:0;padding:32px}
main{max-width:1320px;margin:0 auto}.top{background:#102b46;color:#fff;padding:26px 30px;border-radius:16px}
h1{margin:0 0 10px;font-size:28px}.meta{display:flex;gap:10px 26px;flex-wrap:wrap;color:#d5e5f5;font-size:14px}
.metrics{display:grid;grid-template-columns:repeat(5,minmax(120px,1fr));gap:14px;margin:20px 0}
.metric{background:#fff;padding:18px;border:1px solid #dce5ee;border-radius:12px;display:flex;flex-direction:column;gap:4px}
.metric strong{font-size:26px}.metric span{color:#586779;font-size:14px}.controls{display:flex;gap:12px;flex-wrap:wrap;margin:18px 0}
input,select{font:inherit;padding:10px 12px;border:1px solid #bac8d6;border-radius:8px;background:#fff;color:#182230}
input{min-width:min(360px,100%)}.table-wrap{overflow:auto;background:#fff;border:1px solid #dce5ee;border-radius:12px}
table{border-collapse:collapse;width:100%;min-width:900px}th,td{text-align:left;padding:11px 12px;border-bottom:1px solid #e7edf3;vertical-align:top}
th{background:#eaf1f8;color:#31465b;position:sticky;top:0;font-size:13px}td{font-size:14px}code{font-size:12px;color:#344b62}
a{color:#075db5}.status{display:inline-block;padding:4px 9px;border-radius:999px;font-size:12px;font-weight:650}
.passed{background:#dff5e8;color:#14643a}.failed{background:#ffe3e3;color:#9c2020}.blocked{background:#fff0cd;color:#805300}
.reused{background:#e2edff;color:#254f91}.other{background:#e7e9ed;color:#475467}.empty{text-align:center;padding:30px;color:#64748b}
.foot{margin-top:14px;color:#64748b;font-size:12px}@media(max-width:700px){body{padding:14px}.top{padding:20px}.metrics{grid-template-columns:repeat(2,1fr)}}
</style></head><body><main><header class="top"><h1>Báo cáo kiểm thử Playwright</h1>
<div class="meta"><span>Phạm vi: __SUITE__</span><span>Mã lượt chạy: __RUN_ID__</span>
<span>Trình duyệt: __BROWSER__</span><span>Bắt đầu: __STARTED__</span><span>Kết thúc: __FINISHED__</span></div></header>
<section class="metrics" aria-label="Tổng hợp kết quả"><article class="metric"><strong>__TOTAL__</strong><span>Tổng ca</span></article>__SUMMARY__</section>
<section class="controls" aria-label="Lọc ca kiểm thử"><input id="search" type="search" placeholder="Tìm mã ca, tên ca hoặc nhóm…" aria-label="Tìm ca kiểm thử">
<select id="status"><option value="">Mọi trạng thái</option><option value="passed">Đạt</option><option value="failed">Lỗi</option><option value="blocked">Chặn</option><option value="reused">Dùng lại</option></select>
<select id="app"><option value="">Mọi ứng dụng</option><option value="User">User</option><option value="Admin">Admin</option><option value="Xuyên ứng dụng">Xuyên ứng dụng</option></select>
<span id="visible-count" aria-live="polite"></span></section>
<div class="table-wrap"><table><thead><tr><th>Mã ca</th><th>Tình huống</th><th>Ứng dụng</th><th>Nhóm</th><th>Kết quả</th><th>Thời gian</th><th>Minh chứng</th></tr></thead>
<tbody>__ROWS__</tbody></table></div><p class="foot">Báo cáo HTML được tạo từ kết quả JSON của lượt chạy. Ảnh minh chứng nằm trong cùng gói artifact CI.</p>
</main><script>
const rows=[...document.querySelectorAll('tbody tr[data-status]')];
const search=document.querySelector('#search'),status=document.querySelector('#status'),app=document.querySelector('#app'),count=document.querySelector('#visible-count');
function filterRows(){let visible=0;for(const row of rows){const match=(!status.value||row.dataset.status===status.value)&&(!app.value||row.dataset.app===app.value)&&(!search.value||row.dataset.search.includes(search.value.toLowerCase()));row.hidden=!match;if(match)visible++}count.textContent=`Hiển thị ${visible} / ${rows.length} ca`;}
search.addEventListener('input',filterRows);status.addEventListener('change',filterRows);app.addEventListener('change',filterRows);filterRows();
</script></body></html>"""
    replacements = {
        "__SUITE__": safe_text(suite_label),
        "__RUN_ID__": run_id,
        "__BROWSER__": browser,
        "__STARTED__": started,
        "__FINISHED__": finished,
        "__TOTAL__": f"{total:,}",
        "__SUMMARY__": summary,
        "__ROWS__": rows,
    }
    for marker, value in replacements.items():
        template = template.replace(marker, value)
    report_path.write_text(template, encoding="utf-8", newline="\n")


def export_latest(suite):
    latest_path = ROOT / "artifacts" / f"{suite}-latest.json"
    if not latest_path.is_file():
        print(f"SKIP: {suite} report pointer was not created")
        return False
    latest = json.loads(latest_path.read_text(encoding="utf-8"))
    run_dir = Path(latest["run_dir"]).resolve()
    runs_root = (ROOT / "artifacts" / "runs").resolve()
    if not run_dir.is_relative_to(runs_root):
        raise ValueError(f"{suite} report is outside the managed E2E artifacts directory")
    source = run_dir / "report.json"
    if not source.is_file():
        print(f"SKIP: {suite} report JSON was not created")
        return False
    report = json.loads(source.read_text(encoding="utf-8"))
    if report.get("suite") != suite:
        raise ValueError(f"Expected {suite} report, got {report.get('suite')!r}")
    destination = run_dir / "playwright-report.html"
    render_report(destination, report)
    print(f"Created: {destination}")
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--latest", choices=tuple(SUITE_LABELS), action="append", required=True)
    args = parser.parse_args()
    for suite in dict.fromkeys(args.latest):
        export_latest(suite)


if __name__ == "__main__":
    main()
