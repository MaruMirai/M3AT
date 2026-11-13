"""What garment each apparel with none of its own would be drawn as: Source/StandIns.cs's scoring over the defs on disk.

    python3 anim_scripts/stand_ins.py [defs folder ...] [--loo]

Reads every apparel ThingDef in the folders (default: the game's Data/*/Defs and the Workshop mods), resolving ParentName
roughly as the game does (lists added to, the rest overridden), and the export's garment_*.txt for each garment's defs.
Prints each apparel with its stand-ins, best first, and the top three scores. --loo also scores the apparel that do have a
garment as if they had none, which shows what the scoring makes of the game's own clothes. The scores shown are single
garments'; a pair is taken when it scores more (pair_score). Combat Extended's patches are
not applied, so its layers can differ from the game's (CE adds its Backpack layer to some packs). Change this with
StandIns.cs.
"""
import glob, json, os, re, sys
import xml.etree.ElementTree as ET

CHIBI = os.path.join(os.path.dirname(__file__), "..", "M3AT: Maru's 3D Animated Toons", "Models", "chibi")
GAME = os.path.expanduser('~/.local/share/Steam/steamapps/common/RimWorld/Data')
WORKSHOP = os.path.expanduser('~/.local/share/Steam/steamapps/workshop/content/294100')

STAND_IN_LEAST = 0.35
LEFT_OVER_LEAST = 2
OTHER_LAYER_STEP = 0.6
OTHER_HEAD_LAYER = 0.7
NOT_ARMOUR = 0.75
NOT_VACSUIT = 0.3
BOTH_VACSUIT = 0.3
BOTH_NOBLE = 0.05
SAME_TECH = 0.2
TECH_STEP = 2   # levels apart at which SAME_TECH is gone
PAIR_COST = 0.03
SAME_STUFF = 0.03
NAME_WORDS = 0.15
GENERIC_BONUS = 0.03
CHILD_ONLY_SHARE = 0.7
WEIGHT = {'Torso': 3, 'Legs': 3, 'Arms': 2, 'Shoulders': 1, 'Neck': 0.5, 'Waist': 1, 'Hands': 1, 'Feet': 1,
          'UpperHead': 3, 'Eyes': 2, 'Mouth': 2, 'FullHead': 1}
SAME = {'Teeth': 'Mouth', 'LeftShoulder': 'Shoulders', 'RightShoulder': 'Shoulders', 'LeftHand': 'Hands',
        'RightHand': 'Hands', 'MiddleFingers': 'Hands'}
BODY = ['OnSkin', 'Middle', 'Shell']
HEAD = ['Overhead', 'OnHead', 'StrappedHead', 'EyeCover']
TECH = ['Animal', 'Neolithic', 'Medieval', 'Industrial', 'Spacer', 'Ultra', 'Archotech']
NO_STAND_IN = {
    'Collar', 'Blindfold', 'TortureCrown', 'CrownStellic', 'Crown', 'Coronet', 'Gunlink', 'AirwireHeadset',
    'ArrayHeadset', 'IntegratorHeadset', 'Slicecap', 'Burka', 'BodyStrap', 'Bandolier', 'CE_ExoFrame', 'CE_RadioPack',
    'Sash', 'Corset', 'Cape', 'CeremonialCultistMask', 'CultistMask', 'ArmorCataphractPhoenix', 'ArmorCataphractPrestige',
    'ArmorLocust', 'ArmorMarineGrenadier', 'PowerArmorPrestige', 'ArmorReconPrestige', 'MechlordSuit',
    'ArmorHelmetCataphractPrestige', 'ArmorHelmetReconPrestige', 'ArmorMarineHelmetPrestige',
    'ArmorHelmetMechCommander', 'ArmorHelmetMechlordHelmet', 'RobeRoyal', 'ShirtRuffle', 'Shadecone', 'Tailcap',
    'TribalHeaddress', 'PsychicFoilHelmet', 'EltexSkullcap', 'Headwrap', 'WarVeil', 'HatTop', 'HatLadies',
    'CE_SimpleNightVision', 'CE_AdvancedNightVision'}
GENERIC = {'BasicShirt', 'Pants', 'Jacket', 'Duster', 'FlakVest', 'SimpleHelmet', 'AdvancedHelmet', 'Tuque', 'CollarShirt',
           'PowerArmor', 'Parka', 'GasMask', 'ClothMask'}
