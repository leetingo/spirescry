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


def cli(*args):
    """Run one spirescry command. not_ready is the bridge's transient
    signal (queue paused, map intro window, ...) — by contract the agent
    retries it (the CLI exits 75, EX_TEMPFAIL, for exactly that)."""
    for _ in range(15):
        r = subprocess.run([BIN, *args], capture_output=True, text=True)
        if r.returncode != 75:
            break
        time.sleep(0.4)
    return r


def run(*args, ok=False):
    """One CLI call that must succeed. ok=True tolerates failure and
    returns {"_err": stderr}; otherwise a failure ends the harness."""
    r = cli(*args)
    if r.returncode != 0:
        if ok:
            return {"_err": r.stderr.strip()}
        sys.exit(f"FAIL: spirescry {' '.join(args)} -> {r.stderr.strip()}")
    return json.loads(r.stdout) if r.stdout.strip() else {}


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
