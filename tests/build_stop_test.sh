#!/usr/bin/env bash
# Public-behaviour regression tests for `./build.sh stop`.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/spirescry-stop-test.XXXXXX")"
fakebin="$scratch/bin"
mkdir -p "$fakebin"
real_ps="$(command -v ps)"
host_dll="$repo/headless/Host/bin/Release/spirescry_host.dll"
created_host_dll=0

cleanup() {
    jobs -pr | xargs kill -9 2>/dev/null || true
    rm -rf "$scratch"
    [ "$created_host_dll" = 0 ] || rm -f "$host_dll"
}
trap cleanup EXIT

# CI runs this shell suite without the proprietary game assemblies needed to
# build Host. The launcher only needs the path to exist before our fake dotnet
# takes over, so provide a disposable ignored build artifact when necessary.
if [ ! -f "$host_dll" ]; then
    mkdir -p "$(dirname "$host_dll")"
    : > "$host_dll"
    created_host_dll=1
fi

timeout_host_pidfile="$scratch/timeout-host.pid"
timeout_port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')"

# A stand-in for `dotnet <host.dll>` that owns the requested port but never
# serves /health. This exercises the public launch/stop contract without a
# game install or a real bridge.
printf '%s\n' \
    '#!/usr/bin/env python3' \
    'import os, socket' \
    'sock = socket.socket()' \
    'sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)' \
    'sock.bind(("127.0.0.1", int(os.environ["STS2_AGENT_PORT"])))' \
    'sock.listen()' \
    'with open(os.environ["SPIRESCRY_TEST_HOST_PIDFILE"], "w") as pidfile:' \
    '    pidfile.write(str(os.getpid()))' \
    'while True:' \
    '    connection, _ = sock.accept()' \
    '    connection.close()' \
    > "$fakebin/dotnet"
chmod +x "$fakebin/dotnet"

# Collapse the normal 30-second health deadline to one polling interval.
printf '%s\n' '#!/bin/sh' 'printf "1\\n"' > "$fakebin/seq"
chmod +x "$fakebin/seq"

# Keep these tests isolated from real hosts, games and bridge ports. `kill` is
# intentionally not stubbed: the observable contract includes process safety.
for command in pgrep pkill curl; do
    ln -s /usr/bin/false "$fakebin/$command"
done
ln -s "$real_ps" "$fakebin/ps"

run_stop() {
    PATH="$fakebin:$PATH" \
        TMPDIR="$scratch" \
        STS2_GAME_DIR="$scratch/no-game-here" \
        STS2_AGENT_PORT=1 \
        "$repo/build.sh" stop
}

run_host_timeout() {
    PATH="$fakebin:$PATH" \
        TMPDIR="$scratch" \
        STS2_GAME_DIR="$scratch/no-game-here" \
        STS2_AGENT_PORT="$timeout_port" \
        SPIRESCRY_TEST_HOST_PIDFILE="$timeout_host_pidfile" \
        SPIRESCRY_TEST_REAL_PS="$real_ps" \
        SPIRESCRY_TEST_START_COUNT="$scratch/start-count" \
        SPIRESCRY_TEST_COMMAND_COUNT="$scratch/command-count" \
        "$repo/build.sh" host
}

