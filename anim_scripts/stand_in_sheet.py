"""A contact sheet of a mod's apparel: each item's worn sprite (south, Male body first) with the garment(s) stand_ins.py
matched it to, for checking the matcher by eye when tuning a mod (dev_notes/apparel.md, Tuning another mod).

    python3 anim_scripts/stand_ins.py > /tmp/standins.txt
    python3 anim_scripts/stand_in_sheet.py <mod folder> /tmp/standins.txt <out.png> [defName prefix ...]

The mod folder is its Workshop folder (its defs read as the game loads them for 1.7, stand_ins.mod_folders). Sprites are
looked for in its Textures folders, then its own Assets.zip (packed as the game's, game_sprite.py). With prefixes, only
apparel whose defName starts with one is shown (a mod's items often share one: CG_, Wolfein_). Items with no sprite
found (a style's path, or one drawn by another node) are shown blank with their match.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import game_sprite  # noqa: E402
import stand_ins as S  # noqa: E402
from PIL import Image, ImageDraw  # noqa: E402

CELL = 150
COLUMNS = 10


def main():
    mod, matches_file, out = sys.argv[1:4]
    prefixes = tuple(sys.argv[4:])
    folders = S.mod_folders(mod)
    defs = S.read_defs(folders)
    index = S.texture_index(folders)
    match = {}
    for line in open(matches_file):
        p = line.split()
        if len(p) >= 2:
            match[p[0]] = p[1]
    names = sorted(n for n in defs if n in match)
    names = [n for n in names if not prefixes or n.startswith(prefixes)]

    def sprite(path):
        if not path:
            return None
        for suffix in ['_Male_south', '_south', '_Female_south', '_Thin_south']:
            where = index.get((path + suffix).lower())
            if where:
                return Image.open(where) if isinstance(where, str) else game_sprite.read(*where)
        return None

    rows = (len(names) + COLUMNS - 1) // COLUMNS
    sheet = Image.new('RGBA', (CELL * COLUMNS, max(1, rows) * (CELL + 26)), (105, 105, 105, 255))
    draw = ImageDraw.Draw(sheet)
    for i, n in enumerate(names):
        x, y = (i % COLUMNS) * CELL, (i // COLUMNS) * (CELL + 26)
        im = sprite(defs[n].get('worn') or defs[n].get('tex'))
        if im is not None:
            im = im.convert('RGBA')
            box = im.getbbox()
            if box:
                im = im.crop(box)
                side = max(im.size)
                square = Image.new('RGBA', (side, side))
                square.paste(im, ((side - im.size[0]) // 2, (side - im.size[1]) // 2))
                sheet.alpha_composite(square.resize((CELL - 10, CELL - 10)), (x + 5, y + 26))
        draw.text((x + 2, y + 1), n[:24], fill='white')
        draw.text((x + 2, y + 12), '-> ' + match[n], fill=(255, 230, 120))
    sheet.save(out)
    print('%s: %d items' % (out, len(names)))


if __name__ == '__main__':
    main()
