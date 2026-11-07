"""Makes the Duster garment: a long open coat. Above the hips it is the body's vertex group naming `-Jacket` (the long
sleeved shirt) copied and pushed out along the normals to the Shell layer, with the body's own weights, so it bends as the
skin under it does, and cut open down the front. Below the hips it is a long skirt hung from that bottom edge, built round
the body as make_tribal.py builds the tribalwear's.

The opening is a straight line each side from the neck's V to OPEN_WIDTH at OPEN_LOW, in the front view, cut through the
group's faces (they don't have a seam there: the body has few vertices down its middle). A vertex the cut makes is kept as
a place on the edge it cuts, so a body key moves it as it moves the edge.

The skirt has no body faces under it, so each vertex of the bottom edge drops a line of vertices straight down to the hem,
set out from the hips' middle as far as the body reaches that way at or above that height, so it hangs clear of both legs
and flares. It has a vent up the back from its VENTS height, so that from there down each half goes with its own leg, and the two
legs don't tear one sheet between them. Its weights go from the edge's (the body's) at the top toward the thigh on its
side, then the shin below the knee, the front more than the back (a knee comes forward through the front and bends the
coat over it; a seated pawn's back panels must stay hanging). Each body key's shape is built the same way from the keyed
body, the hem kept at the same share of the way down the leg, and goes in as the garment's own key.

One surface, drawn in the apparel's colour, both sides, as the game's garments don't cull. It has UVs of its own (add_uvs:
the body's seams on the upper part, the skirt one upright island), for Meshy to paint on, but no image.

With --robe it makes the Robe instead (ROBE's numbers in place of these): the same coat, but open only in a V at the
neck (OPEN_LOW is then the V's point, OPEN_WIDTH its half width at the neck), closed below it, so the bottom edge is a
ring round the body; ankle length and fuller. Its vents (VENTS: the back's, and one up the front) part it from their VENTS
heights down, as two legs inside one closed skirt would otherwise tear it between them.

With --burka it makes the Burka (BURKA's numbers, Ideology's burka): the robe closed right up to the neck, to the top of
the foot and fuller, vented only from the shin down, with its head piece joined on (HOOD, make_hats.py's BurkaHood, made
first into Models/bases: the broadwrap's cloth closed over the face but for a slit over the eyes), taking the body's
weights and keys under it.

With --labcoat it makes the LabCoat (LABCOAT's numbers, Anomaly's lab coat): the duster, open down the front as it is, but
a little narrower at the opening, only to just below the knee and closer to the legs, with a shorter back vent.

Written to <out.glb> as export_frames.py's import_garments takes it (Models/garments/Duster.glb is the garment Duster, for
Apparel_Duster; Robe.glb the Robe, Burka.glb the Burka, LabCoat.glb the LabCoat): the mesh at rest with its keys, its weights and own_weights. out.blend also saves the
.blend with it on the rig; it never saves over the .blend it opened.

With --low (alongside any of them) the skirt is lower poly: LOW's fewer rings and every SKIRT_EVERY-th column.

blender -b Models/pawnHumanRig.blend --python anim_scripts/make_duster.py -- <out.glb> [out.blend] [--robe | --burka | --labcoat] [--low]
"""
import bpy, os, sys
from mathutils import Vector
from mathutils.bvhtree import BVHTree

GROUP = '-Jacket'       # the body's vertex group with this among its names (it also names the shirt and the flak jacket)
NAME = 'Duster'
OFFSET = 0.3           # Blender units the coat stands off the skin (make_from_groups.py's Shell layer, so it goes over a shirt)
OPEN_WIDTH = 1.5       # half the opening's width at OPEN_LOW, Blender units, narrowing to nothing at the neck's V
OPEN_LOW = 12.3        # its height
OPEN_SNAP = 0.08       # a vertex this near the cut is put on it, so the cut leaves no slivers
HEM = 3.8              # the hem's height (mid shin; the shin runs from 6.5 to 1.2)
RINGS = 7              # rings of vertices from the bottom edge down to the hem
EASE = 0.5             # extra room the skirt is given at the hem over the body's reach, growing from none at the top
FLARE = 0.7            # and more again, growing with the square of the way down, so it swings out rather than hanging as a tube
FRONT_THIGH = 1.0      # the legs' share of the weights at the hem, at the front
BACK_THIGH = 0.5      # and at the back
FRONT_CURVE = 0.2     # how soon down the skirt the legs take their share at the front (1 evenly; below 1 sooner, so a raised
                       # thigh lifts the skirt over it from near the top rather than poking through at the hip crease)
