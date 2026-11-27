"""Makes Pauldrons: a big round shoulder pad over each shoulder, for GrimWorld 40,000's shoulder pads (its GW_Shoulder
layer, worn over its power armour: some sixty apparel across Angels of Death, Talons of the Emperor and the rest), which
had nothing to stand in for them. StandIns.cs matches any apparel worn only on a shoulder layer as ShoulderLayer over
the Shoulders, and this garment's layer and groups lines (export_frames.py's GARMENTS) are that, so it is drawn for all
of them and nothing else.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_pauldrons.py -- <out.glb> [out.blend]

Written to Models/garments/Pauldrons.glb. Each pad is a dome (part of a sphere, RINGS rings of SEGMENTS round from its
crown to its edge, CAP degrees from its axis) round a middle just out from the shoulder joint (upper_arm's head moved
OUT straight to the side and DOWN), its axis tipped TILT degrees up from straight out, with a raised rim round its
edge (RIM) and its inside a second dome THICK in, so it is solid from below. Its radius is RADIUS; the run prints how far the
marine power armour (Models/garments/powerArmor.glb, the garment most GrimWorld armour is drawn as) reaches from the
middle within the dome's cone, which should be under it. The run prints how near each comes to the head
at rest; HEAD_CLEAR less than that pulls the cone's top in.

Weighted as one piece, WEIGHTS of the shoulder and upper arm bones on its side (export_frames.py's part_weights keeps
them), so it lifts part way with the arm rather than bending. Untextured: drawn in the apparel's colour. Sizes are in
Blender units at the rig's scale (one cell is about 17.6). The .blend is never saved; the optional out.blend is a copy
with the pads on the rig, to look at."""
import bpy, bmesh, sys, math, os
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_pauldrons.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARMOUR = os.path.join(ROOT, 'Models', 'garments', 'powerArmor.glb')
NAME = 'Pauldrons'

OUT = 1.9          # the middle: straight out to the side from the shoulder joint
DOWN = 0.8         # and down
TILT = 22.0        # degrees the dome's axis is tipped up from straight out to the side
CAP = 75.0         # degrees from the axis to its edge
SEGMENTS = 16
RINGS = 6
RADIUS = 3.4       # the dome's radius
THICK = 0.3        # the shell's thickness
RIM = 0.25         # the rim stands this far out from the shell at the edge
WEIGHTS = {'shoulder': 0.4, 'upper_arm': 0.6}   # by side, .L or .R


def dome(middle, axis, radius, side):
    """The vertices and quads of one pad: the outer dome, the rim, the inner dome, joined at the rim."""
    up = Vector((0, 0, 1))
    u = axis.cross(up).normalized()
    v = axis.cross(u).normalized()
    def at(theta, phi, r):
        t, p = math.radians(theta), 2 * math.pi * phi / SEGMENTS
        return middle + (axis * math.cos(t) + (u * math.cos(p) + v * math.sin(p)) * math.sin(t)) * r
    verts, faces = [], []
    def ring(theta, r):
        verts.extend(at(theta, i, r) for i in range(SEGMENTS))
        return len(verts) - SEGMENTS
    # From the outer crown out to the edge, the rim, then the inner dome back to its crown.
    rows = [(CAP * k / RINGS, radius) for k in range(1, RINGS + 1)]
    rows += [(CAP, radius + RIM), (CAP + 3, radius + RIM), (CAP + 3, radius - THICK)]
    rows += [(CAP * k / RINGS, radius - THICK) for k in range(RINGS, 0, -1)]
    verts.append(at(0, 0, radius))
    crown = 0
    starts = [ring(t, r) for t, r in rows]
    verts.append(at(0, 0, radius - THICK))
    inner = len(verts) - 1
    for i in range(SEGMENTS):
        j = (i + 1) % SEGMENTS
        faces.append((crown, starts[0] + i, starts[0] + j))
        faces.append((inner, starts[-1] + j, starts[-1] + i))
        for a, b in zip(starts, starts[1:]):
            faces.append((a + i, b + i, b + j, a + j))
    return verts, faces


body = bpy.data.objects['Body']
rig = body.find_armature()
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=ARMOUR, disable_bone_shape=True)
new = [o for o in bpy.data.objects if o not in before]
armour = next(o for o in new if o.type == 'MESH')
ab = bmesh.new()
ab.from_mesh(armour.data)
ab.transform(armour.matrix_world)
armour_tree = BVHTree.FromBMesh(ab)
armour_verts = [v.co.copy() for v in ab.verts]
for o in new:
    bpy.data.objects.remove(o)
head = bpy.data.objects.get('Head') or body
deps = bpy.context.evaluated_depsgraph_get()
hb = bmesh.new()
hb.from_object(head, deps)
hb.transform(head.matrix_world)
head_tree = BVHTree.FromBMesh(hb)

mesh = bpy.data.meshes.new(NAME)
obj = bpy.data.objects.new(NAME, mesh)
bpy.context.scene.collection.objects.link(obj)
all_verts, all_faces, groups_of = [], [], []
for side, sx in (('L', 1), ('R', -1)):
    joint = rig.matrix_world @ rig.data.bones['upper_arm.' + side].head_local
    out = Vector((sx, 0, 0))   # straight out to its side (the shoulder's line runs back, which put it behind the arm)
    middle = joint + out * OUT - Vector((0, 0, DOWN))
    axis = (out * math.cos(math.radians(TILT)) + Vector((0, 0, 1)) * math.sin(math.radians(TILT))).normalized()
    radius = RADIUS
    verts, faces = dome(middle, axis, radius, side)
    # How far the armour comes through it: the farthest armour vertex inside the dome's cone within its radius.
    cos_cap = math.cos(math.radians(CAP))
    inside = [p for p in armour_verts if 1e-6 < (p - middle).length and (p - middle).normalized().dot(axis) >= cos_cap]
    reach = max(((p - middle).length for p in inside if (p - middle).length < radius + 1.0), default=0.0)
    base = len(all_verts)
    all_verts += verts
    all_faces += [tuple(base + i for i in f) for f in faces]
    groups_of += [side] * len(verts)
    nearest = min(head_tree.find_nearest(p)[3] for p in verts)
    print('PAULDRON %s: middle %.2f %.2f %.2f, radius %.2f (the armour reaches %.2f within its cone), %.2f from the head at rest'
          % (side, middle.x, middle.y, middle.z, radius, reach, nearest))
mesh.from_pydata([tuple(v) for v in all_verts], [], all_faces)
mesh.update()
for p in mesh.polygons:
    p.use_smooth = True
bm = bmesh.new()
bm.from_mesh(mesh)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
bm.to_mesh(mesh)
bm.free()
for side in ('L', 'R'):
    for bone, w in WEIGHTS.items():
        g = obj.vertex_groups.new(name=bone + '.' + side)
        g.add([i for i, s in enumerate(groups_of) if s == side], w, 'REPLACE')
obj.parent = rig
obj.modifiers.new('Armature', 'ARMATURE').object = rig

bpy.ops.object.select_all(action='DESELECT')
rig.hide_set(False)
rig.hide_viewport = False
obj.select_set(True)
rig.select_set(True)
bpy.context.view_layer.objects.active = obj
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False, export_extras=True)
print('PAULDRONS: %d vertices, %d faces -> %s' % (len(mesh.vertices), len(mesh.polygons), out_glb))
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
