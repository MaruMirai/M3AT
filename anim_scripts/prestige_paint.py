"""Paints gold scrollwork on an armour garment's texture for its Royalty prestige variant, after the game's Prestige
sprites (gold pauldrons and trim on the plain armour).

    python3 anim_scripts/prestige_paint.py <armour> <garment .txt> <image in> <image out> [preview.png]

<armour> picks the placements (ARMOURS: marine, recon, cataphract). The garment .txt is the export's
(garment_<Name>.txt in the mod's Models/chibi), read for its rest places, texture places and triangles; the image is
the garment's own, at any size (swap_garment_image.py takes it out of the .glb and puts the painted one back in).

A Meshy texture is cut into hundreds of small islands, so nothing is painted on the image directly: each texel's rest
place and normal on the mesh are found (the triangles drawn in texture space, a texel and a half of bleed round each so
the seams take the paint too), and the design is drawn in the body's own space, in the front (or back) projection, in
cells. It is mirrored across the figure's middle by drawing on |x|. Gold is laid under the texture's own shading, so its
panel lines and wear show through, each stroke edged with a thin dark line as the sprites ink theirs.

The preview, if asked for, is the garment from the front, the side, the back and the other side."""
import sys
import numpy as np

# Each armour's placements, in cells (game model space: x left of the figure, y up, z toward its front).
#   chest: the chest scroll's base line (y) and size, on the front plates; back: the same on the upper back (None: none).
#   pauldron: the shoulder scroll's middle (|x|, y) and size, on the front and back faces of each shoulder plate, on
#     texels further out than reach (|x|) and not facing down.
#   badge: an ellipse (x, y, half width, half height) on the front whose dark ink is gilded (the marine's eagle).
ARMOURS = {
    'marine': {'chest': (0.725, 1.0), 'back': (0.715, 1.0),
               'pauldron': ((0.235, 0.825), 1.3, 0.13),
               'badge': (-0.09, 0.79, 0.035, 0.03)},
    'recon': {'chest': (0.725, 0.95), 'back': (0.74, 1.0),
              'pauldron': ((0.225, 0.79), 1.0, 0.15)},
    'cataphract': {'chest': (0.73, 1.1), 'back': (0.74, 1.1),
                   'pauldron': ((0.25, 0.81), 1.4, 0.16)},
}

GOLD = np.array([0.80, 0.62, 0.27])       # the gold at the texture's mid grey
GOLD_HIGH = np.array([0.99, 0.90, 0.62])  # what the texture's brightest highlights take
GOLD_LOW = np.array([0.36, 0.24, 0.08])   # what its darkest lines take
BADGE_GOLD = np.array([0.85, 0.66, 0.28])
INK = np.array([0.28, 0.19, 0.07])        # the dark line round each stroke
INK_SHARE = 0.6
SOFT = 0.0012                             # cells over which a stroke's edge fades
INK_AT = 0.0012                           # cells out from the stroke's edge the ink line runs
INK_WIDTH = 0.0011                        # its half width


def load(path):
    V, T, F = [], [], []
    for line in open(path):
        if line.startswith('v '):
            V.append([float(x) for x in line.split()[1:4]])
        elif line.startswith('t '):
            T.append([float(x) for x in line.split()[1:3]])
        elif line.startswith('f '):
            F.append([int(x) for x in line.split()[1:4]])
    return np.array(V), np.array(T), np.array(F)


