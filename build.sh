#!/usr/bin/env bash
# Build + deploy for spirescry.
#
# Subcommands:
#   libs         copy sts2.dll + GodotSharp.dll from the game install → lib/
#   mod          build src/Spirescry.csproj (Release) → src/bin/Release/spirescry.dll
#   cli          build cli/ (cargo --release)             → cli/target/release/spirescry
#   all          mod + cli
#   deploy-mod   cp spirescry.dll + manifest → "$STS2_GAME_DIR/mods/"
#   deploy-cli   cp spirescry → "$SPIRESCRY_CLI_BIN/" (default: ~/.local/bin)
#   deploy       deploy-mod + deploy-cli
#   headless     launch the game with no window; waits until this checkout's
#                bridge answers, and owns the child it started
#   headless-setup  one-time: copy deps, IL-patch sts2.dll, extract loc, build host
#   host         run the pure .NET host — no game binary, no Steam
#                (--foreground: exec in this process; for sandboxed
#                executors that reap background children)
#   verify       conformance: tests/parity.py on both boots + key-set diff
#   gate         the pre-merge gate: the CI set plus the whole e2e suite CI
#                cannot run — exhaustive content sweeps included, never
#                --quick (needs headless-setup; port via SPIRESCRY_GATE_PORT)
#   stamp        print the buildHash this checkout would stamp (git ref +
#                content hash of the source trees and every lib/*.dll)
#   stop         stop a running game or host
#
# Env (overridable):
#   STS2_GAME_DIR      path to ".../SlayTheSpire2.app/Contents/MacOS"
#                      (auto-detected on macOS; required elsewhere)
#   SPIRESCRY_CLI_BIN  where to install the spirescry binary
#   STS2_AGENT_PORT    bridge port the headless health wait polls (default 7777)
#   STS2_AGENT_HTTP_LOG  1 → launched bridge logs one line per request (passes through)

set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO"

if [ -z "${STS2_GAME_DIR:-}" ] && [ "$(uname -s)" = "Darwin" ]; then
    STS2_GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS"
fi
: "${SPIRESCRY_CLI_BIN:=$HOME/.local/bin}"
# Default must match the bridge's own fallback (HttpBridge.StartFromEnv)
# and the CLI's clap default.
: "${STS2_AGENT_PORT:=7777}"

HOST_DLL="$REPO/headless/Host/bin/Release/spirescry_host.dll"

# How long a launcher waits (in 0.1s polls) for the child it forked to exec its
# real binary and become identifiable. Generous: the cost of a slow exec is a
# fraction of a second here, while giving up early strands the child.
LAUNCH_IDENTIFY_TRIES=20

# Stamped into both builds; /health reports it as buildHash so a running
# host can be matched to its build inputs. A git ref alone cannot do
# that: it misses source edits made after the build (dirty or not) and a
# Steam-updated sts2.dll, so the stamp also hashes the binary inputs —
# every tracked + untracked (non-ignored) file under the source trees,
# every dll under lib/ (the compile base), and every third-party dll
# under headless/build/lib (0Harmony and friends, which the host
# compiles against and loads). sts2.headless.dll is excluded as derived:
# it is produced by the (hashed) Patcher sources from the (hashed)
# lib/sts2.dll. Extracted localization tables are likewise derived game
# data outside the stamp. `./build.sh stamp` prints the value the
# current checkout would produce; comparing it to a running host's
# buildHash verifies those inputs byte-for-byte.
#
# Computed lazily at every build point — never cached at script start:
# `libs` / `headless-setup` refresh lib/*.dll first, and a stamp taken
# before the refresh would brand the fresh binary with the old dll hash
# (instantly "stale" to the host check).
if command -v shasum >/dev/null 2>&1; then HASH_CMD="shasum -a 256"; else HASH_CMD="sha256sum"; fi

content_stamp() {
    {
        # Release artifact inputs only: cli/tests is compiled by cargo test,
        # while protocol_generator.rs is a module of the release build script.
        git ls-files -co -z --exclude-standard -- \
            src headless cli/src cli/build.rs cli/protocol_generator.rs \
            cli/Cargo.toml cli/Cargo.lock \
            protocol.json mods build.sh \
            | LC_ALL=C sort -z | xargs -0 $HASH_CMD
        for dll in lib/*.dll headless/build/lib/*.dll; do
            case "$dll" in */sts2.headless.dll) continue ;; esac
            if [ -f "$dll" ]; then $HASH_CMD "$dll"; fi
        done
    } 2>/dev/null | $HASH_CMD | cut -c1-12
}

current_stamp() {
    local git_hash=unknown content=unknown
    if command -v git >/dev/null 2>&1 &&
       git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        git_hash="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
        if [ -n "$(git status --porcelain --untracked-files=all 2>/dev/null)" ]; then
            git_hash="$git_hash-dirty"
        fi
        content="$(content_stamp)"
    fi
    printf '%s.%s\n' "$git_hash" "$content"
}

