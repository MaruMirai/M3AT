# Turns a tracker take (record.py's CSV) into <name>_clip.json beside walk.py, which walk.mocap_at plays as it plays the
# clips import_pluck.py takes out of glTFs.
#   blender -b Models/pawnHumanRig.blend --python anim_scripts/mocap/import_take.py -- <take.csv> [from to] [--as name]
#       [--once] [--loop shortest longest] [--fps n] [--wrist share] [--tool [--calibrate from to] [--hands right left]
#       [--swap]]
# from .. to (seconds into the take) is the stretch taken; without them, from just after the rest pose to where the stop
# gesture began. --loop finds the cycle in that stretch that closes best, shortest .. longest seconds long; without it the
# stretch is taken whole, and unless --once the turn left between its last frame and its first is spread over it so it
# closes (--once: played once through, a getup or a fall). --as names the output (take's clip name by default).
#
# How it is retargeted: the performer's rest pose, the stillest second near the take's start (arms down, facing the way
# the wands point), is taken to be the chibi's standing pose (walk.stand_pose). Each tracker's turn from there is laid on
# the chibi's standing turn of the bone it rides: the hips tracker the hips, the chest tracker the chest, the head
# tracker the head, each foot tracker its foot, each wand its hand; the bones between take a share of both (the waist
# half the hips' and half the chest's, the neck half the chest's and half the head's), and every other bone keeps its
# standing turn on its parent. The legs and arms are placed by IK: the hips and each ankle and wrist moved from where the
# chibi stands as far as the performer's moved, in leg lengths (the arms in arm lengths, from the shoulder), the knee
# bent the way the performer's is (in the plane across the foot's ankle hinge, bent forward) and the elbow toward the
# elbow tracker. A take with knee trackers instead (worn on the outside of the shins just below the knee; take.assign
# tells them apart by height) bends each knee toward its tracker's knee joint (the knee at rest, on the line from hip
# to ankle, ridden on that tracker), and with no elbow trackers each elbow points ELBOW_WAY in the chest's frame. The
# hips tracker may be worn anywhere on the belt: only its turn since the rest pose is used, and the pelvis rides it
# from wherever it sat then (only --tool takes, which take forward from it, need it at the back). No measurement of the performer is needed beyond rough proportions of their height (the head tracker),
# so the chibi's short limbs and big head take nothing odd from a grown body.
#
# A tracker lost for a stretch (the hips tracker folded into the belly in a squat) is carried along with its anchor
# (ANCHOR: the hips with the chest, the head with the chest ...), how it sits on it blended from just before the gap to
# just after, so it moves as the body did rather than freezing or sliding straight across.
#
# --tool: the two wands are taped along the handle of a tool held in both hands (the pick of the mining takes), not held.
# tool_head is the wand nearer the tool's head, tool_grip the other; the handle runs along the line through them.
# Forward is then from the hips tracker (at the back of the belt) to the chest tracker, as the wands point anywhere.
# With --calibrate from to (seconds into the take, while the tool is held upright in front with its head's point
# straight forward), tool_head is the higher wand there and the point's way in its frame is forward there; without it,
# tool_head is the lower wand at rest (the head on the floor) and the point's way is across the handle the way the head
# moves on the downswing, where the point leads (TOOL_EDGE_SPEED). With --hands right left (metres), each fist is that
# far down the handle (away from the head) from its own wand, the right from tool_head and the left from tool_grip (the
# wands taped just above each hand; --swap: the right from tool_grip and the left from tool_head, for a take with the tool
# held head-down, as the spade was, the right hand at its top); without it, both ride tool_grip, RIGHT_BELOW and
# RIGHT_BELOW + LEFT_BELOW down. The
# clip gets a 'tool' entry per frame for walk.py to close the chibi's hands round its own tool along the recorded one:
# the right fist (from the right shoulder in arm lengths, as a wrist is placed), the handle's way toward the head, the
# way its point faces and each elbow tracker's way from the middle of its arm, all in the chest's rest frame (so the
# whole-figure moves walk.py makes leave them where they are on the body).
#
# A lost foot tracker is filled straight across its gap, as a foot is lost mostly while the other steps and it stays
# planted (the mining take's turned only 4 to 11 cm); carried with the hips, it slid as the body leant.
import bpy, json, math, os, sys
import numpy as np
from mathutils import Matrix, Quaternion, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))
import walk
from take import assign, facing, read

