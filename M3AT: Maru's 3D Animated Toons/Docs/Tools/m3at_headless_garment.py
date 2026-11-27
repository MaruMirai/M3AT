# A garment made from M3AT's own body with no clicking: the recommended way for scripts and AI assistants (Docs/AGENTS.md).
#
# It runs the garment kit (m3at_garment_kit.py, beside this file) in Blender without a window: imports M3AT's body,
# selects the body's faces weighted to the bones you name, pushes them out off the skin as a garment (the kit's
# "Garment from selected faces", Docs/Apparel.md Approach 2), gives it texture places and an image if you have one, and
# exports garment_<Name>.txt into an M3ATgarments folder. The garment has exactly as many triangles as the skin it
# covers (a long-sleeved shirt is a few hundred), bends with the body's own weights and follows every body type.
#
#   blender -b --python m3at_headless_garment.py -- --chibi "<M3AT>/Models/chibi" --out "<your mod>/M3ATgarments" \
#       --name MyShirt --defs MyMod_Shirt --layer OnSkin --bones spine.001 spine.002 spine.003 spine.004 shoulder.L shoulder.R upper_arm.L upper_arm.R \
#       [--image shirt.png] [--extra 0.002]
#
# --bones: a body face is taken when each of its corners has at least --share (default 0.2) of its weight on these
#   bones (0.2 by default: the faces along the garment's edges share their corners with the next bones). Bones: spine (hips) spine.001 .. spine.003 (belly to chest) spine.004/.005 (neck) spine.006 (head),
#   shoulder/upper_arm/forearm/hand .L/.R, pelvis/thigh/shin/foot/toe .L/.R.
# --image: painted for the garment's texture places (a front-on projection is made here; paint or bake over it in
#   Blender for a better one). Without it the garment is drawn in the apparel's colour.
import argparse
import importlib.util
import os
import sys

import bpy

here = os.path.dirname(os.path.abspath(__file__))
args = argparse.ArgumentParser()
args.add_argument('--chibi', required=True, help="M3AT's Models/chibi folder (skin.txt, anim.txt)")
args.add_argument('--out', required=True, help="your mod's M3ATgarments folder")
args.add_argument('--name', required=True)
args.add_argument('--defs', nargs='+', required=True, help='the apparel defNames it is drawn for')
args.add_argument('--layer', default='OnSkin', choices=['Underwear', 'OnSkin', 'Middle', 'Shell', 'Webbing'])
args.add_argument('--bones', nargs='+', required=True)
args.add_argument('--share', type=float, default=0.2)
args.add_argument('--extra', type=float, default=0.0, help='cells further out than the layer puts it (a bulkier garment)')
args.add_argument('--image', default=None)
a = args.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])

spec = importlib.util.spec_from_file_location('m3at_garment_kit', os.path.join(here, 'm3at_garment_kit.py'))
kit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(kit)
kit.register()

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)
scene = bpy.context.scene
scene.m3at_kit.chibi = a.chibi
scene.m3at_kit.out = a.out
bpy.ops.m3at.import_body()
body = bpy.data.objects[kit.BODY]

# The body's faces on those bones.
wanted = {g.index for g in body.vertex_groups if g.name in a.bones}
missing = set(a.bones) - {g.name for g in body.vertex_groups}
if missing:
    sys.exit('no such bones: %s' % ', '.join(sorted(missing)))
on = [sum(e.weight for e in v.groups if e.group in wanted) >= a.share for v in body.data.vertices]
picked = 0
for p in body.data.polygons:
    p.select = all(on[i] for i in p.vertices)
    picked += p.select
if picked == 0:
    sys.exit('no body faces have %.0f%% of their weight on those bones' % (a.share * 100))
bpy.ops.m3at.from_faces('EXEC_DEFAULT', name=a.name, layer=a.layer, extra=a.extra)
garment = bpy.data.objects[a.name]
garment.m3at.defs = ' '.join(a.defs)

# Texture places: a front-on projection (x across, height up), so a front sprite lands where it would be seen.
if a.image:
    mesh = garment.data
    uv = mesh.uv_layers.new(name='UVMap')
    xs = [v.co.x for v in mesh.vertices]
    zs = [v.co.z for v in mesh.vertices]
    x0, x1, z0, z1 = min(xs), max(xs), min(zs), max(zs)
    for poly in mesh.polygons:
        for li in poly.loop_indices:
            co = mesh.vertices[mesh.loops[li].vertex_index].co
            uv.data[li].uv = ((co.x - x0) / max(x1 - x0, 1e-6), (co.z - z0) / max(z1 - z0, 1e-6))
    mat = bpy.data.materials.new(a.name)
    mat.use_nodes = True
    node = mat.node_tree.nodes.new('ShaderNodeTexImage')
    node.image = bpy.data.images.load(os.path.abspath(a.image))
    mat.node_tree.links.new(node.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    mesh.materials.append(mat)

for o in bpy.context.selected_objects:
    o.select_set(False)
garment.select_set(True)
bpy.context.view_layer.objects.active = garment
bpy.ops.m3at.export()
tris = sum(len(p.vertices) - 2 for p in garment.data.polygons)
print('M3AT: %s written to %s, %d triangles (budget %d)' % (a.name, a.out, tris, kit.TRIANGLES_MOST))
