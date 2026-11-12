"""Paints the bionic prosthetics' decals, laid on the limb as wounds are (Source/Wounds.cs, BionicDecals).

    python3 anim_scripts/make_prosthetic_decals.py "M3AT: Maru's 3D Animated Toons/Models/prosthetic_decals.png"

Two rows of three cells the size of the wound sheet's (CELL px), which Wounds stacks under that sheet when it loads it.
The first row is the bionic limbs':
  0  plate seams: two seams across the limb (laid with its top up it) and a short one between, rivets beside them;
     across rather than a closed outline, as the limb's sides cut a decal off and an outline came out in pieces
  1  a light strip: a glowing bar in a dark housing, running up and down the cell (laid with its top up the limb)
  2  a joint plate: a ring of seam round a round light, with two short seams off it
The second the archotech limbs', after the game's archotech look (pale olive plates, yellow circuit traces):
  0, 1  circuit traces: thin yellow lines turning at right angles, each ending in a round pad
  2  an inset panel: a lighter olive plate with a darker edge and a short trace off it
The third the prosthetic eyes' irises, laid over Facial Animation's own iris under the lids at its size and cut to its
shape (FaceLayers.DrawEyeWounds, IrisOn): the art's iris IRIS across, its outer colour filling the rest of the cell, so
an iris wider one way or bigger than measured is still covered, dark as an outline (the measured radius is the iris's
solid part, its soft edge a little past it); FaceGaze.cs IrisArtShare is IRIS / CELL:
  0  a bionic iris: dark blued steel, a glowing cyan ring round a bright core
  1  an archotech iris: pale olive, a yellow ring with fine spokes round a dark core
Drawn at SCALE times and shrunk, so the lines are smooth. Our own art: plain shapes, no sprites read.
"""
import sys
from PIL import Image, ImageDraw, ImageFilter

CELL = 128     # the wound sheet's cell (key_wounds.py's OUT_CELL)
SCALE = 4
SEAM = (18, 24, 34, 235)       # a dark seam between plates
SEAM_WIDTH = 9                 # px at CELL
RIVET = (24, 30, 40, 255)
LIGHT = (110, 235, 255, 255)   # the lights' cyan
GLOW = (110, 235, 255, 120)
HOUSING = (20, 26, 36, 255)


def px(v):
    return int(round(v * SCALE))


def panel(d, glow):
    for y in (26, CELL - 26):
        d.line([px(0), px(y), px(CELL), px(y)], fill=SEAM, width=px(SEAM_WIDTH))
        for x in (24, CELL - 24):
            yy = y + (14 if y < CELL / 2 else -14)
            d.ellipse([px(x - 6), px(yy - 6), px(x + 6), px(yy + 6)], fill=RIVET)
    d.line([px(CELL / 2), px(26), px(CELL / 2), px(CELL - 26)], fill=SEAM, width=px(SEAM_WIDTH - 3))


def strip(d, glow):
    w, top, bottom = 18, 8, CELL - 8
    c = CELL / 2
    gd = ImageDraw.Draw(glow)
    gd.rounded_rectangle([px(c - w), px(top - 6), px(c + w), px(bottom + 6)], radius=px(w), fill=GLOW)
    d.rounded_rectangle([px(c - w), px(top), px(c + w), px(bottom)], radius=px(w), fill=HOUSING)
    d.rounded_rectangle([px(c - w + 7), px(top + 8), px(c + w - 7), px(bottom - 8)], radius=px(w - 7), fill=LIGHT)


def joint(d, glow):
    c = CELL / 2
    r = 40
    gd = ImageDraw.Draw(glow)
    gd.ellipse([px(c - 30), px(c - 30), px(c + 30), px(c + 30)], fill=GLOW)
    d.ellipse([px(c - r), px(c - r), px(c + r), px(c + r)], outline=SEAM, width=px(SEAM_WIDTH))
    d.ellipse([px(c - 20), px(c - 20), px(c + 20), px(c + 20)], fill=HOUSING)
    d.ellipse([px(c - 14), px(c - 14), px(c + 14), px(c + 14)], fill=LIGHT)
    for y0, y1 in ((0, c - r), (c + r, CELL)):
        d.line([px(c), px(y0), px(c), px(y1)], fill=SEAM, width=px(SEAM_WIDTH))


