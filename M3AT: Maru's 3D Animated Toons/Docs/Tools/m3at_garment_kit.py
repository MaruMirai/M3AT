"""M3AT garment kit: make apparel and gene parts for M3AT in Blender, without M3AT's own rig.

Load it in Blender (4.2 or newer) with the Text Editor's Run Script, or install it as an add-on (Edit > Preferences >
Add-ons > Install from Disk). It adds an "M3AT" tab to the 3D view's sidebar (N) with:

  Import body       the chibi's body from the installed M3AT (Models/chibi/skin.txt and anim.txt): one mesh in its rest
                    pose with every body shape as a shape key (Male, Female, Thin, Fat, Hulk, ...), a vertex group per
                    bone, and an armature you can pose to see a garment bend. 1 Blender unit is 1 map cell, the figure
                    facing -Y, as Blender's front view shows it.
  Import garment    one of M3AT's own garments (garment_<Name>.txt and its .png), with its texture places and image, to
                    retexture it or model from it.
  Garment from selected faces
                    the body's faces selected in Edit Mode, copied and pushed out off the skin by its apparel layer's
                    offset: a garment that fits exactly and can't clip, made from the body's own few faces.
  Open limbs / Close limbs
                    for a generated mesh (image-to-3D, a scan) to be wrapped in one skin: Open limbs copies it with the
                    thighs swung out and the arms up, so the wrap can't fuse the legs or an arm to the side; Close limbs
                    brings the wrapped copy back to the rest pose, skinned to the rig, optionally made symmetric.
  Capture frame     (power armour and other suits that change the body) the rig's current pose, bones moved and
                    scaled in Pose Mode, kept on the selected garment as the frame its wearer is drawn in: longer
                    legs, a broader chest, bigger hands. Model the garment around the posed body; Export writes it
                    at rest with the frame, and M3AT draws its wearer in that frame, animated. Show frame puts the
                    rig back in a garment's frame, Rest pose takes it out.
  Export            the selected garments to your mod's M3ATgarments folder as garment_<Name>.txt (and its image),
                    with the settings in the panel. M3AT fits the rest (weights, body shapes, hidden skin) in the game.

Nothing here writes into M3AT's own folder. See Docs/Apparel.md and Docs/GeneParts.md beside this file.

Game space (the files): x, y up, z, the figure facing -z; Blender's (x, y, z) is written as (-x, z, -y), so a triangle's
corners go the other way round in the file.
"""

bl_info = {
    "name": "M3AT garment kit",
    "author": "Maru (M3AT)",
    "version": (1, 3, 0),
    "blender": (4, 2, 0),
    "location": "3D View > Sidebar > M3AT",
    "description": "Make apparel and gene parts for M3AT: Maru's 3D Animated Toons",
    "category": "Import-Export",
}

import json
import math
import os
import re
import bpy
import bmesh
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

KIT_VERSION = 4   # 4: writes the body line (the body it was made on)
# Off the skin by apparel layer, cells (M3AT's make_from_groups.py LAYER_OFFSET, 0.1 of its Blender units a layer):
# far enough apart that each layer's garment covers the one under it, close enough that nothing shows a gap.
LAYER_OFFSET = {'Underwear': 0.0030, 'OnSkin': 0.0042, 'Middle': 0.0085, 'Shell': 0.0127, 'Webbing': 0.0170}
TEXTURE_SIZE = 1024   # the most pixels across a written image (M3AT's own are this or less)
TRIANGLES_LOW = 800      # a garment over this many triangles is told it is heavy
TRIANGLES_MOST = 2500     # M3AT's budget for a garment worn on the body (Source/GarmentBudget.cs): past it the game optimizes it
TRIANGLES_HEAD = 1500     # and for one worn on the head
TEXTURE_MOST = 1024       # and its image's longer side, pixels: past it the game shrinks it
BODY = 'M3AT_Body'
RIG = 'M3AT_Rig'


# ---------------------------------------------------------------------------------------------------------- reading

def to_blender(x, y, z):
    return Vector((-x, -z, y))


def to_game(v):
    return (-v.x, v.z, -v.y)


