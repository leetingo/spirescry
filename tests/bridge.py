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

import protocol

# CI points this at the freshly built binary; default is the deployed one.
BIN = os.environ.get("SPIRESCRY_BIN", "spirescry")
PROTOCOL = protocol.DOCUMENT
PHASE = protocol.PHASE
REJECTION = protocol.REJECTION
FAULT_EVENT = protocol.FAULT_EVENT


def cli(*args, timeout=30):
    """Run one spirescry command. not_ready is the bridge's transient
    signal (queue paused, map intro window, ...) — by contract the agent
    retries it (the CLI exits 75, EX_TEMPFAIL, for exactly that)."""
    deadline = time.monotonic() + timeout

    def timed_out():
        return subprocess.CompletedProcess(
            [BIN, *args], 124, "", f"command timed out after {timeout}s")

    for _ in range(15):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return timed_out()
        try:
            r = subprocess.run(
                [BIN, *args], capture_output=True, text=True,
                timeout=remaining)
        except subprocess.TimeoutExpired:
            return timed_out()
        if r.returncode != 75:
            break
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            return timed_out()
        time.sleep(min(0.4, remaining))
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


def wait_phase(*want, timeout=20, raise_on_timeout=True, on_obs=None,
               after_rev=None):
    """Return the first snapshot whose phase is in `want`, riding the
    revision long-poll between reads; None on timeout when not raising."""
    return wait_until(
        lambda d: d.get("phase") in want,
        timeout=timeout,
        raise_on_timeout=raise_on_timeout,
        description=f"phase in {want}",
        on_obs=on_obs,
        after_rev=after_rev,
    )


def wait_until(predicate, timeout=20, raise_on_timeout=True,
               description="condition", on_obs=None, after_rev=None):
    """Wait until `predicate(snapshot)` is true using revision long-polls.

    When `after_rev` is supplied, an unchanged long-poll response cannot
    satisfy the predicate. This is the shared action-follow seam: callers
    capture the snapshot revision before dispatch and park until the action
    produces a newer authoritative observation.
    """
    deadline = time.monotonic() + timeout
    since = after_rev
    while True:
        d = obs(since)
        if on_obs:
            on_obs(d)
        revision = d.get("rev")
        advanced = after_rev is None or (
            isinstance(revision, int) and revision > after_rev)
        if advanced and predicate(d):
            return d
        if time.monotonic() >= deadline:
            if raise_on_timeout:
                raise AssertionError(
                    f"timed out waiting for {description}; last phase={d.get('phase')}"
                )
            return None
        since = d.get("rev")


def wait_after(after_rev, timeout=20, description="action revision",
               on_obs=None):
    """Return the first snapshot newer than an action's input revision."""
    return wait_until(
        lambda _snapshot: True,
        timeout=timeout,
        description=description,
        on_obs=on_obs,
        after_rev=after_rev,
    )


def follow(*args, timeout_ms=10000, allow_errors=False):
    """Run one action through the bridge's settlement boundary."""
    result = run(
        *args, "--follow", str(timeout_ms), allow_fail=True,
        allow_errors=allow_errors,
        timeout=max(0.001, timeout_ms / 1000),
    )
    if "_err" in result:
        raise AssertionError(f"{' '.join(args)}: {result['_err']}")
    if result.get("settled") is not True:
        raise AssertionError(
            f"{' '.join(args)} did not settle: {result.get('outcome')}")
    snapshot = result.get("obs")
    if not isinstance(snapshot, dict):
        raise AssertionError(f"{' '.join(args)} follow response has no obs")
    return snapshot


def launch_run(character="IRONCLAD", seed=None, *, timeout=30, on_obs=None):
    """Launch one checked run and wait by revision until Neow appears."""
    args = ["new-run", character] + (["--seed", seed] if seed else [])
    before_rev = obs()["rev"]
    run(*args)
    return wait_phase(
        PHASE.EVENT, timeout=timeout, on_obs=on_obs, after_rev=before_rev)


def _remaining(deadline, description):
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise AssertionError(f"timed out waiting for {description}")
    return remaining


def _follow_before(deadline, *args):
    remaining = _remaining(deadline, f"{' '.join(args)} to settle")
    timeout_ms = max(1, min(10000, int(remaining * 1000)))
    return follow(*args, timeout_ms=timeout_ms)


