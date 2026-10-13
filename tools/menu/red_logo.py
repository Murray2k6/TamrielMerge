"""Skyrim's 3D main-menu logo (the dragon emblem) in the red stone of the Tamriel logo (tools/menu/logo_source.png):
each vanilla logo texture keeps its carving and wear, recoloured by brightness rank onto the colours of the logo's own
emblem, dark crimson in the shadows to pale rose on the highlights.

  red_logo.py LOGO_SOURCE.png OUT_DIR TEXTURE.dds [TEXTURE.dds ...]
writes OUT_DIR/<name>.dds (uncompressed A8R8G8B8 with mipmaps, as Skyrim reads them) and a .png preview of each.
"""
import os
import struct
import sys

import numpy as np
from PIL import Image


def emblem_ramp(source):
    """256 colours of the logo's red emblem, from its darkest to its brightest pixels."""
    a = np.asarray(Image.open(source).convert("RGB")).astype(np.float32) / 255
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    S = a.shape[0]
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32) / S
    inside = (np.abs(xx - 0.5) / 0.48 + np.abs(yy - 0.5) / 0.47 <= 1.0) | ((np.abs(yy - 0.5) < 0.12) & (np.abs(xx - 0.5) < 0.47))
    red = (r - np.maximum(g, b) > 0.15) & (r > 0.25) & inside
    px = a[red]
    lum = px @ np.array([0.299, 0.587, 0.114], np.float32)
    order = np.argsort(lum)
    bins = np.array_split(px[order], 256)
    return np.stack([bn.mean(0) for bn in bins])


def write_dds(path, rgba):
    """Uncompressed 32-bit DDS (A8R8G8B8) with a full, box-filtered mip chain."""
    h, w = rgba.shape[:2]
    mips = [rgba.astype(np.float32)]
    while mips[-1].shape[0] > 1 or mips[-1].shape[1] > 1:
        m = mips[-1]
        if m.shape[0] % 2: m = np.concatenate([m, m[-1:]], 0)
        if m.shape[1] % 2: m = np.concatenate([m, m[:, -1:]], 1)
        mips.append((m[0::2, 0::2] + m[1::2, 0::2] + m[0::2, 1::2] + m[1::2, 1::2]) / 4)
    with open(path, "wb") as f:
        f.write(struct.pack("<4sIIIIIII", b"DDS ", 124, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000, h, w, w * 4, 0, len(mips)))
        f.write(b"\0" * 44)
        f.write(struct.pack("<IIIIIIII", 32, 0x1 | 0x40, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000))
        f.write(struct.pack("<IIIII", 0x1000 | 0x8 | 0x400000, 0, 0, 0, 0))
        for m in mips:
            q = np.clip(np.round(m), 0, 255).astype(np.uint8)
            f.write(q[..., [2, 1, 0, 3]].tobytes())   # BGRA in memory = A8R8G8B8


def recolour(texture, ramp):
    img = np.asarray(Image.open(texture).convert("RGBA")).astype(np.float32)
    lum = img[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
    used = lum > 6   # the atlas's empty space is black
    # Brightness rank among the carved pixels, so the whole ramp is used whatever the texture's own range.
    ranks = np.zeros_like(lum)
    if used.any():
        vals = lum[used]
        sorted_vals = np.sort(vals)
        ranks[used] = np.searchsorted(sorted_vals, vals) / max(len(sorted_vals) - 1, 1)
    idx = np.clip((ranks * 255).astype(int), 0, 255)
    rgb = ramp[idx] * 255
    rgb[~used] = img[~used][:, :3] * 0.3 + np.array([40, 0, 4]) * 0.7
    out = np.dstack([rgb, img[..., 3]])
    return out


def main(source, out_dir, *textures):
    ramp = emblem_ramp(source)
    os.makedirs(out_dir, exist_ok=True)
    for t in textures:
        name = os.path.splitext(os.path.basename(t))[0]
        out = recolour(t, ramp)
        write_dds(os.path.join(out_dir, name + ".dds"), out)
        Image.fromarray(np.clip(out, 0, 255).astype(np.uint8), "RGBA").save(os.path.join(out_dir, name + ".png"))
        print(f"{name}: recoloured")


if __name__ == "__main__":
    main(*sys.argv[1:])
