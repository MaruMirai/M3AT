"""Gives a garment cut from the body's faces a round bottom hem in place of the shirt's notched one.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/round_hem.py -- <out folder> <garment ...> [--from folder] [--check] [--lift L] [--plane]
    blender -b Models/pawnHumanRig.blend --python anim_scripts/round_hem.py -- --learn <object in the rig>

The shirts' bottom hem (the -BasicShirt group's edge, carried into the collared shirt, ruffled shirt, tank top, jacket
and flak jacket) runs low at the front and back with an upward notch at each front side and back side, where the
trousers' waist comes up the hip (Maru, 2026-11-27: "an upwards triangle gap in the lower rim"). This makes the hem one
smooth ring along its low points:

- The ring: a plane z = a + b*y (body units, Blender axes; x left free as the body is symmetric) fitted to the bottom
  hem's vertices, those standing more than NOTCH above it left out and the fit repeated.
- The notches filled with the legs' faces: every body triangle the garment doesn't hold that meets its bottom hem (and
  those beyond them) with a corner above the plane, pushed out as the garment is (each body vertex along its normal by
  the garment's usual distance; at the hem's own vertices exactly where the garment's are, so they join), each painted
  an island of its own from the garment's nearest triangles (glb_edit.paint, by distance).
- The cut: every triangle across the plane split there (attributes blended along each edge, the same from either
  side, so copies at a texture seam stay at one place), those below it dropped. A corner within SNAP of the plane is
  taken as on it (no slivers).
- The lift: the shirts and trousers stand at one distance off the body (0.10), and the game hides a trouser triangle
  only where the garment over it covers all of it, so the trouser triangles the new hem crosses would be drawn at the
  shirt's own depth (z-fighting). A garment within LIFT_BELOW of the body has its new hem's vertices pushed out LIFT
  along their normals and the row above them half that.

Morph targets: a new vertex takes the nearest old vertex's (the shirts' are zeroed, so the export fits them). Writes
<out folder>/<Name>.glb (give Models/garments to write in place; copy them aside first). --check only reports.
"""
import io
import os
import sys
from collections import defaultdict

import bpy
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import Prim, extend, image_of, paint, read_glb, to_blender, to_gltf, view_bytes, world_matrix, write

HERE = os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd()
GARMENTS = os.path.join(os.path.dirname(HERE), 'Models', 'garments')

NEAR = 0.6          # body units: how far a garment vertex may be from the body vertex it stands over
STRAY = 0.06        # ... and how far off the garment's usual distance
NOTCH = 0.15        # body units: hem vertices this far above the fitted ring are a notch's, left out of the fit
SNAP = 0.03         # body units: a corner this near the plane is on it
LIFT = 0.05         # body units: the new hem pushed out this far, the row above half of it
LIFT_BELOW = 0.2    # ... only for garments standing less than this off the body (not the jackets, at 0.30)
PAINT_NEAR = 1.5    # body units: garment triangles whose middle is this near a new triangle's are read for its island
BAND = 2.0          # body units above the new hem whose texels are repainted (the old hem's band laid along it)
EDGE_IN = 0.03      # ... read this much further in from the old hem's very edge
PAD_BAND = 3        # texels painted past a band triangle's edges where no triangle is
READ = 6            # ... and at least this many of the nearest
WELD = 1e-5
LEARN_VERTEX = 0.03  # body units: --learn takes a hem vertex this near a body vertex's place as on it
LEARN_EDGE = 0.08    # ... and this near a body edge as on it
LEVEL_SNAP = 0.06    # body units: --level takes a body vertex this near the target ring (in height) as on it
LEVEL_END = 0.06     # ... and a crossing this near an edge's end (along it) onto that end

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
check = '--check' in argv
argv = [a for a in argv if a != '--check']
if '--from' in argv:
    i = argv.index('--from')
    GARMENTS = os.path.abspath(argv[i + 1])
    del argv[i:i + 2]
if '--lift' in argv:
    i = argv.index('--lift')
    LIFT = float(argv[i + 1])
    del argv[i:i + 2]
learn = None
if '--learn' in argv:
    i = argv.index('--learn')
    learn = argv[i + 1]
    del argv[i:i + 2]
use_plane = '--plane' in argv
path_arg = None
if '--path' in argv:     # another path file than round_hem_path.json, to learn into, make or cut by
    i = argv.index('--path')
    path_arg = os.path.abspath(argv[i + 1])
    del argv[i:i + 2]
level = None
if '--level' in argv:    # make a path: the body's own edges where z = a + b*y + c*|x| (see level_path)
    i = argv.index('--level')
    level = [float(x) for x in argv[i + 1].split(',')]
    del argv[i:i + 2]
argv = [a for a in argv if a != '--plane']
if len(argv) < 2 and not learn and not level:
    sys.exit(__doc__)
out_folder, names = (os.path.abspath(argv[0]), argv[1:]) if argv else (None, [])

