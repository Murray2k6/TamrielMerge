"""Renders a made model (mesh.obj + texture.png) from a few angles with its texture and simple lighting, next to its
input sprite: a picture of the model without a 3D viewer.  usage: python render.py MODEL_DIR [out.png]"""
import math
import os
import sys

import numpy as np
import trimesh
from PIL import Image


def colour_gain(texture, input_png):
    """As the importer does: brighten the bake to the sprite's foreground mean."""
    inp = np.asarray(Image.open(input_png).convert("RGB")).reshape(-1, 3).astype(float)
    fg = inp[np.abs(inp - 127).sum(1) > 12]
    painted = texture.reshape(-1, 4)
    painted = painted[painted[:, 3] > 0][:, :3].astype(float)
    if len(fg) == 0 or len(painted) == 0:
        return np.ones(3)
    return np.clip(fg.mean(0) / np.maximum(painted.mean(0), 1), 1, 3)


def render(mesh, tex, gain, yaw, size=512):
    v = np.asarray(mesh.vertices, float)
    # TripoSR: x towards the viewer, y across, z up. Turn about z by yaw, look along -x.
    c, s = math.cos(yaw), math.sin(yaw)
    x, y, z = v[:, 0] * c - v[:, 1] * s, v[:, 0] * s + v[:, 1] * c, v[:, 2]
    lo, hi = np.array([y.min(), z.min()]), np.array([y.max(), z.max()])
    scale = (size * 0.9) / max(hi - lo)
    sx = (y - (lo[0] + hi[0]) / 2) * scale + size / 2
    sy = size / 2 - (z - (lo[1] + hi[1]) / 2) * scale
    depth = x
    uv = np.asarray(mesh.visual.uv, float)
    th, tw = tex.shape[:2]
    light = np.array([0.6, -0.3, 0.75]); light /= np.linalg.norm(light)
    # Smooth shading as Skyrim draws it: normals per vertex (welded across texture seams), interpolated per pixel.
    key = np.round(v / 1e-5).astype(np.int64)
    _, weld, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inverse = inverse.reshape(-1)
    welded = trimesh.Trimesh(vertices=v[weld], faces=inverse[np.asarray(mesh.faces)], process=False)
    vn = np.asarray(welded.vertex_normals, float)[inverse]
    vn_rot = np.stack([vn[:, 0] * c - vn[:, 1] * s, vn[:, 0] * s + vn[:, 1] * c, vn[:, 2]], 1)
    # Soft matte light from the front-left and above (ambient + Lambert), roughly a lit Skyrim interior.
    shade = 0.55 + 0.45 * np.clip(vn_rot @ light, 0, 1)
    img = np.full((size, size, 3), 34, float)
    zbuf = np.full((size, size), -np.inf)
    for f, (a, b, d) in enumerate(mesh.faces):
        xs, ys = np.array([sx[a], sx[b], sx[d]]), np.array([sy[a], sy[b], sy[d]])
        x0, x1 = max(int(xs.min()), 0), min(int(xs.max()) + 1, size - 1)
        y0, y1 = max(int(ys.min()), 0), min(int(ys.max()) + 1, size - 1)
        if x0 > x1 or y0 > y1:
            continue
        den = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
        if abs(den) < 1e-9:
            continue
        px, py = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        w0 = ((ys[1] - ys[2]) * (px - xs[2]) + (xs[2] - xs[1]) * (py - ys[2])) / den
        w1 = ((ys[2] - ys[0]) * (px - xs[2]) + (xs[0] - xs[2]) * (py - ys[2])) / den
        w2 = 1 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        dz = w0 * depth[a] + w1 * depth[b] + w2 * depth[d]
        iy, ix = np.nonzero(inside)
        gy, gx = iy + y0, ix + x0
        zz = dz[iy, ix]
        front = zz > zbuf[gy, gx]
        if not front.any():
            continue
        gy, gx, iy, ix, zz = gy[front], gx[front], iy[front], ix[front], zz[front]
        u = w0[iy, ix] * uv[a, 0] + w1[iy, ix] * uv[b, 0] + w2[iy, ix] * uv[d, 0]
        t = w0[iy, ix] * uv[a, 1] + w1[iy, ix] * uv[b, 1] + w2[iy, ix] * uv[d, 1]
        texel = tex[np.clip(((1 - t) * (th - 1)).astype(int), 0, th - 1), np.clip((u * (tw - 1)).astype(int), 0, tw - 1)]
        # Alpha test, as Skyrim draws cut-out foliage: transparent texels show what is behind.
        opaque = texel[:, 3] >= 128
        if not opaque.any():
            continue
        gy, gx, iy, ix, zz, texel = gy[opaque], gx[opaque], iy[opaque], ix[opaque], zz[opaque], texel[opaque]
        col = texel[:, :3].astype(float)
        lit = (w0[iy, ix] * shade[a] + w1[iy, ix] * shade[b] + w2[iy, ix] * shade[d])[:, None]
        img[gy, gx] = np.clip(col * gain * lit, 0, 255)
        zbuf[gy, gx] = zz
    return Image.fromarray(img.astype(np.uint8))


def main():
    d = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(d, "render.png")
    mesh = trimesh.load(os.path.join(d, "mesh.obj"), process=False)
    tex = np.asarray(Image.open(os.path.join(d, "texture.png")).convert("RGBA"))
    gain = colour_gain(tex, os.path.join(d, "input.png"))
    size = 400
    sheet = Image.new("RGB", (size * 4, size), (34, 34, 34))
    sheet.paste(Image.open(os.path.join(d, "input.png")).resize((size, size)), (0, 0))
    for i, yaw in enumerate([0.0, math.radians(40), math.radians(110)]):
        sheet.paste(render(mesh, tex, gain, yaw, size), (size * (i + 1), 0))
    sheet.save(out)
    print(out, len(mesh.vertices), "vertices", len(mesh.faces), "faces")


if __name__ == "__main__":
    main()
