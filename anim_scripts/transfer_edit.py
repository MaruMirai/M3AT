"""Carry a hand edit of the cataphract armour's boots over to another armour of another topology.

blender -b --python transfer_boots.py -- <orig.glb> <edited.glb> <target.glb> <out.glb> [--report]

The edit is a displacement per vertex of the original (edited - orig, world space, legs only). Each target vertex takes
the displacement at the nearest point on the original's surface, barycentric, faded out by its distance from it, then
smoothed over the target's welded neighbours. Vertices far off the original (added parts such as a locust's booster)
take the mean displacement of the surface round their own piece's foot, so they ride along whole.
"""
import bpy, sys, numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

a = sys.argv[sys.argv.index('--') + 1:]
orig_p, edit_p, targ_p, out_p = a[:4]
LEGS_BELOW = -0.3          # local units of the base armour (its object scale ~10.58): legs only
NEAR, FAR = 0.15, 0.6      # world units: full displacement within NEAR of the original's surface, none past FAR
SMOOTH = 0

def load(p, keep=False):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=p, disable_bone_shape=True)
    new = [o for o in bpy.data.objects if o not in before]
    m = next(o for o in new if o.type == 'MESH')
    if not keep:
        for o in new:
            if o is not m: bpy.data.objects.remove(o)
    return m

bpy.ops.wm.read_factory_settings(use_empty=True)
O = load(orig_p); E = load(edit_p)
mwO = O.matrix_world.copy()
loc = np.array([v.co[:] for v in O.data.vertices])
ow = np.array([(mwO @ v.co)[:] for v in O.data.vertices])
ew = np.array([(E.matrix_world @ v.co)[:] for v in E.data.vertices])
D = ew - ow
D[loc[:, 2] > LEGS_BELOW] = 0
polys = [list(p.vertices) for p in O.data.polygons]
bvh = BVHTree.FromPolygons([Vector(p) for p in ow], polys)
for o in (O, E): bpy.data.objects.remove(o)

T = load(targ_p, keep=True)
mw = T.matrix_world; imw3 = mw.to_3x3().inverted()
me = T.data
tw = np.array([(mw @ v.co)[:] for v in me.vertices])
n = len(tw)
disp = np.zeros((n, 3)); dist = np.zeros(n)
for i, p in enumerate(tw):
    hit, nrm, fi, r = bvh.find_nearest(Vector(p))
    vs = polys[fi]
    w = poly_3d_calc([Vector(ow[j]) for j in vs], hit)
    disp[i] = sum(wj * D[j] for wj, j in zip(w, vs)); dist[i] = r
fade = np.clip((FAR - dist) / (FAR - NEAR), 0, 1)
disp *= fade[:, None]

# Weld by place for neighbours (the .glb is split at UV seams).
key = {}
weld = np.array([key.setdefault(tuple(np.round(p, 4)), len(key)) for p in tw])
m = len(key)
nb = [set() for _ in range(m)]
for e in me.edges:
    x, y = weld[e.vertices[0]], weld[e.vertices[1]]
    if x != y: nb[x].add(y); nb[y].add(x)
wd = np.zeros((m, 3)); cnt = np.zeros(m)
np.add.at(wd, weld, disp); np.add.at(cnt, weld, 1); wd /= cnt[:, None]
wdist = np.full(m, 1e9); np.minimum.at(wdist, weld, dist)

# Pieces far off the original: connected parts with every vertex past FAR ride their foot's mean displacement.
far = wdist > FAR
seen = np.zeros(m, bool); rides = 0
for s in range(m):
    if not far[s] or seen[s]: continue
    stack = [s]; comp = []; seen[s] = True
    while stack:
        x = stack.pop(); comp.append(x)
        for y in nb[x]:
            if far[y] and not seen[y]: seen[y] = True; stack.append(y)
    rim = {y for x in comp for y in nb[x] if not far[y]}
    if rim:
        mv = wd[list(rim)].mean(0)
        if np.linalg.norm(mv) > 1e-6:
            wd[comp] = mv; rides += len(comp)

moving = np.linalg.norm(wd, axis=1) > 1e-7
for _ in range(SMOOTH):
    new = wd.copy()
    for x in range(m):
        if not nb[x] or far[x]: continue
        new[x] = 0.5 * wd[x] + 0.5 * wd[list(nb[x])].mean(0)
    wd = new
final = wd[weld]
for i, v in enumerate(me.vertices):
    v.co = v.co + imw3 @ Vector(final[i])
me.update()
mag = np.linalg.norm(final, axis=1)
print('TRANSFER %s: %d of %d vertices moved, max %.3f, mean dist of moved %.3f, rode whole %d' % (
    targ_p, (mag > 1e-4).sum(), n, mag.max(), dist[mag > 1e-4].mean() if (mag > 1e-4).any() else 0, rides))
bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=out_p, export_format='GLB', export_skins=True, export_animations=False,
                          export_morph=False, export_extras=True, export_image_format='AUTO')
