"""Rebuild one side of the body as the mirror of the other, wherever the two differ.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/mirror_side.py -- -x|+x <out.blend> [mesh] [--check]

(or the Text Editor's Run Script, which copies -x onto +x in the open session, unsaved). `-x` copies the -x side (the
figure's right, .R) onto +x. Unlike mirror_cut.py, which only copies cuts through existing edges, this takes any
edit made on one side: moved, merged, dissolved or added vertices, re-cut faces.

Each vertex on the other side whose place is the mirror of a source vertex's (within `MATCH`) is kept as it is, with
its own weights, keys and UVs, so what was symmetric stays untouched. Every other vertex there is deleted, and so is
every face there whose mirror is not a source face. Then each source face with no mirror is copied across: new
vertices at the mirrored place, shape keys mirrored (x flipped), bone weights .L for .R, the face's winding reversed,
its seams copied. UVs: the atlas lays the right's islands as the left's mirrors, each at its own offset (u' = S - u,
v' = v - D, per island), so each source UV island's S and D are read from its faces that already have a mirror, and the
copied faces take them.

The dashed garment groups (`-BasicShirt`, ...) are read by make_from_groups.py as a selection (any weight above 0 is
in), but a knife or loop cut interpolates them like weights, so a new vertex between one in a group and one out comes
out partly in, and the garment grows across the cut. So a source vertex partly in a dashed group is taken out of it
(as mirror_cut.py does). A dashed group the two sides don't share (`-Sash`, `-TribalA`'s strap) is given to a copied
vertex only if its source vertex is in it and every kept neighbour on its own side is too; a shared one is copied.

`--check` reports what it would do without saving.
"""
import sys

import bpy
import bmesh
from mathutils import Vector, kdtree

MATCH = 1e-4      # how close a vertex's mirror must be to a vertex to count as its pair, in the mesh's units
MID = 1e-4        # how near x = 0 a vertex is on the midline
UV_SAME = 0.02    # UVs this close at a shared vertex join two faces into one island (a hand merge can leave a
                  # vertex's corners a little apart inside an island, as at the armpit on 2026-11-27)
UV_SPREAD = 1e-3  # how near its island's commonest S and D a paired corner must be to count as agreeing
AGREE = 0.8       # the share of an island's paired corners that must agree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else ['-x']
check = '--check' in argv
argv = [a for a in argv if not a.startswith('--')]
src_side = -1 if argv[0] == '-x' else 1
out = argv[1] if len(argv) > 1 else None
name = argv[2] if len(argv) > 2 else 'Body'
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


def mirrored(co):
    return Vector((-co.x, co.y, co.z))


bm = bmesh.new()
bm.from_mesh(me)
bm.verts.ensure_lookup_table()
bm.faces.ensure_lookup_table()
deform = bm.verts.layers.deform.verify()
shapes = list(bm.verts.layers.shape.values())
uv = bm.loops.layers.uv.active
colours = list(bm.loops.layers.color.values()) + list(bm.loops.layers.float_color.values())
names = [g.name for g in obj.vertex_groups]
index_of = {n: i for i, n in enumerate(names)}
dashed = {i for i, n in enumerate(names) if n.startswith('-')}


def weld_uvs():
    """Joins a vertex's corners that lie within UV_SAME of each other across an unseamed edge, at their mean: a hand
    merge or dissolve leaves them a few thousandths apart (tears in the atlas, 28 after the 2026-11-27 edits)."""
    joined = 0
    for v in bm.verts:
        loops = list(v.link_loops)
        root = {l: l for l in loops}

        def find(l):
            while root[l] is not l:
                l = root[l]
            return l
        for e in v.link_edges:
            if e.seam or len(e.link_faces) != 2:
                continue
            a, b = (next(l for l in f.loops if l.vert is v) for f in e.link_faces)
            gap = (a[uv].uv - b[uv].uv).length
            if gap < UV_SAME:
                root[find(a)] = find(b)
                joined += gap > 1e-6
        groups = {}
        for l in loops:
            groups.setdefault(find(l), []).append(l)
        for g in groups.values():
            mean = sum((l[uv].uv for l in g), Vector((0, 0))) / len(g)
            for l in g:
                l[uv].uv = mean
    return joined


def side(v):
    if abs(v.co.x) <= MID:
        return 0
    return src_side if v.co.x * src_side > 0 else -src_side


open_before = len([e for e in bm.edges if len(e.link_faces) == 1])

