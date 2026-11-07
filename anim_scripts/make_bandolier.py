"""Makes the heavy bandolier (Biotech's Apparel_Bandolier) from the shape of the body: a wide strap from the left shoulder
across the chest to the right hip and round the back, a row of rounds in its loops down the front, and a pouch where it
meets the hip.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_bandolier.py -- <out.glb> [out.blend]

The strap is laid on the skin along a loop: lines cast out from the trunk's middle in a slanted plane (through SHOULDER
and HIP and the line from front to back) find the middle line, its distance out smoothed round the loop (SMOOTH passes)
but never brought in under the skin, so it bridges hollows. Its width is then taken across the skin at each point rather
than across the plane, as the plane meets the right hip's side on edge, and each edge taken to the skin nearest it. Its
inside stands INNER off the skin (over the Middle layer's vests, 0.2) and it is THICK thick, with rims. The
rounds stand across the strap, as a bandolier's loops hold them, ROUNDS of them evenly down the front from ROUNDS_FROM to
ROUNDS_TO of the way from the shoulder to the hip, their points toward the neck's side.

Sizes are in Blender units at the rig's scale (the figure about 22 tall, 1.25 cells; one cell is about 17.6), and chunky:
a round is only a few pixels across at the game's usual zoom.

It has UVs and a small image of flat colours (COLOURS), so the game tints the strap and the pouch by the apparel's colour
(its stuff) and the rounds stay brass, only darkened as much by it. Weights are the body's from the point under each
vertex (the exporter takes them that way anyway, and fits the body keys from that point too). The .blend is never saved;
the optional out.blend is a copy with the bandolier on the rig, to look at or model from."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_bandolier.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

# Where the strap's middle line runs, at rest, in world space (x to the figure's left, y back, z up): over the top of the
# left shoulder, between the neck and the arm, and down to the right hip, just above the thigh's joint.
SHOULDER = Vector((1.35, 0.0, 19.9))
HIP = Vector((-2.7, 0.0, 12.2))
WIDTH = 1.7            # the strap's width
INNER = 0.3            # its inside off the skin
THICK = 0.25           # its thickness
SAMPLES = 40           # points round the loop
SMOOTH = 3             # passes of smoothing the distance out round the loop
ROUNDS = 9
ROUNDS_FROM = 0.2      # shares of the front's arc length, from the shoulder's top to the hip
ROUNDS_TO = 0.86
ROUND_RADIUS = 0.36
ROUND_LENGTH = 1.55    # the casing, across the strap
ROUND_TIP = 0.55       # the bullet's point past the casing's end
ROUND_SIDES = 6
ROUND_SHIFT = 0.12     # the round's middle off the strap's, toward the neck's side, so the points stand out over it
POUCH = (1.6, 1.35, 0.85)   # across the strap, along it, out from it
POUCH_AT = 0.95        # its place along the front, as ROUNDS_FROM
FLAP = 0.12            # the flap's thickness over its top, and how far it hangs over the front

# The image: one flat colour per part, each a square of the image (the faces' UVs all at its middle). The strap's and the
# pouch's are pale, so the apparel's colour shows; the rounds are brass and their points copper.
COLOURS = {'strap': (0.92, 0.90, 0.86), 'pouch': (0.78, 0.76, 0.72), 'brass': (1.0, 0.80, 0.38), 'tip': (0.80, 0.50, 0.32)}
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
tree = BVHTree.FromPolygons(verts, tris)

# The slanted plane: through the shoulder and the hip, and containing the line from front to back.
along = (HIP - SHOULDER).normalized()
back = Vector((0, 1, 0))
across = along.cross(back).normalized()   # the strap's width runs this way
middle = (SHOULDER + HIP) / 2
# Halfway between the chest and the back there, found by casting lines in from the front and the back (the body is too
# few vertices to average).
front_y = tree.ray_cast(Vector((middle.x, -40, middle.z)), Vector((0, 1, 0)), 80.0)[0].y
back_y = tree.ray_cast(Vector((middle.x, 40, middle.z)), Vector((0, -1, 0)), 80.0)[0].y
middle.y = (front_y + back_y) / 2

def angle_of(k):
    """The k-th way out round the loop: starting toward the shoulder, round the front to the hip, and on round the back."""
    return math.pi * 2 * k / SAMPLES

def way(a):
    return (-along * math.cos(a) - back * math.sin(a)).normalized()   # a = 0: the shoulder; a = pi / 2: the front

def centre_loop():
    """The strap's middle line on the skin round the loop: where each line out from the middle meets it, the distance
    out smoothed round the loop but never brought in under the skin."""
    raw = []
    for k in range(SAMPLES):
        hit = tree.ray_cast(middle, way(angle_of(k)), 40.0)
        if hit[0] is None:
            sys.exit('make_bandolier.py: no skin out from the middle at %.0f degrees' % math.degrees(angle_of(k)))
        raw.append((hit[0] - middle).length)
    d = raw[:]
    for _ in range(SMOOTH):
        d = [max(raw[k], (d[k - 1] + 2 * d[k] + d[(k + 1) % SAMPLES]) / 4) for k in range(SAMPLES)]
    return [middle + way(angle_of(k)) * d[k] for k in range(SAMPLES)]

# The skin's smooth normal: the body's vertex normals blended across the nearest triangle (its faces' own are flat, and
# the body is low-poly).
vertex_normals = [(bw.to_3x3() @ v.normal).normalized() for v in me.vertices]

def skin_at(p):
    loc, n, ti, dist = tree.find_nearest(p)
    a, b, c = (verts[i] for i in tris[ti])
    v0, v1, v2 = b - a, c - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-9
    wb = (d11 * d20 - d01 * d21) / den
    wc = (d00 * d21 - d01 * d20) / den
    na, nb, nc = (vertex_normals[i] for i in tris[ti])
    return loc, (na * (1 - wb - wc) + nb * wb + nc * wc).normalized()

centre = centre_loop()

# The frame at each point of the middle line: out from the skin (smoothed with the points either side), along the loop,
# and across it on the skin, so the strap lies flat on the body wherever it goes, turning to lie up the hip's side where
# the loop's plane meets the skin on edge there.
outs = [skin_at(c)[1] for c in centre]
outs = [(outs[k - 1] + outs[k] * 2 + outs[(k + 1) % SAMPLES]).normalized() for k in range(SAMPLES)]
frames = []
for k in range(SAMPLES):
    t = (centre[(k + 1) % SAMPLES] - centre[k - 1]).normalized()
    n = (outs[k] - t * outs[k].dot(t)).normalized()
    w = t.cross(n)
    if w.dot(across) < 0:
        w = -w
    frames.append((n, t, w))

# Each corner of the strap's section: the edge's point taken to the skin nearest it, pushed out along the skin's normal.
bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new('UVMap')
part_of = {}

def add_face(vs, part):
    f = bm.faces.new(vs)
    part_of[f] = part
    return f

ring = []
for k in range(SAMPLES):
    n, t, w = frames[k]
    corners = []
    for e in (-1, 1):
        loc, sn = skin_at(centre[k] + w * e * WIDTH / 2)
        sn = (sn + n).normalized()
        corners.append((loc + sn * INNER, loc + sn * (INNER + THICK)))
    # In, out on the first edge; out, in on the second: a box round the strap.
    ring.append([bm.verts.new(corners[0][0]), bm.verts.new(corners[0][1]), bm.verts.new(corners[1][1]), bm.verts.new(corners[1][0])])
for k in range(SAMPLES):
    a, b = ring[k], ring[(k + 1) % SAMPLES]
    for s in range(4):
        add_face([a[s], a[(s + 1) % 4], b[(s + 1) % 4], b[s]], 'strap')

def frame_at(k):
    """The strap's middle line on its outside, and its frame there: out from the skin, along the loop, across it."""
    r = ring[k]
    m = (r[1].co + r[2].co) / 2
    return (m,) + frames[k]

