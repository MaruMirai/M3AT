"""Makes a low-poly backpack for the chibi, fitted to its own back, as one .glb.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_backpack.py -- <out.glb> [out.blend]

The bag is a few rounded boxes (the bag, a lid hanging over its top, a front pocket, two buckle tabs and a bedroll
under it), each a box of quads with its edges rounded by one ring between the faces, so it is chunky and soft like the
body, not faceted. Its front stands GAP off the furthest-back point of the back it covers. The two shoulder straps are
bands laid over the skin: from the bag's top over each shoulder, down the chest and back round under the arm to the
bag's bottom corner, each point put STRAP_OFF off the body surface nearest it.

It has its own weights (the custom property own_weights): the bag, lid and pocket all take one weighting, the body's
own averaged over the skin the bag covers, so it moves whole with the back; a strap takes the body's under each of its
points, eased into the bag's over its ends. Likewise its own body keys: the bag moves by the mean of what each key does
to the skin it covers (so the Fat or Pregnant body pushes it back whole rather than bending it), a strap by the skin
under it. One surface per piece, a UV map (a box projection) and no image, so it is drawn in the apparel's colour.

Named CE_Backpack, so in Models/garments it is the garment for CE_Apparel_Backpack (Combat Extended's). The .blend is never
saved; the optional out.blend is a copy with the backpack on the rig, to model from. Directions are as the figure
stands: it faces -y, z is up. Lengths are in back heights (the chest's height, the waist joint to the neck's), so it
holds if the rig is scaled."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:]
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

body = walk.join_body()   # as export_frames.py: the body's parts (walk.BODY_PARTS) as one mesh
rig = body.parent
keys = body.data.shape_keys.key_blocks if body.data.shape_keys else []
for k in keys:
    k.value = 0.0

bw = body.matrix_world
basis = [v.co.copy() for v in body.data.vertices] if not keys else [p.co.copy() for p in keys[0].data]
verts = [bw @ p for p in basis]
tris = []
for p in body.data.polygons:
    vs = list(p.vertices)
    tris += [(vs[0], vs[i], vs[i + 1]) for i in range(1, len(vs) - 1)]
tree = BVHTree.FromPolygons(verts, tris)

def joint(name, tail=False):
    b = rig.data.bones[name]
    return rig.matrix_world @ (b.tail_local if tail else b.head_local)

WAIST = joint('spine.001')     # the waist joint
NECK = joint('spine.004')      # the neck's foot
SHOULDER = joint('upper_arm.L')
H = NECK.z - WAIST.z           # a back height
print('BACK waist %.2f, neck %.2f, back height %.3f, shoulder %s' % (WAIST.z, NECK.z, H, tuple(round(c, 2) for c in SHOULDER)))

# The bag, in back heights from the waist joint (z) and the middle of the back (x).
BAG_BOTTOM = -0.12   # below the waist joint, over the top of the seat
BAG_TOP = 0.93       # just under the neck's foot, so the head clears it when it tips back
BAG_WIDE = 0.98      # across, at the bottom
BAG_TAPER = 0.88     # the top's width over the bottom's
BAG_DEEP = 0.42      # front to back
BAG_ROUND = 0.12     # the rounding of its edges
BAG_BULGE = 0.07     # how far the middle of its back and sides swell out
GAP = 0.03           # its front off the furthest-back point of the back it covers
# The lid: over the top and hanging down the back of the bag.
LID_THICK = 0.08
LID_OVER = 0.04      # past the bag's sides and back
LID_HANG = 0.4       # down the back, a share of the bag's height
# The front pocket, on the bag's back face.
POCKET_WIDE = 0.68   # a share of the bag's width
POCKET_HIGH = 0.33   # a share of the bag's height
POCKET_DEEP = 0.13
POCKET_FROM = 0.1    # its bottom above the bag's, a share of the bag's height
# The bedroll strapped under the bag, across it.
ROLL_RADIUS = 0.13
ROLL_LONG = 1.2      # its length, in back heights
ROLL_SIDES = 8
# The shoulder straps.
STRAP_WIDE = 0.14
STRAP_THICK = 0.035
STRAP_OFF = 0.028    # off the skin (to its middle)
STRAP_APART = 0.22   # from the middle of the back, at the bag's top, each side
STRAP_SAMPLES = 16   # points along each strap
STRAP_EASE = 3       # points at each end eased into the bag's weights and keys

def back_y(x, z):
    """The back's surface behind (x, z): how far back (+y) it is, casting in from behind."""
    hit = tree.ray_cast(Vector((x, 50.0, z)), Vector((0, -1, 0)))
    return hit[0].y if hit[0] is not None else None

