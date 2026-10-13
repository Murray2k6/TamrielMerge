"""The model viewer's PBR tool (user, 2026-10-08: "a texture selector with the model viewer with a pbr tool", PBR of
our own, without Community Shaders). From a colour texture it makes a physically based set — normals (an AI model,
DeepBump: GPL-3.0, downloaded by setup, run here by our own code), height (the normals integrated), roughness,
metalness and ambient occlusion — every map tunable, then bakes the set into what Skyrim's own shaders draw:

  colour   (_d)  the albedo, darkened in its crevices by the occlusion;
  normals  (_n)  the normals, their alpha the specular amount from the roughness;
  height   (_p)  for the parallax shader;
  env mask (_m)  where the surface reflects, from metalness and smoothness (the environment-map shader).

  pbr.py thumb IN OUT.png [--size 256]                         a picture of any texture (DDS too)
  pbr.py make DIFFUSE OUT_DIR [--params JSON] [--model ONNX]    the maps, the baked textures and a preview (preview.png)

PARAMS (all optional): normal_strength 1, height_depth 1, rough_base 0.65, rough_detail 0.5, rough_invert false,
metal 0, metal_from_colour 0, ao_strength 0.5, spec_strength 1, parallax true, reflections true.
The normals are cached per colour texture, so changing the sliders re-bakes in about a second.
"""
import argparse
import hashlib
import json
import os
import sys

import numpy as np
from PIL import Image

DEFAULTS = {"normal_strength": 1.0, "height_depth": 1.0, "rough_base": 0.65, "rough_detail": 0.5, "rough_invert": False,
            "metal": 0.0, "metal_from_colour": 0.0, "ao_strength": 0.5, "spec_strength": 1.0, "parallax": True,
            "reflections": True}
TILE = 256
MAX_SIZE = 2048


def load(path):
    img = Image.open(path)
    has_alpha = "A" in img.getbands()
    img = img.convert("RGBA")
    if max(img.size) > MAX_SIZE:
        s = MAX_SIZE / max(img.size)
        img = img.resize((int(img.width * s), int(img.height * s)), Image.LANCZOS)
    a = np.asarray(img).astype(np.float32) / 255.0
    return a[..., :3], a[..., 3], has_alpha and a[..., 3].min() < 0.99


def blend_weights(n):
    """A tent across a tile, so overlapping tiles fade into each other."""
    r = 1.0 - np.abs(np.linspace(-1, 1, n))
    return np.maximum(np.outer(r, r), 1e-3).astype(np.float32)


def ai_normals(gray, model):
    """Tangent-space normals (H, W, 3 in -1..1) from DeepBump, in overlapping tiles that wrap round (textures tile)."""
    import onnxruntime as ort
    sess = ort.InferenceSession(model, providers=["CPUExecutionProvider"])
    h, w = gray.shape
    stride = TILE * 3 // 4
    ph, pw = max(h, TILE), max(w, TILE)
    # Wrap-padded so every pixel is covered by whole tiles, and tiled textures stay seamless.
    ny, nx = -(-ph // stride) + 1, -(-pw // stride) + 1
    big = np.pad(gray, ((TILE, ny * stride + TILE), (TILE, nx * stride + TILE)), mode="wrap")
    acc = np.zeros((3,) + big.shape, np.float32)
    wsum = np.zeros(big.shape, np.float32)
    wt = blend_weights(TILE)
    for ty in range(ny + 1):
        for tx in range(nx + 1):
            y0, x0 = TILE // 2 + ty * stride, TILE // 2 + tx * stride
            tile = big[y0:y0 + TILE, x0:x0 + TILE]
            if tile.shape != (TILE, TILE):
                continue
            pred = sess.run(None, {"input": tile[None, None].astype(np.float32)})[0][0]
            acc[:, y0:y0 + TILE, x0:x0 + TILE] += pred * wt
            wsum[y0:y0 + TILE, x0:x0 + TILE] += wt
    n = acc[:, TILE:TILE + h, TILE:TILE + w] / np.maximum(wsum[TILE:TILE + h, TILE:TILE + w], 1e-6)
    n = np.transpose(n, (1, 2, 0))
    if n.min() >= -0.01:                       # the model's output in 0..1: to -1..1
        n = n * 2 - 1
    return n / np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-6)


def height_from_normals(n):
    """The surface the normals describe (Frankot-Chellappa: their slopes integrated in the frequency domain), 0..1."""
    h, w, _ = n.shape
    nz = np.maximum(n[..., 2], 0.05)
    p, q = -n[..., 0] / nz, n[..., 1] / nz
    wx = np.fft.fftfreq(w) * 2 * np.pi
    wy = np.fft.fftfreq(h) * 2 * np.pi
    u, v = np.meshgrid(wx, wy)
    den = u ** 2 + v ** 2
    den[0, 0] = 1.0
    z = np.real(np.fft.ifft2((-1j * u * np.fft.fft2(p) - 1j * v * np.fft.fft2(q)) / den))
    z -= z.min()
    return z / max(z.max(), 1e-6)


