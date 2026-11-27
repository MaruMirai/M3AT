"""Makes Combat Extended's powered exoframe (CE_Apparel_ExoFrame, its sprite Things/Apparel/Exoframe/CE_Exoframe) from
the shape of the body: a load-carrying exoskeleton, an open frame of struts, joints and straps that takes a load's weight
down the back and the legs to the ground, not a suit of armour.

  - a padded hip belt round the hips (BELT_Z), with a lumbar plate on its back (LUMBAR) carrying a battery (BATTERY);
  - a spine of vertebra blocks up the middle of the back (VERTEBRAE) on a spine bar (SPINE_BAR), and either side of it
    a rail of the pack frame from the shoulder yoke down to the lumbar plate (RAIL), with a cross bar (CROSS);
  - a yoke across the top of the back (YOKE), and from each end a harness strap over the shoulder and down the front to
    the belt (STRAP), joined across the chest by a sternum strap with a buckle (STERNUM, BUCKLE);
  - on the outside of each leg a hip joint disc (HIP_JOINT), a strut down the thigh (THIGH), a knee joint disc (KNEE), a
    strut down the shin (SHIN) and an ankle joint disc (ANKLE), with a cuff strapped round the thigh and the shin (CUFFS).

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_exoframe.py -- <out.glb> [out.blend] [--low]

With --low it is simplified and lower poly (LOW's numbers): fewer sides on the discs, struts and belt, longer steps along
the straps, fewer vertebrae, and no cross bar, buckle or battery light.

Written to Models/garments/CE_ExoFrame.glb it is the garment CE_ExoFrame, drawn for CE_Apparel_ExoFrame (DEF_PREFIXES).
The game draws it untinted (it is not made from stuff), so its colours are its own (COLOURS), the sprite's greys. The
earlier armour-like frame (a chest plate, pauldrons) is kept as Models/unused/CE_ExoFrame_armour.glb.

Places are in Blender units at the rig's scale (x to the figure's left, y back, z up, the figure about 22 tall); paths
are guides, laid onto the skin. The plates and bars on the trunk carry their own weights (plate_weights: the body's
under them less the arms', shoulders', neck's and head's, as the skin over the shoulder blades is weighted mostly to the
upper arms), so the frame bends with the spine but not with the arms; the struts and joints are weighted whole on their
bone (export_frames.py's "part_weights" keeps those); the belt and cuffs take the body's at export. The .blend is never
saved; the optional out.blend is a copy with the frame on the rig, to look at."""
import bpy, bmesh, sys, math, os
import numpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
low = '--low' in argv
argv = [a for a in argv if a != '--low']
if not argv:
    sys.exit('make_exoframe.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

NAME = 'CE_ExoFrame'
INNER = 0.45          # the frame's inside off the skin: over a vest (the Middle layer, 0.2 to 0.25)
THICK = 0.28          # a plate's thickness
FOLLOW_SMOOTH = 12    # passes of smoothing a plate's depth as it follows the skin
STEP = 0.4            # the most between points along a path or round a plate's outline
RINGS = 4             # rings of points from a plate's outline in to its middle
TUBE_ROWS, TUBE_COLS = 6, 5   # a strut's points along the bone and across it
FINE = 13             # places along a strut the skin under it is looked at
DETAILS = True        # the cross bar, the buckle and the battery light

# The hip belt, padded, round the hips; the lumbar plate on its back (an outline in the back projection, (x, z), the
# left half from the top middle round to the bottom middle, the right its mirror), stood on the belt's outside.
BELT_Z = 13.0
BELT_WIDTH = 1.0
BELT_THICK = 0.3
BELT_SAMPLES = 28
LUMBAR = [(0.0, 14.5), (0.8, 14.5), (1.45, 14.15), (1.65, 13.3), (1.4, 12.45), (0.8, 12.2), (0.0, 12.15)]
# The battery on the lumbar plate: its middle (x, z), half its width, height and depth, out from the plate.
BATTERY = {'at': (0.0, 13.35), 'half': (0.95, 0.6, 0.32)}
BATTERY_LIGHT = {'at': (0.55, 13.6), 'half': (0.14, 0.1, 0.05)}

# The spine: blocks up the middle of the back on a bar, each block riding the body where it is (so the column bends).
SPINE_BAR = {'from': 14.3, 'to': 18.7, 'width': 0.4, 'thick': 0.22}
VERTEBRAE = {'from': 14.75, 'to': 18.35, 'count': 6, 'half': (0.42, 0.22, 0.2)}
# The pack frame's rails, each side of the spine, from the yoke down to the lumbar plate, and a bar across them, stood
# further off the back than the spine, so a pack sits on them.
RAIL = [Vector((1.25, 2.0, 18.4)), Vector((1.2, 2.0, 17.2)), Vector((1.1, 2.0, 15.8)), Vector((1.05, 2.0, 14.5))]
RAIL_WIDTH = 0.32
RAIL_INNER = INNER + 0.25
RAIL_THICK = 0.3
CROSS = [Vector((1.15, 2.0, 16.5)), Vector((0.0, 2.0, 16.5)), Vector((-1.15, 2.0, 16.5))]

# The yoke across the top of the back, and the harness: each strap (the left one's points) from the yoke's end over
# the shoulder and down the front to the belt, and the sternum strap across the chest between them.
YOKE = [Vector((1.7, 1.3, 18.75)), Vector((0.9, 1.5, 18.85)), Vector((0.0, 1.5, 18.9)), Vector((-0.9, 1.5, 18.85)),
        Vector((-1.7, 1.3, 18.75))]
YOKE_WIDTH = 0.55
YOKE_THICK = 0.3
STRAP = [Vector((1.7, 1.3, 18.75)), Vector((1.85, 0.6, 19.35)), Vector((1.85, -0.3, 19.4)), Vector((1.7, -1.2, 18.8)),
         Vector((1.45, -1.8, 17.6)), Vector((1.3, -2.1, 16.3)), Vector((1.2, -2.3, 15.0)), Vector((1.15, -2.3, 13.6))]
STRAP_WIDTH = 0.5
STRAP_THICK = 0.16
STERNUM = [Vector((1.4, -2.0, 16.9)), Vector((0.0, -2.2, 16.9)), Vector((-1.4, -2.0, 16.9))]
STERNUM_WIDTH = 0.32
BUCKLE = {'at': (0.0, 16.9), 'half': (0.32, 0.26, 0.08)}

# The legs: discs at the hip, knee and ankle joints on the outside of the leg; DISC is a disc's profile out from where
# it stands, (radius, height off it, colour), from the rim in.
DISC = [(1.0, 0.0, 'frame'), (1.0, 0.2, 'frame'), (0.78, 0.24, 'joint'), (0.45, 0.36, 'joint'), (0.0, 0.4, 'frame')]
SEGMENTS = 12
HIP_JOINT = {'z': 12.55, 'scale': 0.75, 'weights': {'spine': 1.0}}
KNEE = {'scale': 0.7, 'gap': 0.3, 'bone': 'shin'}
ANKLE = {'scale': 0.5, 'gap': 0.25, 'bone': 'shin'}
# The struts (tube_shell): part of a tube round the bone's line over its outside, from/to of its length, arc degrees
# either side of straight out.
THIGH = {'from': 0.04, 'to': 0.94, 'arc': 20.0, 'gap': 0.3, 'thick': 0.28, 'weights': {'thigh': 1.0}}
SHIN = {'from': 0.06, 'to': 0.95, 'arc': 20.0, 'gap': 0.3, 'thick': 0.26, 'weights': {'shin': 1.0}}
# The cuffs: (bone, share of its length), each a strap round the leg there.
CUFFS = [('thigh', 0.55), ('shin', 0.45)]
CUFF_WIDTH = 0.55
CUFF_THICK = 0.18
CUFF_INNER = 0.12
CUFF_SAMPLES = 16

# The lower poly version's numbers (--low), in place of the ones above.
LOW = {'STEP': 1.2, 'RINGS': 2, 'TUBE_ROWS': 3, 'TUBE_COLS': 3, 'SEGMENTS': 8, 'BELT_SAMPLES': 12, 'CUFF_SAMPLES': 8,
       'DISC': [(1.0, 0.0, 'frame'), (1.0, 0.24, 'frame'), (0.6, 0.36, 'joint'), (0.0, 0.4, 'joint')],
       'VERTEBRAE': dict(VERTEBRAE, count=4, half=(0.45, 0.32, 0.2)), 'DETAILS': False}
if low:
    globals().update(LOW)

COLOURS = {'plate': (0.45, 0.45, 0.46), 'edge': (0.30, 0.30, 0.31), 'frame': (0.12, 0.12, 0.13),
           'joint': (0.66, 0.66, 0.67), 'pad': (0.22, 0.22, 0.23), 'light': (0.55, 0.85, 0.95)}
CELL = 16
SQUARES = list(COLOURS)

body = walk.join_body()
rig = body.parent
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0
bw = body.matrix_world
me = body.data
verts = [bw @ v.co for v in me.vertices]
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]
vertex_normals = [(bw.to_3x3() @ v.normal).normalized() for v in me.vertices]