# Blender's axes to the game's as a matrix, for a bone's deform (a frame line): game = TO_GAME @ blender.
TO_GAME = Matrix(((-1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))


def stamp(path):
    """skin.txt's hash, as M3AT names the body it fits garments to (FNV-1a, 64 bits, hex)."""
    h = 14695981039346656037
    with open(path, 'rb') as f:
        for b in f.read():
            h ^= b
            h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return '%016x' % h


def body_line(context):
    """The body line for a garment made now: the installed skin.txt's hash and its generation, or None without one.
    M3AT's body changes between updates; the line tells it (and you) which body a garment was made on."""
    skin = os.path.join(chibi_folder(context), 'skin.txt')
    if not os.path.isfile(skin):
        return None
    generation = 0
    with open(skin) as f:
        for line in f:
            if line.startswith('generation '):
                generation = int(line.split()[1])
                break
    return 'body %s %d' % (stamp(skin), generation)


def read_bones(chibi):
    """anim.txt's bones: (name, head in game space, parent number) by number."""
    bones = []
    with open(os.path.join(chibi, 'anim.txt')) as f:
        for line in f:
            if line.startswith('bone '):
                p = line.split()
                bones.append((p[1], (float(p[2]), float(p[3]), float(p[4])), int(p[5]) if len(p) > 5 else -1))
            elif bones and line.startswith('clip '):
                break
    return bones


def read_piece(path):
    """A skin.txt or garment file: vertices, triangles, weights, texture places, keys and its other lines."""
    piece = {'v': [], 'f': [], 'w': [], 't': [], 'keys': {}, 'lines': {}}
    key = None
    with open(path) as f:
        for raw in f:
            p = raw.split()
            if not p or p[0].startswith('#'):
                continue
            k = p[0]
            if k == 'v':
                piece['v'].append(tuple(float(x) for x in p[1:4]))
            elif k == 'f':
                piece['f'].append(tuple(int(x) for x in p[1:4]))
            elif k == 'w':
                piece['w'].append([(int(p[i]), float(p[i + 1])) for i in range(1, len(p) - 1, 2)])
            elif k == 't':
                piece['t'].append((float(p[1]), float(p[2])))
            elif k == 'key':
                key = {}
                piece['keys'][p[1]] = key
            elif k == 'd' and key is not None:
                key[int(p[1])] = tuple(float(x) for x in p[2:5])
            elif k in ('db', 'dc', 'fv', 'ff', 'hide', 'opens', 'visor', 'skin', 'scalp', 'hairline'):
                piece['lines'].setdefault(k, []).extend(p[1:])
            else:
                piece['lines'][k] = raw.strip()[len(k):].strip()
    return piece


def chibi_folder(context):
    folder = bpy.path.abspath(context.scene.m3at_kit.chibi)
    if not os.path.isfile(os.path.join(folder, 'skin.txt')):
        sub = os.path.join(folder, 'Models', 'chibi')   # the mod's own folder given
        if os.path.isfile(os.path.join(sub, 'skin.txt')):
            return sub
    return folder


# ---------------------------------------------------------------------------------------------------------- settings

LAYERS = [('Underwear', 'Underwear', "Under everything (UnderWhere's layers)"),
          ('OnSkin', 'On skin', 'Shirts, trousers'),
          ('Middle', 'Middle', 'Vests, armour under a coat'),
          ('Shell', 'Shell', 'Coats, jackets, dusters'),
          ('Webbing', 'Webbing', "Combat Extended's webbing, over armour")]


class M3ATGarment(bpy.types.PropertyGroup):
    """A garment's settings, written as its file's lines (M3AT's Source/ModGarments.cs and SkinnedFigure.ReadMesh)."""
    kind: bpy.props.EnumProperty(name='Kind', items=[('APPAREL', 'Apparel', 'Drawn for the apparel named in Defs'),
                                                     ('GENE', 'Gene part', 'Drawn while the pawn has one of the genes')])
    defs: bpy.props.StringProperty(name='Defs', description='The apparel it is drawn for: ThingDef defNames, space separated')
    genes: bpy.props.StringProperty(name='Genes', description='A gene part: the GeneDef defNames it is drawn for, space separated')
    colour_from: bpy.props.EnumProperty(name='Colour', items=[('skin', 'Skin', "The pawn's skin colour"),
                                                              ('hair', 'Hair', "The pawn's hair colour"),
                                                              ('own', 'Own', 'Its own colour below, or its texture as it is')])
    colour: bpy.props.FloatVectorProperty(name='Own colour', subtype='COLOR', size=3, min=0, max=1, default=(1, 1, 1))
    use_colour: bpy.props.BoolProperty(name='Own colour', description="Drawn in this colour, not the apparel's (or with a gene part's Own)")
    layer: bpy.props.EnumProperty(name='Layer', items=LAYERS, default='OnSkin',
                                  description='Its apparel layer: how far out "Garment from selected faces" pushes it, and the layer it may stand in on')
    stand_in: bpy.props.BoolProperty(name='Stand in on layer and groups', description='Also a stand-in for apparel with no garment of its own worn on this layer over these groups (its Defs already make it one for apparel like its own)')
    groups: bpy.props.StringProperty(name='Groups', default='Torso', description='Body part groups (BodyPartGroupDef) it stands in for, space separated')
    tint: bpy.props.EnumProperty(name='Tint', items=[('auto', 'As the apparel', 'Tinted by the apparel colour if it is made from stuff or dyed'),
                                                     ('yes', 'Always', 'Always tinted by the apparel colour (paint it pale grey)'),
                                                     ('no', 'Never', 'Drawn as painted')])
    cutout: bpy.props.BoolProperty(name='Cut out (alpha)', description='Keep the image\'s alpha: cut out below 0.5. Off: written opaque')
    hide_hair: bpy.props.EnumProperty(name='Hair', items=[('auto', 'Automatic', 'Left off if it covers most of the scalp'),
                                                          ('no', 'Kept', 'The hair is always drawn'),
                                                          ('yes', 'Left off', 'The hair is left off while worn'),
                                                          ('tuck', 'Tucked under', 'The hair cut at its rim and coming out from under it (hats on the head bone)')])
    hide_face: bpy.props.EnumProperty(name='Face', items=[('auto', 'Automatic', 'Left off if it covers most of the face'),
                                                          ('no', 'Kept', 'The face is always drawn'),
                                                          ('yes', 'Left off', 'The face is left off while worn')])
    on_head: bpy.props.BoolProperty(name='Rides the head', description='Moves whole with the head bone (spine.006), not bent by the skin: hats, helmets, masks')
    hard: bpy.props.BoolProperty(name='Hard', description='Keeps its shape on every body type, moved by its bones (plate armour). On by itself for one riding the head')
    hides_skin: bpy.props.BoolProperty(name='Hides the skin under it', default=True, description='Off: the body is drawn under it (see-through veils)')
    hide_bones: bpy.props.StringProperty(name='Also hide bones', description='Hide all the skin on bones starting with these (space separated: thigh shin forearm), for armour that stands well off it')
    hide_clothes: bpy.props.BoolProperty(name='Hides clothes under it', description='Garments on lower layers covering its groups are left off (full body armour)')
    frame: bpy.props.FloatProperty(name="Wearer's frame", default=0.0, min=0.0, max=1.0, step=5, precision=2,
                                   description="Power armour: how much broader and bigger its wearer is drawn (M3AT's Armoured body shape; recon armour 0.25, marine 0.5, cataphract 1). Tick Hard too, so the armour is fitted to it")
    own_weights: bpy.props.BoolProperty(name='Own weights', description="Keep this mesh's vertex groups (named for bones) as its weights, not the skin's under it: long skirts, capes")
    base: bpy.props.StringProperty(name='Based on', description="The M3AT garment it was imported from")
    retexture_only: bpy.props.BoolProperty(name='Retexture only', description='Write only the image and settings, on the M3AT garment it is based on: for a mesh left as it was imported')


class M3ATKit(bpy.types.PropertyGroup):
    chibi: bpy.props.StringProperty(name='M3AT folder', subtype='DIR_PATH',
                                    description="M3AT's Models/chibi folder, or the mod's own folder (in the Workshop's content/294100/3839520750)")
    out: bpy.props.StringProperty(name='Export to', subtype='DIR_PATH', description="Your mod's M3ATgarments folder (at its root)")


# ---------------------------------------------------------------------------------------------------------- importing

class M3AT_OT_import_body(bpy.types.Operator):
    """The chibi's body from M3AT's skin.txt, with its shapes, bone groups and an armature to pose"""
    bl_idname = 'm3at.import_body'
    bl_label = 'Import body'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        chibi = chibi_folder(context)
        skin = os.path.join(chibi, 'skin.txt')
        if not os.path.isfile(skin):
            self.report({'ERROR'}, 'No skin.txt in %s: set the M3AT folder first' % chibi)
            return {'CANCELLED'}
        bones = read_bones(chibi)
        body = read_piece(skin)
        for name in (BODY, RIG):
            old = bpy.data.objects.get(name)
            if old:
                bpy.data.objects.remove(old)
        mesh = bpy.data.meshes.new(BODY)
        mesh.from_pydata([to_blender(*v) for v in body['v']], [], [(a, c, b) for a, b, c in body['f']])
        for p in mesh.polygons:
            p.use_smooth = True
        mesh.update()
        o = bpy.data.objects.new(BODY, mesh)
        context.collection.objects.link(o)
        o['m3at_body'] = stamp(skin)
        groups = [o.vertex_groups.new(name=b[0]) for b in bones]
        for i, ws in enumerate(body['w']):
            for b, w in ws:
                if 0 <= b < len(groups):
                    groups[b].add([i], w, 'REPLACE')
        o.shape_key_add(name='Basis', from_mix=False)
        for name, moves in body['keys'].items():
            k = o.shape_key_add(name=name, from_mix=False)
            k.value = 0.0   # (each came in at 1, every shape at once, and the body swelled over the garments)
            for i, d in moves.items():
                k.data[i].co = to_blender(*(body['v'][i][c] + d[c] for c in range(3)))
        # The armature: each bone from its joint to its first child's (or on along its parent's way), to pose.
        arm = bpy.data.armatures.new(RIG)
        rig = bpy.data.objects.new(RIG, arm)
        context.collection.objects.link(rig)
        context.view_layer.objects.active = rig
        bpy.ops.object.mode_set(mode='EDIT')
        edit = []
        for k, (name, head, parent) in enumerate(bones):
            e = arm.edit_bones.new(name)
            e.head = to_blender(*head)
            edit.append(e)
        for k, (name, head, parent) in enumerate(bones):
            e = edit[k]
            children = [j for j, b in enumerate(bones) if b[2] == k]
            if children:
                # (the child down the same chain, spine to spine.001, else the first)
                same = [j for j in children if bones[j][0].split('.')[0] == name.split('.')[0]]
                tail = edit[(same or children)[0]].head
            elif parent >= 0:
                tail = e.head + (e.head - edit[parent].head).normalized() * 0.04
            else:
                tail = e.head + Vector((0, 0, 0.05))
            if (tail - e.head).length < 0.005:
                tail = e.head + Vector((0, 0, 0.02))
            e.tail = tail
            if parent >= 0:
                e.parent = edit[parent]
        bpy.ops.object.mode_set(mode='OBJECT')
        rig.show_in_front = True
        o.parent = rig
        o.modifiers.new('Armature', 'ARMATURE').object = rig
        o.hide_select = False
        self.report({'INFO'}, 'Body: %d vertices, %d triangles, %d shapes, %d bones' % (len(body['v']), len(body['f']), len(body['keys']), len(bones)))
        return {'FINISHED'}


class M3AT_OT_import_garment(bpy.types.Operator):
    """One of M3AT's own garments (garment_<Name>.txt), with its texture places and image"""
    bl_idname = 'm3at.import_garment'
    bl_label = 'Import garment'
    bl_options = {'REGISTER', 'UNDO'}
    filepath: bpy.props.StringProperty(subtype='FILE_PATH')
    filter_glob: bpy.props.StringProperty(default='garment_*.txt', options={'HIDDEN'})

    def invoke(self, context, event):
        self.filepath = chibi_folder(context) + os.sep
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        import_garment(context, self.filepath)
        return {'FINISHED'}


def import_garment(context, path):
    g = read_piece(path)
    name = os.path.splitext(os.path.basename(path))[0]
    if name.startswith('garment_'):
        name = name[len('garment_'):]
    chibi = os.path.dirname(path)
    bones = [b[0] for b in read_bones(chibi)] if os.path.isfile(os.path.join(chibi, 'anim.txt')) else []
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([to_blender(*v) for v in g['v']], [], [(a, c, b) for a, b, c in g['f']])
    if g['t']:
        uv = mesh.uv_layers.new(name='UVMap')
        for poly in mesh.polygons:
            for li in poly.loop_indices:
                uv.data[li].uv = g['t'][mesh.loops[li].vertex_index]
    for p in mesh.polygons:
        p.use_smooth = True
    # Copies of a vertex split at a texture seam welded back by place (only for the shape: the texture places stay).
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
    bm.to_mesh(mesh)
    bm.free()
    mesh.update()
    o = bpy.data.objects.new(name, mesh)
    context.collection.objects.link(o)
    if bones and g['w'] and len(g['w']) == len(g['v']):
        # (weights by place: the copies were welded, so each place takes its first copy's)
        groups = {}
        at = {}
        for i, v in enumerate(g['v']):
            at.setdefault(tuple(round(c, 5) for c in v), i)
        for vert in mesh.vertices:
            src = at.get(tuple(round(c, 5) for c in to_game(vert.co)))
            for b, w in (g['w'][src] if src is not None else []):
                if 0 <= b < len(bones):
                    grp = groups.get(b) or o.vertex_groups.new(name=bones[b])
                    groups[b] = grp
                    grp.add([vert.index], w, 'REPLACE')
    tex = g['lines'].get('texture')
    if tex and os.path.isfile(os.path.join(chibi, tex)):
        img = bpy.data.images.load(os.path.join(chibi, tex), check_existing=True)
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        bsdf = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
        node = mat.node_tree.nodes.new('ShaderNodeTexImage')
        node.image = img
        mat.node_tree.links.new(node.outputs['Color'], bsdf.inputs['Base Color'])
        o.data.materials.append(mat)
    s = o.m3at
    s.base = name
    s.retexture_only = True
    s.defs = g['lines'].get('defs', '')
    layer = g['lines'].get('layer', '').split()
    if layer and layer[0] in LAYER_OFFSET:
        s.layer = layer[0]
        s.stand_in = True
        s.groups = g['lines'].get('groups', '')
    if 'tint' in g['lines']:
        s.tint = g['lines']['tint']
    for prop in ('hide_hair', 'hide_face'):
        if prop in g['lines'] and g['lines'][prop] in ('yes', 'no', 'tuck'):
            setattr(s, prop, g['lines'][prop])
    ws = g['w']
    if ws and all(len(x) == 1 for x in ws) and len({x[0][0] for x in ws}) == 1:
        s.on_head = True
    s.hide_clothes = g['lines'].get('hide_clothes') == 'yes'
    frame = g['lines'].get('wearer_key', '').split()
    s.frame = float(frame[1]) if len(frame) == 2 and frame[0] == 'Armoured' else 0.0
    context.view_layer.objects.active = o
    return o


class M3AT_OT_from_faces(bpy.types.Operator):
    """A garment from the body's faces selected in Edit Mode, pushed out off the skin by the layer's offset"""
    bl_idname = 'm3at.from_faces'
    bl_label = 'Garment from selected faces'
    bl_options = {'REGISTER', 'UNDO'}
    name: bpy.props.StringProperty(name='Name', default='MyShirt')
    layer: bpy.props.EnumProperty(name='Layer', items=LAYERS, default='OnSkin')
    extra: bpy.props.FloatProperty(name='Extra offset (cells)', default=0.0, min=-0.003, max=0.05, step=0.01, precision=4,
                                   description='More than the layer\'s offset: a bulkier garment')

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        body = bpy.data.objects.get(BODY)
        if body is None:
            self.report({'ERROR'}, 'Import the body first')
            return {'CANCELLED'}
        if body.mode == 'EDIT':
            bpy.ops.object.mode_set(mode='OBJECT')
        me = body.data
        faces = [list(p.vertices) for p in me.polygons if p.select]
        if not faces:
            self.report({'ERROR'}, "Select some of the body's faces in Edit Mode (face select) first")
            return {'CANCELLED'}
        rest = [Vector(kb.co) for kb in me.shape_keys.reference_key.data] if me.shape_keys else [v.co.copy() for v in me.vertices]
        # Area-weighted normals of the whole body, so the copy stands off evenly even at the edge of the selection.
        n = [Vector() for _ in rest]
        for p in me.polygons:
            vs = list(p.vertices)
            for k in range(1, len(vs) - 1):
                c = (rest[vs[k]] - rest[vs[0]]).cross(rest[vs[k + 1]] - rest[vs[0]])
                for i in (vs[0], vs[k], vs[k + 1]):
                    n[i] += c
        offset = LAYER_OFFSET[self.layer] + self.extra
        order = sorted({i for f in faces for i in f})
        index = {i: k for k, i in enumerate(order)}
        mesh = bpy.data.meshes.new(self.name)
        mesh.from_pydata([rest[i] + n[i].normalized() * offset for i in order], [], [[index[i] for i in f] for f in faces])
        for p in mesh.polygons:
            p.use_smooth = True
        mesh.update()
        old = bpy.data.objects.get(self.name)
        if old is not None and old != body:
            bpy.data.objects.remove(old)
        o = bpy.data.objects.new(self.name, mesh)
        context.collection.objects.link(o)
        o.matrix_world = body.matrix_world.copy()
        rig = bpy.data.objects.get(RIG)
        if rig is not None:
            # Posed with the body (the skin's weights copied), to look at; the export takes the rest pose.
            names = {g.index: g.name for g in body.vertex_groups}
            for i in order:
                for e in me.vertices[i].groups:
                    if e.weight > 0:
                        grp = o.vertex_groups.get(names[e.group]) or o.vertex_groups.new(name=names[e.group])
                        grp.add([index[i]], e.weight, 'REPLACE')
            o.parent = rig
            o.modifiers.new('Armature', 'ARMATURE').object = rig
        o.m3at.layer = self.layer
        for p in body.data.polygons:
            p.select = False
        context.view_layer.objects.active = o
        for x in context.selected_objects:
            x.select_set(False)
        o.select_set(True)
        self.report({'INFO'}, '%s: %d faces, %.4f cells off the skin; unwrap it (U) to paint it' % (self.name, len(faces), offset))
        return {'FINISHED'}


# ---------------------------------------------------------------------------------------------------------- open limbs

# A port of M3AT's anim_scripts/open_limbs.py (kept in step by hand): a wrap (a shrink-wrap tool, or Remesh in Voxel
# mode) closes any gap narrower than its detail, so the legs or an arm and the side come out as one lump that tears
# when they move apart. Wrapping the mesh with its limbs opened keeps the gaps wide, and the inverse of the same pose
# brings the wrap back to rest.
OPEN_LEGS = ('thigh.L', 'thigh.R')
OPEN_ARMS = ('upper_arm.L', 'upper_arm.R')
MIDDLE_GAP = 0.02   # cells a mirrored half stays off the middle where it isn't joined, so the legs touch but don't cross


def body_surface(body, to_rig):
    """The body at rest (its Basis), in armature space: a BVH of its triangles, the triangles, points and weights."""
    me = body.data
    rest = [Vector(k.co) for k in me.shape_keys.reference_key.data] if me.shape_keys else [v.co.copy() for v in me.vertices]
    pts = [to_rig @ body.matrix_world @ p for p in rest]
    return mesh_surface(me, pts, group_weights(body))


def mesh_surface(me, pts, weights):
    me.calc_loop_triangles()
    tris = [tuple(t.vertices) for t in me.loop_triangles]
    return BVHTree.FromPolygons(pts, tris), tris, pts, weights


def group_weights(o):
    """Each vertex's weights by bone name, adding up to 1."""
    names = {g.index: g.name for g in o.vertex_groups}
    out = []
    for v in o.data.vertices:
        ws = {names[g.group]: g.weight for g in v.groups if g.weight > 0 and g.group in names}
        t = sum(ws.values())
        out.append({b: w / t for b, w in ws.items()} if t else {})
    return out


def blended_weights(at, surf):
    """The weights at the nearest point on a surface, blended across its triangle by barycentric place."""
    tree, tris, pts, ws = surf
    p, _, k, _ = tree.find_nearest(at)
    if p is None:
        return {}
    tri = tris[k]
    a, b, c = (pts[i] for i in tri)
    v0, v1, v2 = b - a, c - a, p - a
    d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
    den = d00 * d11 - d01 * d01 or 1e-12
    bv = (d11 * d20 - d01 * d21) / den
    bw = (d00 * d21 - d01 * d20) / den
    out = {}
    for i, f in zip(tri, (1 - bv - bw, bv, bw)):
        for bone, w in ws[i].items():
            out[bone] = out.get(bone, 0) + w * f
    t = sum(out.values())
    return {b: w / t for b, w in out.items() if w > 1e-4} if t else {}


def limb_pose(rig, angle, arm_angle):
    """Each moved bone's matrix in armature space: a limb's top bone and everything below it turned about the forward
    axis at its head, whichever way takes a knee outward or an elbow upward."""
    mats = {}
    for roots, deg, opened in ((OPEN_LEGS, angle, lambda was, now: abs(now.x) > abs(was.x)),
                               (OPEN_ARMS, arm_angle, lambda was, now: now.z > was.z)):
        for root in roots:
            bone = rig.data.bones.get(root)
            if bone is None:
                continue
            head = bone.head_local
            for sign in (1, -1):
                turn = Matrix.Translation(head) @ Matrix.Rotation(math.radians(sign * deg), 4, 'Y') @ Matrix.Translation(-head)
                if opened(bone.tail_local, turn @ bone.tail_local):
                    break
            for b in [bone] + list(bone.children_recursive):
                mats[b.name] = turn
    return mats


def skin_matrix(ws, mats):
    """A vertex's blended bone matrix, as linear blend skinning (Blender's Armature modifier) moves it."""
    if not ws:
        return Matrix.Identity(4)
    m = Matrix(((0,) * 4,) * 4)
    for bone, w in ws.items():
        m += mats.get(bone, Matrix.Identity(4)) * w
    return m


def set_weights(o, weights):
    o.vertex_groups.clear()
    groups = {}
    for i, ws in enumerate(weights):
        for b, w in ws.items():
            g = groups.get(b) or o.vertex_groups.new(name=b)
            groups[b] = g
            g.add([i], w, 'REPLACE')


def kit_rig_and_body():
    body, rig = bpy.data.objects.get(BODY), bpy.data.objects.get(RIG)
    if body is None or rig is None:
        return None, None, 'Import the body first'
    return body, rig, None


class M3AT_OT_open_limbs(bpy.types.Operator):
    """A copy of the selected mesh with the thighs swung out and the arms up, to wrap in one skin without the wrap
    fusing the legs together or an arm to the side. Wrap the copy, then Close limbs brings the wrap back to rest"""
    bl_idname = 'm3at.open_limbs'
    bl_label = 'Open limbs'
    bl_options = {'REGISTER', 'UNDO'}
    angle: bpy.props.FloatProperty(name='Legs out (degrees)', default=15.0, min=0.0, max=45.0)
    arm_angle: bpy.props.FloatProperty(name='Arms up (degrees)', default=20.0, min=0.0, max=60.0)

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        body, rig, err = kit_rig_and_body()
        o = context.active_object
        if err or o is None or o.type != 'MESH' or o in (body, rig):
            self.report({'ERROR'}, err or 'Select the mesh to open (fitted over M3AT_Body at rest)')
            return {'CANCELLED'}
        if o.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        to_rig = rig.matrix_world.inverted()
        surf = body_surface(body, to_rig)
        rest = [to_rig @ o.matrix_world @ v.co for v in o.data.vertices]
        ws = [blended_weights(p, surf) for p in rest]
        mats = limb_pose(rig, self.angle, self.arm_angle)
        me = o.data.copy()
        me.name = o.name + '_open'
        back = o.matrix_world.inverted() @ rig.matrix_world
        for v, p, w in zip(me.vertices, rest, ws):
            v.co = back @ (skin_matrix(w, mats) @ p)
        opened = bpy.data.objects.new(o.name + '_open', me)
        context.collection.objects.link(opened)
        opened.matrix_world = o.matrix_world.copy()
        if me.shape_keys:
            opened.shape_key_clear()
        set_weights(opened, ws)   # its weights, for Close limbs to take the wrap's from
        opened['m3at_open'] = json.dumps({'from': o.name, 'angle': self.angle, 'arm_angle': self.arm_angle,
                                          'bones': {b: [list(r) for r in m] for b, m in mats.items()}})
        opened['m3at_open_ref'] = opened.name
        o.hide_set(True)
        for x in context.selected_objects:
            x.select_set(False)
        opened.select_set(True)
        context.view_layer.objects.active = opened
        self.report({'INFO'}, '%s: legs out %.0f, arms up %.0f degrees. Duplicate it (Shift+D) and wrap the duplicate, '
                    'then select the wrap and press Close limbs' % (opened.name, self.angle, self.arm_angle))
        return {'FINISHED'}


def mirrored_weights(ws):
    swap = {'L': 'R', 'R': 'L'}
    return {re.sub(r'\.([LR])(?=\.|$)', lambda m: '.' + swap[m.group(1)], b): w for b, w in ws.items()}


def cut_half(me, pts, side):
    """The wrap's kept half (side +1: +x), cut along the middle while the limbs are open, so only what really joins
    there is cut: its points, faces and the indices of the points on the cut. Loose bits the cut leaves are dropped,
    as a wrap is one closed surface."""
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.verts.ensure_lookup_table()
    for v, p in zip(bm.verts, pts):
        v.co = p
    cut = bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-5, plane_co=(0, 0, 0),
                                 plane_no=(1, 0, 0), clear_inner=side > 0, clear_outer=side < 0)
    seam = {v for v in cut['geom_cut'] if isinstance(v, bmesh.types.BMVert)}
    seen, parts = set(), []
    for f in bm.faces:
        if f in seen:
            continue
        seen.add(f)
        stack, part = [f], []
        while stack:
            x = stack.pop()
            part.append(x)
            for e in x.edges:
                for g in e.link_faces:
                    if g not in seen:
                        seen.add(g)
                        stack.append(g)
        parts.append(part)
    if not parts:
        bm.free()
        return [], [], set()
    parts.sort(key=len, reverse=True)
    order, faces = {}, []
    for f in parts[0]:
        faces.append([order.setdefault(v, len(order)) for v in f.verts])
    points = [v.co.copy() for v in order]
    on_cut = {order[v] for v in seam if v in order}
    bm.free()
    return points, faces, on_cut


