"""Opens a garment's legs and arms for the shrinkwrap app, and brings the wrap back to the rig's rest pose, so the wrap
doesn't fuse the legs together or the arms to the sides.

pose: the garment gets the body's weights (the point on the skin nearest each vertex, its weights blended across that
triangle, as the exporter does), and each thigh is swung out `ANGLE` degrees and each upper arm up `ARM_ANGLE` degrees
about the forward axis at its head, the bones below it with it, by linear blend skinning as Blender's Armature modifier
does. The posed garment is written
as a .glb for the app, and the pose (each moved bone's matrix, armature space) to <out>.pose.json beside it.

unpose: the app's .obj is centred and scaled to about +-1, so it is fitted back onto the posed garment's bounding box
(longest side, as shrinkwrap_compare.py does). Each wrap vertex takes the weights of the nearest point on the posed
garment, and is moved by the inverse of its own blended bone matrix, which undoes the pose exactly where the wrap lies
on the garment. --keep +x or -x makes it symmetric: the wrap is cut down the middle while the limbs are open, so only
what really joins there is cut (at rest the inner thighs reach the middle, and Blender's Symmetrize welds them), the
kept half is brought to rest, and mirrored with its weights, joined only along that cut. Written as a .glb at rest, skinned to the rig, as import_garments takes it; --blend also saves the
.blend with it on the rig (never over the .blend it opened).

blender -b Models/pawnHumanRig.blend --python anim_scripts/open_limbs.py -- pose <garment> <out.glb> [angle [arm angle]]
blender -b Models/pawnHumanRig.blend --python anim_scripts/open_limbs.py -- unpose <garment> <out.pose.json> <wrap.obj> <out.glb> [--name Name] [--blend out.blend] [--keep +x|-x]
"""
import bmesh, bpy, json, math, os, re, sys
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

ANGLE = 15.0       # degrees each thigh is swung out from rest
ARM_ANGLE = 20.0   # degrees each upper arm is swung up from rest
LEGS = ('thigh.L', 'thigh.R')
ARMS = ('upper_arm.L', 'upper_arm.R')
MIDDLE_GAP = 0.02   # Blender units a mirrored half stays off the middle where it isn't joined, so the legs touch but don't cross

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
flags = {}
for flag in ('--blend', '--name', '--keep'):
    if flag in argv:
        k = argv.index(flag)
        flags[flag] = argv[k + 1]
        del argv[k:k + 2]
blend, name, keep = flags.get('--blend'), flags.get('--name'), flags.get('--keep')
if keep not in (None, '+x', '-x'):
    sys.exit('open_limbs.py: --keep takes +x or -x')
if len(argv) < 3 or argv[0] not in ('pose', 'unpose'):
    sys.exit(__doc__)
mode, garment_name = argv[0], argv[1]

for o in bpy.data.objects:   # a mesh saved in Edit Mode has its edits outside its data
    if o.type == 'MESH' and o.mode != 'OBJECT':
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode='OBJECT')
garment = bpy.data.objects[garment_name]
body_parts = [bpy.data.objects[n] for n in ('Pawn', 'Body', 'Head') if n in bpy.data.objects]
rig = body_parts[0].find_armature()
deform = [b.name for b in rig.data.bones if b.use_deform]
to_rig = rig.matrix_world.inverted()

def surface(o, points, weights):
    """A BVH of a mesh's triangles in armature space, with each triangle's corner points and weight dicts."""
    tris = []
    for p in o.data.loop_triangles:
        tris.append(tuple(p.vertices))
    return BVHTree.FromPolygons(points, tris), tris, points, weights

def body_weights(o):
    names = {g.index: g.name for g in o.vertex_groups}
    out = []
    for v in o.data.vertices:
        ws = {names[g.group]: g.weight for g in v.groups if names[g.group] in deform and g.weight > 0}
        t = sum(ws.values())
        out.append({b: w / t for b, w in ws.items()} if t else {})
    return out

def blended(at, surfaces):
    """The weights at the nearest point on any of the surfaces, blended across its triangle by barycentric place."""
    best = None
    for tree, tris, pts, ws in surfaces:
        hit = tree.find_nearest(at)
        if hit[0] is not None and (best is None or hit[3] < best[0]):
            best = (hit[3], hit[0], tris[hit[2]], pts, ws)
    _, p, tri, pts, ws = best
    a, b, c = (pts[i] for i in tri)
    v0, v1, v2 = b - a, c - a, p - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-12
    bv = (d11 * d20 - d01 * d21) / den
    bw = (d00 * d21 - d01 * d20) / den
    out = {}
    for i, f in zip(tri, (1 - bv - bw, bv, bw)):
        for bone, w in ws[i].items():
            out[bone] = out.get(bone, 0) + w * f
    t = sum(out.values())
    return {b: w / t for b, w in out.items() if w > 1e-4} if t else {}

def deselect():
    for o in bpy.context.view_layer.objects:
        o.select_set(False)

