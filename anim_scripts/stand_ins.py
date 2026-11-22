"""What garment each apparel with none of its own would be drawn as: Source/StandIns.cs's scoring over the defs on disk.

    python3 anim_scripts/stand_ins.py [defs folder ...] [--loo]

Reads every apparel ThingDef in the folders (default: the game's Data/*/Defs and the Workshop mods, each mod only as the
game loads it for 1.7: its LoadFolders.xml's newest version up to 1.7, else its root and newest version folder, mod_folders), resolving ParentName
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
HEAD_NOUN = 0.5   # more for a garment the apparel's label ends on (head_noun)
GENERIC_BONUS = 0.03
CHILD_ONLY_SHARE = 0.7
NARROW_SHARE = 0.7
# A narrower kind of a plain garment: behind it on coverage alone, unless the apparel's name says so (StandIns.cs).
NARROW = {'CropTop', 'Tanktop', 'Shorts', 'ShortShorts'}
WEIGHT = {'Torso': 3, 'Legs': 3, 'Arms': 2, 'Shoulders': 1, 'Neck': 0.5, 'Waist': 1, 'Hands': 1, 'Feet': 1,
          'UpperHead': 3, 'Eyes': 2, 'Mouth': 2, 'FullHead': 1}
SAME = {'Teeth': 'Mouth', 'LeftShoulder': 'Shoulders', 'RightShoulder': 'Shoulders', 'LeftHand': 'Hands',
        'RightHand': 'Hands', 'MiddleFingers': 'Hands'}
# A mod's layer is taken as the body layer it counts as (under, SkinnedFigure.BodyLayerOf): 'Underwear' for any
# underwear layer (UnderWhere's Underwear and UnderwearTop, Kiiro's Kiiro_Underwear), OnSkin for Kiiro's trousers layer.
BODY = ['Underwear', 'OnSkin', 'Middle', 'Shell']
LAYER_ORDER = {'OnSkin': 0}   # each ApparelLayerDef's drawOrder, as the Defs are read
LAYER_LABEL = {}              # and its label
# A body layer, then the words in a layer's name or label that make it that layer (SkinnedFigure.BodyLayerWords).
BODY_LAYER_WORDS = [('OnSkin', ('onskin', 'skin', 'pants', 'trouser', 'skirt', 'shorts', 'legwear')),
                    ('Middle', ('middle',)), ('Shell', ('shell', 'outer'))]
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
    'CE_SimpleNightVision', 'CE_AdvancedNightVision', 'Scarf'}
# Other words naming the same thing as a NO_STAND_IN garment's name word, and phrases holding it that don't (StandIns.cs).
NAME_ALIKE = {'frame': ('exoskelet', 'exosuit'),   # CE's exoframe is any exoskeleton (StandIns.cs)
              'radio': ('vox', 'comms', 'transceiver', 'communicat'),   # CE's radio pack is any pack with a radio
              'scarf': ('neckerchief', 'kerchief', 'muffler', 'shemagh', 'keffiyeh', 'snood'),
              'short': ('hotpant', 'hot pant'),   # hotpants are the short shorts, not the shorts
              'crop': ('halter', 'tube top'),
              'tanktop': ('tank top', 'singlet', 'camisole')}   # a tank top is the tank top's, not the crop top's
NAME_NOT = {'scarf': ('headscarf', 'head scarf'), 'pants': ('hotpant', 'underpant')}
# A NO_STAND_IN garment named by the apparel is the thing itself: coverage at least NAMED_COVER, NAMED_BONUS ahead.
NAMED_COVER = 0.5
NAMED_BONUS = 1.0
GENERIC = {'BasicShirt', 'Pants', 'Jacket', 'Duster', 'FlakVest', 'SimpleHelmet', 'AdvancedHelmet', 'Tuque', 'CollarShirt',
           'PowerArmor', 'Parka', 'GasMask', 'ClothMask', 'CE_Backpack'}
# Garments over the face, standing in only for apparel named as one (MASK_WORDS): mods give plain hats FullHead.
MASKS = {'GasMask', 'ClothMask', 'WarMask', 'VisageMask', 'WarVeil', 'CultistMask', 'CeremonialCultistMask'}
MASK_WORDS = ('mask', 'respirator', 'balaclava', 'veil')
COMMON_WORDS = {'hat', 'helmet', 'armor', 'armour', 'apparel', 'kid'}


# Open-fronted garments stand in only for apparel whose front sprite is open down the middle (Source/OpenFronts.cs):
# the clear gap up from its hem at least OPEN_FRONT_LEAST of its height.
OPEN_FRONTS = {'Duster', 'LabCoat'}
OPEN_FRONT_LEAST = 0.45
OPEN_SIZE = 128
OPEN_HALF_WIDTH = 1
OPEN_BODIES = ['Male', 'Female', 'Thin', 'Hulk', 'Fat']
TEXTURES = []   # (texture path without extension, lower case) -> where it is: a file, or (zip, info); filled by main
OPEN_SHARES = {}


def texture_index(folders):
    """Every texture the game could load, by its path under Textures (lower case, no extension): each mod folder's
    Textures, then the game's Assets.zip files; the first found wins, as mods load after the game and override it."""
    sys.path.insert(0, os.path.dirname(__file__))
    import game_sprite
    index = {}
    for folder in reversed(folders):   # (later mods override earlier ones)
        t = os.path.join(folder, 'Textures')
        for f in glob.glob(os.path.join(t, '**', '*.png'), recursive=True):
            index.setdefault(os.path.relpath(f, t)[:-4].replace(os.sep, '/').lower(), f)
    root = os.path.dirname(GAME)
    for z in [os.path.join(root, 'Assets.zip')] + sorted(glob.glob(os.path.join(GAME, '*', 'Assets.zip'))):
        if os.path.exists(z):
            for name, info in game_sprite.entries(z).items():
                key = name.split('Textures/', 1)[-1].lower()
                index.setdefault(key, (z, info))
    return index