z0, z1 = WAIST.z + BAG_BOTTOM * H, WAIST.z + BAG_TOP * H
half = BAG_WIDE * H / 2
footprint = [back_y(half * (i / 4 - 1), z0 + (z1 - z0) * j / 6) for i in range(9) for j in range(7)]
FRONT = max(y for y in footprint if y is not None) + GAP * H
print('BAG from z %.2f to %.2f, front at y %.2f' % (z0, z1, FRONT))

def rounded_box(lo, hi, r, cuts=(1, 1, 1), shape=None):
    """A box from lo to hi (corners), its edges rounded by r: a box of quads, cuts flat rows on each face along each
    axis, its outer rows at the corners turned round the box shrunk by r. shape(p, n), if given, moves each point (n
    the way it faces). Returns (points, quads)."""
    axes = []
    for a in range(3):
        g = [lo[a], lo[a] + r] + [lo[a] + r + (hi[a] - lo[a] - 2 * r) * (k + 1) / (cuts[a] + 1) for k in range(cuts[a])] + [hi[a] - r, hi[a]]
        axes.append(g)
    n = [len(g) for g in axes]
    index, points = {}, []
    def at(i, j, k):
        if (i, j, k) not in index:
            p = Vector((axes[0][i], axes[1][j], axes[2][k]))
            c = Vector([min(max(p[a], lo[a] + r), hi[a] - r) for a in range(3)])
            d = p - c
            out = d.normalized() if d.length > 1e-9 else Vector()
            q = c + out * r
            if shape:
                q = shape(q, out)
            index[(i, j, k)] = len(points)
            points.append(q)
        return index[(i, j, k)]
    quads = []
    for a in range(3):
        b, c = (a + 1) % 3, (a + 2) % 3
        for end in (0, n[a] - 1):
            for u in range(n[b] - 1):
                for v in range(n[c] - 1):
                    corner = []
                    for du, dv in ((0, 0), (1, 0), (1, 1), (0, 1)):
                        ijk = [0, 0, 0]
                        ijk[a], ijk[b], ijk[c] = end, u + du, v + dv
                        corner.append(at(*ijk))
                    quads.append(corner if end else corner[::-1])
    return points, quads

def plain_box(lo, hi):
    """A box from lo to hi with square edges (a small part, where rounding would only add faces)."""
    points = [Vector((hi.x if i & 1 else lo.x, hi.y if i & 2 else lo.y, hi.z if i & 4 else lo.z)) for i in range(8)]
    return points, [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]]

def bag_shape(p, n):
    """The bag narrowed to the top and swollen out in the middle of its back and sides."""
    t = (p.z - z0) / (z1 - z0)
    p = Vector((p.x * (1 + (BAG_TAPER - 1) * t), p.y, p.z))
    swell = math.sin(math.pi * min(max(t, 0), 1)) * BAG_BULGE * H
    along = 1 - abs(p.x) / half
    p.y += swell * max(0.0, (p.y - FRONT) / (BAG_DEEP * H)) * (0.4 + 0.6 * along)
    p.x += math.copysign(swell * 0.5, p.x) * max(0.0, 1 - abs(p.y - FRONT - BAG_DEEP * H / 2) / (BAG_DEEP * H))
    return p

pieces = []   # (name, points, faces)
deep = BAG_DEEP * H
pieces.append(('bag',) + rounded_box(Vector((-half, FRONT, z0)), Vector((half, FRONT + deep, z1)), BAG_ROUND * H, (1, 1, 1), bag_shape))

