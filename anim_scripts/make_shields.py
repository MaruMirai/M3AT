"""Makes Combat Extended's two shields, after its sprites (Textures/Things/Apparel/Shield/CE_Shield.png and
BallisticShield/CE_BallisticShield.png):

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_shields.py -- <out folder> [shield ...] [--blend out.blend]

CE_MeleeShield (CE_Apparel_MeleeShield): a round shield of planks, twelve-sided as the sprite is, dished (the face bowed
forward, DISH), with an iron rim round its edge and an iron boss over the grip in its middle; the planks a little
different in shade, with grooves between them where the sprite has its lines.

CE_BallisticShield (CE_Apparel_BallisticShield): a tall plate, curved round the body (CURVE), its top corners a little
rounded and its bottom ones more, with a viewport of glass across its upper part in a pale frame (the glass on a
second material, Visor, which export_frames.py writes as visor lines, drawn see-through in the game) and four bolt heads
below it.

Each has a handle behind it on two posts, which the left fist closes round. Both are built in the shield's own frame (the
grip's middle at the origin, x to the figure's left, y back, z up, the face toward -y) and taken to the rest pose by
walk.shield_rest(), so in the guard (walk.shield_at) they stand upright in front of the left shoulder, turned out to the
left; the handle runs along the grip's way there. Change the guard's numbers in walk.py, then run this again.

The shields in Models/garments/ have since been replaced by other meshes placed on the back by hand, which
export_frames.py's shield_held_at takes into the fist; running this over them would put back ones in the fist, which the
export would take for placed on the back. Write to another folder.

Sizes are in armature units (the figure 29.7 tall, 1.25 cells; one cell is about 23.6). Low-poly, one closed solid each,
with UVs and a small image of flat colours (COLOURS), pale where the game should tint them by the shield's stuff (all
of it is, as the apparel is made from stuff, so the iron is only a darker share of it). Written without a skin, as the
hats are: export_frames.py gives them hand.L whole (GARMENT_PROPS' bone). The .blend is never saved; --blend saves a
copy with them on the rig, to look at."""
import bpy, bmesh, sys, math, os
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
out_blend = None
if '--blend' in argv:
    i = argv.index('--blend')
    out_blend = os.path.abspath(argv[i + 1])
    argv = argv[:i] + argv[i + 2:]
if not argv:
    sys.exit('make_shields.py: give an out folder')
out_dir = os.path.abspath(argv[0])
wanted = argv[1:]

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

# Behind both: the handle, along the grip's way through the fist, on a post at each end to the shield's back.
HANDLE = 1.3          # its length
HANDLE_THICK = 0.32   # its thickness
STANDOFF = 1.25       # from the grip's middle forward to the round shield's back at its middle (its boss over the knuckles)
BALLISTIC_STANDOFF = 1.75   # and the ballistic shield's, which has no boss, clear of them

# The round shield.
ROUND_SIDES = 12
ROUND_RADIUS = 3.9    # to a corner of the outline
ROUND_THICK = 0.3
DISH = 0.55           # how far the edge stands back of the middle
PLANK_EDGES = (-0.65, -0.30, 0.24, 0.66)   # the grooves between the planks, shares of the radius across, as the sprite
GROOVE = 0.09         # each groove's width
RIM = 0.32            # the iron rim's width round the edge (on the face), and how far it stands out of the face
RIM_OUT = 0.12
BOSS = 1.05           # the boss's radius, and its height out of the face
BOSS_HIGH = 0.75
BOSS_RINGS = 3
BOSS_SIDES = 10

