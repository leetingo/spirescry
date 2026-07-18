---
name: play-sts2
description: Play Slay the Spire 2 — scry the board as JSON via the spirescry CLI, fire one verb per decision. Use when the user wants a run played (started, continued, watched) or the headless host set up.
---

# Playing Slay the Spire 2

`spirescry` is your whole interface: **scry** the board (`obs`), fire
**one verb**, wait for the world to change. The verbs on this page are
the complete play surface.

`./build.sh` runs from the repo root — this file's real path
(`readlink -f`) is `<repo>/.claude/skills/play-sts2/SKILL.md`.

## CLI pre-flight

Before `health` or **any verb**, run this once in the shell that will play.
It resolves the `spirescry` executable currently on `PATH`, compares its
SHA-256 with this checkout's release CLI (falling back to a byte comparison
when no SHA-256 tool is installed), and installs a shell wrapper for the
verified choice. Rerun it after rebuilding or redeploying the CLI.

```sh
spirescry_preflight() {
    local repo_root repo_cli path_cli repo_hash path_hash same_cli
    repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || {
        echo "spirescry pre-flight: not inside the spirescry repository; stop" >&2
        return 1
    }
    repo_cli="$repo_root/cli/target/release/spirescry"
    if [ ! -x "$repo_cli" ]; then
        echo "spirescry pre-flight: repo release CLI is missing; run (cd '$repo_root' && ./build.sh cli), then retry; do not run verbs" >&2
        return 1
    fi

    # A fresh /bin/sh resolves PATH without seeing a wrapper or alias left
    # behind by an earlier pre-flight in this interactive shell.
    path_cli="$(PATH="$PATH" /bin/sh -c 'command -v spirescry' 2>/dev/null || true)"
    case "$path_cli" in
        /*) ;;
        "") ;;
        *) path_cli="$PWD/$path_cli" ;;
    esac

    same_cli=0
    if [ -n "$path_cli" ] && [ -x "$path_cli" ]; then
        if command -v shasum >/dev/null 2>&1; then
            repo_hash="$(shasum -a 256 "$repo_cli" | awk '{print $1}')"
            path_hash="$(shasum -a 256 "$path_cli" | awk '{print $1}')"
            [ -n "$repo_hash" ] && [ "$repo_hash" = "$path_hash" ] && same_cli=1
        elif command -v sha256sum >/dev/null 2>&1; then
            repo_hash="$(sha256sum "$repo_cli" | awk '{print $1}')"
            path_hash="$(sha256sum "$path_cli" | awk '{print $1}')"
            [ -n "$repo_hash" ] && [ "$repo_hash" = "$path_hash" ] && same_cli=1
        elif cmp -s "$repo_cli" "$path_cli"; then
            same_cli=1
        fi
    fi

    if [ "$same_cli" -eq 1 ]; then
        SPIRESCRY_CLI="$path_cli"
    else
        SPIRESCRY_CLI="$repo_cli"
        if [ -n "$path_cli" ]; then
            echo "spirescry pre-flight: PATH CLI differs from repo release; using $repo_cli (run ./build.sh deploy-cli before relying on PATH again)" >&2
        else
            echo "spirescry pre-flight: no PATH CLI; using $repo_cli" >&2
        fi
    fi
    export SPIRESCRY_CLI
}

if spirescry_preflight; then
    spirescry() { "$SPIRESCRY_CLI" "$@"; }
else
    echo "spirescry pre-flight failed: stop and fix the CLI before playing" >&2
    return 1 2>/dev/null || false
fi
```

A matching PATH CLI produces no warning and runs normally. A missing or
stale PATH CLI automatically uses the repository release binary; a missing
repository release binary is the hard stop because there is nothing trusted
to compare or run.

## Boot

Run the host as a long-lived task you own (a background task or
persistent terminal):

```sh
./build.sh host --foreground   # blocks while the game runs
spirescry health               # booted when it prints {"ok": true, …}
./build.sh stop                # when you're done playing
```

- "host not built" / `spirescry: command not found` → [SETUP.md](SETUP.md),
  then retry.
- "host already running" → it's live; play.
- "Operation not permitted" on connect (sandboxed executors) → approve
  loopback access to `127.0.0.1:7777`; the bridge never leaves localhost.
- `STS2_AGENT_LANG=zhs` before the host command switches text language.
- Host output lands in `$TMPDIR/spirescry-host.log` (foreground too;
  previous boot at `.log.1`). `STS2_AGENT_HTTP_LOG=1` before the host
  command logs every bridge request — turn it on before filing a bridge
  fault, and pair it with `spirescry --verbose` on the CLI side.

Host quirks: runs never save (each boot starts clean), and card pickers
auto-confirm at max picks — `confirm` accepts a partial pick.

