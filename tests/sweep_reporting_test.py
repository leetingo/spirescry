#!/usr/bin/env python3
"""Contract: a sweep names every failure the moment it records it.

The end-of-run `SWEEP FAILURE:` summary is not reachable when the sweep
dies early — a bridge timeout, a wedged host, a CI kill. Whatever the
sweep already found has to be readable in the output at that point, or a
gate failure reports a count with no name and forces a full re-run.
"""
import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(__file__))
import bridge
import sweeps

COMBAT = bridge.PHASE.COMBAT
MAP = bridge.PHASE.MAP


def snapshot(hand=(), potions=(), phase=COMBAT, rev=7):
    return {"phase": phase, "rev": rev, "hand": list(hand),
            "potions": list(potions), "enemies": [{"id": 1, "alive": True}],
            "player": {"relics": []}}


class SweepReportingTest(unittest.TestCase):
    def setUp(self):
        self.log = []

    def logger(self, line):
        self.log.append(line)

    def failure_lines(self):
        return [l for l in self.log if l.strip().startswith("FAIL ")]

    # ---------- cards ----------

    def run_cards(self, entries, follow_result, only=None, **overrides):
        """Drive cards() over a scripted bridge. `follow_result` is the
        only seam that decides pass/fail per card."""
        patches = {
            "model_entries": lambda kind: list(entries),
            "fresh_run": mock.DEFAULT,
            "enter_sandbag": lambda: snapshot(),
            "follow_result": follow_result,
            "wedge_events": lambda since: [],
            "obs": lambda *a, **k: snapshot(),
            "run": lambda *a, **k: {},
        }
        patches.update(overrides)
        with mock.patch.multiple(sweeps, **patches), \
                mock.patch.object(bridge, "follow",
                                  side_effect=lambda *a, **k: snapshot()), \
                mock.patch.object(bridge, "walk_world",
                                  side_effect=lambda *a, **k: snapshot()):
            return sweeps.cards(log=self.logger,
                                only={e["model"] for e in entries}
                                if only is None else only)

    def test_graft_failure_is_named_when_it_happens(self):
        failures = self.run_cards(
            [{"model": "MAD_SCIENCE", "pool": "ironclad"}],
            lambda *args: {"_err": "engine_error:System.ArgumentOutOfRange"})

        self.assertEqual(["MAD_SCIENCE"], list(failures))
        self.assertIn("  FAIL MAD_SCIENCE: graft: engine_error:"
                      "System.ArgumentOutOfRange", self.log)

    def test_card_that_never_reaches_the_hand_is_named(self):
        failures = self.run_cards(
            [{"model": "GHOST", "pool": "ironclad"}],
            lambda *args: {"obs": snapshot(hand=[{"model": "OTHER"}])})

        self.assertEqual(["GHOST"], list(failures))
        self.assertEqual(["  FAIL GHOST: grafted card never reached the hand"],
                         self.failure_lines())

    def test_accepted_unplayable_card_is_named(self):
        hand = [{"model": "BURN", "unplayable": True}]

        def follow_result(*args):
            if args[0] == "cheat":
                return {"obs": snapshot(hand=hand)}
            return {"obs": snapshot(hand=hand)}  # play accepted — a fault

        failures = self.run_cards(
            [{"model": "BURN", "pool": "ironclad"}], follow_result)

        self.assertEqual(["BURN"], list(failures))
        self.assertEqual(["  FAIL BURN: unplayable card was accepted"],
                         self.failure_lines())

    def test_wrong_rejection_error_is_named(self):
        hand = [{"model": "BURN", "unplayable": True}]

        def follow_result(*args):
            if args[0] == "cheat":
                return {"obs": snapshot(hand=hand)}
            return {"_err": "internal_error: boom"}

        failures = self.run_cards(
            [{"model": "BURN", "pool": "ironclad"}], follow_result)

        self.assertEqual(["BURN"], list(failures))
        self.assertEqual(1, len(self.failure_lines()))
        self.assertIn("rejected with wrong error", self.failure_lines()[0])

    def test_play_failure_is_named(self):
        hand = [{"model": "CHARGE"}]

        def follow_result(*args):
            if args[0] == "cheat":
                return {"obs": snapshot(hand=hand)}
            return {"_err": "did not settle (timeout)"}

        failures = self.run_cards(
            [{"model": "CHARGE", "pool": "ironclad"}], follow_result)

        self.assertEqual(["CHARGE"], list(failures))
        self.assertEqual(["  FAIL CHARGE: play: did not settle (timeout)"],
                         self.failure_lines())

    def test_progress_line_survives_a_failing_entry(self):
        entries = [{"model": f"CARD{i:03d}", "pool": "ironclad"}
                   for i in range(50)]

        self.run_cards(
            entries, lambda *args: {"_err": "graft exploded"})

        self.assertIn("  ...50/50 (50 failures)", self.log)

    def test_names_survive_a_sweep_that_dies_before_its_summary(self):
        entries = [{"model": "FIRST", "pool": "ironclad"},
                   {"model": "SECOND", "pool": "ironclad"}]

        def follow_result(*args):
            if args[-1] == "SECOND":
                raise TimeoutError("spirescry obs -> command timed out after 30s")
            return {"_err": "engine_error"}

        with self.assertRaises(TimeoutError):
            self.run_cards(entries, follow_result)

        # No summary ran, yet the first failure is named in the output.
        self.assertEqual(["  FAIL FIRST: graft: engine_error"],
                         self.failure_lines())

    def test_clean_sweep_names_nothing(self):
        hand = [{"model": "STRIKE"}]

        failures = self.run_cards(
            [{"model": "STRIKE", "pool": "ironclad"}],
            lambda *args: {"obs": snapshot(hand=hand)})

        self.assertEqual({}, failures)
        self.assertEqual([], self.failure_lines())

    # ---------- the other three sweeps report on the same terms ----------

    def test_encounter_failure_is_named_when_it_happens(self):
        with mock.patch.multiple(
                sweeps,
                model_entries=lambda kind: [{"model": "GREMLIN_GANG"}],
                fresh_run=mock.DEFAULT,
                run=lambda *a, **k: ({"_err": "no such encounter"}
                                     if a[0] == "cheat" else {}),
                obs=lambda *a, **k: snapshot(phase=MAP)), \
                mock.patch.object(bridge, "walk_world",
                                  side_effect=lambda *a, **k: snapshot(phase=MAP)):
            failures = sweeps.encounters(log=self.logger)

        self.assertEqual(["GREMLIN_GANG"], list(failures))
        self.assertEqual(["  FAIL GREMLIN_GANG: force: no such encounter"],
                         self.failure_lines())

    def test_potion_failure_is_named_when_it_happens(self):
        with mock.patch.multiple(
                sweeps,
                model_entries=lambda kind: [{"model": "FIRE_POTION"}],
                fresh_run=mock.DEFAULT,
                enter_sandbag=lambda: snapshot(),
                follow_result=lambda *a, **k: {"_err": "engine_error"},
                obs=lambda *a, **k: snapshot(),
                run=lambda *a, **k: {}), \
                mock.patch.object(bridge, "follow",
                                  side_effect=lambda *a, **k: snapshot()):
            failures = sweeps.potions(log=self.logger)

        self.assertEqual(["FIRE_POTION"], list(failures))
        self.assertEqual(["  FAIL FIRE_POTION: procure: engine_error"],
                         self.failure_lines())

    def test_relic_failure_is_named_when_it_happens(self):
        with mock.patch.multiple(
                sweeps,
                model_entries=lambda kind: [{"model": "BURNING_BLOOD"}],
                fresh_run=mock.DEFAULT,
                follow_result=lambda *a, **k: {"_err": "engine_error"},
                obs=lambda *a, **k: snapshot(phase=MAP),
                run=lambda *a, **k: {}):
            failures = sweeps.relics(log=self.logger)

        self.assertEqual(["BURNING_BLOOD"], list(failures))
        self.assertEqual(["  FAIL BURNING_BLOOD: grant: engine_error"],
                         self.failure_lines())


if __name__ == "__main__":
    unittest.main(verbosity=2)
