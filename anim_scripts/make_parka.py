"""Makes the Parka garment: a puffy quilted winter coat.

Pushing the body's own faces out (make_from_groups.py, make_duster.py's top) doesn't do for this: the body's faces are as big
as a baffle and uneven, and all that unevenness comes through as lumps when pushed this far, with flaps in the armpits. So
the coat is built of rings, as make_duster.py's skirt is, each ring set out by how far the body reaches each way:

- The trunk is one barrel from the hem to the neck, round the trunk and hips only, not the arms. A ring takes the body
  vertices at its height (within TRUNK_WINDOW; below HEM_CUT, everything from there up to HEM_CUT, so it hangs clear of both
  legs), chosen once at rest, and its middle is theirs.
- Each sleeve is a tube of the same kind round the arm, along the line from the shoulder to the wrist. It starts inside the
  barrel and isn't joined to it, so an arm can go anywhere without tearing anything: it comes out of the coat's side as
  the two surfaces meet, and the outline goes round both. Its cuff is drawn in to the skin (CUFF_OFFSET).
- Round the neck is a stand collar, one fat roll (COLLAR), the barrel's last rings.

Each ring stands OFFSET off the body's reach, plus its baffle's profile (PUFF, PROFILE: none on a seam, all of it midway). There
are no UVs and no image: the quilting is all in the shape, which the game's smooth shading picks out. A zip down the front
(ZIP) is the same again, less stand-off toward the figure's middle. Weights come from the nearest body vertices at rest of the
part the ring goes round; below HEM_CUT they go over to the thigh on that side, the front more than the back, so a raised thigh lifts
the skirt. Every body key's shape is built the same way from the keyed body, with the same vertices in each ring, and goes in
as the garment's own key.

One surface, no UVs: drawn in the apparel's colour, both sides, as the game's garments don't cull.

Written to <out.glb> as export_frames.py's import_garments takes it (Models/garments/Parka.glb is the garment Parka, for
Apparel_Parka; the game's AdultOf gives Apparel_KidParka the same): the mesh at rest with its keys, its weights and
own_weights. out.blend also saves the .blend with it on the rig; it never saves over the .blend it opened.

blender -b Models/pawnHumanRig.blend --python anim_scripts/make_parka.py -- <out.glb> [out.blend]
"""
import bpy, math, os, sys
from mathutils import Vector

NAME = 'Parka'
TRUNK_SEAMS = (10.9, 13.0, 15.1, 17.1, 19.5)   # heights of the barrel's seams, from the hem up (the thigh runs from 12.5 to 6.3)
HEM_CUT = 13.0          # below this the barrel hangs clear of the body as a skirt
TRUNK_COLUMNS = 20      # vertices round the barrel (even, so one is on the figure's middle at the front and back)
TRUNK_WINDOW = 0.9      # Blender units either side of a ring's height that its body vertices lie within
TOP_WINDOW = 0.45       # and from TOP_FROM up, so the shoulders slope in to the neck rather than standing out as a shelf
TOP_FROM = 18.0
SHOULDER_IN = 1.0       # the arm's vertices up to this far along it count as the trunk's too, so the barrel covers the shoulder
SLEEVE_SEAMS = (0.2, 2.0, 3.9, 5.8)   # Blender units along the arm from the upper arm's head; the first is inside the barrel
CUFF_END = 6.7          # where the sleeve stops, at the wrist
CUFF_RINGS = (6.1,)     # the rings between the last seam and that
SLEEVE_COLUMNS = 10     # vertices round a sleeve
SLEEVE_WINDOW = 1.0     # as TRUNK_WINDOW, along the arm
LINES = 4               # rings per baffle, so 3 between its seams to make it round
OFFSET = 0.55           # Blender units the coat stands off the body's reach at the seams (make_from_groups.py's Shell layer is 0.3)
PUFF = 0.55             # and how much further at a baffle's middle
SLEEVE_PUFF = 0.42      # on a sleeve, whose baffles are narrower
PROFILE = 0.7           # a baffle's shape: sine to this power, so below 1 it is full across and pinched at the seams
CUFF_OFFSET = 0.2       # the cuff's stand-off, as it is drawn in
TRUNK_PASSES = 2        # passes evening out the barrel's rings over their neighbours, as few body vertices set each
SLEEVE_PASSES = 5       # and a sleeve's: an arm has fewer still, and it saws to and fro without
SMOOTH = 0.5            # how far each pulls toward them
ZIP = 1.0               # how far to each side of the middle the zip's dip reaches, front only
ZIP_DEPTH = 0.2         # how much less the coat stands off at the zip itself
SKIRT_EASE = 0.25       # extra room over the body's reach at the hem, growing from none at HEM_CUT
FRONT_THIGH = 0.9       # the legs' share of the weights at the hem, at the front
BACK_THIGH = 0.35       # and at the back
FRONT_REACH = 1.2       # the front's share holds this much further round, so the sides lift with a raised thigh
SIDE_BLEND = 0.6        # across the front and back (as a share of the way round to the side) the two legs are blended
COLLAR = ((0.95, -0.5), (1.5, -0.1), (1.6, 0.35), (1.1, 0.7), (0.35, 0.65))   # the roll's rings: out from the neck, up from the neck edge's mean height
NECK_GROUP = '-CollarShirt -Jacket'   # the collar stands on this group's edge round the neck
NEAREST = 4             # body vertices a ring vertex takes its weights from
INFLUENCES = 4          # the most bones a vertex takes, as a .glb carries no more
LIMB_BONES = ('upper_arm', 'forearm', 'hand', 'thumb', 'f_')   # the bones of an arm

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_parka.py: give an out .glb')
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
    return {b: w / total for b, w in ws.items()} if total else {}

