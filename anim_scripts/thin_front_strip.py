"""Takes out the narrow strip down the front of a closed skirt made by make_duster.py (the robe, burka and dresses), in the
finished .glb, texture and all. A closed front is cut down the figure's middle, which adds a vertex on the middle of the
body edge joining the two vertices either side of it, so the bottom edge has three columns close together there (about
0.4 Blender units apart where the rest are about 2), and the skirt hangs a column from each. The middle one is the front
vent's, so it stays: the skirt's vertices in the two beside it are welded onto it, ring by ring (to the vent's copy on
their own side below its top), and the faces between collapse. The bottom edge's own vertices stay (the top row of faces
fans from them, as make_duster.py's SKIRT_EVERY does). What is left keeps its UVs, weights, shape keys and normals, and
the welded faces' corners take the middle vertex's UV in their own island and its normal, so the texture is stretched
across the wider faces. Only for a texture with nothing painted in the strip: the robe's Meshy texture has its placket
there, and the royal robe its centre panel, which this cuts up (so they keep theirs). make_duster.py leaves these columns out itself since (VENT_CLEAR), so this is for .glbs made before.

    blender -b --python anim_scripts/thin_front_strip.py -- <in.glb> <out.glb> [near]

near: how far (Blender units at the hem) a column may be from the middle to be welded onto it (0.6)."""
import bpy, bmesh, math, sys
from mathutils import Vector

args = sys.argv[sys.argv.index('--') + 1:]
src, out = args[0], args[1]
NEAR = float(args[2]) if len(args) > 2 else 0.6
MIDDLE = 1e-3   # |x| of the middle column's vertices
ON_COLUMN = 0.003   # radians round the middle a column's vertices keep to

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src, disable_bone_shape=True)
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
assert len(meshes) == 1, 'one mesh expected, found %d' % len(meshes)
obj = meshes[0]
me = obj.data
to_world = obj.matrix_world

# Each vertex's normal as the file has it (the mean of its corners'), kept through the edit as an attribute.
sums = [Vector() for _ in me.vertices]
for li, loop in enumerate(me.loops):
    sums[loop.vertex_index] += Vector(me.corner_normals[li].vector)
keep_n = me.attributes.new('keep_n', 'FLOAT_VECTOR', 'POINT')
for i, s in enumerate(sums):
    keep_n.data[i].vector = s.normalized()

bm = bmesh.new()
bm.from_mesh(me)
bm.verts.ensure_lookup_table()
uv = bm.loops.layers.uv.active
deform = bm.verts.layers.deform.active
shapes = list(bm.verts.layers.shape.values())

# The file splits its vertices along UV seams (a retexture's atlas cuts the skirt up), which would stop the walk up a
# column. Copies at one place with the same weights are joined again (each corner keeps its own UV, so the seams stay
# and the export splits them again); the vent's two copies have different weights (one per leg), so they stay apart.
def twin_key(v):
    ws = tuple(sorted((g, round(w, 4)) for g, w in v[deform].items())) if deform else ()
    return tuple(round(c, 5) for c in v.co) + ws + tuple(tuple(round(c, 5) for c in v[s]) for s in shapes)
first = {}
joins = {}
for v in bm.verts:
    k = twin_key(v)
    if k in first:
        joins[v] = first[k]
    else:
        first[k] = v
bmesh.ops.weld_verts(bm, targetmap=joins)
bm.verts.ensure_lookup_table()
print('JOINED %d seam copies' % len(joins))

world = {v: to_world @ v.co for v in bm.verts}
zmin = min(p.z for p in world.values())
front_middle = [v for v in bm.verts if abs(world[v].x) < MIDDLE and world[v].y < 0]
if not any(world[v].z < zmin + 0.05 for v in front_middle):
    sys.exit("thin_front_strip.py: %s has no column down the front's middle" % src)
middle_y = sum(p.y for p in world.values()) / len(world)

def angle(v):
    p = world[v]
    return math.atan2(p.x, -(p.y - middle_y))

