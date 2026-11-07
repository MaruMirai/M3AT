# Droops the rig's floppy ears (Floppy_Ears_Gene on Floppy_Ears_Rig) over and to the side of the head.
# Run it in the open session (the Text Editor's Run Script, then save yourself), or on a copy:
#   blender -b <copy of the rig .blend> --python anim_scripts/droop_floppy_ears.py -- <out.blend>
#
# The ears as made stand up with their cups facing forward, so their wide side runs across the head. Bending the bones from
# there would fold each ear across its width, crushing it, so the mesh is laid along a new line instead of being posed:
#   - each ear is turned about its own line, so its width runs front to back and its cup faces the way it will bend,
#   - it is aimed out from the root, then follows an arc over and down the side of the head, the cup coming to face the skull,
#   - the cup is made shallower (FLATTEN), as it hangs flat, and every cross-section stays the same size,
#   - each bone's rest place and roll go the same way, so the joints still run down the middle of the ear. Weights are kept.
# The first run keeps the upright mesh and bones on the objects (the custom properties `upright`), and every run starts from
# them, so running it again with other numbers replaces the droop rather than adding to it. It follows that edits made to the
# mesh after a run are lost by the next one: delete the property `upright` on the mesh and the rig to start from what is there.
#
# It prints how far the hanging part of each ear stands off the head. Lengths are in Blender units (the head is about 9.5 wide)
# and turns in degrees, from straight up toward the figure's side.
import json
import math
import sys

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

EARS, EARS_RIG, RIG = 'Floppy_Ears_Gene', 'Floppy_Ears_Rig', 'metarig'
HEAD = 'Head'
BONES = ('Ear.%s', 'Ear.%s.001', 'Ear.%s.002')
STORE = 'upright'

ROOT_AIM = 42.0     # the way the ear leaves the head
HANG_AIM = 178.0    # the way its end hangs (180 is straight down; less flares the tip away from the head)
BEND_FROM = 0.3     # along the ear from the root joint, where the arc starts (about where it comes out of the skull)
BEND_TO = 3.9       # and where it has reached HANG_AIM
STRETCH = 1.15      # the ear made longer, its width kept, so more of it hangs
SWEEP = 0.0         # the whole ear swung about the vertical through its root, + toward the back of the head
ROOT_OUT = 1.0      # the root moved out along the head's side
ROOT_DOWN = 0.7    # and down it
FLATTEN = 0.6       # the cup's depth kept
SMOOTH = 3          # passes of smoothing over the ear's edges, as the arc crowds its few vertices on the inside of the bend
SMOOTH_SHARE = 0.4  # how far each pass moves a vertex toward the middle of its neighbours
STEP = 0.01         # of the arc's table
HANGING = 1.5       # how far along the ear the clearance printed starts counting


def smooth(a, b, x):
    x = max(0.0, min(1.0, (x - a) / (b - a)))
    return x * x * (3.0 - 2.0 * x)


def turn(aim):
    """The rotation taking straight up to the way `aim`, about the figure's front-to-back line (+y turns z toward x)."""
    return Matrix.Rotation(math.radians(aim), 3, 'Y')


def arc():
    """The ear's line as a table of (place from the root, aim), one row per STEP along it, the root joint at row 0."""
    place, rows = Vector((0.0, 0.0, 0.0)), []
    n = int(BEND_TO / STEP) + 1
    for i in range(n + 1):
        aim = ROOT_AIM + (HANG_AIM - ROOT_AIM) * smooth(BEND_FROM, BEND_TO, i * STEP)
        rows.append((place.copy(), aim))
        place += turn(aim) @ Vector((0.0, 0.0, STEP))
    return rows


def along(rows, t):
    """The place and aim t along the ear, straight on past either end of the table."""
    if t <= 0.0:
        return turn(rows[0][1]) @ Vector((0.0, 0.0, t)), rows[0][1]
    i = int(t / STEP)
    if i >= len(rows) - 1:
        last = rows[-1]
        return last[0] + turn(last[1]) @ Vector((0.0, 0.0, t - (len(rows) - 1) * STEP)), last[1]
    f = t / STEP - i
    return rows[i][0].lerp(rows[i + 1][0], f), rows[i][1] + (rows[i + 1][1] - rows[i][1]) * f


