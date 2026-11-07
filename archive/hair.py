# Builds 3D hair for the chibi from a hairstyle's three sprites (south, east, north), with no hand modelling.
#   blender -b Models/pawnHumanRig.blend --python archive/hair.py -- <Hairs texture folder> <out folder> [--preview x.blend] [Bob Afro ...]
# Without style names it does every style in the folder (Royalty/Anne for one in a subfolder). For each it writes
# <style>.txt, for the game (SkinnedFigure.cs): the mesh in the rest pose in the game's model space, as skin.txt is,
# with smooth normals, texture coordinates and the body's keys fitted to it; and <style>.png, the three sprites side by
# side, which the texture coordinates point into. The game draws a pawn's hair from the file named after its HairDef's
# texPath (less Things/Pawn/Humanlike/Hairs/). The hair rides the head bone. With --preview it also saves a .blend with
# every style on the rig, each in its own collection (show one at a time).
#
# Each style also gets <style>.shell.txt, the shell its outline is drawn from: the whole closed hull, before the
# see-through triangles are dropped, with the vertices on no hair laid on the head and marked as skin (outlined in the
# skin's colour). The outline is an inside-out shell of which only the far side shows, so the hair's own open mesh would
# leave the outline broken wherever its far side has a hole (the face, the nape, between strands), with the head's
# outline showing in the gaps. It lists the body triangles it encloses, which the game leaves out of the head's outline
# while the hair is worn, so the two don't overlap.
#
# The shape is the sprites' visual hull: each sprite's outline pushed straight through along its view, and the voxels that
# fall inside every one kept. Three views can only give a boxy shape, so each horizontal slice is also cut to the rounded
# rectangle (a superellipse) that fits its width and depth. The south sprite has a hole where the face is, which would carve
# a tunnel through the head, so every outline has Facial Animation's head (the art these sprites are drawn over) filled in
# first. The face opening then comes from the texture: each triangle takes the sprite that faces it most, and the triangles
# that land on a see-through part of it are dropped.
#
# Source/HairBuilder.cs does all of this again in the game, for hairstyles with no file (modded ones), so keep the two in
# step: a change to the method here wants the same change there (and its Version raised, so cached hair is rebuilt).
#
# Sprite space: pixels of the 256 px sprites. X is the south sprite's column (the viewer's right, the figure's left), Y its
# row (down), Z the east sprite's column (towards the face: the figure faces east in it, and shows its right side).
import bpy, bmesh, math, os, sys
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'anim_scripts'))
import walk

HEAD_SPRITES = os.path.expanduser('~/.local/share/Steam/steamapps/workshop/content/294100/1635901197/1.6/Textures/'
                                  'Things/Pawn/Humanlike/Heads_Blank/Normal/Female')   # Facial Animation's head, normal_<view>.png
SIZE = 256           # the hair sprites' width and height, pixels
VOXEL = 2            # sprite pixels per voxel side
HEAD_GROW = 2        # pixels the head's outline is grown by before it is filled into the hair's
ROUND = 2.6          # the superellipse power each slice is cut to (2 an ellipse; higher is boxier)
SMOOTH = 12          # smoothing passes on the remeshed hull
TRIANGLES = 3000     # about how many triangles to keep per style
CLEAR = 0.12         # Blender units the hair is kept off the head (the head is about 9.5 wide)
TIGHT = 0.6          # the hair's gap off the head is scaled by this (after texturing, so the art comes in with it) ...
TIGHT_REACH = 3.0    # ... up to this far out (Blender units); past it, a bun or ponytail keeps its own length
TIGHT_SMOOTH = 3     # smoothing passes after pulling in, for the folds where neighbours were pulled different ways
SOLID = 0.5          # least alpha that counts as hair (the game's cut-out)
CUT = 0.9            # alpha the mesh is cut at, a little inside the art: cut at SOLID, the shader would drop a sliver
                     # along the edge (blended pixels, and smaller mip levels at a distance) and the wall's join would show
SKIRT_REACH = 2.5    # Blender units: the shell's open edges are walled in towards the head up to this deep (a strand
                     # further out than that gets a wall this thick instead of one reaching all the way in)
