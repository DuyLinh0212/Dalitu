"""Arrange E2E screenshots as run-date/group/scope/case and update evidence links."""
import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPO = ROOT.parents[1]
sys.path.insert(0, str(ROOT))
from support.evidence import run_date_folder, screenshot_path

TEXT_SUFFIXES = {'.json', '.md', '.txt', '.yaml', '.yml'}
AUTH_CASE = re.compile(r'AUTH-(?:REG|LOGIN|VERIFY|ADMIN)-\d{2}')


def resolve_reference(value, base=None):
    path = Path(value)
    if path.is_absolute():
        return path.resolve()
    if path.parts and path.parts[0] == 'screenshots':
        return (ROOT / path).resolve()
    return ((base or ROOT) / path).resolve()


def report_metadata():
    by_file = {}
    known_cases = []

    def add(run_id, suite, application, case_id, module, step, source, viewport=None):
        if not case_id or not source:
            return
        source = Path(source).resolve()
        destination = screenshot_path(run_id, suite, application, case_id, step,
                                      module=module, viewport=viewport)
        metadata = {'run_id': run_id, 'suite': suite, 'application': application,
                    'case_id': str(case_id), 'module': module or '',
                    'destination': destination, 'step': step, 'viewport': viewport}
        previous = by_file.get(source)
        if previous and previous['destination'] != destination:
            raise ValueError(f'Conflicting report metadata for {source}')
        by_file[source] = metadata
        known_cases.append(metadata)

    reports = sorted((ROOT / 'artifacts/runs').glob('*/*.json'))
    for report_path in reports:
        if report_path.name not in ('report.json', 'user-report.json', 'admin-report.json'):
            continue
        report = json.loads(report_path.read_text(encoding='utf-8'))
        run_id = str(report.get('run_id') or report_path.parent.name)
        suite = report.get('suite') or ('auth' if report.get('scope') == 'real-local-auth' else '')
        for case in report.get('cases', []):
            case_id = case.get('id')
            application = case.get('application')
            if application not in ('user', 'admin'):
                application = 'admin' if report_path.name.startswith('admin-') else 'user'
            module = case.get('module') or case.get('parent')
            screenshot = case.get('screenshot')
            if screenshot:
                source = resolve_reference(screenshot)
                add(run_id, suite, application, case_id,
                    module, case.get('status') or source.stem, source)
            for capture in case.get('screenshots', []):
                source = resolve_reference(capture.get('path', ''))
                add(run_id, suite, application, case_id, module,
                    capture.get('step') or source.stem, source, capture.get('viewport'))

        screenshot_dir = report.get('screenshot_dir')
        for capture in report.get('screenshots', []):
            if not screenshot_dir:
                continue
            source = resolve_reference(capture.get('path', ''), Path(screenshot_dir))
            application = capture.get('application') or 'user'
            add(run_id, 'auth', application, capture.get('case_id'), capture.get('module'),
                capture.get('step') or source.stem, source, capture.get('viewport'))

    return by_file, known_cases


def screenshot_scopes():
    screenshots = ROOT / 'screenshots'
    if not screenshots.is_dir():
        return []
    return sorted((path for path in screenshots.iterdir() if path.is_dir()), key=lambda p: p.name)


def infer_metadata(image, scope, known_cases):
    stem = image.stem
    application = stem.split('-', 1)[0]
    if application not in ('user', 'admin'):
        return None
    candidates = [item for item in known_cases if item['application'] == application
                  and stem.startswith(application + '-' + item['case_id'] + '-')]
    if not candidates:
        body = stem[len(application) + 1:]
        parts = body.split('-')
        end = next((i for i, part in enumerate(parts) if re.fullmatch(r'\d{2}', part)), None)
        if end is None:
            auth_id = AUTH_CASE.search(body)
            if not auth_id:
                return None
            case_id = auth_id.group(0)
            step = body[auth_id.end():].strip('-') or 'capture'
            suite, module = 'auth', 'authentication'
        else:
            case_id = '-'.join(parts[:end + 1])
            step = '-'.join(parts[end + 1:]) or 'capture'
            suite, module = 'web', case_id.split('-')[1].lower()
        return {'run_id': scope.name, 'suite': suite, 'application': application,
                'case_id': case_id, 'module': module, 'step': step}
    candidates.sort(key=lambda item: (item['run_id'] != scope.name, -len(item['case_id'])))
    selected = candidates[0]
    prefix = application + '-' + selected['case_id'] + '-'
    step = stem[len(prefix):] or selected['step']
    return {**selected, 'run_id': scope.name, 'step': step}


def collect_moves():
    by_file, known_cases = report_metadata()
    moves = []
    unmapped = []
    for scope in screenshot_scopes():
        if scope.name == 'baseline-auth':
            continue
        for image in sorted(scope.rglob('*.png')):
            relative_parts = image.relative_to(ROOT / 'screenshots').parts
            if (len(relative_parts) == 5 and relative_parts[2] in ('admin', 'user', 'all')
                    and relative_parts[3] == image.parent.name):
                continue
            metadata = by_file.get(image.resolve()) or infer_metadata(image, scope, known_cases)
            if not metadata:
                unmapped.append(image)
                continue
            destination_relative = screenshot_path(
                metadata['run_id'], metadata['suite'], metadata['application'],
                metadata['case_id'], metadata['step'], metadata.get('module'),
                metadata.get('viewport'))
            destination = ROOT / destination_relative
            if destination.resolve() == image.resolve():
                continue
            if not image.resolve().is_relative_to(ROOT) or not destination.resolve().is_relative_to(ROOT):
                raise ValueError(f'Screenshot move must stay within e2e_testing: {image}')
            moves.append({'source': image, 'destination': destination,
                          'run_id': metadata['run_id']})

    by_destination = {}
    for move in moves:
        by_destination.setdefault(move['destination'], []).append(move)
    for destination, group in by_destination.items():
        if len(group) == 1 and not destination.exists():
            continue
        for move in group:
            digest = hashlib.sha1(move['source'].relative_to(ROOT).as_posix().encode('utf-8')).hexdigest()[:8]
            move['destination'] = destination.with_name(destination.stem + '-' + digest + '.png')
            if move['destination'].exists():
                raise FileExistsError(f'Cannot safely resolve screenshot name collision: {move["destination"]}')
    return moves, unmapped


