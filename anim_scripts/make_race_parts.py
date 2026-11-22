# Makes the bespoke ears and tails of the popular Humanoid Alien Races races, in place of the parts built from their
# sprites (Source/AddonBuilder.cs), as low-poly meshes in the gene ears' and tail's own form (export_cat.py's header), so
# the game moves them on the same springs (Source/RaceTails.cs, CatEars.cs, CatTail.cs):
#   python3 anim_scripts/make_race_parts.py "M3AT: Maru's 3D Animated Toons/Models/chibi" [part ...]
# Each is shaped after the race's own sprites and placed from the chibi's head in skin.txt and the cat tail's root in
# tail.txt, so run it again after the body changes. Written:
#   race_ratkin_ears.txt   Ratkin (NewRatkinPlus): big round mouse ears, cupped forward, pink lining (inner)
#   race_ratkin_tail.txt   a long, thin, bare tail, hanging back and down and curling up off the ground
#   race_wolfein_ears.txt  Wolfein: tall pointed wolf ears, cupped, a pale fluffy lining with a ragged edge
#   race_wolfein_tail.txt  a big bushy tail, narrow at the root, swelling and curving out, a pale tip (inner)
#   race_milira_wing_L.txt, race_milira_wing_R.txt   Milira: feathered wings folded on the back, riding the chest (bone line)
# Lengths are cells (the game's model space: x the figure's right, y up, z forward). Untextured: the game colours them
# with the race's add-on colour and the hair's strand grain, the lining (inner) in its own colour.
import math
import os
import sys

import numpy as np

# Ratkin's ears: a round disc, bottom on the head's top corner.
RATKIN_EAR = dict(
    radius=0.105,        # of the disc: the sprite's ears are about half the head's width across
    squash=0.92,         # its width over its height
    cup=0.035,           # how far its middle is dished back from its rim
    thick=0.018,         # at the middle, thinning to nothing at the rim
    out=48.0,            # degrees it leans out from upright, from the head's side
    face_out=22.0,       # degrees its face turns out from straight forward
    back=8.0,            # degrees it leans back
    around=58.0,         # degrees round from the top of the head (in the head's front view) its root sits
    sink=0.01,           # how far the root goes into the head
    lining=0.78,         # of the disc's radius, the pink inside
)
# Wolfein's ears: a tall triangle, pointed.
WOLF_EAR = dict(
    tall=0.28, base=0.08,    # half the base's width
    cup=0.03, thick=0.022,
    out=22.0, face_out=12.0, back=6.0, around=36.0, sink=-0.02,   # stood off the head, as the hair is up to 0.05 over it
    lining=0.72,
    ragged=0.12,         # of the width: the lining's edge, in and out in teeth
)
RATKIN_TAIL = dict(
    path=[(0.0, 0.0, 0.0), (0.0, -0.06, -0.09), (0.0, -0.2, -0.19), (0.02, -0.36, -0.27), (0.06, -0.49, -0.37),
          (0.1, -0.55, -0.5), (0.13, -0.53, -0.63), (0.15, -0.46, -0.72)],   # from the root
    radius=[(0.0, 0.03), (0.15, 0.026), (0.6, 0.014), (1.0, 0.005)],   # along it (share), cells
    sides=6, rings=20, lumps=0.0, tip_from=None,
)
WOLF_TAIL = dict(
    path=[(0.0, 0.0, 0.0), (0.0, -0.05, -0.08), (0.0, -0.16, -0.17), (0.0, -0.27, -0.25), (0.0, -0.34, -0.36),
          (0.0, -0.33, -0.48), (0.0, -0.26, -0.58), (0.0, -0.16, -0.63)],
    radius=[(0.0, 0.035), (0.12, 0.05), (0.4, 0.105), (0.65, 0.1), (0.85, 0.065), (1.0, 0.0)],
    sides=9, rings=16, lumps=0.14,    # each ring's sides in and out by this share, as fur in locks
    tip_from=0.78,       # of the way along, the pale tip
)
TAIL_BONES = 9
EAR_BONES = 3
SINK_TAIL = 0.02         # the tail's root goes this far into the body


def read_skin(path):
    verts, weights, head, centre = [], [], None, None
    for line in open(path):
        p = line.split()
        if not p:
            continue
        if p[0] == 'v':
            verts.append([float(x) for x in p[1:4]])
        elif p[0] == 'w':
            weights.append(dict(zip(p[1::2], map(float, p[2::2]))))
        elif p[0] == 'head':
            head = p[1]
        elif p[0] == 'centre':
            centre = np.array([float(x) for x in p[1:4]])
    verts = np.array(verts)
    on_head = np.array([w.get(head, 0.0) > 0.5 for w in weights])
    return verts[on_head], centre


