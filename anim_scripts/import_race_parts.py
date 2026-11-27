# Puts the race parts' game files (race_*.txt, first made by make_race_parts.py) into the rig's .blend as editable meshes,
# each skinned to a small armature of its own parented to a metarig bone, as the cat ears and tail are, so they can be
# sculpted and re-weighted in Blender; export_cat.py (and so a full export) then writes them back out:
#   blender -b Models/pawnHumanRig.blend --python anim_scripts/import_race_parts.py -- "<mod>/Models/chibi" [--over | out.blend] [part ...]
# or the Text Editor's Run Script in the open session (unsaved; the folder below). With --over it saves over the .blend
# it opened. A part already in the .blend is left alone unless named.
#
# Each part is <Race>_<Part>_Race (the mesh) on <Race>_<Part>_Rig (the armature):
#   ears: bones Ear.L, Ear.L.001, ... and Ear.R, ... (export_cat.py's ear_chain), on spine.006, vertex group `inner` the lining
#   tail: bones Tail, Tail.001, ... on its file's bone (spine, the hips, unless a bone line says otherwise: Milira's wings ride
#         spine.003), vertex group `inner` the tip; custom properties m3at_mirror (-1: its side swing turned round, a left
#         wing) and m3at_plain (drawn without the hair's strand grain)
# The mesh's custom property m3at_race_file names the game file export_cat.py writes it to.
import os
import sys

import bpy
from mathutils import Matrix, Vector

# The repository's root: the .blend is in its Models folder (so the Text Editor, whose working folder may be anywhere, finds it).
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(bpy.data.filepath))) if bpy.data.filepath else os.getcwd()
sys.path.insert(0, os.path.join(ROOT, 'anim_scripts'))
if '__file__' in globals() and os.path.exists(__file__):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_cat
import walk

FOLDER = os.path.join(ROOT, "M3AT: Maru's 3D Animated Toons", 'Models', 'chibi')   # for Run Script
CAT_EARS = ('Cat_Ears_Gene', 'Cat_Ears_Rig')   # the ears a race's are copied from, below, for the player to reshape
COPIES = {'race_ratkin_ears.txt', 'race_wolfein_ears.txt'}   # parts made as duplicates of the cat ears rather than read from a file
PARTS = {   # file: (object name stem, kind)
    'race_ratkin_ears.txt': ('Ratkin_Ears', 'ears'),
    'race_ratkin_tail.txt': ('Ratkin_Tail', 'tail'),
    'race_wolfein_ears.txt': ('Wolfein_Ears', 'ears'),
    'race_wolfein_tail.txt': ('Wolfein_Tail', 'tail'),
    'race_miho_ears.txt': ('Miho_Ears', 'ears'),   # made from Wolfein's by make_miho_parts.py; once imported, the .blend's
    'race_miho_tail.txt': ('Miho_Tail', 'tail'),   # are written over them by a full export
    'race_milira_wing_L.txt': ('Milira_Wing_L', 'tail'),
    'race_milira_wing_R.txt': ('Milira_Wing_R', 'tail'),
}
HEAD_BONE, HIPS_BONE = 'spine.006', 'spine'
COLLECTION = 'RaceParts'


def read(path):
    out = {'v': [], 'f': [], 'ear': {}, 'tail': {}, 'ew': {}, 'tw': {}, 'inner': set(), 'bone': HIPS_BONE, 'mirror': 1, 'plain': False}
    for line in open(path):
        p = line.split()
        if not p or p[0].startswith('#'):
            continue
        k = p[0]
        if k == 'v':
            out['v'].append(tuple(float(x) for x in p[1:4]))
        elif k == 'f':
            out['f'].append(tuple(int(x) for x in p[1:4]))
        elif k == 'ear':
            out['ear'][(p[1], int(p[2]))] = tuple(float(x) for x in p[3:6])
        elif k == 'tail':
            out['tail'][int(p[1])] = tuple(float(x) for x in p[2:5])
        elif k == 'ew':
            out['ew'][int(p[1])] = [(p[i], int(p[i + 1]), float(p[i + 2])) for i in range(2, len(p) - 2, 3)]
        elif k == 'tw':
            out['tw'][int(p[1])] = [(int(p[i]), float(p[i + 1])) for i in range(2, len(p) - 1, 2)]
        elif k == 'inner':
            out['inner'] |= set(int(x) for x in p[1:])
        elif k == 'bone':
            out['bone'] = p[1]
        elif k == 'mirror':
            out['mirror'] = -1 if float(p[1]) < 0 else 1
        elif k == 'plain':
            out['plain'] = True
    return out