def text_files():
    files = set()
    for directory in (ROOT / 'artifacts', ROOT / 'coverage'):
        if directory.is_dir():
            files.update(path for path in directory.rglob('*')
                         if path.is_file() and path.suffix.lower() in TEXT_SUFFIXES)
    for relative in ('docs/memory.md', 'docs/WEB_TEST_SCENARIOS.md', 'docs/ISSUE_34_E2E_GUIDE.md'):
        path = REPO / relative
        if path.is_file():
            files.add(path)
    return sorted(files)


def move_replacements(move):
    old = move['source'].relative_to(ROOT)
    new = move['destination'].relative_to(ROOT)
    pairs = []
    for before, after in ((old.as_posix(), new.as_posix()),
                          (str(old).replace('/', '\\'), str(new).replace('/', '\\')),
                          (str(ROOT / old), str(ROOT / new))):
        pairs.append((before, after))
        pairs.append((before.replace('\\', '/'), after.replace('\\', '/')))
    old_run_root = Path('screenshots') / move['run_id']
    new_run_root = Path('screenshots') / run_date_folder(move['run_id'])
    source_run_root = ROOT / old_run_root
    old_local = (move['source'].relative_to(source_run_root).as_posix()
                 if source_run_root in move['source'].parents else move['source'].name)
    new_local = move['destination'].relative_to(ROOT / new_run_root).as_posix()
    return pairs, old_local, new_local


def rewrite_references(moves, apply):
    replacements = [(move, *move_replacements(move)) for move in moves]
    changed_paths = []
    changed_references = 0
    for path in text_files():
        try:
            original = path.read_bytes().decode('utf-8')
        except UnicodeDecodeError:
            continue
        updated = original
        try:
            file_run = path.relative_to(ROOT / 'artifacts/runs').parts[0]
        except ValueError:
            file_run = None
        for move, pairs, old_local, new_local in replacements:
            for old, new in pairs:
                if old in updated:
                    changed_references += updated.count(old)
                    updated = updated.replace(old, new)
            if file_run == move['run_id'] and old_local in updated:
                changed_references += updated.count(old_local)
                updated = updated.replace(old_local, new_local)
        if file_run:
            old_root = ROOT / 'screenshots' / file_run
            new_root = ROOT / 'screenshots' / run_date_folder(file_run)
            for old_text, new_text in (
                    (str(old_root), str(new_root)),
                    (str(old_root).replace('\\', '/'), str(new_root).replace('\\', '/')),
                    (str(old_root).replace('\\', '/').replace('/', '\\'),
                     str(new_root).replace('\\', '/').replace('/', '\\')),
                    (str(old_root).replace('\\', '\\\\'), str(new_root).replace('\\', '\\\\'))):
                if old_text in updated:
                    changed_references += updated.count(old_text)
                    updated = updated.replace(old_text, new_text)
        if updated != original:
            changed_paths.append(path)
            if apply:
                path.write_bytes(updated.encode('utf-8'))
    return changed_paths, changed_references


def prune_empty_directories():
    removed = 0
    for scope in screenshot_scopes():
        for directory in sorted((p for p in scope.rglob('*') if p.is_dir()),
                                key=lambda p: len(p.parts), reverse=True):
            try:
                directory.rmdir()
                removed += 1
            except OSError:
                pass
        if scope.name != 'baseline-auth':
            try:
                scope.rmdir()
                removed += 1
            except OSError:
                pass
    return removed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true', help='move screenshots and rewrite their recorded paths')
    args = parser.parse_args()
    moves, unmapped = collect_moves()
    changed_paths, changed_references = rewrite_references(moves, apply=False)
    print(f'Would organize {len(moves)} screenshots into run-date/group/scope/case folders.')
    print(f'Would update evidence links in {len(changed_paths)} files ({changed_references} path references).')
    if unmapped:
        print(f'Unmapped screenshots: {len(unmapped)}')
        for path in unmapped[:12]:
            print('  ' + path.relative_to(ROOT).as_posix())
    if not args.apply:
        print('Review the mapping, then run with --apply to perform the migration.')
        return
    if unmapped:
        raise RuntimeError('Refusing to move screenshots without group/scope/case metadata.')
    for move in moves:
        move['destination'].parent.mkdir(parents=True, exist_ok=True)
        move['source'].rename(move['destination'])
    removed_directories = prune_empty_directories()
    actual_paths, actual_references = rewrite_references(moves, apply=True)
    print(f'Organized {len(moves)} screenshots; updated {len(actual_paths)} files ({actual_references} references).')
    print(f'PNG bytes were unchanged; removed {removed_directories} empty directories.')


if __name__ == '__main__':
    main()
