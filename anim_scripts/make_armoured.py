"""Makes the body's Armoured shape key: the frame a full suit of power armour gives its wearer, broader and thicker
round the bones but no longer along them, so the clips' turns still put the hands and feet about where they were.

    blender -b <in.blend> --python anim_scripts/make_armoured.py -- <out.blend> [strength]

Each body vertex (the Body part only: the head stays the wearer's own, in its helmet) is moved by each bone it is
weighted to, by that weight: away from its nearest point on the bone's line by the bone's GIRTH (the trunk's by its
TRUNK width and depth apart, in the world's x and y), then with the bone's own shift. A bone's shift is its parent's,
plus the parent's push of its head where that is off the parent's line (the hips' width carries the thighs out, the
chest's the shoulders), plus its own SHIFT (the shoulders carried out sideways), so a limb moves with the flesh round
its joint, which is where the exporter's anchors put the joint in the key. The feet and toes are not pushed down, so
the soles stay on the ground. The move is then smoothed over the mesh (SMOOTH, ROUNDS), as the bones' different pushes
meet in a step at each joint. strength scales every push (1 by default: the cataphract armour's, the game taking less
for the marine armour). Saved to out.blend, never over in.blend."""
import bpy, sys, os
from mathutils import Vector

KEY = 'Armoured'

# How far out from each bone's line its flesh is pushed, of its distance from it (1: not at all), at strength 1; a
# pair is the push at the bone's head and at its tail, eased between (the upper arm thick at the deltoid). Mostly the
# shoulders: the rest of the frame only a little, so the armour reads as broad-shouldered rather than fat.
GIRTH = {
    'spine.004': 1.06, 'spine.005': 1.03,                      # the neck, under a collar
    'shoulder': 1.80,                                           # the trapezius under the pauldrons, up and down
    'upper_arm': (1.60, 1.15), 'forearm': 1.12, 'hand': 1.05,
    'pelvis': 1.02,
    'thigh': 1.05, 'shin': 1.08, 'foot': 1.04, 'toe': 1.02,
}
# The trunk's bones, run about upright: their push sideways (x) and front to back (y) apart. The upper chest broad
# under the shoulders, the waist and hips nearly as they are.
TRUNK = {
    'spine': (1.0, 1.0), 'spine.001': (1.02, 1.04), 'spine.002': (1.08, 1.10), 'spine.003': (1.30, 1.18),
}
# Each bone's own shift, model (world) units, + out to its side in x, + up in z; its children take it too.
SHIFT = {'shoulder': Vector((0.80, 0.0, 0.15))}
NOT_DOWN = ('foot', 'toe')     # bones whose flesh below their line isn't pushed down
SMOOTH = 0.5                    # each round, the share of a vertex's move taken from its neighbours' mean
ROUNDS = 4

argv = sys.argv[sys.argv.index('--') + 1:]
out_blend = os.path.abspath(argv[0])
strength = float(argv[1]) if len(argv) > 1 else 1.0
if out_blend == os.path.abspath(bpy.data.filepath):
    sys.exit('make_armoured.py: out.blend must not be the .blend it was opened from')

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

def stem(name):
    """The bone's name without its side: upper_arm.L -> upper_arm, spine.003 stays."""
    return name[:-2] if name.endswith('.L') or name.endswith('.R') else name

def side(name):
    return 1.0 if name.endswith('.L') else -1.0 if name.endswith('.R') else 0.0

def nearest_on(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-12)))
    return a + ab * t

rig = next(o for o in bpy.data.objects if o.type == 'ARMATURE' and o.name == 'metarig')
mw = rig.matrix_world
bones = {b.name: b for b in rig.data.bones if b.use_deform}
seg = {n: (mw @ b.head_local, mw @ b.tail_local) for n, b in bones.items()}

def along(p, a, b):
    ab = b - a
    return max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-12)))

def push(name, d, t=0.5):
    """A bone's push of an offset d from its line, at t along it (0 its head), at strength (the trunk's in x and y
    apart)."""
    s = stem(name)
    if s in TRUNK:
        sx, sy = TRUNK[s]
        return Vector((d.x * (1 + (sx - 1) * strength), d.y * (1 + (sy - 1) * strength), d.z))
    girth = GIRTH.get(s, 1.0)
    if isinstance(girth, tuple):
        e = t * t * (3 - 2 * t)
        girth = girth[0] + (girth[1] - girth[0]) * e
    g = 1 + (girth - 1) * strength
    out = d * g
    if s in NOT_DOWN and d.z < 0:
        out.z = d.z
    return out

shift = {}
def shift_of(name):
    if name in shift:
        return shift[name]
    b = bones[name]
    t = Vector()
    if b.parent is not None and b.parent.name in bones:
        p = b.parent.name
        h = seg[name][0]
        c = nearest_on(h, *seg[p])
        t = shift_of(p) + (push(p, h - c, along(h, *seg[p])) - (h - c))
    own = SHIFT.get(stem(name))
    if own is not None:
        t = t + Vector((own.x * side(name), own.y, own.z)) * strength
    shift[name] = t
    return t

for n in bones:
    shift_of(n)

for o in walk.body_parts():
    if o.name == 'Head':
        continue
    me = o.data
    if not me.shape_keys:
        o.shape_key_add(name='Basis')
    ref = me.shape_keys.reference_key
    to_world, to_local = o.matrix_world, o.matrix_world.inverted()
    names = {g.index: g.name for g in o.vertex_groups}
    moves = []
    for v in me.vertices:
        p = to_world @ ref.data[v.index].co
        ws = [(names[g.group], g.weight) for g in v.groups if names.get(g.group) in bones and g.weight > 0]
        total = sum(w for _, w in ws)
        if total <= 0:
            moves.append(Vector())
            continue
        q = Vector()
        for n, w in ws:
            c = nearest_on(p, *seg[n])
            q += (c + shift[n] + push(n, p - c, along(p, *seg[n]))) * (w / total)
        moves.append(q - p)
    neighbours = [[] for _ in me.vertices]
    for e in me.edges:
        a, b = e.vertices
        neighbours[a].append(b)
        neighbours[b].append(a)
    for _ in range(ROUNDS):
        moves = [m.lerp(sum((moves[j] for j in nb), Vector()) / len(nb), SMOOTH) if nb else m
                 for m, nb in zip(moves, neighbours)]
    key = me.shape_keys.key_blocks.get(KEY) or o.shape_key_add(name=KEY, from_mix=False)
    key.relative_key = ref
    m3 = to_local.to_3x3()
    for d, r, m in zip(key.data, ref.data, moves):
        d.co = r.co + m3 @ m
    most = max(m.length for m in moves)
    print('ARMOURED %s: %d vertices, at most %.3f moved, shoulders out %.3f, hips %.3f (strength %.2f)'
          % (o.name, len(moves), most, shift['upper_arm.L'].x, shift['thigh.L'].x, strength))

bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
