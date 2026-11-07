"""Makes the formal shirt (Royalty's Apparel_ShirtRuffle) from the collared shirt: Models/garments/CollarShirt.glb as it
is, its texture too, with a jabot of frills cascading down the front from under the collar and a frill out of each cuff.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_formal_shirt.py -- <out.glb> [out.blend]

Written to Models/garments/ShirtRuffle.glb it is the garment ShirtRuffle, drawn for Apparel_ShirtRuffle (export_frames.py's
default_defs). Re-run it after the collared shirt changes (a new make_from_groups.py or place_textured.py run).

The jabot is TIERS frills, one under the other, each hung from a line across the shirt's front: gathered narrow at the
top (TOP_HALF across) and spread at the bottom (BOTTOM_HALF), standing further off the shirt toward its bottom (FLARE) and
folded there (FOLDS folds of FOLD deep), so the tier above stands out over the top of the next. Each cuff's frill starts
just inside the sleeve's opening, BACK up it, and comes out along the forearm LENGTH past it, flaring out (CUFF_FLARE) and
folded (CUFF_FOLDS of CUFF_FOLD). Both are one surface, as the garments are; the game draws them from both sides.

Sizes are in Blender units at the rig's scale (the figure about 22 tall, 1.25 cells; one cell is about 17.6). The frills
are chunky, as a pawn is only so many pixels at the game's usual zoom, and low-poly like the shirt. Their texture places
are all on one plain patch of the shirt's image (the brightest, evenest square of PATCH pixels), so they take the
apparel's colour as the shirt does. Weights and body keys are left to the exporter, which takes them from the body under
each vertex as it does for the shirt (it is not one of export_frames.py's KEEP_WEIGHTS). The .blend is never saved; the
optional out.blend is a copy with the shirt on the rig, to look at."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_formal_shirt.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'Models', 'garments', 'CollarShirt.glb')
NAME = 'ShirtRuffle'

# The jabot, in world space at rest (x to the figure's left, y back, z up; the front is -y).
TIERS = 3
TOP = 19.55            # the first tier's top, just under the collar's V (19.68)
STEP = 0.5             # each tier's top this far under the one above's
LENGTH_JABOT = 0.85    # each tier from its top to its bottom
TOP_HALF = 0.3         # half its width at the top
BOTTOM_HALF = 0.8      # and at the bottom
WIDEN = 0.12           # each tier down this much wider again (a share of the first's)
LIFT = 0.06            # its top off the shirt
FLARE = 0.4            # its bottom further off
FOLDS = 3              # folds across its bottom
FOLD = 0.2             # their depth
SCALLOP = 0.08         # the bottom edge drawn up between the folds
ROWS, COLS = 3, 4 * FOLDS + 1

# The cuffs' frills, about the forearm's line (forearm.<side> to hand.<side> at rest).
BACK = 0.12            # how far up inside the sleeve each starts
LENGTH = 0.45          # how far past the cuff it comes
INSET = 0.94           # its start's distance from the forearm's line, a share of the cuff's, so it starts inside it
CUFF_FLARE = 0.35      # its edge further out from the line
CUFF_FOLDS = 7
CUFF_FOLD = 0.1
SAMPLES = 28           # points round
CUFF_ROWS = 3

PATCH = 24             # pixels: the square of the image the frills take their colour from


def ring_loops(bm):
    """The shirt's openings: each loop of boundary edges, as a list of its vertices (in no order)."""
    left = {e for e in bm.edges if e.is_boundary}
    loops = []
    while left:
        e = left.pop()
        stack, vs = [e], set(e.verts)
        while stack:
            for v in stack.pop().verts:
                for y in v.link_edges:
                    if y in left:
                        left.remove(y)
                        stack.append(y)
                        vs.update(y.verts)
        loops.append([v.co.copy() for v in vs])
    return loops


