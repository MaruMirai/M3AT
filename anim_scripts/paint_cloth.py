"""Gives the plain cloth garments (the skirts and dresses, the hosiery, the footwear, the gloves, the leotard, the chest
wrap, the burka) UVs of their own and a grey fabric texture, as they had none: drawn flat in the apparel's colour they
read as plastic, and with no texture places of their own the game can't lay another mod's art on them either
(Source/SpriteClothes.cs projects an apparel's own sprites into these UVs and multiplies this texture's detail back in).

    blender -b Models/pawnHumanRig.blend --python anim_scripts/paint_cloth.py -- <garment.glb> [...] [--size 512]
        [--keep folder] [--preview folder] [--kind kind]

Each .glb is rewritten in place (a copy first into --keep, Models/unused/ by default, as <name>_beforePaint.glb, unless
one is there already): welded, unwrapped by Smart UV Project (its old UVs were the body's atlas, using a few per cent of
it, or both halves of a skirt on one), and painted. Every texel is drawn by its rest place on the garment, as
paint_long_johns.py and paint_backpack.py paint theirs, by the garment's kind (KINDS, by its name): a fine weave, soft
folds hanging from the waist, stitched hems and bands along its openings (found as the open edge loops of the welded
mesh, by distance from them), soles, laces and toe caps on the footwear, knitted courses on the hosiery. All greys near
white (BASE), but the soles and laces, as the game multiplies the image by the apparel's colour. Weights, custom
properties (own_weights) and any shape keys are kept: a skinned file is written skinned again. Lengths are in leg
lengths of the rig (the thigh's head to the floor). make_skirts.sh runs it after making the skirts and dresses; run it
again after make_from_groups.py remakes the shoes, socks, boots, sandals or gloves, or make_underwear.py the hosiery."""
import bpy, bmesh, sys, os, math, shutil
import numpy as np

argv = sys.argv[sys.argv.index('--') + 1:]
SIZE = 512
keep_dir = 'Models/unused'
preview_dir = None
force_kind = None   # --kind <kind>: paint every garment given as that kind (KINDS' second words), whatever its name; with a
                    # Meshy-textured garment remade from the body's faces (its UVs the body's atlas, a few per cent of the
                    # image), it gives the new mesh an unwrap of its own for carry_texture.py to paint, the cloth image
                    # then replaced (2026-11-27: the shirts, jackets, vests, Corset, KidRomper and TribalA)
files = []
i = 0
while i < len(argv):
    if argv[i] == '--size':
        SIZE = int(argv[i + 1]); i += 2
    elif argv[i] == '--keep':
        keep_dir = argv[i + 1]; i += 2
    elif argv[i] == '--kind':
        force_kind = argv[i + 1]; i += 2
    elif argv[i] == '--preview':
        preview_dir = argv[i + 1]; i += 2
    else:
        files.append(argv[i]); i += 1

BASE = 0.92          # the cloth's grey
WEAVE = 0.022        # the fine weave's strength, and its period along and across (leg lengths)
WEAVE_STEP = 0.006
FOLDS = 11           # soft folds round a skirt
FOLD = 0.16          # their shading at the hem (none at the waist)
STITCH = 0.0035      # a line of stitching's half width
STITCH_DASH = 0.012  # and its dashes' length
STITCH_SHADE = 0.6
HEM = 0.035          # a hem's depth up from the bottom edge, stitched along its top
BAND = 0.03          # a waistband's or binding's depth in from its opening
BAND_SHADE = 0.96
KNIT = 0.03          # hosiery's knitted courses round the leg, and their spacing
KNIT_STEP = 0.0045
WELT = 0.05          # a stocking's or tights' welt (a doubled band at the top), deeper and darker
WELT_SHADE = 0.86
REINFORCED = 0.88    # the toe and heel of hosiery and socks
RIB_COUNT = 18       # a sock's ribs round the leg
CUFF = 0.07
SOLE = 0.022         # a shoe's sole, up from the floor (leg lengths)
SOLE_SHADE = 0.32
WELT_LINE = 0.012    # the stitched line above it
LACE_SHADE = 0.55
SEED = 7

