# Fits the rig's floppy ear bones (Floppy_Ears_Rig) to their mesh (Floppy_Ears_Gene) as it stands, in place of wherever they are,
# bones placed by hand included. weight_floppy_ears.py is the other way round, the weights to the bones. Run it in the open session (the Text Editor's Run Script, then save yourself), or on a copy:
#   blender -b <copy of the rig .blend> --python anim_scripts/fit_floppy_ears.py -- <out.blend>
#
# Each ear's line is found from its surface, so the two needn't have the same vertices:
#   - the distance over the surface from where the ear comes out of the skull (its vertices inside the Head) gives every point
#     of the ear a place along it, the farthest vertex being the tip,
#   - the ring of surface at any one distance goes round the ear, so the ring's middle is on the ear's line,
#   - the root joint is the first ring's middle, the tip that farthest vertex, and each joint between where the weights hand over
#     from one bone to the next (or evenly along the ear, for bones with no weights in common).
# The mesh's places stay as they are. Its weights do too, but for two things export_cat.py stops on: a weight on the far ear's bones
# (Symmetrize copies them under the same names) goes to the same bone on the vertex's own side, and a vertex with no weight on its
# ear's bones (a knife cut leaves them) takes the mean of its neighbours'.
import sys

import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

EARS, EARS_RIG = 'Floppy_Ears_Gene', 'Floppy_Ears_Rig'
HEAD = 'Head'
ROOT_RING = 0.03            # of the ear's length past the skull, the ring the root joint is put in
FRONT = Vector((0.0, -1.0, 0.0))   # each bone's z is kept as near this as it can be, the same on both sides
WEIGHT_LEAST = 1e-4


def rest_points(o):
    """The vertices of o where the rig has them at rest, in world space. An evaluated copy, for the Head's armature."""
    dg = bpy.context.evaluated_depsgraph_get()
    e = o.evaluated_get(dg)
    me = e.to_mesh()
    me.calc_loop_triangles()
    points = [o.matrix_world @ v.co for v in me.vertices]
    tris = [tuple(t.vertices) for t in me.loop_triangles]
    e.to_mesh_clear()
    return points, tris


def inside_of(tree, p):
    at, normal, _, _ = tree.find_nearest(p)
    return at is not None and (p - at).dot(normal) < 0.0


def over_surface(points, near, seeds):
    """Each vertex's distance from the seeds along the mesh's edges."""
    far = {i: float('inf') for i in near}
    for s in seeds:
        far[s] = 0.0
    left = set(near)
    while left:
        i = min(left, key=far.__getitem__)
        left.remove(i)
        for j in near[i]:
            d = far[i] + (points[i] - points[j]).length
            if d < far[j]:
                far[j] = d
    return far


def ring_middle(points, tris, far, level):
    """The middle of the surface's ring at that distance: its pieces' midpoints by their lengths, so it doesn't lean toward
    wherever the mesh has more vertices."""
    total, length = Vector(), 0.0
    for tri in tris:
        cuts = []
        for k in range(3):
            a, b = tri[k], tri[(k + 1) % 3]
            if (far[a] < level) != (far[b] < level):
                cuts.append(points[a].lerp(points[b], (level - far[a]) / (far[b] - far[a])))
        if len(cuts) == 2:
            piece = (cuts[0] - cuts[1]).length
            total += (cuts[0] + cuts[1]) * 0.5 * piece
            length += piece
    return total / length if length > 0.0 else None


def weights_to_own_side(o):
    """Moves each vertex's weights onto the bones of the side it is on (Blender's left is +x). Returns how many it moved."""
    names = {g.index: g.name for g in o.vertex_groups}
    moved = 0
    for v in o.data.vertices:
        side = 'L' if (o.matrix_world @ v.co).x > 0.0 else 'R'
        for group, weight in [(e.group, e.weight) for e in v.groups]:
            name = names[group]
            if name.startswith('Ear.') and name[4] != side:
                o.vertex_groups[name].remove([v.index])
                o.vertex_groups['Ear.' + side + name[5:]].add([v.index], weight, 'ADD')
                moved += 1
    return moved


def weight_table(o):
    """{vertex: {group name: weight}} for the groups named for an ear bone."""
    names = {g.index: g.name for g in o.vertex_groups}
    table = {}
    for v in o.data.vertices:
        table[v.index] = {names[e.group]: e.weight for e in v.groups
                          if names[e.group].startswith('Ear.') and e.weight > WEIGHT_LEAST}
    return table


def fill_weights(o, table, near):
    """Gives the vertices with no weights the mean of their neighbours', working inward. Returns how many there were."""
    empty = [i for i, w in table.items() if not w]
    found = len(empty)
    while empty:
        waiting = []
        for i in empty:
            round_it = [table[j] for j in near[i] if table[j]]
            if not round_it:
                waiting.append(i)
                continue
            mean = {}
            for w in round_it:
                for name, x in w.items():
                    mean[name] = mean.get(name, 0.0) + x / len(round_it)
            table[i] = mean
            for name, x in mean.items():
                o.vertex_groups[name].add([i], x, 'REPLACE')
        if len(waiting) == len(empty):
            raise SystemExit('%s: %d vertices have no weights and no weighted neighbour' % (o.name, len(waiting)))
        empty = waiting
    return found