FPS = 30               # frames a second written
REST_LONG = 0.8        # seconds of rest pose measured
REST_SEARCH = (1.0, 8.0)   # where in the take it is looked for (seconds), the arms down from the start gesture
# What a lost tracker is carried along with, in turn of preference.
ANCHOR = {'hips': ('chest', 'foot_L'), 'chest': ('hips', 'head'), 'head': ('chest', 'hips'),
          'elbow_L': ('chest', 'hand_L'), 'elbow_R': ('chest', 'hand_R'), 'hand_L': ('elbow_L', 'chest'),
          'hand_R': ('elbow_R', 'chest'), 'foot_L': (), 'foot_R': (), 'knee_L': ('foot_L', 'hips'),
          'knee_R': ('foot_R', 'hips')}
# Rough proportions of a grown body by its height (Drillis and Contini), for the joints the trackers don't sit on.
HEAD_ABOVE = 0.10      # metres from the head tracker up to the top of the head
UPPER_ARM, FOREARM = 0.186, 0.146
THIGH, SHIN = 0.245, 0.246
# Without elbow trackers (a knee tracker take), the way each elbow points from the shoulder-to-wrist line, in the
# chest's frame (x out to that side, y back, z up): down, a little out and back, as an arm hangs or reaches.
ELBOW_WAY = (0.3, 0.2, -1.0)
HIP_HEIGHT = 0.53      # the hip joints' height, by height
HIP_HALF = 0.085       # metres from the pelvis's middle to each hip joint
SHOULDER_HEIGHT = 0.818
SHOULDER_HALF = 0.115  # by height
ANKLE_HEIGHT = 0.08    # metres
FOOT_TRACKER_HEIGHT = 0.05   # metres a foot tracker on the laces sits over the floor, standing (the first takes, with
                             # SteamVR's floor right, read 0.04 to 0.06)
ANKLE_BEHIND = 0.06    # metres the ankle is behind a foot tracker worn on the laces
# The wrist in a wand's own frame (SteamVR: +y up the ring, +z back toward the holder), metres.
WRIST_IN_WAND = (0.0, -0.03, 0.10)
FOREARM_TWIST = 0.6    # share of the hand's twist on the forearm that the forearm takes, as the walk's arms do
# Share of the wrist's bend (the hand's turn on the forearm away from how the chibi stands) the chibi's hand takes. A wand
# is held in a fist with its ring above the thumb, so reaching it to the ground cocks the wrist far more than a bare hand
# would: 60 to 100 degrees in the planting take, which on the chibi reads as a hand folded in.
WRIST_SHARE = 0.5
# --tool: metres from the grip wand down the handle (away from the head) to the middle of the right fist, and from there
# on to the middle of the left fist.
RIGHT_BELOW = 0.07
LEFT_BELOW = 0.22
TOOL_EDGE_SPEED = 1.5  # metres a second the tool's head moves across its handle at the least, for the point's way

args = sys.argv[sys.argv.index('--') + 1:]
def option(name, n):
    if name not in args:
        return None
    k = args.index(name)
    values = args[k + 1:k + 1 + n]
    del args[k:k + 1 + n]
    return values
name = option('--as', 1)
wrist = option('--wrist', 1)
WRIST_SHARE = float(wrist[0]) if wrist else WRIST_SHARE
loop = option('--loop', 2)
fps = option('--fps', 1)
FPS = int(fps[0]) if fps else FPS
once = option('--once', 0) is not None
tool = option('--tool', 0) is not None
calibrate = option('--calibrate', 2)
calibrate = (float(calibrate[0]), float(calibrate[1])) if calibrate else None
hands = option('--hands', 2)
hands = (float(hands[0]), float(hands[1])) if hands else None
swap = option('--swap', 0) is not None
path = args[0]
span = (float(args[1]), float(args[2])) if len(args) >= 3 else None
header, devices = read(path)
name = name[0] if name else header.get('clip', 'take')

# ---- the samples, one time line for every device

times = next(iter(devices.values()))[0]
N = len(times)
kinds = {s: header['devices'].get(s, ('tracker', '?'))[0] for s in devices}

def stillest():
    """The start of the stillest REST_LONG seconds in REST_SEARCH with every device tracked: the rest pose."""
    rate = 1.0 / np.median(np.diff(times))
    w = int(REST_LONG * rate)
    speed = np.zeros(N)
    tracked = np.ones(N, bool)
    for s, (t, p, q, ok) in devices.items():
        speed[1:] += np.linalg.norm(np.diff(p, axis=0), axis=1)
        tracked &= ok
    best, at = None, None
    for i in range(N - w):
        if not (REST_SEARCH[0] <= times[i] and times[i + w] <= REST_SEARCH[1]) or not tracked[i:i + w].all():
            continue
        score = speed[i + 1:i + w].sum()
        if best is None or score < best:
            best, at = score, i
    if at is None:
        sys.exit('IMPORT no stretch with every device tracked between %.1f and %.1f s for the rest pose' % REST_SEARCH)
    return at, at + w

