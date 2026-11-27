"""Reading, editing and writing a garment's .glb in place, and painting new texture islands from old ones.

Shared by cut_garments.py (the body's knife cuts carried into the garments) and thin_rings.py (every other ring taken
out of a skirt). Reads and writes the .glb's JSON and binary directly, so its material, skin, nodes and extras are kept
as they were (the buffer is repacked, so the old mesh data goes). Needs numpy and PIL (both in Blender's Python).
"""
import io
import json
import struct

import numpy as np
from PIL import Image, ImageDraw

PAD = 4             # texels painted past a new island's edges, so texture filtering stays on its own colours
PACK_PAD = 3        # texels kept clear between a new island (with its PAD) and anything else in the image
CT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}
TYPE_OF = {1: 'SCALAR', 2: 'VEC2', 3: 'VEC3', 4: 'VEC4'}


def to_gltf(v):
    """Blender's (x, y, z) as glTF's (x, z, -y)."""
    v = np.asarray(v, dtype=float)
    return np.stack([v[..., 0], v[..., 2], -v[..., 1]], -1)

def to_blender(v):
    v = np.asarray(v, dtype=float)
    return np.stack([v[..., 0], -v[..., 2], v[..., 1]], -1)

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


def image_of(j, prim):
    """The index of the image the primitive's base colour reads, or None."""
    try:
        tex = j['materials'][prim['material']]['pbrMetallicRoughness']['baseColorTexture']['index']
        return j['textures'][tex]['source']
    except (KeyError, IndexError, TypeError):
        return None



def paint(pr, olds, islands, picture, place, drop, rows, by_distance=False):
    """Packs each island (its new vertex rows, three for a triangle or four for a quad split 0 1 2 and 0 2 3; the
    group of old triangle rows it is painted from; ...) into the
    image's free space, at that group's own texel density, and paints it from those old triangles. rows are the new
    vertices (numbered from the primitive's count), given their texture places here. Returns the scale used."""
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
    for face, items in olds.items():
        area_uv = area_3d = 0.0
        for r in items:
            t = [int(v) for v in pr.tri[r]]
            u = uv[t] * [W, H]
            area_uv += abs(cross2(u[1] - u[0], u[2] - u[0])) / 2
            p = pr.attrs['POSITION'][t]
            area_3d += np.linalg.norm(np.cross(p[1] - p[0], p[2] - p[0])) / 2
        density[face] = np.sqrt(area_uv / area_3d)
    old_tris = {face: [([int(v) for v in pr.tri[r]], None) for r in items] for face, items in olds.items()}
    for scale in (1.0, 0.85, 0.7, 0.55, 0.4):
        free = taken.copy()
        placed = []
        for tri, face, *_ in islands:
            p = [place(v) for v in tri]
            e = p[1] - p[0]
            x2 = float(np.dot(p[2] - p[0], e) / np.linalg.norm(e))
            y2 = float(np.linalg.norm(np.cross(e, p[2] - p[0])) / np.linalg.norm(e))
            flat = [[0, 0], [np.linalg.norm(e), 0], [x2, y2]]
            if len(tri) == 4:
                # a quad (triangles 0 1 2 and 0 2 3): its fourth corner unfolded across the diagonal from the second
                d = p[2] - p[0]
                along = float(np.dot(p[3] - p[0], d) / np.linalg.norm(d))
                off = float(np.linalg.norm(np.cross(d, p[3] - p[0])) / np.linalg.norm(d))
                u = np.array([x2, y2]) / np.hypot(x2, y2)
                n = np.array([u[1], -u[0]])
                if np.dot(n, flat[1]) > 0:          # the diagonal's other side from the second corner
                    n = -n
                flat.append(list(u * along + n * off))
            flat = np.array(flat) * density[face] * scale
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
    for (tri, face, *_), corners in zip(islands, placed):
        for v, c in zip(tri, corners):
            rows[v - count]['TEXCOORD_0'] = np.array([c[0] / W, c[1] / H])
        points = np.array([place(v) for v in tri])
        if len(tri) == 4:
            # each half with its padding, then the first's inside again, as the second's padding reaches over it
            for h, pad in (((0, 1, 2), PAD), ((0, 2, 3), PAD), ((0, 1, 2), 0)):
                bake(src, picture, corners[list(h)], points[list(h)], old_tris[face], pr.attrs['POSITION'], uv, W, H, pad, by_distance)
        else:
            bake(src, picture, corners, points, old_tris[face], pr.attrs['POSITION'], uv, W, H, PAD, by_distance)
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


def bake(src, dst, corners, points, olds, pos, uv, W, H, pad=PAD, by_distance=False):
    """Paints the island with corners (texels) from the old islands: each texel's point of the new triangle (points:
    its corners on the garment) laid onto the old triangle it lies most inside of (by_distance: the one nearest it),
    and read from src at the old UV there. pad texels past its edges are painted too."""
    lo = np.floor(corners.min(0) - pad).astype(int)
    hi = np.ceil(corners.max(0) + pad).astype(int)
    xs, ys = np.meshgrid(np.arange(lo[0], hi[0] + 1), np.arange(lo[1], hi[1] + 1))
    pts = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], 1)
    bary = barycentric(pts, corners)
    a = corners
    heights = []
    for i in range(3):
        j, k = (i + 1) % 3, (i + 2) % 3
        e = a[k] - a[j]
        heights.append(abs(cross2(e, a[i] - a[j])) / np.linalg.norm(e))
    keep = np.min(bary * np.array(heights), 1) >= -pad     # within pad texels of the triangle
    pts, bary = pts[keep], bary[keep]
    bary = np.clip(bary, 0, None)
    bary /= bary.sum(1, keepdims=True)
    p3 = bary @ points
    best_m = np.full(len(p3), -np.inf)
    best_uv = np.zeros((len(p3), 2))
    for t, _ in olds:
        b = barycentric3(p3, pos[t])
        bb = np.clip(b, 0, None)
        bb /= bb.sum(1, keepdims=True)
        if by_distance:
            # how far the point is from the triangle (its place laid onto it, kept inside): for olds from all round a
            # skirt, where a point also lies over the far side's triangles
            m = -np.linalg.norm(bb @ pos[t] - p3, axis=1)
        else:
            m = b.min(1)
        better = m > best_m
        best_uv[better] = bb[better] @ (uv[t] * [W, H])
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
