"""Isolated Web fixture and browser evidence. Never uses personal credentials."""
import json
import os
import time
import traceback
import inspect
from pathlib import Path
from urllib.parse import urlsplit

from playwright.sync_api import expect
from support.evidence import screenshot_path
from support.local_db import LocalDb
from support.state import atomic_json, now


class WebSuite:
    def __init__(self, config, playwright):
        self.config = config
        self.root = Path(__file__).resolve().parents[1]
        self.run_dir = Path(config['run_dir'])
        self.run_id = self.run_dir.name
        self.db = LocalDb(config)
        self.state_path = Path(config['state_dir']) / 'web-fixtures.json'
        self.state = json.loads(self.state_path.read_text(encoding='utf-8')) if self.state_path.exists() else {}
        scope = {key: config[key] for key in ('api_url', 'user_url', 'admin_url', 'pg_port')}
        if self.state.get('scope', scope) != scope:
            raise RuntimeError('Web fixtures belong to another environment')
        self.state['scope'] = scope
        evidence = self.state.setdefault('case_evidence', {})
        # Preserve the first actual action evidence even when later runs reuse
        # the resource. Only import reports made by this isolated Web suite.
        for report_path in sorted((self.root / 'artifacts/runs').glob('*/report.json')):
            old = json.loads(report_path.read_text(encoding='utf-8'))
            if old.get('suite') != 'web':
                continue
            for case in old.get('cases', []):
                if case['status'] == 'passed' and case.get('screenshot') and (self.root / case['screenshot']).is_file():
                    evidence.setdefault(case['id'], {'first_passed_run': old['run_id'], 'screenshot': case['screenshot']})
        self.report = {'suite': 'web', 'run_id': self.run_id, 'started_at': now(), 'cases': [], 'page_errors': [], 'setup': [], 'http_statuses': [], 'request_failures': []}
        self.browser = playwright.chromium.launch(headless=True, **({'executable_path': os.environ['CHROME_BIN']} if os.environ.get('CHROME_BIN') else {}))
        self.report['browser_version'] = self.browser.version
        self.actors = {}
        expect.set_options(timeout=config['timeouts_ms']['action'])

    def save(self):
        atomic_json(self.state_path, self.state)

    def flush(self):
        for status in ('passed', 'failed', 'blocked', 'reused'):
            self.report[status] = sum(case['status'] == status for case in self.report['cases'])
        self.report['account_count'] = len(self.actors)
        atomic_json(self.run_dir / 'report.json', self.report)
        for application in ('user', 'admin'):
            cases = [case for case in self.report['cases'] if case['application'] == application]
            atomic_json(self.run_dir / (application + '-report.json'), {'suite': application + '-web', 'run_id': self.run_id, 'cases': cases})

    def actor(self, name, role='user'):
        email = 'hutube.e2e.web.' + name + '@example.test'
        password = 'HuTubeWebE2e2026!'
        app = 'user' if role == 'user' else 'admin'
        context = self.browser.new_context(viewport={'width': 1440, 'height': 960}, locale='vi-VN')
        existing = self.db.account(email)
        if not existing:
            response = context.request.post(self.config['api_url'] + '/auth/register', data={'email': email, 'password': password, 'username': 'hutube_web_' + name, 'displayName': 'HuTube E2E ' + name})
            if response.status != 201:
                raise RuntimeError('Fixture registration failed: HTTP ' + str(response.status))
            existing = self.db.account(email)
        self.db.sql("UPDATE users SET status='active',email_verified_at=COALESCE(email_verified_at,now()),role_id=(SELECT role_id FROM roles WHERE code=:'role'),updated_at=now() WHERE user_id=:'id'::uuid;", {'id': existing['user_id'], 'role': role})
        page = context.new_page()
        page.set_default_timeout(self.config['timeouts_ms']['action'])
        page.set_default_navigation_timeout(self.config['timeouts_ms']['navigation'])
        actor = {'name': name, 'application': app, 'context': context, 'page': page, 'headers': {}, 'email': email, 'user_id': existing['user_id']}
        def observe(request):
            if request.url.startswith(self.config['api_url'] + '/') and request.headers.get('authorization'):
                actor['headers']['Authorization'] = request.headers['authorization']
        page.on('request', observe)
        page.on('response', lambda response: self.report['http_statuses'].append({'actor': name, 'method': response.request.method, 'path': urlsplit(response.url).path, 'status': response.status}))
        page.on('pageerror', lambda error: self.report['page_errors'].append({'actor': name, 'type': error.name}))
        page.on('requestfailed', lambda request: self.report['request_failures'].append({'actor': name, 'path': urlsplit(request.url).path, 'failure': request.failure}))
        setup = {'actor': name, 'application': app, 'status': 'failed'}
        self.report['setup'].append(setup)
        page.goto(self.config[app + '_url'] + '/login')
        expect(page.locator('#email')).to_be_visible(timeout=self.config['timeouts_ms']['navigation'])
        page.locator('#email').fill(email)
        page.locator('#password').fill(password)
        with page.expect_response(lambda r: r.request.method == 'POST' and r.url == self.config['api_url'] + '/auth/login', timeout=30000) as pending:
            page.locator('button[type=submit]').click()
        if pending.value.status != 200:
            raise RuntimeError('Fixture login failed: HTTP ' + str(pending.value.status))
        actor['identity'] = pending.value.json()['user']
        login_route = '/home' if app == 'user' else '/account'
        page.wait_for_url('**' + login_route)
        if app == 'user':
            expect(page.locator('app-home-page')).to_be_visible(timeout=self.config['timeouts_ms']['navigation'])
            # Account checks start from the protected account page after the
            # redirect assertion above, so the fixture covers both behaviors.
            page.goto(self.config['user_url'] + '/account')
            page.wait_for_url('**/account')
        expect(page.locator('app-account-page')).to_be_visible()
        self.actors[name] = actor
        setup.update(status='passed', http_status=200, login_route=login_route)
        return actor

    def go(self, actor, route, selector):
        page = actor['page']
        identity_path = '/api/v1/auth/me' if actor['application'] == 'user' else '/api/v1/admin/me'
        with page.expect_response(lambda response: urlsplit(response.url).path == identity_path,
                                  timeout=self.config['timeouts_ms']['navigation']) as identity:
            page.goto(self.config[actor['application'] + '_url'] + route)
        assert identity.value.status == 200
        expect(page.locator(selector)).to_be_visible(timeout=self.config['timeouts_ms']['navigation'])
        return page

    def api(self, actor, method, path, **kwargs):
        response = getattr(actor['context'].request, method)(self.config['api_url'] + path, headers=actor['headers'], **kwargs)
        self.report['http_statuses'].append({'actor': actor['name'], 'method': method.upper(), 'path': urlsplit(self.config['api_url'] + path).path, 'status': response.status})
        return response

    def case(self, actor, case_id, title, action, module=None, expected='', steps='', data=''):
        entry = {'id': case_id, 'name': title, 'application': actor['application'], 'actor': actor['name'], 'module': module or case_id.split('-')[1].lower(), 'expected': expected, 'steps': steps, 'data': data, 'status': 'failed', 'started_at': now()}
        entry['script'] = Path(inspect.getsourcefile(action)).relative_to(self.root).as_posix()
        entry['script_line'] = inspect.getsourcelines(action)[1]
        self.report['cases'].append(entry)
        started = time.monotonic()
        try:
            entry['status'] = action() or 'passed'
        except Exception as error:
            entry['error_type'] = type(error).__name__
            entry['failure_location'] = [{'file': Path(frame.filename).name, 'function': frame.name, 'line': frame.lineno}
                                         for frame in traceback.extract_tb(error.__traceback__)[:4]]
            entry['last_http_statuses'] = [item for item in self.report['http_statuses'] if item['actor'] == actor['name'] and item['path'].startswith('/api/v1/')][-8:]
            # Do not retain token-bearing requests, response bodies or passwords.
            entry['actual'] = 'Assertion/operation failed; inspect screenshot. ' + type(error).__name__
            if self.report['suite'] == 'rbac':
                # This suite uses only synthetic fixture payloads. Keep actionable
                # assertion/locator diagnostics, without request headers or tokens.
                entry['actual'] = str(error)[:1500]
        entry['duration_ms'] = round((time.monotonic() - started) * 1000)
        entry['viewport'] = actor['page'].viewport_size
        destination = self.root / screenshot_path(self.run_id, self.report['suite'],
                                                   actor['application'], case_id,
                                                   entry['status'], entry['module'])
        destination.parent.mkdir(parents=True, exist_ok=True)
        try:
            actor['page'].screenshot(path=str(destination), full_page=True, animations='disabled', mask=[actor['page'].locator('input[type=password]')])
            entry['screenshot'] = destination.relative_to(self.root).as_posix()
        except Exception:
            entry['screenshot_error'] = True
        if entry['status'] == 'passed' and entry.get('screenshot'):
            self.state['case_evidence'].setdefault(case_id, {'first_passed_run': self.run_id, 'screenshot': entry['screenshot']})
            self.save()
        if case_id in self.state['case_evidence']:
            entry['first_passed_evidence'] = self.state['case_evidence'][case_id]
        print(case_id, entry['status'], flush=True)
        self.flush()
        return entry['status'] in ('passed', 'reused')

    def blocked(self, actor, case_id, title, reason):
        def action():
            return 'blocked'
        self.case(actor, case_id, title, action)
        self.report['cases'][-1]['actual'] = reason
        self.flush()

    def close(self):
        self.report['finished_at'] = now()
        self.flush()
        latest = 'rbac-latest.json' if self.report['suite'] == 'rbac' else 'web-latest.json'
        atomic_json(self.root / 'artifacts' / latest, {'run_dir': str(self.run_dir), **{key: self.report[key] for key in ('passed', 'failed', 'blocked', 'reused')}})
        self.browser.close()
