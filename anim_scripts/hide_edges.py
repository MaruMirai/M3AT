# The body under a garment's edges, kept drawn where some clip brings it out from under the garment.
#
# export_frames.py's hidden_under leaves out the body triangles a garment covers, tested once at rest. Where a loose
# garment keeps its own weights (the parka's sleeves) or simply sits off the skin, a pose can swing its edge (a cuff, a hem,
# a collar) off the skin it covered at rest: the hidden triangles then leave the body's mesh open at the edge, and one
# looks into it (the inside of the hand through the parka's right cuff in the drafted rifle stance, shaded near black).
# So, after the export, every clip is posed (every STEP-th frame) and each hidden body triangle along the edge of what a
# garment hides (sharing a corner with a triangle left drawn) is tested again as hidden_under tests it at rest: a line
# along the posed skin's normal from HIDE_REACH inside to HIDE_LOOK out, from each corner and the middle, must meet the
# posed garment. One that fails in any frame is kept drawn, and the edge moves in to the triangles behind it, ROUNDS times.
# Skin-tight garments (cut from the body's faces, skinned with its weights) keep every triangle hidden, so nothing more
# is drawn under them. The garment's hide lines lose the kept triangles, and an opens line lists them, which the game's
# UnderOf reads to keep a garment under this one drawn there too (a shirt's cuff inside the parka's).
#
#   blender -b --python anim_scripts/hide_edges.py -- <chibi folder> [garment ...] [--step n] [--dry]
#
# Run by export_frames.py after anim.txt. Numpy and Blender's mathutils (its BVH ray casts). Garments with a `hide_edges no` line (fixed hiding: hide_body,
# hide_head, hide_groups) and those riding one bone whole are left alone, as is a garment hiding nothing.
import os
import sys
import time
import hashlib
import numpy as np

HIDE_REACH = 0.04   # export_frames.HIDE_REACH (cells)
HIDE_LOOK = 0.25    # export_frames.HIDE_LOOK
STEP = 4            # every this many frames of each clip is posed
ROUNDS = 3          # how many times the edge may move in
INWARD = 0.25       # of the way from each corner to the middle, where it is tested
DRIFT_STEP = 4      # every this many of the frames posed is used for the drift, as it only picks what to test
DRIFT = 0.02        # cells: a garment vertex that some frame takes further than this from where the skin under it would is loose there
APART_NEAR = 0.12   # cells: an edge triangle is tested only with such a vertex this near its middle at rest
CACHE = os.path.expanduser('~/.cache/m3at-hide-edges')   # each garment's kept triangles by what they were worked out from
SPREAD = 6          # points along each side of a body triangle where its weights are taken


def read_piece(path):
    """Rest places, triangles, weights (vertex, bone, weight) and the lines, as the game reads a skin.txt or garment file."""
    verts, tris, wv, wb, ww, hide = [], [], [], [], [], []
    flags = {}
    n = 0
    with open(path) as f:
        for line in f:
            w = line.split()
            if not w:
                continue
            if w[0] == 'v':
                verts.append((float(w[1]), float(w[2]), float(w[3])))
            elif w[0] == 'f':
                tris.append((int(w[1]), int(w[2]), int(w[3])))
            elif w[0] == 'w':
                for b, x in zip(w[1::2], w[2::2]):
                    wv.append(n)
                    wb.append(int(b))
                    ww.append(float(x))
                n += 1
            elif w[0] in ('hide', 'opens'):
                hide += [int(x) for x in w[1:]]   # (what an earlier run kept, hidden again, so running twice changes nothing)
            elif w[0] in ('hide_edges', 'bone', 'held', 'fur'):
                flags[w[0]] = w[1] if len(w) > 1 else ''
    return (np.array(verts, dtype=float).reshape(-1, 3), np.array(tris, dtype=int).reshape(-1, 3),
            (np.array(wv, dtype=int), np.array(wb, dtype=int), np.array(ww, dtype=float)), hide, flags)


