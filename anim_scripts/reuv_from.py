"""Gives a garment cut from another (shorts and briefs from the Pants) back the source's texture places after an
edit such as Symmetrize copied one side's UVs across: every face takes the UV island of the source face under its
middle, and each of its corners the UV of the nearest point on that island, interpolated across its triangle.

    blender -b --python anim_scripts/reuv_from.py -- <source.glb> <in.glb> <out.glb> [--max d]

Both files are read as they are (the same units); a corner further than --max (default 0.01) from the source's
surface is reported, as there the result is a guess. Only the UVs change; the mesh, material and image are kept.
"""
import bpy, sys, numpy as np
from mathutils.bvhtree import BVHTree
from mathutils import Vector
from mathutils.geometry import barycentric_transform

args = sys.argv[sys.argv.index('--') + 1:]
most = 0.01
if '--max' in args:
    i = args.index('--max'); most = float(args[i + 1]); del args[i:i + 2]
src_path, in_path, out_path = args

def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.data.objects if o not in before and o.type == 'MESH'][0]

bpy.ops.wm.read_factory_settings(use_empty=True)
S = load(src_path); sm = S.data
sm.calc_loop_triangles()
suv = sm.uv_layers.active.data

# UV islands of the source: faces joined across an edge whose two ends have the same UVs on both sides.
parent = list(range(len(sm.polygons)))
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]; a = parent[a]
    return a
edge_faces = {}
for p in sm.polygons:
    n = p.loop_total
    for k in range(n):
        a = sm.loops[p.loop_start + k]; b = sm.loops[p.loop_start + (k + 1) % n]
        key = tuple(sorted((a.vertex_index, b.vertex_index)))
        uvs = {a.vertex_index: tuple(np.round(suv[a.index].uv, 5)), b.vertex_index: tuple(np.round(suv[b.index].uv, 5))}
        edge_faces.setdefault(key, []).append((p.index, uvs))
for key, fs in edge_faces.items():
    for i in range(1, len(fs)):
        if fs[i][1] == fs[0][1]:
            parent[find(fs[i][0])] = find(fs[0][0])
island_of = [find(i) for i in range(len(sm.polygons))]

verts = [v.co.copy() for v in sm.vertices]
tris_by_island = {}
for t in sm.loop_triangles:
    tris_by_island.setdefault(island_of[t.polygon_index], []).append(t)
trees = {}
for isl, ts in tris_by_island.items():
    trees[isl] = (BVHTree.FromPolygons(verts, [list(t.vertices) for t in ts]), ts)
whole = BVHTree.FromPolygons(verts, [list(p.vertices) for p in sm.polygons])
print('REUV source', len(sm.polygons), 'faces,', len(trees), 'islands')

def uv_at(tri, co):
    a, b, c = (verts[i] for i in tri.vertices)
    ua, ub, uc = (Vector((*suv[l].uv, 0.0)) for l in tri.loops)
    r = barycentric_transform(co, a, b, c, ua, ub, uc)
    return r.x, r.y

O = load(in_path); m = O.data
uv = m.uv_layers.active.data
far = 0; worst = 0.0
for p in m.polygons:
    mid = sum((m.vertices[v].co for v in p.vertices), Vector()) / p.loop_total
    hit = whole.find_nearest(mid)
    tree, ts = trees[island_of[hit[2]]]
    for li in range(p.loop_start, p.loop_start + p.loop_total):
        co = m.vertices[m.loops[li].vertex_index].co
        loc, _n, ti, d = tree.find_nearest(co)
        worst = max(worst, d)
        if d > most:
            far += 1
        uv[li].uv = uv_at(ts[ti], co)
print('REUV', in_path, '->', out_path, len(m.polygons), 'faces; corners off the source island past', most, ':', far,
      '; worst', round(worst, 5))

for o in bpy.data.objects:
    o.select_set(o == O)
bpy.context.view_layer.objects.active = O
bpy.ops.export_scene.gltf(filepath=out_path, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False)
