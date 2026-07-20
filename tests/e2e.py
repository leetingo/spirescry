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
  R  run lifecycle: seeded determinism, every character boots + fights,
     abandon from map and mid-combat (regression: #66)
  C  combat economy: block, energy accounting, bad_target, overdraw
  S  shop: every buy kind with gold accounting, removal picker, leave
  W  skip: card reward + treasure walk away without granting
  X  special screens: crystal-sphere minigame, Neow bundle select
  K  cheat surface: gold / relic / card-upgraded / card graft real state
  V  victory: cheat-driven full clear to a victory game_over
  E  events: all 57 forced; every unlocked option clicked and drained
     to completion (--quick: first option only)
  M  exhaustive content sweeps (tests/sweeps.py): every encounter
     fought, every card attempted (playable effects execute; legality
     rejects stay clean), every potion drunk, every relic obtained
  F  the full act-1 parity loop (tests/parity.py), key sets recorded
  H  request audit trail (STS2_AGENT_HTTP_LOG line format)

  Coverage map — phases: all but relic_reward (no reachable trigger in
  the current game build; pick-relic is exercised in treasure) and
  overlay/unknown (fault phases by design). Verbs: all, including every
  cheat. Content: all cards/potions/relics/encounters/events/characters
  (per /models). Outcomes: victory and defeat (abandoned rides R3/R4).
  Combinatorial interactions (card x relic x enemy) remain sampled, not
  enumerated — parity, V1, and real runs are that layer.

  e2e.py --boot           boot a host on STS2_AGENT_PORT (default 7779),
                          use this checkout's release CLI, run all cases,
                          tear the host down
  e2e.py                  run against an already-listening bridge
                          (boot-log and audit-trail cases are skipped)
  e2e.py --quick          skip the M sweeps, E1 clicks first options only
  e2e.py --only P1,M2     run a subset (case-name prefixes)
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
import tempfile
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
from protocol import (  # noqa: E402
    CHEAT_ARGUMENT_SHAPES,
    FAULT_EVENT_TOKENS,
    PHASE,
    PROTOCOL_VERSION,
    REJECTION,
)

run, obs = bridge.run, bridge.obs

CASES = []
LOG_PATH = None  # set in main() when --boot
# The pinned parity seed (see F1): full path coverage — shop with
# potions (one opens a mid-combat picker in the boss fight), treasure,
# smith. SPIRECI2/SPIRECI3 also pass, with less potion coverage.
PARITY_SEED = "SPIRECI1"
WORLD_CLAIMS = {
    "claim_reward_tiles": True,
    "claim_card_reward": True,
    "claim_relic_reward": True,
}
VICTORY_CLAIMS = {
    "claim_reward_tiles": True,
    "claim_relic_reward": True,
}


def case(name, boot_only=False, deep=False):
    def deco(fn):
        CASES.append((name, boot_only, deep, fn))
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


def followed_http_obs(status, result, description):
    """Validate a raw /step follow response before consuming its snapshot."""
    assert status == 200 and result.get("ok") is True, \
        f"{description}: {status} {result}"
    assert result.get("settled") is True, \
        f"{description} did not settle: {result.get('outcome')}"
    assert result.get("outcome") in ("settled", "next_decision"), \
        f"{description} faulted: {result.get('outcome')}"
    assert result.get("errors") == [], \
        f"{description} reported engine errors: {result.get('errors')}"
    snapshot = result.get("obs")
    assert isinstance(snapshot, dict), f"{description} returned no observation"
    return snapshot


def await_semantic_snapshot(settled, predicate, description, timeout=10):
    """Read past settlement when presentation data is not revision-bearing."""
    if predicate(settled):
        return settled
    return bridge.wait_until(
        predicate,
        timeout=timeout,
        description=description,
    )


def host_log():
    assert LOG_PATH, "boot-only case ran without --boot"
    with open(LOG_PATH, encoding="utf-8", errors="replace") as f:
        return f.read()


def to_menu():
    bridge.run("abandon", allow_fail=True)
    bridge.wait_phase(PHASE.MAIN_MENU, timeout=15)


def launch(character="IRONCLAD", seed=None):
    to_menu()
    bridge.launch_run(character=character, seed=seed)


def character_roster():
    to_menu()
    err = reject(["new-run", "NOT_A_CHARACTER"], REJECTION.BAD_REQUEST)
    names = [n for n in re.findall(r"[A-Z][A-Z_]{3,}", err)
             if n != "NOT_A_CHARACTER"]
    assert "IRONCLAD" in names, f"roster not in the rejection: {err}"
    return list(dict.fromkeys(names))


def run_test_script(script, *args):
    completed = subprocess.run(
        [sys.executable, os.path.join(REPO, "tests", script), *args])
    assert completed.returncode == 0, \
        f"{script} exited {completed.returncode}"


def configure_cli_for_boot():
    """A self-booted checkout must drive its host with the same checkout's
    CLI. Falling back to a deployed PATH binary can preserve the same numeric
    protocol while carrying an older replay projection, producing a false
    reconstruction divergence long after the compatibility gate."""
    selected = os.environ.get("SPIRESCRY_BIN")
    if not selected:
        selected = os.path.join(
            REPO, "cli", "target", "release", "spirescry")
        if not os.path.isfile(selected) or not os.access(selected, os.X_OK):
            sys.exit(
                f"checkout CLI not built ({selected}) — run: ./build.sh cli")
    bridge.BIN = selected
    os.environ["SPIRESCRY_BIN"] = selected
    return selected


def to_map(seed=None, character="IRONCLAD"):
    launch(character=character, seed=seed)
    run("proceed")
    return bridge.wait_phase(PHASE.MAP)


def into_combat(seed=None, character="IRONCLAD"):
    d = to_map(seed=seed, character=character)
    node = next(p for p in d["next"] if p["type"] == "monster")
    before_rev = d["rev"]
    run("map-move", str(node["col"]), str(node["row"]))
    return bridge.wait_until(
        lambda snapshot: snapshot.get("phase") == PHASE.COMBAT
        and snapshot.get("side") == "player",
        description="combat player turn",
        after_rev=before_rev,
    )


def alive_enemy(d):
    return next(e for e in d["enemies"] if e["alive"])


def latest_runlog_entry(action, *, cheat=None):
    return next(
        verb for verb in reversed(run("runlog")["verbs"])
        if verb["action"] == action
        and (cheat is None or verb.get("args", {}).get("name") == cheat)
    )


def open_amalgamator_picker():
    to_map(seed="CIAMALG")
    run("cheat", PHASE.EVENT, "AMALGAMATOR")
    d = bridge.wait_phase(PHASE.EVENT)
    combine = next(
        option for option in d["options"]
        if "defend" in (
            (option.get("title") or "") + (option.get("description") or "")
        ).lower()
        and not option.get("locked")
    )
    deck_before = [card["model"] for card in obs()["player"]["deck"]]
    picking = run("option", str(combine["idx"]), "--follow", "5000")
    assert picking["obs"]["phase"] == PHASE.CARD_SELECT, picking["obs"]["phase"]
    return deck_before


# ---------- B: boot ----------

@case("B1 health shape")
def b1():
    status, d = http("GET", "/health")
    assert status == 200 and d["ok"] is True, d
    for k in ("mod", "version", "buildHash", "protocolVersion",
              "capabilities", "phase", "rev", "runId",
              "executorStuckMs", "pendingAsync", "pendingEventOptions",
              "queues"):
        assert k in d, f"health missing {k}: {sorted(d)}"
    caps = d["capabilities"]
    assert "end-turn" in caps["verbs"], caps
    assert "relic" in caps["cheats"], caps
    assert caps["cheatArgumentShapes"] == list(CHEAT_ARGUMENT_SHAPES), caps
    assert d["protocolVersion"] == PROTOCOL_VERSION, d["protocolVersion"]
    # build.sh stamps <gitref>[-dirty].<12-hex content hash>. Reject
    # "unknown" here: a direct dotnet build is alive but its inputs cannot
    # be matched to this checkout, which would let stale-host regressions pass.
    build_hash = d["buildHash"]
    assert re.fullmatch(
        r"[0-9a-f]{7,40}(?:-dirty)?\.[0-9a-f]{12}", build_hash), \
        (f"buildHash '{build_hash}' is not a content stamp — "
         "build the host via ./build.sh headless-setup so identity is verifiable")
    expected = subprocess.run(
        [os.path.join(REPO, "build.sh"), "stamp"],
        capture_output=True, text=True, timeout=60, check=True,
    ).stdout.strip()
    assert build_hash == expected, \
        f"host build '{build_hash}' != checkout stamp '{expected}' — stale host"


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


@case("P3b no-wait since reports change honestly")
def p3b():
    to_menu()
    cur = obs()["rev"]

    t0 = time.monotonic()
    unchanged = run("obs", "--since", str(cur))
    assert time.monotonic() - t0 < 1, "omitted --wait unexpectedly parked"
    assert unchanged.get("changed") is False, unchanged
    assert unchanged.get("events") == [], unchanged.get("events")

    explicit = run("obs", "--since", str(cur), "--wait", "0")
    assert explicit.get("changed") is False, explicit

    launch(seed="CIP3B")
    advanced = run("obs", "--since", str(cur), "--wait", "0")
    assert advanced.get("changed") is True, advanced
    assert advanced.get("events"), "advanced rev returned no events"
    to_menu()


@case("P4 unknown routes 404")
def p4():
    for method, path in (("GET", "/nope"), ("POST", "/obs"), ("GET", "/step")):
        status, d = http(method, path, {} if method == "POST" else None)
        assert status == 404 and d.get("err") == REJECTION.NOT_FOUND, \
            f"{method} {path} -> {status} {d}"


@case("P5 bad character is rejected with the roster")
def p5():
    print(f"    roster: {character_roster()}")


@case("P6 unknown cheat lists the surface")
def p6():
    launch(seed="CIP6")
    err = reject(["cheat", "bogus"], REJECTION.BAD_REQUEST)
    for tok in ("wound-enemies", "card-upgraded", "relic"):
        assert tok in err, f"'{tok}' missing from: {err}"
    to_menu()


@case("P7 unknown /step action is rejected")
def p7():
    status, d = http("POST", "/step", {"action": "warp", "args": {}})
    assert status >= 400 and d.get("ok") is False, f"{status} {d}"
    assert "runId" in d, d


@case("P8 run identity and optimistic guards")
def p8():
    to_menu()
    menu = obs()
    assert menu["runId"] == "none", menu
    launch(seed="CIGUARDS")
    cur = obs()
    run_id, rev = cur["runId"], cur["rev"]
    assert run_id != "none", cur

    status, d = http("POST", "/step", {
        "action": "proceed", "args": {}, "ifRun": "replaced-run",
    })
    assert status == 400 and d.get("err") == REJECTION.EXTERNAL_CHANGE, d
    assert d.get("runId") == run_id, d

    status, d = http("POST", "/step", {
        "action": "proceed", "args": {}, "ifRun": run_id,
        "ifRev": max(0, rev - 1),
    })
    assert status == 400 and d.get("err") == REJECTION.STALE_STATE, d
    assert d.get("runId") == run_id, d

    for bad in ({"ifRev": "1"}, {"ifRev": -1}, {"ifRun": ""}):
        status, d = http("POST", "/step", {
            "action": "proceed", "args": {}, **bad,
        })
        assert status == 400 and d.get("err") == REJECTION.BAD_REQUEST, (bad, d)
        assert d.get("runId") == run_id, d

    status, d = http("POST", "/step", {
        "action": "proceed", "args": {}, "ifRun": run_id, "ifRev": rev,
    })
    assert status == 200 and d.get("ok") is True, d
    assert d.get("runId") == run_id, d
    bridge.wait_phase(PHASE.MAP)
    to_menu()


@case("P9 decision projection is stable and caller-scoped")
def p9():
    to_menu()
    menu = run("obs", "--decision")
    assert menu["legal"] == ["new-run"], menu["legal"]

    into_combat(seed="CIDEcision")
    bridge.follow("cheat", "card", "BASH")
    bridge.follow("cheat", "card-upgraded", "BASH")
    first = run("obs", "--decision")
    second = run("obs", "--decision")
    assert first["legal"] == second["legal"], (first["legal"], second["legal"])
    for verb in ("play", "end-turn", "abandon"):
        assert verb in first["legal"], first["legal"]

    def text_shape(d):
        return [(c.get("textKey"), c.get("description")) for c in d["hand"]]

    # GET is referentially stable: it does not consume a process-global
    # first-sighting set. Duplicate models carry prose once per response.
    assert text_shape(first) == text_shape(second)
    by_key = {}
    for card in first["hand"]:
        by_key.setdefault(card["textKey"], []).append(card.get("description"))
    assert all(sum(text is not None for text in texts) <= 1
               for texts in by_key.values()), by_key
    assert "BASH+0" in by_key and "BASH+1" in by_key, sorted(by_key)

    known = next(c["textKey"] for c in first["hand"]
                 if c.get("description") is not None)
    cached = run("obs", "--decision", "--known-card", known)
    assert all(c.get("description") is None
               for c in cached["hand"] if c["textKey"] == known), cached["hand"]
    to_menu()


@case("P10 follow waits for settlement or the next decision")
def p10():
    to_menu()
    before = obs()
    launched = run(
        "new-run", "IRONCLAD", "--seed", "CIFOLLOW",
        "--if-rev", str(before["rev"]), "--if-run", before["runId"],
        "--follow", "5000",
    )
    assert launched["settled"] is True, launched
    assert launched["outcome"] in ("settled", "next_decision"), launched
    assert launched["acceptedRev"] <= launched["rev"], launched
    assert launched["runId"] == launched["obs"]["runId"], launched
    assert launched["obs"]["phase"] == PHASE.EVENT, launched["obs"]
    assert launched["obs"].get("legal"), launched["obs"]
    # The engine-fault channel is part of the follow contract: present,
    # and empty on a clean action.
    assert launched["errors"] == [], launched["errors"]

    for bad_follow in ("5000", -1, 60001):
        status, d = http("POST", "/step", {
            "action": "proceed", "args": {}, "follow": bad_follow,
        })
        assert status == 400 and d.get("err") == REJECTION.BAD_REQUEST, (bad_follow, d)
        assert d.get("runId") == launched["runId"], d

    run("proceed", "--follow", "5000")
    d = bridge.wait_phase(PHASE.MAP)
    rest = next(point for point in d["graph"] if point["type"] == "restsite")
    entered = run(
        "cheat", "goto", str(rest["col"]), str(rest["row"]), "--follow", "5000")
    assert entered["obs"]["phase"] == PHASE.REST_SITE, entered["obs"]
    rest_obs = entered["obs"]
    smith = next(option for option in rest_obs["options"]
                 if "smith" in option["id"].lower() and option["enabled"])

    picking = run("option", str(smith["idx"]), "--follow", "5000")
    assert picking["outcome"] == "next_decision", picking
    assert picking["obs"]["phase"] == PHASE.CARD_SELECT, picking["obs"]
    assert "pick-card" in picking["obs"]["legal"], picking["obs"]["legal"]

    resolved = run("pick-card", "0", "--follow", "5000")
    assert resolved["outcome"] == "settled", resolved
    assert resolved["obs"]["phase"] == PHASE.REST_SITE, resolved["obs"]
    to_menu()


@case("P11 runlog reconstruction verifies every verb and stops on divergence")
def p11():
    to_menu()

    # Accepted verbs without follow are diagnostic history, but are not a
    # replayable recipe because no settled fingerprint was captured.
    run("new-run", "IRONCLAD", "--seed", "CIUNVERIFIED")
    incomplete = run("runlog")
    assert incomplete["complete"] is False, incomplete
    assert not incomplete["verbs"][0].get("fingerprint"), incomplete["verbs"]
    to_menu()

    run("new-run", "IRONCLAD", "--seed", "CIRUNLOG", "--follow", "5000")
    run("proceed", "--follow", "5000")
    bridge.wait_phase(PHASE.MAP)
    log = run("runlog")
    assert log["complete"] is True, log
    assert log["kind"] == "diagnostic_reconstruction_recipe", log
    assert log["runId"] != "none", log
    assert log["verbs"] and log["verbs"][0]["action"] == "new-run", log
    assert all(v["runId"] == log["runId"] for v in log["verbs"]), log["verbs"]
    assert all(v.get("fingerprint") for v in log["verbs"]), log["verbs"]

    with tempfile.TemporaryDirectory(prefix="spirescry-runlog-") as td:
        recipe = os.path.join(td, "recipe.json")
        with open(recipe, "w", encoding="utf-8") as f:
            json.dump(log, f, ensure_ascii=False)

        # Replay is intentionally non-destructive: it refuses to abandon or
        # replace a live run on the caller's behalf.
        active = bridge.cli("replay", recipe)
        assert active.returncode != 0 and "requires a clean main_menu" in active.stderr, active.stderr

        to_menu()
        rebuilt = run("replay", recipe)
        assert rebuilt["kind"] == "diagnostic_reconstruction_result", rebuilt
        assert rebuilt["sourceRunId"] == log["runId"], rebuilt
        assert rebuilt["reconstructionRunId"] != log["runId"], rebuilt
        assert rebuilt["verifiedFingerprints"] == len(log["verbs"]), rebuilt
        assert rebuilt["verifiedFingerprints"] > 0, rebuilt
        assert "not the source run" in rebuilt["attribution"], rebuilt

        to_menu()
        broken = json.loads(json.dumps(log))
        broken["verbs"][0]["fingerprint"] = "0000000000000000"
        bad_recipe = os.path.join(td, "broken.json")
        with open(bad_recipe, "w", encoding="utf-8") as f:
            json.dump(broken, f, ensure_ascii=False)
        diverged = bridge.cli("replay", bad_recipe)
        assert diverged.returncode != 0 and "divergence at verb 1" in diverged.stderr, diverged.stderr

        # Even a hand-edited complete flag cannot bypass fingerprint checks.
        missing = json.loads(json.dumps(log))
        del missing["verbs"][0]["fingerprint"]
        missing["complete"] = True
        missing_recipe = os.path.join(td, "missing.json")
        with open(missing_recipe, "w", encoding="utf-8") as f:
            json.dump(missing, f, ensure_ascii=False)
        rejected = bridge.cli("replay", missing_recipe)
        assert rejected.returncode != 0 and "no verifiable settled fingerprint" in rejected.stderr, rejected.stderr
    to_menu()


@case("P12 asynchronous verb faults wake observation waiters")
def p12():
    to_menu()
    accepted = run("cheat", "async-fault")
    t0 = time.monotonic()
    changed = run("obs", "--since", str(accepted["rev"]), "--wait", "2000")
    took = time.monotonic() - t0
    fault_events = [
        event for event in changed.get("events", [])
        if event["type"].startswith(
            FAULT_EVENT_TOKENS["asyncFault"] + "forced-async-fault:")
    ]
    assert changed.get("changed") is True, changed
    assert fault_events, changed.get("events")
    assert took < 1.5, f"fault event did not wake parked obs ({took:.2f}s)"

    # A followed verb whose async work faults must say so in `errors` —
    # "settled" alone is engine quiescence, not proof of a clean effect.
    followed = run("cheat", "async-fault", "--follow", "5000", allow_errors=True)
    assert any(
        error.startswith(FAULT_EVENT_TOKENS["asyncFault"] + "forced-async-fault:")
        for error in followed["errors"]
    ), followed["errors"]


@case("P13 engine log errors surface in follow errors and the runlog")
def p13():
    # The engine logs-and-swallows faults inside its own task chains; the
    # engine-error cheat writes through that same Error logger, so this
    # regression covers the log-line channel end to end (async-fault in
    # P12 covers the tracked-task stream).
    launch(seed="CIENGERR")
    faulted = run("cheat", "engine-error", "--follow", "5000", allow_errors=True)
    assert faulted["settled"] is True, faulted
    assert any(
        error.startswith(FAULT_EVENT_TOKENS["engineError"])
        and "forced engine log error" in error
        for error in faulted["errors"]
    ), faulted["errors"]

    # The fault survives into the diagnostic recipe: forensics must not
    # depend on the host log alone.
    entry = latest_runlog_entry("cheat", cheat="engine-error")
    assert any("forced engine log error" in e for e in entry.get("errors", [])), entry

    # Delayed variant: the error line lands from a tracked continuation
    # ~250ms after acceptance. Follow must stay busy across the delay and
    # carry the fault in THIS response — a first-quiet-probe return would
    # report errors: [] and leak the fault past the runlog entry too.
    delayed = run("cheat", "engine-error-delayed", "--follow", "5000",
                  allow_errors=True)
    assert any(
        error.startswith("engine_error:") and "delayed engine log error" in error
        for error in delayed["errors"]
    ), delayed["errors"]

    # The delayed error is forced after the verb was accepted. One read-only
    # command must retain its complete forensic trail before abandon destroys
    # the run-scoped bridge journals.
    bundle = run("fault-bundle")
    sections = bundle["sections"]
    source_run_id = sections["run"]["value"]["runId"]
    assert bundle["kind"] == "spirescry_fault_bundle", bundle
    assert bundle["readOnly"] is True, bundle
    assert bundle["revision"]["unchanged"] is True, bundle["revision"]
    assert all(sections[name]["available"] for name in (
        "runLog", "observation", "health", "recentEvents", "recentErrors",
        "identity", "run", "lastAcceptedVerb",
    )), sections
    assert sections["run"]["value"]["seed"] == "CIENGERR", sections["run"]
    assert sections["lastAcceptedVerb"]["value"]["args"]["name"] \
        == "engine-error-delayed", sections["lastAcceptedVerb"]
    assert any("delayed engine log error" in event["type"]
               for event in sections["recentErrors"]["value"]), \
        sections["recentErrors"]

    to_menu()
    assert obs()["runId"] == "none"
    assert sections["runLog"]["value"]["runId"] == source_run_id
    assert sections["lastAcceptedVerb"]["value"]["args"]["name"] \
        == "engine-error-delayed"


@case("P13b accepted observation faults retain a typed action outcome")
def p13b():
    launch(seed="CIOBSFAULT")

    completed = bridge.cli(
        "cheat", "observation-fault", "--follow", "5000")
    assert completed.returncode == 0, completed.stderr
    faulted = json.loads(completed.stdout)

    assert faulted["outcome"] == "fault" and faulted["settled"] is True, faulted
    assert faulted["action"] == "cheat" and faulted["enqueued"] == "cheat", faulted
    assert isinstance(faulted["acceptedRev"], int), faulted
    assert faulted["runId"] not in (None, "none"), faulted
    assert faulted["observationAvailable"] is False and faulted["obs"] is None, faulted
    assert any(
        error.startswith("async_fault:observation:InvalidOperationException:")
        and "forced post-acceptance observation failure" in error
        for error in faulted["errors"]
    ), faulted["errors"]
    assert "do not retry it blindly" in completed.stderr, completed.stderr

    entry = latest_runlog_entry("cheat", cheat="observation-fault")
    assert entry["outcome"] == "fault" and "fingerprint" not in entry, entry
    assert entry["acceptedRev"] == faulted["acceptedRev"], (entry, faulted)
    assert entry["errors"] == faulted["errors"], (entry, faulted)
    to_menu()


@case("P14 delayed event-option faults land in their own follow window")
def p14():
    # Integration regression for the synchronizer-boundary sweep: the
    # cheat appends a RunSafely-wrapped delayed throw to the REAL
    # _pendingOptionTasks list without telling the dispatcher — the way
    # a multiplayer client's vote arrives via a network message. The
    # per-tick sweep must discover the task, the three-state busy logic
    # must hold the follow open across the delay (no combat, nothing
    # parked), and the fault must land in this same response.
    launch(seed="CIEVOPT")  # parked at the Neow event
    faulted = run("cheat", "event-fault-delayed", "--follow", "5000",
                  allow_errors=True)
    assert faulted["settled"] is True, faulted
    assert any(
        error.startswith("async_fault:event-option:")
        and "delayed event-option failure" in error
        for error in faulted["errors"]
    ), faulted["errors"]

    entry = latest_runlog_entry("cheat", cheat="event-fault-delayed")
    assert any("delayed event-option failure" in e
               for e in entry.get("errors", [])), entry

    # The full client window: the cheat leaves only a pending vote —
    # NO task exists — and the "network" delivers the faulting task
    # ~600ms later. Nothing but the vote can hold the follow open
    # through the gap, so this fails if quiet frames close the response
    # before delivery.
    late = run("cheat", "event-fault-late", "--follow", "8000",
               allow_errors=True)
    assert late["settled"] is True, late
    assert any(
        error.startswith("async_fault:event-option:")
        and "delayed event-option failure" in error
        for error in late["errors"]
    ), late["errors"]
    entry = latest_runlog_entry("cheat", cheat="event-fault-late")
    assert any("delayed event-option failure" in e
               for e in entry.get("errors", [])), entry
    to_menu()


@case("P15 clean late event-option completion wakes its follow window")
def p15():
    # A client vote can resolve to a page-only Chosen() whose RunSafely
    # task is already complete when the next Tick inspects the engine.
    # Clearing the vote and observing that completed task must wake the
    # originating follow; otherwise it sleeps until its full deadline.
    launch(seed="CIEVOPTCLEAN")
    started = time.monotonic()
    completed = run("cheat", "event-complete-late", "--follow", "3000")
    elapsed = time.monotonic() - started
    assert completed["settled"] is True, completed
    assert completed["outcome"] == "settled", completed
    assert completed["errors"] == [], completed["errors"]
    assert sum(event["type"] == "async:event-option"
               for event in completed["events"]) == 1, completed["events"]
    assert elapsed < 2.0, f"clean delivery did not wake follow ({elapsed:.2f}s)"
    to_menu()


@case("P16 abandoned event-option work cannot enter the next run")
def p16():
    launch(seed="CIEVOPTOLD")
    run("cheat", "event-orphan")
    status, health = http("GET", "/health")
    assert status == 200 and health["pendingEventOptions"] == 1, health

    abandoned = run("abandon", "--follow", "3000")
    assert abandoned["outcome"] != "timeout", abandoned
    assert abandoned["obs"]["phase"] == "main_menu", abandoned["obs"]
    fresh = run("new-run", "IRONCLAD", "--seed", "CIEVOPTNEW",
                "--follow", "3000")
    assert fresh["outcome"] != "timeout", fresh
    status, health = http("GET", "/health")
    assert status == 200 and health["pendingEventOptions"] == 0, health

    # Complete the old task while writing a genuine current-run Error with
    # the SAME exception type/message. Text-only matching suppresses the
    # marked current line and leaks the unmarked stale line. Task-identity
    # correlation must do the reverse: exactly the marked engine_error is
    # attributed to this verb, while the old async fault stays retired.
    released = run("cheat", "event-orphan-collision", "--follow", "3000",
                   allow_errors=True)
    assert released["settled"] is True, released
    collisions = [
        error for error in released["errors"]
        if "orphan event-option failure" in error
    ]
    assert len(collisions) == 1, collisions
    assert collisions[0].startswith("engine_error:"), collisions
    assert "current-run duplicate marker" in collisions[0], collisions
    assert not any("engine-log-correlation" in event["type"]
                   for event in released["events"]), released["events"]
    entry = latest_runlog_entry("cheat", cheat="event-orphan-collision")
    assert any("current-run duplicate marker" in error
               for error in entry.get("errors", [])), entry
    to_menu()


@case("P17 retired tasks stay tombstoned while their synchronizer is live")
def p17():
    launch(seed="CIEVOPTSAME")
    run("cheat", "event-orphan")
    run("cheat", "event-owner-rotate")
    released = run("cheat", "event-orphan-fault", "--follow", "3000",
                   allow_errors=True)
    assert released["settled"] is True, released
    assert not any("orphan event-option failure" in error
                   for error in released["errors"]), released["errors"]
    assert not any("engine-log-correlation" in event["type"]
                   for event in released["events"]), released["events"]
    status, health = http("GET", "/health")
    assert status == 200 and health["pendingEventOptions"] == 0, health
    to_menu()


@case("P14 event economics are part of the semantic fingerprint")
def p14():
    to_map(seed="CIEVENTVARS")
    before = obs()["rev"]
    run("cheat", PHASE.EVENT, "DENSE_VEGETATION")
    event = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") == PHASE.EVENT,
        description="dynamic event to mount",
        after_rev=before,
    )
    assert "semanticState" not in event, event
    event = run("obs", "--semantic-state")
    variables = [
        token for token in event.get("semanticState", [])
        if token.startswith("eventVar:")
    ]
    assert variables, event
    decoded = [json.loads(token.split(":", 1)[1]) for token in variables]
    assert all(len(variable) == 5 for variable in decoded), decoded
    assert all(isinstance(variable[0], str)
               and isinstance(variable[1], str) for variable in decoded), decoded
    to_menu()


