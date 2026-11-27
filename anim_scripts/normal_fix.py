"""Works out and stores each vertex's normal fix: what the game's normals at rest lack of the shading before a cut.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/normal_fix.py -- <before.blend> <before garments folder> [garment ...] [--check]

The game makes a vertex's normal the area-weighted mean of the faces round it (SkinnedFigure.Normals). A cut that puts
new vertices on an old surface (the chest cut of 2026-11-25, recut_garments.py) keeps the shape, but the old surface was
flat faces meeting at shallow folds that the old corners' averaged normals hid: a new vertex on a fold gets that fold's
normal, and the cut shows as creases. The fix of a vertex is the normal it had before (an old vertex: its old normal; a
new one: the old normals blended at its place on the old surface, the nearest point there) less the one the game works
out now, at rest. The game adds it to its worked-out normal in every shape (SkinnedFigure.ShapePiece), so at rest the
shading is as before the cut, while a key's reshaping (the Female chest) still shades as worked out.

Stored where the export reads it (export_frames.py writes nfix lines):
- the rig's Body: a point attribute normal_fix (FLOAT_VECTOR, Blender axes), saved into the .blend it opened;
- each garment's .glb in Models/garments (those named, else every one in the before folder): a vertex attribute
  _NORMAL_FIX (glTF axes; Blender imports it as it is, so the export turns it), written in place with glb_edit.
Vertices welded by place (texture seam copies) share one fix, as the game welds them for its normals. A fix under
SMALL is left out. Running it again works from the before files, so it replaces what it wrote. --check only reports.
"""
import os
import sys

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import Prim, read_glb, write

SMALL = 1e-4        # a fix shorter than this is none
FACING = 0.0        # the old surface a garment vertex is laid on must face its own way at least this much (a lining)
ATTR = 'normal_fix'
GLB_ATTR = '_NORMAL_FIX'

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
check = '--check' in argv
argv = [a for a in argv if a != '--check']
if len(argv) < 2:
    sys.exit('normal_fix.py: give the .blend before the cut and the garments as they were before it')
before_blend, before_garments = os.path.abspath(argv[0]), os.path.abspath(argv[1])
only = {a.lower() for a in argv[2:]}
GARMENTS = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Models', 'garments')


def welded(pos, tris):
    """Each vertex's welded id (by exact place, as the game's Normals) and the area-weighted normal of each id."""
    ids, first = np.zeros(len(pos), int), {}
    for i, p in enumerate(map(tuple, pos)):
        ids[i] = first.setdefault(p, len(first))
    n = np.zeros((len(first), 3))
    f = np.cross(pos[tris[:, 1]] - pos[tris[:, 0]], pos[tris[:, 2]] - pos[tris[:, 0]])
    for j in range(3):
        np.add.at(n, ids[tris[:, j]], f)
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
    return ids, n


def fixes(pos, tris, old_pos, old_tris):
    """Each vertex's fix (in the meshes' own axes): the old surface's blended normal at its nearest point there, less
    its area-weighted normal now. Old and new must be in the same space."""
    ids, now = welded(pos, tris)
    old_ids, old_n = welded(old_pos, old_tris)
    vn = old_n[old_ids]   # each old vertex's normal
    tree = BVHTree.FromPolygons([Vector(p) for p in old_pos], [tuple(map(int, t)) for t in old_tris])
    out = np.zeros((len(pos), 3))
    for i, p in enumerate(pos):
        n = now[ids[i]]
        found = tree.find_nearest_range(Vector(p), 0.05) or [tree.find_nearest(Vector(p))]
        best = None
        for loc, fn, k, d in found:
            if loc is None:
                continue
            if fn.dot(Vector(n)) < FACING and len(found) > 1:
                continue
            if best is None or d < best[3]:
                best = (loc, fn, k, d)
        if best is None:
            continue
        loc, _, k, _ = best
        t = [int(x) for x in old_tris[k]]
        a, b, c = (Vector(old_pos[x]) for x in t)
        # the place's weights on the triangle's corners, as a blend of their normals
        w = [barycentric_transform(loc, a, b, c, Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)))[j] for j in range(3)]
        want = sum(vn[x] * wj for x, wj in zip(t, w))
        want /= max(np.linalg.norm(want), 1e-12)
        fix = want - n
        if np.linalg.norm(fix) > SMALL:
            out[i] = fix
    for i in range(len(pos)):   # welded copies alike
        out[i] = out[np.nonzero(ids == ids[i])[0][0]]
    return out


def body_mesh(path=None):
    if path:
        bpy.ops.wm.open_mainfile(filepath=path)
    if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    me = bpy.data.objects['Body'].data
    me.calc_loop_triangles()
    pos = np.array([tuple(v.co) for v in me.vertices])
    return pos, np.array([tuple(t.vertices) for t in me.loop_triangles])


# ---- the body ----------------------------------------------------------------------------------------------------
current = bpy.data.filepath
old_pos, old_tris = body_mesh(before_blend)
bpy.ops.wm.open_mainfile(filepath=current)
pos, tris = body_mesh()
fix = fixes(pos, tris, old_pos, old_tris)
lengths = np.linalg.norm(fix, axis=1)
print('NORMAL_FIX Body: %d vertices fixed, largest %.1f degrees' % ((lengths > 0).sum(), np.degrees(2 * np.arcsin(min(1, lengths.max() / 2)))))
if not check:
    me = bpy.data.objects['Body'].data
    a = me.attributes.get(ATTR) or me.attributes.new(ATTR, 'FLOAT_VECTOR', 'POINT')
    a.data.foreach_set('vector', fix.astype(np.float32).ravel())
    me.update()
    bpy.ops.wm.save_mainfile()
    print('NORMAL_FIX saved into %s' % bpy.data.filepath)

# ---- the garments ------------------------------------------------------------------------------------------------
for fname in sorted(os.listdir(before_garments)):
    stem = os.path.splitext(fname)[0]
    if not fname.lower().endswith('.glb') or (only and stem.lower() not in only):
        continue
    path = os.path.join(GARMENTS, fname)
    jo, bo = read_glb(os.path.join(before_garments, fname))
    po = Prim(jo, bo, 0, jo['meshes'][0]['primitives'][0])
    j, b = read_glb(path)
    pr = Prim(j, b, 0, j['meshes'][0]['primitives'][0])
    f = fixes(pr.attrs['POSITION'], pr.tri, po.attrs['POSITION'], po.tri)   # (glTF axes, both)
    lengths = np.linalg.norm(f, axis=1)
    print('NORMAL_FIX %-22s %d of %d vertices fixed, largest %.1f degrees' % (
        stem, (lengths > 0).sum(), len(f), np.degrees(2 * np.arcsin(min(1, lengths.max() / 2))) if len(f) else 0))
    if check:
        continue
    if GLB_ATTR not in pr.prim['attributes']:
        j['accessors'].append({'componentType': 5126, 'count': 0, 'type': 'VEC3'})
        pr.prim['attributes'][GLB_ATTR] = len(j['accessors']) - 1
        pr.kinds[GLB_ATTR] = (5126, False)
    pr.attrs[GLB_ATTR] = f
    write(j, b, pr, path)
