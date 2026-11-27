"""Two-way surface distance of a mesh against the target (current body's own hand past the knuckles and its neck cap
count as target there too), by region."""
import numpy as np, trimesh, sys, zr
tg = np.load('target.npz'); TGT = trimesh.Trimesh(tg['V'], tg['T'], process=False)
cb = np.load('body.npz'); CUR = trimesh.Trimesh(cb['V'], cb['T'], process=False)
def region(p):
    x, y, z = abs(p[:, 0]), p[:, 1], p[:, 2]
    r = np.full(len(p), 'torso', dtype=object)
    r[z > 19.9] = 'neck'
    r[(x > 2.3) & (z > 14)] = 'arm'
    r[(x > 7.4) & (z > 13)] = 'hand'
    r[(z < 11.8)] = 'leg'
    r[(z < 2.2)] = 'foot'
    return r
def handmesh(p): return (abs(p[:, 0]) > 8.85) & (p[:, 2] > 13)
def measure(V, T, label):
    M = trimesh.Trimesh(V, T, process=False)
    # mesh -> target (hand fingers -> current mitten)
    a, _ = trimesh.sample.sample_surface_even(M, 40000, seed=3)
    da = np.zeros(len(a))
    h = handmesh(a)
    da[~h] = trimesh.proximity.closest_point(TGT, a[~h])[1]
    da[h] = trimesh.proximity.closest_point(CUR, a[h])[1]
    # target -> mesh (skip the sculpt's separate fingers)
    b, _ = trimesh.sample.sample_surface_even(TGT, 40000, seed=4)
    b = b[~handmesh(b)]
    db = trimesh.proximity.closest_point(M, b)[1]
    ra, rb = region(a), region(b)
    print('%-8s tris %4d | all: mean %.3f p95 %.3f max %.3f' % (label, len(T), (da.mean() + db.mean()) / 2, np.percentile(np.r_[da, db], 95), max(da.max(), db.max())), end='')
    for r in ('torso', 'neck', 'arm', 'hand', 'leg', 'foot'):
        print(' | %s %.3f' % (r, (da[ra == r].mean() + (db[rb == r].mean() if (rb == r).any() else da[ra == r].mean())) / 2), end='')
    print()
measure(cb['V'], cb['T'], 'current')
V, T, E, F = zr.load_quads(sys.argv[1] if len(sys.argv) > 1 else 'newbody.npz')
measure(V, T, 'new')
