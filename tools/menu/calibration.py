"""Menu camera calibration (docs/menu.md): Skyrim places the main menu's camera in code, so to fit the flyby to the
screen we photograph it. `make` writes an atlas of ArUco markers and calibration.json, the markers' quads on the six
faces of a box around the emblem (positions relative to the emblem's middle); `menu-calibrate` puts them in the logo
meshes. `solve` finds the markers in a screenshot of the menu and solves the camera's position, direction and field of
view (menu_camera.json), which menu-build then uses to place the flyby screen-filling.

  python calibration.py make OUTDIR
  python calibration.py solve OUTDIR screenshot.png [more screenshots...]
"""
import json
import math
import os
import struct
import sys

import cv2
import numpy as np

DICT = cv2.aruco.DICT_6X6_250
CELL = 128          # atlas cell, pixels
GRID = 16           # atlas cells per side (256 markers)
HALF = 1200.0       # box half-size around the emblem, game units (the old flyby sphere, radius 1500, was in view)
PER_FACE = 6        # markers per face edge (36 per face)


def write_dds_rgba(path, rgba):
    h, w = rgba.shape[:2]
    hdr = struct.pack("<4sIIIIIII44sIIIIIIIIIIIII", b"DDS ", 124, 0x1 | 0x2 | 0x4 | 0x1000 | 0x8, h, w, w * 4, 0, 1, b"\0" * 44,
                      32, 0x41, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, 0x1000, 0, 0, 0, 0)
    bgra = rgba[..., [2, 1, 0, 3]]
    with open(path, "wb") as f:
        f.write(hdr)
        f.write(np.ascontiguousarray(bgra).tobytes())


def faces():
    """Each face: (name, centre, u axis, v axis); u and v span the face, seen from inside the box."""
    H = HALF
    return [
        ("front", (0, H, 0), (1, 0, 0), (0, 0, 1)),
        ("back", (0, -H, 0), (-1, 0, 0), (0, 0, 1)),
        ("right", (H, 0, 0), (0, -1, 0), (0, 0, 1)),
        ("left", (-H, 0, 0), (0, 1, 0), (0, 0, 1)),
        ("top", (0, 0, H), (1, 0, 0), (0, -1, 0)),
        ("bottom", (0, 0, -H), (1, 0, 0), (0, 1, 0)),
    ]


def make(out):
    os.makedirs(out, exist_ok=True)
    dictionary = cv2.aruco.getPredefinedDictionary(DICT)
    atlas = np.full((GRID * CELL, GRID * CELL, 4), 255, np.uint8)
    quads = []
    mid = 0
    side = 2 * HALF / PER_FACE
    size = side * 0.8                 # the quad (marker plus its white margin) on the face
    for name, c, u, v in faces():
        c, u, v = np.array(c, float), np.array(u, float), np.array(v, float)
        for j in range(PER_FACE):
            for i in range(PER_FACE):
                centre = c + u * ((i + 0.5) * side - HALF) + v * ((j + 0.5) * side - HALF)
                ax, ay = mid % GRID, mid // GRID
                img = cv2.aruco.generateImageMarker(dictionary, mid, CELL - 32)
                cell = np.full((CELL, CELL), 255, np.uint8)
                cell[16:CELL - 16, 16:CELL - 16] = img
                atlas[ay * CELL:(ay + 1) * CELL, ax * CELL:(ax + 1) * CELL, :3] = cell[..., None]
                # Quad corners (game units, relative to the emblem) and their UVs; the marker itself is the inner
                # (CELL-64)/CELL of the quad, corners in ArUco's order: top-left, top-right, bottom-right, bottom-left.
                h = size / 2
                corners = [centre - u * h + v * h, centre + u * h + v * h, centre + u * h - v * h, centre - u * h - v * h]
                u0, v0, u1, v1 = ax / GRID, ay / GRID, (ax + 1) / GRID, (ay + 1) / GRID
                k = (CELL - 32) / CELL
                inner = [centre + (p - centre) * k for p in corners]
                quads.append({"id": mid, "face": name, "corners": [list(map(float, p)) for p in corners],
                              "uvs": [[u0, v0], [u1, v0], [u1, v1], [u0, v1]],
                              "marker": [list(map(float, p)) for p in inner]})
                mid += 1
    write_dds_rgba(os.path.join(out, "calibration.dds"), atlas)
    json.dump({"half": HALF, "quads": quads}, open(os.path.join(out, "calibration.json"), "w"), indent=1)
    print(f"{len(quads)} markers -> {out}")


