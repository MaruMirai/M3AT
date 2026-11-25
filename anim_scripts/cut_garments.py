"""Makes the body's knife cut in every garment that was derived from the body's faces, in place in the .glb.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/cut_garments.py -- <out folder> [garment ...] [--check] [--from folder]

For the cut Maru made across the belly (body vertices 432, 433 and 434, see CUT), each garment holding the cut's four
old faces whole (found by matching its vertices to the body's, as the garment stands `offset` out from the skin) gets
those faces replaced by the body's own new triangles (NEW_TRIS: Blender's split of the new faces, the one the export
uses), corner to corner as on the body, with no vertex where the cut crosses an old diagonal. Garments that do not hold
them (tank tops and crop tops have their hem there; skirts, shoes, hats ...) are left alone and listed.

A new vertex goes on the garment's edge at the cut's share, every attribute (normal, bone weights, morph targets)
interpolated along it, then moved by what the body's new vertex moved off the straight line between its edge's ends
(per shape key, into the garment's morph target of that name). The texture:
- where a face's old triangles share their UVs across its diagonal (one island), the new triangles take UVs
  interpolated on the same island;
- where they are separate islands (most painted garments: one island a triangle), each new triangle gets an island of
  its own, packed into the image's free space (PACK_PAD clear round it, at the face's own texel density, smaller if
  there's no room), and painted texel by texel from the old islands at the same point of the face, PAD texels past its
  edges so filtering never reaches a neighbour. The garment looks as it did; only its triangles change. The image is
  written back in its own format (JPEG at quality 95, no chroma subsampling).
Vertices nothing uses any more are dropped.

Reads and writes the .glb's JSON and binary directly: its material, skin, nodes and extras are kept as they were (the
buffer is repacked, so the old mesh data goes). Garments are written to <out folder>/<Name>.glb; give the folder
Models/garments to write them in place (git holds the old ones). --check only reports what would be cut.

To use it for another knife cut, change CUT and OLD_FACES (new body vertices and the old edge each lies on, the old faces
they split) and BEFORE (the .blend with the new vertices still on their old edges, to read each share from).
"""
import json
import os
import struct
import sys

import io

import bpy
import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd()
ROOT = os.path.dirname(HERE)
GARMENTS = os.path.join(ROOT, 'Models', 'garments')
BEFORE = os.path.join(ROOT, 'Models', 'unused', 'pawnHumanRig_before_belly.blend')

# New body vertex -> (the old vertex at the share-0 end of its edge, the one at the share-1 end). Its share along the
# edge is read from BEFORE, where it is still a point on it.
CUT = {432: (285, 283), 433: (7, 2), 434: (142, 137)}
# The old faces the cut splits, as cycles of old body vertices in the body's winding.
OLD_FACES = {
    'QR': [2, 283, 285, 7],
    'QL': [137, 142, 285, 283],
    'TR': [7, 28, 2],
    'TL': [142, 137, 163],
}
NEAR = 0.5          # how far, in body units, a garment vertex may be from the body vertex it stands out from
STRAY = 0.03        # how far a vertex's distance from it may be off the garment's usual one and still stand for it
# Garments whose faces stop short of the body's (the dresses' bodices end at the waist seam of the skirt sewn on, above
# the body's row there): the stand-in corners of find_faces are looked for only in these (by the start of the name).
SHORTENED = ('Dress', 'SleevelessDress')
PAD = 4             # texels painted past a new island's edges, so texture filtering stays on its own colours
PACK_PAD = 3        # texels kept clear between a new island (with its PAD) and anything else in the image
OVER = 0.3          # how far across a stand-in corner may be from the body corner it stands for (a shortened face)
CT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}
TYPE_OF = {1: 'SCALAR', 2: 'VEC2', 3: 'VEC3', 4: 'VEC4'}

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
check = '--check' in argv
argv = [a for a in argv if a != '--check']
if '--from' in argv:
    i = argv.index('--from')
    GARMENTS = os.path.abspath(argv[i + 1])
    del argv[i:i + 2]
if not argv:
    sys.exit('cut_garments.py: give an out folder')
out_folder = os.path.abspath(argv[0])
only = {a.lower() for a in argv[1:]}


# ---- the body ----------------------------------------------------------------------------------------------------

body = bpy.data.objects['Body']
if body.mode != 'OBJECT':
    bpy.ops.object.mode_set(mode='OBJECT')
me = body.data
KEYS = {k.name: np.array([tuple(d.co) for d in k.data], dtype=float) for k in me.shape_keys.key_blocks}
BASIS = KEYS['Basis']
WORLD = np.array(body.matrix_world)[:3, 3]
FACES = [list(p.vertices) for p in me.polygons]
N_OLD = min(CUT)                        # the new vertices are the body's last
assert sorted(CUT) == list(range(N_OLD, len(BASIS))), 'the new vertices must be the last of the body'

