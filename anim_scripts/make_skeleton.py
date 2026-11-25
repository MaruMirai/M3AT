"""Makes the chibi's skeleton, drawn in place of the body for a person gone to bones: a dessicated corpse, and Anomaly's
shamblers raised from one (the game draws both with its RotDrawMode.Dessicated, its bones and Skull head).

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_skeleton.py -- <out.glb> [out.blend] [--preview out.png] [--jaw]

Written to Models/garments/Skeleton.glb it is the garment Skeleton (export_frames.py's GARMENT_PROPS: drawn for the def
M3AT_Skeleton, which no apparel is, so it is never worn or a stand-in; SkinnedFigure.SkeletonPiece finds it by it).

A cartoon skeleton in the chibi's own proportions, built from the rig's bones and the body's shape at rest, about as
many triangles as the body (the skinning's cost goes by its vertices, so it is kept near the body's): geometry where
the shape shows from every side, painting where a part is thin, flat or fiddly.
  - the skull: the head's own shape (the head with No_Human_Ears, cast from its middle as make_hats.py casts its hats),
    SKULL_SHRINK of it, its cheekbones and temples dented (DENTS), with real eye sockets and a nose hole cut through
    it by Blender's exact boolean, each a dark cone going back into the skull (HOLES, hole_cutter, a few points round
    its rim), and with --jaw the jaw cut off below the mouth line and the hinges below the ears (JAW_), both cuts
    capped dark, the jaw turned open JAW_OPEN about the hinges so the bite shows (without it the teeth and the bite
    are painted on the one piece); its teeth, mottling and sutures painted on the image by azimuth and
    elevation (paint_skull);
  - the spine: a rod on each spine bone, lying toward the back (SPINE_BACK), its vertebrae painted round it;
  - the long bones of the arms and legs: a shaft (SHAFT of the flesh's thickness round it) with a knob at each end
    (KNOB), the one at a bone's far end a little bigger than the next bone's, so the two don't fight where they meet;
    the clavicles, rods;
  - painted and cut out (the image's alpha; on the material Unlined, so they are left out of the outline shell, which
    can't follow the cut, and their outlines are painted instead):
    - the ribcage: a band round the chest RIB_IN of the way to the skin, open at the back for the spine, with RIBS pairs
      of ribs sloping down to the front and the sternum painted on it (paint_ribs);
    - the pelvis: a band round the hips with the iliac wings, the sacrum, the hip sockets and the pubic arch (paint_pelvis);
    - the hands: a card in the hand's plane bending at the knuckles and the finger joint, with the metacarpals and
      fingers painted (paint_hand), and a strip for the thumb;
    - the feet: a card along the foot near the floor with the heel, metatarsals and toes (paint_foot).
Each vertex is weighted whole to one bone (export_frames.py keeps its weights, KEEP_WEIGHTS), so a rod stays rigid and a
card bends at its rows; it is hard, so the body types move it by its bones rather than fattening it; the Babylimbs and
ShrinkHead keys are still fitted from the body, so a child's limbs and a shrunk head take it with them.

One image (IMG): the skull's in its top half, the painted parts' panels (PANELS) and the bone colour's swatch below.

Places are in Blender units at the rig's scale (x to the figure's left, y back, z up). The .blend is never saved; the
optional out.blend is a copy with the skeleton on the rig, to look at, and --preview renders it from four sides."""
import bpy, bmesh, sys, math, os
import numpy
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
preview = None
jaw_cut = '--jaw' in argv
argv = [a for a in argv if a != '--jaw']
if '--preview' in argv:
    preview = os.path.abspath(argv[argv.index('--preview') + 1])
    argv = argv[:argv.index('--preview')] + argv[argv.index('--preview') + 2:]
if not argv:
    sys.exit('make_skeleton.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

NAME = 'Skeleton'
UNLINED = 'Unlined'   # the cut-out parts' material: export_frames.py writes its triangles as unlined lines

# The skull.
SKULL_SHRINK = 0.97     # of the head's reach from its middle: the skull just inside the skin, so a hat still fits it
# Dents (azimuth, elevation, half width, half height, in degrees, and depth in head radii, + out): elevations from the
# head's middle, the eyes at make_hats.py's EYE_AZ, EYE_EL (27, -26), mirrored on either side but for az 0.
DENTS = [
    (47, -36, 16, 12, 0.03),     # the cheekbones
    (78, -8, 20, 18, -0.03),     # the temples
]
# Holes through the skull (azimuth, elevation, half width, half height, degrees, mirrored but at az 0), each with a
# cavity behind it going in that many head radii; the rest of the head near them is subdivided SUBDIVIDE times, out to
# SUBDIVIDE_NEAR of a hole's size, so their rims are round.
# Each hole's last number is the points round its rim: the cavity is dark, so its rim's outline is all that shows of
# its shape, and a few points read as round at the game's size.
SOCKET = (27, -24, 14, 12.5)
HOLES = [SOCKET + (0.34, 8), (0, -38, 4.5, 7.0, 0.2, 6)]
SUBDIVIDE, SUBDIVIDE_NEAR = 0, 1.9
# The jaw: cut off by a plane through the mouth line at the front (JAW_FRONT, elevation) and a hinge below each ear
# (JAW_HINGE: azimuth, elevation), behind which a plane straight across (JAW_BACK head radii behind the hinges) keeps the
# skull's base; let down turned open JAW_OPEN degrees about the hinges, so the bite shows dark.
JAW_FRONT = -48.5
JAW_HINGE = (86, -42)
JAW_BACK = 0.05
JAW_OPEN = 6.0
# The painted face, in degrees on the skull's image.
NOSE = (0, -38, 6.5, 8.0)          # an upturned heart: its middle, half width and half height
TEETH = (-43.0, -54.5, 30.0, 6)    # the band's top and bottom elevations, its half width in azimuth, teeth either side of the middle
JAW_LINE = JAW_FRONT               # the line between the upper and lower teeth

# The rest, in Blender units unless said.
SHAFT = 0.5       # a long bone's shaft radius, of the flesh's reach from its bone line (FLESH_SHARE of the way up its vertices')
KNOB = 0.8        # its end knobs
SHAFT_MOST, KNOB_MOST = 0.4, 0.66   # the thighs' and shins' flesh is far thicker than the arms', but chunky leg bones read as boots
KNOB_FAR = 1.12   # the far end's knob against the near one of the next bone
FLESH_SHARE = 0.3
SIDES = 5         # round a rod
SPINE_BACK = 0.3          # how far from the bone line toward the back skin the column runs, as a share
SPINE_R, NECK_R = 0.36, 0.28
SPINE_FROM = 0.9          # the column's foot over the spine bone's head
VERTEBRA_STEP = 0.62      # painted: a vertebra this tall
CLAVICLE_R = 0.2
# The ribcage band.
RIBS = 6
RIB_TOP, RIB_BOTTOM = 18.75, 15.75   # z at the back of the top and bottom ribs
RIB_DROP = 0.9            # how much lower a rib's front end is than its back
RIB_IN = 0.8              # of the way out to the skin
RIB_HALF = 0.15           # a rib's half height
RIB_ENDS = [12, 14, 17, 22, 32, 48]   # azimuth each rib's front end stops at (degrees from the front), top first
RIB_BACK = 168            # the band's ends either side of the spine, degrees from the front
BAND_STEP = 20            # degrees between its columns
STERNUM = (18.55, 16.0, 0.28)        # z top, z bottom, half width
BAND_MARGIN = 0.3         # band past the outermost ribs, up and down
# The pelvis band: z of its bottom and top, of the way out to the skin, columns round.
PELVIS = (11.7, 14.55, 0.82, 12)
# The hand and foot cards.
HAND_SPREAD = 0.62        # the card's half width, of the hand's width
FOOT_SPREAD = 0.5
OUTLINE = 0.06            # the painted parts' outline, units
COLOURS = {'bone': (0.86, 0.81, 0.69), 'shade': (0.62, 0.56, 0.46), 'line': (0.20, 0.15, 0.11), 'dark': (0.11, 0.075, 0.06)}

# The image and its panels (u0, v0, u1, v1).
IMG = 1024
PANELS = {
    'skull': (0.0, 0.5, 1.0, 1.0),
    'ribs': (0.0, 0.25, 0.75, 0.5),
    'spine': (0.75, 0.25, 1.0, 0.5),
    'pelvis': (0.0, 0.0, 0.5, 0.25),
    'hand': (0.5, 0.0, 0.75, 0.25),
    'foot': (0.75, 0.125, 1.0, 0.25),
    'swatch': (0.75, 0.0, 1.0, 0.125),
}
SWATCH_UV = (0.8125, 0.0625)
DARK_UV = (0.9375, 0.0625)   # the cavities and the mouth inside

body = walk.join_body()
rig = body.parent
keys = body.data.shape_keys.key_blocks
for k in keys:
    k.value = 0.0
bw = body.matrix_world
rest = [bw @ v.co for v in keys['Basis'].data]
eared = [bw @ v.co for v in (keys['No_Human_Ears'] if 'No_Human_Ears' in keys else keys['Basis']).data]
me = body.data
me.calc_loop_triangles()
tris = [tuple(t.vertices) for t in me.loop_triangles]
names = {g.index: g.name for g in body.vertex_groups}
deform = {b.name for b in rig.data.bones if b.use_deform}
mw = rig.matrix_world
bone_head = {b.name: mw @ b.head_local for b in rig.data.bones}
bone_tail = {b.name: mw @ b.tail_local for b in rig.data.bones}


def dominant(i):
    gs = [(g.weight, names[g.group]) for g in me.vertices[i].groups if names[g.group] in deform]
    return max(gs)[1] if gs else ''


dom = [dominant(i) for i in range(len(rest))]

# The head, as make_hats.py has it.
head_group = body.vertex_groups[walk.PART_GROUP % 'Head'].index
in_head = [any(g.group == head_group and g.weight > 0.5 for g in v.groups) for v in me.vertices]
head_verts = [p for p, h in zip(eared, in_head) if h]
head_tree = BVHTree.FromPolygons(eared, [tuple(p.vertices) for p in me.polygons if all(in_head[i] for i in p.vertices)])
lo = Vector([min(p[a] for p in head_verts) for a in range(3)])
hi = Vector([max(p[a] for p in head_verts) for a in range(3)])
CENTRE = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, hi.z - (hi.x - lo.x) / 2))

