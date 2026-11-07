"""Makes a garment looser: pushes it out off its own surface without the armpits folding through themselves.

    blender -b --python anim_scripts/inflate_garment.py -- <in.glb> <out.glb> <offset> [steps] [smooth]

Blender's Shrink/Fatten (Alt+S) moves every vertex out along its normal at once, and in a tight inside corner (the
armpit, where the sleeve's underside faces the torso's side closer than twice the offset) the two sides pass through
each other and leave a flap. This goes out in steps instead, smoothing the cloth after each, so the corner rounds off
as it grows; the cloth there ends up nearer the old surface than the offset, as real cloth would. The openings' edges
(the hem, the neck, the sleeves' ends) move out but aren't smoothed, so they keep their line. The copies of a vertex
split along a UV seam move as one, so the seams stay shut. The UVs are kept as they are, though the smoothing slides
the triangles a little over the surface, so a texture may shift slightly.

offset: how far out, in the file's own units (the glb has no rig to measure leg lengths by). steps: how many pieces
it is pushed out in (default STEPS). smooth: how far each smoothing pass pulls a vertex to the middle of its neighbours, 0 to 1 (default SMOOTH). The first
mesh in the file is used; anything else in it (a camera) is left out of the output."""
import bpy, bmesh, sys
from mathutils import Vector
from mathutils.kdtree import KDTree

# More steps keep each push smaller than the corner's curve, but smooth the whole garment that many more times.
STEPS = 10
SMOOTH = 0.5

argv = sys.argv[sys.argv.index('--') + 1:]
in_glb, out_glb, offset = argv[0], argv[1], float(argv[2])
steps = int(argv[3]) if len(argv) > 3 else STEPS
smooth = float(argv[4]) if len(argv) > 4 else SMOOTH

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=in_glb)
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
if not meshes:
    sys.exit('no mesh in %s' % in_glb)
g = meshes[0]
bm = bmesh.new()
bm.from_mesh(g.data)
bm.transform(g.matrix_world)
bm.verts.ensure_lookup_table()

# The copies of each vertex split along a UV seam, found by place.
kd = KDTree(len(bm.verts))
for v in bm.verts:
    kd.insert(v.co, v.index)
kd.balance()
twins = {}
for v in bm.verts:
    twins.setdefault(min(j for _, j, _ in kd.find_range(v.co, 1e-5)), []).append(v)
twins = list(twins.values())

def place(e):
    return frozenset(tuple(round(c, 5) for c in v.co) for v in e.verts)

# An opening's edge is a boundary edge with no copy of itself; a seam's edges come in pairs.
copies = {}
for e in bm.edges:
    if e.is_boundary:
        copies[place(e)] = copies.get(place(e), 0) + 1
rim = {v for e in bm.edges if e.is_boundary and copies[place(e)] == 1 for v in e.verts}

# Each split vertex sees only its own side's edges, so it is smoothed toward the neighbours of all its copies.
neighbours = [list({e.other_vert(v) for v in vs for e in v.link_edges}) for vs in twins]

for _ in range(steps):
    bm.normal_update()
    for vs in twins:
        n = sum((v.normal for v in vs), Vector()).normalized()
        for v in vs:
            v.co += n * (offset / steps)
    moved = [vs[0].co.copy() if vs[0] in rim else vs[0].co.lerp(sum((v.co for v in nb), Vector()) / len(nb), smooth)
             for vs, nb in zip(twins, neighbours)]
    for vs, co in zip(twins, moved):
        for v in vs:
            v.co = co.copy()

bm.transform(g.matrix_world.inverted())
bm.to_mesh(g.data)
bm.free()
g.data.update()
bpy.ops.object.select_all(action='DESELECT')
g.select_set(True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True)
print('INFLATE %s: %d vertices pushed out %.4f in %d steps, smoothed %.2f each, %d on the openings\' edges kept unsmoothed'
      % (g.name, len(g.data.vertices), offset, steps, smooth, sum(len(vs) for vs in twins if vs[0] in rim)))
