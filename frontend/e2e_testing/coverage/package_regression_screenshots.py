"""Copy CI UI-regression screenshots into the shared date/group/scope/case tree."""
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from support.evidence import run_date_folder, screenshot_path

SOURCES = (
    ('artifacts/chunk-recovery', 'admin', 'ADMIN-CHUNK-RECOVERY'),
    ('artifacts/watch-recommendations', 'user', 'USER-WATCH-RECOMMENDATIONS'),
    ('artifacts/channel-watch-scroll', 'user', 'USER-CHANNEL-WATCH-SCROLL'),
    ('artifacts/player-hourly', 'user', 'USER-PLAYER-HOURLY-COMMENTS'),
)


def main():
    latest_path = ROOT / 'artifacts/web-latest.json'
    if not latest_path.is_file():
        print('No Web run found; no UI-regression screenshots to package.')
        return
    run_dir = Path(json.loads(latest_path.read_text(encoding='utf-8'))['run_dir'])
    run_id = run_dir.name
    copied = 0
    for relative_source, application, case_id in SOURCES:
        source = ROOT / relative_source
        if not source.is_dir():
            continue
        for image in sorted(source.rglob('*.png')):
            destination = ROOT / screenshot_path(
                run_id, 'ui-regression', application, case_id, image.stem, 'ui_regression')
            destination.parent.mkdir(parents=True, exist_ok=True)
            if destination.exists():
                if destination.read_bytes() == image.read_bytes():
                    continue
                raise FileExistsError(f'Refusing to overwrite screenshot: {destination}')
            shutil.copyfile(image, destination)
            copied += 1
    print(f'Packaged {copied} UI-regression screenshots under screenshots/{run_date_folder(run_id)}.')


if __name__ == '__main__':
    main()
