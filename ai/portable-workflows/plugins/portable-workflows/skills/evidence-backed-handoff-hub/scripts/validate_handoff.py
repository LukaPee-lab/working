#!/usr/bin/env python3
"""Validate handoff JSON data and an optional generated HTML file."""

from __future__ import annotations

import argparse
import json
import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from typing import Any


ROOT_REQUIRED = {
    "title": str,
    "overview": str,
    "evidence": list,
    "sections": list,
    "open_questions": list,
}


def _is_non_empty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _check_non_empty_string(
    obj: dict[str, Any], key: str, path: str, errors: list[str]
) -> None:
    if key not in obj:
        errors.append(f"{path}: missing required field '{key}'")
    elif not _is_non_empty_string(obj[key]):
        errors.append(f"{path}.{key}: expected a non-empty string")


def _check_optional_string(
    obj: dict[str, Any], key: str, path: str, errors: list[str]
) -> None:
    if key in obj and not isinstance(obj[key], str):
        errors.append(f"{path}.{key}: expected a string")


def _check_string_list(
    obj: dict[str, Any],
    key: str,
    path: str,
    errors: list[str],
    *,
    required: bool,
) -> list[str]:
    if key not in obj:
        if required:
            errors.append(f"{path}: missing required field '{key}'")
        return []
    value = obj[key]
    if not isinstance(value, list):
        errors.append(f"{path}.{key}: expected an array of strings")
        return []
    result: list[str] = []
    for index, item in enumerate(value):
        if not _is_non_empty_string(item):
            errors.append(f"{path}.{key}[{index}]: expected a non-empty string")
        else:
            result.append(item)
    return result


def _register_id(
    obj: dict[str, Any], path: str, seen: dict[str, str], errors: list[str]
) -> str | None:
    _check_non_empty_string(obj, "id", path, errors)
    value = obj.get("id")
    if not _is_non_empty_string(value):
        return None
    if value in seen:
        errors.append(f"{path}.id: duplicate ID '{value}' (first used at {seen[value]})")
    else:
        seen[value] = path
    return value


def _object_at(value: Any, path: str, errors: list[str]) -> dict[str, Any] | None:
    if not isinstance(value, dict):
        errors.append(f"{path}: expected an object")
        return None
    return value


