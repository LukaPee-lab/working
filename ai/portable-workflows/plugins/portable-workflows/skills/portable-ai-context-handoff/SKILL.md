---
name: portable-ai-context-handoff
description: Create a compact, privacy-aware context package that lets another AI session continue work safely. Use when transferring project state across sessions, models, tools, or collaborators; preparing a restart prompt; preserving decisions and working preferences; or redacting a continuity document before sharing it.
---

# Portable AI Context Handoff

## Define the boundary

1. Identify the receiving session, intended task, permitted audience, and minimum context required to continue.
2. Read [references/handoff-template.md](references/handoff-template.md) and use its section order.
3. Set a review or expiry date appropriate to the rate at which the context changes.

## Classify and minimize

1. Classify each candidate detail as `open`, `limited`, `sensitive`, or `secret`.
2. Omit secrets completely. Replace them with a short redaction marker and a safe retrieval instruction only when continuation requires one.
3. Include sensitive or personal information only when it is necessary, authorized, and reduced to the least identifying form.
4. Prefer summaries and source pointers over raw conversations, logs, or document dumps.
5. Exclude unrelated identities, history, credentials, access data, and speculative biography.

## Separate knowledge types

1. Put source-supported statements under verified facts and attach a provenance pointer plus verification date.
2. Put deductions, uncertain recollections, and unsupported prior output under assumptions; state the impact if each assumption is wrong.
3. Record decisions separately from facts and include rationale, status, and revisit triggers.
4. Record constraints as current boundaries, not as permanent truths.
5. Keep unresolved questions visible and name the next verification action.

## Preserve useful working style

1. Capture only preferences that materially affect collaboration, output, or review.
2. Express voice guidance as observable writing or interaction choices.
3. Omit personal background unless the next session strictly needs it.

## Bootstrap and review

1. Write a bootstrap prompt that points to the handoff sections, states the immediate objective, and requires the receiver to recheck stale or high-impact claims.
2. Avoid duplicating the entire handoff inside the prompt.
3. Scan the final package for credentials, tokens, private keys, session data, personal identifiers, hidden metadata, and unnecessary source excerpts.
4. Confirm that redactions do not preserve recoverable fragments.
5. Mark expired material as stale or remove it before transfer.
6. Run `python scripts/validate_context_handoff.py <handoff.md> --strict` and resolve every reported error or warning before sharing.

The validator checks structure, dates, classification, unresolved template placeholders, completion of the safety checklist, and several high-confidence credential formats. It is a guardrail, not proof that a document contains no confidential information; perform a deliberate human review as the final step.
