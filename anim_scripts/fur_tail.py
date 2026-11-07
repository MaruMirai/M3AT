"""The Tail_Furry gene's tail: the rig's smooth Tail, shorter, with tufts in the style of the Furskin gene's (make_fur_edges.py).

export_cat.py calls build() at every export, so it follows the rig's Tail; nothing of it is kept in the .blend. To look at it:

    blender -b <copy of the rig .blend> --python anim_scripts/fur_tail.py -- <out.blend>

which saves a separate file with it beside the smooth tail, at rest and not skinned. The rig's .blend is never saved.

The tail is a tube of rings. Shortening draws the rings' middles and the joints in toward the first ring and leaves each ring its
width, so it keeps its curve and thickness. A tuft is a low wedge of three triangles as on the body: its base across the tube,
its point pulled out toward the tail's end and up off the skin, closed behind on a point on the skin. A ring's edge is too short
to make a triangle, so the base spans two of them. Its vertices are sunk a little into the tube: the game welds normals by place,
and tufts on the tube's own vertices would change its smooth shading. Lengths are in cells of the finished figure."""
import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Vector

NAME = 'Tail_Furry'
SHORT = 0.6           # its length, as a share of the smooth tail's
SEED = 7
LENGTH = 0.055        # cells: a tuft's length (as make_fur_edges.py's forearm)
LENGTH_SPREAD = 0.15  # the share its length may be longer or shorter
LIFT = 30.0           # degrees a tuft stands up off the skin
LIFT_SPREAD = 6.0     # degrees it may stand up more or less
GAP = 0.04            # cells: the least distance between tufts
ROOT_RINGS = 2        # rings from the root left bare: they are in the body, or close against it
SINK = 0.04           # how far a tuft's base is sunk into the tube, as a share of the ring's width
THIN = 0.35           # a ring narrower than this share of the widest is the tip's cap, and gets no tufts
END_TUFTS = 3         # tufts round the last full ring, closing the tail on a point
END_LENGTH = 1.2      # their length, as a share of LENGTH
END_LIFT = 12.0       # degrees they stand up


def rings_of(o):
    """The tube as a bmesh and its two open ends, each a list of vertex indices in order round it."""
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.verts.ensure_lookup_table()
    ends = []
    left = [e for e in bm.edges if e.is_boundary]
    while left:
        e = left.pop(0)
        loop = [e.verts[0], e.verts[1]]
        while True:
            step = [x for x in loop[-1].link_edges if x in left]
            if not step:
                break
            left.remove(step[0])
            loop.append(step[0].other_vert(loop[-1]))
        if loop[-1] == loop[0]:
            loop.pop()
        # From its lowest vertex and the same way round each time, so that every export puts the tufts in the same places.
        loop = [v.index for v in loop]
        at = loop.index(min(loop))
        loop = loop[at:] + loop[:at]
        ends.append(loop if loop[1] < loop[-1] else loop[:1] + loop[:0:-1])
    if len(ends) != 2:
        bm.free()
        raise SystemExit('%s: not a tube: %d open ends' % (o.name, len(ends)))
    return bm, ends


def walk_rings(bm, first):
    """The rings from the open end first, as lists of vertex indices, each vertex below the one at its place in the ring above."""
    rings = [first]
    seen = set(first)
    while True:
        below = []
        for i in rings[-1]:
            down = [e.other_vert(bm.verts[i]).index for e in bm.verts[i].link_edges]
            down = [j for j in down if j not in seen]
            if len(down) > 1:
                raise SystemExit('Tail: vertex %d does not sit in a grid of rings' % i)
            if down:
                below.append(down[0])
        if not below:
            break
        if len(below) != len(first):
            raise SystemExit('Tail: a ring of %d vertices under one of %d' % (len(below), len(first)))
        rings.append(below)
        seen.update(below)
    if len(seen) != len(bm.verts):
        raise SystemExit('Tail: %d vertices are in no ring' % (len(bm.verts) - len(seen)))
    return rings


