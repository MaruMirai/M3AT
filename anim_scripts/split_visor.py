"""Takes a helmet's visor off for a retexture and puts it back after, as its own material.

    blender -b <blend> --python anim_scripts/split_visor.py -- split <object> <shell.glb> <visor.glb>
    blender -b --python anim_scripts/split_visor.py -- join <placed shell.glb> <visor.glb> <out.glb> [out.blend]

The visor is the faces with every corner in the object's `Visor` vertex group (a vertex group, as the visor's edge ring
is shared with the shell). `split` writes the shell alone, to go through anim_scripts/generation/textures/prompts.py (Meshy
would paint the visor as more helmet), and the visor alone. `join` puts the visor back on the retextured shell with a
material of its own, `Visor` (a material slot survives a .glb where a vertex group without a skin does not), and welds
its edge ring to the shell's vertices at the same place, keeping the shell's UV seams.
"""
import sys

import bmesh
import bpy

VISOR = 'Visor'
WELD = 1e-5      # model units: how near a visor vertex must be to a shell vertex to be the same vertex
GLB_OPTIONS = dict(export_format='GLB', use_selection=True, export_animations=False, export_extras=True,
                   export_skins=False, export_morph=False)


def select_only(*objects):
    for o in bpy.context.scene.objects:
        o.select_set(o in objects)
    bpy.context.view_layer.objects.active = objects[0]


def copy_with(o, keep_visor):
    """A copy of o keeping only the visor's faces, or only the rest."""
    gi = o.vertex_groups[VISOR].index
    inside = {v.index for v in o.data.vertices for g in v.groups if g.group == gi and g.weight > 0}
    c = o.copy()
    c.data = o.data.copy()
    bpy.context.scene.collection.objects.link(c)
    bm = bmesh.new()
    bm.from_mesh(c.data)
    gone = [f for f in bm.faces if all(v.index in inside for v in f.verts) != keep_visor]
    bmesh.ops.delete(bm, geom=gone, context='FACES')
    bm.to_mesh(c.data)
    bm.free()
    c.vertex_groups.clear()
    c.shape_key_clear()
    c.modifiers.clear()
    c.parent = None
    c.matrix_world = o.matrix_world
    return c


def split(name, shell_out, visor_out):
    o = bpy.data.objects[name]
    for keep_visor, out in ((False, shell_out), (True, visor_out)):
        c = copy_with(o, keep_visor)
        select_only(c)
        bpy.ops.export_scene.gltf(filepath=out, export_materials='EXPORT', **GLB_OPTIONS)
        print('SPLIT %s: %d triangles to %s' % ('visor' if keep_visor else 'shell',
                                               sum(len(f.vertices) - 2 for f in c.data.polygons), out))


def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    for other in new:
        if other not in meshes:
            bpy.data.objects.remove(other)
    if len(meshes) != 1:
        sys.exit('%s: wants one mesh, has %d' % (path, len(meshes)))
    return meshes[0]


def join(shell_path, visor_path, out, blend_out=None):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    shell = load(shell_path)
    visor = load(visor_path)
    glass = bpy.data.materials.new(VISOR)
    glass.use_nodes = True
    bsdf = glass.node_tree.nodes['Principled BSDF']
    bsdf.inputs['Base Color'].default_value = (0.05, 0.07, 0.09, 1)
    bsdf.inputs['Roughness'].default_value = 0.1
    bsdf.inputs['Alpha'].default_value = 0.4
    visor.data.materials.clear()
    visor.data.materials.append(glass)
    shell_verts = len(shell.data.vertices)
    name = shell.name
    select_only(shell, visor)
    bpy.ops.object.join()
    o = bpy.context.view_layer.objects.active
    o.name = o.data.name = name

    # Weld each visor vertex onto the shell vertex at its place (the first, where a UV seam has split it), so the
    # shell's own vertices and seams stay as they came back.
    bm = bmesh.new()
    bm.from_mesh(o.data)
    bm.verts.ensure_lookup_table()
    at = {}
    for v in bm.verts[:shell_verts]:
        at.setdefault(v.co.to_tuple(5), v)
    targetmap, missed = {}, 0
    for v in bm.verts[shell_verts:]:
        hit = at.get(v.co.to_tuple(5))
        if hit is None:
            near = min(bm.verts[:shell_verts], key=lambda s: (s.co - v.co).length)
            hit = near if (near.co - v.co).length < WELD else None
        if hit is not None:
            targetmap[v] = hit
        else:
            missed += 1
    bmesh.ops.weld_verts(bm, targetmap=targetmap)
    bm.to_mesh(o.data)
    bm.free()
    o.data.update()
    select_only(o)
    bpy.ops.object.shade_smooth()
    bpy.ops.export_scene.gltf(filepath=out, export_materials='EXPORT', **GLB_OPTIONS)
    print('JOINED %s: %d visor vertices welded to the shell, %d free, %d triangles' % (
        name, len(targetmap), missed, sum(len(f.vertices) - 2 for f in o.data.polygons)))
    if blend_out:
        bpy.ops.wm.save_as_mainfile(filepath=blend_out)


a = sys.argv[sys.argv.index('--') + 1:]
if a[0] == 'split':
    split(a[1], a[2], a[3])
elif a[0] == 'join':
    join(*a[1:5])
else:
    sys.exit(__doc__)
