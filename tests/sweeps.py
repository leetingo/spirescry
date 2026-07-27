"""Exhaustive content sweeps: every encounter, card, potion, relic.

Each sweep exercises the atom once without faulting the bridge: combat
loads and resolves; every playable card's OnPlay runs while cards barred
by their own legality rules reject cleanly; potions fire; relic obtain
hooks land. Combinatorial interactions stay out of scope (they're
sampled by parity/V1).

Most content is directly executable: injecting the model is the same
thing the game does. A few models are context-bound — their event or
reward factory stamps a saved property first and the model's own code
assumes it (MAD_SCIENCE gets its card type from TINKER_TIME, SEA_GLASS
its owning character from OROBAS). Raw injection of those is a broken
fixture, not gameplay, so the bridge applies the construction context at
injection and advertises it as `context` on the /models entry. These
sweeps read that flag and insist every context-bound model is actually
exercised — a fixture that quietly stops applying must not read as a
clean sweep.

All sweeps assume a live bridge (tests/e2e.py boots one) and leave the
world at the main menu. Each returns a dict of failures: {} == clean.

Faults that are already filed as open product issues live in QUARANTINE:
they are still swept and still reported, but they do not fail the gate, and
they fail it again the moment they start passing. See QUARANTINE.
"""
import json
import os
import sys
import urllib.request

import bridge

run, obs = bridge.run, bridge.obs

# A boss sandbag: one big-HP enemy, no adds, verified to load via the
# combat cheat. Override if a game update retires it.
SANDBAG = "AEONGLASS_BOSS"

POOL_CHARACTER = {
    "ironclad": "IRONCLAD",
    "silent": "SILENT",
    "defect": "DEFECT",
    "necrobinder": "NECROBINDER",
    "regent": "REGENT",
}
MAP_CLAIMS = {"claim_reward_tiles": True}
TRANSIENT_CLAIMS = {
    "claim_reward_tiles": True,
    "claim_card_reward": True,
    "claim_relic_reward": True,
}


def follow_result(*args, timeout_ms=10000):
    """Return a followed action envelope while preserving rejection text."""
    result = run(
        *args, "--follow", str(timeout_ms), allow_fail=True,
        timeout=max(1, timeout_ms / 1000),
    )
    if "_err" in result:
        return result
    if result.get("settled") is not True or not isinstance(result.get("obs"), dict):
        return {"_err": (
            f"did not settle ({result.get('outcome')}); "
            f"obs={type(result.get('obs')).__name__}"
        )}
    return result


def model_entries(kind):
    port = os.environ.get("STS2_AGENT_PORT", "7777")
    with urllib.request.urlopen(
            f"http://127.0.0.1:{port}/models?kind={kind}", timeout=30) as r:
        return json.load(r)["entries"]


def context_bound(entries):
    """The models the bridge has to construct with an event/character
    context. They must be exercised, never skipped: a fixture that stops
    applying would otherwise hide behind a clean legality rejection."""
    return {e["model"] for e in entries if e.get("context")}


def unexercised(bound, exercised):
    """A named failure for every context-bound model that never ran."""
    return {model: "context-bound model was never exercised"
            for model in sorted(bound - exercised)}


def fresh_run(seed="SWEEP", character="IRONCLAD"):
    d = obs()
    if d.get("phase") != bridge.PHASE.MAIN_MENU:
        run("abandon")
        bridge.wait_phase(
            bridge.PHASE.MAIN_MENU, after_rev=d["rev"])
    bridge.launch_run(
        character=character, seed=seed, timeout=30)
    # Neow owes the seat a decision; proceed only opens once it is taken.
    bridge.walk_world(bridge.PHASE.MAP)


def wedge_events(since):
    return [e["type"] for e in run("obs", "--since", str(since), "--wait", "300")
            .get("events", []) if e["type"].startswith("wedge:")]


def enter_sandbag():
    settled = bridge.walk_world(bridge.PHASE.MAP, **MAP_CLAIMS)
    if settled["phase"] != bridge.PHASE.MAP:
        fresh_run()
        settled = obs()
    before_rev = settled["rev"]
    run("cheat", bridge.PHASE.COMBAT, SANDBAG)
    return bridge.wait_until(
        lambda snapshot: snapshot.get("phase") == bridge.PHASE.COMBAT
        and snapshot.get("side") == "player",
        description="sandbag combat player turn",
        after_rev=before_rev,
    )


