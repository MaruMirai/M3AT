# A live preview of every clip walk.py makes, for tuning its numbers without baking or starting the game.
#   blender Models/pawnHumanRig.blend --python anim_scripts/preview.py      (or ./preview_model.sh)
# Adds a "Pose" tab to the 3D view's sidebar (N). Pick a clip (the walk and run, the idle, each drafted stance, each
# carry, the carried person, each hurt place, sitting, lying, crawling, kneeling at work, limping, hopping, wading, swimming, treading water) and the rig is posed straight from walk.py as the sliders change,
# so what you see is what export_frames.py would bake. Every number at the top of walk.py gets a slider (walk and run
# side by side for the ones RUN overrides), and so do the (x, y, z) places, each DRAFTED and CARRY entry's hands and its
# 'body' numbers. A drafted, carry or hurt loop can be laid over the walk or run as the game lays it (walk.UPPER,
# walk.HURT_UPPER).
# Each held hand of a stance shown on its own also gets a handle, an arrows empty (MaruHand.L, MaruHand.R): moved (G) it
# sets the wrist place, turned (R) the hand's turn ('<side>_turn'), so a hand can be put just so with Blender's own tools.
# A weapon on the 'hand' line gets one too (MaruWeaponGrip, at the grip: x along the weapon, z out of its top), which sets
# where it sits in the hand ('at') and how it is turned there (its aim and roll).
# A drafted stance shows its weapon as the game draws it (WalkFigures.DrawWeapon): a slab of any weapon sprite PNG (the
# game's own are packed away; copies of them go in weapon_sprites/), placed by walk.WEAPONS, whose
# numbers get sliders too. Each weapon's sprite, draw size and angle offset start as its def has them (SPRITE_ROOTS). A weapon
# IsoRim has a 3D model for shows that model instead (IsoRim models), fitted to the sprite as IsoRim's WeaponModels fits it.
# The slab and models are taken out of the scene while the .blend is saved.
# Worn gear (walk.GEAR: IsoRim's packs, belts and hip tools) shows on any clip too: each mount with one of IsoRim's models
# for it, its numbers as sliders and a handle (MaruGearGrip: G moves its place, R turns the model) on the mount picked.
# Holstered weapons (walk.HOLSTERS: where a weapon not held is drawn on the body) show on any clip: each slot with a
# weapon of its size picked to preview in it, its numbers as sliders, and a handle (MaruHolsterGrip, on the slot picked:
# G moves it, R turns it, x along the weapon, z out of its top).
# Worn: pick what the figure has on, from the export's garments (each drawn as the game skins it), its body keys, and which
# worn gear is on: the holsters, backpacks and gear are then placed as the game places them, pushed out past all that and
# each other (holster_push.py, the game's Clearance, HolsterOver, PackSide ...).
# "Mirror from" takes another slot of its size's place across the figure's middle, a handgun's spun for a cross draw.
# "Write to walk.py" puts the numbers back into the file; "Reload walk.py" picks up hand edits to it, code included.
# Nothing is keyed, and the settings are not saved in the .blend.
import ast, bpy, contextlib, glob, importlib, math, os, re, sys
import xml.etree.ElementTree as ET
import numpy as np
from mathutils import Euler, Matrix, Quaternion, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
if not os.path.exists(os.path.join(HERE, 'walk.py')):   # run from Blender's text editor: look beside the .blend instead
    HERE = os.path.join(bpy.path.abspath('//'), '..', 'anim_scripts')
HERE = os.path.normpath(HERE)
WALK_PY = os.path.join(HERE, 'walk.py')
if HERE not in sys.path:
    sys.path.insert(0, HERE)
import walk
import holster_push

ISO_PITCH = 35.3   # IsoRim's isometric camera: degrees below level (View3D isoPitch) ...
ISO_YAW = 45.0     # ... and turned from north (View3D.IsoYaw)
NUMBER = re.compile(r'^([A-Z][A-Z_0-9]*)(\s*=\s*)(-?\d+\.\d*)(.*)$', re.M)
TUPLE = re.compile(r'^([A-Z][A-Z_0-9]*)(\s*=\s*)\((\s*-?\d+\.?\d*(?:\s*,\s*-?\d+\.?\d*)+\s*)\)(.*)$', re.M)
NOT_TUNED = {'LEG', 'L1', 'L2', 'MODEL_LEG', 'STRIKE_AT'}   # STRIKE_AT is WalkFigures' StrikeRaised, StrikeHit and StrikeThrough too
JUMP_SECONDS = 1.0   # how long Play takes over a jump (the game's are 0.5 s and up: 12 cells a second, at least 0.5 s)   # worked out from the rig, not set by hand
AIM_SWEEP_SECONDS = 2.0   # an aim clip played from the lowest aim to the highest, by Time (it has no length of its own)
FLINCH_SECONDS = 0.4   # how long Play takes over a flinch (WalkFigures' FlinchTicks)
STRIKE_SECONDS = 0.8   # how long Play takes over a blow or a throw (the game's melee blow: WalkFigures' WindTicks + RecoverTicks)
# walk.py's paragraphs, by the first name in each, and the clips each one shapes.
SECTIONS = [('FRAMES', 'Walk and run'), ('IDLE_SECONDS', 'Idle'), ('DRAFT_SECONDS', 'Drafted'), ('SIT_SECONDS', 'Sitting'),
            ('LIE_SECONDS', 'Lying'), ('RAGDOLL_CURL', 'Ragdoll'), ('CRAWL_FRAMES', 'Crawling'), ('PLUCK_LEAN', 'Kneeling at work'), ('LIMP_GOOD', 'Limping'), ('HOP_GROUND', 'Hopping'), ('WADE_STRIDE', 'Wading'),
            ('SWIM_FRAMES', 'Swimming'), ('TREAD_SECONDS', 'Treading water'), ('CARRY_SECONDS', 'Carrying'),
            ('HURT_SECONDS', 'Hurt'), ('CRY_SECONDS', 'Cowering'), ('COVER_SECONDS', 'Taking cover'), ('LEAD_SECONDS', 'Leading'), ('WAVE_SECONDS', 'Waving'), ('SAD_SECONDS', 'Sad'), ('DRUNK_SECONDS', 'Drunk'), ('STIR_SECONDS', 'Stirring'), ('FIRE_LEAN', 'Roasting'), ('BURN_SECONDS', 'On fire'), ('REACH_SECONDS', 'Reaching'), ('RUB_UPPER', 'Rubbing the hands'), ('TALK_UPPER', 'Talking'), ('RELOAD_UPPER', 'Reloading'), ('EAT_UPPER', 'Eating'), ('READ_SECONDS', 'Reading'), ('RESEARCH_CLIP', 'Researching'), ('CROUCH_CLIP', 'Working by place'),
            ('CAST_SECONDS', 'Casting'), ('HIP_SECONDS', 'Hip gear'), ('SHIELD_SECONDS', 'Shield'), ('FLINCH_FRAMES', 'Flinching'), ('REEL_SECONDS', 'Staggered'), ('TOSS_SECONDS', 'Tossing'), ('KISS_SECONDS', 'Kissing'), ('BEAT_SECONDS', 'Beating fires'),
            ('DAZE_SECONDS', 'Stunned'), ('HAND_PALM', 'Placing a palm'),
            ('JUMP_FRAMES', 'Jumping'), ('WORK_GRIP', 'Working'), ('BEND_SECONDS', 'Bending'), ('AIM_PITCH', 'Aiming'),
            ('STRIKE_FRAMES', 'Striking'), ('DRAW_FRAMES', 'Drawing')]
CLIP_SECTION = {'gait': 'Walk and run', 'still': 'Walk and run', 'idle': 'Idle', 'drafted': 'Drafted',
                'carry': 'Carrying', 'carried': 'Carrying', 'sit': 'Sitting', 'lie': 'Lying', 'crawl': 'Crawling',
                'bend': 'Bending', 'hurt': 'Hurt', 'cower': 'Cowering', 'hunker': 'Cowering', 'cover': 'Taking cover', 'lead': 'Leading', 'rub': 'Rubbing the hands', 'talk': 'Talking', 'teach': 'Teaching', 'wave': 'Waving', 'sad': 'Sad', 'drunk': 'Drunk', 'stir': 'Stirring', 'stir_crouch': 'Stirring', 'stir_campfire': 'Roasting', 'stir_campfire_sit': 'Roasting', 'burning': 'On fire', 'reach': 'Reaching', 'reload': 'Reloading', 'eat_sit': 'Eating', 'eat_stand': 'Eating', 'smoke': 'Taking drugs', 'pill': 'Taking drugs', 'read': 'Reading', 'research': 'Researching', 'research_stand': 'Researching', 'research_floor': 'Researching', 'crouch': 'Working by place', 'sow': 'Working by place', 'bench': 'Working by place', 'bench_sit': 'Working by place', 'chair': 'Working by place', 'tame': 'Taming', 'tame_kneel': 'Taming', 'stomp': 'Beating fires', 'pat': 'Beating fires', 'piano': 'Playing music', 'piano_sit': 'Playing music', 'harp': 'Playing music', 'harp_sit': 'Playing music', 'drum': 'Playing music', 'drum_sit': 'Playing music', 'cast': 'Casting', 'gear': 'Hip gear', 'shield': 'Shield', 'flinch': 'Flinching', 'reel': 'Staggered', 'toss': 'Tossing', 'snowball': 'Tossing', 'kiss': 'Kissing', 'daze': 'Stunned', 'jump': 'Jumping', 'land': 'Jumping', 'work': 'Working', 'pluck': 'Kneeling at work', 'tend': 'Kneeling at work', 'toys': 'Kneeling at work', 'play_sit': 'Kneeling at work', 'visit_sit': 'Kneeling at work', 'meditate': 'Meditating', 'pray_sit': 'Meditating', 'vomit': 'Vomiting', 'getup': 'Getting up', 'dive': 'Diving', 'duck': 'Hunkering', 'roll': 'Diving', 'sleep': 'Lying', 'baby': 'Lying', 'downed': 'Lying', 'downed_side': 'Lying', 'lovin': 'Lying', 'dead': 'Lying', 'ragdoll': 'Ragdoll', 'caged': 'Lying', 'fall': 'Lying', 'limp': 'Limping', 'hop': 'Hopping', 'wade': 'Wading', 'swim': 'Swimming', 'tread': 'Treading water', 'aim': 'Aiming', 'strike': 'Striking', 'draw': 'Drawing', 'holster': 'Drawing'}
# The clips timed by the ground covered, posed by Phase: each one's pose at a phase, with the picked kind.
CYCLES = {'crawl': lambda p, kind: walk.crawl_at(p), 'limp': lambda p, kind: walk.limp_at(p, kind),
          'hop': lambda p, kind: walk.hop_at(p, kind),
          'wade': lambda p, kind: walk.wade_at(p), 'swim': lambda p, kind: walk.swim_at(p)}
GROUND = {'crawl': lambda: walk.crawl_ground_per_cycle(), 'limp': lambda: walk.limp_ground_per_cycle(),
          'hop': lambda: walk.hop_ground_per_cycle(),
          'wade': lambda: walk.wade_ground_per_cycle(), 'swim': lambda: walk.swim_ground_per_cycle()}
# The stance tables: hands by side ('L', 'R': wrist places in leg lengths) and 'body' (the stance's own numbers).
STANCES = ('DRAFTED', 'CARRY')
OVERS = ('drafted', 'carry', 'hurt', 'cower', 'lead', 'wave', 'sad', 'burning', 'shield', 'rub', 'talk', 'teach', 'kiss', 'snowball', 'reload', 'cast', 'gear', 'flinch', 'reel', 'toss', 'daze', 'aim', 'strike', 'draw', 'holster')   # the clips the game lays over the gait
BODY_KEYS = ('DROP', 'NARROW', 'STAGGER', 'LEAN', 'HEAD_TILT')   # offered for every entry, with any other key an entry already has
ANGLE_KEYS = {'HEAD_TILT'}   # body numbers in degrees either way from 0, which soft_range would give 0 .. 1
ANGLE_RANGE = (-45.0, 45.0)
SIDES = (('R', 'Right hand'), ('L', 'Left hand'))
FIGURE_HEIGHT = 1.25   # export_frames.py's: the still pose's height in model units, which sets the model's scale
SLAB_THICK = 0.05      # IsoRim's SpriteSolids.Thickness: the slab's thickness, cells
SLAB = 'MaruWeaponPreview'
ISORIM_ROOT = os.path.expanduser('~/.local/share/Steam/steamapps/workshop/content/294100/3819577715')   # the installed IsoRim
ISORIM_MODELS = os.path.join(ISORIM_ROOT, 'Models')
MOD_MODELS = os.path.join(os.path.dirname(HERE), "M3AT: Maru's 3D Animated Toons", 'Models')   # this mod's own tools (the rod)
# Where each weapon entry's sprite, draw size and angle offset come from to start with: its def in the game's own Defs
# (the expansions' too), its texPath looked up in each sprite folder in turn (the game's own are packed away, so copies
# taken out of its Assets.zip files into weapon_sprites/<Core or expansion>/, which is gitignored: the game's art, not
# ours). Those folders start below SPRITE_PREFIX. A kind's entry takes one weapon's look (KIND_LOOK).
GAME_DATA = os.path.expanduser('~/.local/share/Steam/steamapps/common/RimWorld/Data')
GAME_DLCS = ('Core', 'Royalty', 'Ideology', 'Biotech', 'Anomaly', 'Odyssey')
SPRITE_ROOTS = [os.path.join(os.path.dirname(HERE), 'weapon_sprites', dlc) for dlc in GAME_DLCS]
SPRITE_PREFIX = 'Things/Item/Equipment/'
KIND_LOOK = {'rifle': 'Gun_AssaultRifle', 'pistol': 'Gun_Autopistol', 'bow': 'Bow_Recurve', 'melee': 'MeleeWeapon_Knife'}
TURNED = 1 - 1e-6   # a handle's quaternion dot its last below this has been turned (about 0.16 degrees): mathutils
                    # keeps single precision, so a re-pose alone can leave it a little under 1
HANDLE = 'MaruHand.'   # + side: the empty a held hand follows (G to move it, R to turn it)
GRIP = 'MaruWeaponGrip'   # the empty a weapon on the 'hand' line follows: at the grip, x along it, z out of its top
LINES = [('hands', 'Through both fists', 'From the holding fist through the other'),
         ('forearm', 'Along the forearm', "On along the holding hand's forearm"),
         ('aim', 'Fixed direction', 'Along Aim (x left, y back, z up), whatever the arms do'),
         ('hand', 'With the hand', 'Rides the holding hand and turns as it turns (switching to it keeps the weapon where it is)')]

state = {'clamp': 0.0, 'error': '', 'timer': False, 'chest': None, 'handles': {}, 'grip': None, 'holster': None, 'gear': None, 'quiet': set(),
         'busy': False}
registered = {}

def read_tuned():
    """walk.py's hand-set numbers and (x, y, z) places in file order: (name, comment, is_tuple), which of them the run
    overrides, and the paragraphs they fall in: (title, names)."""
    with open(WALK_PY) as f:
        src = f.read()
    found = [(m.start(), m.group(1), m.group(4).strip().lstrip('#').strip(), False) for m in NUMBER.finditer(src)]
    found += [(m.start(), m.group(1), m.group(4).strip().lstrip('#').strip(), True) for m in TUPLE.finditer(src)]
    found = sorted(f for f in found if f[1] not in NOT_TUNED)
    leads = []
    for name, title in SECTIONS:
        m = re.search(r'^%s\s*=' % name, src, re.M)
        if m:
            leads.append((m.start(), title))
    leads.sort()
    sections = [(title, []) for _, title in leads] or [('Numbers', [])]
    for at, name, _, _ in found:
        i = max([k for k, (start, _) in enumerate(leads) if start <= at] or [0])
        sections[i][1].append(name)
    names = [(name, comment, is_tuple) for _, name, comment, is_tuple in found]
    return names, [n for n, _, t in names if not t and n in walk.RUN], [s for s in sections if s[1]]

def stance_entry(table, kind):
    """A stance entry as it poses: a weapon's drafted one with its kind's under it (walk.drafted_entry)."""
    return walk.drafted_entry(kind) if table == 'DRAFTED' else getattr(walk, table)[kind]

def shield_shown(p, kind):
    """On a shield stance (walk.DRAFTED's shield_<kind>), the weapon of that kind picked to see it with (Shield weapon),
    or None for the kind's own look."""
    base = walk.shield_weapon(kind) if hasattr(walk, 'shield_weapon') else None
    chosen = getattr(p, 'shield_with', '')
    if base is None or not chosen or walk.DRAFTED.get(chosen, {}).get('kind') != base:
        return None
    return chosen

def drafted_pose_name(p, kind):
    """The drafted entry a drafted clip is posed from: on a shield stance with a weapon picked that has its own
    (walk.shielded_names), that weapon's, worked out from the kind's with its own grip; else the clip's own."""
    chosen = shield_shown(p, kind)
    return walk.SHIELD + chosen if chosen is not None and chosen in walk.shielded_names() else kind

def weapon_name(p, what, kind):
    """The WEAPONS entry drawn in a drafted (or cover) clip's hands: the clip's own, or on a shield stance the weapon
    picked, else the kind's (shield_pistol: pistol)."""
    if what not in ('drafted', 'cover'):
        return kind
    if hasattr(walk, 'shield_weapon') and walk.shield_weapon(kind) is not None:
        return shield_shown(p, kind) or walk.shield_weapon(kind)
    return kind

shield_with_cache = []

def shield_with_items(self, context):
    """The one-handed weapons that can be seen in a shield stance (kept, as Blender wants the strings to live on)."""
    items = [('NONE', "(the kind's)", "The kind's own weapon look; the hands can be moved")]
    if hasattr(walk, 'ONE_HANDED'):
        kinds = {walk.shield_weapon(k) for k in walk.DRAFTED if walk.shield_weapon(k) is not None}
        items += [(k, k, 'Its shield stance (its kind\'s, with its own grip: walk.shielded_entry)')
                  for k, e in walk.DRAFTED.items() if e.get('kind') in kinds and walk.weapon_type(k) in walk.ONE_HANDED]
    shield_with_cache[:] = items
    return shield_with_cache

def read_stances():
    """Each DRAFTED and CARRY entry as it poses: which hands it places, whether a hand can be left free (a drafted
    side can; carry_at needs the ones it names), the body keys to offer and the keys this panel leaves alone (a
    weapon's 'kind' among them)."""
    out = {}
    for table in STANCES:
        for kind, entry in getattr(walk, table, {}).items():
            body = stance_entry(table, kind).get('body', {})
            keys = list(body) + [k for k in BODY_KEYS if k not in body]
            for other in getattr(walk, table).values():
                keys += [k for k in other.get('body', {}) if k not in keys]
            # A person carry's hands go where carry_hands finds them unless the entry places them.
            found = {}
            if table == 'CARRY' and 'hands' in entry and hasattr(walk, 'carry_hands'):
                found = {s: tuple((at - walk.head[walk.HIPS]) / walk.LEG) for s, (at, _) in walk.carry_hands(kind).items()}
            out[(table, kind)] = {
                'sides': [s for s, _ in SIDES if table == 'DRAFTED' or s in entry or s in found],
                'found': found,
                'set': {s for s, _ in SIDES if entry.get(s) is not None},
                'toggle': table == 'DRAFTED',
                'body': keys,
                'keep': {k: v for k, v in entry.items() if k not in SIDE_KEYS + ('body',)},
            }
    return out

def stance_base(table, key):
    """What a body number is in a stance that does not set it (stand_at's and carry_at's own changes)."""
    if table == 'DRAFTED':
        own = dict(DROP=walk.DRAFT_DROP, LEAN=walk.DRAFT_LEAN, NARROW=walk.DRAFT_NARROW)
    else:
        own = dict(DROP=0.02, NARROW=walk.DRAFT_NARROW)
    return own.get(key, walk.WALK.get(key, getattr(walk, key, 0.0)))

def sp(table, kind, rest):
    """A stance slider's property name."""
    return 'st_%s_%s_%s' % (table, kind, rest)

def tuned():
    """As read_tuned, kept from the last (re)load so the panel matches the sliders it has."""
    return registered['names']

def soft_range(*values, signed=False):
    """Slider limits: angles (degrees) and times get a wide range, shares and leg lengths 0 to 1. A small number that can
    go below 0 (signed: its comment says so, or it already is) drags both ways from 0 instead: at least 0.25 each way,
    more if it is already further out."""
    big = max(abs(v) for v in values)
    if big > 1.5:
        return (-90.0, 150.0)
    if signed or min(values) < 0:
        reach = max(0.25, 2 * big)
        return (-reach, reach)
    return (0.0, 1.0)

def props():
    return bpy.context.window_manager.maru_gait

def clip_of(p):
    """The picked clip as (what, kind): ('drafted', 'rifle'), ('gait', '') ..."""
    what, _, kind = p.clip.partition(':')
    return what, kind

def mocap_seconds(name):
    """A mocap clip's length in seconds (walk.<name>, an import_pluck.py clip), or None without it."""
    clip = getattr(walk, name, None)
    return clip['frames'] / clip['fps'] if clip is not None else None

def loop_seconds(what, kind=''):
    """How long a clock-timed loop is in game seconds, or None for one timed by the ground covered."""
    carry = walk.carry_seconds(kind) if what in ('carry', 'carried') and hasattr(walk, 'carry_seconds') else walk.CARRY_SECONDS
    return {'idle': walk.IDLE_SECONDS, 'drafted': walk.DRAFT_SECONDS, 'carry': carry,
            'carried': carry, 'sit': walk.SIT_SECONDS, 'lie': walk.LIE_SECONDS,
            'tread': getattr(walk, 'TREAD_SECONDS', None), 'hurt': getattr(walk, 'HURT_SECONDS', None),
            'cower': getattr(walk, 'CRY_SECONDS', None), 'hunker': getattr(walk, 'CRY_SECONDS', None),
            'cover': getattr(walk, 'COVER_SECONDS', None),
            'lead': getattr(walk, 'LEAD_SECONDS', None), 'shield': getattr(walk, 'SHIELD_DRAW_SECONDS' if kind else 'SHIELD_SECONDS', None), 'rub': getattr(walk, 'RUB_SECONDS', None), 'talk': getattr(walk, 'TALK_SECONDS', None), 'teach': getattr(walk, 'TEACH_SECONDS', None),
            'wave': getattr(walk, 'WAVE_SECONDS', None), 'sad': getattr(walk, 'SAD_SECONDS', None), 'drunk': getattr(walk, 'DRUNK_SECONDS', None), 'stir': getattr(walk, 'STIR_SECONDS', None), 'stir_crouch': getattr(walk, 'STIR_SECONDS', None), 'stir_campfire': getattr(walk, 'STIR_SECONDS', None), 'stir_campfire_sit': getattr(walk, 'STIR_SECONDS', None), 'burning': getattr(walk, 'BURN_SECONDS', None), 'reach': getattr(walk, 'REACH_SECONDS', None),
            'reload': getattr(walk, 'RELOAD_SECONDS', None),
            'eat_sit': getattr(walk, 'EAT_SIT_SECONDS', None), 'eat_stand': getattr(walk, 'EAT_STAND_SECONDS', None), 'smoke': getattr(walk, 'SMOKE_SECONDS', None), 'pill': getattr(walk, 'PILL_SECONDS', None), 'read': getattr(walk, 'READ_SECONDS', None), 'research': getattr(walk, 'RESEARCH_SECONDS', None), 'research_stand': getattr(walk, 'RESEARCH_SECONDS', None), 'research_floor': getattr(walk, 'RESEARCH_SECONDS', None), 'crouch': getattr(walk, 'CROUCH_SECONDS', None), 'sow': getattr(walk, 'SOW_SECONDS', None), 'bench': getattr(walk, 'BENCH_SECONDS', None), 'bench_sit': getattr(walk, 'BENCH_SIT_SECONDS', None), 'chair': getattr(walk, 'CHAIR_SECONDS', None), 'tame': getattr(walk, 'TAME_SECONDS', None), 'tame_kneel': getattr(walk, 'CROUCH_SECONDS', None), 'stomp': getattr(walk, 'BEAT_SECONDS', None), 'pat': getattr(walk, 'BEAT_SECONDS', 0.0) * getattr(walk, 'PAT_HITS', 1), **{w: walk.MUSIC[w.replace('_sit', '')]['seconds'] for w in ('piano', 'piano_sit', 'harp', 'harp_sit', 'drum', 'drum_sit') if hasattr(walk, 'MUSIC')}, 'cast': getattr(walk, 'CAST_SECONDS', None),
            'flinch': FLINCH_SECONDS, 'reel': getattr(walk, 'REEL_SECONDS', None), 'toss': getattr(walk, 'TOSS_SECONDS', None), 'snowball': getattr(walk, 'SNOW_SECONDS', None), 'kiss': getattr(walk, 'KISS_SECONDS', None), 'daze': getattr(walk, 'DAZE_SECONDS', None),
            'jump': JUMP_SECONDS, 'aim': AIM_SWEEP_SECONDS,
            'gear': (AIM_SWEEP_SECONDS if kind in ('aim', 'fire') else getattr(walk, 'HIP_DRAW_SECONDS', None) if kind in ('draw', 'put')
                     else getattr(walk, 'HIP_SECONDS', None)),
            'draw': draw_seconds(kind) if what in ('draw', 'holster') else None, 'holster': draw_seconds(kind) if what in ('draw', 'holster') else None,
            'land': getattr(walk, 'LAND_SECONDS', None), 'strike': STRIKE_SECONDS, 'pluck': getattr(walk, 'PLUCK_SECONDS', None),
            'tend': getattr(walk, 'TEND_SECONDS', None), 'toys': getattr(walk, 'TEND_SECONDS', None), 'play_sit': walk.carry_seconds('play') if hasattr(walk, 'play_sit_at') else None, 'visit_sit': getattr(walk, 'TALK_SECONDS', None), 'meditate': getattr(walk, 'MEDITATE_SECONDS', None), 'pray_sit': getattr(walk, 'PRAY_SECONDS', None), 'vomit': getattr(walk, 'VOMIT_SECONDS', None),
            'getup': getattr(walk, 'GETUP_SECONDS', None), 'dive': getattr(walk, 'DIVE_SECONDS', None), 'duck': getattr(walk, 'DUCK_SECONDS', None), 'roll': getattr(walk, 'ROLL_SECONDS', None),
            'sleep': walk.sleep_seconds() if hasattr(walk, 'sleep_seconds') and walk.SLEEP_CLIP is not None else mocap_seconds('SLEEP_CLIP'), 'baby': getattr(walk, 'BABY_SECONDS', None), 'downed': mocap_seconds('DOWNED_CLIP'),
            'downed_side': mocap_seconds('DOWNED_SIDE_CLIP'), 'lovin': getattr(walk, 'LOVIN_SECONDS', None),
            'fall': mocap_seconds('KNOCKDOWN_CLIP' if kind == 'knockdown' else 'DIE_CLIP'),
            'work': walk.work_seconds(kind) if what == 'work' and hasattr(walk, 'WORK') and kind in walk.WORK else None}.get(what)

def draw_seconds(kind):
    """How long a draw clip takes in game seconds, or None on an older walk.py."""
    return walk.DRAW_SIZES[walk.HOLSTERS[walk.draw_names()[kind][0]]['size']]['seconds'] if kind in walk.draw_names() else None

def push_settings(p):
    """Copies the sliders into walk: its numbers, places and stance tables."""
    names, run, _ = tuned()
    for name, _, is_tuple in names:
        if is_tuple:
            setattr(walk, name, tuple(getattr(p, 'tup_' + name)))
        elif name in run:
            walk.WALK[name] = getattr(p, 'walk_' + name)
            walk.RUN[name] = getattr(p, 'run_' + name)
        else:
            setattr(walk, name, getattr(p, 'walk_' + name))
    full = {}
    for (table, kind), info in registered['stances'].items():
        entry = {}
        for side in info['sides']:
            if not info['toggle'] or getattr(p, sp(table, kind, side + '_on')):
                at = tuple(getattr(p, sp(table, kind, side)))
                # A found hand not moved from where it was found keeps following the carried person.
                if side not in info['found'] or side in info['set'] or not same(list(at), list(info['found'][side])):
                    entry[side] = at
                if getattr(p, sp(table, kind, side + '_turn_on')):
                    entry[side + '_turn'] = tuple(math.degrees(a) for a in getattr(p, sp(table, kind, side + '_turn')))
                lift = getattr(p, sp(table, kind, side + '_elbow'))
                if abs(lift) > 1e-6:
                    entry[side + '_elbow'] = lift
        entry.update(info['keep'])
        body = {k: getattr(p, sp(table, kind, 'b_' + k)) for k in info['body'] if getattr(p, sp(table, kind, 'b_' + k + '_on'))}
        if body:
            entry['body'] = body
        full[(table, kind)] = entry
    # A weapon keeps only what differs from its kind's (as the sliders have it now), so it follows the kind's for the rest.
    for (table, kind), entry in full.items():
        base = full.get((table, entry.get('kind')))
        getattr(walk, table)[kind] = own_keys(entry, base) if base is not None else entry
    for kind, old in getattr(walk, 'WEAPONS', {}).items():
        w = dict(old)
        for k in ('hand', 'line'):
            w[k] = getattr(p, 'wp_%s_%s' % (kind, k))
        for k in ('drop', 'behind', 'roll'):
            w[k] = getattr(p, 'wp_%s_%s' % (kind, k))
        if w['line'] in ('aim', 'hand') or 'aim' in w:
            w['aim'] = tuple(getattr(p, 'wp_%s_aim' % kind))
        at = tuple(getattr(p, 'wp_%s_at' % kind))
        if 'at' in w or any(abs(v) > 1e-6 for v in at):
            w['at'] = at
        w.pop('scale', None)   # its size is its type's
        walk.WEAPONS[kind] = w
    for slot, old in getattr(walk, 'HOLSTERS', {}).items():
        pre = 'hs_%s_' % slot
        h = dict(old)
        h['size'], h['bone'] = getattr(p, pre + 'size'), getattr(p, pre + 'bone')
        for k in ('at', 'aim', 'out'):
            h[k] = tuple(getattr(p, pre + k))
        h['roll'], h['behind'] = getattr(p, pre + 'roll'), getattr(p, pre + 'behind')
        # A melee place only if the slot has one, ticked on.
        for k in ('melee_at', 'melee_aim', 'melee_roll'):
            v = getattr(p, pre + k)
            h.pop(k, None)
            if getattr(p, pre + 'melee_on'):
                h[k] = tuple(v) if k != 'melee_roll' else v
        walk.HOLSTERS[slot] = h
    for mount, old in getattr(walk, 'GEAR', {}).items():
        pre = 'gr_%s_' % mount
        g = dict(old)
        g['bone'], g['scale'] = getattr(p, pre + 'bone'), getattr(p, pre + 'scale')
        g['at'], g['out'] = tuple(getattr(p, pre + 'at')), tuple(getattr(p, pre + 'out'))
        if 'depth' in g or mount == 'belt':
            g['depth'] = getattr(p, pre + 'depth')
        g.pop('turn', None)
        if any(abs(v) > 1e-3 for v in getattr(p, pre + 'turn')):
            g['turn'] = tuple(getattr(p, pre + 'turn'))
        walk.GEAR[mount] = g
    # A weapon's type is written only where it differs from its drafted kind's (or the table already names it); each
    # type's size and holster slots.
    if hasattr(walk, 'WEAPON_TYPES'):
        listed_before = registered.setdefault('types_listed', set(walk.WEAPON_TYPES))
        for kind in getattr(walk, 'WEAPONS', {}):
            kind_of = walk.DRAFTED.get(kind, {}).get('kind')
            base = walk.WEAPON_TYPES.get(kind_of, 'rifle')
            v = getattr(p, 'wt_%s' % kind)
            if kind in listed_before or kind_of is None or v != base:
                walk.WEAPON_TYPES[kind] = v
            else:
                walk.WEAPON_TYPES.pop(kind, None)
        for t in walk.TYPE_SCALE:
            walk.TYPE_SCALE[t] = getattr(p, 'ts_%s' % t)
            walk.TYPE_HOLSTER[t] = getattr(p, 'th_%s' % t)

