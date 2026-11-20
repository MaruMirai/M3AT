"""Paints old age's wrinkles for the face patch, three stages of them (WalkFigures.WrinklesOf, SkinnedFigure.DrawRidges).

    python3 anim_scripts/make_wrinkles.py "M3AT: Maru's 3D Animated Toons/Models/chibi" [preview.png]

Writes ridges_age1.png .. ridges_age<LEVELS>.png, read and drawn as the FacialRidges gene's designs are: the
whole of the face texture's layout (Facial Animation's, the patch's UVs), SIZE pixels square, the head's middle at x 256,
the eye line at y 282, the patch reaching x 171 .. 341 and y 165 .. 335, its top cut off under hair at y 204 in the
middle and 221 at x 220. Facial Animation's art lies at: the eyes x 203 .. 245 (and their mirror), y 267 .. 287, the
lids' cover down to y 297, the brows y 245 .. 255, the mouth y about 311 as the game raises it (FaceLayers.MouthRaise),
the nose's tip about y 302. All numbers are in those pixels at SIZE 512.

The game multiplies the image by the skin colour, so a wrinkle is the skin made darker: a soft line, a little darker and
redder than the skin, which reads on any skin. Each line is a tapered stroke along a quadratic curve, mirrored across
the face's middle. Age runs over STAGES stages, as one continuous amount; each line has the stage it shows in, fading
in and thickening from nothing over it, then growing wider and darker for each stage after (GROW, DEEPEN), so the face
ages by degrees: crow's feet and the lines from the nose first, then the forehead and the bags under the eyes, then the
frown and the lines down from the mouth's corners. It is written at LEVELS evenly spaced amounts (WalkFigures.WrinklesOf
picks the nearest by age), close enough that each step is hardly seen.

With a preview path it also writes each stage over Facial Animation's own flat layers side by side, to look at.
"""
import os
import sys

import numpy as np
from PIL import Image

SIZE = 512
MIDDLE = 256
SOFT = 1.0          # pixels the edge fades over
LINE = (0.66, 0.53, 0.49)   # a wrinkle's colour, times the skin
SHADOW = (0.84, 0.76, 0.72)   # the soft shade beside a fold (the nose's and mouth's lines), times the skin
ALPHA = 0.62        # a line's opacity at the stage it first shows
DEEPEN = 0.12       # and more for each stage after
GROW = 0.2          # its width's share more for each stage after
STAGES = 3
EAR_MOVE = 0.005     # cells: a vertex the head's No_Human_Ears key moves this far or more is on an ear (HairBuilder.EarMove)
EAR_GROW = 1         # pixels the ears' mask is grown by, and
EAR_SOFT = 2.0       # pixels it fades over past that, so a line ends on the cheek rather than being cut off at the ear
LEVELS = 8          # images written, evenly over the stages (WalkFigures.WrinkleLevels)

# Each line: its curve (start, control, end), its half width at the ends and the middle, the stage it shows from, and
# the width of the shade beside it (0: none; a fold, not a crease, has one on its cheek side).
LINES = [
    # crow's feet, fanning out from the eye's outer corner
    # (the ears, part of the patch, start at x 185 .. 197 by row: they stop short of those, and the mask keeps them off it)
    ((203, 275), (199, 273), (195, 271), 0.4, 1.0, 1, 0),
    ((203, 282), (199, 283), (195, 284), 0.4, 1.0, 1, 0),
    ((203, 289), (200, 291), (198, 293), 0.4, 0.9, 2, 0),
    # the nasolabial folds, from beside the nose round to beside the mouth's corners
    ((236, 298), (229, 307), (237, 320), 0.5, 1.6, 1, 3.0),
    # the forehead's lines, between the brows and where hair cuts the patch off
    ((256, 235), (240, 233), (227, 238), 0.6, 1.4, 2, 0),
    ((256, 226), (245, 224), (235, 228), 0.5, 1.2, 3, 0),
    # bags under the eyes
    ((212, 294), (225, 299), (239, 294), 0.5, 1.3, 2, 0),
    # the frown between the brows
    ((250, 246), (249, 243), (250, 240), 0.4, 1.0, 3, 0),
    # the marionette lines, down from the mouth's corners
    ((245, 315), (244, 319), (244, 324), 0.4, 1.0, 3, 0),
]


