"""Fits the arms to a body key that widens the trunk (Hulk, and Fat), so they hang clear of it.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/widen_hulk_arms.py -- <out.blend> [--key Hulk]
        [--out leg lengths] [--hand share] [--smooth passes] [--measure]

The Hulk key widened the chest 0.04 to 0.056 model units a side at the arms' height, but moved the shoulder joint out only
0.026 and thickened the upper arm about 0.029 all round, so the arm's inner side hardly moved while the chest's side came
out to meet it: a drafted stance's arms, posed on the base body, went into it (what SkinnedFigure.ArmsClear patched at
run time, by guessing from the trunk's girth, which armour's shoulder plates fooled). Three steps, each left out at its
default of nothing:

--out: every vertex of the arm moved out sideways in the key by this many leg lengths times its weight on the arm's
    bones (the shoulder's at SHOULDER_SHARE, so the cap and the armpit ease into it), its mirror side the mirror way. The
    export then fits the joints (skin.txt's db lines) to the moved skin, so the arm's bones move out with it and every
    pose holds as posed. Hulk: 0.1 (about 0.05 model units), run 2026-11-23.
--hand: each hand keeps this share of the key's growth about its wrist (the wrist joint as the key moves it, tied to the
    flesh as the export ties it, JOINT_NEAREST), by its weight on the hand's bones, so the wrist ring takes part of it.
    The Hulk key grew the hand about 1.5 times, its palm's base reaching back up over the wrist. Hulk: 0.65.
--smooth: the key's move of each vertex riding the forearm FORE_LEAST or more eased halfway toward its neighbours' mean,
    this many passes, after the hand: the forearm has about two rings between the elbow and the wrist, and the key
    thickened its middle more than its wrist ring, which read as a bend partway down it. Hulk: 8.

It measures first and prints the chest's widening against the arm's inner side at a few heights (--measure: only that).
Each body part (walk.BODY_PARTS) with the key is changed; the Head has no arm. Saved as out.blend, never over the .blend
it opened (back the rig up, check, then copy it over). Each step adds to what is there, so run each once on a key."""
import bpy, sys, os
import numpy
from mathutils import Vector
from mathutils.kdtree import KDTree

SHOULDER_SHARE = 0.5  # the shoulder bone's weight counts this much (the pec and the top of the shoulder ride it)
ARM_BONES = ('upper_arm', 'forearm', 'hand', 'thumb')
JOINT_NEAREST = 16    # body vertices a joint is tied to (export_frames.py's)
FORE_LEAST = 0.3      # a vertex with this much weight on the forearm is smoothed


def option(name, default, kind=float):
    return kind(argv[argv.index(name) + 1]) if name in argv else default


argv = sys.argv[sys.argv.index('--') + 1:]
measure_only = '--measure' in argv
key_name = option('--key', 'Hulk', str)
out = option('--out', 0.0)
hand_keep = option('--hand', 1.0)
passes = option('--smooth', 0, int)
out_blend = os.path.abspath(argv[0]) if argv and not argv[0].startswith('--') else None
if not measure_only and (out_blend is None or out_blend == os.path.abspath(bpy.data.filepath)):
    sys.exit('widen_hulk_arms.py: give an out.blend other than the .blend it was opened from')

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk


def groups_of(o, v):
    return {o.vertex_groups[g.group].name: g.weight for g in v.groups}


def on_side(name, side):
    return name.endswith('.' + side) or '.' + side + '.' in name


def arm_weight(o, v, side):
    """The vertex's weight on that side's arm bones, the shoulder's at SHOULDER_SHARE."""
    total = 0.0
    for name, w in groups_of(o, v).items():
        if not on_side(name, side):
            continue
        stem = name.split('.')[0]
        if stem in ARM_BONES:
            total += w
        elif stem == 'shoulder':
            total += w * SHOULDER_SHARE
    return min(total, 1.0)


def report(o, label):
    """The chest's half width (vertices mostly off the arms) and the arm's inner side, at rest and in the key, in a few
    bands of height (model units)."""
    me = o.data
    key = me.shape_keys.key_blocks.get(key_name)
    rest = me.shape_keys.reference_key.data
    to_model = walk.MODEL_LEG / walk.LEG
    head = walk.head['upper_arm.L']
    print('MEASURE %s %s (%s), the left side, model units' % (o.name, key_name, label))
    for lo, hi in ((-0.60, -0.45), (-0.45, -0.30), (-0.30, -0.20), (-0.20, -0.12), (-0.12, -0.04), (-0.04, 0.02)):
        z0, z1 = head.z + lo * walk.LEG / walk.MODEL_LEG, head.z + hi * walk.LEG / walk.MODEL_LEG
        chest0 = chest1 = 0.0
        arm0 = arm1 = float('inf')
        for v in me.vertices:
            p0 = rest[v.index].co
            if not z0 <= p0.z < z1 or p0.x <= 0:
                continue
            p1 = key.data[v.index].co
            w = arm_weight(o, v, 'L')
            if w < 0.2:
                chest0, chest1 = max(chest0, p0.x), max(chest1, p1.x)
            elif w > 0.8:
                arm0, arm1 = min(arm0, p0.x), min(arm1, p1.x)
        if arm0 == float('inf'):
            continue
        print('  %+.2f..%+.2f of the shoulder: chest %.3f -> %.3f (+%.3f), arm inside %.3f -> %.3f (%+.3f), gap %.3f -> %.3f'
              % (lo, hi, chest0 * to_model, chest1 * to_model, (chest1 - chest0) * to_model, arm0 * to_model,
                 arm1 * to_model, (arm1 - arm0) * to_model, (arm0 - chest0) * to_model, (arm1 - chest1) * to_model))


