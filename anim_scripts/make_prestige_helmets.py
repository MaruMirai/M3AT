"""Makes the Royalty prestige helmets from the plain ones, after the game's PrestigeMarineHelmet, PrestigeReconHelmet and
PrestigeCataphractHelmet sprites: each plain helmet's .glb in Models/garments as it is, its texture too, with gold added.

    blender -b --python anim_scripts/make_prestige_helmets.py -- <out folder> [marine] [recon] [cataphract]

Written to Models/garments it gives the garments ArmorMarineHelmetPrestige, ArmorHelmetReconPrestige and
ArmorHelmetCataphractPrestige, each drawn for the apparel of its own name (export_frames.py's default_defs and
GARMENT_PROPS). Re-run it after a plain helmet changes. With no helmet named it makes all three.

  marine: a transverse crest (a Roman centurion's), a fan of gold stood across the top from one side to the other, its
    inner edge on the helmet (cast out from the dome's middle, DOME, sunk SINK in) and its outer edge CREST_OUT further
    out, from CREST_FROM to 180 - CREST_FROM degrees round, CREST_THICK thick front to back and leant CREST_LEAN forward,
    with a darker band along its middle (CREST_BANDS) as the sprite's bristles have.
  recon: a gold wing on each side (Hermes'), a flat plate (WING, an outline in cells from the rivet, back and up) bolted on
    at the ear (WING_ROOT) and swept back and up past the helmet's top, leant out WING_LEAN at its tip, with a dark rivet.
  cataphract: the front plate and a band over the top gilded (GILD), and a gold fin along the top (FIN: its foot, top and
    height from the front back), swept forward into a hook at the brow as the sprite's is, wider at its top (FIN_HALF).

The added parts are textured from a few flat swatches (SWATCHES) painted into a corner of the helmet's image that no
triangle uses, so they stay one garment with one image. The gilding is painted on the image itself, each texel's place
found as prestige_paint.py finds it (bake), under the texture's own shading, the darkest lines (the face slit) left as
they are. Sizes are in Blender units at the rig's scale (one cell is about 17.6; the helmets are about 10.4 wide), x to
the figure's left, y back, z up, as the .glb files are. The added parts take the body's weights at export as the rest
of the helmet does: they are all over the head."""
import bpy, bmesh, sys, os, math
import numpy as np
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'anim_scripts'))
import prestige_paint as pp

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_prestige_helmets.py: give an out folder')
OUT = os.path.abspath(argv[0])
WANTED = argv[1:] or ['marine', 'recon', 'cataphract']

HELMETS = {   # source .glb in Models/garments, the garment it makes
    'marine': ('powerArmorHelmet.glb', 'ArmorMarineHelmetPrestige'),
    'recon': ('ArmorHelmetRecon.glb', 'ArmorHelmetReconPrestige'),
    'cataphract': ('ArmorHelmetCataphract.glb', 'ArmorHelmetCataphractPrestige'),
}

DOME = Vector((0.0, 0.0, 25.5))   # the helmets' dome's middle (they are about 5.2 round it, the top at 30.7)
SINK = 0.35                       # how far an added part goes into the helmet

CREST_FROM = 28.0     # degrees above the side the crest starts and ends at
CREST_OUT = (3.6, 2.6)   # its depth out from the helmet at the top and at its ends
CREST_THICK = 1.5     # front to back
CREST_LEAN = 8.0      # degrees its top leans forward
CREST_SEGMENTS = 24
CREST_BANDS = [(0.0, 'gold'), (0.38, 'gold_dark'), (0.62, 'gold')]   # from its inner edge out: the swatch from each share

WING_ROOT = Vector((5.0, 0.3, 25.0))   # the rivet, on the left side (x mirrored for the right)
WING_THICK = 0.4
WING_LEAN = 4.0       # degrees out at the top
# The wing's outline from the rivet: (back, up), the leading edge from the root to the tip, then the feathered trailing
# edge back to the root.
WING = [(-0.6, -0.4), (-0.4, 0.8), (1.6, 2.9), (4.2, 4.7), (7.0, 6.2), (5.9, 4.6), (6.7, 4.3), (4.9, 3.0), (5.5, 2.4),
        (3.7, 1.4), (4.1, 0.6), (2.0, -0.4), (0.4, -0.9)]
RIVET = (0.75, 0.35, 8)   # radius, how far it stands off the wing, points round

# The fin along the top: (y of its foot, y of its top, height over the helmet), from the front (-y) back; the tops swept
# forward of the feet at the front, into the hook over the brow.
FIN = [(-4.0, -4.0, 0.0), (-3.2, -4.9, 2.6), (-2.2, -4.1, 2.7), (-1.0, -2.6, 2.3), (0.5, -0.6, 1.9), (2.0, 1.3, 1.2),
       (3.4, 3.4, 0.0)]
