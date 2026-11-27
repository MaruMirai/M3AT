"""Re-cuts a garment that was cut by hand from another (its parent) out of the parent as it is now.

    blender -b --python anim_scripts/recut_from_parent.py -- <parent.glb> <child.glb> <out.glb> [--check] [--no-trim] [--name N]

Made for the tops cut by hand from the shirt (Tanktop, CropTop, Bra) after the shoulder reshape of 2026-11-27: the
shirt was rebuilt from its script on the new body's triangles, while the tops were only moved with the body
(follow_body.py), keeping the old triangles over the shoulders. A top is a region of the shirt's surface bounded by its
own hems (neckline, armholes, bottom hem: each an open loop of its mesh), so this lays those hems onto the new parent
and keeps the parent's triangles inside them:

- Each hem vertex of the child (its open edges, welded by place) is laid on the parent's nearest point among triangles
  facing its way (FACING), snapped onto a parent vertex or edge within SNAP.
- Each hem segment between two such points is traced over the parent along the plane through both points that holds
  their mean normal: every parent edge the plane crosses between them gives a point, found once per edge, so the two
  triangles either side of it share that point exactly (no cracks). A crossing within SNAP of an edge's end is that
  vertex.
- Each parent triangle holding any of these points is split by a constrained Delaunay triangulation in its own plane
  (mathutils.geometry.delaunay_2d_cdt), the hem segments as constraints.
- The pieces are grouped by the hems (flood fill over shared edges that aren't hem segments), and each group is kept if
  most of its area lies on the child (its middle within ON of the child's surface, not past one of its hems).
- The rims are trimmed: a kept triangle standing less than RIM off its hem (a lip along a fold of the parent) goes.

Every new vertex takes the parent triangle's attributes (normal, texture place, morph targets) blended at its place, so
the child reads the parent's texture as the parent does (the child's own image is replaced by the parent's) and the
child's shape is the parent's: a hand edit off the parent's surface inside the hems is not kept (the tops' chests were
0.02-0.03 off the old shirt, from being recut apart from it). The output is the parent's .glb (material, image,
targets) with the child's node name. --check reports without writing.
"""
import os
import sys
from collections import defaultdict

import numpy as np
from mathutils import Vector
from mathutils.geometry import delaunay_2d_cdt

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import Prim, read_glb, world_matrix, write

WELD = 1e-5      # garment units: vertices this near are one place (texture seams)
FACING = -0.2    # a hem vertex is laid only on parent triangles whose normal is at least this along its own (only
                 # the far side of an armpit is shut out: a hem's own normal can be off, its triangles folded)
SNAP = 0.04      # garment units: a point this near a parent vertex is that vertex; this near an edge, on it (0.02
                 # left 8 slivers under 2 degrees in the crop top, where its hem runs just beside a shirt edge)
ON = 0.25        # garment units: a piece's middle this near the child's surface (and not past a hem) is on it
STEPS = 400      # most parent triangles one hem segment may cross
RIM = 0.2        # garment units: a kept triangle on a hem standing less than this off it is trimmed (--no-trim keeps it)

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
check = '--check' in argv
TRIM = '--no-trim' not in argv
argv = [a for a in argv if a != '--no-trim']
argv = [a for a in argv if a != '--check']
name = None
if '--name' in argv:
    i = argv.index('--name')
    name = argv[i + 1]
    del argv[i:i + 2]
if len(argv) < 3:
    sys.exit(__doc__)
parent_path, child_path, out_path = argv[:3]
name = name or os.path.splitext(os.path.basename(out_path))[0]


def load(path):
    j, binary = read_glb(path)
    assert len(j['meshes']) == 1 and len(j['meshes'][0]['primitives']) == 1, path + ': one mesh, one primitive'
    pr = Prim(j, binary, 0, j['meshes'][0]['primitives'][0])
    return j, binary, pr, world_matrix(j, 0)


