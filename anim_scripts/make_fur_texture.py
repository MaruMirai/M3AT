"""Makes the Furskin gene's fur texture: one grey image, multiplied by the skin's colour, on one new atlas shared by the body,
the head and the tufts of Models/garments/Furskin.glb, so that a tuft shows what lies on the skin under it.

    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- atlas <out folder> [size]
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- baked <out folder> <textured.glb> [size]
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- painted <out folder> [size]
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- strip <out folder> <out.glb>
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- paintfile <out folder> <fur.png>
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- game <out folder> <fur.png> <chibi folder>
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- view <out folder> <fur.png> <prefix> [--bare]

The fur's atlas is its own, not the rig's UV map, as it takes in the tufts too: `atlas` makes it and keeps it in <out folder>/fur_atlas.npz; every other
command reads that, so all the images fit one layout. It also writes fur_body.glb and fur_tufts.glb, the meshes with that atlas.

`painted` paints it procedurally (`paint`): each texel's rest place, normal and bone weights on the body give a pale belly,
throat, muzzle, inner legs, arm undersides and paws with tufted edges (`PALE_` numbers), a few tapered stripes over the back,
flanks, thighs and upper arms (`STRIPE_` numbers), and fine strands along the fur's way (down the trunk, along the limbs, back
over the head from the face; `STRAND_`, `CLUMP_`), all worked out in 3D, so the atlas's seams don't show. Writes fur_painted.png.

`paintfile` writes <out folder>/fur_paint.blend, a file of its own (not the rig) to touch the image up by hand in Texture Paint:
the body and the tufts on the atlas, the image linked from beside it (Image > Save in Blender writes the PNG back), and a
material showing it times a skin tone (`Tone` in the material, a Mix node set to 0 shows the image alone).

`game` writes what the game draws a Furskin pawn's coat from into <chibi folder> (Models/chibi in the mod's folder): fur.png,
the painted image (touched up by hand in fur_paint.blend, never written over here) lightened (`GAME_LIFT`) with the hair's
strand grain laid over it along the fur's way (`GRAIN_`: as HairBuilder's StrandStreak, one sprite pixel of the hair a
cell of it across), and fur.txt, each body triangle's corners' places on it, matched to skin.txt by place. Run it again after
painting or after an export that changes the body (SkinnedFigure.ReadCoat warns when they no longer match); the tufts find
their places on it in the game (StrandGrain.cs, CoatTufts), so changing them needs only an export.

`baked` bakes from a mesh Meshy textured (the upload from `strip`, the chibi's own mesh and nothing else, with a text prompt), where the place is the place itself as it comes back on the same triangles.

Lengths are the rig's units."""
import math
import os
import struct
import sys
import zlib

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

PALE = 1.00     # the brightness of white fur: the skin's own colour
COAT = 0.80     # of the coat, as a multiply can only darken
MARK = 0.17     # of the black marks
PAD = 12        # texels each island's edge is carried out by, so nothing dark bleeds in as the image is mipped
ANGLE = 62.0    # degrees: smart project's limit
MARGIN = 0.006  # of the atlas between islands
SKIN_TONES = ((0.96, 0.80, 0.68), (0.72, 0.50, 0.34), (0.42, 0.27, 0.18))



# Meshy paints a face whatever the prompt says, and the game's goes there. Over an oval on the front of the head, in shares of
# the head's height up from the chin and of its half width, the painting gives way to pale fur at the chin and coat above.
PAINTED_FACE = (0.20, 0.27, 0.78)  # the oval's middle, its half height and its half width
PAINTED_FACE_SOFT = 0.15           # the share of the way in over which it gives way
PAINTED_FACE_COAT = (0.14, 0.30)   # the heights over which pale fur turns to coat


def png_write(path, grey):
    """A grey image, row 0 at the top."""
    h, w = grey.shape
    a = (np.clip(grey, 0, 1) * 255 + 0.5).astype(np.uint8)
    raw = b''.join(b'\0' + a[y].tobytes() for y in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d))
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 0, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 6)) + chunk(b'IEND', b''))


def image_read(path):
    """RGBA floats as the file has them, row 0 at the bottom, as UVs have it."""
    im = bpy.data.images.load(path)
    im.colorspace_settings.name = 'Non-Color'
    w, h = im.size
    a = np.empty(w * h * 4, np.float32)
    im.pixels.foreach_get(a)
    bpy.data.images.remove(im)
    return a.reshape(h, w, 4)


def bilinear(image, uv):
    """Samples image (H x W, or H x W x n) at uv (N x 2)."""
    h, w = image.shape[:2]
    x = np.clip(uv[:, 0] * w - 0.5, 0, w - 1.001)
    y = np.clip(uv[:, 1] * h - 0.5, 0, h - 1.001)
    x0, y0 = x.astype(np.int32), y.astype(np.int32)
    fx, fy = x - x0, y - y0
    if image.ndim == 3:
        fx, fy = fx[:, None], fy[:, None]
    return (image[y0, x0] * (1 - fx) * (1 - fy) + image[y0, x0 + 1] * fx * (1 - fy)
            + image[y0 + 1, x0] * (1 - fx) * fy + image[y0 + 1, x0 + 1] * fx * fy)




def dilate(values, known, reach):
    """Carries values out from the known texels, each new one the mean of its known neighbours."""
    values, known = values.copy(), known.copy()
    for _ in range(reach):
        total = np.zeros_like(values)
        count = np.zeros_like(values)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1)):
            total += np.roll(np.roll(values * known, dy, 0), dx, 1)
            count += np.roll(np.roll(known.astype(np.float32), dy, 0), dx, 1)
        new = ~known & (count > 0)
        values[new] = total[new] / count[new]
        known = known | new
    values[~known] = np.median(values[known])
    return values


def select_only(o):
    for x in bpy.context.scene.objects:
        x.select_set(x is o)
    bpy.context.view_layer.objects.active = o