def tail_root(path):
    for line in open(path):
        p = line.split()
        if p[:2] == ['tail', '0']:
            return np.array([float(x) for x in p[2:5]])
    raise SystemExit(path + ': no tail root')


def unit(v):
    return v / np.linalg.norm(v)


def rot(axis, deg):
    axis = unit(np.asarray(axis, float))
    a = math.radians(deg)
    x, y, z = axis
    c, s, t = math.cos(a), math.sin(a), 1 - math.cos(a)
    return np.array([[t * x * x + c, t * x * y - s * z, t * x * z + s * y],
                     [t * x * y + s * z, t * y * y + c, t * y * z - s * x],
                     [t * x * z - s * y, t * y * z + s * x, t * z * z + c]])


def surface_along(head, centre, way):
    """Where a line out from the head's middle along way leaves the head: the farthest head vertex near that line."""
    d = head - centre
    along = d @ way
    off = np.linalg.norm(d - np.outer(along, way), axis=1)
    near = (off < 0.05) & (along > 0)
    return centre + way * along[near].max()


class Mesh:
    def __init__(self):
        self.v, self.f, self.w, self.inner = [], [], [], set()

    def add(self, p, weights, inner=False):
        self.v.append(np.asarray(p, float))
        self.w.append(weights)
        if inner:
            self.inner.add(len(self.v) - 1)
        return len(self.v) - 1

    def tri(self, a, b, c, outward):
        """A triangle wound so its normal (b - a) x (c - a) points along outward, as the game's files have it."""
        if a in (b, c) or b == c:
            return
        n = np.cross(self.v[b] - self.v[a], self.v[c] - self.v[a])
        if np.linalg.norm(n) < 1e-10:
            return
        self.f.append((a, b, c) if n @ outward >= 0 else (a, c, b))


def chain_weights(t, bones):
    """Weights on a chain of bones for a place t (0 root .. 1 tip) along it: whole at a bone's middle, shared at a joint."""
    u = min(max(t * bones - 0.5, 0.0), bones - 1.0)
    k0 = min(int(u), bones - 1)
    k1 = min(k0 + 1, bones - 1)
    f = u - k0
    return [(k0, 1.0)] if k1 == k0 or f < 0.01 else [(k0, 1 - f), (k1, f)]


def ear(m, side, outline, s, head, centre, rows=7, cols=6):
    """One ear (side -1 the figure's left, +1 its right) as a lens: a front sheet dished back by cup and a back sheet
    behind it, meeting at the rim, its outline the half width at each height up it (0 .. 1). Returns its joints."""
    sx = float(side)
    # Its root: round from the head's top toward that side, on the head.
    way = rot((0, 0, 1), -sx * s['around']) @ np.array([0.0, 1.0, 0.0])
    root = surface_along(head, centre, unit(way)) - unit(way) * s['sink']
    up = rot((0, 0, 1), -sx * s['out']) @ np.array([0.0, 1.0, 0.0])
    up = rot((1, 0, 0), -s['back']) @ up
    face = rot((0, 1, 0), sx * s['face_out']) @ np.array([0.0, 0.0, 1.0])
    face = unit(face - up * (face @ up))
    across = np.cross(up, face) * sx   # out from the head
    tall = s['tall']
    grid = {}
    for i in range(rows + 1):
        v = i / rows
        hw = outline(v)
        for j in range(cols + 1):
            u = -1 + 2 * j / cols
            for back in (0, 1):
                rim = j in (0, cols) or hw < 1e-6
                if back and rim:
                    grid[i, j, 1] = grid[i, j, 0]
                    continue
                thick = s['thick'] * math.sqrt(max(0.0, 1 - u * u)) * math.sin(math.pi * min(1.0, v + 0.15)) ** 0.5
                dish = -s['cup'] * (1 - u * u) * math.sin(math.pi * v) ** 0.7
                w = dish + (thick / 2 if not back else -thick / 2)
                p = root + up * (v * tall) + across * (u * hw) + face * w
                if hw < 1e-6 and (i, 0, 0) in grid:
                    grid[i, j, back] = grid[i, 0, 0]
                    continue
                lining = not back and abs(u) < s['lining'] * s.get('edge', lambda v, u: 1.0)(v, u) and 0.08 < v < 0.94
                grid[i, j, back] = m.add(p, [(('L' if sx < 0 else 'R'), b, wt) for b, wt in chain_weights(v, EAR_BONES)], lining)
    for i in range(rows):
        for j in range(cols):
            for back in (0, 1):
                a, b, c, d = grid[i, j, back], grid[i, j + 1, back], grid[i + 1, j + 1, back], grid[i + 1, j, back]
                out = face if not back else -face
                m.tri(a, b, c, out)
                m.tri(a, c, d, out)
    # The open base (a wolf ear's), closed across inside the head.
    if outline(0.0) > 1e-6:
        for j in range(cols):
            a, b, c, d = grid[0, j, 0], grid[0, j + 1, 0], grid[0, j + 1, 1], grid[0, j, 1]
            m.tri(a, b, c, -up)
            m.tri(a, c, d, -up)
    return [root + up * (tall * k / EAR_BONES) for k in range(EAR_BONES + 1)]


