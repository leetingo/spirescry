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
WORLD_CLAIMS = {
    "claim_reward_tiles": False,
    "claim_card_reward": False,
    "claim_relic_reward": False,
}

def fresh_run(seed=None):
    run("abandon", allow_fail=True)
    time.sleep(1)
    bridge.launch_run(
        seed=seed, timeout=30, allow_first_failure=True)
    run("proceed", allow_fail=True)
    if bridge.wait_phase(
            bridge.PHASE.MAP, timeout=20, raise_on_timeout=False) is None:
        sys.exit("could not reach map for a fresh run")
    run("cheat", "gold", "500", allow_fail=True)

def walk_or_stuck(*wanted, **walk_options):
    """Keep the sweep aggregating after one option wedges or times out."""
    try:
        return bridge.walk_world(
            *wanted, **WORLD_CLAIMS, **walk_options)
    except AssertionError as error:
        return {"phase": f"stuck@{obs().get('phase')}:{str(error)[:120]}"}


def force(ev):
    """Force one event from a settled map; return its snapshot or None."""
    # Do not let a combat/picker or a previously explored option leak hp,
    # deck, or one-shot state into the next forced event.
    if obs().get("phase") != bridge.PHASE.MAP:
        fresh_run()
    if "_err" in run("cheat", bridge.PHASE.EVENT, ev, allow_fail=True):
        return None
    d = obs()
    for _ in range(10):  # engine boots mount the room over a few frames
        if d.get("phase") != bridge.PHASE.MAP:
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
        if d.get("phase") != bridge.PHASE.EVENT:
            return None, f"path {path} left event at {d.get('phase')}"
        opts = d.get("options", [])
        if idx >= len(opts) or opts[idx].get("locked"):
            return None, f"path {path} option {idx} unavailable"
        before_rev = d.get("rev")
        result = run("option", str(idx), allow_fail=True)
        if "_err" in result:
            return None, f"path {path} option {idx} rejected: {result['_err'][:120]}"
        d = walk_or_stuck(
            bridge.PHASE.EVENT, bridge.PHASE.MAP, after_rev=before_rev)
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
        if page.get("phase") != bridge.PHASE.EVENT:
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
            # Followed so the response carries `errors`: bridge.run fails
            # the sweep on any engine fault an option swallows — every
            # option click doubles as an engine_error noise regression.
            before_rev = current.get("rev")
            result = run("option", str(idx), "--follow", "4000", allow_fail=True)
            if "_err" in result:
                return outcomes, clicked, locked, (
                    f"path {path} option {idx} rejected: {result['_err'][:120]}")
            clicked += 1
            after = walk_or_stuck(
                bridge.PHASE.EVENT, bridge.PHASE.MAP, after_rev=before_rev)
            if after.get("phase") == bridge.PHASE.EVENT:
                if page_signature(after) not in seen_pages:
                    queue.append(path + (idx,))
                landed = walk_or_stuck(bridge.PHASE.MAP)["phase"]
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
    err = run("cheat", bridge.PHASE.EVENT, "__LIST__", allow_fail=True).get("_err", "")
    ids = sorted(set(err.split("known: ", 1)[1].rstrip(")").split(","))) if "known: " in err else []
    if not ids:
        sys.exit(f"could not enumerate events: {err[:200]}")
    print(f"{len(ids)} events to sweep"
          + (" (every option, drained)" if all_options else ""))

    special_ok = {bridge.PHASE.BUNDLE_SELECT, bridge.PHASE.CRYSTAL_SPHERE, bridge.PHASE.CARD_SELECT, bridge.PHASE.SHOP, bridge.PHASE.COMBAT, bridge.PHASE.TREASURE, bridge.PHASE.REST_SITE}
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
        if ph != bridge.PHASE.EVENT:
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
            if d is None or d.get("phase") != bridge.PHASE.EVENT:
                outcomes.append(f"{idx}:REFORCE-FAIL")
                continue
            opts_now = d.get("options", [])
            if idx >= len(opts_now) or opts_now[idx].get("locked"):
                locked_skipped += 1
                continue
            before_rev = d.get("rev")
            run("option", str(idx), "--follow", "4000", allow_fail=True)
            options_clicked += 1
            landed = walk_or_stuck(
                bridge.PHASE.MAP, after_rev=before_rev)["phase"]
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
    run("abandon", allow_fail=True)
    return bad


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--all-options", action="store_true",
                    help="click every unlocked option, not just the first")
    sys.exit(1 if sweep(all_options=ap.parse_args().all_options) else 0)
