"""Makes the Furskin gene's fur from the chibi's own body: tufts scattered over its skin, lying down it the way fur grows.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_furskin.py -- <out.glb> [out.blend]

The game already colours a furskin pawn's skin as its hair (the gene's skinIsHairColor, story.SkinColor), so the body is
the undercoat and hides nothing: this is only the tufts, each its own small three sided spike (three triangles, four
vertices) rooted on the skin. Each tuft's root is the skin's own colour and its tip a little darker (a small grey image, UV up the
spike), which the game multiplies by the hair colour, and it is lit by the skin's normals, so the roots melt into the skin. The face, the fingers and the soles are left bare. A tuft that would reach another
part of the body (the armpit, between the thighs) is left out. Output goes in Models/garments/Furskin.glb; the exporter's
GARMENT_PROPS draws it for the gene (defs Gene_Furskin) rather than for apparel. The .blend is never saved; the optional out.blend is a separate file with the fur on
the rig. Lengths are in cells of the finished figure (FIGURE_HEIGHT tall), so they hold if the rig is scaled."""
import bpy, sys, os, math, random, bisect
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

NAME = 'Furskin'
FIGURE_HEIGHT = 1.25
SEED = 7
SPACING = 0.033     # cells: tufts on the trunk are no nearer each other than this, others by their region's share of it
WIDTH = 0.6         # a tuft's width at its root, as a share of its length
LIFT = 20.0         # degrees a tuft stands up off the skin
LIFT_SPREAD = 7.0   # degrees it may stand up more or less
TURN_SPREAD = 14.0  # degrees it may turn either way from the way the fur grows
LENGTH_SPREAD = 0.25# the share its length may be longer or shorter
SINK = 0.006        # cells: the root sunk under the skin, so no gap shows under it
CLEAR = 0.5         # least share of its height that the tip must keep off the skin, or it reaches another part of the body
FACE_OPEN = 0.5     # least share of the skin's normal pointing forward (in the head's frame) that is left as bare face
SOLE = -0.4         # the foot's skin facing further down than this is left bare
TIP_SHADE = 0.8     # the image's brightness at a tuft's tip (its root is 1, the skin's own colour, so it melts into it)
IMAGE_SIZE = 16

# Each bone's region: a tuft's length in cells and how close together tufts are (a share of SPACING). A bone not listed
# takes its name's first match; a bone matching nothing in here (the fingers) is left bare.
REGIONS = (
    ('spine.006', 0.052, 0.8),       # the head
    ('spine.005', 0.050, 0.85),      # the neck
    ('spine.004', 0.050, 0.85),
    ('spine', 0.070, 1.0),           # the trunk
    ('pelvis', 0.070, 1.0),
    ('shoulder', 0.070, 0.95),
    ('upper_arm', 0.060, 0.85),
    ('forearm', 0.052, 0.8),
    ('thigh', 0.070, 1.0),
    ('shin', 0.056, 0.85),
    ('hand.L', 0.026, 0.6),          # the back of the hand (its finger bones have a dot and number, FINGERS)
    ('hand.R', 0.026, 0.6),
    ('foot', 0.034, 0.7),
    ('toe', 0.026, 0.6),
    ('heel', 0.034, 0.7),
)
FINGERS = ('hand.L.', 'hand.R.', 'thumb')

argv = sys.argv[sys.argv.index('--') + 1:]
out_glb = argv[0]
out_blend = argv[1] if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

body = walk.join_body()
rig = body.parent
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0
random.seed(SEED)

world = body.matrix_world
turn = world.to_3x3()
rest = [world @ v.co for v in body.data.vertices]
body.data.calc_loop_triangles()
tris = [tuple(t.vertices) for t in body.data.loop_triangles]
tree = BVHTree.FromPolygons(rest, tris)
normals = [(turn @ v.normal).normalized() for v in body.data.vertices]
low = min(p.z for p in rest)
cell = (max(p.z for p in rest) - low) / FIGURE_HEIGHT    # rig units in a cell

groups = {g.index: g.name for g in body.vertex_groups if g.name in rig.data.bones}
def weights_of(i):
    return [(groups[e.group], e.weight) for e in body.data.vertices[i].groups if e.group in groups and e.weight > 0]
bone_of = [max(weights_of(i), key=lambda x: x[1])[0] if weights_of(i) else None for i in range(len(rest))]

def region_of(bone):
    if bone is None or any(bone.startswith(x) for x in FINGERS):
        return None
    for name, length, spacing in REGIONS:
        if bone == name or bone.startswith(name + '.') or bone.startswith(name):
            return length, spacing
    return None

def joint(name, tail=False):
    b = rig.data.bones[name]
    return rig.matrix_world @ (b.tail_local if tail else b.head_local)

# The head's frame at rest: its bone runs up, and the figure faces -y (as walk.py has it).
head_up = (joint(walk.HEAD, True) - joint(walk.HEAD)).normalized()
head_front = (Vector((0, -1, 0)) - head_up * Vector((0, -1, 0)).dot(head_up)).normalized()
head_mid = sum((rest[i] for i in range(len(rest)) if bone_of[i] == walk.HEAD), Vector()) / max(1, sum(b == walk.HEAD for b in bone_of))

def growth(bone, p, n):
    """The way the fur lies at p on this bone's skin, on the skin. Down the limbs, down the trunk, back off the face."""
    if bone == walk.HEAD:
        way = -head_front * 0.8 - head_up * 0.6
    else:
        b = rig.data.bones[bone]
        way = (rig.matrix_world.to_3x3() @ (b.tail_local - b.head_local)).normalized()
        if bone.startswith('foot') or bone.startswith('toe') or bone.startswith('heel'):
            way = Vector((0, -1, -0.2))
        elif way.z > 0.3:   # the spine's bones run up, and fur grows down them
            way = -way
    way = way - n * way.dot(n)
    if way.length < 1e-4:
        way = Vector((0, 0, -1)) - n * -n.z
    return way.normalized()