class M3AT_OT_close_limbs(bpy.types.Operator):
    """The selected wrap (of a mesh opened by Open limbs) brought back to the rest pose, skinned to M3AT_Rig"""
    bl_idname = 'm3at.close_limbs'
    bl_label = 'Close limbs'
    bl_options = {'REGISTER', 'UNDO'}
    keep: bpy.props.EnumProperty(name='Symmetric', default='none',
                                 items=[('none', 'As wrapped', 'Keep both sides as the wrap made them'),
                                        ('+x', 'Keep +X half', "Cut it down the middle, keep the +X half (the figure's left) and mirror it"),
                                        ('-x', 'Keep -X half', "Cut it down the middle, keep the -X half (the figure's right) and mirror it")])
    fit_box: bpy.props.BoolProperty(name='Fit to the opened mesh', default=False,
                                    description="Scale and move the wrap onto the opened mesh's box first: for a wrap made in another program that came back centred or rescaled")

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def execute(self, context):
        body, rig, err = kit_rig_and_body()
        wrap = context.active_object
        if err or wrap is None or wrap.type != 'MESH':
            self.report({'ERROR'}, err or 'Select the wrap')
            return {'CANCELLED'}
        if wrap.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        # The opened mesh: another selected one Open limbs made, or the one the wrap was duplicated from.
        ref = next((x for x in context.selected_objects if x != wrap and x.type == 'MESH' and 'm3at_open' in x), None)
        if ref is None and 'm3at_open_ref' in wrap:
            ref = bpy.data.objects.get(wrap['m3at_open_ref'])
        if ref is None or ref == wrap:
            self.report({'ERROR'}, 'Also select the mesh Open limbs made (wrap a duplicate of it, so it keeps its weights)')
            return {'CANCELLED'}
        saved = json.loads(ref['m3at_open'])
        mats = {b: Matrix(m) for b, m in saved['bones'].items()}
        to_rig = rig.matrix_world.inverted()
        posed = [to_rig @ ref.matrix_world @ v.co for v in ref.data.vertices]
        surf = mesh_surface(ref.data, posed, group_weights(ref))
        # The wrap as it shows, its modifiers applied (an unapplied Remesh included), in armature space.
        deps = context.evaluated_depsgraph_get()
        me = bpy.data.meshes.new_from_object(wrap.evaluated_get(deps))
        pts = [to_rig @ wrap.matrix_world @ v.co for v in me.vertices]
        if not pts:
            self.report({'ERROR'}, '%s has no vertices' % wrap.name)
            return {'CANCELLED'}
        if self.fit_box:
            def box(ps):
                return (Vector([min(p[i] for p in ps) for i in range(3)]), Vector([max(p[i] for p in ps) for i in range(3)]))
            lo, hi = box(posed)
            l2, h2 = box(pts)
            k = max(hi - lo) / (max(h2 - l2) or 1)
            pts = [(p - (l2 + h2) / 2) * k + (lo + hi) / 2 for p in pts]
        if self.keep != 'none':
            side = 1 if self.keep == '+x' else -1
            half, faces, seam = cut_half(me, pts, side)
            if not half:
                self.report({'ERROR'}, 'Nothing of %s is on the %s side' % (wrap.name, self.keep))
                return {'CANCELLED'}
            ws = [blended_weights(p, surf) for p in half]
            for i in seam:   # even across the middle, so the points on the cut stay on it going back to rest
                both = mirrored_weights(ws[i])
                ws[i] = {b: (ws[i].get(b, 0) + both.get(b, 0)) / 2 for b in set(ws[i]) | set(both)}
            rest = [skin_matrix(w, mats).inverted() @ p for p, w in zip(half, ws)]
            for i, p in enumerate(rest):
                if i in seam:
                    p.x = 0.0
                elif p.x * side < MIDDLE_GAP:
                    p.x = side * MIDDLE_GAP
            index, points, weights = {}, list(rest), list(ws)
            for i, p in enumerate(rest):
                if i in seam:
                    index[i] = i
                else:
                    index[i] = len(points)
                    points.append(Vector((-p.x, p.y, p.z)))
                    weights.append(mirrored_weights(ws[i]))
            faces = faces + [[index[i] for i in reversed(f)] for f in faces]
            bpy.data.meshes.remove(me)
            me = bpy.data.meshes.new(wrap.data.name)
            me.from_pydata([tuple(p) for p in points], [], faces)
            for poly in me.polygons:
                poly.use_smooth = True
            me.update()
        else:
            weights = [blended_weights(p, surf) for p in pts]
            for v, p, w in zip(me.vertices, pts, weights):
                v.co = skin_matrix(w, mats).inverted() @ p
            me.update()
        for m in list(wrap.modifiers):
            wrap.modifiers.remove(m)
        if wrap.data.shape_keys:
            wrap.shape_key_clear()
        old = wrap.data
        wrap.data = me
        if old.users == 0:
            bpy.data.meshes.remove(old)
        name = saved.get('from') or wrap.name
        wrap.parent = None
        wrap.matrix_world = rig.matrix_world.copy()
        set_weights(wrap, weights)
        wrap.parent = rig
        wrap.matrix_parent_inverse = Matrix.Identity(4)
        wrap.modifiers.new('Armature', 'ARMATURE').object = rig
        for k in ('m3at_open', 'm3at_open_ref'):
            if k in wrap:
                del wrap[k]
        if not bpy.data.objects.get(name) or bpy.data.objects.get(name) == wrap:
            wrap.name = name
        else:
            wrap.name = name + '_wrapped'
        ref.hide_set(True)
        tris = sum(len(p.vertices) - 2 for p in me.polygons)
        self.report({'INFO'}, '%s: back at rest, %d vertices, %d triangles, skinned to %s; pose the rig to check it, then decimate'
                    % (wrap.name, len(me.vertices), tris, RIG))
        return {'FINISHED'}