# The ballistic shield.
WIDE = 7.2            # across, along the curve's chord
HIGH = 12.6
BELOW = 0.5           # share of HIGH below the grip (so the grip is at its middle)
BALLISTIC_THICK = 0.36
CURVE = 9.0           # the radius it is curved round (about the upright axis), the middle forward
TOP_ROUND = 0.9       # the corners' radii
BOTTOM_ROUND = 2.2
TOP_ARCH = 0.35       # how far the top edge's middle stands above its corners
WINDOW = (-2.5, 2.5, 0.61, 0.77)   # the viewport: across (left, right of the middle) and up (shares of HIGH from the bottom)
FRAME = 0.32          # the pale frame round it
BOLTS = ((-2.5, 0.42), (2.5, 0.42), (-2.5, 0.30), (2.5, 0.30))   # across, and up as WINDOW's
BOLT = 0.32           # a bolt head's size, and how far it stands out
COLUMNS = 1.0         # the longest a face of either is across

COLOURS = {'plank': (0.92, 0.90, 0.86), 'plank2': (0.84, 0.82, 0.78), 'groove': (0.52, 0.50, 0.47),
           'iron': (0.62, 0.63, 0.66), 'plate': (0.82, 0.83, 0.84), 'frame': (0.98, 0.98, 0.97),
           'bolt': (0.36, 0.36, 0.38), 'handle': (0.40, 0.38, 0.36)}
GLASS = 'glass'   # its faces go on the second material, Visor
COLOUR_CELL = 16

body = walk.join_body()
rig = body.parent
place, grip = walk.shield_rest()
place = rig.matrix_world @ place

class Solid:
    """A mesh built of parts, each face naming its colour."""
    def __init__(self, back):
        self.bm = bmesh.new()
        self.colour = {}
        self.back = back   # the colour of the plate's back and edge
    def face(self, points, colour):
        vs = [self.bm.verts.new(p) for p in points]
        self.colour[self.bm.faces.new(vs)] = colour
    def box(self, c, axes, half, colour):
        """A box at c, its sides along axes (three unit vectors), half its size along each."""
        corner = lambda i, j, k: c + axes[0] * half[0] * i + axes[1] * half[1] * j + axes[2] * half[2] * k
        for f in (((-1, -1, -1), (-1, 1, -1), (-1, 1, 1), (-1, -1, 1)), ((1, -1, -1), (1, -1, 1), (1, 1, 1), (1, 1, -1)),
                  ((-1, -1, -1), (-1, -1, 1), (1, -1, 1), (1, -1, -1)), ((-1, 1, -1), (1, 1, -1), (1, 1, 1), (-1, 1, 1)),
                  ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1)), ((-1, -1, 1), (-1, 1, 1), (1, 1, 1), (1, -1, 1))):
            self.face([corner(*x) for x in f], colour)
    def plate(self, xs, extent, front, thick, colour_at, rows_at=None):
        """A plate, convex in its outline: columns at xs (across, sorted), each running up from extent(x)'s low to its
        high, at the heights rows_at(x) gives (shares from low to high, else even) and front(x, z)'s depth (y); its back
        thick behind that. colour_at(x, z) names each face's colour by its middle. The corners where a column comes to a
        point are welded after."""
        rows = []
        for x in xs:
            lo, hi = extent(x)
            shares = rows_at(x, lo, hi) if rows_at else [r / ROWS for r in range(ROWS + 1)]
            rows.append([(x, lo + (hi - lo) * s) for s in shares])
        n = len(rows[0])
        F = [[Vector((x, front(x, z), z)) for x, z in col] for col in rows]
        B = [[p + Vector((0, thick, 0)) for p in col] for col in F]
        for c in range(len(xs) - 1):
            for r in range(n - 1):
                quad = (rows[c][r], rows[c + 1][r], rows[c + 1][r + 1], rows[c][r + 1])
                mx, mz = sum(q[0] for q in quad) / 4, sum(q[1] for q in quad) / 4
                col = colour_at(mx, mz)
                self.face([F[c][r], F[c + 1][r], F[c + 1][r + 1], F[c][r + 1]], col)
                self.face([B[c][r], B[c][r + 1], B[c + 1][r + 1], B[c + 1][r]], col if col == GLASS else self.back)
        # The edge: round the outline, front to back.
        edge = [(c, 0) for c in range(len(xs))] + [(len(xs) - 1, r) for r in range(1, n)] + \
               [(c, n - 1) for c in range(len(xs) - 2, -1, -1)] + [(0, r) for r in range(n - 2, 0, -1)]
        for k in range(len(edge)):
            a, b = edge[k], edge[(k + 1) % len(edge)]
            self.face([F[a[0]][a[1]], B[a[0]][a[1]], B[b[0]][b[1]], F[b[0]][b[1]]], self.back)
    def mesh(self, name):
        bm = self.bm
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
        bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=1e-4)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        names = list(COLOURS)
        uv = bm.loops.layers.uv.new('UVMap')
        for f in bm.faces:
            c = self.colour.get(f, self.back)
            if c == GLASS:
                f.material_index = 1
            u = (names.index(c if c in names else names[0]) + 0.5) / len(names)
            for l in f.loops:
                l[uv].uv = (u, 0.5)
        bmesh.ops.transform(bm, matrix=place, verts=bm.verts)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        return me

