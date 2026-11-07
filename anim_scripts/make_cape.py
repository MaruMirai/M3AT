"""Makes the Cape garment (Apparel_Cape): one sheet from a high collar round the neck, down the back behind the arms,
opening out below the hands to the sides at the hem (mid shin), so the arms never go through it.

It is built from rings, as make_duster.py builds the duster's skirt, not from the body's faces: each ring a fixed height,
each of its columns a fixed way out from the trunk's middle line, set out as far as the trunk (not the arms) reaches that
way at or above that height, so it drapes from the widest place above it and falls straight. At the top only the neck
counts, so the collar hugs it. How far round the back each front edge may come at each height is measured from the
arms (edge_limits): every arm vertex standing and through the walk and the run (arm_sweep), the edge kept ARM_GAP short
of the furthest round any of them goes near that height, so they swing past its edges. Above the shoulders that lets the
collar come round to the throat (CLASP); below the lowest the hands go it opens out to LEFT_WRAP and RIGHT_WRAP at the
hem. The rig's rest pose has its arms out level, so it is built round the body in the standing pose (walk.stand_pose)
and taken back to the rest pose by undoing each vertex's own skinning (as open_limbs.py does). Its weights are the
trunk's under it, and below the hips the thighs' and shins', the front edges more than the back, which hangs clear of
both legs between them. (Models/unused/Cape_overArms.glb is an earlier one hung over the arms, which they poke through.)

The hem rises HEM_RISE toward the front edges.

Each body key's shape is built the same way from the keyed body (the edges where the base shape has them), and goes in as the
garment's own key. One surface, no UVs: drawn in the apparel's colour, both sides, as the game's garments don't cull.

Written to <out.glb> as export_frames.py's import_garments takes it (Models/garments/Cape.glb is the garment Cape, for
Apparel_Cape; Cape is in KEEP_WEIGHTS there): the mesh at rest with its keys and its weights. out.blend also saves the
.blend with it on the rig; it never saves over the .blend it opened.

blender -b Models/pawnHumanRig.blend --python anim_scripts/make_cape.py -- <out.glb> [out.blend]
"""
import bpy, math, os, sys
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