def listed(v):
    """A side key's value as a list of numbers (an elbow lift is one number, a place or turn three)."""
    return list(v) if isinstance(v, (tuple, list)) else [v]

def own_keys(entry, base):
    """A weapon's drafted entry with what it shares with its kind's (base) left out: a hand or turn the kind has and it
    doesn't is None (free, lined up, or no elbow lift), and a body number it leaves unset takes the kind's."""
    own = {k: v for k, v in entry.items() if k not in SIDE_KEYS + ('body',)}
    for k in SIDE_KEYS:
        if k in entry and not (k in base and same(listed(entry[k]), listed(base[k]))):
            own[k] = entry[k]
        elif k in base and k not in entry:
            own[k] = None
    kind_body = base.get('body', {})
    body = {k: v for k, v in entry.get('body', {}).items() if not (k in kind_body and same([v], [kind_body[k]]))}
    if body:
        own['body'] = body
    return own

def draw_phase(what, t):
    """How far through the draw clip a draw or holster shown at t is."""
    return 1.0 - t if what == 'holster' else t

def loop_pose(what, kind, t):
    """The picked loop's pose at t (0 .. 1 round it) at the walk's numbers, as export_loop bakes it."""
    walk.set_gait(0.0)
    if what == 'idle':
        return walk.stand_at(t)
    if what == 'drafted':
        return walk.stand_at(t, drafted_pose_name(props(), kind))
    if what == 'carry':
        return walk.carry_at(t, kind)
    if what == 'carried':
        return walk.carried_at(kind, t), 0.0
    if what == 'sit':
        return walk.sit_at(t)
    if what == 'lie':
        return walk.lie_at(t)
    if what == 'tread':
        return walk.tread_at(t)
    if what == 'bend':
        return walk.bend_pose()
    if what == 'hurt':
        return walk.hurt_at(t, kind)
    if what == 'cower':
        return walk.cower_at(t)
    if what == 'hunker':
        return walk.hunker_at(t)
    if what == 'cover':
        return walk.cover_at(t)
    if what == 'lead':
        return walk.lead_at(t)
    if what == 'shield':
        if kind:
            return walk.shield_draw_at(shield_draw_phase(kind, t), shield_rim_of(kind))
        return walk.shield_at(t)
    if what == 'rub':
        return walk.rub_at(t)
    if what == 'talk':
        return walk.talk_at(t)
    if what == 'teach':
        return walk.teach_at(t)
    if what == 'wave':
        return walk.wave_at(t)   # Time is how far through it: 0 hanging .. waved .. 1 hanging
    if what == 'sad':
        return walk.sad_at(t)
    if what == 'drunk':
        return walk.drunk_at(t)   # as the game adds it (Nudge) to the plain stance: whole, arms and all
    if what in ('stir', 'stir_crouch', 'stir_campfire', 'stir_campfire_sit'):
        return walk.stir_at(t, what)
    if what == 'burning':
        return walk.burn_at(t)
    if what == 'reach':
        return walk.reach_at(t)
    if what == 'reload':
        return walk.reload_at(t)
    if what == 'eat_sit':
        return walk.eat_sit_at(t)
    if what == 'eat_stand':
        return walk.eat_stand_at(t)
    if what == 'smoke':
        return walk.smoke_at(t)
    if what == 'pill':
        return walk.pill_at(t)   # Time is how far through it: 0 the pinch at the left palm .. the swallow .. 1 back down
    if what == 'read':
        # read:L / read:R, the book held out to someone sat on that side (read_show_L, read_show_R)
        return walk.read_at(t, {'L': 1, 'R': -1}.get(kind, 0)) if kind else walk.read_at(t)
    if what == 'cast':
        return walk.cast_at(t, touch=kind == 'touch')
    if what == 'gear':
        if kind in ('draw', 'put'):
            return walk.hip_draw_at(hip_draw_phase(kind, t))
        return walk.hip_aim_at(t, kind == 'fire') if kind in ('aim', 'fire') else walk.hip_hold_at(t)
    if what == 'flinch':
        return walk.flinch_at(t, kind)   # Time is how far through the jolt: the hit at 0, over at 1
    if what == 'snowball':
        return walk.snowball_at(t)   # Time is how far through the throw: 0 standing .. let go at walk.SNOW_RELEASE .. 1 standing
    if what == 'kiss':
        return walk.kiss_at(t)
    if what == 'toss':
        return walk.toss_at(t)   # Time is how far through the pitch: 0 standing .. let go at walk.TOSS_RELEASE .. 1 standing
    if what == 'reel':
        return walk.reel_at(t)   # Time is how far through the stagger: 0 standing .. down on one knee .. 1 standing
    if what == 'daze':
        return walk.daze_at(t)
    if what == 'jump':
        return walk.jump_at(t)   # Time is how far through the flight: 0 take-off .. 1 landing
    if what == 'land':
        return walk.land_pose()
    if what == 'work':
        return walk.work_at(t, kind)
    if what == 'pluck':
        return walk.pluck_at(t)
    if what == 'tend':
        return walk.tend_at(t)
    if what == 'toys':
        return walk.toys_at(t)
    if what == 'play_sit':
        return walk.play_sit_at(t)
    if what == 'visit_sit':
        return walk.visit_sit_at(t)
    if what == 'meditate':
        return walk.meditate_at(t)
    if what == 'pray_sit':
        return walk.pray_sit_at(t)
    if what == 'research':
        return walk.research_at(t)
    if what == 'research_stand':
        return walk.research_stand_at(t)
    if what == 'research_floor':
        return walk.research_floor_at(t)
    if what in ('crouch', 'sow', 'bench', 'bench_sit', 'chair'):
        return getattr(walk, what + '_at')(t)
    if what in ('tame', 'tame_kneel'):
        return walk.tame_at(t, kneel=what == 'tame_kneel')
    if what == 'stomp':
        return walk.stomp_at(t)   # one beat: the stamp at walk.STOMP_HIT
    if what in ('piano', 'piano_sit', 'harp', 'harp_sit', 'drum', 'drum_sit'):
        return getattr(walk, what + '_at')(t)   # an instrument: keys, strings or drum (walk.MUSIC)
    if what == 'pat':
        return walk.pat_at(t)   # two beats: the right palm's slap at walk.PAT_HIT, the left's half a loop on
    if what == 'vomit':
        return walk.vomit_at(t)
    if what == 'sleep':
        return walk.sleep_at(t)   # upright: the game lays it down as it lays the lie
    if what == 'baby':
        return walk.baby_at(t)   # upright, as the lie is
    if what == 'downed':
        return walk.downed_at(t)
    if what == 'downed_side':
        return walk.downed_side_at(t)
    if what == 'lovin':
        return walk.lovin_at(t, kind)   # on the side facing the partner on that side, upright
    if what == 'dead':
        return walk.dead_at(t)
    if what == 'ragdoll':
        return walk.ragdoll_pose()
    if what == 'caged':
        return walk.caged_pose()
    if what == 'fall':
        return walk.fall_at(t, kind)   # Time is how far through it: 0 standing .. 1 down (upright, as the lie is)
    if what == 'dive':
        return walk.dive_at(t, kind)   # Time is how far through it: 0 standing .. 1 down
    if what == 'duck':
        return walk.duck_at(t, kind)
    if what == 'roll':
        return walk.roll_at(t)
    if what == 'getup':
        return walk.getup_at(t)   # Time is how far through it: 0 lying .. 1 standing
    if what == 'aim':
        # Time is the pitch: 0 the lowest aim .. 1 the highest, as the game picks between the frames.
        name, _, stage = kind.partition('/')
        n = walk.AIM_PITCHES
        return walk.aim_at(t * (n - 1) / n, name, stage)
    if what == 'strike':
        return walk.strike_at(t, kind)   # Time is how far through the blow: the guard at 0 and 1, the hit half way
    if what in ('draw', 'holster'):
        # Time is how far through it: hanging at 0 and the guard at 1 for a draw; a holster is the game's same clip backwards.
        return walk.draw_at(draw_phase(what, t) * (walk.DRAW_FRAMES - 1) / walk.DRAW_FRAMES, kind)
    raise ValueError('no clip ' + what)

def upper_share(name, hands, upper=None):
    """export_frames.upper_mask for one bone: its walk.UPPER (or upper) share, or its nearest listed parent's, none for a
    free arm."""
    upper = upper or walk.UPPER
    b = walk.bones[name]
    while b is not None and b.name not in upper:
        b = b.parent
    share = upper[b.name] if b is not None else 0.0
    for side in ('L', 'R'):
        if side not in hands and name.split('.')[0] in ('shoulder', 'upper_arm', 'forearm', 'hand') and name.endswith('.' + side):
            return 0.0
    return share

def set_bone(pb, loc, q):
    pb.location = loc
    if pb.rotation_mode == 'QUATERNION':
        pb.rotation_quaternion = q
    else:
        pb.rotation_euler = q.to_euler(pb.rotation_mode)

def apply_over(Xg, Xs, hands, upper=None):
    """Lays the stance Xs over the gait Xg as the game does (SkinnedFigure.Over): each bone's turn from its parent
    blended by its share, so the stance's arms and chest ride the walking hips."""
    for pb in walk.arm.pose.bones:
        if pb.name not in Xg:
            continue
        lg, qg, _ = walk.basis_of(pb.name, Xg).decompose()
        w = upper_share(pb.name, hands, upper) if pb.name in Xs else 0.0
        if w <= 0.0:
            set_bone(pb, lg, qg)
            continue
        ls, qs, _ = walk.basis_of(pb.name, Xs).decompose()
        set_bone(pb, lg.lerp(ls, w), qg.slerp(qs, w))

def nudged(Xg, Xs, X0, upper):
    """The gait Xg with the flinch Xs added on top as the game does (SkinnedFigure.Nudge): each bone's turn from its
    parent, from the clip's first frame X0 to Xs, put before the gait's, by its share. Returns the pose."""
    out = {}
    def turn(X, n):
        return X[n].to_quaternion()
    def visit(b, parent):
        n = b.name
        if n in Xg:
            share = upper_share(n, 'LR', upper) if n in Xs and n in X0 else 0.0
            pq = turn(Xg, parent) if parent else Quaternion()
            local = pq.inverted() @ turn(Xg, n)
            if share > 0.0:
                sp = turn(Xs, parent) if parent else Quaternion()
                zp = turn(X0, parent) if parent else Quaternion()
                d = (sp.inverted() @ turn(Xs, n)) @ (zp.inverted() @ turn(X0, n)).inverted()
                local = Quaternion().slerp(d, share) @ local
            if parent:
                newp = out[parent].to_quaternion()
                gap = Xg[n] @ walk.head[n] - Xg[parent] @ walk.head[parent]
                at = out[parent] @ walk.head[parent] + (newp @ pq.inverted()) @ gap
                q = newp @ local
            else:
                at, q = Xg[n] @ walk.head[n], local
            out[n] = Matrix.Translation(at) @ q.to_matrix().to_4x4() @ Matrix.Translation(-walk.head[n])
            parent = n
        for c in b.children:
            visit(c, parent)
    for b in walk.arm.data.bones:
        if b.parent is None:
            visit(b, None)
    return out

def model_scale():
    """Model units per Blender unit, as export_frames.py finds it: the still pose made FIGURE_HEIGHT tall."""
    if 'model' not in registered:
        walk.set_gait(0.0)
        walk.apply_pose(walk.stand_pose())
        bpy.context.view_layer.update()
        top = None
        for body in walk.body_parts():   # the top of whichever part reaches highest (the head, if split)
            ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
            me = ev.to_mesh()
            part_top = max((body.matrix_world @ v.co).z for v in me.vertices)
            top = part_top if top is None else max(top, part_top)
            ev.to_mesh_clear()
        registered['model'] = FIGURE_HEIGHT / top
    return registered['model']

def stand_in():
    """A grey bar for a weapon with no sprite picked: the muzzle end red, a bump on top, so its turn reads."""
    img = bpy.data.images.get(SLAB + 'StandIn')
    if img is None:
        n = 64
        img = bpy.data.images.new(SLAB + 'StandIn', n, n, alpha=True)
        px = [0.0] * (n * n * 4)
        for y in range(n):
            for x in range(n):
                c = None
                if 6 <= x < 58 and 27 <= y < 37:
                    c = (0.8, 0.15, 0.1) if x >= 50 else (0.25, 0.25, 0.28)
                elif 28 <= x < 36 and 37 <= y < 41:
                    c = (0.25, 0.25, 0.28)
                if c:
                    px[(y * n + x) * 4:(y * n + x) * 4 + 4] = c + (1.0,)
        img.pixels = px
    return img

def sprite(path):
    """The picked sprite, loaded once; the stand-in when none is picked or it can't be read."""
    path = bpy.path.abspath(path) if path else ''
    if not os.path.isfile(path):
        return stand_in(), 'no sprite file' if path else ''
    try:
        img = bpy.data.images.load(path, check_existing=True)
    except RuntimeError as e:
        return stand_in(), str(e)
    registered.setdefault('images', set()).add(img.name)
    return img, ''

def weapon_defs():
    """Every ThingDef in the game's Defs that has a texPath, by defName: (texPath, drawSize, equippedAngleOffset), each
    taken through its parents as the game does. Read once."""
    if 'weapon_defs' in registered:
        return registered['weapon_defs']
    named, things = {}, []
    for dlc in GAME_DLCS:
        for path in sorted(glob.glob(os.path.join(GAME_DATA, dlc, 'Defs', '**', '*.xml'), recursive=True)):
            try:
                root = ET.parse(path).getroot()
            except (ET.ParseError, OSError):
                continue
            for el in root.iter('ThingDef'):
                if el.get('Name'):
                    named[el.get('Name')] = el
                if el.findtext('defName'):
                    things.append(el)

    def find(el, tag):
        seen = set()
        while el is not None and id(el) not in seen:
            seen.add(id(el))
            v = el.find(tag)
            if v is not None and v.text:
                return v.text.strip()
            el = named.get(el.get('ParentName'))
        return None

    out = {}
    for el in things:
        tex = find(el, 'graphicData/texPath')
        if not tex:
            continue
        size = find(el, 'graphicData/drawSize') or '(1, 1)'
        nums = [float(v) for v in re.findall(r'-?\d+\.?\d*', size)] or [1.0]
        out[el.findtext('defName').strip()] = (tex, (nums[0], nums[-1]), float(find(el, 'equippedAngleOffset') or 0.0))
    registered['weapon_defs'] = out
    return out

def weapon_look(kind):
    """A WEAPONS entry's sprite file ('' when none is found), draw size (cells) and angle offset (degrees), from its
    weapon's def."""
    found = weapon_defs().get(KIND_LOOK.get(kind, kind))
    if found is None:
        return '', (1.0, 1.0), -65.0 if kind == 'melee' or kind.startswith('MeleeWeapon_') else 0.0
    tex, size, offset = found
    if tex.startswith(SPRITE_PREFIX):
        tex = tex[len(SPRITE_PREFIX):]
    for root in SPRITE_ROOTS:
        base = os.path.join(root, *tex.split('/'))
        for path in [base + '.png', base + '_east.png'] + sorted(glob.glob(os.path.join(base, '*.png'))):
            if os.path.isfile(path):
                return path, size, offset
    # The game finds textures whatever their case (MeleeWeapon_LongSword's texPath is LongSword, its file Longsword).
    want = tex.lower()
    for root in SPRITE_ROOTS:
        for path in sorted(glob.glob(os.path.join(root, '**', '*.png'), recursive=True)):
            rel = os.path.relpath(path, root).replace(os.sep, '/').lower()
            if rel in (want + '.png', want + '_east.png'):
                return path, size, offset
    return '', size, offset

def slab_object(name=SLAB):
    """A weapon's slab: two textured faces SLAB_THICK apart, placed by moving their corners (place_slab)."""
    obj = bpy.data.objects.get(name)
    if obj is not None:
        return obj
    me = bpy.data.meshes.new(name)
    me.from_pydata([(0, 0, 0)] * 8, [], [(0, 1, 2, 3), (7, 6, 5, 4)])
    uv = me.uv_layers.new()
    for i, (u, v) in enumerate([(0, 0), (1, 0), (1, 1), (0, 1), (0, 1), (1, 1), (1, 0), (0, 0)]):
        uv.data[i].uv = (u, v)
    mat = bpy.data.materials.new(name)
    try:
        mat.use_nodes = True
    except AttributeError:
        pass
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    nodes.clear()
    tex = nodes.new('ShaderNodeTexImage')
    tex.interpolation = 'Closest'
    shade = nodes.new('ShaderNodeBsdfPrincipled')
    out = nodes.new('ShaderNodeOutputMaterial')
    links.new(tex.outputs['Color'], shade.inputs['Base Color'])
    links.new(tex.outputs['Alpha'], shade.inputs['Alpha'])
    links.new(shade.outputs[0], out.inputs[0])
    if hasattr(mat, 'surface_render_method'):
        mat.surface_render_method = 'DITHERED'
    else:
        mat.blend_method = 'CLIP'
    me.materials.append(mat)
    obj = bpy.data.objects.new(name, me)
    obj.hide_select = True
    bpy.context.scene.collection.objects.link(obj)
    return obj

def top_of(a, roll, front):
    """WalkFigures.TopOf: a weapon's top, straight up as far as its line a lets it (its face to front when it points
    straight up), turned roll degrees about the line. The game's space is this one mirrored, so its turn about the line
    goes the other way here."""
    up = Vector((0, 0, 1))
    top = up - a * up.dot(a)
    if top.length_squared < 0.01:
        top = front - a * front.dot(a)
    return Quaternion(a, math.radians(-roll)) @ top.normalized()

def hand_turn(side):
    """The hand bone's turn from rest as posed now, armature space."""
    name = 'hand.' + side
    return (walk.arm.pose.bones[name].matrix @ walk.rest[name].inverted()).to_quaternion()

def weapon_frame(w):
    """As WalkFigures.DrawWeapon finds them for the pose now on the rig (world space): the fist it is held from (before
    WEAPON_LIFT and the rest), the way the weapon runs, and its top."""
    mw, pose = walk.arm.matrix_world, walk.arm.pose.bones
    k = 1.0 / model_scale()   # Blender units per model unit
    def joint(name):
        return mw @ pose[name].head
    def fist(s):
        """WalkFigures.FistOf: on from the wrist along the forearm."""
        wrist, elbow = joint('hand.' + s), joint('forearm.' + s)
        return wrist + (wrist - elbow).normalized() * walk.HAND_GRIP * k
    side = w['hand']
    other = 'L' if side == 'R' else 'R'
    wrist, elbow = joint('hand.' + side), joint('forearm.' + side)
    if w['line'] == 'hand':
        # Riding the hand: aim and its top set with the hand at rest, turned as the hand is; the fist along the hand.
        q = mw.to_quaternion() @ hand_turn(side)
        hb = pose['hand.' + side]
        hand = wrist + (mw.to_3x3() @ (hb.tail - hb.head)).normalized() * walk.HAND_GRIP * k
        hand += q @ Vector(w.get('at', (0.0, 0.0, 0.0))) * k
        rest = (Vector(w.get('aim', (0.0, -1.0, 0.0))).normalized() + Vector((0, 0, -w['drop']))).normalized()
        a = (q @ rest).normalized()
        top = q @ top_of(rest, w['roll'], Vector((0, -1, 0)))
        return hand, a, (top - a * top.dot(a)).normalized()
    hand = fist(side)
    if w['line'] == 'hands':
        along = fist(other) - hand
    elif w['line'] == 'forearm':
        along = wrist - elbow
    else:
        along = mw.to_3x3() @ Vector(w.get('aim', (0.0, 0.0, 1.0)))
    a = (along.normalized() + Vector((0, 0, -w['drop']))).normalized()
    return hand, a, top_of(a, w['roll'], (mw.to_3x3() @ Vector((0, -1, 0))).normalized())

def weapon_shift(side):
    """WEAPON_LIFT, _OUT and _FORWARD: the whole weapon moved along the figure's own directions (world): up, out to
    the holding hand's side (x is the figure's left), forward (-y)."""
    turn = walk.arm.matrix_world.to_3x3()
    out = (turn @ Vector((1 if side == 'L' else -1, 0, 0))).normalized()
    front = (turn @ Vector((0, -1, 0))).normalized()
    return (Vector((0, 0, 1)) * getattr(walk, 'WEAPON_LIFT', 0.0) + out * getattr(walk, 'WEAPON_OUT', 0.0)
            + front * getattr(walk, 'WEAPON_FORWARD', 0.0)) / model_scale()

def on_hand(side, a, top):
    """The aim and roll on the 'hand' line (no drop) for a weapon running along a with its top up top (world), in the
    pose now on the rig: both taken back to the hand's rest turn."""
    back = (walk.arm.matrix_world.to_quaternion() @ hand_turn(side)).inverted()
    rest, top = (back @ a).normalized(), (back @ top).normalized()
    base = top_of(rest, 0.0, Vector((0, -1, 0)))
    roll = -math.degrees(math.atan2(rest.dot(base.cross(top)), base.dot(top)))
    return tuple(rest), roll

def attach_to_hand(p, kind, w):
    """The aim and roll that put a weapon on the 'hand' line just where w holds it in the pose now on the rig."""
    _, a, top = weapon_frame(w)
    return on_hand(w['hand'], a, top)

def line_changed(kind):
    """A weapon's Line update: switched to 'hand', the weapon is first put on the hand where it was."""
    def changed(self, context):
        old = walk.WEAPONS.get(kind)
        what, shown = clip_of(self)
        if old is not None and old['line'] != 'hand' and getattr(self, 'wp_%s_line' % kind) == 'hand' \
                and what == 'drafted' and shown == kind:
            aim, roll = attach_to_hand(self, kind, old)
            self['wp_%s_aim' % kind] = aim   # quietly: repose below
            self['wp_%s_drop' % kind] = 0.0
            self['wp_%s_roll' % kind] = roll
        repose()
    return changed

def shield_draw_phase(kind, t):
    """How far through shield_draw taking the shield off the back ('draw/<garment>') or putting it back ('put/<garment>')
    shown at t is."""
    return 1.0 - t if kind.startswith('put/') else t

def shield_rim_of(kind):
    """The edge (armature rest space) by which shield_draw's hand takes the shield of a 'draw/<garment>' or
    'put/<garment>' kind, as the export wrote it (held_rim)."""
    fig = figure()
    return Vector(to_rest(fig, fig.piece(kind.partition('/')[2]).held_rim))

def shield_taken(what, kind, t):
    """Where a shield carried on the back is on this clip (WalkFigures' Ease.shieldOut): 1 in the hand on the clips the game
    plays only while it shows the weapon, part way through shield_draw on its clip, else 0 on the back; and the garment
    that clip is for (None for any)."""
    if what == 'shield' and kind:
        at = shield_draw_phase(kind, t) * walk.SHIELD_DRAW_FRAMES / (walk.SHIELD_DRAW_FRAMES - 1)   # as shield_draw_at plays it
        return min(1.0, at), kind.partition('/')[2]
    return (1.0 if what in SHIELD_HELD else 0.0), None

def hip_draw_phase(kind, t):
    """How far through hip_draw taking the hip gear ('draw') or putting it away ('put') shown at t is."""
    return 1.0 - t if kind == 'put' else t

def hip_held(what, kind, t):
    """How far the hip gear has come from its slot to the left hand (WalkFigures' HipShare): all of it holding or pointing
    it, none before hip_draw's hand closes on it, then over HIP_SLIDE; 0 for any other clip."""
    if what != 'gear':
        return 0.0
    if kind not in ('draw', 'put'):
        return 1.0
    at = hip_draw_phase(kind, t) * walk.HIP_DRAW_FRAMES / (walk.HIP_DRAW_FRAMES - 1)   # as hip_draw_at plays it
    return walk.smooth(min(1.0, max(0.0, (at - walk.HIP_GRAB) / walk.HIP_SLIDE)))

def drawn_from(what, kind, t):
    """For a draw or holster clip, its slot and how far its weapon has come from there to the hand (WalkFigures.DrawnShare);
    else None."""
    if what not in ('draw', 'holster'):
        return None
    at, slide = draw_phase(what, t), walk.DRAW_SIZES[walk.HOLSTERS[walk.draw_names()[kind][0]]['size']]['slide']
    return walk.draw_names()[kind][0], walk.smooth(min(1.0, max(0.0, (at - walk.DRAW_AT + slide) / slide))), walk.draw_keys(kind)['guard']

def draw_weapon(p, kind, drawn=None):
    """Places the weapon's slab as WalkFigures.DrawWeapon does, in the hands of the pose now on the rig; hidden when
    the clip has no weapon, or it is still in its holster (drawn: drawn_from's)."""
    w = getattr(walk, 'WEAPONS', {}).get(kind) if kind else None
    obj = bpy.data.objects.get(SLAB)
    if w is None or not p.weapon or (drawn is not None and drawn[1] <= 0.0):
        for o in (obj, bpy.data.objects.get(SLAB + MODEL)):
            if o is not None:
                o.hide_viewport = True
        return
    obj = slab_object()
    obj.hide_viewport = False
    bpy.context.view_layer.update()   # the bones' new places
    hand, a, top = weapon_frame(w)
    hand, behind = hand + weapon_shift(w['hand']), w['behind']
    if drawn is not None and drawn[1] < 1.0:
        fig = figure()
        at, a0, top0 = holster_frame(p, drawn[0], kind, fig.holster_clear(worn_now(p, fig), fig.slot(drawn[0], walk.HOLSTERS[drawn[0]]),
                                                                           weapon_depth(p, kind) + holster_push.STACK_GAP))
        q = Matrix((a0, top0, a0.cross(top0))).transposed().to_quaternion().slerp(Matrix((a, top, a.cross(top))).transposed().to_quaternion(), drawn[1])
        a, top = q @ Vector((1, 0, 0)), q @ Vector((0, 1, 0))
        hand, behind = at.lerp(hand, drawn[1]), walk.HOLSTERS[drawn[0]].get('behind', 0.0) * (1.0 - drawn[1]) + behind * drawn[1]
    place_slab(p, obj, kind, hand, a, top, behind)

def place_slab(p, obj, kind, hand, a, top, behind):
    """Puts a slab on the line a from hand (world), its top toward top, its middle behind (a share of its length) on
    along it, sized and textured as the WEAPONS entry kind's sprite settings have it (WalkFigures.DrawSlab). With IsoRim
    models ticked and an IsoRim model for the weapon, that model is shown laid into the slab instead (WalkFigures.DrawModel)
    and the slab hidden. Gives the name of the model's object, or None."""
    k = 1.0 / model_scale()   # Blender units per model unit
    o = math.radians(getattr(p, 'wp_%s_offset' % kind))
    c, sn = math.cos(o), math.sin(o)
    size = getattr(p, 'wp_%s_size' % kind)
    img, problem = sprite(getattr(p, 'wp_%s_image' % kind))
    rel = isorim_models().get(KIND_LOOK.get(kind, kind))
    # model units per cell: the game sizes it in cells, not with the figure, over the weapon's own GroundRatio
    scale = walk.weapon_scale(kind) / ground_ratio(kind, rel, img, tuple(size), getattr(p, 'wp_%s_offset' % kind)) / walk.GAME_SCALE
    dx, dy = size[0] * scale * k, size[1] * scale * k
    x, y = (a * c - top * sn) * dx, (a * sn + top * c) * dy
    middle = hand + a * (behind * max(dx, dy))
    z = x.cross(y).normalized() * (SLAB_THICK / walk.GAME_SCALE * k / 2)
    corners = [(0, 0), (1, 0), (1, 1), (0, 1)]
    me = obj.data
    for i, (u, v) in enumerate(corners + corners):
        me.vertices[i].co = middle + x * (u - 0.5) + y * (v - 0.5) + (z if i < 4 else -z)
    me.update()
    tex = next(n for n in me.materials[0].node_tree.nodes if n.type == 'TEX_IMAGE')
    if tex.image != img:
        tex.image = img
    if problem:
        state['error'] = 'Sprite: ' + problem
    shown = None
    if rel is not None and p.weapon_models:
        try:
            shown = place_model(obj.name + MODEL, rel, img, tuple(size), getattr(p, 'wp_%s_offset' % kind), x, y,
                                scale * k, middle, isorim_scales().get(KIND_LOOK.get(kind, kind), 1.0)
                                if walk.weapon_type(kind) == 'grenade' else None)
        except Exception as e:   # a model the fit can't read: the slab stays
            state['error'] = 'Model %s: %s' % (rel, e)
    obj.hide_viewport = shown is not None
    held = bpy.data.objects.get(obj.name + MODEL)
    if held is not None and shown is None:
        held.hide_viewport = True
    return shown

