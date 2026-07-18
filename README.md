# spirescry

*Scry the board, fire one verb — a phylactery for Slay the Spire 2.*

An agent-friendly play interface for Slay the Spire 2. A small mod exposes
observations and actions over loopback HTTP; a CLI maps that API onto one
shell command per decision. An agent plays by reading `obs`, thinking, and
firing one verb — no pixels, no synthetic input, no screen scraping.

Scrying is the game's own peek-and-decide mechanic, which is also this
bridge's whole job. (It even drives the crystal-ball divination minigame,
so the name is literal.) The *phylactery* part: the host boot keeps the
game's logic alive outside its body — no game binary, no engine, no Steam.

## Quick start

```sh
./build.sh libs all deploy   # copy game dlls, build mod + CLI, install
./build.sh headless          # boot the game with no window (or launch it normally)

spirescry new-run IRONCLAD --seed MYSEED01
spirescry obs                # → {"phase": "event", "rev": 3, options: [...]}
spirescry option 0
spirescry obs --since 3 --wait 5000   # parks until the world changes
```

The agent loop is three rules:

1. Read `rev` from any response, act, then `obs --since <rev> --wait <ms>` —
   it returns instantly if the change already landed, wakes on the change
   otherwise, and times out with `changed: false` if nothing happened.
2. `not_ready` rejections are transient by contract — retry.
3. If `/health` ever shows `executorStuckMs` climbing (a `wedge:` event
   fires past 8s), `abandon` — a fresh run gets a fresh executor.

There is no fourth failure mode: every verb is either accepted and visible
in the event stream, or rejected with a reason.

## The protocol

**`GET /obs`** returns a phase-shaped snapshot. Combat gets you
(hp/block/energy/powers), hand, enemies (hp/block/powers/intents with
damage × hits), potions, and pile contents; every out-of-combat phase
carries a `player` footer (hp, gold, potions, relics, relicStates, deck);
`player.relics` remains the stable ID-string list while `relicStates` adds
`{model, counter, usedUp, description}` (`description` is `null` in compact
mode). Combat's `you.relics` carries `{model, counter}` so every-N counters
are visible where they tick. The map gets
reachable nodes, the whole act graph, and the run seed; `game_over`
reports outcome / seed / hp / gold plus where the run ended: `actNumber`
(1-based), `actFloor` (within the act), `mapCoord` (`{col, row}`), and
the final `encounter` (model + localized title) — `act` (zero-based
index) and `floor` (run-cumulative) stay for compatibility.

`?compact=1` (CLI: `obs --compact`) elides the big repeats — no act
graph, deck and combat piles as counts-by-model (`"STRIKE_IRONCLAD+": 2`
= two upgraded copies), hand cards keep their numbers (`vars`) but drop
the description prose — for agents that poll often. `obs --decision`
adds legal verbs derived from the targets in that exact snapshot and
deduplicates card prose across every visible card surface. Each card has
a stable `textKey` that includes upgrade, enchantment, and affliction;
pass cached keys back as repeatable `--known-card <key>` flags to omit
their prose without any process-global or read-order-dependent state.

Phases: `main_menu`, `map`, `combat`, `event`, `shop`, `rest_site`,
`treasure`, `rewards`, `card_reward`, `relic_reward`, `card_select`,
`hand_select`, `bundle_select`, `crystal_sphere`, `game_over` — plus
`overlay`/`unknown` for anything unmapped, carrying the overlay's type
name so a stuck screen is diagnosable from `/obs` alone.

Shop inventory keeps `cost` as its original gold-price field and also
exposes the clearer `price` alias. Card stock adds `playCost` and `starCost`;
upgrade candidates keep the string `upgradedPreview` and add
`upgradedPlayCost` / `upgradedStarCost` beside it.

**`POST /step`** takes `{"action": ..., "args": ...}`. Add
`"follow": <ms>` (CLI: `--follow [ms]`) to wait past acceptance until
the engine queues and tracked async work settle, or until a new decision
surface appears. The response distinguishes `outcome: settled`,
`next_decision`, and `timeout`, and includes resolution events plus a fresh
decision `obs`. GUI callbacks that do not return a task must also expose the
same boundary across three consecutive frames; guard checks still happen
atomically before dispatch. This is a bounded settlement signal for work the
bridge can observe, not a claim that every opaque engine continuation has
completed.
Verbs: `new-run`,
`abandon`, `option`, `proceed`, `map-move`, `pick-reward`, `pick-card`,
`pick-relic`, `confirm`, `skip`, `buy`, `leave`, `play`, `end-turn`,
`potion-use`, `potion-discard`, `cheat` — each valid in its own phase.
Errors ride on 4xx/5xx as `{"ok": false, "err": "<code>", "msg": "..."}`
with a stable machine-readable vocabulary:

