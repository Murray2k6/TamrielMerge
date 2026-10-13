"""Paints a made model with its own sprite (TamrielMerge): TripoSR's baked colours invent shading for the sides it
cannot see (dark streaks and glints that read as polished metal). Instead every texel's point on the model is projected
through TripoSR's input camera (1.9 units out along +x, looking at the centre, 40 degrees field of view, image right =
+y, up = +z) onto the sprite, and takes the sprite's colour there: the front shows the sprite, the back its mirror, all
matte, with Daggerfall's own colours. Pixels just off the sprite's edge take the nearest sprite colour.

Pale colours can be whitened per sprite (Daggerfall's palette is dim; a seagull is white). Rewrites texture.png; the AI's
is kept as texture_ai.png. sprite2mesh.py runs this after each model; see main() to repaint finished ones.
"""
import os
import sys

import numpy as np
import trimesh
from PIL import Image

CAMERA_DISTANCE, FOVY_DEG, INPUT_SIZE, RATIO = 1.9, 40.0, 512, 0.85
LIFT = 1.25


def sprite_mask(sprite_png, size=INPUT_SIZE, ratio=RATIO):
    """Where the sprite covers TripoSR's input: its alpha placed as sprite2mesh.prepare places the sprite."""
    sprite = Image.open(sprite_png).convert("RGBA")
    bbox = sprite.getbbox()
    if bbox:
        sprite = sprite.crop(bbox)
    w, h = sprite.size
    scale = ratio * size / max(w, h)
    alpha = sprite.getchannel("A").resize((max(1, round(w * scale)), max(1, round(h * scale))), Image.BILINEAR)
    mask = Image.new("L", (size, size), 0)
    mask.paste(alpha, ((size - alpha.width) // 2, (size - alpha.height) // 2))
    return np.asarray(mask) > 127


def shrink(mask, pixels):
    """The mask without its outer ring: Daggerfall's sprites have dark outlines, which the model's sides and edges
    would otherwise take as their colour."""
    m = mask.copy()
    for _ in range(pixels):
        m = m & np.roll(m, 1, 0) & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1)
    return m if m.any() else mask


def fill_outward(image, mask):
    """Background pixels take the colour of the nearest sprite pixel, softened a little so the fill has no seams.
    (Grown a ring at a time until 2026-10-12: 2.4 s, most of a model's painting.)"""
    from scipy import ndimage
    img = image.astype(float).copy()
    if not mask.any():
        return img
    _, (iy, ix) = ndimage.distance_transform_edt(~mask, return_indices=True)
    filled = img[iy, ix]
    soft = np.stack([ndimage.gaussian_filter(filled[..., c], 1.5) for c in range(filled.shape[-1])], -1)
    img[~mask] = soft[~mask]
    return img


def whiten(colours, amount):
    """Lifts pale, unsaturated colours (a seagull's dim greys) by `amount`, fading out as colours get saturated, so an
    orange beak or a brown post keep theirs."""
    if amount == 1:
        return colours
    mx, mn = colours.max(-1), colours.min(-1)
    saturation = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1), 0)
    weight = np.clip(1 - saturation / 0.25, 0, 1)[..., None]
    return np.clip(colours * (1 + (amount - 1) * weight), 0, 255)


def fitted_projection(vertices, mask):
    """Each vertex's place on the sprite: through TripoSR's input camera, then scaled and moved (per axis) so the
    model's outline spans the sprite's exactly (TripoSR returns models a few percent small)."""
    v = np.asarray(vertices, float)
    focal = 0.5 * INPUT_SIZE / np.tan(np.radians(FOVY_DEG) / 2)
    depth = CAMERA_DISTANCE - v[:, 0]
    px = INPUT_SIZE / 2 + focal * v[:, 1] / depth
    py = INPUT_SIZE / 2 - focal * v[:, 2] / depth
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return px, py
    def fit(p, lo, hi):
        span = max(p.max() - p.min(), 1e-6)
        return lo + (p - p.min()) * (hi + 1 - lo) / span
    return fit(px, xs.min(), xs.max()), fit(py, ys.min(), ys.max())


def grow(mask, pixels):
    m = mask.copy()
    for _ in range(pixels):
        m = m | np.roll(m, 1, 0) | np.roll(m, -1, 0) | np.roll(m, 1, 1) | np.roll(m, -1, 1)
    return m


