# Re-weights the plate armour's torso (output_unwrapped) so it bends at the waistline: the breastplate rides the chest
# bones whole and takes nothing from the arms or legs, the skirt rides the hips with only its hem giving to the thighs,
# and the turn between them all happens in a band at the waist. Weights the body transfer put on the arms and legs from
# the wrong side of those lines come off too. Lengths are the mesh's own units (the rig's: a figure about 29 tall).
#
# The mesh is one connected surface, so it is first split into plate, skirt, arms and legs: each grows from seed
# vertices over the surface, and an edge is dear to cross where the surface folds inward, so they meet in the creases.
#
# To run it in the open session (Ctrl-Z takes it back): the Text Editor's Open, then Run Script, then save as usual.
# Or on a copy: blender -b <copy.blend> --python anim_scripts/weight_armor_torso.py -- <out.blend>
import heapq
import sys

import bpy
import numpy as np

OBJECT = 'output_unwrapped'

WAIST_LOW = 13.45    # all hips below this height
WAIST_HIGH = 14.75   # all chest above it; the one row of vertices round the waist's crease is at about 14
CHEST_LOW = 16.7     # spine.002 below, spine.003 above (they barely turn apart, but the arms and neck hang off .003)
CHEST_HIGH = 17.8
COLLAR_LOW = 19.6    # the collar's rim leans with the neck, as much as COLLAR_NECK at the top, so a bowed head clears it
COLLAR_HIGH = 20.2
COLLAR_NECK = 0.2
HEM_TOP = 12.4       # the skirt takes nothing from the thighs above this
HEM_BOTTOM = 11.2    # and HEM_SHARE of them at the hem: less and a seated thigh slits the skirt's front, more and it bends
HEM_SHARE = 0.5
HEM_SIDES = 0.8      # across the middle the hem shares the thighs out over this far either side
ARM_BLEND = 1.2      # the arm's vertices ease onto the chest over this far along the surface; the plate's own never do
LEG_BLEND = 0.8      # the leg's top eases into the skirt over this far along the surface
CREASE_INWARD = 30.0     # how dear an edge is by the square of its fold (radians) where the surface folds inward
CREASE_OUTWARD = 2.0     # and where it folds outward
FAINT = 0.005        # a weight under this is dropped

ARM_BONES = ('shoulder', 'upper_arm', 'forearm', 'hand', 'thumb')
LEG_BONES = ('thigh', 'shin', 'foot', 'toe', 'pelvis', 'spine')   # what a leg may keep: 'spine' is matched whole below
PLATE, SKIRT, ARM, LEG = 0, 1, 2, 3


def smooth(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def seeds(places):
    """The vertices each part grows from, by where they are at rest."""
    x, y, z = places.T
    side = np.abs(x)
    plate = ((side < 1.6) & (z > 15.2) & (z < 18.0)) | ((side < 1.2) & (z > 19.3) & (y < 0.0))
    skirt = (z > 12.0) & (z < 13.2) & (np.hypot(x, y) > 2.2)
    arm = (side > 7.0) | ((side > 4.6) & (z > 16.5) & (z < 19.0)) | ((side > 3.2) & (z > 17.0))
    leg = (z < 10.7) | ((z < 11.1) & (y > 0.3))   # up to the hem, which is higher at the back
    return [plate, skirt, arm, leg]


def creased_edges(places, tris):
    """Each vertex's neighbours, with the cost of the edge: its length, more the sharper the surface folds along it."""
    normals = np.cross(places[tris[:, 1]] - places[tris[:, 0]], places[tris[:, 2]] - places[tris[:, 0]])
    normals /= np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-9)
    middles = places[tris].mean(axis=1)
    faces = {}
    for f, t in enumerate(tris):
        for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            faces.setdefault((min(a, b), max(a, b)), []).append(f)
    plain = [[] for _ in places]
    creased = [[] for _ in places]
    for (a, b), fs in faces.items():
        length = float(np.linalg.norm(places[a] - places[b]))
        cost = 1.0
        if len(fs) == 2:
            f, g = fs
            fold = float(np.arccos(np.clip(normals[f] @ normals[g], -1.0, 1.0)))
            inward = (middles[g] - middles[f]) @ normals[f] > 1e-5
            cost += (CREASE_INWARD if inward else CREASE_OUTWARD) * fold * fold
        plain[a].append((b, length))
        plain[b].append((a, length))
        creased[a].append((b, length * cost))
        creased[b].append((a, length * cost))
    return plain, creased


def spread(sets, edges):
    """Grows each set of vertices over the surface. Returns how far each vertex is from the nearest set, and which."""
    dist = np.full(len(edges), np.inf)
    label = np.full(len(edges), -1)
    heap = []
    for k, members in enumerate(sets):
        for i in np.where(members)[0]:
            dist[i] = 0.0
            label[i] = k
            heap.append((0.0, int(i)))
    heapq.heapify(heap)
    while heap:
        d, i = heapq.heappop(heap)
        if d > dist[i]:
            continue
        for j, cost in edges[i]:
            if d + cost < dist[j]:
                dist[j] = d + cost
                label[j] = label[i]
                heapq.heappush(heap, (d + cost, j))
    return dist, label