## The loop

1. Scry the clean menu with `spirescry obs --decision`, then launch with
   both values it returned:
   `spirescry new-run IRONCLAD --if-rev <rev> --if-run none --follow`
   (`--seed ABC123 --ascension 5` reproduces a run). The followed `obs`
   lands on phase `event` — Neow greets every run.
2. **Scry**: use that fresh decision `obs`, or call
   `spirescry obs --decision` → read `phase`, `rev`, `runId`,
   `legal`, and the board; decide. `legal` comes from the targets and gates
   visible in this exact snapshot and is safer than the orientation table;
   dispatch still performs the final engine-side validation.
3. **Act with both guards and follow**: fire one verb from `legal`, adding
   `--if-rev <rev> --if-run <runId> --follow`. The response waits past
   acceptance while tracked queues/tasks are busy and includes resolution
   events plus a fresh decision `obs`; GUI-only callbacks must expose the
   same boundary for three consecutive frames.
4. Inspect `outcome`: `settled` means tracked work is quiet (and the GUI
   boundary was stable for those frames), not proof that every opaque engine
   continuation has completed;
   `next_decision` means an effect parked on a picker/dialogue that now
   needs one verb; `timeout` means the verb was accepted but has not
   resolved — do not fire another verb, rescry and inspect `health`.
5. Repeat 2–4 until `game_over`; report outcome, where it ended
   (`actNumber` / `actFloor` / `encounter.title`), and the seed, then
   `abandon` to return to the menu.

`stale_state` means the board changed after your scry: rescry and decide
again. `external_change` means the engine replaced your run: stop; never
continue using the old run's state. One verb per returned decision —
batching skips the guards and builds the next choice on an unconfirmed
world.

Rejections name their fix: exit 75 (`not_ready`) → retry the same verb;
`bad_phase` → rescry; `not_enough_energy` / `not_enough_stars` /
`bad_target` / `bad_index` → decide differently.

Decision card text is caller-scoped, not consumed by GET order. Cache each
visible card's `textKey` (it distinguishes upgrades, enchantments, and
afflictions), then pass cached keys back as repeatable
`obs --decision --known-card <key>` flags. Repeating an unqualified
`obs --decision` deliberately returns the same text again.

For a diagnostic checkpoint, save `spirescry runlog > run-<seed>.json`.
`spirescry replay <file>` only runs from a clean `main_menu`, reconstructs
a **new** guarded run, and rejects logs where any verb lacks a followed,
settled fingerprint. It checks every fingerprint and stops at the first
divergence. Its final board belongs to the reconstruction's new `runId`;
never report it as the source run's outcome or history.

## The ledger

Keep a six-line ledger in your head across the turn, updated after
every followed action, instead of re-deriving from full JSON each read:

```
you:    hp / block / energy / stars
orbs:   slots / ordered queue / passive → evoke values  (Defect only)
facing: left|right / who's behind          (surround fights only)
threat: enemy hp / incoming damage × hits / stacks that gate damage
turn:   cards played so far, once-per-turn triggers used, potions left
run:    clean | polluted | reconstructed
```

Arithmetic mistakes at the final turn kill runs; the ledger is where
you catch them. In surround fights (back-attack powers on the
enemies), track which way you face after every targeted play — hits
from behind are half again as hard, and each targeted card can turn
you around. The `run` line is your integrity flag: it starts `clean`
and only ever degrades (see the next two sections); carry it into
your report.

## Wedge recovery

A `wedge:<Action>` event means an engine action died mid-resolution —
its effects may be lost (energy spent, no damage dealt). Climb the
ladder; abandon is the last rung:

1. Rescry — the game usually keeps accepting verbs.
2. Fire a legal verb; `end-turn` usually works.
3. The run advances → note the loss and keep playing.
4. Every verb rejecting repeatedly → `spirescry abandon`, start fresh.

### Fatal wedge — don't climb the ladder

One signature is past recovery:

```
every enemy alive:false      +  phase still combat
health: executor null, queues empty, executorStuckMs 0
every verb → not_ready: player actions disabled
```

Nothing is running and nothing is queued — the engine died mid
death-resolution and the win flow will never fire. Newer hosts announce
this as a `wedge:DeadBoard` event after ~8 s; older hosts stay silent
(the stuck-executor watchdog can't time an executor that's already
gone). Either way, more verbs only pollute the evidence. Instead:

1. Capture `obs`, `health`, the last verb you fired, and the exception
   from the host log.
2. Report a **technical abort**, distinct from a game loss — the
   outcome is "host fault", not "died to X".
3. `abandon`; if even that rejects, restart the host.

## Impossible observations

First rule out takeover: compare `runId`, and look for a `run:<id>` event
you did not cause. A changed run is no longer yours.