BACK_CURVE = 0.8       # and at the back
KNEE = 6.5             # the height the legs' share starts going from the thigh to the shin
FRONT_SHIN = 0.7       # the shin's part of the legs' share at the hem, at the front (so the coat bends down over a bent knee)
BACK_SHIN = 0.45       # and at the back (a heel coming up lifts the tail less)
FRONT_REACH = 1.5      # the front's numbers hold this much further round: the sides go with the thigh too, as a raised thigh lifts them
SIDE_BLEND = 0.5       # across the back (as a share of the way round to the side) the two legs are blended, above the vent
VENT_BLEND = 0.2       # and below the first vent's top, where the two halves part
UV_MARGIN = 0.02       # the gap left between islands when packed, of the square, so a downscaled image doesn't bleed across
INFLUENCES = 4         # the most bones a vertex takes, as a .glb carries no more (the strongest kept, then evened up)
CLEAR_TRIES = 20       # passes pushing the skirt out where the body pokes through it at rest
CLEAR_OVER = 1.5       # each push is this much of the poke, as a vertex moves a face's middle only part of its own move
LIMB_BONES = ('upper_arm', 'forearm', 'hand', 'thumb', 'f_')   # the body's arms are left out of the skirt's reach

SKIRT_EVERY = 1         # the skirt keeps every this many of the bottom edge's columns (its ends and vents always); the first
                        # row of faces fans from the edge's vertices between two kept columns to the first ring
CLOSED = False          # the front closed below OPEN_LOW (the robe), not open all the way down (the duster)
VENTS = {'back': 9.0}   # each vent's top height, the back's ('front' too, closed)
HOOD = None            # a head piece to join on (a .glb from make_hats.py, from the repository's root), or none

# The robe: open in a V at the neck, closed below, to the ankle and fuller, vented back and front from the shin down.
ROBE = dict(NAME='Robe', CLOSED=True, OPEN_WIDTH=0.8, OPEN_LOW=17.9, HEM=1.9, RINGS=10, EASE=0.7, FLARE=1.1,
            VENTS={'back': 5.0, 'front': 4.2}, FRONT_THIGH=0.8, BACK_THIGH=0.5, FRONT_SHIN=0.6, BACK_SHIN=0.4)

# The burka: the robe closed right up to the neck (no V: an opening of no width, so the cut down
# the middle leaves the two halves sharing their vertices), down to the top of the foot and fuller again, its vents only
# from the shin down; with its head piece (HOOD: make_hats.py's BurkaHood, the broadwrap's cloth closed over the face but
# for a slit over the eyes) joined on, as the game has one garment per apparel.
BURKA = dict(ROBE, NAME='Burka', OPEN_WIDTH=0.0, HEM=1.3, RINGS=11, EASE=0.8, FLARE=1.3,
             VENTS={'back': 3.6, 'front': 3.0}, HOOD='Models/bases/burkaHood.glb')

# Anomaly's lab coat: the duster open all the way down, the opening a little narrower, knee length (the hem just under
# the knee's 6.5) and hanging closer, so less ease and flare; the shin takes little, as the hem is barely past the knee,
# and the back vent starts lower.
LABCOAT = dict(NAME='LabCoat', OPEN_WIDTH=1.2, HEM=5.6, RINGS=5, EASE=0.3, FLARE=0.4, VENTS={'back': 8.2},
               FRONT_SHIN=0.3, BACK_SHIN=0.2)

# A lower poly skirt, with any of them: fewer rings and every other column.
LOW = dict(RINGS=3, SKIRT_EVERY=2)

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if '--robe' in argv:
    argv.remove('--robe')
    globals().update(ROBE)
if '--burka' in argv:
    argv.remove('--burka')
    globals().update(BURKA)
if '--labcoat' in argv:
    argv.remove('--labcoat')
    globals().update(LABCOAT)
if '--low' in argv:
    argv.remove('--low')
    globals().update(LOW)
if not argv:
    sys.exit('make_duster.py: give an out .glb')
out = os.path.abspath(argv[0])
blend = argv[1] if len(argv) > 1 else None

for o in bpy.data.objects:   # a mesh saved in Edit Mode has its edits outside its data, and skips its Armature modifier
    if o.type == 'MESH' and o.mode != 'OBJECT':
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode='OBJECT')
body = bpy.data.objects['Body']
rig = body.find_armature()
me = body.data
names = {g.index: g.name for g in body.vertex_groups}
bones = {b.name for b in rig.data.bones if b.use_deform}

