"""
Exports a downloaded creature model for the converter's own-skeleton creatures (user, 2026-10-10: "just make a new
skeleton", "we use skyrim se"): its deform bones, its skinned meshes and every animation, baked frame by frame (IK and
control rigs resolved), in Skyrim's frame (Z up, facing +Y, Skyrim units).

    blender -b --disable-autoexec --python export_creature.py -- CONFIG.json OUT_DIR

CONFIG (tools/creatures/<creature>/creature.json):
    source       the model file beside the config (.blend, .fbx, .glb)
    armature     the armature to use (default: the first)
    meshes       mesh object names to keep (default: every mesh skinned to the armature)
    body         {"source": model file, "fit": mesh it replaces, "arms": [[upper arm, forearm], ...]}: another model's mesh
                 bound to this armature (a better-looking body on a good rig), see the fitting below
    height       the creature's height in Skyrim units (it is scaled to it)
    facing       which way the model faces in its file: "-Y" (Blender's front, default), "+Y", "+X", "-X"
    actions      {skyrim name: action name in the file}, e.g. {"idle": "Armature|idle"}
    textures     {"diffuse": file, "normal": file, ...} beside the config, for every part (single-material models)
    materials    {material name: {"diffuse": file or "packed:IMAGE", ...}} for models whose materials don't link theirs

OUT_DIR/creature.json: bones (name, parent, local rest transform), meshes (vertices, normals, UVs, triangles, up to
four bone weights each, material textures), animations (fps, per-frame local transforms of every bone).
"""
import bpy, filecmp, json, math, os, shutil, sys
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
config_path, out_dir = os.path.abspath(args[0]), os.path.abspath(args[1])
config = json.load(open(config_path, encoding="utf-8"))
here = os.path.dirname(config_path)
os.makedirs(out_dir, exist_ok=True)

source = os.path.join(here, config["source"])
if source.lower().endswith(".blend"):
    bpy.ops.wm.open_mainfile(filepath=source, load_ui=False)
else:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if source.lower().endswith((".glb", ".gltf")):
        bpy.ops.import_scene.gltf(filepath=source)
    else:
        bpy.ops.import_scene.fbx(filepath=source, automatic_bone_orientation=False, ignore_leaf_bones=True)

scene = bpy.context.scene
arm = bpy.data.objects[config["armature"]] if config.get("armature") else next(o for o in scene.objects if o.type == 'ARMATURE')

def select_only(*objs, active=None):
    for o in bpy.context.view_layer.objects: o.select_set(False)
    for o in objs: o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]

def world_bounds_of(o):
    deps = bpy.context.evaluated_depsgraph_get()
    ev = o.evaluated_get(deps); me = ev.to_mesh()
    pts = [ev.matrix_world @ v.co for v in me.vertices]
    ev.to_mesh_clear()
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi, pts