class Ear:
    """One ear's upright frame, in a space where it is on the +x side (the far ear mirrored), and its way to the droop."""

    def __init__(self, joints, points):
        self.root = joints[0]
        self.line = (joints[-1] - joints[0]).normalized()
        # The width: the way the cross-sections spread most, found by power iteration.
        offs = [self.off(p) for p in points]
        width = Vector((1.0, 0.0, 0.0))
        for _ in range(16):
            width = sum((o * o.dot(width) for o in offs), Vector()).normalized()
        self.cup = self.line.cross(width).normalized()
        if self.cup.y > 0.0:   # the cup opens to the front, -y
            self.cup = -self.cup
        self.width = self.cup.cross(self.line)
        depths = [o.dot(self.cup) for o in offs]
        self.middle = 0.5 * (min(depths) + max(depths))
        self.rows = arc()

    def off(self, p):
        d = p - self.root
        return d - self.line * d.dot(self.line)

    def along(self, p):
        return (p - self.root).dot(self.line)

    def frame(self, aim):
        """The (line, cup, width) a cross-section at that aim has: the cup facing the inside of the bend, a true rotation."""
        line = turn(aim) @ Vector((0.0, 0.0, 1.0))
        cup = turn(aim) @ Vector((1.0, 0.0, 0.0))
        sweep = Matrix.Rotation(math.radians(SWEEP), 3, 'Z')
        line, cup = sweep @ line, sweep @ cup
        return line, cup, cup.cross(line)

    def place(self, p):
        o = self.off(p)
        at, aim = along(self.rows, self.along(p) * STRETCH)
        _, cup, width = self.frame(aim)
        at = Matrix.Rotation(math.radians(SWEEP), 3, 'Z') @ at
        depth = (o.dot(self.cup) - self.middle) * FLATTEN
        return self.root + Vector((ROOT_OUT, 0.0, -ROOT_DOWN)) + at + cup * depth + width * o.dot(self.width)

    def carry(self, way, t):
        """A way (a bone's roll axis) in the upright frame, turned as the ear is t along it."""
        line, cup, width = self.frame(along(self.rows, t * STRETCH)[1])
        return line * way.dot(self.line) + cup * way.dot(self.cup) + width * way.dot(self.width)


def mirrored(p, side):
    return p if side == 'L' else Vector((-p.x, p.y, p.z))


def upright_of(o, rig):
    """The upright mesh (its own space) and bones (the rig's), kept on the objects by the first run."""
    if STORE not in o or STORE not in rig:
        bones = {}
        for b in rig.data.bones:
            if b.name.startswith('Ear.'):
                bones[b.name] = [list(b.head_local), list(b.tail_local), list(b.matrix_local.col[2].to_3d())]
        o[STORE] = json.dumps([list(v.co) for v in o.data.vertices])
        rig[STORE] = json.dumps(bones)
    return [Vector(c) for c in json.loads(o[STORE])], json.loads(rig[STORE])


def smoothed(me, places):
    """places (one per vertex of me) with the bumps taken out. The open rim of the root stays put, sunk in the head anyway."""
    near = [set() for _ in places]
    uses = {}
    for e in me.edges:
        a, b = e.vertices
        near[a].add(b)
        near[b].add(a)
    for p in me.polygons:
        for k in p.edge_keys:
            uses[k] = uses.get(k, 0) + 1
    open_rim = {v for k, n in uses.items() if n == 1 for v in k}
    for _ in range(SMOOTH):
        before = list(places)
        for i, around in enumerate(near):
            if around and i not in open_rim:
                mean = sum((before[j] for j in around), Vector()) / len(around)
                places[i] = before[i].lerp(mean, SMOOTH_SHARE)
    return places


