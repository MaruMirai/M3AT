import numpy as np, trimesh, sys, zr
tg = np.load('target.npz'); V0, T0 = tg['V'], tg['T']
V, T, E, F = zr.load_quads(sys.argv[1] if len(sys.argv) > 1 else 'newbody.npz')
M = trimesh.Trimesh(V, T, process=False)
pts, _ = trimesh.sample.sample_surface_even(trimesh.Trimesh(V0, T0, process=False), 60000, seed=5)
cp, d, ti = trimesh.proximity.closest_point(M, pts)
s = np.sign(((pts - cp) * M.face_normals[ti]).sum(1)) * d
fe = np.zeros(len(T)); np.maximum.at(fe, ti, np.where(s > 0, s, 0)); fi = np.zeros(len(T)); np.maximum.at(fi, ti, np.where(s < 0, -s, 0))
k = np.clip((fe - fi) / 0.15, -1, 1)
col = np.stack([200 + 55 * np.clip(k, 0, 1) - 150 * np.clip(-k, 0, 1), 200 - 150 * abs(k), 200 + 55 * np.clip(-k, 0, 1) - 150 * np.clip(k, 0, 1)], 1)
out = sys.argv[2] if len(sys.argv) > 2 else 'nb'
zr.row([zr.render(V, T, 'front', box=(-4, 4, 9.5, 15), px=70, colors=col, edge_list=E), zr.render(V, T, 'back', box=(-4, 4, 9.5, 15), px=70, colors=col, edge_list=E), zr.render(V, T, 'side', box=(-3, 3, 9.5, 15), px=70, colors=col, edge_list=E), zr.render(V, T, 'under', box=(-4, 4, -3, 3), px=60, colors=col, edge_list=E, pitch=30)]).save(out + '_errface.png')