rig = bpy.data.objects['metarig']
rig.data.pose_position = 'REST'
bpy.context.view_layer.update()
bones = rig.data.bones
def head(b):
    return np.array(tuple(rig.matrix_world @ bones[b].head_local))
def tail(b):
    return np.array(tuple(rig.matrix_world @ bones[b].tail_local))
LEG = head('thigh.L')[2]   # the floor is z 0
# A dress's waist: the skirt masters' waist ring, where the skirt joins it (15 rig units over the body's lowest point,
# the ankle joint being 1.025 over it), as Source/SpriteClothes.cs's RigWaist lays an apparel's waist there.
WAIST_Z = head('foot.L')[2] - 1.025 + 15.0
ANKLE_Z = head('foot.L')[2]

# The kinds: the garment names that start with each, longest first.
KINDS = [('SleevelessDress', 'dress'), ('Dress', 'dress'), ('Skirt', 'skirt'), ('Burka', 'robe'), ('Tights', 'tights'),
         ('Stockings', 'stockings'), ('Leotard', 'leotard'), ('Socks', 'socks'), ('Shoes', 'shoes'), ('Boots', 'boots'),
         ('Sandals', 'sandals'), ('Gloves', 'gloves'), ('Chestwrap', 'wrap')]

def kind_of(name):
    for start, k in KINDS:
        if name.startswith(start):
            return k
    return None

rng = np.random.default_rng(SEED)
LATTICE = rng.random((64, 64, 64)).astype(np.float32)

def noise(p, scale):
    """Smooth value noise in [0, 1] at points p (n, 3), scale in leg lengths."""
    q = p / (scale * LEG) * 1.0
    i0 = np.floor(q).astype(int)
    f = q - i0
    f = f * f * (3 - 2 * f)
    out = np.zeros(len(p), np.float32)
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                out += w * LATTICE[(i0[:, 0] + dx) % 64, (i0[:, 1] + dy) % 64, (i0[:, 2] + dz) % 64]
    return out

def line(d, width):
    """1 on a line at d == 0, easing off over width."""
    return np.clip(1 - np.abs(d) / width, 0, 1)

def stitch(d, along):
    """A dashed line of stitching at d == 0, its dashes by along (leg lengths)."""
    dash = (np.mod(along / STITCH_DASH, 1.0) < 0.6).astype(np.float32)
    return line(d / LEG, STITCH) * dash

def painted(name, kind, verts, tris, uvs, loops):
    """The garment's image, SIZE square, painted texel by texel from its rest places."""
    img = np.full((SIZE, SIZE), BASE, np.float32)
    have = np.zeros((SIZE, SIZE), bool)
    ps_all, ns_all, at_all = [], [], []
    for t, tuv in zip(tris, uvs):
        a, b, c = verts[t]
        nrm = np.cross(b - a, c - a)
        ln = np.linalg.norm(nrm)
        if ln < 1e-12:
            continue
        nrm = nrm / ln
        uv = tuv * SIZE
        lo = np.floor(uv.min(0)).astype(int) - 1
        hi = np.ceil(uv.max(0)).astype(int) + 1
        xs, ys = np.meshgrid(np.arange(lo[0], hi[0]), np.arange(lo[1], hi[1]))
        pts = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], 1)
        m = np.array([[uv[1][0] - uv[0][0], uv[2][0] - uv[0][0]], [uv[1][1] - uv[0][1], uv[2][1] - uv[0][1]]])
        if abs(np.linalg.det(m)) < 1e-9:
            continue
        w12 = np.linalg.solve(m, (pts - uv[0]).T).T
        w = np.column_stack([1 - w12.sum(1), w12])
        inside = (w >= -0.02).all(1)
        xi, yi = xs.ravel()[inside], ys.ravel()[inside]
        ok = (xi >= 0) & (xi < SIZE) & (yi >= 0) & (yi < SIZE)
        if not ok.any():
            continue
        p = w[inside][ok] @ np.array([a, b, c])
        ps_all.append(p)
        ns_all.append(np.tile(nrm, (len(p), 1)))
        at_all.append(np.stack([yi[ok], xi[ok]], 1))
    p = np.concatenate(ps_all)
    n = np.concatenate(ns_all)
    at = np.concatenate(at_all)
    img[at[:, 0], at[:, 1]] = colour(kind, p, n, loops)
    have[at[:, 0], at[:, 1]] = True
    # Grown a few pixels past each island, so filtering at its edge takes the cloth, not the empty image.
    for _ in range(6):
        grow = ~have
        acc = np.zeros_like(img)
        cnt = np.zeros_like(img)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            acc += np.roll(np.roll(np.where(have, img, 0), dy, 0), dx, 1)
            cnt += np.roll(np.roll(have.astype(np.float32), dy, 0), dx, 1)
        new = grow & (cnt > 0)
        img[new] = acc[new] / cnt[new]
        have |= new
    img[~have] = BASE
    return img

