"""Makes the locust armour (Royalty's Apparel_ArmorLocust, recon armour with a jump pack built in) from the recon armour:
Models/garments/ArmorRecon.glb as it is, its texture too, with a rocket booster on the outside of each boot, as the back
is wanted for packs, holstered weapons and worn gear.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/make_locust.py -- <out.glb> [out.blend]

Written to Models/garments/ArmorLocust.glb it is the garment ArmorLocust, drawn for Apparel_ArmorLocust (export_frames.py's
default_defs). Re-run it after the recon armour changes.

Each booster is a round pod (SEGMENTS round, its outline PROFILE from the nose down) along the shin, its line the shin
bone's moved back BACK and out until its inner side is SINK into the armour's outside along its whole length (so it is
bolted on, not floating), from TOP to BOTTOM of the way down the shin, with a bell nozzle at its foot and a dark ring
round its middle; a strap (STRAP) round the boot holds each one on. Their texture places are on two plain patches of the
armour's own image, the lightest (the plates) and the darkest (the joints), so they match it.

The pods are weighted to their shin bone whole (shin.<side>, written as the file's skin), so they stay straight as the
knee and ankle bend; export_frames.py's "part_weights" keeps those and gives the armour's own vertices, which have none,
the body's as on any garment. Sizes are in Blender units at the rig's scale (one cell is about 17.6). The .blend is never
saved; the optional out.blend is a copy with the armour on the rig, to look at."""
import bpy, bmesh, sys, math, os
from mathutils import Vector
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    sys.exit('make_locust.py: give an out.glb')
out_glb = os.path.abspath(argv[0])
out_blend = os.path.abspath(argv[1]) if len(argv) > 1 else None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE = os.path.join(ROOT, 'Models', 'garments', 'ArmorRecon.glb')
NAME = 'ArmorLocust'

TOP = 0.14          # the pod's nose, a share of the way down the shin bone from the knee
BOTTOM = 0.80       # its nozzle's mouth
BACK = 0.35         # its line this far behind the shin bone's (y is back), so it sits on the side toward the calf
OUT_TILT = 0.0      # radians: its line leant out at the foot
SINK = 0.12         # how far its inner side goes into the armour's outside
SEGMENTS = 10       # points round
# The outline from the nose down: (share of the pod's length, radius, dark). Radii in Blender units.
PROFILE = [
    (0.00, 0.00, False),
    (0.04, 0.34, False),
    (0.11, 0.55, False),
    (0.20, 0.64, False),
    (0.58, 0.64, False),
    (0.59, 0.69, True),    # the dark ring round its middle
    (0.66, 0.69, True),
    (0.67, 0.64, False),
    (0.82, 0.62, False),
    (0.84, 0.40, True),    # the throat
    (0.90, 0.46, True),
    (1.00, 0.62, True),    # the bell's mouth
    (0.97, 0.30, True),    # the bell's inside, back up to its dark bottom
    (0.93, 0.00, True),
]
STRAP = (0.62, 0.70)  # the strap round the boot, between these shares of the pod's length
STRAP_OUT = 0.16      # its height off the armour
STRAP_WIDE = 1.15     # its half width across the leg, a share of the boot's half width there
STRAP_POINTS = 20

PATCH = 24            # pixels: the squares of the image the pods take their colours from


def even_patches(img):
    """The middles of the lightest and darkest even PATCH-pixel squares of the image, as texture places."""
    w, h = img.size
    px = list(img.pixels[:])
    light, dark = (None, (0.5, 0.5)), (None, (0.5, 0.5))
    for y0 in range(0, h - PATCH, PATCH):
        for x0 in range(0, w - PATCH, PATCH):
            vals = []
            for y in range(y0, y0 + PATCH, 3):
                row = (y * w) * 4
                for x in range(x0, x0 + PATCH, 3):
                    k = row + x * 4
                    vals.append((px[k] + px[k + 1] + px[k + 2]) / 3 if px[k + 3] > 0.5 else -1)
            if min(vals) < 0:
                continue
            mean = sum(vals) / len(vals)
            spread = max(vals) - min(vals)
            at = ((x0 + PATCH / 2) / w, (y0 + PATCH / 2) / h)
            if light[0] is None or mean - 3 * spread > light[0]:
                light = (mean - 3 * spread, at)
            # Dark but not black (an unpainted corner of the atlas is black, and so even).
            if mean > 0.12 and (dark[0] is None or -mean - 3 * spread > dark[0]):
                dark = (-mean - 3 * spread, at)
    return Vector(light[1]), Vector(dark[1])


# The recon armour, as the garment pipeline has it.
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=SOURCE, disable_bone_shape=True)
new = [o for o in bpy.data.objects if o not in before]
armour = next(o for o in new if o.type == 'MESH')
for o in new:
    if o is not armour:
        bpy.data.objects.remove(o)