rest_a, rest_b = stillest()
print('IMPORT rest pose %.2f .. %.2f s' % (times[rest_a], times[rest_b]))
rest = slice(rest_a, rest_b)
trackers = {s: d[1][rest].mean(0) for s, d in devices.items() if kinds[s] == 'tracker'}
wands = {s: d[1][rest].mean(0) for s, d in devices.items() if kinds[s] == 'wand'}
parts, problems = assign(trackers, wands, None if tool else {s: devices[s][2][rest_b - 1] for s in wands})
if problems:
    sys.exit('IMPORT ' + '; '.join(problems))
knees = 'knee_L' in parts.values()     # knee trackers, no elbow trackers
if knees and tool:
    sys.exit('IMPORT --tool needs elbow trackers (this take has knee trackers)')
if knees:
    print('IMPORT knee trackers: the knees bent toward them, the elbows ELBOW_WAY')
if tool:
    if calibrate:
        held = (times >= calibrate[0]) & (times <= calibrate[1])
        high, low = sorted(wands, key=lambda s: -devices[s][1][held][:, 1].mean())
        parts[high], parts[low] = 'tool_head', 'tool_grip'
    else:
        low, high = sorted(wands, key=lambda s: wands[s][1])
        parts[low], parts[high] = 'tool_head', 'tool_grip'
    ANCHOR.update({'tool_head': ('tool_grip', 'elbow_R'), 'tool_grip': ('tool_head', 'elbow_R'),
                   'elbow_L': ('chest', 'tool_grip'), 'elbow_R': ('chest', 'tool_grip')})
HAND = (({'L': 'tool_head', 'R': 'tool_grip'} if swap else {'L': 'tool_grip', 'R': 'tool_head'}) if hands
        else {'L': 'tool_grip', 'R': 'tool_grip'}) if tool else \
    {'L': 'hand_L', 'R': 'hand_R'}
P = {parts[s]: devices[s][1].copy() for s in parts}
Q = {parts[s]: devices[s][2].copy() for s in parts}
OK = {parts[s]: devices[s][3].copy() for s in parts}
for part in sorted(P):
    print('IMPORT %-8s %s, lost %.1f%%' % (part, next(s for s in parts if parts[s] == part), 100 * (1 - OK[part].mean())))

# ---- quaternion helpers (w, x, y, z), numpy

def qmul(a, b):
    w1, x1, y1, z1 = a
    w2, x2, y2, z2 = b
    return np.array([w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2, w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
                     w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2, w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2])
def qinv(a):
    return a * np.array([1, -1, -1, -1])
def qrot(a, v):
    return qmul(qmul(a, np.array([0, *v])), qinv(a))[1:]
def slerp(a, b, u):
    d = np.dot(a, b)
    if d < 0:
        b, d = -b, -d
    if d > 0.9995:
        r = a + (b - a) * u
        return r / np.linalg.norm(r)
    th = math.acos(min(1.0, d))
    return (math.sin((1 - u) * th) * a + math.sin(u * th) * b) / math.sin(th)

# ---- gaps filled from each lost tracker's anchor

def runs(ok):
    """The (first, last) sample of each lost run."""
    out, i = [], 0
    while i < len(ok):
        if not ok[i]:
            j = i
            while j + 1 < len(ok) and not ok[j + 1]:
                j += 1
            out.append((i, j))
            i = j + 1
        else:
            i += 1
    return out

for part in P:
    for a, b in runs(OK[part]):
        before, after = a - 1, b + 1
        anchor = next((x for x in ANCHOR[part] if all(OK[x][max(before, 0):after + 1 if after < N else N])), None)
        for i in range(a, b + 1):
            u = (i - before) / (after - before) if before >= 0 and after < N else (0.0 if before >= 0 else 1.0)
            ends = [k for k in (before, after) if 0 <= k < N]
            if anchor is None:
                # Nothing to carry it: straight across, or held at the end it has.
                k0, k1 = (before if before >= 0 else after), (after if after < N else before)
                P[part][i] = P[part][k0] + (P[part][k1] - P[part][k0]) * u
                Q[part][i] = slerp(Q[part][k0], Q[part][k1], u)
                continue
            rel = []
            for k in ends:
                qa = Q[anchor][k]
                rel.append((qrot(qinv(qa), P[part][k] - P[anchor][k]), qmul(qinv(qa), Q[part][k])))
            if len(rel) == 1:
                rp, rq = rel[0]
            else:
                rp, rq = rel[0][0] + (rel[1][0] - rel[0][0]) * u, slerp(rel[0][1], rel[1][1], u)
            P[part][i] = P[anchor][i] + qrot(Q[anchor][i], rp)
            Q[part][i] = qmul(Q[anchor][i], rq)
        print('IMPORT %s lost %.2f .. %.2f s, carried with %s' % (part, times[a], times[b], anchor or 'nothing'))

