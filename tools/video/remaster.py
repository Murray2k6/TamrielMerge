"""TamrielMerge video remastering: a game's cinematic upscaled with Real-ESRGAN (x4plus) and encoded as Bink 1 for Skyrim.

Steps (each resumable; work files under --work):
  1. frames: from a video file through ffmpeg (Smacker, Bink, ...) at its own frame rate, or from a folder of
     frameNNNNN.png with audio.wav (Daggerfall's VIDs, exported by `TamrielMerge df-vid`). --prescale WxH first
     resizes frames to their display shape (Redguard's 640x240 half-height video shown at 640x480).
  2. upscale: each frame through the model (x4), then resized to --size (default 1440x1080, 4:3).
  3. encode: RAD's Bink 1 compressor (radvideo64 binkc) over the PNG sequence at the frame rate, then the audio mixed in
     (binkmix), giving --output.
--subtitles FILE.json: [{"start": frame, "end": frame, "text": "..."}] (the source's frame numbers, from 0) drawn at the
foot of the upscaled frames before encoding (Redguard's movies show their lines as text, MENU.INI movie#_text).
--no-upscale: frames only resized to --size (bicubic), no model (quick; for checking a conversion).

  --plain skips step 2's model: frames are only resized (bicubic), for videos that don't need the slow upscale
  (Daggerfall's quest videos by default).

Usage: python remaster.py --input FILE|DIR --output out.bik --work DIR [--fps 30] [--prescale 640x480] [--size 1440x1080] [--plain]
       [--loop N] [--subtitles lines.json] [--no-upscale]
"""
import argparse, glob, os, subprocess, sys, time

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.environ.get("TAMRIELMERGE_TOOLS") or os.path.dirname(HERE)
FFMPEG = os.path.join(TOOLS, "ffmpeg", "ffmpeg-master-latest-win64-gpl", "bin", "ffmpeg.exe")
FFPROBE = os.path.join(TOOLS, "ffmpeg", "ffmpeg-master-latest-win64-gpl", "bin", "ffprobe.exe")
RAD = os.path.join(TOOLS, "rad", "bin", "radvideo64.exe")
MODEL = os.path.join(TOOLS, "upscale", "RealESRGAN_x4plus.pth")


def probe_fps(path):
    out = subprocess.run([FFPROBE, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate",
                          "-of", "default=nw=1:nk=1", path], capture_output=True, text=True).stdout.strip()
    num, _, den = out.partition("/")
    return float(num) / float(den or 1)


def extract(args, src_dir):
    """Frames (and audio.wav) into src_dir; returns the frame rate."""
    os.makedirs(src_dir, exist_ok=True)
    if os.path.isdir(args.input):
        frames = sorted(glob.glob(os.path.join(args.input, "frame*.png")))
        for i, f in enumerate(frames, 1):
            dst = os.path.join(src_dir, f"frame{i:05d}.png")
            if not os.path.exists(dst):
                img = Image.open(f).convert("RGB")
                if args.prescale:
                    img = img.resize(args.prescale, Image.BICUBIC)
                img.save(dst)
        audio = os.path.join(args.input, "audio.wav")
        if os.path.exists(audio):
            subprocess.run([FFMPEG, "-v", "error", "-y", "-i", audio, "-ar", "44100", "-ac", "2", "-c:a", "pcm_s16le",
                            os.path.join(src_dir, "audio.wav")], check=True)
        return args.fps or 30.0
    fps = args.fps or probe_fps(args.input)
    if not glob.glob(os.path.join(src_dir, "frame*.png")):
        vf = [f"fps={fps}"]
        if args.prescale:
            vf.append(f"scale={args.prescale[0]}:{args.prescale[1]}:flags=bicubic")
        loop = ["-stream_loop", str(args.loop)] if args.loop > 0 else []
        subprocess.run([FFMPEG, "-v", "error", "-y"] + loop + ["-i", args.input, "-vf", ",".join(vf), "-start_number", "1",
                        os.path.join(src_dir, "frame%05d.png")], check=True)
        # A silent source (Daggerfall's FLC animations) has no sound track: ffmpeg then writes nothing and says so.
        sound = subprocess.run([FFMPEG, "-v", "error", "-y", "-i", args.input, "-vn", "-ar", "44100", "-ac", "2", "-c:a", "pcm_s16le",
                                os.path.join(src_dir, "audio.wav")], capture_output=True, text=True)
        if sound.returncode != 0:
            silent = "does not contain any stream" in sound.stderr
            print("no sound track" if silent else sound.stderr.strip(), flush=True)
    return fps


def gpu_temperature():
    """The GPU's temperature in C (nvidia-smi), or None."""
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=10).stdout.strip().splitlines()
        return int(out[0]) if out else None
    except (OSError, ValueError, subprocess.TimeoutExpired):
        return None


def cool_down(max_temp=78):
    """Waits while the GPU is above max_temp (user, 2026-10-08: it overheated)."""
    t = gpu_temperature()
    while t is not None and t > max_temp:
        print(f"  GPU at {t} C, waiting to cool below {max_temp} C", flush=True)
        time.sleep(20)
        t = gpu_temperature()