def blender_at(g, scale):
    """The game's model space (x right, y up, z forward, cells) back to Blender's world, as export_cat.game undoes it."""
    return Vector((-g[0], -g[2], g[1])) / scale


def ear_bone(side, k):
    return 'Ear.%s' % side if k == 0 else 'Ear.%s.%03d' % (side, k)


def tail_bone(k):
    return 'Tail' if k == 0 else 'Tail.%03d' % k


def copy_cat_ears(stem, file, coll):
    """Duplicates the cat ears (mesh, armature, weights, lining, the parent bone) as <stem>_Race on <stem>_Rig."""
    mesh, rig = bpy.data.objects.get(CAT_EARS[0]), bpy.data.objects.get(CAT_EARS[1])
    if mesh is None or rig is None:
        print('RACE no %s on %s to copy for %s' % (CAT_EARS[0], CAT_EARS[1], stem))
        return
    for name in (stem + '_Race', stem + '_Rig'):
        old = bpy.data.objects.get(name)
        if old is not None:
            bpy.data.objects.remove(old, do_unlink=True)
    new_rig = rig.copy()
    new_rig.data = rig.data.copy()
    new_rig.name = new_rig.data.name = stem + '_Rig'
    new_mesh = mesh.copy()
    new_mesh.data = mesh.data.copy()
    new_mesh.name = new_mesh.data.name = stem + '_Race'
    for o in (new_rig, new_mesh):
        coll.objects.link(o)
    new_mesh.parent = new_rig
    new_mesh.matrix_parent_inverse = mesh.matrix_parent_inverse.copy()
    for mod in new_mesh.modifiers:
        if mod.type == 'ARMATURE':
            mod.object = new_rig
    new_mesh['m3at_race_file'] = file
    new_mesh['m3at_from_cat'] = True   # so a later run leaves it alone: it is the one to reshape
    print('RACE %s: a copy of %s, %d vertices' % (new_mesh.name, mesh.name, len(new_mesh.data.vertices)))