names = {g.index: g.name for g in body.vertex_groups}
deform = {b.name for b in rig.data.bones if b.use_deform}


def dominant(i):
    gs = [(g.weight, names[g.group]) for g in me.vertices[i].groups if names[g.group] in deform]
    return max(gs)[1] if gs else ''


dom = [dominant(i) for i in range(len(verts))]
# The trunk's skin alone: no arm (so a plate cast at the chest's side doesn't land on the arm), no head. A triangle is
# the arm's only when most of its corners are, as the skin over the shoulder blades, near the spine, is weighted mostly
# to the upper arms (the rig moves them with the arms), and leaving out every triangle touching one cast through the back.
OFF_TRUNK = ('upper_arm', 'forearm', 'hand', 'thumb', 'f_', 'palm')
trunk_tris = [t for t in tris if not any(dom[i].startswith(walk.HEAD) for i in t)
              and sum(dom[i].startswith(OFF_TRUNK) for i in t) < 2]
TRUNK = (BVHTree.FromPolygons(verts, trunk_tris), trunk_tris)
# The shoulders and trunk without the head, for the yoke and the straps over the shoulders.
shoulder_tris = [t for t in tris if not any(dom[i].startswith(walk.HEAD) for i in t)]
SHOULDERS = (BVHTree.FromPolygons(verts, shoulder_tris), shoulder_tris)
# Each leg's own skin, so a strut or cuff looks only at its leg (not a hand hanging by the hip, nor the other leg).
LEG_BONES = ('thigh', 'shin', 'foot', 'toe')
LEGS = {}
for side in ('L', 'R'):
    leg_tris = [t for t in tris if all(dom[i].startswith(LEG_BONES) and dom[i].endswith('.' + side) for i in t)]
    LEGS[side] = (BVHTree.FromPolygons(verts, leg_tris), leg_tris)