def weld(pos):
    """Each row's welded vertex, and the welded places."""
    ids, places = {}, []
    wid = np.empty(len(pos), dtype=int)
    for r, p in enumerate(pos):
        k = tuple(np.round(p / WELD).astype(np.int64))
        if k not in ids:
            ids[k] = len(places)
            places.append(p)
        wid[r] = ids[k]
    return wid, np.array(places)


def nearest_on(p, A, B, C):
    """The nearest point of each triangle (A, B, C rows) to p, as barycentric places (n, 3)."""
    ab, ac, ap = B - A, C - A, p - A
    d1, d2 = (ab * ap).sum(1), (ac * ap).sum(1)
    bp, cp = p - B, p - C
    d3, d4 = (ab * bp).sum(1), (ac * bp).sum(1)
    d5, d6 = (ab * cp).sum(1), (ac * cp).sum(1)
    va, vb, vc = d3 * d6 - d5 * d4, d5 * d2 - d1 * d6, d1 * d4 - d3 * d2
    out = np.zeros((len(A), 3))
    den = va + vb + vc
    den = np.where(np.abs(den) < 1e-30, 1e-30, den)
    out[:] = np.stack([1 - (vb + vc) / den, vb / den, vc / den], 1)
    def put(mask, bc):
        out[mask] = bc[mask]
    one = np.ones(len(A))
    zero = np.zeros(len(A))
    with np.errstate(divide='ignore', invalid='ignore'):
        v_ab = d1 / (d1 - d3)
        w_ac = d2 / (d2 - d6)
        w_bc = (d4 - d3) / ((d4 - d3) + (d5 - d6))
    # the regions, last written wins, so in reverse order of the usual test
    put((va <= 0) & (d4 - d3 >= 0) & (d5 - d6 >= 0), np.stack([zero, 1 - w_bc, w_bc], 1))
    put((vb <= 0) & (d2 >= 0) & (d6 <= 0), np.stack([1 - w_ac, zero, w_ac], 1))
    put((vc <= 0) & (d1 >= 0) & (d3 <= 0), np.stack([1 - v_ab, v_ab, zero], 1))
    put((d6 >= 0) & (d5 <= d6), np.stack([zero, zero, one], 1))
    put((d3 >= 0) & (d4 <= d3), np.stack([zero, one, zero], 1))
    put((d1 <= 0) & (d2 <= 0), np.stack([one, zero, zero], 1))
    return out


# ---- the parent -----------------------------------------------------------------------------------------------------

pj, pbin, P, PM = load(parent_path)
cj, cbin, C, CM = load(child_path)
# the child in the parent's space
to_parent = np.linalg.inv(PM) @ CM
P_ROW = P.attrs['POSITION']
pwid, V = weld(P_ROW)
T = pwid[P.tri]                                  # parent triangles, welded vertices
TN = np.cross(V[T[:, 1]] - V[T[:, 0]], V[T[:, 2]] - V[T[:, 0]])
TN /= np.maximum(np.linalg.norm(TN, axis=1, keepdims=True), 1e-12)
EDGE_TRIS = defaultdict(list)                    # (a, b), a < b -> parent triangles holding it
for ti, t in enumerate(T):
    for k in range(3):
        a, b = int(t[k]), int(t[(k + 1) % 3])
        EDGE_TRIS[(min(a, b), max(a, b))].append(ti)
VERT_TRIS = defaultdict(list)
for ti, t in enumerate(T):
    for v in t:
        VERT_TRIS[int(v)].append(ti)

# ---- the child's hems -----------------------------------------------------------------------------------------------

C_ROW = C.attrs['POSITION'] @ to_parent[:3, :3].T + to_parent[:3, 3]
cwid, CV = weld(C_ROW)
CT = cwid[C.tri]
CTN = np.cross(CV[CT[:, 1]] - CV[CT[:, 0]], CV[CT[:, 2]] - CV[CT[:, 0]])
CTN /= np.maximum(np.linalg.norm(CTN, axis=1, keepdims=True), 1e-12)
count = defaultdict(int)
for t in CT:
    for k in range(3):
        a, b = int(t[k]), int(t[(k + 1) % 3])
        count[(min(a, b), max(a, b))] += 1