def bake(V, T, F, size):
    """Each texel's rest place and normal (NaN where no triangle lies). A texel inside a triangle wins over one in the
    bleed round another."""
    P = np.full((size, size, 3), np.nan)
    N = np.full((size, size, 3), np.nan)
    rank = np.zeros((size, size))
    for f in F:
        u = T[f, 0] * size
        v = (1 - T[f, 1]) * size
        n = np.cross(V[f[1]] - V[f[0]], V[f[2]] - V[f[0]])
        length = np.linalg.norm(n)
        if length == 0:
            continue
        n /= length
        x0, x1 = int(max(0, np.floor(u.min() - 1))), int(min(size - 1, np.ceil(u.max() + 1)))
        y0, y1 = int(max(0, np.floor(v.min() - 1))), int(min(size - 1, np.ceil(v.max() + 1)))
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + .5, np.arange(y0, y1 + 1) + .5)
        d = (v[1] - v[2]) * (u[0] - u[2]) + (u[2] - u[1]) * (v[0] - v[2])
        if abs(d) < 1e-12:
            continue
        l0 = ((v[1] - v[2]) * (gx - u[2]) + (u[2] - u[1]) * (gy - v[2])) / d
        l1 = ((v[2] - v[0]) * (gx - u[2]) + (u[0] - u[2]) * (gy - v[2])) / d
        l2 = 1 - l0 - l1
        eps = 1.5 / max(1e-6, np.sqrt(abs(d)))
        near = (l0 >= -eps) & (l1 >= -eps) & (l2 >= -eps)
        r = np.where((l0 >= 0) & (l1 >= 0) & (l2 >= 0), 2, 1)
        sub = rank[y0:y1 + 1, x0:x1 + 1]
        near &= r > sub
        pos = l0[..., None] * V[f[0]] + l1[..., None] * V[f[1]] + l2[..., None] * V[f[2]]
        P[y0:y1 + 1, x0:x1 + 1][near] = pos[near]
        N[y0:y1 + 1, x0:x1 + 1][near] = n
        sub[near] = r[near]
    return P, N


# --- strokes: (points, width along it as a function of the share 0..1), in cells ---

def bez(p0, p1, p2, p3, n=60):
    t = np.linspace(0, 1, n)[:, None]
    p0, p1, p2, p3 = map(np.array, (p0, p1, p2, p3))
    return (1 - t) ** 3 * p0 + 3 * (1 - t) ** 2 * t * p1 + 3 * (1 - t) * t ** 2 * p2 + t ** 3 * p3


def spiral(c, r0, r1, a0, turns, n=80):
    t = np.linspace(0, 1, n)
    a = a0 + t * turns * 2 * np.pi
    r = r0 * (r1 / r0) ** t
    return np.stack([c[0] + r * np.cos(a), c[1] + r * np.sin(a)], 1)


def taper(w0, w1):
    return lambda t: w0 + (w1 - w0) * t


def leaf_width(w):
    return lambda t: w * np.sin(np.pi * np.clip(t, 0, 1)) ** 0.8 + 0.0008


def curl_from(path, c, r_end, turns):
    """The path carried on into a spiral about c, turning the way it was already bending."""
    v = path[-1] - np.array(c)
    a = path[-1] - path[-2]
    b = np.array(c) - path[-1]
    way = np.sign(a[0] * b[1] - a[1] * b[0])
    sp = spiral(c, np.hypot(*v), r_end, np.arctan2(v[1], v[0]), turns * way)
    return np.vstack([path, sp[1:]])


def leaf(base, tip, bend):
    base, tip = np.array(base), np.array(tip)
    d = tip - base
    n = np.array([-d[1], d[0]])
    return bez(base, base + d * 0.33 + n * bend, base + d * 0.66 + n * bend, tip, 24)


def chest_strokes():
    """One side's chest scroll (x > 0), about a base line at y 0; mirrored for the other side."""
    s = []
    # the main vine: from the sternum out along the line, curling up and in at the end
    vine = bez((0.006, 0.0), (0.045, -0.008), (0.10, -0.005), (0.128, 0.022), 50)
    s.append((curl_from(vine, (0.112, 0.035), 0.004, 1.15), taper(0.0065, 0.0022)))
    # a branch up from it, curling back toward the middle
    branch = bez((0.05, -0.003), (0.05, 0.02), (0.06, 0.045), (0.08, 0.052), 30)
    s.append((curl_from(branch, (0.072, 0.038), 0.003, 0.95), taper(0.0045, 0.0018)))
    for base, tip, bend in [((0.03, -0.001), (0.022, 0.028), 0.12), ((0.085, 0.0), (0.098, -0.022), -0.15),
                            ((0.06, 0.035), (0.040, 0.052), 0.15), ((0.105, 0.004), (0.135, 0.0), 0.1)]:
        s.append((leaf(base, tip, bend), leaf_width(0.0055)))
    return s


