"""Kid romper from the BasicShirt and Pants garments: the pants cut off a little above the shirt's hem (inside the
shirt), the shirt's lower part drawn in onto the pants so the two read as one piece, the textures put side by side
in one image (the pants' evened to the shirt's mean colour, as one stuff tints both). Writes <out.glb>, its image
as <out>.png and a .blend beside it:
    blender -b --python anim_scripts/make_romper.py -- <out.glb>
The glb goes in Models/garments/ as kidRomper.glb (the garment KidRomper, for Apparel_KidRomper)."""
import bpy, bmesh, os, sys
import numpy as np
from mathutils.bvhtree import BVHTree
G = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Models', 'garments') + os.sep
OUT = os.path.abspath(sys.argv[sys.argv.index('--') + 1])
CUT = 0.45      # units above the hem the pants are cut at
DRAW = 1.6      # units above the hem over which the shirt is drawn in
GAP = 0.06      # units the shirt is kept off the pants
bpy.ops.wm.read_factory_settings(use_empty=True)
for f in ('basicShirt.glb', 'Pants.glb'):
    bpy.ops.import_scene.gltf(filepath=G + f)
objs = {o.name: o for o in bpy.data.objects if o.type == 'MESH'}
s, p = objs['BasicShirt'], objs['Pants']
for o in (s, p):
    o.data.transform(o.matrix_world); o.matrix_world.identity()
    if o.data.has_custom_normals:   # the game welds its own normals; the pants' imported ones point in
        bpy.context.view_layer.objects.active = o; bpy.ops.mesh.customdata_custom_splitnormals_clear()

def base_image(o):
    for n in o.data.materials[0].node_tree.nodes:
        if n.type == 'TEX_IMAGE' and any(l.to_socket.name == 'Base Color' for l in n.outputs[0].links):
            return n.image
si, pi = base_image(s), base_image(p)
def pixels(img):
    a = np.array(img.pixels[:], dtype=np.float32).reshape(img.size[1], img.size[0], 4); return a

# The shirt's hem: its lowest boundary loop.
bm = bmesh.new(); bm.from_mesh(s.data)
hem = min(v.co.z for e in bm.edges if e.is_boundary for v in e.verts if v.co.z < 13)
hem = max(v.co.z for e in bm.edges if e.is_boundary for v in e.verts if v.co.z < hem + 0.5)
print('hem at', hem)
bm.free()

# Cut the pants.
bm = bmesh.new(); bm.from_mesh(p.data)
bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=(0, 0, hem + CUT), plane_no=(0, 0, 1), clear_outer=True)
bm.to_mesh(p.data); bm.free()

# Draw the shirt in onto the pants.
bm = bmesh.new(); bm.from_mesh(p.data); bm.normal_update()
tree = BVHTree.FromBMesh(bm)
moved = 0
for v in s.data.vertices:
    t = (v.co.z - hem) / DRAW
    if t >= 1: continue
    w = 1 - max(0.0, t); w = w * w * (3 - 2 * w)
    hit = tree.find_nearest(v.co)
    if hit[0] is None: continue
    loc, nrm = hit[0], hit[1]
    target = loc + nrm * GAP
    if (v.co - loc).dot(nrm) > GAP:   # only pull in, never push out
        v.co = v.co.lerp(target, w); moved += 1
print('shirt verts drawn in', moved)
bm.free()

# One image: shirt left, pants right, pants evened to the shirt's mean colour.
a, b = pixels(si), pixels(pi)
ma, mb = a[..., :3].reshape(-1, 3).mean(0), b[..., :3].reshape(-1, 3).mean(0)
b[..., :3] = np.clip(b[..., :3] * (ma / mb), 0, 1)
print('means', ma, mb)
h = max(a.shape[0], b.shape[0])
atlas = np.ones((h, a.shape[1] + b.shape[1], 4), np.float32)
atlas[:a.shape[0], :a.shape[1]] = a; atlas[:b.shape[0], a.shape[1]:] = b
img = bpy.data.images.new('KidRomper', atlas.shape[1], atlas.shape[0], alpha=True)
img.pixels.foreach_set(atlas.ravel())
img.filepath_raw = OUT.replace('.glb', '.png'); img.file_format = 'PNG'; img.save()
half = a.shape[1] / atlas.shape[1]
for o, off, sc in ((s, 0.0, half), (p, half, 1 - half)):
    for d in o.data.uv_layers.active.data:
        d.uv = (off + d.uv.x * sc, d.uv.y)
    o.data.uv_layers.active.name = 'UVMap'

mat = bpy.data.materials.new('KidRomper'); mat.use_nodes = True
bsdf = mat.node_tree.nodes['Principled BSDF']
tex = mat.node_tree.nodes.new('ShaderNodeTexImage'); tex.image = img
mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
bsdf.inputs['Roughness'].default_value = 1.0
for o in (s, p):
    o.data.materials.clear(); o.data.materials.append(mat)
bpy.ops.object.select_all(action='DESELECT')
s.select_set(True); p.select_set(True); bpy.context.view_layer.objects.active = s
bpy.ops.object.join()
s.name = 'KidRomper'; s.data.name = 'KidRomper'
for o in [o for o in bpy.data.objects if o is not s]: bpy.data.objects.remove(o)
print('romper', len(s.data.vertices), 'verts', len(s.data.polygons), 'faces')
bpy.ops.export_scene.gltf(filepath=OUT, use_selection=True, export_image_format='AUTO')
bpy.ops.wm.save_as_mainfile(filepath=OUT.replace('.glb', '.blend'))
