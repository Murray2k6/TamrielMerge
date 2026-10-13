"""A ray-traced look for the games' intro cinematics (user, 2026-10-08: "rt looks" for the videos, Daggerfall's lit by
its torch). The intros are footage, not scenes, so nothing can be truly traced: each frame's depth is estimated
(Depth Anything V2, smoothed over time so it doesn't shimmer) and lighting built from it the way a ray tracer would
see the room: a light in the scene shining on surfaces by their facing (normals from the depth), shadows marched
through the depth toward the light, ambient occlusion in creases, light shafts from the source through the air, a
glow round it, and depth haze.

The light: --light torch finds the brightest warm flame in the frame (Daggerfall's guard's torch) and lights from it,
flickering with it; when the flame is out of frame (close-ups) it shines from where the flame was, off screen.
--light auto lights from the frame's brightest region, in its colour.

  python rt_look.py --frames DIR --audio WAV --out OUT.bik --work DIR [--light torch|auto] [--test START:COUNT]

--test renders COUNT frames from START into WORK/test as before|after pictures and an mp4 to judge, and stops.
Frames that repeat (Daggerfall's video holds a picture for several frames) are done once. The GPU waits below 78 C.
"""
import argparse
import glob
import os
import subprocess
import sys
import time
import wave

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import remaster  # noqa: E402  (ffmpeg, RAD encode)

DEV = "cuda" if torch.cuda.is_available() else "cpu"
HALF = torch.float16 if DEV == "cuda" else torch.float32
MODEL = "depth-anything/Depth-Anything-V2-Base-hf"


def gpu_temperature():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=10).stdout.strip().splitlines()
        return int(out[0]) if out else None
    except Exception:
        return None


def cool_down(limit=78):
    t = gpu_temperature()
    while t is not None and t > limit:
        print(f"  GPU at {t} C, waiting to cool below {limit} C", flush=True)
        time.sleep(20)
        t = gpu_temperature()


class Depth:
    """Relative depth per frame: Depth Anything V2's disparity (near = large), normalised, smoothed over time and reset at
    cuts."""

    def __init__(self):
        from transformers import AutoImageProcessor, AutoModelForDepthEstimation
        self.proc = AutoImageProcessor.from_pretrained(MODEL)
        self.model = AutoModelForDepthEstimation.from_pretrained(MODEL).to(DEV).to(HALF).eval()
        self.prev = None
        self.prev_small = None

    @torch.no_grad()
    def __call__(self, img_t):
        """img_t: 1x3xHxW in 0..1 on DEV. Returns 1x1xHxW disparity in 0..1 (1 = nearest)."""
        h, w = img_t.shape[-2:]
        s = 518 / min(h, w)
        sh, sw = int(round(h * s / 14)) * 14, int(round(w * s / 14)) * 14
        x = F.interpolate(img_t, size=(sh, sw), mode="bicubic", align_corners=False)
        mean = torch.tensor([0.485, 0.456, 0.406], device=DEV).view(1, 3, 1, 1)
        std = torch.tensor([0.229, 0.224, 0.225], device=DEV).view(1, 3, 1, 1)
        d = self.model(pixel_values=((x - mean) / std).to(HALF)).predicted_depth.float().unsqueeze(1)
        d = F.interpolate(d, size=(h, w), mode="bilinear", align_corners=False)
        lo, hi = torch.quantile(d.flatten()[::97], 0.02), torch.quantile(d.flatten()[::97], 0.98)
        d = ((d - lo) / (hi - lo + 1e-6)).clamp(0, 1)
        small = F.interpolate(img_t, size=(90, 120), mode="area")
        cut = self.prev_small is None or (small - self.prev_small).abs().mean() > 0.08
        self.cut = cut
        self.prev_small = small
        if not cut and self.prev is not None:
            d = 0.6 * self.prev + 0.4 * d
        self.prev = d
        return d


