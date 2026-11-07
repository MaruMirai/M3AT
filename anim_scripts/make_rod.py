"""Makes the fishing rod the work_fish clip holds, and the unit length of line the game draws from its tip, as IsoRim's
tool models are made: OBJs in the game's model space (one unit = one map cell at IsoRim's figure scale, +y up, -z north),
one triangle a face, wound as IsoRim's own files are (IsoRim's loader mirrors z), with their own .mtl beside them.

    python3 anim_scripts/make_rod.py "M3AT: Maru's 3D Animated Toons/Models"

The rod runs up +y from its butt at the origin to its tip at (0, LENGTH, 0); its reel hangs off the +z side, which is the
side walk.tool_turn turns down when it pitches the rod forward (Blender's -y, the way an axe's edge points). walk.WORK's
'fish' grip and other are along this line, in these units; WalkFigures.RodTip must stay LENGTH.
"""
import math
import os
import sys

LENGTH = 1.7          # cells from the butt to the tip
GRIP_TOP = 0.32       # the cork handle, from the butt
GRIP_RADIUS = 0.017
BLANK_ROOT = 0.011    # the rod's radius where it leaves the handle, tapering to BLANK_TIP
BLANK_TIP = 0.003
SIDES = 8
REEL_AT = 0.2         # the reel's stem, up the handle
REEL_DROP = 0.045     # the reel's middle below the rod's line (+z)
REEL_RADIUS = 0.03
REEL_WIDTH = 0.026
GUIDES = (0.6, 0.95, 1.25, 1.5)   # rings along the rod, up from the butt
GUIDE_RADIUS = 0.012
LINE_RADIUS = 0.0035  # the line's, so it still shows at the default zoom

COLOURS = {
    'rod_blank': (0.16, 0.12, 0.10),
    'rod_cork': (0.72, 0.56, 0.36),
    'rod_reel': (0.55, 0.57, 0.60),
    'rod_guide': (0.80, 0.80, 0.82),
    'fishing_line': (0.90, 0.92, 0.88),
}


class Mesh:
    def __init__(self):
        self.parts = []   # (material, [triangles of three points])

    def tri(self, mat, a, b, c):
        """One triangle wound counter-clockwise seen from outside (right-handed, outward normal)."""
        if not self.parts or self.parts[-1][0] != mat:
            self.parts.append((mat, []))
        self.parts[-1][1].append((a, b, c))

    def quad(self, mat, a, b, c, d):
        self.tri(mat, a, b, c)
        self.tri(mat, a, c, d)

    def write(self, path, name, mtl):
        with open(path, 'w') as f:
            f.write('# M3AT model: %s (anim_scripts/make_rod.py)\n' % name)
            f.write('# One unit = one map cell, +Y up, -Z = north.\n')
            f.write('mtllib %s\no %s\n' % (mtl, name))
            n = 0
            for mat, tris in self.parts:
                f.write('usemtl %s\n' % mat)
                for a, b, c in tris:
                    # IsoRim's files are wound the other way round, as its loader mirrors z.
                    for p in (a, c, b):
                        f.write('v %.4f %.4f %.4f\n' % p)
                    for p in (a, c, b):
                        f.write('vt %.4f %.4f\n' % (p[0], p[1]))
                    f.write('f %d/%d %d/%d %d/%d\n' % (n + 1, n + 1, n + 2, n + 2, n + 3, n + 3))
                    n += 3


def ring(centre, axis_u, axis_v, r, i):
    a = 2 * math.pi * i / SIDES
    return tuple(centre[k] + r * (math.cos(a) * axis_u[k] + math.sin(a) * axis_v[k]) for k in range(3))


def tube(m, mat, y0, y1, r0, r1, caps=True):
    """A tube up y from y0 to y1, radius r0 to r1."""
    lo = [ring((0, y0, 0), (1, 0, 0), (0, 0, -1), r0, i) for i in range(SIDES)]
    hi = [ring((0, y1, 0), (1, 0, 0), (0, 0, -1), r1, i) for i in range(SIDES)]
    for i in range(SIDES):
        j = (i + 1) % SIDES
        m.quad(mat, lo[i], lo[j], hi[j], hi[i])
    if caps:
        for i in range(SIDES):
            j = (i + 1) % SIDES
            m.tri(mat, (0, y0, 0), lo[j], lo[i])
            m.tri(mat, (0, y1, 0), hi[i], hi[j])