# ---------- R: run lifecycle ----------

@case("R1 same seed, same world")
def r1():
    def fingerprint():
        launch(seed="CIDETERM")
        neow = [o.get("title") for o in obs().get("options", [])]
        run("proceed")
        d = bridge.wait_phase(PHASE.MAP)
        return (d.get("seed"), neow,
                json.dumps(d.get("graph"), sort_keys=True))
    a, b = fingerprint(), fingerprint()
    to_menu()
    assert a[0] == b[0], f"seed drifted: {a[0]} vs {b[0]}"
    assert a[1] == b[1], f"Neow options drifted: {a[1]} vs {b[1]}"
    assert a[2] == b[2], "act-1 graph drifted between identical seeds"


@case("R2 every character boots and fights")
def r2():
    for c in character_roster():
        launch(character=c, seed="CICHAR")
        p = obs().get("player") or {}
        missing = [k for k in ("hp", "gold", "deck", "relics") if k not in p]
        assert not missing, f"{c}: player footer missing {missing}"
        run("proceed")
        d = bridge.wait_phase(PHASE.MAP)
        node = next(x for x in d["next"] if x["type"] == "monster")
        before_rev = d["rev"]
        run("map-move", str(node["col"]), str(node["row"]))
        d = bridge.wait_until(
            lambda snapshot: snapshot.get("phase") == PHASE.COMBAT
            and snapshot.get("side") == "player",
            description=f"{c} opening player turn",
            after_rev=before_rev,
        )
        you = d["you"]
        assert isinstance(you.get("hp"), list) and isinstance(you.get("energy"), list), you
        assert "stars" in you, f"{c}: combat snapshot lost the stars field"
        assert d["hand"], f"{c}: empty opening hand"
        for card in d["hand"]:
            assert card.get("model") and "cost" in card and "vars" in card, card
        en = d["enemies"][0]
        assert en.get("title") and "intents" in en, en
        print(f"    {c}: hp={you['hp']} energy={you['energy']} "
              f"stars={you['stars']} hand={len(d['hand'])}")
        to_menu()  # a mid-combat abandon per character (regression: #66)