# ---------------------------------------------------------------------------------------------------------- frames

FRAME = 'm3at_frame'        # a garment's frame, kept on its object as JSON: each moved bone's deform and pose
FRAME_TURN_WARN = 3.0       # degrees: a bone turned more than this in a frame bends the body at rest (M3AT keeps the
                            # bones' own directions, so a turned forearm is drawn bent along a straight bone)


def flat(m):
    return [m[r][c] for r in range(4) for c in range(4)]


def unflat(xs):
    return Matrix([xs[r * 4:r * 4 + 4] for r in range(4)])


def frame_of(o):
    """The garment's frame: {bone: deform in armature space}, or None."""
    if o is None or FRAME not in o:
        return None
    saved = json.loads(o[FRAME])
    return {b: unflat(m) for b, m in saved['deform'].items()}


def pose_deforms(rig):
    """Each bone's deform in armature space as the rig is posed (what the Armature modifier applies), the moved ones."""
    out = {}
    for pb in rig.pose.bones:
        m = pb.matrix @ pb.bone.matrix_local.inverted()
        if any(abs(m[r][c] - (1.0 if r == c else 0.0)) > 1e-5 for r in range(3) for c in range(4)):
            out[pb.name] = m
    return out


def turn_degrees(m):
    return math.degrees(m.to_3x3().normalized().to_quaternion().angle)


