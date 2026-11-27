"""Makes the unit length of rope the game draws round a prisoner tied to a tree (Tie to Tree; WalkFigures.DrawRope), as
IsoRim's tool models are made (make_rod.py): an OBJ in the game's model space (one unit = one map cell, +y up, -z north),
one triangle a face, wound as IsoRim's own files are (IsoRim's loader mirrors z), with its own .mtl beside it.

    python3 anim_scripts/make_rope.py "M3AT: Maru's 3D Animated Toons/Models"

The rope runs up +y from the origin to (0, 1, 0), RADIUS thick; the game stretches it along y to each straight run of
the loop, so its sides keep their thickness. WalkFigures.RopeRadius must stay RADIUS.
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from make_rod import Mesh

RADIUS = 0.014   # cells
SIDES = 6
COLOURS = {
    'rope': (0.60, 0.48, 0.30),
    'rope_strand': (0.50, 0.39, 0.24),
}


def rope():
    """One cell of rope up y from the origin: SIDES sides, alternate ones a little darker, so it reads as laid strands."""
    m = Mesh()
    lo = [(RADIUS * math.cos(2 * math.pi * i / SIDES), 0.0, RADIUS * math.sin(2 * math.pi * i / SIDES)) for i in range(SIDES)]
    hi = [(x, 1.0, z) for x, _, z in lo]
    for i in range(SIDES):
        j = (i + 1) % SIDES
        # lo runs from +x toward +z, which seen from outside is clockwise: so b before a (as make_rod.line).
        m.quad('rope' if i % 2 == 0 else 'rope_strand', lo[j], lo[i], hi[i], hi[j])
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_rope.mtl'), 'w') as f:
        f.write('# M3AT rope colours (anim_scripts/make_rope.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    rope().write(os.path.join(out, 'tool_rope.obj'), 'tool_rope', 'tool_rope.mtl')
    print('ROPE %s: tool_rope.obj (radius %.3f cells)' % (out, RADIUS))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else "M3AT: Maru's 3D Animated Toons/Models")