@case("R3 abandon mid-run returns to the menu")
def r3():
    to_map(seed="CIR3")
    run("abandon")
    bridge.wait_phase(PHASE.MAIN_MENU)


@case("R3b pre-combat abandon tolerates missing combat manager", boot_only=True)
def r3b():
    to_map(seed="CIR3B")
    before = len(host_log())
    run("abandon")
    bridge.wait_phase(PHASE.MAIN_MENU)
    teardown_log = host_log()[before:]
    assert "abandon combat reset" not in teardown_log, teardown_log[-1000:]


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
    assert d.get("phase") == PHASE.COMBAT, f"combat did not load: {d.get('phase')}"
    to_menu()


# ---------- C: combat ----------

@case("C1 combat economy: block, energy, bad_target, overdraw")
def c1():
    d = into_combat(seed="CICOMBAT")
    assert d.get("side") == "player", f"never got the player turn: {d.get('side')}"
    e0 = d["you"]["energy"][0]

    defend = next(c for c in d["hand"] if c["model"].startswith("DEFEND"))
    before_rev = d["rev"]
    run("play", defend["model"])
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") != PHASE.COMBAT
        or snapshot.get("you", {}).get("block", 0) > 0,
        description="Defend to grant block",
        after_rev=before_rev,
    )
    assert d["you"]["block"] > 0, "Defend raised no block"
    assert d["you"]["energy"][0] == e0 - defend["cost"], \
        f"energy {e0} - {defend['cost']} != {d['you']['energy'][0]}"

    atk = next(c for c in d["hand"] if c["target"] == "anyenemy")
    reject(["play", atk["model"], "--target", "99"], REJECTION.BAD_TARGET)

    # Drain energy with the cheapest legal plays; the first over-cost
    # attempt must come back as not_enough_energy, not something vaguer.
    for _ in range(8):
        d = obs()
        if d["phase"] != PHASE.COMBAT:
            break
        energy = d["you"]["energy"][0]
        hand = [c for c in d["hand"] if not c.get("unplayable")]
        over = [c for c in hand if c["cost"] > energy]
        playable = [c for c in hand if c["cost"] <= energy]
        if over:
            c = over[0]
            args = ["play", c["model"]]
            if c["target"] == "anyenemy":
                args += ["--target", str(alive_enemy(d)["id"])]
            reject(args, REJECTION.NOT_ENOUGH_ENERGY)
            break
        assert playable, "hand emptied before any card went over cost"
        c = min(playable, key=lambda c: c["cost"])
        args = ["play", c["model"]]
        if c["target"] == "anyenemy":
            args += ["--target", str(alive_enemy(d)["id"])]
        before_rev = d["rev"]
        copies = sum(card["model"] == c["model"] for card in d["hand"])
        run(*args)
        bridge.wait_until(
            lambda snapshot: snapshot.get("phase") != PHASE.COMBAT
            or sum(card["model"] == c["model"]
                   for card in snapshot.get("hand", [])) < copies,
            description=f"played {c['model']} to leave the hand",
            after_rev=before_rev,
        )
    else:
        raise AssertionError("never ran out of energy in 8 plays")

    # Leave through the real death pipeline (R4 covers the mid-combat
    # abandon path directly).
    bridge.kill_current_combat()
    bridge.wait_phase(PHASE.REWARDS)
    run("proceed")
    bridge.wait_phase(PHASE.MAP)
    to_menu()


@case("C2 orb economy is visible through /obs")
def c2():
    d = into_combat(seed="CIORBS", character="DEFECT")
    orbs = d["you"]["orbs"]
    assert orbs["slots"] > 0, f"Defect has no orb capacity: {orbs}"
    assert orbs["channeled"], f"Defect opened with no channeled orb: {orbs}"
    first = orbs["channeled"][0]
    assert set(("id", "passive", "evoke")) <= first.keys(), first
    assert first["id"].endswith("_ORB"), first
    to_menu()

    d = into_combat(seed="CIORBSNO", character="IRONCLAD")
    assert d["you"]["orbs"] is None, d["you"]["orbs"]
    to_menu()


