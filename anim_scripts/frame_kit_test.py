"""Headless test of the garment kit's own frames (dev_notes/body.md, Own frames): pose a frame, make a framed suit over the posed body, capture, export,
and check the exported rest mesh against the body at rest and the frame lines against Blender's own deform.
Run: /home/t/Programs/blender-5.2.3-linux-x64/blender -b --factory-startup --python anim_scripts/frame_kit_test.py -- <out folder>
Its garment_FrameTestSuit.txt is what the ownframe autotest wants in <config>/M3AT/M3ATgarments."""
import sys, os, json, math
import bpy
from mathutils import Vector, Matrix

KIT = "/home/t/Programs/rimworld/M3AT/M3AT: Maru's 3D Animated Toons/Docs/Tools/m3at_garment_kit.py"
CHIBI = "/home/t/Programs/rimworld/M3AT/M3AT: Maru's 3D Animated Toons/Models/chibi"
OUT = sys.argv[sys.argv.index('--') + 1]
ns = {'__name__': 'm3at_kit'}
exec(compile(open(KIT).read(), KIT, 'exec'), ns)
ns['register']()

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)
scene = bpy.context.scene
scene.m3at_kit.chibi = CHIBI
scene.m3at_kit.out = OUT
assert bpy.ops.m3at.import_body() == {'FINISHED'}
body, rig = bpy.data.objects['M3AT_Body'], bpy.data.objects['M3AT_Rig']

# The frame: longer thighs, broader shoulders, bigger hands.
for side in 'LR':
    rig.pose.bones['thigh.' + side].scale = (1.0, 1.25, 1.0)
    rig.pose.bones['shoulder.' + side].scale = (1.0, 1.4, 1.0)
    rig.pose.bones['hand.' + side].scale = (1.3, 1.3, 1.3)
bpy.context.view_layer.update()

# A suit modelled around the posed body: the posed body pushed out along its normals, no armature.
deps = bpy.context.evaluated_depsgraph_get()
me = bpy.data.meshes.new_from_object(body.evaluated_get(deps))
me.transform(body.matrix_world)
me.update()
for v in me.vertices:
    v.co += v.normal * 0.02
suit = bpy.data.objects.new('FrameTestSuit', me)
scene.collection.objects.link(suit)
s = suit.m3at
s.defs = 'Apparel_PowerArmor'
s.hard = True
s.hide_clothes = True
for o in bpy.context.selected_objects:
    o.select_set(False)
suit.select_set(True)
bpy.context.view_layer.objects.active = suit
r = bpy.ops.m3at.capture_frame()
print('capture', r, len(json.loads(suit['m3at_frame'])['deform']))

# Rig back at rest (the export must not depend on the pose), then export.
assert bpy.ops.m3at.rest_pose() == {'FINISHED'}
bpy.context.view_layer.update()
r = bpy.ops.m3at.export()
print('export', r)

# Check: each exported vertex against the body at rest pushed out 0.02 (rest is skinned back; the push was posed).
path = os.path.join(OUT, 'garment_FrameTestSuit.txt')
lines = open(path).read().split('\n')
vs = [tuple(float(x) for x in l.split()[1:4]) for l in lines if l.startswith('v ')]
frames = [l for l in lines if l.startswith('frame ')]
print('vertices', len(vs), 'frame lines', len(frames), 'w lines', sum(1 for l in lines if l.startswith('w ')))
ref = [v.co.copy() for v in body.data.shape_keys.reference_key.data]
from mathutils.kdtree import KDTree
kd = KDTree(len(ref))
for i, p in enumerate(ref):
    kd.insert(p, i)
kd.balance()
worst = 0
for g in vs:
    p = Vector((-g[0], -g[2], g[1]))
    _, _, d = kd.find(p)
    worst = max(worst, d)
print('farthest exported vertex from the rest body: %.4f cells (pushed 0.02 posed)' % worst)

# The frame lines against Blender: a game-space point through a line equals the Blender deform done in Blender space.
show = bpy.ops.m3at.show_frame()
bpy.context.view_layer.update()
pb = rig.pose.bones['hand.L']
m = pb.matrix @ pb.bone.matrix_local.inverted()
line = next(l for l in frames if l.split()[1] == 'hand.L').split()
G = Matrix([[float(x) for x in line[2 + 4 * r:6 + 4 * r]] for r in range(3)] + [[0, 0, 0, 1]])
bp = pb.bone.head_local + Vector((0.01, 0.02, 0.03))
gp = Vector((-bp.x, bp.z, -bp.y))
a = m @ bp
b = G @ gp
print('frame line check: blender', tuple(round(c, 5) for c in (-a.x, a.z, -a.y)), 'game', tuple(round(c, 5) for c in b))
