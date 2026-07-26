#!/usr/bin/env bash
# Public-behaviour regression tests for the `./build.sh` launchers and
# `./build.sh stop`.

set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/spirescry-stop-test.XXXXXX")"
fakebin="$scratch/bin"
mkdir -p "$fakebin"
real_ps="$(command -v ps)"
real_curl="$(command -v curl)"
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
# game install or a real bridge. It records its PID before touching the port,
# so "the launcher never started me" stays distinguishable from "I started and
# could not bind".
printf '%s\n' \
    '#!/usr/bin/env python3' \
    'import os, socket' \
    'with open(os.environ["SPIRESCRY_TEST_HOST_PIDFILE"], "w") as pidfile:' \
    '    pidfile.write(str(os.getpid()))' \
    'sock = socket.socket()' \
    'sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)' \
    'sock.bind(("127.0.0.1", int(os.environ["STS2_AGENT_PORT"])))' \
    'sock.listen()' \
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
for command in pgrep pkill; do
    ln -s /usr/bin/false "$fakebin/$command"
done
# curl must fail the way an unheld port fails — exit 7, connection refused. A
# stub that exits 1 instead reads as "something is on the port that does not
# speak HTTP", which is exactly what the launcher's port guard refuses; every
# launch case below would abort before starting anything.
printf '%s\n' '#!/bin/sh' 'exit 7' > "$fakebin/curl"
chmod +x "$fakebin/curl"
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
        "$repo/build.sh" host
}