@case("C3 power text expands energyPrefix without leaking format state")
def c3():
    d = into_combat(seed="CILOC")
    d = bridge.follow("cheat", "card", "FERAL")
    feral = next(c for c in d["hand"] if c["model"] == "FERAL")
    args = ["play", feral["model"]]
    if feral["target"] == "anyenemy":
        args += ["--target", str(alive_enemy(d)["id"])]
    before_rev = d["rev"]
    run(*args)
    d = bridge.wait_until(
        lambda snapshot: any(
            power.get("id") == "FERAL_POWER"
            for power in snapshot.get("you", {}).get("powers", [])),
        description="Feral power to apply",
        after_rev=before_rev,
    )
    power = next(p for p in d["you"]["powers"] if p["id"] == "FERAL_POWER")
    description = power["description"]
    assert "{energyPrefix" not in description, description
    assert "[energy]" in description, description
    assert next(p for p in obs()["you"]["powers"]
                if p["id"] == "FERAL_POWER")["description"] == description
    to_menu()


# ---------- S: shop ----------

@case("S1 shop: every buy kind, gold accounting, leave")
def s1():
    d = to_map(seed="CISHOP")
    shop = next((p for p in d["graph"] if p["type"] == PHASE.SHOP), None)
    assert shop, "seed CISHOP grew no shop — re-pin the seed"
    bridge.follow("cheat", "gold", "5000")
    run("cheat", "goto", str(shop["col"]), str(shop["row"]))
    d = bridge.wait_phase(PHASE.SHOP)

    def buy(kind, stock_key):
        before = obs()
        stock = before[stock_key]
        assert stock and stock[0]["stocked"], f"no {kind} in stock: {stock}"
        cost = stock[0]["cost"]
        run("buy", kind, "--idx", "0")
        after = bridge.wait_until(
            lambda snapshot: snapshot.get("gold") == before["gold"] - cost,
            description=f"{kind} purchase to debit gold",
            after_rev=before["rev"],
        )
        assert after["gold"] == before["gold"] - cost, \
            f"{kind}: gold {before['gold']} - {cost} != {after['gold']}"
        return after

    deck0 = len(obs()["player"]["deck"])
    buy("card", "cards")
    buy("colorless", "colorless")
    assert len(obs()["player"]["deck"]) == deck0 + 2, "bought cards missing from deck"

    relics0 = len(obs()["player"]["relics"])
    buy("relic", "relics")
    assert len(obs()["player"]["relics"]) == relics0 + 1, "bought relic missing"

    potions0 = len(obs()["player"]["potions"])
    buy("potion", "potions")
    assert len(obs()["player"]["potions"]) == potions0 + 1, "bought potion missing"

    d = obs()
    assert d["cardRemoval"] and not d["cardRemoval"]["used"], d.get("cardRemoval")
    run("buy", "card_removal", "--idx", "0")
    d = bridge.wait_phase(PHASE.CARD_SELECT)
    before_rev = d["rev"]
    run("pick-card", "0")
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") != PHASE.CARD_SELECT
        or snapshot.get("confirmable") is True,
        description="card removal pick to apply",
        after_rev=before_rev,
    )
    if d["phase"] == PHASE.CARD_SELECT:
        run("confirm")
    bridge.wait_phase(PHASE.SHOP)
    assert len(obs()["player"]["deck"]) == deck0 + 1, "removal did not shrink the deck"

    run("leave")
    bridge.wait_phase(PHASE.MAP)
    to_menu()


@case("S2 Foul Potion can be redeemed only at a merchant")
def s2():
    d = to_map(seed=PARITY_SEED)

    def cheat_potion(model):
        status, result = http("POST", "/step", {
            "action": "cheat", "args": {"name": "potion", "id": model},
            "follow": 5000,
        })
        return followed_http_obs(status, result, f"could not procure {model}")

    procured = cheat_potion("FOUL_POTION")
    foul = next(p for p in procured["player"]["potions"]
                if p["model"] == "FOUL_POTION")

    shop = next(p for p in d["graph"] if p["type"] == PHASE.SHOP)
    run("cheat", "goto", str(shop["col"]), str(shop["row"]))
    d = bridge.wait_phase(PHASE.SHOP)
    gold_before = d["gold"]
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": foul["slot"]},
        "follow": 5000,
    })
    d = followed_http_obs(
        status, result, "Foul Potion merchant redemption")
    gained = d["gold"] - gold_before
    assert gained > 0, f"Foul Potion awarded no gold: {gold_before} -> {d['gold']}"
    assert f"[blue]{gained}[/blue]" in foul["description"], \
        f"Foul Potion awarded {gained}, inconsistent with its description: {foul}"
    assert not any(p["slot"] == foul["slot"] for p in d["player"]["potions"]), \
        "redeemed Foul Potion stayed in its belt slot"

    procured = cheat_potion("ENERGY_POTION")
    energy = next(p for p in procured["player"]["potions"]
                  if p["model"] == "ENERGY_POTION")
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": energy["slot"]},
    })
    assert status == 400 and result.get("err") == REJECTION.NOT_PLAYABLE, \
        f"ordinary potion got the wrong merchant gate: {status} {result}"
    assert "merchant" in result.get("msg", "").lower(), result
    assert any(p["slot"] == energy["slot"] for p in obs()["player"]["potions"]), \
        "rejected ordinary potion left its belt slot"
    to_menu()


@case("S3 potion-use outside combat explains both supported contexts")
def s3():
    to_map(seed="CIPOTIONHINT")
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": 0},
    })
    assert status == 400 and result.get("err") == REJECTION.BAD_PHASE, result
    message = result.get("msg", "").lower()
    assert PHASE.COMBAT in message, result
    assert "foul potion" in message and "merchant" in message, result
    to_menu()


@case("W1 skip: card reward and treasure walk away clean")
def w1():
    d = into_combat(seed="CISKIP")
    d = bridge.follow("cheat", "wound-enemies")
    atk = next(c for c in d["hand"] if c["target"] == "anyenemy")
    run("play", atk["model"], "--target", str(alive_enemy(d)["id"]))
    d = bridge.wait_phase(PHASE.REWARDS)

    deck0 = len(obs()["player"]["deck"])
    card_tile = next(t for t in d["rewards"] if t["type"] == "card")
    run("pick-reward", str(card_tile["idx"]))
    d = bridge.wait_phase(PHASE.CARD_REWARD)
    assert d.get("cards"), "card reward offered nothing"
    before_rev = d["rev"]
    run("skip")
    d = bridge.wait_phase(
        PHASE.REWARDS, PHASE.MAP, after_rev=before_rev)
    assert len(d["player"]["deck"]) == deck0, "skip still added a card"
    if d["phase"] == PHASE.REWARDS:
        run("proceed")
        bridge.wait_phase(PHASE.MAP)

    tre = next((p for p in obs()["graph"] if p["type"] == PHASE.TREASURE), None)
    assert tre, "seed CISKIP grew no treasure — re-pin the seed"
    relics0 = len(obs()["player"]["relics"])
    run("cheat", "goto", str(tre["col"]), str(tre["row"]))
    d = bridge.wait_phase(PHASE.TREASURE)
    assert d.get("chestOpened") is False and not d.get("relics"), d
    before_rev = d["rev"]
    run("skip")  # opens the chest; observation stays read-only
    d = bridge.wait_until(
        lambda snapshot: bool(snapshot.get("relics")),
        description="treasure chest to expose relics",
        after_rev=before_rev,
    )
    after = bridge.follow("skip")  # declines the visible offer
    assert len(after["player"]["relics"]) == relics0, "skip still granted a relic"
    # The offer resolved but the chest does not close again: reading
    # chestOpened=false here would re-advertise the opening pick-relic.
    assert after["chestOpened"] is True, after
    to_menu()


@case("W2 CLI skip selects among multiple card reward alternatives")
def w2():
    d = into_combat(seed="CISKIPALT")
    d = bridge.follow("cheat", "relic", "PAELS_WING")
    assert any(r["model"] == "PAELS_WING" for r in d["you"]["relics"]), \
        "Pael's Wing was not obtained"

    d = bridge.follow("cheat", "wound-enemies")
    atk = next(c for c in d["hand"] if c["target"] == "anyenemy")
    run("play", atk["model"], "--target", str(alive_enemy(d)["id"]))
    rewards = bridge.wait_phase(PHASE.REWARDS)
    card_tile = next(t for t in rewards["rewards"] if t["type"] == "card")
    deck0 = len(obs()["player"]["deck"])
    run("pick-reward", str(card_tile["idx"]))
    offered = bridge.wait_phase(PHASE.CARD_REWARD)
    alternatives = offered.get("alternatives", [])
    assert len(alternatives) >= 2, alternatives

    reject(["skip"], REJECTION.BAD_REQUEST)
    run("skip", str(alternatives[-1]["idx"]))
    bridge.wait_phase(PHASE.REWARDS, PHASE.MAP)
    assert len(obs()["player"]["deck"]) == deck0, "alternative skip added a card"
    to_menu()


@case("W3 treasure observation is read-only until a verb opens the chest")
def w3():
    to_map(seed="CITREASUREOBS")
    tre = next((p for p in obs()["graph"] if p["type"] == PHASE.TREASURE), None)
    assert tre, "seed CITREASUREOBS grew no treasure — re-pin the seed"
    run("cheat", "goto", str(tre["col"]), str(tre["row"]))

    first = bridge.wait_phase(PHASE.TREASURE)
    second = obs()
    assert first["chestOpened"] is False and second["chestOpened"] is False
    assert first["relics"] == [] and second["relics"] == []
    assert first["player"]["gold"] == second["player"]["gold"], \
        "observing the closed chest changed gold"

    # The closed chest must advertise its opening verb: an agent that
    # only fires legal verbs otherwise walks past every treasure room.
    closed = run("obs", "--decision")
    assert "pick-relic" in closed["legal"], closed["legal"]

    relics0 = len(first["player"]["relics"])
    opened = bridge.follow("pick-relic", "0")
    assert opened["chestOpened"] is True and opened["relics"], opened
    claimed = bridge.follow("pick-relic", "0")
    assert len(claimed["player"]["relics"]) == relics0 + 1
    # Resolved offer: chest stays open, pick-relic is no longer legal.
    resolved = run("obs", "--decision")
    assert resolved["chestOpened"] is True, resolved
    assert "pick-relic" not in resolved["legal"], resolved["legal"]
    run("proceed")
    bridge.wait_phase(PHASE.MAP)
    to_menu()


@case("W4 treasure relic can be followed by skip")
def w4():
    d = to_map(seed=PARITY_SEED)
    treasure = next(p for p in d["graph"] if p["type"] == PHASE.TREASURE)
    run("cheat", "goto", str(treasure["col"]), str(treasure["row"]))
    bridge.wait_phase(PHASE.TREASURE)

    d = bridge.follow("pick-relic", "0")
    assert d.get("relics"), d
    d = bridge.follow("pick-relic", str(d["relics"][0]["idx"]))
    if d.get("phase") == PHASE.TREASURE:
        status, result = http("POST", "/step", {"action": "skip", "args": {}})
        assert status == 200 and result.get("ok") is True, \
            f"skip after pick-relic was permanently rejected: {status} {result}"
    bridge.wait_phase(PHASE.MAP)
    to_menu()


