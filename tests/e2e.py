#!/usr/bin/env python3
"""Pre-merge end-to-end suite for the spirescry bridge.

Local, not CI — the host is built from the game's own dlls, so this
runs where the game is installed. Run it (--boot) before merging.

Everything drives the pure .NET host (the dll boot: no game binary,
no Steam). Case groups:

  B  boot: /health shape, boot-log assertions (Harmony patches landed,
     ModelDb registered clean, timestamped lines)
  P  protocol: rev monotonicity, long-poll semantics, route/verb/cheat
     rejections with actionable codes, follow windows scoped to the run
     that accepted the verb
  R  run lifecycle: seeded determinism, every character boots + fights,
     abandon from map and mid-combat (regression: #66)
  C  combat economy: block, energy accounting, bad_target, overdraw
  S  shop: every buy kind with gold accounting, removal picker, leave
  W  skip: card reward + treasure walk away without granting
  X  special screens: crystal-sphere minigame, Neow bundle select
  K  cheat surface: gold / relic / card-upgraded / card graft real state
  L  localized item titles: cards, relics, and potions follow host language
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
  e2e.py --quick          LOCAL ITERATION ONLY: skip the M sweeps, E1
                          clicks first options only. `./build.sh gate`
                          never passes it — the gate runs everything, and
                          tests/gate_coverage_test.py holds it to that.
  e2e.py --only P1,M2     run a subset: an exact case id selects that one
                          case, anything else is a case-name prefix (--only M
                          runs the M family)
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
import threading
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
    SETTLEMENT_OUTCOME,
)

run, obs = bridge.run, bridge.obs

CASES = []
LOG_PATH = None  # set in main() when --boot
# The pinned parity seed (see F1): full path coverage — shop with
# potions (one opens a mid-combat picker in the boss fight), treasure,
# smith. SPIRECI2/SPIRECI3 also pass, with less potion coverage.
PARITY_SEED = "SPIRECI1"
# MerchantCardRemovalEntry.PriceIncrease off ascension: every removal a run
# has already used adds this much to the next merchant's asking price.
REMOVAL_PRICE_INCREASE = 25
WORLD_CLAIMS = {
    "claim_reward_tiles": True,
    "claim_card_reward": True,
    "claim_relic_reward": True,
}
VICTORY_CLAIMS = {
    "claim_reward_tiles": True,
    "claim_relic_reward": True,
}


def case_id(name):
    """The leading token of a case name — its stable handle (`--only P14`,
    the id cited in issues and commit messages)."""
    return name.split(" ", 1)[0]


def case(name, boot_only=False, deep=False):
    """Register a case. Ids are unique: a repeat is a collision between two
    independently added cases, and it silently makes both unaddressable by
    `--only`, so it fails at import rather than at integration."""
    def deco(fn):
        new = case_id(name)
        for existing, _, _, _ in CASES:
            if case_id(existing) == new:
                raise ValueError(
                    f"duplicate e2e case id {new!r}:\n"
                    f"  already registered: {existing}\n"
                    f"  newly registered:   {name}\n"
                    "Case ids are handles (--only, issues, commits) and must "
                    "be unique — give the new case a free id.")
        CASES.append((name, boot_only, deep, fn))
        return fn
    return deco


def selects(name, only):
    """Does a `--only` selection (a list of patterns, or None for all) pick
    this case? A pattern that is exactly some case's id picks that one case
    and nothing else, so every id addresses exactly one case; anything else
    is a case-name prefix, which is how a family (`--only M`) is selected."""
    if not only:
        return True
    ids = {case_id(existing) for existing, _, _, _ in CASES}
    return any(case_id(name) == p if p in ids else name.startswith(p)
               for p in only)


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


def legal():
    """The verbs the current observation advertises. Only the decision
    projection carries them, so a legal-verb assertion needs its own read."""
    return run("obs", "--decision")["legal"]


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


def leave_opening_event():
    """Answer Neow's blessing and return the whole map board.

    `proceed` is only legal once an event page owes nothing, so the opening
    boon has to be taken rather than stepped over. walk_world hands back the
    followed (compact) observation; callers here want the full snapshot.
    """
    bridge.walk_world(PHASE.MAP)
    return bridge.wait_phase(PHASE.MAP)


def to_map(seed=None, character="IRONCLAD"):
    launch(character=character, seed=seed)
    return leave_opening_event()


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


def remove_a_card_at_the_stall():
    """Buy the merchant's card removal and drive its picker to the end.

    `buy card_removal` only opens the picker — the purchase resolves when a
    card is actually picked (and confirmed, where the grid asks for it), so
    every assertion about a landed removal has to wait for that. Returns the
    shop snapshot the removal landed in.
    """
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
    return bridge.wait_phase(PHASE.SHOP)


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


@case("P19 every route answers a boolean-ok envelope")
def p19():
    # The CLI validates the envelope strictly — a body without a boolean
    # `ok`, or one whose `ok` disagrees with the status, is malformed and
    # exits 1. Snapshot bodies are assembled as raw JSON nodes, so they are
    # the ones that can silently drop the stamp.
    def envelope(method, path, body, want_ok):
        status, d = http(method, path, body)
        where = f"{method} {path}"
        assert isinstance(d, dict), f"{where} is not an object envelope: {d}"
        assert d.get("ok") is want_ok, f"{where} -> {status} ok={d.get('ok')!r}"
        assert (status < 400) is want_ok, \
            f"{where} status {status} contradicts ok={d.get('ok')!r}"
        assert ("err" in d) is not want_ok, f"{where} -> {status} {sorted(d)[:8]}"

    to_menu()
    for path, want_ok in (
            ("/health", True),
            ("/obs", True),
            ("/obs?since=0", True),
            ("/runlog", True),
            ("/models?kind=relic", True),
            ("/models?kind=bogus", False),
            ("/nope", False),
    ):
        envelope("GET", path, None, want_ok)

    # /step answers in four shapes — plain accept, accept with a note,
    # follow success, follow fault — all stamped by the one rule; these are
    # the two an e2e can steer to directly, plus a rejection.
    envelope("POST", "/step", {"action": "warp", "args": {}}, False)
    envelope("POST", "/step",
             {"action": "new-run",
              "args": {"character": "IRONCLAD", "seed": "CIP4B"}}, True)
    bridge.wait_phase(PHASE.EVENT, timeout=30)
    envelope("POST", "/step", {"action": "abandon", "args": {}, "follow": 8000},
             True)
    to_menu()


@case("P4b malformed obs query parameters are rejected")
def p4b():
    to_menu()

    # Present but malformed: rejected, never silently defaulted or clamped.
    # The valueless spellings (`?compact`, `?since`) matter most — .NET
    # files them under its query collection's null key, so a server that
    # reads QueryString["compact"] sees them as omitted.
    for query in ("?since=abc", "?since=-1", "?since=", "?since=1.0",
                  "?since", "?SINCE=abc",
                  "?wait=soon", "?wait=-1", "?wait=60001", "?wait",
                  "?compact=yes", "?compact=", "?compact",
                  "?decision=maybe", "?decision", "?since=0&decision&wait=10",
                  "?semanticState=2", "?semanticState",
                  "?compact=1&compact=0", "?since=1&since=2"):
        status, d = http("GET", "/obs" + query)
        assert status == 400 and d.get("err") == REJECTION.BAD_REQUEST, \
            f"/obs{query} -> {status} {d}"
        assert d.get("runId") == "none", d

    # Omitted: the existing defaults — no change feed, no legal projection.
    status, omitted = http("GET", "/obs")
    assert status == 200 and omitted["phase"] == PHASE.MAIN_MENU, omitted
    assert "changed" not in omitted and "legal" not in omitted, omitted

    # Both documented encodings of each boolean are accepted, and the false
    # ones land on the same snapshot shape as omitting the parameter.
    for form in ("1", "true"):
        status, on = http("GET", f"/obs?decision={form}&compact={form}")
        assert status == 200 and on.get("legal") == ["new-run"], (form, on)
    for form in ("0", "false"):
        status, off = http("GET", f"/obs?decision={form}&compact={form}")
        assert status == 200 and "legal" not in off, (form, off)

    # A valid since/wait pair still parks for the full window.
    for _ in range(2):  # one retry in case a background bump beat the timer
        cur = obs()["rev"]
        t0 = time.monotonic()
        status, parked = http("GET", f"/obs?since={cur}&wait=1200")
        took = time.monotonic() - t0
        assert status == 200, parked
        if not parked.get("changed"):
            break
    assert parked.get("changed") is False, parked.get("events")
    assert took >= 1.0, f"valid long poll returned early ({took:.2f}s)"

    # An accepted flag has to reach the snapshot, not just the status line:
    # at the menu a compact and a full snapshot are byte-identical, so the
    # accepted encodings are checked inside a run, where they differ.
    launch(seed="CIP4B")
    status, full = http("GET", "/obs?compact=0")
    assert status == 200, full
    status, small = http("GET", "/obs?compact=true")
    assert status == 200, small
    assert full["player"]["relicStates"][0]["title"], full["player"]
    assert small["player"]["relicStates"][0]["title"] is None, small["player"]
    assert "semanticState" not in full, full
    status, diagnostic = http("GET", "/obs?semanticState=1")
    assert status == 200 and diagnostic.get("semanticState"), diagnostic
    to_menu()


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

    # The guards above are checked before the verb reaches the board, which
    # is why a gated proceed still reports them. The accepted call needs a
    # verb the decision genuinely offers: Neow owes the seat a choice, so
    # `option` is the move and proceed is withheld until it is taken.
    status, d = http("POST", "/step", {
        "action": "option", "args": {"idx": 0},
        "ifRun": run_id, "ifRev": rev,
    })
    assert status == 200 and d.get("ok") is True, d
    assert d.get("runId") == run_id, d
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

    d = leave_opening_event()
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
    leave_opening_event()
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


def orphan_async_channel(release, marker, seed):
    """One release channel of the #145 fixture, over the same four timings.

    An abandoned run's tracked work reaches the next run two ways: its own
    fault, and any engine Error line it writes — the engine catches
    exceptions from fire-and-forget chains and only logs them, so that
    second shape publishes an error without ever faulting a task the mod
    holds, and then completes successfully. Both land in the same revision
    stream, error journal and follow result, so both are tested here.
    """
    launch(seed=seed + "L")

    # Baseline first: work still owned by the run that dispatched it must
    # keep reporting. Ownership suppresses orphans, not failures.
    run("cheat", "async-orphan")
    status, health = http("GET", "/health")
    assert status == 200 and health["pendingAsync"] == 1, health
    live = run("cheat", release, "--follow", "3000", allow_errors=True)
    assert live["settled"] is True, live
    assert any(marker in error for error in live["errors"]), live["errors"]

    # Park a second task, then walk away from the run that owns it. The
    # zombie must leave the pending ledger at the rotation: it is parked on a
    # run nobody is playing, and counting it as work the board owes would
    # hold the follow probe busy for every later run.
    run("cheat", "async-orphan")
    abandoned = run("abandon", "--follow", "3000")
    assert abandoned["outcome"] != "timeout", abandoned
    assert abandoned["obs"]["phase"] == PHASE.MAIN_MENU, abandoned["obs"]
    status, health = http("GET", "/health")
    assert status == 200 and health["pendingAsync"] == 0, health

    # (a) it lands at the menu, immediately before the next run — the window
    # where a published error would be attributed to the run about to start,
    # or leak into its very first follow response.
    menu_rev = obs()["rev"]
    run("cheat", release)
    time.sleep(0.6)
    quiet = run("obs", "--since", str(menu_rev), "--wait", "500")
    assert not any(marker in event["type"]
                   for event in quiet.get("events") or []), quiet
    fresh = run("new-run", "IRONCLAD", "--seed", seed + "A", "--follow", "3000")
    assert fresh["outcome"] != "timeout", fresh
    assert fresh["errors"] == [], fresh["errors"]

    # (b) it lands across new-run: released a heartbeat before the verb, it
    # resolves while the next run is being built or just after it comes up —
    # the window where a run-blind tracker hands an abandoned run's failure
    # to the run the agent is actually playing.
    run("cheat", "async-orphan")
    run("abandon", "--follow", "3000")
    handover_rev = obs()["rev"]
    run("cheat", release)
    during = run("new-run", "IRONCLAD", "--seed", seed + "B", "--follow", "3000")
    assert during["outcome"] != "timeout", during
    assert during["errors"] == [], during["errors"]
    # follow only reports what its own step accepted, so give a release that
    # outlived the handover time to land and check everything the journal
    # took since the menu — the new run is live for all of it.
    time.sleep(0.6)
    window = run("obs", "--since", str(handover_rev), "--wait", "500")
    assert not any(marker in event["type"]
                   for event in window.get("events") or []), window
    status, health = http("GET", "/health")
    assert status == 200 and health["pendingAsync"] == 0, health

    # (c) the dual: work the run-ending verb itself started. `abandon` tracks
    # the very task that clears RunState, so ownership binds it to the run it
    # is tearing down — retiring it at that rotation would report a clean
    # settle for a teardown that actually failed. Its failure must still
    # reach the error journal once the board is back at the menu.
    run("cheat", "async-orphan-ends-run")
    teardown_rev = obs()["rev"]
    # No --follow here: run-ending work is deliberately still pending across
    # its own rotation, so the probe would sit out its whole budget waiting
    # for a task this fixture only releases below.
    run("abandon")
    bridge.wait_phase(PHASE.MAIN_MENU, timeout=15)
    run("cheat", release)
    time.sleep(0.6)
    reported = run("obs", "--since", str(teardown_rev), "--wait", "500")
    assert any(marker in event["type"]
               for event in reported.get("events") or []), reported
    to_menu()


@case("P17b abandoned fire-and-forget work cannot enter the next run")
def p17b():
    # #145: ordinary dispatcher work is run-owned too. P16/P17 cover the
    # event-option channel; this covers the plain tracked-task channel, whose
    # completions reach the same revision stream, error journal and follow
    # result — and which used to publish into whatever run happened to be
    # live when a long-abandoned task finally landed.
    orphan_async_channel("async-orphan-fault",
                         "forced orphan fire-and-forget failure",
                         "CIASYNCF")
    orphan_async_channel("async-orphan-log",
                         "forced orphan fire-and-forget engine log error",
                         "CIASYNCG")


@case("P18 a followed verb never settles against another run")
def p18():
    # #144. The bridge answers requests concurrently, so a second client's
    # abandon can land while an accepted verb is still being followed. The
    # menu it leaves behind is quiet and decision-free — exactly what the
    # settlement loop reads as a boundary — so the verb used to report
    # `settled` with `runId: none`, and the run log recorded a MAIN-MENU
    # fingerprint as that verb's replayable outcome. The action's own
    # result was never observed; only an explicit owner change can say so.
    # (Landing on a different run's ID is the same rotation one beat later;
    # SettlementReportsAnOwnerChangeWhenAnotherRunTakesOver pins that half,
    # where the timing is deterministic.)
    to_menu()
    # new-run adopts the run it mints: its own follow still settles, and
    # the entry it writes is the same-run baseline asserted below.
    launched = run("new-run", "IRONCLAD", "--seed", "CIOWNERSCOPE",
                   "--follow", "8000")
    # Whether the Neow page has already mounted decides between the two
    # replayable boundaries; both belong to the run new-run just minted.
    assert launched["outcome"] in (SETTLEMENT_OUTCOME.SETTLED,
                                   SETTLEMENT_OUTCOME.NEXT_DECISION), launched
    accepted_run = launched["runId"]
    assert accepted_run != "none", launched
    bridge.wait_phase(PHASE.EVENT)
    # Parked option work holds every follow window in this run open.
    run("cheat", "event-orphan")

    followed = {}

    def follow_gold():
        followed["status"], followed["response"] = http("POST", "/step", {
            "action": "cheat", "args": {"name": "gold", "value": 77},
            "follow": 9000})

    def gold_accepted():
        return any(verb["action"] == "cheat"
                   and verb.get("args", {}).get("name") == "gold"
                   for verb in run("runlog")["verbs"])

    follower = threading.Thread(target=follow_gold)
    follower.start()
    try:
        # Acceptance precedes the follow window and is visible in the run
        # log, so wait for it instead of racing a sleep.
        deadline = time.monotonic() + 15
        while not gold_accepted():
            assert time.monotonic() < deadline, "the gold cheat was never accepted"
            time.sleep(0.05)
        abandoned = run("abandon", "--follow", "5000")
    finally:
        follower.join(60)
    assert not follower.is_alive(), "the followed verb never came back"

    status, unowned = followed["status"], followed["response"]
    assert status == 200 and unowned.get("ok") is True, unowned
    assert unowned["acceptedRunId"] == accepted_run, unowned
    assert unowned["outcome"] == SETTLEMENT_OUTCOME.OWNER_CHANGED, unowned
    assert unowned["settled"] is False, unowned
    assert unowned["runId"] != accepted_run, unowned

    # abandon owns the transition it asked for: still a real boundary.
    assert abandoned["settled"] is True, abandoned
    assert abandoned["outcome"] in (SETTLEMENT_OUTCOME.SETTLED,
                                    SETTLEMENT_OUTCOME.NEXT_DECISION), abandoned
    assert abandoned["runId"] == "none", abandoned
    assert abandoned["obs"]["phase"] == PHASE.MAIN_MENU, abandoned["obs"]

    verbs = run("runlog")["verbs"]
    gold = next(verb for verb in reversed(verbs)
                if verb["action"] == "cheat"
                and verb.get("args", {}).get("name") == "gold")
    assert gold["runId"] == accepted_run, gold
    assert gold["outcome"] == SETTLEMENT_OUTCOME.OWNER_CHANGED, gold
    assert "fingerprint" not in gold, gold
    assert "phaseAfter" not in gold, gold
    # Same-run entries keep attributing their own boundary.
    started = next(verb for verb in verbs if verb["action"] == "new-run")
    assert started["runId"] == accepted_run, started
    assert started["outcome"] == launched["outcome"], (started, launched)
    assert started.get("fingerprint"), started
    assert started["phaseAfter"] == PHASE.EVENT, started

    # Release the parked task in a fresh run (P16's shape) so the orphan
    # slot is free for the next case.
    launch(seed="CIOWNERNEXT")
    released = run("cheat", "event-orphan-fault", "--follow", "3000",
                   allow_errors=True)
    assert released["settled"] is True, released
    to_menu()


@case("P20 event economics are part of the semantic fingerprint")
def p20():
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
        d = leave_opening_event()
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
        d = leave_opening_event()
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
    # One stall, one index: the removal publishes the only idx buy accepts,
    # and a rejected index leaves the deck alone.
    assert d["cardRemoval"]["idx"] == 0, d["cardRemoval"]
    deck_before_rejects = len(d["player"]["deck"])
    for bad in ("1", "2", "7"):
        err = reject(["buy", "card_removal", "--idx", bad], REJECTION.BAD_INDEX)
        assert "one entry" in err, err
    d = obs()
    assert d["phase"] == PHASE.SHOP, \
        f"a rejected card_removal idx still opened its picker: {d['phase']}"
    assert len(d["player"]["deck"]) == deck_before_rejects, \
        "a rejected card_removal idx removed a card anyway"
    d = remove_a_card_at_the_stall()
    assert len(d["player"]["deck"]) == deck0 + 1, "removal did not shrink the deck"
    # The stall is a one-shot, and `used` is the witness that says so — S5
    # holds it to the whole sold-out contract.
    assert d["cardRemoval"]["used"] is True, d["cardRemoval"]

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


@case("S4 shop advertises exactly the actions the dispatcher accepts")
def s4():
    d = to_map(seed="CISHOP")
    shop = next(p for p in d["graph"] if p["type"] == PHASE.SHOP)
    bridge.follow("cheat", "gold", "5000")
    run("cheat", "goto", str(shop["col"]), str(shop["row"]))
    d = bridge.wait_phase(PHASE.SHOP)

    assert "buy" in legal() and "leave" in legal(), legal()
    # An empty belt has nothing to discard or redeem, however full the
    # shelf is — merchant stock is not the player's potions.
    assert d["potions"], "no potions on the shelf to confuse with the belt"
    for potion in list(d["player"]["potions"]):
        d = bridge.follow("potion-discard", str(potion["slot"]))
    assert d["player"]["potions"] == [], d["player"]["potions"]
    assert "potion-discard" not in legal(), legal()
    assert "potion-use" not in legal(), legal()

    # A belt potion is discardable in a shop, and the dispatcher agrees.
    d = bridge.follow("cheat", "potion", "ENERGY_POTION")
    assert "potion-discard" in legal(), legal()
    # ...but an ordinary potion has no merchant interaction, so potion-use
    # stays off the list and the dispatcher rejects it.
    assert "potion-use" not in legal(), legal()
    ordinary = next(p for p in d["player"]["potions"]
                    if p["model"] == "ENERGY_POTION")
    assert ordinary["playable"] is False, ordinary
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": ordinary["slot"]},
    })
    assert status == 400 and result.get("err") == REJECTION.NOT_PLAYABLE, \
        f"unadvertised potion-use was not rejected: {status} {result}"

    # The Foul Potion is the one the merchant trades for: advertised, and
    # accepted for gold.
    d = bridge.follow("cheat", "potion", "FOUL_POTION")
    assert "potion-use" in legal(), legal()
    foul = next(p for p in d["player"]["potions"] if p["model"] == "FOUL_POTION")
    assert foul["playable"] is True, foul
    gold_before = d["gold"]
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": foul["slot"]}, "follow": 5000,
    })
    d = followed_http_obs(status, result, "advertised Foul Potion redemption")
    assert d["gold"] > gold_before, (gold_before, d["gold"])
    assert "potion-use" not in legal(), \
        f"redeemed Foul Potion still advertises potion-use: {legal()}"

    # Discard the rest of the belt; potion-discard retires with it.
    for potion in list(d["player"]["potions"]):
        d = bridge.follow("potion-discard", str(potion["slot"]))
    assert d["player"]["potions"] == [], d["player"]["potions"]
    assert "potion-discard" not in legal(), legal()

    # Broke: nothing on the shelf is affordable, so buy retires too.
    d = bridge.follow("cheat", "gold", "0")
    assert not any(item.get("purchasable")
                   for key in ("cards", "colorless", "relics", "potions")
                   for item in d[key]), d
    assert d["cardRemoval"]["purchasable"] is False, d["cardRemoval"]
    assert "buy" not in legal(), legal()
    reject(["buy", "relic", "--idx", "0"], REJECTION.NOT_ENOUGH_GOLD)
    assert "leave" in legal(), legal()
    to_menu()


@case("S5 the card removal stall sells out once a removal lands")
def s5():
    # A merchant removes one card, ever. The engine flips that flag from a
    # GUI node the headless seat never mounts, so the seat compensates for
    # it — without that, gold and deck kept draining into an endless stall
    # (#166).
    d = to_map(seed="CISHOP")
    shops = [p for p in d["graph"] if p["type"] == PHASE.SHOP]
    assert shops, "seed CISHOP grew no shop — re-pin the seed"
    bridge.follow("cheat", "gold", "5000")
    run("cheat", "goto", str(shops[0]["col"]), str(shops[0]["row"]))
    d = bridge.wait_phase(PHASE.SHOP)

    stall = d["cardRemoval"]
    assert stall and stall["used"] is False and stall["purchasable"] is True, stall
    first_cost = stall["cost"]
    deck_before = len(d["player"]["deck"])
    gold_before = d["gold"]

    d = remove_a_card_at_the_stall()
    assert len(d["player"]["deck"]) == deck_before - 1, "removal did not land"
    assert d["gold"] == gold_before - first_cost, \
        f"removal cost {first_cost}: {gold_before} -> {d['gold']}"

    # The observation taken straight after the removal already says sold
    # out — an agent that reads `used`/`purchasable` never even asks.
    stall = d["cardRemoval"]
    assert stall["used"] is True, f"a landed removal left the stall unused: {stall}"
    assert stall["purchasable"] is False, stall

    # ...and the dispatcher rejects the second buy on the same fact.
    err = reject(["buy", "card_removal", "--idx", "0"], REJECTION.BAD_INDEX)
    assert "sold out" in err, err
    d = obs()
    assert d["phase"] == PHASE.SHOP, \
        f"a sold-out card_removal still opened its picker: {d['phase']}"
    assert len(d["player"]["deck"]) == deck_before - 1, \
        "a sold-out card_removal removed a second card anyway"
    assert d["gold"] == gold_before - first_cost, \
        "a sold-out card_removal debited gold anyway"

    # Sold out is this stall's fact, not the run's: the next merchant sells
    # a removal again, priced up by the engine's own increase per removal
    # the run has already used (75 -> 100 off ascension).
    assert len(shops) > 1, "seed CISHOP grew only one shop — re-pin the seed"
    run("leave")
    bridge.wait_phase(PHASE.MAP)
    run("cheat", "goto", str(shops[1]["col"]), str(shops[1]["row"]))
    stall = bridge.wait_phase(PHASE.SHOP)["cardRemoval"]
    assert stall["used"] is False and stall["purchasable"] is True, \
        f"the next merchant opened already sold out: {stall}"
    assert stall["cost"] == first_cost + REMOVAL_PRICE_INCREASE, \
        f"removal priced {stall['cost']} after one at {first_cost}"
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
    # The followed decision elides presentation fields that the subsequent
    # full observation restores; idempotency is the stable belt identity.
    def potion_identity(potion):
        return potion["model"], potion["slot"], potion["target"]
    assert [potion_identity(potion) for potion in belt_after_second] == [
        potion_identity(potion) for potion in belt_after_first
    ], \
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
            elif phase == PHASE.REST_SITE:
                # A rest site owes the seat a decision before it can be left.
                bridge.walk_world(PHASE.MAP)
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


# ---------- L: localized item titles ----------

@case("L1 item titles follow the configured host language", boot_only=True)
def l1():
    expected = {
        "eng": {
            "BASH": "Bash",
            "DEFEND_IRONCLAD": "Defend",
            "STRIKE_IRONCLAD": "Strike",
            "BURNING_BLOOD": "Burning Blood",
            "FLEX_POTION": "Flex Potion",
        },
        "zhs": {
            "BASH": "痛击",
            "DEFEND_IRONCLAD": "防御",
            "STRIKE_IRONCLAD": "打击",
            "BURNING_BLOOD": "燃烧之血",
            "FLEX_POTION": "肌肉药水",
        },
    }
    language = os.environ.get("STS2_AGENT_LANG", "eng")
    assert language in expected, \
        f"L1 must boot with eng or zhs, got STS2_AGENT_LANG={language}"
    titles = expected[language]

    d = to_map(seed="CILOCALIZEDTITLES")
    deck = d["player"]["deck"]
    assert deck and all(card.get("title") for card in deck), deck
    for model in ("BASH", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD"):
        assert next(card["title"] for card in deck if card["model"] == model) \
            == titles[model], deck
    burning_blood = next(
        relic for relic in d["player"]["relicStates"]
        if relic["model"] == "BURNING_BLOOD")
    assert burning_blood["title"] == titles["BURNING_BLOOD"], burning_blood

    compact = run("obs", "--compact")
    assert isinstance(compact["player"]["deck"], dict), compact["player"]["deck"]
    assert compact["player"]["deck"].get("STRIKE_IRONCLAD", 0) > 0, \
        compact["player"]["deck"]
    assert all(relic.get("model") and relic.get("title") is None
               for relic in compact["player"]["relicStates"]), \
        compact["player"]["relicStates"]

    bridge.follow("cheat", PHASE.COMBAT, "CULTISTS_NORMAL")
    bridge.follow("cheat", "potion", "FLEX_POTION")
    d = obs()
    assert d["phase"] == PHASE.COMBAT, d["phase"]
    assert d["hand"] and all(card.get("title") for card in d["hand"]), d["hand"]
    for model in ("BASH", "DEFEND_IRONCLAD", "STRIKE_IRONCLAD"):
        matching = [card for card in d["hand"] if card["model"] == model]
        if matching:
            assert all(card["title"] == titles[model] for card in matching), matching

    # titles is index-aligned with cards, not a selector lookup: one selector
    # can cover copies whose titles differ by upgrade level.
    for pile in d["piles"].values():
        selectors = pile["cards"]
        assert isinstance(selectors, list), pile
        assert len(pile["titles"]) == len(selectors), pile
        assert all(pile["titles"]), pile
    draw = d["piles"]["draw"]
    assert ("DEFEND_IRONCLAD", titles["DEFEND_IRONCLAD"]) in \
        list(zip(draw["cards"], draw["titles"])), draw

    burning_blood = next(
        relic for relic in d["you"]["relics"]
        if relic["model"] == "BURNING_BLOOD")
    assert burning_blood["title"] == titles["BURNING_BLOOD"], burning_blood
    flex = next(
        potion for potion in d["potions"]
        if potion["model"] == "FLEX_POTION")
    assert flex["title"] == titles["FLEX_POTION"], flex

    compact = run("obs", "--compact")
    assert all(card.get("model") and card.get("selector")
               and card.get("title") is None for card in compact["hand"]), \
        compact["hand"]
    assert all(pile["cards"] is not None and pile.get("titles") is None
               for pile in compact["piles"].values()), compact["piles"]
    assert all(relic.get("model") and relic.get("title") is None
               for relic in compact["you"]["relics"]), compact["you"]["relics"]
    assert all(potion.get("model") and potion.get("title") is None
               for potion in compact["potions"]), compact["potions"]
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
    assert all(card.get("title") for card in first["cards"]), first["cards"]

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
    d = leave_opening_event()
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


def hand_selection(d):
    """The per-card view of what is picked, as sorted selectors."""
    return sorted(card["selector"] for card in d["cards"]
                  if card.get("selected"))


@case("C14 hand selection reports which cards are already picked")
def c14():
    # The host's stand-in picker keeps every candidate listed after a pick,
    # so the only way to tell a picked row from a free one is the row's own
    # selected flag — hands hold several copies of one model, and models
    # alone cannot distinguish them. Without the flag a caller re-picks the
    # first row forever, toggling it on and off instead of finishing.
    d = into_combat(seed="CIHANDSEL", character="SILENT")
    bridge.follow("cheat", "energy", "99")
    bridge.follow("cheat", "card", "HIDDEN_DAGGERS")
    d = bridge.follow("play", "HIDDEN_DAGGERS")
    assert d["phase"] == PHASE.HAND_SELECT, d
    assert (d.get("min"), d.get("max")) == (2, 2), d
    assert all("selected" in card for card in d["cards"]), d["cards"]
    assert hand_selection(d) == sorted(d["selected"]) == [], d

    first = d["cards"][0]
    d = bridge.follow("pick-card", str(first["idx"]))
    assert d["phase"] == PHASE.HAND_SELECT, d
    assert d["cards"][first["idx"]]["selected"] is True, d["cards"]
    assert hand_selection(d) == sorted(d["selected"]) \
        == [first["selector"]], d
    twin = next((card for card in d["cards"]
                 if card["idx"] != first["idx"]
                 and card["model"] == first["model"]), None)
    assert twin is None or twin["selected"] is False, d["cards"]

    # picking it again toggles it off — both views agree on that too
    d = bridge.follow("pick-card", str(first["idx"]))
    assert hand_selection(d) == sorted(d["selected"]) == [], d

    # a second, different card completes the pair and resolves the effect
    d = bridge.follow("pick-card", str(first["idx"]))
    second = next(card for card in d["cards"] if card["idx"] != first["idx"])
    d = bridge.follow("pick-card", str(second["idx"]))
    assert d["phase"] == PHASE.COMBAT, d

    # PURITY selects 0..3, so its partial selection needs an explicit confirm
    bridge.follow("cheat", "energy", "99")
    bridge.follow("cheat", "card", "PURITY")
    d = bridge.follow("play", "PURITY")
    assert d["phase"] == PHASE.HAND_SELECT and d.get("min") == 0, d
    partial = next(card for card in d["cards"] if not card["selected"])
    d = bridge.follow("pick-card", str(partial["idx"]))
    assert hand_selection(d) == sorted(d["selected"]) \
        == [partial["selector"]], d
    assert d.get("confirmable") is True, d
    d = bridge.follow("confirm")
    assert d["phase"] == PHASE.COMBAT, d

    # CHARGE picks from the draw pile rather than the hand, and lands on the
    # same stand-in picker. Drive it with the shared walker: that generic
    # agent path is what looped once a decision needed two distinct picks.
    d = into_combat(seed="CIHANDSEL2", character="REGENT")
    bridge.follow("cheat", "energy", "99")
    bridge.follow("cheat", "card", "CHARGE")
    d = bridge.follow("play", "CHARGE")
    assert d["phase"] == PHASE.HAND_SELECT, d
    assert (d.get("min"), d.get("max")) == (2, 2), d
    d = bridge.walk_world(PHASE.COMBAT, initial=d, timeout=30)
    assert d["phase"] == PHASE.COMBAT, d
    to_menu()


# ---------- V: victory ----------

@case("V1 cheat-driven full clear reaches a victory game_over")
def v1():
    launch(seed="CIVICT")
    leave_opening_event()
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
#
# One case per sweep kind in tests/sweeps.py (SWEEPS). deep=True marks the
# slow tail --quick drops for local iteration; the pre-merge gate runs them
# all, and tests/gate_coverage_test.py fails if a kind here goes uncovered.

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


@case("M6 context-bound content runs through its construction fixture")
def m6():
    # The sweeps' fixture contract, held in a case --quick still runs: M2
    # and M4 are deep, so without this the fixture could rot for a month.
    #
    # SEA_GLASS and MAD_SCIENCE are never constructed bare by the game.
    # OROBAS stamps the character whose pool Sea Glass reads; TINKER_TIME
    # stamps both the card type Mad Science resolves as and the rider it
    # carries. Injected raw, the first logs "obtained without a character ID
    # assigned" and the second throws ArgumentOutOfRangeException out of
    # OnPlay — fixture faults the sweeps would otherwise report as product
    # faults.
    entries = {
        kind: {e["model"]: e.get("context")
               for e in http("GET", f"/models?kind={kind}")[1]["entries"]}
        for kind in ("card", "relic")
    }
    assert entries["relic"]["SEA_GLASS"] == ["CharacterId"], \
        entries["relic"]["SEA_GLASS"]
    assert entries["card"]["MAD_SCIENCE"] == \
        ["TinkerTimeType", "TinkerTimeRider"], entries["card"]["MAD_SCIENCE"]
    assert entries["card"]["STRIKE_IRONCLAD"] is None, \
        "directly executable content must not advertise a construction context"

    # Mad Science: the stamped type decides how the card resolves, so an
    # attack must arrive targetable and land on an enemy — and the stamped
    # rider has to run too. A rider-less card still deals its damage, so
    # damage alone would pass on a half-configured fixture: the description
    # renders the rider clause as "???" (the game's own invalid-state
    # placeholder) and Sapping's Weak and Vulnerable never land.
    d = into_combat(seed="CIM6", character="IRONCLAD")
    bridge.follow("cheat", "energy", "99")
    d = bridge.follow("cheat", "card", "MAD_SCIENCE")
    grafted = next(c for c in d["hand"] if c["model"] == "MAD_SCIENCE")
    assert grafted.get("target") == "anyenemy", grafted
    assert "?" not in (grafted.get("description") or "?"), grafted
    victim = alive_enemy(d)
    d = bridge.follow("play", "MAD_SCIENCE", "--target", str(victim["id"]))
    hit = next(e for e in d["enemies"] if e["id"] == victim["id"])
    assert hit["hp"][0] < victim["hp"][0] or not hit["alive"], (victim, hit)
    assert {"WEAK_POWER", "VULNERABLE_POWER"} <= {p["id"] for p in hit["powers"]}, \
        hit
    to_menu()

    # Sea Glass: SILENT, not the Ironclad the unstamped relic falls back
    # to — the grid must be drawn from the owning character's pool.
    pools = {e["model"]: e.get("pool")
             for e in http("GET", "/models?kind=card")[1]["entries"]}
    to_map(seed="CIM6R", character="SILENT")
    d = bridge.follow("cheat", "relic", "SEA_GLASS")
    assert d["phase"] == PHASE.CARD_SELECT, d["phase"]
    offered = {pools.get(c["model"]) for c in d["cards"]}
    assert offered == {"silent"}, offered
    d = bridge.walk_world(PHASE.MAP, initial=d, claim_card_reward=True,
                          claim_reward_tiles=True)
    assert "SEA_GLASS" in d["player"]["relics"], d["player"]["relics"]
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
    # The stall also publishes the ordinary shop-stock shape, so the same
    # `buy relic --idx N` the dispatcher takes is derivable from obs alone.
    stock = d["relics"]
    assert [item["model"] for item in stock] == \
        [item["model"] for item in shop["relics"]], (stock, shop["relics"])
    assert all(item["idx"] == i for i, item in enumerate(stock)), stock
    assert all(item["purchasable"] for item in stock), stock
    assert "buy" in legal(), legal()
    # Broke: nothing is purchasable, so buy retires and the dispatcher agrees.
    poor = bridge.follow("cheat", "gold", "0")
    assert not any(item["purchasable"] for item in poor["relics"]), poor["relics"]
    assert "buy" not in legal(), legal()
    reject(["buy", "relic", "--idx", "0"], REJECTION.NOT_ENOUGH_GOLD)
    # The stall sells relics only — no cards, potions, or card removal.
    assert poor.get("cardRemoval") is None, poor.get("cardRemoval")
    for kind in ("card", "colorless", "potion", "card_removal"):
        reject(["buy", kind, "--idx", "0"], REJECTION.BAD_INDEX)
    reject(["buy", "card_removal", "--idx", "1"], REJECTION.BAD_INDEX)

    d = bridge.follow("cheat", "gold", "500")
    assert "buy" in legal(), legal()
    before_gold = d["player"]["gold"]
    before_rev = d["rev"]
    run("buy", "relic", "--idx", "0")
    d = bridge.wait_until(
        lambda snapshot: not snapshot["fakeMerchant"]["relics"][0]["stocked"],
        description="fake merchant relic to sell",
        after_rev=before_rev,
    )
    assert not d["fakeMerchant"]["relics"][0]["stocked"], d["fakeMerchant"]
    assert d["relics"][0]["purchasable"] is False, d["relics"][0]
    assert d["player"]["gold"] < before_gold, d["player"]
    assert first["model"] in d["player"]["relics"], d["player"]["relics"]
    to_menu()


@case("I7 fake merchant takes the Foul Potion as a fight")
def i7():
    # The stall's `canFight` advertised a fight no verb could fire: the
    # dispatcher took its merchant branch only in Phase.Shop, so the raw
    # step came back bad_phase (#167). Now the whole chain agrees — the
    # flag, the belt entry's `playable`, obs.legal and the dispatcher.
    to_map(seed="CIFAKEFIGHT")
    run("cheat", PHASE.EVENT, "FAKE_MERCHANT")
    d = bridge.wait_phase(PHASE.EVENT)

    # An empty belt buys nothing: no fight advertised, and the raw step is
    # refused for want of a potion rather than for the phase.
    for potion in list(d["player"]["potions"]):
        d = bridge.follow("potion-discard", str(potion["slot"]))
    assert d["player"]["potions"] == [], d["player"]["potions"]
    assert d["fakeMerchant"]["canFight"] is False, d["fakeMerchant"]
    assert "potion-use" not in legal(), legal()
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": 0},
    })
    assert status == 400 and result.get("err") == REJECTION.BAD_INDEX, \
        f"potion-use on an empty belt got the wrong gate: {status} {result}"

    # An ordinary potion is not what the stall trades for.
    d = bridge.follow("cheat", "potion", "ENERGY_POTION")
    ordinary = next(p for p in d["player"]["potions"]
                    if p["model"] == "ENERGY_POTION")
    assert ordinary["playable"] is False, ordinary
    assert d["fakeMerchant"]["canFight"] is False, d["fakeMerchant"]
    assert "potion-use" not in legal(), legal()
    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": ordinary["slot"]},
    })
    assert status == 400 and result.get("err") == REJECTION.NOT_PLAYABLE, \
        f"unadvertised stall potion-use was not rejected: {status} {result}"
    assert any(p["slot"] == ordinary["slot"] for p in obs()["player"]["potions"]), \
        "rejected ordinary potion left its belt slot"

    # The Foul Potion is the one the stall takes, and it pays in a fight.
    d = bridge.follow("cheat", "potion", "FOUL_POTION")
    foul = next(p for p in d["player"]["potions"] if p["model"] == "FOUL_POTION")
    assert foul["playable"] is True, foul
    assert d["fakeMerchant"]["canFight"] is True, d["fakeMerchant"]
    assert "potion-use" in legal(), legal()
    stocked = [relic for relic in d["fakeMerchant"]["relics"] if relic["stocked"]]
    rug = "The Merchant's Rug???"

    status, result = http("POST", "/step", {
        "action": "potion-use", "args": {"slot": foul["slot"]}, "follow": 5000,
    })
    d = followed_http_obs(status, result, "Foul Potion thrown at the stall")
    d = bridge.wait_phase(PHASE.COMBAT)
    # Spent exactly once: the slot cleared, so the same potion cannot also
    # be redeemed for gold in a later shop. Combat has no player footer —
    # the belt is the top-level `potions` there.
    assert not any(p["slot"] == foul["slot"] for p in d["potions"]), \
        f"thrown Foul Potion stayed in its belt slot: {d['potions']}"

    # The stall's own relic is the fight's prize, queued together with every
    # relic still on its shelf — the fight is how you rob the merchant.
    bridge.kill_current_combat()
    d = bridge.wait_phase(PHASE.REWARDS)
    relics = [r["description"] for r in d["rewards"] if r["type"] == "relic"]
    assert rug in relics, d["rewards"]
    assert len(relics) == 1 + len(stocked), (relics, stocked)
    to_menu()


@case("I6 proceed waits for the room's own decision")
def i6():
    # Neow offers three boons and no way out — the GUI renders no exit from
    # that page, so neither the decision projection nor the dispatcher may
    # invent one. `proceed` used to walk straight out of any event.
    launch(seed="CIPROCEEDGATE")
    opening = obs()
    assert opening["phase"] == PHASE.EVENT, opening
    assert opening.get("proceedAvailable") is False, opening
    assert opening["options"], opening
    assert "proceed" not in run("obs", "--decision")["legal"], \
        run("obs", "--decision")["legal"]

    reject(["proceed"], REJECTION.BAD_STATE)
    still_here = obs()
    assert still_here["phase"] == PHASE.EVENT, still_here
    assert still_here["id"] == opening["id"], (opening["id"], still_here["id"])
    assert still_here["options"] == opening["options"], still_here["options"]

    # Answering the page finishes the event; the way out then appears and
    # actually leaves.
    resolved = bridge.resolve_event_choices()
    assert resolved["phase"] == PHASE.EVENT, resolved
    assert resolved.get("proceedAvailable") is True, resolved
    assert "proceed" in run("obs", "--decision")["legal"], \
        run("obs", "--decision")["legal"]
    d = bridge.walk_world(PHASE.MAP, initial=bridge.follow("proceed"))
    assert d["phase"] == PHASE.MAP, d

    # Same gate at a rest site: an unspent option is a decision the room is
    # still owed, and headless used to advertise proceed unconditionally.
    rest = next(point for point in obs()["graph"]
                if point["type"] == "restsite")
    entered = bridge.follow(
        "cheat", "goto", str(rest["col"]), str(rest["row"]))
    assert entered["phase"] == PHASE.REST_SITE, entered
    assert entered.get("proceedAvailable") is False, entered
    assert "proceed" not in run("obs", "--decision")["legal"], \
        run("obs", "--decision")["legal"]

    reject(["proceed"], REJECTION.BAD_STATE)
    unmoved = obs()
    assert unmoved["phase"] == PHASE.REST_SITE, unmoved
    assert [option["id"] for option in unmoved["options"]] == \
        [option["id"] for option in entered["options"]], unmoved["options"]

    # Backing out of an option's own picker consumes nothing: the seat still
    # owes the room a decision even though the synchronizer has already
    # stamped its chosen index. Reading that stamp as "spent" let proceed
    # leave a rest site with both options and full hp intact.
    smith = next(option for option in entered["options"]
                 if option["id"] == "SMITH" and option["enabled"])
    cancelling = bridge.follow("option", str(smith["idx"]))
    assert cancelling["phase"] == PHASE.CARD_SELECT, cancelling
    backed_out = bridge.follow("skip")
    if backed_out["phase"] != PHASE.REST_SITE:
        backed_out = bridge.wait_phase(PHASE.REST_SITE)
    assert backed_out.get("proceedAvailable") is False, backed_out
    assert "proceed" not in run("obs", "--decision")["legal"], \
        run("obs", "--decision")["legal"]
    assert [option["id"] for option in backed_out["options"]] == \
        [option["id"] for option in entered["options"]], backed_out["options"]
    assert backed_out["player"]["hp"] == entered["player"]["hp"], \
        (entered["player"]["hp"], backed_out["player"]["hp"])

    reject(["proceed"], REJECTION.BAD_STATE)
    intact = obs()
    assert intact["phase"] == PHASE.REST_SITE, intact
    assert [option["id"] for option in intact["options"]] == \
        [option["id"] for option in entered["options"]], intact["options"]

    # The same option, carried through: now the room is spent and lets go.
    picking = bridge.follow("option", str(smith["idx"]))
    assert picking["phase"] == PHASE.CARD_SELECT, picking
    settled = bridge.follow("pick-card", "0")
    if settled["phase"] != PHASE.REST_SITE:
        settled = bridge.wait_phase(PHASE.REST_SITE)
    assert settled.get("proceedAvailable") is True, settled
    assert "proceed" in run("obs", "--decision")["legal"], \
        run("obs", "--decision")["legal"]
    left = bridge.walk_world(PHASE.MAP, initial=bridge.follow("proceed"))
    assert left["phase"] == PHASE.MAP, left
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


def build_parser():
    ap = argparse.ArgumentParser()
    ap.add_argument("--boot", action="store_true")
    ap.add_argument("--quick", action="store_true",
                    help="LOCAL ITERATION ONLY — skip the exhaustive sweeps "
                         "(M*), first-option E1; never for the pre-merge gate")
    ap.add_argument("--only",
                    help="comma-separated case ids or case-name prefixes")
    ap.add_argument("--keys-out")
    ap.add_argument("--log", default=os.path.join(
        os.environ.get("TMPDIR", "/tmp"), "spirescry-ci-host.log"))
    ap.add_argument("--list", action="store_true")
    return ap


def skip_reason(boot_only, deep, *, boot, quick):
    """Why an invocation with these flags would not run a case — None if
    it runs. A plain rule over the case flags so tests/gate_coverage_test.py
    can ask what a given argv (the gate's, say) actually covers."""
    if boot_only and not boot:
        return "needs --boot"
    if deep and quick:
        return "--quick"
    return None


def main():
    global LOG_PATH, ARGS
    ARGS = build_parser().parse_args()

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
            if not selects(name, only):
                continue
            skipped = skip_reason(boot_only, deep,
                                  boot=ARGS.boot, quick=ARGS.quick)
            if skipped:
                print(f"SKIP {name} ({skipped})")
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
              if selects(name, only)
              and skip_reason(b, deep, boot=ARGS.boot, quick=ARGS.quick) is None)
    print(f"\n{ran - len(failures)}/{ran} cases passed"
          + (f"; FAILED: {failures}" if failures else ""))
    if failures and LOG_PATH:
        print(f"host log: {LOG_PATH}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
