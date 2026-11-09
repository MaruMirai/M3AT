"""Finds a hairstyle's (or beard's) three sprites and writes them as the game hands them to HairBuilder.cs, for
anim_scripts/hairbuild/ (the builder run outside the game, ./view_hair.sh).

    python3 anim_scripts/hair_sprites.py <out prefix> <style> [--beard] [--head]

<style> is the HairDef's texPath less Things/Pawn/Humanlike/Hairs/ (Beards/ with --beard), as the cache names it
(Bob, Hair/AFUf044), a whole texPath, or a path to a sprite less its _south.png. It is looked for in this order: as a
path; in the active mods' Textures folders (ModsConfig.xml, the last loaded first, as the game takes a retexture
over its own; each mod's folders as its LoadFolders.xml picks them); in the game's and the expansions' Assets.zip (game_sprite.py's
decoding). --head reads the head the builder fits hair to (HairBuilder's
HeadSprites, the first found). Writes <out prefix>_south.rgba, _east.rgba and _north.rgba: SIZE x SIZE RGBA, rows top
first, as HairBuilder.Pixels has them. A missing east sprite is the west one mirrored, and a beard's north one is left
empty, as HairBuilder.Read does. Each is resampled as IsoRim's SpritePixels.Sample does: the mip level nearest the size
(each level a 2x2 box of the one above), then bilinear with clamped edges and 8-bit weights, so the alpha cut lands
where the game's does to within a pixel. Prints the files the sprites came from."""
import glob, io, os, re, struct, subprocess, sys, zipfile
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import game_sprite

SIZE = 256   # HairBuilder.Size
GAME = os.path.expanduser('~/.local/share/Steam/steamapps/common/RimWorld')
WORKSHOP = os.path.expanduser('~/.local/share/Steam/steamapps/workshop/content/294100')
HEADS = ['Things/Pawn/Humanlike/Heads/Female/Female_Average_Normal',   # HairBuilder.HeadSprites
         'Things/Pawn/Humanlike/Heads/Male/Male_Average_Normal']
BEARD_HEADS = HEADS[::-1]

def zips():
    return [os.path.join(GAME, 'Assets.zip')] + sorted(glob.glob(os.path.join(GAME, 'Data', '*', 'Assets.zip')))

def from_zip(path, tex):
    """The sprite at texPath tex in an Assets.zip, or None."""
    want = 'Resources/Textures/' + tex + '.png.dds.split'
    with open(path, 'rb') as f:
        try:
            infos = zipfile.ZipFile(f).infolist()
        except zipfile.BadZipFile:
            return None
        for i in infos:
            if i.filename != want and i.filename != want.replace('.png.', '.psd.'):
                continue
            f.seek(i.header_offset)
            head = f.read(30)
            name_len, extra_len = struct.unpack('<HH', head[26:30])
            f.seek(i.header_offset + 30 + name_len + extra_len)
            data = f.read(i.compress_size)
            if i.compress_type == 93:
                data = subprocess.run(['zstd', '-d', '-c'], input=data, capture_output=True, check=True).stdout
            im = game_sprite.image(data)
            return (im, path + ':' + i.filename) if im is not None else None
    return None

CONFIG = os.path.expanduser('~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml')

def active_ids():
    """The active mods' packageIds, lower case, in load order (ModsConfig.xml)."""
    if not os.path.exists(CONFIG):
        return []
    block = open(CONFIG).read().split('<activeMods>', 1)[1].split('</activeMods>', 1)[0]
    return [m.strip().lower() for m in re.findall(r'<li>([^<]+)</li>', block)]

def active_mods():
    """The active mods' folders, last loaded first (the game takes a texture from the last mod that has it), from
    ModsConfig.xml's load order and each mod's About.xml packageId. The game's own packs aren't folders here."""
    roots = {}
    for root in glob.glob(os.path.join(WORKSHOP, '*')) + glob.glob(os.path.join(GAME, 'Mods', '*')):
        about = os.path.join(root, 'About', 'About.xml')
        if os.path.exists(about):
            m = re.search(r'<packageId>\s*([^<\s]+)', open(about, errors='replace').read())
            if m:
                roots.setdefault(m.group(1).lower(), root)
    return [roots[m] for m in reversed(active_ids()) if m in roots]

GAME_VERSION = (1, 7)

def load_folders(root):
    """A mod's folders the game loads, in order (a later one's file replaces an earlier one's), as the game picks them:
    LoadFolders.xml's list for the newest version up to the game's, each <li> kept if any mod of its IfModActive list
    is active and none of its IfModNotActive list; without one, the root, Common and the newest version folder."""
    active = set(active_ids())
    path = os.path.join(root, 'LoadFolders.xml')
    if os.path.exists(path):
        text = open(path, errors='replace').read()
        best = None
        for m in re.finditer(r'<v(\d+)\.(\d+)>(.*?)</v\1\.\2>', text, re.S):
            v = (int(m.group(1)), int(m.group(2)))
            if v <= GAME_VERSION and (best is None or v > best[0]):
                best = (v, m.group(3))
        if best:
            folders = []
            for attrs, folder in re.findall(r'<li([^>]*)>([^<]*)</li>', best[1]):
                want = re.search(r'IfModActive="([^"]*)"', attrs)
                avoid = re.search(r'IfModNotActive="([^"]*)"', attrs)
                if want and not any(x.strip().lower() in active for x in want.group(1).split(',')):
                    continue
                if avoid and any(x.strip().lower() in active for x in avoid.group(1).split(',')):
                    continue
                folders.append(os.path.join(root, folder.strip().strip('/')))
            return folders
    versions = sorted((tuple(map(int, d.split('.'))), d) for d in os.listdir(root)
                      if re.fullmatch(r'\d+\.\d+', d) and tuple(map(int, d.split('.'))) <= GAME_VERSION)
    return [root, os.path.join(root, 'Common')] + ([os.path.join(root, versions[-1][1])] if versions else [])

