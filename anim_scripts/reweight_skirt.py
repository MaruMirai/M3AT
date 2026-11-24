"""Weights a hand-made skirt and, with --dress, makes the dress of the same length from it.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/reweight_skirt.py -- <skirt.glb> <out.glb>
        [--name Name] [--dress <dress out.glb> --dress-name Name [--dress-top <top.glb>]] [--reference <reference.glb>]

skirt.glb is a skirt edited by hand and saved without the rig (Models/bases/skirts/, Maru's): a closed tube from a ring at
the waist down to the hem, no weights, its shape keys broken by Symmetrize (they carry a move across without flipping it
in x). reference.glb is make_duster.py's skirt of the same length, closed (--skirt --hem <z> --no-vents), whose weights go
from the body's at the waist toward each thigh and below the knee the shin, the front more than the back, as a skirt
must move (made here by running make_duster.py, at the hem's height in the body's frame, unless given). Each vertex of the hand-made skirt takes the reference's weights at the nearest point on its surface (looked
for a little in toward the vertex's own faces, so either side of a seam takes its own side's). Its shape keys are dropped:
export_frames.py fits every body key from the body (a skirt's hanging part smoothed, `hung`). Written skinned to the rig,
as import_garments takes it, and read back to check every vertex has weights.

--dress joins the top of make_duster.py's dress on: the T-shirt's group (-BasicShirt) pushed DRESS_OFFSET off the skin
(the shirt layer's), cut along the skirt's waist ring (its height round the body; a vertex within SNAP over the cut left
out, its edges cut, so there are no slivers), its weights the body's under it; then welded to the ring with no strip
between: each cut vertex onto the ring vertex it pairs with (each the other's nearest round the body, within
PAIR_TURN), the others put on the ring's edge (or the cut edge) where they fall, so the join is the ring itself with a
few points added along it. Its keys are fitted at export too. --dress-top takes the top from a file instead (made by
hand above the same ring, extract_dress_top.py: Models/bases/skirts/dresstop.glb, Maru's V neck), its UVs kept, its
weights the body's at the nearest point on the skin, welded on the same way.
"""
import bpy, bmesh, os, sys, math
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.geometry import barycentric_transform

DRESS_GROUP = '-BasicShirt'
DRESS_OFFSET = 0.1      # Blender units off the skin (make_duster.py's skirts and dresses, the shirt layer's)
SNAP = 0.12             # a vertex this near over the cut is left out, its edges cut instead, so there are no slivers
PAIR_TURN = 7.0         # degrees round the body: a cut vertex and a ring vertex this near (each the other's nearest) are one
INWARD = 0.05           # a vertex's nearest point looked for this share of the way toward its faces' middle
INFLUENCES = 6

argv = sys.argv[sys.argv.index('--') + 1:]
def option(name, default=None):
    if name in argv:
        k = argv.index(name)
        value = argv[k + 1]
        del argv[k:k + 2]
        return value
    return default
skirt_name = option('--name')
dress_out = option('--dress')
dress_name = option('--dress-name', 'Dress')
dress_top = option('--dress-top')   # a dress top made by hand (extract_dress_top.py), welded on in place of the one cut here
reference_path = option('--reference')
if len(argv) < 2:
    sys.exit('reweight_skirt.py: give <skirt.glb> <out.glb>')
master_path, out = (os.path.abspath(a) for a in argv[:2])
skirt_name = skirt_name or os.path.splitext(os.path.basename(out))[0]

for o in bpy.data.objects:   # a mesh saved in Edit Mode has its edits outside its data
    if o.type == 'MESH' and o.mode != 'OBJECT':
        bpy.context.view_layer.objects.active = o
        bpy.ops.object.mode_set(mode='OBJECT')
body = bpy.data.objects['Body']
rig = body.find_armature()
bones = {b.name for b in rig.data.bones if b.use_deform}
to_body = body.matrix_world.inverted()