def limb_pose(angle, arm_angle):
    """Each moved bone's matrix in armature space: a limb's top bone and everything below it turned about the forward
    axis at its head, the way that takes a knee outward or an elbow upward."""
    mats = {}
    for roots, deg, opened in ((LEGS, angle, lambda was, now: abs(now.x) > abs(was.x)),
                               (ARMS, arm_angle, lambda was, now: now.z > was.z)):
        for root in roots:
            bone = rig.data.bones[root]
            head = bone.head_local
            for sign in (1, -1):
                turn = Matrix.Translation(head) @ Matrix.Rotation(math.radians(sign * deg), 4, 'Y') @ Matrix.Translation(-head)
                if opened(bone.tail_local, turn @ bone.tail_local):
                    break
            for b in [bone] + list(bone.children_recursive):
                mats[b.name] = turn
    return mats

def slope(bone, mat=Matrix.Identity(4)):
    """How far a bone points above level, degrees."""
    way = mat @ bone.tail_local - mat @ bone.head_local
    return math.degrees(math.asin(way.z / way.length))

def skin(p, ws, mats):
    m = Matrix(((0,) * 4,) * 4)
    for bone, w in ws.items():
        m += mats.get(bone, Matrix.Identity(4)) * w
    if not ws:
        m = Matrix.Identity(4)
    return m

# The body at rest, in armature space, and the garment's weights from it.
surfs = []
for o in body_parts:
    pts = [to_rig @ o.matrix_world @ v.co for v in o.data.vertices]
    o.data.calc_loop_triangles()
    surfs.append(surface(o, pts, body_weights(o)))
garment.data.calc_loop_triangles()
rest = [to_rig @ garment.matrix_world @ v.co for v in garment.data.vertices]
gws = [blended(p, surfs) for p in rest]

if mode == 'pose':
    out = os.path.abspath(argv[2])
    angle = float(argv[3]) if len(argv) > 3 else ANGLE
    arm_angle = float(argv[4]) if len(argv) > 4 else ARM_ANGLE
    mats = limb_pose(angle, arm_angle)
    posed = [skin(p, w, mats) @ p for p, w in zip(rest, gws)]
    from_rig = garment.matrix_world.inverted() @ rig.matrix_world
    me = garment.data.copy()
    for v, p in zip(me.vertices, posed):
        v.co = from_rig @ p
    o = bpy.data.objects.new(garment.name + '_open', me)
    o.matrix_world = garment.matrix_world
    bpy.context.scene.collection.objects.link(o)
    deselect()
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_skins=False,
                              export_animations=False, export_apply=False, export_materials='NONE')
    pose_file = os.path.splitext(out)[0] + '.pose.json'
    with open(pose_file, 'w') as f:
        json.dump({'garment': garment.name, 'angle': angle, 'arm_angle': arm_angle,
                   'bones': {b: [list(r) for r in m] for b, m in mats.items()}}, f, indent=1)
    # How far apart the legs now are: the narrowest gap across the middle at knee height and below.
    knee = rig.data.bones['thigh.L'].tail_local.z
    left = [p.x for p in posed if p.z < knee and p.x > 0]
    right = [p.x for p in posed if p.z < knee and p.x < 0]
    print('POSE %s legs out %.1f degrees, %d vertices; below the knees the legs reach %.3f and %.3f from the middle'
          % (garment.name, angle, len(posed), min(left, default=0), max(right, default=0)))
    for arm in ARMS:
        bone = rig.data.bones[arm]
        print('POSE %s %.1f degrees from level at rest, %.1f opened' % (arm, slope(bone), slope(bone, mats[arm])))
    print('GLB %s\nPOSE %s' % (out, pose_file))
    sys.exit(0)

# unpose
pose_file, wrap_file, out = (os.path.abspath(a) for a in argv[2:5])
with open(pose_file) as f:
    saved = json.load(f)
mats = {b: Matrix(m) for b, m in saved['bones'].items()}
posed = [skin(p, w, mats) @ p for p, w in zip(rest, gws)]
posed_surf = [surface(garment, posed, gws)]

deselect()
bpy.ops.wm.obj_import(filepath=wrap_file)
wrap = [o for o in bpy.context.selected_objects if o.type == 'MESH']
if len(wrap) > 1:
    with bpy.context.temp_override(active_object=wrap[0], selected_editable_objects=wrap, selected_objects=wrap):
        bpy.ops.object.join()
wrap = wrap[0]
pts = [to_rig @ wrap.matrix_world @ v.co for v in wrap.data.vertices]

def box(ps):
    return (Vector([min(p[i] for p in ps) for i in range(3)]), Vector([max(p[i] for p in ps) for i in range(3)]))
