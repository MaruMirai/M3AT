"""Smooth Fatten: Shrink/Fatten (Alt+S) that goes out in steps and smooths after each, so tight inside corners (an
armpit, the crotch) round off instead of folding through themselves.

The same push as anim_scripts/inflate_garment.py and make_fat.py, as an operator with a redo panel (F9):

- Smooth the shape (inflate_garment.py): each step pulls a vertex toward the middle of its neighbours. Good for making
  a garment looser; the openings' edges (the hem, the neck, the sleeves' ends) can be kept out of the smoothing so
  they keep their line.
- Smooth the move (make_fat.py): each step smooths how far each vertex has gone from where it started, not where it
  is, so the mesh's own detail (knees, fingers, the navel) rides along. Good for a body's Fat shape key.

In Edit Mode it moves the selected vertices (the unselected ones stay put and hold the edge of the push). In Object
Mode it moves every vertex of each selected mesh, into its active shape key, or into a new shape key relative to the
basis. The copies of a vertex split along a UV seam (as a glTF import has them) move as one, so the seams stay shut."""
import bpy, bmesh
from bpy.props import BoolProperty, EnumProperty, FloatProperty, IntProperty, StringProperty
from mathutils import Vector
from mathutils.kdtree import KDTree


def twins_of(bm, weld):
    """Groups of vertex indices that share a place (one per vertex when weld is 0)."""
    if weld <= 0:
        return [[v.index] for v in bm.verts]
    kd = KDTree(len(bm.verts))
    for v in bm.verts:
        kd.insert(v.co, v.index)
    kd.balance()
    groups = {}
    for v in bm.verts:
        groups.setdefault(min(j for _, j, _ in kd.find_range(v.co, weld)), []).append(v.index)
    return list(groups.values())


def fatten(bm, start, moving, amount, offset, steps, smooth, method, keep_rims, weld):
    """Pushes bm's vertices from start out along their normals in steps, smoothing after each. moving: which vertex
    indices may move. amount: each vertex's share of the offset. Returns the new places, one per vertex."""
    bm.verts.ensure_lookup_table()
    for v, c in zip(bm.verts, start):
        v.co = c
    twins = twins_of(bm, weld)
    group = [0] * len(bm.verts)
    for g, vs in enumerate(twins):
        for i in vs:
            group[i] = g
    # Each split vertex sees only its own side's edges, so it is smoothed toward the neighbours of all its copies.
    neighbours = [list({e.other_vert(bm.verts[i]).index for i in vs for e in bm.verts[i].link_edges} - set(vs))
                  for vs in twins]
    # An opening's edge is a boundary edge with no copy of itself; a seam's edges come in pairs.
    copies = {}
    for e in bm.edges:
        if e.is_boundary:
            k = frozenset(group[v.index] for v in e.verts)
            copies[k] = copies.get(k, 0) + 1
    rim = {group[v.index] for e in bm.edges if e.is_boundary
           and copies[frozenset(group[v.index] for v in e.verts)] == 1 for v in e.verts} if keep_rims else set()
    # A group moves if any copy is selected, all of it together, by its largest share.
    live = [g for g, vs in enumerate(twins) if any(moving[i] for i in vs)]
    share = {g: max(amount[i] for i in twins[g]) for g in live}

    for _ in range(steps):
        bm.normal_update()
        for g in live:
            vs = twins[g]
            n = sum((bm.verts[i].normal for i in vs), Vector())
            if n.length_squared == 0:
                continue
            push = n.normalized() * (offset / steps * share[g])
            for i in vs:
                bm.verts[i].co += push
        cos = [v.co.copy() for v in bm.verts]
        moved = {}
        for g in live:
            nb = neighbours[g]
            i = twins[g][0]
            if g in rim or not nb:
                moved[g] = cos[i]
            elif method == 'SHAPE':
                moved[g] = cos[i].lerp(sum((cos[j] for j in nb), Vector()) / len(nb), smooth)
            else:
                d = cos[i] - start[i]
                mean = sum((cos[j] - start[j] for j in nb), Vector()) / len(nb)
                moved[g] = start[i] + d.lerp(mean, smooth)
        for g, co in moved.items():
            for i in twins[g]:
                bm.verts[i].co = co
    return [v.co.copy() for v in bm.verts]


