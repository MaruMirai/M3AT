"""Makes the spade the digging stroke holds (walk.TOOL_TAKES' dig, WORK['dig'], played for sowing and clearing pollution), what Maru mimed with
the T-shaped stick in the take: an OBJ in IsoRim's tool space (model units, as IsoRim's tool_hoe.obj, the shaft up +y
from its grip's end at -0.06), with its own .mtl beside it.

    python3 anim_scripts/make_spade.py "M3AT: Maru's 3D Animated Toons/Models"

The stick's head end went into the ground, so it is the blade's end. It was held head-down, the right hand at its top:
the shaft runs from a T grip at the end (the right hand just above it) through the left hand (0.28 m on) down to the
blade, which is centred on where the stick's head was (0.60 m past the left hand, taken to model units as make_axe.py
does), so it goes a little way into the
soil at the bottom of the stroke as a spade does. The blade's width lies along the stick's crossbar, the way the take's
calibration calls the point (the model's z, both ways, as the spade is the same either way round), flat across x. GRIP
(the right fist) and OTHER (the left) are where the fists close (walk.TOOL_TAKES' dig 'grip' and 'other'; keep them the
same), TIP the blade's end up the shaft ('head') and WIDE its half width ('blade').
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_rod
from make_rod import Mesh, tube

PER_METRE = 0.66      # model units a metre of the performer's stick
END = -0.06           # the shaft's grip end
GRIP = END + 0.04 * PER_METRE     # the right fist, by the T grip
OTHER = GRIP + 0.28 * PER_METRE   # the left fist, nearer the blade
MIDDLE = OTHER + 0.60 * PER_METRE  # the blade's middle, where the stick's head was
LONG = 0.09           # the blade's half length along the shaft
TIP = MIDDLE + LONG   # its end
WIDE = 0.065          # its half width (z)
THICK = 0.006         # its half thickness (x)
SOCKET = 0.05         # the shaft's socket down onto the blade
RADIUS = 0.016
T_GRIP = 0.05         # the T grip's half width (z)

COLOURS = {
    'spade_wood': (0.58, 0.40, 0.24),
    'spade_steel': (0.52, 0.54, 0.57),
}


def blade(m, mat):
    """A flat plate from MIDDLE - LONG to TIP, slightly rounded at its end (the corners cut back)."""
    y0, y1 = MIDDLE - LONG, TIP
    cut = LONG * 0.35
    outline = [(-WIDE, y0), (WIDE, y0), (WIDE, y1 - cut), (WIDE * 0.55, y1), (-WIDE * 0.55, y1), (-WIDE, y1 - cut)]
    front = [(THICK, y, z) for z, y in outline]
    back = [(-THICK, y, z) for z, y in outline]
    n = len(outline)
    # (wound counter-clockwise seen from outside, as Mesh.tri wants)
    for i in range(1, n - 1):
        m.tri(mat, front[0], front[i + 1], front[i])
        m.tri(mat, back[0], back[i], back[i + 1])
    for i in range(n):
        j = (i + 1) % n
        m.quad(mat, front[j], back[j], back[i], front[i])


def t_grip(m, mat):
    """A short bar across the shaft's end along z."""
    r = RADIUS * 0.9
    lo, hi = (-r, END - r, -T_GRIP), (r, END + r, T_GRIP)
    make_rod.box(m, mat, lo, hi)


def spade():
    make_rod.SIDES = 8
    m = Mesh()
    tube(m, 'spade_wood', END, MIDDLE - LONG - SOCKET * 0.5, RADIUS, RADIUS)
    tube(m, 'spade_steel', MIDDLE - LONG - SOCKET, MIDDLE - LONG + 0.01, RADIUS * 1.2, RADIUS * 1.5)
    blade(m, 'spade_steel')
    t_grip(m, 'spade_wood')
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_spade.mtl'), 'w') as f:
        f.write('# M3AT spade colours (anim_scripts/make_spade.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    path = os.path.join(out, 'tool_spade.obj')
    spade().write(path, 'tool_spade', 'tool_spade.mtl')
    with open(path) as f:
        text = f.read().replace('make_rod.py', 'make_spade.py', 1)
    with open(path, 'w') as f:
        f.write(text)
    print('SPADE %s: tool_spade.obj, grip %.3f other %.3f tip %.3f wide %.3f (model units)' % (out, GRIP, OTHER, TIP, WIDE))


if __name__ == '__main__':
    main(sys.argv[1])
