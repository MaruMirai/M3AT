"""Makes the slave body strap (Ideology's Apparel_BodyStrap) from the shape of the body, after its icon: a strap over each
shoulder, down the front to a ring on the chest, one strap from there down to a second ring at the waist, and a belt round
the waist through it; at the back each shoulder strap goes straight down to the belt.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_body_strap.py -- <out.glb> [out.blend]

Each strap is a path of points (STRAPS, at rest, world space: x to the figure's left, y back, z up) laid on the skin: the
path cut into short steps, each taken to the nearest point of the trunk's skin (the head, forearms and hands left out, so the
neck's strap doesn't snap onto the jaw or a hand), the line smoothed (SMOOTH passes) and then pushed back out
wherever that took it under the skin, so it bridges hollows as a strap does. Its section is a flat box WIDTH wide, its
inside INNER off the skin (over the Middle layer's vests, as the bandolier's) and THICK thick, its corners taken to the
skin across it as the bandolier's are, capped at the ends. A strap that ends at a ring stops at the ring's edge. The belt
is a closed loop round the waist at BELT_Z, found by casting out from the trunk's middle in the level plane. The rings are
tori lying on the skin (RING, RING_TUBE), their middle level with the straps' middle.

Sizes are in Blender units at the rig's scale (the figure about 22 tall, 1.25 cells), chunky as the bandolier's are.

It has UVs and a small image of flat colours (COLOURS): the straps pale, so the game tints them by the apparel's colour
(fabric or leather), the rings a light steel only darkened as much by it. Weights are the body's from the point under each
vertex (the exporter takes them that way anyway). The .blend is never saved; the optional out.blend is a copy with the
strap on the rig, to look at or model from."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_body_strap.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

WIDTH = 0.95           # a strap's width
BELT_WIDTH = 1.1       # the belt's
INNER = 0.3            # the inside off the skin
THICK = 0.22           # the thickness
BELT_LIFT = 0.1        # the belt's inside further off the skin, so it lies over the back straps' ends
STEP = 0.3             # the length of a step along a strap's path
SMOOTH = 4             # passes of smoothing a strap's line
BELT_Z = 14.6          # the belt's height (the waist, over the chibi's round belly)
BELT_SAMPLES = 40
CHEST = Vector((0.0, -2.0, 17.0))   # the chest ring, and the waist ring on the belt below it
WAIST = Vector((0.0, -2.4, BELT_Z))
RING = 0.62            # a ring's radius to the middle of its metal
RING_TUBE = 0.16       # the metal's radius
RING_SIDES = (16, 6)

# Each strap: its points, and which ends stop at a ring (that ring's middle is then the first or last point). The left one;
# the right is its mirror. Over the shoulder between the neck and the arm, down the back beside the spine to
# under the belt, stopping short of its lower edge.
SHOULDER_PATH = [CHEST, Vector((0.7, -1.85, 17.7)), Vector((1.15, -1.3, 18.6)), Vector((1.35, -0.5, 19.5)),
                 Vector((1.4, 0.3, 19.8)), Vector((1.35, 1.1, 19.3)), Vector((1.2, 1.4, 18.0)),
                 Vector((1.05, 1.2, 16.5)), Vector((1.0, 1.5, BELT_Z + 0.25))]
STRAPS = [
    (SHOULDER_PATH, CHEST, None),
    ([Vector((-p.x, p.y, p.z)) for p in SHOULDER_PATH], CHEST, None),
    ([CHEST, Vector((0.0, -2.2, 15.8)), WAIST], CHEST, WAIST),
]

COLOURS = {'strap': (0.90, 0.88, 0.85), 'ring': (0.82, 0.83, 0.86)}
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

# The trunk's skin alone: the triangles none of whose corners belong mostly to the head, a forearm or a hand. The upper
# arm stays, as the upper back over the shoulder blades is mostly its.
names = {g.index: g.name for g in body.vertex_groups}
deform = {b.name for b in rig.data.bones if b.use_deform}
OFF_TRUNK = ('forearm', 'hand', 'thumb', 'f_', 'palm', walk.HEAD)

def dominant(i):
    gs = [(g.weight, names[g.group]) for g in me.vertices[i].groups if names[g.group] in deform]
    return max(gs)[1] if gs else ''

off = [dominant(i).startswith(OFF_TRUNK) for i in range(len(verts))]
trunk_tris = [t for t in tris if not any(off[i] for i in t)]
tree = BVHTree.FromPolygons(verts, trunk_tris)
whole_tree = BVHTree.FromPolygons(verts, tris)

def skin_at(p):
    """The nearest point of the trunk's skin and the skin's smooth normal there."""
    loc, n, ti, dist = tree.find_nearest(p)
    a, b, c = (verts[i] for i in trunk_tris[ti])
    v0, v1, v2 = b - a, c - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-9
    wb = (d11 * d20 - d01 * d21) / den
    wc = (d00 * d21 - d01 * d20) / den
    na, nb, nc = (vertex_normals[i] for i in trunk_tris[ti])
    return loc, (na * (1 - wb - wc) + nb * wb + nc * wc).normalized()

def outside(p):
    """p, or the skin under it if it is inside."""
    loc, n = skin_at(p)
    return p if (p - loc).dot(n) > 0 else loc

def resample(path, closed=False):
    pts = path + ([path[0]] if closed else [])
    out = []
    for a, b in zip(pts, pts[1:]):
        n = max(1, round((b - a).length / STEP))
        out += [a.lerp(b, j / n) for j in range(n)]
    if not closed:
        out.append(pts[-1])
    return out

def lay(path, closed=False):
    """The path laid on the skin: stepped, each step to the skin, smoothed and kept out of the skin."""
    pts = [skin_at(p)[0] for p in resample(path, closed)]
    n = len(pts)
    for _ in range(SMOOTH):
        new = []
        for k in range(n):
            if not closed and k in (0, n - 1):
                new.append(pts[k])
                continue
            new.append(outside((pts[k - 1] + pts[k] * 2 + pts[(k + 1) % n]) / 4))
        pts = new
    return pts

def trim(pts, ring_at, from_start):
    """The strap's line cut back to where it leaves the ring's middle by RING."""
    seq = pts if from_start else pts[::-1]
    loc = skin_at(ring_at)[0]
    k = next((k for k, p in enumerate(seq) if (p - loc).length >= RING), len(seq) - 1)
    seq = seq[max(0, k - 1):]
    return seq if from_start else seq[::-1]

bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new('UVMap')
part_of = {}

def add_face(vs, part):
    f = bm.faces.new(vs)
    part_of[f] = part
    return f

def frames_of(pts, closed):
    n = len(pts)
    outs = [skin_at(p)[1] for p in pts]
    outs = [(outs[max(0, k - 1)] + outs[k] * 2 + outs[(k + 1) % n if closed else min(n - 1, k + 1)]).normalized()
            for k in range(n)]
    frames = []
    for k in range(n):
        if closed:
            t = pts[(k + 1) % n] - pts[k - 1]
        else:
            t = pts[min(n - 1, k + 1)] - pts[max(0, k - 1)]
        t.normalize()
        nn = (outs[k] - t * outs[k].dot(t)).normalized()
        frames.append((nn, t, t.cross(nn)))
    return frames

def add_strap(pts, width, closed=False, lift=0.0):
    """A flat box along the line, each corner of its section taken across the skin and pushed out along its normal."""
    frames = frames_of(pts, closed)
    ring = []
    for p, (n, t, w) in zip(pts, frames):
        corners = []
        for e in (-1, 1):
            q = p + w * e * width / 2
            loc, sn = skin_at(q)
            h = max(0.0, (q - loc).dot(sn))   # how far a bridging strap stands off the skin here
            sn = (sn + n).normalized()
            corners.append((loc + sn * (h + INNER + lift), loc + sn * (h + INNER + lift + THICK)))
        ring.append([bm.verts.new(corners[0][0]), bm.verts.new(corners[0][1]),
                     bm.verts.new(corners[1][1]), bm.verts.new(corners[1][0])])
    n = len(ring)
    for k in range(n if closed else n - 1):
        a, b = ring[k], ring[(k + 1) % n]
        for s in range(4):
            add_face([a[s], a[(s + 1) % 4], b[(s + 1) % 4], b[s]], 'strap')
    if not closed:
        add_face(list(reversed(ring[0])), 'strap')
        add_face(ring[-1], 'strap')

def add_ring(at):
    """A torus lying on the skin at the point nearest at, its metal's middle level with the straps' middle."""
    loc, n = skin_at(at)
    c = loc + n * (INNER + THICK / 2)
    u = Vector((1, 0, 0))
    u = (u - n * u.dot(n)).normalized()
    v = n.cross(u)
    big, small = RING_SIDES
    rows = []
    for i in range(big):
        a = math.pi * 2 * i / big
        way = u * math.cos(a) + v * math.sin(a)
        rows.append([bm.verts.new(c + way * (RING + RING_TUBE * math.cos(b)) + n * RING_TUBE * math.sin(b))
                     for b in (math.pi * 2 * j / small for j in range(small))])
    for i in range(big):
        for j in range(small):
            r0, r1 = rows[i], rows[(i + 1) % big]
            add_face([r0[j], r1[j], r1[(j + 1) % small], r0[(j + 1) % small]], 'ring')

for path, start_ring, end_ring in STRAPS:
    pts = lay(path)
    if start_ring is not None:
        pts = trim(pts, start_ring, True)
    if end_ring is not None:
        pts = trim(pts, end_ring, False)
    add_strap(pts, WIDTH)

# The belt: out from the trunk's middle at BELT_Z in the level plane, round the whole waist.
front_y = whole_tree.ray_cast(Vector((0, -40, BELT_Z)), Vector((0, 1, 0)), 80.0)[0].y
back_y = whole_tree.ray_cast(Vector((0, 40, BELT_Z)), Vector((0, -1, 0)), 80.0)[0].y
middle = Vector((0, (front_y + back_y) / 2, BELT_Z))
belt = []
for k in range(BELT_SAMPLES):
    a = math.pi * 2 * k / BELT_SAMPLES
    hit = tree.ray_cast(middle, Vector((math.sin(a), -math.cos(a), 0)), 40.0)
    if hit[0] is None:
        sys.exit('make_body_strap.py: no skin out from the waist at %.0f degrees' % math.degrees(a))
    belt.append(hit[0])
add_strap(lay(belt, closed=True), BELT_WIDTH, closed=True, lift=BELT_LIFT)

add_ring(CHEST)
add_ring(WAIST)

bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
for f in bm.faces:
    u = (SQUARES.index(part_of.get(f, 'strap')) + 0.5) / len(SQUARES)
    for l in f.loops:
        l[uv_layer].uv = (u, 0.5)

mesh = bpy.data.meshes.new('BodyStrap')
bm.to_mesh(mesh)
bm.free()
for poly in mesh.polygons:
    poly.use_smooth = False

img = bpy.data.images.new('BodyStrap', CELL * len(SQUARES), CELL, alpha=False)
px = []
for y in range(CELL):
    for x in range(CELL * len(SQUARES)):
        r, g, b = COLOURS[SQUARES[x // CELL]]
        px += [r, g, b, 1.0]
img.pixels = px
img.filepath_raw = os.path.splitext(out_glb)[0] + '.png'
img.file_format = 'PNG'
img.pack()
mat = bpy.data.materials.new('BodyStrap')
mat.use_nodes = True
bsdf = mat.node_tree.nodes['Principled BSDF']
tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
tex.image = img
tex.interpolation = 'Closest'
mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
mesh.materials.append(mat)

o = bpy.data.objects.new('BodyStrap', mesh)
body.users_collection[0].objects.link(o)
o.parent = rig
o.matrix_parent_inverse = rig.matrix_world.inverted()   # left where it is: the vertices are in world space
o.modifiers.new('Armature', 'ARMATURE').object = rig

# Weights: the body's at the point on the skin nearest each vertex, blended across its triangle.
def vertex_weights(i):
    return {names[g.group]: g.weight for g in me.vertices[i].groups if names[g.group] in deform and g.weight > 0}
for v in mesh.vertices:
    loc, n, ti, dist = tree.find_nearest(v.co)
    a, b, c3 = (verts[i] for i in trunk_tris[ti])
    v0, v1, v2 = b - a, c3 - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-9
    wb = (d11 * d20 - d01 * d21) / den
    wc = (d00 * d21 - d01 * d20) / den
    total = {}
    for s, i in zip((1 - wb - wc, wb, wc), trunk_tris[ti]):
        for bone, w in vertex_weights(i).items():
            total[bone] = total.get(bone, 0.0) + max(0.0, s) * w
    whole = sum(total.values()) or 1.0
    for bone, w in total.items():
        if w / whole > 1e-4:
            (o.vertex_groups.get(bone) or o.vertex_groups.new(name=bone)).add([v.index], w / whole, 'REPLACE')

print('BODYSTRAP: %d vertices, %d faces, %d triangles' % (len(mesh.vertices), len(mesh.polygons),
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
print('BODYSTRAP -> %s' % out_glb)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