COMMON_WORDS = {'hat', 'helmet', 'armor', 'armour', 'apparel', 'kid'}


def read_defs(folders):
    nodes = {}
    for r in folders:
        for f in glob.glob(os.path.join(r, '**', '*.xml'), recursive=True):
            if os.sep + 'Patches' + os.sep in f:
                continue
            try:
                root = ET.parse(f).getroot()
            except Exception:
                continue
            for d in root.findall('ThingDef'):
                if d.get('Name'):
                    nodes[('n', d.get('Name'))] = d
                if d.findtext('defName'):
                    nodes[('d', d.findtext('defName'))] = d

    def get(d, path, depth=0):
        v = d.find(path)
        listy = path.split('/')[-1] in ('layers', 'bodyPartGroups', 'thingCategories', 'tags', 'stuffCategories', 'comps')
        parent = d.get('ParentName')
        up = get(nodes[('n', parent)], path, depth + 1) if parent and ('n', parent) in nodes and depth < 20 else None
        if v is None:
            return up
        if listy:
            own = [x.text if x.text and x.text.strip() else x.get('Class') for x in v]
            return own if v.get('Inherit') == 'False' else (up or []) + own
        return v.text
    out = {}
    for (kind, name), d in nodes.items():
        if kind != 'd' or d.get('Abstract') == 'True' or get(d, 'apparel/layers') is None:
            continue
        out[name] = dict(name=name, label=get(d, 'label') or '', layers=get(d, 'apparel/layers') or [],
                         groups=get(d, 'apparel/bodyPartGroups') or [], cats=get(d, 'thingCategories') or [],
                         tags=get(d, 'apparel/tags') or [], tech=get(d, 'techLevel'), stuff=get(d, 'stuffCategories') or [],
                         stage=get(d, 'apparel/developmentalStageFilter') or 'Child, Adult', comps=get(d, 'comps') or [])
    return out


def groups_of(gs):
    out = set()
    for g in gs:
        g = SAME.get(g, g)
        if g == 'FullHead':
            out |= {'UpperHead', 'Eyes', 'Mouth'}
        out.add(g)
    return out


def profile(d):
    return dict(name=d['name'], layers=d['layers'], groups=groups_of(d['groups']),
                armour=any('armor' in c.lower() for c in d['cats']), noble=any('noble' in c.lower() for c in d['cats']),
                vacsuit='Vacsuit' in d['tags'] or 'vacsuit' in d['name'].lower(), child='Adult' not in d['stage'],
                tech=d['tech'], stuff=set(d['stuff']), text=(d['name'] + ' ' + d['label']).lower())


def tokens(def_name):
    n = re.sub(r'^(CE_)?Apparel_', '', def_name)
    return [t.lower() for t in re.findall(r'[A-Z][a-z]+|[a-z]+', n) if len(t) >= 3 and t.lower() not in COMMON_WORDS]


def never(d):
    return any(l == 'Belt' or 'shield' in l.lower() for l in d['layers']) \
        or any('shield' in c.lower() for c in d['cats']) or 'CompProperties_Shield' in d['comps']


def layer_share(a, g):
    best = 0
    for x in a:
        for y in g:
            if x == y:
                return 1
            if x in BODY and y in BODY:
                best = max(best, OTHER_LAYER_STEP ** abs(BODY.index(x) - BODY.index(y)))
            elif x in HEAD and y in HEAD:
                best = max(best, OTHER_HEAD_LAYER)
    return best


def factors(a, g):
    """What a garment's kind makes of its score for a: (times, plus)."""
    times, plus = 1.0, 0.0
    if a['armour'] != g['armour']:
        times *= NOT_ARMOUR
    if g['vacsuit'] and not a['vacsuit']:
        times *= NOT_VACSUIT
    if g['child']:
        times *= CHILD_ONLY_SHARE
    if g['vacsuit'] and a['vacsuit']:
        plus += BOTH_VACSUIT
    if a['noble'] and g['noble']:
        plus += BOTH_NOBLE
    if a['tech'] in TECH and g['tech'] in TECH:
        plus += SAME_TECH * max(0, 1 - abs(TECH.index(a['tech']) - TECH.index(g['tech'])) / TECH_STEP)
    if a['stuff'] & g['stuff']:
        plus += SAME_STUFF
    plus += NAME_WORDS * name_share(a, g)
    if g['garment'] in GENERIC:
        plus += GENERIC_BONUS
    return times, plus