step() { printf '\033[1;34m▶\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m✓\033[0m %s\n' "$*"; }
die()  { printf '\033[1;31m✗\033[0m %s\n' "$*" >&2; exit 1; }

need_game_dir() { [ -n "${STS2_GAME_DIR:-}" ] || die "STS2_GAME_DIR not set and game install not auto-detected"; }

# /health reports mod and buildHash as flat string fields, so one sed per
# field reads them without a JSON parser.
health_string_field() {
    printf '%s' "$1" \
        | sed -n 's/.*"'"$2"'"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p'
}

# An HTTP 2xx proves only that *something* listens on the port: a bridge left
# over from another checkout — or from a build made before the last edit —
# answers /health exactly like the one we just launched, and reporting it as
# success hands the agent a host that does not run this code. buildHash is the
# stamp over every source and dll input (see current_stamp), so comparing it to
# the checkout's own stamp settles the question by value.
#
# bridge_identity_fault <health-body> <fix-hint>: prints why the answering
# bridge is not this checkout's and returns 1; silent and 0 when it is.
bridge_identity_fault() {
    local body="$1" hint="$2" mod hash expected
    mod="$(health_string_field "$body" mod)"
    hash="$(health_string_field "$body" buildHash)"
    if [ "$mod" != spirescry ]; then
        printf 'port %s answers /health but not as a spirescry bridge (mod "%s")\n' \
            "$STS2_AGENT_PORT" "$mod"
        return 1
    fi
    if [[ ! "$hash" =~ ^[0-9a-f]{7,40}(-dirty)?\.[0-9a-f]{12}$ ]]; then
        printf 'bridge on port %s reports build "%s", not a build.sh stamp — %s\n' \
            "$STS2_AGENT_PORT" "$hash" "$hint"
        return 1
    fi
    expected="$(current_stamp)"
    if [ "$hash" != "$expected" ]; then
        printf 'bridge on port %s is build %s, this checkout is %s — %s\n' \
            "$STS2_AGENT_PORT" "$hash" "$expected" "$hint"
        return 1
    fi
    return 0
}

# Nothing may hold the bridge port when a launch starts: the child would fail
# to bind, and the health wait would then greet the squatter as the bridge it
# asked for. Only a refused connection (curl exit 7) proves the port is free;
# absence is the special case here, not the default. Everything else means
# something is there — it answered (0), sent non-HTTP bytes (1), hung up or
# truncated the reply (18, 52, 56), or stalled (28) — and enumerating just the
# ones we happen to have seen would let the rest through as "free", spawning a
# child that cannot bind and then blaming the health deadline for it.
require_free_bridge_port() {
    local body status=0 mod hash
    command -v curl >/dev/null 2>&1 \
        || die "curl not found — cannot check whether port $STS2_AGENT_PORT is free"
    body="$(curl -s --max-time 5 "http://127.0.0.1:$STS2_AGENT_PORT/health" 2>/dev/null)" \
        || status=$?
    case "$status" in
        0) ;;
        7) return 0 ;;
        *)
            die "port $STS2_AGENT_PORT is held by something that does not answer /health (curl exit $status) — stop it, or launch on another STS2_AGENT_PORT" ;;
    esac
    mod="$(health_string_field "$body" mod)"
    hash="$(health_string_field "$body" buildHash)"
    [ "$mod" = spirescry ] && \
        die "a spirescry bridge (build ${hash:-unknown}) already answers on port $STS2_AGENT_PORT — ./build.sh stop first, or launch on another STS2_AGENT_PORT"
    die "port $STS2_AGENT_PORT is already served by something that is not a spirescry bridge — free it, or launch on another STS2_AGENT_PORT"
}

# wait_bridge <timeout_s> <log> <fix-hint>: poll /health until *this
# checkout's* bridge answers. A foreign or stale bridge is a hard failure, not
# something to keep waiting on.
wait_bridge() {
    local body fault
    for _ in $(seq 1 "$1"); do
        if body="$(curl -sf "http://127.0.0.1:$STS2_AGENT_PORT/health" 2>/dev/null)"; then
            fault="$(bridge_identity_fault "$body" "$3")" || die "$fault"
            ok "bridge up — try: spirescry obs"
            return
        fi
        sleep 1
    done
    die "bridge not up after ${1}s (see $2)"
}

