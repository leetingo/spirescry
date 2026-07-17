#!/usr/bin/env python3
"""Pre-merge end-to-end suite for the spirescry bridge.

Local, not CI — the host is built from the game's own dlls, so this
runs where the game is installed. Run it (--boot) before merging.

Everything drives the pure .NET host (the dll boot: no game binary,
no Steam). Case groups:

  B  boot: /health shape, boot-log assertions (Harmony patches landed,
     ModelDb registered clean, timestamped lines)
  P  protocol: rev monotonicity, long-poll semantics, route/verb/cheat
     rejections with actionable codes
  R  run lifecycle: seeded determinism, every character boots + fights,
     abandon from map and mid-combat (regression: #66)
  C  combat economy: block, energy accounting, bad_target, overdraw
  S  shop: every buy kind with gold accounting, removal picker, leave
  W  skip: card reward + treasure walk away without granting
  X  special screens: crystal-sphere minigame, Neow bundle select
  K  cheat surface: gold / relic / card-upgraded / card graft real state
  V  victory: cheat-driven full clear to a victory game_over
  E  events: all 57 forced; every unlocked option clicked and drained
     to completion (--quick: first option only)
  M  exhaustive content sweeps (tests/sweeps.py): every encounter
     fought, every card attempted (playable effects execute; legality
     rejects stay clean), every potion drunk, every relic obtained
  F  the full act-1 parity loop (tests/parity.py), key sets recorded
  H  request audit trail (STS2_AGENT_HTTP_LOG line format)

  Coverage map — phases: all but relic_reward (no reachable trigger in
  the current game build; pick-relic is exercised in treasure) and
  overlay/unknown (fault phases by design). Verbs: all, including every
  cheat. Content: all cards/potions/relics/encounters/events/characters
  (per /models). Outcomes: victory and defeat (abandoned rides R3/R4).
  Combinatorial interactions (card x relic x enemy) remain sampled, not
  enumerated — parity, V1, and real runs are that layer.

  e2e.py --boot           boot a host on STS2_AGENT_PORT (default 7779),
                          run all cases, tear the host down
  e2e.py                  run against an already-listening bridge
                          (boot-log and audit-trail cases are skipped)
  e2e.py --quick          skip the M sweeps, E1 clicks first options only
  e2e.py --only P1,M2     run a subset (case-name prefixes)
  e2e.py --keys-out F     write the parity key sets to F
  e2e.py --log F          host stderr file (with --boot)

Cases keep the world tidy: each one starts from the state it needs and
a failure falls back to the main menu before the next case runs.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.request

PORT = int(os.environ.get("STS2_AGENT_PORT", "7779"))
os.environ["STS2_AGENT_PORT"] = str(PORT)  # the CLI reads it too
BASE = f"http://127.0.0.1:{PORT}"
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HOST_DLL = os.path.join(REPO, "headless", "Host", "bin", "Release", "spirescry_host.dll")

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bridge  # noqa: E402

run, obs = bridge.run, bridge.obs

CASES = []
LOG_PATH = None  # set in main() when --boot
ROSTER = []  # collected by P5, consumed by R2
# The pinned parity seed (see F1): full path coverage — shop with
# potions (one opens a mid-combat picker in the boss fight), treasure,
# smith. SPIRECI2/SPIRECI3 also pass, with less potion coverage.
PARITY_SEED = "SPIRECI1"


def case(name, boot_only=False, deep=False):
    def deco(fn):
        CASES.append((name, boot_only, deep, fn))
        return fn
    return deco


# ---------- plumbing ----------

def http(method, path, body=None):
    """Raw bridge request, for cases the CLI can't express (bad routes,
    unknown actions). Returns (status, parsed-json)."""
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(BASE + path, method=method, data=data)
    try:
        with urllib.request.urlopen(req, timeout=70) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"{}")


def reject(args, code):
    """Expect the CLI call to fail with this error code; return stderr."""
    r = bridge.cli(*args)
    assert r.returncode != 0, \
        f"expected {code}, got success: {r.stdout.strip()[:120]}"
    err = r.stderr.strip()
    assert f"spirescry: {code}:" in err, f"expected {code}, got: {err}"
    return err


def host_log():
    assert LOG_PATH, "boot-only case ran without --boot"
    with open(LOG_PATH, encoding="utf-8", errors="replace") as f:
        return f.read()


def to_menu():
    bridge.run("abandon", ok=True)
    bridge.wait_phase("main_menu", timeout=15)


def launch(character="IRONCLAD", seed=None):
    to_menu()
    args = ["new-run", character] + (["--seed", seed] if seed else [])
    for attempt in range(3):
        bridge.run(*args, ok=attempt > 0)
        if bridge.wait_phase("event", timeout=30, raise_on_timeout=False):
            return
    raise AssertionError(f"new-run {character} never reached the Neow event")


def to_map(seed=None, character="IRONCLAD"):
    launch(character=character, seed=seed)
    run("proceed")
    return bridge.wait_phase("map")


def into_combat(seed=None, character="IRONCLAD"):
    d = to_map(seed=seed, character=character)
    node = next(p for p in d["next"] if p["type"] == "monster")
    run("map-move", str(node["col"]), str(node["row"]))
    d = bridge.wait_phase("combat")
    for _ in range(10):  # intents/side settle over a tick or two
        if d.get("side") == "player":
            return d
        time.sleep(1)
        d = obs()
    return d


def alive_enemy(d):
    return next(e for e in d["enemies"] if e["alive"])


# ---------- B: boot ----------

@case("B1 health shape")
def b1():
    status, d = http("GET", "/health")
    assert status == 200 and d["ok"] is True, d
    for k in ("mod", "version", "phase", "rev", "executorStuckMs", "queues"):
        assert k in d, f"health missing {k}: {sorted(d)}"


@case("B2 boot log: patches landed, models clean", boot_only=True)
def b2():
    text = host_log()
    assert "bridge listening" in text
    # fix #64 — the ReattachPower death-fade skip must find its target
    assert "skipping ReattachPower death fade" in text, \
        "ReattachPower patch missed (game update renamed the method?)"
    m = re.search(r"ModelDb: (\d+) registered, (\d+) failed", text)
    assert m, "no ModelDb summary in the boot log"
    assert m.group(2) == "0", f"ModelDb registration failures: {m.group(2)}"


@case("B3 host log lines carry timestamps", boot_only=True)
def b3():
    stamped = re.compile(r"^\[\d{2}:\d{2}:\d{2}\.\d{3}\] ")
    lines = [l for l in host_log().splitlines() if "[spirescry_host]" in l]
    assert lines, "no host log lines yet"
    bad = [l for l in lines if not stamped.match(l)]
    assert not bad, f"unstamped host lines: {bad[:3]}"


# ---------- P: protocol ----------

@case("P1 rev is monotonic")
def p1():
    revs = [obs()["rev"] for _ in range(5)]
    assert revs == sorted(revs), f"rev went backwards: {revs}"


@case("P2 long-poll parks quietly on the menu")
def p2():
    to_menu()
    for _ in range(2):  # one retry in case a background bump beat the timer
        cur = obs()["rev"]
        t0 = time.monotonic()
        d = run("obs", "--since", str(cur), "--wait", "1500")
        took = time.monotonic() - t0
        if not d.get("changed"):
            break
    assert d.get("changed") is False, f"menu kept bumping: {d.get('events')}"
    assert not d.get("events"), d.get("events")
    assert took >= 1.2, f"long poll returned early ({took:.2f}s)"


@case("P3 stale since returns immediately with events")
def p3():
    launch(seed="CIP3")  # guarantees revs behind us
    t0 = time.monotonic()
    d = run("obs", "--since", "0", "--wait", "4000")
    took = time.monotonic() - t0
    assert d.get("changed") is True
    assert d.get("events"), "changed:true with no events"
    assert took < 2, f"stale since still parked ({took:.2f}s)"
    to_menu()


@case("P4 unknown routes 404")
def p4():
    for method, path in (("GET", "/nope"), ("POST", "/obs"), ("GET", "/step")):
        status, d = http(method, path, {} if method == "POST" else None)
        assert status == 404 and d.get("err") == "not_found", \
            f"{method} {path} -> {status} {d}"


@case("P5 bad character is rejected with the roster")
def p5():
    to_menu()
    err = reject(["new-run", "NOT_A_CHARACTER"], "bad_request")
    names = [n for n in re.findall(r"[A-Z][A-Z_]{3,}", err)
             if n != "NOT_A_CHARACTER"]
    assert "IRONCLAD" in names, f"roster not in the rejection: {err}"
    ROSTER[:] = list(dict.fromkeys(names))
    print(f"    roster: {ROSTER}")


@case("P6 unknown cheat lists the surface")
def p6():
    launch(seed="CIP6")
    err = reject(["cheat", "bogus"], "bad_request")
    for tok in ("wound-enemies", "card-upgraded", "relic"):
        assert tok in err, f"'{tok}' missing from: {err}"
    to_menu()


@case("P7 unknown /step action is rejected")
def p7():
    status, d = http("POST", "/step", {"action": "warp", "args": {}})
    assert status >= 400 and d.get("ok") is False, f"{status} {d}"


# ---------- R: run lifecycle ----------

@case("R1 same seed, same world")
def r1():
    def fingerprint():
        launch(seed="CIDETERM")
        neow = [o.get("title") for o in obs().get("options", [])]
        run("proceed")
        d = bridge.wait_phase("map")
        return (d.get("seed"), neow,
                json.dumps(d.get("graph"), sort_keys=True))
    a, b = fingerprint(), fingerprint()
    to_menu()
    assert a[0] == b[0], f"seed drifted: {a[0]} vs {b[0]}"
    assert a[1] == b[1], f"Neow options drifted: {a[1]} vs {b[1]}"
    assert a[2] == b[2], "act-1 graph drifted between identical seeds"


@case("R2 every character boots and fights")
def r2():
    assert ROSTER, "P5 did not collect a roster"
    for c in ROSTER:
        launch(character=c, seed="CICHAR")
        p = obs().get("player") or {}
        missing = [k for k in ("hp", "gold", "deck", "relics") if k not in p]
        assert not missing, f"{c}: player footer missing {missing}"
        run("proceed")
        d = bridge.wait_phase("map")
        node = next(x for x in d["next"] if x["type"] == "monster")
        run("map-move", str(node["col"]), str(node["row"]))
        d = bridge.wait_phase("combat")
        for _ in range(10):
            if d.get("side") == "player":
                break
            time.sleep(1)
            d = obs()
        you = d["you"]
        assert isinstance(you.get("hp"), list) and isinstance(you.get("energy"), list), you
        assert "stars" in you, f"{c}: combat snapshot lost the stars field"
        assert d["hand"], f"{c}: empty opening hand"
        for card in d["hand"]:
            assert card.get("model") and "cost" in card and "vars" in card, card
        en = d["enemies"][0]
        assert en.get("title") and "intents" in en, en
        print(f"    {c}: hp={you['hp']} energy={you['energy']} "
              f"stars={you['stars']} hand={len(d['hand'])}")
        to_menu()  # a mid-combat abandon per character (regression: #66)


@case("R3 abandon mid-run returns to the menu")
def r3():
    to_map(seed="CIR3")
    run("abandon")
    bridge.wait_phase("main_menu")


@case("R4 mid-combat abandon doesn't poison the next combat")
def r4():
    # Regression: CombatManager is a static singleton — abandoning
    # mid-fight used to leave a _pendingLoss on it that instantly ended
    # the NEXT run's first combat (phase parked at unknown, transition
    # queue left paused). The abandon path now routes through the
    # engine's own CombatManager.Reset.
    into_combat(seed="CIR4A")
    to_menu()
    d = into_combat(seed="CIR4B")
    assert d.get("phase") == "combat", f"combat did not load: {d.get('phase')}"
    to_menu()


# ---------- C: combat ----------

@case("C1 combat economy: block, energy, bad_target, overdraw")
def c1():
    d = into_combat(seed="CICOMBAT")
    assert d.get("side") == "player", f"never got the player turn: {d.get('side')}"
    e0 = d["you"]["energy"][0]

    defend = next(c for c in d["hand"] if c["model"].startswith("DEFEND"))
    run("play", defend["model"])
    time.sleep(1)
    d = obs()
    assert d["you"]["block"] > 0, "Defend raised no block"
    assert d["you"]["energy"][0] == e0 - defend["cost"], \
        f"energy {e0} - {defend['cost']} != {d['you']['energy'][0]}"

    atk = next(c for c in d["hand"] if c["target"] == "anyenemy")
    reject(["play", atk["model"], "--target", "99"], "bad_target")

    # Drain energy with the cheapest legal plays; the first over-cost
    # attempt must come back as not_enough_energy, not something vaguer.
    for _ in range(8):
        d = obs()
        if d["phase"] != "combat":
            break
        energy = d["you"]["energy"][0]
        hand = [c for c in d["hand"] if not c.get("unplayable")]
        over = [c for c in hand if c["cost"] > energy]
        playable = [c for c in hand if c["cost"] <= energy]
        if over:
            c = over[0]
            args = ["play", c["model"]]
            if c["target"] == "anyenemy":
                args += ["--target", str(alive_enemy(d)["id"])]
            reject(args, "not_enough_energy")
            break
        assert playable, "hand emptied before any card went over cost"
        c = min(playable, key=lambda c: c["cost"])
        args = ["play", c["model"]]
        if c["target"] == "anyenemy":
            args += ["--target", str(alive_enemy(d)["id"])]
        run(*args, ok=True)
        time.sleep(0.8)
    else:
        raise AssertionError("never ran out of energy in 8 plays")

    # Leave through the real death pipeline (R4 covers the mid-combat
    # abandon path directly).
    import parity
    parity.kill_current_combat()
    bridge.wait_phase("rewards")
    run("proceed")
    bridge.wait_phase("map")
    to_menu()


@case("C2 orb economy is visible through /obs")
def c2():
    d = into_combat(seed="CIORBS", character="DEFECT")
    orbs = d["you"]["orbs"]
    assert orbs["slots"] > 0, f"Defect has no orb capacity: {orbs}"
    assert orbs["channeled"], f"Defect opened with no channeled orb: {orbs}"
    first = orbs["channeled"][0]
    assert set(("id", "passive", "evoke")) <= first.keys(), first
    assert first["id"].endswith("_ORB"), first
    to_menu()

    d = into_combat(seed="CIORBSNO", character="IRONCLAD")
    assert d["you"]["orbs"] is None, d["you"]["orbs"]
    to_menu()


# ---------- S: shop ----------

@case("S1 shop: every buy kind, gold accounting, leave")
def s1():
    d = to_map(seed="CISHOP")
    shop = next((p for p in d["graph"] if p["type"] == "shop"), None)
    assert shop, "seed CISHOP grew no shop — re-pin the seed"
    run("cheat", "gold", "5000")
    run("cheat", "goto", str(shop["col"]), str(shop["row"]))
    d = bridge.wait_phase("shop")

    def buy(kind, stock_key):
        before = obs()
        stock = before[stock_key]
        assert stock and stock[0]["stocked"], f"no {kind} in stock: {stock}"
        cost = stock[0]["cost"]
        run("buy", kind, "--idx", "0")
        time.sleep(0.8)
        after = obs()
        assert after["gold"] == before["gold"] - cost, \
            f"{kind}: gold {before['gold']} - {cost} != {after['gold']}"
        return after

    deck0 = len(obs()["player"]["deck"])
    buy("card", "cards")
    buy("colorless", "colorless")
    assert len(obs()["player"]["deck"]) == deck0 + 2, "bought cards missing from deck"

    relics0 = len(obs()["player"]["relics"])
    buy("relic", "relics")
    assert len(obs()["player"]["relics"]) == relics0 + 1, "bought relic missing"

    potions0 = len(obs()["player"]["potions"])
    buy("potion", "potions")
    assert len(obs()["player"]["potions"]) == potions0 + 1, "bought potion missing"

    d = obs()
    assert d["cardRemoval"] and not d["cardRemoval"]["used"], d.get("cardRemoval")
    run("buy", "card_removal", "--idx", "0")
    d = bridge.wait_phase("card_select")
    run("pick-card", "0")
    time.sleep(1)
    if obs()["phase"] == "card_select":
        run("confirm")
    bridge.wait_phase("shop")
    assert len(obs()["player"]["deck"]) == deck0 + 1, "removal did not shrink the deck"

    run("leave")
    bridge.wait_phase("map")
    to_menu()


@case("W1 skip: card reward and treasure walk away clean")
def w1():
    d = into_combat(seed="CISKIP")
    run("cheat", "wound-enemies", ok=True)
    atk = next(c for c in d["hand"] if c["target"] == "anyenemy")
    run("play", atk["model"], "--target", str(alive_enemy(d)["id"]))
    d = bridge.wait_phase("rewards")

    deck0 = len(obs()["player"]["deck"])
    card_tile = next(t for t in d["rewards"] if t["type"] == "card")
    run("pick-reward", str(card_tile["idx"]))
    d = bridge.wait_phase("card_reward")
    assert d.get("cards"), "card reward offered nothing"
    run("skip")
    time.sleep(1)
    assert len(obs()["player"]["deck"]) == deck0, "skip still added a card"
    bridge.wait_phase("rewards", "map")
    if obs()["phase"] == "rewards":
        run("proceed")
        bridge.wait_phase("map")

    tre = next((p for p in obs()["graph"] if p["type"] == "treasure"), None)
    assert tre, "seed CISKIP grew no treasure — re-pin the seed"
    relics0 = len(obs()["player"]["relics"])
    run("cheat", "goto", str(tre["col"]), str(tre["row"]))
    d = bridge.wait_phase("treasure")
    assert d.get("relics"), "treasure offered no relics"
    run("skip")
    time.sleep(1)
    assert len(obs()["player"]["relics"]) == relics0, "skip still granted a relic"
    to_menu()


# ---------- X: special screens ----------

@case("X1 crystal sphere: dig, tool verb, rewards out")
def x1():
    to_map(seed="CICRYS")
    run("cheat", "event", "CRYSTAL_SPHERE")
    d = bridge.wait_phase("event")
    run("option", "0")  # Uncover Future
    d = bridge.wait_phase("crystal_sphere")
    assert d["grid"]["width"] > 0 and d["divinationsLeft"] > 0, d["grid"]
    assert d["cells"], "no cells in the crystal snapshot"
    before = d.get("tool")
    assert before, "no tool in the crystal snapshot"
    run("option", "0" if before == "big" else "1")  # the OTHER tool
    time.sleep(0.5)
    d = obs()
    assert d["tool"] != before, f"tool verb changed nothing (still {d['tool']})"
    left = d["divinationsLeft"]
    for _ in range(left + 2):
        d = obs()
        if d["phase"] != "crystal_sphere" or d.get("finished"):
            break
        hidden = next((c for c in d["cells"] if c["hidden"]), None)
        assert hidden, "no hidden cells left but the minigame isn't finished"
        run("map-move", str(hidden["col"]), str(hidden["row"]))
        time.sleep(0.8)
    d = bridge.wait_phase("rewards", "map", "event", timeout=15)
    if d["phase"] == "rewards":
        run("proceed")
        bridge.wait_phase("map")
    to_menu()


@case("X2 bundle select: Neow's Scroll Boxes")
def x2():
    launch(seed="BX16")
    d = obs()
    pack = next((o for o in d.get("options", [])
                 if "pack" in (o.get("description") or "").lower()), None)
    assert pack, ("seed BX16 no longer offers Scroll Boxes — re-pin: "
                  f"{[o.get('title') for o in d.get('options', [])]}")
    deck0 = len(obs()["player"]["deck"])
    run("option", str(pack["idx"]))
    d = bridge.wait_phase("bundle_select")
    bundles = d.get("bundles")
    assert bundles and bundles[0]["cards"], f"empty bundle offer: {d}"
    picked = len(bundles[0]["cards"])
    run("pick-card", "0")
    time.sleep(1)
    d = obs()
    assert d["phase"] != "bundle_select", "pick-card did not resolve the bundle"
    assert len(obs()["player"]["deck"]) == deck0 + picked, \
        f"deck did not grow by the pack ({picked})"
    to_menu()


# ---------- K: cheats ----------

@case("K1 cheat surface grafts real state")
def k1():
    to_map(seed="CICHEAT")
    run("cheat", "gold", "123")
    run("cheat", "relic", "VAJRA")
    run("cheat", "card-upgraded", "STRIKE_IRONCLAD")
    run("cheat", "card", "BASH")
    p = run("obs", "--compact")["player"]
    assert p["gold"] == 123, f"gold cheat: {p['gold']}"
    assert "VAJRA" in p["relics"], f"relic cheat: {p['relics']}"
    assert p["deck"].get("STRIKE_IRONCLAD+", 0) >= 1, \
        f"card-upgraded cheat: {p['deck']}"
    assert p["deck"].get("BASH", 0) >= 2, f"card cheat: {p['deck']}"
    to_menu()


# ---------- V: victory ----------

@case("V1 cheat-driven full clear reaches a victory game_over")
def v1():
    import parity
    launch(seed="CIVICT")
    run("proceed")
    bridge.wait_phase("map")
    for _ in range(8):  # acts; the loop exits on game_over
        d = obs()
        if d["phase"] != "map":
            break
        boss = next(p for p in d["graph"] if p["type"] == "boss")
        run("cheat", "goto", str(boss["col"]), str(boss["row"]), ok=True)
        bridge.wait_phase("combat", timeout=30)
        parity.kill_current_combat()
        # walk whatever follows — reward tiles, transition events,
        # pickers — until the next act's map or the victory screen
        for _ in range(40):
            d = obs()
            ph = d["phase"]
            if ph in ("map", "game_over"):
                break
            if ph == "rewards":
                tiles = d.get("rewards", [])
                if tiles:
                    run("pick-reward", str(tiles[0]["idx"]), ok=True)
                else:
                    run("proceed", ok=True)
            elif ph == "relic_reward":
                run("pick-relic", "0", ok=True)
            elif ph == "card_reward":
                run("skip", ok=True)
            elif ph in ("card_select", "hand_select", "bundle_select"):
                run("pick-card", "0", ok=True)
                run("confirm", ok=True)
            elif ph == "event":
                opts = [x for x in d.get("options", [])
                        if not x.get("locked") and not x.get("chosen")]
                if opts:
                    run("option", str(opts[0]["idx"]), ok=True)
                else:
                    run("proceed", ok=True)
            elif ph == "combat":
                parity.kill_current_combat()
            else:
                run("proceed", ok=True)
            time.sleep(0.8)
        if obs()["phase"] == "game_over":
            break
    d = obs()
    assert d["phase"] == "game_over", f"never reached game_over: {d['phase']}"
    assert d.get("outcome") == "victory", f"outcome: {d.get('outcome')}"
    assert d.get("seed") == "CIVICT", d.get("seed")
    assert d.get("actNumber", 0) >= 3, \
        f"won suspiciously early: act {d.get('actNumber')}"
    print(f"    victory at act {d['actNumber']} floor {d['actFloor']}")
    to_menu()


# ---------- E: events ----------

@case("E1 every event responds (every option unless --quick)")
def e1():
    import eventsweep
    bad = eventsweep.sweep(all_options=not ARGS.quick)
    assert not bad, f"{len(bad)} events misbehaved: {bad}"


# ---------- M: exhaustive content sweeps ----------

@case("M1 bestiary: every encounter loads, fights, resolves", deep=True)
def m1():
    import sweeps
    bad = sweeps.encounters()
    assert not bad, f"{len(bad)} encounters misbehaved: {bad}"


@case("M2 every playable card executes; legality rejects cleanly", deep=True)
def m2():
    import sweeps
    bad = sweeps.cards()
    assert not bad, f"{len(bad)} cards misbehaved: {bad}"


@case("M3 every potion procures and drinks", deep=True)
def m3():
    import sweeps
    bad = sweeps.potions()
    assert not bad, f"{len(bad)} potions misbehaved: {bad}"


@case("M4 every legal relic obtain hook lands", deep=True)
def m4():
    import sweeps
    bad = sweeps.relics()
    assert not bad, f"{len(bad)} relics misbehaved: {bad}"


@case("M5 delayed card effects expose their picker")
def m5():
    # TOOLS_OF_THE_TRADE asks for a discard at the start of the NEXT
    # player turn, outside the play verb's HeadlessPicker.Around scope.
    # The pure host must still expose that automatic choice rather than
    # falling through to the absent GUI screen.
    d = into_combat(seed="CIM5", character="SILENT")
    assert d.get("side") == "player", f"never got player turn: {d.get('side')}"
    run("cheat", "card", "TOOLS_OF_THE_TRADE")
    run("cheat", "energy", "99")
    run("play", "TOOLS_OF_THE_TRADE")
    run("end-turn")
    pick = bridge.wait_phase("hand_select", timeout=25)
    assert pick.get("min") == 1 and pick.get("cards"), pick
    run("pick-card", str(pick["cards"][0]["idx"]))
    time.sleep(0.8)
    assert obs()["phase"] == "combat", obs()["phase"]
    to_menu()


# ---------- F: the full loop ----------

@case("F1 act-1 parity loop")
def f1():
    import parity
    parity.KEYS.clear()
    # Pinned seed: the pre-merge run wants the same map/shop/boss every
    # time. Re-pin via env after a game update reshuffles what the seed
    # generates.
    parity.drive(seed=os.environ.get("SPIRESCRY_PARITY_SEED", PARITY_SEED))
    if ARGS.keys_out:
        json.dump(parity.KEYS, open(ARGS.keys_out, "w"),
                  indent=1, sort_keys=True)
        print(f"    key sets -> {ARGS.keys_out}")


# ---------- H: audit trail ----------

@case("H1 request audit trail is on and well-formed", boot_only=True)
def h1():
    text = host_log()
    assert re.search(r"http POST /step new-run \S 200 rev \d+\S\d+ \d+ms", text), \
        "no new-run audit line (STS2_AGENT_HTTP_LOG not honored?)"
    assert re.search(r"http GET /obs\?since=\d+", text), \
        "no long-poll audit line"


# ---------- runner ----------

def boot_host(log_path):
    assert os.path.exists(HOST_DLL), \
        f"host not built ({HOST_DLL}) — run: ./build.sh headless-setup"
    env = dict(os.environ,
               STS2_AGENT_PORT=str(PORT),
               STS2_AGENT_HTTP_LOG="1")
    logf = open(log_path, "w")
    proc = subprocess.Popen(["dotnet", HOST_DLL], cwd=REPO,
                            stdout=logf, stderr=subprocess.STDOUT, env=env)
    for _ in range(60):
        if proc.poll() is not None:
            sys.exit(f"host died during boot — see {log_path}")
        try:
            status, d = http("GET", "/health")
            if status == 200 and d.get("ok"):
                print(f"host up on :{PORT} (pid {proc.pid})")
                return proc
        except OSError:
            pass
        time.sleep(1)
    proc.kill()
    sys.exit(f"bridge not up after 60s — see {log_path}")


def main():
    global LOG_PATH, ARGS
    ap = argparse.ArgumentParser()
    ap.add_argument("--boot", action="store_true")
    ap.add_argument("--quick", action="store_true",
                    help="skip the exhaustive sweeps (M*), first-option E1")
    ap.add_argument("--only", help="comma-separated case-name prefixes")
    ap.add_argument("--keys-out")
    ap.add_argument("--log", default=os.path.join(
        os.environ.get("TMPDIR", "/tmp"), "spirescry-ci-host.log"))
    ap.add_argument("--list", action="store_true")
    ARGS = ap.parse_args()

    if ARGS.list:
        for name, boot_only, deep, _ in CASES:
            print(name + ("  (--boot only)" if boot_only else "")
                  + ("  (skipped by --quick)" if deep else ""))
        return 0

    proc = None
    if ARGS.boot:
        LOG_PATH = ARGS.log
        proc = boot_host(LOG_PATH)

    only = [p.strip() for p in ARGS.only.split(",")] if ARGS.only else None
    failures = []
    try:
        for name, boot_only, deep, fn in CASES:
            if only and not any(name.startswith(p) for p in only):
                continue
            if boot_only and not ARGS.boot:
                print(f"SKIP {name} (needs --boot)")
                continue
            if deep and ARGS.quick:
                print(f"SKIP {name} (--quick)")
                continue
            print(f"== {name}")
            t0 = time.monotonic()
            try:
                fn()
                print(f"PASS {name} ({time.monotonic() - t0:.1f}s)")
            except (AssertionError, SystemExit, Exception) as e:  # noqa: B902
                failures.append(name)
                print(f"FAIL {name}: {e}")
                traceback.print_exc(limit=3)
                try:  # the snapshot usually names the stuck screen
                    print("    world at failure:",
                          json.dumps(run("obs", "--compact"), sort_keys=True)[:600])
                except Exception:
                    pass
                try:
                    to_menu()  # leave a sane world for the next case
                except Exception:
                    pass
    finally:
        if proc:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()

    ran = sum(1 for name, b, deep, _ in CASES
              if (not only or any(name.startswith(p) for p in only))
              and (ARGS.boot or not b) and not (deep and ARGS.quick))
    print(f"\n{ran - len(failures)}/{ran} cases passed"
          + (f"; FAILED: {failures}" if failures else ""))
    if failures and LOG_PATH:
        print(f"host log: {LOG_PATH}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