def _act_and_settle(snapshot, *args, deadline, on_obs=None):
    if snapshot.get("rev") is None:
        raise AssertionError(
            f"cannot follow {' '.join(args)}: snapshot has no revision")
    settled = _follow_before(deadline, *args)
    if on_obs:
        on_obs(settled)
    return settled


def kill_current_combat(*, on_obs=None, timeout=60, initial=None):
    """Cheat-kill the current combat, resolving any picker it opens."""
    deadline = time.monotonic() + timeout
    d = initial if initial is not None else obs()
    if initial is None and on_obs:
        on_obs(d)
    used_potion = False
    for _ in range(90):
        if d["phase"] in (PHASE.HAND_SELECT, PHASE.CARD_SELECT):
            if d.get("confirmable"):
                d = _act_and_settle(
                    d, "confirm", deadline=deadline, on_obs=on_obs)
            elif d.get("cards"):
                d = _act_and_settle(
                    d, "pick-card", str(d["cards"][0]["idx"]),
                    deadline=deadline, on_obs=on_obs)
            else:
                raise AssertionError(
                    f"{d['phase']} has neither selectable cards nor confirm")
            continue
        if d["phase"] != PHASE.COMBAT:
            return d
        if d.get("side") != "player":
            d = wait_until(
                lambda snapshot: snapshot.get("phase") != PHASE.COMBAT
                or snapshot.get("side") == "player",
                timeout=_remaining(deadline, "player turn"),
                description="player turn",
                on_obs=on_obs,
                after_rev=d.get("rev"),
            )
            continue
        if not used_potion:
            used_potion = True
            pot = next(iter(d.get("potions", [])), None)
            if pot:
                alive_now = [e for e in d["enemies"] if e["alive"]]
                if pot["target"] == "anyenemy" and alive_now:
                    d = _act_and_settle(
                        d, "potion-use", str(pot["slot"]), "--target",
                        str(alive_now[0]["id"]), deadline=deadline,
                        on_obs=on_obs)
                else:
                    d = _act_and_settle(
                        d, "potion-use", str(pot["slot"]),
                        deadline=deadline, on_obs=on_obs)
                continue
        d = _act_and_settle(
            d, "cheat", "heal", deadline=deadline, on_obs=on_obs)
        if d.get("phase") != PHASE.COMBAT:
            return d
        d = _act_and_settle(
            d, "cheat", "wound-enemies", deadline=deadline, on_obs=on_obs)
        if d["phase"] in (PHASE.HAND_SELECT, PHASE.CARD_SELECT):
            continue
        if d["phase"] != PHASE.COMBAT:
            return d
        alive = [e for e in d["enemies"] if e["alive"]]
        if not alive:
            d = _act_and_settle(
                d, "end-turn", deadline=deadline, on_obs=on_obs)
            continue
        energy = d["you"]["energy"][0]
        attack = next((card for card in d["hand"]
                       if card["target"] == "anyenemy"
                       and card["cost"] <= energy), None)
        if attack:
            d = _act_and_settle(
                d, "play", attack["model"], "--target",
                str(alive[0]["id"]), deadline=deadline, on_obs=on_obs)
        else:
            d = _act_and_settle(
                d, "end-turn", deadline=deadline, on_obs=on_obs)
    raise AssertionError("combat did not finish in 90 revision-driven steps")


