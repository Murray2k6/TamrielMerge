"""Offline renderer for scenes exported by `TamrielMerge.Cli scene-export` (Skyrim/Render/SceneExporter.cs): a
converted place drawn without running the game, for the main menu's logo and its looping background.

Draws every placed mesh (instanced) with its diffuse texture and vertex colours, alpha-tested cut-outs, the land with
its painted textures blended by weight, water, a sky with the sun, sun shadows (one shadow map), hemisphere ambient
light and distance fog. Skyrim's axes: Z up, units of ~1.43 cm.

  render_scene.py SCENE --overview out.png                       a top-down view, to choose cameras
  render_scene.py SCENE --camera x,y,z --target x,y,z --out a.png  one still
  render_scene.py SCENE --path path.json --out-dir frames/          a flythrough (path.json: keys of camera/target/time)
"""
import argparse
import json
import math
import os
import struct
import subprocess
import sys
import time

import moderngl
import numpy as np
from PIL import Image


def gpu_temperature():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=10).stdout.strip().splitlines()
        return int(out[0]) if out else None
    except Exception:
        return None


def cool_down(max_temp=78):
    t = gpu_temperature()
    while t is not None and t > max_temp:
        print(f"  GPU at {t} C, waiting to cool below {max_temp} C", flush=True)
        time.sleep(20)
        t = gpu_temperature()


# ---- scene files ----

def read_string(b, o):
    n = struct.unpack_from("<i", b, o)[0]
    return b[o + 4:o + 4 + n].decode("utf-8"), o + 4 + n


def read_mesh(path):
    b = open(path, "rb").read()
    count = struct.unpack_from("<i", b, 0)[0]
    o = 4
    shapes = []
    for _ in range(count):
        diffuse, o = read_string(b, o)
        normal, o = read_string(b, o)
        alpha_mode, threshold, two_sided, nv, nt = struct.unpack_from("<ifiii", b, o)
        o += 20
        pos = np.frombuffer(b, np.float32, nv * 3, o).reshape(nv, 3); o += nv * 12
        nrm = np.frombuffer(b, np.float32, nv * 3, o).reshape(nv, 3); o += nv * 12
        uv = np.frombuffer(b, np.float32, nv * 2, o).reshape(nv, 2); o += nv * 8
        col = np.frombuffer(b, np.float32, nv * 4, o).reshape(nv, 4); o += nv * 16
        tri = np.frombuffer(b, np.int32, nt * 3, o).reshape(nt, 3); o += nt * 12
        shapes.append(dict(diffuse=diffuse, normal=normal, alpha_mode=alpha_mode, threshold=threshold / 255.0,
                           pos=pos, nrm=nrm, uv=uv, col=col, tri=tri))
    return shapes


def read_land(path):
    b = open(path, "rb").read()
    n = struct.unpack_from("<i", b, 0)[0]
    o = 4
    cells = []
    for _ in range(n):
        gx, gy, has = struct.unpack_from("<iii", b, o)
        o += 12
        if not has:
            continue
        h = np.frombuffer(b, np.float32, 33 * 33, o).reshape(33, 33); o += 33 * 33 * 4
        k = struct.unpack_from("<i", b, o)[0]; o += 4
        weights = {}
        for _ in range(k):
            tid = struct.unpack_from("<i", b, o)[0]; o += 4
            weights[tid] = np.frombuffer(b, np.float32, 33 * 33, o).reshape(33, 33); o += 33 * 33 * 4
        cells.append((gx, gy, h, weights))
    return cells


# ---- maths ----

def look_at(eye, target, up=(0, 0, 1)):
    eye, target, up = map(lambda v: np.array(v, np.float64), (eye, target, up))
    f = target - eye; f /= np.linalg.norm(f)
    s = np.cross(f, up); s /= np.linalg.norm(s)
    u = np.cross(s, f)
    m = np.identity(4)
    m[0, :3], m[1, :3], m[2, :3] = s, u, -f
    m[:3, 3] = -m[:3, :3] @ eye
    return m


def perspective(fovy, aspect, near, far):
    f = 1 / math.tan(math.radians(fovy) / 2)
    m = np.zeros((4, 4))
    m[0, 0], m[1, 1] = f / aspect, f
    m[2, 2], m[2, 3] = (far + near) / (near - far), 2 * far * near / (near - far)
    m[3, 2] = -1
    return m


def ortho(l, r, b, t, n, f):
    m = np.identity(4)
    m[0, 0], m[1, 1], m[2, 2] = 2 / (r - l), 2 / (t - b), -2 / (f - n)
    m[0, 3], m[1, 3], m[2, 3] = -(r + l) / (r - l), -(t + b) / (t - b), -(f + n) / (f - n)
    return m


def gl(m):
    return np.ascontiguousarray(m.T, dtype="f4").tobytes()


# ---- shaders ----