def loop_distance(p, loop):
    """Distance from each point to an open edge loop (points along it), and the distance along it at the nearest."""
    d = np.full(len(p), np.inf, np.float32)
    along = np.zeros(len(p), np.float32)
    pts, alg = loop
    for k in range(0, len(pts), 64):
        dd = np.linalg.norm(p[:, None, :] - pts[None, k:k + 64, :], axis=2)
        j = dd.argmin(1)
        m = dd[np.arange(len(p)), j]
        better = m < d
        d[better] = m[better]
        along[better] = alg[k:k + 64][j[better]]
    return d, along

def openings(p, loops, which):
    """The nearest distance to the loops chosen (and along it), inf where none are."""
    d = np.full(len(p), np.inf, np.float32)
    along = np.zeros(len(p), np.float32)
    for lp in loops:
        if which(lp):
            dd, aa = loop_distance(p, (lp['pts'], lp['along']))
            better = dd < d
            d[better] = dd[better]
            along[better] = aa[better]
    return d, along

def weave(p):
    # A fine plain weave: two sets of threads, up the body and round it, slightly uneven.
    a = np.sin(p[:, 2] / (WEAVE_STEP * LEG) * math.pi)
    b = np.sin((p[:, 0] + p[:, 1]) / (WEAVE_STEP * LEG) * math.pi)
    return WEAVE * (0.5 * a * b + (noise(p, 0.05) - 0.5))

def folds(p, top_z, bottom_z, strength=FOLD):
    """Soft folds hanging from top_z, shading most at bottom_z, round the hips' upright line."""
    centre = (head('thigh.L') + head('thigh.R')) / 2
    theta = np.arctan2(p[:, 1] - centre[1], p[:, 0] - centre[0])
    share = np.clip((top_z - p[:, 2]) / max(1e-6, top_z - bottom_z), 0, 1)
    wobble = 0.6 * (noise(p, 0.15) - 0.5)
    return strength * share ** 1.3 * np.cos(FOLDS * theta + wobble * 3)

def leg_axis(p):
    """The nearer leg's bones: the side of each point (+1 left, -1 right) by x."""
    return np.where(p[:, 0] >= 0, 'L', 'R')

def azimuth_round(p, bone):
    """Each point's angle round each side's bone, from its own side's."""
    out = np.zeros(len(p), np.float32)
    for s in ('L', 'R'):
        a, b = head(bone + '.' + s), tail(bone + '.' + s)
        axis = (b - a) / np.linalg.norm(b - a)
        side = p[:, 0] >= 0 if s == 'L' else p[:, 0] < 0
        q = p[side] - a
        q = q - np.outer(q @ axis, axis)
        ref = np.cross(axis, np.array([1.0, 0, 0]))
        ref /= np.linalg.norm(ref)
        out[side] = np.arctan2(q @ np.cross(axis, ref), q @ ref)
    return out