NAME = 'Cape'
OFFSET = 0.3           # Blender units the cape stands off the skin (the Shell layer's, as the duster's)
COLLAR = 19.9          # the collar's height (the neck's foot; the shoulder joints are at 18.9)
SHOULDER = 18.6        # the height the sheet starts to fall straight and flare from
HEM = 3.6              # the hem's height at the back (mid shin; the shin runs from 6.5 to 1.2)
HEM_RISE = 1.2         # how much higher the hem is at the front edges, growing with the square of the way round
RINGS = 22             # rings from the collar down to the hem
RING_CURVE = 1.35      # how much closer together the rings are near the top (1 evenly), where the shoulders bend it
COLUMNS = 36           # columns from the right front edge round the back to the left one
LEFT_WRAP = 110.0      # degrees round from the back's middle to the left front edge at the hem (180 is the front's middle)
RIGHT_WRAP = 110.0     # and to the right one
CLASP = 168.0          # both edges at the collar, so it closes at the throat (where the arms leave room)
CLASP_DROP = 1.0       # the edges go from CLASP to their WRAP over this much down from the collar
BAND = 0.3             # a ring counts the body from this far below it up to the collar
EASE = 0.4             # extra room at the hem over the body's reach, growing from none at SHOULDER
FLARE = 0.8            # and more again, growing with the square of the way down, so it swings out
EDGE_FLARE = 0.3       # the share of it the front edges keep, so it swings out behind rather than in front
HIPS = 12.6            # below this the trunk's weights are the hips' (the thighs' joints are at 12.66)
KNEE = 6.5             # the height the legs' share starts going from the thigh to the shin
FRONT_LEGS = 0.7       # the legs' share at the hem at the front edges
BACK_LEGS = 0.35       # and at the back's middle
LEG_CURVE = 1.3        # how late down the skirt the legs take their share (1 evenly)
FRONT_SHIN = 0.6       # the shin's part of the legs' share at the hem, at the front edges
BACK_SHIN = 0.3        # and at the back
SIDE_BLEND = 0.5       # across the back (as a share of the way round to the side) the two legs are blended
SMOOTH_PASSES = 3      # passes evening the weights out over the sheet's edges
INFLUENCES = 4         # the most bones a vertex takes, as a .glb carries no more
CLEAR_TRIES = 20       # passes pushing the sheet out where the body pokes through it
CLEAR_OVER = 1.5       # each push is this much of the poke
FILL_PASSES = 6        # passes filling in dents between neighbouring corners
SLOPE_REACH = 2.5      # a body point less than this below SHOULDER is looked at along a line from the collar's middle
ARM_SHARE = 0.0        # the upper arm's share of the weights straight out over it (none: the cape keeps behind the arms)
ARM_GAP = 8.0          # degrees the front edges keep short of the furthest round the arms go at that height
ARM_BAND = 0.7         # an arm vertex this far above or below a ring counts for it
WIDEN_CURVE = 0.8      # below the lowest the hands go the edges open out to their WRAP at the hem, evenly at 1, sooner below 1
EDGE_SMOOTH = 3        # passes smoothing the edges' line down the rings
SWEEP_GAITS = (0.0, 1.0)   # the gaits (walk.set_gait) the arms are swung through, walk and run
SWEEP_STEPS = 12       # poses through each gait's cycle
ARM_SPREAD = 50.0      # degrees round either side of straight out over the arm it fades over
ARM_LOW = 10.5         # the height (standing) it has faded to nothing by, below the hands; full from the elbows up
ARM_ELBOW = 15.5       # the elbows' height standing
ARM_BONES = ('upper_arm', 'forearm', 'hand', 'thumb', 'f_', 'palm')
LEG_BONES = ('thigh', 'shin', 'foot', 'toe', 'heel')

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_cape.py: give an out .glb')
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
    return {b: w / total for b, w in ws.items()} if total > 0 else {}

def strongest(i):
    return max(weights_of(i).items(), key=lambda bw: bw[1], default=('', 0))[0]

arm_verts = [v.index for v in me.vertices if strongest(v.index).startswith(ARM_BONES)]
arm_set = set(arm_verts)
trunk_verts = [v.index for v in me.vertices if v.co.z <= COLLAR + BAND and v.index not in arm_set]

def posed_body(key=None):
    """Every body vertex's place in the standing pose, in the body's own space, with the body key named key at full;
    and each deforming bone's move from rest to that pose, in the body's own space."""
    blocks = me.shape_keys.key_blocks
    for kb in blocks[1:]:
        kb.value = 1.0 if kb.name == key else 0.0
    rig.animation_data_create()
    rig.animation_data.action = None
    walk.apply_pose(walk.stand_pose())
    bpy.context.view_layer.update()
    ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
    m = ev.to_mesh()
    pos = [v.co.copy() for v in m.vertices]
    ev.to_mesh_clear()
    to_rig = rig.matrix_world.inverted() @ body.matrix_world
    moves = {pb.name: to_rig.inverted() @ pb.matrix @ pb.bone.matrix_local.inverted() @ to_rig
             for pb in rig.pose.bones if pb.name in bones}
    for kb in blocks[1:]:
        kb.value = 0.0
    rest_pose()
    return pos, moves

def rest_pose():
    """Back to rest, so nothing after this sees a pose."""
    for pb in rig.pose.bones:
        pb.location = (0, 0, 0)
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.rotation_euler = (0, 0, 0)
        pb.scale = (1, 1, 1)
    bpy.context.view_layer.update()

rest = [v.co.copy() for v in me.vertices]
y_band = [rest[i].y for i in trunk_verts if 14.0 < rest[i].z < 19.0]
middle = Vector((0.0, (min(y_band) + max(y_band)) / 2, 0.0))

def ring_height(k):
    return COLLAR - (COLLAR - HEM) * (k / RINGS) ** RING_CURVE

def smoothstep(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3 - 2 * x)