# Godot puts the .NET assemblies in a data_sts2_* dir: next to the game
# binary on Windows/Linux, under Contents/Resources on macOS.
find_data_dir() {
    need_game_dir
    candidates="$(find "$STS2_GAME_DIR" "$STS2_GAME_DIR/../Resources" -maxdepth 1 -type d -name 'data_sts2_*' 2>/dev/null || true)"
    data_dir="$(grep -m1 "$(uname -m)" <<<"$candidates" || head -n 1 <<<"$candidates")"
    [ -n "$data_dir" ] || die "no data_sts2_* dir found near $STS2_GAME_DIR"
    [ -f "$data_dir/sts2.dll" ] || die "no sts2.dll in $data_dir"
}

build_libs() {
    find_data_dir
    step "copy game dlls → lib/"
    mkdir -p lib
    cp "$data_dir/sts2.dll" "$data_dir/GodotSharp.dll" lib/
    ok "lib/sts2.dll + lib/GodotSharp.dll (from $data_dir)"
}

# Prepare the pure .NET host: third-party deps + IL-patched sts2 +
# localization tables from the .pck, then build the host itself.
headless_setup() {
    find_data_dir
    # The game auto-updates on Steam; a stale lib/sts2.dll compiles fine
    # but skews from the runtime dll we patch below (MissingMethod at
    # runtime). Refresh lib/ so compile base == runtime, always.
    if ! cmp -s "$data_dir/sts2.dll" lib/sts2.dll 2>/dev/null; then
        build_libs
        echo "  (lib/ refreshed — rerun './build.sh mod deploy-mod' for the in-game mod)"
    fi
    libdir="headless/build/lib"
    mkdir -p "$libdir"

    step "copy third-party dlls → $libdir/"
    for dll in Steamworks.NET.dll SmartFormat.dll SmartFormat.ZString.dll Sentry.dll \
               MonoMod.Backports.dll MonoMod.ILHelpers.dll 0Harmony.dll System.IO.Hashing.dll; do
        [ -f "$data_dir/$dll" ] || die "missing $dll in $data_dir"
        cp "$data_dir/$dll" "$libdir/"
    done
    ok "8 dlls"

    step "IL-patch sts2.dll → $libdir/sts2.headless.dll"
    dotnet run --project headless/Patcher -c Release --verbosity minimal -- \
        "$data_dir/sts2.dll" "$libdir/sts2.headless.dll"

    # LocManager normally reads the tables via res:// from the .pck, which
    # doesn't resolve without the engine — extract them to disk.
    pck="$(dirname "$data_dir")/Slay the Spire 2.pck"
    if [ -f "$pck" ] && command -v python3 >/dev/null 2>&1; then
        step "extract localization from pck"
        rm -rf "$libdir/localization" "$libdir/_pck"
        python3 headless/extract_pck_localization.py "$pck" "$libdir/_pck" "res://localization/" >/dev/null
        if [ -d "$libdir/_pck/localization" ]; then
            mv "$libdir/_pck/localization" "$libdir/localization"
            ok "$(find "$libdir/localization" -name '*.json' | wc -l | tr -d ' ') tables"
        else
            echo "  (no tables extracted; text falls back to entry keys)"
        fi
        rm -rf "$libdir/_pck"
    fi

    step "build host"
    # Stamp taken now — after the lib/ and dependency refreshes above.
    dotnet build -c Release -p:SourceRevisionId="spirescry.$(current_stamp)" \
        headless/Host/Host.csproj --nologo --verbosity minimal
    [ -x headless/Host/bin/Release/spirescry_host ] || die "host build produced no binary"
    ok "headless/Host/bin/Release/spirescry_host"
}

# Keep exactly one previous generation: debugging "it worked last boot"
# needs last boot's log, and `>` used to destroy it at relaunch.
rotate_log() {
    [ -f "$1" ] && mv -f "$1" "$1.1"
    return 0
}

# A PID alone is not an identity: after a process exits, the kernel can reuse
# it for an unrelated program. Capture both start time and command, and treat a
# zombie as already stopped. All signals below are gated by this snapshot.
read_process_fields() {
    local pid value value_status
    pid="$1"
    shift
    value=""
    if value="$(ps -p "$pid" "$@" 2>&1)"; then
        value="$(printf '%s\n' "$value" | sed -E 's/^[[:space:]]+//')"
        [ -n "$value" ] || return 2
        printf '%s\n' "$value"
        return 0
    else
        value_status=$?
    fi
    # BSD/GNU ps use 1 for a well-formed query that selected no process.
    # Invocation/permission failures use another status and stay unknown.
    [ "$value_status" = 1 ] && return 1
    return 2
}

process_snapshot() {
    read_process_fields "$1" -o lstart= -o command=
}

# The command may legitimately change in-place when a launcher shell execs
# dotnet. Process start time does not, so keep it as the launch identity that
# survives that transition and detects a recycled PID running the same command.
process_start_identity() {
    read_process_fields "$1" -o lstart=
}

