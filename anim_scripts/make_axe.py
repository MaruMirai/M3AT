"""Makes the felling axe the woodchop stroke holds (walk.TOOL_TAKES' axe, WORK['axe']), shaped after the T-shaped stick
Maru swung in the take, in place of IsoRim's shorter tool_axe: an OBJ in IsoRim's tool space (model units, as IsoRim's
tool_axe.obj, the handle up +y from its end at -0.06, the blade toward -z), with its own .mtl beside it.

    python3 anim_scripts/make_axe.py "M3AT: Maru's 3D Animated Toons/Models"

The stick, measured from a photo of the rig: the crossbar (the head) 0.60 m above the right hand, the left hand 0.28 m
below the right, the stick's end 0.04 m below the left. Taken to model units through the chibi's arms (about 1.06 leg
lengths a metre of the performer's arm, walk's WORK scale 0.8 making 1.61 leg lengths a model unit: 0.66 model units a
metre), so the fists are spaced as the performer's hands were, the head a little nearer them than the crossbar was (which
looked long). GRIP and OTHER are
where the fists close (walk.TOOL_TAKES' axe 'grip' and 'other'; keep them the same), HEAD the blade's middle up the
handle ('head') and BLADE how far the edge stands out from the handle ('blade').
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_rod
from make_rod import Mesh, tube

PER_METRE = 0.66      # model units a metre of the performer's stick
END = -0.06           # the handle's end
OTHER = END + 0.04 * PER_METRE    # the left fist
GRIP = OTHER + 0.28 * PER_METRE   # the right fist
HEAD = GRIP + 0.50 * PER_METRE    # the head's middle: nearer the hands than the stick's crossbar (0.60 m), which made the
                                  # handle look long
TOP = HEAD + 0.035    # the handle's top, through the head
RADIUS = 0.016        # the handle, swelling a little at the end so it doesn't slip
RADIUS_END = 0.02
BLADE = 0.11          # the edge, out from the handle's middle (-z)
POLL = 0.03           # the back of the head (+z)
EYE_HIGH = 0.026      # the head's half height at the eye, along the handle
EDGE_HIGH = 0.055     # and at the edge, its corners swept up and down
EYE_THICK = 0.016     # the head's half thickness at the eye (x)
EDGE_THICK = 0.003    # and at the edge

COLOURS = {
    'axe_wood': (0.58, 0.40, 0.24),
    'axe_steel': (0.55, 0.57, 0.60),
}


def head(m, mat):
    """The axe's head about HEAD up the handle: a wedge from the poll (+z) out to the edge (-z), thinning and widening
    toward it, as a box of eight corners at each of three stations (poll, eye, edge)."""
    stations = [(POLL, EYE_HIGH * 0.9, EYE_THICK), (0.0, EYE_HIGH, EYE_THICK),
                (-BLADE * 0.55, EYE_HIGH * 1.15, EYE_THICK * 0.6), (-BLADE, EDGE_HIGH, EDGE_THICK)]
    rings = []
    for z, high, thick in stations:
        rings.append([(-thick, HEAD - high, z), (thick, HEAD - high, z), (thick, HEAD + high, z), (-thick, HEAD + high, z)])
    # (wound counter-clockwise seen from outside, as Mesh.tri wants)
    for a, b in zip(rings, rings[1:]):
        for i in range(4):
            j = (i + 1) % 4
            m.quad(mat, b[i], b[j], a[j], a[i])
    first, last = rings[0], rings[-1]
    m.quad(mat, first[0], first[1], first[2], first[3])
    m.quad(mat, last[3], last[2], last[1], last[0])


def axe():
    make_rod.SIDES = 8
    m = Mesh()
    tube(m, 'axe_wood', END, END + 0.05, RADIUS_END, RADIUS)
    tube(m, 'axe_wood', END + 0.05, TOP, RADIUS, RADIUS * 0.9)
    head(m, 'axe_steel')
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_felling_axe.mtl'), 'w') as f:
        f.write('# M3AT felling axe colours (anim_scripts/make_axe.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    path = os.path.join(out, 'tool_felling_axe.obj')
    axe().write(path, 'tool_felling_axe', 'tool_felling_axe.mtl')
    with open(path) as f:
        text = f.read().replace('make_rod.py', 'make_axe.py', 1)
    with open(path, 'w') as f:
        f.write(text)
    print('AXE %s: tool_felling_axe.obj, grip %.3f other %.3f head %.3f blade %.3f (model units)' % (out, GRIP, OTHER, HEAD, BLADE))


if __name__ == '__main__':
    main(sys.argv[1])