def outermost(origin, way, which, reach=20.0):
    """How far out from origin along way the outermost skin of which is, or 0 if none."""
    hit = which[0].ray_cast(origin + way * reach, -way, reach)
    return reach - hit[3] if hit[0] is not None else 0.0


def smooth_normal(t, loc):
    a, b, c = (verts[i] for i in t)
    v0, v1, v2 = b - a, c - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-9
    wb = (d11 * d20 - d01 * d21) / den
    wc = (d00 * d21 - d01 * d20) / den
    na, nb, nc = (vertex_normals[i] for i in t)
    return (na * (1 - wb - wc) + nb * wb + nc * wc).normalized()


def skin_at(p, which=TRUNK):
    loc, n, ti, dist = which[0].find_nearest(p)
    return loc, smooth_normal(which[1][ti], loc)


bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new('UVMap')
part_of = {}          # each face's colour
rigid = {}            # each vertex's own weights (the rest take the body's at export)
misses = []


def face(vs, colour):
    f = bm.faces.new(vs)
    part_of[f] = colour
    return f


def resample(path, closed=True, step=None):
    pts = path + ([path[0]] if closed else [])
    out = []
    for a, b in zip(pts, pts[1:]):
        n = max(1, math.ceil((b - a).length / (step or STEP)))
        out += [a.lerp(b, j / n) for j in range(n)]
    if not closed:
        out.append(pts[-1])
    return out


def mirrored_outline(half):
    """The whole outline from the left half (top middle round to bottom middle), going round the left then the right."""
    left = [Vector((x, z)) for x, z in half]
    right = [Vector((-p.x, p.y)) for p in reversed(left[1:-1])]
    return left + right


def cast(x, z, side):
    """The trunk's skin seen from the front (side -1) or back (1) at (x, z), and its normal there."""
    hit = TRUNK[0].ray_cast(Vector((x, 40.0 * side, z)), Vector((0, -side, 0)), 80.0)
    if hit[0] is None:
        misses.append((x, z, side))
        return skin_at(Vector((x, 0.0, z)))
    return hit[0], smooth_normal(trunk_tris[hit[2]], hit[0])


# The bones the frame on the trunk does not follow: the arms (the skin over the shoulder blades is mostly theirs, which
# would swing a hard back plate with each arm), the shoulders, the neck and the head.
PLATE_NOT = ('upper_arm', 'forearm', 'hand', 'thumb', 'f_', 'palm', 'shoulder', 'spine.004', 'spine.005', walk.HEAD)


