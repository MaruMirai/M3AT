"""The Tail_Furry gene's tail made from Wolfein's (race_wolfein_tail.txt): thinner, with tufts of fur.

    python3 anim_scripts/make_fluffy_tail.py "<mod>/Models/chibi" [out.txt]

Writes tail_furry.txt in that folder (or out.txt), in the form of export_cat.py's tail. Until Nov 2026 the furry tail was
the smooth cat tail shorter and tufted (fur_tail.py), which read as a thin cat's tail; Wolfein's tail has a dog's or fox's
curve and taper, so the furry tail is that tail thinned (each vertex drawn in toward the joint chain, THIN of the way, the
root kept at least ROOT_LEAST thick where it meets the body) with tufts as fur_tail.py's: each a low wedge of three
triangles, its base across the tail and sunk a little into it, its point pulled out toward the tail's end and up off the
skin, closed behind on a point on the skin. Wolfein's pale tip (its inner group) is left out: the gene's tail is the hair's
colour. Its joints, weights and triangle winding are Wolfein's. export_cat.py leaves a tail_furry.txt made here alone (its
first line names this script), so run this again after Wolfein's tail changes. numpy only. Lengths are in cells.
"""
import math
import os
import sys

import numpy as np

THIN = 0.55            # the share of its thickness the tail keeps
ROOT_LEAST = 0.024     # cells: the least thickness kept at the root, as the cat tail's
ROOT_BARE = 0.14       # the share of the length from the root left without tufts: in the body, or close against it
TIP_BARE = 0.97        # tufts no further out than this share
GAP = 0.034            # cells: the least distance between tufts' middles
LENGTH = 0.046         # cells: a tuft's length (fur_tail.py's)
LENGTH_SPREAD = 0.2    # the share its length may be longer or shorter
TIP_LONGER = 0.5       # tufts toward the tip this share longer, so the end is a brush
LIFT = 16.0            # degrees a tuft stands up off the skin
LIFT_SPREAD = 6.0
HALF_WIDTH = 0.018     # cells: half a tuft's base
SINK = 0.004           # cells: how far a tuft's base is sunk into the tail
BACK = 0.35            # the share of its length the closing point lies behind the base
SEED = 7


def read(path):
    v, f, joints, tw = [], [], {}, {}
    for line in open(path):
        p = line.split()
        if not p:
            continue
        if p[0] == 'v':
            v.append([float(x) for x in p[1:4]])
        elif p[0] == 'f':
            f.append([int(x) for x in p[1:4]])
        elif p[0] == 'tail':
            joints[int(p[1])] = [float(x) for x in p[2:5]]
        elif p[0] == 'tw':
            tw[int(p[1])] = [(int(a), float(b)) for a, b in zip(p[2::2], p[3::2])]
    return np.array(v), np.array(f), np.array([joints[k] for k in sorted(joints)]), [tw[i] for i in range(len(v))]


def along(points, joints):
    """Each point's nearest place on the joint chain: the share of the chain's length there, the place, the way along."""
    seg = np.linalg.norm(joints[1:] - joints[:-1], axis=1)
    cum = np.r_[0, np.cumsum(seg)]
    best = np.full(len(points), np.inf)
    share = np.zeros(len(points))
    place = np.zeros_like(points)
    ahead = np.zeros_like(points)
    for i in range(len(joints) - 1):
        a, ab = joints[i], joints[i + 1] - joints[i]
        u = np.clip(((points - a) @ ab) / (ab @ ab), 0, 1)
        q = a + u[:, None] * ab
        d = np.linalg.norm(points - q, axis=1)
        m = d < best
        best[m] = d[m]
        place[m] = q[m]
        share[m] = (cum[i] + u[m] * seg[i]) / cum[-1]
        ahead[m] = ab / np.linalg.norm(ab)
    return share, place, ahead


def unit(x):
    return x / np.linalg.norm(x)


