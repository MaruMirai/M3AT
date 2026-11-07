"""Makes a garment from each of the body's dashed vertex groups (`-BasicShirt`, `-Pants`, ...): the group's faces copied
and pushed out along the normals, with the body's own weights, so it bends exactly as the skin under it does. A group
naming more than one garment (`-CollarShirt -Jacket`) makes each of them from the same faces. Each body key's shape is
built the same way from the keyed body and goes in as the garment's own key.

A garment another script makes from its group (`OWN_SCRIPT`: TribalA, by make_tribal.py) or with a mesh of its own already
in the .blend (one modelled by hand) is left alone; one this script made before (custom property `from_group`) is made
again. Name garments after the script's arguments to make only those.

How far out each garment stands is by its apparel layer (`LAYER_OFFSET`), so a jacket made from the same faces as a
shirt goes over it, and the game's garment-over-garment hiding leaves the shirt out under it. One surface, with the
body's own UVs (unwrap_pawn.py's atlas) and no image: drawn in the apparel's colour until it is given one.

Each is written to <out folder>/<Name>.glb, as export_frames.py's import_garments takes it (Models/garments/ is the
folder it reads): the mesh at rest with its keys, its weights and its custom properties. --blend also saves the .blend
with them on the rig; it never saves over the .blend it opened.

blender -b Models/pawnHumanRig.blend --python anim_scripts/make_from_groups.py -- <out folder> [garment ...] [--blend out.blend]
"""
import bpy, os, sys
from mathutils import Vector

# The apparel layer and defs of each garment. A garment not listed is OnSkin. With no defs it is drawn for Apparel_<name>,
# or for CE_<name> Combat Extended's CE_Apparel_<name> (export_frames.py's default_defs).
GARMENTS = {
    'CE_CompositeVest': ('Middle', None),
    'CE_ExoFrame': ('Shell', None),
    'BasicShirt': ('OnSkin', None),
    'CollarShirt': ('OnSkin', None),
    'Pants': ('OnSkin', None),
    'Corset': ('Middle', None),
    'Sash': ('Middle', None),
    'VestRoyal': ('Middle', None),
    'FlakVest': ('Middle', None),
    'PsyfocusVest': ('Middle', None),
    'Jacket': ('Shell', None),
    'FlakJacket': ('Shell', None),
    'KidRomper': ('OnSkin', None),
    'Vacsuit': ('Shell', None),
}
OWN_SCRIPT = {'TribalA'}   # these groups' garments have more than the group's faces, and a script of their own
LAYER_OFFSET = {'OnSkin': 0.1, 'Middle': 0.2, 'Shell': 0.3, 'Webbing': 0.4}   # Blender units off the skin (0.1 is about 0.008 leg lengths, as make_tribal.py's)
OWN_OFFSET = {'CE_CompositeVest': 0.25, 'Shoes': 0.15}   # in place of its layer's: bulkier than the flak vest, still under a jacket at 0.3; shoes a little fuller than the trousers they meet
# Garments made from what a group leaves bare below it rather than from a group: every body face not in that group's
# faces and joined (by edges, through such faces) to the body's lowest vertex. So far the shoes, from the feet and
# shins below the trousers' cuffs; they meet the trousers along the cuffs' own edges.
BELOW = {'Shoes': '-Pants'}
# Garments made from every face of the Body (the head is its own mesh part): so far Odyssey's vacsuit, gloves and boots
# and all, at the Shell layer's offset, as nothing is worn over it. (make_vacsuit.py makes it the other way, a smoothed
# shell pushed off the skin.)
WHOLE = {'Vacsuit'}

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
blend = None
if '--blend' in argv:
    k = argv.index('--blend')
    blend = argv[k + 1]
    del argv[k:k + 2]
if not argv:
    sys.exit('make_from_groups.py: give an out folder')
out, only = os.path.abspath(argv[0]), set(argv[1:])
os.makedirs(out, exist_ok=True)

for o in bpy.data.objects:   # a mesh saved in Edit Mode has its edits outside its data, and skips its Armature modifier
    if o.type == 'MESH' and o.mode != 'OBJECT':
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode='OBJECT')
body = bpy.data.objects['Body']
rig = body.find_armature()
me = body.data
names = {g.index: g.name for g in body.vertex_groups}
bones = {b.name for b in rig.data.bones if b.use_deform}

def weights_of(i):
    ws = {names[g.group]: g.weight for g in me.vertices[i].groups if names[g.group] in bones and g.weight > 0}
    total = sum(ws.values())
    return {b: w / total for b, w in ws.items()}

def normals(pos):
    n = [Vector() for _ in pos]
    for p in me.polygons:
        vs = list(p.vertices)
        a = pos[vs[0]]
        for k in range(1, len(vs) - 1):
            c = (pos[vs[k]] - a).cross(pos[vs[k + 1]] - a)   # area weighted
            for i in (vs[0], vs[k], vs[k + 1]):
                n[i] += c
    return [x.normalized() for x in n]

