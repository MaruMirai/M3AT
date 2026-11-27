import numpy as np, trimesh, zr
tg = np.load('target.npz'); T0 = trimesh.Trimesh(tg['V'], tg['T'], process=False)
V, T, E, F = zr.load_quads('newbody.npz'); M = trimesh.Trimesh(V, T, process=False)
def band(p): return (p[:, 2] > 18.6) & (p[:, 2] < 20.4) & (np.abs(p[:, 0]) < 2.6)
a, _ = trimesh.sample.sample_surface_even(M, 40000, seed=3); a = a[band(a)]
b, _ = trimesh.sample.sample_surface_even(T0, 40000, seed=4); b = b[band(b)]
da = trimesh.proximity.closest_point(T0, a)[1]; db = trimesh.proximity.closest_point(M, b)[1]
print('neck band: mean %.3f max %.3f' % ((da.mean() + db.mean()) / 2, max(da.max(), db.max())))