class Chibi:
    """The rig's body and head at rest as one mesh, in world places, with its triangles and weights."""

    def __init__(self):
        self.rig = bpy.data.objects['metarig']
        self.bones = [b.name for b in self.rig.data.bones if b.use_deform]
        index = {n: i for i, n in enumerate(self.bones)}
        places, tris, parts, weights = [], [], [], []
        for part, name in enumerate(('Body', 'Head')):
            o = bpy.data.objects[name]
            m = o.data
            m.calc_loop_triangles()
            start = len(places)
            basis = m.shape_keys.reference_key.data if m.shape_keys else m.vertices
            names = [g.name for g in o.vertex_groups]
            for i, v in enumerate(m.vertices):
                places.append((o.matrix_world @ basis[i].co)[:])
                w = np.zeros(len(self.bones), np.float32)
                for g in v.groups:
                    if names[g.group] in index:
                        w[index[names[g.group]]] = g.weight
                weights.append(w / max(w.sum(), 1e-6))
            for t in m.loop_triangles:
                tris.append([start + i for i in t.vertices])
                parts.append(part)
        self.places = np.array(places, np.float64)
        self.tris = np.array(tris, np.int32)
        self.parts = np.array(parts, np.int32)
        self.weights = np.array(weights, np.float32)
        self.bvh = BVHTree.FromPolygons([Vector(p) for p in self.places], [t.tolist() for t in self.tris])

    def joint(self, bone, end):
        b = self.rig.data.bones[bone]
        return np.array((self.rig.matrix_world @ (b.head_local if end == 'head' else b.tail_local))[:])

    def parts_of_vertices(self):
        out = np.zeros(len(self.places), np.int32)
        out[self.tris[self.parts == 1].ravel()] = 1
        return out

    def mesh_object(self, name, uv=None):
        m = bpy.data.meshes.new(name)
        m.from_pydata(self.places.tolist(), [], self.tris.tolist())
        m.update()
        if uv is not None:
            layer = m.uv_layers.new(name='UVMap')
            layer.data.foreach_set('uv', uv.reshape(-1))
        o = bpy.data.objects.new(name, m)
        bpy.context.scene.collection.objects.link(o)
        return o


def make_atlas(chibi):
    o = chibi.mesh_object('FurAtlas')
    select_only(o)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(ANGLE), island_margin=MARGIN, area_weight=0.0, scale_to_bounds=True)
    bpy.ops.object.mode_set(mode='OBJECT')
    uv = np.empty(len(o.data.loops) * 2, np.float32)
    o.data.uv_layers.active.data.foreach_get('uv', uv)
    uv = uv.reshape(-1, 3, 2).astype(np.float64)
    bpy.data.objects.remove(o)
    return uv