def tuft_at(tri, bary):
    """A tuft rooted at this place on the triangle, or None if the skin there stays bare."""
    p = sum((rest[i] * b for i, b in zip(tri, bary)), Vector())
    n = sum((normals[i] * b for i, b in zip(tri, bary)), Vector()).normalized()
    bone = bone_of[max(zip(tri, bary), key=lambda x: x[1])[0]]
    region = region_of(bone)
    if region is None:
        return None
    if bone == walk.HEAD and n.dot(head_front) > FACE_OPEN and (p - head_mid).dot(head_front) > 0:
        return None
    if bone.startswith(('foot', 'toe', 'heel')) and n.z < SOLE:
        return None
    return p, n, bone, region

def build(p, n, bone, length):
    length *= cell * (1 + random.uniform(-LENGTH_SPREAD, LENGTH_SPREAD))
    way = Matrix.Rotation(math.radians(random.uniform(-TURN_SPREAD, TURN_SPREAD)), 3, n) @ growth(bone, p, n)
    side = n.cross(way).normalized()
    lift = math.radians(LIFT + random.uniform(-LIFT_SPREAD, LIFT_SPREAD))
    tip = p + way * length * math.cos(lift) + n * length * math.sin(lift)
    half = length * WIDTH / 2
    sink = n * SINK * cell
    base = [p + side * half - sink, p - way * half * 0.7 - sink, p - side * half - sink]
    return base, tip, length * math.sin(lift)

def clear(p, n, tip, height):
    """False if the tuft runs into the body or its tip ends up close to another part of it."""
    start = p + n * 0.01 * cell
    hit = tree.ray_cast(start, (tip - start).normalized(), (tip - start).length)
    if hit[0] is not None:
        return False
    return tree.find_nearest(tip)[3] >= height * CLEAR

# Dart throwing, area weighted, in a grid of cells a spacing wide, as the wanted count of tufts isn't known beforehand.
areas = []
total = 0.0
for t in tris:
    a, b, c = (rest[i] for i in t)
    total += (b - a).cross(c - a).length / 2
    areas.append(total)
placed = []
grid = {}
reach = SPACING * cell
def key(p):
    return (int(p.x // reach), int(p.y // reach), int(p.z // reach))
def near(p, gap):
    k = key(p)
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for q in grid.get((k[0] + dx, k[1] + dy, k[2] + dz), ()):
                    if (q - p).length < gap:
                        return True
    return False

verts, faces, uvs = [], [], []
for _ in range(int(total / (reach * reach) * 60)):
    t = tris[bisect.bisect(areas, random.uniform(0, total))]
    u, v = random.random(), random.random()
    if u + v > 1:
        u, v = 1 - u, 1 - v
    bary = (1 - u - v, u, v)
    found = tuft_at(t, bary)
    if found is None:
        continue
    p, n, bone, (length, spacing) = found
    if near(p, reach * spacing):
        continue
    base, tip, height = build(p, n, bone, length)
    if not clear(p, n, tip, height):
        continue
    grid.setdefault(key(p), []).append(p)
    first = len(verts)
    verts += base + [tip]
    uvs += [0.0, 0.0, 0.0, 1.0]
    faces += [(first, first + 1, first + 3), (first + 1, first + 2, first + 3), (first + 2, first, first + 3)]
    placed.append(p)
print('FUR %d tufts, %d vertices, %d triangles' % (len(placed), len(verts), len(faces)))

mesh = bpy.data.meshes.new(NAME)
mesh.from_pydata([tuple(v) for v in verts], [], faces)
mesh.update()
# Each spike wound out, whichever way round its side came.
for k in range(0, len(faces), 3):
    tip = mesh.vertices[faces[k][2]].co
    a, b = (mesh.vertices[i].co for i in faces[k][:2])
    c = mesh.vertices[faces[k + 1][1]].co
    if (b - a).cross(tip - a).dot(a - c) < 0:
        for j in range(3):
            mesh.polygons[k + j].flip()
for poly in mesh.polygons:
    poly.use_smooth = True      # flat faces would split the vertices in the .glb
uv = mesh.uv_layers.new(name='UVMap')
for loop in mesh.loops:
    uv.data[loop.index].uv = (0.5, uvs[loop.vertex_index])

image = bpy.data.images.new('Furskin', 2, IMAGE_SIZE, alpha=False)
px = []
for y in range(IMAGE_SIZE):
    shade = 1 + (TIP_SHADE - 1) * y / (IMAGE_SIZE - 1)
    px += [shade, shade, shade, 1.0] * 2
image.pixels = px
image.pack()
material = bpy.data.materials.new('Furskin')
material.use_nodes = True
nodes = material.node_tree
texture = nodes.nodes.new('ShaderNodeTexImage')
texture.image = image
nodes.links.new(texture.outputs['Color'], nodes.nodes['Principled BSDF'].inputs['Base Color'])
mesh.materials.append(material)

fur = bpy.data.objects.new(NAME, mesh)
bpy.context.scene.collection.objects.link(fur)
bpy.ops.object.select_all(action='DESELECT')
fur.select_set(True)
bpy.context.view_layer.objects.active = fur
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False)
if out_blend:
    fur.parent = rig
    fur.matrix_parent_inverse = rig.matrix_world.inverted()
    fur.modifiers.new('Armature', 'ARMATURE').object = rig
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
