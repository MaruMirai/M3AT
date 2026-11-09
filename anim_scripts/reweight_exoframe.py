"""Puts the weights back on Combat Extended's exoframe (make_exoframe.py's CE_ExoFrame) after the .glb has been edited
and saved without its rig, keeping the edits.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/reweight_exoframe.py -- <source.glb> <edited.glb> <out.glb>

source.glb is make_exoframe.py's output with its weights (run it again with the same switches, e.g. --low, to a scratch
file if the one in Models/garments has been overwritten by the edit). The edited mesh is split into pieces joined by
edges (copies split at a texture seam counted as one). A piece along the spine (taller than BEND_SPAN, narrower than
BEND_WIDTH and about the middle: the spine bar, not a leg strut) bends with it: each vertex takes the body's weights at the trunk's skin nearest it, less the
arms', shoulders', neck's and head's (make_exoframe.py's plate_weights). Every other piece is rigid: wholly on the one
bone that the source vertices nearest its vertices are weighted most to (a strut on its thigh or shin, a joint disc on
its shin or the hips, a vertebra or the yoke on its spine bone, the hip belt on the hips). A piece whose nearest source
vertices have no weights of their own (the belt and cuffs, which take the body's at export) is left with none. The
result is written skinned to the rig, as make_exoframe.py writes it, and each piece's bone printed (REWEIGHT lines)."""
import bpy, bmesh, sys, os, collections
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if len(argv) != 3:
    sys.exit('reweight_exoframe.py: give source.glb edited.glb out.glb')
source_glb, edited_glb, out_glb = (os.path.abspath(a) for a in argv)

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

NAME = 'CE_ExoFrame'
BEND_SPAN = 2.0        # Blender units: a piece this tall ...
BEND_WIDTH = 1.5       # ... and no wider bends with the spine
NEAREST = 3            # source vertices looked at round each edited vertex
WELD = 1e-4
PLATE_NOT = ('upper_arm', 'forearm', 'hand', 'thumb', 'f_', 'palm', 'shoulder', 'spine.004', 'spine.005', walk.HEAD)

body = walk.join_body()
rig = body.parent
deform = {b.name for b in rig.data.bones if b.use_deform}


def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    new = [o for o in bpy.data.objects if o not in before]
    mesh = next(o for o in new if o.type == 'MESH')
    for o in new:
        if o is not mesh:
            bpy.data.objects.remove(o)
    return mesh


source = load(source_glb)
edited = load(edited_glb)
bpy.context.view_layer.update()

# The source's own weights, by vertex (none for a vertex the make script left to the body's).
source_names = {g.index: g.name for g in source.vertex_groups}
source_weights = []
for v in source.data.vertices:
    ws = {source_names[g.group]: g.weight for g in v.groups if g.weight > 0 and source_names[g.group] in deform}
    source_weights.append(ws)
kd = KDTree(len(source.data.vertices))
for v in source.data.vertices:
    kd.insert(source.matrix_world @ v.co, v.index)
kd.balance()

# The trunk's skin, for the bending piece.
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0
bw = body.matrix_world
me = body.data
verts = [bw @ v.co for v in me.vertices]
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]
names = {g.index: g.name for g in body.vertex_groups}


def dominant(i):
    gs = [(g.weight, names[g.group]) for g in me.vertices[i].groups if names[g.group] in deform]
    return max(gs)[1] if gs else ''


dom = [dominant(i) for i in range(len(verts))]
OFF_TRUNK = ('upper_arm', 'forearm', 'hand', 'thumb', 'f_', 'palm')
trunk_tris = [t for t in tris if not any(dom[i].startswith(walk.HEAD) for i in t)
              and sum(dom[i].startswith(OFF_TRUNK) for i in t) < 2]
trunk = BVHTree.FromPolygons(verts, trunk_tris)


