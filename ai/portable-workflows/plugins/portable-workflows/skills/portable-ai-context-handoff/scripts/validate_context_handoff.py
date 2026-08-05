#!/usr/bin/env python3
"""Validate the structure and basic safety of a portable context handoff."""

from __future__ import annotations

import argparse
import re
from datetime import date
from pathlib import Path


REQUIRED_HEADINGS = (
    "Handoff metadata",
    "Scope",
    "Current state",
    "Verified facts",
    "Assumptions and inferences",
    "Decisions",
    "Constraints",
    "Unresolved questions",
    "Working preferences and voice",
    "Next actions",
    "Bootstrap prompt",
    "Final safety review",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate a Markdown context handoff before sharing it."
    )
    parser.add_argument("handoff", type=Path, help="Filled Markdown handoff")
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Return a failure status when warnings are present.",
    )
    return parser.parse_args()


def metadata_value(text: str, field: str) -> str | None:
    match = re.search(
        rf"^\|\s*{re.escape(field)}\s*\|\s*(.*?)\s*\|\s*$",
        text,
        flags=re.IGNORECASE | re.MULTILINE,
    )
    if not match:
        return None
    value = match.group(1).strip()
    if len(value) >= 2 and value.startswith("`") and value.endswith("`"):
        value = value[1:-1].strip()
    return value


def section(text: str, heading: str) -> str:
    match = re.search(
        rf"^##\s+{re.escape(heading)}\s*$\n(.*?)(?=^##\s+|\Z)",
        text,
        flags=re.IGNORECASE | re.MULTILINE | re.DOTALL,
    )
    return match.group(1).strip() if match else ""


def parse_iso_date(value: str | None, label: str, errors: list[str]) -> date | None:
    if value is None:
        errors.append(f"Missing metadata row: {label}")
        return None
    try:
        return date.fromisoformat(value)
    except ValueError:
        errors.append(f"{label} must use YYYY-MM-DD: {value!r}")
        return None


def suspected_secret_labels(text: str) -> list[str]:
    # Construct recognizable prefixes in pieces so source-code scans do not
    # mistake these detection rules for embedded credentials.
    patterns = {
        "private key block": r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
        "cloud access key": r"A" + r"KIA[0-9A-Z]{16}",
        "API token": r"s" + r"k-[A-Za-z0-9_-]{20,}",
        "repository token": r"g" + r"hp_[A-Za-z0-9]{36}",
        "messaging token": r"x" + r"ox[baprs]-[A-Za-z0-9-]{20,}",
        "JWT-like token": r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
    }
    return [label for label, pattern in patterns.items() if re.search(pattern, text)]


def validate(text: str) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    warnings: list[str] = []

    title = re.search(r"^#\s+Context handoff:\s*(.+?)\s*$", text, re.MULTILINE)
    if not title:
        errors.append("Missing filled '# Context handoff: ...' title.")

    for heading in REQUIRED_HEADINGS:
        if not re.search(rf"^##\s+{re.escape(heading)}\s*$", text, re.I | re.M):
            errors.append(f"Missing required heading: {heading}")

    placeholders = re.findall(r"<[^>\n]+>", text)
    if placeholders:
        preview = ", ".join(dict.fromkeys(placeholders[:5]))
        errors.append(f"Unresolved angle-bracket placeholder(s): {preview}")

    created = parse_iso_date(metadata_value(text, "Created on"), "Created on", errors)
    expiry = parse_iso_date(
        metadata_value(text, "Review or expiry date"),
        "Review or expiry date",
        errors,
    )
    if created and expiry and expiry < created:
        errors.append("Review or expiry date precedes the creation date.")

    classification = metadata_value(text, "Overall classification")
    allowed = {"open", "limited", "sensitive"}
    if classification is None or classification.casefold() not in allowed:
        errors.append("Overall classification must be open, limited, or sensitive.")

    receiver = metadata_value(text, "Intended receiver")
    if not receiver:
        errors.append("Intended receiver must name a bounded session, role, or audience.")

    objective = metadata_value(text, "Continuation objective")
    if not objective:
        errors.append("Continuation objective must be present.")

    bootstrap = section(text, "Bootstrap prompt")
    if "```" not in bootstrap:
        errors.append("Bootstrap prompt must contain a fenced prompt block.")
    if not re.search(r"Immediate objective\s*:", bootstrap, re.I):
        errors.append("Bootstrap prompt must state an immediate objective.")
    if not re.search(r"assumptions?\s+as\s+unverified", bootstrap, re.I):
        warnings.append("Bootstrap prompt should explicitly treat assumptions as unverified.")

    safety = section(text, "Final safety review")
    unchecked = re.findall(r"^\s*-\s*\[\s\]\s+(.+)$", safety, re.MULTILINE)
    if unchecked:
        errors.append(f"Final safety review has {len(unchecked)} unchecked item(s).")
    checked = re.findall(r"^\s*-\s*\[[xX]\]\s+", safety, re.MULTILINE)
    if len(checked) < 9:
        errors.append("Final safety review must contain all 9 checked items.")

    facts = section(text, "Verified facts")
    if not re.search(r"^\|\s*`?F-[A-Za-z0-9_-]+`?\s*\|", facts, re.MULTILINE):
        warnings.append("No verified-fact row was found; confirm that this is intentional.")

    for label in suspected_secret_labels(text):
        errors.append(f"Possible {label} detected; remove the value before sharing.")

    return errors, warnings


def main() -> int:
    args = parse_args()
    path = args.handoff.expanduser().resolve()
    if not path.is_file():
        raise FileNotFoundError(f"Handoff file was not found: {path}")

    text = path.read_text(encoding="utf-8")
    errors, warnings = validate(text)
    for item in errors:
        print(f"ERROR: {item}")
    for item in warnings:
        print(f"WARNING: {item}")

    print(f"Validated {path}: {len(errors)} error(s), {len(warnings)} warning(s).")
    print("This structural scan cannot guarantee that confidential data is absent.")
    return 1 if errors or (args.strict and warnings) else 0


if __name__ == "__main__":
    raise SystemExit(main())
