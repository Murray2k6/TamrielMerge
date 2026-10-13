"""Checks every made model against its sprite (TamrielMerge rule: no model blocky; each must match its sprite):
- fidelity: the model's outline seen through TripoSR's camera (the sprite's own view) against the sprite's outline,
  as intersection over union (1 = identical); below --min it is flagged;
- a contact sheet of every model, smooth-shaded as Skyrim draws it, with its score, for looking over.

usage: python check.py MODELS_DIR SPRITES_LIST [--min 0.85] [--sheet out.png]
Writes MODELS_DIR/../fidelity.txt.
"""
import argparse
import math
import os

import numpy as np
import trimesh
from PIL import Image, ImageDraw

from render import colour_gain, render
from reproject import INPUT_SIZE, fitted_projection, grow, sprite_mask


def outline(mesh, mask):
    """The model's filled outline through TripoSR's input camera, fitted to the sprite's extent (as it is painted)."""
    px, py = fitted_projection(mesh.vertices, mask)
    img = Image.new("L", (INPUT_SIZE, INPUT_SIZE), 0)
    draw = ImageDraw.Draw(img)
    for a, b, c in mesh.faces:
        draw.polygon([(px[a], py[a]), (px[b], py[b]), (px[c], py[c])], fill=255)
    return np.asarray(img) > 127


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("models")
    ap.add_argument("listing")
    ap.add_argument("--min", type=float, default=0.85)
    ap.add_argument("--sheet", default=None)
    args = ap.parse_args()
    jobs = []
    with open(args.listing, encoding="utf-8-sig") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) >= 2 and os.path.exists(os.path.join(args.models, parts[0], "mesh.obj")):
                jobs.append((parts[0], parts[1], len(parts) > 4 and parts[4] in ("Plant", "Tree")))
    results, tiles = [], []
    for name, sprite, cutout in jobs:
        d = os.path.join(args.models, name)
        mesh = trimesh.load(os.path.join(d, "mesh.obj"), process=False)
        want = sprite_mask(sprite)
        have = outline(mesh, want)
        if cutout:
            have &= grow(want, 2)   # cut out where the sprite is not (alpha test)
        iou = (want & have).sum() / max((want | have).sum(), 1)
        results.append((name, iou))
        tex = np.asarray(Image.open(os.path.join(d, "texture.png")).convert("RGBA"))
        tile = render(mesh, tex, colour_gain(tex, os.path.join(d, "input.png")), math.radians(30), 192)
        ImageDraw.Draw(tile).text((4, 4), f"{name} {iou:.2f}", fill=(255, 90, 90) if iou < args.min else (230, 230, 230))
        tiles.append(tile)
        print(f"{name}: {iou:.3f}{'  LOW' if iou < args.min else ''}", flush=True)
    with open(os.path.join(args.models, "..", "fidelity.txt"), "w", encoding="utf-8") as f:
        for name, iou in results:
            f.write(f"{name}\t{iou:.3f}\n")
    if tiles:
        cols = 10
        sheet = Image.new("RGB", (192 * cols, 192 * ((len(tiles) + cols - 1) // cols)), (34, 34, 34))
        for i, t in enumerate(tiles):
            sheet.paste(t, (i % cols * 192, i // cols * 192))
        sheet.save(args.sheet or os.path.join(args.models, "..", "models.png"))
    low = [n for n, s in results if s < args.min]
    print(f"{len(results)} models, mean fidelity {np.mean([s for _, s in results]):.3f}, {len(low)} below {args.min}: {', '.join(low)}")


if __name__ == "__main__":
    main()
