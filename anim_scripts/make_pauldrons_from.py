"""Makes Pauldrons and PauldronsBig from Maru's own hand-made pad (the `Pauldron` mesh in
Models/unused/pawnHumanRigPauldron.blend, over the figure's right shoulder, set up on the recon armour, with a `Big` shape
key for Space Marine armour), in place of make_pauldrons.py's round domes, for GrimWorld 40,000's shoulder pads.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_pauldrons_from.py -- <out folder> [out.blend]

Writes <out folder>/Pauldrons.glb (the Basis shape) and PauldronsBig.glb (the Big key), each the pad and its mirror over
the left shoulder. The pad was placed on the recon armour, but GrimWorld's armour is drawn as the marine power armour
(Models/garments/powerArmor.glb), whose shoulders stand a little higher and wider: each shape is moved OUT along the pad's
mean facing (Blender units at the rig's scale, one cell about 17.6), which brings its median clearance off the marine armour
to about what it had off the recon armour; the run prints both. Weighted as one piece per side, WEIGHTS of the shoulder and
upper arm bones on its side (export_frames.py's part_weights keeps them).

Its texture is its own, painted here (paint()), not the recon armour's atlas it was cut from: the pad unwrapped flat along
its facing and painted as a plain armour plate, a raised rim round its edge with a groove inside it and rivets along it, a
recessed roundel in its middle for a chapter's mark, faint scuffs; near white, so the apparel's colour tints it. The
.blend is never saved; the optional out.blend is a copy with both pairs on the rig, to look at."""
import bpy, bmesh, sys, math, os
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_pauldrons_from.py: give an out folder')
out_dir = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'Models', 'unused', 'pawnHumanRigPauldron.blend')
SOURCE_OBJECT = 'Pauldron'
MARINE = os.path.join(ROOT, 'Models', 'garments', 'powerArmor.glb')
RECON = os.path.join(ROOT, 'Models', 'garments', 'ArmorRecon.glb')
SHAPES = (('Pauldrons', 'Basis'), ('PauldronsBig', 'Big'))

OUT = 0.45         # moved this far out along the pad's mean facing, off the recon armour onto the marine armour's shoulder
WEIGHTS = {'shoulder': 0.4, 'upper_arm': 0.6}   # by side, .L or .R
SIZE = 512         # the texture's size in pixels
MARGIN = 0.04      # the unwrap's margin round the pad, of the texture

# The plate's paint, in shares of the pad's half size across (the unwrap's larger half extent): the rim's width, the groove
# inside it, the rivets along the rim (their spacing and radius), and the roundel in the middle (its radius and ring).
RIM = 0.10
GROOVE = 0.025
RIVET_STEP = 0.30
RIVET = 0.032
ROUNDEL = 0.30
RING = 0.035
PLATE = 0.88       # the plate's grey, the rim's, the groove's, the rivets'
RIM_GREY = 0.80
GROOVE_GREY = 0.55
RIVET_GREY = 0.62