def plate_weights(p):
    """The body's weights at the trunk's skin nearest p, blended across its triangle, less PLATE_NOT's bones, adding
    up to 1 (the chest's top bone if nothing is left)."""
    loc, n, ti, dist = TRUNK[0].find_nearest(p)
    t = trunk_tris[ti]
    a, b, c = (verts[i] for i in t)
    v0, v1, v2 = b - a, c - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-9
    wb = (d11 * d20 - d01 * d21) / den
    wc = (d00 * d21 - d01 * d20) / den
    total = {}
    for share, i in zip((1 - wb - wc, wb, wc), t):
        for g in me.vertices[i].groups:
            name = names[g.group]
            if name in deform and g.weight > 0 and not name.startswith(PLATE_NOT):
                total[name] = total.get(name, 0.0) + max(0.0, share) * g.weight
    whole = sum(total.values())
    if whole <= 0:
        return {'spine.003': 1.0}
    return {b: w / whole for b, w in total.items() if w / whole > 1e-3}


def solid(inner, outer, grid_faces, rim, colour='plate', rim_colour='edge'):
    """A shell from its inner and outer vertices (the same layout), the grid's faces on both, the rim (indices round its
    edge, in order) joined between them."""
    for f in grid_faces:
        face([outer[i] for i in f], colour)
        face([inner[i] for i in reversed(f)], colour)
    for a, b in zip(rim, rim[1:] + rim[:1]):
        face([inner[a], inner[b], outer[b], outer[a]], rim_colour)


def skin_plate(half, side, inner_off):
    """A plate cast onto the trunk from the front or back: rings from the outline in to its middle, the inside
    inner_off off the skin (its depth along the projection smoothed over its neighbours but never nearer), THICK thick.
    Gives the outer surface's points and normals, to stand things on."""
    outline = resample(mirrored_outline(half))
    centre = sum(outline, Vector((0, 0))) / len(outline)
    m = len(outline)
    flat = []
    for r in range(RINGS):
        f = 1 - r / RINGS
        flat += [centre + (p - centre) * f for p in outline]
    flat.append(centre)
    middle = len(flat) - 1
    grid_faces = []
    for r in range(RINGS - 1):
        for k in range(m):
            a, b = r * m + k, r * m + (k + 1) % m
            grid_faces.append((a, b, b + m, a + m))
    last = (RINGS - 1) * m
    for k in range(m):
        grid_faces.append((last + k, last + (k + 1) % m, middle))
    neighbours = [set() for _ in flat]
    for f in grid_faces:
        for a, b in zip(f, f[1:] + f[:1]):
            neighbours[a].add(b)
            neighbours[b].add(a)
    # Where the skin turns away (the sides) inner_off along the projection is less than inner_off off it, so the need
    # there is taken along the skin's normal.
    hits = [cast(p.x, p.y, side) for p in flat]
    need = [loc.y + side * inner_off / max(0.6, abs(n.y)) for loc, n in hits]
    depth = list(need)
    for _ in range(FOLLOW_SMOOTH):
        depth = [(d * 2 + sum(depth[j] for j in neighbours[i]) / len(neighbours[i])) / 3 for i, d in enumerate(depth)]
        depth = [max(d, n) if side > 0 else min(d, n) for d, n in zip(depth, need)]
    pts = [Vector((p.x, d, p.y)) for p, d in zip(flat, depth)]
    normals = [Vector() for _ in pts]
    for f in grid_faces:
        n = (pts[f[1]] - pts[f[0]]).cross(pts[f[-1]] - pts[f[0]])
        for i in f:
            normals[i] += n
    out = []
    for n in normals:
        n = n.normalized()
        out.append(-n if n.dot(Vector((0, side, 0))) < 0 else n)
    inner = [bm.verts.new(p) for p in pts]
    outer = [bm.verts.new(p + n * THICK) for p, n in zip(pts, out)]
    solid(inner, outer, grid_faces, list(range(m)))
    for x, y in zip(inner, outer):
        rigid[x] = rigid[y] = plate_weights(x.co)
    return [p + n * THICK for p, n in zip(pts, out)], out


def surface_at(surface, x, z):
    """The nearest of a plate's outer points to (x, z) in the projection, and its normal."""
    pts, ns = surface
    i = min(range(len(pts)), key=lambda i: (pts[i].x - x) ** 2 + (pts[i].z - z) ** 2)
    return pts[i], ns[i]


