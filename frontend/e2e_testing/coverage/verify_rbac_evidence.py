"""Fail CI on a missing permission scenario, failed assertion or missing evidence."""
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from support.evidence import run_date_folder
from tests.rbac_matrix import ACTION_CONTROLS, CATALOGUE_ONLY, ROUTES


def verify(run_dir):
    report_path = run_dir / 'report.json'
    assert report_path.is_file(), f'Missing report.json in {run_dir}'
    report = json.loads(report_path.read_text(encoding='utf-8'))
    assert report['suite'] == 'rbac'
    assert not report.get('run_error'), (
        'Incomplete RBAC runner execution: ' + str(report.get('run_error_message') or report.get('run_error')))
    assert not report.get('page_errors'), 'Unexpected JavaScript page errors'
    assert report['failed'] == report['blocked'] == report['reused'] == 0
    coverage_path = run_dir / 'rbac-coverage.json'
    assert coverage_path.is_file(), f'Missing rbac-coverage.json after a completed RBAC run in {run_dir}'
    coverage = json.loads(coverage_path.read_text(encoding='utf-8'))
    codes = coverage['catalogue_permissions']
    group_count = len({c.split('.')[0] for c in codes})
    assert len(set(codes)) == 52 and group_count == 17
    assert coverage['group_count'] == group_count
    assert set(coverage['catalogue_only_permissions']) == CATALOGUE_ONLY
    cases = report['cases']
    assert len({c['id'] for c in cases}) == len(cases)
    expected = {(p, 'GRANT-REVOKE-REGRANT') for p in codes}
    expected |= {(p, 'API-' + phase) for p in codes if p not in CATALOGUE_ONLY
                 for phase in ('ANONYMOUS', 'DENIED', 'ALLOWED', 'REVOKED')}
    expected |= {(p, layer) for p, _, _ in ROUTES for layer in ('UI-ALLOW', 'UI-DENY')}
    expected |= {(p, layer) for p, _, _, _ in ACTION_CONTROLS for layer in ('UI-CONTROL-ALLOW', 'UI-CONTROL-DENY')}
    expected |= {(p, layer) for p in ('channel.edit', 'channel.strike', 'channel.remove_strike', 'video.strike') for layer in ('UI-ACTION-ALLOW', 'UI-ACTION-DENY')}
    expected |= {('role.view', 'SUPERADMIN-USER-ROLE'), ('role.edit', 'NO-PRIVILEGE-ESCALATION'),
                 ('role.delete', 'DELETE-ASSIGNED-ROLE'), ('role.delete', 'PROTECTED-SYSTEM-ROLE')}
    expected |= {('channel.edit', 'VALIDATION-AUDIT'), ('channel.remove_strike', 'CHANNEL-SCOPE'),
                 ('video.export', 'PAGINATION-PRIVATE-FILTER'), ('channel.export', 'CSV-FILTER-FORMULA-SAFETY'),
                 ('video.strike', 'VALIDATION-VIDEO-AUDIT')}
    assert {(c['permission'], c['coverage_layer']) for c in cases} == expected
    for case in cases:
        assert case['status'] == 'passed', case['id']
        screenshot = (ROOT / case['screenshot']).resolve()
        relative = screenshot.relative_to((ROOT / 'screenshots').resolve()).parts
        assert relative[0] == run_date_folder(report['run_id'])
        assert relative[1] == 'RBAC' and relative[2] == 'admin' and relative[3] == case['id']
        assert screenshot.read_bytes()[:8] == b'\x89PNG\r\n\x1a\n', case['id']
        assert case['business_endpoint_available'] == (case['permission'] not in CATALOGUE_ONLY)
    assert len(coverage['requests']) == len(codes) * 4
    for request in coverage['requests']:
        status = request['status']
        phase = request['phase']
        assert (phase == 'ALLOWED' and status in (200, 201, 204)
                or phase == 'ANONYMOUS' and status == 401
                or phase in ('DENIED', 'REVOKED') and status == (404 if request['permission'] == 'video.view_private' else 403)), request
    print(f"RBAC: {len(cases)} passed; {len(codes)} permission lifecycles; {len(codes)} real API contracts; {len(ROUTES)} route guards.")


if __name__ == '__main__':
    latest = json.loads((ROOT / 'artifacts/rbac-latest.json').read_text(encoding='utf-8'))
    verify(Path(latest['run_dir']))
