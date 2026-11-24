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
import bpy, bmesh, math, numpy, os, sys
from mathutils import Vector, interpolate
from mathutils.bvhtree import BVHTree

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
    'Gloves': ('OnSkin', None),
    'Socks': ('OnSkin', None),
    'Boots': ('Middle', None),
    'Sandals': ('Middle', None),
}
OWN_SCRIPT = {'TribalA'}   # these groups' garments have more than the group's faces, and a script of their own
LAYER_OFFSET = {'OnSkin': 0.1, 'Middle': 0.2, 'Shell': 0.3, 'Webbing': 0.4}   # Blender units off the skin (0.1 is about 0.008 leg lengths, as make_tribal.py's)
OWN_OFFSET = {'CE_CompositeVest': 0.25, 'Shoes': 0.15, 'Socks': 0.11, 'Boots': 0.2, 'Sandals': 0.15}   # in place of its layer's: bulkier than the flak vest, still under a jacket at 0.3; shoes a little fuller than the trousers they meet
# Garments made from what a group leaves bare below it rather than from a group: every body face not in that group's
# faces and joined (by edges, through such faces) to the body's lowest vertex. So far the shoes, from the feet and
# shins below the trousers' cuffs; they meet the trousers along the cuffs' own edges.
BELOW = {'Shoes': '-Pants'}
# Garments made from every face of the Body (the head is its own mesh part): so far Odyssey's vacsuit, gloves and boots
# and all, at the Shell layer's offset, as nothing is worn over it. (make_vacsuit.py makes it the other way, a smoothed
# shell pushed off the skin.)
WHOLE = {'Vacsuit'}
# Footwear and gloves cut from the body by bmesh on a copy of it (body_copy), each new vertex taking its edge's weights,
# keys and texture places. Lengths are Blender units (the shin is about 5.3 long, the foot about 4.5 from heel to toe).
# Socks: the shoes' faces at less push-out. Boots: the shoes with their top ring extruded BOOT_UP up the shin and laid back
# on the skin. Sandals: the shoes cut down to a sole rim up to SANDAL_SOLE over the floor, straps across the foot
# (SANDAL_STRAPS: shares of the way from the heel to the toe tip), one round the ankle (SANDAL_ANKLE: heights over the
# floor of the rig) and a strip up the back of the heel (SANDAL_HEEL: the share of the foot's length from the heel).
# Gloves: the hands' faces and the forearms' up to a plane through the next ring up the forearm's well-placed vertices
# (those within GLOVE_NEAR of the forearm's length from the wrist), the long faces past it cut on it.
CUT = {'Socks', 'Boots', 'Sandals', 'Gloves'}
BOOT_UP = 0.7
BOOT_GAP = 0.06   # the boots' collar this far out past the trousers
SANDAL_SOLE = 0.35
SANDAL_STRAPS = ((0.36, 0.45), (0.62, 0.72))
SANDAL_ANKLE = (1.45, 1.8)
SANDAL_HEEL = (0.0, 0.08)
GLOVE_NEAR = 0.15
STRAP_CUT = 0.005   # the sandal's straps cut exactly, so their edges run straight
ON_CUT = 0.08   # a vertex this close to a cutting plane is taken as on it, so no sliver is cut beside it
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

def weights_of(i, src=None):
    src = src or body
    nm = {g.index: g.name for g in src.vertex_groups}
    ws = {nm[g.group]: g.weight for g in src.data.vertices[i].groups if nm[g.group] in bones and g.weight > 0}
    total = sum(ws.values())
    return {b: w / total for b, w in ws.items()}

def normals(pos, mesh=None):
    n = [Vector() for _ in pos]
    for p in (mesh or me).polygons:
        vs = list(p.vertices)
        a = pos[vs[0]]
        for k in range(1, len(vs) - 1):
            c = (pos[vs[k]] - a).cross(pos[vs[k + 1]] - a)   # area weighted
            for i in (vs[0], vs[k], vs[k + 1]):
                n[i] += c
    return [x.normalized() for x in n]

def make(name, faces, uvs, offset, src=None):
    """The garment from faces of the body (or src, a copy of it cut by body_copy), pushed out offset along the normals
    in the rest shape and in each key."""
    src = src or body
    sme = src.data
    order = sorted({i for f in faces for i in f})
    index = {i: k for k, i in enumerate(order)}
    def build(pos):
        n = normals(pos, sme)
        return [pos[i] + n[i] * offset for i in order]
    old = bpy.data.objects.get(name)
    if old is not None:
        bpy.data.objects.remove(old)
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([tuple(v) for v in build([v.co.copy() for v in sme.vertices])], [], [[index[i] for i in f] for f in faces])
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
        for b, w in weights_of(i, src).items():
            if w > 1e-4:
                (g.vertex_groups.get(b) or g.vertex_groups.new(name=b)).add([index[i]], w, 'REPLACE')
    if sme.shape_keys:
        g.shape_key_add(name='Basis', from_mix=False)
        for kb in sme.shape_keys.key_blocks[1:]:
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

