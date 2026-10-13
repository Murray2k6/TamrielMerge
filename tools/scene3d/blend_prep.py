"""Prepares a scene exported by `scene-export` for path tracing in Blender (blend_scene.py): its textures as PNGs no
bigger than --max (Blender's Cycles keeps textures uncompressed, so they are kept small; the intro's shots are aerial),
and the land's painted texture weights normalised and packed four layers to an image.

  blend_prep.py SCENE [--max 512]     writes SCENE/blend/ (tex/*.png, land_weights_N.png, manifest.json)
"""
import argparse
import hashlib
import json
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(__file__))
from render_scene import read_land, read_mesh  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scene"); ap.add_argument("--max", type=int, default=512)
    a = ap.parse_args()
    out = os.path.join(a.scene, "blend"); tex_out = os.path.join(out, "tex")
    os.makedirs(tex_out, exist_ok=True)
    scene = json.load(open(os.path.join(a.scene, "scene.json")))
    textures = {}

    def convert(path, size=None, cap=None):
        if not path:
            return None
        if path in textures:
            return textures[path]
        src = os.path.join(a.scene, path.replace("\\", os.sep))
        name = hashlib.md5(path.lower().encode()).hexdigest()[:16] + ".png"
        dst = os.path.join(tex_out, name)
        if not os.path.exists(dst):
            try:
                img = Image.open(src).convert("RGBA")
                limit = size or cap or a.max
                if size:
                    img = img.resize((size, size), Image.LANCZOS)
                elif max(img.size) > limit:
                    s = limit / max(img.size)
                    img = img.resize((max(1, int(img.width * s)), max(1, int(img.height * s))), Image.LANCZOS)
                img.save(dst)
            except Exception as e:
                print(f"  texture {path}: {e}", file=sys.stderr)
                textures[path] = None
                return None
        textures[path] = "tex/" + name
        return textures[path]

    names = scene["meshes"]
    used = {i["mesh"] for i in scene["instances"]}
    # The town's people: their parts, standing and at each step of the walk.
    for person in scene.get("people", []):
        used.update(person["parts"])
        for step in person.get("walk") or []:
            used.update(step)
    # The grass the land grows (terrain "grasses": each land texture's).
    for gl in (scene.get("terrain") or {}).get("grasses") or []:
        used.update(g["mesh"] for g in gl)
    margin = next((i for i, m in enumerate(names) if "margin" in m.lower()), None)
    if margin is not None:
        used.add(margin)
    for mesh_id in sorted(used):
        p = os.path.join(a.scene, "meshes", f"{mesh_id}.bin")
        if os.path.exists(p) and os.path.getsize(p) > 0:
            for s in read_mesh(p):
                convert(s["diffuse"])
    manifest = {"textures": textures, "margin": margin}
    # The weather's cloud layers (lighting.json, from the scene export), kept larger: they span the sky.
    lp = os.path.join(a.scene, "lighting.json")
    if os.path.exists(lp):
        for c in json.load(open(lp)).get("clouds") or []:
            t = c["texture"].lower()
            convert(t if t.startswith("textures\\") else "textures\\" + t, cap=max(a.max, 1024))

    land = os.path.join(a.scene, "land.bin")
    cells = read_land(land) if os.path.exists(land) else []
    if cells:
        gxs = [c[0] for c in cells]; gys = [c[1] for c in cells]
        x0, y0, x1, y1 = min(gxs), min(gys), max(gxs), max(gys)
        W, H = (x1 - x0 + 1) * 32 + 1, (y1 - y0 + 1) * 32 + 1
        layers = scene["terrain"]["textures"]
        weights = np.zeros((max(1, len(layers)), H, W), np.float32)
        for gx, gy, h, ws in cells:
            ox, oy = (gx - x0) * 32, (gy - y0) * 32
            for tid, w in ws.items():
                weights[tid, oy:oy + 33, ox:ox + 33] = np.maximum(weights[tid, oy:oy + 33, ox:ox + 33], w)
        total = weights.sum(0, keepdims=True)
        weights = np.where(total > 0.002, weights / np.maximum(total, 1e-6), 0)
        # Layers that matter, four to an image (rows flipped: Blender's images start at the bottom).
        keep = [i for i in range(len(layers)) if weights[i].max() > 0.02]
        groups = []
        for g in range(0, len(keep), 4):
            ids = keep[g:g + 4]
            rgba = np.zeros((H, W, 4), np.uint8)
            for c, i in enumerate(ids):
                rgba[..., c] = (weights[i] * 255 + 0.5).astype(np.uint8)
            name = f"land_weights_{g // 4}.png"
            Image.fromarray(rgba[::-1], "RGBA").save(os.path.join(out, name))
            groups.append({"image": name, "layers": [convert(layers[i], 512) for i in ids]})
        manifest["land"] = {"origin": [x0 * 4096, y0 * 4096], "size": [W, H], "groups": groups}
    json.dump(manifest, open(os.path.join(out, "manifest.json"), "w"), indent=1)
    print(f"{a.scene}: {sum(1 for v in textures.values() if v)} textures, land {'yes' if cells else 'no'} -> {out}")


if __name__ == "__main__":
    main()