# "body": another model's mesh on this armature (a better-looking model for a good rig): scaled to the mesh it replaces,
# its arms met by posing the arm chains towards its hands, bound there, brought back to the rest pose, bound again.
body = config.get("body")
if body:
    before = set(bpy.data.objects)
    src = os.path.join(here, body["source"])
    if src.lower().endswith((".glb", ".gltf")): bpy.ops.import_scene.gltf(filepath=src)
    elif src.lower().endswith(".obj"): bpy.ops.wm.obj_import(filepath=src)
    else: bpy.ops.import_scene.fbx(filepath=src)
    new = [o for o in bpy.data.objects if o not in before and o.type == 'MESH']
    for o in [o for o in bpy.data.objects if o not in before and o.type != 'MESH']: bpy.data.objects.remove(o)
    select_only(*new, active=new[0])
    if len(new) > 1: bpy.ops.object.join()
    nb = bpy.context.view_layer.objects.active
    nb.parent = None
    select_only(nb); bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    old = bpy.data.objects[body["fit"]]
    mods = [m for m in old.modifiers if m.type == 'ARMATURE']
    for m in mods: m.show_viewport = False
    olo, ohi, _ = world_bounds_of(old)
    for m in mods: m.show_viewport = True
    nlo, nhi, _ = world_bounds_of(nb)
    k = (ohi.z - olo.z) / max(nhi.z - nlo.z, 1e-6) * body.get("scale", 1.0)
    nb.scale = (k, k, k); select_only(nb); bpy.ops.object.transform_apply(scale=True)
    nlo, nhi, _ = world_bounds_of(nb)
    nb.location += Vector(((olo.x + ohi.x) / 2 - (nlo.x + nhi.x) / 2, (olo.y + ohi.y) / 2 - (nlo.y + nhi.y) / 2, olo.z - nlo.z + body.get("lift", 0.0)))
    select_only(nb); bpy.ops.object.transform_apply(location=True)
    # The rig's own constraints (IK, copy rotation) would fight the fitting pose.
    muted = [c for pb in arm.pose.bones for c in pb.constraints if not c.mute]
    for c in muted: c.mute = True
    arm.data.pose_position = 'POSE'
    for pb in arm.pose.bones: pb.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    _, _, pts = world_bounds_of(nb)
    height_lo, height_hi = min(p.z for p in pts), max(p.z for p in pts)
    upper = [p for p in pts if p.z > height_lo + 0.35 * (height_hi - height_lo)]
    for chain in body.get("arms", []):
        ua = arm.pose.bones[chain[0]]
        head = arm.matrix_world @ ua.head
        side = 1 if head.x >= 0 else -1
        hand = max(upper, key=lambda p: p.x * side)
        want = (hand - head).normalized()
        have = ((arm.matrix_world @ ua.tail) - head).normalized()
        turn = have.rotation_difference(want).to_matrix().to_4x4()
        aw = arm.matrix_world
        m = aw.inverted() @ Matrix.Translation(head) @ turn @ Matrix.Translation(-head) @ aw @ ua.matrix
        ua.matrix = m
        bpy.context.view_layer.update()
        print(f"fit: {chain[0]} turned {math.degrees(have.angle(want)):.0f} deg towards the hand")
    # Weights from the mesh it replaces (already weighted to this rig), posed the same way: each vertex takes the
    # nearest surface's weights (Blender's automatic weights fail on many downloads). Then the rest pose brings the
    # body to the rig's own pose, and that shape is kept.
    for g in old.vertex_groups:
        if g.name not in nb.vertex_groups: nb.vertex_groups.new(name=g.name)
    dt = nb.modifiers.new("TMWeights", 'DATA_TRANSFER')
    dt.object = old
    dt.use_object_transform = True
    dt.use_vert_data = True
    dt.data_types_verts = {'VGROUP_WEIGHTS'}
    dt.vert_mapping = 'POLYINTERP_NEAREST'
    dt.layers_vgroup_select_src = 'ALL'
    dt.layers_vgroup_select_dst = 'NAME'
    select_only(nb); bpy.ops.object.modifier_apply(modifier=dt.name)
    # Back to the rig's rest pose: each vertex through its bones' pose-to-rest (inverse linear blend skinning).
    aw = arm.matrix_world
    pose_of = {pb.name: aw @ pb.matrix @ pb.bone.matrix_local.inverted() @ aw.inverted() for pb in arm.pose.bones}
    names = {g.index: g.name for g in nb.vertex_groups}
    mw = nb.matrix_world
    for v in nb.data.vertices:
        ws = [(names[g.group], g.weight) for g in v.groups if g.weight > 0 and names.get(g.group) in pose_of]
        total = sum(w for _, w in ws)
        if total <= 0: continue
        m = Matrix(((0.0,) * 4,) * 4)
        for n, w in ws: m += pose_of[n] * (w / total)
        v.co = mw.inverted() @ (m.inverted() @ (mw @ v.co))
    nb.data.update()
    for pb in arm.pose.bones: pb.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
    am = nb.modifiers.new("Armature", 'ARMATURE'); am.object = arm
    weighted_groups = sum(1 for g in nb.vertex_groups if any(g.index in [x.group for x in v.groups] for v in nb.data.vertices[:2000]))
    for c in muted: c.mute = False
    print(f"fit: {nb.name} weighted from {old.name} ({weighted_groups} of {len(nb.vertex_groups)} groups used)")
    config["meshes"] = [nb.name]