# ---------- sweep: every encounter ----------

def encounters(log=print):
    """Force every encounter, watch it load (titled, intent-bearing
    enemies), kill it through the real pipeline, leave cleanly."""
    failures = {}
    ids = [e["model"] for e in model_entries("encounter")]
    log(f"{len(ids)} encounters to sweep")
    fresh_run()
    for i, enc in enumerate(ids):
        try:
            settled = bridge.walk_world(bridge.PHASE.MAP, **MAP_CLAIMS)
            if settled["phase"] != bridge.PHASE.MAP:
                fresh_run()
            rev = obs()["rev"]
            r = run("cheat", bridge.PHASE.COMBAT, enc, allow_fail=True)
            if "_err" in r:
                failures[enc] = f"force: {r['_err'][:90]}"
                continue
            d = bridge.wait_until(
                lambda snapshot: snapshot.get("phase") == bridge.PHASE.COMBAT
                and snapshot.get("side") == "player"
                and bool(snapshot.get("enemies")),
                description=f"encounter {enc} player turn",
                after_rev=rev,
            )
            bad = [e for e in d.get("enemies", []) if not e.get("title")]
            if not d.get("enemies") or bad:
                failures[enc] = f"load: enemies={d.get('enemies')}"
                continue
            bridge.kill_current_combat()
            w = wedge_events(rev)
            if w:
                failures[enc] = f"wedge after kill: {w}"
                fresh_run()
                continue
            settled = bridge.walk_world(bridge.PHASE.MAP, **MAP_CLAIMS)
            if settled["phase"] != bridge.PHASE.MAP:
                fresh_run()
        except (AssertionError, SystemExit) as e:
            failures[enc] = str(e)[:120]
            fresh_run()
        if (i + 1) % 10 == 0:
            log(f"  ...{i + 1}/{len(ids)} ({len(failures)} failures)")
    run("abandon", allow_fail=True)
    return failures


# ---------- sweep: every card ----------