def pauldron_strokes():
    """An ionic scroll about (0, 0), symmetric left and right."""
    s = []
    for side in (1, -1):
        # a stem out from the middle, curling down and back in at each end
        stem = bez((0, 0.004), (side * 0.03, 0.012), (side * 0.055, 0.008), (side * 0.062, -0.006), 40)
        s.append((curl_from(stem, (side * 0.048, -0.008), 0.003, 1.1), taper(0.0055, 0.0018)))
        # a tendril under it, curling up
        low = bez((side * 0.004, -0.004), (side * 0.02, -0.018), (side * 0.035, -0.022), (side * 0.042, -0.014), 30)
        s.append((curl_from(low, (side * 0.032, -0.012), 0.0025, 0.9), taper(0.004, 0.0015)))
        s.append((leaf((side * 0.022, 0.011), (side * 0.034, 0.030), -side * 0.15), leaf_width(0.0045)))
        s.append((leaf((side * 0.06, 0.0), (side * 0.078, 0.014), side * 0.12), leaf_width(0.0038)))
    # a leaf straight up from the middle, and a bud under it
    s.append((leaf((0, 0.004), (0, 0.036), 0.0), leaf_width(0.0062)))
    s.append((leaf((0, -0.002), (0, -0.024), 0.0), leaf_width(0.0042)))
    return s


def stroke_field(qx, qy, strokes):
    """The signed distance to the nearest stroke's edge (negative inside), at each query point."""
    edge = np.full(qx.shape, np.inf)
    for pts, width in strokes:
        best = np.full(qx.shape, np.inf)
        share = np.zeros(qx.shape)
        L = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(pts, axis=0), axis=1))])
        L /= L[-1]
        for i, (a, b) in enumerate(zip(pts[:-1], pts[1:])):
            ab = b - a
            t = np.clip(((qx - a[0]) * ab[0] + (qy - a[1]) * ab[1]) / max(ab @ ab, 1e-12), 0, 1)
            d = np.hypot(qx - a[0] - t * ab[0], qy - a[1] - t * ab[1])
            closer = d < best
            best = np.where(closer, d, best)
            share = np.where(closer, L[i] + (L[i + 1] - L[i]) * t, share)
        edge = np.minimum(edge, best - width(share))
    return edge


def paint(armour, V, T, F, img):
    from PIL import Image
    cfg = ARMOURS[armour]
    size = img.width
    P, N = bake(V, T, F, size)
    ok = ~np.isnan(P[..., 0])
    X, Y, Z = P[..., 0], P[..., 1], P[..., 2]
    ny, nz = N[..., 1], N[..., 2]
    ax = np.where(ok, np.abs(X), 9.0)
    front = ok & (nz > 0.2) & (Z > 0.02)
    back = ok & (nz < -0.2) & (Z < -0.02)
    gold = np.zeros((size, size))
    ink = np.zeros((size, size))

    def lay(where, edge):
        nonlocal gold, ink
        gold = np.maximum(gold, np.where(where, np.clip(-edge / SOFT + 0.5, 0, 1), 0))
        ink = np.maximum(ink, np.where(where, np.clip(1 - np.abs(edge - INK_AT) / INK_WIDTH, 0, 1), 0))

    for side, key in ((front, 'chest'), (back, 'back')):
        if cfg.get(key):
            y0, k = cfg[key]
            lay(side, stroke_field(ax / k, np.where(ok, (Y - y0) / k, 9.0), chest_strokes()) * k)
    if cfg.get('pauldron'):
        (mx, my), k, reach = cfg['pauldron']
        edge = stroke_field((ax - mx) / k, np.where(ok, (Y - my) / k, 9.0), pauldron_strokes()) * k
        for side in (ok & (nz > 0.12) & (Z > 0.0), ok & (nz < -0.12) & (Z < 0.0)):
            lay(side & (ax > reach) & (ny > -0.2), edge)

    rgb = np.asarray(img, float)[..., :3] / 255
    alpha = np.asarray(img)[..., 3:]
    lum = (rgb @ [0.299, 0.587, 0.114])[..., None]
    tone = GOLD_LOW + (GOLD - GOLD_LOW) * np.clip((lum - 0.15) / 0.7, 0, 1) \
        + (GOLD_HIGH - GOLD) * 0.55 * np.clip((lum - 0.93) / 0.07, 0, 1)
    out = rgb * (1 - gold[..., None]) + np.clip(tone, 0, 1) * gold[..., None]
    if cfg.get('badge'):
        bx, by, bw, bh = cfg['badge']
        badge = front & (np.hypot((X - bx) / bw, (Y - by) / bh) < 1) & (lum[..., 0] < 0.45)
        out = np.where(badge[..., None], BADGE_GOLD, out)
    out = out * (1 - INK_SHARE * ink[..., None]) + INK * INK_SHARE * ink[..., None]
    return Image.fromarray(np.dstack([(np.clip(out, 0, 1) * 255).astype(np.uint8), alpha]), 'RGBA')


