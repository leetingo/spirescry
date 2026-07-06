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
import subprocess
import sys
import time

KEYS: dict[str, dict[str, list[str]]] = {}


def record(d):
    phase = d.get("phase")
    if not phase or d.get("available") is False:
        return
    slot = KEYS.setdefault(phase, {})
    slot.setdefault("top", sorted(d.keys()))
    for k, v in d.items():
        if isinstance(v, list) and v and isinstance(v[0], dict):
            slot.setdefault(k + "[]", sorted(v[0].keys()))


def run(*args, ok_fail=False):
    # not_ready is the bridge's transient signal (queue paused, map intro
    # window, ...) — by contract the agent retries it.
    for _ in range(15):
        r = subprocess.run(["spirescry", *args], capture_output=True, text=True)
        if r.returncode == 0 or "not_ready" not in r.stderr:
            break
        time.sleep(0.4)
    if r.returncode != 0 and not ok_fail:
        sys.exit(f"FAIL: spirescry {' '.join(args)} -> {r.stderr.strip()}")
    return json.loads(r.stdout) if r.stdout.strip() else {}


def obs():
    d = run("obs")
    record(d)
    return d


def phase():
    return obs().get("phase")


def wait_phase(*want, timeout=20):
    for _ in range(timeout * 2):
        ph = phase()
        if ph in want:
            return ph
        time.sleep(0.5)
    raise AssertionError(f"phase stuck at {phase()}, wanted {want}")


def confirm_if_selecting():
    # GUI pickers wait for an explicit confirm; the host picker resolves
    # itself once max cards are picked. Both are legal under the protocol:
    # after pick-card, re-read and confirm only if still selecting.
    time.sleep(1)
    if phase() == "card_select":
        run("confirm")


def step(msg):
    print(f"== {msg}")



def kill_current_combat():
    """Cheat-kill whatever we're fighting; use a potion once if one's on hand."""
    used_potion = False
    for _ in range(30):
        d = obs()
        if d["phase"] != "combat":
            return
        if d.get("side") != "player":
            time.sleep(1.5)
            continue
        if not used_potion:
            used_potion = True
            pot = next(iter(d["you"].get("potions", [])), None)
            if pot:
                alive_now = [e for e in d["enemies"] if e["alive"]]
                if pot["target"] == "anyenemy" and alive_now:
                    run("potion-use", str(pot["slot"]), "--target",
                        str(alive_now[0]["id"]), ok_fail=True)
                else:
                    run("potion-use", str(pot["slot"]), ok_fail=True)
        run("cheat", "heal", ok_fail=True)
        run("cheat", "wound-enemies", ok_fail=True)
        d = obs()
        if d["phase"] != "combat":
            return
        alive = [e for e in d["enemies"] if e["alive"]]
        if not alive:
            # transforming bosses revive on their own turn
            run("end-turn", ok_fail=True)
            time.sleep(1.5)
            continue
        energy = d["you"]["energy"][0]
        atk = next((c for c in d["hand"]
                    if c["target"] == "anyenemy" and c["cost"] <= energy), None)
        if atk:
            run("play", atk["model"], "--target", str(alive[0]["id"]), ok_fail=True)
            time.sleep(1)
        else:
            run("end-turn", ok_fail=True)
            time.sleep(2)
    raise AssertionError("combat did not finish in 30 turns")


