"""A mod's weapons from its Defs (1.6 folder, else the root): defName -> texture file, drawSize, equippedAngleOffset,
category (gun, pistol, melee, bow), parents taken through the game's own Defs too, as the game does."""
import os, re, glob
import xml.etree.ElementTree as ET
HEAVY = 1.8   # WeaponTypes.HeavyMelee: kg from which a melee weapon is two-handed (keep in step)
GAME = os.path.expanduser('~/.local/share/Steam/steamapps/common/RimWorld/Data')

def _roots(folder):
    """The folders the game loads for 1.6 (its loadFolders' usual layout), else 1.5's, with Common and the root."""
    ver = '1.6' if os.path.isdir(os.path.join(folder, '1.6')) else '1.5'
    for sub in (ver, os.path.join(ver, 'Biotech'), 'Common', ''):
        if os.path.isdir(os.path.join(folder, sub)):
            yield os.path.join(folder, sub)

def load(folder):
    named, things = {}, []
    files = glob.glob(os.path.join(GAME, '*', 'Defs', '**', '*.xml'), recursive=True)
    mod = []
    for r in _roots(folder):
        if os.path.isdir(os.path.join(r, 'Defs')):
            mod += glob.glob(os.path.join(r, 'Defs', '**', '*.xml'), recursive=True)
    for path in files + mod:
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError):
            continue
        for el in root.iter('ThingDef'):
            if el.get('Name'):
                named[el.get('Name')] = el
            if el.findtext('defName') and path in mod:
                things.append(el)

    def chain(el):
        seen = set()
        while el is not None and id(el) not in seen:
            seen.add(id(el)); yield el
            el = named.get(el.get('ParentName'))

    def find(el, tag):
        for e in chain(el):
            v = e.find(tag)
            if v is not None and v.text:
                return v.text.strip()
        return None

    texroots = [os.path.join(r, 'Textures') for r in _roots(folder)]
    out = {}
    for el in things:
        names = ' '.join((e.get('Name') or '') + ' ' + (e.get('ParentName') or '') for e in chain(el))
        if 'Apparel' in names or find(el, 'apparel/layers/li') or ('Weapon' not in names and 'Gun' not in names and el.find('verbs') is None):
            continue
        if find(el, 'equipmentType') not in (None, 'Primary') and 'Weapon' not in names:
            continue
        tex = find(el, 'graphicData/texPath')
        if not tex:
            continue
        path = None
        for tr in texroots:
            for cand in (os.path.join(tr, tex + '.png'), *sorted(glob.glob(os.path.join(tr, tex, '*.png')))):
                if os.path.isfile(cand):
                    path = cand; break
            if path: break
        if not path:
            continue
        size = find(el, 'graphicData/drawSize') or '(1, 1)'
        nums = [float(v) for v in re.findall(r'-?\d+\.?\d*', size)] or [1.0]
        name = el.findtext('defName').strip()
        melee = 'Melee' in names or (el.find('verbs') is None and find(el, 'verbs/li/verbClass') is None and 'Gun' not in names)
        low = (name + ' ' + tex).lower()
        cat = 'melee' if melee else 'bow' if re.search(r'bow(?!l)', low) and 'crossbow' not in low else \
            'pistol' if re.search(r'pistol|revolver|handgun', low) else 'gun'
        mass = float(find(el, 'statBases/Mass') or 0.0)
        if cat == 'melee' and mass >= HEAVY:
            cat = 'melee2'
        out[name] = dict(path=path, size=(nums[0], nums[-1]), offset=float(find(el, 'equippedAngleOffset') or 0.0), cat=cat)
    return out

if __name__ == '__main__':
    import sys
    for n, e in sorted(load(sys.argv[1]).items()):
        print('%-40s %-6s %6.0f %s %s' % (n, e['cat'], e['offset'], e['size'], os.path.relpath(e['path'], sys.argv[1])))
