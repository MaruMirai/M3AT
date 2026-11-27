"""Makes Odyssey's vacsuit from the chibi's own body: the whole body from the neck down, hands and feet too.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_vacsuit.py -- <out.glb> [out.blend]

As make_pants.py makes the trousers: the body (walk.BODY_PARTS, joined) is copied in its rest pose and base shape, cut
by one plane across the neck (the collar the helmet seals onto; the helmet is made apart), put back on the body's
surface OFFSET out, one surface, smoothed between pushes so it stands off over hollows. Gloves and boots are part of
it, laid closer to the skin (HAND_OFFSET, FOOT_OFFSET) so the fingers stay apart. It has a UV map but no image, so the
game draws it in the apparel's colour. The .blend is never saved; the optional out.blend is a separate file with the
suit on the rig. Heights and lengths are in leg lengths of the rig (the thigh's head to the floor)."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

NAME = 'Vacsuit'
# The collar's height over the shoulders' bone at the back, in leg lengths, and how much lower it dips at the front.
COLLAR_BACK = 0.14
COLLAR_DIP = 0.0
# How far the suit stands off the skin, in leg lengths: looser than a shirt, as a pressure suit is padded; the hands and
# feet (by the nearest bone) closer, so gloves don't fuse the fingers and boots stay boots.
OFFSET = 0.022
HAND_OFFSET = 0.007
FOOT_OFFSET = 0.012
# The share of the way along the forearm (0 elbow, 1 wrist) and the shin where the cuffs ease to the glove and boot.
CUFF_FROM = 0.6
# Where the stand-off has gone this share of the way from OFFSET to nothing, the glove or boot begins, and is not smoothed.
GLOVE_SHARE = 0.25
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

# The collar: a plane through the neck, tipped down to the front (the figure faces -y).
neck = joint('shoulder.L').z + COLLAR_BACK * leg
tip = math.atan2(COLLAR_DIP * leg, 0.25 * leg)
cut(Vector((0, 0, neck)), Vector((0, -math.sin(tip), math.cos(tip))))

# The plane leaves the head as a piece of its own (and the Head part was one already): keep the largest piece, the suit.
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
suit = bpy.data.objects.new(NAME, mesh)
bpy.context.collection.objects.link(suit)
for o in bpy.context.view_layer.objects:
    o.select_set(o is suit)
bpy.context.view_layer.objects.active = suit

def apply(kind, **props):
    m = suit.modifiers.new(kind.lower(), kind)
    for k, v in props.items():
        setattr(m, k, v)
    bpy.ops.object.modifier_apply(modifier=m.name)

if SUBDIVIDE:
    apply('SUBSURF', levels=SUBDIVIDE, subdivision_type='SIMPLE')

def along(p, a, b):
    """How far p is along a to b (0 at a, 1 at b, clamped), and its distance from that line."""
    t = max(0.0, min(1.0, (p - a).dot(b - a) / (b - a).length_squared))
    return t, (p - a.lerp(b, t)).length

def smoothstep(x):
    x = max(0.0, min(1.0, x))
    return x * x * (3 - 2 * x)

# Each limb's end as two lines: the forearm or shin, where the suit eases in to the glove or boot, and the hand or foot.
ends = []
for side in ('L', 'R'):
    ends.append((joint('forearm.' + side), joint('forearm.' + side, tail=True),
                 joint('hand.' + side), joint('hand.%s.002' % side, tail=True), HAND_OFFSET))
    ends.append((joint('shin.' + side), joint('shin.' + side, tail=True),
                 joint('foot.' + side), joint('toe.' + side, tail=True), FOOT_OFFSET))

def offset_at(p):
    """The suit's stand-off at p: OFFSET on the trunk and limbs, easing to the glove's or boot's past CUFF_FROM of the
    forearm or shin, by whichever limb end p is nearest."""
    best, off = None, OFFSET
    for a, b, c, d, end in ends:
        t, r = along(p, a, b)
        u, s = along(p, c, d)
        dist, share = (r, smoothstep((t - CUFF_FROM) / (1 - CUFF_FROM))) if r < s else (s, 1.0)
        if best is None or dist < best:
            best, off = dist, OFFSET + (end - OFFSET) * share
    return off * leg

def lay_on(bm, snug):
    """Pushes the cloth out to its stand-off from the nearest point of the skin wherever it is closer, so the inside
    stands clear of it at rest; with snug, puts it there exactly (the collar's edge, so it doesn't gape)."""
    for v in bm.verts:
        hit, n, _, _ = btree.find_nearest(v.co)
        if hit is None:
            continue
        o = offset_at(hit)
        if snug(v) or (v.co - hit).dot(n) < o:
            v.co = hit + n * o

# Smoothing pulls the cloth into the skin, so it is pushed back out after each pass; it is free to stand off it over a
# hollow (the navel, the spine's groove), as cloth would. The collar's edge is left where the plane cut it, and the
# gloves and boots (where the stand-off has gone most of the way to theirs) aren't smoothed at all: smoothing shrinks
# the fingertips and toes faster than the push brings them back, and they came through.
bm = bmesh.new()
bm.from_mesh(suit.data)
lay_on(bm, lambda v: True)
def smoothed(v):
    hit = btree.find_nearest(v.co)[0]
    return not v.is_boundary and (hit is None or offset_at(hit) > (1 - GLOVE_SHARE) * OFFSET * leg)
inside = [v for v in bm.verts if smoothed(v)]
for _ in range(SMOOTH):
    bmesh.ops.smooth_vert(bm, verts=inside, factor=0.5, use_axis_x=True, use_axis_y=True, use_axis_z=True)
    lay_on(bm, lambda v: v.is_boundary)
bm.to_mesh(suit.data)
bm.free()
suit.data.update()

# The body's own groups came along with the copy; the exporter blends the weights from the body under each vertex.
suit.vertex_groups.clear()
if suit.data.shape_keys:
    suit.shape_key_clear()

# Normals out of the body, then the cloth's thickness outward from the skin.
bm = bmesh.new()
bm.from_mesh(suit.data)
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
bm.faces.ensure_lookup_table()
f = max(bm.faces, key=lambda f: f.calc_center_median().z)
hit, n, _, _ = btree.find_nearest(f.calc_center_median())
if f.normal.dot(n) < 0:
    bmesh.ops.reverse_faces(bm, faces=bm.faces)
bm.to_mesh(suit.data)
bm.free()
if THICKNESS > 0:
    apply('SOLIDIFY', thickness=THICKNESS * leg, offset=1.0, use_rim=True, use_quality_normals=True)

# UVs to paint on later: the trousers unwrapped by their seams, the openings' rims and a line down each side.
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.01)
bpy.ops.object.mode_set(mode='OBJECT')
for p in suit.data.polygons:
    p.use_smooth = True

mat = bpy.data.materials.new(NAME)
mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'].default_value = (0.8, 0.8, 0.8, 1.0)
suit.data.materials.append(mat)

print('VACSUIT %s: %d vertices, %d faces, leg %.3f' % (NAME, len(suit.data.vertices), len(suit.data.polygons), leg))

bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False)
if out_blend:
    suit.parent = rig
    suit.matrix_parent_inverse = rig.matrix_world.inverted()
    suit.modifiers.new('Armature', 'ARMATURE').object = rig
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
