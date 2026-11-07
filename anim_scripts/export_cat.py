# Writes the cat, pig and floppy ears and the cat tail the game draws (Source/CatEars.cs, Source/CatTail.cs) from the meshes on the rig.
#   blender -b <copy of the rig .blend> --python anim_scripts/export_cat.py -- "M3AT: Maru's 3D Animated Toons/Models/chibi"
# export_frames.py calls write() too, so a full export brings them up to date.
#
# The parts are not garments: each keeps a small armature of its own (EARS' rigs, TAIL_RIG), parented to a bone of the
# metarig, and is skinned to that armature by vertex groups named for its bones. Only those groups are read (any other is
# left behind). The game moves the bones itself, so the clips never see them; the file says where each joint sits at rest.
#
# ears.txt, ears_pig.txt, ears_floppy.txt (the game's model space, riding the head bone as hair does):
#   v x y z, f a b c       a vertex and a triangle, as the garments have them
#   ear s b x y z          side s (L, R): joint b's rest place (the root of each bone from Ear.s down, then the tip)
#   ew i s b w ...         vertex i's weights on those joints
#   inner i ...            the lining's vertices, if the mesh has a vertex group named `inner`
# tail.txt (riding the hips):
#   v, f                   as above
#   tail b x y z           joint b's rest place, from the root down; the last is the tip
#   tw i b w ...           vertex i's weights on the bones (b the joint it turns about)
# tail_furry.txt:          as tail.txt, the same tail made shorter and tufted (fur_tail.py), for the Tail_Furry gene
# Both are untextured: the game colours them with the pawn's hair.
import math
import os
import sys

import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fur_tail
import walk

FIGURE_HEIGHT = 1.25   # as export_frames.py's
EARS = (('ears.txt', 'Cat_Ears_Gene', 'Cat_Ears_Rig', 'Ears_Cat'),   # file, mesh, its rig, the gene that draws it
        ('ears_pig.txt', 'Pig_Ears_Gene', 'Pig_Ears_Rig', 'Ears_Pig'),
        ('ears_floppy.txt', 'Floppy_Ears_Gene', 'Floppy_Ears_Rig', 'Ears_Floppy'))
TAIL, TAIL_RIG = 'Tail', 'Cat_Tail_Rig'
TAIL_END = 'Tail.008_end'   # a stub on the last bone: its weights count for the bone before it
LINING_GROUP = 'inner'
WEIGHT_LEAST = 1e-4


def figure_scale():
    """Cells per Blender unit: the posed body's top at FIGURE_HEIGHT, as export_frames.py has it."""
    walk.apply_pose(walk.stand_pose())
    bpy.context.view_layer.update()
    dg = bpy.context.evaluated_depsgraph_get()
    top = 0.0
    for o in walk.body_parts():
        me = o.evaluated_get(dg).to_mesh()
        top = max(top, max((o.matrix_world @ v.co).z for v in me.vertices))
        o.evaluated_get(dg).to_mesh_clear()
    return FIGURE_HEIGHT / top


def game(v, scale):
    return Vector((-v.x, v.z, -v.y)) * scale


def weights_on(o, bones):
    """Vertex i's weights as a list of (index in bones, weight) from o's groups named for those bones, normalised."""
    names = {g.index: g.name for g in o.vertex_groups}
    out = []
    for v in o.data.vertices:
        ws = {}
        for e in v.groups:
            n = names[e.group]
            n = bones.get(n)
            if n is not None and e.weight > WEIGHT_LEAST:
                ws[n] = ws.get(n, 0.0) + e.weight
        total = sum(ws.values())
        if total <= 0.0:
            raise SystemExit('%s: vertex %d has no weight on its rig\'s bones (%s). On the floppy ears, '
                             'anim_scripts/fit_floppy_ears.py gives them some.' % (o.name, v.index, ', '.join(bones)))
        out.append([(b, w / total) for b, w in sorted(ws.items())])
    return out


def mesh_lines(o, scale, f):
    """The vertices and triangles, in the game's space (base shape, the rig at rest), the winding reversed as the garments'."""
    for v in o.data.vertices:
        f.write('v %.5f %.5f %.5f\n' % tuple(game(o.matrix_world @ v.co, scale)))
    o.data.calc_loop_triangles()
    for t in o.data.loop_triangles:
        a, b, c = t.vertices
        f.write('f %d %d %d\n' % (a, c, b))


def ear_chain(rig, side):
    """An ear's bones from the root: Ear.L, Ear.L.001, ... as far as they go, as an ear may have any number."""
    chain = [rig.data.bones['Ear.%s' % side]]
    while 'Ear.%s.%03d' % (side, len(chain)) in rig.data.bones:
        chain.append(rig.data.bones['Ear.%s.%03d' % (side, len(chain))])
    return chain


