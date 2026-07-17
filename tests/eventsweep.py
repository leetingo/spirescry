#!/usr/bin/env python3
"""Force every event via `cheat event` and check it renders + interacts.

Run against either boot (host is fastest). Not part of ./build.sh verify —
it takes minutes and mutates a throwaway run heavily; use it after engine
updates or event-related changes.
"""
import re, sys, time
from collections import Counter

import bridge

run = bridge.run

obs = lambda: run("obs")

def wait(*want, timeout=20):
    return bridge.wait_phase(*want, timeout=timeout, raise_on_timeout=False)

def fresh_run(seed=None):
    run("abandon", ok=True)
    time.sleep(1)
    for _ in range(3):
        args = ["new-run", "IRONCLAD"] + (["--seed", seed] if seed else [])
        run(*args, ok=True)
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

def drain(max_steps=30):
    """Generically resolve whatever an option opened, all the way out.
    Returns where it settled — map/main_menu/game_over — or 'stuck@…'."""
    import parity
    for _ in range(max_steps):
        d = obs()
        ph = d.get("phase")
        if ph in ("map", "main_menu", "game_over"):
            return ph
        if ph == "event":
            opts = [o for o in d.get("options", [])
                    if not o.get("locked") and not o.get("chosen")]
            if opts:
                run("option", str(opts[0]["idx"]), ok=True)
            else:
                run("proceed", ok=True)
        elif ph in ("card_select", "hand_select"):
            need = max(1, d.get("min", 1))
            for card in d.get("cards", [])[:need]:
                picked = run("pick-card", str(card["idx"]), ok=True)
                if "_err" in picked:
                    return f"stuck@{ph}:pick:{picked['_err'][:60]}"
                if obs().get("phase") not in ("card_select", "hand_select"):
                    break
            time.sleep(0.4)
            if obs().get("phase") in ("card_select", "hand_select"):
                confirmed = run("confirm", ok=True)
                if "_err" in confirmed:
                    return f"stuck@{ph}:confirm:{confirmed['_err'][:60]}"
        elif ph == "bundle_select":
            run("pick-card", "0", ok=True)
        elif ph == "combat":
            parity.kill_current_combat()
        elif ph in ("card_reward", "relic_reward"):
            run("skip", ok=True)
        elif ph == "shop":
            run("leave", ok=True)
        else:  # rewards, rest_site, treasure, crystal_sphere, …
            run("proceed", ok=True)
        time.sleep(0.4)
    return f"stuck@{obs().get('phase')}"


def settle_to_event_or_exit(max_steps=30):
    """Resolve transient screens without choosing another event option."""
    import parity
    for _ in range(max_steps):
        d = obs()
        ph = d.get("phase")
        if ph in ("event", "map", "main_menu", "game_over"):
            return d
        if ph in ("card_select", "hand_select"):
            need = max(1, d.get("min", 1))
            for card in d.get("cards", [])[:need]:
                run("pick-card", str(card["idx"]), ok=True)
                if obs().get("phase") not in ("card_select", "hand_select"):
                    break
            if obs().get("phase") in ("card_select", "hand_select"):
                run("confirm", ok=True)
        elif ph == "bundle_select":
            run("pick-card", "0", ok=True)
        elif ph == "combat":
            parity.kill_current_combat()
        elif ph in ("card_reward", "relic_reward"):
            run("skip", ok=True)
        elif ph == "shop":
            run("leave", ok=True)
        else:
            run("proceed", ok=True)
        time.sleep(0.4)
    return {"phase": f"stuck@{obs().get('phase')}"}


def force(ev):
    """Force one event from a settled map; return its snapshot or None."""
    ensure_map()
    if "_err" in run("cheat", "event", ev, ok=True):
        return None
    d = obs()
    for _ in range(10):  # engine boots mount the room over a few frames
        if d.get("phase") != "map":
            break
        time.sleep(0.5)
        d = obs()
    return d


def replay_event_path(ev, path):
    """Re-force an event and replay option indices to a specific page."""
    # Every path starts from the same hp/gold/deck/RNG baseline. Exploring
    # a sibling must not inherit damage, purchases, or one-shot state from
    # the option tested immediately before it.
    fresh_run("EVTREE")
    d = force(ev)
    if d is None:
        return None, "REFORCE-FAIL"
    for idx in path:
        if d.get("phase") != "event":
            return None, f"path {path} left event at {d.get('phase')}"
        opts = d.get("options", [])
        if idx >= len(opts) or opts[idx].get("locked"):
            return None, f"path {path} option {idx} unavailable"
        result = run("option", str(idx), ok=True)
        if "_err" in result:
            return None, f"path {path} option {idx} rejected: {result['_err'][:120]}"
        time.sleep(0.4)
        d = settle_to_event_or_exit()
    return d, None


def page_signature(d):
    # Re-forcing legitimately re-rolls numeric values. Treat those as the
    # same decision page or a repeatable event becomes an infinite tree.
    stable = lambda text: re.sub(r"\d+", "#", text or "")
    return (d.get("finished"), tuple(
        (stable(o.get("title")), stable(o.get("description")),
         o.get("locked"), o.get("proceed"))
        for o in d.get("options", [])))


