#!/usr/bin/env python3
"""Public B1 contract for stamped host identity."""

import subprocess
import unittest
from unittest import mock

import e2e
from protocol import CHEAT_ARGUMENT_SHAPES, PROTOCOL_VERSION


def health(build_hash):
    return {
        "ok": True,
        "mod": "spirescry",
        "version": "0.1.0",
        "buildHash": build_hash,
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": {
            "verbs": ["end-turn"],
            "cheats": ["relic"],
            "cheatArgumentShapes": list(CHEAT_ARGUMENT_SHAPES),
        },
        "phase": "main_menu",
        "rev": 0,
        "runId": "none",
        "executorStuckMs": 0,
        "queues": [],
    }


class BuildIdentityTests(unittest.TestCase):
    def assert_b1_accepts(self, build_hash):
        completed = subprocess.CompletedProcess(
            ["build.sh", "stamp"], 0, stdout=build_hash + "\n", stderr="")
        with mock.patch.object(e2e, "http", return_value=(200, health(build_hash))), \
                mock.patch.object(e2e.subprocess, "run", return_value=completed):
            e2e.b1()

    def assert_b1_rejects(self, build_hash, checkout_stamp=None):
        completed = subprocess.CompletedProcess(
            ["build.sh", "stamp"], 0,
            stdout=(checkout_stamp or build_hash) + "\n", stderr="")
        with mock.patch.object(e2e, "http", return_value=(200, health(build_hash))), \
                mock.patch.object(e2e.subprocess, "run", return_value=completed), \
                self.assertRaises(AssertionError):
            e2e.b1()

    def test_b1_accepts_clean_and_dirty_content_stamps(self):
        self.assert_b1_accepts("94c7e57.a1b2c3d4e5f6")
        self.assert_b1_accepts("94c7e57-dirty.a1b2c3d4e5f6")

    def test_b1_rejects_unverifiable_or_legacy_identity(self):
        self.assert_b1_rejects("unknown")
        self.assert_b1_rejects("94c7e57")

    def test_b1_rejects_a_stamped_host_from_different_inputs(self):
        self.assert_b1_rejects(
            "94c7e57.a1b2c3d4e5f6",
            checkout_stamp="94c7e57.0123456789ab",
        )


if __name__ == "__main__":
    unittest.main()
