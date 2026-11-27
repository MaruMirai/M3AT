"""Carries a re-cut patch of the body into every garment made from the body's faces, in place in the .glb.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/recut_garments.py -- <before.blend> <out folder> [garment ...] [--check] [--from folder]

cut_garments.py handles one knife cut whose new vertices all lie on old edges. This handles any re-cut of a patch of
the body (the chest cut of 2026-11-25): new vertices on old edges or inside old faces, a little off the old surface,
old vertices moved, the old faces re-triangulated, and key moves changed on old vertices (the chest's Female shape). It
compares the rig as it is with <before.blend> (the rig before the re-cut; the vertex order may differ, as old vertices
are matched by place):

- Old vertices are matched to the new body's by place (within MATCH); an old vertex with no match was moved by hand and
  is the unmatched new vertex nearest it (within MOVED). A matched vertex whose place or key moves changed (MOVED, or a
  key reshaped) has that change carried onto every garment vertex standing for it.
- The old faces no longer in the body are the patch. Each new vertex is laid onto the old patch (the nearest point of
  its triangles); what it stands off that point, per key, is carried into the garment as cut_garments.py's MOVE is.
- Each new triangle of the patch has a home: the old triangle its middle lies on. In a garment, the old triangles it
  holds of the patch are swapped for the new ones homed in them: a corner that is an old vertex is the garment's own
  vertex of that triangle, a new one is blended from the home triangle's three corners at its place there (normal,
  weights, morph targets, texture place), so the texture is as it was.
- A new triangle whose corners lie beyond its home (it crosses an old edge) takes each corner from the old triangle
  that corner lies on. If those triangles are one texture island (their shared corners have the same texture places)
  that is all; if not, it gets an island of its own painted from them (glb_edit.paint, by 3D distance), as
  cut_garments.py does.
- A garment holding only some of the patch is cut where it does (a neckline through the patch); one where a new
  triangle crosses from a face it holds to one it doesn't is left alone and listed, as is one holding none of it.

Garments are found as cut_garments.py finds them: each garment vertex stands for the old body vertex nearest it if it
is as far out from it as most of the garment stands (STRAY). Written to <out folder>/<Name>.glb; give Models/garments
to write them in place (copy them aside first: many are not in git). --check only reports.
"""
import io
import os
import sys

import bpy
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd())
from glb_edit import Prim, barycentric3, extend, image_of, paint, read_glb, to_blender, to_gltf, view_bytes, world_matrix, write

HERE = os.path.dirname(os.path.abspath(__file__)) if '__file__' in globals() else os.getcwd()
ROOT = os.path.dirname(HERE)
GARMENTS = os.path.join(ROOT, 'Models', 'garments')

MATCH = 1e-3        # how near, in body units, a new vertex must be to an old one to be it
MOVED = 0.3         # how far an old vertex may have been moved by hand and still be found
CHANGED = 1e-5      # a key move changed by more than this (body units) is carried onto the garments
NEAR = 0.5          # how far, in body units, a garment vertex may be from the body vertex it stands out from
STRAY = 0.03        # how far a vertex's distance from it may be off the garment's usual one and still stand for it
ONE_ISLAND = 1e-5   # texture places this close count as the same
PAINT_NEAR = 1.5    # body units: old triangles whose middle is this near a painted island's are read for it
# Garments cut by hand from others, whose own hems and bands cross the patch (none of their triangles there is a body
# face), cut by overlay() instead: the body's new edges laid over the garment in a front view of the patch. Also those
# whose own long triangles cross the patch (BabyRomper, Leotard), which the swap leaves whole, and the bands (Chestwrap,
# Corset, Sash), whose hems run through the patch.
OVERLAY = ('Tanktop', 'CropTop', 'Bra', 'BabyRomper', 'Leotard', 'Chestwrap', 'Corset', 'Sash')
MERGE = 0.005       # body units, flat: points of the cut this near each other are one (no slivers)
SNAP = 0.12         # body units, flat: a body vertex this near a garment vertex is laid on it
FRONT = -0.3        # a garment vertex is on the patch's front if the body triangle nearest it faces at least this far -y

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
check = '--check' in argv
argv = [a for a in argv if a != '--check']
if '--from' in argv:
    i = argv.index('--from')
    GARMENTS = os.path.abspath(argv[i + 1])
    del argv[i:i + 2]
layout_path = None
if '--swap-first' in argv:   # every garment tries the face swap before the overlay (a quad split the other way: no new
    argv.remove('--swap-first')   # vertices, and the overlay garments already hold the body's triangles there)
    OVERLAY = ()
GONE = False
if '--gone' in argv:   # old vertices may be gone (dissolved or merged by hand): their faces are part of the patch, and
    argv.remove('--gone')   # one moved further than MOVED counts as gone, its new place a new vertex (the shoulders
    GONE = True             # and shins of 2026-11-27)
if '--moved' in argv:
    i = argv.index('--moved')
    MOVED = float(argv[i + 1])
    del argv[i:i + 2]
FOLLOW = '--no-follow' not in argv   # follow() and overlay() lay the patch flat in a front view of the chest; a patch
if not FOLLOW:                        # round the shoulders or shins needs neither (anim_scripts/follow_body.py moves
    argv.remove('--no-follow')        # what the swap can't cut, in 3D)
if '--layout' in argv:   # the rig whose Basis places the vertices over the old surface (before a fairing moved them off it)
    i = argv.index('--layout')
    layout_path = os.path.abspath(argv[i + 1])
    del argv[i:i + 2]
if len(argv) < 2:
    sys.exit('recut_garments.py: give the .blend before the re-cut and an out folder')
before_path, out_folder = os.path.abspath(argv[0]), os.path.abspath(argv[1])
only = {a.lower() for a in argv[2:]}


# ---- the body, now and before ---------------------------------------------------------------------------------------

def body_of():
    if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    body = bpy.data.objects['Body']
    me = body.data
    keys = {k.name: np.array([tuple(d.co) for d in k.data], dtype=float) for k in me.shape_keys.key_blocks}
    polys = [tuple(p.vertices) for p in me.polygons]
    me.calc_loop_triangles()
    tris = [(tuple(t.vertices), t.polygon_index) for t in me.loop_triangles]
    return keys, polys, tris, np.array(body.matrix_world)[:3, 3]

