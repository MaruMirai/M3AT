"""Paints the long johns (make_underwear.py's LongJohns) a texture of their own, so they read as one knitted union suit
rather than the collared shirt and trousers whose body faces they are cut from.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/paint_long_johns.py -- <in.glb> <out.glb> [image.png]

in.glb is make_underwear.py's LongJohns (untextured, on the body's own UV atlas). Every texel is drawn by its rest place
on the garment (found through the triangle it lies in on the atlas), as paint_backpack.py does: a fine knit rib running
along the body and each limb crossed by rows along it, a thermal's waffle knit (a whole number of ribs round each part,
RIBS, so no seam breaks them, blended across the shoulders and hips by the nearest bones), plain ribbed cuffs at the wrists and ankles and a ribbed band round the neck
(by distance over the garment from those openings, found as its open edge loops), a henley placket down the front of
the chest with BUTTONS, and the union suit's drop seat at the back (a stitched flap over the seat, buttoned at its top
corners). No waistband. All greys near white (BASE), as the game multiplies the image by the apparel's colour.
Lengths are in leg lengths of the rig (the thigh's head to the floor)."""
import bpy, bmesh, sys, os, math
import numpy as np
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
in_glb, out_glb = argv[0], argv[1]
image_path = argv[2] if len(argv) > 2 else os.path.splitext(out_glb)[0] + '.png'

SIZE = 1024            # the image's side, pixels
BASE = 0.93            # the cloth's grey
RIB = 0.05             # how much the cuffs' ribs lighten and darken it
WAFFLE = 0.06          # and the waffle knit's cells the rest (a thermal's raised squares with dark lines between)
WAFFLE_ALONG = 0.024   # a cell's length along the part (about as long as it is wide round the trunk)
# Ribs round each part (a whole number, so they meet across its seam): the trunk, a thigh, a shin, an upper arm, a forearm.
RIBS = {'trunk': 44, 'thigh': 24, 'shin': 18, 'upper_arm': 16, 'forearm': 14}
CUFF = 0.045           # the cuffs' depth up from the wrist and ankle openings
CUFF_RIBS = 2.0        # how many times as many ribs the cuffs and neck band have
CUFF_SHADE = 0.94      # and their grey's share of the cloth's
NECK = 0.03            # the neck band's depth down from the neck opening
STITCH = 0.004         # a line of stitching's half width
STITCH_SHADE = 0.80
PLACKET_HALF = 0.022   # the placket's half width either side of the middle, and its length down from the neckline
PLACKET_LONG = 0.2     # (the chibi's chin hides its top)
BUTTONS = 4            # down the placket
BUTTON = 0.011         # a button's radius
BUTTON_SHADE = 0.86
SEAT = {'half': 0.13, 'top': 0.10, 'bottom': -0.07}   # the drop seat: half width; top and bottom over the thigh's head

rig = bpy.data.objects['metarig']
rig.data.pose_position = 'REST'
bpy.context.view_layer.update()
bones = rig.data.bones
def head(b):
    return np.array(tuple(rig.matrix_world @ bones[b].head_local))
def tail(b):
    return np.array(tuple(rig.matrix_world @ bones[b].tail_local))
hip_l = head('thigh.L')
leg = hip_l[2]   # the thigh's head to the floor (z 0)
zero_z = hip_l[2]

before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=in_glb, disable_bone_shape=True)
g = next(o for o in set(bpy.data.objects) - before if o.type == 'MESH')
for o in set(bpy.data.objects) - before:
    if o is not g:
        bpy.data.objects.remove(o)
mw = g.matrix_world.copy()
me = g.data
uv = me.uv_layers.active
me.calc_loop_triangles()
verts = np.array([tuple(mw @ v.co) for v in me.vertices])

# The parts, each an axis (from, to) the ribs run along, and the way round it they are counted from.
PARTS = []
spine = [head('spine'), tail('spine.003')]
PARTS.append(('trunk', spine[0], spine[1]))
for s in ('L', 'R'):
    PARTS += [('thigh', head('thigh.' + s), tail('thigh.' + s)), ('shin', head('shin.' + s), tail('shin.' + s)),
              ('upper_arm', head('upper_arm.' + s), tail('upper_arm.' + s)),
              ('forearm', head('forearm.' + s), tail('forearm.' + s))]

def seg_dist(p, a, b):
    ab = b - a
    t = np.clip(((p - a) @ ab) / (ab @ ab), 0, 1)
    return np.linalg.norm(p - (a + t[:, None] * ab), axis=1)

def ribs(p, a, b, n, times=1.0):
    """The knit's brightness at points p round the axis a..b: n ribs round, so whole round the part."""
    d = (b - a) / np.linalg.norm(b - a)
    ref = np.array([0.0, -1.0, 0.0]) if abs(d[1]) < 0.9 else np.array([0.0, 0.0, 1.0])
    u = ref - d * (ref @ d)
    u /= np.linalg.norm(u)
    v = np.cross(d, u)
    r = p - a
    ang = np.arctan2(r @ v, r @ u)
    x = ang * n * times
    # a rounded rib: a wide light rib and a narrow dark groove between
    s = np.cos(x)
    return np.sign(s) * np.abs(s) ** 0.6

