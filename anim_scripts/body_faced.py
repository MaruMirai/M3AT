"""Lists the garments made from the body's own faces: those to recut or remake when the body's mesh changes.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/body_faced.py -- [garments folder] [--all]

A garment triangle counts as a body face if each corner stands over a body vertex (its nearest, within NEAR) at the
distance off the body most of the garment shares (the make script's push-out, within STRAY), and those three vertices
are a triangle of the body (its loop triangles, as the export takes them). Prints each garment with any, most first:
its triangles, how many are body faces and the usual distance (rig units). Garments holding only a few (under FEW) are
listed apart: a skirt's waist ring or a strap lying over the body by chance. --all prints every garment, none too.
"""
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import Prim, read_glb, to_blender, world_matrix

HERE = os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd()
NEAR = 0.6     # rig units: how far a garment vertex may be from the body vertex it stands over
STRAY = 0.06   # ... and how far off the garment's usual distance
FEW = 5        # fewer body faces than this are taken as chance

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
every = '--all' in argv
argv = [a for a in argv if a != '--all']
folder = os.path.abspath(argv[0]) if argv else os.path.join(os.path.dirname(HERE), 'Models', 'garments')

body = bpy.data.objects['Body']
me = body.data
BODY = np.array([tuple(v.co) for v in me.vertices])
me.calc_loop_triangles()
TRIS = {frozenset(t.vertices) for t in me.loop_triangles}
WORLD = np.array(body.matrix_world)[:3, 3]

rows = []
for fname in sorted(os.listdir(folder)):
    if not fname.lower().endswith('.glb'):
        continue
    j, binary = read_glb(os.path.join(folder, fname))
    total = held = 0
    usual = None
    for mi, mesh in enumerate(j['meshes']):
        M = world_matrix(j, mi)
        for prim in mesh['primitives']:
            if 'POSITION' not in prim['attributes']:
                continue
            pr = Prim(j, binary, mi, prim)
            at = to_blender(pr.attrs['POSITION'] @ M[:3, :3].T + M[:3, 3]) - WORLD
            total += len(pr.tri)
            d = np.linalg.norm(at[:, None, :] - BODY[None], axis=2)
            k, dk = d.argmin(1), d.min(1)
            near = dk < NEAR
            if near.sum() < 3:
                continue
            off = float(np.bincount(np.round(dk[near] / 0.01).astype(int)).argmax()) * 0.01
            usual = off if usual is None else usual
            over = {g: int(k[g]) for g in range(len(at)) if near[g] and abs(dk[g] - off) < STRAY}
            for t in pr.tri:
                if all(int(v) in over for v in t):
                    f = frozenset(over[int(v)] for v in t)
                    held += len(f) == 3 and f in TRIS
    rows.append((os.path.splitext(fname)[0], total, held, usual or 0.0))
rows.sort(key=lambda r: -r[2] / max(r[1], 1))
print('BODY_FACED garments made from the body\'s faces (triangles, body faces, usual distance off the body):')
for name, total, held, usual in rows:
    if held >= FEW:
        print('BODY_FACED %-26s %5d %5d (%3.0f%%) %.2f' % (name, total, held, 100 * held / total, usual))
print('BODY_FACED by chance (under %d): %s' % (FEW, ', '.join(r[0] for r in rows if 0 < r[2] < FEW)))
if every:
    print('BODY_FACED none: %s' % ', '.join(r[0] for r in rows if r[2] == 0))