# A bare `grep -q <<<"$output"` exits 1 under `set -e` having printed
# nothing at all, which is how a launch regression once reached CI as a
# blank failing step. Always show what was expected and what was captured.
assert_contains() {
    grep -q "$3" <<<"$2" || {
        echo "expected $1 to contain: $3" >&2
        echo "--- captured output ---" >&2
        echo "$2" >&2
        echo "--- end ---" >&2
        exit 1
    }
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

# A launch that reaches its bridge deadline must not return control while its
# unmanaged child still owns a process or a listening port. A subsequent stop
# is intentionally harmless and reports that launch cleanup already finished.
if output="$(run_host_timeout 2>&1)"; then
    echo "bridge-less host unexpectedly reported success: $output" >&2
    exit 1
fi
assert_contains 'bridge timeout message' "$output" 'bridge not up after 30s'
[ -s "$timeout_host_pidfile" ] || {
    echo "bridge-less host did not expose its test PID" >&2
    exit 1
}
timeout_host_pid="$(cat "$timeout_host_pidfile")"
timeout_failure=0
if kill -0 "$timeout_host_pid" 2>/dev/null; then
    echo "bridge timeout left host PID $timeout_host_pid running" >&2
    timeout_failure=1
fi
if ! python3 -c 'import socket, sys; s=socket.socket(); s.bind(("127.0.0.1", int(sys.argv[1]))); s.close()' "$timeout_port"; then
    echo "bridge timeout left port $timeout_port occupied" >&2
    timeout_failure=1
fi
stop_output="$(run_stop 2>&1)"
assert_contains 'stop after a reclaimed launch' "$stop_output" 'nothing running'
if kill -0 "$timeout_host_pid" 2>/dev/null; then
    kill -KILL "$timeout_host_pid" 2>/dev/null || true
    timeout_failure=1
fi
[ "$timeout_failure" = 0 ] || exit 1

# A backgrounded child does not become the host at fork: until nohup execs
# it still carries this script's own argv. Launch must wait that window out
# instead of concluding the PID it just forked is a stranger — doing so
# aborted the launch AND left the child running, which is how this suite
# once failed in CI having printed nothing at all. Report the parent's argv
# for the first samples, then the truth.
rm -f "$timeout_host_pidfile" "$scratch/command-count" "$fakebin/ps"
printf '%s\n' \
    '#!/bin/sh' \
    'case "$*" in' \
    '    *command=*)' \
    '        count=0' \
    '        [ ! -f "$SPIRESCRY_TEST_COMMAND_COUNT" ] || read -r count < "$SPIRESCRY_TEST_COMMAND_COUNT"' \
    '        count=$((count + 1))' \
    '        printf "%s\\n" "$count" > "$SPIRESCRY_TEST_COMMAND_COUNT"' \
    '        if [ "$count" -le 3 ]; then' \
    '            printf "Mon Jan  1 00:00:00 2026 /bin/bash build.sh host\\n"' \
    '            exit 0' \
    '        fi' \
    '        exec "$SPIRESCRY_TEST_REAL_PS" "$@"' \
    '        ;;' \
    '    *) exec "$SPIRESCRY_TEST_REAL_PS" "$@" ;;' \
    'esac' \
    > "$fakebin/ps"
chmod +x "$fakebin/ps"

if preexec_output="$(run_host_timeout 2>&1)"; then
    echo "pre-exec host unexpectedly reported success: $preexec_output" >&2
    exit 1
fi
# Reaching the bridge deadline is the proof: the launch identified its child
# instead of dying in the pre-exec window.
assert_contains 'pre-exec launch' "$preexec_output" 'bridge not up after 30s'
[ -s "$timeout_host_pidfile" ] || {
    echo "pre-exec host did not expose its test PID" >&2
    exit 1
}
preexec_pid="$(cat "$timeout_host_pidfile")"
if kill -0 "$preexec_pid" 2>/dev/null; then
    kill -KILL "$preexec_pid" 2>/dev/null || true
    echo "pre-exec launch left host PID $preexec_pid running" >&2
    exit 1
fi
rm -f "$fakebin/ps"
ln -s "$real_ps" "$fakebin/ps"

# A matching command is not enough identity: the kernel may recycle the PID
# for a new invocation of that same command. Let both launch-time reads and
# the timeout's pre-snapshot read agree, then make the post-snapshot identity
# differ while the real process and command remain unchanged.
rm -f "$timeout_host_pidfile" "$scratch/start-count" "$fakebin/ps"
printf '%s\n' \
    '#!/bin/sh' \
    'case "$*" in' \
    '    *command=*) exec "$SPIRESCRY_TEST_REAL_PS" "$@" ;;' \
    '    *lstart=*)' \
    '        count=0' \
    '        [ ! -f "$SPIRESCRY_TEST_START_COUNT" ] || read -r count < "$SPIRESCRY_TEST_START_COUNT"' \
    '        count=$((count + 1))' \
    '        printf "%s\\n" "$count" > "$SPIRESCRY_TEST_START_COUNT"' \
    '        if [ "$count" -gt 3 ]; then' \
    '            printf "Mon Jan  1 00:00:00 2099\\n"' \
    '            exit 0' \
    '        fi' \
    '        exec "$SPIRESCRY_TEST_REAL_PS" "$@"' \
    '        ;;' \
    '    *) exec "$SPIRESCRY_TEST_REAL_PS" "$@" ;;' \
    'esac' \
    > "$fakebin/ps"
chmod +x "$fakebin/ps"

if reused_output="$(run_host_timeout 2>&1)"; then
    echo "start-reused host unexpectedly reported success: $reused_output" >&2
    exit 1
fi
[ -s "$timeout_host_pidfile" ] || {
    echo "start-reused host did not expose its test PID" >&2
    exit 1
}
reused_timeout_pid="$(cat "$timeout_host_pidfile")"
assert_alive "$reused_timeout_pid"
if ! grep -q 'start identity changed' <<<"$reused_output"; then
    kill -KILL "$reused_timeout_pid" 2>/dev/null || true
    echo "start identity change was not reported: $reused_output" >&2
    exit 1
fi
kill -KILL "$reused_timeout_pid" 2>/dev/null || true
rm -f "$fakebin/ps"
ln -s "$real_ps" "$fakebin/ps"

printf 'not-a-pid\n' > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "corrupt pidfile unexpectedly reported success: $output" >&2
    exit 1
fi
assert_contains 'corrupt pidfile report' "$output" 'invalid host pidfile'

sleep 60 &
unrelated_pid=$!
printf '%s\n' "$unrelated_pid" > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "reused PID unexpectedly reported success: $output" >&2
    exit 1
fi
assert_alive "$unrelated_pid"
assert_contains 'reused PID report' "$output" 'does not belong to this host'

# An environment that cannot inspect a live PID must fail honestly: it may
# neither discard the launch record nor guess that the process has exited.
ln -sf "$repo/tests/fixtures/pgrep-unavailable.sh" "$fakebin/ps"
printf '%s\n%s\n' "$unrelated_pid" 'unverifiable snapshot' > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "uninspectable PID unexpectedly reported success: $output" >&2
    exit 1
fi
assert_alive "$unrelated_pid"
[ -e "$pidfile" ]
assert_contains 'uninspectable PID report' "$output" 'cannot inspect PID'
ln -sf "$real_ps" "$fakebin/ps"

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
assert_contains 'changed identity report' "$output" 'was reused or restarted'
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
assert_contains 'snapshot-less pidfile report' "$output" 'has no saved process snapshot'

host_snapshot="$(ps -p "$host_pid" -o lstart= -o command= 2>/dev/null \
    | sed -E 's/^[[:space:]]+//')"
printf '%s\n%s\n' "$host_pid" "$host_snapshot" > "$pidfile"
output="$(run_stop 2>&1)"
assert_dead "$host_pid"
assert_contains 'successful stop report' "$output" 'stopped'
[ ! -e "$pidfile" ]

ln -sf /usr/bin/false "$fakebin/pgrep"
printf '99999999\n' > "$pidfile"
output="$(run_stop 2>&1)"
assert_contains 'stale pidfile report' "$output" 'nothing running'
[ ! -e "$pidfile" ]

echo "build stop tests passed"
