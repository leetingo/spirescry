#!/usr/bin/env python3
"""The sweep quarantine must stay small, tracked, and self-retiring.

The gate runs the M1–M4 sweeps in full (tests/gate_coverage_test.py keeps
it that way). A fault filed as an open product issue is quarantined so an
unrelated PR's gate is not red for a bug it did not cause — but a
quarantine that can hide a new fault, or that outlives its fix, is worse
than the --quick it replaced. These are pure value tests: no host needed,
so CI runs them too.
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import sweeps


class QuarantineShapeTests(unittest.TestCase):
    def test_every_quarantined_kind_is_a_real_sweep(self):
        self.assertEqual(set(sweeps.QUARANTINE) - set(sweeps.SWEEPS), set(),
                         "sweeps.QUARANTINE names a kind sweeps.SWEEPS "
                         "does not run — nothing would ever retire it")

    def test_every_entry_names_an_open_issue(self):
        for kind, entries in sweeps.QUARANTINE.items():
            for name, issue in entries.items():
                self.assertIsInstance(name, str, kind)
                self.assertTrue(name, f"{kind}: empty content id")
                self.assertIsInstance(issue, int, f"{kind}/{name}")
                self.assertGreater(issue, 0, f"{kind}/{name}: not an issue "
                                             "number — a quarantine with no "
                                             "issue is never fixed")


class QuarantineMechanismTests(unittest.TestCase):
    """The map is empty today (every entry retired when its fix merged), so
    these drive the mechanism through a stand-in map. Testing it against
    whatever happens to be quarantined would make the behaviour untested the
    moment the list empties — exactly when the next regression needs it."""

    ENTRIES = {
        "cards": {"BROKEN_CARD": 4242, "OTHER_CARD": 4243},
        "relics": {"BROKEN_RELIC": 4242},
    }

    def setUp(self):
        real = sweeps.QUARANTINE
        sweeps.QUARANTINE = {k: dict(v) for k, v in self.ENTRIES.items()}
        self.addCleanup(setattr, sweeps, "QUARANTINE", real)

    def test_untracked_failure_blocks(self):
        blocking, tracked = sweeps.partition("relics", {"NEW_RELIC": "boom"})
        self.assertEqual(blocking, {"NEW_RELIC": "boom"})
        self.assertEqual(tracked, {})

    def test_tracked_failure_does_not_block(self):
        blocking, tracked = sweeps.partition("relics", {"BROKEN_RELIC": "boom"})
        self.assertEqual(blocking, {})
        self.assertEqual(tracked, {"BROKEN_RELIC": (4242, "boom")})

    def test_a_new_fault_alongside_a_tracked_one_still_blocks(self):
        blocking, _ = sweeps.partition(
            "cards", {"BROKEN_CARD": "boom", "NEW_CARD": "wedge"})
        self.assertEqual(blocking, {"NEW_CARD": "wedge"})

    def test_kind_with_no_quarantine_blocks_everything(self):
        blocking, tracked = sweeps.partition("nosuchkind", {"X": "boom"})
        self.assertEqual(blocking, {"X": "boom"})
        self.assertEqual(tracked, {})

    def test_entry_that_stopped_failing_is_stale(self):
        self.assertEqual(
            sweeps.stale_quarantine("cards", {"OTHER_CARD": "boom"}),
            ["BROKEN_CARD"])

    def test_nothing_is_stale_while_every_entry_still_fails(self):
        self.assertEqual(
            sweeps.stale_quarantine(
                "cards", {"BROKEN_CARD": "boom", "OTHER_CARD": "boom"}), [])


class LiveQuarantineTests(unittest.TestCase):
    def test_every_kind_has_a_place_to_file_a_regression(self):
        # relics() reads QUARANTINE["relics"] directly, and an entry filed
        # under a kind with no slot would never be honoured.
        self.assertEqual(set(sweeps.QUARANTINE), set(sweeps.SWEEPS))

    def test_a_fault_the_live_map_does_not_list_is_never_exempt(self):
        # Empty is the healthy state; make sure it reads as "no exemptions"
        # rather than "everything exempt".
        for kind in sweeps.SWEEPS:
            blocking, tracked = sweeps.partition(kind, {"ANY": "boom"})
            self.assertEqual(blocking, {"ANY": "boom"}, kind)
            self.assertEqual(tracked, {}, kind)


class RelicSweepTests(unittest.TestCase):
    def test_relic_sweep_only_fails_fast_on_untracked_faults(self):
        # relics() stops at the first fault because a broken obtain hook can
        # wedge the run. A quarantined fault must not stop it — the rest of
        # the belt would then go unswept for as long as the issue is open,
        # which is the coverage hole the quarantine exists to avoid.
        import inspect
        source = inspect.getsource(sweeps.relics)
        self.assertIn('if relic not in QUARANTINE["relics"]:', source)
        self.assertIn("return failures", source)


if __name__ == "__main__":
    unittest.main()
