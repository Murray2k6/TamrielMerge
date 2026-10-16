# The games' intro videos

Each installed game's opening cinematic plays once, at the start of its story (TMStoryOrder.PlayIntro), as
`Video\TamrielMerge_<Game>Intro.bik`. `TamrielMerge.Cli videos --out DIR [--gpu] [--no-rt-look]` makes them; `convert`
runs it (`--skip Videos` leaves them out).

## Remaster

`tools/video/remaster.py`: the game's video decoded (ffmpeg; Daggerfall's DAG2.VID by `df-vid`, Redguard's INTRO.SMK
off its CD image), every frame upscaled with Real-ESRGAN to 1440x1080 (Oblivion's to 1920x1080), and encoded as Bink 1
with RAD Video Tools' compressor, the format Skyrim plays, with the sound mixed in. On the CPU with the fast model
unless `--gpu`. Without the tools, Morrowind's and Oblivion's own Bink intros are copied as they are.

## Daggerfall's quest videos

The same step makes the videos Daggerfall's quests and covens play, from the player's own ARENA2:

- `ANIMnnnn.VID` (`play video n`, Daggerfall Unity PlayVideo: the main quest's visions and the seven endings, 3-15) as
  `Video\TamrielMerge_DfAnim<n>.bik`, decoded by `DfVid` at 30 fps;
- the Daedra Princes' `*.FLC` animations (shown when a coven summons one, `DfQuestBuilder.Daedra`) as
  `Video\TamrielMerge_DfDaedra<NAME>.bik`, through ffmpeg and looped three times (they are about three seconds).

They are only resized (`remaster.py --plain`: bicubic to 960x720, no model; their sources are 640x480 at most, and the
Bink compressor's time goes with the pixels), so they need RAD's compressor, ffmpeg and
the Python environment but not the upscaler; `--upscale-quest-videos` runs them through Real-ESRGAN as the intros are
(slow on the CPU). `--no-quest-videos` leaves them out. Dev: `videos --quest-videos-only --anims 11,AZURA`.

## Redguard's story movies

Redguard plays ten more movies in the story (MENU.INI `movie_name[n]`, Smacker files on the CD): 1 SCENE3 (the
journal), 2 SCENE7 (escaping N'Gasta's isle after Vile's riddles), 3 SCENE10 (A'tor's soul going into the sword), 4 WIN
(the ending), 5-10 C/A/B_RUINS1-2 (the dwarven ruins' machines). The videos step (Redguard/RgMovies.cs) extracts each
from REDGUARD.bin and converts it (640x240 interlaced shown at 640x480, then 1440x1080) to
`Video\TamrielMerge_RedguardMovie{n}.bik`, with the movie's lines (`movie#_text`: first and last frame, text) drawn at
the foot of their frames (`remaster.py --subtitles`), since Skyrim's Bink player shows no text. The scripts play them
where Redguard queued them (QueueMovie(n) right before the map change that followed; WonGame plays 4) through
`Game.PlayBink` (TMRgState.PlayMovie). Like Daggerfall's quest videos they are only resized unless
`--upscale-quest-videos` (2026-10-16: on the CPU the upscale took nearly two hours for movie 1's 2,472 frames, and
movie 2 has 5,970). Resized frames go to the work folder's `plain\`, so a stopped upscale's frames in `up\` are kept for
a later upscale and never mixed with them.

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
