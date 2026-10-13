"""The main menu's background: the towns' flybys (tools/scene3d/render_scene.py --path) joined into one seamless loop,
each crossfading into the next and the last back into the first, packed as flipbook sheets for the menu mesh
(Skyrim/Render/MenuBuilder.cs): COLS x ROWS frames per DXT1 texture.

  flyby_loop.py OUT_DIR FPS FADE_FRAMES TOWN_DIR [TOWN_DIR ...]
writes OUT_DIR/flyby_NN.dds and OUT_DIR/flyby.json (frame count, fps, layout), and a contact sheet flyby.png.
"""
import glob
import json
import os
import sys

import numpy as np
from PIL import Image

COLS, ROWS, SHEET = 4, 7, 4096


def frames(d):
    return sorted(glob.glob(os.path.join(d, "*.png")))


def main(out_dir, fps, fade, *towns):
    fps, fade = float(fps), int(fade)
    clips = [frames(t) for t in towns]
    seq = []   # (path_a, path_b or None, mix)
    for i, clip in enumerate(clips):
        nxt = clips[(i + 1) % len(clips)]
        n = len(clip)
        for k in range(fade, n - fade):
            seq.append((clip[k], None, 0.0))
        for k in range(fade):
            t = (k + 1) / (fade + 1)
            seq.append((clip[n - fade + k], nxt[k], t * t * (3 - 2 * t)))
    first = Image.open(seq[0][0])
    fw, fh = first.size
    per = COLS * ROWS
    os.makedirs(out_dir, exist_ok=True)
    sheets = (len(seq) + per - 1) // per
    for s in range(sheets):
        sheet = Image.new("RGB", (SHEET, SHEET))
        for j, (a, b, m) in enumerate(seq[s * per:(s + 1) * per]):
            img = np.asarray(Image.open(a).convert("RGB")).astype(np.float32)
            if b:
                img = img * (1 - m) + np.asarray(Image.open(b).convert("RGB")).astype(np.float32) * m
            sheet.paste(Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)), ((j % COLS) * fw, (j // COLS) * fh))
        sheet.save(os.path.join(out_dir, f"flyby_{s:02d}.dds"), pixel_format="DXT1")
        print(f"  sheet {s + 1}/{sheets}", flush=True)
    json.dump({"frames": len(seq), "fps": fps, "cols": COLS, "rows": ROWS, "sheet": SHEET, "frameWidth": fw,
               "frameHeight": fh, "sheets": sheets, "towns": [os.path.basename(t.rstrip("/\\")) for t in towns]},
              open(os.path.join(out_dir, "flyby.json"), "w"), indent=1)
    thumbs = [Image.open(seq[i][0]).resize((256, 144)) for i in range(0, len(seq), max(1, len(seq) // 16))][:16]
    contact = Image.new("RGB", (1024, 576))
    for i, t in enumerate(thumbs):
        contact.paste(t, ((i % 4) * 256, (i // 4) * 144))
    contact.save(os.path.join(out_dir, "flyby.png"))
    print(f"{len(seq)} frames ({len(seq) / fps:.1f} s at {fps:g} fps) in {sheets} sheets")


if __name__ == "__main__":
    main(*sys.argv[1:])
