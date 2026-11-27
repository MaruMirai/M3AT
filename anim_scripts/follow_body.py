"""Moves every garment with the body where the body's surface was reshaped, keeping the garment's own mesh.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/follow_body.py -- <before.blend> <out folder> [garment ...] [--check] [--from folder]

recut_garments.py swaps a re-cut patch's faces into the garments made from the body's faces; it needs the garment to hold
the body's triangles there, and it stops at hand edits (the Pants' cuffs moved onto the shoes, a shirt's shaped
shoulder) and at hand-made garments (armour, the skirts' masters). This instead moves each garment vertex by what the
body's surface under it moved, in 3D, so any garment fits the new shape and keeps its triangles, texture places, image,
weights and hand edits. It doesn't give the garment the patch's new edges (a garment over a new loop bends as it did).

Each garment vertex (within REACH of the old body) is laid on the old body's nearest point q, on an old triangle. If that
triangle is still in the body (its three vertices where they were), the body didn't move there: nothing moves. If not
(the patch: a re-cut, moved or dissolved part), q is laid on the new body's nearest point q' among the triangles that
face the same way (FACING), and the vertex moves by q' - q, faded out from FADE_FROM to REACH off the body. Each morph
target the garment has its own (not all zero) moves by the key's q' - q less the basis's; zero ones the export fits.
Copies of a vertex (texture seams) stand at one place and so move alike; so do two garments meeting at a seam (the
Pants' cuffs on the Shoes' top ring).

The body's units are the rig's; written to <out folder>/<Name>.glb (give Models/garments to write in place; git holds
the old ones). --check only reports. Made for the shoulders and shins of 2026-11-27.
"""
import os
import sys

import bpy
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import Prim, read_glb, to_blender, to_gltf, world_matrix, write

HERE = os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd()
GARMENTS = os.path.join(os.path.dirname(HERE), 'Models', 'garments')

MATCH = 1e-4       # body units: an old vertex this near a new one is it
REACH = 2.5        # body units: garment vertices further than this off the old body don't move
FADE_FROM = 1.0    # ... and from this far they move less, down to nothing at REACH
FACING = 0.3       # the new triangle q' is laid on must face at least this much the old one's way (cosine)
SMALL = 1e-6       # moves under this (body units) count as none
CLEAR = 0.9        # after the move, the garment must stand at least this share of its usual distance off the new body
                   # over the reshaped part, along each body vertex's normal (pushed out where it doesn't)
ROUNDS = 12        # ... in at most this many rounds of pushes
GIVE_UP = 0.7      # ... a body vertex is left once a round cuts its shortfall by less than this share
MOST_PUSH = 1.0    # ... each place pushed at most this many times the garment's usual distance off the body (a ray
                   # past a pushed triangle can meet another behind it; uncapped, 40 rounds took a jacket 0.87 out)
NO_CLEAR = ('Bandolier', 'gorget', 'BodyStrap', 'ShoulderBag', 'scarf', 'collar', 'hatHood', 'Sandals', 'broadwrap',
            'ceremonialCultistMask', 'warVeil')   # straps and
                   # bags kept as they are (no one distance off the body to keep)
SEAMS = {'Pants': 'Shoes'}   # as export_frames.py's: the first's vertices on the second's at rest are put back on them
SEAM_NEAR = 1e-4   # body units: how near they were
DEBUG = None       # FOLLOW_DEBUG=<garment>: print each shortfall the first round finds in it
HIDE_REACH = 0.04 / 0.0424   # export_frames.py's HIDE_REACH and HIDE_LOOK (cells) in body units (its scale, cells a unit)
HIDE_LOOK = 0.25 / 0.0424
CLEAR_KEYS = ('Basis', 'Fat', 'Hulk', 'Thin', 'Female', 'Male', 'Pregnant')   # the shapes it clears the body in
SHELL = 0.5        # ... only for a garment with at least this share of its vertices near the body at its usual distance
# Hard garments keep their own shape: a plate dented where the body slimmed under it would only look warped, and the
# armours hide the body whole. Gear riding the body (packs, shields, the exoframe) and the skeleton likewise.
LEAVE = ('Armor', 'PowerArmor', 'PlateArmor', 'MechlordSuit', 'Helmet', 'CE_Backpack', 'CE_RadioPack',
         'CE_TribalBackpack', 'CE_BallisticShield', 'CE_MeleeShield', 'CE_ExoFrame', 'CE_PlateHelmet', 'Pauldrons',
         'Skeleton')

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
check = '--check' in argv
argv = [a for a in argv if a != '--check']
if '--from' in argv:
    i = argv.index('--from')
    GARMENTS = os.path.abspath(argv[i + 1])
    del argv[i:i + 2]