class MESH_OT_smooth_fatten(bpy.types.Operator):
    """Push vertices out along their normals in steps, smoothing after each, so inside corners round off"""
    bl_idname = "mesh.smooth_fatten"
    bl_label = "Smooth Fatten"
    bl_options = {'REGISTER', 'UNDO'}

    offset: FloatProperty(name="Offset", description="How far out (negative goes in)", default=0.02,
                          subtype='DISTANCE', unit='LENGTH', soft_min=-1, soft_max=1, step=0.1, precision=4)
    steps: IntProperty(name="Steps", description="How many pieces it is pushed out in. More keep each push smaller "
                       "than a corner's curve, but smooth that many more times", default=10, min=1, soft_max=50)
    smooth: FloatProperty(name="Smooth", description="How far each pass pulls a vertex toward its neighbours' "
                          "middle (shape) or its move toward theirs (move)", default=0.5, min=0, max=1,
                          subtype='FACTOR')
    method: EnumProperty(name="Smooth", items=(
        ('SHAPE', "Shape", "Smooth where each vertex is: corners and small detail round off (a looser garment)"),
        ('MOVE', "Move", "Smooth how far each vertex has gone: the mesh's own detail rides along (a fat body)")),
        default='SHAPE')
    keep_rims: BoolProperty(name="Keep Openings' Edges", description="Leave the boundary edges (a hem, a neck, a "
                            "sleeve's end) out of the smoothing, so they keep their line", default=True)
    weld: FloatProperty(name="Join Split Copies", description="Vertices this close move as one, so UV seams split "
                        "on import stay shut (0: off)", default=1e-5, min=0, soft_max=0.001, precision=6,
                        subtype='DISTANCE', unit='LENGTH')
    vertex_group: StringProperty(name="Group", description="Vertices in this group take Group Share of the offset, "
                                 "by their weight (as make_fat.py does the hands)")
    group_share: FloatProperty(name="Group Share", description="The share of the offset a vertex fully in the group "
                               "takes", default=0.4, min=0, soft_max=1, subtype='FACTOR')
    new_key: BoolProperty(name="New Shape Key", description="Object Mode: write the result as a new shape key "
                          "relative to the basis, from the basis, instead of into the active shape", default=False)
    key_name: StringProperty(name="Key Name", default="Fat")

    @classmethod
    def poll(cls, context):
        return context.mode in {'EDIT_MESH', 'OBJECT'} and any(o.type == 'MESH' for o in context.selected_objects
                                                                + [context.active_object] if o)

    def draw(self, context):
        lay = self.layout
        lay.use_property_split = True
        for p in ("offset", "steps", "smooth", "method", "keep_rims", "weld"):
            lay.prop(self, p)
        o = context.active_object
        if o and o.type == 'MESH':
            lay.prop_search(self, "vertex_group", o, "vertex_groups")
        else:
            lay.prop(self, "vertex_group")
        row = lay.row()
        row.active = bool(self.vertex_group)
        row.prop(self, "group_share")
        if context.mode == 'OBJECT':
            lay.prop(self, "new_key")
            row = lay.row()
            row.active = self.new_key
            row.prop(self, "key_name")

    def amounts(self, o, bm):
        vg = o.vertex_groups.get(self.vertex_group) if self.vertex_group else None
        if vg is None:
            return [1.0] * len(bm.verts)
        deform = bm.verts.layers.deform.active
        if deform is None:
            return [1.0] * len(bm.verts)
        return [1.0 - (1.0 - self.group_share) * min(1.0, v[deform].get(vg.index, 0.0)) for v in bm.verts]

    def run(self, bm, start, moving, amount):
        return fatten(bm, start, moving, amount, self.offset, self.steps, self.smooth, self.method,
                      self.keep_rims, self.weld)

    def execute(self, context):
        if context.mode == 'EDIT_MESH':
            done = 0
            for o in context.objects_in_mode_unique_data:
                if o.type != 'MESH':
                    continue
                em = bmesh.from_edit_mesh(o.data)
                moving = [v.select and not v.hide for v in em.verts]
                if not any(moving):
                    continue
                # Work on a copy so the edit mesh's own normals and state are only touched once, at the end.
                work = em.copy()
                start = [v.co.copy() for v in em.verts]
                out = self.run(work, start, moving, self.amounts(o, em))
                work.free()
                for v, c in zip(em.verts, out):
                    v.co = c
                bmesh.update_edit_mesh(o.data, loop_triangles=True, destructive=False)
                done += 1
            if not done:
                self.report({'WARNING'}, "Nothing selected")
                return {'CANCELLED'}
            return {'FINISHED'}

        objs = [o for o in context.selected_objects if o.type == 'MESH'] or [context.active_object]
        for o in objs:
            me = o.data
            keys = me.shape_keys
            if self.new_key:
                if not keys:
                    o.shape_key_add(name="Basis", from_mix=False)
                    keys = me.shape_keys
                source = keys.reference_key
            else:
                source = o.active_shape_key if keys else None
            start = [Vector(d.co) for d in (source.data if source else me.vertices)]
            work = bmesh.new()
            work.from_mesh(me)
            out = self.run(work, start, [True] * len(start), self.amounts(o, work))
            work.free()
            if self.new_key:
                key = keys.key_blocks.get(self.key_name) or o.shape_key_add(name=self.key_name, from_mix=False)
                key.relative_key = keys.reference_key
                target = key.data
            else:
                target = source.data if source else me.vertices
            for d, c in zip(target, out):
                d.co = c
            me.update()
        return {'FINISHED'}


def menu(self, context):
    self.layout.operator(MESH_OT_smooth_fatten.bl_idname)


def register():
    bpy.utils.register_class(MESH_OT_smooth_fatten)
    bpy.types.VIEW3D_MT_transform.append(menu)
    bpy.types.VIEW3D_MT_transform_object.append(menu)


def unregister():
    bpy.types.VIEW3D_MT_transform_object.remove(menu)
    bpy.types.VIEW3D_MT_transform.remove(menu)
    bpy.utils.unregister_class(MESH_OT_smooth_fatten)