def tuft_atlas(chibi, uv):
    """The tufts of Furskin.glb on the body's atlas: each tuft, whole, on the one body triangle under its base's middle, its
    corners going by the place on that triangle nearest them. A tuft across an island's edge would otherwise smear the atlas
    between the two islands. Returns the mesh's places, triangles and each corner's UV."""
    path = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'Models', 'garments', 'Furskin.glb')
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    o = next(x for x in bpy.data.objects if x not in before and x.type == 'MESH')
    o.data.calc_loop_triangles()
    places = np.array([(o.matrix_world @ v.co)[:] for v in o.data.vertices])
    tris = np.array([t.vertices[:] for t in o.data.loop_triangles], np.int32)
    for x in [x for x in bpy.data.objects if x not in before]:
        bpy.data.objects.remove(x)

    parent = list(range(len(places)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    for t in tris:
        parent[find(t[1])] = find(t[0])
        parent[find(t[2])] = find(t[0])
    tufts = {}
    for i in range(len(places)):
        tufts.setdefault(find(i), []).append(i)

    corner = np.zeros((len(places), 2))
    for members in tufts.values():
        # A tuft's point is the corner furthest from the skin; the rest lie on it.
        off = [chibi.bvh.find_nearest(Vector(places[i]))[3] for i in members]
        base = [m for m, d in zip(members, off) if d < max(off) * 0.5] or members
        hit = chibi.bvh.find_nearest(Vector(places[base].mean(0)))
        a, b, c = chibi.places[chibi.tris[hit[2]]]
        ua, ub, uc = uv[hit[2]]
        for i in members:
            corner[i] = closest_on_triangle_uv(places[i], a, b, c, ua, ub, uc)
    return places, tris, corner[tris]


def closest_on_triangle_uv(p, a, b, c, ua, ub, uc):
    ab, ac = b - a, c - a
    d = np.array(((ab @ ab, ab @ ac), (ab @ ac, ac @ ac)))
    r = np.array((ab @ (p - a), ac @ (p - a)))
    try:
        v, w = np.linalg.solve(d, r)
    except np.linalg.LinAlgError:
        return (ua + ub + uc) / 3
    v, w = max(v, 0.0), max(w, 0.0)
    if v + w > 1:
        v, w = v / (v + w), w / (v + w)
    return ua * (1 - v - w) + ub * v + uc * w


def texels(chibi, uv, size):
    """Yields each triangle's covered texels: (triangle, y, x, the corners' shares)."""
    for t in range(len(chibi.tris)):
        q = uv[t] * size - 0.5
        low = np.floor(q.min(0)).astype(int)
        high = np.ceil(q.max(0)).astype(int)
        low = np.maximum(low, 0)
        high = np.minimum(high, size - 1)
        if (high < low).any():
            continue
        xs, ys = np.meshgrid(np.arange(low[0], high[0] + 1), np.arange(low[1], high[1] + 1))
        p = np.stack((xs.ravel(), ys.ravel()), axis=1).astype(np.float64)
        e1, e2 = q[1] - q[0], q[2] - q[0]
        det = e1[0] * e2[1] - e1[1] * e2[0]
        if abs(det) < 1e-9:
            continue
        rel = p - q[0]
        b = (rel[:, 0] * e2[1] - rel[:, 1] * e2[0]) / det
        c = (e1[0] * rel[:, 1] - e1[1] * rel[:, 0]) / det
        a = 1 - b - c
        # A little past the edge, so no texel on one falls between two triangles.
        inside = (a >= -0.02) & (b >= -0.02) & (c >= -0.02)
        if inside.any():
            yield t, ys.ravel()[inside], xs.ravel()[inside], np.stack((a[inside], b[inside], c[inside]), axis=1)








def nearest(tree, corners, uvs, q):
    """For each place of q (N x 3): the texture place of the nearest point on the mesh, and which triangle it is on."""
    hits = [tree.find_nearest(Vector(place)) for place in q]
    faces = np.array([h[2] for h in hits], np.int32)
    points = np.array([h[0][:] for h in hits])
    a, b, c = corners[faces, 0], corners[faces, 1], corners[faces, 2]
    ab, ac, ap = b - a, c - a, points - a
    d00, d01, d11 = (ab * ab).sum(1), (ab * ac).sum(1), (ac * ac).sum(1)
    d20, d21 = (ap * ab).sum(1), (ap * ac).sum(1)
    det = np.maximum(d00 * d11 - d01 * d01, 1e-20)
    v = np.clip((d11 * d20 - d01 * d21) / det, 0, 1)
    w = np.clip((d00 * d21 - d01 * d20) / det, 0, 1)
    u = np.clip(1 - v - w, 0, 1)
    t = uvs[faces]
    return t[:, 0] * u[:, None] + t[:, 1] * v[:, None] + t[:, 2] * w[:, None], faces












def textured_mesh(path, chibi):
    """A mesh Meshy textured: its corners fitted back to the chibi's box (it comes back centred, its longest side 2), its
    corners' texture places, and its image, row 0 at the bottom."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    o = next(x for x in bpy.data.objects if x not in before and x.type == 'MESH')
    o.data.calc_loop_triangles()
    co = np.array([(o.matrix_world @ v.co)[:] for v in o.data.vertices])
    tris = np.array([t.vertices[:] for t in o.data.loop_triangles], np.int32)
    uvd = o.data.uv_layers.active.data
    uvs = np.array([[uvd[l].uv[:] for l in t.loops] for t in o.data.loop_triangles])
    image = next(n.image for n in o.data.materials[0].node_tree.nodes if n.type == 'TEX_IMAGE')
    w, h = image.size
    pixels = np.empty(w * h * 4, np.float32)
    image.colorspace_settings.name = 'Non-Color'
    image.pixels.foreach_get(pixels)
    low, high = co.min(0), co.max(0)
    lowc, highc = chibi.places.min(0), chibi.places.max(0)
    co = lowc + (co - low) * (highc - lowc) / (high - low)
    return co, tris, uvs, pixels.reshape(h, w, 4)


def levelled(light):
    """Meshy's brightness set out as pale fur, coat and marks: the middle of it (the most of the coat) at COAT, its top at PALE, and from
    half way down to its darkest toward MARK, so the stripes are as dark as the spots."""
    middle = np.percentile(light, 50)
    top = max(np.percentile(light, 98), middle + 0.02)
    dark = min(np.percentile(light, 1), middle - 0.05)
    return np.interp(light, (dark, (dark + middle) / 2, middle, top), (MARK, (MARK + COAT) / 2, COAT, PALE))


def bake_meshy(chibi, uv, size, path):
    co, tris, uvs, image = textured_mesh(path, chibi)
    tree = BVHTree.FromPolygons([Vector(p) for p in co], tris.tolist())
    corners = co[tris]
    light = np.zeros((size, size), np.float32)
    known = np.zeros((size, size), bool)
    gone = np.zeros((size, size), np.float32)
    fill = np.zeros((size, size), np.float32)
    head = chibi.places[chibi.parts_of_vertices() == 1]
    chin, top = head[:, 2].min(), head[:, 2].max()
    half = head[:, 0].max()
    mid_depth = (head[:, 1].min() + head[:, 1].max()) / 2
    for t, ys, xs, shares in texels(chibi, uv, size):
        p = shares @ chibi.places[chibi.tris[t]]
        places, _ = nearest(tree, corners, uvs, p)
        rgb = bilinear(image, places)[:, :3]
        light[ys, xs] = rgb @ np.array((0.299, 0.587, 0.114))
        if chibi.parts[t] == 1:
            up = (p[:, 2] - chin) / (top - chin)
            off = np.hypot((up - PAINTED_FACE[0]) / PAINTED_FACE[1], (p[:, 0] / half) / PAINTED_FACE[2])
            gone[ys, xs] = np.clip((1 - off) / PAINTED_FACE_SOFT, 0, 1) * (p[:, 1] < mid_depth)
            fill[ys, xs] = np.interp(up, PAINTED_FACE_COAT, (PALE, COAT))
    known = light > 0
    grey = np.zeros_like(light)
    grey[known] = levelled(light[known])
    grey = grey * (1 - gone) + fill * gone
    return dilate(grey, known, PAD)


# The painted fur (paint). Lengths in cells (FIGURE_HEIGHT 1.25 a grown pawn's height), angles in degrees.
FIGURE_HEIGHT = 1.25
PAINT_COAT = 0.68        # the coat's brightness
PAINT_PALE = 0.97        # the pale fur's, a little under white so the strands show on it
PAINT_STRIPE = 0.36      # the stripes'
PALE_BELLY = ((0.0, 26), (0.18, 36), (0.5, 22), (0.75, 34), (0.9, 45), (1.0, 40))   # (share from the crotch to the neck, half angle round the trunk from the front)
PALE_LEG = ((0.0, 75), (0.45, 50), (1.0, 34))   # (share from the hip to the ankle, half angle round the leg from its inner side)
PALE_ARM = 55            # half angle round the arm from its underside
PALE_ARM_FORWARD = 20    # the underside turned this far forward, as the arm's inside faces the body
PALE_MUZZLE = (0.1, 0.24, 54)   # the muzzle: its middle and how far above that it reaches (shares of the head's height up from the chin), and its half angle round the head from the front
PALE_BELOW = 260         # degrees the belly's narrows per share (of the crotch to the neck) below the crotch
PALE_LEG_TURN = 0.3      # the share down the leg over which its pale side turns from toward the front to the inner side
PALE_LEG_FRONT = 0.6     # how far toward the front it faces at the hip (0 inner, 1 front)
PALE_OFF_PART = 120      # degrees a limb's or the head's is held back where the skin is none of that part's (by the bone weights)
PALE_PIT = (-0.025, -0.04, -0.1)   # cells: the armpit patch's middle from the shoulder joint (out, back, up)
PALE_PIT_SIZE = 0.078    # cells: its radius
PALE_PIT_BACK = 3.5      # how many times further a place behind the patch's middle counts as
PALE_THROAT = 62         # half angle round the neck from the front that is pale
PALE_THROAT_REACH = 0.03 # cells past the neck's joint and the chin it reaches
PALE_PER_CELL = 600      # degrees a cell counts as, for a patch measured by distance
PALE_BLEND = 12          # degrees over which two parts' pale fur run together in a curve
PALE_PAWS = 0.55         # how far the hands and feet go pale (0 coat, 1 pale)
PALE_SOFT = 2.5            # degrees over which an edge goes from coat to pale, under the tufts
TUFT_ANGLE = 15          # degrees the tufts push an edge in or out
TUFT_ACROSS, TUFT_ALONG = 0.013, 0.1   # cells: a tuft's width and length
STRIPE_TRUNK = (0.22, 0.92, 4)   # the trunk's stripes: from and to (shares from the crotch to the neck) and how many
STRIPE_THIGH = (0.12, 0.55, 2)   # the thighs': shares down the leg from the hip
STRIPE_SHIN = (0.62, 0.80, 1)
STRIPE_ARM = (0.08, 0.45, 2)     # the upper arms': shares along the arm from the shoulder to the wrist
STRIPE_WIDTH = 0.36      # a stripe's share of its spacing at its widest, along the spine or the limb's outside
STRIPE_REACH = 0.82      # how far round from there toward the pale fur it goes before its point (share)
STRIPE_SWEEP = 0.22      # how far a trunk stripe sweeps down toward the belly (shares of the spacing per radian round)
STRIPE_WAVE = 0.18       # how far a stripe wanders (shares of the spacing)
STRAND_ACROSS, STRAND_ALONG = 0.0055, 0.04   # cells: a strand's width and length
STRAND = 0.2            # how much the strands brighten or darken
CLUMP_ACROSS, CLUMP_ALONG = 0.02, 0.09        # cells: clumps of strands
CLUMP = 0.025
FAN = 0.6                # how far the trunk's fur leans out from the middle (sideways per unit down)


def hash3(i, seed):
    """A number from -1 to 1 for each integer lattice point (N x 3, int64)."""
    h = (i[:, 0] * 73856093) ^ (i[:, 1] * 19349663) ^ (i[:, 2] * 83492791) ^ (seed * 2654435761)
    h = (h ^ (h >> 13)) * 1274126177
    h = h ^ (h >> 16)
    return (h & 0xFFFFFF).astype(np.float64) / 0x7FFFFF - 1.0


def vnoise(q, seed):
    """Smooth value noise in 3D, -1 to 1, one lattice cell a unit."""
    i = np.floor(q).astype(np.int64)
    f = q - i
    f = f * f * (3 - 2 * f)
    out = 0.0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                out = out + w * hash3(i + np.array((dx, dy, dz)), seed)
    return out


def streaked(p, way, across, along, seed, octaves=2):
    """Noise stretched along the fur's way (N x 3, unit): across wide by across, along it by along (cells)."""
    a = np.sum(p * way, axis=1)[:, None]
    q = (p - a * way) / across + a * way / along
    out, amp, total = 0.0, 1.0, 0.0
    for o in range(octaves):
        out = out + amp * vnoise(q * (2 ** o) + 17.3 * o, seed + o)
        total += amp
        amp *= 0.5
    return out / total


def dashes(p, way, across, along, seed):
    """Short strokes scattered over the skin, each stretched along the fur's way (across wide by across, along it by along,
    cells) with a brightness of its own, -1 to 1: summed where they overlap. One stroke to a cell of the stretched lattice,
    jittered, as a painted coat is laid stroke by stroke."""
    a = np.sum(p * way, axis=1)[:, None]
    q = (p - a * way) / across + a * way / along
    i = np.floor(q).astype(np.int64)
    out = np.zeros(len(p))
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                c = i + np.array((dx, dy, dz))
                at = c + 0.5 + 0.45 * np.stack((hash3(c, seed), hash3(c, seed + 1), hash3(c, seed + 2)), axis=1)
                d = np.linalg.norm(q - at, axis=1)
                out += hash3(c, seed + 3) * np.clip(1 - d / 0.9, 0, 1) ** 2
    return out


def tongues(p, way, across, along, seed):
    """Separate strokes as dashes lays them, 0 to 1: the strongest at each place rather than their sum, so a level cut through
    them (an edge pushed out by them) comes out as pointed tongues of fur, not blobs."""
    a = np.sum(p * way, axis=1)[:, None]
    q = (p - a * way) / across + a * way / along
    i = np.floor(q).astype(np.int64)
    out = np.zeros(len(p))
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                c = i + np.array((dx, dy, dz))
                at = c + 0.5 + 0.45 * np.stack((hash3(c, seed), hash3(c, seed + 1), hash3(c, seed + 2)), axis=1)
                d = np.linalg.norm(q - at, axis=1)
                out = np.maximum(out, (0.55 + 0.45 * hash3(c, seed + 3)) * np.clip(1 - d / 0.95, 0, 1))
    return out


def strands(p, way):
    """Fine strands along the fur's way, about -1 to 1: two sizes of strokes."""
    return 0.7 * dashes(p, way, STRAND_ACROSS, STRAND_ALONG, 3) + 0.4 * dashes(p, way, STRAND_ACROSS * 2.2, STRAND_ALONG * 1.6, 41)


def on_chain(p, joints):
    """Each place's nearest point on a chain of joints: how far along it (share of the chain's length), the offset from it, and the
    way of the segment it is on."""
    best = np.full(len(p), np.inf)
    along = np.zeros(len(p))
    off = np.zeros_like(p)
    way = np.zeros_like(p)
    run, total = 0.0, sum(np.linalg.norm(joints[j + 1] - joints[j]) for j in range(len(joints) - 1))
    for j in range(len(joints) - 1):
        a, d = joints[j], joints[j + 1] - joints[j]
        ln = np.linalg.norm(d)
        t = np.clip((p - a) @ d / (ln * ln), 0, 1)
        q = a + t[:, None] * d
        dist = np.sum((p - q) ** 2, axis=1)
        take = dist < best
        best[take] = dist[take]
        along[take] = (run + t[take] * ln) / total
        off[take] = (p - q)[take]
        way[take] = d / ln
        run += ln
    return along, off, way


def angle_from(off, way, toward):
    """Degrees round the chain from the way toward (one vector or N x 3), in the plane square to the chain."""
    perp = off - np.sum(off * way, axis=1)[:, None] * way
    t = np.broadcast_to(toward, off.shape)
    t = t - np.sum(t * way, axis=1)[:, None] * way
    c = np.sum(perp * t, axis=1) / np.maximum(np.linalg.norm(perp, axis=1) * np.linalg.norm(t, axis=1), 1e-9)
    return np.degrees(np.arccos(np.clip(c, -1, 1)))


def smooth_most(a, b):
    """The larger of two margins, rounded where they cross (over PALE_BLEND)."""
    h = np.clip(PALE_BLEND - np.abs(a - b), 0, None) / PALE_BLEND
    return np.maximum(a, b) + h * h * PALE_BLEND * 0.25


def smooth_edge(x, soft):
    """0 to 1 as x goes from -soft to soft."""
    t = np.clip((x + soft) / (2 * soft), 0, 1)
    return t * t * (3 - 2 * t)


def stripes(s, width):
    """1 on a stripe: s in stripe spacings (a stripe about each whole number), width its share of the spacing there."""
    d = np.abs(s - np.round(s))
    return smooth_edge(width / 2 - d, 0.04) * np.clip(width / 0.08, 0, 1)


def fur_setup(chibi):
    """What every texel's fur is worked out from: places in cells, the joints, normals and the way each bone's fur lies."""
    places = chibi.places
    low, high = places.min(0), places.max(0)
    cell = (high[2] - low[2]) / FIGURE_HEIGHT
    index = {n: i for i, n in enumerate(chibi.bones)}

    def j(bone, end='head'):
        return chibi.joint(bone, end) / cell

    def group(names):
        g = np.zeros(len(chibi.bones), np.float32)
        for n in names:
            if n in index:
                g[index[n]] = 1
        return g

    # Each vertex's normal.
    v = places / cell
    corners = v[chibi.tris]
    fn = np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0])
    vn = np.zeros_like(v)
    for k in range(3):
        np.add.at(vn, chibi.tris[:, k], fn)
    vn /= np.maximum(np.linalg.norm(vn, axis=1), 1e-12)[:, None]

    spine = [j(n) for n in ('spine', 'spine.001', 'spine.002', 'spine.003', 'spine.004', 'spine.005')]
    crotch, neck = j('thigh.L')[2] - 0.03, j('spine.005')[2]
    head = v[chibi.parts_of_vertices() == 1]
    chin, crown = head[:, 2].min(), head[:, 2].max()
    head_mid = np.array((0.0, (head[:, 1].min() + head[:, 1].max()) / 2, (chin + crown) / 2))
    nose = np.array((0.0, head[:, 1].min(), chin + 0.35 * (crown - chin)))
    sides = {}
    for side, sign in (('L', 1.0), ('R', -1.0)):
        if 'thigh.' + side not in index:
            continue
        sides[side] = dict(
            sign=sign,
            leg=[j('thigh.' + side), j('shin.' + side), j('foot.' + side)],
            arm=[j('upper_arm.' + side), j('forearm.' + side), j('hand.' + side)],
            legs=group([n for n in chibi.bones if n.startswith(('thigh.', 'shin.')) and n.endswith(side)]),
            arms=group([n for n in chibi.bones if n.startswith(('upper_arm.', 'forearm.', 'shoulder.')) and n.endswith(side)]),
            hand=group([n for n in chibi.bones if n.startswith(('hand.', 'thumb.', 'f_', 'palm.')) and n.endswith(side)]),
            foot=group([n for n in chibi.bones if n.startswith(('foot.', 'toe.', 'heel.')) and n.endswith(side)]))
    head_bones = group(['spine.005', 'spine.006'] + [n for n in chibi.bones if n.startswith(('face', 'jaw', 'ear', 'eye', 'nose', 'lip', 'brow', 'cheek', 'chin', 'forehead', 'temple', 'tongue', 'teeth'))])
    # Each bone's way the fur lies: down the trunk, along a limb toward its end; the head's from the face (head_way below).
    way_of = np.zeros((len(chibi.bones), 3))
    way_of[:] = (0, 0, -1)
    for b, n in enumerate(chibi.bones):
        if n.startswith(('upper_arm', 'forearm', 'hand', 'thumb', 'f_', 'palm', 'thigh', 'shin', 'foot', 'toe')):
            d = chibi.joint(n, 'tail') - chibi.joint(n, 'head')
            way_of[b] = d / max(np.linalg.norm(d), 1e-9)

    neck_bones = group(['spine.005'])
    limb_bones = group([n for n in chibi.bones if n.startswith(('upper_arm', 'forearm', 'hand', 'thumb', 'f_', 'palm', 'thigh', 'shin', 'foot', 'toe'))])
    return dict(cell=cell, v=v, vn=vn, spine=spine, crotch=crotch, neck=neck, chin=chin, crown=crown, head_mid=head_mid,
                nose=nose, sides=sides, head_bones=head_bones, way_of=way_of, neck_bones=neck_bones, limb_bones=limb_bones)


