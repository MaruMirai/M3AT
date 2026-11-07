"""Makes the style references for gen.py: the mods' own mouths, enlarged on flat green.

    python3 make_refs.py

Writes refs/fa.png and refs/retex.png, each a row of the mouths the new one has to sit beside.
"""
import os
from PIL import Image

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'grittedMouth'))
W = os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/")
EXP = W + "2581693737/Textures/Things/Pawn/FA-Experimentals/Mouth/"
RET = W + "3271240549/Textures/Things/Pawn/FA-Experimentals/Mouth/"
GREEN = (0, 255, 0, 255)
BOX = (232, 300, 280, 332)     # round the mouth on the 512 px texture
ZOOM = 10

SETS = {
    "fa": [EXP + p for p in ("Normal/Male/open", "Normal/Male/surprise", "Normal/Male/sad", "Normal/Female/open", "Shark/Female/open")],
    "retex": [RET + p for p in ("Normal/Female/open", "Smiling/Female/smile", "Smirk/Female/smile", "Grumpy/Female/open", "Impish/Female/open")],
}

for name, paths in SETS.items():
    w, h = (BOX[2] - BOX[0]) * ZOOM, (BOX[3] - BOX[1]) * ZOOM
    sheet = Image.new("RGBA", (w * len(paths), h), GREEN)
    for i, p in enumerate(paths):
        im = Image.open(p + "_south.png").convert("RGBA").crop(BOX).resize((w, h), Image.LANCZOS)
        sheet.alpha_composite(im, (i * w, 0))
    sheet.convert("RGB").save(os.path.join(HERE, "refs", name + ".png"))