tree = kdtree.KDTree(len(bm.verts))
for v in bm.verts:
    tree.insert(v.co, v.index)
tree.balance()

twin = {}         # source or midline vertex -> its kept mirror (itself on the midline)
for v in bm.verts:
    if side(v) == -src_side:
        continue
    if side(v) == 0:
        twin[v] = v
        continue
    _, i, d = tree.find(mirrored(v.co))
    if d < MATCH and side(bm.verts[i]) == -src_side:
        twin[v] = bm.verts[i]
source_of = {t: s for s, t in twin.items()}

# Partial dashed weights on the source side (a cut's interpolation) are taken out.
partial = []
for v in bm.verts:
    if side(v) == -src_side or v in twin:   # older partial weights (earlier cuts) are left as they are
        continue
    for g in [g for g, w in v[deform].items() if g in dashed and w < 0.999]:
        partial.append((v.index, names[g], round(v[deform][g], 2)))
        del v[deform][g]
print('mirror_side.py: %d partial dashed weights taken out on the new source vertices' % len(partial))
for p in partial:
    print('   vertex %d %s %.2f' % p)


print('mirror_side.py: %d UV tears welded' % weld_uvs())

# Which dashed groups both sides share (judged on the kept pairs).
shared = set()
for g in dashed:
    ins = [v for v in twin if side(v) == src_side and g in v[deform]]
    if ins and all(g in twin[v][deform] for v in ins):
        shared.add(g)
print('mirror_side.py: one-sided dashed groups: %s' % sorted(names[g] for g in dashed - shared))

src_faces = [f for f in bm.faces if any(side(v) == src_side for v in f.verts)
             and all(side(v) != -src_side for v in f.verts)]
key = lambda verts: frozenset(verts)
src_keys = {key(f.verts): f for f in src_faces}

# UV islands on the source side, and each one's mirror offset from its faces that already have a twin.
island = {}
for f in src_faces:
    if f in island:
        continue
    n = len(set(island.values()))
    stack = [f]
    island[f] = n
    while stack:
        a = stack.pop()
        for e in a.edges:
            if e.seam:
                continue
            for b in e.link_faces:
                if b in island or b not in src_keys.values():
                    continue
                same = True
                for v in e.verts:
                    ua = next(l[uv].uv for l in a.loops if l.vert is v)
                    ub = next(l[uv].uv for l in b.loops if l.vert is v)
                    same &= (ua - ub).length < UV_SAME
                if same:
                    island[b] = n
                    stack.append(b)

target_faces = {key(f.verts): f for f in bm.faces if any(side(v) == -src_side for v in f.verts)}
offsets = {}
for f in src_faces:
    if not all(v in twin for v in f.verts):
        continue
    t = target_faces.get(key(twin[v] for v in f.verts))
    if t is None:
        continue
    for l in f.loops:
        tl = next(x for x in t.loops if x.vert is twin[l.vert])
        offsets.setdefault(island[f], []).append((l[uv].uv.x + tl[uv].uv.x, l[uv].uv.y - tl[uv].uv.y))
offset = {}
for n, samples in offsets.items():
    best = max(samples, key=lambda a: sum(abs(a[0] - b[0]) < UV_SPREAD and abs(a[1] - b[1]) < UV_SPREAD for b in samples))
    agree = [b for b in samples if abs(best[0] - b[0]) < UV_SPREAD and abs(best[1] - b[1]) < UV_SPREAD]
    if len(agree) < AGREE * len(samples):
        sys.exit('mirror_side.py: UV island %d is not one mirror offset (%d of %d corners agree)'
                 % (n, len(agree), len(samples)))
    if len(agree) < len(samples):
        print('mirror_side.py: UV island %d: %d of %d paired corners off its mirror offset (kept as they are)'
              % (n, len(samples) - len(agree), len(samples)))
    offset[n] = (sum(a for a, _ in agree) / len(agree), sum(b for _, b in agree) / len(agree))

# Delete what isn't the mirror of the source side.
lone = [v for v in bm.verts if side(v) == -src_side and v not in source_of]
stale = [f for k, f in target_faces.items()
         if not all(v in source_of for v in f.verts) or key(source_of[v] for v in f.verts) not in src_keys]
stale = [f for f in stale if not any(v in lone for v in f.verts)]
print('mirror_side.py: deleting %d vertices and %d more faces on the %s side'
      % (len(lone), len(stale), '+x' if src_side < 0 else '-x'))
bmesh.ops.delete(bm, geom=stale, context='FACES_ONLY')
bmesh.ops.delete(bm, geom=lone, context='VERTS')

