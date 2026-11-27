"""Generates a low-poly body with edge loops round the bones (left half built, mirrored), relaxed over the target
surface and fitted to it. Writes newbody.npz (V, faces as a ragged list, names) and newbody.obj."""
import numpy as np, trimesh, json, sys, os
from collections import defaultdict

tg = np.load('target.npz'); TGT = trimesh.Trimesh(tg['V'], tg['T'], process=False)
cb = np.load('body.npz'); CUR = trimesh.Trimesh(cb['V'], cb['T'], process=False)
BJ = json.load(open('bones.json'))
def head(b): return np.array(BJ[b]['head'])
def tail(b): return np.array(BJ[b]['tail'])
def along(b, t): return head(b) + (tail(b) - head(b)) * t
def unit(v): return v / np.linalg.norm(v)
def perp(axis, ref):
    a = unit(axis); r = ref - a * (ref @ a); return unit(r)

P = {}            # name -> position
PLANE = {}        # name -> (point, normal): ring vertices kept in their ring's plane while relaxing
SRC = {}          # name -> which surface it sits on: 0 target, 1 current body
FACES = []        # tuples of names (left half and midline)
MID = set()       # names on the midline (x = 0)

def cast(origin, dirs, mesh=None, last=False):
    mesh = TGT if mesh is None else mesh
    dirs = np.array([unit(d) for d in dirs])
    orig = np.repeat(np.asarray(origin, float)[None], len(dirs), 0)
    locs, ri, ti = mesh.ray.intersects_location(orig, dirs, multiple_hits=True)
    out = []
    for k in range(len(dirs)):
        m = ri == k
        if not m.any():
            print('  no hit from', np.round(origin, 2), 'along', np.round(dirs[k], 2)); out.append(orig[k] + dirs[k] * 0.5); continue
        d = np.linalg.norm(locs[m] - orig[k], axis=1)
        out.append(locs[m][np.argmax(d) if last else np.argmin(d)])
    return np.array(out)

def ring(prefix, centre, axis, ref, angles, side_ref, mesh=None, src=0, last=False, plane=True):
    """Ring of vertices prefix0.. in the plane through centre normal to axis, at angles (deg) from ref towards side_ref."""
    u = perp(axis, ref); w = perp(axis, side_ref - u * (side_ref @ u))
    dirs = [np.cos(np.radians(a)) * u + np.sin(np.radians(a)) * w for a in angles]
    pts = cast(centre, dirs, mesh, last)
    names = []
    for k, p in enumerate(pts):
        n = '%s%d' % (prefix, k); P[n] = p; SRC[n] = src; names.append(n)
        if plane: PLANE[n] = (np.asarray(centre, float), unit(axis))
    return names

def quad(*ns): FACES.append(tuple(ns))
def bridge(A, B, closed=True):
    """Quads between two loops of equal length, A[k] next to B[k]."""
    n = len(A)
    for k in range(n if closed else n - 1):
        quad(A[k], A[(k + 1) % n], B[(k + 1) % n], B[k])
def fan(loop, centre):
    for k in range(len(loop)): FACES.append((loop[k], loop[(k + 1) % len(loop)], centre))

X, Y, Z = np.eye(3)
# Loops left out (an ablation found each cost nearly nothing in shape): the thumb's root ring (the thumb starts
# from the hole in the palm), and the upper arm's middle ring. DROP=... overrides, for trying others.
DROP = set(filter(None, os.environ.get('DROP', 'T1,R1').split(',')))
def chain(rings, labels):
    """Bridges the rings in order, leaving out (and deleting) those whose label is in DROP."""
    kept = []
    for r, l in zip(rings, labels):
        if l in DROP:
            for n in r:
                for d in (P, SRC, PLANE): d.pop(n, None)
        else: kept.append(r)
    for i in range(len(kept) - 1):
        kept[i + 1] = best_match(kept[i], kept[i + 1]) if labels[0] == 'hole' else kept[i + 1]
        bridge(kept[i], kept[i + 1])
    return kept
