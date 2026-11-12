"""Makes the stick of chalk the chalk clip draws with (Biotech's Floordrawing), as make_joint.py makes the joint: an OBJ in
the game's model space (one unit = one map cell at a grown pawn's size, +y up), with its own .mtl beside it.

    python3 anim_scripts/make_chalk.py "M3AT: Maru's 3D Animated Toons/Models"

It runs up +y from its drawing tip at the origin to its back end at (0, LENGTH, 0): walk.chalk_rest() puts the origin
CHALK_OUT on the palm's side of the fingers and +y out through the back of them, so the tip is what touches the floor.
A thick round stick of pavement chalk, worn to a blunt cone at the tip, a little bigger than a real one against the
chibi's hand, so it shows at the default zoom; pastel, as the game's floor drawings are.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_rod
from make_rod import Mesh, tube

LENGTH = 0.075        # cells from the tip to the back end
WORN = 0.012          # the worn cone at the tip
RADIUS = 0.011
RADIUS_TIP = 0.005

COLOURS = {
    'chalk': (0.96, 0.72, 0.80),       # pastel pink
    'chalk_dust': (0.99, 0.93, 0.95),  # the worn tip, paler with dust
}


def chalk():
    make_rod.SIDES = 8
    m = Mesh()
    tube(m, 'chalk_dust', 0.0, WORN, RADIUS_TIP, RADIUS)
    tube(m, 'chalk', WORN, LENGTH, RADIUS, RADIUS)
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_chalk.mtl'), 'w') as f:
        f.write('# M3AT chalk colours (anim_scripts/make_chalk.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    path = os.path.join(out, 'tool_chalk.obj')
    chalk().write(path, 'tool_chalk', 'tool_chalk.mtl')
    with open(path) as f:
        text = f.read().replace('make_rod.py', 'make_chalk.py', 1)
    with open(path, 'w') as f:
        f.write(text)
    print('CHALK %s: tool_chalk.obj (%.3f cells long)' % (out, LENGTH))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else "M3AT: Maru's 3D Animated Toons/Models")