# ---- the performer's space: x their left, y behind them, z up (the metarig's axes), the floor at 0, the origin
# between the feet at rest

if tool:
    fw = P['chest'][rest].mean(0) - P['hips'][rest].mean(0)     # the hips tracker is at the back of the belt
    fw[1] = 0.0
    fw /= np.linalg.norm(fw)
else:
    fw = facing({}, {}, {'L': Q['hand_L'][rest_b - 1], 'R': Q['hand_R'][rest_b - 1]})   # the way the wands point
up = np.array([0.0, 1.0, 0.0])
rt = np.cross(fw, up)
C = np.array([-rt, -fw, up])            # rows: the performer's left, back and up in SteamVR space
origin = (P['foot_L'][rest].mean(0) + P['foot_R'][rest].mean(0)) / 2
# The floor from the feet at rest, not SteamVR's: its floor moves when the room is set up again or it starts afresh
# (one session's takes read the feet 0.18 m over it).
origin[1] = min(P['foot_L'][rest].mean(0)[1], P['foot_R'][rest].mean(0)[1]) - FOOT_TRACKER_HEIGHT
if abs(origin[1]) > 0.03:
    print('IMPORT SteamVR\'s floor is %.2f m off the floor the feet stand on; taking the feet\'s' % origin[1])
Cq = Quaternion(Matrix(C.tolist()).to_quaternion())
for part in P:
    P[part] = (P[part] - origin) @ C.T
    Q[part] = np.array([list(Cq @ Quaternion(q) @ Cq.conjugated()) for q in Q[part]])

def at(part, i):
    return Vector(P[part][i]), Quaternion(Q[part][i])

# ---- the performer's joints at rest, each on the tracker it rides

def mean_at(part):
    p = Vector(P[part][rest].mean(0))
    q = Quaternion(Q[part][rest_b - 1])
    return p, q

height = mean_at('head')[0].z + HEAD_ABOVE
print('IMPORT height about %.2f m' % height)
joints = {}   # name: (tracker part, the joint in the tracker's frame)
def ride(joint, part, place):
    p, q = mean_at(part)
    joints[joint] = (part, q.conjugated() @ (place - p))
ankles = {}
for side in 'LR':
    p, q = mean_at('foot_' + side)
    ankle = Vector((p.x, p.y + ANKLE_BEHIND, ANKLE_HEIGHT))
    ankles[side] = ankle
    ride('ankle_' + side, 'foot_' + side, ankle)
pelvis = (ankles['L'] + ankles['R']) / 2
pelvis.z = HIP_HEIGHT * height
ride('pelvis', 'hips', pelvis)
for side, sign in (('L', 1), ('R', -1)):
    ride('hip_' + side, 'hips', pelvis + Vector((sign * HIP_HALF, 0, 0)))
    if knees:
        # The knee at rest on the straight leg's line from hip to ankle, a shin's length up, ridden on the knee
        # tracker so where on the shin it is worn doesn't matter.
        hip_rest = pelvis + Vector((sign * HIP_HALF, 0, 0))
        ride('knee_' + side, 'knee_' + side, ankles[side] + (hip_rest - ankles[side]) * (SHIN / (THIGH + SHIN)))
    ride('shoulder_' + side, 'chest', Vector((pelvis.x + sign * SHOULDER_HALF * height, pelvis.y, SHOULDER_HEIGHT * height)))
    if tool:
        # The fists on the handle, below the grip wand on the line from the head through it.
        g, h = mean_at('tool_grip')[0], mean_at('tool_head')[0]
        down = (g - h).normalized()
        if hands:
            ride('wrist_' + side, HAND[side], mean_at(HAND[side])[0] + down * hands[0 if side == 'R' else 1])
        else:
            ride('wrist_' + side, 'tool_grip', g + down * (RIGHT_BELOW + (LEFT_BELOW if side == 'L' else 0.0)))
        continue
    p, q = mean_at('hand_' + side)
    wrist_vr = np.array(WRIST_IN_WAND)
    ride('wrist_' + side, 'hand_' + side, p + q @ Vector(C @ wrist_vr))
