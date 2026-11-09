"""Paints Combat Extended's backpack, CE_Backpack, a texture of its own: a buckled top flap, an outer pocket with webbing
rows, two straps over the top, a zipped pouch and a compression strap on each side, a quilted back panel, a reinforced
base and stitching, drawn by where each texel lies on the pack rather than by Meshy, whose retexture of this plain box
(text alone, on its own UVs or ours) only ever came back as bare canvas weave.

    blender -b --python anim_scripts/paint_backpack.py -- <in.glb> <out.glb> [image.png]

The mesh is welded, its triangles joined back into its flat faces, and each face unwrapped as one island (seams where
faces meet at more than SEAM_ANGLE), so the pack's places stay as they were. Every texel then gets its rest place on the
pack (Blender world units, x left of the wearer... across, y back, z up: the outer face is +y, the side on the wearer's
back -y) and the face's way out, and the design is a function of those, so it runs on across the edges between faces.
The image is greys about MIDDLE, as prompts.py's neutral() leaves a retexture, since the game multiplies it by the
apparel's colour. The garment is written unskinned with its image on Base Color, as the retexture's was.
"""
import math
import sys

import bmesh
import bpy
import numpy as np

SEAM_ANGLE = 25          # degrees between faces that makes a UV seam
SIZE = 1024              # the image written
SUPER = 2                # drawn at SUPER times that and scaled down, for smooth edges
MARGIN = 0.02            # of the UV square, between islands
PAD = 10                 # texels the colour is spread past each island's edge, for mipmaps

# Tones, of 255, before the apparel's colour
MIDDLE = 235             # the canvas
WEBBING = 168            # straps and webbing strips
BINDING = 176            # edge binding round flaps and pouches
STITCH = 150
SEAM = 200               # piping where faces meet
BASE = 212               # the reinforced bottom
METAL = 92               # buckle frames, the zip
PADDED = 242             # the quilted back panel

# The design, in the pack's own units (its outer face spans x -2.4..2.4 and z 14.8..18.8; y 1.35..3.57)
FLAP_HALF = 1.95         # the top flap's half width
FLAP_LOW = 17.3          # its lower edge on the outer face
FLAP_HINGE = 2.0         # y on the top face where it is sewn on
STRAP_X = 1.0            # the two flap straps' middles, either side
STRAP_HALF = 0.17
BUCKLE = (16.78, 17.16)  # where on the outer face the straps' buckles are
POCKET = (1.45, 14.95, 16.3)        # the outer pocket's half width, bottom and top
POCKET_FLAP = 15.85      # its flap's lower edge
MOLLE = ((15.2, 15.38), (15.5, 15.68))   # webbing rows across the pocket
MOLLE_TACK = 0.45        # between the tacks that make the loops
BASE_TOP = 15.12         # the reinforced bottom goes up to here all round
SIDE_POUCH = (1.75, 2.95, 15.2, 17.1)    # y from, y to, z from, z to, on each side face
SIDE_STRAP = 17.8        # the side compression strap's middle
PANEL = (1.75, 15.25, 19.15)             # the back panel's half width, bottom and top
QUILT = 0.7              # its diamond quilting's spacing


def args():
    a = sys.argv[sys.argv.index('--') + 1:]
    if len(a) < 2:
        sys.exit(__doc__)
    return a


def unwrap(o):
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.join_triangles(bm, faces=bm.faces, angle_face_threshold=math.radians(15),
                             angle_shape_threshold=math.radians(180))
    for e in bm.edges:
        e.seam = e.is_boundary or (len(e.link_faces) == 2 and e.calc_face_angle() > math.radians(SEAM_ANGLE))
    bm.to_mesh(o.data)
    bm.free()
    while o.data.uv_layers:
        o.data.uv_layers.remove(o.data.uv_layers[0])
    o.data.uv_layers.new(name='UVMap')
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.unwrap(method='CONFORMAL', margin=MARGIN)
    bpy.ops.uv.pack_islands(margin=MARGIN, rotate=True)
    bpy.ops.mesh.quads_convert_to_tris()
    bpy.ops.object.mode_set(mode='OBJECT')
    print('UNWRAPPED %d triangles, %d seams' % (len(o.data.polygons), sum(e.use_seam for e in o.data.edges)))


