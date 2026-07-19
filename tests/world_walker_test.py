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
        prepared = []
        for revision, snapshot in enumerate(snapshots, start=1):
            snapshot = dict(snapshot)
            snapshot.setdefault("rev", revision)
            prepared.append(snapshot)
        remaining = iter(prepared)
        actions = []

        def observe(*_args, **_kwargs):
            return next(remaining)

        def act(*args, **kwargs):
            action_args = args[:-2] if args[-2:] == ("--follow", "10000") else args
            actions.append((action_args, kwargs))
            return {}

        def kill(**_kwargs):
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
                {"phase": "rewards", "rev": 41,
                 "rewards": [{"idx": 4}]},
                {"phase": "map", "rev": 42},
            ],
            "map",
            claim_reward_tiles=True,
        )

        self.assertEqual("map", settled["phase"])
        self.assertEqual(("pick-reward", "4"), actions[0][0])
        self.assertEqual(0, sleeps)

    def test_action_rejection_is_reported_before_another_observation(self):
        with mock.patch.object(
                bridge, "obs",
                return_value={"phase": "event", "rev": 17,
                              "options": [{"idx": 2}]}) as observe, \
                mock.patch.object(
                    bridge, "run",
                    return_value={"_err": "bad_state: option disappeared"}) as act:
            with self.assertRaisesRegex(
                    AssertionError, "option.*bad_state: option disappeared"):
                bridge.walk_world("map")

        observe.assert_called_once_with()
        act.assert_called_once_with("option", "2", allow_fail=True)

    def test_transient_action_rejections_keep_their_verb_and_error(self):
        cases = [
            ({"phase": "bundle_select", "rev": 20,
              "confirmable": False}, {}, "pick-card"),
            ({"phase": "rewards", "rev": 21,
              "rewards": [{"idx": 3}]},
             {"claim_reward_tiles": True}, "pick-reward"),
            ({"phase": "card_reward", "rev": 22,
              "cards": [{"idx": 4}]},
             {"claim_card_reward": True}, "pick-card"),
            ({"phase": "relic_reward", "rev": 23,
              "relics": [{"idx": 5}]},
             {"claim_relic_reward": True}, "pick-relic"),
            ({"phase": "shop", "rev": 24}, {}, "leave"),
        ]
        for snapshot, claims, verb in cases:
            with self.subTest(phase=snapshot["phase"]), \
                    mock.patch.object(
                        bridge, "obs", return_value=snapshot) as observe, \
                    mock.patch.object(
                        bridge, "run",
                        return_value={"_err": "bad_state: rejected"}):
                with self.assertRaisesRegex(
                        AssertionError, f"{verb}.*bad_state: rejected"):
                    bridge.walk_world("map", **claims)

                observe.assert_called_once_with()

    def test_combat_walker_surfaces_action_rejection_without_sleeping(self):
        combat = {
            "phase": "combat", "rev": 31, "side": "player",
            "potions": [], "enemies": [{"alive": True, "id": 1}],
            "you": {"energy": [3, 3]}, "hand": [],
        }
        with mock.patch.object(bridge, "obs", return_value=combat), \
                mock.patch.object(
                    bridge, "run",
                    return_value={"_err": "bad_state: heal rejected"}), \
                mock.patch.object(bridge.time, "sleep") as sleep:
            with self.assertRaisesRegex(
                    AssertionError, "cheat heal.*bad_state: heal rejected"):
                bridge.kill_current_combat()

        sleep.assert_not_called()

    def test_wait_until_requires_a_revision_change_after_an_action(self):
        with mock.patch.object(
                bridge, "obs",
                side_effect=[
                    {"phase": "event", "rev": 10, "changed": True},
                    {"phase": "event", "rev": 11, "changed": False},
                    {"phase": "event", "rev": 12, "changed": True},
                ]) as observe:
            settled = bridge.wait_until(
                lambda snapshot: snapshot["phase"] == "event",
                after_rev=11,
                description="event action applied",
            )

        self.assertEqual(12, settled["rev"])
        self.assertEqual(11, observe.call_args_list[0].args[0])

    def test_follow_returns_only_a_settled_observation(self):
        response = {
            "settled": True,
            "outcome": "settled",
            "obs": {"phase": "treasure", "rev": 19},
        }
        with mock.patch.object(bridge, "run", return_value=response) as act:
            settled = bridge.follow("skip")

        self.assertEqual(response["obs"], settled)
        act.assert_called_once_with(
            "skip", "--follow", "10000", allow_errors=False, timeout=30)

    def test_follow_rejects_an_unsettled_boundary(self):
        with mock.patch.object(
                bridge, "run",
                return_value={"settled": False, "outcome": "timeout"}):
            with self.assertRaisesRegex(AssertionError, "skip did not settle"):
                bridge.follow("skip")

    def test_launch_is_single_checked_action_followed_by_revision_wait(self):
        started = {"phase": bridge.PHASE.EVENT, "rev": 8}
        with mock.patch.object(
                bridge, "obs", return_value={"phase": "main_menu", "rev": 7}), \
                mock.patch.object(bridge, "run", return_value={}) as act, \
                mock.patch.object(
                    bridge, "wait_phase", return_value=started) as wait:
            actual = bridge.launch_run(seed="SEED", timeout=40)

        self.assertEqual(started, actual)
        act.assert_called_once_with("new-run", "IRONCLAD", "--seed", "SEED")
        wait.assert_called_once_with(
            bridge.PHASE.EVENT, timeout=40, on_obs=None, after_rev=7)

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
                side_effect=[
                    {"phase": "combat", "rev": 8},
                    {"phase": "map", "rev": 9},
                ]), \
                mock.patch.object(eventsweep, "fresh_run") as fresh, \
                mock.patch.object(eventsweep, "run", return_value={}), \
                mock.patch.object(
                    eventsweep.bridge, "obs",
                    return_value={"phase": "event", "rev": 10}):
            result = eventsweep.force("TEST_EVENT")

        fresh.assert_called_once_with()
        self.assertEqual("event", result["phase"])


if __name__ == "__main__":
    unittest.main()
