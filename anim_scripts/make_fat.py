"""Makes the body's Fat shape key by inflating it, less at the hands, without creases folding through themselves.

    blender -b <in.blend> --python anim_scripts/make_fat.py -- <out.blend> <offset> [hands] [steps] [smooth]

Each body part (walk.BODY_PARTS, but not the Head: it meets the body by intersecting it, so the neck stays buried in it)
is pushed out along its normals in steps, and after each step its move from the base shape is smoothed, not its shape,
so the base's own detail (knees, fingers, the navel) rides along while a crease (the armpits, the crotch) rounds out
instead of folding. The key is written as Fat (replaced if there is one), relative to the part's base shape, and the
file is saved as out.blend, never over in.blend (the rig is edited live; append the part or Join as Shapes from it).

offset: how far out, in the .blend's units. hands: the share of it a hand takes, by its weight on the hand bones,
fading in from the wrist (default HANDS). steps, smooth: as inflate_garment.py (defaults STEPS, SMOOTH)."""
import bpy, bmesh, sys, os
from mathutils import Vector
from mathutils.kdtree import KDTree

KEY = 'Fat'
HANDS = 0.4
HAND_BONES = ('hand.L', 'hand.R')
STEPS = 10
SMOOTH = 0.5

argv = sys.argv[sys.argv.index('--') + 1:]
out_blend, offset = os.path.abspath(argv[0]), float(argv[1])
hands = float(argv[2]) if len(argv) > 2 else HANDS
steps = int(argv[3]) if len(argv) > 3 else STEPS
smooth = float(argv[4]) if len(argv) > 4 else SMOOTH
if out_blend == os.path.abspath(bpy.data.filepath):
    sys.exit('make_fat.py: out.blend must not be the .blend it was opened from')

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

def fatten(o):
    me = o.data
    if not me.shape_keys:
        o.shape_key_add(name='Basis')
    rest = [Vector(d.co) for d in me.shape_keys.reference_key.data]
    # How much of the push each vertex takes. A side not weighted yet takes its mirror's (across x) hand weight, so
    # both hands come out the same.
    groups = {o.vertex_groups[n].index for n in HAND_BONES if n in o.vertex_groups}
    on_hand = [min(1.0, sum(g.weight for g in v.groups if g.group in groups)) for v in me.vertices]
    kd = KDTree(len(rest))
    for i, c in enumerate(rest):
        kd.insert(c, i)
    kd.balance()
    on_hand = [max(w, on_hand[kd.find(Vector((-c.x, c.y, c.z)))[1]]) for w, c in zip(on_hand, rest)]
    amount = [1.0 - (1.0 - hands) * w for w in on_hand]

    bm = bmesh.new()
    bm.from_mesh(me)
    bm.verts.ensure_lookup_table()
    for v, c in zip(bm.verts, rest):
        v.co = c
    neighbours = [[e.other_vert(v).index for e in v.link_edges] for v in bm.verts]
    for _ in range(steps):
        bm.normal_update()
        for v in bm.verts:
            v.co += v.normal * (offset / steps * amount[v.index])
        moves = [v.co - c for v, c in zip(bm.verts, rest)]
        for v, c, nb, d in zip(bm.verts, rest, neighbours, moves):
            if nb:
                v.co = c + d.lerp(sum((moves[j] for j in nb), Vector()) / len(nb), smooth)
    key = me.shape_keys.key_blocks.get(KEY) or o.shape_key_add(name=KEY, from_mix=False)
    key.relative_key = me.shape_keys.reference_key
    for d, v in zip(key.data, bm.verts):
        d.co = v.co
    bm.free()
    print('FAT %s: %d vertices pushed out %.4f (hands %.2f of it, %d mostly on a hand) in %d steps, smoothed %.2f each'
          % (o.name, len(rest), offset, hands, sum(w > 0.5 for w in on_hand), steps, smooth))

parts = [o for o in walk.body_parts() if o.name != 'Head']
for o in parts:
    fatten(o)
bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