# ---- the body (rest shape) ------------------------------------------------------------------------------------------
body = bpy.data.objects['Body']
me = body.data
BODY = np.array([tuple(d.co) for d in me.shape_keys.key_blocks['Basis'].data])
me.calc_loop_triangles()
BTRIS = [tuple(int(v) for v in t.vertices) for t in me.loop_triangles]
BNORM = np.array([tuple(v.normal) for v in me.vertices])
WORLD = np.array(body.matrix_world)[:3, 3]
B_EDGE_TRIS = defaultdict(list)
for bi, t in enumerate(BTRIS):
    for k in range(3):
        B_EDGE_TRIS[frozenset((t[k], t[(k + 1) % 3]))].append(bi)


PATH_FILE = path_arg or os.path.join(HERE, 'round_hem_path.json')
BEDGES = sorted({tuple(sorted((t[k], t[(k + 1) % 3]))) for t in BTRIS for k in range(3)})
TRISETS = [frozenset(t) for t in BTRIS]


def learn_path(name):
    """The bottom hem of the rig's object name (a garment edited by hand: Maru's shirt of 2026-11-27) as a path over
    the body: each hem vertex laid on the body vertex (within LEARN_VERTEX) or body edge it was put on by hand, in the
    garment's own space (each body vertex where the garment's vertex over it is, else pushed out along its normal), the
    choices among near ones made so every face of the garment lies within one body triangle. Saved to PATH_FILE as
    ["v", vertex] / ["e", a, b, share from a] in the loop's order."""
    import json
    import itertools
    ob = bpy.data.objects[name]
    EV = np.array([tuple(ob.matrix_world @ v.co) for v in ob.data.vertices]) - WORLD
    faces = [list(p.vertices) for p in ob.data.polygons]
    d = np.linalg.norm(EV[:, None] - BODY[None], axis=2)
    lab, dist = d.argmin(1), d.min(1)
    near = dist < NEAR
    usual = float(np.bincount(np.round(dist[near] / 0.01).astype(int)).argmax()) * 0.01
    place = {}
    for i in range(len(EV)):
        if abs(dist[i] - usual) < LEARN_VERTEX:
            place.setdefault(int(lab[i]), EV[i])
    GP = np.array([place[v] if v in place else BODY[v] + BNORM[v] * usual for v in range(len(BODY))])
    cands = {}
    for i, p in enumerate(EV):
        c = [(float(np.linalg.norm(GP[v] - p)), ('v', int(v))) for v in np.nonzero(np.linalg.norm(GP - p, axis=1) < LEARN_VERTEX)[0]]
        for a, b in BEDGES:
            e = GP[b] - GP[a]
            t = float(np.clip((p - GP[a]) @ e / (e @ e), 0, 1))
            dd = float(np.linalg.norm(GP[a] + e * t - p))
            if dd < LEARN_EDGE and 0.02 < t < 0.98:
                c.append((dd, ('e', int(a), int(b), t)))
        cands[i] = sorted(c)
    lost = [i for i in cands if not cands[i]]
    if lost:
        sys.exit('round_hem.py: vertices %s are on no body vertex or edge' % lost)
    choice = {i: 0 for i in cands}
    def sup(s_):
        return {s_[1]} if s_[0] == 'v' else {s_[1], s_[2]}
    def bad():
        return [f for f in faces if not any(set().union(*[sup(cands[i][choice[i]][1]) for i in f]) <= t for t in TRISETS)]
    for _ in range(5):
        if not bad():
            break
        for f in bad():
            for i in f:
                best = (len(bad()), choice[i])
                for k in range(len(cands[i])):
                    choice[i] = k
                    if len(bad()) < best[0]:
                        best = (len(bad()), k)
                choice[i] = best[1]
    if bad():
        sys.exit('round_hem.py: %d faces of %s lie in no one body triangle' % (len(bad()), name))
    # the bottom hem loop
    count = defaultdict(int)
    for f in faces:
        for k in range(len(f)):
            count[frozenset((f[k], f[(k + 1) % len(f)]))] += 1
    nxt = {f[k]: f[(k + 1) % len(f)] for f in faces for k in range(len(f)) if count[frozenset((f[k], f[(k + 1) % len(f)]))] == 1}
    loops, seen = [], set()
    for s0 in nxt:
        if s0 in seen:
            continue
        loop, v = [], s0
        while v not in seen and v in nxt:
            seen.add(v)
            loop.append(v)
            v = nxt[v]
        loops.append(loop)
    hem = min(loops, key=lambda l: EV[l][:, 2].mean())
    path = []
    for i in hem:
        s_ = cands[i][choice[i]][1]
        path.append(['v', s_[1]] if s_[0] == 'v' else ['e', s_[1], s_[2], round(s_[3], 6)])
    moved = max(cands[i][choice[i]][0] for i in hem)
    # the body is symmetric (mirror_side.py): each point's share made the mean of its own and its mirror's
    mirror = {}
    for v in range(len(BODY)):
        m = BODY * [-1, 1, 1]
        mirror[v] = int(np.linalg.norm(m - BODY[v], axis=1).argmin())
    shares = {}
    for q in path:
        if q[0] == 'e':
            shares[(q[1], q[2])] = q[3]
    for q in path:
        if q[0] == 'e':
            twin = (mirror[q[1]], mirror[q[2]])
            if twin in shares:
                q[3] = round((q[3] + shares[twin]) / 2, 6)
            elif twin[::-1] in shares:
                q[3] = round((q[3] + 1 - shares[twin[::-1]]) / 2, 6)
    json.dump({'from': name, 'path': path}, open(PATH_FILE, 'w'), indent=0)
    print('ROUND_HEM learned %d hem points from %s (%d on body vertices, %d on edges; moved at most %.3f onto them) -> %s'
          % (len(path), name, sum(p[0] == 'v' for p in path), sum(p[0] == 'e' for p in path), moved, PATH_FILE))


