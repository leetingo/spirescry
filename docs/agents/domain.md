# Domain Docs

This repository uses a single domain context.

## Before exploring

- Read `CONTEXT.md` at the repository root.
- Read ADRs under `docs/adr/` that touch the area being changed.
- If either location does not exist, proceed silently; create domain documentation only when a decision or vocabulary gap needs to be recorded.

## Vocabulary

Use the terms defined in `CONTEXT.md` in issue titles, plans, tests, and implementation notes. Avoid synonyms that the glossary explicitly rejects.

If a required concept is not defined, reconsider whether existing vocabulary already covers it. Record a genuine gap for later domain modeling rather than silently inventing a competing term.

## ADR conflicts

If proposed work conflicts with an existing ADR, call out the conflict explicitly instead of silently overriding the decision.
