#!/usr/bin/env python3
"""Parity actions consume the bridge settlement boundary, not acceptance."""

import ast
import inspect
import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(__file__))
import bridge
import parity


class ParitySettlementTests(unittest.TestCase):
    def test_in_place_actions_do_not_bypass_follow(self):
        tree = ast.parse(inspect.getsource(parity))
        direct_actions = []
        for node in ast.walk(tree):
            if not isinstance(node, ast.Call) or not isinstance(node.func, ast.Name):
                continue
            if node.func.id != "run" or not node.args:
                continue
            action = node.args[0]
            qualifier = node.args[1] if len(node.args) > 1 else None
            direct_actions.append((
                action.value if isinstance(action, ast.Constant) else None,
                qualifier.value if isinstance(qualifier, ast.Constant) else None,
            ))

        # Direct run() is reserved for transitions whose target phase is an
        # explicit wait predicate. In-place mutations must consume follow().
        allowed = {
            ("abandon", None),
            ("buy", "card_removal"),
            ("cheat", "goto"),
            ("leave", None),
            ("map-move", None),
            ("option", None),
            ("proceed", None),
        }
        self.assertTrue(direct_actions)
        self.assertEqual([], [action for action in direct_actions
                              if action not in allowed])

    def test_followed_action_records_its_settled_observation_once(self):
        settled = {"phase": bridge.PHASE.REWARDS, "rev": 12}
        with mock.patch.object(
                parity.bridge, "follow", return_value=settled) as follow, \
                mock.patch.object(parity, "record") as record:
            actual = parity.follow("pick-reward", "3")

        self.assertEqual(settled, actual)
        follow.assert_called_once_with("pick-reward", "3", timeout_ms=10000)
        record.assert_called_once_with(settled)

    def test_picker_confirm_consumes_the_settled_pick_observation(self):
        picked = {
            "phase": bridge.PHASE.CARD_SELECT,
            "rev": 12,
            "confirmable": True,
        }
        confirmed = {"phase": bridge.PHASE.REST_SITE, "rev": 14}
        with mock.patch.object(
                parity.bridge, "follow", return_value=confirmed) as follow, \
                mock.patch.object(parity.bridge, "wait_after") as wait_after, \
                mock.patch.object(parity, "record") as record:
            actual = parity.confirm_if_selecting(picked)

        self.assertEqual(confirmed, actual)
        follow.assert_called_once_with("confirm", timeout_ms=10000)
        wait_after.assert_not_called()
        record.assert_called_once_with(confirmed)


if __name__ == "__main__":
    unittest.main()
