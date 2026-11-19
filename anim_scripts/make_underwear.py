"""Makes underwear for the chibi from its own body, for UnderWhere (Malistaticy.UnderWhere) and mods like it.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_underwear.py -- <out folder> [garment ...] [--blend out.blend]

As make_pants.py makes the trousers: the body (walk.BODY_PARTS, joined) is copied in its rest pose and base shape, cut
by planes, and pushed OFFSET out along the skin's smooth normals, one surface. Every plane is bisected through the mesh
before any face is dropped, so each opening follows a clean line on the low-poly body; which faces go is then any test of
their middles against those planes (a strap is the faces between two planes that a neckline would otherwise take). It
is kept as lean as the garments make_from_groups.py builds from the body's faces (lean): the body's own faces and UV
atlas (unwrapped anew, every island's seam split the exported vertices), the bisects' vertices only along the openings,
and those within WELD of a body vertex welded onto it. A cut takes a body vertex within ON_PLANE of its plane as on it,
so an opening runs along an edge loop where the body has one near the line, and only cuts through where none is.
OFFSET is the shirt layer's (make_from_groups.py's LAYER_OFFSET OnSkin, 0.1 Blender units): the game hides a garment
wherever one on an outer layer covers it (SkinnedFigure.HideGarments), as it does a shirt's waist under trousers, so
underwear needs no lower offset, and closer to the skin it fought the body for the same pixels where a narrow strap or
band hides none of the body's large triangles. No image: the game draws each in the apparel's colour.

The garments (GARMENTS), each named for the export (Models/garments/<Name>.glb, the garment <Name>; export_frames.py's
GARMENT_PROPS give each its UnderWhere def and GARMENTS its layer and groups, so it stands in for other mods' underwear):
    Panties    a high-cut brief, the leg openings rising from the crotch to the hip, lower at the back.
    Boxers     a short pair of trousers, cut square across the thigh.
    LongJohns  the collared shirt's and the trousers' vertex groups together, whole: to the neck, wrists and ankles.
    Tanktop    a scoop neck front and back, straps over the shoulders, no sleeves, tucked in at the waist.
    Bra        a band under the chest, two cups rising to a strap each over the shoulders, a V between them.
    Chestwrap  a wide band round the chest, its top slanting across as a wound cloth's would.
    Loincloth  a low brief with a flap hung from the waistband front and back to mid-thigh, its hem ragged.

The loincloth's flaps hang flat (each row as far out as its furthest column needs) clear of the thighs, so they carry their own weights (the hips at the waistband, going over to
each thigh by side further down), written into the .glb with the rig; GARMENT_PROPS' part_weights keeps them and gives the
brief the body's. Heights and lengths are in leg lengths of the rig (the thigh's head to the floor). The .blend is never
saved; --blend writes a separate file with the garments on the rig, to model from."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

# How far the cloth stands off the skin, in leg lengths: the shirt layer's (0.1 Blender units).
OFFSET = 0.008
# The chest wrap is a few turns of cloth, so it stands off further.
WRAP_OFFSET = 0.011
# How far outside the cloth each body vertex is kept, as a share of the offset, and how far round it the cloth is moved.
CORNER_CLEAR = 0.5
CORNER_REACH = 0.05

# Panties: the waistband over the thigh's head at the front and how much higher at the back; the gusset's half width at
# the crotch; the leg opening's height at the hip's side over the thigh's head; and how much lower it comes per leg
# length back (so it covers the seat and is cut high at the front).
PANTIES = {'waist': 0.03, 'rise_back': 0.03, 'gusset': 0.035, 'side': -0.025, 'back_slope': 0.6}
# Boxers: the waistband, and how far down the thigh each leg reaches (the thigh's head 0, the knee 1).
BOXERS = {'waist': 0.06, 'rise_back': 0.03, 'leg': 0.70}   # (the leg at the thigh's edge ring there)
# Long johns: the faces of the body's vertex groups for the collared shirt (to the wrists) and the trousers (to the
# ankles), as make_from_groups.py makes those, whole, with no cut at all.
LONG_JOHNS_GROUPS = ('-CollarShirt -Jacket -FlakJacket', '-Pants')
# Tank top: the hem over the thigh's head; the neckline's half width and its lowest point at the front and the back over
# the thigh's head; the straps' outer edge from the middle; and the armhole's bottom over the thigh's head.
TANKTOP = {'hem': 0.03, 'neck_half': 0.085, 'neck_front': 0.37, 'neck_back': 0.45, 'strap_out': 0.15, 'armhole': 0.36}
# Bra (over the thigh's head): the band's bottom; the band's top at the sides and back; the V between the cups and its
# half width; each cup's top and how far out from the middle it is highest; the straps' middle and half width.
BRA = {'bottom': 0.27, 'band': 0.32, 'v': 0.31, 'v_half': 0.02, 'cup_top': 0.40, 'cup_out': 0.10, 'strap': 0.10,
       'strap_half': 0.017}
# Chest wrap (over the thigh's head): its bottom, its top at the middle and how much higher it is per leg length to the
# figure's left (a slant across the chest).
CHESTWRAP = {'bottom': 0.27, 'top': 0.38, 'slant': 0.4}
# Loincloth: its brief (as PANTIES, lower and cut less high); each flap's half width at the top and the bottom, its hem
# over the thigh's head (below: negative), how far it hangs clear of the skin, the ragged hem's teeth and their depth.
LOIN_BRIEF = {'waist': -0.01, 'rise_back': 0.02, 'gusset': 0.035, 'side': -0.08, 'back_slope': 0.5, 'exact': True}
FLAP = {'top_half': 0.085, 'hem_half': 0.10, 'hem': -0.42, 'gap': 0.012, 'teeth': 4, 'tooth': 0.05, 'columns': 8,
        'rows': 5, 'flare': 0.02}
# How far down the flap (0 at the waistband, 1 at the hem) it is the thighs' rather than the hips', at most.
FLAP_THIGHS = 0.85

argv = sys.argv[sys.argv.index('--') + 1:]
out_blend = None
if '--blend' in argv:
    i = argv.index('--blend')
    out_blend = argv[i + 1]
    argv = argv[:i] + argv[i + 2:]
out_dir = argv[0]
wanted = argv[1:]

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

body = walk.join_body()
rig = body.parent
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0

def joint(name, tail=False):
    b = rig.data.bones[name]
    return rig.matrix_world @ (b.tail_local if tail else b.head_local)

hip = joint('thigh.L')
leg = hip.z   # the floor is at 0
bw = body.matrix_world
bverts = [bw @ v.co for v in body.data.vertices]
btree = BVHTree.FromPolygons(bverts, [tuple(p.vertices) for p in body.data.polygons])
# The crotch: the lowest body vertex near the middle above the knees.
crotch = min(v.z for v in bverts if abs(v.x) < 0.06 * leg and v.z > joint('shin.L').z)
# The trunk's half width at the hip joint's height (the leg openings rise to it).
hip_side = max(abs(v.x) for v in bverts if abs(v.z - hip.z) < 0.04 * leg and abs(v.x) < 0.4 * leg)

def H(share):
    """A height in leg lengths over the thigh's head."""
    return hip.z + share * leg

