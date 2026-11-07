"""Makes Combat Extended's tactical vest (CE_Apparel_TacVest, its sprite Things/Apparel/TacRigging/CE_TacRigging) from the
shape of the body, after its sprite: a chest rig rather than a vest. A dark belt round the waist, four magazine pouches
on it across the belly (two each side of the middle, where the belt shows between them), one wide pouch low on the back
with rounded bottom corners, and two dark straps from the front pouches straight up over the shoulders and down the back
into the back pouch, not crossing (an H harness).

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_tac_vest.py -- <out.glb> [out.blend]

The output goes in Models/garments/CE_TacVest.glb (make_from_groups.py made the old one from a group of the body's faces,
which came out a vest's shape, not the rig's).

The straps are laid on the skin as make_body_strap.py lays its own (STRAPS, at rest, world space: x to the figure's left,
y back, z up; stepped, taken to the trunk's skin, smoothed, kept out of it), INNER off the skin, over the Shell layer's
jackets (0.3) as the Webbing layer goes over them. The belt is a closed loop round the waist at BELT_Z, found by casting
out from the trunk's middle in the level plane. Each pouch is a row of columns along the belt's outside (POUCHES: where
along the belt from the front's middle, how wide, how tall, how deep; each column level, the pouch leaning in or out as
the skin under its middle does and each column pushed out until its whole height clears the skin by the belt's outside,
so a pouch follows the belly's curve across and down but its sides stay upright), with a flap over its top, and on
the front pouches a small tab on the flap, as the sprite's grey marks.

Sizes are in Blender units at the rig's scale (the figure about 22 tall, 1.25 cells).

It has UVs and a small image of flat colours (COLOURS): the belt and straps near black, as the sprite's are, which the
game's tint by the apparel's colour leaves dark; the pouches pale, so they take the apparel's colour (fabric or leather);
the tabs grey. Weights are the body's from the point under each vertex (the exporter takes them that way anyway). The
.blend is never saved; the optional out.blend is a copy with the rig on, to look at or model from."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_tac_vest.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

NAME = 'CE_TacVest'
WIDTH = 0.62           # a strap's width
BELT_WIDTH = 0.85      # the belt's
INNER = 0.42           # a strap's inside off the skin: over a jacket (the Shell layer, 0.3)
THICK = 0.16           # a strap's and the belt's thickness
STEP = 0.4             # the length of a step along a strap's path
SMOOTH = 4             # passes of smoothing a strap's line
BELT_Z = 14.5          # the belt's middle height (the waist, over the chibi's round belly)
BELT_SAMPLES = 28      # the belt's columns round the waist
CLEAR = INNER + THICK  # a pouch's inside off the skin: on the belt's outside
COLUMNS = 0.35         # the most across a pouch's column
TILT = 0.3             # the most a pouch's inside leans out per unit up or down

# Each pouch: its middle along the belt's outside from the front's middle (+ to the figure's left; the back's middle is
# half the belt's length), its width along the belt, its bottom and top heights, its depth out, how far up its bottom
# corners are rounded (0 square), and whether its flap has a tab. The front four are the sprite's magazine pouches, a
# pair each side, the belt showing in the gap between the pairs; the back one its single wide pouch.
FRONT = [(-1.95, 1.05), (-0.75, 1.05), (0.75, 1.05), (1.95, 1.05)]   # middle, width
POUCHES = [(at, wide, 13.75, 16.0, 0.62, 0.15, True) for at, wide in FRONT] + [('back', 3.0, 13.8, 16.1, 0.7, 0.6, False)]
FLAP = 0.42            # a flap's height down from the pouch's top
FLAP_OUT = 0.07        # how far a flap stands out past the pouch, and past its sides
TAB = (0.18, 0.3, 0.06)   # a tab's width, height (down from the flap's top edge) and how far it stands out from the flap

# Each strap's points: up from inside a front pouch, over the shoulder between the neck and the arm, splaying a little
# outward as the sprite's do, and down the back into the back pouch. The left one; the right is its mirror. Both ends are
# inside a pouch, which hides them.
LEFT_STRAP = [Vector((0.75, -2.3, 15.2)), Vector((0.85, -2.1, 16.4)), Vector((0.98, -1.9, 17.4)),
              Vector((1.12, -1.6, 18.4)), Vector((1.25, -1.0, 19.2)), Vector((1.35, -0.3, 19.75)),
              Vector((1.4, 0.4, 19.8)), Vector((1.35, 1.1, 19.3)), Vector((1.25, 1.4, 18.0)),
              Vector((1.12, 1.2, 16.6)), Vector((1.0, 1.4, 15.2))]
STRAPS = [LEFT_STRAP, [Vector((-p.x, p.y, p.z)) for p in LEFT_STRAP]]

COLOURS = {'strap': (0.13, 0.13, 0.14), 'pouch': (0.92, 0.91, 0.88), 'tab': (0.55, 0.55, 0.56)}
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

# The trunk's skin alone: the triangles none of whose corners belong mostly to the head, a forearm or a hand, so a strap
# doesn't snap onto the jaw and a pouch's casts don't meet a hanging hand.
names = {g.index: g.name for g in body.vertex_groups}
deform = {b.name for b in rig.data.bones if b.use_deform}
OFF_TRUNK = ('forearm', 'hand', 'thumb', 'f_', 'palm', walk.HEAD)

def dominant(i):
    gs = [(g.weight, names[g.group]) for g in me.vertices[i].groups if names[g.group] in deform]
    return max(gs)[1] if gs else ''

off = [dominant(i).startswith(OFF_TRUNK) for i in range(len(verts))]
trunk_tris = [t for t in tris if not any(off[i] for i in t)]
tree = BVHTree.FromPolygons(verts, trunk_tris)

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

def add_strap(pts, width, closed=False):
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
            corners.append((loc + sn * (h + INNER), loc + sn * (h + INNER + THICK)))
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

for path in STRAPS:
    add_strap(lay(path), WIDTH)

# The belt: out from the trunk's middle at BELT_Z in the level plane, round the whole waist, starting at the front's
# middle and going round to the figure's left (+x).
def ring_at(z, samples):
    front_y = tree.ray_cast(Vector((0, -40, z)), Vector((0, 1, 0)), 80.0)[0].y
    back_y = tree.ray_cast(Vector((0, 40, z)), Vector((0, -1, 0)), 80.0)[0].y
    middle = Vector((0, (front_y + back_y) / 2, z))
    pts = []
    for k in range(samples):
        a = math.pi * 2 * k / samples
        hit = tree.ray_cast(middle, Vector((math.sin(a), -math.cos(a), 0)), 40.0)
        if hit[0] is None:
            sys.exit('make_tac_vest.py: no skin out from the waist at %.0f degrees' % math.degrees(a))
        pts.append(hit[0])
    return pts

add_strap(lay(ring_at(BELT_Z, BELT_SAMPLES), closed=True), BELT_WIDTH, closed=True)

# The pouches' guide: the skin round the waist at the belt's height, finely, as a closed line with its length along it.
guide = ring_at(BELT_Z, 240)
guide_len = [0.0]
for a, b in zip(guide, guide[1:] + guide[:1]):
    guide_len.append(guide_len[-1] + (b - a).length)
round_len = guide_len[-1]

def along(s):
    """The skin's point at the belt's height s along the waist from the front's middle, and its level outward way."""
    s %= round_len
    i = next(i for i in range(1, len(guide_len)) if guide_len[i] >= s)
    f = (s - guide_len[i - 1]) / max(1e-6, guide_len[i] - guide_len[i - 1])
    a, b = guide[i - 1], guide[i % len(guide)]
    p = a.lerp(b, f)
    t = b - a
    out = Vector((t.y, -t.x, 0)).normalized()   # the guide goes round anticlockwise seen from above, so out is to its right
    if out.dot(Vector((p.x, p.y, 0))) < 0:
        out = -out
    return p, out