def disc(centre, out, scale, weights):
    """A turned disc standing out of a surface along out: DISC's rings, scaled, weighted whole."""
    u = out.cross(Vector((0, 0, 1)))
    if u.length < 1e-4:
        u = out.cross(Vector((1, 0, 0)))
    u.normalize()
    v = out.cross(u).normalized()
    rings = []
    for r, h, _ in DISC:
        c = centre + out * (h * scale - 0.05)
        if r == 0:
            rings.append([bm.verts.new(c)])
            continue
        rings.append([bm.verts.new(c + (u * math.cos(2 * math.pi * i / SEGMENTS) + v * math.sin(2 * math.pi * i / SEGMENTS)) * r * scale)
                      for i in range(SEGMENTS)])
    for a in range(len(rings) - 1):
        ra, rb = rings[a], rings[a + 1]
        colour = DISC[a + 1][2]
        for i in range(SEGMENTS):
            j = (i + 1) % SEGMENTS
            if len(rb) == 1:
                face([ra[i], ra[j], rb[0]], colour)
            else:
                face([ra[i], ra[j], rb[j], rb[i]], colour)
    face(list(reversed(rings[0])), 'frame')
    for ring in rings:
        for x in ring:
            rigid[x] = weights


def box(centre, out, up, half, colour, side_colour, weights):
    """A box standing out of a surface: its inside face at centre, out its way out, up turned square to it, half its
    width, height and depth; its outer face colour, its sides side_colour; weighted whole."""
    out = out.normalized()
    up = (up - out * up.dot(out)).normalized()
    across = up.cross(out).normalized()
    hw, hh, d = half[0], half[1], half[2] * 2
    c = {}
    for i in (-1, 1):
        for j in (-1, 1):
            for k in (0, 1):
                c[i, j, k] = bm.verts.new(centre + across * hw * i + up * hh * j + out * (d * k - 0.05))
    face([c[-1, -1, 1], c[1, -1, 1], c[1, 1, 1], c[-1, 1, 1]], colour)
    face([c[-1, 1, 0], c[1, 1, 0], c[1, -1, 0], c[-1, -1, 0]], side_colour)
    face([c[-1, -1, 0], c[1, -1, 0], c[1, -1, 1], c[-1, -1, 1]], side_colour)
    face([c[-1, 1, 1], c[1, 1, 1], c[1, 1, 0], c[-1, 1, 0]], side_colour)
    face([c[-1, -1, 0], c[-1, -1, 1], c[-1, 1, 1], c[-1, 1, 0]], side_colour)
    face([c[1, -1, 1], c[1, -1, 0], c[1, 1, 0], c[1, 1, 1]], side_colour)
    for x in c.values():
        rigid[x] = weights


def frames_of(pts, closed, which):
    n = len(pts)
    outs = [skin_at(p, which)[1] for p in pts]
    outs = [(outs[max(0, k - 1)] + outs[k] * 2 + outs[(k + 1) % n if closed else min(n - 1, k + 1)]).normalized()
            for k in range(n)]
    frames = []
    for k in range(n):
        t = (pts[(k + 1) % n] - pts[k - 1]) if closed else (pts[min(n - 1, k + 1)] - pts[max(0, k - 1)])
        t.normalize()
        nn = (outs[k] - t * outs[k].dot(t)).normalized()
        frames.append((nn, t, t.cross(nn)))
    return frames


def lay(path, closed, which):
    # A closed ring (the belt, a cuff) is already sampled round the body, so it keeps its own points.
    pts = [skin_at(p, which)[0] for p in (path if closed else resample(path, closed))]
    if closed:
        return pts
    n = len(pts)
    for _ in range(4):
        new = []
        for k in range(n):
            if not closed and k in (0, n - 1):
                new.append(pts[k])
                continue
            new.append((pts[k - 1] + pts[k] * 2 + pts[(k + 1) % n]) / 4)
        pts = new
    return pts