class Plane:
    def __init__(self, co, no):
        self.co, self.no = Vector(co), Vector(no).normalized()
    def past(self, p):
        return (p - self.co).dot(self.no) > 0

def fresh():
    bm = bmesh.new()
    bm.from_mesh(body.data)
    bm.transform(bw)
    return bm

# The body's own vertices by place (a bisect copies a layer's values onto the vertices it makes, so they are told by
# place), for lean.
_own = {tuple(round(c, 4) for c in v) for v in bverts}

def own(v):
    return tuple(round(c, 4) for c in v.co) in _own

# A body vertex this close to a cut's plane is taken as on it, in leg lengths, so where the body has an edge loop near
# the line the opening follows it with whole faces and only a face nothing comes near is cut through (bisect_plane's
# dist). The narrow cuts (fine: straps and bands narrower than a face) take the line exactly.
ON_PLANE = 0.035

def split(bm, plane, where=None, fine=False):
    """Bisects the faces whose middle passes where (all with none) along the plane. (Keeping the body's faces whole and
    dropping each by its middle was tried, as make_from_groups.py's garments keep them: on this triangulated body the
    openings came out as a sawtooth along the triangles' diagonals, and sliding those vertices onto the line stretched
    the faces across the body's ridges, which came through. fine marks the cuts narrower than a face, which take
    the line exactly; the rest go by ON_PLANE.)"""
    faces = [f for f in bm.faces if where is None or where(f.calc_center_median())]
    edges = list({e for f in faces for e in f.edges})
    verts = list({v for f in faces for v in f.verts})
    bmesh.ops.bisect_plane(bm, geom=verts + edges + faces, dist=(1e-5 if fine else ON_PLANE * leg), plane_co=plane.co,
                           plane_no=plane.no)

