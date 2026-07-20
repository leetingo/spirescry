#!/usr/bin/env python3
"""Public text contract for play-sts2 forensic and revival guidance."""

from pathlib import Path
import unittest


REPO = Path(__file__).resolve().parents[1]
SKILL = (REPO / ".claude/skills/play-sts2/SKILL.md").read_text(encoding="utf-8")


class PlaySkillFaultProtocolTest(unittest.TestCase):
    def test_fault_bundle_is_the_first_step_for_every_ambiguous_follow(self):
        self.assertIn("spirescry fault-bundle > run-<seed>-fault.json", SKILL)
        self.assertIn("non-empty follow `errors`", SKILL)
        self.assertIn("transport failure after acceptance", SKILL)
        self.assertIn("impossible observation", SKILL)

        impossible = SKILL.split("## Impossible observations", 1)[1]
        first_bundle = impossible.index("spirescry fault-bundle")
        self.assertLess(first_bundle, impossible.index("spirescry health"))
        self.assertLess(first_bundle, impossible.index("abandon"))

    def test_ledger_consumes_companion_and_one_shot_relic_fields(self):
        ledger = SKILL.split("## The ledger", 1)[1].split("## Wedge recovery", 1)[0]
        normalized = " ".join(ledger.split())
        self.assertIn("you.osty", normalized)
        self.assertIn("hp [current, max] / block / alive / powers", normalized)
        self.assertIn("you.relics[].usedUp", normalized)
        self.assertIn("player.relicStates[].usedUp", normalized)
        self.assertIn("`usedUp: false` means available", normalized)
        self.assertIn("`usedUp: true` means consumed", normalized)
        self.assertIn("Never decode `semanticState`", normalized)
        self.assertIn("never infer Osty's HP from a card preview", normalized)

    def test_terminal_guidance_waits_for_stable_post_settlement_observation(self):
        terminal = SKILL.split("## Terminal and revival stability", 1)[1]
        normalized = " ".join(terminal.split())
        self.assertIn("stable current `obs` after settlement", normalized)
        self.assertIn("phase event is evidence of progress, not a terminal result", normalized)
        self.assertIn("### Worked revival sequence: Queen + Lizard Tail", normalized)
        self.assertIn("`usedUp: false`", normalized)
        self.assertIn("revives to 24 HP", normalized)
        self.assertIn("`usedUp: true`", normalized)
        self.assertIn("later genuine defeat", normalized)


if __name__ == "__main__":
    unittest.main()
