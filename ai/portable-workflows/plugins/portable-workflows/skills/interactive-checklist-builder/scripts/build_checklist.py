#!/usr/bin/env python3
"""Build an accessible, self-contained checklist HTML file from JSON."""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import sys
import unicodedata
from pathlib import Path
from string import Template
from typing import Any


ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
STORAGE_KEY_PATTERN = re.compile(r"^[A-Za-z0-9._:-]{1,100}$")
LANGUAGE_PATTERN = re.compile(r"^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$")
ROOT_FIELDS = {"title", "description", "language", "storage_key", "share_url_hash", "sections"}
SECTION_FIELDS = {"id", "title", "description", "items"}
ITEM_FIELDS = {"id", "text", "details", "required"}


class InputError(ValueError):
    """Raised when the checklist specification is invalid."""


def reject_unknown_fields(value: dict[str, Any], allowed: set[str], location: str) -> None:
    unknown = sorted(set(value) - allowed)
    if unknown:
        raise InputError(f"{location} contains unknown field(s): {', '.join(unknown)}")


def require_object(value: Any, location: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise InputError(f"{location} must be an object")
    return value


def require_text(value: dict[str, Any], key: str, location: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result.strip():
        raise InputError(f"{location}.{key} must be a non-empty string")
    return result.strip()


def optional_text(value: dict[str, Any], key: str, location: str) -> str:
    result = value.get(key, "")
    if not isinstance(result, str):
        raise InputError(f"{location}.{key} must be a string")
    return result.strip()


def optional_bool(value: dict[str, Any], key: str, default: bool, location: str) -> bool:
    result = value.get(key, default)
    if not isinstance(result, bool):
        raise InputError(f"{location}.{key} must be a boolean")
    return result


def slugify(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode("ascii")
    slug = re.sub(r"[^a-z0-9]+", "-", normalized.lower()).strip("-")
    return slug[:32] or "entry"


def validate_explicit_id(value: Any, location: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or not ID_PATTERN.fullmatch(value):
        raise InputError(
            f"{location} must start with a letter or digit and contain at most 64 "
            "letters, digits, underscores, or hyphens"
        )
    return value


def normalize_spec(raw: Any) -> dict[str, Any]:
    root = require_object(raw, "root")
    reject_unknown_fields(root, ROOT_FIELDS, "root")
    title = require_text(root, "title", "root")
    description = optional_text(root, "description", "root")

    language = root.get("language", "en")
    if not isinstance(language, str) or not LANGUAGE_PATTERN.fullmatch(language):
        raise InputError("root.language must be a simple BCP 47-style language tag")

    share_url_hash = optional_bool(root, "share_url_hash", False, "root")
    sections_raw = root.get("sections")
    if not isinstance(sections_raw, list) or not sections_raw:
        raise InputError("root.sections must be a non-empty array")

    used_section_ids: set[str] = set()
    used_item_ids: set[str] = set()
    sections: list[dict[str, Any]] = []

    for section_index, section_value in enumerate(sections_raw, start=1):
        location = f"root.sections[{section_index - 1}]"
        section = require_object(section_value, location)
        reject_unknown_fields(section, SECTION_FIELDS, location)
        section_title = require_text(section, "title", location)
        requested_section_id = validate_explicit_id(section.get("id"), f"{location}.id")
        section_base = requested_section_id or f"{section_index}-{slugify(section_title)}"
        section_id = f"section-{section_base}"
        if section_id in used_section_ids:
            raise InputError(f"{location}.id duplicates another section ID: {section_base}")
        used_section_ids.add(section_id)

        items_raw = section.get("items")
        if not isinstance(items_raw, list) or not items_raw:
            raise InputError(f"{location}.items must be a non-empty array")

        items: list[dict[str, Any]] = []
        for item_index, item_value in enumerate(items_raw, start=1):
            item_location = f"{location}.items[{item_index - 1}]"
            item = require_object(item_value, item_location)
            reject_unknown_fields(item, ITEM_FIELDS, item_location)
            item_text = require_text(item, "text", item_location)
            requested_item_id = validate_explicit_id(item.get("id"), f"{item_location}.id")
            item_base = requested_item_id or f"{section_index}-{item_index}-{slugify(item_text)}"
            item_id = f"item-{item_base}"
            if item_id in used_item_ids:
                raise InputError(f"{item_location}.id duplicates another item ID: {item_base}")
            used_item_ids.add(item_id)
            items.append(
                {
                    "id": item_id,
                    "control_id": f"control-{item_id}",
                    "text": item_text,
                    "details": optional_text(item, "details", item_location),
                    "required": optional_bool(item, "required", False, item_location),
                }
            )

        sections.append(
            {
                "id": section_id,
                "title": section_title,
                "description": optional_text(section, "description", location),
                "items": items,
            }
        )

    storage_key = root.get("storage_key")
    if storage_key is not None:
        if not isinstance(storage_key, str) or not STORAGE_KEY_PATTERN.fullmatch(storage_key):
            raise InputError(
                "root.storage_key must contain 1-100 letters, digits, periods, underscores, colons, or hyphens"
            )
    else:
        identity = json.dumps(
            {"title": title, "items": [item["id"] for section in sections for item in section["items"]]},
            ensure_ascii=False,
            sort_keys=True,
        ).encode("utf-8")
        storage_key = "interactive-checklist:" + hashlib.sha256(identity).hexdigest()[:20]

    return {
        "title": title,
        "description": description,
        "language": language,
        "storage_key": storage_key,
        "share_url_hash": share_url_hash,
        "sections": sections,
    }


def escaped(value: str) -> str:
    return html.escape(value, quote=True)


def render_sections(sections: list[dict[str, Any]]) -> str:
    rendered: list[str] = []
    for section in sections:
        description = ""
        if section["description"]:
            description = f'<p class="section-description">{escaped(section["description"])}</p>'

        items: list[str] = []
        for item in section["items"]:
            details_id = f'{item["id"]}-details'
            described_by = f' aria-describedby="{escaped(details_id)}"' if item["details"] else ""
            required = ""
            if item["required"]:
                required = (
                    '<span class="required-badge" aria-hidden="true">Required</span>'
                    '<span class="sr-only"> (required)</span>'
                )
            details = ""
            if item["details"]:
                details = f'<p class="item-details" id="{escaped(details_id)}">{escaped(item["details"])}</p>'
            items.append(
                "\n".join(
                    [
                        f'<li class="checklist-item" data-item-row="{escaped(item["id"])}">',
                        '  <div class="item-control">',
                        f'    <input type="checkbox" id="{escaped(item["control_id"])}" '
                        f'data-item-id="{escaped(item["id"])}"{described_by}>',
                        f'    <label for="{escaped(item["control_id"])}">'
                        f'<span class="item-text">{escaped(item["text"])}</span>{required}</label>',
                        "  </div>",
                        f"  {details}" if details else "",
                        "</li>",
                    ]
                )
            )

        rendered.append(
            "\n".join(
                [
                    f'<section class="checklist-section" aria-labelledby="{escaped(section["id"])}-title">',
                    f'  <h2 id="{escaped(section["id"])}-title">{escaped(section["title"])}</h2>',
                    f"  {description}" if description else "",
                    '  <ul class="checklist-items" role="list">',
                    "\n".join(items),
                    "  </ul>",
                    "</section>",
                ]
            )
        )
    return "\n".join(rendered)


HTML_TEMPLATE = Template(
    r'''<!doctype html>
<html lang="$language">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <meta name="color-scheme" content="light dark">
  <title>$title</title>
  <style>
    :root {
      color-scheme: light dark;
      --page: #f4f6f8;
      --surface: #ffffff;
      --surface-soft: #f8fafc;
      --text: #17202a;
      --muted: #5d6875;
      --line: #d7dde4;
      --accent: #2368d1;
      --accent-strong: #174d9e;
      --success: #1c7c54;
      --focus: #f0a500;
      --shadow: 0 12px 32px rgba(20, 31, 45, 0.10);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--page);
      color: var(--text);
      font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      line-height: 1.5;
    }
    button, input { font: inherit; }
    button:focus-visible, input:focus-visible {
      outline: 3px solid var(--focus);
      outline-offset: 3px;
    }
    .page-shell {
      width: min(100% - 2rem, 58rem);
      margin: 0 auto;
      padding: clamp(2rem, 6vw, 4.5rem) 0;
    }
    .hero, .checklist-section {
      background: var(--surface);
      border: 1px solid var(--line);
      border-radius: 1rem;
      box-shadow: var(--shadow);
    }
    .hero { padding: clamp(1.25rem, 4vw, 2.25rem); }
    h1, h2, p { margin-top: 0; }
    h1 {
      margin-bottom: .6rem;
      font-size: clamp(1.8rem, 5vw, 2.75rem);
      line-height: 1.12;
      letter-spacing: -.025em;
    }
    h2 { margin-bottom: .35rem; font-size: clamp(1.2rem, 3vw, 1.55rem); }
    .intro, .section-description, .item-details { color: var(--muted); white-space: pre-line; }
    .intro { margin-bottom: 1.5rem; max-width: 65ch; }
    .progress-grid {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: .75rem 1rem;
      align-items: center;
    }
    progress {
      width: 100%;
      height: .85rem;
      border: 0;
      border-radius: 999px;
      overflow: hidden;
      accent-color: var(--success);
    }
    progress::-webkit-progress-bar { background: var(--line); }
    progress::-webkit-progress-value { background: var(--success); }
    progress::-moz-progress-bar { background: var(--success); }
    #progress-summary { margin: 0; color: var(--muted); font-weight: 650; white-space: nowrap; }
    .actions { display: flex; flex-wrap: wrap; gap: .65rem; margin-top: 1.25rem; }
    button {
      min-height: 2.75rem;
      padding: .65rem 1rem;
      border: 1px solid var(--line);
      border-radius: .7rem;
      background: var(--surface-soft);
      color: var(--text);
      cursor: pointer;
      font-weight: 700;
    }
    button.primary { border-color: var(--accent); background: var(--accent); color: #ffffff; }
    button.primary:hover { background: var(--accent-strong); }
    button:hover { border-color: var(--accent); }
    .sections { display: grid; gap: 1rem; margin-top: 1rem; }
    .checklist-section { padding: clamp(1rem, 3vw, 1.6rem); }
    .checklist-items { display: grid; gap: .7rem; padding: 0; margin: 1rem 0 0; }
    .checklist-item {
      list-style: none;
      padding: 1rem;
      border: 1px solid var(--line);
      border-radius: .8rem;
      background: var(--surface-soft);
      transition: border-color .15s ease, opacity .15s ease;
    }
    .checklist-item.is-complete { border-color: color-mix(in srgb, var(--success) 55%, var(--line)); opacity: .72; }
    .item-control { display: grid; grid-template-columns: 1.4rem minmax(0, 1fr); gap: .8rem; align-items: start; }
    .item-control input { width: 1.25rem; height: 1.25rem; margin: .1rem 0 0; accent-color: var(--success); }
    .item-control label { min-width: 0; cursor: pointer; font-weight: 650; }
    .is-complete .item-text { text-decoration: line-through; text-decoration-thickness: .1em; }
    .required-badge {
      display: inline-block;
      margin-left: .55rem;
      padding: .08rem .42rem;
      border-radius: 999px;
      background: #e8eef9;
      color: #244c85;
      font-size: .72rem;
      font-weight: 800;
      vertical-align: .12rem;
    }
    .item-details { margin: .45rem 0 0 2.2rem; font-size: .93rem; }
    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      padding: 0;
      margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }
    #status-message { min-height: 1.5em; margin: .8rem 0 0; color: var(--muted); }
    noscript { display: block; margin-top: 1rem; color: #8a3d18; }
    @media (max-width: 34rem) {
      .page-shell { width: min(100% - 1rem, 58rem); padding: .5rem 0 2rem; }
      .hero, .checklist-section { border-radius: .8rem; }
      .progress-grid { grid-template-columns: 1fr; }
      .actions button { flex: 1 1 10rem; }
    }
    @media (prefers-reduced-motion: reduce) {
      *, *::before, *::after { scroll-behavior: auto !important; transition: none !important; }
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --page: #11161c;
        --surface: #19212a;
        --surface-soft: #202a35;
        --text: #edf2f7;
        --muted: #b5c0cc;
        --line: #3a4653;
        --accent: #73a7f5;
        --accent-strong: #4f8ce8;
        --success: #65c99b;
        --focus: #ffd166;
        --shadow: none;
      }
      .required-badge { background: #283f61; color: #c9dcfb; }
      button.primary { color: #0d1a2b; }
    }
  </style>
</head>
<body>
  <main class="page-shell" id="checklist-app" data-storage-key="$storage_key" data-share-hash="$share_url_hash">
    <header class="hero">
      <h1>$title</h1>
      $description
      <div class="progress-grid">
        <progress id="checklist-progress" max="$item_count" value="0" aria-label="Checklist progress" aria-describedby="progress-summary"></progress>
        <p id="progress-summary" aria-live="polite">0 of $item_count complete</p>
      </div>
      <div class="actions" aria-label="Checklist actions">
        <button class="primary" id="copy-button" type="button">Copy link</button>
        <button id="reset-button" type="button">Reset progress</button>
      </div>
      <p id="status-message" role="status" aria-live="polite"></p>
      <noscript>JavaScript is required for progress saving, resetting, and link copying. The checklist items remain readable.</noscript>
    </header>
    <div class="sections">
      $sections
    </div>
  </main>
  <script>
    (() => {
      "use strict";

      const app = document.getElementById("checklist-app");
      const progress = document.getElementById("checklist-progress");
      const summary = document.getElementById("progress-summary");
      const status = document.getElementById("status-message");
      const copyButton = document.getElementById("copy-button");
      const resetButton = document.getElementById("reset-button");
      const checkboxes = Array.from(app.querySelectorAll('input[type="checkbox"][data-item-id]'));
      const allowedIds = new Set(checkboxes.map((checkbox) => checkbox.dataset.itemId));
      const storageKey = app.dataset.storageKey;
      const shareEnabled = app.dataset.shareHash === "true";

      function normalizedIds(value) {
        if (!Array.isArray(value)) return null;
        return Array.from(new Set(value.filter((id) => typeof id === "string" && allowedIds.has(id))));
      }

      function selectedIds() {
        return checkboxes.filter((checkbox) => checkbox.checked).map((checkbox) => checkbox.dataset.itemId);
      }

      function applyIds(ids) {
        const selected = new Set(ids || []);
        checkboxes.forEach((checkbox) => {
          checkbox.checked = selected.has(checkbox.dataset.itemId);
        });
      }

      function render() {
        const complete = selectedIds().length;
        progress.value = complete;
        summary.textContent = complete + " of " + checkboxes.length + " complete";
        checkboxes.forEach((checkbox) => {
          const row = checkbox.closest(".checklist-item");
          if (row) row.classList.toggle("is-complete", checkbox.checked);
        });
      }

      function saveLocal() {
        try {
          localStorage.setItem(storageKey, JSON.stringify({ version: 1, checked: selectedIds() }));
        } catch (_error) {
          status.textContent = "Progress changed, but this browser did not allow local saving.";
        }
      }

      function readLocal() {
        try {
          const parsed = JSON.parse(localStorage.getItem(storageKey));
          return parsed && parsed.version === 1 ? normalizedIds(parsed.checked) : null;
        } catch (_error) {
          return null;
        }
      }

      function encodeIds(ids) {
        return btoa(JSON.stringify(ids)).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$$/g, "");
      }

      function readHash() {
        if (!shareEnabled) return null;
        const parameters = new URLSearchParams(window.location.hash.slice(1));
        if (!parameters.has("checklist")) return null;
        try {
          let encoded = parameters.get("checklist").replaceAll("-", "+").replaceAll("_", "/");
          encoded += "=".repeat((4 - encoded.length % 4) % 4);
          return normalizedIds(JSON.parse(atob(encoded)));
        } catch (_error) {
          return null;
        }
      }

      function writeHash() {
        if (!shareEnabled) return;
        try {
          const parameters = new URLSearchParams(window.location.hash.slice(1));
          const ids = selectedIds();
          if (ids.length) parameters.set("checklist", encodeIds(ids));
          else parameters.delete("checklist");
          const hash = parameters.toString();
          const nextUrl = window.location.pathname + window.location.search + (hash ? "#" + hash : "");
          window.history.replaceState(null, "", nextUrl);
        } catch (_error) {
          status.textContent = "Progress was saved locally, but the URL could not be updated.";
        }
      }

      function persist() {
        saveLocal();
        writeHash();
        render();
      }

      function fallbackCopy(value) {
        const field = document.createElement("textarea");
        field.value = value;
        field.setAttribute("readonly", "");
        field.style.position = "fixed";
        field.style.opacity = "0";
        document.body.appendChild(field);
        field.select();
        const copied = document.execCommand("copy");
        field.remove();
        if (!copied) throw new Error("Copy command was not accepted");
      }

      async function copyLink() {
        writeHash();
        const url = new URL(window.location.href);
        if (!shareEnabled) url.hash = "";
        try {
          if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(url.href);
          else fallbackCopy(url.href);
          status.textContent = shareEnabled
            ? "Link copied with the current progress."
            : "Page link copied. Progress sharing is disabled for this checklist.";
        } catch (_error) {
          status.textContent = "The link could not be copied. Copy it from the address bar instead.";
        }
      }

      checkboxes.forEach((checkbox) => checkbox.addEventListener("change", persist));
      resetButton.addEventListener("click", () => {
        if (!window.confirm("Clear all completed items?")) return;
        applyIds([]);
        persist();
        status.textContent = "Progress reset.";
        resetButton.focus();
      });
      copyButton.addEventListener("click", copyLink);

      if (shareEnabled) {
        window.addEventListener("hashchange", () => {
          const shared = readHash();
          if (shared === null) return;
          applyIds(shared);
          saveLocal();
          render();
          status.textContent = "Progress loaded from the link.";
        });
      }

      const shared = readHash();
      applyIds(shared === null ? (readLocal() || []) : shared);
      if (shared !== null) saveLocal();
      render();
    })();
  </script>
</body>
</html>
'''
)


def build_html(spec: dict[str, Any]) -> str:
    item_count = sum(len(section["items"]) for section in spec["sections"])
    description = ""
    if spec["description"]:
        description = f'<p class="intro">{escaped(spec["description"])}</p>'
    return HTML_TEMPLATE.substitute(
        language=escaped(spec["language"]),
        title=escaped(spec["title"]),
        description=description,
        storage_key=escaped(spec["storage_key"]),
        share_url_hash="true" if spec["share_url_hash"] else "false",
        item_count=str(item_count),
        sections=render_sections(spec["sections"]),
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="UTF-8 checklist JSON")
    parser.add_argument("output", type=Path, help="HTML output path")
    parser.add_argument("--force", action="store_true", help="replace an existing output file")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    input_path = args.input.expanduser().resolve()
    output_path = args.output.expanduser().resolve()
    if input_path == output_path:
        raise InputError("input and output paths must be different")
    if not input_path.is_file():
        raise InputError(f"input file does not exist: {input_path}")
    if output_path.exists() and not args.force:
        raise InputError(f"output already exists; pass --force to replace it: {output_path}")

    try:
        raw = json.loads(input_path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        raise InputError(f"invalid JSON at line {error.lineno}, column {error.colno}: {error.msg}") from error

    spec = normalize_spec(raw)
    document = build_html(spec)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(document, encoding="utf-8", newline="\n")
    item_count = sum(len(section["items"]) for section in spec["sections"])
    print(
        f"Built {output_path} with {len(spec['sections'])} section(s), "
        f"{item_count} unique item(s), and URL-hash sharing "
        f"{'enabled' if spec['share_url_hash'] else 'disabled'}."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (InputError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(2)
