"""Makes the mechlord suit (Biotech's Apparel_MechlordSuit, a power-assisted suit packed with mechanitor gear) from the
recon armour: Models/garments/ArmorRecon.glb as it is, its texture too, with the extras the game's sprite has:

  - a tall fin standing up from each shoulder (FIN), leant out and back past the big head, as the sprite's side view
    has them sweeping back;
  - a short blade flaring down, out and back from each side of the waist (BLADE);
  - a round core on the belly, a light dome in a dark ring (CORE), and a flatter disc in the middle of the back
    (BACK_DISC).

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_mechlord.py -- <out.glb> [out.blend]

Written to Models/garments/MechlordSuit.glb it is the garment MechlordSuit, drawn for Apparel_MechlordSuit
(export_frames.py's default_defs; the marine armour no longer stands in for it). Re-run it after the recon armour
changes.

The fins and blades are flat plates: an outline (in shares of their length along their line and units across it)
extruded THICK, the faces light and the edges dark, as the sprite's white plates with grey sides. Each fin's foot is
where a line looking down at (x, y) meets the armour's shoulder, SINK into it; each blade's where a line looking in from
the side at (y, z) meets the armour's waist. The two discs are turned (a profile out from the surface), their middle
where a line looking in from the front or back at (x, z) meets the armour. Their texture places are on the lightest and
darkest even patches of the armour's Base Color image.

Each part is weighted as one piece, every vertex with the body's weights round its foot less those of the bones in its
NOT list (the arms, the neck and head for the fins; the legs for the blades), written as the file's skin, so it rides
the trunk without bending; export_frames.py's "part_weights" keeps those and gives the armour's own vertices, which have
none, the body's as on any garment. Sizes are in Blender units at the rig's scale (one cell is about 17.6). The .blend is
never saved; the optional out.blend is a copy with the suit on the rig, to look at."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_mechlord.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'Models', 'garments', 'ArmorRecon.glb')
NAME = 'MechlordSuit'

THICK = 0.55          # the plates' thickness
SINK = 0.6            # how far a plate's foot goes into the armour
ARMS = ('upper_arm', 'forearm', 'hand')
HEAD = ('spine.004', 'spine.005', 'spine.006')
LEGS = ('thigh', 'shin', 'foot', 'toe')
# The fins, given for the left (x out to the left, y back, z up); the right is its mirror. "at" is the (x, y) looked
# down at for the foot; "way" the line (out, back, up), normalised; "outline" (share of the length, across), across
# being toward the front edge, from the foot round.
FIN = {
    'at': (3.6, 1.6),
    'way': (0.38, 0.55, 0.85),
    'length': 9.5,
    'face': 50.0,         # degrees its flat face is turned from facing out toward facing the front, so it shows wide
    #   from the front as well as the side, as on the sprite
    'outline': [(0.0, -0.9), (0.0, 0.9), (0.55, 1.25), (0.92, 1.1), (1.0, 0.5), (0.97, -0.65), (0.6, -0.95)],
    'not': ARMS + HEAD,
}
BLADE = {
    'at': (0.9, 11.6),    # (y, z) looked in at from the side for the foot
    'way': (0.55, 0.45, -0.70),
    'length': 4.6,
    'outline': [(0.0, -0.7), (0.0, 0.7), (0.5, 0.75), (1.0, 0.15), (0.85, -0.55)],
    'not': ARMS + LEGS,
}
# The discs: (x, z) looked at, the side (-1 the front, 1 the back), and the profile out from the surface: (radius,
# height off it, dark), from the rim in.
CORE = {
    'at': (0.0, 12.6), 'side': -1,
    'profile': [(1.55, -0.4, True), (1.55, 0.35, True), (1.15, 0.45, True), (1.05, 0.45, False), (0.7, 0.65, False),
                (0.0, 0.75, False)],
    'not': ARMS + LEGS,
}
BACK_DISC = {
    'at': (0.0, 15.6), 'side': 1,
    'profile': [(1.5, -0.4, True), (1.5, 0.25, True), (1.1, 0.3, True), (1.0, 0.3, False), (0.0, 0.38, False)],
    'not': ARMS + LEGS,
}
SEGMENTS = 14          # points round a disc
WEIGHT_NEAR = 6        # body vertices round a foot whose weights a part takes
PATCH = 10             # pixels: the squares of the image the parts take their colours from
EVEN = 0.06            # their brightness spread at most


def even_patches(img):
    """The middles of the lightest and the darkest PATCH-pixel squares of the image that are even (their brightness
    spread within EVEN), as texture places. Black (an unpainted corner of the atlas) does not count as dark."""
    w, h = img.size
    px = list(img.pixels[:])
    found = []
    for y0 in range(0, h - PATCH, PATCH):
        for x0 in range(PATCH, w - 2 * PATCH, PATCH):   # not the edge columns, often padding
            vals = []
            for y in range(y0, y0 + PATCH, 3):
                row = (y * w) * 4
                for x in range(x0, x0 + PATCH, 3):
                    k = row + x * 4
                    vals.append((px[k] + px[k + 1] + px[k + 2]) / 3 if px[k + 3] > 0.5 else -1)
            if min(vals) < 0 or max(vals) - min(vals) > EVEN:
                continue
            found.append((sum(vals) / len(vals), ((x0 + PATCH / 2) / w, (y0 + PATCH / 2) / h)))
    if not found:
        return Vector((0.5, 0.5)), Vector((0.5, 0.5))
    light = max(found)
    dark = min((f for f in found if f[0] > 0.12), default=min(found))
    print('MECHLORD colours: plates %.2f, joints %.2f bright' % (light[0], dark[0]))
    return Vector(light[1]), Vector(dark[1])


def base_colour_image(materials):
    """The image feeding a material's Base Color (through any nodes between), or None: not its normal or other maps."""
    def upstream(socket, seen):
        for link in socket.links:
            n = link.from_node
            if n in seen:
                continue
            seen.add(n)
            if n.type == 'TEX_IMAGE' and n.image:
                return n.image
            for s in n.inputs:
                found = upstream(s, seen)
                if found:
                    return found
        return None
    for m in materials:
        if m and m.node_tree:
            for n in m.node_tree.nodes:
                if n.type == 'BSDF_PRINCIPLED':
                    found = upstream(n.inputs['Base Color'], set())
                    if found:
                        return found
    return None


