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
  Export            the selected garments to your mod's M3ATgarments folder as garment_<Name>.txt (and its image),
                    with the settings in the panel. M3AT fits the rest (weights, body shapes, hidden skin) in the game.

Nothing here writes into M3AT's own folder. See Docs/Apparel.md and Docs/GeneParts.md beside this file.

Game space (the files): x, y up, z, the figure facing -z; Blender's (x, y, z) is written as (-x, z, -y), so a triangle's
corners go the other way round in the file.
"""

bl_info = {
    "name": "M3AT garment kit",
    "author": "Maru (M3AT)",
    "version": (1, 0, 0),
    "blender": (4, 2, 0),
    "location": "3D View > Sidebar > M3AT",
    "description": "Make apparel and gene parts for M3AT: Maru's 3D Animated Toons",
    "category": "Import-Export",
}

import os
import bpy
import bmesh
from mathutils import Vector

KIT_VERSION = 1
# Off the skin by apparel layer, cells (M3AT's make_from_groups.py LAYER_OFFSET, 0.1 of its Blender units a layer):
# far enough apart that each layer's garment covers the one under it, close enough that nothing shows a gap.
LAYER_OFFSET = {'Underwear': 0.0030, 'OnSkin': 0.0042, 'Middle': 0.0085, 'Shell': 0.0127, 'Webbing': 0.0170}
TEXTURE_SIZE = 1024   # the most pixels across a written image (M3AT's own are this or less)
TRIANGLES_LOW = 800      # a garment over this many triangles is told it is heavy
TRIANGLES_MOST = 2500     # M3AT's budget for a garment worn on the body (Source/GarmentBudget.cs): past it the game simplifies it
TRIANGLES_HEAD = 1500     # and for one worn on the head
BODY = 'M3AT_Body'
RIG = 'M3AT_Rig'


# ---------------------------------------------------------------------------------------------------------- reading

def to_blender(x, y, z):
    return Vector((-x, -z, y))


def to_game(v):
    return (-v.x, v.z, -v.y)


def stamp(path):
    """skin.txt's hash, as M3AT names the body it fits garments to (FNV-1a, 64 bits, hex)."""
    h = 14695981039346656037
    with open(path, 'rb') as f:
        for b in f.read():
            h ^= b
            h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return '%016x' % h


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
    return out


def garment_name(o):
    return ''.join(c if c.isalnum() or c in '_-' else '_' for c in o.name)


def export_garment(context, o, out, report):
    """Writes o as out/garment_<Name>.txt (and its image). Returns the triangle count, or None."""
    s = o.m3at
    name = garment_name(o)
    lines = ['# M3AT: the garment %s, written by the M3AT garment kit %d (Docs/Tools/m3at_garment_kit.py).' % (name, KIT_VERSION),
             '# M3AT fits its weights, body shapes and the skin it hides in the game (Source/ModGarments.cs).']
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
        if visor:
            for k in range(0, len(visor), 32):
                lines.append('visor %s' % ' '.join(str(x) for x in visor[k:k + 32]))
        for vi in verts:
            lines.append('v %.5f %.5f %.5f' % to_game(me.vertices[vi].co))
        if s.own_weights:
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
        report({'WARNING'}, '%s: %d triangles, over M3AT\'s budget of %d: the game will simplify it as it loads, which can spoil its shape. '
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
            box.prop(s, 'tint')
            box.prop(s, 'cutout')
            box.prop(s, 'on_head')
            box.prop(s, 'hard')
            box.prop(s, 'own_weights')
            box.prop(s, 'hides_skin')
            box.prop(s, 'hide_bones')
            if s.base:
                box.prop(s, 'retexture_only', text='Retexture only (on %s)' % s.base)
            tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
            box.label(text='%d triangles' % tris, icon='ERROR' if tris > TRIANGLES_MOST else 'INFO')
        col.separator()
        col.prop(kit, 'out')
        col.operator('m3at.export')


CLASSES = (M3ATGarment, M3ATKit, M3AT_OT_import_body, M3AT_OT_import_garment, M3AT_OT_from_faces, M3AT_OT_export, M3AT_PT_kit)


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