def level_path(a, b, c, ref):
    """Makes a hem path by Maru's principles from a target ring z = a + b*y + c*|x| (body units, Blender axes, at the
    garment's own places): every hem point a body vertex or on a body edge, each body triangle crossed by one straight
    piece (the target's level set over the body's triangles, marched), a body vertex within LEVEL_SNAP of the target
    taken onto it, and a crossing within LEVEL_END (along the edge) of its end moved onto that end (no slivers, fewer
    pieces; by share of the edge it moved points up to 0.31 off the target where the hips' triangles are long).
    Places are the reference garment's (ref, a .glb) where it stands over the body, else the body's pushed out at its
    usual distance. Saved to PATH_FILE."""
    import json
    j, binary = read_glb(ref)
    pr = Prim(j, binary, 0, j['meshes'][0]['primitives'][0])
    M = world_matrix(j, 0)
    G = to_blender(pr.attrs['POSITION'] @ M[:3, :3].T + M[:3, 3]) - WORLD
    d = np.linalg.norm(G[:, None, :] - BODY[None], axis=2)
    lab, dist = d.argmin(1), d.min(1)
    near = dist < NEAR
    usual = float(np.bincount(np.round(dist[near] / 0.01).astype(int)).argmax()) * 0.01
    place = {}
    for g in range(len(G)):
        if near[g] and abs(dist[g] - usual) < STRAY:
            place.setdefault(int(lab[g]), G[g])
    GP = np.array([place[v] if v in place else BODY[v] + BNORM[v] * usual for v in range(len(BODY))])
    f = GP[:, 2] - (a + b * GP[:, 1] + c * np.abs(GP[:, 0]))
    f = np.where(np.abs(f) < LEVEL_SNAP, 0.0, f)
    # the torso and hips round the target only (not the hands hanging beside them)
    band = [t for t in BTRIS if np.abs(f[list(t)]).min() < 1.0 and np.abs(GP[list(t), 0]).max() < 3.8]
    for _ in range(20):
        changed = False
        for t in band:
            for k in range(3):
                u, v = t[k], t[(k + 1) % 3]
                if f[u] * f[v] < 0:
                    share = f[u] / (f[u] - f[v])
                    along = np.linalg.norm(GP[v] - GP[u])
                    if min(share, 1 - share) * along < LEVEL_END:
                        f[u if share < 0.5 else v] = 0.0
                        changed = True
        if not changed:
            break
    def pt(u, v):
        if u > v:
            u, v = v, u
        return ('e', u, v, round(float(f[u] / (f[u] - f[v])), 6))
    segs = set()
    for t in band:
        pts = [('v', v) for v in t if f[v] == 0]
        for k in range(3):
            u, v = t[k], t[(k + 1) % 3]
            if f[u] * f[v] < 0:
                pts.append(pt(u, v))
        pts = list(dict.fromkeys(pts))
        if len(pts) == 2:
            segs.add(frozenset(pts))
        elif len(pts) == 3:
            # the whole triangle on the target (three snapped corners): its edges are left to its neighbours
            pass
    adj = defaultdict(set)
    for sg in segs:
        x, y = tuple(sg)
        adj[x].add(y)
        adj[y].add(x)
    odd = [x for x in adj if len(adj[x]) != 2]
    # the loop round the body: walk from the point nearest the front middle
    def where(q):
        return GP[q[1]] if q[0] == 'v' else GP[q[1]] * (1 - q[3]) + GP[q[2]] * q[3]
    start = min((q for q in adj if len(adj[q]) == 2), key=lambda q: abs(where(q)[0]) + max(0, where(q)[1]))
    loop, prev, cur = [start], None, start
    while True:
        nxt = [q for q in adj[cur] if q != prev]
        if not nxt:
            break
        prev, cur = cur, nxt[0]
        if cur == start:
            break
        loop.append(cur)
        if len(loop) > 500:
            sys.exit('round_hem.py: the level loop does not close')
    P3 = np.array([where(q) for q in loop])
    span = np.unwrap(np.arctan2(P3[:, 0], -P3[:, 1]))
    if abs(span[-1] - span[0]) < 5:
        sys.exit('round_hem.py: the level loop does not go round the body (%d points, %d odd points)' % (len(loop), len(odd)))
    path = [['v', q[1]] if q[0] == 'v' else ['e', q[1], q[2], q[3]] for q in loop]
    json.dump({'from': 'level %g %+g y %+g |x|' % (a, b, c), 'path': path}, open(PATH_FILE, 'w'), indent=0)
    print('ROUND_HEM made a path of %d points (%d body vertices, %d edge crossings; %d points off the loop) from '
          'z = %.3f %+.3f y %+.3f |x|, heights %.2f..%.2f -> %s' % (len(path), sum(q[0] == 'v' for q in path),
          sum(q[0] == 'e' for q in path), len(odd), a, b, c, P3[:, 2].min(), P3[:, 2].max(), PATH_FILE))