def fur_at(c, chibi, t, shares):
    """A run of texels on triangle t (their corners' shares): their places (cells), normals, bone weights, share on the head
    and the way the fur lies there (unit, along the skin)."""
    v, vn, head_bones, way_of, limb_bones, nose = c['v'], c['vn'], c['head_bones'], c['way_of'], c['limb_bones'], c['nose']
    tri = chibi.tris[t]
    p = shares @ v[tri]
    n = shares @ vn[tri]
    n /= np.maximum(np.linalg.norm(n, axis=1), 1e-12)[:, None]
    w = shares @ chibi.weights[tri]
    on_head = w @ head_bones
    way = w @ way_of
    # On the trunk the fur fans out from the middle as it goes down (FAN of the way out at the sides), as on the chest of
    # the art it is after, so the pale front's edge breaks into tufts pointing out and down rather than streaks along it.
    trunk_w = np.clip(1 - w @ limb_bones - on_head, 0, 1)
    way = way + (FAN * np.tanh(p[:, 0] / 0.03) * trunk_w)[:, None] * np.array((1.0, 0, 0))
    from_face = p - nose
    way = way * (1 - on_head)[:, None] + from_face * on_head[:, None]
    way -= np.sum(way * n, axis=1)[:, None] * n
    way /= np.maximum(np.linalg.norm(way, axis=1), 1e-9)[:, None]
    return p, n, w, on_head, way


