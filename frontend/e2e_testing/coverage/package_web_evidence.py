"""Package the workbook and referenced Web evidence without runtime secrets."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
from zipfile import ZIP_DEFLATED, ZipFile

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', type=Path)
    parser.add_argument('--workbook', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--verification', type=Path, help='Include a preceding fresh-database run, including any diagnosed test failure.')
    args = parser.parse_args()
    run = args.run or Path(json.loads((ROOT / 'artifacts/web-latest.json').read_text(encoding='utf-8'))['run_dir'])
    report = json.loads((run / 'report.json').read_text(encoding='utf-8'))
    assert report['suite'] == 'web' and not report.get('run_error')
    files['artifacts/runs/' + report['run_id'] + '/' + args.workbook.name] = args.workbook.resolve()
    referenced = {}

    def add_image(case):
        relative = case['screenshot']
        image = (ROOT / relative).resolve()
        assert image.is_relative_to((ROOT / 'screenshots').resolve())
        assert image.read_bytes()[:8] == b'\x89PNG\r\n\x1a\n'
        files[relative.replace('\\', '/')] = image

    for case in report['cases']:
        add_image(case)
        first = case.get('first_passed_evidence')
        if first:
            add_image(first)
            referenced.setdefault(first['first_passed_run'], set()).add(case['id'])
    for name in ('report.json', 'user-report.json', 'admin-report.json', 'scope-progress.json'):
        files['reports/' + report['run_id'] + '/' + name] = run / name
    for downloaded in (run / 'downloads').glob('*.csv'):
        files['downloads/' + downloaded.name] = downloaded
    for name in ('WEB_E2E_PROGRESS.md', 'WEB_E2E_BUG_FIXES.md'):
        if (REPO / 'docs' / name).is_file():
            files['docs/' + name] = REPO / 'docs' / name
    generated = {}
    for run_id, identifiers in referenced.items():
        original = json.loads((ROOT / 'artifacts/runs' / run_id / 'report.json').read_text(encoding='utf-8'))
        selected = [case for case in original['cases'] if case['id'] in identifiers and case['status'] == 'passed']
        generated['reports/' + run_id + '/first-pass-excerpts.json'] = json.dumps(
            {'run_id': run_id, 'note': 'Selected first PASS cases; this is not the complete historical run.', 'cases': selected},
            ensure_ascii=False, indent=2).encode('utf-8')
    if args.baseline:
        baseline = json.loads((args.baseline / 'report.json').read_text(encoding='utf-8'))
        assert baseline['suite'] == 'web'
        files['reports/' + baseline['run_id'] + '/report.json'] = args.baseline / 'report.json'
        for case in baseline['cases']:
            if case['status'] == 'failed':
                add_image(case)
    if args.verification:
        verification = json.loads((args.verification / 'report.json').read_text(encoding='utf-8'))
        assert verification['suite'] == 'web'
        files['reports/' + verification['run_id'] + '/fresh-verification-report.json'] = args.verification / 'report.json'
        for case in verification['cases']:
            add_image(case)
    generated['README.md'] = (
        '# HuTube Web E2E evidence\n\n'
        f'Current run: `{report["run_id"]}`. Open the workbook Summary, User and Admin sheets.\n\n'
        'Extract the entire ZIP before opening screenshot paths from Notes. Paths are relative to this package. '
        'First action PASS images refer to historical runs; REUSED is not a newly executed create operation.\n\n'
        'Read docs/WEB_E2E_PROGRESS.md for exclusions and unimplemented flows. '
        'The included original-code baseline is incomplete; its failed cases demonstrate the reported regressions. '
        'GitHub Actions has not been executed for these changes.\n\n'
        'The preceding fresh-database verification report is retained when supplied, including its diagnosed '
        'test synchronization failure. It is separate from the final run; see the bug-fix document.\n\n'
        'Only reports, workbook, PNG evidence, downloaded CSV and documentation are included. '
        'Runtime database, credentials, browser traces and mail are excluded.\n'
    ).encode('utf-8')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    manifest = []
    with ZipFile(args.output, 'w', ZIP_DEFLATED) as bundle:
        for name, source in sorted(files.items()):
            assert not PurePosixPath(name).is_absolute() and '..' not in PurePosixPath(name).parts
            data = source.read_bytes()
            bundle.writestr(name, data)
            manifest.append({'path': name, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)})
        for name, data in sorted(generated.items()):
            bundle.writestr(name, data)
            manifest.append({'path': name, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)})
        bundle.writestr('manifest.json', json.dumps(manifest, indent=2))
    with ZipFile(args.output) as bundle:
        assert bundle.testzip() is None
    print(f'Packaged {len(manifest)} files: {args.output}')


if __name__ == '__main__':
    main()