FRONT = -Y

# ---------------------------------------------------------------- torso
# Columns 0..7 on the left: 0 the front midline, 7 the back midline; angles from the front towards +x.
TC = [c * 180 / 7 for c in range(8)]
def spine_centre(z):
    p = cast([0, 0.2, z], [FRONT, Y])
    return np.array([0, (p[0][1] + p[1][1]) / 2, z])

def torso_ring(name, z, cols, tilt=0.0):
    c = spine_centre(z)
    axis = unit(np.array([0, tilt, 1.0]))
    out = {}
    u = perp(axis, FRONT); w = perp(axis, X)
    dirs = [np.cos(np.radians(TC[k])) * u + np.sin(np.radians(TC[k])) * w for k in cols]
    pts = cast(c, dirs)
    for k, p in zip(cols, pts):
        n = '%s%d' % (name, k); P[n] = p; SRC[n] = 0; PLANE[n] = (c, axis); out[k] = n
        if k in (0, 7): MID.add(n); P[n][0] = 0
    return out

ua0, ua1 = head('upper_arm.L'), tail('upper_arm.L')
arm_axis = unit(ua1 - ua0)
# ring order round the arm: 0 down-front, 1 down, 2 down-back, 3 back, 4 up-back, 5 up, 6 up-front, 7 front
ARM_ANG = [-45 + 45 * k for k in range(8)]
# The armhole: a near-upright plane where the torso's side meets the arm (armpit ~ (2.15, 0.3, 17.5), shoulder top
# above it at ~19.2), tilted a little along the arm. Cast across the arm it went through the torso.
root_c = np.array([2.2, 0.3, 18.35])
root_n = unit(np.array([1.0, 0.05, 0.05]))
root = ring('r0_', root_c, root_n, -Z, ARM_ANG, Y, plane=False)

ZH, ZB, ZW, ZU, ZP = 12.35, 13.45, 14.5, 15.6, 16.5
B_ = torso_ring('tb', ZB, range(8))
W_ = torso_ring('tw', ZW, range(8))
U_ = torso_ring('tu', ZU, range(8))
P_ = torso_ring('tp', ZP, range(8))
zA = np.mean([P[root[k]][2] for k in (0, 1, 2)])
A_ = torso_ring('ta', zA, [0, 1, 2, 6, 7])
A_[3], A_[4], A_[5] = root[0], root[1], root[2]
zM = np.mean([P[root[k]][2] for k in (3, 7)])
M_ = torso_ring('tm', zM, [0, 1, 2, 6, 7])
M_[3], M_[5] = root[7], root[3]
zS = np.mean([P[root[k]][2] for k in (4, 5, 6)])
S_ = torso_ring('ts', zS + float(os.environ.get('SOFF', 0.15)), [0, 1, 2, 6, 7])
S_[3], S_[4], S_[5] = root[6], root[5], root[4]

# Hip ring = the leg's seam: the midline ends at the crotch front and back, the rest round the hip in a tilted plane.
crotch = cast([0, 0.25, 12.4], [unit(np.array([0, -1, -0.75])), unit(np.array([0, 1, -0.75]))])
H_ = {}
for k, p in zip((0, 7), crotch):
    n = 'th%d' % k; P[n] = p.copy(); P[n][0] = 0; SRC[n] = 0; MID.add(n); H_[k] = n
th0 = head('thigh.L')
hip_axis = unit(np.array([0.3, 0.05, -1.0]))      # tilted out: low at the crotch, high at the hip
LEG_ANG = [-50 + 40 * j for j in range(9)]         # 0 front-inner ... 7 back-inner, 8 inner (the crotch vertex)
hip = ring('th_', th0 + np.array([0.05, 0.1, -0.35]), hip_axis, FRONT, LEG_ANG, X, plane=False)
for j in range(1, 7):
    H_[j] = hip[j]
