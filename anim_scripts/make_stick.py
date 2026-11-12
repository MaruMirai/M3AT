"""Makes the roasting stick the campfire cooking clips hold out over the fire (work_stir_campfire,
work_stir_campfire_sit), as make_spoon.py makes the spoon: an OBJ in the game's model space (one unit = one map cell at a
grown pawn's size, +y up), with its own .mtl beside it.

    python3 anim_scripts/make_stick.py "M3AT: Maru's 3D Animated Toons/Models"

Its grip is at the origin, where the fist closes round it (walk.stir_rest puts the origin in the fist), the stick running
up +y to its tip LONG out, the butt BUTT behind the fist, with CHUNKS of meat skewered near the tip. A plain peeled branch
with a little bend in it and a knot or two, thicker than a real one against the chibi's hand so it shows at the default
zoom. walk.ROAST_LONG must stay LONG.
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_rod
from make_rod import Mesh

LONG = 0.72           # cells from the grip to the tip (walk.ROAST_LONG)
BUTT = 0.05           # cells the butt end stands back past the grip
RADIUS_BUTT = 0.010
RADIUS_TIP = 0.0045
BEND = 0.018          # cells the middle of the stick bows off the straight line (toward -x), as a branch does
SEGMENTS = 8          # pieces along the stick
SIDES = 7
# The meat: chunks threaded on near the tip, each a lumpy ellipsoid about the stick (cells along it from the grip, half
# length along it, half widths across).
CHUNKS = ((0.57, 0.030, 0.030, 0.026), (0.645, 0.026, 0.027, 0.029))
RINGS = 6
ROUND = 9
# A short side twig snapped off, so it reads as a branch: where along it, its length and its way out.
TWIG_AT = 0.24
TWIG_LONG = 0.035
TWIG_WAY = (0.6, 0.55, 0.3)

COLOURS = {
    'stick_wood': (0.62, 0.48, 0.32),
    'stick_meat': (0.55, 0.24, 0.16),
}


def centre(y):
    """The stick's middle line at y along it: bowed off straight by BEND, none at either end."""
    s = (y + BUTT) / (LONG + BUTT)
    return (-BEND * math.sin(math.pi * s), y, 0.0)


def ring(c, r, i, twist=0.0):
    a = 2 * math.pi * i / SIDES + twist
    return (c[0] + r * math.cos(a), c[1], c[2] - r * math.sin(a))


def stick(m, mat):
    ys = [-BUTT + (LONG + BUTT) * k / SEGMENTS for k in range(SEGMENTS + 1)]
    rs = [RADIUS_BUTT + (RADIUS_TIP - RADIUS_BUTT) * k / SEGMENTS for k in range(SEGMENTS + 1)]
    rings = [[ring(centre(y), r, i, 0.3 * k) for i in range(SIDES)] for k, (y, r) in enumerate(zip(ys, rs))]
    for k in range(SEGMENTS):
        lo, hi = rings[k], rings[k + 1]
        for i in range(SIDES):
            j = (i + 1) % SIDES
            m.quad(mat, lo[i], lo[j], hi[j], hi[i])
    for i in range(SIDES):
        j = (i + 1) % SIDES
        m.tri(mat, centre(ys[0]), rings[0][j], rings[0][i])
        m.tri(mat, centre(ys[-1]), rings[-1][i], rings[-1][j])


def twig(m, mat):
    """A short stub of a side branch: a little cone out from the stick."""
    base = centre(TWIG_AT)
    w = TWIG_WAY
    n = math.sqrt(sum(v * v for v in w))
    w = tuple(v / n for v in w)
    tip = tuple(base[k] + w[k] * TWIG_LONG for k in range(3))
    # two axes across the twig
    u = (0.0, -w[2], w[1])
    n = math.sqrt(sum(v * v for v in u))
    u = tuple(v / n for v in u)
    v = (w[1] * u[2] - w[2] * u[1], w[2] * u[0] - w[0] * u[2], w[0] * u[1] - w[1] * u[0])
    r = RADIUS_BUTT * 0.6
    pts = [tuple(base[k] + r * (math.cos(2 * math.pi * i / 5) * u[k] + math.sin(2 * math.pi * i / 5) * v[k]) for k in range(3))
           for i in range(5)]
    for i in range(5):
        j = (i + 1) % 5
        m.tri(mat, pts[i], pts[j], tip)


def chunk(m, mat, at, half, wide, deep, seed):
    """A lumpy ellipsoid about the stick's line at `at` along it."""
    c = centre(at)
    def p(i, j):
        u = math.pi * i / RINGS
        v = 2 * math.pi * j / ROUND
        lump = 1.0 + 0.12 * math.sin(3 * v + seed) * math.sin(2 * u + seed * 0.7)
        return (c[0] + wide * lump * math.sin(u) * math.cos(v),
                c[1] - half * math.cos(u),
                c[2] - deep * lump * math.sin(u) * math.sin(v))
    for i in range(RINGS):
        for j in range(ROUND):
            k = (j + 1) % ROUND
            a, b, cc, d = p(i, j), p(i, k), p(i + 1, k), p(i + 1, j)
            if i == 0:
                m.tri(mat, a, cc, d)   # (a and b are the one pole)
            elif i == RINGS - 1:
                m.tri(mat, a, b, cc)   # (cc and d are the other)
            else:
                m.quad(mat, a, b, cc, d)


def model():
    m = Mesh()
    stick(m, 'stick_wood')
    twig(m, 'stick_wood')
    for n, (at, half, wide, deep) in enumerate(CHUNKS):
        chunk(m, 'stick_meat', at, half, wide, deep, 1.3 + 2.1 * n)
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_stick.mtl'), 'w') as f:
        f.write('# M3AT roasting stick colours (anim_scripts/make_stick.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    path = os.path.join(out, 'tool_stick.obj')
    model().write(path, 'tool_stick', 'tool_stick.mtl')
    with open(path) as f:
        text = f.read().replace('make_rod.py', 'make_stick.py', 1)
    with open(path, 'w') as f:
        f.write(text)
    print('STICK %s: tool_stick.obj (%.2f cells long)' % (out, LONG + BUTT))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else "M3AT: Maru's 3D Animated Toons/Models")