# Each new vertex's share along its edge, from the .blend that has it on the straight line.
SHARE = {}
if not os.path.exists(BEFORE):
    sys.exit('cut_garments.py: %s is missing (the body before the new vertices were moved)' % BEFORE)
current = bpy.data.filepath
bpy.ops.wm.open_mainfile(filepath=BEFORE)
_before = bpy.data.objects['Body'].data
for n, (a, b) in CUT.items():
    p = np.array(_before.vertices[n].co)
    pa, pb = np.array(_before.vertices[a].co), np.array(_before.vertices[b].co)
    d = pb - pa
    SHARE[n] = float((p - pa).dot(d) / d.dot(d))
    assert np.linalg.norm(pa + d * SHARE[n] - p) < 1e-3, 'vertex %d was not on its edge in %s' % (n, BEFORE)
bpy.ops.wm.open_mainfile(filepath=current)
body = bpy.data.objects['Body']
me = body.data
print('CUT shares', {n: round(t, 4) for n, t in SHARE.items()})

def to_gltf(v):
    """Blender's (x, y, z) as glTF's (x, z, -y)."""
    v = np.asarray(v, dtype=float)
    return np.stack([v[..., 0], v[..., 2], -v[..., 1]], -1)

def to_blender(v):
    v = np.asarray(v, dtype=float)
    return np.stack([v[..., 0], -v[..., 2], v[..., 1]], -1)

# What each new body vertex moved from the straight line between its edge's ends, per shape key (Blender units).
MOVE = {}
for n, (a, b) in CUT.items():
    t = SHARE[n]
    MOVE[n] = {name: k[n] - (k[a] * (1 - t) + k[b] * t) for name, k in KEYS.items()}

def square(cyc):
    """Each old vertex's place in its face's own (u, v): a unit square for a quad, a corner triangle for a triangle."""
    corners = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)] if len(cyc) == 4 else [(0.0, 0.0), (1.0, 0.0), (0.0, 1.0)]
    return {v: np.array(c) for v, c in zip(cyc, corners)}

PARAM = {name: square(cyc) for name, cyc in OLD_FACES.items()}

# The body's own triangles of its new faces (Blender's split, as the export takes them), under the old face each came
# from, in the body's winding.
me.calc_loop_triangles()
NEW_TRIS = {name: [] for name in OLD_FACES}
for t in me.loop_triangles:
    f = FACES[t.polygon_index]
    if not set(f) & set(CUT):
        continue
    # its old face: the one holding all its old corners and the edges its new vertices lie on
    old = [name for name, cyc in OLD_FACES.items() if all(v in cyc if v not in CUT else set(CUT[v]) <= set(cyc) for v in f)]
    assert len(old) == 1, 'new body face %s fits %s old faces' % (f, old)
    NEW_TRIS[old[0]].append(tuple(t.vertices))
for name, cyc in OLD_FACES.items():
    assert len(NEW_TRIS[name]) >= 2, '%s came out as %d triangles' % (name, len(NEW_TRIS[name]))
    for t in NEW_TRIS[name]:
        for n in t:
            assert n not in CUT or set(CUT[n]) <= set(cyc), 'new vertex %d is not on an edge of %s' % (n, name)


# ---- glTF ----------------------------------------------------------------------------------------------------------

def read_glb(path):
    data = open(path, 'rb').read()
    assert data[:4] == b'glTF', path
    off, js, binary = 12, None, b''
    while off < len(data):
        n, kind = struct.unpack('<II', data[off:off + 8])
        chunk = data[off + 8:off + 8 + n]
        if kind == 0x4E4F534A:
            js = json.loads(chunk)
        elif kind == 0x004E4942:
            binary = chunk
        off += 8 + n
    return js, binary

def view_bytes(j, binary, bv, extra=0, length=None):
    v = j['bufferViews'][bv]
    o = v.get('byteOffset', 0) + extra
    return binary[o:o + (length if length is not None else v['byteLength'] - extra)]

def read_accessor(j, binary, ai):
    a = j['accessors'][ai]
    n, dt = NC[a['type']], CT[a['componentType']]
    size = np.dtype(dt).itemsize * n
    if 'bufferView' in a:
        v = j['bufferViews'][a['bufferView']]
        stride = v.get('byteStride', size)
        o = v.get('byteOffset', 0) + a.get('byteOffset', 0)
        if stride == size:
            arr = np.frombuffer(binary[o:o + a['count'] * size], dtype=dt).reshape(a['count'], n).copy()
        else:
            arr = np.stack([np.frombuffer(binary[o + i * stride:o + i * stride + size], dtype=dt) for i in range(a['count'])])
    else:
        arr = np.zeros((a['count'], n), dtype=dt)
    if 'sparse' in a:
        s = a['sparse']
        k = s['count']
        idt = CT[s['indices']['componentType']]
        io = j['bufferViews'][s['indices']['bufferView']].get('byteOffset', 0) + s['indices'].get('byteOffset', 0)
        idx = np.frombuffer(binary[io:io + k * np.dtype(idt).itemsize], dtype=idt)
        vo = j['bufferViews'][s['values']['bufferView']].get('byteOffset', 0) + s['values'].get('byteOffset', 0)
        arr[idx] = np.frombuffer(binary[vo:vo + k * size], dtype=dt).reshape(k, n)
    return arr

