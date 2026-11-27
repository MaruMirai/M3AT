"""Miho's fox ears and tail (the Miho race, Alien_Miho) made from Wolfein's (race_wolfein_ears.txt, race_wolfein_tail.txt).

    python3 anim_scripts/make_miho_parts.py "<mod>/Models/chibi" [ears|tail ...] [--out <folder>]

Writes race_miho_ears.txt and race_miho_tail.txt in that folder (or --out), in the form of export_cat.py's ears and tail.

Miho's art (the race mod's Miho/Addon/EarLeft, EarRight and Tail pictures) has broad triangular fox ears, widest at the
base and shorter than Wolfein's tall narrow ones, standing nearly upright; and a big bushy tail, wider than the hips from
behind, about as long as the body below the neck, fattest past its middle and drawn to a point that flicks up. So:

  ears: in each ear's own frame (along its joints from root to tip, across it in the front plane, and its depth), each
        vertex's place across is scaled to a triangle's width (EAR_BASE at the root falling to the tip, EAR_POINT the
        curve), its place along by EAR_LONG, its depth by EAR_DEPTH; then the whole ear turned in about its root so it
        leans out EAR_LEAN (Wolfein's lean 25 degrees). Miho's pictures have each ear in two kinds, upright (Ear1) and
        leaning out about 31 degrees (Ear); HAR picks one per ear, so many Miho have one ear tilted. That lean is not in
        this file: the game measures each ear's picture and leans that model ear to match (WalkFigures.EarLeanOf).
  tail: its joints lengthened TAIL_LONG from the root; each vertex kept at its share along the chain, its distance from
        the chain scaled from Wolfein's thickness there to the fox's (TAIL_ROOT at the root, TAIL_MOST from TAIL_FAT_FROM
        to TAIL_FAT_TO, then down to TAIL_TIP at the end, TAIL_POINT the curve); the end's vertices drawn out along the last
        joint TAIL_TIP_OUT so it ends in a point; then tufts as make_fluffy_tail.py's, fewer and longer (TUFT_*), for the
        art's flame-edged fur.

Joints, weights, triangle winding and the tail's tip group (inner) are Wolfein's. export_cat.py writes race files only
for meshes in the .blend, so these are left alone; to edit one by hand, import it with import_race_parts.py (it is in
its PARTS), and a full export then writes it from the .blend. numpy only. Lengths are in cells.
"""
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_fluffy_tail   # noqa: E402  (its reader, its along(), its tufts)

EAR_BASE = 0.24         # cells: the ear's width at its root (Wolfein's is 0.06 at the root, 0.125 at most, a third up)
EAR_POINT = 1.1         # the width falls as (1 - share along)^this: above 1 a little concave, a fox's point
EAR_LONG = 0.88         # of Wolfein's length
EAR_DEPTH = 0.9         # of Wolfein's depth (front to back)
EAR_LEAN = 12.0         # degrees the ear leans out from upright (Wolfein's 25)
EAR_BLEND = 0.15        # the share along over which the root keeps Wolfein's width: it is in the head

TAIL_LONG = 1.18        # of Wolfein's length
TAIL_ROOT = 0.034       # cells: its thickness (from the chain) at the root, about Wolfein's
TAIL_MOST = 0.098       # at its fattest (Wolfein's 0.068)
TAIL_FAT_FROM = 0.42    # the share along where it reaches its fattest
TAIL_FAT_TO = 0.66      # and where it starts to narrow
TAIL_TIP = 0.004        # at the very end
TAIL_POINT = 1.5        # the narrowing goes as (1 - t)^this: above 1 the end is drawn to a point
TAIL_TIP_OUT = 0.05     # cells the end's vertices are drawn out along the last joint, at the very end
TAIL_TIP_FROM = 0.9     # from this share along

TUFT_GAP = 0.068        # make_fluffy_tail's GAP, LENGTH, HALF_WIDTH, LIFT for the fox tail's tufts: fewer, flatter, a light fringe
TUFT_LENGTH = 0.058
TUFT_HALF_WIDTH = 0.026
TUFT_LIFT = 11.0
TUFT_ROOT_BARE = 0.12


def unit(x):
    return x / np.linalg.norm(x)


def turn(axis, deg):
    """Rodrigues' rotation matrix about a unit axis, degrees."""
    a = unit(np.asarray(axis, float))
    t = math.radians(deg)
    k = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    return np.eye(3) + math.sin(t) * k + (1 - math.cos(t)) * (k @ k)


# Ears ---------------------------------------------------------------------------------------------------------------

