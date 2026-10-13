"""
Remasters a creature's meshes (user, 2026-10-10: "the guar model since it exist can we reimagine it", chose "remaster it"):
each mesh of a creature.json (CreatureRig.SaveJson) rebuilt in Blender with its UVs and bone weights, the vertices its
old format split at seams merged back, subdivided (Catmull-Clark, UVs kept at their borders, weights carried by Blender),
shaded smooth, and written back in the same format. Bones and animations aren't touched.

    blender -b --disable-autoexec --python remaster_mesh.py -- IN.json OUT.json [LEVELS]
"""
import bpy, bmesh, json, sys

args = sys.argv[sys.argv.index("--") + 1:]
src, dst = args[0], args[1]
levels = int(args[2]) if len(args) > 2 else 1
data = json.load(open(src, encoding="utf-8"))
bpy.ops.wm.read_factory_settings(use_empty=True)

out_meshes = []
for m in data["meshes"]:
    me = bpy.data.meshes.new(m["name"])
    me.from_pydata([tuple(v) for v in m["vertices"]], [], [tuple(t) for t in m["triangles"]])
    me.update()
    uv = me.uv_layers.new(name="UV")
    for poly in me.polygons:
        for li in poly.loop_indices:
            u, v = m["uvs"][me.loops[li].vertex_index]
            uv.data[li].uv = (u, 1 - v)
    ob = bpy.data.objects.new(m["name"], me)
    bpy.context.scene.collection.objects.link(ob)
    groups = {}
    for vi, ws in enumerate(m["weights"]):
        for b, w in ws:
            g = groups.get(b) or ob.vertex_groups.new(name=f"b{b}")
            groups[b] = g
            g.add([vi], w, 'ADD')
    # Seams: the old format splits a vertex wherever UVs or normals change; merge them so subdividing doesn't open cracks
    # (UVs live on the loops, so the seams stay in the texture).
    bm = bmesh.new(); bm.from_mesh(me)
    size = max(max(v.co.x for v in bm.verts) - min(v.co.x for v in bm.verts), 1e-3)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=size * 1e-4)
    bm.to_mesh(me); bm.free()
    sub = ob.modifiers.new("Subdivide", 'SUBSURF')
    sub.levels = sub.render_levels = levels
    sub.uv_smooth = 'PRESERVE_BOUNDARIES'
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    bpy.ops.object.modifier_apply(modifier=sub.name)
    me = ob.data
    me.calc_loop_triangles()
    for p in me.polygons: p.use_smooth = True
    uvl = me.uv_layers.active.data
    verts, norms, uvs, weights, tris, remap = [], [], [], [], [], {}
    names = {g.index: g.name for g in ob.vertex_groups}
    for lt in me.loop_triangles:
        tri = []
        for li, vi in zip(lt.loops, lt.vertices):
            n = me.corner_normals[li].vector
            u = tuple(round(c, 6) for c in uvl[li].uv)
            key = (vi, u, tuple(round(c, 3) for c in n))
            if key not in remap:
                remap[key] = len(verts)
                verts.append(list(me.vertices[vi].co))
                norms.append(list(n.normalized()))
                uvs.append([u[0], 1 - u[1]])
                ws = sorted(((int(names[g.group][1:]), g.weight) for g in me.vertices[vi].groups if g.weight > 0), key=lambda x: -x[1])[:4]
                total = sum(w for _, w in ws) or 1
                weights.append([[b, w / total] for b, w in ws] or [[0, 1.0]])
            tri.append(remap[key])
        tris.append(tri)
    out_meshes.append({**m, "vertices": verts, "normals": norms, "uvs": uvs, "weights": weights, "triangles": tris})
    print(f"remastered {m['name']}: {len(m['triangles'])} -> {len(tris)} triangles")

data["meshes"] = out_meshes
json.dump(data, open(dst, "w", encoding="utf-8"))