def only_bones(weights, keep):
    """The weights on just these bones (one column flag per bone), summing to one again where any are left."""
    kept = weights * keep
    total = kept.sum(axis=1, keepdims=True)
    return np.where(total > 1e-6, kept / np.maximum(total, 1e-6), 0.0)


def new_weights(places, tris, old, names):
    bone = {n: i for i, n in enumerate(names)}
    x, y, z = places.T
    plain, creased = creased_edges(places, tris)
    _, label = spread(seeds(places), creased)
    label[label < 0] = LEG   # a loose piece the parts did not reach: between the legs
    weights = np.zeros_like(old)

    # The trunk, by height only: which of plate and skirt a vertex is hardly matters, as the waist lies between them.
    torso = (label == PLATE) | (label == SKIRT)
    chest = smooth(WAIST_LOW, WAIST_HIGH, z)
    hips = 1.0 - chest
    upper = smooth(CHEST_LOW, CHEST_HIGH, z)
    hem = smooth(HEM_TOP, HEM_BOTTOM, z) * HEM_SHARE
    left = smooth(-HEM_SIDES, HEM_SIDES, x)   # thigh.L is on +x
    weights[torso, bone['spine']] = (hips * (1.0 - hem))[torso]
    weights[torso, bone['spine.002']] = (chest * (1.0 - upper))[torso]
    weights[torso, bone['spine.003']] = (chest * upper)[torso]
    weights[torso, bone['thigh.L']] = (hips * hem * left)[torso]
    weights[torso, bone['thigh.R']] = (hips * hem * (1.0 - left))[torso]
    neck = smooth(COLLAR_LOW, COLLAR_HIGH, z) * COLLAR_NECK
    collar = torso & (neck > 0.0)
    weights[collar, bone['spine.004']] = neck[collar]
    weights[collar, bone['spine.003']] -= neck[collar]

    # The arms keep their own bones, eased onto the chest at their inner edge, on their side of it.
    arm = label == ARM
    own = only_bones(old, np.array([n.startswith(ARM_BONES) for n in names], dtype=float))
    plate = torso & (z > WAIST_HIGH)
    away, _ = spread([plate], plain)
    share = smooth(0.0, ARM_BLEND, away)[:, None]
    on_chest = np.zeros_like(old)
    on_chest[:, bone['spine.003']] = 1.0
    weights[arm] = (share * own + (1.0 - share) * on_chest)[arm]
    bare = arm & (weights.sum(axis=1) < 0.5)
    weights[bare] = on_chest[bare]

    # The legs keep their own bones, and their top eases into the weights of the skirt next to it.
    leg = label == LEG
    keep = np.array([n.startswith(LEG_BONES[:-1]) or n == 'spine' for n in names], dtype=float)
    weights[leg] = only_bones(old, keep)[leg]
    away, _ = spread([torso], plain)
    share = smooth(0.0, LEG_BLEND, away)
    trunk = np.where(torso)[0]
    for i in np.where(leg & (away < LEG_BLEND))[0]:
        near = trunk[np.argmin(((places[trunk] - places[i]) ** 2).sum(axis=1))]
        weights[i] = share[i] * weights[i] + (1.0 - share[i]) * weights[near]

    weights[weights < FAINT] = 0.0
    weights /= np.maximum(weights.sum(axis=1, keepdims=True), 1e-9)
    print('M3AT weight_armor_torso: plate %d, skirt %d, arms %d, legs %d vertices' % tuple(int((label == k).sum()) for k in (PLATE, SKIRT, ARM, LEG)))
    return weights


def run():
    ob = bpy.data.objects[OBJECT]
    if bpy.context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')   # edit mode's changes reach the mesh only on leaving it
    if not bpy.app.background:
        bpy.ops.ed.undo_push(message='Before weight_armor_torso')
    me = ob.data
    me.calc_loop_triangles()
    places = np.array([v.co for v in me.vertices], dtype=float)
    tris = np.array([t.vertices for t in me.loop_triangles])
    names = [g.name for g in ob.vertex_groups]
    old = np.zeros((len(places), len(names)))
    for v in me.vertices:
        for g in v.groups:
            old[v.index, g.group] = g.weight

    weights = new_weights(places, tris, old, names)

    for k, group in enumerate(ob.vertex_groups):
        for i in range(len(places)):
            if weights[i, k] > 0.0:
                group.add([i], float(weights[i, k]), 'REPLACE')
            elif old[i, k] > 0.0:
                group.remove([i])
    ob['own_weights'] = True   # the exporter otherwise throws painted weights away for the skin's under each vertex
    me.update()
    if not bpy.app.background:
        bpy.ops.ed.undo_push(message='weight_armor_torso')


run()

if '--' in sys.argv and sys.argv[sys.argv.index('--') + 1:]:
    out = sys.argv[sys.argv.index('--') + 1]
    if bpy.path.abspath(out) == bpy.data.filepath:
        raise SystemExit('weight_armor_torso: will not save over the .blend it opened')
    bpy.ops.wm.save_as_mainfile(filepath=out, copy=True)
