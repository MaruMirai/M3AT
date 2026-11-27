"""Relaxes the generated body over its surfaces (tangential smoothing, ring vertices held in their rings' planes, then
projected back), then fits it: each vertex moved along its normal so its faces straddle the surface instead of cutting
inside it. Writes newbody.npz and newbody.obj, and prints the shape error."""
import numpy as np, trimesh, sys
from collections import defaultdict

g = np.load('gen_raw.npz', allow_pickle=True)
V = g['V'].copy(); Fp = g['F']; F = [[i for i in f if i >= 0] for f in Fp]
pp, pn, src, mid = g['plane_p'], g['plane_n'], g['src'], g['mid']
tg = np.load('target.npz'); TGT = trimesh.Trimesh(tg['V'], tg['T'], process=False)
cb = np.load('body.npz'); CUR = trimesh.Trimesh(cb['V'], cb['T'], process=False)
SURF = [TGT, CUR]
ITER = int(sys.argv[1]) if len(sys.argv) > 1 else 25
FIT = int(sys.argv[2]) if len(sys.argv) > 2 else 8

nb = defaultdict(set)
for f in F:
    for k in range(len(f)):
        a, b = f[k], f[(k + 1) % len(f)]; nb[a].add(b); nb[b].add(a)
nbl = [np.array(sorted(nb[i])) for i in range(len(V))]
NL = int(g['nleft']); MIR = g['mirror']
diag = np.zeros(len(F), int)     # for a quad: 0 splits a-c, 1 splits b-d
def triangulate():
    out = []
    for fi, f in enumerate(F):
        if len(f) == 4:
            a, b, c, d = f
            out += [[a, b, c], [a, c, d]] if diag[fi] == 0 else [[a, b, d], [b, c, d]]
        else:
            out += [[f[0], f[i], f[i + 1]] for i in range(1, len(f) - 1)]
    return np.array(out)
def choose_diagonals(V):
    """Each left quad's split by which diagonal's middle lies nearer the surface; the right quad mirrors it."""
    quads = [fi for fi in range(NL) if len(F[fi]) == 4]
    mids = np.array([[(V[F[fi][0]] + V[F[fi][2]]) / 2, (V[F[fi][1]] + V[F[fi][3]]) / 2] for fi in quads]).reshape(-1, 3)
    srcs = np.repeat([src[F[fi]].max() for fi in quads], 2)
    d = np.zeros(len(mids))
    for s in (0, 1):
        m = srcs == s
        if m.any(): d[m] = trimesh.proximity.closest_point(SURF[s], mids[m])[1]
    d = d.reshape(-1, 2)
    for k, fi in enumerate(quads):
        diag[fi] = 0 if d[k, 0] <= d[k, 1] else 1
        a, c = (F[fi][0], F[fi][2]) if diag[fi] == 0 else (F[fi][1], F[fi][3])
        rf = F[fi + NL]; pair = {MIR[a], MIR[c]}
        diag[fi + NL] = 0 if {rf[0], rf[2]} == pair else 1
tris = triangulate()

def vnormals(V):
    fn = np.cross(V[tris[:, 1]] - V[tris[:, 0]], V[tris[:, 2]] - V[tris[:, 0]])
    n = np.zeros_like(V)
    for k in range(3): np.add.at(n, tris[:, k], fn)
    return n / (np.linalg.norm(n, axis=1)[:, None] + 1e-12)

def project(V, N):
    """Each vertex to the nearest point of its surface that faces the same way (else the nearest point at all)."""
    out = V.copy()
    for s in (0, 1):
        m = np.where(src == s)[0]
        if not len(m): continue
        cp, d, ti = trimesh.proximity.closest_point(SURF[s], V[m])
        ok = (SURF[s].face_normals[ti] * N[m]).sum(1) > 0.2
        out[m[ok]] = cp[ok]
        bad = m[~ok]
        for i in bad:   # cast both ways along the normal, nearest hit
            locs, ri, _ = SURF[s].ray.intersects_location(np.array([V[i] - N[i] * 0.6]), np.array([N[i]]), multiple_hits=True)
            if len(locs):
                out[i] = locs[np.argmin(np.linalg.norm(locs - V[i], axis=1))]
            else:
                out[i] = cp[list(m).index(i)]
    return out