class M3AT_OT_capture_frame(bpy.types.Operator):
    """The rig's current pose kept on the selected garment as its wearer's frame (move and scale bones in Pose Mode first)"""
    bl_idname = 'm3at.capture_frame'
    bl_label = 'Capture frame'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        body, rig, err = kit_rig_and_body()
        o = context.active_object
        if err or o is None or o.type != 'MESH' or o.name == BODY:
            self.report({'ERROR'}, err or 'Select the garment (not the body) to keep the frame on')
            return {'CANCELLED'}
        deform = pose_deforms(rig)
        if not deform:
            self.report({'ERROR'}, '%s is at rest: move and scale its bones in Pose Mode first' % RIG)
            return {'CANCELLED'}
        basis = {pb.name: flat(pb.matrix_basis) for pb in rig.pose.bones if pb.name in deform}
        o[FRAME] = json.dumps({'deform': {b: flat(m) for b, m in deform.items()}, 'basis': basis})
        turned = sorted(((turn_degrees(m), b) for b, m in deform.items()), reverse=True)
        if turned and turned[0][0] > FRAME_TURN_WARN:
            self.report({'WARNING'}, '%s: frame of %d bones kept, but %s is turned %.0f degrees: move and scale bones, '
                        "don't turn them (M3AT keeps the bones' directions)" % (o.name, len(deform), turned[0][1], turned[0][0]))
        else:
            self.report({'INFO'}, '%s: frame of %d bones kept. Model it around the posed body; Export writes it at rest' % (o.name, len(deform)))
        return {'FINISHED'}


