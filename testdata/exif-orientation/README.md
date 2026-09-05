# EXIF orientation fixtures

Eight JPEGs, one per EXIF orientation value (1-8). Every file stores *different*
pixels, but they all carry the orientation tag that turns those pixels back into
the **same** picture: a 600x400 landscape image with a red TOP bar, a blue BOTTOM
bar, a green left edge, an amber right edge, and a large asymmetric `F`.

Files 5-8 are stored as 400x600 **portrait** pixel arrays, so a viewer that
ignores the tag gets both the shape and the aspect ratio wrong.

## How to read the result

Open this folder in MoveCopyScrap and step through all eight.

| What you see | What it means |
|---|---|
| All eight identical, all landscape | Pixels *and* aspect ratio are orientation-aware. |
| All eight upright, but 5-8 sit in a portrait-shaped slot / are letterboxed | Pixels are oriented, the reported size is not. Fix the size query. |
| 5-8 appear rotated or mirrored | Nothing is orientation-aware. The decode path needs to orient explicitly. |

The `F` tells you *which* transform went wrong: a mirrored `F` means a flip is
being lost, a sideways `F` means a rotation is.

Generated with Pillow; each file is verified to round-trip to the canonical
image via `ImageOps.exif_transpose`.