meshes = [bpy.data.objects[n] for n in config["meshes"]] if config.get("meshes") else \
    [o for o in scene.objects if o.type == 'MESH' and any(m.type == 'ARMATURE' and m.object == arm for m in o.modifiers)]

# Deform bones: those some kept mesh is weighted to, and their ancestors (so the chain stays whole).
weighted = {g.name for o in meshes for g in o.vertex_groups}
keep = set()
for b in arm.data.bones:
    if b.name in weighted and b.use_deform:
        p = b
        while p is not None:
            keep.add(p.name); p = p.parent
bones = [b for b in arm.data.bones if b.name in keep]   # parents come before children in this order
index = {b.name: i for i, b in enumerate(bones)}

def kept_parent(b):
    p = b.parent
    while p is not None and p.name not in keep: p = p.parent
    return p

# Skyrim's frame: turn the model so it faces +Y, then scale it to its height.
turn = {"-Y": math.pi, "+Y": 0.0, "+X": math.pi / 2, "-X": -math.pi / 2}[config.get("facing", "-Y")]
to_skyrim_rot = Matrix.Rotation(turn, 4, 'Z')

def world_bounds():
    deps = bpy.context.evaluated_depsgraph_get()
    lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
    for o in meshes:
        ev = o.evaluated_get(deps)
        for v in ev.to_mesh().vertices:
            w = to_skyrim_rot @ ev.matrix_world @ v.co
            lo = Vector(map(min, lo, w)); hi = Vector(map(max, hi, w))
        ev.to_mesh_clear()
    return lo, hi

# Files saved showing the armature's rest position ignore animation: show poses.
print(f"armature {arm.name}: pose position was {arm.data.pose_position}")
arm.data.pose_position = 'POSE'
# Rest pose for the bind (no action playing).
if arm.animation_data: arm.animation_data.action = None
for pb in arm.pose.bones: pb.matrix_basis = Matrix.Identity(4)
scene.frame_set(scene.frame_start)
lo, hi = world_bounds()
scale = config["height"] / max(hi.z - lo.z, 1e-6)
centre = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
# World (Blender) -> Skyrim: turn, stand on z = 0 centred, scale.
to_skyrim = Matrix.Diagonal((scale, scale, scale, 1)) @ Matrix.Translation(-centre) @ to_skyrim_rot

def bone_world(pose_bone_matrix):
    """Armature-space bone matrix -> Skyrim space, as (translation, rotation quaternion) with unit scale."""
    m = to_skyrim @ arm.matrix_world @ pose_bone_matrix
    t, r, _ = m.decompose()
    return t, r

def local(child_t, child_r, parent):
    if parent is None: return child_t, child_r
    pt, pr = parent
    inv = pr.inverted()
    return inv @ (child_t - pt), inv @ child_r

def tr(t, r): return {"t": [t.x, t.y, t.z], "q": [r.w, r.x, r.y, r.z]}

rest_world = {b.name: bone_world(b.matrix_local) for b in bones}
out = {"scale": scale, "bones": []}
for b in bones:
    p = kept_parent(b)
    t, r = local(*rest_world[b.name], rest_world[p.name] if p else None)
    out["bones"].append({"name": b.name, "parent": p.name if p else None, **tr(t, r)})

# Meshes, skinned: Skyrim-space vertices, normals, UVs, triangles, up to four weights.
deps = bpy.context.evaluated_depsgraph_get()
out["meshes"] = []

# Image files beside the config, by name (lower case, without a Blender ".001" suffix): models whose files name their
# textures without linking them (FBX/GLB downloads) find them here.
library = {}
for rootdir, _, files in os.walk(here):
    if os.sep + "work" in rootdir: continue
    for f in files:
        if f.lower().endswith((".png", ".jpg", ".jpeg", ".tga", ".bmp")):
            library.setdefault(f.lower(), os.path.join(rootdir, f))

