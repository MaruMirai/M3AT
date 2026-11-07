"""Turns Gemini's mouths into 512 px Facial Animation textures, 

    python3 process.py [width]

Keys out the green, shrinks to width px across (premultiplied, so no green fringe) and centres
it where the mod's mouths sit. Writes out/<name>_south.png. face_test.py shows them on faces.
"""
import glob
import os
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageChops

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'grittedMouth'))
WIDTH = int(sys.argv[1]) if len(sys.argv) > 1 else 28
GROW = {"fa": 15, "retex": 21}     # raw pixels the dark lines are spread by, per style
CENTRE = (256, 317)     # the middle of surprise and open on the mods' textures
W = os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/")
EXP = W + "2581693737/Textures/Things/Pawn/FA-Experimentals/"
RET = W + "3271240549/Textures/Things/Pawn/FA-Experimentals/"
SKIN = (236, 200, 170, 255)


def keyed(path):
    a = np.asarray(Image.open(path).convert("RGB")).astype(float) / 255
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    green = np.clip((g - np.maximum(r, b)) * 2.2, 0, 1)      # how far toward pure green
    alpha = 1 - green
    # What is left of each edge pixel once the green is taken out of it.
    rgb = np.stack([r, np.minimum(g, np.maximum(r, b)), b], -1)
    return rgb, alpha


def thickened(rgb, alpha, grow):
    """Spreads the dark lines before shrinking, as a line a tenth of a final pixel wide would all but vanish."""
    if grow < 3:
        return rgb
    from PIL import ImageFilter
    # Only inside the mouth, with the green counted as white so the outer edge doesn't spread out into it.
    flat = rgb * alpha[..., None] + (1 - alpha[..., None])
    return np.stack([np.asarray(Image.fromarray(np.uint8(flat[..., i] * 255)).filter(ImageFilter.MinFilter(grow))) / 255.0 for i in range(3)], -1)


def texture(path, grow=0):
    rgb, alpha = keyed(path)
    rgb = thickened(rgb, alpha, grow)
    ys, xs = np.where(alpha > 0.5)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    h = max(1, round((y1 - y0) * WIDTH / (x1 - x0)))
    # Shrunk premultiplied, by each channel on its own.
    pre = [Image.fromarray(np.float32((rgb[..., i] * alpha)[y0:y1, x0:x1]), "F").resize((WIDTH, h), Image.LANCZOS) for i in range(3)]
    a = np.clip(np.asarray(Image.fromarray(np.float32(alpha[y0:y1, x0:x1]), "F").resize((WIDTH, h), Image.LANCZOS)), 0, 1)
    out = np.zeros((h, WIDTH, 4), np.uint8)
    for i in range(3):
        out[..., i] = np.clip(np.asarray(pre[i]) / np.maximum(a, 1e-3), 0, 1) * 255
    out[..., 3] = a * 255
    canvas = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    canvas.paste(Image.fromarray(out, "RGBA"), (CENTRE[0] - WIDTH // 2, CENTRE[1] - h // 2))
    return canvas


def tint(c):
    r, g, b, a = c.split()
    return Image.merge("RGBA", [ImageChops.multiply(x, Image.new("L", c.size, v)) for x, v in zip((r, g, b), SKIN[:3])] + [a])


def face(root, gender, mouth):
    """Wincing: closed lids, frowning brows, tears, the mouth given."""
    im = Image.new("RGBA", (512, 512), SKIN)
    im.alpha_composite(tint(Image.open(root + "Lids/Normal/%s/close_cover_south.png" % gender).convert("RGBA")))
    im.alpha_composite(Image.open(EXP + "LidOptions/Normal/%s/tear_south.png" % gender).convert("RGBA"))
    im.alpha_composite(Image.open(root + "Brows/Normal/%s/s-shaped_south.png" % gender).convert("RGBA"))
    im.alpha_composite(mouth)
    return im



if __name__ == "__main__":
    os.makedirs(os.path.join(HERE, "out"), exist_ok=True)
    for path in sorted(glob.glob(os.path.join(HERE, "raw", "*.png"))):
        name = os.path.splitext(os.path.basename(path))[0]
        texture(path, GROW[name.split("_")[0]]).save(os.path.join(HERE, "out", name + "_south.png"))
