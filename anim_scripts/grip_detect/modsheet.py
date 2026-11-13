"""Contact sheet of a mod's weapons (by its Defs): red where its kind's hold puts the fist now (the reference sprite's
place), blue where the detected grip puts it, no blue where the detector falls back to the kind's hold.
python3 modsheet.py <mod folder> <out.png> [limit]"""
import sys, os, json
from PIL import Image, ImageDraw
from detect import detect, category, trusted
import moddefs
HERE = os.path.dirname(os.path.abspath(__file__))
REF = {'gun': 'Gun_AssaultRifle', 'pistol': 'Gun_Autopistol', 'melee': 'MeleeWeapon_Knife', 'melee2': 'MeleeWeapon_Knife', 'bow': 'Bow_Recurve'}
T = {e['name']: e for e in json.load(open(os.path.join(HERE, 'truth.json')))}
refs = {c: (T[n], detect(T[n]['path'], category(T[n]), T[n]['offset'])) for c, n in REF.items()}
defs = moddefs.load(sys.argv[1])
items = sorted(defs.items())[:int(sys.argv[3]) if len(sys.argv) > 3 else 200]
C, cols = 160, 8
sheet = Image.new('RGB', (cols * C, ((len(items) + cols - 1) // cols) * (C + 12)), (60, 60, 60))
d = ImageDraw.Draw(sheet)
for k, (name, e) in enumerate(items):
    c = e['cat']
    x0, y0 = (k % cols) * C, (k // cols) * (C + 12)
    r = detect(e['path'], c, e['offset'])
    im = Image.open(e['path']).convert('RGBA')
    bg = Image.new('RGBA', (C, C), (140, 150, 140, 255)); bg.alpha_composite(im.resize((C, C), Image.NEAREST))
    sheet.paste(bg.convert('RGB'), (x0, y0))
    ref, rr = refs[c]
    def dot(u, v, col, rad=5):
        px, py = x0 + u * C, y0 + (1 - v) * C
        d.ellipse([px - rad, py - rad, px + rad, py + rad], outline=col, width=2)
    dot(ref['u'], ref['v'], (255, 0, 0))
    if trusted(r, c):
        # the reference's fist from its grip in cells, put on this one's grip (same offset assumed)
        dot(r['u'] + (ref['u'] - rr['u']) * ref['size'][0] / e['size'][0], r['v'] + (ref['v'] - rr['v']) * ref['size'][1] / e['size'][1], (0, 90, 255))
    d.text((x0 + 2, y0 + C), (c[0] + ' ' + r['how'][:5] + ' ' + name)[:26], fill=(255, 255, 255))
sheet.save(sys.argv[2])
print(len(items))