def image_file(img):
    """A Blender image as a file on disk: its own path, a packed image saved out, or a downloaded file of its name."""
    path = bpy.path.abspath(img.filepath) if img.filepath else ""
    if path and os.path.exists(path) and not img.packed_file: return path
    if img.packed_file or img.has_data:
        out_path = os.path.join(out_dir, "tex_" + bpy.path.clean_name(img.name) + ".png")
        try:
            img.filepath_raw = out_path; img.file_format = 'PNG'; img.save()
            return out_path
        except Exception: pass
    name = img.name.lower()
    base = name.rsplit(".", 1)[0] if name.rsplit(".", 1)[-1].isdigit() else name
    for key in (name, base, os.path.basename(path).lower()):
        if key in library: return library[key]
    return None

def config_texture(v):
    """A config texture: a file beside the config, or "packed:NAME" for an image packed in the .blend."""
    if v.startswith("packed:"):
        img = bpy.data.images.get(v[7:])
        return image_file(img) if img else None
    return os.path.join(here, v)

def portable(path):
    """A texture copied into the export's tex folder, named by where it is relative to the export: the tool ships the
    export and these copies (not the downloads, hundreds of megabytes), and works wherever it is installed."""
    tex_dir = os.path.join(out_dir, "tex")
    os.makedirs(tex_dir, exist_ok=True)
    rel = os.path.relpath(path, out_dir)
    if rel.replace("\\", "/").startswith("tex/"):
        return rel.replace("\\", "/")
    stem, ext = os.path.splitext(os.path.basename(path))
    name = bpy.path.clean_name(stem) + ext.lower()
    target = os.path.join(tex_dir, name)
    n = 1
    while os.path.exists(target) and not filecmp.cmp(path, target, shallow=False):
        n += 1; target = os.path.join(tex_dir, f"{bpy.path.clean_name(stem)}_{n}{ext.lower()}")
    if not os.path.exists(target):
        shutil.copyfile(path, target)
    return "tex/" + os.path.basename(target)

def material_textures(mat):
    tex = {}
    if mat and mat.use_nodes:
        for node in mat.node_tree.nodes:
            if node.type != 'TEX_IMAGE' or not node.image: continue
            links = node.outputs[0].links
            n = node.image.name.lower()
            kind = "normal" if any(l.to_node.type == 'NORMAL_MAP' for l in links) or "normal" in n or "_nrm" in n or "_n." in n else                    "diffuse" if any(l.to_socket.name == 'Base Color' for l in links) or "color" in n or "albedo" in n or "diff" in n else None
            if kind and kind not in tex:
                f = image_file(node.image)
                if f: tex[kind] = f
    override = config.get("materials", {}).get(mat.name if mat else "", {})
    tex.update({k: config_texture(v) for k, v in override.items() if config_texture(v)})
    return tex