def arm_sweep():
    """Where the arms go: (height, degrees round from the back's middle, either side) of every arm vertex standing and
    through the walk and the run, in the body's own space."""
    found = []
    poses = [walk.stand_pose()]
    for gait in SWEEP_GAITS:
        walk.set_gait(gait)
        poses += [walk.pose_at(n / SWEEP_STEPS)[0] for n in range(SWEEP_STEPS)]
    walk.set_gait(0.0)
    rig.animation_data_create()
    rig.animation_data.action = None
    for X in poses:
        walk.apply_pose(X)
        bpy.context.view_layer.update()
        ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
        m = ev.to_mesh()
        for i in arm_verts:
            c = m.vertices[i].co
            found.append((c.z, math.degrees(math.atan2(abs(c.x - middle.x), c.y - middle.y))))
        ev.to_mesh_clear()
    rest_pose()
    return found

def edge_limits():
    """The most each front edge may come round at each ring, degrees from the back's middle: ARM_GAP short of where
    any arm goes at about that height, so they swing past its edges rather than through it; easing open from below
    the lowest the hands go to the hem, and over the collar's own drop above the shoulders. Smoothed down the rings."""
    sweep = arm_sweep()
    lowest = min(z for z, _ in sweep)
    highest = max(z for z, _ in sweep)
    raw = []
    for k in range(RINGS + 1):
        z = ring_height(k)
        near_ = [a for zz, a in sweep if abs(zz - z) <= ARM_BAND]
        if near_:
            raw.append(min(near_) - ARM_GAP)
        else:
            raw.append(None)
    known = [a for a in raw if a is not None]
    for k in range(RINGS + 1):   # above and below the arms: from the nearest ring that has one, opening out
        if raw[k] is None:
            z = ring_height(k)
            if z > highest:
                start = next(a for a in raw if a is not None)
                raw[k] = start + 180 * smoothstep((z - highest - ARM_BAND) / CLASP_DROP)
            else:
                start = next(a for a in reversed(raw) if a is not None)
                top = lowest - ARM_BAND
                x = min(1.0, max(0.0, (top - z) / (top - HEM)))
                raw[k] = start + (max(LEFT_WRAP, RIGHT_WRAP) - start) * x ** WIDEN_CURVE
    for _ in range(EDGE_SMOOTH):   # never further out than it was, so smoothing can't bring an edge into an arm
        raw = [min(raw[k], (raw[max(0, k - 1)] + raw[k] + raw[min(RINGS, k + 1)]) / 3) for k in range(RINGS + 1)]
    print('CAPE arms from %.2f to %.2f; edges %s' % (lowest, highest, ' '.join(
        '%.1f:%.0f' % (ring_height(k), min(a, 180)) for k, a in enumerate(raw))))
    return raw

limits = edge_limits()

def ring_span(k):
    """The angles of the right and left front edges at ring k, degrees from the back's middle, + to the left: closing
    in from CLASP at the collar to their WRAP, and kept behind the arms."""
    z = ring_height(k)
    s = smoothstep((COLLAR - z) / CLASP_DROP)
    right = min(CLASP + (RIGHT_WRAP - CLASP) * s, limits[k])
    left = min(CLASP + (LEFT_WRAP - CLASP) * s, limits[k])
    return -right, left

def angle_at(k, j):
    lo, hi = ring_span(k)
    return lo + (hi - lo) * j / COLUMNS

def way_of(a):
    r = math.radians(a)
    return Vector((math.sin(r), math.cos(r), 0.0))   # 0 is +y, the back; +90 is +x, the figure's left

def edge_share(a):
    """How far round toward a front edge the angle a is: 0 at the back's middle, 1 at the edge."""
    lo, hi = -RIGHT_WRAP, LEFT_WRAP
    return min(1.0, a / hi) if a >= 0 else min(1.0, a / lo)