def drum(m, mat, centre, r, width):
    """A short cylinder about x (the reel's spool)."""
    x0, x1 = centre[0] - width / 2, centre[0] + width / 2
    a = [ring((x0, centre[1], centre[2]), (0, 1, 0), (0, 0, 1), r, i) for i in range(SIDES)]
    b = [ring((x1, centre[1], centre[2]), (0, 1, 0), (0, 0, 1), r, i) for i in range(SIDES)]
    for i in range(SIDES):
        j = (i + 1) % SIDES
        m.quad(mat, a[i], a[j], b[j], b[i])
        m.tri(mat, (x0, centre[1], centre[2]), a[j], a[i])
        m.tri(mat, (x1, centre[1], centre[2]), b[i], b[j])


def box(m, mat, lo, hi):
    (x0, y0, z0), (x1, y1, z1) = lo, hi
    P = lambda x, y, z: (x, y, z)
    m.quad(mat, P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1))   # +z
    m.quad(mat, P(x1, y0, z0), P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0))   # -z
    m.quad(mat, P(x1, y0, z1), P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1))   # +x
    m.quad(mat, P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1), P(x0, y1, z0))   # -x
    m.quad(mat, P(x0, y1, z1), P(x1, y1, z1), P(x1, y1, z0), P(x0, y1, z0))   # +y
    m.quad(mat, P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1))   # -y


def rod():
    m = Mesh()
    tube(m, 'rod_cork', 0.0, GRIP_TOP, GRIP_RADIUS, GRIP_RADIUS * 0.9)
    tube(m, 'rod_blank', GRIP_TOP, LENGTH, BLANK_ROOT, BLANK_TIP)
    # The reel: a stem down from the handle and the spool across under it.
    box(m, 'rod_reel', (-0.004, REEL_AT - 0.012, 0.0), (0.004, REEL_AT + 0.012, REEL_DROP - REEL_RADIUS * 0.5))
    drum(m, 'rod_reel', (0.0, REEL_AT, REEL_DROP), REEL_RADIUS, REEL_WIDTH)
    # Its handle, out to the right of the spool (the figure's right: +x is its left in the game's model space).
    box(m, 'rod_reel', (-REEL_WIDTH / 2 - 0.028, REEL_AT - 0.004, REEL_DROP - 0.004),
        (-REEL_WIDTH / 2, REEL_AT + 0.004, REEL_DROP + 0.004))
    # Guides: small rings hanging under the rod.
    for y in GUIDES:
        r = BLANK_ROOT + (BLANK_TIP - BLANK_ROOT) * (y - GRIP_TOP) / (LENGTH - GRIP_TOP)
        box(m, 'rod_guide', (-0.002, y - 0.002, r), (0.002, y + 0.002, r + GUIDE_RADIUS))
    return m


def line():
    """One cell of line up y from the origin, drawn stretched from the rod's tip to the water."""
    m = Mesh()
    sides = 4
    lo = [(LINE_RADIUS * math.cos(2 * math.pi * i / sides), 0.0, LINE_RADIUS * math.sin(2 * math.pi * i / sides)) for i in range(sides)]
    hi = [(x, 1.0, z) for x, _, z in lo]
    for i in range(sides):
        j = (i + 1) % sides
        # lo runs from +x toward +z, which seen from outside is clockwise: so b before a.
        m.quad('fishing_line', lo[j], lo[i], hi[i], hi[j])
    return m


def main(out):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, 'tool_rod.mtl'), 'w') as f:
        f.write('# M3AT fishing rod colours (anim_scripts/make_rod.py); fixed (no stuff)\n')
        for name, (r, g, b) in COLOURS.items():
            f.write('\nnewmtl %s\nKd %.3f %.3f %.3f\n' % (name, r, g, b))
    rod().write(os.path.join(out, 'tool_rod.obj'), 'tool_rod', 'tool_rod.mtl')
    line().write(os.path.join(out, 'fishing_line.obj'), 'fishing_line', 'tool_rod.mtl')
    print('ROD %s: tool_rod.obj (tip %.2f cells up), fishing_line.obj' % (out, LENGTH))


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else "M3AT: Maru's 3D Animated Toons/Models")
