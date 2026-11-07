"""Makes the grenadier armour (Royalty's Apparel_ArmorMarineGrenadier, marine armour with a frag launcher built into the
shoulder) from the marine armour: Models/garments/powerArmor.glb as it is, its texture too, with a launcher tube standing
up on the left shoulder, as the game's sprite has it (the right shoulder is the hip gear's, walk.GEAR).

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_grenadier.py -- <out.glb> [out.blend] [--phoenix]

Written to Models/garments/ArmorMarineGrenadier.glb it is the garment ArmorMarineGrenadier, drawn for
Apparel_ArmorMarineGrenadier (export_frames.py's default_defs). Re-run it after the marine armour changes.

With --phoenix it makes the phoenix armour (Royalty's Apparel_ArmorCataphractPhoenix, cataphract armour with a flamebolt
launcher on the shoulder) the same way from Models/garments/ArmorCataphract.glb, the tube as big and where the
grenadier's is, its outline from PHOENIX. Written to
Models/garments/ArmorCataphractPhoenix.glb it is the garment ArmorCataphractPhoenix. Re-run it after the cataphract
armour changes.

The tube is round (SEGMENTS round, its outline PROFILE from its foot to its mouth). The chibi's shoulders are under its
big head, and just out from them is already the upper arm, so it is mounted on the upper back over the left shoulder
blade: its foot where a line looking forward from behind at BASE (x out to the left, z up) meets the armour's back,
SINK into it, its line leant back (FORWARD below 0) and well OUT from upright, LENGTH long, so it stands clear of the
head (the run prints how near it comes at rest) and shows above the left shoulder from the front, as on the sprite. Its
mouth is a dark bore. Its texture places are on the lightest and darkest even patches of the armour's Base Color image.

The tube is weighted as one piece, every vertex with the body's weights round its foot less the arm's (NOT_ARMS; written
as the file's skin), so it rides the chest without bending or swinging as the arm aims; export_frames.py's
"part_weights" keeps those and gives the armour's own vertices, which have none, the body's as on any garment. Sizes are
in Blender units at the rig's scale (one cell is about 17.6). The .blend is never saved; the optional out.blend is a
copy with the armour on the rig, to look at."""
import bpy, bmesh, sys, math, os
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
phoenix = '--phoenix' in argv
argv = [a for a in argv if a != '--phoenix']
if not argv:
    sys.exit('make_grenadier.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'Models', 'garments', 'powerArmor.glb')
NAME = 'ArmorMarineGrenadier'

BASE = (2.0, 18.0)     # x, z of the tube's foot on the left of the upper back
SINK = 0.35            # how far its foot goes into the armour
FORWARD = -15.0        # degrees its line leans forward from upright (back, below 0)
OUT = 45.0             # and out to the left, away from the head
LENGTH = 5.5           # from its foot to its mouth
SEGMENTS = 12
FOOT = AXIS = None   # the foot's place and the line's way, if given, in place of BASE, SINK, FORWARD and OUT
SCALE = 1.0          # the whole tube, length and radii
WEIGHTS = None       # its weights outright ({bone: weight}), in place of the body's round its foot
# The outline from the foot up: (share of the length, radius, dark). Then the bore: down inside the mouth to a floor.
PROFILE = [
    (0.00, 0.00, True),
    (0.00, 1.25, True),     # the collar it is mounted on
    (0.14, 1.25, True),
    (0.15, 0.95, False),    # the tube
    (0.62, 0.95, False),
    (0.63, 1.02, True),     # a dark band round it
    (0.70, 1.02, True),
    (0.71, 0.95, False),
    (1.00, 0.95, False),    # the mouth's rim
    (1.00, 0.68, True),     # the bore
    (0.86, 0.68, True),
    (0.86, 0.00, True),
]
NOT_ARMS = ('upper_arm', 'forearm', 'hand')   # bones left out of its weights: the back over the shoulder blade is
#   partly the arm's, which would swing the launcher about as the arm aims
# The phoenix armour's flamebolt launcher: where and as big as the grenadier's tube is in its .glb, which was moved by
# hand after this script made it (measured: its foot, its line pointing forward and up over the left shoulder, about
# 0.86 of the script's size), but plain, with a lip round its mouth in place of the band. Each name here replaces the
# grenadier's.
PHOENIX = {
    'SOURCE': os.path.join(ROOT, 'Models', 'garments', 'ArmorCataphract.glb'),
    'NAME': 'ArmorCataphractPhoenix',
    'FOOT': (4.02, 3.33, 19.91),
    'AXIS': (0.017, -0.907, 0.422),
    'SCALE': 0.86,
    'WEIGHTS': {'shoulder.L': 0.91, 'spine.003': 0.09},   # as the grenadier's tube in its .glb
    'PROFILE': [
        (0.00, 0.00, True),
        (0.00, 1.25, True),     # the collar it is mounted on
        (0.14, 1.25, True),
        (0.15, 0.95, False),    # the tube
        (0.94, 0.95, False),
        (0.95, 1.02, False),    # a lip round the mouth
        (1.00, 1.02, False),
        (1.00, 0.68, True),     # the bore
        (0.86, 0.68, True),
        (0.86, 0.00, True),
    ],
}
if phoenix:
    globals().update(PHOENIX)
WEIGHT_NEAR = 6        # body vertices round the foot whose weights it takes
PATCH = 10             # pixels: the squares of the image the tube takes its colours from
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
    print(('PHOENIX' if phoenix else 'GRENADIER') + ' colours: plates %.2f, joints %.2f bright' % (light[0], dark[0]))
    return Vector(light[1]), Vector(dark[1])


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


img = base_colour_image(armour.data.materials)
light_uv, dark_uv = even_patches(img) if img else (Vector((0.5, 0.5)), Vector((0.5, 0.5)))

# The foot: where a line looking forward from behind at BASE (x, z) meets the armour's back, SINK into it.
if FOOT is not None:
    # Placed outright (the phoenix's, copied from the grenadier's tube as it was moved by hand in its .glb).
    foot = Vector(FOOT)
    axis = Vector(AXIS).normalized()
else:
    hit = tree.ray_cast(Vector((BASE[0], 40.0, BASE[1])), Vector((0, -1, 0)), 80)[0]
    if hit is None:
        sys.exit('make_grenadier.py: no armour behind x %.2f z %.2f' % BASE)
    axis = (Matrix.Rotation(math.radians(OUT), 3, 'Y') @ Matrix.Rotation(math.radians(FORWARD), 3, 'X')) @ Vector((0, 0, 1))
    # Rotating about x by FORWARD tips +z toward -y (forward); about y by OUT tips it toward +x (out to the left).
    axis = axis.normalized()
    foot = hit - axis * SINK
u = axis.cross(Vector((0, 1, 0))).normalized()
v = axis.cross(u).normalized()

bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new('UVMap')
rings = []
for share, r, _ in PROFILE:
    c = foot + axis * LENGTH * SCALE * share
    rings.append([bm.verts.new(c + (u * math.cos(2 * math.pi * i / SEGMENTS) + v * math.sin(2 * math.pi * i / SEGMENTS)) * r * SCALE)
                  for i in range(SEGMENTS)])
for k in range(len(rings) - 1):
    a, b = rings[k], rings[k + 1]
    dark = PROFILE[k][2] and PROFILE[k + 1][2]
    for i in range(SEGMENTS):
        j = (i + 1) % SEGMENTS
        f = bm.faces.new([a[i], b[i], b[j], a[j]])
        f.smooth = True
        for l in f.loops:
            l[uv_layer].uv = dark_uv if dark else light_uv
# The foot and the bore's floor are single points: rings of one place, welded; then turned out from its inside.
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=1e-6)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
# The mouth's rim is a hard edge; the rest is round.
for f in bm.faces:
    f.smooth = True