def read_anim(folder, step):
    """The rest heads and every step-th frame of every clip (anim.txt's k lines)."""
    rest, frames, rows, cur = [], [], [], None
    def flush():
        if cur is not None and rows:
            a = np.array(rows).reshape(-1, len(rest), 7)
            frames.extend(a[::step])
    with open(os.path.join(folder, 'anim.txt')) as f:
        for line in f:
            if line.startswith('k '):
                rows.append([float(x) for x in line.split()[1:]])
            elif line.startswith('clip '):
                flush()
                rows, cur = [], line.split()[1]
            elif line.startswith('bone '):
                rest.append([float(x) for x in line.split()[2:5]])
    flush()
    return np.array(rest), np.array(frames)


def bone_rows(rest, frames):
    """Each frame's skinning matrices (top three rows), one more for 'no bone': frames x (bones + 1) x 3 x 4."""
    q = frames[..., :4]
    w, x, y, z = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    r = np.empty(q.shape[:-1] + (3, 3))
    r[..., 0, 0] = 1 - 2 * (y * y + z * z); r[..., 0, 1] = 2 * (x * y - z * w); r[..., 0, 2] = 2 * (x * z + y * w)
    r[..., 1, 0] = 2 * (x * y + z * w); r[..., 1, 1] = 1 - 2 * (x * x + z * z); r[..., 1, 2] = 2 * (y * z - x * w)
    r[..., 2, 0] = 2 * (x * z - y * w); r[..., 2, 1] = 2 * (y * z + x * w); r[..., 2, 2] = 1 - 2 * (x * x + y * y)
    m = np.zeros(frames.shape[:2][:1] + (len(rest) + 1, 3, 4))
    m[:, :-1, :, :3] = r
    m[:, :-1, :, 3] = frames[..., 4:7] - np.einsum('fbij,bj->fbi', r, rest)
    m[:, -1, :, :3] = np.eye(3)
    return m


def weight_matrix(n, weights, bones):
    vert, bone, weight = weights
    w = np.zeros((n, bones + 1))
    np.add.at(w, (vert, bone), weight)
    total = w.sum(1)
    w[total == 0, bones] = 1.0
    total[total == 0] = 1.0
    return w / total[:, None]


def posed(w, rest, m):
    """Linear skinning of rest by weights w (vertices x bones) in every frame of m: frames x vertices x 3."""
    blend = np.einsum('vb,fbij->fvij', w, m, optimize=True)
    return np.einsum('fvij,vj->fvi', blend[..., :3], rest) + blend[..., 3]


def vertex_normals(v, tris):
    """frames x vertices x 3, area weighted."""
    n = np.cross(v[:, tris[:, 1]] - v[:, tris[:, 0]], v[:, tris[:, 2]] - v[:, tris[:, 0]])
    out = np.zeros_like(v)
    for k in range(3):
        np.add.at(out, (slice(None), tris[:, k]), n)
    return out / np.maximum(np.linalg.norm(out, axis=2, keepdims=True), 1e-12)