def render(V, T, F, tex, yaw, size):
    """The mesh with its texture, looked at from the front turned yaw degrees about the vertical, lit from the eye."""
    from PIL import Image
    a = np.radians(yaw)
    R = np.array([[np.cos(a), 0, np.sin(a)], [0, 1, 0], [-np.sin(a), 0, np.cos(a)]])
    Q = V @ R.T
    lo, hi = V.min(0), V.max(0)
    span = max(hi - lo) * 1.05
    c = (lo + hi) / 2
    X = ((Q[:, 0] - c[0]) / span + 0.5) * size
    Y = (0.5 - (Q[:, 1] - c[1]) / span) * size
    img = np.zeros((size, size, 3))
    depth = np.full((size, size), -1e9)
    tx = np.asarray(tex.convert('RGB'), float) / 255
    th, tw = tx.shape[:2]
    for f in F:
        x, y = X[f], Y[f]
        n = np.cross(Q[f[1]] - Q[f[0]], Q[f[2]] - Q[f[0]])
        if np.linalg.norm(n) == 0:
            continue
        n /= np.linalg.norm(n)
        x0, x1 = int(max(0, np.floor(x.min()))), int(min(size - 1, np.ceil(x.max())))
        y0, y1 = int(max(0, np.floor(y.min()))), int(min(size - 1, np.ceil(y.max())))
        if x1 < x0 or y1 < y0:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + .5, np.arange(y0, y1 + 1) + .5)
        d = (y[1] - y[2]) * (x[0] - x[2]) + (x[2] - x[1]) * (y[0] - y[2])
        if abs(d) < 1e-12:
            continue
        l0 = ((y[1] - y[2]) * (gx - x[2]) + (x[2] - x[1]) * (gy - y[2])) / d
        l1 = ((y[2] - y[0]) * (gx - x[2]) + (x[0] - x[2]) * (gy - y[2])) / d
        l2 = 1 - l0 - l1
        z = l0 * Q[f[0], 2] + l1 * Q[f[1], 2] + l2 * Q[f[2], 2]
        sub = depth[y0:y1 + 1, x0:x1 + 1]
        m = (l0 >= 0) & (l1 >= 0) & (l2 >= 0) & (z > sub)
        uv = l0[..., None] * T[f[0]] + l1[..., None] * T[f[1]] + l2[..., None] * T[f[2]]
        u = np.clip((uv[..., 0] * tw).astype(int), 0, tw - 1)
        v = np.clip(((1 - uv[..., 1]) * th).astype(int), 0, th - 1)
        sub[m] = z[m]
        img[y0:y1 + 1, x0:x1 + 1][m] = (tx[v, u] * (0.55 + 0.45 * abs(n[2])))[m]
    return Image.fromarray((img * 255).astype(np.uint8))


if __name__ == '__main__':
    from PIL import Image
    armour, mesh, image_in, image_out = sys.argv[1:5]
    V, T, F = load(mesh)
    painted = paint(armour, V, T, F, Image.open(image_in).convert('RGBA'))
    painted.save(image_out)
    print('PAINTED %s -> %s' % (armour, image_out))
    if len(sys.argv) > 5:
        size = 400
        sheet = Image.new('RGB', (size * 4, size))
        for i, yaw in enumerate((0, 90, 180, 270)):
            sheet.paste(render(V, T, F, painted, yaw, size), (i * size, 0))
        sheet.save(sys.argv[5])
