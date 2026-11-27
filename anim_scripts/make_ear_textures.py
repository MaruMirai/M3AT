# Paints the ears' textures (Source/EarLining.cs), grey, as the game multiplies them by the ear's colours (the outside the
# hair's, the inside the lining's, or for the fluffy ears the hair's paled by the alpha):
#   python3 anim_scripts/make_ear_textures.py "<mod>/Models/chibi" [preview.png]
# (preview: the fluffy texture on dark, ginger and pale hair beside the mouse's grey)
# Each is two halves, the outside on the left and the inside on the right, and in each half an ear is laid out by its
# place along it (v: the root at the bottom, the tip at the top) and across it (u: from one edge, LIMIT, to the other),
# so one painting fits every ear shape. Written:
#   ear_fluffy.png  the cat ears (the gene's and Kiiro's), Wolfein's and Miho's: fur streaks outside; inside, grey fluff
#                   with a shadowed cup, and in the alpha how pale each place goes (the shader blends the hair's colour
#                   toward a warm white by it): bare along the rim, tufts growing up and in from it, fading out at the tip
#   ear_mouse.png   Ratkin's: short velvet outside; inside, bare smooth skin with a shadowed cup, faint veins and a thin
#                   fringe of fur along the rim
# Our own art, procedural (a fixed seed), no game sprites.
import math
import os
import sys

import numpy as np
from PIL import Image

SIZE = 256
HALF = 0.23     # of the width, half an ear's width in its half, as EarLining.cs's LiningHalf
SEED = 11
# How pale the fluffy inside goes, the texture's alpha (MarusSmooth.frag.glsl's _Lining blends the hair's colour toward a
# warm white by it, per pixel): across is of the inside's own width at each height (EarLining.cs's InsideEdge), so the rim
# is +-1 all the way up. None within RIM_BARE of the rim, whole by RIM_FULL in; tufts start TUFT_FROM..TUFT_TO across and
# grow up and in over the bare rim. Faded out along from TIP_FROM to TIP_TO, and in over ROOT at the root.
RIM_BARE, RIM_FULL = 0.1, 0.55
TUFT_FROM, TUFT_TO = 0.55, 0.9
TIP_FROM, TIP_TO = 0.62, 0.92
ROOT = 0.08
# Whether the inside is paled at all (off Nov 2026, back on 2026-11-27 as a per-pixel fade).
PALE_INSIDE = True
LINING_PALE, LINING_KEEP = (0.97, 0.94, 0.9), 0.12   # MarusSmooth.frag.glsl's LiningPale and LiningKeep (keep them the same)


def grid():
    """Each pixel's place: which half (0 outside, 1 inside), across (-1 .. 1, edge to edge, past them in the margin) and
    along (0 root .. 1 tip). Row 0 of the image is its top, the tip."""
    x = (np.arange(SIZE) + 0.5) / SIZE
    y = 1.0 - (np.arange(SIZE) + 0.5) / SIZE
    u, v = np.meshgrid(x, y)
    inside = u >= 0.5
    across = (u - np.where(inside, 0.75, 0.25)) / HALF
    return inside, across, v


def width_at(t):
    """How wide an ear is at that place along it, of its widest: a pointed ear's, so the edges are known for the fluff."""
    return np.clip(1.05 * (1.0 - t) ** 0.75 * (0.6 + 0.4 * np.sin(np.clip(t * 3.0, 0, math.pi / 2))), 0.08, 1.0)


def smooth_noise(rng, shape, scale_x, scale_y):
    """Value noise, stretched (scale: cells across the image each way), 0 .. 1."""
    gx, gy = int(scale_x) + 2, int(scale_y) + 2
    g = rng.random((gy, gx))
    ys = np.linspace(0, scale_y, shape[0])
    xs = np.linspace(0, scale_x, shape[1])
    x0, y0 = xs.astype(int), ys.astype(int)
    fx, fy = xs - x0, ys - y0
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    a = g[y0][:, x0] * (1 - fx) + g[y0][:, x0 + 1] * fx
    b = g[y0 + 1][:, x0] * (1 - fx) + g[y0 + 1][:, x0 + 1] * fx
    return a * (1 - fy[:, None]) + b * fy[:, None]


