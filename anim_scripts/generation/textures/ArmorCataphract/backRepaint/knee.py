"""blender -b --python knee.py -- <glb> <prefix>: close views of the backs of the knees (behind, behind from above, the two back 3/4s)."""
import math, sys, bpy
from mathutils import Vector, Euler
a = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=a[0], disable_bone_shape=True)
ms = [o for o in bpy.data.objects if o.type == 'MESH']
pts = [o.matrix_world @ v.co for o in ms for v in o.data.vertices]
low = Vector(min(p[i] for p in pts) for i in range(3)); high = Vector(max(p[i] for p in pts) for i in range(3))
sc = bpy.context.scene
sc.render.engine = 'BLENDER_WORKBENCH'
sc.display.shading.light = 'FLAT'; sc.display.shading.color_type = 'TEXTURE'
sc.view_settings.view_transform = 'Standard'
sc.render.resolution_x = sc.render.resolution_y = 800
cam = bpy.data.objects.new('c', bpy.data.cameras.new('c'))
cam.data.type = 'ORTHO'; cam.data.ortho_scale = (high - low).z * 0.42
sc.collection.objects.link(cam); sc.camera = cam
knee = Vector(((low.x + high.x) / 2, (low.y + high.y) / 2, low.z + (high.z - low.z) * 0.36))
for name, yaw, pitch in (('b', 180, 0), ('up', 180, 40), ('l', 145, 25), ('r', 215, 25)):
    y, p = math.radians(yaw), math.radians(pitch)
    d = Vector((math.sin(y) * math.cos(p), -math.cos(y) * math.cos(p), math.sin(p)))
    cam.location = knee + d * 20
    cam.rotation_euler = (math.pi / 2 - p, 0, y)
    sc.render.filepath = '%s_%s.png' % (a[1], name)
    bpy.ops.render.render(write_still=True)