# The trunk's skin, for the ribs and pelvis: no arm, no head, no lower leg.
OFF_TRUNK = ('upper_arm', 'forearm', 'hand', 'thumb')
trunk_tris = [t for t in tris if not any(in_head[i] for i in t) and sum(dom[i].startswith(OFF_TRUNK) for i in t) < 2
              and sum(dom[i].startswith(('shin', 'foot', 'toe')) for i in t) < 2]
trunk_tree = BVHTree.FromPolygons(rest, trunk_tris)


def way(az, el=0.0):
    a, e = math.radians(az), math.radians(el)
    return Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e)))


_reach = {}
def head_reach(az, el):
    key = (round(az, 3), round(el, 3))
    if key not in _reach:
        ds = []
        for da, de in ((0, 0), (5, 0), (-5, 0), (0, 5), (0, -5), (4, 4), (-4, 4), (4, -4), (-4, -4)):
            hit = head_tree.ray_cast(CENTRE, way(az + da, max(-89.9, min(89.9, el + de))))
            if hit[0] is not None:
                ds.append(hit[3])
        _reach[key] = sum(ds) / len(ds) if ds else None
    return _reach[key]


R = sum(head_reach(az, el) or 0 for az in range(0, 360, 30) for el in range(0, 90, 15)) / 72


def bump(az, el, spec):
    """How much of a dent's depth reaches az, el: 1 at its middle, easing to 0 at its edge (an ellipse)."""
    a0, e0, ha, he = spec[:4]
    best = 0.0
    for sign in ((1, -1) if a0 else (1,)):
        da = ((az - sign * a0 + 180) % 360 - 180) / ha
        de = (el - e0) / he
        d = math.sqrt(da * da + de * de)
        if d < 1:
            best = max(best, 0.5 + 0.5 * math.cos(math.pi * d))
    return best


def trunk_axis(z):
    """The middle of the trunk front to back at height z, on x = 0."""
    hits = [trunk_tree.ray_cast(Vector((0, s * 10, z)), Vector((0, -s, 0)), 20.0) for s in (1, -1)]
    ys = [h[0].y for h in hits if h[0] is not None]
    return Vector((0, sum(ys) / len(ys) if len(ys) == 2 else 0.0, z))


def trunk_reach(az, z):
    c = trunk_axis(z)
    hit = trunk_tree.ray_cast(c, way(az), 20.0)
    return (hit[3] if hit[0] is not None else 1.5), c


def in_panel(name, u, v):
    """A place 0..1 across and up a panel as a place on the image."""
    u0, v0, u1, v1 = PANELS[name]
    return (u0 + (u1 - u0) * u, v0 + (v1 - v0) * v)


# --- the mesh

bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new()
weights = {}    # bmesh vertex: bone
cut_faces = set()


def add_face(vs, uvs, bone=None, cut=False):
    try:
        f = bm.faces.new(vs)
    except ValueError:
        return None
    for l, uv in zip(f.loops, uvs):
        l[uv_layer].uv = uv
    f.smooth = True
    if bone is not None:
        for v in vs:
            weights[v] = bone
    if cut:
        f.material_index = 1
        cut_faces.add(f)
    return f


def frame_of(axis):
    axis = axis.normalized()
    side = axis.cross(Vector((0, 0, 1)))
    if side.length < 1e-3:
        side = axis.cross(Vector((0, 1, 0)))
    side.normalize()
    return side, axis.cross(side).normalized()