# The front: from the point most nearly over the shoulder's top round to the one most nearly at the hip.
front = list(range(0, SAMPLES // 2 + 1))
points = [frame_at(k) for k in front]
lengths = [0.0]
for i in range(1, len(points)):
    lengths.append(lengths[-1] + (points[i][0] - points[i - 1][0]).length)

def front_at(share):
    """The strap's outside and frame at a share of the front's length."""
    s = share * lengths[-1]
    i = max(1, next((i for i in range(1, len(lengths)) if lengths[i] >= s), len(lengths) - 1))
    f = (s - lengths[i - 1]) / max(1e-6, lengths[i] - lengths[i - 1])
    p0, p1 = points[i - 1], points[i]
    p = p0[0].lerp(p1[0], f)
    n = p0[1].lerp(p1[1], f).normalized()
    t = p0[2].lerp(p1[2], f).normalized()
    w = p0[3].lerp(p1[3], f).normalized()
    t = (t - n * t.dot(n)).normalized()
    w = n.cross(t) if n.cross(t).dot(w) > 0 else t.cross(n)
    return p, n, t, w

# The neck's side of the strap: the way across that goes up.
neck_side = 1 if frames[SAMPLES // 4][2].z > 0 else -1

def add_round(share):
    p, n, t, w = front_at(share)
    w = w * neck_side
    c = p + n * (ROUND_RADIUS * 0.85) + w * ROUND_SHIFT
    rings = []
    for x in (-ROUND_LENGTH / 2, ROUND_LENGTH / 2):
        rings.append([bm.verts.new(c + w * x + (t * math.cos(a) + n * math.sin(a)) * ROUND_RADIUS)
                      for a in (math.pi * 2 * (j + 0.5) / ROUND_SIDES for j in range(ROUND_SIDES))])
    tip = bm.verts.new(c + w * (ROUND_LENGTH / 2 + ROUND_TIP))
    add_face(list(reversed(rings[0])), 'brass')
    for j in range(ROUND_SIDES):
        j2 = (j + 1) % ROUND_SIDES
        add_face([rings[0][j], rings[0][j2], rings[1][j2], rings[1][j]], 'brass')
        add_face([rings[1][j], rings[1][j2], tip], 'tip')

def add_box(c, axes, half, part):
    """A box about c, its half sizes along the three axes."""
    vs = {}
    for sx in (-1, 1):
        for sy in (-1, 1):
            for sz in (-1, 1):
                vs[sx, sy, sz] = bm.verts.new(c + axes[0] * sx * half[0] + axes[1] * sy * half[1] + axes[2] * sz * half[2])
    for a in range(3):
        for s in (-1, 1):
            quad = []
            for u, v in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                k = [0, 0, 0]
                k[a] = s
                k[(a + 1) % 3] = u
                k[(a + 2) % 3] = v
                quad.append(vs[tuple(k)])
            add_face(quad if s > 0 else list(reversed(quad)), part)

for r in range(ROUNDS):
    add_round(ROUNDS_FROM + (ROUNDS_TO - ROUNDS_FROM) * r / max(1, ROUNDS - 1))

# The pouch, on the strap's outside, its flap over the top (the side toward the shoulder) and down its front.
p, n, t, w = front_at(POUCH_AT)
c = p + n * (POUCH[2] / 2 - 0.05)
add_box(c, (w, t, n), (POUCH[0] / 2, POUCH[1] / 2, POUCH[2] / 2), 'pouch')
up = -t   # toward the shoulder
add_box(c + up * (POUCH[1] / 2 - POUCH[1] * 0.2) + n * (FLAP / 2),
        (w, t, n), (POUCH[0] / 2 + FLAP / 2, POUCH[1] * 0.22, POUCH[2] / 2 + FLAP / 2), 'strap')

# The faces' outward way: each the way its normal points from its own part's middle is set by the order given above;
# recalc only flips what came out inward.
bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))

# UVs: every corner of a face at the middle of its part's square of the image.
for f in bm.faces:
    s = SQUARES.index(part_of.get(f, 'strap'))
    u = (s + 0.5) / len(SQUARES)
    for l in f.loops:
        l[uv_layer].uv = (u, 0.5)

mesh = bpy.data.meshes.new('Bandolier')
bm.to_mesh(mesh)
bm.free()
for poly in mesh.polygons:
    poly.use_smooth = False

img = bpy.data.images.new('Bandolier', CELL * len(SQUARES), CELL, alpha=False)
px = []
for y in range(CELL):
    for x in range(CELL * len(SQUARES)):
        r, g, b = COLOURS[SQUARES[x // CELL]]
        px += [r, g, b, 1.0]
img.pixels = px
img.filepath_raw = os.path.splitext(out_glb)[0] + '.png'
img.file_format = 'PNG'
img.pack()
mat = bpy.data.materials.new('Bandolier')
mat.use_nodes = True
bsdf = mat.node_tree.nodes['Principled BSDF']
tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
tex.image = img
tex.interpolation = 'Closest'
mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
mesh.materials.append(mat)

o = bpy.data.objects.new('Bandolier', mesh)
body.users_collection[0].objects.link(o)
o.parent = rig
o.matrix_parent_inverse = rig.matrix_world.inverted()   # left where it is: the vertices are in world space
o.modifiers.new('Armature', 'ARMATURE').object = rig

# Weights: the body's at the point on the skin nearest each vertex, blended across its triangle.
bones = {b.name for b in rig.data.bones if b.use_deform}
names = {g.index: g.name for g in body.vertex_groups}
def vertex_weights(i):
    return {names[g.group]: g.weight for g in me.vertices[i].groups if names[g.group] in bones and g.weight > 0}
for v in mesh.vertices:
    loc, n, ti, dist = tree.find_nearest(v.co)
    a, b, c3 = (verts[i] for i in tris[ti])
    # Barycentric shares of loc in its triangle.
    v0, v1, v2 = b - a, c3 - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-9
    wb = (d11 * d20 - d01 * d21) / den
    wc = (d00 * d21 - d01 * d20) / den
    shares = (1 - wb - wc, wb, wc)
    total = {}
    for s, i in zip(shares, tris[ti]):
        for bone, w in vertex_weights(i).items():
            total[bone] = total.get(bone, 0.0) + max(0.0, s) * w
    whole = sum(total.values()) or 1.0
    for bone, w in total.items():
        if w / whole > 1e-4:
            (o.vertex_groups.get(bone) or o.vertex_groups.new(name=bone)).add([v.index], w / whole, 'REPLACE')

print('BANDOLIER: %d vertices, %d faces, %d triangles' % (len(mesh.vertices), len(mesh.polygons),
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
print('BANDOLIER -> %s' % out_glb)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