process_state() {
    local state state_status
    state=""
    if state="$(ps -p "$1" -o stat= 2>&1)"; then
        state="$(printf '%s\n' "$state" | sed -E 's/^[[:space:]]+//')"
        if [ -z "$state" ]; then
            printf 'unknown\n'
        elif [[ "$state" = Z* ]]; then
            printf 'dead\n'
        else
            printf 'live\n'
        fi
        return 0
    else
        state_status=$?
    fi
    if [ "$state_status" = 1 ]; then
        printf 'dead\n'
    else
        printf 'unknown\n'
    fi
}

process_is_same() {
    local observed_state current current_status
    observed_state="$(process_state "$1")"
    [ "$observed_state" = unknown ] && return 2
    [ "$observed_state" = live ] || return 1
    current=""
    current_status=0
    current="$(process_snapshot "$1")" || current_status=$?
    [ "$current_status" = 2 ] && return 2
    [ "$current_status" = 0 ] && [ "$current" = "$2" ]
}

is_this_host_snapshot() {
    # Spaces on both sides make this an argument match, not a loose substring
    # such as /tmp/not-spirescry_host.dll.backup.
    case " $1 " in
        *" $HOST_DLL "*) return 0 ;;
        *) return 1 ;;
    esac
}

is_this_game_snapshot() {
    case "$1" in
        *"$STS2_GAME_DIR/"*) return 0 ;;
        *) return 1 ;;
    esac
}

# stop_exact_process <pid> <captured-snapshot> <label>
#
# Re-check the snapshot before every escalation so a PID recycled between
# TERM and KILL can never make us kill its new owner.
stop_exact_process() {
    target_pid="$1"
    target_snapshot="$2"
    target_label="$3"

    same_status=0
    process_is_same "$target_pid" "$target_snapshot" || same_status=$?
    [ "$same_status" = 2 ] && \
        die "cannot inspect $target_label PID $target_pid safely — refusing to signal it"
    [ "$same_status" = 0 ] || return 0
    kill -TERM "$target_pid" 2>/dev/null || true
    sleep 1
    same_status=0
    process_is_same "$target_pid" "$target_snapshot" || same_status=$?
    [ "$same_status" = 2 ] && \
        die "cannot re-check $target_label PID $target_pid after SIGTERM"
    if [ "$same_status" = 0 ]; then
        kill -KILL "$target_pid" 2>/dev/null || true
        sleep 1
    fi
    same_status=0
    process_is_same "$target_pid" "$target_snapshot" || same_status=$?
    [ "$same_status" = 2 ] && \
        die "cannot re-check $target_label PID $target_pid after SIGKILL"
    [ "$same_status" = 0 ] && \
        die "$target_label PID $target_pid survived SIGKILL (permissions?)"
    return 0
}

# sample_child_identity <pid>: read the identity of a child a launcher started.
# Sets CHILD_START_IDENTITY (start time — the one field that cannot change in
# place) and CHILD_SNAPSHOT (start time + command). The start time is read on
# both sides of the command read, so a PID recycled mid-sample surfaces here
# instead of passing as our child. Returns:
#   0 live and consistently sampled     1 gone
#   2 cannot be inspected safely        3 live, but the start time moved
sample_child_identity() {
    local pid="$1" before after before_status=0 after_status=0 snapshot_status=0
    CHILD_START_IDENTITY=""
    CHILD_SNAPSHOT=""
    before="$(process_start_identity "$pid")" || before_status=$?
    CHILD_SNAPSHOT="$(process_snapshot "$pid")" || snapshot_status=$?
    after="$(process_start_identity "$pid")" || after_status=$?
    case "$(process_state "$pid")" in
        unknown) return 2 ;;
        dead)    return 1 ;;
    esac
    [ "$before_status" = 0 ] && [ "$after_status" = 0 ] && [ "$snapshot_status" = 0 ] \
        || return 2
    [ "$before" = "$after" ] || return 3
    CHILD_START_IDENTITY="$before"
    return 0
}

