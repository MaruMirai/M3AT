"""Takes a textured high-poly garment to the new style: welded, decimated to a number of triangles, its UVs and images
dropped, so it is drawn in the apparel's colour. For one with no script of its own (make_hats.py makes the other hats).

    blender -b --python anim_scripts/decimate_garment.py -- <in.glb> <out.glb> <triangles>

Written unskinned, as export_frames.py's import_garments takes it."""
import bpy, sys

argv = sys.argv[sys.argv.index('--') + 1:]
WELD = 0.0005   # Blender units: the split copies a texture's seams leave are put back together, or decimating opens them

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=argv[0], disable_bone_shape=True)
o = [x for x in bpy.data.objects if x.type == 'MESH'][0]
bpy.context.view_layer.objects.active = o
o.select_set(True)
for uv in list(o.data.uv_layers):
    o.data.uv_layers.remove(uv)
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.remove_doubles(threshold=WELD)
bpy.ops.object.mode_set(mode='OBJECT')
d = o.modifiers.new('Decimate', 'DECIMATE')
d.ratio = float(argv[2]) / len(o.data.polygons)
bpy.ops.object.modifier_apply(modifier='Decimate')
o.data.materials.clear()
o.data.materials.append(bpy.data.materials.new('Plain'))
bpy.ops.export_scene.gltf(filepath=argv[1], use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False)
print('GARMENT %s: %d vertices, %d triangles' % (argv[1], len(o.data.vertices), len(o.data.polygons)))
