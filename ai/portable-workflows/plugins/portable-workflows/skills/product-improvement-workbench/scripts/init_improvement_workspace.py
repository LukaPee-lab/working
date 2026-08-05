#!/usr/bin/env python3
"""Initialize a portable product-improvement workspace."""

from __future__ import annotations

import argparse
import json
import sys
from datetime import date
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Create collection and build areas for an improvement workbench."
    )
    parser.add_argument("workspace", type=Path, help="Workspace directory to create")
    parser.add_argument("--title", help="Human-readable workspace title")
    parser.add_argument(
        "--created-on",
        type=iso_date,
        metavar="YYYY-MM-DD",
        help="Creation date; defaults to the current local date",
    )
    return parser.parse_args()


def iso_date(value: str) -> str:
    try:
        return date.fromisoformat(value).isoformat()
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            f"invalid ISO date {value!r}; expected YYYY-MM-DD"
        ) from exc


def default_title(workspace: Path) -> str:
    name = workspace.resolve().name.replace("-", " ").replace("_", " ").strip()
    return name or "Product improvement workbench"


def main() -> int:
    args = parse_args()
    workspace = args.workspace.resolve()
    created_on = args.created_on or date.today().isoformat()
    collection = workspace / "collection"
    evidence = collection / "evidence"
    build = workspace / "build"
    log_path = collection / "improvement-log.json"

    if workspace.exists() and not workspace.is_dir():
        print(f"ERROR: workspace path is not a directory: {workspace}", file=sys.stderr)
        return 2
    if log_path.exists():
        print(f"ERROR: refusing to overwrite existing log: {log_path}", file=sys.stderr)
        return 2

    evidence.mkdir(parents=True, exist_ok=True)
    build.mkdir(parents=True, exist_ok=True)

    document = {
        "schema_version": 1,
        "workspace": {
            "title": args.title or default_title(workspace),
            "created_on": created_on,
            "updated_on": created_on,
        },
        "entries": [],
    }
    log_path.write_text(
        json.dumps(document, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    print(f"Initialized workspace: {workspace}")
    print(f"Source log: {log_path}")
    print(f"Evidence directory: {evidence}")
    print(f"Build directory: {build}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
