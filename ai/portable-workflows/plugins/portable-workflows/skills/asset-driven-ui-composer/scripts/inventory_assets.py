#!/usr/bin/env python3
"""Inventory images under an explicitly approved directory."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any


SUPPORTED_EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff"}


class InventoryError(ValueError):
    """Raised when the approved asset inventory cannot be created safely."""


def load_pillow() -> tuple[Any, type[Exception]]:
    try:
        from PIL import Image, UnidentifiedImageError
    except ImportError as error:
        raise InventoryError("Pillow is required; install it with: python -m pip install Pillow") from error
    return Image, UnidentifiedImageError


def is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("approved_root", type=Path, help="directory explicitly approved for image use")
    parser.add_argument("output", type=Path, help="inventory JSON path outside the approved root")
    parser.add_argument("--force", action="store_true", help="replace an existing inventory file")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    root = args.approved_root.expanduser().resolve(strict=True)
    output = args.output.expanduser().resolve()
    if not root.is_dir():
        raise InventoryError(f"approved root is not a directory: {root}")
    if is_within(output, root):
        raise InventoryError("inventory output must be outside the approved asset directory")
    if output.exists() and not args.force:
        raise InventoryError(f"inventory already exists; pass --force to replace it: {output}")

    Image, UnidentifiedImageError = load_pillow()
    candidates = sorted(
        (path for path in root.rglob("*") if path.is_file() and path.suffix.lower() in SUPPORTED_EXTENSIONS),
        key=lambda path: path.relative_to(root).as_posix().casefold(),
    )
    assets: list[dict[str, Any]] = []
    for candidate in candidates:
        if candidate.is_symlink():
            raise InventoryError(f"symlinked assets are not allowed: {candidate.relative_to(root).as_posix()}")
        resolved = candidate.resolve(strict=True)
        if not is_within(resolved, root):
            raise InventoryError(f"asset resolves outside the approved root: {candidate}")
        relative = resolved.relative_to(root).as_posix()
        try:
            with Image.open(resolved) as image:
                image.load()
                width, height = image.size
                mode = image.mode
                detected_format = image.format or "UNKNOWN"
                frame_count = int(getattr(image, "n_frames", 1))
        except (UnidentifiedImageError, OSError) as error:
            raise InventoryError(f"could not read image asset {relative}: {error}") from error
        assets.append(
            {
                "id": relative,
                "relative_path": relative,
                "width": width,
                "height": height,
                "mode": mode,
                "format": detected_format,
                "frames": frame_count,
                "bytes": resolved.stat().st_size,
                "sha256": sha256_file(resolved),
            }
        )

    if not assets:
        raise InventoryError("the approved directory contains no supported image files")

    inventory = {
        "schema_version": 1,
        "approved_root": str(root),
        "assets": assets,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(inventory, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"Inventoried {len(assets)} approved image(s) from {root} into {output}.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (InventoryError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(2)