def build(pos):
    """The cape's vertices in the standing pose, ring by ring from the collar down, for the body's posed places."""
    reach_pts = [pos[i] for i in trunk_verts]
    pts = []
    for k in range(RINGS + 1):
        z0 = ring_height(k)
        t = max(0.0, (SHOULDER - z0) / (SHOULDER - HEM))
        band = [p for p in reach_pts if z0 - BAND <= p.z <= COLLAR + BAND]
        for j in range(COLUMNS + 1):
            a = angle_at(k, j)
            way = way_of(a)
            z = z0 + HEM_RISE * edge_share(a) ** 2 * (t * t)
            reach = max((p - middle).dot(way) for p in band)
            r = reach + OFFSET + (EASE * t + FLARE * t * t) * (1 - (1 - EDGE_FLARE) * edge_share(a))
            pts.append(Vector((middle.x + way.x * r, middle.y + way.y * r, z)))
    # The faces between two rings are flat and can cut inside a bulge between them (the shoulders' tops, the hips). A
    # body point the sheet passes inside of pushes out the corners of the face it pokes through, until none does. The
    # line it is looked along comes from the middle line at the collar for a point near the shoulders, so the tops of
    # the shoulders and arms, which a level line would pass over, push the slope out too; else it is level. Between
    # passes each column is kept from tucking in below the shoulders (a cape falls straight from the widest place
    # above), and the pushes are spread over their neighbours, so a single bump doesn't stand out.
    width = COLUMNS + 1
    quads = [(k * width + j, (k + 1) * width + j, (k + 1) * width + j + 1, k * width + j + 1)
             for k in range(RINGS) for j in range(COLUMNS)]
    def radius(q):
        return (pts[q] - middle).to_2d().length
    for _ in range(CLEAR_TRIES):
        tree = BVHTree.FromPolygons(pts, quads)
        push = [0.0] * len(pts)
        for p in reach_pts:
            if p.z > COLLAR:
                continue
            origin = Vector((middle.x, middle.y, COLLAR if p.z > SHOULDER - SLOPE_REACH else p.z))
            line = p - origin
            far = line.length
            if far < 1e-6:
                continue
            hit, _, face, gap = tree.ray_cast(origin, line / far, far + OFFSET)
            if hit is not None and far + OFFSET - gap > 1e-4:
                for q in quads[face]:
                    if q >= width:   # the collar stays on the neck
                        push[q] = max(push[q], (far + OFFSET - gap) * CLEAR_OVER)
        if not any(push):
            break
        spread = list(push)
        for k in range(1, RINGS + 1):
            for j in range(width):
                q = k * width + j
                around = [push[k2 * width + j2] for k2 in (k - 1, k, k + 1) for j2 in (j - 1, j, j + 1)
                          if 1 <= k2 <= RINGS and 0 <= j2 < width]
                spread[q] = max(push[q], sum(around) / len(around))
        for q, d in enumerate(spread):
            if d:
                way = Vector((pts[q].x - middle.x, pts[q].y - middle.y, 0)).normalized()
                pts[q] = pts[q] + way * d
        for k in range(1, RINGS + 1):
            if ring_height(k) >= SHOULDER:
                continue
            for j in range(width):
                q, above = k * width + j, (k - 1) * width + j
                if radius(q) < radius(above):
                    way = Vector((pts[q].x - middle.x, pts[q].y - middle.y, 0)).normalized()
                    pts[q] = Vector((middle.x + way.x * radius(above), middle.y + way.y * radius(above), pts[q].z))
    # Then dents are filled in: a corner further in than its neighbours' mean goes out to it. Only ever out, so nothing
    # pokes through again.
    for _ in range(FILL_PASSES):
        r = [radius(q) for q in range(len(pts))]
        for k in range(1, RINGS + 1):
            for j in range(width):
                q = k * width + j
                around = [r[k2 * width + j2] for k2, j2 in ((k - 1, j), (k + 1, j), (k, j - 1), (k, j + 1))
                          if 1 <= k2 <= RINGS and 0 <= j2 < width]
                mean = sum(around) / len(around)
                if mean > r[q]:
                    way = Vector((pts[q].x - middle.x, pts[q].y - middle.y, 0)).normalized()
                    pts[q] = Vector((middle.x + way.x * mean, middle.y + way.y * mean, pts[q].z))
    return pts, quads

