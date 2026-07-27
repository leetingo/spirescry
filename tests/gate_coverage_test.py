#!/usr/bin/env python3
"""The gate/CI split must stay honest.

CI cannot boot the host, so `./build.sh gate` is the only place the M1–M4
sweeps ever run. `--quick` exists for the local iteration loop; the day it
creeps back into gate() the sweeps stop running and nothing else notices.
This test reads the real invocation out of build.sh, feeds it through
e2e's own argument parser and skip rule, and asserts the sweeps survive.

The other half of the split: everything the gate runs that does *not* need
a host is a plain value test, so CI must run it too. A test added to only
one of the two lists is a hole, and the two lists drift silently.
"""
import inspect
import os
import re
import shlex
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import e2e
import sweeps

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILD_SH = os.path.join(REPO, "build.sh")
WORKFLOW = os.path.join(REPO, ".github/workflows/unit-tests.yml")
SWEEP_CALL = re.compile(r"""run_test_script\(\s*["']sweeps\.py["']\s*,\s*["'](\w+)["']""")
PY_TEST_LOOP = re.compile(r"""for t in (.+?); do\s*\n\s*python3 "tests/\$t\.py\"""", re.S)
PY_TEST_CALL = re.compile(r"python3 tests/(\w+)\.py")


def gate_body(script):
    """The body of build.sh's gate() — flags outside it say nothing about
    what the gate runs."""
    lines = script.splitlines()
    start = next(i for i, line in enumerate(lines) if line.startswith("gate() {"))
    end = next(i for i in range(start + 1, len(lines)) if lines[i] == "}")
    return "\n".join(lines[start + 1:end])


def gate_python_unit_tests(body):
    """The python unit tests build.sh's gate() loops over."""
    loop = PY_TEST_LOOP.search(body)
    assert loop, "gate() no longer has a `for t in ...; do python3 tests/$t.py` loop"
    return set(loop.group(1).replace("\\\n", " ").split())


def gate_e2e_argv(body):
    """The flags build.sh's gate() hands tests/e2e.py."""
    argvs = []
    for line in body.splitlines():
        head, sep, tail = line.partition("tests/e2e.py")
        if sep and not head.lstrip().startswith("#"):
            argvs.append(shlex.split(tail))
    return argvs


class GateCoverageTests(unittest.TestCase):
    def setUp(self):
        with open(BUILD_SH) as f:
            self.body = gate_body(f.read())
        argvs = gate_e2e_argv(self.body)
        self.assertEqual(len(argvs), 1,
                         f"expected exactly one e2e invocation in gate(): {argvs}")
        self.argv = argvs[0]
        self.args = e2e.build_parser().parse_args(self.argv)

    def gate_runs(self, name, boot_only, deep):
        return e2e.skip_reason(boot_only, deep,
                               boot=self.args.boot, quick=self.args.quick) is None \
            and (not self.args.only
                 or any(name.startswith(p) for p in self.args.only.split(",")))

    def test_gate_does_not_pass_quick(self):
        self.assertNotIn("--quick", self.argv,
                         "gate() runs e2e --quick — the exhaustive sweeps would "
                         "be skipped and CI cannot run them either")
        self.assertFalse(self.args.quick)

    def test_gate_runs_every_deep_case(self):
        deep = [name for name, b, d, _ in e2e.CASES if d]
        self.assertTrue(deep, "no deep cases left in e2e.py to protect")
        skipped = [name for name, b, d, _ in e2e.CASES
                   if d and not self.gate_runs(name, b, d)]
        self.assertEqual(skipped, [],
                         f"gate() would skip exhaustive cases: {skipped}")

    def test_gate_covers_every_sweep_kind(self):
        covered = set()
        for name, boot_only, deep, fn in e2e.CASES:
            if not self.gate_runs(name, boot_only, deep):
                continue
            covered.update(SWEEP_CALL.findall(inspect.getsource(fn)))
        self.assertEqual(covered, set(sweeps.SWEEPS),
                         "the gate's e2e run does not cover every sweep in "
                         "tests/sweeps.py")

    def test_gate_boots_its_own_host(self):
        # Without --boot the whole suite degrades to whatever bridge happens
        # to answer, and the boot-only cases silently drop out.
        self.assertTrue(self.args.boot, "gate() must run e2e with --boot")

    def test_e2e_selects_cases_through_the_rule_this_test_asks(self):
        # Everything above reasons about the gate through build_parser() and
        # skip_reason(). If main() ever stops routing through them — inlining
        # its own skip again — those answers describe a rule nothing runs, and
        # this file would keep passing while the gate quietly narrowed.
        source = inspect.getsource(e2e.main)
        for fn in ("build_parser(", "skip_reason("):
            self.assertIn(fn, source,
                          f"e2e.main() no longer calls {fn.rstrip('(')}() — "
                          "gate_coverage_test would be asserting about dead code")

    def test_ci_runs_every_python_unit_test_the_gate_runs(self):
        # The gate is the CI set plus the host-only e2e suite. Its python unit
        # tests are all pure value tests, so a name in one list and not the
        # other is a test that only runs where someone remembers to look.
        with open(WORKFLOW) as f:
            ci = set(PY_TEST_CALL.findall(f.read()))
        self.assertEqual(gate_python_unit_tests(self.body), ci,
                         "build.sh gate() and .github/workflows/unit-tests.yml "
                         "run different python unit tests")


if __name__ == "__main__":
    unittest.main()
