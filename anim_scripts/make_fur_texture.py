"""Makes the Furskin gene's fur texture: one grey image, multiplied by the skin's colour, on one new atlas shared by the body,
the head and the tufts of Models/garments/Furskin.glb, so that a tuft shows what lies on the skin under it.

    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- atlas <out folder> [size]
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- baked <out folder> <textured.glb> [size]
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- strip <out folder> <out.glb>
    blender -b <copy of the rig .blend> --python anim_scripts/make_fur_texture.py -- view <out folder> <fur.png> <prefix>

The fur's atlas is its own, not the rig's UV map, as it takes in the tufts too: `atlas` makes it and keeps it in <out folder>/fur_atlas.npz; every other
command reads that, so all the images fit one layout. It also writes fur_body.glb and fur_tufts.glb, the meshes with that atlas.

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


def view(folder, path, prefix):
    """A sheet: front, side and back in the first skin tone, then the front in the others, the tufts on."""
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
    elif what == 'view':
        view(folder, a[2], a[3])
    else:
        sys.exit(__doc__)


main(sys.argv[sys.argv.index('--') + 1:])