def write_ears(path, title, s, outline, head, centre):
    m = Mesh()
    joints = {}
    for side, name in ((-1, 'L'), (1, 'R')):
        joints[name] = ear(m, side, outline, s, head, centre)
    with open(path, 'w') as f:
        f.write('# M3AT: %s, made by anim_scripts/make_race_parts.py (in the form of export_cat.py\'s ears).\n' % title)
        for p in m.v:
            f.write('v %.5f %.5f %.5f\n' % tuple(p))
        for t in m.f:
            f.write('f %d %d %d\n' % t)
        for name in ('L', 'R'):
            for b, p in enumerate(joints[name]):
                f.write('ear %s %d %.5f %.5f %.5f\n' % ((name, b) + tuple(p)))
        for i, ws in enumerate(m.w):
            f.write('ew %d %s\n' % (i, ' '.join('%s %d %.5f' % w for w in ws)))
        if m.inner:
            f.write('inner %s\n' % ' '.join(str(i) for i in sorted(m.inner)))
    print('EARS %s: %d vertices, %d triangles' % (os.path.basename(path), len(m.v), len(m.f)))


def catmull(points, t):
    """A smooth curve through points (Catmull-Rom), t 0 .. 1 by segment."""
    p = [points[0]] + list(points) + [points[-1]]
    n = len(points) - 1
    x = min(t * n, n - 1e-9)
    k = int(x)
    u = x - k
    p0, p1, p2, p3 = (np.asarray(p[k + i], float) for i in range(4))
    return 0.5 * ((2 * p1) + (-p0 + p2) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (-p0 + 3 * p1 - 3 * p2 + p3) * u ** 3)


def radius_at(table, t):
    for (a, ra), (b, rb) in zip(table, table[1:]):
        if t <= b:
            return ra + (rb - ra) * (t - a) / (b - a)
    return table[-1][1]


def write_tail(path, title, s, root):
    # The path evenly by length, from a fine sampling.
    fine = [catmull(s['path'], k / 400) for k in range(401)]
    length = np.concatenate([[0], np.cumsum([np.linalg.norm(b - a) for a, b in zip(fine, fine[1:])])])
    total = length[-1]

    def at(t):
        d = t * total
        k = min(np.searchsorted(length, d), 400)
        k0 = max(k - 1, 0)
        span = length[k] - length[k0]
        f = (d - length[k0]) / span if span > 0 else 0
        return fine[k0] + (fine[k] - fine[k0]) * f

    rings = s['rings']
    centres = [at(i / rings) for i in range(rings + 1)]
    m = Mesh()
    sides = s['sides']
    rng = np.random.default_rng(7)
    side_ref = np.array([1.0, 0.0, 0.0])
    ring_ids = []
    for i in range(rings + 1):
        t = i / rings
        c = centres[i]
        ahead = unit(centres[min(i + 1, rings)] - centres[max(i - 1, 0)])
        a = unit(side_ref - ahead * (side_ref @ ahead))
        b = np.cross(ahead, a)
        r = radius_at(s['radius'], t)
        pos = root + c + ahead * 0.0
        if i == 0:
            pos = pos + ahead * -SINK_TAIL
        inner = s['tip_from'] is not None and t >= s['tip_from']
        if r < 1e-6:
            ring_ids.append([m.add(pos, chain_weights(t, TAIL_BONES), inner)] * sides)
            continue
        ids = []
        for k in range(sides):
            ang = 2 * math.pi * (k + 0.5 * (i % 2)) / sides
            lump = 1 + s['lumps'] * (rng.random() * 2 - 1)
            p = pos + (a * math.cos(ang) + b * math.sin(ang)) * r * lump
            ids.append(m.add(p, chain_weights(t, TAIL_BONES), inner))
        ring_ids.append(ids)
    for i in range(rings):
        mid = root + (centres[i] + centres[i + 1]) / 2
        for k in range(sides):
            a, b = ring_ids[i][k], ring_ids[i][(k + 1) % sides]
            c, d = ring_ids[i + 1][(k + 1) % sides], ring_ids[i + 1][k]
            out = (m.v[a] + m.v[b] + m.v[c] + m.v[d]) / 4 - mid
            m.tri(a, b, c, out)
            m.tri(a, c, d, out)
    # The root closed, inside the body.
    cap = m.add(root + centres[0] - unit(centres[1] - centres[0]) * SINK_TAIL * 1.5, chain_weights(0.0, TAIL_BONES))
    back = -unit(centres[1] - centres[0])
    for k in range(sides):
        m.tri(cap, ring_ids[0][k], ring_ids[0][(k + 1) % sides], back)
    joints = [root + at(k / TAIL_BONES) for k in range(TAIL_BONES + 1)]
    with open(path, 'w') as f:
        f.write('# M3AT: %s, made by anim_scripts/make_race_parts.py (in the form of export_cat.py\'s tail).\n' % title)
        for p in m.v:
            f.write('v %.5f %.5f %.5f\n' % tuple(p))
        for t in m.f:
            f.write('f %d %d %d\n' % t)
        for b, p in enumerate(joints):
            f.write('tail %d %.5f %.5f %.5f\n' % ((b,) + tuple(p)))
        for i, ws in enumerate(m.w):
            f.write('tw %d %s\n' % (i, ' '.join('%d %.5f' % w for w in ws)))
        if m.inner:
            f.write('inner %s\n' % ' '.join(str(i) for i in sorted(m.inner)))
    print('TAIL %s: %d vertices, %d triangles, %.2f cells long' % (os.path.basename(path), len(m.v), len(m.f), total))


