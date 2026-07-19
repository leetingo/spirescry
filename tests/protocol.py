"""Read-only Python view of the checked protocol vocabulary artifact."""

import json
import os
import re
from types import SimpleNamespace


ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARTIFACT = os.path.join(ROOT, "protocol.json")

with open(ARTIFACT, encoding="utf-8") as artifact_file:
    DOCUMENT = json.load(artifact_file)

PROTOCOL_VERSION = DOCUMENT["protocolVersion"]
REJECTION_CODES = tuple(DOCUMENT["rejectionCodes"])
PHASES = tuple(DOCUMENT["phases"])
FAULT_EVENT_TOKENS = DOCUMENT["faultEventTokens"]
CHEAT_ARGUMENT_SHAPES = tuple(DOCUMENT["cheatArgumentShapes"])


def _namespace(values):
    """Expose artifact tokens as generated attribute names (MAP, BAD_REQUEST)."""
    pairs = [
        (re.sub(r"[^A-Za-z0-9]+", "_", value).strip("_").upper(), value)
        for value in values
    ]
    assert len(pairs) == len(dict(pairs)), \
        f"protocol tokens collapse to duplicate Python names: {pairs}"
    return SimpleNamespace(**dict(pairs))


PHASE = _namespace(PHASES)
REJECTION = _namespace(REJECTION_CODES)
FAULT_EVENT = _namespace(FAULT_EVENT_TOKENS.values())


def _marked(path, name):
    with open(os.path.join(ROOT, path), encoding="utf-8") as source:
        text = source.read()
    start = f"<!-- protocol:{name}:start -->"
    end = f"<!-- protocol:{name}:end -->"
    assert text.count(start) == 1 and text.count(end) == 1, \
        f"{path}: expected one {name} marker pair"
    return text.split(start, 1)[1].split(end, 1)[0]


def _table_first_column(section):
    return tuple(
        match.group(1)
        for line in section.splitlines()
        if (match := re.match(r"^\s*\|\s*`([^`]+)`\s*\|", line))
    )


def _shape_signature(shape):
    parts = []
    for argument in shape["arguments"]:
        value = f'{argument["name"]}:{argument["type"]}'
        parts.append(f"[{value}]" if argument.get("optional") else value)
    return " ".join(parts) or "—"


def check_documentation():
    """Fail when checked protocol tables drift from protocol.json."""
    readme_phases = tuple(re.findall(
        r"`([^`]+)`", _marked("README.md", "phases")))
    assert readme_phases == PHASES, \
        f"README phases drift: {readme_phases} != {PHASES}"

    readme_rejections = _table_first_column(
        _marked("README.md", "rejection-codes"))
    assert readme_rejections == REJECTION_CODES, \
        f"README rejection codes drift: {readme_rejections} != {REJECTION_CODES}"

    cheat_rows = []
    for line in _marked("README.md", "cheat-argument-shapes").splitlines():
        match = re.match(
            r"^\s*\|\s*`([^`]+)`\s*\|\s*([^|]+?)\s*\|$", line)
        if match:
            cheat_rows.append(
                (match.group(1), match.group(2).strip().replace("`", "")))
    cheat_names = [name for name, _ in cheat_rows]
    assert len(cheat_names) == len(set(cheat_names)), \
        "README cheat argument table contains duplicates"
    expected_cheats = {
        shape["name"]: _shape_signature(shape) for shape in CHEAT_ARGUMENT_SHAPES
    }
    assert dict(cheat_rows) == expected_cheats, \
        f"README cheat shapes drift: {cheat_rows} != {expected_cheats}"

    skill_phases = _table_first_column(
        _marked(".claude/skills/play-sts2/SKILL.md", "phases"))
    assert len(skill_phases) == len(set(skill_phases)), \
        "play skill phase table contains duplicates"
    assert set(skill_phases) == set(PHASES), \
        f"play skill phases drift: {skill_phases} != {PHASES}"

    skill_faults = tuple(re.findall(
        r"`([^`]+)`",
        _marked(".claude/skills/play-sts2/SKILL.md", "fault-event-tokens"),
    ))
    expected_faults = tuple(FAULT_EVENT_TOKENS.values())
    assert skill_faults == expected_faults, \
        f"play skill fault tokens drift: {skill_faults} != {expected_faults}"