for o in meshes:
    # Rest shape without the armature's deformation.
    mods = [m for m in o.modifiers if m.type == 'ARMATURE']
    for m in mods: m.show_viewport = False
    ev = o.evaluated_get(bpy.context.evaluated_depsgraph_get())
    me = ev.to_mesh()
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data if me.uv_layers.active else None
    groups = {g.index: g.name for g in o.vertex_groups}
    mw = to_skyrim @ o.matrix_world
    nrm = mw.to_3x3().inverted().transposed()
    # One part per material.
    parts = {}
    for lt in me.loop_triangles:
        part = parts.setdefault(lt.material_index, {"verts": [], "norms": [], "uvs": [], "weights": [], "tris": [], "remap": {}})
        tri = []
        for li, vi in zip(lt.loops, lt.vertices):
            n = me.corner_normals[li].vector if hasattr(me, "corner_normals") else me.loops[li].normal
            u = tuple(round(c, 6) for c in uv[li].uv) if uv else (0.0, 0.0)
            key = (vi, u, tuple(round(c, 4) for c in n))
            if key not in part["remap"]:
                part["remap"][key] = len(part["verts"])
                part["verts"].append(list(mw @ me.vertices[vi].co))
                nn = (nrm @ n).normalized(); part["norms"].append([nn.x, nn.y, nn.z])
                part["uvs"].append([u[0], 1 - u[1]])
                ws = []
                for g in me.vertices[vi].groups:
                    name = groups.get(g.group)
                    # A weight on a bone that isn't kept goes to its nearest kept ancestor.
                    b = arm.data.bones.get(name) if name else None
                    while b is not None and b.name not in keep: b = b.parent
                    if b is not None and g.weight > 0: ws.append((index[b.name], g.weight))
                merged = {}
                for i, w in ws: merged[i] = merged.get(i, 0) + w
                top = sorted(merged.items(), key=lambda x: -x[1])[:4]
                total = sum(w for _, w in top) or 1
                part["weights"].append([[i, w / total] for i, w in top] or [[0, 1.0]])
            tri.append(part["remap"][key])
        part["tris"].append(tri)
    for mi, part in sorted(parts.items()):
        mat = o.material_slots[mi].material if mi < len(o.material_slots) else None
        textures = material_textures(mat)
        # Textures named in the config for every part (single-material models) come last.
        textures.update({k: config_texture(v) for k, v in config.get("textures", {}).items() if config_texture(v)})
        textures = {k: portable(v) for k, v in textures.items()}
        name = o.name if len(parts) == 1 else f"{o.name}_{mat.name if mat else mi}"
        out["meshes"].append({"name": bpy.path.clean_name(name), "vertices": part["verts"], "normals": part["norms"], "uvs": part["uvs"],
                              "weights": part["weights"], "triangles": part["tris"], "textures": textures})
        print(f"part {name}: {len(part['tris'])} triangles, textures {sorted(textures)}")
    ev.to_mesh_clear()
    for m in mods: m.show_viewport = True

# Animations: every frame of each wanted action, each kept bone's local transform (constraints and IK evaluated).
out["animations"] = {}
fps = scene.render.fps / scene.render.fps_base
for name, action_name in config.get("actions", {}).items():
    action = bpy.data.actions.get(action_name)
    if action is None:
        print(f"WARNING action {action_name} not found"); continue
    arm.animation_data_create()
    arm.animation_data.action = action
    # Blender 4.4+: an action plays through a slot; older files' actions need theirs chosen.
    if hasattr(action, "slots") and len(action.slots) > 0 and getattr(arm.animation_data, "action_slot", None) is None:
        arm.animation_data.action_slot = action.slots[0]
    f0, f1 = int(action.frame_range[0]), int(action.frame_range[1])
    frames = []
    for f in range(f0, f1 + 1):
        scene.frame_set(f)
        world = {pb.name: bone_world(pb.matrix) for pb in arm.pose.bones if pb.name in keep}
        frame = []
        for b in bones:
            p = kept_parent(b)
            t, r = local(*world[b.name], world[p.name] if p else None)
            frame.append(tr(t, r))
        frames.append(frame)
    out["animations"][name] = {"fps": fps, "frames": frames}
    # How much it moves: the largest change of any bone's local translation or rotation over the clip.
    moved = max((max(abs(frames[f][b]["t"][i] - frames[0][b]["t"][i]) for i in range(3)) +
                 max(abs(frames[f][b]["q"][i] - frames[0][b]["q"][i]) for i in range(4))
                 for f in range(len(frames)) for b in range(len(frames[0]))), default=0)
    print(f"animation {name}: {len(frames)} frames, moves {moved:.3f}{'  <-- STILL' if moved < 1e-4 else ''}")

json.dump(out, open(os.path.join(out_dir, "creature.json"), "w"))
print(f"EXPORTED {len(bones)} bones, {sum(len(m['triangles']) for m in out['meshes'])} triangles, "
      f"{len(out['animations'])} animations, scale {scale:.3f}")
