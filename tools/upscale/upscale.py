"""TamrielMerge texture upscaling: the games' textures at four times their size with Real-ESRGAN.

Reads the raw textures written by `TamrielMerge upscale-textures` (or mw-/df-upscale-export) (<name>.rgba: int32 width,
int32 height, then
width*height pixels as little-endian 0xAARRGGBB) from --input, and writes the upscaled ones under the same names to
--output, scaled back down so neither side exceeds --max (default 1024). Colour goes through the model; alpha
(cut-outs, blends) is resized smoothly. Textures already done are skipped, so it can be stopped and resumed.

Usage: python upscale.py --input in --output out [--model RealESRGAN_x4plus.pth] [--max 1024] [--device cuda|cpu]
       [--threads N]
--device defaults to the graphics card when there is one; the converter passes cpu unless it was given --gpu (graphics
cards run hot under sustained upscaling).
"""
import argparse, os, struct, sys, time

import numpy as np
import torch
import torch.nn.functional as F


def read_rgba(path):
    with open(path, "rb") as f:
        w, h = struct.unpack("<ii", f.read(8))
        px = np.frombuffer(f.read(w * h * 4), dtype="<u4").reshape(h, w)
    a = (px >> 24) & 255
    r = (px >> 16) & 255
    g = (px >> 8) & 255
    b = px & 255
    return np.stack([r, g, b], axis=-1).astype(np.float32) / 255.0, a.astype(np.float32) / 255.0


def write_rgba(path, rgb, a):
    h, w = a.shape
    rgb8 = np.clip(rgb * 255.0 + 0.5, 0, 255).astype(np.uint32)
    a8 = np.clip(a * 255.0 + 0.5, 0, 255).astype(np.uint32)
    px = (a8 << 24) | (rgb8[..., 0] << 16) | (rgb8[..., 1] << 8) | rgb8[..., 2]
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(struct.pack("<ii", w, h))
        f.write(px.astype("<u4").tobytes())
    os.replace(tmp, path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True)
    ap.add_argument("--output", required=True)
    ap.add_argument("--model", default=os.path.join(os.path.dirname(__file__), "RealESRGAN_x4plus.pth"))
    ap.add_argument("--max", type=int, default=1024)
    ap.add_argument("--device", choices=["cuda", "cpu"], default=None)
    ap.add_argument("--threads", type=int, default=0)
    args = ap.parse_args()

    from spandrel import ModelLoader
    device = args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    if device == "cuda" and not torch.cuda.is_available():
        device = "cpu"
    if args.threads > 0:
        torch.set_num_threads(args.threads)
    # Half precision on the graphics card; the processor works in full (its half-precision convolutions are slow).
    dtype = torch.float16 if device == "cuda" else torch.float32
    model = ModelLoader().load_from_file(args.model).model.to(device).eval().to(dtype)
    print(f"device {device}, model {os.path.basename(args.model)}", flush=True)

    names = sorted(n for n in os.listdir(args.input) if n.endswith(".rgba"))
    todo = [n for n in names if not os.path.exists(os.path.join(args.output, n))]
    print(f"{len(names)} textures, {len(todo)} to upscale", flush=True)
    start = time.time()
    for i, name in enumerate(todo, 1):
        out_path = os.path.join(args.output, name)
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        rgb, a = read_rgba(os.path.join(args.input, name))
        h, w = a.shape
        scale = min(4.0, args.max / max(w, h))
        if scale <= 1.0:
            write_rgba(out_path, rgb, a)   # already large: kept as is
            continue
        tw, th = int(round(w * scale)), int(round(h * scale))
        try:
            with torch.no_grad():
                # A cut-out (a sprite: grass) has see-through pixels: its outline goes through the model too, so its
                # edges come out smooth rather than in the pixel art's steps, and it is padded with its own edge (it
                # doesn't tile, so wrapping would bring the bottom in at the top).
                cutout = bool((a < 0.99).any())
                mode = "replicate" if cutout else "circular"

                def through_model(t):
                    if min(w, h) < 16:
                        return t   # a few pixels (colour swatches, bars): nothing for the model to sharpen
                    # Padded (wrapped for tiling textures, so they stay seamless), upscaled, the padding cropped off.
                    pad = 8
                    t = F.pad(t.to(dtype), (pad, pad, pad, pad), mode=mode)
                    up = model(t).float().clamp(0, 1)
                    return up[:, :, pad * 4:-pad * 4, pad * 4:-pad * 4]

                def sized(t):
                    if (th, tw) != tuple(t.shape[2:]):
                        t = F.interpolate(t, size=(th, tw), mode="bicubic", antialias=True, align_corners=False).clamp(0, 1)
                    return t

                up = sized(through_model(torch.from_numpy(rgb).permute(2, 0, 1).unsqueeze(0).to(device).float()))
                alpha = torch.from_numpy(a).unsqueeze(0).unsqueeze(0).to(device)
                if cutout:
                    alpha = sized(through_model(alpha.repeat(1, 3, 1, 1)).mean(1, keepdim=True))
                else:
                    alpha = F.interpolate(alpha, size=(th, tw), mode="bicubic", align_corners=False).clamp(0, 1)
            write_rgba(out_path, up[0].permute(1, 2, 0).cpu().numpy(), alpha[0, 0].cpu().numpy())
        except Exception as e:
            print(f"  {name}: {e}", flush=True)
            continue
        if i % 100 == 0:
            rate = i / (time.time() - start)
            print(f"  {i}/{len(todo)} ({rate:.1f}/s)", flush=True)
    print(f"done in {(time.time() - start) / 60:.1f} min", flush=True)


if __name__ == "__main__":
    main()