def cards(log=print, only=None):
    """Graft every card into the hand and play it once against the
    sandbag. Unplayable-by-design cards (curses/statuses) are verified
    to reject as unplayable rather than fault."""
    failures = {}
    skipped = []
    executed = set()
    playable_attempts = 0
    playable_executed = 0
    entries = sorted(model_entries("card"), key=lambda e: (
        POOL_CHARACTER.get(e.get("pool"), "IRONCLAD"), e["model"]))
    if only is not None:
        entries = [e for e in entries if e["model"] in only]
    bound = context_bound(entries)
    log(f"{len(entries)} cards to sweep")
    active_character = POOL_CHARACTER.get(entries[0].get("pool"), "IRONCLAD")
    fresh_run(character=active_character)
    d = enter_sandbag()
    plays_in_fight = 0

    for i, entry in enumerate(entries):
        card = entry["model"]
        character = POOL_CHARACTER.get(entry.get("pool"), "IRONCLAD")
        try:
            if character != active_character:
                active_character = character
                fresh_run(character=active_character)
                d = enter_sandbag()
                plays_in_fight = 0
            elif obs()["phase"] != bridge.PHASE.COMBAT or plays_in_fight >= 25:
                # bound power/deck pollution; also recovers ended fights
                fresh_run(character=active_character)
                d = enter_sandbag()
                plays_in_fight = 0
            bridge.follow("cheat", "heal")
            bridge.follow("cheat", "energy", "99")
            d = bridge.follow("cheat", "stars", "99")
            if len(d["hand"]) >= 9:  # keep room for the graft
                d = bridge.follow("end-turn", timeout_ms=30000)
                if d["phase"] != bridge.PHASE.COMBAT:
                    fresh_run(character=active_character)
                    d = enter_sandbag()
                else:
                    bridge.follow("cheat", "heal")
                    bridge.follow("cheat", "energy", "99")
                    d = bridge.follow("cheat", "stars", "99")
            r = follow_result("cheat", "card", card)
            if "_err" in r:
                failures[card] = f"graft: {r['_err'][:90]}"
                continue
            d = r["obs"]
            mine = next((c for c in d.get("hand", []) if c["model"] == card), None)
            if mine is None:
                failures[card] = "grafted card never reached the hand"
                continue
            if mine.get("unplayable"):
                rejected = follow_result("play", card)
                if "_err" not in rejected:
                    failures[card] = "unplayable card was accepted"
                elif f"not_playable: {card}:" not in rejected["_err"]:
                    failures[card] = (
                        "unplayable card rejected with wrong error: "
                        f"{rejected['_err'][:100]}")
                else:
                    skipped.append(card)  # rejected cleanly — by design
                continue
            playable_attempts += 1
            rev = d["rev"]
            args = ["play", card]
            if mine.get("target") == "anyenemy":
                alive = [e for e in d["enemies"] if e["alive"]]
                if not alive:
                    fresh_run(character=active_character)
                    d = enter_sandbag()
                    continue
                args += ["--target", str(alive[0]["id"])]
            followed = follow_result(*args)
            if "_err" in followed:
                err = followed["_err"]
                if f"not_playable: {card}:" in err:
                    # Some cards require a state the single-player atomic
                    # sandbox cannot generically manufacture (empty draw
                    # pile, multiplayer handshake, only attacks in hand).
                    # A named, clean legality rejection is the correct
                    # protocol behavior; internal/timeout/wedge still fail.
                    skipped.append(f"{card}: {err.split('not_playable:', 1)[1].strip()}")
                else:
                    failures[card] = f"play: {err[:110]}"
                continue
            plays_in_fight += 1
            playable_executed += 1
            executed.add(card)
            ph = bridge.walk_world(
                initial=followed["obs"], **TRANSIENT_CLAIMS)["phase"]
            w = wedge_events(rev)
            if w:
                failures[card] = f"wedge: {w}"
                fresh_run(character=active_character)
                d = enter_sandbag()
                plays_in_fight = 0
            elif ph != bridge.PHASE.COMBAT:
                # the play legitimately ended the fight (kill, escape…)
                d = enter_sandbag()
                plays_in_fight = 0
        except (AssertionError, SystemExit) as e:
            failures[card] = str(e)[:120]
            fresh_run(character=active_character)
            d = enter_sandbag()
            plays_in_fight = 0
        if card in failures:
            log(f"  FAIL {card}: {failures[card]}")
        if (i + 1) % 50 == 0:
            log(f"  ...{i + 1}/{len(entries)} ({len(failures)} failures)")
    log(f"  cleanly rejected by card legality: {len(skipped)}")
    if bound:
        log(f"  context-bound cards executed: "
            f"{len(bound & executed)}/{len(bound)} ({','.join(sorted(bound))})")
        # A model that already failed keeps its own, more specific reason.
        failures.update({model: why for model, why
                         in unexercised(bound, executed).items()
                         if model not in failures})
    # A named legality rejection is valid for cards that require a state the
    # generic sandbag cannot manufacture, but it must not let a broken play
    # path turn the whole sweep green. Require the large majority of cards
    # advertised as playable to actually enter the engine's play pipeline.
    if only is None and playable_attempts:
        ratio = playable_executed / playable_attempts
        log(f"  playable cards executed: {playable_executed}/{playable_attempts} ({ratio:.1%})")
        if ratio < 0.90:
            failures["__coverage__"] = (
                f"only {playable_executed}/{playable_attempts} playable cards executed")
    run("abandon", allow_fail=True)
    if failures and only is None:
        first_pass = set(failures)
        log(f"  retrying {len(first_pass)} first-pass failures in isolated combats")
        failures = cards(log=log, only=first_pass)
        recovered = sorted(first_pass - set(failures))
        if recovered:
            log(f"  recovered from batch pollution: {','.join(recovered)}")
    return failures


# ---------- sweep: every potion ----------