def write_ears(path, scale, mesh, rig_name, gene):
    o, rig = bpy.data.objects.get(mesh), bpy.data.objects.get(rig_name)
    if o is None or rig is None:
        print('CAT no ears (%s on %s)' % (mesh, rig_name))
        return
    bones, joints = {}, {}
    for side in ('L', 'R'):
        chain = ear_chain(rig, side)
        for k, b in enumerate(chain):
            bones[b.name] = (side, k)
        joints[side] = [rig.matrix_world @ b.head_local for b in chain] + [rig.matrix_world @ chain[-1].tail_local]
    ws = weights_on(o, bones)
    for v, w in zip(o.data.vertices, ws):
        side = 'L' if (o.matrix_world @ v.co).x > 0.0 else 'R'   # Blender's left is +x
        if any(who[0] != side for who, _ in w):
            raise SystemExit('%s: vertex %d is on the %s side but weighted to the other ear\'s bones, which would swing it '
                             'about that ear\'s joints. anim_scripts/tidy_cat_parts.py moves them.' % (mesh, v.index, side))
    inner = o.vertex_groups.get(LINING_GROUP)
    lined = [v.index for v in o.data.vertices if inner and any(e.group == inner.index and e.weight > 0.5 for e in v.groups)]
    with open(path, 'w') as f:
        f.write('# M3AT: ears for the %s gene, from the rig\'s %s (anim_scripts/export_cat.py).\n' % (gene, mesh))
        mesh_lines(o, scale, f)
        for side in ('L', 'R'):
            for k, p in enumerate(joints[side]):
                f.write('ear %s %d %.5f %.5f %.5f\n' % ((side, k) + tuple(game(p, scale))))
        for i, w in enumerate(ws):
            f.write('ew %d %s\n' % (i, ' '.join('%s %d %.5f' % (who[0], who[1], x) for who, x in w)))
        for k in range(0, len(lined), 32):
            f.write('inner %s\n' % ' '.join(str(i) for i in lined[k:k + 32]))
    print('CAT %s: %d vertices, %d triangles, %d in the lining' % (mesh, len(o.data.vertices), len(o.data.polygons), len(lined)))


def tail_rig_of(o, rig):
    """The tail's joints in world space, from the root, and each vertex's weights on them."""
    chain = [b for b in rig.data.bones if b.name != TAIL_END]
    chain.sort(key=lambda b: len(b.parent_recursive))
    bones = {b.name: k for k, b in enumerate(chain)}
    if TAIL_END in rig.data.bones:
        bones[TAIL_END] = len(chain) - 1
    end = rig.data.bones.get(TAIL_END) or chain[-1]
    return [rig.matrix_world @ b.head_local for b in chain] + [rig.matrix_world @ end.tail_local], weights_on(o, bones)


def fur_tail_parts(scale):
    """The furry tail made from the smooth one (fur_tail.py), with the rig at rest: vertices, triangles, joints, weights."""
    o, rig = bpy.data.objects[TAIL], bpy.data.objects[TAIL_RIG]
    joints, ws = tail_rig_of(o, rig)
    return fur_tail.build(o, joints, ws, scale)


def write_fur_tail(path, scale):
    if bpy.data.objects.get(TAIL) is None or bpy.data.objects.get(TAIL_RIG) is None:
        return
    verts, tris, joints, ws = fur_tail_parts(scale)
    with open(path, 'w') as f:
        f.write('# M3AT: the furry tail, made from the rig\'s %s (anim_scripts/fur_tail.py).\n' % TAIL)
        for v in verts:
            f.write('v %.5f %.5f %.5f\n' % tuple(game(v, scale)))
        for a, b, c in tris:
            f.write('f %d %d %d\n' % (a, c, b))
        for k, p in enumerate(joints):
            f.write('tail %d %.5f %.5f %.5f\n' % ((k,) + tuple(game(p, scale))))
        for i, w in enumerate(ws):
            f.write('tw %d %s\n' % (i, ' '.join('%d %.5f' % bw for bw in w)))


def write_tail(path, scale):
    o, rig = bpy.data.objects.get(TAIL), bpy.data.objects.get(TAIL_RIG)
    if o is None or rig is None:
        print('CAT no tail (%s on %s)' % (TAIL, TAIL_RIG))
        return
    joints, ws = tail_rig_of(o, rig)
    chain = joints[:-1]
    with open(path, 'w') as f:
        f.write('# M3AT: the cat tail, from the rig\'s %s (anim_scripts/export_cat.py).\n' % TAIL)
        mesh_lines(o, scale, f)
        for k, p in enumerate(joints):
            f.write('tail %d %.5f %.5f %.5f\n' % ((k,) + tuple(game(p, scale))))
        for i, w in enumerate(ws):
            f.write('tw %d %s\n' % (i, ' '.join('%d %.5f' % bw for bw in w)))
    print('CAT tail: %d vertices, %d triangles, %d bones' % (len(o.data.vertices), len(o.data.polygons), len(chain)))


def write(out, scale=None):
    """Writes the ears' files and tail.txt into out, with the rig at rest meanwhile (the .blend is never saved by a batch run)."""
    if scale is None:
        scale = figure_scale()
    rigs = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    was = {o: o.data.pose_position for o in rigs}
    for o in rigs:
        o.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    try:
        for file, mesh, rig, gene in EARS:
            write_ears(os.path.join(out, file), scale, mesh, rig, gene)
        write_tail(os.path.join(out, 'tail.txt'), scale)
        write_fur_tail(os.path.join(out, 'tail_furry.txt'), scale)
    finally:
        for o, p in was.items():
            o.data.pose_position = p
        bpy.context.view_layer.update()


if __name__ == '__main__':
    write(sys.argv[sys.argv.index('--') + 1])
