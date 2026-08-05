# Improvement entry contract

Use one UTF-8 JSON file at `collection/improvement-log.json`. Store evidence inside the workspace and refer to it with paths relative to the workspace root. Keep generated files out of `collection/`.

## Contents

- [Workspace layout](#workspace-layout)
- [Root object](#root-object)
- [Entry object](#entry-object)
- [Field rules](#field-rules)
- [Readiness gate](#readiness-gate)
- [Evidence rules](#evidence-rules)

## Workspace layout

```text
workspace/
  collection/
    improvement-log.json
    evidence/
  build/
    review-ready.md
```

## Root object

```json
{
  "schema_version": 1,
  "workspace": {
    "title": "Fictional storefront improvements",
    "created_on": "2030-01-10",
    "updated_on": "2030-01-10"
  },
  "entries": []
}
```

- Keep `schema_version` equal to `1`.
- Use ISO `YYYY-MM-DD` dates.
- Update `updated_on` whenever the source log changes.
- Keep `entries` as an array, even when empty.

## Entry object

```json
{
  "id": "IMP-001",
  "title": "Confirmation message is easy to miss",
  "observation": {
    "statement": "Three scripted trials ended without the participant noticing the confirmation message.",
    "verification_status": "verified",
    "verification_method": "Reviewed recordings for trials T-01 through T-03.",
    "verified_by": "Research reviewer",
    "verified_on": "2030-01-10",
    "evidence_paths": [
      "collection/evidence/IMP-001-trial-notes.txt"
    ]
  },
  "proposal": {
    "summary": "Move the confirmation message next to the final action and retain it until dismissed.",
    "expected_outcome": "Participants can confirm completion without scanning another region.",
    "tradeoffs": [
      "The persistent message uses additional vertical space."
    ]
  },
  "priority": "high",
  "owner": "Interface team",
  "status": "review-ready"
}
```

## Field rules

| Field | Required values and meaning |
| --- | --- |
| `id` | Use a unique identifier matching `IMP-` plus at least three digits. Never recycle an identifier. |
| `title` | State the opportunity in one non-empty line. |
| `observation.statement` | Describe the observed condition without prescribing a solution. |
| `observation.verification_status` | Use `pending`, `verified`, or `disproved`. |
| `observation.verification_method` | Record how the claim was checked. Require a non-empty value for `verified` or `disproved` records. |
| `observation.verified_by` | Record a role or accountable reviewer. Require a non-empty value for `verified` or `disproved` records. |
| `observation.verified_on` | Use an ISO date. Require it for `verified` or `disproved` records. |
| `observation.evidence_paths` | Use an array of relative, in-workspace file paths. Require at least one existing file for `verified`, `disproved`, or gated records. |
| `proposal.summary` | Describe the proposed change. Allow an empty value only while collecting. |
| `proposal.expected_outcome` | Describe a testable result. Allow an empty value only while collecting. |
| `proposal.tradeoffs` | Use an array of concise strings; use an empty array when none are known. |
| `priority` | Use `critical`, `high`, `medium`, `low`, or `untriaged`. |
| `owner` | Record an accountable role or group. Use `Unassigned` while collecting. |
| `status` | Use `collecting`, `review-ready`, `approved`, `in-progress`, `done`, `deferred`, or `rejected`. |

## Readiness gate

Treat `review-ready`, `approved`, `in-progress`, and `done` as gated states. Require every gated record to have:

- a `verified` observation;
- a non-empty verification method, verifier, and verification date;
- at least one valid evidence path whose file exists;
- a non-empty proposal and expected outcome;
- a ranked priority other than `untriaged`; and
- an owner other than `Unassigned`.

Include only records whose status is exactly `review-ready` in `build/review-ready.md`. Preserve other states in the source log for traceability.

## Evidence rules

- Copy or author only evidence that may be retained in the workspace.
- Prefer the smallest sufficient excerpt or artifact.
- Name evidence files with the entry identifier when practical.
- Reject absolute paths and any path that escapes the workspace.
- Keep sensitive information out unless it is necessary and authorized; redact unrelated details before collection.