class M3AT_OT_show_frame(bpy.types.Operator):
    """M3AT_Rig posed in the selected garment's frame"""
    bl_idname = 'm3at.show_frame'
    bl_label = 'Show frame'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        body, rig, err = kit_rig_and_body()
        o = context.active_object
        if err or o is None or FRAME not in o:
            self.report({'ERROR'}, err or 'Select a garment with a frame')
            return {'CANCELLED'}
        basis = json.loads(o[FRAME])['basis']
        for pb in rig.pose.bones:
            pb.matrix_basis = unflat(basis[pb.name]) if pb.name in basis else Matrix.Identity(4)
        return {'FINISHED'}


class M3AT_OT_rest_pose(bpy.types.Operator):
    """M3AT_Rig back in its rest pose"""
    bl_idname = 'm3at.rest_pose'
    bl_label = 'Rest pose'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        body, rig, err = kit_rig_and_body()
        if err:
            self.report({'ERROR'}, err)
            return {'CANCELLED'}
        for pb in rig.pose.bones:
            pb.matrix_basis = Matrix.Identity(4)
        return {'FINISHED'}


class M3AT_OT_clear_frame(bpy.types.Operator):
    """The selected garment's frame forgotten: its wearer is drawn in their own body"""
    bl_idname = 'm3at.clear_frame'
    bl_label = 'Clear frame'
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        o = context.active_object
        if o is not None and FRAME in o:
            del o[FRAME]
        return {'FINISHED'}


def framed_rest(o, me, rig, body, mats, modelled_framed, report):
    """The garment's vertices (me, world space) at rest under its frame, in world space, and each one's weights by bone.
    Modelled framed (no Armature modifier): each vertex takes the weights of the posed body under it and is taken back
    through them; otherwise its mesh is at rest already, with its own vertex groups as weights or else the body's."""
    to_rig = rig.matrix_world.inverted()
    rest_surf = body_surface(body, to_rig)
    _, _, body_pts, body_ws = rest_surf
    if modelled_framed:
        posed = [skin_matrix(w, mats) @ p for p, w in zip(body_pts, body_ws)]
        surf = mesh_surface(body.data, posed, body_ws)
    else:
        surf = rest_surf
    bones = {b.name for b in rig.data.bones}
    names = {g.index: g.name for g in o.vertex_groups}
    rest, weights = [], []
    for k, v in enumerate(me.vertices):
        p = to_rig @ v.co
        own = {}
        if not modelled_framed and k < len(o.data.vertices):
            own = {names[e.group]: e.weight for e in o.data.vertices[k].groups
                   if e.weight > 0 and names.get(e.group) in bones}
            t = sum(own.values())
            own = {b: w / t for b, w in own.items()} if t else {}
        w = own or blended_weights(p, surf)
        if modelled_framed:
            p = skin_matrix(w, mats).inverted() @ p
        rest.append(rig.matrix_world @ p)
        weights.append(w)
    return rest, weights


# ---------------------------------------------------------------------------------------------------------- exporting

def base_colour_image(o):
    def upstream(socket, seen):
        for link in socket.links:
            n = link.from_node
            if n in seen:
                continue
            seen.add(n)
            if n.type == 'TEX_IMAGE' and n.image:
                return n.image
            for s in n.inputs:
                found = upstream(s, seen)
                if found:
                    return found
        return None
    for slot in o.material_slots:
        m = slot.material
        if m and m.use_nodes:
            for n in m.node_tree.nodes:
                if n.type == 'BSDF_PRINCIPLED':
                    found = upstream(n.inputs['Base Color'], set())
                    if found:
                        return found
    return None