# The lid, as wide as the bag's top and over it.
top_half = half * BAG_TAPER + (BAG_BULGE * 0.5 + LID_OVER) * H
lid_lo = Vector((-top_half, FRONT + 0.02 * H, z1 - LID_THICK * H * 0.4))
lid_hi = Vector((top_half, FRONT + deep + (BAG_BULGE + LID_OVER) * H, z1 + LID_THICK * H * 0.6))
pieces.append(('lid',) + rounded_box(lid_lo, lid_hi, LID_THICK * H * 0.45, (0, 0, 0)))
hang_lo = Vector((-top_half, lid_hi.y - LID_THICK * H, z1 - LID_HANG * (z1 - z0)))
hang_hi = Vector((top_half, lid_hi.y, lid_hi.z))
pieces.append(('flap',) + rounded_box(hang_lo, hang_hi, LID_THICK * H * 0.45, (0, 0, 0)))

# The pocket on the back face, and a tab from the flap down to it either side, as if buckled.
back_face = FRONT + deep + BAG_BULGE * H * 0.6
pw, ph = half * POCKET_WIDE, POCKET_HIGH * (z1 - z0)
pz = z0 + POCKET_FROM * (z1 - z0)
pieces.append(('pocket',) + rounded_box(Vector((-pw, back_face - 0.05 * H, pz)), Vector((pw, back_face + POCKET_DEEP * H, pz + ph)),
                                        POCKET_DEEP * H * 0.45, (0, 0, 0)))
for s in (-1, 1):
    x = s * half * 0.45
    tab_lo = Vector((x - 0.05 * H, hang_lo.y + LID_THICK * H * 0.5, hang_lo.z - 0.12 * H))
    tab_hi = Vector((x + 0.05 * H, hang_hi.y + 0.02 * H, hang_lo.z + 0.05 * H))
    pieces.append(('tab',) + plain_box(tab_lo, tab_hi))

# The bedroll under the bag, across it, set back so it clears the seat.
roll_c = Vector((0, FRONT + deep * 0.6, z0 - ROLL_RADIUS * H * 0.7))
roll_pts, roll_faces = [], []
for e, x in enumerate((-ROLL_LONG * H / 2, -ROLL_LONG * H / 2 + 0.03 * H, ROLL_LONG * H / 2 - 0.03 * H, ROLL_LONG * H / 2)):
    rad = ROLL_RADIUS * H * (0.8 if e in (0, 3) else 1.0)
    for k in range(ROLL_SIDES):
        a = 2 * math.pi * (k + 0.5) / ROLL_SIDES
        roll_pts.append(roll_c + Vector((x, rad * math.cos(a), rad * math.sin(a))))
for e in range(3):
    for k in range(ROLL_SIDES):
        k2 = (k + 1) % ROLL_SIDES
        roll_faces.append([e * ROLL_SIDES + k, e * ROLL_SIDES + k2, (e + 1) * ROLL_SIDES + k2, (e + 1) * ROLL_SIDES + k])
roll_faces.append(list(range(ROLL_SIDES))[::-1])
roll_faces.append([3 * ROLL_SIDES + k for k in range(ROLL_SIDES)])
pieces.append(('roll', roll_pts, roll_faces))

# The straps. A path of guide points round each shoulder (the left's; the right mirrors it), smoothed, each point put
# STRAP_OFF off the skin nearest it.
def onto_skin(p):
    loc, normal, _, _ = tree.find_nearest(p)
    out = (p - loc)
    out = out.normalized() if out.length > 1e-6 and out.dot(normal) > 0 else normal
    return loc + out * STRAP_OFF * H, out

