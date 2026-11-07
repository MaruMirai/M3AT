# Clears what an imported avatar left in the rig .blend once its ears and tail were remade for the pawn (see export_cat.py).
# Run it in the open session (the Text Editor's Run Script, then save yourself), or on a copy:
#   blender -b <copy of the rig .blend> --python anim_scripts/tidy_cat_parts.py -- <out.blend>
# It removes
#   - images loaded from a file that nothing uses and that are not packed (the imported textures, left pointing at
#     paths on one machine),
#   - on the ears and the tail, vertex groups that are not bones of their own armature (the avatar's weights),
#   - an Armature modifier on them that has no armature.
#   - on the ears, weights on the other side's bones: a vertex on the figure's left (+x) on Ear.R* goes to the same Ear.L*
#     bone and the other way round, as an ear mirrored without flipping its group names rides the far ear's joints.
# Used or packed images, other meshes and the bones themselves are left alone. It prints what it removed.
import sys
import bpy

EARS = (('Cat_Ears_Gene', 'Cat_Ears_Rig'), ('Pig_Ears_Gene', 'Pig_Ears_Rig'), ('Floppy_Ears_Gene', 'Floppy_Ears_Rig'))
PARTS = EARS + (('Tail', 'Cat_Tail_Rig'),)
KEEP_GROUPS = ('inner',)   # the ears' lining, which export_cat.py reads

def ears_on_own_side(o):
    """Moves each ear vertex's weights onto the bones of the side it is on. Blender's left is +x."""
    moved = 0
    for v in o.data.vertices:
        own, far = ('.L', '.R') if (o.matrix_world @ v.co).x > 0.0 else ('.R', '.L')
        # Read first: changing a vertex's groups leaves v.groups' entries pointing at nothing.
        for name, weight in [(o.vertex_groups[e.group].name, e.weight) for e in v.groups]:
            if name.startswith('Ear' + far):
                o.vertex_groups[name.replace(far, own, 1)].add([v.index], weight, 'ADD')
                o.vertex_groups[name].remove([v.index])
                moved += 1
    if moved:
        print('TIDY ear weights moved to their own side', moved)

def main():
    for image in [i for i in bpy.data.images if i.source == 'FILE' and i.users == 0 and not i.packed_file]:
        print('TIDY image', image.name, image.filepath)
        bpy.data.images.remove(image)
    for mesh_name, _ in EARS:
        if mesh_name in bpy.data.objects:
            ears_on_own_side(bpy.data.objects[mesh_name])
    for mesh_name, rig_name in PARTS:
        o, rig = bpy.data.objects.get(mesh_name), bpy.data.objects.get(rig_name)
        if o is None or rig is None:
            continue
        bones = {b.name for b in rig.data.bones}
        for g in [g for g in o.vertex_groups if g.name not in bones and g.name not in KEEP_GROUPS]:
            print('TIDY vertex group', mesh_name, g.name)
            o.vertex_groups.remove(g)
        for m in [m for m in o.modifiers if m.type == 'ARMATURE' and m.object is None]:
            print('TIDY modifier', mesh_name, m.name)
            o.modifiers.remove(m)

main()
if '--' in sys.argv and len(sys.argv) > sys.argv.index('--') + 1:
    bpy.ops.wm.save_as_mainfile(filepath=sys.argv[sys.argv.index('--') + 1], copy=True)
