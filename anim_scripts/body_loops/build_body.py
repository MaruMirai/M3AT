"""Blender: puts the generated body into a copy of the rig file as `Body`, with the current Body's bone weights, shape
keys and other vertex groups carried over by surface interpolation. The old body is kept as `Body_old` and a quad copy
of the new one as `Body_quads` (neither has an Armature modifier, so the exporter never sees them).
  blender -b Models/pawnHumanRig.blend --python build_body.py -- newbody.json out.blend"""
import bpy, bmesh, json, sys
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform

argv = sys.argv[sys.argv.index('--') + 1:]
data = json.load(open(argv[0])); out = argv[1]
old = bpy.data.objects['Body']; rig = old.parent
mw = old.matrix_world.copy(); inv = mw.inverted()

me_old = old.data
ov = [v.co.copy() for v in me_old.vertices]                       # local, base shape
me_old.calc_loop_triangles()
otris = [tuple(t.vertices) for t in me_old.loop_triangles]
bvh = BVHTree.FromPolygons(ov, otris)
bones = {b.name for b in rig.data.bones if b.use_deform}
gnames = [g.name for g in old.vertex_groups]
ow = [{g.group: g.weight for g in v.groups} for v in me_old.vertices]

me = bpy.data.meshes.new('Body')
verts = [inv @ Vector(p) for p in data['V']]
me.from_pydata(verts, [], data['F'])
me.update()
new = bpy.data.objects.new('Body_new', me)
for c in old.users_collection: c.objects.link(new)
new.parent = old.parent; new.parent_type = old.parent_type
new.matrix_parent_inverse = old.matrix_parent_inverse.copy()
new.location, new.rotation_euler, new.scale = old.location.copy(), old.rotation_euler.copy(), old.scale.copy()
for p in me.polygons: p.use_smooth = True

# Where each new vertex lies on the old surface: its triangle and barycentric weights.
where = []
far = 0.0
for v in me.vertices:
    co, n, ti, d = bvh.find_nearest(v.co)
    far = max(far, d)
    a, b, c = otris[ti]
    # barycentric of co in triangle (a, b, c)
    pa, pb, pc = ov[a], ov[b], ov[c]
    v0, v1, v2 = pb - pa, pc - pa, co - pa
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01
    wb = (d11 * d20 - d01 * d21) / den if den else 0.0
    wc = (d00 * d21 - d01 * d20) / den if den else 0.0
    wa = 1 - wb - wc
    where.append(((a, wa), (b, wb), (c, wc)))
print('BUILD furthest new vertex from the old surface: %.3f' % far)

# Vertex groups: bone weights interpolated and normalised (tiny ones dropped); other groups (garment hides,
# selections) kept where the interpolated membership is at least half.
for gi, name in enumerate(gnames):
    new.vertex_groups.new(name=name)
bone_g = [gi for gi, n in enumerate(gnames) if n in bones]
for vi, w3 in enumerate(where):
    acc = {}
    for oi, b in w3:
        for gi, wt in ow[oi].items():
            acc[gi] = acc.get(gi, 0.0) + wt * b
    bw = {gi: w for gi, w in acc.items() if gi in bone_g and w > 0.01}
    tot = sum(bw.values())
    for gi, w in bw.items():
        new.vertex_groups[gi].add([vi], w / tot, 'REPLACE')
    for gi, w in acc.items():
        if gi not in bone_g and w >= 0.5:
            new.vertex_groups[gi].add([vi], 1.0, 'REPLACE')

# Shape keys: each key's move interpolated from the old triangle.
if me_old.shape_keys:
    kb = me_old.shape_keys.key_blocks
    basis = new.shape_key_add(name=kb[0].name, from_mix=False)
    for k in kb[1:]:
        nk = new.shape_key_add(name=k.name, from_mix=False)
        nk.slider_min, nk.slider_max, nk.value = k.slider_min, k.slider_max, 0.0
        nk.interpolation = k.interpolation
        if k.vertex_group: nk.vertex_group = k.vertex_group
        rel = k.relative_key
        for vi, w3 in enumerate(where):
            d = Vector()
            for oi, b in w3:
                d += (k.data[oi].co - rel.data[oi].co) * b
            nk.data[vi].co = me.vertices[vi].co + d
    new.data.shape_keys.use_relative = me_old.shape_keys.use_relative

# Armature modifier as the old body's.
om = next(m for m in old.modifiers if m.type == 'ARMATURE')
m = new.modifiers.new('Armature', 'ARMATURE')
m.object = om.object; m.use_vertex_groups = om.use_vertex_groups; m.use_bone_envelopes = om.use_bone_envelopes
m.use_deform_preserve_volume = om.use_deform_preserve_volume

# A UV map (the old body had one), by smart projection.
bpy.context.view_layer.objects.active = new
for o in bpy.context.selected_objects: o.select_set(False)
new.select_set(True)
try:
    bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=1.15, island_margin=0.01)
    bpy.ops.object.mode_set(mode='OBJECT')
except Exception as e:
    print('BUILD no UV map:', e)
    if new.mode != 'OBJECT': bpy.ops.object.mode_set(mode='OBJECT')

# Reference copies with no modifiers, in a collection of their own, hidden.
ref = bpy.data.collections.new('BodyReference'); bpy.context.scene.collection.children.link(ref)
quads = new.copy(); quads.data = new.data.copy(); quads.name = 'Body_quads'
quads.modifiers.clear(); ref.objects.link(quads)
for c in list(quads.users_collection):
    if c is not ref: c.objects.unlink(quads)
old.name = 'Body_old'; old.modifiers.clear()
for c in list(old.users_collection): c.objects.unlink(old)
ref.objects.link(old)
for o in (quads, old): o.hide_set(True); o.hide_render = True
ref.hide_viewport = False

# The body itself triangulated along the chosen diagonals (each quad's first-third corners).
bm = bmesh.new(); bm.from_mesh(new.data)
bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method='FIXED', ngon_method='BEAUTY')
bm.to_mesh(new.data); bm.free()
new.name = 'Body'; new.data.name = 'Body'
print('BUILD Body: %d vertices, %d triangles, %d groups, %d keys' % (len(new.data.vertices), len(new.data.polygons),
      len(new.vertex_groups), len(new.data.shape_keys.key_blocks) if new.data.shape_keys else 0))
bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)
print('BUILD saved', out)
