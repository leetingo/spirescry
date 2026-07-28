# Issue tracker: GitHub

Issues and PRDs for this repo live as GitHub issues. Use the `gh` CLI for all operations.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body "..."`.
- **Read an issue**: `gh issue view <number> --comments`, including its labels.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments`, with appropriate label and state filters.
- **Comment on an issue**: `gh issue comment <number> --body "..."`.
- **Apply or remove labels**: `gh issue edit <number> --add-label "..."` or `--remove-label "..."`.
- **Close an issue**: `gh issue close <number> --comment "..."`.

Infer the repository from `git remote -v`; `gh` does this automatically when run inside this clone.

## Pull requests as a triage surface

**PRs as a request surface: no.** External PRs do not enter the issue triage queue.

## Skill operations

- When a skill says to publish to the issue tracker, create a GitHub issue.
- When a skill says to fetch a ticket, run `gh issue view <number> --comments`.
- Apply the configured `ready-for-agent` label to agent-grabbable tickets.

## Blocking relationships

Prefer GitHub's native issue dependencies. Add an edge through the issue dependencies API using the blocker's numeric database ID, not its issue number or node ID. If native dependencies are unavailable, add a `Blocked by: #<number>` line to the dependent issue body. An issue is unblocked once all referenced blockers are closed.