def blur(a, r):
    """A box blur that wraps round (r in pixels), done with cumulative sums."""
    if r < 1:
        return a
    out = a
    for axis in (0, 1):
        pad = np.concatenate([np.take(out, range(-r, 0), axis=axis), out, np.take(out, range(0, r + 1), axis=axis)], axis=axis)
        c = np.cumsum(pad, axis=axis)
        n = out.shape[axis]
        out = (np.take(c, range(2 * r + 1, 2 * r + 1 + n), axis=axis) - np.take(c, range(0, n), axis=axis)) / (2 * r + 1)
    return out


def derive(colour, normals, p):
    """Height, roughness, metalness and occlusion from the colour and normals, by the parameters."""
    gray = colour @ np.array([0.299, 0.587, 0.114], np.float32)
    # The fine relief only: integrating normals drifts over large areas into blotches, which would read as warping.
    height = height_from_normals(normals)
    height = height - blur(height, max(8, min(height.shape) // 24))
    height = (height - np.percentile(height, 1)) / max(np.percentile(height, 99) - np.percentile(height, 1), 1e-6)
    height = np.clip(0.5 + (height - 0.5) * p["height_depth"], 0, 1)
    detail = np.abs(gray - blur(gray, 4))
    detail = detail / max(np.percentile(detail, 98), 1e-4)
    rough = p["rough_base"] + (detail - 0.5) * 0.4 * p["rough_detail"] * (-1 if p["rough_invert"] else 1)
    rough = np.clip(rough, 0.04, 1.0)
    sat = colour.max(2) - colour.min(2)
    metal = np.full(gray.shape, p["metal"], np.float32)
    if p["metal_from_colour"] > 0:
        # Bare metal reads as bright and grey: the brighter and greyer, the more metallic.
        metal = np.maximum(metal, np.clip((gray - 0.35) * 2.5, 0, 1) * np.clip(1 - sat * 4, 0, 1) * p["metal_from_colour"])
    cavity = np.clip((blur(height, 6) - height) * 6, 0, 1)
    ao = 1 - cavity * p["ao_strength"]
    return height, rough.astype(np.float32), metal.astype(np.float32), ao.astype(np.float32)


def save_dds(arr01, path, fmt):
    img = Image.fromarray((np.clip(arr01, 0, 1) * 255 + 0.5).astype(np.uint8))
    img.save(path, pixel_format=fmt)


def preview(colour, normals, rough, metal, ao, out, p, size=384):
    """The material on a ball and a flat swatch under a low sun, shaded GGX-like with a sky reflection."""
    h, w = rough.shape
    sun = np.array([-0.5, 0.6, 0.62]); sun /= np.linalg.norm(sun)
    view = np.array([0.0, 0.0, 1.0])
    half = sun + view; half /= np.linalg.norm(half)

    def shade(nrm, uvx, uvy, mask):
        ix = (uvx % 1.0 * (w - 1)).astype(int); iy = (uvy % 1.0 * (h - 1)).astype(int)
        alb = colour[iy, ix] * ao[iy, ix][..., None]
        r = rough[iy, ix]; m = metal[iy, ix]
        nd = np.clip(nrm @ sun, 0, 1)
        nh = np.clip(nrm @ half, 0, 1)
        a2 = (r * r) ** 2
        spec = a2 / (np.pi * ((nh * nh) * (a2 - 1) + 1) ** 2 + 1e-6)
        f0 = 0.04 * (1 - m)[..., None] + alb * m[..., None]
        sky = np.array([0.55, 0.65, 0.8]) * (0.4 + 0.6 * np.clip(nrm[..., 1:2], 0, 1))
        refl = sky * (f0 + (1 - f0) * (1 - np.clip(nrm[..., 2:3], 0, 1)) ** 5) * (1 - r)[..., None]
        c = alb * (1 - m)[..., None] * (0.18 + 1.1 * nd[..., None]) + f0 * spec[..., None] * nd[..., None] * 0.6 * p["spec_strength"] + refl
        c = c / (1 + c)                        # gentle tone mapping
        img = np.where(mask[..., None], c ** (1 / 1.6), 0.12)
        return img

    s = size
    yy, xx = np.mgrid[0:s, 0:s].astype(np.float32)
    x, y = (xx - s / 2) / (s * 0.45), -(yy - s / 2) / (s * 0.45)
    inside = x * x + y * y < 1
    z = np.sqrt(np.clip(1 - x * x - y * y, 0, 1))
    pos = np.stack([x, y, z], -1)
    u = (np.arctan2(x, z) / np.pi + 1) * 1.0
    v = np.arccos(np.clip(y, -1, 1)) / np.pi * 1.0
    t = np.stack([np.cos(np.arctan2(x, z)), np.zeros_like(x), -np.sin(np.arctan2(x, z))], -1)
    b = np.cross(pos, t)
    ix = (u % 1.0 * (w - 1)).astype(int); iy = (v % 1.0 * (h - 1)).astype(int)
    tn = normals[iy, ix]
    tn = tn * np.array([p["normal_strength"], p["normal_strength"], 1.0])
    nrm = tn[..., 0:1] * t - tn[..., 1:2] * b + tn[..., 2:3] * pos
    nrm /= np.maximum(np.linalg.norm(nrm, axis=-1, keepdims=True), 1e-6)
    ball = shade(nrm, u, v, inside)
    # The flat swatch, the sun grazing it so its relief shows.
    tn = normals[(yy / s * (h - 1)).astype(int), (xx / s * (w - 1)).astype(int)] * np.array([p["normal_strength"], p["normal_strength"], 1.0])
    flat = np.stack([tn[..., 0], -tn[..., 1], tn[..., 2]], -1)
    flat /= np.maximum(np.linalg.norm(flat, axis=-1, keepdims=True), 1e-6)
    swatch = shade(flat, xx / s, yy / s, np.ones_like(inside))
    img = np.concatenate([ball, swatch], 1)
    Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(out)


def make(diffuse, out, params, model):
    os.makedirs(out, exist_ok=True)
    p = dict(DEFAULTS); p.update(params)
    colour, alpha, keeps_alpha = load(diffuse)
    key = hashlib.sha1(open(diffuse, "rb").read()).hexdigest()[:16]
    cache = os.path.join(out, f"normals_{key}.npy")
    if os.path.exists(cache):
        normals = np.load(cache)
    else:
        gray = colour @ np.array([0.299, 0.587, 0.114], np.float32)
        normals = ai_normals(gray, model).astype(np.float32)
        np.save(cache, normals)
    height, rough, metal, ao = derive(colour, normals, p)
    n = normals * np.array([p["normal_strength"], p["normal_strength"], 1.0], np.float32)
    n /= np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-6)
    # The maps, for looking at.
    for name, a in (("normal", n * 0.5 + 0.5), ("height", height), ("roughness", rough), ("metalness", metal), ("occlusion", ao)):
        Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8)).save(os.path.join(out, f"map_{name}.png"))
    # Baked for Skyrim's own shaders.
    d = colour * (ao[..., None])
    save_dds(np.dstack([d, alpha]) if keeps_alpha else d, os.path.join(out, "baked_d.dds"), "DXT5" if keeps_alpha else "DXT1")
    spec = np.clip((1 - rough) ** 2 * p["spec_strength"], 0, 1)
    save_dds(np.dstack([n * 0.5 + 0.5, spec]), os.path.join(out, "baked_n.dds"), "DXT5")
    if p["parallax"]:
        save_dds(np.dstack([height] * 3), os.path.join(out, "baked_p.dds"), "DXT1")
    env = np.clip(metal * (1 - rough) * 1.2 + (1 - rough) * 0.15, 0, 1)
    # Reflections only where the surface is metal or truly smooth; stone and wood get parallax depth instead.
    if p["reflections"] and (metal.max() > 0.05 or float((1 - rough).mean()) > 0.55):
        save_dds(np.dstack([env] * 3), os.path.join(out, "baked_m.dds"), "DXT1")
    preview(colour, normals, rough, metal, ao, os.path.join(out, "preview.png"), p)
    info = {"params": p, "size": [int(colour.shape[1]), int(colour.shape[0])], "glossiness": float(np.clip((1 - rough.mean()) * 200, 5, 400)),
            "specular_strength": float(np.clip((1 - rough.mean()) * 2 * p["spec_strength"], 0.05, 3)),
            "parallax": os.path.exists(os.path.join(out, "baked_p.dds")) and p["parallax"],
            "reflections": os.path.exists(os.path.join(out, "baked_m.dds")) and p["reflections"]}
    json.dump(info, open(os.path.join(out, "material.json"), "w"), indent=1)
    print(json.dumps(info))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd"); ap.add_argument("inp"); ap.add_argument("out")
    ap.add_argument("--size", type=int, default=256); ap.add_argument("--params", default="{}")
    ap.add_argument("--model", default=os.path.join(os.environ.get("TAMRIELMERGE_TOOLS", "tools"), "pbr", "deepbump256.onnx"))
    a = ap.parse_args()
    if a.cmd == "thumb":
        img = Image.open(a.inp).convert("RGBA")
        img.thumbnail((a.size, a.size), Image.LANCZOS)
        bg = Image.new("RGBA", img.size, (90, 90, 96, 255)); bg.alpha_composite(img)
        bg.convert("RGB").save(a.out)
    elif a.cmd == "make":
        params = json.loads(open(a.params).read() if os.path.exists(a.params) else a.params)
        make(a.inp, a.out, params, a.model)
    else:
        sys.exit(f"unknown command {a.cmd}")


if __name__ == "__main__":
    main()