@case("W5 combat reward slots are claimable exactly once")
def w5():
    into_combat(seed=PARITY_SEED)
    bridge.kill_current_combat()
    d = bridge.wait_phase(PHASE.REWARDS)
    reward = next(r for r in d["rewards"] if r["type"] == "potion")
    reward_idx = reward["idx"]
    belt_before = {p["slot"]: p for p in d["player"]["potions"]}

    status, result = http("POST", "/step", {
        "action": "pick-reward", "args": {"idx": reward_idx},
        "follow": 5000,
    })
    claimed = followed_http_obs(status, result, "potion reward claim")
    belt_after_first = claimed["player"]["potions"]
    added = [p for p in belt_after_first if p["slot"] not in belt_before]
    assert len(added) == 1, \
        f"potion reward changed the belt by {len(added)} slots: {belt_before} -> {belt_after_first}"
    potion_model = added[0]["model"]
    model_count_before = sum(p["model"] == potion_model for p in belt_before.values())
    assert sum(p["model"] == potion_model for p in belt_after_first) == model_count_before + 1, \
        f"potion reward did not add exactly one {potion_model}: {belt_after_first}"

    status, result = http("POST", "/step", {
        "action": "pick-reward", "args": {"idx": reward_idx},
    })
    assert status == 400 and result.get("err") == REJECTION.BAD_INDEX, \
        f"consumed reward slot was claimable twice: {status} {result}"
    belt_after_second = obs()["player"]["potions"]
    assert belt_after_second == belt_after_first, \
        f"repeat reward claim changed the belt: {belt_after_first} -> {belt_after_second}"
    assert sum(p["model"] == potion_model for p in belt_after_second) == model_count_before + 1, \
        f"repeat reward claim duplicated {potion_model}: {belt_after_second}"
    to_menu()


@case("W6 Act 4 treasure entry completes without a map-action wedge", boot_only=True)
def w6():
    d = to_map(seed="CIACT4TREASURE")

    # Dev-cheat only the long approach; the Act 4 transition and treasure
    # entry below use the public rewards/map-move interfaces under test.
    for next_act in range(1, 4):
        boss = next(p for p in d["graph"] if p["type"] == "boss")
        run("cheat", "goto", str(boss["col"]), str(boss["row"]), allow_fail=True)
        bridge.wait_phase(PHASE.COMBAT, timeout=30)
        bridge.kill_current_combat()
        bridge.wait_phase(PHASE.REWARDS)
        run("proceed")
        d = bridge.wait_until(
            lambda snapshot: snapshot.get("phase") == PHASE.MAP
            and snapshot.get("act") == next_act,
            timeout=30,
            description=f"act {next_act + 1} map",
        )

    graph = d["graph"]
    treasure = None
    for target in (p for p in graph if p["type"] == PHASE.TREASURE):
        predecessors = [p for p in graph
                        if p["row"] == target["row"] - 1
                        and abs(p["col"] - target["col"]) <= 1
                        and p["type"] != "unknown"]
        predecessors.sort(key=lambda p: (
            {"monster": 0, "elite": 0, "restsite": 1,
             PHASE.SHOP: 2, PHASE.TREASURE: 3}.get(p["type"], 4),
            abs(p["col"] - target["col"]),
        ))
        for predecessor in predecessors:
            run("cheat", "goto", str(predecessor["col"]), str(predecessor["row"]),
                allow_fail=True)
            phase = bridge.wait_phase(
                PHASE.COMBAT, PHASE.REST_SITE, PHASE.SHOP, PHASE.TREASURE, timeout=30)["phase"]
            if phase == PHASE.COMBAT:
                bridge.kill_current_combat()
                bridge.wait_phase(PHASE.REWARDS)
                run("proceed")
            elif phase == PHASE.SHOP:
                run("leave")
            else:
                run("proceed")
            d = bridge.wait_phase(PHASE.MAP)
            if any(p["col"] == target["col"] and p["row"] == target["row"]
                   for p in d["next"]):
                treasure = target
                break
        if treasure:
            break

    assert treasure, "could not establish a reachable Act 4 treasure predecessor"
    run("map-move", str(treasure["col"]), str(treasure["row"]))
    bridge.wait_phase(PHASE.TREASURE, timeout=12)
    d = bridge.follow("pick-relic", "0")
    assert d.get("relics"), d
    relics_before = len(d["player"]["relics"])
    d = bridge.follow("pick-relic", str(d["relics"][0]["idx"]))
    assert len(d["player"]["relics"]) == relics_before + 1, \
        "Act 4 treasure selection did not grant exactly one relic"
    if d.get("phase") == PHASE.TREASURE:
        run("skip")
    bridge.wait_phase(PHASE.MAP)
    assert "wedge:" not in host_log(), \
        "Act 4 treasure travel tripped the executor watchdog"
    to_menu()


# ---------- X: special screens ----------

@case("X1 crystal sphere: dig, tool verb, rewards out")
def x1():
    to_map(seed="CICRYS")
    run("cheat", PHASE.EVENT, "CRYSTAL_SPHERE")
    d = bridge.wait_phase(PHASE.EVENT)
    run("option", "0")  # Uncover Future
    d = bridge.wait_phase(PHASE.CRYSTAL_SPHERE)
    assert d["grid"]["width"] > 0 and d["divinationsLeft"] > 0, d["grid"]
    assert d["cells"], "no cells in the crystal snapshot"
    before = d.get("tool")
    assert before, "no tool in the crystal snapshot"
    before_rev = d["rev"]
    run("option", "0" if before == "big" else "1")  # the OTHER tool
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("tool") != before,
        description="crystal sphere tool to change",
        after_rev=before_rev,
    )
    assert d["tool"] != before, f"tool verb changed nothing (still {d['tool']})"
    left = d["divinationsLeft"]
    for _ in range(left + 2):
        d = obs()
        if d["phase"] != PHASE.CRYSTAL_SPHERE or d.get("finished"):
            break
        hidden = next((c for c in d["cells"] if c["hidden"]), None)
        assert hidden, "no hidden cells left but the minigame isn't finished"
        hidden_count = sum(cell["hidden"] for cell in d["cells"])
        before_rev = d["rev"]
        run("map-move", str(hidden["col"]), str(hidden["row"]))
        d = bridge.wait_until(
            lambda snapshot: snapshot.get("phase") != PHASE.CRYSTAL_SPHERE
            or snapshot.get("finished")
            or sum(cell.get("hidden", False)
                   for cell in snapshot.get("cells", [])) < hidden_count,
            description="crystal sphere cell to reveal",
            after_rev=before_rev,
        )
    d = bridge.wait_phase(PHASE.REWARDS, PHASE.MAP, PHASE.EVENT, timeout=15)
    if d["phase"] == PHASE.REWARDS:
        run("proceed")
        bridge.wait_phase(PHASE.MAP)
    to_menu()


@case("X2 bundle select: Neow's Scroll Boxes")
def x2():
    launch(seed="BX16")
    d = obs()
    pack = next((o for o in d.get("options", [])
                 if "pack" in (o.get("description") or "").lower()), None)
    assert pack, ("seed BX16 no longer offers Scroll Boxes — re-pin: "
                  f"{[o.get('title') for o in d.get('options', [])]}")
    deck0 = len(obs()["player"]["deck"])
    run("option", str(pack["idx"]))
    d = bridge.wait_phase(PHASE.BUNDLE_SELECT)
    bundles = d.get("bundles")
    assert bundles and bundles[0]["cards"], f"empty bundle offer: {d}"
    picked = len(bundles[0]["cards"])
    before_rev = d["rev"]
    run("pick-card", "0")
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") != PHASE.BUNDLE_SELECT,
        description="bundle pick to resolve",
        after_rev=before_rev,
    )
    assert d["phase"] != PHASE.BUNDLE_SELECT, "pick-card did not resolve the bundle"
    assert len(obs()["player"]["deck"]) == deck0 + picked, \
        f"deck did not grow by the pack ({picked})"
    to_menu()


@case("X3 compact crystal reveals decisions without dumping the board")
def x3():
    to_map(seed="CICRYSTALINFO")
    run("cheat", PHASE.EVENT, "CRYSTAL_SPHERE")
    d = bridge.wait_phase(PHASE.EVENT)
    option = next(o for o in d["options"] if not o["locked"])
    run("option", str(option["idx"]))
    full = bridge.wait_phase(PHASE.CRYSTAL_SPHERE)

    compact = run("obs", "--compact")
    width, height = full["grid"]["width"], full["grid"]["height"]
    total_cells = width * height
    assert len(compact["cells"]) < total_cells, \
        f"compact crystal dumped the raw board: {len(compact['cells'])} cells"
    assert compact["hiddenCells"] == sum(c["hidden"] for c in full["cells"]), compact

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


# ---------- K: cheats ----------

@case("K1 cheat surface grafts real state")
def k1():
    to_map(seed="CICHEAT")
    bridge.follow("cheat", "gold", "123")
    bridge.follow("cheat", "relic", "VAJRA")
    bridge.follow("cheat", "card-upgraded", "STRIKE_IRONCLAD")
    bridge.follow("cheat", "card", "BASH")
    p = run("obs", "--compact")["player"]
    assert p["gold"] == 123, f"gold cheat: {p['gold']}"
    assert "VAJRA" in p["relics"], f"relic cheat: {p['relics']}"
    assert p["deck"].get("STRIKE_IRONCLAD+", 0) >= 1, \
        f"card-upgraded cheat: {p['deck']}"
    assert p["deck"].get("BASH", 0) >= 2, f"card cheat: {p['deck']}"
    to_menu()


@case("K2 relic IDs stay compatible while rich state follows combat")
def k2():
    full_obs = to_map(seed="CIRELICSTATE")
    full_obs = bridge.follow("cheat", "relic", "HAPPY_FLOWER")
    full_obs = await_semantic_snapshot(
        full_obs,
        lambda snapshot: any(
            relic.get("model") == "HAPPY_FLOWER" and relic.get("description")
            for relic in (snapshot.get("player") or {}).get("relicStates", [])),
        "HAPPY_FLOWER description hydration",
    )
    full = full_obs["player"]
    assert "HAPPY_FLOWER" in full["relics"], \
        f"relic IDs changed shape: {full['relics']}"
    flower = next(r for r in full["relicStates"]
                  if r["model"] == "HAPPY_FLOWER")
    assert flower["counter"] == 0, f"initial HAPPY_FLOWER counter: {flower}"
    assert flower["usedUp"] is False, f"fresh relic marked used-up: {flower}"
    assert flower["description"], f"full relic description missing: {flower}"

    compact = run("obs", "--compact")["player"]
    compact_flower = next(r for r in compact["relicStates"]
                          if r["model"] == "HAPPY_FLOWER")
    assert compact_flower["description"] is None, \
        f"compact relic prose was not elided: {compact_flower}"

    monster = next(n for n in full_obs["graph"] if n["type"] == "monster")
    run("cheat", "goto", str(monster["col"]), str(monster["row"]))
    combat = bridge.wait_phase(PHASE.COMBAT, timeout=30)
    combat_flower = next(r for r in combat["you"]["relics"]
                         if r["model"] == "HAPPY_FLOWER")
    assert isinstance(combat_flower["counter"], int), \
        f"combat relic counter missing: {combat_flower}"
    assert combat_flower["usedUp"] is False, \
        f"fresh combat relic marked used-up: {combat_flower}"
    to_menu()


@case("K3 Spoils Map marks its next-act treasure node")
def k3():
    d = to_map(seed="CISPOILSMAP")
    d = bridge.follow("cheat", "card", "SPOILS_MAP")
    d = await_semantic_snapshot(
        d,
        lambda snapshot: bool(snapshot.get("graph")),
        "Spoils Map graph hydration",
    )
    assert "marked" not in d, d
    assert all("markers" not in point for point in d["graph"]), d["graph"]
    assert "marked" not in run("obs", "--compact")

    boss = next(p for p in d["graph"] if p["type"] == "boss")
    run("cheat", "goto", str(boss["col"]), str(boss["row"]), allow_fail=True)
    bridge.wait_phase(PHASE.COMBAT, timeout=30)
    bridge.kill_current_combat()
    bridge.wait_phase(PHASE.REWARDS)
    run("proceed")
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") == PHASE.MAP and snapshot.get("act") == 1,
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


