"""Puts the weights back on the parts a script added to an armour (make_locust.py's boosters, make_grenadier.py's
launcher) after the .glb has been edited and saved without its rig, keeping the edits.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/reweight_part.py -- <source.glb> <edited.glb> <out.glb> [--whole]

source.glb is the armour the part was added to (Models/garments/powerArmor.glb, ArmorRecon.glb); every vertex of the
edited file further than ON_SOURCE from its surface is the part's. The part is split into pieces joined by edges (copies
split at a texture seam counted as one), and each piece is weighted as one: every vertex the body's weights round its
foot (the vertex nearest the source's surface), its deform bones only and less the arm's (NOT_ARMS), as make_grenadier.py
weights its launcher. With --whole each piece goes wholly to the one bone with the most of that, as make_locust.py has
its boosters on their shins. The armour's own vertices are left with none, so export_frames.py's "part_weights" gives
them the body's. The result is written skinned to the rig, as the make scripts write it; out.glb may be edited.glb."""
import bpy, bmesh, sys, os
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
whole_bone = '--whole' in argv
argv = [a for a in argv if a != '--whole']
if len(argv) != 3:
    sys.exit('reweight_part.py: give source.glb edited.glb out.glb [--whole]')
source_glb, edited_glb, out_glb = (os.path.abspath(a) for a in argv)

ON_SOURCE = 1e-3       # Blender units: a vertex this near the source's surface is the armour's own
NOT_ARMS = ('upper_arm', 'forearm', 'hand')
WEIGHT_NEAR = 6        # body vertices round a piece's foot whose weights it takes


def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    new = [o for o in bpy.data.objects if o not in before]
    mesh = next(o for o in new if o.type == 'MESH')
    for o in new:
        if o is not mesh:
            bpy.data.objects.remove(o)
    return mesh


source = load(source_glb)
src = bmesh.new()
src.from_mesh(source.data)
src.transform(source.matrix_world)
source_tree = BVHTree.FromBMesh(src)
src.free()
bpy.data.objects.remove(source)

edited = load(edited_glb)
name = edited.name
mw = edited.matrix_world
places = [mw @ v.co for v in edited.data.vertices]
gap = [source_tree.find_nearest(p)[3] for p in places]
part = {i for i, d in enumerate(gap) if d > ON_SOURCE}
if not part:
    sys.exit('reweight_part.py: every vertex of %s is on %s; nothing to weight' % (edited_glb, source_glb))

# The pieces: joined by edges, and copies at one place (a texture seam's) as one.
link = {i: set() for i in part}
for e in edited.data.edges:
    a, b = e.vertices
    if a in part and b in part:
        link[a].add(b)
        link[b].add(a)
at = {}
for i in part:
    at.setdefault(tuple(round(c, 5) for c in places[i]), []).append(i)
for same in at.values():
    for i in same:
        link[i].update(j for j in same if j != i)
pieces, left = [], set(part)
while left:
    stack = [left.pop()]
    piece = set(stack)
    while stack:
        for j in link[stack.pop()]:
            if j in left:
                left.remove(j)
                piece.add(j)
                stack.append(j)
    pieces.append(piece)

body = bpy.data.objects['Body']
rig = body.find_armature()
bones = {b.name for b in rig.data.bones if b.use_deform}
kd = KDTree(len(body.data.vertices))
for i, v in enumerate(body.data.vertices):
    kd.insert(body.matrix_world @ v.co, i)
kd.balance()
names = {g.index: g.name for g in body.vertex_groups}

edited.vertex_groups.clear()
for n, piece in enumerate(pieces):
    foot = places[min(piece, key=lambda i: gap[i])]
    weights = {}
    for _, i, dist in kd.find_n(foot, WEIGHT_NEAR):
        for g in body.data.vertices[i].groups:
            b = names[g.group]
            if b in bones and g.weight > 0 and not b.startswith(NOT_ARMS):
                weights[b] = weights.get(b, 0.0) + g.weight / (1 + dist)
    if whole_bone:
        weights = {max(weights, key=weights.get): 1.0}
    total = sum(weights.values())
    weights = {b: w / total for b, w in weights.items() if w / total > 0.02}
    total = sum(weights.values())
    weights = {b: w / total for b, w in weights.items()}
    for b, w in weights.items():
        group = edited.vertex_groups.get(b) or edited.vertex_groups.new(name=b)
        group.add(sorted(piece), w, 'REPLACE')
    print('REWEIGHT piece %d: %d vertices, foot at %.2f %.2f %.2f, %s'
          % (n, len(piece), foot.x, foot.y, foot.z, ', '.join('%s %.2f' % bw for bw in sorted(weights.items(), key=lambda bw: -bw[1]))))

world = edited.matrix_world.copy()
edited.parent = rig
edited.matrix_world = world
for m in [m for m in edited.modifiers if m.type == 'ARMATURE']:
    edited.modifiers.remove(m)
edited.modifiers.new('Armature', 'ARMATURE').object = rig
edited.name = name
bpy.ops.object.select_all(action='DESELECT')
rig.hide_set(False)
rig.hide_viewport = False
edited.select_set(True)
rig.select_set(True)
bpy.context.view_layer.objects.active = edited
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False, export_extras=True)
print('REWEIGHT %d of %d vertices in %d pieces -> %s' % (len(part), len(places), len(pieces), out_glb))