# supervise_launch <pid> <label> <snapshot-predicate> <timeout_s> <log>
#                  <pidfile> <fix-hint>
#
# Own the child a launcher just started: identify it, wait for this checkout's
# bridge, and — when that bridge never arrives — reclaim exactly that child
# instead of leaving it holding the port. On success, the PID and its stable
# post-boot snapshot land in <pidfile> so `stop` signals the same process
# without rediscovering it. Returns 1 when the bridge never came up; the child
# is already reclaimed by then.
#
# Ownership reaches exactly one process: the child this launcher forked. Both
# launch targets are the bridge-hosting process itself — `dotnet <host.dll>`,
# and the game's Mach-O/ELF binary, which Godot runs in-process — so that child
# is the thing holding the port. A launcher that forked a grandchild and exited
# would leave nothing safe to signal: the direct child is gone by the first ps
# sample, and its grandchild is not identifiable as ours. That case fails loudly
# ("could not be identified") rather than guessing at a PID to kill — but a
# child that is still live under the start identity we forked is reclaimed
# first, identified or not.
supervise_launch() {
    local pid="$1" label="$2" predicate="$3" deadline="$4" log="$5" pidfile="$6" hint="$7"
    local launch_identity="" sample_status=0 pidtmp attempt=0 identified=0

    # The child we just forked is still this shell until it execs, so its first
    # ps command line is the launcher's own — a race, not an answer. Poll until
    # the exec lands (LAUNCH_IDENTIFY_TRIES × 0.1s) before ruling on identity.
    while :; do
        sample_status=0
        sample_child_identity "$pid" || sample_status=$?
        [ "$sample_status" = 0 ] || break
        [ -n "$launch_identity" ] || launch_identity="$CHILD_START_IDENTITY"
        # A start time that moved means this PID is no longer the child we
        # forked; nothing here is ours to wait on or to signal.
        [ "$CHILD_START_IDENTITY" = "$launch_identity" ] || break
        if "$predicate" "$CHILD_SNAPSHOT"; then
            identified=1
            break
        fi
        attempt=$((attempt + 1))
        [ "$attempt" -lt "$LAUNCH_IDENTIFY_TRIES" ] || break
        sleep 0.1
    done
    if [ "$identified" = 0 ]; then
        # We forked this PID ourselves and its start time never moved, so it is
        # still ours to reclaim. Dying with it alive would leave it to exec and
        # take the port — the leak this launcher exists to prevent.
        if [ "$sample_status" = 0 ] && [ "$CHILD_START_IDENTITY" = "$launch_identity" ]; then
            stop_exact_process "$pid" "$CHILD_SNAPSHOT" "unidentified $label"
        fi
        die "launched $label PID $pid could not be identified — see $log, and check port $STS2_AGENT_PORT is free before relaunching"
    fi

    # wait_bridge calls die on deadline. Run it in a subshell so the launcher
    # can still reclaim the exact child it started before returning failure.
    if ! (wait_bridge "$deadline" "$log" "$hint"); then
        sample_status=0
        sample_child_identity "$pid" || sample_status=$?
        case "$sample_status" in
            1) ;;  # already exited — nothing of ours is left to reclaim
            2) die "bridge did not come up and $label PID $pid cannot be inspected safely" ;;
            3) die "bridge did not come up and $label PID $pid start identity changed — refusing to signal it" ;;
            *)
                [ "$CHILD_START_IDENTITY" = "$launch_identity" ] || \
                    die "bridge did not come up and $label PID $pid start identity changed — refusing to signal it"
                "$predicate" "$CHILD_SNAPSHOT" || \
                    die "bridge did not come up and PID $pid no longer belongs to this $label"
                stop_exact_process "$pid" "$CHILD_SNAPSHOT" "abandoned $label"
                ;;
        esac
        return 1
    fi

    # The child may still be the forked shell at the first ps sample and exec
    # its real binary a moment later without changing PID or start time.
    # Persist the stable, post-boot command so stop does not mistake that exec
    # for PID reuse.
    sample_status=0
    sample_child_identity "$pid" || sample_status=$?
    [ "$sample_status" = 0 ] \
        && [ "$CHILD_START_IDENTITY" = "$launch_identity" ] \
        && "$predicate" "$CHILD_SNAPSHOT" || \
        die "booted $label PID $pid could not be identified"
    pidtmp="$(mktemp "${pidfile}.XXXXXX")"
    printf '%s\n%s\n' "$pid" "$CHILD_SNAPSHOT" > "$pidtmp"
    mv -f "$pidtmp" "$pidfile"
}

# Run the host: game logic from the IL-patched sts2.dll inside a plain
# .NET process — no game binary, no Godot engine, no Steam.
#
# --foreground execs the host in this process. Sandboxed executors (CI,
# agent runners) reap nohup'd children with their parent shell; a
# foreground host lives exactly as long as its own terminal/task. Its
# output still tees into the log file, so diagnostics survive the
# terminal — the tee children hang off the host process and exit with it.
launch_host() {
    [ -f "$HOST_DLL" ] || die "host not built — run: ./build.sh headless-setup"
    ! pgrep -qf spirescry_host || die "host already running — ./build.sh stop first"
    require_free_bridge_port
    log="${TMPDIR:-/tmp}/spirescry-host.log"
    rotate_log "$log"
    # Through the dotnet CLI, not the apphost — the CLI resolves its own
    # runtime regardless of DOTNET_ROOT.
    if [ "${1:-}" = "--foreground" ]; then
        step "launch host, foreground (bridge port $STS2_AGENT_PORT, log $log)"
        exec dotnet "$HOST_DLL" \
            > >(tee -a "$log") 2> >(tee -a "$log" >&2)
    fi
    pidfile="${TMPDIR:-/tmp}/spirescry-host.pid"
    step "launch host (bridge port $STS2_AGENT_PORT, log $log)"
    nohup dotnet "$HOST_DLL" > "$log" 2>&1 &
    host_pid=$!
    supervise_launch "$host_pid" host is_this_host_snapshot 30 "$log" "$pidfile" \
        "rebuild this checkout's host with ./build.sh headless-setup"
}