def paint(chibi, uv, size):
    c = fur_setup(chibi)
    (v, spine, crotch, neck, chin, crown, head_mid, sides, neck_bones) = (c['v'], c['spine'], c['crotch'], c['neck'], c['chin'],
        c['crown'], c['head_mid'], c['sides'], c['neck_bones'])
    grey = np.zeros((size, size), np.float32)
    known = np.zeros((size, size), bool)
    for t, ys, xs, shares in texels(chibi, uv, size):
        p, n, w, on_head, way = fur_at(c, chibi, t, shares)
        tuft = (tongues(p, way, TUFT_ACROSS, TUFT_ALONG, 11) - 0.35) * TUFT_ANGLE

        # The pale fur is where a margin (degrees, as round a limb) is above none, less the tufts: the most of each part's own
        # (smooth_most, so they run into each other in a curve), rather than one part's mask faded into the next's by the bone
        # weights, which left a seam where the belly met the thighs.
        # The trunk: pale down the front, narrowing to nothing below the crotch (PALE_BELOW), where the thighs' takes over.
        share_up = (p[:, 2] - crotch) / (neck - crotch)
        _, off, sway = on_chain(p, spine)
        round_ = angle_from(off, np.array((0, 0, 1.0)), np.array((0, -1.0, 0)))
        half = np.interp(share_up, *zip(*PALE_BELLY))
        margin = half - round_ - np.clip(-share_up, 0, None) * PALE_BELOW
        stripe = np.zeros(len(p))
        lo, hi, count = STRIPE_TRUNK
        reach = (180 - half - 12) * STRIPE_REACH
        from_back = 180 - round_
        s = (share_up - lo) / (hi - lo) * count + STRIPE_SWEEP * np.radians(from_back) + STRIPE_WAVE * streaked(p, way, 0.08, 0.08, 23, 1)
        width = STRIPE_WIDTH * np.clip(1 - from_back / np.maximum(reach, 1), 0, 1) ** 0.7
        width += 0.04 * tuft / TUFT_ANGLE + 0.08 * streaked(p, way, 0.05, 0.05, 37, 1) * (width > 0)
        trunk_stripe = stripes(s, width) * (s > 0.5) * (s < count + 0.5)
        limbs = np.zeros(len(p))
        paws = np.zeros(len(p))

        # The limbs.
        for side, d in sides.items():
            lw = w @ d['legs']
            if lw.max() > 0:
                along, off, lway = on_chain(p, d['leg'])
                # Round the leg from its inner side, but at the hip from between that and the front, where the belly's comes down.
                turn = smooth_edge(PALE_LEG_TURN / 2 - along, PALE_LEG_TURN / 2) * PALE_LEG_FRONT
                r = angle_from(off, lway, np.stack((-d['sign'] * (1 - turn), -turn, np.zeros(len(p))), axis=1))
                lhalf = np.interp(along, *zip(*PALE_LEG))
                margin = smooth_most(margin, lhalf - r - (1 - lw) * PALE_OFF_PART)
                r = angle_from(off, lway, np.array((-d['sign'], 0, 0)))
                outer = (180 - r) / 180
                for lo, hi, count in (STRIPE_THIGH, STRIPE_SHIN):
                    s = (along - lo) / (hi - lo) * count + 0.5 + STRIPE_WAVE * streaked(p, way, 0.06, 0.06, 29, 1) + 0.15 * (1 - outer)
                    width = STRIPE_WIDTH * np.clip((outer - 0.42) / 0.45, 0, 1) ** 0.7 + 0.04 * tuft / TUFT_ANGLE
                    stripe = np.maximum(stripe, stripes(s, width) * (s > 0.5) * (s < count + 0.5) * lw)
            aw = w @ d['arms']
            if aw.max() > 0:
                along, off, away = on_chain(p, d['arm'])
                under = np.array((0, -np.sin(np.radians(PALE_ARM_FORWARD)), -np.cos(np.radians(PALE_ARM_FORWARD))))
                r = angle_from(off, away, under)
                margin = smooth_most(margin, PALE_ARM - r - (1 - aw) * PALE_OFF_PART)
                outer = r / 180
                lo, hi, count = STRIPE_ARM
                s = (along - lo) / (hi - lo) * count + 0.5 + STRIPE_WAVE * streaked(p, way, 0.06, 0.06, 31, 1)
                width = STRIPE_WIDTH * 0.8 * np.clip((outer - PALE_ARM / 180) / (1 - PALE_ARM / 180) * 1.4 - 0.2, 0, 1) ** 0.7 + 0.04 * tuft / TUFT_ANGLE
                stripe = np.maximum(stripe, stripes(s, width) * (s > 0.5) * (s < count + 0.5) * aw)
            # A round patch at the armpit, which the chest's and the arm's underside's both run into.
            pit = d['arm'][0] + np.array((PALE_PIT[0] * d['sign'], PALE_PIT[1], PALE_PIT[2]))
            away = p - pit
            away[:, 1] *= np.where(away[:, 1] > 0, PALE_PIT_BACK, 1.0)   # behind it counts as further, so it doesn't reach round onto the back
            margin = smooth_most(margin, (PALE_PIT_SIZE - np.linalg.norm(away, axis=1)) * PALE_PER_CELL)
            paws += w @ (d['hand'] + d['foot'])
            limbs += w @ d['legs'] + w @ d['arms'] + w @ (d['hand'] + d['foot'])

        # The head: a pale muzzle, round over its top and running down under the chin to the throat's.
        off = p - head_mid
        hr = np.degrees(np.arccos(np.clip(-off[:, 1] / np.maximum(np.hypot(off[:, 0], off[:, 1]), 1e-9), -1, 1)))
        up = (p[:, 2] - chin) / (crown - chin)
        middle, high, wide = PALE_MUZZLE
        muzzle = (1 - np.hypot(hr / wide, np.clip(up - middle, 0, None) / high)) * wide
        # The neck goes with the trunk, so the throat's pale fur runs up the front of it and into the muzzle's under the chin.
        skull = np.clip((on_head - w @ neck_bones) / np.maximum(1 - w @ neck_bones, 1e-6), 0, 1)
        margin = margin - skull * PALE_OFF_PART
        margin = np.where(skull > 0, smooth_most(margin, muzzle - (1 - skull) * PALE_OFF_PART), margin)
        # The throat: pale round the front of the neck from the chest's up to under the chin, by height rather than by the
        # bone weights, as the neck's skin is shared between the trunk's bones and the head's.
        throat = np.where((p[:, 2] > neck - PALE_THROAT_REACH) & (p[:, 2] < chin + PALE_THROAT_REACH), PALE_THROAT - round_, -180)
        margin = smooth_most(margin, throat)
        pale = smooth_edge(margin + tuft, PALE_SOFT)
        pale = pale * (1 - paws) + PALE_PAWS * paws
        stripe = np.maximum(stripe, trunk_stripe * np.clip(1 - limbs - on_head, 0, 1))
        stripe *= (1 - on_head) * (1 - pale) * (1 - np.clip(paws, 0, 1))

        value = PAINT_COAT + (PAINT_PALE - PAINT_COAT) * pale
        value = value + (PAINT_STRIPE - value) * stripe
        detail = 1 + STRAND * strands(p, way) + CLUMP * streaked(p, way, CLUMP_ACROSS, CLUMP_ALONG, 5)
        grey[ys, xs] = np.clip(value * detail, 0, 1)
        known[ys, xs] = True
    return dilate(grey, known, PAD)