def drive():
    step("new-run")
    run("abandon", ok_fail=True)
    # The bridge comes up during mod init, before a cold engine boot's
    # menu is actually ready — a launch fired into that window can be
    # dropped silently. Settle, then retry the launch if it didn't land.
    time.sleep(5)
    for attempt in range(3):
        run("new-run", "IRONCLAD", ok_fail=attempt > 0)
        try:
            wait_phase("event", timeout=40)
            break
        except AssertionError:
            if attempt == 2:
                raise
            print("    launch didn't land, retrying")

    step("neow: proceed past")
    run("proceed")
    wait_phase("map")

    step("map-move to first monster")
    node = next(p for p in obs()["next"] if p["type"] == "monster")
    run("map-move", str(node["col"]), str(node["row"]))
    wait_phase("combat")

    step("combat: intents")
    en = obs()["enemies"][0]
    if not en["intents"]:
        time.sleep(2)
        en = obs()["enemies"][0]
    print("   ", en["model"], en["hp"], en["intents"])
    assert en["intents"], "no intents"

    step("combat: kill")
    kill_current_combat()
    wait_phase("rewards")

    step("rewards: claim all (re-read between picks; GUI reflows idx)")
    for _ in range(8):
        d = obs()
        if d["phase"] == "card_reward":
            print("    card offer:", [c["model"] for c in d["cards"]])
            run("pick-card", "0")
            time.sleep(1.5)
            continue
        rewards = d.get("rewards", [])
        if not rewards:
            break
        run("pick-reward", str(rewards[0]["idx"]))
        time.sleep(1.5)
    wait_phase("rewards")
    run("proceed")
    wait_phase("map")

    step("rest site: smith upgrade")
    rest = next(p for p in obs()["graph"] if p["type"] == "restsite")
    run("cheat", "goto", str(rest["col"]), str(rest["row"]))
    wait_phase("rest_site")
    opts = obs()["options"]
    smith = next(o for o in opts if o["id"] == "SMITH")
    run("option", str(smith["idx"]))
    wait_phase("card_select")
    d = obs()
    print(f"    picker: min={d['min']} max={d['max']} cards={len(d['cards'])}")
    run("pick-card", "1")
    confirm_if_selecting()
    wait_phase("rest_site")
    run("proceed")
    wait_phase("map")

    step("shop: card removal")
    shop = next((p for p in obs()["graph"] if p["type"] == "shop"), None)
    if shop is None:
        print("    no shop on this map — skipped")
    else:
        run("cheat", "gold", "1000")
        run("cheat", "goto", str(shop["col"]), str(shop["row"]))
        wait_phase("shop")
        run("buy", "card_removal", "--idx", "0")
        wait_phase("card_select")
        run("pick-card", "0")
        confirm_if_selecting()
        wait_phase("shop")
        gold = obs()["gold"]
        assert gold == 925, f"expected 925 gold after removal, got {gold}"

        step("shop: potions (potion-use/potion-discard checks)")
        for_sale = obs().get("potions", [])
        bought = sum(
            1 for idx in range(min(2, len(for_sale)))
            if run("buy", "potion", "--idx", str(idx), ok_fail=True)
        )
        if bought == 0:
            print("    no potions in the shop this run — skipped")
        else:
            print(f"    bought {bought} potion(s)")
            slot = obs()["potions"][0]["slot"]
            run("potion-discard", str(slot))
            assert not any(p["slot"] == slot for p in obs()["potions"]), \
                "potion-discard did not clear the slot"

        run("leave")
        wait_phase("map")

    step("treasure")
    tre = next((p for p in obs()["graph"] if p["type"] == "treasure"), None)
    if tre is None:
        print("    no treasure on this map — skipped")
    else:
        run("cheat", "goto", str(tre["col"]), str(tre["row"]))
        wait_phase("treasure")
        d = obs()
        print("    relics:", [r["model"] for r in d["relics"]])
        if d["relics"]:
            run("pick-relic", "0")
        run("proceed")
        wait_phase("map")

    step("boss → act transition")
    d = obs()
    boss = next(p for p in d["graph"] if p["type"] == "boss")
    run("cheat", "goto", str(boss["col"]), str(boss["row"]), ok_fail=True)
    wait_phase("combat", timeout=30)
    kill_current_combat()
    wait_phase("rewards")
    run("proceed")
    for _ in range(60):
        d = obs()
        if d["phase"] == "map" and d.get("act") == 1:
            break
        time.sleep(1)
    assert obs().get("act") == 1, "act transition did not reach act 1"
    print("    act 1 reached")

    step("defeat → game_over")
    run("abandon")
    wait_phase("main_menu")
    time.sleep(3)
    for attempt in range(3):
        run("new-run", "IRONCLAD", ok_fail=attempt > 0)
        try:
            wait_phase("event", timeout=40)
            break
        except AssertionError:
            if attempt == 2:
                raise
    run("proceed")
    wait_phase("map")
    node = next(p for p in obs()["next"] if p["type"] == "monster")
    run("map-move", str(node["col"]), str(node["row"]))
    wait_phase("combat")
    for _ in range(20):
        d = obs()
        if d["phase"] == "game_over":
            break
        if d["phase"] == "combat" and d.get("side") == "player":
            run("cheat", "hp", "1", ok_fail=True)
            run("end-turn", ok_fail=True)
        time.sleep(1.5)
    wait_phase("game_over", timeout=30)
    d = obs()
    assert d.get("outcome") == "defeat", f"expected defeat, got {d.get('outcome')}"
    print("    defeat recorded, floor", d.get("floor"))

    step("abandon → main menu")
    run("abandon")
    wait_phase("main_menu")
    print("PASS")


def compare(a_path, b_path):
    a = json.load(open(a_path))
    b = json.load(open(b_path))
    drift = []
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
    args = ap.parse_args()
    if args.compare:
        compare(*args.compare)
    else:
        drive()
        if args.keys_out:
            json.dump(KEYS, open(args.keys_out, "w"), indent=1, sort_keys=True)
            print(f"key sets → {args.keys_out}")