def build(o, joints, weights, scale):
    """The furry tail, in world space at rest: its vertices, triangles (wound outward), joints and each vertex's weights. o: the
    smooth tail; joints: its joints in world space, from the root; weights: per vertex, as export_cat.weights_on gives them;
    scale: cells per Blender unit."""
    world = o.matrix_world
    bm, ends = rings_of(o)
    rest = [world @ v.co for v in o.data.vertices]
    def middle(ring):
        return sum((rest[i] for i in ring), Vector()) / len(ring)
    ends.sort(key=lambda ring: (middle(ring) - joints[0]).length)
    rings = walk_rings(bm, ends[0])
    bm.free()
    middles = [middle(r) for r in rings]
    root = middles[0]

    verts = list(rest)
    for ring, mid in zip(rings, middles):
        for i in ring:
            verts[i] = root + (mid - root) * SHORT + (rest[i] - mid)
    tris = []
    o.data.calc_loop_triangles()
    for t in o.data.loop_triangles:
        tris.append(tuple(t.vertices))
    new_joints = [root + (j - root) * SHORT for j in joints]
    middles = [root + (m - root) * SHORT for m in middles]
    ws = [list(w) for w in weights]

    widths = [max((verts[i] - m).length for i in r) for r, m in zip(rings, middles)]
    wide = max(widths)
    full = [k for k in range(len(rings)) if widths[k] >= wide * THIN]
    last = full[-1]
    count = len(rings[0])
    cell = 1.0 / scale

    def way_at(r):
        return (middles[min(r + 1, len(rings) - 1)] - middles[max(r - 1, 0)]).normalized()

    def twin_of(r, k):
        """The vertex of ring r across the figure's middle from k."""
        at = verts[rings[r][k]] - middles[r]
        at = Vector((-at.x, at.y, at.z))
        return min(range(count), key=lambda j: ((verts[rings[r][j]] - middles[r]) - at).length)

    def add_tuft(r, k, length, lift):
        ring = rings[r]
        mid = middles[r]
        a, b = ring[(k - 1) % count], ring[(k + 1) % count]
        centre = verts[ring[k]]
        n = (centre - mid).normalized()
        way = way_at(r)
        way = (way - n * way.dot(n)).normalized()
        behind = rings[r - 1][k]
        def sunk(i):
            m = middles[r] if i in ring else middles[r - 1]
            return verts[i] - (verts[i] - m) * SINK
        pull = length * cell
        tip = centre + way * pull * math.cos(lift) + n * pull * math.sin(lift)
        # Seen from outside with the fur running away: the end on the left first, so the faces are wound outward.
        if (verts[b] - verts[a]).dot(n.cross(way)) < 0:
            a, b = b, a
        # The ring whose weights the point takes: the one it lies over.
        over = min(range(len(rings)), key=lambda j: (middles[j] - tip).length)
        n0 = len(verts)
        verts.extend((sunk(a), sunk(b), sunk(behind), tip))
        ws.extend((list(weights[a]), list(weights[b]), list(weights[behind]), list(weights[rings[over][k]])))
        tris.extend(((n0 + 2, n0, n0 + 3), (n0 + 1, n0 + 2, n0 + 3), (n0, n0 + 1, n0 + 3)))

    # The end's tufts first, so the others keep their distance from them: one on the middle line and the rest set evenly
    # round from it, so the two sides match.
    top = max(range(count), key=lambda k: -abs((verts[rings[last][k]] - middles[last]).x)
              if twin_of(last, k) == k else -1e9)
    placed = []
    for t in range(END_TUFTS):
        k = (top + round(t * count / END_TUFTS)) % count
        add_tuft(last, k, LENGTH * END_LENGTH, math.radians(END_LIFT))
        placed.append(verts[rings[last][k]])
    tufts = END_TUFTS

    rnd = random.Random(SEED)
    spots = [(r, k) for r in full[ROOT_RINGS:] if r != last for k in range(count)]
    rnd.shuffle(spots)
    for r, k in spots:
        twin = twin_of(r, k)
        spread = (rnd.uniform(-1, 1), rnd.uniform(-1, 1))
        here = [(k, 1.0)] if twin == k else [(k, 1.0), (twin, 0.8)]   # a little different, so the sides don't look stamped
        at = [verts[rings[r][j]] for j, _ in here]
        if len(at) == 2 and (at[0] - at[1]).length < GAP * cell:
            continue
        if any((p - q).length < GAP * cell for p in at for q in placed):
            continue
        placed += at
        for j, share in here:
            add_tuft(r, j, LENGTH * (1 + spread[0] * share * LENGTH_SPREAD), math.radians(LIFT + spread[1] * share * LIFT_SPREAD))
            tufts += 1
    print('FUR TAIL %d tufts on %d rings of %d, %d vertices, %d triangles, %.3f cells long'
          % (tufts, len(full), count, len(verts), len(tris), sum((new_joints[i + 1] - new_joints[i]).length for i in range(len(new_joints) - 1)) * scale))
    return verts, tris, new_joints, ws


if __name__ == '__main__':
    out = sys.argv[sys.argv.index('--') + 1]
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import export_cat
    scale = export_cat.figure_scale()
    for a in bpy.data.objects:
        if a.type == 'ARMATURE':
            a.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    verts, tris, joints, ws = export_cat.fur_tail_parts(scale)
    mesh = bpy.data.meshes.new(NAME)
    mesh.from_pydata([tuple(v) for v in verts], [], tris)
    mesh.update()
    for poly in mesh.polygons:
        poly.use_smooth = True
    bpy.context.scene.collection.objects.link(bpy.data.objects.new(NAME, mesh))
    bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)
