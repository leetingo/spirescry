"""Shared bridge client for the test harnesses.

One place owns the CLI subprocess call, the not_ready retry contract,
and phase waiting — wait_phase parks on the bridge's own long-poll
(obs --since --wait) instead of sleep-polling.
"""
import json
import os
import subprocess
import sys
import time

# CI points this at the freshly built binary; default is the deployed one.
BIN = os.environ.get("SPIRESCRY_BIN", "spirescry")


def cli(*args, timeout=30):
    """Run one spirescry command. not_ready is the bridge's transient
    signal (queue paused, map intro window, ...) — by contract the agent
    retries it (the CLI exits 75, EX_TEMPFAIL, for exactly that)."""
    for _ in range(15):
        try:
            r = subprocess.run(
                [BIN, *args], capture_output=True, text=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            return subprocess.CompletedProcess(
                [BIN, *args], 124, "",
                f"command timed out after {timeout}s")
        if r.returncode != 75:
            break
        time.sleep(0.4)
    return r


def run(*args, allow_fail=False, allow_errors=False, timeout=30):
    """One CLI call that must succeed. allow_fail=True tolerates failure and
    returns {"_err": stderr}; otherwise a failure ends the harness.
    allow_errors=True is for deliberate fault injection: by default any
    followed response carrying engine errors fails the suite, so every
    ordinary action across every case doubles as a noise regression for
    the errors channel."""
    r = cli(*args, timeout=timeout)
    if r.returncode != 0:
        if allow_fail:
            return {"_err": r.stderr.strip()}
        sys.exit(f"FAIL: spirescry {' '.join(args)} -> {r.stderr.strip()}")
    data = json.loads(r.stdout) if r.stdout.strip() else {}
    if not allow_errors and isinstance(data, dict) and data.get("errors"):
        sys.exit(f"FAIL: spirescry {' '.join(args)} -> "
                 f"engine errors on a clean action: {data['errors']}")
    return data


def obs(since=None, wait_ms=2000):
    args = ["obs"] if since is None else ["obs", "--since", str(since), "--wait", str(wait_ms)]
    return run(*args)


def wait_phase(*want, timeout=20, raise_on_timeout=True, on_obs=None):
    """Return the first snapshot whose phase is in `want`, riding the
    revision long-poll between reads; None on timeout when not raising."""
    return wait_until(
        lambda d: d.get("phase") in want,
        timeout=timeout,
        raise_on_timeout=raise_on_timeout,
        description=f"phase in {want}",
        on_obs=on_obs,
    )


def wait_until(predicate, timeout=20, raise_on_timeout=True,
               description="condition", on_obs=None):
    """Wait until `predicate(snapshot)` is true using revision long-polls."""
    deadline = time.monotonic() + timeout
    since = None
    while True:
        d = obs(since)
        if on_obs:
            on_obs(d)
        if predicate(d):
            return d
        if time.monotonic() >= deadline:
            if raise_on_timeout:
                raise AssertionError(
                    f"timed out waiting for {description}; last phase={d.get('phase')}"
                )
            return None
        since = d.get("rev")


def launch_run(character="IRONCLAD", seed=None, *, timeout=30,
               allow_first_failure=False, on_obs=None, on_retry=None):
    """Launch a run, retrying the cold-boot window until Neow appears."""
    args = ["new-run", character] + (["--seed", seed] if seed else [])
    for attempt in range(3):
        run(*args, allow_fail=allow_first_failure or attempt > 0)
        started = wait_phase(
            "event", timeout=timeout, raise_on_timeout=False, on_obs=on_obs)
        if started is not None:
            return started
        if attempt < 2 and on_retry:
            on_retry()
    raise AssertionError(f"new-run {character} never reached the Neow event")


def kill_current_combat(*, on_obs=None):
    """Cheat-kill the current combat, resolving any picker it opens."""
    def observe():
        snapshot = obs()
        if on_obs:
            on_obs(snapshot)
        return snapshot

    used_potion = False
    for _ in range(30):
        d = observe()
        if d["phase"] in ("hand_select", "card_select"):
            run("pick-card", "0", allow_fail=True)
            time.sleep(1)
            if observe()["phase"] in ("hand_select", "card_select"):
                run("confirm", allow_fail=True)
                time.sleep(1)
            continue
        if d["phase"] != "combat":
            return
        if d.get("side") != "player":
            time.sleep(1.5)
            continue
        if not used_potion:
            used_potion = True
            pot = next(iter(d.get("potions", [])), None)
            if pot:
                alive_now = [e for e in d["enemies"] if e["alive"]]
                if pot["target"] == "anyenemy" and alive_now:
                    run("potion-use", str(pot["slot"]), "--target",
                        str(alive_now[0]["id"]), allow_fail=True)
                else:
                    run("potion-use", str(pot["slot"]), allow_fail=True)
        run("cheat", "heal", allow_fail=True)
        run("cheat", "wound-enemies", allow_fail=True)
        d = observe()
        if d["phase"] in ("hand_select", "card_select"):
            continue
        if d["phase"] != "combat":
            return
        alive = [e for e in d["enemies"] if e["alive"]]
        if not alive:
            run("end-turn", allow_fail=True)
            time.sleep(1.5)
            continue
        energy = d["you"]["energy"][0]
        attack = next((card for card in d["hand"]
                       if card["target"] == "anyenemy"
                       and card["cost"] <= energy), None)
        if attack:
            # play wants the exact selector: an upgraded copy is
            # "MODEL+", and the bare model no longer matches once an
            # accumulated relic upgrades the deck (the 84/85 bestiary
            # failure — thirty silent bad_index rejects in a row).
            run("play", attack.get("selector") or attack["model"],
                "--target", str(alive[0]["id"]), allow_fail=True)
            time.sleep(1)
        else:
            run("end-turn", allow_fail=True)
            time.sleep(2)
    raise AssertionError("combat did not finish in 30 turns")


def resolve_transient_phase(d, *, claim_reward_tiles=False,
                            claim_card_reward=False,
                            claim_relic_reward=False):
    """Advance one non-event transient phase; return an error if rejected."""
    phase = d.get("phase")
    if phase in ("card_select", "hand_select"):
        need = max(1, d.get("min", 1))
        for card in d.get("cards", [])[:need]:
            picked = run("pick-card", str(card["idx"]), allow_fail=True)
            if "_err" in picked:
                return f"pick:{picked['_err'][:60]}"
            if obs().get("phase") not in ("card_select", "hand_select"):
                break
        if obs().get("phase") in ("card_select", "hand_select"):
            confirmed = run("confirm", allow_fail=True)
            if "_err" in confirmed:
                return f"confirm:{confirmed['_err'][:60]}"
    elif phase == "bundle_select":
        run("pick-card", "0", allow_fail=True)
    elif phase == "combat":
        kill_current_combat()
    elif phase == "rewards" and claim_reward_tiles and d.get("rewards"):
        run("pick-reward", str(d["rewards"][0]["idx"]), allow_fail=True)
    elif phase == "card_reward" and claim_card_reward and d.get("cards"):
        run("pick-card", str(d["cards"][0]["idx"]), allow_fail=True)
    elif phase == "relic_reward" and claim_relic_reward and d.get("relics"):
        run("pick-relic", str(d["relics"][0]["idx"]), allow_fail=True)
    elif phase in ("card_reward", "relic_reward"):
        run("skip", allow_fail=True)
    elif phase == "shop":
        run("leave", allow_fail=True)
    else:
        run("proceed", allow_fail=True)
    return None
