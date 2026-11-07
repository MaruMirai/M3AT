# Takes one loop of kneeling at work on the ground out of a glTF animation (Meshy's animation library on the chibi, a
# Mixamo rig) into pluck_clip.json, which walk.pluck_at plays on the metarig.
#   blender -b --python anim_scripts/import_pluck.py -- <glb> [action [first last]] [--as name] [--afloat]
# --as name writes <name>_clip.json instead, for another clip of the same kind (tend: walk.tend_at, kneeling by a
# patient, taken from the same kneel as `--as tend _01a178c1_b294_72a2_ba12_4d79ac733a8b 16 87`, which stops before
# the kneel's last stretch, where the left hand digs forward).
# --afloat is for a clip with no floor (swim: walk.swim_at, the front crawl, `--as swim Swim_Forward --afloat`): the
# hips' place is written from an even pace along the ground the cycle covers, less its mean, so the body keeps its rest
# place on average.
# --planted is for a clip that gets somewhere before the part taken (vomit: walk.vomit_at, bent over at the end of
# Meshy's Catching_Breath, `--as vomit Catching_Breath 124 167 --planted`): the hips' place is written from the feet's
# mean place over the cycle, against the hips' place from the feet at rest, rather than from the hips' rest place.
# --once is for a clip played once through rather than round (getup: walk.getup_at, rising from lying on the back,
# Meshy's Arise up to where it is standing, before it steps back, `--as getup Arise 0 32 --once`): no leftover turn
# spread over it, its last frame is the last one taken, and the hips are written so that frame stands over its feet as
# they stand at rest, with the ground it covers spread out over it, so it gets up in place. Their height is from the
# lowest joint of each frame (lying, that is not a toe); the game rests it on its lowest point anyway.
# --fall is for a clip played once from standing to lying (knockdown, die: walk.fall_at, `--as die dying_backwards 6 52
# --fall`): as --once, but the hips are written so the first frame stands over its feet as they stand at rest, and the
# ground it covers is kept, as it falls over away from where it stood.
# --mirror flips the clip left for right (eat_stand: Meshy's Stand_and_Drink, drunk with the left hand, `--as eat_stand
# Stand_and_Drink 0 211 --mirror`, so it eats with the right as eat_sit does): each metarig bone follows the glTF bone
# of the other side, its turn and every place reflected across the figure's middle (x).
# action is the glTF animation to take (without it, the first whose name is none of SKIP: Meshy leaves the kneel
# unnamed). first .. last are its frames for one cycle (the frame after the last is the first again); without them the
# cycle that closes best is used.
#
# Each bone is written as its turn from its own rest, in armature space, as import_crawl.py writes the crawl. The hips'
# place is written from their rest place, in leg lengths, its height taken from the floor (the lowest the toes go in the
# clip) so the figure kneels as low as it was made to; walk.pluck_at sets it on the metarig's floor again.
import bpy, json, math, os, sys
from mathutils import Matrix, Quaternion, Vector

GLB_ARMATURE = 'target_character'
SKIP = ('Running', 'Walking', 'Crawl', 'Kneel_on_One_Knee')   # the other clips Meshy puts in the same file
CYCLE = (60, 140)      # shortest and longest cycle tried, in frames
M = 'mixamorig:'
# metarig bone <- glTF bone. Bones of the metarig not listed turn with their parent.
MAP = {'spine': 'Hips', 'spine.001': 'Spine', 'spine.002': 'Spine1', 'spine.003': 'Spine2', 'spine.004': 'Neck',
       'spine.006': 'Head',
       'shoulder.L': 'LeftShoulder', 'upper_arm.L': 'LeftArm', 'forearm.L': 'LeftForeArm', 'hand.L': 'LeftHand',
       'hand.L.001': 'LeftHandMiddle1', 'hand.L.002': 'LeftHandMiddle2', 'thumb.L': 'LeftHandThumb1',
       'thumb.L.001': 'LeftHandThumb2',
       'shoulder.R': 'RightShoulder', 'upper_arm.R': 'RightArm', 'forearm.R': 'RightForeArm', 'hand.R': 'RightHand',
       'hand.R.001': 'RightHandMiddle1', 'hand.R.002': 'RightHandMiddle2', 'thumb.R': 'RightHandThumb1',
       'thumb.R.001': 'RightHandThumb2',
       'thigh.L': 'LeftUpLeg', 'shin.L': 'LeftLeg', 'foot.L': 'LeftFoot', 'toe.L': 'LeftToeBase',
       'thigh.R': 'RightUpLeg', 'shin.R': 'RightLeg', 'foot.R': 'RightFoot', 'toe.R': 'RightToeBase'}
