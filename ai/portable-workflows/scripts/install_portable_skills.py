#!/usr/bin/env python3
"""Install bundled skills into a personal Codex skill directory safely."""

from __future__ import annotations

import argparse
import shutil
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Install Portable Workflows as standalone personal skills."
    )
    parser.add_argument(
        "--destination",
        type=Path,
        default=Path.home() / ".agents" / "skills",
        help="Skill root (default: ~/.agents/skills)",
    )
    parser.add_argument(
        "--force",
        action="store_true",
        help="Update files in an existing skill directory without deleting extras.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Report actions without writing files.",
    )
    return parser.parse_args()


def ensure_safe_destination(destination: Path) -> Path:
    resolved = destination.expanduser().resolve()
    home = Path.home().resolve()
    anchor = Path(resolved.anchor).resolve()
    if resolved in {home, anchor}:
        raise ValueError(f"Refusing broad destination: {resolved}")
    return resolved


def main() -> int:
    args = parse_args()
    repository_root = Path(__file__).resolve().parent.parent
    source_root = repository_root / "plugins" / "portable-workflows" / "skills"
    destination_root = ensure_safe_destination(args.destination)

    if not source_root.is_dir():
        raise FileNotFoundError(f"Skill source directory was not found: {source_root}")

    installed = 0
    skipped = 0
    if not args.dry_run:
        destination_root.mkdir(parents=True, exist_ok=True)

    for source_skill in sorted(path for path in source_root.iterdir() if path.is_dir()):
        if source_skill.is_symlink():
            raise ValueError(f"Refusing symlinked skill source: {source_skill}")
        target_skill = destination_root / source_skill.name
        if target_skill.is_symlink():
            raise ValueError(f"Refusing symlinked skill destination: {target_skill}")
        if target_skill.exists() and not args.force:
            print(f"SKIP existing: {target_skill}")
            skipped += 1
            continue

        action = "UPDATE" if target_skill.exists() else "INSTALL"
        print(f"{action}: {source_skill.name} -> {target_skill}")
        if not args.dry_run:
            shutil.copytree(source_skill, target_skill, dirs_exist_ok=args.force)
        installed += 1

    print(f"Installed or updated: {installed}")
    print(f"Skipped: {skipped}")
    print(f"Destination: {destination_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
