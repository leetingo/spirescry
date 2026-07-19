"""Exhaustive content sweeps: every encounter, card, potion, relic.

Each sweep exercises the atom once without faulting the bridge: combat
loads and resolves; every playable card's OnPlay runs while cards barred
by their own legality rules reject cleanly; potions fire; relic obtain
hooks land. Combinatorial interactions stay out of scope (they're
sampled by parity/V1).

All sweeps assume a live bridge (tests/e2e.py boots one) and leave the
world at the main menu. Each returns a dict of failures: {} == clean.
"""
import json
import os
import sys
import time
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


def model_entries(kind):
    port = os.environ.get("STS2_AGENT_PORT", "7777")
    with urllib.request.urlopen(
            f"http://127.0.0.1:{port}/models?kind={kind}", timeout=30) as r:
        return json.load(r)["entries"]


def fresh_run(seed="SWEEP", character="IRONCLAD"):
    run("abandon", allow_fail=True)
    time.sleep(0.4)
    bridge.launch_run(
        character=character, seed=seed, timeout=30,
        allow_first_failure=True)
    run("proceed", allow_fail=True)
    bridge.wait_phase(bridge.PHASE.MAP, timeout=20)


def wedge_events(since):
    return [e["type"] for e in run("obs", "--since", str(since), "--wait", "300")
            .get("events", []) if e["type"].startswith("wedge:")]


def enter_sandbag():
    settled = bridge.walk_world(bridge.PHASE.MAP, **MAP_CLAIMS)
    if settled["phase"] != bridge.PHASE.MAP:
        fresh_run()
    run("cheat", bridge.PHASE.COMBAT, SANDBAG)
    d = bridge.wait_phase(bridge.PHASE.COMBAT, timeout=20)
    for _ in range(10):
        if d.get("side") == "player":
            break
        time.sleep(1)
        d = obs()
    return d


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
            d = bridge.wait_phase(bridge.PHASE.COMBAT, timeout=20)
            for _ in range(10):
                if d.get("side") == "player" and d.get("enemies"):
                    break
                time.sleep(1)
                d = obs()
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
    playable_attempts = 0
    playable_executed = 0
    entries = sorted(model_entries("card"), key=lambda e: (
        POOL_CHARACTER.get(e.get("pool"), "IRONCLAD"), e["model"]))
    if only is not None:
        entries = [e for e in entries if e["model"] in only]
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
            run("cheat", "heal", allow_fail=True)
            run("cheat", "energy", "99", allow_fail=True)
            run("cheat", "stars", "99", allow_fail=True)
            d = obs()
            if len(d["hand"]) >= 9:  # keep room for the graft
                run("end-turn", allow_fail=True)
                time.sleep(1.2)
                run("cheat", "heal", allow_fail=True)
                run("cheat", "energy", "99", allow_fail=True)
                run("cheat", "stars", "99", allow_fail=True)
                d = obs()
                if d["phase"] != bridge.PHASE.COMBAT:
                    fresh_run(character=active_character)
                    d = enter_sandbag()
            r = run("cheat", "card", card, allow_fail=True)
            if "_err" in r:
                failures[card] = f"graft: {r['_err'][:90]}"
                continue
            time.sleep(0.3)
            d = obs()
            mine = next((c for c in d.get("hand", []) if c["model"] == card), None)
            if mine is None:
                failures[card] = "grafted card never reached the hand"
                continue
            if mine.get("unplayable"):
                res = bridge.cli("play", card)
                if res.returncode == 0:
                    failures[card] = "unplayable card was accepted"
                elif f"not_playable: {card}:" not in res.stderr:
                    failures[card] = (
                        "unplayable card rejected with wrong error: "
                        f"{res.stderr.strip()[:100]}")
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
            res = bridge.cli(*args)
            if res.returncode != 0:
                err = res.stderr.strip()
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
            ph = bridge.walk_world(**TRANSIENT_CLAIMS)["phase"]
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

def potions(log=print):
    """Procure and drink every potion; combat-gated ones fire against
    the sandbag, the rest on the map."""
    failures = {}
    ids = [e["model"] for e in model_entries("potion")]
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
            run("cheat", "heal", allow_fail=True)
            r = run("cheat", "potion", pot, allow_fail=True)
            if "_err" in r:
                failures[pot] = f"procure: {r['_err'][:90]}"
                continue
            time.sleep(0.3)
            d = obs()
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
            res = bridge.cli(*args)
            if res.returncode != 0:
                # not usable here (out-of-combat potion?) — try the map
                settled = bridge.walk_world(bridge.PHASE.MAP, **MAP_CLAIMS)
                if settled["phase"] != bridge.PHASE.MAP:
                    fresh_run()
                res = bridge.cli(*args)
                if res.returncode != 0:
                    failures[pot] = f"use: {res.stderr.strip()[:110]}"
                enter_sandbag()
                used_in_fight = 0
                continue
            used_in_fight += 1
            bridge.walk_world(**TRANSIENT_CLAIMS)
            w = wedge_events(rev)
            if w:
                failures[pot] = f"wedge: {w}"
                fresh_run()
                enter_sandbag()
                used_in_fight = 0
                continue
            d = obs()
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
    ids = [e["model"] for e in model_entries("relic")]
    log(f"{len(ids)} relics to sweep")
    fresh_run("SWEEPREL")

    def grant_and_settle(relic):
        r = run("cheat", "relic", relic, allow_fail=True)
        if "_err" in r:
            if "not_playable:" in r["_err"]:
                return "LEGAL_REJECT"
            return f"grant: {r['_err'][:90]}"
        try:
            phase = bridge.walk_world(**TRANSIENT_CLAIMS)["phase"]
        except AssertionError as e:
            return f"obtain picker: {str(e)[:90]}"
        if phase != bridge.PHASE.MAP:
            return f"obtain hook settled at {phase}, expected map"
        if relic not in obs()["player"]["relics"]:
            return "obtain completed but relic is absent from inventory"
        return None

    legal_rejects = 0
    verified = 0
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
            return failures
        verified += 1
        if (i + 1) % 50 == 0:
            n = len(obs()["player"]["relics"])
            log(f"  ...{i + 1}/{len(ids)} verified (current belt shows {n})")
    log(f"  {verified} legal obtain hooks completed; "
        f"{legal_rejects} context-ineligible relics rejected cleanly")
    run("abandon", allow_fail=True)
    return failures


if __name__ == "__main__":
    sweep = sys.argv[1] if len(sys.argv) == 2 else ""
    choices = {
        "encounters": encounters,
        "cards": cards,
        "potions": potions,
        "relics": relics,
    }
    if sweep not in choices:
        sys.exit("usage: sweeps.py encounters|cards|potions|relics")
    sys.exit(1 if choices[sweep]() else 0)
