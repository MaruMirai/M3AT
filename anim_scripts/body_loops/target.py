"""Builds the target surface for the new body: the full-poly sculpt's smooth curvature, moved by a smoothed field of
how far the current low-poly Body sits from it (Maru's edits: shoulders, shins, belly), so broad edits are kept and the
low-poly facets are not. Above NECK_Z the current body itself (the neck and its cap inside the head).
Writes target.npz (V, T, src: 0 = moved sculpt, 1 = current body)."""
import numpy as np, trimesh
from scipy.spatial import cKDTree

NECK_Z = 19.95
SIGMA = float(__import__('os').environ.get('SIGMA', 0.35))          # units (map cells / ~20 per figure): the reach of the smoothing of the offset field

b = np.load('body.npz'); f = np.load('full.npz')
cur = trimesh.Trimesh(b['V'], b['T'], process=False)

full = trimesh.Trimesh(f['V'] + [0, 0, 0.141], f['T'], process=False)

# Signed offsets, measured both ways so faces as well as corners of the low-poly count: from dense samples of the
# current body's surface to the sculpt (offset = how far out the current body is along the sculpt's normal).
pts, fi = trimesh.sample.sample_surface_even(cur, 60000, seed=1)
cp, d, tri = trimesh.proximity.closest_point(full, pts)
n = full.face_normals[tri]
sd = ((pts - cp) * n).sum(1)
samp_n = cur.face_normals[fi]

# Spread to the sculpt's vertices: Gaussian-weighted mean of samples near each vertex whose surface faces the same way.
fv = full.vertices; fn = full.vertex_normals
tree = cKDTree(pts)
off = np.zeros(len(fv)); wsum = np.zeros(len(fv))
nbrs = tree.query_ball_point(fv, 2.2 * SIGMA)
for i, js in enumerate(nbrs):
    if not js: continue
    js = np.array(js)
    dd = np.linalg.norm(pts[js] - fv[i], axis=1)
    w = np.exp(-(dd / SIGMA) ** 2 / 2) * np.clip(samp_n[js] @ fn[i], 0, 1) ** 2
    s = w.sum()
    if s > 1e-6:
        off[i] = (w * sd[js]).sum() / s; wsum[i] = s
print('offset: mean %.3f  p5 %.3f  p95 %.3f' % (off.mean(), np.percentile(off, 5), np.percentile(off, 95)))

# Light Taubin smoothing of the sculpt itself first (its brush noise is far below this mesh's scale) then the offset.
V = fv.copy()
adj = [[] for _ in range(len(V))]
for a, c in full.edges_unique:
    adj[a].append(c); adj[c].append(a)
nb_i = np.concatenate([[i] * len(a) for i, a in enumerate(adj)]).astype(int)
nb_j = np.concatenate([a for a in adj]).astype(int)
deg = np.bincount(nb_i, minlength=len(V))
def lap(X):
    s = np.zeros_like(X); np.add.at(s, nb_i, X[nb_j]); return s / deg[:, None] - X
for _ in range(10):
    V = V + 0.5 * lap(V); V = V - 0.53 * lap(V)
moved = trimesh.Trimesh(V, full.faces, process=False)
V = V + moved.vertex_normals * off[:, None]

# Keep the sculpt below the neck line (head and neck dropped), the current body above it.
keep_s = V[full.faces].mean(1)[:, 2] < NECK_Z
keep_c = cur.triangles_center[:, 2] >= NECK_Z - 0.3      # a little overlap so casts near the seam find something
TV = np.concatenate([V, cur.vertices]); TT = np.concatenate([full.faces[keep_s], cur.faces[keep_c] + len(V)])
src = np.concatenate([np.zeros(keep_s.sum(), int), np.ones(keep_c.sum(), int)])
np.savez('target.npz', V=TV, T=TT, src=src)
t = trimesh.Trimesh(TV, TT, process=False)
cp, d, _ = trimesh.proximity.closest_point(t, cur.vertices)
print('current body verts to target: mean %.3f p90 %.3f max %.3f' % (d.mean(), np.percentile(d, 90), d.max()))