def weights_of(i):
    ws = {names[g.group]: g.weight for g in me.vertices[i].groups if names[g.group] in bones and g.weight > 0}
    total = sum(ws.values())
    return {b: w / total for b, w in ws.items()}

group = next(vg.index for vg in body.vertex_groups if GROUP in vg.name.split())
inside = {v.index for v in me.vertices if any(g.group == group and g.weight > 0 for g in v.groups)}
polygons = [list(p.vertices) for p in me.polygons if all(i in inside for i in p.vertices)]

# The opening. Its top is the neck's V, the group's lowest vertex round the neck on the figure's middle.
counts = {}
for f in polygons:
    for k in range(len(f)):
        e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
        counts[e] = counts.get(e, 0) + 1
rim = {i for e, c in counts.items() if c == 1 for i in e}
apex = max((i for i in rim if abs(me.vertices[i].co.x) < 1e-3 and me.vertices[i].co.y < 0), key=lambda i: me.vertices[i].co.z)
apex_z = me.vertices[apex].co.z

def beside(i, sign):
    """How far body vertex i is out of the opening on the side sign (+1 is +x, -1 is -x), by its rest place."""
    c = me.vertices[i].co
    d = sign * c.x - half_width(c.z)
    return 0.0 if abs(d) < OPEN_SNAP else d

def half_width(z):
    """The opening's half width at height z: the duster's widening from the neck's V down, the robe's V narrowing to its
    point at OPEN_LOW and closed below it (none: the two sides' cuts there are the same line, so they share their vertices)."""
    if CLOSED:
        return OPEN_WIDTH * min(1.0, max(0.0, (z - OPEN_LOW) / (apex_z - OPEN_LOW)))
    return OPEN_WIDTH * (apex_z - z) / (apex_z - OPEN_LOW)

# A garment vertex is a body vertex (i, i, 0) or a place on a body edge (a, b, t); `cuts` holds each by that key.
places = []
cuts = {}
def place_of(a, b=None, t=0.0):
    key = (a, a, 0.0) if b is None else (a, b, t)
    if key not in cuts:
        cuts[key] = len(places)
        places.append(key)
    return cuts[key]

def clip(f, sign):
    """Face f's part on the side sign of the opening, in the face's own winding, or None."""
    kept = []
    for k, a in enumerate(f):
        b = f[(k + 1) % len(f)]
        da, db = beside(a, sign), beside(b, sign)
        if da >= 0:
            kept.append(place_of(a))
        if (da > 0 and db < 0) or (da < 0 and db > 0):
            lo, hi = sorted((a, b))   # one way along the edge, so the face either side of it gets the same vertex
            dl, dh = beside(lo, sign), beside(hi, sign)
            kept.append(place_of(lo, hi, dl / (dl - dh)))
    return kept if len(kept) >= 3 else None

faces = []
for f in polygons:
    centre = sum((me.vertices[i].co for i in f), Vector()) / len(f)
    if centre.y >= 0 or all(beside(i, 1) >= 0 for i in f) or all(beside(i, -1) >= 0 for i in f):
        faces.append([place_of(i) for i in f])
    else:
        faces += [part for part in (clip(f, 1), clip(f, -1)) if part]
n_upper = len(places)

# The bottom edge, walked one way from the opening's corner on one side to its corner on the other.
counts = {}
for f in faces:
    for k in range(len(f)):
        e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
        counts[e] = counts.get(e, 0) + 1
next_of = {}
for f in faces:
    for k in range(len(f)):
        a, b = f[k], f[(k + 1) % len(f)]
        if counts[tuple(sorted((a, b)))] == 1:
            next_of[a] = b

def rest_of(q):
    a, b, t = places[q]
    return me.vertices[a].co.lerp(me.vertices[b].co, t)

def on_line(q, sign):
    p = rest_of(q)
    return abs(sign * p.x - half_width(p.z)) < OPEN_SNAP * 1.5 and p.y < 0

chain = None
if CLOSED:
    # The bottom edge is a ring of its own: the open edges' loop through the back's middle that is lowest there (the neck's
    # also goes through it). It starts and ends at that vertex, so the ring's first and last columns are one, parted as a vent.
    backs = [q for q in next_of if abs(rest_of(q).x) < 1e-3 and rest_of(q).y > 0]
    s = min(backs, key=lambda q: rest_of(q).z)
    walk_, x = [s], next_of[s]
    while x != s and len(walk_) <= len(next_of):
        walk_.append(x)
        x = next_of[x]
    if x == s:
        chain = walk_ + [s]