current = bpy.data.filepath
KEYS, POLYS, TRIS, WORLD = body_of()
bpy.ops.wm.open_mainfile(filepath=before_path)
OLD_KEYS, OLD_POLYS, OLD_TRIS, OLD_WORLD = body_of()
LAYOUT = None
if layout_path:
    bpy.ops.wm.open_mainfile(filepath=layout_path)
    LAYOUT = body_of()[0]['Basis']
bpy.ops.wm.open_mainfile(filepath=current)
assert np.allclose(WORLD, OLD_WORLD), 'the body was moved'
NAMES = [k for k in KEYS if k in OLD_KEYS]
BASIS, OLD = KEYS['Basis'], OLD_KEYS['Basis']
# where each vertex lies over the old surface: its place now, or (--layout, after fair_patch.py moved the cut off the old
# surface along its normals) its place in the layout rig; what it stands off that place is carried as OFF and CHANGE
PLACE = BASIS if LAYOUT is None else LAYOUT
assert len(PLACE) == len(BASIS), 'the layout rig has another vertex count'

# new vertex -> old vertex
old_of = {}
for i, p in enumerate(PLACE):
    d = np.linalg.norm(OLD - p, axis=1)
    j = int(d.argmin())
    if d[j] < MATCH:
        old_of[i] = j
for j in sorted(set(range(len(OLD))) - set(old_of.values())):
    free = [i for i in range(len(BASIS)) if i not in old_of]
    i = min(free, key=lambda i: np.linalg.norm(PLACE[i] - OLD[j]))
    if np.linalg.norm(PLACE[i] - OLD[j]) > MOVED and GONE:
        print('GONE old vertex %d (nearest unmatched new one %.3f off)' % (j, np.linalg.norm(PLACE[i] - OLD[j])))
        continue
    if np.linalg.norm(PLACE[i] - OLD[j]) > MOVED:
        sys.exit('recut_garments.py: old vertex %d is gone (nothing within %.2f of it)' % (j, MOVED))
    old_of[i] = j
    print('MOVED old vertex %d (now %d) by %.3f' % (j, i, np.linalg.norm(PLACE[i] - OLD[j])))
new_of = {j: i for i, j in old_of.items()}
NEW = [i for i in range(len(BASIS)) if i not in old_of]

# what changed on the old vertices, per key, in body units: {old vertex: {key: move}}
CHANGE = {}
for i, j in old_of.items():
    ch = {k: KEYS[k][i] - OLD_KEYS[k][j] for k in NAMES}
    if any(np.abs(v).max() > CHANGED for v in ch.values()):
        CHANGE[j] = ch
print('CHANGED %d old vertices (%s)' % (len(CHANGE), ', '.join(sorted({k for c in CHANGE.values() for k, v in c.items()
                                                                     if np.abs(v).max() > CHANGED}))))

# the patch: old faces no longer in the body, and the new faces in their place
mapped = {frozenset(old_of.get(v, -1 - v) for v in p) for p in POLYS}
gone_polys = {pi for pi, p in enumerate(OLD_POLYS) if frozenset(p) not in mapped}
PATCH = [t for t, pi in OLD_TRIS if pi in gone_polys]            # old triangles, old vertex numbers
FACE_OF = {t: pi for t, pi in OLD_TRIS if pi in gone_polys}       # old triangle -> the old face (quad) it was part of
old_sets = {frozenset(p) for p in OLD_POLYS}
new_polys = {pi for pi, p in enumerate(POLYS) if frozenset(old_of.get(v, -1 - v) for v in p) not in old_sets}
NEW_TRIS = [t for t, pi in TRIS if pi in new_polys]               # new triangles, new vertex numbers
print('PATCH %d old triangles -> %d new (%d new vertices)' % (len(PATCH), len(NEW_TRIS), len(NEW)))

def closest(p, tris):
    """The nearest point of the old triangles (old basis) to p: (distance, triangle, barycentric place)."""
    best = None
    for t in tris:
        a, b, c = OLD[list(t)]
        ab, ac, ap = b - a, c - a, p - a
        d1, d2 = ab @ ap, ac @ ap
        bp, cp = p - b, p - c
        d3, d4, d5, d6 = ab @ bp, ac @ bp, ab @ cp, ac @ cp
        va, vb, vc = d3 * d6 - d5 * d4, d5 * d2 - d1 * d6, d1 * d4 - d3 * d2
        if d1 <= 0 and d2 <= 0: bc = (1, 0, 0)
        elif d3 >= 0 and d4 <= d3: bc = (0, 1, 0)
        elif d6 >= 0 and d5 <= d6: bc = (0, 0, 1)
        elif vc <= 0 and d1 >= 0 and d3 <= 0: v = d1 / (d1 - d3); bc = (1 - v, v, 0)
        elif vb <= 0 and d2 >= 0 and d6 <= 0: w = d2 / (d2 - d6); bc = (1 - w, 0, w)
        elif va <= 0 and d4 - d3 >= 0 and d5 - d6 >= 0: w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); bc = (0, 1 - w, w)
        else:
            den = 1 / (va + vb + vc); v, w = vb * den, vc * den; bc = (1 - v - w, v, w)
        bc = np.array(bc, dtype=float)
        d = float(np.linalg.norm(p - bc @ OLD[list(t)]))
        if best is None or d < best[0] - 1e-9:
            best = (d, t, bc)
    return best

# each new vertex on the old patch: its triangle and place there, and what it stands off that point per key
AT, OFF = {}, {}
for i in NEW:
    d, t, bc = closest(PLACE[i], PATCH)
    AT[i] = (t, bc)
    OFF[i] = {k: KEYS[k][i] - bc @ OLD_KEYS[k][list(t)] for k in NAMES}
    print('NEW %d on old triangle %s at %s, %.3f off' % (i, t, np.round(bc, 2), d))
