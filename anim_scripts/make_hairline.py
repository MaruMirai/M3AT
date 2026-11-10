"""Draws the hairline the hair's walls hang from (Source/HairlineWalls.cs) as a closed curve, `Hairline`, on the rig's
head: freehand, by a height at each angle round the head, rather than the edge of the Scalp vertex group, which the
low-poly head puts high and which is drawn small to leave room for the scalp colour's fade.

    the Text Editor's Run Script in the open session (adds or replaces `Hairline`, unsaved), or
    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_hairline.py -- <out.blend>

LINE gives the hairline's height (a share of the head's height from the chin to the top) at angles round the head
(degrees, 0 the front, 90 the figure's left side, 180 the back), for the left side; the right is its mirror. They are
joined by one smooth periodic curve (Catmull-Rom) sampled every STEP degrees, and each sample is laid on the head's
skin by a ray straight out from the head's upright axis at that height (from inside, so it meets the skull, not the
far side of an ear), then lifted LIFT off it to show. The curve is a poly spline, so it can be edited point by point;
export_frames.py writes its points as skin.txt's hairline line, and the game lays each back on the skin."""
import bpy, math, sys
from mathutils import Vector
from mathutils.bvhtree import BVHTree

HEAD = 'Head'
NAME = 'Hairline'
STEP = 4.0       # degrees between the curve's points
LIFT = 0.03      # Blender units off the skin, so the line shows over it
# (angle, height): the forehead high, the temple corner, down the temple to a sideburn in front of the ear (the ear
# runs from about 80 to 104 degrees, 0.27 to 0.42 of the head's height), up and just over the ear, down behind it and
# round low to the nape.
LINE = [
    (0, 0.63), (20, 0.62), (38, 0.58), (52, 0.52), (62, 0.46), (68, 0.38),
    (73, 0.32), (77, 0.32), (81, 0.40), (86, 0.45), (95, 0.46), (103, 0.44),
    (108, 0.36), (118, 0.24), (135, 0.16), (158, 0.11), (180, 0.10),
]


def heights():
    """Every STEP degrees round the head (0 .. 360), the height share, by a periodic Catmull-Rom through LINE and its
    mirror."""
    keys = [(a, h) for a, h in LINE] + [(360 - a, h) for a, h in reversed(LINE) if 0 < a < 180]
    n = len(keys)
    out = []
    a = 0.0
    while a < 360 - 1e-6:
        i = max(k for k in range(n) if keys[k][0] <= a)
        p0, p1, p2, p3 = (keys[(i + d) % n] for d in (-1, 0, 1, 2))
        a1, a2 = p1[0], p2[0] if p2[0] > p1[0] else p2[0] + 360
        t = (a - a1) / (a2 - a1)
        h = 0.5 * (2 * p1[1] + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t * t
                   + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t * t * t)
        out.append((a, h))
        a += STEP
    return out


def make():
    head = bpy.data.objects[HEAD]
    m = head.matrix_world
    verts = [m @ v.co for v in head.data.vertices]
    tree = BVHTree.FromPolygons(verts, [p.vertices for p in head.data.polygons])
    top = max(v.z for v in verts)
    # The chin: the lowest point of the front of the face, near the middle (as HairBuilder.MeasureHead has it).
    front = min(v.y for v in verts)
    width = max(abs(v.x) for v in verts)
    chin = min(v.z for v in verts if v.y < front * 0.6 and abs(v.x) < 0.3 * width)
    mid_y = (min(v.y for v in verts) + max(v.y for v in verts)) / 2
    points = []
    for a, h in heights():
        z = chin + h * (top - chin)
        r = math.radians(a)
        way = Vector((math.sin(r), -math.cos(r), 0.0))   # 0 the front (-y), 90 the figure's left (+x)
        hit = tree.ray_cast(Vector((0.0, mid_y, z)), way)
        if hit[0] is None:
            print('HAIRLINE no head at %.0f degrees, height %.2f' % (a, h))
            continue
        normal = hit[1] if hit[1].dot(way) > 0 else -hit[1]
        points.append(hit[0] + normal * LIFT)
    old = bpy.data.objects.get(NAME)
    if old is not None:
        bpy.data.objects.remove(old)
    curve = bpy.data.curves.new(NAME, 'CURVE')
    curve.dimensions = '3D'
    spline = curve.splines.new('POLY')
    spline.points.add(len(points) - 1)
    for p, at in zip(spline.points, points):
        p.co = (at.x, at.y, at.z, 1.0)
    spline.use_cyclic_u = True
    curve.bevel_depth = 0.06   # a thin tube, to see it; the export reads only the points
    ob = bpy.data.objects.new(NAME, curve)
    (head.users_collection[0] if head.users_collection else bpy.context.scene.collection).objects.link(ob)
    print('HAIRLINE %d points, chin %.2f top %.2f' % (len(points), chin, top))


make()
if '--' in sys.argv:
    out = sys.argv[sys.argv.index('--') + 1:]
    if out:
        bpy.ops.wm.save_as_mainfile(filepath=out[0], copy=True)
