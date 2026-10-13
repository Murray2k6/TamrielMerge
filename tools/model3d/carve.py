"""Carves TripoSR's solid with the sprite before it becomes a mesh (TamrielMerge): TripoSR guesses a shape from one view
and fills in what it can't see, joining a staff to the body with a web, smoothing a crown's notches into a cone and
making thin poles flat fins. Two cuts put back what the sprite does show:

- outline: every point of the solid that falls outside the sprite seen through TripoSR's input camera is removed, so
  gaps the sprite has (between a staff and a robe, a crown's notches) go through the model;
- thin parts are round: where the sprite is narrow (a pole, a leg, a chain; up to THIN_PX pixels across) the solid is
  kept only within about that width of its middle front to back, so it is a rod, not a fin.

Foliage (cut out by its texture's alpha) is only cut on its outline's large gaps, so a canopy doesn't get tunnels
through it wherever the sprite shows sky between leaves.

usage: from carve import carved_mesh
"""
import os
import sys

import numpy as np
import torch
import trimesh
from scipy import ndimage

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "TripoSR"))
from tsr.utils import scale_tensor  # noqa: E402
from reproject import CAMERA_DISTANCE, FOVY_DEG, INPUT_SIZE  # noqa: E402

THRESHOLD = 25.0     # TripoSR's own density level for the surface
THIN_PX = 12         # parts up to twice this wide (in TripoSR's 512 px input) are made round
ROUNDNESS = 1.25     # how much deeper than wide a thin part may be
SMOOTH_VOXELS = 1.0  # the solid blurred this much (Gaussian sigma, in grid voxels) before it becomes a mesh
QUERY_CHUNK = 262144  # points per density query (~0.5 GB of GPU memory)
THIN_VOXELS = 30     # a thin piece the blur would erase is kept unblurred when it has at least this many voxels


def disk(r):
    y, x = np.ogrid[-r:r + 1, -r:r + 1]
    return x * x + y * y <= r * r


def thickness(mask):
    """Per pixel, the radius (in pixels, up to THIN_PX) of the widest disc inside the sprite that covers it; THIN_PX
    where the sprite is wider than that."""
    t = np.zeros(mask.shape, np.int32)
    inner = ndimage.distance_transform_edt(mask)
    for r in range(1, THIN_PX + 1):
        # The opening by a disc of radius r, by distances: centres at least r inside, grown back by r.
        centres = inner >= r
        if not centres.any():
            break
        t[(ndimage.distance_transform_edt(~centres) <= r) & mask] = r
    return t


def camera_projection(points):
    """Points (x towards the camera, y image right, z up) to TripoSR input pixels, and the pixel size at their depth."""
    focal = 0.5 * INPUT_SIZE / np.tan(np.radians(FOVY_DEG) / 2)
    depth = CAMERA_DISTANCE - points[:, 0]
    return INPUT_SIZE / 2 + focal * points[:, 1] / depth, INPUT_SIZE / 2 - focal * points[:, 2] / depth, depth / focal


def carved_mesh(model, code, resolution, mask, cutout=False, blur=SMOOTH_VOXELS):
    """TripoSR's mesh for one scene code, carved by the sprite's mask (sprite_mask), its surface smoothed by blurring
    the solid by `blur` voxels before it becomes a mesh."""
    d = carved_density(model, code, resolution, mask, cutout)
    if blur > 0:
        grid = d.reshape(resolution, resolution, resolution)
        smooth = ndimage.gaussian_filter(grid, blur)
        # Parts too thin to live through the blur (a spider statue's legs, a cage's bars, a frond's tip: a few voxels
        # across, 2026-10-12) are kept as they were: solid that an opening removes, in pieces big enough not to be
        # a bump of noise on a surface.
        solid = grid > THRESHOLD
        thin = solid & ~ndimage.binary_opening(solid, iterations=2)
        labels, count = ndimage.label(thin, np.ones((3, 3, 3), bool))
        if count:
            sizes = np.bincount(labels.reshape(-1))
            keep = (sizes >= THIN_VOXELS)[labels] & (labels > 0)
            smooth[keep] = np.maximum(smooth[keep], grid[keep])
        d = smooth.reshape(-1)
    return level_mesh(model, d, code.device)


def level_mesh(model, d, device):
    """The surface of a density grid at TripoSR's level."""
    helper = model.isosurface_helper
    radius = model.renderer.cfg.radius
    v_pos, t_pos_idx = helper(-(torch.from_numpy(np.ascontiguousarray(d, np.float32)).to(device) - THRESHOLD))
    v_pos = scale_tensor(v_pos, helper.points_range, (-radius, radius))
    return trimesh.Trimesh(vertices=v_pos.cpu().numpy(), faces=t_pos_idx.cpu().numpy())


def carved_density(model, code, resolution, mask, cutout=False):
    """TripoSR's density on its marching cubes grid (x-major), carved by the sprite's mask."""
    model.set_marching_cubes_resolution(resolution)
    helper = model.isosurface_helper
    radius = model.renderer.cfg.radius
    grid = scale_tensor(helper.grid_vertices.to(code.device), helper.points_range, (-radius, radius))
    # In big chunks: the 7 million points in sprite2mesh's 4096 took 1728 trips through Python (2 s a model).
    chunk = model.renderer.chunk_size
    model.renderer.set_chunk_size(QUERY_CHUNK)
    try:
        with torch.no_grad():
            density = model.renderer.query_triplane(model.decoder, grid, code)["density_act"].reshape(-1)
    finally:
        model.renderer.set_chunk_size(chunk)
    d = density.float().cpu().numpy()
    pts = grid.cpu().numpy()
    occupied = d > THRESHOLD
    ys, xs = np.nonzero(mask)
    if occupied.any() and len(xs):
        px, py, unit = camera_projection(pts)
        # TripoSR's solid comes out a few percent small: fit its outline to the sprite's (as reproject paints it).
        def fit(p, lo, hi):
            a, b = p[occupied].min(), p[occupied].max()
            s = (hi + 1 - lo) / max(b - a, 1e-6)
            return lo + (p - a) * s, s
        px, sx = fit(px, xs.min(), xs.max())
        py, sy = fit(py, ys.min(), ys.max())
        unit = unit / ((sx + sy) / 2)   # world size of one sprite pixel at each point
        keep = ndimage.binary_closing(mask, disk(4)) | mask if cutout else ndimage.binary_dilation(mask, disk(1))
        ix, iy = np.round(px).astype(np.int64), np.round(py).astype(np.int64)
        on = (ix >= 0) & (ix < INPUT_SIZE) & (iy >= 0) & (iy < INPUT_SIZE)
        inside = np.zeros(len(d), bool)
        inside[on] = keep[iy[on], ix[on]]
        d[~inside] = 0
        if not cutout:
            # Thin parts round: within ROUNDNESS x their half-width of the middle of what is solid along the same line
            # of sight (a column of the grid, straight along x; the camera's perspective is mild at this distance).
            t = np.full(len(d), THIN_PX)
            t[on] = thickness(mask)[iy[on], ix[on]]
            solid = d > THRESHOLD
            n = resolution
            column = np.arange(len(d)) % (n * n)            # the grid is x-major: index = (ix * n + iy) * n + iz
            count = np.bincount(column[solid], minlength=n * n)
            centre = np.bincount(column[solid], weights=pts[solid, 0], minlength=n * n) / np.maximum(count, 1)
            thin = t < THIN_PX
            too_deep = thin & (np.abs(pts[:, 0] - centre[column]) > ROUNDNESS * np.maximum(t, 1) * unit)
            d[too_deep] = 0
    return d