for j in (0, 7, 8):
    P.pop(hip[j]); SRC.pop(hip[j])
# the crotch's lowest point, on the midline between the legs
P['tc'] = cast([0, 0.15, 12.3], [-Z])[0]; P['tc'][0] = 0; SRC['tc'] = 0; MID.add('tc')

TORSO = [H_, B_, W_, U_, P_, A_, M_, S_]
for lo, hi in zip(TORSO[:-1], TORSO[1:]):
    for k in range(7):
        if lo is A_ and k in (3, 4): continue          # the arm's hole (two columns, two bands)
        if lo is M_ and k in (3, 4): continue
        quad(lo[k], lo[k + 1], hi[k + 1], hi[k])

# The breast: the four quads round ta1 (the Female key's centre, x 0.92 z 17.56, falls on it) made a ring of 8 quads
# round an inner ring of 8 and 4 quads in the middle, so a bust key has a round patch to shape (+16 triangles a side).
patch = [P_[0], P_[1], P_[2], A_[2], M_[2], M_[1], M_[0], A_[0]]
for f in [(P_[0], P_[1], A_[1], A_[0]), (P_[1], P_[2], A_[2], A_[1]), (A_[0], A_[1], M_[1], M_[0]), (A_[1], A_[2], M_[2], M_[1])]:
    FACES.remove(f)
centre = A_[1]; inner = []
for k, bn in enumerate(patch):
    n = 'bi%d' % k; P[n] = P[centre] + (P[bn] - P[centre]) * 0.5; SRC[n] = 0; inner.append(n)
bridge(patch, inner)
for k in (0, 2, 4, 6): quad(inner[k], inner[k + 1], inner[(k + 2) % 8], centre)

# ---------------------------------------------------------------- neck and the cap inside the head
NC = [0, 36, 72, 108, 144, 180]                    # 10 round (6 a side with the midline)
def neck_ring(name, z, mesh=None, src=1, last=False, tilt=0.0):
    c = np.array([0, -0.1, z])
    if mesh is None: mesh = CUR
    ax = unit(np.array([0, -tilt, 1.0]))          # tilt > 0: lower at the back
    dirs = [np.cos(np.radians(a)) * perp(ax, FRONT) + np.sin(np.radians(a)) * perp(ax, X) for a in NC]
    pts = cast(c, dirs, mesh, last)
    out = []
    for k, p in enumerate(pts):
        n = '%s%d' % (name, k); P[n] = p; SRC[n] = src; PLANE[n] = (c, ax); out.append(n)
        if k in (0, len(NC) - 1): MID.add(n); P[n][0] = 0
    return out
N0 = neck_ring('n0_', float(os.environ.get('N0Z', 19.85)), tilt=float(os.environ.get('NT', 0)))
N1 = neck_ring('n1_', 20.45)
# the cap inside the head is a tilted disc: its ring is cast in the disc's own plane, from its middle, to the rim
rimA, rimB, rimC = np.array([0, -2.1, 20.71]), np.array([1.92, -0.65, 21.29]), np.array([0, 1.78, 21.99])
cap_n = unit(np.cross(rimC - rimA, rimB - rimA)); cap_n = cap_n if cap_n[2] > 0 else -cap_n
cap_c = (rimA + rimC) / 2 - cap_n * 0.12
NF = []
for k, a in enumerate(NC):
    d = np.cos(np.radians(a)) * perp(cap_n, FRONT) + np.sin(np.radians(a)) * perp(cap_n, X)
    p = cast(cap_c, [d], CUR, last=True)[0]
    n = 'nf_%d' % k; P[n] = p; SRC[n] = 1; PLANE[n] = (cap_c, cap_n); NF.append(n)
    if k in (0, len(NC) - 1): MID.add(n); P[n][0] = 0