def strap(sign):
    sx = SHOULDER.x * sign
    guides = [Vector((STRAP_APART * H * sign, FRONT + 0.05 * H, z1 - 0.08 * H)),            # into the bag's top
              Vector((sx * 0.55, (NECK.y + FRONT) / 2, NECK.z + 0.02 * H)),                 # over the top of the back
              Vector((sx * 0.6, NECK.y - 0.05 * H, NECK.z + 0.06 * H)),                     # over the shoulder
              Vector((sx * 0.62, NECK.y - 0.5 * H, NECK.z - 0.18 * H)),                     # down the chest
              Vector((sx * 0.75, NECK.y - 0.5 * H, WAIST.z + 0.45 * H)),
              Vector((sx * 1.0, WAIST.y - 0.1 * H, WAIST.z + 0.3 * H)),                     # under the arm
              Vector((sx * 0.95, FRONT - 0.05 * H, WAIST.z + 0.12 * H)),
              Vector((half * 0.8 * sign, FRONT + 0.05 * H, z0 + 0.1 * H))]                  # into the bag's bottom corner
    # Catmull-Rom through the guides, at even steps along them.
    g = [guides[0]] + guides + [guides[-1]]
    dense = []
    for i in range(1, len(g) - 2):
        p0, p1, p2, p3 = g[i - 1], g[i], g[i + 1], g[i + 2]
        for k in range(8):
            t = k / 8
            dense.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    dense.append(guides[-1])
    lengths = [0.0]
    for a, b in zip(dense, dense[1:]):
        lengths.append(lengths[-1] + (b - a).length)
    path = []
    for s in range(STRAP_SAMPLES):
        want = lengths[-1] * s / (STRAP_SAMPLES - 1)
        i = max(k for k in range(len(lengths)) if lengths[k] <= want + 1e-9)
        i = min(i, len(dense) - 2)
        t = (want - lengths[i]) / max(lengths[i + 1] - lengths[i], 1e-9)
        path.append(dense[i].lerp(dense[i + 1], t))
    # Onto the skin, smoothed along the strap so it doesn't take every facet, and onto it again; the ends stay at the bag.
    ends = (path[0].copy(), path[1].copy(), path[-2].copy(), path[-1].copy())
    outs = [None] * len(path)
    for _ in range(4):
        laid = [onto_skin(p) for p in path]
        path = [p for p, _ in laid]
        outs = [o for _, o in laid]
        path = [path[0]] + [(path[i - 1] + 2 * path[i] + path[i + 1]) / 4 for i in range(1, len(path) - 1)] + [path[-1]]
    path = [onto_skin(p)[0] if 1 < i < len(path) - 2 else p for i, p in enumerate(path)]
    path[0], path[1], path[-2], path[-1] = ends
    outs[0] = outs[1] = outs[-1] = outs[-2] = Vector((0, -1, 0))
    # A band of four sides along the path, flat to the skin.
    points, faces = [], []
    for i, p in enumerate(path):
        along = (path[min(i + 1, len(path) - 1)] - path[max(i - 1, 0)]).normalized()
        n = (outs[i] - along * outs[i].dot(along)).normalized()
        across = along.cross(n).normalized()
        w, t = STRAP_WIDE * H / 2, STRAP_THICK * H / 2
        points += [p - across * w - n * t, p + across * w - n * t, p + across * w + n * t, p - across * w + n * t]
    for i in range(len(path) - 1):
        for k in range(4):
            k2 = (k + 1) % 4
            faces.append([i * 4 + k, i * 4 + k2, (i + 1) * 4 + k2, (i + 1) * 4 + k])
    faces.append([3, 2, 1, 0])
    m = (len(path) - 1) * 4
    faces.append([m, m + 1, m + 2, m + 3])
    return points, faces

pieces.append(('strap.L',) + strap(1))
pieces.append(('strap.R',) + strap(-1))

# One mesh of them all.
bm = bmesh.new()
piece_of = []
for name, points, faces in pieces:
    vs = [bm.verts.new(p) for p in points]
    piece_of += [name] * len(vs)
    for f in faces:
        try:
            bm.faces.new([vs[i] for i in f])
        except ValueError:
            pass
bm.verts.index_update()
bmesh.ops.recalc_face_normals(bm, faces=[f for f in bm.faces])
# UVs: a box projection, by the way each face faces, in back heights.
uv = bm.loops.layers.uv.new()
for f in bm.faces:
    a = max(range(3), key=lambda k: abs(f.normal[k]))
    u_axis, v_axis = [k for k in range(3) if k != a]
    for l in f.loops:
        l[uv].uv = (l.vert.co[u_axis] / H, l.vert.co[v_axis] / H)
