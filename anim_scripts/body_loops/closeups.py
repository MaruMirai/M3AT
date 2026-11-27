"""Close-ups of the old body against the new (newbody.npz) where Maru looks: underarms, neck, wrist, chest; plain and
wire. Writes cu_arm.png, cu_neck.png, cu_wrist.png, cu_chest.png (each pair old | new, plain row over wire row)."""
import numpy as np, zr
from PIL import Image
b = np.load('body.npz'); h = np.load('head.npz')
nV, nT, E, F = zr.load_quads('newbody.npz')
def wh(V, T): return np.r_[V, h['V']], np.r_[T, h['T'] + len(V)]
oV, oT = wh(b['V'], b['T']); NV, NT = wh(nV, nT)
oE = sorted({tuple(sorted((t[k], t[(k + 1) % 3]))) for t in b['T'] for k in range(3)})
SETS = {
    'arm': [('front', (0.5, 4, 15.5, 19.5), -25, -25), ('side', (-2, 2, 15.5, 19.5), 0, 0), ('back', (-4, -0.5, 15.5, 19.5), 20, -20)],
    'neck': [('front', (-2.2, 2.2, 18.3, 21.3), 0, 0), ('side', (-2.2, 2.2, 18.3, 21.3), 0, 0), ('back', (-2.2, 2.2, 18.3, 21.3), 0, 0)],
    'wrist': [('front', (6, 9.5, 13.8, 17), 0, -50), ('front', (6, 9.5, 13.8, 17), 0, 0), ('side', (-1.5, 2.5, 13.8, 17), 90, 0)],
    'chest': [('front', (-2.5, 2.5, 15, 19.5), 0, 0), ('side', (-2.5, 2.5, 15, 19.5), 0, 0), ('front', (-2.5, 2.5, 15, 19.5), -40, 0)],
}
for name, shots in SETS.items():
    rows = []
    for wire in (False, True):
        r = []
        for v, box, yaw, pitch in shots:
            r.append(zr.render(oV, oT, v, box=box, width=280, yaw=yaw, pitch=pitch, edges=wire, edge_list=oE if wire else None))
            r.append(zr.render(NV, NT, v, box=box, width=280, yaw=yaw, pitch=pitch, edges=wire, edge_list=E if wire else None))
        rows.append(zr.row(r))
    out = Image.new('RGB', (rows[0].width, rows[0].height + rows[1].height + 6), 'white')
    out.paste(rows[0], (0, 0)); out.paste(rows[1], (0, rows[0].height + 6)); out.save('cu_%s.png' % name)
