"""Makes Biotech's two mechanitor helmets from the recon helmet, after the game's MechcommanderHelmet and MechlordHelmet
sprites: Models/garments/ArmorHelmetRecon.glb as it is, its texture and glass visor too, with parts added. In the game
both are children of the recon helmet (ApparelArmorHelmetReconBase), so they keep its shape.

    blender -b --python anim_scripts/make_mech_helmets.py -- <out folder> [commander] [lord]

Written to Models/garments it gives the garments ArmorHelmetMechCommander and ArmorHelmetMechlordHelmet, each drawn for
the apparel of its own name (export_frames.py's default_defs; their GARMENT_PROPS are the recon helmet's). Re-run it
after the recon helmet changes. With no helmet named it makes both.

  commander: a round pod over each ear (POD: a plate rim with a dark middle), and from each a dark signal fin (FIN, an
    outline in cells from the pod's middle, back and up) standing straight up past the helmet's top, leant back FIN_LEAN
    and out FIN_SPLAY, as the sprite's two uprights are.
  lord: a fan of ribs behind the head (FAN: part of a cone opening backward from the dome's middle, DOME, its rays
    FAN_OPEN off the straight back way, raised FAN_RAISE, from FAN_FROM to 180 - FAN_FROM degrees round, so it is a halo
    over and round the head from the front, a spread of rays sweeping back from the side and a dish from behind, as the
    sprite's three views are; its inner edge on the helmet, sunk SINK in, its outer edge FAN_OUT further out), its ribs plate-coloured
    with dark between (FAN_BANDS, a rib every FAN_RIB columns), and a dark chevron on the brow over the visor (CHEVRON,
    its middle line across the front, x and z, sunk CHEVRON_SINK into the helmet).

The added parts are textured from a few flat swatches (SWATCHES; 'plate' is measured from the helmet's own image, the
median of the texels its triangles use, so they match it) painted into a corner of the helmet's image that no triangle
uses, so each stays one garment with one image. Sizes are in Blender units at the rig's scale (one cell is about 17.6;
the helmet is about 10.4 wide), x to the figure's left, y back, z up, as the .glb files are. The added parts take the
body's weights at export as the rest of the helmet does: they are all over the head. The helpers are the same as
make_prestige_helmets.py's, which does this for the prestige helmets."""
import bpy, bmesh, sys, os, math
import numpy as np
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'anim_scripts'))
import prestige_paint as pp

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_mech_helmets.py: give an out folder')
OUT = os.path.abspath(argv[0])
WANTED = argv[1:] or ['commander', 'lord']

SOURCE = 'ArmorHelmetRecon.glb'
NAMES = {'commander': 'ArmorHelmetMechCommander', 'lord': 'ArmorHelmetMechlordHelmet'}

DOME = Vector((0.0, 0.0, 25.5))   # the helmet's dome's middle (it is about 5.2 round it, the top at 30.8)
SINK = 0.35                       # how far an added part goes into the helmet

# The commander's ear pods: their middle (left side, x is found on the helmet), radius, thickness, the dark middle's
# radius and how far it stands proud, points round.
POD = {'y': 0.6, 'z': 23.8, 'radius': 1.7, 'thick': 0.8, 'inner': 1.0, 'proud': 0.15, 'points': 16}
# The fin's outline from the pod's middle: (back, up), round from the front of its foot; it stands FIN_OFF out from the
# helmet's side at the pod, FIN_THICK thick.
FIN = [(-1.1, 0.0), (-1.05, 4.4), (-0.85, 6.4), (-0.4, 7.1), (0.2, 7.2), (0.75, 6.8), (1.05, 5.6), (1.15, 0.0),
       (0.0, -1.1)]
FIN_THICK = 1.1
FIN_OFF = 0.5
FIN_LEAN = 4.0        # degrees its top leans back
FIN_SPLAY = 2.0       # degrees its top leans out from the head

FAN_FROM = 12.0       # degrees above the side the fan starts and ends at
FAN_OPEN = 58.0       # degrees each ray is off the cone's axis, the straight back way
FAN_RAISE = 12.0      # degrees the axis is raised from level
FAN_OUT = (4.0, 2.6)  # its depth out from the helmet at the top and at its ends
FAN_THICK = 0.6
FAN_SEGMENTS = 21     # columns round; with FAN_RIB 3 that is seven ribs, one at the top
FAN_RIB = 3
FAN_BANDS = [0.0, 0.16, 0.84]   # from its inner edge out: an inner rim, the ribs, an outer rim
# The chevron's middle line across the brow, (x, z), and its half width; a slab CHEVRON_THICK thick laid on the helmet's
# front, cast along y onto it.
CHEVRON = [(-2.5, 28.3), (-1.25, 27.35), (0.0, 26.35), (1.25, 27.35), (2.5, 28.3)]
CHEVRON_SPLIT = 3     # points on each stretch of the line, so it follows the helmet's curve
CHEVRON_HALF = 0.42
CHEVRON_THICK = 0.4
CHEVRON_SINK = 0.15   # how far its inner face goes into the helmet (the rest stands out)