# Bones whose rest pose points well away from the metarig's (the glTF rig's ankle stands almost straight over the ball
# of the foot, its hands and neck are turned another way), by the joint each points to: the way to it at rest is
# written ('rest_ways'), so walk.mocap_at can turn the metarig's bone from its own rest way to the clip's first.
NEXT = {'foot.L': 'toe.L', 'foot.R': 'toe.R', 'hand.L': 'hand.L.001', 'hand.R': 'hand.R.001', 'spine.004': 'spine.006'}
TOES = ('LeftToeBase', 'RightToeBase')
FEET = ('LeftFoot', 'RightFoot')
# The trunk's joints, hips to neck. The glTF rig's trunk is far shorter than the metarig's, and bends most in its short
# top bone (Spine2), so its turns copied bone for bone would throw the metarig's long upper chest, and the neck with it,
# well forward of where the clip has it. Its line is written as well, for the metarig's trunk to follow by shape.
TRUNK = ('Hips', 'Spine', 'Spine1', 'Spine2', 'Neck')

args = sys.argv[sys.argv.index('--') + 1:]
clip = 'pluck'
if '--as' in args:
    k = args.index('--as')
    clip = args[k + 1]
    del args[k:k + 2]
afloat = '--afloat' in args
if afloat:
    args.remove('--afloat')
planted = '--planted' in args
if planted:
    args.remove('--planted')
once = '--once' in args
if once:
    args.remove('--once')
mirror = '--mirror' in args
if mirror:
    args.remove('--mirror')
fall = '--fall' in args
if fall:
    args.remove('--fall')
    once = True
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), clip + '_clip.json')
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=args[0])
# (some of Meshy's files name it otherwise, SmartRigArmature: then the file's one armature)
rig = bpy.data.objects.get(GLB_ARMATURE) or next(o for o in bpy.data.objects if o.type == 'ARMATURE')
sc = bpy.context.scene
if len(args) >= 2:
    act = bpy.data.actions[args[1]]
else:
    act = next(a for a in bpy.data.actions if not any(a.name.startswith(s) for s in SKIP))
rig.animation_data.action = act
first_frame, last_frame = (int(v) for v in act.frame_range)
bones = rig.data.bones
mw = rig.matrix_world
# Places are read through mp: mirrored, reflected across x (the glTF rig's left is +x).
mp = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0)) @ mw if mirror else mw
leg = bones[M + 'LeftUpLeg'].length + bones[M + 'LeftLeg'].length

def sample(f):
    sc.frame_set(f)
    turns = {}
    for name, src in MAP.items():
        if mirror:
            src = src.replace('Left', '\0').replace('Right', 'Left').replace('\0', 'Right')
        pb = rig.pose.bones[M + src]
        q = (mw.to_3x3() @ pb.matrix.to_3x3() @ bones[M + src].matrix_local.to_3x3().inverted()).to_quaternion()
        # A turn reflected across x keeps its x part and flips the other two.
        turns[name] = Quaternion((q.w, q.x, -q.y, -q.z)) if mirror else q
    toe = min((mp @ rig.pose.bones[M + t].head).z for t in TOES)
    line = [mp @ rig.pose.bones[M + j].head for j in TRUNK]
    feet = sum((mp @ rig.pose.bones[M + j].head for j in FEET), Vector()) / len(FEET)
    if once:
        toe = min((mp @ rig.pose.bones[M + src].head).z for src in MAP.values())
    return turns, line[0], toe, line, feet

frames = [sample(f) for f in range(first_frame, last_frame + 1)]

def mismatch(a, b):
    # (the short way round: a turn and its negative quaternion are the same turn, which .angle gives as a whole one)
    return math.sqrt(sum(min(d, 2 * math.pi - d) ** 2 for d in (a[0][n].rotation_difference(b[0][n]).angle for n in MAP))
                     / len(MAP))

if len(args) >= 4:
    a, b = int(args[2]) - first_frame, int(args[3]) + 1 - first_frame
