"""Makes the joint the smoke clip holds (smokeleaf, flake), as make_rod.py makes the fishing rod: an OBJ in the game's
model space (one unit = one map cell at a grown pawn's size, +y up), with its own .mtl beside it.

    python3 anim_scripts/make_joint.py "M3AT: Maru's 3D Animated Toons/Models"

It runs up +y from its filter end at the origin to its lit end at (0, LENGTH, 0): walk.smoke_rest() puts the origin
SMOKE_OUT on the palm's side of the fingers and +y out through them. A cone joint, wider at the lit end, a little longer
and thicker than a real one against the chibi's hand, so it shows at the default zoom. tool_joint_unlit.obj is the same
with no ember, its end twisted shut: the joint being rolled at the bench (WalkFigures.DrawRolledJoint).
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_rod
from make_rod import Mesh, tube

LENGTH = 0.1          # cells from the filter end to the lit end
FILTER = 0.022        # the rolled card filter, from the filter end
EMBER = 0.007         # the burning end
RADIUS_FILTER = 0.0065
RADIUS_LIT = 0.009

COLOURS = {
    'joint_filter': (0.80, 0.68, 0.48),
    'joint_paper': (0.93, 0.92, 0.86),
    'joint_ember': (0.95, 0.38, 0.10),
}


def joint(lit=True):
    make_rod.SIDES = 8
    m = Mesh()
    at = lambda y: RADIUS_FILTER + (RADIUS_LIT - RADIUS_FILTER) * y / LENGTH
    tube(m, 'joint_filter', 0.0, FILTER, at(0.0), at(FILTER))
    tube(m, 'joint_paper', FILTER, LENGTH - EMBER, at(FILTER), at(LENGTH - EMBER))
    # The ember a little narrower than the paper, as burnt back; unlit, the paper twisted shut there instead.
    tube(m, 'joint_ember' if lit else 'joint_paper', LENGTH - EMBER, LENGTH, at(LENGTH - EMBER) * 0.95, at(LENGTH) * (0.8 if lit else 0.45))
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_joint.mtl'), 'w') as f:
        f.write('# M3AT joint colours (anim_scripts/make_joint.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    # tool_joint.obj, lit, smoked (the smoke clip); tool_joint_unlit.obj, being rolled on the bench (Make_SmokeleafJoint).
    for name, lit in (('tool_joint', True), ('tool_joint_unlit', False)):
        path = os.path.join(out, name + '.obj')
        joint(lit).write(path, name, 'tool_joint.mtl')
        with open(path) as f:
            text = f.read().replace('make_rod.py', 'make_joint.py', 1)
        with open(path, 'w') as f:
            f.write(text)
    print('JOINT %s: tool_joint.obj, tool_joint_unlit.obj (%.2f cells long)' % (out, LENGTH))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else "M3AT: Maru's 3D Animated Toons/Models")