| Code | Meaning |
| --- | --- |
| `bad_request` | The verb or arguments are malformed or unsupported. |
| `bad_phase` | The verb does not apply to the current phase. |
| `bad_index` | An index or named item is absent from the current observation. |
| `bad_target` | The requested map/combat target is not legal now. |
| `bad_state` | The request needs another decision or state change; waiting alone cannot make this same request valid. |
| `not_ready` | A transient engine/UI window; waiting and retrying can make the same request valid. |
| `not_playable` | The chosen card, potion, relic, or option is currently prohibited by game rules. |
| `not_enough_gold` | The purchase exceeds current gold. |
| `not_enough_energy` | The card exceeds current energy. |
| `not_enough_stars` | The card exceeds current stars. |
| `run_exists` | `new-run` was requested while a run is already loaded. |
| `stale_state` | `--if-rev` no longer matches; rescry before deciding. |
| `external_change` | `--if-run` names a run that is no longer live. |
| `resolution_partial` | An inline engine action faulted after observable state changed. |
| `resolution_failed` | An inline engine action faulted before observable state changed. |
| `not_found` | The HTTP route does not exist. |
| `internal` | The bridge hit an invariant or unexpected implementation failure. |

The CLI prints failures on stderr and exits `75` (`EX_TEMPFAIL`) only for
`not_ready`; every other bridge rejection, local validation error, transport
failure, or malformed response exits `1`. Success exits `0`. Callers should
branch on the exit status, not parse the rendered error text.

**Combat targets.** `play <model> --target <id>` and
`potion-use <slot> --target <id>` take the combat ID shown on an enemy in the
current observation. An enemy-targeted card or potion auto-targets when
exactly one enemy is alive; with multiple living enemies an explicit target
is required. Self, ally, and untargeted effects do not need an enemy ID.

Health, observations, and step responses carry a `runId`: `none` between
runs and a fresh token whenever the engine replaces its live `RunState`.
Verbs accept optimistic guards, checked atomically with dispatch:
`--if-rev <rev>` rejects with `stale_state` when the board moved since it
was read, while `--if-run <id>` rejects with `external_change` when a GUI
action or another agent replaced the run. Run swaps also ride the event
stream as `run:<id>` / `run:none`.

**Event-driven waits.** Every snapshot carries a monotonic `rev`. Changes
bump it from the engine's own C# events (action executor, combat manager,
overlay stack) plus a per-tick phase diff as the safety net; a `/step`
accepted without either (in-phase inline mutations — reward claims, shop
buys) bumps it itself. `obs?since=` responses name the events behind the
bump (`phase:map->combat`, `action:PlayCardAction`, `enqueued:...`,
`step:buy`, `wedge:...`). The server clamps `wait` to `0..60000` ms. Omitting
`--wait` (or passing `0`) is a no-wait poll; pass a positive value explicitly
when the caller should park for a change.

The event log retains the latest 64 revision events. After a burst larger
than that, an old `--since` still returns immediately with `changed: true`
and the authoritative current observation, but `events` contains only the
retained suffix and may not begin at `since + 1`. Treat it as a wake-up and
diagnostic trail, not a durable replay log. No sleep-polling anywhere.

**`GET /health`** returns identity and compatibility fields (`ok`, `mod`,
`version`, `buildHash`, `protocolVersion`, and advertised verb/cheat
`capabilities`), the current `phase`, `rev`, and `runId`, plus live executor
diagnostics. `executor` is `null` or `ActionType:State`, `executorStuckMs` is
the time spent on that same action, and each `queues` entry reports its
`owner`, `depth`, and `paused` state.

**Tracing a session.** `STS2_AGENT_HTTP_LOG=1` on the host makes the
bridge log one line per request — verb, status, `rev` movement, wall
time — the audit trail for reconstructing what an agent fired when a run
wedged. `spirescry --verbose` traces the same round-trips from the CLI
side on stderr; both stamp the same UTC clock, so the two logs line up.
`build.sh` boots keep their output in `$TMPDIR/spirescry-host.log`
(engine-headless: `spirescry-headless.log`), foreground included; the
previous boot's log survives at `.log.1`.

