#!/bin/sh
set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
skill="$repo_root/.claude/skills/play-sts2/SKILL.md"
fixture=$(mktemp -d "${TMPDIR:-/tmp}/spirescry-skill-preflight.XXXXXX")
fixture=$(CDPATH= cd -P -- "$fixture" && pwd -P)
trap 'rm -rf "$fixture"' EXIT HUP INT TERM

fixture_repo="$fixture/repo with spaces"
repo_cli="$fixture_repo/cli/target/release/spirescry"
stale_bin="$fixture/stale-bin"
matched_bin="$fixture/matched-bin"
preflight="$fixture/preflight.sh"

mkdir -p "$(dirname -- "$repo_cli")" "$stale_bin" "$matched_bin"
git -C "$fixture" init -q "$(basename -- "$fixture_repo")"

printf '%s\n' '#!/bin/sh' 'printf '\''repo:%s\n'\'' "$1"' >"$repo_cli"
printf '%s\n' '#!/bin/sh' 'printf '\''stale:%s\n'\'' "$1"' >"$stale_bin/spirescry"
chmod +x "$repo_cli" "$stale_bin/spirescry"
cp "$repo_cli" "$matched_bin/spirescry"

awk '
    /^## CLI pre-flight$/ { in_section = 1; next }
    in_section && /^```sh$/ { in_fence = 1; next }
    in_fence && /^```$/ { exit }
    in_fence { print }
' "$skill" >"$preflight"
test -s "$preflight"

# The pre-flight runs in one process; the selected command is consumed by a
# different process, matching the one-shell-per-verb agent execution model.
stale_command=$(
    cd "$fixture_repo"
    PATH="$stale_bin:/usr/bin:/bin" /bin/sh "$preflight" 2>"$fixture/stale.err"
)

if [ "$stale_command" != "$repo_cli" ]; then
    printf 'expected stale PATH pre-flight to select %s, got %s\n' \
        "$repo_cli" "${stale_command:-<no command>}" >&2
    exit 1
fi

stale_result=$(
    cd "$fixture_repo"
    PATH="$stale_bin:/usr/bin:/bin" /bin/sh -c '"$1" "$2"' sh "$stale_command" obs
)
test "$stale_result" = "repo:obs"
grep -q 'PATH CLI differs from repo release' "$fixture/stale.err"

matched_command=$(
    cd "$fixture_repo"
    PATH="$matched_bin:/usr/bin:/bin" /bin/sh "$preflight" 2>"$fixture/matched.err"
)
test "$matched_command" = "spirescry"
test ! -s "$fixture/matched.err"

matched_result=$(
    cd "$fixture_repo"
    PATH="$matched_bin:/usr/bin:/bin" /bin/sh -c '"$1" "$2"' sh "$matched_command" health
)
test "$matched_result" = "repo:health"

printf 'play-sts2 pre-flight fresh-shell contract: ok\n'
