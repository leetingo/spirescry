#!/usr/bin/env python3
"""Every e2e case id addresses exactly one case.

A case id is a handle: `--only P14`, and the name cited in an issue or a
commit message. Two cases claiming the same id makes both unaddressable
(`--only` matches by prefix, so it selects the pair) and the id ambiguous
in prose — and, because each branch is gated in isolation, a collision is
only discovered when the branches are integrated. It has happened twice.

So the collision is refused at registration, and this test holds that
refusal in place: a value test, no host, runs in CI and in the gate.
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import e2e


class CaseIdTests(unittest.TestCase):
    def test_every_registered_case_id_is_unique(self):
        seen = {}
        for name, _, _, _ in e2e.CASES:
            cid = e2e.case_id(name)
            self.assertNotIn(
                cid, seen,
                f"duplicate e2e case id {cid!r}: {seen.get(cid)!r} and {name!r}")
            seen[cid] = name

    def test_registering_a_duplicate_id_is_refused(self):
        taken = e2e.case_id(e2e.CASES[0][0])
        before = list(e2e.CASES)
        try:
            with self.assertRaises(ValueError) as caught:
                e2e.case(f"{taken} a second case claiming a taken id")(
                    lambda: None)
        finally:
            e2e.CASES[:] = before
        message = str(caught.exception)
        # The message has to name the id and both titles, or the next person
        # to hit this has to go hunting for the other half of the collision.
        self.assertIn(taken, message)
        self.assertIn(e2e.CASES[0][0], message)
        self.assertIn("a second case claiming a taken id", message)

    def test_a_free_id_still_registers(self):
        before = list(e2e.CASES)
        try:
            e2e.case("ZZ9 a case with an id nothing else claims")(lambda: None)
            self.assertEqual(e2e.CASES[-1][0],
                             "ZZ9 a case with an id nothing else claims")
        finally:
            e2e.CASES[:] = before

    def test_an_exact_id_selects_exactly_one_case(self):
        # Prefix selection alone cannot do this: `--only P1` would drag in
        # P10..P19, so no case in a numbered family could be run on its own.
        for name, _, _, _ in e2e.CASES:
            cid = e2e.case_id(name)
            picked = [other for other, _, _, _ in e2e.CASES
                      if e2e.selects(other, [cid])]
            self.assertEqual(picked, [name], f"--only {cid} selected {picked}")

    def test_a_non_id_pattern_still_selects_a_family(self):
        picked = [name for name, _, _, _ in e2e.CASES
                  if e2e.selects(name, ["M"])]
        self.assertTrue(len(picked) > 1, picked)
        self.assertTrue(all(name.startswith("M") for name in picked), picked)

    def test_no_selection_runs_everything(self):
        self.assertTrue(all(e2e.selects(name, None) for name, _, _, _ in e2e.CASES))


if __name__ == "__main__":
    unittest.main()
