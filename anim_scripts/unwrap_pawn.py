"""Unwraps the pawn's body and head onto one new atlas, as the rig's own UV maps had been left folded and stacked left on right.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/unwrap_pawn.py -- <report folder> [--save]

The seams are found from the bones, so running it again after a remesh cuts the same way: the trunk and the head into a front
and a back (each ear whole on the front, the cut round the back of its foot), each arm and leg a tube cut along its hidden side, each hand a back and a palm, each foot a top and a sole. Only the
left side is flattened; the right takes its mirror, so a symmetric texture can be painted once. Each island is the same number
of texels to the rig's unit, the limbs stand upright and the front panels are exactly symmetric.

It works on throwaway copies and writes back only the UVs and the edges' seam marks, so the mesh, shape keys and weights can't
change. Without --save it leaves the file alone. Either way it writes <report folder>/after_uv.json, for drawing the layout.
With the Text Editor's Run Script instead, it writes them into the open session, unsaved, and the report to the system's temp folder."""
import json
import math
import os
import sys
import tempfile

import bpy
import numpy as np

PARTS = ('Body', 'Head')
MARGIN = 0.008      # of the atlas between islands
SMOOTH_PASSES = 3   # of majority voting along the front/back line, which otherwise zigzags on a low-poly mesh
UNFOLD_PASSES = 30  # at most
MIN_FACES = 8       # an island with fewer goes to its neighbour
HIDE_BIAS = 6.0     # the part of a cut's cost that is only its length: keeps it from wandering to reach the hidden side
HEAD_DENSITY = 1.3  # texels on the head per texel on the body: the face is what is seen, as the head is half the chibi
# The box the chibi's own ears lie in, in the rig's units: every vertex out to the ring where each meets the skull, and no further.
EAR_OUT = 2.8        # from the middle, either side
EAR_BACK = (-0.4, 1.6)
EAR_UP = (21.5, 24.2)


def swap_side(label):
    return label.replace('.L', '\0').replace('.R', '.L').replace('\0', '.R')


def region_of(group):
    """The part of the body a bone's weight puts a face in, without its side; None for a vertex group that isn't a bone."""
    if group.startswith('-') or group in ('Bone', 'bodyselectnohands'):
        return None
    side = '.L' if '.L' in group else '.R' if '.R' in group else ''
    if group.startswith(('upper_arm', 'forearm')):
        return 'arm' + side
    if group.startswith(('hand', 'thumb')):
        return 'hand' + side
    if group.startswith(('thigh', 'shin')):
        return 'leg' + side
    if group.startswith(('foot', 'toe', 'heel')):
        return 'foot' + side
    return 'trunk'


