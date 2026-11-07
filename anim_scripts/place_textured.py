"""Takes a garment to Meshy's retexture and back, keeping it where it was and what it had on it.

    blender -b --python anim_scripts/place_textured.py -- strip <garment.glb> <input.glb> [--keep-uvs]
    blender -b --python anim_scripts/place_textured.py -- place <garment.glb> <textured.glb> <out.glb>
    blender -b --python anim_scripts/place_textured.py -- view <garment.glb> <prefix>

`strip` writes the mesh alone (no UVs, materials, keys or skin; --keep-uvs leaves the UVs, for a retexture told to paint
on them, `meshy.py multitexture --keep-uvs`), the rest shape, to upload. `place` puts what came back
on the garment's own vertices: Meshy keeps the triangles and splits vertices at its UV seams, but returns the mesh centred
with its longest side 2, so it is fitted back to the garment's box and each vertex then snapped to the garment's vertex
under it (stopping if one has none). A garment that keeps its weights (export_frames.py's KEEP_WEIGHTS) or has its own
shape keys gets them back through that same match, and its rig comes along. `view` renders <prefix>_front.png, _side.png
and _back.png.
"""
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector, kdtree

KEEP_WEIGHTS = ('Duster', 'TribalA', 'Parka', 'Cape', 'Robe', 'RobeRoyal', 'Burka', 'LabCoat')   # as export_frames.py's
MATCH = 1e-3     # of the garment's size: how far off a vertex may come back and still be the same vertex
GLB_OPTIONS = dict(export_format='GLB', use_selection=True, export_animations=False, export_extras=True)


