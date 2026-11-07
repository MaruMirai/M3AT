"""Makes the TribalA garment (tribalwear) from the body's own faces: a one-shoulder tunic down to the hips that is the
body's `-TribalA` vertex group copied and pushed out along the normals, with the body's own weights (so it bends exactly
as the skin under it does), and below it a short skirt with a jagged hem, hung from the tunic's bottom edge.

The skirt has no body faces under it (it spans the gap between the legs), so it is built round the body instead: each
vertex of the tunic's bottom edge drops a line of vertices straight down to the hem, set out from the hips' middle as far
as the body reaches that way at or above that height (the support of the hips and thighs), so it hangs clear of both
legs and flares. Its weights go from the edge's (the body's) at the top toward the thigh on its side at the hem, the front
more than the back (a knee comes forward through the front; a seated pawn's back panel must stay hanging, not swing up
through the seat). Each body key's shape is built the same way from the keyed body, so it keeps clear of a Fat or Hulk
body's legs; those go in as the garment's own keys.

Written to <out.glb> as export_frames.py's import_garments takes it (Models/garments/TribalA.glb is the garment TribalA):
the mesh at rest with its keys, its weights and own_weights. out.blend also saves the .blend with it on the rig; it never
saves over the .blend it opened.

blender -b Models/pawnHumanRig.blend --python anim_scripts/make_tribal.py -- <out.glb> [out.blend]
"""
import bpy, os, sys
from mathutils import Vector
from mathutils.bvhtree import BVHTree

GROUP = '-TribalA'
NAME = 'TribalA'
CUT = 11.0             # Blender units: the group's faces with a corner below this are dropped (the thighs)
CROTCH_X = 1.0         # and those with a corner within this of the middle (either way) below CROTCH_CUT: the groin and the
CROTCH_CUT = 12.3      # cleft, so the skirt hangs from the bottom of the belly and the seat, not from deep between the legs
OFFSET = 0.1           # Blender units the tunic stands off the skin (about 0.008 leg lengths, as make_shirt.py's)
HEM = 9.3              # the hem's height between its points
POINT = 0.8            # how much lower each other hem vertex hangs: the jagged edge
RINGS = 3              # rings of vertices from the tunic's edge down to the hem
EASE = 0.35            # extra room the skirt is given at the hem over the body's reach, growing from none at the top
FRONT_THIGH = 0.8      # the thighs' share of the weights at the hem, at the front
BACK_THIGH = 0.3       # and at the back
THIGH_CURVE = 0.6      # how soon down the skirt the thighs take their share (1 evenly; below 1 sooner, so a raised thigh lifts the
                       # skirt over it from near the top rather than poking through at the hip crease)
SIDE_BLEND = 0.5       # across the middle (as a share of the way round to the side) the two thighs are blended
CLEAR_TRIES = 20       # passes pushing the skirt out where the body pokes through it at rest
CLEAR_OVER = 1.5       # each push is this much of the poke, as a vertex moves a face's middle only part of its own move
LIMB_BONES =('upper_arm', 'forearm', 'hand', 'thumb', 'f_')   # the body's arms are left out of the skirt's reach

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_tribal.py: give an out .glb')
out = os.path.abspath(argv[0])
blend = argv[1] if len(argv) > 1 else None

for o in bpy.data.objects:   # a mesh saved in Edit Mode has its edits outside its data, and skips its Armature modifier
    if o.type == 'MESH' and o.mode != 'OBJECT':
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode='OBJECT')
body = bpy.data.objects['Body']
rig = body.find_armature()
me = body.data
group = body.vertex_groups[GROUP].index
inside = {v.index for v in me.vertices if any(g.group == group for g in v.groups)}
def low(i):
    c = me.vertices[i].co
    return c.z < CUT or (abs(c.x) < CROTCH_X and c.z < CROTCH_CUT)
faces = [list(p.vertices) for p in me.polygons
         if all(i in inside for i in p.vertices) and not any(low(i) for i in p.vertices)]
names = {g.index: g.name for g in body.vertex_groups}
bones = {b.name for b in rig.data.bones if b.use_deform}

def weights_of(i):
    ws = {names[g.group]: g.weight for g in me.vertices[i].groups if names[g.group] in bones and g.weight > 0}
    total = sum(ws.values())
    return {b: w / total for b, w in ws.items()}

