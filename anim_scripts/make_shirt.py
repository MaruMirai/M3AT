"""Makes a plain T-shirt from the chibi's own body, as a base to model other shirts from.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_shirt.py -- <out.glb> [out.blend]

The body (walk.BODY_PARTS, joined) is copied in its rest pose and base shape, cut by planes at the neck, the hem and each sleeve's end, put
back on the body's surface OFFSET out, one surface (the game draws both its sides); THICKNESS above 0 gives it that
much outward, with a rim at each opening. It has a UV map but no image, so the game draws it in the apparel's colour.
The .blend is never saved; the optional out.blend is a separate file with the shirt on the rig, to model from. Heights and lengths are in leg
lengths of the rig (the thigh's head to the floor), so they hold if the rig is scaled."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

NAME = 'TShirt'
# The hem's height over the thigh's head, in leg lengths: a little below the waist, over the top of the trousers.
HEM = 0.04
# The neckline: its height at the back over the shoulders' bone, and how much lower it dips at the front.
NECK_BACK = 0.07
NECK_DIP = 0.07
# How far down the upper arm each sleeve reaches, from the shoulder joint (0) to the elbow (1).
SLEEVE = 0.55
# How far from the arm's bones the sleeve's cut reaches, in leg lengths: past the arm's skin, short of the flank.
ARM_REACH = 0.15
# How far the cloth stands off the skin, and how thick it is (0: one surface), in leg lengths.
OFFSET = 0.008
THICKNESS = 0.0
# The cut shell is subdivided this many times and smoothed before it is laid back on the body.
SUBDIVIDE = 1
SMOOTH = 12

argv = sys.argv[sys.argv.index('--') + 1:]
out_glb = argv[0]
out_blend = argv[1] if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

body = walk.join_body()   # as export_frames.py: the body's parts (walk.BODY_PARTS) as one mesh
rig = body.parent
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0

def joint(name, tail=False):
    b = rig.data.bones[name]
    return rig.matrix_world @ (b.tail_local if tail else b.head_local)

floor = 0.0
leg = (joint('thigh.L') - Vector((joint('thigh.L').x, joint('thigh.L').y, floor))).length
up = Vector((0, 0, 1))

# The body at rest in world space, and a tree of it to lay the shell back on.
bw = body.matrix_world
bverts = [bw @ v.co for v in body.data.vertices]
btree = BVHTree.FromPolygons(bverts, [tuple(p.vertices) for p in body.data.polygons])

bm = bmesh.new()
bm.from_mesh(body.data)
bm.transform(bw)
bm.verts.ensure_lookup_table()

def cut(co, no, only=None):
    """Cuts along the plane at co and drops what is on the side no points to: all of it, or with only, just the faces
    whose middle passes that test (so a sleeve's plane leaves the torso alone)."""
    geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
    bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-5, plane_co=co, plane_no=no)
    drop = [f for f in bm.faces if (f.calc_center_median() - co).dot(no) > 0 and (only is None or only(f))]
    bmesh.ops.delete(bm, geom=drop, context='FACES')
    loose = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=loose, context='VERTS')

# The hem, level.
hip = joint('thigh.L')
cut(Vector((0, 0, hip.z + HEM * leg)), -up)
# The neckline: a plane through the middle of the neck, tipped down to the front (the figure faces -y).
neck = joint('shoulder.L').z + NECK_BACK * leg
tip = math.atan2(NECK_DIP * leg, 0.25 * leg)
cut(Vector((0, 0, neck)), Vector((0, -math.sin(tip), math.cos(tip))))
# The sleeves, square across the upper arm, dropping only what is round the arm (in the A-pose the arm's plane also
# passes through the hips).
def near_arm(f, a, b):
    p = f.calc_center_median()
    t = max(0.0, min(1.0, (p - a).dot(b - a) / (b - a).length_squared))
    return (p - a.lerp(b, t)).length < ARM_REACH * leg
for side in ('L', 'R'):
    a, b = joint('upper_arm.' + side), joint('upper_arm.' + side, tail=True)
    hand = joint('hand.%s.002' % side, tail=True)
    cut(a.lerp(b, SLEEVE), (b - a).normalized(), only=lambda f, a=a, hand=hand: near_arm(f, a, hand))