# Bisect vertices on an opening this close to one of the body's own vertices along an edge are welded onto it, in leg
# lengths: a little off the line, one vertex fewer.
WELD = 0.035

# The body's edges, for on_body_edge; and how near straight (degrees) an opening must run through a vertex to lose it.
_body_edges = [(bverts[e.vertices[0]], bverts[e.vertices[1]]) for e in body.data.edges]
STRAIGHT = 25.0

def on_body_edge(p):
    for a, b in _body_edges:
        ab = b - a
        t = max(0.0, min(1.0, (p - a).dot(ab) / ab.length_squared))
        if (p - (a + ab * t)).length < 1e-3 * leg:
            return True
    return False

def straight(v):
    """True if the opening runs on through v within STRAIGHT degrees."""
    edges = [e for e in v.link_edges if e.is_boundary]
    if len(edges) != 2:
        return False
    a, b = ((e.other_vert(v).co - v.co).normalized() for e in edges)
    return a.dot(b) < -math.cos(math.radians(STRAIGHT))

def lean(bm):
    """Leaves the cut piece the body's own faces but along its openings: the vertices the bisects made inside the cloth
    (a plane crossing what is kept) are dissolved, and those on an opening next to a body vertex welded onto it."""
    inner = [v for v in bm.verts if not own(v) and not v.is_boundary]
    if inner:
        bmesh.ops.dissolve_verts(bm, verts=inner)
    weld = {}
    for v in bm.verts:
        if own(v) or not v.is_boundary:
            continue
        near = [e.other_vert(v) for e in v.link_edges if own(e.other_vert(v))]
        near = [u for u in near if (u.co - v.co).length < WELD * leg and u not in weld]
        if near:
            weld[v] = min(near, key=lambda u: (u.co - v.co).length)
    if weld:
        bmesh.ops.weld_verts(bm, targetmap=weld)
    # A vertex on an opening inside one body face (where another plane crossed the opening, a strap's across a band) on a
    # straight run adds nothing: the cloth there lies flat on that face and moves with it. Only those on a body edge,
    # where the cloth folds with the skin, and the corners are kept.
    loose = [v for v in bm.verts if v.is_boundary and not own(v) and not on_body_edge(v.co) and straight(v)]
    if loose:
        bmesh.ops.dissolve_verts(bm, verts=loose)
    bmesh.ops.dissolve_degenerate(bm, dist=1e-4 * leg, edges=bm.edges[:])
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])

def drop(bm, test):
    """Deletes the faces whose middle passes test, and any vertices left loose."""
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if test(f.calc_center_median())], context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')

def cut(bm, plane, where=None, fine=False):
    split(bm, plane, where, fine)
    drop(bm, lambda p: plane.past(p) and (where is None or where(p)))

