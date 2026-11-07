# Makes the cat ears the game draws for the Ears_Cat gene (Source/CatEars.cs): our own, modelled here from numbers, low-poly
# like the hats. Needs only numpy, not Blender:
#
#   python3 anim_scripts/make_ears.py [skin.txt [out file]]
#
# Run it again after changing the head. Lengths are shares of the head's half width, so the ears keep to its size. One ear is
# drawn flat as seen from the front (x out from the middle, y up from the top of the head), as rings inside its outline, then
# bent onto the head's roundness; the other is its mirror.
#
# ears.txt is in the game's model space, riding the head bone as hair does:
#   v, f, key/d      as the hair files have them; the keys are up and down, each ear by itself
#   ear s b x y z    side s (L, R): joint b's rest place (the last is the tip), which the game swings the ear about
#   ew i s b w ...   vertex i's weights on those joints, s and b as above
#   inner i ...      the vertices of the ear's lining, drawn in a colour of their own
import os
import sys
import numpy as np

# The outline: from the tip down the near-straight outer edge to the head's corner, and up the inner edge's hollow curve.
TIP = (0.97, 0.82)
OUTER_BASE = (0.90, -0.72)   # well inside the head, as the base has to stay buried when the ear swings
OUTER_BOW = (1.06, 0.02)    # the curve's pull: this keeps the outer edge standing nearly straight up off the corner
INNER_BASE = (0.12, -0.20)
INNER_BOW = (0.60, 0.24)     # and this hollows the inner edge
EDGE_POINTS = 6              # segments along each edge
BASE_POINTS = 2              # and across the buried base
# The rings inside it: toward MIDDLE by this share, and out of the ear's plane by this much (+ is its front).
MIDDLE = (0.68, 0.10)
FRONT_RINGS = [(0.10, 0.05), (0.22, 0.025)]               # the rim: hair-coloured
LINING_RINGS = [(0.22, 0.025), (0.52, -0.02), (0.80, -0.045)]   # the lining, starting at the rim's last ring
BACK_RINGS = [(0.35, -0.08), (0.70, -0.13)]               # the back: a shallow bulge
FRONT_CENTRE, BACK_CENTRE = -0.05, -0.15
# Bent onto the head: the ear's outer side goes back round the head, and its top leans back a little.
Z_AT = -0.02          # where its middle is, front to back, from the middle of the head's depth
Z_OUT = -0.42         # how far back it goes per unit out
Z_UP = -0.12          # and per unit up
# How the keys hold it, turned about its root, more toward the tip. Degrees.
DOWN_OUT, DOWN_BACK = 62.0, 22.0
UP_IN, UP_FORWARD, UP_LONGER = 9.0, 7.0, 0.08
HEAD_SHARE = 0.5      # a body vertex is on the head if the head bone's weight on it is over this
HUMAN_EAR_HIGH = 1.03 # game units: the widest the head goes is measured above the chibi's own ear
FOLDER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "M3AT: Maru's 3D Animated Toons", "Models", "chibi")


def read_head(skin):
    # The chibi's head as skin.txt has it: its half width above its own ears, its top, the middle of its depth.
    verts, weights, head = [], [], None
    with open(skin) as f:
        for raw in f:
            p = raw.split()
            if not p:
                continue
            if p[0] == "v":
                verts.append([float(x) for x in p[1:4]])
            elif p[0] == "w":
                weights.append(dict((int(p[i]), float(p[i + 1])) for i in range(1, len(p) - 1, 2)))
            elif p[0] == "head":
                head = int(p[1])
    on = np.array([v for v, w in zip(verts, weights) if w.get(head, 0.0) > HEAD_SHARE])
    half = np.abs(on[on[:, 1] > HUMAN_EAR_HIGH, 0]).max()
    return half, on[:, 1].max(), (on[:, 2].min() + on[:, 2].max()) / 2.0


def bow(a, c, b, steps):
    # A curve from a to b pulled toward c, without either end's repeat of b.
    a, b, c = np.array(a), np.array(b), np.array(c)
    t = np.arange(steps)[:, None] / steps
    return (1 - t) ** 2 * a + 2 * t * (1 - t) * c + t ** 2 * b


def outline():
    return np.vstack([bow(TIP, OUTER_BOW, OUTER_BASE, EDGE_POINTS),
                      bow(OUTER_BASE, np.add(OUTER_BASE, INNER_BASE) / 2, INNER_BASE, BASE_POINTS),
                      bow(INNER_BASE, INNER_BOW, TIP, EDGE_POINTS)])


def build():
    # One ear, in the head's half widths: its vertices (x, y, depth out of its plane), triangles, and which are the lining.
    edge = outline()
    middle = np.array(MIDDLE)
    n = len(edge)

    def ring(share, depth):
        return np.column_stack([edge + (middle - edge) * share, np.full(n, depth)])
    verts, tris, inner = [], [], []

    def add(points, is_inner=False):
        first = len(verts)
        verts.extend(points)
        inner.extend([is_inner] * len(points))
        return first

    def band(a, b):
        # Between two rings, each n round, a's front to the viewer on the side the rings run clockwise from.
        for i in range(n):
            j = (i + 1) % n
            tris.append((a + i, a + j, b + i))
            tris.append((a + j, b + j, b + i))

    def cap(a, centre):
        for i in range(n):
            tris.append((a + i, a + (i + 1) % n, centre))
    rim = add(ring(0.0, 0.0))
    last = rim
    for share, depth in FRONT_RINGS:
        here = add(ring(share, depth))
        band(last, here)
        last = here
    last = None
    for share, depth in LINING_RINGS:
        here = add(ring(share, depth), True)
        if last is not None:
            band(last, here)
        last = here
    centre = add([[middle[0], middle[1], FRONT_CENTRE]], True)
    cap(last, centre)
    last = rim
    for share, depth in BACK_RINGS:
        here = add(ring(share, depth))
        band(here, last)   # the back runs the other way round
        last = here
    centre = add([[middle[0], middle[1], BACK_CENTRE]])
    for i in range(n):
        tris.append((last + (i + 1) % n, last + i, centre))
    return np.array(verts), np.array(tris), np.array(inner)