# ---- IsoRim's weapon models (WeaponModels): each laid into the sprite's slab so that its outline covers the sprite's.
# Fitted as IsoRim fits them (WeaponModels.Fit), in the game's model space: the OBJ with z flipped, one entry per corner.

MODEL = '.model'   # + to a slab's name: its weapon's model
FIT_N = 64             # WeaponModels.N: the sprite's mask, N x N
FIT_FRONT = 0.5        # WeaponModels.FrontBonus is 0.08, which lets a minigun fit backwards along a level sprite
FIT_UP = 0.2           # WeaponModels.UpBonus
FIT_SPARSE = 1.6       # WeaponModels.Sparse
FIT_LONG = 0.9         # WeaponModels.LongAxis
FIT_UP_ALONG = 0.87    # WeaponFit.UpAlong: the art's front this close (cosine) to the axis tried for its top to count
FIT_UP_TIE = 0.01      # WeaponFit.UpTie: else the top bonus only breaks ties

def ground_ratio(kind, rel, img, draw, offset):
    """WeaponTypes.GroundRatioOf: how many times bigger the weapon's IsoRim model rel is fitted to its sprite than IsoRim
    draws it on the ground (its ModelDef's scale), which the game divides the type's size by (about 1.36 for IsoRim's
    own models). 1 without a model or for a thrown weapon. Taken whether or not IsoRim models is ticked, as the game has
    IsoRim's weapon models on by default and a slab drawn in its place is the sprite's length all the same."""
    if rel is None or walk.weapon_type(kind) == 'grenade':
        return 1.0
    ground = isorim_scales().get(KIND_LOOK.get(kind, kind), 1.0)
    if ground <= 0.0:
        return 1.0
    fits = registered.setdefault('model_fits', {})
    key = (rel, img.name, draw, round(offset, 3))
    try:
        if key not in fits:
            fits[key] = fit_model(isorim_model(rel)[0], sprite_mask(img), draw, offset)
    except Exception:   # a model the fit can't read: drawn as the sprite
        return 1.0
    if fits[key] is None:
        return 1.0
    slab = fits[key][0]
    return float(np.linalg.norm(slab[:3, 0] * np.array(draw + (1.0,)))) / ground

def isorim_scales():
    """thingDef -> its ModelDef's scale (IsoRim's size on the ground, cells per model unit). Read with isorim_models."""
    isorim_models()
    return registered['isorim_scales']

def isorim_models():
    """thingDef -> model file (relative to IsoRim's folder) for each IsoRim ModelDef bound to a thing by its defName
    (WeaponModels.Uses: its own model), not flat. Read once."""
    if 'isorim_models' in registered:
        return registered['isorim_models']
    out, scales = {}, {}
    for path in sorted(glob.glob(os.path.join(ISORIM_ROOT, 'Defs', '**', '*.xml'), recursive=True)):
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError):
            continue
        for el in root.iter('IsoRim.ModelDef'):
            thing, model = (el.findtext('thingDef') or '').strip(), (el.findtext('model') or '').strip()
            if thing and model and (el.findtext('flat') or '').strip().lower() != 'true':
                out[thing] = model
                try:
                    scales[thing] = float((el.findtext('scale') or '1').strip())
                except ValueError:
                    scales[thing] = 1.0
    registered['isorim_models'], registered['isorim_scales'] = out, scales
    return out

def isorim_model(rel):
    """An IsoRim OBJ as its loader has it: (corners (T, 3, 3) in the game's model space, each triangle's material name,
    material name -> Kd colour). Read once."""
    cache = registered.setdefault('isorim_model', {})
    if rel in cache:
        return cache[rel]
    path = os.path.join(ISORIM_ROOT, rel)
    vs, tris, mats, colours, current = [], [], [], {}, ''
    with open(path) as f:
        lines = f.read().splitlines()
    for line in lines:
        q = line.split()
        if not q:
            continue
        if q[0] == 'mtllib':
            mtl = os.path.join(os.path.dirname(path), ' '.join(q[1:]))
            if os.path.isfile(mtl):
                name = None
                for ml in open(mtl).read().splitlines():
                    mq = ml.split()
                    if mq and mq[0] == 'newmtl':
                        name = ' '.join(mq[1:])
                    elif mq and mq[0] == 'Kd' and name:
                        colours[name] = tuple(float(v) for v in mq[1:4])
        elif q[0] == 'usemtl':
            current = ' '.join(q[1:])
        elif q[0] == 'v':
            vs.append((float(q[1]), float(q[2]), -float(q[3])))   # flipping z into the game's axes, as ObjLoader does
        elif q[0] == 'f':
            face = []
            for c in q[1:]:
                i = int(c.split('/')[0])
                face.append(i - 1 if i > 0 else len(vs) + i)
            for i in range(1, len(face) - 1):
                tris.append((vs[face[0]], vs[face[i]], vs[face[i + 1]]))
                mats.append(current)
    cache[rel] = (np.array(tris, dtype=float), mats, colours)
    return cache[rel]

def sprite_mask(img):
    """The sprite's opaque pixels on an N x N grid (row 0 the bottom), each the mean alpha of the pixels under it over
    half (SpritePixels.Sample at a mip of about N, alpha above 128)."""
    w, h = img.size
    a = np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)[:, :, 3]
    rows, cols = (np.arange(h) * FIT_N) // h, (np.arange(w) * FIT_N) // w
    sums, counts = np.zeros((FIT_N, FIT_N)), np.zeros((FIT_N, FIT_N))
    np.add.at(sums, (rows[:, None], cols[None, :]), a)
    np.add.at(counts, (rows[:, None], cols[None, :]), 1.0)
    return sums / np.maximum(counts, 1.0) > 128.0 / 255.0

def one_piece(mask):
    """WeaponModels.OnePiece: a sprite of several of the thing apart (a heap of grenades) cut to its largest piece."""
    label = np.zeros(mask.shape, dtype=int)
    sizes = [0]
    for j0, i0 in zip(*np.nonzero(mask)):
        if label[j0, i0]:
            continue
        sizes.append(0)
        label[j0, i0] = len(sizes) - 1
        todo = [(j0, i0)]
        while todo:
            j, i = todo.pop()
            sizes[-1] += 1
            for nj, ni in ((j, i + 1), (j, i - 1), (j + 1, i), (j - 1, i)):
                if 0 <= nj < FIT_N and 0 <= ni < FIT_N and mask[nj, ni] and not label[nj, ni]:
                    label[nj, ni] = len(sizes) - 1
                    todo.append((nj, ni))
    total = sum(sizes)
    best = max(range(len(sizes)), key=lambda n: sizes[n])
    big = sum(1 for n in sizes[1:] if n >= total * 0.15)
    if big < 2 or sizes[best] >= total * 0.85:
        return mask
    return label == best

def art_front(draw, offset):
    """WeaponModels.ArtFront: the way the game points the art, slab space at the drawn size, unit length."""
    o = math.radians(offset)
    d = np.array((math.cos(o) * draw[0], math.sin(o) * draw[1]))
    n = np.linalg.norm(d)
    return d / n if n > 1e-4 else np.array((1.0, 0.0))

def unit2(v):
    n = np.linalg.norm(v)
    return v / n if n > 1e-9 else v * 0.0

def raster(tris2):
    """WeaponModels.Raster: the triangles (T, 3, 2, in grid units) filled into the N x N grid."""
    a, b, c = tris2[:, 0], tris2[:, 1], tris2[:, 2]
    area = (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (b[:, 1] - a[:, 1]) * (c[:, 0] - a[:, 0])
    keep = np.abs(area) >= 1e-6
    a, b, c, area = a[keep], b[keep], c[keep], area[keep][:, None]
    g = np.arange(FIT_N) + 0.5
    qx, qy = np.meshgrid(g, g)   # [row j, column i]
    qx, qy = qx.ravel()[None, :], qy.ravel()[None, :]
    def edge(p, r):
        return (p[:, 0:1] - qx) * (r[:, 1:2] - qy) - (p[:, 1:2] - qy) * (r[:, 0:1] - qx)
    w0, w1 = edge(b, c) / area, edge(c, a) / area
    inside = (w0 >= -1e-4) & (w1 >= -1e-4) & (1.0 - w0 - w1 >= -1e-4)
    return inside.any(axis=0).reshape(FIT_N, FIT_N)

def extreme(P, place, d, forward):
    """WeaponModels.Extreme: the middle of the placed corners furthest along d (or against it)."""
    q = P @ place[:3, :3].T + place[:3, 3]
    along = (1.0 if forward else -1.0) * (q[:, 0] * d[0] + q[:, 1] * d[1])
    return q[along >= along.max() - 0.02].mean(axis=0)

def fit_model(tris, mask, draw, offset):
    """WeaponModels.Fit: (the model laid into the slab's unit square, its barrel end in slab space, how well it covers
    the sprite), or None for a sprite with nothing opaque."""
    sprite = one_piece(mask)
    js, is_ = np.nonzero(sprite)
    if len(js) == 0:
        return None
    pts = np.stack((((is_ + 0.5) / FIT_N - 0.5) * draw[0], ((js + 0.5) / FIT_N - 0.5) * draw[1]), axis=1)
    best_width, theta = float('inf'), 0.0
    for deg in range(180):
        r = math.radians(deg)
        d = pts @ np.array((-math.sin(r), math.cos(r)))
        if d.max() - d.min() < best_width - 1e-5:
            best_width, theta = d.max() - d.min(), deg
    front = art_front(draw, offset)
    P = tris.reshape(-1, 3)
    lo, hi = P.min(axis=0), P.max(axis=0)
    ext, mid = hi - lo, (lo + hi) * 0.5
    model_length = ext.max()
    best = None
    for axis in ((theta, 0.0) if theta != 0 else (0.0,)):
        tried = fit_along(tris, sprite, pts, axis, draw, front, ext, mid, model_length)
        if tried is not None and (best is None or tried[0] > best[0]):
            best = tried
    if best is None:
        return None
    _, place, iou, filled, centre = best
    if filled > len(js) * FIT_SPARSE:   # a heap of a few small things, one model: sized by area
        c3 = (centre[0], centre[1], 0.0)
        place = translate(c3) @ scaled(math.sqrt(len(js) / filled)) @ translate(-np.array(c3)) @ place
    unit = np.diag((1.0 / draw[0], 1.0 / draw[1], 1.0, 1.0))
    if unit2(place[:2, 0]) @ front > 0.3:   # its front came out toward the art's: GunEnd
        end = P[P[:, 0] >= P[:, 0].max() - 0.02].mean(axis=0)
        tip = place[:3, :3] @ end + place[:3, 3]
    else:
        tip = extreme(P, place, front, True)
    return unit @ place, unit[:3, :3] @ tip, iou

def translate(v):
    m = np.identity(4)
    m[:3, 3] = v
    return m

def scaled(f):
    return np.diag((f, f, f, 1.0))

def fit_along(tris, sprite, pts, theta, draw, front, ext, mid, model_length):
    """The best of the 24 square turns of the model laid along the sprite axis theta (degrees from +u toward +v):
    (score, placing, iou, pixels drawn, the sprite's middle), or None."""
    r = math.radians(theta)
    along, across = np.array((math.cos(r), math.sin(r))), np.array((-math.sin(r), math.cos(r)))
    da, dp = pts @ along, pts @ across
    sprite_length = da.max() - da.min()
    centre = along * (da.min() + da.max()) * 0.5 + across * (dp.min() + dp.max()) * 0.5
    rot = np.identity(4)
    rot[:2, 0], rot[:2, 1] = along, across
    def grid(q):
        return np.stack(((q[..., 0] / draw[0] + 0.5) * FIT_N, (q[..., 1] / draw[1] + 0.5) * FIT_N), axis=-1)
    up_bonus = FIT_UP if abs(along @ front) >= FIT_UP_ALONG else FIT_UP_TIE
    best = None
    perms = ((0, 1, 2), (0, 2, 1), (1, 0, 2), (1, 2, 0), (2, 0, 1), (2, 1, 0))
    perm_sign = (1, -1, -1, 1, 1, -1)
    for pi, perm in enumerate(perms):
        for sx in (-1, 1):
            for sy in (-1, 1):
                sz = perm_sign[pi] * sx * sy   # a proper turn (no mirror)
                lx = ext[perm[0]]
                if lx < 1e-4 or lx < model_length * FIT_LONG:
                    continue
                R = np.zeros((4, 4))
                R[0, perm[0]], R[1, perm[1]], R[2, perm[2]], R[3, 3] = sx, sy, sz, 1.0
                place = (translate((centre[0], centre[1], 0.0)) @ rot @ scaled(sprite_length / lx) @ R
                         @ translate(-mid))
                q = tris @ place[:3, :3].T + place[:3, 3]
                drawn = raster(grid(q[..., :2]))
                both, either = (drawn & sprite).sum(), (drawn | sprite).sum()
                iou = both / either if either else 0.0
                mf, mu = place[:2, 0], place[:2, 1]
                score = (iou + (FIT_FRONT if unit2(mf) @ front > 0.3 else 0.0)
                         + (up_bonus if unit2(mu) @ np.array((-front[1], front[0])) > 0.3 else 0.0))
                if best is None or score > best[0]:
                    best = (score, place, iou, drawn.sum(), centre)
    return best

def model_material(name, colour):
    mat = bpy.data.materials.get(MODEL_MAT + name)
    if mat is None:
        mat = bpy.data.materials.new(MODEL_MAT + name)
        mat.diffuse_color = tuple(colour) + (1.0,)
        try:
            mat.use_nodes = True
        except AttributeError:
            pass
        shade = next((n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
        if shade is not None:
            shade.inputs['Base Color'].default_value = tuple(colour) + (1.0,)
    return mat

MODEL_MAT = 'MaruIsoRim.'   # + an IsoRim material name: a weapon model's colour (its Kd)

def place_model(name, rel, img, draw, offset, x, y, cell, middle, ground=None):
    """IsoRim's model rel shown as the object name, laid into the slab with corners middle +- x/2, y/2 (world) as
    WeaponModels fits it to the sprite img at draw size draw (cells) and angle offset offset. cell: Blender units per
    cell at the weapon's held size, which the slab's thickness axis is scaled by (WalkFigures.DrawModel). The preview's
    space is the game's mirrored, so that axis is the other way round from x cross y. ground: a thrown weapon's ModelDef
    scale, its model then sized as on the ground (WeaponFit.AtGroundSize), not as its sprite's heap. Gives name."""
    tris, mats, colours = isorim_model(rel)
    fits = registered.setdefault('model_fits', {})
    key = (rel, img.name, draw, round(offset, 3))
    if key not in fits:
        fits[key] = fit_model(tris, sprite_mask(img), draw, offset)
    fitted = fits[key]
    if fitted is None:
        raise ValueError('the sprite has nothing to fit it to')
    slab, _, iou = fitted
    if ground is not None:
        slab = at_ground_size(slab, tris, draw, ground)
    obj = bpy.data.objects.get(name)
    if obj is not None and obj.get('model') != rel:
        me = obj.data
        bpy.data.objects.remove(obj)
        bpy.data.meshes.remove(me)
        obj = None
    if obj is None:
        me = bpy.data.meshes.new(name)
        n = len(tris)
        me.from_pydata([tuple(c) for c in tris.reshape(-1, 3)], [], [(3 * t, 3 * t + 1, 3 * t + 2) for t in range(n)])
        names = sorted(set(mats))
        for m in names:
            me.materials.append(model_material(m, colours.get(m, (0.5, 0.5, 0.5))))
        for t, m in enumerate(mats):
            me.polygons[t].material_index = names.index(m)
        me.update()
        obj = bpy.data.objects.new(name, me)
        obj['model'] = rel
        obj.hide_select = True
        obj.hide_render = True
        bpy.context.scene.collection.objects.link(obj)
    z = -x.cross(y).normalized() * cell
    S = Matrix((x.to_4d(), y.to_4d(), z.to_4d(), middle.to_4d())).transposed()
    for c in range(3):
        S[3][c] = 0.0
    obj.matrix_world = S @ Matrix(slab.tolist())
    obj['fit'] = round(float(iou), 2)
    obj.hide_viewport = False
    return name

def at_ground_size(slab, tris, draw, ground):
    """WeaponFit.AtGroundSize: the fitted slab scaled evenly in drawn cells about the model's middle, so the model comes
    out at ground (its ModelDef's scale, cells per model unit) rather than the size it was fitted to."""
    back = np.diag((draw[0], draw[1], 1.0, 1.0))
    fitted = float(np.linalg.norm((back @ slab)[:3, 0]))   # drawn cells per model unit
    if fitted < 1e-6 or ground <= 0.0:
        return slab
    P = tris.reshape(-1, 3)
    c = slab @ np.append((P.min(axis=0) + P.max(axis=0)) * 0.5, 1.0)
    unit = np.diag((1.0 / draw[0], 1.0 / draw[1], 1.0, 1.0))
    return translate(c[:3]) @ unit @ scaled(ground / fitted) @ back @ translate(-c[:3]) @ slab

def clear_slab(*_):
    """Takes the slab, its material, the sprites and the hand handles out of the file (before a save, and when the
    panel goes)."""
    clear_handles()
    clear_tool()
    clear_load()
    show_rig_garments()
    for name in [SLAB, SLAB + MODEL] + [o.name for o in bpy.data.objects if o.name.startswith((HOLSTER, GEAR_OBJ, WORN, 'MaruPack'))]:
        obj = bpy.data.objects.get(name)
        if obj is not None:
            me = obj.data
            bpy.data.objects.remove(obj)
            bpy.data.meshes.remove(me)
        mat = bpy.data.materials.get(name)
        if mat is not None:
            bpy.data.materials.remove(mat)
    for mat in [m for m in bpy.data.materials if m.name.startswith((MODEL_MAT, WORN)) and m.users == 0]:
        bpy.data.materials.remove(mat)
    for name in registered.get('images', set()) | {SLAB + 'StandIn'}:
        img = bpy.data.images.get(name)
        if img is not None and img.users == 0:
            bpy.data.images.remove(img)

TOOL = 'MaruTool'

def draw_tool(kind, t, X=None):
    """IsoRim's tool for a work clip, placed in the right hand as walk.tool_matrix puts it (the game's tool line); taken
    away for any other clip. A mocap stroke (walk.work_mocap) carries it in the hand of X, the pose shown, held as the
    posed stroke holds it."""
    obj = bpy.data.objects.get(TOOL)
    stirring = kind in getattr(walk, 'STIR_KINDS', {}) and X is not None
    w = walk.WORK.get(kind) if kind and kind not in ('read', 'smoke') and not stirring else None
    model = (w.get('tool') if w else 'open_book' if kind == 'read' and X is not None
             else 'tool_joint' if kind == 'smoke' and X is not None else getattr(walk, 'stir_tool', lambda k: 'tool_spoon')(kind) if stirring else None)
    if model is None:
        if obj is not None:
            obj.hide_viewport = True
        return
    if obj is None or obj.get('model') != model:
        if obj is not None:
            me = obj.data
            bpy.data.objects.remove(obj)
            bpy.data.meshes.remove(me)
        vs, fs = [], []
        own = os.path.join(MOD_MODELS, model + '.obj')   # this mod's own tool first (the fishing rod), else IsoRim's
        with open(own if os.path.exists(own) else os.path.join(ISORIM_MODELS, model + '.obj')) as f:
            for line in f:
                q = line.split()
                if q and q[0] == 'v':
                    x, y, z = map(float, q[1:4])
                    vs.append((-x, z, y))   # the game's model space (IsoRim's loader flips z) in Blender's axes
                elif q and q[0] == 'f':
                    fs.append([int(i.split('/')[0]) - 1 for i in q[1:]])
        me = bpy.data.meshes.new(TOOL)
        me.from_pydata(vs, [], fs)
        obj = bpy.data.objects.new(TOOL, me)
        obj['model'] = model
        bpy.context.scene.collection.objects.link(obj)
    obj.hide_viewport = False
    if kind == 'read':
        obj.matrix_world = walk.arm.matrix_world @ X['hand.L'] @ walk.read_rest()   # the open book, as the tool read line
        return
    if kind == 'smoke':
        obj.matrix_world = walk.arm.matrix_world @ X['hand.R'] @ walk.smoke_rest()   # the joint, as the tool smoke line
        return
    if stirring:
        obj.matrix_world = walk.arm.matrix_world @ X['hand.R'] @ walk.stir_rest(kind)   # the spoon or the roasting stick, as the tool stir line
        return
    if X is not None and getattr(walk, 'work_mocap', lambda k: None)(kind) is not None:
        rest, _ = walk.tool_stroke_at(0.0, kind)
        held = rest['hand.R'].inverted() @ walk.tool_matrix(kind, walk.work_key(w, walk.work_stroke(0.0, w)))
        obj.matrix_world = walk.arm.matrix_world @ X['hand.R'] @ held
    else:
        obj.matrix_world = walk.arm.matrix_world @ walk.tool_matrix(kind, walk.work_key(w, walk.work_stroke(t, w)))

INSTRUMENT = 'MaruInstrument'
# Each music clip's instrument: IsoRim's model of it and how far its middle is from the middle of the pawn's cell
# (cells: the game's interaction cell, two from a piano's or harpsichord's middle, one from a harp's or drum's).
INSTRUMENT_MODELS = {'piano': ('piano', 2.0), 'harp': ('harp', 1.0), 'drum': ('ritual_drum', 1.0)}

def draw_instrument(what):
    """IsoRim's model of the instrument a music clip plays (walk.MUSIC), where the game draws it from the figure stepped
    up to it by the clip's 'closer' (seated, the floor MUSIC_SEAT under the sit's own, as the chair's seat lifts it);
    taken away for any other clip."""
    obj = bpy.data.objects.get(INSTRUMENT)
    kind = what[:-4] if what.endswith('_sit') else what
    if not hasattr(walk, 'MUSIC') or kind not in INSTRUMENT_MODELS:
        if obj is not None:
            obj.hide_viewport = True
        return
    model, middle = INSTRUMENT_MODELS[kind]
    if obj is None or obj.get('model') != model:
        if obj is not None:
            me = obj.data
            bpy.data.objects.remove(obj)
            bpy.data.meshes.remove(me)
        vs, fs = [], []
        with open(os.path.join(ISORIM_MODELS, model + '.obj')) as f:
            for line in f:
                q = line.split()
                if q and q[0] == 'v':
                    x, y, z = map(float, q[1:4])
                    vs.append((-x, z, y))   # the game's model space (IsoRim's loader flips z) in Blender's axes
                elif q and q[0] == 'f':
                    fs.append([int(i.split('/')[0]) - 1 for i in q[1:]])
        me = bpy.data.meshes.new(INSTRUMENT)
        me.from_pydata(vs, [], fs)
        obj = bpy.data.objects.new(INSTRUMENT, me)
        obj['model'] = model
        bpy.context.scene.collection.objects.link(obj)
    obj.hide_viewport = False
    seated = what.endswith('_sit')
    cell = walk.LEG / walk.MODEL_LEG / walk.GAME_SCALE   # armature units per cell, for a grown pawn
    closer = walk.MUSIC[kind]['closer'][1 if seated else 0]
    at = Vector((0.0, -(middle - closer), -(walk.MUSIC_SEAT if seated else 0.0))) * cell
    obj.matrix_world = walk.arm.matrix_world @ Matrix.Translation(at) @ Matrix.Scale(cell, 4)

def clear_tool():
    for name in (TOOL, INSTRUMENT):   # (the instrument too: neither is saved with the .blend)
        obj = bpy.data.objects.get(name)
        if obj is not None:
            me = obj.data
            bpy.data.objects.remove(obj)
            bpy.data.meshes.remove(me)

LOAD = 'MaruLoad'
ITEM_SCALE = 0.8   # IsoRim's Carried.ItemScale: a carried thing's share of its drawn size
LOAD_REACH = 0.2   # WalkFigures.LoadReach: model units in front that a carried thing's middle is at least

def chest_now():
    """The chest bone as posed on the rig now (armature space, from rest), laid over a gait or not."""
    return walk.arm.pose.bones[walk.CHEST_BONE].matrix @ walk.rest[walk.CHEST_BONE].inverted()

def load_object(name, material):
    """One of the load's preview objects (a mesh, unselectable, so the hand handles can be picked through it)."""
    obj = bpy.data.objects.get(name)
    if obj is None:
        obj = bpy.data.objects.new(name, bpy.data.meshes.new(name))
        obj.hide_select = True
        obj.hide_render = True
        bpy.context.scene.collection.objects.link(obj)
    if material and not obj.data.materials:
        mat = bpy.data.materials.get(LOAD) or bpy.data.materials.new(LOAD)
        mat.diffuse_color = (0.35, 0.6, 1.0, 1.0)
        obj.data.materials.append(mat)
    return obj

PET = 'MaruPet'
PET_MODEL = 'animal_cat_blob'   # IsoRim's cat (R3D_Animal_Cat: scale 1), textured by PET_TEXTURE
PET_TEXTURE = os.path.join(ISORIM_ROOT, 'Textures', 'IsoRim', 'Animals', 'Cat.png')
PET_BELLY = 0.5        # WalkFigures.PetBelly: of the height of its back, how far the pet is sunk below the forearm
PET_BACK_SHARE = 0.45  # WalkFigures.PetBackShare: of its model's height, the top of its back

def pet_mesh():
    """IsoRim's cat as a mesh object in the game's model space (cells) in Blender's axes, with its texture places and
    image; None without the model. Made once (not saved with the .blend, as the other preview objects)."""
    obj = bpy.data.objects.get(PET)
    if obj is not None:
        return obj
    path = os.path.join(ISORIM_MODELS, PET_MODEL + '.obj')
    if not os.path.exists(path):
        return None
    vs, uvs, fs = [], [], []
    with open(path) as f:
        for line in f:
            q = line.split()
            if not q:
                continue
            if q[0] == 'v':
                x, y, z = map(float, q[1:4])
                vs.append((-x, z, y))   # the game's model space (IsoRim's loader flips z) in Blender's axes
            elif q[0] == 'vt':
                uvs.append((float(q[1]), float(q[2])))
            elif q[0] == 'f':
                corners = [c.split('/') for c in q[1:]][::-1]   # (written reversed for IsoRim's mirroring loader)
                fs.append([(int(c[0]) - 1, int(c[1]) - 1 if len(c) > 1 and c[1] else -1) for c in corners])
    me = bpy.data.meshes.new(PET)
    me.from_pydata(vs, [], [[v for v, _ in face] for face in fs])
    if uvs:
        layer = me.uv_layers.new(name='UVMap')
        i = 0
        for face in fs:
            for _, t in face:
                if t >= 0:
                    layer.data[i].uv = uvs[t]
                i += 1
    for poly in me.polygons:
        poly.use_smooth = True
    mat = bpy.data.materials.get(PET) or bpy.data.materials.new(PET)
    mat.diffuse_color = (0.85, 0.6, 0.35, 1.0)   # (Solid view: a ginger cat)
    if os.path.exists(PET_TEXTURE):
        mat.use_nodes = True
        nodes = mat.node_tree.nodes
        bsdf = next((n for n in nodes if n.type == 'BSDF_PRINCIPLED'), None)
        tex = nodes.new('ShaderNodeTexImage')
        tex.image = bpy.data.images.load(PET_TEXTURE, check_existing=True)
        if bsdf is not None:
            mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    me.materials.append(mat)
    obj = bpy.data.objects.new(PET, me)
    obj.hide_select = True   # so the hand handles can be picked through it
    obj.hide_render = True
    bpy.context.scene.collection.objects.link(obj)
    return obj

def draw_pet(mw, mid):
    """Puts IsoRim's cat on a cuddle clip's forearm (mid: the left forearm's PET_ALONG point lifted PET_LIFT, armature
    space), as WalkFigures.DrawLoad does for a grown cat on a standard grown pawn. False without the model."""
    obj = pet_mesh()
    if obj is None:
        return False
    cell = walk.LEG / walk.MODEL_LEG / walk.GAME_SCALE   # armature units per cell, for a grown pawn
    height = max(v.co.z for v in obj.data.vertices)   # Api.ModelHeight: its top, cells
    sink = height * PET_BACK_SHARE * PET_BELLY
    # Its front (-y, as the figure's) turned to +x, the carrier's left (the game's Rotation: yaw - 90).
    obj.matrix_world = (mw @ Matrix.Translation(mid - Vector((0, 0, sink * cell)))
                        @ Matrix.Rotation(math.radians(90.0), 4, 'Z') @ Matrix.Scale(cell, 4))
    obj.hide_viewport = False
    state['pet_back'] = height * PET_BACK_SHARE * (1 - PET_BELLY) * cell / walk.LEG   # its back over mid, leg lengths
    return True

def carried_mesh(kind):
    """The body posed as the carried person (walk.carried_at at the start of its loop, as carry_at places the hands
    from), armature space, as (vertices, faces); kept until the numbers change. The rig is put back as it was."""
    names = tuned()[0]
    key = (kind, repr(walk.WALK), repr(walk.RUN), repr([getattr(walk, n, None) for n, _, _ in names]))
    if state.get('carried_key') == key:
        return state['carried_mesh']
    pose = walk.arm.pose
    kept = {pb.name: pb.matrix_basis.copy() for pb in pose.bones}
    walk.apply_pose(walk.carried_at(kind, 0.0))
    bpy.context.view_layer.update()
    to_arm = walk.arm.matrix_world.inverted()
    vs, fs = [], []
    try:
        for body in walk.body_parts():
            ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
            me = ev.to_mesh()
            m = to_arm @ body.matrix_world
            base = len(vs)
            vs += [m @ v.co for v in me.vertices]
            fs += [[base + i for i in f.vertices] for f in me.polygons]
            ev.to_mesh_clear()
    finally:
        for pb in pose.bones:
            pb.matrix_basis = kept[pb.name]
        bpy.context.view_layer.update()
    state['carried_key'], state['carried_mesh'] = key, (vs, fs)
    return vs, fs

def draw_load(p, what, kind):
    """What a carry clip holds, where the game puts it, so the hands can be set against it: an item's box between
    the wrists, its bottom on them and its middle at least LOAD_REACH in front (WalkFigures.DrawLoad, IsoRim's
    Carried.Draw lays it flat and square to the map: the figure facing south); a person as the carried loop put on the
    chest as posed (DrawHeld), a baby shrunk about its hips. Hidden for any other clip."""
    shown = p.load and what == 'carry' and kind in walk.CARRY
    pet = shown and walk.CARRY[kind].get('pet')
    item = shown and (kind == 'item' or pet)
    person = shown and kind != 'item' and not pet
    state.pop('pet_back', None)
    for name, on in ((LOAD + 'Item', item and not (pet and pet_mesh() is not None)), (LOAD + 'Person', person), (PET, pet)):
        obj = bpy.data.objects.get(name)
        if obj is not None and not on:
            obj.hide_viewport = True
    if not shown:
        return
    bpy.context.view_layer.update()   # the bones' new places
    mw = walk.arm.matrix_world
    if pet:
        # A cuddled pet (walk.pet_on): IsoRim's cat where the game draws it (WalkFigures.DrawLoad): its base point on the
        # left forearm's PET_ALONG point, PET_LIFT over it, let down PET_BELLY of its back's height (PetBackOf), head to
        # the carrier's left. Without IsoRim's model, a box for its trunk, its top at walk.PET_BACK.
        pose = walk.arm.pose.bones
        fa = pose['forearm.L']
        mid = fa.head.lerp(fa.tail, walk.PET_ALONG) + Vector((0, 0, walk.PET_LIFT * walk.LEG))
        if draw_pet(mw, mid):
            return
        length, width, back = 0.5 * walk.LEG, 0.22 * walk.LEG, walk.PET_BACK * walk.LEG
        obj = load_object(LOAD + 'Item', False)
        obj.display_type = 'WIRE'
        obj.show_in_front = True
        me = obj.data
        if not me.vertices:
            me.from_pydata([(x, y, z) for x in (-0.5, 0.5) for y in (-0.5, 0.5) for z in (0.0, 1.0)], [],
                           [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)])
        obj.matrix_world = mw @ Matrix.Translation(mid - Vector((0, 0, back * 0.25))) @ Matrix.Diagonal((length, width, back * 1.25, 1.0))
        obj.hide_viewport = False
        return
    if item:
        k = 1.0 / model_scale()   # Blender units per model unit
        pose = walk.arm.pose.bones
        hands = (pose['hand.L'].head + pose['hand.R'].head) / 2   # armature space
        hands.y = min(hands.y, -LOAD_REACH * k / mw.to_scale().y)   # the figure faces -y
        bottom = mw @ hands
        size = p.load_size
        dx, dy, dz = (size[0] * ITEM_SCALE / walk.GAME_SCALE * k, size[1] * ITEM_SCALE / walk.GAME_SCALE * k,
                      p.load_thick / walk.GAME_SCALE * k)
        obj = load_object(LOAD + 'Item', False)
        obj.display_type = 'WIRE'
        obj.show_in_front = True
        me = obj.data
        if not me.vertices:
            me.from_pydata([(x, y, z) for x in (-0.5, 0.5) for y in (-0.5, 0.5) for z in (0.0, 1.0)], [],
                           [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)])
        obj.matrix_world = Matrix.Translation(bottom) @ Matrix.Diagonal((dx, dy, dz, 1.0))
    else:
        obj = load_object(LOAD + 'Person', True)
        vs, fs = carried_mesh(kind)
        me = obj.data
        if obj.get('key') != state['carried_key']:
            me.clear_geometry()
            me.from_pydata(vs, [], fs)
            me.update()
            obj['key'] = state['carried_key']
        shrink = Matrix.Identity(4)
        rocked = walk.CARRY[kind].get('rock')
        if kind == 'cradle' or rocked:
            hips = walk.carried_at(kind, 0.0)[walk.HIPS] @ walk.head[walk.HIPS]
            shrink = (Matrix.Translation(hips) @ Matrix.Diagonal((walk.CRADLE_SCALE,) * 3 + (1.0,))
                      @ Matrix.Translation(-hips))
        # A rocked baby swings in the arms (walk.carry_rock); the mesh is its pose at the loop's start, swung whole.
        rock = walk.carry_rock(kind, p.time % 1.0) if rocked else Matrix.Identity(4)
        obj.matrix_world = mw @ chest_now() @ rock @ shrink
    obj.hide_viewport = False

def clear_load():
    for name in (LOAD + 'Item', LOAD + 'Person', PET):
        obj = bpy.data.objects.get(name)
        if obj is not None:
            me = obj.data
            bpy.data.objects.remove(obj)
            bpy.data.meshes.remove(me)
    for name in (LOAD, PET):
        mat = bpy.data.materials.get(name)
        if mat is not None and mat.users == 0:
            bpy.data.materials.remove(mat)
    image = bpy.data.images.get(os.path.basename(PET_TEXTURE))
    if image is not None and image.users == 0:
        bpy.data.images.remove(image)
    state.pop('carried_key', None)

HOLSTER = 'MaruHolster.'   # + slot: a holstered weapon's slab
HOLSTER_GRIP = 'MaruHolsterGrip'   # the handle on the slot picked: at its place, x along the weapon, z out of its top
SIZES = [('small', 'Small', 'Pistols and one-handed melee weapons'), ('big', 'Big', 'Rifles, bows and two-handed melee weapons'),
         ('grenade', 'Grenade', 'Grenades and anything else thrown')]
TYPE_LABELS = {'handgun': 'Handgun', 'rifle': 'Rifle', 'bow': 'Bow', 'grenade': 'Grenade', 'melee_1h': '1h melee',
               'melee_2h': '2h melee'}
FIGURE_FOLDER = "M3AT: Maru's 3D Animated Toons/Models/chibi"   # the game's files, from the repository's root
WORN = 'MaruWorn.'   # + garment: a garment the figure has on (draw_worn), and their material
WORN_COLOUR = (0.45, 0.42, 0.36, 1.0)
HOLSTER_LOOK = {'small': 'pistol', 'big': 'rifle', 'grenade': 'Weapon_GrenadeFrag'}   # the weapon a slot shows to start with
# Degrees a place mirrored from another slot is spun about the weapon's own line (the handle's x), by slot size: mirrored
# alone, a handgun's butt points back for the hand on its own side, so it is turned to point forward for the far hand
# reaching across the belly.
HOLSTER_MIRROR_SPIN = {'small': 180.0}

def holster_place(p, slot, kind=None):
    """The slot's place, aim and roll as shown for the weapon kind (the slot's preview weapon if None): its melee_ ones for a
    melee weapon, where it has them."""
    return walk.holster_place(slot, melee_place(p, slot, kind))

def melee_place(p, slot, kind=None):
    """Whether the slot shows its melee_ place: it has one and the weapon kind (the slot's preview weapon if None) is melee."""
    return getattr(p, 'hs_%s_melee_on' % slot) and walk.is_melee(kind or getattr(p, 'hs_%s_weapon' % slot))

def holster_bone(slot):
    """The move of the bone a slot rides, from rest to the pose now on the rig (armature space)."""
    bone = walk.HOLSTERS[slot]['bone']
    return walk.arm.pose.bones[bone].matrix @ walk.rest[bone].inverted()

def figure():
    """The export as holster_push reads it, the files the game reads; read again when it is exported anew."""
    folder = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(WALK_PY))), FIGURE_FOLDER)
    stamp = os.path.getmtime(os.path.join(folder, 'skin.txt'))
    fig = registered.get('figure')
    if fig is None or registered.get('figure_stamp') != stamp:
        fig = registered['figure'] = holster_push.Figure(folder, getattr(walk, 'HOLSTER_PACKS', ()))
        registered['figure_stamp'], registered['figure_gear'] = stamp, None
    gear = repr(getattr(walk, 'GEAR', {}))
    if registered.get('figure_gear') != gear:
        fig.set_gear(getattr(walk, 'GEAR', {}))
        registered['figure_gear'] = gear
    return fig

