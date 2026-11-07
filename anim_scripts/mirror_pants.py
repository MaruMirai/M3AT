"""Rebuilds the Pants with mirrored topology, so Blender's X-mirror editing works on it,
keeping the original's asymmetric UVs.

    blender -b <copy of the rig .blend> --python anim_scripts/mirror_pants.py -- <out.blend> <out.glb> +x|-x

+x keeps that half's shape and mirrors it onto the other. The kept half is first cut along
the mirror of the other half's UV seams, so after Symmetrize both halves have every seam
of the original as edges and every triangle lies inside one UV island; each triangle then
takes its UVs from the original under it, island by island. The original is kept hidden
as Pants_orig in out.blend. Never run it on the rig .blend itself: it is saved as a copy,
but run it on a copy anyway.

Written for the Pants only: the object and UV map names are fixed, the ribbons are marked
with material slot 1 (a garment using two materials would lose faces), the original's
faces must be triangles (as glTF imports them), and RIBBON and SAME_SEAM are in the
Pants' units. A thick or layered garment can take UVs from the wrong layer.
"""
import bpy, bmesh, sys, collections
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform
out_blend, out_glb, keep = sys.argv[sys.argv.index('--')+1:]
KEEP_POS = keep == '+x'
RIBBON = 0.1      # how far the cutting ribbons reach either side of the surface
SAME_SEAM = 0.01  # a mirrored seam this close to one of the kept half's is the same seam

O = bpy.data.objects['Pants']; O.name = 'Pants_orig'
om = O.data
uvo = om.uv_layers['UVMap'].data

# Islands: faces joined across every edge whose two sides have the same UVs. (The
# glTF mesh is split at every quad, for its normals, so shared vertices don't tell.)
key = lambda i: tuple(round(c, 5) for c in om.vertices[i].co)
parent = list(range(len(om.polygons)))
def find(a):
    while parent[a] != a: parent[a] = parent[parent[a]]; a = parent[a]
    return a
edges = collections.defaultdict(list)
for p in om.polygons:
    vs = list(p.vertices); ls = list(p.loop_indices)
    for j in range(3):
        a, b = key(vs[j]), key(vs[(j+1) % 3]); ua, ub = uvo[ls[j]].uv.copy(), uvo[ls[(j+1) % 3]].uv.copy()
        if a > b: a, b, ua, ub = b, a, ub, ua
        edges[(a, b)].append((p.index, ua, ub))
for v in edges.values():
    if len(v) == 2 and (v[0][1] - v[1][1]).length < 1e-4 and (v[0][2] - v[1][2]).length < 1e-4:
        parent[find(v[0][0])] = find(v[1][0])
roots = {}
island = [roots.setdefault(find(p.index), len(roots)) for p in om.polygons]
print('islands', len(roots))

# Per island, a BVH of the original's triangles with their UVs.
by_isl = collections.defaultdict(list)
for p in om.polygons: by_isl[island[p.index]].append(p.index)
isl_bvh = {}
for k, fs in by_isl.items():
    tris = [[om.vertices[i].co.copy() for i in om.polygons[f].vertices] for f in fs]
    uvs = [[uvo[l].uv.copy() for l in om.polygons[f].loop_indices] for f in fs]
    verts = [c for t in tris for c in t]
    isl_bvh[k] = (BVHTree.FromPolygons(verts, [[3*j, 3*j+1, 3*j+2] for j in range(len(tris))]), tris, uvs)
all_bvh = BVHTree.FromPolygons([om.vertices[i].co.copy() for i in range(len(om.vertices))], [list(p.vertices) for p in om.polygons])

# The working copy, welded, each face tagged with its island.
P = O.copy(); P.data = om.copy(); P.name = 'Pants'
for c in O.users_collection: c.objects.link(P)
P.vertex_groups.clear()
bm = bmesh.new(); bm.from_mesh(P.data)
isl = bm.faces.layers.int.new('island')
bm.faces.ensure_lookup_table()
for f in bm.faces: f[isl] = island[f.index]
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
side = (lambda x: x > 0) if KEEP_POS else (lambda x: x < 0)

def seam_edges():
    return [e for e in bm.edges if len(e.link_faces) == 2 and e.link_faces[0][isl] != e.link_faces[1][isl]]
seams = seam_edges()
kept = [e for e in seams if side(e.verts[0].co.x) or side(e.verts[1].co.x)]
other = [e for e in seams if not side(e.verts[0].co.x) or not side(e.verts[1].co.x)]
print('seam edges', len(seams), 'kept side', len(kept), 'other side', len(other))
kept_bvh = BVHTree.FromPolygons([v for e in kept for v in (e.verts[0].co.copy(), e.verts[1].co.copy(), (e.verts[0].co + e.verts[1].co) / 2)],
                                [[3*j, 3*j+1, 3*j+2] for j in range(len(kept))])