def hand_over(table, far, a, b):
    """How far along the ear the weights go from bone a to bone b; None if they share no vertices."""
    total, amount = 0.0, 0.0
    for i, d in far.items():
        share = table[i].get(a, 0.0) * table[i].get(b, 0.0)
        total += d * share
        amount += share
    return total / amount if amount > 0.0 else None


def chain_of(rig, side):
    """The ear's bones from the root: Ear.L, Ear.L.001, ... as far as they go."""
    names = ['Ear.%s' % side]
    while '%s.%03d' % (names[0], len(names)) in rig.data.bones:
        names.append('%s.%03d' % (names[0], len(names)))
    return names


def fit_side(names, side, points, tris, near, table, head_tree):
    """The ear's joints, in world space, from its vertices: the root of each of the bones named, then the tip."""
    own = {i for i in near if (points[i].x > 0.0) == (side == 'L')}
    if not own:
        raise SystemExit('%s: there is no %s ear' % (EARS, side))
    seeds = [i for i in own if inside_of(head_tree, points[i])]
    if not seeds:
        seeds = [min(own, key=lambda i: (points[i] - head_tree.find_nearest(points[i])[0]).length)]
    far = over_surface(points, {i: near[i] for i in own}, seeds)
    tip = max(own, key=far.__getitem__)
    length = far[tip]
    tris = [t for t in tris if t[0] in own]

    levels = [ROOT_RING * length]
    for k in range(len(names) - 1):
        at = hand_over(table, far, names[k], names[k + 1])
        levels.append(at if at is not None else (k + 1) * length / len(names))
    joints = [ring_middle(points, tris, far, level) for level in levels]
    if any(j is None for j in joints):
        raise SystemExit('%s: no ring of surface at %s along the %s ear' % (EARS, levels, side))
    joints.append(points[tip])
    return joints, len(seeds), levels, length


def main():
    # First, as leaving Edit Mode writes its own copy of the mesh back, over any change made while it was on.
    if bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    o, rig = bpy.data.objects[EARS], bpy.data.objects[EARS_RIG]
    rigs = [r for r in bpy.data.objects if r.type == 'ARMATURE']
    was = {r: r.data.pose_position for r in rigs}
    for r in rigs:
        r.data.pose_position = 'REST'   # the skull's place is wanted at rest, whatever clip the preview has on
    bpy.context.view_layer.update()
    try:
        head_points, head_tris = rest_points(bpy.data.objects[HEAD])
        head_tree = BVHTree.FromPolygons(head_points, head_tris)
        points, tris = rest_points(o)
        near = {i: set() for i in range(len(points))}
        for e in o.data.edges:
            a, b = e.vertices
            near[a].add(b)
            near[b].add(a)
        moved = weights_to_own_side(o)
        table = weight_table(o)
        filled = fill_weights(o, table, near)

        back = rig.matrix_world.inverted_safe()
        front = (back.to_3x3() @ FRONT).normalized()
        new = {}
        for side in ('L', 'R'):
            names = chain_of(rig, side)
            joints, seeds, levels, length = fit_side(names, side, points, tris, near, table, head_tree)
            for k, name in enumerate(names):
                new[name] = (back @ joints[k], back @ joints[k + 1])
            print('FIT %s: %d vertices in the skull, %.2f long over the surface, joints at %s; bones %s long' % (
                side, seeds, length, ', '.join('%.2f' % x for x in levels),
                ', '.join('%.2f' % (joints[k + 1] - joints[k]).length for k in range(len(names)))))

        hidden = rig.hide_get(), rig.hide_viewport
        rig.hide_set(False)
        rig.hide_viewport = False
        active = bpy.context.view_layer.objects.active
        bpy.context.view_layer.objects.active = rig
        # X-mirror off meanwhile, or setting one ear's bones would move the other's, and the ears are not each other's mirror.
        mirror = rig.data.use_mirror_x
        rig.data.use_mirror_x = False
        bpy.ops.object.mode_set(mode='EDIT')
        for name, (head, tail) in new.items():
            e = rig.data.edit_bones[name]
            e.head, e.tail = head, tail
            e.align_roll(front)
        bpy.ops.object.mode_set(mode='OBJECT')
        rig.data.use_mirror_x = mirror
        bpy.context.view_layer.objects.active = active
        rig.hide_set(hidden[0])
        rig.hide_viewport = hidden[1]
        o.data.update()
        print('FIT %d weights moved off the far ear\'s bones, %d vertices with no weights took their neighbours\'' % (moved, filled))
    finally:
        for r, p in was.items():
            r.data.pose_position = p
        bpy.context.view_layer.update()


main()
if '--' in sys.argv and len(sys.argv) > sys.argv.index('--') + 1:
    bpy.ops.wm.save_as_mainfile(filepath=sys.argv[sys.argv.index('--') + 1], copy=True)