def ear_mask(skin_path):
    """1 over the texels where the face patch's front-most triangle (toward +z) is an ear's, 0 elsewhere, SIZE square, rows top
    first. The ears are part of the face patch and stick out past the cheeks at its sides, in the same front projection (the
    flat beard clears them the same way: HairBuilder.ClearEars), so a wrinkle painted there is drawn on the ear. An ear is
    what the head's No_Human_Ears key moves, at two corners or more."""
    fv, ff, key, current = {}, [], {}, None
    for line in open(skin_path):
        t = line.split()
        if not t:
            continue
        if t[0] == "garment":
            break
        if t[0] == "fv":
            fv[int(t[1])] = (float(t[2]) * SIZE, (1 - float(t[3])) * SIZE, float(t[6]))   # column, row (top first), depth
        elif t[0] == "ff":
            ff.append(tuple(map(int, t[1:4])))
        elif t[0] == "key":
            current = t[1]
            key.setdefault(current, {})
        elif t[0] == "d" and current:
            key[current][int(t[1])] = tuple(map(float, t[2:5]))
    moved = {i for i, d in key.get("No_Human_Ears", {}).items() if sum(x * x for x in d) >= EAR_MOVE ** 2}
    depth = np.full((SIZE, SIZE), -1e9)
    ear = np.zeros((SIZE, SIZE), bool)
    for tri in ff:
        if any(i not in fv for i in tri):
            continue
        pa, pb, pc = (fv[i] for i in tri)
        is_ear = sum(i in moved for i in tri) >= 2
        area = (pb[0] - pa[0]) * (pc[1] - pa[1]) - (pc[0] - pa[0]) * (pb[1] - pa[1])
        if abs(area) < 1e-6:
            continue
        x0, x1 = int(max(0, min(pa[0], pb[0], pc[0]))), int(min(SIZE - 1, max(pa[0], pb[0], pc[0]) + 1))
        y0, y1 = int(max(0, min(pa[1], pb[1], pc[1]))), int(min(SIZE - 1, max(pa[1], pb[1], pc[1]) + 1))
        ys, xs = np.mgrid[y0:y1 + 1, x0:x1 + 1] + 0.5
        wa = ((pb[0] - xs) * (pc[1] - ys) - (pc[0] - xs) * (pb[1] - ys)) / area
        wb = ((pc[0] - xs) * (pa[1] - ys) - (pa[0] - xs) * (pc[1] - ys)) / area
        wc = 1 - wa - wb
        inside = (wa >= 0) & (wb >= 0) & (wc >= 0)
        z = wa * pa[2] + wb * pb[2] + wc * pc[2]
        sub = (slice(y0, y1 + 1), slice(x0, x1 + 1))
        win = inside & (z > depth[sub])
        depth[sub] = np.where(win, z, depth[sub])
        ear[sub] = np.where(win, is_ear, ear[sub])
    return ear


def clear_of(ear):
    """1 where a wrinkle may be drawn: off the ears by EAR_GROW pixels, fading in over EAR_SOFT more."""
    ys, xs = np.nonzero(ear)
    dist = np.full((SIZE, SIZE), 1e6)
    if len(xs):
        gy, gx = np.mgrid[0:SIZE, 0:SIZE]
        for k in range(0, len(xs), 400):   # distance to the nearest ear texel, in chunks
            d = np.hypot(gx[..., None] - xs[None, None, k:k + 400], gy[..., None] - ys[None, None, k:k + 400]).min(axis=2)
            dist = np.minimum(dist, d)
    return np.clip((dist - EAR_GROW) / EAR_SOFT, 0.0, 1.0)


def stroke(xs, ys, start, control, end, ends, middle):
    """How far each pixel is inside the tapered stroke, in pixels (below 0: outside), and where along it (0 .. 1)."""
    t = np.linspace(0.0, 1.0, 64)[:, None, None]
    px = (1 - t) ** 2 * start[0] + 2 * (1 - t) * t * control[0] + t ** 2 * end[0]
    py = (1 - t) ** 2 * start[1] + 2 * (1 - t) * t * control[1] + t ** 2 * end[1]
    radius = ends + (middle - ends) * np.sin(np.pi * t) ** 0.7
    inside = radius - np.hypot(xs - px, ys - py)
    return inside.max(axis=0)


def side_shade(xs, ys, start, control, end, width):
    """A soft shade out to width pixels on the cheek's side of a fold (further from the face's middle), 0 .. 1."""
    t = np.linspace(0.0, 1.0, 64)[:, None, None]
    px = (1 - t) ** 2 * start[0] + 2 * (1 - t) * t * control[0] + t ** 2 * end[0]
    py = (1 - t) ** 2 * start[1] + 2 * (1 - t) * t * control[1] + t ** 2 * end[1]
    d = np.hypot(xs - px, ys - py)
    i = d.argmin(axis=0)
    near = np.take_along_axis(px, i[None], axis=0)[0]
    outward = np.abs(xs - MIDDLE) > np.abs(near - MIDDLE)
    taper = np.sin(np.pi * np.take_along_axis(t + 0 * px, i[None], axis=0)[0])
    return np.clip(1 - d.min(axis=0) / width, 0, 1) * outward * taper


