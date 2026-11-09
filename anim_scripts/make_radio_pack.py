"""Makes Combat Extended's radio pack (CE_RadioPack) from the backpack garment as it is, after the radio pack's sprite.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_radio_pack.py -- <out.glb> [backpack.glb] [out.blend]

The backpack (Models/garments/CE_Backpack.glb unless another is given) is read as it is, welded, and its image dropped.
To it are added: two dark compression bands round the bag (BANDS), a radio set on its outer face with two knobs on top
(RADIO, KNOB), a whip antenna from a mount on the bag's top at the left (the figure's left, +x, where the sprite has it
from behind), leant out past the big head (ANTENNA), and a handset hung on the bag's left side (HANDSET) on a coiled cord
down to the bag's bottom (CORD). Everything added is kept to the outer half of the bag's depth, as the game measures a
pack's face against the back over its inner half only (SkinnedFigure.PackRigOf), so the antenna above it and the handset
beside it don't move where it rests.

Like the backpack it has no weights: a pack (walk.HOLSTER_PACKS) is drawn whole on the pack slot. It has UVs on a small
image of flat colours (COLOURS), as make_bandolier.py does: the radio pack isn't made from stuff, so the game draws it as
the image has it, olive drab with black fittings, where an untextured one would come out white.

Directions as the figure stands: it faces -y, x is its left, z is up. Lengths are in the backpack's file's units (the
rig's), most of them as shares of the bag's height (H). The .blend is never saved; out.blend is a copy with it in, to look
at."""
import bpy, bmesh, sys, os, math
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:]
out_glb = os.path.abspath(argv[0])
here = os.path.dirname(os.path.abspath(__file__))
in_glb = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.join(here, '..', 'Models', 'garments', 'CE_Backpack.glb')
out_blend = os.path.abspath(argv[2]) if len(argv) > 2 else None

# Colours, one square of the image each. Not tinted in the game (not stuff, not dyed), so these are what is drawn.
COLOURS = {'bag': (0.40, 0.41, 0.24), 'band': (0.13, 0.13, 0.11), 'radio': (0.27, 0.29, 0.21),
           'metal': (0.08, 0.08, 0.08), 'handset': (0.10, 0.10, 0.10)}
CELL = 16
SQUARES = list(COLOURS)

BANDS = (0.28, 0.70)   # each band's middle, a share of the bag's height from its bottom
BAND_HIGH = 0.07       # a band's height, of H
BAND_OFF = 0.025       # its outside off the bag, of H
BAND_SIDES = 16        # points round
RADIO = (0.55, 0.30, 0.13)   # the set: its width (of the bag's), height and depth (of H)
RADIO_AT = 0.62        # its middle's height, a share of the bag's height
KNOB = (0.045, 0.06, 6)      # a knob's radius and height (of H), and its sides
KNOB_X = (-0.25, 0.1)        # where the two stand along the set's top, shares of its width from its middle
ANTENNA_X = 0.80       # the mount's place across the bag's top, a share of its half width toward +x
ANTENNA_Y = 0.80       # and front to back, a share of the bag's depth from its inner face (kept in the outer half)
MOUNT = (0.09, 0.07)   # the mount's radius and height, of H
ANTENNA = (1.8, 0.022, 0.011, 5)   # the whip's length, its radius at the foot and the tip (of H), and its sides
ANTENNA_OUT = 32.0     # degrees it leans out (+x) from upright, so it clears the head
ANTENNA_BACK = 4.0     # degrees it leans back (+y): a little, as how far back it reaches is the pack's depth to the game
HANDSET = (0.11, 0.42, 0.09)  # its width (front to back), length and thickness (out from the bag), of H
HANDSET_AT = (0.55, 0.85)     # its middle's height (of the bag's) and place front to back (of the bag's depth)
HANDSET_GAP = 0.015    # off the bag's side, of H
CORD = (0.035, 0.010, 5, 6, 4)   # the coil's radius and the wire's (of H), the coil's turns, its points a turn, the wire's sides

