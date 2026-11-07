"""Makes the head's Nose_Pig shape key, a pig's blunt snout for the Nose_Pig gene, from the head's own vertices.

In Blender: open it in the Text Editor and press Run Script, with the rig's .blend open. Nothing is saved. Headless, it
saves to out.blend, never over the file it opened:

    blender -b <in.blend> --python anim_scripts/make_nose_pig.py -- <out.blend>

The vertices round the nose are drawn forward onto one plane, which makes the flat end, and those further out by less,
which makes the sides. They move straight forward and no other way, so seen from the front nothing shifts: the face
patch's texture places are a front projection in the head bone's frame, so Facial Animation's layers still line up. The
key is replaced if there is one. Run it again after a remesh, as it goes by place. Lengths are in the .blend's units,
with the head about 9.6 tall."""
import bpy, os, sys
from mathutils import Vector

KEY = 'Nose_Pig'
OBJECT = 'Head'
CENTRE_Z = 21.05      # the flat end's middle, from the nose's tip down: the bridge above it stays nearly where it was, or
                      # the one long triangle from there to the forehead makes it a muzzle sloping from the brow
END_Y = -5.0          # where the flat end comes to (forward is -y; the nose is at -3.9, the forehead at -4.4)
FLAT_X = 1.6          # half width of the flat end
FLAT_UP = 0.65        # how far it reaches up from CENTRE_Z, to the nose's tip
FLAT_DOWN = 0.95      # how far down, to the middle of the chin, or the flat end has a cleft there
SIDE_X = 2.6          # half width of the snout where it meets the face
SIDE_UP = 1.7         # how far up it meets the face, taking a little of the bridge with it
SIDE_DOWN = 1.25      # how far down
FRONT_Y = -1.5        # vertices further back than this stay, whatever their height

def reach(p, x, up, down):
    """How far out p is across the front of the face, 1 at the ellipse of these half sizes."""
    dz = p.z - CENTRE_Z
    z = up if dz > 0 else down
    return ((p.x / x) ** 2 + (dz / z) ** 2) ** 0.5

def share(p):
    """How much of the way to the flat end p goes: all of it inside the flat end, none outside the sides, eased between."""
    if p.y > FRONT_Y:
        return 0.0
    inner, outer = reach(p, FLAT_X, FLAT_UP, FLAT_DOWN), reach(p, SIDE_X, SIDE_UP, SIDE_DOWN)
    if inner <= 1:
        return 1.0
    if outer >= 1:
        return 0.0
    # Where p lies between the two ellipses along its own line from the middle.
    t = (1 / outer - 1) / (1 / outer - 1 / inner)
    return t * t * (3 - 2 * t)

def make_nose_pig(o):
    me = o.data
    if not me.shape_keys:
        o.shape_key_add(name='Basis', from_mix=False)
    block = me.shape_keys.key_blocks.get(KEY)
    if block is None:
        block = o.shape_key_add(name=KEY, from_mix=False)
    rest = me.shape_keys.reference_key.data
    moved = 0
    for i, d in enumerate(block.data):
        p = Vector(rest[i].co)
        p.y += (END_Y - p.y) * share(p)
        moved += (p - rest[i].co).length > 1e-5
        d.co = p
    block.value = 0.0
    print('NOSE_PIG key %s on %s: %d of %d vertices moved' % (KEY, o.name, moved, len(rest)))

make_nose_pig(bpy.data.objects[OBJECT])
if '--' in sys.argv:
    out_blend = os.path.abspath(sys.argv[sys.argv.index('--') + 1])
    if out_blend == os.path.abspath(bpy.data.filepath):
        sys.exit('make_nose_pig.py: out.blend must not be the .blend it was opened from')
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
