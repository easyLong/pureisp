#!/usr/bin/env bash

SERVICE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
RUN_DIR="$SERVICE_DIR/run"
PID_FILE="$RUN_DIR/pureisp.pid"
LOG_FILE="$SERVICE_DIR/logs/pureisp.log"
MAIN_FILE="$SERVICE_DIR/src/main.js"

read_pid() {
    PID=''
    if [[ -f "$PID_FILE" ]]; then
        read -r PID < "$PID_FILE" || true
    fi
    [[ "$PID" =~ ^[0-9]+$ ]] && (( PID > 1 ))
}

is_running() {
    read_pid || return 1
    kill -0 "$PID" 2>/dev/null || return 1
    [[ "$(readlink -- "/proc/$PID/cwd" 2>/dev/null)" == "$SERVICE_DIR" ]] || return 1
    # Never signal an unrelated process if a stale PID has been reused.
    local argument
    while IFS= read -r -d '' argument; do
        if [[ "$argument" == "$MAIN_FILE" ]]; then
            return 0
        fi
    done < "/proc/$PID/cmdline" 2>/dev/null
    return 1
}

lock_service() {
    mkdir -p -- "$RUN_DIR"
    exec 9>"$RUN_DIR/control.lock"
    if ! flock -n 9; then
        printf '%s\n' 'Another start/stop operation is in progress.' >&2
        return 1
    fi
}

start_service() {
    if is_running; then
        printf 'Already running (PID %s).\n' "$PID"
        return 0
    fi
    command -v node >/dev/null || { printf '%s\n' 'Node.js 22+ is required.' >&2; return 1; }
    command -v setsid >/dev/null || { printf '%s\n' 'setsid is required (provided by util-linux).' >&2; return 1; }
    [[ -f "$SERVICE_DIR/.env" ]] || { printf '%s\n' 'Missing .env. Run npm run setup and configure it first.' >&2; return 1; }
    [[ -d "$SERVICE_DIR/node_modules/mysql2" ]] || { printf '%s\n' 'Dependencies are missing. Run npm ci first.' >&2; return 1; }
    mkdir -p -- "$SERVICE_DIR/logs"
    cd -- "$SERVICE_DIR"
    nohup setsid node --env-file=.env "$MAIN_FILE" </dev/null >>"$LOG_FILE" 2>&1 9>&- &
    printf '%s\n' "$!" > "$PID_FILE"
    sleep 2
    if ! is_running; then
        rm -f -- "$PID_FILE"
        printf 'Start failed. Recent log (%s):\n' "$LOG_FILE" >&2
        tail -n 20 -- "$LOG_FILE" >&2
        return 1
    fi
    printf 'Background process started (PID %s).\nLog: %s\nUse ./status.sh and ./logs.sh to check readiness.\n' "$PID" "$LOG_FILE"
}

stop_service() {
    if ! is_running; then
        rm -f -- "$PID_FILE"
        printf '%s\n' 'Not running.'
        return 0
    fi
    printf 'Stopping PID %s...\n' "$PID"
    kill -TERM "$PID"
    local attempt
    for attempt in {1..15}; do
        if ! is_running; then
            rm -f -- "$PID_FILE"
            printf '%s\n' 'Stopped.'
            return 0
        fi
        sleep 1
    done
    printf '%s\n' 'Still stopping after 15 seconds. Check ./logs.sh; no forced kill was sent.' >&2
    return 1
}