def world_matrix(j, mesh):
    """The world matrix of the node that draws the mesh (glTF ignores it for a skinned one, but a garment here is
    written with it as the identity)."""
    nodes = j.get('nodes', [])
    parent = {c: i for i, nd in enumerate(nodes) for c in nd.get('children', [])}
    def local(nd):
        if 'matrix' in nd:
            return np.array(nd['matrix']).reshape(4, 4).T
        x, y, z, w = nd.get('rotation', [0, 0, 0, 1])
        r = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                      [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                      [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
        m = np.eye(4)
        m[:3, :3] = r * np.array(nd.get('scale', [1, 1, 1]))
        m[:3, 3] = nd.get('translation', [0, 0, 0])
        return m
    for i, nd in enumerate(nodes):
        if nd.get('mesh') == mesh:
            m, k = local(nd), i
            while k in parent:
                k = parent[k]
                m = local(nodes[k]) @ m
            return m
    return np.eye(4)


class Prim:
    """One primitive's vertex data as float arrays, with the accessors they came from."""

    def __init__(self, j, binary, mesh_index, prim):
        self.j, self.prim, self.mesh_index = j, prim, mesh_index
        self.attrs = {}
        self.kinds = {}
        for name, ai in prim['attributes'].items():
            raw = read_accessor(j, binary, ai)
            acc = j['accessors'][ai]
            self.kinds[name] = (acc['componentType'], acc.get('normalized', False))
            if name.startswith('WEIGHTS') and acc.get('normalized'):
                raw = raw / float(np.iinfo(raw.dtype).max)
            self.attrs[name] = raw if name.startswith('JOINTS') else raw.astype(float)
        self.targets = []
        for t in prim.get('targets', []):
            self.targets.append({name: read_accessor(j, binary, ai).astype(float) for name, ai in t.items()})
        self.tri = read_accessor(j, binary, prim['indices']).reshape(-1, 3).astype(int) if 'indices' in prim else \
            np.arange(len(self.attrs['POSITION'])).reshape(-1, 3)
        names = j['meshes'][mesh_index].get('extras', {}).get('targetNames', [])
        self.names = names + [None] * (len(self.targets) - len(names))
        self.joint_sets = sorted(k for k in self.attrs if k.startswith('JOINTS'))

    def blend(self, ga, gb, t):
        """The attributes at share t from vertex ga towards gb (plain interpolation; bone weights merged and kept to
        what the sets hold)."""
        row = {}
        for name, arr in self.attrs.items():
            if name.startswith('JOINTS') or name.startswith('WEIGHTS'):
                continue
            row[name] = arr[ga] * (1 - t) + arr[gb] * t
        if 'NORMAL' in row:
            row['NORMAL'] = row['NORMAL'] / max(np.linalg.norm(row['NORMAL']), 1e-9)
        if self.joint_sets:
            infl = {}
            for g, share in ((ga, 1 - t), (gb, t)):
                for s in self.joint_sets:
                    w = self.attrs['WEIGHTS' + s[6:]]
                    for jt, wt in zip(self.attrs[s][g], w[g]):
                        if wt > 0:
                            infl[int(jt)] = infl.get(int(jt), 0.0) + wt * share
            ranked = sorted(infl.items(), key=lambda kv: -kv[1])[:4 * len(self.joint_sets)]
            total = sum(w for _, w in ranked) or 1.0
            ranked = [(jt, w / total) for jt, w in ranked] + [(0, 0.0)] * (4 * len(self.joint_sets))
            for si, s in enumerate(self.joint_sets):
                part = ranked[4 * si:4 * si + 4]
                row[s] = np.array([p[0] for p in part])
                row['WEIGHTS' + s[6:]] = np.array([p[1] for p in part])
        row['targets'] = [{name: arr[ga] * (1 - t) + arr[gb] * t for name, arr in tg.items()} for tg in self.targets]
        return row


def find_faces(pr, M, stand_ins):
    """The garment's triangles under each old body face, {face: [(triangle row, [body vertex of each corner])]} with
    the faces it has none of left out, or (None, why).

    Also each new vertex's share along its edge in this garment.

    A garment vertex stands for the old body vertex it is nearest, if it is as far out from it as the garment stands
    from the body (the commonest distance, within STRAY), so the rings of a skirt sewn on below a bodice don't count. A face whose
    corner the garment doesn't reach (a bodice stopping at a waist seam above the body's row) takes for it the vertex,
    beside the face's other corners, most nearly over the body's corner (within OVER across, above it, below the
    face's other corners; only for garments named in SHORTENED), and the new vertex on such a shortened edge goes level with the body's new vertex rather
    than at its share of the edge."""
    pos = pr.attrs['POSITION']
    world = to_blender(pos @ M[:3, :3].T + M[:3, 3])
    old = BASIS[:N_OLD] + WORLD
    near = {}
    for g in range(len(pos)):
        d = np.linalg.norm(old - world[g], axis=1)
        k = int(d.argmin())
        if d[k] < NEAR:
            near[g] = (k, float(d[k]))
    if not near:
        return None, 'holds none of the four faces'
    ds = np.round(np.array([d for _, d in near.values()]) / 0.01).astype(int)
    offset = float(np.bincount(ds).argmax()) * 0.01   # the distance most of them stand at (the skirt's rings vary)
    label = {g: k for g, (k, d) in near.items() if abs(d - offset) < STRAY}
    tri_of = {}
    for t in pr.tri:
        for v in t:
            tri_of.setdefault(int(v), []).append([int(x) for x in t])
    raised = set()
    found = {}
    for name, cyc in OLD_FACES.items():
        mine = [g for g, k in label.items() if k in cyc]
        have = {label[g] for g in mine}
        if not have:
            continue
        for k in [k for k in cyc if k not in have] if stand_ins else []:
            beside = {v for g in mine for t in tri_of.get(g, []) for v in t if v not in label}
            top = min((world[g][2] for g in mine if label[g] not in raised), default=-1e9)
            want = old[k]
            best = min(beside, key=lambda v: np.linalg.norm((world[v] - want)[:2]), default=None)
            if best is None or np.linalg.norm((world[best] - want)[:2]) > OVER or not want[2] < world[best][2] < top:
                continue
            for v in range(len(pos)):
                if np.linalg.norm(world[v] - world[best]) < 1e-5:
                    label[v] = k
            raised.add(k)
        tris = [(i, [label[int(v)] for v in t]) for i, t in enumerate(pr.tri)
                if all(int(v) in label and label[int(v)] in cyc for v in t) and len({label[int(v)] for v in t}) == 3]
        if tris and len(tris) != len(cyc) - 2:
            return None, '%s holds %d of its %d triangles' % (name, len(tris), len(cyc) - 2)
        if tris:
            found[name] = tris
    if not found:
        return None, 'holds none of the four faces'
    share = {}
    for n, (a, b) in CUT.items():
        name = next((f for f in found if a in OLD_FACES[f] and b in OLD_FACES[f]), None)
        if name is None:
            continue
        t = SHARE[n]
        if a in raised or b in raised:
            corner = {label[v]: world[v] for i, _ in found[name] for v in (int(x) for x in pr.tri[i])}
            pa, pb = corner[a], corner[b]
            t = float((BASIS[n] + WORLD - pa).dot(pb - pa) / (pb - pa).dot(pb - pa))
            if not 0.05 < t < 0.95:
                return None, '%s already has an edge where the cut goes (share %.2f)' % (name, t)
        share[n] = t
    return (found, share, sorted(raised)), None


def image_of(j, prim):
    """The index of the image the primitive's base colour reads, or None."""
    try:
        tex = j['materials'][prim['material']]['pbrMetallicRoughness']['baseColorTexture']['index']
        return j['textures'][tex]['source']
    except (KeyError, IndexError, TypeError):
        return None


def cut(pr, found, share, M, picture):
    """Swaps each found face's triangles for the body's new ones. Returns the new vertex rows, the triangle rows
    removed, the triangles that replace them, the faces given islands of their own, and how small those islands had
    to be made to fit (1 = the face's own texel density). picture is the garment's image as an array (painted into)
    or None."""
    rows = []
    made = {}
    m3 = np.linalg.inv(M[:3, :3])
    count = len(pr.attrs['POSITION'])
    pos = pr.attrs['POSITION']
    uv = pr.attrs.get('TEXCOORD_0')
    def add(row):
        rows.append(row)
        return count + len(rows) - 1
    def edge_row(ga, gb, n):
        row = pr.blend(ga, gb, share[n])
        move = {k: m3 @ to_gltf(v) for k, v in MOVE[n].items()}
        row['POSITION'] = row['POSITION'] + move['Basis']
        for i, name in enumerate(pr.names):
            if name in move:
                row['targets'][i]['POSITION'] = row['targets'][i]['POSITION'] + move[name] - move['Basis']
        return row
    def place(i):
        return rows[i - count]['POSITION'] if i >= count else pos[i]
    new_tris, drop, islands = [], [], []
    baked = []
    for face, items in found.items():
        drop += [tuple(int(v) for v in pr.tri[r]) for r, _ in items]
        copies = {}
        for r, labels in items:
            for v, k in zip(pr.tri[r], labels):
                copies.setdefault(k, [])
                if int(v) not in copies[k]:
                    copies[k].append(int(v))
        one = uv is None or all(np.abs(uv[c] - uv[cs[0]]).max() < 1e-5 for cs in copies.values() for c in cs)
        # the garment's winding against the body's
        r, labels = items[0]
        gp = [to_blender(pos[int(v)] @ M[:3, :3].T) for v in pr.tri[r]]
        bp = [BASIS[k] for k in labels]
        same = np.cross(gp[1] - gp[0], gp[2] - gp[0]).dot(np.cross(bp[1] - bp[0], bp[2] - bp[0])) > 0
        q_of = dict(PARAM[face])
        for n, (a, b) in CUT.items():
            if a in q_of and b in q_of:
                q_of[n] = q_of[a] * (1 - share[n]) + q_of[b] * share[n]
        for bt in NEW_TRIS[face]:
            corners = list(bt) if same else list(bt)[::-1]
            if one:
                tri = []
                for k in corners:
                    if k in CUT:
                        a, b = CUT[k]
                        key = (copies[a][0], copies[b][0], k)
                        if key not in made:
                            made[key] = add(edge_row(copies[a][0], copies[b][0], k))
                        tri.append(made[key])
                    else:
                        tri.append(copies[k][0])
                new_tris.append(tri)
            else:
                tri = []
                for k in corners:
                    if k in CUT:
                        a, b = CUT[k]
                        tri.append(add(edge_row(copies[a][0], copies[b][0], k)))
                    else:
                        tri.append(add(pr.blend(copies[k][0], copies[k][0], 0.0)))
                new_tris.append(tri)
                islands.append((tri, face, [q_of[k] for k in corners]))
        if not one:
            baked.append(face)
    scale = 1.0
    if islands:
        if picture is None:
            raise RuntimeError('separate islands but no image to paint')
        scale = paint(pr, found, islands, picture, place, drop, rows)
    return rows, new_tris, drop, baked, scale


def paint(pr, found, islands, picture, place, drop, rows):
    """Packs each island into the image's free space and paints it from the old islands. Returns the scale used."""
    src = picture.copy()
    H, W = src.shape[:2]
    uv = pr.attrs['TEXCOORD_0']
    count = len(pr.attrs['POSITION'])
    gone = set(drop)
    # what the image holds for the triangles that stay, kept clear of
    mask = Image.new('L', (W, H), 0)
    draw = ImageDraw.Draw(mask)
    for t in pr.tri:
        if tuple(int(v) for v in t) in gone:
            continue
        draw.polygon([(uv[v, 0] * W, uv[v, 1] * H) for v in t], fill=255, outline=255)
    taken = np.array(mask) > 0
    for _ in range(PAD + PACK_PAD):
        grown = taken.copy()
        grown[1:] |= taken[:-1]; grown[:-1] |= taken[1:]; grown[:, 1:] |= taken[:, :-1]; grown[:, :-1] |= taken[:, 1:]
        taken = grown
    taken[:PAD + 1] = taken[-PAD - 1:] = True
    taken[:, :PAD + 1] = taken[:, -PAD - 1:] = True
    # each face's texel density: texels per unit of the garment's surface
    density = {}
    for face, items in found.items():
        area_uv = area_3d = 0.0
        for r, _ in items:
            t = [int(v) for v in pr.tri[r]]
            u = uv[t] * [W, H]
            area_uv += abs(cross2(u[1] - u[0], u[2] - u[0])) / 2
            p = pr.attrs['POSITION'][t]
            area_3d += np.linalg.norm(np.cross(p[1] - p[0], p[2] - p[0])) / 2
        density[face] = np.sqrt(area_uv / area_3d)
    old_tris = {face: [([int(v) for v in pr.tri[r]], labels) for r, labels in items] for face, items in found.items()}
    for scale in (1.0, 0.85, 0.7, 0.55, 0.4):
        free = taken.copy()
        placed = []
        for tri, face, qs in islands:
            p = [place(v) for v in tri]
            e = p[1] - p[0]
            x2 = float(np.dot(p[2] - p[0], e) / np.linalg.norm(e))
            y2 = float(np.linalg.norm(np.cross(e, p[2] - p[0])) / np.linalg.norm(e))
            flat = np.array([[0, 0], [np.linalg.norm(e), 0], [x2, y2]]) * density[face] * scale
            spot = fit(free, flat)
            if spot is None:
                break
            corners, cells = spot
            free |= cells
            placed.append(corners)
        else:
            break
    else:
        raise RuntimeError('no room in the image for the new islands')
    if scale < 1.0:
        print('  (islands at %.0f%% of their texel density, for room)' % (scale * 100))
    for (tri, face, qs), corners in zip(islands, placed):
        for v, c in zip(tri, corners):
            rows[v - count]['TEXCOORD_0'] = np.array([c[0] / W, c[1] / H])
        bake(src, picture, corners, np.array([place(v) for v in tri]), old_tris[face], pr.attrs['POSITION'], uv, W, H)
    return scale


def fit(free, flat):
    """A place for the triangle flat (texel units) where it and PAD round it touch nothing taken: its corners in the
    image's texels and the cells it takes (PAD and PACK_PAD round it), or None. Tries it turned four ways, at the first
    free place reading from the top left."""
    H, W = free.shape
    best = None
    for turn in range(4):
        a = turn * np.pi / 2
        rot = flat @ np.array([[np.cos(a), np.sin(a)], [-np.sin(a), np.cos(a)]])
        rot = rot - rot.min(0) + PAD + PACK_PAD
        w, h = (int(np.ceil(x)) + PAD + PACK_PAD + 1 for x in rot.max(0))
        if w >= W or h >= H:
            continue
        m = Image.new('L', (w, h), 0)
        ImageDraw.Draw(m).polygon([tuple(c) for c in rot], fill=255, outline=255)
        cells = np.array(m) > 0
        for _ in range(PAD + PACK_PAD):
            g = cells.copy()
            g[1:] |= cells[:-1]; g[:-1] |= cells[1:]; g[:, 1:] |= cells[:, :-1]; g[:, :-1] |= cells[:, 1:]
            cells = g
        # overlap with what's taken at every offset, by FFT
        F = np.fft.rfft2(free.astype(np.float32))
        K = np.zeros((H, W), np.float32)
        K[:h, :w] = cells
        hit = np.fft.irfft2(F * np.conj(np.fft.rfft2(K)), s=(H, W))[:H - h, :W - w]
        ys, xs = np.nonzero(hit < 0.5)
        if len(ys) == 0:
            continue
        k = int(np.argmin(ys * W + xs))
        y, x = int(ys[k]), int(xs[k])
        if best is None or (y, x) < best[0]:
            full = np.zeros((H, W), bool)
            full[y:y + h, x:x + w] = cells
            best = ((y, x), rot + [x, y], full)
    return None if best is None else (best[1], best[2])


def bake(src, dst, corners, points, olds, pos, uv, W, H):
    """Paints the island with corners (texels) from the old islands: each texel's point of the new triangle (points:
    its corners on the garment) laid onto the old triangle nearest it, and read from src at the old UV there."""
    lo = np.floor(corners.min(0) - PAD).astype(int)
    hi = np.ceil(corners.max(0) + PAD).astype(int)
    xs, ys = np.meshgrid(np.arange(lo[0], hi[0] + 1), np.arange(lo[1], hi[1] + 1))
    pts = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], 1)
    bary = barycentric(pts, corners)
    a = corners
    heights = []
    for i in range(3):
        j, k = (i + 1) % 3, (i + 2) % 3
        e = a[k] - a[j]
        heights.append(abs(cross2(e, a[i] - a[j])) / np.linalg.norm(e))
    keep = np.min(bary * np.array(heights), 1) >= -PAD     # within PAD texels of the triangle
    pts, bary = pts[keep], bary[keep]
    bary = np.clip(bary, 0, None)
    bary /= bary.sum(1, keepdims=True)
    p3 = bary @ points
    best_m = np.full(len(p3), -np.inf)
    best_uv = np.zeros((len(p3), 2))
    for t, _ in olds:
        b = barycentric3(p3, pos[t])
        m = b.min(1)
        better = m > best_m
        bb = np.clip(b[better], 0, None)
        bb /= bb.sum(1, keepdims=True)
        best_uv[better] = bb @ (uv[t] * [W, H])
        best_m[better] = m[better]
    colour = sample(src, best_uv)
    x = pts[:, 0].astype(int)
    y = pts[:, 1].astype(int)
    ok = (x >= 0) & (x < W) & (y >= 0) & (y < H)
    dst[y[ok], x[ok]] = colour[ok]