def strongest_bone(i):
    return max(weights_of(i).items(), key=lambda bw: bw[1], default=('', 0))[0]

def body_point(bone):
    return body.matrix_world.inverted() @ rig.matrix_world @ rig.data.bones[bone].head_local

def smoothstep(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3 - 2 * x)

def lines_of(seams):
    """The rings' places for a run of seams, in order: each seam and LINES - 1 between it and the next, and how full the
    baffle is there, 0 on a seam."""
    out_ = []
    for a, b in zip(seams, seams[1:]):
        for k in range(LINES):
            out_.append((a + (b - a) * k / LINES, math.sin(math.pi * k / LINES) ** PROFILE if k else 0.0))
    out_.append((seams[-1], 0.0))
    return out_

class Part:
    """A run of rings round a part of the body: each ring a set of body vertices chosen at rest, its plane at `level` along
    `axis`, its columns going round by `ways`, each a unit vector across the axis. keep_out: the evening out may raise a
    ring's reach but not lower it, so the body can't come through (the trunk's breastbone stands out from its neighbours)."""
    def __init__(self, axis, ways, levels, sets, passes, keep_out):
        self.axis, self.ways, self.levels, self.sets, self.passes, self.keep_out = axis, ways, levels, sets, passes, keep_out

    def reach(self, pos):
        """Each ring's middle and how far the body reaches each way from it, evened out over its neighbours."""
        n = len(self.levels)
        mids = []
        for k, s in enumerate(self.sets):
            at = sum((pos[v] for v in s), Vector()) / len(s)
            mids.append(at)
        rest_along = [sum((me.vertices[v].co for v in s), Vector()).dot(self.axis) / len(s) for s in self.sets]
        for _ in range(self.passes):   # the middles first, or the reach measured from them saws as they do
            mids = [mids[k] + (((mids[k - 1] if k else mids[k]) + (mids[k + 1] if k < n - 1 else mids[k])) / 2 - mids[k]) * SMOOTH
                    for k in range(n)]
        # Along the axis each stays at its level, moved only as its vertices have moved.
        moved = [sum((pos[v] for v in s), Vector()).dot(self.axis) / len(s) - rest_along[k] for k, s in enumerate(self.sets)]
        mids = [mids[k] + self.axis * (self.levels[k][0] + moved[k] - mids[k].dot(self.axis)) for k in range(n)]
        raw = [[max((pos[v] - mids[k]).dot(w) for v in self.sets[k]) for w in self.ways] for k in range(n)]
        rs = raw
        m = len(self.ways)
        for _ in range(self.passes):
            new = []
            for k in range(n):
                row = []
                for j in range(m):
                    near = [rs[k][(j - 1) % m], rs[k][(j + 1) % m]] + [rs[x][j] for x in (k - 1, k + 1) if 0 <= x < n]
                    r = rs[k][j] + (sum(near) / len(near) - rs[k][j]) * SMOOTH
                    row.append(max(r, raw[k][j]) if self.keep_out else r)
                new.append(row)
            rs = new
        return mids, rs

