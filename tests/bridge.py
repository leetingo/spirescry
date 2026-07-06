"""Shared bridge client for the test harnesses.

One place owns the CLI subprocess call, the not_ready retry contract,
and phase waiting — wait_phase parks on the bridge's own long-poll
(obs --since --wait) instead of sleep-polling.
"""
import json
import subprocess
import sys
import time


def cli(*args):
    """Run one spirescry command. not_ready is the bridge's transient
    signal (queue paused, map intro window, ...) — by contract the agent
    retries it (the CLI exits 75 for it)."""
    for _ in range(15):
        r = subprocess.run(["spirescry", *args], capture_output=True, text=True)
        if r.returncode == 0 or (r.returncode != 75 and "not_ready" not in r.stderr):
            break
        time.sleep(0.4)
    return r


def obs(since=None, wait_ms=2000):
    args = ["obs"] if since is None else ["obs", "--since", str(since), "--wait", str(wait_ms)]
    r = cli(*args)
    if r.returncode != 0:
        sys.exit(f"FAIL: spirescry {' '.join(args)} -> {r.stderr.strip()}")
    return json.loads(r.stdout)


def wait_phase(*want, timeout=20, raise_on_timeout=True, on_obs=None):
    """Return the first snapshot whose phase is in `want`, riding the
    revision long-poll between reads; None on timeout when not raising."""
    deadline = time.monotonic() + timeout
    since = None
    while True:
        d = obs(since)
        if on_obs:
            on_obs(d)
        if d.get("phase") in want:
            return d
        if time.monotonic() >= deadline:
            if raise_on_timeout:
                raise AssertionError(f"phase stuck at {d.get('phase')}, wanted {want}")
            return None
        since = d.get("rev")
