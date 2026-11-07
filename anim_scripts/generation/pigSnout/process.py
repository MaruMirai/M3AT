"""Turns Gemini's snouts into the Nose_Pig gene's 512 px texture for the face patch (SkinnedFigure.DrawRidges).

    python3 process.py [width] [bottom row] [suffix] [squash]

Keys out the green, makes it exactly symmetric, shrinks it to width px across (premultiplied, so no green fringe) and
centres it on the nose. The game multiplies the texture by the skin colour, so each colour is divided by the snout's own
disc colour: its disc comes out white, plain skin on any pawn, and only what is darker than it shows.
Writes out/<name>.png.

Texture pixels at 512. The nose tip's vertex is at (256, 301.7). The triangles just under it, y 301 to 305, are the ledge
under the nose: from IsoRim's 55 degree camera they are stretched 5 to 9 times down the screen against 1.6 times on the
slope above, so art across them smears into long bars and it has to end above them, which also keeps it clear of every
mouth (they start at y 308 at the highest, Experimentals' and the retexture's). The lid covers end lower than that in
the middle at their lowest, but they are skin-coloured and under this layer, so it can overlap them; it must only stay
between the eyes, whose holes come to about x 241 and 271 down to y 285.
"""
import glob
import os
import sys
import numpy as np
from PIL import Image, ImageFilter

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'pigSnout'))
WIDTH = int(sys.argv[1]) if len(sys.argv) > 1 else 28
BOTTOM = int(sys.argv[2]) if len(sys.argv) > 2 else 300    # the row below the snout: above the ledge under the nose
SUFFIX = sys.argv[3] if len(sys.argv) > 3 else ""
SQUASH = float(sys.argv[4]) if len(sys.argv) > 4 else 0.85    # its height over what its art has, against the slope's stretch
GROW = 11       # raw pixels the dark lines are spread by, as a thin one all but vanishes at this size


def keyed(path):
    a = np.asarray(Image.open(path).convert("RGB")).astype(float) / 255
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    green = np.clip((g - np.maximum(r, b)) * 2.2, 0, 1)      # how far toward pure green
    # What is left of each edge pixel once the green is taken out of it.
    return np.stack([r, np.minimum(g, np.maximum(r, b)), b], -1), 1 - green


def symmetric(rgb, alpha):
    """Mirrored about the middle of the snout itself, averaging the two halves."""
    xs = np.where((alpha > 0.5).any(0))[0]
    mid = (xs.min() + xs.max() + 1) // 2
    half = max(mid, alpha.shape[1] - mid)
    pad = half - mid
    wide = lambda v: np.pad(v, [(0, 0), (pad, half * 2 - v.shape[1] - pad)] + [(0, 0)] * (v.ndim - 2))
    rgb, alpha = wide(rgb), wide(alpha)
    pre = rgb * alpha[..., None]
    pre = (pre + pre[:, ::-1]) / 2
    alpha = (alpha + alpha[:, ::-1]) / 2
    return pre / np.maximum(alpha[..., None], 1e-3), alpha


def thickened(rgb, alpha):
    """Spreads the dark lines before shrinking, inside the snout only, so the outer edge doesn't spread into the green."""
    flat = rgb * alpha[..., None] + (1 - alpha[..., None])
    return np.stack([np.asarray(Image.fromarray(np.uint8(flat[..., i] * 255)).filter(ImageFilter.MinFilter(GROW))) / 255.0 for i in range(3)], -1)


def texture(path):
    rgb, alpha = symmetric(*keyed(path))
    rgb = thickened(rgb, alpha)
    ys, xs = np.where(alpha > 0.5)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    # The disc's colour: the commonest bright colour inside it, by the median of what is near the brightest.
    inside = rgb[alpha > 0.9]
    lum = inside.sum(1)
    disc = np.median(inside[lum > np.percentile(lum, 50)], axis=0)
    rgb = np.clip(rgb / disc, 0, 1)
    h = max(1, round((y1 - y0) * WIDTH / (x1 - x0) * SQUASH))
    pre = [Image.fromarray(np.float32((rgb[..., i] * alpha)[y0:y1, x0:x1]), "F").resize((WIDTH, h), Image.LANCZOS) for i in range(3)]
    a = np.clip(np.asarray(Image.fromarray(np.float32(alpha[y0:y1, x0:x1]), "F").resize((WIDTH, h), Image.LANCZOS)), 0, 1)
    out = np.zeros((h, WIDTH, 4), np.uint8)
    for i in range(3):
        out[..., i] = np.clip(np.asarray(pre[i]) / np.maximum(a, 1e-3), 0, 1) * 255
    out[..., 3] = a * 255
    canvas = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    canvas.paste(Image.fromarray(out, "RGBA"), (256 - WIDTH // 2, BOTTOM - h))
    return canvas, (WIDTH, h, BOTTOM - h)


if __name__ == "__main__":
    os.makedirs(os.path.join(HERE, "out"), exist_ok=True)
    for path in sorted(glob.glob(os.path.join(HERE, "raw", "*.png"))):
        name = os.path.splitext(os.path.basename(path))[0]
        im, size = texture(path)
        im.save(os.path.join(HERE, "out", name + SUFFIX + ".png"))
        print(name, "width %d height %d top %d" % size)
