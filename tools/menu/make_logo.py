"""The Tamriel logo with its four corners showing the converted games' towns (rendered by tools/scene3d from the
conversions themselves): top left Daggerfall (Daggerfall), top right Vivec (Morrowind), bottom left the Imperial City
(Oblivion), bottom right Stros M'Kai (Redguard). The red emblem and lettering stay exactly as drawn; each town is turned
into the logo's light grey, sparkling style and faded into its corner.

  make_logo.py SOURCE.png CORNERS_DIR OUT_DIR
writes OUT_DIR/logo.png (the whole picture) and OUT_DIR/emblem.png (the emblem alone on transparency, for the menu).
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

# Each town: its render, the landmark's place in the render (0-1) and where it goes in the logo (0-1).
CORNERS = [("daggerfall", (0.66, 0.52), (0.17, 0.24)), ("vivec", (0.72, 0.42), (0.83, 0.25)),
           ("imperialcity", (0.48, 0.62), (0.18, 0.76)), ("strosmkai", (0.68, 0.72), (0.82, 0.78))]


def emblem_mask(img):
    """The red emblem and lettering: strongly red pixels, grown a little to take their dark edges, feathered."""
    a = img.astype(np.float32) / 255
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    red = (r - np.maximum(g, b) > 0.12) & (r > 0.22)
    # Only the emblem's diamond and its bar (the old corner art had red too: the volcano's lava).
    S = a.shape[0]
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32) / S
    diamond = np.abs(xx - 0.5) / 0.48 + np.abs(yy - 0.5) / 0.47 <= 1.02
    bar = (np.abs(yy - 0.5) < 0.12) & (np.abs(xx - 0.5) < 0.47)
    red &= diamond | bar
    m = Image.fromarray((red * 255).astype(np.uint8))
    m = m.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.GaussianBlur(1.5))
    return np.asarray(m).astype(np.float32) / 255


def sparkle_layer(img):
    """The sparkles: small points much brighter than their surroundings."""
    lum = np.asarray(Image.fromarray(img).convert("L")).astype(np.float32) / 255
    blur = np.asarray(Image.fromarray((lum * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(6))).astype(np.float32) / 255
    s = np.clip((lum - blur - 0.06) * 4, 0, 1) * (lum > 0.8)
    return s


def stars(S, count, seed=7):
    """Four-pointed sparkles like the logo's own: a bright core and thin cross flares, of many sizes."""
    rng = np.random.default_rng(seed)
    layer = np.zeros((S, S), np.float32)
    for _ in range(count):
        x, y = rng.uniform(0, S, 2)
        size = rng.choice([1.2, 1.6, 2.2, 3.0, 4.5, 7.0], p=[0.35, 0.25, 0.18, 0.12, 0.07, 0.03])
        r = int(size * 7) + 2
        x0, y0, x1, y1 = max(int(x) - r, 0), max(int(y) - r, 0), min(int(x) + r + 1, S), min(int(y) + r + 1, S)
        yy, xx = np.mgrid[y0:y1, x0:x1].astype(np.float32)
        dx, dy = np.abs(xx - x), np.abs(yy - y)
        core = np.exp(-(dx * dx + dy * dy) / (2 * (size * 0.6) ** 2))
        flare = np.exp(-dy / 0.7) * np.exp(-dx / (size * 2.2)) + np.exp(-dx / 0.7) * np.exp(-dy / (size * 2.2))
        layer[y0:y1, x0:x1] = np.maximum(layer[y0:y1, x0:x1], np.clip(core + 0.6 * flare, 0, 1) * rng.uniform(0.6, 1.0))
    return layer


def styled(render, size):
    """A rendered town in the logo's style: grey, light, the sky near white, with crisp detail."""
    img = render.convert("L").resize((size, size), Image.LANCZOS)
    a = np.asarray(img).astype(np.float32) / 255
    a = np.clip((a - 0.12) / 0.80, 0, 1) ** 0.85          # lift the darks, push the sky to white
    sharp = np.asarray(Image.fromarray((a * 255).astype(np.uint8)).filter(ImageFilter.UnsharpMask(3, 160, 2))).astype(np.float32) / 255
    a = 0.30 + 0.66 * sharp                                 # light, like a pencil drawing, with the logo's greys
    return a


def main(source, corners_dir, out_dir):
    src = np.asarray(Image.open(source).convert("RGB"))
    S = src.shape[0]
    out = src.astype(np.float32) / 255
    emblem = emblem_mask(src)
    sparkles = sparkle_layer(src)
    yy, xx = np.mgrid[0:S, 0:S].astype(np.float32)
    size = int(S * 0.72)
    for name, (sx, sy), (tx, ty) in CORNERS:
        cx, cy = int(tx > 0.5), int(ty > 0.5)
        render = Image.open(os.path.join(corners_dir, f"corner-{name}.png"))
        art = styled(render, size)
        # Placed with the town toward its corner; faded out toward the centre.
        ox, oy = int(tx * S - sx * size), int(ty * S - sy * size)
        layer = np.ones((S, S), np.float32)
        x0, y0, x1, y1 = max(ox, 0), max(oy, 0), min(ox + size, S), min(oy + size, S)
        layer[y0:y1, x0:x1] = art[y0 - oy:y1 - oy, x0 - ox:x1 - ox]
        corner = np.array([0 if cx == 0 else S, 0 if cy == 0 else S], np.float32)
        d = np.hypot(xx - corner[0], yy - corner[1]) / S
        fade = 1 - np.clip((d - 0.34) / 0.30, 0, 1)
        fade = fade * fade * (3 - 2 * fade)
        inside = np.zeros((S, S), np.float32); inside[y0:y1, x0:x1] = 1
        inside = np.asarray(Image.fromarray((inside * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(60))).astype(np.float32) / 255
        w = np.clip(fade * inside * 1.15, 0, 1)[..., None]
        out = out * (1 - w) + layer[..., None] * w
    # Sparkles back over the towns (the logo's own, and as many again where the towns hid them), then the emblem
    # exactly as drawn.
    out = np.maximum(out, sparkles[..., None])
    out = out + (1 - out) * stars(S, 700)[..., None]
    e = emblem[..., None]
    out = out * (1 - e) + (src.astype(np.float32) / 255) * e
    os.makedirs(out_dir, exist_ok=True)
    Image.fromarray((np.clip(out, 0, 1) * 255).astype(np.uint8)).save(os.path.join(out_dir, "logo.png"))
    rgba = np.dstack([src, (emblem * 255).astype(np.uint8)])
    Image.fromarray(rgba, "RGBA").save(os.path.join(out_dir, "emblem.png"))
    print(f"wrote {out_dir}/logo.png and emblem.png")


if __name__ == "__main__":
    main(*sys.argv[1:4])