ROWS = 4         # the round shield's rows up each column (its dish is gentle)

def columns(edges, half):
    """Columns across from -half to half, through every one of edges, none further apart than COLUMNS."""
    cuts = sorted(set([-half * 0.999, half * 0.999] + [e for e in edges if -half < e < half]))
    xs = []
    for a, b in zip(cuts, cuts[1:]):
        k = max(1, math.ceil((b - a) / COLUMNS))
        xs += [a + (b - a) * i / k for i in range(k)]
    return xs + [cuts[-1]]

def handle(s, back_y):
    """The handle along the grip's way through the origin, and a post from each end forward to the back at back_y."""
    g = grip.normalized()
    side = g.cross(Vector((0, 1, 0))).normalized()
    out = side.cross(g).normalized()
    s.box(Vector(), (g, side, out), (HANDLE / 2, HANDLE_THICK / 2, HANDLE_THICK / 2), 'handle')
    for end in (-1, 1):
        p = g * (HANDLE / 2 - HANDLE_THICK / 2) * end
        depth = (p.y - back_y) / 2 + 0.05
        s.box(Vector((p.x, (p.y + back_y) / 2, p.z)), (Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))),
              (HANDLE_THICK / 2, depth, HANDLE_THICK / 2), 'handle')

def polygon_extent(corners):
    """extent(x) of a convex polygon (x, z corners): the lowest and highest z on it at x."""
    def extent(x):
        zs = []
        for (ax, az), (bx, bz) in zip(corners, corners[1:] + corners[:1]):
            if (ax - x) * (bx - x) <= 0 and ax != bx:
                zs.append(az + (bz - az) * (x - ax) / (bx - ax))
        return min(zs), max(zs)
    return extent