# Ribbons along the mirror of the other side's seams, across the surface.
bm.verts.ensure_lookup_table()
RIB = 1  # material index marks the ribbon faces
if len(P.data.materials) < 2: P.data.materials.append(None)
n_rib = 0
mir = lambda v: Vector((-v.x, v.y, v.z))
for e in other:
    a, b = e.verts
    ma, mb = mir(a.co), mir(b.co)
    hit = kept_bvh.find_nearest((ma + mb) / 2)
    if hit[0] is not None and hit[3] < SAME_SEAM: continue
    na, nb = mir(a.normal), mir(b.normal)
    vs = [bm.verts.new(c) for c in (ma - na*RIBBON, mb - nb*RIBBON, mb + nb*RIBBON, ma + na*RIBBON)]
    f = bm.faces.new(vs); f.material_index = RIB; f[isl] = -1; n_rib += 1
print('ribbons', n_rib)
bm.to_mesh(P.data); bm.free()

for ob in bpy.context.view_layer.objects: ob.select_set(ob is P)
bpy.context.view_layer.objects.active = P
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_mode(type='FACE')
bpy.ops.mesh.select_all(action='DESELECT')
P.active_material_index = RIB
bpy.ops.object.material_slot_select()
bpy.ops.mesh.intersect(mode='SELECT_UNSELECT', separate_mode='CUT', threshold=1e-6)
bpy.ops.object.mode_set(mode='OBJECT')

bm = bmesh.new(); bm.from_mesh(P.data)
isl = bm.faces.layers.int.get('island')
bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index == RIB], context='FACES')
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=1e-4)
bmesh.ops.symmetrize(bm, input=bm.verts[:] + bm.edges[:] + bm.faces[:], direction='-X' if KEEP_POS else 'X', dist=1e-4)
bmesh.ops.triangulate(bm, faces=bm.faces)
for f in bm.faces: f.material_index = 0

# Partners: every vertex must have one at its mirror.
from mathutils.kdtree import KDTree
kd = KDTree(len(bm.verts))
for v in bm.verts: kd.insert(v.co, v.index)
kd.balance()
lone = sum(kd.find(mir(v.co))[2] > 1e-5 for v in bm.verts)
print('vertices', len(bm.verts), 'faces', len(bm.faces), 'without a mirror partner', lone)

# UVs: each face takes the island of the original face under its middle, and each
# corner the UV of the nearest point on that island.
uvl = bm.loops.layers.uv.get('UVMap') or bm.loops.layers.uv.new('UVMap')
far = 0; stretched = 0; ratios = []
for f in bm.faces:
    c = f.calc_center_median()
    _, _, fi, _ = all_bvh.find_nearest(c)
    k = island[fi]
    bvh, tris, uvs = isl_bvh[k]
    for l in f.loops:
        co, _, j, d = bvh.find_nearest(l.vert.co)
        if d > 0.05: far += 1
        t, u = tris[j], uvs[j]
        w = barycentric_transform(co, t[0], t[1], t[2], u[0].to_3d(), u[1].to_3d(), u[2].to_3d())
        l[uvl].uv = (w.x, w.y)
    a3 = f.calc_area()
    u = [l[uvl].uv for l in f.loops]
    a2 = abs((u[1]-u[0]).cross(u[2]-u[0])) / 2
    if a3 > 1e-9: ratios.append((a2 / a3, f.index))
med = sorted(r for r, _ in ratios)[len(ratios)//2]
odd = [i for r, i in ratios if r > 4*med or r < med/4]
print('corners more than 0.05 from their island', far)
print('faces with UV scale off by over 4x', len(odd), 'of', len(bm.faces))
bm.faces.layers.int.remove(isl)
bm.to_mesh(P.data); bm.free()
P.data.materials.pop(index=1)
for p in P.data.polygons: p.use_smooth = True
if P.data.has_custom_normals: bpy.ops.mesh.customdata_custom_splitnormals_clear()

# Weights from the original surface, so the Armature modifier keeps working in the viewport.
dt = P.modifiers.new('wt', 'DATA_TRANSFER')
dt.object = O; dt.use_vert_data = True; dt.data_types_verts = {'VGROUP_WEIGHTS'}
dt.vert_mapping = 'POLYINTERP_NEAREST'; dt.layers_vgroup_select_src = 'ALL'; dt.layers_vgroup_select_dst = 'NAME'
bpy.ops.object.datalayout_transfer(modifier='wt')
bpy.ops.object.modifier_move_to_index(modifier='wt', index=0)
bpy.ops.object.modifier_apply(modifier='wt')
print('vertex groups', len(P.vertex_groups))

O.hide_set(True); O.hide_render = True
bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
for ob in bpy.context.view_layer.objects: ob.select_set(ob is P)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_skins=False, export_animations=False)