else:
    tries = [(mismatch(frames[a], frames[b]), a, b) for a in range(len(frames))
             for b in range(a + CYCLE[0], min(a + CYCLE[1], len(frames)))]
    _, a, b = min(tries)
n = b - a
# The floor: the lowest the balls of the feet go in the cycle, against their height at rest.
floor = min(fr[2] for fr in frames[a:b])
rest_hips = mp @ bones[M + 'Hips'].head_local
rest_floor = min((mp @ bones[M + t].head_local).z for t in TOES)
rest_line = [mp @ bones[M + j].head_local for j in TRUNK]
span = sum((b - a).length for a, b in zip(rest_line, rest_line[1:]))
travel = frames[b][1] - frames[a][1]
# Afloat, the hips from where an even pace would have them, less that over the whole cycle.
drift = [frames[a + i][1] - frames[a][1] - travel * (i / n) for i in range(n)]
mean = sum(drift, Vector()) / n
# Planted, the hips' rest place moved to stand over where the feet are in the cycle.
if planted:
    rest_feet = sum((mp @ bones[M + j].head_local for j in FEET), Vector()) / len(FEET)
    feet = sum((fr[4] for fr in frames[a:b]), Vector()) / n
    rest_hips = rest_hips + Vector((feet.x - rest_feet.x, feet.y - rest_feet.y, 0.0))
out = {'source': os.path.basename(args[0]), 'action': act.name, 'first': a + first_frame, 'last': b - 1 + first_frame,
       'frames': n, 'fps': sc.render.fps, 'ground': round(travel.length / leg, 6), 'turns': {name: [] for name in MAP}, 'hips': [],
       # Each trunk joint's share of the way up the trunk at rest, then per frame its place from the hips joint in
       # trunk lengths (along it at rest).
       'trunk_at': [round(sum((b - a).length for a, b in zip(rest_line[:k], rest_line[1:k + 1])) / span, 6)
                    for k in range(len(TRUNK))],
       'trunk': []}
def side_of(src):
    return src.replace('Left', '\0').replace('Right', 'Left').replace('\0', 'Right') if mirror else src
out['rest_ways'] = {name: {'to': to, 'way': [round(v, 6) for v in (mp @ bones[M + side_of(MAP[to])].head_local
                                                                    - mp @ bones[M + side_of(MAP[name])].head_local)
                                                                   .normalized()]}
                    for name, to in NEXT.items()}
# Once through, the hips' rest place moved to stand over the feet of the last frame, and the ground covered from the
# first frame to the last spread out over it.
if once:
    rest_feet = sum((mp @ bones[M + j].head_local for j in FEET), Vector()) / len(FEET)
    last = frames[a if fall else b - 1]
    rest_hips = rest_hips + Vector((last[4].x - rest_feet.x, last[4].y - rest_feet.y, 0.0))
    travel = Vector() if fall else last[1] - frames[a][1]
    travel.z = 0.0
for i in range(n):
    t = i / n
    if once:
        t = (i - (n - 1)) / (n - 1) if n > 1 else 0.0   # -1 at the first frame .. 0 at the last: the place's share left
    turns, hips, low, line, _ = frames[a + i]
    for name in MAP:
        # The loop's leftover turn (from the last frame on to the first) is taken out a share at a time.
        fix = frames[a][0][name] @ frames[b][0][name].inverted()
        q = turns[name] if once else Quaternion().slerp(fix, t) @ turns[name]
        out['turns'][name].append([round(v, 6) for v in q])
    if afloat:
        off = drift[i] - mean
    else:
        off = hips - rest_hips
        off.z = (hips.z - (low if once else floor)) - (rest_hips.z - rest_floor)
        # The hips' leftover move spread over the loop too.
        off -= travel * t
    out['hips'].append([round(v / leg, 6) for v in off])
    first, last = frames[a][3], frames[b][3]
    if once:
        first = last = frames[a][3]
    out['trunk'].append([[round(v / span, 6) for v in (p - line[0]) - ((last[k] - last[0]) - (first[k] - first[0])) * t]
                         for k, p in enumerate(line)])
with open(OUT, 'w') as f:
    json.dump(out, f)
print('%s frames %d .. %d of %s in %s (%d frames, %.2f s, %.3f leg lengths covered), seam %.1f degrees before closing'
      % (clip.upper(), out['first'], out['last'], act.name, out['source'], n, n / sc.render.fps, out['ground'],
         math.degrees(mismatch(frames[a], frames[b]))))
