"""Makes the Furskin gene's fur by pulling a few of the body's own edges out into triangles, in place of make_furskin.py's spikes.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_fur_edges.py -- <out.glb> [out.blend]

Each tuft stands on one edge of the body: a low wedge of three triangles, its base and its point pulled out down the fur and up
off the skin, closed behind on a point on the skin. An edge that runs across the way the fur grows is the base itself. Most of the body's edges
don't (its faces are long and thin on the limbs), so one that runs along it or is too long gives only its middle, a short base turned across the fur and
laid on the skin. Fanning the edge's own faces round the point made shards; standing on one line alone keeps every tuft a triangle
in the figure's outline. They are short and the same on both sides (the body is near enough symmetric), thickest round the head, as the fur
itself is to be the skin's texture. The skin stays whole under them (the exporter's "fur"). The face, the very top of the head, the hands, the feet, the insides of the
thighs and the groin are left bare, and a tuft that would reach another part of the body is left out. Output goes in
Models/garments/Furskin.glb; the exporter's GARMENT_PROPS draws it for the gene (defs Gene_Furskin) rather than for apparel. The
.blend is never saved; the optional out.blend is a separate file with the fur on the rig.
Lengths are in cells of the finished figure (FIGURE_HEIGHT tall), so they hold if the rig is scaled."""
import bpy, bmesh, sys, os, math, random
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

NAME = 'Furskin'
FIGURE_HEIGHT = 1.25
SEED = 7
LIFT = 26.0         # degrees a tuft stands up off the skin
LIFT_SPREAD = 6.0   # degrees it may stand up more or less
LENGTH_SPREAD = 0.15# the share its length may be longer or shorter
ACROSS = 0.65       # most share of the edge that may run along the way the fur grows and still be the base; past it the base is turned
EDGE_SHORT = 0.018  # cells: an edge shorter than this makes a needle, not a triangle
EDGE_LONG = 0.08    # cells: and one longer than this a sail, so its base is turned
TURNED = 0.05       # cells: the width of a turned base (less on a shorter edge)
SLIM = 2.2          # the longest a tuft may be, as a share of its edge, so a short edge on an arm still makes a triangle
BACK = 0.5          # how far behind the edge the wedge closes on the skin, as a share of its length
CLEAR = 0.5         # least share of its height that the tip must keep off the skin, or it reaches another part of the body
FACE_OPEN = 0.5     # least share of the skin's normal pointing forward (in the head's frame) left bare: the face patch's middle, so its sides are framed
CROWN = 0.92        # least share pointing up the head left bare: only the very top, where the fur has no way to lie (hair hides the rest: HideFur)
FOREHEAD = 0.45     # least share pointing up the head, on its front half, left bare: the fur runs up over the top of the forehead there, and stands like horns
EAR_X, EAR_LOW, EAR_HIGH, EAR_BACK, EAR_FRONT = 0.135, 0.92, 1.03, -0.065, 0.01    # cells, the box of the chibi's own ear at rest
EAR_CLEAR = 0.035   # cells: a tuft whose middle or tip is this near the ear would lie over it
INSIDE = -0.1       # least share of a hip or thigh's normal pointing away from the figure's middle that gets fur
TOP = 0.55          # most share of the normal pointing up that gets fur on the trunk and shoulders: on the flat of a shoulder it would stick straight out
MIRROR = 0.012      # cells: how far off the mirror of a vertex its twin may be

# Each bone's region: a tuft's length in cells and the least distance between tufts in cells. The first match on the start of
# the name wins; a bone matching nothing in here (the hands, the feet) is left bare.
REGIONS = (
    ('spine.006', 0.060, 0.045),     # the head (the hair hides most of these: HideFur)
    ('spine.005', 0.060, 0.05),      # the neck
    ('spine.004', 0.065, 0.05),
    ('spine', 0.070, 0.06),          # the trunk
    ('pelvis', 0.070, 0.07),
    ('shoulder', 0.070, 0.065),
    ('upper_arm', 0.060, 0.055),
    ('forearm', 0.055, 0.055),
    ('thigh', 0.070, 0.06),
    ('shin', 0.060, 0.06),
)

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
bm = bmesh.new()
bm.from_mesh(body.data)
bm.verts.ensure_lookup_table()
rest = [world @ v.co for v in bm.verts]
normals = [(turn @ v.normal).normalized() for v in bm.verts]
body.data.calc_loop_triangles()
tris = [tuple(t.vertices) for t in body.data.loop_triangles]
tree = BVHTree.FromPolygons(rest, tris)
zs = [p.z for p in rest]
cell = (max(zs) - min(zs)) / FIGURE_HEIGHT    # rig units in a cell