# Milira's wings, folded on the back, after its sprites: from the shoulder blade up and out to an arched wrist beside the
# head, then the long feathers hanging down to below the hips, their tips in scallops. An outline in the wing's own plane
# (a out from the back's middle, b up from the root, cells), the leading edge and the trailing edge each from the root to
# the tip, the sheet between them.
MILIRA_WING = dict(
    root=(0.045, 0.765, -0.07),   # the left wing's mirrored: x out, y up, z (back is -z)
    lead=[(0.0, 0.0), (0.08, 0.14), (0.18, 0.22), (0.25, 0.2), (0.28, 0.09), (0.28, -0.08), (0.25, -0.24), (0.21, -0.32)],
    trail=[(0.0, 0.0), (0.03, -0.06), (0.06, -0.12), (0.1, -0.19), (0.13, -0.25), (0.17, -0.3), (0.21, -0.32)],
    sweep=40.0,      # degrees the wing's plane turns back from the figure's side-to-side, so it stands off the back
    tilt=18.0,       # degrees its top leans back
    thick=0.03,      # at the leading edge, thinning to the trailing edge
    feathers=7,      # scallops along the trailing edge
    scallop=0.03,    # how deep each is
    coverts=0.45,    # of the way across from the leading edge, the shorter feathers' paler band (inner)
    rows=14, across=4,
)
WING_BONES = 3