def lathe(a, b, profile, bone, sides=SIDES, uv_of=None):
    """A closed solid round the line from a to b: profile is (t along it, 0 at a and 1 at b, may go past either; radius)
    from end to end, each end closed to its point. uv_of(t): its place on the image at t (the swatch if none)."""
    axis = b - a
    u, v = frame_of(axis)
    rings = []
    for t, r in profile:
        c = a + axis * t
        if r <= 1e-6:
            rings.append([bm.verts.new(c)])
            continue
        rings.append([bm.verts.new(c + (u * math.cos(2 * math.pi * k / sides) + v * math.sin(2 * math.pi * k / sides)) * r)
                      for k in range(sides)])
    uvs = [uv_of(t) if uv_of else SWATCH_UV for t, _ in profile]
    for i, (r0, r1) in enumerate(zip(rings, rings[1:])):
        if len(r0) == 1 and len(r1) == 1:
            continue
        a_uv, b_uv = uvs[i], uvs[i + 1]
        for k in range(sides):
            k1 = (k + 1) % sides
            if len(r0) == 1:
                add_face([r0[0], r1[k], r1[k1]], [a_uv, b_uv, b_uv], bone)
            elif len(r1) == 1:
                add_face([r0[k], r1[0], r0[k1]], [a_uv, b_uv, a_uv], bone)
            else:
                add_face([r0[k], r1[k], r1[k1], r0[k1]], [a_uv, b_uv, b_uv, a_uv], bone)


def rod(a, b, r, bone, knob_a, knob_b, sides=SIDES):
    """A bone from a to b: a shaft of radius r, swelling to a knob at each end (radius knob_a, knob_b)."""
    length = (b - a).length
    ea, eb = knob_a / length, knob_b / length
    lathe(a, b, [(-ea * 0.75, 0.0), (0.0, knob_a), (ea * 1.4, r), (1 - eb * 1.4, r), (1.0, knob_b), (1 + eb * 0.75, 0.0)],
          bone, sides=sides)


def tube(path, r, bone, sides=SIDES):
    """A rod swept along a path of points, a ring at each point square to the path there, its ends closed to a point."""
    n = len(path)
    rings = []
    for i, p in enumerate(path):
        t = (path[min(i + 1, n - 1)] - path[max(i - 1, 0)]).normalized()
        u, v = frame_of(t)
        rings.append([bm.verts.new(p + (u * math.cos(2 * math.pi * k / sides) + v * math.sin(2 * math.pi * k / sides)) * r)
                      for k in range(sides)])
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(sides):
            add_face([r0[k], r1[k], r1[(k + 1) % sides], r0[(k + 1) % sides]], [SWATCH_UV] * 4, bone)
    for ring, p, q in ((rings[0], path[0], path[1]), (rings[-1], path[-1], path[-2])):
        tip = bm.verts.new(p + (p - q).normalized() * r * 0.8)
        for k in range(sides):
            add_face([ring[k], ring[(k + 1) % sides], tip], [SWATCH_UV] * 3, bone)


def sheet(points, uvs, bones, closed=False):
    """A cut-out card: a grid of points (rows of columns), each row's vertices on its bone (bones[row]), or each
    vertex's (bones[row][col]); closed joins the last column to the first (its uvs then have one column more)."""
    vs = [[bm.verts.new(p) for p in row] for row in points]
    for j, row in enumerate(vs):
        for i, v in enumerate(row):
            b = bones[j] if isinstance(bones[j], str) else bones[j][i]
            weights[v] = b
    cols = len(points[0])
    for j in range(len(vs) - 1):
        for i in range(cols if closed else cols - 1):
            i1 = (i + 1) % cols
            add_face([vs[j][i], vs[j][i1], vs[j + 1][i1], vs[j + 1][i]],
                     [uvs[j][i], uvs[j][i + 1], uvs[j + 1][i + 1], uvs[j + 1][i]], cut=True)


def flesh_reach(bone):
    """The body's reach round a bone's line, FLESH_SHARE of the way up the vertices it moves most."""
    a, b = bone_head[bone], bone_tail[bone]
    ax = b - a
    ds = []
    for p, d in zip(rest, dom):
        if d != bone:
            continue
        t = max(0.0, min(1.0, (p - a).dot(ax) / ax.length_squared))
        ds.append((p - (a + ax * t)).length)
    ds.sort()
    return ds[int(len(ds) * FLESH_SHARE)] if ds else 0.5


# The skull: the head's own mesh (its ears taken off by No_Human_Ears), SKULL_SHRINK of it about its middle, its texture
# by azimuth and elevation from there (0 at the back, the front in the middle).
def direction(p):
    d = (p - CENTRE).normalized()
    return math.degrees(math.atan2(d.x, -d.y)), math.degrees(math.asin(max(-1.0, min(1.0, d.z))))


def hole_d(az, el, spec):
    a0, e0, ha, he = spec[:4]
    best = 9.0
    for sign in ((1, -1) if a0 else (1,)):
        da = ((az - sign * a0 + 180) % 360 - 180) / ha
        best = min(best, math.hypot(da, (el - e0) / he))
    return best


def on_skull(az, el):
    return CENTRE + way(az, el) * (head_reach(az, el) or R) * SKULL_SHRINK


sk = bmesh.new()
made = {}
for poly in me.polygons:
    if not all(in_head[i] for i in poly.vertices):
        continue
    for i in poly.vertices:
        if i not in made:
            made[i] = sk.verts.new(CENTRE + (eared[i] - CENTRE) * SKULL_SHRINK)
    try:
        sk.faces.new([made[i] for i in poly.vertices])
    except ValueError:
        pass
original = set(sk.verts)
for _ in range(SUBDIVIDE):
    near = [f for f in sk.faces if min(hole_d(*direction(f.calc_center_median()), spec) for spec in HOLES) < SUBDIVIDE_NEAR]
    bmesh.ops.subdivide_edges(sk, edges=list({e for f in near for e in f.edges}), cuts=1, use_grid_fill=True)
    bmesh.ops.triangulate(sk, faces=sk.faces[:])
for v in sk.verts:
    az, el = direction(v.co)
    if v not in original:
        v.co = on_skull(az, el)   # (the new vertices laid on the head's smooth shape, not its flat faces)
    v.co += way(az, el) * sum(bump(az, el, spec) * spec[4] for spec in DENTS) * R

# The holes and the jaw are cut by Blender's exact boolean, with cutters whose faces are on the second material (the
# dark inside), which the cut carries over: each hole's cutter has walls straight out from the head's middle through
# its outline, so the hole's rim is exactly the outline, then narrows in and back to the cavity's back; the jaw's is
# the part of a big box below the mouth's plane and in front of the plane across behind the hinges.
skull_mats = [bpy.data.materials.new('skull_bone'), bpy.data.materials.new('skull_dark')]


def as_object(name, b):
    m = bpy.data.meshes.new(name)
    b.to_mesh(m)
    for mat in skull_mats:
        m.materials.append(mat)
    ob = bpy.data.objects.new(name, m)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def cut(target, cutter, operation):
    """The target's mesh with the cutter taken away (DIFFERENCE) or kept (INTERSECT), as a new mesh."""
    mod = target.modifiers.new('cut', 'BOOLEAN')
    mod.operation = operation
    mod.object = cutter
    mod.solver = 'EXACT'
    mod.use_self = True           # (the dented head may touch itself near the nose, which the plain cut gives up on)
    mod.use_hole_tolerant = True
    if hasattr(mod, 'material_mode'):
        mod.material_mode = 'INDEX'
    bpy.context.view_layer.update()
    result = bpy.data.meshes.new_from_object(target.evaluated_get(bpy.context.evaluated_depsgraph_get()))
    print('SKELETON: %s by %s: %d faces to %d' % (operation.lower(), cutter.name, len(target.data.polygons), len(result.polygons)))
    target.modifiers.remove(mod)
    return result