def weld(pos):
    ids, wid = {}, np.empty(len(pos), dtype=int)
    for r, p in enumerate(pos):
        wid[r] = ids.setdefault(tuple(np.round(p / WELD).astype(np.int64)), len(ids))
    return wid


def closest_points(P, A, B, C):
    """For each point (n, 3) the nearest point of each triangle (rows of A, B, C): (n, T, 3) barycentric places."""
    p = P[:, None, :]
    ab, ac, ap = (B - A)[None], (C - A)[None], p - A[None]
    bp, cp = p - B[None], p - C[None]
    d1, d2 = (ab * ap).sum(2), (ac * ap).sum(2)
    d3, d4 = (ab * bp).sum(2), (ac * bp).sum(2)
    d5, d6 = (ab * cp).sum(2), (ac * cp).sum(2)
    va, vb, vc = d3 * d6 - d5 * d4, d5 * d2 - d1 * d6, d1 * d4 - d3 * d2
    with np.errstate(divide='ignore', invalid='ignore'):
        den = 1 / (va + vb + vc)
        out = np.stack([1 - (vb + vc) * den, vb * den, vc * den], 2)
        cases = [
            ((va <= 0) & (d4 - d3 >= 0) & (d5 - d6 >= 0), lambda: (0, 1 - (d4 - d3) / ((d4 - d3) + (d5 - d6)), (d4 - d3) / ((d4 - d3) + (d5 - d6)))),
            ((vb <= 0) & (d2 >= 0) & (d6 <= 0), lambda: (1 - d2 / (d2 - d6), 0, d2 / (d2 - d6))),
            ((vc <= 0) & (d1 >= 0) & (d3 <= 0), lambda: (1 - d1 / (d1 - d3), d1 / (d1 - d3), 0)),
            ((d6 >= 0) & (d5 <= d6), lambda: (0, 0, 1)),
            ((d3 >= 0) & (d4 <= d3), lambda: (0, 1, 0)),
            ((d1 <= 0) & (d2 <= 0), lambda: (1, 0, 0)),
        ]
        for mask, f in cases:
            vals = np.stack([np.broadcast_to(np.asarray(x, float), d1.shape) for x in f()], 2)
            out[mask] = vals[mask]
    return out