if len(argv) < 2:
    sys.exit('follow_body.py: give the .blend before the reshape and an out folder')
before_path, out_folder = os.path.abspath(argv[0]), os.path.abspath(argv[1])
only = {a.lower() for a in argv[2:]}


def body_of():
    if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    body = bpy.data.objects['Body']
    me = body.data
    keys = {k.name: np.array([tuple(d.co) for d in k.data], dtype=float) for k in me.shape_keys.key_blocks}
    me.calc_loop_triangles()
    tris = np.array([tuple(t.vertices) for t in me.loop_triangles], dtype=int)
    return keys, tris, np.array(body.matrix_world)[:3, 3]


current = bpy.data.filepath
KEYS, TRIS, WORLD = body_of()
bpy.ops.wm.open_mainfile(filepath=before_path)
OLD_KEYS, OLD_TRIS, OLD_WORLD = body_of()
bpy.ops.wm.open_mainfile(filepath=current)
assert np.allclose(WORLD, OLD_WORLD), 'the body was moved'
NAMES = [k for k in KEYS if k in OLD_KEYS]
NEW, OLD = KEYS['Basis'], OLD_KEYS['Basis']

# old triangles still in the body: all three corners at an unmoved new vertex, and that triangle in the new body
same = {}
for j, p in enumerate(OLD):
    d = np.linalg.norm(NEW - p, axis=1)
    i = int(d.argmin())
    if d[i] < MATCH:
        same[j] = i
new_sets = {frozenset(t) for t in TRIS.tolist()}
KEPT = np.array([all(v in same for v in t) and frozenset(same[v] for v in t) in new_sets for t in OLD_TRIS.tolist()])
# (a kept triangle whose corners' key moves changed still moves in that key; none on 2026-11-27, but report it)
changed_keys = {k: max((np.abs((KEYS[k][same[j]] - NEW[same[j]]) - (OLD_KEYS[k][j] - OLD[j])).max() for j in same), default=0)
                for k in NAMES}
print('follow_body.py: %d of %d old triangles reshaped; key moves changed on kept vertices by at most %s'
      % ((~KEPT).sum(), len(OLD_TRIS), ', '.join('%s %.4f' % kv for kv in changed_keys.items() if kv[1] > 1e-4) or 'nothing'))


def normals(V, T):
    n = np.cross(V[T[:, 1]] - V[T[:, 0]], V[T[:, 2]] - V[T[:, 0]])
    return n / np.maximum(np.linalg.norm(n, axis=1), 1e-12)[:, None]


OLD_N, NEW_N = normals(OLD, OLD_TRIS), normals(NEW, TRIS)
# the new body's vertex normals (area-weighted, as the game's) and the vertices of its reshaped triangles, whose
# clearance under each garment is checked after the move
_area = np.cross(NEW[TRIS[:, 1]] - NEW[TRIS[:, 0]], NEW[TRIS[:, 2]] - NEW[TRIS[:, 0]])
VERT_N = np.zeros_like(NEW)
for _k in range(3):
    np.add.at(VERT_N, TRIS[:, _k], _area)
VERT_N /= np.maximum(np.linalg.norm(VERT_N, axis=1), 1e-12)[:, None]
_old_sets = {frozenset(same.get(v, -1 - v) for v in t) for t in OLD_TRIS.tolist()}
_inv = {i: j for j, i in same.items()}
# (only vertices new or moved: a corner of a re-triangulated face that stayed put has the garment over it as before,
# and the neck's met the jackets' collars from inside, pushing them 0.3 out)
RESHAPED = sorted(v for v in range(len(NEW)) if v not in _inv)
print('follow_body.py: %d body vertices new or moved' % len(RESHAPED))