def hole_cutter(a0, e0, ha, he, depth, points):
    """A cone: walls straight out from the head's middle through the hole's outline (points round it), from outside the
    skull to just inside it, then in to the cavity's back."""
    c = bmesh.new()
    mdir = way(a0, e0)
    middle = on_skull(a0, e0)
    rings = []
    for share in (1.3, 0.92):
        rings.append([c.verts.new(CENTRE + way(a0 + ha * math.cos(t), e0 + he * math.sin(t))
                                  * (head_reach(a0 + ha * math.cos(t), e0 + he * math.sin(t)) or R) * SKULL_SHRINK * share)
                      for t in (2 * math.pi * (k + 0.5) / points for k in range(points))])
    outer = c.verts.new(CENTRE + mdir * (head_reach(a0, e0) or R) * 1.5)
    back = c.verts.new(middle - mdir * depth * R)
    for k in range(points):
        k1 = (k + 1) % points
        c.faces.new([rings[0][k], rings[0][k1], rings[1][k1], rings[1][k]])
        c.faces.new([rings[0][k1], rings[0][k], outer])
        c.faces.new([rings[1][k], rings[1][k1], back])
    bmesh.ops.recalc_face_normals(c, faces=c.faces[:])
    for f in c.faces:
        f.material_index = 1
    return c


bmesh.ops.recalc_face_normals(sk, faces=sk.faces[:])
skull_ob = as_object('SkullCut', sk)
sk.free()
cutters = []
for a0, e0, ha, he, depth, points in HOLES:
    for sign in ((1, -1) if a0 else (1,)):
        cutter = as_object('HoleCutter', hole_cutter(sign * a0, e0, ha, he, depth, points))
        cutters.append(cutter)
        done = cut(skull_ob, cutter, 'DIFFERENCE')
        skull_ob.data = done

mouth = on_skull(0, JAW_FRONT)
hinges = [on_skull(sign * JAW_HINGE[0], JAW_HINGE[1]) for sign in (1, -1)]
cut_no = (hinges[0] - mouth).cross(hinges[1] - mouth).normalized()
if cut_no.z < 0:
    cut_no = -cut_no
back_y = hinges[0].y + JAW_BACK * R
box = bmesh.new()
bmesh.ops.create_cube(box, size=4 * R, matrix=Matrix.Translation(CENTRE))
for co, no in ((mouth, cut_no), (Vector((0, back_y, 0)), Vector((0, 1, 0)))):
    res = bmesh.ops.bisect_plane(box, geom=box.verts[:] + box.edges[:] + box.faces[:], plane_co=co, plane_no=no, clear_outer=True)
    bmesh.ops.holes_fill(box, edges=[e for e in box.edges if len(e.link_faces) < 2], sides=0)
bmesh.ops.recalc_face_normals(box, faces=box.faces[:])
for f in box.faces:
    f.material_index = 1
jaw_cutter = as_object('JawCutter', box)
box.free()
if jaw_cut:
    cranium = cut(skull_ob, jaw_cutter, 'DIFFERENCE')
    jaw = cut(skull_ob, jaw_cutter, 'INTERSECT')
else:
    cranium = skull_ob.data   # (one piece: the teeth and the bite are only painted)
    jaw = None

hinge_axis = (hinges[0] - hinges[1]).normalized()
hinge_mid = (hinges[0] + hinges[1]) * 0.5
turn = Matrix.Rotation(math.radians(JAW_OPEN), 3, hinge_axis)
if (turn @ (mouth - hinge_mid)).z > (mouth - hinge_mid).z:
    turn = Matrix.Rotation(-math.radians(JAW_OPEN), 3, hinge_axis)   # (opening is the front going down)


def skull_part(mesh_, moved):
    """A cut piece of the skull into the main mesh: its dark faces (the cutters') on the dark swatch, the rest textured by
    each corner's direction from the head's middle before it moved."""
    out = {}
    for v in mesh_.vertices:
        out[v.index] = bm.verts.new(moved(v.co.copy()))
        weights[out[v.index]] = walk.HEAD
    count = 0
    for f in mesh_.polygons:
        vs = [out[i] for i in f.vertices]
        if f.material_index == 1:
            add_face(vs, [DARK_UV] * len(vs), walk.HEAD)
        else:
            dirs = [direction(mesh_.vertices[i].co) for i in f.vertices]
            us = [(az + 180.0) / 360.0 for az, _ in dirs]
            if max(us) - min(us) > 0.5:   # (across the seam at the back: all on one side of it, plain bone there)
                us = [0.999 if us[0] > 0.5 else 0.001] * len(us)
            add_face(vs, [in_panel('skull', u, (el + 90.0) / 180.0) for u, (_, el) in zip(us, dirs)], walk.HEAD)
        count += len(f.vertices) - 2
    return count


SKULL_TRIS = skull_part(cranium, lambda p: p)
if jaw is not None:
    SKULL_TRIS += skull_part(jaw, lambda p: hinge_mid + turn @ (p - hinge_mid))
for ob in [skull_ob, jaw_cutter] + cutters:
    bpy.data.objects.remove(ob)

# The spine: a rod on each spine bone, the column moved toward the back, its vertebrae painted by height.
chain = ['spine', 'spine.001', 'spine.002', 'spine.003', 'spine.004', 'spine.005']


def column(z):
    for name in chain:
        a, b = bone_head[name], bone_tail[name]
        if a.z <= z <= b.z or name == chain[-1]:
            p = a.lerp(b, max(0.0, min(1.0, (z - a.z) / (b.z - a.z))))
            break
    if name in ('spine.004', 'spine.005'):
        return name, p
    hit = trunk_tree.ray_cast(Vector((0, 10, z)), Vector((0, -1, 0)), 20.0)
    back = hit[0].y if hit[0] is not None else p.y + 1.0
    return name, Vector((0, p.y + SPINE_BACK * (back - p.y), z))


SPINE_LOW = bone_head['spine'].z + SPINE_FROM
SPINE_HIGH = CENTRE.z - (head_reach(180, -90) or R) * SKULL_SHRINK + 0.2
spine_uv = lambda z: in_panel('spine', 0.5, (z - SPINE_LOW) / (SPINE_HIGH - SPINE_LOW))
for name in chain:
    za = max(SPINE_LOW, bone_head[name].z)
    zb = min(SPINE_HIGH, bone_tail[name].z)
    if zb <= za:
        continue
    _, a = column(za)
    _, b = column(zb)
    r = NECK_R if name in ('spine.004', 'spine.005') else SPINE_R
    lathe(a, b, [(0, 0.0), (0, r * 0.9), (1, r * 0.9), (1, 0.0)], name, sides=SIDES,
          uv_of=lambda t, za=za, zb=zb: spine_uv(za + (zb - za) * t))