def widen(o, key):
    step = out * walk.LEG
    moved = 0
    for v in o.data.vertices:
        for side, way in (('L', 1.0), ('R', -1.0)):
            w = arm_weight(o, v, side)
            if w > 0:
                key.data[v.index].co.x += way * step * w
                moved += 1
    print('WIDEN %s: %d vertices moved out up to %.4f (%.3f leg lengths)' % (o.name, moved, step, out))


def anchor(tree, cos, p):
    """export_frames.anchor: weights on the nearest vertices whose weighted mean is p exactly."""
    near = tree.find_n(p, JOINT_NEAREST)
    w0 = numpy.array([1.0 / (d * d + 1e-6) for _, _, d in near])
    w0 /= w0.sum()
    cs = numpy.array([(co.x, co.y, co.z, 1.0) for co, _, _ in near])
    try:
        w = w0 + cs @ numpy.linalg.solve(cs.T @ cs, numpy.array((p.x, p.y, p.z, 1.0)) - cs.T @ w0)
        if abs(w.sum() - 1.0) < 1e-6 and numpy.abs(w).max() < 3.0:
            return [(i, x) for (_, i, _), x in zip(near, w.tolist())]
    except numpy.linalg.LinAlgError:
        pass
    return [(i, x) for (_, i, _), x in zip(near, w0.tolist())]


def shrink_hands(o, key):
    """Each hand vertex keeps hand_keep of the key's growth about the wrist joint, by its weight on the hand."""
    ref = o.data.shape_keys.reference_key.data
    # The joint tied to this part's own vertices (in the part's space): the hand lies wholly on the Body.
    tree = KDTree(len(ref))
    for i, d in enumerate(ref):
        tree.insert(d.co, i)
    tree.balance()
    to_part = o.matrix_world.inverted() @ walk.arm.matrix_world
    wrist = {}
    for side in 'LR':
        a = anchor(tree, ref, to_part @ walk.head['hand.' + side])
        w0 = sum((ref[i].co * w for i, w in a), Vector())
        w1 = sum((key.data[i].co * w for i, w in a), Vector())
        wrist[side] = (w0, w1)
    moved = 0
    for v in o.data.vertices:
        g = groups_of(o, v)
        p1 = key.data[v.index].co.copy()
        p = p1.copy()
        for side in 'LR':
            s = min(sum(w for n, w in g.items() if (n.startswith('hand.') or n.startswith('thumb.')) and on_side(n, side)), 1.0)
            if s <= 0:
                continue
            w0, w1 = wrist[side]
            r0, r1 = ref[v.index].co - w0, p1 - w1
            p += (w1 + r0 + (r1 - r0) * hand_keep - p1) * s
        if (p - p1).length > 1e-9:
            key.data[v.index].co = p
            moved += 1
    print('HAND %s: %d vertices keep %.2f of the growth about the wrist' % (o.name, moved, hand_keep))


def smooth_forearms(o, key):
    ref = o.data.shape_keys.reference_key.data
    nbrs = [[] for _ in o.data.vertices]
    for e in o.data.edges:
        a, b = e.vertices
        nbrs[a].append(b)
        nbrs[b].append(a)
    fore = [sum(w for n, w in groups_of(o, v).items() if n.split('.')[0] == 'forearm') >= FORE_LEAST for v in o.data.vertices]
    d = [key.data[i].co - ref[i].co for i in range(len(ref))]
    for _ in range(passes):
        nd = list(d)
        for i in range(len(d)):
            if fore[i] and nbrs[i]:
                m = sum((d[j] for j in nbrs[i]), Vector()) / len(nbrs[i])
                nd[i] = d[i] + (m - d[i]) * 0.5
        d = nd
    for i in range(len(d)):
        if fore[i]:
            key.data[i].co = ref[i].co + d[i]
    print('SMOOTH %s: %d forearm vertices, %d passes' % (o.name, sum(fore), passes))


parts = [o for o in walk.body_parts() if o.data.shape_keys and key_name in o.data.shape_keys.key_blocks]
if not parts:
    sys.exit('widen_hulk_arms.py: no body part has a %s key' % key_name)
for o in parts:
    report(o, 'before')
if measure_only:
    sys.exit(0)
for o in parts:
    key = o.data.shape_keys.key_blocks[key_name]
    has_arm = any(n.startswith('forearm.') for n in o.vertex_groups.keys())
    if out:
        widen(o, key)
    if hand_keep != 1.0 and has_arm:
        shrink_hands(o, key)
    if passes and has_arm:
        smooth_forearms(o, key)
    report(o, 'after')
bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
print('SAVED', out_blend)