def validate_data(data: Any) -> list[str]:
    """Return a list of schema and reference errors for parsed JSON data."""
    errors: list[str] = []
    if not isinstance(data, dict):
        return ["root: expected a JSON object"]

    for key, expected_type in ROOT_REQUIRED.items():
        if key not in data:
            errors.append(f"root: missing required field '{key}'")
        elif not isinstance(data[key], expected_type):
            errors.append(f"root.{key}: expected {expected_type.__name__}")

    for key in ("title", "overview"):
        if key in data and not _is_non_empty_string(data[key]):
            errors.append(f"root.{key}: expected a non-empty string")
    for key in ("subtitle", "last_updated"):
        _check_optional_string(data, key, "root", errors)
    for key in ("tables", "relationships"):
        if key in data and not isinstance(data[key], list):
            errors.append(f"root.{key}: expected an array")

    evidence_items = data.get("evidence") if isinstance(data.get("evidence"), list) else []
    section_items = data.get("sections") if isinstance(data.get("sections"), list) else []
    question_items = (
        data.get("open_questions") if isinstance(data.get("open_questions"), list) else []
    )
    table_items = data.get("tables") if isinstance(data.get("tables"), list) else []
    relationship_items = (
        data.get("relationships") if isinstance(data.get("relationships"), list) else []
    )

    if isinstance(data.get("evidence"), list) and not evidence_items:
        errors.append("root.evidence: expected at least one item")
    if isinstance(data.get("sections"), list) and not section_items:
        errors.append("root.sections: expected at least one item")

    seen_ids: dict[str, str] = {}
    evidence_ids: set[str] = set()
    section_references: list[tuple[str, list[str]]] = []

    for index, raw in enumerate(evidence_items):
        path = f"root.evidence[{index}]"
        obj = _object_at(raw, path, errors)
        if obj is None:
            continue
        item_id = _register_id(obj, path, seen_ids, errors)
        if item_id is not None:
            evidence_ids.add(item_id)
        for key in ("title", "source", "status", "summary"):
            _check_non_empty_string(obj, key, path, errors)
        _check_string_list(obj, "details", path, errors, required=False)

    for index, raw in enumerate(section_items):
        path = f"root.sections[{index}]"
        obj = _object_at(raw, path, errors)
        if obj is None:
            continue
        _register_id(obj, path, seen_ids, errors)
        for key in ("title", "summary"):
            _check_non_empty_string(obj, key, path, errors)
        _check_string_list(obj, "details", path, errors, required=False)
        refs = _check_string_list(obj, "evidence_ids", path, errors, required=True)
        if len(refs) != len(set(refs)):
            errors.append(f"{path}.evidence_ids: duplicate evidence reference")
        section_references.append((path, refs))

    for path, refs in section_references:
        for evidence_id in refs:
            if evidence_id not in evidence_ids:
                errors.append(
                    f"{path}.evidence_ids: unknown evidence ID '{evidence_id}'"
                )

    for index, raw in enumerate(question_items):
        path = f"root.open_questions[{index}]"
        obj = _object_at(raw, path, errors)
        if obj is None:
            continue
        _register_id(obj, path, seen_ids, errors)
        for key in ("question", "status"):
            _check_non_empty_string(obj, key, path, errors)
        for key in ("context", "owner"):
            _check_optional_string(obj, key, path, errors)

    tables: dict[str, set[str]] = {}
    for index, raw in enumerate(table_items):
        path = f"root.tables[{index}]"
        obj = _object_at(raw, path, errors)
        if obj is None:
            continue
        table_id = _register_id(obj, path, seen_ids, errors)
        if table_id is not None and "." in table_id:
            errors.append(f"{path}.id: table IDs must not contain '.'")
        for key in ("name", "description"):
            _check_non_empty_string(obj, key, path, errors)
        fields = obj.get("fields")
        if "fields" not in obj:
            errors.append(f"{path}: missing required field 'fields'")
            fields = []
        elif not isinstance(fields, list):
            errors.append(f"{path}.fields: expected an array")
            fields = []
        field_names: set[str] = set()
        for field_index, raw_field in enumerate(fields):
            field_path = f"{path}.fields[{field_index}]"
            field = _object_at(raw_field, field_path, errors)
            if field is None:
                continue
            for key in ("name", "description"):
                _check_non_empty_string(field, key, field_path, errors)
            field_name = field.get("name")
            if _is_non_empty_string(field_name):
                if "." in field_name:
                    errors.append(f"{field_path}.name: field names must not contain '.'")
                if field_name in field_names:
                    errors.append(
                        f"{field_path}.name: duplicate field name '{field_name}'"
                    )
                field_names.add(field_name)
        if table_id is not None:
            tables[table_id] = field_names

    endpoints: list[tuple[str, str]] = []
    for index, raw in enumerate(relationship_items):
        path = f"root.relationships[{index}]"
        obj = _object_at(raw, path, errors)
        if obj is None:
            continue
        _register_id(obj, path, seen_ids, errors)
        for key in ("from", "to", "label"):
            _check_non_empty_string(obj, key, path, errors)
        for key in ("from", "to"):
            endpoint = obj.get(key)
            if _is_non_empty_string(endpoint):
                endpoints.append((f"{path}.{key}", endpoint))

    for path, endpoint in endpoints:
        table_id, separator, field_name = endpoint.partition(".")
        if not table_id or (separator and not field_name):
            errors.append(f"{path}: invalid endpoint '{endpoint}'")
            continue
        if table_id not in tables:
            errors.append(f"{path}: unknown table ID '{table_id}'")
        elif separator and field_name not in tables[table_id]:
            errors.append(
                f"{path}: unknown field '{field_name}' on table '{table_id}'"
            )

    return errors


def load_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