def bands(faces, base, n_rings, n_columns, closed=True):
    """The quads between a part's rings, whose first vertex is base."""
    for k in range(n_rings - 1):
        for j in range(n_columns if closed else n_columns - 1):
            j2 = (j + 1) % n_columns
            faces.append([base + k * n_columns + j, base + k * n_columns + j2,
                          base + (k + 1) * n_columns + j2, base + (k + 1) * n_columns + j])

limb = {v.index for v in me.vertices if strongest_bone(v.index).startswith(LIMB_BONES)}
core = [v.index for v in me.vertices if v.index not in limb]

# The collar stands on the group's edge round the neck.
group = body.vertex_groups[NECK_GROUP].index
inside = {v.index for v in me.vertices if any(g.group == group and g.weight > 0 for g in v.groups)}
counts = {}
for p in me.polygons:
    if all(i in inside for i in p.vertices):
        vs = list(p.vertices)
        for k, a in enumerate(vs):
            e = tuple(sorted((a, vs[(k + 1) % len(vs)])))
            counts[e] = counts.get(e, 0) + 1
neck_edge = [i for i in {i for e, c in counts.items() if c == 1 for i in e}
             if abs(me.vertices[i].co.x) < 2 and me.vertices[i].co.z > TRUNK_SEAMS[-1] - 0.5]

# The sleeves, from inside the barrel to the wrist, by the side each is on (+1 is +x).
sleeves = {}
shoulders = []
for suffix in ('L', 'R'):
    start = body_point('upper_arm.' + suffix)
    sign = 1 if start.x > 0 else -1
    axis = (body_point('hand.' + suffix) - start).normalized()
    up = (Vector((0, 0, 1)) - axis * axis.z).normalized()
    across = axis.cross(up)
    ways = [(up * math.cos(2 * math.pi * j / SLEEVE_COLUMNS) + across * math.sin(2 * math.pi * j / SLEEVE_COLUMNS))
            for j in range(SLEEVE_COLUMNS)]
    levels = lines_of(SLEEVE_SEAMS) + [(t, 0.0) for t in CUFF_RINGS] + [(CUFF_END, 0.0)]
    arm = [v for v in limb if me.vertices[v].co.x * sign > 0]
    shoulders += [v for v in arm if (me.vertices[v].co - start).dot(axis) < SHOULDER_IN]
    def sleeve_set(t):
        here = [v for v in arm if abs((me.vertices[v].co - start).dot(axis) - t) <= SLEEVE_WINDOW]
        return here or sorted(arm, key=lambda v: abs((me.vertices[v].co - start).dot(axis) - t))[:6]
    part = Part(axis, ways, [(start.dot(axis) + t, f) for t, f in levels], [sleeve_set(t) for t, _ in levels], SLEEVE_PASSES, False)
    part.along = [t for t, _ in levels]
    part.arm = arm
    sleeves[sign] = part

# The barrel, the hem first.
trunk_ways = [Vector((math.sin(2 * math.pi * j / TRUNK_COLUMNS), -math.cos(2 * math.pi * j / TRUNK_COLUMNS), 0))
              for j in range(TRUNK_COLUMNS)]   # column 0 is the middle of the front (-y)
trunk_levels = lines_of(TRUNK_SEAMS)
def trunk_set(z):
    if z < HEM_CUT:
        return [v for v in core if z - 0.3 <= me.vertices[v].co.z <= HEM_CUT + 0.5]
    window = TOP_WINDOW if z >= TOP_FROM else TRUNK_WINDOW
    return [v for v in core + shoulders if abs(me.vertices[v].co.z - z) <= window]
barrel = Part(Vector((0, 0, 1)), trunk_ways, trunk_levels, [trunk_set(z) for z, _ in trunk_levels], TRUNK_PASSES, True)

