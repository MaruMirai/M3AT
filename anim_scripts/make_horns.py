# Makes the horns for the Headbone_MiniHorns and Headbone_CenterHorn genes (Source/Horns.cs) from the head in skin.txt.
#   python3 anim_scripts/make_horns.py "M3AT: Maru's 3D Animated Toons/Models/chibi"
# export_frames.py calls write() too, so a full export brings them up to date.
#
# Each horn is rings of SIDES round a curved line, narrowing to a point, its root sunk SINK into the skull so the head's
# keys can't lift it off the skin. The line starts at the place HORNS names, a way round (`az`, degrees toward the figure's
# left, one on each side with `pair`) and up (`el`) from the middle of the head, straight out of the head (or at `rise`
# degrees up and `swing` round), and turns toward `curve`. IsoRim's camera looks down at 55 degrees by default, so a horn
# near that from a pawn facing south is seen end on: the center horn stands well above it, the small ones go out to the
# sides first. Lengths are cells from the skin, on the grown figure's head, and are longer than the sprites would make
# them: built hair stands up to 0.05 cells off the forehead (0.03 for nine styles in ten), and a horn has to come through
# that with something left to see. `Clearance` lines print how much it does against any hair given as further arguments.
#
# horns_<name>.txt (the game's model space, riding the head bone as hair does, untextured):
#   v x y z, f a b c       a vertex and a triangle, as the garments have them
#   horn x y z first n     a horn's root on the skin, which it is fitted to the shape's head by, and its n vertices
#   root i ...             the vertices at its root, coloured darker
import math
import os
import sys

import numpy as np

HORNS = {
    # Two short ones, set wide, out to the sides and curving up.
    'mini': dict(pair=True, az=40.0, el=56.0, swing=75.0, rise=45.0, length=0.17, radius=0.036, curve=(0.1, 1.0, -0.1), bend=1.0),
    # One long one in the middle of the forehead, sweeping up and a little back.
    'center': dict(pair=False, az=0.0, el=38.0, rise=62.0, length=0.27, radius=0.052, curve=(0.0, 1.0, -0.25), bend=1.0),
}
SIDES = 8           # round each ring
RINGS = 6           # along it, not counting the point
SINK = 0.035        # cells the root is inside the skull
TAPER = 0.85        # how quickly it narrows: a ring's radius is the root's times (1 - share along) ** this
ROOT_RINGS = 2      # rings from the root coloured darker, which fade out over the next one


def read_head(path):
    """The body's vertices and triangles from skin.txt, and the middle of the head."""
    verts, tris, centre = [], [], None
    with open(path) as f:
        for line in f:
            p = line.split()
            if not p:
                continue
            if p[0] == 'v':
                verts.append([float(x) for x in p[1:4]])
            elif p[0] == 'f':
                tris.append([int(x) for x in p[1:4]])
            elif p[0] == 'centre':
                centre = np.array([float(x) for x in p[1:4]])
    return np.array(verts), np.array(tris), centre


def ray_hits(start, way, verts, tris):
    """How far along way from start each triangle it passes through is, in units of way."""
    a, b, c = verts[tris[:, 0]], verts[tris[:, 1]], verts[tris[:, 2]]
    ab, ac = b - a, c - a
    h = np.cross(np.broadcast_to(way, ac.shape), ac)
    det = (ab * h).sum(1)
    near = np.abs(det) < 1e-9
    det[near] = 1.0
    s = start - a
    u = (s * h).sum(1) / det
    q = np.cross(s, ab)
    v = (q * way).sum(1) / det
    t = (ac * q).sum(1) / det
    return t[~near & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 0)]


def line_of(root, sunk, out, spec, sign):
    """The horn's centre line, RINGS + 1 places from inside the skull (sunk: the way back in) to the tip: out, turning toward curve."""
    curve = np.array(spec['curve'], dtype=float)
    curve[0] *= sign
    curve /= np.linalg.norm(curve)
    step = spec['length'] / RINGS
    start = root - sunk * SINK
    line = [start]
    place = root.copy()
    for k in range(RINGS):
        way = out + (curve - out) * spec['bend'] * (k + 0.5) / RINGS
        place = place + way / np.linalg.norm(way) * step
        line.append(place)
    return np.array(line)