def make(name, faces, uvs, offset):
    order = sorted({i for f in faces for i in f})
    index = {i: k for k, i in enumerate(order)}
    def build(pos):
        n = normals(pos)
        return [pos[i] + n[i] * offset for i in order]
    old = bpy.data.objects.get(name)
    if old is not None:
        bpy.data.objects.remove(old)
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(v) for v in build([v.co.copy() for v in me.vertices])], [], [[index[i] for i in f] for f in faces])
    if uvs:
        uv_map = mesh.uv_layers.new(name='UVMap')
        for k, uv in enumerate(uvs):   # from_pydata keeps the corners in the order the faces gave them
            uv_map.uv[k].vector = uv
    for p in mesh.polygons:
        p.use_smooth = True
    mesh.update()
    g = bpy.data.objects.new(name, mesh)
    body.users_collection[0].objects.link(g)
    g.parent = rig
    g.matrix_world = body.matrix_world.copy()
    g.modifiers.new('Armature', 'ARMATURE').object = rig
    g['from_group'] = True
    layer, defs = GARMENTS.get(name, ('OnSkin', None))
    if defs:
        g['defs'] = defs
    for i in order:
        for b, w in weights_of(i).items():
            if w > 1e-4:
                (g.vertex_groups.get(b) or g.vertex_groups.new(name=b)).add([index[i]], w, 'REPLACE')
    if me.shape_keys:
        g.shape_key_add(name='Basis', from_mix=False)
        for kb in me.shape_keys.key_blocks[1:]:
            key = g.shape_key_add(name=kb.name, from_mix=False)
            key.value = 0.0
            for k, p in enumerate(build([Vector(d.co) for d in kb.data])):
                key.data[k].co = p
    print('GROUP %s: %d vertices, %d faces, %s at %.2f%s' % (name, len(order), len(faces), layer, offset,
                                                              ', defs ' + defs if defs else ''))
    return g

def export(g):
    """The garment as a .glb, skinned to the rig so its weights go along (import_garments drops the rig and puts it on
    the .blend's own)."""
    bpy.ops.object.select_all(action='DESELECT')
    g.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = g
    path = os.path.join(out, g.name + '.glb')
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format='GLB', export_extras=True,
                              export_skins=True, export_morph=True, export_morph_normal=False,
                              export_animations=False, export_apply=False, export_materials='NONE')
    print('GLB %s' % path)

def below(group):
    """The body's faces under the group's: those with a corner off it, reached from the lowest vertex across edges
    with a corner off it, so the flood stops at the group's faces."""
    vg = body.vertex_groups[group]
    inside = {v.index for v in me.vertices if any(x.group == vg.index and x.weight > 0 for x in v.groups)}
    covered = {p.index for p in me.polygons if all(i in inside for i in p.vertices)}
    by_vertex = {}
    for p in me.polygons:
        if p.index not in covered:
            for i in p.vertices:
                by_vertex.setdefault(i, []).append(p.index)
    taken = set()
    for side in (1, -1):   # each foot: the lowest vertex on that side of the middle
        start = min((v for v in me.vertices if (body.matrix_world @ v.co).x * side > 0 and v.index in by_vertex),
                    key=lambda v: (body.matrix_world @ v.co).z)
        stack = list(by_vertex[start.index])
        while stack:
            k = stack.pop()
            if k in taken:
                continue
            taken.add(k)
            for i in me.polygons[k].vertices:
                stack.extend(j for j in by_vertex.get(i, ()) if j not in taken)
    return [me.polygons[k] for k in sorted(taken)]

for name, group in BELOW.items():
    if only and name not in only:
        continue
    have = bpy.data.objects.get(name)
    if have is not None and not have.get('from_group'):
        print('GROUP %s: has a mesh of its own, left alone' % name)
        continue
    polys = below(group)
    body_uv = me.uv_layers.active.uv if me.uv_layers.active else None
    uvs = [body_uv[k].vector.copy() for p in polys for k in p.loop_indices] if body_uv else None
    export(make(name, [list(p.vertices) for p in polys], uvs, OWN_OFFSET.get(name, LAYER_OFFSET['OnSkin'])))

for name in WHOLE:
    if only and name not in only:
        continue
    have = bpy.data.objects.get(name)
    if have is not None and not have.get('from_group'):
        print('GROUP %s: has a mesh of its own, left alone' % name)
        continue
    polys = list(me.polygons)
    body_uv = me.uv_layers.active.uv if me.uv_layers.active else None
    uvs = [body_uv[k].vector.copy() for p in polys for k in p.loop_indices] if body_uv else None
    export(make(name, [list(p.vertices) for p in polys], uvs, OWN_OFFSET.get(name, LAYER_OFFSET[GARMENTS[name][0]])))

for vg in body.vertex_groups:
    if not vg.name.startswith('-'):
        continue
    inside = {v.index for v in me.vertices if any(x.group == vg.index and x.weight > 0 for x in v.groups)}
    polys = [p for p in me.polygons if all(i in inside for i in p.vertices)]
    faces = [list(p.vertices) for p in polys]
    body_uv = me.uv_layers.active.uv if me.uv_layers.active else None
    uvs = [body_uv[k].vector.copy() for p in polys for k in p.loop_indices] if body_uv else None
    for name in vg.name.split():
        name = name.lstrip('-')
        if only and name not in only:
            continue
        if name in OWN_SCRIPT:
            print('GROUP %s: has a script of its own, left alone' % name)
            continue
        have = bpy.data.objects.get(name)
        if have is not None and not have.get('from_group'):
            print('GROUP %s: has a mesh of its own, left alone' % name)
            continue
        if not faces:
            print('GROUP %s: no faces in %s' % (name, vg.name))
            continue
        export(make(name, faces, uvs, OWN_OFFSET.get(name, LAYER_OFFSET[GARMENTS.get(name, ('OnSkin', None))[0]])))

if blend:
    bpy.ops.wm.save_as_mainfile(filepath=blend, copy=True)