def build(v, f, joints, tw):
    share, place, _ = along(v, joints)
    off = v - place
    r = np.linalg.norm(off, axis=1)
    keep = np.maximum(r * THIN, np.minimum(r, ROOT_LEAST * (1 - np.clip(share / ROOT_BARE, 0, 1))))
    scale = np.where(r > 1e-9, keep / np.maximum(r, 1e-9), 1.0)
    v = place + off * scale[:, None]
    # The file's winding: whether (b - a) x (c - a) points out of the tail.
    mid = v[f].mean(axis=1)
    _, mid_place, _ = along(mid, joints)
    n = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])
    outward = np.sign(np.sum(np.einsum('ij,ij->i', n, mid - mid_place)))
    verts, tris, weights = list(map(np.array, v)), [list(t) for t in f], list(tw)

    def tri(a, b, c, facing):
        nn = np.cross(verts[b] - verts[a], verts[c] - verts[a])
        tris.append([a, b, c] if np.sign(nn @ facing) == outward else [a, c, b])

    def add(p, w):
        verts.append(p)
        weights.append(w)
        return len(verts) - 1

    # Places for tufts: points on the surface, by area, kept GAP apart.
    rng = np.random.default_rng(SEED)
    area = np.linalg.norm(np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]]), axis=1) / 2
    picks = rng.choice(len(f), size=4000, p=area / area.sum())
    bary = rng.random((4000, 2))
    flip = bary.sum(axis=1) > 1
    bary[flip] = 1 - bary[flip]
    pts = v[f[picks, 0]] + bary[:, :1] * (v[f[picks, 1]] - v[f[picks, 0]]) + bary[:, 1:] * (v[f[picks, 2]] - v[f[picks, 0]])
    s, q, ahead = along(pts, joints)
    chosen = []
    for k in range(len(pts)):
        if not ROOT_BARE <= s[k] <= TIP_BARE:
            continue
        if any(np.linalg.norm(pts[k] - pts[c]) < GAP for c in chosen):
            continue
        chosen.append(k)
    for k in chosen:
        c, out = pts[k], unit(pts[k] - q[k])
        go = unit(ahead[k] - out * (ahead[k] @ out))
        across = np.cross(out, go)
        near = int(np.argmin(np.linalg.norm(v - c, axis=1)))
        w = tw[near]
        length = LENGTH * (1 + LENGTH_SPREAD * (rng.random() * 2 - 1)) * (1 + TIP_LONGER * max(0.0, (s[k] - 0.6) / 0.4))
        lift = math.radians(LIFT + LIFT_SPREAD * (rng.random() * 2 - 1))
        sunk = c - out * SINK
        b1 = add(sunk + across * HALF_WIDTH, w)
        b2 = add(sunk - across * HALF_WIDTH, w)
        tip = add(c + (go * math.cos(lift) + out * math.sin(lift)) * length, w)
        back = add(sunk - go * length * BACK, w)
        tri(back, b1, tip, out - across * 0.5)
        tri(back, tip, b2, out + across * 0.5)
        tri(b1, b2, tip, go * math.sin(lift) - out * math.cos(lift))
    return np.array(verts), tris, weights, len(chosen)


def main():
    folder = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(folder, 'tail_furry.txt')
    v, f, joints, tw = read(os.path.join(folder, 'race_wolfein_tail.txt'))
    verts, tris, weights, tufts = build(v, f, joints, tw)
    with open(out, 'w') as fh:
        fh.write("# M3AT: the furry tail, made from Wolfein's tail thinner and tufted (anim_scripts/make_fluffy_tail.py).\n")
        for p in verts:
            fh.write('v %.5f %.5f %.5f\n' % tuple(p))
        for t in tris:
            fh.write('f %d %d %d\n' % tuple(t))
        for b, p in enumerate(joints):
            fh.write('tail %d %.5f %.5f %.5f\n' % ((b,) + tuple(p)))
        for i, ws in enumerate(weights):
            fh.write('tw %d %s\n' % (i, ' '.join('%d %.5f' % x for x in ws)))
    print('%s: %d vertices, %d triangles (%d tufts)' % (out, len(verts), len(tris), tufts))


if __name__ == '__main__':
    main()
