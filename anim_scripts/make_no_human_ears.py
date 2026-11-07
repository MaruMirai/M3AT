"""Makes the head's No_Human_Ears shape key, the chibi's own ears gone, for the genes that give a pawn animal ears instead.

In Blender: open it in the Text Editor and press Run Script, with the rig's .blend open. Nothing is saved. Headless, it
saves to out.blend, or over the .blend it opened with --over:

    blender -b <in.blend> --python anim_scripts/make_no_human_ears.py -- <out.blend>
    blender -b <in.blend> --python anim_scripts/make_no_human_ears.py -- --over

Each ear stands on one loop of edges on the skull (RING). The loop is eased a little toward what's round it on the skull
(RING_EASE), as it sits in a hollow where it meets the jaw, and everything it shuts off from the rest of the head is laid
across it as a skin, each vertex at the mean of its neighbours, which keeps the triangles from folding over one another,
then raised into a dome (DOME) so the skull goes on rounding over where the ear was. The key is replaced if there is one.
Run it again after a remesh, as RING goes by place: it stops if the loop isn't there any more. Lengths are in the .blend's
units, with the head about 9.6 tall."""
import bpy, os, sys
from mathutils import Vector

KEY = 'No_Human_Ears'
OBJECT = 'Head'
# Round the left ear (+x), where it meets the skull; the right ear's is its mirror.
RING = ((3.81, -0.06, 23.71), (3.83, 1.05, 23.78), (3.65, 1.27, 23.26), (3.31, 1.02, 22.44),
        (2.93, 0.62, 21.89), (2.94, -0.03, 21.66), (3.20, -0.22, 22.05), (3.50, -0.02, 22.66))
RING_NEAR = 0.05      # how far a vertex may be from its place in RING
RING_EASE = 0.9       # the share of the way each of the loop's vertices goes to the mean of its neighbours off the ear
DOME = 0.3            # how far the middle of the skin stands out from flat
PASSES = 2000         # of averaging, far more than the few rings of it need

def nearest(rest, place):
    i = min(range(len(rest)), key=lambda v: (rest[v] - place).length)
    if (rest[i] - place).length > RING_NEAR:
        sys.exit('make_no_human_ears.py: no vertex at %s: the head has been remeshed, so set RING again' % (place,))
    return i

def relax(ear, around, start, source):
    """Each of ear's values at the mean of its neighbours', plus source; those not in ear stay as start has them."""
    now = dict(start)
    for _ in range(PASSES):
        for v in ear:
            now[v] = sum((now[o] for o in around[v]), source * 0) / len(around[v]) + source
    return now

def make_key(o):
    me = o.data
    if not me.shape_keys:
        o.shape_key_add(name='Basis', from_mix=False)
    block = me.shape_keys.key_blocks.get(KEY)
    if block is None:
        block = o.shape_key_add(name=KEY, from_mix=False)
    rest = [k.co.copy() for k in me.shape_keys.reference_key.data]
    around = [set() for _ in rest]
    for e in me.edges:
        around[e.vertices[0]].add(e.vertices[1])
        around[e.vertices[1]].add(e.vertices[0])

    # What's left of the head without the two loops is the skull, the largest piece, and the two ears.
    rings = [[nearest(rest, Vector((s * p[0], p[1], p[2]))) for p in RING] for s in (1, -1)]
    pieces, seen = [], set(rings[0]) | set(rings[1])
    for first in range(len(rest)):
        if first not in seen:
            seen.add(first)
            piece, grow = [first], [first]
            while grow:
                for n in around[grow.pop()]:
                    if n not in seen:
                        seen.add(n)
                        piece.append(n)
                        grow.append(n)
            pieces.append(piece)
    pieces.sort(key=len)
    if len(pieces) != 3 or len(pieces[0]) != len(pieces[1]):
        sys.exit('make_no_human_ears.py: the loops should cut off two like ears, not pieces of %s' % [len(p) for p in pieces])

    moved = list(rest)
    for ring in rings:
        ear = next(p for p in pieces[:2] if (rest[p[0]].x > 0) == (rest[ring[0]].x > 0))
        inside = set(ear)
        places = {v: rest[v] for v in ear}
        for v in ring:
            off_ear = [n for n in around[v] if n not in inside]
            mean = sum((rest[n] for n in off_ear), Vector()) / len(off_ear)
            places[v] = moved[v] = rest[v].lerp(mean, RING_EASE)
        flat = relax(ear, around, places, Vector())
        # Out along the loop's overall facing, away from the head's middle.
        middle = sum((places[v] for v in ring), Vector()) / len(ring)
        out = Vector()
        for a, b in zip(ring, ring[1:] + ring[:1]):
            out += (places[a] - middle).cross(places[b] - middle)
        out.normalize()
        if out.x * middle.x < 0:
            out = -out
        height = relax(ear, around, {v: 0.0 for v in ear + ring}, 1.0)
        top = max(height[v] for v in ear)
        for v in ear:
            moved[v] = flat[v] + out * (DOME * height[v] / top)

    count = 0
    for i, d in enumerate(block.data):
        d.co = moved[i]
        count += (moved[i] - rest[i]).length > 1e-5
    block.value = 0.0
    print('NO_HUMAN_EARS key %s on %s: %d of %d vertices moved' % (KEY, o.name, count, len(rest)))

make_key(bpy.data.objects[OBJECT])
if '--' in sys.argv:
    arg = sys.argv[sys.argv.index('--') + 1]
    if arg == '--over':
        bpy.ops.wm.save_mainfile()
    else:
        out_blend = os.path.abspath(arg)
        if out_blend == os.path.abspath(bpy.data.filepath):
            sys.exit('make_no_human_ears.py: to save over the .blend it was opened from, say --over')
        bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