def make_corridor(out):
    """The second shot, from what the first showed (2026-10-08): the camera looks along +Y and sees, on the plane 1200
    beyond the emblem, x -248..300 and z -317..-8. A dense grid fills that plane, and small panels nearer the camera
    (all inside the view whatever its depth) give the depth and the field of view; the emblem stays full size so its
    place on screen shows too."""
    os.makedirs(out, exist_ok=True)
    dictionary = cv2.aruco.getPredefinedDictionary(DICT)
    atlas = np.full((GRID * CELL, GRID * CELL, 4), 255, np.uint8)
    quads = []
    u, v = np.array([1.0, 0, 0]), np.array([0, 0, 1.0])

    def panel(centre, size, name):
        mid = len(quads)
        ax, ay = mid % GRID, mid // GRID
        cell = np.full((CELL, CELL), 255, np.uint8)
        cell[16:CELL - 16, 16:CELL - 16] = cv2.aruco.generateImageMarker(dictionary, mid, CELL - 32)
        atlas[ay * CELL:(ay + 1) * CELL, ax * CELL:(ax + 1) * CELL, :3] = cell[..., None]
        centre = np.array(centre, float); h = size / 2
        corners = [centre - u * h + v * h, centre + u * h + v * h, centre + u * h - v * h, centre - u * h - v * h]
        k = (CELL - 32) / CELL
        quads.append({"id": mid, "face": name, "corners": [list(map(float, p)) for p in corners],
                      "uvs": [[ax / GRID, ay / GRID], [(ax + 1) / GRID, ay / GRID], [(ax + 1) / GRID, (ay + 1) / GRID], [ax / GRID, (ay + 1) / GRID]],
                      "marker": [list(map(float, centre + (p - centre) * k)) for p in corners]})

    for z in np.arange(-380, 61, 90):
        for x in np.arange(-330, 381, 90):
            panel((x, 1200, z), 72, "wall")
    cx, cz = 26, -162
    for y, off, size in ((700, 80, 50), (300, 55, 36), (-300, 0, 24)):
        for dx in ((-off, off) if off else (0,)):
            panel((cx + dx, y, cz - 40), size, f"y{y}")
    write_dds_rgba(os.path.join(out, "calibration.dds"), atlas)
    json.dump({"shrink": False, "quads": quads}, open(os.path.join(out, "calibration.json"), "w"), indent=1)
    print(f"{len(quads)} markers -> {out}")


def solve(out, shots):
    cal = json.load(open(os.path.join(out, "calibration.json")))
    centre = np.array(json.load(open(os.path.join(out, "centre.json")))["centre"], float)
    by_id = {q["id"]: q for q in cal["quads"]}
    detector = cv2.aruco.ArucoDetector(cv2.aruco.getPredefinedDictionary(DICT), cv2.aruco.DetectorParameters())
    results = []
    for shot in shots:
        img = cv2.imread(shot)
        h, w = img.shape[:2]
        corners, ids, _ = detector.detectMarkers(cv2.cvtColor(img, cv2.COLOR_BGR2GRAY))
        if ids is None or len(ids) < 2:
            print(f"{shot}: {0 if ids is None else len(ids)} markers found, not enough"); continue
        obj, pix = [], []
        for c, i in zip(corners, ids.flatten()):
            if int(i) not in by_id: continue
            for p3, p2 in zip(by_id[int(i)]["marker"], c.reshape(4, 2)):
                obj.append(centre + np.array(p3)); pix.append(p2)
        obj = np.array(obj, np.float64); pix = np.array(pix, np.float64)
        # Square pixels, centred principal point, no distortion: only the focal length is unknown. Search it, solving
        # the pose at each, and keep the least reprojection error.
        best = None
        for fov in np.concatenate([np.arange(2, 30, 0.02), np.arange(30, 130, 0.25)]):
            f = (w / 2) / math.tan(math.radians(fov) / 2)
            K = np.array([[f, 0, w / 2], [0, f, h / 2], [0, 0, 1]])
            ok, rvec, tvec = cv2.solvePnP(obj, pix, K, None, flags=cv2.SOLVEPNP_ITERATIVE)
            if not ok: continue
            proj, _ = cv2.projectPoints(obj, rvec, tvec, K, None)
            err = float(np.sqrt(np.mean(np.sum((proj.reshape(-1, 2) - pix) ** 2, axis=1))))
            if best is None or err < best[0]: best = (err, fov, rvec, tvec)
        err, fov, rvec, tvec = best
        R, _ = cv2.Rodrigues(rvec)
        eye = (-R.T @ tvec).flatten()
        forward = R.T @ np.array([0, 0, 1.0]); up = -(R.T @ np.array([0, 1.0, 0]))
        r = {"shot": shot, "markers": int(len(ids)), "rms_px": err, "hfov": float(fov),
             "vfov": float(math.degrees(2 * math.atan(math.tan(math.radians(fov) / 2) * h / w))),
             "eye": eye.tolist(), "forward": forward.tolist(), "up": up.tolist(), "width": w, "height": h}
        print(json.dumps(r))
        results.append(r)
    if results:
        json.dump(results, open(os.path.join(out, "menu_camera.json"), "w"), indent=1)


if __name__ == "__main__":
    if sys.argv[1] == "make": make(sys.argv[2])
    elif sys.argv[1] == "corridor": make_corridor(sys.argv[2])
    else: solve(sys.argv[2], sys.argv[3:])
