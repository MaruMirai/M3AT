"""Makes hats for the chibi from the shape of its own head, one .glb each.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_hats.py -- <out folder> [hat ...] [--blend out.blend]

Each hat is built round the head's middle (the middle of the Head part at rest, in its base shape): a snug part (a
helmet's shell, a tuque) is laid over the skin by casting a line out from the middle in every direction and standing
off the head where it meets it; a hat with a crown of its own (a cowboy hat, a bowler) stands its crown on a band laid
round the head that way. A hat is low-poly, about the head's own density (SEGMENTS round, a few rings up), and one surface
(the game draws both its sides) with no UVs, so it is drawn in the apparel's colour. The file is named for the game's apparel (cowboyHat.glb is the garment CowboyHat, for
Apparel_CowboyHat), so put in Models/garments it is worn; export_frames.py's GARMENT_PROPS has it ride the head bone
whole (bone), keep its shape through the body types (hard) and cut the hair at its rim (hide_hair), but for the hood, whose cowl
takes the body's weights. The war mask, veil and face mask are sheets rather than rings, cut to an outline laid over the
face, and leave the hair and the face layers on; so does the blindfold, a band round the head over the eyes, knotted at
the back. The coronet and crown are metal bands with points round their tops, the crown's round a closed cap; the coronet is
stood off the head over the hair, which it leaves on. The stellic crown is a gold helm with a flat crest across its top. The authority cap is a peaked cap and the flophat a thick,
slumped knitted cap; the gunlink is a unit over the right ear with a rod up from it and an arm round to the right eye,
and leaves the hair and the face on. The authority cap and the gunlink are coloured: UVs and a small image of flat
colours (Shape.palette), the cap's pale enough for the apparel's colour to show. Ideology's headwrap is a wound cloth
high in a V over the forehead, its shadecone a wide shallow cone on a headband inside it, its slicecap a crescent of
hard shell over the left of the scalp alone, its tailcap a flat-topped fur cap with a ragged edge and a tail hanging on
the right, and its torture crown a band stood over the hair with spikes up from its top and down from its bottom. Biotech's
airwire headset is a unit over the right ear with an antenna up from it, crossed twice near its top, and a boom forward
round the temple; it leaves the hair and the face on. Its array headset is a visor round the front over the eyes, out to
a pod over each ear with an antenna up from it leaning back, and its integrator headset a bar hooked over the right of
the crown down to the ear, with a boom round the jaw to a ball microphone. The slave collar is no hat: a band round the
neck, built round the neck's own upright axis, with a riveted plate on either side and a ring at the throat. The tailcap, torture crown and headsets are
coloured, as the cap is. Ideology's visage mask is a rigid shield over the face with round eye holes and a nose, a sheet
given a thickness; Biotech's gas mask a rubber face piece with two lenses of glass (GLASS: the Visor material, which the
game draws see-through with the eyes behind), a snout to a filter canister and a harness of straps, coloured; and
Ideology's broadwrap a cloth over the head, the neck and the shoulders with an opening for the face, hung from the head
rather than laid on the skin below it, and taking the body's weights as the hood does. Anomaly's ritual mask is a skull's
face over the whole front of the head, a sheet given a thickness as the visage mask, with scowling eye sockets, a nose
hole and four fangs hanging below the chin; its ceremonial hood the broadwrap's cloth drawn up into a point, falling as a
bell to the shoulders and open down the front, a dark mask of the same kind with long teeth in the opening, coloured.
The burka's head piece (BurkaHood, in PARTS) is the broadwrap's cloth closed over the face but for a slit over the eyes;
it is no garment of its own, but joined onto the burka by make_duster.py --burka, so make it into Models/bases.

Without names it makes every hat in HATS (not PARTS). The .blend is never saved; the optional out.blend is a separate file with the
hats on the rig, to model from. Directions are as the figure stands: it faces -y, z is up. A place round the head is an
azimuth (degrees from the front, round either side to the back at 180, the hats being the same both sides) and an
elevation (degrees up from level through the head's middle). Lengths are in head radii (the head's mean distance from
its middle to the skin, measured), so the hats hold if the rig is scaled."""
import bpy, bmesh, sys, math, os, random
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import delaunay_2d_cdt

argv = sys.argv[sys.argv.index('--') + 1:]
out_blend = None
if '--blend' in argv:
    out_blend = argv[argv.index('--blend') + 1]
    argv = argv[:argv.index('--blend')] + argv[argv.index('--blend') + 2:]
out_dir = os.path.abspath(argv[0])
wanted = argv[1:]

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

body = walk.join_body()   # as export_frames.py: the body's parts (walk.BODY_PARTS) as one mesh
rig = body.parent
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0

# The head alone (its part's group), at rest in world space, and a tree of it to cast lines at.
bw = body.matrix_world
head_group = body.vertex_groups[walk.PART_GROUP % 'Head'].index
in_head = [any(g.group == head_group and g.weight > 0.5 for g in v.groups) for v in body.data.vertices]
verts = [bw @ v.co for v in body.data.vertices]
head_verts = [p for p, h in zip(verts, in_head) if h]
tree = BVHTree.FromPolygons(verts, [tuple(p.vertices) for p in body.data.polygons if all(in_head[i] for i in p.vertices)])
top = max(p.z for p in head_verts)
lo = Vector([min(p[a] for p in head_verts) for a in range(3)])
hi = Vector([max(p[a] for p in head_verts) for a in range(3)])
# The middle: halfway across and from front to back, and as far under the crown as it is wide (the Head part runs down
# the neck, so halfway up would be too low).
CENTRE = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, top - (hi.x - lo.x) / 2))

def way(az, el):
    """The unit direction at an azimuth and elevation (degrees)."""
    a, e = math.radians(az), math.radians(el)
    return Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e)))

_reach = {}
def skin(az, el):
    """How far the skin is from the middle that way (world units). Lines that miss (down the open neck) take the mean.
    Averaged over a small cone so the hat doesn't take every facet of the head's low-poly surface."""
    key = (round(az, 3), round(el, 3))
    if key not in _reach:
        ds = []
        for da, de in ((0, 0), (5, 0), (-5, 0), (0, 5), (0, -5), (4, 4), (-4, 4), (4, -4), (-4, -4)):
            hit = tree.ray_cast(CENTRE, way(az + da, el + de))
            if hit[0] is not None:
                ds.append(hit[3])
        _reach[key] = sum(ds) / len(ds) if ds else None
    return _reach[key]

R = sum(skin(az, el) for az in range(0, 360, 30) for el in range(0, 90, 15)) / 72   # the head's radius
side = lambda az: math.sin(math.radians(az)) ** 2           # 1 at either side, 0 front and back
front = lambda az: max(0.0, math.cos(math.radians(az))) ** 2  # 1 at the front, 0 from the sides back
back = lambda az: max(0.0, -math.cos(math.radians(az))) ** 2  # 1 at the back, 0 from the sides forward

def band(f, s, b):
    """An elevation round the head that is f at the front, s at the sides and b at the back, smoothly between."""
    return lambda az: s + (f - s) * front(az) + (b - s) * back(az)

def snug(az, el, off):
    """The point off (head radii) out from the skin at az, el."""
    d = skin(az, el)
    return CENTRE + way(az, el) * ((d if d is not None else R) + off * R)

SEGMENTS = 16   # points round each ring

def azimuths(n=None):
    n = n or SEGMENTS
    return [360.0 * i / n for i in range(n)]

class Shape:
    """Rings of points round the head, joined in order into quads, the last closed to a point: tip, else its middle."""
    def __init__(self, segments=None):
        self.rings, self.tip = [], None
        self.n = segments or SEGMENTS   # points round each ring: more for a hat with arches cut in its edge
        self.parts = []   # other pieces of the same mesh: (points, faces as lists of indices into them, colour)
        # A hat of more than one colour (palette: name to (r, g, b)) gets UVs and a small image of flat colours, as the
        # bandolier has. Each ring and part names its colour; the faces from a ring up to the next take that ring's, the
        # tip's fan the last ring's. Without a palette, the hat is plain and drawn in the apparel's colour.
        self.palette, self.ring_colours = None, []
    def ring(self, points, colour=None):
        self.rings.append(list(points))
        self.ring_colours.append(colour)
    def part(self, points, faces, colour=None):
        self.parts.append((points, faces, colour))
    def mesh(self, name):
        bm = bmesh.new()
        colour_of = {}   # face: its colour's name
        for points, faces, colour in self.parts:
            vs = [bm.verts.new(p) for p in points]
            for f in faces:
                colour_of[bm.faces.new([vs[i] for i in f])] = colour
        if self.rings:   # a mask or veil is parts alone
            rows = [[bm.verts.new(p) for p in ring] for ring in self.rings]
            for r in range(len(rows) - 1):
                a, b = rows[r], rows[r + 1]
                for i in range(self.n):
                    j = (i + 1) % self.n
                    colour_of[bm.faces.new((a[i], a[j], b[j], b[i]))] = self.ring_colours[r]
            last = self.rings[-1]
            tip = bm.verts.new(self.tip if self.tip is not None else sum(last, Vector()) / len(last))
            row = rows[-1]
            for i in range(self.n):
                colour_of[bm.faces.new((row[i], row[(i + 1) % self.n], tip))] = self.ring_colours[-1]
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        for f, c in colour_of.items():   # glass on the second material, the Visor (made() adds it)
            if c == GLASS:
                f.material_index = 1
        if self.palette:
            # Every corner of a face at the middle of its colour's square of the image (the first colour for any not named,
            # and for the glass, which the game draws in its own colour).
            names = list(self.palette)
            uv = bm.loops.layers.uv.new('UVMap')
            for f in bm.faces:
                c = colour_of.get(f)
                u = (names.index(c if c in names else names[0]) + 0.5) / len(names)
                for l in f.loops:
                    l[uv].uv = (u, 0.5)
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        return me

def smoothstep(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)

def dome(shape, bottom, off, rings=5):
    """A shell over the head from the elevation bottom(az) up to the crown, off(t) head radii off the skin (t 0 at the
    bottom, 1 at the crown), the crown's own point closing it."""
    for r in range(rings):
        t = r / rings
        shape.ring([snug(az, bottom(az) + (90.0 - bottom(az)) * t, off(t)) for az in azimuths(shape.n)])
    shape.tip = snug(0, 90, off(1))

def flare(shape, base, out, down, rings=1):
    """A rim out from the ring base: out(az) head radii level and outward from the head's middle, falling down(az, t)
    (head radii; negative curls it up) across it, t 0 at the base and 1 at the edge. Added below the base, so the
    rings run from the edge in."""
    edge = []
    for r in range(rings, 0, -1):
        t = r / rings
        pts = []
        for az, p in zip(azimuths(), base):
            flat = Vector((p.x - CENTRE.x, p.y - CENTRE.y, 0)).normalized()
            pts.append(p + flat * out(az) * t * R - Vector((0, 0, down(az, t) * R)))
        edge.append(pts)
    return edge

COLOUR_CELL = 16   # pixels square each colour of a coloured hat's image
# A part in this colour is glass: its faces go on a second material named Visor, which export_frames.py writes as visor
# lines, drawn see-through in the game with the face behind them (as the recon helmet's glass).
GLASS = 'glass'

