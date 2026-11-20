"""Paints the bionic prosthetics' decals, laid on the limb as wounds are (Source/Wounds.cs, BionicDecals).

    python3 anim_scripts/make_prosthetic_decals.py "M3AT: Maru's 3D Animated Toons/Models/prosthetic_decals.png"

Rows of three cells the size of the wound sheet's (CELL px), which Wounds stacks under that sheet when it loads it.
The first row is the bionic limbs':
  0  plate seams: two bevelled seams across the limb (laid with its top up it) and a short one between, rivets along
     them and a raised hatch with vent slots in one bay; across rather than a closed outline, as the limb's sides cut a
     decal off and an outline came out in pieces
  1  a light strip: a slim segmented light in a dark housing with a soft glow, running up and down the cell (laid with
     its top up the limb)
  2  a joint plate: a bevelled round plate with four bolts and a lit lens, all inside the cell
  Each seam is a thin groove a little darker than the bionic plate with a lit edge under it (GROOVE, BEVEL), in the
  archotech traces' fine style: the black 9 px seams before read as marker drawn on the limb.
The second the archotech limbs', after the game's archotech look (pale olive plates, yellow circuit traces):
  0, 1  circuit traces: thin yellow lines turning at right angles, each ending in a round pad
  2  an inset panel: a lighter olive plate with a darker edge and a short trace off it
The third the prosthetic eyes' irises, laid over Facial Animation's own iris under the lids at its size and cut to its
shape (FaceLayers.DrawEyeWounds, IrisOn): the art's iris IRIS across, its outer colour filling the rest of the cell, so
an iris wider one way or bigger than measured is still covered, dark as an outline (the measured radius is the iris's
solid part, its soft edge a little past it); FaceGaze.cs IrisArtShare is IRIS / CELL:
  0  a bionic iris: dark blued steel, a glowing cyan ring round a bright core
  1  an archotech iris: pale olive, a yellow ring with fine spokes round a dark core
  2  a cataract (Aging.cs, WalkFigures.CataractEyes): a milky cloud over the iris, see-through at its edge so the eye's
     own colour shows round it and densest over the pupil, with softer clouds drifting in it; past IRIS a thin haze
The fourth and fifth rows are the eyes' recolourings (RAMPS), used in place of the third row's art wherever Facial
Animation's iris is measured: the iris's own art is drawn again in these colours, so the prosthetic or the cloud keeps
that eye style's drawing (MarusFace.frag.glsl mode 7 with _Recolour). Fourth row: bionic, archotech, cataract (also
blindness); fifth: age's faint haze on an old pawn's eyes (Aging.cs HazeOf), made fainter still by the game as it builds. Each cell is three strips, from its bottom up:
  ramp      across: the colour (and opacity) for the iris art's brightness, 0 at the cell's left to 1 at its right
  ring      across: a glow added over it by the distance from the iris's middle, 0 to 2 iris radii (its alpha the
            strength)
  settings  its red: how far the iris art is blurred first, in iris radii (red / 255 * BLUR_MOST)
Drawn at SCALE times and shrunk, so the lines are smooth. Our own art: plain shapes, no sprites read.
"""
import sys
from PIL import Image, ImageDraw, ImageFilter

CELL = 128     # the wound sheet's cell (key_wounds.py's OUT_CELL)
SCALE = 4
# The bionic plate is LimbPlate's dark blued metal (84, 98, 118): a seam is a thin groove a little darker than it with a
# lighter edge under it, as a bevel, rather than a black line, which read as marker drawn on the skin.
GROOVE = (20, 26, 38, 245)
BEVEL = (176, 196, 222, 225)
GROOVE_WIDTH = 4               # px at CELL
BEVEL_WIDTH = 2.5
HATCH = (104, 120, 142, 255)   # a raised hatch plate, a little lighter than the limb
VENT = (30, 38, 50, 235)
LIGHT = (130, 240, 255, 255)   # the lights' cyan
CORE = (225, 252, 255, 255)
GLOW = (110, 235, 255, 110)
HOUSING = (26, 32, 44, 245)


def px(v):
    return int(round(v * SCALE))


def seam(d, points, width=GROOVE_WIDTH):
    """A bevelled seam along the points: the groove, then its lit lower edge beside it."""
    d.line([(px(x), px(y + width * 0.5 + BEVEL_WIDTH * 0.5)) for x, y in points], fill=BEVEL, width=px(BEVEL_WIDTH), joint='curve')
    d.line([(px(x), px(y)) for x, y in points], fill=GROOVE, width=px(width), joint='curve')