def keep_largest(bm):
    bm.faces.index_update()
    seen, pieces = set(), []
    for f in bm.faces:
        if f.index in seen:
            continue
        stack, piece = [f], []
        seen.add(f.index)
        while stack:
            g = stack.pop()
            piece.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h.index not in seen:
                        seen.add(h.index)
                        stack.append(h)
        pieces.append(piece)
    pieces.sort(key=len)
    for piece in pieces[:-1]:
        bmesh.ops.delete(bm, geom=piece, context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')

def side_of(sign):
    return lambda p: p.x * sign > 0

def near_segment(a, b, reach):
    """A test: a point within reach (leg lengths) of the segment a..b."""
    def near(p):
        t = max(0.0, min(1.0, (p - a).dot(b - a) / (b - a).length_squared))
        return (p - a.lerp(b, t)).length < reach * leg
    return near

def narrow(spec):
    """True if a brief's cuts take their lines exactly ('exact': the loincloth's, whose band at the hip's side is
    narrower than a face, so snapped to the body's edges it broke there)."""
    return spec.get('exact', False)

def waistband(bm, spec):
    """The waistband: a plane through the waist, tipped up to the back (the figure faces -y)."""
    tip = math.atan2(spec['rise_back'] * leg, 0.25 * leg)
    cut(bm, Plane((0, 0, H(spec['waist'])), (0, math.sin(tip), math.cos(tip))), fine=narrow(spec))

def thigh_cuts(bm, share):
    for side, sign in (('L', 1.0), ('R', -1.0)):
        a, b = joint('thigh.' + side), joint('thigh.' + side, tail=True)
        cut(bm, Plane(a.lerp(b, share), b - a), side_of(sign))

def shin_cuts(bm, share):
    for side, sign in (('L', 1.0), ('R', -1.0)):
        a, b = joint('shin.' + side), joint('shin.' + side, tail=True)
        cut(bm, Plane(a.lerp(b, share), b - a), side_of(sign))

def arms_off(bm, share):
    """Drops each arm from share along the upper arm (its head 0, the elbow 1; below 0 into the shoulder), square across
    it, only what is near the arm's line (the plane also passes through the trunk lower down)."""
    for side in ('L', 'R'):
        a, b = joint('upper_arm.' + side), joint('upper_arm.' + side, tail=True)
        hand = joint('hand.' + side, tail=True)
        near = near_segment(a.lerp(b, share), hand, 0.12)
        cut(bm, Plane(a.lerp(b, share), b - a), near)

def sleeves_at(bm, share):
    for side in ('L', 'R'):
        a, b = joint('forearm.' + side), joint('forearm.' + side, tail=True)
        hand = joint('hand.' + side, tail=True)
        cut(bm, Plane(a.lerp(b, share), b - a), near_segment(a, hand, 0.12))

def leg_openings(bm, spec):
    """A brief's leg openings: for each leg, a plane from the gusset at the crotch up to the trunk's side at the hip,
    dropping what is below it and outward; leant so the opening comes lower at the back."""
    zc, zh = crotch - 0.005 * leg, H(spec['side'])
    g = spec['gusset'] * leg
    for sign in (1.0, -1.0):
        run, rise = hip_side - g, zh - zc
        no = Vector((sign * rise, -spec['back_slope'] * run, -run))
        cut(bm, Plane((sign * g, 0, zc), no), side_of(sign), fine=narrow(spec))

def brief(spec):
    bm = fresh()
    waistband(bm, spec)
    leg_openings(bm, spec)
    keep_largest(bm)
    return bm

def panties():
    return brief(PANTIES)

def boxers():
    bm = fresh()
    waistband(bm, BOXERS)
    thigh_cuts(bm, BOXERS['leg'])
    keep_largest(bm)
    return bm

def neckline(bm, back, dip):
    neck = joint('shoulder.L').z + back * leg
    tip = math.atan2(dip * leg, 0.25 * leg)
    cut(bm, Plane((0, 0, neck), (0, -math.sin(tip), math.cos(tip))))

def group_faces(bm, names):
    """Drops every face not wholly in one of the body's vertex groups names (any weight above 0 counts, as in
    make_from_groups.py)."""
    deform = bm.verts.layers.deform.active
    index = {body.vertex_groups[n].index for n in names}
    inside = lambda v: any(g in index and w > 0 for g, w in v[deform].items())
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if not all(inside(v) for v in f.verts)], context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')

def long_johns():
    bm = fresh()
    group_faces(bm, LONG_JOHNS_GROUPS)
    keep_largest(bm)
    return bm

def tanktop():
    s = TANKTOP
    bm = fresh()
    cut(bm, Plane((0, 0, H(s['hem'])), (0, 0, -1)))
    arms_off(bm, 0.0)
    # The scoop: the faces within neck_half of the middle above a plane from neck_front at the front to neck_back at the
    # back (the chest's front and back about 0.15 leg lengths either side of the spine).
    nh, so = s['neck_half'] * leg, s['strap_out'] * leg
    zf, zb = H(s['neck_front']), H(s['neck_back'])
    scoop = Plane((0, 0, (zf + zb) / 2), (0, -(zb - zf) / (0.3 * leg), 1))
    sides = [Plane((nh, 0, 0), (-1, 0, 0)), Plane((-nh, 0, 0), (1, 0, 0))]
    armholes = [Plane((so, 0, 0), (1, 0, 0)), Plane((-so, 0, 0), (-1, 0, 0))]
    low = Plane((0, 0, H(s['armhole'])), (0, 0, 1))
    upper = lambda p: p.z > H(s['armhole']) - 0.05 * leg
    for pl in sides + armholes + [scoop]:
        split(bm, pl, upper, fine=pl is not scoop)   # (the straps narrower than the shoulder's faces)
    split(bm, low)
    drop(bm, lambda p: (scoop.past(p) and sides[0].past(p) and sides[1].past(p))
         or (low.past(p) and (armholes[0].past(p) or armholes[1].past(p))))
    keep_largest(bm)
    return bm

