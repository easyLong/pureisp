#!/usr/bin/env bash
# Integration smoke test: isolated file store, random local port, no real .env.
set -euo pipefail
SOURCE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
TEST_ROOT="$(mktemp -d)"
TEST_DIR="$TEST_ROOT/service with spaces"
UNRELATED_PID=''
cleanup() {
    if [[ -d "$TEST_DIR" ]]; then bash "$TEST_DIR/stop.sh" >/dev/null 2>&1 || true; fi
    if [[ -n "$UNRELATED_PID" ]]; then kill "$UNRELATED_PID" 2>/dev/null || true; fi
    # TEST_ROOT is created by mktemp above, never derived from user input.
    rm -rf -- "$TEST_ROOT"
}
trap cleanup EXIT
mkdir -- "$TEST_DIR"
cp -R -- "$SOURCE_DIR/src" "$SOURCE_DIR/scripts" "$SOURCE_DIR/public" "$TEST_DIR/"
cp -- "$SOURCE_DIR/package.json" "$SOURCE_DIR/package-lock.json" "$SOURCE_DIR/"*.sh "$TEST_DIR/"
(cd -- "$TEST_DIR" && npm ci --ignore-scripts --no-audit --no-fund)
TEST_PORT="$(node -e 'const s=require("net").createServer();s.listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close();});')"
cat > "$TEST_DIR/.env" <<EOF
HOST=127.0.0.1
PORT=$TEST_PORT
ACCOUNT_STORE=file
SESSION_SECRET=1111111111111111111111111111111111111111111111111111111111111111
ADMIN_KEY=2222222222222222222222222222222222222222222222222222222222222222
CARD_ENCRYPTION_KEY=3333333333333333333333333333333333333333333333333333333333333333
RENEWAL_URL=https://example.com/
EOF

if bash "$TEST_DIR/status.sh"; then printf '%s\n' 'Expected stopped status.' >&2; exit 1; fi
bash "$TEST_DIR/start.sh"
FIRST_PID="$(cat "$TEST_DIR/run/pureisp.pid")"
# The Node process must have its own session and no controlling terminal.
[[ "$(ps -p "$FIRST_PID" -o sid= | tr -d ' ')" == "$FIRST_PID" ]]
[[ "$(ps -p "$FIRST_PID" -o tty= | tr -d ' ')" == '?' ]]
bash "$TEST_DIR/start.sh"
[[ "$(cat "$TEST_DIR/run/pureisp.pid")" == "$FIRST_PID" ]]
bash "$TEST_DIR/status.sh"
node --input-type=module -e '
  const response = await fetch("http://127.0.0.1:" + process.argv[1] + "/health", { signal: AbortSignal.timeout(5000) });
  const data = await response.json();
  if (!response.ok || data.service !== "pureisp-account-service" || data.databaseAvailable !== true) process.exit(1);
' "$TEST_PORT"
if timeout 1 bash "$TEST_DIR/logs.sh" > "$TEST_ROOT/log-output.txt"; then
    printf '%s\n' 'Expected log follow to wait for more output.' >&2
    exit 1
else
    [[ "$?" == 124 ]]
fi
grep -q 'listening on' "$TEST_ROOT/log-output.txt"
kill -0 "$FIRST_PID"
bash "$TEST_DIR/restart.sh"
[[ "$(cat "$TEST_DIR/run/pureisp.pid")" != "$FIRST_PID" ]]
bash "$TEST_DIR/stop.sh"
bash "$TEST_DIR/stop.sh"
if bash "$TEST_DIR/status.sh"; then printf '%s\n' 'Expected stopped status.' >&2; exit 1; fi

sleep 120 &
UNRELATED_PID="$!"
printf '%s\n' "$UNRELATED_PID" > "$TEST_DIR/run/pureisp.pid"
bash "$TEST_DIR/stop.sh"
kill -0 "$UNRELATED_PID"
printf '%s\n' "$UNRELATED_PID" > "$TEST_DIR/run/pureisp.pid"
bash "$TEST_DIR/start.sh"
kill -0 "$UNRELATED_PID"
bash "$TEST_DIR/stop.sh"

sed -i 's|RENEWAL_URL=.*|RENEWAL_URL=http://0.0.0.0/renew|' "$TEST_DIR/.env"
if bash "$TEST_DIR/start.sh"; then printf '%s\n' 'Expected startup configuration failure.' >&2; exit 1; fi
[[ ! -f "$TEST_DIR/run/pureisp.pid" ]]
grep -q 'RENEWAL_URL requires HTTPS' "$TEST_DIR/logs/pureisp.log"
printf '%s\n' 'PASS: detached background start, duplicate start, status, HTTP health, log follow, restart, graceful stop, stale PID protection and failed startup.'