def write_wing(path, title, s, side):
    """One wing (side -1 the figure's left), a chain from the root up over the wrist and down to the tip, riding the chest
    (a bone line), turned the other way round for the left (a mirror line, which the springs' side swing takes)."""
    sx = float(side)
    root = np.array(s['root'], float) * np.array([sx, 1.0, 1.0])
    out = rot((0, 1, 0), sx * s['sweep']) @ np.array([sx, 0.0, 0.0])   # swept back (-z)
    up = rot(out, sx * s['tilt']) @ np.array([0.0, 1.0, 0.0])
    up = unit(up - out * (up @ out))
    normal = np.cross(out, up) * sx   # out of the wing's back (away from the body)
    if normal[2] > 0:
        normal = -normal

    def curve(points, t):
        return catmull([np.array(q, float) for q in points], t)

    m = Mesh()
    rows, across = s['rows'], s['across']
    grid = {}
    for i in range(rows + 1):
        t = i / rows
        lead, trail = curve(s['lead'], t), curve(s['trail'], t)
        # The scallops: the trailing edge pulled in between feather tips.
        notch = s['scallop'] * abs(math.sin(math.pi * t * s['feathers'])) * min(1.0, t * 4)
        to_lead = lead - trail
        if np.linalg.norm(to_lead) > 1e-6:
            trail = trail + to_lead / np.linalg.norm(to_lead) * notch
        for j in range(across + 1):
            u = j / across   # 0 the leading edge, 1 the trailing
            q = lead + (trail - lead) * u
            thick = s['thick'] * (1 - u) ** 0.7 * math.sin(math.pi * min(1.0, 0.1 + t)) ** 0.5
            for back in (0, 1):
                edge = j == across or (i in (0, rows))
                if back and edge:
                    grid[i, j, 1] = grid[i, j, 0]
                    continue
                p = root + out * q[0] + up * q[1] + normal * (thick / 2 if not back else -thick / 2)
                if (i in (0, rows)) and j > 0:
                    grid[i, j, back] = grid[i, 0, 0]   # the root and the tip are points
                    continue
                inner = u > s['coverts']
                grid[i, j, back] = m.add(p, chain_weights(t, WING_BONES), inner)
    for i in range(rows):
        for j in range(across):
            for back in (0, 1):
                a, b, c, d = grid[i, j, back], grid[i, j + 1, back], grid[i + 1, j + 1, back], grid[i + 1, j, back]
                w = normal if not back else -normal
                m.tri(a, b, c, w)
                m.tri(a, c, d, w)
    # The leading edge closed between the two sheets.
    for i in range(rows):
        a, b, c, d = grid[i, 0, 0], grid[i + 1, 0, 0], grid[i + 1, 0, 1], grid[i, 0, 1]
        lead = curve(s['lead'], (i + 0.5) / rows) - curve(s['trail'], (i + 0.5) / rows)
        w = out * lead[0] + up * lead[1]
        m.tri(a, b, c, w)
        m.tri(a, c, d, w)
    mids = [root + out * q[0] + up * q[1] for q in ((curve(s['lead'], k / WING_BONES) + curve(s['trail'], k / WING_BONES)) / 2 for k in range(WING_BONES + 1))]
    mids[0] = root
    with open(path, 'w') as f:
        f.write("# M3AT: %s, made by anim_scripts/make_race_parts.py (in the form of export_cat.py's tail, on the chest).\n" % title)
        f.write('bone spine.003\n')
        f.write('mirror %d\n' % (-1 if sx < 0 else 1))
        f.write('plain\n')   # feathers: no hair strand grain
        for p in m.v:
            f.write('v %.5f %.5f %.5f\n' % tuple(p))
        for t in m.f:
            f.write('f %d %d %d\n' % t)
        for b, p in enumerate(mids):
            f.write('tail %d %.5f %.5f %.5f\n' % ((b,) + tuple(p)))
        for i, ws in enumerate(m.w):
            f.write('tw %d %s\n' % (i, ' '.join('%d %.5f' % w for w in ws)))
        if m.inner:
            f.write('inner %s\n' % ' '.join(str(i) for i in sorted(m.inner)))
    print('WING %s: %d vertices, %d triangles' % (os.path.basename(path), len(m.v), len(m.f)))


def main():
    args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else sys.argv[1:]
    if not args:
        raise SystemExit(__doc__ if __doc__ else 'give the chibi folder')
    folder, wanted = args[0], set(args[1:])
    head, centre = read_skin(os.path.join(folder, 'skin.txt'))
    root = tail_root(os.path.join(folder, 'tail.txt'))

    def want(name):
        return not wanted or name in wanted

    if want('race_ratkin_ears'):
        s = dict(RATKIN_EAR, tall=2 * RATKIN_EAR['radius'])
        write_ears(os.path.join(folder, 'race_ratkin_ears.txt'), "Ratkin's ears", s,
                   lambda v: RATKIN_EAR['radius'] * RATKIN_EAR['squash'] * math.sqrt(max(0.0, 1 - (2 * v - 1) ** 2)), head, centre)
    if want('race_wolfein_ears'):
        teeth = WOLF_EAR['ragged']
        s = dict(WOLF_EAR, edge=lambda v, u: 1 - teeth * (0.5 + 0.5 * math.sin(v * 31.0)))
        write_ears(os.path.join(folder, 'race_wolfein_ears.txt'), "Wolfein's ears", s,
                   lambda v: WOLF_EAR['base'] * (1 - v) ** 0.85 * (1 + 0.25 * math.sin(math.pi * v)), head, centre)
    if want('race_ratkin_tail'):
        write_tail(os.path.join(folder, 'race_ratkin_tail.txt'), "Ratkin's tail", RATKIN_TAIL, root)
    if want('race_wolfein_tail'):
        write_tail(os.path.join(folder, 'race_wolfein_tail.txt'), "Wolfein's tail", WOLF_TAIL, root)
    if want('race_milira_wings'):
        write_wing(os.path.join(folder, 'race_milira_wing_L.txt'), "Milira's left wing", MILIRA_WING, -1)
        write_wing(os.path.join(folder, 'race_milira_wing_R.txt'), "Milira's right wing", MILIRA_WING, 1)


if __name__ == '__main__':
    main()