def barycentric3(p, tri):
    """Barycentric places of points p laid onto the plane of the 3D triangle tri."""
    a, b, c = tri
    v0, v1 = b - a, c - a
    v2 = p - a
    d00, d01, d11 = v0 @ v0, v0 @ v1, v1 @ v1
    d20, d21 = v2 @ v0, v2 @ v1
    den = d00 * d11 - d01 * d01
    l1 = (d11 * d20 - d01 * d21) / den
    l2 = (d00 * d21 - d01 * d20) / den
    return np.stack([1 - l1 - l2, l1, l2], 1)


def cross2(a, b):
    return a[0] * b[1] - a[1] * b[0]


def barycentric(p, tri):
    a, b, c = tri
    v0, v1 = b - a, c - a
    v2 = p - a
    d = v0[0] * v1[1] - v1[0] * v0[1]
    l1 = (v2[:, 0] * v1[1] - v1[0] * v2[:, 1]) / d
    l2 = (v0[0] * v2[:, 1] - v2[:, 0] * v0[1]) / d
    return np.stack([1 - l1 - l2, l1, l2], 1)


def sample(img, at):
    """Catmull-Rom (bicubic) reads of img at texel places (x, y), pixel centres at +0.5: sharper than bilinear, so
    fine print (a zip's teeth) keeps its edge when copied texel for texel."""
    H, W = img.shape[:2]
    x = at[:, 0] - 0.5
    y = at[:, 1] - 0.5
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    def weights(f):
        f2, f3 = f * f, f * f * f
        return np.stack([-0.5 * f3 + f2 - 0.5 * f, 1.5 * f3 - 2.5 * f2 + 1, -1.5 * f3 + 2 * f2 + 0.5 * f, 0.5 * f3 - 0.5 * f2], 1)
    wx, wy = weights(fx), weights(fy)
    out = np.zeros((len(at), img.shape[2]), np.float32)
    for j in range(4):
        yy = np.clip(y0 - 1 + j, 0, H - 1)
        row = np.zeros_like(out)
        for i in range(4):
            xx = np.clip(x0 - 1 + i, 0, W - 1)
            row += img[yy, xx] * wx[:, i:i + 1]
        out += row * wy[:, j:j + 1]
    return np.clip(out, 0, 255)