build_mod() {
    [ -f lib/sts2.dll ] || die "lib/sts2.dll missing — run: ./build.sh libs"
    step "build mod (Release)"
    # Stamp taken now, so a preceding `libs` refresh in the same
    # invocation is already reflected.
    dotnet build -c Release -p:SourceRevisionId="spirescry.$(current_stamp)" \
        src/Spirescry.csproj --nologo --verbosity minimal
    [ -f src/bin/Release/spirescry.dll ] || die "mod build did not produce spirescry.dll"
    ok "src/bin/Release/spirescry.dll"
}

build_cli() {
    step "build cli (cargo --release)"
    (cd cli && cargo build --release --quiet)
    [ -x cli/target/release/spirescry ] || die "cli build did not produce spirescry"
    ok "cli/target/release/spirescry"
}

deploy_mod() {
    [ -f src/bin/Release/spirescry.dll ] || die "mod not built; run: ./build.sh mod"
    need_game_dir
    [ -d "$STS2_GAME_DIR/mods" ] || die "no mods dir under $STS2_GAME_DIR"
    step "deploy mod → $STS2_GAME_DIR/mods/"
    cp src/bin/Release/spirescry.dll "$STS2_GAME_DIR/mods/spirescry.dll"
    cp mods/spirescry.json "$STS2_GAME_DIR/mods/spirescry.json"
    ok "deployed"
}

deploy_cli() {
    [ -x cli/target/release/spirescry ] || die "cli not built; run: ./build.sh cli"
    mkdir -p "$SPIRESCRY_CLI_BIN"
    # macOS AMFI SIGKILLs linker-signed binaries copied across paths
    # (exit 137, silent). Replace the linker signature on the release
    # artifact before copying so the installed binary remains valid and
    # byte-identical — the play skill's pre-flight can then compare hashes.
    if [ "$(uname -s)" = "Darwin" ] && command -v codesign >/dev/null 2>&1; then
        codesign --force --sign - cli/target/release/spirescry >/dev/null 2>&1
    fi
    step "deploy cli → $SPIRESCRY_CLI_BIN/spirescry"
    cp cli/target/release/spirescry "$SPIRESCRY_CLI_BIN/spirescry"
    ok "deployed"
}

# Godot's --headless display server skips rendering but the scene tree,
# signals, and frame loop still run — the bridge drives the same UI paths
# it does with a window. Steam must be running (the game requires it).
launch_headless() {
    need_game_dir
    # -perm -u+x, not +111: BSD-only mode syntax made this find fail outright
    # on GNU findutils, where "no game binary" is a lie about the install.
    game_bin="$(find "$STS2_GAME_DIR" -maxdepth 1 -type f -perm -u+x | head -n 1)"
    [ -n "$game_bin" ] || die "no game binary found in $STS2_GAME_DIR"
    ! pgrep -qf "$STS2_GAME_DIR" || die "game already running — ./build.sh stop first"
    require_free_bridge_port

    log="${TMPDIR:-/tmp}/spirescry-headless.log"
    pidfile="${TMPDIR:-/tmp}/spirescry-game.pid"
    rotate_log "$log"
    step "launch headless (bridge port $STS2_AGENT_PORT, log $log)"
    nohup "$game_bin" --headless > "$log" 2>&1 &
    game_pid=$!
    # The bridge here is the deployed mod, not this checkout's build tree, so
    # a stale deploy is the identity mismatch to expect.
    supervise_launch "$game_pid" game is_this_game_snapshot 60 "$log" "$pidfile" \
        "redeploy this checkout's mod with ./build.sh mod deploy-mod"
}

