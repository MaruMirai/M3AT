"""Puts every mouth in out/ on male and female faces in each pain expression.

    python3 process.py && python3 face_test.py

Writes face_test.png. A mock-up on a flat skin colour, not a game render: no hair colour on the brows,
no eye colour, none of the game's lighting. Layers go in Facial Animation's order (its CreateSlaveNode calls).
"""
import glob
import os
import sys
from PIL import Image, ImageChops, ImageDraw, ImageFilter

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'grittedMouth'))
W = os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/")
EXP = W + "2581693737/Textures/Things/Pawn/FA-Experimentals/"
RET = W + "3271240549/Textures/Things/Pawn/FA-Experimentals/"
SKIN = (236, 200, 170, 255)
FACES = {
    "brave": dict(brow="s-shaped", lid="half"),
    "wince": dict(brow="angled", lid="close"),
    "anguish": dict(brow="s-shaped", lid="close", emotion="gloomy"),
}
GENDERS = ("Male", "Female")
FACE = (170, 225, 342, 345)
PIXELS = (236, 303, 276, 331)
CW, CH = 230, 160


def load(roots, path):
    # Retexture is loaded over Experimentals, so what it lacks comes from Experimentals.
    for r in roots:
        if os.path.exists(r + path):
            return Image.open(r + path).convert("RGBA")
    return None


def tint(c):
    r, g, b, a = c.split()
    return Image.merge("RGBA", [ImageChops.multiply(x, Image.new("L", c.size, v)) for x, v in zip((r, g, b), SKIN[:3])] + [a])


def eyes_through(im, roots, gender, cover):
    # The eye-white layer fills the texture and the lid cover masks it: only the cover's holes show it.
    eye = Image.new("RGBA", (512, 512), (0, 0, 0, 0))
    for p in ("Common/%s/normal_L_south.png", "Common/%s/normal_R_south.png", "Normal/%s/normal_south.png"):
        c = load(roots, "Eyes/" + p % gender)
        if c:
            eye.alpha_composite(c)
    solid = cover.getchannel("A").point(lambda v: 255 if v >= 128 else 0)
    out = solid.copy()
    ImageDraw.floodfill(out, (0, 0), 128)
    im.paste(eye, (0, 0), out.point(lambda v: 255 if v == 0 else 0).filter(ImageFilter.MaxFilter(5)))


def face(roots, gender, mouth, brow, lid, emotion=None):
    im = Image.new("RGBA", (512, 512), SKIN)
    cover = load(roots, "Lids/Normal/%s/%s_cover_south.png" % (gender, lid))
    eyes_through(im, roots, gender, cover)
    im.alpha_composite(tint(cover))
    im.alpha_composite(mouth)
    if emotion:
        im.alpha_composite(load(roots, "Emotions/Normal/%s/%s_south.png" % (gender, emotion)))
    im.alpha_composite(load(roots, "LidOptions/Normal/%s/tear_south.png" % gender))
    im.alpha_composite(load(roots, "Brows/Normal/%s/%s_south.png" % (gender, brow)))
    return im


names = sorted(glob.glob(os.path.join(HERE, "out", "*_south.png")))
cols = ["texture pixels (inset: 1/3 size)"] + ["%s %s" % (g, f) for g in GENDERS for f in FACES]
top = 20
sheet = Image.new("RGB", (CW * len(cols) + 80, top + (CH + 4) * len(names)), (255, 255, 255))
d = ImageDraw.Draw(sheet)
for j, c in enumerate(cols):
    d.text((84 + j * CW, 5), c, fill=(0, 0, 0))
for i, path in enumerate(names):
    name = os.path.basename(path).replace("_south.png", "")
    y = top + i * (CH + 4)
    d.text((6, y + CH // 2 - 6), name, fill=(0, 0, 0))
    tex = Image.open(path).convert("RGBA")
    roots = [RET, EXP] if name.startswith("retex") else [EXP]
    bg = Image.new("RGBA", (512, 512), SKIN)
    bg.alpha_composite(tex)
    sheet.paste(bg.crop(PIXELS).resize((CW, CH), Image.NEAREST).convert("RGB"), (80, y))
    j = 1
    for g in GENDERS:
        for f, a in FACES.items():
            whole = face(roots, g, tex, **a).crop(FACE).convert("RGB")
            sheet.paste(whole.resize((CW, CH), Image.LANCZOS), (80 + j * CW, y))
            if f == "wince" and g == "Male":
                small = whole.resize((57, 40), Image.LANCZOS)
                sheet.paste(small, (80 + CW - 60, y + CH - 43))
            j += 1
sheet.save(os.path.join(HERE, "face_test.png"))