TRACE = (232, 226, 58, 255)    # the archotech circuit traces' yellow
TRACE_WIDTH = 4
PAD = 8                         # a trace's end pad's radius
INSET = (214, 214, 168, 235)   # a lighter inset plate
INSET_EDGE = (120, 122, 84, 220)


def trace(d, points):
    d.line([(px(x), px(y)) for x, y in points], fill=TRACE, width=px(TRACE_WIDTH), joint='curve')
    x, y = points[-1]
    d.ellipse([px(x - PAD), px(y - PAD), px(x + PAD), px(y + PAD)], fill=TRACE)


def circuit_a(d, glow):
    trace(d, [(30, 0), (30, 46), (70, 46), (70, 96)])
    trace(d, [(100, 0), (100, 30), (112, 30), (112, 66)])


def circuit_b(d, glow):
    trace(d, [(64, 0), (64, 40), (28, 40), (28, 104)])
    trace(d, [(96, 128), (96, 80)])


def inset(d, glow):
    d.rectangle([px(26), px(30), px(102), px(80)], fill=INSET, outline=INSET_EDGE, width=px(4))
    trace(d, [(64, 80), (64, 102), (86, 102), (86, 112)])


IRIS = 34   # the iris's radius, px at CELL, which the game scales to Facial Animation's iris's (FaceGaze.cs IrisArtShare)


def ring(d, r, width, fill):
    c = CELL / 2
    d.ellipse([px(c - r), px(c - r), px(c + r), px(c + r)], outline=fill, width=px(width))


def disc(d, r, fill):
    c = CELL / 2
    d.ellipse([px(c - r), px(c - r), px(c + r), px(c + r)], fill=fill)


def fill(d, colour):
    d.rectangle([0, 0, px(CELL), px(CELL)], fill=colour)


def eye_bionic(d, glow):
    fill(d, (14, 18, 26, 255))   # past IRIS, out to Facial Animation's iris's soft edge: a dark outline, as its own iris has
    disc(d, IRIS, (58, 68, 86, 255))
    disc(d, IRIS - 5, (20, 28, 40, 255))
    ring(d, IRIS - 9, 8, (90, 220, 255, 255))
    disc(d, 11, (200, 250, 255, 255))


def eye_archotech(d, glow):
    c = CELL / 2
    fill(d, (44, 46, 30, 255))
    disc(d, IRIS, (150, 152, 104, 255))
    disc(d, IRIS - 5, (120, 122, 80, 255))
    ring(d, IRIS - 8, 6, TRACE)
    import math
    for k in range(8):
        a = k * math.pi / 4
        d.line([px(c + 12 * math.cos(a)), px(c + 12 * math.sin(a)), px(c + (IRIS - 11) * math.cos(a)), px(c + (IRIS - 11) * math.sin(a))],
               fill=TRACE, width=px(2))
    disc(d, 11, (34, 36, 26, 255))


def blank(d, glow):
    pass


def main(out):
    sheet = Image.new('RGBA', (CELL * 3, CELL * 3), (0, 0, 0, 0))
    for i, paint in enumerate((panel, strip, joint, circuit_a, circuit_b, inset, eye_bionic, eye_archotech, blank)):
        big = Image.new('RGBA', (px(CELL), px(CELL)), (0, 0, 0, 0))
        glow = Image.new('RGBA', big.size, (0, 0, 0, 0))
        paint(ImageDraw.Draw(big), glow)
        glow = glow.filter(ImageFilter.GaussianBlur(px(5)))
        cell = Image.alpha_composite(glow, big).resize((CELL, CELL), Image.LANCZOS)
        sheet.paste(cell, (i % 3 * CELL, i // 3 * CELL))
    sheet.save(out)
    print('wrote', out, sheet.size)


if __name__ == '__main__':
    main(sys.argv[1])