def build_horn(line, radius):
    """Rings round the line and a point at its end; the root's end capped. Vertices, triangles, the root's vertices."""
    verts, tris = [], []
    n = len(line) - 1
    last = (line[1] - line[0]) / np.linalg.norm(line[1] - line[0])
    side = np.cross(last, (0.0, 0.0, 1.0))
    if np.linalg.norm(side) < 0.1:
        side = np.cross(last, (1.0, 0.0, 0.0))
    side /= np.linalg.norm(side)
    for k in range(n):
        way = line[k + 1] - line[max(k - 1, 0)]
        way /= np.linalg.norm(way)
        # The ring's frame carried along the line, so the sides don't twist.
        side = side - way * np.dot(side, way)
        side /= np.linalg.norm(side)
        other = np.cross(way, side)
        r = radius * (1.0 - k / n) ** TAPER
        for s in range(SIDES):
            a = 2.0 * math.pi * s / SIDES
            verts.append(line[k] + (side * math.cos(a) + other * math.sin(a)) * r)
    verts.append(line[n])      # the point
    verts.append(line[0])      # the cap's middle
    tip, cap = n * SIDES, n * SIDES + 1
    for k in range(n):
        for s in range(SIDES):
            a, b = k * SIDES + s, k * SIDES + (s + 1) % SIDES
            if k + 1 < n:
                c, d = a + SIDES, b + SIDES
                tris += [(a, b, d), (a, d, c)]
            else:
                tris.append((a, b, tip))
    for s in range(SIDES):
        tris.append((cap, (s + 1) % SIDES, s))
    root = list(range(ROOT_RINGS * SIDES)) + [cap]
    return np.array(verts), tris, root


def horns_of(spec, verts, tris, centre):
    """Each horn of a HORNS entry: (root on the skin, centre line, vertices, triangles, its root's vertices)."""
    out = []
    for sign in ((1.0, -1.0) if spec['pair'] else (1.0,)):
        az, el = math.radians(spec['az']) * sign, math.radians(spec['el'])
        way = np.array([math.sin(az) * math.cos(el), math.sin(el), math.cos(az) * math.cos(el)])
        hits = ray_hits(centre, way, verts, tris)
        if len(hits) == 0:
            raise SystemExit('make_horns: no skin at az %.0f el %.0f' % (spec['az'] * sign, spec['el']))
        root = centre + way * hits.max()
        rise = math.radians(spec.get('rise', spec['el']))
        swing = math.radians(spec.get('swing', spec['az'])) * sign
        start = np.array([math.sin(swing) * math.cos(rise), math.sin(rise), math.cos(swing) * math.cos(rise)])
        line = line_of(root, way, start, spec, sign)
        v, t, r = build_horn(line, spec['radius'])
        out.append((root, line, v, t, r))
    return out


def write_horns(path, name, horns):
    with open(path, 'w') as f:
        f.write('# M3AT: the %s horns, from the head in skin.txt (anim_scripts/make_horns.py).\n' % name)
        first = 0
        lines = []
        for root, _, v, t, r in horns:
            for p in v:
                f.write('v %.5f %.5f %.5f\n' % tuple(p))
            for a, b, c in t:
                f.write('f %d %d %d\n' % (first + a, first + b, first + c))
            lines.append('horn %.5f %.5f %.5f %d %d\n' % (root[0], root[1], root[2], first, len(v)))
            lines.append('root %s\n' % ' '.join(str(first + i) for i in r))
            first += len(v)
        f.writelines(lines)


def read_hair(path):
    verts, tris = [], []
    with open(path) as f:
        for line in f:
            if line.startswith('v '):
                verts.append([float(x) for x in line.split()[1:4]])
            elif line.startswith('f '):
                tris.append([int(x) for x in line.split()[1:4]])
    return np.array(verts), np.array(tris)


def showing(line, hair_verts, hair_tris):
    """Cells of the centre line past the last place it passes through this hair, and all of it from inside the skull."""
    total = 0.0
    seen = 0.0
    for k in range(len(line) - 1):
        a, b = line[k], line[k + 1]
        way = b - a
        length = float(np.linalg.norm(way))
        hits = ray_hits(a, way, hair_verts, hair_tris)
        hits = hits[hits <= 1.0]
        seen = length * (1.0 - float(hits.max())) if len(hits) else seen + length
        total += length
    return seen, total


def clearance(name, horns, hair_paths):
    results = []
    for path in hair_paths:
        v, t = read_hair(path)
        results.append(min(showing(h[1], v, t)[0] for h in horns))
    total = showing(horns[0][1], np.zeros((3, 3)), np.array([[0, 1, 2]]))[1]
    results = np.array(results)
    print('Clearance %s: %.3f cells from inside the skull to the tip; showing through %d styles: least %.3f, tenth %.3f, median %.3f'
          % (name, total, len(results), results.min(), np.percentile(results, 10), np.median(results)))


def write(out, hair_paths=()):
    """Writes horns_<name>.txt into out, from the skin.txt there."""
    verts, tris, centre = read_head(os.path.join(out, 'skin.txt'))
    for name, spec in HORNS.items():
        horns = horns_of(spec, verts, tris, centre)
        write_horns(os.path.join(out, 'horns_%s.txt' % name), name, horns)
        print('HORNS %s: %d vertices, %d triangles' % (name, sum(len(h[2]) for h in horns), sum(len(h[3]) for h in horns)))
        if hair_paths:
            clearance(name, horns, hair_paths)


if __name__ == '__main__':
    write(sys.argv[1], sys.argv[2:])
