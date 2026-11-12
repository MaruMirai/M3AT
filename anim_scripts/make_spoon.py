"""Makes the wooden spoon the stirring clips hold (work_stir, work_stir_crouch), as make_joint.py makes the joint: an OBJ
in the game's model space (one unit = one map cell at a grown pawn's size, +y up), with its own .mtl beside it.

    python3 anim_scripts/make_spoon.py "M3AT: Maru's 3D Animated Toons/Models"

Its grip is at the origin, where the fist closes round it (walk.stir_rest puts the origin in the fist), the handle's end
up +y a little past the fist and the bowl down -y, its middle BOWL_AT below the grip (walk.STIR_REACH, keep them the
same). The bowl is flat across z, so it faces the figure's side as the fist holds it. A little thicker than a real one
against the chibi's hand, so it shows at the default zoom.
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_rod
from make_rod import Mesh, tube

TOP = 0.035           # cells the handle's end stands up past the grip
NECK = 0.145          # cells below the grip the handle meets the bowl
BOWL_AT = 0.175       # cells below the grip the bowl's middle is (walk.STIR_REACH)
BOWL_LONG = 0.034     # the bowl's half length, along the handle
BOWL_WIDE = 0.021     # its half width, across (x)
BOWL_THICK = 0.008    # its half thickness (z)
RADIUS_TOP = 0.008
RADIUS_NECK = 0.0055
RINGS = 6             # rings over the bowl, end to end
SIDES = 10

COLOURS = {
    'spoon_wood': (0.72, 0.52, 0.30),
}


def bowl(m, mat):
    """A flattened ellipsoid about BOWL_AT down the handle's line."""
    def at(i, j):
        u = math.pi * i / RINGS          # 0 at its far end .. pi at the neck's end
        v = 2 * math.pi * j / SIDES
        return (BOWL_WIDE * math.sin(u) * math.cos(v),
                -BOWL_AT - BOWL_LONG * math.cos(u),
                BOWL_THICK * math.sin(u) * math.sin(v))
    for i in range(RINGS):
        for j in range(SIDES):
            k = (j + 1) % SIDES
            a, b, c, d = at(i, j), at(i, k), at(i + 1, k), at(i + 1, j)
            # (wound d, c, b, a: counter-clockwise seen from outside, as Mesh.tri wants)
            if i == 0:
                m.tri(mat, d, c, a)
            elif i == RINGS - 1:
                m.tri(mat, c, b, a)
            else:
                m.quad(mat, d, c, b, a)


def spoon():
    make_rod.SIDES = 8
    m = Mesh()
    # The handle, a little thinner toward the bowl, run into it so no gap shows.
    tube(m, 'spoon_wood', -NECK - 0.01, TOP, RADIUS_NECK, RADIUS_TOP)
    bowl(m, 'spoon_wood')
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_spoon.mtl'), 'w') as f:
        f.write('# M3AT wooden spoon colours (anim_scripts/make_spoon.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    path = os.path.join(out, 'tool_spoon.obj')
    spoon().write(path, 'tool_spoon', 'tool_spoon.mtl')
    with open(path) as f:
        text = f.read().replace('make_rod.py', 'make_spoon.py', 1)
    with open(path, 'w') as f:
        f.write(text)
    print('SPOON %s: tool_spoon.obj (%.2f cells long)' % (out, TOP + BOWL_AT + BOWL_LONG))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else "M3AT: Maru's 3D Animated Toons/Models")
