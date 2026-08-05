# Checklist input format

Provide one UTF-8 JSON object. Unknown fields are rejected so misspellings cannot silently change the result.

## Root object

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `title` | non-empty string | yes | Page and checklist title. |
| `description` | string | no | Introductory text shown below the title. |
| `language` | BCP 47-style tag | no | HTML language tag; defaults to `en`. |
| `storage_key` | string matching `[A-Za-z0-9._:-]{1,100}` | no | Browser storage namespace. A deterministic key is generated when omitted. |
| `share_url_hash` | boolean | no | Store checked item IDs in the URL hash and include them when copying the link. Defaults to `false`. |
| `sections` | array of section objects | yes | One or more ordered sections. |

## Section object

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | safe ID string | no | Stable section ID. |
| `title` | non-empty string | yes | Section heading. |
| `description` | string | no | Short section guidance. |
| `items` | array of item objects | yes | One or more ordered checklist items. |

## Item object

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `id` | safe ID string | no | Stable, document-wide unique item ID. Use one when links must survive edits. |
| `text` | non-empty string | yes | The action or verification statement. |
| `details` | string | no | Supporting context shown beneath the item. |
| `required` | boolean | no | Display a required marker. Defaults to `false`. |

A safe ID starts with an ASCII letter or digit, contains only letters, digits, `_`, or `-`, and is at most 64 characters. Generated IDs are deterministic for the current ordering. Explicit duplicate item IDs are rejected.

## Example

```json
{
  "title": "Community room opening check",
  "description": "Complete before visitors arrive.",
  "language": "en",
  "storage_key": "community-room-opening-v1",
  "share_url_hash": true,
  "sections": [
    {
      "id": "room",
      "title": "Room setup",
      "items": [
        {
          "id": "walkways",
          "text": "Confirm that walkways are clear.",
          "required": true
        },
        {
          "id": "signage",
          "text": "Place the welcome sign near the entrance.",
          "details": "Keep the accessible route unobstructed."
        }
      ]
    }
  ]
}
```

Text fields are rendered as text, never executable markup. Newlines are preserved visually. Browser persistence is device-local. When `share_url_hash` is `true`, checked item IDs are encoded in the fragment after `#`; the fragment does not contain checklist text.
