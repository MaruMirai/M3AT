"""Finds where the fist goes on a weapon sprite from its alpha alone. Coordinates: u right, v up, 0..1.
The game's copy is Source/GripFinder.cs (guns and melee only): change the two together. python3 score.py scores it
against the vanilla holds (truth.json, from truth.py), python3 modsheet.py <mod folder> <out.png> draws a mod's weapons."""
import json, sys
import numpy as np
from PIL import Image

N = 128

def mask_of(path):
    im = Image.open(path).convert('RGBA')
    a = np.asarray(im.resize((N, N), Image.BOX), dtype=np.float32)[:, :, 3]
    return a[::-1] > 128   # [row j (v up), col i]

def components(m):
    """4-connected labels of True pixels."""
    lab = np.zeros(m.shape, int); n = 0
    for j0, i0 in zip(*np.nonzero(m)):
        if lab[j0, i0]: continue
        n += 1; lab[j0, i0] = n; todo = [(j0, i0)]
        while todo:
            j, i = todo.pop()
            for jj, ii in ((j+1,i),(j-1,i),(j,i+1),(j,i-1)):
                if 0 <= jj < m.shape[0] and 0 <= ii < m.shape[1] and m[jj, ii] and not lab[jj, ii]:
                    lab[jj, ii] = n; todo.append((jj, ii))
    return lab, n

def largest(m):
    lab, n = components(m)
    if n == 0: return m
    sizes = np.bincount(lab.ravel())[1:]
    return lab == (1 + int(np.argmax(sizes)))

def hanging(m, h, i0, L):
    """The parts below row h deep enough to count, rear to front."""
    below = m.copy(); below[h:, :] = False
    lab, n = components(below)
    parts = []
    for k in range(1, n + 1):
        jj, ii = np.nonzero(lab == k)
        depth = h - jj.min()
        if depth < max(3, 0.06 * L): continue
        parts.append(dict(k=k, lo=ii.min(), hi=ii.max(), depth=depth, area=len(jj), rear=ii.min() <= i0 + 0.04 * L,
                          jj=jj, ii=ii))
    parts.sort(key=lambda p: p['lo'])
    return parts