def waffle(p, a, b, n):
    """A waffle knit's brightness: the ribs round the part crossed by rows along it, dark where either has its groove."""
    d = (b - a) / np.linalg.norm(b - a)
    along = np.cos(2 * math.pi * ((p - a) @ d) / (WAFFLE_ALONG * leg))
    around = ribs(p, a, b, n)
    cell = np.minimum(around, np.sign(along) * np.abs(along) ** 0.6)
    return cell

# The open edge loops: the neck (highest), the wrists (furthest out sideways), the ankles (lowest).
bm = bmesh.new()
bm.from_mesh(me)
bm.transform(mw)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)   # (the .glb is split at every UV seam, which isn't an opening)
edges = [e for e in bm.edges if e.is_boundary]
loops, seen = [], set()
for e in edges:
    if e in seen:
        continue
    stack, loop = [e], []
    while stack:
        x = stack.pop()
        if x in seen:
            continue
        seen.add(x)
        loop.append(x)
        for v in x.verts:
            stack += [y for y in v.link_edges if y.is_boundary and y not in seen]
    pts = []
    for x in loop:
        a, b = x.verts[0].co, x.verts[1].co
        pts += [a.lerp(b, k / 6) for k in range(7)]
    loops.append(np.array([tuple(p) for p in pts]))
bm.free()
loops.sort(key=lambda l: -l[:, 2].mean())
neck = loops[0]
rest = loops[1:]
wrists = sorted(rest, key=lambda l: -abs(l[:, 0]).mean())[:2]
ankles = sorted(rest, key=lambda l: l[:, 2].mean())[:2]
for l in loops:
    print('LONGJOHNS loop of %d points at x %.2f..%.2f, y %.2f..%.2f, z %.2f..%.2f' % (
        len(l), l[:, 0].min(), l[:, 0].max(), l[:, 1].min(), l[:, 1].max(), l[:, 2].min(), l[:, 2].max()))
print('LONGJOHNS %d open loops: neck at %.2f, wrists at x %s, ankles at z %s' % (
    len(loops), neck[:, 2].mean(), ['%.2f' % abs(l[:, 0]).mean() for l in wrists], ['%.2f' % l[:, 2].mean() for l in ankles]))

def near(p, pts):
    """Each point's distance to the nearest of pts."""
    out = np.full(len(p), np.inf)
    for i in range(0, len(pts), 256):
        d = np.linalg.norm(p[:, None, :] - pts[None, i:i + 256, :], axis=2).min(axis=1)
        out = np.minimum(out, d)
    return out

neck_front = neck[neck[:, 1] < neck[:, 1].mean()]
neck_low = neck_front[np.argmin(np.abs(neck_front[:, 0]))][2]   # the neckline's lowest at the front middle

def colour(p, n):
    """The cloth's grey at rest places p with face normals n."""
    # The ribs, from the two nearest parts, blended by how much nearer the one is.
    ds = np.stack([seg_dist(p, a, b) for _, a, b in PARTS], axis=1)
    order = np.argsort(ds, axis=1)
    rib = np.zeros(len(p))
    cuff_rib = np.zeros(len(p))
    first = ds[np.arange(len(p)), order[:, 0]]
    second = ds[np.arange(len(p)), order[:, 1]]
    w1 = np.clip(0.5 + (second - first) / (0.06 * leg), 0.5, 1.0)
    for k, (name, a, b) in enumerate(PARTS):
        m1, m2 = order[:, 0] == k, order[:, 1] == k
        if m1.any():
            rib[m1] += w1[m1] * waffle(p[m1], a, b, RIBS[name])
            cuff_rib[m1] += ribs(p[m1], a, b, RIBS[name], CUFF_RIBS)
        if m2.any():
            rib[m2] += (1 - w1[m2]) * waffle(p[m2], a, b, RIBS[name])
    g = BASE + WAFFLE * rib
    # The cuffs and the neck band: denser ribs, a touch darker, a stitch where they meet the cloth.
    for pts, depth in [(w, CUFF) for w in wrists + ankles] + [(neck, NECK)]:
        d = near(p, pts) / leg
        band = d < depth
        g[band] = (BASE + RIB * 1.2 * cuff_rib[band]) * CUFF_SHADE
        line = np.abs(d - depth) < STITCH
        g[line] = BASE * STITCH_SHADE
    # The placket: two stitched lines down the front middle from the neckline, buttons between them.
    front = n[:, 1] < -0.2
    x, z = p[:, 0] / leg, (p[:, 2] - zero_z) / leg
    top = (neck_low - zero_z) / leg
    on = front & (z <= top + 0.005) & (z >= top - PLACKET_LONG)
    edge = on & (np.abs(np.abs(x) - PLACKET_HALF) < STITCH)
    g[edge] = BASE * STITCH_SHADE
    bottom = front & (np.abs(z - (top - PLACKET_LONG)) < STITCH) & (np.abs(x) < PLACKET_HALF)
    g[bottom] = BASE * STITCH_SHADE
    inside = on & (np.abs(x) < PLACKET_HALF - STITCH)
    g[inside] = np.minimum(g[inside], BASE * 0.97)
    for i in range(BUTTONS):
        bz = top - PLACKET_LONG * (i + 0.6) / (BUTTONS + 0.2)
        r = np.hypot(x, z - bz)
        disc = front & (r < BUTTON)
        g[disc] = BASE * BUTTON_SHADE
        rim = front & (np.abs(r - BUTTON) < 0.0025)
        g[rim] = BASE * 0.6
        for hx in (-0.35, 0.35):   # two holes
            g[front & (np.hypot(x - hx * BUTTON, z - bz) < 0.0022)] = BASE * 0.55
    # The drop seat: a stitched flap over the seat at the back, its top corners buttoned.
    back = n[:, 1] > 0.2
    s = SEAT
    flap = back & (np.abs(x) < s['half']) & (z < s['top']) & (z > s['bottom'])
    outline = back & (((np.abs(np.abs(x) - s['half']) < STITCH) & (z < s['top']) & (z > s['bottom']))
                      | ((np.abs(z - s['top']) < STITCH) & (np.abs(x) < s['half'])))
    g[flap] *= 0.985
    g[outline] = BASE * STITCH_SHADE
    for cx in (-s['half'] + 0.02, s['half'] - 0.02):
        r = np.hypot(x - cx, z - (s['top'] - 0.018))
        g[back & (r < BUTTON)] = BASE * BUTTON_SHADE
        g[back & (np.abs(r - BUTTON) < 0.0025)] = BASE * 0.6
    return np.clip(g, 0, 1)

