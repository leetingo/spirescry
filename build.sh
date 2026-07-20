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
#   headless     launch the game with no window; waits until the bridge is up
#   headless-setup  one-time: copy deps, IL-patch sts2.dll, extract loc, build host
#   host         run the pure .NET host — no game binary, no Steam
#                (--foreground: exec in this process; for sandboxed
#                executors that reap background children)
#   verify       conformance: tests/parity.py on both boots + key-set diff
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

# wait_bridge <timeout_s> <log>: poll /health until the bridge answers.
wait_bridge() {
    for _ in $(seq 1 "$1"); do
        if curl -sf "http://127.0.0.1:$STS2_AGENT_PORT/health" > /dev/null; then
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
    host_start_status=0
    host_start_identity="$(process_start_identity "$host_pid")" || host_start_status=$?
    host_snapshot_status=0
    host_snapshot="$(process_snapshot "$host_pid")" || host_snapshot_status=$?
    host_start_confirm_status=0
    host_start_confirm="$(process_start_identity "$host_pid")" || host_start_confirm_status=$?
    [ "$(process_state "$host_pid")" = live ] \
        && [ "$host_start_status" = 0 ] \
        && [ "$host_start_confirm_status" = 0 ] \
        && [ "$host_start_identity" = "$host_start_confirm" ] \
        && [ "$host_snapshot_status" = 0 ] \
        && is_this_host_snapshot "$host_snapshot" || \
        die "launched host PID $host_pid could not be identified"
    # wait_bridge calls die on deadline. Run it in a subshell so launch_host
    # can still reclaim the exact child it started before returning failure.
    if ! (wait_bridge 30 "$log"); then
        timeout_start_before_status=0
        timeout_start_before="$(process_start_identity "$host_pid")" || timeout_start_before_status=$?
        timeout_snapshot_status=0
        timeout_snapshot="$(process_snapshot "$host_pid")" || timeout_snapshot_status=$?
        timeout_start_after_status=0
        timeout_start_after="$(process_start_identity "$host_pid")" || timeout_start_after_status=$?
        timeout_state="$(process_state "$host_pid")"
        if [ "$timeout_state" = unknown ] \
            || { [ "$timeout_state" = live ] \
                && { [ "$timeout_start_before_status" != 0 ] \
                    || [ "$timeout_snapshot_status" != 0 ] \
                    || [ "$timeout_start_after_status" != 0 ]; }; }; then
            die "bridge timed out and host PID $host_pid cannot be inspected safely"
        elif [ "$timeout_state" = live ]; then
            [ "$timeout_start_before" = "$host_start_identity" ] \
                && [ "$timeout_start_after" = "$host_start_identity" ] || \
                die "bridge timed out and host PID $host_pid start identity changed — refusing to signal it"
            is_this_host_snapshot "$timeout_snapshot" || \
                die "bridge timed out and PID $host_pid no longer belongs to this host"
            stop_exact_process "$host_pid" "$timeout_snapshot" "timed-out host"
        fi
        return 1
    fi

    # The child may still be the forked shell at the first ps sample and
    # exec dotnet a moment later without changing PID/start time. Persist
    # the stable, post-boot command so stop does not mistake that exec for
    # PID reuse.
    booted_start_before_status=0
    booted_start_before="$(process_start_identity "$host_pid")" || booted_start_before_status=$?
    host_snapshot_status=0
    host_snapshot="$(process_snapshot "$host_pid")" || host_snapshot_status=$?
    booted_start_after_status=0
    booted_start_after="$(process_start_identity "$host_pid")" || booted_start_after_status=$?
    [ "$(process_state "$host_pid")" = live ] \
        && [ "$booted_start_before_status" = 0 ] \
        && [ "$booted_start_after_status" = 0 ] \
        && [ "$booted_start_before" = "$host_start_identity" ] \
        && [ "$booted_start_after" = "$host_start_identity" ] \
        && [ "$host_snapshot_status" = 0 ] \
        && is_this_host_snapshot "$host_snapshot" || \
        die "booted host PID $host_pid could not be identified"
    pidtmp="$(mktemp "${pidfile}.XXXXXX")"
    printf '%s\n%s\n' "$host_pid" "$host_snapshot" > "$pidtmp"
    mv -f "$pidtmp" "$pidfile"
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
    game_bin="$(find "$STS2_GAME_DIR" -maxdepth 1 -type f -perm +111 | head -n 1)"
    [ -n "$game_bin" ] || die "no game binary found in $STS2_GAME_DIR"
    ! pgrep -qf "$STS2_GAME_DIR" || die "game already running — ./build.sh stop first"

    log="${TMPDIR:-/tmp}/spirescry-headless.log"
    rotate_log "$log"
    step "launch headless (bridge port $STS2_AGENT_PORT, log $log)"
    nohup "$game_bin" --headless > "$log" 2>&1 &
    wait_bridge 60 "$log"
}

# Kill by PID file first (works where sandboxes hide other processes from
# pgrep), then use pgrep only to discover candidates and validate every PID
# before signalling it. Exit non-zero whenever "nothing running" cannot be
# established honestly.
stop_game() {
    need_game_dir
    pidfile="${TMPDIR:-/tmp}/spirescry-host.pid"
    stopped=0
    enumeration_failed=0
    if [ -f "$pidfile" ]; then
        IFS= read -r host_pid < "$pidfile" || host_pid=""
        if [[ ! "$host_pid" =~ ^[0-9]+$ ]] || [ "${#host_pid}" -gt 10 ] || [ "$host_pid" -le 1 ]; then
            die "invalid host pidfile $pidfile — refusing to signal anything"
        fi

        saved_snapshot="$(sed -n '2p' "$pidfile")"
        current_snapshot_status=0
        current_snapshot="$(process_snapshot "$host_pid")" || current_snapshot_status=$?
        current_state="$(process_state "$host_pid")"
        if [ "$current_state" = unknown ]; then
            die "cannot inspect PID $host_pid in $pidfile — refusing to signal or discard it"
        elif [ "$current_state" = dead ]; then
            # A dead PID is an ordinary stale file and is safe to clean up.
            rm -f "$pidfile"
        elif [ "$current_snapshot_status" != 0 ]; then
            die "cannot read identity for live PID $host_pid in $pidfile — refusing to signal it"
        elif ! is_this_host_snapshot "$current_snapshot"; then
            die "PID $host_pid in $pidfile does not belong to this host — refusing to signal it"
        elif [ -z "$saved_snapshot" ]; then
            die "host pidfile $pidfile has no saved process snapshot — refusing to signal PID $host_pid"
        elif [ "$saved_snapshot" != "$current_snapshot" ]; then
            die "PID $host_pid in $pidfile was reused or restarted — refusing to signal it"
        else
            stop_exact_process "$host_pid" "$current_snapshot" "host"
            rm -f "$pidfile"
            stopped=1
        fi
    fi

    # Foreground hosts and the engine boot have no PID file. pgrep is only a
    # discovery aid: broad pkill patterns never receive a signal directly.
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
        die "could not enumerate processes and no valid host pidfile was available"
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
        stamp)      current_stamp ;;
        stop)       stop_game ;;
        -h|--help|help) usage ;;
        *) die "unknown command: $1 (run with --help)" ;;
    esac
    shift
done