def repaint_band(pr, to_body, picture, SRC, OLD, OLD_TRI, OLD_UV, H, new_z):
    """Repaints the texels of every triangle within BAND above the new hem (the filled ones' islands too) from the old
    garment, read lower down by how far the old hem stood above the ring at that angle, fading to none at BAND: the
    old hem's band (its stitching) is laid along the new hem, and the filled notches get the cloth above the old one's
    edge instead of a smear of it. Texels are written in place (inside each triangle, and its PAD where no triangle
    is), so the islands stay where they are."""
    Hh, Ww = picture.shape[:2]
    yc = H[:, 1].mean()
    def angle(p):
        return np.arctan2(p[..., 0], -(p[..., 1] - yc))
    # the old hem unrolled: (s, z), s its length round the body (horizontal), densely sampled
    th = angle(H)
    order = np.argsort(th)
    Hs = H[order]
    th = th[order]
    step = np.linalg.norm(np.diff(np.vstack([Hs, Hs[:1]])[:, :2], axis=0), axis=1)
    s_at = np.concatenate([[0], np.cumsum(step)])          # s of each hem vertex, the first again at the end
    loop_len = s_at[-1]
    z_at = np.concatenate([Hs[:, 2], Hs[:1, 2]])
    fine_s = np.linspace(0, loop_len, 2400, endpoint=False)
    fine_z = np.interp(fine_s, s_at, z_at)
    th_at = np.concatenate([th, th[:1] + 2 * np.pi])
    def unroll(p):
        a = angle(p)
        a = np.where(a < th[0], a + 2 * np.pi, a)
        return np.interp(a, th_at, s_at)
    def offset_z(s_p, h):
        """The old hem's offset curve at s_p, h in from it (the upper envelope of circles round its points)."""
        ds = np.abs(s_p[:, None] - fine_s[None])
        ds = np.minimum(ds, loop_len - ds)
        reach = np.sqrt(np.clip(h[:, None] ** 2 - ds ** 2, 0, None))
        reach = np.where(ds <= h[:, None], reach, -np.inf)
        best = (fine_z[None] + reach).max(1)
        return np.where(np.isfinite(best), best, np.interp(s_p, s_at, z_at))
    G = to_body(pr.attrs['POSITION'])
    uv = pr.attrs['TEXCOORD_0'] * [Ww, Hh]
    height = G[:, 2] - new_z(G)
    band = [t for t in pr.tri if height[t].min() < BAND]
    # what every triangle covers, so a band triangle's padding doesn't paint over another island
    from PIL import ImageDraw
    mask = Image.new('L', (Ww, Hh), 0)
    draw = ImageDraw.Draw(mask)
    for t in pr.tri:
        draw.polygon([tuple(uv[v]) for v in t], fill=255, outline=255)
    covered = np.array(mask) > 0
    A, B, C = OLD[OLD_TRI[:, 0]], OLD[OLD_TRI[:, 1]], OLD[OLD_TRI[:, 2]]
    low = np.minimum(np.minimum(A[:, 2], B[:, 2]), C[:, 2])
    cand = np.nonzero(low < H[:, 2].max() + BAND + 1.0)[0]
    A, B, C = A[cand], B[cand], C[cand]
    OUV = OLD_UV[OLD_TRI[cand]] * [Ww, Hh]
    from glb_edit import barycentric, sample
    for t in band:
        corners = uv[t]
        lo = np.floor(corners.min(0) - PAD_BAND).astype(int)
        hi = np.ceil(corners.max(0) + PAD_BAND).astype(int)
        xs, ys = np.meshgrid(np.arange(max(lo[0], 0), min(hi[0], Ww - 1) + 1), np.arange(max(lo[1], 0), min(hi[1], Hh - 1) + 1))
        pts = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], 1)
        if not len(pts) or abs((corners[1] - corners[0])[0] * (corners[2] - corners[0])[1]
                               - (corners[1] - corners[0])[1] * (corners[2] - corners[0])[0]) < 1e-9:
            continue
        bc = barycentric(pts, corners)
        inside = bc.min(1) >= -1e-6
        edge = np.min(bc * np.array([np.linalg.norm(corners[(k + 1) % 3] - corners[(k + 2) % 3]) for k in range(3)]), 1)
        x, y = pts[:, 0].astype(int), pts[:, 1].astype(int)
        pad = ~inside & (edge > -PAD_BAND * 2) & ~covered[y, x]
        use = inside | pad
        if not use.any():
            continue
        bc = np.clip(bc[use], 0, None)
        bc /= bc.sum(1, keepdims=True)
        p3 = bc @ G[t]
        h = p3[:, 2] - new_z(p3)
        w = np.clip(1 - h / BAND, 0, 1)
        src = p3.copy()
        hh = np.clip(h, 0, None) + EDGE_IN
        src[:, 2] += (offset_z(unroll(p3), hh) - (new_z(p3) + hh)) * w + EDGE_IN * w
        got = np.zeros((len(src), 2))
        for c0 in range(0, len(src), 4000):
            P = src[c0:c0 + 4000]
            bcs = closest_points(P, A, B, C)
            q = bcs[..., :1] * A[None] + bcs[..., 1:2] * B[None] + bcs[..., 2:] * C[None]
            k = np.linalg.norm(q - P[:, None, :], axis=2).argmin(1)
            got[c0:c0 + 4000] = np.einsum('nk,nkd->nd', bcs[np.arange(len(P)), k], OUV[k])
        picture[y[use], x[use]] = sample(SRC, got)[:, :picture.shape[2]]
    return len(band)


