#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/scripts/service-common.sh"
if is_running; then
    printf 'Running (PID %s).\n' "$PID"
    ps -p "$PID" -o pid=,etime=,comm=
    printf 'Log: %s\n' "$LOG_FILE"
else
    printf '%s\n' 'Not running (or stale PID file).'
    exit 1
fi