def build(pos):
    """All the garment's vertices for the body's vertices at pos: the barrel's and collar's rings, then each sleeve's."""
    mids, rs = barrel.reach(pos)
    pts = []
    for k, (z, full) in enumerate(trunk_levels):
        ease = SKIRT_EASE * smoothstep((HEM_CUT - z) / (HEM_CUT - TRUNK_SEAMS[0]))
        for j, w in enumerate(trunk_ways):
            near_zip = smoothstep(abs(w.x) * rs[k][j] / ZIP) if w.y < 0 else 1.0
            r = rs[k][j] + ease + OFFSET - ZIP_DEPTH * (1 - near_zip) + PUFF * full * near_zip
            pts.append(mids[k] + w * r)
    bare = [pos[v] for v in neck_edge]
    mid = sum(bare, Vector()) / len(bare)
    reach = [max((b - mid).dot(w) for b in bare) for w in trunk_ways]
    for out_, up in COLLAR:
        for j, w in enumerate(trunk_ways):
            pts.append(mid + w * (reach[j] + out_) + Vector((0, 0, up)))
    for sign in (1, -1):
        part = sleeves[sign]
        mids, rs = part.reach(pos)
        for k, t in enumerate(part.along):
            stand = CUFF_OFFSET + (OFFSET - CUFF_OFFSET) * smoothstep((CUFF_END - t) / (CUFF_END - SLEEVE_SEAMS[-1]) * 0.5 if t > SLEEVE_SEAMS[-1] else 1.0)
            for j, w in enumerate(part.ways):
                pts.append(mids[k] + w * (rs[k][j] + stand + SLEEVE_PUFF * part.levels[k][1]))
    return pts

verts = build([v.co.copy() for v in me.vertices])
n_barrel = len(trunk_levels) + len(COLLAR)
polys = []
bands(polys, 0, n_barrel, TRUNK_COLUMNS)
n_trunk_faces = len(polys)
base = n_barrel * TRUNK_COLUMNS
sleeve_base = {}
for sign in (1, -1):
    sleeve_base[sign] = base
    bands(polys, base, len(sleeves[sign].levels), SLEEVE_COLUMNS)
    base += len(sleeves[sign].levels) * SLEEVE_COLUMNS

old = bpy.data.objects.get(NAME)
if old is not None:
    bpy.data.objects.remove(old)
mesh = bpy.data.meshes.new(NAME)
mesh.from_pydata([tuple(v) for v in verts], [], polys)
mesh.update()
# A part's rings are one grid, so one winding does for all of it, the collar's doubling back too: tried on its first rings,
# which go straight round the middle.
parts = [(0, n_trunk_faces, Vector((0, sum(v.co.y for v in me.vertices) / len(me.vertices), 0)), Vector((0, 0, 1)))]
at = n_trunk_faces
for sign in (1, -1):
    part = sleeves[sign]
    count = (len(part.levels) - 1) * SLEEVE_COLUMNS
    parts.append((at, at + count, sum((me.vertices[v].co for v in part.arm), Vector()) / len(part.arm), part.axis))
    at += count
for first, last, centre, axis in parts:
    faces = mesh.polygons[first:first + 2 * TRUNK_COLUMNS if first == 0 else first + 2 * SLEEVE_COLUMNS]
    turned = sum(p.normal.dot((p.center - centre) - axis * (p.center - centre).dot(axis)) for p in faces) < 0
    for p in mesh.polygons[first:last]:
        p.use_smooth = True
        if turned:
            p.flip()
mesh.update()
g = bpy.data.objects.new(NAME, mesh)
body.users_collection[0].objects.link(g)
g.parent = rig
g.matrix_world = body.matrix_world.copy()
g.modifiers.new('Armature', 'ARMATURE').object = rig
g['own_weights'] = True

def put(vi, ws):
    strongest = sorted(ws.items(), key=lambda bw: -bw[1])[:INFLUENCES]
    total = sum(w for _, w in strongest)
    for b, w in strongest:
        if w / total > 1e-4:
            (g.vertex_groups.get(b) or g.vertex_groups.new(name=b)).add([vi], w / total, 'REPLACE')

def nearest_weights(p, among, leave_out=()):
    """Weights mixed from the NEAREST body vertices of `among` to p, by inverse square distance, without the bones named."""
    near = sorted(among, key=lambda v: (me.vertices[v].co - p).length_squared)[:NEAREST]
    ws = {}
    for v in near:
        d = 1.0 / max(1e-4, (me.vertices[v].co - p).length_squared)
        for b, w in weights_of(v).items():
            if not b.startswith(leave_out) if leave_out else True:
                ws[b] = ws.get(b, 0.0) + w * d
    return ws