# The planes leave the head, hands and legs as pieces of their own: keep the largest piece only, the shirt.
bm.faces.index_update()
seen, pieces = set(), []
for f in bm.faces:
    if f.index in seen:
        continue
    stack, piece = [f], []
    seen.add(f.index)
    while stack:
        g = stack.pop()
        piece.append(g)
        for e in g.edges:
            for h in e.link_faces:
                if h.index not in seen:
                    seen.add(h.index)
                    stack.append(h)
    pieces.append(piece)
pieces.sort(key=len)
for piece in pieces[:-1]:
    bmesh.ops.delete(bm, geom=piece, context='FACES')

mesh = bpy.data.meshes.new(NAME)
bm.to_mesh(mesh)
bm.free()
shirt = bpy.data.objects.new(NAME, mesh)
bpy.context.collection.objects.link(shirt)
for o in bpy.context.view_layer.objects:
    o.select_set(o is shirt)
bpy.context.view_layer.objects.active = shirt

def apply(kind, **props):
    m = shirt.modifiers.new(kind.lower(), kind)
    for k, v in props.items():
        setattr(m, k, v)
    bpy.ops.object.modifier_apply(modifier=m.name)

if SUBDIVIDE:
    apply('SUBSURF', levels=SUBDIVIDE, subdivision_type='SIMPLE')

def lay_on(bm, snug):
    """Pushes the cloth out to OFFSET from the nearest point of the skin wherever it is closer, so the inside stands
    clear of it at rest; with snug, puts it at OFFSET exactly (the openings' edges, so they don't gape)."""
    for v in bm.verts:
        hit, n, _, _ = btree.find_nearest(v.co)
        if hit is not None and (snug(v) or (v.co - hit).dot(n) < OFFSET * leg):
            v.co = hit + n * OFFSET * leg

# Smoothing pulls the cloth into the skin, so it is pushed back out after each pass; it is free to stand off it over a
# hollow (the navel, the spine's groove), as cloth would. The openings' edges are left where the planes cut them.
bm = bmesh.new()
bm.from_mesh(shirt.data)
lay_on(bm, lambda v: True)
inside = [v for v in bm.verts if not v.is_boundary]
for _ in range(SMOOTH):
    bmesh.ops.smooth_vert(bm, verts=inside, factor=0.5, use_axis_x=True, use_axis_y=True, use_axis_z=True)
    lay_on(bm, lambda v: v.is_boundary)
bm.to_mesh(shirt.data)
bm.free()
shirt.data.update()

# The body's own groups came along with the copy; the exporter blends the weights from the body under each vertex.
shirt.vertex_groups.clear()
if shirt.data.shape_keys:
    shirt.shape_key_clear()

# Normals out of the body, then the cloth's thickness outward from the skin.
bm = bmesh.new()
bm.from_mesh(shirt.data)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
bm.faces.ensure_lookup_table()
f = max(bm.faces, key=lambda f: f.calc_center_median().z)
hit, n, _, _ = btree.find_nearest(f.calc_center_median())
if f.normal.dot(n) < 0:
    bmesh.ops.reverse_faces(bm, faces=bm.faces)
bm.to_mesh(shirt.data)
bm.free()
if THICKNESS > 0:
    apply('SOLIDIFY', thickness=THICKNESS * leg, offset=1.0, use_rim=True, use_quality_normals=True)

# UVs to paint on later: the shirt unwrapped by its seams, which are the openings' rims and a line down each side.
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.01)
bpy.ops.object.mode_set(mode='OBJECT')
for p in shirt.data.polygons:
    p.use_smooth = True

mat = bpy.data.materials.new(NAME)
mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.8, 0.8, 0.8, 1.0)
shirt.data.materials.append(mat)

print('SHIRT %s: %d vertices, %d faces, leg %.3f' % (NAME, len(shirt.data.vertices), len(shirt.data.polygons), leg))

bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False)
if out_blend:
    shirt.parent = rig
    shirt.matrix_parent_inverse = rig.matrix_world.inverted()
    shirt.modifiers.new('Armature', 'ARMATURE').object = rig
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