else:
    ends = [min((q for q in next_of if on_line(q, sign)), key=lambda q: rest_of(q).z) for sign in (1, -1)]
    for s, e in (ends, ends[::-1]):
        walk_, x = [s], s
        while x != e and len(walk_) <= len(next_of):
            x = next_of[x]
            walk_.append(x)
        if x == e and max(rest_of(q).z for q in walk_) < OPEN_LOW + 2.5:   # the other way round goes up over the neck
            chain = walk_
if chain is None:
    sys.exit('make_duster.py: no bottom edge' + ('' if CLOSED else ' between the opening\'s corners'))
n_chain = len(chain)

reach_verts = [v.index for v in me.vertices
               if v.co.z < max(rest_of(q).z for q in chain) + 0.5
               and not max(weights_of(v.index).items(), key=lambda bw: bw[1], default=('', 0))[0].startswith(LIMB_BONES)]

def normals(pos):
    n = [Vector() for _ in pos]
    for p in me.polygons:
        vs = list(p.vertices)
        a = pos[vs[0]]
        for k in range(1, len(vs) - 1):
            c = (pos[vs[k]] - a).cross(pos[vs[k + 1]] - a)   # area weighted
            for i in (vs[0], vs[k], vs[k + 1]):
                n[i] += c
    return [x.normalized() for x in n]

# The skirt's columns: every SKIRT_EVERY-th of the bottom edge's, its two ends and each vent's column always kept.
def column_of(back):
    return min(range(n_chain), key=lambda j: abs(rest_of(chain[j]).x) + (0 if (rest_of(chain[j]).y > 0) == back else 100))
vent_cols = {which: 0 if CLOSED and which == 'back' else column_of(which == 'back') for which in VENTS}
cols = sorted(set(range(0, n_chain, SKIRT_EVERY)) | {0, n_chain - 1} | set(vent_cols.values()))
n_cols = len(cols)
col = {j: c for c, j in enumerate(cols)}
def ring_at(sk, k, j):
    """Ring k's (1 the first below the edge) vertex in column j of a skirt from build."""
    return sk[(k - 1) * n_cols + col[j]]

floor_rest = min(v.co.z for v in me.vertices)
top_rest = sum(rest_of(q).z for q in chain) / n_chain
hem_share = (HEM - floor_rest) / (top_rest - floor_rest)

def build(pos):
    """The garment's vertices for the body's vertices at pos: the upper part's, then the skirt's ring by ring."""
    n = normals(pos)
    off = [p + d * OFFSET for p, d in zip(pos, n)]
    upper = [off[a].lerp(off[b], t) for a, b, t in places[:n_upper]]
    top = [upper[q] for q in chain]
    middle = Vector((0, (min(p.y for p in top) + max(p.y for p in top)) / 2, 0))
    floor = min(p.z for p in pos)
    hem = floor + hem_share * (sum(p.z for p in top) / n_chain - floor)
    pts = list(top)
    for k in range(1, RINGS + 1):
        t = k / RINGS
        for j in cols:
            p = top[j]
            way = Vector((p.x - middle.x, p.y - middle.y, 0)).normalized()
            z = p.z + (hem - p.z) * t
            reach = max((pos[v] - middle).dot(way) for v in reach_verts if pos[v].z >= z - 0.3)
            r = max(reach + OFFSET + EASE * t + FLARE * t * t, (p - middle).to_2d().dot(way.to_2d()))
            pts.append(Vector((middle.x + way.x * r, middle.y + way.y * r, z)))
    # Each ring is set out by the body's reach at its own height, but the faces between two rings are flat and can cut
    # inside a bulge of the hip between them. A body vertex the skirt passes inside of pushes the rings of the face it
    # pokes through out, until none does.
    def at(k, c):
        return cols[c] if k == 0 else n_chain + (k - 1) * n_cols + c
    quads = [(at(k, c), at(k + 1, c), at(k + 1, c + 1), at(k, c + 1))
             + (tuple(range(cols[c + 1] - 1, cols[c], -1)) if k == 0 else ())   # the top row fans from every edge vertex
             for k in range(RINGS) for c in range(n_cols - 1)]
    top_z = max(p.z for p in top)
    for _ in range(CLEAR_TRIES):
        tree = BVHTree.FromPolygons(pts, quads)
        push = [0.0] * len(pts)
        for v in reach_verts:
            p = pos[v]
            if p.z > top_z:
                continue
            out_ = Vector((p.x - middle.x, p.y - middle.y, 0))
            far = out_.length
            if far < 1e-6:
                continue
            hit, _, face, gap = tree.ray_cast(Vector((middle.x, middle.y, p.z)), out_ / far, far + OFFSET)
            if hit is not None and far + OFFSET - gap > 1e-4:
                for q in quads[face]:
                    if q >= n_chain:
                        push[q] = max(push[q], (far + OFFSET - gap) * CLEAR_OVER)
        if not any(push):
            break
        for q, d in enumerate(push):
            if d:
                way = Vector((pts[q].x - middle.x, pts[q].y - middle.y, 0)).normalized()
                pts[q] = pts[q] + way * d
    return upper, pts[n_chain:], middle

