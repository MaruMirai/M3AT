"""Takes the top of a dress edited by hand (above the skirt's waist ring) out of a .blend, as the dress top the dresses
are all made with (make_skirts.sh, reweight_skirt.py's --dress-top).

    blender -b <the .blend> --python anim_scripts/extract_dress_top.py -- <dress object> <a skirt master .glb> <out.glb>

The skirt masters (Models/bases/skirts/) all share one waist ring; a face of the dress is the top's if its middle is
above the ring's height at its turn round the body (about the ring's middle). Written in the body's frame, unweighted
and without keys (reweight_skirt.py weights it from the body under it; the export fits its keys). Made first from
Models/unused/vneckdress.blend's DressShort (Maru's V neck, 2026-11-24).
"""
import bpy, bmesh, math, os, sys
from mathutils import Vector

argv = sys.argv[sys.argv.index('--') + 1:]
if len(argv) < 3:
    sys.exit('extract_dress_top.py: give <dress object> <skirt master .glb> <out.glb>')
name, skirt_path, out = argv[0], os.path.abspath(argv[1]), os.path.abspath(argv[2])
for o in bpy.data.objects:
    if o.type == 'MESH' and o.mode != 'OBJECT':
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode='OBJECT')
body = bpy.data.objects['Body']
to_body = body.matrix_world.inverted()
dress = bpy.data.objects[name]

# The skirt's waist ring: its highest open loop, in the body's frame.
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=skirt_path, disable_bone_shape=True)
came = [o for o in bpy.data.objects if o not in before]
sk = next(o for o in came if o.type == 'MESH')
sb = bmesh.new()
sb.from_mesh(sk.data)
sb.transform(to_body @ sk.matrix_world)
bmesh.ops.remove_doubles(sb, verts=sb.verts, dist=1e-4)
for o in came:
    bpy.data.objects.remove(o)
open_verts = {v for e in sb.edges if e.is_boundary for v in e.verts}
top_z = max(v.co.z for v in open_verts)
ring = [v.co.copy() for v in open_verts if v.co.z > top_z - 1.0]
axis = sum(ring, Vector()) / len(ring)
by_angle = sorted((math.atan2(p.y - axis.y, p.x - axis.x), p.z) for p in ring)

def ring_z(c):
    a = math.atan2(c.y - axis.y, c.x - axis.x)
    k = next((i for i, (b, _) in enumerate(by_angle) if b >= a), len(by_angle))
    a0, z0 = by_angle[k - 1] if k > 0 else (by_angle[-1][0] - 2 * math.pi, by_angle[-1][1])
    a1, z1 = by_angle[k] if k < len(by_angle) else (by_angle[0][0] + 2 * math.pi, by_angle[0][1])
    return z0 + (z1 - z0) * (a - a0) / (a1 - a0) if a1 != a0 else z0

# The dress's faces above the ring.
db = bmesh.new()
db.from_mesh(dress.data)
db.transform(to_body @ dress.matrix_world)
bmesh.ops.remove_doubles(db, verts=db.verts, dist=1e-4)
below = [f for f in db.faces if f.calc_center_median().z < ring_z(f.calc_center_median())]
bmesh.ops.delete(db, geom=below, context='FACES')
for layer in list(db.verts.layers.deform):
    db.verts.layers.deform.remove(layer)
for layer in list(db.verts.layers.shape):
    db.verts.layers.shape.remove(layer)
me = bpy.data.meshes.new('DressTop')
db.to_mesh(me)
top = bpy.data.objects.new('DressTop', me)
bpy.context.scene.collection.objects.link(top)
top.matrix_world = body.matrix_world.copy()
print('TOP %s: %d vertices, %d faces above the ring (%d below left out), %.2f .. %.2f high'
      % (name, len(me.vertices), len(me.polygons), len(below), min(v.co.z for v in me.vertices), max(v.co.z for v in me.vertices)))
bpy.ops.object.select_all(action='DESELECT')
top.select_set(True)
bpy.context.view_layer.objects.active = top
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_skins=False, export_morph=False,
                          export_animations=False, export_apply=False, export_materials='NONE')
print('GLB %s' % out)
