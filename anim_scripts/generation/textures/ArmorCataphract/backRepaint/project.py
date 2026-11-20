"""python3 project.py <mesh.npz from dump.py> <paint.png> <box.txt> <old texture.png> <out texture.png> [<weights.png>]

Lays a painting of the garment seen straight from behind (flat.py's camera: ortho, from +y, box.txt's middle and scale)
onto its own texture: each texel inside a triangle is placed at its point on the mesh, and where that point faces the
camera (FACE_FROM..FACE_FULL of the way) and is the front-most there (the depth image), it takes the painting's colour,
blended with the old by how far it faces. Texels the painting shows as background keep the old. The rest is untouched,
then the changed texels are grown PAD texels into the padding round the islands so mip levels don't bleed the old in."""
import sys
import numpy as np
from PIL import Image

FACE_FROM, FACE_FULL = 0.15, 0.5     # the normal's share toward the camera
import os
HARD = float(os.environ.get('HARD', '0'))   # above 0: each triangle all new from this facing, else all old
DEPTH_SLACK = 0.03                   # of the scale: how far behind the front-most a point may be and still be seen
PAD = 6

a = sys.argv[1:]
glb, paint_path, box_path, old_path, out_path = a[:5]
d = np.load(glb)
P, uv = d['P'], d['uv']
co_mid = d['mid']
mx, mz, scale, res = [float(x) for x in open(box_path).read().split()]
res = int(res)

paint = np.asarray(Image.open(paint_path).convert('RGB').resize((res, res), Image.LANCZOS)).astype(np.float32)
bg = paint[5:40, 5:40].reshape(-1, 3).mean(0)
paint_ok = np.abs(paint - bg).sum(2) > 12
old = np.asarray(Image.open(old_path).convert('RGB')).astype(np.float32)
H, W = old.shape[:2]


def screen(p):
    """Camera from +y looking toward -y: right is -x, up is z. Pixel (column, row)."""
    return np.stack([(-(p[..., 0] - mx) / scale + 0.5) * res, (0.5 - (p[..., 2] - mz) / scale) * res], -1)


def raster(pts2, n, fn):
    """Every pixel centre inside each triangle pts2 (T,3,2) of an n-by-n image: fn(t, xs, ys, barycentrics)."""
    for t in range(len(pts2)):
        q = pts2[t]
        x0, y0 = np.floor(q.min(0)).astype(int); x1, y1 = np.ceil(q.max(0)).astype(int)
        x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, n - 1), min(y1, n - 1)
        if x1 < x0 or y1 < y0:
            continue
        ys, xs = np.mgrid[y0:y1 + 1, x0:x1 + 1]
        px, py = xs + 0.5, ys + 0.5
        (ax, ay), (bx, by), (cx, cy) = q
        d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(d) < 1e-12:
            continue
        l0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / d
        l1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / d
        l2 = 1 - l0 - l1
        inside = (l0 >= -1e-4) & (l1 >= -1e-4) & (l2 >= -1e-4)
        if inside.any():
            fn(t, xs[inside], ys[inside], np.stack([l0[inside], l1[inside], l2[inside]], -1))


# The depth image: the largest y (nearest the camera) per pixel.
depth = np.full((res, res), -1e9)
S = screen(P)
def put_depth(t, xs, ys, l):
    y = l @ P[t][:, 1]
    np.maximum.at(depth, (ys, xs), y)
raster(S, res, put_depth)

# What the painting shows of the model: the mesh's own silhouette, shrunk ERODE pixels, as the painting's grey lines
# are near the background's grey and a test by colour threw them out.
ERODE = 2
paint_ok = depth > -1e8
for _ in range(ERODE):
    paint_ok = paint_ok & np.roll(paint_ok, 1, 0) & np.roll(paint_ok, -1, 0) & np.roll(paint_ok, 1, 1) & np.roll(paint_ok, -1, 1)

normals = np.cross(P[:, 1] - P[:, 0], P[:, 2] - P[:, 0])
normals /= np.linalg.norm(normals, axis=1, keepdims=True) + 1e-12
# The mesh's winding may face in: take the sign that makes most of the area face out from the middle.
mid = co_mid
if (np.einsum('ij,ij->i', normals, P.mean(1) - mid) < 0).sum() > len(P) / 2:
    normals = -normals

out = old.copy()
weight = np.zeros((H, W), np.float32)
changed = np.zeros((H, W), bool)
UV = np.stack([uv[..., 0] * W, (1 - uv[..., 1]) * H], -1)
slack = DEPTH_SLACK * scale
def put_texel(t, xs, ys, l):
    face = normals[t][1]
    w = np.clip((face - FACE_FROM) / (FACE_FULL - FACE_FROM), 0, 1)
    w = w * w * (3 - 2 * w)
    if HARD > 0:
        w = 1.0 if face >= HARD else 0.0
    if w <= 0:
        return
    p = l @ P[t]
    s = screen(p)
    sx = np.clip(s[:, 0], 0, res - 1.001); sy = np.clip(s[:, 1], 0, res - 1.001)
    ix, iy = sx.astype(int), sy.astype(int)
    seen = p[:, 1] >= depth[iy, ix] - slack
    ok = seen & paint_ok[iy, ix]
    if not ok.any():
        return
    fx, fy = (sx - ix)[:, None], (sy - iy)[:, None]
    c = (paint[iy, ix] * (1 - fx) * (1 - fy) + paint[iy, ix + 1] * fx * (1 - fy)
         + paint[iy + 1, ix] * (1 - fx) * fy + paint[iy + 1, ix + 1] * fx * fy)
    xs, ys, c = xs[ok], ys[ok], c[ok]
    out[ys, xs] = old[ys, xs] * (1 - w) + c * w
    weight[ys, xs] = w
    changed[ys, xs] = True