def worn_prop(garment):
    return 'wn_' + re.sub(r'\W', '_', garment)

def garment_names():
    """The export's garments, none if there is no export yet."""
    try:
        return figure().names
    except OSError:
        return []

def body_mix():
    """The body's shape keys as they are set on the rig, the shape the game would make of them (ShapeOf, in hundredths)."""
    mix = {}
    for part in walk.body_parts():
        keys = part.data.shape_keys
        for key in (keys.key_blocks[1:] if keys else ()):
            if key.value > 0.0:
                mix[key.name] = round(key.value, 2)
    return tuple(sorted(mix.items()))

def gear_on(p, mount):
    """Whether this mount's gear is on the figure."""
    return p.gear_show and getattr(p, 'gr_%s_on' % mount) \
        and getattr(p, 'gr_%s_model' % mount) in [r for _, r in gear_models().get(mount, [])]

def worn_now(p, fig):
    """What the figure has on, as SkinnedFigure.Wearing is told it."""
    return holster_push.Worn(body_mix(), tuple(g for g in fig.names if p.worn_show and getattr(p, worn_prop(g), False)), gear_on(p, 'pack'), gear_belt=gear_on(p, 'belt'))

def on_pack_sides(fig, worn):
    """WalkFigures.OnPackSides: both a backpack and an equipment pack on, so the back's weapons go to their sides."""
    return worn.gear_pack and fig.worn_pack(worn) is not None

def slot_used(fig, worn, slot):
    """SkinnedFigure.HolsterFits, for what is worn."""
    h = walk.HOLSTERS[slot]
    return h.get('pack', on_pack_sides(fig, worn)) == on_pack_sides(fig, worn) and h.get('belt', worn.gear_belt) == worn.gear_belt

def to_rest(fig, at):
    """Places in the game's model space (rows) in the armature's rest space, as export_frames' to_game backwards."""
    d = (np.asarray(at) - fig.hips) / (model_scale() * walk.arm.matrix_world.to_scale().y)
    return np.array(walk.head[walk.HIPS]) + d[..., (0, 2, 1)] * (-1.0, -1.0, 1.0)

def weapon_depth(p, kind):
    """WalkFigures.HalfDepth / size: how far a weapon stands off its slab's middle either way out of its face, model units."""
    cells = holster_push.SLAB_HALF_DEPTH
    look = KIND_LOOK.get(kind, kind)
    rel = isorim_models().get(look)
    if rel is not None and p.weapon_models:
        try:
            img = sprite(getattr(p, 'wp_%s_image' % kind))[0]
            size, offset = tuple(getattr(p, 'wp_%s_size' % kind)), getattr(p, 'wp_%s_offset' % kind)
            depths = registered.setdefault('model_depths', {})
            key = (rel, img.name, size, round(offset, 3), walk.weapon_type(kind) == 'grenade')
            if key not in depths:
                tris = isorim_model(rel)[0]
                fitted = fit_model(tris, sprite_mask(img), size, offset)
                depths[key] = None
                if fitted is not None:
                    slab = at_ground_size(fitted[0], tris, size, isorim_scales().get(look, 1.0)) if key[4] else fitted[0]
                    depths[key] = float(np.abs(tris.reshape(-1, 3) @ slab[2, :3] + slab[2, 3]).max())
            if depths[key] is not None:
                cells = depths[key] * walk.weapon_scale(kind) / ground_ratio(kind, rel, img, size, offset)
        except Exception:   # a model the fit can't read: drawn as the sprite
            pass
    return cells / walk.GAME_SCALE

def slot_extras(p, fig, worn, drawn=None):
    """WalkFigures.DrawHolstered's slotExtra: how much further out than its Clearance the weapon in each slot goes, to lie on
    a pack and on those in the slots under it. On a draw clip (drawn: drawn_from's) a slot whose weapon has left for the
    hand is kept for it, and counts."""
    slots, depths = [], {}
    for slot, h in getattr(walk, 'HOLSTERS', {}).items():
        if not slot_used(fig, worn, slot):
            continue
        slots.append(fig.slot(slot, h))
        kind = getattr(p, 'hs_%s_weapon' % slot)
        if drawn is not None and slot == drawn[0]:
            kind = drawn[2]
            depths[slot] = -weapon_depth(p, kind) if drawn[1] > 0.0 and p.weapon else weapon_depth(p, kind)
        elif getattr(p, 'hs_%s_filled' % slot) and kind in getattr(walk, 'WEAPONS', {}):
            depths[slot] = weapon_depth(p, kind)
    return fig.extras(worn, slots, depths)

def holster_frame(p, slot, kind=None, extra=None):
    """As WalkFigures' Holster finds it, past what is worn (world space): where the slot holds its weapon, the way the
    weapon runs and its top. extra: HolsterAt's, by default from the slots filled (the pack alone for a slot not in use)."""
    mw = walk.arm.matrix_world
    at, aim, roll = holster_place(p, slot, kind)
    B = holster_bone(slot)
    fig = figure()
    worn = worn_now(p, fig)
    h = fig.slot(slot, walk.HOLSTERS[slot])
    if extra is None:
        extra = slot_extras(p, fig, worn).get(slot)
    if extra is None:
        extra = fig.holster_clear(worn, h, weapon_depth(p, kind or getattr(p, 'hs_%s_weapon' % slot)) + holster_push.STACK_GAP)
    rest = Vector(to_rest(fig, fig.holster_at(worn, h, fig.hips + holster_push.game_dir(at), extra)))
    q = mw.to_quaternion() @ B.to_quaternion()
    aim = Vector(aim).normalized()
    a = (q @ aim).normalized()
    top = q @ top_of(aim, roll, Vector((0, -1, 0)))
    top = (top - a * top.dot(a)).normalized()
    name = kind or getattr(p, 'hs_%s_weapon' % slot)
    if walk.is_melee(name):
        # A weapon with another offset than the place is set for, turned by the difference (SlotLine); on a slot with no
        # melee place, by its whole offset and end for end, so the blade runs along the aim's line hilt first.
        spin = math.radians(getattr(p, 'wp_%s_offset' % name) - (walk.MELEE_SLOT_OFFSET if melee_place(p, slot, kind) else -180.0))
        a, top = a * math.cos(spin) + top * math.sin(spin), top * math.cos(spin) - a * math.sin(spin)
    return mw @ (B @ rest), a, top

def rig_garments():
    """The meshes on the rig that aren't the body: what the Worn list stands in for while it is on."""
    body = set(o.name for o in walk.body_parts())
    return [o for o in bpy.data.objects if o.type == 'MESH' and o.name not in body
            and (o.parent == walk.arm or any(m.type == 'ARMATURE' and m.object == walk.arm for m in o.modifiers))]

def show_rig_garments():
    """Puts back the rig's garments that were hidden for the Worn list."""
    for name in state.pop('rig_hidden', ()):
        obj = bpy.data.objects.get(name)
        if obj is not None:
            obj.hide_set(False)

def worn_mesh(name, verts, tris):
    obj = bpy.data.objects.get(WORN + name)
    if obj is None:
        me = bpy.data.meshes.new(WORN + name)
        me.from_pydata(verts.tolist(), [], tris.tolist())
        mat = bpy.data.materials.get(WORN)
        if mat is None:
            mat = bpy.data.materials.new(WORN)
            mat.diffuse_color = WORN_COLOUR
        me.materials.append(mat)
        me.update()
        obj = bpy.data.objects.new(WORN + name, me)
        obj.hide_select = True
        obj.hide_render = True
        bpy.context.scene.collection.objects.link(obj)
    else:
        obj.data.vertices.foreach_set('co', verts.ravel())
        obj.data.update()
    return obj

def shield_over(p, fig, worn, drawn=None):
    """WalkFigures.ShieldOverWeapons: how far out the weapons holstered on the back stand, for a shield carried there."""
    slots, depths = [], {}
    for slot, h in getattr(walk, 'HOLSTERS', {}).items():
        if not slot_used(fig, worn, slot):
            continue
        slots.append(fig.slot(slot, h))
        kind = drawn[2] if drawn is not None and slot == drawn[0] else getattr(p, 'hs_%s_weapon' % slot)
        if (drawn is not None and slot == drawn[0]) or (getattr(p, 'hs_%s_filled' % slot) and kind in getattr(walk, 'WEAPONS', {})):
            depths[slot] = weapon_depth(p, kind)
    return fig.shield_over(worn, slots, slot_extras(p, fig, worn, drawn), depths)

# The clips a shield carried on the back is shown in the hand on: those the game plays only while it shows the weapon.
SHIELD_HELD = ('drafted', 'aim', 'strike', 'shield', 'cover', 'draw', 'holster', 'reload')

def draw_worn(p, held=(0.0, None), drawn=None):
    """The garments ticked, as the game has them on: skinned from the export's weights in the shape the body keys make, a
    backpack whole on its bone where SkinnedFigure.Packed puts it, a shield whole on the back over all else there or in
    the left fist, or part way between on its shield_draw clip (held: shield_taken's). In the rest of the rig's garments'
    place, which the list stands in for; garments the game's files don't have yet want an export, or Show worn off."""
    shown = set()
    if p.worn_show:
        for obj in rig_garments():
            if obj.visible_get():
                obj.hide_set(True)
                state.setdefault('rig_hidden', set()).add(obj.name)
    else:
        show_rig_garments()
    fig = figure()
    worn = worn_now(p, fig)
    if worn.mask:
        bpy.context.view_layer.update()   # the bones' new places
        pose = walk.arm.pose.bones
        moves = np.array([np.array(pose[n].matrix @ walk.rest[n].inverted()) for n in fig.bone_names])
        for g in worn.mask:
            piece = fig.piece(g)
            if fig.is_pack(g):
                rest = fig.packed(worn, g)
                if rest is None:
                    continue
                m = moves[fig.pack_rig(g)[0].bone]
                verts = to_rest(fig, rest) @ m[:3, :3].T + m[:3, 3]
            elif fig.is_shield(g):
                taken = held[0] if held[1] in (None, g) else 0.0
                grab, slide = walk.SHIELD_GRAB, walk.SHIELD_SLIDE
                b, rest = fig.shielded(worn, g, -np.inf if taken >= 1.0 else shield_over(p, fig, worn, drawn), taken >= 1.0)
                m = moves[b]
                verts = to_rest(fig, rest) @ m[:3, :3].T + m[:3, 3]
                if grab < taken < 1.0 and piece.held_grip is not None:
                    # As ShieldPlace: taken off the back onto the hand over the slide, turning in the hand to the guard's
                    # grip over the rest of the clip (the slide here a blend of places, the game's of turns).
                    b, rest = fig.shield_on_hand(worn, g, walk.smooth(min(1.0, (taken - grab) / (1.0 - grab))))
                    m = moves[b]
                    hand = to_rest(fig, rest) @ m[:3, :3].T + m[:3, 3]
                    u = walk.smooth(min(1.0, (taken - grab) / slide)) if slide > 0 else 1.0
                    verts = verts * (1.0 - u) + hand * u
            else:
                rest = to_rest(fig, fig.shaped(g, worn.mix))
                verts = np.zeros_like(rest)
                m = moves[piece.weight_bone]
                placed = (m[:, :3, :3] @ rest[piece.weight_vert][..., None])[..., 0] + m[:, :3, 3]
                np.add.at(verts, piece.weight_vert, piece.weight[:, None] * placed)
            obj = worn_mesh(g, verts, piece.tris)
            obj.matrix_world = walk.arm.matrix_world
            obj.display_type = 'WIRE' if p.worn_wire else 'TEXTURED'
            obj.hide_viewport = False
            shown.add(obj.name)
    for obj in bpy.data.objects:
        if obj.name.startswith(WORN) and obj.name not in shown:
            obj.hide_viewport = True

def draw_holsters(p, drawn=None):
    """Each slot's preview weapon on the body where the slot holds it past what is worn, with those on each other in the
    order they fill; none with Show holsters off. On a draw clip (drawn: drawn_from's) its slot shows the weapon it draws,
    until it leaves for the hand."""
    shown = set()
    if p.holsters:
        bpy.context.view_layer.update()   # the bones' new places
        fig = figure()
        worn = worn_now(p, fig)
        extras = slot_extras(p, fig, worn, drawn)
        for slot in getattr(walk, 'HOLSTERS', {}):
            if slot not in extras:
                continue
            kind = getattr(p, 'hs_%s_weapon' % slot)
            if drawn is not None and slot == drawn[0]:
                if drawn[1] > 0.0 and p.weapon:
                    continue
                kind = drawn[2]
            obj = slab_object(HOLSTER + slot)
            obj.hide_viewport = False
            grip, a, top = holster_frame(p, slot, kind, extras[slot])
            held = place_slab(p, obj, kind, grip, a, top, walk.HOLSTERS[slot].get('behind', 0.0))
            shown.add(obj.name)
            if held:
                shown.add(held)
    for obj in bpy.data.objects:
        if obj.name.startswith(HOLSTER) and obj.name not in shown:
            obj.hide_viewport = True

def place_holster_grip(p, write=True):
    """An arrows empty on the slot picked, x along its weapon and z out of its top (left where it is while playing, or
    when it was just moved)."""
    slot = p.holster_slot if getattr(walk, 'HOLSTERS', None) else None
    obj = bpy.data.objects.get(HOLSTER_GRIP)
    if not p.holsters or slot not in walk.HOLSTERS:
        if obj is not None:
            bpy.data.objects.remove(obj)
        state['holster'] = None
        return
    if obj is None:
        obj = bpy.data.objects.new(HOLSTER_GRIP, None)
        obj.empty_display_type = 'ARROWS'
        obj.rotation_mode = 'QUATERNION'
        obj.hide_render = True
        bpy.context.scene.collection.objects.link(obj)
        state['holster'] = None
    obj.empty_display_size = walk.LEG * walk.arm.matrix_world.to_scale().y * 0.3
    if state.get('holster') is not None and (p.playing or not write):
        return
    loc, a, top = holster_frame(p, slot)
    obj.location, obj.rotation_quaternion = loc, Matrix((a, top.cross(a), top)).transposed().to_quaternion()
    state['holster'] = stored(obj)

def holster_moved(p):
    """The holster handle moved (G) sets the slot's place, and turned (R) its aim and roll, both back to the rest pose
    through the bone it rides. Whether it was."""
    obj = bpy.data.objects.get(HOLSTER_GRIP)
    slot = p.holster_slot
    if obj is None or state.get('holster') is None or slot not in getattr(walk, 'HOLSTERS', {}):
        return False
    loc0, q0 = state['holster']
    loc, q = obj.location.copy(), obj.rotation_quaternion.normalized()
    mw = walk.arm.matrix_world
    shifted = (loc - loc0).length > 1e-5 * mw.to_scale().y
    turned = abs(q.dot(q0)) < TURNED
    if not (shifted or turned):
        return False
    state['holster'] = (loc, q)
    pre = 'hs_%s_' % slot
    if melee_place(p, slot):
        pre += 'melee_'
    B = holster_bone(slot)
    if shifted:
        # By how far it moved, as the handle is on the place past what is worn.
        back = B.inverted() @ mw.inverted()
        p[pre + 'at'] = tuple(Vector(getattr(p, pre + 'at')) + (back @ loc - back @ loc0) * model_scale() * mw.to_scale().y)
    if turned:
        back = (mw.to_quaternion() @ B.to_quaternion()).inverted()
        m = q.to_matrix()
        rest, top = (back @ m.col[0]).normalized(), (back @ m.col[2]).normalized()
        base = top_of(rest, 0.0, Vector((0, -1, 0)))
        p[pre + 'aim'] = tuple(rest)
        p[pre + 'roll'] = -math.degrees(math.atan2(rest.dot(base.cross(top)), base.dot(top)))
    return True

def across(v):
    """A way or place in the rig's rest space (x left), mirrored across the figure's middle."""
    return Vector((-v[0], v[1], v[2]))

def mirrored_place(at, aim, roll, spin):
    """A slot's at, aim and roll put on the other side of the figure, then the weapon spun spin degrees about its own
    line. The muzzle and the top are mirrored as ways and the roll found again from them, as
    holster_moved finds it, since a roll's sign alone doesn't carry over a line that has moved."""
    front = Vector((0, -1, 0))
    aim = Vector(aim).normalized()
    top = top_of(aim, roll, front)
    aim = across(aim).normalized()
    top = Quaternion(aim, math.radians(spin)) @ across(top)
    base = top_of(aim, 0.0, front)
    return tuple(across(at)), tuple(aim), -math.degrees(math.atan2(aim.dot(base.cross(top)), base.dot(top)))

def slot_picked(self, context):
    """A slot used only with both packs on or a belt on, or only without, picked: what is worn is changed so that it is in use."""
    h = getattr(walk, 'HOLSTERS', {}).get(self.holster_slot, {})
    fig = figure()
    if h.get('pack') is True:
        self['gear_show'], self['gr_pack_on'], self['worn_show'] = True, True, True
        if fig.worn_pack(worn_now(self, fig)) is None:
            first = next((g for g in fig.names if fig.is_pack(g)), None)
            if first is not None:
                self[worn_prop(first)] = True
    elif h.get('pack') is False and on_pack_sides(fig, worn_now(self, fig)):
        self['gr_pack_on'] = False
    if h.get('belt') is True:
        self['gear_show'], self['gr_belt_on'] = True, True
    elif h.get('belt') is False:
        self['gr_belt_on'] = False
    repose()

def holster_slot_items(self, context):
    items = [(s, s, 'Slot %s (%s)' % (s, h['size'])) for s, h in getattr(walk, 'HOLSTERS', {}).items()]
    holster_cache['slots'] = items or [('', '(none)', 'walk.py has no HOLSTERS')]
    return holster_cache['slots']

holster_cache = {}   # Blender's dynamic menus need their item strings kept alive

# Worn gear (walk.GEAR: IsoRim's packs, belts and hip tools, drawn by SkinnedFigure.GearAt): each mount shows one of IsoRim's
# models for it on the body, its numbers as sliders and a handle on the slot's place (G moves it), without the game's push
# past garments.
GEAR_OBJ = 'MaruGear.'   # + mount: that mount's model
GEAR_GRIP = 'MaruGearGrip'   # the handle on the mount picked: at its place
GAME_TO_RIG = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))   # the game's model space to the rig's (x left, y back, z up)

def gear_models():
    """mount -> [(apparel defName, model file relative to IsoRim's folder)] from IsoRim's WornGearDefs. Read once."""
    if 'gear_models' in registered:
        return registered['gear_models']
    out = {}
    for path in sorted(glob.glob(os.path.join(ISORIM_ROOT, 'Defs', '**', 'WornGear*.xml'), recursive=True)):
        try:
            root = ET.parse(path).getroot()
        except (ET.ParseError, OSError):
            continue
        for el in root.iter('IsoRim.WornGearDef'):
            apparel, model, mount = [(el.findtext(t) or '').strip() for t in ('apparel', 'model', 'mount')]
            if apparel and model and os.path.exists(os.path.join(ISORIM_ROOT, model)):
                out.setdefault(mount or 'pack', []).append((apparel, model))
    registered['gear_models'] = out
    return out

def gear_bone(mount):
    """The move of the bone a mount rides, from rest to the pose now on the rig (armature space)."""
    bone = walk.GEAR[mount]['bone']
    return walk.arm.pose.bones[bone].matrix @ walk.rest[bone].inverted()

def gear_matrix(p, mount):
    """As SkinnedFigure.GearAt: the model's own (game) space to the world, as posed, past what is worn."""
    mw = walk.arm.matrix_world
    fig = figure()
    f = model_scale() * mw.to_scale().y
    to_rig = Matrix.Translation(walk.head[walk.HIPS]) @ Matrix.Scale(1.0 / f, 4) @ GAME_TO_RIG @ Matrix.Translation(-Vector(fig.hips))
    return mw @ gear_bone(mount) @ to_rig @ Matrix(fig.gear_at(worn_now(p, fig), mount)[0].tolist())

def gear_object(mount, rel):
    """The mount's preview model: IsoRim's file rel as a mesh in its own space (replaced when another is picked)."""
    name = GEAR_OBJ + mount
    obj = bpy.data.objects.get(name)
    if obj is not None and obj.get('model') != rel:
        me = obj.data
        bpy.data.objects.remove(obj)
        bpy.data.meshes.remove(me)
        obj = None
    if obj is None:
        tris, mats, colours = isorim_model(rel)
        me = bpy.data.meshes.new(name)
        n = len(tris)
        me.from_pydata([tuple(c) for c in tris.reshape(-1, 3)], [], [(3 * t, 3 * t + 1, 3 * t + 2) for t in range(n)])
        names = sorted(set(mats))
        for m in names:
            me.materials.append(model_material(m, colours.get(m, (0.5, 0.5, 0.5))))
        for t, m in enumerate(mats):
            me.polygons[t].material_index = names.index(m)
        me.update()
        obj = bpy.data.objects.new(name, me)
        obj['model'] = rel
        obj.hide_select = True
        obj.hide_render = True
        bpy.context.scene.collection.objects.link(obj)
    return obj

def blend_frames(A, B, u):
    """A matrix (turn, uniform size and place) u of the way from A to B."""
    la, ra, sa = A.decompose()
    lb, rb, sb = B.decompose()
    return Matrix.LocRotScale(la.lerp(lb, u), ra.slerp(rb, u), sa.lerp(sb, u))

def draw_gear(p, held=0.0):
    """Each mount's preview model on the body where its slot holds it; none with Show worn gear off. The hip gear held
    of the way from its slot to the left hand (hip_held), where walk.hip_rest holds it."""
    shown = set()
    if p.gear_show and getattr(walk, 'GEAR', None):
        bpy.context.view_layer.update()   # the bones' new places
        for mount in walk.GEAR:
            rel = getattr(p, 'gr_%s_model' % mount)
            if not gear_on(p, mount):
                continue
            obj = gear_object(mount, rel)
            obj.matrix_world = gear_matrix(p, mount)
            if mount == 'hip' and held > 0.0 and hasattr(walk, 'hip_rest'):
                hand = walk.arm.pose.bones['hand.L'].matrix @ walk.rest['hand.L'].inverted()
                obj.matrix_world = blend_frames(obj.matrix_world, walk.arm.matrix_world @ hand @ walk.hip_rest() @ GAME_TO_RIG, held)
            obj.hide_viewport = False
            shown.add(obj.name)
    for obj in bpy.data.objects:
        if obj.name.startswith(GEAR_OBJ) and obj.name not in shown:
            obj.hide_viewport = True