_EXTERNAL_URL = re.compile(r"^(?:https?:|ftp:|//)", re.IGNORECASE)
_CSS_EXTERNAL_URL = re.compile(
    r"(?:url\(\s*['\"]?\s*(?:https?:|ftp:|//)|@import\s+(?:url\()?\s*['\"]?\s*(?:https?:|ftp:|//))",
    re.IGNORECASE,
)


class _OfflineHTMLInspector(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.errors: list[str] = []
        self._style_depth = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        lowered_tag = tag.lower()
        if lowered_tag == "script":
            self.errors.append("html: script element is not allowed")
        if lowered_tag == "style":
            self._style_depth += 1
        for name, value in attrs:
            lowered_name = name.lower()
            text = value or ""
            if lowered_name.startswith("on"):
                self.errors.append(
                    f"html: inline event handler '{name}' is not allowed on <{tag}>"
                )
            if text.strip().lower().startswith("javascript:"):
                self.errors.append(
                    f"html: javascript URL is not allowed in {name} on <{tag}>"
                )
            if lowered_name in {
                "src",
                "href",
                "action",
                "formaction",
                "poster",
                "data",
                "xlink:href",
                "background",
                "cite",
                "longdesc",
                "manifest",
                "profile",
            } and _EXTERNAL_URL.match(text.strip()):
                self.errors.append(
                    f"html: external URL is not allowed in {name} on <{tag}>"
                )
            if lowered_name == "srcset":
                for candidate in text.split(","):
                    url = candidate.strip().split(" ", 1)[0]
                    if _EXTERNAL_URL.match(url):
                        self.errors.append(
                            f"html: external URL is not allowed in srcset on <{tag}>"
                        )
            if lowered_name == "style" and _CSS_EXTERNAL_URL.search(text):
                self.errors.append(
                    f"html: external CSS URL is not allowed in style on <{tag}>"
                )
        if lowered_tag == "meta":
            attributes = {name.lower(): value or "" for name, value in attrs}
            if attributes.get("http-equiv", "").lower() == "refresh" and re.search(
                r"(?:https?:|ftp:|//)", attributes.get("content", ""), re.IGNORECASE
            ):
                self.errors.append("html: external meta refresh URL is not allowed")

    def handle_startendtag(
        self, tag: str, attrs: list[tuple[str, str | None]]
    ) -> None:
        self.handle_starttag(tag, attrs)
        if tag.lower() == "style":
            self._style_depth = max(0, self._style_depth - 1)

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() == "style":
            self._style_depth = max(0, self._style_depth - 1)

    def handle_data(self, data: str) -> None:
        if self._style_depth and _CSS_EXTERNAL_URL.search(data):
            self.errors.append("html: external CSS URL is not allowed in <style>")


def validate_html_text(html_text: str) -> list[str]:
    """Return portability and script-safety errors for generated HTML."""
    inspector = _OfflineHTMLInspector()
    try:
        inspector.feed(html_text)
        inspector.close()
    except Exception as exc:  # HTMLParser errors are uncommon but actionable.
        inspector.errors.append(f"html: could not parse document: {exc}")
    return inspector.errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Validate handoff JSON and optionally verify an offline HTML build."
    )
    parser.add_argument("input", type=Path, help="UTF-8 handoff JSON file")
    parser.add_argument("--html", type=Path, help="Generated HTML file to inspect")
    args = parser.parse_args(argv)

    errors: list[str] = []
    try:
        data = load_json(args.input)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        errors.append(f"input: could not read valid UTF-8 JSON: {exc}")
    else:
        errors.extend(validate_data(data))

    if args.html is not None:
        try:
            html_text = args.html.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as exc:
            errors.append(f"html: could not read UTF-8 file: {exc}")
        else:
            errors.extend(validate_html_text(html_text))

    if errors:
        for error in errors:
            print(f"ERROR {error}", file=sys.stderr)
        print(f"Validation failed with {len(errors)} error(s).", file=sys.stderr)
        return 1

    suffix = " and HTML" if args.html is not None else ""
    print(f"Validation passed: JSON{suffix}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