def symmetric(V):
    """Each vertex and its mirror image made exact mirrors (the sculpt is not quite symmetric; the rig is)."""
    M = V[MIR] * [-1, 1, 1]
    V = (V + M) / 2
    V[mid, 0] = 0
    return V

def constrain(V):
    has = np.linalg.norm(pn, axis=1) > 0
    off = ((V - pp) * pn).sum(1)
    V[has] -= pn[has] * off[has][:, None]
    V[mid, 0] = 0
    return V

names = g['names']
LOCK = np.array([str(n).split('~')[0].startswith(('fm_', 'fg_', 'fl_', 'fsole', 'ftip', 'ttip')) for n in names])
N = vnormals(V)
V = project(V, N)
for it in range(ITER):
    N = vnormals(V)
    avg = np.array([V[n].mean(0) for n in nbl])
    L = avg - V
    L -= N * (L * N).sum(1)[:, None]
    L[LOCK] = 0
    V = V + 0.45 * L
    V = constrain(V)
    V = project(V, vnormals(V))
    V = constrain(V)
V = symmetric(project(V, vnormals(V)))
choose_diagonals(V); tris = triangulate()

# ---- fit: faces straddle the surface
def samples(V):
    """Points over each triangle (barycentric grid), with the triangle and the weights."""
    bc = np.array([[a, b, 1 - a - b] for a in np.linspace(0, 1, 5) for b in np.linspace(0, 1, 5) if a + b <= 1 + 1e-9])
    return bc
BC = samples(V)
def residuals(V):
    """Signed distance (out = +) from points over each triangle to its vertices' surface, along the triangle normal."""
    fn = np.cross(V[tris[:, 1]] - V[tris[:, 0]], V[tris[:, 2]] - V[tris[:, 0]]); fn /= np.linalg.norm(fn, axis=1)[:, None]
    P = (BC[None, :, :, None] * V[tris][:, None, :, :]).sum(2)          # tri, sample, xyz
    R = np.zeros(P.shape[:2])
    ts = src[tris].max(1)
    for s in (0, 1):
        m = np.where(ts == s)[0]
        if not len(m): continue
        pts = P[m].reshape(-1, 3)
        cp, d, ti = trimesh.proximity.closest_point(SURF[s], pts)
        r = ((pts - cp) * np.repeat(fn[m], len(BC), 0)).sum(1)
        R[m] = r.reshape(len(m), len(BC))
    return R, fn
for it in range(FIT):
    R, fn = residuals(V)
    # per vertex: weighted mean residual of the samples near it (weight = its barycentric share)
    acc = np.zeros(len(V)); wsum = np.zeros(len(V))
    for k in range(3):
        w = BC[:, k][None, :] * np.ones((len(tris), 1))
        np.add.at(acc, tris[:, k], (w * R).sum(1)); np.add.at(wsum, tris[:, k], w.sum(1))
    move = -acc / np.maximum(wsum, 1e-9)
    N = vnormals(V)
    V = V + N * (0.8 * move)[:, None]
    V = symmetric(V)
    if it == FIT // 2: choose_diagonals(V); tris = triangulate()
    print('fit %d: mean signed %.4f mean abs %.4f' % (it, (R.mean()), np.abs(R).mean()))

R, fn = residuals(V)
print('final: mean signed %.4f mean |d| %.4f p95 %.4f max %.4f' % (R.mean(), np.abs(R).mean(), np.percentile(np.abs(R), 95), np.abs(R).max()))
np.savez('newbody.npz', V=V, F=Fp, tris=tris, diag=diag, names=g['names'], src=src, mid=mid, allow_pickle=True)
with open('newbody.obj', 'w') as f:
    for p in V: f.write('v %.5f %.5f %.5f\n' % tuple(p))
    for fc in F: f.write('f ' + ' '.join(str(i + 1) for i in fc) + '\n')
print('vertices %d faces %d tris %d' % (len(V), len(F), len(tris)))