def strokes(img, mask_half, rng, count, start, direction, length, width, value, fade):
    """Soft curved strokes drawn as max() into img on one half: start(rng) -> (across, along), direction(across, along) ->
    a unit way (d_across, d_along) in the ear's own layout, length and width in pixels."""
    h, w = img.shape
    yy, xx = np.mgrid[0:h, 0:w]
    for _ in range(count):
        s, t = start(rng)
        da, dt = direction(s, t, rng)
        n = int(length(rng))
        bend = rng.uniform(-0.6, 0.6)
        px = (0.75 if mask_half else 0.25) + s * HALF
        py = t
        wid = width(rng)
        for k in range(n):
            frac = k / max(n - 1, 1)
            col = px * w
            row = (1.0 - py) * h
            r0, r1 = int(max(row - 4, 0)), int(min(row + 5, h))
            c0, c1 = int(max(col - 4, 0)), int(min(col + 5, w))
            if r1 <= r0 or c1 <= c0:
                break
            d2 = (xx[r0:r1, c0:c1] - col) ** 2 + (yy[r0:r1, c0:c1] - row) ** 2
            taper = (1.0 - frac) ** 0.8
            soft = np.exp(-d2 / (2 * (wid * taper + 0.3) ** 2))
            val = value * fade(s, py) * soft
            img[r0:r1, c0:c1] = np.maximum(img[r0:r1, c0:c1], val)
            # One pixel's step along its way (in the ear's layout, across scaled to the half's width), turning slowly.
            ang = math.atan2(dt, da) + bend * 0.02
            da, dt = math.cos(ang), math.sin(ang)
            px += da / w   # a pixel's step: u and v are both shares of the image's size
            py += dt / h
    return img


def fluffy():
    """The fluffy ears' grey and, in its alpha, how pale each place of the inside goes (the shader's _Lining)."""
    rng = np.random.default_rng(SEED)
    inside, across, along = grid()
    # Outside: fur streaks along the ear, a little darker at the root.
    streak = smooth_noise(rng, (SIZE, SIZE), 70, 6)
    fine = smooth_noise(rng, (SIZE, SIZE), 140, 10)
    outside = 0.8 + 0.09 * (streak - 0.5) * 2 + 0.05 * (fine - 0.5) * 2 + 0.05 * np.clip(along, 0, 1)
    # Inside: across is its own width here (EarLining.cs's InsideEdge), so +-1 is the rim at every height.
    rim = 1.0 - np.abs(across)          # 0 at the rim, 1 down the middle
    cup = np.exp(-(across / 0.5) ** 2) * np.exp(-((along - 0.35) / 0.3) ** 2)
    tufts = np.zeros((SIZE, SIZE))

    def start(rng):
        # From just in from the rim, more of them low down, where the fluff is thickest.
        t = rng.uniform(0.0, 0.85) ** 1.25
        return rng.choice([-1.0, 1.0]) * rng.uniform(TUFT_FROM, TUFT_TO), t

    def direction(s, t, rng):
        # Up the ear and in toward its middle, more upward higher up.
        inward = -np.sign(s) * rng.uniform(0.3, 0.9) * (1.1 - t)
        k = math.hypot(inward, 1.0)
        return inward / k, 1.0 / k

    def fade(s, t):
        return float(np.clip(1.15 - t * 0.9, 0.3, 1.0))

    tufts = strokes(tufts, True, rng, 420, start, direction, lambda r: r.uniform(18, 46), lambda r: r.uniform(0.6, 1.1), 0.9, fade)
    # The grey: soft skin under the fluff, the cup in shadow, the tufts lighter than what is between them.
    grain = smooth_noise(rng, (SIZE, SIZE), 60, 14)
    inner = 0.9 - 0.09 * cup + 0.03 * (grain - 0.5) + 0.1 * tufts
    # How pale: none along the rim (RIM_BARE), rising into the middle, less in the shadowed cup; the tufts paler still
    # and reaching out over the bare rim, so the edge is fur, not a line. Faded out toward the tip and at the root.
    body = smoothstep((rim - RIM_BARE) / (RIM_FULL - RIM_BARE)) * (0.93 - 0.1 * cup + 0.06 * (grain - 0.5))
    pale = np.maximum(body, tufts * smoothstep(rim / RIM_BARE) * 0.98)
    pale *= (1 - smoothstep((along - TIP_FROM) / (TIP_TO - TIP_FROM))) * smoothstep(along / ROOT)
    if not PALE_INSIDE:
        pale = np.zeros_like(pale)
    grey = np.clip(np.where(inside, inner, outside), 0, 1)
    alpha = np.where(inside, np.clip(pale, 0, 1), 0.0)
    return grey, alpha