def turn(points, pivot, axis, degrees, along):
    # Turned about the axis through pivot, by along's share of the angle at each point.
    axis = np.array(axis) / np.linalg.norm(axis)
    a = np.radians(degrees) * along[:, None]
    p = points - pivot
    return pivot + p * np.cos(a) + np.cross(axis, p) * np.sin(a) + axis * (p @ axis)[:, None] * (1 - np.cos(a))


def main():
    args = sys.argv[1:]
    skin = args[0] if args else os.path.join(FOLDER, "skin.txt")
    out = args[1] if len(args) > 1 else os.path.join(FOLDER, "ears.txt")
    half, top, depth = read_head(skin)
    flat, tris, inner = build()

    def on_head(p):
        # To game units, bent onto the head: the right-hand ear (x is out, y up, z forward).
        p = np.atleast_2d(p)
        z = Z_AT + Z_OUT * (p[:, 0] - MIDDLE[0]) + Z_UP * (p[:, 1] - MIDDLE[1]) + p[:, 2]
        return np.column_stack([p[:, 0] * half, top + p[:, 1] * half, depth + z * half])
    rest = on_head(flat)
    tip = on_head([TIP[0], TIP[1], 0.0])[0]
    root = on_head([(OUTER_BASE[0] + INNER_BASE[0]) / 2, (OUTER_BASE[1] + INNER_BASE[1]) / 2 + 0.25, 0.0])[0]
    joints = [root + (tip - root) * s for s in (0.0, 1.0 / 3.0, 2.0 / 3.0, 1.0)]
    along = np.clip((rest - root) @ (tip - root) / ((tip - root) @ (tip - root)), 0.0, 1.0)
    bent = np.clip(along * 1.6, 0.0, 1.0) ** 1.2   # the base stays in the head

    # The way out of the head is +x on this ear, so tipping out goes from +y to +x: about -z.
    down = turn(turn(rest, root, (0, 0, -1), DOWN_OUT, bent), root, (1, 0, 0), -DOWN_BACK, bent) - rest
    longer = rest + (tip - root) * (along * UP_LONGER)[:, None]
    up = turn(turn(longer, root, (0, 0, -1), -UP_IN, bent), root, (1, 0, 0), UP_FORWARD, bent) - rest

    n = len(rest)
    mirror = np.array([-1.0, 1.0, 1.0])
    # The game's left is at -x, as the body has it. The vertices run left ear, then right.
    verts = np.vstack([rest * mirror, rest])
    right = tris + n

    def inside_out(p, t):
        return np.einsum("ij,ij->i", p[t[:, 0]], np.cross(p[t[:, 1]], p[t[:, 2]])).sum() < 0
    # Both wound so the game's test (SkinnedFigure.InsideOut) finds each outward, about the ear's own middle.
    if inside_out(rest - rest.mean(0), tris):
        right = right[:, [0, 2, 1]]
    left = right[:, [0, 2, 1]] - n
    with open(out, "w", newline="\n") as f:
        f.write("# M3AT: cat ears for the Ears_Cat gene, made by anim_scripts/make_ears.py.\n")
        for v in verts:
            f.write("v %.5f %.5f %.5f\n" % tuple(v))
        for a, b, c in np.vstack([left, right]):
            f.write("f %d %d %d\n" % (a, b, c))
        for name, sign, way in (("L", mirror, 0), ("R", np.ones(3), 1)):
            for b, j in enumerate(joints):
                f.write("ear %s %d %.5f %.5f %.5f\n" % ((name, b) + tuple(j * sign)))
        for side, offset in (("L", 0), ("R", n)):
            for i in range(n):
                t = along[i] * 3.0
                b = min(int(t), 2)
                share = t - b if b < 2 else 0.0
                line = "ew %d %s %d %.4f" % (i + offset, side, b, 1.0 - share)
                if share > 0.0:
                    line += " %s %d %.4f" % (side, b + 1, share)
                f.write(line + "\n")
        f.write("inner " + " ".join(str(i) for i in np.where(inner)[0]) + " " + " ".join(str(i + n) for i in np.where(inner)[0]) + "\n")
        for name, moves, offset, sign in (("up_L", up, 0, mirror), ("up_R", up, n, np.ones(3)), ("down_L", down, 0, mirror), ("down_R", down, n, np.ones(3))):
            f.write("key %s\n" % name)
            for i in range(n):
                if np.abs(moves[i]).max() > 1e-5:
                    f.write("d %d %.5f %.5f %.5f\n" % ((i + offset,) + tuple(moves[i] * sign)))
    print("EARS wrote", out, "vertices", len(verts), "triangles", len(tris) * 2, "head half", round(half, 4), "top", round(top, 4), "tip", tip.round(3))


main()