def body_copy(name, keep):
    """A copy of the Body, shape keys, weights and texture places and all, cut down to the faces keep (indices), as a
    bmesh to cut further. finish_cut turns it into the garment."""
    c = body.copy()
    c.data = body.data.copy()
    c.name = name + '_cut'
    body.users_collection[0].objects.link(c)
    bm = bmesh.new()
    bm.from_mesh(c.data)
    bm.faces.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index not in keep], context='FACES')
    return c, bm

def finish_cut(name, c, bm):
    bm.to_mesh(c.data)
    bm.free()
    c.data.update()
    polys = list(c.data.polygons)
    uv = c.data.uv_layers.active
    uvs = [uv.uv[k].vector.copy() for p in polys for k in p.loop_indices] if uv else None
    g = make(name, [list(p.vertices) for p in polys], uvs, OWN_OFFSET.get(name, LAYER_OFFSET[GARMENTS[name][0]]), src=c)
    mesh = c.data
    bpy.data.objects.remove(c)
    bpy.data.meshes.remove(mesh)
    export(g)

M = body.matrix_world
Mi = M.inverted()
R = rig.matrix_world

def bisect(bm, co, no, dist=ON_CUT):
    """Cuts every face of bm on the plane through co with normal no (both in world space)."""
    co_l = Mi @ co
    no_l = (M.to_3x3().transposed() @ no).normalized()
    bmesh.ops.bisect_plane(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces), dist=dist,
                           plane_co=co_l, plane_no=no_l)

def keep_faces(bm, wanted):
    """Deletes every face whose middle (world space) wanted turns down."""
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if not wanted(M @ f.calc_center_median())], context='FACES')

def side(p):
    return 'L' if p.x > 0 else 'R'

def bone_line(name):
    b = rig.data.bones[name]
    return R @ b.head_local, R @ b.tail_local

# The body at rest as triangles, to lay a moved vertex back on the skin with the skin's weights and keys there.
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]
skin_bvh = BVHTree.FromPolygons([v.co.copy() for v in me.vertices], tris)
key_blocks = list(me.shape_keys.key_blocks) if me.shape_keys else []

def onto_skin(bm, v, world):
    """Puts v on the body's skin nearest world, its keys and weights the skin's there."""
    at, _, k, _ = skin_bvh.find_nearest(Mi @ world)
    a, b, c = tris[k]
    w = interpolate.poly_3d_calc([me.vertices[i].co for i in (a, b, c)], at)
    v.co = at
    for layer in bm.verts.layers.shape.values():
        kb = me.shape_keys.key_blocks[layer.name]
        v[layer] = sum((kb.data[i].co * x for i, x in zip((a, b, c), w)), Vector())
    deform = bm.verts.layers.deform.active
    if deform is not None:
        mixed = {}
        for i, x in zip((a, b, c), w):
            for g in me.vertices[i].groups:
                mixed[g.group] = mixed.get(g.group, 0.0) + g.weight * x
        dv = v[deform]
        dv.clear()
        for gi, x in mixed.items():
            if x > 1e-4:
                dv[gi] = x

def shoe_faces():
    return {p.index for p in below('-Pants')}

def make_socks():
    c, bm = body_copy('Socks', shoe_faces())
    finish_cut('Socks', c, bm)

def trousers_bvh():
    """The trousers at rest in world space (Models/garments/Pants.glb, which the export puts on the rig), or None."""
    path = os.path.join(os.path.dirname(bpy.data.filepath), 'garments', 'Pants.glb')
    if not os.path.exists(path):
        return None
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    new = [o for o in bpy.data.objects if o not in before]
    mesh = next((o for o in new if o.type == 'MESH'), None)
    bvh = None
    if mesh is not None:
        W = mesh.matrix_world
        bvh = BVHTree.FromPolygons([W @ v.co for v in mesh.data.vertices], [list(p.vertices) for p in mesh.data.polygons])
    for o in new:
        bpy.data.objects.remove(o)
    return bvh