# Every texel by the triangle it is in on the atlas.
img = np.zeros((SIZE, SIZE), np.float32)
have = np.zeros((SIZE, SIZE), bool)
for t in me.loop_triangles:
    uvs = np.array([tuple(uv.data[l].uv) for l in t.loops]) * SIZE
    ps = verts[list(t.vertices)]
    nrm = np.array(tuple((mw.to_3x3() @ t.normal).normalized()))
    lo = np.floor(uvs.min(axis=0)).astype(int) - 1
    hi = np.ceil(uvs.max(axis=0)).astype(int) + 1
    xs, ys = np.meshgrid(np.arange(lo[0], hi[0] + 1), np.arange(lo[1], hi[1] + 1))
    q = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], axis=1)
    a, b, c = uvs
    m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
    if abs(np.linalg.det(m)) < 1e-9:
        continue
    lam = np.linalg.solve(m, (q - a).T).T
    w = np.stack([1 - lam[:, 0] - lam[:, 1], lam[:, 0], lam[:, 1]], axis=1)
    inside = (w >= -0.02).all(axis=1)
    xi, yi = xs.ravel()[inside], ys.ravel()[inside]
    ok = (xi >= 0) & (xi < SIZE) & (yi >= 0) & (yi < SIZE)
    if not ok.any():
        continue
    p = w[inside][ok] @ ps
    img[yi[ok], xi[ok]] = colour(p, np.tile(nrm, (len(p), 1)))
    have[yi[ok], xi[ok]] = True

# Grown a few pixels past each island, so filtering at its edge takes the cloth, not the empty atlas.
for _ in range(8):
    grow = ~have
    acc = np.zeros_like(img)
    cnt = np.zeros_like(img)
    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        acc += np.roll(np.roll(np.where(have, img, 0), dy, 0), dx, 1)
        cnt += np.roll(np.roll(have.astype(np.float32), dy, 0), dx, 1)
    new = grow & (cnt > 0)
    img[new] = acc[new] / cnt[new]
    have |= new
img[~have] = BASE

image = bpy.data.images.new('LongJohns', SIZE, SIZE, alpha=False)
rgba = np.ones((SIZE, SIZE, 4), np.float32)
rgba[..., 0] = rgba[..., 1] = rgba[..., 2] = img
image.pixels.foreach_set(rgba.ravel())
image.filepath_raw = image_path
image.file_format = 'PNG'
image.save()
print('LONGJOHNS image %s, %.0f%% of it painted' % (image_path, 100 * have.mean()))

mat = g.data.materials[0] if g.data.materials else bpy.data.materials.new('LongJohns')
if not g.data.materials:
    g.data.materials.append(mat)
nodes = mat.node_tree.nodes
tex = nodes.new('ShaderNodeTexImage')
tex.image = image
mat.node_tree.links.new(tex.outputs['Color'], nodes['Principled BSDF'].inputs['Base Color'])
g.name = 'LongJohns'
bpy.ops.object.select_all(action='DESELECT')
g.select_set(True)
bpy.context.view_layer.objects.active = g
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False)
print('LONGJOHNS -> %s' % out_glb)
