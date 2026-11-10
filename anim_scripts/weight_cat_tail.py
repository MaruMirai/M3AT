# Weights the rig's cat tail (Tail) to the bones of Cat_Tail_Rig as they stand, however many there are: Tail, Tail.001, ... in
# order down the chain, the stub TAIL_END (if there) given nothing, as export_cat.py counts its weights for the bone before it.
# Neither the bones nor the mesh's places change, and any old weights on the rig's bones' groups go.
# Run it in the open session (the Text Editor's Run Script, then save yourself), or on a copy:
#   blender -b <copy of the rig .blend> --python anim_scripts/weight_cat_tail.py -- <out.blend>
#
# As weight_floppy_ears.py, each vertex goes by a distance along the chain of joints export_cat.py writes (each bone's head, then
# the stub's tail): all on one bone in the middle of it, handing over to the next by a smooth step round each joint (BLEND). The
# distance is the ring's, not the vertex's (fur_tail.py's walk of the tube's rings, the nearest point on the chain to the ring's
# middle), so each ring turns as one and the outside of a bend can't take a later bone than its inside. A mesh that is not a
# tube of rings goes vertex by vertex instead, smoothed over its edges (SMOOTH). Lengths are Blender units.
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fur_tail

TAIL, TAIL_RIG = 'Tail', 'Cat_Tail_Rig'
TAIL_END = 'Tail.008_end'   # as export_cat.py's
BLEND = 0.4          # how far either side of a joint the hand over reaches, as a share of the shorter bone there (below 0.5)
SMOOTH = 2           # passes of smoothing the weights over the edges, for a mesh that is not a tube of rings
SMOOTH_SHARE = 0.5
WEIGHT_LEAST = 0.01  # a weight below this is dropped and the rest made up to 1


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
    """Each bone's share at that distance along the chain, adding up to 1."""
    past = [1.0]
    for k in range(1, len(lengths)):
        reach = BLEND * min(lengths[k - 1], lengths[k])
        past.append(step(starts[k] - reach, starts[k] + reach, s))
    past.append(0.0)
    return [past[k] - past[k + 1] for k in range(len(lengths))]


def main():
    if bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    o, rig = bpy.data.objects[TAIL], bpy.data.objects[TAIL_RIG]
    rigs = [r for r in bpy.data.objects if r.type == 'ARMATURE']
    was = {r: r.data.pose_position for r in rigs}
    for r in rigs:
        r.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    try:
        chain = [b for b in rig.data.bones if b.name != TAIL_END]
        chain.sort(key=lambda b: len(b.parent_recursive))
        end = rig.data.bones.get(TAIL_END) or chain[-1]
        joints = [rig.matrix_world @ b.head_local for b in chain] + [rig.matrix_world @ end.tail_local]
        lengths = [(joints[k + 1] - joints[k]).length for k in range(len(chain))]
        starts = [sum(lengths[:k]) for k in range(len(chain))]
        points = [o.matrix_world @ v.co for v in o.data.vertices]

        weights = [None] * len(points)
        try:
            bm, ends = fur_tail.rings_of(o)
            ends.sort(key=lambda ring: (sum((points[i] for i in ring), joints[0] * 0) / len(ring) - joints[0]).length)
            rings = fur_tail.walk_rings(bm, ends[0])
            bm.free()
        except SystemExit as e:
            print('WEIGHT not a tube of rings (%s): vertex by vertex' % e)
            rings = None
        if rings:
            for ring in rings:
                mid = sum((points[i] for i in ring), joints[0] * 0) / len(ring)
                shares = shares_at(along_chain(mid, joints, lengths, starts), lengths, starts)
                for i in ring:
                    weights[i] = shares
        else:
            near = [set() for _ in points]
            for e in o.data.edges:
                a, b = e.vertices
                near[a].add(b)
                near[b].add(a)
            weights = [shares_at(along_chain(p, joints, lengths, starts), lengths, starts) for p in points]
            for _ in range(SMOOTH):
                before = list(weights)
                for i, around in enumerate(near):
                    if around:
                        mean = [sum(before[j][k] for j in around) / len(around) for k in range(len(chain))]
                        weights[i] = [a + (b - a) * SMOOTH_SHARE for a, b in zip(before[i], mean)]

        names = [b.name for b in rig.data.bones]
        for g in [g for g in o.vertex_groups if g.name in names]:
            o.vertex_groups.remove(g)
        for b in chain:
            o.vertex_groups.new(name=b.name)
        counts = {}
        for i, shares in enumerate(weights):
            kept = [(k, x) for k, x in enumerate(shares) if x >= WEIGHT_LEAST]
            total = sum(x for _, x in kept)
            for k, x in kept:
                o.vertex_groups[chain[k].name].add([i], x / total, 'REPLACE')
                counts[chain[k].name] = counts.get(chain[k].name, 0) + 1
        o.data.update()
        print('WEIGHT %d vertices%s on %s' % (len(points), ' in %d rings' % len(rings) if rings else '',
                                             ', '.join('%s %d' % (b.name, counts.get(b.name, 0)) for b in chain)))
    finally:
        for r, p in was.items():
            r.data.pose_position = p
        bpy.context.view_layer.update()


main()
if '--' in sys.argv and len(sys.argv) > sys.argv.index('--') + 1:
    bpy.ops.wm.save_as_mainfile(filepath=sys.argv[sys.argv.index('--') + 1], copy=True)