def bar(path, width, inner, thick, colour='frame', closed=False, which=TRUNK, trunk=False):
    """A flat box along the path laid on the skin (make_tac_vest.py's straps), each corner of its section pushed out.
    With trunk, its vertices are weighted as a plate's (plate_weights), so a strap over the shoulder doesn't follow the
    arm; else they take the body's at export (the belt and cuffs, which give with the thighs)."""
    pts = lay(path, closed, which)
    if closed:
        # The flat sides between a ring's points dip in toward its middle (by more the fewer they are), so the ring
        # stands that much further out.
        middle = sum(pts, Vector()) / len(pts)
        radius = sum((q - middle).length for q in pts) / len(pts)
        inner += radius * (1 - math.cos(math.pi / len(pts)))
    ring = []
    for p, (n, t, w) in zip(pts, frames_of(pts, closed, which)):
        corners = []
        for e in (-1, 1):
            q = p + w * e * width / 2
            loc, sn = skin_at(q, which)
            h = max(0.0, (q - loc).dot(sn))
            sn = (sn + n).normalized()
            corners.append((loc + sn * (h + inner), loc + sn * (h + inner + thick)))
        ring.append([bm.verts.new(corners[0][0]), bm.verts.new(corners[0][1]),
                     bm.verts.new(corners[1][1]), bm.verts.new(corners[1][0])])
    if trunk:
        for r in ring:
            for x in r:
                rigid[x] = plate_weights(x.co)
    n = len(ring)
    for k in range(n if closed else n - 1):
        a, b = ring[k], ring[(k + 1) % n]
        for s in range(4):
            face([a[s], a[(s + 1) % 4], b[(s + 1) % 4], b[s]], colour)
    if not closed:
        face(list(reversed(ring[0])), colour)
        face(ring[-1], colour)


def bone_frame(name):
    b = rig.data.bones[name]
    return rig.matrix_world @ b.head_local, rig.matrix_world @ b.tail_local


def out_from(axis, side):
    """The way straight out to the figure's side (side 1 its left), square to the bone's axis."""
    way = Vector((side, 0, 0)) - axis * axis.x * side
    return way.normalized()


def least_clearance(points, which):
    """The least any of the points is off the skin of which (below 0 inside it)."""
    return min((q - loc).dot(n) for q in points for loc, n in [skin_at(q, which)])


def tube_shell(name, spec, which):
    """Part of a tube round the bone's line (spec's from/to of its length, arc degrees either side of straight out to
    the figure's side), clearing the skin of which by gap, thick; weighted whole by spec's weights (sided)."""
    head, tail = bone_frame(name)
    axis = tail - head
    length = axis.length
    axis.normalize()
    side = 1 if head.x > 0 else -1
    middle = out_from(axis, side)
    across = axis.cross(middle).normalized()
    rows, cols = TUBE_ROWS, TUBE_COLS
    angles = [math.radians(spec['arc']) * (2 * k / (cols - 1) - 1) for k in range(cols)]
    ways = [middle * math.cos(a) + across * math.sin(a) for a in angles]
    # Each row's radius clears the skin under it, the rows then eased toward each other (never in past their need), so
    # the strut follows the leg's taper as one straight-ish plate rather than standing far off the knee. The skin is
    # looked at FINE times along the strut, not only at its rows, and each row raised until the straight face between
    # it and the next clears it; and the flat faces across it are chords, nearer the bone than their corners by sag.
    share = lambda t: spec['from'] + (spec['to'] - spec['from']) * t
    places = [head + axis * length * share(j / (rows - 1)) for j in range(rows)]
    sag = 1 / math.cos(math.radians(spec['arc']) / (cols - 1))
    fine = [(t, max(outermost(head + axis * length * share(t), w, which) for w in ways) * sag + spec['gap'])
            for t in (k / (FINE - 1) for k in range(FINE))]
    need = [max(outermost(p, w, which) for w in ways) * sag + spec['gap'] for p in places]
    radii = list(need)
    for _ in range(3):
        radii = [max(n, (radii[max(0, j - 1)] + radii[j] * 2 + radii[min(rows - 1, j + 1)]) / 4)
                 for j, n in enumerate(need)]
    for t, n in fine:
        j = min(rows - 2, int(t * (rows - 1)))
        f = t * (rows - 1) - j
        short = n - (radii[j] * (1 - f) + radii[j + 1] * f)
        if short > 0:
            radii[j] += short
            radii[j + 1] += short
    inner = [bm.verts.new(p + w * r) for p, r in zip(places, radii) for w in ways]
    outer = [bm.verts.new(p + w * (r + spec['thick'])) for p, r in zip(places, radii) for w in ways]
    r = radii[0]
    grid_faces = []
    for j in range(rows - 1):
        for k in range(cols - 1):
            a = j * cols + k
            grid_faces.append((a, a + cols, a + cols + 1, a + 1))
    rim = [k for k in range(cols)] + [j * cols + cols - 1 for j in range(1, rows)] + \
          [(rows - 1) * cols + k for k in reversed(range(cols - 1))] + [j * cols for j in reversed(range(1, rows - 1))]
    solid(inner, outer, grid_faces, rim)
    sided = '.L' if side > 0 else '.R'
    for x in inner + outer:
        rigid[x] = {b + sided: w for b, w in spec['weights'].items()}
    return radii, least_clearance([v.co for v in inner], which)


