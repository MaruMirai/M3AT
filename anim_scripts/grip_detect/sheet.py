import os, json, sys
from PIL import Image, ImageDraw
t = [e for e in json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'truth.json'))) if e['own']]
extra = json.load(open(sys.argv[1])) if len(sys.argv) > 1 else {}
C = 192
cols = 8
rows = (len(t) + cols - 1) // cols
sheet = Image.new('RGB', (cols * C, rows * (C + 14)), (60, 60, 60))
d = ImageDraw.Draw(sheet)
for k, e in enumerate(t):
    im = Image.open(e['path']).convert('RGBA').resize((C, C), Image.NEAREST)
    bg = Image.new('RGBA', (C, C), (140, 150, 140, 255)); bg.alpha_composite(im)
    x0, y0 = (k % cols) * C, (k // cols) * (C + 14)
    sheet.paste(bg.convert('RGB'), (x0, y0))
    def dot(u, v, col, r=5):
        px, py = x0 + u * C, y0 + (1 - v) * C
        d.ellipse([px - r, py - r, px + r, py + r], outline=col, width=2)
    px, py = x0 + e['u'] * C, y0 + (1 - e['v']) * C
    d.line([px, py, px + e['au'] * 30, py - e['av'] * 30], fill=(255, 0, 0), width=1)
    dot(e['u'], e['v'], (255, 0, 0))
    if e['name'] in extra:
        g = extra[e['name']]
        dot(g['u'], g['v'], (0, 120, 255))
        if 'u2' in g: dot(g['u2'], g['v2'], (0, 255, 255), 4)
    d.text((x0 + 2, y0 + C), e['name'].replace('MeleeWeapon_', 'M_').replace('Gun_', ''), fill=(255, 255, 255))
sheet.save(sys.argv[2] if len(sys.argv) > 2 else 'truth.png')