# The tunic's bottom edge, walked in order round the body.
count = {}
for f in faces:
    for k in range(len(f)):
        e = tuple(sorted((f[k], f[(k + 1) % len(f)])))
        count[e] = count.get(e, 0) + 1
next_of = {}
for f in faces:   # boundary edges in the faces' own winding, so the walk goes one way
    for k in range(len(f)):
        a, b = f[k], f[(k + 1) % len(f)]
        if count[tuple(sorted((a, b)))] == 1:
            next_of[a] = b
loops, seen = [], set()
for s in next_of:
    if s in seen:
        continue
    loop, x = [], s
    while x not in seen:
        seen.add(x)
        loop.append(x)
        x = next_of[x]
    loops.append(loop)
edge = min(loops, key=lambda l: sum(me.vertices[i].co.z for i in l) / len(l))

reach_verts = [v.index for v in me.vertices
               if v.co.z < max(me.vertices[i].co.z for i in edge) + 0.5
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

def build(pos):
    """The garment's vertices for the body's vertices at pos: the tunic's, then the skirt's ring by ring."""
    n = normals(pos)
    tunic = {i: pos[i] + n[i] * OFFSET for f in faces for i in f}
    middle = sum((tunic[i] for i in edge), Vector()) / len(edge)
    middle.z = 0
    rings = []
    for k in range(1, RINGS + 1):
        t = k / RINGS
        ring = []
        for j, i in enumerate(edge):
            top = tunic[i]
            way = Vector((top.x - middle.x, top.y - middle.y, 0)).normalized()
            hem = HEM - (POINT if j % 2 == 0 else 0)
            z = top.z + (hem - top.z) * t
            reach = max((pos[v] - middle).dot(way) for v in reach_verts if pos[v].z >= z - 0.3)
            r = max(reach + OFFSET + EASE * t, (top - middle).to_2d().dot(way.to_2d()))
            ring.append(Vector((middle.x + way.x * r, middle.y + way.y * r, z)))
        rings.append(ring)
    # Each ring is set out by the body's reach at its own height, but the faces between two rings (or between the edge,
    # which zigzags up and down, and the first ring) are flat and can cut inside a bulge of the hip between them. A body
    # vertex the skirt passes inside of pushes the rings of the face it pokes through out, until none does.
    n_edge = len(edge)
    pts = [tunic[i] for i in edge] + [p for ring in rings for p in ring]
    quads = [(k * n_edge + j, (k + 1) * n_edge + j, (k + 1) * n_edge + (j + 1) % n_edge, k * n_edge + (j + 1) % n_edge)
             for k in range(RINGS) for j in range(n_edge)]
    top_z = max(tunic[i].z for i in edge)
    for _ in range(CLEAR_TRIES):
        tree = BVHTree.FromPolygons(pts, quads)
        push = [0.0] * len(pts)
        for v in reach_verts:
            p = pos[v]
            if p.z > top_z:
                continue
            out = Vector((p.x - middle.x, p.y - middle.y, 0))
            far = out.length
            if far < 1e-6:
                continue
            hit, _, face, gap = tree.ray_cast(Vector((middle.x, middle.y, p.z)), out / far, far + OFFSET)
            if hit is not None and far + OFFSET - gap > 1e-4:
                for q in quads[face]:
                    if q >= n_edge:
                        push[q] = max(push[q], (far + OFFSET - gap) * CLEAR_OVER)
        if not any(push):
            break
        for q, d in enumerate(push):
            if d:
                way = Vector((pts[q].x - middle.x, pts[q].y - middle.y, 0)).normalized()
                pts[q] = pts[q] + way * d
    rings = [pts[(k + 1) * n_edge:(k + 2) * n_edge] for k in range(RINGS)]
    return tunic, rings

base = [v.co.copy() for v in me.vertices]
tunic, rings = build(base)
order = sorted(tunic)
index = {i: k for k, i in enumerate(order)}
verts = [tunic[i] for i in order] + [p for ring in rings for p in ring]
ring_start = len(order)
n_edge = len(edge)

def ring_index(k, j):   # ring 0 is the tunic's edge
    j %= n_edge
    return index[edge[j]] if k == 0 else ring_start + (k - 1) * n_edge + j

polys = [[index[i] for i in f] for f in faces]
for k in range(RINGS):
    for j in range(n_edge):
        polys.append([ring_index(k, j), ring_index(k + 1, j), ring_index(k + 1, j + 1), ring_index(k, j + 1)])

old = bpy.data.objects.get(NAME)
if old is not None:
    bpy.data.objects.remove(old)
mesh = bpy.data.meshes.new(NAME)
mesh.from_pydata([tuple(v) for v in verts], [], polys)
mesh.update()
middle = sum((v for v in verts), Vector()) / len(verts)
for p in mesh.polygons:   # the skirt's quads face out
    p.use_smooth = True
    if p.index >= len(faces):
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

# Weights: the tunic's are the body's own; down the skirt they go from the edge's toward the thigh on each side.
def put(vi, ws):
    for b, w in ws.items():
        if w > 1e-4:
            (g.vertex_groups.get(b) or g.vertex_groups.new(name=b)).add([vi], w, 'REPLACE')
for i in order:
    put(index[i], weights_of(i))
middle = sum((tunic[i] for i in edge), Vector()) / len(edge)
# Which thigh is on the +x side, as the body's own weights have it (not by the bones' names: the skirt must go as the
# legs under it do, whatever the body's weights say).
plus = {'thigh.L': 0.0, 'thigh.R': 0.0}
for v in me.vertices:
    for b, w in weights_of(v.index).items():
        if b in plus:
            plus[b] += w * v.co.x
plus_thigh, minus_thigh = sorted(plus, key=plus.get, reverse=True)
for j, i in enumerate(edge):
    top = weights_of(i)
    way = Vector((tunic[i].x - middle.x, tunic[i].y - middle.y, 0)).normalized()
    left = min(1.0, max(0.0, 0.5 + way.x / (2 * SIDE_BLEND)))   # the share of the thigh on the +x side
    left = left * left * (3 - 2 * left)
    thigh_share = BACK_THIGH + (FRONT_THIGH - BACK_THIGH) * (0.5 - 0.5 * way.y)   # -y is the front
    for k in range(1, RINGS + 1):
        share = thigh_share * (k / RINGS) ** THIGH_CURVE
        ws = {b: w * (1 - share) for b, w in top.items()}
        ws[plus_thigh] = ws.get(plus_thigh, 0) + share * left
        ws[minus_thigh] = ws.get(minus_thigh, 0) + share * (1 - left)
        put(ring_index(k, j), ws)

# The body's keys, built from the keyed body.
g.shape_key_add(name='Basis', from_mix=False)
for kb in me.shape_keys.key_blocks[1:]:
    keyed = [Vector(d.co) for d in kb.data]
    t, r = build(keyed)
    moved = [t[i] for i in order] + [p for ring in r for p in ring]
    key = g.shape_key_add(name=kb.name, from_mix=False)
    key.value = 0.0
    for k, p in enumerate(moved):
        key.data[k].co = p
print('TRIBAL +x thigh %s' % plus_thigh)
print('TRIBAL %d vertices, %d faces (%d from the body, %d skirt), edge of %d, keys %s'
      % (len(verts), len(polys), len(faces), len(polys) - len(faces), n_edge,
         ', '.join(k.name for k in me.shape_keys.key_blocks[1:])))

rig.hide_set(False)   # a hidden rig can't be selected, and without it the file has no skin: every vertex would ride the hips
rig.hide_viewport = False
rig.hide_select = False
bpy.ops.object.select_all(action='DESELECT')
g.select_set(True)
rig.select_set(True)
if not rig.select_get():
    sys.exit('make_tribal.py: the rig can\'t be selected, so the .glb would have no weights')
bpy.context.view_layer.objects.active = g
os.makedirs(os.path.dirname(out), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_extras=True,
                          export_skins=True, export_all_influences=True, export_morph=True, export_morph_normal=False,
                          export_animations=False, export_apply=False, export_materials='NONE')
before = set(bpy.data.objects)   # read it back: the weights are what this file is for
bpy.ops.import_scene.gltf(filepath=out, disable_bone_shape=True)
back = [o for o in bpy.data.objects if o not in before]
weighted = sum(1 for o in back if o.type == 'MESH' for v in o.data.vertices if v.groups)
for o in back:
    bpy.data.objects.remove(o)
if weighted != len(verts):
    sys.exit('make_tribal.py: %d of %d vertices have weights in the .glb' % (weighted, len(verts)))
print('GLB %s' % out)
if blend:
    bpy.ops.wm.save_as_mainfile(filepath=blend, copy=True)
