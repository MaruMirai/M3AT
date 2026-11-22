"""Puts the weights back on the loincloth after it is edited and saved without its rig (a .glb with no skin), keeping the
edit: the briefs are left unweighted, so the exporter gives them the body's (GARMENT_PROPS' part_weights), and each flap
is weighted as make_underwear.py's add_flaps weights it, the hips at its top going over to the thighs further down
(FLAP_THIGHS of the way at its hem), each thigh by how far across the flap it is (the figure's left, +x, thigh.L's).

    blender -b Models/pawnHumanRig.blend --python anim_scripts/reweight_loincloth.py -- <edited.glb> <out.glb>

The flaps are told from the briefs by place: a vertex on the surface of Models/garments/Underwear.glb (within ON_BRIEFS),
which the loincloth's briefs are, is the briefs'; any other is a flap's, front or back by its side of the body, as an
edit (Symmetrize) may join the flaps to the briefs. The .glb is written skinned to the rig, its image as it was."""
import bpy, bmesh, sys, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

# make_underwear.py's: how far down the flap it is the thighs' rather than the hips', at most.
FLAP_THIGHS = 0.85
# A vertex this close (leg lengths) to the briefs' surface is the briefs' (the flaps' top row stands 0.002 off them).
ON_BRIEFS = 0.0008
# The briefs the loincloth was made from.
BRIEFS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Models', 'garments', 'Underwear.glb')

src, out = sys.argv[sys.argv.index('--') + 1:][:2]
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

body = walk.join_body()
rig = body.parent
leg = (rig.matrix_world @ rig.data.bones['thigh.L'].head_local).z

before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=os.path.abspath(src), disable_bone_shape=True)
new = [o for o in bpy.data.objects if o not in before]
o = [x for x in new if x.type == 'MESH'][0]
for x in new:
    if x is not o:
        bpy.data.objects.remove(x)
o.name = 'Loincloth'
mw = o.matrix_world.copy()
places = [mw @ v.co for v in o.data.vertices]

# The briefs, by place: what lies on the Underwear garment's surface (the loincloth's briefs are it, unchanged); every
# other vertex is a flap's, the front's or the back's by its side of the body (an edit may join them to the briefs).
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=BRIEFS, disable_bone_shape=True)
new = [x for x in bpy.data.objects if x not in before]
u = [x for x in new if x.type == 'MESH'][0]
briefs = BVHTree.FromPolygons([u.matrix_world @ v.co for v in u.data.vertices], [tuple(q.vertices) for q in u.data.polygons])
for x in new:
    bpy.data.objects.remove(x)
on = [(briefs.find_nearest(p)[0] - p).length < ON_BRIEFS * leg for p in places]
brief = [i for i, b in enumerate(on) if b]
flaps = [ids for ids in ([i for i, b in enumerate(on) if not b and places[i].y < 0],
                         [i for i, b in enumerate(on) if not b and places[i].y >= 0]) if ids]

o.vertex_groups.clear()
hips = o.vertex_groups.new(name=walk.HIPS)
thighs = {'L': o.vertex_groups.new(name='thigh.L'), 'R': o.vertex_groups.new(name='thigh.R')}
for ids in flaps:
    top = max(places[i].z for i in ids)
    hem = min(places[i].z for i in ids)
    left = max(places[i].x for i in ids)
    right = min(places[i].x for i in ids)
    for i in ids:
        p = places[i]
        below = min(1.0, (top - p.z) / max(top - hem, 1e-6)) * FLAP_THIGHS
        across = (p.x - right) / max(left - right, 1e-6)
        if below < 1.0:
            hips.add([i], 1.0 - below, 'REPLACE')
        if below > 0.0:
            thighs['L'].add([i], below * across, 'REPLACE')
            thighs['R'].add([i], below * (1.0 - across), 'REPLACE')
    way = 'back' if sum(places[i].y for i in ids) > 0 else 'front'
    print('REWEIGHT flap (%s): %d vertices, %.3f to %.3f leg lengths over the floor' % (way, len(ids), hem / leg, top / leg))
print('REWEIGHT briefs: %d vertices, left to the body\'s weights' % len(brief))

o.parent = rig
o.matrix_world = mw
o.modifiers.new('Armature', 'ARMATURE').object = rig
bpy.ops.object.select_all(action='DESELECT')
rig.hide_set(False)
rig.hide_viewport = False
rig.select_set(True)
o.select_set(True)
bpy.context.view_layer.objects.active = o
bpy.ops.export_scene.gltf(filepath=os.path.abspath(out), use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False)
print('REWEIGHT written', out)
