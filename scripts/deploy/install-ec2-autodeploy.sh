#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 || $# -ne 1 ]]; then
  echo 'Usage: sudo bash scripts/deploy/install-ec2-autodeploy.sh /absolute/path/to/checkout' >&2
  exit 1
fi

repo_dir=$(realpath -- "$1")
run_user=$(stat -c '%U' "$repo_dir/.git")
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
test -d "$repo_dir/.git"
test -f "$repo_dir/.env.ec2-api"
test -f "$repo_dir/recommendation-service/.env.ec2"
command -v python3 >/dev/null
command -v docker >/dev/null
command -v runuser >/dev/null
docker compose version >/dev/null

install -d -m 0755 /usr/local/lib/dalitu-autodeploy
install -d -m 0700 /var/lib/dalitu-autodeploy
install -m 0755 "$script_dir/ec2_autodeploy.py" /usr/local/lib/dalitu-autodeploy/ec2_autodeploy.py

# Host configuration and application secrets stay on the EC2 host.
if [[ ! -e /etc/dalitu-autodeploy.json ]]; then
  python3 - "$repo_dir" "$run_user" <<'PY'
import json
import os
import sys
from pathlib import Path
config = {
    "repository": "DuyLinh0212/Dalitu",
    "repo_dir": sys.argv[1], "run_user": sys.argv[2],
    "project_name": "hutube-ec2", "state_dir": "/var/lib/dalitu-autodeploy",
    "api_url": "http://127.0.0.1:5080",
    "recommendation_url": "http://127.0.0.1:8000",
    "public_api_url": "https://api.dalitu.app",
    "public_recommendation_url": "https://recommendation.dalitu.app",
    "required_statuses": ["Vercel – dalitu", "Vercel – dalitu-admin"],
}
path = Path('/etc/dalitu-autodeploy.json')
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as stream:
    json.dump(config, stream, indent=2)
    stream.write('\n')
PY
fi

# Migrate the former GitHub repository name while preserving host-specific settings.
python3 - "$repo_dir" "$run_user" <<'PY'
import json
import os
import subprocess
import sys
from pathlib import Path

repo_dir = Path(sys.argv[1]).resolve()
run_user = sys.argv[2]
config_path = Path('/etc/dalitu-autodeploy.json')
config = json.loads(config_path.read_text(encoding='utf-8'))
old_repo = 'DuyLinh0212/HuTube'
canonical_repo = 'DuyLinh0212/Dalitu'
if config.get('repository') not in {old_repo, canonical_repo}:
    raise SystemExit('Unexpected auto-deploy repository; refusing to change it')
if Path(config.get('repo_dir', '')).resolve() != repo_dir:
    raise SystemExit('Auto-deploy checkout differs from the configured checkout')

old_remote = 'https://github.com/' + old_repo + '.git'
canonical_remote = 'https://github.com/' + canonical_repo + '.git'
remote = subprocess.run(
    ['runuser', '-u', run_user, '--', 'git', '-C', str(repo_dir), 'remote', 'get-url', 'origin'],
    check=True, capture_output=True, text=True,
).stdout.strip()
if remote not in {old_remote, canonical_remote}:
    raise SystemExit('Unexpected Git origin; refusing to change it')

config['repository'] = canonical_repo
temporary = config_path.with_name(config_path.name + '.tmp')
fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, 'w', encoding='utf-8') as stream:
    json.dump(config, stream, indent=2)
    stream.write('\n')
os.replace(temporary, config_path)

if remote != canonical_remote:
    subprocess.run(
        ['runuser', '-u', run_user, '--', 'git', '-C', str(repo_dir),
         'remote', 'set-url', 'origin', canonical_remote],
        check=True,
    )
PY

cat > /etc/systemd/system/dalitu-autodeploy.service <<'UNIT'
[Unit]
Description=Deploy Dalitu main after GitHub CI and Vercel checks pass
Wants=network-online.target
After=network-online.target docker.service
Requires=docker.service

[Service]
Type=oneshot
User=root
UMask=0077
ExecStart=/usr/bin/python3 /usr/local/lib/dalitu-autodeploy/ec2_autodeploy.py
TimeoutStartSec=50min
TimeoutStopSec=7min
KillMode=mixed
# runuser must switch to the checkout owner for Git operations.
NoNewPrivileges=false
PrivateTmp=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
UNIT

cat > /etc/systemd/system/dalitu-autodeploy.timer <<'UNIT'
[Unit]
Description=Check verified Dalitu main updates every five minutes

[Timer]
OnBootSec=2min
OnUnitInactiveSec=5min
RandomizedDelaySec=15s
Unit=dalitu-autodeploy.service

[Install]
WantedBy=timers.target
UNIT

systemctl daemon-reload
systemctl enable --now dalitu-autodeploy.timer
echo 'Dalitu autodeploy timer enabled. Inspect with: sudo journalctl -u dalitu-autodeploy.service'
