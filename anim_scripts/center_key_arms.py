"""Put a body shape key's arms back round their bones.

Growing the arms in a key (Hulk) by hand or with an inflate tends to push
them off their bones sideways: the forearm and hand of Hulk came out up to
0.45 units in front of the bones while Basis, Male and Fat keep each
cross-section within about 0.1 of them. Only the bones' rest pose is there to
go by, and the game moves each joint by the vertices round it (the exporter's
`anchor`), so an arm off its bone bends about the wrong place.

For each arm, the body is sliced square across its bone chain (upper arm,
forearm, hand) every `STEP` of a bone, in Basis and in the key, and each
slice's middle (the cut loop's centre by length) measured off the bone. The
key's change of that sideways offset from Basis's is what went wrong (Basis's
own offset is kept: it is the modelled arm), and it is taken out of the key at
every arm vertex, eased along the chain between the slices, times the share of
the vertex's weights on the arm's bones (so the shoulder, where the arm meets
the trunk, moves less and the trunk not at all). How far the key moves the arm
along its bone (the wider shoulders) and how much it grows it are untouched.
Slices that cut the trunk too (near the shoulder) are not used: the arm before
the first good slice takes that slice's correction.

Run it with the Text Editor's Run Script in the open session, or
    blender -b Models/pawnHumanRig.blend --python anim_scripts/center_key_arms.py -- <out.blend> [key ...]
(Hulk by default), which never saves over the .blend it opened.
"""
import sys
import bmesh
import bpy
from mathutils import Vector

KEYS = ['Hulk']
BODY = 'Body'
RIG = 'metarig'
SIDES = ('L', 'R')
CHAIN = ('upper_arm', 'forearm', 'hand')
# Bones past the chain whose vertices take the hand's end correction.
FINGERS = ('thumb',)   # hand.<side>.001/.002 match as 'hand'
STEP = 0.05          # share of a bone between slices
FIRST = 0.55         # share of the upper arm before which slices meet the trunk
TRUNK_GIRTH = 2.0    # a slice loop this many times the next one's length is the trunk
ALONG = True         # also take out the key's slide of the arm along its bones
PASSES = 1          # going again piles onto the vertices both bones' slices share
SMOOTH = 0.6         # the drift averaged along the chain over this (Gaussian sigma, rig units),
                     # as the slices tilt at each joint and the drift jumps there


def arm_bones(rig, side):
    names = set()
    for b in rig.data.bones:
        parts = b.name.split('.')
        if not b.use_deform or len(parts) < 2 or parts[1] != side:
            continue
        base = parts[0]
        if base in CHAIN or base in FINGERS:
            names.add(b.name)
    return names


def chain_of(rig, side):
    segs = []
    for n in CHAIN:
        b = rig.data.bones[n + '.' + side]
        segs.append((n + '.' + side, rig.matrix_world @ b.head_local, rig.matrix_world @ b.tail_local))
    return segs


def project(segs, p):
    """(distance along the chain, nearest segment) for a point."""
    best = None
    acc = 0.0
    for i, (_, h, t) in enumerate(segs):
        ax = t - h
        L = ax.length
        u = ax / L
        s = max(0.0, min(L, (p - h).dot(u)))
        d = (p - (h + u * s)).length
        if best is None or d < best[0]:
            best = (d, acc + s, i)
        acc += L
    return best[1], best[2]


def sections(cos, faces, h, u, at):
    bm = bmesh.new()
    vs = [bm.verts.new(c) for c in cos]
    for f in faces:
        try:
            bm.faces.new([vs[i] for i in f])
        except ValueError:
            pass
    p = h + u * at
    r = bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=p, plane_no=u)
    cut = {e for e in r['geom_cut'] if isinstance(e, bmesh.types.BMEdge)}
    loops = []
    seen = set()
    for e in cut:
        if e in seen:
            continue
        stack, comp = [e], []
        while stack:
            x = stack.pop()
            if x in seen:
                continue
            seen.add(x)
            comp.append(x)
            for v in x.verts:
                stack.extend(y for y in v.link_edges if y in cut and y not in seen)
        L = sum(x.calc_length() for x in comp)
        if L <= 0:
            continue
        cen = sum((((x.verts[0].co + x.verts[1].co) / 2) * x.calc_length() for x in comp), Vector()) / L
        loops.append((cen, L))
    bm.free()
    # the loop round the bone: nearest middle to it
    best = None
    for cen, L in loops:
        off = cen - p
        off -= u * off.dot(u)
        if best is None or off.length < best[0].length:
            best = (off, L)
    return best


def measure(body, kb, key, segs, faces):
    mw = body.matrix_world
    base = [mw @ k.co for k in kb['Basis'].data]
    keyed = [mw @ k.co for k in kb[key].data]
    samples = []   # (chain distance, world correction)
    acc = 0.0
    for i, (name, h, t) in enumerate(segs):
        L = (t - h).length
        u = (t - h) / L
        f = FIRST if i == 0 else STEP
        while f < 1.0 - 1e-6:
            a = sections(base, faces, h, u, f * L)
            b = sections(keyed, faces, h, u, f * L)
            if a and b:
                samples.append((acc + f * L, b[0] - a[0], a[1], b[1], name, f))
            f += STEP
        acc += L
    # drop slices whose key loop takes in the trunk (far longer than the next one along)
    good = []
    for j, s in enumerate(samples):
        nxt = samples[j + 1][3] if j + 1 < len(samples) else s[3]
        if s[3] > TRUNK_GIRTH * nxt or s[2] > TRUNK_GIRTH * (samples[j + 1][2] if j + 1 < len(samples) else s[2]):
            continue
        good.append(s)
    return good


