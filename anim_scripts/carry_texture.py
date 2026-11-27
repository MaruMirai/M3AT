"""Carries a garment's texture over to a remade mesh of it, with no retexture: the new mesh's own UVs painted from the old.

    blender -b --python anim_scripts/carry_texture.py -- <new garment.glb> <old textured garment.glb> <textured out.glb>
    blender -b --python anim_scripts/place_textured.py -- place <new garment.glb> <textured out.glb> <garment out.glb>

For when a make script is re-run over a garment that was textured since (make_duster.py's skirts made lower poly, say):
the new mesh is nearly where the old one was, so each texel of each new triangle (at its UVs, make_duster.py's add_uvs)
is laid onto the nearest old triangle in 3D and read from the old image there (glb_edit.bake, by distance, Catmull-Rom),
then painted again without its padding, so no triangle's padding covers a neighbour's inside. The image is the old one's
size and kind (JPEG at quality 95, or PNG); what no triangle covers is the old image's median colour, so a downscaled
image doesn't bleed dark at the seams. Writes the mesh alone with the image on its material, as Meshy's retexture would
come back, for place_textured.py's place to put on the garment with its rig, weights and keys.
"""
import io
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import PAD, Image, Prim, bake, image_of, read_glb, view_bytes, world_matrix

NEAR = 0.8      # Blender units past both triangles' reach an old triangle may be and still be read from

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if len(argv) != 3:
    sys.exit('carry_texture.py: give the new garment, the old textured one and the out .glb')
new_path, old_path, out = (os.path.abspath(a) for a in argv)


def mesh_of(path):
    j, binary = read_glb(path)
    stem = os.path.splitext(os.path.basename(path))[0]
    nodes = [nd for nd in j['nodes'] if 'mesh' in nd]
    nd = next((nd for nd in nodes if nd.get('name', '').lower() == stem.lower()), None) or \
        max(nodes, key=lambda nd: j['accessors'][j['meshes'][nd['mesh']]['primitives'][0]['attributes']['POSITION']]['count'])
    mi = nd['mesh']
    prim = j['meshes'][mi]['primitives'][0]
    pr = Prim(j, binary, mi, prim)
    M = world_matrix(j, mi)
    world = pr.attrs['POSITION'] @ M[:3, :3].T + M[:3, 3]
    return j, binary, prim, pr, world


j_old, bin_old, prim_old, old, old_pos = mesh_of(old_path)
_, _, _, new, new_pos = mesh_of(new_path)
im = image_of(j_old, prim_old)
if im is None:
    sys.exit('carry_texture.py: %s has no image' % old_path)
mime = j_old['images'][im].get('mimeType', 'image/png')
pil = Image.open(io.BytesIO(bytes(view_bytes(j_old, bin_old, j_old['images'][im]['bufferView']))))
src = np.asarray(pil.convert('RGB'), dtype=np.float32)
H, W = src.shape[:2]
picture = np.empty_like(src)
picture[:] = np.median(src.reshape(-1, 3), 0)
if 'TEXCOORD_0' not in new.attrs:
    sys.exit('carry_texture.py: %s has no UVs' % new_path)
uv_new = new.attrs['TEXCOORD_0']
uv_old = old.attrs['TEXCOORD_0']

# Each old triangle's middle and reach, to pick the few near each new one.
old_tris = old.tri
mids = old_pos[old_tris].mean(1)
reach = np.linalg.norm(old_pos[old_tris] - mids[:, None, :], axis=2).max(1)

for pad in (PAD, 0):
    for t in new.tri:
        t = [int(v) for v in t]
        pts = new_pos[t]
        mid = pts.mean(0)
        r = np.linalg.norm(pts - mid, axis=1).max()
        near = np.nonzero(np.linalg.norm(mids - mid, axis=1) < r + reach + NEAR)[0]
        if len(near) == 0:
            sys.exit('carry_texture.py: no old triangle near the new one at %s' % mid.round(2))
        bake(src, picture, uv_new[t] * [W, H], pts, [([int(v) for v in old_tris[k]], None) for k in near],
             old_pos, uv_old, W, H, pad, True)

# The mesh alone with the image on its material, for place_textured.py's place.
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=new_path, disable_bone_shape=True)
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
o = max(meshes, key=lambda m: len(m.data.vertices))
for other in list(bpy.data.objects):
    if other is not o:
        bpy.data.objects.remove(other)
o.modifiers.clear()
o.vertex_groups.clear()
if o.data.shape_keys:
    o.shape_key_clear()
ext = '.jpg' if mime == 'image/jpeg' else '.png'
img_path = os.path.splitext(out)[0] + ext
done = Image.fromarray(np.clip(np.round(picture), 0, 255).astype(np.uint8))
if ext == '.jpg':
    done.save(img_path, 'JPEG', quality=95, subsampling=0)
else:
    done.save(img_path, 'PNG')
img = bpy.data.images.load(img_path)
img.pack()
mat = bpy.data.materials.new(o.name)
mat.use_nodes = True
bsdf = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
node = mat.node_tree.nodes.new('ShaderNodeTexImage')
node.image = img
mat.node_tree.links.new(node.outputs['Color'], bsdf.inputs['Base Color'])
o.data.materials.clear()
o.data.materials.append(mat)
for x in bpy.context.scene.objects:
    x.select_set(x is o)
bpy.context.view_layer.objects.active = o
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', use_selection=True, export_animations=False,
                          export_skins=False, export_morph=False)
print('CARRIED %s: %d triangles painted from %d, a %dx%d %s' % (os.path.basename(new_path), len(new.tri), len(old_tris), W, H, ext[1:]))