def clearance(o, side_of):
    """How far the ears stand off the head, past the root: (closest, how many vertices are inside it)."""
    head = bpy.data.objects[HEAD]
    dg = bpy.context.evaluated_depsgraph_get()
    me = head.evaluated_get(dg).to_mesh()
    me.calc_loop_triangles()
    verts = [head.matrix_world @ v.co for v in me.vertices]
    polys = [tuple(t.vertices) for t in me.loop_triangles]
    head.evaluated_get(dg).to_mesh_clear()
    tree = BVHTree.FromPolygons(verts, polys)
    closest, inside = 1e9, 0
    for i, p in side_of:
        at, normal, _, dist = tree.find_nearest(p)
        if at is None:
            continue
        if (p - at).dot(normal) < 0.0:
            dist = -dist
            inside += 1
        closest = min(closest, dist)
    return closest, inside


def main():
    # First, as leaving Edit Mode writes its own copy of the mesh back, over any change made while it was on.
    if bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    o, rig, body = bpy.data.objects[EARS], bpy.data.objects[EARS_RIG], bpy.data.objects[RIG]
    was_pose = body.data.pose_position
    body.data.pose_position = 'REST'   # the numbers are against the head at rest, whatever clip the preview has on
    bpy.context.view_layer.update()
    try:
        points, bones = upright_of(o, rig)
        mw, rw = o.matrix_world.copy(), rig.matrix_world.copy()
        world = [mw @ p for p in points]
        new = list(world)
        hanging = []
        rig_new = {}
        for side in ('L', 'R'):
            names = [b % side for b in BONES]
            joints = [mirrored(rw @ Vector(bones[n][0]), side) for n in names]
            joints.append(mirrored(rw @ Vector(bones[names[-1]][1]), side))
            own = [i for i, p in enumerate(world) if (p.x > 0.0) == (side == 'L')]
            ear = Ear(joints, [mirrored(world[i], side) for i in own])
            for i in own:
                p = mirrored(world[i], side)
                new[i] = mirrored(ear.place(p), side)
                if ear.along(p) >= HANGING:
                    hanging.append((i, new[i]))
            inv = rw.inverted_safe().to_3x3()
            for b, n in enumerate(names):
                z = mirrored(rw.to_3x3() @ Vector(bones[n][2]), side)
                z = mirrored(ear.carry(z, ear.along(joints[b])), side)
                rig_new[n] = (rw.inverted_safe() @ mirrored(ear.place(joints[b]), side),
                              rw.inverted_safe() @ mirrored(ear.place(joints[b + 1]), side), inv @ z)

        new = smoothed(o.data, new)
        hanging = [(i, new[i]) for i, _ in hanging]
        back = mw.inverted_safe()
        for v, p in zip(o.data.vertices, new):
            v.co = back @ p
        o.data.update()

        hidden = rig.hide_get(), rig.hide_viewport
        rig.hide_set(False)
        rig.hide_viewport = False
        active = bpy.context.view_layer.objects.active
        bpy.context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        for n, (head, tail, z) in rig_new.items():
            e = rig.data.edit_bones[n]
            e.head, e.tail = head, tail
            e.align_roll(z)
        bpy.ops.object.mode_set(mode='OBJECT')
        bpy.context.view_layer.objects.active = active
        rig.hide_set(hidden[0])
        rig.hide_viewport = hidden[1]
        bpy.context.view_layer.update()

        closest, inside = clearance(o, hanging)
        low = min(p.z for p in new)
        far = max(abs(p.x) for p in new)
        print('DROOP closest to the head past the root %.2f, %d of %d vertices inside it; lowest z %.2f, widest x %.2f'
              % (closest, inside, len(hanging), low, far))
    finally:
        body.data.pose_position = was_pose
        bpy.context.view_layer.update()


main()
if '--' in sys.argv and len(sys.argv) > sys.argv.index('--') + 1:
    bpy.ops.wm.save_as_mainfile(filepath=sys.argv[sys.argv.index('--') + 1], copy=True)