def explore_all_event_options(ev):
    """Breadth-first exploration of every distinct event page and option."""
    queue = [()]
    seen_pages = set()
    outcomes = []
    clicked = locked = 0
    while queue:
        if len(seen_pages) >= 40:
            return outcomes, clicked, locked, "more than 40 distinct event pages"
        path = queue.pop(0)
        page, err = replay_event_path(ev, path)
        if err:
            return outcomes, clicked, locked, err
        if page.get("phase") != "event":
            return outcomes, clicked, locked, f"path {path} ended at {page.get('phase')}"
        signature = page_signature(page)
        if signature in seen_pages:
            continue
        seen_pages.add(signature)
        for idx, option in enumerate(page.get("options", [])):
            if option.get("locked"):
                locked += 1
                continue
            current, err = replay_event_path(ev, path)
            if err:
                return outcomes, clicked, locked, err
            result = run("option", str(idx), ok=True)
            if "_err" in result:
                return outcomes, clicked, locked, (
                    f"path {path} option {idx} rejected: {result['_err'][:120]}")
            time.sleep(0.4)
            clicked += 1
            after = settle_to_event_or_exit()
            if after.get("phase") == "event":
                if page_signature(after) not in seen_pages:
                    queue.append(path + (idx,))
                landed = drain()
            else:
                landed = after.get("phase")
            outcomes.append(f"{path + (idx,)}->{landed}")
    return outcomes, clicked, locked, None


def sweep(all_options=False):
    """Force every event and interact; with all_options, EVERY unlocked
    option on every reachable page is clicked on a fresh forcing and drained to
    completion. Returns the problem dict (empty == clean). Importable —
    tests/e2e.py runs this as its event-coverage case."""
    # event list from the cheat's known-list error (needs map phase)
    fresh_run()
    err = run("cheat", "event", "__LIST__", ok=True).get("_err", "")
    ids = sorted(set(err.split("known: ", 1)[1].rstrip(")").split(","))) if "known: " in err else []
    if not ids:
        sys.exit(f"could not enumerate events: {err[:200]}")
    print(f"{len(ids)} events to sweep"
          + (" (every option, drained)" if all_options else ""))

    special_ok = {"bundle_select", "crystal_sphere", "card_select", "shop", "combat", "treasure", "rest_site"}
    results = {}
    options_clicked = locked_skipped = 0
    for i, ev in enumerate(ids):
        d = force(ev)
        if d is None:
            results[ev] = "FORCE-FAIL"
            continue
        ph = d.get("phase")
        if ph in special_ok:
            results[ev] = f"special:{ph}"
            continue
        if ph != "event":
            results[ev] = f"UNEXPECTED phase={ph} overlay={d.get('overlay')}"
            continue
        opts = d.get("options", [])
        render = f"event opts={len(opts)}"
        if not opts and not d.get("finished"):
            results[ev] = render + " NO-OPTIONS"
            continue
        if all_options:
            print(f"  explore {ev}")
            outcomes, clicked, locked, error = explore_all_event_options(ev)
            options_clicked += clicked
            locked_skipped += locked
            if error:
                results[ev] = render + f" STUCK [{error}]"
            else:
                stuck = [o for o in outcomes if "stuck" in o or "FAIL" in o]
                results[ev] = render + (f" STUCK {stuck}" if stuck else " ok")
            if (i + 1) % 10 == 0:
                print(f"  ...{i + 1}/{len(ids)}")
                fresh_run()
            continue

        take = [0]
        outcomes = []
        for idx in take:
            d = obs() if idx == take[0] else force(ev)
            if d is None or d.get("phase") != "event":
                outcomes.append(f"{idx}:REFORCE-FAIL")
                continue
            opts_now = d.get("options", [])
            if idx >= len(opts_now) or opts_now[idx].get("locked"):
                locked_skipped += 1
                continue
            run("option", str(idx), ok=True)
            time.sleep(0.5)
            options_clicked += 1
            landed = drain()
            outcomes.append(f"{idx}->{landed}")
        stuck = [o for o in outcomes if "stuck" in o or "FAIL" in o]
        results[ev] = render + (f" STUCK {stuck}" if stuck else " ok")
        if (i + 1) % 10 == 0:
            print(f"  ...{i + 1}/{len(ids)}")
            fresh_run()

    print()
    bad = {k: v for k, v in results.items()
           if "FAIL" in v or "UNEXPECTED" in v or "NO-OPTIONS" in v or "STUCK" in v}
    tally = Counter("ok" if k not in bad else "bad" for k in results)
    print(f"== {tally['ok']} ok, {tally['bad']} problems of {len(results)}"
          + f"; {options_clicked} options clicked, {locked_skipped} locked")
    for k, v in sorted(bad.items()):
        print(f"  {k}: {v}")
    run("abandon", ok=True)
    return bad


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--all-options", action="store_true",
                    help="click every unlocked option, not just the first")
    sys.exit(1 if sweep(all_options=ap.parse_args().all_options) else 0)