def gun(m, info, pistol=False):
    """A gun drawn level, muzzle to the right. The body's underside is the highest cut that leaves the most separate
    parts hanging below it (stock, grip, magazine, ...). The fist on the pistol grip: the rearmost of those that is not
    the stock; else the stock's wrist (where it meets the body)."""
    m = largest(m)
    cols = np.nonzero(m.any(0))[0]
    rows = np.nonzero(m.any(1))[0]
    i0, i1 = cols[0], cols[-1]
    L = i1 - i0 + 1
    best = None
    for h in range(rows[0] + 3, rows[-1]):
        parts = hanging(m, h, i0, L)
        if any(p['hi'] - p['lo'] > 0.7 * L for p in parts):
            break   # above the body's underside: everything hangs as one
        # The most parts; between cuts with as many, where the rows widen most going up (the body's underside).
        widen = int(m[h, i0:i1+1].sum()) - int(m[h - 1, i0:i1+1].sum())
        if best is None or (len(parts), widen) >= (len(best[1]), best[2]):
            best = (h, parts, widen)
    if best is None:
        info['how'] = 'none'
        return (i0 + 0.3 * L) / N, (rows[0] + rows[-1]) / 2 / N
    base, parts, _ = best
    info['base'] = int(base)
    info['parts'] = [(round(float(p['lo']-i0)/L,2), round(float(p['hi']-i0)/L,2), int(p['depth']), bool(p['rear'])) for p in parts]
    stock = None
    if not pistol and parts and parts[0]['rear'] and (len(parts) > 1 or (parts[0]['hi'] - parts[0]['lo']) > 0.25 * L):
        stock = parts.pop(0)
    if parts and (stock is None or parts[0]['lo'] - stock['hi'] < 0.5 * L):
        g = parts[0]
        jj, ii = g['jj'], g['ii']
        # A trigger guard or a lip under the body can join the grip to what is in front of it: the grip is the rearmost
        # leg reaching more than half the part's depth down, followed up its slant to just under the body.
        cut = base - 0.5 * g['depth']
        sub = np.zeros_like(m); sub[jj, ii] = True; sub[int(np.ceil(cut)):, :] = False
        lab2, n2 = components(sub)
        legs = sorted(range(1, n2 + 1), key=lambda k: np.nonzero(lab2 == k)[1].min())
        lj, li = np.nonzero(lab2 == legs[0])
        y = base - 0.3 * g['depth']
        if lj.max() - lj.min() >= 2:
            slope, icept = np.polyfit(lj + 0.5, li + 0.5, 1)
            x = slope * y + icept
        else:
            x = li.mean() + 0.5
        narrow = li.max() - li.min() <= 0.25 * L   # a grip, not the underside of a boxy body
        info['how'] = 'grip' if ((x - i0) / L < 0.45 or pistol) and narrow else 'unsure'
        return x / N, (y + 0.5) / N
    if stock is not None:
        # No pistol grip: the stock's wrist, its thinnest column between the butt and the body.
        top = np.array([np.nonzero(m[:, i])[0][-1] if m[:, i].any() else -1 for i in range(N)])
        bot = np.array([np.nonzero(m[:, i])[0][0] if m[:, i].any() else N for i in range(N)])
        span = range(int(i0 + 0.12 * L), int(i0 + 0.35 * L))
        i = min(span, key=lambda i: (top[i] - bot[i], -i))
        # a wrist only where the stock narrows to it: thinner than the butt and than the body ahead
        butt = max(top[i] - bot[i] for i in range(i0, int(i0 + 0.1 * L) + 1))
        ahead = max(top[i] - bot[i] for i in range(i, int(i0 + 0.6 * L)))
        middle = (rows[0] + rows[-1]) / 2
        low = bot[i] <= middle   # a stock's wrist reaches into the gun's lower half; a rail along its top does not
        info['how'] = 'wrist' if top[i] - bot[i] <= min(0.85 * butt, 0.9 * ahead) and low else 'unsure'
        return (i + 0.5) / N, ((top[i] + bot[i]) / 2 + 0.5) / N
    info['how'] = 'none'
    return (i0 + 0.3 * L) / N, (base + 0.5) / N

def melee(m, info, two=False):
    """A melee weapon, its head to the right (as the game's art has them): along its long axis, the handle is the
    narrowest stretch from the left end, the guard or head where it widens. The fist on the handle, near the guard for a
    two-handed one; a long shaft by its share."""
    m = largest(m)
    jj, ii = np.nonzero(m)
    P = np.stack([ii, jj], 1).astype(float) + 0.5
    c = P.mean(0)
    w, V = np.linalg.eigh(np.cov((P - c).T))
    ax = V[:, 1] if V[0, 1] >= 0 else -V[:, 1]
    across = np.array([-ax[1], ax[0]])
    s = (P - c) @ ax; t = (P - c) @ across
    s0 = s.min(); L = int(np.ceil(s.max() - s0)) + 1
    b = np.clip((s - s0).astype(int), 0, L - 1)
    lo = np.full(L, np.inf); hi = np.full(L, -np.inf)
    np.minimum.at(lo, b, t); np.maximum.at(hi, b, t)
    width = np.where(np.isfinite(lo), hi - lo + 1, 0)
    a, z = max(2, int(0.06 * L)), max(4, int(0.45 * L))
    m0 = a + int(np.argmin(width[a:z]))
    hmin = width[m0]
    thr = hmin + max(2, 0.2 * hmin)
    k = m0
    while k > 0 and width[k - 1] <= thr: k -= 1
    e = m0
    while e < L and width[e] <= thr: e += 1
    info['handle'] = (round(k / L, 2), round(e / L, 2))
    if k > 0.2 * L:
        info['how'] = 'unsure'   # the narrowest stretch is not at the end: no handle found (a knuckle guard, a blade)
        return (c + ax * (s0 + 0.2 * L))[0] / N, (c + ax * (s0 + 0.2 * L))[1] / N
    if e > 0.45 * L:
        f = k + 0.4 * (e - k); info['how'] = 'shaft'
    else:
        f = k + (0.85 if two else 0.55) * (e - k)
        jump = width[min(L - 1, e + 1)] if e + 1 < L else 0
        info['how'] = 'hilt' if jump >= 1.3 * hmin + 2 else 'unsure'
    sel = np.abs(b - f) <= 1
    tm = (lo[int(f)] + hi[int(f)]) / 2 if np.isfinite(lo[int(f)]) else 0.0
    p = c + ax * (s0 + f + 0.5) + across * tm
    return p[0] / N, p[1] / N