# stop_recorded_process <pidfile> <label> <snapshot-predicate>
#
# Stop the process a launch recorded, or refuse and say why. A launch record is
# a claim about a PID, not proof: the PID may have been recycled, or the file
# may predate a restart, so both the live command and the saved snapshot must
# still agree before anything is signalled. Returns 0 when the recorded process
# was stopped, 1 when there was nothing to stop.
stop_recorded_process() {
    local pidfile="$1" label="$2" predicate="$3"
    local recorded_pid saved_snapshot current_snapshot current_state
    local current_snapshot_status=0

    [ -f "$pidfile" ] || return 1
    IFS= read -r recorded_pid < "$pidfile" || recorded_pid=""
    if [[ ! "$recorded_pid" =~ ^[0-9]+$ ]] || [ "${#recorded_pid}" -gt 10 ] || [ "$recorded_pid" -le 1 ]; then
        die "invalid $label pidfile $pidfile — refusing to signal anything"
    fi

    saved_snapshot="$(sed -n '2p' "$pidfile")"
    current_snapshot="$(process_snapshot "$recorded_pid")" || current_snapshot_status=$?
    current_state="$(process_state "$recorded_pid")"
    if [ "$current_state" = unknown ]; then
        die "cannot inspect PID $recorded_pid in $pidfile — refusing to signal or discard it"
    elif [ "$current_state" = dead ]; then
        # A dead PID is an ordinary stale file and is safe to clean up.
        rm -f "$pidfile"
        return 1
    elif [ "$current_snapshot_status" != 0 ]; then
        die "cannot read identity for live PID $recorded_pid in $pidfile — refusing to signal it"
    elif ! "$predicate" "$current_snapshot"; then
        die "PID $recorded_pid in $pidfile does not belong to this $label — refusing to signal it"
    elif [ -z "$saved_snapshot" ]; then
        die "$label pidfile $pidfile has no saved process snapshot — refusing to signal PID $recorded_pid"
    elif [ "$saved_snapshot" != "$current_snapshot" ]; then
        die "PID $recorded_pid in $pidfile was reused or restarted — refusing to signal it"
    fi
    stop_exact_process "$recorded_pid" "$current_snapshot" "$label"
    rm -f "$pidfile"
    return 0
}

# Kill by PID file first (works where sandboxes hide other processes from
# pgrep), then use pgrep only to discover candidates and validate every PID
# before signalling it. Exit non-zero whenever "nothing running" cannot be
# established honestly.
stop_game() {
    need_game_dir
    stopped=0
    enumeration_failed=0
    if stop_recorded_process "${TMPDIR:-/tmp}/spirescry-host.pid" host is_this_host_snapshot; then
        stopped=1
    fi
    if stop_recorded_process "${TMPDIR:-/tmp}/spirescry-game.pid" game is_this_game_snapshot; then
        stopped=1
    fi

    # Foreground hosts, and anything started outside these launchers, leave no
    # launch record. pgrep is only a discovery aid: broad pkill patterns never
    # receive a signal directly.
    if command -v pgrep >/dev/null 2>&1; then
        host_pgrep_status=0
        host_pids="$(pgrep -f spirescry_host 2>/dev/null)" || host_pgrep_status=$?
        if [ "$host_pgrep_status" = 0 ]; then
            for candidate_pid in $host_pids; do
                candidate_snapshot_status=0
                candidate_snapshot="$(process_snapshot "$candidate_pid")" || candidate_snapshot_status=$?
                candidate_state="$(process_state "$candidate_pid")"
                if [ "$candidate_state" = unknown ] \
                    || { [ "$candidate_state" = live ] && [ "$candidate_snapshot_status" != 0 ]; }; then
                    enumeration_failed=1
                elif [ "$candidate_state" = live ] && is_this_host_snapshot "$candidate_snapshot"; then
                    stop_exact_process "$candidate_pid" "$candidate_snapshot" "host"
                    stopped=1
                fi
            done
        elif [ "$host_pgrep_status" -gt 1 ]; then
            enumeration_failed=1
        fi
        game_pgrep_status=0
        game_pids="$(pgrep -f "$STS2_GAME_DIR" 2>/dev/null)" || game_pgrep_status=$?
        if [ "$game_pgrep_status" = 0 ]; then
            for candidate_pid in $game_pids; do
                candidate_snapshot_status=0
                candidate_snapshot="$(process_snapshot "$candidate_pid")" || candidate_snapshot_status=$?
                candidate_state="$(process_state "$candidate_pid")"
                if [ "$candidate_state" = unknown ] \
                    || { [ "$candidate_state" = live ] && [ "$candidate_snapshot_status" != 0 ]; }; then
                    enumeration_failed=1
                elif [ "$candidate_state" = live ] && is_this_game_snapshot "$candidate_snapshot"; then
                    stop_exact_process "$candidate_pid" "$candidate_snapshot" "game"
                    stopped=1
                fi
            done
        elif [ "$game_pgrep_status" -gt 1 ]; then
            enumeration_failed=1
        fi
    else
        enumeration_failed=1
    fi

    if curl -sf "http://127.0.0.1:$STS2_AGENT_PORT/health" > /dev/null 2>&1; then
        die "a bridge still answers on port $STS2_AGENT_PORT — kill it manually (permissions?)"
    fi
    if [ "$stopped" = 0 ] && [ "$enumeration_failed" = 1 ]; then
        die "could not enumerate processes and no valid launch record was available"
    fi
    if [ "$stopped" = 1 ]; then ok "stopped"; else ok "nothing running"; fi
}

