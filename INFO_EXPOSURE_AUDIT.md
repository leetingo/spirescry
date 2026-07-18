# Decision-information exposure audit

Audited 2026-07-12 against the GUI behavior in `sts2.dll`, the bridge
snapshot/dispatcher implementation, and 132 live full-`obs` captures covering
all 57 forceable events and every reachable special phase.

The raw captures were intentionally kept outside the repository because they
are local diagnostic artifacts. Reproducible claims from them are encoded in
the `I1`–`I5` public E2E cases. The audit distinguishes information shown by
the GUI from engine-internal information that neither a human nor an agent
should receive.

## Fixed in this branch

### Event observation mutated the event

`EventSnapshot` called `EventModel.CalculateVars()` on every `/obs`. The engine
already calls it once at event entry. Several implementations mutate RNG and
state there:

- `ENDLESS_CONVEYOR`: the advertised dish changed on every read, while clicking
  executed the newly rolled dish. A capture advertised Caviar (+4 max HP) and
  actually granted the Clam Roll heal.
- `LOST_WISP`: its 60±15 gold roll random-walked per read; a live capture reached
  44 gold, outside the legal one-roll range.
- `JUNGLE_MAZE_ADVENTURE`: both gold values drifted and reads consumed shared
  event RNG.

Snapshots are now read-only, with an E2E test comparing consecutive event
observations.

### GUI-only event decision payloads were absent

Event options now expose:

- `lethal`: the current-player result of the GUI's `WillKillPlayer` red warning;
- `hints[]`: the card faces and text/model hover tips displayed by the GUI;
- `relic`: the attached ancient-option relic with model, title, and description.

This covers the blind choices found in Doll Room, Drowning Beacon, Field of
Man-Sized Holes, Grave of the Forgotten, Lost Wisp, and the same pattern in
other events. Event page descriptions also receive the character and
`IsMultiplayer` variables used by the GUI, removing leaked template text.

### Fake Merchant was invisible and undrivable

The custom event previously appeared as `finished:true`, description
`Placeholder`, and zero options. Its six relics are now exposed under
`fakeMerchant.relics[]` with model, title, description, price, stock, and
affordability. The existing `buy relic --idx N` contract now accepts this custom
merchant and was verified to spend gold, grant the relic, and mark the tile sold.

## Confirmed remaining gaps

Ordered by expected decision impact:

1. **Fake Merchant Foul Potion fight.** `fakeMerchant.canFight` reports whether
   the special route is available, but the bridge still needs a headless-safe
   verb that consumes the potion and invokes the event combat/reward path.
2. **Crystal Sphere partial-item identity.** The GUI's fog reveals item art,
   rarity, and footprint wherever a cell has been uncovered. JSON currently
   reports only `hasItem`; it should expose type/rarity/origin/size for an item
   only after at least one of its cells is visible.
3. **Map decision metadata beyond routing.** Graph edges are exposed, but quest
   markers, ancient identity, and traveled-node history shown by the GUI are
   still absent.
4. **Combat run context and special resources.** Combat now exposes relic
   counters and the orb queue, but still omits the master deck, gold,
   act/floor/ascension, and pets. These should be added only where the GUI
   exposes them and without leaking draw order.
5. **Picker intent.** Headless card selectors expose an empty prompt, so the
   agent must infer upgrade/remove/transform/discard from the preceding action.
6. **Degradation markers.** Text, powers, intents, card vars, reflection-backed
   collections, and the 64-entry signal log can silently degrade to plausible
   empty/null values. Responses should identify degraded/truncated fields.

## Non-issues established by the sweep

- `relic_reward` is defensive dead-code coverage in this game build. The only
  engine opener has no call sites; treasure, elite, boss, and Doll Room relics
  all use other flows.
- Empty intents on the Battleworn Dummy are valid; a separate three-enemy
  capture confirmed populated intent exposure.
- Draw-pile order remains intentionally hidden and sorted. Exposing it would
  leak information unavailable to the GUI player.