def bow(m, info):
    """A bow drawn upright: the middle of the stave (the widest run across the middle row)."""
    rows = np.nonzero(m.any(1))[0]
    j = (rows[0] + rows[-1]) // 2
    best = None; i = 0
    while i < N:
        if m[j, i]:
            k = i
            while k < N and m[j, k]: k += 1
            if k - i >= 2: best = (i, k)
            i = k
        else: i += 1
    info['how'] = 'stave'
    return (best[0] + best[1]) / 2 / N, (j + 0.5) / N

def turned(m, deg):
    """The mask turned deg degrees counterclockwise about its middle (nearest pixel)."""
    if abs(deg) < 1e-3:
        return m
    t = np.radians(deg); c, s = np.cos(t), np.sin(t)
    j, i = np.mgrid[0:N, 0:N] + 0.5 - N / 2
    # each output pixel reads the input turned back
    si, sj = c * i + s * j + N / 2, -s * i + c * j + N / 2
    ok = (si >= 0) & (si < N) & (sj >= 0) & (sj < N)
    out = np.zeros_like(m)
    out[ok] = m[sj[ok].astype(int), si[ok].astype(int)]
    return out

def turn_point(u, v, deg):
    """A point (u, v) turned deg degrees counterclockwise about the middle."""
    t = np.radians(deg); c, s = np.cos(t), np.sin(t)
    x, y = u - 0.5, v - 0.5
    return 0.5 + c * x - s * y, 0.5 + s * x + c * y

def detect(path, kind, offset=0.0):
    """kind: gun, pistol, melee, melee2 (two-handed), bow. offset: the def's equippedAngleOffset; a gun's barrel runs
    offset degrees counterclockwise from the art's right (as WeaponFit.SpriteBarrelEnd reads it), so its art is turned
    level first. Melee finds its own axis."""
    m = mask_of(path); info = {}
    if kind in ('gun', 'pistol'):
        u, v = gun(turned(m, -offset), info, kind == 'pistol')
        u, v = turn_point(u, v, offset)
    elif kind == 'bow':
        u, v = bow(m, info)
    else:
        u, v = melee(m, info, kind == 'melee2')
    info.update(u=float(u), v=float(v))
    return info

def trusted(r, kind):
    """Whether a detection is sure enough to use (else the kind's hold stays): guns and melee only."""
    return r['how'] in ('grip', 'wrist', 'hilt', 'shaft') and kind not in ('pistol', 'bow')

def category(e):
    t = e['type']
    if t == 'bow': return 'bow'
    if t.startswith('melee') or e['name'].startswith('MeleeWeapon'): return 'melee2' if t == 'melee_2h' else 'melee'
    if t == 'grenade': return None
    return 'pistol' if t == 'handgun' else 'gun'

if __name__ == '__main__':
    truth = [e for e in json.load(open('truth.json')) if e['own']]
    res = {}; errs = {}
    for e in truth:
        c = category(e)
        if c is None or e['name'] == 'Pila': continue
        r = detect(e['path'], c); res[e['name']] = r
        err = np.hypot((r['u'] - e['u']) * N, (r['v'] - e['v']) * N)
        errs.setdefault({'pistol': 'gun', 'melee2': 'melee'}.get(c, c), []).append(err)
        print(f"{e['name']:28s} {c:5s} {r.get('how',''):6s} err {err:5.1f}px  {r.get('parts', r.get('handle', ''))}")
    for c, v in errs.items():
        v = np.array(v); print(c, 'n', len(v), 'median %.1f' % np.median(v), 'within 6px %d/%d' % ((v <= 6).sum(), len(v)))
    json.dump(res, open('detected.json', 'w'))
