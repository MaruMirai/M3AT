"""blender -b --python flat.py -- <glb> <out.png> <out_box.txt>: the back, texture colour only, ortho, grey background, 1024."""
import math, sys, bpy
from mathutils import Vector
a = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=a[0], disable_bone_shape=True)
ms = [o for o in bpy.data.objects if o.type == 'MESH']
pts = [o.matrix_world @ v.co for o in ms for v in o.data.vertices]
low = Vector(min(p[i] for p in pts) for i in range(3)); high = Vector(max(p[i] for p in pts) for i in range(3))
sc = bpy.context.scene
sc.render.engine = 'BLENDER_WORKBENCH'
sc.display.shading.light = 'FLAT'; sc.display.shading.color_type = 'TEXTURE'
sc.render.resolution_x = sc.render.resolution_y = 1024
sc.world = bpy.data.worlds.new('w'); sc.world.color = (0.33, 0.33, 0.33)
sc.view_settings.view_transform = 'Standard'
cam = bpy.data.objects.new('c', bpy.data.cameras.new('c'))
cam.data.type = 'ORTHO'; scale = max(high - low) * 1.05; cam.data.ortho_scale = scale
sc.collection.objects.link(cam); sc.camera = cam
mid = (low + high) / 2
r = math.radians(float(a[3]) if len(a) > 3 else 180)
cam.location = mid + Vector((math.sin(r), -math.cos(r), 0)) * 10; cam.rotation_euler = (math.pi / 2, 0, r)
sc.render.filepath = a[1]
bpy.ops.render.render(write_still=True)
open(a[2], 'w').write('%r %r %r %r\n' % (mid.x, mid.z, scale, 1024))