posed, moves = posed_body()
verts, quads = build(posed)

# The weights. The trunk's: the nearest trunk vertex to the point straight in from the cape at its height (no lower
# than the hips), its weights on the arms and legs left out.
inner = [i for i in trunk_verts if not strongest(i).startswith(ARM_BONES + LEG_BONES)]
kd = KDTree(len(inner))
for n, i in enumerate(inner):
    kd.insert(posed[i], n)
kd.balance()

def side_legs():
    """Which thigh, shin and upper arm are on the +x side, by the body's own weights."""
    def plus_first(a, b):
        side = {a: 0.0, b: 0.0}
        for v in me.vertices:
            for x, w in weights_of(v.index).items():
                if x in side:
                    side[x] += w * v.co.x
        return sorted(side, key=side.get, reverse=True)
    return plus_first('thigh.L', 'thigh.R'), plus_first('shin.L', 'shin.R'), plus_first('upper_arm.L', 'upper_arm.R')
(plus_thigh, minus_thigh), (plus_shin, minus_shin), (plus_arm, minus_arm) = side_legs()

weights = []
for k in range(RINGS + 1):
    for j in range(COLUMNS + 1):
        p = verts[k * (COLUMNS + 1) + j]
        a = angle_at(k, j)
        way = way_of(a)
        z = max(p.z, HIPS)
        # Straight in toward the middle line until near the body: the nearest trunk vertex to a point a little inside
        # the reach, so a sheet far out from the hips still takes the hips' weights.
        q = Vector((middle.x, middle.y, z)) + way * 0.5
        best = None
        for co, n, d in kd.find_n(Vector((q.x, q.y, z)), 64):
            dz = abs(co.z - z)
            score = dz * 3 + (co - Vector((middle.x, middle.y, co.z))).normalized().dot(way) * -2
            if best is None or score < best[0]:
                best = (score, inner[n])
        top = {b: w for b, w in weights_of(best[1]).items() if not b.startswith(ARM_BONES + LEG_BONES)}
        total = sum(top.values()) or 1.0
        top = {b: w / total for b, w in top.items()} if top else {'spine': 1.0}
        down = max(0.0, (HIPS - p.z) / (HIPS - HEM))
        front = edge_share(a)
        share = (BACK_LEGS + (FRONT_LEGS - BACK_LEGS) * front) * down ** LEG_CURVE
        shin = (BACK_SHIN + (FRONT_SHIN - BACK_SHIN) * front) * min(1.0, max(0.0, (KNEE - p.z) / (KNEE - HEM)))
        left = min(1.0, max(0.0, 0.5 + way.x / (2 * SIDE_BLEND)))
        left = left * left * (3 - 2 * left)
        ws = {b: w * (1 - share) for b, w in top.items()}
        for b, w in ((plus_thigh, share * left * (1 - shin)), (minus_thigh, share * (1 - left) * (1 - shin)),
                     (plus_shin, share * left * shin), (minus_shin, share * (1 - left) * shin)):
            if w > 0:
                ws[b] = ws.get(b, 0) + w
        # The upper arm, straight out over it from the elbows up, fading out round from there and down to ARM_LOW.
        side, arm = (90.0, plus_arm) if a >= 0 else (-90.0, minus_arm)
        round_ = max(0.0, 1 - abs(a - side) / ARM_SPREAD)
        high = min(1.0, max(0.0, (p.z - ARM_LOW) / (ARM_ELBOW - ARM_LOW)))
        lift = min(1.0, max(0.0, (COLLAR - p.z) / (COLLAR - SHOULDER)))
        arm_share = ARM_SHARE * round_ * round_ * (3 - 2 * round_) * high * high * (3 - 2 * high) * lift
        if arm_share > 0:
            ws = {b: w * (1 - arm_share) for b, w in ws.items()}
            ws[arm] = ws.get(arm, 0) + arm_share
        weights.append(ws)

near = [set() for _ in verts]
for qd in quads:
    for x in range(4):
        near[qd[x]].update((qd[x - 1], qd[(x + 1) % 4]))
