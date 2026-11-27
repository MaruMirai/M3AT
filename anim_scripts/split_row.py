"""Splits a garment's front triangles along the chest cut's bottom row, so overlay() in recut_garments.py can cut them.

    python3 anim_scripts/split_row.py <garment.glb> <out.glb>

For a garment whose own long triangles run from above the chest patch to well below it (the Leotard's centre, a fan from
the neckline to the crotch): recut_garments.py's overlay only takes triangles wholly over the patch, so those were left
whole. Each front triangle edge crossing the row (ROW: x, z in body units of the row's vertices, the belly cut's, under
the chest) is split where it crosses, and so is every triangle with that edge, by place, so no crack is left. Run on the
garment as it was before the cut, then recut_garments.py --from the folder it was written to.
"""
import sys, os
import numpy as np
sys.path.insert(0, 'anim_scripts')
from glb_edit import Prim, read_glb, write, extend, to_blender, world_matrix
src, dst = sys.argv[1], sys.argv[2]
ROW = np.array([[-1.65, 15.92], [-0.7, 15.89], [0.0, 15.84], [0.7, 15.89], [1.65, 15.92]])   # x, z of the chest's bottom row
def h(x): return np.interp(x, ROW[:, 0], ROW[:, 1])
j, b = read_glb(src)
pr = Prim(j, b, 0, j['meshes'][0]['primitives'][0]); M = world_matrix(j, 0)
P = to_blender(pr.attrs['POSITION'] @ M[:3, :3].T + M[:3, 3])
s = P[:, 2] - h(P[:, 0])
front = (P[:, 1] < -0.5) & (np.abs(P[:, 0]) < 2.4)
n0 = len(pr.attrs['POSITION']); rows, made, new_tris, drop = [], {}, [], []
def cut(a, c):
    key = (min(a, c), max(a, c))
    if key not in made:
        # the share along a->c where s crosses 0 (s is near linear along a short edge; refined twice for the curve)
        lo, hi = 0.0, 1.0
        for _ in range(30):
            t = (lo + hi) / 2; p = P[a] * (1 - t) + P[c] * t
            if (p[2] - h(p[0]) > 0) == (s[a] > 0): lo = t
            else: hi = t
        t = (lo + hi) / 2
        rows.append(pr.blend(a, c, t)); made[key] = n0 + len(rows) - 1
        made[(key, 'a')] = a if a < c else c
    return made[key]
EPS = 1e-4
K = lambda v: tuple(np.round(P[v], 5))
def ekey(a, c): return tuple(sorted((K(a), K(c))))
sg = lambda v: 0 if abs(s[v]) < EPS else (1 if s[v] > 0 else -1)
# the edges to split: those crossing the row in the front triangles (by place, so every copy and neighbour shares them)
split = set()
for t in pr.tri:
    t = [int(v) for v in t]
    if not all(front[v] for v in t): continue
    for a, c in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
        if sg(a) * sg(c) < 0: split.add(ekey(a, c))
for t in pr.tri:
    t = [int(v) for v in t]
    es = [(i, ekey(t[i], t[(i + 1) % 3])) for i in range(3)]
    hit = [i for i, e in es if e in split]
    if not hit: continue
    if len(hit) == 1:
        i = hit[0]; a, b2, c = t[i], t[(i + 1) % 3], t[(i + 2) % 3]
        m = cut(a, b2); new_tris += [[a, m, c], [m, b2, c]]
    elif len(hit) == 2:
        i = ({0, 1, 2} - set(hit)).pop()          # the unsplit edge is t[i]-t[i+1]; the lone corner is t[i+2]
        b2, c, a = t[i], t[(i + 1) % 3], t[(i + 2) % 3]
        mc, mb = cut(c, a), cut(a, b2)            # points on c-a and a-b
        new_tris += [[a, mb, mc], [mb, b2, c], [mb, c, mc]]
    else:
        continue
    drop.append(tuple(t))
extend(pr, rows, new_tris, drop)
write(j, b, pr, dst)
print('SPLIT', len(drop), 'triangles across the row,', len(rows), 'points added')