SKIRT_INSET = 4      # pixels in from the middle of an open edge (towards its triangle's middle) its wall looks for its colour
SKIRT_DARK = 0.3     # grey at or below which a pixel is the art's outline, which the walls don't take their colour from
SKIRT_SINK = 0.15    # Blender units the wall goes into the head, so no gap shows where it meets it
CROWN = 0.6          # least share of a triangle's normal pointing up for it to count as the top of the head
OUTLINE = 6          # pixels below a sprite's top edge the crown is coloured from, under the black outline
CROWN_SPREAD = 6     # columns either side whose lowest top edge is used, so a tufty edge (an afro) doesn't streak
# Where Facial Animation's head is in the 256 px sprites (measured from normal_<view>.png): the south and north
# sprites' columns and rows, and the east sprite's columns, back of the head to the face.
SPRITE_HEAD_X = (87, 168)
SPRITE_HEAD_Y = (77, 171)
SPRITE_HEAD_Z = (84, 169)
VIEWS = ('south', 'east', 'north')

FIGURE_HEIGHT = 1.25   # as export_frames.py: the standing figure's height in cells, which sets the scale

args = sys.argv[sys.argv.index('--') + 1:]
hairs, out = args[0], os.path.abspath(args[1])
preview = None
if '--preview' in args:
    preview = os.path.abspath(args[args.index('--preview') + 1])
    args = args[:args.index('--preview')] + args[args.index('--preview') + 2:]
styles = args[2:]
if not styles:
    for root, _, files in os.walk(hairs):
        for f in sorted(files):
            if f.endswith('_south.png'):
                styles.append(os.path.relpath(os.path.join(root, f[:-len('_south.png')]), hairs).replace(os.sep, '/'))
os.makedirs(out, exist_ok=True)
rig = walk.arm
body = bpy.data.objects.get('Pawn') or max((o for o in bpy.data.objects if o.type == 'MESH'), key=lambda o: len(o.data.vertices))
if body.data.shape_keys:
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0

def figure_scale():
    """Cells per Blender unit, as export_frames.py works it out: the standing pose made FIGURE_HEIGHT tall."""
    rig.animation_data_create()
    rig.animation_data.action = None
    walk.apply_pose(walk.stand_pose())
    bpy.context.view_layer.update()
    ev = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
    top = max((body.matrix_world @ v.co).z for v in ev.data.vertices)
    return FIGURE_HEIGHT / top

scale = figure_scale()

def to_game(v):
    """A world place in the game's model space (cells), as export_frames.to_game."""
    return Vector((-v.x, v.z, -v.y)) * scale

def load(path):
    """A png's alpha and grey value as SIZE x SIZE arrays, top row first (a larger image is shrunk to SIZE by taking
    the most solid pixel of each block)."""
    im = bpy.data.images.load(path, check_existing=True)
    w, h = im.size
    px = np.array(im.pixels[:], dtype=np.float32).reshape(h, w, 4)[::-1]
    f = w // SIZE
    if f > 1:
        px = px.reshape(SIZE, f, SIZE, f, 4).max(axis=(1, 3))
    return px

def grow(mask, r):
    m = mask.copy()
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dx * dx + dy * dy <= r * r:
                m |= np.roll(np.roll(mask, dy, 0), dx, 1)
    return m

head_masks = {v: grow(load(os.path.join(HEAD_SPRITES, 'normal_%s.png' % v))[..., 3] > SOLID, HEAD_GROW) for v in VIEWS}

def on_head():
    """Which body vertices the head bone moves most (bone groups only: selections such as Selecthead left out)."""
    hi = body.vertex_groups[walk.HEAD].index
    bones = {g.index for g in body.vertex_groups if g.name in rig.data.bones}
    heads = set()
    for v in body.data.vertices:
        gs = [g for g in v.groups if g.group in bones]
        if gs and max(gs, key=lambda g: g.weight).group == hi:
            heads.add(v.index)
    return heads

head_verts = on_head()

def chibi_head():
    """The head bone's rest frame (world place and turn) and the chibi head's extent in it: x the figure's left, y up
    the head, z the way it faces. The chin is the lowest point of the front of the face, not of the neck below it."""
    loc, turn, _ = (rig.matrix_world @ rig.data.bones[walk.HEAD].matrix_local).decompose()
    pts = [turn.inverted() @ (body.matrix_world @ body.data.vertices[i].co - loc) for i in head_verts]
    half = max(abs(p.x) for p in pts)
    back, front = min(p.z for p in pts), max(p.z for p in pts)
    top = max(p.y for p in pts)
    chin = min(p.y for p in pts if p.z > 0.6 * front and abs(p.x) < 0.3 * half)
    return loc, turn, (half, chin, top, back, front)

