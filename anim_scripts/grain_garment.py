"""Adds a woven cloth grain to a textured garment's image, the same size on the body everywhere: each texel's rest place is
found from its triangle and the grain sampled there triplanar (blended by the smoothed normal), so it doesn't follow the
UV islands' sizes. The grain is the pants' own (Meshy painted a clear weave on Models/garments/Pants.glb, and not on the
BasicShirt however it was asked): a patch of it, high-passed and made to tile, at the pants' texels per unit.

    python3 anim_scripts/grain_garment.py <garment.glb> <its image.png> <pants image.png> <out.png> [density [gain]]

density: grain pixels per glb unit (51.2, the pants': 2048 px times their 0.025 UV per unit). gain: its strength (1, the
pants'). Take the images out and put the result back with swap_garment_image.py (get, put). Made for the BasicShirt:
generationAssets/textures/BasicShirt/3_placed.glb to 4_grained.glb, which is Models/garments/BasicShirt.glb.
"""
import json, struct, sys
import numpy as np
from PIL import Image, ImageFilter

def load(f):
    d=open(f,'rb').read(); n=struct.unpack('<I',d[12:16])[0]; j=json.loads(d[20:20+n]); b=d[20+n+8:]
    p=j['meshes'][0]['primitives'][0]
    def acc(i,c):
        a=j['accessors'][i]; v=j['bufferViews'][a['bufferView']]
        dt={5126:np.float32,5123:np.uint16,5125:np.uint32}[a['componentType']]
        return np.frombuffer(b,dt,a['count']*c,v.get('byteOffset',0)+a.get('byteOffset',0)).reshape(-1,c).astype(np.float64)
    return (acc(p['attributes']['POSITION'],3), acc(p['attributes']['NORMAL'],3), acc(p['attributes']['TEXCOORD_0'],2),
            acc(p['indices'],1).reshape(-1,3).astype(int))

garment, base_png, pants_png, out_png = sys.argv[1:5]
density = float(sys.argv[5]) if len(sys.argv) > 5 else 51.2
gain = float(sys.argv[6]) if len(sys.argv) > 6 else 1.0
P, N, U, I = load(garment)
base = np.asarray(Image.open(base_png).convert('L'), np.float32)
H, W = base.shape
g = Image.open(pants_png).convert('L')
hp = np.asarray(g, np.float32) - np.asarray(g.filter(ImageFilter.GaussianBlur(3)), np.float32)
patch = hp[470:890, 140:330]   # the left front of the pants' upper thigh, clear of seams; patch = patch - patch.mean()
# seamless without mirroring (a mirror line showed down the front): crossfaded with itself shifted half a tile,
# its spread kept where the two are mixed
h, w_ = patch.shape
wy = np.sin(np.pi * (np.arange(h) + 0.5) / h) ** 2; wx = np.sin(np.pi * (np.arange(w_) + 0.5) / w_) ** 2
wy, wx = wy[:, None], wx[None, :]
rows = (wy * patch + (1 - wy) * np.roll(patch, h // 2, 0)) / np.sqrt(wy ** 2 + (1 - wy) ** 2)
tile = (wx * rows + (1 - wx) * np.roll(rows, w_ // 2, 1)) / np.sqrt(wx ** 2 + (1 - wx) ** 2)
th, tw = tile.shape
def sample(a, b):
    x = np.floor(a).astype(int) % tw; y = np.floor(b).astype(int) % th
    return tile[y, x]

grain = np.zeros((H, W), np.float32); have = np.zeros((H, W), bool)
px = np.c_[U[:, 0] * W, U[:, 1] * H]   # glTF v runs down the image
for t in I:
    a, b, c = px[t]
    lo = np.floor(np.minimum(np.minimum(a, b), c)).astype(int); hi = np.ceil(np.maximum(np.maximum(a, b), c)).astype(int)
    lo = np.maximum(lo, 0); hi = np.minimum(hi, [W - 1, H - 1])
    xs, ys = np.meshgrid(np.arange(lo[0], hi[0] + 1) + 0.5, np.arange(lo[1], hi[1] + 1) + 0.5)
    m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
    if abs(np.linalg.det(m)) < 1e-9: continue
    inv = np.linalg.inv(m)
    l1, l2 = inv @ np.vstack([xs.ravel() - a[0], ys.ravel() - a[1]])
    l0 = 1 - l1 - l2
    inside = (l0 >= -0.02) & (l1 >= -0.02) & (l2 >= -0.02)
    if not inside.any(): continue
    l = np.vstack([l0, l1, l2])[:, inside]
    pos = (P[t].T @ l).T * density + 97.3   # so no tile edge falls on the garment's middle
    nor = (N[t].T @ l).T; nor /= np.linalg.norm(nor, axis=1, keepdims=True)
    w = np.abs(nor) ** 4; w /= w.sum(1, keepdims=True)
    v = w[:, 0] * sample(pos[:, 1], pos[:, 2]) + w[:, 1] * sample(pos[:, 0], pos[:, 2]) + w[:, 2] * sample(pos[:, 0], pos[:, 1])
    X = xs.ravel()[inside].astype(int); Y = ys.ravel()[inside].astype(int)
    grain[Y, X] = v; have[Y, X] = True
for _ in range(6):   # into the padding round each island, so filtering at its edge finds grain too
    acc = np.zeros_like(grain); cnt = np.zeros_like(grain)
    for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
        acc += np.roll(np.where(have, grain, 0), (dy, dx), (0, 1)); cnt += np.roll(have, (dy, dx), (0, 1))
    new = ~have & (cnt > 0)
    grain[new] = acc[new] / cnt[new]; have |= new
out = np.clip(base + gain * grain, 0, 255).astype(np.uint8)
Image.fromarray(out).convert('RGB').save(out_png)
print('GRAIN covered %.0f%% of the image' % (100 * have.mean()))
