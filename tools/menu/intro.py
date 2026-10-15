"""The main menu's intro video (docs/menu.md): Oblivion's menu flies slowly over Cyrodiil behind its logo; ours flies
over the conversions, one cinematic aerial shot per game, each crossfading into the next and the last back into the
first, at the size and frame rate the menu mesh plays (Skyrim/Render/MenuBuilder.cs).

  python intro.py export  OUT --cli EXE --plugins DIR [--data DIR]   the shots' scenes, exported where missing
  python intro.py prep    OUT                  each scene prepared for Blender (tools/scene3d/blend_prep.py)
  python intro.py paths   OUT                  camera paths of the shots (OUT/<shot>.json)
  python intro.py preview OUT [shot ...]       three stills per shot, small (OUT/preview/<shot>_N.png)
  python intro.py render  OUT [shot ...]       every frame (OUT/<shot>/NNNNN.png)
  python intro.py rt-preview OUT [shot ...]    one ray-traced still per shot, full size (OUT/rt-preview/<shot>/)
  python intro.py rt-render  OUT [shot ...]    every frame ray-traced in Blender's EEVEE (OUT/rt/<shot>/)
  python intro.py pack    OUT MENU_DIR [--frames rt] [--emblem PNG]   grade, captions, crossfades
                                               -> MENU_DIR/flyby_NN.dds + flyby.json (and the banner, below)
  python intro.py banner  OUT MENU_DIR --emblem PNG   the emblem on its banner -> MENU_DIR/banner.dds + banner.json
  python intro.py upscale OUT [--frames rt] [--size 2560x1440]   the frames through Real-ESRGAN (tools/video/remaster.py,
                                               on the GPU, 78 C guard) -> OUT/<frames><height>/<shot>/
  pack --size WxH packs frames of that size (e.g. --frames rt1440 --size 2560x1440); sheets hold as many whole frames
  as fit in 4096 each way (2560x1440: 1 x 2, 2560x2880 sheets).

The rt- commands (user, 2026-10-08: "make it look raytraced"; 2026-10-09: "flybys need rt not pt": EEVEE's ray
tracing, not Cycles' path tracing) need each scene prepared by
tools/scene3d/blend_prep.py; tools/scene3d/blend_scene.py builds and renders it. Blender is stopped if free memory
(commit) falls under 0.7 GB.

Only the games converted get a shot (user, 2026-10-09: "with a persons converted or owned games equivulant the
flybys will be different depending on what game they own"): export leaves out a shot whose plugin is missing, and
every later step works on the shots exported.

Paths: scenes in $TAMRIELMERGE_SCENES (default out/scenes); the converter's tools (the Python with moderngl, Blender)
from $TAMRIELMERGE_MODEL_PYTHON and $TAMRIELMERGE_BLENDER, set by `TamrielMerge.Cli menu`, else found under
$TAMRIELMERGE_TOOLS (default .	ools) and Blender's usual install folders.

`--pause PID` (preview, render) suspends that process while the GPU renders and resumes it after (the voice generator,
which shares the GPU: user, 2026-10-08). Renders wait while the GPU is above 78 C (render_scene.cool_down).
"""
import copy
import json
import math
import os
import subprocess
import sys
import time
from contextlib import contextmanager

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

CODE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # the tools' scripts (repo tools\, or the build's)
DEPS = os.path.abspath(os.environ.get("TAMRIELMERGE_TOOLS") or "tools")  # the installed tools (setup)
SCENES = os.path.abspath(os.environ.get("TAMRIELMERGE_SCENES") or os.path.join("out", "scenes"))
RENDER = os.path.join(CODE, "scene3d", "render_scene.py")
# The renderer needs moderngl, installed in the model tools' venv.
RENDER_PY = os.environ.get("TAMRIELMERGE_MODEL_PYTHON") or os.path.join(DEPS, "model3d", ".venv", "Scripts", "python.exe")
FPS = 20
SIZE = (1200, 675)          # 3 x 6 frames on a 4096 sheet
FADE = 20                   # crossfade frames between shots
FOV = 36                    # vertical, degrees: a long lens, as aerial footage is shot
CAPTION_FONT = "C:/Windows/Fonts/pala.ttf"
BLEND_SCENE = os.path.join(CODE, "scene3d", "blend_scene.py")


def find_blender():
    """Blender (5.x): the converter's choice, else its own portable copy, else an installed one."""
    import glob
    if os.environ.get("TAMRIELMERGE_BLENDER"):
        return os.environ["TAMRIELMERGE_BLENDER"]
    found = sorted(glob.glob(os.path.join(DEPS, "blender", "*", "blender.exe")))
    found += sorted(glob.glob("C:/Program Files/Blender Foundation/Blender */blender.exe"))
    found += glob.glob("C:/Program Files (x86)/Steam/steamapps/common/Blender/blender.exe")

    def version(exe):
        """The exe's own version, as the converter takes it (the newest wins: 5.1's EEVEE drew enclosed streets black)."""
        try:
            out = subprocess.run([exe, "-b", "--factory-startup", "--version"], capture_output=True, text=True, timeout=60).stdout
            return tuple(int(x) for x in out.split()[1].split(".")[:3])
        except Exception:
            return (0,)
    return max(found, key=version) if found else None
RT_SAMPLES = 64      # EEVEE's anti-aliasing samples a frame

