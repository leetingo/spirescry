---
name: play-sts2
description: Play Slay the Spire 2 (singleplayer) as the agent — boot the windowless host, read the board as JSON with `spirescry obs`, act one verb per decision. Use when the user asks to play STS2, start a run, or watch you play.
---

# Playing Slay the Spire 2

You play by reading a JSON snapshot, deciding, and firing exactly one verb.
No window, no pixels — the game runs headless and `spirescry` is your only
interface. Use only the verbs on this page.

All `./build.sh` commands run from the repo root. If this skill was invoked
from outside the repo, resolve this file's real path (`readlink -f`) — it
lives at `<repo>/.claude/skills/play-sts2/SKILL.md`.

## First-time setup

Prerequisites: your own Slay the Spire 2 install (its files are read from
disk, never distributed), .NET 9, Rust, and python3. On macOS the game is
found automatically; elsewhere set `STS2_GAME_DIR` to the game's binary
directory first.

```sh
./build.sh headless-setup    # prepare the windowless host from your install
./build.sh cli deploy-cli    # build the spirescry command → ~/.local/bin
```

`spirescry: command not found` afterwards means `~/.local/bin` isn't on
your PATH (or set `SPIRESCRY_CLI_BIN` to somewhere that is).

## Boot

From the repo root, run the host as a long-lived task you own (a
background task or persistent terminal — not a fire-and-forget shell:
sandboxed executors reap orphaned background children):

```sh
./build.sh host --foreground   # blocks while the game runs — own it as a background task
spirescry health               # → {"ok": true, ...} confirms it's alive
./build.sh stop                # shut it down when you're done
```

- "host not built" → run the first-time setup above, then retry.
- "host already running" → it's live; just play (or `stop` first to restart).
- Plain `./build.sh host` self-backgrounds with nohup — fine in a real
  terminal, unreliable under sandboxed executors.

Host quirks: runs never save (each boot starts clean), and card pickers
auto-confirm the moment you've picked the maximum — use `confirm` only to
accept fewer picks. `STS2_AGENT_LANG=zhs ./build.sh host --foreground`
switches card and event text to another language.

## The loop

1. `spirescry obs` → read `phase` and `rev`, decide.
2. Fire **one** verb.
3. `spirescry obs --since <rev> --wait 5000` → returns the moment the world
   changes (`"changed": false` after the timeout means nothing happened —
   re-read and reassess).

Rules:

- One decision per command. Never sleep-poll — `--since`/`--wait` does the
  waiting for you.
- Exit code 75 (`not_ready`) is transient by contract: retry the same command.
- Any other rejection names what to fix: `bad_phase` (re-read `obs`),
  `not_enough_energy`, `bad_target`, `bad_index`, …
- If `obs` stops changing and every verb rejects, `spirescry abandon` and
  start a fresh run.

## Starting a run

```sh
spirescry new-run IRONCLAD                # random seed
spirescry new-run IRONCLAD --seed ABC123 --ascension 5
```

A wrong character entry is rejected with the list of valid ones.

## Verbs by phase

`obs.phase` tells you which verbs apply. Every phase outside combat also
carries a `player` footer (hp, gold, potions, relics, deck).

| phase | you see | verbs |
|---|---|---|
| `main_menu` | — | `new-run <CHARACTER>` |
| `map` | `next`: reachable nodes (col/row/type) | `map-move <col> <row>` |
| `combat` | `you`, `hand`, `enemies`, `potions`, `piles`, `turn` | `play <MODEL> [--target <id>]`, `end-turn`, `potion-use <slot> [--target <id>]` |
| `event` | `options[]` (idx/title/description/locked) | `option <idx>`; `proceed` to page dialogue / leave a finished event |
| `rest_site` | `options[]` | `option <idx>` (upgrade opens a deck picker) |
| `shop` | goods with idx + prices | `buy <kind> --idx <n>` (kind: `card`/`colorless`/`relic`/`potion`/`card_removal`), `leave` |
| `treasure`, `relic_reward` | relics on offer | `pick-relic <idx>`, `skip` |
| `rewards` | reward tiles | `pick-reward <idx>`; `proceed` leaves, skipping the rest |
| `card_reward` | cards on offer | `pick-card <idx>`, `skip` |
| `card_select`, `hand_select` | a picker over cards | `pick-card <idx>` (toggles), `confirm`, `skip` (if cancelable) |
| `bundle_select` | card packs (e.g. Neow) | `pick-card <idx>` |
| `crystal_sphere` | divination minigame | `map-move <col> <row>` picks a cell, `option 0`/`1` picks the tool |
| `game_over` | outcome, floor, act, seed | `abandon` → back to the main menu |
| any (in a run) | — | `potion-discard <slot>`, `abandon` |

## Reading combat

- `hp` and `energy` are `[current, max]` pairs.
- `hand[]`: `model` is the exact string `play` wants, plus `cost`, `target`
  (`none`/`anyenemy`/…), `unplayable`, `upgraded`.
- `enemies[]`: `id` is the `--target` value, plus `hp`, `block`, `powers`,
  and `intents[]` (`type`, `damage` × `hits` — nulls mean a non-attack).
- Pass `--target` only when the card targets an enemy; a lone enemy
  auto-targets.

Example turn:

```sh
spirescry obs                              # phase: combat, rev: 10
spirescry play STRIKE_IRONCLAD --target 1
spirescry obs --since 10 --wait 5000       # enemy hp dropped, rev: 12
spirescry end-turn
spirescry obs --since 12 --wait 5000       # enemies acted, your turn again
```
