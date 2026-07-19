#!/usr/bin/env python3
"""Unit contract for the shared Python world walker."""
import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(__file__))
import bridge
import eventsweep


class WorldWalkerTests(unittest.TestCase):
    def drive(self, snapshots, *wanted, **claims):
        remaining = iter(snapshots)
        actions = []

        def observe(*_args, **_kwargs):
            return next(remaining)

        def act(*args, **kwargs):
            actions.append((args, kwargs))
            return {}

        def kill():
            actions.append((("kill-current-combat",), {}))

        with mock.patch.object(bridge, "obs", side_effect=observe), \
                mock.patch.object(bridge, "run", side_effect=act), \
                mock.patch.object(
                    bridge, "kill_current_combat", side_effect=kill), \
                mock.patch.object(bridge.time, "sleep") as sleep:
            settled = bridge.walk_world(*wanted, **claims)
        return settled, actions, sleep.call_count

    def test_walk_to_map_claims_reward_tiles_when_requested(self):
        settled, actions, sleeps = self.drive(
            [
                {"phase": "rewards", "rewards": [{"idx": 4}]},
                {"phase": "map"},
            ],
            "map",
            claim_reward_tiles=True,
        )

        self.assertEqual("map", settled["phase"])
        self.assertEqual(("pick-reward", "4"), actions[0][0])
        self.assertEqual(1, sleeps)

    def test_claim_flags_select_card_and_relic_in_shared_grammar(self):
        cases = [
            ("card_reward", "cards", "claim_card_reward", "pick-card"),
            ("relic_reward", "relics", "claim_relic_reward", "pick-relic"),
        ]
        for phase, field, flag, verb in cases:
            with self.subTest(phase=phase):
                settled, actions, _ = self.drive(
                    [
                        {"phase": phase, field: [{"idx": 7}]},
                        {"phase": "map"},
                    ],
                    "map",
                    **{flag: True},
                )
                self.assertEqual("map", settled["phase"])
                self.assertEqual((verb, "7"), actions[0][0])

    def test_unclaimed_card_and_relic_rewards_are_skipped(self):
        for phase, field in (
                ("card_reward", "cards"), ("relic_reward", "relics")):
            with self.subTest(phase=phase):
                _, actions, _ = self.drive(
                    [
                        {"phase": phase, field: [{"idx": 2}]},
                        {"phase": "map"},
                    ],
                    "map",
                )
                self.assertEqual(("skip",), actions[0][0])

    def test_no_wanted_phase_drains_picker_but_does_not_finish_combat(self):
        settled, actions, _ = self.drive(
            [
                {"phase": "card_select", "min": 1,
                 "cards": [{"idx": 3}]},
                {"phase": "combat"},
                {"phase": "combat"},
                {"phase": "combat"},
            ],
            claim_reward_tiles=True,
            claim_card_reward=True,
            claim_relic_reward=True,
        )

        self.assertEqual("combat", settled["phase"])
        self.assertEqual([("pick-card", "3")],
                         [action for action, _ in actions])

    def test_walk_to_map_selects_event_option_and_finishes_combat(self):
        settled, actions, _ = self.drive(
            [
                {"phase": "event", "options": [
                    {"idx": 0, "locked": True},
                    {"idx": 5, "locked": False, "chosen": False},
                ]},
                {"phase": "combat"},
                {"phase": "map"},
            ],
            "map",
        )

        self.assertEqual("map", settled["phase"])
        self.assertEqual(
            [("option", "5"), ("kill-current-combat",)],
            [action for action, _ in actions],
        )

    def test_terminal_phase_returns_without_guessing_suite_policy(self):
        settled, actions, sleeps = self.drive(
            [{"phase": "game_over"}], "map")

        self.assertEqual("game_over", settled["phase"])
        self.assertEqual([], actions)
        self.assertEqual(0, sleeps)

    def test_wanted_event_is_observed_without_choosing_an_option(self):
        settled, actions, sleeps = self.drive(
            [{"phase": "event", "options": [{"idx": 1}]}],
            "event", "map")

        self.assertEqual("event", settled["phase"])
        self.assertEqual([], actions)
        self.assertEqual(0, sleeps)

    def test_after_revision_long_polls_before_accepting_wanted_phase(self):
        with mock.patch.object(
                bridge, "obs", return_value={"phase": "event", "rev": 12}) as observe:
            settled = bridge.walk_world("event", after_rev=11)

        self.assertEqual("event", settled["phase"])
        observe.assert_called_once_with(11)

    def test_event_sweep_archives_walker_failures_as_stuck_results(self):
        with mock.patch.object(
                eventsweep.bridge, "walk_world",
                side_effect=AssertionError("would not settle")), \
                mock.patch.object(
                    eventsweep, "obs", return_value={"phase": "combat"}):
            result = eventsweep.walk_or_stuck("map")

        self.assertIn("stuck@combat:would not settle", result["phase"])

    def test_event_force_restarts_instead_of_inheriting_non_map_state(self):
        with mock.patch.object(
                eventsweep, "obs",
                side_effect=[{"phase": "combat"}, {"phase": "event"}]), \
                mock.patch.object(eventsweep, "fresh_run") as fresh, \
                mock.patch.object(eventsweep, "run", return_value={}):
            result = eventsweep.force("TEST_EVENT")

        fresh.assert_called_once_with()
        self.assertEqual("event", result["phase"])


if __name__ == "__main__":
    unittest.main()