old = bpy.data.objects.get(NAME)
if old is not None:
    bpy.data.objects.remove(old)
armour.name = NAME
armour.data.name = NAME
if armour.data.shape_keys:
    armour.shape_key_clear()
armour.vertex_groups.clear()

body = bpy.data.objects['Body']
rig = body.find_armature()
mw = armour.matrix_world.copy()

welded = bmesh.new()
welded.from_mesh(armour.data)
welded.transform(mw)
bmesh.ops.remove_doubles(welded, verts=welded.verts, dist=1e-4)
tree = BVHTree.FromBMesh(welded)

def base_colour_image(materials):
    """The image feeding a material's Base Color (through any nodes between), or None: not its normal or other maps."""
    def upstream(socket, seen):
        for link in socket.links:
            n = link.from_node
            if n in seen:
                continue
            seen.add(n)
            if n.type == 'TEX_IMAGE' and n.image:
                return n.image
            for s in n.inputs:
                found = upstream(s, seen)
                if found:
                    return found
        return None
    for m in materials:
        if m and m.node_tree:
            for n in m.node_tree.nodes:
                if n.type == 'BSDF_PRINCIPLED':
                    found = upstream(n.inputs['Base Color'], set())
                    if found:
                        return found
    return None


img = base_colour_image(armour.data.materials)
light_uv, dark_uv = even_patches(img) if img else (Vector((0.5, 0.5)), Vector((0.5, 0.5)))

bm = bmesh.new()
uv_layer = bm.loops.layers.uv.new('UVMap')
side_of = {}   # each new vertex's side
strap_faces = set()


def surface_out(p, out):
    """How far out from p along out the armour's outside is (looking in from well outside)."""
    hit = tree.ray_cast(p + out * 30, -out, 60)[0]
    return (hit - p).dot(out) if hit is not None else 0.0


def leg_out(p, way, most=3.0):
    """How far out from p, inside the leg, along way its armour's outside is: the last surface met within most (so not
    the other leg, which looking in from far outside would meet first)."""
    reach, start = 0.0, p
    while True:
        hit = tree.ray_cast(start, way, most - reach)[0]
        if hit is None:
            return reach
        reach = (hit - p).dot(way)
        start = hit + way * 1e-3


def ring_faces(rings, dark_of, side):
    """Quads between consecutive rings of SEGMENTS points; dark_of(k) whether band k is dark. The rings run from the
    nose down, so the faces are wound to face out."""
    verts = [[bm.verts.new(p) for p in ring] for ring in rings]
    for row in verts:
        for v in row:
            side_of[v] = side
    for k in range(len(verts) - 1):
        a, b = verts[k], verts[k + 1]
        for i in range(SEGMENTS):
            j = (i + 1) % SEGMENTS
            quad = [a[i], b[i], b[j], a[j]]
            f = bm.faces.new(quad)
            f.smooth = True
            for l in f.loops:
                l[uv_layer].uv = dark_uv if dark_of(k) else light_uv


for side, sign in (('L', 1), ('R', -1)):
    head = rig.matrix_world @ rig.data.bones['shin.' + side].head_local
    tail = rig.matrix_world @ rig.data.bones['shin.' + side].tail_local
    back = Vector((0, BACK, 0))
    top = head + (tail - head) * TOP + back
    bottom = head + (tail - head) * BOTTOM + back
    axis = (bottom - top).normalized()
    out = Vector((sign, 0, 0))
    out = (out - axis * out.dot(axis)).normalized()
    side_way = axis.cross(out).normalized()   # across: front or back, by side
    radius = max(r for _, r, _ in PROFILE)
    # Out until the inner side, at every height, is SINK into the armour's outside (looking at its middle and its edges).
    need = 0.0
    for k in range(13):
        p = top + (bottom - top) * (k / 12)
        for s in (-0.7, 0.0, 0.7):
            q = p + side_way * s * radius
            need = max(need, surface_out(q, out) + radius * math.sqrt(1 - s * s) - SINK)
    top = top + out * need
    bottom = bottom + out * need + out * OUT_TILT * (bottom - top).length
    axis = (bottom - top).normalized()
    length = (bottom - top).length
    u = out
    v = axis.cross(u).normalized()
    rings = []
    for share, r, _ in PROFILE:
        c = top + axis * length * share
        rings.append([c + (u * math.cos(2 * math.pi * i / SEGMENTS) + v * math.sin(2 * math.pi * i / SEGMENTS)) * r
                      for i in range(SEGMENTS)])
    # The nose and the bell's bottom are single points: rings of one place, welded below.
    ring_faces(rings, lambda k: PROFILE[k][2] and PROFILE[k + 1][2], side)

    # The strap round the boot, from the pod's front edge round the leg to its back edge: a band laid STRAP_OUT off the
    # armour, found by looking in at the leg's middle line from each way round.
    leg_centre = lambda p: p - out * need - back   # the shin's own line at that height
    strap = []
    for share in STRAP:
        c = top + axis * length * share
        mid = leg_centre(c)
        row = []
        for i in range(STRAP_POINTS + 1):
            a = math.pi * 0.25 + 1.5 * math.pi * i / STRAP_POINTS   # round the inside of the leg, away from the pod
            way = out * math.cos(a) + v * math.sin(a)
            reach = leg_out(mid, way)
            row.append(mid + way * (max(reach, 0.5) + STRAP_OUT))
        strap.append(row)
    sv = [[bm.verts.new(p) for p in row] for row in strap]
    for row in sv:
        for x in row:
            side_of[x] = side
    for i in range(STRAP_POINTS):
        quad = [sv[0][i], sv[0][i + 1], sv[1][i + 1], sv[1][i]]
        f = bm.faces.new(quad)
        f.smooth = True
        f.normal_update()
        mid = sum((x.co for x in quad), Vector()) / 4
        if f.normal.dot(mid - leg_centre(mid)) < 0:
            f.normal_flip()
        for l in f.loops:
            l[uv_layer].uv = dark_uv
        strap_faces.add(f)
    print('LOCUST pod %s: %.2f long, %.2f out from the shin bone, nose at z %.2f, mouth at z %.2f'
          % (side, length, need, top.z, bottom.z))