# Each shot: its scene, light, caption and camera. "orbit": round a centre (angle in degrees from +x, counter-
# clockwise), radius and height moving from the first value to the second, looking at the centre raised by look_z.
# "glide": eye and target each moving in a straight line.
# Level paths (user, 2026-10-08/09: "ground is sinking again", "its all"): a camera rising while it looks at the same
# spot slides the whole landscape down the frame, so every shot keeps one height and one aim height. And one
# distance: closing in (or Daggerfall's old glide up the boulevard, 3 to 4 px a frame) streamed the ground downward too
# (measured with optical flow, 2026-10-09). Circling sank one side of the ground (2026-10-10), so each shot passes its
# place sideways (camera(): the "orbit" numbers place the pass).
SHOTS = [
    # Skyrim itself (user, 2026-10-08: "we never added skyrim to the flyby"): Whiterun on its hill over the tundra, from
    # Skyrim.esm in the game's Data folder ("skyrim": the plugin isn't a converted one).
    {"name": "whiterun", "scene": "whiterun", "caption": ("Whiterun", "Skyrim"), "seconds": 7.5,
     # The land well past the objects (user, 2026-10-08: "why is the ground sinking"): with only the objects' circle
     # of land, Whiterun stood on an island over a plain at the valley floor's level.
     "export": {"plugin": "Skyrim.esm", "world": "Tamriel+WhiterunWorld", "centre": [24000, -3000], "radius": 22000, "land_radius": 70000,
                "skyrim": True},
     "light": {"azimuth": 225, "elevation": 40, "sun": [1.0, 0.86, 0.66], "sun_power": 3.6, "sky": [0.36, 0.5, 0.74],
               "fog": [0.74, 0.74, 0.72], "fog_density": 1.0e-5, "exposure": 0.92, "water": [0.05, 0.11, 0.15],
               "cloud_cover": 0.45},
     # The tundra's golden grass (the export has none): its ground warmed and brightened, and past the land's edge.
     "land_tint": [6.0, 5.4, 4.2], "ground": [0.2, 0.17, 0.09], "populate": 90,
     # The city's own ground (its streets' stone floor, slate edging, dirt, grass and the plots' stone: dark textures in
     # the shade between the raised plots) tinted as the land is (user, 2026-10-11: "black still"): untinted beside the
     # tinted land the streets read as black channels. Texture names, by how they start. (Not the walls' tops: their
     # black bands were the backs of one-sided faces, which blend_scene now hides as the game does.)
     "tint_textures": ["wrstonefloor", "wrslate", "wrbasedirt", "wrdirtbase", "wrfieldgrass", "wrstonebase"],
     "orbit": {"centre": [24000, -3000, 0], "angle": [200, 228], "radius": [15750, 15750], "height": [2050, 2050],
               "look_z": [-2050, -2050]}},
    {"name": "imperialcity", "scene": "imperialcity", "caption": ("The Imperial City", "Oblivion"), "seconds": 7.5,
     # The city is a worldspace a district on Tamriel's coordinates (Tamriel has only its walls and tower): all of them
     # over Tamriel (user, 2026-10-09: "the games have interiors and exteriors so the worldspace needs to be correct"),
     # not the main quest's variants (MQ16). The Waterfront is Tamriel's own.
     "export": {"plugin": "Oblivion.esm", "world": "OBTamriel+OBICMarketDistrict+OBICTempleDistrict+OBICArenaDistrict"
                "+OBICElvenGardensDistrict+OBICArboretumDistrict+OBICTheArcaneUniversity+OBICTalosPlazaDistrict"
                "+OBICImperialPrisonDistrict+OBICImperialPalace", "centre": [28700, 63424], "radius": 18000, "land_radius": 60000},
     "populate": 140,
     "light": {"azimuth": 105, "elevation": 16, "sun": [1.0, 0.82, 0.62], "sun_power": 3.6, "sky": [0.36, 0.5, 0.74],
               "fog": [0.7, 0.66, 0.62], "fog_density": 0.8e-5, "exposure": 0.92, "water": [0.05, 0.11, 0.15],
               "cloud_cover": 0.4},
     # High enough to look over the walls into the districts (streets at about 3500, the tower's top near 17500).
     "orbit": {"centre": [28672, 63424, 0], "angle": [206, 236], "radius": [34000, 34000], "height": [14000, 14000],
               "look_z": [7300, 7300]}},
    {"name": "vivec", "scene": "vivec", "caption": ("Vivec", "Morrowind"), "seconds": 7.5,
     "export": {"plugin": "Morrowind.esm", "world": "MWVvardenfell", "centre": [31500, -100000], "radius": 16000, "land_radius": 50000},
     "populate": 110,
     "light": {"azimuth": 255, "elevation": 11, "sun": [1.0, 0.7, 0.45], "sun_power": 3.4, "sky": [0.34, 0.42, 0.64],
               "fog": [0.78, 0.6, 0.48], "fog_density": 0.9e-5, "exposure": 0.95, "water": [0.04, 0.09, 0.12],
               "cloud_cover": 0.5},
     "orbit": {"centre": [32800, -97000, 0], "angle": [-62, -38], "radius": [17750, 17750], "height": [2000, 2000],
               "look_z": [1700, 1700]}},
    # Daggerfall's town is a worldspace of its own, without land: shown where it stands in the Iliac Bay overland (user,
    # 2026-10-09: "the menu flybys need to be the games worldspace not a test area"). "~+DFTDaggerfall17@door": the
    # exporter finds the overland (whichever tile holds the gate's door) and the offset putting the town on that door
    # (84992, -141312, 2301 with the default terrain settings), so this shot's numbers are the town's own coordinates;
    # the camera and the crowd move with the offset scene.json records. The overland's land under the town is taken out.
    # Under a sunlit morning mist (user, 2026-10-08: the first render's horizon read as a grey fog bank).
    {"name": "daggerfall", "scene": "daggerfall", "caption": ("Daggerfall", "Daggerfall"), "seconds": 7.5,
     # The overland's land is in its own plugin (Daggerfall Land.esm), loaded over Daggerfall.esm.
     "export": {"plugin": "Daggerfall.esm", "also": ["Daggerfall Land.esm"], "world": "~+DFTDaggerfall17@door",
                "centre": [-2048, -2048], "radius": 46000, "land_radius": 75000},
     "light": {"azimuth": 240, "elevation": 14, "sun": [1.0, 0.74, 0.48], "sun_power": 4.2, "sky": [0.38, 0.5, 0.72],
               "fog": [0.9, 0.8, 0.66], "fog_density": 0.8e-5, "exposure": 0.95, "cloud_cover": 0.55},
     "ground": [0.2, 0.25, 0.1], "land_tint": [3.2, 3.6, 2.4],
     # Thinner haze since EEVEE (2026-10-09: its stronger sky light thickened it into a grey wall hiding the overland).
     # Townsfolk along the boulevard the camera glides up (user, 2026-10-08: "what happened to the npcs").
     "crowd": [{"line": [[12300, -6000], [11600, 17000]], "width": 900, "count": 140}], "populate": 60,
     "orbit": {"centre": [11600, 9000, 0], "angle": [-96, -76], "radius": [17000, 17000], "height": [3900, 3900],
               "look_z": [-2300, -2300]}},
    {"name": "strosmkai", "scene": "strosmkai", "caption": ("Stros M'Kai", "Redguard"), "seconds": 7.5,
     # The palace's courtyard is a worldspace of its own on the island's coordinates (Redguard's EXTPALAC).
     "export": {"plugin": "Redguard.esm", "world": "RGISLAND+RGEXTPALAC", "centre": [0, 0], "radius": 0},
     "populate": 90,
     "light": {"azimuth": 200, "elevation": 22, "sun": [1.0, 0.88, 0.7], "sun_power": 3.6, "sky": [0.3, 0.5, 0.8],
               "fog": [0.68, 0.74, 0.8], "fog_density": 1.0e-5, "exposure": 0.95, "water": [0.03, 0.12, 0.16],
               "cloud_cover": 0.35},
     # The town where the island's stretch (Redguard/RgWarp: its wilds widened, settlements only moved) put it: 8500,
     # 3500 from where it stood (its buildings matched between the exports).
     "orbit": {"centre": [13500, 12000, 0], "angle": [-80, -52], "radius": [14250, 14250], "height": [2150, 2150],
               "look_z": [550, 550]}},
]


