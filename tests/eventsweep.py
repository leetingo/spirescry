#!/usr/bin/env python3
"""Force every event via `cheat event` and check it renders + interacts.

Run against either boot (host is fastest). Not part of ./build.sh verify —
it takes minutes and mutates a throwaway run heavily; use it after engine
updates or event-related changes.
"""
import json, sys, time
from collections import Counter

import bridge

def run(*a, ok=False):
    r = bridge.cli(*a)
    if r.returncode != 0:
        if ok:
            return {"_err": r.stderr.strip()}
        sys.exit(f"FAIL: spirescry {' '.join(a)} -> {r.stderr.strip()}")
    return json.loads(r.stdout) if r.stdout.strip() else {}

obs = lambda: run("obs")

def wait(*want, timeout=20):
    return bridge.wait_phase(*want, timeout=timeout, raise_on_timeout=False)

def fresh_run():
    run("abandon", ok=True)
    time.sleep(1)
    for _ in range(3):
        run("new-run", "IRONCLAD", ok=True)
        if wait("event", timeout=30) is not None:
            break
    run("proceed", ok=True)
    if wait("map", timeout=20) is None:
        sys.exit("could not reach map for a fresh run")
    run("cheat", "gold", "500", ok=True)

def ensure_map():
    d = obs()
    if d["phase"] == "map":
        return
    # try the generic exits first, else nuke
    for _ in range(4):
        ph = obs()["phase"]
        if ph == "map":
            return
        if ph in ("event", "rest_site", "treasure", "rewards"):
            run("proceed", ok=True)
        elif ph == "shop":
            run("leave", ok=True)
        elif ph in ("card_select", "bundle_select"):
            run("pick-card", "0", ok=True)
            run("skip", ok=True)
        elif ph == "crystal_sphere":
            run("proceed", ok=True)
        elif ph in ("combat", "hand_select", "card_reward", "relic_reward"):
            break  # fresh run below
        else:
            break
        time.sleep(0.5)
    if obs()["phase"] != "map":
        fresh_run()

# event list from the cheat's known-list error (needs map phase)
fresh_run()
err = run("cheat", "event", "__LIST__", ok=True).get("_err", "")
ids = sorted(set(err.split("known: ", 1)[1].rstrip(")").split(","))) if "known: " in err else []
if not ids:
    sys.exit(f"could not enumerate events: {err[:200]}")
print(f"{len(ids)} events to sweep")

special_ok = {"bundle_select", "crystal_sphere", "card_select", "shop", "combat", "treasure", "rest_site"}
results = {}
for i, ev in enumerate(ids):
    ensure_map()
    r = run("cheat", "event", ev, ok=True)
    if "_err" in r:
        results[ev] = f"FORCE-FAIL: {r['_err'][:80]}"
        continue
    # engine boots mount the room over a few frames
    d = obs()
    for _ in range(10):
        if d.get("phase") != "map":
            break
        time.sleep(0.5)
        d = obs()
    ph = d.get("phase")
    if ph == "event":
        opts = d.get("options", [])
        title = d.get("title") or ""
        render = f"event opts={len(opts)}"
        if not opts and not d.get("finished"):
            render += " NO-OPTIONS"
        # interaction: take option 0 if any, then drain
        if opts:
            unlocked = [o for o in opts if not o.get("locked")]
            if unlocked:
                run("option", str(unlocked[0]["idx"]), ok=True)
                time.sleep(0.5)
                after = obs().get("phase")
                render += f" ->{after}"
                if after == "card_select":
                    run("pick-card", "0", ok=True)
                    run("confirm", ok=True)
                elif after == "bundle_select":
                    run("pick-card", "0", ok=True)
                elif after == "crystal_sphere":
                    run("proceed", ok=True)
        results[ev] = render
    elif ph in special_ok:
        results[ev] = f"special:{ph}"
    else:
        results[ev] = f"UNEXPECTED phase={ph} overlay={d.get('overlay')}"
    if (i + 1) % 20 == 0:
        print(f"  ...{i + 1}/{len(ids)}")
        fresh_run()

print()
bad = {k: v for k, v in results.items() if "FAIL" in v or "UNEXPECTED" in v or "NO-OPTIONS" in v}
tally = Counter("ok" if k not in bad else "bad" for k in results)
print(f"== {tally['ok']} ok, {tally['bad']} problems of {len(results)}")
for k, v in sorted(bad.items()):
    print(f"  {k}: {v}")
post = Counter(v.split("->")[1].split()[0] for v in results.values() if "->" in v)
print("option-0 landed in:", dict(post))
run("abandon", ok=True)