def plus_first(a, b):
    """Which leg is on the +x side, as the body's own weights have it."""
    side = {a: 0.0, b: 0.0}
    for v in me.vertices:
        for x, w in weights_of(v.index).items():
            if x in side:
                side[x] += w * v.co.x
    return sorted(side, key=side.get, reverse=True)
plus_thigh, minus_thigh = plus_first('thigh.L', 'thigh.R')

above_hem = [v for v in core if me.vertices[v].co.z >= HEM_CUT - 0.5]
hem_ring = next(k for k, (z, _) in enumerate(trunk_levels) if z >= HEM_CUT)
neck_core = [v for v in core if me.vertices[v].co.z >= TRUNK_SEAMS[-1] - 0.5]
for k, (z, _) in enumerate(trunk_levels):
    for j, w in enumerate(trunk_ways):
        vi = k * TRUNK_COLUMNS + j
        if z >= HEM_CUT:
            put(vi, nearest_weights(Vector(verts[vi]) - w * OFFSET, above_hem, LIMB_BONES))
        else:
            top = nearest_weights(Vector(verts[hem_ring * TRUNK_COLUMNS + j]) - w * OFFSET, above_hem, LIMB_BONES)
            total = sum(top.values())
            t = (HEM_CUT - z) / (HEM_CUT - TRUNK_SEAMS[0])
            left = smoothstep(0.5 + w.x / (2 * SIDE_BLEND))
            front = min(1.0, (0.5 - 0.5 * w.y) * FRONT_REACH)   # -y is the front
            share = (BACK_THIGH + (FRONT_THIGH - BACK_THIGH) * front) * t
            ws = {b: x / total * (1 - share) for b, x in top.items()}
            ws[plus_thigh] = ws.get(plus_thigh, 0.0) + share * left
            ws[minus_thigh] = ws.get(minus_thigh, 0.0) + share * (1 - left)
            put(vi, ws)
for k in range(len(COLLAR)):
    for j in range(TRUNK_COLUMNS):
        vi = (len(trunk_levels) + k) * TRUNK_COLUMNS + j
        put(vi, nearest_weights(Vector(verts[vi]), neck_core, LIMB_BONES))
for sign in (1, -1):
    part = sleeves[sign]
    for k in range(len(part.levels)):
        for j, w in enumerate(part.ways):
            vi = sleeve_base[sign] + k * SLEEVE_COLUMNS + j
            put(vi, nearest_weights(Vector(verts[vi]) - w * OFFSET, part.arm))

# The body's keys, built from the keyed body.
g.shape_key_add(name='Basis', from_mix=False)
for kb in me.shape_keys.key_blocks[1:]:
    moved = build([Vector(d.co) for d in kb.data])
    key = g.shape_key_add(name=kb.name, from_mix=False)
    key.value = 0.0
    for k, p in enumerate(moved):
        key.data[k].co = p

print('PARKA %d vertices, %d triangles (barrel and collar %d, sleeves %d), keys %s'
      % (len(verts), 2 * len(polys), 2 * n_trunk_faces, 2 * (len(polys) - n_trunk_faces),
         ', '.join(k.name for k in me.shape_keys.key_blocks[1:])))

rig.hide_set(False)   # a hidden rig can't be selected, and without it the file has no skin: every vertex would ride the hips
rig.hide_viewport = False
rig.hide_select = False
bpy.ops.object.select_all(action='DESELECT')
g.select_set(True)
rig.select_set(True)
if not rig.select_get():
    sys.exit('make_parka.py: the rig can\'t be selected, so the .glb would have no weights')
bpy.context.view_layer.objects.active = g
os.makedirs(os.path.dirname(out), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_extras=True,
                          export_skins=True, export_morph=True, export_morph_normal=False,
                          export_animations=False, export_apply=False, export_materials='NONE')
before = set(bpy.data.objects)   # read it back: the weights are what this file is for
bpy.ops.import_scene.gltf(filepath=out, disable_bone_shape=True)
back_in = [o for o in bpy.data.objects if o not in before]
weighted = sum(1 for o in back_in if o.type == 'MESH' for v in o.data.vertices if v.groups)
for o in back_in:
    bpy.data.objects.remove(o)
if weighted != len(verts):
    sys.exit('make_parka.py: %d of %d vertices have weights in the .glb' % (weighted, len(verts)))
print('GLB %s' % out)
if blend:
    bpy.ops.wm.save_as_mainfile(filepath=blend, copy=True)