# The ribcage band, open at the back for the spine.
z_top, z_bottom = RIB_TOP + BAND_MARGIN, RIB_BOTTOM - RIB_DROP - BAND_MARGIN
band_rows = 5
band_az = list(range(-RIB_BACK, RIB_BACK + 1, BAND_STEP))
if band_az[-1] != RIB_BACK:
    band_az.append(RIB_BACK)
pts, uvs, bones = [], [], []
for j in range(band_rows):
    z = z_top + (z_bottom - z_top) * j / (band_rows - 1)
    row, row_uv = [], []
    for az in band_az:
        reach, c = trunk_reach(az, z)
        row.append(c + way(az) * reach * RIB_IN)
        row_uv.append(in_panel('ribs', (az + RIB_BACK) / (2.0 * RIB_BACK), (z - z_bottom) / (z_top - z_bottom)))
    pts.append(row)
    uvs.append(row_uv)
    bones.append('spine.003' if z >= bone_head['spine.003'].z else 'spine.002')
sheet(pts, uvs, bones)
# (how many units a degree round the chest is, on average, for painting the ribs at their true thickness)
RIB_UNIT = sum(trunk_reach(az, (RIB_TOP + RIB_BOTTOM) / 2)[0] * RIB_IN for az in range(0, 180, 20)) / 9 * math.pi / 180

# The pelvis band, all round.
p_bottom, p_top, p_out, p_cols = PELVIS
pts, uvs = [], []
for j in range(3):
    z = p_bottom + (p_top - p_bottom) * j / 2
    row, row_uv = [], []
    for i in range(p_cols + 1):
        az = -180 + 360.0 * i / p_cols
        if i < p_cols:
            reach, c = trunk_reach(az, z)
            row.append(c + way(az) * reach * p_out)
        row_uv.append(in_panel('pelvis', i / p_cols, j / 2))
    pts.append(row)
    uvs.append(row_uv)
sheet(pts, uvs, ['spine'] * 3, closed=True)
PELVIS_UNIT = sum(trunk_reach(az, (p_bottom + p_top) / 2)[0] * p_out for az in range(0, 360, 30)) / 12 * math.pi / 180

# The clavicles, from the sternum's top out to each shoulder.
reach_top, c_top = trunk_reach(0, STERNUM[0])
st_top = c_top + way(0) * reach_top * RIB_IN
for side, sign in (('L', 1), ('R', -1)):
    shoulder = bone_head['upper_arm.' + side]
    tube([st_top + Vector((sign * 0.35, -0.05, 0.1)), st_top.lerp(shoulder, 0.55) + Vector((0, 0.05, 0.25)),
          shoulder + Vector((-sign * 0.25, -0.15, 0.3))], CLAVICLE_R, 'shoulder.' + side)

# The arms and legs.
radii = {}
for side in ('L', 'R'):
    for bone in ('upper_arm', 'forearm', 'thigh', 'shin'):
        radii[bone + '.' + side] = flesh_reach(bone + '.' + side)
for side in ('L', 'R'):
    for upper, lower in (('upper_arm', 'forearm'), ('thigh', 'shin')):
        un, ln = upper + '.' + side, lower + '.' + side
        ru, rl = min(radii[un] * SHAFT, SHAFT_MOST), min(radii[ln] * SHAFT, SHAFT_MOST)
        joint = min(radii[un] * KNOB, radii[ln] * KNOB, KNOB_MOST)
        rod(bone_head[un], bone_tail[un], ru, un, min(radii[un] * KNOB, KNOB_MOST) * (1.0 if upper == 'upper_arm' else 1.1),
            joint * KNOB_FAR)
        rod(bone_head[ln], bone_tail[ln], rl, ln, joint, min(radii[ln] * KNOB, KNOB_MOST) * 0.8 * KNOB_FAR)

# The hands: a card from the wrist to the fingertips in the hand's plane (across it away from the thumb), its rows at
# the wrist and knuckles on the hand, at the finger joint on its first bone and at the tips on its second; and a strip
# for the thumb. The hand's lengths along the card, for painting (HAND_ROWS).
HAND_ROWS = None
for side in ('L', 'R'):
    hand, f1, f2 = 'hand.' + side, 'hand.' + side + '.001', 'hand.' + side + '.002'
    wrist, knuckle = bone_head[hand], bone_tail[hand]
    along = (knuckle - wrist).normalized()
    across = bone_head['thumb.' + side] - wrist
    across = -(across - along * across.dot(along)).normalized()   # away from the thumb
    hand_verts = [p for p, d in zip(rest, dom) if d.startswith(hand)]
    width = max(p.dot(across) for p in hand_verts) - min(p.dot(across) for p in hand_verts)
    middle = sum((p for p in hand_verts), Vector()) / len(hand_verts)
    off = across * (middle - wrist).dot(across)   # the hand's middle across
    half = across * width * HAND_SPREAD
    spine_pts = [wrist - along * 0.15, knuckle, bone_tail[f1], bone_tail[f2] + (bone_tail[f2] - bone_head[f2]).normalized() * 0.15]
    lengths = [0.0]
    for a, b in zip(spine_pts, spine_pts[1:]):
        lengths.append(lengths[-1] + (b - a).length)
    HAND_ROWS = [x / lengths[-1] for x in lengths]
    pts = [[p + off - half, p + off, p + off + half] for p in spine_pts]
    uvs = [[in_panel('hand', u * 0.75, v) for u in (0.0, 0.5, 1.0)] for v in HAND_ROWS]
    sheet(pts, uvs, [hand, hand, f1, f2])
    t0, t1 = 'thumb.' + side, 'thumb.' + side + '.001'
    t_pts = [wrist + along * 0.15, bone_head[t0], bone_tail[t0], bone_tail[t1] + (bone_tail[t1] - bone_head[t1]).normalized() * 0.12]
    t_half = across * width * 0.16
    sheet([[p - t_half, p + t_half] for p in t_pts],
          [[in_panel('hand', u, v) for u in (0.78, 0.98)] for v in (0.0, 0.3, 0.65, 1.0)], [hand, hand, t0, t1])
    THUMB_ROWS = [0.0, 0.3, 0.65, 1.0]

# The feet: a card along each foot near the floor from behind the heel to past the toes, its rows on the foot but the
# last on the toe.
FOOT_ROWS = None
for side in ('L', 'R'):
    foot, toe = 'foot.' + side, 'toe.' + side
    ankle, ball = bone_head[foot], bone_tail[foot]
    flat = Vector(((ball - ankle).x, (ball - ankle).y, 0)).normalized()
    foot_across = flat.cross(Vector((0, 0, 1))).normalized()
    foot_verts = [p for p, d in zip(rest, dom) if d.startswith((foot, toe))]
    fw = max(p.dot(foot_across) for p in foot_verts) - min(p.dot(foot_across) for p in foot_verts)
    floor = min(p.z for p in foot_verts)
    lift = lambda p, h: Vector((p.x, p.y, floor + h))
    tip = bone_tail[toe] + (bone_tail[toe] - bone_head[toe]).normalized() * 0.1
    spine_pts = [lift(ankle - flat * 0.7, 0.3), lift(ankle, 0.45), lift(ball, 0.3), lift(tip, 0.2)]
    lengths = [0.0]
    for a, b in zip(spine_pts, spine_pts[1:]):
        lengths.append(lengths[-1] + (b - a).length)
    FOOT_ROWS = [x / lengths[-1] for x in lengths]
    half = foot_across * fw * FOOT_SPREAD
    pts = [[p - half, p, p + half] for p in spine_pts]
    # (the art is the right foot's; the left's is the same, mirrored across)
    us = (0.0, 0.5, 1.0) if side == 'R' else (1.0, 0.5, 0.0)
    uvs = [[in_panel('foot', u, v) for u in us] for v in FOOT_ROWS]
    sheet(pts, uvs, [foot, foot, foot, toe])
    FOOT_LONG, FOOT_WIDE = lengths[-1], fw * FOOT_SPREAD * 2

