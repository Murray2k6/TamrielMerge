# The games' intro videos

Each installed game's opening cinematic plays once, at the start of its story (TMStoryOrder.PlayIntro), as
`Video\TamrielMerge_<Game>Intro.bik`. `TamrielMerge.Cli videos --out DIR [--gpu] [--no-rt-look]` makes them; `convert`
runs it (`--skip Videos` leaves them out).

## Remaster

`tools/video/remaster.py`: the game's video decoded (ffmpeg; Daggerfall's DAG2.VID by `df-vid`, Redguard's INTRO.SMK
off its CD image), every frame upscaled with Real-ESRGAN to 1440x1080 (Oblivion's to 1920x1080), and encoded as Bink 1
with RAD Video Tools' compressor, the format Skyrim plays, with the sound mixed in. On the CPU with the fast model
unless `--gpu`. Without the tools, Morrowind's and Oblivion's own Bink intros are copied as they are.

## The ray-traced look (`--gpu`)

User request (2026-10-08): the intros made to look ray-traced, like the path-traced menu intro. They are footage, so
nothing can be traced: `tools/video/rt_look.py` estimates each frame's depth (Depth Anything V2, smoothed over time
and reset at cuts) and lights from it.

- The light: `--light torch` (Daggerfall) lights from the torch's flame when it is in frame, flickering with it; when
  it isn't (close-ups), and in `--light auto` (Oblivion, Redguard), the footage's own key light is read from its
  shading (brightness fitted against surface facing) and strengthened, never contradicted.
- Brightness only: every pixel keeps its filmed hue and saturation (skin tone), the light only changes how bright it
  is, with a slight warmth; colourfulness is matched back to the frame's, and the colour scaling is measured over a
  small area and capped so near-black eye sockets don't turn red.
- Daggerfall only: broad shadows (a face's far side, filmed under studio lamps with a prop torch) are faded toward the
  surface round them, as a smooth field so eyes and creases keep their shading.
- Gentle occlusion in creases (never on bright things), a small soft glow round the flame, the flame itself as
  filmed. Outdoors and in abstract footage the effect fades with distance, where estimated depth can't be trusted.
- Tried and removed (2026-10-08): light shafts (their stepped radial smear drew rings), shadows marched through the
  estimated depth (they cut hard seams across faces), an unsoftened facing term (rings at the flame's edge).

Morrowind's intro (text and dream imagery, no real depth) stays upscaled only: the depth model invents shapes in it.

## Daggerfall's tape footage

Daggerfall's video is 256x200, from video tape, its torch filmed through a lens flare. `tools/video/deflare.py`
cleans the upscaled frames first:

- the AI upscale tempered 65/35 with a plain enlargement and given a fine grain (alone it smooths skin and hands into
  wax and sharpens soft shadows into lines);
- colour softened back to tape's resolution, brightness keeping its detail (the upscale's colour detail on skin is
  invented);
- round the flame, the smooth enlargement in place of the upscaler's hard splatter;
- the flare's ring found (Hough circles round a big flame, scored by edge strength, smoothed within a shot) and
  painted out. It lives a third of a second (frames 33-42) and grows past the screen; faint traces can remain.

## Tools

`setup` installs them: the Python environment gets OpenCV and psutil, and the depth model is downloaded into the
tools' Hugging Face cache (`tools\model3d\hf`).