_gl = None


def texture_space(uv, faces, attributes, size, dilation=2.0):
    """Three values per vertex drawn over the texture (rows top-down, as the PNG): RGB the interpolated values, A 1
    where a triangle (or its edge grown by `dilation` texels) covers the texel."""
    import moderngl
    global _gl
    if _gl is None:
        ctx = moderngl.create_context(standalone=True)
        vs = """#version 330
            in vec2 in_uv; in vec3 in_attr; out vec3 %s;
            void main() { %s = in_attr; gl_Position = vec4(in_uv * 2.0 - 1.0, 0.0, 1.0); }"""
        fs = """#version 330
            in vec3 %s; out vec4 o_col; void main() { o_col = vec4(%s, 1.0); }"""
        basic = ctx.program(vertex_shader=vs % ("v_attr", "v_attr"), fragment_shader=fs % ("v_attr", "v_attr"))
        edges = ctx.program(vertex_shader=vs % ("vg_attr", "vg_attr"), fragment_shader=fs % ("vf_attr", "vf_attr"),
            geometry_shader="""#version 330
            uniform float u_resolution; uniform float u_dilation;
            layout (triangles) in; layout (triangle_strip, max_vertices = 12) out;
            in vec3 vg_attr[]; out vec3 vf_attr;
            void edge(int a, int b) {
                vec2 pa = gl_in[a].gl_Position.xy, pb = gl_in[b].gl_Position.xy;
                vec2 dir = normalize((pb - pa) * u_resolution + 1e-9);
                vec2 off = vec2(-dir.y, dir.x) * u_dilation / u_resolution;
                gl_Position = vec4(pa + off, 0.0, 1.0); vf_attr = vg_attr[a]; EmitVertex();
                gl_Position = vec4(pa - off, 0.0, 1.0); vf_attr = vg_attr[a]; EmitVertex();
                gl_Position = vec4(pb + off, 0.0, 1.0); vf_attr = vg_attr[b]; EmitVertex();
                gl_Position = vec4(pb - off, 0.0, 1.0); vf_attr = vg_attr[b]; EmitVertex();
                EndPrimitive();
            }
            void main() { edge(0, 1); edge(1, 2); edge(2, 0); }""")
        _gl = (ctx, basic, edges)
    ctx, basic, edges = _gl
    vbo_uv = ctx.buffer(np.ascontiguousarray(uv, "f4"))
    vbo_attr = ctx.buffer(np.ascontiguousarray(attributes, "f4"))
    ibo = ctx.buffer(np.ascontiguousarray(faces, "i4"))
    fbo = ctx.framebuffer(color_attachments=[ctx.texture((size, size), 4, dtype="f4")])
    try:
        fbo.use()
        fbo.clear(0.0, 0.0, 0.0, 0.0)
        edges["u_resolution"].value = size
        edges["u_dilation"].value = dilation
        for prog in (edges, basic):   # the grown edges first, the triangles themselves over them
            vao = ctx.vertex_array(prog, [(vbo_uv, "2f", "in_uv"), (vbo_attr, "3f", "in_attr")], ibo)
            vao.render()
            vao.release()
        data = np.frombuffer(fbo.color_attachments[0].read(), "f4").reshape(size, size, 4)
    finally:
        fbo.color_attachments[0].release()
        fbo.release()
        vbo_uv.release(); vbo_attr.release(); ibo.release()
    return np.flipud(data)   # GL's rows run bottom-up (v = 0 at the bottom)