def plain_patch(img):
    """The middle of the brightest, evenest PATCH-pixel square of the image, as a texture place."""
    w, h = img.size
    px = list(img.pixels[:])
    best, at = None, (0.5, 0.5)
    for y0 in range(0, h - PATCH, PATCH):
        for x0 in range(0, w - PATCH, PATCH):
            vals = []
            for y in range(y0, y0 + PATCH, 3):
                row = (y * w) * 4
                for x in range(x0, x0 + PATCH, 3):
                    k = row + x * 4
                    vals.append((px[k] + px[k + 1] + px[k + 2]) / 3 * px[k + 3])
            mean = sum(vals) / len(vals)
            spread = max(vals) - min(vals)
            score = mean - 2 * spread
            if best is None or score > best:
                best, at = score, ((x0 + PATCH / 2) / w, (y0 + PATCH / 2) / h)
    return at


def add_sheet(bm, uv_layer, grid, outward, uv):
    """A sheet of quads over grid (rows of points), each face turned to face along outward(point)."""
    verts = [[bm.verts.new(p) for p in row] for row in grid]
    for r in range(len(grid) - 1):
        for c in range(len(grid[r]) - 1):
            quad = [verts[r][c], verts[r][c + 1], verts[r + 1][c + 1], verts[r + 1][c]]
            f = bm.faces.new(quad)
            f.normal_update()
            middle = sum((v.co for v in quad), Vector()) / 4
            if f.normal.dot(outward(middle)) < 0:
                f.normal_flip()
            f.smooth = True
            for l in f.loops:
                l[uv_layer].uv = uv


# The collared shirt, as the garment pipeline has it.
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=SOURCE, disable_bone_shape=True)
new = [o for o in bpy.data.objects if o not in before]
shirt = next(o for o in new if o.type == 'MESH')
for o in new:
    if o is not shirt:
        bpy.data.objects.remove(o)
old = bpy.data.objects.get(NAME)
if old is not None:
    bpy.data.objects.remove(old)
shirt.name = NAME
shirt.data.name = NAME
if shirt.data.shape_keys:   # the exporter fits every key from the body; the frills have no moves of their own
    shirt.shape_key_clear()

body = bpy.data.objects['Body']
rig = body.find_armature()
mw = shirt.matrix_world.copy()

# The shirt welded, in world space: its surface to lay the jabot on, and its openings.
welded = bmesh.new()
welded.from_mesh(shirt.data)
welded.transform(mw)
bmesh.ops.remove_doubles(welded, verts=welded.verts, dist=1e-4)
tree = BVHTree.FromBMesh(welded)
loops = ring_loops(welded)

img = None
for m in shirt.data.materials:
    if m and m.use_nodes:
        for n in m.node_tree.nodes:
            if n.type == 'TEX_IMAGE' and n.image:
                img = n.image
uv = Vector(plain_patch(img)) if img else Vector((0.5, 0.5))

bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new('UVMap')


def front_of(x, z):
    """The shirt's front surface at (x, z), and its way out."""
    loc, n, _, _ = tree.ray_cast(Vector((x, -50.0, z)), Vector((0, 1, 0)))
    if loc is None:
        sys.exit('make_formal_shirt.py: no shirt at x %.2f z %.2f to hang the jabot on' % (x, z))
    if n.y > 0:
        n = -n
    return loc, n


# The jabot: the lowest tier first, so a tier's faces come after those it hangs over (no matter to the game).
for k in reversed(range(TIERS)):
    top = TOP - k * STEP
    widen = 1 + WIDEN * k
    grid = []
    for r in range(ROWS):
        t = r / (ROWS - 1)
        row = []
        for c in range(COLS):
            s = c / (COLS - 1) * 2 - 1   # -1 .. 1 across
            half = (TOP_HALF + (BOTTOM_HALF - TOP_HALF) * t) * widen
            wave = 0.5 * (1 + math.cos(math.pi * FOLDS * (s + 1)))   # 1 on a fold's crest, 0 between
            z = top - LENGTH_JABOT * t + SCALLOP * t * (1 - wave)
            loc, n = front_of(s * half, z)
            row.append(loc + n * (LIFT + FLARE * t + FOLD * t * wave))
        grid.append(row)
    add_sheet(bm, uv_layer, grid, lambda p: Vector((0, -1, 0)), uv)