def open_front_share(d):
    """How far up from its hem the clear gap down the middle of the apparel's south sprite reaches, as a share of its
    height (rows with art either side counted); -1 without a sprite (OpenFronts.cs's OpenFrontShare)."""
    if d['name'] in OPEN_SHARES:
        return OPEN_SHARES[d['name']]
    import numpy as np
    from PIL import Image
    sys.path.insert(0, os.path.dirname(__file__))
    import game_sprite
    share, path = -1, d.get('worn')
    where = None
    for suffix in ['_' + b + '_south' for b in OPEN_BODIES] + ['_south']:
        where = path and TEXTURES[0].get((path + suffix).lower()) if TEXTURES else None
        if where:
            break
    if where:
        im = Image.open(where) if isinstance(where, str) else game_sprite.read(*where)
        if im is not None:
            a = np.array(im.convert('RGBA').resize((OPEN_SIZE, OPEN_SIZE)))[::-1, :, 3] > 128   # rows bottom first
            rows = np.where(a.any(1))[0]
            share = 0
            if len(rows):
                low, high, mid, w = rows[0], rows[-1], OPEN_SIZE // 2, OPEN_HALF_WIDTH
                n = 0
                for j in range(low, high + 1):
                    if a[j, mid - w:mid + w + 1].any():
                        break
                    if a[j, :mid - w].any() and a[j, mid + w + 1:].any():
                        n += 1
                share = n / (high - low + 1)
    OPEN_SHARES[d['name']] = share
    return share