def closest(P, V, T, allow=None):
    """For each point of P: (distance, triangle, barycentric place) of its nearest point on the triangles T of V;
    allow (points x triangles, bool) limits the triangles each point may take."""
    A, B, C = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    AB, AC = B - A, C - A
    best_d = np.full(len(P), np.inf)
    best_t = np.zeros(len(P), dtype=int)
    best_b = np.zeros((len(P), 3))
    for n, p in enumerate(P):
        AP, BP, CP = p - A, p - B, p - C
        d1, d2 = (AB * AP).sum(1), (AC * AP).sum(1)
        d3, d4 = (AB * BP).sum(1), (AC * BP).sum(1)
        d5, d6 = (AB * CP).sum(1), (AC * CP).sum(1)
        va, vb, vc = d3 * d6 - d5 * d4, d5 * d2 - d1 * d6, d1 * d4 - d3 * d2
        bc = np.zeros((len(T), 3))
        den = va + vb + vc
        den = np.where(np.abs(den) < 1e-18, 1e-18, den)
        bc[:, 1], bc[:, 2] = vb / den, vc / den
        bc[:, 0] = 1 - bc[:, 1] - bc[:, 2]
        # the regions outside the triangle (Ericson's closest point on a triangle), last one wins
        r = (va <= 0) & (d4 - d3 >= 0) & (d5 - d6 >= 0)
        w = (d4 - d3) / np.where(r, (d4 - d3) + (d5 - d6), 1)
        bc[r] = np.stack([np.zeros(r.sum()), 1 - w[r], w[r]], 1)
        r = (vb <= 0) & (d2 >= 0) & (d6 <= 0)
        w = d2 / np.where(r, d2 - d6, 1)
        bc[r] = np.stack([1 - w[r], np.zeros(r.sum()), w[r]], 1)
        r = (vc <= 0) & (d1 >= 0) & (d3 <= 0)
        v = d1 / np.where(r, d1 - d3, 1)
        bc[r] = np.stack([1 - v[r], v[r], np.zeros(r.sum())], 1)
        bc[(d6 >= 0) & (d5 <= d6)] = (0, 0, 1)
        bc[(d3 >= 0) & (d4 <= d3)] = (0, 1, 0)
        bc[(d1 <= 0) & (d2 <= 0)] = (1, 0, 0)
        q = bc[:, :1] * A + bc[:, 1:2] * B + bc[:, 2:] * C
        d = np.linalg.norm(q - p, axis=1)
        if allow is not None:
            d = np.where(allow[n], d, np.inf)
        k = int(d.argmin())
        best_d[n], best_t[n], best_b[n] = d[k], k, bc[k]
    return best_d, best_t, best_b


def moves_for(points):
    """{point index: {key: move}} (body units) for the points (old body's frame) that move."""
    near = np.linalg.norm(points[:, None, :] - OLD[None], axis=2).min(1) < REACH + 2.0
    idx = np.nonzero(near)[0]
    if not len(idx):
        return {}
    d, t, b = closest(points[idx], OLD, OLD_TRIS)
    out = {}
    patch = [n for n in range(len(idx)) if d[n] < REACH and not KEPT[t[n]]]
    if not patch:
        return {}
    q = np.array([b[n] @ OLD[OLD_TRIS[t[n]]] for n in patch])
    allow = (NEW_N @ OLD_N[t[patch]].T).T > FACING
    d2, t2, b2 = closest(q, NEW, TRIS, allow)
    for m, n in enumerate(patch):
        fade = 1.0 if d[n] <= FADE_FROM else max(0.0, (REACH - d[n]) / (REACH - FADE_FROM))
        mv = {}
        for k in NAMES:
            mv[k] = fade * (b2[m] @ KEYS[k][TRIS[t2[m]]] - b[n] @ OLD_KEYS[k][OLD_TRIS[t[n]]])
        if max(np.abs(v).max() for v in mv.values()) > SMALL:
            out[int(idx[n])] = mv
    return out


