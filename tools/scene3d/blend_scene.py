"""Renders a scene exported by `scene-export` in Blender's EEVEE with ray tracing (user, 2026-10-08: the menu's intro
video made with real ray tracing; 2026-10-09: "flybys need rt not pt" - ray tracing, not Cycles' path tracing). Run inside Blender after blend_prep.py:

  blender -b --factory-startup --python blend_scene.py -- SCENE PATH.json OUT_DIR [--size 1200x675] [--samples 128]
          [--fps 20] [--only 0,75,149]

Builds the place in metres (a Skyrim unit is 1.428 cm): every placed mesh with its texture, vertex colours and
cut-outs, the land with its painted textures blended by weight, rippled reflective water, a physical sky lit by a sun
from the shot's light (path.json "light": azimuth from +Y toward +X, elevation, colour) and a thin haze the sun shines
through, and the town's people (user, 2026-10-08: "what happened to the npcs in the video"; 2026-10-09: "the npcs from
those areas need to walk around and be populated and not obstructing each other"): the place's own, more of them copied
about its streets, each on the ground under it, most walking a way planned ahead round walls and up stairs, at the step
of their walk that matches the distance walked, none ever closer to another than GAP. Then renders the
path's frames (eye and target keys, Catmull-Rom as render_scene.lerp_path), skipping frames already written, waiting
before each while the GPU is above 78 C.
"""
import json
import math
import os
import struct
import subprocess
import sys
import time

import bpy
import numpy as np
from mathutils import Matrix, Vector

S = 0.01428          # metres per Skyrim unit
TILE = 1024.0        # the land's texture repeat, units
FAR_LAND = 30000.0   # land cells further than this from the scene's centre are built coarser
GROUND_FADE = (4000.0, 12000.0)   # metres from the camera over which the stand-in ground past the land fades out
STEP = 30.0          # a walker's route: one point every STEP units
GAP = 70.0           # people never come closer than this to each other (Skyrim's actors are about 2 x 22 wide)
RISE = 26.0          # the most the ground may rise or fall in one step (a stair's step, Skyrim's easy 24)
TURNS = (0, 20, -20, 45, -45, 90, -90, 135, -135, 180)   # ways a walker tries, least turn first
SKY_FILL = 1.8      # the sky's light, times what Cycles needed (EEVEE: shadows lit by the sky alone)
MERGE_TILE = 16384.0 # still objects merged per material in squares this wide (EEVEE: few objects, culled by area)
CHUNK = 6            # the land in chunks of CHUNK x CHUNK cells, each with only its own layers (EEVEE: 32 textures)
MAX_LAYERS = 12      # at most this many layers a chunk (and their weight maps): the most painted


def args():
    a = sys.argv[sys.argv.index("--") + 1:]
    opts = {"size": "1200x675", "samples": "128", "fps": "20", "only": ""}
    pos = []
    i = 0
    while i < len(a):
        if a[i].startswith("--"):
            opts[a[i][2:]] = a[i + 1]; i += 2
        else:
            pos.append(a[i]); i += 1
    return pos, opts


def gpu_temperature():
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"],
                             capture_output=True, text=True, timeout=10).stdout.strip().splitlines()
        return int(out[0]) if out else None
    except Exception:
        return None


