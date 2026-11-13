# Bakes an in-place walk cycle onto the chibi's metarig (Models/pawnHumanRig.blend) as plain FK keys (action "Walk").
# Legs: 2-bone IK against a planted-foot path, then converted to bone rotations.
import bpy, contextlib, math, sys
from mathutils import Euler, Matrix, Vector, Quaternion

# The rig's spine chain (Rigify metarig names). Bones not driven here follow the one they are listed against.
HIPS, SPINE, CHEST_BONE, NECK, HEAD = 'spine', 'spine.001', 'spine.002', 'spine.004', 'spine.006'
FOLLOW = {'spine.003': CHEST_BONE, 'spine.005': NECK, 'pelvis.L': HIPS, 'pelvis.R': HIPS}
TRUNK = (HIPS, SPINE, CHEST_BONE, 'spine.003', NECK)   # the trunk's joints, hips to neck (mocap_at)

FRAMES = 24          # one full cycle (two steps) at 24 fps
STANCE = 0.6         # share of the cycle each foot is on the ground
# Distances are in leg lengths (thigh + shin), so they suit the rig's own size.
STRIDE = 0.55        # how far a planted foot slides back during stance
LIFT = 0.15          # ankle lift at mid-swing
DROP = 0.035         # hips lowered from rest so the legs can reach
BOB = 0.03           # hip rise and fall, twice a cycle
TWIST = 5.0          # pelvis turn (degrees), chest turns against it
CHEST = 4.0
ROLL = 2.5           # pelvis drops on the swing side
LEAN = 4.0           # forward lean of the spine (the big head tips it further)
ARM = 25.0           # upper arm swing
ARM_DOWN = 40.6      # bring the A-pose arms (rest: upper arm 35 degrees below level) closer to the body
ARM_FWD = 9.0        # turn the arms forward at the shoulder first (the rest pose angles them slightly back)
FOREARM_IN = 6.0     # forearm turned in toward the body at the elbow (the rest pose angles it further out)
HAND_IN = 26.0       # hand turned in at the wrist to hang in line with the forearm (it flares out at rest)
NARROW = 0.6         # share of the way the feet are drawn in from the rig's wide stance to under the hips
ELBOW = 14.0         # base elbow bend, more when the arm is forward
HEEL = 22.0          # heel rise before toe-off
KICK = 0.0           # swinging foot thrown back toward the seat early in the swing
PEAK = 0.5           # share of the swing at which the foot is highest
BOB_AT = 0.06        # cycle phase at which the hips are lowest (and again half a cycle on)

# Running: the values that differ from the walk. Each foot is down for under half the cycle, so both are off the
# ground between steps; longer strides, a deeper lean, the heel kicked up behind and the arms bent and pumping.
# set_gait blends between the two, for the in-between poses the game shows while a pawn speeds up or slows down.
RUN = dict(STANCE=0.3, STRIDE=0.7, LIFT=0.45, KICK=0.35, PEAK=0.4, DROP=0.08, BOB=0.04, BOB_AT=0.15, TWIST=7.0,
           CHEST=7.0, ROLL=2.0, LEAN=20.0, ARM=42.0, ARM_DOWN=40.8, FOREARM_IN=8.0, HAND_IN=30.0, NARROW=0.75,
           ELBOW=60.0, HEEL=30.0)
WALK = {k: globals()[k] for k in RUN}

# Standing still (stand_at): one loop of the idle, sampled at IDLE_FPS (the game blends between frames). The pawn
# breathes BREATHS times a loop and shifts its weight onto one foot, holds it, then onto the other.
IDLE_SECONDS = 12.0
BREATHS = 3          # breaths per idle loop
IDLE_FPS = 12
BREATH = 2.5         # chest tipped back on the in-breath (degrees)
BREATH_SHOULDER = 4.0   # shoulders raised on the in-breath (degrees)
BREATH_BOB = 0.006   # hips lowered on the out-breath (knees give a little)
SWAY = 0.08          # hips moved sideways over the standing foot
SWAY_ROLL = 5.0      # pelvis tilt with it (degrees): the standing side up; the spine rights itself above it
SWAY_HOLD = 2.2      # how long each side is held: 0 swings evenly, more holds the weight on one foot longer

# Drafted (stand_at with a weapon kind): more upright, a little lower and wider on the feet, with the hands held as the
# weapon kind wants (DRAFTED). The game lays its arms, chest, neck and head over whatever the legs are doing (UPPER), so
# the walk and run keep their stride under it.
DRAFT_SECONDS = 4.0  # one breath
DRAFT_LEAN = 1.0     # forward lean of the spine, in place of LEAN
DRAFT_DROP = 0.03    # hips lowered (knees bent ready)
DRAFT_NARROW = 0.35  # in place of NARROW: feet further apart
ELBOW_OUT = 0.3      # how far the elbows point out to the side rather than down
STAGGER = 0.0        # the left foot set forward of the right (a fighting stance), leg lengths between them
HEAD_TILT = 0.0      # the head tipped over sideways about the forward line, + to the left (degrees), the neck taking half:
                     # clears room at the right shoulder for a launcher shouldered there
FOREARM_TWIST = 0.5  # share of a turned hand's twist (a '<side>_turn' below) the forearm takes with it
FOREARM_TWIST_MID = -40.0  # degrees, mirrored for the left hand: the middle of the hand twists the forearm follows. Half a
                           # turn from it the forearm's roll flips over, so it sits far from every hold (now -128 .. 42)
# Each weapon kind's hands, one drafted clip each (drafted_<kind>; the game sorts weapons into these, WalkFigures.KindOf).
# Wrist places are in leg lengths from the middle of the hips at rest: x to the figure's left, y back (forward is -y),
# z up. A side left out hangs free, and keeps the walk's arm swing under the drafted chest. '<side>_turn' (x, y, z degrees,
# turned in that order in the chest's frame from the hand's rest turn) holds that hand at a set turn, wherever the wrist
# is; left out, the hand lines up with the forearm. '<side>_elbow' (degrees) swings that elbow out and up about the line
# from the shoulder to the wrist, the hand staying put, so a hold that brings the arm across the body doesn't sink the
# elbow into the side of the chest; left out, it is where ELBOW_OUT puts it.
# 'body': the stance's own changes to the numbers above (a lower, wider, staggered fighting stance).
DRAFTED = {
    # Collapsed low ready: slung across the chest from the right shoulder down to the left hip, muzzle down and left.
    # on the pistol grip, at the middle of the chest
    'rifle': {'R': (-0.2583, -0.356, 0.4535), 'R_turn': (101.0351, -158.5573, 8.8982), 'R_elbow': 21.0, 'L': (0.1292, -0.2744, 0.1174), 'L_turn': (142.1423, -22.6339, -66.2976)},     # on the handguard, low by the left hip (as far down as the arm reaches)
    # One-handed low ready: the arm nearly straight, down and forward, the muzzle at the ground ahead.
    'pistol': {'R': (-0.0077, -0.4058, 0.2864), 'R_turn': (-66.2829, -24.3397, 150.5612), 'R_elbow': 56.0, 'L': (0.1443, -0.3047, 0.1197), 'L_turn': (-117.9665, -16.0991, -103.3278)},
    'bow': {'L': (0.3216, -0.338, 0.0604), 'L_turn': (-63.6771, 49.9648, -101.7576)},       # the bow held low in front on the left
    # A knife fighter's guard: the weapon hand up by the right shoulder, blade up; the other hand up by the chin.
    'melee': {'R': (-0.2946, -0.329, 0.2319), 'R_turn': (-64.6928, 1.9417, 117.7251), 'R_elbow': 3.0, 'L': (0.2748, -0.3776, 0.3486), 'L_turn': (-28.55, -33.9862, -117.3661), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    # Open hands up in front of the face, the right a little higher and further out.
    'unarmed': {'R': (-0.1667, -0.3951, 0.5415), 'R_turn': (-27.3696, 45.7186, 145.2212), 'L': (0.1937, -0.236, 0.4861), 'L_turn': (-36.5014, -37.3041, -128.3353), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    # With a shield up (Combat Extended's, held in the left fist, walk.shield_at): the one-handed kinds' stances, the
    # weapon hand kept out to the right clear of the shield's back. 'shield': the left arm is the shield guard's
    # (SHIELD_FIST), whatever L says. Each one-handed weapon (ONE_HANDED types) of a kind with one of these gets its own
    # (shield_<defName>, shielded_entry): this one with the hand and weapon moved together as the weapon's own drafted
    # entry has them against its kind's, so it keeps its own grip. The game plays them in place of drafted_<kind> while
    # the shield is up (WalkFigures.DraftedClip); a two-handed weapon keeps its own stance.
    'shield_pistol': {'R': (-0.3108, -0.2677, 0.1639), 'R_turn': (-75.3907, -42.6764, 105.976), 'R_elbow': 56.0, 'shield': True},
    'shield_melee': {'R': (-0.3188, -0.3192, 0.2792), 'R_turn': (-64.6928, 1.9417, 117.7251), 'R_elbow': 3.0, 'shield': True, 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'shield_unarmed': {'R': (-0.2404, -0.435, 0.3911), 'R_turn': (-27.3696, 45.7186, 145.2212), 'shield': True, 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    # Each weapon of the game and its expansions, by defName (drafted_<defName>), to set one by one. 'kind': the entry
    # above it starts from (drafted_entry), taking each hand, turn and body number the weapon doesn't give (None: left free,
    # or a turn left lined up with the forearm). One that gives nothing of its own is its kind's stance, and gets no clip
    # of its own (the game falls back to the kind's). A weapon not listed here (a modded one) takes its kind's; a unique
    # or persona weapon takes the plain one's (WalkFigures.DraftedClip).
    # Two-handed guns (rifle).
    # Core
    'Gun_BoltActionRifle': {'kind': 'rifle', 'R': (-0.2671, -0.3449, 0.4991), 'L': (0.1699, -0.2973, 0.1696)},
    'Gun_PumpShotgun': {'kind': 'rifle', 'R': (-0.2657, -0.353, 0.496), 'L': (0.1335, -0.2385, 0.2089)},
    'Gun_ChainShotgun': {'kind': 'rifle', 'R': (-0.2906, -0.3638, 0.4469), 'L': (0.1008, -0.2379, 0.1447)},
    'Gun_HeavySMG': {'kind': 'rifle', 'R': (-0.2731, -0.3664, 0.4217), 'L': (0.2304, -0.2695, 0.1374)},
    'Gun_LMG': {'kind': 'rifle', 'R': (-0.2657, -0.353, 0.496), 'L': (0.1438, -0.2509, 0.1777)},
    'Gun_AssaultRifle': {'kind': 'rifle', 'R': (-0.2628, -0.3558, 0.457), 'L': (0.1168, -0.2704, 0.1386)},
    'Gun_SniperRifle': {'kind': 'rifle', 'R': (-0.301, -0.3231, 0.5345), 'L': (0.1822, -0.2854, 0.1602)},
    'Gun_Minigun': {'kind': 'rifle', 'R': (-0.1363, -0.4467, 0.4191), 'R_turn': (139.4665, -189.4024, -50.5031), 'R_elbow': 30.0, 'L': (0.13, -0.2651, 0.4852), 'L_turn': (-3.0054, -36.9019, -84.7494), 'L_elbow': 12.0},
    'Gun_IncendiaryLauncher': {'kind': 'rifle', 'R': (-0.3104, -0.4795, 0.3901), 'R_turn': (105.9551, -174.6567, 11.094), 'L': (0.2253, -0.3415, 0.1857)},
    'Gun_TripleRocket': {'kind': 'rifle', 'R': (-0.3166, -0.4663, 0.4572), 'R_turn': (105.9551, -174.6567, 11.094), 'L': (0.2253, -0.3415, 0.1857)},
    'Gun_DoomsdayRocket': {'kind': 'rifle', 'R': (-0.3166, -0.4663, 0.4572), 'R_turn': (105.9551, -174.6567, 11.094), 'L': (0.2423, -0.3174, 0.1642)},
    'Gun_ChargeRifle': {'kind': 'rifle', 'R': (-0.2628, -0.3558, 0.457), 'L': (0.1563, -0.2698, 0.1724)},
    'Gun_ChargeLance': {'kind': 'rifle', 'R': (-0.2628, -0.3558, 0.457), 'L': (0.1168, -0.2704, 0.1386)},
    'Pila': {'kind': 'rifle', 'R': (-0.2578, -0.3744, 0.2149), 'R_turn': (-64.6928, 1.9417, 117.7251), 'R_elbow': None, 'L': (0.2748, -0.3776, 0.3486), 'L_turn': (-28.55, -33.9862, -117.3661), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    # Biotech
    'Gun_ToxbombLauncher': {'kind': 'rifle', 'R': (-0.3013, -0.3413, 0.5003), 'R_turn': (-82.0003, -3.7064, -166.6001), 'R_elbow': 14.0, 'L': (0.1952, -0.2576, 0.0133), 'L_turn': (167.8215, -15.937, -65.1284), 'L_elbow': 32.0},
    'Gun_BeamGraser': {'kind': 'rifle', 'R': (-0.4931, -0.3275, 0.5485), 'R_turn': (78.1382, -158.8568, -5.7454), 'R_elbow': 47.0, 'L': (0.0913, -0.3699, -0.0441), 'L_turn': (153.7378, -9.9156, -83.2651)},
    # Anomaly
    'Gun_HellcatRifle': {'kind': 'rifle', 'R': (-0.2657, -0.353, 0.496), 'R_elbow': 12.0, 'L': (0.0904, -0.3245, -0.0426), 'L_elbow': 65.0},
    'Gun_Incinerator': {'kind': 'rifle', 'R': (-0.3519, -0.3699, 0.4769), 'R_elbow': 23.0, 'L': (0.0739, -0.324, -0.113), 'L_turn': (145.4226, -17.2989, -73.0207), 'L_elbow': 77.0},
    'NerveSpiker': {'kind': 'rifle', 'R': (-0.2817, -0.3952, 0.4642), 'L': (0.1232, -0.3392, 0.1188), 'L_turn': (145.2928, -17.1239, -73.5125)},
    # Odyssey
    'Gun_BeamRepeater': {'kind': 'rifle', 'R': (-0.2657, -0.353, 0.496), 'R_elbow': None, 'L': (0.2114, -0.2725, 0.1015), 'L_elbow': 60.0},
    # One-handed guns and grenades (pistol).
    # Core
    'Gun_Revolver': {'kind': 'pistol', 'R': (-0.0347, -0.4019, 0.3392), 'R_turn': (103.7784, -173.4859, -20.8694), 'R_elbow': 68.0, 'L': (0.1587, -0.2877, 0.1693), 'L_turn': (-122.635, -18.8921, -105.6593), 'L_elbow': 29.0},
    'Gun_Autopistol': {'kind': 'pistol', 'R': (-0.0166, -0.3872, 0.3389), 'R_turn': (103.7784, -173.4859, -20.8694), 'R_elbow': 68.0, 'L': (0.1975, -0.2962, 0.1967), 'L_turn': (-122.635, -18.8921, -105.6593), 'L_elbow': 29.0},
    'Gun_MachinePistol': {'kind': 'pistol', 'R': (-0.0347, -0.4019, 0.3392), 'R_turn': (103.7784, -173.4859, -20.8694), 'R_elbow': 68.0, 'L': (0.1908, -0.2956, 0.204), 'L_turn': (-122.635, -18.8921, -105.6593), 'L_elbow': 29.0},
    'Gun_SmokeLauncher': {'kind': 'pistol', 'R': (-0.3166, -0.4663, 0.4572), 'R_turn': (105.9551, -174.6567, 11.094), 'R_elbow': 21.0, 'L': (0.2423, -0.3174, 0.1642), 'L_turn': (142.1423, -22.6339, -66.2976)},
    'Gun_EmpLauncher': {'kind': 'pistol', 'R': (-0.3166, -0.4663, 0.4572), 'R_turn': (105.9551, -174.6567, 11.094), 'R_elbow': 21.0, 'L': (0.2547, -0.3118, 0.1839), 'L_turn': (142.1423, -22.6339, -66.2976)},
    'Weapon_GrenadeFrag': {'kind': 'pistol', 'R': (-0.3176, -0.1873, 0.3437), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'L_elbow': 24.0, 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'Weapon_GrenadeMolotov': {'kind': 'pistol', 'R': (-0.3176, -0.1873, 0.3437), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'L_elbow': 24.0, 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'Weapon_GrenadeEMP': {'kind': 'pistol', 'R': (-0.3176, -0.1873, 0.3437), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'L_elbow': 24.0, 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    # Biotech
    'Weapon_GrenadeTox': {'kind': 'pistol', 'R': (-0.3176, -0.1873, 0.3437), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'L_elbow': 24.0, 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    # Bows (bow).
    # Core
    'Bow_Short': {'kind': 'bow'},
    'Bow_Recurve': {'kind': 'bow'},
    'Bow_Great': {'kind': 'bow'},
    # Biotech
    'Flamebow': {'kind': 'bow'},
    # Melee weapons (melee).
    # Core
    'MeleeWeapon_Knife': {'kind': 'melee'},
    'MeleeWeapon_Club': {'kind': 'melee', 'R_elbow': 23.0},
    'MeleeWeapon_Ikwa': {'kind': 'melee'},
    'MeleeWeapon_Gladius': {'kind': 'melee', 'R_elbow': 15.0},
    'MeleeWeapon_Mace': {'kind': 'melee'},
    'MeleeWeapon_LongSword': {'kind': 'melee', 'R': (-0.2211, -0.3225, 0.3411), 'R_elbow': 18.0, 'L': (-0.1073, -0.3189, 0.2228), 'L_turn': (-96.4636, -10.6086, -103.345), 'L_elbow': 54.0},
    'MeleeWeapon_Spear': {'kind': 'melee', 'R': (-0.2074, -0.3251, 0.2293), 'R_turn': (-51.9769, 0.0469, 122.4149), 'R_elbow': 22.0, 'L': (0.0452, -0.3151, 0.4121), 'L_turn': (-99.0584, -22.5664, -108.2994), 'L_elbow': 17.0},
    'MeleeWeapon_BreachAxe': {'kind': 'melee', 'R': (-0.0983, -0.3345, 0.3432), 'R_turn': (-27.0989, 18.4927, 113.5556), 'R_elbow': 20.0, 'L': (0.1667, -0.288, 0.4641), 'L_turn': (-118.8454, -48.3207, -89.2827)},
    # Royalty
    'MeleeWeapon_Axe': {'kind': 'melee', 'R_elbow': 23.0},
    'MeleeWeapon_Warhammer': {'kind': 'melee', 'R': (-0.0718, -0.3395, 0.3338), 'R_turn': (-27.0989, 18.4927, 113.5556), 'R_elbow': 20.0, 'L': (0.1667, -0.288, 0.4641), 'L_turn': (-118.8454, -48.3207, -89.2827)},
    'MeleeWeapon_MonoSword': {'kind': 'melee', 'R': (-0.0912, -0.4089, 0.3033), 'R_elbow': 51.0, 'L': (0.0895, -0.4372, 0.4101), 'L_turn': (-96.4636, -10.6086, -103.345), 'L_elbow': 51.0},
    'MeleeWeapon_Zeushammer': {'kind': 'melee', 'R': (-0.0615, -0.3441, 0.3783), 'R_turn': (-27.0989, 18.4927, 113.5556), 'R_elbow': 27.0, 'L': (0.2532, -0.3124, 0.5119), 'L_turn': (-118.8193, -48.1725, -89.139), 'L_elbow': 10.0},
    'MeleeWeapon_PlasmaSword': {'kind': 'melee', 'R': (-0.145, -0.4381, 0.2888), 'R_elbow': 51.0, 'L': (0.0346, -0.4366, 0.4008), 'L_turn': (-96.4636, -10.6086, -103.345), 'L_elbow': 51.0},
    'MeleeWeapon_PsyfocusStaff': {'kind': 'melee', 'R': (-0.128, -0.4487, 0.3551), 'R_turn': (-29.4551, 9.7254, 111.6695), 'R_elbow': 20.0, 'L': (0.2073, -0.4172, 0.5695), 'L_turn': (-118.8454, -48.3207, -89.2827)},
}
SHIELD = 'shield_'   # the shield stances' names: SHIELD + a one-handed kind (DRAFTED), or + a weapon's defName (derived)
ONE_HANDED = ('handgun', 'melee_1h', 'grenade')   # the weapon types (WEAPON_TYPES) that take the shield stances

def shield_weapon(name):
    """The weapon (or kind) a shield stance's name is for ('shield_pistol': 'pistol'), or None for any other name."""
    return name[len(SHIELD):] if name.startswith(SHIELD) and name not in WEAPONS else None

def shielded_names():
    """The weapons that get a shield stance of their own (shield_<defName>): one-handed, of a kind with one, and held
    otherwise than their kind is (their own drafted hand or turn), so the kind's would put their grip elsewhere."""
    out = []
    for name, entry in DRAFTED.items():
        kind = entry.get('kind')
        if kind is None or SHIELD + kind not in DRAFTED or weapon_type(name) not in ONE_HANDED:
            continue
        own, base = drafted_entry(name), drafted_entry(kind)
        if any(own.get(k) != base.get(k) for k in ('R', 'R_turn')):
            out.append(name)
    return out

def shielded_entry(name):
    """A weapon's shield stance: its kind's (DRAFTED's shield_<kind>), with the right hand moved as a whole with the
    weapon in it, so that the weapon's own drafted hand and turn stand to the kind's shield stance as they do to the
    kind's plain one (the change C from the kind's plain hand turn to its shield one, put on the weapon's turn and on its
    wrist's offset from the kind's)."""
    kind = DRAFTED[name]['kind']
    out = dict(drafted_entry(SHIELD + kind))
    own, base = drafted_entry(name), drafted_entry(kind)
    if out.get('R') is None or own.get('R') is None or base.get('R') is None:
        return out
    def turn(e):
        return Euler([math.radians(v) for v in e['R_turn']], 'XYZ').to_matrix() if e.get('R_turn') is not None else None
    C = Matrix.Identity(3)
    if turn(out) is not None and turn(base) is not None:
        C = turn(out) @ turn(base).inverted()
    out['R'] = tuple(round(v, 4) for v in Vector(out['R']) + C @ (Vector(own['R']) - Vector(base['R'])))
    if turn(own) is not None and turn(base) is not None and turn(out) is not None:
        out['R_turn'] = tuple(round(math.degrees(a), 4) for a in (C @ turn(own)).to_euler('XYZ'))
    return out

def drafted_entry(name):
    """A DRAFTED entry with its kind's under it (its 'kind'): the hands, turns and body it poses with. A weapon's shield
    stance not in the table is worked out from its kind's (shielded_entry)."""
    if name not in DRAFTED and shield_weapon(name) in DRAFTED:
        return shielded_entry(shield_weapon(name))
    entry = DRAFTED[name]
    if 'kind' not in entry:
        return entry
    base = drafted_entry(entry['kind'])
    out = {k: v for k, v in base.items() if k != 'body'}
    out.update((k, v) for k, v in entry.items() if k not in ('kind', 'body'))
    body = dict(base.get('body', {}), **entry.get('body', {}))
    if body:
        out['body'] = body
    return {k: v for k, v in out.items() if v is not None}

# The bones the drafted clip is laid over the gait with, and how much (a bone not listed takes its parent's share; a free
# arm's take none).
UPPER = {'spine.001': 0.5, 'spine.002': 1.0}
# The weapon in each drafted stance's hands: IsoRim's solid slab of the game's sprite (WalkFigures.DrawWeapon, written to
# anim.txt for it). 'hand': the side whose fist holds it. 'line': what it runs along: 'hands' (from that fist through the
# other), 'forearm' (on along that hand's forearm), 'aim' (the fixed direction aim: x left, y back, z up, as DRAFTED's) or
# 'hand' (riding that hand: aim, drop and roll are as they would be with the hand at its rest turn, and the weapon turns
# as the hand turns, the fist along the hand rather than the forearm; a '<side>_turn' in DRAFTED aims it). 'at' (on the
# 'hand' line): the grip moved from the fist, model units in the figure's directions with the hand at its rest turn.
# 'drop': straight down added to the line, a share of its length (the muzzle lower). 'behind': how far back from the
# sprite's middle the fist is, a share of its length. 'roll': turned about the line (degrees), the sprite's top kept up
# at 0. Its size is its type's (WEAPON_TYPES, TYPE_SCALE: weapon_scale). Not used by any pose here; preview.py draws it.
WEAPON_SCALE = 0.9   # a share of the weapon's drawn size on the floor (the chibi's arms are short), for a type without one
GAME_SCALE = 1.2     # WalkFigures.ScaleUp: cells per model unit for a grown pawn, which a weapon's size is not scaled by
HAND_GRIP = 0.03     # model units from the wrist along the forearm to where the hand closes
# The weapon moved as a whole once its fist and line are found, along the figure's own directions (model units), so it
# keeps its turn: up, out to the side of the hand holding it, and forward.
WEAPON_LIFT = 0.0    # up (down if less than 0)
WEAPON_OUT = 0.0     # out to the holding hand's side (in across the body if less than 0)
WEAPON_FORWARD = 0.0   # forward (back if less than 0)
def weapon_scale(name):
    """A WEAPONS entry's size held (and holstered): its type's TYPE_SCALE, else WEAPON_SCALE."""
    return TYPE_SCALE.get(weapon_type(name), WEAPON_SCALE)

# Every weapon is of one type, which sets how big it is drawn (held or holstered) and which holster slots it takes; the
# game's mod options can change each type's size, starting from these. 'handgun' (pistols, revolvers), 'rifle' (any
# other gun, launchers too), 'bow', 'grenade' (anything thrown), 'melee_1h' and 'melee_2h'. WEAPON_TYPES names a
# weapon's type by its defName, or a drafted kind's; a weapon not named takes its drafted kind's (DRAFTED's 'kind'),
# and in the game one the export doesn't name is sorted by WalkFigures.TypeOf: thrown is a grenade, then by its
# drafted kind (WalkFigures.KindOf), a melee weapon two-handed from WalkFigures.HeavyMelee kg. The preview rewrites
# these three tables whole, one entry a line.
WEAPON_TYPES = {
    'rifle': 'rifle',
    'pistol': 'handgun',
    'bow': 'bow',
    'melee': 'melee_1h',
    'Gun_SmokeLauncher': 'rifle',
    'Gun_EmpLauncher': 'rifle',
    'Weapon_GrenadeFrag': 'grenade',
    'Weapon_GrenadeMolotov': 'grenade',
    'Weapon_GrenadeEMP': 'grenade',
    'Weapon_GrenadeTox': 'grenade',
    'MeleeWeapon_LongSword': 'melee_2h',
    'MeleeWeapon_Spear': 'melee_2h',
    'MeleeWeapon_BreachAxe': 'melee_2h',
    'MeleeWeapon_Warhammer': 'melee_2h',
    'MeleeWeapon_MonoSword': 'melee_2h',
    'MeleeWeapon_Zeushammer': 'melee_2h',
    'MeleeWeapon_PlasmaSword': 'melee_2h',
    'MeleeWeapon_PsyfocusStaff': 'melee_2h',
    'Gun_BoltActionRifle': 'rifle',
    'Gun_PumpShotgun': 'rifle',
    'Gun_ChainShotgun': 'rifle',
    'Gun_HeavySMG': 'rifle',
    'Gun_LMG': 'rifle',
    'Gun_AssaultRifle': 'rifle',
    'Gun_SniperRifle': 'rifle',
    'Gun_Minigun': 'rifle',
    'Gun_IncendiaryLauncher': 'rifle',
    'Gun_TripleRocket': 'rifle',
    'Gun_DoomsdayRocket': 'rifle',
    'Gun_ChargeRifle': 'rifle',
    'Gun_ChargeLance': 'rifle',
    'Pila': 'rifle',
    'Gun_ToxbombLauncher': 'rifle',
    'Gun_BeamGraser': 'rifle',
    'Gun_HellcatRifle': 'rifle',
    'Gun_Incinerator': 'rifle',
    'NerveSpiker': 'rifle',
    'Gun_BeamRepeater': 'rifle',
}
# Each type's size, against the same weapon lying on the floor as IsoRim draws it (the game divides it by GroundRatio,
# as IsoRim's ground models are smaller than their sprites, to get the share of the sprite it draws it at).
TYPE_SCALE = {
    'handgun': 0.66,
    'rifle': 1.0,
    'bow': 1.2,
    'grenade': 0.66,
    'melee_1h': 1.0,
    'melee_2h': 1.3,
}
# The holster slots (HOLSTERS' 'size') each type takes.
TYPE_HOLSTER = {
    'handgun': 'small',
    'rifle': 'big',
    'bow': 'big',
    'grenade': 'grenade',
    'melee_1h': 'small',
    'melee_2h': 'big',
}

def weapon_type(name):
    """A WEAPONS entry's (or drafted kind's) type: its own WEAPON_TYPES, else its drafted kind's, else a rifle's."""
    if name in WEAPON_TYPES:
        return WEAPON_TYPES[name]
    kind = DRAFTED.get(name, {}).get('kind')
    return WEAPON_TYPES.get(kind, 'rifle')

WEAPONS = {
    'rifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0824, -0.0446, -0.0089)),
    'pistol': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=72.0057, aim=(-0.9497, 0.2855, 0.1285), at=(-0.0369, -0.0463, -0.017)),   # the muzzle a little lower than the arm
    'bow': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-88.0681, aim=(0.9286, 0.337, 0.1554), at=(-0.0217, -0.0134, -0.0395)),
    # The blade up, a little forward and out to the right, clear of the head.
    'melee': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.046, -0.0376, -0.0046)),
    # Each weapon of the game and its expansions, by defName, as its kind above holds it to start with.
    # Two-handed guns (rifle).
    # Core
    'Gun_BoltActionRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0782, -0.0525, 0.0043)),
    'Gun_PumpShotgun': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1049, -0.0506, -0.0)),
    'Gun_ChainShotgun': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0777, -0.0479, -0.0093)),
    'Gun_HeavySMG': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0581, -0.0452, -0.0158)),
    'Gun_LMG': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1028, -0.0041, -0.0065)),
    'Gun_AssaultRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0659, -0.0563, -0.0102)),
    'Gun_SniperRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0951, -0.0234, 0.0027)),
    'Gun_Minigun': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=20.2101, aim=(-0.4586, -0.8711, 0.176), at=(-0.1288, -0.1043, 0.083)),
    'Gun_IncendiaryLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=77.3609, aim=(-0.8438, 0.478, 0.244), at=(-0.104, -0.0735, 0.0001)),
    'Gun_TripleRocket': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=77.3609, aim=(-0.8438, 0.478, 0.244), at=(-0.1096, -0.0403, -0.0022)),
    'Gun_DoomsdayRocket': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=77.3609, aim=(-0.8438, 0.478, 0.244), at=(-0.1096, -0.0403, -0.0022)),
    'Gun_ChargeRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0659, -0.0563, -0.0102)),
    'Gun_ChargeLance': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1261, 0.0121, -0.0005)),
    'Pila': dict(hand='R', line='hand', drop=0.0, behind=0.03, roll=98.6146, aim=(-0.4336, -0.8776, 0.2046), at=(-0.0478, -0.0442, -0.0062)),
    # Biotech
    'Gun_ToxbombLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.062, -0.0643, -0.0097)),
    'Gun_BeamGraser': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=94.0664, aim=(-0.9091, 0.2842, 0.3047), at=(-0.2347, -0.0317, -0.0098)),
    # Anomaly
    'Gun_HellcatRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1151, -0.0382, -0.0012)),
    'Gun_Incinerator': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0904, -0.0545, -0.0063)),
    'NerveSpiker': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0622, -0.0372, 0.0513)),
    # Odyssey
    'Gun_BeamRepeater': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1049, -0.0506, -0.0)),
    # One-handed guns and grenades (pistol).
    # Core
    'Gun_Revolver': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=71.4942, aim=(-0.8909, 0.4506, 0.0563), at=(-0.0884, 0.0077, -0.0178)),
    'Gun_Autopistol': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=71.4942, aim=(-0.8909, 0.4506, 0.0563), at=(-0.0577, -0.0486, -0.0052)),
    'Gun_MachinePistol': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=71.4942, aim=(-0.8909, 0.4506, 0.0563), at=(-0.0752, -0.0253, -0.0084)),
    'Gun_SmokeLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=77.3609, aim=(-0.8438, 0.478, 0.244), at=(-0.1096, -0.0403, -0.0022)),
    'Gun_EmpLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=77.3609, aim=(-0.8438, 0.478, 0.244), at=(-0.1096, -0.0403, -0.0022)),
    'Weapon_GrenadeFrag': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-68.513, aim=(0.7647, 0.6158, -0.1896), at=(-0.1044, -0.0756, 0.0024)),
    'Weapon_GrenadeMolotov': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-141.5622, aim=(-0.538, -0.349, -0.7673), at=(0.0129, 0.0069, 0.0817)),
    'Weapon_GrenadeEMP': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-68.513, aim=(0.7647, 0.6158, -0.1896), at=(-0.1044, -0.0756, 0.0024)),
    # Biotech
    'Weapon_GrenadeTox': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-68.513, aim=(0.7647, 0.6158, -0.1896), at=(-0.1044, -0.0756, 0.0024)),
    # Bows (bow).
    # Core
    'Bow_Short': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-90.1989, aim=(0.8942, 0.3639, 0.2609), at=(-0.0526, -0.0228, -0.0447)),
    'Bow_Recurve': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-88.0681, aim=(0.9286, 0.337, 0.1554), at=(-0.0217, -0.0134, -0.0395)),
    'Bow_Great': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-88.0681, aim=(0.9286, 0.337, 0.1554), at=(-0.0374, -0.0142, -0.0367)),
    # Biotech
    'Flamebow': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-80.5073, aim=(0.9286, 0.3554, 0.107), at=(-0.0751, -0.0329, -0.051)),
    # Melee weapons (melee).
    # Core
    'MeleeWeapon_Knife': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.046, -0.0376, -0.0046)),
    'MeleeWeapon_Club': dict(hand='R', line='hand', aim=(-0.9088, 0.078, 0.4099), drop=0.0, behind=0.03, roll=82.2887, at=(-0.0623, -0.0919, 0.0034)),
    'MeleeWeapon_Ikwa': dict(hand='R', line='hand', aim=(-0.6623, -0.5944, 0.456), drop=0.0, behind=0.03, roll=74.8588, at=(-0.0385, -0.0399, 0.0006)),
    'MeleeWeapon_Gladius': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.0956, -0.1166, 0.0196)),
    'MeleeWeapon_Mace': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.0574, -0.0825, 0.0101)),
    'MeleeWeapon_LongSword': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.1394, -0.2003, 0.0615)),
    'MeleeWeapon_Spear': dict(hand='R', line='hand', aim=(-0.6452, -0.6243, 0.4404), drop=0.0, behind=0.03, roll=99.4437, at=(-0.0826, -0.1444, 0.0369)),
    'MeleeWeapon_BreachAxe': dict(hand='R', line='hand', aim=(0.766, -0.6423, 0.0266), drop=0.0, behind=0.03, roll=-68.4701, at=(-0.1187, -0.1221, 0.0325)),
    # Royalty
    'MeleeWeapon_Axe': dict(hand='R', line='hand', aim=(-0.9088, 0.078, 0.4099), drop=0.0, behind=0.03, roll=82.2887, at=(-0.0676, -0.0923, -0.0013)),
    'MeleeWeapon_Warhammer': dict(hand='R', line='hand', aim=(0.766, -0.6423, 0.0266), drop=0.0, behind=0.03, roll=-68.4701, at=(-0.113, -0.1154, 0.0436)),
    'MeleeWeapon_MonoSword': dict(hand='R', line='hand', aim=(-0.9197, -0.0774, 0.3848), drop=0.0, behind=0.03, roll=81.6481, at=(-0.0973, -0.2719, 0.0591)),
    'MeleeWeapon_Zeushammer': dict(hand='R', line='hand', aim=(-0.9368, 0.0631, 0.3441), drop=0.0, behind=0.03, roll=78.3226, at=(-0.0422, -0.0941, -0.0142)),
    'MeleeWeapon_PlasmaSword': dict(hand='R', line='hand', aim=(-0.9197, -0.0774, 0.3848), drop=0.0, behind=0.03, roll=81.6481, at=(-0.0915, -0.272, 0.0583)),
    'MeleeWeapon_PsyfocusStaff': dict(hand='R', line='hand', aim=(0.766, -0.6423, 0.0266), drop=0.0, behind=0.03, roll=-68.4701, at=(-0.0779, -0.1092, 0.0189)),
}

# Holstered weapons (WalkFigures.DrawHolstered): a weapon the pawn has but is not holding (the equipped one while it is
# not drafted or aiming, and any in its inventory, such as sidearms) is drawn on the body, the same slab of its sprite at
# the size it is held at (weapon_scale). Each slot rides a bone and takes one weapon of its 'size' (its type's
# TYPE_HOLSTER): 'small' (handguns, one-handed melee), 'grenade' or 'big' (the rest); the equipped weapon is put in the first free slot of its size, then the
# inventory's in turn, and a weapon with no free slot of its size is not drawn. 'bone': the bone it rides. 'at': where
# the weapon sits with the figure at rest, model units from the middle of the hips (x left, y back, z up). 'aim': the
# way its muzzle (the sprite's right) points at rest, the same axes; 'roll': degrees turned about that line, the
# sprite's top kept as near up as the line lets it at 0 (straight down, it faces forward). 'behind': how far along the
# line from 'at' the sprite's middle is, a share of its length. 'out': the way out of the body there; the game pushes
# the slot out along it by however much the pawn's body shape and the garments it wears stand further out there than
# the bare base body does (SkinnedFigure.Clearance), so it sits on the outside of a coat rather than in it, and a big
# slot further if its weapon would cut into the weapon in an earlier big slot on the same bone facing the same way
# (SkinnedFigure.HolsterOver; 'at' is then only as far in as it goes).
# A backpack is one more garment to it. 'melee_at', 'melee_aim', 'melee_roll': the same, used in their place for a
# melee weapon: its blade is not along the 'aim' as a gun's barrel is, but well round from it toward the
# top (the def's equippedAngleOffset), so a slot's gun place lays it flat. Set for MELEE_SLOT_OFFSET, the usual one: a
# weapon with another is turned by the difference in its own plane, so every blade hangs the same way.
# 'pack': True, the slot is only used while the back's weapons are moved to the sides of a pack, False, only while they
# aren't; left out, either. They are with both a garment of HOLSTER_PACKS and an equipment pack (GEAR's 'pack';
# WalkFigures.OnPackSides). A garment of HOLSTER_PACKS is carried as a holster is, whole on GEAR's
# 'pack' bone and the same size on every pawn (SkinnedFigure.Packed). A pack slot's 'at' says only how high and how far
# back along the pack it goes, set against the CE backpack; how far out comes from the pack's side and the weapon's own
# thickness in the game (SkinnedFigure.PackSide). 'out' away from the side.
# 'belt': the same for a belt (GEAR's 'belt' mount, if drawn), which takes the front of the waist from the grenades: True,
# the slot is only used with one on, False, only with none. With the left arm gone they stay at the waist
# (WalkFigures.OnBeltSlots).
MELEE_SLOT_OFFSET = -65.0
HOLSTER_PACKS = ('CE_Apparel_Backpack', 'CE_Apparel_TribalBackpack', 'CE_Apparel_RadioPack')   # defNames of the apparel that moves the back's weapons
HOLSTERS = {
    'back': dict(size='big', bone='spine.002', at=(0.0, 0.06, 0.17), aim=(0.5, 0.0, 0.656), roll=0.0, behind=0.0, out=(0.0, 1.0, 0.0), pack=False),
    'back_2': dict(size='big', bone='spine.002', at=(-0.0, 0.107, 0.17), aim=(-0.6062, 0.0, 0.7953), roll=-0.0, behind=0.0, out=(-0.0, 1.0, 0.0), pack=False),
    # Down each side of a worn pack, muzzle down and the upper end leant back, clear of the chibi's big head.
    'pack_R': dict(size='big', bone='spine.002', at=(-0.147, 0.0777, 0.185), aim=(0.0, -0.2588, -0.9659), roll=0.0, behind=0.0, out=(-1.0, 0.0, 0.0), pack=True),
    'pack_L': dict(size='big', bone='spine.002', at=(0.147, 0.0777, 0.185), aim=(0.0, -0.2588, -0.9659), roll=0.0, behind=0.0, out=(1.0, 0.0, 0.0), pack=True),
    'hip_R': dict(size='small', bone='spine', at=(-0.1576, -0.0463, -0.0766), aim=(0.0452, 0.4054, -0.913), roll=-173.284, behind=0.0, out=(-1.0, 0.0, 0.0), melee_aim=(0.034, -0.6426, -0.7655), melee_roll=175.748, melee_at=(-0.1562, -0.0717, -0.0843)),
    'hip_L': dict(size='small', bone='spine', at=(0.1576, -0.0463, -0.0766), aim=(-0.0452, 0.4054, -0.913), roll=-6.716, behind=0.0, out=(1.0, 0.0, 0.0), melee_aim=(-0.034, -0.6426, -0.7655), melee_roll=-175.748, melee_at=(0.1562, -0.0717, -0.0843)),
    # Grenades hang on the front of the belt, either side of the buckle.
    'grenade_L': dict(size='grenade', bone='spine', at=(-0.0704, -0.1585, 0.1291), aim=(-0.4586, -0.3166, -0.8303), roll=54.111, behind=0.0, out=(0.0, -1.0, 0.0), belt=False),
    'grenade_R': dict(size='grenade', bone='spine', at=(-0.0179, -0.1774, 0.1069), aim=(-0.3765, -0.4068, -0.8323), roll=44.1465, behind=0.0, out=(0.0, -1.0, 0.0), belt=False),
    # With a belt on they go on the outside of the upper left arm, one above the other, lying flat on it.
    'grenade_arm': dict(size='grenade', bone='spine.001', at=(-0.0603, -0.1614, 0.1791), aim=(0.4392, -0.5505, -0.7099), roll=-58.1041, behind=0.0, out=(0.5745, 0.0494, 0.817), belt=True),
    'grenade_arm_2': dict(size='grenade', bone='spine.001', at=(-0.0055, -0.1604, 0.218), aim=(0.4892, -0.5536, -0.674), roll=-47.3743, behind=0.0, out=(-0.5745, 0.0494, 0.817), belt=True),
}

def holster_size(name):
    """The slot size a WEAPONS entry takes: its type's TYPE_HOLSTER."""
    return TYPE_HOLSTER.get(weapon_type(name), 'big')

def is_melee(name):
    """Whether a WEAPONS entry (or drafted kind) is a melee weapon, by its type."""
    return weapon_type(name).startswith('melee')

def holster_place(slot, melee=False):
    """A HOLSTERS slot's at, aim and roll, its melee_ ones for a melee weapon (where it has them)."""
    h = HOLSTERS[slot]
    if melee:
        return h.get('melee_at', h['at']), h.get('melee_aim', h['aim']), h.get('melee_roll', h['roll'])
    return h['at'], h['aim'], h['roll']

# Packs, belts and waist tools worn on the belt layer (IsoRim's WornGearDef: Apparel_PackJump, Apparel_ShieldBelt, the lances
# ...; WalkFigures.DrawWornGear, SkinnedFigure.GearAt), drawn on the body from IsoRim's own models, which it made on its
# own figure. One slot per IsoRim 'mount': 'pack' (behind the back), 'belt' (a ring round the waist, stretched to it) and 'hip'
# (a tool hung at the right hip). 'bone': the bone it rides. 'at': where the model's reference point goes with the figure at
# rest, model units from the middle of the hips (x left, y back, z up): for a pack the middle of its back plane at the
# waist's height (IsoRim's 0.30), for a hip tool its clip (0.19 out at that height), for a belt the middle of the ring, its
# half width (the x here) and depth where the bare body's waist is. 'out': the way out of the body there; the game pushes
# the slot out along it past the garments worn and the body shape (SkinnedFigure.Clearance), as a weapon's holster is.
# 'scale': the model's size, a share of IsoRim's (its figure's trunk is longer and wider than the chibi's), the belt's
# height only. 'depth' (a belt): how much further its depth is stretched than its width. 'turn' (none by default): the
# model's turn at rest about its reference point (a belt's: the ring's middle), degrees about x, y and z in that order.
GEAR = {
    'pack': dict(bone='spine.002', at=(0.0, 0.0277, 0.0744), out=(0.0, 1.0, 0.0), scale=0.622),
    'belt': dict(bone='spine', at=(-0.107, -0.0273, 0.0744), out=(-1.0, 0.0, 0.0), scale=0.622, depth=1.345),
    'hip': dict(bone='upper_arm.R', at=(-0.138, -0.0172, 0.2701), out=(-1.0, 0.0, 0.0), turn=(3.0302, 60.9007, -8.2747), scale=0.622),
}

# Aiming and firing (aim_at): while the game aims the weapon (FigureWeapons.AimOf: the warm-up, the burst and the
# cool-down) it is brought up out of the drafted stance and pointed along the aim, laid over whatever the legs do from
# the waist up (AIM_UPPER). The poses are generic, one per kind of weapon (AIMED: where the hands go and how the trunk
# turns), but baked for each weapon: the hand holding it is turned so that weapon's own hold (WEAPONS) runs exactly along
# the aim with its top up, whatever its sprite, and the other hand closes round it 'support' on from the fist. A clip's
# frames go from the aim AIM_PITCH degrees down to as far up in AIM_PITCHES steps; the game picks between them by where
# the target is. aim_<name> is ready to fire; fire_<name> the kick of a shot (the game eases it out over the weapon's
# recoil time); a bow's nock_<name> has the arrow nocked with the string hand at the bow, and is drawn into aim_ over the
# warm-up, its fire_ the loose with the string hand thrown back.
AIM_PITCH = 40.0     # degrees the frames reach up and down (IsoRim's FigureWeapons stops the aim at 70)
AIM_PITCHES = 5      # frames per clip, evenly from -AIM_PITCH to AIM_PITCH
AIM_BEND = 0.35      # share of the pitch the trunk takes (bent back to aim up); the arms do the rest
AIM_UP_REACH = (0.15, 0.12)  # leg lengths the fist is pushed forward and down at the highest aim, so the weapon
                            # clears the big chin (in proportion on the way up)
AIM_LOOK = 0.6       # share of the pitch the head takes, to look along the aim; it also turns back the trunk's twist
AIM_UPPER = {'spine.001': 0.5, 'spine.002': 1.0}   # in place of UPPER (both hands are taken)
# Each kind: 'fist' (the holding hand's fist, as DRAFTED's wrists: leg lengths from the middle of the hips in the chest's
# frame, x left, y back, z up, aimed level; the weapon sits off it by its hold's 'at'), 'elbow' (that elbow's lift, as
# DRAFTED's '<side>_elbow'); 'support' (leg lengths on along the aim from the weapon's grip that the other hand closes
# round it, or less if that arm can't reach, 'under' that far below its line, the fingers pointing up from under it
# turned 'wrap' degrees about it, or held at 'other_turn' (x, y, z degrees in the weapon's frame: x along it, y its top,
# z to its right, from the hand's rest turn; a bow's string hand too), 'other_elbow' its elbow's lift); 'twist' (the
# trunk turned about the vertical, + to the left, degrees; the head turns it back), 'lean' (the trunk bent forward,
# degrees) and 'sight' (the head bowed onto the sights, degrees). A shot drives the fist 'kick' leg lengths back along
# the aim and the muzzle 'rise' degrees up, rocking the trunk back by 'rock' degrees. The holding side is the weapon's
# WEAPONS 'hand'; a weapon held in the other hand from its kind's is not aimed.
# Standing still the game plays the whole aim clip in place of the drafted stance, so it has its own legs: 'body' (the
# numbers DROP (knees bent), NARROW, STAGGER (the left foot forward) and HEAD_TILT (degrees, + to the left), in place of the drafted stance's) and 'hips' (the
# whole figure turned about the vertical, feet and all, + to the left, degrees; 'twist' turns the trunk on from there, and
# the head turns both back to face the target). On the move only the waist up is laid over the stride.
# A bow's string hand goes by the aim instead ('draw', 'nock', 'loose': its wrist from the bow hand's fist, leg lengths to
# the figure's right, back along the aim and up), its elbow toward 'draw_pole' (the chest's frame, as arm_to's pole)
# and then lifted by 'other_elbow', the same at every stage.
# Each weapon by defName names its 'kind' (else its DRAFTED kind's is used) and gives only what it changes; None: not
# aimed at all (it is thrown).
AIMED = {
    # The stock in the right shoulder under the chin (the head is too big for a cheek weld), the left hand on the handguard.
    # Squared up: feet apart, the left forward, knees bent, leaning into it, turned well to the right so the left shoulder
    # comes forward and the short left arm reaches further along the gun.
    'rifle': dict(fist=(0.2197, -0.4411, 0.5682), elbow=30.0, support=0.067, under=0.1137, wrap=74.3931, other_elbow=10.0, hips=-25.0, twist=-25.0, lean=12.0, sight=6.0, kick=0.04, rise=6.0, rock=4.0, body=dict(DROP=0.08, NARROW=0.15, STAGGER=0.22), other_turn=(38.2819, -52.8616, 56.0577)),
    # Both arms out in front at chest height, the left hand cupping the right, turned a little to the right (Weaver).
    'pistol': dict(fist=(-0.0069, -0.5687, 0.7017), elbow=15.0, support=-0.0124, under=0.2469, wrap=-60.0, other_elbow=10.0, hips=-10.0, twist=-5.0, lean=10.0, sight=4.0, kick=0.03, rise=12.0, rock=3.0, body=dict(DROP=0.07, NARROW=0.2, STAGGER=0.12), other_turn=(107.3974, 9.6516, 31.2259)),
    # From the hip: the fist by the right hip, the left hand well up the barrel, leaning into it.
    'heavy': dict(fist=(-0.22, -0.08, 0.32), elbow=0.0, support=0.05, under=0.04, wrap=-20.0, other_elbow=0.0,
                  hips=-25.0, twist=-20.0, lean=10.0, sight=-4.0, kick=0.05, rise=3.0, rock=5.0,
                  body=dict(DROP=0.1, NARROW=0.05, STAGGER=0.25)),
    # Side on to the target, the bow arm straight out, drawn to the right of the chin.
    'bow': dict(fist=(0.523, -0.5058, 0.7092), elbow=1.8, hips=-50.58, twist=0.0, lean=10.02, sight=4.0,
                body=dict(DROP=0.03, NARROW=0.2, STAGGER=0.24),
                draw=(0.2343, 0.6384, 0.0301), nock=(0.1864, 0.3616, -0.0142), loose=(0.3548, 0.7051, 0.0617), draw_pole=(-0.6, 1.0, 0.2),
                other_turn=(-16.9019, -45.4158, 217.5032), other_elbow=90.0,
                kick=0.0, rise=0.0, rock=0.0),
    'Gun_Minigun': dict(kind='heavy', fist=(-0.0521, -0.4513, 0.2714), support=0.2604, under=0.0494, twist=-37.73, hips=-71.14, body=dict(DROP=0.07), other_turn=(-30.4861, -3.7294, 232.7636)),
    'Gun_SmokeLauncher': dict(kind='rifle', fist=(-0.2476, -0.214, 0.6054), other_turn=(74.3174, -8.948, 5.5417), support=0.4692, under=0.0862, body=dict(HEAD_TILT=36.03)),
    'Gun_EmpLauncher': dict(kind='rifle', fist=(-0.1606, -0.2899, 0.532), support=-0.0107, under=0.3159, other_turn=(40.5254, -57.9054, 62.1673), body=dict(HEAD_TILT=36.21)),
    'Pila': None,
    'Weapon_GrenadeFrag': None,
    'Weapon_GrenadeMolotov': None,
    'Weapon_GrenadeEMP': None,
    'Weapon_GrenadeTox': None,
    'Gun_DoomsdayRocket': dict(fist=(-0.3009, -0.2103, 0.5624), body=dict(HEAD_TILT=27.78), support=0.4074, under=0.1049, wrap=76.4235, other_turn=(76.2612, -72.3131, 7.1915)),
    'Gun_BoltActionRifle': dict(fist=(0.0731, -0.3852, 0.5393), elbow=30.99, other_turn=(74.8761, -20.872, 26.6132), support=0.2851, under=0.0833),
    'Gun_PumpShotgun': dict(support=0.1572, under=0.1118, other_turn=(80.7439, -10.546, 33.9946), elbow=51.6, fist=(0.0406, -0.4223, 0.5286)),
    'Gun_ChainShotgun': dict(fist=(0.1859, -0.4207, 0.5263), other_turn=(98.998, -10.5561, 35.6602), support=0.3039, under=0.0872),
    'Gun_HeavySMG': dict(fist=(0.2083, -0.4312, 0.5098), support=0.1173, under=0.0203, other_turn=(53.6095, -31.9556, 43.8817)),
    'Gun_LMG': dict(fist=(0.1145, -0.3868, 0.5237), other_turn=(438.0952, -12.0064, 32.6493), support=0.1873, under=0.0198),
    'Gun_SniperRifle': dict(support=0.1555, under=0.0843, fist=(0.0387, -0.3685, 0.5399), other_turn=(66.2901, -21.236, 35.1411)),
    'Gun_IncendiaryLauncher': dict(support=0.4633, under=0.1471, fist=(-0.2592, -0.2339, 0.526), other_turn=(90.4688, -5.9004, 24.3028), body=dict(HEAD_TILT=37.53)),
    'Gun_TripleRocket': dict(fist=(-0.2518, -0.2091, 0.5769), other_turn=(61.0379, -30.1183, 26.6709), support=0.3151, under=0.1426, body=dict(HEAD_TILT=35.46)),
    'Gun_ChargeLance': dict(other_turn=(62.5122, -25.6384, 40.0085), support=0.1847, under=0.0345, fist=(-0.0276, -0.3094, 0.5329)),
    'Gun_ToxbombLauncher': dict(fist=(0.0127, -0.3471, 0.4372), support=0.3687, under=0.3575, other_turn=(57.3644, -32.4987, 45.7054)),
    'Gun_BeamGraser': dict(other_turn=(56.6437, -36.5021, 52.9033), support=0.3988, under=0.3323, fist=(-0.1116, -0.3531, 0.5062)),
    'Gun_HellcatRifle': dict(fist=(0.0058, -0.3349, 0.5015), support=0.3471, under=0.1889, other_turn=(80.4534, -20.9217, 27.95)),
    'Gun_Revolver': dict(fist=(-0.0412, -0.6323, 0.7083), support=-0.1218, under=0.2168, other_turn=(-240.8533, 12.8026, 13.0483)),
    'Gun_Incinerator': dict(fist=(-0.0008, -0.3534, 0.3995), support=0.0889, under=0.1416),
    'NerveSpiker': dict(support=0.3427, under=0.1421, other_turn=(8.7062, -6.9987, 69.218)),
}

# The sprite slab's holds and stances. DRAFTED, WEAPONS and AIMED were redone (commit 9fc90b4) to fit IsoRim's 3D weapon
# models, whose grips and lengths are not where the game's sprites have them: the grips moved and the other hand put
# where the model's foregrip is. A weapon IsoRim draws as its sprite's slab (no model for it, or its "Weapons as 3D models" setting
# off) is held and aimed by these instead, the entries as they were before (commit ccb1abf), each only where it differs:
# it is laid over its table's while the slab clips are exported (slab_numbers), as '<clip>_slab' (drafted_rifle_slab,
# aim_rifle_slab ...) and 'weapon <name>_slab' lines, and the game takes them for a weapon it draws as a slab
# (WalkFigures.AsSlab). A weapon with no entry here is held as with its model. The strikes and draws still start and end
# at the model's guard. {} in SLAB_AIMED: the weapon had no entry of its own then (aimed as its kind). The bows keep the
# model's arms (no entries in SLAB_DRAFTED or SLAB_AIMED); only their holds are here, moved so the sprite's grip is in
# the fist (a hold's 'at' is in the hand's frame, so it holds whatever the arms do).
SLAB = '_slab'
SLAB_DRAFTED = {
    'rifle': {'R': (-0.2657, -0.353, 0.496), 'R_turn': (101.0351, -158.5573, 8.8982), 'R_elbow': 21.0, 'L': (0.2114, -0.2725, 0.1015), 'L_turn': (142.1423, -22.6339, -66.2976)},     # on the handguard, low by the left hip (as far down as the arm reaches)
    'pistol': {'R': (-0.0077, -0.4058, 0.2864), 'R_turn': (-66.2829, -24.3397, 150.5612), 'R_elbow': 56.0, 'L': (0.1871, -0.3124, 0.1098), 'L_turn': (-117.9665, -16.0991, -103.3278)},
    'melee': {'R': (-0.2946, -0.329, 0.2319), 'R_turn': (-64.6928, 1.9417, 117.7251), 'L': (0.2748, -0.3776, 0.3486), 'L_turn': (-28.55, -33.9862, -117.3661), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'unarmed': {'R': (-0.1667, -0.3951, 0.5415), 'R_turn': (-38.9931, 35.7118, 137.1571), 'L': (0.1937, -0.236, 0.4861), 'L_turn': (-36.5014, -37.3041, -128.3353), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'Gun_BoltActionRifle': {'kind': 'rifle', 'R': (-0.2589, -0.3721, 0.482), 'R_turn': (98.039, -138.9431, 7.1849), 'R_elbow': 24.0, 'L': (0.1225, -0.2771, -0.044), 'L_turn': (161.4368, -15.3106, -76.1904)},
    'Gun_PumpShotgun': {'kind': 'rifle', 'R': (-0.2106, -0.3862, 0.5056), 'R_turn': (98.039, -138.9431, 7.1849), 'R_elbow': 18.0, 'L': (0.1224, -0.3082, 0.0798), 'L_turn': (161.4368, -15.3106, -76.1904)},
    'Gun_ChainShotgun': {'kind': 'rifle', 'R': (-0.325, -0.3528, 0.5293), 'R_elbow': 24.0, 'L': (0.2431, -0.3321, 0.0231), 'L_turn': (152.6926, 5.1563, -82.0858)},
    'Gun_HeavySMG': {'kind': 'rifle', 'R': (-0.2832, -0.3641, 0.5318), 'R_elbow': 45.0, 'L': (0.2801, -0.3437, 0.0134), 'L_turn': (147.6814, -0.9852, -75.0837)},
    'Gun_LMG': {'kind': 'rifle', 'R_elbow': 31.0, 'L': (0.2308, -0.2842, 0.044), 'L_turn': (146.9871, -14.3005, -78.9835)},
    'Gun_SniperRifle': {'kind': 'rifle', 'R': (-0.2589, -0.3721, 0.482), 'R_turn': (98.039, -138.9431, 7.1849), 'R_elbow': 24.0, 'L': (0.1225, -0.2771, -0.044), 'L_turn': (161.4368, -15.3106, -76.1904)},
    'Gun_Minigun': {'kind': 'rifle', 'R': (-0.3418, -0.6318, 0.2111), 'R_turn': (96.7516, -144.2497, 27.3126), 'R_elbow': 30.0, 'L': (0.2056, -0.2903, 0.5535), 'L_turn': (-30.1936, 18.3994, -85.798), 'L_elbow': 12.0},
    'Gun_IncendiaryLauncher': {'kind': 'rifle', 'R': (-0.3763, -0.3591, 0.4524), 'R_turn': (109.5223, -179.9215, 8.0689), 'R_elbow': 1.0, 'L': (0.0676, -0.3755, 0.0119), 'L_turn': (177.4163, -5.9966, -81.8477), 'L_elbow': 14.0},
    'Gun_TripleRocket': {'kind': 'rifle', 'R': (-0.2203, -0.4191, 0.1768), 'R_turn': (-77.3372, 19.63, -166.2455), 'R_elbow': None, 'L': (0.3069, -0.3513, 0.1836), 'L_turn': (-85.2311, -27.2537, -145.3639)},
    'Gun_DoomsdayRocket': {'kind': 'rifle', 'R': (-0.2306, -0.5156, 0.0569), 'R_turn': (89.6756, -133.3236, 9.8356), 'R_elbow': 53.0, 'L': (0.1421, -0.5202, 0.1348), 'L_turn': (131.4303, 104.3359, 7.6692), 'L_elbow': 72.0},
    'Gun_ChargeRifle': {'kind': 'rifle', 'R_elbow': 18.0, 'L': (0.2661, -0.3376, -0.0787), 'L_turn': (149.4947, -14.8703, -79.3754), 'L_elbow': 70.0},
    'Gun_ChargeLance': {'kind': 'rifle', 'R': (-0.2633, -0.356, 0.4941), 'R_turn': (81.6041, -158.3411, -6.6936), 'R_elbow': None, 'L': (0.1492, -0.3273, 0.0932), 'L_turn': (156.6231, -6.1531, -95.2736)},
    'Pila': {'kind': 'rifle', 'R': (-0.2946, -0.329, 0.2319), 'R_turn': (-64.6928, 1.9417, 117.7251), 'R_elbow': None, 'L': (0.2748, -0.3776, 0.3486), 'L_turn': (-28.55, -33.9862, -117.3661), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'Gun_ToxbombLauncher': {'kind': 'rifle', 'R': (-0.3488, -0.3416, 0.4632), 'R_turn': (-82.0003, -3.7064, -166.6001), 'R_elbow': 14.0, 'L': (0.1952, -0.2576, 0.0133), 'L_turn': (167.8215, -15.937, -65.1284), 'L_elbow': 32.0},
    'Gun_BeamGraser': {'kind': 'rifle', 'R_turn': (78.1382, -158.8568, -5.7454), 'R_elbow': None, 'L': (0.1566, -0.3268, 0.0613), 'L_turn': (153.7378, -9.9156, -83.2651)},
    'Gun_HellcatRifle': {'kind': 'rifle', 'R_elbow': 12.0, 'L': (0.2187, -0.2757, 0.0797), 'L_elbow': 65.0},
    'Gun_Incinerator': {'kind': 'rifle', 'R': (-0.379, -0.3599, 0.4926), 'R_elbow': 23.0, 'L': (0.1611, -0.3241, 0.0501), 'L_elbow': 77.0},
    'NerveSpiker': {'kind': 'rifle', 'R_elbow': None, 'L_elbow': 82.0},
    'Gun_BeamRepeater': {'kind': 'rifle', 'R_elbow': None, 'L_elbow': 60.0},
    'Gun_Revolver': {'kind': 'pistol', 'R': (-0.0336, -0.3976, 0.2817), 'R_turn': (97.5025, -143.9392, -19.2937), 'R_elbow': 68.0, 'L': (0.1336, -0.2724, 0.004), 'L_turn': (-141.324, -5.2586, -111.6846), 'L_elbow': 29.0},
    'Gun_Autopistol': {'kind': 'pistol', 'R_elbow': 47.0, 'L_elbow': 24.0},
    'Gun_MachinePistol': {'kind': 'pistol', 'R': (-0.1194, -0.3616, 0.3174), 'R_turn': (96.4541, -158.3603, -13.3963), 'R_elbow': 20.0, 'L': (0.1997, -0.2993, 0.1035), 'L_turn': (-118.3057, -10.2684, -104.928), 'L_elbow': 52.0},
    'Gun_SmokeLauncher': {'kind': 'pistol', 'R': (-0.3757, -0.3636, 0.417), 'R_turn': (-77.4302, -2.4354, -170.2143), 'R_elbow': 24.0, 'L': (0.4244, -0.4327, -0.1228), 'L_turn': (164.5979, -10.2423, -69.8948)},
    'Gun_EmpLauncher': {'kind': 'pistol', 'R': (-0.3482, -0.366, 0.4612), 'R_turn': (-78.2759, -10.4906, -169.9419), 'R_elbow': 30.0, 'L': (0.2476, -0.376, 0.0341), 'L_turn': (142.1423, -22.6339, -66.2976), 'L_elbow': 57.0},
    'Weapon_GrenadeFrag': {'kind': 'pistol', 'R': (-0.2925, -0.2025, 0.3498), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'L_elbow': 24.0, 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'Weapon_GrenadeMolotov': {'kind': 'pistol', 'R': (-0.2925, -0.2025, 0.3498), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'Weapon_GrenadeEMP': {'kind': 'pistol', 'R': (-0.2925, -0.2025, 0.3498), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'Weapon_GrenadeTox': {'kind': 'pistol', 'R': (-0.2925, -0.2025, 0.3498), 'R_turn': (-49.1687, -1.08, 115.3258), 'L': (0.1105, -0.4213, 0.3999), 'L_turn': (-56.7964, -35.2724, -136.4743), 'body': dict(DROP=0.07, NARROW=0.0, STAGGER=0.22, LEAN=4.0)},
    'MeleeWeapon_Knife': {'kind': 'melee', 'R_elbow': 3.0},
    'MeleeWeapon_Ikwa': {'kind': 'melee', 'R_elbow': 17.0},
    'MeleeWeapon_Mace': {'kind': 'melee', 'R_elbow': 17.0},
    'MeleeWeapon_LongSword': {'kind': 'melee', 'R': (-0.2359, -0.3362, 0.222), 'R_elbow': 18.0, 'L': (-0.0541, -0.3389, 0.2687), 'L_turn': (-96.4636, -10.6086, -103.345), 'L_elbow': 54.0},
    'MeleeWeapon_Spear': {'kind': 'melee', 'R': (-0.2074, -0.3251, 0.2293), 'R_turn': (-51.9769, 0.0469, 122.4149), 'R_elbow': 22.0, 'L': (0.1035, -0.3092, 0.3766), 'L_turn': (-99.0584, -22.5664, -108.2994), 'L_elbow': 17.0},
    'MeleeWeapon_BreachAxe': {'kind': 'melee', 'R': (-0.1577, -0.3217, 0.253), 'R_turn': (-27.0989, 18.4927, 113.5556), 'R_elbow': 20.0, 'L': (0.3422, -0.2661, 0.5565), 'L_turn': (-118.8193, -48.1725, -89.139)},
    'MeleeWeapon_Warhammer': {'kind': 'melee', 'R': (-0.1577, -0.3217, 0.253), 'R_turn': (-27.0989, 18.4927, 113.5556), 'R_elbow': 22.0, 'L': (0.3488, -0.3134, 0.5514), 'L_turn': (-118.8193, -48.1725, -89.139)},
    'MeleeWeapon_MonoSword': {'kind': 'melee', 'R': (-0.2165, -0.2697, 0.2069), 'R_elbow': 13.0, 'L': (-0.002, -0.3107, 0.2914), 'L_turn': (-96.4636, -10.6086, -103.345), 'L_elbow': 51.0},
    'MeleeWeapon_Zeushammer': {'kind': 'melee', 'R': (-0.1577, -0.3217, 0.253), 'R_turn': (-27.0989, 18.4927, 113.5556), 'R_elbow': 27.0, 'L': (0.3202, -0.312, 0.5191), 'L_turn': (-118.8193, -48.1725, -89.139), 'L_elbow': 10.0},
    'MeleeWeapon_PlasmaSword': {'kind': 'melee', 'R': (-0.2165, -0.2697, 0.2069), 'R_elbow': 15.0, 'L': (-0.002, -0.3107, 0.2914), 'L_turn': (-96.4636, -10.6086, -103.345), 'L_elbow': 46.0},
    'MeleeWeapon_PsyfocusStaff': {'kind': 'melee', 'R': (0.006, -0.3521, 0.4422), 'R_turn': (-64.9897, 16.9754, 104.1523), 'R_elbow': 59.0, 'L': (0.1964, -0.4025, 0.5844), 'L_turn': (-86.2585, -34.3029, -105.3746), 'L_elbow': 5.0},
}
SLAB_WEAPONS = {
    'rifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1049, -0.0506, -0.0)),
    'melee': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.0539, -0.065, -0.007)),
    'Gun_BoltActionRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=86.0675, aim=(-0.9688, -0.1471, 0.1994), at=(-0.1126, -0.0247, -0.0052)),
    'Gun_PumpShotgun': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=86.0675, aim=(-0.9688, -0.1471, 0.1994), at=(-0.0667, 0.0003, -0.0111)),
    'Gun_ChainShotgun': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0816, -0.0142, -0.0096)),
    'Gun_HeavySMG': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0571, -0.0176, -0.0197)),
    'Gun_LMG': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1077, -0.0279, 0.0009)),
    'Gun_AssaultRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1049, -0.0506, -0.0)),
    'Gun_SniperRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=86.0675, aim=(-0.9688, -0.1471, 0.1994), at=(-0.1126, -0.0247, -0.0052)),
    'Gun_Minigun': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=97.184, aim=(-0.5375, -0.6822, 0.4957), at=(-0.1285, -0.1968, 0.0945)),
    'Gun_IncendiaryLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1306, -0.0479, 0.0038)),
    'Gun_TripleRocket': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=76.7062, aim=(-0.8686, 0.4224, 0.2592), at=(-0.006, -0.1623, -0.0185)),
    'Gun_DoomsdayRocket': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=97.9314, aim=(-0.6148, -0.684, 0.3927), at=(-0.2738, 0.0539, 0.0563)),
    'Gun_ChargeRifle': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1049, -0.0506, -0.0)),
    'Gun_ChargeLance': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=94.0664, aim=(-0.9091, 0.2842, 0.3047), at=(-0.1177, -0.0135, -0.0068)),
    'Pila': dict(hand='R', line='hand', drop=0.0, behind=0.03, roll=98.6146, aim=(-0.4336, -0.8776, 0.2046), at=(-0.0752, -0.0387, -0.0143)),
    'Gun_ToxbombLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.083, -0.0828, -0.002)),
    'Gun_BeamGraser': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=94.0664, aim=(-0.9091, 0.2842, 0.3047), at=(-0.1174, -0.0129, -0.0099)),
    'Gun_Incinerator': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1049, -0.0506, -0.0)),
    'NerveSpiker': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.1049, -0.0506, -0.0)),
    'Gun_Revolver': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=73.333, aim=(-0.948, -0.0077, 0.3182), at=(-0.0548, -0.0236, -0.0124)),
    'Gun_Autopistol': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=72.0057, aim=(-0.9497, 0.2855, 0.1285), at=(-0.0369, -0.0463, -0.017)),
    'Gun_MachinePistol': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=72.0057, aim=(-0.9497, 0.2855, 0.1285), at=(-0.0369, -0.0463, -0.017)),
    'Gun_SmokeLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.0996, -0.0346, -0.0031)),
    'Gun_EmpLauncher': dict(hand='R', line='hand', drop=0.0, behind=0.12, roll=78.4268, aim=(-0.874, 0.4098, 0.2612), at=(-0.2505, 0.003, 0.0422)),
    'Weapon_GrenadeFrag': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-68.513, aim=(0.7647, 0.6158, -0.1896), at=(-0.14, -0.0231, -0.0244)),
    'Weapon_GrenadeMolotov': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-68.513, aim=(0.7647, 0.6158, -0.1896), at=(-0.14, -0.0231, -0.0244)),
    'Weapon_GrenadeEMP': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-68.513, aim=(0.7647, 0.6158, -0.1896), at=(-0.14, -0.0231, -0.0244)),
    'Weapon_GrenadeTox': dict(hand='R', line='hand', drop=0.0, behind=0.15, roll=-68.513, aim=(0.7647, 0.6158, -0.1896), at=(-0.14, -0.0231, -0.0244)),
    'MeleeWeapon_Knife': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.0539, -0.065, -0.007)),
    'MeleeWeapon_Club': dict(hand='R', line='hand', aim=(-0.9088, 0.078, 0.4099), drop=0.0, behind=0.03, roll=82.2887, at=(-0.0676, -0.0923, -0.0013)),
    'MeleeWeapon_Ikwa': dict(hand='R', line='hand', aim=(-0.745, -0.6452, 0.1697), drop=0.0, behind=0.03, roll=92.8495, at=(-0.0592, -0.0457, -0.0222)),
    'MeleeWeapon_Gladius': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.0963, -0.1358, 0.0236)),
    'MeleeWeapon_Mace': dict(hand='R', line='hand', aim=(-0.9088, 0.078, 0.4099), drop=0.0, behind=0.03, roll=82.2887, at=(-0.0676, -0.0923, -0.0013)),
    'MeleeWeapon_LongSword': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.1451, -0.2311, 0.0634)),
    'MeleeWeapon_Spear': dict(hand='R', line='hand', aim=(-0.6452, -0.6243, 0.4404), drop=0.0, behind=0.03, roll=99.4437, at=(-0.0911, -0.1441, 0.0171)),
    'MeleeWeapon_BreachAxe': dict(hand='R', line='hand', aim=(-0.9368, 0.0631, 0.3441), drop=0.0, behind=0.03, roll=78.3226, at=(-0.0914, -0.1479, 0.0103)),
    'MeleeWeapon_Warhammer': dict(hand='R', line='hand', aim=(-0.9368, 0.0631, 0.3441), drop=0.0, behind=0.03, roll=78.3226, at=(-0.0914, -0.1479, 0.0103)),
    'MeleeWeapon_MonoSword': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.1367, -0.2353, 0.0592)),
    'MeleeWeapon_Zeushammer': dict(hand='R', line='hand', aim=(-0.9368, 0.0631, 0.3441), drop=0.0, behind=0.03, roll=78.3226, at=(-0.0914, -0.1479, 0.0103)),
    'MeleeWeapon_PlasmaSword': dict(hand='R', line='hand', aim=(-0.9368, 0.0571, 0.3451), drop=0.0, behind=0.03, roll=79.3857, at=(-0.1367, -0.2353, 0.0592)),
    'MeleeWeapon_PsyfocusStaff': dict(hand='R', line='hand', aim=(-0.9376, 0.0912, 0.3357), drop=0.0, behind=0.03, roll=77.9541, at=(-0.1312, -0.2002, 0.0171)),
    # The bows: their model holds with the grip's place along and across the bow scaled by how much bigger the slab is
    # drawn than the model (the model's fit to the sprite, 1.22 to 1.36), its depth into the palm kept; checked on renders.
    # Bow_Recurve and Bow_Great moved onto their staves as measured by ./autotest.sh bows_slab (WalkFigures.AutoSlabGrip:
    # the fist sat near the string), for Combat Extended's 256 px sprites of them.
    'bow': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-88.0681, aim=(0.9286, 0.337, 0.1554), at=(-0.0278, -0.0165, -0.0405)),
    'Bow_Short': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-90.1989, aim=(0.8942, 0.3639, 0.2609), at=(-0.074, -0.0321, -0.051)),
    'Bow_Recurve': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-88.0681, aim=(0.9286, 0.337, 0.1554), at=(-0.126, -0.0515, -0.0212)),
    'Bow_Great': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-88.0681, aim=(0.9286, 0.337, 0.1554), at=(-0.179, -0.0636, -0.0299)),
    'Flamebow': dict(hand='L', line='hand', drop=0.0, behind=0.0, roll=-80.5073, aim=(0.9286, 0.3554, 0.107), at=(-0.1029, -0.0424, -0.0543)),
}
SLAB_AIMED = {
    'rifle': dict(fist=(-0.13, -0.3, 0.56), elbow=30.0, support=0.7269, under=0.1532, wrap=74.3931, other_elbow=10.0, hips=-25.0, twist=-25.0, lean=12.0, sight=6.0, kick=0.04, rise=6.0, rock=4.0, body=dict(DROP=0.08, NARROW=0.15, STAGGER=0.22)),
    'pistol': dict(fist=(-0.03, -0.46, 0.58), elbow=15.0, support=-0.01, under=0.05, wrap=-60.0, other_elbow=10.0,
                   hips=-10.0, twist=-5.0, lean=10.0, sight=4.0, kick=0.03, rise=12.0, rock=3.0,
                   body=dict(DROP=0.07, NARROW=0.2, STAGGER=0.12)),
    'Gun_Minigun': dict(kind='heavy', fist=(-0.0563, -1.0546, -0.1945), support=0.2199, under=-0.4641, twist=-37.73, hips=-71.14, body=dict(DROP=0.07), other_turn=(-34.3886, -2.5511, 232.6626)),
    'Gun_SmokeLauncher': dict(kind='rifle', fist=(0.1459, -0.3981, 0.4238), other_turn=(74.3174, -8.948, 5.5417), support=0.3376, under=0.3778),
    'Gun_EmpLauncher': dict(kind='rifle', fist=(-0.2214, -0.2239, 0.3428), support=0.1169, under=0.1475, other_turn=(40.5254, -57.9054, 62.1673)),
    'Gun_DoomsdayRocket': dict(fist=(-0.4591, -0.8682, -0.0631), body=dict(DROP=0.23), support=0.2127, under=-0.5905, twist=-31.31, wrap=76.4235, hips=-39.45, other_turn=(5.3866, -79.5673, 194.4816)),
    'Gun_BoltActionRifle': dict(fist=(0.0611, -0.3708, 0.7023), elbow=30.99, other_turn=(93.3404, -4.0956, 20.7516), support=0.2585, under=0.1202),
    'Gun_PumpShotgun': dict(support=0.5537, under=0.1482, other_turn=(80.9931, -10.3423, 36.0366), elbow=51.6, fist=(-0.1279, -0.2994, 0.4705)),
    'Gun_ChainShotgun': dict(fist=(-0.1212, -0.2402, 0.2899), other_turn=(98.998, -10.5561, 35.6602), support=0.5027, under=0.0658),
    'Gun_HeavySMG': dict(fist=(-0.1467, -0.2968, 0.255), support=0.4622, under=0.0312, other_turn=(74.9301, -20.1793, 30.1825)),
    'Gun_LMG': dict(fist=(0.1145, -0.3868, 0.5237), other_turn=(438.0952, -12.0064, 32.6493), support=0.406, under=0.1523),
    'Gun_SniperRifle': dict(support=0.2558, under=0.1384, fist=(0.0468, -0.3174, 0.7302), other_turn=(66.2901, -21.236, 35.1411)),
    'Gun_IncendiaryLauncher': dict(support=0.1512, under=0.3908, fist=(0.0495, -0.4129, 0.5033), other_turn=(90.4688, -5.9004, 24.3028)),
    'Gun_TripleRocket': dict(fist=(-0.1169, -0.7002, 0.4107), hips=-10.99, twist=4.88, elbow=3.03, other_elbow=10.0, other_turn=(61.0379, -30.1183, 26.6709), support=-0.0583, under=0.1823),
    'Gun_ChargeLance': dict(other_turn=(62.5122, -25.6384, 40.0085), support=0.343, under=0.1795),
    'Gun_HellcatRifle': dict(fist=(0.0779, -0.3984, 0.524), support=0.3786, under=0.1012, other_turn=(80.4534, -20.9217, 27.95)),
    'Gun_Revolver': dict(fist=(-0.0412, -0.6323, 0.7083), support=-0.1218, under=0.2168, other_turn=(-243.7457, 19.6047, 32.2737)),
    'Gun_Incinerator': {},   # (not in AIMED then: aimed as its kind)
    'NerveSpiker': {},   # (not in AIMED then: aimed as its kind)
}

def slab_numbers():
    """DRAFTED, WEAPONS and AIMED with the slab's entries laid over them, for varied()."""
    return dict(DRAFTED=dict(DRAFTED, **SLAB_DRAFTED), WEAPONS=dict(WEAPONS, **SLAB_WEAPONS),
                AIMED=dict(AIMED, **SLAB_AIMED))

# Striking (strike_at): one melee blow (melee_<kind>) or one throw (throw_<side>), from the drafted stance's guard back to
# it. The game plays the half before the blow lands (or the thing leaves the hand) over the end of the cool-down from the
# last blow (a throw: over the aim's warm-up), and the half after it from the tick it landed (WalkFigures.StrikeOf); the
# clip is laid on by how far from the guard it is, so between blows each weapon keeps its own drafted stance. Each clip
# starts and ends at the guard (its drafted pose), with three keys eased between: 'raised' (wound up, or the throw cocked
# back) at STRIKE_AT[0] of the way through, 'hit' (the blow landing, the throw let go) at STRIKE_AT[1] and 'through' (the
# follow-through) at STRIKE_AT[2]. The wind-up is eased, the blow speeds up into the hit and slows after it.
# Each key, for a right-handed blow (a left-handed one is mirrored):
#   'hand': the striking wrist, as DRAFTED's (leg lengths from the middle of the hips at rest in the chest's frame: x left,
#     y back, z up), and 'elbow' its elbow's lift (as DRAFTED's '<side>_elbow'); the hand turned from the guard's turn
#     'tip' degrees forward (about the chest's left-right line: the guard's upright blade comes down in front at 90),
#     then 'swing' degrees to the left about the chest's up; or, bare handed, 'fingers' the way the closed fist's
#     fingers point and 'palm' the way its palm faces (the same frame).
#   'other': the other wrist, held at the guard's turn.
#   'lean' (the trunk bent forward, degrees), 'twist' (turned about the vertical, + to the left, degrees; the head takes
#   back STRIKE_LOOK of both, or its own 'look'), 'lunge' (the hips and all above them moved forward, leg lengths, the feet kept planted) and
#   'drop' (lowered, leg lengths, knees bent).
STRIKE_FRAMES = 24
STRIKE_AT = (0.4, 0.5, 0.6)
STRIKE_LOOK = 0.7
STRIKE_UPPER = {'spine.001': 1.0}   # in place of UPPER: on the move, the blow is from the waist up
MELEE_SAME = 15.0    # degrees a melee weapon's hold may point from the generic one's and still swing in its clips
# How far forward of upright a weapon already points in its own drafted guard (degrees), taken off each key's 'tip' in
# its own clips (a spear is held slanted forward, where a sword is held upright).
GUARD_TIP = {'MeleeWeapon_Spear': 40.0}
MELEE = {
    # Overhead: raised out past the right shoulder, the blade tipped back and turned out to the right so it clears the
    # head (straight up behind the shoulder, the chibi's big head is in its way), brought down onto the target in front.
    'smash': dict(raised=dict(hand=(-0.6, 0.0, 0.62), tip=-20.0, swing=-90.0, elbow=25.0, other=(0.22, -0.32, 0.42),
                              lean=-6.0, twist=-20.0, lunge=-0.04, drop=0.0),
                  hit=dict(hand=(-0.1, -0.48, 0.58), tip=100.0, swing=10.0, other=(0.24, -0.22, 0.36),
                           lean=14.0, twist=10.0, lunge=0.1, drop=0.06),
                  through=dict(hand=(0.0, -0.36, 0.28), tip=150.0, swing=15.0, other=(0.24, -0.22, 0.36),
                               lean=18.0, twist=15.0, lunge=0.1, drop=0.07)),
    # Forehand, swept from out on the right across in front to the left, the blade level.
    'slash': dict(raised=dict(hand=(-0.45, 0.0, 0.6), tip=80.0, swing=-110.0, other=(0.25, -0.3, 0.45),
                              lean=0.0, twist=-30.0, lunge=-0.03, drop=0.0),
                  hit=dict(hand=(-0.05, -0.5, 0.55), tip=90.0, swing=20.0, other=(0.25, -0.2, 0.4),
                           lean=8.0, twist=15.0, lunge=0.1, drop=0.05),
                  through=dict(hand=(0.15, -0.38, 0.38), tip=100.0, swing=80.0, other=(0.25, -0.15, 0.38),
                               lean=12.0, twist=30.0, lunge=0.08, drop=0.05)),
    # A thrust: drawn back by the hip, then driven straight out in front, lunging onto the front foot.
    'stab': dict(raised=dict(hand=(-0.26, 0.05, 0.45), tip=85.0, swing=0.0, other=(0.2, -0.4, 0.45),
                             lean=-2.0, twist=-20.0, lunge=-0.05, drop=0.0),
                 hit=dict(hand=(-0.08, -0.56, 0.52), tip=80.0, swing=0.0, other=(0.25, -0.15, 0.38),
                          lean=12.0, twist=20.0, lunge=0.16, drop=0.06),
                 through=dict(hand=(-0.08, -0.54, 0.5), tip=80.0, swing=0.0, other=(0.25, -0.15, 0.38),
                              lean=12.0, twist=18.0, lunge=0.15, drop=0.06)),
    # Bare-handed, a straight punch from the guard, the shoulder turned into it (fist_R; fist_L is it mirrored).
    'fist': dict(raised=dict(hand=(-0.2, -0.2, 0.5), fingers=(0.0, -1.0, 0.3), palm=(0.3, 0.0, -1.0), other=(0.19, -0.3, 0.5),
                             lean=2.0, twist=-12.0, lunge=-0.02, drop=0.0),
                 hit=dict(hand=(-0.05, -0.56, 0.55), fingers=(0.0, -1.0, 0.0), palm=(0.0, 0.0, -1.0), other=(0.18, -0.22, 0.48),
                          lean=10.0, twist=22.0, lunge=0.12, drop=0.04),
                 through=dict(hand=(-0.05, -0.53, 0.53), fingers=(0.0, -1.0, 0.0), palm=(0.0, 0.0, -1.0),
                              other=(0.18, -0.22, 0.48), lean=10.0, twist=20.0, lunge=0.11, drop=0.04)),
}
# Blows at a body already on the ground (melee_down_<kind>; WalkFigures.StrikeOf, for a target down before the blow): the same
# keys, but bent and sunk well down to reach it (the hand's place is in the chest's frame, so it goes down with the lean).
# 'look' in a key is its own STRIKE_LOOK: a smaller share of the lean taken back, so the head looks down at the target.
MELEE_DOWN = {
    # Overhead and on down onto the body at the feet.
    'smash': dict(raised=dict(hand=(-0.5, -0.1, 0.75), aim=(-0.45, 0.35, 0.82), elbow=25.0, other=(0.22, -0.3, 0.42),
                              lean=-4.0, twist=-18.0, lunge=-0.04, drop=0.02, look=0.7),
                  hit=dict(hand=(-0.1, -0.49, 0.42), aim=(0.05, -1.0, 0.1), other=(0.24, -0.2, 0.3),
                           lean=42.0, twist=8.0, lunge=0.2, drop=0.3, look=0.35),
                  through=dict(hand=(-0.06, -0.48, 0.36), aim=(0.05, -1.0, -0.07), other=(0.24, -0.2, 0.3),
                               lean=46.0, twist=10.0, lunge=0.2, drop=0.33, look=0.35)),
    # A chop slantwise down across it from the right.
    'slash': dict(raised=dict(hand=(-0.5, -0.05, 0.65), aim=(-0.9, 0.0, 0.35), other=(0.25, -0.3, 0.45),
                              lean=0.0, twist=-28.0, lunge=-0.03, drop=0.02, look=0.7),
                  hit=dict(hand=(-0.08, -0.49, 0.4), aim=(0.25, -0.97, 0.1), other=(0.25, -0.2, 0.3),
                           lean=40.0, twist=12.0, lunge=0.2, drop=0.28, look=0.35),
                  through=dict(hand=(0.08, -0.42, 0.36), aim=(0.75, -0.65, 0.0), other=(0.25, -0.15, 0.3),
                               lean=44.0, twist=28.0, lunge=0.18, drop=0.3, look=0.35)),
    # The point driven down, the hand brought from up in front of the chest.
    'stab': dict(raised=dict(hand=(-0.18, -0.35, 0.72), aim=(0.0, -0.35, -0.95), elbow=20.0, other=(0.22, -0.3, 0.42),
                             lean=2.0, twist=-14.0, lunge=-0.02, drop=0.02, look=0.6),
                 hit=dict(hand=(-0.1, -0.49, 0.38), aim=(0.0, -0.97, -0.25), other=(0.25, -0.2, 0.3),
                          lean=38.0, twist=10.0, lunge=0.2, drop=0.28, look=0.35),
                 through=dict(hand=(-0.1, -0.48, 0.35), aim=(0.0, -0.97, -0.25), other=(0.25, -0.2, 0.3),
                              lean=40.0, twist=10.0, lunge=0.2, drop=0.3, look=0.35)),
    # A hammer fist from the shoulder down, sunk low as the bare hand has no reach (fist_L is it mirrored).
    'fist': dict(raised=dict(hand=(-0.24, -0.14, 0.72), fingers=(0.0, -0.3, 1.0), palm=(1.0, 0.0, 0.0), other=(0.19, -0.3, 0.45),
                             lean=4.0, twist=-16.0, lunge=-0.02, drop=0.04, look=0.6),
                 hit=dict(hand=(-0.1, -0.46, 0.3), fingers=(0.0, -1.0, 0.15), palm=(1.0, 0.0, 0.0), other=(0.2, -0.22, 0.32),
                          lean=56.0, twist=12.0, lunge=0.24, drop=0.46, look=0.3),
                 through=dict(hand=(-0.1, -0.45, 0.28), fingers=(0.0, -1.0, 0.15), palm=(1.0, 0.0, 0.0), other=(0.2, -0.22, 0.32),
                              lean=58.0, twist=12.0, lunge=0.24, drop=0.48, look=0.3)),
}
# Fighting hand to hand with a ranged weapon (melee_<blow>_<kind>, melee_down_<blow>_<kind>): the game's tools are all blunt
# ('stock', 'grip', 'limb', and 'barrel' too) or a poke. 'butt' is the blunt blow, 'jab' the poke, 'down_' each at a body on the
# ground. The keys as MELEE's, but for 'aim': the way the weapon's muzzle (a bow's arrow) runs in the chest's frame, and
# 'roll' about that from its top up (held_turn). 'both': the other hand stays on the weapon, as aim_at puts it there,
# from the weapon's AIMED numbers, in place of an 'other' place; without either the other hand keeps the guard's.
BOW_LIMB = (0.0, -0.05, -1.0)
BOW_SPLAY = -25.0
MELEE_GUN = {
    'rifle': dict(
        # The stock driven forward at the head, the muzzle swung back past the left side.
        butt=dict(both=True,
                  raised=dict(hand=(-0.3, 0.05, 0.4), aim=(0.35, -0.9, 0.15), lean=-2.0, twist=-25.0, lunge=-0.04, drop=0.0),
                  hit=dict(hand=(-0.22, -0.48, 0.48), aim=(0.75, 0.55, 0.1), lean=8.0, twist=25.0, lunge=0.14, drop=0.05),
                  through=dict(hand=(-0.14, -0.45, 0.5), aim=(0.85, 0.45, 0.0), lean=12.0, twist=35.0, lunge=0.14, drop=0.05)),
        # The muzzle thrust straight out with both hands.
        jab=dict(both=True,
                 raised=dict(hand=(-0.22, 0.12, 0.46), aim=(0.1, -1.0, 0.02), lean=-2.0, twist=-20.0, lunge=-0.05, drop=0.0),
                 hit=dict(hand=(-0.16, -0.4, 0.5), aim=(0.05, -1.0, 0.02), lean=12.0, twist=18.0, lunge=0.16, drop=0.05),
                 through=dict(hand=(-0.16, -0.38, 0.5), aim=(0.05, -1.0, 0.02), lean=12.0, twist=16.0, lunge=0.15, drop=0.05)),
        # Stood on end and rammed down, the butt first, the muzzle going back past the left of the head, where that hand can follow it.
        down_butt=dict(both=True,
                       raised=dict(hand=(-0.16, -0.42, 0.72), aim=(-0.05, 0.2, 1.0), lean=-2.0, twist=-6.0, lunge=0.0, drop=0.0, look=0.7),
                       hit=dict(hand=(-0.06, -0.47, 0.42), aim=(0.6, 0.72, 0.34), lean=40.0, twist=6.0, lunge=0.2, drop=0.3, look=0.35),
                       through=dict(hand=(-0.06, -0.46, 0.38), aim=(0.6, 0.72, 0.34), lean=42.0, twist=6.0, lunge=0.2, drop=0.32, look=0.35)),
        # The muzzle stabbed down from up by the shoulder.
        down_jab=dict(both=True,
                      raised=dict(hand=(-0.2, -0.05, 0.8), aim=(0.05, -0.45, -0.9), lean=0.0, twist=-14.0, lunge=-0.02, drop=0.0, look=0.7),
                      hit=dict(hand=(-0.14, -0.4, 0.42), aim=(0.05, -0.95, -0.3), lean=30.0, twist=10.0, lunge=0.18, drop=0.22, look=0.35),
                      through=dict(hand=(-0.14, -0.38, 0.38), aim=(0.05, -0.95, -0.3), lean=32.0, twist=10.0, lunge=0.18, drop=0.24, look=0.35))),
    'pistol': dict(
        # A pistol whip: brought over from up by the ear.
        butt=dict(raised=dict(hand=(-0.42, -0.05, 0.72), aim=(-0.2, 0.6, 0.75), elbow=20.0, other=(0.16, -0.28, 0.28),
                              lean=-4.0, twist=-22.0, lunge=-0.04, drop=0.0),
                  hit=dict(hand=(-0.08, -0.52, 0.55), aim=(0.0, -0.75, -0.65), other=(0.2, -0.2, 0.26),
                           lean=12.0, twist=14.0, lunge=0.12, drop=0.05),
                  through=dict(hand=(-0.02, -0.4, 0.3), aim=(0.0, -0.3, -0.95), other=(0.2, -0.2, 0.26),
                               lean=16.0, twist=18.0, lunge=0.12, drop=0.06)),
        # Punched out, the muzzle leading.
        jab=dict(raised=dict(hand=(-0.22, 0.02, 0.46), aim=(0.0, -1.0, 0.0), other=(0.16, -0.28, 0.28),
                             lean=0.0, twist=-16.0, lunge=-0.03, drop=0.0),
                 hit=dict(hand=(-0.06, -0.58, 0.55), aim=(0.02, -1.0, -0.02), other=(0.2, -0.2, 0.26),
                          lean=10.0, twist=20.0, lunge=0.14, drop=0.04),
                 through=dict(hand=(-0.06, -0.55, 0.53), aim=(0.02, -1.0, -0.02), other=(0.2, -0.2, 0.26),
                              lean=10.0, twist=18.0, lunge=0.13, drop=0.04)),
        down_butt=dict(raised=dict(hand=(-0.4, -0.05, 0.7), aim=(-0.2, 0.6, 0.75), elbow=20.0, other=(0.16, -0.28, 0.28),
                                   lean=-2.0, twist=-18.0, lunge=-0.03, drop=0.02, look=0.7),
                       hit=dict(hand=(-0.1, -0.49, 0.42), aim=(0.0, -0.9, -0.2), other=(0.22, -0.2, 0.26),
                                lean=46.0, twist=10.0, lunge=0.22, drop=0.34, look=0.35),
                       through=dict(hand=(-0.08, -0.47, 0.36), aim=(0.0, -0.85, -0.5), other=(0.22, -0.2, 0.26),
                                    lean=48.0, twist=10.0, lunge=0.22, drop=0.36, look=0.35)),
        down_jab=dict(raised=dict(hand=(-0.2, -0.25, 0.62), aim=(0.0, -0.5, -0.85), other=(0.16, -0.28, 0.28),
                                  lean=4.0, twist=-12.0, lunge=-0.01, drop=0.02, look=0.6),
                      hit=dict(hand=(-0.1, -0.49, 0.38), aim=(0.0, -0.99, 0.1), other=(0.22, -0.2, 0.26),
                               lean=46.0, twist=10.0, lunge=0.22, drop=0.38, look=0.3),
                      through=dict(hand=(-0.1, -0.48, 0.35), aim=(0.0, -0.99, 0.1), other=(0.22, -0.2, 0.26),
                                   lean=47.0, twist=10.0, lunge=0.22, drop=0.39, look=0.3))),
    # The bow in the left hand, as held, so these are left-handed as they stand (+x). Its limbs run along its top, so 'aim' down
    # brings the top forward: the far limb at the target, and with BOW_SPLAY the near one back out past the left side, clear of
    # the body.
    'bow': dict(
        butt=dict(raised=dict(hand=(0.45, -0.05, 0.72), aim=(0.0, -1.0, 0.0), lean=-4.0, twist=20.0, lunge=-0.04, drop=0.0),
                  hit=dict(hand=(0.24, -0.5, 0.52), aim=BOW_LIMB, roll=BOW_SPLAY, lean=12.0, twist=-14.0, lunge=0.12, drop=0.05),
                  through=dict(hand=(0.22, -0.44, 0.4), aim=BOW_LIMB, roll=BOW_SPLAY, lean=16.0, twist=-18.0, lunge=0.12, drop=0.06)),
        jab=dict(raised=dict(hand=(0.34, 0.02, 0.45), aim=BOW_LIMB, roll=BOW_SPLAY, lean=0.0, twist=16.0, lunge=-0.03, drop=0.0),
                 hit=dict(hand=(0.22, -0.52, 0.52), aim=BOW_LIMB, roll=BOW_SPLAY, lean=10.0, twist=-18.0, lunge=0.14, drop=0.04),
                 through=dict(hand=(0.22, -0.5, 0.5), aim=BOW_LIMB, roll=BOW_SPLAY, lean=10.0, twist=-16.0, lunge=0.13, drop=0.04)),
        down_butt=dict(raised=dict(hand=(0.42, -0.05, 0.72), aim=(0.0, -1.0, 0.0), lean=-2.0, twist=18.0, lunge=-0.03, drop=0.02, look=0.7),
                       hit=dict(hand=(0.24, -0.46, 0.42), aim=BOW_LIMB, roll=BOW_SPLAY, lean=42.0, twist=-10.0, lunge=0.2, drop=0.3, look=0.35),
                       through=dict(hand=(0.24, -0.45, 0.36), aim=BOW_LIMB, roll=BOW_SPLAY, lean=45.0, twist=-10.0, lunge=0.2, drop=0.32, look=0.35)),
        down_jab=dict(raised=dict(hand=(0.32, -0.3, 0.62), aim=BOW_LIMB, roll=BOW_SPLAY, lean=4.0, twist=12.0, lunge=-0.01, drop=0.02, look=0.6),
                      hit=dict(hand=(0.24, -0.46, 0.38), aim=BOW_LIMB, roll=BOW_SPLAY, lean=46.0, twist=-10.0, lunge=0.2, drop=0.34, look=0.3),
                      through=dict(hand=(0.24, -0.45, 0.35), aim=BOW_LIMB, roll=BOW_SPLAY, lean=47.0, twist=-10.0, lunge=0.2, drop=0.35, look=0.3))),
}
# An overhand throw (a grenade, a pilum), right-handed: cocked back behind the head with the other arm pointing at the
# target, let go up in front, and followed through down across the body. throw_R is posed from THROW_GUARD['R']'s
# drafted stance and hold, throw_L (mirrored) from THROW_GUARD['L']'s; a thrown weapon throws with the hand it holds it in.
THROW_GUARD = {'R': 'Pila', 'L': 'Weapon_GrenadeFrag'}
# A thrown weapon held in the left hand is a pack of them, so throw_pack throws one with the right instead (grenades
# were held so; they are now held in the right hand and thrown by throw_R, which leaves throw_pack unplayed):
# posed from THROW_PACK's drafted stance, the right hand first reaches across to the pack and takes one ('take', at its
# own 'at' of the way through, inside the wind-up), then it is thrown as throw_R throws. The pack stays in the left hand,
# which brings it in to meet the right and then points at the target. 'take' is a key as THROW's are, the right hand's
# turn given as a bare hand's ('fingers', 'palm').
THROW_PACK = 'Weapon_GrenadeFrag'
PACK_TAKE = dict(at=0.15, hand=(0.1, -0.4, 0.46), fingers=(0.8, -0.4, -0.45), palm=(0.3, 0.0, -1.0),
                 other=(0.2, -0.36, 0.42), lean=4.0, twist=8.0, lunge=0.0, drop=0.02)
THROW = dict(raised=dict(hand=(-0.46, 0.2, 0.8), tip=80.0, swing=0.0, other=(0.14, -0.5, 0.62),
                         lean=-8.0, twist=-35.0, lunge=-0.06, drop=0.0),
             hit=dict(hand=(-0.18, -0.42, 0.88), tip=90.0, swing=0.0, other=(0.3, -0.15, 0.32),
                      lean=12.0, twist=15.0, lunge=0.08, drop=0.02),
             through=dict(hand=(0.05, -0.4, 0.3), tip=140.0, swing=20.0, other=(0.32, 0.0, 0.28),
                          lean=22.0, twist=30.0, lunge=0.1, drop=0.05))

# Drawing a weapon from a holster slot (draw_at), and putting it back (the game plays the same clip backwards): the hand
# comes from hanging round the body to the weapon's handle where it sits in the slot, closes on it (DRAW_AT of the way
# through), pulls it clear and brings it round to the guard (the weapon's drafted stance). Nothing here says where the
# handle is: draw_keys works it out from HOLSTERS and the hold of the type's placeholder (HOLSTER_WEAPON), so a slot or a
# hold moved only wants a new export. A handle too far off is met first by turning and bending the trunk (up to
# DRAW_TWIST, DRAW_LEAN, DRAW_SIDE degrees) and dropping the shoulder (up to DRAW_SHOULDER), and whatever gap is left the
# game closes by swinging the weapon to the hand over the last 'slide' of the reach, as a slung weapon is pulled round;
# so does any weapon that isn't quite where its placeholder is (another length, a coat under it, a backpack).
DRAW_FRAMES = 24
DRAW_AT = 0.45       # share of the clip at which the hand closes on the handle
DRAW_LOOK = 0.7      # share of the trunk's side bend the head takes back (STRIKE_LOOK the rest)
DRAW_TWIST = 40.0
DRAW_LEAN = 30.0
DRAW_SIDE = 20.0
DRAW_SHOULDER = 30.0
DRAW_BEND_COST = 0.001   # what a degree of trunk squared costs against a leg length of reach squared, when choosing how far to bend
DRAW_UPPER = {'spine.001': 1.0}   # in place of UPPER: the trunk from the waist up, and the arm that draws
# By slot size: 'seconds' (game seconds to draw), 'approach' (leg lengths the hand comes in from along the slot's 'out'),
# 'out' and 'along' (leg lengths it pulls the weapon clear: out, and back along the weapon's line) and 'slide' (the share
# of the clip the game swings the weapon to the hand over), 'turn_from' (the share of the way from the handle to the guard
# the weapon keeps the slot's slant for: a long one has to be round the side of the body before it swings to the front,
# or its far end goes through the trunk).
DRAW_SIZES = {
    'small': dict(seconds=0.55, approach=0.08, out=0.06, along=0.2, slide=0.08, turn_from=0.25),
    'big': dict(seconds=0.95, approach=0.15, out=0.25, along=0.0, slide=0.2, turn_from=0.55),
    'grenade': dict(seconds=0.5, approach=0.08, out=0.12, along=0.0, slide=0.08, turn_from=0.25),
}
# The weapon each type's clips are posed for, whose handle the hand goes to and whose guard it ends in.
HOLSTER_WEAPON = {'handgun': 'Gun_Autopistol', 'rifle': 'Gun_AssaultRifle', 'bow': 'Bow_Recurve', 'grenade': 'Weapon_GrenadeFrag',
                  'melee_1h': 'MeleeWeapon_Gladius', 'melee_2h': 'MeleeWeapon_LongSword'}
GROUND_RATIO = 1.36  # WalkFigures.GroundRatio for most of IsoRim's models: a weapon's sprite is drawn this much smaller than TYPE_SCALE says

# Sitting (sit_at): thighs forward and nearly level, shins hanging down, hands resting on the thighs, breathing as the
# idle does. The game puts the seat under whatever is lowest behind the hips (SkinnedFigure.SeatOf), so the height the
# pose has here does not matter. Hand places are in thigh lengths from the hip joint on its side: x in toward the
# middle, y forward along the thigh, z up.
SIT_SECONDS = 6.0
SIT_BREATHS = 2
SIT_THIGH = 6.0      # thighs tipped down from level toward the knee (degrees)
SIT_SHIN = 8.0       # shins swung forward from straight down (degrees)
SIT_KNEES = 0.1      # knees turned out from straight ahead (share of the thigh's length)
SIT_LEAN = -2.0      # forward lean of the spine, in place of LEAN (back: a little reclined)
SIT_HANDS = (0.15, 0.6, 0.25)

# Lying (lie_at): on the back. The game lays the figure down (as IsoRim lays its own), so here it stands straight: legs
# straight and nearly together, arms down the sides, toes pointed so the feet lie along the legs, breathing slowly. A
# corpse is shown at its first frame, which is out of breath.
LIE_SECONDS = 8.0
LIE_BREATHS = 2
LIE_NARROW = 0.8     # in place of NARROW
LIE_POINT = 45.0     # toes pointed (degrees of heel up)
LIE_ARMS = 6.0       # arms brought in closer to the sides than the walk's (degrees on top of ARM_DOWN)

# Ragdoll (ragdoll_pose): the pose the game's ragdoll (Ragdoll.cs) pulls each joint gently toward once a body has gone
# limp, loosely the neutral body posture a body takes when nothing holds it up (floating in microgravity): the back curled
# a little and the head bowed, the shoulders rolled forward, the arms a little out and forward with the elbows bent, the
# hips and knees bent, the feet pointed and the hands half closed. Only each bone's turn on its parent's is used, so it is
# posed standing. Degrees.
RAGDOLL_CURL = 10.0    # the back curled forward from the waist
RAGDOLL_NOD = 12.0     # the head bowed forward on the neck
RAGDOLL_ROLL = 8.0     # the shoulders rolled forward
RAGDOLL_ARMS = 25.0    # the arms swung out from hanging at the sides
RAGDOLL_REACH = 20.0   # and forward
RAGDOLL_ELBOW = 45.0
RAGDOLL_HIP = 35.0     # the thighs swung forward
RAGDOLL_KNEE = 50.0
RAGDOLL_POINT = 25.0   # the feet pointed (heel up)
RAGDOLL_HAND = 0.35    # the hands closed, a share of FIST

# Caged (caged_pose): a corpse in a gibbet cage, slumped where it stands on the cage floor (WalkFigures.DrawCaged): the
# knees buckled and the hips sunk over the planted feet, the back curled and the head hung forward and lolled to one
# side, the shoulders rolled forward, the arms hanging straight down by the sides, the hands half closed. Degrees but
# CAGED_DROP (leg lengths).
CAGED_DROP = 0.2      # the hips let down, the knees bent to keep the feet where they stand
CAGED_NARROW = 0.85   # the feet drawn in under the hips (NARROW)
CAGED_CURL = 22.0     # the back curled forward from the waist
CAGED_NOD = 30.0      # the head hung forward on the neck
CAGED_LOLL = 14.0     # and fallen over to its left
CAGED_ROLL = 12.0     # the shoulders rolled forward
CAGED_ARMS = 6.0      # the arms in closer to the sides than the walk's (on top of ARM_DOWN)
CAGED_ELBOW = 8.0     # the elbows all but straight
CAGED_HAND = 0.3      # the hands closed, a share of FIST

# Crawling (crawl_at): downed, pulling along on the belly. One hand reaches ahead while the other pulls back, and the leg
# on the other side is drawn up and out to push. Made upright like lying (the game tips it face down, by its own crawl
# angle, Crawl.ChestLift), so ahead is up here and the ground is in front. Timed by the ground covered, like the walk: the
# hand pulling back drags the body its reach, twice a cycle (crawl_ground_per_cycle). Distances in arm lengths
# (upper arm + forearm), leg lengths for the legs.
CRAWL_FRAMES = 24
CRAWL_REACH = 0.6    # how far a hand moves from pulled back to reached ahead
CRAWL_HANDS = (0.15, 0.45, -0.2)   # the pulled-back wrist from the shoulder: x out, y toward the ground, z ahead
CRAWL_DRAW = 0.3     # how far a foot is drawn up (and half that out to the side)
CRAWL_LOOK = 55.0    # neck and head tipped back so the face looks ahead when face down (degrees)
CRAWL_ROLL = 6.0     # the body rolled toward the drawn-up leg (degrees)
# With crawl_clip.json beside this file (import_crawl.py: one loop of a mocap crawl on hands and knees), crawl_at plays
# that in place of the belly crawl above, timed by the ground it covers. It is tipped back up by CRAWL_TIP, the turn the
# game lays the crawl down by (90 less Crawl.ChestLift, 15), so it comes out on hands and knees as it was made.
CRAWL_TIP = 75.0
try:
    import json, os
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'crawl_clip.json')) as _f:
        CRAWL_CLIP = json.load(_f)
    CRAWL_FRAMES = CRAWL_CLIP['frames']
except FileNotFoundError:
    CRAWL_CLIP = None

# Kneeling at work on the ground (pluck_at, work_pluck: sowing by hand, foraging, pulling weeds): pluck_clip.json beside
# this file (import_pluck.py: one loop of Meshy's kneel, down on the left knee with both hands busy in front), played on
# the clock and set on the floor, the trunk leant over toward the raised right leg (the clip leans over the kneeling
# one) and the right hand pawing at the ground in front of it: dragged back hard along it, then lifted and reached
# forward again. The chibi's arms are short for this kneel, so the stoop is what lets the hand reach the floor.
# Distances in leg lengths.
PLUCK_LEAN = 12.0    # degrees the trunk is leant from the waist to the right, toward the raised leg
PLUCK_STOOP = 8.0    # and forward, on top of the clip's own stoop
PLUCK_PAWS = 3       # paws a loop
PLUCK_DRAG = 0.4     # share of each paw the hand drags along the ground (quick), the rest lifting it forward (slower)
PLUCK_PAW = (0.05, 0.22, 0.14)   # the right wrist's path from under the right shoulder: x out to the right, y forward
                                 # at the start of the drag, how far it drags back
PLUCK_LOW = 0.13     # the wrist's height above the floor while it drags (the curled fingers on the ground)
PLUCK_HIGH = 0.2     # and at the top of the lift
PLUCK_POINT = 20.0   # the hand pointed down from level (degrees), the palm to the ground, cocked back off the
                     # steep forearm so the curled fingers scrape
PLUCK_CURL = (0.8, 0.35)   # the fingers curled while dragging and while lifting (shares of FIST)
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'pluck_clip.json')) as _f:
        PLUCK_CLIP = json.load(_f)
    PLUCK_SECONDS = PLUCK_CLIP['frames'] / PLUCK_CLIP['fps']
except FileNotFoundError:
    PLUCK_CLIP = None
# Kneeling by a patient (tend_at, work_tend: tending, feeding, visiting the sick): tend_clip.json beside this file
# (import_pluck.py --as tend: the same kneel, both hands busy in front, cut before its last stretch, where the left hand
# digs forward), played on the clock as it is, set on the floor as pluck's is.
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'tend_clip.json')) as _f:
        TEND_CLIP = json.load(_f)
    TEND_SECONDS = TEND_CLIP['frames'] / TEND_CLIP['fps']
except FileNotFoundError:
    TEND_CLIP = None
# Playing with a baby by a toy box (toys_at, work_toys: Biotech's PlayToys, the baby set down on the floor): the tend kneel
# as it is, with the right hand held out over the baby lying in front and waved slowly from side to side, bobbing as if
# dangling a toy, the fingers wiggling. Distances in leg lengths, the wrist's place from the right shoulder: x out to
# the right, y forward, z down from it. The tend clip's hands are about 0.13 forward of the shoulder and 0.33 under it.
TOYS_WRIST = (0.04, 0.26, 0.27)   # the middle of the wave
TOYS_SWAY = 0.12     # how far the wrist goes either side of it
TOYS_BOB = 0.04      # up and down, twice a sway (it dips at each end)
TOYS_WAVES = 3       # sways a loop (the tend loop's TEND_SECONDS)
TOYS_WIGGLES = 8     # the fingers' wiggles a loop
TOYS_CURL = (0.1, 0.5)   # the fingers' curl between (shares of FIST)
TOYS_POINT = 25.0    # the hand pointed down from level (degrees), the palm to the baby
TOYS_ROLL = 20.0     # the hand rolled with the sway (degrees either way), leading it
# Drawing on the floor in chalk (chalk_at, work_chalk: Biotech's Floordrawing, a child knelt on its cell drawing on the
# one in front): the pluck kneel (PLUCK_CLIP, on its clock), stooped further, the left palm propped on the floor and the
# right hand holding a stick of chalk (Models/tool_chalk.obj, make_chalk.py) whose tip is kept on the floor, scribbling
# loops while it sweeps from side to side, lifted once a loop to start again. Places on the floor from under the hips in
# the figure's own frame (x out to the left, y back: the game faces it at the cell it draws on), as the clip's chest is
# turned well to the left; the hands' ways in a level frame turned as the chest is (read_level). Leg lengths.
CHALK_LEAN = 6.0      # degrees the trunk is leant from the waist to the right, toward the drawing hand
CHALK_STOOP = 20.0    # and forward, on top of the clip's own stoop
CHALK_LOOK = 14.0     # the head bowed further, to look at the drawing (degrees)
CHALK_MIDDLE = (0.0, -0.88)   # the middle of the drawing: out in front
CHALK_SWEEP = 0.1     # how far the hand sweeps either side of it, once a loop
CHALK_LOOPS = 5       # loops scribbled a loop (a whole number, so it closes)
CHALK_RADIUS = (0.045, 0.03)   # each loop's size across and along
CHALK_LIFT = 0.06     # how far the tip is lifted off the floor once a loop
CHALK_LIFT_AT = (0.42, 0.16)   # where in the loop it lifts, and for how long (shares of the loop)
CHALK_FLOOR = 0.004   # the tip's height over the floor while it draws
CHALK_FINGERS = (0.1, -1.0, -0.35)   # the right fist's fingers' way: forward and a little down
CHALK_FACING = (1.0, 0.0, -0.15)   # the right palm's facing: in toward the middle, so the thumb is up
CHALK_ELBOW = (-0.8, 0.4, -0.4)   # the right elbow out and back (chest's frame)
CHALK_CURL = 0.95     # the right fingers' curl round the chalk (shares of FIST)
CHALK_PROP = (0.3, -0.6)   # the left palm on the floor, out to the left in front
CHALK_PROP_FINGERS = (-0.25, -1.0, 0.0)   # its fingers' way: forward, a little in
CHALK_PROP_ELBOW = (0.8, 0.5, -0.3)   # the left elbow out and back (chest's frame)
CHALK_GRIP = 0.0      # where the chalk crosses the palm: shares of the fingers' length from their joint (hand.R.001's head)
CHALK_PALM = 0.045    # and out from the palm (leg lengths), inside the curled fingers
CHALK_TIP = 10.0      # degrees its back end is tipped toward the wrist from straight across the fist
CHALK_OUT = 0.05      # how far the tip stands out of the little finger's side (its model's units)
CHALK_SIZE = 1.0      # the chalk model's size (its own units are cells at a grown pawn's size)

# Vomiting (vomit_at, work_vomit: the Vomit job): vomit_clip.json beside this file (import_pluck.py --as vomit --planted:
# the end of Meshy's Catching_Breath, bent over with the hands on the knees, panting), played on the clock and set on
# the floor as the kneels are, with a heave once a loop: the upper chest and the neck thrown forward and down quickly,
# then eased back. The heave is kept above the shoulders' bone so the hands stay near the knees. The clip's legs are
# left out for the chibi's own, planted as it stands (vomit_at).
VOMIT_HEAVE = (6.0, 22.0)   # degrees the upper chest (spine.003) and the neck are thrown forward at the top of a heave
VOMIT_AT = 0.08     # where in the loop a heave starts (into the clip's deepest breath)
VOMIT_RISE = 0.1    # share of the loop the throw takes
VOMIT_FALL = 0.4    # and the easing back
VOMIT_KNEE = 0.03   # how far short of straight the legs are kept at least (leg lengths), so the knees stay soft
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'vomit_clip.json')) as _f:
        VOMIT_CLIP = json.load(_f)
    VOMIT_SECONDS = VOMIT_CLIP['frames'] / VOMIT_CLIP['fps']
except FileNotFoundError:
    VOMIT_CLIP = None
# Getting up (getup_at: off the ground when no longer downed, out of bed on waking): getup_clip.json beside this file
# (import_pluck.py --as getup --once: Meshy's Arise, from lying on the back to standing, cut before it steps back),
# played once through, not round; the game plays it fast, as it stands a pawn up at once, and rests it on its lowest
# point. Here each frame is set on the floor by its lowest joint.
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'getup_clip.json')) as _f:
        GETUP_CLIP = json.load(_f)
    GETUP_SECONDS = GETUP_CLIP['frames'] / GETUP_CLIP['fps']
except FileNotFoundError:
    GETUP_CLIP = None
def clip_file(name):
    """<name>_clip.json beside this file (import_pluck.py --as name), or None without it."""
    try:
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), name + '_clip.json')) as f:
            return json.load(f)
    except FileNotFoundError:
        return None

# Meditating (meditate_at, work_meditate: the game's Meditate and MeditatePray jobs): meditate_clip.json beside this file
# (`import_pluck.py -- <glb> Sit_Cross_Legged_on_Floor 0 59 --as meditate`: Meshy's Sit_Cross_Legged_on_Floor, sat
# cross-legged on the floor, the hands on the knees, breathing; its Sit_Cross_Legged is sat on a chair, one leg over the
# other), played on the clock as it is, set on the floor as the kneels are and then lifted MEDITATE_LIFT, as sat on the
# floor the seat goes lower than the toes, which would sink it through.
MEDITATE_LIFT = 0.08   # leg lengths (the base body's seat is 0.076 below the floor with the toes on it)
MEDITATE_CLIP = clip_file('meditate')
MEDITATE_SECONDS = MEDITATE_CLIP['frames'] / MEDITATE_CLIP['fps'] if MEDITATE_CLIP is not None else None

# Praying sat on a chair or throne (pray_sit_at, work_meditate_sit: the game's MeditatePray on a seat, which otherwise
# keeps the plain sit): the sit's loop with the palms pressed together in front of the chest, the fingers up, the trunk
# leant a little forward and the head bowed, laid over the sit from the waist up (EAT_UPPER), as seated work is.
PRAY_SECONDS = SIT_SECONDS   # one loop, the sit's, so its breathing closes
PRAY_LEAN = 6.0        # degrees the trunk is leant forward from the waist
PRAY_BOW = 16.0        # degrees the head is bowed, half at the neck and half at the head
PRAY_BOB = 2.0         # degrees the bow deepens and lifts with each breath
PRAY_HANDS = (0.0, -0.32, -0.25)   # where the palms meet: leg lengths from the middle between the shoulder joints
                                   # (x left, y back, z up, level and turned as the chest is)
PRAY_FINGERS = (0.0, -0.45, 1.0)   # the fingers' way (the same frame): up, tipped forward
PRAY_PALM = (-1.0, 0.0, 0.0)       # the left palm's facing (the right mirrored): in, onto the other
PRAY_CURL = 0.08                   # the fingers all but straight
PRAY_ELBOW = (0.8, 0.3, -0.7)      # the left elbow out and down (the right mirrored)

# Lying loops from mocap clips made lying down (lying_at): asleep (Meshy's Sleep_Normally, on the back), downed in bed
# (Groan_Holding_Stomach_in_Sleep, on the back, the knees drawn up and let down) and downed on the ground
# (SideLying_Reach_Help on its side, cut before it props itself up to reach out), each `import_pluck.py ... --as <name>`.
# Made upright as lie_at is, so the game lays them down the way it lays the lie loop: the head (the clip's mean way from
# the hips to the neck) turned to point back, then the whole tipped up about x, so lying down undoes it.
SLEEP_CLIP = clip_file('sleep')
DOWNED_CLIP = clip_file('downed')
DOWNED_SIDE_CLIP = clip_file('downed_side')
# Asleep on the side (sleep~2, a VARIANTS entry: SLEEP_CLIP set to DOWNED_SIDE_CLIP and SLEEP_HOLD on): the downed
# side lying without its writhing, held on one frame of it (SLEEP_HOLD_FRAME, the frame nearest the clip's mean pose,
# measured over every bone's turn) and breathing slowly from the chest (SLEEP_BREATHS a loop, the upper chest turned
# SLEEP_BREATH degrees about its own side-to-side line, the neck SLEEP_BREATH_NECK back against it so the head stays).
SLEEP_HOLD = False
SLEEP_HOLD_FRAME = 18
SLEEP_BREATHS = 2
SLEEP_HOLD_SECONDS = 8.0   # the held loop's length (2 breaths in 8 s: a sleeper's 15 a minute)
SLEEP_BREATH = 2.5
SLEEP_BREATH_NECK = 1.5
# At lovin in a bed with no blanket over it (lovin_L, lovin_R: the partner on the figure's left or right; WalkFigures
# draws the two toward each other): the same still side lying, facing the partner (mirrored left for right where the
# clip faces the other way, mirror_pose), breathing a little faster.
LOVIN_SECONDS = 6.0
LOVIN_BREATHS = 3
# A baby awake on its back (baby_at): the lie loop's trunk and breathing, with the arms waving and the legs kicking laid
# on it. The arms are posed by hand: they flail all the while (BABY_FLAIL: each arm's raise forward, swing out and
# elbow bend, and how far the fingers curl, wandering about a middle by a few sines a loop, each a whole number of times
# round so it closes, of random weights and phases), and BABY_MOVES are laid over that in turn, each eased in over its
# `rise` and out over its `fall` (shares of the loop): reaching up for the sky with both hands grasping, a fist to the
# mouth (the wrist brought by IK to BABY_MOUTH, in the head's frame as EAT_MOUTH is), a startle (the arms flung out wide,
# the fingers spread, then let back slowly) and batting at the air in turn (BABY_BATS swings, each arm BABY_BAT up and
# down, BABY_SPREAD out and in, the elbow BABY_BAT_BEND with it). Raised is forward, so laid down it is up at the sky.
# The legs are the crawl's (CRAWL_CLIP, on hands and knees: the thighs come forward and the shins hang, so on the back
# they are drawn up and pump in turn), from the hips down, BABY_KICKS of its cycles a loop, kicking all the while
# (easing them in and out of a still pose flipped the toes over in a frame).
BABY_SECONDS = 10.0
BABY_KICKS = 10         # the crawl's leg cycles a loop (a whole number, so it closes)
BABY_ARMS_TIP = 30.0    # degrees the arms are raised forward under everything else
# Each channel's middle, and how far it wanders either way (degrees; the fingers 0 open .. 1 a fist).
BABY_FLAIL = dict(reach=(20.0, 18.0), spread=(12.0, 10.0), bend=(55.0, 25.0), fist=(0.55, 0.35))
BABY_FLAIL_TIMES = (3, 4, 7, 11)   # times round a loop of the sines each channel is made of
BABY_MOVES = [
    dict(move='reach', at=0.04, to=0.24, rise=0.05, fall=0.05),
    dict(move='mouth', side='R', at=0.30, to=0.50, rise=0.05, fall=0.05),
    dict(move='startle', at=0.56, to=0.68, rise=0.015, fall=0.08),
    dict(move='bat', at=0.72, to=0.97, rise=0.05, fall=0.05),
]
BABY_REACH = dict(reach=80.0, spread=-6.0, bend=15.0)   # reaching for the sky
BABY_GRASPS = 3         # times the hands open and close reaching up
BABY_MOUTH = (0.0, -0.3, -0.06)   # where the fist is brought to: leg lengths from the head's joint in the head's frame
BABY_STARTLE = dict(reach=55.0, spread=60.0, bend=10.0, fist=0.0)
BABY_BAT_ARMS = dict(reach=50.0, spread=12.0, bend=30.0)
BABY_BATS = 4           # swings of each arm while batting
BABY_BAT = 25.0         # degrees each arm swings up and down, in turn
BABY_SPREAD = 12.0      # degrees each arm swings out and in with it
BABY_BAT_BEND = 15.0    # degrees the elbow bends and straightens with the swing
# Falling (fall_at): played once, from standing to lying, when a pawn drawn standing goes down (knockdown: Meshy's
# Stand_To_Side_Lying, ending on its side as DOWNED_SIDE_CLIP lies) or dies (die: dying_backwards, ending on its back
# with the arms flung out), each `import_pluck.py ... --fall`. Made upright as the lying loops are, so the game lays it
# down the same way and can blend it into the loop it ends in (FALL_INTO); its last frame is turned and placed as that
# loop's first lies. A corpse lies as the death fall ends (dead_at).
KNOCKDOWN_CLIP = clip_file('knockdown')
DIE_CLIP = clip_file('die')
FALL_INTO = {'knockdown': 'downed_side', 'die': 'dead'}
# Every way of falling, each a variant of both falls above (VARIANTS). electrocuted: the end of Meshy's Electrocuted_Fall,
# from as the head goes back after the flail: it slumps to its knees and pitches onto its face. Only a ragdoll is drawn
# from another fall's end, so the game plays a fall's own clip with ragdolls off (it ends in the loop it blends into).
ELECTROCUTED_CLIP = clip_file('electrocuted')
# shot_fall: Meshy's Shot_and_Fall_Backward, from just before the hit (the trunk thrown back, a moment on its feet, then
# dropped onto its back), stopped before it writhes. falling_down: Meshy's falling_down, from the first give of the knees,
# stopped where it has settled on its back, before the head lifts again.
SHOT_FALL_CLIP = clip_file('shot_fall')
FALLING_DOWN_CLIP = clip_file('falling_down')
for clip in (ELECTROCUTED_CLIP, SHOT_FALL_CLIP, FALLING_DOWN_CLIP):
    if clip is not None:
        clip.pop('rest_ways', None)   # turned from the glTF's rest ways its feet point like a dancer's, 27 degrees off the shin
FALL_CLIPS = [clip for clip in (KNOCKDOWN_CLIP, DIE_CLIP, ELECTROCUTED_CLIP, SHOT_FALL_CLIP, FALLING_DOWN_CLIP) if clip is not None]
# Mocap work strokes, standing on the floor in place of a WORK kind's stroke (WORK_MOCAPS, by kind), or as one of its
# VARIANTS (WORK_MOCAP, set by the variant): chop (Meshy's Right_Hand_Sword_Slash, a level swing across, for felling a
# tree with the axe; the only axe stroke) and gather (Female_Crouch_Pick_Fruit_Basket_Stand, crouched and picking at the
# ground with the right hand, for harvesting by hand in place of the scythe). WORK_MOCAP_TOOL: whether the kind's tool
# rides the hand as it does in the stroke.
# The slash follows through round to the left, but an axe stops in the trunk: the chop is played from CHOP_FROM (the clip's
# frame near the top of the backswing; before it the weapon is held out level in front) up to CHOP_HIT (where the blade
# comes across the front), and goes straight back up to CHOP_FROM over CHOP_BACK frames (all at the clip's fps), so the
# only stop in the loop is the blow itself. CHOP_LEAVE and CHOP_ARRIVE: how fast that way back starts and ends, against
# an even pace, so it doesn't dwell at the trunk and runs on into the swing.
CHOP_FROM = 8
CHOP_HIT = 13.4
CHOP_BACK = 20
CHOP_LEAVE = 2.0
CHOP_ARRIVE = 1.0
# The right wrist turns as the axe comes in, so the blade goes into the trunk rather than across it: at the hit the edge
# points CHOP_BLADE (armature space: -y the way the figure faces the trunk, +x its left, a level blow). Turned in from
# CHOP_TURN_FROM frames of the clip and out again over CHOP_TURN_BACK frames of the way back.
CHOP_BLADE = (0.5, -0.85, 0.0)
CHOP_TURN_FROM = 10
CHOP_TURN_BACK = 8
# Away from the hit (the rest of the wrist's turn's share), the whole right arm is swung CHOP_OUT degrees from the shoulder
# toward the figure's right, so the axe goes round the chibi's big head rather than through it.
CHOP_OUT = 35.0
# The clip leans the trunk well over to the right, so going straight back from the hit the axe went through the head,
# and again at the top of the wind-up beside the right ear. So meanwhile the arm is swung CHOP_CLEAR degrees about the
# shoulder taking the wrist away from the head (the hand keeping its way), and the axe tipped CHOP_TIP degrees about the
# wrist taking its head away from it, so it comes back under the chin and up outside the hair. Eased in over
# CHOP_CLEAR_IN frames after the hit, held round the loop and eased out over CHOP_CLEAR_OUT frames up to CHOP_CLEAR_TO
# frames into the swing (frames of CHOP_CLIP, whose frame 0 is CHOP_FROM), as from there the blade is on its way forward.
# Measured on the base body: the axe stays 0.12 cells off the head at a grown pawn's standard size.
CHOP_CLEAR = 35.0
CHOP_TIP = 60.0
CHOP_CLEAR_IN = 3.0
CHOP_CLEAR_OUT = 1.5
CHOP_CLEAR_TO = 5.0
# The ways those swings (and CHOP_OUT's) are made about are smoothed round the loop (see chop_axes), sampled
# CHOP_AXIS_STEPS times a frame: over CHOP_AXIS_SMOOTH frames (a Gaussian's spread) on the swing, where they keep the axe
# off the head, and CHOP_AXIS_BACK on the way back (nearly one way all the way: taken afresh each frame there, the arm
# dropped and rose again and snapped round where it passed in line with the head), eased between over CHOP_AXIS_EASE
# frames. Measured on the base body: on the way back the wrist now dips about a quarter of a head's height as the
# blade comes out of the trunk, then rises in one arc into the wind-up.
CHOP_AXIS_SMOOTH = 2.0
CHOP_AXIS_BACK = 16.0
CHOP_AXIS_EASE = 3.0
CHOP_AXIS_STEPS = 4
# Leg lengths the whole stroke, feet and all, is set forward toward the trunk, so the blade ends in it: the clip's
# reach alone stopped it 0.75 cells ahead of the pawn's middle (a grown pawn at the standard size), short of a tree in
# the next cell; this takes it to 0.85.
CHOP_FORWARD = 0.18
# The clip's legs were made for the glTF rig's, and on the chibi leave the heels up, the toes pointing 28 degrees at the
# sky and the back ankle sliding about under a foot that rolls and swings. So only the hips and up are the clip's: each
# foot is planted flat for the whole stroke, at the ankle's mean place in the clip, the knee found by IK over its toes.
# CHOP_TOE_L and CHOP_TOE_R: degrees each foot is turned out from rest, the back one as a staggered stance has it.
# CHOP_HEEL_L and CHOP_HEEL_R: degrees the heel is up, the foot pivoting on the ball and the toes flat, as flat on the
# floor the back shin leans so far forward it lies on the instep.
# CHOP_SET_L and CHOP_SET_R: leg lengths each foot is set back from the clip's mean, which opens the back knee.
# CHOP_REACH: a foot is drawn in along the floor until its hip stays within this share of the leg at every frame, as the
# lunge into the hit would otherwise pull the back leg past straight.
CHOP_TOE_L = 0.0
CHOP_TOE_R = 25.0
CHOP_HEEL_L = 0.0
CHOP_HEEL_R = 30.0
CHOP_SET_L = 0.0
CHOP_SET_R = 0.15
CHOP_REACH = 0.97

def clip_frame(clip, f):
    """A glTF clip's turns, hips and trunk line at frame f (fractional, eased between the frames either side)."""
    n = clip['frames']
    i = int(math.floor(f)) % n
    j, u = (i + 1) % n, f - math.floor(f)
    return clip_blend(clip, i, j, u)

def clip_blend(clip, i, j, u):
    """A glTF clip's turns, hips and trunk line u of the way from frame i to frame j."""
    turns = {b: list(Quaternion(q[i]).slerp(Quaternion(q[j]), u)) for b, q in clip['turns'].items()}
    hips = list(Vector(clip['hips'][i]).lerp(Vector(clip['hips'][j]), u))
    trunk = [list(Vector(a).lerp(Vector(b), u)) for a, b in zip(clip['trunk'][i], clip['trunk'][j])] \
        if 'trunk' in clip else None
    return turns, hips, trunk

def stopped_clip(clip):
    """The chop (see CHOP_HIT) as a clip of its own: the swing up to the hit and straight back to its start. Its 'turn' is
    each frame's share of the wrist's turn (chop_at)."""
    if clip is None:
        return None
    ease = lambda t: t * t * (3 - 2 * t)
    # The way back, from 0 to 1 at s from 0 to 1: a cubic through the two ends leaving and arriving at those paces.
    back = lambda s: ease(s) + CHOP_LEAVE * s * (1 - s) ** 2 - CHOP_ARRIVE * s * s * (1 - s)
    swing = list(range(CHOP_FROM, int(math.ceil(CHOP_HIT))))
    frames = [clip_frame(clip, f) for f in swing]
    frames.append(clip_frame(clip, CHOP_HIT))
    share = [ease(min(max((f - CHOP_TURN_FROM) / (CHOP_HIT - CHOP_TURN_FROM), 0.0), 1.0)) for f in swing] + [1.0]
    out = dict(clip, action=clip['action'] + ' stopped', last=-1, frames=len(frames) + CHOP_BACK - 1, hit=len(frames) - 1)
    # Going back: from the hit to the first frame, by a clip of just those two.
    ends = {'turns': {b: [frames[-1][0][b], frames[0][0][b]] for b in clip['turns']},
            'hips': [frames[-1][1], frames[0][1]]}
    if 'trunk' in clip:
        ends['trunk'] = [frames[-1][2], frames[0][2]]
    # The hand goes back on the forearm rather than on its own, or the two take different ways round and the wrist folds.
    wrist = [Quaternion(f[0]['forearm.R']).inverted() @ Quaternion(f[0]['hand.R']) for f in (frames[-1], frames[0])]
    for k in range(1, CHOP_BACK):
        u = back(k / CHOP_BACK)
        frame = clip_blend(ends, 0, 1, u)
        frame[0]['hand.R'] = list(Quaternion(frame[0]['forearm.R']) @ wrist[0].slerp(wrist[1], u))
        frames.append(frame)
    share += [1 - ease(min(k / CHOP_TURN_BACK, 1.0)) for k in range(1, CHOP_BACK)]
    out['turns'] = {b: [f[0][b] for f in frames] for b in clip['turns']}
    out['hips'] = [f[1] for f in frames]
    if 'trunk' in clip:
        out['trunk'] = [f[2] for f in frames]
    out['turn'] = share
    return out

CHOP_CLIP = stopped_clip(clip_file('chop'))
GATHER_CLIP = clip_file('gather')
WORK_MOCAPS = {'axe': CHOP_CLIP} if CHOP_CLIP is not None else {}
WORK_MOCAP = None
WORK_MOCAP_TOOL = True

# Rubbing the hands for warmth (rub_at: a pawn with a cold hediff, now and then, standing or walking): rub_clip.json
# beside this file (import_pluck.py --as rub Scheming_Hand_Rub 0 78: Meshy's hand rub, the hands brought together in
# front of the chest, rubbed and let down again, so the loop starts and ends with them hanging). Only the waist up is
# taken (RUB_UPPER): the clip's feet are angled wrong, so the legs and hips are the standing pose's. The game plays it
# once through for each bout (WalkFigures.RubOf).
RUB_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'rub_clip.json')) as _f:
        RUB_CLIP = json.load(_f)
    RUB_SECONDS = RUB_CLIP['frames'] / RUB_CLIP['fps']
except FileNotFoundError:
    RUB_CLIP = None

# Talking (talk_at: board games, giving a speech, and now and then at a party): talk_clip.json beside this file
# (import_pluck.py --as talk Stand_Talking_Angry 26 149: the first six seconds or so of Meshy's angry talk, from after
# it lifts its arms out of the rest pose, to where it closes best). From the waist up only (TALK_UPPER), as the rub is,
# so the game can lay it over the idle, the sit or the walk (WalkFigures.TalkOf).
TALK_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up
TALK_NECK = ('spine.004', 'spine.005')   # the neck's bones, bent on the chest as the standing pose bends them (talk_at)
TALK_NOD = 0.6   # share of the clip's nodding (about its mean, which is bowed) kept on the levelled head
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'talk_clip.json')) as _f:
        TALK_CLIP = json.load(_f)
    TALK_SECONDS = TALK_CLIP['frames'] / TALK_CLIP['fps']
except FileNotFoundError:
    TALK_CLIP = None

# Reloading (reload_at: Combat Extended's ReloadWeapon job): reload_clip.json beside this file (import_pluck.py --as
# reload Standing_Reload 0 78: Meshy's standing reload, the rifle kept shouldered in the right hand while the left goes
# down to the belt for a fresh magazine and puts it in, starting and ending in the held pose). From the waist up
# (RELOAD_UPPER), laid over the drafted stance; the game stretches it over the reload's own time (WalkFigures.ReloadOf).
RELOAD_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up
RELOAD_CLIP = clip_file('reload')
RELOAD_SECONDS = RELOAD_CLIP['frames'] / RELOAD_CLIP['fps'] if RELOAD_CLIP is not None else None

# Eating (eat_sit_at, eat_stand_at: the Ingest job, once the pawn has its meal and stands or sits still with it). Seated
# at a table, eat_sit_clip.json (`import_pluck.py -- <glb> Sit_and_Drink 28 182 --as eat_sit`: Meshy's Sit_and_Drink
# without the cup fetched from beside the hip and put back), laid over the sit from the waist up, the right hand eating
# from the table and the left on the lap. Standing, eat_stand_clip.json (`import_pluck.py -- <glb> Stand_and_Drink 0 211
# --as eat_stand --mirror`: drunk with the left hand, mirrored so it eats with the right as seated), laid over the idle
# the same way, the left hand holding the meal in front of the chest. Both clips drink from a cup, the head bowed to it and then tipped back, the hand lifted over the face; so the
# head's nod is taken out (a share of it seated, where it looks down at the table) and the eating hand is only brought
# from where it rests to just under the mouth, as far as the clip's own hand has risen, its timing and turn kept.
EAT_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up
EAT_LEVEL = (0.5, 1.0)   # share of the clip's nod taken out, about the neck, seated and standing: the head kept leaning as
                         # the sit's or the standing pose's does
EAT_MOUTH = (0.04, -0.33, -0.1)   # where the eating wrist is brought to: leg lengths from the head's joint in the head's
                                  # rest frame, x out (+ toward the eating hand's side), y back (- forward), z up; under
                                  # and in front of the mouth
EAT_TABLE = (-0.1, -0.34, 0.34)  # where the right wrist rests seated, at the meal on the table: leg lengths from the
                                  # middle of the hips at rest in the chest's frame, x out (- is right), y back, z up
EAT_MEAL = (0.03, -0.26, 0.42)  # standing, the left palm holding the meal, the same way
EAT_MEAL_FINGERS = (-0.6, -1.0, 0.0)   # the way the left fingers point under the meal (chest's frame), palm up
EAT_MEAL_CURL = 0.0   # share of FIST the left fingers curl under it: flat, as more pokes them up through the tray
EAT_SIT_CLIP = clip_file('eat_sit')
EAT_SIT_SECONDS = EAT_SIT_CLIP['frames'] / EAT_SIT_CLIP['fps'] if EAT_SIT_CLIP is not None else None
# A second way of eating at a table, as one of eat_sit's VARIANTS: eat_sit2_clip.json (`import_pluck.py -- animDLs/
# sittingeat2.glb _01a17c6c_b5eb_7247_a1be_79e70ad1c6fe --as eat_sit2`, frames 35 .. 94: both hands at the plate with a
# knife and fork, the head bowed down to it). Its own arms and nod are kept as they are (EAT_REACH off, no EAT_LEVEL).
EAT_SIT2_CLIP = clip_file('eat_sit2')
EAT_REACH = True   # whether the eating wrist is brought to the mouth (eat_at); else the clip's arms are kept
EAT_STAND_CLIP = clip_file('eat_stand')
EAT_STAND_SECONDS = EAT_STAND_CLIP['frames'] / EAT_STAND_CLIP['fps'] if EAT_STAND_CLIP is not None else None

# Taking drugs (the Ingest job's chew, as eating, by how the drug is taken: WalkFigures.DrugClipOf, its ingestSound).
# Both procedural, laid over the sit or the idle from the waist up (EAT_UPPER). Smoking (smoke_at, smoke: smokeleaf,
# flake): a loop, the joint (Models/tool_joint.obj, anim_scripts/make_joint.py) through the right hand's fingers
# across them, its filter end on the palm's side; the hand raised loosely by the right shoulder, palm toward the face,
# brought to the lips for a drag (the filter end on the mouth), lowered as the head tips back to breathe the smoke out,
# and the ash flicked once a loop. Its place on the hand is a `tool smoke joint R` line (smoke_rest). The left arm hangs.
# Taking a pill (pill_at, pill: pills, and a powder snorted, yayo): once through over the chew (the game times it), the
# left palm up holding what it takes as eating standing does (EAT_MEAL, so the game draws it there), the right fingers
# pinching from it, up to the mouth, the head tipped back to swallow and level again, the hand back to the left palm.
# Hand places are given as where a point on the hand goes (the joint's filter end, the pinch between the fingertips and
# the thumb's): in the head's rest frame from its joint, leg lengths, x out to the figure's right (the right hand's
# side), y back (- forward), z up; or (rest places) from the right shoulder in a level frame turned as the chest is,
# shares of the arm's reach, x out (+ is left), y back, z up, as TEACH_KEYS. Ways (fingers, palm facing) in that frame.
SMOKE_SECONDS = 6.0   # one loop: one drag (the game's joint lasts 720 ticks, 12 s, so two)
SMOKE_BREATHS = 2     # breaths a loop, besides the drag's
SMOKE_MOUTH = (0.03, -0.32, 0.05)   # where the filter end goes at the drag: the lips, from the head's joint (head's frame)
SMOKE_AT = (0.0, 0.14, 0.24, 0.36, 0.48, 0.62, 0.76, 0.82, 0.88)   # the keys round the loop
SMOKE_KEYS = {
    'mouth': (0.0, 0.0, 1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0),   # share of the way from the rest place to the lips
    'drop': (0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.07, 0.0),   # the rest place let down (shares of the reach): the flick
    'bow': (0.0, 0.0, 4.0, 4.0, -12.0, -8.0, 0.0, 0.0, 0.0),   # the head bowed to the hand, then tipped back to blow
    'breath': (0.3, 0.3, 0.6, 1.0, 0.4, 0.0, 0.2, 0.2, 0.3),   # the chest filled: in on the drag, out after
    'curl': (0.25, 0.25, 0.2, 0.2, 0.25, 0.25, 0.25, 0.4, 0.25),   # the fingers' curl (shares of FIST)
}
SMOKE_REST = (-0.3, -0.55, -0.2)   # the filter end at rest, from the right shoulder (level frame, shares of the reach)
SMOKE_REST_FACING = (0.6, 0.6, 0.5)   # the right palm's facing at rest: in toward the chest and the face, a little up
SMOKE_REST_FINGERS = (0.1, -0.3, 1.0)   # the fingers' way at rest: up
SMOKE_MOUTH_FACING = (0.4, 1.0, 0.45)   # the palm's facing at the lips: back to the face, a little in
SMOKE_MOUTH_FINGERS = (0.35, 0.0, 1.0)   # the fingers' way at the lips: up and in across the face
SMOKE_ELBOW = (-0.7, 0.2, -0.7)   # the right elbow out and down (chest's frame)
SMOKE_JOINT = (0.45, 0.02, 0.35)   # where the joint crosses the fingers: shares from the fingers' joint (hand.R.001's
                                   # head) toward their tip, across toward the thumb's side, and its length (the filter
                                   # end on the palm side); the joint's line tipped toward the fingertips by SMOKE_TIP
SMOKE_TIP = 25.0      # degrees
SMOKE_SIZE = 1.0      # the joint model's size (its own units are cells at a grown pawn's size)
PILL_SECONDS = 100 / 60.0   # the clip's own length; the game plays it over the chew (100 ticks for a pill at full eating speed)
PILL_FRAMES = 30
PILL_AT = (0.0, 0.14, 0.42, 0.52, 0.66, 0.84, 1.0)   # the keys once through
PILL_KEYS = {
    'mouth': (0.0, 0.0, 1.0, 1.0, 0.2, 0.0, 0.0),   # share of the way from over the left palm to the lips
    'bow': (6.0, 6.0, 0.0, -6.0, -18.0, -4.0, 6.0),   # the head: looking down at the palm, tipped back to swallow
    'curl': (0.2, 0.55, 0.55, 0.3, 0.2, 0.2, 0.2),   # the right fingers: pinched from the palm to the lips
}
PILL_MOUTH = (0.0, -0.36, 0.05)   # the pinch at the lips, from the head's joint (head's frame)
PILL_PALM = (0.0, 0.0, 0.05)   # the pinch over the left palm: leg lengths from the palm's face, in the level frame
PILL_FACING = ((0.2, 0.3, -1.0), (0.5, 1.0, 0.0))   # the right palm's facing over the left palm (down) and at the lips
PILL_FINGERS = ((0.4, -1.0, 0.0), (0.4, -0.2, 1.0))   # the right fingers' way over the palm (forward) and at the lips
PILL_ELBOW = (-0.7, 0.3, -0.6)

# Reading (read_at, read: the game's Reading job, once the book is open, sat or standing still with it). Procedural: the
# open book lies on the left palm under its spine, tipped up toward the face, the right hand resting on the right-hand
# page, the trunk leant a little and the head bowed to it; once a loop the right hand lifts the page, carries it over to
# the left and comes back. Laid over the sit or the idle from the waist up, as eating is (EAT_UPPER). The book rides the
# left hand: its place is written as a `tool read open_book L` line (read_rest), so the game draws IsoRim's open book
# model (or its map or schematic) there, and the right hand is placed on it in the book's own frame.
READ_SECONDS = 8.0   # one loop: the breaths and one page turned
READ_BREATHS = 2     # breaths a loop
READ_BOOK = (0.02, -0.5, 0.32)   # the middle of the book's underside at the spine: leg lengths from the middle of the
                                   # hips at rest in a level frame turned as the chest is, x out (- is right), y back (-
                                   # forward), z up; in front of the belly, under the chin
READ_TILT = 35.0     # the book tipped up toward the face from flat (degrees), its far edge raised
READ_SCALE = 0.55    # the book's size: a share of IsoRim's open_book model (0.56 cells across open), at a grown pawn's size
READ_PALM = 0.03     # the left palm this far under the book's underside (leg lengths), so the fingers don't show through
READ_FINGERS = (-0.3, -1.0, 0.0)   # the way the left fingers point under the book, palm up: in the book's frame (x
                                    # across, + toward the left page, y along the spine, + toward the reader, z out of its
                                    # face), away from the reader and a little in toward the right
READ_CURL = 0.15     # share of FIST the left fingers curl round the spine
READ_REST = (-0.62, 0.35, 0.05)   # the right palm on the right page, in the book's frame: shares of half its width across
                                  # (- the right page), of half its depth along the spine and of half its width up off it
READ_TURN = (0.55, 0.85)   # the share of the loop the page turn takes, start and end
READ_OVER = (0.55, 0.25, 0.4)   # where the turning hand goes: across onto the left page, along and up, as READ_REST
READ_LIFT = 0.45     # how high the hand arcs over the book at the middle of the turn (shares of half its width)
READ_LEAN = 6.0      # the trunk leant forward over the book (degrees, from the waist)
READ_LOOK = 22.0     # the head bowed further to look down at it (degrees)
READ_ELBOW_L = (0.6, 0.4, -0.8)   # the left elbow out and down (chest's frame)
READ_ELBOW_R = (-0.7, 0.4, -0.6)   # the right elbow out and down
# Showing the book to someone sat beside (read_show_L and read_show_R, read_at's show: Romance on the Rim's shared
# reading, WalkFigures.ReadOf): the book moved out toward that side and turned to face between the two, the trunk and
# head turned a little after it, so both can read it.
READ_SHOW_OUT = 0.26    # leg lengths the book moves toward the watcher
READ_SHOW_TURN = 30.0   # degrees the book turns about the vertical, to face between the two (who are also turned and leant together, WalkFigures.ShareTurn, ShareLean)
READ_SHOW_TWIST = 16.0  # degrees the trunk turns toward the watcher
READ_SHOW_LOOK = 10.0    # degrees the head turns further toward the book

# Researching (research_at, work_research: the Research job, sat at the bench): inspect_clip.json (`import_pluck.py --
# animDLs/inspect.glb _01a17cae_051d_7585_b81e_0406f2e4310d 8 87 --as inspect`: sat holding something up in both hands
# and looking it over), laid over the sit from the waist up as eating at a table is (EAT_UPPER), its own arms and lean
# kept. Standing at the bench, the same from the chest up on the standing pose (research_stand_at, work_research_stand,
# the whole figure in place of the idle), the chest stood up a share of its lean (RESEARCH_STAND_RAISE). With no seat,
# sat cross-legged on the floor (research_floor_at, work_research_floor, the whole figure): the clip from the waist up on
# the meditation's hips and legs, whose pelvis is tipped back about as far as the clip's own.
# (research_clip.json, from animDLs/research.glb frames 13 .. 83, sat at a desk working with both hands, was the clip
# before; it is kept but not played.)
RESEARCH_CLIP = clip_file('inspect')
RESEARCH_SECONDS = RESEARCH_CLIP['frames'] / RESEARCH_CLIP['fps'] if RESEARCH_CLIP is not None else None
RESEARCH_STAND_RAISE = 0.5   # share of the clip's forward lean of the chest taken out standing (research_stand_at)

# Generic work by where it is done, in place of the tool strokes for most work (the game sorts the job by its place,
# WalkFigures.WorkOf). Each `import_pluck.py -- animDLs/<glb> <action> <first> <last> --as <name>`, played on the clock:
#   crouch (crouch_at, work_crouch): groundkneel.glb _01a17cb8_cd0c_7420_ae05_9af20c372719 39 70, knelt on the left
#     knee with both hands at the ground: work on the ground with no seat (a crafting or butcher spot, tending a patient
#     on the ground, cutting plants other than trees). Frames 0 .. 33 are it kneeling down from standing, left out (the
#     game eases into the loop). Set on the floor as the kneels are. The clip's right elbow sinks into the raised right
#     knee (the chibi's short trunk brings that shoulder down beside the knee), so the knee is tucked in toward the
#     middle (CROUCH_KNEE) and the right arm placed again with its elbow out and up (CROUCH_ELBOW), the hand kept. The
#     stooped chest still folds onto the raised thigh, so the trunk is straightened back a little (CROUCH_RAISE).
#   sow (sow_at, work_sow): crouchingWorkAtGroundFixed.glb crouchingWorkAtGround 0 201, crouched hunched over with both
#     hands at the ground (the crouch before the kneel): sowing and planting. The whole clip, which closes on itself.
#     The chest folds down into the knees, so they are spread (SOW_SPREAD) and the trunk straightened (SOW_RAISE).
#     A second way, one of work_sow's VARIANTS: bendsow.glb _01a17da3_6e15_721c_99ae_7f75cda3ed37 0 94 --as bendsow,
#     stood and bent down from the hips to the ground and up again, the whole clip (it closes on itself; its last
#     frame, 95, is its first again), the legs left near straight (no SOW_SPREAD or SOW_RAISE).
#   bench (bench_at, work_bench): StandingWorkAtTable.glb 4 66, stood at a bench working with both hands: any bench or
#     table with no seat at it (crafting, cooking, drugs, research standing). Set on the floor as the kneels are.
#   bench_sit (bench_sit_at, work_bench_sit): sittingWorkAtTable.glb 26 94, sat at a bench working on it: the same with
#     a seat. Laid over the sit from the waist up as research is (EAT_UPPER); the clip's own legs are on tiptoe on a
#     higher chair than the game's, so they and the seat are the sit's.
#   chair (chair_at, work_chair): ChairWorkNoTable.glb ChairWorkNoTable 25 104, sat working on the lap: a seat at work
#     with no bench (a crafting or butcher spot with a chair on it). Laid over the sit the same way.
CROUCH_CLIP = clip_file('crouch')
CROUCH_SECONDS = CROUCH_CLIP['frames'] / CROUCH_CLIP['fps'] if CROUCH_CLIP is not None else None
CROUCH_KNEE = 20.0   # degrees the raised right knee is swung in about its hip-to-ankle line, the foot kept
CROUCH_SHIN = 0.15   # share of the way from under the raised knee back toward the hip that its foot is planted
CROUCH_ELBOW = (1.0, 0.0, 1.0)   # the way the right elbow points, out over the knee: x out, y back, z up
CROUCH_RAISE = 10.0  # degrees the trunk is straightened back from the waist, out of the raised thigh (raise_trunk)
SOW_CLIP = clip_file('sow')
SOW_SECONDS = SOW_CLIP['frames'] / SOW_CLIP['fps'] if SOW_CLIP is not None else None
SOW_HANDS = 0.05     # the height over the floor (leg lengths) below which a fingertip is let down less and less
                     # (sow_at), so the hands come to rest on the ground rather than dig into it
SOW_SPREAD = 30.0    # degrees each knee is swung out about its hip-to-ankle line, the feet kept, so the trunk folds
                     # down between the thighs rather than into them
SOW_RAISE = 6.0      # degrees the trunk is straightened back from the waist, out of the thighs (raise_trunk)
BENDSOW_CLIP = clip_file('bendsow')
BENCH_CLIP = clip_file('bench')
BENCH_RAISE = 4.0    # degrees the bench work's trunk is stood up from the waist (raise_trunk), the hands kept: the clip
                     # stoops the chibi's big head right down onto what lies on the bench (a stove's pot, the food); more
                     # than about 4 pulls the shoulders back out of the hands' reach (8: 0.3 leg lengths short)
BENCH_LOOK = 20.0    # degrees the head is lifted on top of that (+ up), which leaves the hands as they are
BENCH_SECONDS = BENCH_CLIP['frames'] / BENCH_CLIP['fps'] if BENCH_CLIP is not None else None
BENCH_SIT_CLIP = clip_file('bench_sit')
BENCH_SIT_SECONDS = BENCH_SIT_CLIP['frames'] / BENCH_SIT_CLIP['fps'] if BENCH_SIT_CLIP is not None else None
CHAIR_CLIP = clip_file('chair')
SEAT_WAIST = 1.0   # share of the way the seated clips' lower spine is leant from where the clip has it to halfway
                   # between the sit's hips and the clip's chest (bend_waist): research, bench_sit and chair
CHAIR_SECONDS = CHAIR_CLIP['frames'] / CHAIR_CLIP['fps'] if CHAIR_CLIP is not None else None

# Limping (limp_at): the walk favouring one leg, for a leg that is hurt, missing or a poor prosthetic. One clip per side
# (limp_L, limp_R), timed by the ground covered like the walk, the good heel striking where the walk's does on its side
# so the game can blend the two. The bad foot is down for less of the cycle (the good leg swings through fast) and
# barely lifts, kept stiff and swung out round the other; the pelvis hitches up on that side to clear it. The hips drop
# as the weight comes off the bad leg onto the good one, and while the bad leg bears it the trunk leans out over it.
# That side's hand is held to the hip. Phases are from the good heel strike; distances in leg lengths.
LIMP_GOOD = 0.7      # share of the cycle the good foot is on the ground, in place of STANCE
LIMP_STANCE = 0.45   # and the bad one (LIMP_STRIKE + LIMP_STANCE at least 1, or both feet leave the ground)
LIMP_STRIKE = 0.6    # cycle phase of the bad heel strike (before LIMP_GOOD, so a foot is always down)
LIMP_STRIDE = 0.8    # the walk's stride scaled: shorter steps
LIMP_LIFT = 0.05     # the bad ankle's lift at mid-swing, in place of LIFT
LIMP_HEEL = 8.0      # its heel rise before toe-off (degrees), in place of HEEL
LIMP_OUT = 0.07      # how far the bad foot swings out to the side at mid-swing
LIMP_HIKE = 6.0      # the bad side of the pelvis hitched up while it swings (degrees)
LIMP_RISE = 0.02     # the hips raised over the bad leg while it bears the weight, so it stays straighter
LIMP_DIP = 0.05      # and dropped as the weight comes onto the good one
LIMP_DIP_AT = 0.06   # cycle phase of the lowest point
LIMP_LEAN = 7.0      # the trunk leaned over the bad leg while it bears the weight (degrees)
LIMP_SWAY = 0.05     # the hips moved sideways over the foot bearing the weight
LIMP_HAND = (0.12, -0.1, 0.05)   # the bad side's wrist from its hip joint: x out, y forward (-), z up

# Hopping (hop_at): on one leg, for a pawn with a leg missing. One clip per missing side (hop_L, hop_R), one hop a
# cycle, timed by the ground covered like the walk. The phase runs from the landing: the foot is down for HOP_STANCE of
# the cycle, sliding back at the body's pace, and the figure is in the air for the rest. The hips stay over the foot and
# sink on landing just as fast as they came down, the trunk bowing with them and rising in the air, the arms held out and
# lifted with each hop. The other leg, which the game cuts away at the hip, is posed held up, the knee forward.
# Distances in leg lengths.
HOP_GROUND = 1.4     # ground covered per hop: a pawn on one leg goes at about half pace, so about 2 to 3 hops a game second
HOP_STANCE = 0.4     # share of the cycle the foot is on the ground
HOP_HEIGHT = 0.14    # how far the hips rise in the air
HOP_DROP = 0.05      # hips lowered from rest at the landing and the take-off
HOP_LIFT = 0.2       # the ankle's lift off the ground in the air
HOP_HEEL = 30.0      # its heel rise as it pushes off (degrees), in place of HEEL
HOP_OVER = 0.8       # share of the way the hips move sideways to be over the foot
HOP_LEAN = 8.0       # the trunk leaned forward on average (degrees)
HOP_NOD = 8.0        # and more on the ground, less in the air (degrees)
HOP_TILT = 4.0       # the trunk leaned over the standing leg (degrees)
HOP_LOOK = 0.7       # share of the lean the head takes back, to look ahead
HOP_HANDS_DOWN = (0.35, -0.2, -0.85)   # each wrist from its shoulder, by the arm's reach: x out, y forward (-), z up; on the ground
HOP_HANDS_UP = (0.6, -0.35, -0.55)     # and at the top of the hop
HOP_STUMP = (0.0, 0.1, -0.72)   # the other ankle from its hip joint
HOP_STUMP_SWING = 0.06   # and swung back on the ground, forward in the air

# Wading (wade_at): the walk through water, by the ground covered. The game sinks the figure to the water's depth (so
# this is the same for knee-deep and chest-deep water): shorter steps, the knees brought up high to step over the water,
# a deeper lean, the hips and shoulders turned into each step, and the arms held up and out from the sides, swinging
# less. Each number is in place of the walk's of the same name less WADE_.
WADE_STRIDE = 0.42
WADE_LIFT = 0.32
WADE_PEAK = 0.35
WADE_KICK = 0.0
WADE_LEAN = 12.0
WADE_TWIST = 9.0
WADE_CHEST = 7.0
WADE_DROP = 0.05
WADE_ARM = 12.0
WADE_ARM_DOWN = 14.0   # the arms out from the sides, hands up out of the water
WADE_ELBOW = 40.0

# Swimming (swim_at): a front crawl, for the game's GoSwimming. Made upright like the crawl (the game tips it face down,
# SkinnedFigure lifting the head end SwimLift, and sinks it to the neck), so ahead is up here, the water's surface is
# behind the figure and the bottom in front. The arms go round in turn: from reached ahead, pulled down under the body
# to the hip, then brought back over the water with the elbow high. The body rolls toward the pulling arm, the legs
# flutter-kick, and the head is tipped back to look ahead. Timed by the ground covered: SWIM_GROUND leg lengths a cycle
# (two strokes). Arm places in arm lengths from the shoulder: x out, y toward the bottom (-) or the surface (+), z ahead.
SWIM_FRAMES = 24
SWIM_GROUND = 2.0
SWIM_AHEAD = (0.15, -0.1, 0.95)   # the wrist reached ahead, as it goes in
SWIM_PULLED = (0.2, -0.15, -0.55)   # and pulled back to the hip, as it comes out
SWIM_DEEP = 0.6      # the wrist's reach from the shoulder half way through the pull, under the body
SWIM_HIGH = 0.6      # and half way back over the water. The wrist goes round the shoulder at a steady turn, so
                     # it never passes close by it: a straight path did, and the elbow spun round there.
SWIM_ROLL = 25.0     # the body rolled toward the pulling arm (degrees)
SWIM_KICK = 14.0     # each thigh kicked up and down (degrees)
SWIM_KICKS = 3       # kicks per leg per cycle
SWIM_KNEE = 15.0     # the knee bent on the down-kick (degrees)
SWIM_LOOK = 40.0     # neck and head tipped back to look ahead (degrees)
# With swim_clip.json beside this file (import_pluck.py --as swim --afloat: one loop of Meshy's Swim_Forward), swim_at
# plays that in place of the stroke above, timed by the ground it covers, tipped back up by SWIM_TIP, the turn the game
# lays the swimmer down by (90 less SwimLift, 15), so it comes out in the water as it was made.
SWIM_TIP = 75.0
try:
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'swim_clip.json')) as _f:
        SWIM_CLIP = json.load(_f)
    SWIM_FRAMES = SWIM_CLIP['frames']
except FileNotFoundError:
    SWIM_CLIP = None

# Treading water (tread_at): swimming but still (the game's GoSwimming waits in the water a while). Upright, sunk to
# the neck; the legs pedal round under the body and the hands scull out to the sides in front of the chest. Leg places in
# leg lengths from the hip joint, hand places in arm lengths from the shoulder: x out, y forward (-), z up.
TREAD_SECONDS = 4.0
TREAD_STROKES = 3    # pedals and sculls per loop
TREAD_FOOT = (0.08, -0.15, -0.8)   # the middle of the circle each ankle goes round
TREAD_PEDAL = 0.15   # its radius
TREAD_HAND = (0.6, -0.35, -0.35)   # the middle of each hand's sweep
TREAD_SCULL = 0.2    # how far each hand sweeps in and out

# Carrying (carry_at): the arms, chest and head of one loop per kind of load, laid over the gait as the drafted stance is
# (UPPER; a side left out of the kind's hands keeps the gait's swing). 'item': a hauled thing held out in front, the hands
# under it. 'bridal' and 'fireman': a person, who is posed by carried_at as one clip of its own (carried_<kind>). That
# clip is in the carrier's rest space, so the game puts it on the carrier's chest bone as posed (CHEST_BONE) and it goes
# where the chest goes; the carrier's hands are placed from it (on the far side of the knees and back, or round the
# thighs), so they meet; 'hands' names the sides that hold it. A person carry can still set a hand's '<side>' place,
# '<side>_turn' and '<side>_elbow' (the preview's handles do), in place of the one carry_hands finds. Places are in leg
# lengths from the middle of the carrier's hips at rest, as DRAFTED's.
CARRY_SECONDS = 4.0  # one breath
CARRY = {
    'item': {'R': (-0.1503, -0.3545, 0.2435), 'R_turn': (-13.528, 134.3796, -96.2423), 'L': (0.1565, -0.3455, 0.2747), 'L_turn': (-6.5397, -151.2349, 71.2259)},   # forearms forward, palms up under the load
    # Cradled across the front: the back on the left forearm, the knees over the right. Leaning back against the weight.
    'bridal': {'R': (-0.3795, -0.5426, 0.3797), 'R_turn': (-175.8663, 28.5409, 56.3002), 'L': (0.3328, -0.4742, 0.4191), 'L_turn': (-147.1173, -43.3968, -101.9629), 'hands': 'LR', 'body': dict(LEAN=-4.0, DROP=0.05, NARROW=0.45)},
    # Over the right shoulder, the right arm round the thighs; the left hangs free. Leaning forward under the weight.
    'fireman': {'R': (-0.6637, 0.0079, 0.625), 'R_turn': (69.0728, 61.9779, 238.1801), 'hands': 'R', 'body': dict(LEAN=9.0, DROP=0.06, NARROW=0.45)},
    # A baby, in the left arm: its head in the crook of the elbow, the forearm along its back and the hand under its
    # bottom; the right hand over its belly. Held up and in against the chest.
    'cradle': {'R': (-0.0519, -0.5028, 0.4453), 'R_turn': (-69.338, 30.2702, 206.5912), 'R_elbow': -21.0, 'L': (0.1369, -0.3901, 0.3699), 'L_turn': (-156.2982, -16.3123, -161.5277), 'L_elbow': 10.0, 'hands': 'LR', 'body': dict(LEAN=-2.0, DROP=0.03, NARROW=0.45)},
    # Playing with a baby (Biotech's PlayWalking): held as the cradle holds it, but rocked from side to side in the arms
    # (carry_rock), the trunk swaying with it, and the baby awake and waving (carried_at). 'rock' makes it so.
    'play': {'R': (-0.0519, -0.5028, 0.4453), 'R_turn': (-69.338, 30.2702, 206.5912), 'R_elbow': -21.0, 'L': (0.1369, -0.3901, 0.3699), 'L_turn': (-156.2982, -16.3123, -161.5277), 'L_elbow': 10.0, 'hands': 'LR', 'body': dict(LEAN=-2.0, DROP=0.03, NARROW=0.45), 'rock': True},
    # A small pet cuddled ([DG]Lovly Animal Cuddling's AC_CuddleAnimal): the cradle's left arm, the pet sat along its
    # forearm (pet_on), and the right hand laid on its back (cuddle_hand), head to the carrier's left. 'pet': no
    # carried_<kind> clip (IsoRim draws the animal), the right hand placed by cuddle_hand. 'cuddle_stroke' strokes it
    # from the neck to the rump STROKES times a loop of STROKE_SECONDS, the hand lifted back over it between; the game
    # eases it in over 'cuddle' while the pawn stands or sits still.
    'cuddle': {'L': (0.0994, -0.4228, 0.1993), 'L_turn': (-165.5394, -34.528, -155.3791), 'hands': 'LR', 'pet': True, 'body': dict(LEAN=-2.0, DROP=0.03, NARROW=0.45)},
    'cuddle_stroke': {'L': (0.1129, -0.4169, 0.2037), 'L_turn': (-166.366, -39.5846, -155.8013), 'L_elbow': 10.0, 'hands': 'LR', 'body': dict(LEAN=-2.0, DROP=0.03, NARROW=0.45), 'pet': True, 'stroke': True},
}
# The cuddled pet (a CARRY entry with 'pet'): its trunk's middle PET_ALONG of the way from the left elbow to the wrist and
# PET_LIFT (leg lengths) over that line, the top of its back PET_BACK over that again (a cat's, at the standard size; the
# game moves the right hand by how much higher or lower the animal it draws is, anim.txt's cuddle_pet line). The right
# palm rests on its back STROKE_REST (leg lengths along the carrier's left, toward the head; + forward), its fingers
# forward and toward the rump (STROKE_FINGERS, as the short arms reach it best so, the carrier's rest space: -x its right, -y forward), the palm facing
# STROKE_PALM (straight down: on its back) and STROKE_REST's third number (leg lengths) over the back (the game moves the
# hand up or down to the back of the animal it draws, keeping that). Stroking: from STROKE_FROM to
# STROKE_TO along the back (leg lengths, + toward the head) over STROKE_PRESS of each stroke, pressed STROKE_DIP into the
# fur at its middle, then lifted STROKE_LIFT over it on the way back. The head bowed CUDDLE_LOOK degrees to look at it.
PET_ALONG = 0.65
PET_LIFT = 0.04
PET_BACK = 0.18
STROKE_REST = (0.0616, 0.0305, 0.103)
STROKE_FINGERS = (0.7936, -0.5702, -0.2124)
STROKE_PALM = (-0.4393, -0.2955, -0.8483)
STROKE_SECONDS = 6.0
STROKES = 3
STROKE_FROM = 0.0308
STROKE_TO = -0.2092
STROKE_PRESS = 0.6
STROKE_DIP = 0.015
STROKE_LIFT = 0.10
CUDDLE_LOOK = 14.0
# Rocking (a CARRY entry with 'rock'): the baby swung like a pendulum in the arms about a point between the shoulders
# (PLAY_PIVOT, leg lengths from the middle of the hips at rest, as CARRY's places), PLAY_SWING degrees either way,
# PLAY_ROCKS times a loop of PLAY_SECONDS, and lifted PLAY_LILT (leg lengths) at each end of the swing; the hands go with
# it. The carrier's trunk turns after the baby (PLAY_TWIST degrees about the vertical) and leans the other way against
# its weight (PLAY_ROLL), so the chest, and the baby riding it, swings further than the arms alone take it.
PLAY_SECONDS = 8.0
PLAY_ROCKS = 5
PLAY_PIVOT = (0.0, -0.25, 0.80)
PLAY_SWING = 16.0
PLAY_LILT = 0.03
PLAY_TWIST = 12.0
PLAY_ROLL = 3.0
CARRY_UNDER = 0.08   # how far the hands are under the carried person's knees and back (its thickness there)
# The carried person, limp. Bridal: its hip joint here, the legs folded up at the hips (FOLD, degrees) and hanging from
# the knees (KNEE), the whole tipped back (TILT) and turned across the carrier, head to the carrier's left.
BRIDAL_AT = (-0.02, -0.46, 0.30)
BRIDAL_FOLD = 55.0
BRIDAL_KNEE = 85.0
BRIDAL_TILT = 55.0
BRIDAL_LOLL = 25.0   # head tipped back (degrees)
# Cradle: a baby, which the game draws at a share of the carrier's size about its hips (WalkFigures.SizeOf: growing from
# BirthSize). Its hip joint is here, and the hands are placed on it as shrunk to CRADLE_SCALE, about a year and a half
# old. Curled up (FOLD, KNEE), lying back nearly flat (TILT) and
# turned across the carrier as the bridal carry is, the head held up a little rather than lolling (LOLL), the arms
# folded on its chest (ARMS, ELBOWS).
CRADLE_SCALE = 0.31
CRADLE_AT = (-0.06, -0.34, 0.40)
CRADLE_FOLD = 75.0
CRADLE_KNEE = 70.0
CRADLE_TILT = 75.0
CRADLE_LOLL = -10.0
CRADLE_ARMS = 50.0
CRADLE_ELBOWS = 90.0
CRADLE_UNDER = 0.10   # how far the left hand is under its bottom and the right over its belly (its own leg lengths)
# Fireman: its hip joint here (to the right of the carrier's head, over the shoulder), folded double at the hips (FOLD),
# upside down with the torso hanging down the carrier's back and the legs down its front, tipped back (TILT) so the fold
# sits over the shoulder. The arms hang toward the ground.
FIREMAN_AT = (-0.40, 0.02, 0.98)
FIREMAN_FOLD = 115.0
FIREMAN_KNEE = 10.0
FIREMAN_TILT = 32.0
FIREMAN_ARMS = 130.0   # the arms raised over the head (degrees), so upside down they hang

# Bending to the ground (bend_pose), to pick something up or set it down: one still pose, which the game blends in and
# out over BEND_SECONDS of game time when what a pawn carries changes. The game cannot see a pick-up coming (it takes the
# thing in one tick), so the pose is reached fast and left slowly: the hands come up from the ground with the load.
# A squat with the hips tipped forward, feet where they stood; the hands reach for the ground in front.
BEND_SECONDS = 0.8
BEND_DROP = 0.55     # hips lowered (leg lengths)
BEND_BACK = 0.16     # and moved back over the heels (leg lengths)
BEND_HIPS = 32.0     # hips tipped forward (degrees); the spine above follows
BEND_LEAN = 8.0      # the spine curled further forward, in place of LEAN
BEND_HANDS = (0.2, -0.6, 0.3)   # wrists from between the ankles: x out, y forward (-), z up (leg lengths)
# On the move (bend_walk_pose): the gait keeps the legs, so the bend is the upper body's alone, folded at the waist, laid
# over the stride (BEND_WALK_UPPER) and played faster (WalkFigures.BendHurry).
BEND_WALK = 42.0     # the upper body folded forward at the waist (degrees)
BEND_WALK_LOOK = 18.0   # the head tipped back up from that, to look ahead of the hands (degrees)
BEND_WALK_HANDS = (0.2, -0.65, 0.45)   # wrists from between the ankles, as BEND_HANDS (out of reach: as near as they get)
BEND_WALK_UPPER = {'spine.001': 1.0}   # in place of UPPER: the whole fold from the waist up

# Hurt (hurt_at): a hand held to the worst wound and the trunk hunched over it, breathing short and shallow. One loop
# per place and side (hurt_<place>_<side>: head, neck, chest, belly, each held by the hand on the wound's side; arm, the
# hurt arm held across the belly and clutched by the other hand), laid over whatever the legs do as the drafted
# stance is, but from the waist up (HURT_UPPER), so a limp or a seat keeps its legs under it. The arm not holding
# anything keeps the gait's swing. The game picks the place from the pawn's injuries (WalkFigures.HurtOf).
HURT_SECONDS = 3.0
HURT_BREATHS = 4     # short breaths per loop
HURT_BREATH = 4.0    # the chest heaved on the in-breath (degrees), in place of BREATH
HURT_LEAN = 6.0      # the trunk bent toward the hurt side (degrees; not for the head or neck)
HURT_LOOK = 0.6      # share of the hunch the head takes back, to look ahead
HURT_UPPER = {'spine.001': 1.0}   # in place of UPPER: the hunch is from the waist up
# Each place's hunch (degrees forward, half at the waist and half at the chest) and the holding wrist: from that place's
# bone's rest head (HEAD, NECK, CHEST_BONE, SPINE: the belly), moved with it as posed, in leg lengths, x out to the hurt
# side (less than 0: across), y back (forward is -y), z up. '_TURN' is the hand's turn (x, y, z degrees in the chest's frame, as DRAFTED's
# '<side>_turn').
HURT_HEAD = (0.36, -0.2, 0.08)
HURT_HEAD_TURN = (60.0, 0.0, 90.0)
HURT_HEAD_HUNCH = 8.0
HURT_NECK = (-0.06, -0.18, -0.1)   # across: the hand on the far side of the neck, palm turned back toward it
HURT_NECK_TURN = (14.7, -71.1, 27.8)
HURT_NECK_HUNCH = 10.0
HURT_CHEST = (0.1, -0.24, 0.2)
HURT_CHEST_TURN = (-90.0, 0.0, 130.0)
HURT_CHEST_HUNCH = 20.0
HURT_BELLY = (0.1, -0.33, 0.1)
HURT_BELLY_TURN = (-90.0, 0.0, 130.0)
HURT_BELLY_HUNCH = 30.0
# The hurt arm: its wrist across the front of the belly, from the middle of the hips as DRAFTED's (x toward the arm's
# own side, so less than 0 is across), moved with the chest, the elbow tucked in at the side; the other hand under its
# forearm, palm down near the elbow (HURT_ARM_GRIP of the way from the elbow to the wrist, the palm HURT_ARM_UNDER below
# it: less than 0 is on top, where the game's camera sees it), clutching it.
# The turns are for a hurt left arm (the right hand holding), mirrored for the right.
HURT_ARM = (-0.02, -0.42, 0.34)
HURT_ARM_TURN = (44.3, -151.7, 8.3)
HURT_ARM_GRIP = 0.3
HURT_ARM_UNDER = -0.05
HURT_ARM_HOLD_TURN = (-48.2, -7.8, -168.5)
HURT_ARM_HUNCH = 12.0

# Cowering (FleeAndCower, FleeAndCowerShort): hunched forward, the head a little bowed and both hands drawn up apart
# in front of the chest, the elbows tucked down, breathing quick. On the move it is laid over the run from the waist
# up (CRY_UPPER), so the legs keep running; standing still the pawn sinks into a crouch as well (the hunker clip,
# hunker_at).
CRY_SECONDS = 3.0
CRY_BREATHS = 3      # quick breaths per loop
CRY_BREATH = 4.0     # the chest heaved on the in-breath (degrees), in place of BREATH
CRY_LEAN = 28.0      # the trunk folded forward from the waist (degrees), spine and chest half each
CRY_HEAD = -30.0     # the head tipped (degrees, less than 0 is back up), the neck taking half and the head half: it
                     # undoes most of the lean and the hunker's hip tip, so the head is only a little bowed
CRY_HANDS = (0.17, -0.27, 0.54)   # the left wrist (mirrored for the right), as DRAFTED's: leg lengths from the middle
                                  # of the hips at rest in the chest's frame, x out, y back, z up; apart, in front of the
                                  # lower chest
CRY_ELBOW = (0.4, 0.2, -1.0)    # the elbows down and in by the sides (chest's frame)
CRY_SHRUG = 15.0     # the shoulders drawn up (degrees)
CRY_UPPER = {'spine.001': 1.0}   # in place of UPPER: the whole cower is from the waist up
HUNKER_DROP = 0.22   # standing still: hips lowered (leg lengths), knees bent
HUNKER_BACK = 0.1    # and moved back over the heels (leg lengths)
HUNKER_HIPS = 12.0   # hips tipped forward (degrees); the trunk's fold is on top of it
HUNKER_NARROW = 0.45   # in place of NARROW: feet a little apart to crouch on

# Taking cover (drafted, standing still, an enemy in sight: WalkFigures.CoverOf, after Combat Extended's crouch): a low
# ready crouch for the legs, the feet planted where the drafted stance has them and the hips let down and back over
# them, the trunk only a little forward. It is the whole figure, in place of the standing legs, and the drafted stance
# or the aim is laid over it from the waist up as over the walk, so its own arms only hang. The game plays it at a share
# (how deep) from the cover beside the pawn as IsoRim draws it, so this is the deepest, out in the open; the hips go down
# by the share of COVER_DROP, so the drafted hands do too.
COVER_SECONDS = 4.0   # one breath, as the drafted loop
COVER_DROP = 0.4     # hips lowered (leg lengths), knees bent
COVER_BACK = 0.12    # and moved back over the heels (leg lengths)
COVER_HIPS = 16.0    # hips tipped forward (degrees)
COVER_UPRIGHT = 10.0   # the lower spine turned back up (degrees), so the trunk leans only the rest of COVER_HIPS
COVER_LOOK = 0.8     # share of the lean the head takes back, to look ahead
COVER_SPLAY = 0.2    # the knees bent forward and this much out to their own side, so they fold apart rather than straight ahead

# Leading on a rope (an animal roped to the pawn: the game's RopeToPen, Follow and the rest): the right arm reached a
# little back and down, the hand holding the rope, tugged gently in time with the breath. Only that arm is laid over the
# gait (LEAD_UPPER); the rest keeps walking or standing as it was.
LEAD_SECONDS = 4.0   # one breath
LEAD_HAND = (-0.30, 0.20, 0.16)   # the right wrist, as DRAFTED's: leg lengths from the middle of the hips at rest in the
                                  # chest's frame, x out (- is right), y back, z up; about 30 degrees back from hanging
LEAD_TUG = 0.03      # the wrist drawn back on the in-breath (leg lengths), as the rope pulls
LEAD_ELBOW = (-0.4, 1.0, -0.3)   # the elbow toward the back and a little out (chest's frame), so it bends the natural way
LEAD_UPPER = {'shoulder.R': 1.0}   # in place of UPPER: the right arm only

# Casting a psycast or ability (cast_at): the left hand raised toward the target, palm out and fingers up, pushed out a
# little in time with the breath, the left shoulder brought forward to lead it. The left hand, so a weapon in the right
# stays where its stance holds it: only the left arm, chest and head are laid over the gait (CAST_UPPER), the right arm
# keeping whatever it does. A touch ability (cast_touch) reaches the open hand out low in front instead, leaning into it.
# The game eases it in over the ability's warm-up and turns the figure to the target (WalkFigures.CastOf).
CAST_SECONDS = 2.0   # one push
CAST_HAND = (0.3, -0.85, 0.15)   # the left palm from its shoulder in the chest's frame (x out, y forward (-), z up;
                                 # shares of the arm's reach): out in front at the shoulder's height, clear of the face
CAST_TOUCH = (0.05, -0.9, -0.2)   # and for a touch: out in front at the belly's height
CAST_PUSH = 0.08     # the palm pushed further out on the in-breath (shares of the reach)
CAST_TWIST = -12.0   # the trunk turned to bring the left shoulder forward (degrees, + to the left); the head takes it back
CAST_LEAN = (0.0, 14.0)   # the trunk leaned forward (degrees): casting, touching
CAST_ELBOW = (0.8, 0.4, -0.6)   # the elbow out and down (chest's frame)
CAST_UPPER = {'spine.001': 0.5, 'spine.002': 1.0}   # in place of UPPER (as UPPER; the right arm is left out)

# Taming (tame_at, work_tame and work_tame_kneel: the game's Tame job, at the animal): the right hand held out to it palm
# up, as if offering food, eased a little further out and back once a loop, the fingers curling in to coax it closer.
# Stood at an animal whose back is drawn high, knelt (the ground work's kneel, crouch_at, the left hand kept on the ground)
# by a low one, sorted in the game as milking is (WalkFigures.AnimalKind). The wrist's place from the right shoulder in a
# level frame turned as the chest is (so a stooped chest doesn't point it at the ground), shares of the arm's reach:
# x out (- is right), y forward (-), z up.
TAME_SECONDS = 4.0   # one coax, standing (knelt it takes the crouch clip's CROUCH_SECONDS)
TAME_HAND = (-0.1, -0.9, -0.25)   # standing: reached out in front a little under the shoulder
TAME_KNEEL_HAND = (-0.3, -0.85, -0.15)   # knelt: out ahead and to the right of the raised right knee
TAME_PUSH = 0.04     # the wrist eased further out at the middle of the loop (shares of the reach)
TAME_LEAN = (10.0, -8.0)   # the trunk leant forward toward the animal (degrees): standing, knelt (the
                          # kneel stoops low over the ground, so knelt it is sat up a little)
TAME_LOOK = (15.0, -10.0)   # the head bowed further, to look at the hand (degrees): standing, knelt
TAME_TIP = 15.0      # the fingers tipped down from level (degrees), the palm still up
TAME_COAX = 2        # the fingers' curls a loop
TAME_CURL = (0.1, 0.45)   # the fingers' curl between (shares of FIST)
TAME_ELBOW = (-0.6, 0.3, -0.8)   # the elbow out and down (chest's frame)

# Teaching a lesson at a school desk (teach_at, teach: Biotech's Lessongiving, once at the teacher's spot). The game puts
# the teacher and the pupil side by side on the near side of the desk, both facing it, the pupil on the teacher's right;
# the game turns the head round toward the pupil (WalkFigures.LessonLookOf), so the clip keeps it facing ahead. Procedural:
# explaining with the hands, keyed at TEACH_AT and joined smoothly round the loop (loop_key): the right hand raised palm up
# to present, beating a point twice with the hand edge on, opened out toward the pupil, both hands spread, then patting
# down at the desk with the head bowed to it (the game's look eases off there, TeachGlance) and back to rest. The left hand
# mostly hangs, joining the spread. Laid over the idle or the sit from the waist up (TEACH_UPPER), as talking is. Each
# wrist from its shoulder in a level frame turned as the chest is, shares of the arm's reach: x out (+ is left), y forward
# (-), z up; each palm's facing and the fingers' way in the same frame.
TEACH_SECONDS = 9.0   # one loop
TEACH_BREATHS = 3     # breaths a loop
TEACH_AT = (0.0, 0.14, 0.26, 0.36, 0.5, 0.62, 0.74, 0.86)   # the keys round the loop
TEACH_KEYS = {
    'wrist_R': ((-0.12, -0.25, -0.85), (-0.3, -0.65, -0.35), (-0.25, -0.72, -0.3), (-0.25, -0.7, -0.5),
                (-0.5, -0.55, -0.32), (-0.42, -0.6, -0.34), (-0.1, -0.8, -0.58), (-0.15, -0.45, -0.75)),
    'palm_R': ((1, 0, 0), (0.3, 0, 1), (1, 0, 0.2), (1, 0, 0.1), (0.2, 0, 1), (0.2, 0, 1), (0, -0.2, -1), (1, 0, 0)),
    'fingers_R': ((0, -0.2, -1), (-0.4, -1, 0.1), (-0.1, -1, 0.2), (-0.1, -1, 0), (-0.8, -0.8, 0), (-0.6, -0.9, 0),
                  (0, -1, -0.4), (0, -0.5, -1)),
    'curl_R': (0.35, 0.1, 0.15, 0.15, 0.05, 0.05, 0.2, 0.3),
    'wrist_L': ((0.12, -0.2, -0.88), (0.12, -0.2, -0.88), (0.15, -0.3, -0.82), (0.15, -0.3, -0.82),
                (0.2, -0.45, -0.7), (0.42, -0.6, -0.34), (0.2, -0.45, -0.7), (0.12, -0.25, -0.86)),
    'palm_L': ((-1, 0, 0), (-1, 0, 0), (-1, 0, 0), (-1, 0, 0), (-1, 0, 0.3), (-0.2, 0, 1), (-1, 0, 0.3), (-1, 0, 0)),
    'fingers_L': ((0, -0.2, -1), (0, -0.2, -1), (0, -0.3, -1), (0, -0.3, -1), (0.2, -0.6, -0.8), (0.6, -0.9, 0),
                  (0.2, -0.6, -0.8), (0, -0.2, -1)),
    'curl_L': (0.35, 0.35, 0.3, 0.3, 0.25, 0.05, 0.25, 0.35),
    'twist': (0.0, -4.0, -4.0, -4.0, -8.0, -3.0, 0.0, 0.0),   # the trunk turned toward the pupil (degrees, + to the left)
    'lean': (3.0, 4.0, 5.0, 6.0, 4.0, 3.0, 6.0, 4.0),   # leant forward from the waist (degrees)
    'bow': (0.0, 0.0, 3.0, 3.0, 0.0, -2.0, 9.0, 3.0),   # the head bowed further (degrees): down to the desk at the pat
}
TEACH_NODS = 6       # small nods a loop as it talks
TEACH_NOD = 3.0      # degrees each
TEACH_ELBOW_R = (-0.7, 0.4, -0.6)   # the right elbow out and down (chest's frame)
TEACH_ELBOW_L = (0.7, 0.4, -0.6)    # the left
TEACH_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up

# A quick wave in passing (wave_at, wave: the game's social interactions, a chat or a greeting, which happen at once while
# the pawn walks or works, WalkFigures.InteractedPostfix). Played once through, not looped: the right hand brought up
# out to the side of the head palm out (well clear of it, the chibi's arm being short beside its head), waved side to
# side twice and let fall. Only that arm is laid over the rest (WAVE_UPPER), so
# it waves on the move too. Each key's wrist from the right shoulder in a level frame turned as the chest is, shares of
# the arm's reach: x out (+ is left), y forward (-), z up; the palm's facing and the fingers' way in the same frame.
WAVE_SECONDS = 1.6   # once through
WAVE_AT = (0.0, 0.22, 0.36, 0.5, 0.64, 0.78)   # the keys (the last back down to the first, round the loop)
WAVE_KEYS = {
    'wrist': ((-0.15, -0.1, -0.95), (-0.72, -0.5, 0.3), (-0.8, -0.42, 0.28), (-0.68, -0.55, 0.32), (-0.8, -0.42, 0.28),
              (-0.7, -0.52, 0.3)),
    'palm': ((1, 0, 0), (0, -1, 0), (0, -1, 0), (0, -1, 0), (0, -1, 0), (0, -1, 0)),
    'fingers': ((0, 0, -1), (-0.1, 0, 1), (-0.6, 0, 1), (0.35, 0, 1), (-0.6, 0, 1), (0.2, 0, 1)),
    'curl': (0.35, 0.05, 0.0, 0.0, 0.0, 0.05),
}
WAVE_ELBOW = (-0.8, 0.3, -0.6)   # the elbow out and down (chest's frame)
WAVE_UPPER = {'shoulder.R': 1.0}   # in place of UPPER: the right arm only

# Sad (sad_at, sad: the Wander_Sad, Wander_OwnRoom and Wander_Psychotic mental breaks, and a visitor giving up): slumped,
# the trunk leant forward, the shoulders rolled forward and down, the head hung and the arms limp by the sides (the standing
# pose's, hanging), with a slow sigh once a loop. Laid over the gait from the waist up (SAD_UPPER), the arms only SAD_ARMS of the way, so the walk
# still swings them a little.
SAD_SECONDS = 6.0    # one loop: two slow breaths and a sigh
SAD_BREATHS = 2
SAD_LEAN = 12.0      # the trunk leant forward from the waist (degrees), spine and chest half each
SAD_SIGH = 4.0       # leant this much further, the shoulders dropping, at the sigh (degrees)
SAD_HEAD = 16.0      # the head hung (degrees), the neck taking half and the head half
SAD_SLUMP = 10.0     # the shoulders rolled forward and down (degrees)
SAD_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up
SAD_ARMS = 0.5       # share of the arms laid over the gait's swing

# Drunk (drunk_at; drunk and drunk_arms: the AlcoholHigh hediff, by its severity, WalkFigures.DrunkOf): the trunk swaying
# from the waist in a figure of eight (side to side once a loop, forward and back twice), turning a little with it, the
# head lolling after it and nodding with the lurch. Made on the plain stance, every turn none at the loop's first frame,
# as the game adds each bone's turn from that frame on top of whatever the figure is doing (SkinnedFigure.Nudge, as the
# flinch), so it sways standing, walking, working or aiming alike. drunk takes the trunk, neck and head (DRUNK_UPPER, the
# arms riding the chest, so a held weapon or load goes with it); drunk_arms the upper arms alone, hanging back against
# the sway (DRUNK_HANG) so they stay hanging loose, added only with the hands free. On the move the game also weaves the
# figure from side to side with the sway (WalkFigures.DrunkWeave).
DRUNK_SECONDS = 4.0   # one loop: once to each side
DRUNK_SWAY = 11.0     # degrees the trunk leans to each side from the waist (spine and chest half each)
DRUNK_LURCH = 6.0     # degrees it lurches forward and back, twice a loop
DRUNK_TWIST = 6.0     # degrees it turns about the vertical with the sway
DRUNK_LOLL = 10.0     # degrees the head lolls sideways with the sway, late (neck and head half each)
DRUNK_NOD = 5.0       # degrees the head nods forward and back with the lurch
DRUNK_HANG = 0.8      # share of the trunk's lean the upper arms are turned back against, hanging (drunk_arms)
DRUNK_UPPER = {'spine.001': 1.0}   # the bones drunk nudges: from the waist up, the arms riding the chest

# Stirring (stir_at; work_stir, stood at a stove or any bench, work_stir_crouch, knelt on the ground work, and
# work_stir_campfire, sat cross-legged at a campfire, below fire_at): a cooking
# bill's work, now and then in place of the bench or ground work (WalkFigures.StirOf, in bouts). The figure as the bench
# or ground work has it at STIR_FROM of its loop, the right fist round a wooden spoon (Models/tool_spoon.obj,
# anim_scripts/make_spoon.py; a `tool stir tool_spoon R` line, stir_rest) circling over the pot, the spoon's bowl circling
# less in it, the left hand on the pot's rim steadying it, the trunk turning a little after the spoon. The pot is where
# that work's hands are on average over its loop (stir_pot); places from it in a level frame turned as the chest is,
# leg lengths, x out to the figure's left, y back, z up. The fist's way round the handle is the straightest wrist
# (stir_hold).
STIR_SECONDS = 6.0     # one loop, the length of a bout
STIR_TURNS = 5         # times round the pot a loop
STIR_FROM = 0.0        # where in the bench or ground work's loop the figure is taken from
STIR_DIP = 0.0         # the spoon's bowl over the pot's middle (z, leg lengths)
STIR_BOWL = 0.06       # the bowl's circle's radius (leg lengths)
STIR_FIST = 0.10       # the fist's circle's radius (leg lengths), wider, as the spoon is held slanted
STIR_REACH = 0.175     # cells from the fist's middle to the bowl's (make_spoon.py's BOWL_AT)
STIR_BACK = -0.08      # the pot's middle drawn this much back toward the figure from the work's hands (leg lengths;
                       # - out from it, as the work's hands are close in and the rim's hand came out on the hip)
STIR_RIM = (0.16, 0.0, 0.06)   # the left palm on the pot's rim, from its middle
STIR_RIM_FINGERS = (-0.2, -1.0, -0.3)   # the left hand's fingers' way: forward, over the rim
STIR_RIM_PALM = (-0.6, 0.0, -1.0)       # the left palm's facing: down and in onto the rim
STIR_TWIST = 5.0       # degrees the trunk turns after the spoon round the pot
STIR_LEAN = 4.0        # degrees the trunk leans in over the pot as the spoon goes round its far side

# On fire, patting at the flames on itself (burn_at, burning: the game's pawns on fire that are not rolling it out, as when
# fleeing or drafted). Both hands slap at the chest and the belly in turn, quickly, the trunk hunched over to look down
# at itself and twisting from side to side. Each hand's places are the hurt loop's (HURT_CHEST, HURT_BELLY: the palm on
# the body on its own side), its wrist drawn BURN_LIFT off the body between pats. Laid over the run from the waist up
# (BURN_UPPER).
BURN_SECONDS = 1.2   # one loop
BURN_PATS = 3        # pats a loop by each hand, the hands half a pat apart
BURN_LIFT = 0.12     # the wrist drawn off the body between pats (leg lengths, along the chest's forward way)
BURN_LEAN = 16.0     # the trunk hunched forward (degrees), spine and chest half each
BURN_LOOK = 14.0     # the head bowed to look at itself (degrees)
BURN_TWIST = 10.0    # the trunk twisted from side to side once a loop (degrees each way)
BURN_BREATHS = 4     # quick breaths a loop
BURN_BREATH = 4.0    # the chest heaved on the in-breath (degrees), in place of BREATH
BURN_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up

# Reaching to something (reach_at, work_reach: the game's Flick and Open, a switch, a lamp or a lid stood at): stood, the
# right hand out in front about the chest's height, palm forward and fingers up, pressed in once a loop. The whole
# figure, in place of the idle, as a work kind. The wrist from the right shoulder in a level frame turned as the chest is,
# shares of the arm's reach: x out (- is right), y forward (-), z up.
REACH_SECONDS = 1.5   # one press
REACH_HAND = (-0.15, -0.85, -0.1)   # out in front a little under the shoulder
REACH_PUSH = 0.08     # the wrist pressed further in at the middle of the loop (shares of the reach)
REACH_TIP = 30.0      # the fingers tipped forward from straight up (degrees)
REACH_LEAN = 6.0      # the trunk leant forward toward it (degrees)
REACH_LOOK = 8.0      # the head bowed to look at the hand (degrees)
REACH_CURL = (0.15, 0.35)   # the fingers' curl, as it presses and between (shares of FIST)
REACH_ELBOW = (-0.6, 0.3, -0.8)   # the elbow out and down (chest's frame)

# Hip gear (GEAR's 'hip' mount: IsoRim's psychic lances, orbital targeters and tornado generator, apparel the game uses
# through a verb of its own, as it does a weapon). The left hand takes it from its slot on the right shoulder (hip_draw;
# the game plays it backwards to put it back), holds it ready while drafted (hip_hold) and points it at the target while
# it is used (hip_aim, one frame per pitch as the aim clips have, and hip_fire, the push as it goes off). Holding it, only
# the left arm is laid over the rest (HIP_UPPER), so the right keeps its weapon; pointing it and taking it, the trunk and
# head come too (HIP_AIM_UPPER), so it points where the target is and the hand reaches across. The tool rides the left
# hand (anim.txt's 'tool hip' line, hip_rest): a rod, IsoRim's model running up its y, gripped low in the closed fist
# as a handle is (hip_grip_rest), its tip out of the thumb's side and its face (the side away from its clip) out of the
# back of the hand. Held, the hand lies straight on the forearm with the palm in, and the rod points where that takes it.
HIP_SECONDS = 4.0    # the hold's loop: one breath
HIP_GRIP = (0.232, 0.24, 0.0)   # where the fist closes on the tool, IsoRim's figure space (model units, x right, y up):
                                # low on the rod, which runs up y beside its clip at x 0.19
HIP_TILT = 30.0      # the rod's way through the fist at rest: across it from the little finger to the thumb, as a
                     # handle is held (grip_handle), tipped this many degrees toward the fingertips; the fist closes
                     # round it WORK_GRIP along the hand from the wrist and WORK_PALM out of the palm
HIP_PALM = (-1.0, 0.0, 0.0)   # the way the left palm faces holding it and pointing it (chest's frame): in, toward
                              # the body's middle, the thumb on top
HIP_HOLD = (0.2, -0.38, 0.5)    # the left fist held ready: leg lengths from the middle of the hips at rest, in the
                                # chest's frame (x left, y back, z up)
HIP_ELBOW = 10.0     # degrees the elbow is swung out and up (arm_to's lift), holding it and pointing it
HIP_REACH = 0.85     # pointing it, how far out from the shoulder the fist is (shares of the arm's reach), along a line
                     # let down from the aim until the wrist is straightest (hip_aim_at)
HIP_ACROSS = 0.08    # and how far in toward the body's middle (leg lengths), so it points from in front of the chest
HIP_TWIST = -18.0    # the trunk turned to bring the left shoulder forward pointing it (degrees, + to the left); the
                     # head takes it back
HIP_LEAN = 6.0       # and leant into it (degrees)
HIP_KICK = 0.06      # leg lengths the fist is thrust out along the aim as it goes off (hip_fire)
HIP_RISE = 10.0      # degrees the rod tips up as it goes off
HIP_DRAW_FRAMES = 24
HIP_DRAW_SECONDS = 0.9   # game seconds to take it from the shoulder to the hold (and to put it back)
HIP_GRAB = 0.45      # share of hip_draw at which the hand closes on it in the slot
HIP_SLIDE = 0.2      # share of hip_draw after that over which the game moves it from the slot into the hand: the slot
                     # rides the right arm, which the clip leaves to the stance, so the hand may not meet it exactly
HIP_APPROACH = 0.12  # leg lengths in front of the slot the hand comes in from and leaves by
HIP_DRAW_TWIST = -25.0   # degrees the trunk turns to the right to bring the slot to the left hand (+ to the left)
HIP_DRAW_LEAN = 8.0  # and leans forward (degrees)
HIP_UPPER = {'shoulder.L': 1.0}   # in place of UPPER: holding it, the left arm only
HIP_AIM_UPPER = {'spine.001': 0.5, 'spine.002': 1.0}   # pointing it and taking it, as CAST_UPPER (the right arm left out)

# Shields (Combat Extended's melee and ballistic shields, on its Shield apparel layer; anim_scripts/make_shields.py): held
# by a grip in the left fist, as the round shield's boss is over a centre grip and a ballistic shield has a handle
# behind it. The chibi's arms are too short to strap one across a forearm in front of the chest. The game shows a
# shield only when CE would draw it (drafted, or a job or duty that shows the weapon), and then lays the guard (shield_at)
# over the rest with the left arm only (SHIELD_UPPER): the left fist out in front of the left shoulder, palm in, the hand
# straight on the forearm, so the grip stands upright and the shield faces forward. The shield rides hand.L
# (shield_rest), placed from the guard's first frame: upright, its face turned SHIELD_YAW out to the left, so its right
# edge falls back clear of a pistol held in the middle. Change these, then run make_shields.py again and export.
SHIELD_SECONDS = 4.0   # the guard's loop: one breath
SHIELD_FIST = (0.36, -0.34, 0.36)   # the left fist: leg lengths from the middle of the hips at rest, in the chest's frame
                                    # (x left, y back, z up): in front of the left shoulder, below it
SHIELD_PALM = (-1.0, 0.0, 0.0)   # the way the left palm faces (chest's frame): in, the thumb on top
SHIELD_ELBOW = 25.0  # degrees the elbow is swung out and up (arm_to's lift), so it clears the chest
SHIELD_YAW = 22.0    # degrees the shield's face is turned out to the left of straight ahead in the guard
SHIELD_TIP = 4.0     # and its top tipped back
SHIELD_UPPER = {'shoulder.L': 1.0}   # in place of UPPER: the left arm only
# Taking a shield carried on the back into the guard (shield_draw_at, one clip per shield, shield_draw_<garment>, as each
# sits on the back in its own .glb): the left hand round the left side to the shield's left edge (shield_rim: the edge
# at the middle of its height), the trunk turned to bring it in reach, closing on the edge palm in by SHIELD_GRAB; then
# round the side again into the guard. The game moves the shield from the back into the hand from there: first onto
# the hand as the clip holds it at the grab (held_grip, over SHIELD_SLIDE, as the game pushes it out past what is on the
# back), then turning in the hand to the guard's grip (held_at) for the rest of the clip. It plays it backwards to put
# the shield back.
SHIELD_DRAW_FRAMES = 24
SHIELD_DRAW_SECONDS = 1.0   # game seconds to take it from the back into the guard (and to put it back)
SHIELD_GRAB = 0.45   # share of shield_draw at which the hand closes on the shield's edge
SHIELD_SLIDE = 0.12  # share after that over which the game takes the shield from where it is on the back onto the hand
SHIELD_APPROACH = 0.12   # leg lengths out to the left (and forward) of the edge the hand comes in from and leaves by
SHIELD_RIM_PALM = (-1.0, 0.0, 0.0)   # the way the palm faces on the edge (chest's frame): in, toward the shield's middle
SHIELD_DRAW_TWIST = 45.0   # degrees the trunk may turn to the left to bring the edge into reach, at most (searched)
SHIELD_DRAW_LEAN = 10.0    # and lean forward
SHIELD_DRAW_POLE = (0.8, 1.0, -0.8)   # the way the elbow points reaching for the edge and taking it (chest's frame: x left,
                                      # y back, z up; arm_to's pole), eased in from hanging and back out into the guard:
                                      # left to hang, it folded in behind the shoulder blade and then wrung round the side
SHIELD_DRAW_UPPER = {'spine.001': 0.5, 'spine.002': 1.0}   # in place of UPPER: the trunk and the left arm

# Flinching from a hit (flinch_at): one jolt, the trunk thrown the way the blow pushes it, the head held back up out of
# it, the shoulders shrugged and the forearms drawn up in front. It is added on top of whatever the pawn is doing (the
# game's Nudge: each bone's turn from the clip's first frame, which is the plain stance), so one clip works standing,
# walking, aiming or lying. One clip per way the figure is pushed (flinch_<way>: F forward, B back, L to its left, R to
# its right); the game mixes them by where the hit came from and plays them by the ticks since it (WalkFigures.FlinchOf).
FLINCH_FRAMES = 12
FLINCH_AT = 0.2      # share of the clip at which the jolt is furthest
FLINCH_LEAN = 18.0   # the trunk thrown (degrees), spine and chest half each
FLINCH_LOOK = 0.6    # share of that the head takes back
FLINCH_SHRUG = 12.0  # the shoulders drawn up (degrees)
FLINCH_ARMS = 15.0   # the upper arms brought forward from the shoulder (degrees)
FLINCH_FOREARMS = 30.0   # and the forearms drawn up from the elbow (degrees)
FLINCH_UPPER = {'spine.001': 1.0}   # the bones it nudges: from the waist up

# Staggered (the game's StaggerHandler: knocked off balance and slowed for its ticks; reel_at): a stumble to one knee and
# back up, once through, which the game plays by how far through the stagger the pawn is (WalkFigures.ReelOf), not on
# the clock. The trunk lurches forward with the arms thrown out, the knee on REEL_KNEE's side goes down to the ground
# behind the other foot, set forward flat, the near hand braced on the forward knee and the other out for balance; it
# stays down a moment, then pushes up off that knee and stands. Each REEL_KEYS line is one key, REEL_AT of the way through:
# hips the hip joint's place (y back of rest, z over the toe joint's rest height; leg lengths), pitch the trunk tipped
# forward and look the head turned back up from that (degrees); front the forward foot's ankle and back the kneeling
# leg's (x out from the middle, y back of rest, z over the toe joint's rest height, 0.03 at rest; leg lengths), toes that
# foot's pitch (degrees, + heel up: about 85 stands it on its toes); brace the forward hand's share of the way from its
# arm key to the forward knee; arm and out each wrist from its shoulder as JUMP_HANDS, in the chest's frame (arm the
# bracing hand's, out the other). The keys are joined smoothly (reel_key). Standing still, the whole figure; on the
# move (the game slows a staggered pawn to a crawl), from the waist up (REEL_UPPER).
REEL_SECONDS = 1.6   # game seconds (the game's stagger is 95 ticks, about 1.6 s; only how the keys space out)
REEL_FRAMES = 32
REEL_KNEE = 'R'      # the knee that goes down (the variant takes the other)
REEL_AT = (0.0, 0.14, 0.32, 0.62, 0.84, 1.0)
REEL_KEYS = dict(
    hips=((0.0, 1.0), (-0.06, 0.9), (0.02, 0.58), (0.02, 0.6), (-0.02, 0.8), (0.0, 1.0)),
    pitch=(0.0, 28.0, 26.0, 14.0, 30.0, 0.0),
    look=(0.0, 20.0, 18.0, 10.0, 22.0, 0.0),
    front=((0.17, 0.0, 0.03), (0.17, -0.3, 0.12), (0.16, -0.46, 0.03), (0.16, -0.46, 0.03), (0.16, -0.4, 0.03), (0.17, 0.0, 0.03)),
    back=((0.17, 0.0, 0.03), (0.17, 0.08, 0.06), (0.15, 0.42, 0.22), (0.15, 0.42, 0.22), (0.16, 0.2, 0.1), (0.17, 0.0, 0.03)),
    toes=(0.0, 20.0, 85.0, 85.0, 50.0, 0.0),
    brace=(0.0, 0.0, 0.9, 1.0, 1.0, 0.0),
    arm=((0.3, -0.15, -0.9), (0.5, -0.7, 0.05), (0.3, -0.7, -0.5), (0.3, -0.7, -0.5), (0.3, -0.7, -0.5), (0.3, -0.15, -0.9)),
    out=((0.3, -0.15, -0.9), (0.7, -0.5, 0.2), (0.8, -0.3, -0.2), (0.75, -0.25, -0.4), (0.7, -0.4, -0.2), (0.3, -0.15, -0.9)),
)
REEL_BRACE = (0.0, 0.06, 0.09)   # the bracing wrist from the forward knee joint (x out, y back, z up; leg lengths)
REEL_UPPER = {'spine.001': 1.0}   # on the move: from the waist up

# Tossing (toss_at): one underhand pitch of a horseshoe or a hoopstone stone (the game's Play_Horseshoes and Play_Hoopstone,
# which throw a fleck at the pin every 400 ticks), right-handed, once through, its first and last frames the idle's first.
# The thing is brought up in front of the belly, the arm swung straight back past the hip as the trunk leans and the
# knees give, then forward with a step onto the left foot, let go out in front a little below the shoulder (TOSS_RELEASE
# of the way through, the tick the game throws the fleck: WalkFigures.TossOf), followed up, and the foot stepped back.
# Keyed at TOSS_AT and joined smoothly (toss_key). Standing, the whole figure in place of the idle; seated (a pin played
# from a seat), from the waist up (TOSS_UPPER).
#   'arm' / 'other': the right / left wrist from its shoulder in the chest's frame (x out, y back, z up; shares of the
#     arm's reach), None for where the standing pose hangs it.
#   'palm' / 'fingers': the right palm's way and its fingers' (the chest's frame, x out), the hand cupped under the thing.
#   'curl': the right fingers' curl (shares of FIST), opening as it lets go.
#   'lean' (forward), 'twist' (+ to the left) degrees, the head taking TOSS_LOOK of both back so it watches the pin;
#   'lunge' (the hips forward) and 'drop' (down, knees bent) leg lengths; 'step' and 'lift' the left foot forward and up,
#   leg lengths; 'heel' the right heel up off its toes (degrees) as it pushes off.
TOSS_SECONDS = 1.4   # game seconds, wind-up to the foot back (84 ticks, in the 400 between throws)
TOSS_FRAMES = 28
TOSS_AT = (0.0, 0.2, 0.42, 0.6, 0.76, 0.9, 1.0)
TOSS_RELEASE = 0.6   # TOSS_AT's let-go key
TOSS_LOOK = 0.7
TOSS_KEYS = dict(
    arm=(None, (0.05, -0.5, -0.6), (0.12, 0.6, -0.78), (0.05, -0.8, -0.5), (0.0, -0.9, 0.2), (0.05, -0.5, -0.65), None),
    palm=((-1.0, 0.0, 0.0), (0.0, -0.2, 1.0), (0.0, -0.78, -0.6), (0.0, -0.6, 0.8), (0.0, 0.2, 1.0), (-0.5, -0.3, 0.5), (-1.0, 0.0, 0.0)),
    fingers=((0.0, 0.0, -1.0), (0.0, -1.0, -0.2), (0.0, 0.6, -0.8), (0.0, -0.8, -0.6), (0.0, -0.95, 0.2), (0.0, -0.6, -0.8), (0.0, 0.0, -1.0)),
    curl=(0.0, 0.55, 0.55, 0.3, 0.05, 0.1, 0.0),
    other=(None, (0.25, -0.35, -0.75), (0.3, -0.6, -0.45), (0.3, 0.1, -0.9), (0.3, 0.25, -0.85), (0.25, 0.05, -0.9), None),
    lean=(0.0, 6.0, 22.0, 14.0, 8.0, 4.0, 0.0),
    twist=(0.0, 0.0, -18.0, 8.0, 12.0, 4.0, 0.0),
    lunge=(0.0, 0.0, -0.03, 0.07, 0.08, 0.03, 0.0),
    drop=(0.0, 0.01, 0.07, 0.04, 0.02, 0.01, 0.0),
    step=(0.0, 0.0, 0.06, 0.26, 0.26, 0.13, 0.0),
    lift=(0.0, 0.0, 0.04, 0.0, 0.0, 0.04, 0.0),
    heel=(0.0, 0.0, 0.0, 15.0, 20.0, 8.0, 0.0),
)
TOSS_UPPER = {'spine.001': 1.0}   # seated: from the waist up

# Throwing a snowball (snowball_at: Romance on the Rim's snowball fight, RotR_Job_SnowballFight, which throws a snowball
# fleck at the partner every SNOW_INTERVAL ticks while it stands at its spot): one quick overhand throw, right-handed, once
# through, its first and last frames the idle's first, by toss_at with these keys (as TOSS_KEYS). The ball is brought up
# and back by the ear as the trunk turns away and the left hand points at the partner, then whipped forward over the
# shoulder with a short step onto the left foot, let go at SNOW_RELEASE (the tick the game throws the fleck:
# WalkFigures.TossPhase) and followed through across the body. Short enough to fit between throws.
SNOW_INTERVAL = 40   # game ticks between throws (JobDriver_SnowballFight's ThrowInterval)
SNOW_SECONDS = 0.6   # game seconds, wind-up to the foot back (36 ticks)
SNOW_FRAMES = 18
SNOW_AT = (0.0, 0.32, 0.55, 0.78, 1.0)
SNOW_RELEASE = 0.55   # SNOW_AT's let-go key
SNOW_LOOK = 0.8
SNOW_KEYS = dict(
    arm=(None, (0.3, 0.35, 0.45), (0.08, -0.8, 0.4), (-0.15, -0.65, -0.45), None),
    palm=((-1.0, 0.0, 0.0), (0.0, -1.0, 0.2), (0.0, -1.0, -0.2), (0.3, 0.0, -1.0), (-1.0, 0.0, 0.0)),
    fingers=((0.0, 0.0, -1.0), (0.0, 0.2, 1.0), (0.0, -0.4, 0.9), (0.0, -0.8, -0.6), (0.0, 0.0, -1.0)),
    curl=(0.0, 0.6, 0.15, 0.1, 0.0),
    other=(None, (0.3, -0.8, 0.15), (0.35, 0.15, -0.75), (0.3, 0.1, -0.85), None),
    lean=(0.0, -4.0, 12.0, 9.0, 0.0),
    twist=(0.0, -24.0, 14.0, 10.0, 0.0),
    lunge=(0.0, -0.02, 0.05, 0.03, 0.0),
    drop=(0.0, 0.02, 0.03, 0.02, 0.0),
    step=(0.0, 0.03, 0.12, 0.1, 0.0),
    lift=(0.0, 0.04, 0.0, 0.0, 0.0),
    heel=(0.0, 0.0, 12.0, 10.0, 0.0),
)
SNOW_UPPER = {'spine.001': 1.0}   # seated: from the waist up

# Dressing (dress_at: the game's Wear job, which stands facing the apparel for its EquipDelay, and RemoveApparel, which
# the game plays backwards). One clip per kind of apparel, each once through, standing, the whole figure, its first and
# last frames the idle's first:
#   legs: trousers. Squatted and bent over with the hands holding the waistband open by the knees, one foot stepped in
#     and then the other, then pulled up to the waist and tugged.
#   over: a shirt pulled on over the head. Held out in front, raised up over the head with it ducked into it (the
#     chibi's arms reach only beside the head, not over it), drawn down past the head and the hem tugged down.
#   coat: a coat, jacket or vest swung on. Held up in front by the collar, the right arm back into its sleeve behind
#     as the trunk turns, then the left, and the lapels tugged straight.
#   head: a hat, helmet or mask. Held in front of the face, put on with a hand each side of the head, and settled.
#   belt: a belt or pack. The hands round the back and to the buckle at the front, fastening it with the head bowed.
# Each has a stretch, DRESS[kind]['loop'] (the tugging and settling), that the game keeps at its own pace in a long
# EquipDelay (power armour), slowing the rest of the clip instead: written as a `dress_loop <clip> from to` line.
# Keyed at 'at' and joined smoothly (toss_key). Keys:
#   'hips': the hips moved (x to the figure's left, y back, z up; leg lengths); 'pitch' the body bent forward about
#     the hip joints, 'lean' the spine curled further, 'twist' the trunk turned (+ to the left), 'look' the head bowed
#     (+ down) and 'tilt' it tipped (+ to its left), degrees.
#   'foot_<side>': that ankle moved from where the standing pose has it (x out, y back, z up; leg lengths).
#   'hands' (both, mirrored) or '<side>': each palm's middle as (landmark, (x out, y back, z up)) from a landmark of
#     the pose as bent (ankle, knee and hip of the hand's side, its shoulder joint, or the waist and head joints in
#     the middle; leg lengths), None for where the standing pose hangs it; 'palm[_<side>]' the palm's facing and
#     'fingers[_<side>]' the fingers' way (x out, y back, z up), 'curl[_<side>]' their curl (shares of FIST).
DRESS_FPS = 20   # frames a game second of the clip's own length
DRESS_HANG = ((-1.0, 0.0, 0.0), (0.0, 0.0, -1.0))   # the palm's facing and the fingers' way hanging (in, down)
DRESS = dict(
    legs=dict(
        seconds=3.2,
        at=(0.0, 0.13, 0.25, 0.36, 0.48, 0.59, 0.72, 0.8, 0.87, 0.94, 1.0),
        loop=(7, 9),
        keys=dict(
            hips=((0, 0, 0), (0, 0.03, -0.22), (0.05, 0.02, -0.18), (0, 0.03, -0.22), (-0.05, 0.02, -0.18), (0, 0.03, -0.22),
                  (0, 0.01, -0.06), (0, 0, -0.02), (0.015, 0, -0.03), (0, 0, -0.02), (0, 0, 0)),
            pitch=(0.0, 45.0, 40.0, 45.0, 40.0, 45.0, 15.0, 4.0, 6.0, 4.0, 0.0),
            lean=(0.0, 12.0, 10.0, 12.0, 10.0, 12.0, 5.0, 2.0, 4.0, 2.0, 0.0),
            look=(0.0, 5.0, 5.0, 5.0, 5.0, 5.0, 8.0, 10.0, 14.0, 10.0, 0.0),
            foot_R=((0, 0, 0), (0, 0, 0), (0.0, -0.14, 0.36), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0)),
            foot_L=((0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0.0, -0.14, 0.36), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0), (0, 0, 0)),
            hands=(None, ('knee', (0.05, -0.12, 0.06)), ('knee', (0.05, -0.12, 0.06)), ('knee', (0.05, -0.12, 0.06)), ('knee', (0.05, -0.12, 0.06)),
                   ('knee', (0.05, -0.12, 0.06)), ('hip', (0.08, -0.12, -0.15)), ('hip', (0.1, -0.05, 0.0)), ('hip', (0.12, -0.02, 0.04)),
                   ('hip', (0.1, -0.05, 0.0)), None),
            palm=(DRESS_HANG[0],) + ((-0.5, 0.8, 0.0),) * 6 + ((-1.0, 0.3, 0.0),) * 3 + (DRESS_HANG[0],),
            fingers=(DRESS_HANG[1],) + ((0.0, -0.3, -1.0),) * 6 + ((0.0, -0.4, -1.0),) * 3 + (DRESS_HANG[1],),
            curl=(0.2, 0.6, 0.6, 0.6, 0.6, 0.6, 0.6, 0.6, 0.65, 0.6, 0.2),
        )),
    over=dict(
        seconds=2.4,
        at=(0.0, 0.15, 0.33, 0.47, 0.62, 0.72, 0.8, 0.88, 1.0),
        loop=(5, 7),
        keys=dict(
            hips=((0, 0, 0), (0, 0, -0.02), (0, 0, -0.03), (0, 0, -0.02), (0, 0, -0.02), (0, 0, -0.02), (0, 0, -0.03), (0, 0, -0.02), (0, 0, 0)),
            pitch=(0.0, 5.0, 8.0, 6.0, 4.0, 2.0, 4.0, 2.0, 0.0),
            lean=(0.0, 5.0, 10.0, 6.0, 6.0, 4.0, 6.0, 4.0, 0.0),
            look=(0.0, 10.0, 25.0, 0.0, 8.0, 12.0, 15.0, 12.0, 0.0),
            hands=(None, ('shoulder', (-0.05, -0.3, -0.15)), ('head', (0.2, -0.12, 0.25)), ('head', (0.36, -0.05, 0.05)),
                   ('shoulder', (0.02, -0.1, -0.45)), ('hip', (0.0, -0.12, -0.02)), ('hip', (0.04, -0.1, -0.07)),
                   ('hip', (0.0, -0.12, -0.02)), None),
            palm=(DRESS_HANG[0], (-0.7, -0.5, 0.2), (-0.7, 0.0, 0.5), (-1.0, 0.0, 0.0), (-1.0, 0.2, 0.0),
                  (-0.4, 0.9, 0.0), (-0.4, 0.9, 0.0), (-0.4, 0.9, 0.0), DRESS_HANG[0]),
            fingers=(DRESS_HANG[1], (0.0, -0.3, 1.0), (0.0, -0.6, 0.8), (0.0, 0.0, 1.0), (0.0, 0.0, -1.0),
                     (0.0, -0.3, -1.0), (0.0, -0.3, -1.0), (0.0, -0.3, -1.0), DRESS_HANG[1]),
            curl=(0.2, 0.6, 0.6, 0.5, 0.6, 0.6, 0.7, 0.6, 0.2),
        )),
    coat=dict(
        seconds=3.0,
        at=(0.0, 0.12, 0.26, 0.38, 0.52, 0.64, 0.74, 0.82, 0.9, 1.0),
        loop=(6, 8),
        keys=dict(
            hips=((0, 0, 0),) + ((0, 0, -0.02),) * 8 + ((0, 0, 0),),
            twist=(0.0, 0.0, -32.0, -10.0, 32.0, 10.0, 0.0, 0.0, 0.0, 0.0),
            lean=(0.0, 4.0, 6.0, 4.0, 6.0, 4.0, 2.0, 4.0, 2.0, 0.0),
            look=(0.0, 8.0, 0.0, 5.0, 0.0, 5.0, 10.0, 12.0, 10.0, 0.0),
            R=(None, ('shoulder', (-0.15, -0.32, -0.2)), ('hip', (0.14, 0.24, 0.08)), ('shoulder', (0.05, -0.15, -0.42)),
               ('shoulder', (-0.28, -0.12, 0.0)), ('shoulder', (-0.12, -0.2, -0.12)), ('shoulder', (-0.1, -0.18, -0.12)),
               ('shoulder', (-0.1, -0.2, -0.25)), ('shoulder', (-0.1, -0.18, -0.12)), None),
            L=(None, ('shoulder', (-0.15, -0.32, -0.2)), ('shoulder', (-0.28, -0.12, 0.0)), ('shoulder', (-0.12, -0.2, -0.12)),
               ('hip', (0.14, 0.24, 0.08)), ('shoulder', (0.05, -0.15, -0.42)), ('shoulder', (-0.1, -0.18, -0.12)),
               ('shoulder', (-0.1, -0.2, -0.25)), ('shoulder', (-0.1, -0.18, -0.12)), None),
            palm_R=(DRESS_HANG[0], (-0.7, -0.5, 0.2), (-1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (-0.3, 0.9, 0.3),
                    (-0.4, 0.9, 0.0), (-0.4, 0.9, 0.0), (-0.4, 0.9, 0.0), (-0.4, 0.9, 0.0), DRESS_HANG[0]),
            fingers_R=(DRESS_HANG[1], (0.0, -0.3, 1.0), (0.0, 0.4, -1.0), (0.0, -0.3, -1.0), (0.0, 0.0, 1.0),
                       (0.3, 0.0, 1.0), (0.3, 0.0, 1.0), (0.3, 0.0, 1.0), (0.3, 0.0, 1.0), DRESS_HANG[1]),
            palm_L=(DRESS_HANG[0], (-0.7, -0.5, 0.2), (-0.3, 0.9, 0.3), (-0.4, 0.9, 0.0), (-1.0, 0.0, 0.0),
                    (-1.0, 0.0, 0.0), (-0.4, 0.9, 0.0), (-0.4, 0.9, 0.0), (-0.4, 0.9, 0.0), DRESS_HANG[0]),
            fingers_L=(DRESS_HANG[1], (0.0, -0.3, 1.0), (0.0, 0.0, 1.0), (0.3, 0.0, 1.0), (0.0, 0.4, -1.0),
                       (0.0, -0.3, -1.0), (0.3, 0.0, 1.0), (0.3, 0.0, 1.0), (0.3, 0.0, 1.0), DRESS_HANG[1]),
            curl=(0.2, 0.6, 0.4, 0.3, 0.6, 0.6, 0.7, 0.75, 0.7, 0.2),
        )),
    head=dict(
        # Put on from the front, the right hand holding it by the front brim (the game draws the hat there, tipped up on
        # edge, then flying the last of the way onto the head, as the chibi's arms don't reach a hat on its big head:
        # WalkFigures' DressHat; HAT_HELD, HAT_RELEASE and HAT_LANDS are its shares), the left hand meeting it as it lands.
        seconds=2.0,
        at=(0.0, 0.18, 0.38, 0.5, 0.6, 0.7, 0.8, 1.0),
        loop=(4, 6),
        keys=dict(
            look=(0.0, 10.0, -8.0, 6.0, 4.0, 8.0, 4.0, 0.0),
            tilt=(0.0, 0.0, 0.0, 0.0, 0.0, -4.0, 0.0, 0.0),
            lean=(0.0, 4.0, 0.0, 3.0, 2.0, 3.0, 2.0, 0.0),
            R=(None, ('shoulder', (-0.05, -0.36, -0.18)), ('head', (0.1, -0.32, 0.1)), ('head', (0.1, -0.14, 0.34)),
               ('head', (0.34, -0.14, 0.2)), ('head', (0.34, -0.18, 0.15)), ('head', (0.34, -0.14, 0.2)), None),
            L=(None, None, None, ('shoulder', (0.02, -0.12, -0.3)), ('head', (0.36, -0.06, 0.15)), ('head', (0.36, -0.08, 0.12)),
               ('head', (0.36, -0.06, 0.15)), None),
            palm_R=(DRESS_HANG[0], (-0.6, 0.7, 0.2), (-0.3, 0.9, 0.2), (-0.2, 0.7, 0.6), (-1.0, 0.0, 0.0), (-1.0, 0.1, -0.1), (-1.0, 0.0, 0.0), DRESS_HANG[0]),
            fingers_R=(DRESS_HANG[1], (0.0, -0.3, 1.0), (0.0, 0.0, 1.0), (0.0, 0.4, 1.0), (0.0, 0.0, 1.0), (0.0, -0.2, 1.0), (0.0, 0.0, 1.0), DRESS_HANG[1]),
            palm_L=(DRESS_HANG[0], DRESS_HANG[0], DRESS_HANG[0], (-1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (-1.0, 0.1, -0.1), (-1.0, 0.0, 0.0), DRESS_HANG[0]),
            fingers_L=(DRESS_HANG[1], DRESS_HANG[1], DRESS_HANG[1], (0.0, -0.3, -1.0), (0.0, 0.0, 1.0), (0.0, -0.2, 1.0), (0.0, 0.0, 1.0), DRESS_HANG[1]),
            curl_R=(0.2, 0.7, 0.7, 0.6, 0.35, 0.5, 0.35, 0.2),
            curl_L=(0.2, 0.2, 0.2, 0.3, 0.35, 0.5, 0.35, 0.2),
        )),
    belt=dict(
        seconds=1.8,
        at=(0.0, 0.2, 0.42, 0.55, 0.66, 0.77, 1.0),
        loop=(3, 5),
        keys=dict(
            pitch=(0.0, 3.0, 8.0, 8.0, 8.0, 8.0, 0.0),
            look=(0.0, 5.0, 25.0, 28.0, 25.0, 28.0, 0.0),
            hands=(None, ('hip', (0.12, 0.12, 0.02)), ('waist', (0.05, -0.2, 0.02)), ('waist', (0.04, -0.21, 0.0)),
                   ('waist', (0.07, -0.2, -0.02)), ('waist', (0.04, -0.21, 0.0)), None),
            palm=(DRESS_HANG[0], (-1.0, 0.3, 0.0), (-0.2, 0.9, 0.0), (-0.2, 0.9, 0.0), (-0.2, 0.9, 0.0), (-0.2, 0.9, 0.0), DRESS_HANG[0]),
            fingers=(DRESS_HANG[1], (0.0, 1.0, -0.3), (-1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), DRESS_HANG[1]),
            curl=(0.2, 0.5, 0.5, 0.6, 0.5, 0.6, 0.2),
        )),
)
DRESS_UPPER = {'spine.001': 1.0}   # its over line, from the waist up (the game plays it whole, standing)

# Kissing (kiss_at: Romance on the Rim's kiss, RomanceOnTheRim_InitiateKiss and _ReceiveKiss, the two a cell apart east
# and west, facing each other; the game draws each KissCloser toward the other, WalkFigures.KissOf). Both play it: leant
# in from the waist, the head tilted to its right (so the two tilt opposite ways) and tipped up a little, both hands at
# the partner's waist, swaying slowly. A loop, laid over the idle from the waist up (KISS_UPPER). Each wrist from its
# shoulder in a level frame turned as the chest is, shares of the arm's reach: x out (+ is left), y forward (-), z up;
# each palm's facing and the fingers' way in the same frame.
KISS_SECONDS = 4.0   # one loop (a common kiss is 250 ticks, a romantic one 500)
KISS_BREATHS = 2
KISS_LEAN = 12.0     # degrees leant in from the waist
KISS_TILT = 16.0     # degrees the head is tilted to its right (rolled about the way it faces)
KISS_CHIN = -4.0     # degrees the head is tipped up (- up)
KISS_SWAY = 2.5      # degrees the trunk sways side to side, once a loop
KISS_WRIST = (0.32, -0.72, -0.4)    # the left wrist (the right mirrored): forward and in, about the partner's waist
KISS_PALM = (-0.8, -0.2, 0.1)       # the left palm faces in, onto the partner's side
KISS_FINGERS = (-0.3, -1.0, -0.1)   # the left fingers forward round it
KISS_CURL = 0.25
KISS_ELBOW = (0.7, 0.3, -0.6)       # the left elbow out and down (the right mirrored)
KISS_UPPER = {'spine.001': 1.0}   # from the waist up

# Beating out a fire (the game's BeatFire job: one beat, Verb_BeatFire, as soon as each 1.1 s cool-down ends, so every
# BEAT_SECONDS). Two ways, sorted in the game by what is burning (WalkFigures.FireKind): stomping (stomp_at, work_stomp)
# for a fire on the floor, an item or a plant, and patting (pat_at, work_pat) for one on a structure or furniture drawn
# BuildHigh or more tall, or on a person standing. Both are loops on the game's beats: the game plays them so a blow
# lands on the tick the game beats (WalkFigures.BeatPhase), STOMP_HIT and PAT_HIT of the way round (PAT_HITS of them a
# loop, a hand each). The whole figure, in place of the idle. Keyed at <name>_AT round the loop and joined smoothly
# round it (loop_key).
BEAT_SECONDS = 67 / 60.0   # game seconds a beat: the verb's 66-tick cool-down and the tick it is cast on
# Stomping: one foot (STOMP_FOOT; work_stomp~2 the other) raised knee high and stamped down ahead onto the fire, ground
# in, then drawn back under the hips, the other foot planted and the weight over it while the stamping foot is up; the
# arms out for balance and swung down with the stamp, the head bowed to watch the foot.
#   'step' / 'lift': the stamping ankle forward and up from where the standing pose plants it (leg lengths); 'pitch' its
#     heel up (degrees; - the toes up); 'grind' its turn about the vertical (degrees, + toes in), the foot screwed in.
#   'lean' (forward, degrees); 'lunge' (the hips forward), 'drop' (down, knees bent) and 'sway' (sideways over the
#     planted foot) leg lengths.
#   'arm' / 'other': the stamping side's / the other side's wrist from its shoulder in the chest's frame (x out, y back,
#     z up; shares of the arm's reach).
STOMP_FOOT = 'R'
STOMP_FRAMES = 20    # one beat
STOMP_HIT = 0.5      # STOMP_AT's stamp
STOMP_LOOK = 8.0     # the head bowed further than the trunk leans, to watch the foot (degrees)
STOMP_AT = (0.0, 0.2, 0.4, 0.5, 0.62, 0.8)
STOMP_KEYS = dict(
    step=(0.03, 0.1, 0.16, 0.26, 0.26, 0.14),
    lift=(0.0, 0.24, 0.36, 0.0, 0.0, 0.06),
    pitch=(0.0, -10.0, -16.0, 0.0, 0.0, 6.0),
    grind=(0.0, 0.0, 0.0, 0.0, 10.0, 4.0),
    lean=(10.0, 12.0, 14.0, 20.0, 18.0, 13.0),
    lunge=(0.0, 0.0, 0.01, 0.06, 0.05, 0.02),
    drop=(0.01, 0.0, 0.0, 0.05, 0.04, 0.02),
    sway=(0.02, 0.06, 0.06, 0.01, 0.0, 0.0),
    arm=((0.3, -0.25, -0.8), (0.45, -0.25, -0.65), (0.55, -0.25, -0.5), (0.3, -0.2, -0.85), (0.3, -0.2, -0.85), (0.3, -0.25, -0.82)),
    other=((0.3, -0.3, -0.8), (0.5, -0.35, -0.6), (0.6, -0.35, -0.45), (0.35, -0.3, -0.8), (0.32, -0.28, -0.82), (0.3, -0.3, -0.8)),
)
# Patting: leant in at the burning thing, the palms slapped down on it in turn, the right then the left, at about a
# table's height in front; the trunk turned a little toward the hand that slaps and bobbing with each slap.
#   PAT_HAND_AT / PAT_WRIST: each hand's wrist through its own beat (its slap at 0, held on, lifted, cocked high and
#     brought down), from its shoulder in a level frame turned as the chest is (x out, y forward (-), z up; shares of
#     the arm's reach); PAT_TIP its fingers tipped down from level (degrees; - up, the palm facing ahead as it is
#     raised); PAT_CURL the fingers' curl (shares of FIST).
PAT_FRAMES = 40      # two beats, a hand each
PAT_HITS = 2
PAT_HIT = 0.25       # the right hand's slap; the left's half a loop on
PAT_LEAN = 24.0      # the trunk leant forward over the thing (degrees)
PAT_BOB = 4.0        # and further at each slap
PAT_TWIST = 6.0      # turned toward the slapping hand (degrees)
PAT_LOOK = 10.0      # the head bowed further, to watch the hands (degrees)
PAT_DROP = 0.03      # the hips let down (knees bent), leg lengths, and PAT_DIP more at each slap
PAT_DIP = 0.015
PAT_LUNGE = 0.03     # the hips forward over the feet, leg lengths
PAT_HAND_AT = (0.0, 0.12, 0.42, 0.78)
PAT_WRIST = ((-0.05, -0.62, -0.72), (-0.05, -0.6, -0.7), (0.1, -0.5, -0.12), (0.06, -0.58, -0.2))
PAT_TIP = (6.0, 4.0, -35.0, -25.0)
PAT_CURL = (0.05, 0.05, 0.2, 0.15)
PAT_ELBOW = (0.7, 0.4, -0.6)   # the elbows out and down (chest's frame, x out)

# Playing a musical instrument (the game's Play_MusicalInstrument: Royalty's piano and harpsichord, played as one
# keyboard, and harp; Ideology's drum, one large tribal drum), stood at it or sat on a seat put at it. The game keeps
# the pawn in the middle of the instrument's interaction cell, out of reach of it, so it is drawn stepped up to it
# (WalkFigures.InstrumentCloser, MUSIC's 'closer': keep the two in step).
#   MUSIC[kind]: 'seconds' a loop; 'closer' (standing, seated) cells the figure is drawn toward the instrument from its
#     cell's middle; 'play' where the hands play, cells for a grown pawn (GAME_SCALE) from the middle of the pawn's cell
#     on the floor (x out to the left, y forward, z up, as IsoRim draws the instrument's model: the keys' tops, the
#     strings, the drum's head; music_play takes the closer and, seated, MUSIC_SEAT off it); 'lean' (standing, seated)
#     the trunk leant toward it (degrees); 'look' (standing, seated) how far the head is bowed from upright to watch
#     the hands (degrees): the neck turned back against the trunk's lean by as much as that is more, so the chibi's big
#     head stays over its shoulders rather than going forward with the lean into the instrument.
# Seated, the figure is the sit's (sit_at), which IsoRim sets on a chair's seat (0.44 cells) MUSIC_SEAT higher than the
# sit has its own floor; the game lays it over the sit from the waist up (EAT_UPPER). A chair is drawn in the middle of
# the cell, so a seated figure can be drawn only a little toward the instrument, and leans further.
# The lean, look and closer are set so the head (and its hair, which stands about 0.05 cells off it) keeps 0.07 cells or
# more off the instrument, the hands falling a little short of it where both can't be had (seated, and at the harp).
# Seated, the figure is perched on the chair's front edge (0.33 cells on), which at the harp is well short of the
# strings (HARP_SIT_REACH).
MUSIC_SEAT = 0.12
MUSIC = {
    # The keys' tops 0.83 cells up and 0.66 .. 0.93 ahead of the pawn's cell's middle (IsoRim's piano.obj and
    # harpsichord.obj, the keyboard toward the interaction cell, two cells from the middle); the case 0.6 ahead.
    'piano': dict(seconds=4.0, closer=(0.35, 0.3), play=(0.0, 0.71, 0.86), lean=(14.0, 24.0), look=(12.0, 10.0)),
    # The strings in a plane square to the pawn 0.98 ahead (harp.obj, a cell from it), from 0.2 to 1.35 up, the
    # soundbox slanting up from the left to the right as the pawn faces it; the base is 0.24 deep either side and 0.1
    # high, so the feet can't go much nearer.
    'harp': dict(seconds=4.0, closer=(0.62, 0.33), play=(0.0, 0.98, 0.9), lean=(6.0, 30.0), look=(-8.0, 0.0)),
    # The head 0.79 up, 0.34 round about a middle a cell ahead (ritual_drum.obj); its body 0.4 round.
    'drum': dict(seconds=2.4, closer=(0.45, 0.33), play=(0.0, 0.81, 0.82), lean=(18.0, 20.0), look=(15.0, 10.0)),
}

def music_play(kind, seated):
    """MUSIC[kind]'s 'play' from the drawn figure's feet (cells): stepped up by its 'closer', seated on the seat."""
    m = MUSIC[kind]
    i = 1 if seated else 0
    x, y, z = m['play']
    return Vector((x, y - m['closer'][i], z - (MUSIC_SEAT if seated else 0.0)))

# The keyboard: each hand's palm about 'play' (cells), the left PIANO_SPAN out to the left and the right as far to the
# right, wandering PIANO_WANDER either way along the keys (the right PIANO_RUNS times a loop, the left PIANO_RUNS - 1,
# so they move apart and together), pressed down PIANO_PRESS (cells) PIANO_PRESSES times a loop (the left half as
# often), the fingers curling between PIANO_CURL (shares of FIST) as they play, the thumb out of step; the fingers
# pointed PIANO_TIP down from level (degrees). The trunk swayed PIANO_SWAY (degrees) after the right hand.
PIANO_SPAN = 0.12
PIANO_WANDER = (0.07, 0.05)   # the left's, the right's
PIANO_RUNS = 2
PIANO_PRESS = 0.012
PIANO_PRESSES = 8
PIANO_CURL = (0.15, 0.45)
PIANO_TIP = 25.0
PIANO_SWAY = 4.0
PIANO_ELBOW = (0.9, 0.3, -0.5)   # the elbows out and down (chest's frame, x out)
# The harp: each hand's palm toward the strings, on them about 'play' (cells): HARP_HANDS (x out to its own side, z up,
# cells), drifting HARP_DRIFT along and up the strings (sines, once a loop and out of step); each hand plucking HARP_PLUCKS
# times a loop (the left's, the right's), its fingers curling from HARP_CURL's first to its second over HARP_PLUCK of
# a pluck and the hand drawn back off the strings HARP_DRAW (cells) as they catch, then opened and laid back on; the
# fingers pointing up and in toward the middle by HARP_FINGERS (degrees from straight up). The head tilted toward the
# soundbox (the right) HARP_TILT degrees, and the trunk swaying HARP_SWAY.
HARP_HANDS = ((0.06, 0.08), (0.04, -0.08))   # the left's, the right's: (out, up)
HARP_DRIFT = (0.04, 0.08)
HARP_PLUCKS = (3, 4)
HARP_PLUCK = 0.3
HARP_CURL = (0.1, 0.75)
HARP_DRAW = 0.03
HARP_FINGERS = 30.0
HARP_TILT = 8.0
HARP_SWAY = 3.0
HARP_ELBOW = (0.8, 0.2, -0.6)
HARP_SIT_REACH = 0.92   # seated, perched on the chair's front edge out of reach of the strings: the hands as far out as
                        # this share of the arm's reach goes (music_hand), the elbows not locked straight
# The drum: beaten with the bare palms in turn, DRUM_BEATS eighths of a loop at which each hand strikes (the left's, the
# right's; both together on the accent, DRUM_ACCENT, which is beaten harder and nearer the middle). Between strikes a hand
# is raised up to DRUM_LIFT (cells, the most, for a gap of a quarter loop or more; less for a shorter one), slowly at
# first and brought down fast, the fingers tipped up DRUM_TIP (degrees) at the top; each palm DRUM_SPAN out from the
# middle of the head. The hips let down DRUM_DIP (cells) and the trunk leant DRUM_BOB (degrees) further at each strike,
# twice that on the accent, and the head nodding DRUM_NOD (degrees) with them.
DRUM_BEATS = ((1, 4, 7), (0, 2, 4, 5, 6))
DRUM_ACCENT = 4
DRUM_LIFT = 0.12
DRUM_TIP = 30.0
DRUM_SPAN = 0.09
DRUM_DIP = 0.01
DRUM_BOB = 2.0
DRUM_NOD = 4.0
DRUM_CURL = 0.1
DRUM_ELBOW = (0.8, 0.4, -0.5)

# Stunned (the game's StunHandler: an EMP, a psychic shock, a stun throw): standing dazed, slumped on soft knees, the
# arms hanging and the head lolling round, swaying slowly. The whole figure in place of the idle; on the move (pushed),
# from the waist up (DAZE_UPPER).
DAZE_SECONDS = 4.0
DAZE_DROP = 0.08     # hips lowered (leg lengths), knees bent
DAZE_LEAN = 12.0     # the trunk slumped forward (degrees), in place of LEAN
DAZE_BOW = 28.0      # the head hung forward (degrees)
DAZE_LOLL = 10.0     # and rolled round, once a loop (degrees each way)
DAZE_SWAY = 0.5      # the weight swayed from foot to foot (as the idle's sway, -1 .. 1)
DAZE_ARMS = 6.0      # the arms hanging closer in (degrees on top of ARM_DOWN)
DAZE_UPPER = {'spine.001': 1.0}   # in place of UPPER: from the waist up

# The hand at rest, for placing a palm (hand_on): the way the left palm faces (armature space; the right's mirrored),
# measured from the mesh (the hand is thinnest that way: palms down in the A-pose), and half the hand's thickness, from its
# bone to the palm (leg lengths).
HAND_PALM = (-0.01, 0.44, -0.9)
HAND_HALF = 0.04

# Jumping (jump_at: a jump pack, a mech's jump launcher, any PawnFlyer): one pass through the flight, which the game plays
# by how far through it the pawn is (WalkFigures.FlightOf), not on the clock. Pushed off straight with the arms thrown
# up, then the knees tucked and the arms out for balance at the top, then the legs reaching down for the ground with the
# arms forward. The three numbers of JUMP_LEAN, JUMP_LOOK and JUMP_POINT are at (take-off, top, landing), and so are
# the JUMP_OFF_, JUMP_TOP_ and JUMP_LAND_ places; the top is JUMP_TOP of the way through the flight, and each is eased
# into the next (jump_keys). The crouch (land_pose) is held on the ground: sunk into over the jump's warm-up, and dropped
# into on landing.
JUMP_FRAMES = 24
JUMP_TOP = 0.45
JUMP_LEAN = (25.0, 8.0, -4.0)    # the trunk leaned forward (degrees)
JUMP_LOOK = (0.7, 0.6, 0.3)      # share of the lean the head takes back, to look ahead
JUMP_POINT = (45.0, 20.0, 10.0)  # the feet's pitch (degrees, + = toes pointed down)
# The left ankle from its hip joint (x out, y forward (-), z up; leg lengths; the right's mirrored).
JUMP_OFF_ANKLE = (0.0, 0.22, -0.96)    # pushed off, the legs trailing straight
JUMP_TOP_ANKLE = (0.05, -0.2, -0.45)   # tucked up
JUMP_LAND_ANKLE = (0.04, -0.15, -0.92)   # reaching down for the ground
# The right ankle this much further back and lower than the left (leg lengths), so the legs are not in step (none at
# take-off).
JUMP_TOP_SPLIT = (0.1, 0.12)
JUMP_LAND_SPLIT = (0.03, 0.04)
# Each wrist from its shoulder in the chest's frame (x out, y forward (-), z up; shares of the arm's reach).
JUMP_OFF_HANDS = (0.45, -0.45, 0.7)    # thrown up
JUMP_TOP_HANDS = (0.8, -0.25, 0.05)    # out to the sides for balance
JUMP_LAND_HANDS = (0.4, -0.75, -0.2)   # forward, ready to land
JUMP_ELBOW = (1.0, 0.3, -0.6)    # the elbows out and down (chest's frame, x mirrored)
LAND_SECONDS = 0.6   # game seconds a landing crouch takes to come up out of
LAND_DROP = 0.3      # hips lowered (leg lengths), knees bent
LAND_BACK = 0.08     # and moved back over the heels (leg lengths)
LAND_HIPS = 22.0     # hips tipped forward (degrees)
LAND_LOOK = 0.8      # share of that the head takes back, to look ahead
LAND_HANDS = (0.5, -0.65, -0.25)   # each wrist from its shoulder, as JUMP_HANDS: forward and out for balance

# The dive to the ground that hunkering (Combat Extended's HunkerDown) and rolling to put out a fire go down by
# (WalkFigures.DuckOf): the head down and the free hand at the back of the neck by its end, the weapon kept in its hand;
# the upper body thrown forward and the legs kicked back, the hips staying over the cell. Unlike the lie and the crawl it is
# posed where it lies (not made upright), so the game plays it as it does the getup, and backwards to get up again. (The
# game's own reaction to an explosive about to go off is only a sprint away, so nothing dives from one.)
# One of each by the hand that holds the weapon. Each DIVE_KEYS
# line is one key, DIVE_AT of the way through: hips is the hips' place (y back of rest, z the
# hip joint over the toe joint's rest height, leg lengths), pitch the trunk tipped forward, look the head turned back up from that and foot
# the feet's pitch (degrees); ankle is the left ankle (x out, y back of rest, z over the toe joint's rest height, 0.03 at
# rest, leg lengths; the right's moved by DIVE_SPLIT), grip and free each wrist from its shoulder as JUMP_HANDS, in the chest's frame (at the last key z
# is along the body, -y to the ground); the keys are joined smoothly (dive_key).
DIVE_SECONDS = 0.7   # game seconds, standing to down
DIVE_FRAMES = 28
DIVE_AT = (0.0, 0.2, 0.44, 0.7, 1.0)
DIVE_KEYS = dict(
    hips=((0.0, 1.0), (0.05, 0.72), (-0.12, 0.72), (-0.05, 0.38), (0.0, 0.27)),
    pitch=(0.0, 30.0, 62.0, 76.0, 80.0),
    look=(0.0, 18.0, 35.0, 30.0, 12.0),
    foot=(0.0, 0.0, 70.0, 95.0, 105.0),
    ankle=((0.17, 0.0, 0.03), (0.19, 0.0, 0.03), (0.16, 0.56, 0.45), (0.14, 0.8, 0.3), (0.14, 0.85, 0.28)),
    grip=((0.3, -0.15, -0.9), (0.45, -0.6, -0.35), (0.6, -0.55, 0.3), (0.75, -0.45, 0.35), (0.78, -0.25, 0.4)),
    free=((0.3, -0.15, -0.9), (0.45, -0.6, -0.35), (0.4, -0.6, 0.6), (0.5, -0.55, 0.5), (0.4, -0.55, 0.5)),
)
DIVE_SPLIT = (0.04, 0.1, 0.0)    # the right ankle this much (x out, y back, z up; leg lengths) from the left's mirror, at rest none
DIVE_ELBOW = (1.0, 0.3, -0.6)    # the elbows out and down (chest's frame, x mirrored)
PRONE_HAND = (0.12, 0.16, 0.0)   # the free wrist over the back of the head: from the head bone's joint in its frame (x to that
                                 # side, y back, z along the head; leg lengths)
PRONE_COVER = 0.72   # of the dive: from here that hand goes from the ground to the head
PRONE_BREATH = 3.0   # the chest heaved on the in-breath (degrees)
PRONE_DUCK = 4.0     # the head ducked and raised a little (degrees), twice a loop
# Hunkering under fire (Combat Extended's HunkerDown job; the name hunker is the cower's): flat on the front as the dive
# leaves it (the game gets it there by the dive, WalkFigures.DuckOf), the arms up with a hand by
# each side of the head, forearms on the ground, the weapon kept in its hand. Posed where it lies, as the dive is.
# (duck_clip.json, Meshy's CrouchLookAroundBow, the crouch it played before, is kept but no longer read.)
DUCK_SECONDS = 2.4   # game seconds round the loop
DUCK_BREATHS = 4     # quick breaths a loop
DUCK_HAND = (0.42, -0.18, 0.25)  # each wrist by the head: from the head bone's joint in its frame (x to that side, y back,
                                 # so up off the ground, z along the head toward its crown; leg lengths)
DUCK_ELBOW = (1.0, -0.3, -0.5)   # the elbows out, down to the ground and back toward the feet (chest's frame, x mirrored)
DUCK_LOOK = 4.0      # the head turned back up from the trunk's line (degrees): the face kept down
# Stop, drop and roll (the game's ExtinguishSelf job, which only stands the pawn there until the fire is out): down flat on
# the front by the same dive (WalkFigures.DuckOf), then rolled over about the body's long line, from the front over one
# side onto the back and back again, then over the other side, the arms crossed on the chest and the legs straight
# together. Posed where it lies, as the dive is; the game rests it on its lowest point as it turns.
ROLL_SECONDS = 2.4   # game seconds round the loop: over onto the back and back each way
ROLL_TURN = 180.0    # degrees rolled over each way from lying on the front (180 the back)
ROLL_RADIUS = 0.2    # the body's half thickness about its long line (leg lengths): the hips carried sideways that much
                     # per radian rolled, so it rolls over the ground rather than spinning on the spot
ROLL_TRAVEL = 0.6    # share of that taken, so it doesn't leave its cell
ROLL_ANKLE = (0.07, 0.9, 0.2)   # the left ankle as DIVE_KEYS' ankle (the right its mirror): the legs out straight behind
ROLL_FOOT = 100.0    # the feet's pitch (degrees), the toes pointed back
ROLL_WRIST = (-0.15, -0.45, -0.3)   # each wrist from its shoulder as JUMP_HANDS, in the chest's frame (x mirrored, so - is
                                    # across to the other side; -y off the chest, -z toward the hips): crossed on the chest
ROLL_ELBOW = (1.0, 0.2, -0.8)    # the elbows out and down toward the hips (chest's frame, x mirrored)
ROLL_TUCK = 15.0     # the head tucked down toward the chest (degrees), out of the flames

# Variants of the one-shot clips, so the same event is not played the same way every time: the game picks one of a
# clip and its variants at random each time the event starts (SkinnedFigure.Variant). Each key is a clip, and also every
# clip named after it plus '_' and more (so 'flinch' is all four flinch_<way>, 'melee_slash' the spear's and ikwa's own
# too, 'bend' bend_walk as well), which all get the same number of variants, so the game picks the same one of each.
# Each variant is a dict of this module's numbers to pose it with in place of their own (varied), and is exported as
# '<clip>~2', '<clip>~3' and so on. A variant may name a different mocap clip too, e.g. dict(GETUP_CLIP=<another
# import_pluck.py --once json>, GETUP_SECONDS=...) for a second way of getting up. The game also takes variants for
# getup, though there is only the one mocap clip for it so far. The falls a ragdoll starts from share theirs: downed or
# dead, any of FALL_CLIPS.
VARIANTS = {
    # Asleep on the side, still (see SLEEP_HOLD).
    'sleep': [dict(SLEEP_CLIP=DOWNED_SIDE_CLIP, SLEEP_HOLD=True)] if DOWNED_SIDE_CLIP is not None else [],
    'knockdown': [dict(KNOCKDOWN_CLIP=clip) for clip in FALL_CLIPS if clip is not KNOCKDOWN_CLIP],
    'die': [dict(DIE_CLIP=clip) for clip in FALL_CLIPS if clip is not DIE_CLIP],
    'flinch': [
        # The forearms thrown up to shield the face, the trunk turned away less.
        dict(FLINCH_LEAN=12.0, FLINCH_SHRUG=18.0, FLINCH_ARMS=40.0, FLINCH_FOREARMS=70.0),
        # A sharp jolt, the head snapped with the trunk, the arms barely moving.
        dict(FLINCH_AT=0.12, FLINCH_LEAN=26.0, FLINCH_LOOK=0.3, FLINCH_SHRUG=6.0, FLINCH_ARMS=6.0, FLINCH_FOREARMS=12.0),
    ],
    'bend': [
        # Stooped from the hips, the legs straighter; on the move, folded further.
        dict(BEND_DROP=0.28, BEND_BACK=0.1, BEND_HIPS=58.0, BEND_LEAN=14.0, BEND_HANDS=(0.16, -0.5, 0.2),
             BEND_WALK=50.0, BEND_WALK_LOOK=24.0),
        # Squatted right down, the trunk more upright; on the move, folded less.
        dict(BEND_DROP=0.64, BEND_BACK=0.2, BEND_HIPS=24.0, BEND_HANDS=(0.24, -0.68, 0.32),
             BEND_WALK=36.0, BEND_WALK_HANDS=(0.24, -0.6, 0.5)),
    ],
    'reel': [
        # Down on the other knee.
        dict(REEL_KNEE='L'),
    ],
    'work_stomp': [
        # Stamped with the other foot.
        dict(STOMP_FOOT='L'),
    ],
    'land': [
        # Landed heavily, sunk lower with the hands reaching for the ground.
        dict(LAND_DROP=0.42, LAND_HIPS=34.0, LAND_HANDS=(0.35, -0.75, -0.55)),
        # Landed lightly, barely crouched, the arms out wide.
        dict(LAND_DROP=0.18, LAND_HIPS=12.0, LAND_HANDS=(0.75, -0.3, 0.1)),
    ],
    'jump': [
        # Tucked tight at the top, the arms in round the knees.
        dict(JUMP_LEAN=(25.0, 18.0, -4.0), JUMP_TOP_ANKLE=(0.05, -0.3, -0.35), JUMP_TOP_HANDS=(0.3, -0.7, -0.1)),
        # Reaching up all the way, the legs trailing long.
        dict(JUMP_LEAN=(15.0, 0.0, -4.0), JUMP_OFF_HANDS=(0.3, -0.2, 0.95), JUMP_TOP_HANDS=(0.45, -0.1, 0.9),
             JUMP_TOP_ANKLE=(0.05, 0.05, -0.75)),
    ],
    'melee_slash': [
        # A backhand: wound up past the left shoulder and swept across to the right, the other hand drawn back low.
        dict(MELEE=dict(MELEE, slash=dict(
            raised=dict(hand=(0.15, -0.15, 0.62), tip=80.0, swing=100.0, other=(0.28, 0.05, 0.32),
                        lean=0.0, twist=25.0, lunge=-0.03, drop=0.0),
            hit=dict(hand=(-0.05, -0.5, 0.55), tip=90.0, swing=-20.0, other=(0.28, -0.05, 0.34),
                     lean=8.0, twist=-10.0, lunge=0.1, drop=0.05),
            through=dict(hand=(-0.4, -0.3, 0.45), tip=100.0, swing=-90.0, other=(0.28, -0.05, 0.36),
                         lean=10.0, twist=-30.0, lunge=0.08, drop=0.05)))),
    ],
    'melee_stab': [
        # Low, at the belly, stooping into it.
        dict(MELEE=dict(MELEE, stab=dict(
            raised=dict(hand=(-0.28, 0.08, 0.35), tip=90.0, swing=0.0, other=(0.2, -0.4, 0.45),
                        lean=0.0, twist=-22.0, lunge=-0.05, drop=0.02),
            hit=dict(hand=(-0.08, -0.56, 0.32), tip=95.0, swing=0.0, other=(0.25, -0.15, 0.36),
                     lean=18.0, twist=20.0, lunge=0.16, drop=0.12),
            through=dict(hand=(-0.08, -0.54, 0.32), tip=95.0, swing=0.0, other=(0.25, -0.15, 0.36),
                         lean=18.0, twist=18.0, lunge=0.15, drop=0.12)))),
    ],
    'melee_fist': [
        # A hook: swung round from out to the side, the palm down, the shoulder turned well into it.
        dict(MELEE=dict(MELEE, fist=dict(
            raised=dict(hand=(-0.35, -0.1, 0.5), fingers=(0.0, -1.0, 0.3), palm=(0.3, 0.0, -1.0), other=(0.19, -0.3, 0.5),
                        lean=2.0, twist=-20.0, lunge=-0.02, drop=0.0),
            hit=dict(hand=(0.05, -0.45, 0.58), fingers=(1.0, -0.6, 0.0), palm=(0.0, 0.0, -1.0), other=(0.18, -0.22, 0.48),
                     lean=8.0, twist=28.0, lunge=0.1, drop=0.04),
            through=dict(hand=(0.15, -0.4, 0.55), fingers=(1.0, -0.3, 0.0), palm=(0.0, 0.0, -1.0), other=(0.18, -0.22, 0.48),
                         lean=8.0, twist=32.0, lunge=0.09, drop=0.04)))),
    ],
    # Harvesting by hand, crouched and picking (the mocap gather), with no scythe.
    'work_scythe': [dict(WORK_MOCAP=GATHER_CLIP, WORK_MOCAP_TOOL=False)] if GATHER_CLIP is not None else [],
    # Eating at a table with a knife and fork, bowed to the plate (the second mocap clip).
    'eat_sit': [dict(EAT_SIT_CLIP=EAT_SIT2_CLIP, EAT_SIT_SECONDS=EAT_SIT2_CLIP['frames'] / EAT_SIT2_CLIP['fps'],
                     EAT_REACH=False, EAT_LEVEL=(0.0, EAT_LEVEL[1]))] if EAT_SIT2_CLIP is not None else [],
    # Sowing stood and bent down from the hips to the ground and up again (the second mocap clip).
    'work_sow': [dict(SOW_CLIP=BENDSOW_CLIP, SOW_SECONDS=BENDSOW_CLIP['frames'] / BENDSOW_CLIP['fps'],
                      SOW_SPREAD=0.0, SOW_RAISE=0.0)] if BENDSOW_CLIP is not None else [],
}

def variants(clip):
    """The VARIANTS entries for clip (its own, or the one it is named after), in order; none for most clips."""
    for key, numbers in VARIANTS.items():
        if clip == key or clip.startswith(key + '_'):
            return numbers
    return []

@contextlib.contextmanager
def varied(numbers):
    """This module's numbers set from numbers (a VARIANTS entry) while the block runs, then put back."""
    g = globals()
    saved = {k: g[k] for k in numbers}
    g.update(numbers)
    try:
        yield
    finally:
        g.update(saved)

# Working (work_at): one stroke of each tool IsoRim's Work.cs hands a pawn at work, looped, standing still at the work
# (the game turns the figure to face it, WalkFigures.WorkOf). IsoRim's own tool model rides the right hand
# (tool_matrix: a tool line in anim.txt). 'motion' times the stroke as Work.cs does: 'chop' winds up slowly from struck
# to raised over the first 0.55 of it, brings the blow down fast to 0.7 and bounces a little off the work; 'sweep' swings
# evenly from struck to raised and back; 'tap' goes from struck to raised and back 'taps' times. 'ticks': one stroke in
# game ticks (Work.cs's period). Each key ('struck', 'raised') is eased into the other:
#   'fist': where the right hand closes round the handle, leg lengths from the middle of the hips at rest (x to the
#     figure's left, y back (forward is -y), z up), fixed in space so the blow lands in one place.
#   'pitch': the handle tipped forward from upright (degrees; past 90 it points down in front), then 'yaw' turned about
#     the vertical (+ to the figure's left) and 'roll' about its own length. The tool's head is at the far end, its edge
#     (an axe's blade, a hammer's face) forward when upright.
#   'L' (and 'R' with no tool): that wrist placed free, in the same frame; left out, the left hand holds the handle
#     'other' along it from the tool's own grip (model units, before 'scale'), or hangs if 'other' is None.
#   'lean' (the trunk bent forward, degrees), 'twist' (turned about the vertical, + to the left, degrees; the head takes
#     back 'look' of both, to keep its eyes on the work) and 'drop' (hips lowered, leg lengths, knees bent); optional:
#     'shift' (the hips carried forward, leg lengths, the feet kept planted), 'hips' (the hips turned about the
#     vertical over the feet, + to the left, degrees; the head takes back 'look' of it too) and 'tilt' (the trunk bent
#     sideways, + its top to the left, degrees).
# 'path': optional keys between the two, (u, key) with u from 0 (struck) to 1 (raised), each giving only what it changes
#   from struck and raised eased together; work_key draws one smooth curve through them all.
#   A key may also give 'other', so the left hand slides along the handle through the stroke (give it in both struck
#   and raised).
# 'grip': how far along the handle from the tool model's own origin the right hand is (model units, before 'scale').
# 'wrap': the right hand turned about the handle from its fingers pointing the way the edge does (degrees), and 'thumb'
#   which way along the handle its thumb points (1 toward the tool's head, -1 away); 'other_wrap' and 'other_thumb' the
#   left hand's. Left out (as they mostly are), each is chosen with the elbow (work_holds) so the wrist stays as straight
#   as it can over the whole stroke. A hand keeps one hold on the handle all through it: the tool rides it.
# 'body': the stance's own numbers (NARROW, STAGGER) in place of the walk's.
# 'seated': sat (sit_at's pose from the hips down, the trunk bent over it by 'lean', 'twist' and 'tilt'; no 'drop',
#   'shift' or 'hips'), laid over the sit from the waist up in the game (EAT_UPPER).
WORK_GRIP = 0.06     # leg lengths from the wrist along the hand to the middle of the fist round a handle
WORK_PALM = 0.05     # and out of the palm to the handle's middle, so the curled fingers close round it, not through it
MODEL_LEG = 0.4964   # model units per leg length (export_frames.py prints the true figure: keep it in step)
WORK_LOOK = 0.6      # share of the lean and twist the head takes back
# A hand round a handle has its turn set by the tool but for its turn about the handle ('wrap'), so that turn and the
# elbow's swing about the line from the shoulder to the wrist are chosen together (work_holds) to where the wrist is
# least bent and the hand least twisted about the forearm: past half a turn the forearm's roll flips over.
GRIP_SWING = 150.0   # degrees the elbow may swing either way from where arm_to would put it
GRIP_STEP = 5.0      # degrees between the swings tried
GRIP_FREE = 60.0     # degrees the hand may twist about the forearm either side of FOREARM_TWIST_MID before it counts
GRIP_LOW = 0.15      # leg lengths below the shoulder an elbow may come up to before it counts (a winged elbow)
GRIP_RAISE = 400.0   # and its cost past that, as degrees of twist past GRIP_FREE per leg length
GRIP_SAMPLES = 13    # points along the stroke from struck to raised the swing is chosen at, eased between
GRIP_EASE = 4.0      # cost of the swing changing between neighbouring points, per degree squared (twist past GRIP_FREE: 1)
GRIP_KEEP = 0.3      # cost of swinging at all, per degree squared, so a hand that isn't twisted keeps the plain elbow
GRIP_WRAP_STEP = 10.0   # degrees between the turns about the handle tried
GRIP_COARSE = 3      # the turns are compared with the swings this many GRIP_STEPs apart, then the best one finely
GRIP_FLEX = (-10.0, 30.0)   # degrees the wrist bends toward the palm (- back) round the middle of a power grip, and how far
                            # either side of it before it counts, per degree squared
GRIP_DEVIATE = (-10.0, 15.0)   # the same for the hand tipped toward the thumb (- toward the little finger)
# The hand closed round a handle (close_hand), degrees from straight: the fingers at the knuckle and the middle joint
# (the rig's fingers move as one), then the thumb at its root and its tip.
FIST = (85.0, 95.0, 40.0, 30.0)
WORK = {
    # Felling a tree: swung from over the right shoulder into the trunk in front, both hands at the end of the handle.
    'axe': dict(tool='tool_axe', ticks=70, motion='chop', scale=0.8, grip=0.1, other=0.0,
                body=dict(NARROW=0.35, STAGGER=0.2),
                struck=dict(fist=(-0.04, -0.42, 0.3), pitch=95.0, yaw=8.0, roll=0.0, lean=15.0, twist=5.0, drop=0.08),
                raised=dict(fist=(-0.3, -0.18, 0.55), pitch=-30.0, yaw=35.0, roll=0.0, lean=0.0, twist=-30.0, drop=0.02)),
    # Mining: the game mines a wall of rock or ore from the cell beside it, so the pick is swung from high over the right
    # shoulder, over the top and forward into the wall's face at chest height, the point going in square to it. The whole
    # body is in it: raised with the weight on the back foot, the hips turned away and the trunk arched back; then the hips
    # drive round and forward onto the front foot and the knees give as the point bites; then it is worked out of the rock
    # and lifted back round in an arc past the shoulder. The struck fist is set so the point reaches 0.55 cells ahead (measured
    # on the posed figure at the standard grown size, ScaleUp 1.2): the face is half a cell ahead of the pawn's middle.
    # The chibi's head is wider than its shoulders and reaches nearly as far forward as the wall, so the pick can't pass
    # in front of the face: it strikes below the chin, the handle tipped forward and a little across, and on the wind-up
    # goes up past the right side of the head, at least 0.12 leg lengths off it all round (room for built hair).
    'pickaxe': dict(tool='tool_pickaxe', ticks=80, motion='chop', scale=0.8, grip=0.1, other=0.0,
                    body=dict(NARROW=0.3, STAGGER=0.3),
                    struck=dict(fist=(-0.15, -0.37, 0.0), pitch=32.0, yaw=15.0, roll=15.0, lean=-3.0, twist=6.0, drop=0.14,
                                shift=0.04, hips=10.0, tilt=0.0, other=0.3),
                    path=[(0.2, dict(fist=(-0.2, -0.44, 0.2), pitch=7.5, yaw=21.0, lean=-6.5, twist=-3.5, drop=0.1, shift=0.02,
                                     hips=4.0, tilt=10.5, other=0.25)),
                          (0.6, dict(fist=(-0.38, -0.34, 0.62), pitch=-25.0, yaw=25.5, lean=-1.5, twist=-20.5, drop=0.04,
                                     shift=-0.03, hips=-14.0, tilt=7.5, other=0.05))],
                    raised=dict(fist=(-0.4, -0.04, 0.84), pitch=-70.0, yaw=12.0, roll=0.0, lean=-12.0, twist=-32.0, drop=0.02,
                                shift=-0.07, hips=-16.0, tilt=9.0, other=0.0)),
    # Building, repairing, taking down: hammered into a wall's face in front (the frame stands in the next cell, its face
    # half a cell ahead, as the pickaxe's), the handle tipped a little forward at the blow so the face meets it square.
    # Between blows the forearm comes up and back and the wrist cocks, so the head goes back past the right ear; on the
    # way down the fist leads and the head lags behind it (the 'path' key), then the wrist snaps it through. The wrist goes
    # from tipped toward the thumb when cocked to toward the little finger at the blow, as a real hammer's does. All of it
    # stays below the chin, out past the shoulder, as the chibi's head is wider than its shoulders (the head is kept at
    # least 0.06 leg lengths off the body all through). The face lands 0.50 cells ahead at the standard grown size. The
    # wall is out of the left hand's reach, so it hangs loose by the hip, swaying a little against the swing. Found by
    # searching these numbers for the least wrist and forearm strain (work_holds' costs) with the face on the wall.
    'hammer': dict(tool='tool_hammer', ticks=34, motion='chop', scale=0.7, grip=0.05, other=None,
                   body=dict(NARROW=0.35, STAGGER=0.2),
                   struck=dict(fist=(-0.2, -0.54, 0.24), pitch=20.0, yaw=0.0, roll=20.0, lean=9.0, twist=10.0, drop=0.05,
                               shift=0.06, L=(0.2, -0.26, 0.14)),
                   path=[(0.4, dict(fist=(-0.36, -0.52, 0.34), pitch=-15.0))],
                   raised=dict(fist=(-0.28, -0.35, 0.52), pitch=-60.0, yaw=30.0, roll=-15.0, lean=5.0, twist=-15.0,
                               drop=0.03, shift=0.02, L=(0.2, -0.2, 0.12))),
    # At a stonecutter's or sculptor's table, standing: short raps down at the work on the table, the hammer held
    # choked up for control, the thumb toward the head (so the handle comes out of the top of the fist, not from under
    # it), the face coming down onto the work with the wrist tipped toward the little finger, and the left hand holding
    # the work. The face lands where the bench clip's right hand is, as the game draws a pawn stood at a bench stepped
    # up to it (WalkFigures.BenchCloser).
    'hammer_bench': dict(tool='tool_hammer', ticks=30, motion='chop', scale=0.7, grip=0.18, other=None, thumb=1,
                         body=dict(NARROW=0.4),
                         struck=dict(fist=(-0.08, -0.293, 0.146), pitch=70.0, yaw=0.0, roll=0.0, lean=14.0, twist=0.0,
                                     drop=0.03, L=(0.1, -0.46, 0.12)),
                         raised=dict(fist=(-0.1, -0.265, 0.25), pitch=25.0, yaw=0.0, roll=0.0, lean=12.0, twist=-5.0,
                                     drop=0.03, L=(0.1, -0.46, 0.13))),
    # The same sat at the table ('seated': the trunk leant over the sit, its legs left alone, as the game lays it over
    # the sit from the waist up), the arm reached right out so the face lands on the worktop where the seated bench
    # clip's right hand is (0.8 leg lengths ahead). Sat, the shoulder is barely above the worktop, so the handle is
    # flatter at the blow than standing (the face meets the work at a slant) and upright when cocked, which keeps the
    # wrist from bending right over.
    'hammer_bench_sit': dict(tool='tool_hammer', ticks=30, motion='chop', scale=0.7, grip=0.18, other=None, thumb=1,
                             seated=True,
                             struck=dict(fist=(-0.09, -0.515, -0.055), pitch=50.0, yaw=0.0, roll=0.0, lean=28.0,
                                         twist=0.0, L=(0.15, -0.72, -0.03)),
                             raised=dict(fist=(-0.1, -0.559, 0.026), pitch=0.0, yaw=0.0, roll=0.0, lean=26.0,
                                         twist=-5.0, L=(0.15, -0.72, -0.02))),
    # Sowing: the blade raised in front and dug into the ground ahead, the left hand further up the handle.
    'hoe': dict(tool='tool_hoe', ticks=78, motion='chop', scale=0.8, grip=0.05, other=0.12,
                body=dict(NARROW=0.4, STAGGER=0.15),
                struck=dict(fist=(-0.1, -0.4, 0.15), pitch=135.0, yaw=0.0, roll=180.0, lean=15.0, twist=0.0, drop=0.08),
                raised=dict(fist=(-0.2, -0.32, 0.4), pitch=60.0, yaw=0.0, roll=180.0, lean=5.0, twist=-5.0, drop=0.04)),
    # Butchering: quick cuts at the table, the left hand holding the carcass.
    'knife': dict(tool='tool_knife', ticks=30, motion='chop', scale=0.6, grip=0.02, other=None,
                  body=dict(NARROW=0.4),
                  struck=dict(fist=(-0.1, -0.45, 0.1), pitch=110.0, yaw=0.0, roll=0.0, lean=10.0, twist=0.0, drop=0.04,
                              L=(0.12, -0.4, 0.08)),
                  raised=dict(fist=(-0.15, -0.38, 0.25), pitch=40.0, yaw=0.0, roll=0.0, lean=9.0, twist=-3.0, drop=0.04,
                              L=(0.12, -0.4, 0.08))),
    # Harvesting crops: the blade swept low from side to side, the left hand half way up the handle. The model's blade
    # stands out from the handle's end as an axe's edge does, so it is rolled a quarter turn to lie out to the left.
    'scythe': dict(tool='tool_scythe', ticks=64, motion='sweep', scale=0.8, grip=0.05, other=0.1,
                   body=dict(NARROW=0.3),
                   struck=dict(fist=(-0.28, -0.28, 0.22), pitch=125.0, yaw=-40.0, roll=90.0, lean=12.0, twist=-20.0, drop=0.08),
                   raised=dict(fist=(0.05, -0.3, 0.22), pitch=125.0, yaw=40.0, roll=90.0, lean=12.0, twist=20.0, drop=0.08)),
    # Cleaning: the broom swept from side to side over the floor in front, the right hand on top.
    'broom': dict(tool='tool_broom', ticks=40, motion='sweep', scale=0.8, grip=0.05, other=0.15,
                  body=dict(NARROW=0.4),
                  struck=dict(fist=(-0.15, -0.25, 0.35), pitch=150.0, yaw=-25.0, roll=0.0, lean=10.0, twist=-10.0, drop=0.04),
                  raised=dict(fist=(-0.1, -0.25, 0.35), pitch=150.0, yaw=25.0, roll=0.0, lean=10.0, twist=10.0, drop=0.04)),
    # Fishing (Odyssey's Fish, stood on the bank facing the water): the rod (this mod's own model, make_rod.py: 1.7 cells,
    # the reel hanging under it 0.2 up from the butt) held out over the water, the right hand round the reel's stem and
    # the left on the handle behind it, out in front of the belly; held still but for a slow sway, then once a stroke the
    # tip jigged up and let down again ('jig': struck is the rod low, raised the jig's top). The game draws the line from
    # its tip to the water (WalkFigures.DrawLine). Held lower and closer (the fist at 0.3, the left hand on the butt,
    # other 0.05), the left hand sat at the crotch with the arm against the side, hidden by the belly from the front.
    'fish': dict(tool='tool_rod', ticks=420, motion='jig', scale=0.8, grip=0.24, other=0.12,
                 body=dict(NARROW=0.4, STAGGER=0.2),
                 struck=dict(fist=(-0.06, -0.52, 0.38), pitch=70.0, yaw=10.0, roll=0.0, lean=4.0, twist=-4.0, drop=0.02),
                 raised=dict(fist=(-0.06, -0.48, 0.5), pitch=40.0, yaw=10.0, roll=0.0, lean=0.0, twist=-6.0, drop=0.02)),
    # Any other bench or desk (research, crafting by hand): both hands busy on the work in front, no tool.
    'hands': dict(tool=None, ticks=90, motion='tap', taps=3,
                  body=dict(NARROW=0.5),
                  struck=dict(L=(0.12, -0.38, 0.12), R=(-0.12, -0.38, 0.08), lean=10.0, twist=0.0, drop=0.03),
                  raised=dict(L=(0.12, -0.38, 0.08), R=(-0.12, -0.38, 0.12), lean=10.0, twist=0.0, drop=0.03)),
}

def set_gait(g):
    """0 walks, 1 runs, in between blends the two."""
    for k in RUN:
        globals()[k] = WALK[k] + (RUN[k] - WALK[k]) * g

def ground_per_cycle():
    """Body travel per cycle (armature units) at the authored speed."""
    return LEG * STRIDE / STANCE

arm =bpy.data.objects['metarig']
if arm.mode != 'OBJECT':   # a rig saved in Edit Mode neither syncs its bones nor plays its pose
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='OBJECT')
bones = arm.data.bones
rest = {b.name: b.matrix_local.copy() for b in bones}
head = {b.name: b.head_local.copy() for b in bones}
tail = {b.name: b.tail_local.copy() for b in bones}
FWD = Vector((0, -1, 0))

# The body's meshes on the rig: one (Pawn) or split into parts (Body, Head), which the exporters join into one body.
# Without any of these names, the skinned mesh with the most vertices is the body.
BODY_PARTS = ('Pawn', 'Body', 'Head')
# join_body gives each part's own vertices a vertex group of this name (weight 1), so they can still be told apart once
# joined (a garment's "hide_body" and "hide_head" hide the Body and Head parts whole).
PART_GROUP = 'Part_%s'

def skinned_meshes():
    return [o for o in bpy.data.objects
            if o.type == 'MESH' and any(m.type == 'ARMATURE' and m.object == arm for m in o.modifiers)]

def body_parts():
    skinned = skinned_meshes()
    parts = [o for o in skinned if o.name in BODY_PARTS]
    return parts or [max(skinned, key=lambda o: len(o.data.vertices))]

def join_body():
    """Joins the body's parts into one mesh and returns it, with their vertex groups and shape keys merged by name (a
    part without a key of that name keeps its base shape in it). For the batch scripts only: the .blend is never saved."""
    parts = body_parts()
    if bpy.context.object and bpy.context.object.mode != 'OBJECT':   # a part saved in edit mode can't take a group
        bpy.ops.object.mode_set(mode='OBJECT')
    for o in parts:
        group = o.vertex_groups.new(name=PART_GROUP % o.name)
        group.add(range(len(o.data.vertices)), 1.0, 'REPLACE')
    if len(parts) == 1:
        return parts[0]
    for o in parts:
        o.hide_set(False)
        o.hide_viewport = False
    target = max(parts, key=lambda o: len(o.data.vertices))
    names = ', '.join(sorted(o.name for o in parts))   # the others are gone after the join
    with bpy.context.temp_override(active_object=target, selected_editable_objects=parts, selected_objects=parts):
        bpy.ops.object.join()
    print('BODY joined %s into %s' % (names, target.name))
    return target

def T(v): return Matrix.Translation(v)
def rot(axis, deg): return Matrix.Rotation(math.radians(deg), 4, Vector(axis))
def place(pivot_world, R, pivot_rest):
    """Bone transform (rest -> posed, armature space): R about the bone's rest pivot, pivot moved to pivot_world."""
    return T(pivot_world) @ R @ T(-pivot_rest)
def aim(rest_dir, new_dir):
    return rest_dir.normalized().rotation_difference(new_dir.normalized()).to_matrix().to_4x4()
def smooth(t): return (1 - math.cos(math.pi * t)) / 2

L1 = (tail['thigh.L'] - head['thigh.L']).length
L2 = (tail['shin.L'] - head['shin.L']).length
LEG = L1 + L2
toe_off = head['toe.L'] - head['foot.L']   # toe joint relative to ankle at rest (same for both sides, x aside)

def foot_path(p, side):
    """Ankle position and foot pitch (deg, + = heel up) for a leg at cycle phase p."""
    a_rest = head['foot.' + side].copy()
    a_rest.x += (head['thigh.' + side].x - a_rest.x) * NARROW
    a_rest.y -= LEG * STAGGER * (0.5 if side == 'L' else -0.5)   # left foot forward, right back
    if p < STANCE:
        t = p / STANCE
        y = a_rest.y + LEG * STRIDE * (t - 0.5)           # forward is -y: slides from front to back
        ankle = Vector((a_rest.x, y, a_rest.z))
        pitch = 0.0
        if t > 0.75:                                      # heel rises, pivoting on the toe joint
            pitch = HEEL * smooth((t - 0.75) / 0.25)
            toe = ankle + Vector((0, toe_off.y, toe_off.z))
            ankle = toe + (rot((1, 0, 0), pitch).to_3x3() @ -Vector((0, toe_off.y, toe_off.z)))
        return ankle, pitch
    t = (p - STANCE) / (1 - STANCE)
    y = a_rest.y + LEG * STRIDE * (0.5 - smooth(t)) + LEG * KICK * math.sin(math.pi * t) * (1 - t)
    start = HEEL * (1 - smooth(min(1, t / 0.3)))          # toe still pointing down just after toe-off
    end = -10.0 * smooth(max(0, (t - 0.6) / 0.4))         # toes up for the heel strike
    z = a_rest.z + LEG * LIFT * math.sin(math.pi * t ** (math.log(0.5) / math.log(PEAK))) ** 1.2
    return Vector((a_rest.x, y, z)), start + end

def leg(X_hip_parentless, hip_world, p, side, X):
    ankle, pitch = foot_path(p, side)
    d_vec = ankle - hip_world
    d = d_vec.length
    clamp = 0.0
    if d > L1 + L2 - 1e-3:
        clamp = d - (L1 + L2 - 1e-3)
        d = L1 + L2 - 1e-3
        ankle = hip_world + d_vec.normalized() * d
    u = (ankle - hip_world).normalized()
    a = (L1 * L1 - L2 * L2 + d * d) / (2 * d)
    h = math.sqrt(max(0.0, L1 * L1 - a * a))
    perp = (FWD - FWD.dot(u) * u).normalized()        # knee bends forward
    knee = hip_world + u * a + perp * h
    th, sh, ft = 'thigh.' + side, 'shin.' + side, 'foot.' + side
    X[th] = place(hip_world, aim(tail[th] - head[th], knee - hip_world), head[th])
    X[sh] = place(knee, aim(tail[sh] - head[sh], ankle - knee), head[sh])
    X[ft] = place(ankle, rot((1, 0, 0), pitch), head[ft])
    X['toe.' + side] = X[ft]
    X['heel.02.' + side] = X[ft]
    return clamp

def leg_to(side, hip, ankle, pole, X, pitch=0.0, turn=None):
    """Places one leg by 2-bone IK from the hip joint (armature space) to ankle, the knee bent toward pole, the foot
    level but for pitch (deg, + = heel up) and turn (a rotation, or None). Returns how far the ankle was out of reach."""
    d_vec = ankle - hip
    d = d_vec.length
    clamp = max(0.0, d - (L1 + L2 - 1e-3))
    d = min(d, L1 + L2 - 1e-3)
    u = d_vec.normalized()
    ankle = hip + u * d
    a = (L1 * L1 - L2 * L2 + d * d) / (2 * d)
    h = math.sqrt(max(0.0, L1 * L1 - a * a))
    perp = (pole - pole.dot(u) * u).normalized()
    knee = hip + u * a + perp * h
    th, sh, ft = 'thigh.' + side, 'shin.' + side, 'foot.' + side
    X[th] = place(hip, aim(tail[th] - head[th], knee - hip), head[th])
    X[sh] = place(knee, aim(tail[sh] - head[sh], ankle - knee), head[sh])
    X[ft] = place(ankle, (turn if turn is not None else Matrix.Identity(4)) @ rot((1, 0, 0), pitch), head[ft])
    X['toe.' + side] = X[ft]
    X['heel.02.' + side] = X[ft]
    return clamp

# The finger and thumb bones, each after its parent (the fingers move as one; the rig has no bone per finger).
FINGERS = [n % s for s in ('L', 'R') for n in ('hand.%s.001', 'hand.%s.002', 'thumb.%s', 'thumb.%s.001') if n % s in bones]

def close_hand(X, side, sign, amount=1.0):
    """Curls one hand's fingers and thumb toward the palm by amount of FIST (0 straight, 1 a fist round a handle), in
    place: each bone turned from rest about its own head, after the hand as X has it. amount may be four, one per FIST
    bone (the finger's two, then the thumb's two)."""
    amounts = tuple(amount) if isinstance(amount, (tuple, list)) else (amount,) * 4
    hn = 'hand.' + side
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector((sign * HAND_PALM[0], HAND_PALM[1], HAND_PALM[2]))
    n0 = (n0 - n0.dot(d0) * d0).normalized()
    for (name, parent), deg in zip((('hand.%s.001', 'hand.%s'), ('hand.%s.002', 'hand.%s.001'), ('thumb.%s', 'hand.%s'),
                                    ('thumb.%s.001', 'thumb.%s')), [f * a for f, a in zip(FIST, amounts)]):
        name, parent = name % side, parent % side
        if name not in bones:
            continue
        along = (tail[name] - head[name]).normalized()
        if name.startswith('thumb'):
            # The thumb folds across the palm toward the little finger's side, over the fingers.
            axis = d0 * sign
        else:
            axis = along.cross(n0)   # turns the bone toward the palm
        X[name] = X[parent] @ about(head[name], axis, deg)

arm_twist = {}   # by side, a turned hand's twist about the forearm from FOREARM_TWIST_MID (degrees) as arm_to last left
                 # it; 0 for a hand left in line

def arm_to(side, sign, wrist, X, pole=None, turn=None, lift=None):
    """Places one arm by 2-bone IK so its wrist is at wrist (armature space), the elbow out to the side and down (or
    toward pole, in the chest's frame) and then swung out and up by lift (degrees, as DRAFTED's '<side>_elbow'), the
    hand turned in as the walk's is, or by turn (degrees, x y z in the chest's frame from the hand's rest turn, as
    DRAFTED's '<side>_turn'). Returns how far the wrist was out of reach."""
    ua, fa, hn = 'upper_arm.' + side, 'forearm.' + side, 'hand.' + side
    la, lf = (tail[ua] - head[ua]).length, (tail[fa] - head[fa]).length
    shoulder = X['shoulder.' + side] @ head[ua]
    d_vec = wrist - shoulder
    d = d_vec.length
    clamp = max(0.0, d - (la + lf - 1e-3))
    d = min(d, la + lf - 1e-3)
    u = d_vec.normalized()
    wrist = shoulder + u * d
    a = (la * la - lf * lf + d * d) / (2 * d)
    h = math.sqrt(max(0.0, la * la - a * a))
    pole = X[CHEST_BONE].to_3x3() @ (pole if pole is not None else Vector((sign * ELBOW_OUT, 0.25, -1.0)))
    perp = (pole - pole.dot(u) * u).normalized()
    if lift:
        # Turned about the shoulder-to-wrist line whichever way first takes the elbow outward, away from the chest.
        out = X[CHEST_BONE].to_3x3() @ Vector((sign, 0, 0))
        way = 1.0 if u.cross(perp).dot(out) >= 0 else -1.0
        perp = Matrix.Rotation(math.radians(lift) * way, 3, u) @ perp
    elbow = shoulder + u * a + perp * h
    # Turned from the walk's lowered arm, not from the A-pose, so the arm keeps the hanging arm's roll.
    lower = rot((0, 1, 0), ARM_DOWN * sign) @ rot((0, 0, 1), -ARM_FWD * sign)
    R_ua = aim(lower.to_3x3() @ (tail[ua] - head[ua]), elbow - shoulder) @ lower
    X[ua] = place(shoulder, R_ua, head[ua])
    R_in = R_ua @ rot((0, 1, 0), FOREARM_IN * sign)
    R_fa = aim(R_in.to_3x3() @ (tail[fa] - head[fa]), wrist - elbow) @ R_in
    R_hn = R_fa @ rot((0, 1, 0), HAND_IN * sign)
    arm_twist[side] = 0.0
    if turn is not None:
        R_turned = X[CHEST_BONE].to_quaternion().to_matrix().to_4x4() @ Euler([math.radians(a) for a in turn]).to_matrix().to_4x4()
        # The forearm rolls about its own length by a share of the hand's twist from where it would have hung, so the
        # wrist doesn't wring.
        along = (wrist - elbow).normalized()
        q = (R_turned @ R_hn.inverted()).to_quaternion()
        part = Vector((q.x, q.y, q.z)).dot(along)
        twist = Quaternion((q.w, *(along * part)))
        arm_twist[side] = -FOREARM_TWIST_MID * sign
        if twist.magnitude > 1e-6:
            twist.normalize()
            angle = twist.angle if part >= 0 else -twist.angle
            # Taken within half a turn of FOREARM_TWIST_MID, mirrored by side, so the flip lies where no hand is held.
            mid = math.radians(FOREARM_TWIST_MID) * sign
            angle = (angle - mid + math.pi) % (2 * math.pi) - math.pi + mid
            R_fa = Matrix.Rotation(angle * FOREARM_TWIST, 4, along) @ R_fa
            arm_twist[side] = math.degrees(angle - mid)
        R_hn = R_turned
    X[fa] = place(elbow, R_fa, head[fa])
    X[hn] = place(wrist, R_hn, head[hn])
    return clamp

def pose_at(p, sway=0.0, breath=0.0, grips=None):
    """The pose at cycle phase p. sway (-1 .. 1): the weight shifted onto the left (+) or right foot; breath (0 .. 1):
    how far into the in-breath; grips: the wrist places by side ('L', 'R', and turns and elbow lifts, as in DRAFTED), the arms left out swinging as
    the walk's. Left out, they are the walk's."""
    X = {}
    c = math.cos(2 * math.pi * p)          # +1 when the left foot is forward (left heel strike at p = 0)
    s = math.sin(2 * math.pi * p)
    bob = -LEG * (DROP + BOB * math.cos(4 * math.pi * (p - BOB_AT)))
    roll = SWAY_ROLL * sway
    hp = head[HIPS]
    X[HIPS] = place(hp + Vector((LEG * SWAY * sway, 0, bob)), rot((0, 0, 1), -TWIST * c) @ rot((0, 1, 0), -ROLL * s - roll), hp)
    sp = X[HIPS] @ head[SPINE]
    X[SPINE] = place(sp, rot((0, 0, 1), -TWIST * c * 0.3) @ rot((1, 0, 0), LEAN * 0.5), head[SPINE])
    ch = X[SPINE] @ head[CHEST_BONE]
    R_chest = rot((0, 0, 1), CHEST * c) @ rot((0, 1, 0), 0.4 * roll) @ rot((1, 0, 0), LEAN - BREATH * breath)
    X[CHEST_BONE] = place(ch, R_chest, head[CHEST_BONE])
    nk = X[CHEST_BONE] @ head[NECK]
    X[NECK] = place(nk, rot((0, 1, 0), HEAD_TILT * 0.5) @ rot((1, 0, 0), LEAN * 0.3), head[NECK])
    hd = X[NECK] @ head[HEAD]
    X[HEAD] = place(hd, rot((0, 1, 0), HEAD_TILT) @ rot((1, 0, 0), -LEAN * 0.8 + 1.5 * math.cos(4 * math.pi * p)), head[HEAD])
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    clamp = 0.0
    for side, sign, ph in (('L', 1, p), ('R', -1, (p + 0.5) % 1.0)):
        clamp = max(clamp, leg(None, X[HIPS] @ head['thigh.' + side], ph, side, X))
        sh = 'shoulder.' + side
        # raised a little about its own head on the in-breath
        X[sh] = X[CHEST_BONE] @ place(head[sh], rot((0, 1, 0), -BREATH_SHOULDER * breath * sign), head[sh])
        if grips and side in grips:
            clamp = max(clamp, arm_to(side, sign, X[CHEST_BONE] @ (head[HIPS] + Vector(grips[side]) * LEG), X,
                                      turn=grips.get(side + '_turn'), lift=grips.get(side + '_elbow')))
            continue
        ua, fa, hn = 'upper_arm.' + side, 'forearm.' + side, 'hand.' + side
        # arm swings against the leg on its side, a little behind it
        fwd = -math.cos(2 * math.pi * (p - 0.04)) * sign      # left arm back when left foot forward
        lower = rot((0, 1, 0), ARM_DOWN * sign) @ rot((0, 0, 1), -ARM_FWD * sign)
        swing = rot((-1, 0, 0), ARM * fwd)
        R_ua = rot((0, 0, 1), CHEST * c) @ swing @ lower
        X[ua] = place(X[sh] @ head[ua], R_ua, head[ua])
        R_in = R_ua @ rot((0, 1, 0), FOREARM_IN * sign)
        arm_dir = (R_in.to_3x3() @ (tail[fa] - head[fa])).normalized()
        axis = arm_dir.cross(FWD).normalized()
        bend = ELBOW + 14.0 * max(0.0, fwd)
        R_fa = Matrix.Rotation(math.radians(bend), 4, axis) @ R_in
        X[fa] = place(X[ua] @ head[fa], R_fa, head[fa])
        X[hn] = place(X[fa] @ head[hn], R_fa @ rot((0, 1, 0), HAND_IN * sign), head[hn])
    return X, clamp

def basis_of(name, X):
    b = bones[name]
    Xc = X[name]
    Xp = X[b.parent.name] if b.parent else Matrix.Identity(4)
    return rest[name].inverted() @ Xp.inverted() @ Xc @ rest[name]

def apply_pose(X, frame=None):
    """Sets every bone in X on the rig, and keys it when frame is given. A finger or thumb bone X leaves out lies
    straight along its parent, so it never keeps a curl from the pose set before."""
    X = dict(X)
    for name in FINGERS:
        if name not in X and bones[name].parent.name in X:
            X[name] = X[bones[name].parent.name]
    for pb in arm.pose.bones:
        if pb.name not in X:
            continue
        m = basis_of(pb.name, X)
        loc, q, _ = m.decompose()
        pb.location = loc
        if pb.rotation_mode == 'QUATERNION':
            pb.rotation_quaternion = q
            path = 'rotation_quaternion'
        else:
            pb.rotation_euler = q.to_euler(pb.rotation_mode)
            path = 'rotation_euler'
        if frame is not None:
            pb.keyframe_insert(path, frame=frame)
            pb.keyframe_insert('location', frame=frame)

def stand_pose():
    """Standing still: the walk's posture with no stride, sway or swing (feet together, arms down)."""
    global STRIDE, LIFT, DROP, BOB, TWIST, CHEST, ROLL, ARM, HEEL
    saved = (STRIDE, LIFT, DROP, BOB, TWIST, CHEST, ROLL, ARM, HEEL)
    STRIDE, LIFT, DROP, BOB, TWIST, CHEST, ROLL, ARM, HEEL = 0.0, 0.0, 0.01, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0
    X, _ = pose_at(0.29)   # feet flat, arms level with the body (no swing either way)
    STRIDE, LIFT, DROP, BOB, TWIST, CHEST, ROLL, ARM, HEEL = saved
    return X

def stand_at(t, drafted=None):
    """The idle loop at t (0 .. 1 round it), or with drafted (a DRAFTED kind) that kind's drafted loop: the standing
    pose breathing, and the idle shifting its weight from foot to foot. Returns the pose and the worst overreach."""
    g = globals()
    stand = dict(STRIDE=0.0, LIFT=0.0, BOB=0.0, TWIST=0.0, CHEST=0.0, ROLL=0.0, ARM=0.0, HEEL=0.0)
    grips = None
    breaths = 1 if drafted else BREATHS
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * breaths * t)
    if drafted:
        stand.update(DROP=DRAFT_DROP, LEAN=DRAFT_LEAN, NARROW=DRAFT_NARROW)
        grips = drafted_entry(drafted)
        stand.update(grips.get('body', {}))
        sway = 0.0
    else:
        stand['DROP'] = 0.01
        sway = math.tanh(SWAY_HOLD * math.sin(2 * math.pi * t)) / math.tanh(SWAY_HOLD) if SWAY_HOLD > 0 else math.sin(2 * math.pi * t)
    stand['DROP'] += BREATH_BOB * (1 - breath)   # the knees give a little on the out-breath
    saved = {k: g[k] for k in stand}
    g.update(stand)
    try:
        X, clamp = pose_at(0.29, sway=sway, breath=breath, grips=grips)   # 0.29: feet flat, as stand_pose
        if grips is not None and grips.get('shield'):
            # The left arm in the shield guard, as shield_at holds it (the game lays that over it too).
            fist = X[CHEST_BONE] @ (head[HIPS] + Vector(SHIELD_FIST) * LEG)
            clamp = max(clamp, hip_hand_straight(X, fist, lift=SHIELD_ELBOW, palm=SHIELD_PALM))
        return X, clamp
    finally:
        g.update(saved)

def posed_with(params, *args, **kw):
    """pose_at with these module globals changed for the call."""
    g = globals()
    saved = {k: g[k] for k in params}
    g.update(params)
    try:
        return pose_at(*args, **kw)
    finally:
        g.update(saved)

STILL = dict(STRIDE=0.0, LIFT=0.0, BOB=0.0, TWIST=0.0, CHEST=0.0, ROLL=0.0, ARM=0.0, HEEL=0.0)

def moved_all(M, X):
    """Every bone of X moved by M (armature space), keeping the bones that share a matrix sharing it."""
    return {k: M @ v for k, v in X.items()}

def sit_at(t):
    """The seated loop at t (0 .. 1 round it): the standing pose lowered onto bent knees, hands on the thighs, breathing.
    Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * SIT_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.01, LEAN=SIT_LEAN), 0.29, breath=breath)
    th, sh = math.radians(SIT_THIGH), math.radians(SIT_SHIN)
    down = Vector((0, 0, -1))
    # Lowered so the shins reach the floor where the standing feet were.
    hip = X[HIPS] @ head['thigh.L']
    floor = (X['foot.L'] @ head['foot.L']).z
    X = moved_all(T(Vector((0, 0, floor + L2 * math.cos(sh) + L1 * math.sin(th) - hip.z))), X)
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        hip = X[HIPS] @ head['thigh.' + side]
        along = (FWD * math.cos(th) + down * math.sin(th) + Vector((sign * SIT_KNEES, 0, 0))).normalized()
        knee = hip + along * L1
        ankle = knee + (down * math.cos(sh) + FWD * math.sin(sh)) * L2
        clamp = max(clamp, leg_to(side, hip, ankle, along, X))
        hand = hip + (Vector((-sign * SIT_HANDS[0], 0, 0)) + along * SIT_HANDS[1] + Vector((0, 0, SIT_HANDS[2]))) * L1
        clamp = max(clamp, arm_to(side, sign, hand, X))
    return X, clamp

def lie_at(t):
    """The lying loop at t (0 .. 1 round it), upright: legs straight, arms at the sides, toes pointed, breathing."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * LIE_BREATHS * t)
    X, clamp = posed_with(dict(STILL, DROP=0.005, LEAN=0.0, NARROW=LIE_NARROW, ARM_DOWN=ARM_DOWN + LIE_ARMS, ELBOW=6.0),
                          0.29, breath=breath)
    for side in ('L', 'R'):
        ft = 'foot.' + side
        X[ft] = place(X[ft] @ head[ft], X[ft].to_3x3().to_4x4() @ rot((1, 0, 0), LIE_POINT), head[ft])
        X['toe.' + side] = X[ft]
        X['heel.02.' + side] = X[ft]
    return X, clamp

def ragdoll_pose():
    """The ragdoll's neutral pose (see RAGDOLL_CURL), standing. Returns the pose and the worst overreach (none)."""
    X, _ = posed_with(dict(STILL, DROP=0.0, LEAN=0.0, NARROW=LIE_NARROW, ELBOW=0.0), 0.29)
    turn_branch(X, CHEST_BONE, about(X[CHEST_BONE] @ head[CHEST_BONE], (1, 0, 0), RAGDOLL_CURL))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), RAGDOLL_NOD))
    for side, sign in (('L', 1), ('R', -1)):
        sh, ua, fa, hn = 'shoulder.' + side, 'upper_arm.' + side, 'forearm.' + side, 'hand.' + side
        # Rolled forward about the upright (the shoulder bone points out to its side), then the arm swung out about the
        # way the figure faces and forward about its side-to-side line, then the elbow bent forward.
        turn_branch(X, sh, about(X[sh] @ head[sh], (0, 0, 1), -RAGDOLL_ROLL * sign))
        joint = X[ua] @ head[ua]
        turn_branch(X, ua, about(joint, (1, 0, 0), -RAGDOLL_REACH) @ about(joint, (0, 1, 0), -RAGDOLL_ARMS * sign))
        along = (X[ua].to_3x3() @ (tail[ua] - head[ua])).normalized()
        turn_branch(X, fa, about(X[fa] @ head[fa], along.cross(FWD).normalized(), RAGDOLL_ELBOW))
        th, sn, ft = 'thigh.' + side, 'shin.' + side, 'foot.' + side
        turn_branch(X, th, about(X[th] @ head[th], (1, 0, 0), -RAGDOLL_HIP))
        turn_branch(X, sn, about(X[sn] @ head[sn], (1, 0, 0), RAGDOLL_KNEE))
        turn_branch(X, ft, about(X[ft] @ head[ft], (1, 0, 0), RAGDOLL_POINT))
        close_hand(X, side, sign, RAGDOLL_HAND)
    return X, 0.0

def caged_pose():
    """A corpse slumped standing in a gibbet cage (see CAGED_DROP): one frame. Returns the pose and the worst overreach."""
    X, clamp = posed_with(dict(STILL, DROP=CAGED_DROP, LEAN=0.0, NARROW=CAGED_NARROW, ARM_DOWN=ARM_DOWN + CAGED_ARMS,
                               ELBOW=CAGED_ELBOW), 0.29)
    turn_branch(X, CHEST_BONE, about(X[CHEST_BONE] @ head[CHEST_BONE], (1, 0, 0), CAGED_CURL))
    neck = X[NECK] @ head[NECK]
    turn_branch(X, NECK, about(neck, (0, 1, 0), -CAGED_LOLL) @ about(neck, (1, 0, 0), CAGED_NOD))
    for side, sign in (('L', 1), ('R', -1)):
        sh = 'shoulder.' + side
        turn_branch(X, sh, about(X[sh] @ head[sh], (0, 0, 1), -CAGED_ROLL * sign))
        close_hand(X, side, sign, CAGED_HAND)
    return X, clamp

def crawl_at(p):
    """The crawl at cycle phase p, upright: the mocap clip if there is one (see CRAWL_TIP), else the belly crawl. Returns
    the pose and the worst overreach."""
    if CRAWL_CLIP is None:
        return belly_crawl_at(p)
    return mocap_at(CRAWL_CLIP, p, Quaternion((1, 0, 0), math.radians(-CRAWL_TIP))), 0.0

def mocap_at(clip, p, up):
    """A clip read in from a glTF (import_crawl.py, import_pluck.py) at phase p (0 .. 1 round it), turned by up (a
    quaternion) as a whole: each bone it has turned from its rest as the clip has it, the rest with their parents, and the
    hips moved by the clip's offsets (leg lengths). A clip with a 'trunk' line (import_pluck.py) has the trunk's bones
    aimed along it instead, each at the same share of the way from the hips to the neck, keeping the clip's twist. A
    bone in its 'rest_ways' (import_pluck.py) is first turned from its own rest way to the glTF bone's, which points
    well away from it at rest (the feet, hands and neck), so it points as the clip's does rather than off by that."""
    n = clip['frames']
    i = int(p * n) % n
    j, u = (i + 1) % n, p * n - math.floor(p * n)
    turns = clip['turns']
    hips = clip['hips']
    line = None
    if 'trunk' in clip:
        line = [Vector(a).lerp(Vector(b), u) for a, b in zip(clip['trunk'][i], clip['trunk'][j])]
        shares = clip['trunk_at']
        span = sum((head[b] - head[a]).length for a, b in zip(TRUNK, TRUNK[1:]))
        def along(f):
            # The clip's trunk line at share f of the way up it, in trunk lengths from the hips joint.
            k = min(max(sum(1 for s in shares[1:-1] if s <= f), 0), len(shares) - 2)
            w = (f - shares[k]) / (shares[k + 1] - shares[k])
            return line[k].lerp(line[k + 1], w)
        reach = [0.0]
        for a, b in zip(TRUNK, TRUNK[1:]):
            reach.append(reach[-1] + (head[b] - head[a]).length / span)
    X = {}
    def pose(b):
        source = b.name if b.name in turns else None
        if source is not None:
            q = Quaternion(turns[source][i]).slerp(Quaternion(turns[source][j]), u)
            if b.name in clip.get('rest_ways', {}):
                rest_way = clip['rest_ways'][b.name]
                q = q @ (head[rest_way['to']] - head[b.name]).rotation_difference(Vector(rest_way['way']))
            if line is not None and b.name in TRUNK[:-1]:
                k = TRUNK.index(b.name)
                rest_way = head[TRUNK[k + 1]] - head[b.name]
                way = along(reach[k + 1]) - along(reach[k])
                q = (q @ rest_way).rotation_difference(way) @ q
            R = (up @ q).to_matrix().to_4x4()
        else:
            R = X[b.parent.name].to_3x3().normalized().to_4x4() if b.parent else up.to_matrix().to_4x4()
        if b.parent is None:
            off = Vector(hips[i]).lerp(Vector(hips[j]), u) * LEG
            at = head[b.name] + up @ off
        else:
            at = X[b.parent.name] @ head[b.name]
        X[b.name] = place(at, R, head[b.name])
        for c in b.children:
            pose(c)
    for b in bones:
        if b.parent is None:
            pose(b)
    return X

kneel_floors = {}   # the lift that sets each kneeling clip on the floor, by its source frames, once worked out

def kneel_at(clip, t):
    """A kneeling clip (import_pluck.py) at t (0 .. 1 round the loop), set on the floor: its lowest toe joint put at
    the toes' height at rest, as the glTF rig's proportions differ from the metarig's, so its hips' height doesn't kneel
    this one on the floor by itself."""
    key = (clip['source'], clip['action'], clip['first'], clip['last'])
    if key not in kneel_floors:
        n = clip['frames']
        low = min(min((Y['toe.' + s] @ head['toe.' + s]).z for s in ('L', 'R'))
                  for Y in (mocap_at(clip, f / n, Quaternion()) for f in range(n)))
        kneel_floors[key] = min(head['toe.L'].z, head['toe.R'].z) - low
    return moved_all(T(Vector((0, 0, kneel_floors[key]))), mocap_at(clip, t, Quaternion()))

def tend_at(t):
    """Kneeling by a patient at t (0 .. 1 round the loop; see TEND_CLIP). Returns the pose and the worst overreach."""
    return kneel_at(TEND_CLIP, t), 0.0

def meditate_at(t):
    """Sat cross-legged on the floor meditating at t (0 .. 1 round the loop; see MEDITATE_CLIP). Returns the pose and the worst
    overreach."""
    return moved_all(T(Vector((0, 0, MEDITATE_LIFT * LEG))), kneel_at(MEDITATE_CLIP, t)), 0.0

def pray_sit_at(t):
    """Praying sat on a seat at t (0 .. 1 round the loop, PRAY_SECONDS long; see PRAY_HANDS): the sit with the palms
    pressed together in front of the chest and the head bowed, to be laid over the sit from the waist up (EAT_UPPER).
    Returns the pose and the worst overreach."""
    X, clamp = sit_at(t)
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * SIT_BREATHS * t)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        across = (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized()
        turn_branch(X, bone, about(pivot, across, PRAY_LEAN * 0.5))
    bow = PRAY_BOW + PRAY_BOB * (breath - 0.5)
    for bone in (NECK, HEAD):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), bow * 0.5))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    side = X[CHEST_BONE].to_3x3() @ Vector((1, 0, 0))
    side = Vector((side.x, side.y, 0.0)).normalized()
    level = Matrix((side, Vector((0, 0, 1)).cross(side), Vector((0, 0, 1)))).transposed()
    middle = (X['shoulder.L'] @ head['upper_arm.L'] + X['shoulder.R'] @ head['upper_arm.R']) * 0.5
    palms = middle + level @ Vector(PRAY_HANDS) * LEG
    for s, sign in (('R', -1), ('L', 1)):
        m = lambda v: Vector((sign * v[0], v[1], v[2]))
        facing = (level @ m(PRAY_PALM)).normalized()
        fingers = level @ Vector(PRAY_FINGERS)
        fingers = (fingers - fingers.dot(facing) * facing).normalized()
        clamp = max(clamp, hand_on(s, sign, palms, fingers, facing, X, pole=m(PRAY_ELBOW)))
        close_hand(X, s, sign, PRAY_CURL)
    return X, clamp

def toys_at(t):
    """Knelt by a baby on the floor, waving a hand over it, at t (0 .. 1 round the loop; see TOYS_WRIST). Returns the pose
    and the worst overreach."""
    X = kneel_at(TEND_CLIP, t)
    w = 2 * math.pi * TOYS_WAVES * t
    shoulder = X['shoulder.R'] @ head['upper_arm.R']
    across = math.sin(w)
    wrist = shoulder + Vector((-(TOYS_WRIST[0] + TOYS_SWAY * across), -TOYS_WRIST[1],
                               -TOYS_WRIST[2] + TOYS_BOB * (across * across - 0.5))) * LEG
    # The hand pointed forward and down, its palm to the baby, rolled about its length with the sway (leading it by a
    # quarter, as a hand waved), as pluck_at turns it.
    hn = 'hand.R'
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector((-HAND_PALM[0], HAND_PALM[1], HAND_PALM[2]))
    n0 = (n0 - n0.dot(d0) * d0).normalized()
    d1 = Vector((0.0, -math.cos(math.radians(TOYS_POINT)), -math.sin(math.radians(TOYS_POINT))))
    n1 = Matrix.Rotation(math.radians(TOYS_ROLL) * math.cos(w), 3, d1) @ Vector((0.0, 0.0, -1.0))
    n1 = (n1 - n1.dot(d1) * d1).normalized()
    R0 = Matrix((d0, n0, d0.cross(n0))).transposed()
    R1 = Matrix((d1, n1, d1.cross(n1))).transposed()
    want = (X[CHEST_BONE].to_quaternion().inverted() @ (R1 @ R0.transposed()).to_quaternion()).to_euler()
    clamp = arm_to('R', -1, wrist, X, pole=Vector((-0.8, 0.3, -0.5)), turn=[math.degrees(a) for a in want])
    wiggle = 0.5 + 0.5 * math.sin(2 * math.pi * TOYS_WIGGLES * t)
    close_hand(X, 'R', -1, TOYS_CURL[0] + (TOYS_CURL[1] - TOYS_CURL[0]) * wiggle)
    return X, clamp

def vomit_at(t):
    """Bent over vomiting at t (0 .. 1 round the loop; see VOMIT_CLIP). Returns the pose and the worst overreach."""
    # The clip from the hips up (their tip forward is much of the bend), but not its legs, which were made for the glTF
    # rig's and don't fit the chibi's: the feet are planted where the standing pose has them, flat and straight ahead,
    # and the knees found by IK, bent forward. The figure is let down as far as the legs need to reach.
    X = mocap_at(VOMIT_CLIP, t, Quaternion())
    S = stand_pose()
    ankles = {side: S['foot.' + side] @ head['foot.' + side] for side in ('L', 'R')}
    reach = L1 + L2 - VOMIT_KNEE * LEG
    drop = 0.0
    for side, ankle in ankles.items():
        hip = X[HIPS] @ head['thigh.' + side]
        flat = Vector((hip.x - ankle.x, hip.y - ankle.y, 0.0)).length
        if flat < reach:
            drop = max(drop, (hip.z - ankle.z) - math.sqrt(reach * reach - flat * flat))
    X = moved_all(T(Vector((0.0, 0.0, -drop))), X)
    clamp = 0.0
    for side, ankle in ankles.items():
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, FWD, X))
    s = (t - VOMIT_AT) % 1.0
    if s < VOMIT_RISE:
        w = smooth(s / VOMIT_RISE)
    elif s < VOMIT_RISE + VOMIT_FALL:
        w = 1.0 - smooth((s - VOMIT_RISE) / VOMIT_FALL)
    else:
        w = 0.0
    # Each turned about its own joint, the upper chest first so the neck's joint has moved with it.
    for b, deg in zip(('spine.003', NECK), VOMIT_HEAVE):
        joint = X[b] @ head[b]
        turn_branch(X, b, about(joint, (1, 0, 0), deg * w))
    return X, clamp

def getup_at(t):
    """Getting up at t (0 lying on the back .. 1 standing; see GETUP_CLIP). Returns the pose and the worst overreach."""
    n = GETUP_CLIP['frames']
    X = mocap_at(GETUP_CLIP, min(max(t, 0.0), 1.0) * (n - 1) / n, Quaternion())
    low = min((X[b] @ head[b]).z for b in X)
    # The hips held over where they stand at the end all the way through: the clip's own path swings them well over
    # the ground as it rises, and the game already moves them from where the pawn lay (WalkFigures.lieAlong).
    end = head[HIPS] + Vector(GETUP_CLIP['hips'][-1]) * LEG
    at = X[HIPS] @ head[HIPS]
    return moved_all(T(Vector((end.x - at.x, end.y - at.y, min(head['toe.L'].z, head['toe.R'].z) - low))), X), 0.0

def lying_up(clip, frame=None):
    """The turn that stands a lying clip up as lie_at stands: its head (the way from its hips to its neck, over the whole
    clip or at one frame) turned to point back (+y, where the game's lay-down puts the head), then tipped up about x."""
    lines = clip['trunk'] if frame is None else [clip['trunk'][frame]]
    way = sum((Vector(line[-1]) - Vector(line[0]) for line in lines), Vector())
    yaw = Quaternion((0, 0, 1), math.atan2(way.x, way.y))
    return Quaternion((1, 0, 0), math.radians(90)) @ yaw

def lying_at(clip, t):
    """A lying loop (SLEEP_CLIP and the rest) at t (0 .. 1 round it), made upright (lying_up), its hips at their rest
    place on average, as lie_at's are."""
    up = lying_up(clip)
    n = len(clip['hips'])
    mean = sum((Vector(h) for h in clip['hips']), Vector()) / n
    return moved_all(T(-(up @ mean) * LEG), mocap_at(clip, t, up))

def held_clip(clip, frame):
    """The clip held on one frame all round its loop: every frame a copy of that one, so it plays as a still pose."""
    n = clip['frames']
    f = min(max(frame, 0), n - 1)
    held = dict(clip)
    held['turns'] = {b: [qs[f]] * n for b, qs in clip['turns'].items()}
    held['hips'] = [clip['hips'][f]] * n
    if 'trunk' in clip:
        held['trunk'] = [clip['trunk'][f]] * n
    return held

def sleep_frames():
    """The sleep loop's frames: the clip's own, or held still, enough for its breathing."""
    return loop_frames(SLEEP_HOLD_SECONDS) if SLEEP_HOLD else SLEEP_CLIP['frames']

def sleep_seconds():
    """The sleep loop's length in game seconds: the clip's own, or held still, SLEEP_HOLD_SECONDS."""
    return SLEEP_HOLD_SECONDS if SLEEP_HOLD else SLEEP_CLIP['frames'] / SLEEP_CLIP['fps']

def sleep_at(t):
    """Asleep at t (0 .. 1 round the loop; see SLEEP_CLIP), or with SLEEP_HOLD held still on SLEEP_HOLD_FRAME and
    breathing. Returns the pose and the worst overreach."""
    if not SLEEP_HOLD:
        return lying_at(SLEEP_CLIP, t), 0.0
    X = lying_at(held_clip(SLEEP_CLIP, SLEEP_HOLD_FRAME), t)
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * SLEEP_BREATHS * t)
    chest = X['spine.003']
    side = (chest.to_3x3() @ Vector((1, 0, 0))).normalized()
    turn_branch(X, 'spine.003', about(chest @ head['spine.003'], side, -SLEEP_BREATH * breath))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], side, SLEEP_BREATH_NECK * breath))
    return X, 0.0

def mirror_name(b):
    """The bone's counterpart on the other side (.L for .R and back), or itself."""
    return b[:-2] + ('.R' if b.endswith('.L') else '.L') if b.endswith(('.L', '.R')) else b

def mirror_pose(X):
    """The pose mirrored left for right across the figure's middle (x = 0): each bone takes its counterpart's
    matrix seen in a mirror, as the rig is symmetric at rest."""
    S = Matrix.Scale(-1.0, 4, Vector((1, 0, 0)))
    return {b: S @ X[mirror_name(b)] @ S for b in X if mirror_name(b) in X}

def lovin_at(t, side):
    """At lovin on the side facing the partner, on the figure's side ('L' its left, +x; 'R' its right) at t (0 .. 1
    round the loop; see LOVIN_SECONDS). Returns the pose and the worst overreach."""
    with varied(dict(SLEEP_CLIP=DOWNED_SIDE_CLIP, SLEEP_HOLD=True, SLEEP_HOLD_SECONDS=LOVIN_SECONDS, SLEEP_BREATHS=LOVIN_BREATHS)):
        X, _ = sleep_at(t)
    face = X[HEAD].to_3x3() @ FWD
    if (face.x > 0.0) != (side == 'L'):
        X = mirror_pose(X)
    return X, 0.0

def downed_at(t):
    """Downed in bed at t (0 .. 1 round the loop; see DOWNED_CLIP). Returns the pose and the worst overreach."""
    return lying_at(DOWNED_CLIP, t), 0.0

def downed_side_at(t):
    """Downed on the ground at t (0 .. 1 round the loop; see DOWNED_SIDE_CLIP). Returns the pose and the worst
    overreach."""
    return lying_at(DOWNED_SIDE_CLIP, t), 0.0

def baby_ease(move, t):
    """How far into a BABY_MOVES move the baby is at t (0 .. 1 round the loop): 0 .. 1, and how far through it (0 .. 1)."""
    s = (t - move['at']) % 1.0
    span = (move['to'] - move['at']) % 1.0
    if s > span:
        return 0.0, 0.0
    return smooth(min(s / move['rise'], (span - s) / move['fall'], 1.0)), s / span

baby_flail_waves = {}   # each side and channel's sines (times round, weight, phase), once picked

def baby_flail(side, t):
    """The flail's raise, swing out, elbow bend and finger curl for one arm at t (0 .. 1 round the loop)."""
    if not baby_flail_waves:
        import random
        rng = random.Random(7)
        for sd in ('L', 'R'):
            for ch in BABY_FLAIL:
                ws = [(k, rng.uniform(0.3, 1.0) / k ** 0.5, rng.uniform(0, 2 * math.pi)) for k in BABY_FLAIL_TIMES]
                total = sum(w for _, w, _ in ws)
                baby_flail_waves[sd, ch] = [(k, w / total, ph) for k, w, ph in ws]
    return {ch: mid + far * sum(w * math.sin(2 * math.pi * k * t + ph) for k, w, ph in baby_flail_waves[side, ch])
            for ch, (mid, far) in BABY_FLAIL.items()}

def baby_arm(X, side, sign, a):
    """Poses one arm from the lie's hanging arm by a's raise, swing out and elbow bend (degrees): the elbow bent forward
    while the arm hangs, then the arm raised forward and swung out about the shoulder, in the chest's frame."""
    ua, fa = 'upper_arm.' + side, 'forearm.' + side
    C = X[CHEST_BONE].to_3x3()
    along = (X[ua].to_3x3() @ (tail[ua] - head[ua])).normalized()
    turn_branch(X, fa, about(X[fa] @ head[fa], along.cross(C @ FWD).normalized(), a['bend']))
    joint = X[ua] @ head[ua]
    turn_branch(X, ua, about(joint, C @ Vector((1, 0, 0)), -a['reach'])
                @ about(joint, C @ Vector((0, 1, 0)), -a['spread'] * sign))

def baby_at(t):
    """A baby awake on its back at t (0 .. 1 round the loop; see BABY_FLAIL and BABY_MOVES), upright: the game lays it
    down as it lays the lie. Returns the pose and the worst overreach."""
    X, clamp = lie_at(t)
    for side, sign in (('L', 1), ('R', -1)):
        a = baby_flail(side, t)
        to_mouth = 0.0
        for move in BABY_MOVES:
            if move.get('side', side) != side:
                continue
            w, through = baby_ease(move, t)
            if w <= 0:
                continue
            kind = move['move']
            if kind == 'reach':
                goal = dict(BABY_REACH, fist=0.5 - 0.5 * math.cos(2 * math.pi * BABY_GRASPS * through))
            elif kind == 'startle':
                goal = BABY_STARTLE
            elif kind == 'bat':
                swing = math.sin(2 * math.pi * (BABY_BATS * through + (0.0 if side == 'L' else 0.5)))
                goal = dict(reach=BABY_BAT_ARMS['reach'] + BABY_BAT * swing,
                            spread=BABY_BAT_ARMS['spread'] + BABY_SPREAD * swing,
                            bend=BABY_BAT_ARMS['bend'] + BABY_BAT_BEND * swing)
            else:   # mouth: a fist, the arm put there by IK below
                goal, to_mouth = dict(fist=1.0), w
            a = {ch: v + (goal[ch] - v) * w if ch in goal else v for ch, v in a.items()}
        a['reach'] += BABY_ARMS_TIP
        ua = 'upper_arm.' + side
        arm = [bones[ua], bones['forearm.' + side], bones['hand.' + side]]   # the fingers are close_hand's
        before = {b.name: X[b.name] for b in arm}
        baby_arm(X, side, sign, a)
        if to_mouth > 0:
            # Both ways of posing the arm, each bone's turn slerped between them and placed down the arm.
            free = {b.name: X[b.name] for b in arm}
            X.update(before)
            H = X[HEAD]
            arm_to(side, sign, H @ (head[HEAD] + Vector(BABY_MOUTH) * LEG), X)
            for b in arm:
                R = free[b.name].to_quaternion().slerp(X[b.name].to_quaternion(), to_mouth).to_matrix().to_4x4()
                pivot = (X[b.parent.name] if b.name != ua else X['shoulder.' + side]) @ head[b.name]
                X[b.name] = place(pivot, R, head[b.name])
        close_hand(X, side, sign, max(0.0, min(1.0, a['fist'])))
    # The legs: each bone's turn on the hips from the crawl, placed down the leg from the hips.
    legs = [b for side in ('L', 'R') for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive)]
    C = mocap_at(CRAWL_CLIP, (t * BABY_KICKS) % 1.0, Quaternion())
    H = X[HIPS].to_quaternion() @ C[HIPS].to_quaternion().inverted()
    for b in legs:
        if b.name in C:
            R = (H @ C[b.name].to_quaternion()).to_matrix().to_4x4()
            X[b.name] = place(X[b.parent.name] @ head[b.name], R, head[b.name])
    return X, clamp

fall_places = {}   # the move that puts each fall's last frame where the loop it falls into starts, once worked out

def fall_pose(clip, t):
    """A fall (KNOCKDOWN_CLIP, DIE_CLIP) at t (0 standing .. 1 down), turned so its last frame's head points back, set
    on the floor by its lowest joint and tipped up as the lying loops are (about the rest hips), not yet moved."""
    n = clip['frames']
    up = lying_up(clip, n - 1)
    yaw = Quaternion((1, 0, 0), math.radians(-90)) @ up   # the turn about z alone
    X = mocap_at(clip, min(max(t, 0.0), 1.0) * (n - 1) / n, yaw)
    low = min((X[b] @ head[b]).z for b in X)
    X = moved_all(T(Vector((0, 0, min(head['toe.L'].z, head['toe.R'].z) - low))), X)
    pivot = head[HIPS]
    return moved_all(T(pivot) @ Quaternion((1, 0, 0), math.radians(90)).to_matrix().to_4x4() @ T(-pivot), X)

def dead_at(t):
    """A corpse: the death fall's last frame, made upright with its hips at their rest place. Returns the pose and the
    worst overreach."""
    X = fall_pose(DIE_CLIP, 1.0)
    return moved_all(T(head[HIPS] - X[HIPS] @ head[HIPS]), X), 0.0

def fall_clip(name):
    """The mocap clip of the fall name ('knockdown', 'die'), or None without it; a variant's, while one is being posed."""
    return KNOCKDOWN_CLIP if name == 'knockdown' else DIE_CLIP

def fall_at(t, name):
    """The fall name ('knockdown', 'die') at t (0 standing .. 1 down), made upright, its last frame's hips where the
    loop it falls into (FALL_INTO) has them at its first. Returns the pose and the worst overreach."""
    clip = fall_clip(name)
    into = downed_side_at if FALL_INTO[name] == 'downed_side' else dead_at
    place = (name, clip['source'], clip['action'], clip['first'], clip['last'])   # a variant's clip ends somewhere else
    if place not in fall_places:
        end = fall_pose(clip, 1.0)
        start, _ = into(0.0)
        fall_places[place] = T(start[HIPS] @ head[HIPS] - end[HIPS] @ head[HIPS])
    return moved_all(fall_places[place], fall_pose(clip, t)), 0.0

def rub_at(t):
    """Rubbing the hands at t (0 .. 1 round the loop; see RUB_CLIP): the clip from the waist up on the standing pose's
    hips and legs, each upper bone kept where it is from the clip's hips. Returns the pose and the worst overreach."""
    return upper_from(stand_pose(), mocap_at(RUB_CLIP, t, Quaternion())), 0.0

talk_nods = {}   # the clip's mean head pitch (degrees), by its source frames, once worked out

def talk_at(t):
    """Talking at t (0 .. 1 round the loop; see TALK_CLIP): the clip from the waist up on the standing pose's hips and
    legs, as rub_at is. The clip hunches the chest forward but holds the neck upright and bows the head, so the neck
    stood behind the chest and the face looked at the floor: the neck's two bones are bent on the chest as the standing
    pose bends them, and the head is put level as the standing pose holds it, with TALK_NOD of the clip's own nodding
    about its mean kept. Returns the pose and the worst overreach."""
    B = stand_pose()
    key = (TALK_CLIP['source'], TALK_CLIP['action'], TALK_CLIP['first'], TALK_CLIP['last'])
    if key not in talk_nods:
        n = TALK_CLIP['frames']
        talk_nods[key] = sum(pitch_of(upper_from(dict(B), mocap_at(TALK_CLIP, f / n, Quaternion())), HEAD)
                             for f in range(n)) / n
    X = upper_from(dict(B), mocap_at(TALK_CLIP, t, Quaternion()))
    nod = pitch_of(X, HEAD) - talk_nods[key]
    for b in TALK_NECK:
        parent = bones[b].parent.name
        want = pitch_of(X, parent) + pitch_of(B, b) - pitch_of(B, parent)
        turn_branch(X, b, about(X[b] @ head[b], (1, 0, 0), want - pitch_of(X, b)))
    want = pitch_of(B, HEAD) + nod * TALK_NOD
    turn_branch(X, HEAD, about(X[HEAD] @ head[HEAD], (1, 0, 0), want - pitch_of(X, HEAD)))
    return X, 0.0

def reload_at(t):
    """Reloading at t (0 .. 1 through it; see RELOAD_CLIP): the clip from the waist up on the standing pose's hips and
    legs, as talk_at is. Returns the pose and the worst overreach."""
    return upper_from(stand_pose(), mocap_at(RELOAD_CLIP, t, Quaternion())), 0.0

def upper_from(X, Y, turned=True, joint=HIPS):
    """X with every bone above the hips taken from Y (a mocap pose), moved so Y's hips lie on X's: the clip from the
    waist up on X's hips and legs. turned: turned with X's hips as well; else only moved, so the trunk keeps the clip's
    own lean whatever way X's hips are tipped, and moved so Y's joint lies on X's (SPINE: the waist, so a clip whose
    hips are tipped far from X's doesn't start its trunk off the end of X's hips)."""
    legs = {HIPS}
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    M = X[HIPS] @ Y[HIPS].inverted() if turned else T(X[HIPS] @ head[joint] - Y[joint] @ head[joint])
    X.update((k, M @ v) for k, v in Y.items() if k not in legs)
    return X

def pitch_of(X, b):
    """How far bone b leans forward in X (degrees from straight up, toward -y)."""
    d = X[b].to_3x3() @ (tail[b] - head[b])
    return math.degrees(math.atan2(-d.y, d.z))

eat_rises = {}   # by clip: where its eating wrist starts, and how low and high it goes

def eat_rise(clip, base, side):
    """Where the eating clip's wrist on side is at its start, and the lowest and highest it goes (armature space, laid
    on base's hips)."""
    key = (clip['source'], clip['action'], clip['first'], clip['last'])
    if key not in eat_rises:
        n = clip['frames']
        at = [upper_from(dict(base), mocap_at(clip, f / n, Quaternion()), False)['hand.' + side] @ head['hand.' + side]
              for f in range(n)]
        eat_rises[key] = (at[0], min(w.z for w in at), max(w.z for w in at))
    return eat_rises[key]

def eat_at(clip, t, base, side, sign, rest, level):
    """An eating clip at t (0 .. 1 round the loop) from the waist up on base's hips and legs, its nod taken out by level
    (a share), and the wrist on side brought from rest (armature space, or None: where the clip starts it) toward
    EAT_MOUTH as far as the clip's own wrist has risen, its turn kept and the elbow hanging (unless EAT_REACH is off). Returns the pose and the
    worst overreach."""
    X = upper_from(dict(base), mocap_at(clip, t, Quaternion()), False)
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), (pitch_of(base, HEAD) - pitch_of(X, HEAD)) * level))
    if not EAT_REACH:
        return X, 0.0
    hn = 'hand.' + side
    start, low, high = eat_rise(clip, base, side)
    rise = min(max(((X[hn] @ head[hn]).z - low) / (high - low), 0.0), 1.0)
    mouth = Vector(EAT_MOUTH)
    mouth.x *= sign
    wrist = (rest if rest is not None else start).lerp(X[HEAD] @ (head[HEAD] + mouth * LEG), smooth(rise))
    fingers = {b.name: X[hn].inverted() @ X[b.name] for b in bones[hn].children_recursive if b.name in X}
    turn = (X[CHEST_BONE].to_quaternion().to_matrix().inverted() @ X[hn].to_3x3().normalized()).to_euler('XYZ')
    clamp = arm_to(side, sign, wrist, X, turn=[math.degrees(a) for a in turn])
    X.update((name, X[hn] @ rel) for name, rel in fingers.items())
    return X, clamp

def eat_sit_at(t):
    """Eating seated at a table at t (0 .. 1 round the loop; see EAT_SIT_CLIP): the right hand from the meal on the table
    (EAT_TABLE) to the mouth. Returns the pose and the worst overreach."""
    S, clamp = sit_at(t)
    table = S[CHEST_BONE] @ (head[HIPS] + Vector(EAT_TABLE) * LEG)
    X, reach = eat_at(EAT_SIT_CLIP, t, S, 'R', -1, table, EAT_LEVEL[0])
    return X, max(clamp, reach)

def research_at(t):
    """Researching seated at t (0 .. 1 round the loop; see RESEARCH_CLIP): the clip from the waist up on the sit's hips
    and legs, keeping its own lean. Returns the pose and the worst overreach."""
    return seated_upper(RESEARCH_CLIP, t)

def seated_upper(clip, t):
    """A seated clip at t from the waist up on the sit's hips and legs, joined at the waist and bent evenly there
    (SEAT_WAIST, bend_waist), keeping its own lean. Returns the pose and the worst overreach."""
    S, clamp = sit_at(t)
    X = upper_from(S, mocap_at(clip, t, Quaternion()), False, SPINE)
    return X, max(clamp, bend_waist(X, SEAT_WAIST))

def research_stand_at(t):
    """Researching standing at t (0 .. 1 round the loop; see RESEARCH_CLIP): the clip from the chest up on the standing
    pose's hips, lower spine and legs, moved so its chest joint lies on the standing pose's, and its chest stood up
    RESEARCH_STAND_RAISE of the way to the standing pose's lean, the head turned back down by as much so it still looks
    at what the hands hold. The clip is sat with its pelvis tipped well back, so from the waist up its lower spine leant
    back off the standing hips and the chest folded forward over a pushed-out belly. Returns the pose and the worst
    overreach."""
    X = stand_pose()
    Y = mocap_at(RESEARCH_CLIP, t, Quaternion())
    keep = {b.name for b in [bones[CHEST_BONE]] + list(bones[CHEST_BONE].children_recursive)}
    M = T(X[SPINE] @ head[CHEST_BONE] - Y[CHEST_BONE] @ head[CHEST_BONE])
    X.update((k, M @ v) for k, v in Y.items() if k in keep)
    deg = (pitch_of(X, CHEST_BONE) - pitch_of(stand_pose(), CHEST_BONE)) * RESEARCH_STAND_RAISE
    turn_branch(X, CHEST_BONE, about(X[SPINE] @ head[CHEST_BONE], (1, 0, 0), -deg))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), deg))
    return X, 0.0

def research_floor_at(t):
    """Researching sat cross-legged on the floor at t (0 .. 1 round the loop; see RESEARCH_CLIP): the clip from the waist
    up on the meditation's hips and legs (meditate_at), joined at the waist and keeping its own lean. The meditation's
    pelvis is tipped back about as far as the clip's sat-back one, so the trunk sits on it as it does in the clip. Returns
    the pose and the worst overreach."""
    S, clamp = meditate_at(t)
    return upper_from(S, mocap_at(RESEARCH_CLIP, t, Quaternion()), False, SPINE), clamp

PLAY_SIT_LEAN = 16.0   # degrees the trunk is leant forward from the waist (play_sit_at), the head over the hips

def play_sit_at(t):
    """Sat cross-legged on the floor rocking a baby in the arms at t (0 .. 1 round the loop, carry_seconds('play') long;
    work_play: Biotech's PlayStatic): the play carry from the waist up on the meditation's hips and legs, joined at the
    waist and keeping its own (upright) lean, as laid over parent by parent it took the meditation's tipped-back pelvis
    and leant far back. The meditation runs a whole number of times round the loop, so it closes. Returns the pose and
    the worst overreach."""
    times = max(1, round(carry_seconds('play') / MEDITATE_SECONDS))
    S, _ = meditate_at((t * times) % 1.0)
    C, clamp = carry_at(t, 'play')
    X = upper_from(S, C, False, SPINE)
    # Leant forward from the waist, the arms and the baby with it: the meditation's waist is well behind its hips, so the
    # carry's upright trunk on it has the head behind them.
    waist = X[HIPS] @ head[SPINE]
    turn_branch(X, SPINE, about(waist, (1, 0, 0), PLAY_SIT_LEAN))
    return X, clamp

VISIT_SIT_LEAN = 16.0   # degrees the trunk is leant forward from the waist (visit_sit_at), the head over the hips

def visit_sit_at(t):
    """Visiting someone sick on the ground at t (0 .. 1 round the loop, TALK_SECONDS long; work_visit: VisitSickPawn by
    a patient not in a bed): sat cross-legged beside them talking with the hands, talk_at from the waist up on the
    meditation's hips and legs, joined at the waist keeping its own upright lean and then leant forward VISIT_SIT_LEAN,
    as play_sit_at is. The meditation runs a whole number of times round the loop, so it closes. Returns the pose and
    the worst overreach."""
    times = max(1, round(TALK_SECONDS / MEDITATE_SECONDS))
    S, _ = meditate_at((t * times) % 1.0)
    T, clamp = talk_at(t)
    X = upper_from(S, T, False, SPINE)
    waist = X[HIPS] @ head[SPINE]
    turn_branch(X, SPINE, about(waist, (1, 0, 0), VISIT_SIT_LEAN))
    return X, clamp

def raise_trunk(X, deg):
    """The trunk in X straightened back deg about the waist, so a stooped chest comes up out of the thighs (the chibi's
    short trunk folds down onto them where the clip's rig's clears), the head turned back down by as much so it still
    looks where the clip looks, and each arm put back by IK so the hands stay where the clip has them."""
    if not deg:
        return 0.0
    hands = held_hands(X)
    waist = X[HIPS] @ head[SPINE]
    turn_branch(X, SPINE, about(waist, (1, 0, 0), -deg))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), deg))
    return keep_hands(X, hands)

def bend_waist(X, share):
    """The waist in X bent evenly: the lower spine (SPINE) leant toward halfway between the hips' lean and the chest's by
    share, and the chest turned back by as much so it and the head lean as before. A seated clip laid on the sit has the
    clip's own trunk on the sit's hips, which lean forward where the clip's sat back; its lower spine stands up straight
    off them and its chest leans well forward, so the belly bends back and then folds forward at the chest. The hands are
    put back by IK where the clip has them. Returns the worst overreach."""
    if not share:
        return 0.0
    hands = held_hands(X)
    deg = ((pitch_of(X, HIPS) + pitch_of(X, CHEST_BONE)) / 2 - pitch_of(X, SPINE)) * share
    turn_branch(X, SPINE, about(X[HIPS] @ head[SPINE], (1, 0, 0), deg))
    turn_branch(X, CHEST_BONE, about(X[SPINE] @ head[CHEST_BONE], (1, 0, 0), -deg))
    return keep_hands(X, hands)

def held_hands(X):
    """Each hand's bones and fingers in X, by side, to put back with keep_hands."""
    hands = {}
    for side in ('L', 'R'):
        hn = 'hand.' + side
        hands[side] = {b.name: X[b.name].copy() for b in [bones[hn]] + list(bones[hn].children_recursive) if b.name in X}
    return hands

def keep_hands(X, hands):
    """Each arm in X put back by IK to the wrist held_hands took, the elbow the way it points now, and the hand as it
    was, moved onto the wrist the arm reached. Returns the worst overreach."""
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        hn = 'hand.' + side
        wrist = hands[side][hn] @ head[hn]
        shoulder, elbow = X['shoulder.' + side] @ head['upper_arm.' + side], X['forearm.' + side] @ head['forearm.' + side]
        pole = X[CHEST_BONE].to_3x3().inverted() @ (elbow - (shoulder + wrist) / 2)
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=pole))
        # The hand as the clip turns it, moved onto the wrist the arm reached (short of it if out of reach).
        M = T(X[hn] @ head[hn] - wrist)
        X.update((name, M @ m) for name, m in hands[side].items())
    return clamp

def crouch_at(t):
    """Knelt at work on the ground at t (0 .. 1 round the loop; see CROUCH_CLIP), the right arm brought up out of the
    raised right knee (CROUCH_KNEE, CROUCH_ELBOW). Returns the pose and the worst overreach."""
    X = kneel_at(CROUCH_CLIP, t)
    raised = raise_trunk(X, CROUCH_RAISE)
    hand = {b.name: X[b.name] for b in [bones['hand.R']] + list(bones['hand.R'].children_recursive) if b.name in X}
    # The right knee swung in about its hip-to-ankle line (whichever way takes it toward the middle), the foot kept planted.
    hip, ankle = X['thigh.R'] @ head['thigh.R'], X['foot.R'] @ head['foot.R']
    axis = (ankle - hip).normalized()
    knee = X['shin.R'] @ head['shin.R'] - hip
    way = 1.0 if axis.cross(knee - knee.dot(axis) * axis).x >= 0 else -1.0
    M = about(hip, axis, CROUCH_KNEE * way)
    knee = M @ X['shin.R'] @ head['shin.R']
    # The clip holds that foot up off the floor, toes raised into the stooped chest, so it is planted flat at its rest
    # height under the knee (CROUCH_SHIN of the way back toward the hip), still pointing where the clip points it.
    ankle = knee.lerp(hip, CROUCH_SHIN)
    ankle.z = head['foot.R'].z
    way = X['foot.R'].to_3x3() @ (tail['foot.R'] - head['foot.R'])
    rest = tail['foot.R'] - head['foot.R']
    turn = aim(Vector((rest.x, rest.y, 0.0)), Vector((way.x, way.y, 0.0)))
    leg_to('R', hip, ankle, knee - hip, X, turn=turn)
    # The right arm put back by IK to the clip's own wrist, the elbow toward CROUCH_ELBOW (in the figure's own frame, so
    # out past the knee whatever the stooped chest's turn), the hand and fingers kept as the clip has them.
    way = Vector((-CROUCH_ELBOW[0], CROUCH_ELBOW[1], CROUCH_ELBOW[2]))
    clamp = arm_to('R', -1, X['hand.R'] @ head['hand.R'], X, pole=X[CHEST_BONE].to_3x3().inverted() @ way)
    X.update(hand)
    return X, max(clamp, raised)

def sow_at(t):
    """Crouched hunched over sowing or planting at t (0 .. 1 round the loop; see SOW_CLIP), the feet planted and the
    hands kept out of the ground (SOW_HANDS). Returns the pose and the worst overreach."""
    X = kneel_at(SOW_CLIP, t)
    raised = raise_trunk(X, SOW_RAISE)
    floor = min(head['toe.L'].z, head['toe.R'].z)
    # The clip's rig has longer legs for its arms than the chibi, so set on the floor by its lowest ball of the foot
    # over the loop, the feet float and bob by up to a tenth of a leg length while the hands dig well into the ground.
    # Each foot is let down onto its ball as the clip turns it, the knee bent the clip's way.
    for side in ('L', 'R'):
        ft, toe = 'foot.' + side, 'toe.' + side
        hip = X['thigh.' + side] @ head['thigh.' + side]
        ball = X[toe] @ head[toe]
        toe_rel = X[ft].inverted() @ X[toe]
        ankle = X[ft] @ head[ft] - Vector((0, 0, ball.z - floor))
        # The knee swung out SOW_SPREAD about the hip-to-ankle line, so the trunk folds down between the thighs.
        axis = (ankle - hip).normalized()
        knee = X['shin.' + side] @ head['shin.' + side] - hip
        out = 1.0 if side == 'L' else -1.0
        way = 1.0 if axis.cross(knee - knee.dot(axis) * axis).x * out >= 0 else -1.0
        knee = Matrix.Rotation(math.radians(SOW_SPREAD * way), 3, axis) @ knee
        leg_to(side, hip, ankle, knee, X, turn=X[ft].to_3x3().to_4x4())
        X[toe] = X[ft] @ toe_rel
    # Each hand lifted so its lowest fingertip eases to a stop on the floor rather than going under it: above
    # SOW_HANDS it follows the clip, below it is let down less and less.
    clamp = raised
    reach = SOW_HANDS * LEG
    for side, sign in (('L', 1), ('R', -1)):
        hn = 'hand.' + side
        hand = [bones[hn]] + list(bones[hn].children_recursive)
        low = min((X[b.name] @ tail[b.name]).z for b in hand if b.name in X) - floor
        if low >= reach:
            continue
        wrist = X[hn] @ head[hn] + Vector((0, 0, reach * math.exp((low - reach) / reach) - low))
        fingers = {b.name: X[hn].inverted() @ X[b.name] for b in hand[1:] if b.name in X}
        turn = (X[CHEST_BONE].to_quaternion().to_matrix().inverted() @ X[hn].to_3x3().normalized()).to_euler('XYZ')
        shoulder, elbow = X['shoulder.' + side] @ head['upper_arm.' + side], X['forearm.' + side] @ head['forearm.' + side]
        pole = X[CHEST_BONE].to_3x3().inverted() @ (elbow - (shoulder + wrist) / 2)
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=pole, turn=[math.degrees(a) for a in turn]))
        X.update((name, X[hn] @ rel) for name, rel in fingers.items())
    return X, clamp

def bench_at(t):
    """Stood at a bench working at t (0 .. 1 round the loop; see BENCH_CLIP), the trunk stood up BENCH_RAISE and the head
    lifted BENCH_LOOK, the hands kept where the clip has them. Returns the pose and the worst overreach."""
    X = kneel_at(BENCH_CLIP, t)
    clamp = raise_trunk(X, BENCH_RAISE)
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), -BENCH_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    return X, clamp

def bench_sit_at(t):
    """Sat at a bench working at t (0 .. 1 round the loop; see BENCH_SIT_CLIP): the clip from the waist up on the sit's
    hips and legs, keeping its own lean. Returns the pose and the worst overreach."""
    return seated_upper(BENCH_SIT_CLIP, t)

def chair_at(t):
    """Sat working on the lap at t (0 .. 1 round the loop; see CHAIR_CLIP), as bench_sit_at. Returns the pose and the
    worst overreach."""
    return seated_upper(CHAIR_CLIP, t)

def eat_stand_at(t):
    """Eating standing at t (0 .. 1 round the loop; see EAT_STAND_CLIP): the right hand to the mouth, the left holding the
    meal (EAT_MEAL). Returns the pose and the worst overreach."""
    X, clamp = eat_at(EAT_STAND_CLIP, t, stand_pose(), 'R', -1, None, EAT_LEVEL[1])
    chest = X[CHEST_BONE].to_3x3()
    palm = X[CHEST_BONE] @ (head[HIPS] + Vector(EAT_MEAL) * LEG)
    clamp = max(clamp, hand_on('L', 1, palm, chest @ Vector(EAT_MEAL_FINGERS), chest @ Vector((0, 0, 1)), X))
    close_hand(X, 'L', 1, EAT_MEAL_CURL)
    return X, clamp

def hand_point_to(side, sign, at, target, fingers, facing, X, pole=None, curl=0.0, tries=4):
    """Places one hand by hand_on so that the point at(X) on it (armature space, worked out from the pose, as a fingertip
    is) lands on target, the fingers along fingers and the palm facing facing, curled curl of FIST. Returns how far
    the wrist was out of reach."""
    palm = target.copy()
    clamp = 0.0
    for _ in range(tries):
        clamp = hand_on(side, sign, palm, fingers, facing, X, pole=pole)
        close_hand(X, side, sign, curl)
        palm += target - at(X)
    clamp = hand_on(side, sign, palm, fingers, facing, X, pole=pole)
    close_hand(X, side, sign, curl)
    return clamp

def head_point(X, v, sign):
    """A place given from the head's joint in its rest frame (leg lengths, x out toward the side of sign, y back, z up),
    in the pose X (armature space)."""
    v = Vector(v)
    v.x *= sign
    return X[HEAD] @ (head[HEAD] + v * LEG)

def bow_head(X, deg):
    """The head bowed deg degrees (+ down) about the neck, in place."""
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), deg))

def smoke_rest():
    """The joint in the right hand at rest: the matrix from its model's points (Blender's axes, model units, its filter
    end at the origin and its lit end up +z) to the rest pose (armature space), which the hand's move takes to any pose
    (the `tool smoke joint R` line); across the fingers (SMOKE_JOINT), through them from the palm's side."""
    hn, fn = 'hand.R', 'hand.R.001'
    d0, n0 = hand_rest_axes('R', -1)
    t0 = d0.cross(n0)   # toward the thumb's side, as grip_handle's thumb for the right hand
    t0 = (t0 - t0.dot(d0) * d0).normalized()
    d1 = (tail[fn] - head[fn]).normalized()
    flen = (tail[fn] - head[fn]).length + (tail['hand.R.002'] - head['hand.R.002']).length
    cross = head[fn] + d1 * SMOKE_JOINT[0] * flen + t0 * SMOKE_JOINT[1] * flen
    tip = math.radians(SMOKE_TIP)
    a = (-n0 * math.cos(tip) + d1 * math.sin(tip)).normalized()   # from the palm's side out of the back of the fingers
    x = (t0 - t0.dot(a) * a).normalized()
    size = SMOKE_SIZE * LEG / MODEL_LEG
    filter_end = cross - a * SMOKE_OUT * size
    return T(filter_end) @ Matrix((x, a.cross(x), a)).transposed().to_4x4() @ Matrix.Scale(size, 4)

SMOKE_OUT = 0.025   # how far the joint's filter end stands out on the palm's side of the fingers (its model's units)

def smoke_at(t):
    """Smoking at t (0 .. 1 round the loop; see SMOKE_KEYS): the joint in the right hand from by the shoulder to the lips
    and back, to be laid over the idle or the sit from the waist up (EAT_UPPER). Returns the pose and the worst
    overreach."""
    k = {n: loop_key(SMOKE_AT, line, t) for n, line in SMOKE_KEYS.items()}
    breath = min(max(0.5 * k['breath'] + 0.25 - 0.25 * math.cos(2 * math.pi * SMOKE_BREATHS * t), 0.0), 1.0)
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    bow_head(X, k['bow'])
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    level = read_level(X)
    filter_rest = smoke_rest() @ Vector((0, 0, 0))
    at = lambda Y: Y['hand.R'] @ filter_rest
    m = min(max(k['mouth'], 0.0), 1.0)
    rest = X['shoulder.R'] @ head['upper_arm.R'] + level @ (Vector(SMOKE_REST) - Vector((0, 0, k['drop']))) * wrist_reach('R')
    lips = head_point(X, SMOKE_MOUTH, -1)
    target = rest.lerp(lips, smooth(m))
    facing = (level @ Vector(SMOKE_REST_FACING)).normalized().lerp((level @ Vector(SMOKE_MOUTH_FACING)).normalized(), smooth(m))
    fingers = (level @ Vector(SMOKE_REST_FINGERS)).normalized().lerp((level @ Vector(SMOKE_MOUTH_FINGERS)).normalized(), smooth(m))
    clamp = hand_point_to('R', -1, at, target, fingers, facing, X, pole=Vector(SMOKE_ELBOW), curl=k['curl'])
    close_hand(X, 'L', 1, 0.3)
    return X, clamp

def pill_pinch(X):
    """Where the right hand's fingertips and thumb tip meet in the pose X (armature space)."""
    return (X['hand.R.002'] @ tail['hand.R.002'] + X['thumb.R.001'] @ tail['thumb.R.001']) * 0.5

def pill_at(t):
    """Taking a pill (or a powder) at t (0 .. 1, once through; see PILL_KEYS): the left palm holding it, the right
    fingers pinching from it up to the mouth, the head tipped back to swallow; to be laid over the idle or the sit from
    the waist up (EAT_UPPER). Returns the pose and the worst overreach."""
    k = {n: toss_key(line, t, PILL_AT) for n, line in PILL_KEYS.items()}
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=0.3)
    bow_head(X, k['bow'])
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    chest = X[CHEST_BONE].to_3x3()
    palm = X[CHEST_BONE] @ (head[HIPS] + Vector(EAT_MEAL) * LEG)
    clamp = hand_on('L', 1, palm, chest @ Vector(EAT_MEAL_FINGERS), chest @ Vector((0, 0, 1)), X)
    close_hand(X, 'L', 1, EAT_MEAL_CURL)
    level = read_level(X)
    m = smooth(min(max(k['mouth'], 0.0), 1.0))
    target = (palm + level @ Vector(PILL_PALM) * LEG).lerp(head_point(X, PILL_MOUTH, -1), m)
    way = lambda v: (level @ Vector(v)).normalized()
    facing = way(PILL_FACING[0]).lerp(way(PILL_FACING[1]), m)
    fingers = way(PILL_FINGERS[0]).lerp(way(PILL_FINGERS[1]), m)
    clamp = max(clamp, hand_point_to('R', -1, pill_pinch, target, fingers, facing, X, pole=Vector(PILL_ELBOW), curl=k['curl']))
    return X, clamp

def read_level(X):
    """A level frame turned as the chest of X is about the vertical (x out to the left, y back, z up)."""
    side = X[CHEST_BONE].to_3x3() @ Vector((1, 0, 0))
    side = Vector((side.x, side.y, 0.0)).normalized()
    return Matrix((side, Vector((0, 0, 1)).cross(side), Vector((0, 0, 1)))).transposed()

def read_book(X, show=0):
    """Where the open book is in the pose X: the matrix from its model's points (Blender's axes, model units, as
    tool_matrix takes them) to armature space, its underside's middle at READ_BOOK, tipped up READ_TILT; shown to the
    left (show 1) or right (-1), moved READ_SHOW_OUT that way and turned READ_SHOW_TURN to face it."""
    level = read_level(X)
    at = X[CHEST_BONE] @ head[HIPS] + level @ (Vector(READ_BOOK) + Vector((show * READ_SHOW_OUT, 0, 0))) * LEG
    R = level.to_4x4() @ rot((0, 0, 1), -show * READ_SHOW_TURN) @ rot((1, 0, 0), -READ_TILT)
    return T(at) @ R @ Matrix.Scale(READ_SCALE * LEG / MODEL_LEG, 4)

def read_show_L_at(t):
    return read_at(t, 1)

def read_show_R_at(t):
    return read_at(t, -1)

def read_at(t, show=0):
    """Reading at t (0 .. 1 round the loop; see READ_SECONDS): standing, the open book on the left palm, the right hand
    on its right page and turning one over READ_TURN, to be laid over the sit or the idle from the waist up (EAT_UPPER);
    with show 1 or -1 the book held out to someone sat on the left or right (READ_SHOW_OUT). Returns the pose and the
    worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * READ_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), show * READ_SHOW_TWIST * 0.5)
                    @ about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), READ_LEAN * 0.5))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (0, 0, 1), show * READ_SHOW_LOOK)
                @ about(X[NECK] @ head[NECK], (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), READ_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    B = read_book(X, show)
    R = B.to_3x3().normalized()
    ex, ey, ez = R.col[0], R.col[1], R.col[2]
    origin = B.translation
    # The left palm under the spine, facing up into the book.
    palm = origin - ez * READ_PALM * LEG
    clamp = hand_on('L', 1, palm, R @ Vector(READ_FINGERS), ez, X, pole=Vector(READ_ELBOW_L))
    close_hand(X, 'L', 1, READ_CURL)
    # The right palm on the right page, and once a loop over to the left page and back, arcing up over the book.
    half_w = 0.282 * READ_SCALE * LEG / MODEL_LEG   # IsoRim's open_book: 0.282 model units either side of the spine
    half_d = 0.195 * READ_SCALE * LEG / MODEL_LEG
    rest_at, over_at = Vector(READ_REST), Vector(READ_OVER)
    a, b = READ_TURN
    s = (t - a) / (b - a) if a <= t < b else -1.0
    roll = 0.0
    if s < 0:
        v, lift = rest_at, 0.0
    elif s < 0.55:
        u = s / 0.55
        v, lift = rest_at.lerp(over_at, smooth(u)), READ_LIFT * math.sin(math.pi * u)
        roll = 50.0 * math.sin(math.pi * 0.5 * u)
    else:
        u = (s - 0.55) / 0.45
        v, lift = over_at.lerp(rest_at, smooth(u)), 0.6 * READ_LIFT * math.sin(math.pi * u)
        roll = 50.0 * (1.0 - smooth(u))
    point = origin + ex * v.x * half_w + ey * v.y * half_d + ez * (v.z + lift) * half_w
    facing = Matrix.Rotation(math.radians(roll), 3, ey) @ -ez   # turned over toward the left page as it carries it
    fingers = R @ Vector((0.4, -1.0, 0.0))
    clamp = max(clamp, hand_on('R', -1, point, fingers, facing, X, pole=Vector(READ_ELBOW_R)))
    close_hand(X, 'R', -1, 0.1 + 0.3 * math.sin(math.pi * max(0.0, min(1.0, s))) if s >= 0 else 0.1)
    return X, clamp

def read_rest():
    """The book on the left hand at rest: the matrix from its model's points (as read_book) to the rest pose (armature
    space), which the hand's move takes to any pose (the `tool read` line)."""
    X, _ = read_at(0.0)
    return X['hand.L'].inverted() @ read_book(X)

def pluck_at(t):
    """Kneeling at work on the ground at t (0 .. 1 round the loop; see PLUCK_LEAN). Returns the pose and the worst
    overreach."""
    X = kneel_at(PLUCK_CLIP, t)
    # The trunk leant over to the right and stooped, about the waist.
    waist = X[HIPS] @ head[SPINE]
    turn_branch(X, SPINE, about(waist, (0, -1, 0), PLUCK_LEAN) @ about(waist, (1, 0, 0), PLUCK_STOOP))
    # The right hand pawing: dragged back along the floor beside the right foot over PLUCK_DRAG of each paw, then lifted
    # and reached forward again.
    s = (t * PLUCK_PAWS) % 1.0
    if s < PLUCK_DRAG:
        u = smooth(s / PLUCK_DRAG)
        ahead, high, curl = 1.0 - u, 0.0, PLUCK_CURL[0] + (PLUCK_CURL[1] - PLUCK_CURL[0]) * smooth(max(0.0, u - 0.7) / 0.3)
    else:
        u = (s - PLUCK_DRAG) / (1.0 - PLUCK_DRAG)
        ahead, high = smooth(u), math.sin(math.pi * u)
        curl = PLUCK_CURL[1] + (PLUCK_CURL[0] - PLUCK_CURL[1]) * smooth(max(0.0, u - 0.6) / 0.4)
    shoulder = X['shoulder.R'] @ head['upper_arm.R']
    wrist = Vector((shoulder.x - PLUCK_PAW[0] * LEG, shoulder.y - (PLUCK_PAW[1] - PLUCK_PAW[2] * (1.0 - ahead)) * LEG,
                    LEG * (PLUCK_LOW + (PLUCK_HIGH - PLUCK_LOW) * high)))
    # The hand pointed forward and down, its palm to the ground: its rest line and palm turned onto those.
    hn = 'hand.R'
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector((-HAND_PALM[0], HAND_PALM[1], HAND_PALM[2]))
    n0 = (n0 - n0.dot(d0) * d0).normalized()
    d1 = Vector((0.0, -math.cos(math.radians(PLUCK_POINT)), -math.sin(math.radians(PLUCK_POINT))))
    n1 = Vector((0.0, 0.0, -1.0))
    n1 = (n1 - n1.dot(d1) * d1).normalized()
    R0 = Matrix((d0, n0, d0.cross(n0))).transposed()
    R1 = Matrix((d1, n1, d1.cross(n1))).transposed()
    want = (X[CHEST_BONE].to_quaternion().inverted() @ (R1 @ R0.transposed()).to_quaternion()).to_euler()
    clamp = arm_to('R', -1, wrist, X, pole=Vector((-0.8, 0.4, -0.4)), turn=[math.degrees(a) for a in want])
    close_hand(X, 'R', -1, curl)
    return X, clamp

def chalk_rest():
    """The chalk in the right hand at rest: the matrix from its model's points (Blender's axes, model units, its tip at the
    origin and its back end up +z) to the rest pose (armature space), which the hand's move takes to any pose (the `tool
    chalk tool_chalk R` line). Held in the fist as a child holds it: across the palm (CHALK_PALM out from it, CHALK_GRIP
    along the fingers' joint), its back end out of the thumb's side and its tip out of the little finger's
    (CHALK_OUT), tipped CHALK_TIP toward the fingertips."""
    fn = 'hand.R.001'
    d0, n0 = hand_rest_axes('R', -1)
    t0 = d0.cross(n0)   # toward the thumb's side
    t0 = (t0 - t0.dot(d0) * d0).normalized()
    flen = (tail[fn] - head[fn]).length + (tail['hand.R.002'] - head['hand.R.002']).length
    cross = head[fn] + d0 * CHALK_GRIP * flen + n0 * CHALK_PALM * LEG
    tip = math.radians(CHALK_TIP)
    a = (t0 * math.cos(tip) - d0 * math.sin(tip)).normalized()   # from the tip out of the thumb's side
    x = (n0 - n0.dot(a) * a).normalized()
    size = CHALK_SIZE * LEG / MODEL_LEG
    return T(cross - a * CHALK_OUT * size) @ Matrix((x, a.cross(x), a)).transposed().to_4x4() @ Matrix.Scale(size, 4)

def chalk_tip(X):
    """Where the chalk's tip is in the pose X (armature space)."""
    return X['hand.R'] @ (chalk_rest() @ Vector((0, 0, 0)))

def chalk_at(t):
    """Drawing on the floor in chalk at t (0 .. 1 round the loop, PLUCK_SECONDS long; see CHALK_MIDDLE). Returns the pose
    and the worst overreach."""
    X = kneel_at(PLUCK_CLIP, t)
    waist = X[HIPS] @ head[SPINE]
    turn_branch(X, SPINE, about(waist, (0, -1, 0), CHALK_LEAN) @ about(waist, (1, 0, 0), CHALK_STOOP))
    bow_head(X, CHALK_LOOK)
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    level = read_level(X)
    floor = min(head['toe.L'].z, head['toe.R'].z)
    hips = X[HIPS] @ head[HIPS]
    base = Vector((hips.x, hips.y, floor))
    # The tip's path: loops scribbled while sweeping across and back, lifted off the floor once a loop.
    w = 2 * math.pi * t
    loop = 2 * math.pi * CHALK_LOOPS * t
    flat = (Vector(CHALK_MIDDLE) + Vector((CHALK_SWEEP * math.sin(w), 0.0))
            + Vector((CHALK_RADIUS[0] * math.cos(loop), CHALK_RADIUS[1] * math.sin(loop))))
    s = ((t - CHALK_LIFT_AT[0]) % 1.0) / CHALK_LIFT_AT[1]
    lift = CHALK_LIFT * math.sin(math.pi * s) if s < 1.0 else 0.0
    target = base + Vector((flat.x, flat.y, CHALK_FLOOR + lift)) * LEG
    way = lambda v: (level @ Vector(v)).normalized()
    clamp = hand_point_to('R', -1, chalk_tip, target, way(CHALK_FINGERS), way(CHALK_FACING), X,
                          pole=Vector(CHALK_ELBOW), curl=CHALK_CURL)
    # The left palm propped flat on the floor, taking the lean.
    palm = base + Vector((CHALK_PROP[0], CHALK_PROP[1], 0.01)) * LEG
    clamp = max(clamp, hand_on('L', 1, palm, way(CHALK_PROP_FINGERS), Vector((0, 0, -1)), X, pole=Vector(CHALK_PROP_ELBOW)))
    close_hand(X, 'L', 1, 0.15)
    return X, clamp

def belly_crawl_at(p):
    """The belly crawl at cycle phase p, upright (see CRAWL_REACH). Returns the pose and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.005, LEAN=0.0, NARROW=LIE_NARROW), 0.29)
    c = math.cos(2 * math.pi * p)   # +1: the left hand reached ahead, the right leg drawn up
    middle = (head['thigh.L'] + head['thigh.R']) / 2
    roll = place(X[HIPS] @ middle, rot((0, 0, 1), -CRAWL_ROLL * c), X[HIPS] @ middle)
    X = moved_all(roll, X)
    turn = roll.to_3x3().to_4x4()
    X[NECK] = place(X[CHEST_BONE] @ head[NECK], turn @ rot((1, 0, 0), -CRAWL_LOOK * 0.45), head[NECK])
    X[HEAD] = place(X[NECK] @ head[HEAD], turn @ rot((1, 0, 0), -CRAWL_LOOK), head[HEAD])
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        u = 0.5 + 0.5 * c * sign   # 1 reached ahead, 0 pulled back
        shoulder = X['shoulder.' + side] @ head[ua]
        wrist = shoulder + turn.to_3x3() @ Vector((sign * CRAWL_HANDS[0], -CRAWL_HANDS[1], CRAWL_HANDS[2] + CRAWL_REACH * u)) * reach
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign, 0.6, -0.3))))
        ft = 'foot.' + side
        drawn = 1 - u   # the leg against the reaching hand
        ankle = X[ft] @ head[ft] + turn.to_3x3() @ Vector((sign * 0.5, 0.0, 1.0)) * (CRAWL_DRAW * drawn * LEG)
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, turn.to_3x3() @ Vector((sign, 0.3, 0.0)),
                                  X, pitch=LIE_POINT, turn=turn))
    return X, clamp

def crawl_ground_per_cycle():
    """Body travel per crawl cycle (armature units): the mocap clip's, else each hand drags it its reach."""
    if CRAWL_CLIP is not None:
        return CRAWL_CLIP['ground'] * LEG
    reach = (tail['upper_arm.L'] - head['upper_arm.L']).length + (tail['forearm.L'] - head['forearm.L']).length
    return 2 * CRAWL_REACH * reach

def bump(q, at, width):
    """1 at cycle phase at, easing to 0 width either side of it (round the cycle)."""
    d = abs((q - at + 0.5) % 1.0 - 0.5)
    return smooth(max(0.0, 1.0 - d / width))

def limp_at(p, bad):
    """The limp at cycle phase p, favouring the bad leg ('L' or 'R'; see LIMP_GOOD). Returns the pose and the worst
    overreach."""
    good = 'R' if bad == 'L' else 'L'
    bs = 1 if bad == 'L' else -1   # the bad side's sign: +1 left
    q = (p - (0.0 if good == 'L' else 0.5)) % 1.0   # from the good heel strike, where the walk has that heel's
    pace = STRIDE * LIMP_STRIDE / STANCE   # body travel per cycle, leg lengths
    X, _ = posed_with(dict(BOB=0.0, ROLL=0.0), p)
    # The weight is on the good foot alone from the bad toe-off to the bad heel strike, and on the bad one alone while
    # the good one swings.
    off = max(LIMP_STRIKE + LIMP_STANCE, 1.0) - 1.0
    on_good = bump(q, (off + LIMP_STRIKE) / 2, (LIMP_STRIKE - off) / 2 + 0.1)
    on_bad = bump(q, (LIMP_GOOD + 1.0) / 2, (1.0 - LIMP_GOOD) / 2 + 0.1)
    hip = X[HIPS] @ head[HIPS]
    shift = Vector((LEG * LIMP_SWAY * bs * (on_bad - on_good), 0,
                    LEG * (LIMP_RISE * on_bad - LIMP_DIP * bump(q, LIMP_DIP_AT, 0.2))))
    hike = LIMP_HIKE * on_good   # the bad leg swings while the good one bears the weight
    X = moved_all(T(shift) @ about(hip, (0, 1, 0), -bs * hike), X)
    # The trunk righted against the hitch and leaned out over the bad leg, the head kept nearer level.
    lean = LIMP_LEAN * on_bad + 0.8 * hike
    turn_branch(X, SPINE, about(X[SPINE] @ head[SPINE], (0, 1, 0), bs * lean))
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (0, 1, 0), -0.6 * bs * lean))
    # Each leg on its own stance share, the body going at one pace, so the bad foot takes the shorter step.
    g = globals()
    clamp = 0.0
    for side, ph, stance, own in ((good, q, LIMP_GOOD, {}),
                                  (bad, (q - LIMP_STRIKE) % 1.0, LIMP_STANCE, dict(LIFT=LIMP_LIFT, HEEL=LIMP_HEEL, KICK=0.0))):
        params = dict(own, STANCE=stance, STRIDE=pace * stance)
        saved = {k: g[k] for k in params}
        g.update(params)
        try:
            ankle, pitch = foot_path(ph, side)
        finally:
            g.update(saved)
        if side == bad and ph > stance:
            ankle.x += bs * LEG * LIMP_OUT * math.sin(math.pi * (ph - stance) / (1 - stance))
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, FWD, X, pitch=pitch))
    wrist = X[HIPS] @ head['thigh.' + bad] + X[HIPS].to_3x3() @ Vector((bs * LIMP_HAND[0], LIMP_HAND[1], LIMP_HAND[2])) * LEG
    clamp = max(clamp, arm_to(bad, bs, wrist, X, pole=Vector((bs * 1.0, 0.6, -0.4))))
    return X, clamp

def limp_ground_per_cycle():
    """Body travel per limping cycle (armature units)."""
    return LEG * STRIDE * LIMP_STRIDE / STANCE

def hop_at(p, gone):
    """The hop at cycle phase p, on one leg, the leg gone ('L' or 'R') held up (see HOP_STANCE). Returns the pose and the
    worst overreach."""
    keep = 'R' if gone == 'L' else 'L'
    gs = 1 if gone == 'L' else -1   # the gone side's sign: +1 left
    X, _ = posed_with(dict(STILL, DROP=HOP_DROP), 0.29)
    # In the air the hips follow a parabola; on the ground they sink on a sine, as deep as keeps their speed at the landing.
    if p < HOP_STANCE:
        squat = 4 * HOP_HEIGHT * HOP_STANCE / ((1 - HOP_STANCE) * math.pi)
        rise = -squat * math.sin(math.pi * p / HOP_STANCE)
    else:
        u = (p - HOP_STANCE) / (1 - HOP_STANCE)
        rise = 4 * HOP_HEIGHT * u * (1 - u)
    over = (X['foot.' + keep] @ head['foot.' + keep]).x * HOP_OVER
    X = moved_all(T(Vector((over, 0, rise * LEG))), X)
    # 1 in the middle of the time on the ground, -1 at the top of the hop.
    wave = math.cos(2 * math.pi * (p - HOP_STANCE / 2))
    lean = HOP_LEAN + HOP_NOD * wave
    sp = X[SPINE] @ head[SPINE]
    turn_branch(X, SPINE, about(sp, (1, 0, 0), lean) @ about(sp, (0, 1, 0), -gs * HOP_TILT))
    nk = X[CHEST_BONE] @ head[NECK]
    turn_branch(X, NECK, about(nk, (1, 0, 0), -lean * HOP_LOOK) @ about(nk, (0, 1, 0), gs * HOP_TILT * HOP_LOOK))
    # The standing leg on the hop's own stance share, the foot going back at the body's pace.
    g = globals()
    params = dict(STANCE=HOP_STANCE, STRIDE=HOP_GROUND * HOP_STANCE, LIFT=HOP_LIFT, HEEL=HOP_HEEL, KICK=0.0, PEAK=0.5)
    saved = {k: g[k] for k in params}
    g.update(params)
    try:
        ankle, pitch = foot_path(p, keep)
    finally:
        g.update(saved)
    # Just after it pushes off the heel is still high, which would put the toes through the ground.
    toe = rot((1, 0, 0), pitch).to_3x3() @ Vector((0, toe_off.y, toe_off.z))
    ankle.z = max(ankle.z, head['foot.' + keep].z + toe_off.z - toe.z)
    clamp = leg_to(keep, X[HIPS] @ head['thigh.' + keep], ankle, FWD, X, pitch=pitch)
    hip = X[HIPS] @ head['thigh.' + gone]
    stump = Vector((gs * HOP_STUMP[0], HOP_STUMP[1] + HOP_STUMP_SWING * wave, HOP_STUMP[2]))
    clamp = max(clamp, leg_to(gone, hip, hip + X[HIPS].to_3x3() @ stump * LEG, FWD, X))
    up = (1 - wave) / 2
    for side, sign in (('L', 1), ('R', -1)):
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        w = Vector(HOP_HANDS_DOWN).lerp(Vector(HOP_HANDS_UP), up)
        wrist = X['shoulder.' + side] @ head[ua] + X[CHEST_BONE].to_3x3() @ Vector((sign * w.x, w.y, w.z)) * reach
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * JUMP_ELBOW[0], JUMP_ELBOW[1], JUMP_ELBOW[2]))))
    return X, clamp

def hop_ground_per_cycle():
    """Body travel per hop (armature units)."""
    return LEG * HOP_GROUND

WADE_KEYS = ('STRIDE', 'LIFT', 'PEAK', 'KICK', 'LEAN', 'TWIST', 'CHEST', 'DROP', 'ARM', 'ARM_DOWN', 'ELBOW')

def wade_at(p):
    """The wade at cycle phase p (see WADE_STRIDE). Returns the pose and the worst overreach."""
    return posed_with({k: globals()['WADE_' + k] for k in WADE_KEYS}, p)

def wade_ground_per_cycle():
    """Body travel per wading cycle (armature units)."""
    return LEG * WADE_STRIDE / STANCE

def swim_at(p):
    """The front crawl at cycle phase p, upright: the mocap clip if there is one (see SWIM_TIP), else the stroke of
    SWIM_FRAMES. Returns the pose and the worst overreach."""
    if SWIM_CLIP is not None:
        return mocap_at(SWIM_CLIP, p, Quaternion((1, 0, 0), math.radians(-SWIM_TIP))), 0.0
    X, _ = posed_with(dict(STILL, DROP=0.005, LEAN=0.0, NARROW=LIE_NARROW), 0.29)
    for side in ('L', 'R'):
        ft = 'foot.' + side
        X[ft] = place(X[ft] @ head[ft], X[ft].to_3x3().to_4x4() @ rot((1, 0, 0), LIE_POINT), head[ft])
        X['toe.' + side] = X[ft]
        X['heel.02.' + side] = X[ft]
    # Rolled toward the arm pulling: the left from p = 0 to 0.5, the right after.
    middle = (head['thigh.L'] + head['thigh.R']) / 2
    roll = about(X[HIPS] @ middle, (0, 0, 1), -SWIM_ROLL * math.sin(2 * math.pi * p))
    X = moved_all(roll, X)
    turn = roll.to_3x3().to_4x4()
    X[NECK] = place(X[CHEST_BONE] @ head[NECK], turn @ rot((1, 0, 0), -SWIM_LOOK * 0.45), head[NECK])
    X[HEAD] = place(X[NECK] @ head[HEAD], turn @ rot((1, 0, 0), -SWIM_LOOK), head[HEAD])
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    across = turn.to_3x3() @ Vector((1, 0, 0))
    clamp = 0.0
    for side, sign, k in (('L', 1, 0.0), ('R', -1, 0.5)):
        u = (p + k) % 1.0
        # The wrist's turn round the shoulder from ahead toward the surface (y over z), always going the same way:
        # from ahead down past the bottom to the hip, then on round past the surface to ahead again.
        ahead, pulled = Vector(SWIM_AHEAD), Vector(SWIM_PULLED)
        a_ahead = math.atan2(ahead.y, ahead.z)
        a_pulled = math.atan2(pulled.y, pulled.z)
        if a_pulled > a_ahead:
            a_pulled -= 2 * math.pi
        r_ahead, r_pulled = ahead.yz.length, pulled.yz.length
        if u < 0.5:   # the pull, under the body
            t = u / 0.5
            ang = a_ahead + (a_pulled - a_ahead) * smooth(t)
            r = r_ahead + (r_pulled - r_ahead) * smooth(t) + (SWIM_DEEP - (r_ahead + r_pulled) / 2) * math.sin(math.pi * t)
            x = ahead.x + (pulled.x - ahead.x) * smooth(t)
        else:   # back over the water, the elbow high
            t = (u - 0.5) / 0.5
            ang = a_pulled + (a_ahead - 2 * math.pi - a_pulled) * smooth(t)
            r = r_pulled + (r_ahead - r_pulled) * smooth(t) + (SWIM_HIGH - (r_ahead + r_pulled) / 2) * math.sin(math.pi * t)
            x = pulled.x + (ahead.x - pulled.x) * smooth(t)
        at = Vector((x, r * math.sin(ang), r * math.cos(ang)))
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        wrist = X['shoulder.' + side] @ head[ua] + turn.to_3x3() @ Vector((sign * at.x, at.y, at.z)) * reach
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * 0.7, 0.7, 0.2))))
        # The flutter kick: each thigh up toward the surface and down, the knee bending on the way down.
        th, sh = 'thigh.' + side, 'shin.' + side
        w = math.sin(2 * math.pi * (SWIM_KICKS * p + k))
        turn_branch(X, th, about(X[HIPS] @ head[th], across, SWIM_KICK * w))
        turn_branch(X, sh, about(X[th] @ head[sh], across, SWIM_KNEE * max(0.0, -w)))
    return X, clamp

def swim_ground_per_cycle():
    """Body travel per swimming cycle (armature units): the mocap clip's, else SWIM_GROUND."""
    if SWIM_CLIP is not None:
        return SWIM_CLIP['ground'] * LEG
    return LEG * SWIM_GROUND

def tread_at(t):
    """Treading water at t (0 .. 1 round the loop; see TREAD_SECONDS). Returns the pose and the worst overreach."""
    a = 2 * math.pi * TREAD_STROKES * t
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    X, _ = posed_with(dict(STILL, DROP=0.01, LEAN=0.0, NARROW=LIE_NARROW), 0.29, breath=breath)
    clamp = 0.0
    for side, sign, k in (('L', 1, 0.0), ('R', -1, 0.5)):
        hip = X[HIPS] @ head['thigh.' + side]
        b = a + 2 * math.pi * k
        middle = Vector((sign * TREAD_FOOT[0], TREAD_FOOT[1], TREAD_FOOT[2]))
        ankle = hip + (middle + Vector((0, -math.cos(b), math.sin(b))) * TREAD_PEDAL) * LEG
        clamp = max(clamp, leg_to(side, hip, ankle, FWD + Vector((sign * 0.3, 0, 0)), X, pitch=LIE_POINT * 0.5))
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        h = Vector((TREAD_HAND[0] + TREAD_SCULL * math.sin(a), TREAD_HAND[1], TREAD_HAND[2]))
        wrist = X['shoulder.' + side] @ head[ua] + X[CHEST_BONE].to_3x3() @ Vector((sign * h.x, h.y, h.z)) * reach
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign, 0.5, -0.5))))
    return X, clamp

def turn_branch(X, root, M):
    """root and every bone under it moved by M (armature space)."""
    for b in [bones[root]] + list(bones[root].children_recursive):
        if b.name in X:
            X[b.name] = M @ X[b.name]

def about(pivot, axis, deg):
    """A turn of deg about axis through pivot (armature space)."""
    return T(pivot) @ rot(axis, deg) @ T(-pivot)

def carried_at(kind, t):
    """The carried person at t (0 .. 1 round the loop), limp and breathing, in the carrier's rest space (see CARRY)."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    X, _ = posed_with(dict(STILL, DROP=0.0, LEAN=0.0, NARROW=LIE_NARROW, ARM_DOWN=ARM_DOWN + LIE_ARMS, ELBOW=10.0),
                      0.29, breath=0.5 * breath)
    own, rocked = kind, CARRY[kind].get('rock')
    if rocked:
        kind = 'cradle'   # held as the cradle holds it, then rocked (carry_rock)
    fold, knee = {'bridal': (BRIDAL_FOLD, BRIDAL_KNEE), 'fireman': (FIREMAN_FOLD, FIREMAN_KNEE),
                  'cradle': (CRADLE_FOLD, CRADLE_KNEE)}[kind]
    # In its own space first, so each limb turns about its own joint: the thighs swing forward (-x turns down to -y),
    # the shins back from them.
    for side in ('L', 'R'):
        turn_branch(X, 'thigh.' + side, about(X[HIPS] @ head['thigh.' + side], (1, 0, 0), -fold))
        turn_branch(X, 'shin.' + side, about(X['thigh.' + side] @ head['shin.' + side], (1, 0, 0), knee))
    hips = X[HIPS] @ head[HIPS]
    if kind == 'bridal':
        turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -BRIDAL_LOLL))
        # Both arms hang straight down once tipped back: swung back by as much as the body tips.
        for side in ('L', 'R'):
            turn_branch(X, 'upper_arm.' + side, about(X['shoulder.' + side] @ head['upper_arm.' + side], (1, 0, 0), BRIDAL_TILT))
        at = Vector(BRIDAL_AT)
        M = rot((0, 0, 1), -90.0) @ rot((1, 0, 0), -BRIDAL_TILT)   # tipped back, then turned: its feet to the carrier's right
    elif kind == 'cradle':
        turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -CRADLE_LOLL))
        for side, sign in (('L', 1), ('R', -1)):
            if rocked:
                # Awake and played with: the arms wave as a baby's on its back do (baby_flail), raised off its chest.
                a = baby_flail(side, t)
                a['reach'] += BABY_ARMS_TIP
                baby_arm(X, side, sign, a)
                close_hand(X, side, sign, max(0.0, min(1.0, a['fist'])))
                continue
            turn_branch(X, 'upper_arm.' + side, about(X['shoulder.' + side] @ head['upper_arm.' + side], (1, 0, 0), -CRADLE_ARMS))
            turn_branch(X, 'forearm.' + side, about(X['upper_arm.' + side] @ head['forearm.' + side], (1, 0, 0), -CRADLE_ELBOWS))
        at = Vector(CRADLE_AT)
        M = rot((0, 0, 1), -90.0) @ rot((1, 0, 0), -CRADLE_TILT)
    else:
        for side in ('L', 'R'):
            turn_branch(X, 'upper_arm.' + side, about(X['shoulder.' + side] @ head['upper_arm.' + side], (1, 0, 0), -FIREMAN_ARMS))
            turn_branch(X, 'forearm.' + side, about(X['upper_arm.' + side] @ head['forearm.' + side], (1, 0, 0), -15.0))
        at = Vector(FIREMAN_AT)
        M = rot((1, 0, 0), FIREMAN_TILT) @ rot((0, 1, 0), 180.0)   # upside down, its front to the carrier
    place_m = T(head[HIPS] + at * LEG) @ M @ T(-hips)
    return moved_all(carry_rock(own, t) @ place_m, X)

def carry_seconds(kind):
    """How long a CARRY kind's loop (and its carried_<kind>) is, in game seconds."""
    if CARRY[kind].get('stroke'):
        return STROKE_SECONDS
    return PLAY_SECONDS if CARRY[kind].get('rock') else CARRY_SECONDS

def pet_on(X):
    """Where a cuddled pet's trunk middle is (armature space) in the pose X: on the left forearm (PET_ALONG, PET_LIFT)."""
    fa = 'forearm.L'
    return (X[fa] @ head[fa]).lerp(X[fa] @ tail[fa], PET_ALONG) + Vector((0, 0, PET_LIFT * LEG))

def stroke_at(t):
    """The stroking hand at t (0 .. 1 round the loop): (along the back, leg lengths, + toward the head; height over its
    top, leg lengths), STROKES strokes from STROKE_FROM to STROKE_TO each, pressed in on the way and lifted back."""
    u = (t * STROKES) % 1.0
    if u < STROKE_PRESS:
        s = u / STROKE_PRESS
        e = s * s * (3 - 2 * s)
        return STROKE_FROM + (STROKE_TO - STROKE_FROM) * e, -STROKE_DIP * math.sin(math.pi * s)
    s = (u - STROKE_PRESS) / (1 - STROKE_PRESS)
    e = s * s * (3 - 2 * s)
    return STROKE_TO + (STROKE_FROM - STROKE_TO) * e, STROKE_LIFT * math.sin(math.pi * s)

pet_last = {}   # where cuddle_hand last put the right hand, for preview.py's handle to set STROKE_* from

def cuddle_hand(X, kind, t):
    """Lays the right palm on a cuddled pet's back (see PET_BACK), at rest or stroking (a 'stroke' entry), and bows the
    head to it, in place. Returns how far the wrist was out of reach."""
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), CUDDLE_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    along, high = stroke_at(t) if CARRY[kind].get('stroke') else (STROKE_REST[0], 0.0)
    up = STROKE_REST[2] if len(STROKE_REST) > 2 else 0.0
    pet = pet_on(X)
    palm = pet + Vector((along, STROKE_REST[1], PET_BACK + high + up)) * LEG
    facing = Vector(STROKE_PALM)
    fingers, n = palm_axes(Vector(STROKE_FINGERS), facing)
    pet_last.clear()
    pet_last.update(kind=kind, pet=pet, along=along, high=high, fingers=fingers, facing=n,
                    wrist=palm - n * HAND_HALF * LEG - fingers * (tail['hand.R'] - head['hand.R']).length * 0.5)
    return hand_on('R', -1, palm, fingers, facing, X, pole=Vector((-0.9, 0.3, -0.5)))

def rock_wave(kind, t):
    """Where a rocking carry is in its swing at t (0 .. 1 round the loop): -1 the baby out to the carrier's right .. 1
    its left; 0 for a kind that doesn't rock."""
    if kind not in CARRY or not CARRY[kind].get('rock'):
        return 0.0
    return math.sin(2 * math.pi * PLAY_ROCKS * t)

def carry_rock(kind, t):
    """The baby's swing in the arms at t (see PLAY_PIVOT), in the carrier's rest space: put on the carried pose and the
    hands both, so they stay together. The identity for a kind that doesn't rock, and at t = 0."""
    s = rock_wave(kind, t)
    if s == 0.0:
        return Matrix.Identity(4)
    pivot = head[HIPS] + Vector(PLAY_PIVOT) * LEG
    # About the way back (+y): a turn by -angle swings what hangs below the pivot to +x, the carrier's left.
    return T(Vector((0, 0, PLAY_LILT * LEG * s * s))) @ about(pivot, (0, 1, 0), -PLAY_SWING * s)

def carry_at(t, kind):
    """The carrying loop for kind (a CARRY key) at t (0 .. 1 round it): standing as the drafted stance does, the hands on
    the load. Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    X, _ = posed_with(dict(STILL, DROP=0.02, NARROW=DRAFT_NARROW) | CARRY[kind].get('body', {}), 0.29, breath=breath)
    clamp = 0.0
    s = rock_wave(kind, t)
    if s != 0.0:
        # The trunk turned after the swinging baby and leant against it, from the chest's foot up (the hips and legs
        # stay; spine.002, as UPPER lays all of its turn over a gait).
        waist = X['spine.002'] @ head['spine.002']
        turn_branch(X, 'spine.002', about(waist, (0, 0, 1), PLAY_TWIST * s) @ about(waist, (0, 1, 0), PLAY_ROLL * s))
    chest = X[CHEST_BONE] @ carry_rock(kind, t)
    if CARRY[kind].get('pet'):
        clamp = arm_to('L', 1, chest @ (head[HIPS] + Vector(CARRY[kind]['L']) * LEG), X,
                       turn=CARRY[kind].get('L_turn'), lift=CARRY[kind].get('L_elbow'))
        return X, max(clamp, cuddle_hand(X, kind, t))
    if kind == 'item':
        for side, sign in (('L', 1), ('R', -1)):
            clamp = max(clamp, arm_to(side, sign, chest @ (head[HIPS] + Vector(CARRY[kind][side]) * LEG), X,
                                      turn=CARRY[kind].get(side + '_turn'), lift=CARRY[kind].get(side + '_elbow')))
        return X, clamp
    for side, (at, pole) in carry_hands(kind).items():
        if CARRY[kind].get(side) is not None:
            at = head[HIPS] + Vector(CARRY[kind][side]) * LEG
        clamp = max(clamp, arm_to(side, 1 if side == 'L' else -1, chest @ at, X, pole=pole,
                                  turn=CARRY[kind].get(side + '_turn'), lift=CARRY[kind].get(side + '_elbow')))
    return X, clamp

def carry_hands(kind):
    """Where a person carry's hands go when its CARRY entry doesn't place them: {side: (wrist in the carrier's rest
    space, elbow pole)}, put on the carried person (carried_at), so they meet it. None for a pet (IsoRim draws it)."""
    if CARRY[kind].get('pet'):
        return {}
    C = carried_at(kind, 0.0)
    under = Vector((0, 0, -CARRY_UNDER * LEG))
    if kind == 'bridal':
        knees = sum((C['thigh.' + s] @ head['shin.' + s] for s in ('L', 'R')), Vector()) / 2
        back = C[CHEST_BONE] @ head['spine.003']
        return {'R': (knees + under, Vector((-0.6, 0.6, -0.6))), 'L': (back + under, Vector((0.6, 0.6, -0.6)))}
    if kind == 'cradle' or CARRY[kind].get('rock'):
        # On the baby as the game shrinks it about its hips.
        hips = C[HIPS] @ head[HIPS]
        small = lambda v: hips + (v - hips) * CRADLE_SCALE
        up = C[HIPS].to_3x3() @ Vector((0, -1, 0))   # out of its front
        up.normalize()
        bottom = small(hips - up * CRADLE_UNDER * LEG)
        belly = small(C[SPINE] @ head[SPINE] + up * CRADLE_UNDER * LEG)
        return {'L': (bottom, Vector((0.8, 0.3, -0.6))), 'R': (belly, Vector((-0.8, 0.3, -0.3)))}
    # Round the front of the thighs, halfway down them.
    thighs = sum((C['thigh.' + s] @ ((head['thigh.' + s] + head['shin.' + s]) / 2) for s in ('L', 'R')), Vector()) / 2
    return {'R': (thighs + Vector((0.05, -CARRY_UNDER, 0)) * LEG, Vector((-1.0, 0.3, -0.5)))}

HURT_PLACES = ('head', 'neck', 'chest', 'belly', 'arm')
HURT_BONES = {'head': HEAD, 'neck': NECK, 'chest': CHEST_BONE, 'belly': SPINE}

def hurt_kinds():
    """Every hurt loop's kind: <place>_<side>, the side the wound is on."""
    return ['%s_%s' % (place, side) for place in HURT_PLACES for side in ('L', 'R')]

def hurt_hands(kind):
    """The sides whose hands a hurt loop places (the rest keep the gait's swing): the wound's side, or both for an arm."""
    place, _, side = kind.partition('_')
    return 'LR' if place == 'arm' else side

def hurt_at(t, kind):
    """The hurt loop for kind (see hurt_kinds) at t (0 .. 1 round it): hunched, a hand on the wound, breathing short.
    Returns the pose and the worst overreach."""
    place, _, side = kind.partition('_')
    s = 1 if side == 'L' else -1
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * HURT_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.02, BREATH=HURT_BREATH), 0.29, breath=breath)
    hunch = globals()['HURT_%s_HUNCH' % place.upper()]
    lean = HURT_LEAN * s if place in ('chest', 'belly', 'arm') else 0.0
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (1, 0, 0), hunch * 0.5) @ about(pivot, (0, 1, 0), lean * 0.5))
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -hunch * HURT_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    if place == 'arm':
        other, o = ('R', -1) if side == 'L' else ('L', 1)
        v, turn, hold = Vector(HURT_ARM), Vector(HURT_ARM_TURN), Vector(HURT_ARM_HOLD_TURN)
        clamp = arm_to(side, s, X[CHEST_BONE] @ (head[HIPS] + Vector((s * v.x, v.y, v.z)) * LEG), X,
                       pole=Vector((s * 0.4, 0.6, -1.0)), turn=(turn.x, s * turn.y, s * turn.z))
        fa, hn = 'forearm.' + side, 'hand.' + other
        elbow, wrist = X[fa] @ head[fa], X[fa] @ tail[fa]
        palm = elbow.lerp(wrist, HURT_ARM_GRIP) - X[CHEST_BONE].to_3x3() @ Vector((0, 0, HURT_ARM_UNDER * LEG))
        # The holding wrist back from the palm by half the hand, along where its fingers point.
        R = X[CHEST_BONE].to_quaternion().to_matrix() @ Euler([math.radians(a) for a in (hold.x, s * hold.y, s * hold.z)]).to_matrix()
        grip = palm - R @ (tail[hn] - head[hn]) * 0.5
        return X, max(clamp, arm_to(other, o, grip, X, pole=Vector((o * 1.0, 0.3, -0.6)),
                                    turn=(hold.x, s * hold.y, s * hold.z)))
    bone = HURT_BONES[place]
    v = Vector(globals()['HURT_%s' % place.upper()])
    turn = Vector(globals()['HURT_%s_TURN' % place.upper()])
    wrist = X[bone] @ (head[bone] + Vector((s * v.x, v.y, v.z)) * LEG)
    pole = Vector((s * 1.0, 0.2, -0.4)) if place == 'head' else Vector((s * 0.8, 0.4, -0.8))
    # The turn as given is the left hand's: mirrored for the right (y and z turns the other way).
    return X, arm_to(side, s, wrist, X, pole=pole, turn=(turn.x, s * turn.y, s * turn.z))

def hand_rest_axes(side, sign):
    """A hand bone's fingers' way and its palm's facing at rest (armature space), square to each other."""
    hn = 'hand.' + side
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector((sign * HAND_PALM[0], HAND_PALM[1], HAND_PALM[2]))
    return d0, (n0 - n0.dot(d0) * d0).normalized()

def palm_axes(fingers, facing):
    """The fingers' way and the palm's facing hand_on lays a hand at: the facing as given, the fingers made square to it."""
    n = Vector(facing).normalized()
    f = Vector(fingers)
    return (f - f.dot(n) * n).normalized(), n

def hand_on(side, sign, palm, fingers, facing, X, pole=None):
    """Places one arm by IK so its palm lies at palm (armature space), facing facing, the fingers pointing along fingers
    (made square to facing). Returns how far the wrist was out of reach."""
    hn = 'hand.' + side
    d0, n0 = hand_rest_axes(side, sign)
    f, n = palm_axes(fingers, facing)
    # The turn from rest taking the rest fingers and palm onto these, as arm_to takes it: in the chest's frame.
    R = Matrix((f, n, f.cross(n))).transposed() @ Matrix((d0, n0, d0.cross(n0)))
    turn = (X[CHEST_BONE].to_quaternion().to_matrix().inverted() @ R).to_euler('XYZ')
    wrist = palm - n * HAND_HALF * LEG - f * (tail[hn] - head[hn]).length * 0.5
    return arm_to(side, sign, wrist, X, pole=pole, turn=[math.degrees(a) for a in turn])

def cower_upper(X):
    """Lays the cower over the pose X, in place: the trunk folded, the head kept up and both hands drawn up in front of
    the chest. Returns the worst overreach."""
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (1, 0, 0), CRY_LEAN * 0.5))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), CRY_HEAD * 0.5))
    turn_branch(X, HEAD, about(X[HEAD] @ head[HEAD], (1, 0, 0), CRY_HEAD * 0.5))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    for side, sign in (('L', 1), ('R', -1)):
        sh = 'shoulder.' + side
        turn_branch(X, sh, about(X[sh] @ head[sh], X[CHEST_BONE].to_3x3() @ Vector((0, 1, 0)), -CRY_SHRUG * sign))
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        wrist = X[CHEST_BONE] @ (head[HIPS] + Vector((sign * CRY_HANDS[0], CRY_HANDS[1], CRY_HANDS[2])) * LEG)
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * CRY_ELBOW[0], CRY_ELBOW[1], CRY_ELBOW[2]))))
    return clamp

def cower_at(t):
    """The cowering loop at t (0 .. 1 round it), standing, to be laid over the run: hunched, the hands drawn up in front
    of the chest, breathing quick. Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * CRY_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.02, BREATH=CRY_BREATH), 0.29, breath=breath)
    return X, cower_upper(X)

def hunker_at(t):
    """The cower standing still (see CRY_SECONDS): crouched as well. Returns the pose and the worst
    overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * CRY_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.01, NARROW=HUNKER_NARROW, BREATH=CRY_BREATH), 0.29, breath=breath)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    hip = X[HIPS] @ head[HIPS]
    M = T(Vector((0, HUNKER_BACK, -HUNKER_DROP)) * LEG) @ about(hip, (1, 0, 0), HUNKER_HIPS)
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    X = {k: (v if k in legs else M @ v) for k, v in X.items()}
    clamp = 0.0
    for side in ('L', 'R'):
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], FWD, X))
    return X, max(clamp, cower_upper(X))

def cover_at(t):
    """The crouch behind cover at t (0 .. 1 round it, see COVER_SECONDS): the drafted stance's feet and breathing, the hips
    let down and back over the planted feet, the arms hanging (the stance's are laid over it). Returns the pose and the
    worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    X, _ = posed_with(dict(STILL, DROP=DRAFT_DROP + BREATH_BOB * (1 - breath), LEAN=DRAFT_LEAN, NARROW=DRAFT_NARROW), 0.29,
                      breath=breath)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    hip = X[HIPS] @ head[HIPS]
    M = T(Vector((0, COVER_BACK, -COVER_DROP)) * LEG) @ about(hip, (1, 0, 0), COVER_HIPS)
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    X = {k: (v if k in legs else M @ v) for k, v in X.items()}
    turn_branch(X, SPINE, about(X[SPINE] @ head[SPINE], (1, 0, 0), -COVER_UPRIGHT))
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -(COVER_HIPS - COVER_UPRIGHT) * COVER_LOOK))
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        pole = Vector((sign * COVER_SPLAY, -1.0, 0.0))
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], pole, X))
    return X, clamp

def crouch_legs(X, amount=1.0):
    """Lets the hips of pose X down COVER_DROP and back COVER_BACK (by amount) over its own planted feet, the knees bent
    to reach them, forward and out COVER_SPLAY; the trunk, arms and head ride the hips unturned. The game's crouch behind
    cover (SkinnedFigure.Crouch), laid on whatever the standing pose is, so a stance turned or staggered keeps its legs and
    what it holds points as it does standing. Changes X; returns the worst overreach."""
    hip = X[HIPS] @ head[HIPS]
    facing = X[HIPS].to_3x3() @ Vector((0, -1, 0))
    facing.z = 0.0
    facing = facing.normalized() if facing.length > 1e-6 else Vector((0, -1, 0))
    move = (Vector((0, 0, -COVER_DROP)) - facing * COVER_BACK) * LEG * amount
    feet = {}
    for side in ('L', 'R'):
        ft = 'foot.' + side
        feet[side] = (X[ft] @ head[ft], X[ft].to_quaternion().to_matrix().to_4x4(), X['shin.' + side] @ head['shin.' + side])
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    M = T(move)
    old_hips = {s: X[HIPS] @ head['thigh.' + s] for s in ('L', 'R')}
    for k in list(X):
        if k not in legs:
            X[k] = M @ X[k]
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        ankle, turn, knee = feet[side]
        hip_joint = X[HIPS] @ head['thigh.' + side]
        bend = knee - old_hips[side]
        line = (ankle - hip_joint).normalized()
        bend = bend - bend.dot(line) * line
        bend = bend.normalized() if bend.length > 1e-6 else facing
        pole = bend + (X[HIPS].to_3x3() @ Vector((sign, 0, 0))).normalized() * COVER_SPLAY
        clamp = max(clamp, leg_to(side, hip_joint, ankle, pole, X, turn=turn))
    return clamp

def lead_at(t):
    """The leading loop at t (0 .. 1 round it): standing, the right hand reached a little back holding the rope, to be laid
    over the gait (LEAD_UPPER). Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    wrist = X[CHEST_BONE] @ (head[HIPS] + (Vector(LEAD_HAND) + Vector((0, LEAD_TUG * breath, 0))) * LEG)
    return X, arm_to('R', -1, wrist, X, pole=Vector(LEAD_ELBOW))

def cast_at(t, touch=False):
    """The casting loop at t (0 .. 1 round it): the left hand raised toward the target, palm out (or with touch reached
    out low to touch it), to be laid over the gait (CAST_UPPER). Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    lean = CAST_LEAN[1 if touch else 0]
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), CAST_TWIST * 0.5) @ about(pivot, (1, 0, 0), lean * 0.5))
    # The head turned back to look where the hand points.
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (0, 0, 1), -CAST_TWIST) @ about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -lean * 0.8))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    ua, fa = 'upper_arm.L', 'forearm.L'
    reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
    v = Vector(CAST_TOUCH if touch else CAST_HAND) + Vector((0, -CAST_PUSH * breath, 0))
    chest = X[CHEST_BONE].to_3x3()
    palm = X['shoulder.L'] @ head[ua] + chest @ v * reach
    fingers = chest @ (Vector((0, -0.6, 1.0)) if touch else Vector((0, 0, 1)))
    return X, hand_on('L', 1, palm, fingers, chest @ Vector((0, -1, 0)), X, pole=Vector(CAST_ELBOW))

def tame_at(t, kneel=False):
    """Taming an animal at t (0 .. 1 round the loop; see TAME_HAND): standing, or with kneel knelt by a small one (the
    ground work's kneel, crouch_at, its left hand left on the ground), the right hand held out palm up, coaxing. The whole
    figure, in place of the idle. Returns the pose and the worst overreach."""
    if kneel:
        X, clamp = crouch_at(t)
    else:
        breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
        X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
        clamp = 0.0
    hands = held_hands(X) if kneel else None
    lean, look = TAME_LEAN[1 if kneel else 0], TAME_LOOK[1 if kneel else 0]
    if lean:
        for bone in (SPINE, CHEST_BONE):
            pivot = X[bone] @ head[bone]
            turn_branch(X, bone, about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), lean * 0.5))
    if look:
        turn_branch(X, NECK, about(X[NECK] @ head[NECK], (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), look))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    if hands is not None and lean:
        clamp = max(clamp, keep_hands(X, hands))   # (the left kept on the ground; the right is placed below)
    # A level frame turned as the chest is about the vertical.
    side = X[CHEST_BONE].to_3x3() @ Vector((1, 0, 0))
    side = Vector((side.x, side.y, 0.0)).normalized()
    level = Matrix((side, Vector((0, 0, 1)).cross(side), Vector((0, 0, 1)))).transposed()
    ua, fa, hn = 'upper_arm.R', 'forearm.R', 'hand.R'
    reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
    push = 0.5 - 0.5 * math.cos(2 * math.pi * t)   # out at the middle, back at the ends
    v = Vector(TAME_KNEEL_HAND if kneel else TAME_HAND) + Vector((0, -TAME_PUSH * push, 0))
    wrist = X['shoulder.R'] @ head[ua] + level @ v * reach
    # The palm up, the fingers forward and a little down, the wrist at its place (hand_on places the palm).
    tip = math.radians(TAME_TIP)
    fingers = level @ Vector((0, -math.cos(tip), -math.sin(tip)))
    facing = level @ Vector((0, -math.sin(tip), math.cos(tip)))
    palm = wrist + facing * HAND_HALF * LEG + fingers * (tail[hn] - head[hn]).length * 0.5
    clamp = max(clamp, hand_on('R', -1, palm, fingers, facing, X, pole=Vector(TAME_ELBOW)))
    coax = 0.5 - 0.5 * math.cos(2 * math.pi * TAME_COAX * t)
    close_hand(X, 'R', -1, TAME_CURL[0] + (TAME_CURL[1] - TAME_CURL[0]) * coax)
    return X, clamp

def tame_kneel_at(t):
    return tame_at(t, kneel=True)

def teach_at(t):
    """Teaching at t (0 .. 1 round the loop; see TEACH_KEYS): standing, explaining with the hands, to be laid over the idle
    or the sit from the waist up (TEACH_UPPER). Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * TEACH_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    k = {n: loop_key(TEACH_AT, line, t) for n, line in TEACH_KEYS.items()}
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), k['twist'] * 0.5)
                    @ about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), k['lean'] * 0.5))
    nod = k['bow'] + TEACH_NOD * (0.5 - 0.5 * math.cos(2 * math.pi * TEACH_NODS * t))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), nod))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    # A level frame turned as the chest is about the vertical.
    side = X[CHEST_BONE].to_3x3() @ Vector((1, 0, 0))
    side = Vector((side.x, side.y, 0.0)).normalized()
    level = Matrix((side, Vector((0, 0, 1)).cross(side), Vector((0, 0, 1)))).transposed()
    clamp = 0.0
    for s, sign, elbow in (('R', -1, TEACH_ELBOW_R), ('L', 1, TEACH_ELBOW_L)):
        ua, hn = 'upper_arm.' + s, 'hand.' + s
        wrist = X['shoulder.' + s] @ head[ua] + level @ k['wrist_' + s] * wrist_reach(s)
        facing = (level @ k['palm_' + s]).normalized()
        fingers = level @ k['fingers_' + s]
        fingers = (fingers - fingers.dot(facing) * facing).normalized()
        palm = wrist + facing * HAND_HALF * LEG + fingers * (tail[hn] - head[hn]).length * 0.5
        clamp = max(clamp, hand_on(s, sign, palm, fingers, facing, X, pole=Vector(elbow)))
        close_hand(X, s, sign, max(0.0, k['curl_' + s]))
    return X, clamp

def level_frame(X):
    """A level frame turned as the chest is about the vertical (x the chest's left, y its back, z up)."""
    side = X[CHEST_BONE].to_3x3() @ Vector((1, 0, 0))
    side = Vector((side.x, side.y, 0.0)).normalized()
    return Matrix((side, Vector((0, 0, 1)).cross(side), Vector((0, 0, 1)))).transposed()

def wave_at(t):
    """A wave in passing at t (0 .. 1 once through; see WAVE_KEYS): standing, the right hand up beside the head waved and
    let fall, to be laid over anything on that arm only (WAVE_UPPER). Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    k = {n: loop_key(WAVE_AT, line, t) for n, line in WAVE_KEYS.items()}
    level = level_frame(X)
    hn = 'hand.R'
    wrist = X['shoulder.R'] @ head['upper_arm.R'] + level @ k['wrist'] * wrist_reach('R')
    facing = (level @ k['palm']).normalized()
    fingers = level @ k['fingers']
    fingers = (fingers - fingers.dot(facing) * facing).normalized()
    palm = wrist + facing * HAND_HALF * LEG + fingers * (tail[hn] - head[hn]).length * 0.5
    clamp = hand_on('R', -1, palm, fingers, facing, X, pole=Vector(WAVE_ELBOW))
    close_hand(X, 'R', -1, max(0.0, k['curl']))
    return X, clamp

def sad_at(t):
    """Sad at t (0 .. 1 round the loop): slumped, the head hung and the arms limp, sighing once a loop, to be laid over the
    gait from the waist up (SAD_UPPER). Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * SAD_BREATHS * t)
    sigh = max(0.0, math.sin(2 * math.pi * t)) ** 2   # once, in the loop's first half
    X, _ = posed_with(dict(STILL, DROP=0.03), 0.29, breath=max(breath, sigh))
    lean = SAD_LEAN + SAD_SIGH * sigh
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), lean * 0.5))
    for bone in (NECK, HEAD):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), SAD_HEAD * 0.5))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    for side, sign in (('L', 1), ('R', -1)):
        sh = 'shoulder.' + side
        chest = X[CHEST_BONE].to_3x3()
        slump = SAD_SLUMP * (1.0 + 0.5 * sigh)
        turn_branch(X, sh, about(X[sh] @ head[sh], chest @ Vector((1, 0, 0)), slump * 0.6)
                    @ about(X[sh] @ head[sh], chest @ Vector((0, 1, 0)), slump * sign))
    # The arms left hanging as the standing pose has them, the hands half closed.
    for side, sign in (('L', 1), ('R', -1)):
        close_hand(X, side, sign, 0.3)
    return X, 0.0

def drunk_at(t):
    """Drunk at t (0 .. 1 round the loop): the trunk swaying from the waist in a figure of eight, the head lolling after
    it, the upper arms hanging back against it; every turn none at t = 0, the plain stance the game nudges from (see
    DRUNK_SECONDS). Returns the pose and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29)
    once, twice = math.sin(2 * math.pi * t), math.sin(4 * math.pi * t)
    side, lurch, twist = DRUNK_SWAY * once, DRUNK_LURCH * twice, DRUNK_TWIST * once
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), twist * 0.5) @ about(pivot, (0, 1, 0), side * 0.5)
                    @ about(pivot, (1, 0, 0), lurch * 0.5))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    # The head late after the sway (its second harmonic takes its peaks later), so it lolls rather than rides along.
    loll = DRUNK_LOLL * (once - 0.4 * twice)
    for bone in (NECK, HEAD):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 1, 0), loll * 0.5) @ about(pivot, (1, 0, 0), DRUNK_NOD * twice * 0.5))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    for s in ('L', 'R'):
        ua = 'upper_arm.' + s
        pivot = X[ua] @ head[ua]
        turn_branch(X, ua, about(pivot, (0, 1, 0), -side * DRUNK_HANG) @ about(pivot, (1, 0, 0), -lurch * DRUNK_HANG))
    return X, 0.0

STIR_KINDS = {'stir': 'bench', 'stir_crouch': 'crouch', 'stir_campfire': 'fire', 'stir_campfire_sit': 'fire_sit'}   # each stirring clip (work_<name>) and the work it is made on
stir_made = {}   # by stirring clip: the figure it is made on, the pot's middle (at a campfire, the point over the fire the stick is aimed at) and the fist's way round the handle
stir_numbers = {}   # by stirring clip: the numbers it was made with

def stir_work(kind, t):
    """The work a stirring clip (a STIR_KINDS key) is made on, at t round its loop."""
    work = STIR_KINDS[kind]
    return (bench_at(t)[0] if work == 'bench' else fire_base(t) if work == 'fire' else fire_base(t, True) if work == 'fire_sit'
            else crouch_at(t)[0])

# Cooking at a campfire (stir_at's stir_campfire and stir_campfire_sit, on fire_base; the game's work at a campfire the
# whole time): sat cross-legged (work_stir_campfire), the meditation's hips and legs with the trunk leant forward
# FIRE_LEAN from the waist, or on a chair (work_stir_campfire_sit), the sit hunched forward FIRE_SIT_LEAN and laid over
# the sit from the waist up (EAT_UPPER; the game draws the chair where it stands, so nothing brings it nearer). Each holds
# a roasting stick out over the fire (roast_frames; Models/tool_stick.obj, anim_scripts/make_stick.py, a
# `tool stir_campfire tool_stick R` line), meat skewered near its tip, the right fist turning it slowly back and forth so
# it cooks all round and bobbing it a little, the left hand left on the knee. They stirred a pot by the fire before, but
# reached out that far the spoon had to lean back at the figure and the arm stuck out straight. The pawn sits on the
# campfire's interaction cell, its middle a cell from the fire's (cross-legged, the game draws it FireCloser nearer). The
# fist from the right shoulder and the point the stick is aimed at over the fire from the floor under the hips (the sit's
# floor seated), in a level frame turned as the chest is, leg lengths: x to the figure's left, y back, z up.
FIRE_LEAN = 20.0       # degrees the trunk is leant forward from the waist, cross-legged
FIRE_LOOK = 14.0       # degrees the head is tipped back up from the lean, toward the fire
FIRE_SIT_LEAN = 38.0   # degrees the trunk is hunched forward from the waist, on a chair
ROAST_FIST = (0.32, -0.25, -0.20)      # cross-legged: the fist low in front of the chest, the forearm across the stick
ROAST_AIM = (0.0, -1.8, 1.2)           # cross-legged: over the fire's middle, about 0.6 cells off the ground, over the flames
ROAST_SIT_FIST = (0.32, -0.35, -0.10)  # on a chair: in front of the chest, the forearm across the stick
ROAST_SIT_AIM = (0.0, -2.1, 0.96)      # on a chair: over the fire, about 0.6 cells off the ground (MUSIC_SEAT under the sit's floor)
ROAST_LONG = 0.72      # cells from the fist to the stick's tip (make_stick.py's LONG)
ROAST_SWING = 60.0     # degrees the elbow is swung out about the line from the shoulder to the wrist, cross-legged
ROAST_SIT_SWING = 90.0   # the same on a chair
ROAST_THUMB_BACK = True    # the thumb toward the butt rather than the fire: with the elbow out and the forearm across
                           # the body the stick comes out of the little finger's side, so the wrist is near straight
ROAST_TURNS = 2        # times a loop the stick is turned back and forth
ROAST_ROLL = 20.0      # degrees the fist turns the stick each way about its line
ROAST_BOB = 0.025      # leg lengths the fist rises and falls with each turn
ROAST_DRIFT = 0.03     # leg lengths the fist slides along the stick's line and back, once a loop

def fire_base(t, seated=False):
    """Sat leant forward toward a campfire at t (0 .. 1 round its loop): cross-legged, the meditation with the trunk
    leant FIRE_LEAN from the waist, or on a chair (seated), the sit hunched FIRE_SIT_LEAN; the head tipped back up
    FIRE_LOOK, the hands still on the knees or thighs."""
    X = sit_at(t)[0] if seated else meditate_at(t)[0]
    turn_branch(X, SPINE, about(X[HIPS] @ head[SPINE], (1, 0, 0), FIRE_SIT_LEAN if seated else FIRE_LEAN))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), -FIRE_LOOK))
    return X

def fire_floor(X):
    """The point on the floor under the hips in X and the level frame there (fire places' origin and axes)."""
    hips = X[HIPS] @ head[HIPS]
    return Vector((hips.x, hips.y, 0.0)), level_frame(X)

def stir_frames(kind, t, wrap):
    """Stirring at t (0 .. 1 round the loop), the fist turned wrap degrees about the handle from pointing forward:
    the pose, the worst overreach and the spoon's place (the matrix from its model's points, Blender's axes, cells, its
    grip at the origin and its handle's end up +z, to armature space)."""
    if STIR_KINDS[kind] in ('fire', 'fire_sit'):
        return roast_frames(kind, t, wrap)
    base, pot = stir_made[kind][:2]
    X = dict(base)
    level = level_frame(X)
    a = 2 * math.pi * STIR_TURNS * t
    ring = level @ Vector((math.cos(a), math.sin(a), 0.0))
    # The trunk turned after the spoon and leant in over the pot as it goes round the far side.
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), STIR_TWIST * math.cos(a) * 0.5)
                    @ about(pivot, level @ Vector((1, 0, 0)), STIR_LEAN * (0.5 - 0.5 * math.sin(a)) * 0.5))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    reach = STIR_REACH / MODEL_LEG * LEG
    bowl = pot + Vector((0, 0, STIR_DIP * LEG)) + ring * STIR_BOWL * LEG
    d = (pot + Vector((0, 0, STIR_DIP * LEG + reach)) + ring * STIR_FIST * LEG - bowl).normalized()
    fist = bowl + d * reach
    # The thumb up the handle, away from the bowl (grip_handle's d), the fingers round it.
    edge = level @ Vector((0, -1, 0))
    clamp = grip_handle('R', -1, fist, d, edge, wrap, X)
    close_hand(X, 'R', -1, 1.0)
    f = Matrix.Rotation(math.radians(wrap), 3, d) @ edge
    f = (f - f.dot(d) * d).normalized()
    spoon = T(fist) @ Matrix((f, d.cross(f), d)).transposed().to_4x4() @ Matrix.Scale(LEG / MODEL_LEG, 4)
    # The left palm on the rim, steadying the pot.
    palm = pot + level @ Vector(STIR_RIM) * LEG
    clamp = max(clamp, hand_on('L', 1, palm, level @ Vector(STIR_RIM_FINGERS), level @ Vector(STIR_RIM_PALM), X,
                               pole=Vector((1.0, 0.3, -0.6))))
    close_hand(X, 'L', 1, 0.35)
    return X, clamp, spoon

def roast_frames(kind, t, wrap):
    """Holding a roasting stick out over a campfire at t (0 .. 1 round the loop), sat cross-legged or on a chair, the fist
    turned wrap degrees about the stick from fingers down, plus the slow turning back and forth: the pose, the worst
    overreach and the stick's place (as stir_frames gives the spoon's: its grip at the origin, its tip up +z)."""
    base, aim = stir_made[kind][:2]
    X = dict(base)
    level = level_frame(X)
    sit = STIR_KINDS[kind] == 'fire_sit'
    shoulder = X['upper_arm.R'] @ head['upper_arm.R']
    a = 2 * math.pi * ROAST_TURNS * t
    fist = shoulder + level @ Vector(ROAST_SIT_FIST if sit else ROAST_FIST) * LEG
    d = (aim - fist).normalized()
    fist += Vector((0, 0, ROAST_BOB * math.sin(2 * a) * LEG)) + d * ROAST_DRIFT * math.sin(2 * math.pi * t) * LEG
    d = (aim - fist).normalized()
    # The fingers round the stick, turned back and forth about it; the thumb toward the fire (grip_handle's d) or, with
    # ROAST_THUMB_BACK, toward the butt, the stick coming out of the little finger's side.
    hd = -d if ROAST_THUMB_BACK else d
    edge = level @ Vector((0, 0, -1))
    turn = wrap + ROAST_ROLL * math.sin(a)
    clamp = grip_handle('R', -1, fist, hd, edge, turn, X, swing=ROAST_SIT_SWING if sit else ROAST_SWING)
    close_hand(X, 'R', -1, 1.0)
    f = Matrix.Rotation(math.radians(turn), 3, hd) @ edge
    f = (f - f.dot(hd) * hd).normalized()
    stick = T(fist) @ Matrix((f, d.cross(f), d)).transposed().to_4x4() @ Matrix.Scale(LEG / MODEL_LEG, 4)
    return X, clamp, stick   # (the left hand stays on its knee)

def stir_tool(kind):
    """The model a stirring clip holds in its right fist: the spoon, or at a campfire the roasting stick."""
    return 'tool_stick' if STIR_KINDS[kind] in ('fire', 'fire_sit') else 'tool_spoon'

def stir_make(kind):
    """Works out what a stirring clip is made on, once: the figure, the pot's middle (where that work's palms are on
    average over its loop, moved STIR_BACK toward the figure) and the fist's way round the handle, the one with the
    straightest wrist over the loop (as work_holds picks a tool's)."""
    # (remade when any of its numbers has changed, as the preview's sliders change them in place)
    numbers = tuple((k, v) for k, v in sorted(globals().items()) if k.startswith(('STIR_', 'FIRE_', 'ROAST_')))
    if kind in stir_made and stir_numbers.get(kind) == numbers:
        return
    stir_numbers[kind] = numbers
    base = stir_work(kind, STIR_FROM)
    if STIR_KINDS[kind] in ('fire', 'fire_sit'):
        # Sat at a campfire: the point over the fire the roasting stick is aimed at (ROAST_AIM, ROAST_SIT_AIM).
        floor, level = fire_floor(base)
        pot = floor + level @ Vector(ROAST_SIT_AIM if STIR_KINDS[kind] == 'fire_sit' else ROAST_AIM) * LEG
    else:
        clip = BENCH_CLIP if STIR_KINDS[kind] == 'bench' else CROUCH_CLIP
        n = clip['frames']
        total = Vector()
        for i in range(n):
            Y = stir_work(kind, i / n)
            for s in ('L', 'R'):
                total += Y['hand.' + s] @ ((head['hand.' + s] + tail['hand.' + s]) / 2)
        pot = total / (2 * n) + level_frame(base) @ Vector((0, STIR_BACK * LEG, 0))
    stir_made[kind] = (base, pot, 0.0)
    best, best_cost = 0.0, None
    for wrap in range(0, 360, 15):
        cost = 0.0
        for i in range(6):
            X, clamp, _ = stir_frames(kind, i / 6, wrap)
            flex, deviate = wrist_bend(X, 'R', -1)
            cost += abs(flex) + abs(deviate) + 1000 * clamp
        if best_cost is None or cost < best_cost:
            best, best_cost = float(wrap), cost
    stir_made[kind] = (base, pot, best)

def stir_at(t, kind='stir'):
    """Stirring a pot at t (0 .. 1 round the loop; see STIR_SECONDS), made on STIR_KINDS[kind]'s work. Returns the pose
    and the worst overreach."""
    stir_make(kind)
    X, clamp, _ = stir_frames(kind, t, stir_made[kind][2])
    return X, clamp

def stir_rest(kind='stir'):
    """The spoon in the right hand: its place in the hand bone's frame (the hand as posed, inverted, times the spoon's
    place), the same all through the loop, as the fist holds it one way; for the `tool <kind> tool_spoon R` line."""
    stir_make(kind)
    X, _, spoon = stir_frames(kind, 0.0, stir_made[kind][2])
    return X['hand.R'].inverted() @ spoon

def burn_at(t):
    """On fire at t (0 .. 1 round the loop): hunched, both hands patting at the chest and belly in turn, to be laid over
    the run from the waist up (BURN_UPPER). Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * BURN_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.03, BREATH=BURN_BREATH), 0.29, breath=breath)
    twist = BURN_TWIST * math.sin(2 * math.pi * t)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), twist * 0.5) @ about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), BURN_LEAN * 0.5))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), BURN_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    clamp = 0.0
    for side, s, offset in (('R', -1, 0.0), ('L', 1, 0.5)):
        p = BURN_PATS * t + offset
        lift = 0.5 - 0.5 * math.cos(2 * math.pi * p)   # on the body at each whole p
        mix = 0.5 - 0.5 * math.cos(math.pi * p)       # the chest at even p, the belly at odd
        chest_at = X[CHEST_BONE] @ (head[CHEST_BONE] + Vector((s * HURT_CHEST[0], HURT_CHEST[1], HURT_CHEST[2])) * LEG)
        belly_at = X[SPINE] @ (head[SPINE] + Vector((s * HURT_BELLY[0], HURT_BELLY[1], HURT_BELLY[2])) * LEG)
        forward = X[CHEST_BONE].to_3x3() @ Vector((0, -1, 0))
        wrist = chest_at.lerp(belly_at, mix) + forward * BURN_LIFT * LEG * lift
        turn = Vector(HURT_CHEST_TURN).lerp(Vector(HURT_BELLY_TURN), mix)
        clamp = max(clamp, arm_to(side, s, wrist, X, pole=Vector((s * 0.8, 0.4, -0.8)), turn=(turn.x, s * turn.y, s * turn.z)))
    return X, clamp

def reach_at(t):
    """Reaching to a switch or a lid at t (0 .. 1 round the loop): stood, the right hand out in front palm forward,
    pressed in once a loop. The whole figure, in place of the idle. Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * t)
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized(), REACH_LEAN * 0.5))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), REACH_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    level = level_frame(X)
    hn = 'hand.R'
    push = 0.5 - 0.5 * math.cos(2 * math.pi * t)   # in at the middle, back at the ends
    v = Vector(REACH_HAND) + Vector((0, -REACH_PUSH * push, 0))
    wrist = X['shoulder.R'] @ head['upper_arm.R'] + level @ v * wrist_reach('R')
    tip = math.radians(REACH_TIP)
    fingers = level @ Vector((0, -math.sin(tip), math.cos(tip)))
    facing = level @ Vector((0, -math.cos(tip), -math.sin(tip)))
    palm = wrist + facing * HAND_HALF * LEG + fingers * (tail[hn] - head[hn]).length * 0.5
    clamp = hand_on('R', -1, palm, fingers, facing, X, pole=Vector(REACH_ELBOW))
    close_hand(X, 'R', -1, REACH_CURL[1] + (REACH_CURL[0] - REACH_CURL[1]) * push)
    return X, clamp

def cast_touch_at(t):
    return cast_at(t, touch=True)

def rig_of(v):
    """A point or way in the game's model space (IsoRim's figure space) in Blender's axes, as export_frames.GAME turns back."""
    return Vector((-v[0], -v[2], v[1]))

def hip_grip_rest():
    """The hip gear's grip in the left hand at rest (armature space): where the fist closes on the rod, the rod's way
    (toward its tip, out of the thumb's side) and the palm's way, as grip_handle closes a hand round a handle."""
    hn = 'hand.L'
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector(HAND_PALM)
    t0 = -d0.cross(n0)   # the thumb at rest
    t0 = (t0 - t0.dot(d0) * d0).normalized()
    n0 = (n0 - n0.dot(d0) * d0).normalized()
    a = math.radians(HIP_TILT)
    ra = t0 * math.cos(a) + d0 * math.sin(a)
    fist = head[hn] + d0 * WORK_GRIP * LEG + n0 * WORK_PALM * LEG
    return fist, ra, n0

def hip_rest():
    """The hip gear in the left hand: the matrix from its model's points (Blender's axes, model units, as tool_matrix
    takes them) to the rest pose (armature space), which the hand's move takes to any pose. Its rod (the model's y) runs
    through the fist as hip_grip_rest has it, its face (the model's x, away from its clip) out of the back of the hand,
    HIP_GRIP in the fist."""
    size = LEG / MODEL_LEG   # armature units per model unit
    fist, ra, n0 = hip_grip_rest()
    rt = -n0
    R = Matrix((-rt, ra.cross(-rt), ra)).transposed().to_4x4()   # Blender's x, y, z: the face's back, across, the rod
    return T(fist) @ R @ Matrix.Scale(GEAR['hip']['scale'] * size, 4) @ T(-rig_of(HIP_GRIP))

def hip_slot(X):
    """Where the hip slot holds the gear in the pose X: the matrix from its model's points (as hip_rest) to the pose, as
    SkinnedFigure.GearAt places it but without the game's push past what is worn."""
    g = GEAR['hip']
    size = LEG / MODEL_LEG
    R = Euler([math.radians(v) for v in g.get('turn', (0.0, 0.0, 0.0))], 'XYZ').to_matrix().to_4x4()
    return (X[g['bone']] @ T(head[HIPS] + Vector(g['at']) * size) @ R @ Matrix.Scale(g['scale'] * size, 4)
            @ T(-rig_of((0.19, 0.30, 0.0))))   # IsoRim's hip tools have their clip there

def hip_body(lean=0.0, twist=0.0, nod=0.0, breath=0.0):
    """The drafted standing pose the hip gear clips are made on, the trunk turned twist degrees (+ to the left) and leant
    lean forward from the waist, the head turning the twist back and bowed nod further."""
    X, clamp = posed_with(dict(STILL, DROP=DRAFT_DROP, LEAN=DRAFT_LEAN, NARROW=DRAFT_NARROW), 0.29, breath=breath)
    if lean or twist or nod:
        for bone in (SPINE, CHEST_BONE):
            pivot = X[bone] @ head[bone]
            turn_branch(X, bone, about(pivot, (0, 0, 1), twist * 0.5) @ about(pivot, (1, 0, 0), lean * 0.5))
        pivot = X[NECK] @ head[NECK]
        turn_branch(X, NECK, about(pivot, (0, 0, 1), -twist) @ about(pivot, (1, 0, 0), nod))
        for name, leader in FOLLOW.items():
            X[name] = X[leader]
    return X, clamp

def hip_hand(X, fist, d, palm, lift=None):
    """Closes the left hand on the hip gear with its fist at fist, the rod pointing along d and the palm facing palm
    (armature space, made square to d). Returns how far the wrist was out of reach."""
    hn = 'hand.L'
    rest_fist, ra, n0 = hip_grip_rest()
    d = d.normalized()
    p = (palm - palm.dot(d) * d).normalized()
    R = Matrix((d, p, d.cross(p))).transposed() @ Matrix((ra, n0, ra.cross(n0)))
    turn = (X[CHEST_BONE].to_quaternion().to_matrix().inverted() @ R).to_euler('XYZ')
    wrist = fist - R @ (rest_fist - head[hn])
    clamp = arm_to('L', 1, wrist, X, turn=[math.degrees(v) for v in turn], lift=HIP_ELBOW if lift is None else lift)
    close_hand(X, 'L', 1)
    return clamp

def hip_hand_straight(X, fist, lift=None, palm=None, pole=None):
    """Closes the left hand on the hip gear with its fist at fist, the hand lying on the forearm as it does at rest (so
    the wrist is straight, HAND_IN) and turned about the forearm so the palm faces HIP_PALM, or palm (chest's frame): the
    rod goes where the grip (hip_grip_rest) then takes it. Returns how far the wrist was out of reach."""
    hn, fa = 'hand.L', 'forearm.L'
    rest_fist, ra, n0 = hip_grip_rest()
    chest = X[CHEST_BONE].to_quaternion().to_matrix()
    lift = HIP_ELBOW if lift is None else lift
    want = chest @ Vector(HIP_PALM if palm is None else palm)
    R = Matrix.Identity(3)
    for _ in range(12):   # the forearm's way depends on where the wrist goes, which depends on the hand's turn
        wrist = fist - R @ (rest_fist - head[hn])
        Y = dict(X)
        clamp = arm_to('L', 1, wrist, Y, pole=pole, turn=[math.degrees(v) for v in (chest.inverted() @ R).to_euler('XYZ')], lift=lift)
        F = Y[fa].to_3x3().normalized()
        a = (F @ (tail[fa] - head[fa])).normalized()
        F = F @ rot((0, 1, 0), HAND_IN).to_3x3()   # the hand's own bend on the forearm, as arm_to hangs it
        p = F @ n0
        p, w = (p - p.dot(a) * a).normalized(), (want - want.dot(a) * a).normalized()
        R = Matrix.Rotation(math.atan2(p.cross(w).dot(a), p.dot(w)), 3, a) @ F
    wrist = fist - R @ (rest_fist - head[hn])
    clamp = arm_to('L', 1, wrist, X, pole=pole, turn=[math.degrees(v) for v in (chest.inverted() @ R).to_euler('XYZ')], lift=lift)
    close_hand(X, 'L', 1)
    return clamp

def hip_hold_at(t):
    """The hip gear held ready at t (0 .. 1 round the loop): the left fist low in front, palm in, the hand straight on the
    forearm and the rod up and forward out of the thumb's side, rising and falling with the breath. Laid over the rest with the left arm only (HIP_UPPER). Returns the pose and the
    worst overreach."""
    X, clamp = hip_body(breath=0.5 - 0.5 * math.cos(2 * math.pi * t))
    chest = X[CHEST_BONE]
    fist = chest @ (head[HIPS] + Vector(HIP_HOLD) * LEG)
    return X, max(clamp, hip_hand_straight(X, fist))

def hip_aim_at(t, fired=False):
    """The hip gear pointed at the target, a frame at t (see aim_pitch), with fired the push as it goes off: the left arm
    out from the shoulder below the aim with the palm in, the trunk turned to bring that shoulder forward and bent with the pitch, the head
    looking along it. Laid over the rest with the trunk and left arm (HIP_AIM_UPPER). Returns the pose and the worst
    overreach."""
    pitch = aim_pitch(t)
    lean = HIP_LEAN - pitch * AIM_BEND
    X, clamp = hip_body(lean=lean, twist=HIP_TWIST, nod=-pitch * AIM_LOOK - lean)
    d = rot((1, 0, 0), -pitch).to_3x3() @ FWD
    ua, fa = 'upper_arm.L', 'forearm.L'
    reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
    shoulder = X['shoulder.L'] @ head[ua]
    palm = X[CHEST_BONE].to_3x3() @ Vector(HIP_PALM)
    # The rod comes out of the fist well above the forearm's line, so with the palm in the arm points below the aim, as
    # a torch is pointed from low down: the line from the shoulder to the fist is let down from the aim by whatever
    # leaves the wrist straightest (as it hangs, HAND_IN), kick and all taken from there.
    down = Vector((0, 0, -1))
    down = (down - down.dot(d) * d).normalized()
    straight = wrist_bend(hip_body()[0], 'L', 1)
    best = None
    for step in range(0, 15):
        a = math.radians(step * 5.0)
        line = d * math.cos(a) + down * math.sin(a)
        Y = dict(X)
        over = hip_hand(Y, shoulder + line * HIP_REACH * reach + Vector((-HIP_ACROSS * LEG, 0, 0)), d, palm)
        flex, dev = wrist_bend(Y, 'L', 1)
        cost = math.hypot(flex - straight[0], dev - straight[1]) + over / LEG * 1000.0
        if best is None or cost < best[0]:
            best = (cost, line)
    fist = shoulder + best[1] * HIP_REACH * reach + Vector((-HIP_ACROSS * LEG, 0, 0))
    if fired:
        fist = fist + d * HIP_KICK * LEG
        d = Matrix.Rotation(math.radians(HIP_RISE), 3, d.cross(Vector((0, 0, 1)))) @ d
    return X, max(clamp, hip_hand(X, fist, d, palm))

def hip_fire_at(t):
    return hip_aim_at(t, fired=True)

def shield_at(t):
    """The shield guard at t (0 .. 1 round the loop): the drafted stance with the left fist out in front of the left
    shoulder (SHIELD_FIST), palm in and the hand straight on the forearm, rising and falling with the breath. Laid over the
    rest with the left arm only (SHIELD_UPPER). Returns the pose and the worst overreach."""
    X, clamp = hip_body(breath=0.5 - 0.5 * math.cos(2 * math.pi * t))
    fist = X[CHEST_BONE] @ (head[HIPS] + Vector(SHIELD_FIST) * LEG)
    return X, max(clamp, hip_hand_straight(X, fist, lift=SHIELD_ELBOW, palm=SHIELD_PALM))

def shield_rest():
    """A shield in the left fist: the matrix from its own frame (armature units, the grip's middle at the origin, x to
    the figure's left, y back, z up, its face toward -y; as make_shields.py builds it) to the rest pose (armature space),
    which hand.L's move takes to any pose; and the grip's way in that frame (toward the thumb), along which the fist
    closes round it. Placed from the guard's first frame, upright and turned SHIELD_YAW out to the left, tipped back
    SHIELD_TIP."""
    X, _ = shield_at(0.0)
    hn = 'hand.L'
    rest_fist, ra, _ = hip_grip_rest()
    R = rot((0, 0, 1), SHIELD_YAW) @ rot((1, 0, 0), -SHIELD_TIP)
    M = X[hn].inverted() @ T(X[hn] @ rest_fist) @ R
    grip = (R.to_3x3().inverted() @ (X[hn].to_3x3() @ ra)).normalized()
    return M, grip

def shield_rim(places):
    """Where the left hand takes a shield on the back (armature rest space): its left edge (the figure's left, +x) at
    the middle of its height, from the shield's rest places (armature space, rows of three)."""
    zs = [p[2] for p in places]
    lo, hi = min(zs), max(zs)
    band = [Vector(p) for p in places if abs(p[2] - (lo + hi) / 2) <= (hi - lo) / 6] or [Vector(p) for p in places]
    edge = max(p.x for p in band)
    near = [p for p in band if p.x >= edge - 0.02 * (hi - lo)]
    return sum(near, Vector()) / len(near)

shield_draw_cache = {}

def shield_draw_keys(rim):
    """shield_draw's working for a shield whose edge is at rim (shield_rim): the left wrist (as hip_draw_keys': leg lengths
    from the middle of the hips, in the chest's frame) and the hand's turn (chest's frame) hanging ('hang'), closed on the
    edge ('grab', with the trunk turned 'twist' and leant 'lean', the least that brings it in reach) and in the guard
    ('end'); 'come', where it comes in from beside the edge; 'gap', the leg lengths it was left out of reach."""
    rim = Vector(rim)
    cache = (tuple(round(v, 5) for v in rim), SHIELD_FIST, SHIELD_PALM, SHIELD_ELBOW, SHIELD_RIM_PALM, SHIELD_APPROACH,
             SHIELD_DRAW_TWIST, SHIELD_DRAW_LEAN, SHIELD_DRAW_POLE, DRAW_BEND_COST, DRAFT_DROP, DRAFT_LEAN, DRAFT_NARROW)
    if cache in shield_draw_cache:
        return shield_draw_cache[cache]
    hn, pack = 'hand.L', GEAR['pack']['bone']
    def held(X):
        return (X[CHEST_BONE].inverted() @ (X[hn] @ head[hn]) - head[HIPS]) / LEG, \
            X[CHEST_BONE].to_quaternion().inverted() @ X[hn].to_quaternion()
    keys = {}
    keys['hang'], keys['hang_turn'] = held(hip_body()[0])
    keys['end'], keys['end_turn'] = held(shield_at(0.0)[0])
    best = None
    for twist in range(0, int(SHIELD_DRAW_TWIST) + 1, 5):
        for lean in (0.0, SHIELD_DRAW_LEAN * 0.5, SHIELD_DRAW_LEAN):
            X, _ = hip_body(lean=lean, twist=twist)
            Y = dict(X)
            over = hip_hand_straight(Y, X[pack] @ rim, lift=0.0, palm=SHIELD_RIM_PALM, pole=Vector(SHIELD_DRAW_POLE))
            cost = (twist * twist + lean * lean) * DRAW_BEND_COST + (over / LEG) ** 2 * 1000.0
            if best is None or cost < best[0]:
                best = (cost, twist, lean, Y, over)
    _, keys['twist'], keys['lean'], Y, over = best
    keys['grab'], keys['grab_turn'] = held(Y)
    keys['come'] = keys['grab'] + Vector((SHIELD_APPROACH, -SHIELD_APPROACH, 0))
    keys['gap'] = over / LEG
    shield_draw_cache[cache] = keys
    return keys

def shield_draw_at(t, rim):
    """shield_draw at t for a shield whose edge is at rim (frames at t = 0, 1/n .. (n-1)/n as export_loop takes them,
    the last the guard's first frame): the left hand from hanging round the left side to the shield's edge by
    SHIELD_GRAB, the trunk turning to meet it, then round again into the guard. The game plays it backwards to put the
    shield back. Returns the pose and the worst overreach."""
    g = shield_draw_keys(rim)
    hang = Vector((ELBOW_OUT, 0.25, -1.0))   # arm_to's own pole
    t = min(1.0, t * SHIELD_DRAW_FRAMES / (SHIELD_DRAW_FRAMES - 1))
    if t <= SHIELD_GRAB:
        u = smooth(t / SHIELD_GRAB)
        share = u
        place = round_body(g['hang'], g['come'], g['grab'], u, 'L')
        q0, q1, v = g['hang_turn'], g['grab_turn'], min(1.0, u / 0.8)
        fist = u * u
        lift = 0.0
        pole = hang.lerp(Vector(SHIELD_DRAW_POLE), u)
    else:
        u = smooth((t - SHIELD_GRAB) / (1.0 - SHIELD_GRAB))
        share = 1.0 - u
        place = round_body(g['grab'], g['come'], g['end'], u, 'L')
        q0, q1, v = g['grab_turn'], g['end_turn'], u
        fist = 1.0
        lift = SHIELD_ELBOW * u
        pole = Vector(SHIELD_DRAW_POLE).lerp(hang, u)
    X, clamp = hip_body(lean=g['lean'] * share, twist=g['twist'] * share)
    wrist = X[CHEST_BONE] @ (head[HIPS] + place * LEG)
    if q0.dot(q1) < 0:
        q1 = -q1
    turn = q0.slerp(q1, v).to_euler('XYZ')
    clamp = max(clamp, arm_to('L', 1, wrist, X, pole=pole, turn=[math.degrees(n) for n in turn], lift=lift))
    close_hand(X, 'L', 1, fist)
    return X, clamp

def shield_grip_rest(rim):
    """A shield taken by its edge: the matrix (armature space) from its rest place on the back to its place at rest on
    hand.L, as shield_draw holds it where the hand closes on it, which hand.L's move then takes to any pose."""
    X, _ = shield_draw_at(SHIELD_GRAB * (SHIELD_DRAW_FRAMES - 1) / SHIELD_DRAW_FRAMES, rim)
    return X['hand.L'].inverted() @ X[GEAR['pack']['bone']]

hip_draw_cache = {}

def hip_draw_keys():
    """hip_draw's working: the left wrist (as DRAFTED's: leg lengths from the middle of the hips, in the chest's frame) and
    the hand's turn (in the chest's frame, from rest) hanging ('hang'), closed on the gear in its slot ('grab', with the
    trunk turned as HIP_DRAW_TWIST has it) and holding it ready ('end'); 'come', where it comes in from in front of the
    slot; 'gap', the leg lengths it was left out of reach of the slot."""
    cache = (repr(GEAR['hip']), HIP_TILT, HIP_PALM, HIP_GRIP, HIP_HOLD, HIP_ELBOW, HIP_APPROACH, HIP_DRAW_TWIST,
             HIP_DRAW_LEAN, DRAFT_DROP, DRAFT_LEAN, DRAFT_NARROW)
    if cache in hip_draw_cache:
        return hip_draw_cache[cache]
    hn = 'hand.L'
    def held(X):
        return (X[CHEST_BONE].inverted() @ (X[hn] @ head[hn]) - head[HIPS]) / LEG, \
            X[CHEST_BONE].to_quaternion().inverted() @ X[hn].to_quaternion()
    hanging, _ = hip_body()
    keys = {}
    keys['hang'], keys['hang_turn'] = held(hanging)
    keys['end'], keys['end_turn'] = held(hip_hold_at(0.0)[0])
    X, _ = hip_body(lean=HIP_DRAW_LEAN, twist=HIP_DRAW_TWIST)
    # The fist closes on the gear's grip where it sits, along its rod, turned about the rod whichever way leaves the
    # wrist straightest and the slot in reach; the game turns the gear into the hold over HIP_SLIDE.
    G = hip_slot(X)
    fist = G @ rig_of(HIP_GRIP)
    d = (G.to_3x3() @ Vector((0, 0, 1))).normalized()   # the rod, Blender's z (the model's y)
    side = d.cross(Vector((0, 0, 1)))
    side = side.normalized() if side.length > 1e-6 else Vector((1, 0, 0))
    best = None
    for step in range(24):
        Y = dict(X)
        over = hip_hand(Y, fist, d, Matrix.Rotation(math.radians(step * 15.0), 3, d) @ side, lift=0.0)
        cost = math.hypot(*wrist_bend(Y, 'L', 1)) + over / LEG * 2000.0
        if best is None or cost < best[0]:
            best = (cost, Y)
    wrist = best[1][hn] @ head[hn]
    R = best[1][hn].to_3x3().normalized()
    keys['grab'] = (X[CHEST_BONE].inverted() @ wrist - head[HIPS]) / LEG
    keys['grab_turn'] = X[CHEST_BONE].to_quaternion().inverted() @ R.to_quaternion()
    keys['come'] = keys['grab'] + Vector((0, -HIP_APPROACH, 0))
    Y = dict(X)
    keys['gap'] = arm_to('L', 1, wrist, Y) / LEG
    hip_draw_cache[cache] = keys
    return keys

def hip_draw_at(t):
    """hip_draw at t (frames at t = 0, 1/n .. (n-1)/n as export_loop takes them, the last holding it ready): the left hand
    from hanging across to the slot on the right shoulder by HIP_GRAB, the trunk turning to meet it, then back with the
    gear to the hold. The game plays it backwards to put the gear away. Returns the pose and the worst overreach."""
    g = hip_draw_keys()
    t = min(1.0, t * HIP_DRAW_FRAMES / (HIP_DRAW_FRAMES - 1))
    if t <= HIP_GRAB:
        u = smooth(t / HIP_GRAB)
        share = u
        place = round_body(g['hang'], g['come'], g['grab'], u, 'L')
        q0, q1, v = g['hang_turn'], g['grab_turn'], min(1.0, u / 0.8)
        fist = u * u
        lift = 0.0
    else:
        u = smooth((t - HIP_GRAB) / (1.0 - HIP_GRAB))
        share = 1.0 - u
        place = round_body(g['grab'], g['come'], g['end'], u, 'L')
        q0, q1, v = g['grab_turn'], g['end_turn'], u
        fist = 1.0
        lift = HIP_ELBOW * u
    X, clamp = hip_body(lean=HIP_DRAW_LEAN * share, twist=HIP_DRAW_TWIST * share)
    wrist = X[CHEST_BONE] @ (head[HIPS] + place * LEG)
    if q0.dot(q1) < 0:
        q1 = -q1
    turn = q0.slerp(q1, v).to_euler('XYZ')
    clamp = max(clamp, arm_to('L', 1, wrist, X, turn=[math.degrees(n) for n in turn], lift=lift))
    close_hand(X, 'L', 1, fist)
    return X, clamp

FLINCH_WAYS = {'F': ((1, 0, 0), 1.0), 'B': ((1, 0, 0), -1.0), 'L': ((0, 1, 0), 1.0), 'R': ((0, 1, 0), -1.0)}

def flinch_at(t, way):
    """The flinch at t (0 the hit .. 1 over) for the figure pushed way (a FLINCH_WAYS key); its first frame is the plain
    stance, which the game takes each bone's nudge from. Returns the pose and the worst overreach."""
    if t < FLINCH_AT:
        u = smooth(t / FLINCH_AT)
    else:
        u = smooth(1 - (t - FLINCH_AT) / (1 - FLINCH_AT))
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29)
    axis, sign = FLINCH_WAYS[way]
    lean = FLINCH_LEAN * sign * u
    for bone in (SPINE, CHEST_BONE):
        turn_branch(X, bone, about(X[bone] @ head[bone], axis, lean * 0.5))
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], axis, -lean * FLINCH_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    for side, sign in (('L', 1), ('R', -1)):
        sh, ua, fa = 'shoulder.' + side, 'upper_arm.' + side, 'forearm.' + side
        chest = X[CHEST_BONE].to_3x3()
        turn_branch(X, sh, about(X[sh] @ head[sh], chest @ Vector((0, 1, 0)), -FLINCH_SHRUG * sign * u))
        turn_branch(X, ua, about(X[sh] @ head[ua], chest @ Vector((1, 0, 0)), -FLINCH_ARMS * u))
        turn_branch(X, fa, about(X[ua] @ head[fa], chest @ Vector((1, 0, 0)), -FLINCH_FOREARMS * u))
    return X, 0.0

def reel_key(name, t):
    """The REEL_KEYS line name at t through the stumble (0 .. 1), joined smoothly through its keys (Catmull-Rom)."""
    keys, at = REEL_KEYS[name], REEL_AT
    t = min(max(t, 0.0), 1.0)
    i = min(sum(1 for a in at[1:] if a <= t), len(at) - 2)
    u = (t - at[i]) / (at[i + 1] - at[i])
    def part(k):
        p0, p1, p2, p3 = (k[max(i - 1, 0)], k[i], k[i + 1], k[min(i + 2, len(k) - 1)])
        return 0.5 * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (3 * p1 - p0 - 3 * p2 + p3) * u ** 3)
    if isinstance(keys[0], tuple):
        return Vector([part([k[n] for k in keys]) for n in range(len(keys[0]))])
    return part(keys)

def reel_at(t):
    """The stumble to one knee at t (0 standing .. 1 standing again; see REEL_KEYS), the knee REEL_KNEE's. Returns the
    pose and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.0, LEAN=0.0, NARROW=HUNKER_NARROW), 0.29)
    floor = min(head['toe.L'].z, head['toe.R'].z)
    hip = X[HIPS] @ head[HIPS]
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    hips = reel_key('hips', t)
    hip_z = head['thigh.L'].z - head[HIPS].z
    to = Vector((head[HIPS].x, head[HIPS].y + hips[0] * LEG, floor + hips[1] * LEG - hip_z))
    pitch = reel_key('pitch', t)
    M = T(to - hip) @ about(hip, (1, 0, 0), pitch)
    X = {k: (v if k in legs else M @ v) for k, v in X.items()}
    look = reel_key('look', t)
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), -look * 0.5))
    turn_branch(X, HEAD, about(X[HEAD] @ head[HEAD], (1, 0, 0), -look * 0.5))
    knee = REEL_KNEE
    front_side = 'L' if knee == 'R' else 'R'
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        if side == knee:
            a, toes, pole = reel_key('back', t), reel_key('toes', t), FWD + Vector((0, 0, -1.2))
        else:
            a, toes, pole = reel_key('front', t), 0.0, FWD + Vector((sign * 0.15, 0, 0.6))
        ankle = Vector((head[HIPS].x + sign * a[0] * LEG, head['foot.' + side].y + a[1] * LEG, floor + a[2] * LEG))
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, pole, X, pitch=toes))
        if side == knee and toes > 0:
            # On its toes: the toes bent back flat along the ground at the ball, not stood up with the foot.
            tj = X['foot.' + side] @ head['toe.' + side]
            X['toe.' + side] = place(tj, Matrix.Identity(4), head['toe.' + side])
    brace = reel_key('brace', t)
    front_knee = X['shin.' + front_side] @ head['shin.' + front_side]
    for side, sign in (('L', 1), ('R', -1)):
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        w = reel_key('arm', t) if side == front_side else reel_key('out', t)
        wrist = X['shoulder.' + side] @ head[ua] + X[CHEST_BONE].to_3x3() @ Vector((sign * w[0], w[1], w[2])) * reach
        if side == front_side and brace > 0:
            on = front_knee + Vector((sign * REEL_BRACE[0], REEL_BRACE[1], REEL_BRACE[2])) * LEG
            wrist = wrist.lerp(on, min(brace, 1.0))
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * DIVE_ELBOW[0], DIVE_ELBOW[1], DIVE_ELBOW[2]))))
        close_hand(X, side, sign, 0.35)
    return X, clamp

def toss_key(line, t, at=None):
    """The TOSS_KEYS line at t through the toss (0 .. 1), joined smoothly through its keys (Catmull-Rom, as reel_key);
    at the keys' places (TOSS_AT by default)."""
    at = at or TOSS_AT
    t = min(max(t, 0.0), 1.0)
    i = min(sum(1 for a in at[1:] if a <= t), len(at) - 2)
    u = (t - at[i]) / (at[i + 1] - at[i])
    def part(k):
        p0, p1, p2, p3 = (k[max(i - 1, 0)], k[i], k[i + 1], k[min(i + 2, len(k) - 1)])
        return 0.5 * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (3 * p1 - p0 - 3 * p2 + p3) * u ** 3)
    if isinstance(line[0], (tuple, Vector)):
        return Vector([part([k[n] for k in line]) for n in range(len(line[0]))])
    return part(line)

def snowball_at(t):
    """The overhand snowball throw at t (0 standing .. let go at SNOW_RELEASE .. 1 standing; see SNOW_KEYS)."""
    return toss_at(t, SNOW_KEYS, SNOW_AT, SNOW_LOOK)

def kiss_at(t):
    """Kissing at t (0 .. 1 round the loop; see KISS_LEAN): leant in, the head tilted, the hands at the partner's waist,
    to be laid over the idle from the waist up (KISS_UPPER). Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * KISS_BREATHS * t)
    X, _ = posed_with(dict(STILL, DROP=0.02), 0.29, breath=breath)
    sway = KISS_SWAY * math.sin(2 * math.pi * t)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        across = (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized()
        turn_branch(X, bone, about(pivot, across.cross(Vector((0, 0, 1))), sway * 0.5) @ about(pivot, across, KISS_LEAN * 0.5))
    pivot = X[NECK] @ head[NECK]
    # (forward: the bone's own y runs along it, up the neck)
    fwd = (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized().cross(Vector((0, 0, 1)))
    # (most of the lean taken back off the head, so it looks at the partner rather than down, then tilted and tipped)
    turn_branch(X, NECK, about(pivot, fwd, -KISS_TILT - sway)
                @ about(pivot, (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), KISS_CHIN - KISS_LEAN * 0.6))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    side = X[CHEST_BONE].to_3x3() @ Vector((1, 0, 0))
    side = Vector((side.x, side.y, 0.0)).normalized()
    level = Matrix((side, Vector((0, 0, 1)).cross(side), Vector((0, 0, 1)))).transposed()
    clamp = 0.0
    for s, sign in (('R', -1), ('L', 1)):
        m = lambda v: Vector((sign * v[0], v[1], v[2]))
        hn = 'hand.' + s
        wrist = X['shoulder.' + s] @ head['upper_arm.' + s] + level @ m(KISS_WRIST) * wrist_reach(s)
        facing = (level @ m(KISS_PALM)).normalized()
        fingers = level @ m(KISS_FINGERS)
        fingers = (fingers - fingers.dot(facing) * facing).normalized()
        palm = wrist + facing * HAND_HALF * LEG + fingers * (tail[hn] - head[hn]).length * 0.5
        clamp = max(clamp, hand_on(s, sign, palm, fingers, facing, X, pole=m(KISS_ELBOW)))
        close_hand(X, s, sign, KISS_CURL)
    return X, clamp

def toss_at(t, keys=None, at=None, look=None):
    """The underhand toss at t (0 standing .. let go at TOSS_RELEASE .. 1 standing; see TOSS_KEYS), from the idle's first
    frame; or another throw by its own keys, their places and its look (snowball_at). Returns the pose and the worst
    overreach."""
    keys, at, look = keys or TOSS_KEYS, at or TOSS_AT, TOSS_LOOK if look is None else look
    X, _ = stand_at(0.0)
    hanging = {}
    for side, sign in (('L', 1), ('R', -1)):
        # Where the standing pose hangs each wrist, as a key: from its shoulder in the chest's frame, shares of the reach.
        ua, fa, hn = 'upper_arm.' + side, 'forearm.' + side, 'hand.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        v = X[CHEST_BONE].to_3x3().inverted() @ (X[hn] @ head[hn] - X['shoulder.' + side] @ head[ua]) / reach
        hanging[side] = Vector((v.x * sign, v.y, v.z))
    k = {n: toss_key(line, t, at) for n, line in keys.items() if n not in ('arm', 'other')}
    for n, side in (('arm', 'R'), ('other', 'L')):
        k[n] = toss_key([hanging[side] if v is None else Vector(v) for v in keys[n]], t, at)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    toes = {s: X['foot.' + s] @ head['toe.' + s] for s in ('L', 'R')}
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    M = T(Vector((0, -k['lunge'], -k['drop'])) * LEG)
    X = {n: (v if n in legs else M @ v) for n, v in X.items()}
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), k['twist'] * 0.5) @ about(pivot, (1, 0, 0), k['lean'] * 0.5))
    pivot = X[NECK] @ head[NECK]
    turn_branch(X, NECK, about(pivot, (1, 0, 0), -k['lean'] * look) @ about(pivot, (0, 0, 1), -k['twist'] * look))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        ankle, pitch = ankles[side].copy(), 0.0
        if side == 'L':
            ankle += Vector((0, -k['step'], k['lift'])) * LEG
        elif k['heel'] > 0:
            # The heel up about the toe joint, as the walk's push off, so the toes stay on the ground.
            pitch = k['heel']
            ankle = toes[side] + rot((1, 0, 0), pitch).to_3x3() @ (ankles[side] - toes[side])
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, FWD + Vector((sign * 0.15, 0, 0)), X, pitch=pitch))
        if side == 'R' and pitch > 0:
            X['toe.R'] = place(X['foot.R'] @ head['toe.R'], Matrix.Identity(4), head['toe.R'])   # the toes flat
    chest = X[CHEST_BONE].to_3x3()
    for side, sign, n in (('R', -1, 'arm'), ('L', 1, 'other')):
        ua, fa, hn = 'upper_arm.' + side, 'forearm.' + side, 'hand.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        w = k[n]
        wrist = X['shoulder.' + side] @ head[ua] + chest @ Vector((sign * w.x, w.y, w.z)) * reach
        if side == 'L':
            clamp = max(clamp, arm_to(side, sign, wrist, X))
            continue
        facing = chest @ Vector((sign * k['palm'].x, k['palm'].y, k['palm'].z))
        fingers = chest @ Vector((sign * k['fingers'].x, k['fingers'].y, k['fingers'].z))
        n_ = facing.normalized()
        f = (fingers - fingers.dot(n_) * n_).normalized()
        palm = wrist + n_ * HAND_HALF * LEG + f * (tail[hn] - head[hn]).length * 0.5   # so hand_on puts the wrist there
        clamp = max(clamp, hand_on(side, sign, palm, fingers, facing, X))
        close_hand(X, side, sign, max(0.0, k['curl']))
    return X, clamp

def dress_frames(kind):
    """How many frames the dress_<kind> clip has (DRESS_FPS of its own length)."""
    return max(8, int(round(DRESS[kind]['seconds'] * DRESS_FPS)))

def dress_at(t, kind):
    """Putting on apparel of this DRESS kind at t (0 standing .. 1 standing; see DRESS), from the idle's first frame.
    Returns the pose and the worst overreach."""
    d = DRESS[kind]
    at, K = d['at'], d['keys']
    m = lambda v, sign: Vector((sign * v[0], v[1], v[2]))
    def k(name, default):
        return toss_key(K[name], t, at) if name in K else default
    X, _ = stand_at(0.0)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    toes = {s: X['foot.' + s] @ head['toe.' + s] for s in ('L', 'R')}
    # Where the standing pose hangs each wrist from its shoulder joint, for a hand key of None.
    hang = {s: X['hand.' + s] @ head['hand.' + s] - X['shoulder.' + s] @ head['upper_arm.' + s] for s in ('L', 'R')}
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    hips = k('hips', Vector((0, 0, 0)))
    pivot = (X['thigh.L'] @ head['thigh.L'] + X['thigh.R'] @ head['thigh.R']) * 0.5
    M = T(Vector(hips) * LEG) @ about(pivot, (1, 0, 0), k('pitch', 0.0))
    X = {n: (v if n in legs else M @ v) for n, v in X.items()}
    lean, twist = k('lean', 0.0), k('twist', 0.0)
    for bone in (SPINE, CHEST_BONE):
        p = X[bone] @ head[bone]
        R = X[bone].to_3x3()
        turn_branch(X, bone, about(p, (R @ Vector((0, 0, 1))).normalized(), twist * 0.5)
                    @ about(p, (R @ Vector((1, 0, 0))).normalized(), lean * 0.5))
    look, tilt = k('look', 0.0), k('tilt', 0.0)
    for bone in (NECK, HEAD):
        p = X[bone] @ head[bone]
        across = (X[bone].to_3x3() @ Vector((1, 0, 0))).normalized()
        turn_branch(X, bone, about(p, across.cross(Vector((0, 0, 1))), -tilt * 0.5) @ about(p, across, look * 0.5))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        ankle = ankles[side] + m(k('foot_' + side, (0, 0, 0)), sign) * LEG
        lifted = ankle.z - ankles[side].z
        # A foot lifted is pointed down a little, as stepping into something; the knee forward and a little out.
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, FWD + Vector((sign * 0.15, 0, 0)), X,
                                  pitch=-min(lifted / LEG, 0.3) * 60.0))
    def landmark(name, side, sign):
        if name == 'ankle':
            return X['foot.' + side] @ head['foot.' + side]
        if name == 'knee':
            return X['shin.' + side] @ head['shin.' + side]
        if name == 'hip':
            return X['thigh.' + side] @ head['thigh.' + side]
        if name == 'shoulder':
            return X['shoulder.' + side] @ head['upper_arm.' + side]
        if name == 'waist':
            return X[HIPS] @ head[HIPS]
        if name == 'head':
            return X[HEAD] @ head[HEAD]
        raise KeyError(name)
    for side, sign in (('L', 1), ('R', -1)):
        line = K.get(side, K.get('hands'))
        shoulder = landmark('shoulder', side, sign)
        points = [shoulder + hang[side] if key is None else landmark(key[0], side, sign) + m(key[1], sign) * LEG
                  for key in line]
        palm = toss_key(points, t, at)
        facing = m(toss_key(K.get('palm_' + side, K.get('palm')), t, at), sign).normalized()
        fingers = m(toss_key(K.get('fingers_' + side, K.get('fingers')), t, at), sign)
        clamp = max(clamp, hand_on(side, sign, palm, fingers, facing, X))
        close_hand(X, side, sign, max(0.0, toss_key(K.get('curl_' + side, K.get('curl')), t, at)))
    return X, clamp

def loop_key(at, line, t):
    """line (one value or tuple a key, at at's places round a loop, the first at 0) at t round it (0 .. 1), joined
    smoothly through its keys and round from the last to the first (Catmull-Rom, as toss_key, but closed)."""
    n = len(at)
    t %= 1.0
    i = max(j for j in range(n) if at[j] <= t)
    k = (i + 1) % n
    span = (at[k] if k else 1.0) - at[i]
    u = (t - at[i]) / span
    def part(v):
        p0, p1, p2, p3 = v[(i - 1) % n], v[i], v[k], v[(k + 1) % n]
        return 0.5 * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (3 * p1 - p0 - 3 * p2 + p3) * u ** 3)
    if isinstance(line[0], (tuple, Vector)):
        return Vector([part([v[m] for v in line]) for m in range(len(line[0]))])
    return part(line)

def wrist_reach(side):
    """The arm's reach, shoulder joint to wrist at rest (armature units)."""
    ua, fa = 'upper_arm.' + side, 'forearm.' + side
    return (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length

def stomp_at(t):
    """Stomping out a fire at t (0 .. 1 round one beat; see STOMP_KEYS): STOMP_FOOT raised and stamped down ahead at
    STOMP_HIT, from the idle's first frame. The whole figure, in place of the idle. Returns the pose and the worst
    overreach."""
    X, _ = stand_at(0.0)
    foot = STOMP_FOOT
    k = {n: loop_key(STOMP_AT, line, t) for n, line in STOMP_KEYS.items()}
    k['lift'] = max(0.0, k['lift'])   # (the curve dips under the floor after the stamp)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    planted = 1 if foot == 'R' else -1   # the planted foot's side of the middle (+x the left)
    M = T(Vector((planted * k['sway'], -k['lunge'], -k['drop'])) * LEG)
    X = {n: (v if n in legs else M @ v) for n, v in X.items()}
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (1, 0, 0), k['lean'] * 0.5))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), STOMP_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        ankle, pitch, turn = ankles[side].copy(), 0.0, None
        if side == foot:
            ankle += Vector((0, -k['step'], k['lift'])) * LEG
            pitch = k['pitch']
            turn = rot((0, 0, 1), -k['grind'] * sign)   # (toes in: the left foot's turned clockwise from above)
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, FWD + Vector((sign * 0.15, 0, 0)), X,
                                  pitch=pitch, turn=turn))
    chest = X[CHEST_BONE].to_3x3()
    for side, sign in (('L', 1), ('R', -1)):
        w = k['arm' if side == foot else 'other']
        wrist = X['shoulder.' + side] @ head['upper_arm.' + side] + chest @ Vector((sign * w.x, w.y, w.z)) * wrist_reach(side)
        clamp = max(clamp, arm_to(side, sign, wrist, X))
        close_hand(X, side, sign, 0.35)
    return X, clamp

def pat_at(t):
    """Patting out a fire at t (0 .. 1 round two beats; see PAT_WRIST): leant in, the right palm slapped down at PAT_HIT
    and the left half a loop on, from the idle's first frame. The whole figure, in place of the idle. Returns the pose
    and the worst overreach."""
    X, _ = stand_at(0.0)
    hits = [PAT_HIT + n / PAT_HITS for n in range(PAT_HITS)]
    slap = max(0.5 + 0.5 * math.cos(2 * math.pi * PAT_HITS * (t - PAT_HIT)), 0.0)   # 1 at each slap
    twist = -PAT_TWIST * math.cos(2 * math.pi * (t - PAT_HIT))   # toward the right at its slap (+ is to the left)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    M = T(Vector((0, -PAT_LUNGE, -(PAT_DROP + PAT_DIP * slap))) * LEG)
    X = {n: (v if n in legs else M @ v) for n, v in X.items()}
    lean = PAT_LEAN + PAT_BOB * slap
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), twist * 0.5) @ about(pivot, (1, 0, 0), lean * 0.5))
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), PAT_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], FWD + Vector((sign * 0.15, 0, 0)), X))
    # A level frame turned as the chest is about the vertical (as tame_at's).
    across = X[CHEST_BONE].to_3x3() @ Vector((1, 0, 0))
    across = Vector((across.x, across.y, 0.0)).normalized()
    level = Matrix((across, Vector((0, 0, 1)).cross(across), Vector((0, 0, 1)))).transposed()
    for (side, sign), hit in zip((('R', -1), ('L', 1)), hits):
        c = (t - hit) % 1.0
        w = loop_key(PAT_HAND_AT, PAT_WRIST, c)
        tip = math.radians(loop_key(PAT_HAND_AT, PAT_TIP, c))
        wrist = X['shoulder.' + side] @ head['upper_arm.' + side] + level @ Vector((sign * w.x, w.y, w.z)) * wrist_reach(side)
        # The palm down (ahead as it is raised), the fingers forward, tipped by tip, the wrist at its place.
        fingers = level @ Vector((0, -math.cos(tip), -math.sin(tip)))
        facing = level @ Vector((0, math.sin(tip), -math.cos(tip)))
        hn = 'hand.' + side
        palm = wrist + facing * HAND_HALF * LEG + fingers * (tail[hn] - head[hn]).length * 0.5
        clamp = max(clamp, hand_on(side, sign, palm, fingers, facing, X,
                                   pole=Vector((sign * PAT_ELBOW[0], PAT_ELBOW[1], PAT_ELBOW[2]))))
        close_hand(X, side, sign, max(0.0, loop_key(PAT_HAND_AT, PAT_CURL, c)))
    return X, clamp

def music_cells(v):
    """A place in MUSIC's cells (x out to the left, y forward, z up, from the figure's feet, for a grown pawn) in
    armature units."""
    return Vector((v[0], -v[1], v[2])) * (LEG / MODEL_LEG / GAME_SCALE)

def music_body(kind, t, seated, dip=0.0, bob=0.0, roll=0.0, nod=0.0, tilt=0.0):
    """The body at an instrument (see MUSIC): the idle's first frame (or the sit at t, seated) leant toward it by its
    'lean' and bob more, rolled roll (degrees, + toward the left), the head bowed its 'look' from upright (the neck
    turned back against the lean) and nod more and tilted
    tilt (+ toward the left), the hips let down dip (cells) over the planted feet standing. Returns the pose."""
    m = MUSIC[kind]
    i = 1 if seated else 0
    if seated:
        X, _ = sit_at(t)
    else:
        X, _ = stand_at(0.0)
        if dip:
            ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
            legs = set()
            for side in ('L', 'R'):
                legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
            M = T(music_cells((0.0, 0.0, -dip)))
            X = {n: (v if n in legs else M @ v) for n, v in X.items()}
            for side, sign in (('L', 1), ('R', -1)):
                leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], FWD + Vector((sign * 0.15, 0, 0)), X)
    lean = m['lean'][i] + bob
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 1, 0), roll * 0.5) @ about(pivot, (1, 0, 0), lean * 0.5))
    nk = X[NECK] @ head[NECK]
    turn_branch(X, NECK, about(nk, (X[NECK].to_3x3() @ Vector((0, 1, 0))).normalized(), tilt)
                @ about(nk, (X[NECK].to_3x3() @ Vector((1, 0, 0))).normalized(), m['look'][i] - m['lean'][i] + nod))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    return X

def music_hand(X, side, sign, palm, fingers, facing, curl, elbow, reach=None):
    """One hand laid on an instrument: its palm at palm, the fingers along fingers and the palm facing facing (all in
    MUSIC's cells and ways), curled curl (a share of FIST, or four), the elbow toward elbow (chest's frame, x out).
    Given reach (a share of the arm's), a palm further than that from its shoulder is brought back toward it until it
    is that far, so a hand that can't get there plays as far out as it goes with the elbow still a little bent, rather
    than the arm locked straight. Returns how far the wrist was out of reach."""
    way = lambda v: Vector((v[0], -v[1], v[2]))
    at = music_cells(palm)
    if reach is not None:
        shoulder = X['shoulder.' + side] @ head['upper_arm.' + side]
        hn = 'hand.' + side
        most = reach * wrist_reach(side) + HAND_HALF * LEG + (tail[hn] - head[hn]).length * 0.5
        if (at - shoulder).length > most:
            at = shoulder + (at - shoulder).normalized() * most
    clamp = hand_on(side, sign, at, way(fingers), way(facing), X,
                    pole=Vector((sign * elbow[0], elbow[1], elbow[2])))
    close_hand(X, side, sign, curl)
    return clamp

def piano_at(t, seated=False):
    """Playing a keyboard (the piano or the harpsichord) at t (0 .. 1 round the loop; see PIANO_SPAN), stood at it or,
    seated, to be laid over the sit. Returns the pose and the worst overreach."""
    a = music_play('piano', seated)
    w = 2 * math.pi * t
    along = {'L': math.sin((PIANO_RUNS - 1) * w + 1.0), 'R': math.sin(PIANO_RUNS * w)}
    X = music_body('piano', t, seated, roll=-PIANO_SWAY * along['R'])
    tip = math.radians(PIANO_TIP)
    fingers = (0.0, math.cos(tip), -math.sin(tip))
    facing = (0.0, -math.sin(tip), -math.cos(tip))
    clamp = 0.0
    for (side, sign), wander, presses, phase in ((('L', 1), PIANO_WANDER[0], PIANO_PRESSES // 2, 0.3),
                                                 (('R', -1), PIANO_WANDER[1], PIANO_PRESSES, 0.0)):
        x = sign * (PIANO_SPAN + wander * along[side])
        p = 2 * math.pi * (presses * t + phase)
        press = (0.5 + 0.5 * math.cos(p)) ** 3   # 1 as a key goes down, briefly
        lo, hi = PIANO_CURL
        fingers_curl = lo + (hi - lo) * (0.5 + 0.5 * math.cos(p))
        thumb_curl = lo + (hi - lo) * (0.5 - 0.5 * math.cos(p))
        palm = (a.x + x, a.y, a.z - PIANO_PRESS * press)
        clamp = max(clamp, music_hand(X, side, sign, palm, fingers, facing,
                                      (fingers_curl, fingers_curl, thumb_curl, thumb_curl), PIANO_ELBOW))
    return X, clamp

def piano_sit_at(t):
    return piano_at(t, seated=True)

def harp_at(t, seated=False):
    """Playing the harp at t (0 .. 1 round the loop; see HARP_HANDS), stood at it or, seated, to be laid over the sit.
    Returns the pose and the worst overreach."""
    a = music_play('harp', seated)
    w = 2 * math.pi * t
    X = music_body('harp', t, seated, roll=HARP_SWAY * math.sin(w), tilt=-HARP_TILT)
    f = math.radians(HARP_FINGERS)
    clamp = 0.0
    for k, (side, sign) in enumerate((('L', 1), ('R', -1))):
        out, up = HARP_HANDS[k]
        out += HARP_DRIFT[0] * math.sin(w + 2.0 * k)
        up += HARP_DRIFT[1] * math.sin(w + 1.0 + 2.5 * k)
        c = (t * HARP_PLUCKS[k] + 0.37 * k) % 1.0
        lo, hi = HARP_CURL
        if c < HARP_PLUCK:
            u = smooth(c / HARP_PLUCK)
        else:
            u = 1.0 - smooth((c - HARP_PLUCK) / (1.0 - HARP_PLUCK))
        curl = lo + (hi - lo) * u
        palm = (a.x + sign * out, a.y - HARP_DRAW * u, a.z + up)
        fingers = (-sign * math.sin(f), 0.0, math.cos(f))   # up and in toward the middle
        clamp = max(clamp, music_hand(X, side, sign, palm, fingers, (0.0, 1.0, 0.0),
                                      (curl, curl, curl * 0.6, curl * 0.6), HARP_ELBOW,
                                      reach=HARP_SIT_REACH if seated else None))
    return X, clamp

def harp_sit_at(t):
    return harp_at(t, seated=True)

def drum_at(t, seated=False):
    """Beating the drum with the palms at t (0 .. 1 round the loop; see DRUM_BEATS), stood at it or, seated, to be laid
    over the sit. Returns the pose and the worst overreach."""
    a = music_play('drum', seated)
    t8 = (t % 1.0) * 8
    pulse = 0.0   # 1 on a strike, falling off over half an eighth either side; 2 on the accent
    for b in set(DRUM_BEATS[0]) | set(DRUM_BEATS[1]):
        d = abs((t8 - b + 4) % 8 - 4)
        pulse = max(pulse, max(0.0, 1.0 - d / 0.5) ** 2 * (2.0 if b == DRUM_ACCENT else 1.0))
    X = music_body('drum', t, seated, dip=DRUM_DIP * pulse, bob=DRUM_BOB * pulse, nod=DRUM_NOD * pulse)
    clamp = 0.0
    for k, (side, sign) in enumerate((('L', 1), ('R', -1))):
        beats = sorted(DRUM_BEATS[k])
        prev = max([b for b in beats if b <= t8] or [beats[-1] - 8])
        nxt = min([b for b in beats if b > t8] or [beats[0] + 8])
        f = (t8 - prev) / (nxt - prev)
        g = math.sin(math.pi * f ** 1.6)   # raised slowly after a strike, brought down fast into the next
        lift = DRUM_LIFT * min(1.0, (nxt - prev) / 2.0) * g
        span = DRUM_SPAN
        if nxt % 8 == DRUM_ACCENT:
            lift *= 1.3
            span *= 1.0 - 0.3 * smooth(f)
        elif prev % 8 == DRUM_ACCENT:
            span *= 0.7 + 0.3 * smooth(f)
        tip = -math.radians(DRUM_TIP) * g
        fingers = (0.0, math.cos(tip), -math.sin(tip))
        facing = (0.0, -math.sin(tip), -math.cos(tip))
        palm = (a.x + sign * span, a.y, a.z + lift)
        clamp = max(clamp, music_hand(X, side, sign, palm, fingers, facing, DRUM_CURL, DRUM_ELBOW))
    return X, clamp

def drum_sit_at(t):
    return drum_at(t, seated=True)

def daze_at(t):
    """The stunned loop at t (0 .. 1 round it): slumped on soft knees, the arms hanging, the head lolling round, swaying.
    Returns the pose and the worst overreach."""
    X, clamp = posed_with(dict(STILL, DROP=DAZE_DROP, LEAN=DAZE_LEAN, ARM_DOWN=ARM_DOWN + DAZE_ARMS, ELBOW=6.0), 0.29,
                          sway=DAZE_SWAY * math.sin(2 * math.pi * t))
    hd = X[NECK] @ head[HEAD]
    a = 2 * math.pi * t
    turn_branch(X, HEAD, about(hd, (1, 0, 0), DAZE_BOW + DAZE_LOLL * 0.5 * math.cos(a))
                @ about(hd, (0, 1, 0), DAZE_LOLL * math.sin(a)))
    return X, clamp

def jump_keys(keys, t):
    """keys (take-off, top, landing: numbers or tuples) at t through the flight, eased between them (see JUMP_TOP)."""
    if t < JUMP_TOP:
        a, b, u = keys[0], keys[1], smooth(t / JUMP_TOP)
    else:
        a, b, u = keys[1], keys[2], smooth((t - JUMP_TOP) / (1 - JUMP_TOP))
    if isinstance(a, tuple):
        return Vector(a).lerp(Vector(b), u)
    return a + (b - a) * u

def jump_at(t):
    """The jump at t (0 take-off .. 1 landing; see JUMP_FRAMES). Returns the pose and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.0, LEAN=0.0, NARROW=HUNKER_NARROW), 0.29)
    def keys(name):
        """The JUMP_OFF_, JUMP_TOP_ and JUMP_LAND_ places of name (none at take-off: nothing)."""
        g = globals()
        return g.get('JUMP_OFF_' + name, (0.0, 0.0)), g['JUMP_TOP_' + name], g['JUMP_LAND_' + name]
    lean = jump_keys(JUMP_LEAN, t)
    turn_branch(X, SPINE, about(X[SPINE] @ head[SPINE], (1, 0, 0), lean))
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -lean * jump_keys(JUMP_LOOK, t)))
    clamp = 0.0
    split = jump_keys(keys('SPLIT'), t)
    for side, sign in (('L', 1), ('R', -1)):
        hip = X[HIPS] @ head['thigh.' + side]
        a = jump_keys(keys('ANKLE'), t)
        if sign < 0:
            a = a + Vector((0.0, split.x, -split.y))
        ankle = hip + Vector((sign * a.x, a.y, a.z)) * LEG
        clamp = max(clamp, leg_to(side, hip, ankle, Vector((sign * 0.15, -1.0, 0.0)), X, pitch=jump_keys(JUMP_POINT, t)))
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        w = jump_keys(keys('HANDS'), t)
        wrist = X['shoulder.' + side] @ head[ua] + X[CHEST_BONE].to_3x3() @ Vector((sign * w.x, w.y, w.z)) * reach
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * JUMP_ELBOW[0], JUMP_ELBOW[1], JUMP_ELBOW[2]))))
    return X, clamp

def land_pose():
    """Crouched to spring or having just landed (see LAND_SECONDS), the arms forward and out for balance. Returns the pose
    and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.01, LEAN=0.0, NARROW=HUNKER_NARROW), 0.29)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    hip = X[HIPS] @ head[HIPS]
    M = T(Vector((0, LAND_BACK, -LAND_DROP)) * LEG) @ about(hip, (1, 0, 0), LAND_HIPS)
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    X = {k: (v if k in legs else M @ v) for k, v in X.items()}
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -LAND_HIPS * LAND_LOOK))
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], FWD, X))
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        wrist = X['shoulder.' + side] @ head[ua] + X[CHEST_BONE].to_3x3() @ Vector((sign * LAND_HANDS[0], LAND_HANDS[1], LAND_HANDS[2])) * reach
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * JUMP_ELBOW[0], JUMP_ELBOW[1], JUMP_ELBOW[2]))))
    return X, clamp

def dive_key(name, t):
    """The DIVE_KEYS line name at t through the dive (0 .. 1), joined smoothly through its keys (Catmull-Rom)."""
    keys, at = DIVE_KEYS[name], DIVE_AT
    t = min(max(t, 0.0), 1.0)
    i = min(sum(1 for a in at[1:] if a <= t), len(at) - 2)
    u = (t - at[i]) / (at[i + 1] - at[i])
    def part(k):
        p0, p1, p2, p3 = (k[max(i - 1, 0)], k[i], k[i + 1], k[min(i + 2, len(k) - 1)])
        return 0.5 * (2 * p1 + (p2 - p0) * u + (2 * p0 - 5 * p1 + 4 * p2 - p3) * u * u + (3 * p1 - p0 - 3 * p2 + p3) * u ** 3)
    if isinstance(keys[0], tuple):
        return Vector([part([k[n] for k in keys]) for n in range(len(keys[0]))])
    return part(keys)

def low_pose(hips, pitch, look, foot, a, knee, split, grip_w, free_w, cover, grip, breath=0.0, duck=0.0):
    """A pose low to the ground, the dive's and the duck's, with the weapon in the hand grip ('L' or 'R'): as DIVE_KEYS,
    with knee the way the knees bend tipped forward (degrees), split how far DIVE_SPLIT is taken, cover (0 .. 1) how far
    the free hand has gone to PRONE_HAND, breath (0 .. 1) and duck (-1 .. 1) moving the chest and head of a loop. Returns
    the pose and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.0, LEAN=0.0, NARROW=HUNKER_NARROW), 0.29)
    floor = min(head['toe.L'].z, head['toe.R'].z)
    hip = X[HIPS] @ head[HIPS]
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    hip_z = head['thigh.L'].z - head[HIPS].z
    to = Vector((head[HIPS].x, head[HIPS].y + hips[0] * LEG, floor + hips[1] * LEG - hip_z))
    M = T(to - hip) @ about(hip, (1, 0, 0), pitch)
    X = {k: (v if k in legs else M @ v) for k, v in X.items()}
    if breath:
        turn_branch(X, CHEST_BONE, about(X[CHEST_BONE] @ head[CHEST_BONE], (1, 0, 0), -PRONE_BREATH * breath))
    look = look + PRONE_DUCK * duck
    turn_branch(X, NECK, about(X[NECK] @ head[NECK], (1, 0, 0), -look * 0.5))
    turn_branch(X, HEAD, about(X[HEAD] @ head[HEAD], (1, 0, 0), -look * 0.5))
    clamp = 0.0
    tipped = rot((1, 0, 0), knee).to_3x3()
    for side, sign in (('L', 1), ('R', -1)):
        ankle = Vector((head[HIPS].x + sign * a[0] * LEG, head['foot.' + side].y + a[1] * LEG, floor + a[2] * LEG))
        if sign < 0:
            ankle += Vector((-DIVE_SPLIT[0], DIVE_SPLIT[1], DIVE_SPLIT[2])) * LEG * split
        pole = tipped @ (FWD + Vector((sign * 0.15, 0, 0)))
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankle, pole, X, pitch=foot))
    for side, sign in (('L', 1), ('R', -1)):
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        w = grip_w if side == grip else free_w
        wrist = X['shoulder.' + side] @ head[ua] + X[CHEST_BONE].to_3x3() @ Vector((sign * w[0], w[1], w[2])) * reach
        if side != grip and cover > 0:
            over = X[HEAD] @ (head[HEAD] + Vector((sign * PRONE_HAND[0], PRONE_HAND[1], PRONE_HAND[2])) * LEG)
            wrist = wrist.lerp(over, cover)
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * DIVE_ELBOW[0], DIVE_ELBOW[1], DIVE_ELBOW[2]))))
        close_hand(X, side, sign, 1.0 if side == grip else 0.35)
    return X, clamp

def dive_pose(t, grip, breath=0.0, duck=0.0):
    """The dive at t (0 standing .. 1 down on the front) with the weapon in the hand grip; breath and duck as low_pose's.
    Returns the pose and the worst overreach."""
    pitch = dive_key('pitch', t)
    return low_pose(dive_key('hips', t), pitch, dive_key('look', t), dive_key('foot', t), dive_key('ankle', t), pitch,
                    min(1.0, t / DIVE_AT[2]), dive_key('grip', t), dive_key('free', t),
                    smooth((t - PRONE_COVER) / (1 - PRONE_COVER)) if t > PRONE_COVER else 0.0, grip, breath, duck)

def duck_frames():
    """How many frames the hunker under fire is baked with, by its length on the clock."""
    return loop_frames(DUCK_SECONDS)

def duck_at(t, grip='R'):
    """Hunkered flat on the front under fire at t (0 .. 1 round the loop; see DUCK_HAND), the weapon in the hand grip.
    Returns the pose and the worst overreach."""
    breath = 0.5 - 0.5 * math.cos(2 * math.pi * DUCK_BREATHS * t)
    pitch = dive_key('pitch', 1.0)
    X, clamp = low_pose(dive_key('hips', 1.0), pitch, DUCK_LOOK, dive_key('foot', 1.0), dive_key('ankle', 1.0), pitch, 1.0,
                        dive_key('grip', 1.0), dive_key('free', 1.0), 0.0, grip, breath, 0.5 * math.sin(4 * math.pi * t))
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        wrist = X[HEAD] @ (head[HEAD] + Vector((sign * DUCK_HAND[0], DUCK_HAND[1], DUCK_HAND[2])) * LEG)
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * DUCK_ELBOW[0], DUCK_ELBOW[1], DUCK_ELBOW[2]))))
        close_hand(X, side, sign, 1.0 if side == grip else 0.35)
    return X, clamp

def roll_frames():
    """How many frames the roll is baked with, by its length on the clock."""
    return loop_frames(ROLL_SECONDS)

def roll_at(t, grip='R'):
    """Rolling on the ground to put out a fire at t (0 .. 1 round the loop; see ROLL_SECONDS): lying on the front as the
    dive leaves it at 0 and 0.5, on the back at 0.25 (rolled over the left side) and 0.75 (back over the right). grip is
    the hand a weapon would be in, closed. Returns the pose and the worst overreach."""
    pitch = dive_key('pitch', 1.0)
    X, _ = low_pose(dive_key('hips', 1.0), pitch, -ROLL_TUCK, ROLL_FOOT, ROLL_ANKLE, pitch, 0.0,
                    dive_key('grip', 1.0), dive_key('free', 1.0), 0.0, grip)
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        ua, fa = 'upper_arm.' + side, 'forearm.' + side
        reach = (tail[ua] - head[ua]).length + (tail[fa] - head[fa]).length
        wrist = X['shoulder.' + side] @ head[ua] + X[CHEST_BONE].to_3x3() @ Vector((sign * ROLL_WRIST[0], ROLL_WRIST[1], ROLL_WRIST[2])) * reach
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=X[CHEST_BONE].to_3x3() @ Vector((sign * ROLL_ELBOW[0], ROLL_ELBOW[1], ROLL_ELBOW[2]))))
        close_hand(X, side, sign, 1.0 if side == grip else 0.8)
    # Rolled about the line from the hips to the neck: a sine, so it slows to turn back on its back and passes the front
    # at full speed. The hips go sideways with it, the way the body would roll over the ground.
    turn = ROLL_TURN * math.sin(2 * math.pi * t)
    hip, neck = X[HIPS] @ head[HIPS], X[NECK] @ head[NECK]
    along = (neck - hip).normalized()
    across = along.cross(Vector((0, 0, -1)))
    across.z = 0.0
    M = T(-across * ROLL_RADIUS * ROLL_TRAVEL * LEG * math.radians(turn)) @ about(hip.lerp(neck, 0.5), along, turn)
    return {k: M @ v for k, v in X.items()}, clamp

def dive_at(t, grip='R'):
    """The dive to the ground at t (0 standing .. 1 down; see DIVE_KEYS). Returns the pose and the worst overreach."""
    return dive_pose(t, grip)

def bend_pose():
    """Squatting and reaching for the ground (see BEND_SECONDS). Returns the pose and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.01, LEAN=BEND_LEAN, NARROW=DRAFT_NARROW), 0.29)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    hip = X[HIPS] @ head[HIPS]
    M = T(Vector((0, BEND_BACK, -BEND_DROP)) * LEG) @ about(hip, (1, 0, 0), BEND_HIPS)
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    X = {k: (v if k in legs else M @ v) for k, v in X.items()}
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], FWD, X))
    middle = (ankles['L'] + ankles['R']) / 2
    for side, sign in (('L', 1), ('R', -1)):
        wrist = middle + Vector((sign * BEND_HANDS[0], BEND_HANDS[1], BEND_HANDS[2])) * LEG
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * 0.8, 0.5, -0.3))))
    return X, clamp

def bend_walk_pose():
    """Standing, folded at the waist and reaching down in front (see BEND_WALK). Returns the pose and the worst overreach."""
    X, _ = posed_with(dict(STILL, DROP=0.01, LEAN=BEND_LEAN, NARROW=DRAFT_NARROW), 0.29)
    turn_branch(X, SPINE, about(X[SPINE] @ head[SPINE], (1, 0, 0), BEND_WALK))
    turn_branch(X, NECK, about(X[CHEST_BONE] @ head[NECK], (1, 0, 0), -BEND_WALK_LOOK))
    ankles = [X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')]
    middle = (ankles[0] + ankles[1]) / 2
    clamp = 0.0
    for side, sign in (('L', 1), ('R', -1)):
        wrist = middle + Vector((sign * BEND_WALK_HANDS[0], BEND_WALK_HANDS[1], BEND_WALK_HANDS[2])) * LEG
        clamp = max(clamp, arm_to(side, sign, wrist, X, pole=Vector((sign * 0.8, 0.5, -0.3))))
    return X, clamp

def work_mocap(kind):
    """The mocap clip a WORK kind's stroke plays (a variant's WORK_MOCAP, else its own WORK_MOCAPS), or None."""
    return WORK_MOCAP if WORK_MOCAP is not None else WORK_MOCAPS.get(kind)

def work_seconds(kind):
    """One stroke of a WORK kind in game seconds (a mocap stroke's at its own length)."""
    clip = work_mocap(kind)
    return clip['frames'] / clip['fps'] if clip is not None else WORK[kind]['ticks'] / 60.0

def work_stroke(t, w):
    """How far a WORK entry's stroke is from struck (0) to raised (1) at t (0 .. 1 round it), timed as Work.cs's."""
    if w['motion'] == 'chop':
        if t < 0.55:
            return smooth(t / 0.55)   # wound up slowly
        if t < 0.7:
            return 1 - ((t - 0.55) / 0.15) ** 2   # the blow, speeding up
        return 0.07 * math.sin(math.pi * (t - 0.7) / 0.3)   # a small bounce off the work
    if w['motion'] == 'sweep':
        return 0.5 + 0.5 * math.sin(2 * math.pi * t)
    if w['motion'] == 'jig':
        # Held low with a slow sway, then the tip twitched up quickly from 0.62 and let down again by 0.85.
        jig = 0.0
        if 0.62 <= t < 0.85:
            v = (t - 0.62) / 0.23
            jig = math.sin(math.pi * min(v / 0.35, 1.0) / 2) ** 2 if v < 0.35 else smooth(1 - (v - 0.35) / 0.65)
        return 0.08 + 0.06 * math.sin(2 * math.pi * t) + 0.86 * jig
    return 0.5 - 0.5 * math.cos(2 * math.pi * w.get('taps', 1) * t)

def work_key(w, u):
    """A WORK entry's keys eased together at u (0 struck .. 1 raised): its 'path' keys between them, if it has any, joined
    by a smooth curve through every key (Catmull-Rom, so the tool swings round an arc rather than straight across), else
    the two eased straight into each other."""
    a, b = w['struck'], w['raised']
    knots = [(0.0, a)] + [(at, dict({n: lerp_key(a[n], b[n], at) for n in a}, **part)) for at, part in w.get('path', ())] \
        + [(1.0, b)]
    u = min(max(u, 0.0), 1.0)
    i = 0
    while i < len(knots) - 2 and u > knots[i + 1][0]:
        i += 1
    (u0, k0), (u1, k1) = knots[i], knots[i + 1]
    s = (u - u0) / (u1 - u0)
    if len(knots) == 2:
        return {n: lerp_key(k0[n], k1[n], s) for n in k0}
    before, after = knots[max(i - 1, 0)], knots[min(i + 2, len(knots) - 1)]
    h00, h10, h01, h11 = 2 * s ** 3 - 3 * s * s + 1, s ** 3 - 2 * s * s + s, -2 * s ** 3 + 3 * s * s, s ** 3 - s * s
    out = {}
    # (a place a path key leaves out comes eased from struck and raised as a Vector, beside the keys' own tuples)
    vec = lambda v: Vector(v) if isinstance(v, tuple) else v
    for n in k0:
        p0, p1 = vec(k0[n]), vec(k1[n])
        q0, q1 = vec(before[1].get(n, k0[n])), vec(after[1].get(n, k1[n]))
        # Each key's slope from its neighbours, over this span's length of u.
        m0 = (p1 - q0) * ((u1 - u0) / (u1 - before[0])) if before[0] < u0 else (p1 - p0)
        m1 = (q1 - p0) * ((u1 - u0) / (after[0] - u0)) if after[0] > u1 else (p1 - p0)
        out[n] = p0 * h00 + m0 * h10 + p1 * h01 + m1 * h11
    return out

def lerp_key(a, b, u):
    """One number or place of a work key eased from a to b by u."""
    if isinstance(a, tuple):
        return Vector(a).lerp(Vector(b), u)
    return a + (b - a) * u

def tool_turn(k):
    """The tool's turn from upright (armature space) for a work key: pitched forward, turned about the vertical, rolled
    about the handle."""
    return rot((0, 0, 1), k['yaw']) @ rot((1, 0, 0), k['pitch']) @ rot((0, 0, 1), k['roll'])

def tool_matrix(kind, k):
    """Where a WORK kind's tool model is for the work key k (armature space): it takes a point of the model given in
    Blender's axes (the game's model space turned back, export_frames.GAME) in model units."""
    w = WORK[kind]
    R = tool_turn(k)
    size = w['scale'] * LEG / MODEL_LEG   # armature units per model unit
    d = R.to_3x3() @ Vector((0, 0, 1))
    origin = head[HIPS] + k['fist'] * LEG - d * w['grip'] * size
    return T(origin) @ R @ Matrix.Scale(size, 4)

def grip_handle(side, sign, fist, d, edge, wrap, X, lift=None, swing=0.0, palm=0.0):
    """Places one arm by IK so its fist closes round a handle at fist (armature space) running along d (toward the tool's
    head, which the thumb points at), the fingers pointing the way edge does turned wrap degrees about the handle, the
    handle palm leg lengths out of the palm, the elbow lifted as arm_to's lift, or swung swing degrees about the line
    from the shoulder to the wrist (out from the body when the hand is in front, a fixed way round so it eases evenly).
    Returns how far the wrist was out of reach."""
    hn = 'hand.' + side
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector((sign * HAND_PALM[0], HAND_PALM[1], HAND_PALM[2]))
    t0 = -sign * d0.cross(n0)   # the thumb at rest
    t0 = (t0 - t0.dot(d0) * d0).normalized()
    n0 = (n0 - n0.dot(d0) * d0).normalized()
    d = d.normalized()
    f = Matrix.Rotation(math.radians(wrap), 3, d) @ edge
    f = (f - f.dot(d) * d).normalized()
    R = Matrix((f, d, f.cross(d))).transposed() @ Matrix((d0, t0, d0.cross(t0)))
    chest = X[CHEST_BONE].to_quaternion().to_matrix()
    turn = (chest.inverted() @ R).to_euler('XYZ')
    wrist = fist - f * WORK_GRIP * LEG - (R @ n0) * palm * LEG
    pole = None
    if swing:
        line = (wrist - X['shoulder.' + side] @ head['upper_arm.' + side]).normalized()
        pole = chest.inverted() @ Matrix.Rotation(math.radians(swing * sign), 3, line) @ chest @ Vector((sign * ELBOW_OUT, 0.25, -1.0))
    return arm_to(side, sign, wrist, X, pole=pole, turn=[math.degrees(a) for a in turn], lift=lift)

def wrist_bend(X, side, sign):
    """How far one hand is bent at the wrist from straight on its forearm, degrees: toward the palm (- back) and toward
    the thumb (- toward the little finger)."""
    hn, fa = 'hand.' + side, 'forearm.' + side
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector((sign * HAND_PALM[0], HAND_PALM[1], HAND_PALM[2]))
    n0 = (n0 - n0.dot(d0) * d0).normalized()
    R = X[hn].to_3x3()
    h, n, t = R @ d0, R @ n0, R @ (-sign * d0.cross(n0))
    a = (X[fa].to_3x3() @ (tail[fa] - head[fa])).normalized()
    return math.degrees(math.atan2(-a.dot(n), a.dot(h))), math.degrees(math.atan2(-a.dot(t), a.dot(h)))

def work_body(kind, k):
    """A WORK kind's pose for the work key k, all but the arms. Returns the pose and the worst overreach."""
    w = WORK[kind]
    if w.get('seated'):
        # Sat: the sit as it is from the hips down, the trunk bent over it from the waist.
        X, clamp = sit_at(0.0)
        return work_trunk(X, k), clamp
    X, _ = posed_with(dict(STILL, DROP=0.01) | w.get('body', {}), 0.29)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    # Lowered on bent knees with the feet kept where they stood, then bent forward and turned from the waist, the head
    # taking some of it back to look at the work.
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    # The hips carried forward ('shift') and turned ('hips') over the planted feet too, so the weight goes from the back
    # foot to the front one through a swing.
    hips_turn = k.get('hips', 0.0)
    pivot = X[HIPS] @ head[HIPS]
    M = T(FWD * k.get('shift', 0.0) * LEG + Vector((0, 0, -(k['drop'] - 0.01) * LEG))) @ about(pivot, (0, 0, 1), hips_turn)
    X = {n: (v if n in legs else M @ v) for n, v in X.items()}
    clamp = 0.0
    for side in ('L', 'R'):
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], FWD, X))
    return work_trunk(X, k), clamp

def work_trunk(X, k):
    """Bends X's trunk from the waist for the work key k (its lean, twist and tilt), the head taking back WORK_LOOK of
    them (and of the hips' turn) to look at the work. Returns X."""
    hips_turn = k.get('hips', 0.0)
    tilt = k.get('tilt', 0.0)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), k['twist'] * 0.5) @ about(pivot, (1, 0, 0), k['lean'] * 0.5)
                    @ about(pivot, (0, 1, 0), tilt * 0.5))
    pivot = X[NECK] @ head[NECK]
    turn_branch(X, NECK, about(pivot, (0, 1, 0), -tilt * WORK_LOOK) @ about(pivot, (1, 0, 0), -k['lean'] * WORK_LOOK)
                @ about(pivot, (0, 0, 1), -(k['twist'] + hips_turn) * WORK_LOOK))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    return X

def work_grip(kind, k, side, X, hold, swing=0.0):
    """Closes one hand round a WORK kind's handle for the work key k ('R' at the tool's grip, 'L' at 'other' along it),
    held as hold has it (the turn about the handle, grip_handle's wrap, and the thumb's way along it, 1 toward the head),
    the elbow swung as grip_handle's. Returns how far the wrist was out of reach."""
    w = WORK[kind]
    R = tool_turn(k).to_3x3()
    wrap, thumb = hold
    d, edge = R @ Vector((0, 0, thumb)), R @ Vector((0, -1, 0))
    fist = head[HIPS] + k['fist'] * LEG
    if side == 'R':
        clamp = grip_handle('R', -1, fist, d, edge, wrap, X, swing=swing, palm=WORK_PALM)
    else:
        size = w['scale'] * LEG / MODEL_LEG
        clamp = grip_handle('L', 1, fist + d * thumb * (k.get('other', w['other']) - w['grip']) * size, d, edge, wrap, X, swing=swing, palm=WORK_PALM)
    close_hand(X, side, -1 if side == 'R' else 1)
    return clamp

def work_grips(kind):
    """The sides whose hand closes round a WORK kind's handle."""
    w = WORK[kind]
    if w['tool'] is None:
        return ()
    return ('R', 'L') if w.get('other') is not None and 'L' not in w['struck'] else ('R',)

work_hold_cache = {}

def work_holds(kind):
    """Each gripping hand's hold on the handle (work_grip's: the turn about it and the thumb's way along it, one for the
    whole stroke, as the tool rides the hand) and its elbow swing (grip_handle's) at GRIP_SAMPLES points evenly along a
    WORK kind's stroke from struck (0) to raised (1): those that keep the wrist straightest and the hand least twisted
    over the whole stroke without winging the elbow, the swing kept from jumping between points (a shortest path over
    the swings tried), so the elbow moves smoothly. Returns {side: (hold, swings)}; a hold the WORK entry gives is kept."""
    key = (kind, repr(WORK[kind]), GRIP_SWING, GRIP_STEP, GRIP_FREE, GRIP_LOW, GRIP_RAISE, GRIP_SAMPLES, GRIP_EASE, GRIP_KEEP,
           GRIP_WRAP_STEP, GRIP_COARSE, GRIP_FLEX, GRIP_DEVIATE, WORK_GRIP, WORK_PALM, FIST, FOREARM_TWIST, FOREARM_TWIST_MID)
    if key in work_hold_cache:
        return work_hold_cache[key]
    w = WORK[kind]
    bodies = [(k, work_body(kind, k)[0]) for k in (work_key(w, i / (GRIP_SAMPLES - 1)) for i in range(GRIP_SAMPLES))]

    def path_of(side, hold, step):
        """The cheapest swings for one hold, tried step degrees apart: (cost, swings)."""
        n = int(GRIP_SWING / step)
        tried = [i * step for i in range(-n, n + 1)]
        costs = []
        for k, X in bodies:
            row = []
            for s in tried:
                Y = dict(X)
                over = work_grip(kind, k, side, Y, hold, s)
                elbow = Y['forearm.' + side] @ head['forearm.' + side]
                shoulder = Y['upper_arm.' + side] @ head['upper_arm.' + side]
                raised = max(0.0, (elbow.z - shoulder.z) / LEG + GRIP_LOW)
                flex, deviate = wrist_bend(Y, side, -1 if side == 'R' else 1)
                row.append(max(0.0, abs(arm_twist[side]) - GRIP_FREE) ** 2 + GRIP_KEEP * s * s + (GRIP_RAISE * raised) ** 2
                           + max(0.0, abs(flex - GRIP_FLEX[0]) - GRIP_FLEX[1]) ** 2
                           + max(0.0, abs(deviate - GRIP_DEVIATE[0]) - GRIP_DEVIATE[1]) ** 2 + 1e6 * over)
            costs.append(row)
        # Cheapest path through the swings, a change of swing between neighbouring points costing GRIP_EASE per degree
        # squared.
        total, back = list(costs[0]), []
        for row in costs[1:]:
            step_to = [min((total[j] + GRIP_EASE * (tried[i] - tried[j]) ** 2, j) for j in range(len(tried))) for i in range(len(tried))]
            back.append([j for _, j in step_to])
            total = [c + row[i] for i, (c, _) in enumerate(step_to)]
        j = min(range(len(tried)), key=lambda i: total[i])
        cost, path = total[j], [j]
        for b in reversed(back):
            j = b[j]
            path.append(j)
        return cost, [tried[j] for j in reversed(path)]

    out = {}
    for side in work_grips(kind):
        pre = '' if side == 'R' else 'other_'
        wraps = [w[pre + 'wrap']] if w.get(pre + 'wrap') is not None else \
            [i * GRIP_WRAP_STEP - 180.0 for i in range(int(360 / GRIP_WRAP_STEP))]
        thumbs = [w[pre + 'thumb']] if w.get(pre + 'thumb') is not None else [1, -1]
        holds = [(wrap, thumb) for thumb in thumbs for wrap in wraps]
        hold = holds[0] if len(holds) == 1 else min(holds, key=lambda h: path_of(side, h, GRIP_STEP * GRIP_COARSE)[0])
        out[side] = (hold, path_of(side, hold, GRIP_STEP)[1])
    work_hold_cache[key] = out
    return out

def work_at(t, kind):
    """The work loop for a WORK kind at t (0 .. 1 round one stroke): its mocap clip if it has one (work_mocap), else its
    posed stroke. Returns the pose and the worst overreach."""
    clip = work_mocap(kind)
    if clip is CHOP_CLIP and clip is not None:
        return chop_at(t), 0.0
    if clip is not None:
        return kneel_at(clip, t), 0.0
    return tool_stroke_at(t, kind)

chop_wrist = []   # the wrist's turn at the hit, in the hand's rest frame, once worked out (chop_at)
chop_balls = {}   # where each foot's ball is planted for the chop, by side, once worked out (chop_ball)

def chop_foot(side):
    """The chop's turn for this side's foot, and its ankle's place from its ball (see CHOP_HEEL_L)."""
    sign, out, heel = (1, CHOP_TOE_L, CHOP_HEEL_L) if side == 'L' else (-1, CHOP_TOE_R, CHOP_HEEL_R)
    flat = rot((0, 0, 1), out * sign)
    return flat, flat @ rot((1, 0, 0), heel), head['foot.' + side] - head['toe.' + side]

def chop_ball(side):
    """Where the chop plants this side's ball (armature space; see CHOP_REACH)."""
    if side not in chop_balls:
        n = CHOP_CLIP['frames']
        poses = [kneel_at(CHOP_CLIP, f / n) for f in range(n)]
        ft, th = 'foot.' + side, 'thigh.' + side
        flat, up, back = chop_foot(side)
        ankle = sum((X[ft] @ head[ft] for X in poses), Vector()) / n
        ankle.z = (stand_pose()[ft] @ head[ft]).z
        ankle.y += (CHOP_SET_L if side == 'L' else CHOP_SET_R) * LEG
        ball = ankle - flat.to_3x3() @ back
        hips = [X[HIPS] @ head[th] for X in poses]
        middle = sum(hips, Vector()) / n
        # Drawn in along the floor toward the hips' mean, by steps of a hundredth of a leg.
        way = Vector((middle.x - ball.x, middle.y - ball.y, 0.0)).normalized()
        while max((hip - (ball + up.to_3x3() @ back)).length for hip in hips) > CHOP_REACH * (L1 + L2):
            ball += way * LEG * 0.01
        chop_balls[side] = ball
    return chop_balls[side]

def chop_legs(X):
    """Plants the feet in the chop's pose X (see CHOP_REACH), leaving the hips and up as they are."""
    for side in ('L', 'R'):
        flat, up, back = chop_foot(side)
        ball = chop_ball(side)
        leg_to(side, X[HIPS] @ head['thigh.' + side], ball + up.to_3x3() @ back, flat.to_3x3() @ FWD, X, turn=up)
        X['toe.' + side] = place(ball, flat, head['toe.' + side])

def axe_edge():
    """The way the axe's edge points in the right hand's rest frame, the hand holding it as the posed stroke does."""
    w = WORK['axe']
    X, _ = tool_stroke_at(0.0, 'axe')
    held = X['hand.R'].inverted() @ tool_matrix('axe', work_key(w, work_stroke(0.0, w)))
    return (held.to_3x3() @ Vector((0, -1, 0))).normalized()   # the blade lies toward the model's -y in Blender's axes

chop_head = []   # the axe's head in the right hand's rest frame, once worked out (chop_axe_head)

def chop_axe_head():
    """Where the axe's head is in the right hand's rest frame (armature units), the hand holding it as the posed stroke
    does: the end of the handle, which runs along the model's z, its head 0.45 model units up it."""
    if not chop_head:
        w = WORK['axe']
        X, _ = tool_stroke_at(0.0, 'axe')
        held = X['hand.R'].inverted() @ tool_matrix('axe', work_key(w, work_stroke(0.0, w)))
        chop_head.append(held @ Vector((0, 0, 0.45)))
    return chop_head[0]

chop_turns = {}   # the ways of the arm's swings, by stage, at each of CHOP_AXIS_STEPS a frame (chop_axes)

def chop_at(t):
    """The chop at t (0 .. 1 round the loop; see CHOP_CLIP), the right wrist turned as CHOP_BLADE has it."""
    return chop_pose(t, chop_axes())

def chop_axes():
    """The ways the arm is swung out (CHOP_OUT), clear of the head (CHOP_CLEAR) and the axe tipped (CHOP_TIP), in the
    chest's frame, at each of CHOP_AXIS_STEPS samples a frame round the loop. Each is worked out from the pose (the
    wrist about the shoulder and the head), but on the way back the arm passes nearly in line with the head, where that
    way whips round within a frame (the arm snapped) and sweeps about either side of it (the wrist went down and up
    again). So each is smoothed round the loop (CHOP_AXIS_SMOOTH, CHOP_AXIS_BACK), each sample weighed
    by how much of the swing there is and how well its way is known (the cross product's length)."""
    if not chop_turns:
        n = CHOP_CLIP['frames']
        m = CHOP_AXIS_STEPS * n
        hit, ease = CHOP_CLIP['hit'], lambda u: u * u * (3 - 2 * u)

        def spread_at(k):
            # CHOP_AXIS_BACK on the way back, eased to CHOP_AXIS_SMOOTH over CHOP_AXIS_EASE frames either end of it.
            since = (k / CHOP_AXIS_STEPS - hit) % n
            edge = min(since, n - hit - since) if since < n - hit else 0.0
            u = ease(min(max(edge / CHOP_AXIS_EASE, 0.0), 1.0))
            return (CHOP_AXIS_SMOOTH + (CHOP_AXIS_BACK - CHOP_AXIS_SMOOTH) * u) * CHOP_AXIS_STEPS

        for stage in ('out', 'clear', 'tip'):
            raw = []
            for k in range(m):
                seen = {}
                chop_pose(k / m, chop_turns, seen)
                raw.append(seen.get(stage, Vector()))
            ways = []
            for k in range(m):
                spread = spread_at(k)
                reach = min(int(math.ceil(3 * spread)), m // 2)
                ways.append(sum((raw[(k + d) % m] * math.exp(-0.5 * (d / spread) ** 2)
                                 for d in range(-reach, reach + 1)), Vector()).normalized())
            chop_turns[stage] = ways
    return chop_turns

def chop_way(axes, stage, t):
    """axes' way for this stage at t (0 .. 1 round the loop), eased between its samples."""
    ways = axes[stage]
    u = (t % 1.0) * len(ways)
    i = int(u) % len(ways)
    return ways[i].lerp(ways[(i + 1) % len(ways)], u - math.floor(u)).normalized()

def chop_pose(t, axes, seen=None):
    """The chop at t, its swings about the ways in axes (chest frame). A swing not in axes is about the way the pose
    gives at t, which is put in seen (chest frame, weighed: see chop_axes)."""
    clip = CHOP_CLIP
    X = kneel_at(clip, t)
    chop_legs(X)
    hand = 'hand.R'
    if not chop_wrist:
        hit = kneel_at(clip, clip['hit'] / clip['frames'])[hand].to_quaternion()
        edge = axe_edge()
        chop_wrist.append(edge.rotation_difference(hit.inverted() @ Vector(CHOP_BLADE)))
    n = clip['frames']
    i = int(t * n) % n
    share = clip['turn'][i] + (clip['turn'][(i + 1) % n] - clip['turn'][i]) * (t * n - math.floor(t * n))
    ease = lambda u: u * u * (3 - 2 * u)
    since = (t * n - clip['hit']) % n   # frames since the hit
    span = n - clip['hit'] + CHOP_CLEAR_TO
    clear = ease(min(since / CHOP_CLEAR_IN, 1.0)) * ease(min(max(span - since, 0.0) / CHOP_CLEAR_OUT, 1.0))
    out = CHOP_OUT * (1 - share)
    chest = X['spine.003'].to_3x3().normalized()

    def way_of(stage, found, weight):
        if stage in axes:
            return chest @ chop_way(axes, stage, t)
        if seen is not None:
            seen[stage] = chest.inverted() @ found * weight
        return found.normalized()

    if out > 0:
        shoulder = X['upper_arm.R'] @ head['upper_arm.R']
        way = way_of('out', (X[hand] @ head[hand] - shoulder).cross(Vector((-1, 0, 0))), out / CHOP_OUT)
        turn_branch(X, 'upper_arm.R', about(shoulder, way, out))
        wrist = X[hand] @ head[hand]
        turn_branch(X, hand, about(wrist, way, -out))   # the hand keeps its way, so the axe only moves out
    if clear > 0:
        # Swung about the shoulder so the wrist goes away from the head, then the axe tipped about the wrist so its
        # head goes away from it too.
        middle = X[HEAD] @ ((head[HEAD] + tail[HEAD]) / 2)
        shoulder = X['upper_arm.R'] @ head['upper_arm.R']
        wrist = X[hand] @ head[hand]
        way = way_of('clear', (wrist - shoulder).cross(wrist - middle), clear)
        turn_branch(X, 'upper_arm.R', about(shoulder, way, CHOP_CLEAR * clear))
        wrist = X[hand] @ head[hand]
        turn_branch(X, hand, about(wrist, way, -CHOP_CLEAR * clear))   # the hand keeps its way
        top = X[hand] @ chop_axe_head()
        way = way_of('tip', (top - wrist).cross(top - middle), clear)
        turn_branch(X, hand, about(wrist, way, CHOP_TIP * clear))
    if share > 0:
        R = X[hand].to_quaternion()
        turn = R @ Quaternion().slerp(chop_wrist[0], share) @ R.inverted()
        pivot = X[hand] @ head[hand]
        turn_branch(X, hand, T(pivot) @ turn.to_matrix().to_4x4() @ T(-pivot))
    return moved_all(T(Vector((0.0, -CHOP_FORWARD * LEG, 0.0))), X)

def tool_stroke_at(t, kind):
    """A WORK kind's posed stroke at t (0 .. 1 round it), its tool in the right hand (which places the tool line, even
    for a kind that plays a mocap clip). Returns the pose and the worst overreach."""
    w = WORK[kind]
    u = work_stroke(t, w)
    k = work_key(w, u)
    X, clamp = work_body(kind, k)
    hips = head[HIPS]
    if w['tool'] is None:
        for side, sign in (('L', 1), ('R', -1)):
            clamp = max(clamp, arm_to(side, sign, hips + k[side] * LEG, X))
        return X, clamp
    # Each gripping hand turned about the handle as work_holds has it, its elbow swung as it has it, eased between its
    # points by how far through the stroke the tool is.
    at = min(max(u, 0.0), 1.0) * (GRIP_SAMPLES - 1)
    i = min(int(at), GRIP_SAMPLES - 2)
    for side, (hold, path) in work_holds(kind).items():
        clamp = max(clamp, work_grip(kind, k, side, X, hold, path[i] + (path[i + 1] - path[i]) * (at - i)))
    if 'L' in k:
        clamp = max(clamp, arm_to('L', 1, hips + k['L'] * LEG, X))
    return X, clamp

def aimed_kind(name):
    """The AIMED kind a weapon (or kind) is aimed as: its own entry's 'kind', else its DRAFTED kind, else itself."""
    entry = AIMED.get(name) or {}
    return entry.get('kind') or DRAFTED.get(name, {}).get('kind') or name

def aimed_entry(name):
    """An AIMED entry with its kind's under it; None if it is not aimed."""
    if name in AIMED and AIMED[name] is None:
        return None
    kind = aimed_kind(name)
    if kind not in AIMED or AIMED[kind] is None:
        return None
    own = AIMED.get(name) or {}
    out = dict(AIMED[kind], **{k: v for k, v in own.items() if k != 'kind'})
    body = dict(AIMED[kind].get('body', {}), **own.get('body', {}))
    if body:
        out['body'] = body
    return out

def aimed_names():
    """Every kind and weapon that gets aim clips: held on the 'hand' line in the hand its kind aims with (a bow's left, any
    other's right). A weapon held just as its kind is, and aimed as it is, gets none (the game falls back to the kind's)."""
    out = []
    for name, w in WEAPONS.items():
        if aimed_entry(name) is None or w['line'] != 'hand' or w['hand'] != ('L' if aimed_kind(name) == 'bow' else 'R'):
            continue
        kind = DRAFTED.get(name, {}).get('kind')
        own = {k: v for k, v in w.items() if k != 'scale'}
        if kind in out and own == {k: v for k, v in WEAPONS[kind].items() if k != 'scale'} \
                and aimed_kind(name) == aimed_kind(kind) and not AIMED.get(name):
            continue
        out.append(name)
    return out

def aim_stages(name):
    """The clips one aimed weapon gets: aim_ and fire_, and a bow's nock_."""
    return ('aim', 'fire', 'nock') if 'draw' in aimed_entry(name) else ('aim', 'fire')

def aim_pitch(t):
    """The pitch (degrees, up +) of an aim clip's frame at t, frames at t = 0, 1/n .. (n-1)/n as export_loop takes them."""
    n = AIM_PITCHES
    return AIM_PITCH * (2.0 * t * n / max(1, n - 1) - 1.0)

def weapon_top(a, roll):
    """WalkFigures.TopOf in armature space: straight up as far as the line a lets it (the front when a points straight up),
    turned roll degrees about the line (the game's space is this one mirrored, so the other way here)."""
    up = Vector((0, 0, 1))
    top = up - a * up.dot(a)
    if top.length_squared < 0.01:
        top = FWD - a * FWD.dot(a)
    return Quaternion(a, math.radians(-roll)) @ top.normalized()

def held_turn(w, d, roll=0.0):
    """The hand's turn from rest (armature space) that has a weapon held as w (a WEAPONS 'hand' line) run along d with its
    top as near straight up as d lets it, then turned roll degrees about d, as WalkFigures.DrawWeapon places it."""
    ra = (Vector(w.get('aim', (0.0, -1.0, 0.0))).normalized() + Vector((0, 0, -w['drop']))).normalized()
    rt = weapon_top(ra, w['roll'])
    rt = (rt - rt.dot(ra) * ra).normalized()
    d = d.normalized()
    u = weapon_top(d, roll)
    return Matrix((d, u, d.cross(u))).transposed() @ Matrix((ra, rt, ra.cross(rt)))

def held_grip(w, fist, R):
    """Where the weapon's grip is (armature space) when the hand turned R holding it as w closes at fist (on along the hand
    from the wrist by HAND_GRIP): moved from there by its 'at' and WEAPON_LIFT and the rest, as WalkFigures.DrawWeapon
    moves it."""
    size = LEG / MODEL_LEG   # armature units per model unit
    shift = Vector((0, 0, WEAPON_LIFT)) + Vector((1 if w['hand'] == 'L' else -1, 0, 0)) * WEAPON_OUT + FWD * WEAPON_FORWARD
    return fist + (R.to_3x3() @ Vector(w.get('at', (0.0, 0.0, 0.0))) + shift) * size

aim_last = {}   # where aim_at last put the hands and in what frame, for preview.py's handles to set AIMED from

def aim_at(t, name, stage='aim'):
    """An aim clip's frame at t (see aim_pitch) for an aimed weapon or kind (aimed_names): stage 'aim' ready to fire,
    'fire' the kick of a shot (a bow's loose), 'nock' a bow's arrow nocked before the draw. Returns the pose and the worst
    overreach (and keeps its working in aim_last)."""
    a, w = aimed_entry(name), WEAPONS[name]
    pitch = aim_pitch(t)
    fired = stage == 'fire'
    X, _ = posed_with(dict(STILL, DROP=DRAFT_DROP, LEAN=DRAFT_LEAN, NARROW=DRAFT_NARROW) | a.get('body', {}), 0.29)
    # The whole figure turned (the hips and feet), then the trunk turned side on and bent back to aim up (forward to aim down), rocked back by a shot; the head turns the
    # twist back to face the target, follows the pitch further than the trunk and bows onto the sights.
    lean = a['lean'] - pitch * AIM_BEND - (a['rock'] if fired else 0.0)
    hips = a.get('hips', 0.0)
    if hips:
        X = moved_all(about(X[HIPS] @ head[HIPS], (0, 0, 1), hips), X)
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), a['twist'] * 0.5) @ about(pivot, (1, 0, 0), lean * 0.5))
    pivot = X[NECK] @ head[NECK]
    nod = a['sight'] - pitch * AIM_LOOK - lean
    turn_branch(X, NECK, about(pivot, (0, 0, 1), -a['twist'] - hips) @ about(pivot, (1, 0, 0), nod))
    for n, leader in FOLLOW.items():
        X[n] = X[leader]
    side = w['hand']
    sign = 1 if side == 'L' else -1
    other, osign = ('R', -1) if side == 'L' else ('L', 1)
    # The fist where the kind holds it in the chest's frame, swung on round the shoulder by the pitch the trunk left.
    shoulder = X['upper_arm.' + side] @ head['upper_arm.' + side]
    place = (T(Vector((0, -AIM_UP_REACH[0], -AIM_UP_REACH[1])) * LEG * max(0.0, pitch) / AIM_PITCH)
             @ about(shoulder, (1, 0, 0), -pitch * (1 - AIM_BEND)) @ X[CHEST_BONE] @ T(head[HIPS]) @ Matrix.Scale(LEG, 4))
    fist = place @ Vector(a['fist'])
    d = rot((1, 0, 0), -pitch).to_3x3() @ FWD
    kicked = Vector((0, 0, 0))
    if fired:
        kicked = d * a['kick'] * LEG
        fist = fist - kicked
        d = Matrix.Rotation(math.radians(a['rise']), 3, d.cross(Vector((0, 0, 1)))) @ d
    R = held_turn(w, d)
    turn = (X[CHEST_BONE].to_quaternion().to_matrix().inverted() @ R.to_3x3()).to_euler('XYZ')
    hn = 'hand.' + side
    wrist = fist - R.to_3x3() @ (tail[hn] - head[hn]).normalized() * HAND_GRIP * LEG / MODEL_LEG
    clamp = arm_to(side, sign, wrist, X, turn=[math.degrees(v) for v in turn], lift=a.get('elbow'))
    grip = held_grip(w, fist, R)
    up = weapon_top(d, 0.0)
    right = d.cross(up)
    aim_last.clear()
    aim_last.update(name=name, stage=stage, side=side, other=other, osign=osign, place=place, kicked=kicked, fist=fist,
                    grip=grip, d=d, up=up, right=right)
    if 'draw' in a:
        # The string hand along the arrow from the bow hand: at the bow nocked, drawn back to the chin, or thrown back.
        o = a['loose'] if fired else a['nock'] if stage == 'nock' else a['draw']
        wrist = fist + (right * o[0] - d * o[1] + up * o[2]) * LEG
        aim_last['string'] = wrist
        held = aim_turn(a, X, d, up, right)
        return X, max(clamp, arm_to(other, osign, wrist, X, pole=Vector(a['draw_pole']), turn=held and held[1],
                                    lift=a.get('other_elbow')))
    hold = support_hold(X, other, grip, d, up, a)
    aim_last['hold'] = hold
    held = aim_turn(a, X, d, up, right)
    if held:
        # Held at its own turn: the wrist back from the fist along the fingers, as grip_handle has it.
        fingers = held[0] @ (tail['hand.' + other] - head['hand.' + other]).normalized()
        return X, max(clamp, arm_to(other, osign, hold - fingers * WORK_GRIP * LEG, X, turn=held[1], lift=a.get('other_elbow')))
    return X, max(clamp, grip_handle(other, osign, hold, d, up, a['wrap'] * osign, X, lift=a.get('other_elbow')))

def support_hold(X, other, grip, d, up, a):
    """Where the other hand closes on the weapon, by an AIMED entry's numbers: on along it from the grip, or slid back along it
    toward that shoulder as far as that arm needs to reach."""
    reach = sum((tail[b + other] - head[b + other]).length for b in ('upper_arm.', 'forearm.')) * 0.97 + WORK_GRIP * LEG
    shoulder = X['upper_arm.' + other] @ head['upper_arm.' + other]
    near = (shoulder - grip).dot(d) / LEG
    along = a['support']
    while along > near and (grip + (d * along - up * a['under']) * LEG - shoulder).length > reach:
        along -= 0.01
    return grip + (d * along - up * a['under']) * LEG

def aim_frame(d, up, right):
    """The weapon's frame as an aim clip has it (armature space): x along it, y its top, z to its right."""
    return Matrix((d, up, right)).transposed()

def aim_turn(a, X, d, up, right):
    """An AIMED entry's 'other_turn' as the other hand's turn from rest (armature space) and as arm_to's turn (degrees in
    the chest's frame); None if it has none."""
    if a.get('other_turn') is None:
        return None
    R = aim_frame(d, up, right) @ Euler([math.radians(v) for v in a['other_turn']]).to_matrix()
    turn = (X[CHEST_BONE].to_quaternion().to_matrix().inverted() @ R).to_euler('XYZ')
    return R, [math.degrees(v) for v in turn]

def mirrored(key):
    """A STRIKE key for the right hand turned into the left's: mirrored left to right."""
    out = dict(key)
    for k in ('hand', 'fingers', 'palm', 'other'):
        if k in key:
            out[k] = (-key[k][0], key[k][1], key[k][2])
    for k in ('twist', 'swing'):
        if k in key:
            out[k] = -key[k]
    return out

def hold_rest(w):
    """A WEAPONS 'hand' hold's way along and top with the hand at its rest turn (armature space), as held_turn takes them."""
    ra = (Vector(w.get('aim', (0.0, -1.0, 0.0))).normalized() + Vector((0, 0, -w['drop']))).normalized()
    rt = weapon_top(ra, w['roll'])
    return ra, (rt - rt.dot(ra) * ra).normalized()

def melee_own():
    """The melee weapons whose hold points too far from the generic melee one's (MELEE_SAME) to swing in its clips, so
    they get their own."""
    a0, t0 = hold_rest(WEAPONS['melee'])
    out = []
    for name, w in WEAPONS.items():
        if DRAFTED.get(name, {}).get('kind') != 'melee' or w['line'] != 'hand' or w['hand'] != 'R':
            continue
        a, t = hold_rest(w)
        if math.degrees(a.angle(a0)) > MELEE_SAME or math.degrees(t.angle(t0)) > MELEE_SAME:
            out.append(name)
    return out

def gun_own():
    """The ranged weapons whose hold points too far from their kind's (MELEE_SAME) to swing in its clips, as melee_own: {name:
    its kind}. Thrown ones are left out: they aren't swung (WalkFigures.StrikeOf)."""
    out = {}
    for name, w in WEAPONS.items():
        kind = DRAFTED.get(name, {}).get('kind')
        if kind not in MELEE_GUN or aimed_entry(name) is None or w['line'] != 'hand' or w['hand'] != WEAPONS[kind]['hand']:
            continue
        a0, t0 = hold_rest(WEAPONS[kind])
        a, t = hold_rest(w)
        if math.degrees(a.angle(a0)) > MELEE_SAME or math.degrees(t.angle(t0)) > MELEE_SAME:
            out[name] = kind
    return out

def strike_names():
    """Every strike clip and what it is posed from: {clip: (keys, guard (a DRAFTED name), hold (a WEAPONS name, None bare
    handed), side)}."""
    out = {}
    for kind in ('smash', 'slash', 'stab'):
        out['melee_' + kind] = (MELEE[kind], 'melee', 'melee', 'R')
        out['melee_down_' + kind] = (MELEE_DOWN[kind], 'melee', 'melee', 'R')
        for name in melee_own():
            out['melee_%s_%s' % (kind, name)] = (MELEE[kind], name, name, 'R')
            out['melee_down_%s_%s' % (kind, name)] = (MELEE_DOWN[kind], name, name, 'R')
    out['melee_fist_R'] = (MELEE['fist'], 'unarmed', None, 'R')
    out['melee_fist_L'] = ({k: mirrored(v) for k, v in MELEE['fist'].items()}, 'unarmed', None, 'L')
    out['melee_down_fist_R'] = (MELEE_DOWN['fist'], 'unarmed', None, 'R')
    out['melee_down_fist_L'] = ({k: mirrored(v) for k, v in MELEE_DOWN['fist'].items()}, 'unarmed', None, 'L')
    own = gun_own()
    for name, kind in [(k, k) for k in MELEE_GUN] + list(own.items()):
        for blow in ('butt', 'jab'):
            side = WEAPONS[name]['hand']
            out['melee_%s_%s' % (blow, name)] = (MELEE_GUN[kind][blow], name, name, side)
            out['melee_down_%s_%s' % (blow, name)] = (MELEE_GUN[kind]['down_' + blow], name, name, side)
    for side, name in THROW_GUARD.items():
        out['throw_' + side] = (THROW if side == 'R' else {k: mirrored(v) for k, v in THROW.items()}, name, name, side)
    out['throw_pack'] = (dict(THROW, take=PACK_TAKE), THROW_PACK, THROW_PACK, 'R')
    return out

def palm_turn(side, sign, fingers, facing):
    """The hand's turn from rest that points its fingers along fingers with its palm facing facing (made square to it), as
    hand_on turns it."""
    hn = 'hand.' + side
    d0 = (tail[hn] - head[hn]).normalized()
    n0 = Vector((sign * HAND_PALM[0], HAND_PALM[1], HAND_PALM[2]))
    n0 = (n0 - n0.dot(d0) * d0).normalized()
    n = Vector(facing).normalized()
    f = Vector(fingers)
    f = (f - f.dot(n) * n).normalized()
    return Matrix((f, n, f.cross(n))).transposed() @ Matrix((d0, n0, d0.cross(n0)))

def strike_body(guard, k):
    """A strike's pose for the key k on the guard's drafted stance, all but the arms: the hips lunged forward and lowered
    with the feet kept planted, then the trunk bent and turned, the head taking some of it back."""
    params = dict(STILL, DROP=DRAFT_DROP, LEAN=DRAFT_LEAN, NARROW=DRAFT_NARROW)
    params.update(drafted_entry(guard).get('body', {}))
    params['DROP'] += BREATH_BOB   # as stand_at at the bottom of the breath, so the guard is the drafted loop's first frame
    X, _ = posed_with(params, 0.29)
    ankles = {s: X['foot.' + s] @ head['foot.' + s] for s in ('L', 'R')}
    legs = set()
    for side in ('L', 'R'):
        legs.update(b.name for b in [bones['thigh.' + side]] + list(bones['thigh.' + side].children_recursive))
    M = T(Vector((0, -k['lunge'], -k['drop'])) * LEG)
    X = {n: (v if n in legs else M @ v) for n, v in X.items()}
    clamp = 0.0
    for side in ('L', 'R'):
        clamp = max(clamp, leg_to(side, X[HIPS] @ head['thigh.' + side], ankles[side], FWD, X))
    for bone in (SPINE, CHEST_BONE):
        pivot = X[bone] @ head[bone]
        turn_branch(X, bone, about(pivot, (0, 0, 1), k['twist'] * 0.5) @ about(pivot, (1, 0, 0), k['lean'] * 0.5))
    pivot = X[NECK] @ head[NECK]
    look = k.get('look', STRIKE_LOOK)
    turn_branch(X, NECK, about(pivot, (1, 0, 0), -k['lean'] * look) @ about(pivot, (0, 0, 1), -k['twist'] * look))
    for name, leader in FOLLOW.items():
        X[name] = X[leader]
    return X, clamp

strike_key_cache = {}

def strike_keys(clip):
    """A strike clip's keys from the guard round to it again, each with its body numbers and each hand's wrist (as
    DRAFTED's), turn (in the chest's frame, from rest) and elbow lift. The guard's are read off its drafted pose."""
    keys, guard, hold, side = strike_names()[clip]
    cache = (clip, repr(keys), repr(drafted_entry(guard)), GUARD_TIP.get(guard), STRIKE_LOOK, DRAFT_DROP, DRAFT_LEAN,
             DRAFT_NARROW, BREATH_BOB)
    if cache in strike_key_cache:
        return strike_key_cache[cache]
    other = 'L' if side == 'R' else 'R'
    sign = 1 if side == 'L' else -1
    G, _ = stand_at(0.0, guard)
    chest = G[CHEST_BONE]
    entry = drafted_entry(guard)
    def held(s):
        hn = 'hand.' + s
        place = (chest.inverted() @ (G[hn] @ head[hn]) - head[HIPS]) / LEG
        return place, chest.to_quaternion().inverted() @ G[hn].to_quaternion(), entry.get(s + '_elbow') or 0.0
    g = dict(lean=0.0, twist=0.0, lunge=0.0, drop=0.0, look=STRIKE_LOOK)
    g['hand'], g['turn'], g['elbow'] = held(side)
    g['other'], g['other_turn'], g['other_elbow'] = held(other)
    out = [g]
    for name in (('take',) if 'take' in keys else ()) + ('raised', 'hit', 'through'):
        k = dict(keys[name])
        k['hand'], k['other'] = Vector(k['hand']), Vector(k['other']) if 'other' in k else g['other']
        k.setdefault('look', STRIKE_LOOK)
        if 'fingers' in k:
            k['turn'] = palm_turn(side, sign, k['fingers'], k['palm']).to_quaternion()
        elif 'aim' in k:
            k['turn'] = held_turn(WEAPONS[hold], Vector(k['aim']), k.get('roll', 0.0)).to_quaternion()
        else:
            tip = k['tip'] - GUARD_TIP.get(guard, 0.0)
            k['turn'] = (rot((0, 0, 1), k['swing']) @ rot((1, 0, 0), tip)).to_quaternion() @ g['turn']
        k['elbow'] = k.get('elbow', 0.0)
        k['other_turn'], k['other_elbow'] = g['other_turn'], g['other_elbow']
        out.append(k)
    out.append(g)
    strike_key_cache[cache] = out
    return out

def strike_ease(t, take=None):
    """Which stretch of a strike t (0 .. 1 through it) is in (the wind-up, the blow, the follow-through, back to the
    guard; with take, the wind-up split at it in two) and how far through it, eased: the blow speeding up into the hit
    and slowing after it."""
    at = (0.0,) + ((take,) if take is not None else ()) + tuple(STRIKE_AT) + (1.0,)
    blow = len(at) - 4   # the stretch into the hit
    for i in range(len(at) - 1):
        if t <= at[i + 1] or i == len(at) - 2:
            u = min(max((t - at[i]) / (at[i + 1] - at[i]), 0.0), 1.0)
            return i, u * u if i == blow else 1 - (1 - u) ** 2 if i == blow + 1 else smooth(u)

def strike_at(t, clip):
    """A strike clip (strike_names) at t (0 .. 1 through it, the guard at both ends). Returns the pose and the worst
    overreach."""
    keys, guard, hold, side = strike_names()[clip]
    other = 'L' if side == 'R' else 'R'
    i, u = strike_ease(t, keys['take']['at'] if 'take' in keys else None)
    a, b = strike_keys(clip)[i:i + 2]
    k = {n: a[n] + (b[n] - a[n]) * u for n in ('lean', 'twist', 'lunge', 'drop', 'look', 'elbow', 'other_elbow')}
    X, clamp = strike_body(guard, k)
    chest = X[CHEST_BONE]
    for s, pre, at in ((side, '', 'hand'), (other, 'other_', 'other')):
        sign = 1 if s == 'L' else -1
        q0, q1 = a[pre + 'turn'], b[pre + 'turn']
        if q0.dot(q1) < 0:
            q1 = -q1
        q = q0.slerp(q1, u)
        wrist = chest @ (head[HIPS] + a[at].lerp(b[at], u) * LEG)
        if pre and keys.get('both'):
            continue
        clamp = max(clamp, arm_to(s, sign, wrist, X, turn=[math.degrees(v) for v in q.to_euler('XYZ')], lift=k[pre + 'elbow']))
        if not pre:
            held = wrist, chest.to_3x3() @ q.to_matrix()
    if keys.get('both'):
        # The other hand stays on the weapon wherever the blow takes it, as the aim has it there.
        wrist, R = held
        w, e = WEAPONS[hold], aimed_entry(hold)
        hn, osign = 'hand.' + side, 1 if other == 'L' else -1
        fist = wrist + R @ (tail[hn] - head[hn]).normalized() * HAND_GRIP * LEG / MODEL_LEG
        ra, rt = hold_rest(w)
        d, up = R @ ra, R @ rt
        where = support_hold(X, other, held_grip(w, fist, R), d, up, e)
        clamp = max(clamp, grip_handle(other, osign, where, d, up, e['wrap'] * osign, X, lift=e.get('other_elbow')))
    close_hand(X, side, 1 if side == 'L' else -1)   # round the weapon, or a fist
    return X, clamp

def draw_names():
    """Every draw clip: {clip: (slot, type)}, one per holster slot and weapon type that goes in it."""
    return {'draw_%s_%s' % (slot, t): (slot, t) for slot, h in HOLSTERS.items() for t in TYPE_SCALE
            if TYPE_HOLSTER.get(t) == h['size'] and HOLSTER_WEAPON.get(t) in WEAPONS}

def weapon_length(name):
    """How long a weapon's sprite is drawn on a grown figure (model units), a sprite of one cell, as WalkFigures.DrawSlab
    sizes it."""
    return weapon_scale(name) / (1.0 if weapon_type(name) == 'grenade' else GROUND_RATIO) / GAME_SCALE

def holster_frame(slot, X, melee=False):
    """Where a slot holds its weapon in the pose X (armature space), as SkinnedFigure.HolsterAt: its place, the way the
    weapon runs and its top, without the game's push past garments."""
    B = X[HOLSTERS[slot]['bone']]
    at, aim, roll = holster_place(slot, melee)
    aim = Vector(aim).normalized()
    a = (B.to_3x3() @ aim).normalized()
    top = B.to_3x3() @ weapon_top(aim, roll)
    top = (top - top.dot(a) * a).normalized()
    if melee and 'melee_aim' not in HOLSTERS[slot]:
        # No melee place: the weapon (taken to have MELEE_SLOT_OFFSET) turned by its offset so its blade runs along the
        # aim's line, hilt first, as SkinnedFigure.HolsterAt's spin has it.
        spin = math.radians(MELEE_SLOT_OFFSET + 180.0)
        a, top = a * math.cos(spin) + top * math.sin(spin), top * math.cos(spin) - a * math.sin(spin)
    return B @ (head[HIPS] + Vector(at) * LEG / MODEL_LEG), a, top

def handle_of(slot, name, X):
    """The wrist's place and the hand's turn from rest (armature space) that take hold of the weapon name (a WEAPONS entry
    on the 'hand' line) just as its hold has it, where it sits in the slot in the pose X: held_grip turned round."""
    w, h = WEAPONS[name], HOLSTERS[slot]
    at, a, top = holster_frame(slot, X, is_melee(name))
    ra, rt = hold_rest(w)
    R = Matrix((a, top, a.cross(top))).transposed() @ Matrix((ra, rt, ra.cross(rt)))
    size = LEG / MODEL_LEG   # armature units per model unit
    grip = at + a * (h.get('behind', 0.0) - w['behind']) * weapon_length(name) * size
    fist = grip - held_grip(w, Vector((0, 0, 0)), R)
    hn = 'hand.' + w['hand']
    return fist - R.to_3x3() @ (tail[hn] - head[hn]).normalized() * HAND_GRIP * size, R

def draw_body(guard, k):
    """A draw's pose for the key k on the guard's drafted stance, all but the arm: as strike_body, then bent sideways by
    k['side'] (degrees, + to the left), the head taking most of that back."""
    X, clamp = strike_body(guard, dict(k, lunge=0.0, drop=0.0))
    if k['side']:
        for bone in (SPINE, CHEST_BONE):
            turn_branch(X, bone, about(X[bone] @ head[bone], (0, 1, 0), k['side'] * 0.5))
        turn_branch(X, NECK, about(X[NECK] @ head[NECK], (0, 1, 0), -k['side'] * DRAW_LOOK))
        for name, leader in FOLLOW.items():
            X[name] = X[leader]
    return X, clamp

def drop_shoulder(X, side, wrist, most):
    """Turns the shoulder about its own head toward wrist by as much as the arm needs to reach it, up to most degrees.
    Returns the degrees turned and how far the wrist is then still out of reach."""
    sh, ua = 'shoulder.' + side, 'upper_arm.' + side
    reach = sum((tail[b + side] - head[b + side]).length for b in ('upper_arm.', 'forearm.')) - 1e-3
    pivot = X[sh] @ head[sh]
    joint = X[sh] @ head[ua]
    axis = (joint - pivot).cross(wrist - pivot)
    if axis.length < 1e-6:
        return 0.0, max(0.0, (wrist - joint).length - reach)
    best, gap = 0.0, (wrist - joint).length - reach
    if gap <= 0.0:
        return 0.0, 0.0
    deg = 0.0
    while deg < most and gap > 0.0:
        deg = min(most, deg + 1.0)
        gap = (wrist - (about(pivot, axis, deg) @ joint)).length - reach
        best = deg
    X[sh] = about(pivot, axis, best) @ X[sh]
    return best, max(0.0, gap)

draw_key_cache = {}

def draw_keys(clip):
    """A draw clip's working: 'guard', the weapon it is posed for, and 'side', the hand; 'hang', 'grab' and 'end', each that
    hand's wrist (as DRAFTED's: leg lengths from the middle of the hips, in the chest's frame) and 'turn' (in the chest's
    frame, from rest) hanging, on the handle and in the guard; 'come' and 'go', the wrist's way in to the handle and out
    from it; 'body', the trunk at the handle; 'shoulder', the most its shoulder drops there; 'gap', the leg lengths left
    out of reach, 'elbow', the guard's lift."""
    slot, kind = draw_names()[clip]
    name, h = HOLSTER_WEAPON[kind], HOLSTERS[slot]
    size = DRAW_SIZES[h['size']]
    cache = (clip, repr(h), repr(WEAPONS[name]), repr(size), repr(drafted_entry(name)), DRAW_TWIST, DRAW_LEAN, DRAW_SIDE,
             DRAW_SHOULDER, DRAW_BEND_COST, DRAW_LOOK, TYPE_SCALE.get(kind), STRIKE_LOOK, DRAFT_DROP, DRAFT_LEAN, DRAFT_NARROW)
    if cache in draw_key_cache:
        return draw_key_cache[cache]
    side = WEAPONS[name]['hand']
    # A slot on an arm: the clip leaves the other arm to whatever is under it (DRAW_UPPER), so as the hand reaches it that
    # arm hangs on the trunk as it does standing, not raised as in the guard.
    hanging = stand_at(0.0)[0]
    def slot_pose(X):
        if not h['bone'].startswith(('shoulder.', 'upper_arm.', 'forearm.', 'hand.')):
            return X
        return dict(X, **{h['bone']: X[CHEST_BONE] @ hanging[CHEST_BONE].inverted() @ hanging[h['bone']]})
    def place_of(X, wrist):
        return (X[CHEST_BONE].inverted() @ wrist - head[HIPS]) / LEG
    def turn_of(X, q):
        return X[CHEST_BONE].to_quaternion().inverted() @ q
    def held(X):
        hn = 'hand.' + side
        return place_of(X, X[hn] @ head[hn]), turn_of(X, X[hn].to_quaternion())
    def steps(most, by):
        n = int(most / by)
        return [i * by for i in range(-n, n + 1)]
    # The least bending that reaches the handle: each number in turn, until none of them gets it any nearer.
    def cost(k):
        X, _ = draw_body(name, k)
        wrist, _ = handle_of(slot, name, slot_pose(X))
        _, gap = drop_shoulder(X, side, wrist, DRAW_SHOULDER)
        return (gap / LEG) ** 2 + DRAW_BEND_COST * (k['twist'] ** 2 + k['lean'] ** 2 + k['side'] ** 2) / 900.0
    k = dict(lean=0.0, twist=0.0, side=0.0)
    for _ in range(3):
        for n, most in (('twist', DRAW_TWIST), ('lean', DRAW_LEAN), ('side', DRAW_SIDE)):
            k[n] = min(steps(most, 5.0), key=lambda v: cost(dict(k, **{n: v})))
    X, _ = draw_body(name, k)
    wrist, R = handle_of(slot, name, slot_pose(X))
    bone = slot_pose(X)[h['bone']].to_3x3()
    chest = X[CHEST_BONE].to_3x3().inverted()
    out = (bone @ Vector(h.get('out', (0.0, 1.0, 0.0)))).normalized()
    a = holster_frame(slot, slot_pose(X), is_melee(name))[1]
    grab = place_of(X, wrist)
    keys = dict(guard=name, side=side, body=k, grab=grab, grab_turn=turn_of(X, R.to_quaternion()),
                come=grab + chest @ out * size['approach'], go=grab + chest @ (out * size['out'] - a * size['along']))
    deg, gap = drop_shoulder(X, side, wrist, DRAW_SHOULDER)
    keys['shoulder'], keys['gap'] = deg, gap / LEG
    keys['hang'], keys['hang_turn'] = held(hanging)
    keys['end'], keys['end_turn'] = held(stand_at(0.0, name)[0])
    keys['elbow'] = drafted_entry(name).get(side + '_elbow') or 0.0
    draw_key_cache[cache] = keys
    return keys

def round_body(a, c, b, u, side):
    """The wrist from a to b by way of c (a quadratic curve, u 0 .. 1), in the chest's frame, going round the body rather
    than through it: its height, its way round and its distance from the body's middle each curved alone, the way round
    measured from that hand's side, so it never goes past the far side."""
    def round_of(v):
        way = math.atan2(v.y, v.x) - (0.0 if side == 'L' else math.pi)
        return Vector((math.hypot(v.x, v.y), (way + math.pi) % (2 * math.pi) - math.pi, v.z))
    p0, p1, p2 = round_of(a), round_of(c), round_of(b)
    p = p0 * (1 - u) ** 2 + p1 * 2 * u * (1 - u) + p2 * u ** 2
    way = p.y + (0.0 if side == 'L' else math.pi)
    return Vector((p.x * math.cos(way), p.x * math.sin(way), p.z))

def draw_at(t, clip):
    """A draw clip (draw_names) at t (frames at t = 0, 1/n .. (n-1)/n as export_loop takes them, the last the guard): the
    hand from hanging to the handle at DRAW_AT and on to the guard, the fingers closing on the way in and letting go into
    the guard's open hold. Returns the pose and the worst overreach."""
    g = draw_keys(clip)
    t = min(1.0, t * DRAW_FRAMES / (DRAW_FRAMES - 1))
    side, sign = g['side'], 1 if g['side'] == 'L' else -1
    if t <= DRAW_AT:
        u = smooth(t / DRAW_AT)
        share = u
        place = round_body(g['hang'], g['come'], g['grab'], u, side)
        q0, q1, v = g['hang_turn'], g['grab_turn'], min(1.0, u / 0.8)
        fist = u * u
        lift = 0.0
    else:
        u = smooth((t - DRAW_AT) / (1.0 - DRAW_AT))
        share = 1.0 - u
        place = round_body(g['grab'], g['go'], g['end'], u, side)
        keep = DRAW_SIZES[HOLSTERS[draw_names()[clip][0]]['size']]['turn_from']
        q0, q1, v = g['grab_turn'], g['end_turn'], smooth(max(0.0, (u - keep) / (1.0 - keep)))
        fist = 1.0 - smooth(max(0.0, (u - 0.7) / 0.3))
        lift = g['elbow'] * u
    X, clamp = draw_body(g['guard'], {n: x * share for n, x in g['body'].items()})
    wrist = X[CHEST_BONE] @ (head[HIPS] + place * LEG)
    drop_shoulder(X, side, wrist, g['shoulder'] * share)
    if q0.dot(q1) < 0:
        q1 = -q1
    turn = q0.slerp(q1, v).to_euler('XYZ')
    clamp = max(clamp, arm_to(side, sign, wrist, X, turn=[math.degrees(n) for n in turn], lift=lift))
    close_hand(X, side, sign, fist)
    return X, clamp

def loop_frames(seconds):
    """How many frames a loop timed by the clock is baked with."""
    return max(1, round(seconds * IDLE_FPS))

def bake_loop(name, at, n):
    """Keys at(t) (a pose and its overreach, t 0 .. 1 round the loop) as an action of n frames from 1, the last a copy
    of the first so it loops."""
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    if not arm.animation_data:
        arm.animation_data_create()
    arm.animation_data.action = act
    worst = 0.0
    for f in range(n + 1):
        X, clamp = at((f % n) / n)
        worst = max(worst, clamp)
        apply_pose(X, f + 1)
    return act, worst

def bake(name='Walk'):
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    if not arm.animation_data:
        arm.animation_data_create()
    arm.animation_data.action = act
    worst = 0.0
    for f in range(FRAMES + 1):
        X, clamp = pose_at((f % FRAMES) / FRAMES)
        worst = max(worst, clamp)
        apply_pose(X, f + 1)
    return act, worst

def fcurves_of(action):
    try:
        return list(action.fcurves)
    except AttributeError:   # layered actions (Blender 4.4+)
        out = []
        for layer in action.layers:
            for strip in layer.strips:
                for bag in strip.channelbags:
                    out.extend(bag.fcurves)
        return out

if __name__ == '__main__':   # run by Blender; imported (export_frames.py) it only defines the poses
    act, worst = bake()
    for fc in fcurves_of(act):
        fc.modifiers.new('CYCLES')
        for k in fc.keyframe_points:
            k.interpolation = 'LINEAR'
    sc = bpy.context.scene
    sc.frame_start, sc.frame_end = 1, FRAMES
    sc.render.fps = 24
    act['ground_per_cycle'] = ground_per_cycle()
    print('WALK baked; worst leg overreach clamp', round(worst, 4), 'ground per cycle', round(ground_per_cycle(), 3))
    out = sys.argv[sys.argv.index('--') + 1]
    bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)