assert max(count.values()) <= 2, 'the child is not manifold'
OPEN = {e for e, n in count.items() if n == 1}
nxt = {}
for t in CT:
    for k in range(3):
        a, b = int(t[k]), int(t[(k + 1) % 3])
        if (min(a, b), max(a, b)) in OPEN:
            assert a not in nxt, 'a hem vertex on two hems (child vertex %d)' % a
            nxt[a] = b
loops, seen = [], set()
for s in nxt:
    if s in seen:
        continue
    loop, v = [], s
    while v not in seen:
        seen.add(v)
        loop.append(v)
        v = nxt[v]
    assert v == s, 'a hem that does not close'
    loops.append(loop)
CVN = np.zeros_like(CV)                          # the child's vertex normals (area weighted)
for t, n in zip(CT, np.cross(CV[CT[:, 1]] - CV[CT[:, 0]], CV[CT[:, 2]] - CV[CT[:, 0]])):
    CVN[t] += n
CVN /= np.maximum(np.linalg.norm(CVN, axis=1, keepdims=True), 1e-12)
print('CHILD %s: %d triangles, hems of %s vertices' % (os.path.basename(child_path), len(CT),
                                                       ', '.join(str(len(l)) for l in loops)))

# ---- points on the parent -------------------------------------------------------------------------------------------
# A point is ('v', vertex), ('e', a, b, t) with a < b and t the share from a to b, or ('f', triangle, b0, b1, b2).

def place(pt):
    if pt[0] == 'v':
        return V[pt[1]]
    if pt[0] == 'e':
        return V[pt[1]] * (1 - pt[3]) + V[pt[2]] * pt[3]
    return np.array(pt[2:]) @ V[T[pt[1]]]

def tris_of(pt):
    if pt[0] == 'v':
        return set(VERT_TRIS[pt[1]])
    if pt[0] == 'e':
        return set(EDGE_TRIS[(pt[1], pt[2])])
    return {pt[1]}

def snapped(ti, bc):
    """A place in a triangle as a point, on a vertex or edge if it is within SNAP of one."""
    t = T[ti]
    p = bc @ V[t]
    for k in range(3):
        if np.linalg.norm(p - V[t[k]]) < SNAP:
            return ('v', int(t[k]))
    for k in range(3):
        a, b = int(t[k]), int(t[(k + 1) % 3])
        d = V[b] - V[a]
        s = float(np.clip((p - V[a]) @ d / (d @ d), 0, 1))
        if np.linalg.norm(V[a] + d * s - p) < SNAP:
            return ('e', a, b, s) if a < b else ('e', b, a, 1 - s)
    return ('f', ti) + tuple(float(x) for x in bc)

def lay(p, n):
    """The child's hem vertex p (normal n) on the parent."""
    ok = TN @ n > FACING
    idx = np.where(ok)[0]
    A, B, Cc = V[T[idx, 0]], V[T[idx, 1]], V[T[idx, 2]]
    bc = nearest_on(p, A, B, Cc)
    q = bc[:, :1] * A + bc[:, 1:2] * B + bc[:, 2:] * Cc
    d = np.linalg.norm(q - p, axis=1)
    k = int(d.argmin())
    return snapped(int(idx[k]), bc[k]), float(d[k])

crossing = {}   # (a, b) parent edge -> its point where a hem plane crosses it (kept so neighbours share it)

def cross_edge(a, b, s):
    """The point where the plane (signed distances s) crosses edge a-b."""
    e = (min(a, b), max(a, b))
    sa, sb = s[e[0]], s[e[1]]
    t = sa / (sa - sb)
    p = V[e[0]] * (1 - t) + V[e[1]] * t
    if np.linalg.norm(p - V[e[0]]) < SNAP:
        return ('v', e[0])
    if np.linalg.norm(p - V[e[1]]) < SNAP:
        return ('v', e[1])
    return ('e', e[0], e[1], float(t))