def bra():
    s = BRA
    bm = fresh()
    # (all bisected: the band is narrower than the chest's faces, so whole faces would leave no band)
    cut(bm, Plane((0, 0, H(s['bottom'])), (0, 0, -1)), fine=True)
    arms_off(bm, 0.0)
    band, v, top = H(s['band']), H(s['v']), H(s['cup_top'])
    vh, out = s['v_half'] * leg, s['cup_out'] * leg
    # The front's top: up from the V to each cup's top, then down to the band at the side, beyond which the band runs
    # level round the back. Rising: z = v + (|x| - vh) * up; falling: z = top - (|x| - out) * down.
    up = (top - v) / (out - vh)
    down = (top - band) / (0.08 * leg)
    rising = [Plane((sign * vh, 0, v), (-sign * up, 0, 1)) for sign in (1.0, -1.0)]
    falling = [Plane((sign * out, 0, top), (sign * down, 0, 1)) for sign in (1.0, -1.0)]
    level = Plane((0, 0, band), (0, 0, 1))
    centre = [Plane((vh, 0, 0), (-1, 0, 0)), Plane((-vh, 0, 0), (1, 0, 0))]
    sh, sm = s['strap_half'] * leg, s['strap'] * leg
    straps = [Plane((sm - sh, 0, 0), (1, 0, 0)), Plane((sm + sh, 0, 0), (-1, 0, 0)),
              Plane((-sm + sh, 0, 0), (-1, 0, 0)), Plane((-sm - sh, 0, 0), (1, 0, 0))]
    high = lambda p: p.z > band - 0.05 * leg
    for pl in rising + falling + centre + straps + [level]:
        split(bm, pl, high, fine=True)
    front = lambda p: p.y < 0
    def over_top(p):
        if not level.past(p):
            return False
        if not front(p):
            return True   # the back: the level band
        sign = 1.0 if p.x > 0 else -1.0
        r, f = rising[0 if sign > 0 else 1], falling[0 if sign > 0 else 1]
        if abs(p.x) < vh:
            return v < p.z
        return r.past(p) or f.past(p)
    def strap(p):
        return (straps[0].past(p) and straps[1].past(p)) or (straps[2].past(p) and straps[3].past(p))
    shoulder_top = joint('shoulder.L').z + 0.08 * leg
    drop(bm, lambda p: over_top(p) and not (strap(p) and p.z < shoulder_top))
    keep_largest(bm)
    return bm

def chestwrap():
    s = CHESTWRAP
    bm = fresh()
    cut(bm, Plane((0, 0, H(s['bottom'])), (0, 0, -1)))
    arms_off(bm, 0.0)
    cut(bm, Plane((0, 0, H(s['top'])), (-s['slant'], 0, 1)))
    keep_largest(bm)
    return bm

def loincloth():
    return brief(LOIN_BRIEF)

MAKERS = {'Panties': panties, 'Boxers': boxers, 'LongJohns': long_johns, 'Tanktop': tanktop, 'Bra': bra,
          'Chestwrap': chestwrap, 'Loincloth': loincloth}
OFFSETS = {'Chestwrap': WRAP_OFFSET}

# The skin's smooth normals, out of the body (the body may be written inside out: its top vertex's says which way).
_vn = [(bw.to_3x3() @ v.normal).normalized() for v in body.data.vertices]
if _vn[max(range(len(bverts)), key=lambda i: bverts[i].z)].z < 0:
    _vn = [-n for n in _vn]
_polys = [tuple(p.vertices) for p in body.data.polygons]

