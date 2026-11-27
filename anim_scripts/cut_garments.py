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
import io
import os
import sys

import bpy
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import Prim, extend, image_of, paint, read_glb, to_blender, to_gltf, view_bytes, world_matrix, write

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
OVER = 0.3          # how far across a stand-in corner may be from the body corner it stands for (a shortened face)

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
        scale = paint(pr, {face: [r for r, _ in items] for face, items in found.items()}, islands, picture, place, drop, rows)
    return rows, new_tris, drop, baked, scale


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
