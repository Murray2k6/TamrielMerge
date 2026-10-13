"""Poses of a perched creature's idle (as SpriteModelImporter keys it: the part above the perch turns and dips on the
perch's top), rendered side by side and as a looping GIF.  usage: python idle_preview.py MODEL_DIR"""
import math
import os
import sys

import numpy as np
import trimesh
from PIL import Image

from render import colour_gain, render


def perch(v):
    """As the importer: the first height past 8/25 where the spread jumps over 1.6x the perch's (z up here)."""
    lo, hi = v[:, 2].min(), v[:, 2].max()
    step = (hi - lo) / 25
    spreads = []
    for i in range(25):
        s = v[(v[:, 2] >= lo + i * step) & (v[:, 2] < lo + (i + 1) * step)]
        spreads.append(max(np.ptp(s[:, 0]), np.ptp(s[:, 1])) if len(s) else 0)
    base = sorted(x for x in spreads[:10] if x > 0)
    post = base[len(base) // 2] if base else 0
    for i in range(8, 25):
        if post > 0 and spreads[i] > post * 1.6:
            return lo + (i + 1) * step
    return None


def interp(keys, t):
    for (t0, a), (t1, b) in zip(keys, keys[1:]):
        if t0 <= t <= t1:
            return a + (b - a) * (t - t0) / (t1 - t0)
    return keys[-1][1]


TURN = [(0, 0), (2.5, 0), (3.0, 0.4), (5.5, 0.4), (6.2, -0.35), (8.5, -0.35), (9.0, 0), (12, 0)]
DIP = [(0, 0), (10.0, 0), (10.3, 0.18), (10.7, 0), (12, 0)]


def main():
    d = sys.argv[1]
    mesh = trimesh.load(os.path.join(d, "mesh.obj"), process=False)
    tex = np.asarray(Image.open(os.path.join(d, "texture.png")).convert("RGBA"))
    gain = colour_gain(tex, os.path.join(d, "input.png"))
    v0 = np.asarray(mesh.vertices, float)
    split = perch(v0)
    print("perch top at", None if split is None else round(float(split), 3), "of", round(float(v0[:, 2].min()), 3), "..", round(float(v0[:, 2].max()), 3))
    if split is None:
        return
    faces = np.asarray(mesh.faces)
    above_v = np.zeros(len(v0), bool)
    above_v[np.unique(faces[v0[faces].mean(1)[:, 2] > split])] = True
    pivot = np.array([v0[~above_v, 0].mean(), v0[~above_v, 1].mean(), split])
    frames = []
    for t in np.arange(0, 12, 0.25):
        turn, dip = interp(TURN, t), interp(DIP, t)
        bob = 0.012 * (v0[:, 2].max() - v0[:, 2].min()) * ((int(round(t / 0.75)) % 2) if True else 0)
        p = v0[above_v] - pivot
        # dip about the y axis, then turn about z (as the NIF's XYZ keys apply them)
        cy, sy = math.cos(dip), math.sin(dip)
        p = np.stack([p[:, 0] * cy + p[:, 2] * sy, p[:, 1], -p[:, 0] * sy + p[:, 2] * cy], 1)
        cz, sz = math.cos(turn), math.sin(turn)
        p = np.stack([p[:, 0] * cz - p[:, 1] * sz, p[:, 0] * sz + p[:, 1] * cz, p[:, 2]], 1)
        v = v0.copy()
        v[above_v] = p + pivot + [0, 0, bob]
        posed = trimesh.Trimesh(vertices=v, faces=faces, visual=mesh.visual, process=False)
        frames.append((t, render(posed, tex, gain, math.radians(30), 320)))
    strip = Image.new("RGB", (320 * 5, 320), (34, 34, 34))
    for i, t in enumerate([0, 4, 7.5, 10.25, 11.5]):
        strip.paste(min(frames, key=lambda f: abs(f[0] - t))[1], (320 * i, 0))
    strip.save(os.path.join(d, "idle.png"))
    frames[0][1].save(os.path.join(d, "idle.gif"), save_all=True, append_images=[f[1] for f in frames[1:]], duration=250, loop=0)
    print(os.path.join(d, "idle.png"))


if __name__ == "__main__":
    main()