def place_gear_grip(p, write=True):
    """An arrows empty on the slot of the mount picked (left where it is while playing, or when it was just moved)."""
    mount = p.gear_mount if getattr(walk, 'GEAR', None) else None
    obj = bpy.data.objects.get(GEAR_GRIP)
    if not p.gear_show or mount not in getattr(walk, 'GEAR', {}):
        if obj is not None:
            bpy.data.objects.remove(obj)
        state['gear'] = None
        return
    if obj is None:
        obj = bpy.data.objects.new(GEAR_GRIP, None)
        obj.empty_display_type = 'ARROWS'
        obj.rotation_mode = 'QUATERNION'
        obj.hide_render = True
        bpy.context.scene.collection.objects.link(obj)
        state['gear'] = None
    obj.empty_display_size = walk.LEG * walk.arm.matrix_world.to_scale().y * 0.3
    if state.get('gear') is not None and (p.playing or not write):
        return
    mw = walk.arm.matrix_world
    fig = figure()
    obj.location = mw @ (gear_bone(mount) @ Vector(to_rest(fig, fig.gear_at(worn_now(p, fig), mount)[1])))
    turn = Euler([math.radians(v) for v in walk.GEAR[mount].get('turn', (0.0, 0.0, 0.0))], 'XYZ').to_quaternion()
    obj.rotation_quaternion = mw.to_quaternion() @ gear_bone(mount).to_quaternion() @ turn
    state['gear'] = stored(obj)

def gear_moved(p):
    """The gear handle moved (G) sets the mount's place, and turned (R) the model's turn, both back to the rest pose
    through the bone it rides. Whether it was."""
    obj = bpy.data.objects.get(GEAR_GRIP)
    mount = p.gear_mount
    if obj is None or state.get('gear') is None or mount not in getattr(walk, 'GEAR', {}):
        return False
    loc0, q0 = state['gear']
    loc, q = obj.location.copy(), obj.rotation_quaternion.normalized()
    mw = walk.arm.matrix_world
    shifted = (loc - loc0).length > 1e-5 * mw.to_scale().y
    turned = abs(q.dot(q0)) < TURNED
    if not (shifted or turned):
        return False
    state['gear'] = (loc, q)
    pre = 'gr_%s_' % mount
    if shifted:
        # By how far it moved, as the handle is on the place past what is worn.
        back = gear_bone(mount).inverted() @ mw.inverted()
        p[pre + 'at'] = tuple(Vector(getattr(p, pre + 'at')) + (back @ loc - back @ loc0) * model_scale() * mw.to_scale().y)
    if turned:
        # The numbers nearest the last ones, so a slider doesn't jump to the same turn written another way.
        was = Euler([math.radians(v) for v in getattr(p, pre + 'turn')], 'XYZ')
        rest = (mw.to_quaternion() @ gear_bone(mount).to_quaternion()).inverted() @ q
        p[pre + 'turn'] = tuple(math.degrees(v) for v in rest.to_euler('XYZ', was))
    return True

def gear_mount_items(self, context):
    items = [(m, m, 'The %s mount' % m) for m in getattr(walk, 'GEAR', {})]
    holster_cache['mounts'] = items or [('', '(none)', 'walk.py has no GEAR')]
    return holster_cache['mounts']

def restore_slab(*_):
    repose()

def held_sides(p, what, kind):
    """The stance table and the sides whose hands get handles: the held ones of the stance shown on its own (laid over
    a gait, the arms are the gait's blend, not the places set); an aim clip's two (table 'AIMED')."""
    if what == 'aim' and not p.over:
        return 'AIMED', ('L', 'R') if walk.aim_last.get('name') == kind.partition('/')[0] else ()
    if what not in ('drafted', 'carry') or p.over:
        return None, ()
    table = 'DRAFTED' if what == 'drafted' else 'CARRY'
    info = registered['stances'].get((table, kind))
    if info is None or (what == 'drafted' and drafted_pose_name(p, kind) != kind):
        return table, ()   # (a weapon's shield stance is worked out from its kind's: move the kind's hands)
    sides = [s for s in info['sides'] if not info['toggle'] or getattr(p, sp(table, kind, s + '_on'))]
    if pet_hand(table, kind, 'R') and 'R' not in sides:
        sides.append('R')   # the hand on the pet's back, placed by walk.cuddle_hand (STROKE_*), not the entry
    return table, sides

def pet_hand(table, kind, side):
    """Whether this is a cuddle clip's hand on the pet (walk.cuddle_hand): its handle sets walk.py's STROKE_REST,
    STROKE_FINGERS and STROKE_PALM (and on the stroke, shifts STROKE_FROM and STROKE_TO), not the CARRY entry."""
    return (table == 'CARRY' and side == 'R' and kind in walk.CARRY and bool(walk.CARRY[kind].get('pet'))
            and hasattr(walk, 'pet_last') and hasattr(walk, 'STROKE_PALM'))

def pet_hand_turn(f, n):
    """The right hand's turn from rest (armature space) that lays its fingers along f, its palm facing n (hand_on's)."""
    d0, n0 = walk.hand_rest_axes('R', -1)
    return (Matrix((f, n, f.cross(n))).transposed() @ Matrix((d0, n0, d0.cross(n0)))).to_quaternion()

def turn_set(p, table, kind, side):
    """Whether a stance holds this hand at a turn of its own (an aim clip's hands are turned by the aim)."""
    if pet_hand(table, kind, side):
        return True
    return table != 'AIMED' and getattr(p, sp(table, kind, side + '_turn_on'))

def aim_handle_place(side):
    """Where an aim clip's handle for this hand goes (armature space): the holding fist, the other hand's fist round the
    weapon, or a bow's string hand at its wrist (walk.aim_last)."""
    last = walk.aim_last
    if side == last['side']:
        return last['fist']
    return last['string'] if 'string' in last else last['hold']

def handle_frame(p, table, kind, side, X):
    """Where a hand's handle goes (world): on the wrist place set, turned as the hand bone is, by the turn set or as
    the arm left it; on an aim clip, on its fist (aim_handle_place), turned as the hand is."""
    mw, chest, hn = walk.arm.matrix_world, state['chest'], 'hand.' + side
    if pet_hand(table, kind, side) and walk.pet_last.get('kind') == kind:
        last = walk.pet_last
        q = pet_hand_turn(last['fingers'], last['facing'])
        return mw @ last['wrist'], (mw.to_quaternion() @ q @ walk.rest[hn].to_quaternion()).normalized()
    if table == 'AIMED':
        q = X[hn].to_quaternion()
        return mw @ aim_handle_place(side), (mw.to_quaternion() @ q @ walk.rest[hn].to_quaternion()).normalized()
    at = chest @ (walk.head[walk.HIPS] + Vector(getattr(p, sp(table, kind, side))) * walk.LEG)
    if getattr(p, sp(table, kind, side + '_turn_on')):
        q = chest.to_quaternion() @ Euler(tuple(getattr(p, sp(table, kind, side + '_turn')))).to_quaternion()
    else:
        q = X[hn].to_quaternion()
    return mw @ at, (mw.to_quaternion() @ q @ walk.rest[hn].to_quaternion()).normalized()

def place_handles(p, what, kind, X, write=True):
    """An arrows empty on each held hand of the stance shown, put where the stance sets the hand (left where it is
    while playing, and not moved when it was just moved, so a drag isn't fought). Its arrows are the hand bone's own
    axes."""
    table, sides = held_sides(p, what, kind) if X is not None else (None, ())
    for side in ('L', 'R'):
        obj = bpy.data.objects.get(HANDLE + side)
        if side not in sides:
            if obj is not None:
                bpy.data.objects.remove(obj)
            state['handles'].pop(side, None)
            continue
        if obj is None:
            obj = bpy.data.objects.new(HANDLE + side, None)
            obj.empty_display_type = 'ARROWS'
            obj.rotation_mode = 'QUATERNION'
            obj.hide_render = True
            bpy.context.scene.collection.objects.link(obj)
            state['handles'].pop(side, None)
        obj.empty_display_size = walk.LEG * walk.arm.matrix_world.to_scale().y * 0.25
        obj.show_in_front = True   # (seen through the body, and a cuddled pet the right hand is laid on)
        if side in state['handles'] and p.playing:
            continue
        loc, q = handle_frame(p, table, kind, side, X)
        # Just moved: left where it was put. On an aim clip only the one dragged is: the other hand goes with the weapon.
        if side in state['handles'] and not write and (table != 'AIMED' or side in state.get('dragged', ())):
            if turn_set(p, table, kind, side):
                continue
            loc = state['handles'][side][0]   # only moved: its arrows turned to follow the hand the arm turned
        obj.location, obj.rotation_quaternion = loc, q
        state['handles'][side] = stored(obj)

def place_grip(p, what, kind, write=True):
    """An arrows empty where a weapon on the 'hand' line is held, x along it and z out of its top, for the stance
    shown on its own (left where it is while playing, or when it was just moved)."""
    kind = weapon_name(p, what, kind)
    w = getattr(walk, 'WEAPONS', {}).get(kind) if what == 'drafted' else None
    obj = bpy.data.objects.get(GRIP)
    if w is None or w['line'] != 'hand' or not p.weapon or p.over or state['chest'] is None:
        if obj is not None:
            bpy.data.objects.remove(obj)
        state['grip'] = None
        return
    if obj is None:
        obj = bpy.data.objects.new(GRIP, None)
        obj.empty_display_type = 'ARROWS'
        obj.rotation_mode = 'QUATERNION'
        obj.hide_render = True
        bpy.context.scene.collection.objects.link(obj)
        state['grip'] = None
    obj.empty_display_size = walk.LEG * walk.arm.matrix_world.to_scale().y * 0.3
    if state['grip'] is not None and (p.playing or not write):
        return
    hand, a, top = weapon_frame(w)
    loc = hand + weapon_shift(w['hand'])
    q = Matrix((a, top.cross(a), top)).transposed().to_quaternion()
    obj.location, obj.rotation_quaternion = loc, q
    state['grip'] = stored(obj)

def stored(obj):
    """A handle's place and turn as Blender keeps them (single precision), so the next look doesn't take the rounding
    for a move and re-pose again, and again."""
    return obj.location.copy(), obj.rotation_quaternion.normalized()

def clear_handles():
    for name in (HANDLE + 'L', HANDLE + 'R', GRIP, HOLSTER_GRIP, GEAR_GRIP):
        obj = bpy.data.objects.get(name)
        if obj is not None:
            bpy.data.objects.remove(obj)
    state['handles'].clear()
    state['grip'] = None
    state['holster'] = None
    state['gear'] = None

def grip_moved(p):
    """The grip handle moved (G) sets the weapon's place in the hand ('at'), and turned (R) its aim and roll; whether
    it was."""
    obj = bpy.data.objects.get(GRIP)
    what, kind = clip_of(p)
    kind = weapon_name(p, what, kind)
    w = getattr(walk, 'WEAPONS', {}).get(kind)
    if obj is None or state['grip'] is None or w is None or w['line'] != 'hand':
        return False
    loc0, q0 = state['grip']
    loc, q = obj.location.copy(), obj.rotation_quaternion.normalized()
    shifted = (loc - loc0).length > 1e-5 * walk.arm.matrix_world.to_scale().y
    turned = abs(q.dot(q0)) < TURNED
    if not (shifted or turned):
        return False
    state['grip'] = (loc, q)
    pre = 'wp_%s_' % kind
    if shifted:
        base, _, _ = weapon_frame(dict(w, at=(0.0, 0.0, 0.0)))
        back = (walk.arm.matrix_world.to_quaternion() @ hand_turn(w['hand'])).inverted()
        p[pre + 'at'] = tuple(back @ (loc - weapon_shift(w['hand']) - base) * model_scale())
    if turned:
        m = q.to_matrix()
        aim, roll = on_hand(w['hand'], m.col[0], m.col[2])
        p[pre + 'aim'], p[pre + 'roll'], p[pre + 'drop'] = aim, roll, 0.0
    return True

def aimed_own(name):
    """The AIMED entry a handle or slider on an aim clip writes into: the weapon's own (made if it has none: it then
    takes its kind's for the rest), or the kind's on a kind's clip."""
    own = walk.AIMED.get(name)
    if not isinstance(own, dict):
        own = walk.AIMED[name] = {}
    return own

def aim_moved(kind, side, loc, q, shifted, turned):
    """An aim clip's handle moved (G) or turned (R), back into AIMED for the weapon shown, from the frame the pose was
    made in (walk.aim_last), so it can be set at any pitch: the holding fist sets 'fist' (where the weapon is: turning it
    does nothing, the aim turns it); the other hand's 'support' (along the weapon) and 'under' (below it); a bow's string
    hand the place of the draw, the nock or the loose, as the stage shown. Either turned sets its 'other_turn', the hand's
    turn in the weapon's frame, so it keeps it at every pitch. Whether anything was set."""
    name, _, stage = kind.partition('/')
    last = walk.aim_last
    if last.get('name') != name or last.get('stage') != stage:
        return False
    mw = walk.arm.matrix_world
    at = mw.inverted() @ loc
    own = aimed_own(name)
    leg = walk.LEG
    if side == last['side']:
        if shifted:
            own['fist'] = tuple(last['place'].inverted() @ (at + last['kicked']))
        return shifted
    d, up = last['d'], last['up']
    if shifted and 'string' in last:
        o = at - last['fist']
        key = {'fire': 'loose', 'nock': 'nock'}.get(stage, 'draw')
        own[key] = (o.dot(last['right']) / leg, -o.dot(d) / leg, o.dot(up) / leg)
    elif shifted:
        o = at - last['grip']
        own['support'], own['under'] = o.dot(d) / leg, -o.dot(up) / leg
    if turned:
        hn = 'hand.' + side
        R = (mw.to_quaternion().inverted() @ q @ walk.rest[hn].to_quaternion().inverted()).to_matrix()
        local = walk.aim_frame(d, up, last['right']).transposed() @ R
        # Kept near the turn it had, so the angles stay continuous (as the drafted hands' turns).
        before = walk.aimed_entry(name).get('other_turn')
        before = Euler([math.radians(v) for v in before]) if before is not None else None
        e = local.to_euler('XYZ', before) if before is not None else local.to_euler('XYZ')
        own['other_turn'] = tuple(math.degrees(v) for v in e)
    return shifted or turned

# An aim clip's sliders: AIMED keys (upper case: its 'body' numbers), their names and what they do.
AIM_NUMBERS = (('elbow', 'Elbow lift', "The holding elbow swung out and up (degrees), the hand kept"),
               ('other_elbow', 'Other elbow lift', "The other hand's elbow swung out and up (degrees)"),
               ('hips', 'Hips turn', "The whole figure turned, feet and all (degrees, + to the left); standing still only"),
               ('twist', 'Trunk twist', "The trunk turned on from the hips (degrees, + to the left)"),
               ('lean', 'Lean', "The trunk bent forward (degrees)"),
               ('sight', 'Head bow', "The head bowed onto the sights (degrees)"),
               ('HEAD_TILT', 'Head tilt', "The head tipped over sideways (degrees, + to the left), to clear a weapon at the shoulder"),
               ('DROP', 'Knees bent', "Hips lowered (leg lengths); standing still only"),
               ('NARROW', 'Feet drawn in', "Share of the way the feet are drawn in under the hips (less: wider); standing still only"),
               ('STAGGER', 'Left foot forward', "Leg lengths the left foot is set forward of the right; standing still only"))

def aim_number(entry, key):
    """An aim clip's slider value: the AIMED entry's own (a body number in its 'body'), else what aim_at uses without."""
    if key.isupper():
        default = {'DROP': walk.DRAFT_DROP, 'NARROW': walk.DRAFT_NARROW}.get(key, getattr(walk, key, 0.0))
        return entry.get('body', {}).get(key, default)
    return entry.get(key) or 0.0

def aim_number_changed(key):
    """An aim clip's slider into the weapon's (or kind's) AIMED entry."""
    def changed(self, context):
        what, kind = clip_of(self)
        if what != 'aim':
            return
        own = aimed_own(kind.partition('/')[0])
        v = getattr(self, 'aim_' + key)
        if key.isupper():
            own['body'] = dict(own.get('body', {}), **{key: v})
        else:
            own[key] = v
        repose()
    return changed

def handles_moved(*_):
    """After any change in the scene: a hand's handle moved (G) sets its wrist place, and one turned (R) its hand's
    turn; the grip handle sets the weapon's place and turn in the hand."""
    if state['busy'] or registered.get('props') is None:
        return
    if body_mix() != state.get('mix'):   # the body's keys were moved: what is on it goes with them
        state['busy'] = True
        try:
            repose()
        finally:
            state['busy'] = False
        return
    if not state.get('holster') and not state.get('gear') and (not (state['handles'] or state['grip']) or state['chest'] is None):
        return
    state['busy'] = True   # the re-pose updates the scene itself (draw_weapon): don't look again from inside it
    try:
        moved_handles()
    finally:
        state['busy'] = False

def moved_handles():
    p = props()
    if gear_moved(p):
        state['quiet'] = {'gear'}   # leave the gear handle where it was put
        try:
            repose()
        finally:
            state['quiet'] = set()
        return
    if holster_moved(p):
        state['quiet'] = {'holster'}   # leave the holster handle where it was put
        try:
            repose()
        finally:
            state['quiet'] = set()
        return
    if state['chest'] is None:
        return
    if grip_moved(p):
        state['quiet'] = {'grip'}   # leave the grip handle where it was put
        try:
            repose()
        finally:
            state['quiet'] = set()
        return
    what, kind = clip_of(p)
    table, sides = held_sides(p, what, kind)
    mw = walk.arm.matrix_world
    near = 1e-5 * mw.to_scale().y
    moved = False
    for side, (loc0, q0) in list(state['handles'].items()):
        obj = bpy.data.objects.get(HANDLE + side)
        if obj is None or side not in sides:
            continue
        loc, q = obj.location.copy(), obj.rotation_quaternion.normalized()
        shifted = (loc - loc0).length > near
        turned = abs(q.dot(q0)) < TURNED
        if not (shifted or turned):
            continue
        state['handles'][side] = (loc, q)
        if table == 'AIMED':
            if aim_moved(kind, side, loc, q, shifted, turned):
                state['dragged'] = state.get('dragged', set()) | {side}
                moved = True
            continue
        if pet_hand(table, kind, side) and walk.pet_last.get('kind') == kind:
            pet_moved(p, loc, q, shifted, turned)
            moved = True
            continue
        chest = state['chest']
        if shifted:
            p[sp(table, kind, side)] = tuple((chest.inverted() @ (mw.inverted() @ loc) - walk.head[walk.HIPS]) / walk.LEG)
        if turned:
            hand = mw.to_quaternion().inverted() @ q @ walk.rest['hand.' + side].to_quaternion().inverted()
            # The turn as the slider had it, so the Euler angles stay continuous: without it, the XYZ decomposition
            # flips X and Z where the middle (Y) angle passes 90 degrees, and the slider snaps
            before = Euler(tuple(getattr(p, sp(table, kind, side + '_turn'))))
            p[sp(table, kind, side + '_turn')] = tuple((chest.to_quaternion().inverted() @ hand).to_euler('XYZ', before))
            p[sp(table, kind, side + '_turn_on')] = True
        moved = True
    if moved:
        state['quiet'] = {'hands'}   # a hand's handle was just moved: leave the hands' handles where they are
        try:
            repose()
        finally:
            state['quiet'] = set()
            state['dragged'] = set()

def pet_moved(p, loc, q, shifted, turned):
    """The cuddle's right-hand handle moved (world loc, q, on the wrist): turned, its fingers' way and palm's
    facing go to STROKE_FINGERS and STROKE_PALM (armature space); either way, the palm's place over the pet (walk.pet_on) to STROKE_REST
    (along, across, up over PET_BACK), on the stroke the along shifting STROKE_FROM and STROKE_TO by as much, so the
    stroke's path moves with the hand."""
    last = walk.pet_last
    mw = walk.arm.matrix_world
    f, n = last['fingers'], last['facing']
    if turned:
        X = mw.to_quaternion().inverted() @ q @ walk.rest['hand.R'].to_quaternion().inverted()
        d0, n0 = walk.hand_rest_axes('R', -1)
        f, n = (X @ d0).normalized(), (X @ n0).normalized()
        p['tup_STROKE_FINGERS'] = tuple(f)
        p['tup_STROKE_PALM'] = tuple(n)
    if shifted or turned:   # (turned about the wrist, the handle's place: the palm goes round it)
        hn = 'hand.R'
        palm = (mw.inverted() @ loc) + n * walk.HAND_HALF * walk.LEG + f * (walk.tail[hn] - walk.head[hn]).length * 0.5
        off = (palm - last['pet']) / walk.LEG
        rest = list(getattr(p, 'tup_STROKE_REST'))
        rest[1] = off.y
        if len(rest) > 2:
            rest[2] = off.z - walk.PET_BACK - last['high']
        if walk.CARRY[last['kind']].get('stroke'):
            by = off.x - last['along']
            p['walk_STROKE_FROM'] = getattr(p, 'walk_STROKE_FROM') + by
            p['walk_STROKE_TO'] = getattr(p, 'walk_STROKE_TO') + by
        else:
            rest[0] = off.x
        p['tup_STROKE_REST'] = tuple(rest)

def game_clip(what, kind):
    """The game's name for the clip shown (walk.VARIANTS is keyed by it), or None for one without variants."""
    if what in ('bend', 'land', 'jump', 'getup', 'eat_sit'):
        return what
    if what == 'flinch':
        return 'flinch_' + kind
    if what in ('strike', 'fall'):
        return kind
    if what == 'work':
        return 'work_' + kind
    if what == 'sow':
        return 'work_sow'
    if what == 'stomp':
        return 'work_stomp'
    return None

def clip_variants(what, kind):
    """The variants of the clip shown (walk.VARIANTS entries); none on an older walk.py."""
    clip = game_clip(what, kind)
    return walk.variants(clip) if clip is not None and hasattr(walk, 'variants') else []

def variant_numbers(p, what, kind):
    """The numbers of the variant picked (1 the clip itself, 2 its first variant, as the export names them ~2)."""
    numbers = clip_variants(what, kind)
    return numbers[p.variant - 2] if 2 <= p.variant <= len(numbers) + 1 else {}

def varied(numbers):
    return walk.varied(numbers) if hasattr(walk, 'varied') else contextlib.nullcontext()

def repose(_self=None, _context=None):
    """Copies the sliders into walk and poses the rig with the picked clip at the chosen gait, phase and time."""
    p = props()
    push_settings(p)
    what, kind = clip_of(p)
    if what == 'aim':
        entry = walk.aimed_entry(kind.partition('/')[0]) or {}
        for key, _, _ in AIM_NUMBERS:
            p['aim_' + key] = aim_number(entry, key)   # quietly
    # A variant (walk.VARIANTS) is posed with its own numbers in place of the sliders' for those it changes.
    with varied(variant_numbers(p, what, kind)):
        try:
            if walk.arm.animation_data:
                walk.arm.animation_data.action = None   # a baked action would put its own pose back
            if what == 'gait':
                walk.set_gait(p.gait)
                X, clamp = walk.pose_at(p.phase % 1.0)
            elif what == 'still':
                walk.set_gait(0.0)
                X, clamp = walk.stand_pose(), 0.0
            elif what in CYCLES:
                walk.set_gait(0.0)
                X, clamp = CYCLES[what](p.phase % 1.0, kind)
            elif what == 'cover' and kind:
                X, clamp = walk.stand_at(p.time % 1.0, kind)
                if hasattr(walk, 'crouch_legs'):
                    clamp = max(clamp, walk.crouch_legs(X))   # the game's crouch: the stance's own legs let down
                else:
                    Xs = X
                    Xc, cover_clamp = walk.cover_at(p.time % 1.0)
                    entry = stance_entry('DRAFTED', kind)
                    apply_over(Xc, Xs, entry.get('hands', entry))
                    X, clamp = None, max(clamp, cover_clamp)
            elif what in OVERS and p.over:
                Xs, clamp = loop_pose(what, kind, p.time % 1.0)
                walk.set_gait(p.gait)
                Xg, gait_clamp = walk.pose_at(p.phase % 1.0)
                if what == 'hurt':
                    apply_over(Xg, Xs, walk.hurt_hands(kind), walk.HURT_UPPER)
                elif what == 'cower':
                    apply_over(Xg, Xs, 'LR', walk.CRY_UPPER)
                elif what == 'lead':
                    apply_over(Xg, Xs, 'R', walk.LEAD_UPPER)
                elif what == 'shield':
                    apply_over(Xg, Xs, 'L', walk.SHIELD_DRAW_UPPER if kind else walk.SHIELD_UPPER)
                elif what == 'rub':
                    apply_over(Xg, Xs, 'LR', walk.RUB_UPPER)
                elif what == 'talk':
                    apply_over(Xg, Xs, 'LR', walk.TALK_UPPER)
                elif what == 'teach':
                    apply_over(Xg, Xs, 'LR', walk.TEACH_UPPER)
                elif what == 'wave':
                    apply_over(Xg, Xs, 'R', walk.WAVE_UPPER)
                elif what == 'sad':
                    apply_over(Xg, Xs, 'LR', walk.SAD_UPPER)   # (the game takes only SAD_ARMS of the arms)
                elif what == 'burning':
                    apply_over(Xg, Xs, 'LR', walk.BURN_UPPER)
                elif what == 'reload':
                    apply_over(Xg, Xs, 'LR', walk.RELOAD_UPPER)
                elif what == 'cast':
                    apply_over(Xg, Xs, 'L', walk.CAST_UPPER)
                elif what == 'gear':
                    apply_over(Xg, Xs, 'L', walk.HIP_UPPER if kind == 'hold' else walk.HIP_AIM_UPPER)
                elif what == 'flinch':
                    X0, _ = walk.flinch_at(0.0, kind)
                    Xg = nudged(Xg, Xs, X0, walk.FLINCH_UPPER)
                    walk.apply_pose(Xg)
                elif what == 'reel':
                    apply_over(Xg, Xs, 'LR', walk.REEL_UPPER)
                elif what == 'toss':
                    apply_over(Xg, Xs, 'LR', walk.TOSS_UPPER)
                elif what == 'snowball':
                    apply_over(Xg, Xs, 'LR', walk.SNOW_UPPER)
                elif what == 'kiss':
                    apply_over(Xg, Xs, 'LR', walk.KISS_UPPER)
                elif what == 'daze':
                    apply_over(Xg, Xs, 'LR', walk.DAZE_UPPER)
                elif what == 'aim':
                    apply_over(Xg, Xs, 'LR', walk.AIM_UPPER)
                elif what == 'strike':
                    apply_over(Xg, Xs, 'LR', walk.STRIKE_UPPER)
                elif what in ('draw', 'holster'):
                    apply_over(Xg, Xs, walk.draw_keys(kind)['side'], walk.DRAW_UPPER)
                else:
                    entry = stance_entry('DRAFTED' if what == 'drafted' else 'CARRY', kind)
                    if entry.get('shield'):
                        entry = dict(entry, L=True)   # a shield stance takes the left arm too (the guard)
                    apply_over(Xg, Xs, entry.get('hands', entry))
                X, clamp = None, max(clamp, gait_clamp)
            else:
                X, clamp = loop_pose(what, kind, p.time % 1.0)
            if X is not None:
                walk.apply_pose(X)
            state['clamp'], state['error'] = clamp, ''
            state['chest'] = X[walk.CHEST_BONE].copy() if X is not None else None
            place_handles(p, what, kind, X, write='hands' not in state['quiet'])
            draw_weapon(p, weapon_name(p, what, kind) if what in ('drafted', 'cover') else kind.partition('/')[0] if what == 'aim'
                        else walk.strike_names()[kind][2] if what == 'strike' else 'rifle' if what == 'reload'
                        else walk.draw_keys(kind)['guard'] if what in ('draw', 'holster') else None,
                        drawn=drawn_from(what, kind, p.time % 1.0))
            draw_tool(what if what in ('read', 'smoke', 'stir', 'stir_crouch', 'stir_campfire', 'stir_campfire_sit') else kind if what == 'work' else None, p.time % 1.0, X)
            draw_instrument(what)
            draw_load(p, what, kind)
            place_grip(p, what, kind, write='grip' not in state['quiet'])
            state['mix'] = body_mix()
            draw_worn(p, shield_taken(what, kind, p.time % 1.0), drawn_from(what, kind, p.time % 1.0))
            draw_holsters(p, drawn_from(what, kind, p.time % 1.0))
            place_holster_grip(p, write='holster' not in state['quiet'])
            draw_gear(p, hip_held(what, kind, p.time % 1.0))
            place_gear_grip(p, write='gear' not in state['quiet'])
        except Exception as e:   # a share of 0 or 1 (STANCE, PEAK) can divide by zero: say so rather than spam tracebacks
            state['error'] = '%s: %s' % (type(e).__name__, e)
    walk.arm.update_tag()
    redraw()

def clip_changed(self, context):
    """Opens the picked clip's paragraph of numbers, then poses it."""
    what, _ = clip_of(self)
    sync_menus(self)
    for i, (title, _) in enumerate(tuned()[2]):
        if title == CLIP_SECTION.get(what):
            setattr(self, 'show_%d' % i, True)
    repose()

def redraw():
    for w in bpy.context.window_manager.windows:
        for a in w.screen.areas:
            if a.type == 'VIEW_3D':
                a.tag_redraw()

def tick():
    p = props()
    if not p.playing:
        state['timer'] = False
        return None
    what, kind = clip_of(p)
    with varied(variant_numbers(p, what, kind)):   # a variant with another mocap clip has its own length
        seconds = loop_seconds(what, kind)
    if seconds:
        p['time'] = (p.time + 1 / (30.0 * seconds)) % 1.0   # game seconds at 1x: one second a second
    p.phase = (p.phase + p.speed / 30.0) % 1.0   # its update re-poses
    return 1 / 30.0

def toggle_play(self, context):
    if self.playing and not state['timer']:
        state['timer'] = True
        bpy.app.timers.register(tick)

# The Clip menu is two: a category, then the clips in it (clip_in); clip holds the pick for the rest of the preview.
CATEGORIES = [('move', 'Moving', 'Walking, running, standing, swimming, crawling and jumping'),
              ('rest', 'Sitting and lying', 'The seated and lying loops'),
              ('carry', 'Carrying and leading', 'Carrying a load or a person, bending for it, leading on a rope'),
              ('work', 'Working', 'One stroke of each tool'),
              ('drafted', 'Drafted stances', 'The drafted stance for each weapon kind and weapon'),
              ('aim', 'Aiming', 'The aim, fire and nock clips for each aimed weapon'),
              ('strike', 'Melee and throwing', 'The melee blows and throws'),
              ('draw', 'Drawing and holstering', 'Taking each type of weapon from each holster slot, and putting it back'),
              ('cast', 'Casting', 'Casting a psycast or ability'),
              ('gear', 'Hip gear', 'Taking, holding and pointing a psychic lance, orbital targeter or tornado generator'),
              ('hurt', 'Hurt and reactions', 'Holding a wound, cowering, flinching, staggered and stunned')]