def clear(pr, M, m3):
    """Pushes the garment out where, over the reshaped part, it stands nearer the new body than CLEAR of its usual
    distance: each reshaped body vertex looks straight out along its normal; if the garment is there but too near, the
    triangle it meets is pushed out along that normal (its corners by their share, so the point met moves the shortfall),
    in rounds until none falls short; in each of CLEAR_KEYS, the garment fitted as the export does, the push made at
    rest. The garment's own triangles were made for the old body, so a vertex moved by hand
    (202, up 0.6) or a new one bulging between its corners (452-454) came right up to it. Returns (most push, places)."""
    from mathutils import Vector
    from mathutils.bvhtree import BVHTree
    pos = pr.attrs['POSITION']
    body = to_blender(pos @ M[:3, :3].T + M[:3, 3]) - WORLD
    places, inverse = np.unique(np.round(body, 6), axis=0, return_inverse=True)
    inverse = inverse.reshape(-1)
    near = np.linalg.norm(places[:, None, :] - NEW[None], axis=2).min(1) < REACH
    if not near.any():
        return 0.0, 0
    d, _, _ = closest(places[near], NEW, TRIS)
    d = d[d < 0.5]
    if not len(d):
        return 0.0, 0
    usual = float(np.bincount(np.round(d / 0.01).astype(int)).argmax()) * 0.01
    if usual < 0.02:   # (the fur's tufts sit on the skin)
        return 0.0, 0
    if (np.abs(d - usual) < 0.03).mean() < SHELL:   # a strap, bag or the parka's quilting: no one distance to keep
        return 0.0, 0
    want = CLEAR * usual
    tris = [tuple(int(inverse[int(v)]) for v in t) for t in pr.tri]
    tris = [t for t in tris if len(set(t)) == 3]
    start = places.copy()
    # each key the body has: the body there, and the garment as the export fits it (each place moving as the body's
    # nearest point does), so the push clears the body in every shape, not only at rest (in Fat the new shoulder
    # vertices came out through the old shirt's triangles)
    _, ct, cb = closest(places, NEW, TRIS)
    shapes = []
    for k in CLEAR_KEYS:
        if k not in KEYS:
            continue
        body_k = KEYS[k]
        fit = np.einsum('ni,nij->nj', cb, body_k[TRIS[ct]] - NEW[TRIS[ct]])
        area = np.cross(body_k[TRIS[:, 1]] - body_k[TRIS[:, 0]], body_k[TRIS[:, 2]] - body_k[TRIS[:, 0]])
        vn = np.zeros_like(body_k)
        for c in range(3):
            np.add.at(vn, TRIS[:, c], area)
        vn /= np.maximum(np.linalg.norm(vn, axis=1), 1e-12)[:, None]
        shapes.append((body_k, vn, fit))
    # the body triangles this garment will leave drawn (the export's hide test at rest, hidden_under: a corner is under
    # it if the garment is within HIDE_REACH and straight out along the skin's normal within HIDE_LOOK); in the other
    # keys only their corners are checked, as skin under a hidden triangle can't show whatever the shape does (in Fat a
    # jacket's armpit came to 0.009 off the merged armpit vertex, which it hides)
    rest_tree = BVHTree.FromPolygons([Vector(p) for p in places], tris)
    def covered(p, n):
        if rest_tree.find_nearest(p, HIDE_REACH)[0] is None:
            return False
        return rest_tree.ray_cast(p - n * HIDE_REACH, n, HIDE_REACH + HIDE_LOOK)[0] is not None
    under = [covered(Vector(NEW[v]), Vector(VERT_N[v])) for v in range(len(NEW))]
    edge = {v for t in TRIS.tolist() if not all(under[i] for i in t) for v in t}
    last, given_up = {}, set()
    for rnd in range(ROUNDS):
        push = {}
        for si, (body_k, vn, fit) in enumerate(shapes):
            tree = BVHTree.FromPolygons([Vector(p) for p in places + fit], tris)
            for v in RESHAPED:
                if si > 0 and v not in edge:
                    continue
                p, n = Vector(body_k[v]), Vector(vn[v])
                hit, _, ti, dist = tree.ray_cast(p, n, usual * 2.5)
                if hit is None or dist >= want - 1e-5:
                    continue
                t = tris[ti]
                bc = np.linalg.lstsq(np.array([places[k] + fit[k] for k in t]).T, np.array(hit), rcond=None)[0]
                bc = np.clip(bc, 0, None)
                bc /= max(bc.sum(), 1e-12)
                short = want - dist
                # a body vertex whose shortfall the last push didn't cut (an armpit's crease, where the ray crosses to
                # the sleeve and pushing it along the ray never opens the gap) is given up on, not pushed to the cap
                if (si, v) in given_up:
                    continue
                if (si, v) in last and short > GIVE_UP * last[(si, v)]:
                    given_up.add((si, v))
                    continue
                last[(si, v)] = short
                if DEBUG and rnd == 0:
                    print('DEBUG %s key %s vertex %d at %s short %.3f (garment %.3f off, wants %.3f)' % (
                        DEBUG, [k for k in CLEAR_KEYS if k in KEYS][si], v, np.round(body_k[v], 2), short, dist, want))
                for k, b in zip(t, bc):
                    step = short * b / float((bc ** 2).sum())
                    if step > push.get(k, (0, None))[0]:
                        push[k] = (step, np.array(VERT_N[v]))
        if not push:
            break
        grew = False
        for k, (step, n) in push.items():
            room = usual * MOST_PUSH - np.linalg.norm(places[k] - start[k])
            if room > 1e-5:
                places[k] += n * min(step, room)
                grew = True
        if not grew:
            break
    moved = np.linalg.norm(places - start, axis=1)
    for g in range(len(pos)):
        k = int(inverse[g])
        if moved[k] > SMALL:
            pos[g] = pos[g] + m3 @ to_gltf(places[k] - start[k])
    return float(moved.max()), int((moved > SMALL).sum())