def make_boots():
    """The shoes with their top ring extruded BOOT_UP up each shin, laid back on the skin there."""
    c, bm = body_copy('Boots', shoe_faces())
    ring = [e for e in bm.edges if e.is_boundary]
    made = bmesh.ops.extrude_edge_only(bm, edges=ring)
    pants = trousers_bvh()
    for v in [x for x in made['geom'] if isinstance(x, bmesh.types.BMVert)]:
        p = M @ v.co
        head, tail = bone_line('shin.' + side(p))
        onto_skin(bm, v, p + (head - tail).normalized() * BOOT_UP)
        # Out past the trousers there (they flare well clear of the skin at the cuff), BOOT_GAP clear, less the push-out
        # make() gives every vertex.
        at = M @ v.co
        out = (at - head) - (tail - head).normalized() * (at - head).dot((tail - head).normalized())
        out.normalize()
        hit = pants.ray_cast(at, out, 2.0) if pants else (None,)
        extra = (hit[3] + BOOT_GAP - OWN_OFFSET['Boots']) if hit[0] is not None else 0.0
        if extra > 0:
            move = M.to_3x3().inverted() @ (out * extra)
            v.co += move
            for layer in bm.verts.layers.shape.values():
                v[layer] = v[layer] + move
    # The new faces wound as the shoe's: each faces out from its shin's line.
    flip = []
    for f in [x for x in made['geom'] if isinstance(x, bmesh.types.BMFace)]:
        p = M @ f.calc_center_median()
        head, tail = bone_line('shin.' + side(p))
        way = (tail - head).normalized()
        out = (p - head) - way * (p - head).dot(way)
        f.normal_update()
        if (M.to_3x3() @ f.normal).dot(out) < 0:
            flip.append(f)
    if flip:
        bmesh.ops.reverse_faces(bm, faces=flip)
    finish_cut('Boots', c, bm)

def make_sandals():
    """The shoes cut down to a sole rim, straps across the foot, one round the ankle and a strip up the back of the heel."""
    c, bm = body_copy('Sandals', shoe_faces())
    ground = min((M @ v.co).z for v in bm.verts)
    up = Vector((0, 0, 1))
    for z in (ground + SANDAL_SOLE,) + SANDAL_ANKLE:
        bisect(bm, Vector((0, 0, z)), up, STRAP_CUT)
    feet = {}
    for s in 'LR':
        head, _ = bone_line('foot.' + s)
        _, tip = bone_line('toe.' + s)
        way = tip - head
        way.z = 0
        way.normalize()
        along = [(M @ v.co).dot(way) for v in bm.verts if side(M @ v.co) == s]
        heel, toe = min(along), max(along)
        feet[s] = (way, heel, toe - heel)
        for t in [x for band in SANDAL_STRAPS + (SANDAL_HEEL,) for x in band if 0 < x < 1]:
            bisect(bm, way * (heel + t * (toe - heel)), way, STRAP_CUT)
    def wanted(p):
        way, heel, long = feet[side(p)]
        t = (p.dot(way) - heel) / long
        return (p.z < ground + SANDAL_SOLE or SANDAL_ANKLE[0] < p.z < SANDAL_ANKLE[1]
                or any(a < t < b for a, b in SANDAL_STRAPS)
                or (SANDAL_HEEL[0] <= t < SANDAL_HEEL[1] and p.z < SANDAL_ANKLE[1]))
    keep_faces(bm, wanted)
    finish_cut('Sandals', c, bm)

def make_gloves():
    """The hands' faces and the forearms' up to a plane through the next ring's well-placed vertices, the long faces
    reaching further up the arm cut on it."""
    def share(i, prefixes):
        return sum(x for b, x in weights_of(i).items() if b.startswith(prefixes))
    hand = {v.index for v in me.vertices if share(v.index, ('hand.', 'thumb.')) >= 0.5}
    arm = {v.index for v in me.vertices if share(v.index, ('hand.', 'thumb.', 'forearm.')) >= 0.5}
    planes = {}
    for s in 'LR':
        elbow, wrist = bone_line('forearm.' + s)
        way = (wrist - elbow)
        long = way.length
        way.normalize()
        ring = set()
        for e in me.edges:
            a, b = e.vertices
            if (a in hand) != (b in hand):
                o = b if a in hand else a
                if side(M @ me.vertices[o].co) == s:
                    ring.add(o)
        near = [M @ me.vertices[o].co for o in ring if (M @ me.vertices[o].co - elbow).dot(way) / long >= 1 - GLOVE_NEAR]
        middle = sum(near, Vector()) / len(near)
        pts = numpy.array([tuple(p - middle) for p in near])
        normal = Vector(numpy.linalg.svd(pts)[2][2]) if len(near) >= 3 else way
        if normal.dot(way) < 0:
            normal = -normal
        if normal.angle(way) > math.radians(35):   # too few or too flat to say: square across the forearm
            normal = way
        planes[s] = (middle, normal)
        print('GLOVE %s: cut %.2f of the forearm from the elbow through %d vertices, %.0f degrees off square'
              % (s, (middle - elbow).dot(way) / long, len(near), math.degrees(normal.angle(way))))
    keep = {p.index for p in me.polygons if all(i in arm for i in p.vertices)}
    c, bm = body_copy('Gloves', keep)
    for s in 'LR':
        bisect(bm, *planes[s])
    keep_faces(bm, lambda p: (p - planes[side(p)][0]).dot(planes[side(p)][1]) > 0)
    finish_cut('Gloves', c, bm)

for name, made in (('Socks', make_socks), ('Boots', make_boots), ('Sandals', make_sandals), ('Gloves', make_gloves)):
    if only and name not in only:
        continue
    have = bpy.data.objects.get(name)
    if have is not None and not have.get('from_group'):
        print('GROUP %s: has a mesh of its own, left alone' % name)
        continue
    made()

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