legs = (THIGH * height, SHIN * height)
arms = (UPPER_ARM * height, FOREARM * height)
leg_long = HIP_HEIGHT * height - ANKLE_HEIGHT    # the performer's hip joint over the ankle at rest, metres

def joint(name, i):
    part, local = joints[name]
    p, q = at(part, i)
    return p + q @ local

def turn(part, i):
    """The tracker's turn since the rest pose (armature space)."""
    return Quaternion(Q[part][i]) @ mean_at(part)[1].conjugated()

def bend(top, end, lengths, pick):
    """The middle joint of a two-bone limb from top to end, on its circle of places the one pick(centre, way across
    the line, u) chooses."""
    a_len, b_len = lengths
    d_vec = end - top
    d = min(max(d_vec.length, 1e-4), a_len + b_len - 1e-4)
    u = d_vec.normalized()
    a = (a_len * a_len - b_len * b_len + d * d) / (2 * d)
    h = math.sqrt(max(0.0, a_len * a_len - a * a))
    centre = top + u * a
    return centre + pick(centre, u) * h

# ---- the chibi's pose at each sample

S = walk.stand_pose()
SR = {b: S[b].to_3x3().normalized() for b in S}
order = []
def walk_down(b):
    order.append(b)
    for c in b.children:
        walk_down(c)
for b in walk.bones:
    if b.parent is None:
        walk_down(b)
for b in order:     # a bone stand_pose leaves out (the fingers) stands as its parent turns, as apply_pose lays it
    if b.name not in SR:
        SR[b.name] = SR[b.parent.name] if b.parent else Matrix.Identity(3)
        S[b.name] = S[b.parent.name] if b.parent else Matrix.Identity(4)
ROOT = walk.HIPS
LEG = walk.LEG
SCALE = LEG / leg_long          # armature units a metre of the performer's leg
DRIVEN = {walk.HIPS: ('hips', 'hips', 0.0), walk.SPINE: ('hips', 'chest', 0.5), walk.CHEST_BONE: ('chest', 'chest', 0.0),
          walk.NECK: ('chest', 'head', 0.5), walk.HEAD: ('head', 'head', 0.0)}
rest_pelvis = joint('pelvis', rest_b - 1)
rest_ankle = {s: joint('ankle_' + s, rest_b - 1) for s in 'LR'}

def elbow_of(side, i):
    """The performer's elbow at sample i: on the circle of places the arm allows, the one nearest the elbow tracker
    (without one, the one furthest ELBOW_WAY)."""
    if knees:
        x, y, z = ELBOW_WAY
        way = turn('chest', i) @ Vector((x if side == 'L' else -x, y, z))
        def toward(centre, u):
            w = way - way.dot(u) * u
            return w.normalized() if w.length > 1e-6 else Vector((0, 0, -1))
        return bend(joint('shoulder_' + side, i), joint('wrist_' + side, i), arms, toward)
    elbow_t = at('elbow_' + side, i)[0]
    def toward(centre, u):
        w = elbow_t - centre
        w = w - w.dot(u) * u
        return w.normalized() if w.length > 1e-6 else Vector((0, 0, -1))
    return bend(joint('shoulder_' + side, i), joint('wrist_' + side, i), arms, toward)

# The hands matched at rest by the forearm, not as they stand: in the rest pose the performer holds the wands out in front
# with the forearms about level, the hand in line with the forearm, where the chibi's standing hand hangs at its side.
# Each wand's turns are laid on the chibi's standing hand first turned as its forearm would be to point along the
# performer's resting forearm; laid on the hanging hand as it stood, a hand pointed down at the ground turned it on
# past the wrist and folded it back toward the body.
hand_rest = {}
for side in 'LR':
    fa, hn = 'forearm.' + side, 'hand.' + side
    standing = SR[fa] @ (walk.tail[fa] - walk.head[fa])
    resting = joint('wrist_' + side, rest_b - 1) - elbow_of(side, rest_b - 1)
    hand_rest[side] = standing.rotation_difference(resting).to_matrix() @ SR[hn]