def blur(x, sigma):
    k = int(sigma * 3) * 2 + 1
    g = torch.exp(-((torch.arange(k, device=x.device) - k // 2) ** 2) / (2 * sigma * sigma)); g = g / g.sum()
    c = x.shape[1]
    x = F.conv2d(F.pad(x, (k // 2, k // 2, 0, 0), mode="replicate"), g.view(1, 1, 1, k).repeat(c, 1, 1, 1), groups=c)
    return F.conv2d(F.pad(x, (0, 0, k // 2, k // 2), mode="replicate"), g.view(1, 1, k, 1).repeat(c, 1, 1, 1), groups=c)


def lum(x):
    return 0.2126 * x[:, 0:1] + 0.7152 * x[:, 1:2] + 0.0722 * x[:, 2:3]


class Light:
    """Where the light is (screen x, y in 0..1, its disparity), its colour and strength, frame by frame."""

    def __init__(self, mode):
        self.mode = mode
        self.pos = None          # (x, y)
        self.disp = 0.6
        self.colour = torch.tensor([1.0, 0.62, 0.32]) if mode == "torch" else torch.tensor([1.0, 0.95, 0.88])
        self.flicker = 1.0
        self.seen = False
        self.compact = True
        self.direction = None    # the footage's own key light, read from its shading (unit vector), per shot

    def update(self, img, disp):
        h, w = img.shape[-2:]
        y = lum(img)[0, 0]
        if self.mode == "torch":
            r, g, b = img[0, 0], img[0, 1], img[0, 2]
            flame = (y > 0.8) & (r >= g) & (g >= b * 0.9)
        else:
            flame = y > torch.quantile(y.flatten()[::31], 0.995).clamp(min=0.55)
        n = int(flame.sum())
        # A compact source (a flame, the sun's disc) casts shafts; a wide bright sky does not.
        self.compact = n < h * w * 0.015
        if n > max(30, h * w * 0.00015) and (self.mode == "torch" or self.compact):
            ys, xs = torch.nonzero(flame, as_tuple=True)
            # The flame's lower part: where it burns, not the tip of the flame.
            cut = torch.quantile(ys.float(), 0.6)
            sel = ys.float() >= cut
            cx, cy = xs[sel].float().mean().item() / w, ys[sel].float().mean().item() / h
            self.pos = (cx, cy) if self.pos is None or not self.seen else (0.5 * self.pos[0] + 0.5 * cx, 0.5 * self.pos[1] + 0.5 * cy)
            ix, iy = int(cx * (w - 1)), int(cy * (h - 1))
            near = disp[0, 0, max(0, iy - 8):iy + 8, max(0, ix - 8):ix + 8]
            self.disp = float(near.median()) if near.numel() else self.disp
            strength = min(n / (h * w * 0.004), 1.0)
            self.flicker = 0.75 + 0.25 * strength
            if self.mode == "auto":
                c = img[0][:, flame].mean(1).cpu()
                self.colour = 0.8 * self.colour + 0.2 * (c / c.max().clamp(min=1e-3))
            self.seen = True
        else:
            # No source in frame (Daggerfall's close-ups): the light is the footage's own, its direction read from the
            # shading (relight), so the new light deepens what's there instead of lighting the shadowed side.
            self.seen = False
            self.flicker = 0.9
        return self

    def read_direction(self, img, nrm, cut):
        """The footage's key light: brightness against surface facing, fitted (brightness ~ a + b . normal), b's
        direction; smoothed within a shot, started afresh at a cut."""
        y = lum(img)[0, 0]
        m = (y > 0.03) & (y < 0.92)
        if int(m.sum()) < 500:
            return self.direction
        idx = torch.nonzero(m.flatten()).flatten()[::13]
        n = nrm.reshape(3, -1)[:, idx].T
        X = torch.cat([torch.ones(len(idx), 1, device=n.device), n], 1)
        b = torch.linalg.lstsq(X, y.flatten()[idx].unsqueeze(1)).solution[1:, 0]
        if float(b.norm()) < 1e-3:
            return self.direction
        d = (b / b.norm()).cpu()
        self.direction = d if cut or self.direction is None else (0.85 * self.direction + 0.15 * d) / (0.85 * self.direction + 0.15 * d).norm()
        return self.direction


SHADOWS = False   # --shadows: depth-marched shadows; off, as footage keeps its own and the estimated depth's jumps
                  # cut hard seams across faces (user, 2026-10-08: "light rings", blur)


def relight(img, disp, light, frame_no, mode, cut=False):
    """The look, for one frame (1x3xHxW, 0..1)."""
    h, w = img.shape[-2:]
    # Pseudo 3D: depth from disparity, screen coordinates spread by it.
    z = 1.0 / (0.15 + disp * 1.6)
    yy, xx = torch.meshgrid(torch.linspace(0, 1, h, device=DEV), torch.linspace(0, 1, w, device=DEV), indexing="ij")
    aspect = w / h
    P = torch.stack([(xx - 0.5) * aspect * z[0, 0], (yy - 0.5) * z[0, 0], z[0, 0]], 0)          # 3xHxW
    # Normals from the depth's slopes (smoothed so skin and cloth stay soft).
    zs = blur(z, 6)[0, 0]
    Ps = torch.stack([(xx - 0.5) * aspect * zs, (yy - 0.5) * zs, zs], 0)
    dx = torch.zeros_like(Ps); dy = torch.zeros_like(Ps)
    dx[:, :, 1:-1] = Ps[:, :, 2:] - Ps[:, :, :-2]; dy[:, 1:-1] = Ps[:, 2:] - Ps[:, :-2]
    nrm = torch.cross(dx, dy, dim=0)
    nrm = nrm / nrm.norm(dim=0, keepdim=True).clamp(min=1e-6)
    nrm = torch.where(nrm[2:3] > 0, -nrm, nrm)       # facing the camera
    # The light in that space: the seen source as a point, else the footage's own key light as a direction.
    if light.seen:
        lx, ly = light.pos
        lz = 1.0 / (0.15 + light.disp * 1.6) * 0.92
        Lp = torch.tensor([(lx - 0.5) * aspect * lz, (ly - 0.5) * lz, lz], device=DEV).view(3, 1, 1)
        L = Lp - P
        dist = L.norm(dim=0, keepdim=True).clamp(min=1e-4)
        L = L / dist
        fall = 1.0 / (1.0 + (dist / 0.9) ** 2)
    else:
        d = light.read_direction(img, nrm, cut)
        if d is None:
            return img
        L = d.to(DEV).view(3, 1, 1).expand_as(P)
        Lp = P + L * 0.8
        fall = torch.ones_like(P[:1])
    # Light varies smoothly over a surface: the facing term is softened so the estimated depth's jumps (a flame's edge,
    # a forehead) don't step it into rings and seams.
    ndl = blur((nrm * L).sum(0, keepdim=True).clamp(min=0).unsqueeze(0), 12)[0]
    # Shadows: march from each pixel toward the light through the depth (half resolution).
    hs, ws = h // 2, w // 2
    zh = F.interpolate(z, size=(hs, ws), mode="bilinear", align_corners=False)
    Ph = F.interpolate(P.unsqueeze(0), size=(hs, ws), mode="bilinear", align_corners=False)[0]
    shadow = torch.ones(1, 1, hs, ws, device=DEV)
    steps = 20 if SHADOWS else 0
    for k in range(1, steps + 1):
        t = k / (steps + 1)
        Lh = Lp if Lp.dim() == 3 and Lp.shape[1] == 1 else F.interpolate(Lp.unsqueeze(0), size=(hs, ws), mode="bilinear", align_corners=False)[0]
        Q = Ph + (Lh - Ph) * t                                    # point along the ray
        qx = Q[0] / (aspect * Q[2]) + 0.5; qy = Q[1] / Q[2] + 0.5
        grid = torch.stack([qx * 2 - 1, qy * 2 - 1], -1).unsqueeze(0)
        scene_z = F.grid_sample(zh, grid, mode="bilinear", padding_mode="border", align_corners=True)
        inside = ((qx > 0) & (qx < 1) & (qy > 0) & (qy < 1)).float().unsqueeze(0).unsqueeze(0)
        blocked = ((Q[2].unsqueeze(0).unsqueeze(0) - scene_z) > 0.04).float() * ((Q[2].unsqueeze(0).unsqueeze(0) - scene_z) < 0.6).float()
        shadow = shadow * (1 - (0.22 if mode == "torch" else 0.1) * blocked * inside)
    shadow = F.interpolate(blur(shadow, 2), size=(h, w), mode="bilinear", align_corners=False)
    # Ambient occlusion: where the surroundings stand nearer than the pixel.
    ao = torch.ones_like(disp)
    for sigma, k in ((6, 1.2), (24, 0.8)):
        ao = ao * (1 - k * (blur(disp, sigma) - disp).clamp(min=0)).clamp(0.6, 1)
    bright = blur(((lum(img) - 0.55) / 0.25).clamp(0, 1), 6)
    ao = ao + (1 - ao) * bright
    # Lighting: the footage already holds its light, so the new light is added on top of it, shaped by facing and
    # shadow, and the shadows and creases deepen what's there.
    col = light.colour.to(DEV).view(1, 3, 1, 1)
    flick = light.flicker * (1.0 + 0.06 * np.sin(frame_no * 0.9) + 0.04 * np.sin(frame_no * 2.3 + 1.0)) if mode == "torch" else 1.0
    key = (ndl * fall * shadow * flick).clamp(0, 1.5)
    # The seen torch lights strongly; the footage's own light is strengthened gently (its side lifted, the far side
    # and the shadows deepened).
    strength = (1.1 if light.seen else 0.45) if mode == "torch" else 0.3
    base = img * (0.55 + 0.45 * ao) * (0.65 + 0.35 * shadow) * (0.78 + 0.3 * ndl.clamp(0, 1))
    # The surfaces' own colour, lifted out of the dark (footage this dark holds little of it), takes the new light.
    albedo = img.clamp(min=1e-4) ** 0.6 * 0.6
    out = base + albedo * col * key * strength * ao
    if mode == "torch":
        # Faded shadows (user, 2026-10-08: filmed under studio lamps, the faces' torch side is dark): each shadow is
        # lifted toward the brightness of the surface round it, so a shadow on a lit face lightens and softens while the
        # dark room (dark all round) stays dark; most near the seen torch.
        reach = fall.unsqueeze(0) * 0.6 + 0.15 if light.seen else torch.full_like(disp, 0.25)
        # (the flame itself left out: its glow isn't a surface, and lifting toward it smeared light into the air)
        flame = blur(((lum(img) - 0.65) / 0.15).clamp(0, 1), 10).clamp(0, 1)
        surf = out * (1 - flame)
        # Only broad shadows (a face's far side) are lifted, as a smooth field laid over the surface, so eyes, creases
        # and features keep their own shading (user, 2026-10-08: the guard's face went flat).
        lift = (blur(surf, 40) - blur(out, 12)).clamp(min=0)
        out = out + lift * reach.clamp(0, 0.6) * 0.5
    # A soft glow round the frame's few brightest things (the flame), nothing over the rest of the picture.
    thr = max(0.75, float(torch.quantile(lum(img).flatten()[::97], 0.997)))
    src = (lum(img) - thr).clamp(min=0) / max(1 - thr, 0.05) * img
    out = out + blur(src, 8) * 0.25 + blur(src, 24) * 0.15
    # Haze with distance, for dark and dim scenes; bright outdoor footage already has its own air.
    haze = (1 - disp) ** 2 * (0.05 if mode == "torch" else (0.08 if float(lum(img).mean()) < 0.35 else 0.0))
    out = out * (1 - haze) + haze * (col * 0.12 if mode == "torch" else lum(img).mean() * 1.2)
    # Skin and everything keep their filmed colour (user, 2026-10-08: "the tone of the skin is off"): the new light
    # changes brightness only, the original hue and saturation scaled to it, with a slight warmth from the light.
    # (measured over a small area and capped, so near-black eye sockets don't blow their colour noise up)
    ratio = ((blur(lum(out), 3) + 0.02) / (blur(lum(img), 3) + 0.02)).clamp(0.5, 2.0)
    out = (img * ratio).clamp(0, 1) * 0.9 + out * 0.1
    # Brightening reads as more saturated: the colourfulness is matched to the filmed frame's.
    def chroma(x):
        return (x.max(1, keepdim=True).values - x.min(1, keepdim=True).values)
    sel = (lum(img) > 0.08) & (lum(img) < 0.8)
    if int(sel.sum()) > 1000:
        k = float((chroma(img)[sel].mean() / chroma(out)[sel].mean().clamp(min=1e-4)).clamp(0.7, 1.0)) ** 0.5
        l = lum(out)
        out = (l + (out - l) * k).clamp(0, 1)
    keep = ((lum(img) - 0.8) / 0.12).clamp(0, 1)
    out = out * (1 - keep) + img * keep
    if mode != "torch":
        # Outdoors and in abstract footage the estimated depth is only trustworthy near: far off (haze, mountains,
        # sky) the footage is left as it was.
        near = ((disp - 0.15) / 0.35).clamp(0, 1)
        near = near * near * (3 - 2 * near)
        out = img + (out - img) * near
    return out.clamp(0, 1)


def frames_of(d):
    return sorted(glob.glob(os.path.join(d, "frame*.png")))


def wav_seconds(path):
    with wave.open(path) as w:
        return w.getnframes() / w.getframerate()


def process(files, out_dir, mode, compare_dir=None):
    os.makedirs(out_dir, exist_ok=True)
    depth = Depth()
    light = Light(mode)
    prev_bytes, prev_out = None, None
    start = time.time()
    for i, f in enumerate(files):
        dst = os.path.join(out_dir, os.path.basename(f))
        data = open(f, "rb").read()
        if os.path.exists(dst) and compare_dir is None:
            prev_bytes, prev_out = data, dst
            continue
        if data == prev_bytes and prev_out:
            with open(prev_out, "rb") as a, open(dst, "wb") as b:
                b.write(a.read())
            continue
        if i % 40 == 0:
            cool_down()
        import io
        rgb = np.asarray(Image.open(io.BytesIO(data)).convert("RGB"), dtype=np.float32) / 255.0
        with torch.no_grad():
            t = torch.from_numpy(rgb).permute(2, 0, 1).unsqueeze(0).to(DEV)
            d = depth(t)
            light.update(t, d)
            o = relight(t, d, light, int(os.path.basename(f)[5:10]), mode, depth.cut)
            arr = (o[0].permute(1, 2, 0).cpu().numpy() * 255 + 0.5).astype(np.uint8)
        Image.fromarray(arr).save(dst, compress_level=1)
        if compare_dir:
            os.makedirs(compare_dir, exist_ok=True)
            both = np.concatenate([(rgb * 255).astype(np.uint8), arr], 1)
            dep = (d[0, 0].cpu().numpy() * 255).astype(np.uint8)
            Image.fromarray(both).save(os.path.join(compare_dir, os.path.basename(f)), compress_level=1)
            if i == 0:
                Image.fromarray(dep).save(os.path.join(compare_dir, "depth_" + os.path.basename(f)))
        prev_bytes, prev_out = data, dst
        if (i + 1) % 100 == 0:
            rate = (i + 1) / (time.time() - start)
            print(f"  {i + 1}/{len(files)} ({rate:.1f}/s, about {(len(files) - i - 1) / rate / 60:.0f} min left)", flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--frames", required=True); ap.add_argument("--audio", required=True)
    ap.add_argument("--out", required=True); ap.add_argument("--work", required=True)
    ap.add_argument("--light", default="auto", choices=["torch", "auto"])
    ap.add_argument("--test")
    ap.add_argument("--shadows", action="store_true")
    a = ap.parse_args()
    global SHADOWS
    SHADOWS = a.shadows
    files = frames_of(a.frames)
    fps = len(files) / wav_seconds(a.audio)
    if a.test:
        s, n = map(int, a.test.split(":"))
        part = files[s:s + n]
        out = os.path.join(a.work, "test", "frames"); cmp_dir = os.path.join(a.work, "test", "compare")
        process(part, out, a.light, cmp_dir)
        # A short side-by-side clip with its sound, to watch.
        clip = os.path.join(a.work, "test", "before_after.mp4")
        subprocess.run([remaster.FFMPEG, "-v", "error", "-y", "-framerate", f"{fps:.4f}", "-start_number", str(s + 1),
                        "-i", os.path.join(cmp_dir, "frame%05d.png"), "-ss", f"{s / fps:.3f}", "-t", f"{n / fps:.3f}",
                        "-i", a.audio, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18", "-c:a", "aac", "-shortest", clip], check=True)
        print(f"test: {clip} and {cmp_dir}", flush=True)
        return
    out = os.path.join(a.work, "frames")
    process(files, out, a.light)
    src_dir = os.path.dirname(os.path.abspath(a.audio))
    remaster.encode(out, src_dir, fps, os.path.abspath(a.out))


if __name__ == "__main__":
    main()
