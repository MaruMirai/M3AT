# Takes one loop of a hands-and-knees crawl out of a glTF animation (Meshy's auto-rig on the chibi, which names its bones
# as the metarig does) into crawl_clip.json, which walk.crawl_at plays on the metarig.
#   blender -b --python anim_scripts/import_crawl.py -- <crawl.glb> [first last]
# first .. last are the glTF's frames for one cycle (the frame after the last is the first again); without them the
# cycle that closes best is used (away from START .. END, the look back in Meshy's "Crawl and Look Back").
#
# Each bone is written as its turn from its own rest, in armature space (both rigs face -Y with +Z up, so the turn means
# the same on the metarig whatever their rest poses), and the hips' place as an offset from where they would be if the
# crawl went at an even pace, in leg lengths. What is left between the last frame and the first is spread over the loop
# so it closes.
import bpy, json, math, os, sys
from mathutils import Quaternion

GLB_ARMATURE = 'target_character'
LOOK_BACK = (46, 78)   # frames to leave out when looking for a cycle: the look back
CYCLE = (16, 40)       # shortest and longest cycle tried, in frames
# metarig bone <- glTF bone. Bones of the metarig not listed turn with their parent.
MAP = {'spine': 'spine', 'spine.001': 'spine.001', 'spine.002': 'spine.002', 'spine.003': 'spine.003',
       'spine.004': 'spine.004', 'spine.006': 'spine.005',   # the glTF has one neck bone and the head after it
       'shoulder.L': 'shoulder.L', 'upper_arm.L': 'upper_arm.L', 'forearm.L': 'forearm.L', 'hand.L': 'hand.L',
       'hand.L.001': 'f_middle.01.L', 'hand.L.002': 'f_middle.02.L', 'thumb.L': 'thumb.01.L', 'thumb.L.001': 'thumb.02.L',
       'shoulder.R': 'shoulder.R', 'upper_arm.R': 'upper_arm.R', 'forearm.R': 'forearm.R', 'hand.R': 'hand.R',
       'hand.R.001': 'f_middle.01.R', 'hand.R.002': 'f_middle.02.R', 'thumb.R': 'thumb.01.R', 'thumb.R.001': 'thumb.02.R',
       'thigh.L': 'thigh.L', 'shin.L': 'shin.L', 'foot.L': 'foot.L', 'toe.L': 'toe.L',
       'thigh.R': 'thigh.R', 'shin.R': 'shin.R', 'foot.R': 'foot.R', 'toe.R': 'toe.R'}
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'crawl_clip.json')

args = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=args[0])
rig = bpy.data.objects[GLB_ARMATURE]
sc = bpy.context.scene
act = rig.animation_data.action
first_frame, last_frame = (int(v) for v in act.frame_range)
bones = rig.data.bones
leg = bones['thigh.L'].length + bones['shin.L'].length

def sample(f):
    sc.frame_set(f)
    turns = {}
    for name, src in MAP.items():
        pb = rig.pose.bones[src]
        turns[name] = (rig.matrix_world.to_3x3() @ pb.matrix.to_3x3() @ bones[src].matrix_local.to_3x3().inverted()).to_quaternion()
    return turns, rig.matrix_world @ rig.pose.bones['spine'].head

frames = [sample(f) for f in range(first_frame, last_frame + 1)]

def mismatch(a, b):
    return math.sqrt(sum(a[0][n].rotation_difference(b[0][n]).angle ** 2 for n in MAP) / len(MAP))

if len(args) >= 3:
    a, b = int(args[1]) - first_frame, int(args[2]) + 1 - first_frame
else:
    tries = [(mismatch(frames[a], frames[b]), a, b) for a in range(len(frames)) for b in range(a + CYCLE[0], min(a + CYCLE[1], len(frames)))
             if b < LOOK_BACK[0] - first_frame or a > LOOK_BACK[1] - first_frame]
    _, a, b = min(tries)
n = b - a
start, end = frames[a][1], frames[b][1]
travel = end - start
out = {'source': os.path.basename(args[0]), 'first': a + first_frame, 'last': b - 1 + first_frame, 'frames': n,
       'ground': travel.length / leg, 'turns': {name: [] for name in MAP}, 'hips': []}
for i in range(n):
    t = i / n
    turns, hips = frames[a + i]
    for name in MAP:
        # The loop's leftover turn (from the last frame on to the first) is taken out a share at a time.
        fix = frames[a][0][name] @ frames[b][0][name].inverted()
        q = Quaternion().slerp(fix, t) @ turns[name]
        out['turns'][name].append([round(v, 6) for v in q])
    # The hips from where an even pace would have them, the offset the loop leaves at the end spread over it as well.
    off = (hips - (start + travel * t)) / leg
    out['hips'].append([round(v, 6) for v in off])
with open(OUT, 'w') as f:
    json.dump(out, f)
print('CRAWL frames %d .. %d of %s (%d frames), %.3f leg lengths a cycle, seam %.1f degrees before closing'
      % (out['first'], out['last'], out['source'], n, out['ground'], math.degrees(mismatch(frames[a], frames[b]))))