def reproject(model_dir, sprite_png, tex_size=1024, whiten_amount=1.0, cutout=False):
    """cutout (plants and trees): texels whose point falls outside the sprite's outline are transparent, so fronds and
    leaves keep the sprite's gaps exactly, as Skyrim's own foliage is cut out (alpha-tested)."""
    mesh = trimesh.load(os.path.join(model_dir, "mesh.obj"), process=False)
    source = np.asarray(Image.open(os.path.join(model_dir, "input.png")).convert("RGB"))
    mask = sprite_mask(sprite_png)
    keep = grow(mask, 2)
    # Daggerfall's palette is dim (its own lighting lifts it): every model a little brighter, pale colours more so.
    colours = np.clip(whiten(fill_outward(source, shrink(mask, 7)), whiten_amount) * LIFT, 0, 255)
    uv = np.asarray(mesh.visual.uv, float)
    px, py = fitted_projection(mesh.vertices, mask)
    # Surfaces seen edge-on from the sprite's view have no honest sprite colour (they would take whatever lies behind
    # them): those take the sprite's colour there shaded by the AI's own light and dark, so they aren't streaks.
    v = np.asarray(mesh.vertices, float)
    key = np.round(v / 1e-5).astype(np.int64)
    _, weld, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    inverse = inverse.reshape(-1)
    welded = trimesh.Trimesh(vertices=v[weld], faces=inverse[np.asarray(mesh.faces)], process=False)
    facing = np.abs(np.asarray(welded.vertex_normals, float)[inverse][:, 0])   # |n . view|, view along x
    ai_path = os.path.join(model_dir, "texture_ai.png")
    if not os.path.exists(ai_path):
        ai_path = os.path.join(model_dir, "texture.png")
    ai = np.asarray(Image.open(ai_path).convert("RGBA")).astype(float)
    painted = ai[ai[..., 3] > 0][:, :3]
    # Only the AI's light and dark is used, on the sprite's own colour: its hues are noise (green and pink specks
    # on statues' sides, 2026-10-12), blurred and kept to a narrow range so it shades, not streaks.
    lum = ai[..., :3].mean(-1)
    ref = max(painted.mean(), 1) if len(painted) else 1
    from scipy import ndimage
    ai_shade = np.clip(ndimage.gaussian_filter(lum, 2) / ref, 0.75, 1.15)
    ah, aw = ai.shape[:2]
    out = np.zeros((tex_size, tex_size, 4), np.uint8)
    # Every triangle drawn in texture space on the GPU (moderngl, as TripoSR bakes), its texels interpolating the
    # corners' sprite positions and facing, and a little past its edges so mip levels don't pull in black. (Drawn one
    # at a time in Python until 2026-10-12: 3.8 s a model, the slowest step; user: "its slow too".)
    texels = texture_space(uv, np.asarray(mesh.faces), np.stack([px, py, facing], 1), tex_size)
    gy_, gx_ = np.nonzero(texels[..., 3] > 0)
    t = texels[gy_, gx_]
    cy, cx = np.clip(t[:, 1].astype(int), 0, INPUT_SIZE - 1), np.clip(t[:, 0].astype(int), 0, INPUT_SIZE - 1)
    weight = np.clip((t[:, 2] - 0.2) / 0.4, 0, 1)[:, None]   # 1 facing the view, 0 edge-on
    own = colours[cy, cx] * ai_shade[np.clip(gy_ * ah // tex_size, 0, ah - 1), np.clip(gx_ * aw // tex_size, 0, aw - 1)][:, None]
    out[gy_, gx_, :3] = (colours[cy, cx] * weight + own * (1 - weight)).astype(np.uint8)
    out[gy_, gx_, 3] = np.where(keep[cy, cx], 255, 0) if cutout else 255
    texture = os.path.join(model_dir, "texture.png")
    ai = os.path.join(model_dir, "texture_ai.png")
    if not os.path.exists(ai) and os.path.exists(texture):
        os.replace(texture, ai)
    Image.fromarray(out).save(texture)
    open(os.path.join(model_dir, "reprojected"), "w").close()


def main():
    """usage: python reproject.py MODELS_DIR SPRITES_LIST [names...] [--again]  (SPRITES_LIST: sprite2mesh's job list,
    for each sprite's path and whitening)"""
    again = "--again" in sys.argv
    args = [a for a in sys.argv[1:] if a != "--again"]
    models, listing = args[0], args[1]
    jobs = {}
    with open(listing, encoding="utf-8-sig") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) >= 2:
                jobs[parts[0]] = (parts[1], float(parts[3]) if len(parts) > 3 else 1.0, len(parts) > 4 and parts[4] in ("Plant", "Tree"))
    for name in args[2:] or sorted(jobs):
        d = os.path.join(models, name)
        if name not in jobs or not os.path.exists(os.path.join(d, "mesh.obj")):
            continue
        if os.path.exists(os.path.join(d, "reprojected")) and not again:
            continue
        sprite, amount, cutout = jobs[name]
        reproject(d, sprite, whiten_amount=amount, cutout=cutout)
        print("painted", name, flush=True)


if __name__ == "__main__":
    main()