def plate_weights(p):
    """make_exoframe.py's: the body's weights at the trunk's skin nearest p, less PLATE_NOT's bones, adding up to 1."""
    loc, n, ti, dist = trunk.find_nearest(p)
    t = trunk_tris[ti]
    a, b, c = (verts[i] for i in t)
    v0, v1, v2 = b - a, c - a, loc - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-9
    wb = (d11 * d20 - d01 * d21) / den
    wc = (d00 * d21 - d01 * d20) / den
    total = {}
    for share, i in zip((1 - wb - wc, wb, wc), t):
        for g in me.vertices[i].groups:
            name = names[g.group]
            if name in deform and g.weight > 0 and not name.startswith(PLATE_NOT):
                total[name] = total.get(name, 0.0) + max(0.0, share) * g.weight
    whole = sum(total.values())
    if whole <= 0:
        return {'spine.003': 1.0}
    return {b: w / whole for b, w in total.items() if w / whole > 1e-3}


# The edited mesh's pieces, by welded place: each mesh vertex to its piece.
places = [edited.matrix_world @ v.co for v in edited.data.vertices]
bm = bmesh.new()
bm.from_mesh(edited.data)
bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=WELD)
# remove_doubles keeps one vertex of each group; map every original index to a kept one by place.
keep = KDTree(len(bm.verts))
bm.verts.index_update()
for v in bm.verts:
    keep.insert(v.co, v.index)
keep.balance()
piece_of = {}
pieces = []
for v in bm.verts:
    if v.index in piece_of:
        continue
    stack, members = [v], []
    piece_of[v.index] = len(pieces)
    while stack:
        x = stack.pop()
        members.append(x.index)
        for e in x.link_edges:
            y = e.other_vert(x)
            if y.index not in piece_of:
                piece_of[y.index] = len(pieces)
                stack.append(y)
    pieces.append(members)
mesh_piece = [piece_of[keep.find(edited.data.vertices[i].co)[1]] for i in range(len(places))]
bm.free()

weights = {}
for k in range(len(pieces)):
    members = [i for i in range(len(places)) if mesh_piece[i] == k]
    ps = [places[i] for i in members]
    lo = Vector([min(p[a] for p in ps) for a in range(3)])
    hi = Vector([max(p[a] for p in ps) for a in range(3)])
    tally = collections.Counter()
    for p in ps:
        for _, j, _ in kd.find_n(p, NEAREST):
            for b, w in source_weights[j].items():
                tally[b] += w
    if hi.z - lo.z > BEND_SPAN and hi.x - lo.x < BEND_WIDTH and abs(hi.x + lo.x) / 2 < BEND_WIDTH:
        for i in members:
            weights[i] = plate_weights(places[i])
        what = 'bends with the spine'
    elif not tally:
        what = "none (the body's at export)"
    else:
        bone = tally.most_common(1)[0][0]
        for i in members:
            weights[i] = {bone: 1.0}
        what = 'whole on ' + bone
    print('REWEIGHT: piece of %d vertices, (%.1f, %.1f, %.1f)..(%.1f, %.1f, %.1f): %s'
          % (len(members), lo.x, lo.y, lo.z, hi.x, hi.y, hi.z, what))

bpy.data.objects.remove(source)
edited.name = NAME
edited.data.name = NAME
mw = edited.matrix_world.copy()
edited.parent = rig
edited.matrix_world = mw
edited.modifiers.new('Armature', 'ARMATURE').object = rig
for g in list(edited.vertex_groups):
    edited.vertex_groups.remove(g)
for i, ws in weights.items():
    for b, w in ws.items():
        (edited.vertex_groups.get(b) or edited.vertex_groups.new(name=b)).add([i], w, 'REPLACE')
print('REWEIGHT: %d of %d vertices weighted' % (len(weights), len(places)))

bpy.ops.object.select_all(action='DESELECT')
edited.select_set(True)
rig.hide_set(False)
rig.hide_viewport = False
rig.select_set(True)
bpy.context.view_layer.objects.active = edited
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False, export_extras=True)
print('REWEIGHT -> %s' % out_glb)