for _ in range(SMOOTH_PASSES):
    smoothed = []
    for i, ws in enumerate(weights):
        mix = {b: w * 2 for b, w in ws.items()}
        for n in near[i]:
            for b, w in weights[n].items():
                mix[b] = mix.get(b, 0) + w
        total = sum(mix.values())
        smoothed.append({b: w / total for b, w in mix.items()})
    weights = smoothed

# Back to the rest pose: each vertex through the inverse of its own blend of the bones' moves into the standing pose.
def unskin(pts, moves):
    out_ = []
    for p, ws in zip(pts, weights):
        kept = sorted(ws.items(), key=lambda bw: -bw[1])[:INFLUENCES]
        total = sum(w for _, w in kept)
        m = sum((moves[b] * (w / total) for b, w in kept), Matrix(((0,) * 4,) * 4))
        out_.append(m.inverted() @ p)
    return out_

verts = unskin(verts, moves)

old = bpy.data.objects.get(NAME)
if old is not None:
    bpy.data.objects.remove(old)
mesh = bpy.data.meshes.new(NAME)
mesh.from_pydata([tuple(v) for v in verts], [], [list(q) for q in quads])
mesh.update()
for p in mesh.polygons:   # facing out
    p.use_smooth = True
    c = p.center
    if p.normal.dot(Vector((c.x - middle.x, c.y - middle.y, 0))) < 0:
        p.flip()
mesh.update()
g = bpy.data.objects.new(NAME, mesh)
body.users_collection[0].objects.link(g)
g.parent = rig
g.matrix_world = body.matrix_world.copy()
g.modifiers.new('Armature', 'ARMATURE').object = rig

for vi, ws in enumerate(weights):
    kept = sorted(ws.items(), key=lambda bw: -bw[1])[:INFLUENCES]
    total = sum(w for _, w in kept)
    for b, w in kept:
        if w / total > 1e-4:
            (g.vertex_groups.get(b) or g.vertex_groups.new(name=b)).add([vi], w / total, 'REPLACE')


# The body's keys, built from the keyed body.
g.shape_key_add(name='Basis', from_mix=False)
for kb in me.shape_keys.key_blocks[1:]:
    kpos, kmoves = posed_body(kb.name)
    moved = unskin(build(kpos)[0], kmoves)
    key = g.shape_key_add(name=kb.name, from_mix=False)
    key.value = 0.0
    for n, p in enumerate(moved):
        key.data[n].co = p

print('CAPE +x thigh %s, %d vertices, %d faces, hem %.2f..%.2f, widest %.2f, keys %s'
      % (plus_thigh, len(verts), len(quads), min(v.z for v in verts), max(v.z for v in verts[-(COLUMNS + 1):]),
         max((v - middle).to_2d().length for v in verts), ', '.join(k.name for k in me.shape_keys.key_blocks[1:])))

rig.hide_set(False)   # a hidden rig can't be selected, and without it the file has no skin
rig.hide_viewport = False
rig.hide_select = False
bpy.ops.object.select_all(action='DESELECT')
g.select_set(True)
rig.select_set(True)
if not rig.select_get():
    sys.exit('make_cape.py: the rig can\'t be selected, so the .glb would have no weights')
bpy.context.view_layer.objects.active = g
os.makedirs(os.path.dirname(out), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_extras=True,
                          export_skins=True, export_morph=True, export_morph_normal=False,
                          export_animations=False, export_apply=False, export_materials='NONE')
before = set(bpy.data.objects)   # read it back: the weights are what this file is for
bpy.ops.import_scene.gltf(filepath=out, disable_bone_shape=True)
back = [o for o in bpy.data.objects if o not in before]
weighted = sum(1 for o in back if o.type == 'MESH' for v in o.data.vertices if v.groups)
for o in back:
    bpy.data.objects.remove(o)
if weighted != len(verts):
    sys.exit('make_cape.py: %d of %d vertices have weights in the .glb' % (weighted, len(verts)))
print('GLB %s' % out)
if blend:
    bpy.ops.wm.save_as_mainfile(filepath=blend, copy=True)