def mod_folders(mod, version='1.7'):
    """The folders the game loads of a mod for this version: its LoadFolders.xml's entry for the newest version up to
    it (each li, those for a mod's IfModActive too), else its root, Common and its newest version folder up to it."""
    def ver(t):
        m = re.fullmatch(r'v?(\d+)\.(\d+)', t)
        return (int(m.group(1)), int(m.group(2))) if m else None
    want = ver(version)
    # (found whatever its case, as the game does: many mods ship loadFolders.xml)
    lf = next((os.path.join(mod, n) for n in os.listdir(mod) if n.lower() == 'loadfolders.xml'), os.path.join(mod, 'LoadFolders.xml'))
    if os.path.exists(lf):
        try:
            root = ET.parse(lf).getroot()
            best = max(((ver(e.tag), e) for e in root if ver(e.tag) and ver(e.tag) <= want), key=lambda x: x[0], default=None)
            if best:
                out = []
                for li in best[1].findall('li'):
                    t = (li.text or '').strip().strip('/')
                    out.append(os.path.join(mod, t) if t else mod)
                return out
        except ET.ParseError:
            pass
    # Without one the game loads the root, Common and the newest version folder.
    vs = [(ver(n), n) for n in os.listdir(mod) if ver(n) and ver(n) <= want and os.path.isdir(os.path.join(mod, n))]
    common = [os.path.join(mod, 'Common')] if os.path.isdir(os.path.join(mod, 'Common')) else []
    return [mod] + common + ([os.path.join(mod, max(vs)[1])] if vs else [])


def files_of(folder):
    """Its XML files: a mod's root only its own Defs (its version folders are read as mod_folders says), any other
    folder all of them."""
    if os.path.exists(os.path.join(folder, 'About', 'About.xml')):
        return glob.glob(os.path.join(folder, 'Defs', '**', '*.xml'), recursive=True)
    return glob.glob(os.path.join(folder, '**', '*.xml'), recursive=True)


def read_defs(folders):
    nodes = {}
    for r in folders:
        for f in files_of(r):
            if os.sep + 'Patches' + os.sep in f:
                continue
            try:
                root = ET.parse(f).getroot()
            except Exception:
                continue
            for d in root.findall('ApparelLayerDef'):
                if d.findtext('defName') and d.findtext('drawOrder'):
                    LAYER_ORDER[d.findtext('defName')] = float(d.findtext('drawOrder'))
                if d.findtext('defName'):
                    LAYER_LABEL[d.findtext('defName')] = d.findtext('label') or ''
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
                         stage=get(d, 'apparel/developmentalStageFilter') or 'Child, Adult', comps=get(d, 'comps') or [],
                         worn=get(d, 'apparel/wornGraphicPath'))
    return out


def groups_of(gs):
    out = set()
    for g in gs:
        g = SAME.get(g, g)
        if g == 'FullHead':
            out |= {'UpperHead', 'Eyes', 'Mouth'}
        out.add(g)
    return out


def under(layer):
    """The body layer a layer counts as (SkinnedFigure.BodyLayerOf): 'under' in its name or label Underwear, a word of
    BODY_LAYER_WORDS that layer, else drawn under the skin layer Underwear, else itself."""
    text = (layer + ' ' + LAYER_LABEL.get(layer, '')).lower()
    if 'under' in text:
        return 'Underwear'
    for body, words in BODY_LAYER_WORDS:
        if any(w in text for w in words):
            return body
    return 'Underwear' if LAYER_ORDER.get(layer, 0) < LAYER_ORDER['OnSkin'] else layer


# A pack: apparel worn only on a backpack layer (CE's and GrimWorld's Backpack, Ratkin's BackPack), or named a backpack,
# whatever its groups (Torso, Shoulders, Waist): matched as PACK_LAYER over PACK_GROUP alone, so every pack takes a pack
# garment. Webbing: apparel named as it (WEBBING_WORDS), not a pack, matched as CE's tactical vest is (its Webbing layer,
# Torso), as GrimWorld's combat webbing (Shell, Belt, the whole body) was drawn as nothing or a robe (StandIns.cs).
PACK_LAYER, PACK_GROUP = 'Backpack', 'Back'
WEBBING_LAYER = 'Webbing'
SHOULDER_LAYER = 'Shoulder'
WEBBING_WORDS = ('webbing', 'chest rig', 'load bearing', 'load-bearing')