def resolve_transient_phase(d, *, claim_reward_tiles=False,
                            claim_card_reward=False,
                            claim_relic_reward=False, timeout=60,
                            on_obs=None):
    """Advance one transient phase and return its settled observation."""
    phase = d.get("phase")
    deadline = time.monotonic() + timeout
    if phase in (PHASE.CARD_SELECT, PHASE.HAND_SELECT):
        if d.get("confirmable"):
            return _follow_before(deadline, "confirm")
        else:
            card = next(
                (card for card in d.get("cards", [])
                 if not card.get("selected")), None)
            if card is None:
                raise AssertionError(
                    f"{phase} has neither selectable cards nor confirm")
            return _follow_before(deadline, "pick-card", str(card["idx"]))
    elif phase == PHASE.BUNDLE_SELECT:
        if d.get("confirmable"):
            return _follow_before(deadline, "confirm")
        else:
            return _follow_before(deadline, "pick-card", "0")
    elif phase == PHASE.COMBAT:
        return kill_current_combat(
            timeout=timeout, on_obs=on_obs, initial=d)
    elif phase == PHASE.REWARDS and claim_reward_tiles and d.get("rewards"):
        return _follow_before(
            deadline, "pick-reward", str(d["rewards"][0]["idx"]))
    elif phase == PHASE.CARD_REWARD and claim_card_reward and d.get("cards"):
        return _follow_before(
            deadline, "pick-card", str(d["cards"][0]["idx"]))
    elif phase == PHASE.RELIC_REWARD and claim_relic_reward and d.get("relics"):
        return _follow_before(
            deadline, "pick-relic", str(d["relics"][0]["idx"]))
    elif phase in (PHASE.CARD_REWARD, PHASE.RELIC_REWARD):
        return _follow_before(deadline, "skip")
    elif phase == PHASE.SHOP:
        return _follow_before(deadline, "leave")
    else:
        return _follow_before(deadline, "proceed")


_TRANSIENT_PHASES = {
    PHASE.BUNDLE_SELECT, PHASE.CARD_SELECT, PHASE.HAND_SELECT,
    PHASE.CRYSTAL_SPHERE, PHASE.REWARDS, PHASE.CARD_REWARD,
    PHASE.RELIC_REWARD,
}
_TERMINAL_PHASES = {PHASE.MAIN_MENU, PHASE.GAME_OVER}


def walk_world(*wanted_phases, claim_reward_tiles=False,
               claim_card_reward=False, claim_relic_reward=False,
               initial=None, timeout=60, on_obs=None):
    """Drive the world to a wanted phase, or drain one transient chain.

    With no wanted phase, stop at the first stable decision (combat, event,
    map, shop, ...). With wanted phases, resolve every intervening event,
    combat, picker, and room. Terminal phases always return to the caller so
    suites can decide whether to restart or assert an outcome.
    """
    wanted = set(wanted_phases)
    deadline = time.monotonic() + timeout
    # Callers that already crossed the action's settlement boundary hand
    # that observation straight to the walker. Waiting from the pre-action
    # revision here would let the earlier acceptance bump masquerade as the
    # asynchronous effect completing.
    settled_snapshot = initial
    settled_snapshot_observed = False
    for _ in range(120):
        # An action caller supplies its followed observation directly. This
        # API intentionally has no pre-action revision mode: any revision
        # wait can be satisfied by acceptance before the action settles.
        if settled_snapshot is not None:
            snapshot = settled_snapshot
            settled_snapshot = None
            already_observed = settled_snapshot_observed
            settled_snapshot_observed = False
        else:
            snapshot = obs()
            already_observed = False
        if on_obs and not already_observed:
            on_obs(snapshot)
        phase = snapshot.get("phase")
        if phase in wanted or phase in _TERMINAL_PHASES:
            return snapshot
        if not wanted and phase not in _TRANSIENT_PHASES:
            return snapshot

        if phase == PHASE.EVENT:
            options = [option for option in snapshot.get("options", [])
                       if not option.get("locked")
                       and not option.get("chosen")]
            if options:
                settled_snapshot = _follow_before(
                    deadline, "option", str(options[0]["idx"]))
            else:
                settled_snapshot = _follow_before(deadline, "proceed")
            settled_snapshot_observed = False
            continue
        else:
            settled_snapshot = resolve_transient_phase(
                snapshot,
                claim_reward_tiles=claim_reward_tiles,
                claim_card_reward=claim_card_reward,
                claim_relic_reward=claim_relic_reward,
                timeout=_remaining(deadline, "world to settle"),
                on_obs=on_obs,
            )
            if not isinstance(settled_snapshot, dict):
                raise AssertionError(
                    f"{phase} action returned no settled observation")
            settled_snapshot_observed = phase == PHASE.COMBAT and on_obs is not None
            continue

    last = obs()
    raise AssertionError(
        f"world exceeded 120 revision-driven steps toward "
        f"{wanted or 'a stable phase'}: "
        f"{last.get('phase')}")
