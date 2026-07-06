# Architecture Decision Records (ADRs)

Short, dated notes capturing the **why** behind load-bearing decisions in this project. They exist so future contributors (and future-us) don't re-litigate decisions whose context has been lost.

## Reading order

If you only read two, read `0001` and `0002` — they define the shape of the whole system.

| # | Title | Status |
|---|---|---|
| [0001](0001-in-process-http-bridge.md) | In-process HTTP bridge inside Mission Planner | Accepted |
| [0002](0002-mp-owns-metadata.md) | Mission Planner owns parameter metadata | Accepted |
| [0003](0003-dual-transport-stdio-http.md) | MCP server supports both stdio and Streamable HTTP | Accepted |
| [0004](0004-drop-changed-from-default.md) | Drop `changed_from_default` / `default` from MVP | Accepted |
| [0005](0005-parameter-write-path.md) | Parameter write path | Accepted |

## Format

Each ADR follows the same skeleton. Keep them short — **aim for under 100 lines.** The value is capturing the reasoning concisely, not producing a polished essay.

```markdown
# NNNN. Title

- **Status:** Proposed | Accepted | Superseded by ADR-XXXX
- **Date:** YYYY-MM-DD

## Context

What problem we're solving. What constraints exist. What was already true
about the system that forced the decision.

## Decision

What we chose. In present tense. The tradeoffs we accepted.

## Consequences

Positive, negative, and neutral follow-on effects. Be honest about the
downsides — an ADR that only lists upsides is a sales pitch, not a decision.

## Alternatives considered

The options that didn't win, and why. Brief — one paragraph each.
```

## When to write a new ADR

Write one when you're about to make a decision that:

- Changes the shape of the HTTP contract (`bridge_api_version` bump territory).
- Changes the MCP tool / resource surface.
- Picks between two reasonable technical options where the choice will be hard to reverse.
- Reverses or supersedes an earlier ADR (mark the old one `Superseded by ADR-XXXX` and link).

Do **not** write an ADR for:

- Bug fixes.
- Refactors that don't change external behavior.
- Small tactical choices ("use a Map instead of a plain object"). Those belong in code comments or PR descriptions.

## How to add one

1. Pick the next unused number.
2. Copy an existing ADR as a template.
3. Add a row to the table above.
4. Commit as part of the same PR that implements the decision (or as a standalone commit if you're recording a decision that was already made informally).