def upscale(src_dir, up_dir, size, device="cuda", model_path=MODEL, threads=0):
    from spandrel import ModelLoader
    if device == "cuda" and not torch.cuda.is_available():
        device = "cpu"
    if device == "cpu" and threads:
        torch.set_num_threads(threads)
    model = ModelLoader().load_from_file(model_path).model.to(device).eval()
    dtype = torch.float16 if device == "cuda" else torch.float32
    model = model.to(dtype)
    os.makedirs(up_dir, exist_ok=True)
    frames = sorted(glob.glob(os.path.join(src_dir, "frame*.png")))
    todo = [f for f in frames if not os.path.exists(os.path.join(up_dir, os.path.basename(f)))]
    print(f"{len(frames)} frames, {len(todo)} to upscale", flush=True)
    start = time.time()
    # Frames are read and decoded, and results encoded and written, on worker threads (CPU) while the model runs (GPU).
    from concurrent.futures import ThreadPoolExecutor
    workers = max(4, (os.cpu_count() or 8) - 2)
    pool = ThreadPoolExecutor(workers)

    def load(f):
        data = open(f, "rb").read()
        return f, data

    def save(dst, out):
        Image.fromarray(out).save(dst + ".tmp.png", compress_level=1)
        os.replace(dst + ".tmp.png", dst)

    def copy(src, dst, wait):
        wait.result()
        with open(src, "rb") as a, open(dst + ".tmp.png", "wb") as b:
            b.write(a.read())
        os.replace(dst + ".tmp.png", dst)

    prev_bytes, prev_out, prev_save = None, None, None
    pending = []
    def prefetch(files, ahead=32):
        from collections import deque
        queue = deque()
        for f in files:
            queue.append(pool.submit(load, f))
            if len(queue) >= ahead:
                yield queue.popleft().result()
        while queue:
            yield queue.popleft().result()

    loaded = prefetch(todo)
    for i, (f, data) in enumerate(loaded, 1):
        dst = os.path.join(up_dir, os.path.basename(f))
        if device == "cuda" and i % 25 == 0:
            cool_down()
        if data == prev_bytes and prev_save is not None:
            # A repeated frame (Daggerfall's videos hold each picture for many frames): reuse its upscale.
            pending.append(pool.submit(copy, prev_out, dst, prev_save))
            continue
        import io
        rgb = np.asarray(Image.open(io.BytesIO(data)).convert("RGB"), dtype=np.float32) / 255.0
        with torch.no_grad():
            t = torch.from_numpy(rgb).permute(2, 0, 1).unsqueeze(0).to(device).to(dtype)
            pad = 8
            t = F.pad(t, (pad, pad, pad, pad), mode="replicate")
            up = model(t).float().clamp(0, 1)[:, :, pad * 4:-pad * 4, pad * 4:-pad * 4]
            up = F.interpolate(up, size=(size[1], size[0]), mode="bicubic", antialias=True, align_corners=False).clamp(0, 1)
            out = (up[0].permute(1, 2, 0).cpu().numpy() * 255.0 + 0.5).astype(np.uint8)
        prev_save = pool.submit(save, dst, out)
        pending.append(prev_save)
        prev_bytes, prev_out = data, dst
        if i % 50 == 0 or i == len(todo):
            rate = i / (time.time() - start)
            print(f"  {i}/{len(todo)} ({rate:.2f}/s, about {(len(todo) - i) / max(rate, 1e-6) / 60:.0f} min left)", flush=True)
        if len(pending) > 64:
            pending = [p for p in pending if not p.done()]
    for p in pending:
        p.result()
    pool.shutdown()


def resize(src_dir, up_dir, size):
    """--plain: each frame only resized (bicubic) to size; a repeated frame reuses the last result."""
    import io
    os.makedirs(up_dir, exist_ok=True)
    frames = sorted(glob.glob(os.path.join(src_dir, "frame*.png")))
    prev_bytes, prev_dst = None, None
    for f in frames:
        dst = os.path.join(up_dir, os.path.basename(f))
        data = open(f, "rb").read()
        if not os.path.exists(dst):
            if data == prev_bytes and prev_dst:
                with open(prev_dst, "rb") as a, open(dst, "wb") as b:
                    b.write(a.read())
            else:
                Image.open(io.BytesIO(data)).convert("RGB").resize(size, Image.BICUBIC).save(dst, compress_level=1)
        prev_bytes, prev_dst = data, dst
    print(f"{len(frames)} frames resized to {size[0]}x{size[1]}", flush=True)


