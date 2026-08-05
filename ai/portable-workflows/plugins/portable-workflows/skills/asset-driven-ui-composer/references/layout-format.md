# Inventory and layout formats

Use UTF-8 JSON. The scripts reject unknown layout fields and verify every source against its inventory record before rendering.

## Create an inventory

```shell
python scripts/inventory_assets.py approved-assets asset-inventory.json
```

The source directory is read recursively. Supported extensions are `.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`, `.bmp`, `.tif`, and `.tiff`. Symlinks and files that resolve outside the approved root are rejected. The output JSON records:

- the absolute approved root used for enforcement;
- a slash-separated relative path used as the asset ID;
- pixel width and height;
- image mode and detected format;
- frame count, byte size, and SHA-256 hash.

Place the inventory outside the approved source directory. Add `--force` only to replace the intended inventory file.

## Layout object

```json
{
  "name": "Library kiosk welcome panel",
  "canvas": {
    "width": 960,
    "height": 540,
    "background": "#EEF2F6"
  },
  "layers": [
    {
      "asset": "panels/base.png",
      "x": 40,
      "y": 32
    },
    {
      "asset": "controls/start-button.png",
      "x": 680,
      "y": 420,
      "resize": {
        "width": 220,
        "height": 72,
        "resample": "lanczos"
      },
      "opacity": 0.95
    }
  ]
}
```

### Root fields

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `name` | non-empty string | no | Human-readable composition name. |
| `canvas` | object | yes | Output canvas settings. |
| `layers` | array | yes | One or more layers, rendered in array order from back to front. |

### Canvas fields

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `width` | positive integer | yes | Canvas width in pixels, maximum 16384. |
| `height` | positive integer | yes | Canvas height in pixels, maximum 16384. |
| `background` | Pillow-compatible color string | no | Defaults to transparent `#00000000`. |

### Layer fields

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `asset` | string | yes | Exact inventory asset ID, never a free-form filesystem path. |
| `x` | non-negative integer | yes | Left position in pixels. |
| `y` | non-negative integer | yes | Top position in pixels. |
| `resize` | object | no | Explicit opt-in resize. If omitted, native dimensions are retained. |
| `opacity` | number from 0 through 1 | no | Layer opacity; defaults to 1. |

`resize` requires positive integer `width` and `height`. Its optional `resample` value is `nearest`, `bilinear`, `bicubic`, or `lanczos`; the default is `lanczos`. Every placed layer must fit fully inside the canvas. Cropping and implicit fit-to-canvas behavior are intentionally unsupported.

## Compose and verify

```shell
python scripts/compose_layout.py asset-inventory.json layout.json mockup.png mockup.provenance.json
```

The PNG and provenance paths must differ from the inventory and layout paths and must remain outside the approved root. The provenance JSON includes the inventory and layout hashes, output hash, Pillow version, canvas settings, layer order, original and placed dimensions, source hashes, resize decisions, and opacity. Add `--force` only to replace both intended output files.