# Clear of the head? The nearest the tube comes to the head mesh at rest.
head = bpy.data.objects.get('Head') or body
deps = bpy.context.evaluated_depsgraph_get()
hb = bmesh.new()
hb.from_object(head, deps)
hb.transform(head.matrix_world)
head_tree = BVHTree.FromBMesh(hb)
nearest = min(head_tree.find_nearest(x.co)[3] for x in bm.verts)
hb.free()

# The body's weights at its foot (its deform bones only), the same on every vertex, so it moves as one piece.
bones = {b.name for b in rig.data.bones if b.use_deform}
kd = KDTree(len(body.data.vertices))
for i, x in enumerate(body.data.vertices):
    kd.insert(body.matrix_world @ x.co, i)
kd.balance()
names = {g.index: g.name for g in body.vertex_groups}
weights = {}
for _, i, dist in kd.find_n(foot + axis * SINK, WEIGHT_NEAR):
    for g in body.data.vertices[i].groups:
        if names[g.group] in bones and g.weight > 0 and not names[g.group].startswith(NOT_ARMS):
            weights[names[g.group]] = weights.get(names[g.group], 0.0) + g.weight / (1 + dist)
total = sum(weights.values())
weights = {b: w / total for b, w in weights.items() if w / total > 0.02}
total = sum(weights.values())
weights = {b: w / total for b, w in weights.items()}
if WEIGHTS is not None:
    weights = dict(WEIGHTS)

