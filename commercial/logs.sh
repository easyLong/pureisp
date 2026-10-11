#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/scripts/service-common.sh"
if [[ ! -f "$LOG_FILE" ]]; then
    printf '%s\n' 'No log yet. Start the service with ./start.sh first.' >&2
    exit 1
fi
tail -n 100 -f -- "$LOG_FILE"