def support(i):
    """The old vertices a new triangle's corner lies between (its own for an old one)."""
    if i in old_of:
        return {old_of[i]}
    t, bc = AT[i]
    return {v for v, b in zip(t, bc) if b > 1e-3}
# each new vertex's weights over the old vertices it lies between, the same whichever face it is built in, so its
# copies either side of a texture seam come out at exactly one place (the game welds them by exact place)
WEIGHT = {}
for i in NEW:
    t, bc = AT[i]
    w = {v: b for v, b in zip(t, bc) if b > 1e-3}
    total = sum(w.values())
    WEIGHT[i] = {v: b / total for v, b in w.items()}
def surface_point(i):
    """The corner's place on the old surface (old basis)."""
    if i in old_of:
        return OLD[old_of[i]]
    t, bc = AT[i]
    return bc @ OLD[list(t)]
HOME = []
for nt in NEW_TRIS:
    mid = np.mean([surface_point(i) for i in nt], axis=0)
    HOME.append(closest(mid, PATCH)[1])
SPANS = [not set().union(*[support(i) for i in nt]) <= set(h) for nt, h in zip(NEW_TRIS, HOME)]
print('HOMES %d new triangles cross an old edge' % sum(SPANS))


# ---- each garment ---------------------------------------------------------------------------------------------------

# ---- the body's move off the old surface, as a field over the patch ------------------------------------------------

FIELD_TRIS = [t for t, _ in OLD_TRIS if np.min(np.abs(OLD[list(t)][:, 2] - 17)) < 3]   # old triangles a garment is laid on


def flat_two(i):
    """Body vertex i's place on the old surface, flat (front view: x, z)."""
    return surface_point(i)[[0, 2]]


BODY_TRIS2 = [[flat_two(i) for i in nt] for nt in NEW_TRIS]


