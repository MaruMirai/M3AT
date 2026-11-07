"""Takes a garment .glb's Base Color image out as a PNG, or puts one back in under a new name, for repainting it outside
Blender (prestige_paint.py).

    blender -b --python anim_scripts/swap_garment_image.py -- get <in.glb> <out.png>
    blender -b --python anim_scripts/swap_garment_image.py -- put <in.glb> <image.png> <out.glb> <Name>

put keeps the mesh, its UVs, its shape keys and any skin as the file has them, and names the mesh Name, which
import_garments takes as the garment's name only when the file has more than one mesh: it goes by the file's name
otherwise, so name the file after the garment too."""
import bpy, sys

args = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=args[1])
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
assert len(meshes) == 1, 'one mesh expected, found %d' % len(meshes)
mesh = meshes[0]

def base_colour_node(mat):
    for n in mat.node_tree.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            for link in n.inputs['Base Color'].links:
                if link.from_node.type == 'TEX_IMAGE':
                    return link.from_node
    raise SystemExit('no Base Color image on ' + mat.name)

node = base_colour_node(mesh.active_material)
if args[0] == 'get':
    img = node.image
    img.filepath_raw = args[2]
    img.file_format = 'PNG'
    img.save()
    print('GOT %s %dx%d' % (img.name, img.size[0], img.size[1]))
elif args[0] == 'put':
    new = bpy.data.images.load(args[2])
    new.pack()
    node.image = new
    mesh.name = args[4]
    mesh.data.name = args[4]
    bpy.ops.export_scene.gltf(filepath=args[3], export_format='GLB', export_morph=True, export_skins=True,
                              export_image_format='AUTO')
    print('PUT %s into %s' % (args[2], args[3]))
