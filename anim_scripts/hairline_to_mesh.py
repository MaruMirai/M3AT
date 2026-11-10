"""Turns the rig's `Hairline` curve (anim_scripts/make_hairline.py, edited by hand) into a mesh loop of the same points,
in place, so it can be edited with the mesh's X mirror on (curves have no mirror editing). export_frames.py reads a
mesh loop as it reads the curve.

    the Text Editor's Run Script in the open session (unsaved), or
    blender -b <blend> --python anim_scripts/hairline_to_mesh.py -- <out.blend> [+x|-x]

MIRROR_FROM: None keeps every point as it is (X mirror then only links points that are already mirror images); '+x'
(the figure's left) or '-x' (its right) keeps that half and rebuilds the other as its mirror, so every point has a
twin. The loop is cut where it crosses the middle (x = 0), each crossing put exactly on it. The mesh is in world
space (the object at the origin, unturned), so its x = 0 is the head's middle, which the mirror goes about."""
import bpy, sys
from mathutils import Vector

NAME = 'Hairline'
MIRROR_FROM = None   # None, '+x' or '-x'
MIDDLE = 0.01        # Blender units from x = 0 that count as on the middle (the drawn curve's middle points are
                     # a few thousandths off it), snapped onto it


def points(ob):
    """The curve's (or a mesh loop's) points in order, world space."""
    m = ob.matrix_world
    if ob.type == 'CURVE':
        sp = ob.data.splines[0]
        if sp.type == 'BEZIER':
            return [m @ p.co for p in sp.bezier_points]
        return [m @ Vector(p.co[:3]) for p in sp.points]
    nxt = {}
    for e in ob.data.edges:
        a, b = e.vertices
        nxt.setdefault(a, []).append(b)
        nxt.setdefault(b, []).append(a)
    order, prev, at = [], None, ob.data.edges[0].vertices[0]
    while at not in order:
        order.append(at)
        ways = [w for w in nxt[at] if w != prev]
        if not ways:
            break
        prev, at = at, ways[0]
    return [m @ ob.data.vertices[i].co for i in order]


def mirrored(pts, side):
    """The loop rebuilt from its half on side (+1 or -1): that half's points in order from one crossing of the middle
    to the other, both crossings on x = 0, then the same mirrored, back the other way."""
    n = len(pts)
    on = lambda p: p.x * side > MIDDLE
    # Start just after a point not on the kept side, so the kept half is one run.
    start = next((i for i in range(n) if not on(pts[i]) and on(pts[(i + 1) % n])), None)
    if start is None:
        raise SystemExit('the hairline does not cross the middle; nothing to mirror')
    def crossing(out, inside):
        # Where the loop crosses the middle between a point off the kept side and one on it: the first itself if it
        # is on the middle already, else the point between them at x = 0.
        if abs(out.x) <= MIDDLE or abs(inside.x - out.x) < 1e-9:
            return out.copy()
        return out.lerp(inside, (0.0 - out.x) / (inside.x - out.x))

    half = [crossing(pts[start], pts[(start + 1) % n])]
    i = (start + 1) % n
    while on(pts[i]):
        half.append(pts[i].copy())
        a, b = pts[i], pts[(i + 1) % n]
        i = (i + 1) % n
        if not on(b):
            half.append(crossing(b, a))
            break
    for p in (half[0], half[-1]):
        p.x = 0.0
    back = [Vector((-p.x, p.y, p.z)) for p in reversed(half[1:-1])]
    return half + back


def convert(side=None):
    ob = bpy.data.objects.get(NAME)
    if ob is None:
        raise SystemExit('no %s object' % NAME)
    pts = points(ob)
    if side is not None:
        pts = mirrored(pts, 1 if side == '+x' else -1)
    me = bpy.data.meshes.new(NAME)
    me.from_pydata([tuple(p) for p in pts], [(i, (i + 1) % len(pts)) for i in range(len(pts))], [])
    me.update()
    if hasattr(me, 'use_mirror_x'):
        me.use_mirror_x = True
    collections = list(ob.users_collection) or [bpy.context.scene.collection]
    bpy.data.objects.remove(ob)
    new = bpy.data.objects.new(NAME, me)
    for c in collections:
        c.objects.link(new)
    twins = sum(1 for p in pts if any((Vector((-p.x, p.y, p.z)) - q).length < 1e-4 for q in pts))
    print('HAIRLINE a mesh loop of %d points, %d with a mirror twin%s' % (len(pts), twins,
          '' if side else ' (set MIRROR_FROM to mirror one half onto the other)'))


args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
convert(args[1] if len(args) > 1 else MIRROR_FROM)
if args:
    bpy.ops.wm.save_as_mainfile(filepath=args[0], copy=True)