def inside2(p, tri, eps=1e-6):
    """The flat point p's weights on the flat triangle tri, or None if it lies outside (by more than eps)."""
    a, b, c = tri
    d = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])
    if abs(d) < 1e-12:
        return None
    l1 = ((p[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (p[1] - a[1])) / d
    l2 = ((b[0] - a[0]) * (p[1] - a[1]) - (p[0] - a[0]) * (b[1] - a[1])) / d
    w = np.array([1 - l1 - l2, l1, l2])
    return w if w.min() >= -eps else None


def off_at(p):
    """What the new body stands off the old surface at flat place p, per key (None beyond the patch)."""
    for nt, tri in zip(NEW_TRIS, BODY_TRIS2):
        w = inside2(p, tri, 1e-5)
        if w is not None:
            return {k: sum(x * OFF[i][k] for x, i in zip(w, nt) if i in OFF) + 0 * BASIS[0] for k in NAMES}
    return None


def flat_places(pr, M):
    """{garment vertex: its flat place}, for those over the chest's front (nearest old body point facing forward)."""
    pos = pr.attrs['POSITION']
    body = to_blender(pos @ M[:3, :3].T + M[:3, 3]) - WORLD
    flat = {}
    for g in range(len(pos)):
        p = body[g]
        if p[1] > 0 or not 14.5 < p[2] < 20:
            continue
        d, t, bc = closest(p, FIELD_TRIS)
        a, b, c = OLD[list(t)]
        n = np.cross(b - a, c - a)
        if d < NEAR and n[1] / np.linalg.norm(n) < FRONT:
            q = bc @ OLD[list(t)]
            flat[g] = np.array([q[0], q[2]])
    return flat


def follow(pr, M, rows):
    """Every garment vertex over the patch not otherwise moved (a pocket's, a ruffle's, a hem's of the garment's own)
    moved as the body under it moved off the old surface (off_at; fair_patch.py's bulge), with any copy of it among the
    new rows; those standing for a body vertex take its own change (CHANGE) instead. Returns how many moved."""
    label = labels_of(pr, M)
    m3 = np.linalg.inv(M[:3, :3])
    moves = {}
    for g, p in flat_places(pr, M).items():
        if label.get(g) in CHANGE:
            continue
        off = off_at(p)
        if off is not None and max(np.abs(v).max() for v in off.values()) > 1e-6:
            moves[g] = off
    def shift(row, off):
        return moved(pr, row, off, m3)
    for g, off in moves.items():
        row = shift({'POSITION': pr.attrs['POSITION'][g].copy(),
                     'targets': [{n: a[g].copy() for n, a in tg.items()} for tg in pr.targets]}, off)
        pr.attrs['POSITION'][g] = row['POSITION']
        for ti, tg in enumerate(pr.targets):
            for n in tg:
                tg[n][g] = row['targets'][ti][n]
    for r in rows:
        src = int(r['_src']) if '_src' in r else None   # (made an array where copies were made one)
        if src in moves:
            flat = r.get('_flat')
            shift(r, moves[src])
            if flat is not None:
                r['_flat'] = flat
    return len(moves)


def labels_of(pr, M):
    """{garment vertex: old body vertex it stands for}, by cut_garments.find_faces' rule."""
    pos = pr.attrs['POSITION']
    world = to_blender(pos @ M[:3, :3].T + M[:3, 3])
    old = OLD + WORLD
    near = {}
    for g in range(len(pos)):
        d = np.linalg.norm(old - world[g], axis=1)
        k = int(d.argmin())
        if d[k] < NEAR:
            near[g] = (k, float(d[k]))
    if not near:
        return {}
    ds = np.round(np.array([d for _, d in near.values()]) / 0.01).astype(int)
    offset = float(np.bincount(ds).argmax()) * 0.01
    return {g: k for g, (k, d) in near.items() if abs(d - offset) < STRAY}


def blend3(pr, gs, w):
    """The attributes at barycentric place w over garment vertices gs (as Prim.blend, for three)."""
    row = {}
    for name, arr in pr.attrs.items():
        if name.startswith('JOINTS') or name.startswith('WEIGHTS'):
            continue
        row[name] = sum(arr[g] * x for g, x in zip(gs, w))
    if 'NORMAL' in row:
        row['NORMAL'] = row['NORMAL'] / max(np.linalg.norm(row['NORMAL']), 1e-9)
    if pr.joint_sets:
        infl = {}
        for g, share in zip(gs, w):
            for s in pr.joint_sets:
                ws = pr.attrs['WEIGHTS' + s[6:]]
                for jt, wt in zip(pr.attrs[s][g], ws[g]):
                    if wt > 0:
                        infl[int(jt)] = infl.get(int(jt), 0.0) + wt * share
        ranked = sorted(infl.items(), key=lambda kv: -kv[1])[:4 * len(pr.joint_sets)]
        total = sum(x for _, x in ranked) or 1.0
        ranked = [(jt, x / total) for jt, x in ranked] + [(0, 0.0)] * (4 * len(pr.joint_sets))
        for si, s in enumerate(pr.joint_sets):
            part = ranked[4 * si:4 * si + 4]
            row[s] = np.array([p[0] for p in part])
            row['WEIGHTS' + s[6:]] = np.array([p[1] for p in part])
    row['targets'] = [{name: sum(arr[g] * x for g, x in zip(gs, w)) for name, arr in tg.items()} for tg in pr.targets]
    return row


def moved(pr, row, move, m3):
    """row with a body move per key (body units, {key: vector}) added: the basis to its place, the rest to its targets."""
    mv = {k: m3 @ to_gltf(v) for k, v in move.items()}
    row.setdefault('_flat', np.array(row['POSITION'], copy=True))   # its place on the old garment, to paint from
    row['POSITION'] = row['POSITION'] + mv['Basis']
    for ti, name in enumerate(pr.names):
        if name in mv and 'POSITION' in row['targets'][ti]:
            row['targets'][ti]['POSITION'] = row['targets'][ti]['POSITION'] + mv[name] - mv['Basis']
    return row


def recut(pr, M, picture, skip=frozenset()):
    """Recuts one primitive. Returns (rows, new_tris, drop, painted, scale, changed) or (None, why)."""
    label = labels_of(pr, M)
    m3 = np.linalg.inv(M[:3, :3])
    pos = pr.attrs['POSITION']
    uv = pr.attrs.get('TEXCOORD_0')
    held = {}   # old patch triangle -> (garment triangle row, its corners by old body vertex)
    for r, t in enumerate(pr.tri):
        ks = [label.get(int(v)) for v in t]
        if None in ks or len(set(ks)) < 3:
            continue
        for pt in PATCH:
            if set(ks) == set(pt):
                if pt in held:
                    return None, 'holds old triangle %s twice' % (pt,)
                held[pt] = (r, {k: int(v) for k, v in zip(ks, t)})
    if not held:   # (a strap or bag whose few vertices happen to stand at a changed one's distance is left whole)
        return None, 'holds none of the patch'
    for h in skip:   # (left as the garment has them: next to a crack the swap made, see the main loop)
        held.pop(h, None)
    # A new triangle with an old corner on a face the garment doesn't hold (a quad split the other way, where the
    # garment's own hem runs along the old diagonal, so it holds one half): its home is left as the garment has it,
    # and again for any home that then needs it, until none does.
    while True:
        lone = {h for nt, h, sp in zip(NEW_TRIS, HOME, SPANS) if h in held and sp
                for i in nt if i in old_of and old_of[i] not in h
                and not any(old_of[i] in pt for pt in held if pt != h and FACE_OF[pt] == FACE_OF[h])}
        if not lone:
            break
        for h in lone:
            del held[h]
    if not held:
        return None, 'holds none of the patch whole'
    changed = [g for g, k in label.items() if k in CHANGE]
    # every new triangle homed in a held face must have every face its corners lie on held too
    for nt, h, sp in zip(NEW_TRIS, HOME, SPANS):
        if h in held and sp:
            for i in nt:
                if i not in old_of and AT[i][0] not in held and not any(support(i) <= set(pt) for pt in held):
                    return None, 'a new triangle crosses from held %s to one it does not hold (new vertex %d at %s on old %s, %.3f off)' % (
                        h, i, np.round(PLACE[i], 2), AT[i][0], np.linalg.norm(OFF[i]['Basis']))
    count = len(pos)
    rows = []
    def add(row):
        rows.append(row)
        return count + len(rows) - 1
    def place(v):
        return rows[v - count]['POSITION'] if v >= count else pos[v]
    def flat_place(v):
        """Where a vertex was on the old garment (before any move off it), for painting from the old texture."""
        return rows[v - count].get('_flat', rows[v - count]['POSITION']) if v >= count else pos[v]
    def corner_in(pt, i):
        """A row (or garment vertex) for body corner i laid on held old triangle pt."""
        r, by_k = held[pt]
        if i in old_of:
            return by_k[old_of[i]]
        if support(i) <= set(pt):
            w = np.array([WEIGHT[i].get(k, 0.0) for k in pt])
        else:
            w = barycentric3(surface_point(i)[None, :], OLD[list(pt)])[0]
            w = np.clip(w, 0, None)
            w /= w.sum()
        return moved(pr, blend3(pr, [by_k[k] for k in pt], w), OFF[i], m3)
    def face_of(i, home):
        """The held old triangle body corner i is taken from: the home if it lies on it, else the one it lies on."""
        if support(i) <= set(home):
            return home
        if i not in old_of and AT[i][0] in held:
            return AT[i][0]
        return next(pt for pt in held if support(i) <= set(pt))
    made = {}
    of_body = {}   # new body vertex -> its rows, (the face it was built in)
    new_tris, islands, painted = [], [], []
    for nt, h, sp in zip(NEW_TRIS, HOME, SPANS):
        if h not in held:
            continue
        r, by_k = held[h]
        gt = [int(v) for v in pr.tri[r]]
        gp = [to_blender(pos[v] @ M[:3, :3].T) for v in gt]
        bp = [OLD[k] for k in (next(k for k, v in by_k.items() if v == g) for g in gt)]
        same = np.cross(gp[1] - gp[0], gp[2] - gp[0]).dot(np.cross(bp[1] - bp[0], bp[2] - bp[0])) > 0
        corners = list(nt) if same else list(nt)[::-1]
        faces = [face_of(i, h) for i in corners]
        one = True
        if uv is not None and len(set(faces)) > 1:
            # one island if the faces' shared old corners have the same texture place in each
            for a in set(faces):
                for b in set(faces):
                    for k in set(a) & set(b):
                        if np.abs(uv[held[a][1][k]] - uv[held[b][1][k]]).max() > ONE_ISLAND:
                            one = False
        tri = []
        for i, f in zip(corners, faces):
            got = corner_in(f, i)
            if isinstance(got, dict):
                if one:
                    key = (i, f)
                    if key not in made:
                        made[key] = add(got)
                        of_body.setdefault(i, []).append((made[key], f))
                    tri.append(made[key])
                else:
                    tri.append(add(got))
                    of_body.setdefault(i, []).append((tri[-1], f))
            else:
                if one:
                    tri.append(got)
                else:   # a copy of its own for the new island (with its change, as the original gets it below)
                    copy = blend3(pr, [got], [1.0])
                    copy['_src'] = got
                    if label.get(got) in CHANGE:
                        copy = moved(pr, copy, CHANGE[label[got]], m3)
                    tri.append(add(copy))
        new_tris.append(tri)
        if not one:
            key = tuple(sorted(set(faces)))
            islands.append((tri, key))
            painted.append(key)
    drop = [tuple(int(v) for v in pr.tri[r]) for r, _ in held.values()]
    # a kept triangle (one the patch's faces don't cover: a neckline band of the garment's own) along an edge the cut
    # put new vertices on is split at them, or the garment would crack there
    held_rows = {r for r, _ in held.values()}
    for r, t in enumerate(pr.tri):
        if r in held_rows:
            continue
        t = [int(v) for v in t]
        splits = []
        for e in range(3):
            ga, gb = t[e], t[(e + 1) % 3]
            ka, kb = label.get(ga), label.get(gb)
            if ka is None or kb is None:
                continue
            on = sorted((WEIGHT[i][kb], i) for i in of_body if set(WEIGHT[i]) == {ka, kb})
            if on:
                splits.append((e, on))
        if not splits:
            continue
        if len(splits) > 1:
            return None, 'a kept triangle %s has new vertices on two edges' % (t,)
        e, on = splits[0]
        ga, gb, gc = t[e], t[(e + 1) % 3], t[(e + 2) % 3]
        chain = [ga]
        for share, i in on:
            row = moved(pr, pr.blend(ga, gb, share), OFF[i], m3)
            chain.append(add(row))
            of_body[i].append((chain[-1], None))
        chain.append(gb)
        for x, y in zip(chain, chain[1:]):
            new_tris.append([x, y, gc])
        drop.append(tuple(t))
    # every copy of a new vertex at exactly one place, with one normal, weights and key moves (its texture places stay
    # its own): those of the face it lies on, else the first
    for i, copies in of_body.items():
        home = next((v for v, f in copies if f is not None and i in AT and f == AT[i][0]), copies[0][0])
        src = rows[home - count]
        for v, _ in copies:
            dst = rows[v - count]
            for name in dst:
                if name not in ('TEXCOORD_0', 'targets'):   # (_flat too: one place before moves as well)
                    dst[name] = np.array(src[name], copy=True)
            dst['targets'] = [{n: np.array(a, copy=True) for n, a in tg.items()} for tg in src['targets']]
    scale = 1.0
    if islands:
        if picture is None:
            return None, 'separate islands but no image to paint'
        # painted from every old triangle near the island (nearest in 3D wins): its corners can lie over faces beyond
        # the patch (an old vertex moved by hand), and one of the patch faces read at its edge gives its padding
        mids = np.array([pos[[int(v) for v in t]].mean(0) for t in pr.tri])
        olds = {}
        for tri, key in islands:
            mid = np.mean([flat_place(v) for v in tri], axis=0)
            olds[key] = sorted(set(olds.get(key, [])) | set(np.nonzero(np.linalg.norm(mids - mid, axis=1) < PAINT_NEAR)[0].tolist()))
        # (no triangle counts as dropped for finding free space: most new triangles read the old ones' texture)
        scale = paint(pr, olds, islands, picture, flat_place, [], rows, by_distance=True)
    # the old vertices whose body place or keys changed (every copy of them, kept and new)
    for g in changed:
        row = {'POSITION': pr.attrs['POSITION'][g].copy(),
               'targets': [{n: a[g].copy() for n, a in tg.items()} for tg in pr.targets]}
        row = moved(pr, row, CHANGE[label[g]], m3)
        pr.attrs['POSITION'][g] = row['POSITION']
        for ti, tg in enumerate(pr.targets):
            for n in tg:
                tg[n][g] = row['targets'][ti][n]
    return (rows, new_tris, drop, painted, scale, len(changed)), None


# keys reshaped on old vertices (beyond a move of the vertex itself, which every key shares): the chest's Female
RESHAPED = sorted({k for c in CHANGE.values() for k in NAMES if k != 'Basis' and np.abs(c[k] - c['Basis']).max() > CHANGED})
print('RESHAPED keys %s' % RESHAPED)
COPY = 0.02         # a garment's key counts as a copy of the old body's if no vertex standing for a body vertex is further off it


def open_segments(pr):
    """The garment's open edges (welded by place), as pairs of points."""
    pos = pr.attrs['POSITION']
    at = {}
    ids = [at.setdefault(tuple(np.round(x, 4)), len(at)) for x in pos]
    first = {}
    for i, k in enumerate(ids):
        first.setdefault(k, i)
    count = {}
    for t in pr.tri:
        a = [ids[int(v)] for v in t]
        for x, y in ((a[0], a[1]), (a[1], a[2]), (a[2], a[0])):
            e = (min(x, y), max(x, y))
            count[e] = count.get(e, 0) + 1
    return [(pos[first[x]], pos[first[y]]) for (x, y), c in count.items() if c == 1]


def cracks(before, pr, tol=0.002, out=None):
    """How many of the recut garment's open edges don't lie along one it had: a crack where the swap met triangles
    it didn't recognise (a neckline or hem of the garment's own through the patch)."""
    n = 0
    for seg in open_segments(pr):
        ok = False
        for a, b in before:
            d = b - a
            if all(np.linalg.norm(a + d * np.clip((p - a) @ d / (d @ d), 0, 1) - p) <= tol for p in seg):
                ok = True
                break
        n += not ok
        if not ok and out is not None:
            out.append(seg)
    return n


def keys_only(pr, M):
    """For a garment the swap can't cut cleanly: its triangles kept, and each reshaped key that is only a copy of the
    old body's zeroed, so the export fits it from the body as it is now. Returns what was done."""
    label = labels_of(pr, M)
    m3 = np.linalg.inv(M[:3, :3])
    out = []
    for ti, name in enumerate(pr.names):
        if name not in RESHAPED or 'POSITION' not in pr.targets[ti]:
            continue
        tg = pr.targets[ti]['POSITION']
        if not np.abs(tg).max() > 0:
            out.append('%s fitted already' % name)
            continue
        off = max(np.linalg.norm(tg[g] - m3 @ to_gltf(OLD_KEYS[name][k] - OLD[k])) for g, k in label.items())
        if off < COPY:
            for n in pr.targets[ti]:
                pr.targets[ti][n][:] = 0
            out.append('own %s (a copy of the old body\'s, %.3f off) zeroed, to be fitted' % (name, off))
        else:
            out.append('KEEPS its own %s (%.3f off the old body\'s): check it over the new chest' % (name, off))
    return ', '.join(out) or 'no reshaped key of its own (fitted from the body)'


def overlay(pr, M, picture):
    """For a garment cut by hand (OVERLAY), whose own hems and bands cross the patch: its triangles over the patch
    replaced by the body's new triangles, clipped by the garment's edges there (its hems and necklines, and where the
    region meets the rest of the garment). Garment and body are laid flat in a front view (x, z) of the patch, each
    garment vertex at its nearest point of the old body, and cut together by a constrained triangulation
    (mathutils.geometry.delaunay_2d_cdt: the region's outline as faces, the body's new edges as constraints), so the
    garment's own edges inside the region go and the body's take their place. Each point is placed on the garment as
    the old garment triangle it lies in has it (place, weights, keys), moved by what the body stands off the old surface
    there (OFF). A new triangle inside one old garment triangle takes its texture places from it; one that crosses an
    old edge gets an island painted from the old garment (as recut() does). Returns as recut()."""
    from mathutils import Vector
    from mathutils.geometry import delaunay_2d_cdt
    label = labels_of(pr, M)
    m3 = np.linalg.inv(M[:3, :3])
    pos = pr.attrs['POSITION']
    uv = pr.attrs.get('TEXCOORD_0')
    count = len(pos)
    body = to_blender(pos @ M[:3, :3].T + M[:3, 3]) - WORLD
    # each garment vertex near the chest's front: its place laid flat (its nearest point of the old body)
    near_tris = [t for t, _ in OLD_TRIS if np.min(np.abs(OLD[list(t)][:, 2] - 17)) < 3]
    flat = {}
    for g in range(count):
        p = body[g]
        if p[1] > 0 or not 14.5 < p[2] < 20:
            continue
        d, t, bc = closest(p, near_tris)
        a, b, c = OLD[list(t)]
        n = np.cross(b - a, c - a)
        if d < NEAR and n[1] / np.linalg.norm(n) < FRONT:
            q = bc @ OLD[list(t)]
            flat[g] = np.array([q[0], q[2]])
    two = lambda i: surface_point(i)[[0, 2]]
    body_tris = [[two(i) for i in nt] for nt in NEW_TRIS]
    def inside(p, tri, eps=1e-6):
        a, b, c = tri
        d = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])
        if abs(d) < 1e-12:
            return None
        l1 = ((p[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (p[1] - a[1])) / d
        l2 = ((b[0] - a[0]) * (p[1] - a[1]) - (p[0] - a[0]) * (b[1] - a[1])) / d
        w = np.array([1 - l1 - l2, l1, l2])
        return w if w.min() >= -eps else None
    def off_at(p):
        """What the new body stands off the old surface at flat place p, per key (None beyond the patch)."""
        for nt, tri in zip(NEW_TRIS, body_tris):
            w = inside(p, tri, 1e-5)
            if w is not None:
                return {k: sum(x * OFF[i][k] for x, i in zip(w, nt) if i in OFF) + 0 * BASIS[0] for k in NAMES}
        return None
    # the garment's triangles over the patch
    region = []
    for r, t in enumerate(pr.tri):
        t = [int(v) for v in t]
        if any(v not in flat for v in t):
            continue
        tri2 = [flat[v] for v in t]
        probe = [np.mean(tri2, 0)] + [0.8 * x + 0.2 * np.mean(tri2, 0) for x in tri2]
        if any(any(inside(q, bt) is not None for bt in body_tris) for q in probe) or \
                any(inside(two(i), tri2) is not None for nt in NEW_TRIS for i in nt if i not in old_of):
            region.append(r)
    if not region:
        return None, 'nothing over the patch'
    # welded by place (copies split at texture seams are one point)
    weld, wid = {}, {}
    for r in region:
        for v in pr.tri[r]:
            v = int(v)
            wid[v] = weld.setdefault(tuple(np.round(pos[v], 5)), len(weld))
    verts2 = [None] * len(weld)
    for v, w in wid.items():
        verts2[w] = Vector(flat[v])
    nb = len(verts2)
    # the region's outline (edges one of its triangles has), as loops, anticlockwise flat
    directed = {}
    for r in region:
        t = [wid[int(v)] for v in pr.tri[r]]
        a, b, c = (verts2[x] for x in t)
        if (b - a).cross(c - a) < 0:
            t = t[::-1]
        for x, y in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            directed[(x, y)] = directed.get((x, y), 0) + 1
    outline = {x: y for (x, y) in directed if (y, x) not in directed}
    loops = []
    while outline:
        start = next(iter(outline))
        loop = [start]
        nxt = outline.pop(start)
        while nxt != start:
            if nxt not in outline:
                return None, 'the region\'s outline is not a closed loop'
            loop.append(nxt)
            nxt = outline.pop(nxt)
        loops.append(loop)
    body_ids = sorted({i for nt in NEW_TRIS for i in nt})
    bidx = {}
    for i in body_ids:   # a body vertex at a garment vertex (within SNAP, flat) is that vertex, so no sliver between
        p = two(i)
        d = [np.linalg.norm(np.array(v) - p) for v in verts2[:nb]]
        k = int(np.argmin(d))
        if d[k] < SNAP:
            bidx[i] = k
        else:
            bidx[i] = len(verts2)
            verts2.append(Vector(p))
    edges = sorted({tuple(sorted((bidx[a], bidx[b]))) for nt in NEW_TRIS for a, b in ((nt[0], nt[1]), (nt[1], nt[2]), (nt[2], nt[0]))
                    if bidx[a] != bidx[b]})
    out_v, out_e, out_f, orig_v, orig_e, orig_f = delaunay_2d_cdt(verts2, edges, loops, 1, MERGE)
    gtris = [([int(v) for v in pr.tri[r]], [flat[int(v)] for v in pr.tri[r]]) for r in region]
    def home_of(p, eps=1e-6, nearest=False):
        """The old garment triangle (of the region) flat place p lies in, and its weights there (nearest: the one it lies
        most nearly in, for a corner of a triangle the garment holds that falls a little beyond its outline)."""
        best = None
        for gt, g2 in gtris:
            w = inside(p, g2, 1e9)   # (any place: the one it lies most inside of wins)
            if w is not None and (best is None or w.min() > best[1].min()):
                best = (gt, w)
        return best if best is not None and (nearest or best[1].min() > -1e-3) else None
    def turned(gt):
        """Whether the garment triangle winds clockwise flat (its new triangles then turned to match)."""
        a, b, c = (flat[v] for v in gt)
        return (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1]) < 0
    rows, new_tris, islands, painted, made, of_out = [], [], [], [], {}, {}
    def add(row):
        rows.append(row)
        return count + len(rows) - 1
    def point(ov, gt, w):
        """A row (or the garment's own vertex) for output point ov in garment triangle gt at weights w."""
        own = [v for v in gt if wid[v] in orig_v[ov]]
        if own:
            return own[0]
        w = np.clip(w, 0, None)
        w /= w.sum()
        row = blend3(pr, gt, w)
        off = off_at(np.array(out_v[ov]))
        if off is not None:
            row = moved(pr, row, off, m3)
        return add(row)
    for f in out_f:
        f = list(f)
        ps = [np.array(out_v[ov]) for ov in f]
        mid = np.mean(ps, 0)
        home = home_of(mid)
        if home is None:   # (the body's own edges close loops beyond the garment too: those triangles aren't its)
            continue
        gt, _ = home
        g2 = [flat[v] for v in gt]
        ws = [inside(p, g2, 1e-4) for p in ps]
        one = all(w is not None for w in ws)
        tri = []
        for ov, p, w in zip(f, ps, ws):
            if one:
                key = (ov, tuple(gt))
                if key not in made:
                    made[key] = point(ov, gt, w)
                    if made[key] >= count:
                        of_out.setdefault(ov, []).append(made[key])
                tri.append(made[key])
            else:   # crosses an old edge: each corner from the triangle it lies in, an island of its own painted
                hg, hw = home_of(p, nearest=True)
                got = point(ov, hg, hw)
                if got < count:
                    copy = blend3(pr, [got], [1.0])
                    copy['_src'] = got
                    if label.get(got) in CHANGE:
                        copy = moved(pr, copy, CHANGE[label[got]], m3)
                    got = add(copy)
                of_out.setdefault(ov, []).append(got)
                tri.append(got)
        if turned(gt):
            tri = tri[::-1]
        new_tris.append(tri)
        if not one:
            islands.append((tri, ('overlay', len(islands))))
            painted.append(len(islands))
    drop = [tuple(int(v) for v in pr.tri[r]) for r in region]
    # a triangle beyond the region along an edge the cut put points on is split at them (else it would crack there)
    used = {ov for f in out_f if home_of(np.mean([np.array(out_v[x]) for x in f], 0)) is not None for ov in f}
    in_region = set(region)
    for r, t in enumerate(pr.tri):
        if r in in_region:
            continue
        t = [int(v) for v in t]
        for e in range(3):
            ga, gb = t[e], t[(e + 1) % 3]
            if ga not in flat or gb not in flat:
                continue
            a, b = flat[ga], flat[gb]
            d = b - a
            on = []
            for ov in used:
                p = np.array(out_v[ov])
                if any(wid.get(v) in orig_v[ov] for v in (ga, gb) if v in wid):
                    continue
                sh = float((p - a) @ d / (d @ d))
                if 1e-4 < sh < 1 - 1e-4 and np.linalg.norm(a + d * sh - p) < 2 * MERGE:   # (the CDT snaps points to edges within MERGE)
                    on.append((sh, ov))
            if not on:
                continue
            gc = t[(e + 2) % 3]
            chain = [ga]
            for sh, ov in sorted(on):
                row = pr.blend(ga, gb, sh)
                off = off_at(np.array(out_v[ov]))
                if off is not None:
                    row = moved(pr, row, off, m3)
                chain.append(add(row))
                of_out.setdefault(ov, []).append(chain[-1])
            chain.append(gb)
            for x, y in zip(chain, chain[1:]):
                new_tris.append([x, y, gc])
            drop.append(tuple(t))
            break
    for ov, copies in of_out.items():   # one place, normal, weights and keys for every copy of a point
        src = rows[copies[0] - count]
        for v in copies[1:]:
            dst = rows[v - count]
            for name in dst:
                if name not in ('TEXCOORD_0', 'targets'):   # (_flat too: one place before moves as well)
                    dst[name] = np.array(src[name], copy=True)
            dst['targets'] = [{n: np.array(a, copy=True) for n, a in tg.items()} for tg in src['targets']]
    scale = 1.0
    if islands:
        if picture is None:
            return None, 'separate islands but no image to paint'
        def flat_place(v):
            return rows[v - count].get('_flat', rows[v - count]['POSITION']) if v >= count else pos[v]
        mids = np.array([pos[[int(v) for v in t]].mean(0) for t in pr.tri])
        olds = {}
        for tri, key in islands:
            mid = np.mean([flat_place(v) for v in tri], axis=0)
            olds[key] = np.nonzero(np.linalg.norm(mids - mid, axis=1) < PAINT_NEAR)[0].tolist()
        scale = paint(pr, olds, islands, picture, flat_place, [], rows, by_distance=True)
    changed = [g for g, k in label.items() if k in CHANGE]
    for g in changed:
        row = {'POSITION': pr.attrs['POSITION'][g].copy(),
               'targets': [{n: a[g].copy() for n, a in tg.items()} for tg in pr.targets]}
        row = moved(pr, row, CHANGE[label[g]], m3)
        pr.attrs['POSITION'][g] = row['POSITION']
        for ti, tg in enumerate(pr.targets):
            for n in tg:
                tg[n][g] = row['targets'][ti][n]
    return (rows, new_tris, drop, painted, scale, len(changed)), None


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
            im = image_of(j, prim)
            picture = None
            if im is not None and 'bufferView' in j['images'][im]:
                raw = bytes(view_bytes(j, binary, j['images'][im]['bufferView']))
                pil = Image.open(io.BytesIO(raw))
                picture = np.asarray(pil.convert('RGBA' if 'A' in pil.mode else 'RGB'), dtype=np.float32).copy()
            # the face swap, or the overlay for garments cut by hand (and any the swap would crack)
            crack, how = 0, None
            ways = (overlay,) if stem in OVERLAY else (recut, overlay)
            for way in (ways if FOLLOW else (recut,)):
                pr = Prim(j, binary, mi, prim)
                if picture is not None:
                    picture = np.asarray(pil.convert('RGBA' if 'A' in pil.mode else 'RGB'), dtype=np.float32).copy()
                skip = set()
                for attempt in range(6):
                    pr = Prim(j, binary, mi, prim)
                    if picture is not None:
                        picture = np.asarray(pil.convert('RGBA' if 'A' in pil.mode else 'RGB'), dtype=np.float32).copy()
                    got, why = way(pr, M, picture, frozenset(skip)) if way is recut else way(pr, M, picture)
                    if got is None:
                        break
                    rows, new_tris, drop, painted, scale, changed = got
                    before = open_segments(Prim(j, binary, mi, prim))   # (way() moves the vertices standing for CHANGE in pr)
                    # cracks are looked for with every point at its place on the old garment (before moves off it)
                    test = Prim(j, binary, mi, prim)
                    extend(test, [dict(r, POSITION=r.get('_flat', r['POSITION'])) for r in rows], new_tris, drop)
                    bad = []
                    crack = cracks(before, test, out=bad)
                    how = way.__name__
                    if not crack or way is not recut or NEW:
                        break
                    # a re-triangulation only (no new vertices): the faces next to each crack are left as the garment
                    # has them (its own vertices there don't stand where the swap reads them: a hand-moved back) and
                    # the rest swapped again
                    lab = labels_of(test, M)
                    at = {tuple(np.round(test.attrs['POSITION'][g], 4)): k for g, k in lab.items()}
                    near = {at.get(tuple(np.round(p, 4))) for seg in bad for p in seg} - {None}
                    more = {pt for pt in PATCH if set(pt) & near} - skip
                    if not more:
                        break
                    skip |= more
                if got is None:
                    break
                if skip and not crack:
                    how += ' (%d faces by cracks left as they were)' % len(skip)
                if not crack:
                    break
            if got is None:
                result = (why, None)
                continue
            n_before = len(pr.tri)
            followed = follow(pr, M, rows) if not crack and FOLLOW else 0
            extend(pr, rows, new_tris, drop)
            if crack and not FOLLOW:
                result = ('UNCUT (the cut left %d cracks)' % crack, None)
                break
            if crack:
                # left uncut, but moved with the body where it stands over the patch (its vertices standing for body
                # vertices by their change, the rest by the field), so the bulge can't come through it
                pr = Prim(j, binary, mi, prim)
                label = labels_of(pr, M)
                m3 = np.linalg.inv(M[:3, :3])
                for g, k in label.items():
                    if k in CHANGE:
                        row = moved(pr, {'POSITION': pr.attrs['POSITION'][g].copy(),
                                         'targets': [{n: a[g].copy() for n, a in tg.items()} for tg in pr.targets]}, CHANGE[k], m3)
                        pr.attrs['POSITION'][g] = row['POSITION']
                        for ti, tg in enumerate(pr.targets):
                            for n in tg:
                                tg[n][g] = row['targets'][ti][n]
                followed = follow(pr, M, [])
                what = keys_only(pr, M)
                result = ('UNCUT (the cut left %d cracks), %d vertices following the body: %s' % (crack, followed, what), (pr, None))
                break
            image = None
            if painted:
                out = io.BytesIO()
                mime = j['images'][im].get('mimeType', 'image/png')
                img = Image.fromarray(np.clip(np.round(picture), 0, 255).astype(np.uint8))
                if mime == 'image/jpeg':
                    img.save(out, 'JPEG', quality=95, subsampling=0)
                else:
                    img.save(out, 'PNG')
                image = (im, out.getvalue())
            result = ('%s: %d -> %d triangles (%d of the patch), %d vertices moved, %d following%s' % (
                how, n_before, len(pr.tri), len(drop), changed, followed,
                ', %d islands painted%s' % (len(painted), '' if scale == 1 else ' at %.0f%%' % (scale * 100)) if painted else ''),
                (pr, image))
            break
        if result and result[1]:
            break
    if result is None or result[1] is None:
        skipped.append((stem, result[0] if result else 'no mesh'))
        continue
    done.append((stem, result[0]))
    if not check:
        write(j, binary, result[1][0], os.path.join(out_folder, fname), result[1][1])
for stem, why in done:
    print('RECUT   %-26s %s' % (stem, why))
for stem, why in skipped:
    print('SKIPPED %-26s %s' % (stem, why))
print('%d garments %s, %d left alone' % (len(done), 'would be recut' if check else 'recut into ' + out_folder, len(skipped)))
