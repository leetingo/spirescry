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
  R  run lifecycle: seeded determinism, every character boots to Neow
  C  combat economy: block, energy accounting, bad_target, overdraw
  K  cheat surface: gold / relic / card-upgraded / card graft real state
  F  the full act-1 parity loop (tests/parity.py), key sets recorded
  H  request audit trail (STS2_AGENT_HTTP_LOG line format)

  e2e.py --boot           boot a host on STS2_AGENT_PORT (default 7779),
                         run all cases, tear the host down
  e2e.py                  run against an already-listening bridge
                         (boot-log and audit-trail cases are skipped)
  e2e.py --sweep          also run eventsweep.py at the end (minutes)
  e2e.py --only P1,K1     run a subset (case-name prefixes)
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


def case(name, boot_only=False):
    def deco(fn):
        CASES.append((name, boot_only, fn))
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


def to_map(seed=None):
    launch(seed=seed)
    run("proceed")
    return bridge.wait_phase("map")


def into_combat(seed=None):
    d = to_map(seed=seed)
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


@case("R2 every character boots to Neow")
def r2():
    assert ROSTER, "P5 did not collect a roster"
    for c in ROSTER:
        launch(character=c)
        p = obs().get("player") or {}
        missing = [k for k in ("hp", "gold", "deck", "relics") if k not in p]
        assert not missing, f"{c}: player footer missing {missing}"
        print(f"    {c}: hp={p['hp']} deck={len(p['deck'])} cards")
    to_menu()


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
    run("play", defend["selector"])
    time.sleep(1)
    d = obs()
    assert d["you"]["block"] > 0, "Defend raised no block"
    assert d["you"]["energy"][0] == e0 - defend["cost"], \
        f"energy {e0} - {defend['cost']} != {d['you']['energy'][0]}"

    atk = next(c for c in d["hand"] if c["target"] == "anyenemy")
    reject(["play", atk["selector"], "--target", "99"], "bad_target")

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
            args = ["play", c["selector"]]
            if c["target"] == "anyenemy":
                args += ["--target", str(alive_enemy(d)["id"])]
            reject(args, "not_enough_energy")
            break
        assert playable, "hand emptied before any card went over cost"
        c = min(playable, key=lambda c: c["cost"])
        args = ["play", c["selector"]]
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


# ---------- A: action regressions ----------

@case("A1 treasure relic can be followed by skip")
def a1():
    d = to_map(seed=PARITY_SEED)
    treasure = next(p for p in d["graph"] if p["type"] == "treasure")
    run("cheat", "goto", str(treasure["col"]), str(treasure["row"]))
    bridge.wait_phase("treasure")

    d = bridge.wait_until(
        lambda snapshot: bool(snapshot.get("relics")),
        timeout=5,
        description="treasure relic offer",
    )

    run("pick-relic", str(d["relics"][0]["idx"]))
    d = obs()
    if d.get("phase") == "treasure":
        status, result = http("POST", "/step", {"action": "skip", "args": {}})
        assert status == 200 and result.get("ok") is True, \
            f"skip after pick-relic was permanently rejected: {status} {result}"
    bridge.wait_phase("map")
    to_menu()


@case("A2 combat reward slots are claimable exactly once")
def a2():
    into_combat(seed=PARITY_SEED)
    import parity
    parity.kill_current_combat()
    d = bridge.wait_phase("rewards")
    reward = next(r for r in d["rewards"] if r["type"] == "gold")
    gold_before = d["player"]["gold"]

    status, result = http("POST", "/step", {
        "action": "pick-reward", "args": {"idx": reward["idx"]}
    })
    assert status == 200 and result.get("ok") is True, result
    d = obs()
    assert d["player"]["gold"] == gold_before + reward["amount"], \
        f"gold reward applied incorrectly: {gold_before} -> {d['player']['gold']}"

    status, result = http("POST", "/step", {
        "action": "pick-reward", "args": {"idx": reward["idx"]}
    })
    assert status == 400 and result.get("err") == "bad_index", \
        f"consumed reward slot was claimable twice: {status} {result}"
    assert obs()["player"]["gold"] == gold_before + reward["amount"], \
        "repeat reward claim changed gold"
    to_menu()