def extend(pr, rows, new_tris, drop):
    """The primitive's arrays with the new vertices and triangles, and without vertices nothing uses any more."""
    for name in list(pr.attrs):
        if rows:
            add = np.array([r[name] for r in rows])
            pr.attrs[name] = np.concatenate([pr.attrs[name], add.astype(pr.attrs[name].dtype)])
    for i in range(len(pr.targets)):
        for name in pr.targets[i]:
            if rows:
                add = np.array([r['targets'][i][name] for r in rows])
                pr.targets[i][name] = np.concatenate([pr.targets[i][name], add])
    gone = set(drop)
    keep = [list(map(int, t)) for t in pr.tri if tuple(int(v) for v in t) not in gone]
    assert len(pr.tri) - len(keep) == len(drop), 'removed %d triangles, expected %d' % (len(pr.tri) - len(keep), len(drop))
    tris = np.array(keep + new_tris, dtype=int)
    used = np.unique(tris)
    if len(used) < len(pr.attrs['POSITION']):
        new_index = np.full(len(pr.attrs['POSITION']), -1)
        new_index[used] = np.arange(len(used))
        for name in pr.attrs:
            pr.attrs[name] = pr.attrs[name][used]
        for tg in pr.targets:
            for name in tg:
                tg[name] = tg[name][used]
        tris = new_index[tris]
    pr.tri = tris