def tri_crossings(ti, s):
    """The plane's crossings of triangle ti's edges: [(edge, point)]."""
    t = T[ti]
    out = []
    for k in range(3):
        a, b = int(t[k]), int(t[(k + 1) % 3])
        if (s[a] > 0) != (s[b] > 0):
            out.append(((min(a, b), max(a, b)), cross_edge(a, b, s)))
    return out

def trace(A, B, nA, nB):
    """The points from A to B over the parent: [A, ..., B], or None if the walk failed."""
    if tris_of(A) & tris_of(B):
        return [A, B]
    pa, pb = place(A), place(B)
    d = pb - pa
    length = float(np.linalg.norm(d))
    d /= length
    n = nA + nB
    N = np.cross(d, n)
    N /= np.linalg.norm(N)
    s = (V - pa) @ N
    s = np.where(np.abs(s) < 1e-9, 1e-9, s)     # nothing exactly on the plane
    # the first triangle: of those round A, the one whose crossing reaches furthest towards B
    best = None
    for ti in tris_of(A):
        for e, pt in tri_crossings(ti, s):
            ahead = float((place(pt) - pa) @ d)
            if best is None or ahead > best[0]:
                best = (ahead, ti, e, pt)
    if best is None or best[0] <= 0:
        return None
    _, ti, e, pt = best
    path = [A, pt]
    targets = tris_of(B)
    for _ in range(STEPS):
        others = [x for x in EDGE_TRIS[e] if x != ti]
        if not others:
            return None                          # walked off the parent's edge
        ti = others[0]
        if ti in targets:
            path.append(B)
            return path
        nxt_ = [(e2, p2) for e2, p2 in tri_crossings(ti, s) if e2 != e]
        if len(nxt_) != 1:
            return None
        e, pt = nxt_[0]
        if float((place(pt) - pa) @ d) > length * 1.5 + 0.5:
            return None                          # went past B
        path.append(pt)
    return None

def same(p, q):
    return p == q or (p[0] != 'f' and q[0] != 'f' and np.linalg.norm(place(p) - place(q)) < 1e-9)

segments = []      # (point, point, parent triangle) hem pieces, each within one parent triangle
failed = 0
worst = 0.0
for loop in loops:
    laid = []
    for v in loop:
        pt, dist = lay(CV[v], CVN[v])
        worst = max(worst, dist)
        if dist > 0.1:
            print('FAR hem vertex at %s: %.3f off the parent' % (np.round(CV[v], 3), dist))
        n = TN[next(iter(tris_of(pt)))]
        if laid and same(laid[-1][0], pt):
            continue
        laid.append((pt, n))
    if len(laid) > 1 and same(laid[0][0], laid[-1][0]):
        laid.pop()
    for k in range(len(laid)):
        (a, na), (b, nb) = laid[k], laid[(k + 1) % len(laid)]
        path = trace(a, b, na, nb)
        if path is None:
            failed += 1
            print('FAILED to trace a hem segment from %s to %s' % (np.round(place(a), 3), np.round(place(b), 3)))
            continue
        for p, q in zip(path, path[1:]):
            if same(p, q):
                continue
            shared = tris_of(p) & tris_of(q)
            if not shared:
                failed += 1
                print('FAILED: hem points %s and %s share no triangle' % (p, q))
                continue
            for ti in shared:                    # a piece along an edge belongs to both triangles
                segments.append((p, q, ti))
print('HEMS laid on the parent: %d pieces, child up to %.3f off it, %d failed' % (len(segments), worst, failed))

# ---- split the parent's triangles -----------------------------------------------------------------------------------

