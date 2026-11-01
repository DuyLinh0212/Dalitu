"""Real browser registration/login flows. Runtime config comes from run_local.py."""
import argparse
import html
import json
import os
import re
import sys
import time
from email import policy
from email.parser import BytesParser
from email.utils import getaddresses
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from playwright.sync_api import sync_playwright, expect
from support.local_db import LocalDb
from support.state import Checkpoint, atomic_json, now
from support.evidence import auth_relative_path, auth_scope, run_date_folder


def email_link(mail, recipient, origin, timeout_ms):
    deadline = time.monotonic() + timeout_ms / 1000
    while time.monotonic() < deadline:
        for path in sorted(mail.glob("*.eml"), key=lambda p: p.stat().st_mtime, reverse=True):
            message = BytesParser(policy=policy.default).parsebytes(path.read_bytes())
            if recipient.lower() not in {email.lower() for _, email in getaddresses(message.get_all("to", []))}:
                continue
            part = message.get_body(preferencelist=("plain", "html"))
            body = part.get_content() if part else ""
            match = re.search(re.escape(origin) + r"/verify-email\?token=[^\s<>\"]+", body)
            if match:
                return match.group(0)
        time.sleep(0.2)
    raise TimeoutError("No verification email for the checkpoint account in the local Pickup folder.")


def email_otp(mail, recipient, not_before, timeout_ms):
    deadline = time.monotonic() + timeout_ms / 1000
    while time.monotonic() < deadline:
        for path in sorted(mail.glob("*.eml"), key=lambda p: p.stat().st_mtime, reverse=True):
            if path.stat().st_mtime < not_before:
                continue
            message = BytesParser(policy=policy.default).parsebytes(path.read_bytes())
            if recipient.lower() not in {email.lower() for _, email in getaddresses(message.get_all("to", []))}:
                continue
            part = message.get_body(preferencelist=("plain", "html"))
            body = html.unescape(part.get_content() if part else "")
            match = re.search(r"(?m)^Mã OTP: (?P<code>\d{8})\s*$", body)
            if match:
                assert "/reset-password?token=" not in body, "Password reset email must not contain a token link."
                return match.group("code")
        time.sleep(0.2)
    raise TimeoutError("No password-reset OTP for the checkpoint account in the local Pickup folder.")