me = bpy.data.meshes.new('CE_Backpack')
bm.to_mesh(me)
bm.free()
for p in me.polygons:
    p.use_smooth = True
pack = bpy.data.objects.new('CE_Backpack', me)
bpy.context.collection.objects.link(pack)
mat = bpy.data.materials.new('Backpack')
mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.8, 0.8, 0.8, 1.0)
me.materials.append(mat)
pack['own_weights'] = True

# The body's weights and keys at a point on the skin: blended from the triangle nearest it.
groups = {g.index: g.name for g in body.vertex_groups if g.name in rig.data.bones}
body_w = [{groups[e.group]: e.weight for e in v.groups if e.group in groups and e.weight > 0} for v in body.data.vertices]
key_d = {k.name: [bw.to_3x3() @ (p.co - b) for p, b in zip(k.data, basis)] for k in keys[1:]}

def under(p):
    loc, _, ti, _ = tree.find_nearest(p)
    tri = tris[ti]
    a, b, c = (verts[i] for i in tri)
    from mathutils.geometry import barycentric_transform
    bary = barycentric_transform(loc, a, b, c, Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)))
    return list(zip(tri, bary))

def blend(parts, of):
    out = {}
    for i, t in parts:
        for b, w in of(i).items():
            out[b] = out.get(b, 0.0) + w * t
    return out

# The bag's one weighting and keys: the skin it covers, sampled over its front, its spine's share only (the shoulder
# blades are partly the arms', the seat the hips' and thighs', which would swing it).
cover = [under(Vector((half * (i / 4 - 1), FRONT, z0 + (z1 - z0) * j / 6))) for i in range(9) for j in range(7)]
bag_w = {}
for c in cover:
    for b, w in blend(c, lambda i: body_w[i]).items():
        if b in ('spine.001', 'spine.002', 'spine.003'):
            bag_w[b] = bag_w.get(b, 0.0) + w
bag_k = {n: sum((sum((d[i] * t for i, t in c), Vector()) for c in cover), Vector()) / len(cover) for n, d in key_d.items()}

weights, deltas = [], {n: [] for n in key_d}
for vi, v in enumerate(me.vertices):
    name = piece_of[vi]
    if name.startswith('strap'):
        step = vi - piece_of.index(name)
        s = step // 4
        ease = min(1.0, min(s, STRAP_SAMPLES - 1 - s) / STRAP_EASE)
        c = under(v.co)
        w = blend(c, lambda i: body_w[i])
        w = {b: ease * w.get(b, 0.0) + (1 - ease) * bag_w.get(b, 0.0) for b in set(w) | set(bag_w)}
        for n, d in key_d.items():
            deltas[n].append(sum((d[i] * t for i, t in c), Vector()).lerp(bag_k[n], 1 - ease))
    else:
        w = bag_w
        for n in key_d:
            deltas[n].append(bag_k[n])
    total = sum(w.values())
    weights.append({b: x / total for b, x in w.items() if x / total > 1e-3})
for vi, w in enumerate(weights):
    for b, x in w.items():
        g = pack.vertex_groups.get(b) or pack.vertex_groups.new(name=b)
        g.add([vi], x, 'REPLACE')
if key_d:
    pack.shape_key_add(name='Basis', from_mix=False)
    for n, ds in deltas.items():
        k = pack.shape_key_add(name=n, from_mix=False)
        k.value = 0.0
        for p, d in zip(k.data, ds):
            p.co = p.co + d
print('BAG weights %s' % ', '.join('%s %.2f' % (b, w / sum(bag_w.values())) for b, w in sorted(bag_w.items(), key=lambda x: -x[1])))
print('BACKPACK: %d vertices, %d faces, %d triangles' % (len(me.vertices), len(me.polygons), sum(len(p.vertices) - 2 for p in me.polygons)))

pack.parent = rig
pack.matrix_parent_inverse = rig.matrix_world.inverted()
pack.modifiers.new('Armature', 'ARMATURE').object = rig
for x in bpy.context.view_layer.objects:
    x.select_set(x is pack or x is rig)
bpy.context.view_layer.objects.active = pack
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=True)
print('BACKPACK -> %s' % out_glb)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