def key(pt):
    """One name per place: vertices by number, edge points by edge and share, face points as they are."""
    if pt[0] == 'v':
        return ('v', pt[1])
    if pt[0] == 'e':
        return ('e', pt[1], pt[2], round(pt[3], 9))
    return ('f', pt[1]) + tuple(round(x, 9) for x in pt[2:])

points = {}        # key -> point
on_tri = defaultdict(set)
cons = defaultdict(list)
for p, q, ti in segments:
    for x in (p, q):
        points[key(x)] = x
        on_tri[ti].add(key(x))
    cons[ti].append((key(p), key(q)))
# an edge point lies on both triangles of its edge, whether or not a hem piece runs in the other one
for k, p in list(points.items()):
    for ti in tris_of(p):
        on_tri[ti].add(k)

def bary_in(pt, ti):
    t = [int(x) for x in T[ti]]
    if pt[0] == 'v':
        return np.array([1.0 if v == pt[1] else 0.0 for v in t])
    if pt[0] == 'e':
        bc = np.zeros(3)
        bc[t.index(pt[1])] = 1 - pt[3]
        bc[t.index(pt[2])] = pt[3]
        return bc
    assert pt[1] == ti
    return np.array(pt[2:])

rows = []          # output vertex rows: dicts of attributes
row_of = {}        # (point key, parent row triple) -> row index
out_tris, out_keys, barrier = [], [], set()

def row(k, ti, bc):
    corners = tuple(int(r) for r in P.tri[ti])
    # the rows it is blended from: one for a vertex, the edge's two, or the triangle's three (so the two triangles
    # either side of an edge share its points' rows unless the edge is a texture seam)
    held = tuple(r for r, w in zip(corners, bc) if w > 1e-12)
    rk = (k, held)
    if rk in row_of:
        return row_of[rk]
    r = {}
    for nm, arr in P.attrs.items():
        if nm.startswith('JOINTS') or nm.startswith('WEIGHTS'):
            sys.exit('recut_from_parent.py: the parent carries bone weights (not handled)')
        r[nm] = bc @ arr[list(corners)]
    r['POSITION'] = place(points[k]) if k in points else bc @ V[T[ti]]
    if 'NORMAL' in r:
        r['NORMAL'] = r['NORMAL'] / max(np.linalg.norm(r['NORMAL']), 1e-12)
    r['targets'] = [{nm: bc @ arr[list(corners)] for nm, arr in tg.items()} for tg in P.targets]
    row_of[rk] = len(rows)
    rows.append(r)
    return row_of[rk]

