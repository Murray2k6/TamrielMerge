"""Quick look at made models without a 3D viewer: front, side and top views of each mesh (textured point splats with a
depth buffer), next to its input sprite, in one PNG per model.  usage: python preview.py DIR [names...]"""
import os
import sys

import numpy as np
import trimesh
from PIL import Image


def view(vertices, colours, axes, flip, size=256):
    p = vertices[:, axes]
    depth = vertices[:, 3 - sum(axes)] * (-1 if flip else 1)
    lo, hi = p.min(0), p.max(0)
    scale = (size - 8) / max((hi - lo).max(), 1e-6)
    xy = ((p - lo) * scale + 4).astype(int)
    img = np.full((size, size, 3), 40, np.uint8)
    zbuf = np.full((size, size), -np.inf)
    for (x, y), d, c in zip(xy, depth, colours):
        y = size - 1 - y
        for dx in (0, 1):
            for dy in (0, 1):
                xx, yy = min(x + dx, size - 1), min(y + dy, size - 1)
                if d > zbuf[yy, xx]:
                    zbuf[yy, xx] = d
                    img[yy, xx] = c
    return Image.fromarray(img)


def main():
    root = sys.argv[1]
    names = sys.argv[2:] or sorted(os.listdir(root))
    for name in names:
        d = os.path.join(root, name)
        if not os.path.exists(os.path.join(d, "mesh.obj")):
            continue
        mesh = trimesh.load(os.path.join(d, "mesh.obj"), process=False)
        tex = np.asarray(Image.open(os.path.join(d, "texture.png")).convert("RGB"))
        uv = mesh.visual.uv
        h, w = tex.shape[:2]
        cols = tex[np.clip(((1 - uv[:, 1]) * (h - 1)).astype(int), 0, h - 1), np.clip((uv[:, 0] * (w - 1)).astype(int), 0, w - 1)]
        v = np.asarray(mesh.vertices)
        sheet = Image.new("RGB", (256 * 4, 256), (40, 40, 40))
        sheet.paste(Image.open(os.path.join(d, "input.png")).resize((256, 256)), (0, 0))
        # TripoSR's frame: z up, y across the picture, x towards the viewer. Front (y-z), side (x-z), top (y-x).
        sheet.paste(view(v, cols, [1, 2], False), (256, 0))
        sheet.paste(view(v, cols, [0, 2], True), (512, 0))
        sheet.paste(view(v, cols, [1, 0], False), (768, 0))
        sheet.save(os.path.join(d, "preview.png"))
        print(name, "bounds", np.round(v.min(0), 3), np.round(v.max(0), 3))


if __name__ == "__main__":
    main()