class Part:
    """One mesh with what the cutting needs: its faces' regions and panels, its edges and its mirror."""

    def __init__(self, obj):
        self.obj = obj
        m = obj.data
        self.places = np.array([v.co[:] for v in m.vertices])
        self.faces = [tuple(p.vertices) for p in m.polygons]
        self.edge_of = {tuple(sorted(e.vertices)): e.index for e in m.edges}
        self.faces_of_edge = {}
        for f, verts in enumerate(self.faces):
            for a, b in zip(verts, verts[1:] + verts[:1]):
                self.faces_of_edge.setdefault(tuple(sorted((a, b))), []).append(f)
        self.neighbours = [[] for _ in self.faces]
        for pair in self.faces_of_edge.values():
            if len(pair) == 2:
                self.neighbours[pair[0]].append(pair[1])
                self.neighbours[pair[1]].append(pair[0])
        self.centres = np.array([self.places[list(v)].mean(0) for v in self.faces])
        self.mirror = self.find_mirror()
        self.mirror_face = self.find_mirror_faces()

    def find_mirror(self):
        found = {tuple(np.round(p, 4)): i for i, p in enumerate(self.places)}
        out = []
        for p in self.places:
            key = tuple(np.round((-p[0], p[1], p[2]), 4))
            if key not in found:
                raise RuntimeError(self.obj.name + ': not mirror symmetric at ' + str(p))
            out.append(found[key])
        return np.array(out)

    def find_mirror_faces(self):
        by_verts = {frozenset(v): f for f, v in enumerate(self.faces)}
        out = []
        for v in self.faces:
            key = frozenset(self.mirror[list(v)].tolist())
            if key not in by_verts:
                raise RuntimeError(self.obj.name + ': a face has no mirror (the triangles differ across the middle)')
            out.append(by_verts[key])
        return np.array(out)

    def regions(self):
        obj = self.obj
        names = {g.index: region_of(g.name) for g in obj.vertex_groups}
        per_vertex = []
        for v in obj.data.vertices:
            amounts = {}
            for g in v.groups:
                if names.get(g.group):
                    amounts[names[g.group]] = amounts.get(names[g.group], 0.0) + g.weight
            per_vertex.append(amounts)
        out = []
        for verts in self.faces:
            total = {}
            for v in verts:
                for name, w in per_vertex[v].items():
                    total[name] = total.get(name, 0.0) + w
            out.append(max(total, key=total.get) if total else 'trunk')
        return out

    def smooth(self, labels, group):
        """Majority voting among a face's neighbours in the same group, the same both sides of the middle."""
        for _ in range(SMOOTH_PASSES):
            new = list(labels)
            for f in range(len(self.faces)):
                near = [labels[n] for n in self.neighbours[f] if group[n] == group[f]]
                if near:
                    top = max(sorted(set(near), reverse=True), key=near.count)
                    if near.count(top) >= 2:
                        new[f] = top
            labels = self.even_sides(new)
        return labels

    def even_sides(self, labels):
        """The right's labels as the left's, its side swapped."""
        return [labels[f] if self.centres[f][0] >= 0 else swap_side(labels[self.mirror_face[f]]) for f in range(len(self.faces))]

    def absorb_small(self, labels):
        """An island too small to be worth a seam of its own goes to the neighbour it shares the most with."""
        for _ in range(20):
            small = [i for i in self.islands(self.ring_seams(labels)) if len(i) < MIN_FACES]
            if not small:
                return labels
            labels = list(labels)
            for island in small:
                inside = set(island)
                near = [labels[n] for f in island for n in self.neighbours[f] if n not in inside]
                if near:
                    top = max(sorted(set(near), reverse=True), key=near.count)
                    for f in island:
                        labels[f] = top
            labels = self.even_sides(labels)
        return labels

    def cut_holes(self, seams):
        """Each island to a disk: from any outline but its longest a cut to the longest, along the figure's hidden side, the same
        on the right."""
        def hidden(a, b):
            mid = (self.places[a] + self.places[b]) / 2
            return 1.0 + 0.3 * abs(mid[0]) - 0.1 * mid[1] + HIDE_BIAS
        for _ in range(40):
            for island in self.islands(seams):
                if np.mean(self.centres[island], axis=0)[0] < -1e-6:
                    continue
                loops = self.boundary_loops(island, seams)
                if len(loops) > 1:
                    path = self.path_between(loops[-1], loops[0], island, hidden)
                    if not path:
                        raise RuntimeError(self.obj.name + ': no way to cut an island open')
                    seams = mirrored(self, seams | set(path))
                    break
            else:
                return seams
        raise RuntimeError(self.obj.name + ': too many holes')

    def ring_seams(self, labels):
        """Every edge between two faces of different labels."""
        return {e for e, pair in self.faces_of_edge.items() if len(pair) == 2 and labels[pair[0]] != labels[pair[1]]}

    def path_between(self, starts, ends, faces, cost_of):
        """The cheapest run of edges within these faces from one set of vertices to another (Dijkstra). Empty if there is none."""
        import heapq
        edges = set()
        for f in faces:
            v = self.faces[f]
            edges.update(tuple(sorted((a, b))) for a, b in zip(v, v[1:] + v[:1]))
        near = {}
        for a, b in edges:
            near.setdefault(a, []).append(b)
            near.setdefault(b, []).append(a)
        best = {s: 0.0 for s in starts if s in near}
        before = {}
        heap = [(0.0, s) for s in best]
        heapq.heapify(heap)
        while heap:
            d, a = heapq.heappop(heap)
            if d > best[a]:
                continue
            if a in ends:
                path = []
                while a in before:
                    path.append(tuple(sorted((a, before[a]))))
                    a = before[a]
                return path
            for b in near[a]:
                step = np.linalg.norm(self.places[a] - self.places[b]) * cost_of(a, b)
                if d + step < best.get(b, math.inf):
                    best[b] = d + step
                    before[b] = a
                    heapq.heappush(heap, (d + step, b))
        return []

    def boundary_loops(self, faces, seams):
        """The vertices of each closed outline these faces have, along seams or the group's edge: largest first."""
        faces = set(faces)
        rim = {}
        for f in faces:
            v = self.faces[f]
            for a, b in zip(v, v[1:] + v[:1]):
                e = tuple(sorted((a, b)))
                inside = [x for x in self.faces_of_edge[e] if x in faces]
                if len(inside) == 1 or e in seams:
                    rim.setdefault(a, set()).add(b)
                    rim.setdefault(b, set()).add(a)
        loops, seen = [], set()
        for start in rim:
            if start in seen:
                continue
            stack, loop = [start], set()
            while stack:
                a = stack.pop()
                if a in loop:
                    continue
                loop.add(a)
                stack.extend(rim[a])
            seen |= loop
            loops.append(loop)
        return sorted(loops, key=len, reverse=True)

    def islands(self, seams):
        seen, out = set(), []
        for f in range(len(self.faces)):
            if f in seen:
                continue
            stack, island = [f], []
            seen.add(f)
            while stack:
                g = stack.pop()
                island.append(g)
                v = self.faces[g]
                for a, b in zip(v, v[1:] + v[:1]):
                    e = tuple(sorted((a, b)))
                    if e in seams:
                        continue
                    for h in self.faces_of_edge[e]:
                        if h not in seen:
                            seen.add(h)
                            stack.append(h)
            out.append(island)
        return out