def pack_layer(layer):
    return 'backpack' in (layer + ' ' + LAYER_LABEL.get(layer, '')).lower()


def all_pack_layers(layers):
    """Worn on a backpack layer and nothing else but the belt (a jump pack is on both)."""
    return any(pack_layer(x) for x in layers) and all(pack_layer(x) or x == 'Belt' for x in layers)


# Never a pack, whatever its layer: GrimWorld's scout cape is worn on the backpack layer.
NOT_PACK_WORDS = ('cape', 'cloak')
# A pack of power armour: one of SPACER_TECH or later fitted over the neck and shoulders too (GrimWorld's Space Marine
# packs), which the power armour garments draw with their own backpack, so it takes no stand-in (a canvas pack on it).
SPACER_TECH = ('Spacer', 'Ultra', 'Archotech')


def words_of(def_name):
    """A defName as words, lower case: ElysianHelmetOpen held "top" run together (StandIns.cs's WordsOf)."""
    return re.sub(r'([a-z])([A-Z])', r'\1 \2', def_name.replace('_', ' ')).lower()


def text_of(d):
    return words_of(d['name']) + ' ' + d['label'].lower()


def is_pack(text, layers):
    return (all_pack_layers(layers) or 'backpack' in text) and not any(w in text for w in NOT_PACK_WORDS)


def armour_pack(d):
    return d['tech'] in SPACER_TECH and {'Neck', 'Shoulders'} <= groups_of(d['groups'])


def is_webbing(text):
    return any(w in text for w in WEBBING_WORDS)


def as_what_it_is(p, layers):
    """Packs, webbing and eyewear matched as what they are, their layers and groups replaced (StandIns.cs's AsWhatItIs).
    Worn only over the eyes (EyeCover): goggles, glasses, a visor or headset, matched as over the eyes alone, as one with
    UpperHead (the enginseer's goggles, a headset) took the simple helmet."""
    if is_webbing(p['text']):
        p.update(layers=[WEBBING_LAYER], groups={'Torso'})
    elif is_pack(p['text'], layers):
        p.update(layers=[PACK_LAYER], groups={PACK_GROUP})
    elif all_pack_layers(layers):
        p['layers'] = ['Shell']   # a cape or cloak worn on the backpack layer: over the clothes
    elif layers and all('shoulder' in (x + ' ' + LAYER_LABEL.get(x, '')).lower() for x in layers):
        p.update(layers=[SHOULDER_LAYER], groups={'Shoulders'})   # pauldrons (GrimWorld's GW_Shoulder)
    elif p['layers'] and all(x == 'EyeCover' for x in p['layers']) and p['groups']:
        p['groups'] = {'Eyes'}
    return p


def profile(d):
    return as_what_it_is(_profile(d), d['layers'])


def _profile(d):
    return dict(def_=d, mask=any(w in (d['name'] + ' ' + d['label']).lower() for w in MASK_WORDS),
                name=d['name'], layers=list(dict.fromkeys(under(x) for x in d['layers'])), groups=groups_of(d['groups']),
                armour=any('armor' in c.lower() for c in d['cats']), noble=any('noble' in c.lower() for c in d['cats']),
                vacsuit='Vacsuit' in d['tags'] or 'vacsuit' in d['name'].lower(), child='Adult' not in d['stage'],
                tech=d['tech'], stuff=set(d['stuff']), text=text_of(d))


def tokens(def_name):
    n = re.sub(r'^(CE_)?Apparel_', '', def_name)
    return [t.lower() for t in re.findall(r'[A-Z][a-z]+|[a-z]+', n) if len(t) >= 3 and t.lower() not in COMMON_WORDS]


def garment_tokens(name):
    """A garment's name words; a NO_STAND_IN hat's keep "hat", as the top hat's "top" alone was found in topbanded."""
    words = tokens(name)
    return ['hat'] + words if name in NO_STAND_IN and name.startswith('Hat') else words


