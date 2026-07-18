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
    dotnet build -c Release headless/Host/Host.csproj --nologo --verbosity minimal
    [ -x headless/Host/bin/Release/spirescry_host ] || die "host build produced no binary"
    ok "headless/Host/bin/Release/spirescry_host"
}

# Keep exactly one previous generation: debugging "it worked last boot"
# needs last boot's log, and `>` used to destroy it at relaunch.
rotate_log() {
    [ -f "$1" ] && mv -f "$1" "$1.1"
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
    [ -f headless/Host/bin/Release/spirescry_host.dll ] || die "host not built — run: ./build.sh headless-setup"
    ! pgrep -qf spirescry_host || die "host already running — ./build.sh stop first"
    log="${TMPDIR:-/tmp}/spirescry-host.log"
    rotate_log "$log"
    # Through the dotnet CLI, not the apphost — the CLI resolves its own
    # runtime regardless of DOTNET_ROOT.
    if [ "${1:-}" = "--foreground" ]; then
        step "launch host, foreground (bridge port $STS2_AGENT_PORT, log $log)"
        exec dotnet headless/Host/bin/Release/spirescry_host.dll \
            > >(tee -a "$log") 2> >(tee -a "$log" >&2)
    fi
    step "launch host (bridge port $STS2_AGENT_PORT, log $log)"
    nohup dotnet headless/Host/bin/Release/spirescry_host.dll > "$log" 2>&1 &
    wait_bridge 30 "$log"
}

build_mod() {
    [ -f lib/sts2.dll ] || die "lib/sts2.dll missing — run: ./build.sh libs"
    step "build mod (Release)"
    dotnet build -c Release src/Spirescry.csproj --nologo --verbosity minimal
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
        codesign --force --sign - cli/target/release/spirescry >/dev/null 2>&1 || true
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

stop_game() {
    need_game_dir
    stopped=0
    pkill -f "$STS2_GAME_DIR" && stopped=1
    pkill -f spirescry_host && stopped=1
    if [ "$stopped" = 1 ]; then ok "stopped"; else ok "nothing running"; fi
}

# Conformance: run tests/parity.py once per boot, then compare the
# recorded snapshot key sets — same phase must expose the same keys in
# both modes. Engine leg needs Steam and a deployed, current mod.
verify() {
    kh="${TMPDIR:-/tmp}/spirescry-parity-host.json"
    ke="${TMPDIR:-/tmp}/spirescry-parity-engine.json"
    stop_game

    step "verify: host boot"
    launch_host
    python3 tests/parity.py --keys-out "$kh" || { stop_game; die "host parity run failed"; }
    stop_game

    step "verify: engine-headless boot"
    launch_headless
    python3 tests/parity.py --keys-out "$ke" || { stop_game; die "engine parity run failed"; }
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
        stop)       stop_game ;;
        -h|--help|help) usage ;;
        *) die "unknown command: $1 (run with --help)" ;;
    esac
    shift
done
