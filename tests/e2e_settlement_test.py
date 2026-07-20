#!/usr/bin/env python3
"""E2E helpers wait for semantic hydration beyond action settlement."""

import os
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(__file__))
import e2e


class E2ESettlementTests(unittest.TestCase):
    def test_semantic_snapshot_requests_a_full_observation_after_settlement(self):
        settled = {"rev": 12, "graph": None}
        hydrated = {"rev": 14, "graph": [{"markers": []}]}
        predicate = lambda snapshot: bool(snapshot.get("graph"))

        with mock.patch.object(
                e2e.bridge, "wait_until", return_value=hydrated) as wait:
            actual = e2e.await_semantic_snapshot(
                settled, predicate, "map graph hydration")

        self.assertEqual(hydrated, actual)
        wait.assert_called_once_with(
            predicate,
            timeout=10,
            description="map graph hydration",
        )

    def test_semantic_snapshot_does_not_wait_when_already_hydrated(self):
        settled = {"rev": 12, "graph": [{"markers": []}]}
        predicate = lambda snapshot: bool(snapshot.get("graph"))

        with mock.patch.object(e2e.bridge, "wait_until") as wait:
            actual = e2e.await_semantic_snapshot(
                settled, predicate, "map graph hydration")

        self.assertIs(settled, actual)
        wait.assert_not_called()

    def test_raw_http_follow_rejects_engine_faults(self):
        result = {
            "ok": True,
            "settled": True,
            "outcome": "fault",
            "errors": ["boom"],
            "obs": {"rev": 8},
        }

        with self.assertRaises(AssertionError):
            e2e.followed_http_obs(200, result, "faulting action")


if __name__ == "__main__":
    unittest.main()