# The coat as the game draws it (game).
GAME_LIFT = 0.85          # the painted image raised to this power, lightening the coat and stripes more than the pale fur
GRAIN = 0.12              # how much the strand grain brightens or darkens (HairBuilder.StrandStreak), about a mean of 1
GRAIN_PIXEL = 1 / 364.0   # cells: one pixel of the hair's sprites, as SkinnedFigure.StrandScale lays its grain
GRAIN_LENGTH = 12         # pixels a thin streak changes over (HairBuilder.StreakLength)


def png_write_rgb(path, grey):
    """A grey image written as RGB, row 0 at the top (the game's image loader is surer of RGB than of grey PNGs)."""
    h, w = grey.shape
    a = (np.clip(grey, 0, 1) * 255 + 0.5).astype(np.uint8)
    a = np.repeat(a[:, :, None], 3, axis=2)
    raw = b''.join(b'\0' + a[y].tobytes() for y in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d))
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 6)) + chunk(b'IEND', b''))


def game_image(chibi, uv, painted):
    """The painted image lightened, with the hair's strand grain over every texel the body covers (row 0 at the bottom)."""
    grey = painted[:, :, :3] @ np.array((0.299, 0.587, 0.114))
    size = grey.shape[0]
    out = np.clip(grey, 0, 1) ** GAME_LIFT
    c = fur_setup(chibi)
    texel = []
    for t, ys, xs, shares in texels(chibi, uv, size):
        p, n, w, on_head, way = fur_at(c, chibi, t, shares)
        thin = streaked(p, way, GRAIN_PIXEL, GRAIN_PIXEL * GRAIN_LENGTH, 51, 1)
        wide = streaked(p, way, 3 * GRAIN_PIXEL, 2 * GRAIN_PIXEL * GRAIN_LENGTH, 54, 1)
        out[ys, xs] = np.clip(out[ys, xs] * (1 + GRAIN * (0.6 * thin + 0.4 * wide)), 0, 1)
    # How big a texel is on the body, against the grain's pixel: the grain is lost if a texel is much bigger.
    corners = c['v'][chibi.tris]
    area3 = np.linalg.norm(np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0]), axis=1).sum() / 2
    e1, e2 = uv[:, 1] - uv[:, 0], uv[:, 2] - uv[:, 0]
    area2 = np.abs(e1[:, 0] * e2[:, 1] - e1[:, 1] * e2[:, 0]).sum() / 2 * size * size
    print('GRAIN a texel is %.4f cells on the body, the grain\'s pixel %.4f' % (math.sqrt(area3 / area2), GRAIN_PIXEL))
    return out


