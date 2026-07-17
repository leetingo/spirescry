#!/usr/bin/env bash
# Public-behaviour regression tests for `./build.sh stop`.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/spirescry-stop-test.XXXXXX")"
fakebin="$scratch/bin"
mkdir -p "$fakebin"
trap 'jobs -pr | xargs kill -9 2>/dev/null || true; rm -rf "$scratch"' EXIT

# Keep these tests isolated from real hosts, games and bridge ports. `kill` is
# intentionally not stubbed: the observable contract includes process safety.
for command in pgrep pkill curl; do
    ln -s /usr/bin/false "$fakebin/$command"
done

run_stop() {
    PATH="$fakebin:$PATH" \
        TMPDIR="$scratch" \
        STS2_GAME_DIR="$scratch/no-game-here" \
        STS2_AGENT_PORT=1 \
        "$repo/build.sh" stop
}

assert_alive() {
    kill -0 "$1" 2>/dev/null || {
        echo "expected PID $1 to remain alive" >&2
        exit 1
    }
}

assert_dead() {
    ! kill -0 "$1" 2>/dev/null || {
        echo "expected PID $1 to be stopped" >&2
        exit 1
    }
}

pidfile="$scratch/spirescry-host.pid"

printf 'not-a-pid\n' > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "corrupt pidfile unexpectedly reported success: $output" >&2
    exit 1
fi
grep -q 'invalid host pidfile' <<<"$output"

sleep 60 &
unrelated_pid=$!
printf '%s\n' "$unrelated_pid" > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "reused PID unexpectedly reported success: $output" >&2
    exit 1
fi
assert_alive "$unrelated_pid"
grep -q 'does not belong to this host' <<<"$output"
kill "$unrelated_pid"
wait "$unrelated_pid" 2>/dev/null || true

# Even a new process with the right command must not inherit an old launch
# record for a recycled PID.
bash -c 'while :; do sleep 1; done' \
    "$repo/headless/Host/bin/Release/spirescry_host.dll" &
reused_host_pid=$!
sleep 0.1
printf '%s\n%s\n' "$reused_host_pid" 'an older process snapshot' > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "changed process identity unexpectedly reported success: $output" >&2
    exit 1
fi
assert_alive "$reused_host_pid"
grep -q 'was reused or restarted' <<<"$output"
kill "$reused_host_pid"
wait "$reused_host_pid" 2>/dev/null || true

# A valid host that ignores TERM must still be killed by its saved exact PID;
# the pgrep/pkill shims above prove escalation does not depend on enumeration.
ln -sf "$repo/tests/fixtures/pgrep-unavailable.sh" "$fakebin/pgrep"
bash -c 'trap "" TERM; while :; do sleep 1; done' \
    "$repo/headless/Host/bin/Release/spirescry_host.dll" &
host_pid=$!
sleep 0.1
printf '%s\n' "$host_pid" > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "snapshot-less host pidfile unexpectedly reported success: $output" >&2
    exit 1
fi
assert_alive "$host_pid"
grep -q 'has no saved process snapshot' <<<"$output"

host_snapshot="$(ps -p "$host_pid" -o lstart= -o command= 2>/dev/null \
    | sed -E 's/^[[:space:]]+//')"
printf '%s\n%s\n' "$host_pid" "$host_snapshot" > "$pidfile"
output="$(run_stop 2>&1)"
assert_dead "$host_pid"
grep -q 'stopped' <<<"$output"
[ ! -e "$pidfile" ]

ln -sf /usr/bin/false "$fakebin/pgrep"
printf '99999999\n' > "$pidfile"
output="$(run_stop 2>&1)"
grep -q 'nothing running' <<<"$output"
[ ! -e "$pidfile" ]

echo "build stop tests passed"