def ease(u):
    """Mostly steady, easing in and out a little: the crossfades cover the ends."""
    return 0.65 * u + 0.35 * u * u * (3 - 2 * u)


def lerp(a, b, u):
    return np.array(a, float) + (np.array(b, float) - np.array(a, float)) * u


def camera(shot, u):
    u = ease(u)
    if "orbit" in shot and shot["orbit"].get("circle"):
        o = shot["orbit"]
        ang = math.radians(lerp([o["angle"][0]], [o["angle"][1]], u)[0])
        r = lerp([o["radius"][0]], [o["radius"][1]], u)[0]
        h = lerp([o["height"][0]], [o["height"][1]], u)[0]
        c = np.array(o["centre"], float)
        eye = c + np.array([math.cos(ang) * r, math.sin(ang) * r, h])
        target = c + np.array([0, 0, lerp([o["look_z"][0]], [o["look_z"][1]], u)[0]])
        return eye, target
    if "orbit" in shot:
        # A sideways pass (user, 2026-10-10: the ground still sank "when it rotates"): circling a point while looking
        # down at it turns the ground like a tilted plate, one side of the picture sinking while the other rises (0.5 px
        # a frame at 1200 wide, measured with optical flow). Eye and aim slide together along the camera's own sideways
        # axis, so everything moves only sideways in the picture. The pass runs along the middle of the orbit it
        # replaces, as long as the orbit's arc, at the same height, distance and aim ("circle": true keeps the orbit).
        o = shot["orbit"]
        a0, a1 = (math.radians(a) for a in o["angle"])
        mid = (a0 + a1) / 2
        r, h, look = o["radius"][0], o["height"][0], o["look_z"][0]
        c = np.array(o["centre"], float)
        out = np.array([math.cos(mid), math.sin(mid), 0.0])
        side = np.array([-math.sin(mid), math.cos(mid), 0.0])
        shift = side * r * (a1 - a0) * (u - 0.5)
        return c + out * r + np.array([0, 0, h]) + shift, c + np.array([0, 0, look]) + shift
    g = shot["glide"]
    return lerp(*g["eye"], u), lerp(*g["target"], u)


def placed(s):
    """The shot moved by its scene's offset (scene.json "offset": a town placed into its overland), its camera and crowd
    given in the town's own coordinates."""
    off = json.load(open(os.path.join(SCENES, s["scene"], "scene.json"))).get("offset")
    if not off:
        return s
    s = copy.deepcopy(s)
    for key in ("orbit", "glide"):
        for name in ("centre", "eye", "target"):
            v = s.get(key, {}).get(name)
            if v and isinstance(v[0], (int, float)):
                s[key][name] = [v[0] + off[0], v[1] + off[1], v[2] + off[2]]
            elif v:
                s[key][name] = [[p[0] + off[0], p[1] + off[1], p[2] + off[2]] for p in v]
    for c in s.get("crowd", []):
        c["line"] = [[x + off[0], y + off[1]] for x, y in c["line"]]
    return s


def write_paths(out):
    os.makedirs(out, exist_ok=True)
    for s in map(placed, chosen([])):
        keys = []
        n = int(s["seconds"] * 4)
        for k in range(n + 1):
            eye, target = camera(s, k / n)
            keys.append({"t": s["seconds"] * k / n, "eye": eye.round(1).tolist(), "target": target.round(1).tolist()})
        extra = {k: s[k] for k in ("ground", "land_tint", "tint_textures", "crowd", "populate") if k in s}
        extra["game_time"] = game_time(s["light"])
        extra["game_blend"] = 1.0 if extra["game_time"] == "day" else round(game_blend(s["light"]), 3)
        json.dump({"light": s["light"], "fov": FOV, "keys": keys, **extra}, open(os.path.join(out, s["name"] + ".json"), "w"), indent=1)
    print(f"{len(chosen([]))} paths -> {out}")