# The vents: a vent's column (the one nearest the back's or the front's middle) is two from the first ring below its top
# down, one for each half. On the robe's ring the back's column is the first, and the last is the same column: the faces
# either side of it meet there only above the vent.
upper, skirt, middle = build([v.co.copy() for v in me.vertices])
vent_rings = {}
for which, top in VENTS.items():
    j = vent_cols[which]
    vent_rings[j] = next((k for k in range(1, RINGS + 1) if ring_at(skirt, k, j).z <= top), RINGS + 1)
vent_ring = min(vent_rings.values())

def key_of(k, j, side):
    """The vertex at ring k (0 the bottom edge) of column j, for a face on its side side ('after' is the face to j + 1)."""
    if CLOSED and j == n_chain - 1:
        j = 0
    if k == 0:
        return ('edge', chain[j])
    return ('ring', k, j, side == 'after' and k >= vent_rings.get(j, RINGS + 1))

# The skirt's vertices after the upper part's: ring by ring, then each vent's second copies.
index = {('edge', q): q for q in range(n_upper)}
keys = [('ring', k, j, False) for k in range(1, RINGS + 1) for j in cols if not (CLOSED and j == n_chain - 1)]
keys += [('ring', k, j, True) for j, vr in sorted(vent_rings.items()) for k in range(vr, RINGS + 1)]
for key in keys:
    index[key] = len(index)
def skirt_verts(sk):
    return [ring_at(sk, k, j) for _, k, j, _ in keys]

verts = upper + skirt_verts(skirt)
polys = [list(f) for f in faces]
n_faces = len(polys)
for k in range(RINGS):
    for c in range(n_cols - 1):
        j, j2 = cols[c], cols[c + 1]
        between = [chain[i] for i in range(j2 - 1, j, -1)] if k == 0 else []   # the edge's vertices between, back along it
        polys.append([index[key_of(k, j, 'after')], index[key_of(k + 1, j, 'after')],
                      index[key_of(k + 1, j2, 'before')], index[key_of(k, j2, 'before')]] + between)

old = bpy.data.objects.get(NAME)
if old is not None:
    bpy.data.objects.remove(old)
mesh = bpy.data.meshes.new(NAME)
mesh.from_pydata([tuple(v) for v in verts], [], polys)
mesh.update()
for p in mesh.polygons:   # the skirt's quads face out
    p.use_smooth = True
    if p.index >= n_faces:
        c = p.center
        if p.normal.dot(Vector((c.x - middle.x, c.y - middle.y, 0))) < 0:
            p.flip()
mesh.update()
g = bpy.data.objects.new(NAME, mesh)
body.users_collection[0].objects.link(g)
g.parent = rig
g.matrix_world = body.matrix_world.copy()
g.modifiers.new('Armature', 'ARMATURE').object = rig
g['own_weights'] = True