def rivet(d, x, y, r=3.2):
    d.ellipse([px(x - r), px(y - r + 0.8), px(x + r), px(y + r + 0.8)], fill=BEVEL)
    d.ellipse([px(x - r), px(y - r), px(x + r), px(y + r)], fill=GROOVE)


def bevelled_box(d, x0, y0, x1, y1, radius, fill):
    """A raised plate: lit along its bottom and right, a groove round its top and left."""
    d.rounded_rectangle([px(x0), px(y0 + 1.2), px(x1 + 1.2), px(y1 + 1.2)], radius=px(radius), fill=BEVEL)
    d.rounded_rectangle([px(x0 - 1.2), px(y0 - 1.2), px(x1), px(y1)], radius=px(radius), fill=GROOVE)
    d.rounded_rectangle([px(x0), px(y0), px(x1 - 0.6), px(y1 - 0.6)], radius=px(radius), fill=fill)


def panel(d, glow):
    # Two seams across the limb and one between them along it, as the limb's sides cut a decal off and a closed outline
    # came out in pieces; a small hatch with vent slots in one bay, rivets along the seams.
    seam(d, [(0, 30), (CELL, 30)])
    seam(d, [(0, 98), (CELL, 98)])
    seam(d, [(44, 30), (44, 98)], GROOVE_WIDTH - 1)
    for x in (16, 30):
        rivet(d, x, 38)
        rivet(d, x, 90)
    bevelled_box(d, 62, 44, 104, 84, 4, HATCH)
    for y in (54, 62, 70):
        d.rounded_rectangle([px(70), px(y), px(96), px(y + 3)], radius=px(1.5), fill=VENT)
    for x, y in ((66, 48), (100, 48), (66, 80), (100, 80)):
        d.ellipse([px(x - 1.4), px(y - 1.4), px(x + 1.4), px(y + 1.4)], fill=GROOVE)


def strip(d, glow):
    # A slim light along the limb in a dark housing, in segments, with a soft glow round it.
    c, w, top, bottom = CELL / 2, 9, 8, CELL - 8
    gd = ImageDraw.Draw(glow)
    gd.rounded_rectangle([px(c - w - 3), px(top), px(c + w + 3), px(bottom)], radius=px(w + 3), fill=GLOW)
    d.rounded_rectangle([px(c - w), px(top + 1), px(c + w + 1.2), px(bottom + 1.2)], radius=px(w), fill=BEVEL)
    d.rounded_rectangle([px(c - w), px(top), px(c + w), px(bottom)], radius=px(w), fill=HOUSING)
    segments, gap = 4, 5
    inner_top, inner_bottom = top + 6, bottom - 6
    long = (inner_bottom - inner_top - gap * (segments - 1)) / segments
    for k in range(segments):
        y0 = inner_top + k * (long + gap)
        d.rounded_rectangle([px(c - 4), px(y0), px(c + 4), px(y0 + long)], radius=px(4), fill=LIGHT)
        d.rounded_rectangle([px(c - 1.6), px(y0 + 3), px(c + 1.6), px(y0 + long - 3)], radius=px(1.6), fill=CORE)


def joint(d, glow):
    # A round plate over the elbow or knee: a bevelled ring, a lit lens in its middle and four bolts round it; all inside
    # the cell, as lines run off its edge were cut off by the limb's sides.
    c = CELL / 2
    gd = ImageDraw.Draw(glow)
    gd.ellipse([px(c - 26), px(c - 26), px(c + 26), px(c + 26)], fill=GLOW)
    r = 36
    d.ellipse([px(c - r), px(c - r + 1.5), px(c + r), px(c + r + 1.5)], fill=BEVEL)
    d.ellipse([px(c - r - 1.5), px(c - r - 1.5), px(c + r), px(c + r)], fill=GROOVE)
    d.ellipse([px(c - r + 1.5), px(c - r + 1.5), px(c + r - 1.5), px(c + r - 1.5)], fill=HATCH)
    import math
    for k in range(4):
        a = math.pi / 4 + k * math.pi / 2
        rivet(d, c + 27 * math.cos(a), c + 27 * math.sin(a), 3)
    d.ellipse([px(c - 17), px(c - 17), px(c + 17), px(c + 17)], fill=HOUSING)
    d.ellipse([px(c - 12), px(c - 12), px(c + 12), px(c + 12)], fill=LIGHT)
    d.ellipse([px(c - 6), px(c - 6), px(c + 6), px(c + 6)], fill=CORE)


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