def round_hem(pr, M, picture):
    Minv = np.linalg.inv(M[:3, :3])
    def to_body(p):
        return to_blender(p @ M[:3, :3].T + M[:3, 3]) - WORLD
    def to_glb(b):
        return (to_gltf(b + WORLD) - M[:3, 3]) @ Minv.T
    G = to_body(pr.attrs['POSITION'])
    # each garment vertex's body vertex, at the garment's usual distance
    d = np.linalg.norm(G[:, None, :] - BODY[None], axis=2)
    lab, dist = d.argmin(1), d.min(1)
    near = dist < NEAR
    usual = float(np.bincount(np.round(dist[near] / 0.01).astype(int)).argmax()) * 0.01
    label = {g: int(lab[g]) for g in range(len(G)) if near[g] and abs(dist[g] - usual) < STRAY}
    held = set()
    for t in pr.tri:
        if all(int(g) in label for g in t):
            held.add(frozenset(label[int(g)] for g in t))
    # the bottom hem: the open loop round the waist
    wid = weld(G)
    count = defaultdict(int)
    for t in wid[pr.tri]:
        for k in range(3):
            count[frozenset((int(t[k]), int(t[(k + 1) % 3])))] += 1
    nxt = {}
    for t in wid[pr.tri]:
        for k in range(3):
            a, b = int(t[k]), int(t[(k + 1) % 3])
            if count[frozenset((a, b))] == 1:
                nxt[a] = b
    place_of = {}
    for g, w in enumerate(wid):
        place_of.setdefault(int(w), G[g])
    loops, seen = [], set()
    for s in nxt:
        if s in seen:
            continue
        loop, v = [], s
        while v not in seen and v in nxt:
            seen.add(v)
            loop.append(v)
            v = nxt[v]
        P3 = np.array([place_of[w] for w in loop])
        ang = np.unwrap(np.arctan2(P3[:, 0], -P3[:, 1]))
        if len(loop) >= 8 and abs(ang[-1] - ang[0]) > 5:
            loops.append((P3[:, 2].mean(), loop))
    if not loops:
        return None, 'no hem round the body'
    hem = min(loops)[1]
    H = np.array([place_of[w] for w in hem])
    A = np.c_[np.ones(len(H)), H[:, 1]]
    fit = np.ones(len(H), bool)
    for _ in range(6):
        coef = np.linalg.lstsq(A[fit], H[fit, 2], rcond=None)[0]
        res = H[:, 2] - A @ coef
        fit = res < NOTCH
    notch = int((~fit).sum())
    if notch == 0:
        return None, 'no notches in its hem (it is within %.2f of a plane)' % NOTCH
    hem_labels = set()
    for w in hem:
        hem_labels |= {label[g] for g in np.nonzero(wid == w)[0] if g in label}
    # the body vertices the garment stands over, at its own places there
    gplace = {}
    for g in range(len(G)):
        if g in label:
            gplace.setdefault(label[g], G[g])
    def gp(v):
        return gplace[v] if v in gplace else BODY[v] + BNORM[v] * usual
    PATH = None
    if not use_plane and os.path.exists(PATH_FILE):
        import json
        PATH = json.load(open(PATH_FILE))['path']
    yc = H[:, 1].mean()
    def angle(p):
        return np.arctan2(p[..., 0], -(p[..., 1] - yc))
    if PATH is None:
        n = np.array([0.0, -coef[1], 1.0])
        n /= np.linalg.norm(n)
        def side(p):    # body units above the plane, along its normal
            return ((p[..., 2] - (coef[0] + coef[1] * p[..., 1])) * n[2])
        def new_z(p):
            return coef[0] + coef[1] * p[..., 1]
        report = ['ring z = %.2f %+.3f y (hem %.2f..%.2f, %d notch vertices up to %.2f over it, usual distance %.2f)' % (
            coef[0], coef[1], H[:, 2].min(), H[:, 2].max(), notch, res.max(), usual)]
    else:
        # the learned path at this garment's own places
        PP = np.array([gp(q[1]) if q[0] == 'v' else gp(q[1]) * (1 - q[3]) + gp(q[2]) * q[3] for q in PATH])
        order = np.argsort(angle(PP))
        p_th, p_z = angle(PP)[order], PP[order, 2]
        def new_z(p):
            return np.interp(angle(p), p_th, p_z, period=2 * np.pi)
        on_path = {q[1] for q in PATH if q[0] == 'v'}
        path_edge = {}     # (a, b) a < b -> share from a
        for q in PATH:
            if q[0] == 'e':
                a, b, t = q[1], q[2], q[3]
                path_edge[(min(a, b), max(a, b))] = t if a < b else 1 - t
        SIGN = {}
        def sign(v):
            if v not in SIGN:
                if v in on_path:
                    SIGN[v] = 0
                else:
                    p = gp(v)
                    SIGN[v] = 1 if p[2] > new_z(p) else -1
            return SIGN[v]
        # an edge the path crosses has its ends either side, whatever the angle's reading says
        for (a, b) in path_edge:
            if sign(a) == sign(b):
                hi, lo = (a, b) if gp(a)[2] > gp(b)[2] else (b, a)
                SIGN[hi], SIGN[lo] = 1, -1
        def side(p):    # (only the fill uses it in this mode: above the path's height at that angle)
            return p[..., 2] - new_z(p)
        report = ['learned path of %d points (hem %.2f..%.2f, %d notch vertices, usual distance %.2f)' % (
            len(PATH), H[:, 2].min(), H[:, 2].max(), notch, usual)]

    OLD, OLD_TRI, OLD_UV = G.copy(), pr.tri.copy(), pr.attrs['TEXCOORD_0'].copy()
    SRC = picture.copy() if picture is not None else None
    # ---- the notches filled from the body's faces ----------------------------------------------------------------
    hem_body = {}       # body vertex -> the garment's place there (welded hem vertex)
    for w in hem:
        gs = [g for g in range(len(G)) if wid[g] == w and g in label]
        if gs:
            hem_body[label[gs[0]]] = place_of[w]
    def bplace(v):
        return hem_body[v] if v in hem_body else BODY[v] + BNORM[v] * usual
    start = set()
    for k in range(len(hem)):
        a, b = hem[k], hem[(k + 1) % len(hem)]
        la = next((label[g] for g in np.nonzero(wid == a)[0] if g in label), None)
        lb = next((label[g] for g in np.nonzero(wid == b)[0] if g in label), None)
        if la is not None and lb is not None:
            start |= set(B_EDGE_TRIS[frozenset((la, lb))])
    top = H[:, 2].max() + 0.5
    fill, stack = set(), [bi for bi in start]
    while stack:
        bi = stack.pop()
        t = BTRIS[bi]
        if bi in fill or frozenset(t) in held:
            continue
        P3 = np.array([bplace(v) for v in t])
        above = max(sign(v) for v in t) > 0 if PATH is not None else side(P3).max() > SNAP
        if not above or P3[:, 2].mean() > top:
            continue
        fill.add(bi)
        for k in range(3):
            stack.extend(B_EDGE_TRIS[frozenset((t[k], t[(k + 1) % 3]))])
    report.append('%d body triangles filled in' % len(fill))
    count0 = len(G)
    rows, new_tris, islands = [], [], []
    row_label = dict(label)
    near_old = {}
    old_mid = G[pr.tri].mean(1)
    for bi in sorted(fill):
        t = BTRIS[bi]
        P3 = np.array([bplace(v) for v in t])
        # wound as the garment's triangles face: outwards
        if np.cross(P3[1] - P3[0], P3[2] - P3[0]) @ BNORM[list(t)].sum(0) < 0:
            t, P3 = t[::-1], P3[::-1]
        tri = []
        for v, p in zip(t, P3):
            g0 = int(np.linalg.norm(G - p, axis=1).argmin())
            r = {'POSITION': to_glb(p), 'TEXCOORD_0': np.zeros(2),
                 'targets': [{nm: a[g0].copy() for nm, a in tg.items()} for tg in pr.targets]}
            # (the normal: the nearest old vertex's, as the rest of the garment's are the make script's; the game
            # works out its own from the triangles)
            for nm in pr.attrs:
                if nm not in r:
                    r[nm] = pr.attrs[nm][g0].copy()
            rows.append(r)
            tri.append(count0 + len(rows) - 1)
            row_label[count0 + len(rows) - 1] = v
        new_tris.append(tri)
        islands.append((tri, bi))
        # read from the garment's triangles near it, and at least its READ nearest (a long one hanging below the hem)
        far = np.linalg.norm(old_mid - P3.mean(0), axis=1)
        near_old[bi] = sorted(set(np.nonzero(far < PAINT_NEAR)[0].tolist()) | set(np.argsort(far)[:READ].tolist()))
    scale = 1.0
    if islands and picture is not None:
        pos_new = {count0 + i: r['POSITION'] for i, r in enumerate(rows)}
        scale = paint(pr, near_old, islands, picture, lambda v: pos_new[v] if v >= count0 else pr.attrs['POSITION'][v],
                      [], rows, by_distance=True)
    extend(pr, rows, new_tris, [])

    # ---- the cut ----------------------------------------------------------------------------------------------------
    G = to_body(pr.attrs['POSITION'])
    wid = weld(G)
    if PATH is None:
        s = side(G)
        s = np.where(np.abs(s) < SNAP, 0.0, s)
    else:
        # each row on the path's side of its body vertex (rows standing for none, a ruffle, are above it)
        s = np.array([float(sign(row_label[g])) if g in row_label else 1.0 for g in range(len(G))])
    cut_rows, cut_of, keep_tris = [], {}, []
    count1 = len(G)
    missing = []
    def crossing(ga, gb):
        """The row where the hem crosses edge ga-gb, blended from the lower welded vertex so both sides agree."""
        if wid[ga] > wid[gb]:
            ga, gb = gb, ga
        k = (ga, gb)
        if k not in cut_of:
            if PATH is None:
                t = s[ga] / (s[ga] - s[gb])
            else:
                a, b = row_label.get(ga), row_label.get(gb)
                e = (min(a, b), max(a, b)) if a is not None and b is not None else None
                if e not in path_edge:
                    missing.append((a, b))
                    t = 0.5
                else:
                    t = path_edge[e] if a < b else 1 - path_edge[e]
            cut_rows.append(pr.blend(ga, gb, float(t)))
            cut_of[k] = count1 + len(cut_rows) - 1
        return cut_of[k]
    dropped = split = 0
    for t in pr.tri:
        t = [int(v) for v in t]
        st = s[t]
        if (st <= 0).all():
            dropped += 1
            continue
        if (st >= 0).all():
            keep_tris.append(t)
            continue
        split += 1
        # walk the corners, keeping those above and adding a point where an edge crosses
        poly = []
        for k in range(3):
            a, b = t[k], t[(k + 1) % 3]
            if s[a] >= 0:
                poly.append(a)
            if (s[a] > 0 > s[b]) or (s[a] < 0 < s[b]):
                poly.append(crossing(a, b))
        if len(poly) == 4:
            # a quad: split along its shorter diagonal
            def at(r):
                return cut_rows[r - count1]['POSITION'] if r >= count1 else pr.attrs['POSITION'][r]
            if np.linalg.norm(at(poly[0]) - at(poly[2])) > np.linalg.norm(at(poly[1]) - at(poly[3])):
                poly = poly[1:] + poly[:1]
        for k in range(1, len(poly) - 1):
            keep_tris.append([poly[0], poly[k], poly[k + 1]])
    if missing:
        return None, 'the hem crosses %d edges the path does not (%s): relearn it for this body' % (len(missing), sorted(set(missing))[:6])
    report.append('%d triangles split at the hem, %d below it dropped' % (split, dropped))
    gone = [tuple(int(v) for v in t) for t in pr.tri]
    extend(pr, cut_rows, keep_tris, gone)

    # ---- the hem band repainted ---------------------------------------------------------------------------------------
    if SRC is not None:
        n_band = repaint_band(pr, to_body, picture, SRC, OLD, OLD_TRI, OLD_UV, H, new_z)
        report.append('%d triangles of the hem band repainted' % n_band)

    # ---- the lift ---------------------------------------------------------------------------------------------------
    if usual < LIFT_BELOW and LIFT > 0:
        G = to_body(pr.attrs['POSITION'])
        wid = weld(G)
        count = defaultdict(int)
        for t in wid[pr.tri]:
            for k in range(3):
                count[frozenset((int(t[k]), int(t[(k + 1) % 3])))] += 1
        # the new hem: the open edges' places within SNAP of its height
        first = {int(w): g for g, w in reversed(list(enumerate(wid)))}
        ring0 = {w for e, c in count.items() if c == 1 for w in e if abs(G[first[w], 2] - float(new_z(G[first[w]]))) < SNAP + 1e-6}
        ring1 = set()
        for t in wid[pr.tri]:
            for k in range(3):
                a, b = int(t[k]), int(t[(k + 1) % 3])
                if a in ring0 and b not in ring0:
                    ring1.add(b)
                if b in ring0 and a not in ring0:
                    ring1.add(a)
        # one direction per place (the copies' normals averaged), so copies stay at one place
        normal = defaultdict(lambda: np.zeros(3))
        nb = to_body(pr.attrs['POSITION'] + pr.attrs['NORMAL']) - G if 'NORMAL' in pr.attrs else None
        for g, w in enumerate(wid):
            normal[int(w)] += nb[g] if nb is not None else BNORM[int(lab[0])]
        for g, w in enumerate(wid):
            w = int(w)
            amount = LIFT if w in ring0 else LIFT / 2 if w in ring1 else 0.0
            if amount:
                G[g] = G[g] + normal[w] / np.linalg.norm(normal[w]) * amount
        pr.attrs['POSITION'] = to_glb(G)
        report.append('lifted %d hem places by %.3f and %d above them by %.3f' % (len(ring0), LIFT, len(ring1), LIFT / 2))
    else:
        report.append('not lifted (%.2f off the body)' % usual)
    if scale < 1:
        report.append('islands at %.0f%%' % (scale * 100))
    return (bool(islands)), '; '.join(report)


