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

From the repo root:

```sh
./build.sh host        # boots the windowless game; prints "bridge up"
spirescry health       # → {"ok": true, ...} confirms it's alive
./build.sh stop        # shut it down when you're done
```

- "host not built" → run the first-time setup above, then retry.
- "host already running" → it's live; just play (or `stop` first to restart).

Host quirks: runs never save (each boot starts clean), and card pickers
auto-confirm the moment you've picked the maximum — use `confirm` only to
accept fewer picks. `STS2_AGENT_LANG=zhs ./build.sh host` switches card and
event text to another language.

## The loop

1. `spirescry obs` → read `phase` and `rev`, decide.
2. Fire **one** verb.
3. `spirescry obs --since <rev> --wait 5000` → returns the moment the world
   changes (`"changed": false` after the timeout means nothing happened —
   re-read and reassess).

Rules:

- One decision per command. Never sleep-poll — `--since`/`--wait` does the
  waiting for you.
- Wait on the `rev` you read from `obs` **before** acting. Verb responses
  print a `rev` too — ignore it: it marks acceptance, not the outcome.
- Exit code 75 (`not_ready`) is transient by contract: retry the same command.
- Any other rejection names what to fix: `bad_phase` (re-read `obs`),
  `not_enough_energy`, `bad_target`, `bad_index`, …
- In a sandboxed executor, the first `spirescry` call may fail with
  "Operation not permitted" — approve loopback access to `127.0.0.1:7777`
  and retry; the bridge never leaves localhost.
- On a `wedge:*` event, follow the recovery ladder below — don't jump to
  abandon.

## Recovering from a wedge

A `wedge:<Action>` event means an engine action died mid-resolution — its
effects may be lost (e.g. energy spent, no damage dealt). In order:

1. Re-read `obs`: the game usually keeps accepting verbs.
2. Try a legal verb — `end-turn` typically still works.
3. If the run advances, note what was lost and keep playing.
4. `spirescry abandon` only when every verb keeps rejecting.

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
| `event` | `options[]` (idx/title/description/locked) | `option <idx>` — some options only mark `chosen: true` and need a `proceed` after; `proceed` also pages dialogue and leaves once `finished`. Rule: after `option`, re-read — still in the event with nothing new to pick? `proceed`. |
| `rest_site` | `options[]` | `option <idx>` (upgrade opens a deck picker); when you're done (`options` empty), `proceed` → map |
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
- X-cost cards (`WHIRLWIND`) show `cost` = your current remaining energy
  while in combat (and 0 on reward pages): playing one spends everything
  and does the effect X times.

Example turn:

```sh
spirescry obs                              # phase: combat, rev: 10
spirescry play STRIKE_IRONCLAD --target 1
spirescry obs --since 10 --wait 5000       # enemy hp dropped, rev: 12
spirescry end-turn
spirescry obs --since 12 --wait 5000       # enemies acted, your turn again
```