# Conformance: run tests/parity.py once per boot, then compare the
# recorded snapshot key sets — same phase must expose the same keys in
# both modes. Engine leg needs Steam and a deployed, current mod.
verify() {
    kh="${TMPDIR:-/tmp}/spirescry-parity-host.json"
    ke="${TMPDIR:-/tmp}/spirescry-parity-engine.json"
    parity_seed="${SPIRESCRY_PARITY_SEED:-SPIRECI1}"
    stop_game

    step "verify: host boot (seed $parity_seed)"
    launch_host
    python3 tests/parity.py --seed "$parity_seed" --keys-out "$kh" || {
        stop_game
        die "host parity run failed"
    }
    stop_game

    step "verify: engine-headless boot (seed $parity_seed)"
    launch_headless
    python3 tests/parity.py --seed "$parity_seed" --keys-out "$ke" || {
        stop_game
        die "engine parity run failed"
    }
    stop_game

    step "verify: cross-mode key sets"
    python3 tests/parity.py --compare "$kh" "$ke"
    ok "both boots pass, snapshots agree"
}

# Everything that must pass before a merge: the GitHub-hosted CI set plus the
# end-to-end suite. CI cannot run e2e — the host is built from the game's
# non-distributable dlls — so this is the only place those cases are exercised,
# the exhaustive content sweeps among them. Needs ./build.sh headless-setup
# to have been run once.
gate() {
    [ -d headless/build/lib ] \
        || die "headless/build/lib missing — run: ./build.sh headless-setup"

    # e2e boots its own host. Pin the port so an ambient STS2_AGENT_PORT (7777,
    # say) cannot aim the suite at a live host and drive someone's run; e2e's
    # boot wait treats any answering bridge as its own.
    gate_port="${SPIRESCRY_GATE_PORT:-7779}"
    if curl -sf "http://127.0.0.1:$gate_port/health" >/dev/null 2>&1; then
        die "a bridge already answers on port $gate_port — the e2e gate needs it free (override with SPIRESCRY_GATE_PORT)"
    fi

    step "gate: build mod, cli, host"
    build_mod
    build_cli
    # /health.buildHash comes from this stamp; an unstamped host fails e2e B1.
    dotnet build -c Release -p:SourceRevisionId="spirescry.$(current_stamp)" \
        headless/Host/Host.csproj --nologo --verbosity minimal

    step "gate: C# unit tests"
    dotnet run --project tests/UnitTests/UnitTests.csproj

    step "gate: protocol artifact"
    bash tests/protocol_artifact_test.sh

    step "gate: python unit tests"
    for t in protocol_contract_test projection_schema_drift_test \
             world_walker_test parity_settlement_test e2e_settlement_test \
             build_identity_test play_skill_fault_protocol_test \
             gate_coverage_test sweep_quarantine_test sweep_reporting_test; do
        python3 "tests/$t.py"
    done

    step "gate: CLI unit tests"
    cargo test --manifest-path cli/Cargo.toml

    step "gate: build stop + play skill pre-flight"
    tests/build_stop_test.sh
    tests/play_skill_preflight_test.sh

    # No --quick here, ever: the exhaustive M1–M4 content sweeps (every
    # encounter, card, potion, relic) only ever run here, so skipping them
    # leaves that surface unguarded. --quick is the local iteration loop.
    # tests/gate_coverage_test.py fails if this line drops back to it.
    # Faults already filed as open issues are listed in sweeps.QUARANTINE so
    # they don't redden an unrelated PR's gate; anything else still fails.
    step "gate: end-to-end (exhaustive, self-booted on port $gate_port)"
    STS2_AGENT_PORT="$gate_port" python3 tests/e2e.py --boot

    ok "gate passed — CI set plus the local-only e2e suite"
}

usage() { sed -n '2,/^$/p' "$0" | sed -E 's/^# ?//'; exit 1; }

[ "$#" -eq 0 ] && usage
while [ "$#" -gt 0 ]; do
    case "$1" in
        libs)       build_libs ;;
        mod)        build_mod ;;
        cli)        build_cli ;;
        all)        build_mod; build_cli ;;
        deploy-mod) deploy_mod ;;
        deploy-cli) deploy_cli ;;
        deploy)     deploy_mod; deploy_cli ;;
        headless)   launch_headless ;;
        headless-setup) headless_setup ;;
        host)
            if [ "${2:-}" = "--foreground" ]; then
                shift
                launch_host --foreground
            else
                launch_host
            fi ;;
        verify)     verify ;;
        gate)       gate ;;
        stamp)      current_stamp ;;
        stop)       stop_game ;;
        -h|--help|help) usage ;;
        *) die "unknown command: $1 (run with --help)" ;;
    esac
    shift
done