# The backpack, as the file has it, and nothing else.
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=in_glb, disable_bone_shape=True)
new = [o for o in bpy.data.objects if o not in before]
bag_obj = next(o for o in new if o.type == 'MESH')
bm = bmesh.new()
bm.from_mesh(bag_obj.data)
bm.transform(bag_obj.matrix_world)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
for layer in list(bm.loops.layers.uv.values()):
    bm.loops.layers.uv.remove(layer)
for o in new:
    bpy.data.objects.remove(o, do_unlink=True)
part_of = {f: 'bag' for f in bm.faces}

bag_pts = [v.co.copy() for v in bm.verts]
bm.verts.index_update()
bag_tree = BVHTree.FromPolygons(bag_pts, [[v.index for v in f.verts] for f in bm.faces])
lo = Vector([min(p[i] for p in bag_pts) for i in range(3)])
hi = Vector([max(p[i] for p in bag_pts) for i in range(3)])
mid = (lo + hi) / 2
H = hi.z - lo.z
DEEP = hi.y - lo.y
print('BAG %d vertices, x %.2f..%.2f, y %.2f..%.2f, z %.2f..%.2f' % (len(bag_pts), lo.x, hi.x, lo.y, hi.y, lo.z, hi.z))

def surface(start, way):
    """The bag's surface along a line from outside, start going way; None if it misses."""
    hit = bag_tree.ray_cast(start, way)
    return hit[0]

def add_part(name, points, faces):
    vs = [bm.verts.new(p) for p in points]
    made = []
    for f in faces:
        try:
            made.append(bm.faces.new([vs[i] for i in f]))
        except ValueError:
            pass
    bmesh.ops.recalc_face_normals(bm, faces=made)
    for f in made:
        part_of[f] = name

def tube(path, radii, sides, ups=None, closed=True):
    """A tube along path, radius radii[i] at each point, sides round; capped at both ends."""
    points, faces = [], []
    n = len(path)
    for i, p in enumerate(path):
        along = (path[min(i + 1, n - 1)] - path[max(i - 1, 0)]).normalized()
        up = ups[i] if ups else (Vector((0, 0, 1)) if abs(along.z) < 0.9 else Vector((1, 0, 0)))
        a = along.cross(up).normalized()
        b = along.cross(a).normalized()
        for k in range(sides):
            t = 2 * math.pi * k / sides
            points.append(p + (a * math.cos(t) + b * math.sin(t)) * radii[i])
    for i in range(n - 1):
        for k in range(sides):
            k2 = (k + 1) % sides
            faces.append([i * sides + k, i * sides + k2, (i + 1) * sides + k2, (i + 1) * sides + k])
    if closed:
        faces.append(list(range(sides))[::-1])
        faces.append([(n - 1) * sides + k for k in range(sides)])
    return points, faces

def box(lo_, hi_):
    points = [Vector((hi_.x if i & 1 else lo_.x, hi_.y if i & 2 else lo_.y, hi_.z if i & 4 else lo_.z)) for i in range(8)]
    return points, [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]]

# The bands: at each height a ring cast in onto the bag from all round, stood off it, a closed strip with a thickness.
for share in BANDS:
    z = lo.z + share * H
    ring_in, ring_out = [], []
    for k in range(BAND_SIDES):
        t = 2 * math.pi * k / BAND_SIDES
        way = Vector((math.cos(t), math.sin(t), 0))
        p = surface(Vector((mid.x, mid.y, z)) + way * 4 * H, -way)
        if p is None:
            p = Vector((mid.x, mid.y, z)) + way * 0.5 * DEEP
        ring_in.append(p - way * 0.01 * H)
        ring_out.append(p + way * BAND_OFF * H)
    half = BAND_HIGH * H / 2
    points = []
    for p in ring_in + ring_out:
        points += [p - Vector((0, 0, half)), p + Vector((0, 0, half))]
    faces = []
    m = BAND_SIDES
    for k in range(m):
        k2 = (k + 1) % m
        oi, oj = 2 * (m + k), 2 * (m + k2)       # outside
        ii, ij = 2 * k, 2 * k2                     # inside
        faces += [[oi, oj, oj + 1, oi + 1], [ii + 1, ij + 1, ij, ii], [ii, ij, oj, oi], [oi + 1, oj + 1, ij + 1, ii + 1]]
    add_part('band', points, faces)