def potions(log=print, only=None):
    """Procure and drink every potion; combat-gated ones fire against
    the sandbag, the rest on the map."""
    failures = {}
    ids = [e["model"] for e in model_entries("potion")]
    if only is not None:
        ids = [p for p in ids if p in only]
    log(f"{len(ids)} potions to sweep")
    fresh_run()
    enter_sandbag()
    used_in_fight = 0
    for i, pot in enumerate(ids):
        try:
            if obs()["phase"] != bridge.PHASE.COMBAT or used_in_fight >= 20:
                fresh_run()
                enter_sandbag()
                used_in_fight = 0
            bridge.follow("cheat", "heal")
            r = follow_result("cheat", "potion", pot)
            if "_err" in r:
                failures[pot] = f"procure: {r['_err'][:90]}"
                continue
            d = r["obs"]
            slot = next((p for p in d.get("potions", [])
                         if p["model"] == pot), None)
            if slot is None:
                failures[pot] = "procured potion never reached the belt"
                continue
            rev = d["rev"]
            args = ["potion-use", str(slot["slot"])]
            if slot.get("target") == "anyenemy":
                alive = [e for e in d["enemies"] if e["alive"]]
                args += ["--target", str(alive[0]["id"])]
            followed = follow_result(*args)
            if "_err" in followed:
                # not usable here (out-of-combat potion?) — try the map
                settled = bridge.walk_world(bridge.PHASE.MAP, **MAP_CLAIMS)
                if settled["phase"] != bridge.PHASE.MAP:
                    fresh_run()
                followed = follow_result(*args)
                if "_err" in followed:
                    failures[pot] = f"use: {followed['_err'][:110]}"
                enter_sandbag()
                used_in_fight = 0
                continue
            used_in_fight += 1
            d = bridge.walk_world(
                initial=followed["obs"], **TRANSIENT_CLAIMS)
            w = wedge_events(rev)
            if w:
                failures[pot] = f"wedge: {w}"
                fresh_run()
                enter_sandbag()
                used_in_fight = 0
                continue
            if d.get("phase") == bridge.PHASE.COMBAT and any(
                    p["slot"] == slot["slot"] and p["model"] == pot
                    for p in d.get("potions", [])):
                failures[pot] = "drink did not clear the slot"
        except (AssertionError, SystemExit) as e:
            failures[pot] = str(e)[:120]
            fresh_run()
            enter_sandbag()
            used_in_fight = 0
        if (i + 1) % 20 == 0:
            log(f"  ...{i + 1}/{len(ids)} ({len(failures)} failures)")
    run("abandon", allow_fail=True)
    return failures


# ---------- sweep: every relic ----------

def relics(log=print):
    """Grant every relic in one run and verify each obtain hook lands.

    Fighting with every relic at once is deliberately out of scope: it
    creates an impossible reward-alternative combination and contradicts
    this module's atomic, not combinatorial, coverage contract.
    """
    failures = {}
    entries = model_entries("relic")
    ids = [e["model"] for e in entries]
    bound = context_bound(entries)
    log(f"{len(ids)} relics to sweep")
    fresh_run("SWEEPREL")

    def grant_and_settle(relic):
        r = follow_result("cheat", "relic", relic)
        if "_err" in r:
            if "not_playable:" in r["_err"]:
                return "LEGAL_REJECT"
            return f"grant: {r['_err'][:90]}"
        try:
            settled = bridge.walk_world(
                initial=r["obs"], **TRANSIENT_CLAIMS)
        except AssertionError as e:
            return f"obtain picker: {str(e)[:90]}"
        if settled["phase"] != bridge.PHASE.MAP:
            return f"obtain hook settled at {settled['phase']}, expected map"
        if relic not in settled["player"]["relics"]:
            return "obtain completed but relic is absent from inventory"
        return None

    legal_rejects = 0
    verified = 0
    exercised = set()
    for i, relic in enumerate(ids):
        error = grant_and_settle(relic)
        if error == "LEGAL_REJECT":
            legal_rejects += 1
            continue
        if error:
            # A belt full of unrelated relics can create impossible hook
            # combinations. Retry the causal relic alone before calling it
            # a product failure, then continue from that clean run.
            log(f"  retry {relic} in isolation: {error}")
            fresh_run(f"SWEEPREL{i}")
            error = grant_and_settle(relic)
        if error == "LEGAL_REJECT":
            legal_rejects += 1
            continue
        if error:
            failures[relic] = error
            if relic not in QUARANTINE["relics"]:
                # An untracked obtain fault can wedge the run; stop rather
                # than blame every remaining relic on it.
                return failures
            # A tracked fault must not cost the rest of the belt its sweep.
            fresh_run(f"SWEEPREL{i}")
            continue
        verified += 1
        exercised.add(relic)
        if (i + 1) % 50 == 0:
            n = len(obs()["player"]["relics"])
            log(f"  ...{i + 1}/{len(ids)} verified (current belt shows {n})")
    log(f"  {verified} legal obtain hooks completed; "
        f"{legal_rejects} context-ineligible relics rejected cleanly")
    if bound:
        log(f"  context-bound relics exercised: "
            f"{len(bound & exercised)}/{len(bound)} ({','.join(sorted(bound))})")
        failures.update(unexercised(bound, exercised))
    run("abandon", allow_fail=True)
    return failures


