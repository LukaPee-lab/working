---
name: product-improvement-workbench
description: Collect evidence-backed product improvement opportunities, keep verified observations distinct from proposed changes, and build a review-ready improvement brief. Use for improvement backlogs, usability findings, quality audits, prioritization reviews, ownership handoffs, or any workflow that must preserve evidence and decision state without depending on a specific tracker or connector.
---

# Product Improvement Workbench

## Set up the workspace

1. Read [references/entry-contract.md](references/entry-contract.md) before adding or changing records.
2. Run `python scripts/init_improvement_workspace.py <workspace> --title "<title>"` for a new workspace.
3. Keep authored records and source evidence under `collection/`.
4. Keep generated review material under `build/`.

## Collect findings

1. Record what was observed under `observation`; record the suggested change under `proposal`.
2. Cite workspace-relative evidence paths and preserve enough context for another reviewer to reproduce the observation.
3. Mark an observation `verified` only after recording the method, verifier, date, and at least one available evidence file.
4. Leave uncertain claims `pending`; never present a proposal, expected result, or assumption as an observed fact.
5. Assign priority, owner, and status explicitly. Use `untriaged`, `Unassigned`, and `collecting` when those decisions remain open.

## Prepare review candidates

1. Resolve missing evidence and contradictions before changing a record to `review-ready`.
2. Supply a concrete proposal, expected outcome, assigned owner, and ranked priority.
3. Run `python scripts/validate_improvement_log.py <workspace> --strict` and fix every reported issue.
4. Keep rejected or deferred records in the source log when their decision history remains useful.

## Build the review brief

1. Run `python scripts/validate_improvement_log.py <workspace> --strict --build-review`.
2. Review `build/review-ready.md` against the source evidence.
3. Treat `collection/improvement-log.json` as the source of truth; regenerate rather than hand-editing the built brief.
4. Report validated entry counts, included review candidates, warnings, and the generated output path.