def tree_of(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    new = [o for o in bpy.data.objects if o not in before]
    o = next(x for x in new if x.type == 'MESH')
    b = bmesh.new()
    b.from_mesh(o.data)
    b.transform(o.matrix_world)
    for x in new:
        bpy.data.objects.remove(x)
    return BVHTree.FromBMesh(b)


def clearance(points, tris, tree):
    """The median distance off the armour's surface of points across the pad (negative inside it)."""
    samples = list(points)
    for a, b, c in tris:
        pa, pb, pc = points[a], points[b], points[c]
        samples += [pa + (pb - pa) * u + (pc - pa) * v for u in (0.15, 0.33, 0.6) for v in (0.15, 0.33) if u + v <= 0.9]
    ds = []
    for q in samples:
        loc, n, _, d = tree.find_nearest(q)
        ds.append(d if (q - loc).dot(n) > 0 else -d)
    ds.sort()
    return ds[len(ds) // 2], ds[0]


# The source pad: its triangles, UVs left behind, and each shape's world places.
with bpy.data.libraries.load(SOURCE, link=False) as (src, dst):
    dst.objects = [SOURCE_OBJECT]
source = dst.objects[0]
if source is None:
    sys.exit('make_pauldrons_from.py: no %s in %s' % (SOURCE_OBJECT, SOURCE))
bpy.context.scene.collection.objects.link(source)   # (an object read from a library has its world matrix only once linked)
bpy.context.view_layer.update()
keys = source.data.shape_keys.key_blocks if source.data.shape_keys else None
tris = [tuple(p.vertices) for p in source.data.polygons]
if any(len(t) != 3 for t in tris):
    sys.exit('make_pauldrons_from.py: the pad must be triangles')
shapes = {}
for name, key in SHAPES:
    data = keys[key].data if keys and key in keys else source.data.vertices
    if not (keys and key in keys):
        print('WARNING: the pad has no %s key; %s takes its mesh as it is' % (key, name))
    shapes[name] = [source.matrix_world @ d.co for d in data]
bpy.data.objects.remove(source)

body = bpy.data.objects['Body']
rig = body.find_armature()
joint = rig.matrix_world @ rig.data.bones['upper_arm.R'].head_local
marine_tree, recon_tree = tree_of(MARINE), tree_of(RECON)

# Its facing: the area-weighted mean normal, turned away from the shoulder joint; the triangles wound to face it.
base = shapes['Pauldrons']
facing = Vector()
for a, b, c in tris:
    facing += (base[b] - base[a]).cross(base[c] - base[a])
middle = sum(base, Vector()) / len(base)
if facing.dot(middle - joint) < 0:
    facing = -facing
    tris = [(a, c, b) for a, b, c in tris]
facing.normalize()

# The unwrap: flat along the facing, u across the figure's front and v up the pad, scaled to fit with a margin.
up = Vector((0, 0, 1))
across = up.cross(facing).normalized() if abs(up.dot(facing)) < 0.95 else Vector((0, 1, 0)).cross(facing).normalized()
upward = facing.cross(across).normalized()
flat = [((p - middle).dot(across), (p - middle).dot(upward)) for p in base]
lo_u, hi_u = min(f[0] for f in flat), max(f[0] for f in flat)
lo_v, hi_v = min(f[1] for f in flat), max(f[1] for f in flat)
span = max(hi_u - lo_u, hi_v - lo_v)
uvs = [(MARGIN + (1 - 2 * MARGIN) * ((u - lo_u) + (span - (hi_u - lo_u)) / 2) / span,
        MARGIN + (1 - 2 * MARGIN) * ((v - lo_v) + (span - (hi_v - lo_v)) / 2) / span) for u, v in flat]

# The pad's outline: its open edges (on one triangle only), walked into a loop, in the unwrap.
count = {}
for t in tris:
    for k in range(3):
        e = tuple(sorted((t[k], t[(k + 1) % 3])))
        count[e] = count.get(e, 0) + 1
edges = [e for e, n in count.items() if n == 1]
nxt = {}
for a, b in edges:
    nxt.setdefault(a, []).append(b)
    nxt.setdefault(b, []).append(a)
loop = [edges[0][0]]
prev = None
while True:
    here = loop[-1]
    step = [x for x in nxt[here] if x != prev]
    if not step or step[0] == loop[0]:
        break
    prev = here
    loop.append(step[0])
outline = np.array([uvs[i] for i in loop])


def paint():
    """The plate's texture as an SIZE x SIZE RGBA array, row 0 at v = 0."""
    s = (np.arange(SIZE) + 0.5) / SIZE
    u, v = np.meshgrid(s, s)
    # Distance to the outline (in texture widths) and inside or not, by the crossing rule.
    dist = np.full(u.shape, 1e9)
    inside = np.zeros(u.shape, bool)
    for k in range(len(outline)):
        (x0, y0), (x1, y1) = outline[k], outline[(k + 1) % len(outline)]
        dx, dy = x1 - x0, y1 - y0
        t = np.clip(((u - x0) * dx + (v - y0) * dy) / (dx * dx + dy * dy), 0, 1)
        dist = np.minimum(dist, np.hypot(u - x0 - t * dx, v - y0 - t * dy))
        cross = ((y0 > v) != (y1 > v)) & (u < x0 + (v - y0) * dx / (dy if abs(dy) > 1e-12 else 1e-12))
        inside ^= cross
    half = (1 - 2 * MARGIN) / 2
    d = np.where(inside, dist, -dist) / half          # in the pad's half size, + inside
    rng = np.random.default_rng(7)
    grey = np.full(u.shape, PLATE)
    # The plate bellies out a little: lighter toward its middle.
    grey += 0.06 * np.clip(d / 0.5, 0, 1) - 0.03
    # The rim: a raised band round the edge, its outer lip lit and its inner side shaded, then the groove inside it.
    rim = d < RIM
    grey = np.where(rim, RIM_GREY + 0.08 * np.clip(1 - d / (RIM * 0.4), 0, 1), grey)
    groove = (d >= RIM) & (d < RIM + GROOVE)
    grey = np.where(groove, GROOVE_GREY, grey)
    grey = np.where((d >= RIM + GROOVE) & (d < RIM + GROOVE * 2), grey + 0.05, grey)   # the plate's lit edge
    # Rivets along the rim's middle line, evenly by the outline's length.
    lengths = [np.hypot(*(outline[(k + 1) % len(outline)] - outline[k])) for k in range(len(outline))]
    total = sum(lengths)
    step = RIVET_STEP * half
    n = max(6, int(round(total / step)))
    centre = outline.mean(axis=0)
    for j in range(n):
        at = total * j / n
        k = 0
        while at > lengths[k]:
            at -= lengths[k]
            k += 1
        p = outline[k] + (outline[(k + 1) % len(outline)] - outline[k]) * (at / lengths[k])
        p = p + (centre - p) / np.hypot(*(centre - p)) * (RIM * 0.5 * half)   # in onto the rim's middle
        r = np.hypot(u - p[0], v - p[1]) / half
        head = r < RIVET
        lit = np.clip(((p[0] - u) + (v - p[1])) / (RIVET * half), -1, 1)    # lit from the upper left
        grey = np.where(head, RIVET_GREY + 0.18 * lit, grey)
        grey = np.where((r >= RIVET) & (r < RIVET * 1.5), grey - 0.12, grey)   # its shadow ring
    # The roundel: a recessed disc in the middle with a raised ring round it.
    r = np.hypot(u - centre[0], v - centre[1]) / half
    grey = np.where(r < ROUNDEL, grey - 0.06, grey)
    grey = np.where((r >= ROUNDEL) & (r < ROUNDEL + RING), RIM_GREY + 0.06, grey)
    grey = np.where((r >= ROUNDEL - RING * 0.6) & (r < ROUNDEL), GROOVE_GREY + 0.15, grey)
    # Scuffs and wear: fine noise, a few scratches across the plate, the rim's lip chipped darker.
    grey += rng.normal(0, 0.012, u.shape)
    for _ in range(6):
        p = rng.uniform(0.2, 0.8, 2)
        a = rng.uniform(0, math.pi)
        length = rng.uniform(0.03, 0.12)
        dx, dy = math.cos(a), math.sin(a)
        t = np.clip((u - p[0]) * dx + (v - p[1]) * dy, -length, length)
        far = np.hypot(u - p[0] - t * dx, v - p[1] - t * dy)
        grey = np.where((far < 0.0025) & ~rim, grey - 0.05, grey)
    grey = np.where(rim & (d < RIM * 0.3) & (rng.random(u.shape) < 0.04), grey - 0.15, grey)
    grey = np.clip(grey, 0, 1)
    rgba = np.ones(u.shape + (4,))
    rgba[..., 0] = grey
    rgba[..., 1] = grey
    rgba[..., 2] = grey * 0.99
    return rgba


image = bpy.data.images.new('Pauldrons', SIZE, SIZE, alpha=True)
image.pixels.foreach_set(paint().astype(np.float32).ravel())
image.pack()
material = bpy.data.materials.new('Pauldrons')
material.use_nodes = True
nodes = material.node_tree.nodes
tex = nodes.new('ShaderNodeTexImage')
tex.image = image
material.node_tree.links.new(tex.outputs['Color'], nodes['Principled BSDF'].inputs['Base Color'])

made = []
for name, key in SHAPES:
    points = [p + facing * OUT for p in shapes[name]]
    before = clearance(shapes[name], tris, recon_tree)
    after = clearance(points, tris, marine_tree)
    print('PAULDRON %s: off the recon armour as made %.2f (least %.2f), off the marine armour now %.2f (least %.2f)'
          % (name, before[0], before[1], after[0], after[1]))
    count = len(points)
    mirror = [Vector((-p.x, p.y, p.z)) for p in points]
    verts = points + mirror
    faces = tris + [(a + count, c + count, b + count) for a, b, c in tris]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(p) for p in verts], [], faces)
    mesh.update()
    layer = mesh.uv_layers.new(name='UVMap')
    for poly in mesh.polygons:
        poly.use_smooth = True
        for li in poly.loop_indices:
            vi = mesh.loops[li].vertex_index
            layer.data[li].uv = uvs[vi % count]
    mesh.materials.append(material)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    for side, rows in (('R', range(count)), ('L', range(count, 2 * count))):
        for bone, w in WEIGHTS.items():
            obj.vertex_groups.new(name=bone + '.' + side).add(list(rows), w, 'REPLACE')
    obj.parent = rig
    obj.modifiers.new('Armature', 'ARMATURE').object = rig
    bpy.ops.object.select_all(action='DESELECT')
    rig.hide_set(False)
    rig.hide_viewport = False
    obj.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = obj
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, name + '.glb')
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format='GLB', export_skins=True,
                              export_animations=False, export_morph=False, export_extras=True)
    print('PAULDRONS %s: %d vertices, %d faces -> %s' % (name, len(verts), len(faces), path))
    made.append(obj)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