S_list = [S_[k] for k in range(8)]
# shoulder line to neck base: s0:n0, s1:n1, s2,s3:n2, s4:n3, s5,s6:n4, s7:n5
amap = [0, 1, 2, 2, 3, 4, 4, 5]
for i in range(7):
    a, b = amap[i], amap[i + 1]
    if a == b: FACES.append((S_list[i], S_list[i + 1], N0[a]))
    else: quad(S_list[i], S_list[i + 1], N0[b], N0[a])
for lo, hi in ((N0, N1), (N1, NF)):
    for k in range(len(NC) - 1): quad(lo[k], lo[k + 1], hi[k + 1], hi[k])
P['ncap'] = cast(cap_c - cap_n * 0.5, [cap_n], CUR, last=True)[0]; P['ncap'][0] = 0; SRC['ncap'] = 1; MID.add('ncap')
for k in range(len(NC) - 1): FACES.append((NF[k], NF[k + 1], 'ncap'))

# ---------------------------------------------------------------- arm
fa0, fa1 = head('forearm.L'), tail('forearm.L')
elbow_axis = unit(unit(ua1 - ua0) + unit(fa1 - fa0))
R1 = ring('r1_', along('upper_arm.L', 0.42), ua1 - ua0, -Z, ARM_ANG, Y)
E1 = ring('e1_', along('upper_arm.L', 0.84), ua1 - ua0, -Z, ARM_ANG, Y)
E2 = ring('e2_', fa0, elbow_axis, -Z, ARM_ANG, Y)
E3 = ring('e3_', along('forearm.L', 0.16), fa1 - fa0, -Z, ARM_ANG, Y)
WR = ring('wr_', along('forearm.L', 0.99), fa1 - fa0, -Z, ARM_ANG, Y)
chain([root, R1, E1, E2, E3, WR], ['root', 'R1', 'E1', 'E2', 'E3', 'WR'])

# ---------------------------------------------------------------- hand (a mitten with a thumb)
hd = tail('hand.L') - head('hand.L')
P1 = ring('p1_', along('hand.L', 0.3), hd, -Z, ARM_ANG, Y, mesh=CUR, src=1)
P2 = ring('p2_', along('hand.L', 0.62), hd, -Z, ARM_ANG, Y, mesh=CUR, src=1)
KN = ring('kn_', along('hand.L', 0.98), hd, -Z, ARM_ANG, Y, mesh=CUR, src=1)
fd1 = tail('hand.L.001') - head('hand.L.001'); fd2 = tail('hand.L.002') - head('hand.L.002')
F1 = ring('f1_', head('hand.L.002'), unit(fd1) + unit(fd2), -Z, ARM_ANG, Y, mesh=CUR, src=1)
FT = ring('ft_', along('hand.L.002', 0.55), fd2, -Z, ARM_ANG, Y, mesh=CUR, src=1)
tip = cast(along('hand.L.002', 0.3), [fd2], CUR)[0]
P['ftip'] = tip; SRC['ftip'] = 1
# the thumb's hole: column 7 (front) to 0 (down-front), across the bands wrist -> p1 -> p2 (6 corners)
for lo, hi in ((WR, P1), (P1, P2)):
    for k in range(8):
        if k == 7: continue
        quad(lo[k], lo[(k + 1) % 8], hi[(k + 1) % 8], hi[k])
fan(chain([P2, KN, F1, FT], ['P2', 'KN', 'F1', 'FT'])[-1], 'ftip')
hole = [WR[7], WR[0], P1[0], P2[0], P2[7], P1[7]]
t0, t1 = head('thumb.L'), tail('thumb.L'); t2 = tail('thumb.L.001')
TH_ANG = [0, 60, 120, 180, 240, 300]
T1 = ring('t1_', along('thumb.L', 0.35), t1 - t0, -Z, TH_ANG, Y)
T2 = ring('t2_', t1, unit(t1 - t0) + unit(t2 - t1), -Z, TH_ANG, Y)
TT = ring('tt_', along('thumb.L.001', 0.45), t2 - t1, -Z, TH_ANG, Y)
P['ttip'] = cast(along('thumb.L.001', 0.2), [t2 - t1])[0]; SRC['ttip'] = 0