groups = {g.index: g.name for g in body.vertex_groups if g.name in rig.data.bones}
def weights_of(i):
    return [(groups[e.group], e.weight) for e in body.data.vertices[i].groups if e.group in groups and e.weight > 0]
def strongest(i):
    w = weights_of(i)
    return max(w, key=lambda x: x[1])[0] if w else None
bone_of = [strongest(i) for i in range(len(rest))]

def region_of(bone):
    if bone is None:
        return None
    for name, length, gap in REGIONS:
        if bone.startswith(name):
            return length, gap
    return None

def side_of(bone):
    return bone[-2:] if bone and bone[-2:] in ('.L', '.R') else ''

def joined(one, other):
    """True if these two bones' skin is one surface: the same side, or the trunk and a shoulder or hip, which carry a side only by being on it."""
    if side_of(one) == side_of(other):
        return True
    return any(x.startswith('spine') and y.startswith(('shoulder', 'pelvis')) for x, y in ((one, other), (other, one)))

def joint(name, tail=False):
    b = rig.data.bones[name]
    return rig.matrix_world @ (b.tail_local if tail else b.head_local)

# The head's frame at rest: its bone runs up, and the figure faces -y (as walk.py has it).
head_up = (joint(walk.HEAD, True) - joint(walk.HEAD)).normalized()
head_front = (Vector((0, -1, 0)) - head_up * Vector((0, -1, 0)).dot(head_up)).normalized()

ankle = joint('foot.L').z

# The chibi's own ears, found by place as CatEars.cs finds them. Game model space has z toward the figure's front, its feet at 0.
floor = min(zs)
ears = [p for i, p in enumerate(rest) if bone_of[i] == walk.HEAD and abs(p.x) / cell > EAR_X
        and EAR_LOW < (p.z - floor) / cell < EAR_HIGH and EAR_BACK < -p.y / cell < EAR_FRONT]
ear_tree = KDTree(len(ears))
for i, p in enumerate(ears):
    ear_tree.insert(p, i)
ear_tree.balance()
print('FUR %d vertices on the ears' % len(ears))

def growth(bone, n):
    """The way the fur lies on this bone's skin, on the skin. Down the limbs, down the trunk, back and down off the face."""
    if bone == walk.HEAD:
        way = -head_front * 0.8 - head_up * 0.6
    else:
        b = rig.data.bones[bone]
        way = (rig.matrix_world.to_3x3() @ (b.tail_local - b.head_local)).normalized()
        if way.z > 0.3:   # the spine's bones run up, and fur grows down them
            way = -way
    way = way - n * way.dot(n)
    if way.length < 1e-4:
        way = Vector((0, 0, -1)) - n * -n.z
    return way.normalized()

# Each vertex's twin across the figure's middle, or None.
mirror_tree = KDTree(len(rest))
for i, p in enumerate(rest):
    mirror_tree.insert(p, i)
mirror_tree.balance()
def twin_of(i):
    _, j, dist = mirror_tree.find(Vector((-rest[i].x, rest[i].y, rest[i].z)))
    return j if dist <= MIRROR * cell else None
twin = [twin_of(i) for i in range(len(rest))]

