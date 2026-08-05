# Handoff data contract

Store one UTF-8 JSON object in the input file. Keep all IDs globally unique and case-sensitive.

## Root fields

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `title` | string | yes | Non-empty hub title. |
| `overview` | string | yes | Non-empty orientation text. |
| `evidence` | array | yes | One or more evidence objects. |
| `sections` | array | yes | One or more system-section objects. |
| `open_questions` | array | yes | May be empty. |
| `subtitle` | string | no | Short supporting title. |
| `last_updated` | string | no | Display label; use an unambiguous date when supplied. |
| `tables` | array | no | Data-table objects. |
| `relationships` | array | no | Relationship objects. |

## Object shapes

- Evidence: `id`, `title`, `source`, `status`, and `summary` are non-empty strings. `details` is an optional array of strings.
- Section: `id`, `title`, and `summary` are non-empty strings. `details` is an optional array of strings. `evidence_ids` is a required array of existing evidence IDs.
- Open question: `id`, `question`, and `status` are non-empty strings. `context` and `owner` are optional strings.
- Table: `id`, `name`, and `description` are non-empty strings. `fields` is a required array of objects with non-empty `name` and `description` strings. Table IDs and field names must not contain `.`; field names must be unique within a table.
- Relationship: `id`, `from`, `to`, and `label` are non-empty strings. Use `table-id` or `table-id.field-name` for `from` and `to`; each referenced table and field must exist.

Unknown root or object fields are ignored so producers may retain extra metadata. The builder renders all supplied text as escaped text and never treats source labels as links or markup.

## Example

```json
{
  "title": "Signal Relay Service Handoff",
  "subtitle": "Operational orientation for the next maintenance team",
  "last_updated": "2030-04-12",
  "overview": "The service accepts device signals, normalizes them, and records delivery attempts for later review.",
  "evidence": [
    {
      "id": "ev-runbook",
      "title": "Recovery procedure",
      "source": "Operations runbook, revision 7",
      "status": "verified",
      "summary": "The documented restart sequence was exercised in a staging environment.",
      "details": ["The health check recovered after the worker restart."]
    }
  ],
  "sections": [
    {
      "id": "sec-delivery",
      "title": "Delivery workflow",
      "summary": "A worker validates each signal and records every delivery attempt.",
      "details": ["Failed attempts remain available for controlled replay."],
      "evidence_ids": ["ev-runbook"]
    }
  ],
  "open_questions": [
    {
      "id": "q-retention",
      "question": "What retention period should apply to completed attempts?",
      "context": "The current runbook does not state a duration.",
      "owner": "Platform operations",
      "status": "open"
    }
  ],
  "tables": [
    {
      "id": "signals",
      "name": "Signals",
      "description": "Normalized inbound signal records.",
      "fields": [
        {"name": "id", "description": "Stable signal identifier."}
      ]
    },
    {
      "id": "attempts",
      "name": "Delivery attempts",
      "description": "Outcome history for each signal.",
      "fields": [
        {"name": "signal_id", "description": "References the source signal."}
      ]
    }
  ],
  "relationships": [
    {
      "id": "rel-signal-attempts",
      "from": "attempts.signal_id",
      "to": "signals.id",
      "label": "attempt belongs to signal"
    }
  ]
}
```