def smoothstep(x):
    x = np.clip(x, 0, 1)
    return x * x * (3 - 2 * x)


def mouse():
    rng = np.random.default_rng(SEED + 1)
    inside, across, along = grid()
    # Outside: short velvet, barely any grain.
    outside = 0.86 + 0.04 * (smooth_noise(rng, (SIZE, SIZE), 90, 40) - 0.5) * 2
    # Inside: bare skin, the cup in shadow, a few faint veins branching up and out from the base, a fringe at the rim.
    edge = np.abs(across) / np.clip(np.sqrt(np.clip(1 - (2 * along - 1) ** 2, 0.02, 1)), 0.05, 1)   # a round ear's edge
    cup = np.exp(-(across / 0.55) ** 2) * np.exp(-((along - 0.42) / 0.32) ** 2)
    inner = 0.96 - 0.12 * cup + 0.02 * (smooth_noise(rng, (SIZE, SIZE), 20, 20) - 0.5)
    veins = np.zeros((SIZE, SIZE))

    def start(rng):
        return rng.uniform(-0.12, 0.12), rng.uniform(0.04, 0.12)

    def direction(s, t, rng):
        a = rng.uniform(-0.55, 0.55)   # fanned up and out from the base
        return math.sin(a), math.cos(a)

    veins = strokes(veins, True, rng, 11, start, direction, lambda r: r.uniform(70, 150), lambda r: r.uniform(0.6, 1.0), 1.0,
                    lambda s, t: float(np.clip(1.1 - t, 0.2, 1.0)))
    inner -= 0.2 * veins
    fringe = np.clip((edge - 0.82) / 0.12, 0, 1) * (0.7 + 0.3 * smooth_noise(rng, (SIZE, SIZE), 120, 8))
    inner = np.maximum(inner, 0.9 + 0.1 * fringe) * (edge < 1.02) + inner * (edge >= 1.02)
    img = np.where(inside, inner, outside)
    return np.clip(img, 0, 1)


def save(img, path, alpha=None):
    g = (img * 255 + 0.5).astype(np.uint8)
    if alpha is None:
        Image.fromarray(np.stack([g, g, g], axis=-1), 'RGB').save(path)
    else:
        a = (alpha * 255 + 0.5).astype(np.uint8)
        Image.fromarray(np.stack([g, g, g, a], axis=-1), 'RGBA').save(path)
    print('EAR %s: %.2f .. %.2f, mean %.2f' % (os.path.basename(path), img.min(), img.max(), img.mean()))


def preview(grey, alpha, hair):
    """The inside half as the shader draws it on hair of that colour (LiningPale, LiningKeep)."""
    pale = np.array(LINING_PALE) * (1 - LINING_KEEP) + np.array(hair) * LINING_KEEP
    c = np.array(hair)[None, None, :] * (1 - alpha[..., None]) + pale[None, None, :] * alpha[..., None]
    return c * grey[..., None]


def main():
    args = sys.argv[1:]
    if not args:
        raise SystemExit('give the chibi folder')
    folder = args[0]
    (f, fa), m = fluffy(), mouse()
    save(f, os.path.join(folder, 'ear_fluffy.png'), fa)
    save(m, os.path.join(folder, 'ear_mouse.png'))
    if len(args) > 1:
        # The fluffy texture's two halves on dark, ginger and pale hair, then the mouse's grey.
        rows = [np.concatenate([preview(f, fa, h), np.ones((SIZE, 8, 3)), np.repeat(m[..., None], 3, axis=2)], axis=1)
                for h in ((0.2, 0.16, 0.14), (0.85, 0.45, 0.2), (0.75, 0.72, 0.68))]
        both = np.concatenate(rows, axis=0)
        g = (np.clip(both, 0, 1) * 255).astype(np.uint8)
        Image.fromarray(g, 'RGB').resize((both.shape[1] * 2, both.shape[0] * 2), Image.NEAREST).save(args[1])


if __name__ == '__main__':
    main()
