"""A model turned round, for the model viewer (TamrielMerge.Cli model-preview, Skyrim/Models/ModelCatalog.cs): the
scene of one mesh (scene-mesh) drawn by render_scene.py from FRAMES angles round it, a little above, on a plain floor
under a soft sun, framed to fit.

  preview_model.py SCENE OUT_DIR [--frames 24] [--size 512]     writes OUT_DIR/view_NN.png and info.json (size)
"""
import argparse
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from render_scene import Renderer  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scene"); ap.add_argument("out")
    ap.add_argument("--frames", type=int, default=24); ap.add_argument("--size", type=int, default=512)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    r = Renderer(a.scene, a.size, a.size, ss=2, max_texture=1024)
    lo, hi = np.array(r.bounds_min, float), np.array(r.bounds_max, float)
    if not np.all(np.isfinite(lo)):
        json.dump({"empty": True}, open(os.path.join(a.out, "info.json"), "w"))
        print("nothing to draw"); return
    centre = (lo + hi) / 2
    radius = max(float(np.linalg.norm(hi - lo)) / 2, 8.0)
    fov = 35.0
    dist = radius / math.sin(math.radians(fov) / 2) * 1.08
    elev = math.radians(20)
    r.set_light(azimuth=210, elevation=45, sun=(1.0, 0.95, 0.88), sun_power=2.6, sky=(0.5, 0.55, 0.62),
                ground=(0.35, 0.33, 0.3), fog=(0.62, 0.64, 0.68), fog_density=0.0, exposure=1.0, cloud_cover=0.0)
    for i in range(a.frames):
        ang = 2 * math.pi * i / a.frames - math.pi / 2
        eye = centre + np.array([math.cos(ang) * math.cos(elev), math.sin(ang) * math.cos(elev), math.sin(elev)]) * dist
        r.render(eye, centre, fov, os.path.join(a.out, f"view_{i:02d}.png"))
    size = (hi - lo).tolist()
    json.dump({"size": size, "frames": a.frames}, open(os.path.join(a.out, "info.json"), "w"))
    print(f"{a.frames} views, size {[round(v) for v in size]}")


if __name__ == "__main__":
    main()