def pack(array, kind):
    comp, normalized = kind
    dt = CT[comp]
    if normalized:
        array = np.round(array * np.iinfo(dt).max)
    return np.ascontiguousarray(array.astype(dt)).tobytes()


def write(j, binary, pr, path, image=None):
    """The .glb with the primitive's accessors replaced in place, the buffer repacked with only what is used."""
    j = json.loads(json.dumps(j))
    new = {}    # accessor index -> (bytes, dict of accessor fields)
    def put(ai, array, comp, normalized=False, minmax=False):
        n = array.shape[1]
        acc = {'componentType': comp, 'count': int(len(array)), 'type': TYPE_OF[n]}
        if normalized:
            acc['normalized'] = True
        if minmax:
            acc['min'] = [float(x) for x in array.min(0)]
            acc['max'] = [float(x) for x in array.max(0)]
        new[ai] = (pack(array, (comp, normalized)), acc)
    for name, ai in pr.prim['attributes'].items():
        comp, normalized = pr.kinds[name]
        put(ai, pr.attrs[name], comp, normalized, minmax=(name == 'POSITION'))
    for i, tg in enumerate(pr.prim.get('targets', [])):
        for name, ai in tg.items():
            put(ai, pr.targets[i][name], 5126, minmax=(name == 'POSITION'))
    flat = pr.tri.reshape(-1, 1)
    put(pr.prim['indices'], flat, 5123 if len(pr.attrs['POSITION']) < 65535 else 5125)
    # the accessors replaced may not be shared with anything else
    used = {}
    for m in j['meshes']:
        for p in m['primitives']:
            for ai in list(p['attributes'].values()) + [p.get('indices')] + [a for t in p.get('targets', []) for a in t.values()]:
                used[ai] = used.get(ai, 0) + 1
    assert all(used[ai] == 1 for ai in new), 'a replaced accessor is shared'
    for ai in new:
        old = j['accessors'][ai]
        for key in ('sparse', 'bufferView', 'byteOffset', 'min', 'max', 'normalized'):
            old.pop(key, None)
    # repack: every view still referenced, then the new data
    refs = set()
    for ai, a in enumerate(j['accessors']):
        if ai in new:
            continue
        if 'bufferView' in a:
            refs.add(a['bufferView'])
        if 'sparse' in a:
            refs.add(a['sparse']['indices']['bufferView'])
            refs.add(a['sparse']['values']['bufferView'])
    for im in j.get('images', []):
        if 'bufferView' in im:
            refs.add(im['bufferView'])
    out = bytearray()
    remap = {}
    views = []
    def append(data):
        while len(out) % 4:
            out.append(0)
        at = len(out)
        out.extend(data)
        return at
    swap = {}
    if image is not None:
        swap[j['images'][image[0]]['bufferView']] = image[1]
    for bv in sorted(refs):
        v = dict(j['bufferViews'][bv])
        data = swap.get(bv, view_bytes(j, binary, bv))
        v['byteOffset'] = append(data)
        v['byteLength'] = len(data)
        remap[bv] = len(views)
        views.append(v)
    for ai, (data, acc) in sorted(new.items()):
        views.append({'buffer': 0, 'byteOffset': append(data), 'byteLength': len(data),
                      'target': 34963 if ai == pr.prim['indices'] else 34962})
        j['accessors'][ai].update(acc)
        j['accessors'][ai]['bufferView'] = len(views) - 1
    for ai, a in enumerate(j['accessors']):
        if ai in new:
            continue
        if 'bufferView' in a:
            a['bufferView'] = remap[a['bufferView']]
        if 'sparse' in a:
            a['sparse']['indices']['bufferView'] = remap[a['sparse']['indices']['bufferView']]
            a['sparse']['values']['bufferView'] = remap[a['sparse']['values']['bufferView']]
    for im in j.get('images', []):
        if 'bufferView' in im:
            im['bufferView'] = remap[im['bufferView']]
    j['bufferViews'] = views
    while len(out) % 4:
        out.append(0)
    j['buffers'] = [{'byteLength': len(out)}]
    text = json.dumps(j, separators=(',', ':')).encode()
    text += b' ' * (-len(text) % 4)
    blob = struct.pack('<4sII', b'glTF', 2, 12 + 8 + len(text) + 8 + len(out))
    blob += struct.pack('<II', len(text), 0x4E4F534A) + text + struct.pack('<II', len(out), 0x004E4942) + bytes(out)
    open(path, 'wb').write(blob)