def chibi(i):
    X = {}
    def put(b, R):
        pivot = X[b.parent.name] @ walk.head[b.name] if b.parent else S[b.name] @ walk.head[b.name]
        X[b.name] = walk.place(pivot, R.to_4x4(), walk.head[b.name])
    for b in order:
        n = b.name
        if n in DRIVEN:
            a, c, u = DRIVEN[n]
            D = turn(a, i).slerp(turn(c, i), u) if a != c else turn(a, i)
            R = D.to_matrix() @ SR[n]
            if b.parent is None:
                pivot = S[n] @ walk.head[n] + (joint('pelvis', i) - rest_pelvis) * SCALE
                X[n] = walk.place(pivot, R.to_4x4(), walk.head[n])
            else:
                put(b, R)
        elif n.startswith(('thigh.', 'shin.', 'foot.', 'toe.', 'heel.', 'upper_arm.', 'forearm.', 'hand.', 'thumb.')) \
                or n.startswith(('f_', 'palm.')):
            continue    # the limbs, below
        elif b.parent is not None and b.parent.name in X:
            put(b, X[b.parent.name].to_3x3().normalized() @ SR[b.parent.name].inverted() @ SR[n])
        else:
            put(b, SR[n])
    for side in 'LR':
        # The leg: the knee bent as the performer's is, its bend across the foot's ankle hinge and forward.
        hip_p, ankle_p = joint('hip_' + side, i), joint('ankle_' + side, i)
        hinge = turn('foot_' + side, i) @ Vector((-1, 0, 0))     # the foot's right, which a knee bends about
        def knee_way(centre, u, hinge=hinge):
            n = (hinge - hinge.dot(u) * u).normalized()
            way = u.cross(n).normalized()
            # Bent forward, the thigh-to-shin turn is about the foot's left: (hip->knee) x (knee->ankle) . right < 0.
            return way if (way).cross(u).dot(hinge) < 0 else -way
        if knees:
            # Toward the knee joint ridden on the knee tracker; near straight, where that is barely off the line, the
            # foot's hinge as without one.
            knee_t = joint('knee_' + side, i)
            def knee_way(centre, u, knee_t=knee_t, hinge_way=knee_way):
                w = knee_t - centre
                w = w - w.dot(u) * u
                return w.normalized() if w.length > 0.02 else hinge_way(centre, u)
        knee_p = bend(hip_p, ankle_p, legs, knee_way)
        pole = knee_p - (hip_p + ankle_p) / 2
        th = 'thigh.' + side
        hip_c = X[walk.bones[th].parent.name] @ walk.head[th]
        ankle_c = S['foot.' + side] @ walk.head['foot.' + side] + (ankle_p - rest_ankle[side]) * SCALE
        R_foot = turn('foot_' + side, i).to_matrix() @ SR['foot.' + side]
        walk.leg_to(side, hip_c, ankle_c, pole, X, turn=R_foot.to_4x4())
        for b in order:
            if b.name.startswith(('toe.', 'heel.')) and b.name.endswith(side):
                X[b.name] = walk.place(X[b.parent.name] @ walk.head[b.name],
                                       (X[b.parent.name].to_3x3().normalized() @ SR[b.parent.name].inverted()
                                        @ SR[b.name]).to_4x4(), walk.head[b.name])
        # The arm: the wrist moved from the shoulder as the performer's, in arm lengths, the elbow toward theirs.
        ua, fa, hn = 'upper_arm.' + side, 'forearm.' + side, 'hand.' + side
        sh_p, wr_p = joint('shoulder_' + side, i), joint('wrist_' + side, i)
        elbow_p = elbow_of(side, i)
        la, lf = (walk.tail[ua] - walk.head[ua]).length, (walk.tail[fa] - walk.head[fa]).length
        shoulder = X['shoulder.' + side] @ walk.head[ua]
        wrist = shoulder + (wr_p - sh_p) * ((la + lf) / (arms[0] + arms[1]))
        pole = elbow_p - (sh_p + wr_p) / 2
        elbow = bend(shoulder, wrist, (la, lf),
                     lambda centre, u: (pole - pole.dot(u) * u).normalized())
        reach = (wrist - shoulder).length
        if reach > la + lf:
            wrist = shoulder + (wrist - shoulder) * ((la + lf - 1e-4) / reach)
        R_ua = walk.aim(SR[ua] @ (walk.tail[ua] - walk.head[ua]), elbow - shoulder).to_3x3() @ SR[ua]
        X[ua] = walk.place(shoulder, R_ua.to_4x4(), walk.head[ua])
        guess = R_ua @ SR[ua].inverted() @ SR[fa]
        R_fa = walk.aim(guess @ (walk.tail[fa] - walk.head[fa]), wrist - elbow).to_3x3() @ guess
        R_hand = turn(HAND[side], i).to_matrix() @ hand_rest[side]
        # The forearm rolled toward the hand's turn, as a forearm does, so the wrist doesn't wring.
        along = (walk.tail[fa] - walk.head[fa]).normalized()
        rel = (R_fa.inverted() @ R_hand).to_quaternion()
        proj = along * Vector(rel[1:]).dot(along)
        tw = Quaternion((rel.w, proj.x, proj.y, proj.z)).normalized()
        R_fa = R_fa @ Quaternion().slerp(tw, FOREARM_TWIST).to_matrix()
        X[fa] = walk.place(X[ua] @ walk.head[fa], R_fa.to_4x4(), walk.head[fa])
        # Only WRIST_SHARE of the wrist's bend from the standing hand's own (a hand at rest is turned a little on its
        # forearm) is kept.
        standing = (SR[fa].inverted() @ SR[hn]).to_quaternion()
        bent = (R_fa.inverted() @ R_hand).to_quaternion()
        R_hand = R_fa @ (standing @ Quaternion().slerp(standing.conjugated() @ bent, WRIST_SHARE)).to_matrix()
        X[hn] = walk.place(X[fa] @ walk.head[hn], R_hand.to_4x4(), walk.head[hn])
        for b in order:
            if b.name in X or not b.name.endswith(side) or b.parent is None:
                continue
            if b.parent.name in X and b.name.startswith(('hand.', 'thumb.', 'f_', 'palm.')):
                X[b.name] = walk.place(X[b.parent.name] @ walk.head[b.name],
                                       (X[b.parent.name].to_3x3().normalized() @ SR[b.parent.name].inverted()
                                        @ SR[b.name]).to_4x4(), walk.head[b.name])
    for b in order:     # anything left: its standing turn on its parent
        if b.name not in X:
            put(b, X[b.parent.name].to_3x3().normalized() @ SR[b.parent.name].inverted() @ SR[b.name]
                if b.parent and b.parent.name in X else SR[b.name])
    return X