lo, hi = box(posed)
l2, h2 = box(pts)
k = max(hi - lo) / max(h2 - l2)
pts = [(p - (l2 + h2) / 2) * k + (lo + hi) / 2 for p in pts]
tree = posed_surf[0][0]
miss = sorted(tree.find_nearest(p)[3] for p in pts)
print('FIT scale %.4f; wrap to posed garment: median %.4f, 95%% %.4f, most %.4f (Blender units)'
      % (k, miss[len(miss) // 2], miss[int(len(miss) * 0.95)], miss[-1]))

def mirrored_weights(ws):
    swap = {'L': 'R', 'R': 'L'}
    return {re.sub(r'\.([LR])(?=\.|$)', lambda m: '.' + swap[m.group(1)], b): w for b, w in ws.items()}

def cut_half(side):
    """The wrap's kept half, cut along the middle where it lies now, in armature space: its points, its faces and the
    indices of the points on the cut. Any loose bit the cut leaves is dropped, as the film was one closed surface."""
    bm = bmesh.new()
    bm.from_mesh(wrap.data)
    bm.verts.ensure_lookup_table()
    for v, p in zip(bm.verts, pts):
        v.co = p
    cut = bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-5, plane_co=(0, 0, 0),
                                 plane_no=(1, 0, 0), clear_inner=side > 0, clear_outer=side < 0)
    seam = {v for v in cut['geom_cut'] if isinstance(v, bmesh.types.BMVert)}
    seen, parts = set(), []
    for f in bm.faces:
        if f in seen:
            continue
        seen.add(f)
        stack, part = [f], []
        while stack:
            x = stack.pop()
            part.append(x)
            for e in x.edges:
                for g in e.link_faces:
                    if g not in seen:
                        seen.add(g)
                        stack.append(g)
        parts.append(part)
    parts.sort(key=len, reverse=True)
    print('HALF %d faces kept, %d loose pieces dropped (%d faces)' % (len(parts[0]), len(parts) - 1, sum(map(len, parts[1:]))))
    order, faces = {}, []
    for f in parts[0]:
        faces.append([order.setdefault(v, len(order)) for v in f.verts])
    points = [v.co.copy() for v in order]
    return points, faces, {order[v] for v in seam if v in order}

def symmetric(side):
    """The kept half at rest, mirrored. Weights on the cut are made even across the middle, which keeps those points on
    it as they go back to rest. Returns the mesh's points, its weights and its faces."""
    half, faces, seam = cut_half(side)
    ws = [blended(p, posed_surf) for p in half]
    for i in seam:
        both = mirrored_weights(ws[i])
        ws[i] = {b: (ws[i].get(b, 0) + both.get(b, 0)) / 2 for b in set(ws[i]) | set(both)}
    rest_half = [skin(p, w, mats).inverted() @ p for p, w in zip(half, ws)]
    pushed, most = 0, 0.0
    for i, p in enumerate(rest_half):
        if i in seam:
            p.x = 0.0
        elif p.x * side < MIDDLE_GAP:
            most = max(most, MIDDLE_GAP - p.x * side)
            p.x = side * MIDDLE_GAP
            pushed += 1
    print('HALF %d points, %d on the cut; %d kept off the middle at rest, the furthest moved %.3f' % (len(half), len(seam), pushed, most))
    index, points, weights = {}, list(rest_half), list(ws)
    for i, p in enumerate(rest_half):
        if i in seam:
            index[i] = i
        else:
            index[i] = len(points)
            points.append(Vector((-p.x, p.y, p.z)))
            weights.append(mirrored_weights(ws[i]))
    return points, weights, faces + [[index[i] for i in reversed(f)] for f in faces]

if keep:
    unposed, wws, faces = symmetric(1 if keep == '+x' else -1)
    me = bpy.data.meshes.new(wrap.data.name)
    me.from_pydata([tuple(p) for p in unposed], [], faces)
    me.update()
    wrap.data = me
    bm = bmesh.new()
    bm.from_mesh(me)
    print('SYMMETRIC %d vertices, %d faces, %d open or non-manifold edges' % (len(me.vertices), len(me.polygons), sum(1 for e in bm.edges if not e.is_manifold)))
else:
    wws = [blended(p, posed_surf) for p in pts]
    unposed = [skin(p, w, mats).inverted() @ p for p, w in zip(pts, wws)]

wrap.name = wrap.data.name = name or os.path.splitext(os.path.basename(out))[0].capitalize()
wrap.parent = None
wrap.matrix_world = rig.matrix_world
for v, p in zip(wrap.data.vertices, unposed):
    v.co = p
groups = {b: wrap.vertex_groups.new(name=b) for b in deform}
for i, ws in enumerate(wws):
    for b, w in ws.items():
        groups[b].add([i], w, 'REPLACE')
wrap.parent = rig
mod = wrap.modifiers.new('Armature', 'ARMATURE')
mod.object = rig
print('UNPOSE %s: %d vertices back at rest' % (wrap.name, len(unposed)))

deselect()
wrap.select_set(True)
rig.select_set(True)
bpy.context.view_layer.objects.active = wrap
bpy.ops.export_scene.gltf(filepath=out, use_selection=True, export_format='GLB', export_extras=True,
                          export_skins=True, export_animations=False, export_apply=False, export_materials='NONE')
print('GLB %s' % out)
if blend:
    if os.path.abspath(blend) == os.path.abspath(bpy.data.filepath):
        sys.exit('open_legs.py: not saving over the .blend it opened')
    bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(blend), copy=True)
    print('BLEND %s' % blend)