done = []
os.makedirs(out_folder, exist_ok=True)
for fname in sorted(os.listdir(GARMENTS)):
    if not fname.lower().endswith('.glb'):
        continue
    stem = os.path.splitext(fname)[0]
    if only and stem.lower() not in only:
        continue
    if any(stem.lower().startswith(x.lower()) or x.lower() in stem.lower() for x in LEAVE):
        continue
    j, binary = read_glb(os.path.join(GARMENTS, fname))
    prims = []
    most, count, own = 0.0, 0, set()
    most_push, pushes = 0.0, 0
    for mi, mesh in enumerate(j['meshes']):
        for prim in mesh['primitives']:
            if 'POSITION' not in prim['attributes']:
                continue
            pr = Prim(j, binary, mi, prim)
            M = world_matrix(j, mi)
            m3 = np.linalg.inv(M[:3, :3])
            pos = pr.attrs['POSITION']
            body = to_blender(pos @ M[:3, :3].T + M[:3, 3]) - WORLD
            # one move per place (seam copies alike)
            places, inverse = np.unique(np.round(body, 6), axis=0, return_inverse=True)
            inverse = inverse.reshape(-1)
            mv = moves_for(places)
            if not mv:
                continue
            targets = [(ti, name) for ti, name in enumerate(pr.names)
                       if name in NAMES and 'POSITION' in pr.targets[ti] and np.abs(pr.targets[ti]['POSITION']).max() > 0]
            own |= {name for _, name in targets}
            for g in range(len(pos)):
                m = mv.get(int(inverse[g]))
                if m is None:
                    continue
                count += 1
                most = max(most, float(np.linalg.norm(m['Basis'])))
                pos[g] = pos[g] + m3 @ to_gltf(m['Basis'])
                for ti, name in targets:
                    pr.targets[ti]['POSITION'][g] += m3 @ to_gltf(m[name] - m['Basis'])
            DEBUG = stem if os.environ.get('FOLLOW_DEBUG') == stem else None
            pushed = clear(pr, M, m3) if stem not in NO_CLEAR else (0.0, 0)
            most_push = max(most_push, pushed[0])
            pushes += pushed[1]
            prims.append(pr)
    if not prims:
        continue
    done.append((stem, count, most, sorted(own), most_push, pushes))
    if not check:
        # (write() takes one primitive; garments with several meshes are written one primitive at a time)
        for pr in prims:
            write(j, binary, pr, os.path.join(out_folder, fname))
            j, binary = read_glb(os.path.join(out_folder, fname))
# Seams: a garment meeting another edge to edge (the Pants' cuffs on the Shoes' top ring) were moved and pushed apart;
# each of the first's vertices that stood on one of the second's (in the garments as read) is put on it again.
for first, second in SEAMS.items():
    if check or (only and (first.lower() not in only or second.lower() not in only)):
        continue
    paths = {n: os.path.join(out_folder, n + '.glb') for n in (first, second)}
    if not all(os.path.exists(p) for p in paths.values()):
        continue
    def placed(path):
        jj, bb = read_glb(path)
        mi = 0
        pp = Prim(jj, bb, mi, jj['meshes'][mi]['primitives'][0])
        MM = world_matrix(jj, mi)
        return jj, bb, pp, MM, to_blender(pp.attrs['POSITION'] @ MM[:3, :3].T + MM[:3, 3])
    _, _, _, _, a_before = placed(os.path.join(GARMENTS, first + '.glb'))
    _, _, _, _, b_before = placed(os.path.join(GARMENTS, second + '.glb'))
    ja, ba, pa, Ma, _ = placed(paths[first])
    _, _, pb, Mb, b_now = placed(paths[second])
    gap = np.linalg.norm(a_before[:, None, :] - b_before[None], axis=2)
    k = gap.argmin(1)
    on = gap[np.arange(len(k)), k] < SEAM_NEAR
    ma = np.linalg.inv(Ma[:3, :3])
    for g in np.nonzero(on)[0]:
        pa.attrs['POSITION'][g] = ma @ (to_gltf(b_now[k[g]]) - Ma[:3, 3])
    write(ja, ba, pa, paths[first])
    print('SEAM    %s: %d vertices put back on %s' % (first, int(on.sum()), second))
for stem, count, most, own, most_push, pushes in done:
    print('FOLLOW  %-28s %5d vertices moved, most %.3f%s%s' % (stem, count, most,
          ', %d pushed out to clear the body, most %.3f' % (pushes, most_push) if pushes else '',
          ', own keys moved: ' + ' '.join(own) if own else ''))
print('follow_body.py: %d garments %s' % (len(done), 'would move' if check else 'written to ' + out_folder))