Resources moving at a frozen `rev`, a claim that never lands, or an effect
whose promise disagrees with a `settled` board is a bridge fault, not a
puzzle to poll at. One rescry → `spirescry health` → use a currently
`legal` escape verb if one exists → **mark the run polluted**. Keep playing
only if the bridge still returns settled decisions; disclose the fault and
treat conclusions drawn from the polluted state as suspect.

A host restart ends the run, full stop. Rebuilding the same seed and
deck via cheats yields a **reconstructed** run — a new run whose RNG
streams, map history, and reward pools have already diverged. Play it
if it's useful, but its outcome says nothing about the original: report
"reconstructed after host fault; outcome not attributable to the
original seed", never the seed's result.

## Verbs by phase

Every phase outside combat also carries a `player` footer (hp, gold,
potions, relics, deck — enchanted/afflicted cards are distinct). The table
is orientation only; fire a verb only when the current `legal` array names
it.

| phase | you see | verbs |
|---|---|---|
| `main_menu` | — | `new-run <CHARACTER>` (bad names are rejected with the valid list) |
| `map` | `next`: reachable nodes (col/row/type) | `map-move <col> <row>` |
| `combat` | `you`, `hand`, `enemies`, `potions`, `piles`, `turn` | `play <MODEL> [--target <id>]`, `end-turn`, `potion-use <slot> [--target <id>]` |
| `event` | `options[]` (idx/title/description/locked) | `option <idx>` — some options only mark `chosen`: rescry after each, and when nothing new is pickable, `proceed`. `proceed` also pages dialogue and leaves once `finished`. |
| `rest_site` | `options[]` | `option <idx>` (upgrade opens a deck picker); `options` empty → `proceed` → map |
| `shop` | goods with idx + prices | `buy <kind> --idx <n>` (kind: `card`/`colorless`/`relic`/`potion`/`card_removal`), `leave` |
| `treasure`, `relic_reward` | relics on offer | `pick-relic <idx>`, `skip` |
| `rewards` | reward tiles | `pick-reward <idx>`; `proceed` leaves, skipping the rest |
| `card_reward` | cards on offer | `pick-card <idx>`, `skip` |
| `card_select` | picker cards with `cost` and `upgradedPreview` | `pick-card <idx>` (toggles), `confirm` when enough are selected, `skip` only when `cancelable` |
| `hand_select` | cards currently selectable from hand | `pick-card <idx>`, `confirm` when enough are selected (no `skip`) |
| `bundle_select` | card packs (e.g. Neow) | `pick-card <idx>`; GUI may then expose `confirm` |
| `crystal_sphere` | divination minigame | `map-move <col> <row>` picks a cell, `option 0`/`1` picks the tool, `proceed` leaves |
| `game_over` | `outcome`, `seed`, where the run ended: `actNumber`/`actFloor` (1-based), `mapCoord`, `encounter` (model + title). Legacy pair: `act` is the zero-based act index, `floor` the run-cumulative floor — prefer the 1-based fields in reports. | `abandon` → main menu |
| any (in a run) | — | `potion-discard <slot>`, `abandon` |

## Reading combat

- `hp` and `energy` are `[current, max]` pairs.
- `hand[]` cards carry `description` (the card face text) and `vars` —
  the numbers behind it with Strength/Weak already applied
  (`{"Damage": 8}`) — plus the base `model`, exact `selector` that `play`
  wants, `cost`, `target`, and `unplayable`.
- `enemies[]`: `id` is the `--target` value, `title` the readable name;
  `intents[]` show `damage` × `hits` (nulls mean a non-attack). `damage`
  is the modified number the pip shows; `baseDamage` the raw roll — a
  gap between them reflects any active attacker- or target-side damage
  modifier (including Strength/Weak and back-attack).
- Surround fights (`SURROUNDED_POWER` on you): `you.facing` is
  `left`/`right`, each flanker carries `side` and `isBehind` — a hit
  from behind lands half again as hard, and targeted plays can turn
  you around. Re-check after every targeted card.
- `--target` only when the card targets an enemy; a lone enemy
  auto-targets.
- X-cost cards (`WHIRLWIND`) display `cost` = your current energy: they
  spend all of it and repeat the effect X times.

## Stars

Some characters run a second combat currency: `you.stars`, spent by
cards carrying a `starCost` (energy-only cards show `starCost: null`).
A star-starved play is rejected with `not_enough_stars` — the fix is
sequencing, not retrying:

- Generators before spenders: bank stars first, then fire the
  `starCost` cards the same turn or later.
- Star-starved decks upgrade their *generators* (more stars per play),
  not their spenders — supply is usually the bottleneck.
- X-star cards mirror X-energy: `starCost` = your current stars, spend
  everything.