def run(config):
    run_dir = Path(config["run_dir"])
    screenshots = ROOT / "screenshots" / run_date_folder(run_dir.name); screenshots.mkdir(parents=True, exist_ok=True)
    state = Path(config["state_dir"])
    checkpoint = Checkpoint(state / "checkpoint.json", {
        "api_url": config["api_url"], "user_url": config["user_url"], "admin_url": config["admin_url"],
        "database": "hutube_e2e_runner", "pg_port": config["pg_port"]
    }, config["account"])
    account = checkpoint.account
    db = LocalDb(config)
    cases, errors, requests = [], [], []
    active_page = None
    active_case_id = None
    screenshot_entries = []
    registered_posts = 0
    login_posts = 0
    started = time.monotonic()
    user_trace_stopped = False
    original_role = checkpoint.data.get("fixture_original_role")
    if original_role:
        db.restore_role(checkpoint.data["user_id"], original_role)
        checkpoint.data.pop("fixture_original_role", None); checkpoint.save()

    def execute(case_id, name, action):
        nonlocal active_case_id
        active_case_id = case_id
        begin = time.monotonic()
        entry = {"id": case_id, "name": name, "started_at": now()}
        cases.append(entry)
        try:
            status = action() or "passed"
            entry.update(status=status, duration_ms=round((time.monotonic() - begin) * 1000))
            checkpoint.record(case_id, str(run_dir), status)
            print(f"{status.upper()}: {case_id} {name}", flush=True)
        except Exception as exc:
            entry.update(status="failed", duration_ms=round((time.monotonic() - begin) * 1000), error=str(exc)[:1000])
            if active_page and not active_page.is_closed():
                shot(active_page, case_id + "-failure")
            raise

    def shot(page, name):
        relative = auth_relative_path(active_case_id, name + ".png")
        destination = screenshots / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        page.screenshot(path=str(destination), full_page=True,
                        animations="disabled", mask=[page.locator("input[type=password]"), page.locator("#resetCode")])
        application, module, flow = auth_scope(active_case_id)
        screenshot_entries.append({"application": application, "module": module, "flow": flow,
                                   "case_id": active_case_id, "step": name,
                                   "viewport": page.viewport_size, "path": relative.as_posix()})

    def observe(page):
        nonlocal registered_posts, login_posts
        def request(request):
            nonlocal registered_posts, login_posts
            pathname = urlsplit(request.url).path
            if request.method == "POST" and pathname == "/api/v1/auth/register":
                registered_posts += 1
            if request.method == "POST" and pathname == "/api/v1/auth/login":
                login_posts += 1
        page.on("request", request)
        page.on("pageerror", lambda error: errors.append(str(error)))
        # Never persist query strings, request bodies, Authorization or cookie values.
        page.on("response", lambda response: requests.append({"path": urlsplit(response.url).path, "status": response.status}))

    def go(page, origin, route):
        page.goto(origin + route)
        # Realtime connections can keep network active; auth pages are finite requests.
        if route.startswith(("/login", "/register", "/verify-email")):
            page.wait_for_load_state("networkidle", timeout=config["timeouts_ms"]["navigation"])
        else:
            page.wait_for_load_state("domcontentloaded")

    def register_fields(page, confirm=None):
        page.locator("#displayName").fill(account["display_name"])
        page.locator("#username").fill(account["username"])
        page.locator("#email").fill(account["email"])
        page.locator("#password").fill(account["password"])
        page.locator("#confirmPassword").fill(confirm or account["password"])

    def login(page, origin, password, expected_status):
        go(page, origin, "/login")
        page.locator("#email").fill(account["email"])
        page.locator("#password").fill(password)
        with page.expect_response(lambda r: r.request.method == "POST" and r.url == config["api_url"] + "/auth/login", timeout=config["timeouts_ms"]["api"]) as pending:
            page.locator("button[type=submit]").click()
        response = pending.value
        assert response.status == expected_status, f"Login returned {response.status}, expected {expected_status}"
        return response.json()

    try:
        with sync_playwright() as pw:
            browser = pw.chromium.launch(headless=True, executable_path=os.environ.get("CHROME_BIN"))
            user = browser.new_context(viewport={"width": 1440, "height": 960}, locale="vi-VN")
            admin = browser.new_context(viewport={"width": 1440, "height": 960}, locale="vi-VN")
            user.tracing.start(screenshots=True, snapshots=True, sources=True)
            admin.tracing.start(screenshots=True, snapshots=True, sources=True)
            page = active_page = user.new_page(); other = admin.new_page()
            for target in (page, other):
                target.set_default_timeout(config["timeouts_ms"]["action"])
                target.set_default_navigation_timeout(config["timeouts_ms"]["navigation"])
                observe(target)
            try:
                def empty_register():
                    go(page, config["user_url"], "/register")
                    before = registered_posts
                    page.locator("button[type=submit]").click()
                    expect(page.locator("#username")).to_have_attribute("aria-invalid", "true")
                    assert registered_posts == before
                    shot(page, "01-register-required-fields")
                execute("AUTH-REG-01", "Form đăng ký trống không gọi API", empty_register)

                def mismatch():
                    register_fields(page, account["password"] + "Different")
                    before = registered_posts
                    page.locator("button[type=submit]").click()
                    expect(page.get_by_role("alert")).to_contain_text("chưa khớp")
                    assert registered_posts == before
                    shot(page, "02-register-password-mismatch")
                execute("AUTH-REG-02", "Mật khẩu xác nhận không khớp", mismatch)

                def registration():
                    go(page, config["user_url"], "/register"); register_fields(page)
                    shot(page, "03-register-filled-desktop")
                    page.set_viewport_size({"width": 390, "height": 844}); shot(page, "04-register-filled-mobile")
                    page.set_viewport_size({"width": 1440, "height": 960})
                    existing = db.account(account["email"])
                    if existing:
                        checkpoint.data["user_id"] = existing["user_id"]; checkpoint.save()
                        return "reused"
                    with page.expect_response(lambda r: r.request.method == "POST" and r.url == config["api_url"] + "/auth/register", timeout=config["timeouts_ms"]["api"]) as pending:
                        page.locator("button[type=submit]").click()
                    assert pending.value.status == 201
                    existing = db.account(account["email"])
                    assert existing and existing["status"] == "pending" and not existing["verified"]
                    checkpoint.data["user_id"] = existing["user_id"]; checkpoint.save()
                    expect(page.get_by_role("status")).to_contain_text("Tài khoản đã được tạo")
                    shot(page, "05-register-success")
                execute("AUTH-REG-03", "Tạo hoặc dùng lại đúng một tài khoản có checkpoint", registration)

                def unverified():
                    if db.account(account["email"])["verified"]:
                        return "reused"
                    response = login(page, config["user_url"], account["password"], 403)
                    assert response["code"] in ("EMAIL_NOT_VERIFIED", "EMAIL_UNVERIFIED")
                    expect(page.get_by_role("alert")).to_be_visible(); shot(page, "06-login-unverified-denied")
                execute("AUTH-LOGIN-01", "Từ chối đăng nhập trước xác minh", unverified)

                def verification():
                    if db.account(account["email"])["verified"]:
                        return "reused"
                    link = email_link(state / "mail", account["email"], config["user_url"], config["timeouts_ms"]["email"])
                    go(page, config["user_url"], link.removeprefix(config["user_url"]))
                    expect(page.get_by_role("status")).to_contain_text("Email đã được xác minh")
                    assert db.account(account["email"])["verified"]
                    shot(page, "07-email-verification-success")
                execute("AUTH-VERIFY-01", "Xác minh bằng liên kết Pickup trên giao diện thật", verification)

                def duplicate():
                    go(page, config["user_url"], "/register"); register_fields(page)
                    with page.expect_response(lambda r: r.request.method == "POST" and r.url == config["api_url"] + "/auth/register") as pending:
                        page.locator("button[type=submit]").click()
                    assert pending.value.status == 409
                    code = pending.value.json()["code"]
                    assert code in ("EMAIL_ALREADY_EXISTS", "USERNAME_ALREADY_EXISTS"), f"Unexpected duplicate code: {code}"
                    expect(page.get_by_role("alert")).to_be_visible(); assert db.account_count(account["email"]) == 1
                    shot(page, "08-register-duplicate-denied")
                execute("AUTH-REG-04", "Đăng ký trùng không tạo account thứ hai", duplicate)

                def empty_login():
                    go(page, config["user_url"], "/login"); before = login_posts
                    page.locator("button[type=submit]").click()
                    expect(page.locator("#email")).to_have_attribute("aria-invalid", "true")
                    assert login_posts == before; shot(page, "09-login-required-fields")
                execute("AUTH-LOGIN-02", "Form đăng nhập trống không gọi API", empty_login)

                def wrong_password():
                    response = login(page, config["user_url"], account["password"] + "Wrong", 401)
                    assert response["code"] == "INVALID_CREDENTIALS"
                    expect(page.get_by_role("alert")).to_be_visible(); shot(page, "10-login-wrong-password")
                execute("AUTH-LOGIN-03", "Sai mật khẩu hiển thị lỗi và không vào account", wrong_password)

                def user_login():
                    response = login(page, config["user_url"], account["password"], 200)
                    assert response["user"]["userId"] == checkpoint.data["user_id"]
                    # A normal User login intentionally lands on Home. Account is
                    # still protected and is covered by the Web account cases.
                    expect(page).to_have_url(config["user_url"] + "/home")
                    expect(page.locator("app-home-page")).to_be_visible()
                    expect(page.locator(".top-avatar")).to_be_visible()
                    cookies = user.cookies(config["api_url"] + "/auth/refresh")
                    assert any(c["name"] == "hutube_refresh" and c["httpOnly"] and c["path"] == "/api/v1/auth" for c in cookies)
                    assert not response.get("refreshToken")
                    stored = page.evaluate("()=>Object.keys(localStorage).concat(Object.keys(sessionStorage))")
                    assert not any(re.search(r"(access.?token|refresh.?token|password)", key, re.I) for key in stored)
                    shot(page, "11-user-login-success-desktop")
                    page.set_viewport_size({"width": 390, "height": 844}); shot(page, "12-user-login-success-mobile")
                    assert page.evaluate("document.documentElement.scrollWidth <= innerWidth + 1")
                    page.set_viewport_size({"width": 1440, "height": 960})
                execute("AUTH-LOGIN-04", "User login thật, đúng identity và HttpOnly cookie", user_login)

                def restore():
                    page.reload(); expect(page.locator("app-home-page")).to_be_visible()
                    expect(page.locator(".top-avatar")).to_be_visible()
                    expect(page).to_have_url(config["user_url"] + "/home")
                    shot(page, "13-user-session-restored")
                execute("AUTH-LOGIN-05", "Reload phục hồi session đã đăng nhập", restore)

                def logout():
                    page.locator(".top-avatar").click(); page.locator(".account-drawer__logout").click()
                    expect(page.locator("app-feedback-host").get_by_role("dialog", name="Xác nhận đăng xuất", exact=True)).to_be_visible()
                    page.locator("app-feedback-host .confirm-submit").click()
                    expect(page).to_have_url(re.compile(re.escape(config["user_url"]) + r"/login"))
                    go(page, config["user_url"], "/account")
                    expect(page).to_have_url(re.compile(r"/login(?:\?|$)")); shot(page, "14-user-logout-guard")
                execute("AUTH-LOGIN-06", "Logout bằng UI, protected route yêu cầu đăng nhập lại", logout)

                active_page = other
                def admin_boundary():
                    response = login(other, config["admin_url"], account["password"], 403)
                    assert response["code"] == "ADMIN_ACCESS_DENIED"
                    expect(other.get_by_role("alert")).to_be_visible(); shot(other, "15-admin-normal-user-denied")
                execute("AUTH-ADMIN-01", "User thường không đăng nhập được Admin Web", admin_boundary)

                def admin_login():
                    nonlocal original_role
                    original_role = db.account(account["email"])["role_id"]
                    checkpoint.data["fixture_original_role"] = original_role; checkpoint.save()
                    # Fixture provisioning only; not an assertion of RBAC UI coverage.
                    db.grant_admin(checkpoint.data["user_id"])
                    response = login(other, config["admin_url"], account["password"], 200)
                    assert response["user"]["userId"] == checkpoint.data["user_id"] and response["user"]["isAdmin"]
                    expect(other).to_have_url(config["admin_url"] + "/account")
                    expect(other.locator(".profile-summary")).to_contain_text(account["email"])
                    assert any(c["name"] == "hutube_admin_refresh" and c["httpOnly"] for c in admin.cookies(config["api_url"] + "/auth/refresh"))
                    shot(other, "16-admin-login-success-desktop")
                    other.set_viewport_size({"width": 390, "height": 844}); shot(other, "17-admin-login-success-mobile")
                    other.set_viewport_size({"width": 1440, "height": 960})
                    other.reload(); expect(other.locator(".profile-summary")).to_contain_text(account["email"])
                execute("AUTH-ADMIN-02", "Admin login thật và reload với fixture quyền quản trị", admin_login)

                def admin_logout():
                    other.locator("main").get_by_role("button", name="Đăng xuất", exact=True).click()
                    expect(other).to_have_url(re.compile(r"/login(?:\?|$)"))
                    go(other, config["admin_url"], "/users")
                    expect(other).to_have_url(re.compile(r"/login(?:\?|$)")); shot(other, "18-admin-logout-guard")
                execute("AUTH-ADMIN-03", "Admin logout và chặn protected route", admin_logout)

                active_page = page
                # The trace contains DOM snapshots; close it before typing the emailed OTP.
                user.tracing.stop(path=str(run_dir / "user-pre-reset-trace.zip"))
                user_trace_stopped = True
                def password_reset_otp():
                    original_password = account["password"]
                    next_password = ("HuTubeE2EReset2026!B" if original_password == "HuTubeE2EReset2026!A"
                                     else "HuTubeE2EReset2026!A")
                    sent_at = time.time() - 1
                    go(page, config["user_url"], "/forgot-password")
                    page.locator("#email").fill(account["email"])
                    with page.expect_response(lambda r: r.request.method == "POST" and r.url == config["api_url"] + "/auth/forgot-password",
                                              timeout=config["timeouts_ms"]["api"]) as pending:
                        page.locator("button[type=submit]").click()
                    assert pending.value.status == 200
                    expect(page.get_by_role("status")).to_contain_text("Nếu email có trong hệ thống")

                    code = email_otp(state / "mail", account["email"], sent_at, config["timeouts_ms"]["email"])
                    go(page, config["user_url"], "/reset-password?email=" + account["email"])
                    expect(page.locator("#resetCode")).to_be_visible()
                    page.locator("#resetCode").fill(code)
                    page.locator("#password").fill(next_password)
                    page.locator("#confirmPassword").fill(next_password)
                    with page.expect_response(lambda r: r.request.method == "POST" and r.url == config["api_url"] + "/auth/reset-password",
                                              timeout=config["timeouts_ms"]["api"]) as pending:
                        page.locator("button[type=submit]").click()
                    assert pending.value.status == 200
                    expect(page.get_by_role("status")).to_be_visible()
                    # Keep the checkpoint recoverable if the subsequent login assertions fail.
                    account["password"] = next_password
                    checkpoint.save()
                    shot(page, "19-password-reset-otp-success")

                    old_response = login(page, config["user_url"], original_password, 401)
                    assert old_response["code"] == "INVALID_CREDENTIALS"
                    expect(page.get_by_role("alert")).to_be_visible()
                    new_response = login(page, config["user_url"], next_password, 200)
                    assert new_response["user"]["userId"] == checkpoint.data["user_id"]
                    expect(page).to_have_url(config["user_url"] + "/home")
                execute("AUTH-RESET-OTP-01", "Đổi mật khẩu bằng OTP email và từ chối mật khẩu cũ", password_reset_otp)
                assert not errors, "Unexpected JavaScript errors: " + "; ".join(errors)
            finally:
                if not user_trace_stopped:
                    user.tracing.stop(path=str(run_dir / "user-trace.zip"))
                admin.tracing.stop(path=str(run_dir / "admin-trace.zip"))
                user.close(); admin.close(); browser.close()
    finally:
        if original_role:
            db.restore_role(checkpoint.data["user_id"], original_role)
            checkpoint.data.pop("fixture_original_role", None); checkpoint.save()
        report = {"scope": "real-local-auth", "at": now(), "account_email": account["email"],
                  "user_id": checkpoint.data.get("user_id"), "screenshot_dir": str(screenshots),
                  "screenshots": screenshot_entries,
                  "created_account_count": sum(r["path"] == "/api/v1/auth/register" and r["status"] == 201 for r in requests),
                  "account_count": db.account_count(account["email"]), "registration_post_count": registered_posts,
                  "passed": sum(c.get("status") == "passed" for c in cases),
                  "reused": sum(c.get("status") == "reused" for c in cases),
                  "failed": sum(c.get("status") == "failed" for c in cases), "cases": cases,
                  "duration_ms": round((time.monotonic() - started) * 1000), "javascript_errors": errors}
        atomic_json(run_dir / "report.json", report)
        atomic_json(run_dir / "http-statuses.json", requests)
        atomic_json(ROOT / "artifacts/latest.json", {"run_dir": str(run_dir), "passed": report["passed"], "failed": report["failed"], "reused": report["reused"]})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    args = parser.parse_args()
    try:
        run(json.loads(args.config.read_text(encoding="utf-8")))
    except Exception as exc:
        print("FAIL: " + str(exc), file=sys.stderr)
        sys.exit(1)
