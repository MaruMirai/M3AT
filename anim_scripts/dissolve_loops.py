"""Dissolve every other edge loop of a quad-dominant (triangulated in the glTF) mesh.

blender -b --python anim_scripts/dissolve_loops.py -- <in.glb> <out.glb> [out.blend]
"""
import bpy, sys, bmesh, math
from collections import deque

args = sys.argv[sys.argv.index('--') + 1:]
src, out = args[0], args[1]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
o = [o for o in bpy.data.objects if o.type == 'MESH'][0]
bm = bmesh.new(); bm.from_mesh(o.data)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
bmesh.ops.join_triangles(bm, faces=bm.faces, angle_face_threshold=math.radians(40),
                         angle_shape_threshold=math.radians(40), cmp_seam=False, cmp_sharp=False, cmp_uvs=True)
bm.edges.index_update()
uv = bm.loops.layers.uv.active

def seam(e):
    # The glTF split UV seams into separate vertices; welded, a seam is an edge whose two faces disagree on its UVs.
    if len(e.link_loops) != 2: return False
    a, b = e.link_loops
    ua = {a.vert: a[uv].uv.copy(), a.link_loop_next.vert: a.link_loop_next[uv].uv.copy()}
    ub = {b.vert: b[uv].uv.copy(), b.link_loop_next.vert: b.link_loop_next[uv].uv.copy()}
    return any((ua[v] - ub[v]).length > 1e-5 for v in e.verts)

parent = list(range(len(bm.edges)))
def find(i):
    while parent[i] != i:
        parent[i] = parent[parent[i]]; i = parent[i]
    return i
def union(a, b): parent[find(a)] = find(b)

# Loops: an edge carries on through a vertex to the edge opposite it.
for v in bm.verts:
    es = list(v.link_edges)
    if v.is_boundary:
        bd = [e for e in es if e.is_boundary]
        if len(bd) == 2 and len(es) == 3: union(bd[0].index, bd[1].index)
        continue
    if len(es) != 4 or len(v.link_faces) != 4 or any(len(f.verts) != 4 for f in v.link_faces): continue
    for e in es:
        opp = [x for x in es if x is not e and not (set(x.link_faces) & set(e.link_faces))]
        if len(opp) == 1: union(e.index, opp[0].index)

loop_of = [find(e.index) for e in bm.edges]
nbrs = {}
for f in bm.faces:
    if len(f.edges) != 4: continue
    fe = [l.edge for l in f.loops]
    for a, b in ((fe[0], fe[2]), (fe[1], fe[3])):
        la, lb = loop_of[a.index], loop_of[b.index]
        nbrs.setdefault(la, set()).add(lb); nbrs.setdefault(lb, set()).add(la)

# Two-colour the loops across the rings; where the rings close on an odd count, keep both.
colour, keep = {}, set()
for start in sorted(nbrs, key=lambda l: -len(nbrs[l])):
    if start in colour: continue
    colour[start] = 0; q = deque([start])
    while q:
        l = q.popleft()
        for n in nbrs[l]:
            if n == l: keep.add(l)
            elif n not in colour: colour[n] = 1 - colour[l]; q.append(n)
            elif colour[n] == colour[l]: keep.update((n, l))

members = {}
for e in bm.edges: members.setdefault(loop_of[e.index], []).append(e)
gone, skipped = [], {'boundary': 0, 'seam': 0, 'not quads': 0, 'odd ring': 0}
for l, es in members.items():
    if colour.get(l) != 1: continue
    if l in keep: skipped['odd ring'] += 1; continue
    if any(e.is_boundary for e in es): skipped['boundary'] += 1; continue
    # A loop that crosses a seam or runs into triangles loses only its clean stretches; the rest stays as edges.
    clean = [e for e in es if not seam(e) and all(len(f.verts) == 4 for f in e.link_faces)]
    if len(clean) < len(es): skipped['seam' if any(seam(e) for e in es) else 'not quads'] += 1
    gone.extend(clean)
print('LOOPS', len(members), 'dissolving', sum(1 for l in members if members[l][0] in gone), 'edges', len(gone), 'skipped', skipped)
bmesh.ops.dissolve_edges(bm, edges=gone, use_verts=True, use_face_split=False)
sides = {}
for f in bm.faces: sides[len(f.verts)] = sides.get(len(f.verts), 0) + 1
print('RESULT verts', len(bm.verts), 'faces by sides', sorted(sides.items()))
bm.to_mesh(o.data); bm.free()
if len(args) > 2: bpy.ops.wm.save_as_mainfile(filepath=args[2])
bpy.ops.export_scene.gltf(filepath=out, use_selection=False, export_yup=True)