def tuft_of(a, b, spread):
    """The tuft on the edge a-b as (its four corners), or None if it stays bare. spread: its length's and lift's share of their spreads."""
    length = (rest[a] - rest[b]).length / cell
    if length < EDGE_SHORT:
        return None
    bone = bone_of[a]
    region = region_of(bone)
    if region is None or region_of(bone_of[b]) is None or not joined(bone, bone_of[b]):
        return None
    mid = (rest[a] + rest[b]) / 2
    n = (normals[a] + normals[b]).normalized()
    if bone == walk.HEAD and (n.dot(head_front) > FACE_OPEN or n.dot(head_up) > CROWN or (n.dot(head_front) > 0 and n.dot(head_up) > FOREHEAD)):
        return None
    if bone != walk.HEAD and bone.startswith(('spine', 'shoulder', 'pelvis')) and n.z > TOP:
        return None
    if bone.startswith(('pelvis', 'thigh')) and n.x * math.copysign(1, mid.x) < INSIDE:
        return None
    way = growth(bone, n)
    left, right = rest[a], rest[b]
    if length > EDGE_LONG or abs((left - right).normalized().dot(way)) > ACROSS:
        length = min(length, TURNED)
        half = n.cross(way).normalized() * length * cell / 2
        left, right = tree.find_nearest(mid - half)[0], tree.find_nearest(mid + half)[0]
    pull = min(region[0] * cell, length * cell * SLIM) * (1 + spread[0] * LENGTH_SPREAD)
    lift = math.radians(LIFT + spread[1] * LIFT_SPREAD)
    tip = mid + way * pull * math.cos(lift) + n * pull * math.sin(lift)
    height = pull * math.sin(lift)
    if min(ear_tree.find(mid)[2], ear_tree.find(tip)[2]) < EAR_CLEAR * cell:
        return None
    if tip.z < ankle:   # past the shin's end it would hang off the foot, or below the sole
        return None
    # Nothing in the way, and the skin under the tip is this limb's and gets fur: the tip is skinned by what lies under it.
    start = mid + n * 0.002 * cell
    if tree.ray_cast(start, (tip - start).normalized(), (tip - start).length)[0] is not None:
        return None
    _, _, face, dist = tree.find_nearest(tip)
    if dist < height * CLEAR or any(region_of(bone_of[i]) is None or not joined(bone, bone_of[i]) for i in tris[face]):
        return None
    back = tree.find_nearest(mid - way * pull * BACK)[0]
    # Seen from outside with the fur running away: the end on the left first, so the faces are wound outward.
    if (right - left).dot(n.cross(way)) < 0:
        left, right = right, left
    return left, right, back, tip

verts, faces, placed, where = [], [], [], {}
edges = list(bm.edges)
random.shuffle(edges)
unmatched = 0
for edge in edges:
    a, b = edge.verts[0].index, edge.verts[1].index
    if twin[a] is None or twin[b] is None:
        unmatched += 1
        continue
    # The other side's edge has the same length and lift, a little different, so the two sides don't look stamped.
    spread = (random.uniform(-1, 1), random.uniform(-1, 1))
    one = tuft_of(a, b, spread)
    other = tuft_of(twin[a], twin[b], (spread[0] * 0.8, spread[1] * 0.8))
    if one is None or other is None:
        continue
    mids = [(one[0] + one[1]) / 2, (other[0] + other[1]) / 2]
    if (mids[0] - mids[1]).length < 1e-3 * cell:    # on the middle line, its own twin
        mids, wedges = mids[:1], [one]
    else:
        wedges = [one, other]
    gap = region_of(bone_of[a])[1] * cell
    if any((q - m).length < gap for m in mids for q in placed):
        continue
    if len(mids) == 2 and (mids[0] - mids[1]).length < gap:
        continue
    placed += mids
    kind = 'head' if bone_of[a] == walk.HEAD else bone_of[a].split('.')[0]
    where[kind] = where.get(kind, 0) + len(mids)
    # Each tuft has vertices of its own and is its own island (SkinnedFigure.FurUnder).
    for left, right, back, tip in wedges:
        k = len(verts)
        verts += [left, right, back, tip]
        faces += [(k + 2, k, k + 3), (k + 1, k + 2, k + 3), (k, k + 1, k + 3)]
print('FUR %d tufts, %d vertices, %d triangles (%d edges with no twin): %s' % (len(placed), len(verts), len(faces), unmatched, sorted(where.items())))

mesh = bpy.data.meshes.new(NAME)
mesh.from_pydata([tuple(v) for v in verts], [], faces)
mesh.update()
for poly in mesh.polygons:
    poly.use_smooth = True      # flat faces would split the vertices in the .glb

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
