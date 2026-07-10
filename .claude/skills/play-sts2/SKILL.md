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

Host quirks: runs never save (each boot starts clean), and card pickers
auto-confirm at max picks — `confirm` accepts a partial pick.

## The loop

1. `spirescry new-run IRONCLAD` (`--seed ABC123 --ascension 5`
   reproduces a run). Landed when `obs` shows phase `event` — Neow
   greets every run.
2. **Scry**: `spirescry obs` → read `phase`, `rev`, and the board; decide.
3. **Act**: fire one verb from the table below.
4. **Wait**: `spirescry obs --since <rev> --wait 5000` with the rev you
   scried in step 2 — it returns the instant the world changes, and its
   `events` name what happened. `changed: false` means nothing did:
   rescry and reassess. (A verb response prints a `rev` of its own — that
   marks acceptance; keep waiting on the scried one.)
5. Repeat 2–4 until `game_over`; report outcome / floor / seed, then
   `abandon` to return to the menu.

One verb, then its wait — the wait's `events` naming your action is the
confirmation, and the world changing (a card leaving your hand, hp
moving) is the resolution. Batching verbs skips both checks and builds
the next decision on a state you never confirmed.

Rejections name their fix: exit 75 (`not_ready`) → retry the same verb;
`bad_phase` → rescry; `not_enough_energy` / `not_enough_stars` /
`bad_target` / `bad_index` → decide differently. On long runs,
`obs --compact` keeps snapshots small (piles and deck as counts, card
prose dropped, `vars` kept).

## The ledger

Keep a three-line ledger in your head across the turn, updated after
every wait, instead of re-deriving from full JSON each read:

```
you:    hp / block / energy / stars
threat: enemy hp / incoming damage × hits / stacks that gate damage
turn:   cards played so far, potions left
```

Arithmetic mistakes at the final turn kill runs; the ledger is where
you catch them.

## Wedge recovery

A `wedge:<Action>` event means an engine action died mid-resolution —
its effects may be lost (energy spent, no damage dealt). Climb the
ladder; abandon is the last rung:

1. Rescry — the game usually keeps accepting verbs.
2. Fire a legal verb; `end-turn` usually works.
3. The run advances → note the loss and keep playing.
4. Every verb rejecting repeatedly → `spirescry abandon`, start fresh.

## Impossible observations

Resources moving at a frozen `rev`, a claim that never lands, an effect
whose promise doesn't match the world — that's a bridge fault, not a
puzzle to poll at. One rescry → `spirescry health` → `proceed` out of
the phase → **mark the run polluted**: keep playing if you can, but say
so in your report and treat conclusions drawn from the polluted state
as suspect. File what you saw.

## Verbs by phase

Every phase outside combat also carries a `player` footer (hp, gold,
potions, relics, deck — enchanted cards show `enchant`).

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
| `card_select`, `hand_select` | picker cards with `cost` and `upgradedPreview` | `pick-card <idx>` (toggles), `confirm`, `skip` (if cancelable) |
| `bundle_select` | card packs (e.g. Neow) | `pick-card <idx>` |
| `crystal_sphere` | divination minigame | `map-move <col> <row>` picks a cell, `option 0`/`1` picks the tool |
| `game_over` | outcome, floor, act, seed | `abandon` → main menu |
| any (in a run) | — | `potion-discard <slot>`, `abandon` |

## Reading combat

- `hp` and `energy` are `[current, max]` pairs.
- `hand[]` cards carry `description` (the card face text) and `vars` —
  the numbers behind it with Strength/Weak already applied
  (`{"Damage": 8}`) — plus `model` (the exact string `play` wants),
  `cost`, `target`, `unplayable`.
- `enemies[]`: `id` is the `--target` value; `intents[]` show `damage` ×
  `hits` (nulls mean a non-attack).
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