def needs(s, bottom, top, clear):
    """At s along the waist: the guide's point, its level outward way, and how far out from that point a pouch's inside
    must be at each of a few heights, bottom to top, to clear the skin by clear (height taken from BELT_Z)."""
    p, out = along(s)
    need = []
    for j in range(7):
        z = bottom + (top - bottom) * j / 6
        start = Vector((p.x, p.y, z)) + out * 6.0
        hit = tree.ray_cast(start, -out, 12.0)[0]
        if hit is not None:
            need.append((z - BELT_Z, (hit - Vector((p.x, p.y, z))).dot(out) + clear))
    return p, out, need or [(0.0, clear)]

def lean(need):
    """The lean (out per unit up) of the line leaning least off what the skin asks for, at most TILT either way."""
    best = None
    for b in {0.0} | {(r1 - r0) / (z1 - z0) for z0, r0 in need for z1, r1 in need if z1 > z0}:
        b = max(-TILT, min(TILT, b))
        gap = sum(max(r - b * z for z, r in need) + b * z - r for z, r in need)
        if best is None or gap < best[0]:
            best = (gap, b)
    return best[1]

def columns(at, bottom, top, clear):
    """A pouch's columns at the places at along the waist, each (point, out, a, b): its inside a + b * (z - BELT_Z) out
    from the point at height z. The whole pouch takes its middle column's lean, the line leaning least off the skin under
    it, so it leans with the belly or the back rather than standing off it where the body bulges out below, but its sides
    stay upright rather than flaring out over the hips; each column is then pushed out until its whole height clears."""
    found = [needs(s, bottom, top, clear) for s in at]
    b = lean(found[len(found) // 2][2])
    return [(p, out, max(r - b * z for z, r in need), b) for p, out, need in found]

def inside_at(col, z):
    p, out, a, b = col
    return Vector((p.x, p.y, z)) + out * (a + b * (z - BELT_Z))

def add_block(cols, bottom, top, depth, rounding, part):
    """A box curved round the waist along the columns, bottom to top, from their insides out by depth; its bottom
    corners raised by up to rounding over the outer columns."""
    rings = []
    for k, col in enumerate(cols):
        out = col[1]
        x = abs(2 * k / (len(cols) - 1) - 1)                # 0 in the middle, 1 at either end
        lift = rounding * (1 - math.sqrt(max(0.0, 1 - max(0.0, (x - 0.55) / 0.45) ** 2)))
        b = bottom + lift
        rings.append([bm.verts.new(inside_at(col, z) + out * d) for d, z in ((0, b), (depth, b), (depth, top), (0, top))])
    for k in range(len(rings) - 1):
        a, c = rings[k], rings[k + 1]
        for j in range(4):
            add_face([a[j], c[j], c[(j + 1) % 4], a[(j + 1) % 4]], part)
    add_face(rings[0], part)
    add_face(list(reversed(rings[-1])), part)

for at, wide, bottom, top, depth, rounding, tab in POUCHES:
    mid = round_len / 2 if at == 'back' else at
    n = max(2, math.ceil(wide / COLUMNS) + 1)
    cols = columns([mid + wide * (k / (n - 1) - 0.5) for k in range(n)], bottom, top, CLEAR)
    add_block(cols, bottom, top, depth, rounding, 'pouch')
    # The flap: over the pouch's top and a little past its front and sides, on the pouch's own lines, its ends moved out
    # along the waist.
    ends = [along(mid - wide / 2 - FLAP_OUT), along(mid + wide / 2 + FLAP_OUT)]
    flap = [(ends[0][0], ends[0][1]) + cols[0][2:]] + cols[1:-1] + [(ends[1][0], ends[1][1]) + cols[-1][2:]]
    add_block(flap, top - FLAP, top + FLAP_OUT, depth + FLAP_OUT, 0.0, 'pouch')
    if tab:
        col = cols[n // 2] if n % 2 else columns([mid - wide / (n - 1) / 2, mid, mid + wide / (n - 1) / 2], bottom, top, CLEAR)[1]
        out = col[1]
        across = Vector((0, 0, 1)).cross(out)
        z0, z1 = top - FLAP - TAB[1] * 0.4, top - FLAP + TAB[1] * 0.6
        vs = [bm.verts.new(inside_at(col, z) + out * (depth + FLAP_OUT + d) + across * (e * TAB[0] / 2))
              for e, d, z in ((-1, 0, z0), (1, 0, z0), (1, TAB[2], z0), (-1, TAB[2], z0),
                              (-1, 0, z1), (1, 0, z1), (1, TAB[2], z1), (-1, TAB[2], z1))]
        for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
            add_face([vs[i] for i in q], 'tab')

bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
for f in bm.faces:
    u = (SQUARES.index(part_of.get(f, 'strap')) + 0.5) / len(SQUARES)
    for l in f.loops:
        l[uv_layer].uv = (u, 0.5)

mesh = bpy.data.meshes.new(NAME)
bm.to_mesh(mesh)
bm.free()
for poly in mesh.polygons:
    poly.use_smooth = False

img = bpy.data.images.new(NAME, CELL * len(SQUARES), CELL, alpha=False)
px = []
for y in range(CELL):
    for x in range(CELL * len(SQUARES)):
        r, g, b = COLOURS[SQUARES[x // CELL]]
        px += [r, g, b, 1.0]
img.pixels = px
img.filepath_raw = os.path.splitext(out_glb)[0] + '.png'
img.file_format = 'PNG'
img.pack()
mat = bpy.data.materials.new(NAME)
mat.use_nodes = True
bsdf = mat.node_tree.nodes['Principled BSDF']
tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
tex.image = img
tex.interpolation = 'Closest'
mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
mesh.materials.append(mat)

o = bpy.data.objects.new(NAME, mesh)
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

print('TACVEST: %d vertices, %d faces, %d triangles' % (len(mesh.vertices), len(mesh.polygons),
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
print('TACVEST -> %s' % out_glb)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