The pure host also records why it exits: clean process shutdowns,
SIGINT/SIGTERM/SIGHUP/SIGQUIT, boot failures, and unhandled managed
exceptions (with their stack). No process can log after an untrappable
SIGKILL or an OOM-kill; in those cases an abruptly truncated log plus the
operating system's process/pressure records are the available evidence.

**Diagnostic reconstruction.** `spirescry runlog > run.json` records the
current run's seed and accepted bridge verbs; every entry is attributed to
one `runId`. A recipe is complete only when every verb used follow, reached
`settled` or `next_decision`, and recorded a state fingerprint. From a clean
`main_menu` (`runId: none`), `spirescry replay run.json` re-drives that recipe
with CAS guards and follow settlement, checking every recorded state
fingerprint and stopping at the first divergence.
This is a debugging aid, not authoritative replay recording: the output is
explicitly a new reconstruction with its own `runId`, and its final state
must never be attributed to the source run.

## Three ways to run, one bridge

```sh
# 1. the game, windowed — or
# 2. engine-headless: the game process with no window (Steam required)
./build.sh headless

# 3. pure .NET host — no game binary, no Godot engine, no Steam
./build.sh headless-setup   # one-time: deps + IL patch + loc tables + build
./build.sh host
# Keep the host attached to this terminal/task (still logs to $TMPDIR):
./build.sh host --foreground

./build.sh stop             # stops whichever is running
```

**In-game** boots load the mod inside the game process and drive the real
UI. The **host** boot loads an IL-patched `sts2.dll` against a stub
GodotSharp in a plain .NET process; `RunMode` branches swap UI nodes for
the engine's model-layer test entry points (`RunState.CreateForTest`,
deferred `ICardSelector`s, a virtual rewards flow). Same protocol either
way — in host mode actions also resolve inline, so `/step` returns with
the effect already applied.

`host --foreground` execs the host in the invoking process instead of
starting a background child. Use it in CI, sandboxes, and agent runners that
reap detached children; its lifetime then matches the terminal or task, and
its output is still copied to `$TMPDIR/spirescry-host.log`.

Host-mode differences: saves don't persist (runs are test-mode), card
pickers auto-resolve once max cards are picked (`confirm` covers partial
picks), and text comes from tables extracted out of your local install's
`.pck` (`STS2_AGENT_LANG=zhs` etc. to switch).

## What's covered

- **A full run, both boots**: Neow to the finale, victory or defeat —
  including the ascension-10 two-boss ending (the first boss exits back to
  the map; `obs.next` carries the second).
- **Every event**: all 57 event models render; every unlocked first-level
  option is forced on a fresh copy and drained to completion
  (`tests/eventsweep.py --all-options`).
- **Every content atom**: 85 encounters resolve; all 593 cards are tried
  in their owning character's combat (playable effects execute, legality
  rejects stay clean); 65 potions are used; all 297 relic pickup hooks
  run. These sweeps catch single-model host/stub faults; the combinatorial
  card × relic × enemy space remains sampled by real runs.
- **Every selection surface**: deck pickers (rest-site upgrade, shop
  removal, transforms), choose-a-card offers, mid-combat hand selects,
  Neow's card-pack bundles, the crystal-sphere minigame (`map-move`
  clicks a cell, `option 0/1` picks the divination tool).
- **Reproducibility**: `new-run --seed --ascension`.
- **Dev cheats** for single-point verification:
  `cheat goto|gold|hp|stars|energy|heal|wound-enemies|event|combat|card|card-upgraded|relic|potion|async-fault`
  — jump anywhere on the act map, end fights fast, force any event or
  encounter by model id, graft content into the run, or deliberately fault
  tracked async work to verify the failure event stream. `models
  card|relic|potion|event|encounter|character` enumerates the current
  game build instead of baking ids into tests.

## Testing

```sh
./build.sh verify        # drives one act-1 loop against BOTH boots, then
                         # diffs the recorded snapshot key sets — same
                         # phase must expose the same keys in each
python3 tests/e2e.py --boot   # the pre-merge suite against a self-booted
                              # host (port 7779): every phase, verb, and
                              # content atom; intentionally long-running
python3 tests/e2e.py --boot --quick  # iteration loop: skip exhaustive M*,
                                     # click the first option per event
python3 tests/eventsweep.py --all-options  # event sweep alone (e2e E1)
python3 tests/host_exit.py  # force clean/signal/unhandled host exits and
                            # assert the final log evidence
```

