"""Mends shape keys after Symmetrize: copies one half's moves onto the other, mirrored.

Symmetrize mirrors the vertices but carries each shape key's moves across without flipping
them in x, so on the new half every key pushes sideways the wrong way. This takes the kept
half's moves, flips them in x and gives them to the mirror vertex on the other half, for
every key on the mesh. Vertices on the middle line keep theirs.

In Blender: open it in the Text Editor and press Run Script once. That adds "Mirror Keys
from -x" and "Mirror Keys from +x" to the dropdown menu under the Shape Keys list, which
mend every key of the active mesh, in Object or Edit Mode, with undo. Nothing is saved.
Headless, it mends each mesh in OBJECTS and saves to out.blend, never over the file it opened:

    blender -b <in.blend> --python anim_scripts/mirror_keys.py -- +x|-x <out.blend>

The half kept is by the sign of x in the mesh's own space: on the rig's Body, -x is the half
weighted to the .L bones. The mesh must be symmetric (each vertex's mirror within SAME of
another vertex); a vertex with no mirror is left alone and counted.
"""
import bpy, sys
from mathutils import Vector, kdtree

OBJECTS = ('Pawn', 'Body', 'Head')
SAME = 1e-4   # a vertex this close to another's mirror is its mirror; also the middle line's width


def mirror_keys(o, keep):
    """Mirrors every key of mesh object o from the keep half ('+x' or '-x'); o must not be in Edit Mode.
    Returns (vertices moved, vertices with no mirror)."""
    sign = 1 if keep == '+x' else -1
    me = o.data
    kd = kdtree.KDTree(len(me.vertices))
    for v in me.vertices:
        kd.insert(v.co, v.index)
    kd.balance()
    pairs = []
    lone = 0
    for v in me.vertices:
        if v.co.x * -sign > SAME:   # on the half being replaced
            co, j, d = kd.find((-v.co.x, v.co.y, v.co.z))
            if d < SAME:
                pairs.append((v.index, j))
            else:
                lone += 1
    for k in me.shape_keys.key_blocks:
        if k == me.shape_keys.reference_key:
            continue
        base = k.relative_key.data
        for i, j in pairs:
            e = k.data[j].co - base[j].co
            k.data[i].co = base[i].co + Vector((-e.x, e.y, e.z))
    me.update()
    return len(pairs), lone


class MARU_OT_mirror_keys(bpy.types.Operator):
    """Copy one half's shape key moves onto the other half, mirrored (mends keys after Symmetrize)"""
    bl_idname = 'object.maru_mirror_keys'
    bl_label = 'Mirror Keys'
    bl_options = {'REGISTER', 'UNDO'}

    keep: bpy.props.EnumProperty(name='Keep', items=(('-x', '-x', 'Keep the -x half'), ('+x', '+x', 'Keep the +x half')))

    @classmethod
    def poll(cls, context):
        o = context.object
        return o is not None and o.type == 'MESH' and o.data.shape_keys is not None

    def execute(self, context):
        o = context.object
        # Edit Mode keeps the mesh (and its keys) in its own copy, which would overwrite what is written here.
        was = o.mode
        if was != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        moved, lone = mirror_keys(o, self.keep)
        if was != 'OBJECT':
            bpy.ops.object.mode_set(mode=was)
        self.report({'WARNING'} if lone else {'INFO'}, 'Mirrored %d keys onto %d vertices from the %s half%s' % (
            len(o.data.shape_keys.key_blocks) - 1, moved, self.keep,
            ', %d with no mirror left alone' % lone if lone else ''))
        return {'FINISHED'}


def menu(self, context):
    self.layout.separator()
    for keep in ('-x', '+x'):
        self.layout.operator(MARU_OT_mirror_keys.bl_idname, text='Mirror Keys from %s' % keep).keep = keep


if '--' in sys.argv:
    keep, out = sys.argv[sys.argv.index('--')+1:][:2]
    if bpy.context.object and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    for name in OBJECTS:
        o = bpy.data.objects.get(name)
        if o is not None and o.type == 'MESH' and o.data.shape_keys:
            moved, lone = mirror_keys(o, keep)
            print('mirror_keys: %s, %d vertices from the %s half%s' % (
                name, moved, keep, ', %d with no mirror left alone' % lone if lone else ''))
    bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)
else:
    # Run again from the Text Editor, it replaces the menu entries rather than adding a second pair.
    old = getattr(bpy.types, 'OBJECT_OT_maru_mirror_keys', None)
    if old is not None:
        bpy.utils.unregister_class(old)
    old = bpy.app.driver_namespace.get('maru_mirror_keys_menu')
    if old is not None:
        bpy.types.MESH_MT_shape_key_context_menu.remove(old)
    bpy.utils.register_class(MARU_OT_mirror_keys)
    bpy.types.MESH_MT_shape_key_context_menu.append(menu)
    bpy.app.driver_namespace['maru_mirror_keys_menu'] = menu