def colour(kind, p, n, loops):
    g = BASE + weave(p)
    zs = [lp['z'] for lp in loops]
    top = max(zs) if zs else 0
    low_loop = (lambda lp: lp['z'] == min(zs)) if zs else (lambda lp: False)
    if kind in ('skirt', 'dress', 'robe'):
        hem_d, hem_a = openings(p, loops, low_loop)
        hem_z = min(zs)
        waist = top if kind == 'skirt' else min(top, WAIST_Z)
        g = g + folds(p, waist, hem_z)
        # The hem: a turned-up band, a touch darker, stitched along its top.
        g = np.where(hem_d < HEM * LEG, g * 0.9, g)
        g = g * (1 - (1 - STITCH_SHADE) * stitch(hem_d - HEM * LEG, hem_a))
        if kind == 'skirt':
            top_d, top_a = openings(p, loops, lambda lp: lp['z'] == top)
            band = top_d < BAND * LEG
            g = np.where(band, g * BAND_SHADE + 0.03 * np.sin(p[:, 2] / (0.004 * LEG) * math.pi), g)
            g = g * (1 - (1 - STITCH_SHADE) * stitch(top_d - BAND * LEG, top_a))
            g = g * (1 - 0.12 * line(top_d / LEG - BAND, 0.004))
        else:
            # The neck, armholes or sleeves and a robe's face opening: a binding stitched round each; a dress's waist seam.
            up_d, up_a = openings(p, loops, lambda lp: lp['z'] != hem_z)
            g = np.where(up_d < BAND * 0.6 * LEG, g * BAND_SHADE, g)
            g = g * (1 - (1 - STITCH_SHADE) * stitch(up_d - BAND * 0.6 * LEG, up_a))
            if kind == 'dress':
                seam = p[:, 2] - WAIST_Z
                g = g * (1 - 0.16 * line(seam / LEG, 0.004)) * (1 - 0.1 * stitch(seam - 0.012 * LEG, p[:, 0] * 2))
                # Darts up from the waist under the bust, front and back.
                for x in (-0.22, 0.22):
                    dart = np.abs(p[:, 0] / LEG - x * (np.abs(p[:, 1]) / LEG * 0 + 1) * 0.5)
                    up = (p[:, 2] > WAIST_Z) & (p[:, 2] < WAIST_Z + 0.18 * LEG)
                    g = np.where(up, g * (1 - 0.08 * line(dart, 0.004) * (1 - (p[:, 2] - WAIST_Z) / (0.18 * LEG))), g)
    elif kind in ('tights', 'stockings', 'leotard'):
        g = BASE + 0.4 * weave(p)
        g = g + KNIT * np.sin(p[:, 2] / (KNIT_STEP * LEG) * math.pi) * 0.5
        top_d, top_a = openings(p, loops, lambda lp: lp['z'] >= top - 0.25 * LEG)
        if kind != 'leotard':
            g = np.where(top_d < WELT * LEG, g * WELT_SHADE + 0.02 * np.sin(p[:, 2] / (0.003 * LEG) * math.pi), g)
            g = g * (1 - 0.18 * line(top_d / LEG - WELT, 0.003))
            g = np.where(p[:, 2] < ANKLE_Z * 1.05, g * REINFORCED, g)   # the reinforced toe and heel
            if kind == 'stockings':
                # The seam up the back of each leg.
                g = g * (1 - 0.2 * line((p[:, 0] - np.sign(p[:, 0]) * np.abs(head('thigh.L')[0])) / LEG, 0.006)
                         * (n[:, 1] > 0.3))
        else:
            all_d, all_a = openings(p, loops, lambda lp: True)
            g = np.where(all_d < BAND * 0.5 * LEG, g * BAND_SHADE, g)
            g = g * (1 - (1 - STITCH_SHADE) * stitch(all_d - BAND * 0.5 * LEG, all_a))
    elif kind == 'socks':
        top_d, top_a = openings(p, loops, lambda lp: True)
        az = azimuth_round(p, 'shin')
        rib = 0.05 * np.cos(RIB_COUNT * az)
        g = BASE + 0.4 * weave(p) + np.where(top_d < CUFF * LEG, rib, 0.02 * np.sin(p[:, 2] / (KNIT_STEP * LEG) * math.pi))
        g = np.where(p[:, 2] < ANKLE_Z * 0.9, g * REINFORCED, g)
    elif kind in ('shoes', 'boots', 'sandals'):
        grain = 0.05 * (noise(p, 0.012) - 0.5) + 0.03 * (noise(p, 0.004) - 0.5)
        g = BASE + grain
        z = p[:, 2] / LEG
        g = np.where(z < SOLE, SOLE_SHADE + 0.04 * (noise(p, 0.01) - 0.5), g)
        g = g * (1 - 0.25 * line(z - SOLE - 0.002, 0.0025))   # where the upper meets the sole
        g = g * (1 - (1 - STITCH_SHADE) * stitch(p[:, 2] - (SOLE + WELT_LINE) * LEG, p[:, 0] + p[:, 1]))
        top_d, top_a = openings(p, loops, lambda lp: True)
        if kind == 'sandals':
            g = g * (1 - (1 - STITCH_SHADE) * stitch(top_d - 0.008 * LEG, top_a))
        else:
            g = np.where(top_d < BAND * 0.7 * LEG, g * BAND_SHADE, g)
            g = g * (1 - (1 - STITCH_SHADE) * stitch(top_d - BAND * 0.7 * LEG, top_a))
            # Laces: crossing strokes down the front, over the instep (and up the shin for the boots).
            for s, sx in (('L', 1), ('R', -1)):
                foot = head('foot.' + s)
                side = (p[:, 0] * sx > 0)
                # Across the foot's own line seen from above (from the ankle to the toes), as the feet turn out.
                way = (tail('foot.' + s) - foot)[:2]
                way = way / np.linalg.norm(way)
                across = (p[:, 0] - foot[0]) * way[1] - (p[:, 1] - foot[1]) * way[0]
                front = n[:, 1] < -0.2   # Blender's front is -y
                hi = 0.55 if kind == 'boots' else 0.17
                lace_zone = side & front & (z > SOLE + 0.04) & (z < hi) & (np.abs(across) < 0.05 * LEG)
                if kind != 'boots':
                    lace_zone &= p[:, 1] - foot[1] < 0.0   # forward of the ankle
                xz = np.mod(z / 0.03, 1.0)
                cross = line(np.abs(across / LEG) - 0.04 * np.abs(xz - 0.5), 0.004)
                g = np.where(lace_zone & (cross > 0), g * (1 - (1 - LACE_SHADE) * cross), g)
                # The toe cap's seam: round the front of the foot.
                toe = np.linalg.norm((p - tail('foot.' + s))[:, :2], axis=1) / LEG
                g = np.where(side & (z < 0.08), g * (1 - 0.2 * line(toe - 0.07, 0.003)), g)
    elif kind == 'gloves':
        grain = 0.04 * (noise(p, 0.01) - 0.5)
        g = BASE + grain
        top_d, top_a = openings(p, loops, lambda lp: True)
        g = np.where(top_d < BAND * 0.8 * LEG, g * 0.9 + 0.03 * np.cos(14 * azimuth_round(p, 'forearm')), g)
        g = g * (1 - (1 - STITCH_SHADE) * stitch(top_d - BAND * 0.8 * LEG, top_a))
        # Three stitched lines down the back of the hand toward the fingers.
        for s, sx in (('L', 1), ('R', -1)):
            a, b = head('hand.' + s), tail('hand.' + s)
            axis = (b - a) / np.linalg.norm(b - a)
            q = p - a
            t = q @ axis
            side = (p[:, 0] * sx > 0) & (t > 0.01 * LEG) & (t < 0.12 * LEG)
            off = q - np.outer(t, axis)
            across = off @ np.array([0, 1.0, 0])
            for k in (-1, 0, 1):
                g = np.where(side, g * (1 - 0.15 * stitch(across - k * 0.018 * LEG, t)), g)
    elif kind == 'wrap':
        band = np.mod((p[:, 2] + 0.35 * p[:, 0]) / (0.035 * LEG), 1.0)
        g = BASE + weave(p) - 0.07 * line(band - 0.0, 0.12) - 0.07 * line(band - 1.0, 0.12)
    return np.clip(g, 0, 1)