def load(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    if len(meshes) != 1:
        sys.exit('%s: wants one mesh, has %d' % (path, len(meshes)))
    return meshes[0], new


def world_places(o):
    return [o.matrix_world @ v.co for v in o.data.vertices]


def box(places):
    return (Vector(min(p[i] for p in places) for i in range(3)),
            Vector(max(p[i] for p in places) for i in range(3)))


def select_only(*objects):
    for o in bpy.context.scene.objects:
        o.select_set(o in objects)
    bpy.context.view_layer.objects.active = objects[0]


def triangles(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


def strip(src, out, keep_uvs=False):
    o, new = load(src)
    for other in new:
        if other is not o:
            bpy.data.objects.remove(other)
    o.shape_key_clear()
    o.vertex_groups.clear()
    o.modifiers.clear()
    o.data.materials.clear()
    while o.data.uv_layers and not keep_uvs:
        o.data.uv_layers.remove(o.data.uv_layers[0])
    o.parent = None
    select_only(o)
    bpy.ops.export_scene.gltf(filepath=out, export_skins=False, export_morph=False, export_materials='NONE', **GLB_OPTIONS)
    print('STRIPPED %s: %d triangles' % (src, triangles(o)))


def by_triangle(garment, tex, match, name):
    """Where the garment has two vertices at one place (the two sides of a vent) the nearest is either, and the wrong one
    has the other side's weights. So each triangle that came back is paired with the garment's at the same three places,
    which says which of them each corner is."""
    place_of = {}
    for i, v in enumerate(garment.data.vertices):
        place_of.setdefault(v.co.to_tuple(5), []).append(i)
    twins = {i: tuple(group) for group in place_of.values() if len(group) > 1 for i in group}
    if not twins:
        return match
    place = {i: min(twins.get(i, (i,))) for i in range(len(garment.data.vertices))}
    faces = {}
    for f in garment.data.polygons:
        faces.setdefault(frozenset(place[i] for i in f.vertices), []).append(f)
    settled, clashes = {}, 0
    for f in tex.data.polygons:
        same = faces.get(frozenset(place[match[i]] for i in f.vertices))
        if not same:
            sys.exit('%s: a triangle came back that the garment has not' % name)
        # Twin triangles (a doubled sheet) are told apart by their order, as each is met once.
        theirs = same.pop(0) if len(same) > 1 else same[0]
        for i in f.vertices:
            at = place[match[i]]
            mine = next(j for j in theirs.vertices if place[j] == at)
            clashes += settled.get(i, mine) != mine
            settled[i] = mine
    print('TWINS %s: %d vertices share a place, %d corners disagree' % (name, len(twins), clashes))
    return [settled.get(i, m) for i, m in enumerate(match)]


def place(src, textured, out):
    name = os.path.splitext(os.path.basename(src))[0]
    name = name[:1].upper() + name[1:]
    garment, garment_new = load(src)
    rig = next((o for o in garment_new if o.type == 'ARMATURE'), None)
    tex, tex_new = load(textured)
    for other in tex_new:
        if other is not tex:
            bpy.data.objects.remove(other)
    if triangles(tex) != triangles(garment):
        sys.exit('%s: %d triangles came back from %d' % (name, triangles(tex), triangles(garment)))

    # The fit, then the match, in the world's places.
    was = world_places(garment)
    came = world_places(tex)
    (low, high), (tex_low, tex_high) = box(was), box(came)
    size = (high - low).length
    fitted = [Vector(low[i] + (p[i] - tex_low[i]) * (high[i] - low[i]) / max(tex_high[i] - tex_low[i], 1e-9) for i in range(3))
              for p in came]
    tree = kdtree.KDTree(len(was))
    for i, p in enumerate(was):
        tree.insert(p, i)
    tree.balance()
    under = [tree.find(p) for p in fitted]
    worst = max(u[2] for u in under) / size
    if worst > MATCH:
        sys.exit('%s: a vertex came back %.5f of its size from any of the garment\'s' % (name, worst))
    match = by_triangle(garment, tex, [u[1] for u in under], name)

    # Back on the garment's own vertices, in its own object space, so the file sits on the rig as it did.
    tex.parent = None
    tex.matrix_world = garment.matrix_world
    for i, v in enumerate(tex.data.vertices):
        v.co = garment.data.vertices[match[i]].co
    tex.data.update()

    groups = name in KEEP_WEIGHTS
    keys = garment.data.shape_keys
    if groups:
        for g in garment.vertex_groups:
            tex.vertex_groups.new(name=g.name)
        for i, v in enumerate(tex.data.vertices):
            for x in garment.data.vertices[match[i]].groups:
                tex.vertex_groups[garment.vertex_groups[x.group].name].add([i], x.weight, 'REPLACE')
        tex.parent = rig
        tex.matrix_world = garment.matrix_world
        tex.modifiers.new('Armature', 'ARMATURE').object = rig
    if keys:
        for block in keys.key_blocks:
            new = tex.shape_key_add(name=block.name, from_mix=False)
            new.value = 0.0   # a new key comes in at 1, which would write every body key mixed in
            for i in range(len(tex.data.vertices)):
                new.data[i].co = block.data[match[i]].co
    for k in garment.keys():
        tex[k] = garment[k]

    bpy.data.objects.remove(garment)
    tex.name = name
    tex.data.name = name
    select_only(tex)
    bpy.ops.object.shade_smooth()
    if groups:
        rig.hide_set(False)
        select_only(tex, rig)
        bpy.ops.export_scene.gltf(filepath=out, export_skins=True, export_all_influences=True, export_morph=bool(keys), **GLB_OPTIONS)
    else:
        bpy.ops.export_scene.gltf(filepath=out, export_skins=False, export_morph=bool(keys), **GLB_OPTIONS)
    print('PLACED %s: %d vertices from %d, off by at most %.6f of its size, %s, %d keys' % (
        name, len(tex.data.vertices), len(was), worst, 'its weights' if groups else 'no weights', len(keys.key_blocks) if keys else 0))


def render(path, prefix):
    """World light only, as a sun burns hot spots into the look of a metal."""
    o, new = load(path)
    low, high = box(world_places(o))
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE'
    sc.world = bpy.data.worlds.new('w')
    sc.world.use_nodes = True
    sc.world.node_tree.nodes['Background'].inputs[0].default_value = (1, 1, 1, 1)
    sc.render.resolution_x = sc.render.resolution_y = 500
    camera = bpy.data.cameras.new('c')
    camera.type = 'ORTHO'
    camera.ortho_scale = (high - low).length * 1.05
    cam = bpy.data.objects.new('c', camera)
    sc.collection.objects.link(cam)
    sc.camera = cam
    mid = (low + high) / 2
    for view, angle in (('front', 0), ('side', 90), ('back', 180)):
        r = math.radians(angle)
        cam.location = mid + Vector((math.sin(r), -math.cos(r), 0)) * (high - low).length * 3
        cam.rotation_euler = (math.pi / 2, 0, r)
        sc.render.filepath = '%s_%s.png' % (prefix, view)
        bpy.ops.render.render(write_still=True)


a = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
if a[0] == 'strip':
    strip(a[1], a[2], '--keep-uvs' in a)
elif a[0] == 'place':
    place(a[1], a[2], a[3])
elif a[0] == 'view':
    render(a[1], a[2])
else:
    sys.exit(__doc__)
