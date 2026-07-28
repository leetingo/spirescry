## Agent skills

### Issue tracker

Work is tracked in GitHub Issues for `leetingo/spirescry`; external PRs are not a triage surface. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the canonical `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, and `wontfix` labels. See `docs/agents/triage-labels.md`.

### Domain docs

This is a single-context repository using root `CONTEXT.md` and `docs/adr/`. See `docs/agents/domain.md`.

### Pre-merge gate

Run `./build.sh gate` before merging (~3.5 min on a warm tree; 86 cases, of which the exhaustive content sweeps are ~45s). GitHub-hosted CI cannot build the host — it needs the game's non-distributable dlls — so it never runs the end-to-end suite. A green CI tick is not evidence that e2e passes, and three regressions once sat on `main` because of that gap.

When a rule can be stated over plain values, put it in an engine-free module (`RunOutcomeRules`, `SettlementOutcomeRules`, `CardSpecifier.Encode`) instead of inline in `Snapshotter`, so the unit tests CI *does* run can cover it.