if learn:
    learn_path(learn)
if level:
    level_path(*level[:3], os.path.join(os.path.dirname(HERE), 'Models', 'unused', 'before_round_hem', 'BasicShirt.glb'))
if out_folder:
    os.makedirs(out_folder, exist_ok=True)
for name in names:
    path = os.path.join(GARMENTS, name + '.glb')
    j, binary = read_glb(path)
    prim = j['meshes'][0]['primitives'][0]
    pr = Prim(j, binary, 0, prim)
    M = world_matrix(j, 0)
    im = image_of(j, prim)
    picture = None
    if im is not None and 'bufferView' in j['images'][im]:
        pil = Image.open(io.BytesIO(bytes(view_bytes(j, binary, j['images'][im]['bufferView']))))
        picture = np.asarray(pil.convert('RGBA' if 'A' in pil.mode else 'RGB'), dtype=np.float32).copy()
    n0 = len(pr.tri)
    painted, what = round_hem(pr, M, picture)
    if painted is None:
        print('ROUND_HEM %-14s left alone: %s' % (name, what))
        continue
    print('ROUND_HEM %-14s %d -> %d triangles: %s' % (name, n0, len(pr.tri), what))
    if check:
        continue
    image = None
    if painted and picture is not None:
        out = io.BytesIO()
        img = Image.fromarray(np.clip(np.round(picture), 0, 255).astype(np.uint8))
        if j['images'][im].get('mimeType', 'image/png') == 'image/jpeg':
            img.save(out, 'JPEG', quality=95, subsampling=0)
        else:
            img.save(out, 'PNG')
        image = (im, out.getvalue())
    write(j, binary, pr, os.path.join(out_folder, name + '.glb'), image)
