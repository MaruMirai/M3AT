"""Makes the style reference for gen.py: Facial Animation's faces, enlarged, with the green the new snout goes on.

    python3 make_refs.py

Writes refs/fa.png and refs/retex.png: a male and a female face side by side on flat green, for each face set.
"""
import os
from PIL import Image

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'pigSnout'))
W = os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/")
EXP = W + "2581693737/Textures/Things/Pawn/FA-Experimentals/"
RET = W + "3271240549/Textures/Things/Pawn/FA-Experimentals/"
GREEN = (0, 255, 0, 255)
SKIN = (236, 200, 170, 255)
BOX = (196, 236, 316, 336)     # round the eyes and mouth on the 512 px texture
ZOOM = 5
W_, H_ = (BOX[2] - BOX[0]) * ZOOM, (BOX[3] - BOX[1]) * ZOOM


def face(root, gender):
    im = Image.new("RGBA", (512, 512), SKIN)
    for p in ("Lids/Normal/%s/normal_cover_south", "Mouth/Normal/%s/open_south", "Brows/Normal/%s/s-shaped_south"):
        layer = Image.open(root + p % gender + ".png").convert("RGBA")
        if "Lids" in p:
            # The lid cover is the skin's colour times this mask, and the game tints it.
            layer = Image.composite(Image.new("RGBA", layer.size, SKIN), Image.new("RGBA", layer.size, (0, 0, 0, 0)), layer.getchannel("A"))
        im.alpha_composite(layer)
    return im.crop(BOX).resize((W_, H_), Image.LANCZOS)


os.makedirs(os.path.join(HERE, "refs"), exist_ok=True)
for name, root in (("fa", EXP), ("retex", RET)):
    sheet = Image.new("RGBA", (W_ * 2 + 60, W_ * 2 + 60), GREEN)
    sheet.alpha_composite(face(root, "Male"), (20, 20))
    sheet.alpha_composite(face(root, "Female"), (W_ + 40, 20))
    sheet.convert("RGB").save(os.path.join(HERE, "refs", name + ".png"))
