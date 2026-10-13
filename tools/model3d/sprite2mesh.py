"""Daggerfall sprites -> textured 3D models (TamrielMerge, for things Skyrim has no model of: seagulls, palms, cacti...).

Each sprite (an RGBA PNG, transparency = Daggerfall's palette index 0) is enlarged, centred on TripoSR's grey
background, and turned into a mesh with TripoSR (VAST-AI-Research/TripoSR, MIT; weights stabilityai/TripoSR), whose
colour is baked into a texture atlas. Writes <out>/<name>/mesh.obj + texture.png; finished sprites are skipped, so a
run can be stopped and resumed; ones made by an older version of this pipeline (pipeline.txt) are made again.

The GPU is kept cool: between sprites the script reads nvidia-smi and waits while the card is above --max-temp.

usage: python sprite2mesh.py --list sprites.txt --out DIR [--mc 192] [--tex 1024] [--chunk 4096] [--max-temp 78]
  sprites.txt: one "name<TAB>path.png" per line.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time

import numpy as np
import trimesh
import torch
import xatlas
from PIL import Image

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "TripoSR"))
from tsr.system import TSR  # noqa: E402
from tsr.bake_texture import bake_texture  # noqa: E402
from reproject import reproject  # noqa: E402
from carve import carved_mesh  # noqa: E402
from reproject import sprite_mask, grow  # noqa: E402
from check import outline  # noqa: E402


def gpu_temperature():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=10).stdout.strip().splitlines()
        return int(out[0]) if out else None
    except Exception:
        return None


def cool_down(max_temp):
    t = gpu_temperature()
    while t is not None and t > max_temp:
        print(f"  GPU at {t} C, waiting to cool below {max_temp} C", flush=True)
        time.sleep(20)
        t = gpu_temperature()


UPSCALER = None
UPSCALER_WEIGHTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "upscale", "RealESRGAN_x4plus.pth")


def upscale(sprite, device):
    """Real-ESRGAN x4plus (tools/upscale), twice for tiny sprites: Daggerfall's pixel art becomes shapes TripoSR can
    read depth from (enlarged pixels make blocky slabs). Colour and alpha are upscaled separately."""
    global UPSCALER
    if UPSCALER is None:
        import spandrel
        weights = UPSCALER_WEIGHTS
        UPSCALER = spandrel.ModelLoader().load_from_file(weights).to(device).eval()

    def run(img):
        t = torch.from_numpy(np.asarray(img, np.float32) / 255.0).permute(2, 0, 1)[None].to(device)
        with torch.no_grad():
            out = UPSCALER(t).clamp(0, 1)[0].permute(1, 2, 0).cpu().numpy()
        return Image.fromarray((out * 255).astype(np.uint8))

    passes = 2 if max(sprite.size) < 96 else 1
    rgb, alpha = sprite.convert("RGB"), sprite.getchannel("A").convert("RGB")
    for _ in range(passes):
        rgb, alpha = run(rgb), run(alpha)
    out = rgb.convert("RGBA")
    out.putalpha(alpha.convert("L").point(lambda a: 255 if a > 127 else 0))
    return out


def prepare(path, device, size=512, ratio=0.85):
    """The sprite upscaled and centred on grey, as TripoSR expects."""
    sprite = Image.open(path).convert("RGBA")
    bbox = sprite.getbbox()
    if bbox:
        sprite = sprite.crop(bbox)
    sprite = upscale(sprite, device)
    w, h = sprite.size
    scale = ratio * size / max(w, h)
    sprite = sprite.resize((max(1, round(w * scale)), max(1, round(h * scale))), Image.LANCZOS)
    canvas = Image.new("RGBA", (size, size), (127, 127, 127, 255))
    canvas.alpha_composite(sprite, ((size - sprite.width) // 2, (size - sprite.height) // 2))
    return canvas.convert("RGB")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--list", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--mc", type=int, default=192, help="marching cubes resolution")
    ap.add_argument("--tex", type=int, default=1024, help="texture atlas size")
    ap.add_argument("--chunk", type=int, default=4096, help="renderer chunk size (smaller = less GPU memory)")
    ap.add_argument("--max-temp", type=int, default=78)
    ap.add_argument("--device", default="cuda:0")
    ap.add_argument("--upscaler", help="Real-ESRGAN x4plus weights (default: ../upscale/RealESRGAN_x4plus.pth)")
    args = ap.parse_args()
    global UPSCALER_WEIGHTS
    if args.upscaler:
        UPSCALER_WEIGHTS = args.upscaler

    jobs = []
    with open(args.list, encoding="utf-8-sig") as f:
        for line in f:
            if line.strip():
                parts = line.rstrip("\n").split("\t")
                name, path = parts[0], parts[1]
                faces = int(parts[2]) if len(parts) > 2 else 5000
                whiten = float(parts[3]) if len(parts) > 3 else 1.0
                cutout = len(parts) > 4 and parts[4] in ("Plant", "Tree")
                if not current(os.path.join(args.out, name)):
                    jobs.append((name, path, faces, whiten, cutout))
    print(f"{len(jobs)} sprites to make", flush=True)
    if not jobs:
        return

    device = args.device if torch.cuda.is_available() else "cpu"
    model = TSR.from_pretrained("stabilityai/TripoSR", config_name="config.yaml", weight_name="model.ckpt")
    model.renderer.set_chunk_size(args.chunk)
    model.to(device)

    for i, (name, path, faces, whiten, cutout) in enumerate(jobs):
        cool_down(args.max_temp)
        started = time.time()
        out_dir = os.path.join(args.out, name)
        if os.path.isdir(out_dir):
            shutil.rmtree(out_dir)   # an older version's model: nothing of it is kept (reproject reads texture_ai.png)
        os.makedirs(out_dir, exist_ok=True)
        image = prepare(path, device)
        image.save(os.path.join(out_dir, "input.png"))
        with torch.no_grad():
            codes = model([image], device=device)
        # Carved by the sprite (its gaps go through, its thin parts are round) and smoothed as a solid before it
        # becomes a mesh (carve.py). Smoothing the finished mesh instead (Taubin, 2026-10-07 to 10-12) melted thin parts:
        # a statue's staff into its robe, its crown into a cone, its square plinth into a blob (user: "fix the robed
        # figure").
        mask = sprite_mask(path)
        meshes = [carved_mesh(model, codes[0], args.mc, mask, cutout)]
        # Game-sized: simplified to the sprite's face budget (Skyrim's own trees are a few thousand triangles) before
        # the texture is baked, so the texture fits the simplified shape.
        if len(meshes[0].faces) > faces:
            meshes[0] = meshes[0].simplify_quadric_decimation(face_count=faces)
        # Collision: the same shape, much simpler (each triangle becomes a convex slab in the NIF).
        collision = meshes[0].simplify_quadric_decimation(face_count=150) if len(meshes[0].faces) > 150 else meshes[0]
        collision.export(os.path.join(out_dir, "collision.obj"))
        baked = bake_texture(meshes[0], model, codes[0], args.tex)
        xatlas.export(os.path.join(out_dir, "mesh.obj"), meshes[0].vertices[baked["vmapping"]], baked["indices"], baked["uvs"],
                      meshes[0].vertex_normals[baked["vmapping"]])
        Image.fromarray((baked["colors"] * 255.0).astype(np.uint8)).transpose(Image.FLIP_TOP_BOTTOM).save(os.path.join(out_dir, "texture.png"))
        # Painted with the sprite itself (matte, Daggerfall's colours) instead of the bake's invented shading.
        reproject(out_dir, path, args.tex, whiten, cutout)
        if device.startswith("cuda"):
            torch.cuda.empty_cache()
        # How well the outline matches the sprite's (check.py's measure); the importer leaves out models below its
        # minimum (SpriteModelImporter.MinFidelity) and the stand-in stays.
        score = fidelity(trimesh.load(os.path.join(out_dir, "mesh.obj"), process=False), mask, cutout)
        with open(os.path.join(out_dir, "score.txt"), "w") as f:
            f.write(f"{score:.3f}")
        with open(os.path.join(out_dir, "pipeline.txt"), "w") as f:
            f.write(PIPELINE)
        print(f"[{i + 1}/{len(jobs)}] {name}: {len(meshes[0].vertices)} vertices, fidelity {score:.2f}, {time.time() - started:.1f}s, GPU {gpu_temperature()} C", flush=True)


# What each model has been through; one made by an older version is made again (models before 4 were smoothed as
# meshes, which melted thin parts, and their sides had the AI's coloured specks; 4's solid blur lost the thinnest).
PIPELINE = "5: carved by the sprite, smoothed as a solid keeping thin parts, scored, sprite-painted with AI shading on the sides, cut-out foliage"


def fidelity(mesh, mask, cutout):
    """check.py's measure: the outline through the sprite's view against the sprite's, intersection over union."""
    have = outline(mesh, mask)
    if cutout:
        have &= grow(mask, 2)
    return float((mask & have).sum() / max((mask | have).sum(), 1))


def current(model_dir):
    marker = os.path.join(model_dir, "pipeline.txt")
    return os.path.exists(os.path.join(model_dir, "mesh.obj")) and os.path.exists(marker) and open(marker).read() == PIPELINE


if __name__ == "__main__":
    main()