@contextmanager
def paused(pid):
    """The voice generator suspended (not stopped) while we use the GPU."""
    proc = None
    if pid:
        import psutil
        proc = psutil.Process(int(pid)); proc.suspend(); print(f"paused process {pid}")
    try:
        yield
    finally:
        if proc:
            proc.resume(); print(f"resumed process {pid}")


def run_render(out, shot, size, ss, only=None, dest=None):
    cmd = [RENDER_PY if os.path.exists(RENDER_PY) else sys.executable, "-I", RENDER, os.path.join(SCENES, shot["scene"]), "--path", os.path.join(out, shot["name"] + ".json"),
           "--out-dir", dest or os.path.join(out, shot["name"]), "--fps", str(FPS), "--size", f"{size[0]}x{size[1]}", "--ss", str(ss)]
    if only:
        cmd += ["--only", ",".join(map(str, only))]
    subprocess.run(cmd, check=True)


def free_commit_gb():
    import ctypes

    class MS(ctypes.Structure):
        _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong), ("ullTotalPhys", ctypes.c_ulonglong),
                    ("ullAvailPhys", ctypes.c_ulonglong), ("ullTotalPageFile", ctypes.c_ulonglong),
                    ("ullAvailPageFile", ctypes.c_ulonglong), ("ullTotalVirtual", ctypes.c_ulonglong),
                    ("ullAvailVirtual", ctypes.c_ulonglong), ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
    m = MS(); m.dwLength = ctypes.sizeof(MS)
    ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m))
    return m.ullAvailPageFile / 2 ** 30


def run_blender(out, shot, dest, only=None):
    blender = find_blender()
    if not blender:
        raise RuntimeError("Blender not found (TamrielMerge.Cli setup installs it)")
    cmd = [blender, "-b", "--factory-startup", "--python", BLEND_SCENE, "--", os.path.join(SCENES, shot["scene"]),
           os.path.join(out, shot["name"] + ".json"), dest, "--size", f"{SIZE[0]}x{SIZE[1]}", "--samples", str(RT_SAMPLES),
           "--fps", str(FPS)]
    if only:
        cmd += ["--only", ",".join(map(str, only))]
    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)
    import threading
    stopped = []

    peak = [0.0]

    def watch():
        import psutil
        while proc.poll() is None:
            try:
                peak[0] = max(peak[0], psutil.Process(proc.pid).memory_info().private / 2 ** 30)
            except Exception:
                pass
            if free_commit_gb() < 0.7:
                stopped.append(True); proc.kill(); return
            time.sleep(2)
    threading.Thread(target=watch, daemon=True).start()
    for line in proc.stdout:
        if any(k in line for k in ("frame ", "objects", "land:", "town ground", "crowd:", "populate:", "people:", "(no ", "Error", "rror:", "GPU at", "Traceback")):
            print("   ", line.rstrip(), flush=True)
    proc.wait()
    print(f"    Blender peak {peak[0]:.1f} GB", flush=True)
    if stopped:
        raise RuntimeError("Blender stopped: free memory under 0.7 GB")
    if proc.returncode:
        raise RuntimeError(f"Blender exited {proc.returncode}")