def skin_normal(p):
    """The body's smooth normal at its point nearest p, from its polygon's corners."""
    from mathutils.interpolate import poly_3d_calc
    hit, _, i, _ = btree.find_nearest(p)
    corners = _polys[i]
    ws = poly_3d_calc([bverts[c] for c in corners], hit)
    return sum((_vn[c] * w for c, w in zip(corners, ws)), Vector()).normalized()

def clear_corners(bm, off):
    """The cloth's vertices are laid off the skin, but its faces are chords between them, and the low-poly body's own
    corners poke out between them. Each body vertex is kept CORNER_CLEAR of off outside the cloth by moving the cloth's
    vertices round where it would come through out along the cloth's normal there, falling off over CORNER_REACH."""
    for _ in range(8):
        bm.verts.ensure_lookup_table()
        tree = BVHTree.FromBMesh(bm)
        moves = {}
        for b in bverts:
            hit, n, fi, d = tree.find_nearest(b, 0.03 * leg)
            if hit is None:
                continue
            if n.dot(btree.find_nearest(hit)[1]) < 0:
                n = -n   # the cloth's way out from the body (the skin's under it)
            depth = (b - hit).dot(n) + CORNER_CLEAR * off
            if depth <= 0:
                continue
            for v in bm.verts:
                d = (v.co - hit).length
                if d < CORNER_REACH * leg:
                    w = walk.smooth(1.0 - d / (CORNER_REACH * leg)) * depth
                    if w > moves.get(v, (0.0, None))[0]:
                        moves[v] = (w, n)
        if not moves:
            break
        for v, (w, n) in moves.items():
            v.co += n * w

def finish(name, bm):
    """The cut piece as an object: lean, pushed off the skin, normals out."""
    lean(bm)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    o = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(o)
    for x in bpy.context.view_layer.objects:
        x.select_set(x is o)
    bpy.context.view_layer.objects.active = o
    # Each vertex out along the skin's smooth normal at its place, as make_from_groups.py pushes the body's own faces: the
    # cut leaves the body's faces as they are (new vertices only on its edges), so the cloth keeps its low-poly shape and
    # stands evenly off its ridges, where smoothing it had them come through.
    off = OFFSETS.get(name, OFFSET) * leg
    bm = bmesh.new()
    bm.from_mesh(o.data)
    for v in bm.verts:
        v.co = v.co + skin_normal(v.co) * off
    clear_corners(bm, off)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.faces.ensure_lookup_table()
    f = max(bm.faces, key=lambda f: f.calc_area())
    hit, n, _, _ = btree.find_nearest(f.calc_center_median())
    if f.normal.dot(n) < 0:
        bmesh.ops.reverse_faces(bm, faces=bm.faces)
    bm.to_mesh(o.data)
    bm.free()
    o.vertex_groups.clear()
    if o.data.shape_keys:
        o.shape_key_clear()
    return o