COMMON = """
uniform vec3 sun_dir; uniform vec3 sun_col; uniform vec3 sky_col; uniform vec3 ground_col;
uniform vec3 fog_col; uniform float fog_density; uniform vec3 cam_pos; uniform float exposure;
uniform sampler2DShadow shadow_map; uniform mat4 light_vp; uniform vec2 cloud_offset; uniform float cloud_cover;
float shadowed(vec3 world, vec3 n) {
    vec4 lp = light_vp * vec4(world + n * 12.0, 1.0);
    vec3 p = lp.xyz / lp.w * 0.5 + 0.5;
    if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0 || p.z > 1.0) return 1.0;
    float s = 0.0; vec2 texel = 1.0 / vec2(textureSize(shadow_map, 0));
    for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++)
        s += texture(shadow_map, vec3(p.xy + vec2(x, y) * texel * 1.5, p.z - 0.0008));
    return s / 9.0;
}
vec3 to_linear(vec3 c) { return pow(c, vec3(2.2)); }
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}
float fbm(vec2 p) { float s = 0.0, a = 0.5; for (int i = 0; i < 6; i++) { s += a * vnoise(p); p *= 2.03; a *= 0.5; } return s; }
vec3 sky(vec3 dir) {
    dir = normalize(dir);
    float e = clamp(dir.z, -0.2, 1.0);
    vec3 c = mix(fog_col, sky_col, pow(max(e, 0.0), 0.45));
    float sd = max(dot(dir, sun_dir), 0.0);
    // Clouds on a plane overhead, lit from the sun's side, thinning toward the horizon.
    if (dir.z > 0.0 && cloud_cover > 0.0) {
        vec2 uv = dir.xy / (dir.z + 0.08) * 0.9 + cloud_offset;
        float n = fbm(uv);
        float cov = smoothstep(1.0 - cloud_cover, 1.0 - cloud_cover + 0.25, n) * smoothstep(0.0, 0.05, dir.z);
        float lit = clamp(0.55 + 0.6 * (fbm(uv + sun_dir.xy * 0.08) - n) * -4.0 + 0.3 * sd, 0.35, 1.2);
        vec3 cloud = mix(fog_col * 0.85, vec3(1.0, 0.97, 0.92) * (sun_col / max(max(sun_col.r, sun_col.g), sun_col.b)), 0.6) * lit;
        c = mix(c, cloud, cov * 0.92);
    }
    c += sun_col * (pow(sd, 900.0) * 6.0 + pow(sd, 12.0) * 0.18);
    return c;
}
vec3 finish(vec3 c) {
    c *= exposure;
    c = (c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14);   // ACES fit
    return pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2));
}
vec3 light(vec3 albedo, vec3 n, vec3 world) {
    float ndl = max(dot(n, sun_dir), 0.0);
    vec3 amb = mix(ground_col, sky_col, n.z * 0.5 + 0.5);
    vec3 c = albedo * (amb + sun_col * ndl * shadowed(world, n));
    float d = length(world - cam_pos);
    float f = 1.0 - exp(-d * fog_density);
    return mix(c, sky(normalize(world - cam_pos)) * 0.92, clamp(f, 0.0, 1.0));
}
"""

OBJECT_VS = """
#version 330
in vec3 in_pos; in vec3 in_nrm; in vec2 in_uv; in vec4 in_col;
in vec4 m0; in vec4 m1; in vec4 m2; in vec4 m3;
uniform mat4 viewproj;
out vec3 v_world; out vec3 v_nrm; out vec2 v_uv; out vec4 v_col;
void main() {
    mat4 model = mat4(m0, m1, m2, m3);
    vec4 w = model * vec4(in_pos, 1.0);
    v_world = w.xyz; v_nrm = mat3(model) * in_nrm; v_uv = in_uv; v_col = in_col;
    gl_Position = viewproj * w;
}
"""

OBJECT_FS = """
#version 330
""" + COMMON + """
in vec3 v_world; in vec3 v_nrm; in vec2 v_uv; in vec4 v_col;
uniform sampler2D tex; uniform int alpha_mode; uniform float alpha_ref;
out vec4 frag;
void main() {
    vec4 t = texture(tex, v_uv);
    float a = t.a * v_col.a;
    if (alpha_mode == 1 && a < alpha_ref) discard;
    if (alpha_mode == 2 && a < 0.04) discard;
    vec3 n = normalize(v_nrm); if (!gl_FrontFacing) n = -n;
    vec3 c = light(to_linear(t.rgb * v_col.rgb), n, v_world);
    frag = vec4(finish(c), alpha_mode == 2 ? a : 1.0);
}
"""

SHADOW_VS = """
#version 330
in vec3 in_pos; in vec2 in_uv; in vec4 m0; in vec4 m1; in vec4 m2; in vec4 m3;
uniform mat4 viewproj; out vec2 v_uv;
void main() { v_uv = in_uv; gl_Position = viewproj * mat4(m0, m1, m2, m3) * vec4(in_pos, 1.0); }
"""
SHADOW_FS = """
#version 330
in vec2 v_uv; uniform sampler2D tex; uniform int alpha_mode; uniform float alpha_ref;
void main() { if (alpha_mode != 0 && texture(tex, v_uv).a < max(alpha_ref, 0.3)) discard; }
"""

LAND_SHADOW_VS = """
#version 330
in vec3 in_pos; uniform mat4 viewproj;
void main() { gl_Position = viewproj * vec4(in_pos, 1.0); }
"""
LAND_SHADOW_FS = """
#version 330
void main() { }
"""

LAND_VS = """
#version 330
in vec3 in_pos; in vec3 in_nrm; uniform mat4 viewproj;
out vec3 v_world; out vec3 v_nrm;
void main() { v_world = in_pos; v_nrm = in_nrm; gl_Position = viewproj * vec4(in_pos, 1.0); }
"""
LAND_FS = """
#version 330
""" + COMMON + """
in vec3 v_world; in vec3 v_nrm;
uniform sampler2DArray weights; uniform sampler2DArray diffuse; uniform int layers;
uniform vec2 grid_origin; uniform vec2 grid_size; uniform float tile;
out vec4 frag;
void main() {
    vec2 wuv = (v_world.xy - grid_origin) / grid_size;
    vec3 c = vec3(0.0); float total = 0.0;
    for (int i = 0; i < layers; i++) {
        float w = texture(weights, vec3(wuv, float(i))).r;
        if (w <= 0.002) continue;
        c += w * texture(diffuse, vec3(v_world.xy / tile, float(i))).rgb; total += w;
    }
    c = total > 0.0 ? c / total : vec3(0.4, 0.38, 0.3);
    frag = vec4(finish(light(to_linear(c), normalize(v_nrm), v_world)), 1.0);
}
"""