def never(d):
    """Shields (by layer or category); anything on the belt or energy-shielding unless also worn on a body layer, and
    for a shield in it covering the arms or legs too (Terminator armour lists the belt, Custodes plate has a shield in
    it; Biotech's heavy shield unit, Middle over the torso, is a shield); power armour's packs (armour_pack). A pack or
    webbing on the belt (as_what_it_is) takes a pack or webbing garment."""
    text = text_of(d)
    if is_webbing(text):
        return False
    if is_pack(text, d['layers']):
        return armour_pack(d)
    if all_pack_layers(d['layers']):
        return False
    if any('shield' in l.lower() for l in d['layers']) or any('shield' in c.lower() for c in d['cats']):
        return True
    body = any(under(l) in BODY for l in d['layers'])
    limbs = bool({'Arms', 'Legs'} & set(d['groups']))
    return (not body and 'Belt' in d['layers']) or ('CompProperties_Shield' in d['comps'] and not (body and limbs))


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
    named = name_share(a, g)
    if g['garment'] in NARROW and named <= 0:
        times *= NARROW_SHARE
    if g['vacsuit'] and a['vacsuit']:
        plus += BOTH_VACSUIT
    if a['noble'] and g['noble']:
        plus += BOTH_NOBLE
    if a['tech'] in TECH and g['tech'] in TECH:
        plus += SAME_TECH * max(0, 1 - abs(TECH.index(a['tech']) - TECH.index(g['tech'])) / TECH_STEP)
    if a['stuff'] & g['stuff']:
        plus += SAME_STUFF
    plus += NAME_WORDS * named
    if named >= 1 and head_nouns(a['def_']['label']) & {singular(x) for x in g['tokens']}:
        plus += HEAD_NOUN
    if g['garment'] in GENERIC:
        plus += GENERIC_BONUS
    return times, plus


def head_nouns(label):
    """The last word of a label and the word before its first "of", lower case, without a plural s and anything in
    brackets: what the apparel is (a priest robe is a robe, a psyker jacket a jacket, robes of Malcador robes), which
    says more than its groups, as many mods give a long coat the groups of plate armour (StandIns.cs's HeadNouns)."""
    words = re.findall(r'[a-z]+', re.sub(r'\(.*?\)', ' ', label.lower()))
    out = {singular(words[-1])} if words else set()
    if 'of' in words[1:]:
        out.add(singular(words[words.index('of', 1) - 1]))
    return out


def singular(w):
    return w[:-1] if len(w) > 3 and w.endswith('s') and not w.endswith('ss') else w


def name_found(text, t):
    for x in NAME_NOT.get(t, ()):
        text = text.replace(x, ' ')
    return t in text or any(w in text for w in NAME_ALIKE.get(t, ()))


def name_share(a, g):
    return sum(1 for t in g['tokens'] if name_found(a['text'], t)) / len(g['tokens']) if g['tokens'] else 0


def usable(a, g):
    """A garment of NO_STAND_IN stands in only for apparel with all its name words, one of MASKS only for a mask, and one
    over the skull (not a mask, which FullHead only says is, nor one the apparel is named as) only for apparel over it: a
    scarf over the mouth or neck scored just enough for the power armour helmet. An open-fronted garment only for apparel
    drawn open down the front (OPEN_FRONTS)."""
    return (not g['named_only'] or name_share(a, g) == 1) and (g['garment'] not in MASKS or a['mask']) \
        and ('UpperHead' not in g['groups'] or 'UpperHead' in a['groups'] or g['garment'] in MASKS or g['named_only']) \
        and (g['garment'] not in OPEN_FRONTS or open_front_share(a['def_']) >= OPEN_FRONT_LEAST)


# Headgear whose label says it is one of these is a hat even if armour: GrimWorld's Space Marine hoods cover the whole
# head as armour and took the power armour helmet.
HAT_NOUNS = {'hood', 'cap', 'hat', 'beret', 'bandana', 'headwrap', 'turban', 'bonnet'}