def melee_shield():
    s = Solid('plank2')
    R = ROUND_RADIUS
    corners = [(R * math.sin(2 * math.pi * (i + 0.5) / ROUND_SIDES), R * math.cos(2 * math.pi * (i + 0.5) / ROUND_SIDES))
               for i in range(ROUND_SIDES)]
    extent = polygon_extent(corners)
    back = -STANDOFF
    front = lambda x, z: back - ROUND_THICK + DISH * (x * x + z * z) / (R * R)
    grooves = []
    for e in PLANK_EDGES:
        grooves += [(e * R - GROOVE / 2), (e * R + GROOVE / 2)]
    def colour_at(x, z):
        for e in PLANK_EDGES:
            if abs(x - e * R) < GROOVE / 2:
                return 'groove'
        return 'plank' if sum(1 for e in PLANK_EDGES if x > e * R) % 2 == 0 else 'plank2'
    # The rim covers the outline's edge, so the columns needn't meet its corners.
    xs = columns(grooves, R * math.cos(math.pi / ROUND_SIDES))
    s.plate(xs, extent, front, ROUND_THICK, colour_at)
    # The iron rim: a band round the outline over the face's edge and the plate's side, a little proud of both.
    for i in range(ROUND_SIDES):
        a, b = Vector((corners[i][0], 0, corners[i][1])), Vector((corners[(i + 1) % ROUND_SIDES][0], 0, corners[(i + 1) % ROUND_SIDES][1]))
        ring = []
        for p in (a, b):
            r = p.length
            out = p.normalized()
            y_front = front(p.x, p.z)
            ring.append((p + out * RIM_OUT + Vector((0, y_front - RIM_OUT, 0)),
                         p - out * RIM + Vector((0, front(p.x * (1 - RIM / r), p.z * (1 - RIM / r)) - RIM_OUT, 0)),
                         p - out * RIM + Vector((0, y_front + ROUND_THICK + 0.05, 0)),
                         p + out * RIM_OUT + Vector((0, y_front + ROUND_THICK + RIM_OUT, 0))))
        (a0, a1, a2, a3), (b0, b1, b2, b3) = ring
        for f in ((a0, b0, b1, a1), (a1, b1, b2, a2), (a2, b2, b3, a3), (a3, b3, b0, a0)):
            s.face(f, 'iron')
    # The boss: a low dome on the face's middle, in rings.
    base_y = front(0, 0) + 0.05
    rings = []
    for k in range(BOSS_RINGS + 1):
        t = k / BOSS_RINGS
        r = BOSS * math.cos(t * math.pi / 2)
        y = base_y - BOSS_HIGH * math.sin(t * math.pi / 2)
        rings.append([Vector((r * math.cos(2 * math.pi * i / BOSS_SIDES), y, r * math.sin(2 * math.pi * i / BOSS_SIDES)))
                      for i in range(BOSS_SIDES)])
    for k in range(BOSS_RINGS):
        for i in range(BOSS_SIDES):
            j = (i + 1) % BOSS_SIDES
            pts = [rings[k][i], rings[k][j], rings[k + 1][j], rings[k + 1][i]]
            if k == BOSS_RINGS - 1:
                pts = [rings[k][i], rings[k][j], rings[k + 1][0]]
            s.face(pts, 'iron')
    s.face(list(reversed(rings[0])), 'iron')
    handle(s, back)
    return made(s, 'CE_MeleeShield')

