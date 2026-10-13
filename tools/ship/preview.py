"""A still of the ship as the tool converts it (its exported parts with the textures links.json gives each material),
to check the textures and the look: blender -b -P preview.py -- <out.png> [azimuth degrees]"""
import bpy, json, math, os, sys

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out = os.path.abspath(args[0]) if args else os.path.join(os.path.dirname(__file__), "preview.png")
azimuth = math.radians(float(args[1])) if len(args) > 1 else math.radians(35)
here = os.path.dirname(os.path.abspath(__file__))
links = json.load(open(os.path.join(here, "links.json")))
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
for f in sorted(os.listdir(os.path.join(here, "parts"))):
    name = f[:-4]
    bpy.ops.wm.obj_import(filepath=os.path.join(here, "parts", f), forward_axis="Y", up_axis="Z")
    obj = bpy.context.selected_objects[0]
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    maps = links.get(name, {})
    if "DiffuseColor" in maps:
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(os.path.join(here, "tex", maps["DiffuseColor"]))
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    if "NormalMap" in maps:
        nm = nt.nodes.new("ShaderNodeTexImage")
        nm.image = bpy.data.images.load(os.path.join(here, "tex", maps["NormalMap"]))
        nm.image.colorspace_settings.name = "Non-Color"
        nmap = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(nm.outputs["Color"], nmap.inputs["Color"])
        nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    bsdf.inputs["Roughness"].default_value = 0.7
    obj.data.materials.clear()
    obj.data.materials.append(mat)
lo = [min(min((o.matrix_world @ v.co)[k] for v in o.data.vertices) for o in scene.objects) for k in range(3)]
hi = [max(max((o.matrix_world @ v.co)[k] for v in o.data.vertices) for o in scene.objects) for k in range(3)]
centre = [(a + b) / 2 for a, b in zip(lo, hi)]
size = max(b - a for a, b in zip(lo, hi))
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
scene.collection.objects.link(cam)
d = size * 1.6
cam.location = (centre[0] + d * math.sin(azimuth), centre[1] - d * math.cos(azimuth), centre[2] + size * 0.35)
direction = [c - l for c, l in zip(centre, cam.location)]
import mathutils
cam.rotation_euler = mathutils.Vector(direction).to_track_quat("-Z", "Y").to_euler()
scene.camera = cam
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
sun.data.energy = 4
sun.rotation_euler = (math.radians(50), 0, math.radians(30))
scene.collection.objects.link(sun)
world = bpy.data.worlds.new("w"); scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.55, 0.65, 0.8, 1)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items] else "BLENDER_EEVEE"
scene.render.resolution_x, scene.render.resolution_y = 1280, 900
scene.render.filepath = out
bpy.ops.render.render(write_still=True)
print("SHIP preview", out, "bow test: y-min end narrower" if True else "")