# The radio set on the outer face, its back cast onto the bag.
rw, rh, rd = RADIO[0] * (hi.x - lo.x) / 2, RADIO[1] * H / 2, RADIO[2] * H
rz = lo.z + RADIO_AT * H
face_y = max((surface(Vector((x, hi.y + 4 * H, z)), Vector((0, -1, 0))) or Vector((0, hi.y, 0))).y
             for x in (mid.x - rw, mid.x, mid.x + rw) for z in (rz - rh, rz, rz + rh))
radio_lo = Vector((mid.x - rw, face_y - 0.02 * H, rz - rh))
radio_hi = Vector((mid.x + rw, face_y + rd, rz + rh))
add_part('radio', *box(radio_lo, radio_hi))
for s in KNOB_X:
    c = Vector((mid.x + s * rw * 2, (radio_lo.y + radio_hi.y) / 2, radio_hi.z))
    add_part('metal', *tube([c - Vector((0, 0, 0.01 * H)), c + Vector((0, 0, KNOB[1] * H))], [KNOB[0] * H] * 2, KNOB[2]))

# The antenna: a short mount on the bag's top, then the whip.
ax = mid.x + ANTENNA_X * (hi.x - mid.x)
ay = lo.y + ANTENNA_Y * DEEP
top = surface(Vector((ax, ay, hi.z + 4 * H)), Vector((0, 0, -1))) or Vector((ax, ay, hi.z))
foot = top - Vector((0, 0, 0.02 * H))
mount_top = top + Vector((0, 0, MOUNT[1] * H))
add_part('metal', *tube([foot, mount_top], [MOUNT[0] * H] * 2, 6))
out, back = math.radians(ANTENNA_OUT), math.radians(ANTENNA_BACK)
whip = Vector((math.sin(out), math.sin(back), math.cos(out) * math.cos(back))).normalized()
L, r0, r1, sides = ANTENNA[0] * H, ANTENNA[1] * H, ANTENNA[2] * H, ANTENNA[3]
steps = 4
path = [mount_top - whip * 0.02 * H + whip * L * i / steps for i in range(steps + 1)]
add_part('metal', *tube(path, [r0 + (r1 - r0) * i / steps for i in range(steps + 1)], sides))
ball = path[-1]
add_part('metal', *tube([ball - whip * r1 * 2, ball + whip * r1 * 2], [r1 * 1.8] * 2, sides))
tip = path[-1]

# The handset on the left side: a bar with a bulge at each end (the earpiece up, the mouthpiece down), lying on the bag.
hz = lo.z + HANDSET_AT[0] * H
hy = lo.y + HANDSET_AT[1] * DEEP
side = surface(Vector((hi.x + 4 * H, hy, hz)), Vector((-1, 0, 0))) or Vector((hi.x, hy, hz))
hw, hl, ht = HANDSET[0] * H / 2, HANDSET[1] * H / 2, HANDSET[2] * H
hx = side.x + HANDSET_GAP * H
rows = [(-1.0, 1.25), (-0.75, 1.25), (-0.55, 0.75), (0.55, 0.75), (0.75, 1.25), (1.0, 1.25)]   # along, width
points, faces = [], []
for along, wide in rows:
    z = hz + along * hl
    for y, x in ((-1, 0), (1, 0), (1, 1), (-1, 1)):
        points.append(Vector((hx + x * ht * (1.0 if wide > 1 else 0.7), hy + y * hw * wide, z)))
for i in range(len(rows) - 1):
    for k in range(4):
        k2 = (k + 1) % 4
        faces.append([i * 4 + k, i * 4 + k2, (i + 1) * 4 + k2, (i + 1) * 4 + k])