split = 0
for ti in range(len(T)):
    t = [int(x) for x in T[ti]]
    corner_keys = [('v', v) for v in t]
    for v in t:
        points.setdefault(('v', v), ('v', v))
    if ti not in on_tri or on_tri[ti] <= set(corner_keys) and not cons[ti]:
        out_tris.append([row(k, ti, bary_in(points[k], ti)) for k in corner_keys])
        out_keys.append(corner_keys)
        continue
    split += 1
    # the triangle laid flat
    a, b, c = V[t]
    ex = (b - a) / np.linalg.norm(b - a)
    ey = np.cross(TN[ti], ex)
    flat = lambda p: Vector((float((p - a) @ ex), float((p - a) @ ey)))
    ks = list(corner_keys) + sorted(on_tri[ti] - set(corner_keys))
    index = {k: i for i, k in enumerate(ks)}
    corner2 = [np.array(flat(V[v])) for v in t]
    # laid out from the triangle's own corners at the point's share, so points on an edge are exactly on it
    coords = [Vector(tuple(bary_in(points[k], ti) @ np.array(corner2))) for k in ks]
    # the outline: each edge's points in order along it
    edges = []
    for kk in range(3):
        va, vb = t[kk], t[(kk + 1) % 3]
        along = [(0.0, ('v', va)), (1.0, ('v', vb))]
        for k in ks:
            p = points[k]
            if p[0] == 'e' and {p[1], p[2]} == {va, vb}:
                along.append((p[3] if p[1] == va else 1 - p[3], k))
        along.sort()
        for (_, k1), (_, k2) in zip(along, along[1:]):
            edges.append((index[k1], index[k2]))
    n_outline = len(edges)
    for k1, k2 in cons[ti]:
        edges.append((index[k1], index[k2]))
    vs, es, fs, ov, oe, of = delaunay_2d_cdt(coords, edges, [], 0, 1e-6)
    assert all(ov[i] for i in range(len(vs))), 'the hem crossed itself in parent triangle %d' % ti
    vmap = [min(ov[i]) for i in range(len(vs))]
    is_hem = set()
    for ei, (x, y) in enumerate(es):
        if any(o >= n_outline for o in oe[ei]):
            is_hem.add(frozenset((ks[vmap[x]], ks[vmap[y]])))
    barrier |= is_hem
    for f in fs:
        fk = [ks[vmap[i]] for i in f]
        if len(set(fk)) < 3:
            continue
        # the triangulator works in single precision and gives back flat triangles along an edge holding several
        # points: measured in double from the points' shares, they have no area
        b3 = [bary_in(points[k], ti) for k in fk]
        if abs(np.linalg.det(np.array(b3))) < 1e-7:
            continue
        p3 = [place(points[k]) for k in fk]
        if np.cross(p3[1] - p3[0], p3[2] - p3[0]) @ TN[ti] < 0:
            fk = fk[::-1]
        out_tris.append([row(k, ti, bary_in(points[k], ti)) for k in fk])
        out_keys.append(fk)
# a hem piece along a parent edge is a barrier in both triangles even where only one was split
for p, q, ti in segments:
    barrier.add(frozenset((key(p), key(q))))
print('SPLIT %d parent triangles: %d triangles in all' % (split, len(out_tris)))

# ---- keep the pieces on the child -----------------------------------------------------------------------------------

by_edge = defaultdict(list)
for fi, fk in enumerate(out_keys):
    for kk in range(3):
        e = frozenset((fk[kk], fk[(kk + 1) % 3]))
        if e not in barrier:
            by_edge[e].append(fi)
group = [-1] * len(out_keys)
groups = 0
for s in range(len(out_keys)):
    if group[s] >= 0:
        continue
    stack = [s]
    group[s] = groups
    while stack:
        f = stack.pop()
        fk = out_keys[f]
        for kk in range(3):
            for g in by_edge[frozenset((fk[kk], fk[(kk + 1) % 3]))]:
                if group[g] < 0:
                    group[g] = groups
                    stack.append(g)
    groups += 1

CA, CB, CC = CV[CT[:, 0]], CV[CT[:, 1]], CV[CT[:, 2]]
def on_child(p, n):
    """Whether p lies on the child: near its surface (facing the same way) and not past a hem."""
    ok = CTN @ n > FACING
    idx = np.where(ok)[0]
    bc = nearest_on(p, CA[idx], CB[idx], CC[idx])
    q = bc[:, :1] * CA[idx] + bc[:, 1:2] * CB[idx] + bc[:, 2:] * CC[idx]
    d = np.linalg.norm(q - p, axis=1)
    k = int(d.argmin())
    if d[k] > ON:
        return False
    # past a hem: the nearest point is on an open edge and p lies beyond it, along the surface
    t = CT[idx[k]]
    for kk in range(3):
        if bc[k][(kk + 2) % 3] < 1e-6:
            a, b = int(t[kk]), int(t[(kk + 1) % 3])
            if (min(a, b), max(a, b)) in OPEN:
                off = p - q[k]
                along = off - (off @ CTN[idx[k]]) * CTN[idx[k]]
                if np.linalg.norm(along) > 0.2 * max(d[k], 1e-9) + 1e-4:
                    return False
    return True

