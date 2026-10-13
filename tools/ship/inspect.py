"""Prints what ship.fbx holds (objects, polygons, materials and their images, size): blender -b -P inspect.py"""
import bpy, os, mathutils

bpy.ops.wm.read_factory_settings(use_empty=True)
here = os.path.dirname(os.path.abspath(__file__))
bpy.ops.import_scene.fbx(filepath=os.path.join(here, "ship.fbx"))
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
lo = mathutils.Vector((1e18, 1e18, 1e18)); hi = -lo
tris = 0
mats = {}
for o in meshes:
    m = o.data
    m.calc_loop_triangles()
    tris += len(m.loop_triangles)
    for c in o.bound_box:
        w = o.matrix_world @ mathutils.Vector(c)
        lo = mathutils.Vector(map(min, lo, w)); hi = mathutils.Vector(map(max, hi, w))
    for s in o.material_slots:
        if s.material:
            mats.setdefault(s.material.name, 0)
            mats[s.material.name] += 1
print(f"SHIP {len(meshes)} mesh objects, {tris} triangles, size {hi - lo} (min {lo}, max {hi}), units scale {bpy.context.scene.unit_settings.scale_length}")
for o in sorted(meshes, key=lambda o: -len(o.data.polygons))[:25]:
    print(f"SHIP   {o.name}: {len(o.data.polygons)} polys, materials {[s.material.name for s in o.material_slots if s.material]}")
for name, n in sorted(mats.items()):
    mat = bpy.data.materials[name]
    imgs = [n.image.name for n in (mat.node_tree.nodes if mat.node_tree else []) if n.type == "TEX_IMAGE" and n.image]
    print(f"SHIP material {name} on {n} objects; images {imgs}")
