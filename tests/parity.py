#!/usr/bin/env python3
"""Dual-mode conformance test for the spirescry bridge.

Drives one act-1 loop (new-run, Neow, combat, rewards, rest-site smith,
shop removal, potions, treasure, abandon) against whichever bridge is up, polling
after every action — so the same script passes on the in-game boot
(actions resolve over frames) and the host boot (actions resolve inline).

While driving, it records the key set of every snapshot it sees, per
phase. `./build.sh verify` runs it once per boot and compares the two
recordings: same phase => same keys, so the modes can't silently drift.

  parity.py --keys-out FILE     drive the loop, write key sets to FILE
  parity.py --compare A B       diff two recordings (exit 1 on drift)
"""
import argparse
import json
import sys

import bridge

KEYS: dict[str, dict[str, list[str]]] = {}


def record(d):
    phase = d.get("phase")
    if not phase or d.get("available") is False:
        return
    slot = KEYS.setdefault(phase, {})
    # changed/events are the long-poll envelope (only present on
    # obs --since responses), not part of the phase snapshot shape.
    slot.setdefault("top", sorted(d.keys() - {"changed", "events"}))
    for k, v in d.items():
        if isinstance(v, list) and v and isinstance(v[0], dict):
            slot.setdefault(k + "[]", sorted(v[0].keys()))
            for child_k, child_v in v[0].items():
                if (isinstance(child_v, list) and child_v
                        and isinstance(child_v[0], dict)):
                    slot.setdefault(
                        f"{k}[].{child_k}[]", sorted(child_v[0].keys()))


run = bridge.run


def follow(*args, timeout_ms=10000):
    """Run one parity action to settlement and record its observation once."""
    settled = bridge.follow(*args, timeout_ms=timeout_ms)
    record(settled)
    return settled


def obs():
    d = bridge.obs()
    record(d)
    return d


def phase():
    return obs().get("phase")


def wait_phase(*want, timeout=20):
    return bridge.wait_phase(*want, timeout=timeout, on_obs=record)


def confirm_if_selecting(settled_pick):
    # GUI pickers wait for an explicit confirm; the host picker resolves
    # itself once max cards are picked. Both are legal under the protocol:
    # consume pick-card's settled observation and confirm only if it still
    # exposes the picker.
    if settled_pick.get("phase") == bridge.PHASE.CARD_SELECT:
        return follow("confirm")
    return settled_pick


def step(msg):
    print(f"== {msg}")


def exercise_bundle_pilot():
    """Record the Neow bundle decision in both boots before the act loop."""
    step("decision surface: Neow bundle pilot")
    d = obs()
    if d.get("phase") != bridge.PHASE.MAIN_MENU:
        run("abandon")
        bridge.wait_phase(
            bridge.PHASE.MAIN_MENU, on_obs=record,
            after_rev=d["rev"])
    bridge.launch_run(seed="BX16", timeout=40, on_obs=record)
    offer = obs()
    pack = next((option for option in offer.get("options", [])
                 if "pack" in (option.get("description") or "").lower()), None)
    assert pack, ("seed BX16 no longer offers Scroll Boxes — re-pin: "
                  f"{[option.get('title') for option in offer.get('options', [])]}")

    run("option", str(pack["idx"]))
    wait_phase(bridge.PHASE.BUNDLE_SELECT)
    bundle = obs()
    assert bundle.get("bundles") and bundle["bundles"][0].get("cards"), bundle
    d = follow("pick-card", "0")

    # GUI previews the selected bundle and completes on confirm; the
    # headless stand-in owns completion directly and leaves this phase on
    # pick. Both paths are the same decision-surface protocol.
    if d.get("phase") == bridge.PHASE.BUNDLE_SELECT:
        d = follow("confirm")
    assert d.get("phase") in (bridge.PHASE.EVENT, bridge.PHASE.MAP), d
    run("abandon")
    wait_phase(bridge.PHASE.MAIN_MENU)



