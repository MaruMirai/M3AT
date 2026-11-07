"""Copy cuts made on one side of the body across to the other, so the mesh stays mirror-symmetric.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/mirror_cut.py -- <out.blend> [mesh]

(or the Text Editor's Run Script in the open session, on the `Body`, unsaved). <out.blend> may be the .blend it
opened. The mesh (`Body` by default) must be exactly symmetric in x but for the new vertices, each of which lies on an
edge between two vertices that have their mirror (a loop cut or a knife cut through existing edges, not a vertex in
the middle of a face or a slid one). Each of those edges is split on the other side at the same share of the way
along, the same vertices are joined across the faces, and each new vertex takes its mirror's place, shape key moves
(flipped in x) and bone weights (.L for .R). UVs, seams and the other corner data are interpolated in the faces the
cut splits, as Blender does for the cut itself, so each new face keeps its own UV island.

The dashed garment groups (`-BasicShirt`, ...) are read by make_from_groups.py as a selection (any weight above 0 is
in), but a cut interpolates them like weights, so a new vertex between one in a group and one out of it comes out
half in, and the garment would grow across the cut. Here a new vertex is in a dashed group only if both ends of its
edge are (on both sides), so the garments' faces are as they were.
"""
import sys

import bpy
import bmesh
from mathutils import kdtree

ON_EDGE = 1e-3     # how far off the line between its edge's ends a new vertex may be, in the mesh's units
MATCH = 1e-4       # how close a vertex's mirror must be to a vertex to count as its pair

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
out = argv[0] if argv else None
name = argv[1] if len(argv) > 1 else 'Body'
obj = bpy.data.objects[name]
me = obj.data
if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
    bpy.ops.object.mode_set(mode='OBJECT')

def flipped(group):
    """The mirror bone's name: .L for .R, keeping any .001 after it."""
    for a, b in (('.L', '.R'), ('.R', '.L')):
        if group.endswith(a):
            return group[:-2] + b
        if a + '.' in group:
            return group.replace(a + '.', b + '.')
    return group

bm = bmesh.new()
bm.from_mesh(me)
bm.verts.ensure_lookup_table()
deform = bm.verts.layers.deform.verify()
shapes = list(bm.verts.layers.shape.values())
names = [g.name for g in obj.vertex_groups]
index_of = {n: i for i, n in enumerate(names)}

tree = kdtree.KDTree(len(bm.verts))
for v in bm.verts:
    tree.insert(v.co, v.index)
tree.balance()
pair = {}
for v in bm.verts:
    m = v.co.copy()
    m.x = -m.x
    _, i, d = tree.find(m)
    if d < MATCH:
        pair[v] = bm.verts[i]
new = [v for v in bm.verts if v not in pair]
if not new:
    sys.exit('mirror_cut.py: %s is symmetric already' % name)
side = 1 if new[0].co.x > 0 else -1
if any(v.co.x * side <= 0 for v in new):
    sys.exit('mirror_cut.py: new vertices on both sides of %s; cut one side only' % name)

# Each new vertex's edge: the two neighbours with a mirror it lies between.
split = {}
for n in new:
    best = None
    ends = [e.other_vert(n) for e in n.link_edges if e.other_vert(n) in pair]
    for i, a in enumerate(ends):
        for b in ends[i + 1:]:
            d = b.co - a.co
            t = (n.co - a.co).dot(d) / d.length_squared
            off = (a.co + d * t - n.co).length
            if 0 < t < 1 and (best is None or off < best[0]):
                best = (off, a, b, t)
    if best is None or best[0] > ON_EDGE:
        sys.exit('mirror_cut.py: vertex %d is not on an edge between two old vertices' % n.index)
    split[n] = best[1:]
print('CUT %d new vertices on the %s side of %s' % (len(new), '+x' if side > 0 else '-x', name))

# The dashed groups: in only where both ends are, as a selection (see the docstring).
def weight(v, g):
    return v[deform].get(g, 0.0)
for n, (a, b, t) in split.items():
    for g in [g for g in n[deform].keys() if names[g].startswith('-')]:
        if weight(a, g) <= 0 or weight(b, g) <= 0:
            del n[deform][g]
            print('GROUP %s: vertex %d left out (half in, from the cut)' % (names[g], n.index))

# Split the mirrored edges at the same share.
made = {}
for n, (a, b, t) in split.items():
    ma, mb = pair[a], pair[b]
    edge = next((e for e in ma.link_edges if e.other_vert(ma) is mb), None)
    if edge is None:
        sys.exit('mirror_cut.py: no edge %d-%d to mirror vertex %d onto' % (ma.index, mb.index, n.index))
    _, v = bmesh.utils.edge_split(edge, ma, t)
    want = n.co.copy()
    want.x = -want.x
    if (v.co - want).length > ON_EDGE:   # the share was taken from the other end
        sys.exit('mirror_cut.py: split of %d-%d landed %.4f off' % (ma.index, mb.index, (v.co - want).length))
    made[n] = v
mirror = dict(pair)
mirror.update(made)

# Join what the cut joined across faces on this side.
def joined(x, y):
    return any(e.other_vert(x) is y for e in x.link_edges)
links = {frozenset(e.verts) for n in new for e in n.link_edges}
links |= {frozenset(e.verts) for n in new for f in n.link_faces for e in f.edges}
for link in links:
    x, y = (mirror[v] for v in link)
    if joined(x, y):
        continue
    faces = [f for f in x.link_faces if y in f.verts]
    if not faces:
        sys.exit('mirror_cut.py: no face holds both %d and %d' % (x.index, y.index))
    bmesh.ops.connect_verts(bm, verts=[x, y])

# The faces round the cut must now be each other's mirror.
here = {frozenset(f.verts) for n in new for f in n.link_faces}
there = {frozenset(f.verts) for n in made.values() for f in n.link_faces}
if {frozenset(mirror[v] for v in f) for f in here} != there:
    sys.exit('mirror_cut.py: the faces round the cut came out different from their mirror')

# Places, keys and weights from the mirror vertex, seams from the mirror edge.
for n, v in made.items():
    v.co = n.co.copy()
    v.co.x = -v.co.x
    for layer in shapes:
        v[layer] = n[layer].copy()
        v[layer].x = -v[layer].x
    v[deform].clear()
    for g, w in n[deform].items():
        to = index_of.get(flipped(names[g]))
        if to is None:
            sys.exit('mirror_cut.py: no vertex group %s to mirror %s onto' % (flipped(names[g]), names[g]))
        v[deform][to] = w
for n in new:
    for e in n.link_edges:
        x, y = (mirror[w] for w in e.verts)
        m = next(f for f in x.link_edges if f.other_vert(x) is y)
        m.seam = e.seam
        m.smooth = e.smooth

bm.normal_update()
bm.to_mesh(me)
bm.free()
me.update()
print('CUT %s now %d vertices, %d faces' % (name, len(me.vertices), len(me.polygons)))
if out:
    bpy.ops.wm.save_as_mainfile(filepath=out)
    print('SAVED %s' % out)