def add_uvs():
    """UVs of our own, for a retexture told to paint on them (place_textured.py strip --keep-uvs). The upper part is cut
    along the body's own seams (unwrap_pawn.py's, from the bones), and the opening and the bottom edge, and flattened
    again, as the cut pieces of the body's islands no longer lie flat; turned upright. The skirt is one island laid out by
    hand: each ring a row at its own length round (centred, so it is a trapezoid wider at the hem), each column down by
    its length, so its rings are level and its columns run down the image. Then all of it at one texel density, packed."""
    loops_uv = mesh.uv_layers.new(name='UVMap')
    body_seams = {tuple(sorted(e.vertices)) for e in me.edges if e.use_seam}
    def body_of(q):
        a, b, t = places[q]
        return {a} if a == b or t == 0.0 else ({b} if t == 1.0 else {a, b})
    upper_edges, skirt_edges = set(), set()
    for p in mesh.polygons:
        for e in p.edge_keys:
            (upper_edges if p.index < n_faces else skirt_edges).add(e)
    for e in mesh.edges:
        k = tuple(sorted(e.vertices))
        if k in upper_edges and k in skirt_edges:
            e.use_seam = True
        elif k[1] < n_upper:
            both = body_of(k[0]) | body_of(k[1])
            e.use_seam = len(both) == 2 and tuple(sorted(both)) in body_seams

    bpy.context.view_layer.objects.active = g
    bpy.context.scene.tool_settings.use_uv_select_sync = True
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_mode(type='FACE')
    bpy.ops.mesh.select_all(action='DESELECT')
    bpy.ops.object.mode_set(mode='OBJECT')
    for p in mesh.polygons:
        p.select = p.index < n_faces
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.uv.unwrap(method='ANGLE_BASED', margin=0.0)
    bpy.ops.uv.align_rotation(method='GEOMETRY', axis='Z')
    bpy.ops.object.mode_set(mode='OBJECT')

    # The skirt: u along each ring, v down each column, in Blender units.
    rest = [Vector(v) for v in verts]
    chain_u = [0.0]
    for i in range(1, n_chain):
        chain_u.append(chain_u[-1] + (rest[chain[i]] - rest[chain[i - 1]]).length)
    chain_u = [u - (chain_u[0] + chain_u[-1]) / 2 for u in chain_u]
    at_chain = {q: i for i, q in enumerate(chain)}
    loops_uv = mesh.uv_layers['UVMap']   # again: going in and out of Edit Mode leaves the old reference pointing at freed data
    ring_u, down = {}, {j: 0.0 for j in cols}
    for k in range(1, RINGS + 1):
        row = [0.0]
        for c in range(1, n_cols):
            row.append(row[-1] + (ring_at(skirt, k, cols[c]) - ring_at(skirt, k, cols[c - 1])).length)
        for c, j in enumerate(cols):
            above = rest[chain[j]] if k == 1 else ring_at(skirt, k - 1, j)
            down[j] += (ring_at(skirt, k, j) - above).length
            ring_u[k, j] = (row[c] - (row[0] + row[-1]) / 2, -down[j])
    for p in mesh.polygons:
        if p.index < n_faces:
            continue
        for li in p.loop_indices:
            vi = mesh.loops[li].vertex_index
            if vi < n_upper:
                loops_uv.data[li].uv = (chain_u[at_chain[vi]], 0.0)
            else:
                _, k, j, _ = keys[vi - n_upper]
                loops_uv.data[li].uv = ring_u[k, j]

    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.average_islands_scale()
    bpy.ops.uv.pack_islands(rotate=False, margin=UV_MARGIN)
    bpy.ops.object.mode_set(mode='OBJECT')
    print('UVS %s: %d seams, %d skirt rows' % (NAME, sum(e.use_seam for e in mesh.edges), RINGS))

add_uvs()

