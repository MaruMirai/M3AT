"""Baby romper from the kid romper (Models/garments/KidRomper.glb, make_romper.py) as it is, texture and all: the sleeves
cut off at the shoulder, so it is sleeveless, and the legs cut short high on the thigh. The game puts it on a baby once
it has been put to bed (WalkFigures.BabyRomperOf), so it is drawn for no apparel of its own.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_baby_romper.py -- <out.glb>

The glb goes in Models/garments/ as BabyRomper.glb (the garment BabyRomper). The .blend is never saved. Each cut is a
plane square across its limb's upper bone, dropping only what is round that limb (as make_shirt.py cuts its sleeves),
so the trunk is left alone; then only the largest piece is kept. Places along a bone run from its head (0) to its tail
(1); reaches are in leg lengths of the rig (the thigh's head to the floor)."""
import bpy, bmesh, sys, os
from mathutils import Vector

NAME = 'BabyRomper'
# Where the armhole is cut along the upper arm: just out from the shoulder joint, where the sleeve starts.
ARMHOLE = 0.12
# How far from the arm's bones the armhole's cut reaches: past the arm's skin and the sleeve, short of the flank.
ARM_REACH = 0.14
# Where each leg is cut along the thigh: high, a short romper's leg.
LEG = 0.22
# How far from the leg's bones the leg's cut reaches: past the thigh's skin, short of the other leg.
LEG_REACH = 0.21

out_glb = os.path.abspath(sys.argv[sys.argv.index('--') + 1])
G = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Models', 'garments') + os.sep

rig = bpy.data.objects['metarig']
def joint(name, tail=False):
    b = rig.data.bones[name]
    return rig.matrix_world @ (b.tail_local if tail else b.head_local)
hip = joint('thigh.L')
leg = (hip - Vector((hip.x, hip.y, 0.0))).length

before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=G + 'KidRomper.glb')
new = [o for o in bpy.data.objects if o not in before]
romper = next(o for o in new if o.type == 'MESH')
romper.data.transform(romper.matrix_world)
romper.parent = None
romper.matrix_world.identity()
for o in new:
    if o is not romper:
        bpy.data.objects.remove(o)

bm = bmesh.new()
bm.from_mesh(romper.data)

def near(f, a, b, reach):
    p = f.calc_center_median()
    t = max(0.0, min(1.0, (p - a).dot(b - a) / (b - a).length_squared))
    return (p - a.lerp(b, t)).length < reach * leg

def cut(co, no, only):
    """Cuts along the plane at co and drops the faces on the side no points to that pass only."""
    geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
    bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-5, plane_co=co, plane_no=no)
    drop = [f for f in bm.faces if (f.calc_center_median() - co).dot(no) > 0 and only(f)]
    bmesh.ops.delete(bm, geom=drop, context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')

for side in ('L', 'R'):
    a, b = joint('upper_arm.' + side), joint('upper_arm.' + side, tail=True)
    hand = joint('hand.%s.002' % side, tail=True)
    cut(a.lerp(b, ARMHOLE), (b - a).normalized(), lambda f, a=a, hand=hand: near(f, a, hand, ARM_REACH))
    a, b = joint('thigh.' + side), joint('thigh.' + side, tail=True)
    foot = joint('foot.' + side, tail=True)
    cut(a.lerp(b, LEG), (b - a).normalized(), lambda f, a=a, foot=foot: near(f, a, foot, LEG_REACH))

# The cuts leave the sleeves' and legs' ends as pieces of their own: keep the largest piece only, the romper. The mesh
# is split along its UV seams, so faces are joined by their corners' places, not their vertices.
def key(v):
    return tuple(round(c, 4) for c in v.co)
faces_at = {}
for f in bm.faces:
    for v in f.verts:
        faces_at.setdefault(key(v), []).append(f)
bm.faces.index_update()
seen, pieces = set(), []
for f in bm.faces:
    if f.index in seen:
        continue
    stack, piece = [f], []
    seen.add(f.index)
    while stack:
        g = stack.pop()
        piece.append(g)
        for v in g.verts:
            for h in faces_at[key(v)]:
                if h.index not in seen:
                    seen.add(h.index)
                    stack.append(h)
    pieces.append(piece)
pieces.sort(key=len)
for piece in pieces[:-1]:
    bmesh.ops.delete(bm, geom=piece, context='FACES')
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
print('BABYROMPER pieces dropped %s' % [len(p) for p in pieces[:-1]])
bm.to_mesh(romper.data)
bm.free()

romper.name = NAME
romper.data.name = NAME
romper.vertex_groups.clear()
for o in bpy.context.view_layer.objects:
    o.select_set(o is romper)
bpy.context.view_layer.objects.active = romper
print('BABYROMPER %d vertices, %d faces, leg %.3f' % (len(romper.data.vertices), len(romper.data.polygons), leg))
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False, export_image_format='AUTO')
