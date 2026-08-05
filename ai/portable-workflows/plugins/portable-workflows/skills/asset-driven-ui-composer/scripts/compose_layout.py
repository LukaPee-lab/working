#!/usr/bin/env python3
"""Compose a PNG from hash-verified inventory assets and explicit JSON layers."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path, PurePosixPath
from typing import Any


MAX_DIMENSION = 16384
ROOT_FIELDS = {"name", "canvas", "layers"}
CANVAS_FIELDS = {"width", "height", "background"}
LAYER_FIELDS = {"asset", "x", "y", "resize", "opacity"}
RESIZE_FIELDS = {"width", "height", "resample"}
HASH_PATTERN = re.compile(r"^[0-9a-f]{64}$")


class ComposeError(ValueError):
    """Raised when a layout cannot be composed within the approved boundary."""


def load_pillow() -> tuple[Any, Any, str]:
    try:
        import PIL
        from PIL import Image, ImageColor
    except ImportError as error:
        raise ComposeError("Pillow is required; install it with: python -m pip install Pillow") from error
    return Image, ImageColor, PIL.__version__


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def load_json(path: Path, label: str) -> Any:
    if not path.is_file():
        raise ComposeError(f"{label} does not exist: {path}")
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        raise ComposeError(
            f"invalid {label} JSON at line {error.lineno}, column {error.colno}: {error.msg}"
        ) from error


def require_object(value: Any, location: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ComposeError(f"{location} must be an object")
    return value


def reject_unknown(value: dict[str, Any], allowed: set[str], location: str) -> None:
    unknown = sorted(set(value) - allowed)
    if unknown:
        raise ComposeError(f"{location} contains unknown field(s): {', '.join(unknown)}")


def positive_int(value: Any, location: str, maximum: int = MAX_DIMENSION) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not 1 <= value <= maximum:
        raise ComposeError(f"{location} must be an integer from 1 through {maximum}")
    return value


def nonnegative_int(value: Any, location: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ComposeError(f"{location} must be a non-negative integer")
    return value


def validate_asset_id(value: Any, location: str) -> str:
    if not isinstance(value, str) or not value:
        raise ComposeError(f"{location} must be a non-empty inventory asset ID")
    if "\\" in value:
        raise ComposeError(f"{location} must use slash-separated inventory IDs")
    parts = PurePosixPath(value).parts
    if PurePosixPath(value).is_absolute() or not parts or any(part in {"", ".", ".."} for part in parts):
        raise ComposeError(f"{location} must be a safe relative inventory asset ID")
    return value


def inventory_assets(inventory: Any, inventory_path: Path) -> tuple[Path, dict[str, dict[str, Any]]]:
    root_object = require_object(inventory, "inventory")
    if root_object.get("schema_version") != 1:
        raise ComposeError("inventory.schema_version must be 1")
    approved_root_value = root_object.get("approved_root")
    if not isinstance(approved_root_value, str) or not Path(approved_root_value).is_absolute():
        raise ComposeError("inventory.approved_root must be an absolute directory path")
    approved_root = Path(approved_root_value).resolve(strict=True)
    if not approved_root.is_dir():
        raise ComposeError(f"inventory.approved_root is not a directory: {approved_root}")

    records = root_object.get("assets")
    if not isinstance(records, list) or not records:
        raise ComposeError("inventory.assets must be a non-empty array")
    by_id: dict[str, dict[str, Any]] = {}
    for index, raw_record in enumerate(records):
        location = f"inventory.assets[{index}]"
        record = require_object(raw_record, location)
        asset_id = validate_asset_id(record.get("id"), f"{location}.id")
        if record.get("relative_path") != asset_id:
            raise ComposeError(f"{location}.relative_path must equal its id")
        if asset_id in by_id:
            raise ComposeError(f"duplicate inventory asset ID: {asset_id}")
        positive_int(record.get("width"), f"{location}.width")
        positive_int(record.get("height"), f"{location}.height")
        if not isinstance(record.get("mode"), str) or not record["mode"]:
            raise ComposeError(f"{location}.mode must be a non-empty string")
        if not isinstance(record.get("sha256"), str) or not HASH_PATTERN.fullmatch(record["sha256"]):
            raise ComposeError(f"{location}.sha256 must be a lowercase SHA-256 digest")
        source = (approved_root / Path(*PurePosixPath(asset_id).parts)).resolve(strict=True)
        if not source.is_file() or not is_within(source, approved_root):
            raise ComposeError(f"inventory asset is outside the approved root or missing: {asset_id}")
        record_copy = dict(record)
        record_copy["source_path"] = source
        by_id[asset_id] = record_copy
    return approved_root, by_id


def normalize_layout(layout: Any, assets: dict[str, dict[str, Any]], ImageColor: Any) -> dict[str, Any]:
    root = require_object(layout, "layout")
    reject_unknown(root, ROOT_FIELDS, "layout")
    name = root.get("name", "Untitled composition")
    if not isinstance(name, str) or not name.strip():
        raise ComposeError("layout.name must be a non-empty string when provided")

    canvas_raw = require_object(root.get("canvas"), "layout.canvas")
    reject_unknown(canvas_raw, CANVAS_FIELDS, "layout.canvas")
    width = positive_int(canvas_raw.get("width"), "layout.canvas.width")
    height = positive_int(canvas_raw.get("height"), "layout.canvas.height")
    background_raw = canvas_raw.get("background", "#00000000")
    if not isinstance(background_raw, str):
        raise ComposeError("layout.canvas.background must be a color string")
    try:
        background = ImageColor.getcolor(background_raw, "RGBA")
    except ValueError as error:
        raise ComposeError(f"layout.canvas.background is not a valid color: {background_raw}") from error

    layers_raw = root.get("layers")
    if not isinstance(layers_raw, list) or not layers_raw:
        raise ComposeError("layout.layers must be a non-empty array")

    layers: list[dict[str, Any]] = []
    for index, raw_layer in enumerate(layers_raw):
        location = f"layout.layers[{index}]"
        layer = require_object(raw_layer, location)
        reject_unknown(layer, LAYER_FIELDS, location)
        asset_id = validate_asset_id(layer.get("asset"), f"{location}.asset")
        if asset_id not in assets:
            raise ComposeError(f"{location}.asset is not in the approved inventory: {asset_id}")
        x = nonnegative_int(layer.get("x"), f"{location}.x")
        y = nonnegative_int(layer.get("y"), f"{location}.y")

        opacity = layer.get("opacity", 1.0)
        if isinstance(opacity, bool) or not isinstance(opacity, (int, float)) or not 0 <= opacity <= 1:
            raise ComposeError(f"{location}.opacity must be a number from 0 through 1")

        resize = None
        if "resize" in layer:
            resize_raw = require_object(layer["resize"], f"{location}.resize")
            reject_unknown(resize_raw, RESIZE_FIELDS, f"{location}.resize")
            resize_width = positive_int(resize_raw.get("width"), f"{location}.resize.width")
            resize_height = positive_int(resize_raw.get("height"), f"{location}.resize.height")
            resample = resize_raw.get("resample", "lanczos")
            if resample not in {"nearest", "bilinear", "bicubic", "lanczos"}:
                raise ComposeError(
                    f"{location}.resize.resample must be nearest, bilinear, bicubic, or lanczos"
                )
            resize = {"width": resize_width, "height": resize_height, "resample": resample}

        placed_width = resize["width"] if resize else assets[asset_id]["width"]
        placed_height = resize["height"] if resize else assets[asset_id]["height"]
        if x + placed_width > width or y + placed_height > height:
            raise ComposeError(f"{location} extends beyond the canvas; cropping is not implicit")
        layers.append(
            {
                "asset": asset_id,
                "x": x,
                "y": y,
                "opacity": float(opacity),
                "resize": resize,
                "placed_width": placed_width,
                "placed_height": placed_height,
            }
        )

    return {
        "name": name.strip(),
        "canvas": {"width": width, "height": height, "background": background, "background_input": background_raw},
        "layers": layers,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inventory", type=Path, help="inventory JSON from inventory_assets.py")
    parser.add_argument("layout", type=Path, help="explicit layout JSON")
    parser.add_argument("output", type=Path, help="PNG output path outside the approved root")
    parser.add_argument("provenance", type=Path, help="provenance JSON path outside the approved root")
    parser.add_argument("--force", action="store_true", help="replace existing output and provenance files")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    inventory_path = args.inventory.expanduser().resolve()
    layout_path = args.layout.expanduser().resolve()
    output_path = args.output.expanduser().resolve()
    provenance_path = args.provenance.expanduser().resolve()
    if output_path.suffix.lower() != ".png":
        raise ComposeError("output path must end in .png")
    if len({inventory_path, layout_path, output_path, provenance_path}) != 4:
        raise ComposeError("inventory, layout, output, and provenance paths must all be different")
    if not args.force:
        existing = [str(path) for path in (output_path, provenance_path) if path.exists()]
        if existing:
            raise ComposeError("output already exists; pass --force to replace: " + ", ".join(existing))

    Image, ImageColor, pillow_version = load_pillow()
    inventory_raw = load_json(inventory_path, "inventory")
    layout_raw = load_json(layout_path, "layout")
    approved_root, assets = inventory_assets(inventory_raw, inventory_path)
    if is_within(output_path, approved_root) or is_within(provenance_path, approved_root):
        raise ComposeError("PNG and provenance outputs must be outside the approved asset directory")
    layout = normalize_layout(layout_raw, assets, ImageColor)

    resampling = {
        "nearest": Image.Resampling.NEAREST,
        "bilinear": Image.Resampling.BILINEAR,
        "bicubic": Image.Resampling.BICUBIC,
        "lanczos": Image.Resampling.LANCZOS,
    }
    canvas = Image.new(
        "RGBA",
        (layout["canvas"]["width"], layout["canvas"]["height"]),
        layout["canvas"]["background"],
    )
    provenance_layers: list[dict[str, Any]] = []
    for index, layer in enumerate(layout["layers"]):
        record = assets[layer["asset"]]
        source_path = record["source_path"]
        actual_hash = sha256_file(source_path)
        if actual_hash != record["sha256"]:
            raise ComposeError(f"source hash changed after inventory: {layer['asset']}")
        with Image.open(source_path) as source:
            source.load()
            if source.size != (record["width"], record["height"]):
                raise ComposeError(f"source dimensions changed after inventory: {layer['asset']}")
            if source.mode != record["mode"]:
                raise ComposeError(f"source mode changed after inventory: {layer['asset']}")
            layer_image = source.copy() if source.mode == "RGBA" else source.convert("RGBA")

        if layer["resize"]:
            resize = layer["resize"]
            layer_image = layer_image.resize(
                (resize["width"], resize["height"]),
                resample=resampling[resize["resample"]],
            )
        if layer["opacity"] < 1.0:
            alpha = layer_image.getchannel("A").point(lambda value: round(value * layer["opacity"]))
            layer_image.putalpha(alpha)
        canvas.alpha_composite(layer_image, dest=(layer["x"], layer["y"]))

        provenance_layers.append(
            {
                "order": index,
                "asset": layer["asset"],
                "source_sha256": record["sha256"],
                "original": {
                    "width": record["width"],
                    "height": record["height"],
                    "mode": record["mode"],
                },
                "placement": {
                    "x": layer["x"],
                    "y": layer["y"],
                    "width": layer["placed_width"],
                    "height": layer["placed_height"],
                },
                "resize": layer["resize"],
                "opacity": layer["opacity"],
            }
        )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    provenance_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output_path, format="PNG")
    provenance = {
        "schema_version": 1,
        "name": layout["name"],
        "approved_root": str(approved_root),
        "source_assets_preserved": True,
        "inventory_sha256": sha256_file(inventory_path),
        "layout_sha256": sha256_file(layout_path),
        "output": {
            "path": str(output_path),
            "format": "PNG",
            "mode": "RGBA",
            "width": layout["canvas"]["width"],
            "height": layout["canvas"]["height"],
            "background": layout["canvas"]["background_input"],
            "sha256": sha256_file(output_path),
        },
        "renderer": {"library": "Pillow", "version": pillow_version},
        "layers": provenance_layers,
    }
    provenance_path.write_text(
        json.dumps(provenance, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(
        f"Composed {len(provenance_layers)} approved layer(s) into {output_path}; "
        f"provenance: {provenance_path}."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ComposeError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(2)