before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=SOURCE, disable_bone_shape=True)
new = [o for o in bpy.data.objects if o not in before]
armour = next(o for o in new if o.type == 'MESH')
for o in new:
    if o is not armour:
        bpy.data.objects.remove(o)
old = bpy.data.objects.get(NAME)
if old is not None:
    bpy.data.objects.remove(old)
armour.name = NAME
armour.data.name = NAME
if armour.data.shape_keys:
    armour.shape_key_clear()
armour.vertex_groups.clear()

body = bpy.data.objects['Body']
rig = body.find_armature()
mw = armour.matrix_world.copy()

welded = bmesh.new()
welded.from_mesh(armour.data)
welded.transform(mw)
bmesh.ops.remove_doubles(welded, verts=welded.verts, dist=1e-4)
tree = BVHTree.FromBMesh(welded)

img = base_colour_image(armour.data.materials)
light_uv, dark_uv = even_patches(img) if img else (Vector((0.5, 0.5)), Vector((0.5, 0.5)))

deform_bones = {b.name for b in rig.data.bones if b.use_deform}
kd = KDTree(len(body.data.vertices))
for i, x in enumerate(body.data.vertices):
    kd.insert(body.matrix_world @ x.co, i)
kd.balance()
group_names = {g.index: g.name for g in body.vertex_groups}


def weights_near(p, leave_out):
    """The body's weights round p (its deform bones only, less those starting with any of leave_out), summed nearer
    more, the small ones dropped, adding up to 1."""
    found = {}
    for _, i, dist in kd.find_n(p, WEIGHT_NEAR):
        for g in body.data.vertices[i].groups:
            n = group_names[g.group]
            if n in deform_bones and g.weight > 0 and not n.startswith(leave_out):
                found[n] = found.get(n, 0.0) + g.weight / (1 + dist)
    total = sum(found.values())
    found = {b: w / total for b, w in found.items() if w / total > 0.02}
    total = sum(found.values())
    return {b: w / total for b, w in found.items()}


bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new('UVMap')
part_of = {}      # each new vertex's part
part_weights = []
parts = []        # (name, foot, faces)


def paint(f, dark):
    for l in f.loops:
        l[uv_layer].uv = dark_uv if dark else light_uv


def plate(name, foot, axis, across, length, outline, leave_out):
    """A flat plate: the outline (share of the length along axis, units along across) at foot, extruded THICK about
    its middle; the faces light, the edges dark."""
    n = axis.cross(across).normalized()
    k = len(part_weights)
    part_weights.append(weights_near(foot, leave_out))
    flat = [foot + axis * length * s + across * a for s, a in outline]
    top = [bm.verts.new(p + n * THICK / 2) for p in flat]
    bottom = [bm.verts.new(p - n * THICK / 2) for p in flat]
    for x in top + bottom:
        part_of[x] = k
    faces = [bm.faces.new(top), bm.faces.new(list(reversed(bottom)))]
    paint(faces[0], False)
    paint(faces[1], False)
    m = len(flat)
    for i in range(m):
        j = (i + 1) % m
        f = bm.faces.new([top[i], bottom[i], bottom[j], top[j]])
        paint(f, True)
        faces.append(f)
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    parts.append((name, foot, faces))


def disc(name, centre, out, profile, leave_out):
    """A turned disc out of the surface along out: the profile's rings (radius, height, dark) from the rim in."""
    u = out.cross(Vector((0, 0, 1))).normalized()
    v = out.cross(u).normalized()
    k = len(part_weights)
    part_weights.append(weights_near(centre, leave_out))
    rings = []
    for r, h, _ in profile:
        c = centre + out * h
        rings.append([bm.verts.new(c + (u * math.cos(2 * math.pi * i / SEGMENTS) + v * math.sin(2 * math.pi * i / SEGMENTS)) * r)
                      for i in range(SEGMENTS)])
    for ring in rings:
        for x in ring:
            part_of[x] = k
    faces = []
    for a in range(len(rings) - 1):
        ra, rb = rings[a], rings[a + 1]
        dark = profile[a][2] and profile[a + 1][2]
        for i in range(SEGMENTS):
            j = (i + 1) % SEGMENTS
            f = bm.faces.new([ra[i], rb[i], rb[j], ra[j]])
            paint(f, dark)
            faces.append(f)
    parts.append((name, centre, faces))


def mirrored(way, sign):
    return Vector((way[0] * sign, way[1], way[2])).normalized()


for side, sign in (('L', 1), ('R', -1)):
    # The fin: its foot on top of the shoulder, looking down.
    x, y = FIN['at']
    hit = tree.ray_cast(Vector((x * sign, y, 40.0)), Vector((0, 0, -1)), 80)[0]
    if hit is None:
        sys.exit('make_mechlord.py: no shoulder under x %.2f y %.2f' % (x * sign, y))
    axis = mirrored(FIN['way'], sign)
    # The flat face's way: out, turned FIN['face'] toward the front, square to the line; across is then along the face,
    # toward the front edge.
    a = math.radians(FIN['face'])
    face = Vector((sign * math.cos(a), -math.sin(a), 0))
    face = (face - axis * face.dot(axis)).normalized()
    across = face.cross(axis).normalized()
    if across.y > 0:
        across = -across
    plate('fin.' + side, hit - axis * SINK, axis, across, FIN['length'], FIN['outline'], FIN['not'])

    # The blade: its foot on the side of the waist, looking in.
    y, z = BLADE['at']
    hit = tree.ray_cast(Vector((20.0 * sign, y, z)), Vector((-sign, 0, 0)), 40)[0]
    if hit is None:
        sys.exit('make_mechlord.py: no waist at y %.2f z %.2f' % (y, z))
    axis = mirrored(BLADE['way'], sign)
    across = axis.cross(Vector((0, 1, 0))).normalized()   # facing front and back, so it reads from the front
    if across.z < 0:
        across = -across
    plate('blade.' + side, hit - axis * SINK, axis, across, BLADE['length'], BLADE['outline'], BLADE['not'])

