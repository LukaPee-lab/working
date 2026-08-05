---
name: interactive-checklist-builder
description: Build portable, responsive single-file HTML checklists from structured JSON. Use when Codex needs to turn a recurring procedure, readiness review, audit list, release gate, or other checkbox-based workflow into an accessible artifact with progress tracking, browser-local persistence, reset and copy controls, and optional URL-hash state sharing.
---

# Interactive Checklist Builder

Create the checklist through the bundled generator. Do not hand-edit generated HTML; revise the JSON and rebuild so escaping, identifiers, storage, and controls stay consistent.

## Workflow

1. Read [references/input-format.md](references/input-format.md) before authoring input.
2. Translate the requested process into concise sections and atomic items. Preserve supplied wording and order when they are requirements.
3. Treat every title, description, and item string as untrusted content. Put content only in the JSON fields documented by the reference; do not add HTML or JavaScript fragments.
4. Build the artifact:

   ```shell
   python scripts/build_checklist.py input.json output.html
   ```

   Add `--force` only when replacing the intended output.
5. Open the HTML locally and verify keyboard operation, responsive layout, progress updates, reset behavior, persistence after reload, and copy behavior. When hash sharing is enabled, verify that the copied URL restores the same checked items.
6. Deliver the source JSON with the generated HTML when future edits are likely.

## Guardrails

- Keep the output self-contained. The generator embeds all CSS and JavaScript and makes no network requests.
- Use stable explicit item IDs when shared links must remain valid across later text edits or reordering.
- Do not add inline event-handler attributes such as `onclick`; the generator binds events with `addEventListener`.
- Do not place secrets or sensitive completion state in a share-enabled checklist. URL hashes are visible to anyone who receives the link.
- Do not claim browser compatibility from source inspection alone. Exercise the generated file in a browser when a browser is available.