def made(shape, name):
    o = bpy.data.objects.new(name, shape.mesh(name))
    bpy.context.collection.objects.link(o)
    for p in o.data.polygons:
        p.use_smooth = True
    if shape.palette:
        names = list(shape.palette)
        img = bpy.data.images.new(name, COLOUR_CELL * len(names), COLOUR_CELL, alpha=False)
        px = []
        for y in range(COLOUR_CELL):
            for x in range(COLOUR_CELL * len(names)):
                px += list(shape.palette[names[x // COLOUR_CELL]]) + [1.0]
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
        assert shape.palette, 'glass wants a coloured hat, so its own material comes first'
        glass = bpy.data.materials.new('Visor')
        glass.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.6, 0.7, 0.75, 1.0)
        o.data.materials.append(glass)
    return o

# A steel pot with a little brim all round: down to the brows at the front, over the tops of the ears, lower at the back.
def simple_helmet():
    s = Shape()
    bottom = band(14, -2, -12)
    base = [snug(az, bottom(az), 0.07) for az in azimuths()]
    for pts in flare(s, base, lambda az: 0.13, lambda az, t: 0.04 * t * t):
        s.ring(pts)
    dome(s, bottom, lambda t: 0.07)
    return made(s, 'SimpleHelmet')

# A deeper, rounder shell (a flak helmet): over the tops of the ears and the back of the head, a short lip over the brows.
def advanced_helmet():
    s = Shape()
    bottom = band(16, -12, -26)
    base = [snug(az, bottom(az), 0.15) for az in azimuths()]
    for pts in flare(s, base, lambda az: 0.03 + 0.07 * front(az), lambda az, t: 0.03 * t):
        s.ring(pts)
    dome(s, bottom, lambda t: 0.15 + 0.02 * smoothstep(t))
    return made(s, 'AdvancedHelmet')

# A knitted cap pulled down over the ears with a turned-up cuff, standing off the crown and slouching a little back.
def tuque():
    s = Shape()
    bottom = band(18, -14, -20)
    cuff = 0.28   # of the way up, the cuff's top
    def at(az, t, off):
        p = snug(az, bottom(az) + (90.0 - bottom(az)) * t, off + 0.1 * smoothstep((t - 0.3) / 0.7))
        p.y += 0.1 * R * smoothstep((t - 0.5) / 0.5)
        return p
    # The cuff's outer face, folded back in to the cap where it ends, then the cap up to the crown.
    for t, off in ((0.0, 0.16), (cuff, 0.16), (cuff, 0.1), (0.48, 0.1), (0.66, 0.1), (0.84, 0.1)):
        s.ring([at(az, t, off) for az in azimuths()])
    s.tip = at(0, 1.0, 0.1)
    return made(s, 'Tuque')

def oval(bottom, off, n=None):
    """A smooth band round the head at bottom(az): an oval (its own depth to the front and to the back) just big enough
    to take every snug point off head radii out, at the heights they have, n points round (SEGMENTS). A crown stood on
    the skin's own line would take every lump of the low-poly head into its wall and brim."""
    snugs = [snug(az, bottom(az), off) for az in azimuths(n)]
    wide = max(abs(p.x - CENTRE.x) for p in snugs)
    ahead = max(CENTRE.y - p.y for p in snugs)
    behind = max(p.y - CENTRE.y for p in snugs)
    def at(az, k):
        a = math.radians(az)
        return Vector((CENTRE.x + k * wide * math.sin(a), CENTRE.y - k * (ahead if math.cos(a) > 0 else behind) * math.cos(a), 0))
    k = max(math.hypot(p.x - CENTRE.x, p.y - CENTRE.y) / (at(az, 1.0) - Vector((CENTRE.x, CENTRE.y, 0))).length
            for az, p in zip(azimuths(n), snugs))
    return [at(az, k) + Vector((0, 0, p.z)) for az, p in zip(azimuths(n), snugs)]

def crown(s, bottom, height, profile, dent=0.0, pinch=0.0):
    """A crown of its own, stood on the band round the head at bottom(az) (snug). profile is a list of (across, up)
    from the band to the top's middle: across scales the band's distance from the head's upright axis, up runs from the
    band (0) to height head radii above the top of the head (1). dent creases the top front to back (head radii deep);
    pinch draws the upper wall in at either side of the front (a share of the width)."""
    base = oval(bottom, 0.06)
    z_top = top + height * R
    width = max(abs(p.x - CENTRE.x) for p in base)
    depth = max(abs(p.y - CENTRE.y) for p in base)
    for k, (across, up) in enumerate(profile):
        pts = []
        for az, p in zip(azimuths(), base):
            flat = Vector((p.x - CENTRE.x, p.y - CENTRE.y, 0))
            a = abs((az + 180) % 360 - 180)   # degrees from the front, either side
            sc = across * (1 - pinch * math.exp(-((a - 38) / 22) ** 2) * smoothstep((up - 0.4) / 0.6))
            q = Vector((CENTRE.x, CENTRE.y, p.z + (z_top - p.z) * up)) + flat * sc
            if up >= 0.9:
                along = abs(q.y - CENTRE.y) / depth
                q.z -= dent * R * math.exp(-((q.x - CENTRE.x) / (0.32 * width)) ** 2) * (1 - smoothstep((along - 0.55) / 0.35))
            pts.append(q)
        s.ring(pts)

# A cowboy hat: a creased crown pinched at the front and a wide brim turned up at the sides, dipping a little front and back.
def cowboy_hat():
    s = Shape()
    bottom = band(30, 22, 12)
    base = oval(bottom, 0.06)
    brim = lambda az: 0.55 + 0.17 * (1 - side(az))
    curl = lambda az, t: -0.62 * side(az) * t * t + 0.08 * (1 - side(az)) * t * t
    for pts in flare(s, base, brim, curl, rings=2):
        s.ring(pts)
    profile = [(1.0, 0.0), (0.95, 0.5), (0.88, 0.9), (0.68, 1.02), (0.34, 1.02)]
    crown(s, bottom, 0.32, profile, dent=0.3, pinch=0.16)
    return made(s, 'CowboyHat')

# A bowler: a round, hard dome above the head and a short brim rolled up at the sides.
def bowler_hat():
    s = Shape()
    bottom = band(32, 24, 14)
    base = oval(bottom, 0.06)
    brim = lambda az: 0.2 + 0.06 * (1 - side(az))
    curl = lambda az, t: -0.2 * side(az) * t * t + 0.05 * (1 - side(az)) * t * t
    for pts in flare(s, base, brim, curl, rings=2):
        s.ring(pts)
    wall = 0.3
    profile = [(1.0, 0.0)] + [(1.02 * math.cos(math.radians(d)), wall + (1 - wall) * math.sin(math.radians(d)))
                              for d in range(0, 90, 22)]
    crown(s, bottom, 0.25, profile)
    return made(s, 'BowlerHat')

BERET_HEIGHT = 0.1   # head radii the beret's top stands above the top of the head
BERET_DROOP = 0.55   # head radii its overhang falls at the figure's right, the side it is pulled down to
BERET_SLIDE = 0.16   # head radii it is pulled over to that side, by its top

# A beret: a soft, flat cushion of cloth on a snug band, bulging out past the band all round near its top and pulled
# down over the figure's right side, with a little nub in the middle of the top.
def beret():
    s = Shape()
    bottom = band(34, 26, 12)
    profile = [(1.0, 0.0), (1.22, 0.25), (1.4, 0.58), (1.34, 0.8), (1.02, 0.95), (0.52, 1.0)]
    crown(s, bottom, BERET_HEIGHT, profile)
    width = max(abs(p.x - CENTRE.x) for p in s.rings[0])
    # The band stays on the head; above it the cloth goes over to the right (-x) and the farther right of the middle a
    # point is, the more it falls. Only outward of the band, so it doesn't sink into the head.
    for k, ring in enumerate(s.rings):
        up = profile[k][1]
        for q in ring:
            right = max(0.0, (CENTRE.x - q.x) / width)
            q.x -= BERET_SLIDE * R * up
            q.z -= BERET_DROOP * R * smoothstep(up / 0.7) * right * right
    s.tip = sum(s.rings[-1], Vector()) / SEGMENTS + Vector((0, 0, 0.06 * R))
    return made(s, 'Beret')

# A top hat: a tall crown, straight with a slight flare to its flat top, and a narrow brim rolled up at the sides.
def hat_top():
    s = Shape()
    bottom = band(32, 24, 14)
    base = oval(bottom, 0.06)
    brim = lambda az: 0.22 + 0.06 * (1 - side(az))
    curl = lambda az, t: -0.24 * side(az) * t * t + 0.04 * (1 - side(az)) * t * t
    for pts in flare(s, base, brim, curl, rings=2):
        s.ring(pts)
    profile = [(1.0, 0.0), (0.96, 0.12), (0.97, 0.5), (1.03, 0.88), (1.07, 1.0), (0.62, 1.0)]
    crown(s, bottom, 0.95, profile)
    return made(s, 'HatTop')

# A ladies' hat: a low, rounded crown with a soft crease, and a wide brim swept down over the figure's right and turned
# up on its left, dipping a little at the front.
def hat_ladies():
    s = Shape()
    bottom = band(30, 22, 12)
    base = oval(bottom, 0.06)
    brim = lambda az: 0.52 + 0.12 * front(az)
    def curl(az, t):
        lean = math.sin(math.radians(az))   # 1 at the figure's left, -1 at its right
        return t * t * (0.5 * max(0.0, -lean) - 0.38 * max(0.0, lean) + 0.08 * front(az))
    for pts in flare(s, base, brim, curl, rings=3):
        s.ring(pts)
    profile = [(1.0, 0.0), (0.98, 0.45), (0.9, 0.85), (0.66, 1.0), (0.3, 1.02)]
    crown(s, bottom, 0.22, profile, dent=0.14, pinch=0.08)
    return made(s, 'HatLadies')

def by_azimuth(*table):
    """A value round the head from (degrees from the front, value) pairs, straight between them, the same both sides."""
    def at(az):
        a = abs((az + 180) % 360 - 180)
        for (a0, v0), (a1, v1) in zip(table, table[1:]):
            if a <= a1:
                return v0 + (v1 - v0) * (a - a0) / (a1 - a0)
        return table[-1][1]
    return at

def envelope(az, el):
    """How far the head reaches from the middle round about that way, ears and all (world units): the most in a wide
    cone, so a soft part laid on it goes over the ears rather than in and out round them. Down the open neck, the mean."""
    ds = [skin(az + da, el + de) for da in (-36, -18, 0, 18, 36) for de in (-30, -15, 0, 15, 30)]
    ds = [d for d in ds if d is not None]
    return max(ds) if ds else R

def neck_ring(z, off):
    """A ring round the neck at height z, off head radii out from how far the body reaches there."""
    near = [p for p in verts if abs(p.z - z) < 0.06 * R and math.hypot(p.x - CENTRE.x, p.y - CENTRE.y) < 0.5 * R]
    wide = max(abs(p.x - CENTRE.x) for p in near)
    deep = max(abs(p.y - CENTRE.y) for p in near)
    return [CENTRE + Vector(((wide + off * R) * math.sin(math.radians(az)), -(deep + off * R) * math.cos(math.radians(az)),
                             z - CENTRE.z)) for az in azimuths()]

HOOD_PEAK = 0.7   # head radii the hood's crown falls back to a point
COWL_HIGH, COWL_LOW = 0.92, 1.28   # head radii below the head's middle the cowl runs between

# A draped cloth hood: over the head and the ears down to the neck, with a soft point falling back from the crown, open
# round the face with its edge turned back, and a cowl round the neck below the chin that the sides hang over.
def hat_hood():
    s = Shape()
    bottom = by_azimuth((0, 32), (22.5, 28), (45, 6), (67.5, -42), (90, -60), (135, -66), (180, -68))
    OFF = 0.13
    def at(az, t, off):
        el = bottom(az) + (90.0 - bottom(az)) * t
        p = CENTRE + way(az, el) * (envelope(az, el) + off * R)
        p.y += HOOD_PEAK * R * t ** 3
        return p
    # The hem's lip: from just inside the edge, out round it, then on up to the crown.
    s.ring([at(az, 0.0, OFF - 0.06) for az in azimuths()])
    s.ring([at(az, 0.0, OFF + 0.05) for az in azimuths()])
    for t in (0.14, 0.32, 0.52, 0.72, 0.88):
        s.ring([at(az, t, OFF) for az in azimuths()])
    s.tip = at(0, 1.0, OFF)
    # The cowl, a band from behind the chin down to just above the shoulders, widening as it falls.
    high, low = CENTRE.z - COWL_HIGH * R, CENTRE.z - COWL_LOW * R
    rings = [neck_ring(high + (low - high) * k, 0.12 + 0.14 * k) for k in (0.0, 0.5, 1.0)]
    n = SEGMENTS
    s.part([p for r in rings for p in r],
           [[r * n + i, r * n + (i + 1) % n, (r + 1) * n + (i + 1) % n, (r + 1) * n + i] for r in range(2) for i in range(n)])
    return made(s, 'HatHood')

EAR_AZ, EAR_EL = 58, 46                    # where an ear stands on the head
EAR_HEIGHT, EAR_LEAN = 0.95, 0.12          # head radii up, and out from the head's middle by the top
EAR_WIDTH, EAR_DEPTH = 0.42, 0.18          # its foot's half width and half depth

def beast_ear(side):
    """One of the headdress's ears, a tall flat blade (side 1 the figure's left, -1 its right) with its foot under the
    shell: each foot point is drawn in onto the skin, as the head curves away under a foot this wide."""
    foot = snug(side * EAR_AZ, EAR_EL, 0.0)
    out = Vector((side, 0, 0))
    forward = Vector((0, -1, 0))
    up = Vector((0, 0, 1))
    tip = foot + (up * EAR_HEIGHT + out * EAR_LEAN) * R
    def across(w, d, h):
        return foot + (out * w * EAR_WIDTH + forward * d * EAR_DEPTH + up * h * EAR_HEIGHT + out * h * EAR_LEAN) * R
    def under(p):
        d = p - CENTRE
        return snug(math.degrees(math.atan2(d.x, -d.y)), math.degrees(math.asin(d.z / d.length)), 0.0)
    points = [under(across(-1, 0, 0)), under(across(0, 1, 0)), under(across(1, 0, 0)), under(across(0, -1, 0)),   # the foot
              across(-0.45, 0, 0.5), across(0, 0.65, 0.5), across(0.62, 0, 0.5), across(0, -0.65, 0.5),   # bulging on the outer edge
              tip]
    faces = [[i, (i + 1) % 4, (i + 1) % 4 + 4, i + 4] for i in range(4)] + [[i + 4, (i + 1) % 4 + 4, 8] for i in range(4)]
    return points, faces

# A beast's head worn as a tribal headdress, as the game's sprite: two tall ears, the brow arching over each eye down to
# a raised muzzle between them at the nose, a jag at each cheek standing away from the face and a pelt's jagged edge behind.
def tribal_headdress():
    s = Shape()
    bottom = by_azimuth((0, -28), (22.5, 7), (45, 6), (67.5, -14), (90, -36), (112.5, -8), (135, -24), (157.5, -6), (180, -30))
    muzzle = lambda az, t: 0.3 * (1 - smoothstep(t / 0.8)) * (1.0 if abs((az + 180) % 360 - 180) < 1 else 0.0) \
        + 0.06 * (1 - smoothstep(t)) * max(0.0, math.cos(math.radians(az))) ** 8
    cheek = by_azimuth((0, 0.0), (45, 0.0), (67.5, 0.08), (90, 0.26), (112.5, 0.08), (135, 0.12), (180, 0.1))
    at = lambda az, t: snug(az, bottom(az) + (90.0 - bottom(az)) * t, 0.1 + 0.05 * smoothstep(t) + muzzle(az, t) + cheek(az) * (1 - smoothstep(t / 0.4)))
    for t in (0.0, 0.2, 0.42, 0.62, 0.82):
        s.ring([at(az, t) for az in azimuths()])
    s.tip = at(0, 1.0)
    s.part(*beast_ear(1))
    s.part(*beast_ear(-1))
    return made(s, 'TribalHeaddress')

# A tinfoil hat (the psychic foil helmet): a sheet of foil crushed by hand into a blunt cone over the head, its top bent
# back and its edge left rough. Each point is pushed out from the cone by its own amount, more on some than others, as
# foil crumples (even creases, in and out by turns, ran together up the hat in a spiral). Never in past the cone, which
# clears the head. Fixed random numbers, so it comes out the same every time.
def psychic_foil_helmet():
    s = Shape()
    bottom = band(26, 14, 2)
    base = oval(bottom, 0.16)
    rough = random.Random(FOIL_SEED)
    z_top = top + FOIL_HEIGHT * R
    for up, crumple in ((0.0, 0.06), (0.16, 0.2), (0.38, 0.2), (0.6, 0.17), (0.8, 0.13)):
        pts = []
        for p in base:
            flat = Vector((p.x - CENTRE.x, p.y - CENTRE.y, 0))
            q = Vector((CENTRE.x, CENTRE.y, p.z + (z_top - p.z) * up)) + flat * (1 - up ** 1.5)
            q += flat.normalized() * rough.random() ** 2 * crumple * R
            q.y += FOIL_BEND * R * up * up
            q.z += rough.uniform(-1, 1) * crumple * 0.3 * R
            pts.append(q)
        s.ring(pts)
    s.tip = Vector((CENTRE.x, CENTRE.y + FOIL_BEND * R, z_top))
    return made(s, 'PsychicFoilHelmet')

FOIL_HEIGHT = 0.5   # head radii the point stands above the top of the head
FOIL_BEND = 0.3     # head radii the point is bent back
FOIL_SEED = 7

def inside(p, polygon):
    """Whether the point (x, y) is inside the polygon, a list of (x, y)."""
    hit = False
    for (x0, y0), (x1, y1) in zip(polygon, polygon[1:] + polygon[:1]):
        if (y0 > p[1]) != (y1 > p[1]) and p[0] < x0 + (p[1] - y0) * (x1 - x0) / (y1 - y0):
            hit = not hit
    return hit

def cut_out(outline, holes, step, centred=False):
    """A flat sheet cut to the polygon outline, less the polygons holes, with points about step apart inside so it can
    follow the head's curve. Returns its points and triangles. Here x and y are an azimuth and an elevation. Centred, the
    grid has a column at azimuth 0, so a sheet the same both sides comes out so."""
    points = list(outline) + [p for h in holes for p in h]
    edges, start = [], 0
    for loop in [outline] + holes:
        edges += [(start + i, start + (i + 1) % len(loop)) for i in range(len(loop))]
        start += len(loop)
    xs, ys = [p[0] for p in outline], [p[1] for p in outline]
    x = -math.floor(-min(xs) / step) * step if centred else min(xs)
    while x <= max(xs):
        y = min(ys)
        while y <= max(ys):
            # Not right beside the edge, which would leave slivers.
            if inside((x, y), outline) and not any(inside((x, y), h) for h in holes) \
                    and all(math.hypot(x - a, y - b) > step * 0.45 for a, b in points):
                points.append((x, y))
            y += step
        x += step
    made_points, _, faces, _, _, _ = delaunay_2d_cdt([Vector(p) for p in points], edges, [], 0, 1e-4)
    keep = []
    for f in faces:
        mid = sum((made_points[i] for i in f), Vector((0, 0))) / len(f)
        if inside(mid, outline) and not any(inside(mid, h) for h in holes):
            keep.append(list(f))
    return [tuple(p) for p in made_points], keep

def both_sides(half):
    """A polygon the same both sides of azimuth 0, from its right half, top to bottom, (azimuth, elevation) with the
    azimuth 0 ends once only."""
    other = [(-a, e) for a, e in reversed(half) if a != 0]
    return half + other

def strap(s, az_from, el, wide, off, n=12, colour=None):
    """A thin band round the back of the head from az_from on one side to the same on the other, wide degrees across, at
    the elevation el(az), in n pieces (more where it must bend round an ear rather than cut across it), in the palette's
    colour named (if any)."""
    points = []
    for i in range(n + 1):
        az = az_from + (360 - 2 * az_from) * i / n
        points += [snug(az, el(az) + wide / 2, off), snug(az, el(az) - wide / 2, off)]
    s.part(points, [[2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1] for i in range(n)], colour)

# Where the face is on this head, as (azimuth, elevation) off the skin, from the Facial Animation eyes and the nose as
# export_frames.py lays them on the head: the eye's middle, and the height just under the eyes that a mask stops at.
EYE_AZ, EYE_EL = 27, -26
UNDER_EYE_EL = -40

MASK_OFF = 0.09                      # head radii the war mask stands off the skin
EYE_HOLE_AZ, EYE_HOLE_EL = 15, 14    # the half width and half height of its eye holes, degrees
HORN_UP, HORN_OUT = 0.28, 0.12       # head radii its horns' tips rise and lean out past the mask's top

# The war mask, as the game's sprite: a hide stretched over the face, with two small horns at its top corners, drawn in
# at the temples, two lobes at the jaw and the bottom edge curved up between them. Two round eye holes close together,
# which the pawn's own eyes look out of.
def war_mask():
    s = Shape()
    outline = both_sides([(0, 44), (20, 46), (34, 48), (50, 62), (58, 44), (73, 30), (60, 8), (54, -12), (58, -32),
                          (72, -52), (70, -72), (44, -66), (20, -60), (0, -58)])
    def hole(side):
        n = 8
        return [(side * EYE_AZ + EYE_HOLE_AZ * math.cos(2 * math.pi * i / n), EYE_EL + EYE_HOLE_EL * math.sin(2 * math.pi * i / n))
                for i in range(n)]
    points, faces = cut_out(outline, [hole(1), hole(-1)], 14)
    def lift(az, el):
        p = snug(az, el, MASK_OFF)
        rise = smoothstep((el - 46) / 16) * smoothstep((abs(az) - 34) / 12)
        return p + Vector((math.copysign(HORN_OUT, az) * rise, 0, HORN_UP * rise)) * R
    s.part([lift(az, el) for az, el in points], faces)
    return made(s, 'WarMask')

VEIL_REACH = 72      # degrees round either side of the front its top edge goes, stopping short of the ears
VEIL_COLUMNS = 8     # columns of cloth either side of the middle
VEIL_FOLD = 0.035    # head radii its folds go in and out, by the bottom

# A veil, as the game's sprite: a cloth from just under the eyes, tied behind the head, falling straight off the nose
# past the chin in a curtain, in soft folds, longest at the middle and to a torn, jagged hem.
def war_veil():
    s = Shape()
    top = by_azimuth((0, -36), (27, UNDER_EYE_EL), (60, -30), (72, -22))
    length = by_azimuth((0, 1.55), (30, 1.42), (55, 1.12), (72, 0.74))   # head radii below the head's middle
    notch = by_azimuth((0, 0.3), (55, 0.22), (72, 0.1))                           # how far the hem's jags come back up
    n = VEIL_COLUMNS
    azs = [VEIL_REACH * i / n for i in range(-n, n + 1)]
    rows = (0.0, 0.3, 0.62)
    points = []
    for c, az in enumerate(azs):
        head = snug(az, top(az), 0.06)
        bottom = CENTRE.z - length(az) * R + (notch(az) * R if (c - n) % 2 else 0.0)
        fold = VEIL_FOLD * R * (1 if (c - n) % 2 else -1)
        for t in rows + (1.0,):
            points.append(Vector((head.x * (1 - 0.06 * t) + CENTRE.x * 0.06 * t, head.y - 0.1 * R * t * t - fold * t,
                                  head.z + (bottom - head.z) * t)))
    k = len(rows) + 1
    faces = [[c * k + r, (c + 1) * k + r, (c + 1) * k + r + 1, c * k + r + 1] for c in range(2 * n) for r in range(k - 1)]
    s.part(points, faces)
    strap(s, VEIL_REACH - 3, by_azimuth((69, -22), (82, -8), (100, 4), (180, 10)), 6, 0.05)   # up over the ears
    return made(s, 'WarVeil')

CLOTH_REACH = 84   # degrees round either side of the front the face mask goes
CLOTH_PLEAT = 0.025   # head radii its pleats go in and out
CLOTH_OVER = 6     # degrees each way the skin is looked at round a point of the mask, which lies over the most of it

def over_skin(az, el, reach):
    """How far the skin reaches from the middle round about that way (world units): the most within reach degrees, so
    cloth laid on it tents over a sharp edge (the chibi's nose is a ledge, which skin()'s mean would cut into) rather
    than sinking round it. Lines that miss take skin()'s."""
    ds = []
    steps = 3
    for i in range(-steps, steps + 1):
        for j in range(-steps, steps + 1):
            hit = tree.ray_cast(CENTRE, way(az + reach * i / steps, el + reach * j / steps))
            if hit[0] is not None:
                ds.append(hit[3])
    return max(ds) if ds else (skin(az, el) or R)

# A face mask: a pleated cloth over the nose and mouth from just under the eyes to under the chin, its top edge up over
# the bridge of the nose, and a tie from its top corners up over the ears (which stand out from az 85 to 105, el -10 to
# -40 on this head) and round the back of the head.
def cloth_mask():
    s = Shape()
    top = by_azimuth((0, -30), (12, -33), (27, UNDER_EYE_EL), (60, -35), (84, -26))
    bottom = by_azimuth((0, -74), (30, -72), (60, -60), (84, -42))
    n = 7
    azs = [CLOTH_REACH * i / n for i in range(-n, n + 1)]
    rows = 5
    points = []
    for az in azs:
        for r in range(rows):
            t = r / (rows - 1)
            el = top(az) + (bottom(az) - top(az)) * t
            pleat = CLOTH_PLEAT * (1 if r % 2 else -1) if 0 < r < rows - 1 else 0.0
            puff = 0.05 * max(0.0, math.cos(math.radians(az))) * math.sin(math.pi * t)
            points.append(CENTRE + way(az, el) * (over_skin(az, el, CLOTH_OVER) + (0.06 + pleat + puff) * R))
    faces = [[c * rows + r, (c + 1) * rows + r, (c + 1) * rows + r + 1, c * rows + r + 1] for c in range(2 * n) for r in range(rows - 1)]
    s.part(points, faces)
    strap(s, CLOTH_REACH - 3, by_azimuth((81, -27), (88, -4), (100, 4), (180, 8)), 5, 0.05, n=24)
    return made(s, 'ClothMask')

BLIND_COLUMNS = 28    # points round the band
BLIND_OFF = 0.07      # head radii the band stands off the skin
BLIND_PUFF = 0.025    # head radii it bulges between its edges, as cloth folded over on itself
BLIND_SMOOTH = 5      # degrees each way the head is looked at round a point, so the band goes over the ears' tops and
                      # the low-poly head's facets rather than into them
KNOT_EL = 8           # degrees up at the back where it is tied
KNOT_SIZE = 0.18      # head radii the knot stands out and across
TAIL_LENGTH = 0.62    # head radii its two ends hang below the knot
TAIL_WIDTH = 0.17     # head radii across each end
TAIL_SPREAD = 0.12    # head radii the ends part toward their tips

def blind_skin(az, el):
    """How far the band lies from the head's middle that way: the most the skin reaches nearby (BLIND_SMOOTH)."""
    ds = [skin(az + da, el + de) for da in (-BLIND_SMOOTH, 0, BLIND_SMOOTH) for de in (-BLIND_SMOOTH, 0, BLIND_SMOOTH)]
    ds = [d for d in ds if d is not None]
    return max(ds) if ds else R

# A blindfold: a wide band of cloth over both eyes (EYE_AZ, EYE_EL: they are big on the chibi, so it is wide at the front),
# narrowing as it rises over the ears' tops to the back of the head, where it is knotted, its two ends hanging down.
def blindfold():
    s = Shape()
    mid = by_azimuth((0, EYE_EL + 1), (27, EYE_EL + 1), (50, -17), (70, -4), (88, 9), (110, 12), (180, KNOT_EL))
    half = by_azimuth((0, 18), (40, 17), (70, 11), (100, 8), (180, 9))   # degrees above and below the middle
    rows = (-1.0, -0.5, 0.0, 0.5, 1.0)
    n = BLIND_COLUMNS
    points = []
    for c in range(n):
        az = 360.0 * c / n
        for r in rows:
            el = mid(az) + half(az) * r
            off = BLIND_OFF + BLIND_PUFF * (1 - r * r)
            points.append(CENTRE + way(az, el) * (blind_skin(az, el) + off * R))
    k = len(rows)
    faces = [[c * k + r, ((c + 1) % n) * k + r, ((c + 1) % n) * k + r + 1, c * k + r + 1] for c in range(n) for r in range(k - 1)]
    s.part(points, faces)
    # The knot: a squat lump on the band at the back, an octahedron stood out and squashed up and down.
    back_of = lambda az, el, off: CENTRE + way(az, el) * (blind_skin(az, el) + off * R)
    knot = back_of(180, KNOT_EL, BLIND_OFF)
    out = (knot - CENTRE).normalized()
    across, up = Vector((1, 0, 0)), Vector((0, 0, 1))
    kp = [knot + out * KNOT_SIZE * R, knot + across * KNOT_SIZE * R, knot + up * 0.7 * KNOT_SIZE * R,
          knot - across * KNOT_SIZE * R, knot - up * 0.7 * KNOT_SIZE * R]
    s.part(kp, [[0, 1, 2], [0, 2, 3], [0, 3, 4], [0, 4, 1]])
    # The ends: two strips from under the knot, hanging straight down, standing back off the head a little more as they
    # fall (the back of the head curves in toward the neck under them) and parting toward their tips.
    for side in (1, -1):
        tail = []
        steps = 4
        for i in range(steps + 1):
            t = i / steps
            m = knot + out * 0.5 * KNOT_SIZE * R + Vector((side * (0.3 * KNOT_SIZE + TAIL_SPREAD * t) * R, 0.06 * R * t,
                                                           -(0.5 * KNOT_SIZE + TAIL_LENGTH * t) * R))
            w = TAIL_WIDTH * R * (1 - 0.25 * t) / 2
            tail += [m - across * w, m + across * w]
        s.part(tail, [[2 * i, 2 * i + 1, 2 * i + 3, 2 * i + 2] for i in range(steps)])
    return made(s, 'Blindfold')

def loops(s, rings, colour=None):
    """Rings round the head (closed loops of the same length) joined in order into quads, as a part of their own, left
    open at both ends, in the palette's colour named (if any)."""
    n = len(rings[0])
    s.part([p for r in rings for p in r],
           [[r * n + i, r * n + (i + 1) % n, (r + 1) * n + (i + 1) % n, (r + 1) * n + i]
            for r in range(len(rings) - 1) for i in range(n)], colour)

def orb(s, at, size, colour=None):
    """A small gem or ball at a point, an octahedron size head radii across its middle, a little taller than wide, in the
    palette's colour named (if any)."""
    r = size / 2 * R
    s.part([at + Vector((0, 0, 1.25 * r)), at + Vector((r, 0, 0)), at + Vector((0, -r, 0)), at + Vector((-r, 0, 0)),
            at + Vector((0, r, 0)), at - Vector((0, 0, 1.25 * r))],
           [[0, 1, 2], [0, 2, 3], [0, 3, 4], [0, 4, 1], [5, 2, 1], [5, 3, 2], [5, 4, 3], [5, 1, 4]], colour)

def outward(p):
    """The level way out from the head's upright axis through p."""
    return Vector((p.x - CENTRE.x, p.y - CENTRE.y, 0)).normalized()

def circlet(s, base, height, flare, bead, points, peak, rise, orbs=0.0, taller=lambda k: 0.0):
    """A metal band stood on the ring base (from oval, any number of points round) height head radii tall, leaning
    out flare head radii by its top, with a bead round its foot standing bead head radii out (0: none, two rings fewer),
    and its top edge cut into points: points of them round, each peak head radii above the band's top, the edge between
    falling in a curve (rise: how far up the points' sides it stays, 0 a sharp V, toward 1 rounded shoulders; with two
    columns a point, as the low-poly crowns have, it is always a V). Each point's tip is in line with a column of base,
    which must divide evenly by points, the first at the front. orbs: a ball that big on each tip. taller(k): head radii
    more for the k-th point round, from the front."""
    n = len(base)
    step = n // points
    up = Vector((0, 0, 1))
    ring = lambda h, out: [p + up * h * R + outward(p) * out * R for p in base]
    rings = [ring(0.0, 0.0)] + ([ring(0.035, bead), ring(0.07, 0.0)] if bead else []) + [ring(height, flare)]
    edge = []
    for i, p in enumerate(base):
        t = abs(((i % step) / step) * 2 - 1)   # 1 at a tip, 0 halfway between two
        k = int(round(i / step)) % points   # the nearest tip
        h = height + (peak + taller(k)) * (t ** (1.0 / max(rise, 1e-3)) if rise > 0 else t)
        edge.append(p + up * h * R + outward(p) * flare * (h / height) * R)
    rings.append(edge)
    loops(s, rings)
    if orbs:
        for i in range(0, n, step):
            orb(s, edge[i] + up * 0.45 * orbs * R, orbs)
    return edge

CORONET_BOTTOM = (26, 20, 12)   # its foot's elevation at the front, sides and back, degrees
CORONET_OFF = 0.2               # head radii off the skin: it is worn over the hair, which it leaves on

# A coronet: a slim metal band round the head over the hair, with eight points round its top, each tipped with a little
# ball, as the game's sprite, the front one the tallest. Low-poly: SEGMENTS round, so each point is a plain V.
def coronet():
    s = Shape()
    bottom = band(*CORONET_BOTTOM)
    base = oval(bottom, CORONET_OFF)
    circlet(s, base, 0.13, 0.03, 0.0, 8, 0.2, 0.0, orbs=0.075, taller=lambda k: 0.06 if k == 0 else 0.0)
    # A gem in the band under the front point.
    orb(s, base[0] + Vector((0, 0, 0.07 * R)) + outward(base[0]) * 0.03 * R, 0.09)
    return made(s, 'Coronet')

CROWN_BOTTOM = (30, 22, 14)
CROWN_CAP = 0.1   # head radii its cap stands above the top of the head

# A crown: a cushioned cap over the top of the head closing it, and round it a metal band with eight points standing
# out from the cap, the front, back and side ones taller, and a ball on the cap's top.
def crown_royal():
    s = Shape()
    bottom = band(*CROWN_BOTTOM)
    profile = [(1.0, 0.0), (0.86, 0.62), (0.45, 1.0)]
    crown(s, bottom, CROWN_CAP, profile)
    s.tip = sum(s.rings[-1], Vector()) / SEGMENTS + Vector((0, 0, 0.03 * R))
    base = [p + outward(p) * 0.05 * R for p in oval(bottom, 0.06)]
    circlet(s, base, 0.34, 0.1, 0.0, 8, 0.34, 0.0, orbs=0.07, taller=lambda k: 0.08 if k % 2 == 0 else 0.0)
    orb(s, s.tip + Vector((0, 0, 0.07 * R)), 0.16)
    return made(s, 'Crown')

# The stellic crown as a gold helm: a shell over the head from a brow that dips to a point over the nose, an angular
# cheek guard hanging either side of the face, and on top a flat crest standing across the head, two tall spikes up its
# middle with a slit between and a great wing out to either side, its top edge sweeping down and out to a point at the
# brow's height and its under edge curving back up into the helm (a crescent with a trident's middle).
STELLIC_BROW = 18           # degrees up the brow is over the eyes
STELLIC_NOSE = 2            # and at its point over the nose
STELLIC_CHEEK = -38         # and at the cheek guards' tips
STELLIC_BACK = 4            # and at the back
STELLIC_OFF = 0.15          # head radii the helm stands off the skin
# The crest's right half, (across, up) in head radii from the top of the head's middle line, round from the slit's foot
# over the spike and the wing and back in under it; the left is its mirror.
STELLIC_CREST = [(0.04, 0.3), (0.06, 1.25), (0.11, 1.6), (0.24, 1.0), (0.55, 0.68), (0.95, 0.3), (1.2, -0.05),
                 (1.42, -0.45), (1.12, -0.2), (0.85, -0.08), (0.58, -0.22)]
STELLIC_CREST_FOOT = -0.3   # where the crest's bottom edge meets the middle line, under the helm
STELLIC_CREST_AHEAD = 0.3   # head radii ahead of the head's middle it stands
STELLIC_CREST_SWEEP = 0.22  # head radii its wings sweep back by their tips, per (head radius out) squared
STELLIC_CREST_THICK = 0.06  # head radii thick

def crown_stellic():
    s = Shape()
    def bottom(az):
        a = abs((az + 180) % 360 - 180)   # degrees from the front, either side
        # By the 16 columns: the nose point at 0, the brow over the eyes at 22.5 and 45, the cheek guards' tips at
        # 67.5, back up over the ears and round the back.
        table = [(0, STELLIC_NOSE), (22.5, STELLIC_BROW), (45, STELLIC_BROW), (67.5, STELLIC_CHEEK), (90, 0),
                 (135, STELLIC_BACK), (180, STELLIC_BACK)]
        for (a0, e0), (a1, e1) in zip(table, table[1:]):
            if a <= a1:
                return e0 + (e1 - e0) * (a - a0) / (a1 - a0)
        return table[-1][1]
    dome(s, bottom, lambda t: STELLIC_OFF, rings=4)
    # The crest: a flat outline, its two faces and its edge, standing across the head.
    half = STELLIC_CREST
    outline = [(0.0, half[0][1] - 0.05)] + half + [(0.0, STELLIC_CREST_FOOT)] + [(-x, z) for x, z in reversed(half)]
    flat, tris = cut_out(outline, [], 100.0)
    def placed(x, z, f):
        return Vector((CENTRE.x + x * R, CENTRE.y - (STELLIC_CREST_AHEAD - STELLIC_CREST_SWEEP * x * x + f) * R, top + z * R))
    t = STELLIC_CREST_THICK / 2
    n = len(flat)
    pts = [placed(x, z, t) for x, z in flat] + [placed(x, z, -t) for x, z in flat]
    faces = [list(f) for f in tris] + [[n + i for i in reversed(f)] for f in tris]
    # The edge: each outline point is the same index in flat (cut_out keeps its input points first, in order).
    m = len(outline)
    faces += [[i, (i + 1) % m, n + (i + 1) % m, n + i] for i in range(m)]
    s.part(pts, faces)
    return made(s, 'CrownStellic')

def snug_way(d, off):
    """The point off head radii out from the skin along the direction d from the head's middle."""
    d = d.normalized()
    return snug(math.degrees(math.atan2(d.x, -d.y)), math.degrees(math.asin(max(-1.0, min(1.0, d.z)))), off)

ELTEX_SEGMENTS = 40   # points round the eltex hats' rings (9 degrees apart, one on the eye), so the arches in their
                      # edges come out as arches, not Vs

# The eltex helmet's edge, (degrees from the front, elevation), as the game's sprite: a tongue down the bridge of the nose
# between two arches over the eyes (whose tops are at about -12 on this head), a point at each temple beside the eye
# (the eyes reach out to about 42), an arch over each ear (85 to 105 round, its top at about -10), and low round the back.
ELTEX_HELMET_EDGE = ((0, -20), (6, -14), (12, -2), (20, 8), (27, 11), (34, 8), (42, -4), (50, -20), (54, -26), (60, -20),
                     (68, -4), (78, 6), (90, 9), (102, 6), (112, -6), (124, -18), (140, -22), (180, -22))
ELTEX_HELMET_OFF = 0.12   # head radii the shell stands off the skin
# The raised plate down its middle, from the nose tongue over the top to the nape: (degrees along the middle line from
# the front, past 90 down the back, half its width in head radii). A wedge on the front, wide over the top, a panel at
# the back narrowing toward its foot.
ELTEX_PLATE = ((-14, 0.04), (6, 0.1), (36, 0.2), (66, 0.26), (90, 0.27), (120, 0.27), (150, 0.25), (176, 0.2), (192, 0.15))
ELTEX_PLATE_RISE = 0.05   # head radii it stands proud of the shell

# The eltex helmet (Royalty's psyfocus helmet), as the game's sprite: a light, smooth shell over the head, cut in arches
# over the eyes and the ears with a tongue over the nose and a point at each temple, and a raised plate down its middle
# from the nose over the top to the nape.
def psyfocus_helmet():
    s = Shape(ELTEX_SEGMENTS)
    dome(s, by_azimuth(*ELTEX_HELMET_EDGE), lambda t: ELTEX_HELMET_OFF, rings=4)
    # The plate: across each row its edges sink just under the shell and its middle stands proud.
    across = ((-1.0, -0.01), (-0.75, ELTEX_PLATE_RISE), (0.75, ELTEX_PLATE_RISE), (1.0, -0.01))
    points = []
    for along, half in ELTEX_PLATE:
        a = math.radians(along)
        mid = Vector((0, -math.cos(a), math.sin(a)))
        for x, rise in across:
            points.append(snug_way(mid + Vector((x * half, 0, 0)), ELTEX_HELMET_OFF + rise))
    k = len(across)
    s.part(points, [[r * k + i, r * k + i + 1, (r + 1) * k + i + 1, (r + 1) * k + i]
                    for r in range(len(ELTEX_PLATE) - 1) for i in range(k - 1)])
    return made(s, 'PsyfocusHelmet')

# The eltex skullcap's edge, as the game's sprite: from the front a rounded tongue down the middle of the forehead between
# two tall, narrow cut-outs, each side coming down to a point at the temple; from the side another cut-out in front of
# the ear; from behind a band across the back of the head with a wing out over each ear and a spine down to the nape.
ELTEX_CAP_EDGE = ((0, 4), (9, 4), (14, 24), (18, 36), (27, 42), (36, 36), (40, 24), (45, 2), (54, -6), (60, 2), (66, 24),
                  (72, 38), (81, 42), (90, 38), (96, 24), (102, 8), (120, 8), (144, 10), (153, 4), (158, -30), (180, -38))
ELTEX_CAP_OFF = 0.1   # head radii off the skin: it is tight, but the low-poly head's facets come through any closer

# The eltex skullcap: a thin, close cap over the top of the head, more a frame than a shell, cut away over the eyes and
# in front of the ears.
def eltex_skullcap():
    s = Shape(ELTEX_SEGMENTS)
    dome(s, by_azimuth(*ELTEX_CAP_EDGE), lambda t: ELTEX_CAP_OFF, rings=4)
    return made(s, 'EltexSkullcap')

AUTHORITY_BOTTOM = (30, 20, 12)   # the band's foot's elevation at the front, sides and back, degrees
AUTHORITY_HEIGHT = 0.42           # head radii the crown's top stands above the top of the head, at the back
AUTHORITY_PEAK = 0.24             # head radii more it rises to the front, to a point over the middle of the brow
AUTHORITY_VISOR = 0.5             # head radii the visor stands out at the front
AUTHORITY_VISOR_DROP = 0.24       # head radii it falls by its edge

# The authority cap, as the game's sprite: a peaked cap, a grey band round the head under a white crown that spreads out
# past it all round, its top rising to a point over the brow and sloping down to the back, and a dark stiff visor sloping
# down over the eyes. Coloured (pale for the crown, as the game multiplies it by the apparel's colour).
def authority_cap():
    s = Shape()
    s.palette = {'crown': (0.95, 0.95, 0.95), 'band': (0.62, 0.62, 0.62), 'visor': (0.3, 0.3, 0.3)}
    bottom = band(*AUTHORITY_BOTTOM)
    # (across, up, colour of the faces from there up): the band, then the crown, bulging out over it and rounding in to
    # the top.
    profile = [(1.0, 0.0, 'band'), (1.0, 0.3, 'crown'), (1.08, 0.38, 'crown'), (1.3, 0.72, 'crown'), (1.32, 0.88, 'crown'),
               (1.12, 0.98, 'crown'), (0.62, 1.0, 'crown')]
    crown(s, bottom, AUTHORITY_HEIGHT, [(a, u) for a, u, _ in profile])
    s.ring_colours = [c for _, _, c in profile]
    # The top raised to the front, most over the middle line: the crown's upper rings by how high up they are.
    for k, ring in enumerate(s.rings):
        up = profile[k][1]
        for az, q in zip(azimuths(), ring):
            q.z += AUTHORITY_PEAK * R * smoothstep((up - 0.3) / 0.7) * max(0.0, math.cos(math.radians(az))) ** 3
    s.tip = sum(s.rings[-1], Vector()) / SEGMENTS + Vector((0, -0.25 * R, 0.1 * R))
    # The visor: from the band's foot round the front, out and down in two rows, a little up at its corners.
    base = s.rings[0]
    cols = [i for i, az in enumerate(azimuths()) if abs((az + 180) % 360 - 180) <= 70]
    cols.sort(key=lambda i: (azimuths()[i] + 180) % 360)
    points = []
    for i in cols:
        az = azimuths()[i]
        reach = max(0.0, math.cos(math.radians(az))) ** 0.7
        p = base[i]
        for t in (0.0, 0.5, 1.0):
            points.append(p + outward(p) * AUTHORITY_VISOR * reach * t * R
                          - Vector((0, 0, AUTHORITY_VISOR_DROP * reach * t * (0.6 + 0.4 * t) * R)))
    k = 3
    s.part(points, [[c * k + r, (c + 1) * k + r, (c + 1) * k + r + 1, c * k + r + 1] for c in range(len(cols) - 1)
                    for r in range(k - 1)], 'visor')
    return made(s, 'AuthorityCap')

FLOP_BACK = 0.62    # head radii the cap's top slumps back, by the crown
FLOP_DROP = 0.42    # and down
FLOP_THICK = 0.2    # head radii the knit stands off the skin
FLOP_ROLL = 0.07    # head radii its rolls go in and out

# The flophat, as the game's sprite: a big, thick knitted cap in rolls, pulled down over the brow and the ears with a
# rolled edge, its extra fabric slumped back and down behind the head.
def flophat():
    s = Shape()
    bottom = band(20, -14, -24)
    def at(az, t, off):
        p = snug(az, bottom(az) + (90.0 - bottom(az)) * t, off)
        slump = smoothstep((t - 0.25) / 0.75)
        # The slump falls further down at the back, where the extra fabric hangs, than over the brow.
        p.y += FLOP_BACK * R * slump
        p.z -= FLOP_DROP * R * slump * slump * (0.35 + 0.65 * max(0.0, -math.cos(math.radians(az))))
        return p
    # The rolled edge (its foot, round its outside and back in), then the rolls on up to the crown.
    rings = ((0.0, FLOP_THICK - 0.06), (0.0, FLOP_THICK + 0.06), (0.1, FLOP_THICK + 0.08), (0.2, FLOP_THICK),
             (0.33, FLOP_THICK + FLOP_ROLL), (0.47, FLOP_THICK), (0.6, FLOP_THICK + FLOP_ROLL), (0.73, FLOP_THICK + 0.02),
             (0.86, FLOP_THICK + FLOP_ROLL))
    for t, off in rings:
        s.ring([at(az, t, off) for az in azimuths()])
    s.tip = at(0, 1.0, FLOP_THICK)
    return made(s, 'Flophat')

GUN_EAR_AZ, GUN_EAR_EL = -96, -22   # the unit's middle over the figure's right ear (the right being -x, negative azimuths)
GUN_OFF = 0.1                       # head radii it stands off the ear (the most the head reaches round there)
GUN_UNIT = (0.46, 0.66, 0.2)         # head radii: the unit's width (front to back), height and thickness
GUN_ROD_TOP = 0.22                  # head radii the rod's top stands above the top of the head
GUN_ROD_WIDTH = 0.15                # head radii across the rod
GUN_ARM_EL = -17                    # degrees up the arm runs round to the eye, just over the eye's middle
GUN_ARM_HIGH = 6                    # degrees above and below that it is wide
GUN_ARM_TO = -8                     # degrees round from the front it ends, past the right eye (EYE_AZ) to the nose

def slab(middle, ax, ay, az_, colour, s):
    """A box round middle with half sides ax, ay, az_ (vectors), as a part of s."""
    corners = [middle + ax * i + ay * j + az_ * k for i in (-1, 1) for j in (-1, 1) for k in (-1, 1)]
    s.part(corners, [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]], colour)

# The gunlink (Royalty), as the game's sprite: a grey unit over the right ear with a rod standing straight up from it past
# the top of the head, and a white arm from it round the side of the face to in front of the right eye, where the retinal
# projection is. Coloured, as it is not made from stuff: the game draws it as it is.
def gunlink():
    s = Shape()
    s.palette = {'unit': (0.4, 0.4, 0.42), 'rod': (0.52, 0.52, 0.54), 'arm': (0.94, 0.94, 0.94), 'lens': (0.2, 0.22, 0.26)}
    middle = CENTRE + way(GUN_EAR_AZ, GUN_EAR_EL) * (envelope(GUN_EAR_AZ, GUN_EAR_EL) + GUN_OFF * R)
    out = Vector((-1, 0, 0))
    forward = Vector((0, -1, 0))
    up = Vector((0, 0, 1))
    w, h, d = GUN_UNIT
    slab(middle, forward * w / 2 * R, up * h / 2 * R, out * d / 2 * R, 'unit', s)
    # The rod: from inside the unit's top, toward its back, straight up, leaning in a little to keep near the head.
    foot = middle + up * h * 0.3 * R - forward * w * 0.22 * R
    rod_top = Vector((foot.x + 0.06 * R, foot.y, top + GUN_ROD_TOP * R))
    rod_mid = (foot + rod_top) / 2
    along = (rod_top - foot) / 2
    slab(rod_mid, forward * GUN_ROD_WIDTH / 2 * R, along, out * GUN_ROD_WIDTH / 2 * R, 'rod', s)
    # The arm: a strip round the face from the unit's front to the eye, laid off the skin (the most of it nearby, so it
    # rides over the cheek and the nose's ledge rather than into them), standing a little nearer as it comes round.
    n = 10
    points = []
    start = GUN_EAR_AZ + 8
    for i in range(n + 1):
        t = i / n
        az = start + (GUN_ARM_TO - start) * t
        off = GUN_OFF * (1 - t) + 0.12 * t
        for de in (GUN_ARM_HIGH, -GUN_ARM_HIGH):
            el = GUN_ARM_EL + de
            points.append(CENTRE + way(az, el) * (over_skin(az, el, 4) + off * R))
    s.part(points, [[2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1] for i in range(n)], 'arm')
    # The lens: a small dark square at the arm's end, just in front of it, over the eye.
    az = GUN_ARM_TO + (start - GUN_ARM_TO) * 0.22
    lens = CENTRE + way(az, GUN_ARM_EL) * (over_skin(az, GUN_ARM_EL, 4) + 0.135 * R)
    ahead = way(az, GUN_ARM_EL)
    across = ahead.cross(up).normalized()
    slab(lens, across * 0.09 * R, up * 0.07 * R, ahead * 0.012 * R, 'lens', s)
    return made(s, 'Gunlink')

HEADWRAP_SEGMENTS = 24   # points round its rings (15 degrees apart), so the V over the forehead comes to a point
# Its edge, (degrees from the front, elevation), as the game's sprite: high in a V over the middle of the forehead, down
# over the brows to the temples, over the tops of the ears and low round the back to the nape.
HEADWRAP_EDGE = ((0, 30), (15, 17), (30, 6), (60, -4), (90, -14), (130, -30), (180, -36))
HEADWRAP_OFF = 0.1     # head radii the cloth stands off the skin at its edge
HEADWRAP_PUFF = 0.1    # head radii more it stands off over the top and back, where the cloth is wound thickest
HEADWRAP_BACK = 0.12   # head radii its top is drawn back, so it bulges behind as the sprite does
HEADWRAP_OVER = 8      # degrees each way the skin is looked at round a point, so it goes over the ears' tops

# A headwrap (Ideology's): a cloth wound round the head over the hair, high in a V at the middle of the forehead where its
# ends cross, down over the tops of the ears and to the nape behind, fuller over the top and back, its edge turned under.
def headwrap():
    s = Shape(HEADWRAP_SEGMENTS)
    bottom = by_azimuth(*HEADWRAP_EDGE)
    def at(az, t, off):
        el = bottom(az) + (90.0 - bottom(az)) * t
        a = abs((az + 180) % 360 - 180)
        full = smoothstep(t / 0.6) * (0.4 + 0.6 * smoothstep((a - 30) / 120))
        p = CENTRE + way(az, el) * (over_skin(az, el, HEADWRAP_OVER) + (off + HEADWRAP_PUFF * full) * R)
        p.y += HEADWRAP_BACK * R * smoothstep((t - 0.4) / 0.6)
        return p
    # The turned-under edge: from just inside it, out round it, then on up to the crown.
    s.ring([at(az, 0.0, HEADWRAP_OFF - 0.05) for az in azimuths(s.n)])
    s.ring([at(az, 0.0, HEADWRAP_OFF + 0.03) for az in azimuths(s.n)])
    for t in (0.12, 0.3, 0.5, 0.7, 0.88):
        s.ring([at(az, t, HEADWRAP_OFF) for az in azimuths(s.n)])
    s.tip = at(0, 1.0, HEADWRAP_OFF)
    return made(s, 'Headwrap')

SHADECONE_SEGMENTS = 24   # points round the cone
SHADECONE_BRIM = 1.85     # head radii out from the head's upright axis its edge goes (the sprite's is nearly twice the head)
SHADECONE_SLOPE = 26      # degrees the cone falls from level
SHADECONE_KNOB = 0.3      # head radii across (half) the knob on its top
SHADECONE_KNOB_TALL = 0.2 # head radii the knob stands
SHADECONE_CLEAR = 0.08    # head radii the cone stands over the head where it comes nearest
SHADECONE_TILT = 8        # degrees it is tipped back, the front up, as the sprite's side view has it
SHADECONE_BAND = (24, 14, 4)   # its headband's foot, degrees up at the front, sides and back

# A shadecone (Ideology's): a wide, shallow woven cone over the head with a little round knob on its top, its edge rolled
# under, tipped back a little, on a headband inside it that sits on the head (which the hair is tucked under).
def shadecone():
    s = Shape(SHADECONE_SEGMENTS)
    slope = math.tan(math.radians(SHADECONE_SLOPE))
    rk = SHADECONE_KNOB * R
    flat = lambda p: math.hypot(p.x - CENTRE.x, p.y - CENTRE.y)
    # The knob's foot as low as it can be with the cone over every point of the head by the clearance.
    zk = max(p.z + max(0.0, flat(p) - rk) * slope for p in head_verts if p.z > CENTRE.z) + SHADECONE_CLEAR * R
    cone = lambda r: zk - max(0.0, r - rk) * slope
    def at(az, r, z):
        a = math.radians(az)
        return Vector((CENTRE.x + r * math.sin(a), CENTRE.y - r * math.cos(a), z))
    edge = SHADECONE_BRIM * R
    knob_top = zk + SHADECONE_KNOB_TALL * R
    rings = [(edge - 0.08 * R, cone(edge) + 0.02 * R),   # the edge rolled under
             (edge, cone(edge) - 0.02 * R)] \
        + [(r, cone(r)) for r in (edge * 0.72, edge * 0.46, rk + 0.04 * R)] \
        + [(rk, zk + 0.03 * R), (rk * 0.96, knob_top)]
    for r, z in rings:
        s.ring([at(az, r, z) for az in azimuths(s.n)])
    s.tip = Vector((CENTRE.x, CENTRE.y, knob_top + 0.03 * R))
    # The headband: from a snug ring round the head up to just inside the cone's underside.
    foot = oval(band(*SHADECONE_BAND), 0.06)
    head_band = [p.copy() for p in foot]
    for p in head_band:
        p.z = cone(flat(p)) - 0.01 * R
    loops(s, [foot, head_band])
    # Tipped back about the head's middle (the cone's clearance from it holds, the head being near round).
    tip = math.radians(-SHADECONE_TILT)
    def tipped(p):
        d = p - CENTRE
        return CENTRE + Vector((d.x, d.y * math.cos(tip) - d.z * math.sin(tip), d.y * math.sin(tip) + d.z * math.cos(tip)))
    s.rings = [[tipped(p) for p in ring] for ring in s.rings]
    s.tip = tipped(s.tip)
    s.parts = [([tipped(p) for p in points], faces, colour) for points, faces, colour in s.parts]
    return made(s, 'Shadecone')

# The slicecap's outline, on a chart of the head seen from above: (left, forward) in degrees from the crown, so a point's
# distance from (0, 0) is how far down from the top it is (90 the level of the head's middle) and its way is its azimuth.
# A hard shell over the left of the scalp, as the game's sprites (it has three, each a slice of a cap): from a point over
# the forehead round the left side to a point at the back of the head, its lower edge notched over the ear, its inner
# edge bowed in over the left of the crown, so it is a crescent with its horns reaching over the middle line.
SLICECAP_OUTLINE = [(-10, 66), (14, 62), (38, 54), (56, 38), (66, 16), (53, 4), (68, -8), (68, -30), (56, -50), (34, -64),
                    (-6, -74), (6, -52), (14, -26), (16, 2), (12, 30), (2, 52)]
SLICECAP_OFF = 0.11   # head radii off the skin
SLICECAP_STEP = 11    # degrees between the points inside it, so it follows the head's curve

# A slicecap (Ideology's): a hard cap that covers only part of the scalp, a slice of a shell over the left of the head.
def slicecap():
    s = Shape()
    flat, tris = cut_out(SLICECAP_OUTLINE, [], SLICECAP_STEP)
    s.part([snug(math.degrees(math.atan2(u, v)), 90.0 - math.hypot(u, v), SLICECAP_OFF) for u, v in flat], tris)
    return made(s, 'Slicecap')

TAILCAP_SEGMENTS = 24              # points round the cap, so its fur's edge can be ragged
TAILCAP_BOTTOM = (20, 4, -6)       # its band's elevation at the front, sides and back, degrees: down to the brows, over the ears' tops
TAILCAP_OFF = 0.1                  # head radii the fur stands off the skin at the band
TAILCAP_HEIGHT = 0.12              # head radii its flat top stands above the top of the head
TAILCAP_FRINGE = (0.04, 0.13, 0.08, 0.15, 0.05, 0.11)   # head radii the fur's edge hangs below the band, column by column round
TAIL_AZ = -144                     # degrees round the tail hangs from: the figure's right, toward the back (as all three sprites)
TAIL_LENGTH = 1.0                  # head radii it hangs below the cap's edge (the chibi's shoulders are not far under its big head)
TAIL_THICK = 0.17                  # head radii across (half) the tail at its root
TAIL_OUT = 0.2                     # head radii its tip swings out from the head
TAIL_SIDES = 6                     # points round it

# The tailcap (Ideology's), as the game's sprite: a round fur cap with straight sides and a flat top, pulled down to the
# brows and over the ears, its fur's lower edge ragged, and an animal's tail hanging from its back on the figure's right
# down to the shoulder, darker than the cap. Coloured, but pale enough for the leather's colour to show, as the
# authority cap is.
def tailcap():
    s = Shape(TAILCAP_SEGMENTS)
    s.palette = {'cap': (0.95, 0.95, 0.95), 'tail': (0.6, 0.6, 0.6)}
    base = oval(band(*TAILCAP_BOTTOM), TAILCAP_OFF, s.n)
    z_top = top + TAILCAP_HEIGHT * R
    def ring(across, up):
        return [Vector((CENTRE.x, CENTRE.y, p.z + (z_top - p.z) * up)) + Vector((p.x - CENTRE.x, p.y - CENTRE.y, 0)) * across
                for p in base]
    s.ring([p + outward(p) * 0.03 * R - Vector((0, 0, TAILCAP_FRINGE[i % len(TAILCAP_FRINGE)] * R))
            for i, p in enumerate(base)], 'cap')
    for across, up in ((1.0, 0.0), (1.03, 0.3), (1.04, 0.72), (1.0, 0.92), (0.86, 0.99), (0.5, 1.0)):
        s.ring(ring(across, up), 'cap')
    s.tip = Vector((CENTRE.x, CENTRE.y, z_top + 0.01 * R))
    # The tail: a tube from just up the cap's outside, hanging down and swinging out toward its tip, closed at both ends.
    root = base[int(round(TAIL_AZ / 360.0 * s.n)) % s.n]
    out = outward(root)
    steps = 6
    path = [root + out * (TAIL_THICK + TAIL_OUT * t * t) * R + Vector((0, 0.06 * t * R, (0.2 - (TAIL_LENGTH + 0.2) * t) * R))
            for t in (i / steps for i in range(steps + 1))]
    points, faces = [path[0] + Vector((0, 0, 0.06 * R))], []
    k = TAIL_SIDES
    for i, c in enumerate(path):
        t = i / steps
        along = (path[min(i + 1, steps)] - path[max(i - 1, 0)]).normalized()
        u = (out - along * out.dot(along)).normalized()
        v = along.cross(u)
        r = TAIL_THICK * R * (1.0 + 0.15 * math.sin(math.pi * t) - 0.35 * t * t)
        points += [c + (u * math.cos(2 * math.pi * j / k) + v * math.sin(2 * math.pi * j / k)) * r for j in range(k)]
    points.append(path[-1] - Vector((0, 0, 0.1 * R)) + out * 0.03 * R)
    end = len(points) - 1
    faces += [[0, 1 + (j + 1) % k, 1 + j] for j in range(k)]
    faces += [[1 + i * k + j, 1 + i * k + (j + 1) % k, 1 + (i + 1) * k + (j + 1) % k, 1 + (i + 1) * k + j]
              for i in range(steps) for j in range(k)]
    faces += [[1 + steps * k + j, 1 + steps * k + (j + 1) % k, end] for j in range(k)]
    s.part(points, faces, 'tail')
    return made(s, 'Tailcap')

TORTURE_SEGMENTS = 24          # points round: a spike up at every other one from the front, one down between
TORTURE_BOTTOM = (16, 12, 6)   # the band's foot's elevation at the front, sides and back, degrees: round the forehead
TORTURE_OFF = 0.18             # head radii off the skin: it is worn over the hair, which it leaves on, as the coronet
TORTURE_BAND = 0.16            # head radii tall, the band
TORTURE_UP = 0.4               # head radii its spikes stand above the band (each a little more or less, TORTURE_SEED)
TORTURE_DOWN = 0.28            # head radii its spikes hang below the band
TORTURE_IN = 0.08              # head radii the hanging spikes' points lean in toward the head
TORTURE_SEED = 3

# The torture crown (Ideology's), as the game's sprite: a rough band round the forehead with spikes standing up all round
# its top and shorter ones hanging down from its bottom between them, the points leaning in at the head, each spike its
# own length. Coloured, the band paler than the spikes, under the stuff's colour.
def torture_crown():
    s = Shape(TORTURE_SEGMENTS)
    s.palette = {'band': (0.95, 0.95, 0.95), 'spike': (0.72, 0.72, 0.72)}
    base = oval(band(*TORTURE_BOTTOM), TORTURE_OFF, s.n)
    rough = random.Random(TORTURE_SEED)
    up = Vector((0, 0, 1))
    hang = [p - up * (TORTURE_DOWN * rough.uniform(0.75, 1.15) if i % 2 else 0.02) * R
            - outward(p) * (TORTURE_IN if i % 2 else 0.0) * R for i, p in enumerate(base)]
    band_top = [p + up * TORTURE_BAND * R for p in base]
    points = [p + up * (TORTURE_BAND + (TORTURE_UP * rough.uniform(0.8, 1.2) if i % 2 == 0 else 0.03)) * R
              + outward(p) * (0.03 if i % 2 == 0 else 0.0) * R for i, p in enumerate(base)]
    loops(s, [hang, base], 'spike')
    loops(s, [base, band_top], 'band')
    loops(s, [band_top, points], 'spike')
    return made(s, 'TortureCrown')

AIR_EAR_AZ, AIR_EAR_EL = -96, -16   # the unit's middle over the figure's right ear, as the gunlink's
AIR_OFF = 0.08                      # head radii it stands off the ear (the most the head reaches round there)
AIR_UNIT = (0.34, 0.46, 0.16)       # head radii: the unit's width (front to back), height and thickness
AIR_ROD_TOP = 0.55                  # head radii the antenna's top stands above the top of the head
AIR_ROD_WIDTH = 0.08                # head radii across the antenna
AIR_BARS = ((0.45, 0.24), (0.24, 0.17))   # its crossbars: head radii above the top of the head, and half their length
AIR_BAR_WIDTH = 0.06                # head radii across a crossbar
AIR_BOOM_EL = -10                   # degrees up the boom runs forward, from the unit's front
AIR_BOOM_HIGH = 5                   # degrees above and below that it is wide
AIR_BOOM_TO = -40                   # degrees round from the front it ends, short of the right eye's outer corner
AIR_BOOM_DROP = 0.16                # head radii its end turns down

# The airwire headset (Biotech), as the game's sprite: a grey unit over the right ear with a thin antenna standing up from
# its back past the top of the head, two crossbars near its top like a little pylon, and a white boom from the unit's
# front forward round the temple, its end turned down. Coloured, as it is not made from stuff: the game draws it as it is.
def airwire_headset():
    s = Shape()
    s.palette = {'unit': (0.42, 0.42, 0.44), 'rod': (0.34, 0.34, 0.36), 'boom': (0.94, 0.94, 0.94)}
    middle = CENTRE + way(AIR_EAR_AZ, AIR_EAR_EL) * (envelope(AIR_EAR_AZ, AIR_EAR_EL) + AIR_OFF * R)
    out = Vector((-1, 0, 0))
    forward = Vector((0, -1, 0))
    up = Vector((0, 0, 1))
    w, h, d = AIR_UNIT
    slab(middle, forward * w / 2 * R, up * h / 2 * R, out * d / 2 * R, 'unit', s)
    # The antenna: from inside the unit's top, toward its back, straight up, leaning in a little to keep near the head.
    foot = middle + up * h * 0.3 * R - forward * w * 0.25 * R
    rod_top = Vector((foot.x + 0.08 * R, foot.y, top + AIR_ROD_TOP * R))
    slab((foot + rod_top) / 2, forward * AIR_ROD_WIDTH / 2 * R, (rod_top - foot) / 2, out * AIR_ROD_WIDTH / 2 * R, 'rod', s)
    # Its crossbars: each a cross, front to back and side to side, so it reads as a pylon from every side.
    for above, half in AIR_BARS:
        at = foot + (rod_top - foot) * ((top + above * R - foot.z) / (rod_top.z - foot.z))
        b = AIR_BAR_WIDTH / 2 * R
        slab(at, forward * half * R, up * b, out * b, 'rod', s)
        slab(at, forward * b, up * b, out * half * R, 'rod', s)
    # The boom: a strip round the side of the head from the unit's front, laid off the skin (the most of it nearby, so it
    # rides over the cheek rather than into it), then a tab down at its end.
    n = 8
    points = []
    start = AIR_EAR_AZ + 10
    for i in range(n + 1):
        t = i / n
        az = start + (AIR_BOOM_TO - start) * t
        off = AIR_OFF + 0.04 * t
        for de in (AIR_BOOM_HIGH, -AIR_BOOM_HIGH):
            el = AIR_BOOM_EL + de
            points.append(CENTRE + way(az, el) * (over_skin(az, el, 4) + off * R))
    s.part(points, [[2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1] for i in range(n)], 'boom')
    tip = (points[-1] + points[-2]) / 2
    ahead = way(AIR_BOOM_TO, AIR_BOOM_EL)
    across = ahead.cross(up).normalized()
    slab(tip - up * AIR_BOOM_DROP / 2 * R + ahead * 0.02 * R, across * 0.06 * R, up * (AIR_BOOM_DROP / 2 + 0.05) * R,
         ahead * 0.04 * R, 'boom', s)
    return made(s, 'AirwireHeadset')

def bar(s, stations, wide, thick, colour, outer=None):
    """A flat bar along a path of points round the head (each the middle of its inside face), wide(i) head radii across
    at station i and thick head radii out from the head's middle, closed at both ends, as a part of s: its outside face
    in outer[i]'s colour from station i to the next (else colour), the rest in colour."""
    n = len(stations)
    corners = []
    for i, p in enumerate(stations):
        out = (p - CENTRE).normalized()
        along = (stations[min(i + 1, n - 1)] - stations[max(i - 1, 0)]).normalized()
        across = along.cross(out).normalized() * wide(i) / 2 * R
        o = out * thick * R
        corners += [p - across, p + across, p + across + o, p - across + o]
    faces, colours = [], []
    for i in range(n - 1):
        a, b = 4 * i, 4 * i + 4
        for k in range(4):
            faces.append([a + k, a + (k + 1) % 4, b + (k + 1) % 4, b + k])
            colours.append(outer[i] if outer and k == 2 else colour)
    faces += [[3, 2, 1, 0], [4 * n - 4, 4 * n - 3, 4 * n - 2, 4 * n - 1]]
    colours += [colour, colour]
    # Shape.part takes one colour a part, so each colour's faces go in as a part of their own over the same points.
    for c in set(colours):
        s.part(corners, [f for f, fc in zip(faces, colours) if fc == c], c)

ARRAY_REACH = 90          # degrees round either side of the front the visor goes, to over the ears
ARRAY_MID = by_azimuth((0, EYE_EL + 2), (35, EYE_EL + 2), (65, -19), (90, -13))   # the elevation of its middle
ARRAY_HALF = by_azimuth((0, 19), (45, 17), (90, 12))   # degrees above and below its middle it reaches
ARRAY_OVER = 5            # degrees each way the head is looked at round a point, so it goes over the nose and ears
ARRAY_OFF = 0.05          # head radii its inside stands off the skin
ARRAY_THICK = 0.08        # head radii thick
ARRAY_STRIPE = (0.12, 0.5)   # the white stripe across its outside, from and to (-1 its bottom edge, 1 its top)
ARRAY_POD = (0.36, 0.42, 0.12)   # head radii: each end's pod, front to back, high and thick
ARRAY_ROD = 1.05          # head radii long, each antenna
ARRAY_ROD_BACK = 35       # degrees back from upright it leans
ARRAY_ROD_WIDTH = 0.1     # head radii across it

# The array headset (Biotech), as the game's sprite: a dark visor wrapped round the front of the head over the eyes, a
# white stripe across its upper half, out to a pod over each ear, from which an antenna stands up leaning back. Hard,
# so a closed band with a thickness rather than a sheet. It covers the eyes, as the sprite does; the hair stays.
# Coloured, as it is not made from stuff.
def array_headset():
    s = Shape()
    s.palette = {'visor': (0.36, 0.36, 0.38), 'stripe': (0.94, 0.94, 0.94), 'rod': (0.3, 0.3, 0.32)}
    # Its cross-section, round from the inside of the bottom edge: (from bottom -1 to top 1, head radii off the skin).
    lo, hi = ARRAY_STRIPE
    section = [(-1, ARRAY_OFF), (-1, ARRAY_OFF + ARRAY_THICK), (lo, ARRAY_OFF + ARRAY_THICK),
               (hi, ARRAY_OFF + ARRAY_THICK), (1, ARRAY_OFF + ARRAY_THICK), (1, ARRAY_OFF)]
    k = len(section)
    n = 16
    points = []
    for c in range(n + 1):
        az = -ARRAY_REACH + 2 * ARRAY_REACH * c / n
        for r, off in section:
            el = ARRAY_MID(az) + ARRAY_HALF(az) * r
            points.append(CENTRE + way(az, el) * (over_skin(az, el, ARRAY_OVER) + off * R))
    faces = {'visor': [], 'stripe': []}
    for c in range(n):
        for j in range(k):
            jn = (j + 1) % k
            faces['stripe' if j == 2 else 'visor'].append([c * k + j, (c + 1) * k + j, (c + 1) * k + jn, c * k + jn])
    faces['visor'] += [list(range(k))[::-1], list(range(n * k, n * k + k))]
    for colour, fs in faces.items():
        s.part(points, fs, colour)
    up, backward = Vector((0, 0, 1)), Vector((0, 1, 0))
    for end in (0, n):
        az = -ARRAY_REACH + 2 * ARRAY_REACH * end / n
        el = ARRAY_MID(az)
        out = Vector((math.copysign(1, math.sin(math.radians(az))), 0, 0))
        # The pod: a block on the band's end over the ear, as far out as the ear reaches.
        w, h, d = ARRAY_POD
        pod = CENTRE + way(az, el) * (envelope(az, el) + (ARRAY_OFF + d / 2) * R) + backward * 0.05 * R
        slab(pod, backward * w / 2 * R, up * h / 2 * R, out * d / 2 * R, 'visor', s)
        # The antenna: from inside the pod's top, toward its back, up and leaning back.
        foot = pod + up * h * 0.3 * R + backward * w * 0.2 * R
        lean = (up * math.cos(math.radians(ARRAY_ROD_BACK)) + backward * math.sin(math.radians(ARRAY_ROD_BACK))) * ARRAY_ROD * R
        slab(foot + lean / 2, lean.cross(out).normalized() * ARRAY_ROD_WIDTH / 2 * R, lean / 2,
             out * ARRAY_ROD_WIDTH / 2 * R, 'rod', s)
    return made(s, 'ArrayHeadset')

INTEGRATOR_AZ = -86       # degrees round the band runs down the right side, just in front of the ear
INTEGRATOR_FROM = 78      # degrees up it starts, hooked in over the crown
INTEGRATOR_TO = -18       # and down to, at the ear
INTEGRATOR_OFF = 0.2      # head radii off the skin over the hair, which it leaves on, as the coronet
INTEGRATOR_EAR_OFF = 0.1  # and at the ear, where it comes in to the pad
INTEGRATOR_WIDTH = 0.2    # head radii across it
INTEGRATOR_THICK = 0.07
INTEGRATOR_SHINE = 30     # degrees up the white face down its outside starts, to its foot
INTEGRATOR_PAD = (0.34, 0.4, 0.1)   # head radii: the pad over the ear, front to back, high and thick
INTEGRATOR_BOOM = ((-84, -30), (-70, -41), (-52, -50), (-32, -54))   # the boom's path to the mic (azimuth, elevation)
INTEGRATOR_BOOM_OFF = 0.1  # head radii it stands off the skin
INTEGRATOR_BOOM_WIDTH = 0.1
INTEGRATOR_MIC = 0.17     # head radii across the ball at its end

# The integrator headset (Biotech), as the game's sprite: a bar hooked over the right of the crown and down the side of
# the head to a pad on the ear, its straight side white down its outside, then a darker boom curving forward round the
# jaw to a ball of a microphone by the right corner of the mouth. It leaves the hair and the face on. Coloured.
def integrator_headset():
    s = Shape()
    s.palette = {'band': (0.55, 0.55, 0.57), 'shine': (0.94, 0.94, 0.94), 'boom': (0.4, 0.4, 0.42), 'mic': (0.3, 0.3, 0.32)}
    n = 10
    ways, reach, outer = [], [], []
    for i in range(n + 1):
        t = i / n
        el = INTEGRATOR_FROM + (INTEGRATOR_TO - INTEGRATOR_FROM) * t
        off = INTEGRATOR_OFF + (INTEGRATOR_EAR_OFF - INTEGRATOR_OFF) * smoothstep((t - 0.6) / 0.4)
        ways.append(way(INTEGRATOR_AZ, el))
        reach.append(over_skin(INTEGRATOR_AZ, el, 6) + off * R)
        outer.append('shine' if el < INTEGRATOR_SHINE else 'band')
    # Smoothed along it, as a hard bar doesn't follow every facet of the low-poly head.
    for _ in range(12):
        reach = [reach[0]] + [(reach[i - 1] + 2 * reach[i] + reach[i + 1]) / 4 for i in range(1, n)] + [reach[n]]
    stations = [CENTRE + w * d for w, d in zip(ways, reach)]
    bar(s, stations, lambda i: INTEGRATOR_WIDTH, INTEGRATOR_THICK, 'band', outer)
    # The pad over the ear, as far out as the ear reaches, faced along the head's side.
    az, el = INTEGRATOR_AZ - 6, INTEGRATOR_TO - 8
    out = way(az, el)
    w, h, d = INTEGRATOR_PAD
    pad = CENTRE + out * (envelope(az, el) + (INTEGRATOR_EAR_OFF - 0.02 + d / 2) * R)
    forward = out.cross(Vector((0, 0, 1))).normalized()
    if forward.y > 0:
        forward = -forward
    slab(pad, forward * w / 2 * R, out.cross(forward).normalized() * h / 2 * R, out * d / 2 * R, 'band', s)
    # The boom: from the pad's front round the jaw, laid off the most of the skin nearby, so it rides over the cheek.
    booms = [CENTRE + way(a, e) * (over_skin(a, e, 6) + INTEGRATOR_BOOM_OFF * R) for a, e in INTEGRATOR_BOOM]
    bar(s, booms, lambda i: INTEGRATOR_BOOM_WIDTH * (1 - 0.3 * i / (len(booms) - 1)), INTEGRATOR_BOOM_WIDTH * 0.7, 'boom')
    # The microphone: a ball on the boom's end.
    a, e = INTEGRATOR_BOOM[-1]
    mic = CENTRE + way(a, e) * (over_skin(a, e, 6) + (INTEGRATOR_BOOM_OFF + INTEGRATOR_MIC * 0.4) * R)
    orb(s, mic, INTEGRATOR_MIC, 'mic')
    return made(s, 'IntegratorHeadset')

def neighbours(count, faces):
    """Each point's neighbours along the faces' edges."""
    near = [set() for _ in range(count)]
    for f in faces:
        for a, b in zip(f, f[1:] + f[:1]):
            near[a].add(b)
            near[b].add(a)
    return near

def face_sheet(outline, holes, step, off, over, passes, bump=lambda az, el: 0.0):
    """A hard sheet over the face cut to an outline less holes (cut_out's (azimuth, elevation) polygons): its points'
    (az, el), its faces and their places, off head radii out from the most skin nearby (over_skin's over degrees), that
    reach smoothed over the sheet passes times but never let in (so it bridges the hollows round the nose and eyes as a
    rigid mask does, rather than following each facet), plus bump(az, el) head radii."""
    points, faces = cut_out(outline, holes, step, centred=True)
    raw = [over_skin(az, el, over) for az, el in points]
    near = neighbours(len(points), faces)
    reach = raw[:]
    for _ in range(passes):
        reach = [max(raw[i], (reach[i] + sum(reach[j] for j in near[i]) / len(near[i])) / 2) if near[i] else reach[i]
                 for i in range(len(points))]
    places = [CENTRE + way(az, el) * (d + (off + bump(az, el)) * R) for (az, el), d in zip(points, reach)]
    return points, faces, places

def thick_sheet(s, places, faces, thick, colour=None, inside=True):
    """A sheet given a thickness: its places as the outside, a copy thick head radii in toward the head's middle as the
    inside, and the open edges joined round between them, as a part of s. Without inside, the inner copy has no faces of
    its own (only the rim reaches it), for a sheet whose inside lies against the face and is never seen."""
    n = len(places)
    inner = [p - (p - CENTRE).normalized() * thick * R for p in places]
    edges = {}
    for f in faces:
        for a, b in zip(f, f[1:] + f[:1]):
            edges[(a, b)] = edges.get((a, b), 0) + 1
    rim = [[a, b, b + n, a + n] for (a, b) in edges if (b, a) not in edges]
    back = [[i + n for i in reversed(f)] for f in faces] if inside else []
    s.part(places + inner, [list(f) for f in faces] + back + rim, colour)

def ellipse(az, el, half_az, half_el, count):
    """A polygon round (az, el), half_az by half_el degrees, of count points."""
    return [(az + half_az * math.cos(2 * math.pi * i / count), el + half_el * math.sin(2 * math.pi * i / count))
            for i in range(count)]

def lathe(s, base, axis, profile, colours, cap, n=12):
    """A round part along axis from base: profile is (head radii along, head radii across) from its foot, each piece from
    one entry to the next in colours' colour; its far end closed by a flat fan in cap's colour."""
    axis = axis.normalized()
    side = axis.cross(Vector((0, 0, 1)))
    if side.length < 1e-6:
        side = Vector((1, 0, 0))
    side.normalize()
    other = axis.cross(side)
    rings = [[base + (axis * along + (side * math.cos(2 * math.pi * i / n) + other * math.sin(2 * math.pi * i / n)) * width) * R
              for i in range(n)] for along, width in profile]
    for r, colour in enumerate(colours):
        loops(s, rings[r:r + 2], colour)
    end = rings[-1]
    s.part(end + [base + axis * profile[-1][0] * R], [[i, (i + 1) % n, n] for i in range(n)], cap)

VISAGE_OUTLINE = both_sides([(0, 22), (28, 23), (46, 20), (57, 10), (62, -8), (61, -32), (54, -50), (40, -60), (20, -67),
                             (0, -70)])   # its right half, as both_sides wants, a shield: a flat brow, upright sides, a point at the chin
VISAGE_EYE = (13, 10)  # degrees, the half width and half height of its eye holes, at EYE_AZ, EYE_EL + 2
VISAGE_OFF = 0.08      # head radii its outside stands off the skin
VISAGE_THICK = 0.05    # head radii thick
VISAGE_NOSE = 0.1      # head radii its nose stands out, from VISAGE_NOSE_EL's first to its second
VISAGE_NOSE_EL = (-46, -14)
VISAGE_NOSE_WIDE = 11  # degrees either side of the middle it falls off over

# The visage mask (Ideology), as the game's sprite (its second variant, round eyes; its three variants are one garment
# here): a rigid shield over the face carved from wood or beaten from metal, so plain and drawn in the stuff's colour,
# with a flat brow, upright sides and a point at the chin, a nose standing out down its middle and two round eye holes,
# which the pawn's own eyes look out of. A thin strap round the back of the head holds it, over the ears' tops.
def visage_mask():
    s = Shape()
    def nose(az, el):
        lo, hi = VISAGE_NOSE_EL
        return VISAGE_NOSE * math.exp(-(az / VISAGE_NOSE_WIDE) ** 2) * smoothstep((el - lo) / 10) * smoothstep((hi - el) / 10)
    holes = [ellipse(side * EYE_AZ, EYE_EL + 2, VISAGE_EYE[0], VISAGE_EYE[1], 10) for side in (1, -1)]
    points, faces, places = face_sheet(VISAGE_OUTLINE, holes, 10, VISAGE_OFF, 12, 24, nose)
    thick_sheet(s, places, faces, VISAGE_THICK, inside=False)   # its inside lies on the face, never seen
    strap(s, 58, by_azimuth((58, 6), (90, 10), (180, 12)), 5, 0.05)
    return made(s, 'VisageMask')

GAS_OUTLINE = both_sides([(0, 16), (24, 18), (44, 13), (60, 2), (69, -18), (68, -42), (60, -60), (44, -74), (22, -84),
                          (0, -87)])   # its right half: from over the brows round to before the ears, and under the chin
GAS_OFF = 0.07          # head radii its outside stands off the skin
GAS_THICK = 0.05        # head radii thick
GAS_LENS = (15, 13)     # degrees, the half width and half height of each lens, at EYE_AZ, GAS_LENS_EL
GAS_LENS_EL = -23
GAS_RIM = (0.07, 1.16)  # its rim: head radii it stands out of the mask, and how much wider than the lens its lip is
GAS_GLASS = 0.025       # head radii the glass stands out of the mask, inside the rim
GAS_SNOUT_AT = -56      # degrees down the front the snout comes out of the mask, by the mouth
GAS_SNOUT_WAY = -32     # degrees below level it points, forward and down
# The snout and canister along it from the mask (head radii along, head radii across), and each piece's colour.
GAS_CANISTER = [(-0.05, 0.25), (0.14, 0.2), (0.15, 0.26), (0.23, 0.26), (0.24, 0.28), (0.52, 0.28), (0.53, 0.24)]
GAS_PIECES = ['rubber', 'rubber', 'band', 'band', 'filter', 'filter']
GAS_STRAP_WIDE = 0.075  # head radii, half the width of the strap over the crown
GAS_STRAP_OFF = 0.06    # head radii the straps stand off the skin

# The gas mask (Biotech), as the game's sprite: a dark rubber face piece from over the brows to under the chin and round
# to before the ears, two big round lenses in raised rims, of glass (the Visor material), so the pawn's eyes show behind
# them, and a snout from the mouth forward and down to a round filter canister with a brass band, its end a dark grille.
# A harness holds it: straps round the head above and below the ears and one over the crown from the brow to the back.
# Coloured, as it is not made from stuff: the game draws it as it is.
def gas_mask():
    s = Shape()
    s.palette = {'rubber': (0.22, 0.22, 0.23), 'rim': (0.3, 0.3, 0.31), 'band': (0.55, 0.4, 0.22),
                 'filter': (0.3, 0.3, 0.31), 'grille': (0.1, 0.1, 0.1), 'strap': (0.16, 0.16, 0.17)}
    lenses = [ellipse(side * EYE_AZ, GAS_LENS_EL, GAS_LENS[0], GAS_LENS[1], 12) for side in (1, -1)]
    points, faces, places = face_sheet(GAS_OUTLINE, lenses, 10, GAS_OFF, 10, 8)
    thick_sheet(s, places, faces, GAS_THICK, 'rubber')
    # The lenses: cut_out puts the holes' points straight after the outline's, in order, so their places are the mask's.
    start = len(GAS_OUTLINE)
    for lens in lenses:
        edge = places[start:start + len(lens)]
        start += len(lens)
        middle = sum(edge, Vector()) / len(edge)
        out = (middle - CENTRE).normalized()
        high, lip = GAS_RIM
        top = [p + out * high * R for p in edge]
        loops(s, [edge, top, [middle + (p - middle) * lip for p in top], [middle + (p - middle) * lip for p in edge]], 'rim')
        glass = [p + out * GAS_GLASS * R for p in edge]
        s.part(glass + [middle + out * (GAS_GLASS + 0.02) * R], [[i, (i + 1) % len(glass), len(glass)] for i in range(len(glass))], GLASS)
    # The snout and canister.
    base = CENTRE + way(0, GAS_SNOUT_AT) * (over_skin(0, GAS_SNOUT_AT, 10) + GAS_OFF * R)
    lathe(s, base, way(0, GAS_SNOUT_WAY), GAS_CANISTER, GAS_PIECES, 'grille')
    # The harness.
    strap(s, 62, by_azimuth((62, 2), (90, 8), (180, 14)), 7, GAS_STRAP_OFF, colour='strap')
    strap(s, 66, by_azimuth((66, -44), (100, -50), (180, -46)), 7, GAS_STRAP_OFF, colour='strap')
    over = []
    for i in range(11):   # up the front from the mask's top, over the crown and down the back to the band round the head
        el = 12 + (180 - 14 - 12) * i / 10   # past 90, way() points back
        c = snug(0, el, GAS_STRAP_OFF)
        over += [c - Vector((GAS_STRAP_WIDE * R, 0, 0)), c + Vector((GAS_STRAP_WIDE * R, 0, 0))]
    s.part(over, [[2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1] for i in range(10)], 'strap')
    return made(s, 'GasMask')

# The broadwrap's columns round the head (degrees from the front, round by the figure's left), those at WRAP_HOLE_AZ
# and in being the face's opening's sides, and its rings up the head (elevations), WRAP_HOLE's two its bottom and top.
WRAP_AZS = [0, 17.5, 35, 52.5, 70, 90, 112.5, 135, 157.5, 180, 202.5, 225, 247.5, 270, 290, 307.5, 325, 342.5]
WRAP_HOLE_AZ = 52.5
WRAP_RINGS = (-74, -66, -48, -30, -12, 4, 14, 34, 54, 72)
WRAP_HOLE = (-66, 14)
WRAP_OVAL = (-26, 40)   # the opening's middle and half height (degrees), its corners drawn in onto that oval
WRAP_OFF = 0.08         # head radii it stands off the skin
WRAP_OVER = 10          # degrees round a point the skin is looked at, so it goes over the nose, the ears and facets
WRAP_LIP = 0.04         # head radii further out round the opening's edge, turned back
WRAP_HANG = 0.08        # head radii each ring down the sides and back comes in on the one above, hanging from the head
WRAP_HEM = by_azimuth((0, 1.55), (90, 1.42), (180, 1.55))   # head radii below the head's middle the hem is
WRAP_HEM_OUT = 0.1      # head radii out from the body round the hem, over the shoulders
WRAP_CLEAR = 0.08       # head radii it is kept off the body between the jaw and the hem
WRAP_FLARE = 0.14       # head radii the hem spreads out past the jaw's ring, onto the shoulders
WRAP_NECK = 0.06        # head radii it comes in round the neck, halfway down
WRAP_SKIRT = 3          # rings between the jaw and the hem

body_tree = None
def body_reach(az, z):
    """How far out from the head's upright axis the body is at height z that way (world units): the first surface met
    coming in from outside, so the outermost of the neck, the shoulders and the arms' tops."""
    global body_tree
    if body_tree is None:
        body_tree = BVHTree.FromPolygons(verts, [tuple(p.vertices) for p in body.data.polygons])
    d = Vector((math.sin(math.radians(az)), -math.cos(math.radians(az)), 0))
    far = 6 * R
    hit = body_tree.ray_cast(Vector((CENTRE.x, CENTRE.y, z)) + d * far, -d, far)
    return far - hit[3] if hit[0] is not None else 0.0

# The broadwrap (Ideology), as the game's sprite: a cloth wrapped over the whole head and the neck to the shoulders, an
# oval opening for the face with its edge turned back. Down the sides and back it hangs from the head's widest, and in
# front from the chin, rather than following the skin in under the jaw, and below the head it falls over the shoulders to a hem round their tops.
# Plain, drawn in the cloth's colour, and soft: it takes the body's weights, as the hood's cowl does.
def broadwrap():
    s = Shape()
    wrap_cloth(s)
    return made(s, 'Broadwrap')

# The burka's head piece (Ideology's burka covers the whole head): the broadwrap's cloth closed over the face but for a
# slit over the eyes, the face below it hanging as a veil from the slit's bottom edge. Its columns are closer together
# round the front than the broadwrap's, so the slit's sides come where the eyes end, and its rings are closer round the
# eyes (EYE_EL), the slit's top and bottom (BURKA_SLIT) and one through its middle, where the sides stay out at
# BURKA_SLIT_AZ while the corners are drawn in onto the oval, an almond. It stands further off the body below the jaw than
# the broadwrap, clear of the burka's own collar, which make_duster.py lays at the Shell layer's offset, and falls a little
# lower at the front and back. Not a garment of its own: make_duster.py --burka joins it onto the burka.
BURKA_AZS = [0, 15, 30, 45, 67.5, 90, 112.5, 135, 157.5, 180, 202.5, 225, 247.5, 270, 292.5, 315, 330, 345]
BURKA_SLIT_AZ = 45
BURKA_SLIT = (-36, -16)
BURKA_RINGS = (-74, -64, -50, -36, -26, -16, 4, 20, 38, 56, 72)
BURKA_OVAL = (-26, 10)
BURKA_CLEAR = 0.15       # head radii it is kept off the body between the jaw and the hem (the burka stands about 0.09 off)
BURKA_HEM_OUT = 0.18     # and round the hem
BURKA_HEM = by_azimuth((0, 1.75), (90, 1.45), (180, 1.75))
# Degrees round a point the skin is looked at, by azimuth: wide over the face, so the cloth hangs flat from the brow over
# the nose (the slit's top and bottom edges as far out as each other, not a ledge under the eyes), the broadwrap's from the
# cheeks round.
BURKA_OVER = by_azimuth((0, 24), (40, 22), (75, WRAP_OVER), (180, WRAP_OVER))

def burka_hood():
    s = Shape()
    wrap_cloth(s, hem_out=BURKA_HEM_OUT, azs=BURKA_AZS, rings=BURKA_RINGS, hole=BURKA_SLIT, hole_az=BURKA_SLIT_AZ,
               oval=BURKA_OVAL, clear=BURKA_CLEAR, neck=0.0, hem_at=BURKA_HEM, over=BURKA_OVER)
    return made(s, 'BurkaHood')

def wrap_cloth(s, off=WRAP_OFF, flare=WRAP_FLARE, hem_out=WRAP_HEM_OUT, peak=0.0, peak_back=0.0, bell=None, open_down=False,
               colour=None, azs=WRAP_AZS, rings=WRAP_RINGS, hole=WRAP_HOLE, hole_az=WRAP_HOLE_AZ, oval=WRAP_OVAL,
               clear=WRAP_CLEAR, neck=WRAP_NECK, hem_at=WRAP_HEM, over=lambda az: WRAP_OVER):
    """The broadwrap's cloth, as a part of s in colour (if any): off head radii off the skin, flare and hem_out as
    WRAP_FLARE and WRAP_HEM_OUT. With a peak, the crown is drawn up into a point peak head radii over the head and
    peak_back back; with a bell (a power, under 1 bowing it out), the cloth over the head and below it is stood out to a
    cone from that point to the hem, so it falls straight from the peak to the shoulders rather than over the skull.
    Open down, the face's opening runs on down the front to the hem. azs, rings, hole, hole_az and oval stand for
    WRAP_AZS, WRAP_RINGS, WRAP_HOLE, WRAP_HOLE_AZ and WRAP_OVAL (the burka's hood has only a slit over the eyes), clear,
    neck and hem_at for WRAP_CLEAR, WRAP_NECK and WRAP_HEM, and over(az) for WRAP_OVER."""
    n = len(azs)
    signed = [a - 360 if a > 180 else a for a in azs]
    in_hole = [abs(a) <= hole_az + 1e-6 for a in signed]
    lo, hi = rings.index(hole[0]), rings.index(hole[1])
    mid, half = oval
    def on_edge(c, r):
        if not in_hole[c] or not lo <= r <= hi:
            return False
        return r in (lo, hi) or abs(signed[c]) > hole_az - 1e-6
    def lifted(p, el):
        t = max(0.0, el) / 90.0
        return p + Vector((0, peak_back * R * t ** 3, peak * R * t ** 2))
    # The head's rings, from the jaw up: each point off the skin, then down the sides and back hung from the ring above.
    head = []   # rings of (place, horizontal reach from the upright axis)
    for r in range(len(rings) - 1, -1, -1):
        ring = []
        for c, az in enumerate(signed):
            el = rings[r]
            if on_edge(c, r):   # drawn in onto the oval, and turned back
                u, v = az / hole_az, (el - mid) / half
                k = max(1.0, math.hypot(u, v))
                az, el = u / k * hole_az, v / k * half + mid
            p = CENTRE + way(az, el) * (over_skin(az, el, over(az)) + (off + (WRAP_LIP if on_edge(c, r) else 0.0)) * R)
            p = lifted(p, el)
            flat = Vector((p.x - CENTRE.x, p.y - CENTRE.y, 0))
            reach = flat.length
            if head and rings[r] < 0:
                # Below the opening, the front hangs too: straight down from the chin, rather than in under it.
                share = 1.0 if r < lo else smoothstep((abs(signed[c]) - 45) / 35)
                hang = share * (head[-1][c][1] - WRAP_HANG * R - reach)
                if hang > 0:
                    reach += hang
                    p = Vector((CENTRE.x, CENTRE.y, p.z)) + flat.normalized() * reach
            ring.append((p, reach))
        head.append(ring)
    head.reverse()   # from the jaw up
    tip_place = lifted(snug(0, 90, off), 90)
    # Below it, rings from the jaw down to the hem, clear of the body, out over the shoulders.
    jaw = head[0]
    hem = []
    for c, az in enumerate(signed):
        z = CENTRE.z - hem_at(az) * R
        hem.append((z, max(jaw[c][1] + flare * R, body_reach(az, z) + hem_out * R)))
    def cone(c, z):
        """How far out the bell's cone is that way at height z (horizontal, world units)."""
        hz, hr = hem[c]
        share = max(0.0, min(1.0, (tip_place.z - z) / (tip_place.z - hz)))
        return hr * share ** bell
    def stood_out(c, p, reach):
        """p stood out to the bell's cone, if it is inside it."""
        if bell is None:
            return p, reach
        want = cone(c, p.z)
        if want <= reach:
            return p, reach
        az = signed[c]
        d = Vector((math.sin(math.radians(az)), -math.cos(math.radians(az)), 0))
        flat = Vector((p.x - tip_place.x, p.y - tip_place.y, 0))
        return Vector((tip_place.x, tip_place.y, p.z)) + d * want + (flat - d * flat.dot(d)), want
    if bell is not None:
        for r, ring in enumerate(head):
            for c in range(n):
                if not on_edge(c, r) and not (in_hole[c] and lo <= r <= hi):
                    ring[c] = stood_out(c, *ring[c])
    skirt = []
    for k in range(WRAP_SKIRT + 1, 0, -1):   # the hem first
        t = k / (WRAP_SKIRT + 1)
        ring = []
        for c, az in enumerate(signed):
            (j, jr), (hz, hr) = jaw[c], hem[c]
            z = j.z + (hz - j.z) * t
            reach = max(jr + (hr - jr) * smoothstep(t) - (0.0 if bell is not None else neck * R * math.sin(math.pi * t)),
                        body_reach(az, z) + clear * R)
            p = Vector((CENTRE.x, CENTRE.y, z)) + Vector((math.sin(math.radians(az)), -math.cos(math.radians(az)), 0)) * reach
            ring.append(stood_out(c, p, reach)[0])
        skirt.append(ring)
    rows = skirt + [[p for p, _ in ring] for ring in head]
    first = len(skirt)   # the jaw's ring
    points = [p for row in rows for p in row]
    tip = len(points)
    points.append(tip_place)
    faces = []
    for r in range(len(rows) - 1):
        for c in range(n):
            d = (c + 1) % n
            if in_hole[c] and in_hole[d] and (first + lo <= r or open_down) and r < first + hi:
                continue   # the face's opening
            faces.append([r * n + c, r * n + d, (r + 1) * n + d, (r + 1) * n + c])
    last = (len(rows) - 1) * n
    faces += [[last + c, last + (c + 1) % n, tip] for c in range(n)]
    s.part(points, faces, colour)

COLLAR_COLUMNS = 24          # points round the band (15 degrees apart, one at each side for the plates)
COLLAR_BOTTOM = band(-0.18, -0.1, -0.08)   # head radii its bottom edge is above the neck's joint, front, sides and back
COLLAR_HIGH = 0.11           # head radii tall: the chibi's neck is short, the chin overhanging the front of it
COLLAR_GAP = 0.03            # head radii its inside stands off the skin
COLLAR_THICK = 0.06          # head radii thick
COLLAR_OVER = 8              # degrees each way round the neck is looked at round a point, so it rides over its facets
COLLAR_PLATE_AZ = 80         # degrees round from the front each side plate's middle is
COLLAR_PLATE = (22, 0.75, 0.025)   # each plate: degrees round either side of its middle, its share of the band's height, and head radii proud
COLLAR_RIVET = 0.05          # head radii across the rivet in each plate's middle
COLLAR_RING = (0.06, 0.016)  # head radii: the ring's radius and its metal's
COLLAR_RING_DROP = 0.35      # of the band's height its middle is below the band's middle, so it hangs from it

body_tree = None
def neck_skin(az, z, axis):
    """How far the skin is from the neck's upright axis (a point on it at the neck's joint) at height z, that way: the
    most in a small spread round it (world units)."""
    global body_tree
    if body_tree is None:
        body_tree = BVHTree.FromPolygons(verts, [tuple(p.vertices) for p in body.data.polygons])
    reach = 0.0
    for da in (-COLLAR_OVER, 0, COLLAR_OVER):
        d = way(az + da, 0)
        o = Vector((axis.x, axis.y, z)) + d * 4 * R
        hit = body_tree.ray_cast(o, -d)
        if hit[0] is not None:
            reach = max(reach, 4 * R - hit[3])
    return reach

# The slave collar (Ideology), as the game's sprite: a plain band round the neck, lower at the front, where it dips
# under the chin, than at the back, with a grey plate on either side riveted at its front end, and a ring hanging at
# the throat. Coloured, though made from stuff: the band is near white, so the stuff's colour shows on it, and the
# metal is grey under it. Unlike the hats it is not built round the head: round the neck's own upright axis (halfway
# across it between its front and back skin), and it takes the body's weights, so it bends with the neck and grows with
# a fat or hulking neck (export_frames.py's GARMENT_PROPS).
def slave_collar():
    s = Shape()
    s.palette = {'band': (0.93, 0.93, 0.93), 'plate': (0.72, 0.72, 0.74), 'rivet': (0.18, 0.18, 0.2), 'ring': (0.6, 0.6, 0.63)}
    joint = rig.matrix_world @ rig.data.bones[walk.NECK].tail_local
    middle = joint.z + (COLLAR_BOTTOM(0) + COLLAR_BOTTOM(180)) / 2 * R + COLLAR_HIGH / 2 * R
    probe = Vector((0, joint.y, 0))
    ahead, behind = neck_skin(0, middle, probe), neck_skin(180, middle, probe)
    axis = Vector((0, joint.y + (behind - ahead) / 2, 0))
    n = COLLAR_COLUMNS
    azs = [360.0 * i / n for i in range(n)]
    lows = [joint.z + COLLAR_BOTTOM(az) * R for az in azs]
    highs = [z + COLLAR_HIGH * R for z in lows]
    # The inside at the bottom and top edges: off the skin, smoothed round the ring, never into it.
    inner = []
    for zs in (lows, highs):
        raw = [neck_skin(az, z, axis) + COLLAR_GAP * R for az, z in zip(azs, zs)]
        r = list(raw)
        for _ in range(4):
            r = [max(raw[i], (r[i - 1] + 2 * r[i] + r[(i + 1) % n]) / 4) for i in range(n)]
        inner.append(r)
    at = lambda i, z, r: Vector((axis.x, axis.y, z)) + way(azs[i], 0) * r
    ib = [at(i, lows[i], inner[0][i]) for i in range(n)]
    it = [at(i, highs[i], inner[1][i]) for i in range(n)]
    ob = [at(i, lows[i], inner[0][i] + COLLAR_THICK * R) for i in range(n)]
    ot = [at(i, highs[i], inner[1][i] + COLLAR_THICK * R) for i in range(n)]
    # A closed band: round its section from the inside of the bottom edge, out, up, in and down again.
    section = [ib, ob, ot, it]
    s.part([p for r in section for p in r],
           [[k * n + i, ((k + 1) % 4) * n + i, ((k + 1) % 4) * n + (i + 1) % n, k * n + (i + 1) % n]
            for k in range(4) for i in range(n)], 'band')
    def face_at(az, share):
        """The point on the band's outside at az (degrees), share of the way up it, and the level way out there."""
        i = int(az // (360.0 / n)) % n
        j = (i + 1) % n
        t = (az % (360.0 / n)) / (360.0 / n)
        p = ob[i].lerp(ob[j], t).lerp(ot[i].lerp(ot[j], t), share)
        return p, Vector((p.x - axis.x, p.y - axis.y, 0)).normalized()
    up = Vector((0, 0, 1))
    # A plate on either side: a strip curved round the band's outside, standing proud of it, the rivet in its middle.
    half, share, proud = COLLAR_PLATE
    for mid in (COLLAR_PLATE_AZ, 360 - COLLAR_PLATE_AZ):
        steps = 4
        points = []
        for c in range(steps + 1):
            az = mid - half + 2 * half * c / steps
            low, out = face_at(az, (1 - share) / 2)
            high, _ = face_at(az, (1 + share) / 2)
            lift = out * proud * R
            points += [low - out * 0.01 * R, low + lift, high + lift, high - out * 0.01 * R]
        faces = [[c * 4 + j, c * 4 + j + 1, c * 4 + 5 + j, c * 4 + 4 + j] for c in range(steps) for j in range(3)]
        faces += [[3, 2, 1, 0], [steps * 4, steps * 4 + 1, steps * 4 + 2, steps * 4 + 3]]
        s.part(points, faces, 'plate')
        q, qout = face_at(mid, 0.5)
        orb(s, q + qout * (proud + COLLAR_RIVET * 0.2) * R, COLLAR_RIVET, 'rivet')
    # The ring at the throat: a torus facing forward, against the band's outside.
    p, out = face_at(0, 0.5 - COLLAR_RING_DROP)
    big, small = COLLAR_RING
    middle = p + out * small * R
    across = out.cross(up).normalized()
    m, k = 10, 4
    points = []
    for a in range(m):
        ta = 2 * math.pi * a / m
        spoke = across * math.cos(ta) + up * math.sin(ta)
        for b in range(k):
            tb = 2 * math.pi * b / k
            points.append(middle + spoke * (big + small * math.cos(tb)) * R + out * small * math.sin(tb) * R)
    s.part(points, [[a * k + b, a * k + (b + 1) % k, ((a + 1) % m) * k + (b + 1) % k, ((a + 1) % m) * k + b]
                    for a in range(m) for b in range(k)], 'ring')
    print('COLLAR axis y %.3f, front %.3f..%.3f, back %.3f..%.3f (world units), inside %.3f..%.3f from the axis'
          % (axis.y, lows[0], highs[0], lows[n // 2], highs[n // 2], min(inner[0] + inner[1]), max(inner[0] + inner[1])))
    return made(s, 'Collar')

def slanted(az, el, half_long, half_wide, tilt, count):
    """A polygon round (az, el), half_long by half_wide degrees, its long way tipped tilt degrees up from level."""
    c, k = math.cos(math.radians(tilt)), math.sin(math.radians(tilt))
    pts = []
    for i in range(count):
        a = 2 * math.pi * i / count
        x, y = half_long * math.cos(a), half_wide * math.sin(a)
        pts.append((az + x * c - y * k, el + x * k + y * c))
    return pts

def skull_face(az, el, brow_el, brow, cheek):
    """The skull masks' relief: a ridge across the brow over each eye (brow head radii, at brow_el), cheekbones out
    under the eyes (cheek head radii), each falling off smoothly."""
    ridge = brow * math.exp(-((el - brow_el) / 7) ** 2) * smoothstep((58 - abs(az)) / 14) * smoothstep((abs(az) - 3) / 6)
    bone = cheek * math.exp(-((abs(az) - 42) / 13) ** 2 - ((el - (EYE_EL - 18)) / 11) ** 2)
    return ridge + bone

# The eye sockets, tipped up toward the outside so the brows above them scowl, and the nose hole: the ritual mask's and
# the hood's mask's alike, so the pawn's own eyes look out of them.
CULT_EYE = (15, 6, 20)   # degrees: the half length and half width of each socket at EYE_AZ, EYE_EL + 3, and its tip
CULT_NOSE = [(0, -37), (6, -41), (5, -47), (0, -52), (-5, -47), (-6, -41)]
def cult_holes():
    return [slanted(side * EYE_AZ, EYE_EL + 3, CULT_EYE[0], CULT_EYE[1], side * CULT_EYE[2], 12) for side in (1, -1)] + [CULT_NOSE]

# Its right half (both_sides): a dome over the forehead, nicked in at the temples, the cheeks out past the eyes, and four
# fangs along the bottom, the outer pair shorter, notched in the middle under the nose.
CULT_OUTLINE = both_sides([(0, 60), (24, 57), (44, 47), (56, 34), (60, 24), (66, 16), (70, -4), (69, -26), (64, -46),
                           (60, -58), (54, -80), (44, -66), (34, -72), (24, -88), (14, -74), (6, -78), (0, -72)])
CULT_OFF = 0.1      # head radii its outside stands off the skin
CULT_THICK = 0.06   # head radii thick
CULT_BROW = 0.06    # head radii its brow ridges stand out
CULT_CHEEK = 0.05   # and its cheekbones
CULT_HANG = 0.02    # head radii its fangs hang in front of the chin
TEETH_FROM = -62    # degrees down the front the masks' teeth start to hang straight down rather than curl in under the chin
TEETH_LONG = 0.5    # head radii their tips (at -88 degrees) hang below TEETH_FROM

def hang_teeth(points, places, off, ahead, long=TEETH_LONG):
    """Below TEETH_FROM, a face sheet's points (off head radii off the skin) hang straight down from where the sheet is
    at TEETH_FROM, long head radii by -88 degrees, and at least ahead head radii in front of the chin there, rather than
    following the skin in under it and drawing in to a point under the head's middle, so the teeth hang as the sprite's
    do and keep their width. The places are changed in place."""
    tops = {}
    def top(az):
        if az not in tops:
            tops[az] = CENTRE + way(az, TEETH_FROM) * (over_skin(az, TEETH_FROM, 12) + off * R)
        return tops[az]
    chin = min(top(az).y for az in range(-30, 31, 10))
    for i, ((az, el), p) in enumerate(zip(points, places)):
        if el < TEETH_FROM:
            t = (TEETH_FROM - el) / (TEETH_FROM + 88)
            q = top(az)
            places[i] = Vector((q.x, min(q.y, chin) - ahead * R * smoothstep(t), q.z - long * R * t))

# here): a skull's face shaped from bioferrite, so plain and drawn in the stuff's colour, from a dome over the forehead
# to four fangs below the chin, with scowling brows over slanted eye sockets, which the pawn's own eyes look out of, and
# a nose hole. A strap round the back of the head holds it, over the ears' tops.
def cultist_mask():
    s = Shape()
    points, faces, places = face_sheet(CULT_OUTLINE, cult_holes(), 9, CULT_OFF, 12, 24,
                                       lambda az, el: skull_face(az, el, EYE_EL + 15, CULT_BROW, CULT_CHEEK))
    hang_teeth(points, places, CULT_OFF, CULT_HANG)
    thick_sheet(s, places, faces, CULT_THICK, inside=False)
    strap(s, 66, by_azimuth((66, 8), (90, 10), (180, 12)), 6, 0.05)
    return made(s, 'CultistMask')

HOOD_PEAK_UP = 0.5      # head radii the ceremonial hood's point stands above the top of the head
HOOD_PEAK_BACK = 0.12   # and back
HOOD_BELL = 0.7         # its cone's bow: under 1 it bows out, a bell rather than a straight cone
HOOD_OFF = 0.12         # head radii it stands off the skin round the face
HOOD_FLARE = 0.6        # head radii its hem spreads out past the jaw's ring
HOOD_HEM_OUT = 0.45     # head radii out from the body round the hem
# The mask inside it (its right half): two lobes over the brow with a dip between, round the cheeks, and teeth hanging
# down from under the nose over the chin and the cloth below it, long and close together.
WHISPER_OUTLINE = both_sides([(0, 6), (10, 12), (24, 14), (38, 8), (50, -6), (56, -24), (55, -44), (50, -60),
                              (46, -82), (40, -68), (34, -86), (27, -70), (20, -88), (13, -71), (7, -88), (0, -72)])
WHISPER_OFF = 0.07     # head radii it stands off the skin: inside the hood's edge (HOOD_OFF and WRAP_LIP)
WHISPER_THICK = 0.05
WHISPER_HANG = 0.04    # head radii the teeth hang in front of the chin
WHISPER_LONG = 0.62    # head radii they hang below TEETH_FROM, down the hood's open front

# The ceremonial hood (Anomaly), as the game's sprite (the whisperer mask, its first style; its two styles are one
# garment here): a cloth hood drawn up into a point over the head and falling as a bell to a hem over the shoulders,
# open down the front, the broadwrap's cloth, in the cloth's colour; in the opening a dark mask of bioferrite, two lobes over
# the brow, scowling brows over slanted eye sockets and a nose hole as the ritual mask's, and long teeth hanging over the
# chin. Soft, as the broadwrap: it takes the body's weights. Coloured, the cloth near white, so the apparel's colour shows.
def ceremonial_cultist_mask():
    s = Shape()
    s.palette = {'cloth': (0.92, 0.92, 0.9), 'mask': (0.3, 0.27, 0.28)}
    wrap_cloth(s, HOOD_OFF, HOOD_FLARE, HOOD_HEM_OUT, HOOD_PEAK_UP, HOOD_PEAK_BACK, HOOD_BELL, True, 'cloth')
    points, faces, places = face_sheet(WHISPER_OUTLINE, cult_holes(), 8, WHISPER_OFF, 12, 24,
                                       lambda az, el: skull_face(az, el, EYE_EL + 15, CULT_BROW, CULT_CHEEK * 0.6))
    hang_teeth(points, places, WHISPER_OFF, WHISPER_HANG, WHISPER_LONG)
    thick_sheet(s, places, faces, WHISPER_THICK, 'mask', inside=False)
    return made(s, 'CeremonialCultistMask')

def beam(s, a, b, wide, deep, colour, up=Vector((0, 0, 1))):
    """A box from point a to point b, wide head radii across (level, square to it) and deep head radii the other way, as
    a part of s."""
    along = (b - a) / 2
    across = along.cross(up)
    if across.length < 1e-6:
        across = Vector((1, 0, 0))
    across = across.normalized()
    other = across.cross(along).normalized()
    slab((a + b) / 2, across * wide / 2 * R, along, other * deep / 2 * R, colour, s)

NV_BAND = band(10, 2, -12)   # the elevation round the head of the head strap's middle: over the brow, the ears' tops, low behind
NV_BAND_WIDE = 9          # degrees across the head strap
NV_STRAP_OFF = 0.12       # head radii the straps stand off the skin, over the hair, which they leave on
NV_STRAP_WIDE = 0.07      # head radii, half the width of the strap over the crown
NV_PLATE_EL = 6           # degrees up the mount's plate sits on the brow, on the head strap
NV_PLATE = (0.36, 0.28, 0.1)   # head radii: the plate across, high and thick
NV_CLEAR = 0.14           # head radii in front of the most skin round the eye a tube's back end stands
NV_OVER = 8               # degrees each way the skin is looked at round the eye, so a tube clears the nose and brow

def nv_harness(s, colour):
    """The night vision's harness: a strap round the head over the brow and the ears' tops, low at the back, and one
    over the crown from the brow to the back of it, as the sprites have them, standing off over the hair. Gives the
    mount's plate's middle and its way out."""
    strap(s, 0, NV_BAND, NV_BAND_WIDE, NV_STRAP_OFF, n=16, colour=colour)
    over = []
    back_el = 180 - NV_BAND(180)   # past 90, way() points back
    for i in range(11):
        el = NV_BAND(0) + (back_el - NV_BAND(0)) * i / 10
        c = snug(0, el, NV_STRAP_OFF + 0.01)
        over += [c - Vector((NV_STRAP_WIDE * R, 0, 0)), c + Vector((NV_STRAP_WIDE * R, 0, 0))]
    s.part(over, [[2 * i, 2 * i + 2, 2 * i + 3, 2 * i + 1] for i in range(10)], colour)
    out = way(0, NV_PLATE_EL)
    return CENTRE + out * (over_skin(0, NV_PLATE_EL, 6) + (NV_STRAP_OFF + NV_PLATE[2] / 2) * R), out

def nv_tube_base(az, el):
    """Where a tube in front of the face that way has its back end: NV_CLEAR in front of the most skin round about."""
    return CENTRE + way(az, el) * (over_skin(az, el, NV_OVER) + NV_CLEAR * R)

NV_MONO_AT = (-EYE_AZ, EYE_EL + 2)   # the monocular's tube, over the figure's right eye (the right being negative azimuths)
# Its tube from the back end forward (head radii along, head radii across), and each piece's colour; the end the lens.
NV_MONO_TUBE = [(0.0, 0.15), (0.02, 0.17), (0.2, 0.17), (0.21, 0.19), (0.28, 0.19), (0.29, 0.17), (0.42, 0.17)]
NV_MONO_PIECES = ['body', 'body', 'dark', 'dark', 'body', 'body']
NV_MONO_HOUSING = (0.46, 0.4, 0.2)   # head radii: the housing on the tube's back, across, high and front to back
NV_MONO_IR_TUBE = [(0.0, 0.06), (0.1, 0.06)]   # the illuminator's little tube out of the housing's front
NV_MONO_ARM = (0.12, 0.1)  # head radii across and deep, the arm from the housing up to the plate
NV_MONO_BOX = (78, 0)     # the battery box on the strap over the left temple
NV_MONO_BOX_SIZE = (0.34, 0.26, 0.14)   # head radii: front to back, high and thick

# Combat Extended's night vision monocular, as its sprite: a brown head strap round the head and one over the crown, a
# grey mount on the brow, an arm down from it to a housing on one tube in front of the right eye, its lens green, a
# little illuminator out of the housing beside it toward the nose, and a grey battery box on the strap over the left temple. It leaves the hair and the face
# on; the tube stands in front of the eye. Coloured, as it is not made from stuff.
def ce_simple_night_vision():
    s = Shape()
    s.palette = {'strap': (0.42, 0.27, 0.1), 'body': (0.4, 0.38, 0.4), 'dark': (0.26, 0.25, 0.27),
                 'lens': (0.3, 0.62, 0.22)}
    plate, out = nv_harness(s, 'strap')
    up = Vector((0, 0, 1))
    w, h, d = NV_PLATE
    slab(plate, Vector((1, 0, 0)) * w / 2 * R, out.cross(Vector((1, 0, 0))).normalized() * -h / 2 * R, out * d / 2 * R,
         'body', s)
    forward = Vector((0, -1, 0))
    across = Vector((1, 0, 0))
    base = nv_tube_base(*NV_MONO_AT)
    lathe(s, base, forward, NV_MONO_TUBE, NV_MONO_PIECES, 'lens')
    # The housing on the tube's back half, from its top down its inner side (toward the nose), the illuminator's
    # little tube coming out of its front at the bottom; and the arm straight up from it to the plate.
    radius = NV_MONO_TUBE[2][1]
    hw, hh, hd = NV_MONO_HOUSING
    housing = base + forward * hd / 2 * R + across * (radius * 0.45) * R + up * (radius * 0.35) * R
    slab(housing, across * hw / 2 * R, up * hh / 2 * R, forward * hd / 2 * R, 'body', s)
    ir = housing + across * (hw / 2 - NV_MONO_IR_TUBE[0][1] - 0.02) * R - up * (hh / 2 - NV_MONO_IR_TUBE[0][1] - 0.02) * R \
        + forward * hd / 2 * R
    lathe(s, ir, forward, NV_MONO_IR_TUBE, ['dark'], 'body', n=8)
    arm_foot = housing + up * hh / 2 * R
    beam(s, arm_foot - up * 0.05 * R, Vector((arm_foot.x, arm_foot.y, plate.z)) + out * 0.0, NV_MONO_ARM[0], NV_MONO_ARM[1],
         'body', up=forward)
    beam(s, Vector((arm_foot.x, arm_foot.y, plate.z)) - across * 0.05 * R, plate, NV_MONO_ARM[1], NV_MONO_ARM[0], 'body',
         up=forward)
    # The battery box, on the strap over the left temple, faced along the head's side.
    az, el = NV_MONO_BOX
    side = way(az, el)
    w, h, d = NV_MONO_BOX_SIZE
    box = CENTRE + side * (over_skin(az, el, 4) + (NV_STRAP_OFF + d / 2) * R)
    along = side.cross(up).normalized()
    slab(box, along * w / 2 * R, up * h / 2 * R, side * d / 2 * R, 'body', s)
    return made(s, 'CE_SimpleNightVision')

# The goggles' four tubes, (azimuth, elevation, degrees each splays out): two over each eye, the outer ones turned out.
NV_QUAD = ((-14, EYE_EL + 2, 0), (14, EYE_EL + 2, 0), (-40, EYE_EL + 2, 16), (40, EYE_EL + 2, 16))
NV_QUAD_TUBE = [(0.0, 0.12), (0.02, 0.135), (0.3, 0.135), (0.31, 0.15), (0.38, 0.15)]
NV_QUAD_PIECES = ['tube', 'tube', 'tube', 'housing']
NV_QUAD_HOUSING = 0.16    # head radii high, the housing over the tubes' backs
NV_QUAD_MOUNT = (0.18, 0.1)   # head radii across and deep, the mount from the housing up to the plate

# Combat Extended's advanced night vision goggles, as its sprite: a black head strap and one over the crown, a dark
# mount on the brow, and below it a housing across the eyes carrying four tubes in a row, the outer two turned out, their
# lenses green. Coloured, as it is not made from stuff.
def ce_advanced_night_vision():
    s = Shape()
    s.palette = {'strap': (0.12, 0.12, 0.13), 'housing': (0.34, 0.34, 0.36), 'tube': (0.22, 0.22, 0.24),
                 'lens': (0.3, 0.62, 0.22)}
    plate, out = nv_harness(s, 'strap')
    up = Vector((0, 0, 1))
    w, h, d = NV_PLATE
    slab(plate, Vector((1, 0, 0)) * w / 2 * R, out.cross(Vector((1, 0, 0))).normalized() * -h / 2 * R, out * d / 2 * R,
         'housing', s)
    bases = []
    for az, el, splay in NV_QUAD:
        base = nv_tube_base(az, el)
        a = math.radians(math.copysign(splay, az))
        axis = Vector((math.sin(a), -math.cos(a), 0))
        lathe(s, base, axis, NV_QUAD_TUBE, NV_QUAD_PIECES, 'lens')
        bases.append((base, axis))
    # The housing: a bar across over the tubes' backs, from the outer right tube to the outer left, through the inner two.
    stations = []
    for i in (2, 0, 1, 3):
        base, axis = bases[i]
        stations.append(base + axis * 0.1 * R + up * (NV_QUAD_TUBE[2][1] + NV_QUAD_HOUSING / 2 - 0.04) * R)
    for a, b in zip(stations, stations[1:]):
        beam(s, a - (b - a).normalized() * 0.06 * R, b + (b - a).normalized() * 0.06 * R, 0.24, NV_QUAD_HOUSING, 'housing')
    # The mount: from the housing's middle up to the plate.
    middle = (stations[1] + stations[2]) / 2
    beam(s, middle, plate - up * h * 0.2 * R, NV_QUAD_MOUNT[0], NV_QUAD_MOUNT[1], 'housing')
    return made(s, 'CE_AdvancedNightVision')

HATS = {'SimpleHelmet': simple_helmet, 'AdvancedHelmet': advanced_helmet, 'Tuque': tuque, 'CowboyHat': cowboy_hat,
        'BowlerHat': bowler_hat, 'HatHood': hat_hood, 'TribalHeaddress': tribal_headdress, 'PsychicFoilHelmet': psychic_foil_helmet,
        'WarMask': war_mask, 'WarVeil': war_veil, 'ClothMask': cloth_mask, 'Blindfold': blindfold,
        'Beret': beret, 'HatTop': hat_top, 'HatLadies': hat_ladies,
        'Coronet': coronet, 'Crown': crown_royal, 'CrownStellic': crown_stellic,
        'PsyfocusHelmet': psyfocus_helmet, 'EltexSkullcap': eltex_skullcap,
        'AuthorityCap': authority_cap, 'Flophat': flophat, 'Gunlink': gunlink,
        'Headwrap': headwrap, 'Shadecone': shadecone, 'Slicecap': slicecap,
        'Tailcap': tailcap, 'TortureCrown': torture_crown, 'AirwireHeadset': airwire_headset,
        'ArrayHeadset': array_headset, 'IntegratorHeadset': integrator_headset,
        'VisageMask': visage_mask, 'GasMask': gas_mask, 'Broadwrap': broadwrap, 'Collar': slave_collar,
        'CultistMask': cultist_mask, 'CeremonialCultistMask': ceremonial_cultist_mask,
        'CE_SimpleNightVision': ce_simple_night_vision, 'CE_AdvancedNightVision': ce_advanced_night_vision}

# Pieces of other garments, made only when named (they are not garments of their own, so not for Models/garments).
PARTS = {'BurkaHood': burka_hood}

os.makedirs(out_dir, exist_ok=True)
mat = bpy.data.materials.new('Hat')
mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.8, 0.8, 0.8, 1.0)
print('HEAD middle %s, radius %.3f, top %.3f' % (tuple(round(c, 3) for c in CENTRE), R, top))
made_hats = []
for name in (wanted or list(HATS)):
    o = (HATS.get(name) or PARTS[name])()
    if not o.data.materials:   # a coloured hat has its own
        o.data.materials.append(mat)
    for x in bpy.context.view_layer.objects:
        x.select_set(x is o)
    bpy.context.view_layer.objects.active = o
    # Named as the garment, its first letter small (but for a prefix such as CE_, kept as the game's other CE_ files are).
    path = os.path.join(out_dir, (name if name[:2].isupper() else name[:1].lower() + name[1:]) + '.glb')
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format='GLB', export_skins=False,
                              export_animations=False, export_morph=False)
    made_hats.append(o)
    print('HAT %s: %d vertices, %d faces -> %s' % (name, len(o.data.vertices), len(o.data.polygons), path))
if out_blend:
    for o in made_hats:
        o.parent = rig
        o.matrix_parent_inverse = rig.matrix_world.inverted()
        o.modifiers.new('Armature', 'ARMATURE').object = rig
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
