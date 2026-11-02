#!/usr/bin/env bash
# Run as root inside Ubuntu WSL. Secrets live only in the ignored env file.
set -euo pipefail
env_file="${1:?Pass the absolute WSL path to .env.messaging.local}"
export DEBIAN_FRONTEND=noninteractive
if ! command -v rabbitmq-server >/dev/null; then
    apt-get update -qq
    apt-get install -y rabbitmq-server
fi
python3 - "$env_file" <<'PY'
import os, secrets, sys
from pathlib import Path
p=Path(sys.argv[1])
if not p.exists():
    password=secrets.token_urlsafe(32)
    p.write_text('RABBITMQ_USER=dalitu\nRABBITMQ_PASSWORD='+password+'\nRabbitMq__Enabled=true\nRabbitMq__HostName=127.0.0.1\nRabbitMq__Port=5672\nRabbitMq__UserName=dalitu\nRabbitMq__Password='+password+'\nRabbitMq__VirtualHost=dalitu\n')
os.chmod(p,0o600)
PY
set -a
source "$env_file"
set +a
if ! rabbitmq-diagnostics -q ping >/dev/null 2>&1; then
    if [ -f /etc/rabbitmq/rabbitmq.conf ]; then
        cp -p /etc/rabbitmq/rabbitmq.conf /etc/rabbitmq/rabbitmq.conf.before-dalitu
    fi
    cat > /etc/rabbitmq/rabbitmq.conf <<'CONF'
listeners.tcp.1 = 127.0.0.1:5672
management.tcp.ip = 127.0.0.1
management.tcp.port = 15672
vm_memory_high_watermark.absolute = 192MiB
disk_free_limit.absolute = 1GB
consumer_timeout = 3600000
CONF
    rabbitmq-plugins enable --offline rabbitmq_management
    export RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS="+S 2:2"
    runuser -u rabbitmq -- rabbitmq-server -detached
    for attempt in $(seq 1 30); do
        if rabbitmq-diagnostics -q check_running >/dev/null 2>&1; then break; fi
        sleep 1
    done
    rabbitmq-diagnostics -q check_running
fi
rabbitmqctl add_vhost dalitu >/dev/null
if ! rabbitmqctl list_users -q | cut -f1 | grep -Fxq "$RABBITMQ_USER"; then
    rabbitmqctl add_user "$RABBITMQ_USER" "$RABBITMQ_PASSWORD" >/dev/null
else
    rabbitmqctl change_password "$RABBITMQ_USER" "$RABBITMQ_PASSWORD" >/dev/null
fi
rabbitmqctl set_user_tags "$RABBITMQ_USER" administrator >/dev/null
rabbitmqctl set_permissions -p dalitu "$RABBITMQ_USER" '.*' '.*' '.*' >/dev/null
rabbitmq-diagnostics -q check_running
rabbitmqctl version