bmesh.ops.recalc_face_normals(bm, faces=[f for f in bm.faces if f not in cut_faces])


# --- the image

def grid(name):
    """Each texel's place 0..1 across and up a panel, and the panel's slice of the image (rows bottom first)."""
    u0, v0, u1, v1 = PANELS[name]
    x0, x1, y0, y1 = int(u0 * IMG), int(u1 * IMG), int(v0 * IMG), int(v1 * IMG)
    U, V = numpy.meshgrid((numpy.arange(x1 - x0) + 0.5) / (x1 - x0), (numpy.arange(y1 - y0) + 0.5) / (y1 - y0))
    return U, V, (slice(y0, y1), slice(x0, x1))


def capsule(X, Y, a, b, r):
    """Distance from each place to a segment from a to b, less r (negative inside)."""
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    t = numpy.clip(((X - ax) * dx + (Y - ay) * dy) / max(dx * dx + dy * dy, 1e-9), 0, 1)
    return numpy.hypot(X - (ax + dx * t), Y - (ay + dy * t)) - r


def ellipse(X, Y, c, ha, hb):
    """Roughly the distance outside an ellipse (in the units of ha)."""
    return (numpy.hypot((X - c[0]) / ha, (Y - c[1]) / hb) - 1.0) * min(ha, hb)


def bone_art(sdf, holes=None, hollows=None):
    """The bone's colours and alpha from a shape's distance (units, negative inside): the outline's ink round it,
    shaded toward its edge, solid inside; holes (a distance) are dark sockets cut into it."""
    bone = numpy.array(COLOURS['bone'])
    shade = numpy.array(COLOURS['shade'])
    line = numpy.array(COLOURS['line'])
    inner = numpy.clip(-sdf / 0.12, 0, 1)[..., None]
    rgb = shade * (1 - inner) + bone * inner
    rgb = numpy.where((sdf >= 0)[..., None], line, rgb)
    if holes is not None:
        rgb = numpy.where((holes < 0)[..., None], numpy.array(COLOURS['dark']), rgb)
        rgb = numpy.where(((holes >= 0) & (holes < OUTLINE * 0.6))[..., None], line, rgb)
    if hollows is not None:   # shallow ones (a hip socket), in the shade
        rgb = numpy.where((hollows < 0)[..., None], shade * 0.8, rgb)
        rgb = numpy.where(((hollows >= 0) & (hollows < OUTLINE * 0.5))[..., None], line, rgb)
    alpha = (sdf < OUTLINE).astype(float)
    return numpy.concatenate([rgb, alpha[..., None]], axis=2)


def paint_ribs(img):
    """The ribs and sternum on the band, by azimuth (degrees) and height (units)."""
    U, V, at = grid('ribs')
    az = U * 2 * RIB_BACK - RIB_BACK
    z = z_bottom + V * (z_top - z_bottom)
    X, Y = az * RIB_UNIT, z
    sdf = numpy.full(az.shape, 9.0)
    for k in range(RIBS):
        zb = RIB_TOP + (RIB_BOTTOM - RIB_TOP) * k / (RIBS - 1)
        end = RIB_ENDS[k]
        for sign in (1, -1):
            prev = None
            for s in numpy.linspace(0, 1, 14):
                a = 165 + (end - 165) * s
                p = (sign * a * RIB_UNIT, zb - RIB_DROP * s ** 1.3)
                if prev is not None:
                    half = RIB_HALF * (1.0 - 0.3 * s) * (0.85 if k == RIBS - 1 else 1.0)
                    sdf = numpy.minimum(sdf, capsule(X, Y, prev, p, half))
                prev = p
            # (to the spine at the back)
            sdf = numpy.minimum(sdf, capsule(X, Y, (sign * 165 * RIB_UNIT, zb), (sign * RIB_BACK * RIB_UNIT * 1.02, zb + 0.05), RIB_HALF))
    sdf = numpy.minimum(sdf, capsule(X, Y, (0, STERNUM[0]), (0, STERNUM[1]), STERNUM[2]))
    img[at] = bone_art(sdf)


def paint_spine(img):
    """Round each spine rod: a vertebra every VERTEBRA_STEP with a dark line between, fully opaque."""
    U, V, at = grid('spine')
    z = SPINE_LOW + V * (SPINE_HIGH - SPINE_LOW)
    phase = ((z - SPINE_LOW) / VERTEBRA_STEP) % 1.0
    edge = numpy.minimum(phase, 1 - phase) * VERTEBRA_STEP     # units from the line between two vertebrae
    sdf = OUTLINE * 0.6 - edge   # a thin line at each joint
    rgb = numpy.where((edge < OUTLINE * 0.5)[..., None], numpy.array(COLOURS['line']),
                      numpy.array(COLOURS['shade']) + (numpy.array(COLOURS['bone']) - numpy.array(COLOURS['shade']))
                      * numpy.clip(edge / 0.15, 0, 1)[..., None])
    img[at] = numpy.concatenate([rgb, numpy.ones(rgb.shape[:2] + (1,))], axis=2)


def paint_pelvis(img):
    """The pelvis round the band, by azimuth (degrees, 0 the front) and height: an iliac wing up either side, the sacrum at
    the back, the pubic arch at the front with a hole either side, a dark hip socket at each hip."""
    U, V, at = grid('pelvis')
    az = U * 360 - 180
    z = p_bottom + V * (p_top - p_bottom)
    X, Y = az * PELVIS_UNIT, z
    k = PELVIS_UNIT
    sdf = numpy.full(az.shape, 9.0)
    holes = numpy.full(az.shape, 9.0)
    hollows = numpy.full(az.shape, 9.0)
    for sign in (1, -1):
        sdf = numpy.minimum(sdf, ellipse(X, Y, (sign * 100 * k, 13.75), 55 * k, 0.75))    # the wing
        sdf = numpy.minimum(sdf, capsule(X, Y, (sign * 60 * k, 13.1), (sign * 12 * k, 12.15), 0.22))   # the pubic ramus
        sdf = numpy.minimum(sdf, capsule(X, Y, (sign * 78 * k, 12.85), (sign * 40 * k, 12.75), 0.3))
        sdf = numpy.minimum(sdf, capsule(X, Y, (sign * 140 * k, 13.4), (sign * 168 * k, 13.0), 0.35))   # the back of the wing to the sacrum
        hollows = numpy.minimum(hollows, ellipse(X, Y, (sign * 72 * k, 12.8), 9 * k, 0.24))    # the hip socket
        holes = numpy.minimum(holes, ellipse(X, Y, (sign * 34 * k, 12.55), 9 * k, 0.17))   # the obturator hole
    # The sacrum, a wedge down the back.
    back = (180 - numpy.abs(az)) * k   # units from the back's middle
    wedge = numpy.maximum(back - 0.55 * numpy.clip((z - 12.2) / 1.8, 0.15, 1.0), numpy.maximum(12.1 - z, z - 14.1))
    sdf = numpy.minimum(sdf, wedge)
    img[at] = bone_art(sdf, holes, hollows)