CATEGORY_OF = {'drafted': 'drafted', 'carry': 'carry', 'carried': 'carry', 'bend': 'carry', 'lead': 'carry',
               'sit': 'rest', 'lie': 'rest', 'getup': 'rest', 'sleep': 'rest', 'baby': 'rest', 'downed': 'hurt', 'downed_side': 'hurt', 'lovin': 'rest', 'dead': 'hurt', 'ragdoll': 'hurt', 'caged': 'hurt', 'fall': 'hurt', 'work': 'work', 'pluck': 'work', 'tend': 'work', 'toys': 'work', 'play_sit': 'rest', 'visit_sit': 'rest', 'meditate': 'rest', 'pray_sit': 'rest', 'research': 'work', 'research_stand': 'work', 'research_floor': 'work', 'crouch': 'work', 'sow': 'work', 'bench': 'work', 'bench_sit': 'work', 'chair': 'work', 'tame': 'work', 'tame_kneel': 'work', 'stomp': 'work', 'pat': 'work', 'piano': 'rest', 'piano_sit': 'rest', 'harp': 'rest', 'harp_sit': 'rest', 'drum': 'rest', 'drum_sit': 'rest', 'eat_sit': 'rest', 'eat_stand': 'rest', 'smoke': 'rest', 'pill': 'rest', 'read': 'rest', 'vomit': 'hurt', 'rub': 'hurt', 'talk': 'rest', 'teach': 'rest', 'wave': 'rest', 'sad': 'hurt', 'drunk': 'hurt', 'stir': 'work', 'stir_crouch': 'work', 'stir_campfire': 'work', 'stir_campfire_sit': 'work', 'burning': 'hurt', 'reach': 'work', 'toss': 'rest', 'snowball': 'rest', 'kiss': 'rest', 'reload': 'aim', 'aim': 'aim', 'strike': 'strike', 'cast': 'cast', 'gear': 'gear', 'shield': 'drafted', 'draw': 'draw', 'holster': 'draw',
               'hurt': 'hurt', 'cower': 'hurt', 'hunker': 'hurt', 'cover': 'drafted', 'dive': 'hurt', 'duck': 'hurt', 'roll': 'hurt', 'flinch': 'hurt', 'reel': 'hurt', 'daze': 'hurt'}

def category_of(clip):
    """The category a clip's id ('drafted:rifle', 'gait' ...) is listed under; anything not named is Moving."""
    return CATEGORY_OF.get(clip.partition(':')[0], 'move')

def category_items():
    """The categories that have clips, in CATEGORIES' order."""
    have = {category_of(c) for c, _, _ in clip_items()}
    return [c for c in CATEGORIES if c[0] in have]

clip_in_cache = {}   # Blender's dynamic menus need their item strings kept alive

def clip_in_items(self, context):
    """The clips in the picked category, numbered from 0 in their order."""
    cat = self.category
    items = [(c, label, text, i) for i, (c, label, text) in
             enumerate(x for x in clip_items() if category_of(x[0]) == cat)]
    clip_in_cache[cat] = items
    return items

def category_changed(self, context):
    """Picks the clip last shown in the new category (its first, the first time)."""
    ids = [c for c, _, _, _ in clip_in_items(self, context)]
    last = state.setdefault('last_clip', {}).get(self.category)
    if ids:
        self.clip = last if last in ids else ids[0]

def clip_in_changed(self, context):
    if self.clip_in != self.clip:
        self.clip = self.clip_in

def sync_menus(p):
    """Puts the category and clip_in menus on p.clip, quietly (no update calls)."""
    cat = category_of(p.clip)
    cats = [c for c, _, _ in category_items()]
    if cat in cats:
        p['category'] = cats.index(cat)
        ids = [c for c, _, _, _ in clip_in_items(p, None)]
        if p.clip in ids:
            p['clip_in'] = ids.index(p.clip)
        state.setdefault('last_clip', {})[cat] = p.clip

def clip_items():
    items = [('gait', 'Walk / run', 'The walk and run cycle, blended by Gait'),
             ('still', 'Still', 'The still standing pose (walk.stand_pose, idle.obj)'),
             ('idle', 'Idle', 'The idle loop: breathing and weight shifts (walk.stand_at)')]
    items += [('drafted:' + k, 'Drafted: ' + k, 'The drafted stance for a %s (walk.DRAFTED)' % k) for k in walk.DRAFTED]
    items += [('carry:' + k, 'Carrying: ' + k, 'Carrying a load: %s (walk.CARRY)' % k) for k in walk.CARRY]
    items += [('carried:' + k, 'Carried: ' + k, 'The person carried %s, where the carrier holds it (walk.carried_at)' % k)
              for k in walk.CARRY if k != 'item' and not walk.CARRY[k].get('pet')]
    items += [('sit', 'Sitting', 'The seated loop (walk.sit_at)'),
              ('lie', 'Lying', 'The lying loop, upright: the game lays it down (walk.lie_at)'),
              ('crawl', 'Crawling', 'The crawl, upright: the game tips it face down (walk.crawl_at)')]
    if hasattr(walk, 'limp_at'):
        items += [('limp:L', 'Limping: left', 'The walk favouring a bad left leg (walk.limp_at)'),
                  ('limp:R', 'Limping: right', 'The walk favouring a bad right leg (walk.limp_at)'),
                  *([('hop:L', 'Hopping: left leg gone', 'Hopping on the right leg (walk.hop_at)'),
                     ('hop:R', 'Hopping: right leg gone', 'Hopping on the left leg (walk.hop_at)')] if hasattr(walk, 'hop_at') else []),
                  ('wade', 'Wading', 'The walk through water: the game sinks it to the depth (walk.wade_at)'),
                  ('swim', 'Swimming', 'The front crawl, upright: the game tips it face down (walk.swim_at)'),
                  ('tread', 'Treading water', 'Swimming but still, upright: the game sinks it to the neck (walk.tread_at)')]
    if hasattr(walk, 'hurt_at'):
        items += [('hurt:' + k, 'Hurt: ' + k.replace('_', ' '), 'A hand on the wound, hunched (walk.hurt_at)')
                  for k in walk.hurt_kinds()]
    if hasattr(walk, 'cower_at'):
        items.append(('cower', 'Cowering', 'Hunched, both hands drawn up in front of the chest (walk.cower_at), over the run from the waist up'))
    if hasattr(walk, 'hunker_at'):
        items.append(('hunker', 'Cowering: hunkered', 'Cowering standing still, crouched (walk.hunker_at)'))
    if hasattr(walk, 'cover_at'):
        items.append(('cover', 'Taking cover', 'Crouched low behind cover, drafted (walk.cover_at); its arms only hang'))
        items += [('cover:' + k, 'Taking cover: ' + k, 'The %s drafted stance with its hips let down over its own feet (walk.crouch_legs), as the game does' % k)
                  for k in ('rifle', 'pistol', 'bow') if k in walk.DRAFTED]
    if hasattr(walk, 'lead_at'):
        items.append(('lead', 'Leading', 'The right hand reached a little back on a rope (walk.lead_at), over the gait on that arm only'))
    if hasattr(walk, 'shield_at'):
        items.append(('shield', 'Shield guard', 'A shield held up in the left fist in front of the shoulder while CE shows it (walk.shield_at), over the gait on that arm only; the shields are the Worn panel\'s CE_MeleeShield and CE_BallisticShield'))
    if hasattr(walk, 'shield_draw_at'):
        try:
            fig = figure()
            shields = [n for n in fig.names if fig.piece(n).held_rim is not None]
        except OSError:
            shields = []
        for n in shields:
            items += [('shield:draw/' + n, 'Shield: taking %s off the back' % n, 'The left hand round to its edge on the back and into the guard (walk.shield_draw_at), as exported (held_rim); tick it in the Worn panel to see it'),
                      ('shield:put/' + n, 'Shield: putting %s back' % n, 'shield_draw backwards, as the game puts it back')]
    if hasattr(walk, 'cast_at'):
        items += [('cast', 'Casting', 'The left hand raised to the target, palm out (walk.cast_at), over the gait'),
                  ('cast:touch', 'Casting: touch', 'The left hand reached out low to touch (walk.cast_at)')]
    if hasattr(walk, 'hip_hold_at'):
        items += [('gear:hold', 'Hip gear: holding', 'The hip gear held ready in the left hand while drafted (walk.hip_hold_at), over the gait on that arm only'),
                  ('gear:aim', 'Hip gear: pointing', 'Pointed at the target, from the lowest pitch to the highest by Time (walk.hip_aim_at), over the gait from the waist up'),
                  ('gear:fire', 'Hip gear: going off', 'The push as it goes off, by pitch (walk.hip_aim_at with fired)'),
                  ('gear:draw', 'Hip gear: taking it', 'The left hand to the slot on the right shoulder and back to the hold (walk.hip_draw_at)'),
                  ('gear:put', 'Hip gear: putting it away', 'hip_draw backwards, as the game puts it away')]
    if hasattr(walk, 'flinch_at'):
        items += [('flinch:' + k, 'Flinching: ' + w, 'One jolt from a hit, pushed %s, by Time (walk.flinch_at); over the gait it is added on top, as the game does' % w.lower())
                  for k, w in (('B', 'Back'), ('F', 'Forward'), ('L', 'Left'), ('R', 'Right'))]
    if hasattr(walk, 'reel_at'):
        items += [('reel', 'Staggered', 'A stumble to one knee and back up, by Time (walk.reel_at); the whole figure standing still, over the gait from the waist up'),
                  ('daze', 'Stunned', 'Slumped and dazed (walk.daze_at); the whole figure standing still')]
    if hasattr(walk, 'jump_at'):
        items += [('jump', 'Jumping', 'One jump, take-off to landing, by Time (walk.jump_at)'),
                  ('land', 'Jumping: crouched', 'Crouched to jump, or just landed (walk.land_pose)')]
    if hasattr(walk, 'work_at'):
        items += [('work:' + k, 'Working: ' + k, 'One stroke of the %s, IsoRim\'s tool in the right hand (walk.WORK)' % k)
                  for k in walk.WORK]
    if getattr(walk, 'PLUCK_CLIP', None) is not None:
        items.append(('pluck', 'Working: kneeling', 'Knelt at the ground, leant over the right leg, the right hand pawing at it '
                      '(walk.pluck_at, from the mocap clip in pluck_clip.json)'))
    if getattr(walk, 'TEND_CLIP', None) is not None:
        items.append(('tend', 'Working: tending', 'Knelt by a patient, both hands busy in front '
                      '(walk.tend_at, from the mocap clip in tend_clip.json)'))
        if hasattr(walk, 'toys_at'):
            items.append(('toys', 'Playing with a baby: toys', 'Knelt by a baby on the floor, a hand waved over it '
                          '(walk.toys_at, the tend kneel)'))
        if hasattr(walk, 'play_sit_at'):
            items.append(('play_sit', 'Playing with a baby: sat', 'Sat cross-legged rocking a baby in the arms '
                          '(walk.play_sit_at, the play carry on the meditation)'))
        if hasattr(walk, 'visit_sit_at') and getattr(walk, 'TALK_CLIP', None) is not None:
            items.append(('visit_sit', 'Visiting the sick: sat', 'Sat cross-legged by a patient on the ground, talking '
                          'with the hands (walk.visit_sit_at, the talk on the meditation)'))
    if getattr(walk, 'MEDITATE_CLIP', None) is not None:
        items.append(('meditate', 'Meditating', 'Sat cross-legged on the floor, the hands on the knees, breathing '
                      '(walk.meditate_at, from the mocap clip in meditate_clip.json)'))
    if hasattr(walk, 'pray_sit_at'):
        items.append(('pray_sit', 'Praying: on a seat', 'Sat on a chair or throne, the palms pressed together in front of '
                      'the chest and the head bowed (walk.pray_sit_at, over the sit from the waist up)'))
    if getattr(walk, 'RESEARCH_CLIP', None) is not None and hasattr(walk, 'research_at'):
        items.append(('research', 'Working: researching', 'Sat at the bench looking over something held in both hands, over the '
                      'sit from the waist up (walk.research_at, from the mocap clip in inspect_clip.json)'))
    if getattr(walk, 'RESEARCH_CLIP', None) is not None and hasattr(walk, 'research_stand_at'):
        items.append(('research_stand', 'Working: researching standing', 'Stood at the bench looking over something held '
                      'in both hands, from the waist up on the standing pose (walk.research_stand_at, inspect_clip.json)'))
    if getattr(walk, 'RESEARCH_CLIP', None) is not None and getattr(walk, 'MEDITATE_CLIP', None) is not None \
            and hasattr(walk, 'research_floor_at'):
        items.append(('research_floor', 'Working: researching on the floor', 'Sat cross-legged on the floor with no seat, '
                      'looking over something held in both hands (walk.research_floor_at, inspect_clip.json on the '
                      'meditation)'))
    for name, label, what in (('crouch', 'Working: at the ground', 'Knelt on one knee with both hands at the ground, the whole figure'),
                              ('sow', 'Working: sowing', 'Crouched hunched over with both hands at the ground, the whole figure'),
                              ('bench', 'Working: stood at a bench', 'Stood working with both hands, the whole figure'),
                              ('bench_sit', 'Working: sat at a bench', 'Sat working on a bench, over the sit from the waist up'),
                              ('chair', 'Working: sat with no bench', 'Sat working on the lap, over the sit from the waist up')):
        if getattr(walk, name.upper() + '_CLIP', None) is not None and hasattr(walk, name + '_at'):
            items.append((name, label, '%s (walk.%s_at, from the mocap clip in %s_clip.json)' % (what, name, name)))
    if hasattr(walk, 'tame_at'):
        items.append(('tame', 'Taming: standing', 'The right hand held out palm up to a tall animal, coaxing '
                      '(walk.tame_at), the whole figure'))
        if getattr(walk, 'CROUCH_CLIP', None) is not None:
            items.append(('tame_kneel', 'Taming: knelt', 'Knelt on the ground work\'s kneel, the right hand held out palm '
                          'up to a small animal (walk.tame_at)'))
    if hasattr(walk, 'stomp_at'):
        items.append(('stomp', 'Beating fires: stomping', 'A fire on the floor or an item stamped out with one foot, a '
                      'stamp a beat (walk.stomp_at), the whole figure'))
        items.append(('pat', 'Beating fires: patting', 'A fire on a structure or furniture slapped out with the palms in '
                      'turn, a slap a beat (walk.pat_at), the whole figure'))
    if hasattr(walk, 'MUSIC'):
        for name, label, what in (('piano', 'Music: keyboard, standing', 'The piano or harpsichord played stood at it, the whole figure'),
                                  ('piano_sit', 'Music: keyboard, seated', 'The piano or harpsichord played sat at it, over the sit from the waist up'),
                                  ('harp', 'Music: harp, standing', 'The harp\'s strings plucked stood at it, the whole figure'),
                                  ('harp_sit', 'Music: harp, seated', 'The harp played sat at it, over the sit from the waist up'),
                                  ('drum', 'Music: drum, standing', 'The tribal drum beaten with the palms stood at it, the whole figure'),
                                  ('drum_sit', 'Music: drum, seated', 'The drum beaten sat at it, over the sit from the waist up')):
            if hasattr(walk, name + '_at'):
                items.append((name, label, '%s (walk.%s_at, IsoRim\'s model of it drawn where the game puts it)' % (what, name)))
    if getattr(walk, 'VOMIT_CLIP', None) is not None:
        items.append(('vomit', 'Vomiting', 'Bent over, hands on the knees, heaving '
                      '(walk.vomit_at, from the mocap clip in vomit_clip.json)'))
    if getattr(walk, 'RUB_CLIP', None) is not None:
        items.append(('rub', 'Rubbing the hands', 'Cold: the hands brought together and rubbed for warmth, over the gait '
                      'from the waist up (walk.rub_at, from the mocap clip in rub_clip.json)'))
    if getattr(walk, 'TALK_CLIP', None) is not None and hasattr(walk, 'talk_at'):
        items.append(('talk', 'Talking', 'Board games, speeches and parties: talking with the hands, over the gait '
                      'from the waist up (walk.talk_at, from the mocap clip in talk_clip.json)'))
    if hasattr(walk, 'teach_at'):
        items.append(('teach', 'Teaching', 'A lesson at a school desk: explaining with the hands, over the gait or the sit '
                      'from the waist up (walk.teach_at, keyed by hand in TEACH_KEYS); the game turns the head to the pupil'))
    if hasattr(walk, 'wave_at'):
        items.append(('wave', 'Waving', 'A social interaction in passing: the right hand up beside the head and waved, once '
                      'through by Time (walk.wave_at), over the gait on that arm only'))
    if hasattr(walk, 'sad_at'):
        items.append(('sad', 'Sad', 'A sad wander: slumped, the head hung, sighing (walk.sad_at), over the gait from the '
                      'waist up'))
    if hasattr(walk, 'drunk_at'):
        items.append(('drunk', 'Drunk', 'Drunk (AlcoholHigh): the trunk swaying in a figure of eight, the head lolling, the '
                      'arms hanging loose (walk.drunk_at); the game adds it on top of anything, the arms only with the '
                      'hands free, and weaves the figure on the move'))
    if hasattr(walk, 'stir_at'):
        if getattr(walk, 'BENCH_CLIP', None) is not None:
            items.append(('stir', 'Working: stirring at a bench', 'Cooking, now and then: a wooden spoon circled in a pot, '
                          'the left hand on its rim, made on the bench work (walk.stir_at)'))
        if getattr(walk, 'CROUCH_CLIP', None) is not None:
            items.append(('stir_crouch', 'Working: stirring knelt', 'The same knelt on the ground work '
                          "(walk.stir_at's stir_crouch)"))
        if getattr(walk, 'MEDITATE_CLIP', None) is not None and 'stir_campfire' in getattr(walk, 'STIR_KINDS', {}):
            items.append(('stir_campfire', 'Working: roasting at a campfire', 'Sat cross-legged, a stick of meat held out '
                          "over the fire, the whole time the game cooks there (walk.stir_at's stir_campfire)"))
        if 'stir_campfire_sit' in getattr(walk, 'STIR_KINDS', {}):
            items.append(('stir_campfire_sit', 'Working: roasting at a campfire on a chair', 'Sat on a chair leant forward, '
                          "a stick of meat held out over the fire (walk.stir_at's stir_campfire_sit)"))
    if hasattr(walk, 'burn_at'):
        items.append(('burning', 'On fire', 'Patting at the flames on itself, both hands at the chest and belly in turn '
                      '(walk.burn_at), over the run from the waist up'))
    if hasattr(walk, 'reach_at'):
        items.append(('reach', 'Reaching', 'Flicking a switch or opening a lid: the right hand pressed out in front '
                      '(walk.reach_at), the whole figure'))
    if hasattr(walk, 'toss_at'):
        items.append(('toss', 'Tossing', 'Horseshoes and hoopstone: one underhand pitch, by Time (walk.toss_at); the whole '
                      'figure standing, over the sit from the waist up'))
    if hasattr(walk, 'snowball_at'):
        items.append(('snowball', 'Throwing a snowball', "Romance on the Rim's snowball fight: one quick overhand throw, by "
                      'Time (walk.snowball_at, SNOW_KEYS); the whole figure standing'))
    if hasattr(walk, 'kiss_at'):
        items.append(('kiss', 'Kissing', "Romance on the Rim's kiss: leant in, the head tilted, the hands at the partner's "
                      'waist, over the gait from the waist up (walk.kiss_at); the game draws the two closer'))
    if getattr(walk, 'RELOAD_CLIP', None) is not None and hasattr(walk, 'reload_at'):
        items.append(('reload', 'Reloading', "Combat Extended's reload: the left hand to the belt for a magazine and "
                      'back, from the waist up (walk.reload_at, from the mocap clip in reload_clip.json)'))
    if getattr(walk, 'EAT_SIT_CLIP', None) is not None and hasattr(walk, 'eat_sit_at'):
        items.append(('eat_sit', 'Eating: at a table', 'Seated, the right hand from the meal on the table to the mouth '
                      '(walk.eat_sit_at, from the mocap clip in eat_sit_clip.json)'))
    if getattr(walk, 'EAT_STAND_CLIP', None) is not None and hasattr(walk, 'eat_stand_at'):
        items.append(('eat_stand', 'Eating: standing', 'The meal held in the right hand, the left bringing it to the mouth '
                      '(walk.eat_stand_at, from the mocap clip in eat_stand_clip.json)'))
    if hasattr(walk, 'smoke_at'):
        items.append(('smoke', 'Taking drugs: smoking', 'Smokeleaf or flake: the joint in the right hand raised to the lips '
                      'for a drag and lowered as the smoke is blown out (walk.smoke_at, SMOKE_KEYS), over the sit or the idle '
                      'from the waist up; the joint (tool_joint, make_joint.py) drawn on the hand'))
    if hasattr(walk, 'pill_at'):
        items.append(('pill', 'Taking drugs: a pill', 'A pill or a powder: pinched from the left palm up to the mouth and '
                      'swallowed with the head tipped back, once through over the chew (walk.pill_at, PILL_KEYS)'))
    if hasattr(walk, 'read_at'):
        items.append(('read', 'Reading', 'The open book on the left palm, the right hand on its page turning one now and then '
                      '(walk.read_at), laid over the sit or the idle from the waist up; IsoRim\'s open book drawn on the hand'))
        if hasattr(walk, 'READ_SHOW_OUT'):
            items.append(('read:L', 'Reading: showing the book left', "Romance on the Rim's shared reading: the book held out to "
                          'someone sat on the left (read_show_L, walk.read_at with show, READ_SHOW_ numbers)'))
            items.append(('read:R', 'Reading: showing the book right', 'The same to the right (read_show_R)'))
    if getattr(walk, 'SLEEP_CLIP', None) is not None:
        items.append(('sleep', 'Asleep', 'Asleep on the back, upright: the game lays it down (walk.sleep_at, sleep_clip.json)'))
    if hasattr(walk, 'baby_at') and getattr(walk, 'CRAWL_CLIP', None) is not None:
        items.append(('baby', 'Baby awake', 'A baby on its back waving and kicking, upright (walk.baby_at: the arms posed by '
                      'hand, the crawl\'s legs)'))
    if getattr(walk, 'DOWNED_CLIP', None) is not None:
        items.append(('downed', 'Downed: in bed', 'Downed on the back, groaning, upright (walk.downed_at, downed_clip.json)'))
    if getattr(walk, 'DOWNED_SIDE_CLIP', None) is not None:
        items.append(('downed_side', 'Downed: on the ground', 'Downed, curled on the side, upright (walk.downed_side_at, downed_side_clip.json)'))
        if hasattr(walk, 'lovin_at'):
            items += [('lovin:L', 'Lovin: partner on the left', 'On the side facing the partner, still, upright (walk.lovin_at)'),
                      ('lovin:R', 'Lovin: partner on the right', 'The same facing the right (lovin_R)')]
    if getattr(walk, 'DIE_CLIP', None) is not None:
        items += [('dead', 'Dead', 'A corpse: the death fall\'s end, upright (walk.dead_at)'),
                  ('fall:die', 'Falling: dead', 'From standing to dead, once through, by Time, upright as the lie is (walk.fall_at, die_clip.json)')]
    if hasattr(walk, 'ragdoll_pose'):
        items.append(('ragdoll', 'Ragdoll: neutral', 'The pose a limp ragdoll\'s joints are pulled toward, standing (walk.ragdoll_pose)'))
    if hasattr(walk, 'caged_pose'):
        items.append(('caged', 'Dead: in a gibbet cage', 'A corpse slumped standing on the cage floor (walk.caged_pose)'))
    if getattr(walk, 'KNOCKDOWN_CLIP', None) is not None:
        items.append(('fall:knockdown', 'Falling: downed', 'From standing to downed on the side, once through, by Time, upright (walk.fall_at, knockdown_clip.json)'))
    if getattr(walk, 'GETUP_CLIP', None) is not None:
        items.append(('getup', 'Getting up', 'From lying on the back to standing, once through, by Time '
                      '(walk.getup_at, from the mocap clip in getup_clip.json)'))
    if hasattr(walk, 'dive_at'):
        for side, hand in (('R', 'right'), ('L', 'left')):
            items += [('dive:' + side, 'Diving flat to hunker, weapon in the %s hand' % hand, 'From standing to down on the front, once through, by Time (walk.dive_at)'),
                      ('duck:' + side, 'Hunkering, weapon in the %s hand' % hand, 'Flat on the front under fire, the hands by the head (walk.duck_at)')]
    if hasattr(walk, 'roll_at'):
        items.append(('roll', 'Rolling to put out a fire', 'Stop, drop and roll: over each way onto the back and back, the arms crossed (walk.roll_at)'))
    if hasattr(walk, 'bend_pose'):
        items.append(('bend', 'Bending', 'Squatting to pick up or set down (walk.bend_pose)'))
    if hasattr(walk, 'aim_at'):
        what = {'aim': 'Aiming', 'fire': 'Firing', 'nock': 'Nocked'}
        items += [('aim:%s/%s' % (k, s), '%s: %s' % (what[s], k), 'The %s %s, by Time from the lowest aim to the highest (walk.aim_at)' % (k, s))
                  for k in walk.aimed_names() for s in walk.aim_stages(k)]
    if hasattr(walk, 'strike_at'):
        names = {'smash': 'smash', 'slash': 'slash', 'stab': 'stab', 'fist': 'punch', 'butt': 'butt', 'jab': 'jab'}
        for k, (_, guard, hold, side) in walk.strike_names().items():
            if k.startswith('throw_'):
                label = 'Throwing: %s hand (%s)' % ('left' if side == 'L' else 'right', guard)
            else:
                low = k.startswith('melee_down_')
                kind = k.split('_')[2 if low else 1]
                label = 'Melee%s: %s%s' % (' at the downed' if low else '', names[kind], ' (%s)' % guard if guard not in ('melee', 'unarmed') else
                                         ' (%s)' % ('left' if side == 'L' else 'right') if kind == 'fist' else '')
            items.append(('strike:' + k, label, 'One %s from the guard and back, by Time; the hit half way (walk.strike_at)'
                          % ('throw' if k.startswith('throw_') else 'blow')))
    if hasattr(walk, 'draw_at'):
        for k, (slot, t) in walk.draw_names().items():
            weapon = walk.HOLSTER_WEAPON[t]
            items.append(('draw:' + k, 'Draw: %s, %s' % (slot, t), 'Taking a %s from %s, by Time, as the export finds its handle (walk.draw_at)' % (weapon, slot)))
            items.append(('holster:' + k, 'Holster: %s, %s' % (slot, t), 'Putting a %s back in %s, by Time: the draw backwards' % (weapon, slot)))
    return items