# Copy each source face that has no mirror now.
have = {key(f.verts) for f in bm.faces}
made_verts = []
made_faces = []
for f in src_faces:
    for v in f.verts:
        if v in twin:
            continue
        w = bm.verts.new(mirrored(v.co))
        for s in shapes:
            w[s] = mirrored(v[s])
        for g, x in v[deform].items():
            if g in dashed:   # one-sided groups are settled below, once the faces exist
                w[deform][g] = x
            else:
                w[deform][index_of.get(flipped(names[g]), g)] = x
        twin[v] = w
        made_verts.append(w)
    mv = [twin[v] for v in f.verts]
    if key(mv) in have:
        continue
    if f not in island or island[f] not in offset:
        sys.exit('mirror_side.py: no mirror offset for the UV island of the face at %s' % (f.calc_center_median(),))
    S, D = offset[island[f]]
    nf = bm.faces.new(list(reversed(mv)))
    nf.smooth = f.smooth
    nf.material_index = f.material_index
    for l in f.loops:
        tl = next(x for x in nf.loops if x.vert is twin[l.vert])
        tl[uv].uv = (S - l[uv].uv.x, l[uv].uv.y - D)
        for c in colours:
            tl[c] = l[c]
    for e in f.edges:
        te = bm.edges.get([twin[e.verts[0]], twin[e.verts[1]]])
        if te is not None:
            te.seam = e.seam
            te.smooth = e.smooth
    have.add(key(mv))
    made_faces.append(nf)

# Edges the deleted faces leave with no face (an old edge a new cut splits on the source side).
wire = [e for e in bm.edges if not e.link_faces]
bmesh.ops.delete(bm, geom=wire, context='EDGES')
print('mirror_side.py: %d loose edges removed' % len(wire))

# Seams on the kept side follow the source's too (a seam flag is the only thing kept edges could differ in).
for e in bm.edges:
    a, b = e.verts
    if side(a) == -src_side or side(b) == -src_side:
        if a in source_of and b in source_of:
            s_edge = bm.edges.get([source_of[a], source_of[b]])
            if s_edge is not None:
                e.seam = s_edge.seam

# One-sided dashed groups on copied vertices: in only if every kept neighbour on its side is too.
made = set(made_verts)
for w in made_verts:
    for g in [g for g in w[deform].keys() if g in dashed and g not in shared]:
        kept = [e.other_vert(w) for e in w.link_edges if e.other_vert(w) not in made and side(e.other_vert(w)) != src_side]
        if not kept or not all(g in k[deform] for k in kept):
            del w[deform][g]

# Report: is it symmetric now?
bm.verts.index_update()
bm.faces.index_update()
bm.verts.ensure_lookup_table()
tree = kdtree.KDTree(len(bm.verts))
for v in bm.verts:
    tree.insert(v.co, v.index)
tree.balance()
pairs = {}
for v in bm.verts:
    _, i, d = tree.find(mirrored(v.co))
    if d < MATCH:
        pairs[v.index] = i
odd = [v.index for v in bm.verts if v.index not in pairs]
faces = {key(v.index for v in f.verts) for f in bm.faces}
no_mirror = [f.index for f in bm.faces if all(v.index in pairs for v in f.verts)
             and key(pairs[v.index] for v in f.verts) not in faces]
open_edges = [e for e in bm.edges if len(e.link_faces) == 1]
seam_keys = {frozenset(v.index for v in e.verts) for e in bm.edges if e.seam}
odd_seams = [k for k in seam_keys if frozenset(pairs.get(i, -1) for i in k) not in seam_keys]
print('mirror_side.py: %d seams, %d without a mirror seam' % (len(seam_keys), len(odd_seams)))
print('mirror_side.py: open edges before %d' % open_before)
print('mirror_side.py: made %d vertices and %d faces; now %d vertices, %d faces; %d without a mirror vertex, '
      '%d faces without a mirror face, %d open edges'
      % (len(made_verts), len(made_faces), len(bm.verts), len(bm.faces), len(odd), len(no_mirror), len(open_edges)))
if odd or no_mirror:
    sys.exit('mirror_side.py: not symmetric after; nothing saved')

if check:
    print('mirror_side.py: --check, nothing saved')
else:
    bm.to_mesh(me)
    me.update()
    if out:
        bpy.ops.wm.save_as_mainfile(filepath=out)
        print('mirror_side.py: saved %s' % out)
bm.free()