def game_places(chibi, uv, chibi_folder):
    """fur.txt's lines: for each of skin.txt's triangles, in its order, its corners (skin.txt's vertex numbers) and their places."""
    sv, sf = [], []
    with open(os.path.join(chibi_folder, 'skin.txt')) as f:
        for line in f:
            q = line.split()
            if q and q[0] == 'v':
                sv.append([float(x) for x in q[1:4]])
            elif q and q[0] == 'f':
                sf.append([int(x) for x in q[1:4]])
    sv = np.array(sv)
    cell = (chibi.places[:, 2].max() - chibi.places[:, 2].min()) / FIGURE_HEIGHT
    game = np.stack((-chibi.places[:, 0], chibi.places[:, 2], -chibi.places[:, 1]), axis=1) / cell
    game[:, 1] -= game[:, 1].min() - sv[:, 1].min()
    d = np.linalg.norm(game[:, None, :] - sv[None, :, :], axis=2)
    to_skin = d.argmin(1)
    print('PLACES vertices matched to skin.txt within %.5f cells' % d.min(1).max())
    if d.min(1).max() > 0.002:
        raise SystemExit('the rig\'s body no longer matches skin.txt: export it first, or make the atlas again')
    by_set = {}
    for t, tri in enumerate(chibi.tris):
        by_set[frozenset(to_skin[tri].tolist())] = (to_skin[tri], uv[t])
    lines = []
    for k, tri in enumerate(sf):
        hit = by_set.get(frozenset(tri))
        if hit is None:
            raise SystemExit('skin.txt\'s triangle %d has no triangle on the atlas' % k)
        corners, places = hit
        lines.append('t %s %s' % (' '.join(str(int(x)) for x in corners), ' '.join('%.6f %.6f' % tuple(q) for q in places)))
    return lines


def game(folder, painted_path, chibi_folder):
    chibi = Chibi()
    uv = np.load(os.path.join(folder, 'fur_atlas.npz'))['uv']
    lines = game_places(chibi, uv, chibi_folder)
    with open(os.path.join(chibi_folder, 'fur.txt'), 'w') as f:
        f.write('# M3AT: the Furskin gene\'s coat (anim_scripts/make_fur_texture.py game), drawn on the body from fur.png.\n')
        f.write('# t a b c ua va ub vb uc vc: one of skin.txt\'s triangles (in its order), its corners (skin.txt\'s vertices) and their places.\n')
        f.write('image fur.png\n')
        f.write('\n'.join(lines) + '\n')
    out = game_image(chibi, uv, image_read(painted_path))
    png_write_rgb(os.path.join(chibi_folder, 'fur.png'), out[::-1])
    print('GAME fur.txt (%d triangles) and fur.png (%d px) in %s' % (len(lines), out.shape[0], chibi_folder))


def save_atlas(folder, chibi, uv, tuft_places, tuft_tris, tuft_uv):
    np.savez(os.path.join(folder, 'fur_atlas.npz'), uv=uv, places=chibi.places, tris=chibi.tris, parts=chibi.parts,
             tuft_places=tuft_places, tuft_tris=tuft_tris, tuft_uv=tuft_uv)


def mesh_from(name, places, tris, uv):
    m = bpy.data.meshes.new(name)
    m.from_pydata(places.tolist(), [], tris.tolist())
    m.update()
    layer = m.uv_layers.new(name='UVMap')
    layer.data.foreach_set('uv', uv.reshape(-1))
    for p in m.polygons:
        p.use_smooth = True
    o = bpy.data.objects.new(name, m)
    bpy.context.scene.collection.objects.link(o)
    return o


def export_one(o, path, **options):
    select_only(o)
    bpy.ops.export_scene.gltf(filepath=path, export_format='GLB', use_selection=True, export_animations=False, export_skins=False, **options)


def fur_material(path, tone):
    """The image times the skin's colour, as the game draws a tinted texture."""
    mat = bpy.data.materials.new('fur')
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    shader = nodes['Principled BSDF']
    shader.inputs['Roughness'].default_value = 1.0
    tex = nodes.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(path, check_existing=True)
    tex.interpolation = 'Linear'
    mix = nodes.new('ShaderNodeMixRGB')
    mix.blend_type = 'MULTIPLY'
    mix.inputs[0].default_value = 1.0
    mix.inputs[2].default_value = tuple(c ** 2.2 for c in tone) + (1,)
    links.new(tex.outputs['Color'], mix.inputs[1])
    links.new(mix.outputs['Color'], shader.inputs['Base Color'])
    return mat


