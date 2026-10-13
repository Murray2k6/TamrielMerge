"""The ship (ship.fbx, from CGTrader, user-supplied 2026-10-09) as the tool's ship model parts:
blender -b -P export.py -- <out dir>
Writes <out>/parts/<material>.obj (one per material: triangles with positions, UVs and the model's own normals, in
Blender's z-up frame, the ship's length along y as imported), <out>/collision.obj (the hull, wheel and decks simplified
to a few thousand triangles; sails, ropes and windows left out) and <out>/ship.json (sizes, triangle counts)."""
import bpy, bmesh, json, os, sys

out = os.path.abspath(sys.argv[sys.argv.index("--") + 1]) if "--" in sys.argv else os.path.dirname(os.path.abspath(__file__))
here = os.path.dirname(os.path.abspath(__file__))
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=os.path.join(here, "ship.fbx"))
obj = next(o for o in bpy.context.scene.objects if o.type == "MESH")
depsgraph = bpy.context.evaluated_depsgraph_get()
mesh = obj.evaluated_get(depsgraph).to_mesh()
mesh.transform(obj.matrix_world)
mesh.calc_loop_triangles()
uv = mesh.uv_layers.active.data if mesh.uv_layers.active else None
names = [s.material.name if s.material else f"mat{i}" for i, s in enumerate(obj.material_slots)]
os.makedirs(os.path.join(out, "parts"), exist_ok=True)
counts = {}
for mi, name in enumerate(names):
    tris = [t for t in mesh.loop_triangles if t.material_index == mi]
    if not tris:
        continue
    # Corners shared where position, UV and normal agree (a NIF shape holds 65535 vertices).
    index, lines, faces = {}, [], []
    for t in tris:
        face = []
        for li in t.loops:
            loop = mesh.loops[li]
            co = mesh.vertices[loop.vertex_index].co
            n = loop.normal
            u = uv[li].uv if uv else (0, 0)
            key = (loop.vertex_index, round(u[0], 5), round(u[1], 5), round(n.x, 3), round(n.y, 3), round(n.z, 3))
            if key not in index:
                index[key] = len(index) + 1
                lines.append(f"v {co.x:.6f} {co.y:.6f} {co.z:.6f}\nvt {u[0]:.6f} {u[1]:.6f}\nvn {n.x:.5f} {n.y:.5f} {n.z:.5f}\n")
            face.append(index[key])
        faces.append("f " + " ".join(f"{i}/{i}/{i}" for i in face) + "\n")
    with open(os.path.join(out, "parts", name + ".obj"), "w") as f:
        f.writelines(lines)
        f.writelines(faces)
    counts[name] = (len(tris), len(index))

# Collision: the solid parts only, merged and decimated.
solid = [i for i, n in enumerate(names) if not any(k in n.lower() for k in ("sail", "cable", "window"))]
bm = bmesh.new()
bm.from_mesh(mesh)
bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index not in solid], context="FACES")
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=0.002)
cm = bpy.data.meshes.new("collision")
bm.to_mesh(cm)
bm.free()
col = bpy.data.objects.new("collision", cm)
bpy.context.scene.collection.objects.link(col)
dec = col.modifiers.new("dec", "DECIMATE")
cm.calc_loop_triangles()
dec.ratio = min(1.0, 1500 / max(1, len(cm.loop_triangles)))
cmesh = col.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh()
cmesh.calc_loop_triangles()
with open(os.path.join(out, "collision.obj"), "w") as f:
    for v in cmesh.vertices:
        f.write(f"v {v.co.x:.6f} {v.co.y:.6f} {v.co.z:.6f}\n")
    for t in cmesh.loop_triangles:
        a, b, c = (i + 1 for i in t.vertices)
        f.write(f"f {a} {b} {c}\n")
lo = [min(v.co[k] for v in mesh.vertices) for k in range(3)]
hi = [max(v.co[k] for v in mesh.vertices) for k in range(3)]
json.dump({"min": lo, "max": hi, "parts": counts, "collisionTriangles": len(cmesh.loop_triangles)}, open(os.path.join(out, "ship.json"), "w"), indent=1)
print(f"SHIP parts {counts}; collision {len(cmesh.loop_triangles)} triangles; size {[h - l for l, h in zip(lo, hi)]}")