def make(stem, kind, data, scale, metarig, coll):
    for name in (stem + '_Race', stem + '_Rig'):
        old = bpy.data.objects.get(name)
        if old is not None:
            bpy.data.objects.remove(old, do_unlink=True)
    arm = bpy.data.armatures.new(stem + '_Rig')
    arm.display_type = 'OCTAHEDRAL'
    rig = bpy.data.objects.new(stem + '_Rig', arm)
    rig.show_in_front = True
    coll.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    for o in bpy.context.view_layer.objects:
        o.select_set(o == rig)
    bpy.ops.object.mode_set(mode='EDIT')
    chains = {}
    if kind == 'ears':
        for side in ('L', 'R'):
            n = len([1 for (s, _) in data['ear'] if s == side])
            joints = [blender_at(data['ear'][(side, k)], scale) for k in range(n)]
            chains[side] = joints
            parent = None
            for k in range(n - 1):
                b = arm.edit_bones.new(ear_bone(side, k))
                b.head, b.tail = joints[k], joints[k + 1]
                b.parent = parent
                b.use_connect = parent is not None
                parent = b
    else:
        n = len(data['tail'])
        joints = [blender_at(data['tail'][k], scale) for k in range(n)]
        parent = None
        for k in range(n - 1):
            b = arm.edit_bones.new(tail_bone(k))
            b.head, b.tail = joints[k], joints[k + 1]
            b.parent = parent
            b.use_connect = parent is not None
            parent = b
    bpy.ops.object.mode_set(mode='OBJECT')
    # Parented to its metarig bone with the metarig at rest, so it stays where it is at rest and goes with that bone posed.
    bone = HEAD_BONE if kind == 'ears' else data['bone']
    rig.parent = metarig
    rig.parent_type = 'BONE'
    rig.parent_bone = bone
    bpy.context.view_layer.update()
    rig.matrix_world = Matrix.Identity(4)

    me = bpy.data.meshes.new(stem + '_Race')
    me.from_pydata([blender_at(v, scale) for v in data['v']], [], [(a, c, b) for a, b, c in data['f']])   # export_cat reverses them again
    me.update()
    o = bpy.data.objects.new(stem + '_Race', me)
    coll.objects.link(o)
    o.parent = rig
    mod = o.modifiers.new('Armature', 'ARMATURE')
    mod.object = rig
    groups = {}
    if kind == 'ears':
        for i, ws in data['ew'].items():
            for side, k, w in ws:
                name = ear_bone(side, k)
                g = groups.get(name) or o.vertex_groups.new(name=name)
                groups[name] = g
                g.add([i], w, 'REPLACE')
    else:
        for i, ws in data['tw'].items():
            for k, w in ws:
                name = tail_bone(k)
                g = groups.get(name) or o.vertex_groups.new(name=name)
                groups[name] = g
                g.add([i], w, 'REPLACE')
    if data['inner']:
        o.vertex_groups.new(name=export_cat.LINING_GROUP).add(sorted(data['inner']), 1.0, 'REPLACE')
    o['m3at_race_file'] = data['file']
    if kind == 'tail':
        o['m3at_mirror'] = data['mirror']
        o['m3at_plain'] = data['plain']
    print('RACE %s: %d vertices, %d triangles, on %s' % (o.name, len(me.vertices), len(me.polygons), bone))


def main():
    args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv and bpy.app.background else [FOLDER]
    folder = args[0]
    over = '--over' in args
    rest = [a for a in args[1:] if a != '--over']
    out = next((a for a in rest if a.endswith('.blend')), None)
    named = [a for a in rest if not a.endswith('.blend')]
    metarig = bpy.data.objects['metarig']
    # The figure's scale as the export works it out, which poses the standing pose: put the pose back after.
    saved = {pb.name: pb.matrix_basis.copy() for pb in metarig.pose.bones}
    scale = export_cat.figure_scale()
    for pb in metarig.pose.bones:
        pb.matrix_basis = saved[pb.name]
    was = metarig.data.pose_position
    metarig.data.pose_position = 'REST'
    bpy.context.view_layer.update()
    coll = bpy.data.collections.get(COLLECTION)
    if coll is None:
        coll = bpy.data.collections.new(COLLECTION)
        bpy.context.scene.collection.children.link(coll)
    try:
        for file, (stem, kind) in PARTS.items():
            if named and file not in named and stem not in named:
                continue
            have = bpy.data.objects.get(stem + '_Race')
            if file in COPIES and have is not None and not have.get('m3at_from_cat', False) and not named:
                pass   # an earlier import of the drawn ears: replaced by the cat ears' copy below
            elif not named and have is not None:
                print('RACE %s is in the .blend already, left alone (name it to replace it)' % stem)
                continue
            if file in COPIES:
                copy_cat_ears(stem, file, coll)
                continue
            path = os.path.join(folder, file)
            if not os.path.exists(path):
                print('RACE no %s' % path)
                continue
            data = read(path)
            data['file'] = file
            make(stem, kind, data, scale, metarig, coll)
    finally:
        metarig.data.pose_position = was
        bpy.context.view_layer.update()
    if over:
        bpy.ops.wm.save_mainfile()
    elif out:
        bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)


if __name__ == '__main__':
    main()