SWATCHES = {'plate': None, 'dark': (0.27, 0.28, 0.30), 'mid': (0.48, 0.49, 0.51)}
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


def triangles(obj, material=None):
    """The mesh's triangles (only those on that material, if given): places (object space), texture places, as arrays
    for prestige_paint.bake."""
    me = obj.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    V, T, F = [], [], []
    for t in me.loop_triangles:
        if material is not None and t.material_index != material:
            continue
        f = []
        for li in t.loops:
            V.append(tuple(me.vertices[me.loops[li].vertex_index].co))
            T.append(tuple(uv[li].uv))
            f.append(len(V) - 1)
        F.append(f)
    return np.array(V), np.array(T), np.array(F)


def coverage(T, F, size):
    """Which texels of a size by size image the triangles' texture places cover, rows from the bottom."""
    P, _ = pp.bake(np.c_[T, np.zeros(len(T))], T, F, size)   # flat places: bake skips a triangle with no area
    return ~np.isnan(P[..., 0])[::-1]


def free_cells(T, F, size, count):
    """count squares of CELL pixels that no triangle's texture places come near, as their middles (texture places)."""
    # At an eighth of the image's size, bake's bleed of a texel and a half round each triangle is 12 of the image's
    # pixels, room for the game's texture filtering.
    coarse = size // 8
    covered = coverage(T, F, coarse)
    k = CELL // 8
    n = coarse // k
    used = covered[:n * k, :n * k].reshape(n, k, n, k).any(axis=(1, 3))
    spots = [(i, j) for i in range(1, n - 1) for j in range(1, n - 1) if not used[i, j]]
    if len(spots) < count:
        sys.exit('make_mech_helmets.py: only %d free swatch cells in the image' % len(spots))
    return [((j + 0.5) / n, (i + 0.5) / n) for i, j in spots[:count]]