raster(UV, W, put_texel)

# The backs of the knees: the thigh plate overhangs the calf's, so straight from behind the painting's one band of
# ribbed suit falls across both and the calf's top, hidden under the overhang, kept the old paint (shards seen from
# above). There each texel is painted by its place on the model instead: the band's top and bottom per column of the
# painting (band.npy: rows, from the longest dark run) as heights, ribs following them, a rim round it, and plain plate
# round that where the painting was hidden or dark.
KNEE = os.environ.get('KNEE')
if KNEE:
    raw = np.load(KNEE)                                  # per column: (length, top row, bottom row)
    ok_col = raw[:, 0] >= 15
    cols = np.arange(len(raw))
    top = raw[:, 1].astype(float); bot = raw[:, 2].astype(float)
    def smooth(v, width=9):
        v = np.interp(cols, cols[ok_col], v[ok_col])
        k = np.ones(width) / width
        return np.convolve(np.pad(v, width // 2, mode='edge'), k, 'valid')
    # The ribs follow smoother edges than the band's own, so they wobble less.
    rib_zt = mz + (0.5 - smooth(top, 31) / res) * scale; rib_zb = mz + (0.5 - smooth(bot, 31) / res) * scale
    top, bot = smooth(top), smooth(bot)
    left, right = cols[ok_col].min() - 4, cols[ok_col].max() + 4
    zt = mz + (0.5 - top / res) * scale; zb = mz + (0.5 - bot / res) * scale
    MARGIN = 0.6     # model units round the band painted plain where hidden
    RIM, INK = 0.07, 0.11
    RIBS = 6            # across the band, following its top and bottom
    def knee(t, xs, ys, l):
        n = normals[t]
        if n[1] < -0.3 or (n[1] < 0.1 and n[2] < 0.3):
            return
        p = l @ P[t]
        s = screen(p)
        c = np.clip(s[:, 0], 0, res - 1).astype(int)
        inside_x = (s[:, 0] >= left) & (s[:, 0] <= right)
        z = p[:, 2]
        a, b = zb[c], zt[c]
        zone = inside_x & (z > a - MARGIN) & (z < b + MARGIN)
        if not zone.any():
            return
        xs, ys, z, a, b, p = xs[zone], ys[zone], z[zone], a[zone], b[zone], p[zone]
        sx = s[zone]
        u = (z - a) / np.maximum(b - a, 1e-6)
        band = (u >= 0) & (u <= 1)
        out_by = np.maximum(a - z, z - b)                 # how far outside the band, in model units
        c2 = c[zone]
        ru = (z - rib_zb[c2]) / np.maximum(rib_zt[c2] - rib_zb[c2], 1e-6)
        rib = np.cos(ru * RIBS * 2 * np.pi)
        suit = 72 + 22 * np.clip(rib, 0, 1) ** 3 - 26 * np.clip(-rib, 0, 1) ** 6
        col = out[ys, xs].mean(1)
        ix = np.clip(sx[:, 0], 0, res - 1).astype(int); iy = np.clip(sx[:, 1], 0, res - 1).astype(int)
        seen = p[:, 1] >= depth[iy, ix] - slack
        hidden_edge = np.where(out_by < RIM, 188.0, np.where(out_by < INK, 120.0, 248.0))
        v = np.where(band, suit, np.where(seen, col, hidden_edge))
        out[ys, xs] = v[:, None]
        changed[ys, xs] = True
    raster(UV, W, knee)

# Inside triangles at all (to know the padding).
covered = np.zeros((H, W), bool)
def cover(t, xs, ys, l):
    covered[ys, xs] = True
raster(UV, W, cover)

# Grow the changed texels into the padding only.
grow = changed.copy()
for _ in range(PAD):
    nxt = grow.copy(); acc = np.zeros_like(out); cnt = np.zeros((H, W), np.float32)
    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        sh = np.roll(np.roll(grow, dy, 0), dx, 1)
        val = np.roll(np.roll(out, dy, 0), dx, 1)
        take = sh & ~grow & ~covered
        acc[take] += val[take]; cnt[take] += 1
    new = cnt > 0
    out[new] = acc[new] / cnt[new][:, None]
    grow |= new
Image.fromarray(np.clip(out, 0, 255).astype(np.uint8)).save(out_path)
if len(a) > 5:
    Image.fromarray((weight * 255).astype(np.uint8)).save(a[5])
print('PROJECT changed %d texels, %d grown' % (changed.sum(), (grow & ~changed).sum()))