FIN_HALF = (0.3, 0.55)
# Gilding on the cataphract: texels on its front (facing forward more than FRONT_FACING) within FRONT_HALF of the
# middle and forward of FRONT_BACK (else the inside of the rim at the back, facing forward, is gilded too), and on its top
# (facing up more than TOP_FACING) within TOP_HALF of the middle and forward of TOP_BACK.
GILD = {'front_facing': 0.25, 'front_half': 3.1, 'front_back': -1.0, 'top_facing': 0.45, 'top_half': 2.2, 'top_back': 1.5,
        'soft': 0.25, 'keep_dark': 0.22}

SWATCHES = {'gold': (0.80, 0.62, 0.27), 'gold_dark': (0.58, 0.42, 0.15), 'gold_light': (0.93, 0.78, 0.42),
            'rivet': (0.30, 0.30, 0.32)}
CELL = 48   # pixels a swatch, at the image's own size (2048, so 24 at the exported 1024)


def base_colour_image(materials):
    for m in materials:
        if m and m.node_tree:
            for n in m.node_tree.nodes:
                if n.type == 'BSDF_PRINCIPLED':
                    for link in n.inputs['Base Color'].links:
                        if link.from_node.type == 'TEX_IMAGE':
                            return link.from_node.image
    return None


def triangles(obj):
    """The mesh's triangles: places (object space), texture places, as arrays for prestige_paint.bake."""
    me = obj.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    V, T, F = [], [], []
    for t in me.loop_triangles:
        f = []
        for li in t.loops:
            V.append(tuple(me.vertices[me.loops[li].vertex_index].co))
            T.append(tuple(uv[li].uv))
            f.append(len(V) - 1)
        F.append(f)
    return np.array(V), np.array(T), np.array(F)


def free_cells(T, F, size, count):
    """count squares of CELL pixels that no triangle's texture places come near, as their middles (texture places)."""
    # The texels the triangles cover, at an eighth of the image's size (bake's bleed of a texel and a half round each
    # triangle is then 12 of the image's pixels, room for the game's texture filtering).
    coarse = size // 8
    P, _ = pp.bake(np.c_[T, np.zeros(len(T))], T, F, coarse)   # flat places: bake skips a triangle with no area
    covered = ~np.isnan(P[..., 0])[::-1]   # rows from the bottom, as the texture places run
    k = CELL // 8
    n = coarse // k
    used = covered[:n * k, :n * k].reshape(n, k, n, k).any(axis=(1, 3))
    spots = [(i, j) for i in range(1, n - 1) for j in range(1, n - 1) if not used[i, j]]
    if len(spots) < count:
        sys.exit('make_prestige_helmets.py: only %d free swatch cells in the image' % len(spots))
    return [((j + 0.5) / n, (i + 0.5) / n) for i, j in spots[:count]]


def paint_swatches(img, cells):
    w, h = img.size
    px = np.array(img.pixels[:]).reshape(h, w, 4)
    uvs = {}
    for (name, rgb), (u, v) in zip(SWATCHES.items(), cells):
        cx, cy = int(u * w), int(v * h)
        half = int(CELL * w / 2048 / 2) or 1
        px[cy - half:cy + half, cx - half:cx + half, :3] = rgb
        px[cy - half:cy + half, cx - half:cx + half, 3] = 1.0
        uvs[name] = Vector((u, v))
    img.pixels[:] = px.ravel()
    return uvs


def gild(obj, img):
    """Paints the cataphract helmet's front plate and top band gold, under its own shading."""
    V, T, F = triangles(obj)
    w, h = img.size
    P, N = pp.bake(V, T, F, w)            # rows from the top of the image
    P, N = P[::-1], N[::-1]               # as Blender's pixels run, from the bottom
    ok = ~np.isnan(P[..., 0])
    X, Y, Z = (np.where(ok, P[..., k], 0) for k in range(3))
    NY, NZ = np.where(ok, N[..., 1], 0), np.where(ok, N[..., 2], 0)
    g = GILD
    soft = g['soft']
    front = (np.clip((g['front_half'] - np.abs(X)) / soft + 0.5, 0, 1) * np.clip((-NY - g['front_facing']) / 0.1, 0, 1)
             * np.clip((g['front_back'] - Y) / soft + 0.5, 0, 1))
    top = (np.clip((g['top_half'] - np.abs(X)) / soft + 0.5, 0, 1) * np.clip((NZ - g['top_facing']) / 0.1, 0, 1)
           * np.clip((g['top_back'] - Y) / soft + 0.5, 0, 1))
    share = np.where(ok, np.maximum(front, top), 0)
    px = np.array(img.pixels[:]).reshape(h, w, 4)
    rgb = px[..., :3]
    lum = (rgb @ [0.299, 0.587, 0.114])[..., None]
    tone = pp.GOLD_LOW + (pp.GOLD - pp.GOLD_LOW) * np.clip((lum - 0.15) / 0.7, 0, 1) \
        + (pp.GOLD_HIGH - pp.GOLD) * 0.55 * np.clip((lum - 0.93) / 0.07, 0, 1)
    share = share * np.clip((lum[..., 0] - g['keep_dark']) / 0.08, 0, 1)   # the face slit stays dark
    px[..., :3] = rgb * (1 - share[..., None]) + np.clip(tone, 0, 1) * share[..., None]
    img.pixels[:] = px.ravel()
    print('PRESTIGE gilded %d texels' % int((share > 0.5).sum()))