def plate_colour(img, T, F):
    """The median colour of the texels the helmet's shell uses: its plates."""
    w, h = img.size
    px = np.array(img.pixels[:]).reshape(h, w, 4)
    step = 4
    covered = coverage(T, F, w // step)
    sample = px[::step, ::step][:covered.shape[0], :covered.shape[1]][covered]
    return tuple(float(c) for c in np.median(sample[:, :3], axis=0))


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
            self.swatch[new] = self.swatch.get(old, 'plate')


def surface_from_dome(tree, way):
    """The distance from the dome's middle out along way to the helmet's outside."""
    hit = tree.ray_cast(DOME + way * 30, -way, 60)[0]
    return (hit - DOME).length if hit is not None else 5.2


def side_x(tree, y, z):
    """How far out the helmet's left side is at (y, z)."""
    hit = tree.ray_cast(Vector((30, y, z)), Vector((-1, 0, 0)), 60)[0]
    return hit.x if hit is not None else 5.0


def commander(parts, tree):
    for sign in (1, -1):
        side = side_x(tree, POD['y'], POD['z'])
        way = Vector((sign, 0, 0))
        n = POD['points']
        ring = lambda r, x: [Vector((x * sign, POD['y'] + r * math.cos(2 * math.pi * k / n),
                                     POD['z'] + r * math.sin(2 * math.pi * k / n))) for k in range(n)]
        pod_mid = side - SINK + POD['thick'] / 2
        parts.prism(ring(POD['radius'], pod_mid), way, POD['thick'] / 2, 'plate')
        dark_mid = side - SINK + POD['thick'] + POD['proud'] / 2 - 0.02
        parts.prism(ring(POD['inner'], dark_mid), way, POD['proud'] / 2 + 0.02, 'dark')

        tilt = Matrix.Rotation(math.radians(FIN_SPLAY) * sign, 3, 'Y') @ Matrix.Rotation(math.radians(-FIN_LEAN), 3, 'X')
        foot = Vector((sign * (side + FIN_OFF), POD['y'], POD['z']))
        outline = [foot + tilt @ Vector((0, back, up)) for back, up in FIN]
        parts.prism(outline, tilt @ way, FIN_THICK / 2, 'dark')
        top = max(outline, key=lambda p: p.z)
        print('MECH fin: foot x %.2f, top z %.2f (helmet top 30.76), x %.2f there' % (foot.x, top.z, top.x))


def fan(parts, tree):
    raise_ = Matrix.Rotation(math.radians(-FAN_RAISE), 3, 'X')   # the back way (+y) turned up
    axis = raise_ @ Vector((0, 1, 0))
    up = raise_ @ Vector((0, 0, 1))
    side = Vector((1, 0, 0))
    opens, closes = math.sin(math.radians(FAN_OPEN)), math.cos(math.radians(FAN_OPEN))

    def ray(a):
        return axis * closes + (side * math.cos(a) + up * math.sin(a)) * opens

    def normal(a):   # square to the cone's surface there
        return -axis * opens + (side * math.cos(a) + up * math.sin(a)) * closes
    rows = []
    lowest = math.sin(math.radians(FAN_FROM))
    for share in FAN_BANDS + [1.0]:
        row = []
        for k in range(FAN_SEGMENTS + 1):
            a = math.radians(FAN_FROM + (180 - 2 * FAN_FROM) * k / FAN_SEGMENTS)
            way = ray(a)
            inner = surface_from_dome(tree, way) - SINK
            depth = FAN_OUT[1] + (FAN_OUT[0] - FAN_OUT[1]) * (math.sin(a) - lowest) / (1 - lowest)
            row.append(DOME + way * (inner + depth * share))
        rows.append(row)
    def thick(i, j):
        return normal(math.radians(FAN_FROM + (180 - 2 * FAN_FROM) * j / FAN_SEGMENTS)) * (FAN_THICK / 2)

    def swatch_of(i, j):
        if i == 1:   # the ribbed band
            return 'plate' if j % FAN_RIB == FAN_RIB // 2 else 'dark'
        return 'plate'
    parts.slab(rows, thick, swatch_of)
    print('MECH fan: top z %.2f y %.2f, ends x %.2f z %.2f'
          % (rows[-1][FAN_SEGMENTS // 2].z, rows[-1][FAN_SEGMENTS // 2].y, rows[-1][0].x, rows[-1][0].z))


def chevron(parts, tree):
    line = []
    for (x0, z0), (x1, z1) in zip(CHEVRON, CHEVRON[1:]):
        for s in range(CHEVRON_SPLIT):
            t = s / CHEVRON_SPLIT
            line.append(Vector((x0 + (x1 - x0) * t, 0, z0 + (z1 - z0) * t)))
    line.append(Vector((CHEVRON[-1][0], 0, CHEVRON[-1][1])))
    rows = [[], []]
    for k, p in enumerate(line):
        a, b = line[max(k - 1, 0)], line[min(k + 1, len(line) - 1)]
        along = (b - a).normalized()
        across = Vector((-along.z, 0, along.x))   # in the x-z plane, square to the line
        if across.z < 0:
            across = -across
        for i, edge in enumerate((p - across * CHEVRON_HALF, p + across * CHEVRON_HALF)):
            hit = tree.ray_cast(Vector((edge.x, -30, edge.z)), Vector((0, 1, 0)), 60)[0]
            y = hit.y if hit is not None else -5.0
            rows[i].append(Vector((edge.x, y + CHEVRON_SINK - CHEVRON_THICK / 2, edge.z)))
    parts.slab(rows, lambda i, j: Vector((0, CHEVRON_THICK / 2, 0)), lambda i, j: 'dark')


def make(kind):
    name = NAMES[kind]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(ROOT, 'Models', 'garments', SOURCE))
    meshes = [o for o in bpy.data.objects if o.type == 'MESH']
    assert len(meshes) == 1, 'one mesh expected in %s' % SOURCE
    helmet = meshes[0]
    for o in [o for o in bpy.data.objects if o is not helmet]:
        bpy.data.objects.remove(o)
    if helmet.parent is not None:
        world = helmet.matrix_world.copy()
        helmet.parent = None
        helmet.matrix_world = world
    assert helmet.matrix_world == Matrix.Identity(4), '%s is not at the origin' % SOURCE
    helmet.name = name
    helmet.data.name = name
    shell = next(i for i, m in enumerate(helmet.data.materials) if m.name != 'Visor')

    img = base_colour_image(helmet.data.materials)
    if img is None:
        sys.exit('make_mech_helmets.py: %s has no Base Color image' % SOURCE)
    img = img.copy()   # its own, so the recon helmet's is untouched
    img.name = name
    for m in helmet.data.materials:
        for n in m.node_tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image and n.image.name != name:
                if n.outputs['Color'].links and any(l.to_socket.name == 'Base Color' for l in n.outputs['Color'].links):
                    n.image = img

    V, T, F = triangles(helmet)
    cells = free_cells(T, F, img.size[0], len(SWATCHES))
    SWATCHES['plate'] = plate_colour(img, *triangles(helmet, shell)[1:])
    print('MECH plate colour %.2f %.2f %.2f' % SWATCHES['plate'])
    uvs = paint_swatches(img, cells)

    welded = bmesh.new()
    welded.from_mesh(helmet.data)
    bmesh.ops.remove_doubles(welded, verts=welded.verts, dist=1e-4)
    tree = BVHTree.FromBMesh(welded)
    parts = Parts()
    if kind == 'commander':
        commander(parts, tree)
    else:
        fan(parts, tree)
        chevron(parts, tree)
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
        nf.material_index = shell   # the helmet's own material, not the visor
        for l in nf.loops:
            l[uv_layer].uv = uvs[parts.swatch.get(f, 'plate')]
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
    print('MECH %s: %d triangles added -> %s' % (name, added, out))


for kind in WANTED:
    make(kind)
