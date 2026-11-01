"""Account Web checks against already running services; credentials stay in environment."""
import argparse
import json
import os
import sys
from datetime import datetime, timezone
from types import SimpleNamespace
from pathlib import Path
from urllib.parse import urlsplit

from playwright.sync_api import sync_playwright, expect
from support.evidence import screenshot_path


def run_suite(application, run_checks):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--user-url', default='http://localhost:4200')
    parser.add_argument('--admin-url', default='http://localhost:4201')
    parser.add_argument('--write-profile', action='store_true', help='Test profile save/validation on a loopback API, then restore original values.')
    args = parser.parse_args()
    if application == 'admin' and args.write_profile:
        parser.error('--write-profile is only supported by user_account.py')
    credentials = json.loads(os.environ['WEB_E2E_CREDENTIALS'])
    root = Path(__file__).resolve().parents[1]
    run_id = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')
    run_dir = root / 'artifacts/runs' / run_id
    run_dir.mkdir(parents=True)
    report = {'suite': application + '-account', 'write_profile': args.write_profile, 'run_id': run_id, 'cases': [], 'setup': [], 'page_errors': []}
    expect.set_options(timeout=15000)

    def check(case_id, page, action, application, on_failure='failed'):
        entry = {'id': case_id, 'status': 'failed'}
        report['cases'].append(entry)
        try:
            action()
            entry['status'] = 'passed'
        except Exception as error:
            # Assertion text can contain personal data; record type only.
            entry['error_type'] = type(error).__name__
            entry['status'] = on_failure
        shot = root / screenshot_path(run_id, application + '-account', application,
                                      case_id, entry['status'], 'account')
        shot.parent.mkdir(parents=True, exist_ok=True)
        try:
            page.screenshot(path=str(shot), full_page=True, animations='disabled', mask=[page.locator('input[type=password]')])
            entry['screenshot'] = shot.relative_to(root).as_posix()
        except Exception:
            entry['screenshot_error'] = True
        print(case_id, entry['status'], flush=True)

    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, executable_path=os.environ.get('CHROME_BIN') or r'C:\Program Files\Google\Chrome\Application\chrome.exe')
        try:
            for app, origin in [('user', args.user_url), ('admin', args.admin_url)]:
                if application != app:
                    continue
                context = browser.new_context(viewport={'width': 1440, 'height': 960}, locale='vi-VN')
                page = context.new_page()
                page.set_default_timeout(15000)
                page.set_default_navigation_timeout(45000)
                access_headers = {}
                def observe_profile_request(request):
                    if urlsplit(request.url).path == '/api/v1/account/profile':
                        authorization = request.headers.get('authorization')
                        if authorization:
                            access_headers['Authorization'] = authorization
                page.on('request', observe_profile_request)
                page.on('pageerror', lambda error, app=app: report['page_errors'].append({'app': app, 'type': error.name}))
                setup = {'application': app, 'status': 'failed'}
                report['setup'].append(setup)
                try:
                    page.goto(origin + '/login')
                    page.locator('#email').fill(credentials[app]['email'])
                    page.locator('#password').fill(credentials[app]['password'])
                    with page.expect_response(lambda r: r.request.method == 'POST' and urlsplit(r.url).path == '/api/v1/auth/login', timeout=30000) as pending:
                        page.locator('button[type=submit]').click()
                    setup['http_status'] = pending.value.status
                    setup['api_origin'] = urlsplit(pending.value.url)._replace(path='', query='', fragment='').geturl()
                    if pending.value.status != 200:
                        setup['error_code'] = pending.value.json().get('code', 'LOGIN_REJECTED')
                    assert pending.value.status == 200
                    login_data = pending.value.json()
                    identity = login_data['user']
                    assert identity['email'].lower() == credentials[app]['email'].lower()
                    if app == 'admin':
                        assert identity['isAdmin'] is True
                    login_route = '/home' if app == 'user' else '/account'
                    page.wait_for_url('**' + login_route, timeout=30000)
                    if app == 'user':
                        expect(page.locator('app-home-page')).to_be_visible(timeout=30000)
                        page.goto(origin + '/account')
                        page.wait_for_url('**/account', timeout=30000)
                    expect(page.locator('app-account-page')).to_be_visible()
                    setup['status'] = 'passed'
                    run_checks(SimpleNamespace(
                        page=page, context=context, origin=origin, setup=setup,
                        identity=identity, credentials=credentials, app=app,
                        report=report, run_dir=run_dir, access_headers=access_headers,
                        check=check, write_profile=args.write_profile))
                except Exception as error:
                    setup['status'] = 'failed'
                    setup['error_type'] = type(error).__name__
                    def blocked_setup():
                        raise RuntimeError('Account prerequisite unavailable')
                    check(('U' if app == 'user' else 'A') + '-ACCOUNT-SETUP', page, blocked_setup, app, on_failure='blocked')
                finally:
                    context.close()
        finally:
            browser.close()
            report['passed'] = sum(case['status'] == 'passed' for case in report['cases'])
            report['failed'] = sum(case['status'] == 'failed' for case in report['cases'])
            report['blocked'] = sum(case['status'] == 'blocked' for case in report['cases'])
            (run_dir / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
            print('Report:', run_dir / 'report.json', flush=True)
    return 1 if report['failed'] or report['blocked'] else 0