score = np.zeros((groups, 2))
for fi, fk in enumerate(out_keys):
    p3 = [place(points[k]) for k in fk]
    nrm = np.cross(p3[1] - p3[0], p3[2] - p3[0])
    area = np.linalg.norm(nrm) / 2
    if area < 1e-12:
        continue
    score[group[fi], 0 if on_child(np.mean(p3, axis=0), nrm / (2 * area)) else 1] += area
keep_group = score[:, 0] > score[:, 1]
kept = [f for f in range(len(out_tris)) if keep_group[group[f]]]
print('GROUPS %d, kept %d: %s' % (groups, int(keep_group.sum()),
                                  ', '.join('%.2f/%.2f%s' % (s[0], s[0] + s[1], ' kept' if k else '')
                                            for s, k in zip(score, keep_group))))

# the rims: a hem traced just past a fold of the parent (the back of the armpit) leaves a lip of thin triangles along
# it, each a sliver of its parent triangle. Maru trimmed them by hand from the tops of 2026-11-27: every triangle with
# an open edge standing under RIM off it (its height over that edge; the longest, if two are open). In those tops the
# trimmed ones stood 0.02-0.16 off and every hem triangle kept 0.275 or more; one pass gave exactly Maru's edit.
if TRIM:
    open_count = defaultdict(int)
    for f in kept:
        fk = out_keys[f]
        for kk in range(3):
            open_count[frozenset((fk[kk], fk[(kk + 1) % 3]))] += 1
    trimmed = []
    for f in kept:
        fk = out_keys[f]
        p3 = [place(points[k]) for k in fk]
        hems = [np.linalg.norm(p3[(kk + 1) % 3] - p3[kk]) for kk in range(3)
                if open_count[frozenset((fk[kk], fk[(kk + 1) % 3]))] == 1]
        if hems and np.linalg.norm(np.cross(p3[1] - p3[0], p3[2] - p3[0])) / max(hems) < RIM:
            trimmed.append(f)
    kept = [f for f in kept if f not in set(trimmed)]
    print('TRIMMED %d thin triangles off the rims (under %.2f off their hem)' % (len(trimmed), RIM))

# ---- the result -----------------------------------------------------------------------------------------------------

tris = np.array([out_tris[f] for f in kept], dtype=int)
used = np.unique(tris)
index = np.full(len(rows), -1)
index[used] = np.arange(len(used))
R = [rows[i] for i in used]
for nm in P.attrs:
    P.attrs[nm] = np.array([r[nm] for r in R]).astype(P.attrs[nm].dtype)
for i, tg in enumerate(P.targets):
    for nm in tg:
        tg[nm] = np.array([r['targets'][i][nm] for r in R])
P.tri = index[tris]

# check: open edges of the result against the child's hems
cnt = defaultdict(int)
rw, _ = weld(P.attrs['POSITION'])
for t in rw[P.tri]:
    for kk in range(3):
        a, b = int(t[kk]), int(t[(kk + 1) % 3])
        cnt[(min(a, b), max(a, b))] += 1
opened = sum(1 for n in cnt.values() if n == 1)
areas = np.linalg.norm(np.cross(P.attrs['POSITION'][P.tri[:, 1]] - P.attrs['POSITION'][P.tri[:, 0]],
                                P.attrs['POSITION'][P.tri[:, 2]] - P.attrs['POSITION'][P.tri[:, 0]]), axis=1) / 2
child_area = np.linalg.norm(np.cross(CB - CA, CC - CA), axis=1).sum() / 2
print('RESULT %d triangles, %d vertices, %d open edges (child %d), area %.2f (child %.2f), smallest triangle %.5f'
      % (len(P.tri), len(R), opened, len(OPEN), areas.sum(), child_area, areas.min()))
if check:
    sys.exit(0)
pj = dict(pj)
pj['nodes'] = [dict(n, name=name) if 'mesh' in n else n for n in pj['nodes']]
write(pj, pbin, P, out_path)
print('WROTE', out_path)
