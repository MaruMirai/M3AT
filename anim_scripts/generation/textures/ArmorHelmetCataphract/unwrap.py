"""Gives a one-mesh garment .glb fresh UVs with no overlap (Smart UV Project), keeping everything else, as the
cataphract helmet's, carried over from the power armour helmet's mirrored UVs with its new faceplate flat in them,
made Meshy paint one place over another.

    blender -b --python unwrap.py -- <in.glb> <out.glb>
"""
import math, sys, bpy
a = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=a[0], disable_bone_shape=True)
o = next(x for x in bpy.data.objects if x.type == 'MESH')
for x in bpy.context.scene.objects:
    x.select_set(x is o)
bpy.context.view_layer.objects.active = o
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(50), island_margin=0.01, area_weight=0.0, scale_to_bounds=True)
bpy.ops.object.mode_set(mode='OBJECT')
bpy.ops.export_scene.gltf(filepath=a[1], export_format='GLB', use_selection=True, export_animations=False, export_extras=True)
print('UNWRAPPED', o.name, len(o.data.polygons), 'faces')