# The sweep kinds this module offers, by the argument that selects one.
# Module-level so tests/gate_coverage_test.py can check that the pre-merge
# gate really runs an e2e case for every kind — a new sweep added here
# without a case would otherwise never run.
SWEEPS = {
    "encounters": encounters,
    "cards": cards,
    "potions": potions,
    "relics": relics,
}

# Content faults already tracked as open product issues. The sweep still
# runs the entry and still prints what it did, but a tracked fault does not
# fail the pre-merge gate: an unrelated PR must not be blocked by a bug it
# did not cause, because the only unblock anyone reaches for is putting
# --quick back (see tests/gate_coverage_test.py for why that must not
# happen). Everything else still fails, so a NEW fault is caught the day it
# appears, and a quarantined entry that starts passing fails the sweep —
# the list can only ever shrink, and the issue's fix is what empties it.
QUARANTINE = {
    "encounters": {"SLUMBERING_BEETLE_NORMAL": 148},
    "cards": {"MAD_SCIENCE": 149, "CHARGE": 147, "HIDDEN_DAGGERS": 147},
    "potions": {
        "GIGANTIFICATION_POTION": 148,
        "LIQUID_MEMORIES": 148,
        "MOCK_DISCARD_AND_ADD_SHIVS_POTION": 147,
    },
    "relics": {"SEA_GLASS": 149},
}


def partition(kind, failures):
    """Split a sweep's failures into (blocking, tracked-by-an-open-issue)."""
    known = QUARANTINE.get(kind, {})
    blocking = {n: w for n, w in failures.items() if n not in known}
    tracked = {n: (known[n], w) for n, w in failures.items() if n in known}
    return blocking, tracked


def stale_quarantine(kind, failures):
    """Quarantined entries the sweep no longer fails on. The fix landed (or
    the entry was renamed away), so the entry has to go — otherwise a
    quarantine outlives its bug and silently hides the next one."""
    return sorted(n for n in QUARANTINE.get(kind, {}) if n not in failures)


if __name__ == "__main__":
    sweep = sys.argv[1] if len(sys.argv) in (2, 3) else ""
    # The per-entry sweeps take an id list, which re-verifies a single fix in
    # seconds instead of paying for the whole sweep. Filtered runs skip the
    # coverage floor and the batch-pollution retry, so they are a debugging
    # aid, not a substitute for the full sweep.
    filterable = {"cards", "potions"}
    filtered = len(sys.argv) == 3
    if sweep not in SWEEPS:
        sys.exit("usage: sweeps.py " + "|".join(SWEEPS) + " [ID,ID,...]")
    if filtered and sweep not in filterable:
        sys.exit(f"sweeps.py {sweep} cannot be filtered by id")
    failed = (SWEEPS[sweep](only=set(sys.argv[2].split(",")))
              if filtered else SWEEPS[sweep]())
    blocking, tracked = partition(sweep, failed)
    for name, (issue, why) in sorted(tracked.items()):
        print(f"SWEEP KNOWN FAILURE (#{issue}): {name}: {why}")
    # A filtered run never touches most of the map, so its silence says
    # nothing about whether an entry still fails — only a full sweep can
    # retire a quarantine entry.
    for name in ([] if filtered else stale_quarantine(sweep, failed)):
        blocking[name] = (
            f"quarantined for #{QUARANTINE[sweep][name]} but the sweep passed"
            " — drop it from sweeps.QUARANTINE")
    # Name the failures — a bare exit code forces a full re-run under a
    # debugger just to learn WHICH entry broke.
    for name, why in blocking.items():
        print(f"SWEEP FAILURE: {name}: {why}")
    sys.exit(1 if blocking else 0)