class Parts:
    """The added solids, built in a bmesh of their own with a swatch per face."""
    def __init__(self):
        self.bm = bmesh.new()
        self.swatch = {}

    def face(self, verts, swatch):
        f = self.bm.faces.new(verts)
        self.swatch[f] = swatch
        return f

    def slab(self, rows, offset, swatch_of):
        """A closed solid from a grid of points (rows across, columns along) moved by offset(i, j) to either side; the
        front and back faces of cell (i, j) take swatch_of(i, j), the rim the outermost row's or the cell's."""
        front = [[self.bm.verts.new(p + offset(i, j)) for j, p in enumerate(r)] for i, r in enumerate(rows)]
        back = [[self.bm.verts.new(p - offset(i, j)) for j, p in enumerate(r)] for i, r in enumerate(rows)]
        n, m = len(rows), len(rows[0])
        for i in range(n - 1):
            for j in range(m - 1):
                s = swatch_of(i, j)
                self.face([front[i][j], front[i][j + 1], front[i + 1][j + 1], front[i + 1][j]], s)
                self.face([back[i][j], back[i + 1][j], back[i + 1][j + 1], back[i][j + 1]], s)
        ring = [(0, j) for j in range(m)] + [(i, m - 1) for i in range(1, n)] + \
               [(n - 1, j) for j in range(m - 2, -1, -1)] + [(i, 0) for i in range(n - 2, 0, -1)]
        for k in range(len(ring)):
            a, b = ring[k], ring[(k + 1) % len(ring)]
            s = swatch_of(min(a[0], b[0], n - 2), min(a[1], b[1], m - 2))
            self.face([front[a[0]][a[1]], back[a[0]][a[1]], back[b[0]][b[1]], front[b[0]][b[1]]], s)

    def prism(self, outline, way, half, swatch):
        """A flat plate: the outline (points in a plane) moved half along way to either side, its ends n-gons."""
        front = [self.bm.verts.new(p + way * half) for p in outline]
        back = [self.bm.verts.new(p - way * half) for p in outline]
        self.face(front, swatch)
        self.face(back[::-1], swatch)
        for k in range(len(outline)):
            j = (k + 1) % len(outline)
            self.face([front[k], back[k], back[j], front[j]], swatch)

    def finish(self):
        """Faces turned out, n-gons cut into triangles (the swatch kept)."""
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        result = bmesh.ops.triangulate(self.bm, faces=list(self.bm.faces), ngon_method='EAR_CLIP')
        for new, old in result['face_map'].items():
            self.swatch[new] = self.swatch.get(old, 'gold')


def surface_from_dome(tree, way):
    """The distance from the dome's middle out along way to the helmet's outside."""
    hit = tree.ray_cast(DOME + way * 30, -way, 60)[0]
    return (hit - DOME).length if hit is not None else 5.2


