#!/usr/bin/env python3
"""Public B1 contract for stamped host identity."""

import os
import shutil
import subprocess
import tempfile
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
        "pendingAsync": 0,
        "pendingEventOptions": 0,
        "queues": [],
    }


class BuildIdentityTests(unittest.TestCase):
    @staticmethod
    def stamp(repo):
        return subprocess.run(
            [os.path.join(repo, "build.sh"), "stamp"],
            cwd=repo,
            capture_output=True,
            text=True,
            timeout=60,
            check=True,
        ).stdout.strip()

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

    def test_generator_content_changes_stamp_while_checkout_stays_dirty(self):
        source_repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        with tempfile.TemporaryDirectory(prefix="spirescry-stamp-input-") as root:
            checkout = os.path.join(root, "repo")
            subprocess.run(
                ["git", "clone", "--quiet", "--no-hardlinks", source_repo, checkout],
                check=True,
                timeout=60,
            )
            # Exercise the working build.sh during the TDD cycle; after commit
            # this is identical to the clone's tracked copy.
            shutil.copy2(
                os.path.join(source_repo, "build.sh"),
                os.path.join(checkout, "build.sh"),
            )
            generator = os.path.join(checkout, "cli", "protocol_generator.rs")
            with open(generator, encoding="utf-8") as handle:
                original = handle.read()

            baseline = self.stamp(checkout)
            with open(generator, "w", encoding="utf-8") as handle:
                handle.write(original + "\n// stamp variant one\n")
            first_dirty = self.stamp(checkout)
            with open(generator, "w", encoding="utf-8") as handle:
                handle.write(original + "\n// stamp variant two\n")
            second_dirty = self.stamp(checkout)
            with open(generator, "w", encoding="utf-8") as handle:
                handle.write(original)
            restored = self.stamp(checkout)

        self.assertNotEqual(first_dirty, second_dirty)
        self.assertIn("-dirty.", first_dirty)
        self.assertIn("-dirty.", second_dirty)
        self.assertEqual(baseline, restored)

    def test_self_boot_uses_checkout_cli_instead_of_stale_path_binary(self):
        with tempfile.TemporaryDirectory(prefix="spirescry-cli-selection-") as repo:
            checkout_cli = os.path.join(repo, "cli", "target", "release", "spirescry")
            os.makedirs(os.path.dirname(checkout_cli))
            with open(checkout_cli, "w", encoding="utf-8") as handle:
                handle.write("checkout cli")
            os.chmod(checkout_cli, 0o755)

            with mock.patch.dict(e2e.os.environ, {}, clear=True), \
                    mock.patch.object(e2e, "REPO", repo), \
                    mock.patch.object(e2e.bridge, "BIN", "spirescry"):
                selected = e2e.configure_cli_for_boot()

                self.assertEqual(selected, checkout_cli)
                self.assertEqual(e2e.bridge.BIN, checkout_cli)
                self.assertEqual(e2e.os.environ["SPIRESCRY_BIN"], checkout_cli)

    def test_self_boot_preserves_explicit_cli_override(self):
        with mock.patch.dict(
                e2e.os.environ, {"SPIRESCRY_BIN": "/tmp/explicit-spirescry"},
                clear=True), mock.patch.object(e2e.bridge, "BIN", "spirescry"):
            selected = e2e.configure_cli_for_boot()

            self.assertEqual(selected, "/tmp/explicit-spirescry")
            self.assertEqual(e2e.bridge.BIN, "/tmp/explicit-spirescry")

    def test_self_boot_fails_fast_when_checkout_cli_is_not_built(self):
        with tempfile.TemporaryDirectory(prefix="spirescry-cli-missing-") as repo, \
                mock.patch.dict(e2e.os.environ, {}, clear=True), \
                mock.patch.object(e2e, "REPO", repo):
            with self.assertRaisesRegex(SystemExit, r"run: ./build\.sh cli"):
                e2e.configure_cli_for_boot()


if __name__ == "__main__":
    unittest.main()