def build_props():
    """The settings, one slider per walk.py number, place and stance entry, made fresh from the file so new ones show
    up on reload."""
    names, run, sections = tuned()
    ann = {'aim_' + key: bpy.props.FloatProperty(name=label, description="%s: AIMED '%s'" % (text, key), update=aim_number_changed(key),
                                                 soft_min=-90.0 if not key.isupper() else ANGLE_RANGE[0] if key in ANGLE_KEYS else -0.5,
                                                 soft_max=90.0 if not key.isupper() else ANGLE_RANGE[1] if key in ANGLE_KEYS else 1.0)
           for key, label, text in AIM_NUMBERS}
    ann.update({
        'clip': bpy.props.EnumProperty(name='Clip', items=clip_items(), update=clip_changed),
        'category': bpy.props.EnumProperty(name='Category', description='Which kind of clip to pick from',
                                           items=category_items(), update=category_changed),
        'clip_in': bpy.props.EnumProperty(name='Clip', description='The clip to pose', items=clip_in_items,
                                          update=clip_in_changed),
        'gait': bpy.props.FloatProperty(name='Gait', description='0 walks, 1 runs (the game bakes sixths of the way)',
                                        min=0.0, max=1.0, update=repose),
        'phase': bpy.props.FloatProperty(name='Phase', description='Place in the cycle (0: left heel strike)',
                                         min=0.0, max=1.0, update=repose),
        'time': bpy.props.FloatProperty(name='Time', description='Place in a loop timed by the clock (0 .. 1 round it)',
                                        min=0.0, max=1.0, update=repose),
        'variant': bpy.props.IntProperty(name='Variant', default=1, min=1, max=9, update=repose,
                                         description='Which of the clip and its variants (walk.VARIANTS) to pose: 1 the clip '
                                                     'itself, 2 on its variants (<clip>~2 ...), which the game picks between '
                                                     'at random. The sliders edit the numbers a variant leaves alone'),
        'over': bpy.props.BoolProperty(name='Over the gait', update=repose,
                                       description='Lay the stance over the walk or run, as the game does when the pawn moves'),
        'playing': bpy.props.BoolProperty(name='Play', update=toggle_play),
        'speed': bpy.props.FloatProperty(name='Cycles/s', default=1.0, min=0.05, soft_max=3.0,
                                         description='Cycle playback speed (the bake is 24 frames a cycle); loops timed '
                                                     'by the clock play at one game second a second'),
        'turn': bpy.props.IntProperty(name='Turn', default=0, min=-3, max=4,
                                      description="Game view: the figure's heading in 45 degree steps from south"),
        'weapon': bpy.props.BoolProperty(name='Show weapon', default=True, update=repose,
                                         description="Draw the drafted stance's weapon as the game does"),
        'weapon_models': bpy.props.BoolProperty(name='IsoRim models', default=True, update=repose,
                                                description="Show a weapon IsoRim has a 3D model for as that model, fitted "
                                                            "to the sprite as IsoRim fits it (its 'Weapons as 3D models' "
                                                            "setting), in place of the sprite's slab"),
        'load': bpy.props.BoolProperty(name='Show load', default=True, update=repose,
                                       description='Draw what a carry clip holds where the game puts it: an item or the carried person'),
        'load_size': bpy.props.FloatVectorProperty(name='Item size', default=(1.0, 1.0), size=2, min=0.05, soft_max=3.0,
                                                   step=10, precision=2, update=repose,
                                                   description="The carried item's drawn size, cells (its graphic's drawSize: "
                                                               "1 by 1 for most things), as across and front to back"),
        'load_thick': bpy.props.FloatProperty(name='Thickness', default=0.1, min=0.04, max=0.2, step=1, precision=2,
                                              update=repose, description="The carried item's thickness, cells (IsoRim's "
                                                                         "Forms.ItemThickness, 0.04 to 0.2)"),
        'shield_with': bpy.props.EnumProperty(name='Shield weapon', items=shield_with_items, update=repose,
                                              description="The weapon to see a shield stance with: its own grip in it "
                                                          "(walk.shielded_entry), worked out from the kind's stance shown"),
        'copy_from': bpy.props.EnumProperty(name='Copy from', items=[(k, k, 'Copy from drafted_%s' % k) for k in walk.DRAFTED],
                                            description='The drafted stance to copy onto the one shown'),
        'holsters': bpy.props.BoolProperty(name='Show holsters', default=True, update=repose,
                                           description='Draw a weapon in each holster slot (walk.HOLSTERS) on whatever clip is shown'),
        'holster_slot': bpy.props.EnumProperty(name='Slot', items=holster_slot_items, update=slot_picked,
                                               description='The slot whose numbers are shown and which the handle is on'),
    })
    ann.update({
        'worn_show': bpy.props.BoolProperty(name='Show worn', default=True, update=repose,
                                            description="Put on the figure what is ticked here, as the game does, in place of the garments on the rig, "
                                                        "which come back with this off"),
        'worn_wire': bpy.props.BoolProperty(name='Wire', default=False, update=repose, description='Draw what is worn as wires, to see the body and slots under it'),
    })
    for g in garment_names():
        ann[worn_prop(g)] = bpy.props.BoolProperty(name=g, default=False, update=repose, description='Wear %s (garment_%s.txt)' % (g, g))
    ann.update({
        'gear_show': bpy.props.BoolProperty(name='Show worn gear', default=True, update=repose,
                                            description="Draw IsoRim's pack, belt and hip tool models on the body (walk.GEAR) on whatever clip is shown"),
        'gear_mount': bpy.props.EnumProperty(name='Mount', items=gear_mount_items, update=repose,
                                             description='The mount whose numbers are shown and which the handle is on'),
    })
    for mount, g in getattr(walk, 'GEAR', {}).items():
        pre = 'gr_%s_' % mount
        choices = gear_models().get(mount, [])
        ann[pre + 'on'] = bpy.props.BoolProperty(name='On', default=True, update=repose,
                                                 description='This mount has its gear on: a pack pushes what lies on the back out, and with a backpack sends the back\'s weapons to the sides')
        ann[pre + 'model'] = bpy.props.EnumProperty(name='Model', items=[(r, a.replace('Apparel_', ''), 'IsoRim\'s model for %s' % a) for a, r in choices]
                                                    or [('', '(none)', 'IsoRim has no model for this mount')], update=repose,
                                                    description='The gear previewed on this mount')
        ann[pre + 'bone'] = bpy.props.EnumProperty(name='Bone', items=[(b.name, b.name, 'Rides %s' % b.name) for b in walk.arm.data.bones if b.use_deform],
                                                   default=g['bone'], update=repose, description='The bone the mount rides')
        ann[pre + 'at'] = bpy.props.FloatVectorProperty(name='At', default=g['at'], size=3, soft_min=-0.4, soft_max=0.4, step=0.1, precision=4,
                                                        update=repose, subtype='XYZ', description=
                                                        'Where its reference point goes at rest, model units from the hips: x left, y back, z up')
        ann[pre + 'scale'] = bpy.props.FloatProperty(name='Scale', default=g['scale'], min=0.05, soft_max=2.0, step=1, precision=3, update=repose,
                                                     description="The model's size, a share of IsoRim's")
        ann[pre + 'depth'] = bpy.props.FloatProperty(name='Depth', default=g.get('depth', 1.0), min=0.1, soft_max=3.0, step=1, precision=3, update=repose,
                                                     description="A belt's depth stretched this much further than its width")
        ann[pre + 'out'] = bpy.props.FloatVectorProperty(name='Out', default=g['out'], size=3, soft_min=-1.0, soft_max=1.0, step=1, precision=3,
                                                         update=repose, subtype='XYZ', description=
                                                         'The way out of the body here: the game pushes the mount out along it past the garments worn')
        ann[pre + 'turn'] = bpy.props.FloatVectorProperty(name='Turn', default=g.get('turn', (0.0, 0.0, 0.0)), size=3, soft_min=-180.0, soft_max=180.0, step=100, precision=1,
                                                          update=repose, subtype='XYZ', description=
                                                          "The model's turn at rest about its reference point, degrees about x, y and z in that order (R on the handle)")
    weapon_items = [(k, k, 'Preview %s in this slot' % k) for k in getattr(walk, 'WEAPONS', {})]
    bone_items = [(b.name, b.name, 'Rides %s' % b.name) for b in walk.arm.data.bones if b.use_deform]
    if hasattr(walk, 'WEAPON_TYPES'):
        type_items = [(t, TYPE_LABELS.get(t, t), 'Of the %s type: its size and holster slots' % t) for t in walk.TYPE_SCALE]
        for kind in getattr(walk, 'WEAPONS', {}):
            ann['wt_%s' % kind] = bpy.props.EnumProperty(name='Type', items=type_items, default=walk.weapon_type(kind),
                                                         update=repose, description='The type %s is (WEAPON_TYPES)' % kind)
        for t, v in walk.TYPE_SCALE.items():
            ann['ts_%s' % t] = bpy.props.FloatProperty(name='Size', default=v, min=0.01, soft_max=2.0, step=1, precision=3,
                                                       update=repose, description="A %s's size held or holstered, a share of "
                                                       "its size on the floor (TYPE_SCALE; the game's mod options start "
                                                       "from it)" % t)
            ann['th_%s' % t] = bpy.props.EnumProperty(name='Holster', items=SIZES, default=walk.TYPE_HOLSTER.get(t, 'big'),
                                                      update=repose, description='The holster slots a %s takes (TYPE_HOLSTER)' % t)
    for slot, h in getattr(walk, 'HOLSTERS', {}).items():
        pre = 'hs_%s_' % slot
        place = dict(soft_min=-0.4, soft_max=0.4, step=0.1, precision=4, update=repose, subtype='XYZ')
        way = dict(soft_min=-1.0, soft_max=1.0, step=1, precision=3, update=repose, subtype='XYZ')
        look = HOLSTER_LOOK[h['size']]
        ann[pre + 'weapon'] = bpy.props.EnumProperty(name='Weapon', items=weapon_items, update=repose,
                                                     default=look if look in walk.WEAPONS else next(iter(walk.WEAPONS)),
                                                     description='The weapon previewed in this slot (its sprite settings are its own)')
        ann[pre + 'size'] = bpy.props.EnumProperty(name='Size', items=SIZES, default=h['size'], update=repose,
                                                   description='Which weapons go in this slot')
        ann[pre + 'bone'] = bpy.props.EnumProperty(name='Bone', items=bone_items, default=h['bone'], update=repose,
                                                   description='The bone the slot rides')
        ann[pre + 'at'] = bpy.props.FloatVectorProperty(name='At', default=h['at'], size=3, description=
                                                        'Its place at rest, model units from the middle of the hips: x left, y back, z up', **place)
        ann[pre + 'aim'] = bpy.props.FloatVectorProperty(name='Aim', default=h['aim'], size=3, description=
                                                         'The way the muzzle points at rest: x left, y back, z up', **way)
        ann[pre + 'roll'] = bpy.props.FloatProperty(name='Roll', default=h['roll'], soft_min=-180.0, soft_max=180.0, step=100,
                                                    precision=1, update=repose, description="Degrees about the line (0: the top kept up)")
        ann[pre + 'behind'] = bpy.props.FloatProperty(name='Behind', default=h.get('behind', 0.0), soft_min=-0.5, soft_max=0.5,
                                                      step=1, precision=3, update=repose,
                                                      description="How far along the line from At the sprite's middle is, a share of its length")
        ann[pre + 'out'] = bpy.props.FloatVectorProperty(name='Out', default=h.get('out', (0.0, 1.0, 0.0)), size=3, description=
                                                         'The way out of the body here: the game pushes the slot out along it past '
                                                         'the garments worn and the body shape', **way)
        ann[pre + 'filled'] = bpy.props.BoolProperty(name='Weapon in it', default=True, update=repose,
                                                     description='Show a weapon here; without one the slots after it have nothing of its to lie on')
        melee_at, melee_aim, melee_roll = walk.holster_place(slot, True)
        ann[pre + 'melee_on'] = bpy.props.BoolProperty(name='Melee place', default=any(k in h for k in ('melee_at', 'melee_aim', 'melee_roll')),
                                                       update=repose, description='A melee weapon has its own place in this slot, as its blade is not along the aim')
        ann[pre + 'melee_at'] = bpy.props.FloatVectorProperty(name='At', default=melee_at, size=3, description='Where a melee weapon sits at rest, as At', **place)
        ann[pre + 'melee_aim'] = bpy.props.FloatVectorProperty(name='Aim', default=melee_aim, size=3, description='The melee weapon\'s aim, as Aim', **way)
        ann[pre + 'melee_roll'] = bpy.props.FloatProperty(name='Roll', default=melee_roll, soft_min=-180.0, soft_max=180.0, step=100, precision=1, update=repose,
                                                          description='Degrees about the line (0: the top kept up)')
    for kind, w in getattr(walk, 'WEAPONS', {}).items():
        pre = 'wp_%s_' % kind
        look_image, look_size, look_offset = weapon_look(kind)
        ann[pre + 'hand'] = bpy.props.EnumProperty(name='Hand', default=w['hand'], update=repose,
                                                   items=[('R', 'Right', 'Held from the right fist'), ('L', 'Left', 'Held from the left fist')])
        ann[pre + 'line'] = bpy.props.EnumProperty(name='Line', items=LINES, default=w['line'], update=line_changed(kind))
        ann[pre + 'aim'] = bpy.props.FloatVectorProperty(name='Aim', default=w.get('aim', (0.0, -0.4, 1.0)), size=3,
                                                         soft_min=-1.0, soft_max=1.0, step=1, precision=3, update=repose,
                                                         subtype='XYZ', description='The fixed direction: x left, y back, z up')
        ann[pre + 'at'] = bpy.props.FloatVectorProperty(name='At', default=w.get('at', (0.0, 0.0, 0.0)), size=3,
                                                        soft_min=-0.2, soft_max=0.2, step=0.1, precision=4, update=repose,
                                                        subtype='XYZ', description="The grip moved from the fist, model units in "
                                                        "the hand's frame at rest (x left, y back, z up)")
        ann[pre + 'drop'] = bpy.props.FloatProperty(name='Drop', default=w['drop'], soft_min=-1.0, soft_max=1.0, step=1,
                                                    precision=3, update=repose,
                                                    description='Straight down added to the line, a share of its length')
        ann[pre + 'behind'] = bpy.props.FloatProperty(name='Behind', default=w['behind'], soft_min=-0.5, soft_max=0.5,
                                                      step=1, precision=3, update=repose,
                                                      description="How far back from the sprite's middle the fist is, a share of its length")
        ann[pre + 'roll'] = bpy.props.FloatProperty(name='Roll', default=w['roll'], soft_min=-180.0, soft_max=180.0,
                                                    step=100, precision=1, update=repose,
                                                    description="Degrees turned about the line (0: the sprite's top kept up)")
        # The sprite: only for the preview, as the game takes them from each weapon's def.
        ann[pre + 'image'] = bpy.props.StringProperty(name='Sprite', subtype='FILE_PATH', default=look_image, update=repose,
                                                      description='A weapon sprite PNG, muzzle to the right as the game has them (empty: a stand-in bar)')
        ann[pre + 'size'] = bpy.props.FloatVectorProperty(name='Draw size', default=look_size, size=2, min=0.05,
                                                          soft_max=3.0, step=10, precision=2, update=repose,
                                                          description="The weapon def's graphicData drawSize, cells")
        ann[pre + 'offset'] = bpy.props.FloatProperty(name='Angle offset', default=look_offset,
                                                      soft_min=-180.0, soft_max=180.0, step=100, precision=1, update=repose,
                                                      description="The weapon def's equippedAngleOffset (degrees): most melee weapons -65")
    for i, _ in enumerate(sections):
        ann['show_%d' % i] = bpy.props.BoolProperty(default=i == 0)
    for name, comment, is_tuple in names:
        if is_tuple:
            v = getattr(walk, name)
            ann['tup_' + name] = bpy.props.FloatVectorProperty(name=name, description=comment, default=v, size=len(v),
                                                               soft_min=-1.5, soft_max=1.5, step=1, precision=3,
                                                               update=repose)
            continue
        w = walk.WALK.get(name, getattr(walk, name))
        r = walk.RUN.get(name, w)
        lo, hi = ANGLE_RANGE if name in ANGLE_KEYS else soft_range(w, r, signed='less than 0' in comment)
        ann['walk_' + name] = bpy.props.FloatProperty(name=name, description=comment, default=w, soft_min=lo, soft_max=hi,
                                                      step=10 if hi > 1 else 1, precision=3, update=repose)
        if name in run:
            ann['run_' + name] = bpy.props.FloatProperty(name=name, description=comment + ' (run)', default=r, soft_min=lo,
                                                         soft_max=hi, step=10 if hi > 1 else 1, precision=3, update=repose)
    for (table, kind), info in registered['stances'].items():
        entry = stance_entry(table, kind)
        for side, label in SIDES:
            if side not in info['sides']:
                continue
            ann[sp(table, kind, side)] = bpy.props.FloatVectorProperty(
                name=label, default=entry.get(side) or info['found'].get(side, (0.0, -0.3, 0.3)), size=3, soft_min=-1.0, soft_max=1.0, step=1,
                precision=3, update=repose, subtype='XYZ',
                description='Wrist place in leg lengths from the middle of the hips at rest: x to the left, y back, z up')
            ann[sp(table, kind, side + '_on')] = bpy.props.BoolProperty(
                name=label, default=side in entry, update=repose, description='Held (off: hangs free and keeps the gait swing)')
            ann[sp(table, kind, side + '_turn')] = bpy.props.FloatVectorProperty(
                name='Turn', default=[math.radians(a) for a in entry.get(side + '_turn', (0.0, 0.0, 0.0))], size=3,
                subtype='EULER', step=100, precision=1, update=repose,
                description="The hand's turn in the chest's frame from its rest turn (x, then y, then z)")
            ann[sp(table, kind, side + '_turn_on')] = bpy.props.BoolProperty(
                name='Set turn', default=side + '_turn' in entry, update=repose,
                description='Hold the hand at a set turn wherever the wrist goes (off: lined up with the forearm). '
                            'Turning its handle sets it')
            ann[sp(table, kind, side + '_elbow')] = bpy.props.FloatProperty(
                name='Elbow lift', default=entry.get(side + '_elbow', 0.0), soft_min=-30.0, soft_max=90.0, step=100,
                precision=1, update=repose,
                description='Degrees the elbow swings out and up about the shoulder-to-wrist line, the hand staying put '
                            '(to keep it out of the side of the chest)')
        body = entry.get('body', {})
        for k in info['body']:
            v = body.get(k, stance_base(table, k))
            lo, hi = ANGLE_RANGE if k in ANGLE_KEYS else soft_range(v)
            ann[sp(table, kind, 'b_' + k)] = bpy.props.FloatProperty(name=k, default=v, soft_min=lo, soft_max=hi,
                                                                     step=10 if hi > 1 else 1, precision=3, update=repose)
            ann[sp(table, kind, 'b_' + k + '_on')] = bpy.props.BoolProperty(
                name=k, default=k in body, update=repose,
                description="Set in this stance's body (off: the stance's default; a weapon's: its kind's, if the kind sets it)")
    return type('MaruGaitProps', (bpy.types.PropertyGroup,), {'__annotations__': ann})

def fmt(v):
    s = ('%.4f' % v).rstrip('0')
    return s + '0' if s.endswith('.') else s

def fmt_tuple(vs):
    return '(%s)' % ', '.join(fmt(v) for v in vs)

def numbers(text):
    return [float(x) for x in re.findall(r'-?\d+\.?\d*', text)]

def same(a, b):
    """Whether two lists of numbers write the same (the sliders hold 32-bit floats, so 40.6 comes back as 40.5999985)."""
    return len(a) == len(b) and all(fmt(x) == fmt(y) for x, y in zip(a, b))

def realign(old, new, rest):
    """rest (what follows a number) with its spaces before a comment widened or narrowed so the comment stays put."""
    pad = len(old) - len(new)
    if pad > 0 and rest.startswith(' '):
        return ' ' * pad + rest
    if pad < 0 and rest.startswith(' ' * (1 - pad)):
        return rest[-pad:]
    return rest

def match_close(src, i):
    """The index of the bracket closing the one at i, skipping comments and strings."""
    depth = 0
    j = i
    while j < len(src):
        c = src[j]
        if c == '#':
            j = src.index('\n', j)
            continue
        if c in '\'"':
            j = src.index(c, j + 1) + 1
            continue
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
            if depth == 0:
                return j
        j += 1
    raise ValueError('unclosed bracket at %d' % i)

ENTRY = re.compile(r"'(\w+)'\s*:\s*\{")
SIDE_IN = re.compile(r"('([LR](?:_turn)?)'\s*:\s*)\(([^)]*)\)(,?)( *)(#?)")
SIDE_NONE = re.compile(r"'([LR](?:_turn|_elbow)?)'\s*:\s*None")
SIDE_KEYS = ('R', 'R_turn', 'R_elbow', 'L', 'L_turn', 'L_elbow')
ELBOW_IN = re.compile(r"('([LR]_elbow)'\s*:\s*)(-?\d+\.?\d*)")
BODY_IN = re.compile(r"('body'\s*:\s*dict\()([^)]*)(\))")

def entries(src, table, entry=None):
    """The entries of a table in walk.py: (kind, start of its key, its opening bracket, its closing one). Entries are
    'kind': {...} (or what entry matches, up to its opening bracket)."""
    entry = entry or ENTRY
    m = re.search(r'^%s\s*=\s*\{' % table, src, re.M)
    if m is None:
        return []
    close = match_close(src, m.end() - 1)
    out = []
    pos = m.end()
    while True:
        e = entry.search(src, pos, close)
        if e is None:
            return out
        line = src[src.rfind('\n', 0, e.start()) + 1:e.start()]
        if '#' in line:
            pos = e.end()
            continue
        end = match_close(src, e.end() - 1)
        out.append((e.group(1), e.start(), e.end() - 1, end))
        pos = end + 1

def write_entry(table, kind, text, changed):
    """One stance entry's text ({...}) with the walk module's values written in: numbers changed in place, comments
    kept, or the whole entry rewritten on one line when a hand or the body is added or taken away (or a weapon's hand
    or turn starts or stops following its kind's, None)."""
    new = getattr(walk, table)[kind]
    body_new = new.get('body', {})
    sides_file = ({(m.group(2), False) for m in SIDE_IN.finditer(text)} | {(m.group(1), True) for m in SIDE_NONE.finditer(text)}
                  | {(m.group(2), False) for m in ELBOW_IN.finditer(text)})
    body_file = BODY_IN.search(text)
    sides_new = {(s, new[s] is None) for s in SIDE_KEYS if s in new}
    if sides_file != sides_new or bool(body_file) != bool(body_new):
        parts = ['%r: %r' % kv for kv in new.items() if kv[0] == 'kind']
        parts += ["'%s': %s" % (s, 'None' if new[s] is None else fmt(new[s]) if s.endswith('_elbow') else fmt_tuple(new[s]))
                  for s in SIDE_KEYS if s in new]
        parts += ['%r: %r' % kv for kv in new.items() if kv[0] not in SIDE_KEYS + ('body', 'kind')]
        if body_new:
            parts.append("'body': dict(%s)" % ', '.join('%s=%s' % (k, fmt(v)) for k, v in body_new.items()))
        changed.append('%s %s' % (table, kind))
        return '{%s}' % ', '.join(parts), re.findall(r'#[^\n]*', text)

    def side(m):
        old = numbers(m.group(3))
        if same(old, new[m.group(2)]):
            return m.group(0)
        changed.append('%s %s %s' % (table, kind, m.group(2)))
        was, now = '(%s)' % m.group(3), fmt_tuple(new[m.group(2)])
        spaces = m.group(5)
        if m.group(6):   # keep the comment where it was
            spaces = ' ' * max(1, len(spaces) + len(was) - len(now))
        return m.group(1) + now + m.group(4) + spaces + m.group(6)
    text = SIDE_IN.sub(side, text)

    def lift(m):
        if same([float(m.group(3))], [new[m.group(2)]]):
            return m.group(0)
        changed.append('%s %s %s' % (table, kind, m.group(2)))
        return m.group(1) + fmt(new[m.group(2)])
    text = ELBOW_IN.sub(lift, text)

    def body(m):
        old = dict((k, float(v)) for k, v in re.findall(r'(\w+)\s*=\s*(-?\d+\.?\d*)', m.group(2)))
        if old.keys() == body_new.keys() and same([old[k] for k in old], [body_new[k] for k in old]):
            return m.group(0)
        changed.append('%s %s body' % (table, kind))
        order = [k for k in old if k in body_new] + [k for k in body_new if k not in old]
        return m.group(1) + ', '.join('%s=%s' % (k, fmt(body_new[k])) for k in order) + m.group(3)
    return BODY_IN.sub(body, text), []

WEAPON_IN = re.compile(r"'(\w+)'\s*:\s*dict\(")

def write_weapon(kind, inner, changed, table='WEAPONS'):
    """A WEAPONS (or AIMED) entry's dict(...) arguments with the walk module's values, the file's order kept."""
    call = ast.parse('dict(%s)' % inner, mode='eval').body
    old = {kw.arg: literal(kw.value) for kw in call.keywords}
    new = getattr(walk, table)[kind]
    def alike(u, v):
        if u is None or v is None:
            return u is v
        if isinstance(u, dict) or isinstance(v, dict):
            return isinstance(u, dict) and isinstance(v, dict) and u.keys() == v.keys() and all(alike(u[k], v[k]) for k in u)
        if isinstance(u, str) or isinstance(v, str):
            return u == v
        return same(list(u) if isinstance(u, tuple) else [u], list(v) if isinstance(v, tuple) else [v])
    if old.keys() == new.keys() and all(alike(old[k], new[k]) for k in old):
        return inner
    changed.append('%s %s' % (table, kind))
    order = [k for k in old if k in new] + [k for k in new if k not in old]
    return ', '.join('%s=%s' % (k, value_text(new[k])) for k in order)

def literal(node):
    """A value in walk.py's tables: a literal, or dict(KEY=value, ...) (a 'body')."""
    if isinstance(node, ast.Call) and getattr(node.func, 'id', None) == 'dict' and not node.args:
        return {kw.arg: literal(kw.value) for kw in node.keywords}
    return ast.literal_eval(node)

def value_text(v):
    """A value as walk.py's tables write it."""
    if isinstance(v, dict):
        return 'dict(%s)' % ', '.join('%s=%s' % (k, value_text(x)) for k, x in v.items())
    return repr(v) if isinstance(v, (str, bool)) or v is None else fmt_tuple(v) if isinstance(v, tuple) else fmt(v)

def write_aimed(src, changed):
    """walk.py with the AIMED entries the aim handles and sliders changed written in, in place, and a weapon that had
    none added at the end of the table."""
    aimed = getattr(walk, 'AIMED', {})
    found = set()
    for kind, key, open_at, close_at in reversed(entries(src, 'AIMED', WEAPON_IN)):
        found.add(kind)
        if isinstance(aimed.get(kind), dict):
            src = src[:open_at + 1] + write_weapon(kind, src[open_at + 1:close_at], changed, 'AIMED') + src[close_at:]
    added = [k for k, v in aimed.items() if isinstance(v, dict) and v and k not in found]
    m = re.search(r'^AIMED\s*=\s*\{', src, re.M)
    if added and m is not None:
        close = match_close(src, m.end() - 1)
        lines = ''.join("    '%s': dict(%s),\n" % (k, ', '.join('%s=%s' % (n, value_text(v)) for n, v in aimed[k].items()))
                        for k in added)
        src = src[:close] + lines + src[close:]
        changed += ['AIMED %s' % k for k in added]
    return src

def write_gear(src, changed):
    """walk.py with the GEAR table rewritten from the walk module (one mount a line), if it changed."""
    m = re.search(r'^GEAR\s*=\s*\{', src, re.M)
    if m is None or not getattr(walk, 'GEAR', None):
        return src
    close = match_close(src, m.end() - 1)
    def text(v):
        return "'%s'" % v if isinstance(v, str) else '(%s)' % ', '.join(fmt(x) for x in v) if isinstance(v, (tuple, list)) else fmt(v)
    lines = ''.join("    '%s': dict(%s),\n" % (mount, ', '.join('%s=%s' % (k, text(g[k])) for k in ('bone', 'at', 'out', 'turn', 'scale', 'depth') if k in g))
                    for mount, g in walk.GEAR.items())
    if src[m.end():close] == '\n' + lines:
        return src
    changed.append('GEAR')
    return src[:m.end()] + '\n' + lines + src[close:]

def write_table(src, name, changed):
    """walk.py with the table name (WEAPON_TYPES, TYPE_SCALE, TYPE_HOLSTER) rewritten whole from the walk module, one
    entry a line, if it changed."""
    table = getattr(walk, name, None)
    m = re.search(r'^%s\s*=\s*\{' % name, src, re.M)
    if table is None or m is None:
        return src
    close = match_close(src, m.end() - 1)
    old = ast.literal_eval(src[m.end() - 1:close + 1])
    if old.keys() == table.keys() and all(same(listed(old[k]), listed(table[k])) if not isinstance(old[k], str)
                                          else old[k] == table[k] for k in old):
        return src
    changed.append(name)
    lines = ''.join("    '%s': %s,\n" % (k, value_text(v)) for k, v in table.items())
    return src[:m.end()] + '\n' + lines + src[close:]

# A weapon's hold sliders that Copy takes: not its sprite, draw size or angle offset, which are the def's own.
WEAPON_COPIED = ('hand', 'line', 'aim', 'at', 'drop', 'behind', 'roll')

def copy_quietly(p, src, dst):
    """One setting copied without its update (repose follows): an enum by its item's number, an array as a tuple."""
    rna = p.bl_rna.properties[dst]
    v = getattr(p, src)
    if rna.type == 'ENUM':
        p[dst] = rna.enum_items[v].value
    elif getattr(rna, 'array_length', 0):
        p[dst] = tuple(v)
    else:
        p[dst] = v

class MARU_OT_copy(bpy.types.Operator):
    bl_idname = 'maru.stance_copy'
    bl_label = 'Copy'
    bl_description = ("Copy the picked drafted stance onto the one shown: its hands, turns and body, and its weapon's hold "
                      "(not the sprite). Write to walk.py to keep it")

    def execute(self, context):
        p = props()
        what, kind = clip_of(p)
        src = p.copy_from
        if what != 'drafted' or src == kind:
            return {'CANCELLED'}
        info = registered['stances'][('DRAFTED', kind)]
        names = []
        for side in info['sides']:
            names += [side, side + '_on', side + '_turn', side + '_turn_on', side + '_elbow']
        for k in info['body']:
            names += ['b_' + k, 'b_' + k + '_on']
        for n in names:
            if hasattr(p, sp('DRAFTED', src, n)):
                copy_quietly(p, sp('DRAFTED', src, n), sp('DRAFTED', kind, n))
        weapons = getattr(walk, 'WEAPONS', {})
        if kind in weapons and src in weapons:
            for k in WEAPON_COPIED:
                copy_quietly(p, 'wp_%s_%s' % (src, k), 'wp_%s_%s' % (kind, k))
            # The hold's own keys follow the source's, so one that leaves 'aim' or 'at' out doesn't keep the old.
            w = dict(weapons[kind])
            for k in ('aim', 'at'):
                w.pop(k, None)
                if k in weapons[src]:
                    w[k] = weapons[src][k]
            weapons[kind] = w
        repose()
        self.report({'INFO'}, 'drafted_%s copied onto drafted_%s (not yet written to walk.py)' % (src, kind))
        return {'FINISHED'}

class MARU_OT_aim_turn_reset(bpy.types.Operator):
    bl_idname = 'maru.aim_turn_reset'
    bl_label = 'Free the hand\'s turn'
    bl_description = ("Drop the other hand's set turn (AIMED 'other_turn') on the aim shown: it closes round the weapon "
                      "again by 'wrap' (a bow's string hand hangs in line with its forearm)")

    def execute(self, context):
        p = props()
        what, kind = clip_of(p)
        if what != 'aim':
            return {'CANCELLED'}
        name = kind.partition('/')[0]
        own = walk.AIMED.get(name)
        if isinstance(own, dict):
            own.pop('other_turn', None)
        if walk.aimed_entry(name).get('other_turn') is not None:
            own = aimed_own(name)
            own['other_turn'] = None   # its kind sets one: this weapon goes without
        repose()
        return {'FINISHED'}

def aimed_on_file(name):
    """An AIMED entry as walk.py has it on file."""
    with open(WALK_PY) as f:
        src = f.read()
    for kind, _, open_at, close_at in entries(src, 'AIMED', WEAPON_IN):
        if kind == name:
            call = ast.parse('dict(%s)' % src[open_at + 1:close_at], mode='eval').body
            return {kw.arg: literal(kw.value) for kw in call.keywords}
    return {}

class MARU_OT_aim_numbers_reset(bpy.types.Operator):
    bl_idname = 'maru.aim_numbers_reset'
    bl_label = 'Reset sliders'
    bl_description = ("Put the sliders above back on the aim shown: a weapon drops its own values and takes its kind's, "
                      "a kind (which every weapon of it builds on) goes back to what walk.py has. "
                      "The hands stay where their handles put them. Write to walk.py to keep it")

    def execute(self, context):
        p = props()
        what, kind = clip_of(p)
        if what != 'aim':
            return {'CANCELLED'}
        name = kind.partition('/')[0]
        own = walk.AIMED.get(name)
        if not isinstance(own, dict):
            return {'CANCELLED'}
        written = aimed_on_file(name) if walk.aimed_kind(name) == name else {}
        for table, base, keys in ((own, written, [k for k, _, _ in AIM_NUMBERS if not k.isupper()]),
                                  (own.get('body', {}), written.get('body', {}), [k for k, _, _ in AIM_NUMBERS if k.isupper()])):
            for key in keys:
                table.pop(key, None)
                if key in base:
                    table[key] = base[key]
        if not own.get('body', True):
            del own['body']
        repose()
        return {'FINISHED'}