@case("K4 Necrobinder combat exposes Osty as structured state")
def k4():
    absent = into_combat(seed="CIOSTYABSENT", character="IRONCLAD")
    assert "osty" in absent["you"] and absent["you"]["osty"] is None, \
        absent["you"]
    to_menu()

    d = into_combat(seed="CIOSTYSTATE", character="NECROBINDER")
    summoned = d["you"]["osty"]
    assert summoned["model"] == "OSTY" and summoned["title"], summoned
    assert summoned["hp"] == [1, 1], summoned

    d = bridge.follow("cheat", "card", "BODYGUARD")
    bodyguard = next(card for card in d["hand"]
                     if card["model"] == "BODYGUARD")
    d = bridge.follow("play", bodyguard.get("selector") or "BODYGUARD")
    osty = d["you"]["osty"]
    assert osty["model"] == "OSTY" and osty["title"] is None, osty
    assert osty["hp"] == [6, 6] and osty["block"] == 0, osty
    assert osty["alive"] is True and isinstance(osty["powers"], list), osty

    full = run("obs")["you"]["osty"]
    assert full["title"] and all(power["description"]
                                 for power in full["powers"]), full

    compact = run("obs", "--compact")["you"]["osty"]
    assert compact["hp"] == osty["hp"] and compact["block"] == osty["block"], \
        compact
    assert compact["alive"] is True and compact["title"] is None, compact
    assert all(power.get("description") is None for power in compact["powers"]), \
        compact

    # Give Osty enough health to survive a real enemy attack, then compare
    # the structured ledger with Unleash's Osty-dependent preview.
    bridge.follow("cheat", "energy", "99")
    for _ in range(4):
        d = bridge.follow("cheat", "card", "BODYGUARD")
        bodyguard = next(card for card in d["hand"]
                         if card["model"] == "BODYGUARD")
        d = bridge.follow("play", bodyguard.get("selector") or "BODYGUARD")
    grown = d["you"]["osty"]
    assert grown["hp"] == [26, 26], grown

    d = bridge.follow("cheat", "card", "UNLEASH")
    unleash = next(card for card in d["hand"] if card["model"] == "UNLEASH")
    preview_before_damage = unleash["vars"]["CalculatedDamage"]
    player_hp_before_damage = d["you"]["hp"][0]

    damaged = None
    for _ in range(6):
        d = bridge.follow("end-turn", timeout_ms=30000)
        if not (d.get("phase") == PHASE.COMBAT and d.get("side") == "player"):
            d = bridge.wait_until(
                lambda snapshot: snapshot.get("phase") == PHASE.COMBAT
                and snapshot.get("side") == "player",
                timeout=30,
                description="Osty damage absorption player turn",
                after_rev=d.get("rev"),
            )
        candidate = d["you"]["osty"]
        if candidate["alive"] and candidate["hp"][0] < grown["hp"][0]:
            damaged = candidate
            break
    assert damaged is not None, d
    assert d["you"]["hp"][0] == player_hp_before_damage, d["you"]

    d = bridge.follow("cheat", "card", "UNLEASH")
    unleash = next(card for card in d["hand"] if card["model"] == "UNLEASH")
    preview_after_damage = unleash["vars"]["CalculatedDamage"]
    assert preview_after_damage < preview_before_damage, unleash

    before_heal = damaged["hp"]
    bridge.follow("cheat", "energy", "99")
    d = bridge.follow("cheat", "card", "SPUR")
    spur = next(card for card in d["hand"] if card["model"] == "SPUR")
    d = bridge.follow("play", spur.get("selector") or "SPUR")
    healed = d["you"]["osty"]
    assert healed["hp"][0] > before_heal[0], healed
    assert healed["hp"][1] == before_heal[1] + 3, healed
    healed_fingerprint = latest_runlog_entry("play")["fingerprint"]

    d = bridge.follow("cheat", "card", "SACRIFICE")
    sacrifice = next(card for card in d["hand"]
                     if card["model"] == "SACRIFICE")
    d = bridge.follow("play", sacrifice.get("selector") or "SACRIFICE")
    dead = d["you"]["osty"]
    assert dead["hp"][0] == 0 and dead["alive"] is False, dead
    death_fingerprint = latest_runlog_entry("play")["fingerprint"]
    assert healed_fingerprint != death_fingerprint, \
        (healed_fingerprint, death_fingerprint)

    d = bridge.follow("cheat", "card", "UNLEASH")
    unleash = next(card for card in d["hand"] if card["model"] == "UNLEASH")
    assert unleash["vars"]["CalculatedDamage"] \
        == unleash["vars"]["CalculationBase"], unleash
    to_menu()


@case("K5 Lizard Tail revival never publishes a transient game_over")
def k5():
    def settle_to_player_or_terminal(snapshot, event_log):
        if snapshot.get("phase") == PHASE.GAME_OVER or (
                snapshot.get("phase") == PHASE.COMBAT
                and snapshot.get("side") == "player"):
            return snapshot

        def record(observation):
            event_log.extend(observation.get("events", []))

        return bridge.wait_until(
            lambda observation: observation.get("phase") == PHASE.GAME_OVER
            or (observation.get("phase") == PHASE.COMBAT
                and observation.get("side") == "player"),
            timeout=30,
            description="next combat decision or terminal outcome",
            on_obs=record,
            after_rev=snapshot.get("rev"),
        )

    d = to_map(seed="CIREVIVAL")
    d = bridge.follow("cheat", "relic", "LIZARD_TAIL")
    tail = next(relic for relic in d["player"]["relicStates"]
                if relic["model"] == "LIZARD_TAIL")
    assert tail["usedUp"] is False, tail

    monster = next(node for node in d["next"] if node["type"] == "monster")
    run("map-move", str(monster["col"]), str(monster["row"]))
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") == PHASE.COMBAT
        and snapshot.get("side") == "player",
        timeout=30,
        description="Lizard Tail test combat",
    )
    combat_tail = next(relic for relic in d["you"]["relics"]
                       if relic["model"] == "LIZARD_TAIL")
    assert combat_tail["usedUp"] is False, combat_tail

    first_death = None
    first_events = []
    for _ in range(20):
        d = bridge.follow("cheat", "hp", "1")
        first_death = run("end-turn", "--follow", "30000")
        first_events.extend(first_death["events"])
        d = first_death["obs"]
        d = settle_to_player_or_terminal(d, first_events)
        assert d["phase"] != PHASE.GAME_OVER, d
        assert not any("game_over" in event["type"]
                       for event in first_events), first_events
        combat_tail = next(relic for relic in d["you"]["relics"]
                           if relic["model"] == "LIZARD_TAIL")
        if combat_tail["usedUp"]:
            break
    assert first_death is not None and combat_tail["usedUp"] is True, d
    assert d["phase"] == PHASE.COMBAT and d["you"]["hp"][0] > 0, d
    revival_fingerprint = latest_runlog_entry("end-turn")["fingerprint"]
    assert revival_fingerprint, latest_runlog_entry("end-turn")

    second_death = None
    game_over_events = []
    for _ in range(20):
        d = bridge.follow("cheat", "hp", "1")
        second_death = run("end-turn", "--follow", "30000")
        game_over_events.extend(
            event for event in second_death["events"]
            if "game_over" in event["type"]
        )
        d = second_death["obs"]
        later_events = []
        d = settle_to_player_or_terminal(d, later_events)
        game_over_events.extend(
            event for event in later_events if "game_over" in event["type"]
        )
        if d["phase"] == PHASE.GAME_OVER:
            break
    assert second_death is not None and d["phase"] == PHASE.GAME_OVER, d
    assert d["outcome"] == "defeat", d
    defeat_fingerprint = latest_runlog_entry("end-turn")["fingerprint"]
    assert defeat_fingerprint and defeat_fingerprint != revival_fingerprint, \
        (revival_fingerprint, defeat_fingerprint)
    game_over_event_ids = {
        (event["rev"], event["type"]) for event in game_over_events
    }
    assert len(game_over_event_ids) == 1, game_over_events

    stable = run("obs", "--since", str(d["rev"]), "--wait", "500")
    assert stable.get("changed") is False and stable.get("events") == [], stable
    to_menu()

@case("C4 DECIMILLIPEDE last segment dies to end-turn Lightning")
def c4():
    # Exact #67/#68 regression: ReattachPower owns the segmented death
    # flow, and the final lethal lands inside EndPlayerTurnAction rather
    # than a played card.
    to_map(seed="CIC2MILLI", character="DEFECT")
    run("cheat", PHASE.COMBAT, "DECIMILLIPEDE_ELITE")
    d = bridge.wait_phase(PHASE.COMBAT, timeout=30)
    assert len(d.get("enemies", [])) >= 3, d.get("enemies")
    assert all("MILLIPEDE" in (e.get("model") or "") for e in d["enemies"]), d["enemies"]
    d = bridge.follow("cheat", "wound-enemies")

    while True:
        d = obs()
        alive = [e for e in d["enemies"] if e["alive"]]
        if len(alive) <= 1:
            break
        bridge.follow("cheat", "card", "STRIKE_DEFECT")
        bridge.follow("cheat", "energy", "99")
        before_rev = d["rev"]
        alive_count = len(alive)
        run("play", "STRIKE_DEFECT", "--target", str(alive[0]["id"]))
        d = bridge.wait_until(
            lambda snapshot: snapshot.get("phase") != PHASE.COMBAT
            or sum(enemy.get("alive", False)
                   for enemy in snapshot.get("enemies", [])) < alive_count,
            description="millipede segment to die",
            after_rev=before_rev,
        )
        assert d["phase"] == PHASE.COMBAT, \
            "fight ended before the orb-passive lethal"

    assert len(alive) == 1 and alive[0]["hp"][0] == 1, alive
    result = run("end-turn", "--follow", "25000", timeout=30)
    assert result.get("ok") is True and result.get("errors") == [], result
    assert result.get("observationAvailable") is True, result
    assert result.get("obs", {}).get("phase") == PHASE.REWARDS, result
    status, health = http("GET", "/health")
    assert status == 200 and all(q["depth"] == 0 for q in health["queues"]), health
    assert health["executorStuckMs"] < 8000, health
    run("proceed")
    bridge.wait_phase(PHASE.MAP)
    to_menu()


@case("C5 facing/back-attack fields track a surround fight")
def c5():
    to_map(seed="CIC3CRAB", character="DEFECT")
    run("cheat", PHASE.COMBAT, "KAISER_CRAB_BOSS")
    d = bridge.wait_phase(PHASE.COMBAT, timeout=30)

    assert {e["model"] for e in d["enemies"]} == {"CRUSHER", "ROCKET"}, d["enemies"]
    assert d["you"]["facing"] in ("left", "right"), d["you"]
    assert {e["side"] for e in d["enemies"]} == {"left", "right"}, d["enemies"]
    behind = [e for e in d["enemies"] if e["isBehind"]]
    assert len(behind) == 1 and behind[0]["side"] != d["you"]["facing"], d

    attacks = [i for e in d["enemies"] for i in e["intents"]
               if i.get("damage") is not None]
    assert attacks and all(i.get("baseDamage") is not None for i in attacks), attacks
    assert any(i["damage"] != i["baseDamage"] for i in attacks), attacks

    target = behind[0]
    bridge.follow("cheat", "card", "STRIKE_DEFECT")
    bridge.follow("cheat", "energy", "99")
    after = bridge.follow(
        "play", "STRIKE_DEFECT", "--target", str(target["id"]))
    assert after["you"]["facing"] == target["side"], after["you"]
    after_behind = [e for e in after["enemies"] if e["isBehind"]]
    assert len(after_behind) == 1 and after_behind[0]["side"] != target["side"], after
    to_menu()