@case("A3 upgraded same-model card can be played precisely")
def a3():
    into_combat(seed="CIUPGRADEDPLAY")
    run("cheat", "card", "STRIKE_IRONCLAD")
    run("cheat", "card-upgraded", "STRIKE_IRONCLAD")
    run("cheat", "card-upgraded", "STRIKE_IRONCLAD")
    d = obs()
    target = alive_enemy(d)["id"]

    def copies(snapshot, upgraded):
        return sum(c["model"] == "STRIKE_IRONCLAD"
                   and c["upgraded"] is upgraded for c in snapshot["hand"])

    base_before = copies(d, False)
    upgraded_before = copies(d, True)
    assert base_before > 0 and upgraded_before >= 2, d["hand"]
    assert any(c["selector"] == "STRIKE_IRONCLAD" for c in d["hand"]), d["hand"]
    assert any(c["selector"] == "STRIKE_IRONCLAD+" for c in d["hand"]), d["hand"]

    status, result = http("POST", "/step", {
        "action": "play",
        "args": {"model": "STRIKE_IRONCLAD+", "target": "not-an-id"},
    })
    assert status == 400 and result.get("err") == "bad_request", result
    assert copies(obs(), True) == upgraded_before, "malformed target played a card"

    status, result = http("POST", "/step", {
        "action": "play",
        "args": {"model": "STRIKE_IRONCLAD+", "target": target},
    })
    assert status == 200 and result.get("ok") is True, \
        f"could not select upgraded copy: {status} {result}"
    d = obs()
    assert copies(d, True) == upgraded_before - 1, "upgraded copy stayed in hand"
    assert copies(d, False) == base_before, "MODEL+ played an unupgraded copy"

    # Unsuffixed MODEL deterministically means an unupgraded, unmodified
    # copy; identical copies resolve by stable hand order.
    status, result = http("POST", "/step", {
        "action": "play",
        "args": {"model": "STRIKE_IRONCLAD", "target": target},
    })
    assert status == 200 and result.get("ok") is True, result
    d = obs()
    assert copies(d, False) == base_before - 1, "base MODEL did not play a base copy"
    assert copies(d, True) == upgraded_before - 1, "base MODEL played an upgraded copy"
    to_menu()


@case("A4 Foul Potion can be redeemed only at a merchant")
def a4():
    d = to_map(seed=PARITY_SEED)

    def cheat_potion(model):
        status, result = http("POST", "/step", {
            "action": "cheat", "args": {"name": "potion", "id": model},
        })
        assert status == 200 and result.get("ok") is True, \
            f"could not procure {model}: {status} {result}"

    cheat_potion("FOUL_POTION")
    foul = next(p for p in obs()["player"]["potions"]
                if p["model"] == "FOUL_POTION")

    shop = next(p for p in d["graph"] if p["type"] == "shop")
    run("cheat", "goto", str(shop["col"]), str(shop["row"]))
    d = bridge.wait_phase("shop")
    gold_before = d["gold"]
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": foul["slot"]},
    })
    assert status == 200 and result.get("ok") is True, \
        f"Foul Potion merchant redemption failed: {status} {result}"
    d = obs()
    gained = d["gold"] - gold_before
    assert gained > 0, f"Foul Potion awarded no gold: {gold_before} -> {d['gold']}"
    assert f"[blue]{gained}[/blue]" in foul["description"], \
        f"Foul Potion awarded {gained}, inconsistent with its description: {foul}"
    assert not any(p["slot"] == foul["slot"] for p in d["player"]["potions"]), \
        "redeemed Foul Potion stayed in its belt slot"

    cheat_potion("ENERGY_POTION")
    energy = next(p for p in obs()["player"]["potions"]
                  if p["model"] == "ENERGY_POTION")
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": energy["slot"]},
    })
    assert status == 400 and result.get("err") == "not_playable", \
        f"ordinary potion got the wrong merchant gate: {status} {result}"
    assert "merchant" in result.get("msg", "").lower(), result
    assert any(p["slot"] == energy["slot"] for p in obs()["player"]["potions"]), \
        "rejected ordinary potion left its belt slot"
    to_menu()


