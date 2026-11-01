"""Verify catalog/docs links and structured screenshot evidence; does not claim full E2E coverage."""
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1]


def read(name):
    return json.loads((ROOT / name).read_text(encoding='utf-8'))


def main():
    web = read('coverage/web-inventory.json')
    backend = read('coverage/backend-endpoints.json')
    tests = read('coverage/existing-tests.json')
    web_document = (REPO / 'docs/WEB_TEST_SCENARIOS.md').read_text(encoding='utf-8')
    backend_document = (REPO / 'docs/BACKEND_TEST_SCENARIOS.md').read_text(encoding='utf-8')
    for item in web['routes'] + web['bindings'] + web['navigation'] + web['form_controls']:
        assert item['scenario'] in web_document, 'Unmapped Web scenario: ' + item['scenario']
        assert (REPO / item['source']).is_file(), 'Missing Web source: ' + item['source']
    for item in backend['operations']:
        assert item['scenario'] in backend_document, 'Unmapped API scenario: ' + item['scenario']
    for item in tests['dotnet'] + tests['recommendation_python']:
        assert '`' + item['method'] + '`' in backend_document, 'Missing method in appendix'
    evidence = read('artifacts/auth-evidence.json')
    for item in evidence['screenshots']:
        path = ROOT / item['file']
        assert path.is_file() and path.read_bytes()[:8] == b'\x89PNG\r\n\x1a\n'
        assert path.parent.name == item['case_id']
        assert path.parent.parent.name == item['application']
        assert path.parent.parent.parent.name == 'Auth'
    latest = read('artifacts/latest.json')
    report = json.loads((Path(latest['run_dir']) / 'report.json').read_text(encoding='utf-8'))
    assert report['failed'] == 0 and report['passed'] + report['reused'] == 14
    assert report['account_count'] == 1 and report['created_account_count'] in (0, 1)
    assert len(report['cases']) == 14 and not report['javascript_errors']
    for screenshot in report['screenshots']:
        path = Path(report['screenshot_dir']) / screenshot['path']
        assert path.is_file()
        parts = path.relative_to(Path(report['screenshot_dir'])).parts
        assert parts[:3] == ('Auth', screenshot['application'], screenshot['case_id'])
    documents = list(ROOT.rglob('*.md')) + [REPO / 'docs/WEB_TEST_SCENARIOS.md', REPO / 'docs/BACKEND_TEST_SCENARIOS.md']
    for document in documents:
        if any(part in ('.state', '.venv', 'runs') for part in document.relative_to(REPO).parts):
            continue
        for target in re.findall(r'\[[^\]\n]+\]\(([^)\n]+)\)', document.read_text(encoding='utf-8')):
            if target.startswith(('http:', 'https:', '#')):
                continue
            assert (document.parent / target.split('#')[0]).exists(), f'Broken link: {document.name}: {target}'
    print(f'Catalog verified: {len(web["routes"])} route declarations, {len(web["bindings"])} bindings, '
          f'{len(web["navigation"])} navigation references, {len(web["form_controls"])} form controls, '
          f'{len(backend["operations"])} API operations. All scenario references, method appendix and evidence links exist.')
    print(f'Latest auth: {report["passed"]} passed / {report["reused"]} reused / 0 failed; '
          f'1 persistent account; {len(report["screenshots"])} grouped screenshots.')


if __name__ == '__main__':
    main()