def joint_disc(name, spec, strut_r, side_name):
    """A disc on the outside of the bone's head (a joint), standing on the strut's outside (strut_r, the strut's inner
    radius, plus its thickness) or clear of the skin by gap where there is no strut; weighted whole on spec's bone."""
    head, tail = bone_frame(name)
    axis = (tail - head).normalized()
    side = 1 if side_name == 'L' else -1
    way = out_from(axis, side)
    r = max(strut_r, outermost(head, way, LEGS[side_name]) + spec['gap'])
    disc(head + way * r, way, spec['scale'], {spec['bone'] + '.' + side_name: 1.0})
    return r


def cuff_ring(name, share, which):
    """Points on the leg's skin round the bone's line at share of its length, for a cuff."""
    head, tail = bone_frame(name)
    axis = (tail - head).normalized()
    centre = head + (tail - head) * share
    u = out_from(axis, 1)
    v = axis.cross(u).normalized()
    pts = []
    for k in range(CUFF_SAMPLES):
        a = 2 * math.pi * k / CUFF_SAMPLES
        way = u * math.cos(a) + v * math.sin(a)
        hit = which[0].ray_cast(centre + way * 10.0, -way, 10.0)
        pts.append(hit[0] if hit[0] is not None else skin_at(centre + way, which)[0])
    return pts


def ring_at(z, samples):
    front_y = TRUNK[0].ray_cast(Vector((0, -40, z)), Vector((0, 1, 0)), 80.0)[0].y
    back_y = TRUNK[0].ray_cast(Vector((0, 40, z)), Vector((0, -1, 0)), 80.0)[0].y
    middle = Vector((0, (front_y + back_y) / 2, z))
    pts = []
    for k in range(samples):
        a = math.pi * 2 * k / samples
        hit = TRUNK[0].ray_cast(middle, Vector((math.sin(a), -math.cos(a), 0)), 40.0)
        if hit[0] is None:
            sys.exit('make_exoframe.py: no skin out from the hips at %.0f degrees' % math.degrees(a))
        pts.append(hit[0])
    return pts


def mirror(path):
    return [Vector((-p.x, p.y, p.z)) for p in path]


# The hip belt, the lumbar plate on its back and the battery on that.
bar(ring_at(BELT_Z, BELT_SAMPLES), BELT_WIDTH, INNER, BELT_THICK, colour='pad', closed=True)
lumbar = skin_plate(LUMBAR, 1, INNER + BELT_THICK)
p, n = surface_at(lumbar, *BATTERY['at'])
box(p, n, Vector((0, 0, 1)), BATTERY['half'], 'plate', 'edge', plate_weights(p))
if DETAILS:
    p, n = surface_at(lumbar, *BATTERY_LIGHT['at'])
    box(p + n * BATTERY['half'][2] * 2, n, Vector((0, 0, 1)), BATTERY_LIGHT['half'], 'light', 'frame', plate_weights(p))

# The spine: a bar up the middle of the back with blocks on it.
spine_path = [Vector((0.0, 2.0, z)) for z in numpy.linspace(SPINE_BAR['from'], SPINE_BAR['to'], 8 if not low else 3)]
bar(spine_path, SPINE_BAR['width'], INNER, SPINE_BAR['thick'], trunk=True)
for z in numpy.linspace(VERTEBRAE['from'], VERTEBRAE['to'], VERTEBRAE['count']):
    loc, n = cast(0.0, float(z), 1)
    n = (n + Vector((0, 1, 0))).normalized()
    box(loc + n * (INNER + SPINE_BAR['thick']), n, Vector((0, 0, 1)), VERTEBRAE['half'], 'plate', 'edge',
        plate_weights(loc))

# The pack frame's rails and the bar across them, and the yoke.
for path in (RAIL, mirror(RAIL)):
    bar(path, RAIL_WIDTH, RAIL_INNER, RAIL_THICK, trunk=True)
if DETAILS:
    bar(CROSS, RAIL_WIDTH * 0.8, RAIL_INNER + 0.05, RAIL_THICK * 0.7, trunk=True)
bar(YOKE, YOKE_WIDTH, INNER, YOKE_THICK, colour='plate', which=SHOULDERS, trunk=True)

# The harness: a strap over each shoulder down to the belt, and the sternum strap with its buckle.
for path in (STRAP, mirror(STRAP)):
    bar(path, STRAP_WIDTH, INNER * 0.6, STRAP_THICK, colour='pad', which=SHOULDERS, trunk=True)
