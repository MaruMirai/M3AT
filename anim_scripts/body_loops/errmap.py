import numpy as np, trimesh, sys, zr
tg = np.load('target.npz'); V0, T0 = tg['V'], tg['T']
V, T, E, F = zr.load_quads(sys.argv[1] if len(sys.argv) > 1 else 'newbody.npz')
M = trimesh.Trimesh(V, T, process=False)
c = V0[T0].mean(1)
cp, d, ti = trimesh.proximity.closest_point(M, c)
s = np.sign(((c - cp) * M.face_normals[ti]).sum(1)) * d     # + = target outside the new mesh (mesh too small)
k = np.clip(s / 0.15, -1, 1)
col = np.stack([200 + 55 * np.clip(k, 0, 1) - 150 * np.clip(-k, 0, 1), 200 - 150 * abs(k), 200 + 55 * np.clip(-k, 0, 1) - 150 * np.clip(k, 0, 1)], 1)
zr.row([zr.render(V0, T0, v, width=450, colors=col, edges=False) for v in ('front', 'side', 'back')]).save((sys.argv[2] if len(sys.argv) > 2 else 'nb') + '_err.png')