MOD_FILES = None

def mod_textures():
    """For each active mod, last loaded first: every PNG in the Textures folder of each folder it loads, by its path from
    Textures/ less .png, a later folder's replacing an earlier one's."""
    global MOD_FILES
    if MOD_FILES is None:
        MOD_FILES = []
        for root in active_mods():
            files = {}
            for folder in load_folders(root):
                tex = os.path.join(folder, 'Textures')
                for dirpath, dirs, names in os.walk(tex, followlinks=True):
                    for name in names:
                        if name.lower().endswith('.png'):
                            full = os.path.join(dirpath, name)
                            files[os.path.relpath(full, tex)[:-4]] = full
            MOD_FILES.append(files)
    return MOD_FILES

def find(tex):
    """A sprite by texPath (with its direction): (image, where) or None. An active mod's own replaces the game's."""
    for files in mod_textures():
        if tex in files:
            return Image.open(files[tex]), files[tex]
    for z in zips():
        got = from_zip(z, tex)
        if got:
            return got
    return None

def read_views(base, beard):
    """The three views of the sprite set at base: (south, east, north) images and where each came from, or None."""
    def one(view):
        if os.path.exists(base + '_' + view + '.png'):
            return Image.open(base + '_' + view + '.png'), base + '_' + view + '.png'
        return find(base + '_' + view) if not base.startswith('/') else None
    south, east, north = one('south'), one('east'), None if beard else one('north')
    mirror = False
    if east is None:
        east = one('west')
        mirror = True
    if south is None or east is None or (north is None and not beard):
        return None
    return south, east, north, mirror

def mips(a):
    """Each level a 2x2 box of the one above, rounded as IsoRim's Halve."""
    levels = [a]
    while levels[-1].shape[0] > 1 or levels[-1].shape[1] > 1:
        p = levels[-1].astype(np.int32)
        h, w = p.shape[:2]
        y0 = np.minimum(np.arange(max(1, h // 2)) * 2, h - 1)
        y1 = np.minimum(y0 + 1, h - 1)
        x0 = np.minimum(np.arange(max(1, w // 2)) * 2, w - 1)
        x1 = np.minimum(x0 + 1, w - 1)
        s = p[y0][:, x0] + p[y0][:, x1] + p[y1][:, x0] + p[y1][:, x1]
        levels.append(((s + 2) >> 2).astype(np.uint8))
    return levels

def sample(im, mirror=False):
    """SIZE x SIZE RGBA, rows top first, as SpritePixels.Sample then HairBuilder.Pixels."""
    a = np.asarray(im.convert('RGBA'))[::-1]   # rows bottom first, as the engine's levels
    h, w = a.shape[:2]
    ratio = max(w, h) / SIZE
    levels = mips(a)
    mip = 0 if ratio <= 1 else int(np.clip(round(np.log2(ratio)), 0, len(levels) - 1))
    lv = levels[mip].astype(np.float64)
    lh, lw = lv.shape[:2]
    def axis(n_src):
        t = (np.arange(SIZE) + 0.5) / SIZE * n_src - 0.5
        i0 = np.floor(t).astype(int)
        f = np.round((t - i0) * 256) / 256
        return np.clip(i0, 0, n_src - 1), np.clip(i0 + 1, 0, n_src - 1), f
    ya, yb, fy = axis(lh)
    xa, xb, fx = axis(lw)
    fx, fy = fx[None, :, None], fy[:, None, None]
    c = (lv[ya][:, xa] * (1 - fx) * (1 - fy) + lv[ya][:, xb] * fx * (1 - fy)
         + lv[yb][:, xa] * (1 - fx) * fy + lv[yb][:, xb] * fx * fy)
    out = np.clip(np.ceil(c - 0.5 - 1e-4), 0, 255).astype(np.uint8)[::-1]   # back to rows top first
    return out[:, ::-1] if mirror else out

def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    beard, head = '--beard' in sys.argv, '--head' in sys.argv
    if len(args) != (1 if head else 2):
        raise SystemExit(__doc__)
    out = args[0]
    if head:
        tries = BEARD_HEADS if beard else HEADS
    else:
        style = args[1]
        folder = 'Things/Pawn/Humanlike/' + ('Beards/' if beard else 'Hairs/')
        tries = [os.path.abspath(style)] if os.path.exists(style + '_south.png') else [folder + style, style]
    for base in tries:
        got = read_views(base, beard)
        if got:
            break
    else:
        raise SystemExit('no sprites for %s (looked for %s)' % (args[-1], ', '.join(t + '_south' for t in tries)))
    south, east, north, mirror = got
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    for view, item, flip in (('south', south, False), ('east', east, mirror), ('north', north, False)):
        px = sample(item[0], flip) if item else np.zeros((SIZE, SIZE, 4), np.uint8)
        with open(out + '_' + view + '.rgba', 'wb') as f:
            f.write(px.tobytes())
        print('SPRITE %s %s' % (view, (item[1] + (' (west, mirrored)' if flip else '')) if item else '(empty)'))

if __name__ == '__main__':
    main()