def smoothed(samples):
    import math
    out = []
    for s in samples:
        w = [math.exp(-0.5 * ((t[0] - s[0]) / SMOOTH) ** 2) for t in samples]
        c = sum((t[1] * wi for t, wi in zip(samples, w)), Vector()) / sum(w)
        out.append((s[0], c) + tuple(s[2:]))
    return out


def correction_at(samples, s):
    if s <= samples[0][0]:
        return samples[0][1]
    if s >= samples[-1][0]:
        return samples[-1][1]
    for a, b in zip(samples, samples[1:]):
        if a[0] <= s <= b[0]:
            t = (s - a[0]) / (b[0] - a[0])
            return a[1].lerp(b[1], t)
    return samples[-1][1]


def along_moves(body, kb, key, segs, shares):
    """Each arm vertex's chain distance and the key's move of it along its
    bone (a vector along that bone), in world space."""
    mw = body.matrix_world
    m3 = mw.to_3x3()
    base = kb['Basis'].data
    data = kb[key].data
    out = []
    for i in shares:
        s, j = project(segs, mw @ base[i].co)
        _, h, t = segs[j]
        u = (t - h).normalized()
        d = m3 @ (data[i].co - base[i].co)
        out.append((s, i, u * d.dot(u)))
    return out


def along_field(moves, shares, s):
    """The moves along the bones near chain distance s, averaged over SMOOTH
    and weighted by each vertex's share on the arm, so the shape the key gives
    the arm (a muscle swelling, which slants the surface) is kept and only
    the arm's slide along its bones is taken."""
    import math
    tot = 0.0
    acc = Vector()
    for t, i, m in moves:
        w = math.exp(-0.5 * ((t - s) / SMOOTH) ** 2) * shares[i]
        tot += w
        acc += m * w
    return acc / tot if tot else Vector()


def along(body, kb, key, segs, shares, report):
    to_local = body.matrix_world.inverted().to_3x3()
    moves = along_moves(body, kb, key, segs, shares)
    fixes = {i: along_field(moves, shares, s) for s, i, _ in moves}
    worst = max(f.length for f in fixes.values())
    report(f"ARMS {key} along: worst slide {worst:.3f}")
    for s, i, _ in sorted(moves)[::max(1, len(moves) // 12)]:
        report(f"  s {s:.2f} slide {fixes[i].length:.3f}")
    data = kb[key].data
    for i, f in fixes.items():
        data[i].co -= to_local @ (f * shares[i])
    after = along_moves(body, kb, key, segs, shares)
    left = max(along_field(after, shares, s).length for s, _, _ in after)
    report(f"ARMS {key} along after: worst slide {left:.3f}")


def centre(key_names, report=print):
    body = bpy.data.objects[BODY]
    rig = bpy.data.objects[RIG]
    # In Edit Mode the edit mesh is written back over the key blocks on
    # leaving it or saving, so leave it first and come back after.
    editing = body.data.is_editmode
    if editing:
        with bpy.context.temp_override(active_object=body, object=body, selected_objects=[body]):
            bpy.ops.object.mode_set(mode='OBJECT')
    kb = body.data.shape_keys.key_blocks
    mw = body.matrix_world
    to_local = mw.inverted().to_3x3()
    faces = [list(p.vertices) for p in body.data.polygons]
    groups = {g.index: g.name for g in body.vertex_groups}
    deform = {b.name for b in rig.data.bones if b.use_deform}
    for key in key_names:
        for side in SIDES:
            segs = chain_of(rig, side)
            arm = arm_bones(rig, side)
            shares = {}
            for v in body.data.vertices:
                tot = sum(g.weight for g in v.groups if groups.get(g.group) in deform)
                a = sum(g.weight for g in v.groups if groups.get(g.group) in arm)
                if tot > 0 and a > 0:
                    shares[v.index] = a / tot
            if ALONG:
                along(body, kb, key, segs, shares, report)
            for n in range(PASSES):
                samples = measure(body, kb, key, segs, faces)
                worst = max(s[1].length for s in samples)
                report(f"ARMS {key} {side} pass {n}: {len(samples)} slices, worst drift {worst:.3f}")
                if n == 0:
                    for s in samples:
                        report(f"  {s[4]} {s[5]:.2f} drift ({s[1].x:+.3f},{s[1].y:+.3f},{s[1].z:+.3f})")
                if worst < 0.01:
                    break
                samples = smoothed(samples)
                data = kb[key].data
                base = kb['Basis'].data
                for i, w in shares.items():
                    s, _ = project(segs, mw @ base[i].co)
                    data[i].co -= to_local @ (correction_at(samples, s) * w)
            samples = measure(body, kb, key, segs, faces)
            report(f"ARMS {key} {side} after: worst drift {max(s[1].length for s in samples):.3f}")
    body.data.update()
    if editing:
        with bpy.context.temp_override(active_object=body, object=body, selected_objects=[body]):
            bpy.ops.object.mode_set(mode='EDIT')


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    out = argv[0] if argv else None
    keys = argv[1:] or KEYS
    centre(keys)
    if out:
        if bpy.path.abspath(out) == bpy.data.filepath:
            raise SystemExit("ARMS: won't save over the .blend it opened")
        bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)
        print("ARMS saved", out)


main()