def read_ears(path):
    v, f, joints, ew = [], [], {}, {}
    for line in open(path):
        p = line.split()
        if not p:
            continue
        if p[0] == 'v':
            v.append([float(x) for x in p[1:4]])
        elif p[0] == 'f':
            f.append([int(x) for x in p[1:4]])
        elif p[0] == 'ear':
            joints[(p[1], int(p[2]))] = np.array([float(x) for x in p[3:6]])
        elif p[0] == 'ew':
            ew[int(p[1])] = [(p[i], int(p[i + 1]), float(p[i + 2])) for i in range(2, len(p) - 2, 3)]
        elif p[0] not in ('#',) and not p[0].startswith('#'):
            raise SystemExit(path + ': a line this script does not carry over: ' + p[0])
    return np.array(v), f, joints, [ew[i] for i in range(len(v))]


def ear_frame(joints, side):
    n = max(k for (s, k) in joints if s == side)
    root, tip = joints[(side, 0)], joints[(side, n)]
    along = unit(tip - root)
    across = unit(np.cross(along, [0.0, 0.0, 1.0]))   # in the front plane; its sign doesn't matter (widths are scaled)
    depth = np.cross(along, across)
    return root, along, across, depth, np.linalg.norm(tip - root)


def build_ears(v, joints, ew):
    v = v.copy()
    out_joints = dict(joints)
    for side in ('L', 'R'):
        root, along, across, depth, length = ear_frame(joints, side)
        mine = np.array([i for i in range(len(v)) if ew[i][0][0] == side])
        rel = v[mine] - root
        al, ac, de = rel @ along, rel @ across, rel @ depth
        top = al.max()
        share = np.clip(al / top, 0, 1)
        # Wolfein's half width at each share along (the most either way, in bins, smoothed and kept above a floor).
        bins = np.linspace(0, 1, 11)
        half = np.array([np.abs(ac[np.abs(share - b) <= 0.08]).max() if np.any(np.abs(share - b) <= 0.08) else np.nan for b in bins])
        half = np.interp(bins, bins[~np.isnan(half)], half[~np.isnan(half)])
        half = np.convolve(np.r_[half[0], half, half[-1]], [0.25, 0.5, 0.25], 'valid')
        wolf = np.maximum(np.interp(share, bins, half), 0.01)
        fox = 0.5 * EAR_BASE * (1 - share) ** EAR_POINT
        k = fox / wolf
        k = np.where(share < EAR_BLEND, 1 + (k - 1) * np.clip(share / EAR_BLEND, 0, 1) ** 2, k)
        k = np.maximum(k, 0.15)
        lean = math.degrees(math.atan2(-along[0] if side == 'L' else along[0], along[1]))
        # Leaning out is a turn of the tip away from the middle (L's tip toward -x): turning it back in is a turn about
        # -z for L (by the right hand, +z takes +y toward -x) and +z for R.
        r = turn([0, 0, -1 if side == 'L' else 1], lean - EAR_LEAN)
        moved = al[:, None] * EAR_LONG * along + (ac * k)[:, None] * across + (de * EAR_DEPTH)[:, None] * depth
        v[mine] = root + moved @ r.T
        for (s, j), p in joints.items():
            if s == side:
                q = p - root
                out_joints[(s, j)] = root + (((q @ along) * EAR_LONG) * along + (q @ across) * across + (q @ depth) * depth) @ r.T
        now = ear_frame(out_joints, side)[1]
        now_lean = math.degrees(math.atan2(-now[0] if side == 'L' else now[0], now[1]))
        if abs(now_lean - EAR_LEAN) > 0.5:
            raise SystemExit('ears %s: turned to %.1f degrees, not %.1f' % (side, now_lean, EAR_LEAN))
        print('ears %s: lean %.1f -> %.1f degrees, length %.3f -> %.3f, width at a third up %.3f -> %.3f cells'
              % (side, lean, EAR_LEAN, top, top * EAR_LONG,
                 2 * np.interp(1 / 3, bins, half), EAR_BASE * (2 / 3) ** EAR_POINT))
    return v, out_joints


def write_ears(path, v, f, joints, ew):
    with open(path, 'w') as fh:
        fh.write("# M3AT: Miho's fox ears (Alien_Miho), made from Wolfein's broader at the base, shorter and more upright "
                 "(anim_scripts/make_miho_parts.py).\n")
        for p in v:
            fh.write('v %.5f %.5f %.5f\n' % tuple(p))
        for t in f:
            fh.write('f %d %d %d\n' % tuple(t))
        for side in ('L', 'R'):
            for j in sorted(k for (s, k) in joints if s == side):
                fh.write('ear %s %d %.5f %.5f %.5f\n' % ((side, j) + tuple(joints[(side, j)])))
        for i, ws in enumerate(ew):
            fh.write('ew %d %s\n' % (i, ' '.join('%s %d %.5f' % w for w in ws)))


# Tail ---------------------------------------------------------------------------------------------------------------

def read_inner(path):
    for line in open(path):
        p = line.split()
        if p and p[0] == 'inner':
            return [int(x) for x in p[1:]]
    return []