def paint_hand(img):
    """A right hand's bones on its card (across 0..0.75 from the thumb's side, up from the wrist), mirrored by the card
    for the left: carpals at the wrist, four metacarpals to the knuckles, each finger's two segments; and the thumb's."""
    U, V, at = grid('hand')
    X, Y = U, V
    rows = HAND_ROWS
    sdf = numpy.full(U.shape, 9.0)
    w = 0.75   # the hand's art across
    sdf = numpy.minimum(sdf, ellipse(X, Y, (0.37, rows[0] + 0.07), 0.2, 0.06))   # the carpals
    lengths = [0.92, 1.0, 0.94, 0.8]   # each finger's share of the card's finger length
    for n in range(4):
        x_k = 0.12 + 0.17 * n   # across the art, the index finger on the thumb's side
        x_w = 0.28 + 0.06 * n
        r = 0.032
        sdf = numpy.minimum(sdf, capsule(X, Y, (x_w, rows[0] + 0.1), (x_k, rows[1]), r))
        mid = rows[1] + (rows[2] - rows[1]) * lengths[n]
        tip = rows[1] + (rows[3] - rows[1]) * lengths[n] - 0.02
        sdf = numpy.minimum(sdf, capsule(X, Y, (x_k, rows[1] + 0.025), (x_k, mid - 0.012), r * 0.95))
        sdf = numpy.minimum(sdf, capsule(X, Y, (x_k, mid + 0.012), (x_k, tip), r * 0.85))
        sdf = numpy.minimum(sdf, ellipse(X, Y, (x_k, rows[1]), 0.045, 0.03))   # the knuckle
    # The thumb's strip, across 0.78..0.98: one metacarpal and two segments.
    xt = 0.88
    sdf = numpy.minimum(sdf, capsule(X, Y, (xt, 0.05), (xt, 0.29), 0.035))
    sdf = numpy.minimum(sdf, capsule(X, Y, (xt, 0.33), (xt, 0.62), 0.033))
    sdf = numpy.minimum(sdf, capsule(X, Y, (xt, 0.67), (xt, 0.93), 0.03))
    # (the panel is 0..1 square, the card's units unequal across and along: the outline is narrower than OUTLINE)
    art = bone_art(sdf * 3.0)
    img[at] = art


def paint_foot(img):
    """A right foot's bones on its card (across 0..1, along from behind the heel): the heel, the ankle's talus, three
    metatarsals and three toes, the big one on the inside (across 0)."""
    U, V, at = grid('foot')
    X, Y = U, V
    rows = FOOT_ROWS
    sdf = numpy.full(U.shape, 9.0)
    sdf = numpy.minimum(sdf, ellipse(X, Y, (0.5, rows[0] + 0.12), 0.3, 0.12))    # the heel
    sdf = numpy.minimum(sdf, ellipse(X, Y, (0.5, rows[1]), 0.26, 0.08))          # the talus
    for n, x in enumerate((0.25, 0.5, 0.75)):
        r = 0.06 if n == 0 else 0.05
        sdf = numpy.minimum(sdf, capsule(X, Y, (0.5 + (x - 0.5) * 0.5, rows[1] + 0.06), (x, rows[2]), r))
        end = rows[3] - 0.03 - 0.04 * n
        sdf = numpy.minimum(sdf, capsule(X, Y, (x, rows[2] + 0.04), (x, end), r * 0.9))
        sdf = numpy.minimum(sdf, ellipse(X, Y, (x, rows[2]), r * 1.3, 0.035))
    img[at] = bone_art(sdf * 1.6)


def paint_skull(img):
    """The skull's colours by azimuth and elevation: bone, mottled with age; the sockets and the nose dark, shading in at
    their rims; the teeth; the sutures over the crown."""
    rnd = numpy.random.default_rng(7)
    U, V, at = grid('skull')
    az = U * 360.0 - 180.0
    el = V * 180.0 - 90.0
    H, W = U.shape
    bone = numpy.array(COLOURS['bone'])

    def smooth_noise(cells):
        g = rnd.random((cells + 1, cells * 2 + 1))
        gy = numpy.clip(V * cells, 0, cells - 1e-6)
        gx = U * cells * 2
        y0, x0 = gy.astype(int), gx.astype(int) % (cells * 2)
        ty, tx = gy - y0, gx - numpy.floor(gx)
        ty, tx = ty * ty * (3 - 2 * ty), tx * tx * (3 - 2 * tx)
        x1 = (x0 + 1) % (cells * 2)
        a = g[y0, x0] * (1 - tx) + g[y0, x1] * tx
        b = g[y0 + 1, x0] * (1 - tx) + g[y0 + 1, x1] * tx
        return a * (1 - ty) + b * ty
    mott = 0.6 * smooth_noise(6) + 0.3 * smooth_noise(14) + 0.1 * smooth_noise(40)
    out = bone[None, None, :] * (1.0 + (mott - 0.5) * 0.12)[:, :, None]
    dark = numpy.array(COLOURS['dark'])
    rim = numpy.array(COLOURS['shade'])

    def ellipse_d(a0, e0, ha, he, mirror=True):
        best = numpy.full(az.shape, 9.0)
        for sign in ((1, -1) if mirror and a0 else (1,)):
            da = ((az - sign * a0 + 180) % 360 - 180) / ha
            de = (el - e0) / he
            best = numpy.minimum(best, numpy.sqrt(da * da + de * de))
        return best

    def lay(mask, colour):
        nonlocal out
        m = numpy.clip(mask, 0, 1)[:, :, None]
        out = out * (1 - m) + numpy.asarray(colour)[None, None, :] * m

    a0, e0, ha, he = SOCKET
    d = ellipse_d(a0, e0 + 1, ha * 0.92, he * 0.95)
    lay(numpy.clip((1.35 - d) / 0.35, 0, 1) * 0.55, rim)
    lay(numpy.clip((1.0 - d) / 0.08, 0, 1), dark)
    na, ne, nh, nv = NOSE
    lobes = numpy.minimum(ellipse_d(nh * 0.45, ne + nv * 0.25, nh * 0.6, nv * 0.6, True),
                          ellipse_d(0, ne - nv * 0.2, nh * 0.55, nv * 0.8, False))
    point = (numpy.abs(az) / nh) + (el - (ne + nv * 0.1)) / (-nv)
    nose = numpy.minimum(lobes, numpy.where(el < ne + nv * 0.1, point, 9.0))
    lay(numpy.clip((1.4 - nose) / 0.4, 0, 1) * 0.5, rim)
    lay(numpy.clip((1.0 - nose) / 0.1, 0, 1), dark)
    top, bot, half, each = TEETH
    in_band = (el <= top) & (el >= bot) & (numpy.abs(az) <= half)
    edge = numpy.clip(numpy.minimum(numpy.minimum(top - el, el - bot), half - numpy.abs(az)) / 1.2, 0, 1)
    lay(in_band * edge * 0.25, (0.97, 0.94, 0.84))
    tooth = half / each
    phase = numpy.abs(((az + tooth / 2) / tooth) % 1.0 - 0.5) * tooth
    width = 0.55 * (1 + 0.6 * (numpy.abs(az) / half))
    lay(in_band * numpy.clip((width - phase) / 0.4, 0, 1) * edge, (0.30, 0.22, 0.17))
    lay((numpy.abs(az) <= half + 1) * numpy.clip((0.9 - numpy.abs(el - JAW_LINE)) / 0.5, 0, 1), (0.18, 0.13, 0.10))
    outline = (numpy.abs(az) <= half + 1.5) & (el <= top + 1.2) & (el >= bot - 1.2) & ~in_band
    lay(outline * 0.75, (0.35, 0.27, 0.21))
    dx = numpy.cos(numpy.radians(el)) * numpy.sin(numpy.radians(az))
    dy = -numpy.cos(numpy.radians(el)) * numpy.cos(numpy.radians(az))
    dz = numpy.sin(numpy.radians(el))
    wav = 0.018 * numpy.sin(numpy.radians(az) * 40 + numpy.radians(el) * 30)
    line = numpy.zeros(az.shape)
    line = numpy.maximum(line, (numpy.abs(dy + 0.12 + wav) < 0.012) & (dz > 0.25))
    line = numpy.maximum(line, (numpy.abs(dx + wav * 0.8) < 0.012) & (dy > -0.12) & (dz > 0.55))
    lam = dz - (0.55 - 0.9 * numpy.maximum(0, dy - 0.3))
    line = numpy.maximum(line, (numpy.abs(lam + wav) < 0.014) & (dy > 0.3) & (dz > 0.0) & (dz < 0.58))
    lay(line * 0.6, (0.52, 0.45, 0.36))
    img[at] = numpy.concatenate([numpy.clip(out, 0, 1), numpy.ones((H, W, 1))], axis=2)