loc, turn, (half, chin, top, back, front) = chibi_head()
print('HAIR head: half width %.2f, chin %.2f, top %.2f, back %.2f, front %.2f' % (half, chin, top, back, front))
SX = 2 * half / (SPRITE_HEAD_X[1] - SPRITE_HEAD_X[0])
SY = (top - chin) / (SPRITE_HEAD_Y[1] - SPRITE_HEAD_Y[0])
SZ = (front - back) / (SPRITE_HEAD_Z[1] - SPRITE_HEAD_Z[0])
MID_X = (SPRITE_HEAD_X[0] + SPRITE_HEAD_X[1]) / 2

def to_head(X, Y, Z):
    """Sprite space to the head bone's frame."""
    return Vector(((X - MID_X) * SX, top - (Y - SPRITE_HEAD_Y[0]) * SY, back + (Z - SPRITE_HEAD_Z[0]) * SZ))

def to_sprite(p):
    return ((p.x / SX) + MID_X, SPRITE_HEAD_Y[0] + (top - p.y) / SY, SPRITE_HEAD_Z[0] + (p.z - back) / SZ)

def hull(px):
    """The voxels inside every view's outline (with the head filled in) and inside each slice's superellipse:
    an array indexed [y, x, z], one voxel per VOXEL pixels."""
    south = (px['south'][..., 3] > SOLID) | head_masks['south']
    north = (px['north'][..., 3] > SOLID) | head_masks['north']
    east = (px['east'][..., 3] > SOLID) | head_masks['east']
    c = np.arange(VOXEL // 2, SIZE, VOXEL)
    s, n, e = south[np.ix_(c, c)], north[np.ix_(c, SIZE - 1 - c)], east[np.ix_(c, c)]
    inside = s[:, :, None] & n[:, :, None] & e[:, None, :]
    across = s & n
    for y in range(len(c)):
        xs, zs = np.nonzero(across[y])[0], np.nonzero(e[y])[0]
        if len(xs) == 0 or len(zs) == 0:
            continue
        cx, rx = (xs[0] + xs[-1]) / 2, (xs[-1] - xs[0]) / 2 + 0.5
        cz, rz = (zs[0] + zs[-1]) / 2, (zs[-1] - zs[0]) / 2 + 0.5
        dx = np.abs(np.arange(len(c)) - cx)[:, None] / rx
        dz = np.abs(np.arange(len(c)) - cz)[None, :] / rz
        inside[y] &= dx ** ROUND + dz ** ROUND <= 1.0
    return inside

def voxel_mesh(inside, name):
    """A closed mesh of the voxels' outer faces, in sprite space."""
    # Padded voxel i spans corners i to i + 1; corner t is at sprite pixel (t - 1) * VOXEL. The winding is put right
    # after remeshing, so it is not kept here.
    g = np.pad(inside, 1).astype(np.int8)
    corner = {}
    faces = []
    def at(t):
        if t not in corner:
            corner[t] = len(corner)
        return corner[t]
    for axis in range(3):
        b, c = [a for a in range(3) if a != axis]
        for idx in np.argwhere(np.diff(g, axis=axis) != 0):
            quad = []
            for db, dc in ((0, 0), (1, 0), (1, 1), (0, 1)):
                t = list(idx)
                t[axis] += 1
                t[b] += db
                t[c] += dc
                quad.append(at(tuple(t)))
            faces.append(quad)
    verts = [None] * len(corner)
    for (y, x, z), i in corner.items():
        verts[i] = ((x - 1) * VOXEL, (y - 1) * VOXEL, (z - 1) * VOXEL)
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    return me

def shaped(me, name):
    """The voxel mesh remeshed, smoothed and thinned down, as a new object in the head bone's frame."""
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    m = ob.modifiers.new('remesh', 'REMESH')
    m.mode, m.voxel_size = 'VOXEL', VOXEL * 1.0
    m = ob.modifiers.new('smooth', 'SMOOTH')
    m.factor, m.iterations = 0.8, SMOOTH
    dg = bpy.context.evaluated_depsgraph_get()
    tris = sum(len(p.vertices) - 2 for p in ob.evaluated_get(dg).data.polygons)
    m = ob.modifiers.new('decimate', 'DECIMATE')
    m.ratio = min(1.0, TRIANGLES / max(tris, 1))
    dg = bpy.context.evaluated_depsgraph_get()
    done = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
    bpy.data.objects.remove(ob)
    bpy.data.meshes.remove(me)
    for v in done.vertices:
        v.co = to_head(v.co.x, v.co.y, v.co.z)
    bm = bmesh.new()
    bm.from_mesh(done)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(done)
    bm.free()
    return done

def head_tree():
    """The body's rest shape in the head bone's frame, for keeping the hair outside it, and the head's own part of it,
    for pulling the hair in: a ponytail's tip is nearer the shoulders, but it hangs from the head."""
    inv = turn.inverted()
    vs = [inv @ (body.matrix_world @ v.co - loc) for v in body.data.vertices]
    polys = [tuple(p.vertices) for p in body.data.polygons]
    return BVHTree.FromPolygons(vs, polys), BVHTree.FromPolygons(vs, [p for p in polys if all(i in head_verts for i in p)])

tree, head_only = head_tree()

def body_keys():
    """The body's rest triangles (world), a tree of them, and each shape key's move of every vertex (world units)."""
    keys = body.data.shape_keys
    mw = body.matrix_world
    base = [mw @ k.co for k in keys.reference_key.data] if keys else [mw @ v.co for v in body.data.vertices]
    moves = {}
    if keys:
        for k in keys.key_blocks:
            if k != keys.reference_key:
                moves[k.name] = [mw.to_3x3() @ (a.co - b.co) for a, b in zip(k.data, keys.reference_key.data)]
    body.data.calc_loop_triangles()
    # The head's triangles only: the hair moves with the head, even where the shoulders are nearer.
    tris = [tuple(t.vertices) for t in body.data.loop_triangles if all(i in head_verts for i in t.vertices)]
    return base, tris, BVHTree.FromPolygons(base, tris), moves

body_rest, body_tris, body_tree, key_moves = body_keys()

def area(a, b, c):
    return (b - a).cross(c - a).length

def fitted_keys(points):
    """Each key's move of each point (world): the head surface nearest it moves as that triangle does, and the point's
    gap off it grows or shrinks as the triangle does (so with Shrinkhead the hair stays as snug on the smaller head)."""
    out = {name: [] for name in key_moves}
    for p in points:
        q, _, t, _ = body_tree.find_nearest(p)
        a, b, c = body_tris[t]
        w = poly_3d_calc([body_rest[a], body_rest[b], body_rest[c]], q)
        before = area(body_rest[a], body_rest[b], body_rest[c])
        for name, m in key_moves.items():
            grow = math.sqrt(area(body_rest[a] + m[a], body_rest[b] + m[b], body_rest[c] + m[c]) / before) if before > 1e-12 else 1.0
            out[name].append(m[a] * w[0] + m[b] * w[1] + m[c] * w[2] + (p - q) * (grow - 1.0))
    return out

def pull_in(me):
    """Brings the hair closer to the head: each vertex's gap off the head scaled by TIGHT, up to TIGHT_REACH."""
    for v in me.vertices:
        q, n, _, _ = head_only.find_nearest(v.co)
        if q is None:
            continue
        gap = (v.co - q).length
        if gap > 1e-6:
            v.co = q + (v.co - q) * ((TIGHT * min(gap, TIGHT_REACH) + max(gap - TIGHT_REACH, 0.0)) / gap)
    bm = bmesh.new()
    bm.from_mesh(me)
    for _ in range(TIGHT_SMOOTH):
        bmesh.ops.smooth_vert(bm, verts=bm.verts, factor=0.5, use_axis_x=True, use_axis_y=True, use_axis_z=True)
    bm.to_mesh(me)
    bm.free()
    clear_of_head(me)
    me.update()

def clear_of_head(me):
    for v in me.vertices:
        q, n, _, _ = tree.find_nearest(v.co)
        if q is not None and (v.co - q).dot(n) < CLEAR:
            v.co = q + n * CLEAR

def view_of(n, p):
    """Which sprite faces a triangle with normal n at p (head frame) most: 0 south (front), 1 east (either side, as the
    west sprite is the east one mirrored), 2 north (back). No sprite sees the top, so the crown takes the front sprite
    over the front half of the head and the back one behind it."""
    if n.y > CROWN:
        return 0 if p.z > (back + front) / 2 else 2
    if abs(n.x) > abs(n.z):
        return 1
    return 0 if n.z > 0 else 2

def top_edges(px):
    """Each sprite's first solid row in every column, the lowest within CROWN_SPREAD columns (0 where they are empty)."""
    edges = []
    for v in VIEWS:
        solid = px[v][..., 3] > SOLID
        e = np.where(solid.any(axis=0), solid.argmax(axis=0), 0)
        edges.append(np.max([np.roll(e, d) for d in range(-CROWN_SPREAD, CROWN_SPREAD + 1)], axis=0))
    return edges

def place(view, p):
    """Where a point (head frame) falls in that view's sprite: column, row."""
    X, Y, Z = to_sprite(p)
    return ((X, Y), (Z, Y), (SIZE - 1 - X, Y))[view]

def alpha_at(a, uv):
    c, r = int(round(uv[0])), int(round(uv[1]))
    return a[r, c] if 0 <= r < SIZE and 0 <= c < SIZE else 0.0

def smooth_alpha(a, uv):
    """The sprite's alpha at a texture place, blended between pixels (so an edge can be found between them)."""
    x, y = uv[0] - 0.5, uv[1] - 0.5
    c, r = int(math.floor(x)), int(math.floor(y))
    fx, fy = x - c, y - r
    total = 0.0
    for dr, dc, w in ((0, 0, (1 - fx) * (1 - fy)), (0, 1, fx * (1 - fy)), (1, 0, (1 - fx) * fy), (1, 1, fx * fy)):
        rr, cc = r + dr, c + dc
        if 0 <= rr < SIZE and 0 <= cc < SIZE:
            total += a[rr, cc] * w
    return total

class Cut:
    """The hair mesh cut along the edge of its art: its own vertices, and the new ones on the cuts, each kept as a place
    along one of its edges so it follows the mesh when it is pulled in (pull_in) after texturing."""
    def __init__(self, me):
        self.me = me
        self.along = []   # new vertex len(me.vertices) + k: (i, j, t), t of the way from vertex i to vertex j
        self.made = {}

    def point(self, i, j, t):
        key = (i, j, round(t, 6))
        if key not in self.made:
            self.made[key] = len(self.me.vertices) + len(self.along)
            self.along.append((i, j, t))
        return self.made[key]

    def co(self, k):
        n = len(self.me.vertices)
        if k < n:
            return self.me.vertices[k].co
        i, j, t = self.along[k - n]
        return self.me.vertices[i].co.lerp(self.me.vertices[j].co, t)

    def resolved(self):
        """The vertices as they are now (.co, .normal), the mesh's own then the cuts'."""
        out = [Vert(v.co.copy(), v.normal.copy()) for v in self.me.vertices]
        for i, j, t in self.along:
            out.append(Vert(out[i].co.lerp(out[j].co, t), out[i].normal.lerp(out[j].normal, t).normalized()))
        return out

class Vert:
    def __init__(self, co, normal):
        self.co, self.normal = co, normal

class Mesh:
    def __init__(self, vertices):
        self.vertices = vertices

def textured(me, px):
    """The hair cut out of the hull along the edge of its art, and its triangles, each as (vertices, which copy of them:
    its sprite and whether it is crown, texture coordinates into the atlas: south, east, north side by side). Each
    triangle takes the sprite that faces it most. One wholly off the art (its corners) is dropped, one wholly on it is
    kept, and one across the edge is cut where the alpha crosses CUT, so the mesh ends where the hair is seen to end:
    else the hairline would be drawn across whole triangles by the shader's cut-out, with no edge to hang a wall from."""
    edges = top_edges(px)
    def where(view, crown, p):
        u, v = place(view, p)
        if crown:
            # Seen from the front the crown is all squeezed into the top few rows, which are the outline: colour it
            # from just under that instead.
            v = max(v, edges[view][min(SIZE - 1, max(0, int(u)))] + OUTLINE)
        return u, v
    cut = Cut(me)
    kept = []
    for poly in me.polygons:
        t = tuple(poly.vertices)
        ps = [me.vertices[i].co for i in t]
        view = view_of(poly.normal, sum(ps, Vector()) / 3)
        crown = poly.normal.y > CROWN
        a = px[VIEWS[view]][..., 3]
        alpha = lambda p: smooth_alpha(a, where(view, crown, p))
        on = [alpha(p) >= CUT for p in ps]
        if all(on):
            kept.append((t, (view, crown)))
            continue
        if not any(on):
            continue
        # Round the triangle in its winding, keeping the corners on the art and the crossings between.
        ring = []
        for k in range(3):
            i, j = t[k], t[(k + 1) % 3]
            if on[k]:
                ring.append(i)
            if on[k] != on[(k + 1) % 3]:
                p, q = me.vertices[i].co, me.vertices[j].co
                lo, hi = (0.0, 1.0)   # on the art at the end that is on it
                for _ in range(12):
                    mid = (lo + hi) / 2
                    if (alpha(p.lerp(q, mid)) >= CUT) == on[k]:
                        lo = mid
                    else:
                        hi = mid
                t_cross = lo if on[k] else hi
                # The same crossing from either triangle on the edge: named from its lower-numbered end.
                ring.append(cut.point(i, j, t_cross) if i < j else cut.point(j, i, 1.0 - t_cross))
        for k in range(1, len(ring) - 1):
            kept.append(((ring[0], ring[k], ring[k + 1]), (view, crown)))
    return cut, [(t, which, [((which[0] * SIZE + u + 0.5) / (3 * SIZE), 1 - (v + 0.5) / SIZE)
                             for u, v in (where(which[0], which[1], cut.co(i)) for i in t)])
                 for t, which in kept]

def open_edges(tris):
    """The kept shell's open edges (on one triangle only), each in its triangle's winding, with that triangle's sprite
    and the texture places of its two ends."""
    count, directed = {}, []
    for t, which, uvs in tris:
        for k in range(3):
            a, b = t[k], t[(k + 1) % 3]
            e = (min(a, b), max(a, b))
            count[e] = count.get(e, 0) + 1
            centre = (sum(u for u, _ in uvs) / 3, sum(v for _, v in uvs) / 3)
            directed.append((a, b, which, uvs[k], uvs[(k + 1) % 3], centre))
    return [d for d in directed if count[(min(d[0], d[1]), max(d[0], d[1]))] == 1]

def wall_colours(atlas):
    """The atlas pixels (column, row) a wall may take its colour from: solid all round (so blending with the pixels next
    to it keeps it solid) and not the outline."""
    deep = atlas[..., 3] >= 0.9
    for dr in (-1, 0, 1):
        for dc in (-1, 0, 1):
            deep = deep & np.roll(np.roll(atlas[..., 3] >= 0.9, dr, 0), dc, 1)
    deep &= atlas[..., :3].mean(axis=2) > SKIRT_DARK
    rows, cols = np.nonzero(deep)
    return np.stack([cols, rows], axis=1).astype(np.float64)

def wall_colour(colours, uv, centre):
    """A texture place (atlas) for a wall: the pixel of colours nearest a point SKIRT_INSET pixels in from uv (towards
    centre), or uv itself if there is none."""
    if len(colours) == 0:
        return uv
    to_px = lambda t: np.array((t[0] * 3 * SIZE - 0.5, (1 - t[1]) * SIZE - 0.5))
    p, c = to_px(uv), to_px(centre)
    d = c - p
    length = np.hypot(*d)
    if length > 1e-6:
        p = p + d * min(SKIRT_INSET / length, 1.0)
    col, row = colours[np.argmin(((colours - p) ** 2).sum(axis=1))]
    return ((col + 0.5) / (3 * SIZE), 1 - (row + 0.5) / SIZE)

def inward(p):
    """Where an open edge's wall reaches: just inside the head under p, or SKIRT_REACH in towards it if that is further."""
    q, n, _, _ = head_only.find_nearest(p)
    if q is None:
        return p.copy()
    target = q - n * SKIRT_SINK
    d = (target - p).length
    return p + (target - p) * min(1.0, SKIRT_REACH / d) if d > 1e-6 else p.copy()

def write(style, me, tris, px):
    """<style>.png and <style>.txt. A vertex is written once for each sprite its triangles take (their texture places
    differ), all with the welded mesh's smooth normal so the shading has no seams."""
    path = os.path.join(out, style)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    atlas = np.concatenate([px[v] for v in VIEWS], axis=1)
    im = bpy.data.images.new(style + '_atlas', 3 * SIZE, SIZE, alpha=True)
    im.pixels[:] = atlas[::-1].ravel()
    im.filepath_raw = path + '.png'
    im.file_format = 'PNG'
    im.save()
    copies, places, faces = {}, [], []
    def copy(i, which, uv, at=None):
        if (i, which) not in copies:
            copies[(i, which)] = len(places)
            places.append((i, uv, at if at is not None else me.vertices[i].co))
        return copies[(i, which)]
    for t, which, uvs in tris:
        faces.append([copy(i, which, uv) for i, uv in zip(t, uvs)])

    colours = wall_colours(atlas)
    for k, (a, b, which, uva, uvb, centre) in enumerate(open_edges(tris)):
        # Continues the shell's winding (its neighbour across a-b would run b to a), so the wall faces out of the
        # space between the hair and the head. The mesh already ends where the art does (textured), so the wall needs
        # none of the art's see-through parts: it is one colour, the hair's just inside the edge, on its four corners'
        # own copies, so nothing between them can be cut out.
        ia, ib = inward(me.vertices[a].co), inward(me.vertices[b].co)
        w = wall_colour(colours, ((uva[0] + uvb[0]) / 2, (uva[1] + uvb[1]) / 2), centre)
        oa, ob = copy(a, ('wall', k), w), copy(b, ('wall', k), w)
        ka, kb = copy(a, ('inner', k), w, ia), copy(b, ('inner', k), w, ib)
        faces.append([ob, oa, ka])
        faces.append([ob, ka, kb])
    world = [loc + turn @ at for _, _, at in places]
    keys = fitted_keys(world)
    with open(path + '.txt', 'w') as f:
        f.write('# Maru\'s Pawn Models: hair "%s", made from its sprites by archive/hair.py (SkinnedFigure.cs).\n' % style)
        f.write('# The rest pose in game model space (cells), as skin.txt. It rides the head bone. Texture %s.png.\n'
                % os.path.basename(style))
        f.write('# v x y z: a vertex. n x y z: its normal. t u v: its texture place. f a b c: a triangle (vertex numbers).\n')
        for p in world:
            f.write('v %.5f %.5f %.5f\n' % tuple(to_game(p)))
        for i, _, _ in places:
            n = turn @ me.vertices[i].normal   # the shell's, on its wall too, so the edge shades as one
            f.write('n %.4f %.4f %.4f\n' % (-n.x, n.z, -n.y))
        for _, (u, v), _ in places:
            f.write('t %.5f %.5f\n' % (u, v))
        for k, (a, b, c) in enumerate(faces):
            if k == len(tris):
                f.write('# the walls under the open edges from here on\n')
            f.write('f %d %d %d\n' % (a, c, b))   # reversed, as skin.txt: the axis change mirrors
        f.write('# key name: then d i x y z (vertex i moves), cells: fitted from the body surface under each vertex.\n')
        for name, moves in keys.items():
            f.write('key %s\n' % name)
            for i, d in enumerate(moves):
                if d.length > 1e-7:
                    f.write('d %d %.5f %.5f %.5f\n' % ((i,) + tuple(to_game(d))))
    return im

def write_shell(style, me, tris):
    """<style>.shell.txt: the closed hull, the vertices on no kept triangle laid CLEAR off the head (skin), and the body
    triangles (numbered as skin.txt's) inside it."""
    hair = {i for t, _, _ in tris for i in t}
    at = []
    for v in me.vertices:
        p = v.co.copy()
        if v.index not in hair:
            q, n, _, _ = tree.find_nearest(p)
            if q is not None:
                p = q + n * CLEAR
        at.append(p)
    polys = [tuple(poly.vertices) for poly in me.polygons]
    shell = BVHTree.FromPolygons(at, polys)
    inv = turn.inverted()
    body.data.calc_loop_triangles()
    hides = []
    for k, t in enumerate(body.data.loop_triangles):
        inside = True
        for i in t.vertices:
            p = inv @ (body.matrix_world @ body.data.vertices[i].co - loc)
            q, n, _, _ = shell.find_nearest(p)
            if q is None or (p - q).dot(n) > -0.5 * CLEAR:
                inside = False
                break
        if inside:
            hides.append(k)
    world = [loc + turn @ p for p in at]
    keys = fitted_keys(world)
    with open(os.path.join(out, style) + '.shell.txt', 'w') as f:
        f.write('# Maru\'s Pawn Models: the outline shell of hair "%s" (archive/hair.py, SkinnedFigure.cs): rest pose,\n'
                '# game model space (cells), riding the head bone. v x y z: a vertex. f a b c: a triangle. skin i ...: vertices\n'
                '# on no hair, outlined in the skin\'s colour. hide k ...: body triangles inside it, left out of the head\'s outline.\n'
                % style)
        for p in world:
            f.write('v %.5f %.5f %.5f\n' % tuple(to_game(p)))
        for a, b, c in polys:
            f.write('f %d %d %d\n' % (a, c, b))   # reversed, as skin.txt: the axis change mirrors
        skin = [i for i in range(len(at)) if i not in hair]
        for k in range(0, len(skin), 32):
            f.write('skin %s\n' % ' '.join(str(i) for i in skin[k:k + 32]))
        for k in range(0, len(hides), 32):
            f.write('hide %s\n' % ' '.join(str(i) for i in hides[k:k + 32]))
        for name, moves in keys.items():
            f.write('key %s\n' % name)
            for i, d in enumerate(moves):
                if d.length > 1e-7:
                    f.write('d %d %.5f %.5f %.5f\n' % ((i,) + tuple(to_game(d))))
    return len(hides)

def hair_object(style, me, tris, im):
    """The kept triangles as an object in its own collection, riding the head bone."""
    done = bpy.data.meshes.new(style)
    used = sorted({i for t, _, _ in tris for i in t})
    number = {i: k for k, i in enumerate(used)}
    done.from_pydata([me.vertices[i].co.copy() for i in used], [], [[number[i] for i in t] for t, _, _ in tris])
    uv = done.uv_layers.new()
    for poly, (_, _, uvs) in zip(done.polygons, tris):
        for li, t in zip(poly.loop_indices, uvs):
            uv.data[li].uv = t
    mat = bpy.data.materials.new(style)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    tex = nodes.new('ShaderNodeTexImage')
    tex.image, tex.interpolation = im, 'Closest'
    bsdf = nodes['Principled BSDF']
    mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    mat.node_tree.links.new(tex.outputs['Alpha'], bsdf.inputs['Alpha'])
    if hasattr(mat, 'surface_render_method'):
        mat.surface_render_method = 'DITHERED'
    mat.use_backface_culling = False
    done.materials.append(mat)
    ob = bpy.data.objects.new('Hair_' + style, done)
    group = bpy.data.collections.new('Hair ' + style)
    bpy.context.scene.collection.children.link(group)
    group.objects.link(ob)
    ob.parent, ob.parent_type, ob.parent_bone = rig, 'BONE', walk.HEAD
    bpy.context.view_layer.update()
    ob.matrix_world = Matrix.Translation(loc) @ turn.to_matrix().to_4x4()
    return group

for k, style in enumerate(styles):
    px = {v: load(os.path.join(hairs, '%s_%s.png' % (style, v))) for v in VIEWS}
    me = shaped(voxel_mesh(hull(px), style + '_voxels'), style)
    clear_of_head(me)
    me.update()
    cut, tris = textured(me, px)
    if not tris:
        # Nothing solid enough for the cut-out (Shaved is stubble at 40% alpha): no file, so the head stays bare.
        bpy.data.meshes.remove(me)
        print('HAIR %s: nothing at alpha %.2f or more, left out' % (style, SOLID))
        continue
    pull_in(me)
    hair = Mesh(cut.resolved())
    im = write(style, hair, tris, px)
    hidden = write_shell(style, me, tris)
    if preview:
        # Every style on the one rig in its own collection; only the first is left showing.
        group = hair_object(style, hair, tris, im)
        if k:
            bpy.context.view_layer.layer_collection.children[group.name].hide_viewport = True
            group.hide_render = True
    bpy.data.meshes.remove(me)
    print('HAIR %s: %d triangles, the shell hides %d of the body\'s' % (style, len(tris), hidden))

if preview:
    rig.animation_data.action = None
    for pb in rig.pose.bones:   # back to the rest pose the hair was fitted in
        pb.matrix_basis.identity()
    bpy.ops.wm.save_as_mainfile(filepath=preview, copy=True)