def eye_cataract(d, glow):
    c = CELL / 2
    fill(d, (214, 222, 228, 70))
    steps = 14
    for k in range(steps):   # from the edge in, each disc over the last: thicker toward the middle
        r = IRIS + 2 - k * (IRIS + 2 - 7) / (steps - 1)
        disc(d, r, (220 + k, 228 + k // 2, 232, int(105 + 115 * (k / (steps - 1)) ** 1.5)))
    g = ImageDraw.Draw(glow)
    for x, y, r, a in ((-12, -9, 11, 150), (10, -4, 9, 130), (-3, 13, 12, 140), (14, 12, 7, 110)):
        g.ellipse([px(c + x - r), px(c + y - r), px(c + x + r), px(c + y + r)], fill=(244, 246, 248, a))


def blank(d, glow):
    pass


BLUR_MOST = 0.5   # iris radii at a settings red of 255 (MarusFace.frag.glsl's BlurMost: keep in step)

# Per cell: the ramp's stops (brightness, (r, g, b, a)), the ring (middle and width in iris radii, (r, g, b), strength)
# or None, and the blur in iris radii. The dark ends are lifted off black so a mostly dark eye style still reads.
RAMPS = (
    ('bionic', [(0.0, (16, 26, 40, 255)), (0.35, (34, 58, 80, 255)), (0.7, (70, 150, 185, 255)), (1.0, (170, 245, 255, 255))],
     (0.62, 0.12, (80, 220, 255), 0.8), 0.0),
    ('archotech', [(0.0, (42, 40, 20, 255)), (0.4, (110, 110, 70, 255)), (0.75, (175, 172, 112, 255)), (1.0, (240, 235, 170, 255))],
     (0.62, 0.10, (255, 220, 60), 0.7), 0.0),
    # A cataract: a dense milky cloud, whitest over the middle, the eye's own colour faint through it.
    ('cataract', [(0.0, (150, 156, 154, 175)), (0.5, (196, 202, 198, 190)), (1.0, (228, 232, 228, 200))],
     (0.0, 0.55, (60, 62, 60), 0.6), 0.16),
    # Age's haze: the same cloud thinner, the eye showing through (the game fades it in by age).
    ('haze', [(0.0, (170, 176, 174, 140)), (0.5, (205, 210, 206, 155)), (1.0, (232, 235, 232, 170))],
     None, 0.10),
)


def lerp_stops(stops, x):
    for (x0, c0), (x1, c1) in zip(stops, stops[1:]):
        if x <= x1:
            t = 0 if x1 == x0 else (x - x0) / (x1 - x0)
            return tuple(int(round(a + (b - a) * t)) for a, b in zip(c0, c1))
    return stops[-1][1]


def ramp_cell(stops, ring, blur):
    """One recolouring cell (CELL px): settings, ring and ramp strips from the top (the shader reads them from the
    bottom: ramp at a sixth up, ring at a half, settings at five sixths)."""
    import math
    cell = Image.new('RGBA', (CELL, CELL), (0, 0, 0, 0))
    third = CELL // 3
    for x in range(CELL):
        u = (x + 0.5) / CELL
        ramp = lerp_stops(stops, u)
        if ring is None:
            glow = (0, 0, 0, 0)
        else:
            at, width, colour, strength = ring
            g = math.exp(-((u * 2 - at) / width) ** 2) * strength
            glow = colour + (int(round(255 * g)),)
        settings = (int(round(255 * blur / BLUR_MOST)), 0, 0, 255)
        for y in range(CELL):
            cell.putpixel((x, y), settings if y < third else glow if y < CELL - third - 1 else ramp)
    return cell


def main(out):
    sheet = Image.new('RGBA', (CELL * 3, CELL * 5), (0, 0, 0, 0))
    for i, paint in enumerate((panel, strip, joint, circuit_a, circuit_b, inset, eye_bionic, eye_archotech, eye_cataract)):
        big = Image.new('RGBA', (px(CELL), px(CELL)), (0, 0, 0, 0))
        glow = Image.new('RGBA', big.size, (0, 0, 0, 0))
        paint(ImageDraw.Draw(big), glow)
        glow = glow.filter(ImageFilter.GaussianBlur(px(5)))
        cell = Image.alpha_composite(glow, big).resize((CELL, CELL), Image.LANCZOS)
        sheet.paste(cell, (i % 3 * CELL, i // 3 * CELL))
    for i, (name, stops, ring, blur) in enumerate(RAMPS):
        sheet.paste(ramp_cell(stops, ring, blur), (i % 3 * CELL, (3 + i // 3) * CELL))
    sheet.save(out)
    print('wrote', out, sheet.size)


if __name__ == '__main__':
    main(sys.argv[1])
