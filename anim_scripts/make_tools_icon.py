# Makes Textures/UI/M3AT_ToolsToggle.png (the play settings toggle) from About/ModIcon.png: python3 make_tools_icon.py ModIcon.png out.png preview.png
import sys
from PIL import Image, ImageFilter, ImageChops
src, out, prev = sys.argv[1:4]
im = Image.open(src).convert("RGBA")
a = im.getchannel("A")
# the icon has a dark outline halo; take the figure as the opaque part that isn't near-black outline
rgb = im.convert("RGB")
lum = rgb.convert("L")
# solid silhouette: alpha, minus the dark outline ring
fig = ImageChops.multiply(a.point(lambda v: 255 if v > 128 else 0), lum.point(lambda v: 255 if v > 18 else 0))
fig = fig.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.MinFilter(3))
# detail lines: darker areas inside the figure (hair strands, eyes, ears' inner) cut out
detail = lum.point(lambda v: 255 if v < 45 else 0)
from PIL import ImageDraw
ear = Image.new("L", im.size, 255); ImageDraw.Draw(ear).rectangle((0,0,191,92), fill=0)
cut = ImageChops.multiply(ImageChops.multiply(fig, detail), ear)
white = ImageChops.subtract(fig, cut)
white = white.filter(ImageFilter.GaussianBlur(0.6))
box = Image.new("L", im.size, 0)
big = Image.new("RGBA", im.size, (255,255,255,0)); big.putalpha(white)
# crop to content, fit in 56px centred on 64
bb = big.getbbox(); big = big.crop(bb)
s = 56 / max(big.size)
big = big.resize((round(big.width*s), round(big.height*s)), Image.LANCZOS)
c = Image.new("RGBA", (64,64), (255,255,255,0))
c.alpha_composite(big, ((64-big.width)//2, (64-big.height)//2))
c.save(out)
bg = Image.new("RGBA", (64,64), (40,40,40,255)); bg.alpha_composite(c); bg.resize((256,256), Image.NEAREST).save(prev)