# A bare `grep -q` under `set -e` fails the suite with no output at all, leaving
# the gate to report only an exit code. The message the launcher actually
# printed is the first thing anyone debugging that needs.
assert_says() {
    grep -q "$1" <<<"$2" || {
        printf 'expected output matching: %s\ngot:\n%s\n' "$1" "$2" >&2
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
assert_says 'bridge not up after 30s' "$output"
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
assert_says 'nothing running' "$stop_output"
if kill -0 "$timeout_host_pid" 2>/dev/null; then
    kill -KILL "$timeout_host_pid" 2>/dev/null || true
    timeout_failure=1
fi
[ "$timeout_failure" = 0 ] || exit 1

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
assert_says 'invalid host pidfile' "$output"

sleep 60 &
unrelated_pid=$!
printf '%s\n' "$unrelated_pid" > "$pidfile"
if output="$(run_stop 2>&1)"; then
    echo "reused PID unexpectedly reported success: $output" >&2
    exit 1
fi
assert_alive "$unrelated_pid"
assert_says 'does not belong to this host' "$output"

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
assert_says 'cannot inspect PID' "$output"
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
assert_says 'was reused or restarted' "$output"
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
assert_says 'has no saved process snapshot' "$output"

host_snapshot="$(ps -p "$host_pid" -o lstart= -o command= 2>/dev/null \
    | sed -E 's/^[[:space:]]+//')"
printf '%s\n%s\n' "$host_pid" "$host_snapshot" > "$pidfile"
output="$(run_stop 2>&1)"
assert_dead "$host_pid"
assert_says 'stopped' "$output"
[ ! -e "$pidfile" ]

ln -sf /usr/bin/false "$fakebin/pgrep"
printf '99999999\n' > "$pidfile"
output="$(run_stop 2>&1)"
assert_says 'nothing running' "$output"
[ ! -e "$pidfile" ]

# ---------- `./build.sh headless`: owning the child, verifying the bridge ----

# The engine launcher talks to a real bridge over a real port, so this section
# needs the real curl back. pgrep stays stubbed out: `stop` must reclaim the
# launched child from its own launch record, not by rediscovering it.
ln -sf "$real_curl" "$fakebin/curl"

game_dir="$scratch/game"
headless_tmp="$scratch/headless"
game_child_pidfile="$scratch/game-child.pid"
game_pidfile="$headless_tmp/spirescry-game.pid"
mkdir -p "$game_dir" "$headless_tmp"
headless_port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')"

# A stand-in for the game binary: it records its PID, then serves the /health
# body it was handed — or, with none, never brings a bridge up at all.
printf '%s\n' \
    '#!/usr/bin/env python3' \
    'import os, time' \
    'from http.server import BaseHTTPRequestHandler, HTTPServer' \
    'with open(os.environ["SPIRESCRY_TEST_GAME_PIDFILE"], "w") as handle:' \
    '    handle.write(str(os.getpid()))' \
    'health = os.environ.get("SPIRESCRY_TEST_HEALTH", "").encode()' \
    'if not health:' \
    '    while True:' \
    '        time.sleep(1)' \
    'class Health(BaseHTTPRequestHandler):' \
    '    def do_GET(self):' \
    '        self.send_response(200)' \
    '        self.send_header("Content-Type", "application/json")' \
    '        self.send_header("Content-Length", str(len(health)))' \
    '        self.end_headers()' \
    '        self.wfile.write(health)' \
    '    def log_message(self, *args):' \
    '        pass' \
    'HTTPServer(("127.0.0.1", int(os.environ["STS2_AGENT_PORT"])), Health).serve_forever()' \
    > "$scratch/bridge-stub.py"
chmod +x "$scratch/bridge-stub.py"
cp "$scratch/bridge-stub.py" "$game_dir/SlayTheSpire2"

# A port can be held by something that never completes an HTTP exchange at all.
# This one accepts and answers with bytes that are not a response, which is what
# a wrong-protocol server or a half-open proxy looks like from the launcher.
printf '%s\n' \
    '#!/usr/bin/env python3' \
    'import os, socket' \
    'sock = socket.socket()' \
    'sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)' \
    'sock.bind(("127.0.0.1", int(os.environ["STS2_AGENT_PORT"])))' \
    'sock.listen()' \
    'while True:' \
    '    connection, _ = sock.accept()' \
    '    try:' \
    '        connection.recv(4096)' \
    '        connection.sendall(b"GARBAGE NOT HTTP\\r\\n")' \
    '    except OSError:' \
    '        pass' \
    '    connection.close()' \
    > "$scratch/raw-squatter.py"

# One polling interval is too tight for a bridge that has to start a process
# first; five keeps the deadline cases quick without racing the success case.
printf '%s\n' '#!/bin/sh' 'printf "1\\n2\\n3\\n4\\n5\\n"' > "$fakebin/seq"
chmod +x "$fakebin/seq"

checkout_stamp="$("$repo/build.sh" stamp)"

health_json() {
    printf '{"ok":true,"mod":"%s","version":"0.1.0","buildHash":"%s","protocolVersion":1}' \
        "$1" "$2"
}

run_headless() {
    PATH="$fakebin:$PATH" \
        TMPDIR="$headless_tmp" \
        STS2_GAME_DIR="$game_dir" \
        STS2_AGENT_PORT="$headless_port" \
        SPIRESCRY_TEST_GAME_PIDFILE="$game_child_pidfile" \
        SPIRESCRY_TEST_HEALTH="${health_body:-}" \
        "$repo/build.sh" headless
}

run_game_stop() {
    PATH="$fakebin:$PATH" \
        TMPDIR="$headless_tmp" \
        STS2_GAME_DIR="$game_dir" \
        STS2_AGENT_PORT="$headless_port" \
        "$repo/build.sh" stop
}

# Both launchers share the port guard, so it is exercised through both. The
# host's stand-in child is the fake dotnet above, which records its PID before
# it reaches for the port — an absent record therefore means the guard stopped
# the launch, not that a spawned child failed to bind.
host_child_pidfile="$scratch/host-child.pid"

run_host_on_headless_port() {
    PATH="$fakebin:$PATH" \
        TMPDIR="$headless_tmp" \
        STS2_GAME_DIR="$scratch/no-game-here" \
        STS2_AGENT_PORT="$headless_port" \
        SPIRESCRY_TEST_HOST_PIDFILE="$host_child_pidfile" \
        "$repo/build.sh" host
}

# start_squatter <health-body>: hold the bridge port from outside the launcher.
start_squatter() {
    SPIRESCRY_TEST_GAME_PIDFILE="$scratch/squatter.pid" \
        SPIRESCRY_TEST_HEALTH="$1" \
        STS2_AGENT_PORT="$headless_port" \
        python3 "$scratch/bridge-stub.py" &
    squatter_pid=$!
    for _ in $(seq 1 50); do
        if curl -sf "http://127.0.0.1:$headless_port/health" >/dev/null 2>&1; then
            return 0
        fi
        sleep 0.1
    done
    echo "squatter bridge never came up on port $headless_port" >&2
    exit 1
}

# start_raw_squatter: hold the bridge port with a listener that answers with
# bytes that are not an HTTP response.
start_raw_squatter() {
    STS2_AGENT_PORT="$headless_port" python3 "$scratch/raw-squatter.py" &
    squatter_pid=$!
    for _ in $(seq 1 50); do
        if python3 -c 'import socket, sys
probe = socket.socket()
sys.exit(probe.connect_ex(("127.0.0.1", int(sys.argv[1]))) != 0)' "$headless_port"; then
            return 0
        fi
        sleep 0.1
    done
    echo "raw squatter never bound port $headless_port" >&2
    exit 1
}

stop_squatter() {
    kill -KILL "$squatter_pid" 2>/dev/null || true
    wait "$squatter_pid" 2>/dev/null || true
}

assert_no_game_child() {
    [ ! -e "$game_child_pidfile" ] || {
        echo "$1" >&2
        exit 1
    }
}

# assert_game_child_reclaimed: the launcher must not return while the child it
# started still runs, and a launch that never reached a bridge must not leave a
# launch record claiming otherwise. Callers clear both paths beforehand, so what
# is under test is that the failure path wrote neither — `stop` would go on to
# trust that record.
assert_game_child_reclaimed() {
    [ -s "$game_child_pidfile" ] || {
        echo "the game stand-in never recorded its PID" >&2
        exit 1
    }
    reclaimed_pid="$(cat "$game_child_pidfile")"
    if kill -0 "$reclaimed_pid" 2>/dev/null; then
        kill -KILL "$reclaimed_pid" 2>/dev/null || true
        echo "$1 left game PID $reclaimed_pid running" >&2
        exit 1
    fi
    [ ! -e "$game_pidfile" ] || {
        echo "$1 left a launch record at $game_pidfile" >&2
        exit 1
    }
}

# A bridge already on the port is never this launch's child, whoever built it.
rm -f "$game_child_pidfile"
start_squatter "$(health_json spirescry "$checkout_stamp")"
if output="$(run_headless 2>&1)"; then
    stop_squatter
    echo "headless launch onto an occupied port reported success: $output" >&2
    exit 1
fi
stop_squatter
assert_says 'already answers on port' "$output"
assert_no_game_child "headless launch started the game despite a live bridge"

# Same port, something that is not a bridge at all.
start_squatter '{"ok":true,"service":"something else"}'
if output="$(run_headless 2>&1)"; then
    stop_squatter
    echo "headless launch onto a foreign server reported success: $output" >&2
    exit 1
fi
stop_squatter
assert_says 'is not a spirescry bridge' "$output"
assert_no_game_child "headless launch started the game despite a foreign server"

# An occupant that never completes an HTTP exchange is an occupant all the same.
# curl reports it as a transport failure rather than a reply, and a guard that
# recognised only clean replies would wave the launch through onto a port its
# child cannot bind — then blame the health deadline for the silence.
rm -f "$game_child_pidfile"
start_raw_squatter
if output="$(run_headless 2>&1)"; then
    stop_squatter
    echo "headless launch onto a non-HTTP occupant reported success: $output" >&2
    exit 1
fi
stop_squatter
assert_says 'does not answer /health' "$output"
assert_no_game_child "headless launch started the game despite an occupied port"

# The host launcher runs the same guard, and must reject the same occupants.
rm -f "$host_child_pidfile"
start_squatter "$(health_json spirescry "$checkout_stamp")"
if output="$(run_host_on_headless_port 2>&1)"; then
    stop_squatter
    echo "host launch onto an occupied port reported success: $output" >&2
    exit 1
fi
stop_squatter
assert_says 'already answers on port' "$output"
[ ! -e "$host_child_pidfile" ] || {
    echo "host launch started its child despite a live bridge" >&2
    exit 1
}

# A launch that reaches its bridge deadline owns the cleanup of its own child.
rm -f "$game_child_pidfile" "$game_pidfile"
health_body=""
if output="$(run_headless 2>&1)"; then
    echo "bridge-less headless launch reported success: $output" >&2
    exit 1
fi
assert_says 'bridge not up after 60s' "$output"
assert_game_child_reclaimed "the headless bridge deadline"

# An HTTP 2xx is not identity: a bridge from another build answers /health
# exactly like this checkout's would.
rm -f "$game_child_pidfile" "$game_pidfile"
health_body="$(health_json spirescry 0000000.000000000000)"
if output="$(run_headless 2>&1)"; then
    echo "headless launch accepted a foreign build: $output" >&2
    exit 1
fi
assert_says "this checkout is $checkout_stamp" "$output"
assert_game_child_reclaimed "the foreign-build launch"

# The successful path: this checkout's bridge answers, and the launch records
# the exact child that serves it — enough for `stop` to reclaim it with no
# process enumeration at all.
rm -f "$game_child_pidfile" "$game_pidfile"
health_body="$(health_json spirescry "$checkout_stamp")"
output="$(run_headless 2>&1)"
assert_says 'bridge up' "$output"
[ -s "$game_child_pidfile" ] || {
    echo "the game stand-in never recorded its PID" >&2
    exit 1
}
launched_game_pid="$(cat "$game_child_pidfile")"
[ -s "$game_pidfile" ] || {
    kill -KILL "$launched_game_pid" 2>/dev/null || true
    echo "a successful headless launch wrote no launch record at $game_pidfile" >&2
    exit 1
}
IFS= read -r recorded_game_pid < "$game_pidfile"
[ "$recorded_game_pid" = "$launched_game_pid" ] || {
    kill -KILL "$launched_game_pid" 2>/dev/null || true
    echo "launch recorded PID $recorded_game_pid, but the game runs as $launched_game_pid" >&2
    exit 1
}
recorded_game_snapshot="$(sed -n '2p' "$game_pidfile")"
live_game_snapshot="$(ps -p "$launched_game_pid" -o lstart= -o command= 2>/dev/null \
    | sed -E 's/^[[:space:]]+//')"
[ "$recorded_game_snapshot" = "$live_game_snapshot" ] || {
    kill -KILL "$launched_game_pid" 2>/dev/null || true
    echo "launch record snapshot does not match the running game" >&2
    exit 1
}

# An unrelated PID in the launch record is still just a claim.
sleep 60 &
unrelated_game_pid=$!
printf '%s\n%s\n' "$unrelated_game_pid" 'a snapshot of something else' > "$game_pidfile"
if output="$(run_game_stop 2>&1)"; then
    kill -KILL "$launched_game_pid" 2>/dev/null || true
    echo "a foreign PID in the game launch record was accepted: $output" >&2
    exit 1
fi
assert_alive "$unrelated_game_pid"
assert_says 'does not belong to this game' "$output"
kill "$unrelated_game_pid"
wait "$unrelated_game_pid" 2>/dev/null || true

printf '%s\n%s\n' "$launched_game_pid" "$recorded_game_snapshot" > "$game_pidfile"
output="$(run_game_stop 2>&1)"
assert_says 'stopped' "$output"
assert_dead "$launched_game_pid"
[ ! -e "$game_pidfile" ]

echo "build launch and stop tests passed"
