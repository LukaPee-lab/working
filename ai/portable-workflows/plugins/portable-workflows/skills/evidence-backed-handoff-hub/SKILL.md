---
name: evidence-backed-handoff-hub
description: Build and validate a self-contained offline HTML handoff hub from structured JSON evidence. Use for project transitions, operational handoffs, system overviews, audit-ready knowledge packages, evidence inventories, open-question registers, and optional data-table relationship maps that must remain portable and traceable.
---

# Evidence-Backed Handoff Hub

## Follow the workflow

1. Gather facts, source labels, unresolved questions, and system explanations.
2. Separate verified observations from inference or unverified claims through explicit evidence statuses.
3. Read [references/data-contract.md](references/data-contract.md) before authoring the input JSON.
4. Assign stable, globally unique IDs to every evidence item, section, question, table, and relationship.
5. Link each system section to its supporting evidence through `evidence_ids`.
6. Add `tables` and `relationships` only when the handoff describes a data model.
7. Validate the JSON before building.
8. Build the HTML, then validate both the JSON and produced HTML.
9. Open the HTML without a network connection and inspect the rendered hierarchy, labels, and relationship endpoints.

## Prepare the JSON

Keep the input factual and compact. Preserve source wording in evidence summaries, but write system sections for a reader who lacks prior project context.

Use these commands from the skill directory:

```text
python scripts/validate_handoff.py INPUT.json
python scripts/build_handoff_hub.py INPUT.json OUTPUT.html
python scripts/validate_handoff.py INPUT.json --html OUTPUT.html
```

Treat validation errors as blockers. Correct the input instead of editing the generated HTML by hand.

## Qualify evidence

- Record a human-readable `source` for every evidence item.
- Record a meaningful `status` for every evidence item, such as `verified`, `observed`, `inferred`, or `unverified`.
- Keep source locations as text; do not rely on remote links being available.
- State what the evidence supports in `summary` and put supporting specifics in `details`.
- Avoid presenting an inference as a verified fact.

## Structure the handoff

- Write `overview` as the shortest complete orientation for the receiving reader.
- Organize `sections` around systems, workflows, responsibilities, or decisions.
- Use `evidence_ids` to make each section traceable.
- Preserve unresolved items in `open_questions`, including ownership and status when known.
- Describe tables and fields only when they help explain operational or data impact.
- Define every relationship endpoint as `table-id` or `table-id.field-name`.

## Review the output

- Confirm that the overview, evidence cards, system sections, and open questions are present.
- Confirm that every evidence reference resolves to the intended card.
- Confirm that relationship endpoints name existing tables and fields.
- Confirm that untrusted text appears as text rather than executable markup.
- Confirm that the HTML contains no scripts or externally loaded resources.
- Deliver the source JSON with the HTML so future maintainers can rebuild it deterministically.
