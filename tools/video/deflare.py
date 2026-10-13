"""Daggerfall's intro, cleaned before its lighting pass (tools/video/rt_look.py): the 1996 footage (256x200) films the
torch through a lens flare, and the 5.6x AI upscale sharpens the flame's sparkle into hard splatter and the flare's
ring into a crisp circle (user, 2026-10-08: "the blur and the light rings"; chose: soften the flame, fade the ring).

  - everywhere: the upscale tempered with a plain enlargement and a fine grain (AI_SHARE), as the AI alone smooths
    skin into wax and sharpens soft shadows into lines;
  - the flame: round the bright core the upscale is replaced by a smooth enlargement of the original frame, so it
    glows as filmed;
  - the ring: found round a large flame (Hough circles, scored by edge strength along the circumference), its place
    and size smoothed across frames within a shot so the fix doesn't shimmer, then painted out from its surroundings.

  python deflare.py --up UP_DIR --src SRC_DIR --out OUT_DIR [--only START:COUNT]
"""
import argparse
import glob
import os

import cv2
import numpy as np


class Ring:
    def __init__(self):
        self.c = None          # (x, y, r) smoothed
        self.missed = 0
        self.prev_small = None

    def find(self, gray, core_c, core_r):
        h, w = gray.shape
        small = cv2.GaussianBlur(cv2.resize(gray, (w // 2, h // 2)), (0, 0), 1.5)
        cut = self.prev_small is None or np.abs(small.astype(np.float32) - self.prev_small).mean() > 18
        self.prev_small = small.astype(np.float32)
        if cut:
            self.c, self.missed = None, 0
        edges = cv2.Canny(small, 30, 80)
        circles = cv2.HoughCircles(small, cv2.HOUGH_GRADIENT, dp=1, minDist=6, param1=50, param2=18,
                                   minRadius=max(90, int(core_r * 0.9)), maxRadius=440)
        best = None
        yy, xx = np.mgrid[0:h // 2, 0:w // 2]
        for x, y, r in (circles[0][:12] if circles is not None else []):
            if np.hypot(x * 2 - core_c[0], y * 2 - core_c[1]) > r * 2 * 0.5:
                continue
            band = np.abs(np.hypot(xx - x, yy - y) - r) < 1.5
            score = edges[band].mean() / 255.0
            if best is None or score > best[0]:
                best = (score, x * 2, y * 2, r * 2)
        if best and best[0] > 0.08:
            c = np.array(best[1:])
            self.c = c if self.c is None else 0.35 * self.c + 0.65 * c   # the flare grows fast as the torch flares
            self.missed = 0
        elif self.c is not None:
            self.missed += 1
            if self.missed > 6:
                self.c = None
        return self.c


AI_SHARE = 0.65    # the upscale's share against a plain enlargement (user, 2026-10-08: faces and hands looked waxy)


def gentle(up, src, seed):
    """The AI upscale tempered: blended with a plain enlargement of the original (skin and hands keep their texture,
    shadow edges their softness) and given a fine film grain, new each frame."""
    h, w = up.shape[:2]
    plain = cv2.GaussianBlur(cv2.resize(src, (w, h), interpolation=cv2.INTER_LANCZOS4).astype(np.float32), (0, 0), 1.2)
    o = up.astype(np.float32) * AI_SHARE + plain * (1 - AI_SHARE)
    # The footage came from video tape, which keeps colour at a fraction of the brightness's resolution: the upscale's
    # colour detail is invented (blotches on skin), so colour is softened back while brightness keeps its detail.
    ycc = cv2.cvtColor(np.clip(o, 0, 255).astype(np.uint8), cv2.COLOR_BGR2YCrCb).astype(np.float32)
    ycc[..., 1:] = cv2.GaussianBlur(ycc[..., 1:], (0, 0), 3.0)
    o = cv2.cvtColor(np.clip(ycc, 0, 255).astype(np.uint8), cv2.COLOR_YCrCb2BGR).astype(np.float32)
    g = cv2.GaussianBlur(np.random.default_rng(seed).normal(0, 1, (h, w)).astype(np.float32), (0, 0), 0.8) * 3.2
    o = o + g[..., None] * (0.4 + 0.6 * np.sqrt(np.clip(o.mean(2, keepdims=True) / 255, 0, 1)))
    return np.clip(o, 0, 255).astype(np.uint8)


def clean(up, src, ring, seed=0):
    h, w = up.shape[:2]
    gray = cv2.cvtColor(up, cv2.COLOR_BGR2GRAY)
    base = gentle(up, src, seed)
    core = gray > 235
    n = int(core.sum())
    if n < 400:                     # no big flame: only the tempering
        ring.find(gray, (w / 2, h / 2), 0)
        return base
    ys, xs = np.nonzero(core)
    cx, cy = xs.mean(), ys.mean()
    core_r = np.sqrt(n / np.pi)
    smooth = cv2.GaussianBlur(cv2.resize(src, (w, h), interpolation=cv2.INTER_CUBIC), (0, 0), 3)
    m = cv2.dilate(core.astype(np.uint8) * 255, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (61, 61)))
    m = cv2.GaussianBlur(m.astype(np.float32) / 255, (0, 0), 18)[..., None]
    out = (base * (1 - m) + smooth * m).astype(np.uint8)
    c = ring.find(gray, (cx, cy), core_r)
    if c is not None and c[2] > core_r * 1.3:
        x, y, r = c
        yy, xx = np.mgrid[0:h, 0:w]
        d = np.abs(np.hypot(xx - x, yy - y) - r)
        mask = (d < 14).astype(np.uint8) * 255
        painted = cv2.inpaint(out, mask, 12, cv2.INPAINT_TELEA)
        feather = np.clip(1 - (d - 8) / 10, 0, 1)[..., None]
        out = (out * (1 - feather) + painted * feather).astype(np.uint8)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--up", required=True); ap.add_argument("--src", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--only")
    a = ap.parse_args()
    files = sorted(glob.glob(os.path.join(a.up, "frame*.png")))
    if a.only:
        s, n = map(int, a.only.split(":")); files = files[s:s + n]
    os.makedirs(a.out, exist_ok=True)
    ring = Ring()
    prev_key, prev_out = None, None
    for i, f in enumerate(files):
        name = os.path.basename(f)
        dst = os.path.join(a.out, name)
        key = open(f, "rb").read()
        if key == prev_key and prev_out is not None:
            cv2.imwrite(dst, prev_out); continue
        up = cv2.imread(f); src = cv2.imread(os.path.join(a.src, name))
        out = clean(up, src, ring, i) if src is not None else up
        cv2.imwrite(dst, out, [cv2.IMWRITE_PNG_COMPRESSION, 1])
        prev_key, prev_out = key, out
        if (i + 1) % 200 == 0:
            print(f"  {i + 1}/{len(files)}", flush=True)
    print(f"cleaned {len(files)} frames -> {a.out}")


if __name__ == "__main__":
    main()