bm.transform(mw.inverted())
whole = bmesh.new()
whole.from_mesh(armour.data)
uv_armour = whole.loops.layers.uv.active or whole.loops.layers.uv.new('UVMap')
deform = whole.verts.layers.deform.verify()
groups = {b: armour.vertex_groups.new(name=b).index for b in weights}
mapping = {}
for x in bm.verts:
    nv = whole.verts.new(x.co)
    for b, w in weights.items():
        nv[deform][groups[b]] = w
    mapping[x] = nv
added = 0
for f in bm.faces:
    nf = whole.faces.new([mapping[x] for x in f.verts])
    nf.smooth = True
    nf.material_index = 0
    for l, ol in zip(nf.loops, f.loops):
        l[uv_armour].uv = ol[uv_layer].uv
    added += 1
whole.to_mesh(armour.data)
whole.free()
bm.free()
welded.free()

print(('PHOENIX' if phoenix else 'GRENADIER') + ': %d vertices, %d faces (%d of them the launcher), foot at %.2f %.2f %.2f, %.2f from the head at rest, '
      'weights %s, plates from the image at %.3f %.3f, joints at %.3f %.3f'
      % (len(armour.data.vertices), len(armour.data.polygons), added, foot.x, foot.y, foot.z, nearest,
         ', '.join('%s %.2f' % bw for bw in sorted(weights.items(), key=lambda bw: -bw[1])),
         light_uv.x, light_uv.y, dark_uv.x, dark_uv.y))

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
print(('PHOENIX' if phoenix else 'GRENADIER') + ' -> %s' % out_glb)

# Read back: the launcher must still have its weights.
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=out_glb, disable_bone_shape=True)
back = next(o for o in bpy.data.objects if o not in before and o.type == 'MESH')
# (The exporter puts a vertex with no weights on a "neutral_bone", which export_frames.py does not take for a bone.)
weighted = sum(1 for x in back.data.vertices
               if any(g.weight > 0 and back.vertex_groups[g.group].name != 'neutral_bone' for g in x.groups))
print(('PHOENIX' if phoenix else 'GRENADIER') + ' read back: %d vertices, %d weighted (%d expected, more where texture seams split them)'
      % (len(back.data.vertices), weighted, len(mapping)))
for o in [o for o in bpy.data.objects if o not in before]:
    bpy.data.objects.remove(o)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