class Edge:
    """One garment's hidden body triangles along the edge of what it hides, tested over the frames."""
    def __init__(self, name, path, garment, body, bw, bones, m):
        self.name, self.path = name, path
        rest, tris, weights, hide, _ = garment
        self.rest, self.tris = rest, [tuple(int(x) for x in t) for t in tris]
        self.gw = weight_matrix(len(rest), weights, bones)
        self.hidden = set(hide)
        self.keep = set()
        self.body, self.bw = body, bw
        # Which of the garment's vertices move apart from the skin under them (the body's weights at the nearest point of
        # its surface, from points spread over each triangle).
        btris = body[1]
        a, b, c = body[0][btris[:, 0]], body[0][btris[:, 1]], body[0][btris[:, 2]]
        wa, wb_, wc = bw[btris[:, 0]], bw[btris[:, 1]], bw[btris[:, 2]]
        spots, spot_w = [], []
        for i in range(SPREAD + 1):
            for j in range(SPREAD + 1 - i):
                u, v = i / SPREAD, j / SPREAD
                spots.append(a + (b - a) * u + (c - a) * v)
                spot_w.append(wa * (1 - u - v) + wb_ * u + wc * v)
        spots, spot_w = np.concatenate(spots), np.concatenate(spot_w)
        under = np.array([np.argmin(((spots - p) ** 2).sum(1)) for p in rest])
        # Where each vertex goes against where the skin under it would carry it, over every frame: by its own weights or
        # by the skin's. Only past DRIFT is it loose enough to come off the skin (at a seam two triangles' weights differ
        # a little, which moves it nowhere).
        skin_w = spot_w[under]
        drift = np.zeros(len(rest))
        some = m[::DRIFT_STEP]
        for s in range(0, len(some), 256):
            drift = np.maximum(drift, np.linalg.norm(posed(self.gw, rest, some[s:s + 256]) - posed(skin_w, rest, some[s:s + 256]), axis=2).max(0))
        self.apart = drift > DRIFT
        self.next_round()

    def next_round(self):
        """The edge triangles to test: hidden, sharing a corner with one drawn, and with the garment near them loose
        (self.apart; a garment taking the body's weights from the skin under each vertex is that skin pushed out, so it
        folds and turns with it and stays over it)."""
        brest, btris = self.body[0], self.body[1]
        drawn = set(btris[[k for k in range(len(btris)) if k not in self.hidden]].ravel())
        edge = np.array(sorted(k for k in self.hidden if drawn & set(btris[k])), dtype=int)
        if len(edge):
            middle = brest[btris[edge]].mean(1)
            near = ((middle[:, None, :] - self.rest[None]) ** 2).sum(-1) <= APART_NEAR ** 2
            edge = edge[(near & self.apart[None]).any(1)]
        self.edge = edge
        self.out = np.zeros(len(edge), dtype=bool)   # came out from under the garment in some frame tested

    def test(self, m, bp, bn):
        """Tests the edge in the frames of m (body posed bp, its normals bn)."""
        from mathutils import Vector
        from mathutils.bvhtree import BVHTree
        live = np.where(~self.out)[0]
        if len(live) == 0:
            return
        g = posed(self.gw, self.rest, m)
        et = self.body[1][self.edge[live]]
        for f in range(len(m)):
            tree = BVHTree.FromPolygons(g[f].tolist(), self.tris)
            corners = bp[f][et]                       # triangles x 3 x 3
            ways = bn[f][et]
            middle = corners.mean(1)[:, None]
            # Each corner pulled INWARD of the way to the middle: a corner on the edge of what is hidden lies on the
            # garment's own edge, where a line out from it grazes the edge and misses by rounding however tight it fits.
            points = np.concatenate([corners + (middle - corners) * INWARD, middle], axis=1)
            mid_way = ways.sum(1)
            ways = np.concatenate([ways, (mid_way / np.maximum(np.linalg.norm(mid_way, axis=1, keepdims=True), 1e-12))[:, None]], axis=1)
            for j, e in enumerate(live):
                if self.out[e]:
                    continue
                for q, w in zip(points[j], ways[j]):
                    way = Vector(w)
                    if tree.ray_cast(Vector(q) - way * HIDE_REACH, way, HIDE_REACH + HIDE_LOOK)[0] is None:
                        self.out[e] = True
                        break

    def finish_round(self):
        new = {int(e) for e in self.edge[self.out]}
        self.keep |= new
        self.hidden -= new
        return bool(new)


def rewrite(path, keep):
    """The garment file with its hide lines less keep and an opens line listing keep."""
    with open(path) as f:
        lines = f.readlines()
    hide = sorted({int(x) for line in lines if line.startswith(('hide ', 'opens ')) for x in line.split()[1:]})
    left = [k for k in hide if k not in keep]
    out, done = [], False
    for line in lines:
        if line.startswith('hide ') or line.startswith('opens ') or line.startswith('# opens '):
            if not done:
                for k in range(0, len(left), 32):
                    out.append('hide %s\n' % ' '.join(str(x) for x in left[k:k + 32]))
                if keep:
                    out.append('# opens k ...: body triangles it covers at rest that a pose brings out from under its edge '
                               '(hide_edges.py), drawn though it is worn.\n')
                    ordered = sorted(keep)
                    for k in range(0, len(ordered), 32):
                        out.append('opens %s\n' % ' '.join(str(x) for x in ordered[k:k + 32]))
                done = True
            continue
        out.append(line)
    with open(path, 'w') as f:
        f.writelines(out)