WATER_VS = """
#version 330
in vec3 in_pos; uniform mat4 viewproj; out vec3 v_world;
void main() { v_world = in_pos; gl_Position = viewproj * vec4(in_pos, 1.0); }
"""
WATER_FS = """
#version 330
""" + COMMON + """
in vec3 v_world; uniform vec3 water_col; uniform float time; out vec4 frag;
// Ripples: the slope of two drifting noise layers, so the sky and the sun glint off a moving surface.
vec3 ripple(vec2 p) {
    float e = 6.0;
    vec2 a = p / 520.0 + vec2(time * 0.035, time * 0.02), b = p / 190.0 - vec2(time * 0.05, -time * 0.03);
    float h0 = fbm(a) + 0.5 * fbm(b);
    float hx = fbm(a + vec2(e / 520.0, 0)) + 0.5 * fbm(b + vec2(e / 190.0, 0));
    float hy = fbm(a + vec2(0, e / 520.0)) + 0.5 * fbm(b + vec2(0, e / 190.0));
    return normalize(vec3((h0 - hx) * 3.2, (h0 - hy) * 3.2, 1.0));
}
void main() {
    vec3 v = normalize(cam_pos - v_world);
    float d = length(v_world - cam_pos);
    vec3 n = normalize(mix(ripple(v_world.xy), vec3(0, 0, 1), clamp(d / 60000.0, 0.0, 0.85)));
    float fres = 0.03 + 0.97 * pow(1.0 - max(dot(v, n), 0.0), 5.0);
    vec3 refl = sky(reflect(-v, n));
    float lit = 0.35 + 0.65 * shadowed(v_world, vec3(0, 0, 1));
    vec3 c = mix(water_col * lit, refl * 0.8, clamp(fres, 0.0, 0.9));
    c += sun_col * pow(max(dot(reflect(-sun_dir, n), v), 0.0), 220.0) * 2.5 * lit;
    c = mix(c, sky(-v) * 0.92, clamp(1.0 - exp(-d * fog_density), 0.0, 1.0));
    frag = vec4(finish(c), mix(0.94, 1.0, fres));
}
"""

SKY_VS = """
#version 330
in vec2 in_pos; out vec2 v_pos;
void main() { v_pos = in_pos; gl_Position = vec4(in_pos, 0.9999, 1.0); }
"""
SKY_FS = """
#version 330
""" + COMMON + """
in vec2 v_pos; uniform mat4 inv_viewproj; out vec4 frag;
void main() {
    vec4 a = inv_viewproj * vec4(v_pos, -1.0, 1.0), b = inv_viewproj * vec4(v_pos, 1.0, 1.0);
    vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
    frag = vec4(finish(sky(dir)), 1.0);
}
"""