def ballistic_shield():
    s = Solid('plate')
    half = WIDE / 2
    low, high = -HIGH * BELOW, HIGH * (1 - BELOW)
    def extent(x):
        lo, hi = low, high + TOP_ARCH * (1 - (x / half) ** 2)
        d = half - abs(x)
        if d < BOTTOM_ROUND:
            lo = low + BOTTOM_ROUND - math.sqrt(max(0.0, BOTTOM_ROUND ** 2 - (BOTTOM_ROUND - d) ** 2))
        if d < TOP_ROUND:
            hi -= TOP_ROUND - math.sqrt(max(0.0, TOP_ROUND ** 2 - (TOP_ROUND - d) ** 2))
        return lo, hi
    back = -BALLISTIC_STANDOFF
    sag = lambda x: CURVE - math.sqrt(CURVE * CURVE - x * x)
    front = lambda x, z: back - BALLISTIC_THICK + sag(x)
    wl, wr, wb, wt = WINDOW
    wb, wt = low + HIGH * wb, low + HIGH * wt
    def colour_at(x, z):
        if wl < x < wr and wb < z < wt:
            return GLASS
        if wl - FRAME < x < wr + FRAME and wb - FRAME < z < wt + FRAME:
            return 'frame'
        return 'plate'
    levels = [wb - FRAME, wb, wt, wt + FRAME]
    def rows_at(x, lo, hi):
        cuts = sorted(set([lo, hi] + [z for z in levels if lo < z < hi]))
        zs = []
        for a, b in zip(cuts, cuts[1:]):
            k = max(1, math.ceil((b - a) / (COLUMNS * 2.5)))
            zs += [a + (b - a) * i / k for i in range(k)]
        zs.append(cuts[-1])
        # Every column has the same rows: each cut's share kept, the rest spread to make up ROWS_B.
        shares = [(z - lo) / (hi - lo) for z in zs]
        while len(shares) < ROWS_B:
            gaps = [(shares[i + 1] - shares[i], i) for i in range(len(shares) - 1)]
            g, i = max(gaps)
            shares.insert(i + 1, (shares[i] + shares[i + 1]) / 2)
        return shares
    # Rows all columns share: the window's and frame's edges must line up across them, so only the columns between the
    # frame's sides cut at those heights; the rest spread theirs evenly.
    def rows_even(x, lo, hi):
        if wl - FRAME - 1e-6 <= x <= wr + FRAME + 1e-6:
            return rows_at(x, lo, hi)
        return [r / (ROWS_B - 1) for r in range(ROWS_B)]
    xs = columns([wl - FRAME, wl, wr, wr + FRAME], half)
    s.plate(xs, extent, front, BALLISTIC_THICK, colour_at, rows_even)
    for bx, bz in BOLTS:
        z = low + HIGH * bz
        s.box(Vector((bx, front(bx, z) - BOLT / 4, z)), (Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1))),
              (BOLT / 2, BOLT / 2, BOLT / 2), 'bolt')
    handle(s, back + sag(0))
    return made(s, 'CE_BallisticShield')

ROWS_B = 10       # the ballistic shield's, its window's edges among them

def made(s, name):
    o = bpy.data.objects.new(name, s.mesh(name))
    bpy.context.collection.objects.link(o)
    for p in o.data.polygons:
        p.use_smooth = False
    names = list(COLOURS)
    img = bpy.data.images.new(name, COLOUR_CELL * len(names), COLOUR_CELL, alpha=False)
    px = []
    for y in range(COLOUR_CELL):
        for x in range(COLOUR_CELL * len(names)):
            px += list(COLOURS[names[x // COLOUR_CELL]]) + [1.0]
    img.pixels = px
    img.file_format = 'PNG'
    img.pack()
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    tex = m.node_tree.nodes.new('ShaderNodeTexImage')
    tex.image = img
    tex.interpolation = 'Closest'
    m.node_tree.links.new(tex.outputs['Color'], m.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    o.data.materials.append(m)
    if any(p.material_index == 1 for p in o.data.polygons):
        glass = bpy.data.materials.new('Visor')
        glass.use_nodes = True
        glass.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.6, 0.7, 0.75, 1.0)
        o.data.materials.append(glass)
    return o

SHIELDS = {'CE_MeleeShield': melee_shield, 'CE_BallisticShield': ballistic_shield}

os.makedirs(out_dir, exist_ok=True)
made_shields = []
for name in (wanted or list(SHIELDS)):
    o = SHIELDS[name]()
    for x in bpy.context.view_layer.objects:
        x.select_set(x is o)
    bpy.context.view_layer.objects.active = o
    path = os.path.join(out_dir, name + '.glb')
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format='GLB', export_skins=False,
                              export_animations=False, export_morph=False)
    made_shields.append(o)
    print('SHIELD %s: %d vertices, %d faces, %d triangles, %d glass -> %s'
          % (name, len(o.data.vertices), len(o.data.polygons), sum(len(p.vertices) - 2 for p in o.data.polygons),
             sum(1 for p in o.data.polygons if p.material_index == 1), path))
if out_blend:
    for o in made_shields:
        o.parent = rig
        o.matrix_parent_inverse = rig.matrix_world.inverted()
        o.modifiers.new('Armature', 'ARMATURE').object = rig
        o.vertex_groups.new(name='hand.L').add(range(len(o.data.vertices)), 1.0, 'REPLACE')
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
