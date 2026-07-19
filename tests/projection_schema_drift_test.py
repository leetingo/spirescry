#!/usr/bin/env python3
"""Compile-time contract between protocol.json and the Rust projection."""

import json
import os
import shutil
import subprocess
import tempfile
import unittest


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class ProjectionSchemaDriftTests(unittest.TestCase):
    def test_csharp_consumers_do_not_copy_consumed_wire_literals(self):
        with open(os.path.join(REPO, "protocol.json"), encoding="utf-8") as handle:
            document = json.load(handle)
        with open(
            os.path.join(REPO, "src", "State", "SnapshotContract.cs"),
            encoding="utf-8",
        ) as handle:
            source = handle.read()

        implementation = source.split(
            "internal sealed class SnapshotContract", maxsplit=1
        )[1]
        wires = {
            field["wire"]
            for fields in document["consumerProjection"].values()
            for field in fields
        }
        wires.update(("rev", "runId"))

        copied = [wire for wire in sorted(wires) if json.dumps(wire) in implementation]
        self.assertEqual(
            [],
            copied,
            "consumed wire strings must appear only in SnapshotConsumerSchema",
        )

    def checkout_with(self, edit):
        root = tempfile.mkdtemp(prefix="spirescry-projection-drift-")
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        shutil.copytree(
            os.path.join(REPO, "cli"),
            os.path.join(root, "cli"),
            ignore=shutil.ignore_patterns("target"),
        )
        with open(os.path.join(REPO, "protocol.json"), encoding="utf-8") as handle:
            document = json.load(handle)
        edit(document)
        with open(os.path.join(root, "protocol.json"), "w", encoding="utf-8") as handle:
            json.dump(document, handle)
        return root

    def cargo(self, root, *args):
        env = dict(os.environ)
        env["CARGO_TARGET_DIR"] = os.path.join(root, "target")
        return subprocess.run(
            [
                "cargo",
                args[0],
                "--manifest-path",
                os.path.join(root, "cli", "Cargo.toml"),
                *args[1:],
            ],
            capture_output=True,
            text=True,
            timeout=240,
            env=env,
        )

    def test_wire_rename_is_adopted_without_editing_rust_source(self):
        def rename(document):
            model = next(
                field for field in document["consumerProjection"]["item"]
                if field["symbol"] == "model"
            )
            model["wire"] = "cardModel"

        root = self.checkout_with(rename)
        result = self.cargo(
            root,
            "test",
            "tests::replay_projection_uses_generated_item_model_wire_key",
            "--",
            "--exact",
        )

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_output_rename_is_adopted_without_editing_rust_source(self):
        def rename(document):
            model = next(
                field for field in document["consumerProjection"]["item"]
                if field["symbol"] == "model"
            )
            model["output"] = "cardIdentity"

        root = self.checkout_with(rename)
        result = self.cargo(
            root,
            "test",
            "tests::replay_projection_uses_generated_item_model_wire_key",
            "--",
            "--exact",
        )

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_schema_field_removal_breaks_the_rust_compile_contract(self):
        def remove(document):
            document["consumerProjection"]["item"] = [
                field for field in document["consumerProjection"]["item"]
                if field["symbol"] != "model"
            ]

        root = self.checkout_with(remove)
        result = self.cargo(root, "check")

        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("PROJECTION_ITEM_MODEL", result.stderr)


if __name__ == "__main__":
    unittest.main()