@case("A5 malformed numeric fields never default to zero")
def a5():
    to_menu()
    for value in ("10", 2**31, -1):
        status, result = http("POST", "/step", {
            "action": "new-run",
            "args": {"character": "IRONCLAD", "ascension": value},
        })
        assert status == 400 and result.get("err") == "bad_request", \
            f"invalid ascension {value!r} launched a run: {status} {result}"
        assert obs()["phase"] == "main_menu", "invalid ascension mutated the run"

    d = to_map(seed=PARITY_SEED)
    run("cheat", "gold", "1000")
    shop = next(p for p in d["graph"] if p["type"] == "shop")
    run("cheat", "goto", str(shop["col"]), str(shop["row"]))
    d = bridge.wait_phase("shop")
    card = next(c for c in d["cards"] if c["stocked"])
    gold_before = d["gold"]

    for value, code in (("0", "bad_request"), (2**31, "bad_request"), (-1, "bad_index")):
        status, result = http("POST", "/step", {
            "action": "buy", "args": {"kind": "card", "idx": value},
        })
        assert status == 400 and result.get("err") == code, \
            f"invalid buy idx {value!r} acted as slot zero: {status} {result}"
        d = obs()
        assert d["gold"] == gold_before, "invalid buy idx spent gold"
        current = next(c for c in d["cards"] if c["idx"] == card["idx"])
        assert current["stocked"], "invalid buy idx purchased a card"
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


# ---------- I: information snapshots ----------

@case("I3 power descriptions track their live amounts")
def i3():
    launch(character="REGENT", seed="CIPOWERDESC")
    run("proceed")
    d = bridge.wait_phase("map")
    node = next(p for p in d["next"] if p["type"] == "monster")
    run("map-move", str(node["col"]), str(node["row"]))
    bridge.wait_phase("combat")

    def power(model):
        return next(p for p in obs()["you"]["powers"] if p["id"] == model)

    def next_turn():
        before_turn = obs()["turn"]
        run("cheat", "heal", ok=True)
        run("end-turn")
        bridge.wait_until(
            lambda snapshot: snapshot.get("phase") == "combat"
            and snapshot.get("side") == "player"
            and snapshot.get("turn", before_turn) > before_turn,
            timeout=8,
            description="next player turn",
        )

    # Exact QA regressions: the static prose says 3/25, while upgraded
    # cards apply larger live values. The snapshot must render those values.
    run("cheat", "card-upgraded", "BLACK_HOLE")
    run("play", "BLACK_HOLE+")
    black_hole = power("BLACK_HOLE_POWER")
    assert black_hole["amount"] == 4, black_hole
    assert "[blue]4[/blue]" in black_hole["description"], black_hole

    next_turn()
    run("cheat", "card-upgraded", "ROYALTIES")
    run("play", "ROYALTIES+")
    royalties = power("ROYALTIES_POWER")
    assert royalties["amount"] > 25, royalties
    assert f"[blue]{royalties['amount']}[/blue]" in royalties["description"], royalties

    # A normal stacking power must update on the later turn too.
    next_turn()
    run("cheat", "card", "RUPTURE")
    run("play", "RUPTURE")
    first = power("RUPTURE_POWER")
    assert first["amount"] == 1 and "[blue]1[/blue]" in first["description"], first
    next_turn()
    run("cheat", "card", "RUPTURE")
    run("play", "RUPTURE")
    stacked = power("RUPTURE_POWER")
    assert stacked["amount"] == 2 and "[blue]2[/blue]" in stacked["description"], stacked
    to_menu()