# The nose and the bell's bottom: each ring of one place welded to a point (its degenerate quads go with it).
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
bmesh.ops.dissolve_degenerate(bm, edges=bm.edges, dist=1e-6)
# Each pod is closed (a point at the nose and at the bell's bottom), so its faces can be turned out from its inside; the
# straps were turned out from the leg as they were made.
bmesh.ops.recalc_face_normals(bm, faces=[f for f in bm.faces if f not in strap_faces])

# Into the armour's own mesh, in its object space, on its material; the pods' vertices weighted to their shin whole.
bm.transform(mw.inverted())
whole = bmesh.new()
whole.from_mesh(armour.data)
uv_armour = whole.loops.layers.uv.active or whole.loops.layers.uv.new('UVMap')
deform = whole.verts.layers.deform.verify()
groups = {side: armour.vertex_groups.new(name='shin.' + side).index for side in ('L', 'R')}
mapping = {}
for vtx in bm.verts:
    nv = whole.verts.new(vtx.co)
    nv[deform][groups[side_of[vtx]]] = 1.0
    mapping[vtx] = nv
added = 0
for f in bm.faces:
    nf = whole.faces.new([mapping[x] for x in f.verts])
    nf.smooth = True
    nf.material_index = 0
    for l, ol in zip(nf.loops, f.loops):
        l[uv_armour].uv = ol[uv_layer].uv
    added += 1
whole.to_mesh(armour.data)
whole.free()
bm.free()
welded.free()

print('LOCUST: %d vertices, %d faces (%d of them boosters), plates from the image at %.3f %.3f, joints at %.3f %.3f'
      % (len(armour.data.vertices), len(armour.data.polygons), added, light_uv.x, light_uv.y, dark_uv.x, dark_uv.y))

# On the rig, skinned, so the pods' weights go into the file.
world = armour.matrix_world.copy()
armour.parent = rig
armour.matrix_world = world
armour.modifiers.new('Armature', 'ARMATURE').object = rig

bpy.ops.object.select_all(action='DESELECT')
rig.hide_set(False)
rig.hide_viewport = False
armour.select_set(True)
rig.select_set(True)
bpy.context.view_layer.objects.active = armour
os.makedirs(os.path.dirname(out_glb), exist_ok=True)
bpy.ops.export_scene.gltf(filepath=out_glb, use_selection=True, export_format='GLB', export_skins=True,
                          export_animations=False, export_morph=False, export_extras=True)
print('LOCUST -> %s' % out_glb)

# Read back: the pods must still be on their shins.
before = set(bpy.data.objects)
bpy.ops.import_scene.gltf(filepath=out_glb, disable_bone_shape=True)
back = next(o for o in bpy.data.objects if o not in before and o.type == 'MESH')
weighted = sum(1 for v in back.data.vertices
               if any(back.vertex_groups[x.group].name.startswith('shin.') and x.weight > 0.99 for x in v.groups))
print('LOCUST read back: %d vertices, %d on a shin whole (%d expected)' % (len(back.data.vertices), weighted, len(mapping)))
for o in [o for o in bpy.data.objects if o not in before]:
    bpy.data.objects.remove(o)
if out_blend:
    bpy.ops.wm.save_as_mainfile(filepath=out_blend, copy=True)