# The head piece, joined on as more faces of the same mesh. Its weights and its moves in the body's keys are the body's
# under each of its vertices (the nearest point of the Body or the Head, the body being split in two), as export_frames.py
# gives a garment taking the body's weights; the Head's own keys it leaves to export_frames.py, which fits every key the
# garment has none of from the body. It gets UVs of its own, packed in with the rest.
n_coat = len(mesh.vertices)
hood_points = []
if HOOD:
    path = os.path.abspath(HOOD)
    if not os.path.exists(path):
        sys.exit('make_duster.py: no %s: make it first (make_hats.py -- Models/bases BurkaHood)' % path)
    import bmesh
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    came = [o for o in bpy.data.objects if o not in before]
    hm = next(o for o in came if o.type == 'MESH')
    hb = bmesh.new()
    hb.from_mesh(hm.data)
    hb.transform(body.matrix_world.inverted() @ hm.matrix_world)
    bmesh.ops.remove_doubles(hb, verts=hb.verts, dist=1e-4)   # the importer splits vertices the exporter split
    bmesh.ops.delete(hb, geom=[v for v in hb.verts if not v.link_faces], context='VERTS')   # the opening's inner points
    for o in came:
        bpy.data.objects.remove(o)
    cb = bmesh.new()
    cb.from_mesh(mesh)
    new_verts = [cb.verts.new(v.co) for v in hb.verts]
    hb.verts.index_update()
    hood_faces = []
    for f in hb.faces:
        nf = cb.faces.new([new_verts[v.index] for v in f.verts])
        nf.smooth = True
        hood_faces.append(nf)
    hood_points = [Vector(v.co) for v in hb.verts]
    hb.free()
    cb.to_mesh(mesh)
    cb.free()
    mesh.update()
    # Its UVs: a projection of its own, then everything packed again.
    bpy.context.view_layer.objects.active = g
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='DESELECT')
    bpy.ops.object.mode_set(mode='OBJECT')
    hood_from = len(mesh.polygons) - len(hood_faces)
    for p in mesh.polygons:
        p.select = p.index >= hood_from
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.uv.smart_project(island_margin=UV_MARGIN)
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.average_islands_scale()
    bpy.ops.uv.pack_islands(rotate=False, margin=UV_MARGIN)
    bpy.ops.object.mode_set(mode='OBJECT')

    # The surface under it: the Body's and the Head's triangles, in the Body's frame.
    head = bpy.data.objects.get('Head')
    parts = [body] + ([head] if head is not None and head is not body else [])
    surf_pts, surf_tris, surf_of = [], [], []   # surf_of: each point's (object, vertex index)
    for o in parts:
        to_body = body.matrix_world.inverted() @ o.matrix_world
        start = len(surf_pts)
        surf_pts += [to_body @ v.co for v in o.data.vertices]
        surf_of += [(o, i) for i in range(len(o.data.vertices))]
        o.data.calc_loop_triangles()
        surf_tris += [tuple(start + i for i in t.vertices) for t in o.data.loop_triangles]
    surf_tree = BVHTree.FromPolygons(surf_pts, surf_tris)
    def under(q):
        """The surface triangle nearest q and q's barycentric shares of its corners."""
        loc, _, ti, _ = surf_tree.find_nearest(q)
        a, b, c = (surf_pts[i] for i in surf_tris[ti])
        from mathutils.geometry import barycentric_transform
        w = barycentric_transform(loc, a, b, c, Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)))
        w = [max(0.0, x) for x in w]
        total = sum(w) or 1.0
        return surf_tris[ti], [x / total for x in w]
    hood_under = [under(q) for q in hood_points]
    def part_weights(o, i):
        gn = {vg.index: vg.name for vg in o.vertex_groups}
        ws = {gn[x.group]: x.weight for x in o.data.vertices[i].groups if gn[x.group] in bones and x.weight > 0}
        total = sum(ws.values())
        return {b: w / total for b, w in ws.items()} if total > 0 else {}
    def part_delta(o, i, name):
        keys = o.data.shape_keys
        kb = keys.key_blocks.get(name) if keys else None
        if kb is None:
            return Vector()
        to_body = (body.matrix_world.inverted() @ o.matrix_world).to_3x3()
        return to_body @ (kb.data[i].co - keys.reference_key.data[i].co)
    print('HOOD %s: %d vertices, %d faces joined on' % (path, len(hood_points), len(hood_faces)))

def put(vi, ws):
    strongest = sorted(ws.items(), key=lambda bw: -bw[1])[:INFLUENCES]
    total = sum(w for _, w in strongest)
    for b, w in strongest:
        if w / total > 1e-4:
            (g.vertex_groups.get(b) or g.vertex_groups.new(name=b)).add([vi], w / total, 'REPLACE')

def upper_weights(q):
    a, b, t = places[q]
    ws = {x: w * (1 - t) for x, w in weights_of(a).items()}
    for x, w in weights_of(b).items():
        ws[x] = ws.get(x, 0) + w * t
    return ws

for q in range(n_upper):
    put(q, upper_weights(q))

# Which leg is on the +x side, as the body's own weights have it (not by the bones' names: the skirt must go as the legs
# under it do, whatever the body's weights say).
def plus_first(a, b):
    side = {a: 0.0, b: 0.0}
    for v in me.vertices:
        for x, w in weights_of(v.index).items():
            if x in side:
                side[x] += w * v.co.x
    return sorted(side, key=side.get, reverse=True)
plus_thigh, minus_thigh = plus_first('thigh.L', 'thigh.R')
plus_shin, minus_shin = plus_first('shin.L', 'shin.R')

