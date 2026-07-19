#!/usr/bin/env python3
"""Artifact-consumer contract tests: Python constants and checked docs."""

import re
import unittest

import protocol


class ProtocolConsumerContract(unittest.TestCase):
    def test_python_view_is_loaded_from_the_checked_artifact(self):
        self.assertEqual(protocol.PROTOCOL_VERSION, protocol.DOCUMENT["protocolVersion"])
        self.assertEqual(protocol.REJECTION_CODES, tuple(protocol.DOCUMENT["rejectionCodes"]))
        self.assertEqual(protocol.PHASES, tuple(protocol.DOCUMENT["phases"]))
        self.assertEqual(
            protocol.FAULT_EVENT_TOKENS,
            protocol.DOCUMENT["faultEventTokens"],
        )
        self.assertEqual(
            protocol.CHEAT_ARGUMENT_SHAPES,
            tuple(protocol.DOCUMENT["cheatArgumentShapes"]),
        )

    def test_python_namespaces_are_derived_from_artifact_tokens(self):
        for namespace, tokens in (
            (protocol.PHASE, protocol.PHASES),
            (protocol.REJECTION, protocol.REJECTION_CODES),
            (protocol.FAULT_EVENT, protocol.FAULT_EVENT_TOKENS.values()),
        ):
            for token in tokens:
                name = re.sub(r"[^A-Za-z0-9]+", "_", token).strip("_").upper()
                self.assertEqual(getattr(namespace, name), token)

    def test_protocol_documentation_tables_match_the_artifact(self):
        protocol.check_documentation()


if __name__ == "__main__":
    unittest.main()