faces += [[3, 2, 1, 0], [(len(rows) - 1) * 4 + k for k in range(4)]]
add_part('handset', points, faces)
handset_low = Vector((hx + ht * 0.5, hy, hz - hl))

# The cord: a coil from the handset's foot down the side to the bag's bottom corner at the back.
cord_end = Vector((hi.x - 0.1 * H, hy + 0.05 * DEEP, lo.z + 0.12 * H))
cord_end = (surface(cord_end + Vector((4 * H, 0, 0)), Vector((-1, 0, 0))) or cord_end) + Vector((CORD[0] * H * 1.5, 0, 0))
coil_r, wire_r, turns, per, wire_sides = CORD[0] * H, CORD[1] * H, CORD[2], CORD[3], CORD[4]
a, b = handset_low, cord_end
along = (b - a).normalized()
u = along.cross(Vector((0, 1, 0))).normalized()
v = along.cross(u).normalized()
path = []
for i in range(turns * per + 1):
    t = i / (turns * per)
    ang = 2 * math.pi * turns * t
    ease = min(1.0, t * 6, (1 - t) * 6)
    path.append(a.lerp(b, t) + (u * math.cos(ang) + v * math.sin(ang)) * coil_r * ease)
add_part('metal', *tube(path, [wire_r] * len(path), wire_sides))

# UVs: each face's corners at the middle of its part's square.
uv = bm.loops.layers.uv.new('UVMap')
for f in bm.faces:
    s = SQUARES.index(part_of.get(f, 'bag'))
    for l in f.loops:
        l[uv].uv = ((s + 0.5) / len(SQUARES), 0.5)
me = bpy.data.meshes.new('CE_RadioPack')
bm.to_mesh(me)
bm.free()
for p in me.polygons:
    p.use_smooth = False

img = bpy.data.images.new('CE_RadioPack', CELL * len(SQUARES), CELL, alpha=False)
px = []
for y in range(CELL):
    for x in range(CELL * len(SQUARES)):
        r, g, b_ = COLOURS[SQUARES[x // CELL]]
        px += [r, g, b_, 1.0]
img.pixels = px
img.filepath_raw = os.path.splitext(out_glb)[0] + '.png'
img.file_format = 'PNG'
img.pack()
mat = bpy.data.materials.new('CE_RadioPack')
mat.use_nodes = True
tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
tex.image = img
tex.interpolation = 'Closest'
mat.node_tree.links.new(tex.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
me.materials.append(mat)
pack = bpy.data.objects.new('CE_RadioPack', me)
bpy.context.scene.collection.objects.link(pack)

# How far the antenna stays off the head, if the rig is open (the body's head part, or the joined body).
head = bpy.data.objects.get('Head') or bpy.data.objects.get('Body') or bpy.data.objects.get('Pawn')
if head is not None and head.type == 'MESH':
    hw_ = head.matrix_world
    hp = [hw_ @ x.co for x in head.data.vertices]
    near = min(min(((p - (path_p)).length for path_p in [mount_top.lerp(tip, i / 20) for i in range(21)])) for p in hp if p.z > hi.z)
    print('ANTENNA clears the %s by %.3f (of H %.3f)' % (head.name, near, near / H))

along = [p.co.y for p in me.vertices]
inner, outer = min(along), max(along)
cut = inner + 0.5 * (outer - inner)
face = [p.co for p in me.vertices if p.co.y <= cut]
print('RADIO PACK %d vertices, %d triangles; depth %.2f (the bag %.2f); face box x %.2f..%.2f z %.2f..%.2f (the bag x %.2f..%.2f z %.2f..%.2f)' % (
    len(me.vertices), sum(len(p.vertices) - 2 for p in me.polygons), outer - inner, DEEP,
    min(p.x for p in face), max(p.x for p in face), min(p.z for p in face), max(p.z for p in face), lo.x, hi.x, lo.z, hi.z))

for x in bpy.context.view_layer.objects:
    x.select_set(x is pack)
bpy.context.view_layer.objects.active = pack
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_animations=False)
print('RADIO PACK -> %s' % out_glb)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
