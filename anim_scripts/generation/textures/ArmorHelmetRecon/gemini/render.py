"""Renders a garment .glb front, side and back (ortho, world light only) at 1024 px on a plain grey background.
    blender -b --python render.py -- <in.glb> <prefix>"""
import math, sys, bpy
from mathutils import Vector
a = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=a[0])
ps = [o.matrix_world @ v.co for o in bpy.data.objects if o.type == 'MESH' for v in o.data.vertices]
low = Vector(min(p[i] for p in ps) for i in range(3)); high = Vector(max(p[i] for p in ps) for i in range(3))
sc = bpy.context.scene
sc.render.engine = 'BLENDER_EEVEE'
sc.world = bpy.data.worlds.new('w'); sc.world.use_nodes = True
sc.world.node_tree.nodes['Background'].inputs[0].default_value = (1, 1, 1, 1)
sc.render.film_transparent = True
sc.render.resolution_x = sc.render.resolution_y = 1024
sc.view_settings.view_transform = 'Standard'
cam = bpy.data.objects.new('c', bpy.data.cameras.new('c')); cam.data.type = 'ORTHO'
cam.data.ortho_scale = max(high - low) * 1.15
sc.collection.objects.link(cam); sc.camera = cam
mid = (low + high) / 2
for view, angle in (('front', 0), ('side', 90), ('back', 180)):
    r = math.radians(angle)
    cam.location = mid + Vector((math.sin(r), -math.cos(r), 0)) * max(high - low) * 3
    cam.rotation_euler = (math.pi / 2, 0, r)
    sc.render.filepath = '%s_%s.png' % (a[1], view)
    bpy.ops.render.render(write_still=True)