def subtitle(up_dir, lines_path):
    """Draws each line on its frames (source frame n is frame n + 1 of the sequence), once (a marker file)."""
    done = os.path.join(up_dir, "subtitled.done")
    if os.path.exists(done):
        return
    import json
    from PIL import ImageDraw, ImageFont
    lines = json.load(open(lines_path, encoding="utf-8-sig"))
    frames = sorted(glob.glob(os.path.join(up_dir, "frame*.png")))
    if not frames:
        return
    w, h = Image.open(frames[0]).size
    size = max(18, h // 24)
    font = None
    for name in ("arial.ttf", "segoeui.ttf", "DejaVuSans.ttf"):
        try:
            font = ImageFont.truetype(name, size)
            break
        except OSError:
            pass
    if font is None:
        font = ImageFont.load_default()
    for line in lines:
        for n in range(int(line["start"]), int(line["end"]) + 1):
            path = os.path.join(up_dir, f"frame{n + 1:05d}.png")
            if not os.path.exists(path):
                continue
            img = Image.open(path).convert("RGB")
            draw = ImageDraw.Draw(img)
            # Wrapped to the frame's width, centred near the foot, white with a dark outline.
            words, rows, row = line["text"].split(), [], ""
            for word in words:
                test = (row + " " + word).strip()
                if draw.textlength(test, font=font) > w * 0.86 and row:
                    rows.append(row)
                    row = word
                else:
                    row = test
            rows.append(row)
            y = h - (len(rows) + 1) * int(size * 1.25)
            for r in rows:
                x = (w - draw.textlength(r, font=font)) / 2
                draw.text((x, y), r, font=font, fill=(255, 255, 255), stroke_width=max(1, size // 12), stroke_fill=(0, 0, 0))
                y += int(size * 1.25)
            img.save(path + ".tmp.png", compress_level=1)
            os.replace(path + ".tmp.png", path)
    open(done, "w").close()


def run_rad(args):
    """Runs a RAD tool and closes its window once it reports "Done" (it waits open otherwise)."""
    proc = subprocess.Popen([RAD] + args)
    while proc.poll() is None:
        time.sleep(3)
        title = subprocess.run(["tasklist", "/V", "/FO", "CSV", "/NH", "/FI", f"PID eq {proc.pid}"], capture_output=True, text=True).stdout
        if "Done" in title:
            time.sleep(2)
            subprocess.run(["taskkill", "/PID", str(proc.pid)], capture_output=True)
            proc.wait(timeout=30)
            return
    if proc.returncode != 0:
        raise RuntimeError(f"RAD {args[0]} failed ({proc.returncode})")


def encode(up_dir, src_dir, fps, output):
    frames = sorted(glob.glob(os.path.join(up_dir, "frame*.png")))
    video = output + ".video.bik"
    pattern = os.path.join(up_dir, "frame?????.png") + f"*1-{len(frames)}"
    if not os.path.exists(video):
        run_rad(["binkc", pattern, video + ".tmp.bik", f"/F{fps:.4f}"])
        os.replace(video + ".tmp.bik", video)
    audio = os.path.join(src_dir, "audio.wav")
    if os.path.exists(audio) and os.path.getsize(audio) > 1000:
        run_rad(["binkmix", video, audio, output])
        os.remove(video)
    else:
        os.replace(video, output)
    print(f"wrote {output} ({os.path.getsize(output) / 1e6:.1f} MB)", flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--input", required=True)
    ap.add_argument("--output", required=True)
    ap.add_argument("--work", required=True)
    ap.add_argument("--fps", type=float, default=0)
    ap.add_argument("--prescale", type=lambda s: tuple(int(x) for x in s.split("x")))
    ap.add_argument("--size", type=lambda s: tuple(int(x) for x in s.split("x")), default=(1440, 1080))
    ap.add_argument("--device", default="cuda", help="cuda, or cpu to spare the graphics card")
    ap.add_argument("--model", default=MODEL, help="Real-ESRGAN weights (realesr-general-x4v3.pth is the fast one for CPU)")
    ap.add_argument("--threads", type=int, default=0, help="CPU threads for the model (0 = PyTorch's default)")
    ap.add_argument("--plain", action="store_true", help="no model: frames only resized (bicubic) to --size")
    ap.add_argument("--loop", type=int, default=0, help="a video file played this many extra times (short animations)")
    ap.add_argument("--subtitles", help="JSON lines drawn on the frames (start/end source frames, text)")
    ap.add_argument("--no-upscale", action="store_true", help="the same as --plain")
    args = ap.parse_args()
    if os.path.exists(args.output):
        print(f"{args.output} already made", flush=True)
        return
    args.work = os.path.abspath(args.work)
    if not os.path.isdir(args.input):
        args.input = os.path.abspath(args.input)
    src_dir, up_dir = os.path.join(args.work, "src"), os.path.join(args.work, "up")
    fps = extract(args, src_dir)
    print(f"frames at {fps:.3f} fps", flush=True)
    if args.plain or args.no_upscale:
        # Resized frames in a folder of their own: a stopped upscale's frames in up\ are kept for a later upscale, not
        # mixed with resized ones.
        up_dir = os.path.join(args.work, "plain")
        resize(src_dir, up_dir, args.size)
    else:
        upscale(src_dir, up_dir, args.size, args.device, args.model, args.threads)
    if args.subtitles:
        subtitle(up_dir, args.subtitles)
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    encode(up_dir, src_dir, fps, os.path.abspath(args.output))


if __name__ == "__main__":
    main()
