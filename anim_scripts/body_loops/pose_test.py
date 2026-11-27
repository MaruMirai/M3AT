"""Blender: poses the rig in a set of test poses and dumps the evaluated Body and Head (world space) per pose.
  blender -b file.blend --python pose_test.py -- out_prefix [key=value ...]"""
import bpy, sys, math, numpy as np
from mathutils import Quaternion, Vector

argv = sys.argv[sys.argv.index('--') + 1:]
out = argv[0]
keyvals = dict(a.split('=') for a in argv[1:])
rig = bpy.data.objects['metarig']
body = bpy.data.objects['Body']; head = bpy.data.objects['Head']
rig.data.pose_position = 'POSE'
if bpy.context.object and bpy.context.object.mode != 'OBJECT': bpy.ops.object.mode_set(mode='OBJECT')
if rig.animation_data: rig.animation_data.action = None
for o in (body, head):
    for m in o.modifiers: m.show_viewport = True
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks: k.value = float(keyvals.get(k.name, 0))

def rot(bone, axis, deg):
    pb = rig.pose.bones[bone]; pb.rotation_mode = 'QUATERNION'
    pb.rotation_quaternion = Quaternion(Vector(axis), math.radians(deg)) @ pb.rotation_quaternion
def both(b, axis, deg, mirror_axes=(1, 2)):
    rot(b + '.L', axis, deg)
    ax = list(axis)
    for i in mirror_axes: ax[i] = -ax[i]
    rot(b + '.R', ax, deg)

POSES = {
    'rest': [],
    'elbow': [('forearm', (1, 0, 0), 100)],
    'armup': [('upper_arm', (0, 0, 1), 70)],
    'armfwd': [('upper_arm', (1, 0, 0), 80)],
    'sit': [('thigh', (1, 0, 0), -95), ('shin', (1, 0, 0), 100)],
    'kneel': [('shin', (1, 0, 0), 130)],
    'fist': [('hand.L.001', (1, 0, 0), 70), ('hand.L.002', (1, 0, 0), 70), ('thumb.L', (1, 0, 0), 40), ('thumb.L.001', (1, 0, 0), 50), ('hand.R.001', (1, 0, 0), 70), ('hand.R.002', (1, 0, 0), 70), ('thumb.R', (1, 0, 0), 40), ('thumb.R.001', (1, 0, 0), 50)],
    'wrist': [('hand', (1, 0, 0), 60)],
    'bend': [('spine.002', (1, 0, 0), 25), ('spine.003', (1, 0, 0), 20), ('spine.004', (1, 0, 0), 25)],
    'foot': [('foot', (1, 0, 0), 40), ('toe', (1, 0, 0), 40)],
}
dg = bpy.context.evaluated_depsgraph_get()
res = {}
for name, moves in POSES.items():
    for pb in rig.pose.bones:
        pb.rotation_mode = 'QUATERNION'; pb.rotation_quaternion = Quaternion(); pb.location = Vector(); pb.scale = Vector((1, 1, 1))
    for b, ax, deg in moves:
        if b.endswith(('.L', '.R')) or '.L.' in b or '.R.' in b: rot(b, ax, deg)
        else: both(b, ax, deg) if b not in ('spine.002', 'spine.003', 'spine.004') else rot(b, ax, deg)
    bpy.context.view_layer.update(); dg = bpy.context.evaluated_depsgraph_get()
    for o, tag in ((body, 'B'), (head, 'H')):
        ev = o.evaluated_get(dg); me = ev.to_mesh(); me.calc_loop_triangles()
        V = np.array([tuple(o.matrix_world @ v.co) for v in me.vertices]); T = np.array([tuple(t.vertices) for t in me.loop_triangles])
        res[name + '_' + tag + 'V'] = V; res[name + '_' + tag + 'T'] = T
        ev.to_mesh_clear()
    res[name + '_J'] = np.array([tuple(rig.matrix_world @ pb.head) for pb in rig.pose.bones]); res['bones'] = np.array([pb.name for pb in rig.pose.bones])
np.savez(out, **res)
print('POSED', out, list(POSES))
