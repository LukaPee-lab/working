---
name: asset-driven-ui-composer
description: Inventory an explicitly approved local image directory and compose traceable PNG interface mockups from declarative JSON layers. Use when Codex must arrange existing PNG, JPEG, WebP, GIF, BMP, or TIFF assets without altering the originals, enforce an approved-asset boundary, avoid implicit resizing, and produce a machine-readable provenance report for the rendered result.
---

# Asset-driven UI Composer

Compose with the bundled scripts so every source is approved, hash-verified, and traceable. Never modify source images.

## Workflow

1. Read [references/layout-format.md](references/layout-format.md) before writing a layout.
2. Confirm the exact directory the user approves for source images. Inventory only that directory:

   ```shell
   python scripts/inventory_assets.py approved-assets asset-inventory.json
   ```

3. Review the inventory and inspect the relevant images. Reference assets by their inventory IDs; do not use filesystem paths in layout layers.
4. Author an explicit layout JSON. Omit `resize` to retain an asset's native dimensions. Add `resize` only when the requested design requires it.
5. Compose the PNG and provenance report outside the approved asset directory:

   ```shell
   python scripts/compose_layout.py asset-inventory.json layout.json mockup.png mockup.provenance.json
   ```

   Add `--force` only when replacing both intended outputs.
6. Inspect the rendered PNG visually, then verify the provenance layer order, source hashes, original dimensions, placed dimensions, and transformations.

## Guardrails

- Do not copy, rename, rewrite, recolor, crop, or otherwise alter approved source assets.
- Do not bypass a missing inventory entry with an absolute or relative path. Obtain approval and rebuild the inventory when another asset is needed.
- Do not resize by default. A layer is resized only when its own `resize` object supplies explicit width and height.
- Keep the layout and both outputs outside the approved source directory.
- Treat a source hash, mode, or dimension mismatch as a changed approval boundary. Stop and rebuild the inventory before composing.
- Use Pillow through the scripts; if it is unavailable, report that dependency instead of attempting a different image-editing route.