class MARU_OT_holster_mirror(bpy.types.Operator):
    bl_idname = 'maru.holster_mirror'
    bl_label = 'Mirror holster'
    bl_description = ("Set this slot from the other one, mirrored across the figure's middle. "
                      "A slot for handguns is also spun about the weapon's line so the far hand can reach across to draw")
    source: bpy.props.StringProperty()

    def execute(self, context):
        p = props()
        slot = p.holster_slot
        h = walk.HOLSTERS.get(self.source)
        if h is None or slot not in walk.HOLSTERS:
            return {'CANCELLED'}
        spin = HOLSTER_MIRROR_SPIN.get(h['size'], 0.0)
        pre = 'hs_%s_' % slot
        p[pre + 'at'], p[pre + 'aim'], p[pre + 'roll'] = mirrored_place(h['at'], h['aim'], h['roll'], spin)
        at, aim, roll = walk.holster_place(self.source, True)
        p[pre + 'melee_at'], p[pre + 'melee_aim'], p[pre + 'melee_roll'] = mirrored_place(at, aim, roll, 0.0)   # a blade goes back on both hips
        p[pre + 'melee_on'] = getattr(p, 'hs_%s_melee_on' % self.source)
        p[pre + 'out'] = tuple(across(h.get('out', (0.0, 1.0, 0.0))))   # the body's way out there, however the weapon is spun
        p[pre + 'behind'] = h.get('behind', 0.0)
        bone = h['bone']
        other = bone[:-1] + {'L': 'R', 'R': 'L'}[bone[-1]] if bone[-2:] in ('.L', '.R') else bone
        setattr(p, pre + 'bone', other)   # poses the rig
        repose()
        return {'FINISHED'}

class MARU_OT_write(bpy.types.Operator):
    bl_idname = 'maru.gait_write'
    bl_label = 'Write to walk.py'
    bl_description = 'Put the slider values back into walk.py (only the numbers that changed)'

    def execute(self, context):
        p = props()
        push_settings(p)
        names, run, _ = tuned()
        with open(WALK_PY) as f:
            src = f.read()
        changed = []
        def top(m):
            name = m.group(1)
            if name in NOT_TUNED or not hasattr(p, 'walk_' + name):
                return m.group(0)
            v = getattr(p, 'walk_' + name)
            if same([v], [float(m.group(3))]):
                return m.group(0)
            changed.append(name)
            new = fmt(v)
            return m.group(1) + m.group(2) + new + realign(m.group(3), new, m.group(4))
        src = NUMBER.sub(top, src)
        def place(m):
            name = m.group(1)
            if name in NOT_TUNED or not hasattr(p, 'tup_' + name):
                return m.group(0)
            v = tuple(getattr(p, 'tup_' + name))
            if same(numbers(m.group(3)), v):
                return m.group(0)
            changed.append(name)
            was, new = '(%s)' % m.group(3), fmt_tuple(v)
            return m.group(1) + m.group(2) + new + realign(was, new, m.group(4))
        src = TUPLE.sub(place, src)
        start = src.index('RUN = dict(')
        end = src.index(')', start)
        def in_run(m):
            v = getattr(p, 'run_' + m.group(1))
            if same([v], [float(m.group(2))]):
                return m.group(0)
            changed.append('RUN ' + m.group(1))
            return '%s=%s' % (m.group(1), fmt(v))
        block = re.sub(r'\b([A-Z][A-Z_0-9]*)=(-?\d+\.?\d*)', lambda m: in_run(m) if m.group(1) in run else m.group(0),
                       src[start:end])
        src = src[:start] + block + src[end:]
        for table in STANCES:
            for kind, key, open_at, close_at in reversed(entries(src, table)):   # from the end, so earlier places hold
                if kind not in getattr(walk, table):
                    continue
                text, hoisted = write_entry(table, kind, src[open_at:close_at + 1], changed)
                indent = src[src.rfind('\n', 0, key) + 1:key]
                src = src[:key] + ''.join(c + '\n' + indent for c in hoisted) + src[key:open_at] + text + src[close_at + 1:]
        for kind, key, open_at, close_at in reversed(entries(src, 'WEAPONS', WEAPON_IN)):
            if kind in getattr(walk, 'WEAPONS', {}):
                src = src[:open_at + 1] + write_weapon(kind, src[open_at + 1:close_at], changed) + src[close_at:]
        src = write_aimed(src, changed)
        for slot, key, open_at, close_at in reversed(entries(src, 'HOLSTERS', WEAPON_IN)):
            if slot in getattr(walk, 'HOLSTERS', {}):
                src = src[:open_at + 1] + write_weapon(slot, src[open_at + 1:close_at], changed, 'HOLSTERS') + src[close_at:]
        for name in ('WEAPON_TYPES', 'TYPE_SCALE', 'TYPE_HOLSTER'):
            src = write_table(src, name, changed)
        src = write_gear(src, changed)
        with open(WALK_PY, 'w') as f:
            f.write(src)
        reload_walk()
        self.report({'INFO'}, 'walk.py: ' + (', '.join(changed) if changed else 'nothing changed'))
        return {'FINISHED'}

class MARU_OT_reload(bpy.types.Operator):
    bl_idname = 'maru.gait_reload'
    bl_label = 'Reload walk.py'
    bl_description = 'Read walk.py again (its numbers and its code), dropping slider changes not yet written'

    def execute(self, context):
        reload_walk()
        return {'FINISHED'}

class MARU_OT_rest(bpy.types.Operator):
    bl_idname = 'maru.gait_rest'
    bl_label = 'Rest pose'
    bl_description = 'Stop and put the rig back in its rest pose'

    def execute(self, context):
        props().playing = False
        for pb in walk.arm.pose.bones:
            pb.location = (0, 0, 0)
            pb.rotation_quaternion = (1, 0, 0, 0)
            pb.rotation_euler = (0, 0, 0)
        walk.arm.update_tag()
        return {'FINISHED'}

class MARU_OT_grab(bpy.types.Operator):
    bl_idname = 'maru.gait_grab'
    bl_label = 'Select handle'
    bl_description = "Select this handle (in Object Mode): G moves it, R turns it"
    target: bpy.props.StringProperty()

    def execute(self, context):
        obj = bpy.data.objects.get(self.target)
        if obj is None:
            return {'CANCELLED'}
        props().playing = False
        if context.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        for o in context.selected_objects:
            o.select_set(False)
        obj.select_set(True)
        context.view_layer.objects.active = obj
        return {'FINISHED'}

class MARU_OT_view(bpy.types.Operator):
    bl_idname = 'maru.gait_view'
    bl_label = 'Game view'
    bl_description = "Look at the figure as IsoRim's isometric camera does (orthographic, from above at 35.3 degrees)"

    def execute(self, context):
        r3d = context.space_data.region_3d if context.space_data.type == 'VIEW_3D' else None
        if r3d is None:
            return {'CANCELLED'}
        rig = walk.arm
        # Blender's front view (looking along +y at a figure facing -y) is a 90 degree tilt; tip it down to the game's
        # pitch and turn it round the figure. turn steps the figure's heading, which reads as the camera going the other way.
        r3d.view_rotation = Euler((math.radians(90 - ISO_PITCH), 0, math.radians(ISO_YAW - 45 * props().turn))).to_quaternion()
        r3d.view_perspective = 'ORTHO'
        r3d.view_location = rig.matrix_world @ walk.head[walk.HIPS]
        r3d.view_distance = walk.LEG * rig.matrix_world.to_scale().y * 4
        return {'FINISHED'}

class MARU_PT_gait(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Pose'
    bl_label = "Maru's pose preview"

    def draw(self, context):
        p = props()
        names, run, sections = tuned()
        what, kind = clip_of(p)
        seconds = loop_seconds(what, kind)
        over = what in OVERS and p.over
        col = self.layout.column(align=True)
        col.prop(p, 'category', text='')
        col.prop(p, 'clip_in', text='')
        if what in OVERS:
            col.prop(p, 'over')
        if clip_variants(what, kind):
            col.prop(p, 'variant', text='Variant (of %d)' % (len(clip_variants(what, kind)) + 1))
        if what == 'gait' or over:
            col.prop(p, 'gait', slider=True)
        row = col.row(align=True)
        if seconds:
            row.prop(p, 'time', slider=True)
        if what == 'gait' or what in CYCLES or over:
            row.prop(p, 'phase', slider=True)
        if what != 'still':
            row.prop(p, 'playing', text='', icon='PAUSE' if p.playing else 'PLAY')
        if what == 'gait' or what in CYCLES or over:
            col.prop(p, 'speed')
        row = self.layout.row(align=True)
        row.operator('maru.gait_view')
        row.prop(p, 'turn')
        self.layout.operator('maru.gait_rest')

        box = self.layout.box()
        if what == 'gait' or over:
            box.label(text='Ground per cycle: %.3f leg lengths' % (walk.STRIDE / walk.STANCE))
        if what in GROUND:
            box.label(text='Ground per cycle: %.3f leg lengths' % (GROUND[what]() / walk.LEG))
        if seconds:
            box.label(text='Loop: %.1f game seconds, %d frames' % (seconds, walk.STRIKE_FRAMES if what == 'strike'
                                                                    else walk.loop_frames(seconds)))
        if state['clamp'] > 1e-4:
            box.label(text='Overreach: %.3f leg lengths' % (state['clamp'] / walk.LEG), icon='ERROR')
        if state['error']:
            box.label(text=state['error'], icon='ERROR')

        row = self.layout.row(align=True)
        row.operator('maru.gait_write', icon='FILE_TICK')
        row.operator('maru.gait_reload', icon='FILE_REFRESH')

        if what in ('drafted', 'carry'):
            self.draw_stance(p, 'DRAFTED' if what == 'drafted' else 'CARRY', kind)
        if what == 'carry':
            self.draw_load(p, kind)
        if what == 'drafted' and weapon_name(p, what, kind) in getattr(walk, 'WEAPONS', {}):
            self.draw_weapon(p, weapon_name(p, what, kind))
        if what == 'aim':
            self.draw_aim(p, kind)
        if what == 'aim' and kind.partition('/')[0] in getattr(walk, 'WEAPONS', {}):
            self.draw_weapon(p, kind.partition('/')[0], hold=False)
        if what == 'strike' and walk.strike_names()[kind][2] is not None:
            self.draw_weapon(p, walk.strike_names()[kind][2], hold=False)
        if what in ('draw', 'holster'):
            g = walk.draw_keys(kind)
            box = self.layout.box()
            box.label(text='Holding: %s, %s hand' % (g['guard'], 'left' if g['side'] == 'L' else 'right'))
            box.label(text='Trunk: twist %.0f, lean %.0f, side %.0f; shoulder %.0f' % (g['body']['twist'], g['body']['lean'], g['body']['side'], g['shoulder']))
            if g['gap'] > 1e-3:
                box.label(text='Handle %.3f leg lengths out of reach: the weapon swings to the hand' % g['gap'], icon='INFO')
            self.draw_weapon(p, g['guard'], hold=False)
        self.draw_worn(p)
        if getattr(walk, 'HOLSTERS', None):
            self.draw_holsters(p)
        if getattr(walk, 'GEAR', None):
            self.draw_gear(p)

        tuples = {n for n, _, t in names if t}
        for i, (title, members) in enumerate(sections):
            box = self.layout.box()
            shown = getattr(p, 'show_%d' % i)
            box.prop(p, 'show_%d' % i, text=title, icon='TRIA_DOWN' if shown else 'TRIA_RIGHT', emboss=False)
            if not shown:
                continue
            two = any(n in run for n in members)
            col = box.column(align=True)
            if two:
                split = col.split(factor=0.4, align=True)
                split.label(text='')
                split.label(text='Walk')
                split.label(text='Run')
            for name in members:
                split = col.split(factor=0.4, align=True)
                split.label(text=name)
                if name in tuples:
                    split.row(align=True).prop(p, 'tup_' + name, text='')
                    continue
                split.prop(p, 'walk_' + name, text='')
                if name in run:
                    split.prop(p, 'run_' + name, text='')
                elif two:
                    split.label(text='(both)')

    def draw_aim(self, p, kind):
        name, _, stage = kind.partition('/')
        box = self.layout.box()
        own = walk.AIMED.get(name)
        box.label(text='AIMED %s%s' % (name, '' if name in ('rifle', 'pistol', 'heavy', 'bow') else
                                       ' (as %s%s)' % (walk.aimed_kind(name), '' if own else ', nothing of its own yet')))
        bow = 'draw' in (walk.aimed_entry(name) or {})
        col = box.column(align=True)
        for key, label, _ in AIM_NUMBERS:
            col.prop(p, 'aim_' + key, text='String elbow lift' if bow and key == 'other_elbow' else label)
        box.operator('maru.aim_numbers_reset', icon='LOOP_BACK',
                     text='Reset sliders to walk.py' if walk.aimed_kind(name) == name else 'Reset sliders to %s' % walk.aimed_kind(name))
        if p.over:
            box.label(text='Untick Over the gait for the hand handles', icon='INFO')
            return
        holding = walk.aim_last.get('side', 'R')
        other = 'L' if holding == 'R' else 'R'
        row = box.row(align=True)
        row.operator('maru.gait_grab', text='Weapon hand', icon='EMPTY_ARROWS').target = HANDLE + holding
        row.operator('maru.gait_grab', text='String hand' if bow else 'Other hand', icon='EMPTY_ARROWS').target = HANDLE + other
        box.label(text='Weapon hand: G moves the weapon (its fist)')
        if bow:
            box.label(text='String hand: G sets the %s, R its turn' % {'fire': 'loose', 'nock': 'nock'}.get(stage, 'full draw'))
        else:
            box.label(text='Other hand: G slides it along and under the weapon,')
            box.label(text='R turns it freely (held so against the weapon)')
        entry = walk.aimed_entry(name) or {}
        if entry.get('other_turn') is not None:
            box.operator('maru.aim_turn_reset', icon='LOOP_BACK')
        box.label(text='Set at any Time (pitch); written as the level aim')

    def draw_worn(self, p):
        box = self.layout.box()
        row = box.row()
        row.label(text='WORN (what holsters and packs push past)')
        row.prop(p, 'worn_wire', text='', icon='SHADING_WIRE')
        row.prop(p, 'worn_show', text='', icon='HIDE_OFF' if p.worn_show else 'HIDE_ON', emboss=False)
        if not p.worn_show:
            return
        col = box.column(align=True)
        for part in walk.body_parts():
            keys = part.data.shape_keys
            for key in (keys.key_blocks[1:] if keys else ()):
                col.prop(key, 'value', text=key.name, slider=True)
        grid = box.grid_flow(columns=2, even_columns=True, align=True)
        for g in garment_names():
            grid.prop(p, worn_prop(g), toggle=True)

    def draw_gear(self, p):
        box = self.layout.box()
        row = box.row()
        row.label(text='WORN GEAR (IsoRim packs and belts)')
        row.prop(p, 'gear_show', text='', icon='HIDE_OFF' if p.gear_show else 'HIDE_ON', emboss=False)
        if not p.gear_show:
            return
        box.row(align=True).prop(p, 'gear_mount', expand=True)
        mount = p.gear_mount
        if mount not in walk.GEAR:
            return
        pre = 'gr_%s_' % mount
        col = box.column(align=True)
        col.prop(p, pre + 'on')
        col.prop(p, pre + 'model')
        col.prop(p, pre + 'bone')
        col.label(text='At rest, model units from the hips (x left, y back, z up)')
        col.prop(p, pre + 'at', text='')
        col.prop(p, pre + 'scale')
        if mount == 'belt':
            col.prop(p, pre + 'depth')
        col.label(text='Out of the body (pushed past what is worn)')
        col.prop(p, pre + 'out', text='')
        col.label(text='Turned at rest, degrees about x, y, z')
        col.prop(p, pre + 'turn', text='')
        box.operator('maru.gait_grab', text='Select gear handle', icon='EMPTY_ARROWS').target = GEAR_GRIP

    def draw_holsters(self, p):
        box = self.layout.box()
        row = box.row()
        row.label(text='HOLSTERS (weapons not held)')
        row.prop(p, 'holsters', text='', icon='HIDE_OFF' if p.holsters else 'HIDE_ON', emboss=False)
        if not p.holsters:
            return
        box.prop(p, 'holster_slot', text='')
        slot = p.holster_slot
        if slot not in walk.HOLSTERS:
            return
        pre = 'hs_%s_' % slot
        if 'pack' in walk.HOLSTERS[slot] or 'belt' in walk.HOLSTERS[slot]:
            if 'pack' in walk.HOLSTERS[slot]:
                box.label(text='Used with a backpack and an equipment pack on' if walk.HOLSTERS[slot]['pack'] else 'Not used with a backpack and an equipment pack on', icon='INFO')
            if 'belt' in walk.HOLSTERS[slot]:
                box.label(text='Used with a belt on' if walk.HOLSTERS[slot]['belt'] else 'Not used with a belt on', icon='INFO')
            fig = figure()
            if not slot_used(fig, worn_now(p, fig), slot):
                box.label(text='Not in use with what is worn', icon='ERROR')
        col = box.column(align=True)
        col.row(align=True).prop(p, pre + 'size', expand=True)
        col.prop(p, pre + 'bone')
        col = box.column(align=True)
        sub = col.column(align=True)
        sub.label(text='At rest, model units from the hips (x left, y back, z up)')
        sub.prop(p, pre + 'at', text='')
        sub.label(text='Muzzle way')
        sub.prop(p, pre + 'aim', text='')
        sub.prop(p, pre + 'roll')
        col = box.column(align=True)
        col.prop(p, pre + 'melee_on')
        sub = col.column(align=True)
        sub.enabled = getattr(p, pre + 'melee_on')
        sub.label(text='A melee weapon here (blade well round from the aim)')
        sub.prop(p, pre + 'melee_at', text='')
        sub.prop(p, pre + 'melee_aim', text='')
        sub.prop(p, pre + 'melee_roll')
        col = box.column(align=True)
        col.prop(p, pre + 'behind')
        col.label(text='Out of the body (pushed past what is worn)')
        col.prop(p, pre + 'out', text='')
        box.operator('maru.gait_grab', text='Select holster handle', icon='EMPTY_ARROWS').target = HOLSTER_GRIP
        for other, h in walk.HOLSTERS.items():
            if other != slot and h['size'] == getattr(p, pre + 'size'):
                box.operator('maru.holster_mirror', text='Mirror from %s' % other, icon='MOD_MIRROR').source = other
        col = box.column(align=True)
        col.prop(p, pre + 'filled')
        col.prop(p, pre + 'weapon')
        weapon = getattr(p, pre + 'weapon')
        if hasattr(p, 'wt_%s' % weapon):
            self.draw_type(p, weapon, col)
            size = getattr(p, 'th_%s' % getattr(p, 'wt_%s' % weapon))
            if size != getattr(p, pre + 'size'):
                col.label(text='%s goes in %s slots, not this one' % (weapon, size), icon='ERROR')

    def draw_type(self, p, kind, col):
        """A weapon's type, and that type's size and holster slots (shared by every weapon of the type)."""
        t = getattr(p, 'wt_%s' % kind)
        col.prop(p, 'wt_%s' % kind)
        row = col.row(align=True)
        row.prop(p, 'ts_%s' % t, text='%s size' % TYPE_LABELS.get(t, t))
        row.prop(p, 'th_%s' % t, text='')

    def draw_load(self, p, kind):
        box = self.layout.box()
        row = box.row()
        pet = kind in walk.CARRY and walk.CARRY[kind].get('pet')
        row.label(text='Carried item' if kind == 'item' else "Cuddled pet (IsoRim's cat)" if pet
                  else 'Carried person (carried_%s)' % kind)
        row.prop(p, 'load', text='', icon='HIDE_OFF' if p.load else 'HIDE_ON', emboss=False)
        if pet:
            col = box.column(align=True)
            col.label(text='Right hand: G moves the palm on its back, R turns it')
            col.label(text='(STROKE_REST, STROKE_FINGERS, STROKE_PALM)')
            if bpy.data.objects.get(HANDLE + 'R') is not None:
                col.operator('maru.gait_grab', text='Select right hand handle', icon='EMPTY_ARROWS').target = HANDLE + 'R'
            elif p.over:
                col.label(text='(no handles laid over the gait: untick Over the gait)')
            if 'pet_back' in state:
                # The game lays the right hand on the back of the animal it draws (WalkFigures.StrokePet), not PET_BACK.
                col.label(text="Cat's back %.3f over the forearm, PET_BACK %.3f: the game moves the hand %+.3f"
                          % (state['pet_back'], walk.PET_BACK, state['pet_back'] - walk.PET_BACK))
        if kind == 'item' and p.load:
            col = box.column(align=True)
            col.prop(p, 'load_size')
            col.prop(p, 'load_thick')

    def draw_weapon(self, p, kind, hold=True):
        """The weapon's box: its hold (hold: on a drafted clip; an aim clip turns the hand from the hold, so its numbers are
        set there) and the sprite it is previewed with."""
        pre = 'wp_%s_' % kind
        box = self.layout.box()
        row = box.row()
        row.label(text='WEAPONS %s' % kind)
        row.prop(p, 'weapon', text='', icon='HIDE_OFF' if p.weapon else 'HIDE_ON', emboss=False)
        if hold:
            self.draw_hold(p, kind, box)
        col = box.column(align=True)
        col.label(text='Sprite (preview only: the game uses the def\'s)')
        col.prop(p, pre + 'image', text='')
        col.prop(p, pre + 'size', text='')
        col.prop(p, pre + 'offset')
        rel = isorim_models().get(KIND_LOOK.get(kind, kind))
        row = box.row()
        row.prop(p, 'weapon_models')
        held = bpy.data.objects.get(SLAB + MODEL)
        if rel is None:
            row.label(text='(none for this weapon)')
        elif held is not None and not held.hide_viewport and held.get('model') == rel:
            row.label(text='%s, fit %.2f' % (os.path.basename(rel), held.get('fit', 0.0)))
        box.label(text='Textured in Material Preview, or Solid with Texture colour', icon='INFO')

    def draw_hold(self, p, kind, box):
        pre = 'wp_%s_' % kind
        col = box.column(align=True)
        col.row(align=True).prop(p, pre + 'hand', expand=True)
        col.prop(p, pre + 'line', text='')
        if getattr(p, pre + 'line') in ('aim', 'hand'):
            if getattr(p, pre + 'line') == 'hand':
                col.label(text='Aim with the hand at rest (turn the hand to aim)')
            col.prop(p, pre + 'aim', text='')
            if getattr(p, pre + 'line') == 'hand':
                col.label(text='Grip moved in the hand (model units)')
                col.prop(p, pre + 'at', text='')
                if p.weapon and not p.over:
                    col.operator('maru.gait_grab', text='Select weapon grip', icon='EMPTY_ARROWS').target = GRIP
        col.prop(p, pre + 'drop')
        col.prop(p, pre + 'behind')
        col.prop(p, pre + 'roll')
        if hasattr(p, 'wt_%s' % kind):
            self.draw_type(p, kind, col)

    def draw_stance(self, p, table, kind):
        info = registered['stances'].get((table, kind))
        if info is None:
            return
        box = self.layout.box()
        box.label(text='%s %s' % (table, kind))
        if table == 'DRAFTED':
            row = box.row(align=True)
            row.prop(p, 'copy_from', text='')
            row.operator('maru.stance_copy', icon='COPYDOWN')
            if hasattr(walk, 'shield_weapon') and walk.shield_weapon(kind) is not None:
                box.prop(p, 'shield_with')
                if drafted_pose_name(p, kind) != kind:
                    box.label(text='Worked out from this stance with its own grip: no handles', icon='INFO')
        if info['sides']:
            box.label(text='Wrists, leg lengths from the hips: x left, y back, z up')
            box.label(text='Turns (ticked: held at that turn), degrees in the chest\'s frame')
        for side, label in SIDES:
            if side not in info['sides']:
                continue
            col = box.column(align=True)
            held = not info['toggle'] or getattr(p, sp(table, kind, side + '_on'))
            if info['toggle']:
                col.prop(p, sp(table, kind, side + '_on'), text=label + ('' if held else ' (free)'))
            else:
                col.label(text=label)
            row = col.row(align=True)
            row.enabled = held
            row.prop(p, sp(table, kind, side), text='')
            row = col.row(align=True)
            row.enabled = held
            turned = getattr(p, sp(table, kind, side + '_turn_on'))
            row.prop(p, sp(table, kind, side + '_turn_on'), text='', icon='ORIENTATION_GIMBAL')
            sub = row.row(align=True)
            sub.enabled = turned
            sub.prop(p, sp(table, kind, side + '_turn'), text='')
            row = col.row(align=True)
            row.enabled = held
            row.prop(p, sp(table, kind, side + '_elbow'))
            if held and not p.over:
                col.operator('maru.gait_grab', icon='EMPTY_ARROWS').target = HANDLE + side
        col = box.column(align=True)
        col.label(text='Body (ticked: set by this stance)')
        for k in info['body']:
            row = col.row(align=True)
            split = row.split(factor=0.4, align=True)
            split.prop(p, sp(table, kind, 'b_' + k + '_on'), text=k)
            sub = split.row(align=True)
            sub.enabled = getattr(p, sp(table, kind, 'b_' + k + '_on'))
            sub.prop(p, sp(table, kind, 'b_' + k), text='')

CLASSES = (MARU_OT_copy, MARU_OT_aim_turn_reset, MARU_OT_aim_numbers_reset, MARU_OT_holster_mirror, MARU_OT_write, MARU_OT_reload, MARU_OT_rest, MARU_OT_grab, MARU_OT_view, MARU_PT_gait)

def reload_walk():
    global walk
    walk = importlib.reload(walk)
    importlib.reload(holster_push)
    registered.pop('figure', None)
    keep = props()
    playing, gait, phase, time, over, clip = keep.playing, keep.gait, keep.phase, keep.time, keep.over, keep.clip
    shows = {title: getattr(keep, 'show_%d' % i) for i, (title, _) in enumerate(tuned()[2])}
    looks = {n: getattr(keep, n) for n in keep.bl_rna.properties.keys()
             if n.startswith('wp_') and n.endswith(('_image', '_size', '_offset'))}
    # Copied now: an array read off the old settings is gone once swap_props has taken them away.
    looks = {n: v if isinstance(v, (str, bool, float)) else tuple(v) for n, v in looks.items()}
    looks['weapon'], looks['weapon_models'] = keep.weapon, keep.weapon_models
    looks['load'], looks['load_size'], looks['load_thick'] = keep.load, tuple(keep.load_size), keep.load_thick
    state.pop('carried_key', None)   # walk.py's numbers may have changed
    looks['copy_from'] = keep.copy_from
    looks['holsters'] = keep.holsters
    looks['gear_show'] = keep.gear_show
    looks['worn_show'], looks['worn_wire'] = keep.worn_show, keep.worn_wire
    looks.update({n: getattr(keep, n) for n in keep.bl_rna.properties.keys() if n.startswith('wn_') or (n.startswith('hs_') and n.endswith('_filled'))})
    looks.update({n: getattr(keep, n) for n in keep.bl_rna.properties.keys() if n.startswith('gr_') and n.endswith(('_model', '_on'))})
    looks.update({n: getattr(keep, n) for n in keep.bl_rna.properties.keys() if n.startswith('hs_') and n.endswith('_weapon')})
    slot = keep.holster_slot
    mount = keep.gear_mount
    registered.pop('types_listed', None)
    keep.playing = False
    swap_props()
    p = props()
    p.gait, p.phase, p.time, p.over = gait, phase, time, over
    if clip in [c for c, _, _ in clip_items()]:
        p['clip'] = [c for c, _, _ in clip_items()].index(clip)   # quietly: clip_changed would open its paragraph again
    sync_menus(p)
    for i, (title, _) in enumerate(tuned()[2]):
        setattr(p, 'show_%d' % i, shows.get(title, False))
    if slot in getattr(walk, 'HOLSTERS', {}):
        p.holster_slot = slot   # first, as picking one changes what is worn
    copy_from = looks.pop('copy_from')
    if copy_from in walk.DRAFTED:
        p.copy_from = copy_from
    for n, v in looks.items():
        if n.endswith('_weapon') and n.startswith('hs_'):
            if hasattr(p, n) and v in getattr(walk, 'WEAPONS', {}):
                setattr(p, n, v)
        elif hasattr(p, n):
            p[n] = v   # quietly: repose below
    if mount in getattr(walk, 'GEAR', {}):
        p.gear_mount = mount
    p.playing = playing
    repose()

def swap_props():
    old = registered.get('props')
    if old is not None:
        del bpy.types.WindowManager.maru_gait
        bpy.utils.unregister_class(old)
    registered['names'] = read_tuned()
    registered['stances'] = read_stances()
    registered['props'] = build_props()
    bpy.utils.register_class(registered['props'])
    bpy.types.WindowManager.maru_gait = bpy.props.PointerProperty(type=registered['props'])

def object_mode():
    """As export_frames.py does: a mesh saved in Edit Mode skips its Armature modifier and would stand still in the rest pose."""
    for o in bpy.data.objects:
        if o.type == 'MESH' and o.mode != 'OBJECT' and any(m.type == 'ARMATURE' and m.object == walk.arm for m in o.modifiers):
            bpy.context.view_layer.objects.active = o
            bpy.ops.object.mode_set(mode='OBJECT')

def register():
    object_mode()
    model_scale()
    swap_props()
    for c in CLASSES:
        bpy.utils.register_class(c)
    bpy.app.handlers.save_pre.append(clear_slab)
    bpy.app.handlers.save_post.append(restore_slab)
    bpy.app.handlers.depsgraph_update_post.append(handles_moved)
    repose()

def unregister():
    for handlers, fn in ((bpy.app.handlers.save_pre, clear_slab), (bpy.app.handlers.save_post, restore_slab),
                         (bpy.app.handlers.depsgraph_update_post, handles_moved)):
        for h in [h for h in handlers if getattr(h, '__name__', '') == fn.__name__ and getattr(h, '__module__', '') == fn.__module__]:
            handlers.remove(h)
    clear_slab()
    if registered.get('props') is not None:
        props().playing = False
        del bpy.types.WindowManager.maru_gait
        bpy.utils.unregister_class(registered.pop('props'))
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)

if __name__ == '__main__':
    previous = bpy.app.driver_namespace.get('maru_gait_unregister')   # running the script again replaces the panel
    if previous:
        try:
            previous()
        except Exception:
            pass
    register()
    bpy.app.driver_namespace['maru_gait_unregister'] = unregister
