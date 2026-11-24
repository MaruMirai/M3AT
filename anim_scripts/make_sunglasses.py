"""Makes the Sunglasses from the Glasses garment as it is in Models/garments (Maru's hand edit of make_hats.py's), so the
two keep one shape: the same mesh, its glass lenses (the Visor material, drawn see-through) made dark and opaque, so the
eyes are hidden behind them, the frame its own colour.

    blender -b --python anim_scripts/make_sunglasses.py -- <glasses.glb> <out sunglasses.glb>

The image is the glasses' frame colour beside LENS's (one 16 px square each), every face's texture places at the middle
of its square, as make_hats.py's coloured hats have them. Re-run it after the glasses change."""
import bpy, sys, os

LENS = (0.05, 0.05, 0.06)   # the lenses' colour
CELL = 16

argv = sys.argv[sys.argv.index('--') + 1:]
src, out = os.path.abspath(argv[0]), os.path.abspath(argv[1])

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
o = next(x for x in bpy.data.objects if x.type == 'MESH')
me = o.data
glass = [i for i, m in enumerate(me.materials) if m.name.startswith('Visor')]
if not glass:
    sys.exit('make_sunglasses.py: no Visor material on the glasses')
frame_mat = next(m for i, m in enumerate(me.materials) if i not in glass)
tex = next(n for n in frame_mat.node_tree.nodes if n.type == 'TEX_IMAGE')
old = tex.image
px = list(old.pixels)
frame = px[:3]   # the frame's colour: the image is flat colour

img = bpy.data.images.new('Sunglasses', CELL * 2, CELL, alpha=False)
pixels = []
for y in range(CELL):
    for x in range(CELL * 2):
        pixels += (list(frame) if x < CELL else list(LENS)) + [1.0]
img.pixels = pixels
img.pack()
tex.image = img
tex.interpolation = 'Closest'
frame_mat.name = 'Sunglasses'

uv = me.uv_layers.active
for p in me.polygons:
    lens = p.material_index in glass
    for li in p.loop_indices:
        uv.data[li].uv = (0.75 if lens else 0.25, 0.5)
    p.material_index = me.materials.find(frame_mat.name)
for i in sorted(glass, reverse=True):
    me.materials.pop(index=i)
o.name = me.name = 'Sunglasses'

bpy.ops.object.select_all(action='DESELECT')
o.select_set(True)
bpy.context.view_layer.objects.active = o
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False)
print('SUNGLASSES %d vertices, %d faces, %d lens faces, frame %s -> %s'
      % (len(me.vertices), len(me.polygons), sum(1 for p in me.polygons if p.loop_indices and uv.data[p.loop_indices[0]].uv[0] > 0.5),
         tuple(round(c, 3) for c in frame), out))
