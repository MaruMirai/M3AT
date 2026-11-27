"""Fairs a cut patch of the body: each of its vertices moved along the surface's normal, in every key, so the patch's
curvature runs evenly (no creases where the old flat faces met), the old vertices round it held.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/fair_patch.py -- <before.blend> [--mu 0.003] [--check]

The vertices moved are the cut's (those of the body not in <before.blend> within MATCH, less any on the underbust row
UNDERBUST, which the belly cut made and garments hem at) and the old vertices wholly inside the cut (each of whose
neighbours is the cut's or another such: the chest cut's 2 and 137). In each key, each moves only along its smooth
normal there (its layout over the surface kept, so garments and texture still match), by the amounts that make the
normal part of each vertex's umbrella Laplacian (the mean of its neighbours less itself, on the patch and the ring
round it) as small as can be, less a pull back to where it is (MU: larger keeps more of a hand-made shape, such as the
Female chest). A key that leaves the patch as the Basis has it keeps the Basis's result. Saved into the .blend it
opened (copy it first). Then recut the garments (recut_garments.py) and export.
"""
import math
import sys

import bpy
import numpy as np

MATCH = 1e-3        # body units: a vertex this near one of the old body's is that one
UNDERBUST = (15.75, 0.05)   # z and reach (body units): the belly cut's row, held
MU = 0.003

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
check = '--check' in argv
argv = [a for a in argv if a != '--check']
if '--mu' in argv:
    i = argv.index('--mu')
    MU = float(argv[i + 1])
    del argv[i:i + 2]
if not argv:
    sys.exit('fair_patch.py: give the .blend before the cut')


def body():
    if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    me = bpy.data.objects['Body'].data
    me.calc_loop_triangles()
    keys = {k.name: np.array([tuple(d.co) for d in k.data]) for k in me.shape_keys.key_blocks}
    return me, keys, np.array([tuple(t.vertices) for t in me.loop_triangles]), [tuple(e.vertices) for e in me.edges]

current = bpy.data.filepath
bpy.ops.wm.open_mainfile(filepath=argv[0])
_, old_keys, _, _ = body()
OLD = old_keys['Basis']
bpy.ops.wm.open_mainfile(filepath=current)
me, K, T, E = body()
B = K['Basis']
nb = {i: set() for i in range(len(B))}
for a, b in E:
    nb[a].add(b)
    nb[b].add(a)
cut = {i for i, p in enumerate(B) if np.linalg.norm(OLD - p, axis=1).min() > MATCH and abs(p[2] - UNDERBUST[0]) > UNDERBUST[1]}
inner = {i for i in range(len(B)) if i not in cut and len(nb[i] & cut) >= 3
         and all(j in cut or len(nb[j] & cut) >= 3 for j in nb[i])}
movable = sorted(cut | inner)
print('FAIR %d vertices (%d of the cut, old inside it: %s)' % (len(movable), len(cut), sorted(inner)))
ring = sorted(set(movable) | {j for i in movable for j in nb[i]})


def normals(P):
    n = np.zeros_like(P)
    f = np.cross(P[T[:, 1]] - P[T[:, 0]], P[T[:, 2]] - P[T[:, 0]])
    for j in range(3):
        np.add.at(n, T[:, j], f)
    return n / np.linalg.norm(n, axis=1, keepdims=True)


def fair(P):
    """P with the movable vertices moved along their normals to even out the patch's curvature."""
    n = normals(P)
    col = {j: c for c, j in enumerate(movable)}
    A = np.zeros((len(ring) + len(movable), len(movable)))
    b = np.zeros(len(ring) + len(movable))
    for r, i in enumerate(ring):
        lap = np.mean([P[j] for j in nb[i]], 0) - P[i]
        b[r] = -n[i] @ lap
        for j in nb[i]:
            if j in col:
                A[r, col[j]] += (n[i] @ n[j]) / len(nb[i])
        if i in col:
            A[r, col[i]] -= 1.0
    for c in range(len(movable)):
        A[len(ring) + c, c] = math.sqrt(MU)
    t = np.linalg.lstsq(A, b, rcond=None)[0]
    Q = P.copy()
    for c, j in enumerate(movable):
        Q[j] = P[j] + t[c] * n[j]
    return Q, t


faired = {}
basis_q, t = fair(B)
for name, P in K.items():
    if name != 'Basis' and np.abs(P[movable] - B[movable]).max() < 1e-6 and np.abs(P[ring] - B[ring]).max() < 1e-6:
        faired[name] = P.copy()
        faired[name][movable] = basis_q[movable]
        continue
    faired[name], t = (basis_q, t) if name == 'Basis' else fair(P)
    print('FAIR %-16s moves %+.3f .. %+.3f' % (name, t.min(), t.max()))
if not check:
    for k in me.shape_keys.key_blocks:
        k.data.foreach_set('co', faired[k.name].astype(np.float32).ravel())
    me.vertices.foreach_set('co', faired['Basis'].astype(np.float32).ravel())
    me.update()
    bpy.ops.wm.save_mainfile()
    print('FAIR saved into %s' % bpy.data.filepath)