# ---- the stretch taken, at FPS

if span is None:
    span = (times[rest_b] + 0.5, header.get('end', times[-1]))
def sample_at(t):
    return min(max(int(round(np.interp(t, times, np.arange(N)))), 0), N - 1)

def features(i):
    """Where the trackers are (performer space, from the hips' middle), for telling how alike two moments are."""
    base = P['hips'][i]
    return np.concatenate([P[p][i] - base for p in ('head', 'chest', HAND['L'] if not tool else 'tool_head', HAND['R'],
                                                     *(('knee_L', 'knee_R') if knees else ('elbow_L', 'elbow_R')))] +
                          [P['hips'][i] * 0.5])

first_t, last_t = span
if loop:
    shortest, longest = float(loop[0]), float(loop[1])
    cands = np.arange(span[0], span[1], 1.0 / FPS)
    F = {round(t, 4): features(sample_at(t)) for t in cands}
    best = None
    for a in cands:
        for b in cands:
            if not shortest <= b - a <= longest:
                continue
            e = np.linalg.norm(F[round(a, 4)] - F[round(b, 4)])
            if best is None or e < best[0]:
                best = (e, a, b)
    if best is None:
        sys.exit('IMPORT no cycle of %s .. %s s in %.1f .. %.1f s' % (shortest, longest, span[0], span[1]))
    _, first_t, last_t = best
    print('IMPORT closest cycle %.2f .. %.2f s (%.2f s long), %.3f m apart' % (first_t, last_t, last_t - first_t, best[0]))

frames = max(2, int(round((last_t - first_t) * FPS)))
poses = [chibi(sample_at(first_t + f / FPS)) for f in range(frames + (0 if once else 1))]