def up_of(v):
    """The next vertex up v's column: a neighbour well above it at nearly the same angle round."""
    best = None
    for e in v.link_edges:
        o = e.other_vert(v)
        dz = world[o].z - world[v].z
        if dz > 0.1 and abs(angle(o) - angle(v)) < ON_COLUMN and (best is None or dz < world[best].z - world[v].z):
            best = o
    return best

def skirt_edge(v):
    """Whether v is the top of its column: the skirt hangs each column straight out from the hips' middle, so its
    vertices are all at one angle round, and the bottom edge's vertex is the last of them (the body's vertices above it
    are not on that line)."""
    return up_of(v) is None

hem = [v for v in bm.verts if world[v].z < zmin + 0.05 and world[v].y < 0 and MIDDLE < abs(world[v].x) < NEAR]
targets = {}
for v in hem:
    column = [v]
    while not skirt_edge(column[-1]):
        column.append(up_of(column[-1]))
    for w in column[:-1]:   # the last is the bottom edge's vertex: it stays
        onto = [e.other_vert(w) for e in w.link_edges if abs(world[e.other_vert(w)].x) < MIDDLE]
        if not onto:
            sys.exit('thin_front_strip.py: a vertex at (%.3f, %.3f, %.3f) has no middle neighbour' % tuple(world[w]))
        targets[w] = min(onto, key=lambda o: abs(world[o].z - world[w].z))
    print('COLUMN x %.3f: %d vertices welded, the top one at z %.2f kept' % (world[v].x, len(column) - 1, world[column[-1]].z))
if not targets:
    sys.exit('thin_front_strip.py: no columns within %.2f of the middle in %s' % (NEAR, src))

# A corner moved onto a middle vertex takes the UV the middle's place has in its own face's island: carried on from the
# face's other corners (the face's UVs as an affine map of its places), then put on the middle vertex's own UV there
# if it has one that near (the same island), so the texture stays whole across it.
from mathutils.geometry import barycentric_transform
def carried(face, moved, onto):
    others = [l for l in face.loops if l.vert is not moved]
    mine = next(l for l in face.loops if l.vert is moved)
    a, b = others[0], others[1]
    # the map from the triangle (moved, a, b)'s places to its UVs, at the middle vertex's place
    u3 = lambda l: Vector((l[uv].uv.x, l[uv].uv.y, 0.0))
    p = barycentric_transform(onto.co, mine.vert.co, a.vert.co, b.vert.co, u3(mine), u3(a), u3(b))
    return Vector((p.x, p.y))
fixes = []
for moved, onto in targets.items():
    own = [Vector(l[uv].uv) for l in onto.link_loops]
    for f in moved.link_faces:
        if onto in f.verts:
            continue   # collapses
        guess = carried(f, moved, onto)
        near = min(own, key=lambda u: (u - guess).length) if own else None
        put = near if near is not None and (near - guess).length < 0.02 else guess
        fixes.append((f, onto, put, near is not None and put is near))
print('UVS %d corners moved, %d onto the middle vertex\'s own UV' % (len(fixes), sum(f[3] for f in fixes)))
before = len(bm.faces)
fixes = [(set(f.verts), onto, put) for f, onto, put, _ in fixes]
bmesh.ops.weld_verts(bm, targetmap=targets)
for f in bm.faces:
    vs = set(f.verts)
    for others, onto, put in fixes:
        if onto in vs and vs - {onto} <= others and len(vs) == len(others):
            for l in f.loops:
                if l.vert is onto:
                    l[uv].uv = put
bm.to_mesh(me)
bm.free()
me.update()
n = me.attributes['keep_n']
me.normals_split_custom_set_from_vertices([Vector(d.vector) for d in n.data])
me.attributes.remove(me.attributes['keep_n'])
print('WELDED %d vertices, %d faces gone; %d vertices, %d faces left' % (len(targets), before - len(me.polygons),
                                                                       len(me.vertices), len(me.polygons)))

bpy.ops.object.select_all(action='SELECT')
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', export_morph=True, export_morph_normal=False,
                          export_skins=True, export_extras=True, export_animations=False, export_image_format='AUTO')
print('GLB %s' % out)