CI runs only the pure unit tests (`unit-tests.yml`, GitHub-hosted): the
host is built from the game's non-distributable dlls, so the end-to-end
suite stays local — **run `python3 tests/e2e.py --boot` before merging**.

## Build

Requires .NET 9, Rust, and a local install of the game. Its dlls are not
distributable, so `lib/` is gitignored and populated from your install:

```sh
./build.sh libs   # copy sts2.dll + GodotSharp.dll out of the game install
./build.sh all    # mod (dotnet) + cli (cargo)
./build.sh deploy # dll + manifest → game mods/, spirescry → ~/.local/bin
```

Environment:

| Var                 | Default                | Used by                                                        |
| ------------------- | ---------------------- | -------------------------------------------------------------- |
| `STS2_GAME_DIR`     | auto-detected on macOS | build.sh (`libs`, `deploy-mod`, `headless`, `stop`)             |
| `SPIRESCRY_CLI_BIN` | `~/.local/bin`         | build.sh (`deploy-cli`)                                         |
| `STS2_AGENT_PORT`   | `7777`                 | mod/host (bind port), CLI (also `--port`), build.sh bridge wait |
| `STS2_AGENT_HOST`   | `127.0.0.1`            | CLI only (also `--host`) — the bridge always binds 127.0.0.1    |
| `STS2_AGENT_LANG`   | `eng`                  | host only — locale for the extracted text tables                |
| `STS2_HEADLESS_LIB` | `headless/build/lib`   | host only — patched dll + deps + loc tables                     |
| `STS2_HOST_DEBUG`   | unset                  | host only — `1` prints first-chance exception stacks except known stub misses; `2` prints all |
| `STS2_AGENT_HTTP_LOG` | unset                | mod/host — `1` logs each bridge request: verb, status, rev movement, wall time |

## Architecture

    mod entry → main-thread pump → HTTP bridge → phase detector
                                                 snapshotter   (GET /obs)
                                                 dispatcher    (POST /step)

| Piece                           | Where            | Job                                                                                    |
| ------------------------------- | ---------------- | -------------------------------------------------------------------------------------- |
| `MainThreadPump`                | `src/Threading/` | engine singletons aren't thread-safe; GUI drains a queue each frame, host is a mutex    |
| `HttpBridge`                    | `src/Bridge/`    | loopback-only `HttpListener`; no auth because it never leaves 127.0.0.1                 |
| `PhaseDetector` / `Snapshotter` | `src/State/`     | classify what the game is showing, serialize what the agent needs for that phase        |
| `Signals`                       | `src/State/`     | the revision stream: engine events + phase diffs, long-poll waiters, executor watchdog  |
| `Dispatcher`                    | `src/Actions/`   | validate a verb with the engine's own gates, then enqueue the engine's own action       |
| `headless/`                     |                  | host boot: GodotSharp stubs, Mono.Cecil IL patcher, .NET host with Harmony shims        |

The CLI (`cli/`, Rust) is a thin mapping of subcommands onto the HTTP API:
pretty-printed JSON on stdout, bridge errors on stderr with a non-zero exit.

## Design principles

**Minimal change to the game.** The in-game mod carries no Harmony patches
and touches no saves — it only observes through the game's own accessors
and enqueues through the game's own action queue, gated by the same checks
the UI applies. (The host boot shims what a missing engine would have
provided; that's its job.)

**Minimal code.** The smallest end-to-end loop that lets an agent observe
and act. A verb gets in when an agent actually needs it to keep playing.

**No silent failures.** Every wait window found so far is either gated
with an explicit `not_ready` (using the engine's own predicates — the
menu's launch button, the map's travel tweens, queue pause flags) or
covered by the wedge watchdog. Known bounds are documented, not hidden:
host-boot background continuations (unpatched engine `Task.Delay`) can
mutate state between serialized requests — harmless for this protocol,
but not a hard memory barrier.

## Non-goals

Remote access or auth (loopback only), multiplayer, authoritative replay
recording or run-history recovery, a TUI. The runlog above is only a
verified diagnostic recipe; spirescry remains the minimal single-player
agent interface.

---

Not affiliated with MegaCrit. Requires your own copy of Slay the Spire 2;
no game assets or code are distributed with this repository.