class Renderer:
    def __init__(self, scene_dir, width, height, ss=2, max_texture=1024):
        self.dir = scene_dir
        self.scene = json.load(open(os.path.join(scene_dir, "scene.json")))
        self.ctx = moderngl.create_standalone_context(require=330)
        self.size = (width * ss, height * ss)
        self.out_size = (width, height)
        self.max_texture = max_texture
        self.textures = {}
        ctx = self.ctx
        self.obj_prog = ctx.program(vertex_shader=OBJECT_VS, fragment_shader=OBJECT_FS)
        self.shadow_prog = ctx.program(vertex_shader=SHADOW_VS, fragment_shader=SHADOW_FS)
        self.land_prog = ctx.program(vertex_shader=LAND_VS, fragment_shader=LAND_FS)
        # The land has no per-instance placement, so it gets a depth program of its own.
        self.land_shadow_prog = ctx.program(vertex_shader=LAND_SHADOW_VS, fragment_shader=LAND_SHADOW_FS)
        self.water_prog = ctx.program(vertex_shader=WATER_VS, fragment_shader=WATER_FS)
        self.sky_prog = ctx.program(vertex_shader=SKY_VS, fragment_shader=SKY_FS)
        self.white = ctx.texture((1, 1), 4, bytes([255, 255, 255, 255]))
        self.load_objects()
        self.load_land()
        self.add_ground()
        self.people_draws = []
        self.load_people()
        self.color = ctx.texture(self.size, 4)
        self.depth = ctx.depth_texture(self.size)
        self.fbo = ctx.framebuffer([self.color], self.depth)
        self.shadow_size = 8192
        self.shadow_tex = ctx.depth_texture((self.shadow_size, self.shadow_size))
        self.shadow_tex.compare_func = "<="
        self.shadow_tex.filter = (moderngl.LINEAR, moderngl.LINEAR)
        self.shadow_fbo = ctx.framebuffer(depth_attachment=self.shadow_tex)
        quad = np.array([-1, -1, 1, -1, -1, 1, 1, 1], "f4")
        self.sky_vao = ctx.vertex_array(self.sky_prog, [(ctx.buffer(quad), "2f", "in_pos")])
        w = 400000
        water = np.array([-w, -w, 0, w, -w, 0, -w, w, 0, w, w, 0], "f4").reshape(4, 3)
        water[:, :2] += np.array(self.centre, "f4")
        water[:, 2] = self.scene["water"]
        self.water_vao = ctx.vertex_array(self.water_prog, [(ctx.buffer(water.tobytes()), "3f", "in_pos")])
        self.set_light()

    @property
    def centre(self):
        return self.bounds_min[:2] * 0.5 + self.bounds_max[:2] * 0.5

    def texture(self, path):
        if not path:
            return self.white
        if path in self.textures:
            return self.textures[path]
        file = os.path.join(self.dir, path.replace("\\", os.sep))
        tex = self.white
        if os.path.exists(file):
            try:
                img = Image.open(file).convert("RGBA")
                if max(img.size) > self.max_texture:
                    s = self.max_texture / max(img.size)
                    img = img.resize((max(1, int(img.width * s)), max(1, int(img.height * s))), Image.LANCZOS)
                tex = self.ctx.texture(img.size, 4, img.tobytes())
                tex.build_mipmaps()
                tex.filter = (moderngl.LINEAR_MIPMAP_LINEAR, moderngl.LINEAR)
                tex.anisotropy = 16.0
            except Exception as e:
                print(f"  texture {path}: {e}", file=sys.stderr)
        self.textures[path] = tex
        return tex

    def load_objects(self):
        by_mesh = {}
        for inst in self.scene["instances"]:
            if inst["m"][11] > 30000:   # placed far above everything (effects, the Arcane University's portal)
                continue
            by_mesh.setdefault(inst["mesh"], []).append(np.array(inst["m"], np.float32).reshape(4, 4))
        self.draws = []
        lo, hi = np.full(3, np.inf), np.full(3, -np.inf)
        names = self.scene["meshes"]
        for mesh_id, mats in by_mesh.items():
            path = os.path.join(self.dir, "meshes", f"{mesh_id}.bin")
            if not os.path.exists(path) or os.path.getsize(path) == 0:
                continue
            # Effects (cloud layers, mist, fire) aren't drawn.
            if mesh_id < len(names) and ("\\effects\\" in names[mesh_id].lower() or "cloud" in names[mesh_id].lower()):
                continue
            shapes = read_mesh(path)
            cols = np.stack([m.T for m in mats]).astype("f4")   # column-major per instance
            inst_buf = self.ctx.buffer(cols.tobytes())
            for s in shapes:
                size = 0.0
                for m in mats:
                    p = s["pos"] @ m[:3, :3].T + m[:3, 3]
                    lo, hi = np.minimum(lo, p.min(0)), np.maximum(hi, p.max(0))
                    size = max(size, float((p.max(0) - p.min(0))[:2].max()))
                vbo = self.ctx.buffer(np.hstack([s["pos"], s["nrm"], s["uv"], s["col"]]).astype("f4").tobytes())
                ibo = self.ctx.buffer(s["tri"].astype("i4").tobytes())
                vao = self.ctx.vertex_array(self.obj_prog, [(vbo, "3f 3f 2f 4f", "in_pos", "in_nrm", "in_uv", "in_col"),
                                                           (inst_buf, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
                svao = self.ctx.vertex_array(self.shadow_prog, [(vbo, "3f 12x 2f 16x", "in_pos", "in_uv"),
                                                               (inst_buf, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
                self.draws.append(dict(vao=vao, svao=svao, count=len(mats), tex=self.texture(s["diffuse"]),
                                       alpha_mode=s["alpha_mode"], threshold=s["threshold"], size=size))
        self.bounds_min, self.bounds_max = lo, hi
        print(f"{len(self.scene['instances'])} objects, {len(self.scene.get('people', []))} people, {len(self.draws)} shape draws, bounds {lo.round()} .. {hi.round()}")

    def load_people(self):
        """The town's people: each a few posed parts (body, clothes, head) at their place. Most walk the way they face
        (seeded, so every frame of a flyby agrees), shown at the step of their walk cycle that matches the distance
        walked (the exporter poses each part standing and at WALK_STEPS steps); the rest stand."""
        for d in getattr(self, "people_draws", []):
            if d in self.draws:
                self.draws.remove(d)
        people = self.scene.get("people", [])
        self.people = [np.array(p["m"], np.float32).reshape(4, 4) for p in people]
        rng = np.random.default_rng(11)
        self.walk, self.phase = [], []
        for p, m in zip(people, self.people):
            fwd = m[:3, :3] @ np.array([0, 1, 0], np.float32)
            fwd[2] = 0
            n = np.linalg.norm(fwd)
            walks = p.get("walk") and rng.random() < 0.8 and n > 0
            self.walk.append(fwd / n * rng.uniform(60, 85) if walks else None)
            self.phase.append(rng.uniform(0, 1))
        self.parts_of = [p["parts"] for p in people]
        self.walk_parts = [p.get("walk") or [] for p in people]
        users = {}
        for i, p in enumerate(people):
            for mesh in set(p["parts"]) | {m for step in (p.get("walk") or []) for m in step}:
                users[mesh] = users.get(mesh, 0) + 1
        self.people_draws = []
        self.mesh_draws = {}
        for mesh_id, most in users.items():
            path = os.path.join(self.dir, "meshes", f"{mesh_id}.bin")
            if not os.path.exists(path) or os.path.getsize(path) == 0:
                continue
            inst = self.ctx.buffer(reserve=max(1, most) * 64, dynamic=True)
            for sh in read_mesh(path):
                vbo = self.ctx.buffer(np.hstack([sh["pos"], sh["nrm"], sh["uv"], sh["col"]]).astype("f4").tobytes())
                ibo = self.ctx.buffer(sh["tri"].astype("i4").tobytes())
                vao = self.ctx.vertex_array(self.obj_prog, [(vbo, "3f 3f 2f 4f", "in_pos", "in_nrm", "in_uv", "in_col"),
                                                           (inst, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
                svao = self.ctx.vertex_array(self.shadow_prog, [(vbo, "3f 12x 2f 16x", "in_pos", "in_uv"),
                                                               (inst, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
                d = dict(vao=vao, svao=svao, count=0, tex=self.texture(sh["diffuse"]),
                         alpha_mode=sh["alpha_mode"], threshold=sh["threshold"], inst=inst)
                self.draws.append(d)
                self.people_draws.append(d)
                self.mesh_draws.setdefault(mesh_id, []).append(d)
        self.set_time(0.0)

    def set_time(self, t):
        """Puts everyone where they are t seconds in: walkers moved along and shown at their step (a stride every 90
        units), with a slight bob; the water's ripples drift with it."""
        self.time = t
        placed = {}
        for i, m in enumerate(self.people):
            parts = self.parts_of[i]
            if self.walk[i] is not None:
                m = m.copy()
                m[:2, 3] += self.walk[i][:2] * t
                cycle = np.linalg.norm(self.walk[i][:2]) * t / 90.0 + self.phase[i]
                steps = self.walk_parts[i]
                if steps:
                    parts = steps[int(cycle * len(steps)) % len(steps)]
                m[2, 3] += 1.2 * abs(math.sin(cycle * 2 * math.pi))
            for mesh in parts:
                placed.setdefault(mesh, []).append(m)
        for mesh_id, draws in self.mesh_draws.items():
            mats = placed.get(mesh_id, [])
            data = np.stack([x.T for x in mats]).astype("f4").tobytes() if mats else b""
            for d in draws:
                d["count"] = len(mats)
                if mats and d is draws[0]:
                    d["inst"].write(data)

    def add_crowd(self, count, centre, radius, ground_only=True, seed=5):
        """More of the town's own people (copies of those placed, so the same races and clothes) on open, level ground
        within radius of centre: a top-down height map of the scene picks spots above the water, flat over a few
        paces and (ground_only) at street level, not on roofs."""
        people = self.scene.get("people", [])
        # Copies of people only, not the town's animals: a person is dressed from the character assets.
        names = self.scene["meshes"]
        humans = [p for p in people if any("character assets" in names[i].lower() for i in p["parts"] if i < len(names))]
        if not humans or count <= 0:
            return
        n = 1024
        cx, cy = centre
        top, bottom = float(self.bounds_max[2]) + 2000, float(self.bounds_min[2]) - 2000
        view = look_at((cx, cy, top), (cx, cy, bottom), up=(0, 1, 0))
        proj = ortho(-radius, radius, -radius, radius, 1.0, top - bottom + 1)
        depth = self.ctx.depth_texture((n, n))
        fbo = self.ctx.framebuffer(depth_attachment=depth)
        fbo.use(); fbo.clear(depth=1.0)
        self.ctx.enable(moderngl.DEPTH_TEST); self.ctx.disable(moderngl.CULL_FACE | moderngl.BLEND)
        self.shadow_prog["viewproj"].write(gl(proj @ view))
        for d in self.draws:
            # Not the huge ones (a game's sky dome, sea box, world-edge walls), which would hide the ground.
            if d["count"] == 0 or d.get("size", 0) > 15000:
                continue
            d["tex"].use(0); self.shadow_prog["tex"].value = 0
            self.shadow_prog["alpha_mode"].value = d["alpha_mode"]; self.shadow_prog["alpha_ref"].value = d["threshold"]
            d["svao"].render(instances=d["count"])
        if self.land_vao is not None:
            self.land_shadow_prog["viewproj"].write(self.shadow_prog["viewproj"].read())
            self.land_shadow_vao.render()
        d = np.frombuffer(depth.read(), dtype="f4").reshape(n, n)
        height = top - (1.0 + d * (top - bottom))          # orthographic depth is linear
        height[d >= 1.0] = -1e9                            # nothing there
        cell = 2 * radius / n
        from numpy.lib.stride_tricks import sliding_window_view as win
        pad = np.pad(height, 2, mode="edge")
        w5 = win(pad, (5, 5))
        flat = (w5.max(axis=(2, 3)) - w5.min(axis=(2, 3))) < 20
        ok = flat & (height > self.scene["water"] + 30) & (height > -1e8)
        if ground_only:
            k = max(3, int(1500 / cell)) | 1
            padk = np.pad(height, k // 2, mode="edge")
            low = win(padk, (k, k)).min(axis=(2, 3))
            ok &= (height - low) < 60
        ys, xs = np.nonzero(ok)
        if len(xs) == 0:
            print("  crowd: no open ground")
            return
        rng = np.random.default_rng(seed)
        pick = rng.choice(len(xs), size=min(count, len(xs)), replace=False)
        added = 0
        for i in pick:
            src = humans[rng.integers(len(humans))]
            m = np.array(src["m"], np.float32).reshape(4, 4)
            scale = np.linalg.norm(m[:3, 0])
            # Along the longest open run from here (a street's length), either way.
            best, heading = -1, rng.uniform(0, 2 * math.pi)
            for a8 in range(8):
                ang = a8 * math.pi / 8
                run = 0
                for step in range(1, 40):
                    xx = int(xs[i] + math.sin(ang) * step); yy = int(ys[i] + math.cos(ang) * step)
                    if not (0 <= xx < n and 0 <= yy < n and ok[yy, xx]):
                        break
                    run = step
                if run > best:
                    best, heading = run, ang + (math.pi if rng.random() < 0.5 else 0)
            # Facing (sin h, cos h) as the exporter's matrices do: clockwise from north.
            c, s_ = math.cos(heading), math.sin(heading)
            rot = np.array([[c, s_, 0], [-s_, c, 0], [0, 0, 1]], np.float32) * scale
            mm = np.identity(4, np.float32)
            mm[:3, :3] = rot
            # The depth read back starts at the bottom row (-y); columns run from -x.
            mm[0, 3] = cx - radius + (xs[i] + 0.5) * cell
            mm[1, 3] = cy - radius + (ys[i] + 0.5) * cell
            mm[2, 3] = height[ys[i], xs[i]]
            people.append(dict(parts=src["parts"], walk=src.get("walk"), m=mm.reshape(-1).tolist()))
            added += 1
        fbo.release(); depth.release()
        self.load_people()
        print(f"  crowd: {added} more people ({len(xs)} open spots)")

    def town_level(self):
        """A town placed into its overland (scene.json "holes", as blend_scene.town_level): its street level, low
        among its objects' heights in its cells."""
        inside = {tuple(h) for h in self.scene.get("holes", [])}
        zs = [q["m"][11] for q in self.scene["instances"]
              if (math.floor(q["m"][3] / 4096), math.floor(q["m"][7] / 4096)) in inside]
        return float(np.percentile(zs, 10)) if zs else 0.0

    def fill_holes(self):
        """The overland's cells under a placed town: ground at the town's level (its margin's grass), as blend_scene."""
        holes = self.scene.get("holes", [])
        if not holes:
            return
        margin = next((i for i, m in enumerate(self.scene["meshes"]) if "margin" in m.lower()), None)
        tex = self.white
        if margin is not None and os.path.exists(os.path.join(self.dir, "meshes", f"{margin}.bin")):
            tex = self.texture(read_mesh(os.path.join(self.dir, "meshes", f"{margin}.bin"))[0]["diffuse"])
        z, tile = self.town_level() - 4, 2048.0
        rows, idx = [], []
        for k, (gx, gy) in enumerate(holes):
            for x, y in ((gx, gy), (gx + 1, gy), (gx, gy + 1), (gx + 1, gy + 1)):
                rows.append([x * 4096, y * 4096, z, 0, 0, 1, x * 4096 / tile, y * 4096 / tile, 1, 1, 1, 1])
            idx += [4 * k, 4 * k + 1, 4 * k + 3, 4 * k, 4 * k + 3, 4 * k + 2]
        vbo = self.ctx.buffer(np.array(rows, "f4").tobytes())
        ibo = self.ctx.buffer(np.array(idx, "i4").tobytes())
        inst = self.ctx.buffer(np.identity(4, "f4").tobytes())
        vao = self.ctx.vertex_array(self.obj_prog, [(vbo, "3f 3f 2f 4f", "in_pos", "in_nrm", "in_uv", "in_col"),
                                                   (inst, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
        svao = self.ctx.vertex_array(self.shadow_prog, [(vbo, "3f 12x 2f 16x", "in_pos", "in_uv"),
                                                       (inst, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
        self.draws.insert(0, dict(vao=vao, svao=svao, count=1, tex=tex, alpha_mode=0, threshold=0.5))

    def add_ground(self):
        """A town built without land (Daggerfall's): ground out to the horizon under it, with the grass of its
        margin, so wide views don't look out over nothing."""
        self.fill_holes()
        if self.land_vao is not None or self.scene["water"] > -10000:
            return
        margin = next((i for i, m in enumerate(self.scene["meshes"]) if "margin" in m.lower()), None)
        tex = self.white
        if margin is not None and os.path.exists(os.path.join(self.dir, "meshes", f"{margin}.bin")):
            tex = self.texture(read_mesh(os.path.join(self.dir, "meshes", f"{margin}.bin"))[0]["diffuse"])
        z = float(self.bounds_min[2]) - 4
        w, tile = 400000.0, 2048.0
        cx, cy = self.centre
        v = np.array([[cx - w, cy - w, z, 0, 0, 1, (cx - w) / tile, (cy - w) / tile, 1, 1, 1, 1],
                      [cx + w, cy - w, z, 0, 0, 1, (cx + w) / tile, (cy - w) / tile, 1, 1, 1, 1],
                      [cx - w, cy + w, z, 0, 0, 1, (cx - w) / tile, (cy + w) / tile, 1, 1, 1, 1],
                      [cx + w, cy + w, z, 0, 0, 1, (cx + w) / tile, (cy + w) / tile, 1, 1, 1, 1]], "f4")
        vbo = self.ctx.buffer(v.tobytes())
        ibo = self.ctx.buffer(np.array([0, 1, 3, 0, 3, 2], "i4").tobytes())
        inst = self.ctx.buffer(np.identity(4, "f4").tobytes())
        vao = self.ctx.vertex_array(self.obj_prog, [(vbo, "3f 3f 2f 4f", "in_pos", "in_nrm", "in_uv", "in_col"),
                                                   (inst, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
        svao = self.ctx.vertex_array(self.shadow_prog, [(vbo, "3f 12x 2f 16x", "in_pos", "in_uv"),
                                                       (inst, "4f 4f 4f 4f/i", "m0", "m1", "m2", "m3")], ibo)
        self.draws.insert(0, dict(vao=vao, svao=svao, count=1, tex=tex, alpha_mode=0, threshold=0.5))
        self.scene["water"] = z - 1000

    def load_land(self):
        self.land_vao = None
        path = os.path.join(self.dir, "land.bin")
        if not os.path.exists(path):
            return
        cells = read_land(path)
        if not cells:
            return
        gxs = [c[0] for c in cells]; gys = [c[1] for c in cells]
        x0, y0, x1, y1 = min(gxs), min(gys), max(gxs), max(gys)
        W, H = (x1 - x0 + 1) * 32 + 1, (y1 - y0 + 1) * 32 + 1
        names = self.scene["terrain"]["textures"]
        layers = max(1, len(names))
        weights = np.zeros((layers, H, W), np.float32)
        verts, idx = [], []
        base = 0
        # Round a placed town the land levelled to its streets, easing back over 8192 units (as blend_scene.land).
        holes = self.scene.get("holes", [])
        if holes:
            level = self.town_level()
            hx = [q[0] for q in holes]; hy = [q[1] for q in holes]
            X0, X1, Y0, Y1 = min(hx) * 4096, (max(hx) + 1) * 4096, min(hy) * 4096, (max(hy) + 1) * 4096
        for gx, gy, h, ws in cells:
            if holes:
                ys, xs = np.mgrid[0:33, 0:33]
                px, py = gx * 4096 + xs * 128.0, gy * 4096 + ys * 128.0
                u = np.clip(np.hypot(np.maximum(np.maximum(X0 - px, px - X1), 0), np.maximum(np.maximum(Y0 - py, py - Y1), 0)) / 8192.0, 0, 1)
                h = level + (np.asarray(h) - level) * (u * u * (3 - 2 * u))
            ox, oy = (gx - x0) * 32, (gy - y0) * 32
            for tid, w in ws.items():
                weights[tid, oy:oy + 33, ox:ox + 33] = np.maximum(weights[tid, oy:oy + 33, ox:ox + 33], w)
            ys, xs = np.mgrid[0:33, 0:33]
            px, py = gx * 4096 + xs * 128.0, gy * 4096 + ys * 128.0
            dzdx = np.gradient(h, axis=1) / 128.0; dzdy = np.gradient(h, axis=0) / 128.0
            n = np.dstack([-dzdx, -dzdy, np.ones_like(h)]); n /= np.linalg.norm(n, axis=2, keepdims=True)
            v = np.dstack([px, py, h, n]).reshape(-1, 6)
            verts.append(v)
            q = np.arange(33 * 33).reshape(33, 33)
            a, b, c, d = q[:-1, :-1].ravel(), q[:-1, 1:].ravel(), q[1:, :-1].ravel(), q[1:, 1:].ravel()
            idx.append(base + np.stack([a, b, d, a, d, c], 1).ravel())
            base += 33 * 33
            lo = np.array([px.min(), py.min(), h.min()]); hi = np.array([px.max(), py.max(), h.max()])
            self.bounds_min, self.bounds_max = np.minimum(self.bounds_min, lo), np.maximum(self.bounds_max, hi)
        vbo = self.ctx.buffer(np.vstack(verts).astype("f4").tobytes())
        ibo = self.ctx.buffer(np.concatenate(idx).astype("i4").tobytes())
        self.land_vao = self.ctx.vertex_array(self.land_prog, [(vbo, "3f 3f", "in_pos", "in_nrm")], ibo)
        self.land_shadow_vao = self.ctx.vertex_array(self.land_shadow_prog, [(vbo, "3f 12x", "in_pos")], ibo)
        self.land_weights = self.ctx.texture_array((W, H, layers), 1, (np.clip(weights, 0, 1) * 255).astype("u1").tobytes())
        self.land_weights.filter = (moderngl.LINEAR, moderngl.LINEAR)
        size = 512
        stack = []
        for name in names or [""]:
            file = os.path.join(self.dir, name.replace("\\", os.sep)) if name else ""
            try:
                img = Image.open(file).convert("RGBA").resize((size, size), Image.LANCZOS)
            except Exception:
                img = Image.new("RGBA", (size, size), (110, 100, 80, 255))
            stack.append(np.asarray(img))
        self.land_diffuse = self.ctx.texture_array((size, size, layers), 4, np.stack(stack).tobytes())
        self.land_diffuse.build_mipmaps()
        self.land_diffuse.filter = (moderngl.LINEAR_MIPMAP_LINEAR, moderngl.LINEAR)
        self.land_diffuse.anisotropy = 16.0
        self.land_info = (layers, (x0 * 4096.0, y0 * 4096.0), ((x1 - x0 + 1) * 4096.0 + 128, (y1 - y0 + 1) * 4096.0 + 128))
        print(f"land: {len(cells)} cells, {layers} textures")

    def set_light(self, azimuth=225, elevation=32, sun=(1.0, 0.9, 0.75), sun_power=3.2, sky=(0.42, 0.55, 0.78),
                  ground=(0.28, 0.25, 0.22), fog=(0.72, 0.76, 0.82), fog_density=1.6e-5, exposure=0.9,
                  water=(0.06, 0.13, 0.16), cloud_cover=0.45, cloud_offset=(0.0, 0.0)):
        a, e = math.radians(azimuth), math.radians(elevation)
        self.sun_dir = np.array([math.sin(a) * math.cos(e), math.cos(a) * math.cos(e), math.sin(e)])
        self.light = dict(sun_col=tuple(np.array(sun) * sun_power), sky_col=sky, ground_col=ground, fog_col=fog,
                          fog_density=fog_density, exposure=exposure, cloud_cover=cloud_cover, cloud_offset=tuple(cloud_offset))
        self.water_col = water

    def uniforms(self, prog, cam, vp, light_vp):
        def put(name, value):
            if name in prog:
                prog[name].value = value
        put("sun_dir", tuple(self.sun_dir))
        for k, v in self.light.items():
            put(k, v)
        put("cam_pos", tuple(cam))
        if "viewproj" in prog:
            prog["viewproj"].write(gl(vp))
        if "light_vp" in prog:
            prog["light_vp"].write(gl(light_vp))
        if "shadow_map" in prog:
            prog["shadow_map"].value = 7

    def light_matrix(self):
        lo, hi = self.bounds_min.copy(), self.bounds_max.copy()
        hi[2] = max(hi[2], self.scene["water"] + 10); lo[2] = min(lo[2], self.scene["water"] - 10)
        centre = (lo + hi) / 2
        r = np.linalg.norm(hi - lo) / 2
        eye = centre + self.sun_dir * r * 2
        view = look_at(eye, centre, up=(0, 1, 0) if abs(self.sun_dir[2]) > 0.99 else (0, 0, 1))
        return ortho(-r, r, -r, r, 0.1, r * 4) @ view

    def render(self, eye, target, fov=55, out=None):
        cool_down()
        ctx = self.ctx
        light_vp = self.light_matrix()
        # Shadows.
        self.shadow_fbo.use(); self.shadow_fbo.clear(depth=1.0)
        ctx.enable(moderngl.DEPTH_TEST); ctx.disable(moderngl.CULL_FACE | moderngl.BLEND)
        self.shadow_prog["viewproj"].write(gl(light_vp))
        for d in self.draws:
            if d["count"] == 0:
                continue
            d["tex"].use(0); self.shadow_prog["tex"].value = 0
            self.shadow_prog["alpha_mode"].value = d["alpha_mode"]; self.shadow_prog["alpha_ref"].value = d["threshold"]
            d["svao"].render(instances=d["count"])
        if self.land_vao is not None:
            self.land_shadow_prog["viewproj"].write(self.shadow_prog["viewproj"].read())
            self.land_shadow_vao.render()
        # Scene.
        self.fbo.use(); self.fbo.clear(0, 0, 0, 1, depth=1.0)
        aspect = self.size[0] / self.size[1]
        dist = np.linalg.norm(self.bounds_max - self.bounds_min)
        vp = perspective(fov, aspect, 20.0, max(dist * 3, 300000)) @ look_at(eye, target)
        self.shadow_tex.use(7)
        ctx.disable(moderngl.DEPTH_TEST)
        self.uniforms(self.sky_prog, eye, vp, light_vp)
        self.sky_prog["inv_viewproj"].write(gl(np.linalg.inv(vp)))
        self.sky_vao.render(moderngl.TRIANGLE_STRIP)
        ctx.enable(moderngl.DEPTH_TEST)
        if self.land_vao is not None:
            p = self.land_prog
            self.uniforms(p, eye, vp, light_vp)
            self.land_weights.use(1); self.land_diffuse.use(2)
            p["weights"].value, p["diffuse"].value = 1, 2
            p["layers"].value, p["grid_origin"].value, p["grid_size"].value = self.land_info
            p["tile"].value = 1024.0
            self.land_vao.render()
        self.uniforms(self.obj_prog, eye, vp, light_vp)
        blended = []
        for d in self.draws:
            if d["count"] == 0:
                continue
            if d["alpha_mode"] == 2:
                blended.append(d); continue
            d["tex"].use(0); self.obj_prog["tex"].value = 0
            self.obj_prog["alpha_mode"].value = d["alpha_mode"]; self.obj_prog["alpha_ref"].value = d["threshold"]
            d["vao"].render(instances=d["count"])
        ctx.enable(moderngl.BLEND)
        ctx.blend_func = moderngl.SRC_ALPHA, moderngl.ONE_MINUS_SRC_ALPHA
        self.uniforms(self.water_prog, eye, vp, light_vp)
        self.water_prog["water_col"].value = self.water_col
        if "time" in self.water_prog:
            self.water_prog["time"].value = getattr(self, "time", 0.0)
        ctx.depth_mask = False
        self.water_vao.render(moderngl.TRIANGLE_STRIP)
        for d in blended:
            d["tex"].use(0); self.obj_prog["tex"].value = 0
            self.obj_prog["alpha_mode"].value = 2; self.obj_prog["alpha_ref"].value = d["threshold"]
            d["vao"].render(instances=d["count"])
        ctx.depth_mask = True
        ctx.disable(moderngl.BLEND)
        img = Image.frombytes("RGBA", self.size, self.fbo.read(components=4)).transpose(Image.FLIP_TOP_BOTTOM).convert("RGB")
        if img.size != self.out_size:
            img = img.resize(self.out_size, Image.LANCZOS)
        if out:
            img.save(out)
        return img

    def overview(self, out, size=1600):
        """Straight down over the whole scene, with a grid every 8192 units, labelled, to choose cameras."""
        lo, hi = self.bounds_min, self.bounds_max
        c = (lo + hi) / 2
        r = max(hi[0] - lo[0], hi[1] - lo[1]) / 2 * 1.05
        eye = np.array([c[0], c[1], hi[2] + 50000]); target = np.array([c[0], c[1], lo[2]])
        fov = math.degrees(2 * math.atan(r / (eye[2] - c[2])))
        fog = self.light["fog_density"]
        self.light["fog_density"] = 0.0
        img = self.render(eye, target + np.array([0, 0.01, 0]), fov=fov)
        self.light["fog_density"] = fog
        from PIL import ImageDraw
        d = ImageDraw.Draw(img)
        w, h = img.size
        def px(x, y):
            return ((x - (c[0] - r * w / h)) / (2 * r * w / h) * w, (1 - (y - (c[1] - r)) / (2 * r)) * h)
        step = 8192
        for gx in range(int((c[0] - r * 2) // step) * step, int(c[0] + r * 2), step):
            x, _ = px(gx, 0); d.line([(x, 0), (x, h)], fill=(255, 255, 0), width=1); d.text((x + 3, 3), str(gx), fill=(255, 255, 0))
        for gy in range(int((c[1] - r * 2) // step) * step, int(c[1] + r * 2), step):
            _, y = px(0, gy); d.line([(0, y), (w, y)], fill=(255, 255, 0), width=1); d.text((3, y + 3), str(gy), fill=(255, 255, 0))
        img.save(out)
        print(f"overview {out}: centre {c.round()}, half-width {r:.0f}")


def lerp_path(keys, t):
    """Camera and target at time t along keys [{t, eye, target}], Catmull-Rom between keys."""
    ts = [k["t"] for k in keys]
    i = max(0, min(len(keys) - 2, np.searchsorted(ts, t) - 1))
    u = (t - ts[i]) / max(ts[i + 1] - ts[i], 1e-6)
    u = u * u * (3 - 2 * u) if keys[i].get("ease") else u
    def cr(name):
        p0 = np.array(keys[max(i - 1, 0)][name], float); p1 = np.array(keys[i][name], float)
        p2 = np.array(keys[i + 1][name], float); p3 = np.array(keys[min(i + 2, len(keys) - 1)][name], float)
        return 0.5 * ((2 * p1) + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u ** 3)
    return cr("eye"), cr("target")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("scene")
    ap.add_argument("--overview")
    ap.add_argument("--camera"); ap.add_argument("--target"); ap.add_argument("--out")
    ap.add_argument("--path"); ap.add_argument("--out-dir"); ap.add_argument("--fps", type=float, default=24)
    ap.add_argument("--size", default="1920x1080"); ap.add_argument("--ss", type=int, default=2)
    ap.add_argument("--fov", type=float, default=55)
    ap.add_argument("--light", default="{}", help="JSON of Renderer.set_light arguments")
    ap.add_argument("--only", help="with --path: just these frame numbers (comma-separated), to preview a shot")
    a = ap.parse_args()
    w, h = map(int, a.size.split("x"))
    r = Renderer(a.scene, w, h, a.ss)
    r.set_light(**json.loads(a.light))
    if a.overview:
        r.overview(a.overview)
    if a.camera:
        r.render(np.array([float(v) for v in a.camera.split(",")]), np.array([float(v) for v in a.target.split(",")]), a.fov, a.out)
        print(f"wrote {a.out}")
    if a.path:
        spec = json.load(open(a.path))
        keys = spec["keys"]
        if "light" in spec:
            r.set_light(**spec["light"])
        crowds = spec.get("crowd", [])
        for i, c in enumerate(crowds if isinstance(crowds, list) else [crowds]):
            r.add_crowd(c["count"], c["centre"], c["radius"], c.get("ground_only", True), seed=5 + i)
        os.makedirs(a.out_dir, exist_ok=True)
        frames = int(keys[-1]["t"] * a.fps)
        for f in ([int(x) for x in a.only.split(",")] if a.only else range(frames)):
            out = os.path.join(a.out_dir, f"{f:05d}.png")
            if os.path.exists(out):
                continue
            eye, target = lerp_path(keys, f / a.fps)
            r.set_time(f / a.fps)
            r.render(eye, target, spec.get("fov", a.fov), out)
            if f % 24 == 0:
                print(f"  frame {f + 1}/{frames}", flush=True)


if __name__ == "__main__":
    main()