@case("C6 an active hand picker retains exclusive combat input")
def c6():
    # Regression for #31. A delayed picker owns the player-choice context;
    # accepting another picker-opening combat action here overwrites the
    # first completion source and crosses the engine's context stack.
    into_combat(seed="CINESTEDPICK", character="SILENT")
    bridge.follow("cheat", "card", "TOOLS_OF_THE_TRADE")
    procured = bridge.follow("cheat", "potion", "FLEX_POTION")
    potion_slot = procured["potions"][0]["slot"]
    bridge.follow("cheat", "energy", "99")
    bridge.follow("play", "TOOLS_OF_THE_TRADE")
    run("end-turn")
    first = bridge.wait_phase(PHASE.HAND_SELECT, timeout=25)
    assert first.get("cards"), first

    bridge.follow("cheat", "card", "ARMAMENTS")
    bridge.follow("cheat", "energy", "99")
    err = reject(["play", "ARMAMENTS"], REJECTION.BAD_PHASE)
    assert PHASE.HAND_SELECT in err, err
    err = reject(["potion-discard", str(potion_slot)], REJECTION.BAD_PHASE)
    assert PHASE.HAND_SELECT in err, err

    still_first = obs()
    assert still_first["phase"] == PHASE.HAND_SELECT, still_first
    assert [c["model"] for c in still_first["cards"]] == [
        c["model"] for c in first["cards"]
    ], (first, still_first)
    before_rev = still_first["rev"]
    run("pick-card", str(still_first["cards"][0]["idx"]))
    bridge.wait_phase(PHASE.COMBAT, after_rev=before_rev)
    to_menu()


@case("C7 a victory teardown cannot poison the next combat")
def c7():
    # Regression for the M1 batch failure: Soul Nexus ends inside an
    # EndPlayerTurnAction whose victory cleanup clears the queue before the
    # action pops itself. The next combat must not re-observe that stale task.
    to_map(seed="CIPAIRTEARDOWN")
    for encounter in ("SOUL_NEXUS_ELITE", "SPINY_TOAD_NORMAL"):
        settled = bridge.walk_world(PHASE.MAP, **WORLD_CLAIMS)
        assert settled["phase"] == PHASE.MAP, \
            f"run ended while walking to map: {settled['phase']}"
        run("cheat", PHASE.COMBAT, encounter)
        combat = bridge.wait_phase(
            PHASE.COMBAT, timeout=20, raise_on_timeout=False)
        assert combat is not None and combat.get("enemies"), (
            encounter, obs())
        bridge.kill_current_combat()
        settled = bridge.walk_world(PHASE.MAP, **WORLD_CLAIMS)
        assert settled["phase"] == PHASE.MAP, \
            f"run ended while walking to map: {settled['phase']}"
    to_menu()


@case("C8 an incomplete selection is fatal, not transient")
def c8():
    d = to_map(seed="CIBADSTATE")
    rest = next(point for point in d["graph"] if point["type"] == "restsite")
    run("cheat", "goto", str(rest["col"]), str(rest["row"]))
    d = bridge.wait_phase(PHASE.REST_SITE)
    smith = next(option for option in d["options"]
                 if "smith" in option["id"].lower() and option["enabled"])
    run("option", str(smith["idx"]))
    bridge.wait_phase(PHASE.CARD_SELECT)

    rejected = bridge.cli("confirm")
    assert rejected.returncode == 1, rejected
    assert "spirescry: bad_state:" in rejected.stderr, rejected.stderr
    to_menu()


@case("C9 upgraded same-model card can be played precisely")
def c9():
    into_combat(seed="CIUPGRADEDPLAY")
    bridge.follow("cheat", "card", "STRIKE_IRONCLAD")
    bridge.follow("cheat", "card-upgraded", "STRIKE_IRONCLAD")
    d = bridge.follow("cheat", "card-upgraded", "STRIKE_IRONCLAD")
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
    assert status == 400 and result.get("err") == REJECTION.BAD_REQUEST, result
    assert copies(obs(), True) == upgraded_before, "malformed target played a card"

    status, result = http("POST", "/step", {
        "action": "play",
        "args": {"model": "STRIKE_IRONCLAD+", "target": target},
        "follow": 5000,
    })
    d = followed_http_obs(status, result, "upgraded card play")
    assert copies(d, True) == upgraded_before - 1, "upgraded copy stayed in hand"
    assert copies(d, False) == base_before, "MODEL+ played an unupgraded copy"

    status, result = http("POST", "/step", {
        "action": "play",
        "args": {"model": "STRIKE_IRONCLAD", "target": target},
        "follow": 5000,
    })
    d = followed_http_obs(status, result, "base card play")
    assert copies(d, False) == base_before - 1, "base MODEL did not play a base copy"
    assert copies(d, True) == upgraded_before - 1, "base MODEL played an upgraded copy"
    to_menu()


@case("C10 power descriptions track their live amounts")
def c10():
    launch(character="REGENT", seed="CIPOWERDESC")
    run("proceed")
    d = bridge.wait_phase(PHASE.MAP)
    node = next(p for p in d["next"] if p["type"] == "monster")
    run("map-move", str(node["col"]), str(node["row"]))
    bridge.wait_phase(PHASE.COMBAT)

    def power(model):
        return next(p for p in obs()["you"]["powers"] if p["id"] == model)

    def next_turn():
        before_turn = obs()["turn"]
        bridge.follow("cheat", "heal")
        run("end-turn")
        bridge.wait_until(
            lambda snapshot: snapshot.get("phase") == PHASE.COMBAT
            and snapshot.get("side") == "player"
            and snapshot.get("turn", before_turn) > before_turn,
            timeout=8,
            description="next player turn",
        )

    bridge.follow("cheat", "card-upgraded", "BLACK_HOLE")
    bridge.follow("play", "BLACK_HOLE+")
    black_hole = power("BLACK_HOLE_POWER")
    assert black_hole["amount"] == 4, black_hole
    assert "[blue]4[/blue]" in black_hole["description"], black_hole

    next_turn()
    bridge.follow("cheat", "card-upgraded", "ROYALTIES")
    bridge.follow("play", "ROYALTIES+")
    royalties = power("ROYALTIES_POWER")
    assert royalties["amount"] > 25, royalties
    assert f"[blue]{royalties['amount']}[/blue]" in royalties["description"], royalties

    next_turn()
    bridge.follow("cheat", "card", "RUPTURE")
    bridge.follow("play", "RUPTURE")
    first = power("RUPTURE_POWER")
    assert first["amount"] == 1 and "[blue]1[/blue]" in first["description"], first
    next_turn()
    bridge.follow("cheat", "card", "RUPTURE")
    bridge.follow("play", "RUPTURE")
    stacked = power("RUPTURE_POWER")
    assert stacked["amount"] == 2 and "[blue]2[/blue]" in stacked["description"], stacked
    to_menu()


@case("C11 monster smart power descriptions include their owner")
def c11():
    d = to_map(seed="CIPLOW")
    boss = next(p for p in d["graph"] if p["type"] == "boss")
    run("cheat", "goto", str(boss["col"]), str(boss["row"]), allow_fail=True)
    d = bridge.wait_phase(PHASE.COMBAT, timeout=30)
    owner = next((enemy for enemy in d["enemies"]
                  if any(power["id"] == "SLIPPERY_POWER" for power in enemy["powers"])), None)
    assert owner, f"CIPLOW no longer selects the OwnerName boss: {d['enemies']}"
    power = next(power for power in owner["powers"] if power["id"] == "SLIPPERY_POWER")
    assert owner["title"] in power["description"], (owner, power)
    assert f"[blue]{power['amount']}[/blue]" in power["description"], power
    assert "{OwnerName}" not in power["description"], power
    to_menu()


@case("C12 Queen BOUND cards expose the final hook-aware play gate")
def c12():
    d = to_map(seed="CIBOUND")
    before_rev = d["rev"]
    run("cheat", "combat", "QUEEN_BOSS")
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") == PHASE.COMBAT
        and snapshot.get("side") == "player",
        description="Queen player turn",
        after_rev=before_rev,
        timeout=30,
    )
    for _ in range(8):
        bound = [card for card in d["hand"]
                 if card.get("affliction") == "BOUND"]
        if len(bound) == 3:
            break
        bridge.follow("cheat", "heal")
        turn = d["turn"]
        before_rev = d["rev"]
        run("end-turn")
        d = bridge.wait_until(
            lambda snapshot: snapshot.get("phase") != PHASE.COMBAT
            or (snapshot.get("side") == "player"
                and snapshot.get("turn", turn) > turn),
            description="Queen next player turn",
            after_rev=before_rev,
            timeout=30,
        )
    else:
        bound = []
    assert len(bound) == 3, f"Queen did not bind the first three draws: {d['hand']}"

    first = next((card for card in bound if card.get("playable")), None)
    assert first, f"no first BOUND play was available: {bound}"
    args = ["play", first["selector"]]
    if first["target"] == "anyenemy":
        args += ["--target", str(alive_enemy(d)["id"])]
    before_rev = d["rev"]
    copies = sum(card.get("selector") == first["selector"] for card in d["hand"])
    run(*args)
    d = bridge.wait_until(
        lambda snapshot: snapshot.get("phase") != PHASE.COMBAT
        or sum(card.get("selector") == first["selector"]
               for card in snapshot.get("hand", [])) < copies,
        description="first BOUND card to leave the hand",
        after_rev=before_rev,
    )

    remaining = [card for card in d["hand"]
                 if card.get("affliction") == "BOUND"]
    assert len(remaining) == 2, remaining
    for card in remaining:
        assert card["playable"] is False, card
        assert card["unplayableReason"] == "BlockedByHook", card
        assert card["unplayablePreventer"] == "CHAINS_OF_BINDING_POWER", card
    assert any(card.get("playable")
               for card in d["hand"] if card.get("affliction") != "BOUND"), d["hand"]
    decision = run("obs", "--decision")
    assert "play" in decision["legal"], decision["legal"]
    reject(["play", remaining[0]["selector"]], REJECTION.NOT_PLAYABLE)
    to_menu()


@case("C13 lethal played card crosses power teardown cleanly")
def c13():
    into_combat(seed="CISEMTEAR")
    bridge.follow("cheat", "card", "RUPTURE")
    bridge.follow("cheat", "energy", "99")
    powered = bridge.follow("play", "RUPTURE")
    assert any(power["id"] == "RUPTURE_POWER"
               for power in powered["you"]["powers"]), powered["you"]
    normal_decision = run("obs", "--decision")
    assert "semanticState" not in json.dumps(normal_decision), normal_decision
    diagnostic = run("obs", "--decision", "--semantic-state")
    assert diagnostic.get("semanticState"), diagnostic
    assert any(card.get("semanticState") for card in diagnostic["hand"]), \
        diagnostic["hand"]

    wounded = bridge.follow("cheat", "wound-enemies")
    bridge.follow("cheat", "energy", "99")
    attack = next(card for card in wounded["hand"]
                  if card.get("playable") and card.get("target") == "anyenemy")
    target = alive_enemy(wounded)["id"]
    result = run(
        "play", attack["selector"], "--target", str(target),
        "--follow", "25000", timeout=30)

    assert result.get("outcome") in ("settled", "next_decision"), result
    assert result.get("errors") == [], result
    assert result.get("observationAvailable") is True, result
    assert result.get("obs", {}).get("phase") == PHASE.REWARDS, result
    assert "semanticState" not in json.dumps(result["obs"]), result["obs"]
    to_menu()


# ---------- V: victory ----------