@case("I4 compact crystal reveals decisions without dumping the board")
def i4():
    to_map(seed="CICRYSTALINFO")
    run("cheat", "event", "CRYSTAL_SPHERE")
    d = bridge.wait_phase("event")
    option = next(o for o in d["options"] if not o["locked"])
    run("option", str(option["idx"]))
    full = bridge.wait_phase("crystal_sphere")

    compact = run("obs", "--compact")
    width, height = full["grid"]["width"], full["grid"]["height"]
    total_cells = width * height
    assert len(compact["cells"]) < total_cells, \
        f"compact crystal dumped the raw board: {len(compact['cells'])} cells"
    assert compact["hiddenCells"] == sum(c["hidden"] for c in full["cells"]), compact

    # Exercise the real dig seam and spread the finite divinations around
    # the board until a reward/danger footprint is exposed.
    preferred = [(0, 0), (width - 1, height - 1), (0, height - 1),
                 (width - 1, 0), (width // 2, height // 2)]
    item = None
    while full["divinationsLeft"] > 0:
        hidden = {(c["col"], c["row"]): c for c in full["cells"] if c["hidden"]}
        coord = next((p for p in preferred if p in hidden), next(iter(hidden)))
        before_left = full["divinationsLeft"]
        before_hidden = sum(c["hidden"] for c in full["cells"])
        run("map-move", str(coord[0]), str(coord[1]))
        full = bridge.wait_until(
            lambda snapshot: snapshot.get("divinationsLeft", before_left) < before_left,
            timeout=5,
            description="crystal divination",
        )
        assert full["divinationsLeft"] == before_left - 1, full
        assert sum(c["hidden"] for c in full["cells"]) < before_hidden, full
        compact = run("obs", "--compact")
        assert len(compact["cells"]) < total_cells, compact
        item = next((c.get("item") for c in full["cells"] if c.get("item")), None)
        if item:
            break

    assert item and item.get("type"), f"no revealed cell exposed item identity: {full}"
    footprint = item.get("footprint")
    assert footprint and footprint["width"] > 0 and footprint["height"] > 0, item
    for key in ("col", "row"):
        assert key in footprint, item
    compact_item = next((c.get("item") for c in compact["cells"] if c.get("item")), None)
    assert compact_item == item, compact
    to_menu()


@case("I5 Spoils Map marks its next-act treasure node")
def i5():
    d = to_map(seed="CISPOILSMAP")
    run("cheat", "card", "SPOILS_MAP")
    d = obs()
    assert "marked" not in d, d
    assert all("markers" not in point for point in d["graph"]), d["graph"]
    assert "marked" not in run("obs", "--compact")

    import parity
    boss = next(p for p in d["graph"] if p["type"] == "boss")
    run("cheat", "goto", str(boss["col"]), str(boss["row"]), ok=True)
    bridge.wait_phase("combat", timeout=30)
    parity.kill_current_combat()
    bridge.wait_phase("rewards")
    run("proceed")
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") == "map" and snapshot.get("act") == 1,
        timeout=30,
        description="act 2 map",
    )
    assert d.get("act") == 1, d

    marked = d.get("marked")
    assert marked and len(marked) == 1, d
    treasure = marked[0]
    assert treasure["markers"] == ["SPOILS_MAP"], treasure
    in_graph = next(p for p in d["graph"]
                    if p["col"] == treasure["col"] and p["row"] == treasure["row"])
    assert in_graph["markers"] == ["SPOILS_MAP"], in_graph

    compact = run("obs", "--compact")
    assert compact.get("marked") == marked, compact
    assert compact["graph"] is None, compact
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
    ap.add_argument("--sweep", action="store_true")
    ap.add_argument("--only", help="comma-separated case-name prefixes")
    ap.add_argument("--keys-out")
    ap.add_argument("--log", default=os.path.join(
        os.environ.get("TMPDIR", "/tmp"), "spirescry-ci-host.log"))
    ap.add_argument("--list", action="store_true")
    ARGS = ap.parse_args()

    if ARGS.list:
        for name, boot_only, _ in CASES:
            print(name + ("  (--boot only)" if boot_only else ""))
        return 0

    proc = None
    if ARGS.boot:
        LOG_PATH = ARGS.log
        proc = boot_host(LOG_PATH)

    only = [p.strip() for p in ARGS.only.split(",")] if ARGS.only else None
    failures = []
    try:
        for name, boot_only, fn in CASES:
            if only and not any(name.startswith(p) for p in only):
                continue
            if boot_only and not ARGS.boot:
                print(f"SKIP {name} (needs --boot)")
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
        if ARGS.sweep and not failures:
            print("== eventsweep (full event pass)")
            r = subprocess.run(
                [sys.executable, os.path.join(REPO, "tests", "eventsweep.py")])
            if r.returncode != 0:
                failures.append("eventsweep")
    finally:
        if proc:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()

    ran = sum(1 for name, b, _ in CASES
              if (not only or any(name.startswith(p) for p in only))
              and (ARGS.boot or not b))
    print(f"\n{ran - len(failures)}/{ran} cases passed"
          + (f"; FAILED: {failures}" if failures else ""))
    if failures and LOG_PATH:
        print(f"host log: {LOG_PATH}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