def drive(seed=None):
    exercise_bundle_pilot()

    step("new-run")
    d = obs()
    if d.get("phase") != bridge.PHASE.MAIN_MENU:
        run("abandon")
        bridge.wait_phase(
            bridge.PHASE.MAIN_MENU, on_obs=record,
            after_rev=d["rev"])
    bridge.launch_run(seed=seed, timeout=40, on_obs=record)

    step("neow: proceed past")
    run("proceed")
    wait_phase(bridge.PHASE.MAP)

    step("map-move to first monster")
    node = next(p for p in obs()["next"] if p["type"] == "monster")
    run("map-move", str(node["col"]), str(node["row"]))
    wait_phase(bridge.PHASE.COMBAT)

    step("combat: intents")
    d = obs()
    if not d["enemies"][0]["intents"]:
        d = bridge.wait_until(
            lambda snapshot: bool(snapshot.get("enemies"))
            and bool(snapshot["enemies"][0].get("intents")),
            description="opening enemy intents",
            on_obs=record,
            after_rev=d["rev"],
        )
    en = d["enemies"][0]
    print("   ", en["model"], en["hp"], en["intents"])
    assert en["intents"], "no intents"
    assert en.get("title"), f"enemy {en['model']} has no readable title"

    step("combat: kill")
    bridge.kill_current_combat(on_obs=record)
    wait_phase(bridge.PHASE.REWARDS)

    step("rewards: claim all (re-read between picks; GUI reflows idx)")
    d = obs()
    for _ in range(8):
        if d["phase"] == bridge.PHASE.CARD_REWARD:
            print("    card offer:", [c["model"] for c in d["cards"]])
            d = follow("pick-card", "0")
            continue
        rewards = d.get(bridge.PHASE.REWARDS, [])
        if not rewards:
            break
        d = follow("pick-reward", str(rewards[0]["idx"]))
    if d.get("phase") != bridge.PHASE.REWARDS:
        d = wait_phase(bridge.PHASE.REWARDS)
    run("proceed")
    wait_phase(bridge.PHASE.MAP)

    step("rest site: smith upgrade")
    rest = next(p for p in obs()["graph"] if p["type"] == "restsite")
    run("cheat", "goto", str(rest["col"]), str(rest["row"]))
    wait_phase(bridge.PHASE.REST_SITE)
    opts = obs()["options"]
    smith = next(o for o in opts if o["id"] == "SMITH")
    run("option", str(smith["idx"]))
    wait_phase(bridge.PHASE.CARD_SELECT)
    d = obs()
    print(f"    picker: min={d['min']} max={d['max']} cards={len(d['cards'])}")
    preview = next((c for c in d["cards"] if c["upgradedPreview"] is not None), None)
    assert preview is not None, "smith picker has no upgradable-card preview"
    assert isinstance(preview["upgradedPreview"], str), \
        f"upgradedPreview wire type changed: {preview['upgradedPreview']!r}"
    assert "upgradedPlayCost" in preview and "upgradedStarCost" in preview, preview
    d = confirm_if_selecting(follow("pick-card", "1"))
    if d.get("phase") != bridge.PHASE.REST_SITE:
        d = wait_phase(bridge.PHASE.REST_SITE)
    run("proceed")
    wait_phase(bridge.PHASE.MAP)

    step("shop: card removal")
    shop = next((p for p in obs()["graph"] if p["type"] == bridge.PHASE.SHOP), None)
    if shop is None:
        print("    no shop on this map — skipped")
    else:
        follow("cheat", "gold", "1000")
        run("cheat", "goto", str(shop["col"]), str(shop["row"]))
        wait_phase(bridge.PHASE.SHOP)
        inventory = obs()
        for kind in ("cards", "colorless", "relics", "potions"):
            for entry in inventory[kind]:
                assert entry["cost"] == entry["price"], \
                    f"{kind} legacy cost != gold price: {entry}"
        for entry in inventory["cards"] + inventory["colorless"]:
            assert "playCost" in entry and "starCost" in entry, entry
        removal = inventory.get("cardRemoval")
        if removal is not None:
            assert removal["cost"] == removal["price"], removal
        run("buy", "card_removal", "--idx", "0")
        wait_phase(bridge.PHASE.CARD_SELECT)
        d = confirm_if_selecting(follow("pick-card", "0"))
        if d.get("phase") != bridge.PHASE.SHOP:
            d = wait_phase(bridge.PHASE.SHOP)
        gold = d["gold"]
        assert gold == 925, f"expected 925 gold after removal, got {gold}"

        step("shop: potions (potion-use/potion-discard checks)")
        d = obs()
        for_sale = d.get("potions", [])
        bought = 0
        for potion in for_sale[:2]:
            d = follow("buy", "potion", "--idx", str(potion["idx"]))
            bought += 1
        if bought == 0:
            print("    no potions in the shop this run — skipped")
        else:
            print(f"    bought {bought} potion(s)")
            # The shop's own stock is top-level `potions`; the player's
            # belt (with slot numbers) rides under `player`.
            slot = d["player"]["potions"][0]["slot"]
            d = follow("potion-discard", str(slot))
            assert not any(p["slot"] == slot for p in d["player"]["potions"]), \
                "potion-discard did not clear the slot"

        run("leave")
        wait_phase(bridge.PHASE.MAP)

    step(bridge.PHASE.TREASURE)
    tre = next((p for p in obs()["graph"] if p["type"] == bridge.PHASE.TREASURE), None)
    if tre is None:
        print("    no treasure on this map — skipped")
    else:
        run("cheat", "goto", str(tre["col"]), str(tre["row"]))
        wait_phase(bridge.PHASE.TREASURE)
        d = obs()
        if not d["chestOpened"]:
            d = follow("pick-relic", "0")
        print("    relics:", [r["model"] for r in d["relics"]])
        if d["relics"]:
            d = follow("pick-relic", "0")
        run("proceed")
        wait_phase(bridge.PHASE.MAP)

    step("boss → act transition")
    d = obs()
    boss = next(p for p in d["graph"] if p["type"] == "boss")
    run("cheat", "goto", str(boss["col"]), str(boss["row"]))
    wait_phase(bridge.PHASE.COMBAT, timeout=30)
    bridge.kill_current_combat(on_obs=record)
    d = wait_phase(bridge.PHASE.REWARDS)
    settled = bridge.follow("proceed", timeout_ms=30000)
    d = bridge.walk_world(
        bridge.PHASE.MAP, initial=settled, on_obs=record)
    assert d.get("act") == 1, "act transition did not reach act 1"
    print("    act 1 reached")

    step("defeat → game_over")
    run("abandon")
    wait_phase(bridge.PHASE.MAIN_MENU)
    bridge.launch_run(seed=seed, timeout=40, on_obs=record)
    run("proceed")
    wait_phase(bridge.PHASE.MAP)
    node = next(p for p in obs()["next"] if p["type"] == "monster")
    run("map-move", str(node["col"]), str(node["row"]))
    d = wait_phase(bridge.PHASE.COMBAT)
    for _ in range(20):
        if d["phase"] == bridge.PHASE.GAME_OVER:
            break
        if d["phase"] == bridge.PHASE.COMBAT and d.get("side") == "player":
            d = follow("cheat", "hp", "1")
            d = follow("end-turn", timeout_ms=30000)
        else:
            d = bridge.wait_until(
                lambda snapshot: snapshot.get("phase") == bridge.PHASE.GAME_OVER
                or (snapshot.get("phase") == bridge.PHASE.COMBAT
                    and snapshot.get("side") == "player"),
                description="defeat combat player turn",
                on_obs=record,
                after_rev=d["rev"],
            )
    wait_phase(bridge.PHASE.GAME_OVER, timeout=30)
    d = obs()
    assert d.get("outcome") == "defeat", f"expected defeat, got {d.get('outcome')}"
    # Death position in act-local terms: a fresh run that died on the
    # first monster node it walked to.
    assert d.get("actNumber") == d.get("act", -1) + 1, \
        f"actNumber {d.get('actNumber')} is not act {d.get('act')} + 1"
    assert d.get("actFloor") == node["row"] + 1, \
        f"actFloor {d.get('actFloor')} != row {node['row']} + 1"
    assert d.get("mapCoord") == {"col": node["col"], "row": node["row"]}, \
        f"mapCoord {d.get('mapCoord')} != {node}"
    enc = d.get("encounter") or {}
    assert enc.get("model"), "game_over is missing the encounter"
    print("    defeat recorded, floor", d.get("floor"),
          "encounter", enc.get("model"))

    step("abandon → main menu")
    run("abandon")
    wait_phase(bridge.PHASE.MAIN_MENU)
    print("PASS")