def add_flaps(o):
    """The loincloth's two flaps, hung from the waistband front and back, as part of o's mesh with weights of their own."""
    s = FLAP
    tip = math.atan2(LOIN_BRIEF['rise_back'] * leg, 0.25 * leg)
    def waist_z(y):   # the brief's waistband plane's height at y
        return H(LOIN_BRIEF['waist']) - math.tan(tip) * y
    hips = o.vertex_groups.new(name=walk.HIPS)
    thighs = {'L': o.vertex_groups.new(name='thigh.L'), 'R': o.vertex_groups.new(name='thigh.R')}
    bm = bmesh.new()
    bm.from_mesh(o.data)
    deform = bm.verts.layers.deform.verify()
    off = OFFSET * leg
    cols, rows = s['columns'], s['rows']
    for way in (-1.0, 1.0):   # the front (-y) and the back (+y)
        out = min if way < 0 else max   # further out from the body
        top = waist_z(way * 0.15 * leg) - 0.01 * leg
        places, rowmost = [], [None] * (rows + 1)
        for c in range(cols + 1):
            u = c / cols * 2 - 1   # -1 .. 1 across
            # The ragged hem: every other column's corner raised a tooth's depth, the back's the other way round.
            tooth = s['tooth'] * leg if (c + (way > 0)) % 2 else 0.0
            column = []
            for r in range(rows + 1):
                t = r / rows
                x = u * (s['top_half'] + (s['hem_half'] - s['top_half']) * t) * leg
                z = top + (H(s['hem']) + tooth - top) * t
                # The skin's front (or back) at this column and height, if the ray meets it, and the cloth's gap off it.
                hit = btree.ray_cast(Vector((x, way * 3 * leg, z)), Vector((0, -way, 0)))[0]
                gap = off + 0.002 * leg if r == 0 else s['gap'] * leg + s['flare'] * leg * t
                y = None if hit is None else hit.y + way * gap
                if y is not None:
                    rowmost[r] = y if rowmost[r] is None else out(rowmost[r], y)
                column.append((x, y, z, t, u))
            places.append(column)
        # Below the waistband each row hangs flat, as far out as its furthest column needs or any row above it, so the
        # flap falls straight over the thighs and between them (where no ray meets the skin) rather than folding in.
        hang = None
        for r in range(1, rows + 1):
            if rowmost[r] is not None:
                hang = rowmost[r] if hang is None else out(hang, rowmost[r])
            rowmost[r] = hang if hang is not None else way * 0.15 * leg
        grid = [[] for r in range(rows + 1)]
        for c in range(cols + 1):
            for r in range(rows + 1):
                x, y, z, t, u = places[c][r]
                if r > 0 or y is None:
                    y = rowmost[r] if rowmost[r] is not None else rowmost[1]
                v = bm.verts.new(Vector((x, y, z)))
                below = min(1.0, t) * FLAP_THIGHS
                across = (u + 1) / 2   # the figure's left (+x) is thigh.L's
                v[deform][hips.index] = 1.0 - below
                if below > 0:
                    v[deform][thighs['L'].index] = below * across
                    v[deform][thighs['R'].index] = below * (1.0 - across)
                grid[r].append(v)
        for r in range(rows):
            for c in range(cols):
                quad = [grid[r][c], grid[r][c + 1], grid[r + 1][c + 1], grid[r + 1][c]]
                f = bm.faces.new(quad if way > 0 else list(reversed(quad)))
                f.smooth = True
    # Faces out from the body: the front flap's toward -y, the back's toward +y.
    bm.to_mesh(o.data)
    bm.free()

def uv_and_material(o):
    for x in bpy.context.view_layer.objects:
        x.select_set(x is o)
    bpy.context.view_layer.objects.active = o
    # (the UVs are the body's own atlas, unwrap_pawn.py's, which came with the copy: unwrapped again, each island's seam
    # split the exported vertices, nearly doubling them)
    for p in o.data.polygons:
        p.use_smooth = True
    mat = bpy.data.materials.new(o.name)
    mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.8, 0.8, 0.8, 1.0)
    o.data.materials.append(mat)

def export(o, path, skinned):
    bpy.ops.object.select_all(action='DESELECT')
    if skinned:
        world = o.matrix_world.copy()
        o.parent = rig
        o.matrix_world = world
        o.modifiers.new('Armature', 'ARMATURE').object = rig
        rig.hide_set(False)
        rig.hide_viewport = False
        rig.select_set(True)
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format='GLB', export_skins=skinned,
                              export_animations=False, export_morph=False)

os.makedirs(out_dir, exist_ok=True)
made = []
for name, maker in MAKERS.items():
    if wanted and name not in wanted:
        continue
    o = finish(name, maker())
    if name == 'Loincloth':
        add_flaps(o)
    uv_and_material(o)
    path = os.path.join(out_dir, name + '.glb')
    export(o, path, name == 'Loincloth')
    made.append(o)
    uv = o.data.uv_layers.active
    split_at = {(l.vertex_index, tuple(round(c, 5) for c in uv.data[l.index].uv)) for l in o.data.loops} if uv else set()
    print('UNDERWEAR %s: %d vertices (%d split at UV seams), %d triangles -> %s'
          % (name, len(o.data.vertices), len(split_at), sum(len(p.vertices) - 2 for p in o.data.polygons), path))
print('UNDERWEAR leg %.3f, crotch %.3f, hip side %.3f' % (leg, crotch, hip_side))
if out_blend:
    for o in made:
        if o.parent is None:
            o.parent = rig
            o.matrix_parent_inverse = rig.matrix_world.inverted()
            o.modifiers.new('Armature', 'ARMATURE').object = rig
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