def crest(parts, tree):
    lean = Matrix.Rotation(math.radians(CREST_LEAN), 3, 'X')   # the top toward -y, the front
    rows = []
    band_at = [b for b, _ in CREST_BANDS] + [1.0]
    for share in band_at:
        row = []
        for k in range(CREST_SEGMENTS + 1):
            a = math.radians(CREST_FROM + (180 - 2 * CREST_FROM) * k / CREST_SEGMENTS)
            way = lean @ Vector((math.cos(a), 0, math.sin(a)))
            inner = surface_from_dome(tree, way) - SINK
            up = math.sin(a)   # 1 at the top, less toward the ends
            depth = CREST_OUT[1] + (CREST_OUT[0] - CREST_OUT[1]) * (up - math.sin(math.radians(CREST_FROM))) \
                / (1 - math.sin(math.radians(CREST_FROM)))
            row.append(DOME + way * (inner + depth * share))
        rows.append(row)
    thick = lean @ Vector((0, CREST_THICK / 2, 0))
    parts.slab(rows, lambda i, j: thick, lambda i, j: CREST_BANDS[min(i, len(CREST_BANDS) - 1)][1])
    print('PRESTIGE crest: top %.2f over the dome, ends %.2f out' % (rows[-1][CREST_SEGMENTS // 2].z, rows[-1][0].x))


def wings(parts):
    for sign in (1, -1):
        lean = Matrix.Rotation(math.radians(WING_LEAN) * sign, 3, 'Y')   # the top out from the head
        root = Vector((WING_ROOT.x * sign, WING_ROOT.y, WING_ROOT.z))
        outline = [root + lean @ Vector((0, back, up)) for back, up in WING]
        way = lean @ Vector((1, 0, 0))
        parts.prism(outline, way, WING_THICK / 2, 'gold')
        r, stand, n = RIVET
        disc = [root + way * (WING_THICK / 2 * sign + stand / 2 * sign) +
                lean @ Vector((0, r * math.cos(2 * math.pi * k / n), r * math.sin(2 * math.pi * k / n))) for k in range(n)]
        parts.prism(disc, way, stand / 2, 'rivet')


def fin(parts, tree):
    pts = []
    for foot, top, height in FIN:
        hit = tree.ray_cast(Vector((0, foot, 60)), Vector((0, 0, -1)), 80)[0]
        base = (hit.z if hit is not None else 28.0) - SINK
        pts.append((Vector((0, foot, base)), Vector((0, top, base + height + SINK))))
    rows = [[b for b, _ in pts], [t for _, t in pts]]
    parts.slab(rows, lambda i, j: Vector((FIN_HALF[i], 0, 0)), lambda i, j: 'gold')


def make(kind):
    source, name = HELMETS[kind]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(ROOT, 'Models', 'garments', source))
    meshes = [o for o in bpy.data.objects if o.type == 'MESH']
    assert len(meshes) == 1, 'one mesh expected in %s' % source
    helmet = meshes[0]
    for o in [o for o in bpy.data.objects if o is not helmet]:
        bpy.data.objects.remove(o)
    if helmet.parent is not None:
        world = helmet.matrix_world.copy()
        helmet.parent = None
        helmet.matrix_world = world
    assert helmet.matrix_world == Matrix.Identity(4), '%s is not at the origin' % source
    helmet.name = name
    helmet.data.name = name

    img = base_colour_image(helmet.data.materials)
    if img is None:
        sys.exit('make_prestige_helmets.py: %s has no Base Color image' % source)
    img = img.copy()   # its own, so the plain helmet's is untouched
    img.name = name
    for m in helmet.data.materials:
        for n in m.node_tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image and n.image.name != name:
                if n.outputs['Color'].links and any(l.to_socket.name == 'Base Color' for l in n.outputs['Color'].links):
                    n.image = img

    V, T, F = triangles(helmet)
    cells = free_cells(T, F, img.size[0], len(SWATCHES))
    if kind == 'cataphract':
        gild(helmet, img)
    uvs = paint_swatches(img, cells)

    welded = bmesh.new()
    welded.from_mesh(helmet.data)
    bmesh.ops.remove_doubles(welded, verts=welded.verts, dist=1e-4)
    tree = BVHTree.FromBMesh(welded)
    parts = Parts()
    if kind == 'marine':
        crest(parts, tree)
    elif kind == 'recon':
        wings(parts)
    else:
        fin(parts, tree)
    parts.finish()
    welded.free()

    whole = bmesh.new()
    whole.from_mesh(helmet.data)
    uv_layer = whole.loops.layers.uv.active
    mapping = {v: whole.verts.new(v.co) for v in parts.bm.verts}
    added = 0
    for f in parts.bm.faces:
        nf = whole.faces.new([mapping[v] for v in f.verts])
        nf.smooth = False
        nf.material_index = 0   # the helmet's own material, not a visor
        for l in nf.loops:
            l[uv_layer].uv = uvs[parts.swatch.get(f, 'gold')]
        added += 1
    whole.to_mesh(helmet.data)
    whole.free()
    parts.bm.free()
    img.pack()

    out = os.path.join(OUT, name + '.glb')
    os.makedirs(OUT, exist_ok=True)
    bpy.ops.object.select_all(action='DESELECT')
    helmet.select_set(True)
    bpy.context.view_layer.objects.active = helmet
    bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_skins=False,
                              export_animations=False, export_morph=False, export_image_format='AUTO')
    print('PRESTIGE %s: %d triangles added, swatches at %s -> %s'
          % (name, added, ', '.join('%s %.3f %.3f' % (k, v.x, v.y) for k, v in uvs.items()), out))


for kind in WANTED:
    make(kind)
