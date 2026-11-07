# Weights the rig's floppy ears (Floppy_Ears_Gene) to the bones of Floppy_Ears_Rig as they stand, however many there are: the chain
# Ear.L, Ear.L.001, ... and the same for .R. Neither the bones nor the mesh's places change, and any old weights on Ear.* groups go.
# Run it in the open session (the Text Editor's Run Script, then save yourself), or on a copy:
#   blender -b <copy of the rig .blend> --python anim_scripts/weight_floppy_ears.py -- <out.blend>
#
# Each vertex goes by the nearest point on its own ear's chain of bones, as a distance along the chain: all on one bone in the
# middle of it, and handing over to the next by a smooth step round each joint (BLEND). A few passes of smoothing over the mesh's
# edges (SMOOTH) then take out the jump where the nearest point leaps across the outside of a bend. Lengths are Blender units.
import sys

import bpy

EARS, EARS_RIG = 'Floppy_Ears_Gene', 'Floppy_Ears_Rig'
BLEND = 0.4          # how far either side of a joint the hand over reaches, as a share of the shorter bone there (below 0.5)
SMOOTH = 2           # passes of smoothing the weights over the edges
SMOOTH_SHARE = 0.5   # how far each pass moves a vertex's weights toward the mean of its neighbours'
WEIGHT_LEAST = 0.01  # a weight below this is dropped and the rest made up to 1


def chain_of(rig, side):
    """The ear's bones from the root: Ear.L, Ear.L.001, ... as far as they go."""
    names = ['Ear.%s' % side]
    while '%s.%03d' % (names[0], len(names)) in rig.data.bones:
        names.append('%s.%03d' % (names[0], len(names)))
    return names


def step(a, b, x):
    x = max(0.0, min(1.0, (x - a) / (b - a)))
    return x * x * (3.0 - 2.0 * x)


def along_chain(p, joints, lengths, starts):
    """The distance along the chain of the point on it nearest p."""
    best, at = float('inf'), 0.0
    for k, length in enumerate(lengths):
        way = (joints[k + 1] - joints[k]) / length
        t = max(0.0, min(length, (p - joints[k]).dot(way)))
        d = (joints[k] + way * t - p).length
        if d < best:
            best, at = d, starts[k] + t
    return at


def shares_at(s, lengths, starts):
    """Each bone's share at that distance along the chain. How far past each joint it is, so the shares add up to 1."""
    past = [1.0]
    for k in range(1, len(lengths)):
        reach = BLEND * min(lengths[k - 1], lengths[k])
        past.append(step(starts[k] - reach, starts[k] + reach, s))
    past.append(0.0)
    return [past[k] - past[k + 1] for k in range(len(lengths))]


def main():
    # First, as leaving Edit Mode writes its own copy of the mesh back, over any change made while it was on.
    if bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    o, rig = bpy.data.objects[EARS], bpy.data.objects[EARS_RIG]
    rigs = [r for r in bpy.data.objects if r.type == 'ARMATURE']
    was = {r: r.data.pose_position for r in rigs}
    for r in rigs:
        r.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    try:
        points = [o.matrix_world @ v.co for v in o.data.vertices]
        near = [set() for _ in points]
        for e in o.data.edges:
            a, b = e.vertices
            near[a].add(b)
            near[b].add(a)
        weights = [None] * len(points)
        names_of = {}
        for side in ('L', 'R'):
            names = chain_of(rig, side)
            names_of[side] = names
            bones = [rig.data.bones[n] for n in names]
            joints = [rig.matrix_world @ b.head_local for b in bones] + [rig.matrix_world @ bones[-1].tail_local]
            lengths = [(joints[k + 1] - joints[k]).length for k in range(len(bones))]
            starts = [sum(lengths[:k]) for k in range(len(bones))]
            for i, p in enumerate(points):
                if (p.x > 0.0) == (side == 'L'):
                    weights[i] = (side, shares_at(along_chain(p, joints, lengths, starts), lengths, starts))
        if len(names_of['L']) != len(names_of['R']):
            raise SystemExit('%s: the ears have %d and %d bones' % (EARS_RIG, len(names_of['L']), len(names_of['R'])))

        for _ in range(SMOOTH):
            before = [w for _, w in weights]
            for i, around in enumerate(near):
                # An edge never joins the ears, but go by side anyway, as their bones are not each other's.
                others = [before[j] for j in around if weights[j][0] == weights[i][0]]
                if others:
                    mean = [sum(w[k] for w in others) / len(others) for k in range(len(before[i]))]
                    weights[i] = (weights[i][0], [a + (b - a) * SMOOTH_SHARE for a, b in zip(before[i], mean)])

        for g in [g for g in o.vertex_groups if g.name.startswith('Ear.')]:
            o.vertex_groups.remove(g)
        for side in ('L', 'R'):
            for n in names_of[side]:
                o.vertex_groups.new(name=n)
        counts = {}
        for i, (side, shares) in enumerate(weights):
            kept = [(k, x) for k, x in enumerate(shares) if x >= WEIGHT_LEAST]
            total = sum(x for _, x in kept)
            for k, x in kept:
                name = names_of[side][k]
                o.vertex_groups[name].add([i], x / total, 'REPLACE')
                counts[name] = counts.get(name, 0) + 1
        o.data.update()
        print('WEIGHT %d vertices on %s' % (len(points), ', '.join('%s %d' % c for c in sorted(counts.items()))))
    finally:
        for r, p in was.items():
            r.data.pose_position = p
        bpy.context.view_layer.update()


main()
if '--' in sys.argv and len(sys.argv) > sys.argv.index('--') + 1:
    bpy.ops.wm.save_as_mainfile(filepath=sys.argv[sys.argv.index('--') + 1], copy=True)