# The cuffs' frills.
for side in ('L', 'R'):
    fore = rig.matrix_world @ rig.data.bones['forearm.' + side].head_local
    wrist = rig.matrix_world @ rig.data.bones['hand.' + side].head_local
    axis = (wrist - fore).normalized()
    cuff = max(loops, key=lambda vs: sum(v.x for v in vs) / len(vs) * (1 if side == 'L' else -1))
    centre = sum(cuff, Vector()) / len(cuff)
    u = axis.cross(Vector((0, 0, 1))).normalized()
    v = axis.cross(u).normalized()
    # The cuff round the forearm's line: each of its points' angle, distance out and place along the line.
    polar = sorted((math.atan2((p - centre).dot(v), (p - centre).dot(u)),
                    ((p - centre) - axis * (p - centre).dot(axis)).length, (p - centre).dot(axis)) for p in cuff)

    def cuff_at(a):
        """The cuff's distance out and place along the line at angle a, between its points either side."""
        n = len(polar)
        for i in range(n):
            a0, r0, x0 = polar[i - 1]
            a1, r1, x1 = polar[i]
            if i == 0:
                a0 -= 2 * math.pi
            if a0 <= a <= a1:
                f = (a - a0) / ((a1 - a0) or 1)
                return r0 + (r1 - r0) * f, x0 + (x1 - x0) * f
        a0, r0, x0 = polar[-1]
        a1, r1, x1 = polar[0]
        f = (a - a0) / ((a1 + 2 * math.pi - a0) or 1)
        return r0 + (r1 - r0) * f, x0 + (x1 - x0) * f

    grid = []
    for r in range(CUFF_ROWS):
        t = r / (CUFF_ROWS - 1)
        row = []
        for s in range(SAMPLES + 1):   # the last closes the ring on the first's place
            a = -math.pi + 2 * math.pi * (s % SAMPLES) / SAMPLES
            radius, along = cuff_at(a)
            way = u * math.cos(a) + v * math.sin(a)
            wave = 0.5 * (1 + math.cos(CUFF_FOLDS * a))
            row.append(centre + axis * (along - BACK + (BACK + LENGTH) * t)
                       + way * (radius * INSET + (CUFF_FLARE + CUFF_FOLD * wave) * t))
        grid.append(row)
    add_sheet(bm, uv_layer, grid, lambda p, c=centre, a=axis: ((p - c) - a * (p - c).dot(a)).normalized(), uv)
# Each ring's seam: its last column welded onto its first.
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)

# Into the shirt's own mesh, in its object space, on its material.
bm.transform(mw.inverted())
frills = bmesh.new()
frills.from_mesh(shirt.data)
uv_shirt = frills.loops.layers.uv.active or frills.loops.layers.uv.new('UVMap')
mapping = {}
for vtx in bm.verts:
    mapping[vtx] = frills.verts.new(vtx.co)
added = 0
for f in bm.faces:
    nf = frills.faces.new([mapping[x] for x in f.verts])
    nf.smooth = True
    nf.material_index = 0
    for l, ol in zip(nf.loops, f.loops):
        l[uv_shirt].uv = ol[uv_layer].uv
    added += 1
frills.to_mesh(shirt.data)
frills.free()
bm.free()
welded.free()

print('SHIRTRUFFLE: %d vertices, %d faces (%d of them frills), colour from the image at %.3f %.3f'
      % (len(shirt.data.vertices), len(shirt.data.polygons), added, uv.x, uv.y))

# On the rig, as import_garments puts a garment, to look at in out.blend.
world = shirt.matrix_world.copy()
shirt.parent = rig
shirt.matrix_world = world
shirt.modifiers.new('Armature', 'ARMATURE').object = rig

bpy.ops.object.select_all(action='DESELECT')
shirt.select_set(True)
bpy.context.view_layer.objects.active = shirt
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=False,
                          export_animations=False, export_morph=False, export_extras=True)
print('SHIRTRUFFLE -> %s' % out_glb)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