def fox_radius(s):
    rise = np.clip(s / TAIL_FAT_FROM, 0, 1)
    rise = rise * rise * (3 - 2 * rise)
    fall = np.clip((s - TAIL_FAT_TO) / (1 - TAIL_FAT_TO), 0, 1)
    up = TAIL_ROOT + (TAIL_MOST - TAIL_ROOT) * rise
    return np.where(s <= TAIL_FAT_TO, up, TAIL_TIP + (TAIL_MOST - TAIL_TIP) * (1 - fall) ** TAIL_POINT)


def build_tail(v, joints):
    root = joints[0]
    long_joints = root + (joints - root) * TAIL_LONG
    share, place, ahead = make_fluffy_tail.along(v, joints)
    off = v - place
    r = np.linalg.norm(off, axis=1)
    bins = np.linspace(0, 1, 21)
    wolf = np.array([r[np.abs(share - b) <= 0.05].max() if np.any(np.abs(share - b) <= 0.05) else np.nan for b in bins])
    wolf = np.interp(bins, bins[~np.isnan(wolf)], wolf[~np.isnan(wolf)])
    wolf = np.maximum(np.interp(share, bins, wolf), 0.01)
    k = fox_radius(share) / wolf
    new_place = root + (place - root) * TAIL_LONG
    out = np.clip((share - TAIL_TIP_FROM) / (1 - TAIL_TIP_FROM), 0, 1) ** 2 * TAIL_TIP_OUT
    last = unit(joints[-1] - joints[-2])
    # A cap vertex past the chain's end keeps its way out of it (its offset along the last joint), scaled as the rest.
    v = new_place + off * k[:, None] + last[None, :] * out[:, None]
    print('tail: length %.3f -> %.3f cells, thickest %.3f -> %.3f'
          % (np.linalg.norm(joints[1:] - joints[:-1], axis=1).sum(),
             np.linalg.norm(long_joints[1:] - long_joints[:-1], axis=1).sum(), wolf.max(), TAIL_MOST))
    return v, long_joints


def tufted(v, f, joints, tw):
    m = make_fluffy_tail
    saved = (m.THIN, m.ROOT_LEAST, m.GAP, m.LENGTH, m.HALF_WIDTH, m.LIFT, m.ROOT_BARE)
    try:
        m.THIN, m.ROOT_LEAST = 1.0, 0.0   # the shape as made here: only the tufts
        m.GAP, m.LENGTH, m.HALF_WIDTH, m.LIFT, m.ROOT_BARE = TUFT_GAP, TUFT_LENGTH, TUFT_HALF_WIDTH, TUFT_LIFT, TUFT_ROOT_BARE
        return m.build(v, np.array(f), joints, tw)
    finally:
        m.THIN, m.ROOT_LEAST, m.GAP, m.LENGTH, m.HALF_WIDTH, m.LIFT, m.ROOT_BARE = saved


def write_tail(path, v, f, joints, tw, inner):
    with open(path, 'w') as fh:
        fh.write("# M3AT: Miho's fox tail (Alien_Miho), made from Wolfein's longer, bushier and pointed, tufted "
                 "(anim_scripts/make_miho_parts.py).\n")
        for p in v:
            fh.write('v %.5f %.5f %.5f\n' % tuple(p))
        for t in f:
            fh.write('f %d %d %d\n' % tuple(t))
        for b, p in enumerate(joints):
            fh.write('tail %d %.5f %.5f %.5f\n' % ((b,) + tuple(p)))
        for i, ws in enumerate(tw):
            fh.write('tw %d %s\n' % (i, ' '.join('%d %.5f' % x for x in ws)))
        if inner:
            fh.write('inner %s\n' % ' '.join(str(i) for i in inner))


def main():
    args = sys.argv[1:]
    out = None
    if '--out' in args:
        k = args.index('--out')
        out = args[k + 1]
        del args[k:k + 2]
    if not args:
        raise SystemExit(__doc__)
    folder, parts = args[0], set(args[1:]) or {'ears', 'tail'}
    out = out or folder
    if 'ears' in parts:
        v, f, joints, ew = read_ears(os.path.join(folder, 'race_wolfein_ears.txt'))
        v, joints = build_ears(v, joints, ew)
        write_ears(os.path.join(out, 'race_miho_ears.txt'), v, f, joints, ew)
        print('wrote race_miho_ears.txt: %d vertices, %d triangles' % (len(v), len(f)))
    if 'tail' in parts:
        src = os.path.join(folder, 'race_wolfein_tail.txt')
        v, f, joints, tw = make_fluffy_tail.read(src)
        inner = read_inner(src)
        v, joints = build_tail(v, joints)
        verts, tris, weights, tufts = tufted(v, f, joints, tw)
        write_tail(os.path.join(out, 'race_miho_tail.txt'), verts, tris, joints, weights, inner)
        print('wrote race_miho_tail.txt: %d vertices, %d triangles (%d tufts)' % (len(verts), len(tris), tufts))


if __name__ == '__main__':
    main()