def write_image(image, path, cutout):
    """The image as it is in Blender, painted but unsaved strokes and all (image.copy() would read it from its file again),
    at most TEXTURE_SIZE across, opaque unless cutout."""
    w, h = image.size
    copy = bpy.data.images.new('m3at_export', w, h, alpha=True)
    try:
        px = list(image.pixels)
        if not cutout:
            px[3::4] = [1.0] * (len(px) // 4)
        copy.pixels[:] = px
        if max(w, h) > TEXTURE_SIZE:
            k = TEXTURE_SIZE / max(w, h)
            copy.scale(max(1, int(w * k)), max(1, int(h * k)))
        copy.filepath_raw = path
        copy.file_format = 'PNG'
        copy.save()
    finally:
        bpy.data.images.remove(copy)


def setting_lines(o):
    s = o.m3at
    out = []
    if s.kind == 'GENE':
        out.append('genes %s' % ' '.join(s.genes.split()))
        out.append('colour_from %s' % s.colour_from)
    elif s.defs.strip():
        out.append('defs %s' % ' '.join(s.defs.split()))
    if s.kind == 'APPAREL' and s.stand_in and s.groups.strip():
        out.append('layer %s' % s.layer)
        out.append('groups %s' % ' '.join(s.groups.split()))
    if s.use_colour or (s.kind == 'GENE' and s.colour_from == 'own'):
        out.append('colour %.3f %.3f %.3f' % tuple(s.colour))
    if s.tint != 'auto':
        out.append('tint %s' % s.tint)
    if s.hide_hair != 'auto':
        out.append('hide_hair %s' % s.hide_hair)
    if s.hide_face != 'auto':
        out.append('hide_face %s' % s.hide_face)
    if s.on_head:
        out.append('bone spine.006')
    if s.hard:
        out.append('hard yes')
    if not s.hides_skin:
        out.append('hides no')
    if s.hide_bones.strip():
        out.append('hide_bones %s' % ' '.join(s.hide_bones.split()))
    if s.hide_clothes:
        out.append('hide_clothes yes')
    if s.frame > 0.0:
        out.append('wearer_key Armoured %.2f' % s.frame)
    return out


def garment_name(o):
    return ''.join(c if c.isalnum() or c in '_-' else '_' for c in o.name)


def export_garment(context, o, out, report):
    """Writes o as out/garment_<Name>.txt (and its image). Returns the triangle count, or None."""
    s = o.m3at
    name = garment_name(o)
    lines = ['# M3AT: the garment %s, written by the M3AT garment kit %d (Docs/Tools/m3at_garment_kit.py).' % (name, KIT_VERSION),
             '# M3AT fits its weights, body shapes and the skin it hides in the game (Source/ModGarments.cs).']
    made_on = body_line(context)
    if made_on:
        lines.append(made_on)
    image = base_colour_image(o)
    texture = None
    if image is not None:
        texture = 'garment_%s.png' % name
        write_image(image, os.path.join(out, texture), s.cutout)
    if s.base and s.retexture_only:
        lines.append('base %s' % s.base)
        lines += setting_lines(o)
        if texture:
            lines.append('texture %s' % texture)
        with open(os.path.join(out, 'garment_%s.txt' % name), 'w') as f:
            f.write('\n'.join(lines) + '\n')
        report({'INFO'}, '%s: a retexture of %s' % (name, s.base))
        return 0
    # The mesh at rest: modifiers but the armature applied, shape keys at their base, triangulated.
    arms = [m for m in o.modifiers if m.type == 'ARMATURE' and m.show_viewport]
    for m in arms:
        m.show_viewport = False
    keys = o.data.shape_keys
    was = None
    if keys:
        was = [(k, k.value) for k in keys.key_blocks]
        for k, _ in was:
            k.value = 0.0
    try:
        deps = context.evaluated_depsgraph_get()
        ev = o.evaluated_get(deps)
        me = ev.to_mesh()
        bm = bmesh.new()
        bm.from_mesh(me)
        bmesh.ops.triangulate(bm, faces=bm.faces)
        bm.to_mesh(me)
        bm.free()
        me.transform(o.matrix_world)
        me.calc_loop_triangles()
        uv = me.uv_layers.active.data if me.uv_layers.active else None
        if texture and uv is None:
            report({'WARNING'}, '%s has an image but no UV map: written without it' % name)
            texture = None
        # A vertex with more than one texture place is written once per place.
        verts, uvs, tris, copies = [], [], [], {}
        for t in me.loop_triangles:
            tri = []
            for li in t.loops:
                vi = me.loops[li].vertex_index
                place = tuple(round(c, 6) for c in uv[li].uv) if (uv is not None and texture) else (0.0, 0.0)
                k = (vi, place)
                if k not in copies:
                    copies[k] = len(verts)
                    verts.append(vi)
                    uvs.append(place)
                tri.append(copies[k])
            tris.append(tri)
        glass_slots = {i for i, slot in enumerate(o.material_slots) if slot.material and slot.material.name.lower().split('.')[0] == 'visor'}
        visor = [k for k, t in enumerate(me.loop_triangles) if t.material_index in glass_slots]
        lines += setting_lines(o)
        if texture:
            lines.append('texture %s' % texture)
        mats = frame_of(o)
        frame_ws = None
        if mats:
            body, rig, err = kit_rig_and_body()
            if err:
                report({'ERROR'}, '%s has a frame: %s, to export it' % (name, err))
                return None
            # Modelled around the posed body unless it is skinned to the rig (then its mesh is at rest already).
            modelled_framed = not any(m.object == rig for m in arms)
            rest, frame_ws = framed_rest(o, me, rig, body, mats, modelled_framed, report)
            for v, p in zip(me.vertices, rest):
                v.co = p
            for b, m in sorted(mats.items()):
                g = TO_GAME @ m @ TO_GAME.transposed()
                lines.append('frame %s %s' % (b, ' '.join('%.6f' % g[r][c] for r in range(3) for c in range(4))))
            report({'INFO'}, '%s: written at rest from its frame (%d bones; modelled %s)'
                   % (name, len(mats), 'around the posed body' if modelled_framed else 'at rest, skinned to the rig'))
        if visor:
            for k in range(0, len(visor), 32):
                lines.append('visor %s' % ' '.join(str(x) for x in visor[k:k + 32]))
        for vi in verts:
            lines.append('v %.5f %.5f %.5f' % to_game(me.vertices[vi].co))
        if frame_ws is not None:
            # The weights it was taken back to rest by, kept: M3AT puts it in the frame by them again.
            used = []
            for vi in verts:
                for b in frame_ws[vi]:
                    if b not in used:
                        used.append(b)
            lines.append('bones %s' % ' '.join(used))
            lines.append('own_weights yes')
            for vi in verts:
                row = frame_ws[vi]
                lines.append('w %s' % ' '.join('%d %.5f' % (used.index(b), w) for b, w in row.items()) if row else 'w')
        elif s.own_weights:
            rig = bpy.data.objects.get(RIG)
            bone_names = [b.name for b in rig.data.bones] if rig else []
            names = {g.index: g.name for g in o.vertex_groups}
            used = []
            ws = []
            for vi in verts:
                row = [(names[e.group], e.weight) for e in o.data.vertices[vi].groups
                       if e.weight > 0 and names.get(e.group) in bone_names] if vi < len(o.data.vertices) else []
                for b, _ in row:
                    if b not in used:
                        used.append(b)
                ws.append(row)
            if used:
                lines.append('bones %s' % ' '.join(used))
                lines.append('own_weights yes')
                for row in ws:
                    lines.append('w %s' % ' '.join('%d %.5f' % (used.index(b), w) for b, w in row) if row else 'w')
            else:
                report({'WARNING'}, '%s: Own weights is on, but it has no vertex groups named for bones; it takes the skin\'s' % name)
        if texture:
            for u, v in uvs:
                lines.append('t %.6f %.6f' % (u, v))
        for a, b, c in tris:
            lines.append('f %d %d %d' % (a, c, b))
        ev.to_mesh_clear()
    finally:
        for m in arms:
            m.show_viewport = True
        if was:
            for k, v in was:
                k.value = v
    with open(os.path.join(out, 'garment_%s.txt' % name), 'w') as f:
        f.write('\n'.join(lines) + '\n')
    count = len(tris)
    budget = TRIANGLES_HEAD if s.on_head else TRIANGLES_MOST
    if count > budget:
        report({'WARNING'}, '%s: %d triangles, over M3AT\'s budget of %d: the game will optimize it as it loads, which can spoil its shape. '
               'Decimate it yourself (Docs/Apparel.md, Keep it low poly)' % (name, count, budget))
    elif count > TRIANGLES_LOW:
        report({'INFO'}, '%s: %d triangles (a fair amount; most of M3AT\'s garments are under %d)' % (name, count, TRIANGLES_LOW))
    else:
        report({'INFO'}, '%s: %d triangles' % (name, count))
    return count


class M3AT_OT_export(bpy.types.Operator):
    """The selected garments to the Export folder as garment_<Name>.txt, with their images"""
    bl_idname = 'm3at.export'
    bl_label = 'Export selected'

    def execute(self, context):
        out = bpy.path.abspath(context.scene.m3at_kit.out)
        if not out:
            self.report({'ERROR'}, "Set Export to: your mod's M3ATgarments folder")
            return {'CANCELLED'}
        os.makedirs(out, exist_ok=True)
        done = 0
        for o in context.selected_objects:
            if o.type != 'MESH' or o.name == BODY:
                continue
            s = o.m3at
            if s.kind == 'APPAREL' and not s.defs.strip() and not (s.stand_in and s.groups.strip()) and not s.base:
                self.report({'WARNING'}, '%s names no apparel (Defs) and stands in for nothing: nothing would wear it' % o.name)
            if s.kind == 'GENE' and not s.genes.strip():
                self.report({'WARNING'}, '%s is a gene part with no genes' % o.name)
            if export_garment(context, o, out, self.report) is not None:
                done += 1
        if not done:
            self.report({'ERROR'}, 'Select the garments to export (not the body)')
            return {'CANCELLED'}
        return {'FINISHED'}


# ---------------------------------------------------------------------------------------------------------- the panel

class M3AT_PT_kit(bpy.types.Panel):
    bl_label = 'M3AT garments'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'M3AT'

    def draw(self, context):
        kit = context.scene.m3at_kit
        col = self.layout.column()
        col.prop(kit, 'chibi')
        row = col.row(align=True)
        row.operator('m3at.import_body')
        row.operator('m3at.import_garment')
        col.operator('m3at.from_faces')
        row = col.row(align=True)
        row.operator('m3at.open_limbs')
        row.operator('m3at.close_limbs')
        col.separator()
        o = context.active_object
        if o is None or o.type != 'MESH' or o.name == BODY:
            col.label(text='Select a garment for its settings')
        else:
            s = o.m3at
            box = col.box()
            box.label(text=o.name, icon='MATCLOTH')
            box.prop(s, 'kind', expand=True)
            if s.kind == 'GENE':
                box.prop(s, 'genes')
                box.prop(s, 'colour_from')
                if s.colour_from == 'own':
                    box.prop(s, 'colour')
            else:
                box.prop(s, 'defs')
                box.prop(s, 'stand_in')
                if s.stand_in:
                    box.prop(s, 'layer')
                    box.prop(s, 'groups')
                row = box.row()
                row.prop(s, 'use_colour')
                if s.use_colour:
                    row.prop(s, 'colour', text='')
                box.prop(s, 'hide_hair')
                box.prop(s, 'hide_face')
                box.prop(s, 'hide_clothes')
                box.prop(s, 'frame')
                fr = box.box()
                n = len(json.loads(o[FRAME])['deform']) if FRAME in o else 0
                fr.label(text=('Own frame: %d bones' % n) if n else 'Own frame: none (pose the rig, then Capture)', icon='ARMATURE_DATA')
                row = fr.row(align=True)
                row.operator('m3at.capture_frame')
                row.operator('m3at.show_frame')
                row = fr.row(align=True)
                row.operator('m3at.rest_pose')
                row.operator('m3at.clear_frame')
            box.prop(s, 'tint')
            box.prop(s, 'cutout')
            box.prop(s, 'on_head')
            box.prop(s, 'hard')
            box.prop(s, 'own_weights')
            box.prop(s, 'hides_skin')
            box.prop(s, 'hide_bones')
            if s.base:
                box.prop(s, 'retexture_only', text='Retexture only (on %s)' % s.base)
            # The live triangle count against M3AT's budget: under 40% of it light (as M3AT's own are), within it fair,
            # over it the game optimizes the garment as it loads (Source/GarmentBudget.cs).
            tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
            budget = TRIANGLES_HEAD if s.on_head else TRIANGLES_MOST
            share = tris / budget
            verdict = 'light' if share <= 0.4 else 'within budget' if share <= 1.0 else 'OVER budget: the game will optimize it'
            text = '%d / %d triangles: %s' % (tris, budget, verdict)
            if hasattr(box, 'progress'):
                box.progress(factor=min(share, 1.0), type='BAR', text=text)
            else:
                box.label(text=text)
            if share > 1.0:
                warn = box.column(align=True)
                warn.alert = True
                warn.label(text='Rebuild from the body\'s faces, or decimate it', icon='ERROR')
            for img in {n.image for m in o.data.materials if m and m.use_nodes for n in m.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image}:
                if max(img.size) > TEXTURE_MOST:
                    row = box.row()
                    row.alert = True
                    row.label(text='Image %s is %d px: keep it at %d or less' % (img.name, max(img.size), TEXTURE_MOST), icon='ERROR')
        col.separator()
        col.prop(kit, 'out')
        col.operator('m3at.export')


CLASSES = (M3ATGarment, M3ATKit, M3AT_OT_import_body, M3AT_OT_import_garment, M3AT_OT_from_faces, M3AT_OT_open_limbs, M3AT_OT_close_limbs,
           M3AT_OT_capture_frame, M3AT_OT_show_frame, M3AT_OT_rest_pose, M3AT_OT_clear_frame, M3AT_OT_export, M3AT_PT_kit)


def register():
    for c in CLASSES:
        bpy.utils.register_class(c)
    bpy.types.Object.m3at = bpy.props.PointerProperty(type=M3ATGarment)
    bpy.types.Scene.m3at_kit = bpy.props.PointerProperty(type=M3ATKit)


def unregister():
    del bpy.types.Scene.m3at_kit
    del bpy.types.Object.m3at
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)


if __name__ == '__main__':
    try:
        unregister()
    except Exception:
        pass
    register()