def garments_of(folder):
    names = []
    with open(os.path.join(folder, 'skin.txt')) as f:
        for line in f:
            if line.startswith('garment '):
                _, name, rest = line.rstrip('\n').split(' ', 2)
                names.append((name, rest))
    return names


def run(folder, only=None, step=STEP, dry=False, chunk=64):
    start = time.time()
    body = read_piece(os.path.join(folder, 'skin.txt'))
    rest, frames = read_anim(folder, step)
    m = bone_rows(rest, frames)
    bw = weight_matrix(len(body[0]), body[2], len(rest))
    # The body and the clips, which every garment's answer depends on, and then each garment's own file (its hide and
    # opens lines as one, so a garment this has already rewritten keys as it did before).
    common = hashlib.sha1()
    for part in ('skin.txt', 'anim.txt'):
        with open(os.path.join(folder, part), 'rb') as f:
            common.update(f.read())
    common.update(('%d %d %g %g %g %g %g' % (step, ROUNDS, INWARD, DRIFT, DRIFT_STEP, APART_NEAR, SPREAD)).encode())
    os.makedirs(CACHE, exist_ok=True)
    edges, cached, cached_kept = [], 0, 0
    for name, file in garments_of(folder):
        if only and name not in only:
            continue
        path = os.path.join(folder, file)
        garment = read_piece(path)
        flags = garment[4]
        if flags.get('hide_edges') == 'no' or 'bone' in flags or flags.get('held') == 'yes' or 'fur' in flags or not garment[3]:
            continue
        key = common.copy()
        with open(path) as f:
            key.update(''.join(l for l in f if not l.startswith(('hide ', 'opens ', '# opens'))).encode())
        key.update(' '.join(str(k) for k in sorted(set(garment[3]))).encode())
        known = os.path.join(CACHE, key.hexdigest())
        if os.path.exists(known):
            with open(known) as f:
                keep = {int(x) for x in f.read().split()}
            cached += 1
            cached_kept += len(keep)
            if keep:
                print('HIDE_EDGES %s: %d of %d hidden body triangles kept drawn (as before)' % (name, len(keep), len(garment[3])))
            if not dry:
                rewrite(path, keep)
            continue
        e = Edge(name, path, garment, body, bw, len(rest), m)
        e.known = known
        edges.append(e)
    for _ in range(ROUNDS):
        testing = [e for e in edges if len(e.edge)]
        if not testing:
            break
        for s in range(0, len(m), chunk):
            bp = posed(bw, body[0], m[s:s + chunk])
            bn = vertex_normals(bp, body[1])
            for e in testing:
                e.test(m[s:s + chunk], bp, bn)
        moved = False
        for e in testing:
            if e.finish_round():
                moved = True
                e.next_round()
            else:
                e.edge = e.edge[:0]
        if not moved:
            break
    total = cached_kept
    for e in edges:
        total += len(e.keep)
        if e.keep:
            print('HIDE_EDGES %s: %d of %d hidden body triangles kept drawn' % (e.name, len(e.keep), len(e.keep) + len(e.hidden)))
        if not dry:
            rewrite(e.path, e.keep)
        with open(e.known, 'w') as f:
            f.write(' '.join(str(k) for k in sorted(e.keep)))
    print('HIDE_EDGES %d triangles kept over %d frames, %d garments tested, %d as before, in %.0f s'
          % (total, len(frames), len(edges), cached, time.time() - start))


if __name__ == '__main__':
    args = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else sys.argv[1:]
    step, dry = STEP, False
    if '--step' in args:
        i = args.index('--step')
        step = int(args[i + 1])
        del args[i:i + 2]
    if '--dry' in args:
        args.remove('--dry')
        dry = True
    run(args[0], set(args[1:]) or None, step, dry)
