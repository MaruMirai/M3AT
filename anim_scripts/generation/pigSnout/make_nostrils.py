"""The Nose_Pig gene's nostrils alone, with no disc round them, as the 512 px texture for the face patch
(SkinnedFigure.DrawRidges, ridges_snout.png).

    python3 make_nostrils.py [out.png]

Two upright ovals, drawn here rather than cut from Gemini's art (raw/), which at this size came out as a ring with two
blobs. Their colour is the art's nostril over its disc (process.py's division), so with the game multiplying by the skin
colour they are the skin darkened, on any pawn.

Texture pixels at 512. With the Nose_Pig key on, the snout's flat end runs from the nose tip's vertex (256, 301.7) down to
(256, 331.6), its sides at x 244 and 268 (y 325.7). The nostrils sit on its upper half; the mouth is moved down onto its
lower half while the gene is active (FaceLayers.MouthRaisePig).
"""
import os
import sys

import numpy as np
from PIL import Image

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'pigSnout'))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "out", "nostrils.png")
CENTRE_Y = 307.0    # the nostrils' middle row
APART = 6.5         # each nostril's middle from the snout's middle, x
HALF_W = 2.6        # each oval's half width and half height
HALF_H = 4.2
DARK = np.array([76, 38, 33]) / np.array([236, 198, 161])    # the art's nostril over its disc
RIM = 0.35          # the outer share of each oval eased toward the skin, so it isn't a hard blob
SAMPLES = 8         # supersampling per pixel each way


def main():
    n = 512 * SAMPLES
    ys = (np.arange(n) + 0.5) / SAMPLES
    x0, x1, y0, y1 = 240, 272, 300, 318    # only this window can have anything in it
    sx = slice(x0 * SAMPLES, x1 * SAMPLES)
    sy = slice(y0 * SAMPLES, y1 * SAMPLES)
    gy, gx = np.meshgrid(ys[sy], ys[sx], indexing="ij")
    alpha = np.zeros(gx.shape)
    shade = np.zeros(gx.shape)
    for side in (-1, 1):
        r = np.sqrt(((gx - 256 - side * APART) / HALF_W) ** 2 + ((gy - CENTRE_Y) / HALF_H) ** 2)
        inside = r <= 1
        alpha = np.maximum(alpha, inside.astype(float))
        # 0 in the middle, 1 at the edge, only over the rim.
        shade = np.where(inside, np.clip((r - (1 - RIM)) / RIM, 0, 1), shade)
    colour = DARK[None, None, :] * (1 - shade[..., None] * 0.6) + shade[..., None] * 0.6
    h, w = alpha.shape[0] // SAMPLES, alpha.shape[1] // SAMPLES
    a = alpha.reshape(h, SAMPLES, w, SAMPLES).mean((1, 3))
    pre = (colour * alpha[..., None]).reshape(h, SAMPLES, w, SAMPLES, 3).mean((1, 3))
    rgb = pre / np.maximum(a[..., None], 1e-6)
    out = np.zeros((512, 512, 4), np.uint8)
    out[y0:y1, x0:x1, :3] = np.clip(rgb, 0, 1) * 255
    out[y0:y1, x0:x1, 3] = np.clip(a, 0, 1) * 255
    # Fully clear pixels keep the dark colour, so filtering at the edge doesn't pull in black or white.
    out[..., :3] = np.where(out[..., 3:] == 0, (DARK * 255).astype(np.uint8), out[..., :3])
    Image.fromarray(out, "RGBA").save(OUT)
    print("wrote", OUT)


main()