if tool:
    # The way the tool's point faces, in the head wand's own frame: across the handle the way its head moves on the
    # downswing (the point leads), averaged over every sample moving fast enough.
    H, G = P['tool_head'], P['tool_grip']
    vel = np.gradient(H, times, axis=0)
    found = []
    for k in (range(N) if not calibrate else []):
        a = (H[k] - G[k]) / np.linalg.norm(H[k] - G[k])
        v = vel[k] - a * np.dot(vel[k], a)
        if np.linalg.norm(v) > TOOL_EDGE_SPEED and vel[k][2] < 0 and span[0] <= times[k] <= span[1]:
            found.append(qrot(qinv(Q['tool_head'][k]), v / np.linalg.norm(v)))
    if calibrate:
        # Held upright with the point straight forward: forward, square to the handle, in the head wand's frame.
        for k in np.nonzero((times >= calibrate[0]) & (times <= calibrate[1]))[0]:
            a = (H[k] - G[k]) / np.linalg.norm(H[k] - G[k])
            v = np.array([0.0, -1.0, 0.0]) - a * -a[1]   # forward is -y in the performer's space
            found.append(qrot(qinv(Q['tool_head'][k]), v / np.linalg.norm(v)))
        upright = np.degrees(np.mean([math.acos(min(1.0, (H[k] - G[k])[2] / np.linalg.norm(H[k] - G[k])))
                                      for k in np.nonzero((times >= calibrate[0]) & (times <= calibrate[1]))[0]]))
        print('IMPORT calibrated on %.2f .. %.2f s, the handle %.0f degrees off upright' % (calibrate[0], calibrate[1], upright))
    if not found:
        sys.exit('IMPORT no downswing fast enough to find the way the tool\'s point faces')
    edge_local = sum(found) / np.linalg.norm(sum(found))
    spread = np.degrees(np.mean([math.acos(min(1.0, float(np.dot(e, edge_local)))) for e in found]))
    print('IMPORT the tool\'s point from %d samples, %.0f degrees about their mean' % (len(found), spread))
    la = (walk.tail['upper_arm.R'] - walk.head['upper_arm.R']).length
    lf = (walk.tail['forearm.R'] - walk.head['forearm.R']).length
    stretch = (la + lf) / (arms[0] + arms[1])
    tools = []
    for f, X in enumerate(poses):
        i = sample_at(first_t + f / FPS)
        chest = X[walk.CHEST_BONE]
        R3 = chest.to_3x3().normalized().inverted()
        fist = X['shoulder.R'] @ walk.head['upper_arm.R'] + (joint('wrist_R', i) - joint('shoulder_R', i)) * stretch
        way = Vector(H[i] - G[i]).normalized()
        edge = Vector(qrot(Q['tool_head'][i], edge_local))
        edge = (edge - edge.dot(way) * way).normalized()
        entry = {'fist': list(chest.inverted() @ fist), 'way': list(R3 @ way), 'edge': list(R3 @ edge)}
        for side in 'LR':
            # The elbow tracker's offset square to the shoulder-to-fist line: the elbow solved on its circle has almost
            # none with the arm near straight, and its way flipped from frame to frame there.
            sh, wr = joint('shoulder_' + side, i), joint('wrist_' + side, i)
            line = (wr - sh).normalized()
            off = at('elbow_' + side, i)[0] - (sh + wr) / 2
            entry['pole_' + side] = list(R3 @ (off - off.dot(line) * line).normalized())
        tools.append(entry)
names = [b.name for b in order]
turns = {n: [X[n].to_quaternion() for X in poses] for n in names}
hips = [(X[ROOT] @ walk.head[ROOT] - walk.head[ROOT]) / LEG for X in poses]
for n in names:      # one sign throughout, so a slerp between frames takes the short way
    for f in range(1, len(turns[n])):
        if turns[n][f].dot(turns[n][f - 1]) < 0:
            turns[n][f] = -turns[n][f]
if not once:
    # A loop is played on the spot: moved across the ground so its feet are, on average, where the chibi's stand (the
    # performer may have stepped into it, as into the planting squat).
    def feet(X):
        return sum((X['foot.' + s] @ walk.head['foot.' + s] for s in 'LR'), Vector()) / 2
    shift = sum((feet(X) for X in poses), Vector()) / len(poses) - feet(S)
    shift.z = 0.0
    hips = [h - shift / LEG for h in hips]
    print('IMPORT moved %.2f leg lengths across the ground onto its feet' % (shift.length / LEG))
    # What is left between the frame after the last and the first, spread over the loop so it closes.
    for n in names:
        left = turns[n][0] @ turns[n][frames].inverted()
        turns[n] = [Quaternion().slerp(left, f / frames) @ q for f, q in enumerate(turns[n][:frames])]
    gap = hips[0] - hips[frames]
    hips = [h + gap * (f / frames) for f, h in enumerate(hips[:frames])]
    if tool:
        for key in tools[0]:
            gap = Vector(tools[0][key]) - Vector(tools[frames][key])
            for f in range(frames):
                v = Vector(tools[f][key]) + gap * (f / frames)
                tools[f][key] = list(v if key == 'fist' else v.normalized())
        tools = tools[:frames]

out = {'source': os.path.relpath(path), 'action': 'mocap', 'first': int(round(first_t * FPS)),
       'last': int(round(last_t * FPS)) - 1, 'frames': frames, 'fps': FPS, 'ground': 0.0,
       'turns': {n: [[round(c, 6) for c in q] for q in turns[n]] for n in names},
       'hips': [[round(c, 6) for c in h] for h in hips]}
if tool:
    out['tool'] = [{k: [round(c, 6) for c in v] for k, v in e.items()} for e in tools]
target = os.path.join(os.path.dirname(HERE), name + '_clip.json')
with open(target, 'w') as f:
    json.dump(out, f)
print('IMPORT wrote %s: %.2f .. %.2f s, %d frames at %d fps%s' % (
    target, first_t, last_t, frames, FPS, ', played once' if once else ''))
