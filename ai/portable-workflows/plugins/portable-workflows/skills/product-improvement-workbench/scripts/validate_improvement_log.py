#!/usr/bin/env python3
"""Validate an improvement log and optionally build its review-ready brief."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass
from datetime import date
from pathlib import Path
from typing import Any
from urllib.parse import quote


ID_PATTERN = re.compile(r"^IMP-\d{3,}$")
PRIORITIES = {"critical", "high", "medium", "low", "untriaged"}
PRIORITY_ORDER = {"critical": 0, "high": 1, "medium": 2, "low": 3, "untriaged": 4}
VERIFICATION_STATES = {"pending", "verified", "disproved"}
ENTRY_STATES = {
    "collecting",
    "review-ready",
    "approved",
    "in-progress",
    "done",
    "deferred",
    "rejected",
}
GATED_STATES = {"review-ready", "approved", "in-progress", "done"}


@dataclass(frozen=True)
class Issue:
    level: str
    location: str
    message: str


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate collection/improvement-log.json and optionally build Markdown."
    )
    parser.add_argument("source", type=Path, help="Workspace directory or improvement-log.json")
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Return a failure code when warnings are present",
    )
    parser.add_argument(
        "--build-review",
        action="store_true",
        help="Write a brief containing entries marked review-ready",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Markdown output path; requires --build-review",
    )
    args = parser.parse_args()
    if args.output and not args.build_review:
        parser.error("--output requires --build-review")
    return args


def locate_source(source: Path) -> tuple[Path, Path]:
    resolved = source.resolve()
    if resolved.is_dir():
        return resolved / "collection" / "improvement-log.json", resolved
    workspace = resolved.parent.parent if resolved.parent.name == "collection" else resolved.parent
    return resolved, workspace


def nonempty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def is_iso_date(value: Any) -> bool:
    if not nonempty_string(value):
        return False
    try:
        return date.fromisoformat(value).isoformat() == value
    except ValueError:
        return False


def require_keys(
    value: Any, required: tuple[str, ...], location: str, issues: list[Issue]
) -> bool:
    if not isinstance(value, dict):
        issues.append(Issue("ERROR", location, "must be an object"))
        return False
    for key in required:
        if key not in value:
            issues.append(Issue("ERROR", f"{location}.{key}", "is required"))
    return True


def validate_evidence_path(
    raw_path: Any,
    workspace: Path,
    location: str,
    required_to_exist: bool,
    issues: list[Issue],
) -> None:
    if not nonempty_string(raw_path):
        issues.append(Issue("ERROR", location, "must be a non-empty string"))
        return
    candidate = Path(raw_path)
    if candidate.is_absolute():
        issues.append(Issue("ERROR", location, "must be relative to the workspace"))
        return
    root = workspace.resolve()
    resolved = (root / candidate).resolve()
    try:
        resolved.relative_to(root)
    except ValueError:
        issues.append(Issue("ERROR", location, "must not escape the workspace"))
        return
    if not resolved.is_file():
        level = "ERROR" if required_to_exist else "WARNING"
        issues.append(Issue(level, location, f"evidence file does not exist: {raw_path}"))


def validate_document(data: Any, workspace: Path) -> tuple[list[Issue], list[dict[str, Any]]]:
    issues: list[Issue] = []
    review_entries: list[dict[str, Any]] = []
    if not require_keys(data, ("schema_version", "workspace", "entries"), "$", issues):
        return issues, review_entries
    if data.get("schema_version") != 1:
        issues.append(Issue("ERROR", "$.schema_version", "must equal 1"))

    metadata = data.get("workspace")
    if require_keys(metadata, ("title", "created_on", "updated_on"), "$.workspace", issues):
        if not nonempty_string(metadata.get("title")):
            issues.append(Issue("ERROR", "$.workspace.title", "must be a non-empty string"))
        for field in ("created_on", "updated_on"):
            if not is_iso_date(metadata.get(field)):
                issues.append(Issue("ERROR", f"$.workspace.{field}", "must be an ISO date (YYYY-MM-DD)"))

    entries = data.get("entries")
    if not isinstance(entries, list):
        issues.append(Issue("ERROR", "$.entries", "must be an array"))
        return issues, review_entries

    seen_ids: set[str] = set()
    for index, entry in enumerate(entries):
        location = f"$.entries[{index}]"
        required = ("id", "title", "observation", "proposal", "priority", "owner", "status")
        if not require_keys(entry, required, location, issues):
            continue

        entry_id = entry.get("id")
        if not nonempty_string(entry_id) or not ID_PATTERN.fullmatch(entry_id):
            issues.append(Issue("ERROR", f"{location}.id", "must match IMP- followed by at least three digits"))
        elif entry_id in seen_ids:
            issues.append(Issue("ERROR", f"{location}.id", f"duplicates {entry_id}"))
        else:
            seen_ids.add(entry_id)

        if not nonempty_string(entry.get("title")):
            issues.append(Issue("ERROR", f"{location}.title", "must be a non-empty string"))

        priority = entry.get("priority")
        if priority not in PRIORITIES:
            issues.append(Issue("ERROR", f"{location}.priority", f"must be one of {sorted(PRIORITIES)}"))
        owner = entry.get("owner")
        if not nonempty_string(owner):
            issues.append(Issue("ERROR", f"{location}.owner", "must be a non-empty string"))
        status = entry.get("status")
        if status not in ENTRY_STATES:
            issues.append(Issue("ERROR", f"{location}.status", f"must be one of {sorted(ENTRY_STATES)}"))
        gated = status in GATED_STATES

        observation = entry.get("observation")
        observation_required = (
            "statement",
            "verification_status",
            "verification_method",
            "verified_by",
            "verified_on",
            "evidence_paths",
        )
        observation_ok = require_keys(observation, observation_required, f"{location}.observation", issues)
        verification_status = None
        if observation_ok:
            if not nonempty_string(observation.get("statement")):
                issues.append(Issue("ERROR", f"{location}.observation.statement", "must be a non-empty string"))
            verification_status = observation.get("verification_status")
            if verification_status not in VERIFICATION_STATES:
                issues.append(
                    Issue(
                        "ERROR",
                        f"{location}.observation.verification_status",
                        f"must be one of {sorted(VERIFICATION_STATES)}",
                    )
                )
            checked = verification_status in {"verified", "disproved"}
            for field in ("verification_method", "verified_by"):
                if checked and not nonempty_string(observation.get(field)):
                    issues.append(Issue("ERROR", f"{location}.observation.{field}", "is required after verification"))
                elif not isinstance(observation.get(field), str):
                    issues.append(Issue("ERROR", f"{location}.observation.{field}", "must be a string"))
            verified_on = observation.get("verified_on")
            if checked and not is_iso_date(verified_on):
                issues.append(Issue("ERROR", f"{location}.observation.verified_on", "must be an ISO date after verification"))
            elif not checked and verified_on and not is_iso_date(verified_on):
                issues.append(Issue("ERROR", f"{location}.observation.verified_on", "must be empty or an ISO date"))

            evidence_paths = observation.get("evidence_paths")
            if not isinstance(evidence_paths, list):
                issues.append(Issue("ERROR", f"{location}.observation.evidence_paths", "must be an array"))
            else:
                if (checked or gated) and not evidence_paths:
                    issues.append(Issue("ERROR", f"{location}.observation.evidence_paths", "must contain at least one path"))
                for evidence_index, evidence_path in enumerate(evidence_paths):
                    validate_evidence_path(
                        evidence_path,
                        workspace,
                        f"{location}.observation.evidence_paths[{evidence_index}]",
                        checked or gated,
                        issues,
                    )

        proposal = entry.get("proposal")
        proposal_ok = require_keys(
            proposal,
            ("summary", "expected_outcome", "tradeoffs"),
            f"{location}.proposal",
            issues,
        )
        if proposal_ok:
            for field in ("summary", "expected_outcome"):
                value = proposal.get(field)
                if not isinstance(value, str):
                    issues.append(Issue("ERROR", f"{location}.proposal.{field}", "must be a string"))
                elif gated and not value.strip():
                    issues.append(Issue("ERROR", f"{location}.proposal.{field}", "is required in a gated state"))
            tradeoffs = proposal.get("tradeoffs")
            if not isinstance(tradeoffs, list) or any(not nonempty_string(item) for item in tradeoffs):
                issues.append(Issue("ERROR", f"{location}.proposal.tradeoffs", "must be an array of non-empty strings"))

        if gated:
            if verification_status != "verified":
                issues.append(Issue("ERROR", f"{location}.observation.verification_status", "must be verified in a gated state"))
            if priority == "untriaged":
                issues.append(Issue("ERROR", f"{location}.priority", "must be ranked in a gated state"))
            if isinstance(owner, str) and owner.strip().casefold() == "unassigned":
                issues.append(Issue("ERROR", f"{location}.owner", "must be assigned in a gated state"))

        if status == "review-ready":
            review_entries.append(entry)

    return issues, review_entries


def one_line(value: Any) -> str:
    return " ".join(str(value).split())


def markdown_text(value: Any) -> str:
    text = one_line(value).replace("\\", "\\\\")
    for character in "`*_{}[]<>()#+-.!|":
        text = text.replace(character, "\\" + character)
    return text


def evidence_link(raw_path: str, workspace: Path, output: Path) -> str:
    target = (workspace / raw_path).resolve()
    relative = Path(os.path.relpath(target, output.parent.resolve())).as_posix()
    label = markdown_text(raw_path)
    destination = quote(relative, safe="/._~-")
    return f"[{label}](<{destination}>)"


def build_review(
    data: dict[str, Any], entries: list[dict[str, Any]], workspace: Path, output: Path
) -> None:
    title = markdown_text(data["workspace"]["title"])
    source_path = Path(
        os.path.relpath(
            workspace / "collection" / "improvement-log.json",
            output.parent.resolve(),
        )
    ).as_posix()
    sorted_entries = sorted(
        entries,
        key=lambda item: (PRIORITY_ORDER.get(item["priority"], 99), item["id"]),
    )
    lines = [
        f"# {title}: review-ready improvements",
        "",
        f"- Review candidates: {len(sorted_entries)}",
        f"- Source: `{source_path}`",
        "",
    ]
    if not sorted_entries:
        lines.extend(["No entries are currently marked `review-ready`.", ""])
    for entry in sorted_entries:
        observation = entry["observation"]
        proposal = entry["proposal"]
        lines.extend(
            [
                f"## {markdown_text(entry['id'])} — {markdown_text(entry['title'])}",
                "",
                f"- Priority: {markdown_text(entry['priority'])}",
                f"- Owner: {markdown_text(entry['owner'])}",
                f"- Status: {markdown_text(entry['status'])}",
                "",
                "### Verified observation",
                "",
                markdown_text(observation["statement"]),
                "",
                f"- Verification method: {markdown_text(observation['verification_method'])}",
                f"- Verified by: {markdown_text(observation['verified_by'])}",
                f"- Verified on: {markdown_text(observation['verified_on'])}",
                "- Evidence:",
            ]
        )
        for raw_path in observation["evidence_paths"]:
            lines.append(f"  - {evidence_link(raw_path, workspace, output)}")
        lines.extend(
            [
                "",
                "### Proposed change",
                "",
                markdown_text(proposal["summary"]),
                "",
                f"**Expected outcome:** {markdown_text(proposal['expected_outcome'])}",
                "",
                "**Tradeoffs:**",
                "",
            ]
        )
        if proposal["tradeoffs"]:
            lines.extend(f"- {markdown_text(item)}" for item in proposal["tradeoffs"])
        else:
            lines.append("- None recorded.")
        lines.append("")

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    args = parse_args()
    log_path, workspace = locate_source(args.source)
    if not log_path.is_file():
        print(f"ERROR: log file not found: {log_path}", file=sys.stderr)
        return 2
    try:
        data = json.loads(log_path.read_text(encoding="utf-8"))
    except UnicodeDecodeError as exc:
        print(f"ERROR: log must be UTF-8: {exc}", file=sys.stderr)
        return 2
    except json.JSONDecodeError as exc:
        print(f"ERROR: invalid JSON at line {exc.lineno}, column {exc.colno}: {exc.msg}", file=sys.stderr)
        return 2

    issues, review_entries = validate_document(data, workspace)
    errors = [issue for issue in issues if issue.level == "ERROR"]
    warnings = [issue for issue in issues if issue.level == "WARNING"]
    entry_count = len(data.get("entries", [])) if isinstance(data, dict) and isinstance(data.get("entries"), list) else 0
    print(
        f"Validated {entry_count} entries: {len(errors)} error(s), "
        f"{len(warnings)} warning(s), {len(review_entries)} review candidate(s)."
    )
    for issue in issues:
        print(f"{issue.level}: {issue.location}: {issue.message}")

    if errors or (args.strict and warnings):
        return 1
    if args.build_review:
        output = args.output.resolve() if args.output else workspace / "build" / "review-ready.md"
        build_review(data, review_entries, workspace, output)
        print(f"Built review brief: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