for name, d in (('core', CORE), ('back_disc', BACK_DISC)):
    x, z = d['at']
    way = Vector((0, d['side'], 0))
    hit = tree.ray_cast(Vector((x, 40.0 * d['side'], z)), -way, 80)[0]
    if hit is None:
        sys.exit('make_mechlord.py: nothing at x %.2f z %.2f for the %s' % (x, z, name))
    disc(name, hit, way, d['profile'], d['not'])

# How near each part comes to the head and the body at rest.
deps = bpy.context.evaluated_depsgraph_get()
def tree_of(obj):
    b = bmesh.new()
    b.from_object(obj, deps)
    b.transform(obj.matrix_world)
    t = BVHTree.FromBMesh(b)
    b.free()
    return t
head = bpy.data.objects.get('Head') or body
head_tree = tree_of(head)
for name, foot, faces in parts:
    verts = {x for f in faces for x in f.verts}
    nearest = min(head_tree.find_nearest(x.co)[3] for x in verts)
    k = part_of[next(iter(verts))]
    print('MECHLORD %s: foot at %.2f %.2f %.2f, %.2f from the head, weights %s'
          % (name, foot.x, foot.y, foot.z, nearest,
             ', '.join('%s %.2f' % bw for bw in sorted(part_weights[k].items(), key=lambda bw: -bw[1]))))

bm.transform(mw.inverted())
whole = bmesh.new()
whole.from_mesh(armour.data)
uv_armour = whole.loops.layers.uv.active or whole.loops.layers.uv.new('UVMap')
deform = whole.verts.layers.deform.verify()
groups = {}
mapping = {}
for x in bm.verts:
    nv = whole.verts.new(x.co)
    for b, w in part_weights[part_of[x]].items():
        if b not in groups:
            groups[b] = armour.vertex_groups.new(name=b).index
        nv[deform][groups[b]] = w
    mapping[x] = nv
added = 0
for f in bm.faces:
    nf = whole.faces.new([mapping[x] for x in f.verts])
    nf.smooth = False
    nf.material_index = 0
    for l, ol in zip(nf.loops, f.loops):
        l[uv_armour].uv = ol[uv_layer].uv
    added += 1
whole.to_mesh(armour.data)
whole.free()
bm.free()
welded.free()

print('MECHLORD: %d vertices, %d faces (%d of them added), plates from the image at %.3f %.3f, joints at %.3f %.3f'
      % (len(armour.data.vertices), len(armour.data.polygons), added, light_uv.x, light_uv.y, dark_uv.x, dark_uv.y))

world = armour.matrix_world.copy()
armour.parent = rig
armour.matrix_world = world
armour.modifiers.new('Armature', 'ARMATURE').object = rig

bpy.ops.object.select_all(action='DESELECT')
rig.hide_set(False)
rig.hide_viewport = False
armour.select_set(True)
rig.select_set(True)
bpy.context.view_layer.objects.active = armour
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False, export_extras=True)
print('MECHLORD -> %s' % out_glb)

# Read back: the parts must still have their weights.
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=out_glb, disable_bone_shape=True)
back = next(o for o in bpy.data.objects if o not in before and o.type == 'MESH')
# (The exporter puts a vertex with no weights on a "neutral_bone", which export_frames.py does not take for a bone.)
weighted = sum(1 for x in back.data.vertices
               if any(g.weight > 0 and back.vertex_groups[g.group].name != 'neutral_bone' for g in x.groups))
print('MECHLORD read back: %d vertices, %d weighted (%d expected, more where texture seams split them)'
      % (len(back.data.vertices), weighted, len(mapping)))
for o in [o for o in bpy.data.objects if o not in before]:
    bpy.data.objects.remove(o)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
