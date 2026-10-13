"""Smooths a made model's surface (Taubin: removes marching cubes' terracing and the facets left by simplification
without shrinking the shape). The mesh is welded by position first so texture seams (where xatlas split vertices)
move together and don't tear; UVs and faces are kept, so the texture still fits.

usage: python smooth.py MODEL_DIR [iterations]     (rewrites mesh.obj; the unsmoothed one is kept as mesh_raw.obj)
       or: from smooth import smooth_mesh
"""
import os
import shutil
import sys

import numpy as np
import trimesh


def smooth_mesh(mesh, iterations=12):
    """A trimesh mesh (possibly with seam-split vertices) smoothed in place; returns it."""
    v = np.asarray(mesh.vertices, float)
    key = np.round(v / 1e-5).astype(np.int64)
    _, weld, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inverse = inverse.reshape(-1)
    welded = trimesh.Trimesh(vertices=v[weld], faces=inverse[np.asarray(mesh.faces)], process=False)
    trimesh.smoothing.filter_taubin(welded, lamb=0.5, nu=-0.53, iterations=iterations)
    mesh.vertices = np.asarray(welded.vertices)[inverse]
    return mesh


def main():
    d = sys.argv[1]
    iterations = int(sys.argv[2]) if len(sys.argv) > 2 else 12
    path, raw = os.path.join(d, "mesh.obj"), os.path.join(d, "mesh_raw.obj")
    if not os.path.exists(raw):
        shutil.copyfile(path, raw)
    mesh = trimesh.load(raw, process=False)
    smooth_mesh(mesh, iterations)
    mesh.export(path)
    print("smoothed", d, len(mesh.vertices), "vertices")


if __name__ == "__main__":
    main()