def cool_down(limit=78):
    t = gpu_temperature()
    while t is not None and t > limit:
        print(f"  GPU at {t} C, waiting to cool below {limit} C", flush=True)
        time.sleep(20)
        t = gpu_temperature()


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
        shapes.append(dict(diffuse=diffuse, alpha_mode=alpha_mode, threshold=threshold / 255.0, two_sided=bool(two_sided),
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
        for _ in range(k):
            o += 4 + 33 * 33 * 4
        cells.append((gx, gy, h))
    return cells


class Builder:
    def __init__(self, scene_dir):
        self.dir = scene_dir
        self.scene = json.load(open(os.path.join(scene_dir, "scene.json")))
        self.manifest = json.load(open(os.path.join(scene_dir, "blend", "manifest.json")))
        self.images = {}
        self.materials = {}
        self.land_tint = None             # the shot's land tint, for ground textures on meshes too (material)
        self.tint_textures = []           # texture names (how they start) that take it too: a city's own ground
        self.layer_means = {}             # land layers' average colours (linear), for layers_colour
        # Game shading (user, 2026-10-11: "then its the shader"): the place's own lighting from the game (lighting.json,
        # written by the scene export: sun, six-direction ambient, image space for each time of day). Textures are lit
        # as they are stored, as the game lights them, not converted to linear light first.
        lit = os.path.join(scene_dir, "lighting.json")
        self.game = json.load(open(lit)) if os.path.exists(lit) else None
        self.coll = bpy.context.scene.collection
        self.zmin = None

    def image(self, rel, colour=True, alpha=True):
        if not rel:
            return None
        if not alpha:
            key = rel + "#noalpha"
            if key not in self.images:
                img = bpy.data.images.load(os.path.join(self.dir, "blend", rel), check_existing=False)
                img.colorspace_settings.name = "sRGB" if colour and not self.game else "Non-Color"
                img.alpha_mode = "NONE"
                self.images[key] = img
            return self.images[key]
        if rel not in self.images:
            img = bpy.data.images.load(os.path.join(self.dir, "blend", rel))
            img.colorspace_settings.name = "sRGB" if colour and not self.game else "Non-Color"
            # The alpha kept apart from the colour: read as stored (game shading) the colour was multiplied by it, and
            # Skyrim's textures keep other things there (specular, height) - where it was 0 the colour went black
            # (user, 2026-10-11: "the black road patches are there still").
            if not colour or self.game:
                img.alpha_mode = "CHANNEL_PACKED"
            self.images[rel] = img
        return self.images[rel]

    def material(self, diffuse, alpha_mode, threshold, vertex_alpha=True, road=False, two_sided=True):
        """vertex_alpha: the vertex colours' alpha thins the texture's; not for trees and plants (user, 2026-10-09:
        "wheres the trees"): Skyrim's keep how much each leaf sways in the wind there (0.25 to 0.4 on average), which
        cut nearly every leaf away and left the trees bare."""
        tex = self.manifest["textures"].get(diffuse) if diffuse else None
        key = (tex, alpha_mode, round(threshold, 3), vertex_alpha, road, two_sided)
        if key in self.materials:
            return self.materials[key]
        m = bpy.data.materials.new(f"m{len(self.materials)}")
        m.use_nodes = True
        # One-sided shapes show only their front, as in the game (user, 2026-10-11: "you covered it not fixed it"):
        # Blender draws both sides, and the backs of Whiterun's wall tops and walls, which the game never draws, showed
        # through as black bands. Shadows keep both sides.
        m.use_backface_culling = not two_sided
        nt = m.node_tree; N = nt.nodes; L = nt.links
        bsdf = N["Principled BSDF"]
        bsdf.inputs["Roughness"].default_value = 0.78
        bsdf.inputs["Specular IOR Level"].default_value = 0.35
        vcol = N.new("ShaderNodeVertexColor"); vcol.layer_name = "Col"
        alpha = None
        if tex:
            it = N.new("ShaderNodeTexImage"); it.image = self.image(tex)
            # Texture times vertex colour, the colours only (user, 2026-10-11: "the black road patches are there still"):
            # a Mix node's multiply came out black where the vertex alpha is 0 - Skyrim paints that alpha on Whiterun's
            # street stones (wrstonefloor) to blend them into the ground, and the streets showed black blobs there.
            mul = N.new("ShaderNodeVectorMath"); mul.operation = "MULTIPLY"
            L.new(it.outputs["Color"], mul.inputs[0]); L.new(vcol.outputs["Color"], mul.inputs[1])
            colour = mul.outputs[0]
            # Skyrim's road models (their surface and dirt fringe, textured from textures\landscape\) take the shot's land
            # tint as the land does (user, 2026-10-11: "did you fix the roads in the flyby"): untinted beside the tinted
            # land, Whiterun's roads showed as black trenches. Only roads: rocks share those textures and came out white.
            # And the textures the shot names (tint_textures: a city's own ground, Whiterun's streets).
            ground = os.path.basename((diffuse or "").replace("\\", "/")).lower().startswith(tuple(self.tint_textures))
            if self.land_tint and (road or ground) and not self.game:
                tinted = N.new("ShaderNodeVectorMath"); tinted.operation = "MULTIPLY"
                tinted.inputs[1].default_value = tuple(self.land_tint)
                L.new(colour, tinted.inputs[0]); colour = tinted.outputs[0]
            L.new(colour, bsdf.inputs["Base Color"])
            alpha = N.new("ShaderNodeMath"); alpha.operation = "MULTIPLY"
            L.new(it.outputs["Alpha"], alpha.inputs[0])
            if vertex_alpha:
                L.new(vcol.outputs["Alpha"], alpha.inputs[1])
            else:
                alpha.inputs[1].default_value = 1.0
        else:
            L.new(vcol.outputs["Color"], bsdf.inputs["Base Color"])
        if alpha is not None and alpha_mode == 1:
            # Its cut-out in its shadow too (the game's shadows follow the alpha test): else the whole sheet shades what
            # is under it - Whiterun's street grass, a sheet over the stone cut away where the street is.
            if os.environ.get("TM_NO_TRANSPARENT_SHADOW") != "1":
                try:
                    m.use_transparent_shadow = True
                except AttributeError:
                    m.shadow_method = "HASHED"
            cut = N.new("ShaderNodeMath"); cut.operation = "GREATER_THAN"; cut.inputs[1].default_value = threshold
            L.new(alpha.outputs[0], cut.inputs[0]); L.new(cut.outputs[0], bsdf.inputs["Alpha"])
            if vertex_alpha:
                # Cut by its vertex alpha too: drawn blended, not dithered (user, 2026-10-11: "is it two different road
                # textures" - Whiterun's streets are stone under a grass sheet its vertex alpha cuts away). Dithered,
                # EEVEE's depth pass kept the sheet where its shading pass cut it away, hiding the stone: black blobs
                # along every street. The cut is 0 or 1, so blended draws the same edge.
                m.surface_render_method = "BLENDED"
        elif alpha is not None and alpha_mode == 2:
            L.new(alpha.outputs[0], bsdf.inputs["Alpha"])
        self.materials[key] = m
        return m

    def mesh(self, name, shape):
        pos = shape["pos"].astype(np.float64) * S
        tri = shape["tri"]
        nv, nt = len(pos), len(tri)
        me = bpy.data.meshes.new(name)
        me.vertices.add(nv); me.vertices.foreach_set("co", pos.ravel())
        me.loops.add(nt * 3); me.loops.foreach_set("vertex_index", tri.ravel())
        me.polygons.add(nt); me.polygons.foreach_set("loop_start", np.arange(0, nt * 3, 3))
        me.update(calc_edges=True)
        uv = shape["uv"][tri.ravel()].copy(); uv[:, 1] = 1.0 - uv[:, 1]
        me.uv_layers.new(name="UV").data.foreach_set("uv", uv.ravel())
        ca = me.color_attributes.new("Col", "FLOAT_COLOR", "POINT"); ca.data.foreach_set("color", shape["col"].ravel())
        me.polygons.foreach_set("use_smooth", np.ones(nt, bool))
        try:
            try:
                me.normals_split_custom_set_from_vertices(np.asarray(shape["nrm"], np.float32))
            except TypeError:
                me.normals_split_custom_set_from_vertices([tuple(n) for n in shape["nrm"]])
        except Exception:
            pass
        me.materials.append(self.material(shape["diffuse"], shape["alpha_mode"], shape["threshold"], shape.get("vertex_alpha", True), shape.get("road", False),
                                          shape.get("two_sided", True)))
        return me

    def objects(self):
        names = self.scene["meshes"]
        by_mesh = {}
        far_by_mesh = {}
        cx, cy = (self.scene.get("centre") or [0, 0])[:2]
        radius = float(self.scene.get("radius") or 0)
        for inst in self.scene["instances"]:
            if inst["m"][11] > 30000:
                continue
            m = np.array(inst["m"], np.float64).reshape(4, 4)
            far = radius > 0 and math.hypot(m[0, 3] - cx, m[1, 3] - cy) > radius
            (far_by_mesh if far else by_mesh).setdefault(inst["mesh"], []).append(m)
        self.far_objects(far_by_mesh)
        count = 0
        zs = []
        self.bases = []
        groups = {}
        for mesh_id, mats in by_mesh.items():
            path = os.path.join(self.dir, "meshes", f"{mesh_id}.bin")
            if not os.path.exists(path) or os.path.getsize(path) == 0:
                continue
            low = names[mesh_id].lower() if mesh_id < len(names) else ""
            if "\\effects\\" in low or "cloud" in low:
                continue
            # Water as meshes (streams, pools, water over stairs: Skyrim's water shader, exported as a plain white
            # texture, user 2026-10-08: white patches in Whiterun) takes the water material.
            # Not the shore's rocks and plants filed under water (Oblivion's rocks\water\rockbeach*: white streaks on
            # the Imperial City's hill, 2026-10-09), nor things named for water (Whiterun's WRWallWaterGrate01: a white
            # wedge on its wall; water wheels, mills, troughs).
            is_water = ("\\water\\" in low or "water" in os.path.basename(low)) \
                and not any(k in low for k in ("\\rocks\\", "\\plants\\", "rock", "flora", "grate", "wheel", "mill",
                                                "wall", "trough", "bucket"))
            plant = any(k in low for k in ("\\trees\\", "\\plants\\", "tree", "shrub", "bush", "fern", "grass"))
            # Skyrim's road models (meshes\landscape\roads: road surface and dirt fringe) take the land's tint.
            road = "\\landscape\\roads\\" in low
            for k, shape in enumerate(read_mesh(path)):
                # Water running down a slope (Whiterun's stairs, the Wind District's falls: over 150 units of drop)
                # as running water, not a mirror: a mirror tilted to the sky showed as a white wedge (2026-10-09).
                # And a shape without a texture in a river's, stream's, fall's or pool's model: Skyrim draws it with
                # its water shader (Whiterun's WRGreatHousePlatform01River01 rendered as a white wedge, 2026-10-09).
                # Decided by the part, not the model's name: a textured part keeps its texture unless that is a water
                # surface's (user, 2026-10-09: "with vivec why are the drains miscolored" - ex_vivec_waterspout_*,
                # stone spouts, came out as clear water, and their waterfalls lost their foam).
                tex = os.path.basename(shape["diffuse"] or "").lower()
                shape_water = (not tex and (is_water or any(w in os.path.basename(low) for w in ("river", "stream", "falls", "pool", "trough"))))                     or ("water" in tex and "fall" not in tex)
                running = shape_water and float(np.ptp(shape["pos"][:, 2])) > 150
                key = ((shape["diffuse"] or "").lower(), shape["alpha_mode"], round(float(shape["threshold"]), 3), shape_water, plant, running, road, shape["two_sided"])
                for m in mats:
                    # Placed where it stands, into the group of its material and area.
                    tile = (int(m[0, 3] // MERGE_TILE), int(m[1, 3] // MERGE_TILE))
                    groups.setdefault((key, tile), []).append((shape, m))
                    count += 1
                    zs.append(m[2, 3] + float(shape["pos"][:, 2].min()) * float(np.linalg.norm(m[:3, 2])))
                    self.bases.append((m[0, 3], m[1, 3], zs[-1]))
        # The place's still objects merged, one mesh a material and MERGE_TILE square (user, 2026-10-09: "why is it
        # going slow"): EEVEE walks every object on one CPU core for each of its samples, and 14000 objects kept the
        # graphics card waiting - 4 to 5 s a frame at a third of its clock. People stay objects of their own (they move).
        for n, ((key, _), parts) in enumerate(groups.items()):
            pos, nrm, uv, col, tri = [], [], [], [], []
            base = 0
            for shape, m in parts:
                R = m[:3, :3]
                pos.append(shape["pos"].astype(np.float64) @ R.T + m[:3, 3])
                nn = shape["nrm"].astype(np.float64) @ np.linalg.inv(R)
                nrm.append(nn / np.maximum(np.linalg.norm(nn, axis=1, keepdims=True), 1e-9))
                uv.append(shape["uv"]); col.append(shape["col"])
                t = shape["tri"].astype(np.int64)
                tri.append(base + (t[:, ::-1] if np.linalg.det(R) < 0 else t))   # mirrored: faces kept outward
                base += len(shape["pos"])
            first = parts[0][0]
            merged = dict(diffuse=first["diffuse"], alpha_mode=first["alpha_mode"], threshold=first["threshold"], vertex_alpha=not key[4], road=key[6], two_sided=key[7],
                          pos=np.vstack(pos), nrm=np.vstack(nrm).astype(np.float32), uv=np.vstack(uv),
                          col=np.vstack(col), tri=np.vstack(tri).astype(np.int32))
            me = self.mesh(f"merged{n}", merged)
            if key[3]:
                me.materials[0] = self.water_material(running=key[5])
            self.coll.objects.link(bpy.data.objects.new(me.name, me))
        # The ground's level, for a town without land: low among the objects' bases, not the lowest (cellars and
        # foundations reach below the streets: Daggerfall's grass otherwise lay far under the town).
        self.zmin = float(np.percentile(zs, 10)) if zs else 0.0
        print(f"{count} objects in {len(groups)} merged meshes", flush=True)

    # The cloud layers on the game's own cloud mesh (user, 2026-10-12: "the games have a skydome mesh"): Skyrim draws
    # a weather's cloud layer k on shape k of meshes\sky\clouds.nif (layer 0 overhead, 1 east, 2 west, ... 15-18 the
    # horizon's four bands, each on a quarter of its texture, 28 the ring on the horizon), centred on the camera. Each
    # layer's shape here, SKY_SCALE times its size (about 12 km out), follows the camera; unlit, its texture times the
    # layer's colour, see-through by the texture's, the vertices' and the layer's alpha, drifting at the layer's speed,
    # later layers in front.
    SKY_SCALE = 24.0

    def sky_clouds(self, game_all, weather):
        mesh_id = (game_all or {}).get("cloud_mesh")
        path = os.path.join(self.dir, "meshes", f"{mesh_id}.bin") if mesh_id is not None else None
        if not path or not os.path.exists(path):
            print("  sky: no cloud mesh", flush=True)
            return
        shapes = read_mesh(path)
        time = weather.get("time", "day")
        w = 1.0 if time == "day" else float(weather.get("blend", 1.0))
        tval = N_TIME()
        n = 0
        for layer in weather.get("clouds", []):
            k = layer.get("layer")
            img = layer.get("image")
            if k is None or img is None or k >= len(shapes):
                continue
            colour = blend(layer.get("day") or [1, 1, 1], layer.get(time) or [1, 1, 1], w)
            alpha = float(blend(layer.get("alpha_day") or 0, layer.get("alpha_" + time) or 0, w))
            if alpha <= 0:
                continue
            shape = dict(shapes[k]); shape["pos"] = shape["pos"] / S   # self.mesh scales by S; these are metres as given
            me = self.mesh(f"sky_layer{k}", shape)
            m = bpy.data.materials.new(f"sky_layer{k}"); m.use_nodes = True
            nt = m.node_tree; N = nt.nodes; L = nt.links
            for nd in list(N):
                if nd.type != "OUTPUT_MATERIAL":
                    N.remove(nd)
            out = next(nd for nd in N if nd.type == "OUTPUT_MATERIAL")
            uvn = N.new("ShaderNodeUVMap"); uvn.uv_map = "UV"
            drift = N.new("ShaderNodeCombineXYZ")
            for axis, key in (("X", "speed_x"), ("Y", "speed_y")):
                mul = N.new("ShaderNodeMath"); mul.operation = "MULTIPLY"; mul.inputs[1].default_value = 0.004 * float(layer.get(key) or 0)
                L.new(tval(N), mul.inputs[0]); L.new(mul.outputs[0], drift.inputs[axis])
            add = N.new("ShaderNodeVectorMath"); add.operation = "ADD"
            L.new(uvn.outputs["UV"], add.inputs[0]); L.new(drift.outputs[0], add.inputs[1])
            tex = N.new("ShaderNodeTexImage"); tex.image = img; tex.extension = "REPEAT"
            L.new(add.outputs[0], tex.inputs["Vector"])
            vc = N.new("ShaderNodeVertexColor"); vc.layer_name = "Col"
            col = N.new("ShaderNodeVectorMath"); col.operation = "MULTIPLY"; col.inputs[1].default_value = tuple(colour)
            L.new(tex.outputs["Color"], col.inputs[0])
            em = N.new("ShaderNodeEmission"); L.new(col.outputs[0], em.inputs["Color"])
            a1 = N.new("ShaderNodeMath"); a1.operation = "MULTIPLY"; L.new(tex.outputs["Alpha"], a1.inputs[0]); L.new(vc.outputs["Alpha"], a1.inputs[1])
            a2 = N.new("ShaderNodeMath"); a2.operation = "MULTIPLY"; a2.inputs[1].default_value = alpha; L.new(a1.outputs[0], a2.inputs[0])
            tr = N.new("ShaderNodeBsdfTransparent")
            mix = N.new("ShaderNodeMixShader"); L.new(a2.outputs[0], mix.inputs["Fac"])
            L.new(tr.outputs[0], mix.inputs[1]); L.new(em.outputs[0], mix.inputs[2])
            L.new(mix.outputs[0], out.inputs["Surface"])
            m.use_backface_culling = False
            try:
                m.surface_render_method = "BLENDED"
            except Exception:
                m.blend_method = "BLEND"
            me.materials[0] = m
            ob = bpy.data.objects.new(f"sky_layer{k}", me); self.coll.objects.link(ob)
            ob.scale = (self.SKY_SCALE,) * 3
            ob.visible_shadow = False
            try:
                ob.visible_volume_scatter = False
            except AttributeError:
                pass
            # Blended surfaces are drawn far to near by their origins: later layers a little nearer, so in front.
            SKY_OBJECTS.append((ob, (28 - k) * 0.01))
            n += 1
        print(f"  sky: {n} cloud layers on the game's cloud mesh", flush=True)

    def far_objects(self, by_mesh):
        """The trees and mountains past the scene's radius (the scene export's distant view) as instances: each model's
        geometry once, every copy placed by its own matrix. Merged like the near objects they were copied thousands of
        times over (Whiterun's 6418 trees): Blender reached 22 GB of the machine's 32 and the render stopped
        (2026-10-11)."""
        names = self.scene["meshes"]
        n = 0
        for mesh_id, mats in by_mesh.items():
            path = os.path.join(self.dir, "meshes", f"{mesh_id}.bin")
            if not os.path.exists(path) or os.path.getsize(path) == 0:
                continue
            low = names[mesh_id].lower() if mesh_id < len(names) else ""
            plant = any(k in low for k in ("\\trees\\", "\\plants\\", "tree", "shrub", "bush", "fern", "grass", "parasol"))
            M = np.stack(mats)
            pos = M[:, :3, 3]
            scale = np.linalg.norm(M[:, :3, 0], axis=1)
            R = M[:, :3, :3] / np.maximum(scale, 1e-9)[:, None, None]
            # R = Rz Ry Rx (Blender's XYZ Euler).
            rot = np.stack([np.arctan2(R[:, 2, 1], R[:, 2, 2]), -np.arcsin(np.clip(R[:, 2, 0], -1, 1)), np.arctan2(R[:, 1, 0], R[:, 0, 0])], 1)
            pm = bpy.data.meshes.new(f"far_points{mesh_id}")
            pm.vertices.add(len(pos)); pm.vertices.foreach_set("co", (pos * S).astype(np.float32).ravel())
            pm.attributes.new("rot", "FLOAT_VECTOR", "POINT").data.foreach_set("vector", rot.astype(np.float32).ravel())
            pm.attributes.new("scl", "FLOAT_VECTOR", "POINT").data.foreach_set("vector", np.repeat(scale[:, None], 3, 1).astype(np.float32).ravel())
            for k, shape in enumerate(read_mesh(path)):
                shape["vertex_alpha"] = not plant
                me = self.mesh(f"far{mesh_id}_{k}", shape)
                src = bpy.data.objects.new(f"far_src{mesh_id}_{k}", me); self.coll.objects.link(src)
                src.hide_render = True; src.hide_viewport = True
                ob = bpy.data.objects.new(f"far{mesh_id}_{k}", pm); self.coll.objects.link(ob)
                ob.modifiers.new("far", "NODES").node_group = self.grass_nodes(src, realize=False)
            n += len(mats)
        if n:
            print(f"  far objects: {n} as instances of {len(by_mesh)} models", flush=True)

    def people(self):
        """The place's people: each a few posed parts (body, clothes, head), standing or at a step of the walk cycle.
        Four in five of those with walk steps walk (seed 11), 60 to 85 units a second. Each person gets a pool of
        objects whose meshes are swapped for the step shown (set_time); their routes come from plan_walks."""
        people = self.scene.get("people", [])
        names = self.scene["meshes"]
        rng = np.random.default_rng(11)
        self.shapes = {}
        self.crowd = []
        for i, p in enumerate(people):
            m = np.array(p["m"], np.float64).reshape(4, 4)
            variants = [[me for mid in p["parts"] for me in self.shapes_of(mid)]]
            variants += [[me for mid in step for me in self.shapes_of(mid)] for step in (p.get("walk") or [])]
            pool = []
            for k in range(max((len(v) for v in variants), default=0)):
                pool.append(self.person_object(f"person{i}_{k}", next(v[k] for v in variants if k < len(v))))
            human = any("character assets" in names[j].lower() for j in p["parts"] if j < len(names))
            self.crowd.append(dict(m=m, walks=len(variants) > 1 and rng.random() < 0.8, speed=rng.uniform(60, 85),
                                   heading=None, phase=rng.uniform(0, 1), variants=variants, pool=pool, human=human))
        print(f"{len(self.crowd)} people, {sum(len(c['pool']) for c in self.crowd)} objects", flush=True)

    def shapes_of(self, mesh_id):
        if mesh_id not in self.shapes:
            path = os.path.join(self.dir, "meshes", f"{mesh_id}.bin")
            self.shapes[mesh_id] = ([self.mesh(f"p{mesh_id}_{k}", sh) for k, sh in enumerate(read_mesh(path))]
                                    if os.path.exists(path) and os.path.getsize(path) > 0 else [])
        return self.shapes[mesh_id]

    def person_object(self, name, data):
        """A person's part: hidden from the viewport (rays cast for the ground and routes pass through people), shown
        in renders."""
        ob = bpy.data.objects.new(name, data)
        ob.hide_viewport = True
        self.coll.objects.link(ob)
        return ob

    def caster(self):
        """Ray casts in game units into the place (its people left out): cast(origin, direction, distance) -> hit
        point or None, and the hit's normal's z."""
        bpy.context.view_layer.update()
        dg = bpy.context.evaluated_depsgraph_get()
        sc = bpy.context.scene

        def cast(o, d, dist):
            hit, loc, nrm, _, _, _ = sc.ray_cast(dg, Vector((o[0] * S, o[1] * S, o[2] * S)), Vector(d), distance=dist * S)
            return (np.array(loc, np.float64) / S, nrm.z) if hit else (None, 0.0)
        return cast

    def ground_at(self, cast, x, y, z_from, reach=400.0):
        """The ground's height under (x, y) cast down from z_from, and how level it is."""
        hit, nz = cast((x, y, z_from), (0, 0, -1), reach)
        return (float(hit[2]), nz) if hit is not None else (None, 0.0)

    def water_z(self):
        w = self.scene.get("water", -1e9)
        return w if w > -10000 else -1e9

    def humans(self):
        own = [c for c in self.crowd if c["human"]]
        return own or [c for c in self.crowd if len(c["variants"]) > 1]

    def copy_person(self, src, x, y, z, heading, rng, tag):
        """One more of the place's own people (src's race and clothes) at (x, y, z) facing heading."""
        m = src["m"].copy()
        scale = np.linalg.norm(m[:3, 0])
        yaw = math.atan2(-heading[0], heading[1])      # facing +Y turned to the heading
        c, s_ = math.cos(yaw), math.sin(yaw)
        m[:3, :3] = np.array([[c, -s_, 0], [s_, c, 0], [0, 0, 1]]) * scale
        m[:3, 3] = [x, y, z]
        pool = [self.person_object(f"person_{tag}_{k}", src["pool"][k].data) for k in range(len(src["pool"]))]
        self.crowd.append(dict(m=m, walks=len(src["variants"]) > 1 and rng.random() < 0.85, speed=rng.uniform(60, 85),
                               heading=np.array(heading, np.float64), phase=rng.uniform(0, 1), variants=src["variants"],
                               pool=pool, human=src["human"]))

    def open_street(self, cast, x, y, z):
        """Ground out of doors: nothing overhead for 4000 units (not inside a house or under an arch's whole span)."""
        return cast((x, y, z + 20), (0, 0, 1), 4000)[0] is None

    def add_crowd(self, spec, seed=5):
        """More of the town's own people (copies of those placed, so the same races and clothes; not its animals)
        along a street: spec {"line": [[x, y], [x, y]], "width": units, "count": n}. Each spot is found by casting a
        ray down; only open, level ground at the street's height is used (not roofs or walls). Most walk along the
        street, either way."""
        humans = self.humans()
        if not humans:
            print("  crowd: no people to copy", flush=True); return
        rng = np.random.default_rng(seed)
        cast = self.caster()
        (x0, y0), (x1, y1) = spec["line"]
        along = np.array([x1 - x0, y1 - y0], np.float64); length = np.linalg.norm(along); along /= length
        side = np.array([-along[1], along[0]])
        top = max(b[2] for b in self.bases) + 2000 if self.bases else 60000
        probes = [self.ground_at(cast, *(np.array([x0, y0]) + along * length * u), top, 1e6) for u in np.linspace(0, 1, 60)]
        zs = sorted(z for z, nz in probes if z is not None and nz > 0.9)
        if not zs:
            print("  crowd: no ground along the street", flush=True); return
        street = zs[len(zs) // 5]                     # the street's height: low among the level hits
        added = tries = 0
        while added < spec["count"] and tries < spec["count"] * 30:
            tries += 1
            u, v = rng.random(), rng.uniform(-0.5, 0.5)
            x, y = np.array([x0, y0]) + along * length * u + side * spec["width"] * v
            z, nz = self.ground_at(cast, x, y, street + 300, 700)
            if z is None or nz < 0.94 or abs(z - street) > 160 or not self.open_street(cast, x, y, z):
                continue
            heading = along if rng.random() < 0.5 else -along
            if rng.random() < 0.15:                   # a few cross or stand about
                a = rng.uniform(0, 2 * math.pi); heading = np.array([math.cos(a), math.sin(a)])
            self.copy_person(humans[int(rng.integers(len(humans)))], x, y, z, heading, rng, f"c{added}")
            added += 1
        print(f"  crowd: {added} more people along the street (height {street:.0f})", flush=True)

    def populate(self, count, seed=9):
        """More of the place's own people about the ones it has (user, 2026-10-09: "populated"): copies of them on
        open, level ground 150 to 2000 units from one of them and at about its height (its street, not a roof)."""
        humans = self.humans()
        if not humans or count <= 0:
            return
        rng = np.random.default_rng(seed)
        cast = self.caster()
        water = self.water_z()
        added = tries = 0
        while added < count and tries < count * 40:
            tries += 1
            a = humans[int(rng.integers(len(humans)))]
            ax, ay, az = a["m"][:3, 3]
            r, ang = rng.uniform(150, 2000), rng.uniform(0, 2 * math.pi)
            x, y = ax + math.cos(ang) * r, ay + math.sin(ang) * r
            z, nz = self.ground_at(cast, x, y, az + 150, 400)
            if z is None or nz < 0.94 or abs(z - az) > 80 or z < water + 8 or not self.open_street(cast, x, y, z):
                continue
            h = rng.uniform(0, 2 * math.pi)
            self.copy_person(humans[int(rng.integers(len(humans)))], x, y, z, (math.cos(h), math.sin(h)), rng, f"p{added}")
            added += 1
        print(f"  populate: {added} more people", flush=True)

    def route(self, cast, start, heading, distance):
        """A walker's way: STEP units at a time along its heading over ground rising or falling at most RISE a step
        (stairs, slopes), turning the least way where a wall (anything at knee or head height) or a drop is ahead; it
        stops where nothing is clear. The points, ground heights included."""
        water = self.water_z()
        pts = [np.array(start, np.float64)]
        d = np.array(heading[:2], np.float64); d /= np.linalg.norm(d)
        while (len(pts) - 1) * STEP < distance:
            cur = pts[-1]; nxt = None
            for a in TURNS:
                c, s_ = math.cos(math.radians(a)), math.sin(math.radians(a))
                dd = np.array([d[0] * c - d[1] * s_, d[0] * s_ + d[1] * c])
                if any(cast(cur + [0, 0, h], (dd[0], dd[1], 0), STEP + 40)[0] is not None for h in (40, 100)):
                    continue
                x, y = cur[:2] + dd * STEP
                z, nz = self.ground_at(cast, x, y, cur[2] + 60, 60 + RISE + 10)
                if z is None or nz < 0.8 or abs(z - cur[2]) > RISE or z < water + 8:
                    continue
                nxt = np.array([x, y, z]); d = dd
                break
            if nxt is None:
                break
            pts.append(nxt)
        return np.array(pts)

    @staticmethod
    def along(route, speed, t):
        """Where on its route a walker is t seconds in (an array of times gives an array of places)."""
        cum = np.arange(len(route)) * STEP
        d = np.minimum(speed * np.asarray(t, np.float64), cum[-1])
        return np.stack([np.interp(d, cum, route[:, k]) for k in range(3)], -1)

    def plan_walks(self, duration):
        """Every person's way through the shot, planned before it renders: each stands on the ground under it, and a
        walker follows its route (round walls, up stairs) - unless that would bring it within GAP of anyone already
        planned at any moment, when it tries other ways, then standing; one who'd stand in another's way is left out.
        The place's own people come first (the game put them there), then the copies."""
        cast = self.caster()
        ts = np.arange(0, duration + 0.75, 0.25)
        rng = np.random.default_rng(17)
        tracks = np.zeros((0, len(ts), 3))
        kept, walking = [], 0
        for c in self.crowd:
            m = c["m"]
            x, y, z0 = m[:3, 3]
            z, _ = self.ground_at(cast, x, y, z0 + 60, 200)
            if z is not None:
                m[2, 3] = z
            start = m[:3, 3].copy()
            options = []
            if c["walks"]:
                fwd = c["heading"] if c["heading"] is not None else (m[:3, :3] @ np.array([0, 1, 0.0]))[:2]
                if np.linalg.norm(fwd) > 1e-6:
                    base = math.atan2(fwd[1], fwd[0])
                    for a in (0, math.pi, math.pi / 2, -math.pi / 2, rng.uniform(0, 2 * math.pi)):
                        options.append((math.cos(base + a), math.sin(base + a)))
            options.append(None)
            for h in options:
                if h is None:
                    route = start[None, :]
                else:
                    route = self.route(cast, start, h, c["speed"] * (duration + 0.75))
                    if len(route) < 4:
                        continue                      # nowhere to go that way
                track = self.along(route, c["speed"], ts)
                if len(tracks) and np.linalg.norm(tracks - track[None], axis=2).min() < GAP:
                    continue
                c["route"] = route
                tracks = np.concatenate([tracks, track[None]])
                kept.append(c); walking += h is not None
                break
            else:
                for ob in c["pool"]:
                    bpy.data.objects.remove(ob)
        print(f"  people: {len(kept)} kept ({walking} walking), {len(self.crowd) - len(kept)} left out (in another's way)",
              flush=True)
        self.crowd = kept

    def set_time(self, t):
        """Everyone where they are t seconds in: walkers along their route facing the way it goes, at the step of
        their walk for the distance walked, with a slight bob; those at their route's end (or standing) standing."""
        for c in self.crowd:
            m = c["m"].copy()
            shown = c["variants"][0]
            route = c.get("route")
            if route is not None and len(route) > 1:
                d = c["speed"] * t
                p = self.along(route, c["speed"], t)
                m[:3, 3] = p
                if d < (len(route) - 1) * STEP:
                    ahead = self.along(route, 1.0, d + 25) - self.along(route, 1.0, max(d - 25, 0))
                    if np.hypot(ahead[0], ahead[1]) > 1e-3:
                        scale = np.linalg.norm(c["m"][:3, 0])
                        yaw = math.atan2(-ahead[0], ahead[1])
                        co, si = math.cos(yaw), math.sin(yaw)
                        m[:3, :3] = np.array([[co, -si, 0], [si, co, 0], [0, 0, 1]]) * scale
                    cycle = d / 90.0 + c["phase"]
                    steps = c["variants"][1:]
                    if steps:
                        shown = steps[int(cycle * len(steps)) % len(steps)]
                    m[2, 3] += 1.2 * abs(math.sin(cycle * 2 * math.pi))
            w = m.copy(); w[:3, 3] *= S
            mw = Matrix(w.tolist())
            for k, ob in enumerate(c["pool"]):
                if k < len(shown):
                    ob.data = shown[k]
                    ob.matrix_world = mw
                    ob.hide_render = False
                else:
                    ob.hide_render = True

    def land(self, tint=None):
        """The land, its painted layers blended by weight; tint multiplies its colour (Skyrim's ground textures are
        dark under the grass the game grows on them, which no export has)."""
        path = os.path.join(self.dir, "land.bin")
        info = self.manifest.get("land")
        if not info or not os.path.exists(path):
            return False
        cells = read_land(path)
        self.land_raw = {(gx, gy): np.asarray(h).reshape(33, 33) for gx, gy, h in cells}   # for grass
        ox, oy = info["origin"]; W, H = info["size"]
        verts, faces, uvs = [], [], []
        base = 0
        q = np.arange(33 * 33).reshape(33, 33)
        a, b, c, d = q[:-1, :-1].ravel(), q[:-1, 1:].ravel(), q[1:, :-1].ravel(), q[1:, 1:].ravel()
        # Cells far from the place (beyond FAR_LAND units) at a quarter of their points (9 x 9): the wide land of a
        # scene (land_radius) costs Cycles little memory then, and haze covers the difference.
        cx, cy = (self.scene.get("centre") or [0, 0])[:2]
        q9 = np.arange(9 * 9).reshape(9, 9)
        a9, b9, c9, d9 = q9[:-1, :-1].ravel(), q9[:-1, 1:].ravel(), q9[1:, :-1].ravel(), q9[1:, 1:].ravel()
        def is_far(gx, gy):
            return math.hypot(gx * 4096 + 2048 - cx, gy * 4096 + 2048 - cy) > FAR_LAND
        have_cells = {(gx, gy) for gx, gy, _ in cells}

        def coarse_edge(e):
            """An edge of 33 heights as a coarse neighbour draws it: straight between every fourth point."""
            e = e.copy()
            for k in range(0, 32, 4):
                e[k:k + 5] = np.linspace(e[k], e[k + 4], 5)
            return e
        for gx, gy, h in cells:
            h = np.asarray(h).reshape(33, 33)
            far = is_far(gx, gy)
            if not far:
                # Where a coarse cell meets this one, this edge follows the coarse one's straight runs (user, 2026-10-11:
                # a black crack down the Imperial City's hillside, where the camera's foreground crossed FAR_LAND):
                # the two meshes then meet exactly. Rows are y, columns x (the land's 33 x 33 points).
                h = h.copy()
                for (dx, dy), (rows, cols) in {(1, 0): (slice(None), 32), (-1, 0): (slice(None), 0),
                                                 (0, 1): (32, slice(None)), (0, -1): (0, slice(None))}.items():
                    if (gx + dx, gy + dy) in have_cells and is_far(gx + dx, gy + dy):
                        h[rows, cols] = coarse_edge(h[rows, cols])
            step = 4 if far else 1
            ys, xs = np.mgrid[0:33:step, 0:33:step]
            px, py = gx * 4096 + xs * 128.0, gy * 4096 + ys * 128.0
            verts.append(np.dstack([px, py, h[::step, ::step]]).reshape(-1, 3))
            faces.append(base + (np.stack([a9, b9, d9, c9], 1) if far else np.stack([a, b, d, c], 1)))
            base += 81 if far else 33 * 33
        v = np.vstack(verts); f = np.vstack(faces)
        # Round a placed town (scene.json "holes"): the land levelled to the town's ground, as Daggerfall levels the
        # land at its places, easing back to its own heights over BLEND units - else the town, flat in its own
        # worldspace, sat in a pit or stood on a shelf in the overland's hills (user, 2026-10-09: "ground sinks/floats").
        v[:, 2] = self.levelled(v[:, 0], v[:, 1], v[:, 2])
        self.land_min = float(v[:, 2].min())
        self.land_cells = {(gx, gy) for gx, gy, _ in cells}
        # The level of the land's outer edge (its border cells, low among them): ground beyond it continues from there.
        have = {(gx, gy) for gx, gy, _ in cells}
        edge = [h for gx, gy, h in cells if any((gx + dx, gy + dy) not in have for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)))]
        self.land_edge = float(np.percentile(np.concatenate([np.asarray(h).ravel() for h in edge]), 20)) if edge else self.land_min
        # In chunks of CHUNK x CHUNK cells, each with a material of only the layers painted there (user, 2026-10-09:
        # EEVEE for the flybys - a material may use 32 textures, and a whole land's layers and weight maps are more).
        face_cell = np.repeat(np.array([(gx, gy) for gx, gy, _ in cells]),
                              [len(fc) for fc in faces], axis=0)
        chunk_of = (face_cell // CHUNK) @ np.array([1, 100003])
        weights = [self.pixels(g["image"]) for g in info["groups"]]
        materials = {}
        for key in np.unique(chunk_of):
            sel = f[chunk_of == key]
            cxy = face_cell[chunk_of == key]
            # The layers painted in the chunk (weight over 1%), the most painted first, at most MAX_LAYERS.
            px0 = int((cxy[:, 0].min() * 4096 - ox + 64) // 128) - 1; px1 = int(((cxy[:, 0].max() + 1) * 4096 - ox + 64) // 128) + 2
            py0 = int((cxy[:, 1].min() * 4096 - oy + 64) // 128) - 1; py1 = int(((cxy[:, 1].max() + 1) * 4096 - oy + 64) // 128) + 2
            used = []
            for gi, g in enumerate(info["groups"]):
                w = weights[gi][max(py0, 0):max(py1, 0), max(px0, 0):max(px1, 0)]
                for ci, layer in enumerate(g["layers"]):
                    if layer and w.size and w[..., ci].max() > 0.01:
                        used.append((float(w[..., ci].mean()), gi, ci))
            # Roads first (user, 2026-10-11: "its roads almost like oblivion had its roads"): Skyrim's and Oblivion's roads
            # are painted on the land, thin strips whose layers average little over a chunk (Whiterun's dirtpath01 15th
            # of 21 in the city's chunk, the Imperial City's cobblestone01 14th of 25), so ranked by weight alone they
            # were left out. Then the most painted.
            ranked = sorted(used, key=lambda u: (not self.is_road_layer(info, u[1], u[2]), -u[0]))
            used = tuple(sorted((gi, ci) for _, gi, ci in ranked[:MAX_LAYERS]))
            # The layers left out, as one colour (their textures' average, by how much each is painted here): it fills
            # the weight they leave (user, 2026-10-11: "thats a road" - the Imperial City's cobbled road, a thin strip
            # whose layer averages little over a chunk, was left out and the road went black).
            fill = self.layers_colour(info, ranked[MAX_LAYERS:])
            if (used, fill) not in materials:
                materials[(used, fill)] = self.land_material(info, used, tint, fill)
            mat_key = (used, fill)
            idx, local = np.unique(sel.ravel(), return_inverse=True)
            cv = v[idx]; cf = local.reshape(-1, 4)
            me = bpy.data.meshes.new("land")
            me.vertices.add(len(cv)); me.vertices.foreach_set("co", (cv * S).ravel())
            me.loops.add(len(cf) * 4); me.loops.foreach_set("vertex_index", cf.ravel())
            me.polygons.add(len(cf)); me.polygons.foreach_set("loop_start", np.arange(0, len(cf) * 4, 4))
            me.update(calc_edges=True)
            lv = cv[cf.ravel()]
            uv = np.stack([(lv[:, 0] - ox + 64) / (W * 128.0), (lv[:, 1] - oy + 64) / (H * 128.0)], 1)
            me.uv_layers.new(name="Weights").data.foreach_set("uv", uv.ravel())
            me.polygons.foreach_set("use_smooth", np.ones(len(cf), bool))
            me.materials.append(materials[mat_key])
            ob = bpy.data.objects.new("land", me); self.coll.objects.link(ob)
        print(f"  land: {len(np.unique(chunk_of))} chunks, {len(materials)} materials", flush=True)
        print(f"land: {len(cells)} cells", flush=True)
        return True

    def levelled(self, x, y, z):
        """Heights z at x, y levelled round a placed town (scene.json "holes") to its ground, easing back to the land's
        own over BLEND units."""
        holes = self.scene.get("holes", [])
        if not holes:
            return z
        BLEND = 8192.0
        hx = [h[0] for h in holes]; hy = [h[1] for h in holes]
        X0, X1, Y0, Y1 = min(hx) * 4096, (max(hx) + 1) * 4096, min(hy) * 4096, (max(hy) + 1) * 4096
        dx = np.maximum(np.maximum(X0 - x, x - X1), 0)
        dy = np.maximum(np.maximum(Y0 - y, y - Y1), 0)
        u = np.clip(np.hypot(dx, dy) / BLEND, 0, 1)
        u = u * u * (3 - 2 * u)
        return self.town_level() + (z - self.town_level()) * u

    # Grass as the game grows it (user, 2026-10-11: "do the grass"): on a grid GRASS_STEP units apart (the game's
    # iMinGrassSize), each point takes a land texture by how much of it is painted there, then the first of that texture's
    # grasses (LTEX -> GRAS) its density lets grow, if the slope and water allow; moved up to the grass's position range,
    # scaled by its height range, shaded by its colour range, tilted to the slope when it fits to slope. Drawn as instances
    # (one shape's geometry shared by every copy) and casting no shadows, as the game's grass.
    GRASS_STEP = 60.0
    # At most this many (wider apart when more would grow): made real geometry, Daggerfall's 524000 over its 46000-unit
    # radius took Blender to 21 GB of the machine's 32 and the render stopped (2026-10-11).
    GRASS_MAX = 300000

    def grass(self, opts=None):
        opts = opts if isinstance(opts, dict) else {}
        terr = self.scene.get("terrain") or {}
        lists = terr.get("grasses") or []
        info = self.manifest.get("land")
        if not any(lists) or not info or not getattr(self, "land_raw", None):
            print("  grass: none (no land, or no land texture grows any)", flush=True)
            return
        ox, oy = info["origin"]; W, H = info["size"]
        # The land's heights on one grid, a point every 128 units (the weight maps' grid: column i at x = ox + 128 i).
        hgrid = np.full((H, W), np.nan, np.float32)
        cx, cy = (self.scene.get("centre") or [0, 0])[:2]
        q = np.linspace(0, 1, 5)
        for (gx, gy), h in self.land_raw.items():
            if math.hypot(gx * 4096 + 2048 - cx, gy * 4096 + 2048 - cy) > FAR_LAND:
                # A far cell as its coarse mesh has it (every fourth point, straight between), so grass sits on it.
                c = h[::4, ::4]
                h = np.empty((33, 33), np.float32)
                for r in range(33):
                    rr, ru = min(r // 4, 7), (r - min(r // 4, 7) * 4) / 4.0
                    row = c[rr] * (1 - ru) + c[rr + 1] * ru
                    for k in range(8):
                        h[r, k * 4:k * 4 + 5] = row[k] * (1 - q) + row[k + 1] * q
            c0 = int(round((gx * 4096 - ox) / 128)); r0 = int(round((gy * 4096 - oy) / 128))
            rs, cs = slice(max(r0, 0), min(r0 + 33, H)), slice(max(c0, 0), min(c0 + 33, W))
            hgrid[rs, cs] = h[rs.start - r0:rs.stop - r0, cs.start - c0:cs.stop - c0]

        def bilinear(a, x, y):
            fx = np.clip((x - ox) / 128.0, 0, a.shape[1] - 1.001); fy = np.clip((y - oy) / 128.0, 0, a.shape[0] - 1.001)
            i = fx.astype(np.int64); j = fy.astype(np.int64); u = fx - i; v = fy - j
            return (a[j, i] * (1 - u) * (1 - v) + a[j, i + 1] * u * (1 - v) + a[j + 1, i] * (1 - u) * v + a[j + 1, i + 1] * u * v)

        def height(x, y):
            return self.levelled(x, y, bilinear(hgrid, x, y))

        # Each grass-bearing texture's weight map channel.
        rel = {k.lower(): v for k, v in self.manifest["textures"].items()}
        where = {}
        for gi, g in enumerate(info["groups"]):
            for ci, l in enumerate(g["layers"]):
                if l:
                    where.setdefault(l, (gi, ci))
        maps = {}
        types = []
        for path, gl in zip(terr["textures"], lists):
            loc = where.get(rel.get(path.lower()))
            if not gl or loc is None:
                continue
            if loc[0] not in maps:
                maps[loc[0]] = self.pixels(info["groups"][loc[0]]["image"])
            types.append((maps[loc[0]][..., loc[1]], gl))
        if not types:
            print("  grass: none (the textures that grow it aren't painted here)", flush=True)
            return
        R = float(opts.get("radius", self.scene.get("radius") or 22000.0))
        step = float(opts.get("step", self.GRASS_STEP))
        cap = int(opts.get("max", self.GRASS_MAX))
        # Wider apart when the circle would hold too many points (each point grows at most one grass).
        step = max(step, math.sqrt(math.pi * R * R / (2.5 * cap)))
        xs = np.arange(cx - R, cx + R, step); ys = np.arange(cy - R, cy + R, step)
        X, Y = np.meshgrid(xs, ys); X = X.ravel(); Y = Y.ravel()
        keep = np.hypot(X - cx, Y - cy) <= R
        X, Y = X[keep], Y[keep]
        keep = ~np.isnan(bilinear(hgrid, X, Y))
        X, Y = X[keep], Y[keep]
        rnd = np.random.default_rng(23)
        # The texture each point takes: by the painted weights there (those without grass take the rest).
        ws = np.stack([bilinear(m, X, Y) for m, _ in types], 1)
        cum = np.cumsum(ws, 1)
        r = rnd.random(len(X))
        tex = np.where(r[:, None] < cum, np.arange(len(types))[None, :], len(types)).min(1)
        water = self.water_z()
        placed = {}   # mesh id -> lists
        for ti, (_, gl) in enumerate(types):
            sel = np.nonzero(tex == ti)[0]
            left = sel
            for g in gl:
                if len(left) == 0:
                    break
                grow = rnd.random(len(left)) < g["density"] / 100.0
                pts, left = left[grow], left[~grow]
                n = len(pts)
                pr = float(g.get("position_range") or 0)
                x = X[pts] + rnd.uniform(-pr, pr, n); y = Y[pts] + rnd.uniform(-pr, pr, n)
                z = height(x, y)
                dzx = (height(x + 32, y) - height(x - 32, y)) / 64.0; dzy = (height(x, y + 32) - height(x, y - 32)) / 64.0
                nrm = np.stack([-dzx, -dzy, np.ones(n)], 1); nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)
                slope = np.degrees(np.arccos(np.clip(nrm[:, 2], -1, 1)))
                ok = (slope >= g.get("min_slope", 0)) & (slope <= max(g.get("max_slope", 90), g.get("min_slope", 0)))
                wd = float(g.get("water") or 0)
                ok &= (z <= water - wd) if g.get("water_type", "").startswith("Below") else (z >= water + wd)
                ok &= ~np.isnan(z)
                if not ok.any():
                    continue
                x, y, z, nrm = x[ok], y[ok], z[ok], nrm[ok]; n = len(x)
                hr = float(g.get("height_range") or 0); cr = float(g.get("color_range") or 0)
                s = 1 + rnd.uniform(-hr, hr, n)
                scl = np.stack([s, s, s], 1) if g.get("uniform_scale") else np.stack([np.ones(n), np.ones(n), s], 1)
                yaw = rnd.uniform(0, 2 * math.pi, n)
                tilt = nrm if g.get("fit_to_slope") else np.tile([0.0, 0.0, 1.0], (n, 1))
                d = placed.setdefault(g["mesh"], {"pos": [], "rot": [], "scl": [], "tint": []})
                d["pos"].append(np.stack([x, y, z], 1)); d["rot"].append(self.grass_euler(tilt, yaw)); d["scl"].append(scl)
                d["tint"].append(1 - rnd.uniform(0, cr, n))
        total = sum(sum(len(p) for p in d["pos"]) for d in placed.values())
        if total == 0:
            print(f"  grass: none grew ({len(X)} points, {len(types)} textures with grass)", flush=True)
            return
        for mesh_id, d in placed.items():
            path = os.path.join(self.dir, "meshes", f"{mesh_id}.bin")
            if not os.path.exists(path):
                continue
            pos = np.vstack(d["pos"]); rot = np.vstack(d["rot"]); scl = np.vstack(d["scl"]); tint = np.concatenate(d["tint"])
            if total > cap:
                k = rnd.random(len(pos)) < cap / total
                pos, rot, scl, tint = pos[k], rot[k], scl[k], tint[k]
            # In MERGE_TILE squares, one object each: smaller meshes to make real (Daggerfall's 524000 grass in five
            # meshes crashed Blender once, in its threaded geometry code) and culled by area.
            tile = np.floor(pos[:, :2] / MERGE_TILE).astype(np.int64)
            keys = tile[:, 0] * 100003 + tile[:, 1]
            shapes = []
            for k, shape in enumerate(read_mesh(path)):
                shape["vertex_alpha"] = False   # grass keeps its sway there, as trees do
                me = self.mesh(f"grass{mesh_id}_{k}", shape)
                me.materials[0] = self.grass_material(me.materials[0])
                src = bpy.data.objects.new(f"grass_src{mesh_id}_{k}", me); self.coll.objects.link(src)
                src.hide_render = True; src.hide_viewport = True
                shapes.append((k, self.grass_nodes(src)))
            for key in np.unique(keys):
                sel = keys == key
                pm = bpy.data.meshes.new(f"grass_points{mesh_id}")
                pm.vertices.add(int(sel.sum())); pm.vertices.foreach_set("co", (pos[sel] * S).astype(np.float32).ravel())
                for name, kind, data in (("rot", "FLOAT_VECTOR", rot[sel]), ("scl", "FLOAT_VECTOR", scl[sel]), ("grass_tint", "FLOAT", tint[sel])):
                    pm.attributes.new(name, kind, "POINT").data.foreach_set("vector" if kind == "FLOAT_VECTOR" else "value",
                                                                           data.astype(np.float32).ravel())
                for k, nodes in shapes:
                    ob = bpy.data.objects.new(f"grass{mesh_id}_{k}", pm); self.coll.objects.link(ob)
                    ob.visible_shadow = False
                    mod = ob.modifiers.new("grass", "NODES"); mod.node_group = nodes
        print(f"  grass: {min(total, cap)} in {len(placed)} kinds, {step:.0f} units apart within {R:.0f}", flush=True)

    @staticmethod
    def grass_euler(nrm, yaw):
        """XYZ Euler angles turning a grass about its own up by yaw, then tilting its up onto nrm (one row each)."""
        n = len(yaw)
        c, s = np.cos(yaw), np.sin(yaw)
        Rz = np.zeros((n, 3, 3)); Rz[:, 0, 0] = c; Rz[:, 0, 1] = -s; Rz[:, 1, 0] = s; Rz[:, 1, 1] = c; Rz[:, 2, 2] = 1
        # Rodrigues: up (0, 0, 1) onto nrm.
        k = np.stack([-nrm[:, 1], nrm[:, 0], np.zeros(n)], 1)
        sa = np.linalg.norm(k, axis=1); ca = nrm[:, 2]
        k = k / np.maximum(sa, 1e-9)[:, None]
        K = np.zeros((n, 3, 3))
        K[:, 0, 1] = -k[:, 2]; K[:, 0, 2] = k[:, 1]; K[:, 1, 0] = k[:, 2]; K[:, 1, 2] = -k[:, 0]; K[:, 2, 0] = -k[:, 1]; K[:, 2, 1] = k[:, 0]
        Rt = np.eye(3)[None] + sa[:, None, None] * K + (1 - ca)[:, None, None] * (K @ K)
        R = Rt @ Rz
        # R = Rz(cz) Ry(by) Rx(ax), Blender's XYZ order.
        by = -np.arcsin(np.clip(R[:, 2, 0], -1, 1))
        ax = np.arctan2(R[:, 2, 1], R[:, 2, 2])
        cz = np.arctan2(R[:, 1, 0], R[:, 0, 0])
        return np.stack([ax, by, cz], 1)

    def grass_material(self, base):
        """A grass shape's material, its colour times each copy's shade (grass_tint, carried onto the merged mesh)."""
        key = ("grass", base.name)
        if key in self.materials:
            return self.materials[key]
        m = base.copy(); m.name = "grass_" + base.name
        nt = m.node_tree; bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
        if bsdf.inputs["Base Color"].links:
            src = bsdf.inputs["Base Color"].links[0].from_socket
            at = nt.nodes.new("ShaderNodeAttribute"); at.attribute_type = "GEOMETRY"; at.attribute_name = "grass_tint"
            mul = nt.nodes.new("ShaderNodeVectorMath"); mul.operation = "SCALE"
            nt.links.new(src, mul.inputs[0]); nt.links.new(at.outputs["Fac"], mul.inputs["Scale"])
            nt.links.new(mul.outputs[0], bsdf.inputs["Base Color"])
        self.materials[key] = m
        return m

    def grass_nodes(self, src, realize=True):
        """Geometry nodes copying src onto every point, turned (rot) and scaled (scl) per point, as one mesh (or kept
        as instances: realize False)."""
        ng = bpy.data.node_groups.new("grass_" + src.name, "GeometryNodeTree")
        ng.interface.new_socket("Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
        ng.interface.new_socket("Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
        N = ng.nodes; L = ng.links
        gin = N.new("NodeGroupInput"); gout = N.new("NodeGroupOutput")
        iop = N.new("GeometryNodeInstanceOnPoints")
        oi = N.new("GeometryNodeObjectInfo"); oi.inputs["Object"].default_value = src
        oi.inputs["As Instance"].default_value = True
        rot = N.new("GeometryNodeInputNamedAttribute"); rot.data_type = "FLOAT_VECTOR"; rot.inputs["Name"].default_value = "rot"
        scl = N.new("GeometryNodeInputNamedAttribute"); scl.data_type = "FLOAT_VECTOR"; scl.inputs["Name"].default_value = "scl"
        L.new(gin.outputs[0], iop.inputs["Points"]); L.new(oi.outputs["Geometry"], iop.inputs["Instance"])
        L.new(rot.outputs["Attribute"], iop.inputs["Rotation"]); L.new(scl.outputs["Attribute"], iop.inputs["Scale"])
        # Made real geometry, one mesh a kind: EEVEE walks instances one by one on the CPU every sample (194000 of
        # Whiterun's grass kept a frame rendering over 20 minutes with the GPU idle).
        if realize:
            real = N.new("GeometryNodeRealizeInstances")
            L.new(iop.outputs["Instances"], real.inputs["Geometry"]); L.new(real.outputs["Geometry"], gout.inputs[0])
        else:
            L.new(iop.outputs["Instances"], gout.inputs[0])
        return ng

    def pixels(self, rel):
        """A weight map's pixels, rows from the bottom (as its UVs run), RGBA."""
        img = self.image(rel, colour=False)
        a = np.empty(img.size[0] * img.size[1] * 4, np.float32)
        img.pixels.foreach_get(a)
        return a.reshape(img.size[1], img.size[0], 4)

    ROAD_WORDS = ("road", "path", "cobble", "street")

    def is_road_layer(self, info, gi, ci):
        """A land layer whose texture is a road's (by its name)."""
        if not hasattr(self, "_texture_names"):
            self._texture_names = {v: k.lower() for k, v in self.manifest["textures"].items()}
        name = os.path.basename(self._texture_names.get(info["groups"][gi]["layers"][ci], "").replace("\\", "/"))
        return any(w in name for w in self.ROAD_WORDS)

    def layers_colour(self, info, ranked):
        """One colour for layers left out of a chunk ((mean weight, group, channel), as ranked): their textures'
        average colours (linear), weighted by how much each is painted there; None when none is."""
        total = sum(m for m, _, _ in ranked)
        if total <= 0:
            return None
        acc = np.zeros(3)
        for m, gi, ci in ranked:
            rel = info["groups"][gi]["layers"][ci]
            if rel not in self.layer_means:
                img = self.image(rel)
                px = np.empty(img.size[0] * img.size[1] * 4, np.float32); img.pixels.foreach_get(px)
                self.layer_means[rel] = np.power(np.clip(px.reshape(-1, 4)[:, :3].mean(0), 0, 1), 2.2)   # sRGB to linear
            acc += m * self.layer_means[rel]
        return tuple(round(float(c), 3) for c in acc / total)

    def land_material(self, info, used, tint, fill=None):
        """The land's material for the layers used ((group, channel) pairs): each layer's texture tiled every TILE
        units, weighted by its painted weight, multiplied by tint; the weight the layers left out of the chunk leave
        (1 - the kept weights: the weights add to 1) filled with their colour (fill)."""
        m = bpy.data.materials.new("land"); m.use_nodes = True
        m["fill"] = list(fill) if fill else []
        m["layers"] = [f"{gi}:{ci}" for gi, ci in used]
        nt = m.node_tree; N = nt.nodes; L = nt.links
        bsdf = N["Principled BSDF"]; bsdf.inputs["Roughness"].default_value = 0.9
        bsdf.inputs["Specular IOR Level"].default_value = 0.25
        uvw = N.new("ShaderNodeUVMap"); uvw.uv_map = "Weights"
        tc = N.new("ShaderNodeTexCoord")
        tile = N.new("ShaderNodeVectorMath"); tile.operation = "SCALE"; tile.inputs["Scale"].default_value = 1.0 / (TILE * S)
        L.new(tc.outputs["Object"], tile.inputs[0])
        total = None
        wsum = None
        chans = {}
        for gi, ci in used:
            if gi not in chans:
                wimg = N.new("ShaderNodeTexImage"); wimg.image = self.image(info["groups"][gi]["image"], colour=False)
                wimg.extension = "EXTEND"; wimg.interpolation = "Linear"
                L.new(uvw.outputs["UV"], wimg.inputs["Vector"])
                sep = N.new("ShaderNodeSeparateColor"); L.new(wimg.outputs["Color"], sep.inputs["Color"])
                chans[gi] = [sep.outputs[0], sep.outputs[1], sep.outputs[2], wimg.outputs["Alpha"]]
            lt = N.new("ShaderNodeTexImage"); lt.image = self.image(info["groups"][gi]["layers"][ci], alpha=False)
            L.new(tile.outputs["Vector"], lt.inputs["Vector"])
            sc = N.new("ShaderNodeVectorMath"); sc.operation = "SCALE"
            L.new(lt.outputs["Color"], sc.inputs[0]); L.new(chans[gi][ci], sc.inputs["Scale"])
            if total is None:
                total = sc
            else:
                add = N.new("ShaderNodeVectorMath"); add.operation = "ADD"
                L.new(total.outputs[0], add.inputs[0]); L.new(sc.outputs[0], add.inputs[1]); total = add
            if wsum is None:
                wsum = chans[gi][ci]
            else:
                wadd = N.new("ShaderNodeMath"); wadd.operation = "ADD"
                L.new(wsum, wadd.inputs[0]); L.new(chans[gi][ci], wadd.inputs[1]); wsum = wadd.outputs[0]
        # Where layers were left out of the chunk (MAX_LAYERS), their weight (1 - the kept weights) takes their colour.
        # (Dividing by the kept weights instead left the ground black where the left-out layers were nearly all of it.)
        if total is not None and fill is not None:
            rest = N.new("ShaderNodeMath"); rest.operation = "SUBTRACT"; rest.inputs[0].default_value = 1.0; rest.use_clamp = True
            L.new(wsum, rest.inputs[1])
            fc = N.new("ShaderNodeVectorMath"); fc.operation = "SCALE"; fc.inputs[0].default_value = fill
            L.new(rest.outputs[0], fc.inputs["Scale"])
            add = N.new("ShaderNodeVectorMath"); add.operation = "ADD"
            L.new(total.outputs[0], add.inputs[0]); L.new(fc.outputs[0], add.inputs[1]); total = add
        if total is not None and tint:
            mul = N.new("ShaderNodeVectorMath"); mul.operation = "MULTIPLY"
            mul.inputs[1].default_value = tuple(tint)
            L.new(total.outputs[0], mul.inputs[0]); total = mul
        if total is not None:
            L.new(total.outputs[0], bsdf.inputs["Base Color"])
        return m

    def ground(self, colour=None, z=None, rects=None):
        """A town built without land (Daggerfall's): its margin's grass out to the horizon, or with colour, grassland
        of that colour mottled with darker and drier patches. With land (z: just under its lowest point), the same
        grassland past the land's edge to the horizon, where the sky would show otherwise."""
        margin = self.manifest.get("margin")
        tex = None
        if margin is not None:
            p = os.path.join(self.dir, "meshes", f"{margin}.bin")
            if os.path.exists(p):
                tex = read_mesh(p)[0]["diffuse"]
        # Out to the camera's reach (clip_end, 20 km): at 400000 units (5.7 km) its edge sat just under the horizon and the
        # sky's own ground showed past it as a blue band (user, 2026-10-11: "look up top whats rendered up there").
        w = 1200000.0
        # Rectangles (x0, y0, x1, y1) of ground: everywhere for a town without land; with land, only where there is
        # none (user, 2026-10-08: "ground is sinking again": a whole plane at the land's edge level flooded the
        # lower valleys inside the land) - the four bands round the land's cells, and the cells in between it lacks.
        cells = getattr(self, "land_cells", None) if z is not None else None
        holes = {tuple(h) for h in self.scene.get("holes", [])}
        if z is None:
            z = (self.zmin or 0) - 4
        if rects is not None:
            pass
        elif cells:
            gxs = [c[0] for c in cells]; gys = [c[1] for c in cells]
            X0, Y0, X1, Y1 = min(gxs) * 4096, min(gys) * 4096, (max(gxs) + 1) * 4096, (max(gys) + 1) * 4096
            rects = [(-w, -w, X0, w), (X1, -w, w, w), (X0, -w, X1, Y0), (X0, Y1, X1, w)]
            rects += [(gx * 4096, gy * 4096, (gx + 1) * 4096, (gy + 1) * 4096)
                      for gx in range(min(gxs), max(gxs) + 1) for gy in range(min(gys), max(gys) + 1)
                      if (gx, gy) not in cells and (gx, gy) not in holes]
        else:
            rects = [(-w, -w, w, w)]
        pos, tri = [], []
        for i, (x0, y0, x1, y1) in enumerate(rects):
            pos += [[x0, y0, z], [x1, y0, z], [x0, y1, z], [x1, y1, z]]
            tri += [[4 * i, 4 * i + 1, 4 * i + 3], [4 * i, 4 * i + 3, 4 * i + 2]]
        pos = np.array(pos, np.float32)
        shape = dict(diffuse=tex, alpha_mode=0, threshold=0.5, pos=pos,
                     nrm=np.tile([0, 0, 1], (len(pos), 1)).astype(np.float32),
                     uv=pos[:, :2] / 2048.0,
                     col=np.ones((len(pos), 4), np.float32), tri=np.array(tri, np.int32))
        me = self.mesh("ground", shape)
        # Matte: a plane this big catches the sky at grazing angles and reads as water otherwise.
        if colour:
            # The town's own border ring (the margin's texture) matte too: at this angle it otherwise shines blue.
            edge = me.materials[0].node_tree.nodes["Principled BSDF"]
            edge.inputs["Roughness"].default_value = 1.0; edge.inputs["Specular IOR Level"].default_value = 0.0
            m = bpy.data.materials.new("grassland"); m.use_nodes = True
            N = m.node_tree.nodes; L = m.node_tree.links
            tc = N.new("ShaderNodeTexCoord")
            noise = N.new("ShaderNodeTexNoise"); noise.inputs["Scale"].default_value = 0.004; noise.inputs["Detail"].default_value = 8
            L.new(tc.outputs["Object"], noise.inputs["Vector"])
            ramp = N.new("ShaderNodeValToRGB")
            ramp.color_ramp.elements[0].color = (colour[0] * 0.6, colour[1] * 0.6, colour[2] * 0.6, 1)
            ramp.color_ramp.elements[1].color = (colour[0] * 1.6, colour[1] * 1.35, colour[2] * 1.1, 1)
            L.new(noise.outputs["Fac"], ramp.inputs["Fac"])
            L.new(ramp.outputs["Color"], N["Principled BSDF"].inputs["Base Color"])
            me.materials[0] = m
        # Its own copy of the material (the margin's may be shared), fading out with distance from GROUND_FADE[0] to
        # GROUND_FADE[1] metres: the flat stand-in ends in the haze and the sky's horizon, not in an edge.
        mat = me.materials[0].copy(); me.materials[0] = mat
        bsdf = mat.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Roughness"].default_value = 1.0; bsdf.inputs["Specular IOR Level"].default_value = 0.0
        if self.game and self.game.get("fog"):
            # In game shading solid, the weather's fog fading it as the game's fog fades its distant land (blended, it
            # left no depth for the fog and showed as an unfogged band under the horizon).
            ob = bpy.data.objects.new("ground", me); self.coll.objects.link(ob)
            return
        cam = mat.node_tree.nodes.new("ShaderNodeCameraData")
        fade = mat.node_tree.nodes.new("ShaderNodeMapRange")
        fade.inputs["From Min"].default_value, fade.inputs["From Max"].default_value = GROUND_FADE
        fade.inputs["To Min"].default_value, fade.inputs["To Max"].default_value = 1.0, 0.0
        mat.node_tree.links.new(cam.outputs["View Distance"], fade.inputs["Value"])
        mat.node_tree.links.new(fade.outputs["Result"], bsdf.inputs["Alpha"])
        # Blended, not dithered: dithering left a speckled band where it faded out (2026-10-11).
        try:
            mat.surface_render_method = "BLENDED"
        except Exception:
            mat.blend_method = "BLEND"
        ob = bpy.data.objects.new("ground", me); self.coll.objects.link(ob)

    def town_level(self):
        """A placed town's street level: low among its objects' bases in its cells (scene.json "holes")."""
        inside = {tuple(h) for h in self.scene.get("holes", [])}
        zs = [b[2] for b in self.bases if (math.floor(b[0] / 4096), math.floor(b[1] / 4096)) in inside]
        return float(np.percentile(zs, 10)) if zs else (self.zmin or 0)

    def fill_holes(self, colour=None):
        """The overland's cells a placed town stands on (scene.json "holes", their land left out): ground at the
        town's own street level (low among its objects' bases there), under its streets and out to the cells' edges."""
        holes = [tuple(h) for h in self.scene.get("holes", [])]
        if not holes:
            return
        z = self.town_level() - 4
        self.ground(colour, z, [(gx * 4096, gy * 4096, (gx + 1) * 4096, (gy + 1) * 4096) for gx, gy in holes])
        print(f"  town ground: {len(holes)} cells at {z:.0f}", flush=True)

    # Waves (user, 2026-10-09: "the water isnt animated"): three layers drifting with the wind and changing as they go -
    # broad swells (about 17 m), ripples (3 m) and glints (0.6 m): (noise scale per metre, bump distance in metres, drift
    # in metres a second, change a second). One faint layer changing in place read as a still mirror from the air.
    WAVES = ((0.06, 0.9, (0.9, 0.35), 0.12), (0.35, 0.16, (-0.45, 0.8), 0.3), (1.6, 0.035, (0.25, -1.1), 0.7))

    def water_shader(self, m, colour):
        """Clear, reflective water with moving waves: its layers kept for animate_water."""
        nt = m.node_tree; N = nt.nodes; L = nt.links
        bsdf = N["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = (*[c * 0.8 for c in colour], 1)
        bsdf.inputs["Roughness"].default_value = 0.06
        bsdf.inputs["IOR"].default_value = 1.33
        bsdf.inputs["Specular IOR Level"].default_value = 0.6
        tc = N.new("ShaderNodeTexCoord")
        normal = None
        if not hasattr(self, "wave_layers"):
            self.wave_layers = []
        for scale, distance, drift, change in self.WAVES:
            mp = N.new("ShaderNodeMapping"); mp.vector_type = "POINT"
            L.new(tc.outputs["Object"], mp.inputs["Vector"])
            noise = N.new("ShaderNodeTexNoise"); noise.noise_dimensions = "4D"
            noise.inputs["Scale"].default_value = scale; noise.inputs["Detail"].default_value = 4
            L.new(mp.outputs["Vector"], noise.inputs["Vector"])
            bump = N.new("ShaderNodeBump"); bump.inputs["Strength"].default_value = 1.0
            bump.inputs["Distance"].default_value = distance
            L.new(noise.outputs["Fac"], bump.inputs["Height"])
            if normal is not None:
                L.new(normal, bump.inputs["Normal"])
            normal = bump.outputs["Normal"]
            self.wave_layers.append((mp, noise, drift, change))
        L.new(normal, bsdf.inputs["Normal"])

    def animate_water(self, t):
        """Every water material's waves where they are t seconds in."""
        for mp, noise, (dx, dy), change in getattr(self, "wave_layers", []):
            mp.inputs["Location"].default_value = (-dx * t, -dy * t, 0.0)
            noise.inputs["W"].default_value = t * change

    def water_material(self, colour=(0.05, 0.11, 0.15), running=False):
        """Clear, rippled, reflective water (made once); running: water down a slope, broken and frothy rather than a
        mirror (rough, paler, little sky in it)."""
        cache = "_running_mat" if running else "_water_mat"
        if getattr(self, cache, None):
            return getattr(self, cache)
        m = bpy.data.materials.new("running water" if running else "water"); m.use_nodes = True
        self.water_shader(m, colour)
        if running:
            bsdf = m.node_tree.nodes["Principled BSDF"]
            bsdf.inputs["Base Color"].default_value = (0.32, 0.40, 0.42, 1)
            bsdf.inputs["Roughness"].default_value = 0.45
            bsdf.inputs["Specular IOR Level"].default_value = 0.25
        setattr(self, cache, m)
        return m

    def water(self, colour):
        z = self.scene["water"]
        w = 400000.0
        bpy.ops.mesh.primitive_plane_add(size=2 * w * S, location=(0, 0, z * S))
        ob = bpy.context.active_object; ob.name = "water"
        m = bpy.data.materials.new("water"); m.use_nodes = True
        self.water_shader(m, colour)
        ob.data.materials.append(m)


def sun_direction(light):
    a, e = math.radians(light.get("azimuth", 225)), math.radians(light.get("elevation", 32))
    return Vector((math.sin(a) * math.cos(e), math.cos(a) * math.cos(e), math.sin(e)))


def blend(a, b, w):
    """a and b mixed, w of the way to b: numbers and lists of them in proportion, dictionaries key by key, anything
    else the nearer one's (as intro.py's)."""
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


CLOUD_TIME = []
SKY_OBJECTS = []    # (cloud layer object, how far under the camera its origin sits), moved with the camera each frame


def N_TIME():
    """A node-tree's time socket maker: a Value node set to the frame's time each frame (CLOUD_TIME)."""
    def make(N):
        v = N.new("ShaderNodeValue"); v.outputs[0].default_value = 0.0; CLOUD_TIME.append(v)
        return v.outputs[0]
    return make     # the sky's cloud drift nodes (game weather), set to the frame's time in seconds


def game_sky(N, L, unit, sun_dir, game, weather):
    """The game's sky seen in direction unit (a normalised vector socket): the weather's horizon, lower and upper sky
    colours by height, its sun and glare, and its cloud layers drifting at their speeds (user, 2026-10-11: "the weather
    needs done too"). Colours are the game's own (display) values. Returns a colour socket."""
    sky = game["sky"]
    sep = N.new("ShaderNodeSeparateXYZ"); L.new(unit, sep.inputs[0])
    ramp = N.new("ShaderNodeValToRGB"); L.new(sep.outputs["Z"], ramp.inputs["Fac"])
    els = ramp.color_ramp.elements
    # The horizon's colour a narrow band at the horizon, the lower sky's above it, the upper's from about 20 degrees up.
    els[0].position = 0.0; els[0].color = (*sky["horizon"], 1)
    els[1].position = 0.35; els[1].color = (*sky["sky_upper"], 1)
    mid = els.new(0.05); mid.color = (*sky["sky_lower"], 1)
    colour = ramp.outputs["Color"]
    # The sun: its disc in the sun's colour, its glare round it.
    dot = N.new("ShaderNodeVectorMath"); dot.operation = "DOT_PRODUCT"; dot.inputs[1].default_value = tuple(sun_dir)
    L.new(unit, dot.inputs[0])
    for power, col, k in ((900.0, sky["sun_colour"], 3.0), (24.0, sky["sun_glare"], 1.0)):
        pw = N.new("ShaderNodeMath"); pw.operation = "POWER"; pw.inputs[1].default_value = power
        cl = N.new("ShaderNodeMath"); cl.operation = "MAXIMUM"; cl.inputs[1].default_value = 0.0
        L.new(dot.outputs["Value"], cl.inputs[0]); L.new(cl.outputs[0], pw.inputs[0])
        sc = N.new("ShaderNodeVectorMath"); sc.operation = "SCALE"; sc.inputs[0].default_value = tuple(c * k for c in col)
        L.new(pw.outputs[0], sc.inputs["Scale"])
        add = N.new("ShaderNodeVectorMath"); add.operation = "ADD"
        L.new(colour, add.inputs[0]); L.new(sc.outputs[0], add.inputs[1]); colour = add.outputs[0]
    # The cloud layers are the game's own cloud mesh (Builder.sky_clouds), drawn over this.
    return colour


def game_fog(box_mat, game):
    """The weather's volumetric light (VOLI) as the haze volume (user, 2026-10-11: "and volumetrics"): the sun's light
    scattered forward (its phase scattering) by thin air, its shafts shadowed. The fog itself is drawn by game_fog_pass."""
    nt = box_mat.node_tree; N = nt.nodes; L = nt.links
    for n in list(N):
        if n.type != "OUTPUT_MATERIAL":
            N.remove(n)
    out = next(n for n in N if n.type == "OUTPUT_MATERIAL")
    vol = game.get("volumetric")
    if not vol or float(vol.get("intensity") or 0) <= 0:
        print("  weather: no volumetric light", flush=True)
        return False
    sun = game["sun"]
    cc = float(vol.get("custom_colour_contribution") or 0)
    colour = [s_ * (1 - cc) + c * cc for s_, c in zip(sun, vol.get("colour") or [1, 1, 1])]
    sc = N.new("ShaderNodeVolumeScatter"); sc.inputs["Color"].default_value = (*colour, 1)
    sc.inputs["Density"].default_value = 4e-7 / S * float(vol["intensity"]) * float(vol.get("density_contribution") or 0.3) / 0.3
    sc.inputs["Anisotropy"].default_value = min(float(vol.get("phase_scattering") or 0.0), 0.95)
    L.new(sc.outputs[0], out.inputs["Volume"])
    print(f"  weather: volumetric {vol.get('record')}", flush=True)
    return True


def game_fog_pass(scn, game):
    """The weather's fog as the game draws it, on every pixel by its depth (user, 2026-10-11: "the weather needs done
    too"): fog = min(max, ((distance - near) / (far - near)) ^ power), its colour from the near colour to the far one as
    it thickens; the sky, which the game draws unfogged, left as it is. In the compositor, from the depth pass."""
    fog = game.get("fog") or {}
    near = float(fog.get("near") or 0); far = max(float(fog.get("far") or 0), near + 1.0)
    power = float(fog.get("power") or 1.0); fmax = float(fog.get("max") or 1.0)
    sky = game["sky"]
    scn.view_layers[0].use_pass_z = True
    ng = bpy.data.node_groups.new("game fog", "CompositorNodeTree")
    ng.interface.new_socket("Image", in_out="OUTPUT", socket_type="NodeSocketColor")
    N = ng.nodes; L = ng.links
    rl = N.new("CompositorNodeRLayers"); rl.scene = scn
    gout = N.new("NodeGroupOutput")

    def math(op, a, b=None, clamp=False):
        n = N.new("ShaderNodeMath"); n.operation = op; n.use_clamp = clamp
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                n.inputs[i].default_value = v
            else:
                L.new(v, n.inputs[i])
        return n.outputs[0]
    units = math("DIVIDE", rl.outputs["Depth"], S)
    x = math("DIVIDE", math("SUBTRACT", units, near), far - near, clamp=True)
    f = math("MINIMUM", math("POWER", x, power), fmax)
    # The sky (nothing drawn: depth past the camera's reach) takes none.
    f = math("MULTIPLY", f, math("LESS_THAN", rl.outputs["Depth"], 19000.0))
    col = N.new("ShaderNodeMix"); col.data_type = "RGBA"
    L.new(f, col.inputs[0]); col.inputs[6].default_value = (*sky["fog_near"], 1); col.inputs[7].default_value = (*sky["fog_far"], 1)
    mix = N.new("ShaderNodeMix"); mix.data_type = "RGBA"
    L.new(f, mix.inputs[0]); L.new(rl.outputs["Image"], mix.inputs[6]); L.new(col.outputs[2], mix.inputs[7])
    L.new(mix.outputs[2], gout.inputs[0])
    scn.compositing_node_group = ng
    scn.render.use_compositing = True
    print(f"  weather fog: near {near:.0f} far {far:.0f} power {power} max {fmax}", flush=True)


def setup_world(light, haze_centre, game=None, weather=None):
    scn = bpy.context.scene
    world = bpy.data.worlds.new("sky"); scn.world = world; world.use_nodes = True
    N = world.node_tree.nodes; L = world.node_tree.links
    bg = N["Background"]
    sky = N.new("ShaderNodeTexSky")
    for kind in ("NISHITA", "MULTIPLE_SCATTERING", "SINGLE_SCATTERING"):
        try:
            sky.sky_type = kind; break
        except TypeError:
            continue
    d = sun_direction(light)
    elev = math.asin(max(-1, min(1, d.z)))
    sky.sun_disc = False
    sky.sun_elevation = elev
    # Blender's sky puts the sun at rotation 0 on +Y and turns it clockwise seen from above, as our azimuth does.
    sky.sun_rotation = math.radians(light.get("azimuth", 225))
    try:
        sky.altitude = 60.0
        sky.dust_density = 2.5
        sky.air_density = 1.2
    except Exception:
        pass
    L.new(sky.outputs["Color"], bg.inputs["Color"])
    # The sky's light: EEVEE gathers less bounced light than Cycles did, and shadowed ground went near black (user,
    # 2026-10-09: the Imperial City's shadow "does not look right"); SKY_FILL lifts it.
    bg.inputs["Strength"].default_value = 0.14 * SKY_FILL
    if game:
        # The game's ambient: the light from each way is the colour the game gives a surface facing that way (its
        # directional ambient, DALC), so every surface takes it by the way it faces, with no occlusion (fast GI off).
        amb = game["ambient"]
        tca = N.new("ShaderNodeTexCoord"); sepa = N.new("ShaderNodeSeparateXYZ"); L.new(tca.outputs["Generated"], sepa.inputs[0])
        total = None
        for axis, (plus, minus) in zip("XYZ", (("x_plus", "x_minus"), ("y_plus", "y_minus"), ("z_plus", "z_minus"))):
            for key, sign in ((plus, 1.0), (minus, -1.0)):
                part = N.new("ShaderNodeMath"); part.operation = "MULTIPLY"; part.inputs[1].default_value = sign
                L.new(sepa.outputs[axis], part.inputs[0])
                pos = N.new("ShaderNodeMath"); pos.operation = "MAXIMUM"; pos.inputs[1].default_value = 0.0
                L.new(part.outputs[0], pos.inputs[0])
                sq = N.new("ShaderNodeMath"); sq.operation = "POWER"; sq.inputs[1].default_value = 2.0
                L.new(pos.outputs[0], sq.inputs[0])
                col = N.new("ShaderNodeVectorMath"); col.operation = "SCALE"; col.inputs[0].default_value = tuple(amb[key])
                L.new(sq.outputs[0], col.inputs["Scale"])
                if total is None:
                    total = col
                else:
                    add = N.new("ShaderNodeVectorMath"); add.operation = "ADD"
                    L.new(total.outputs[0], add.inputs[0]); L.new(col.outputs[0], add.inputs[1]); total = add
        L.new(total.outputs[0], bg.inputs["Color"])
        bg.inputs["Strength"].default_value = float(amb.get("scale") or 1.0) * float(os.environ.get("TM_AMBIENT_X", "1"))
    # Seen by the camera below the horizon, the horizon's own colour (the sky looked a little upward), not the sky
    # model's ground, which showed as a blue band under the horizon where nothing stood in front of it (user,
    # 2026-10-11: "look up top whats rendered up there"). Camera rays only: the light stays the sky's own (with the
    # lower half bright the whole place washed out in haze).
    tcw = N.new("ShaderNodeTexCoord"); sep = N.new("ShaderNodeSeparateXYZ"); L.new(tcw.outputs["Generated"], sep.inputs[0])
    lift = N.new("ShaderNodeMath"); lift.operation = "MAXIMUM"; lift.inputs[1].default_value = 0.02
    L.new(sep.outputs["Z"], lift.inputs[0])
    comb = N.new("ShaderNodeCombineXYZ")
    L.new(sep.outputs["X"], comb.inputs["X"]); L.new(sep.outputs["Y"], comb.inputs["Y"]); L.new(lift.outputs[0], comb.inputs["Z"])
    unit = N.new("ShaderNodeVectorMath"); unit.operation = "NORMALIZE"; L.new(comb.outputs[0], unit.inputs[0])
    seen = N.new("ShaderNodeTexSky")
    for attr in ("sky_type", "sun_disc", "sun_elevation", "sun_rotation", "altitude", "dust_density", "air_density"):
        try:
            setattr(seen, attr, getattr(sky, attr))
        except Exception:
            pass
    L.new(unit.outputs[0], seen.inputs["Vector"])
    bg_seen = N.new("ShaderNodeBackground"); bg_seen.inputs["Strength"].default_value = 0.14 * SKY_FILL if game else bg.inputs["Strength"].default_value
    if game and game.get("sky"):
        # The game's own sky (its weather's colours and clouds), in display values as written.
        L.new(game_sky(N, L, unit.outputs[0], d, game, weather or {}), bg_seen.inputs["Color"])
        bg_seen.inputs["Strength"].default_value = 1.0
    elif game:
        # The output is written as computed (lit as the game lights, in the textures' own values), so the sky seen is
        # encoded for display here.
        enc = N.new("ShaderNodeGamma"); enc.inputs["Gamma"].default_value = 1 / 2.2
        scale = N.new("ShaderNodeVectorMath"); scale.operation = "SCALE"; scale.inputs["Scale"].default_value = 0.14 * SKY_FILL
        L.new(seen.outputs["Color"], scale.inputs[0]); L.new(scale.outputs[0], enc.inputs["Color"])
        L.new(enc.outputs["Color"], bg_seen.inputs["Color"]); bg_seen.inputs["Strength"].default_value = 1.0
    else:
        L.new(seen.outputs["Color"], bg_seen.inputs["Color"])
    path = N.new("ShaderNodeLightPath"); mix = N.new("ShaderNodeMixShader")
    L.new(path.outputs["Is Camera Ray"], mix.inputs["Fac"])
    L.new(bg.outputs["Background"], mix.inputs[1]); L.new(bg_seen.outputs["Background"], mix.inputs[2])
    L.new(mix.outputs["Shader"], N["World Output"].inputs["Surface"])
    # The sun as a lamp, so its direction and colour are exactly the shot's.
    sun = bpy.data.lights.new("sun", "SUN")
    sun.energy = light.get("sun_power", 3.4) * 1.7
    sun.color = tuple(light.get("sun", [1.0, 0.9, 0.75]))
    if game:
        # The game's sun: its colour at the image space's HDR sunlight scale (a lamp of pi times that lights a
        # surface facing it with exactly the colour times the scale).
        sun.color = tuple(game["sun"])
        sun.energy = math.pi * float((game.get("grade") or {}).get("sunlight_scale") or 1.0)
    sun.angle = math.radians(1.2)
    ob = bpy.data.objects.new("sun", sun); scn.collection.objects.link(ob)
    ob.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
    # Haze the sun shines through: a big thin volume over the place.
    fog = light.get("fog_density", 1.0e-5)
    bpy.ops.mesh.primitive_cube_add(size=1, location=(haze_centre[0], haze_centre[1], 60))
    box = bpy.context.active_object; box.name = "haze"; box.scale = (10000, 10000, 160)
    m = bpy.data.materials.new("haze"); m.use_nodes = True
    N = m.node_tree.nodes; L = m.node_tree.links
    N.remove(N["Principled BSDF"])
    vol = N.new("ShaderNodeVolumePrincipled")
    vol.inputs["Density"].default_value = fog / S * 0.55
    vol.inputs["Anisotropy"].default_value = 0.65
    vol.inputs["Color"].default_value = (*light.get("fog", [0.75, 0.72, 0.7]), 1)
    L.new(vol.outputs["Volume"], N["Material Output"].inputs["Volume"])
    box.data.materials.append(m)
    if game and game.get("fog"):
        # The weather's volumetric light over the whole place, 3 km deep (or none); its fog is a pass of its own.
        box.scale = (14000, 14000, 3000); box.location.z = 0.0
        if not game_fog(m, game):
            box.hide_render = True
        game_fog_pass(scn, game)


def setup_render(w, h, samples, game=None):
    """EEVEE with its ray tracing (user, 2026-10-09: "flybys need rt not pt"): reflections, refraction and diffuse light
    ray traced, shadows traced through shadow maps, the haze's volume lit by the sun; samples are its anti-aliasing
    samples. Settings a Blender version lacks are skipped."""
    scn = bpy.context.scene
    for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
        try:
            scn.render.engine = engine; break
        except TypeError:
            continue
    ee = scn.eevee

    def put(obj, name, value):
        try:
            setattr(obj, name, value)
        except (AttributeError, TypeError, ValueError):
            print(f"  (no {name})", flush=True)
    put(ee, "taa_render_samples", samples)
    # Ray traced in the game-shading mode too (user, 2026-10-09: "flybys need rt not pt"; it had been off there unless
    # TM_GAME_RAYTRACE=1, and the black streets it was turned off for were fixed by Blender 5.2). TM_GAME_RAYTRACE=0 turns it off.
    put(ee, "use_raytracing", os.environ.get("TM_GAME_RAYTRACE") != "0")
    put(ee, "ray_tracing_method", "SCREEN")
    opts = getattr(ee, "ray_tracing_options", None)
    if opts is not None:
        put(opts, "resolution_scale", "1")
        put(opts, "trace_max_roughness", 0.6)
        put(opts, "use_denoise", True)
    put(ee, "use_fast_gi", (not game) or os.environ.get("TM_GAME_FAST_GI") == "1")    # the game has no occlusion or bounce: its ambient is flat
    put(ee, "fast_gi_method", "GLOBAL_ILLUMINATION")
    put(ee, "fast_gi_resolution", "2")
    put(ee, "fast_gi_distance", float(os.environ.get("TM_FAST_GI_DISTANCE", "0")))
    put(ee, "use_shadows", True)
    put(ee, "shadow_ray_count", 2)
    put(ee, "shadow_step_count", 8)
    # Every shadow page in the buffer (user, 2026-10-11: "the roads of the city"): Whiterun's sun shadows at full
    # resolution asked for about 3670 pages a frame of the 2048 a frame may have ("Shadow buffer full"), and the pages
    # left out came out black - its streets between the walls and plots. At 0.6 a frame needs about a third as many;
    # from the air the shadows are as good. (A bigger pool doesn't raise the 2048.)
    put(ee, "shadow_resolution_scale", 0.6)
    put(ee, "shadow_pool_size", "2048")
    # The haze to the horizon: froxels out to 6 km, most near the camera.
    put(ee, "volumetric_start", 1.0)
    put(ee, "volumetric_end", 6000.0)
    put(ee, "volumetric_tile_size", "8")
    put(ee, "volumetric_samples", 96)
    put(ee, "volumetric_sample_distribution", 0.9)
    put(ee, "use_volumetric_shadows", True)
    put(ee, "clamp_world", 10.0)
    scn.render.film_transparent = False
    scn.render.use_persistent_data = False   # kept scene data costs memory the machine has not got
    scn.render.resolution_x, scn.render.resolution_y, scn.render.resolution_percentage = w, h, 100
    scn.render.image_settings.file_format = "PNG"
    if game:
        # Written as computed: the game lights the textures' own (display) values, so nothing more is encoded; its
        # image space grades the frames when they are packed (intro.py).
        scn.view_settings.view_transform = "Raw"
        scn.view_settings.exposure = 0.0
        return
    scn.view_settings.view_transform = "AgX"
    scn.view_settings.exposure = 0.35
    try:
        scn.view_settings.look = "AgX - Medium High Contrast"
    except TypeError:
        pass


def lerp_path(keys, t):
    ts = [k["t"] for k in keys]
    i = max(0, min(len(keys) - 2, int(np.searchsorted(ts, t)) - 1))
    u = (t - ts[i]) / max(ts[i + 1] - ts[i], 1e-6)

    def cr(name):
        p0 = np.array(keys[max(i - 1, 0)][name], float); p1 = np.array(keys[i][name], float)
        p2 = np.array(keys[i + 1][name], float); p3 = np.array(keys[min(i + 2, len(keys) - 1)][name], float)
        return 0.5 * ((2 * p1) + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u ** 3)
    return cr("eye"), cr("target")


def main():
    (scene_dir, path_json, out_dir), o = args()
    w, h = map(int, o["size"].split("x"))
    spec = json.load(open(path_json))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    b = Builder(scene_dir)
    game = None
    if b.game and spec.get("game_time"):
        # Between the day's colours and the sunrise's or sunset's by the sun's height, as the game moves between them
        # (game_blend from intro.py; user, 2026-10-11: "fix the sunsets").
        tw = float(spec.get("game_blend", 1.0))
        game = blend(b.game.get("day"), b.game.get(spec["game_time"]), tw) if spec["game_time"] != "day" else b.game.get("day")
        print(f"  game shading: {b.game.get('weather')} at {spec['game_time']} ({tw:.2f} of the way from day)", flush=True)
    b.game_all = b.game
    b.game = game
    b.land_tint = spec.get("land_tint")
    b.tint_textures = [t.lower() for t in spec.get("tint_textures", [])]
    b.objects()
    b.people()
    has_land = b.land(None if game else spec.get("land_tint"))
    if not has_land and b.scene["water"] < -10000:
        b.ground(spec.get("ground"))
    elif has_land and spec.get("ground"):
        b.ground(spec["ground"], b.land_edge - 60)
    b.fill_holes(spec.get("ground"))
    if spec.get("grass", True) is not False:
        b.grass(spec.get("grass"))
    for c in spec.get("crowd", []):
        b.add_crowd(c)
    b.populate(spec.get("populate", 0))
    b.plan_walks(spec["keys"][-1]["t"])
    if b.scene["water"] > -10000:
        b.water(spec["light"].get("water", [0.05, 0.11, 0.15]))
    keys = spec["keys"]
    mid = np.mean([k["target"] for k in keys], axis=0) * S
    weather = None
    if game and b.game_all:
        clouds = []
        for c in b.game_all.get("clouds") or []:
            t = c["texture"].lower(); t = t if t.startswith("textures\\") else "textures\\" + t
            rel = b.manifest["textures"].get(t)
            clouds.append({**c, "image": b.image(rel) if rel else None})
        weather = {"clouds": clouds, "time": spec["game_time"], "blend": float(spec.get("game_blend", 1.0))}
    setup_world(spec["light"], mid, game, weather)
    if game and weather:
        b.sky_clouds(b.game_all, weather)
    setup_render(w, h, int(o["samples"]), game)
    cam_data = bpy.data.cameras.new("cam"); cam_data.sensor_fit = "VERTICAL"
    cam_data.angle_y = math.radians(spec.get("fov", 36)); cam_data.clip_start = 0.5; cam_data.clip_end = 20000
    cam = bpy.data.objects.new("cam", cam_data); bpy.context.scene.collection.objects.link(cam)
    bpy.context.scene.camera = cam
    fps = float(o["fps"])
    frames = [int(x) for x in o["only"].split(",")] if o["only"] else list(range(int(keys[-1]["t"] * fps)))
    frames = [f for f in frames if not os.path.exists(os.path.join(out_dir, f"{f:05d}.png"))]
    os.makedirs(out_dir, exist_ok=True)

    def place(f):
        """The camera, water and people where they are at frame f; waits while the GPU is hot."""
        t = f / fps
        eye, target = lerp_path(keys, t)
        e, tg = Vector(eye * S), Vector(target * S)
        cam.location = e
        cam.rotation_euler = (tg - e).to_track_quat("-Z", "Y").to_euler()
        b.animate_water(t)
        b.set_time(t)
        for n in CLOUD_TIME:
            n.outputs[0].default_value = t
        for ob, drop in SKY_OBJECTS:
            ob.location = (e.x, e.y, e.z - drop)
        cool_down()

    scn = bpy.context.scene
    if os.environ.get("TM_UNLIT") == "1":
        # Dev aid: every surface its own colour, unlit (base colour as emission, the lights off).
        for m in bpy.data.materials:
            if not m.use_nodes: continue
            nt = m.node_tree
            for bsdf in [n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"]:
                src = bsdf.inputs["Base Color"].links[0].from_socket if bsdf.inputs["Base Color"].links else None
                if src is not None:
                    nt.links.new(src, bsdf.inputs["Emission Color"])
                else:
                    bsdf.inputs["Emission Color"].default_value = bsdf.inputs["Base Color"].default_value
                bsdf.inputs["Emission Strength"].default_value = 1.0
                bsdf.inputs["Base Color"].default_value = (0, 0, 0, 1)
                for l in list(bsdf.inputs["Base Color"].links): nt.links.remove(l)
        for ob in bpy.data.objects:
            if ob.type == "LIGHT": ob.hide_render = True
        bpy.context.scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.0
    if os.environ.get("TM_IDPASS") == "1":
        # Dev aid: every object one flat colour, unlit and uncut (idpass.json beside the frames names them), the haze,
        # lights and world off: which object a pixel shows.
        import random
        rnd = random.Random(7); ids = {}; texs = {}
        for ob in bpy.data.objects:
            if ob.type != "MESH": continue
            if ob.name == "haze": ob.hide_render = True; continue
            c = (rnd.random(), rnd.random(), rnd.random())
            old = ob.data.materials[0] if ob.data.materials else None
            texs[ob.name] = [n.image.filepath for n in old.node_tree.nodes if n.type == "TEX_IMAGE" and n.image][:2] if old and old.node_tree else []
            m = bpy.data.materials.new("id_" + ob.name); m.use_nodes = True
            nt = m.node_tree; nt.nodes.remove(nt.nodes["Principled BSDF"])
            em = nt.nodes.new("ShaderNodeEmission"); em.inputs["Color"].default_value = (*c, 1)
            nt.links.new(em.outputs["Emission"], nt.nodes["Material Output"].inputs["Surface"])
            ob.data.materials.clear(); ob.data.materials.append(m)
            ids[ob.name] = [round(v, 4) for v in c]
        for ob in bpy.data.objects:
            if ob.type == "LIGHT": ob.hide_render = True
        bpy.context.scene.world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.0
        json.dump(ids, open(os.path.join(out_dir, "idpass.json"), "w"))
        json.dump(texs, open(os.path.join(out_dir, "idpass_textures.json"), "w"))
    if os.environ.get("TM_PROBE"):
        # Dev aid: what Blender has at given pixels of the first frame (TM_PROBE="x,y;x,y"), nothing rendered.
        place(frames[0] if frames else 0)
        bpy.context.view_layer.update()
        from bpy_extras.view3d_utils import region_2d_to_vector_3d   # noqa: F401 (not needed: rays built below)
        depsgraph = bpy.context.evaluated_depsgraph_get()
        mw = cam.matrix_world; fwd = mw.to_3x3() @ Vector((0, 0, -1)); right = mw.to_3x3() @ Vector((1, 0, 0)); upv = mw.to_3x3() @ Vector((0, 1, 0))
        k = (h / 2) / math.tan(cam_data.angle_y / 2)
        for item in os.environ["TM_PROBE"].split(";"):
            px, py = (float(v) for v in item.split(","))
            d = (fwd + right * ((px - w / 2) / k) - upv * ((py - h / 2) / k)).normalized()
            origin = cam.location.copy()
            if os.environ.get("TM_PROBE_ALL") == "1":
                o2 = origin.copy()
                for _ in range(12):
                    hit, loc, nrm, face, ob, _ = scn.ray_cast(depsgraph, o2, d)
                    if not hit: break
                    me0 = ob.data; mat0 = me0.materials[me0.polygons[face].material_index] if me0.materials else None
                    col0 = me0.color_attributes.get("Col")
                    va = [round(col0.data[me0.loops[li].vertex_index].color[3], 2) for li in me0.polygons[face].loop_indices] if col0 else None
                    if ob.name.startswith("merged") and me0.uv_layers.get("UV"):
                        uvs = [tuple(round(c, 3) for c in me0.uv_layers["UV"].data[li].uv) for li in me0.polygons[face].loop_indices]
                        rgb = [tuple(round(c, 2) for c in col0.data[me0.loops[li].vertex_index].color[:3]) for li in me0.polygons[face].loop_indices]
                        ti = [n.image for n in mat0.node_tree.nodes if n.type == "TEX_IMAGE" and n.image]
                        smp = []
                        if ti:
                            im = ti[0]; W2, H2 = im.size; pxs = np.empty(W2 * H2 * 4, np.float32); im.pixels.foreach_get(pxs); pxs = pxs.reshape(H2, W2, 4)
                            for u_, v_ in uvs:
                                smp.append(tuple(round(float(c), 2) for c in pxs[int((v_ % 1) * H2) % H2, int((u_ % 1) * W2) % W2]))
                        print(f"  ALL {item}: tri uv {uvs} rgb {rgb} tex {ti[0].name if ti else None} {ti[0].colorspace_settings.name if ti else ''} {ti[0].alpha_mode if ti else ''} samples {smp}", flush=True)
                    print(f"  ALL {item}: dist {(loc - origin).length / S:.1f} z {loc.z / S:.1f} {ob.name} {mat0.name if mat0 else None} "
                          f"normal {tuple(round(c, 2) for c in nrm)} valpha {va}", flush=True)
                    o2 = loc + d * 0.001
            skipped = []
            for _ in range(40):
                hit, loc, nrm, face, ob, _ = scn.ray_cast(depsgraph, origin, d)
                if not hit:
                    break
                if ob.name == "haze":
                    origin = loc + d * 0.01; continue
                me0 = ob.data; poly0 = me0.polygons[face]; mat0 = me0.materials[poly0.material_index] if me0.materials else None
                col0 = me0.color_attributes.get("Col")
                cutout = mat0 is not None and any(n.type == "MATH" and n.operation == "GREATER_THAN" for n in mat0.node_tree.nodes)
                va = min(col0.data[me0.loops[li].vertex_index].color[3] for li in poly0.loop_indices) if col0 else 1.0
                if cutout and va < 0.5:
                    skipped.append(mat0.name); origin = loc + d * 0.01; continue
                break
            if skipped:
                print(f"  probe {item}: through cut-out {skipped}", flush=True)
            if not hit:
                print(f"  probe {item}: nothing", flush=True); continue
            me = ob.data; poly = me.polygons[face]; mat = me.materials[poly.material_index] if me.materials else None
            imgs = [n.image.name for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image] if mat else []
            col = me.color_attributes.get("Col")
            vc = [tuple(round(c, 2) for c in col.data[me.loops[li].vertex_index].color) for li in poly.loop_indices][:1] if col else None
            if ob.name.startswith("land") and mat is not None:
                uvl = me.uv_layers.get("Weights")
                uv = uvl.data[poly.loop_indices[0]].uv if uvl else None
                reads = []
                for n in mat.node_tree.nodes:
                    if n.type == "TEX_IMAGE" and n.image and n.image.name.startswith("land_weights") and uv is not None:
                        im = n.image; W2, H2 = im.size
                        px_ = min(int(uv[0] * W2), W2 - 1); py_ = min(int(uv[1] * H2), H2 - 1)
                        i0 = (py_ * W2 + px_) * 4
                        reads.append((im.name, tuple(round(im.pixels[i0 + c], 2) for c in range(4))))
                print(f"  probe {item}: land uv {tuple(round(c, 4) for c in uv) if uv else None} layers {list(mat.get('layers', []))} fill {list(mat.get('fill', []))} weights {reads}", flush=True)
            print(f"  probe {item}: at {tuple(round(c / S) for c in loc)} {ob.name} material {mat.name if mat else None} images {imgs[:3]} normal {tuple(round(c, 2) for c in nrm)} "
                  f"facing camera {nrm.dot(d) < 0} backface culling {mat.use_backface_culling if mat else None} vcol {vc}", flush=True)
        return
    if len(frames) > 1 and frames == list(range(frames[0], frames[-1] + 1)):
        # A run of frames rendered as one animation (user, 2026-10-09: "why is it going slow"): EEVEE keeps the scene
        # on the GPU from frame to frame. A still per frame re-synced every object and texture each time - 5 s a frame
        # with the GPU idling at a third of its clock. The frame hook moves camera, water and people.
        scn.frame_start, scn.frame_end = frames[0], frames[-1]
        scn.render.fps, scn.render.fps_base = int(round(fps)), round(fps) / fps
        scn.render.filepath = os.path.join(out_dir, "#####")
        scn.render.use_file_extension = True
        scn.render.use_overwrite = True
        scn.render.use_placeholder = False
        clock = [time.time()]

        def before(scene, *_):
            place(scene.frame_current)
            clock[0] = time.time()

        def written(scene, *_):
            print(f"  frame {scene.frame_current} in {time.time() - clock[0]:.1f} s", flush=True)
        bpy.app.handlers.frame_change_pre.append(before)
        bpy.app.handlers.render_write.append(written)
        bpy.ops.render.render(animation=True)
        return
    for f in frames:
        place(f)
        start = time.time()
        scn.render.filepath = os.path.join(out_dir, f"{f:05d}.png")
        bpy.ops.render.render(write_still=True)
        print(f"  frame {f} in {time.time() - start:.1f} s", flush=True)


main()