def paint_file(folder, path):
    a = np.load(os.path.join(folder, 'fur_atlas.npz'))
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    body = mesh_from('FurBody', a['places'], a['tris'], a['uv'])
    tufts = mesh_from('FurTufts', a['tuft_places'], a['tuft_tris'], a['tuft_uv'])
    image = bpy.data.images.load(os.path.abspath(path))
    mat = bpy.data.materials.new('Fur')
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    shader = nodes['Principled BSDF']
    shader.inputs['Roughness'].default_value = 1.0
    tex = nodes.new('ShaderNodeTexImage')
    tex.image = image
    tex.location = (-700, 300)
    mix = nodes.new('ShaderNodeMixRGB')
    mix.name = mix.label = 'Tone'
    mix.blend_type = 'MULTIPLY'
    mix.inputs[0].default_value = 1.0
    mix.inputs[2].default_value = tuple(c ** 2.2 for c in SKIN_TONES[0]) + (1,)
    mix.location = (-350, 300)
    links.new(tex.outputs['Color'], mix.inputs[1])
    links.new(mix.outputs['Color'], shader.inputs['Base Color'])
    nodes.active = tex   # the image Texture Paint paints on
    for o in (body, tufts):
        o.data.materials.append(mat)
    tufts.hide_set(True)   # in the way while painting; unhide to see them
    select_only(body)
    bpy.context.scene.tool_settings.image_paint.mode = 'MATERIAL'
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(os.path.abspath(folder), 'fur_paint.blend'), relative_remap=True)


def view(folder, path, prefix, tufted=True):
    """A sheet: front, side and back in the first skin tone, then the front in the others, the tufts on unless tufted is False."""
    a = np.load(os.path.join(folder, 'fur_atlas.npz'))
    for name in ('Body', 'Head', 'Furskin', 'Backpack', 'TribalBackpack'):
        if name in bpy.data.objects:
            bpy.data.objects[name].hide_render = True
    body = mesh_from('FurBody', a['places'], a['tris'], a['uv'])
    tufts = mesh_from('FurTufts', a['tuft_places'], a['tuft_tris'], a['tuft_uv'])
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE'
    sc.render.resolution_x, sc.render.resolution_y = 800, 1200
    sc.render.image_settings.file_format = 'PNG'
    sc.view_settings.view_transform = 'Standard'
    sc.world = bpy.data.worlds.new('w')
    sc.world.use_nodes = True
    sc.world.node_tree.nodes['Background'].inputs[0].default_value = (1, 1, 1, 1)
    sc.world.node_tree.nodes['Background'].inputs[1].default_value = 0.55
    sun = bpy.data.objects.new('sun', bpy.data.lights.new('sun', 'SUN'))
    sun.data.energy = 1.6
    sun.rotation_euler = (math.radians(55), 0, math.radians(-30))
    sc.collection.objects.link(sun)
    cam_data = bpy.data.cameras.new('c')
    cam_data.type = 'ORTHO'
    places = a['places']
    low, high = places.min(0), places.max(0)
    cam_data.ortho_scale = (high[2] - low[2]) * 1.06
    cam = bpy.data.objects.new('c', cam_data)
    sc.collection.objects.link(cam)
    sc.camera = cam
    mid = (low + high) / 2
    shots = []
    for tone, angle in ((0, 0), (0, 90), (0, 180), (1, 0), (2, 0)):
        mat = fur_material(path, SKIN_TONES[tone])
        tufts.hide_render = not tufted
        for o in (body, tufts):
            o.data.materials.clear()
            o.data.materials.append(mat)
        r = math.radians(angle)
        cam.location = Vector(mid) + Vector((math.sin(r), -math.cos(r), 0)) * 60
        cam.rotation_euler = (math.pi / 2, 0, r)
        sun.rotation_euler = (math.radians(55), 0, r - math.radians(30))
        shot = '%s_%d_%d.png' % (prefix, tone, angle)
        sc.render.filepath = shot
        bpy.ops.render.render(write_still=True)
        shots.append(image_read(shot))
    sheet = np.concatenate(shots, axis=1)
    im = bpy.data.images.new('sheet', sheet.shape[1], sheet.shape[0], alpha=True)
    im.pixels.foreach_set(sheet.reshape(-1))
    im.filepath_raw = prefix + '.png'
    im.file_format = 'PNG'
    im.save()
    for shot in ['%s_%d_%d.png' % (prefix, t, g) for t, g in ((0, 0), (0, 90), (0, 180), (1, 0), (2, 0))]:
        os.remove(shot)


def main(a):
    what, folder = a[0], a[1]
    os.makedirs(folder, exist_ok=True)
    if what == 'atlas':
        chibi = Chibi()
        uv = make_atlas(chibi)
        tuft_places, tuft_tris, tuft_uv = tuft_atlas(chibi, uv)
        save_atlas(folder, chibi, uv, tuft_places, tuft_tris, tuft_uv)
        export_one(mesh_from('FurBody', chibi.places, chibi.tris, uv), os.path.join(folder, 'fur_body.glb'))
        export_one(mesh_from('Furskin', tuft_places, tuft_tris, tuft_uv), os.path.join(folder, 'fur_tufts.glb'))
        print('ATLAS %d triangles, %d tufts\' triangles' % (len(chibi.tris), len(tuft_tris)))
    elif what == 'strip':
        chibi = Chibi()
        o = chibi.mesh_object('FurBody')
        export_one(o, a[2], export_materials='NONE', export_texcoords=False)
    elif what == 'baked':
        chibi = Chibi()
        uv = np.load(os.path.join(folder, 'fur_atlas.npz'))['uv']
        grey = bake_meshy(chibi, uv, int(a[3]) if len(a) > 3 else 2048, a[2])
        png_write(os.path.join(folder, 'fur_stripes.png'), grey[::-1])
    elif what == 'painted':
        chibi = Chibi()
        uv = np.load(os.path.join(folder, 'fur_atlas.npz'))['uv']
        grey = paint(chibi, uv, int(a[2]) if len(a) > 2 else 2048)
        png_write(os.path.join(folder, 'fur_painted.png'), grey[::-1])
    elif what == 'game':
        game(folder, a[2], a[3])
    elif what == 'paintfile':
        paint_file(folder, a[2])
    elif what == 'view':
        view(folder, a[2], a[3], '--bare' not in a)
    else:
        sys.exit(__doc__)


main(sys.argv[sys.argv.index('--') + 1:])