def rt_preview(out, names):
    for s in chosen(names):
        run_blender(out, s, os.path.join(out, "rt-preview", s["name"]), [int(s["seconds"] * FPS) // 2])


CHUNK = 150   # frames per Blender run: a whole shot (EEVEE holds steady, 5.8 GB at most; Cycles grew, 10 a run); the memory guard still stops it


def file_hash(*paths):
    import hashlib
    h = hashlib.sha1()
    for p in paths:
        if os.path.exists(p):
            with open(p, "rb") as f:
                h.update(f.read())
    return h.hexdigest()


def stamp_matches(folder, stamp):
    """Whether folder/stamp.json holds stamp: what the folder's contents were made from."""
    p = os.path.join(folder, "stamp.json")
    try:
        return json.load(open(p)) == stamp
    except (OSError, ValueError):
        return False


def write_stamp(folder, stamp):
    os.makedirs(folder, exist_ok=True)
    json.dump(stamp, open(os.path.join(folder, "stamp.json"), "w"), indent=1)


def folder_hash(folder, skip=("blend", "stamp.json")):
    """A hash of everything in an exported scene's folder (its scene.json, land, lighting, meshes and textures; not
    the prepared blend/ folder or the stamp), in name order."""
    import hashlib
    h = hashlib.sha1()
    for root, dirs, files in os.walk(folder):
        dirs[:] = sorted(d for d in dirs if os.path.relpath(os.path.join(root, d), folder) not in skip)
        for f in sorted(files):
            rel = os.path.relpath(os.path.join(root, f), folder)
            if rel in skip:
                continue
            h.update(rel.replace("\\", "/").encode())
            with open(os.path.join(root, f), "rb") as fh:
                for block in iter(lambda: fh.read(1 << 20), b""):
                    h.update(block)
    return h.hexdigest()


def scene_stamp(s):
    """What a shot's scene holds (the hash of its exported files, recorded by export), for prep and render: a scene
    exported again the same (a new conversion, a rebuilt tool, nothing seen changed) keeps its prepared textures and
    its frames (2026-10-15, user: the flyby "needs to be less time": every conversion re-rendered every shot, about an
    hour, because the plugins' size and time had changed)."""
    p = os.path.join(SCENES, s["scene"], "stamp.json")
    try:
        st = json.load(open(p))
        return st.get("content") or st
    except (OSError, ValueError):
        return None


def rt_render(out, names):
    for s in chosen(names):
        dest = os.path.join(out, "rt", s["name"])
        # Frames are made again when what they are made from changed (2026-10-14: a convert reused frames of older
        # scenes and scripts): the scene, the render and prep scripts, the shot's path and the ray-tracing switch.
        stamp = {"scene": scene_stamp(s), "scripts": file_hash(os.path.join(CODE, "scene3d", "blend_scene.py"), os.path.join(CODE, "scene3d", "blend_prep.py"),
                                                               os.path.join(out, s["name"] + ".json")),
                 "raytrace": os.environ.get("TM_GAME_RAYTRACE", "")}
        if not stamp_matches(dest, stamp):
            if os.path.isdir(dest):
                for f in os.listdir(dest):
                    if f.endswith(".png"):
                        os.remove(os.path.join(dest, f))
            write_stamp(dest, stamp)
        todo = [f for f in range(int(s["seconds"] * FPS)) if not os.path.exists(os.path.join(dest, f"{f:05d}.png"))]
        for i in range(0, len(todo), CHUNK):
            run_blender(out, s, dest, todo[i:i + CHUNK])


def export(cli, plugins, names, data=None):
    """The shots' scenes from the converted plugins (TamrielMerge.Cli scene-export): exported again when the plugins or
    the converter changed since (each scene's stamp.json: the plugins' sizes and times, the converter's, the shot's
    settings). The shots of games converted this time are listed in SCENES/selected.json: only they are rendered and
    packed (2026-10-14: shots of games not in this conversion came from scenes left by earlier ones)."""
    import shutil
    selected = []
    for s in [s for s in SHOTS if not names or s["name"] in names]:
        e, dest = s["export"], os.path.join(SCENES, s["scene"])
        plugin = os.path.join(data or "", e["plugin"]) if e.get("skyrim") else os.path.join(plugins, e["plugin"])
        if not os.path.exists(plugin):
            print(f"{s['name']}: {e['plugin']} not converted, shot left out"); continue
        files = [plugin] + [q for q in (os.path.join(plugins, a) for a in e.get("also", [])) if os.path.exists(q)]
        stamp = {"plugins": [[os.path.basename(f), os.path.getsize(f), int(os.path.getmtime(f))] for f in files],
                 "converter": int(os.path.getmtime(cli)) if os.path.exists(cli) else 0, "export": e}
        selected.append(s["name"])
        if os.path.exists(os.path.join(dest, "scene.json")):
            try:
                old = json.load(open(os.path.join(dest, "stamp.json")))
            except (OSError, ValueError):
                old = {}
            if {k: v for k, v in old.items() if k != "content"} == stamp and old.get("content"):
                continue
        if os.path.isdir(dest):
            shutil.rmtree(dest)
        cmd = [cli, "scene-export", "--plugins", ",".join(files), "--world", e["world"], "--centre", f"{e['centre'][0]},{e['centre'][1]}",
               "--radius", str(e["radius"]), "--out", dest] + (["--data", data] if data else []) \
            + (["--land-radius", str(e["land_radius"])] if "land_radius" in e else [])
        subprocess.run(cmd, check=True)
        stamp["content"] = folder_hash(dest)
        write_stamp(dest, stamp)
    os.makedirs(SCENES, exist_ok=True)
    json.dump(selected, open(os.path.join(SCENES, "selected.json"), "w"))


def prep(names):
    import shutil
    for s in chosen(names):
        d = os.path.join(SCENES, s["scene"])
        # The biggest scenes' textures smaller, to fit Cycles' memory beside everything else (Stros M'Kai, Whiterun with its city: 2026-10-08).
        size = "320" if s["scene"] in ("strosmkai", "whiterun") else "512"
        stamp = {"scene": scene_stamp(s), "prep": file_hash(os.path.join(CODE, "scene3d", "blend_prep.py")), "size": size}
        blend = os.path.join(d, "blend")
        if os.path.exists(os.path.join(d, "scene.json")) and not (os.path.exists(os.path.join(blend, "manifest.json")) and stamp_matches(blend, stamp)):
            if os.path.isdir(blend):
                shutil.rmtree(blend)
            subprocess.run([RENDER_PY, "-I", os.path.join(CODE, "scene3d", "blend_prep.py"), d, "--max", size], check=True)
            write_stamp(blend, stamp)


def exported(s):
    return os.path.exists(os.path.join(SCENES, s["scene"], "scene.json"))


def chosen(names):
    """The shots named (all, without names) among those exported for this conversion (SCENES/selected.json, written by
    export): the person's converted games'."""
    try:
        selected = set(json.load(open(os.path.join(SCENES, "selected.json"))))
    except (OSError, ValueError):
        selected = None
    return [s for s in SHOTS if (not names or s["name"] in names) and exported(s) and (selected is None or s["name"] in selected)]


def rendered(out):
    """The exported shots with frames (OUT/<FRAMES>/<shot>)."""
    return [s for s in chosen([]) if os.path.isdir(os.path.join(out, FRAMES, s["name"])) and shot_frames(out, s)]


def preview(out, names):
    dest = os.path.join(out, "preview")
    for s in chosen(names):
        n = int(s["seconds"] * FPS)
        frames = [0, n // 2, n - 1]
        tmp = os.path.join(dest, s["name"])
        run_render(out, s, (640, 360), 1, frames, tmp)
    print(f"previews -> {dest}")


def render(out, names):
    for s in chosen(names):
        run_render(out, s, SIZE, 2)


# ---- packing ----

def game_time(light):
    """The game's time of day for a shot's sun: day when it stands high, else sunrise in the east, sunset in the west
    (azimuth from +Y toward +X)."""
    if light.get("elevation", 30) >= 25:
        return "day"
    return "sunrise" if 0 < light.get("azimuth", 0) % 360 < 180 else "sunset"


def game_blend(light):
    """How far a shot's time of day is from day to its sunrise or sunset: the game moves from its day colours to its
    sunset's only in the last stretch before the sun meets the horizon, so a sun 10 degrees up or more is all day, one on
    the horizon all sunset (user, 2026-10-11: "fix the sunsets" - at 11 to 22 degrees the shots had taken the sunset's
    colours whole: red skies; 2026-10-12, "vivecs sky again": blended from 25 degrees, Vivec's (11) was still half
    sunset, a pink sky)."""
    return min(max((10.0 - float(light.get("elevation", 30))) / 10.0, 0.0), 1.0)


def blend(a, b, w):
    """a and b mixed, w of the way to b: numbers and lists of them in proportion, dictionaries key by key, anything
    else the nearer one's."""
    if a is None or w >= 1:
        return b
    if b is None or w <= 0:
        return a
    if isinstance(a, dict) and isinstance(b, dict):
        return {k: blend(a.get(k), b.get(k), w) for k in {**a, **b}}
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool):
        return a + (b - a) * w
    if isinstance(a, list) and isinstance(b, list) and len(a) == len(b) and all(isinstance(x, (int, float)) for x in a + b):
        return [x + (y - x) * w for x, y in zip(a, b)]
    return b if w >= 0.5 else a


def game_look(s):
    """The image space of a shot's place at its time of day (scene lighting.json, from the game), or None: between
    the day's and the sunrise's or sunset's by the sun's height (game_blend)."""
    p = os.path.join(SCENES, s["scene"], "lighting.json")
    if not os.path.exists(p):
        return None
    L = json.load(open(p))
    t = game_time(s["light"])
    return blend((L.get("day") or {}).get("grade"), (L.get(t) or {}).get("grade"), 1.0 if t == "day" else game_blend(s["light"]))


def game_grade(img, g):
    """The game's own grade (its image space's cinematic settings): saturation round the luminance, the tint mixed in
    by its amount, then brightness and contrast."""
    a = np.asarray(img).astype(np.float32) / 255.0
    lum = (a @ np.array([0.2125, 0.7154, 0.0721], np.float32))[..., None]
    a = lum + (a - lum) * float(g.get("saturation") or 1.0)
    if g.get("tint"):
        a = a * (1 - float(g.get("tint_amount") or 0)) + lum * np.array(g["tint"], np.float32) * float(g.get("tint_amount") or 0)
    a = a * float(g.get("brightness") or 1.0)
    # Contrast round the picture's average brightness, as the game's image space does, not round mid-grey (user,
    # 2026-10-11: "fix the sunsets" - round 0.5 the dusk grade's 1.5 crushed the sunset sky to near black and red).
    mean = float((a @ np.array([0.2125, 0.7154, 0.0721], np.float32)).mean())
    a = (a - mean) * float(g.get("contrast") or 1.0) + mean
    return np.clip(a, 0, 1)


def grade(img):
    """Film look: a touch of bloom, warm highlights over slightly cool shadows, a soft vignette."""
    a = np.asarray(img).astype(np.float32) / 255.0
    lum = a @ np.array([0.299, 0.587, 0.114], np.float32)
    bright = np.clip((a - 0.8) / 0.2, 0, 1) * a
    glow = np.asarray(Image.fromarray((bright * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(14))).astype(np.float32) / 255.0
    a = a + glow * 0.22
    warm = np.array([1.04, 1.0, 0.93], np.float32); cool = np.array([0.95, 1.0, 1.06], np.float32)
    t = np.clip(lum, 0, 1)[..., None]
    a = a * (cool * (1 - t) + warm * t)
    a = (a - 0.5) * 1.06 + 0.5                       # a little contrast
    h, w = lum.shape
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    r = np.sqrt(((x - w / 2) / (w / 2)) ** 2 + ((y - h / 2) / (h / 2)) ** 2)
    a = a * (1 - 0.32 * np.clip(r - 0.55, 0, 1) ** 1.5)[..., None]
    return np.clip(a, 0, 1)


def caption(a, text, alpha):
    """The place and its game, lower left (the menu's own items are on the right), fading with alpha."""
    if alpha <= 0:
        return a
    h, w = a.shape[:2]
    layer = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(layer)
    big = ImageFont.truetype(CAPTION_FONT, int(h * 0.052)); small = ImageFont.truetype(CAPTION_FONT, int(h * 0.026))
    x, y = int(w * 0.055), int(h * 0.74)
    d.text((x, y), text[0].upper(), font=big, fill=255)
    d.text((x + 2, y + int(h * 0.066)), text[1].upper(), font=small, fill=200)
    m = np.asarray(layer).astype(np.float32) / 255.0 * alpha
    shadow = np.asarray(layer.filter(ImageFilter.GaussianBlur(4))).astype(np.float32) / 255.0 * alpha
    a = a * (1 - 0.55 * shadow[..., None])
    return a * (1 - m[..., None]) + np.array([0.96, 0.92, 0.84], np.float32) * m[..., None]


FRAMES = ""     # the frames' folder under OUT ("" = the raster renders, "rt" = the path-traced ones)
EMBLEM = None   # the red emblem (RGBA PNG, --emblem), on the banner the menu shows on the video's left


def banner_layer(w, h):
    """The banner (user, 2026-10-08: "put the logo in the video", "a black with red lining banner behind the logo",
    "the logo will be on the left side"): black cloth hanging from the top edge on the left, edged in red with a thin
    red line inside, a swallowtail end above the captions, the red emblem on it, a soft shadow on the picture behind.
    The game's own 3D emblem doesn't draw with the video in the logo mesh, so the menu shows this instead, as a layer
    of its own in front of the video (not in the video's frames: their DXT1 compression bled the reds).
    w, h: the picture's size (the banner is laid out in its proportions, any resolution).
    Returns (colour, alpha, shadow), float arrays."""
    ss = 4                                          # drawn 4x and scaled down, for smooth edges
    k = w / 1200.0                                  # sizes below are for a 1200-wide picture
    W, H = w * ss, h * ss
    cx, bw = 0.2 * w, 0.2 * w
    x0, x1, top, bot, notch = cx - bw / 2, cx + bw / 2, -10.0 * k, 0.66 * h, 0.065 * h

    def shape(inset):
        return [((x0 + inset) * ss, (top) * ss), ((x1 - inset) * ss, (top) * ss),
                ((x1 - inset) * ss, (bot - inset * 0.6) * ss), (cx * ss, (bot - notch - inset * 1.5) * ss),
                ((x0 + inset) * ss, (bot - inset * 0.6) * ss)]

    def mask(points):
        m = Image.new("L", (W, H), 0)
        ImageDraw.Draw(m).polygon(points, fill=255)
        return np.asarray(m.resize((w, h), Image.LANCZOS)).astype(np.float32) / 255.0

    outer, edge, line_out, line_in = mask(shape(0)), mask(shape(4.5 * k)), mask(shape(9.5 * k)), mask(shape(11.0 * k))
    red_edge, red_line = np.clip(outer - edge, 0, 1), np.clip(line_out - line_in, 0, 1)
    # Black cloth: darker towards its edges, a few soft vertical folds.
    x = (np.arange(w, dtype=np.float32) - cx) / (bw / 2)
    folds = 0.022 + 0.016 * np.cos(np.clip(x, -1, 1) * math.pi / 2) + 0.008 * np.sin(x * 9.0)
    colour = np.zeros((h, w, 3), np.float32) + folds[None, :, None]
    alpha = outer * 0.92
    colour = colour * (1 - red_edge[..., None]) + np.array([0.52, 0.05, 0.05], np.float32) * red_edge[..., None]
    colour = colour * (1 - red_line[..., None]) + np.array([0.62, 0.09, 0.08], np.float32) * red_line[..., None]
    alpha = np.maximum(alpha, red_edge)
    if EMBLEM:
        e = Image.open(EMBLEM).convert("RGBA")
        eh = int(0.46 * h); ew = int(e.width * eh / e.height)
        e = np.asarray(e.resize((ew, eh), Image.LANCZOS)).astype(np.float32) / 255.0
        ex, ey = int(cx - ew / 2), int(0.31 * h - eh / 2)
        ea = e[..., 3:4]
        region = colour[ey:ey + eh, ex:ex + ew]
        colour[ey:ey + eh, ex:ex + ew] = region * (1 - ea) + e[..., :3] * ea
        alpha[ey:ey + eh, ex:ex + ew] = np.maximum(alpha[ey:ey + eh, ex:ex + ew], ea[..., 0])
    # Its shadow on the picture, a little down and right.
    sh = Image.fromarray((outer * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(10 * k))
    shadow = np.roll(np.roll(np.asarray(sh).astype(np.float32) / 255.0, int(6 * k), axis=0), int(5 * k), axis=1) * 0.45
    return colour, alpha, shadow


def write_banner(menu_dir, width=2400):
    """The banner as the menu's own layer: MENU_DIR/banner.dds (uncompressed RGBA, the shadow in it as dark
    see-through) laid out on a picture `width` wide (16:9, whatever the video's size), and banner.json with its place
    in the video's frame (0..1)."""
    w, h = width, width * 9 // 16
    colour, alpha, shadow = banner_layer(w, h)
    a = np.maximum(alpha, shadow * (1 - alpha))
    rgb = colour * (alpha / np.maximum(a, 1e-4))[..., None]
    ys, xs = np.nonzero(a > 1 / 255)
    x0, x1 = 0, int(xs.max()) + 1
    y0, y1 = 0, int(ys.max()) + 1
    x1 = min(w, x0 + (x1 - x0 + 3) // 4 * 4); y1 = min(h, y0 + (y1 - y0 + 3) // 4 * 4)
    img = np.dstack([np.clip(rgb, 0, 1), a])[y0:y1, x0:x1]
    Image.fromarray((img * 255 + 0.5).astype(np.uint8), "RGBA").save(os.path.join(menu_dir, "banner.dds"))
    json.dump({"u0": x0 / w, "v0": y0 / h, "u1": x1 / w, "v1": y1 / h, "width": x1 - x0, "height": y1 - y0},
              open(os.path.join(menu_dir, "banner.json"), "w"), indent=1)
    print(f"banner {x1 - x0}x{y1 - y0} -> {menu_dir}")


def shot_frames(out, s):
    d = os.path.join(out, FRAMES, s["name"])
    return [os.path.join(d, f) for f in sorted(os.listdir(d)) if f.endswith(".png")]


def finished(out, s, k, n):
    """Frame k of n of shot s, graded and captioned (caption in from 25% to 85% of the shot)."""
    look = game_look(s)
    img = game_grade(Image.open(shot_frames(out, s)[k]).convert("RGB"), look) if look else grade(Image.open(shot_frames(out, s)[k]).convert("RGB"))
    u = k / max(n - 1, 1)
    alpha = min(np.clip((u - 0.18) / 0.12, 0, 1), np.clip((0.86 - u) / 0.12, 0, 1))
    return caption(img, s["caption"], float(alpha))


def upscale_frames(out, size):
    """Each shot's frames (OUT/<FRAMES>/<shot>) upscaled to size with Real-ESRGAN (general-x4v3; user, 2026-10-08: "the video
    needs upscaled", 1440p chosen over 4K for the menu's memory: 520 frames at 4K would be 2.2 GB of textures)."""
    sys.path.insert(0, os.path.join(CODE, "video"))
    import remaster
    for s in rendered(out):
        frames = shot_frames(out, s)
        tmp = os.path.join(out, f"{FRAMES}-in", s["name"])
        dest = os.path.join(out, f"{FRAMES}{size[1]}", s["name"])
        os.makedirs(tmp, exist_ok=True)
        for i, f in enumerate(frames, 1):
            # Fresh links each time, and an upscale older than its frame redone (the shot re-rendered since).
            link = os.path.join(tmp, f"frame{i:05d}.png")
            if os.path.exists(link):
                os.remove(link)
            try:
                os.link(f, link)
            except OSError:
                with open(f, "rb") as a, open(link, "wb") as b:
                    b.write(a.read())
            up = os.path.join(dest, f"frame{i:05d}.png")
            if os.path.exists(up) and os.path.getmtime(up) < os.path.getmtime(f):
                os.remove(up)
        print(f"{s['name']}: {len(frames)} frames -> {dest}", flush=True)
        remaster.cool_down()
        # The compact model: these clean renders need no heavy restoration, and x4plus takes about 30 s a frame here.
        remaster.upscale(tmp, dest, size, model_path=os.path.join(remaster.TOOLS, "upscale", "realesr-general-x4v3.pth"))


def pack(out, menu_dir):
    clips = [(s, len(shot_frames(out, s))) for s in rendered(out)]
    if not clips:
        raise SystemExit("no shots rendered")
    seq = []                                    # ((shot, k), (shot, k) or None, mix)
    for i, (s, n) in enumerate(clips):
        nxt, _ = clips[(i + 1) % len(clips)]
        for k in range(FADE, n - FADE):
            seq.append(((s, k, n), None, 0.0))
        for k in range(FADE):
            t = (k + 1) / (FADE + 1)
            seq.append(((s, n - FADE + k, n), (nxt, k, clips[(i + 1) % len(clips)][1]), t * t * (3 - 2 * t)))
    fw, fh = SIZE
    # As many whole frames as fit in 4096 each way, the sheet trimmed to them (whole 4-pixel blocks for DXT1).
    cols, rows = max(1, 4096 // fw), max(1, 4096 // fh)
    per = cols * rows
    sheet_w, sheet_h = (fw * cols + 3) // 4 * 4, (fh * rows + 3) // 4 * 4
    os.makedirs(menu_dir, exist_ok=True)
    sheets = (len(seq) + per - 1) // per
    contact = []
    for sh in range(sheets):
        sheet = Image.new("RGB", (sheet_w, sheet_h))
        for j, (a, b, m) in enumerate(seq[sh * per:(sh + 1) * per]):
            img = finished(out, *a)
            if b:
                img = img * (1 - m) + finished(out, *b) * m
            frame = Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8))
            sheet.paste(frame, ((j % cols) * fw, (j // cols) * fh))
            if (sh * per + j) % max(1, len(seq) // 16) == 0:
                contact.append(frame.resize((300, 169)))
        sheet.save(os.path.join(menu_dir, f"flyby_{sh:02d}.dds"), pixel_format="DXT1")
        print(f"  sheet {sh + 1}/{sheets}", flush=True)
    for old in range(sheets, 100):
        p = os.path.join(menu_dir, f"flyby_{old:02d}.dds")
        if os.path.exists(p):
            os.remove(p)
    json.dump({"frames": len(seq), "fps": FPS, "cols": cols, "rows": rows, "sheetWidth": sheet_w, "sheetHeight": sheet_h,
               "frameWidth": fw, "frameHeight": fh, "sheets": sheets, "towns": [s["name"] for s, _ in clips]},
              open(os.path.join(menu_dir, "flyby.json"), "w"), indent=1)
    sheet = Image.new("RGB", (1200, 169 * ((len(contact) + 3) // 4)))
    contact = [c.resize((300, 169)) for c in contact]
    for i, t in enumerate(contact):
        sheet.paste(t, ((i % 4) * 300, (i // 4) * 169))
    sheet.save(os.path.join(menu_dir, "flyby.png"))
    print(f"{len(seq)} frames ({len(seq) / FPS:.1f} s at {FPS} fps) in {sheets} sheets of {cols}x{rows}")


def main():
    args = sys.argv[1:]
    pid = None
    if "--pause" in args:
        i = args.index("--pause"); pid = args[i + 1]; del args[i:i + 2]
    cmd, out = args[0], os.path.abspath(args[1])
    if cmd == "paths":
        write_paths(out)
    elif cmd == "preview":
        write_paths(out)
        with paused(pid):
            preview(out, args[2:])
    elif cmd == "render":
        write_paths(out)
        with paused(pid):
            render(out, args[2:])
    elif cmd == "export":
        opt = {args[i][2:]: args[i + 1] for i in range(2, len(args) - 1, 2) if args[i].startswith("--")}
        export(opt["cli"], opt["plugins"], [], opt.get("data"))
    elif cmd == "prep":
        prep(args[2:])
    elif cmd in ("rt-preview", "rt-render"):
        write_paths(out)
        with paused(pid):
            (rt_preview if cmd == "rt-preview" else rt_render)(out, args[2:])
    elif cmd in ("pack", "upscale"):
        global FRAMES, EMBLEM, SIZE
        if "--frames" in args:
            i = args.index("--frames"); FRAMES = args[i + 1]; del args[i:i + 2]
        if "--size" in args:
            i = args.index("--size"); SIZE = tuple(int(x) for x in args[i + 1].split("x")); del args[i:i + 2]
        if cmd == "upscale":
            upscale_frames(out, SIZE)
            return
        if "--emblem" in args:
            i = args.index("--emblem"); EMBLEM = os.path.abspath(args[i + 1]); del args[i:i + 2]
        pack(out, os.path.abspath(args[2]))
        if EMBLEM:
            write_banner(os.path.abspath(args[2]))
    elif cmd == "banner":
        i = args.index("--emblem"); EMBLEM = os.path.abspath(args[i + 1]); del args[i:i + 2]
        write_banner(os.path.abspath(args[2]))


if __name__ == "__main__":
    main()
