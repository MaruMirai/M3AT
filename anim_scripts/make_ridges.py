"""Paints the FacialRidges gene's three designs for the face patch (WalkFigures.RidgesOf, SkinnedFigure.DrawRidges).

    python3 anim_scripts/make_ridges.py "M3AT: Maru's 3D Animated Toons/Models/chibi"

Writes ridges_a.png, ridges_b.png and ridges_c.png. Each is the whole of the face texture's layout (Facial Animation's,
the patch's UVs): SIZE pixels square, the head's middle at x 256, the eye line at y 282, the patch reaching x 171 .. 341
and y 165 .. 335. Under hair its top is cut off along its triangles, at y 204 in the middle but 221 at x 220, and the
brows lie about y 250, so the forehead's ridges keep to the small space between. All numbers are in those pixels
at SIZE 512. The game multiplies the image by the skin colour, so white is plain skin: a ridge is a little darker than
the skin with a darker rim, which reads as a raised edge on any skin. Each ridge is a tapered stroke along a curve; the
right side of the face is the left's mirror.
"""
import os
import sys

import numpy as np
from PIL import Image

SIZE = 512
MIDDLE = 256       # the face's middle
SOFT = 1.2         # pixels the edge fades over
RIM = 2.2          # pixels of the darker rim, inside the edge
BODY = (0.87, 0.81, 0.78)   # a ridge's colour, times the skin
EDGE = (0.70, 0.60, 0.56)   # its rim's

# Each ridge: its curve (a quadratic Bezier, as start, control, end), its width at the ends and at the middle (half
# widths), and whether it is mirrored across the face. A ridge on the middle isn't.
DESIGNS = {
    "a": [   # a bar over each brow, meeting over the nose, and a ridge round the outside of each eye to under it
        ((256, 240), (228, 228), (204, 238), 2.5, 5, True),
        ((199, 268), (200, 307), (238, 309), 1.5, 4.5, True),
    ],
    "b": [   # bars stacked up the middle of the forehead, narrowing upward to a point
        ((256, 240), (232, 238), (218, 241), 3, 4.5, True),
        ((256, 228), (240, 226), (230, 229), 3, 4, True),
        ((256, 217), (248, 215), (242, 218), 3, 3.5, True),
        ((256, 205), (256, 209), (256, 215), 1, 3.5, False),
    ],
    "c": [   # three pairs of leaves fanning up and out from over the nose
        ((250, 242), (226, 232), (206, 240), 1.5, 4.5, True),
        ((251, 232), (234, 220), (222, 226), 1.5, 4.5, True),
        ((252, 225), (250, 212), (240, 209), 1.5, 4, True),
    ],
}


def stroke(xs, ys, start, control, end, ends, middle):
    """How far each pixel is inside the tapered stroke, in pixels (below 0: outside)."""
    t = np.linspace(0.0, 1.0, 96)[:, None, None]
    px = (1 - t) ** 2 * start[0] + 2 * (1 - t) * t * control[0] + t ** 2 * end[0]
    py = (1 - t) ** 2 * start[1] + 2 * (1 - t) * t * control[1] + t ** 2 * end[1]
    radius = ends + (middle - ends) * np.sin(np.pi * t) ** 0.8
    return (radius - np.hypot(xs - px, ys - py)).max(axis=0)


def paint(ridges):
    ys, xs = np.mgrid[0:SIZE, 0:SIZE] + 0.5
    inside = np.full((SIZE, SIZE), -1e6)
    for start, control, end, ends, middle, mirrored in ridges:
        inside = np.maximum(inside, stroke(xs, ys, start, control, end, ends, middle))
        if mirrored:
            inside = np.maximum(inside, stroke(2 * MIDDLE - xs, ys, start, control, end, ends, middle))
    alpha = np.clip(inside / SOFT + 0.5, 0.0, 1.0)
    # The rim is measured in from the edge of the joined ridges, so where two meet there is no line between them.
    body = np.clip((inside - RIM) / SOFT + 0.5, 0.0, 1.0)[..., None]
    rgb = np.array(EDGE) * (1 - body) + np.array(BODY) * body
    return np.dstack([rgb, alpha])


def main():
    out = sys.argv[1]
    for name, ridges in DESIGNS.items():
        image = (paint(ridges) * 255 + 0.5).astype(np.uint8)
        path = os.path.join(out, "ridges_%s.png" % name)
        Image.fromarray(image, "RGBA").save(path)
        print("wrote", path)


if __name__ == "__main__":
    main()