def body_seams(part):
    regions = part.regions()
    panels = list(regions)
    grouped = {}
    for f, r in enumerate(regions):
        grouped.setdefault(r, []).append(f)

    trunk = grouped.get('trunk', [])
    # Front and back: in front of the trunk's own middle at that height, as it leans and the chest and seat stand out.
    if trunk:
        z = part.centres[trunk, 2]
        y = part.centres[trunk, 1]
        order = np.argsort(z)
        middle = {}
        for chunk in np.array_split(order, max(6, len(trunk) // 40)):
            mid = (y[chunk].min() + y[chunk].max()) / 2
            for i in chunk:
                middle[trunk[i]] = mid
        for f in trunk:
            panels[f] = 'front' if part.centres[f][1] < middle[f] else 'back'

    for side in ('.L', '.R'):
        hand = grouped.get('hand' + side, [])
        if hand:
            # A back and a palm, either side of the plane the hand is flattest across.
            verts = sorted({v for f in hand for v in part.faces[f]})
            at = part.places[verts]
            _, _, axes = np.linalg.svd(at - at.mean(0))
            flat = axes[2] * (1 if axes[2][0] >= 0 else -1)
            for f in hand:
                panels[f] = 'palm' + side if (part.centres[f] - at.mean(0)) @ flat < 0 else 'hand_back' + side
        foot = grouped.get('foot' + side, [])
        if foot:
            # The sole is what looks down.
            for f in foot:
                p = part.places[list(part.faces[f])]
                normal = np.cross(p[1] - p[0], p[2] - p[0])
                panels[f] = 'sole' + side if normal[2] < -0.6 * np.linalg.norm(normal) else 'foot_top' + side

    panels = part.absorb_small(part.smooth(panels, regions))
    return part.cut_holes(mirrored(part, part.ring_seams(panels))), panels


def head_seams(part):
    # Front and back at the ears' plane: the face is one piece. An ear goes whole with the front, so the cut runs round the back
    # of it, where it meets the head, and not through it.
    widest = part.places[np.abs(part.places[:, 0]) > np.abs(part.places[:, 0]).max() * 0.85]
    plane = widest[:, 1].mean()
    ear = [all(EAR_OUT <= abs(part.places[v][0]) and EAR_BACK[0] <= part.places[v][1] <= EAR_BACK[1]
               and EAR_UP[0] <= part.places[v][2] <= EAR_UP[1] for v in verts) for verts in part.faces]
    panels = ['front' if ear[f] or c[1] < plane else 'back' for f, c in enumerate(part.centres)]
    panels = part.smooth(panels, ['head'] * len(panels))
    panels = ['front' if ear[f] else p for f, p in enumerate(panels)]
    # A face against the ear's foot left between two front ones would take the cut off the foot, round it.
    on_ear = {v for f, verts in enumerate(part.faces) if ear[f] for v in verts}
    panels = ['front' if on_ear & set(part.faces[f]) and sum(panels[n] == 'front' for n in part.neighbours[f]) >= 2 else p
              for f, p in enumerate(panels)]
    panels = part.absorb_small(part.even_sides(panels))
    return part.cut_holes(mirrored(part, part.ring_seams(panels))), panels


def mirrored(part, seams):
    return seams | {tuple(sorted(part.mirror[list(e)].tolist())) for e in seams}


def copy_flat(obj):
    """A copy of the part at its basis shape, with nothing on it to deform or to write back to shape keys."""
    o = obj.copy()
    o.data = obj.data.copy()
    o.modifiers.clear()
    o.shape_key_clear()
    o.name = obj.name + '_flat'
    o.parent = None
    bpy.context.scene.collection.objects.link(o)
    return o


def signed_area(uv, faces):
    total = 0.0
    for f in faces:
        c = uv[f]
        for i in range(1, len(c) - 1):
            a, b = c[i] - c[0], c[i + 1] - c[0]
            total += (a[0] * b[1] - a[1] * b[0]) / 2
    return total


class Flat:
    """The UVs of one flat copy, per face and corner, by vertex."""

    def __init__(self, part, flat_obj, seams):
        self.part = part
        self.m = flat_obj.data
        data = self.m.uv_layers.active.data
        self.uv = {}
        self.loop_of = {}
        for f, p in enumerate(self.m.polygons):
            self.uv[f] = np.array([data[i].uv[:] for i in p.loop_indices], float)
            self.loop_of[f] = list(p.loop_indices)
        self.islands = part.islands(seams)

    def write(self):
        data = self.m.uv_layers.active.data
        for f, loops in self.loop_of.items():
            for i, li in enumerate(loops):
                data[li].uv = self.uv[f][i]

    def island_points(self, island):
        at = {}
        for f in island:
            for v, p in zip(self.part.faces[f], self.uv[f]):
                at[v] = p
        return at

    def size_in_3d(self, island):
        total = 0.0
        for f in island:
            p = self.part.places[list(self.part.faces[f])]
            for i in range(1, len(p) - 1):
                total += np.linalg.norm(np.cross(p[i] - p[0], p[i + 1] - p[0])) / 2
        return total

    def transform(self, island, fn):
        for f in island:
            self.uv[f] = np.array([fn(p) for p in self.uv[f]])

    def upright(self, island):
        """Same scale everywhere, and turned so the island's own length (a limb's from the far end up, the rest the figure's up) is +v."""
        wanted = math.sqrt(self.size_in_3d(island) / max(abs(signed_area(self.uv, island)), 1e-12))
        centre = np.mean([p for f in island for p in self.uv[f]], axis=0)
        self.transform(island, lambda p: (p - centre) * wanted)
        at = self.island_points(island)
        points = np.array(list(at.values()))
        places = self.part.places[list(at.keys())]
        if self.is_own_mirror(island):
            # Across the middle: its left to right goes along u, and its up is up. A round head has no length to go by.
            way = sum(p - at[self.part.mirror[v]] for v, p in at.items() if self.part.places[v][0] > 1e-6)
            turn = -math.atan2(way[1], way[0])
            along = places[:, 2] - places[:, 2].mean()
            fit = np.linalg.lstsq(points - points.mean(0), along, rcond=None)[0]
            if -math.sin(turn) * fit[0] + math.cos(turn) * fit[1] < 0:
                turn += math.pi
        else:
            # The way to go in the island for its length: a least squares fit of that along the island's plane. A limb's
            # length is along its own bones, not up, as the figure stands in an A pose.
            spread = np.linalg.svd(places - places.mean(0))[2][0]
            if spread[2] < 0:
                spread = -spread
            fit = np.linalg.lstsq(points - points.mean(0), (places - places.mean(0)) @ spread, rcond=None)[0]
            turn = math.pi / 2 - math.atan2(fit[1], fit[0])
        c, s = math.cos(turn), math.sin(turn)
        self.transform(island, lambda p: np.array((c * p[0] - s * p[1], s * p[0] + c * p[1])))
        if signed_area(self.uv, island) < 0:  # a face seen from outside should read as it does on the figure
            self.transform(island, lambda p: np.array((-p[0], p[1])))

    def unfold(self, island):
        """A triangle the flattening left folded over: its corners moved to the middle of their neighbours until none is."""
        def folded():
            return [f for f in island if signed_area(self.uv, [f]) <= 0]
        for _ in range(UNFOLD_PASSES):
            bad = folded()
            if not bad:
                return
            at = self.island_points(island)
            near = {}
            for f in island:
                v = self.part.faces[f]
                for a, b in zip(v, v[1:] + v[:1]):
                    near.setdefault(a, set()).add(b)
                    near.setdefault(b, set()).add(a)
            moved = {v: np.mean([at[n] for n in near[v]], axis=0) for f in bad for v in self.part.faces[f]}
            for f in island:
                self.uv[f] = np.array([moved.get(v, p) for v, p in zip(self.part.faces[f], self.uv[f])])

    def is_own_mirror(self, island):
        faces = set(island)
        return all(self.part.mirror_face[f] in faces for f in island)

    def symmetrise(self, island):
        """A panel across the middle exactly symmetric: each vertex and its mirror meet half way, the middle's on the line."""
        at = self.island_points(island)
        mirror = self.part.mirror
        line = np.mean([(p[0] + at[mirror[v]][0]) / 2 for v, p in at.items()])
        fixed = {v: np.array(((p[0] + 2 * line - at[mirror[v]][0]) / 2, (p[1] + at[mirror[v]][1]) / 2)) for v, p in at.items()}
        for f in island:
            self.uv[f] = np.array([fixed[v] for v in self.part.faces[f]])

    def copy_mirror(self, island):
        """The right's island as the left's mirror, a little way off: the packing puts it in its place."""
        mirror = self.part.mirror
        for f in island:
            source = self.part.mirror_face[f]
            at = {v: p for v, p in zip(self.part.faces[source], self.uv[source])}
            self.uv[f] = np.array([np.array((-at[mirror[v]][0], at[mirror[v]][1])) for v in self.part.faces[f]])

    def scale(self, island, amount):
        centre = np.mean([p for f in island for p in self.uv[f]], axis=0)
        self.transform(island, lambda p: centre + (p - centre) * amount)


def flatten(parts, flats):
    bpy.ops.object.select_all(action='DESELECT')
    for o in flats.values():
        o.select_set(True)
    bpy.context.view_layer.objects.active = next(iter(flats.values()))


def main():
    in_session = '--' not in sys.argv  # the Text Editor's Run Script: into the open session, for the person there to save
    args = [] if in_session else sys.argv[sys.argv.index('--') + 1:]
    report = os.path.join(tempfile.gettempdir(), 'unwrap_pawn') if in_session else args[0]
    save = '--save' in args
    if bpy.context.object and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')

    active, selected = bpy.context.view_layer.objects.active, list(bpy.context.selected_objects)

    parts, seams, panels, flats = {}, {}, {}, {}
    for name in PARTS:
        part = parts[name] = Part(bpy.data.objects[name])
        s, p = (head_seams if name == 'Head' else body_seams)(part)
        seams[name], panels[name] = mirrored(part, s), p
        flats[name] = copy_flat(part.obj)
        for e in flats[name].data.edges:
            e.use_seam = tuple(sorted(e.vertices)) in seams[name]

    flatten(parts, flats)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.reveal()
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.unwrap(method='ANGLE_BASED', fill_holes=True, margin=0.0)
    bpy.ops.object.mode_set(mode='OBJECT')

    uv_flats = {}
    for name, part in parts.items():
        flat = uv_flats[name] = Flat(part, flats[name], seams[name])
        for island in flat.islands:
            mid = np.mean(part.centres[island], axis=0)
            own = flat.is_own_mirror(island)
            if own or mid[0] > 0:  # the left, and what crosses the middle
                flat.upright(island)
                flat.unfold(island)
                if own:
                    flat.symmetrise(island)
        for island in flat.islands:
            if not flat.is_own_mirror(island) and np.mean(part.centres[island], axis=0)[0] < 0:
                flat.copy_mirror(island)
        if name == 'Head':
            for island in flat.islands:
                flat.scale(island, HEAD_DENSITY)
        for i, island in enumerate(flat.islands):  # spread out, as the unpacked islands all lie on one another
            flat.transform(island, lambda p: p + np.array((i * 3.0, 0.0)))
        flat.write()

    flatten(parts, flats)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.select_all(action='SELECT')
    bpy.ops.uv.pack_islands(udim_source='CLOSEST_UDIM', rotate=False, scale=True, merge_overlap=False, margin_method='FRACTION',
                            margin=MARGIN, pin=False, shape_method='CONCAVE')
    bpy.ops.object.mode_set(mode='OBJECT')

    os.makedirs(report, exist_ok=True)
    out = {}
    for name, part in parts.items():
        data = flats[name].data.uv_layers.active.data
        out[name] = [[list(data[i].uv) for i in p.loop_indices] for p in flats[name].data.polygons]
        flat = uv_flats[name]
        names = []
        for island in flat.islands:
            names.append((panels[name][island[0]], len(island), len(part.boundary_loops(island, seams[name]))))
        print('UNWRAP', name, 'islands', len(flat.islands), 'seams', len(seams[name]))
        for row in sorted(names):
            print('UNWRAP  ', *row)
    json.dump(out, open(os.path.join(report, 'after_uv.json'), 'w'))

    if save or in_session:
        for name, part in parts.items():
            m = part.obj.data
            src = flats[name].data.uv_layers.active.data
            dst = m.uv_layers.active.data
            for i in range(len(dst)):
                dst[i].uv = src[i].uv
            for e in m.edges:
                e.use_seam = tuple(sorted(e.vertices)) in seams[name]
    for o in flats.values():
        mesh = o.data
        bpy.data.objects.remove(o)
        bpy.data.meshes.remove(mesh)
    bpy.ops.object.select_all(action='DESELECT')
    for o in selected:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active
    if save:
        # Blender doesn't write what nothing uses: unwrapping shouldn't lose a material, or the image it alone holds.
        for material in bpy.data.materials:
            if material.users == 0:
                material.use_fake_user = True
        bpy.ops.wm.save_mainfile()
        print('UNWRAP saved', bpy.data.filepath)


main()