def boundary_loops(bm, mw):
    """The welded mesh's open edge loops: points every so often along each, its distance along them, and its mean height."""
    edges = [e for e in bm.edges if e.is_boundary]
    seen = set()
    loops = []
    for e in edges:
        if e.index in seen:
            continue
        # Walk the connected boundary edges.
        stack = [e]
        group = []
        while stack:
            x = stack.pop()
            if x.index in seen:
                continue
            seen.add(x.index)
            group.append(x)
            for v in x.verts:
                for y in v.link_edges:
                    if y.is_boundary and y.index not in seen:
                        stack.append(y)
        pts, along = [], []
        s = 0.0
        for x in group:
            a = np.array(tuple(mw @ x.verts[0].co))
            b = np.array(tuple(mw @ x.verts[1].co))
            ln = np.linalg.norm(b - a)
            steps = max(1, int(ln / (0.004 * LEG)))
            for k in range(steps):
                pts.append(a + (b - a) * (k / steps))
                along.append(s + ln * k / steps)
            s += ln
        pts = np.array(pts)
        loops.append({'pts': pts, 'along': np.array(along, np.float32), 'z': float(pts[:, 2].mean())})
    return loops

def paint_file(path):
    name = os.path.splitext(os.path.basename(path))[0]
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    new = set(bpy.data.objects) - before
    meshes = [o for o in new if o.type == 'MESH']
    arms = [o for o in new if o.type == 'ARMATURE']
    if len(meshes) != 1:
        print('PAINT %s: %d meshes, left alone' % (name, len(meshes)))
        for o in new:
            bpy.data.objects.remove(o)
        return
    g = meshes[0]
    kind = force_kind or kind_of(g.name) or kind_of(name[:1].upper() + name[1:])
    if kind is None:
        print('PAINT %s: no kind for %s, left alone' % (name, g.name))
        for o in new:
            bpy.data.objects.remove(o)
        return
    if keep_dir:
        os.makedirs(keep_dir, exist_ok=True)
        kept = os.path.join(keep_dir, name + '_beforePaint.glb')
        if not os.path.exists(kept):
            shutil.copyfile(path, kept)
    me = g.data
    nverts = len(me.vertices)
    # Welded: the file splits its vertices along its old UV seams.
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bm.to_mesh(me)
    bm.free()
    me.update()
    # A fresh unwrap: islands by angle, spread over the whole image.
    bpy.ops.object.select_all(action='DESELECT')
    g.select_set(True)
    bpy.context.view_layer.objects.active = g
    while me.uv_layers:
        me.uv_layers.remove(me.uv_layers[0])
    me.uv_layers.new(name='UVMap')
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.012, area_weight=0.0, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode='OBJECT')
    mw = g.matrix_world.copy()
    me.calc_loop_triangles()
    verts = np.array([tuple(mw @ v.co) for v in me.vertices])
    uvl = me.uv_layers.active.data
    tris = np.array([tuple(t.vertices) for t in me.loop_triangles], int)
    uvs = np.array([[tuple(uvl[l].uv) for l in t.loops] for t in me.loop_triangles], np.float32)
    bm = bmesh.new()
    bm.from_mesh(me)
    loops = boundary_loops(bm, mw)
    bm.free()
    img = painted(name, kind, verts, tris, uvs, loops)
    image = bpy.data.images.new(g.name + '_cloth', SIZE, SIZE, alpha=False)
    rgba = np.ones((SIZE, SIZE, 4), np.float32)
    rgba[..., 0] = rgba[..., 1] = rgba[..., 2] = img
    image.pixels.foreach_set(rgba.ravel())
    image.pack()
    if preview_dir:
        os.makedirs(preview_dir, exist_ok=True)
        image.filepath_raw = os.path.join(preview_dir, name + '.png')
        image.file_format = 'PNG'
        image.save()
    mat = bpy.data.materials.new(g.name + '_cloth')
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    tex = nodes.new('ShaderNodeTexImage')
    tex.image = image
    mat.node_tree.links.new(tex.outputs['Color'], nodes['Principled BSDF'].inputs['Base Color'])
    me.materials.clear()
    me.materials.append(mat)
    bpy.ops.object.select_all(action='DESELECT')
    g.select_set(True)
    skinned = bool(arms) and any(m.type == 'ARMATURE' for m in g.modifiers)
    for a in arms:
        a.select_set(skinned)
    bpy.context.view_layer.objects.active = g
    keys = me.shape_keys is not None and len(me.shape_keys.key_blocks) > 1
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format='GLB', export_extras=True,
                              export_skins=skinned, export_morph=keys, export_morph_normal=False,
                              export_animations=False, export_apply=False)
    print('PAINT %s (%s): %d -> %d vertices welded, %d open edge loops, skinned %s, shape keys %s'
          % (name, kind, nverts, len(me.vertices), len(loops), skinned, keys))
    for o in new:
        if o.name in bpy.data.objects:
            bpy.data.objects.remove(o)

for f in files:
    paint_file(f)