def rasterise(o, n):
    """Each texel's rest place and way out (NaN off the islands), and the units one texel spans."""
    M = o.matrix_world
    R = M.to_3x3()
    uv = o.data.uv_layers.active.data
    place = np.full((n, n, 3), np.nan)
    way = np.zeros((n, n, 3))
    spans = []
    for t in o.data.loop_triangles:
        q = np.array([uv[l].uv[:] for l in t.loops]) * n - 0.5
        p = np.array([(M @ o.data.vertices[v].co)[:] for v in t.vertices])
        w = np.array((R @ t.normal).normalized()[:])
        lo = np.floor(q.min(0)).astype(int).clip(0, n - 1)
        hi = np.ceil(q.max(0)).astype(int).clip(0, n - 1)
        ys, xs = np.mgrid[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
        a, b, c = q
        d = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])
        if abs(d) < 1e-12:
            continue
        l1 = ((xs - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (ys - a[1])) / d
        l2 = ((b[0] - a[0]) * (ys - a[1]) - (xs - a[0]) * (b[1] - a[1])) / d
        l0 = 1 - l1 - l2
        inside = (l0 >= -1e-3) & (l1 >= -1e-3) & (l2 >= -1e-3)
        bary = np.stack([l0, l1, l2], -1)[inside]
        place[ys[inside], xs[inside]] = bary @ p
        way[ys[inside], xs[inside]] = w
        spans.append(np.linalg.norm(p[1] - p[0]) / max(np.linalg.norm(q[1] - q[0]), 1e-9))
    return place, way, float(np.median(spans))


def rounded(x, y, x0, x1, y0, y1, r):
    """Signed distance to a rounded rectangle: below 0 inside."""
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    hx, hy = (x1 - x0) / 2 - r, (y1 - y0) / 2 - r
    dx, dy = np.abs(x - cx) - hx, np.abs(y - cy) - hy
    return np.hypot(np.maximum(dx, 0), np.maximum(dy, 0)) + np.minimum(np.maximum(dx, dy), 0) - r


def band(d, a, b):
    return (d >= a) & (d <= b)


def dashes(along, every=0.16, share=0.6):
    return np.mod(along, every) < every * share


def paint(place, way, span):
    x, y, z = place[..., 0], place[..., 1], place[..., 2]
    nx, ny, nz = way[..., 0], way[..., 1], way[..., 2]
    on = ~np.isnan(x)
    outer = on & (ny > 0.6)
    body = on & (ny < -0.6)
    top = on & ~outer & ~body & (nz > 0.6)
    bottom = on & ~outer & ~body & (nz < -0.6)
    side = on & ~outer & ~body & ~top & ~bottom
    ax = np.abs(x)
    tone = np.full(x.shape, float(MIDDLE))

    # Canvas weave, fine and faint
    tone += 3.0 * np.sin(x * 70) * np.sin(z * 70) + 2.0 * np.sin((y + z) * 55)
    # A little grime toward the bottom
    tone -= np.clip((16.0 - z) / 1.2, 0, 1) * 6

    # The reinforced base, all round
    tone = np.where(on & (z < BASE_TOP) | bottom, BASE, tone)
    tone = np.where((outer | side | body) & band(z, BASE_TOP + 0.05, BASE_TOP + 0.085) & dashes(x + y), STITCH, tone)

    # s runs up the outer face and on back over the top, so the flap and straps carry over it
    s = np.where(top, 19.6 + (3.4 - y), z)
    over = outer | top

    # The outer pocket, its webbing rows and its flap
    px, plo, phi = POCKET
    d = rounded(x, s, -px, px, plo, phi, 0.25)
    pocket = outer & (d < 0)
    tone = np.where(outer & band(d, -0.02, 0.08) & (s > BASE_TOP), BINDING - 10, tone)   # its gusset's shade
    tone = np.where(pocket & band(d, -0.14, -0.10) & dashes(x + s), STITCH, tone)
    for lo, hi in MOLLE:
        row = pocket & (s >= lo) & (s <= hi) & (ax < px - 0.1)
        tone = np.where(row, WEBBING + 4 * np.sin(s * 300), tone)
        tone = np.where(row & (np.abs(np.mod(x + MOLLE_TACK / 2, MOLLE_TACK) - MOLLE_TACK / 2) < 0.025), STITCH - 25, tone)
        tone = np.where(pocket & band(s, lo - 0.05, lo) & (ax < px - 0.1), tone * 0.9, tone)   # the loop's shadow
    df = rounded(x, s, -px - 0.04, px + 0.04, POCKET_FLAP, phi + 0.05, 0.22)
    tone = np.where(outer & band(df, 0, 0.12) & (s < POCKET_FLAP + 0.2), tone * 0.86, tone)   # its shadow below
    tone = np.where(outer & (df < 0), MIDDLE + 2, tone)
    tone = np.where(outer & band(df, -0.09, 0), BINDING, tone)
    tone = np.where(outer & band(df, -0.17, -0.13) & dashes(x + s), STITCH, tone)
    stud = np.hypot(x, s - (POCKET_FLAP + 0.17))
    tone = np.where(outer & (stud < 0.1), METAL + 40, tone)
    tone = np.where(outer & band(stud, 0.06, 0.1), METAL, tone)

    # The top flap, from its hinge on the top face down over the outer face
    hinge = 19.6 + (3.4 - FLAP_HINGE)
    d = rounded(x, s, -FLAP_HALF, FLAP_HALF, FLAP_LOW, hinge + 1, 0.4)
    tone = np.where(outer & band(d, 0, 0.2) & (s < FLAP_LOW + 0.3), tone * (0.8 + d), tone)   # its shadow below
    flap = over & (d < 0) & (s <= hinge)
    tone = np.where(flap, MIDDLE + 3 + 3.0 * np.sin(x * 70) * np.sin(s * 70), tone)
    tone = np.where(flap & band(d, -0.12, 0), BINDING, tone)
    tone = np.where(flap & band(d, -0.21, -0.17) & dashes(x + s), STITCH, tone)
    tone = np.where(top & band(s, hinge - 0.05, hinge) & (ax < FLAP_HALF), STITCH + 20, tone)   # the hinge seam

    # The straps down over the flap to their buckles, the tails through them
    for c in (-STRAP_X, STRAP_X):
        u = x - c
        strap = over & (np.abs(u) < STRAP_HALF) & (s > BUCKLE[0]) & (s < hinge + 0.3)
        tail = outer & (np.abs(u) < STRAP_HALF - 0.02) & (rounded(u, s, -STRAP_HALF, STRAP_HALF, BUCKLE[0] - 0.45, BUCKLE[0] + 0.1, STRAP_HALF - 0.02) < 0)
        tone = np.where(over & (np.abs(u) < STRAP_HALF + 0.06) & (np.abs(u) >= STRAP_HALF) & (s > BUCKLE[0] - 0.45) & (s < hinge + 0.3) & (u > 0), tone * 0.9, tone)
        tone = np.where(strap | tail, WEBBING + 5 * np.sin(s * 260), tone)
        tone = np.where((strap | tail) & (np.abs(np.abs(u) - (STRAP_HALF - 0.035)) < 0.012), WEBBING - 18, tone)
        bx = STRAP_HALF + 0.08
        frame = rounded(u, s, -bx, bx, BUCKLE[0], BUCKLE[1], 0.05)
        tone = np.where(outer & band(frame, -0.07, 0), METAL, tone)
        tone = np.where(outer & (np.abs(s - (BUCKLE[0] + BUCKLE[1]) / 2) < 0.03) & (np.abs(u) < bx), METAL + 15, tone)
        tone = np.where(outer & band(frame, 0, 0.05) & (u > 0), tone * 0.85, tone)

    # Each side: a zipped pouch and a compression strap across
    y0, y1, z0, z1 = SIDE_POUCH
    d = rounded(y, z, y0, y1, z0, z1, 0.22)
    tone = np.where(side & band(d, 0, 0.1), tone * 0.86, tone)
    tone = np.where(side & band(d, -0.1, 0), BINDING, tone)
    tone = np.where(side & band(d, -0.18, -0.14) & dashes(y + z), STITCH, tone)
    zy = (y0 + y1) / 2
    zip_ = side & (np.abs(y - zy) < 0.05) & (z > z0 + 0.2) & (z < z1 - 0.12)
    tone = np.where(zip_, METAL + 20, tone)
    tone = np.where(zip_ & (np.mod(z, 0.07) < 0.035), METAL, tone)
    tone = np.where(side & (rounded(y, z, zy - 0.08, zy + 0.08, z1 - 0.32, z1 - 0.06, 0.04) < 0), METAL - 10, tone)
    st = side & (np.abs(z - SIDE_STRAP) < 0.16)
    tone = np.where(side & band(z - SIDE_STRAP, -0.24, -0.16), tone * 0.88, tone)
    tone = np.where(st, WEBBING + 5 * np.sin(y * 260), tone)
    sb = rounded(y, z, 2.25, 2.6, SIDE_STRAP - 0.24, SIDE_STRAP + 0.24, 0.05)
    tone = np.where(side & band(sb, -0.06, 0), METAL, tone)

    # The back panel, padded and quilted, with the shoulder straps' anchors
    hx, blo, bhi = PANEL
    d = rounded(x, z, -hx, hx, blo, bhi, 0.35)
    panel = body & (d < 0)
    tone = np.where(panel, PADDED, tone)
    q1 = np.abs(np.mod((x + z) / QUILT, 1) - 0.5) > 0.47
    q2 = np.abs(np.mod((x - z) / QUILT, 1) - 0.5) > 0.47
    tone = np.where(panel & (q1 | q2), PADDED - 30, tone)
    tone = np.where(body & band(d, -0.06, 0.04), BINDING, tone)
    tone = np.where(body & band(d, -0.14, -0.1) & dashes(x + z), STITCH, tone)
    for c in (-0.8, 0.8):
        t0, t1 = bhi - 0.55, bhi + 0.15
        dt = rounded(x, z, c - 0.3, c + 0.3, t0, t1, 0.06)
        tab = body & (dt < 0)
        tone = np.where(tab, WEBBING - 10, tone)
        cross = np.abs(np.abs(x - c) / 0.22 - np.abs(z - (t0 + t1) / 2) / 0.27) < 0.08   # the box-X stitching
        tone = np.where(tab & ((band(dt, -0.1, -0.07)) | (cross & (dt < -0.08))), STITCH - 40, tone)
    for c in (-1.4, 1.4):
        tone = np.where(body & (rounded(x, z, c - 0.3, c + 0.3, blo - 0.1, blo + 0.3, 0.06) < 0), WEBBING - 10, tone)

    # The carry loop on top, by the wearer's back
    loop = top & (ax < 0.35) & (y < 1.95)
    tone = np.where(loop, WEBBING + 5 * np.sin(x * 260), tone)
    tone = np.where(top & (ax < 0.43) & (ax >= 0.35) & (y < 1.95), tone * 0.85, tone)
    return tone, on, flap


def edges(on, span):
    """Texels within reach of an island's edge, by repeated erosion, as the faces' piping."""
    reach = max(1, int(round(0.05 / span)))
    inner = on.copy()
    for _ in range(reach):
        e = inner.copy()
        e[1:] &= inner[:-1]
        e[:-1] &= inner[1:]
        e[:, 1:] &= inner[:, :-1]
        e[:, :-1] &= inner[:, 1:]
        inner = e
    return on & ~inner


def spread(tone, on, steps):
    """The colour carried out past the islands' edges."""
    tone = np.where(on, tone, 0.0)
    have = on.astype(float)
    for _ in range(steps):
        t = np.pad(tone, 1)
        h = np.pad(have, 1)
        total = t[:-2, 1:-1] + t[2:, 1:-1] + t[1:-1, :-2] + t[1:-1, 2:]
        count = h[:-2, 1:-1] + h[2:, 1:-1] + h[1:-1, :-2] + h[1:-1, 2:]
        new = (have == 0) & (count > 0)
        tone = np.where(new, total / np.maximum(count, 1), tone)
        have = np.where(new, 1.0, have)
    return np.where(have > 0, tone, MIDDLE)


def main():
    a = args()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=a[0], disable_bone_shape=True)
    o = next(x for x in bpy.data.objects if x.type == 'MESH')
    M = o.matrix_world.copy()
    o.parent = None
    o.matrix_world = M
    o.vertex_groups.clear()
    o.shape_key_clear()
    o.modifiers.clear()
    for other in list(bpy.data.objects):
        if other is not o:
            bpy.data.objects.remove(other)
    unwrap(o)
    o.data.calc_loop_triangles()
    n = SIZE * SUPER
    place, way, span = rasterise(o, n)
    tone, on, flap = paint(place, way, span)
    tone = np.where(edges(on, span) & ~flap, np.minimum(tone, SEAM), tone)   # not across the flap, which lies over its faces' edges
    tone = spread(tone, on, PAD * SUPER)
    tone = tone.reshape(SIZE, SUPER, SIZE, SUPER).mean((1, 3)).clip(0, 255)
    print('PAINTED %d texels, %.4f units each, median %d' % (on.sum() // SUPER ** 2, span * SUPER, np.median(tone[on[::SUPER, ::SUPER]])))

    img = bpy.data.images.new('CE_Backpack', SIZE, SIZE)
    g = tone / 255.0
    img.pixels = np.dstack([g, g, g, np.ones_like(g)]).ravel()   # rows from the bottom, as UV v is
    if len(a) > 2:
        img.filepath_raw = a[2]
        img.file_format = 'PNG'
        img.save()
    img.pack()
    mat = bpy.data.materials.new('CE_Backpack')
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes['Principled BSDF']
    node = mat.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = img
    mat.node_tree.links.new(node.outputs['Color'], bsdf.inputs['Base Color'])
    o.data.materials.clear()
    o.data.materials.append(mat)
    o.name = o.data.name = 'CE_Backpack'
    for x in bpy.context.scene.objects:
        x.select_set(x is o)
    bpy.context.view_layer.objects.active = o
    bpy.ops.export_scene.gltf(filepath=a[1], export_format='GLB', use_selection=True, export_animations=False,
                              export_extras=True, export_skins=False, export_morph=False, export_image_format='AUTO')
    print('WROTE', a[1])


main()