done, skipped = [], []
os.makedirs(out_folder, exist_ok=True)
for fname in sorted(os.listdir(GARMENTS)):
    if not fname.lower().endswith('.glb'):
        continue
    stem = os.path.splitext(fname)[0]
    if only and stem.lower() not in only:
        continue
    j, binary = read_glb(os.path.join(GARMENTS, fname))
    result = None
    for mi, mesh in enumerate(j['meshes']):
        for pi, prim in enumerate(mesh['primitives']):
            if 'POSITION' not in prim['attributes']:
                continue
            pr = Prim(j, binary, mi, prim)
            M = world_matrix(j, mi)
            found, why = find_faces(pr, M, stem.startswith(SHORTENED))
            if found is None:
                result = (why, None)
                continue
            found, share, raised = found
            im = image_of(j, prim)
            picture = None
            if im is not None and 'bufferView' in j['images'][im]:
                raw = bytes(view_bytes(j, binary, j['images'][im]['bufferView']))
                pil = Image.open(io.BytesIO(raw))
                picture = np.asarray(pil.convert('RGBA' if 'A' in pil.mode else 'RGB'), dtype=np.float32)
            rows, new_tris, drop, baked, scale = cut(pr, found, share, M, picture)
            extend(pr, rows, new_tris, drop)
            image = None
            if baked:
                out = io.BytesIO()
                mime = j['images'][im].get('mimeType', 'image/png')
                done_img = Image.fromarray(np.clip(np.round(picture), 0, 255).astype(np.uint8))
                if mime == 'image/jpeg':
                    done_img.save(out, 'JPEG', quality=95, subsampling=0)
                else:
                    done_img.save(out, 'PNG')
                image = (im, out.getvalue())
            result = ('%d triangles%s%s' % (len(pr.tri),
                      ', islands painted for %s%s' % (','.join(baked), '' if scale == 1 else ' at %.0f%%' % (scale * 100)) if baked else '',
                      ', stopping short at body vertices %s' % raised if raised else ''), (pr, mi, pi, image))
            break
        if result and result[1]:
            break
    if result is None or result[1] is None:
        skipped.append((stem, result[0] if result else 'no mesh'))
        continue
    done.append((stem, result[0]))
    if not check:
        write(j, binary, result[1][0], os.path.join(out_folder, fname), result[1][3])
for stem, why in done:
    print('CUT     %-26s %s' % (stem, why))
for stem, why in skipped:
    print('SKIPPED %-26s %s' % (stem, why))
print('%d garments %s, %d left alone' % (len(done), 'would be cut' if check else 'cut into ' + out_folder, len(skipped)))
