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
carries a `player` footer (hp, gold, potions, relics, deck); the map gets
reachable nodes, the whole act graph, and the run seed; `game_over`
reports outcome / seed / hp / gold plus where the run ended: `actNumber`
(1-based), `actFloor` (within the act), `mapCoord` (`{col, row}`), and
the final `encounter` (model + localized title) — `act` (zero-based
index) and `floor` (run-cumulative) stay for compatibility.

`?compact=1` (CLI: `obs --compact`) elides the big repeats — no act
graph, deck and combat piles as counts-by-model (`"STRIKE_IRONCLAD+": 2`
= two upgraded copies), hand cards keep their numbers (`vars`) but drop
the description prose — for agents that poll often.

Phases: `main_menu`, `map`, `combat`, `event`, `shop`, `rest_site`,
`treasure`, `rewards`, `card_reward`, `relic_reward`, `card_select`,
`hand_select`, `bundle_select`, `crystal_sphere`, `game_over` — plus
`overlay`/`unknown` for anything unmapped, carrying the overlay's type
name so a stuck screen is diagnosable from `/obs` alone.

**`POST /step`** takes `{"action": ..., "args": ...}`. Verbs: `new-run`,
`abandon`, `option`, `proceed`, `map-move`, `pick-reward`, `pick-card`,
`pick-relic`, `confirm`, `skip`, `buy`, `leave`, `play`, `end-turn`,
`potion-use`, `potion-discard`, `cheat` — each valid in its own phase.
Errors ride on 4xx/5xx as `{"ok": false, "err": "<code>", "msg": "..."}`
with codes that say what to change (`bad_phase`, `not_enough_energy`,
`bad_target`, `not_ready`, …).

**Event-driven waits.** Every snapshot carries a monotonic `rev`. Changes
bump it from the engine's own C# events (action executor, combat manager,
overlay stack) plus a per-tick phase diff as the safety net; a `/step`
accepted without either (in-phase inline mutations — reward claims, shop
buys) bumps it itself. `obs?since=` responses name the events behind the
bump (`phase:map->combat`, `action:PlayCardAction`, `enqueued:...`,
`step:buy`, `wedge:...`). No sleep-polling anywhere.

**`GET /health`** adds live introspection: the currently executing engine
action and its state, `executorStuckMs`, and per-queue depth/paused flags.

## Three ways to run, one bridge

```sh
# 1. the game, windowed — or
# 2. engine-headless: the game process with no window (Steam required)
./build.sh headless

# 3. pure .NET host — no game binary, no Godot engine, no Steam
./build.sh headless-setup   # one-time: deps + IL patch + loc tables + build
./build.sh host

./build.sh stop             # stops whichever is running
```

**In-game** boots load the mod inside the game process and drive the real
UI. The **host** boot loads an IL-patched `sts2.dll` against a stub
GodotSharp in a plain .NET process; `RunMode` branches swap UI nodes for
the engine's model-layer test entry points (`RunState.CreateForTest`,
deferred `ICardSelector`s, a virtual rewards flow). Same protocol either
way — in host mode actions also resolve inline, so `/step` returns with
the effect already applied.

Host-mode differences: saves don't persist (runs are test-mode), card
pickers auto-resolve once max cards are picked (`confirm` covers partial
picks), and text comes from tables extracted out of your local install's
`.pck` (`STS2_AGENT_LANG=zhs` etc. to switch).

## What's covered

- **A full run, both boots**: Neow to the finale, victory or defeat —
  including the ascension-10 two-boss ending (the first boss exits back to
  the map; `obs.next` carries the second).
- **Every event**: all 57 event models render and interact
  (`tests/eventsweep.py` forces each one via `cheat event`).
- **Every selection surface**: deck pickers (rest-site upgrade, shop
  removal, transforms), choose-a-card offers, mid-combat hand selects,
  Neow's card-pack bundles, the crystal-sphere minigame (`map-move`
  clicks a cell, `option 0/1` picks the divination tool).
- **Reproducibility**: `new-run --seed --ascension`.
- **Dev cheats** for single-point verification:
  `cheat goto|gold|hp|heal|wound-enemies|event` — jump anywhere on the
  act map, end fights fast, force any event room by model id. No full-run
  replay needed to test one phase.

## Testing

```sh
./build.sh verify        # drives one act-1 loop against BOTH boots, then
                         # diffs the recorded snapshot key sets — same
                         # phase must expose the same keys in each
python3 tests/eventsweep.py   # force + exercise all 57 events (minutes)
```

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
| `STS2_HOST_DEBUG`   | unset                  | host only — `1` prints first-chance exception stacks to stderr  |

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

Remote access or auth (loopback only), multiplayer, replay recording, a
TUI. For those, this project's bigger sibling exists; spirescry stays the
minimal single-player agent interface.

---

Not affiliated with MegaCrit. Requires your own copy of Slay the Spire 2;
no game assets or code are distributed with this repository.