def skirt_weights(k, j, plus_side=None):
    """Ring k's weights in column j. plus_side, on the vent's two copies, says which half this copy is of."""
    top = upper_weights(chain[j])
    p = upper[chain[j]]
    way = Vector((p.x - middle.x, p.y - middle.y, 0)).normalized()
    z = ring_at(skirt, k, j).z
    blend = VENT_BLEND if k >= vent_ring else SIDE_BLEND
    left = min(1.0, max(0.0, 0.5 + way.x / (2 * blend)))   # the share of the leg on the +x side
    left = left * left * (3 - 2 * left)
    if plus_side is not None:
        left = 1.0 if plus_side else 0.0
    front = min(1.0, (0.5 - 0.5 * way.y) * FRONT_REACH)   # -y is the front
    share = (BACK_THIGH + (FRONT_THIGH - BACK_THIGH) * front) * (k / RINGS) ** (BACK_CURVE + (FRONT_CURVE - BACK_CURVE) * front)
    shin = (BACK_SHIN + (FRONT_SHIN - BACK_SHIN) * front) * min(1.0, max(0.0, (KNEE - z) / (KNEE - HEM)))
    ws = {b: w * (1 - share) for b, w in top.items()}
    for b, w in ((plus_thigh, share * left * (1 - shin)), (minus_thigh, share * (1 - left) * (1 - shin)),
                 (plus_shin, share * left * shin), (minus_shin, share * (1 - left) * shin)):
        if w > 0:
            ws[b] = ws.get(b, 0) + w
    return ws

# Which copy is which: a vent's second copy (used by the face toward column j + 1) goes with that column's side, the first
# with the side of the column before it.
def plus_side(j, after):
    n = j + 1 if after else (j - 1 if j > 0 else n_chain - 2)   # the edge's next vertex that way: on the same side as the next column
    return rest_of(chain[n]).x > 0
for key in keys:
    _, k, j, second = key
    side = plus_side(j, second) if k >= vent_rings.get(j, RINGS + 1) else None
    put(index[key], skirt_weights(k, j, side))
for h, (tri, bw) in enumerate(hood_under):
    ws = {}
    for i, t in zip(tri, bw):
        for b, w in part_weights(*surf_of[i]).items():
            ws[b] = ws.get(b, 0.0) + w * t
    put(n_coat + h, ws)

# The body's keys, built from the keyed body.
g.shape_key_add(name='Basis', from_mix=False)
for kb in me.shape_keys.key_blocks[1:]:
    u, s, _ = build([Vector(d.co) for d in kb.data])
    moved = u + skirt_verts(s)
    key = g.shape_key_add(name=kb.name, from_mix=False)
    key.value = 0.0
    for k, p in enumerate(moved):
        key.data[k].co = p
    for h, (tri, bw) in enumerate(hood_under):
        key.data[n_coat + h].co = hood_points[h] + sum((part_delta(*surf_of[i], kb.name) * t for i, t in zip(tri, bw)), Vector())

print('%s +x thigh %s, %d vertices, %d faces (%d from the body, %d skirt), bottom edge of %d (%d skirt columns), vents from rings %s, keys %s'
      % (NAME.upper(), plus_thigh, len(verts), len(polys), n_faces, len(polys) - n_faces, n_chain, n_cols,
         ', '.join('%d' % r for r in vent_rings.values()), ', '.join(k.name for k in me.shape_keys.key_blocks[1:])))

rig.hide_set(False)   # a hidden rig can't be selected, and without it the file has no skin: every vertex would ride the hips
rig.hide_viewport = False
rig.hide_select = False
bpy.ops.object.select_all(action='DESELECT')
g.select_set(True)
rig.select_set(True)
if not rig.select_get():
    sys.exit('make_duster.py: the rig can\'t be selected, so the .glb would have no weights')
bpy.context.view_layer.objects.active = g
os.makedirs(os.path.dirname(out), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_extras=True,
                          export_skins=True, export_morph=True, export_morph_normal=False,
                          export_animations=False, export_apply=False, export_materials='NONE')
before = set(bpy.data.objects)   # read it back: the weights are what this file is for
bpy.ops.import_scene.gltf(filepath=out, disable_bone_shape=True)
back = [o for o in bpy.data.objects if o not in before]
weighted = sum(1 for o in back if o.type == 'MESH' for v in o.data.vertices if v.groups)
came = sum(len(o.data.vertices) for o in back if o.type == 'MESH')   # more than verts: a glb splits them at UV seams
for o in back:
    bpy.data.objects.remove(o)
if weighted != came:
    sys.exit('make_duster.py: %d of %d vertices have weights in the .glb' % (weighted, came))
print('GLB %s' % out)
if blend:
    bpy.ops.wm.save_as_mainfile(filepath=blend, copy=True)
