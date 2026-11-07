"""Renders a one-mesh garment .glb from the sides, plain grey, as what Gemini is shown.

    blender -b --python render_views.py -- <in.glb> <out folder>
"""
import math
import sys
import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=args[0])
mesh = next(o for o in bpy.data.objects if o.type == 'MESH')
for m in mesh.data.materials:
    m.use_nodes = False
corners = [mesh.matrix_world @ Vector(c) for c in mesh.bound_box]
mid = sum(corners, Vector()) / 8
size = max((a - b).length for a in corners for b in corners)
print("BOX", [round(max(c[i] for c in corners) - min(c[i] for c in corners), 3) for i in range(3)], "MID", [round(v, 3) for v in mid])

s = bpy.context.scene
s.render.engine = 'BLENDER_WORKBENCH'
s.render.resolution_x = s.render.resolution_y = 1024
s.world = bpy.data.worlds.new("w")
s.world.color = (0.75, 0.75, 0.75)
sh = s.display.shading
sh.light = 'STUDIO'
sh.color_type = 'SINGLE'
sh.single_color = (0.8, 0.8, 0.8)
sh.show_cavity = True
sh.cavity_type = 'WORLD'
sh.show_object_outline = True
sh.object_outline_color = (0, 0, 0)
sh.background_type = 'WORLD'
cam = bpy.data.objects.new("c", bpy.data.cameras.new("c"))
s.collection.objects.link(cam)
s.camera = cam
cam.data.type = 'ORTHO'
cam.data.ortho_scale = size * 1.15
VIEWS = {"minusY": 0, "plusX": 90, "plusY": 180, "minusX": 270, "q_minusY_plusX": 40, "q_plusY_plusX": 140}
for name, deg in VIEWS.items():
    a = math.radians(deg)
    way = Vector((math.sin(a), -math.cos(a), 0.25 if name.startswith("q_") else 0)).normalized()
    cam.location = mid + way * size * 3
    cam.rotation_euler = (-way).to_track_quat('-Z', 'Y').to_euler()
    s.render.filepath = f"{args[1]}/{name}.png"
    bpy.ops.render.render(write_still=True)