pixels = numpy.zeros((IMG, IMG, 4))
paint_skull(pixels)
paint_ribs(pixels)
paint_spine(pixels)
paint_pelvis(pixels)
paint_hand(pixels)
paint_foot(pixels)
_, _, at = grid('swatch')
pixels[at] = list(COLOURS['bone']) + [1.0]
pixels[at[0], int(0.875 * IMG):] = list(COLOURS['dark']) + [1.0]

image = bpy.data.images.new(NAME, IMG, IMG, alpha=True)
image.pixels.foreach_set(pixels.astype(numpy.float32).ravel())
image.filepath_raw = os.path.splitext(out_glb)[0] + '.png'
image.file_format = 'PNG'
image.pack()

mesh = bpy.data.meshes.new(NAME)
bm.to_mesh(mesh)
index = {v: v.index for v in bm.verts}
bone_of = {index[v]: b for v, b in weights.items() if v.is_valid}
bm.free()

for mat_name in (NAME, UNLINED):
    mat = bpy.data.materials.new(mat_name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes['Principled BSDF']
    tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
    tex.image = image
    tex.interpolation = 'Closest' if mat_name == UNLINED else 'Linear'
    mat.node_tree.links.new(tex.outputs['Color'], bsdf.inputs['Base Color'])
    mat.node_tree.links.new(tex.outputs['Alpha'], bsdf.inputs['Alpha'])   # (so the .glb keeps the image's alpha)
    mesh.materials.append(mat)

o = bpy.data.objects.new(NAME, mesh)
body.users_collection[0].objects.link(o)
o.parent = rig
o.matrix_parent_inverse = rig.matrix_world.inverted()
o.modifiers.new('Armature', 'ARMATURE').object = rig
missing = [i for i in range(len(mesh.vertices)) if i not in bone_of]
if missing:
    sys.exit('make_skeleton.py: %d vertices with no bone' % len(missing))
for vi, b in bone_of.items():
    if b not in deform:
        sys.exit('make_skeleton.py: no deform bone %s' % b)
    (o.vertex_groups.get(b) or o.vertex_groups.new(name=b)).add([vi], 1.0, 'REPLACE')

per = {}
for poly in mesh.polygons:
    b = bone_of[poly.vertices[0]]
    b = b.split('.')[0] if not b.startswith('spine') else b
    b = ('cut ' if poly.material_index == 1 else '') + b
    per[b] = per.get(b, 0) + len(poly.vertices) - 2
print('SKELETON: skull %d triangles' % SKULL_TRIS)
print('SKELETON: %d vertices, %d triangles (%d cut out), head radius %.2f, limb flesh %s' % (
    len(mesh.vertices), sum(len(p.vertices) - 2 for p in mesh.polygons),
    sum(len(p.vertices) - 2 for p in mesh.polygons if p.material_index == 1), R,
    ', '.join('%s %.2f' % (k, v) for k, v in sorted(radii.items()) if k.endswith('.L'))))
print('SKELETON: triangles by bone: %s' % ', '.join('%s %d' % kv for kv in sorted(per.items(), key=lambda kv: -kv[1])))

bpy.ops.object.select_all(action='DESELECT')
o.select_set(True)
rig.hide_set(False)
rig.hide_viewport = False
rig.select_set(True)
bpy.context.view_layer.objects.active = o
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False, export_extras=True)
print('SKELETON -> %s' % out_glb)

if preview:
    # Workbench renders of the skeleton alone, its texture shown (cut out by its alpha), from four sides.
    for ob in bpy.context.scene.objects:
        ob.hide_render = ob is not o
    for mat in mesh.materials:
        mat.blend_method = 'CLIP' if hasattr(mat, 'blend_method') else None
        if hasattr(mat, 'surface_render_method'):
            mat.surface_render_method = 'DITHERED'
    scene = bpy.context.scene
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'TEXTURE'
    scene.render.resolution_x, scene.render.resolution_y = 700, 900
    cam_data = bpy.data.cameras.new('cam')
    cam_data.type = 'ORTHO'
    cam_data.ortho_scale = 32
    cam = bpy.data.objects.new('cam', cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    shots = []
    for tag, az, scale, at in (('front', 0, 32, 15), ('three', 35, 32, 15), ('side', 90, 32, 15), ('back', 150, 32, 15),
                               ('skull', 25, 12, CENTRE.z - 1), ('skull_side', 70, 12, CENTRE.z - 1), ('skull_below', 15, 12, CENTRE.z - 1)):
        cam_data.ortho_scale = scale
        d = way(az, 8 if tag != 'skull_below' else -25)
        cam.location = Vector((0, 0, at)) + d * 60
        cam.rotation_euler = (-d).to_track_quat('-Z', 'Y').to_euler()
        path = os.path.splitext(preview)[0] + '_' + tag + '.png'
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        shots.append(path)
    pixels_out = os.path.splitext(preview)[0] + '_image.png'
    image.save(filepath=pixels_out)
    print('PREVIEW', ' '.join(shots), pixels_out)

if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