def as_hat(a):
    """Unarmoured headgear not named as a mask, or headgear named a hat (HAT_NOUNS), is matched as a hat, whatever of
    the face its groups say."""
    if (not a['armour'] or head_nouns(a['def_']['label']) & HAT_NOUNS) and not a['mask'] and 'UpperHead' in a['groups']:
        a['groups'] = a['groups'] - {'Eyes', 'Mouth'}
    return a


def coverage(groups, covered):
    both = sum(WEIGHT.get(x, 1) for x in groups & covered)
    return both / sum(WEIGHT.get(x, 1) for x in groups | covered) if both else 0


def score(a, groups, g):
    lf = layer_share(a['layers'], g['layers'])
    c = coverage(groups, g['groups'])
    named = g['named_only'] and name_share(a, g) == 1
    if named:
        c = max(c, NAMED_COVER)
    if lf == 0 or c == 0:
        return 0
    times, plus = factors(a, g)
    return c * lf * times + plus + (NAMED_BONUS if named else 0)


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
    mods = [f for m in sorted(glob.glob(os.path.join(WORKSHOP, '*'))) for f in mod_folders(m)]
    defs = read_defs(args or glob.glob(os.path.join(GAME, '*', 'Defs')) + mods)
    TEXTURES.append(texture_index(mods))
    garments, own, named = [], {}, {}
    for f in sorted(glob.glob(os.path.join(CHIBI, 'garment_*.txt'))):
        name = os.path.basename(f)[len('garment_'):-4]
        lines = {}
        with open(f) as fh:
            for line in fh:
                p = line.split()
                if p and p[0] in ('defs', 'held', 'with_legwear', 'baby_romper', 'layer', 'groups', 'like', 'stands_for'):
                    lines[p[0]] = p[1:]
        for x in lines.get('defs', []):
            own[x] = name
        for x in lines.get('stands_for', []):
            named.setdefault(x, []).append(name)
        if set(lines) & {'held', 'with_legwear', 'baby_romper'}:
            continue
        d = next((defs[x] for x in lines.get('defs', []) if x in defs), None)
        if d:
            g = profile(d)
            g['layers'] += [x for x in lines.get('layer', []) if x not in g['layers']]   # its lines add layers and groups
            g['groups'] |= groups_of(lines.get('groups', []))
            as_what_it_is(g, d['layers'])   # again, as the lines may have added to a pack's groups
        elif lines.get('layer') and lines.get('groups'):
            # No apparel of its own on disk: by its layer (one or more) and groups lines, as StandIns.cs.
            g = dict(mask=False, armour=False, noble=False, vacsuit=False, child=False, tech=None, stuff=set(),
                     layers=lines['layer'], groups=groups_of(lines['groups']), text=name.lower())
            as_what_it_is(g, lines['layer'])
        else:
            continue
        # Its kind (tech level, stuff, armour) from the apparel its like line names, if there, over its def's.
        like = defs.get((lines.get('like') or [None])[0])
        if like:
            k = profile(like)
            g.update(tech=k['tech'], stuff=k['stuff'], armour=k['armour'], noble=k['noble'])
        g.update(name=name, garment=name, tokens=garment_tokens(name), named_only=name in NO_STAND_IN)
        garments.append(g)
    for name in sorted(defs):
        d = defs[name]
        if name in own and not loo:
            print(f'{name:45} own {own[name]}')
            continue
        a = as_hat(profile(d))
        found = named.get(name) or ([] if never(d) else stand_ins(a, garments, own.get(name)))
        top = sorted(((score(a, a['groups'], g), g['garment']) for g in garments if g['garment'] != own.get(name) and usable(a, g)), reverse=True)[:3]
        print(f"{'LOO ' if name in own else ''}{name:45} {'+'.join(found) or '-':28} " + '  '.join(f'{n}:{s:.2f}' for s, n in top))


if __name__ == '__main__':
    main()
