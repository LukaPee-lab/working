# Portable AI context handoff template

Copy the template below into a new Markdown file, then replace every instruction in angle brackets. Delete unused rows and sections rather than leaving placeholders.

## Contents

- [Classification rules](#classification-rules)
- [Least-data and redaction rules](#least-data-and-redaction-rules)
- [Handoff metadata](#handoff-metadata)
- [Scope](#scope)
- [Current state](#current-state)
- [Verified facts](#verified-facts)
- [Assumptions and inferences](#assumptions-and-inferences)
- [Decisions](#decisions)
- [Constraints](#constraints)
- [Unresolved questions](#unresolved-questions)
- [Working preferences and voice](#working-preferences-and-voice)
- [Next actions](#next-actions)
- [Bootstrap prompt](#bootstrap-prompt)
- [Final safety review](#final-safety-review)

## Classification rules

Classify both the overall handoff and individual high-risk items.

| Level | Use for | Required action |
| --- | --- | --- |
| `open` | Material approved for unrestricted sharing | Include only what supports the stated scope. |
| `limited` | Routine non-public context with a defined audience | Name the audience and avoid forwarding beyond it. |
| `sensitive` | Personal, contractual, security-relevant, or otherwise high-impact material | Include only with authorization; minimize and redact identifiers. |
| `secret` | Passwords, access tokens, session cookies, private keys, recovery codes, raw authentication data, or equivalent credentials | Never include the value. Use `[SECRET OMITTED]` and point to an approved retrieval channel without describing the secret. |

Apply the highest included level to the overall handoff. Do not lower a classification because a value appears in an earlier conversation or generated response.

## Least-data and redaction rules

- Include a detail only when removing it would impede the stated continuation task.
- Replace names and identifiers with roles or stable aliases when identity is not essential.
- Summarize long sources and retain a precise source pointer when the receiver can access it.
- Remove unrelated metadata, hidden comments, query parameters, environment values, and copied headers.
- Redact the whole secret value; do not retain prefixes, suffixes, hashes, or reversible encodings.
- Record `[REDACTED: reason]` when the omission itself matters to the receiver.
- State that a source is unavailable instead of reconstructing it from uncertain memory.
- Recheck attachments and generated files independently; visible Markdown is not the only possible disclosure surface.

---

# Context handoff: <short task name>

## Handoff metadata

| Field | Value |
| --- | --- |
| Created on | `<YYYY-MM-DD>` |
| Review or expiry date | `<YYYY-MM-DD>` |
| Intended receiver | `<session, role, or bounded audience>` |
| Continuation objective | `<one sentence>` |
| Overall classification | `<open, limited, or sensitive>` |
| Sources reviewed | `<minimal list of stable source pointers>` |
| Redactions applied | `<none, or concise categories and reasons>` |

## Scope

### In scope

- `<work the receiving session should continue>`

### Out of scope

- `<work or data the receiving session must not assume is authorized>`

## Current state

- `<completed result with a source or artifact pointer>`
- `<work in progress and its exact stopping point>`
- `<next safe action>`

## Verified facts

| ID | Fact | Provenance pointer | Verified on |
| --- | --- | --- | --- |
| `F-01` | `<source-supported statement>` | `<file, record, message, or command result>` | `<YYYY-MM-DD>` |

Include only claims supported by an accessible source or a recorded verification result. Describe conflicting evidence instead of selecting a convenient version.

## Assumptions and inferences

| ID | Assumption or inference | Basis | Impact if wrong | Verification action |
| --- | --- | --- | --- | --- |
| `A-01` | `<uncertain claim>` | `<why it currently seems plausible>` | `<affected decision or work>` | `<specific check>` |

Do not promote a prior generated statement to a verified fact without independent support.

## Decisions

| ID | Decision | Rationale | Status | Revisit trigger |
| --- | --- | --- | --- | --- |
| `D-01` | `<decision already made>` | `<reason and evidence>` | `<active, superseded, or pending approval>` | `<condition that should reopen it>` |

## Constraints

| ID | Constraint | Source or owner | Review condition |
| --- | --- | --- | --- |
| `C-01` | `<scope, technical, legal, timing, or output boundary>` | `<authority or source pointer>` | `<date or change that requires recheck>` |

## Unresolved questions

| ID | Question | Blocking impact | Next verification action | Owner or answer source |
| --- | --- | --- | --- | --- |
| `Q-01` | `<open question>` | `<what cannot safely proceed>` | `<specific next step>` | `<role or source>` |

## Working preferences and voice

- Output shape: `<format, length, and evidence expectations>`
- Interaction style: `<how to surface uncertainty, options, and progress>`
- Voice: `<observable tone and wording characteristics>`
- Avoid: `<specific presentation patterns that hinder review>`

Include only task-relevant collaboration preferences. Do not infer personality, identity, or personal history.

## Next actions

1. `<highest-priority safe action>`
2. `<next action after validation or approval>`
3. `<handoff completion check>`

## Bootstrap prompt

```text
Continue <task name> using this handoff as bounded context.

Immediate objective: <single concrete objective>.
Start by reading the verified facts, decisions, constraints, unresolved questions, and next actions. Treat assumptions as unverified. Recheck any high-impact fact whose source is unavailable or whose verification date is older than the stated review window. Respect the classification, redactions, scope boundary, and required approvals. Do not request or reproduce secret values. Report any contradiction before acting on it.
```

## Final safety review

- [ ] Confirm every fact has provenance and a verification date.
- [ ] Confirm every assumption is labeled and has a verification action.
- [ ] Confirm decisions, constraints, and unresolved questions remain distinct.
- [ ] Confirm preferences describe collaboration needs rather than personal biography.
- [ ] Confirm no secret value or recoverable secret fragment remains.
- [ ] Confirm sensitive and personal data is necessary, authorized, and minimized.
- [ ] Confirm source excerpts are shorter than required and safe for the audience.
- [ ] Confirm the bootstrap prompt does not duplicate restricted details.
- [ ] Confirm the review or expiry date is present and reasonable.