def best_match(A, B):
    """B re-ordered (rotated, maybe reversed) to sit beside A."""
    best = None
    for rev in (False, True):
        BB = B[::-1] if rev else B
        for s in range(len(B)):
            R = BB[s:] + BB[:s]
            d = sum(np.linalg.norm(P[a] - P[b]) for a, b in zip(A, R))
            if best is None or d < best[0]: best = (d, R)
    return best[1]
TT = chain([hole, T1, T2, TT], ['hole', 'T1', 'T2', 'TT'])[-1]
fan(TT, 'ttip')

# ---------------------------------------------------------------- leg
tg0, tg1 = head('thigh.L'), tail('thigh.L'); sh0, sh1 = head('shin.L'), tail('shin.L')
knee_axis = unit(unit(tg1 - tg0) + unit(sh1 - sh0))
HIP = [H_[j] for j in range(8)] + ['tc']
L1 = ring('l1_', along('thigh.L', 0.3), tg1 - tg0, FRONT, LEG_ANG, X)
K1 = ring('k1_', along('thigh.L', 0.82), tg1 - tg0, FRONT, LEG_ANG, X)
K2 = ring('k2_', sh0, knee_axis, FRONT, LEG_ANG, X)
K3 = ring('k3_', along('shin.L', 0.17), sh1 - sh0, FRONT, LEG_ANG, X)
L5 = ring('l5_', along('shin.L', 0.5), sh1 - sh0, FRONT, LEG_ANG, X)
AN = ring('an_', along('shin.L', 0.86), sh1 - sh0, FRONT, LEG_ANG, X)
chain([HIP, L1, K1, K2, K3, L5, AN], ['HIP', 'L1', 'K1', 'K2', 'K3', 'L5', 'AN'])
# the crotch: th0 - tc - th7 shared by both legs

# ---------------------------------------------------------------- foot: a boot
# The ankle ring, then three rings round the foot's outline (the top one low at the toe, high at the heel; spaced by
# length along the outline, or a long foot gets one vertex on the toe), then the sole. The toe's front corners take
# the toe bone's weight; a chibi foot doesn't need a toe loop. (A tube along the foot with the leg in a hole in its top
# crumpled at the ankle.)
fc = np.array([2.55, -0.2, 0])
def outline_ring(prefix, centre, axis, ref, side_ref, n, anchor):
    """n vertices spaced evenly along the surface's outline in a plane (cast from centre at many angles), vertex
    `anchor` on the outline's point furthest along ref (the toe's tip), the order going towards side_ref."""
    angs = np.arange(0, 360, 2.0)
    u = perp(axis, ref); w = perp(axis, side_ref - u * (side_ref @ u))
    pts = cast(centre, [np.cos(np.radians(a)) * u + np.sin(np.radians(a)) * w for a in angs])
    seg = np.linalg.norm(np.roll(pts, -1, 0) - pts, axis=1); s = np.r_[0, np.cumsum(seg)]; L = s[-1]
    tip = int(np.argmax(pts @ u))
    names = []
    for k in range(n):
        t = (s[tip] + (k - anchor) * L / n) % L
        i = np.searchsorted(s, t, side='right') - 1; f = (t - s[i]) / max(seg[i % len(pts)], 1e-9)
        p = pts[i % len(pts)] * (1 - f) + pts[(i + 1) % len(pts)] * f
        nm = '%s%d' % (prefix, k); P[nm] = p; SRC[nm] = 0; PLANE[nm] = (np.asarray(centre, float), unit(axis)); names.append(nm)
    return names