@case("V1 cheat-driven full clear reaches a victory game_over")
def v1():
    launch(seed="CIVICT")
    run("proceed")
    bridge.wait_phase(PHASE.MAP)
    for _ in range(8):  # acts; the loop exits on game_over
        d = obs()
        if d["phase"] != PHASE.MAP:
            break
        boss = next(p for p in d["graph"] if p["type"] == "boss")
        run("cheat", "goto", str(boss["col"]), str(boss["row"]), allow_fail=True)
        bridge.wait_phase(PHASE.COMBAT, timeout=30)
        bridge.kill_current_combat()
        d = bridge.walk_world(PHASE.MAP, PHASE.GAME_OVER, **VICTORY_CLAIMS)
        if d["phase"] == PHASE.GAME_OVER:
            break
    d = obs()
    assert d["phase"] == PHASE.GAME_OVER, f"never reached game_over: {d['phase']}"
    assert d.get("outcome") == "victory", f"outcome: {d.get('outcome')}"
    assert d.get("seed") == "CIVICT", d.get("seed")
    assert d.get("actNumber", 0) >= 3, \
        f"won suspiciously early: act {d.get('actNumber')}"
    print(f"    victory at act {d['actNumber']} floor {d['actFloor']}")
    to_menu()


# ---------- E: events ----------

@case("E1 every event responds (every option unless --quick)")
def e1():
    args = []
    if not ARGS.quick:
        args.append("--all-options")
    run_test_script("eventsweep.py", *args)


@case("E2 amalgamator combine grants the ultimate defend it promises")
def e2():
    # Regression for the half-executed event effect: NGame.Instance is
    # null headless and CombineDefends screen-shakes between removing the
    # two Defends and granting the Ultimate Defend — unimmunized, the NRE
    # aborted there and the player paid two cards for nothing (the
    # follow guard also asserts the fault no longer fires at all).
    models0 = open_amalgamator_picker()
    run("pick-card", "0", "--follow", "5000")
    done = run("pick-card", "1", "--follow", "5000")  # max picks auto-resolve

    # The option task is tracked through settlement: the engine-side
    # Task.Delay between removing the Defends and granting the reward
    # counts as Busy, so THIS response must already carry the completed
    # effect — no post-hoc polling. (Regression for delayed engine work
    # escaping the follow window; the follow obs deck is the compact
    # counts-by-specifier dict.)
    deck_after = done["obs"]["player"]["deck"]
    assert any(key.startswith("ULTIMATE_DEFEND") for key in deck_after), deck_after

    models = [c["model"] for c in obs()["player"]["deck"]]
    assert models.count("DEFEND_IRONCLAD") == models0.count("DEFEND_IRONCLAD") - 2, \
        (models0, models)
    to_menu()


@case("E4 tasks appearing outside the event phase are still swept")
def e4():
    # A delivered Chosen() can synchronously open a picker before the
    # sweep's next look — the sweep must find tasks by list state, not
    # by the visible phase. Park the Amalgamator's combine picker
    # (phase card_select), inject the fault task there, then resolve the
    # picks: the final follow must span both the combine's own delay and
    # the injected fault, and report both effects.
    open_amalgamator_picker()

    run("cheat", "event-fault-delayed", allow_errors=True)  # injected mid-picker
    first = run("pick-card", "0", "--follow", "5000", allow_errors=True)
    done = run("pick-card", "1", "--follow", "8000", allow_errors=True)

    # The async_fault:event-option prefix exists only for swept/tracked
    # tasks — its presence in either pick window proves the sweep found
    # the task despite the non-event phase (window attribution precision
    # is P14's job; the fault's 250ms timer races the two picks).
    seen = (first.get("errors") or []) + (done.get("errors") or [])
    assert any(
        error.startswith("async_fault:event-option:")
        and "delayed event-option failure" in error
        for error in seen
    ), seen
    deck_after = done["obs"]["player"]["deck"]
    assert any(key.startswith("ULTIMATE_DEFEND") for key in deck_after), deck_after
    to_menu()


@case("E3 trial double-down genuinely abandons the run")
def e3():
    # The confirm popup can't exist headless, so the host reroutes
    # DoubleDown onto the popup's accepted action (the screen-free
    # abandon teardown). The generic sweep can't tell that from an inert
    # swallow — this asserts the real outcome: run over, cleanly.
    to_map(seed="CITRIALDD")
    run("cheat", "event", "TRIAL")
    bridge.wait_phase("event")
    run("option", "1", "--follow", "5000")  # Reject → the double-down page
    down = bridge.wait_phase("event")
    idx = next(o["idx"] for o in down["options"]
               if "double" in (o.get("title") or "").lower())
    ended = run("option", str(idx), "--follow", "8000", allow_errors=True)
    assert ended["errors"] == [], ended["errors"]
    assert ended["obs"]["phase"] == "game_over", ended["obs"]["phase"]
    assert ended["obs"]["outcome"] == "abandoned", ended["obs"]
    to_menu()


# ---------- M: exhaustive content sweeps ----------

@case("M1 bestiary: every encounter loads, fights, resolves", deep=True)
def m1():
    run_test_script("sweeps.py", "encounters")


@case("M2 every playable card executes; legality rejects cleanly", deep=True)
def m2():
    run_test_script("sweeps.py", "cards")


@case("M3 every potion procures and drinks", deep=True)
def m3():
    run_test_script("sweeps.py", "potions")


@case("M4 every legal relic obtain hook lands", deep=True)
def m4():
    run_test_script("sweeps.py", "relics")


@case("M5 delayed card effects expose their picker")
def m5():
    # TOOLS_OF_THE_TRADE asks for a discard at the start of the NEXT
    # player turn, outside the play verb's HeadlessPicker.Around scope.
    # The pure host must still expose that automatic choice rather than
    # falling through to the absent GUI screen.
    d = into_combat(seed="CIM5", character="SILENT")
    assert d.get("side") == "player", f"never got player turn: {d.get('side')}"
    bridge.follow("cheat", "card", "TOOLS_OF_THE_TRADE")
    bridge.follow("cheat", "energy", "99")
    bridge.follow("play", "TOOLS_OF_THE_TRADE")
    run("end-turn")
    pick = bridge.wait_phase(PHASE.HAND_SELECT, timeout=25)
    assert pick.get("min") == 1 and pick.get("cards"), pick
    before_rev = pick["rev"]
    run("pick-card", str(pick["cards"][0]["idx"]))
    bridge.wait_phase(PHASE.COMBAT, after_rev=before_rev)
    to_menu()


# ---------- F: the full loop ----------

@case("F1 act-1 parity loop")
def f1():
    # Pinned seed: the pre-merge run wants the same map/shop/boss every
    # time. Re-pin via env after a game update reshuffles what the seed
    # generates.
    args = ["--seed", os.environ.get("SPIRESCRY_PARITY_SEED", PARITY_SEED)]
    if ARGS.keys_out:
        args.extend(["--keys-out", ARGS.keys_out])
    run_test_script("parity.py", *args)


# ---------- H: audit trail ----------

@case("H1 request audit trail is on and well-formed", boot_only=True)
def h1():
    text = host_log()
    assert re.search(r"http POST /step new-run \S 200 rev \d+\S\d+ \d+ms", text), \
        "no new-run audit line (STS2_AGENT_HTTP_LOG not honored?)"
    assert re.search(r"http GET /obs\?since=\d+", text), \
        "no long-poll audit line"


# ---------- I: information exposure ----------

@case("I1 event snapshots are read-only")
def i1():
    to_map(seed="CIEVENTREAD")
    bridge.follow("cheat", "gold", "500")
    run("cheat", PHASE.EVENT, "LOST_WISP")
    first = bridge.wait_phase(PHASE.EVENT)
    second = obs()
    assert first["options"] == second["options"], \
        f"/obs mutated event options: {first['options']} -> {second['options']}"
    assert first["description"] == second["description"], \
        f"/obs mutated event page: {first['description']} -> {second['description']}"
    to_menu()


@case("I2 event options expose GUI hover-tip decisions")
def i2():
    to_map(seed="CIEVENTTIPS")
    run("cheat", PHASE.EVENT, "DOLL_ROOM")
    bridge.wait_phase(PHASE.EVENT)
    d = bridge.follow("option", "1")
    assert d.get("phase") == PHASE.EVENT, d
    assert len(d["options"]) == 2, d["options"]
    for option in d["options"]:
        hints = option.get("hints")
        assert hints and any(h.get("description") for h in hints), option
        assert any(h.get("model") for h in hints), option
        assert all(isinstance(h.get("title"), str) for h in hints), hints
    to_menu()


@case("I3 lethal event choices are explicit")
def i3():
    to_map(seed="CIEVENTLETHAL")
    bridge.follow("cheat", "hp", "1")
    run("cheat", PHASE.EVENT, "BRAIN_LEECH")
    d = bridge.wait_phase(PHASE.EVENT)
    rip = next(o for o in d["options"] if o["title"] == "Rip the Leech Off")
    assert rip.get("lethal") is True, rip
    to_menu()


@case("I4 event page conditionals match GUI rendering")
def i4():
    to_map(seed="CIEVENTTEXT")
    run("cheat", PHASE.EVENT, "JUNGLE_MAZE_ADVENTURE")
    bridge.wait_phase(PHASE.EVENT)
    d = bridge.follow("option", "0")
    assert d.get("phase") == PHASE.EVENT, d
    assert "{IsMultiplayer:" not in d["description"], d["description"]
    to_menu()


@case("I5 fake merchant inventory is visible and buyable")
def i5():
    to_map(seed="CIFAKESHOP")
    bridge.follow("cheat", "gold", "500")
    run("cheat", PHASE.EVENT, "FAKE_MERCHANT")
    d = bridge.wait_phase(PHASE.EVENT)
    shop = d.get("fakeMerchant")
    assert shop and len(shop["relics"]) == 6, d
    first = shop["relics"][0]
    assert first["model"] and first["description"] and first["stocked"], first
    assert first["price"] == first["cost"] and first["price"] > 0, first
    before_gold = d["player"]["gold"]
    before_rev = d["rev"]
    run("buy", "relic", "--idx", "0")
    d = bridge.wait_until(
        lambda snapshot: not snapshot["fakeMerchant"]["relics"][0]["stocked"],
        description="fake merchant relic to sell",
        after_rev=before_rev,
    )
    assert not d["fakeMerchant"]["relics"][0]["stocked"], d["fakeMerchant"]
    assert d["player"]["gold"] < before_gold, d["player"]
    assert first["model"] in d["player"]["relics"], d["player"]["relics"]
    to_menu()


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
    ap.add_argument("--quick", action="store_true",
                    help="skip the exhaustive sweeps (M*), first-option E1")
    ap.add_argument("--only", help="comma-separated case-name prefixes")
    ap.add_argument("--keys-out")
    ap.add_argument("--log", default=os.path.join(
        os.environ.get("TMPDIR", "/tmp"), "spirescry-ci-host.log"))
    ap.add_argument("--list", action="store_true")
    ARGS = ap.parse_args()

    if ARGS.list:
        for name, boot_only, deep, _ in CASES:
            print(name + ("  (--boot only)" if boot_only else "")
                  + ("  (skipped by --quick)" if deep else ""))
        return 0

    if ARGS.boot:
        configure_cli_for_boot()

    proc = None
    if ARGS.boot:
        LOG_PATH = ARGS.log
        proc = boot_host(LOG_PATH)

    only = [p.strip() for p in ARGS.only.split(",")] if ARGS.only else None
    failures = []
    try:
        for name, boot_only, deep, fn in CASES:
            if only and not any(name.startswith(p) for p in only):
                continue
            if boot_only and not ARGS.boot:
                print(f"SKIP {name} (needs --boot)")
                continue
            if deep and ARGS.quick:
                print(f"SKIP {name} (--quick)")
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
    finally:
        if proc:
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()

    ran = sum(1 for name, b, deep, _ in CASES
              if (not only or any(name.startswith(p) for p in only))
              and (ARGS.boot or not b) and not (deep and ARGS.quick))
    print(f"\n{ran - len(failures)}/{ran} cases passed"
          + (f"; FAILED: {failures}" if failures else ""))
    if failures and LOG_PATH:
        print(f"host log: {LOG_PATH}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