def compare(a_path, b_path):
    a = json.load(open(a_path))
    b = json.load(open(b_path))
    drift = []
    pilot_slots = {"top", "bundles[]", "bundles[].cards[]"}
    for path, recording in ((a_path, a), (b_path, b)):
        pilot = recording.get(bridge.PHASE.BUNDLE_SELECT)
        if pilot is None:
            drift.append(f"{path}: missing required bundle_select pilot phase")
            continue
        missing = pilot_slots - set(pilot)
        if missing:
            drift.append(
                f"{path}: bundle_select missing shape slots {sorted(missing)}")
    for ph in sorted(set(a) & set(b)):
        for slot in sorted(set(a[ph]) & set(b[ph])):
            if a[ph][slot] != b[ph][slot]:
                drift.append(f"{ph}.{slot}: {a_path}={a[ph][slot]} vs {b_path}={b[ph][slot]}")
    only_a, only_b = set(a) - set(b), set(b) - set(a)
    if only_a:
        print(f"(phases only in {a_path}: {sorted(only_a)} — not compared)")
    if only_b:
        print(f"(phases only in {b_path}: {sorted(only_b)} — not compared)")
    if drift:
        print("KEY-SET DRIFT between modes:")
        for line in drift:
            print("  " + line)
        sys.exit(1)
    print(f"parity ok: {len(set(a) & set(b))} phases compared, key sets identical")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--keys-out")
    ap.add_argument("--compare", nargs=2, metavar=("A", "B"))
    ap.add_argument("--seed", help="pin the run seed (CI wants determinism)")
    args = ap.parse_args()
    if args.compare:
        compare(*args.compare)
    else:
        drive(args.seed)
        if args.keys_out:
            json.dump(KEYS, open(args.keys_out, "w"), indent=1, sort_keys=True)
            print(f"key sets → {args.keys_out}")