FM = outline_ring('fm_', fc + [0, 0, 0.9], unit(np.array([0, -0.2, 1.0])), FRONT, X, 9, 1)
FL = outline_ring('fl_', fc + [0, 0, 0.5], unit(np.array([0, -0.08, 1.0])), FRONT, X, 9, 1)
FG = outline_ring('fg_', fc + [0, 0, 0.25], Z, FRONT, X, 9, 1)
bridge(AN, FM); bridge(FM, FL); bridge(FL, FG)
P['fsole'] = cast(fc + [0, 0, 0.8], [-Z])[0]; SRC['fsole'] = 0
fan(FG, 'fsole')

# ---------------------------------------------------------------- mirror into a whole mesh
names = list(P.keys())
for n in names:
    if n in MID: P[n][0] = 0.0
left = [n for n in names]
idx = {}
Vl = []
for n in left:
    idx[n] = len(Vl); Vl.append(P[n])
for n in left:
    if n not in MID:
        idx[n + '~R'] = len(Vl); Vl.append(P[n] * [-1, 1, 1])
V = np.array(Vl, float)
def mir(n): return n if n in MID else n + '~R'
F = [[idx[n] for n in f] for f in FACES] + [[idx[mir(n)] for n in f[::-1]] for f in FACES]
NAMES = [None] * len(V)
for n, i in idx.items(): NAMES[i] = n

# ---------------------------------------------------------------- orient faces consistently, outward
def orient(F, V):
    edge_faces = defaultdict(list)
    for fi, f in enumerate(F):
        for k in range(len(f)):
            edge_faces[frozenset((f[k], f[(k + 1) % len(f)]))].append(fi)
    bad = [e for e, fs in edge_faces.items() if len(fs) != 2]
    if bad: print('NON-MANIFOLD edges:', len(bad), [tuple(sorted(e)) for e in bad[:12]])
    def directed(f): return {(f[k], f[(k + 1) % len(f)]) for k in range(len(f))}
    done = [False] * len(F); F = [list(f) for f in F]
    for s in range(len(F)):
        if done[s]: continue
        done[s] = True; stack = [s]
        while stack:
            fi = stack.pop(); de = directed(F[fi])
            for (a, b) in list(de):
                for fj in edge_faces[frozenset((a, b))]:
                    if fj == fi or done[fj]: continue
                    if (a, b) in directed(F[fj]): F[fj] = F[fj][::-1]
                    done[fj] = True; stack.append(fj)
    tris = [[f[0], f[i], f[i + 1]] for f in F for i in range(1, len(f) - 1)]
    vol = sum(np.dot(V[a], np.cross(V[b], V[c])) for a, b, c in tris)
    if vol < 0: F = [f[::-1] for f in F]
    return F
F = orient(F, V)
tris = [[f[0], f[i], f[i + 1]] for f in F for i in range(1, len(f) - 1)]
print('vertices %d, faces %d (quads %d), triangles %d' % (len(V), len(F), sum(len(f) == 4 for f in F), len(tris)))

MIRROR = np.array([idx[mir(n)] if not n.endswith('~R') else idx[n[:-2]] for n in NAMES])
np.savez('gen_raw.npz', nleft=len(FACES), mirror=MIRROR, V=V, F=np.array([f + [-1] * (4 - len(f)) for f in F]), names=np.array(NAMES, dtype=object),
         plane_p=np.array([PLANE.get(n.replace('~R', ''), (np.zeros(3), np.zeros(3)))[0] * ([-1, 1, 1] if n.endswith('~R') else 1) for n in NAMES]),
         plane_n=np.array([PLANE.get(n.replace('~R', ''), (np.zeros(3), np.zeros(3)))[1] * ([-1, 1, 1] if n.endswith('~R') else 1) for n in NAMES]),
         src=np.array([SRC[n.replace('~R', '')] for n in NAMES]), mid=np.array([n in MID for n in NAMES]), allow_pickle=True)