bar(STERNUM, STERNUM_WIDTH, INNER * 0.6 + STRAP_THICK, STRAP_THICK * 0.8, colour='pad', trunk=True)
if DETAILS:
    loc, n = cast(BUCKLE['at'][0], BUCKLE['at'][1], -1)
    box(loc + n * (INNER * 0.6 + STRAP_THICK * 1.8), n, Vector((0, 0, 1)), BUCKLE['half'], 'joint', 'frame',
        plate_weights(loc))

# The legs, each side.
for side in ('L', 'R'):
    sign = 1 if side == 'L' else -1
    leg = LEGS[side]
    head, _ = bone_frame('thigh.' + side)
    hit = TRUNK[0].ray_cast(Vector((10.0 * sign, head.y, HIP_JOINT['z'])), Vector((-sign, 0, 0)), 20.0)
    if hit[0] is None:
        sys.exit('make_exoframe.py: no hip side at %s' % side)
    n = (smooth_normal(trunk_tris[hit[2]], hit[0]) + Vector((sign, 0, 0))).normalized()
    disc(hit[0] + n * (INNER + THICK), n, HIP_JOINT['scale'],
         {(b + '.' + side if b != 'spine' else b): w for b, w in HIP_JOINT['weights'].items()})
    tr, tclear = tube_shell('thigh.' + side, THIGH, leg)
    sr, sclear = tube_shell('shin.' + side, SHIN, leg)
    kr = joint_disc('shin.' + side, KNEE, max(tr[-1] + THIGH['thick'], sr[0] + SHIN['thick']), side)
    ar = joint_disc('foot.' + side, ANKLE, sr[-1] + SHIN['thick'], side)
    for bone, share in CUFFS:
        bar(cuff_ring(bone + '.' + side, share, leg), CUFF_WIDTH, CUFF_INNER, CUFF_THICK, colour='pad', closed=True,
            which=leg)
    print('EXOFRAME: leg.%s thigh strut radius %.2f..%.2f (at least %.2f off the skin), shin %.2f..%.2f (%.2f), knee '
          'disc at %.2f, ankle at %.2f' % (side, tr[0], tr[-1], tclear, sr[0], sr[-1], sclear, kr, ar))

if misses:
    print('EXOFRAME: %d plate points missed the trunk (taken to the nearest skin), e.g. %s' % (len(misses), misses[:4]))

bmesh.ops.remove_doubles(bm, verts=[v for v in bm.verts if v not in rigid], dist=1e-5)
bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
for f in bm.faces:
    u = (SQUARES.index(part_of.get(f, 'plate')) + 0.5) / len(SQUARES)
    for l in f.loops:
        l[uv_layer].uv = (u, 0.5)
rigid_index = {v.index: w for v, w in rigid.items() if v.is_valid}

mesh = bpy.data.meshes.new(NAME)
bm.to_mesh(mesh)
bm.free()
for poly in mesh.polygons:
    poly.use_smooth = False

img = bpy.data.images.new(NAME, CELL * len(SQUARES), CELL, alpha=False)
px = []
for y in range(CELL):
    for x in range(CELL * len(SQUARES)):
        r, g, b = COLOURS[SQUARES[x // CELL]]
        px += [r, g, b, 1.0]
img.pixels = px
img.filepath_raw = os.path.splitext(out_glb)[0] + '.png'
img.file_format = 'PNG'
img.pack()
mat = bpy.data.materials.new(NAME)
mat.use_nodes = True
bsdf = mat.node_tree.nodes['Principled BSDF']
tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
tex.image = img
tex.interpolation = 'Closest'
mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
mesh.materials.append(mat)

o = bpy.data.objects.new(NAME, mesh)
body.users_collection[0].objects.link(o)
o.parent = rig
o.matrix_parent_inverse = rig.matrix_world.inverted()
o.modifiers.new('Armature', 'ARMATURE').object = rig
for vi, ws in rigid_index.items():
    for b, w in ws.items():
        if b not in deform:
            sys.exit('make_exoframe.py: no deform bone %s' % b)
        (o.vertex_groups.get(b) or o.vertex_groups.new(name=b)).add([vi], w, 'REPLACE')

print('EXOFRAME: %d vertices (%d with their own weights), %d faces, %d triangles' % (len(mesh.vertices), len(rigid_index), len(mesh.polygons),
      sum(len(p.vertices) - 2 for p in mesh.polygons)))

bpy.ops.object.select_all(action='DESELECT')
o.select_set(True)
rig.hide_set(False)
rig.hide_viewport = False
rig.select_set(True)
bpy.context.view_layer.objects.active = o
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False, export_extras=True)
print('EXOFRAME -> %s' % out_glb)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