def paint(stage, clear=None):
    """The wrinkles at an amount of age, 0 .. STAGES (a line of stage n fading in from n - 1 to n)."""
    ys, xs = np.mgrid[0:SIZE, 0:SIZE] + 0.5
    rgb = np.ones((SIZE, SIZE, 3))
    alpha = np.zeros((SIZE, SIZE))
    for start, control, end, ends, middle, first, shade in LINES:
        shown = min(1.0, max(0.0, stage - (first - 1)))
        if shown <= 0:
            continue
        older = max(0.0, stage - first)
        grow = (1 + GROW * older) * (0.6 + 0.4 * shown)
        strength = min(1.0, ALPHA + DEEPEN * older) * shown
        for mirror in (False, True):
            x = 2 * MIDDLE - xs if mirror else xs
            if shade > 0:
                s = side_shade(x, ys, start, control, end, shade * grow) * strength * 0.6
                rgb = rgb * (1 - s[..., None]) + np.array(SHADOW) * s[..., None]
                alpha = np.maximum(alpha, s)
            a = np.clip(stroke(x, ys, start, control, end, ends * grow, middle * grow) / SOFT + 0.5, 0, 1) * strength
            rgb = rgb * (1 - a[..., None]) + np.array(LINE) * a[..., None]
            alpha = np.maximum(alpha, a)
    if clear is not None:
        alpha = alpha * clear   # not on the ears
    # The colour where it is see-through doesn't matter but for filtering: kept the line's, so edges don't go white.
    return np.dstack([rgb, alpha])


def holes_of(alpha):
    """Where the lid cover's mask is open and shut in on every side: the eyes, not the rest of the texture."""
    solid = alpha >= 0.5
    outside = np.zeros_like(solid)
    outside[0, :] = outside[-1, :] = outside[:, 0] = outside[:, -1] = True
    outside &= ~solid
    while True:
        grown = outside.copy()
        grown[1:] |= outside[:-1]
        grown[:-1] |= outside[1:]
        grown[:, 1:] |= outside[:, :-1]
        grown[:, :-1] |= outside[:, 1:]
        grown &= ~solid
        if (grown == outside).all():
            return ~solid & ~outside
        outside = grown


def preview(images, path):
    root = os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/2581693737/Textures/Things/Pawn/FA-Experimentals/")
    skin = np.array([236, 200, 170]) / 255
    cells = []
    for image in [None] + images:
        base = np.ones((SIZE, SIZE, 3)) * skin
        layers = {}
        for p in ("Eyes/Common/Male/normal_L_south.png", "Eyes/Common/Male/normal_R_south.png",
                  "Lids/Normal/Male/normal_cover_south.png", "Mouth/Normal/Male/normal_south.png",
                  "Brows/Normal/Male/s-shaped_south.png"):
            if os.path.exists(root + p):
                layers[p.split("/")[0]] = layers.get(p.split("/")[0], []) + [np.asarray(Image.open(root + p).convert("RGBA").resize((SIZE, SIZE)), float) / 255]
        # The eye layers fill the texture; only the lid cover's openings round the eyes show them (as face_test.py has it).
        eyes = base.copy()
        for a in layers.get("Eyes", []):
            eyes = a[..., :3] * a[..., 3:] + eyes * (1 - a[..., 3:])
        cover = layers["Lids"][0]
        hole = holes_of(cover[..., 3])[..., None]
        base = np.where(hole, eyes, base)
        base = skin * cover[..., 3:] + base * (1 - cover[..., 3:])
        for a in layers.get("Mouth", []):
            a = np.roll(a, -6, axis=0)   # raised as the game raises it
            base = a[..., :3] * a[..., 3:] + base * (1 - a[..., 3:])
        for a in layers.get("Brows", []):
            base = a[..., :3] * a[..., 3:] + base * (1 - a[..., 3:])
        if image is not None:
            base = base * (1 - image[..., 3:]) + base * image[..., :3] * image[..., 3:]
        cells.append(base[165:340, 165:347])
    sheet = np.concatenate(cells, axis=1)
    Image.fromarray((sheet * 255).astype(np.uint8)).resize((sheet.shape[1] * 3, sheet.shape[0] * 3), Image.LANCZOS).save(path)
    print("wrote", path)


def main():
    out = sys.argv[1]
    images = []
    clear = clear_of(ear_mask(os.path.join(out, "skin.txt")))
    for level in range(1, LEVELS + 1):
        image = paint(STAGES * level / LEVELS, clear)
        if level % 2 == 0:
            images.append(image)   # the preview shows every other one
        path = os.path.join(out, "ridges_age%d.png" % level)
        Image.fromarray((image * 255 + 0.5).astype(np.uint8), "RGBA").save(path)
        print("wrote", path)
    if len(sys.argv) > 2:
        preview(images, sys.argv[2])


if __name__ == "__main__":
    main()
