"""Tiny orthographic z-buffer renderer: flat shading, optional edges, optional per-face colours.
render(V, T, view, ...) -> PIL image. Views: front (looking +Y), back, side (from +X), left, top, or a 3x3 rotation."""
import numpy as np
from PIL import Image, ImageDraw

VIEWS = {
    # rows: screen x, screen y (up), depth (towards viewer)
    "front": np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], float),
    "back": np.array([[-1, 0, 0], [0, 0, 1], [0, 1, 0]], float),
    "side": np.array([[0, -1, 0], [0, 0, 1], [1, 0, 0]], float),   # from +X, figure faces left
    "left": np.array([[0, 1, 0], [0, 0, 1], [-1, 0, 0]], float),
    "top": np.array([[1, 0, 0], [0, -1, 0], [0, 0, 1]], float),
    "under": np.array([[1, 0, 0], [0, 1, 0], [0, 0, -1]], float),
}


def rot(view, yaw=0, pitch=0):
    R = VIEWS[view] if isinstance(view, str) else view
    cy, sy = np.cos(np.radians(yaw)), np.sin(np.radians(yaw))
    cp, sp = np.cos(np.radians(pitch)), np.sin(np.radians(pitch))
    Yaw = np.array([[cy, -sy, 0], [sy, cy, 0], [0, 0, 1]])
    Pit = np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]])
    return Pit @ R @ Yaw


def render(V, T, view="front", box=None, px=60, edges=True, colors=None, base=(220, 180, 160), yaw=0, pitch=0,
           ecol=(30, 30, 30), bg=(70, 70, 70), cull=True, width=None, edge_list=None):
    """box = (xmin, xmax, ymin, ymax) in screen units; px = pixels per unit."""
    R = rot(view, yaw, pitch)
    P = V @ R.T
    if box is None:
        box = (P[:, 0].min() - .5, P[:, 0].max() + .5, P[:, 1].min() - .5, P[:, 1].max() + .5)
    if width is not None:
        px = width / (box[1] - box[0])
    W = int((box[1] - box[0]) * px); H = int((box[3] - box[2]) * px)
    sx = (P[:, 0] - box[0]) * px; sy = (box[3] - P[:, 1]) * px; sz = P[:, 2]
    img = np.zeros((H, W, 3)); img[:] = bg
    zb = np.full((H, W), -1e9); fid = np.full((H, W), -1)
    a, b, c = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    N = np.cross(b - a, c - a); N /= np.linalg.norm(N, axis=1)[:, None] + 1e-12
    Nv = N @ R.T
    light = np.array([0.4, 0.5, 0.75]); light /= np.linalg.norm(light)
    shade = np.clip(Nv @ light, 0, 1) * 0.7 + 0.3
    for i, (i0, i1, i2) in enumerate(T):
        if cull and Nv[i, 2] <= 0: continue
        xs = np.array([sx[i0], sx[i1], sx[i2]]); ys = np.array([sy[i0], sy[i1], sy[i2]]); zs = np.array([sz[i0], sz[i1], sz[i2]])
        x0, x1 = max(int(xs.min()), 0), min(int(xs.max()) + 1, W - 1)
        y0, y1 = max(int(ys.min()), 0), min(int(ys.max()) + 1, H - 1)
        if x0 > x1 or y0 > y1: continue
        X, Y = np.meshgrid(np.arange(x0, x1 + 1) + .5, np.arange(y0, y1 + 1) + .5)
        d = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
        if abs(d) < 1e-12: continue
        l0 = ((ys[1] - ys[2]) * (X - xs[2]) + (xs[2] - xs[1]) * (Y - ys[2])) / d
        l1 = ((ys[2] - ys[0]) * (X - xs[2]) + (xs[0] - xs[2]) * (Y - ys[2])) / d
        l2 = 1 - l0 - l1
        m = (l0 >= -1e-6) & (l1 >= -1e-6) & (l2 >= -1e-6)
        z = l0 * zs[0] + l1 * zs[1] + l2 * zs[2]
        sub = zb[y0:y1 + 1, x0:x1 + 1]
        m &= z > sub
        sub[m] = z[m]
        fid[y0:y1 + 1, x0:x1 + 1][m] = i
    vis = fid >= 0
    col = np.array(base, float)[None].repeat(len(T), 0) if colors is None else np.asarray(colors, float)
    img[vis] = col[fid[vis]] * shade[fid[vis]][:, None]
    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    if edges:
        dr = ImageDraw.Draw(im)
        EL = edge_list if edge_list is not None else [(t[k], t[(k + 1) % 3]) for i, t in enumerate(T) if not (cull and Nv[i, 2] <= 0) for k in range(3)]
        for p, q in EL:
            if True:
                # draw edge only if its midpoint is visible (approx: at or in front of zbuffer)
                vis_n = 0
                for f in (0.2, 0.35, 0.5, 0.65, 0.8):
                    mx, my = int(sx[p] + (sx[q] - sx[p]) * f), int(sy[p] + (sy[q] - sy[p]) * f)
                    mz = sz[p] + (sz[q] - sz[p]) * f
                    if 0 <= mx < W and 0 <= my < H and mz >= zb[my, mx] - 0.04: vis_n += 1
                if vis_n >= 3:
                    dr.line([(sx[p], sy[p]), (sx[q], sy[q])], fill=ecol, width=1)
    return im


def row(ims, pad=6, bg=(255, 255, 255)):
    H = max(i.height for i in ims); Wt = sum(i.width for i in ims) + pad * (len(ims) - 1)
    c = Image.new("RGB", (Wt, H), bg); x = 0
    for i in ims: c.paste(i, (x, 0)); x += i.width + pad
    return c


def quads_edges_only(T, F):
    """not used"""
    pass


def load_quads(path):
    import numpy as np
    g = np.load(path, allow_pickle=True)
    V = g['V']; F = [[i for i in f if i >= 0] for f in g['F']]
    T = g['tris'] if 'tris' in g else np.array([[f[0], f[i], f[i + 1]] for f in F for i in range(1, len(f) - 1)])
    E = sorted({tuple(sorted((f[k], f[(k + 1) % len(f)]))) for f in F for k in range(len(f))})
    return V, T, E, F