def name_share(a, g):
    return sum(1 for t in g['tokens'] if t in a['text']) / len(g['tokens']) if g['tokens'] else 0


def usable(a, g):
    """A garment of NO_STAND_IN stands in only for apparel with all its name words."""
    return not g['named_only'] or name_share(a, g) == 1


def coverage(groups, covered):
    both = sum(WEIGHT.get(x, 1) for x in groups & covered)
    return both / sum(WEIGHT.get(x, 1) for x in groups | covered) if both else 0


def score(a, groups, g):
    lf = layer_share(a['layers'], g['layers'])
    c = coverage(groups, g['groups'])
    if lf == 0 or c == 0:
        return 0
    times, plus = factors(a, g)
    return c * lf * times + plus


def pair_score(a, g, h):
    """Two garments drawn together for one apparel: their joined coverage at the farther layer, their kinds averaged."""
    lf = min(layer_share(a['layers'], g['layers']), layer_share(a['layers'], h['layers']))
    if lf == 0 or not (a['groups'] & g['groups']) or not (a['groups'] & h['groups']):
        return 0
    (tg, pg), (th, ph) = factors(a, g), factors(a, h)
    return coverage(a['groups'], g['groups'] | h['groups']) * lf * (tg + th) / 2 + (pg + ph) / 2 - PAIR_COST


def main_shared(a, b):
    return bool(a & b & {'Torso', 'Legs', 'Arms', 'UpperHead'})


def stand_ins(a, garments, skip=None):
    """The best garment for a, or the best two together if they score more; [] if none scores STAND_IN_LEAST."""
    pool = [g for g in garments if g['garment'] != skip and usable(a, g)]
    best, best_score = [], STAND_IN_LEAST
    for g in pool:
        s = score(a, a['groups'], g)
        if s >= best_score:
            best, best_score = [g['garment']], s
    for i, g in enumerate(pool):
        for h in pool[i + 1:]:
            if main_shared(g['groups'], h['groups']):
                continue
            s = pair_score(a, g, h)
            if s > best_score:
                first = score(a, a['groups'], g) >= score(a, a['groups'], h)
                best, best_score = [g['garment'], h['garment']] if first else [h['garment'], g['garment']], s
    return best


def main():
    args = [x for x in sys.argv[1:] if not x.startswith('--')]
    loo = '--loo' in sys.argv
    defs = read_defs(args or glob.glob(os.path.join(GAME, '*', 'Defs')) + glob.glob(os.path.join(WORKSHOP, '*')))
    garments, own = [], {}
    for f in sorted(glob.glob(os.path.join(CHIBI, 'garment_*.txt'))):
        name = os.path.basename(f)[len('garment_'):-4]
        lines = {}
        with open(f) as fh:
            for line in fh:
                p = line.split()
                if p and p[0] in ('defs', 'held', 'with_legwear', 'baby_romper'):
                    lines[p[0]] = p[1:]
        for x in lines.get('defs', []):
            own[x] = name
        if set(lines) & {'held', 'with_legwear', 'baby_romper'}:
            continue
        d = next((defs[x] for x in lines.get('defs', []) if x in defs), None)
        if d:
            g = profile(d)
            g.update(garment=name, tokens=tokens(d['name']), named_only=name in NO_STAND_IN)
            garments.append(g)
    for name in sorted(defs):
        d = defs[name]
        if name in own and not loo:
            print(f'{name:45} own {own[name]}')
            continue
        a = profile(d)
        found = [] if never(d) else stand_ins(a, garments, own.get(name))
        top = sorted(((score(a, a['groups'], g), g['garment']) for g in garments if g['garment'] != own.get(name) and usable(a, g)), reverse=True)[:3]
        print(f"{'LOO ' if name in own else ''}{name:45} {'+'.join(found) or '-':28} " + '  '.join(f'{n}:{s:.2f}' for s, n in top))


if __name__ == '__main__':
    main()