def imported(path):
    """The mesh of a .glb, in the body's frame, and what came in with it (to remove)."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    came = [o for o in bpy.data.objects if o not in before]
    mesh = next(o for o in came if o.type == 'MESH')
    return mesh, came


# The hand-made skirt: welded (the .glb is split at its UV seams; the loops keep their UVs), in the body's frame, its
# keys dropped.
skirt, skirt_came = imported(master_path)
for o in skirt_came:
    if o is not skirt:
        bpy.data.objects.remove(o)
skirt.shape_key_clear()
skirt.data.transform(to_body @ skirt.matrix_world)
skirt.matrix_world = body.matrix_world.copy()
skirt.parent = None
bm = bmesh.new()
bm.from_mesh(skirt.data)
bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
bm.verts.index_update()
bm.verts.ensure_lookup_table()

# The reference, made at the skirt's hem unless given.
if reference_path is None:
    import subprocess, tempfile
    reference_path = os.path.join(tempfile.mkdtemp(prefix='m3at-skirt-'), 'reference.glb')
    hem = min(v.co.z for v in bm.verts)
    here = os.path.dirname(os.path.abspath(__file__))
    run = subprocess.run([bpy.app.binary_path, '-b', bpy.data.filepath, '--python', os.path.join(here, 'make_duster.py'), '--',
                          reference_path, '--skirt', 'medium', '--hem', '%.4f' % hem, '--no-vents'], capture_output=True, text=True)
    if run.returncode != 0 or not os.path.exists(reference_path):
        sys.exit('reweight_skirt.py: making the reference failed:\n' + run.stdout[-2000:] + run.stderr[-2000:])
    print('REFERENCE make_duster.py --skirt --hem %.2f --no-vents' % hem)
# The reference: its triangles in the body's frame and each vertex's weights.
ref, ref_came = imported(reference_path)
ref_pts = [to_body @ (ref.matrix_world @ v.co) for v in ref.data.vertices]
ref.data.calc_loop_triangles()
ref_tris = [tuple(t.vertices) for t in ref.data.loop_triangles]
ref_names = {g.index: g.name for g in ref.vertex_groups}
ref_weights = [{ref_names[g.group]: g.weight for g in v.groups if ref_names[g.group] in bones and g.weight > 0}
               for v in ref.data.vertices]
ref_tree = BVHTree.FromPolygons(ref_pts, ref_tris)
for o in ref_came:
    bpy.data.objects.remove(o)


def weights_at(q):
    """The reference's weights at its surface's nearest point to q."""
    loc, _, ti, _ = ref_tree.find_nearest(q)
    a, b, c = ref_tris[ti]
    w = barycentric_transform(loc, ref_pts[a], ref_pts[b], ref_pts[c], Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)))
    w = [max(0.0, x) for x in w]
    total = sum(w) or 1.0
    out_ = {}
    for i, t in zip((a, b, c), w):
        for bone, x in ref_weights[i].items():
            out_[bone] = out_.get(bone, 0.0) + x * t / total
    return out_


# Each vertex's weights, looked for a little toward its faces' middle.
skirt_weights = []
for v in bm.verts:
    middle = sum((f.calc_center_median() for f in v.link_faces), Vector()) / max(1, len(v.link_faces))
    skirt_weights.append(weights_at(v.co.lerp(middle, INWARD)))
n_skirt = len(bm.verts)
print('SKIRT %s: %d vertices, %d faces, hem at %.2f' % (skirt_name, n_skirt, len(bm.faces), min(v.co.z for v in bm.verts)))


def boundary_loops(b, verts):
    """The open edge loops among verts, each as its vertices in order."""
    edges = [e for e in b.edges if e.is_boundary and e.verts[0] in verts and e.verts[1] in verts]
    left = set(edges)
    loops = []
    while left:
        e = left.pop()
        loop = [e.verts[0], e.verts[1]]
        while True:
            nxt = next((x for x in loop[-1].link_edges if x in left), None)
            if nxt is None:
                break
            left.discard(nxt)
            v = nxt.other_vert(loop[-1])
            if v is loop[0]:
                break
            loop.append(v)
        loops.append(loop)
    return loops


def write(obj, weights, path, name):
    """obj on the rig with these weights (by vertex), written as a skinned .glb and read back."""
    obj.name = name
    obj.data.name = name
    obj.vertex_groups.clear()
    for vi, ws in enumerate(weights):
        strongest = sorted(ws.items(), key=lambda bw: -bw[1])[:INFLUENCES]
        total = sum(w for _, w in strongest)
        for bone, w in strongest:
            if total > 0 and w / total > 1e-4:
                (obj.vertex_groups.get(bone) or obj.vertex_groups.new(name=bone)).add([vi], w / total, 'REPLACE')
    obj.parent = rig
    obj.matrix_world = body.matrix_world.copy()
    obj.modifiers.clear()
    obj.modifiers.new('Armature', 'ARMATURE').object = rig
    obj['own_weights'] = True
    rig.hide_set(False)
    rig.hide_viewport = False
    rig.hide_select = False
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format='GLB', export_extras=True,
                              export_skins=True, export_morph=True, export_morph_normal=False,
                              export_animations=False, export_apply=False, export_materials='NONE')
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=path, disable_bone_shape=True)
    back = [o for o in bpy.data.objects if o not in before]
    weighted = sum(1 for o in back if o.type == 'MESH' for v in o.data.vertices if v.groups)
    came = sum(len(o.data.vertices) for o in back if o.type == 'MESH')
    for o in back:
        bpy.data.objects.remove(o)
    if weighted != came:
        sys.exit('reweight_skirt.py: %d of %d vertices have weights in %s' % (weighted, came, path))
    print('GLB %s (%s)' % (path, name))


def outward(b, axis):
    """Turns every face out from the vertical line through axis if most face in (the bridge's and the top's winding)."""
    bmesh.ops.recalc_face_normals(b, faces=b.faces)
    out_ = sum(f.calc_area() * f.normal.dot(Vector((f.calc_center_median().x - axis.x, f.calc_center_median().y - axis.y, 0)).normalized())
               for f in b.faces)
    if out_ < 0:
        bmesh.ops.reverse_faces(b, faces=b.faces)


# The waist ring: the skirt's highest open loop.
loops = boundary_loops(bm, set(bm.verts))
ring = max(loops, key=lambda l: sum(v.co.z for v in l) / len(l))
axis = sum((v.co for v in ring), Vector()) / len(ring)
outward(bm, axis)
bm.to_mesh(skirt.data)
write(skirt, skirt_weights, out, skirt_name)

if dress_out:
    me = body.data
    names = {g.index: g.name for g in body.vertex_groups}

    def body_weights(i):
        ws = {names[g.group]: g.weight for g in me.vertices[i].groups if names[g.group] in bones and g.weight > 0}
        total = sum(ws.values())
        return {b_: w / total for b_, w in ws.items()} if total else {}

    # The body pushed out to the shirt layer, as make_duster.py does (area-weighted normals).
    pos = [v.co.copy() for v in me.vertices]
    normal = [Vector() for _ in pos]
    for p in me.polygons:
        vs = list(p.vertices)
        for k in range(1, len(vs) - 1):
            c = (pos[vs[k]] - pos[vs[0]]).cross(pos[vs[k + 1]] - pos[vs[0]])
            for i in (vs[0], vs[k], vs[k + 1]):
                normal[i] += c
    off = [p + n.normalized() * DRESS_OFFSET for p, n in zip(pos, normal)]

    # The cut's height round the body: the ring's height at each azimuth about its middle.
    by_angle = sorted((math.atan2(v.co.y - axis.y, v.co.x - axis.x), v.co.z) for v in ring)
    def cut_at(c):
        a = math.atan2(c.y - axis.y, c.x - axis.x)
        for k in range(len(by_angle)):
            a0, z0 = by_angle[k - 1]
            a1, z1 = by_angle[k]
            if k == 0:
                a0 -= 2 * math.pi
            if a0 <= a <= a1 or (k == 0 and a < a1):
                t = 0.0 if a1 == a0 else (a - a0) / (a1 - a0)
                return z0 + (z1 - z0) * min(1.0, max(0.0, t))
        a0, z0 = by_angle[-1]
        a1, z1 = by_angle[0][0] + 2 * math.pi, by_angle[0][1]
        return z0 + (z1 - z0) * (a - a0) / (a1 - a0)

    group = next(vg.index for vg in body.vertex_groups if DRESS_GROUP in vg.name.split())
    if dress_top:
        group = -1   # (the top comes from the file: nothing cut here)
    inside = {v.index for v in me.vertices if any(g.group == group and g.weight > 0 for g in v.groups)}
    def kept(i):
        return off[i].z >= cut_at(off[i]) + SNAP
    places, at = [], {}
    def place(a, b_=None, t=0.0):
        key = (a, a, 0.0) if b_ is None else (a, b_, t)
        if key not in at:
            at[key] = len(places)
            places.append(key)
        return at[key]
    faces = []
    for p in me.polygons:
        vs = list(p.vertices)
        if not all(i in inside for i in vs) or not any(kept(i) for i in vs):
            continue
        f = []
        for k, a in enumerate(vs):
            b_ = vs[(k + 1) % len(vs)]
            if kept(a):
                f.append(place(a))
            if kept(a) != kept(b_):
                lo, hi = sorted((a, b_))
                t = 0.5
                for _ in range(6):   # where the edge meets the cut
                    q = off[lo].lerp(off[hi], t)
                    dz = off[hi].z - off[lo].z
                    t = min(1.0, max(0.0, (cut_at(q) - off[lo].z) / dz)) if abs(dz) > 1e-6 else 0.5
                f.append(place(lo, hi, t))
        if len(f) >= 3:
            faces.append(f)

    # The dress: the skirt's welded mesh with the top's faces added, welded along the ring.
    db = bmesh.new()
    db.from_mesh(skirt.data)
    db.verts.ensure_lookup_table()
    wmap = {db.verts[i]: skirt_weights[i] for i in range(n_skirt)}
    top_layer = db.faces.layers.int.new('top')
    top_verts = []
    if dress_top:
        # The top from the file, welded, in the body's frame; each vertex the body's weights at its nearest skin.
        me.calc_loop_triangles()
        body_tris = [tuple(t.vertices) for t in me.loop_triangles]
        body_tree = BVHTree.FromPolygons(pos, body_tris)
        def skin_weights(q):
            loc, _, ti, _ = body_tree.find_nearest(q)
            a_, b_, c_ = body_tris[ti]
            w = barycentric_transform(loc, pos[a_], pos[b_], pos[c_], Vector((1, 0, 0)), Vector((0, 1, 0)), Vector((0, 0, 1)))
            w = [max(0.0, x) for x in w]
            total = sum(w) or 1.0
            ws = {}
            for i, t in zip((a_, b_, c_), w):
                for k, x in body_weights(i).items():
                    ws[k] = ws.get(k, 0.0) + x * t / total
            return ws
        top_obj, top_came = imported(os.path.abspath(dress_top))
        tb = bmesh.new()
        tb.from_mesh(top_obj.data)
        tb.transform(to_body @ top_obj.matrix_world)
        bmesh.ops.remove_doubles(tb, verts=tb.verts, dist=1e-4)
        for o in top_came:
            bpy.data.objects.remove(o)
        new_of = {}
        for v in tb.verts:
            nv = db.verts.new(v.co)
            wmap[nv] = skin_weights(v.co)
            new_of[v] = nv
            top_verts.append(nv)
        uv_from = tb.loops.layers.uv.active
        uv_to = db.loops.layers.uv.active or db.loops.layers.uv.new('UVMap')
        for f in tb.faces:
            try:
                nf = db.faces.new([new_of[v] for v in f.verts])
            except ValueError:
                continue
            nf[top_layer] = 2   # (2: its UVs from the file)
            if uv_from is not None:
                for l_to, l_from in zip(nf.loops, f.loops):
                    l_to[uv_to].uv = l_from[uv_from].uv
        tb.free()
    for a_, b_, t in places:
        v = db.verts.new(off[a_].lerp(off[b_], t))
        ws = {k: w * (1 - t) for k, w in body_weights(a_).items()}
        for k, w in body_weights(b_).items():
            ws[k] = ws.get(k, 0.0) + w * t
        wmap[v] = ws
        top_verts.append(v)
    for f in faces:
        try:
            db.faces.new([top_verts[i] for i in f])[top_layer] = 1
        except ValueError:
            pass   # (a face twice, from a polygon cut into one)
    db.verts.ensure_lookup_table()
    ring_loop = [db.verts[v.index] for v in ring]
    seam = min(boundary_loops(db, set(top_verts)), key=lambda l: sum(v.co.z for v in l) / len(l))

    def turn(co):
        return math.degrees(math.atan2(co.y - axis.y, co.x - axis.x))
    def apart(a_, b_):
        return abs((a_ - b_ + 180.0) % 360.0 - 180.0)
    def nearest(v, others):
        return min(others, key=lambda o: apart(turn(v.co), turn(o.co)))
    # Pairs: each the other's nearest round the body, near enough.
    pairs = {}
    for t_ in seam:
        r_ = nearest(t_, ring_loop)
        if nearest(r_, seam) is t_ and apart(turn(t_.co), turn(r_.co)) <= PAIR_TURN:
            pairs[t_] = r_

    def split_into(loop, v):
        """Splits the edge of loop (vertices in order round it) that v falls on, round the body, at v's turn; the new vertex."""
        a_ = turn(v.co)
        for k in range(len(loop)):
            p_, q_ = loop[k], loop[(k + 1) % len(loop)]
            span = (turn(q_.co) - turn(p_.co) + 180.0) % 360.0 - 180.0
            along = (a_ - turn(p_.co) + 180.0) % 360.0 - 180.0
            if span != 0 and 0.0 <= along / span <= 1.0:
                fac = along / span
                edge = db.edges.get((p_, q_))
                if edge is None:
                    continue
                _, nv = bmesh.utils.edge_split(edge, p_, fac)
                nv.co = p_.co.lerp(q_.co, fac)
                wa, wb = wmap[p_], wmap[q_]
                wmap[nv] = {k_: wa.get(k_, 0.0) * (1 - fac) + wb.get(k_, 0.0) * fac for k_ in set(wa) | set(wb)}
                loop.insert(k + 1, nv)
                return nv
        sys.exit('reweight_skirt.py: no edge round the join for a vertex at %.1f degrees' % a_)
    weld = dict(pairs)
    for t_ in [x for x in seam if x not in pairs]:
        weld[t_] = split_into(ring_loop, t_)      # a cut vertex with no ring vertex by it: onto the ring's edge
    for r_ in [x for x in list(ring_loop) if x not in pairs.values() and x not in weld.values()]:
        weld[split_into(seam, r_)] = r_           # a ring vertex with no cut vertex by it: the cut edge split there
    bmesh.ops.weld_verts(db, targetmap=weld)
    # A cut face whose corners welded onto one ring edge is left with no area: gone, as is any edge so short.
    bmesh.ops.dissolve_degenerate(db, dist=1e-4, edges=db.edges[:])
    for f in [f for f in db.faces if f.calc_area() < 1e-6]:
        db.faces.remove(f)
    for v in [v for v in db.verts if not v.link_faces]:
        wmap.pop(v, None)
        db.verts.remove(v)
    # Split points lie in a line along the ring's edges, so a face holding them is triangulated here ('beauty'), as
    # the exporter's own fan could lay a triangle flat along that line.
    bmesh.ops.triangulate(db, faces=[f for f in db.faces if len(f.verts) > 3], quad_method='BEAUTY', ngon_method='BEAUTY')
    outward(db, axis)
    for f in db.faces:
        f.smooth = True
    db.verts.index_update()
    weights = [wmap[v] for v in db.verts]
    joined = len(seam)
    dm = bpy.data.meshes.new(dress_name)
    db.to_mesh(dm)
    dress = bpy.data.objects.new(dress_name, dm)
    body.users_collection[0].objects.link(dress)
    # UVs: the skirt's kept; the top a projection of its own.
    bpy.context.view_layer.objects.active = dress
    for o in bpy.context.selected_objects:
        o.select_set(False)
    dress.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='DESELECT')
    bpy.ops.object.mode_set(mode='OBJECT')
    tops = dm.attributes['top'].data
    for p in dm.polygons:
        p.select = tops[p.index].value == 1   # (a top from a file keeps its own UVs)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.uv.smart_project(island_margin=0.02)
    bpy.ops.object.mode_set(mode='OBJECT')
    print('DRESS %s: %d vertices (%d from the skirt), %d faces, a cut edge of %d welded to the ring of %d (%d in pairs)'
          % (dress_name, len(dm.vertices), n_skirt, len(dm.polygons), joined, len(ring), len(pairs)))
    write(dress, weights, os.path.abspath(dress_out), dress_name)
