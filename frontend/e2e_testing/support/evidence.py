"""Shared date/group/scope/case paths for E2E screenshot evidence."""
import re
from pathlib import Path

LEGACY_CASES = {
    '01': 'AUTH-REG-01', '02': 'AUTH-REG-02', '03': 'AUTH-REG-03', '04': 'AUTH-REG-03',
    '05': 'AUTH-REG-03', '06': 'AUTH-LOGIN-01', '07': 'AUTH-VERIFY-01', '08': 'AUTH-REG-04',
    '09': 'AUTH-LOGIN-02', '10': 'AUTH-LOGIN-03', '11': 'AUTH-LOGIN-04', '12': 'AUTH-LOGIN-04',
    '13': 'AUTH-LOGIN-05', '14': 'AUTH-LOGIN-06', '15': 'AUTH-ADMIN-01',
    '16': 'AUTH-ADMIN-02', '17': 'AUTH-ADMIN-02', '18': 'AUTH-ADMIN-03',
}
RUN_ID = re.compile(r'^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})(?:\.(\d+))?Z?$')
SAFE_COMPONENT = re.compile(r'[^A-Za-z0-9._-]+')


def auth_scope(case_id):
    application = 'admin' if case_id.startswith('AUTH-ADMIN-') else 'user'
    if case_id.startswith('AUTH-REG-'):
        flow = 'registration'
    elif case_id.startswith('AUTH-VERIFY-'):
        flow = 'email_verification'
    elif case_id in ('AUTH-LOGIN-06', 'AUTH-ADMIN-03'):
        flow = 'logout'
    elif case_id == 'AUTH-ADMIN-01':
        flow = 'access_denied'
    else:
        flow = 'login'
    return application, 'authentication', flow


def auth_relative_path(case_id, filename):
    if not case_id or '/' in filename or '\\' in filename or Path(filename).name != filename:
        raise ValueError('Use a case ID and a plain screenshot filename.')
    application, _, _ = auth_scope(case_id)
    return Path('Auth') / application / case_id / screenshot_filename(application, case_id, Path(filename).stem)


def run_date_folder(run_id):
    match = RUN_ID.fullmatch(str(run_id))
    if not match:
        return SAFE_COMPONENT.sub('-', str(run_id)).strip('.-_')
    year, month, day, hour, minute, second, fraction = match.groups()
    suffix = '.' + fraction if fraction else ''
    return f'{year}-{month}-{day}_{hour}-{minute}-{second}{suffix}Z'


def evidence_group(suite, module, case_id):
    suite = (suite or '').lower()
    module = (module or '').lower().replace('-', '_')
    case_id = str(case_id or '')
    if module == 'cross_app' or case_id.startswith('X-'):
        return 'CrossApp'
    if case_id == 'A-MODERATION-APPROVE-01':
        return 'CrossApp'
    if case_id.startswith('AUTH-') or 'auth' in suite:
        return 'Auth'
    if 'ui-regression' in suite:
        return 'UIRegression'
    if 'rbac' in suite:
        return 'RBAC'
    if 'live' in suite:
        return 'Live'
    if 'account' in suite:
        return 'Account'
    return 'Web'


def evidence_scope(application, module, case_id):
    module = (module or '').lower().replace('-', '_')
    case_id = str(case_id or '')
    if (module == 'cross_app' or case_id.startswith('X-')
            or case_id in ('A-MODERATION-APPROVE-01',)
            or case_id.startswith('LIVE-MODERATION-')
            or (case_id.startswith('LIVE-ADMIN-VIDEO-')
                and case_id != 'LIVE-ADMIN-VIDEO-METADATA-SAVE')):
        return 'all'
    if application in ('admin', 'user'):
        return application
    if application == 'all':
        return 'all'
    raise ValueError('Screenshot scope must be admin, user or all.')


def screenshot_filename(application, case_id, step, viewport=None):
    """Build a short capture name below its run/group/scope/case folders."""
    if application not in ('user', 'admin', 'all'):
        raise ValueError('Application must be user, admin or all.')
    if not case_id or not step:
        raise ValueError('A case ID and capture step are required.')
    step = str(step)
    for prefix in (application + '-' + case_id + '-', case_id + '-'):
        if step.startswith(prefix):
            step = step[len(prefix):]
            break
    if not step:
        raise ValueError('A capture step is required after the case ID.')
    parts = []
    if viewport:
        parts.append(viewport)
    parts.append(step)
    safe = [SAFE_COMPONENT.sub('-', str(part)).strip('.-_') for part in parts]
    if any(not part for part in safe):
        raise ValueError('Screenshot filename contains an empty component.')
    return '-'.join(safe) + '.png'


def screenshot_path(run_id, suite, application, case_id, step, module=None, viewport=None):
    group = evidence_group(suite, module, case_id)
    scope = evidence_scope(application, module, case_id)
    return (Path('screenshots') / run_date_folder(run_id) / group / scope / str(case_id)
            / screenshot_filename(application, case_id, step, viewport))


def legacy_case(filename):
    match = re.search(r'AUTH-(?:REG|LOGIN|VERIFY|ADMIN)-\d{2}', filename)
    if match:
        return match.group(0)
    if filename.startswith('AUTH-') and filename.endswith('-failure.png'):
        return filename.removesuffix('-failure.png')
    return LEGACY_CASES.get(filename[:2])
