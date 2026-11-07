"""Puts a hairstyle the game built (HairBuilder.cs) on the rig, to look at it as the game draws it.

    blender <copy of the rig .blend> --python anim_scripts/view_hair.py -- <style.txt> [...]

or ./view_hair.sh, which opens a copy with the pose preview too. Each <style>.txt is read from the game's hair cache
(<config>/M3AT/hair/<skin.txt hash>/) or a M3AThair/ folder, with its <style>.png beside it. The hair is in the rest
pose in game model space; it is put back into the rig's space (export_frames.py's to_game turned round: Blender's x is
-x, y is -z, z is y, over the export's scale) and parented to the head bone (walk.HEAD) whole, as the game has it ride
that bone, so it follows any clip the preview poses. Its texture is cut out below alpha 0.5, as MarusSmoothTex is. Each
body key it has is a shape key driven by the body's key of the same name (case aside: the game's ShrinkHead is the
body's Shrinkhead), so setting the body type or head shrink on the body sets the hair's too. The sprites are grey, so the hair is tinted by
COLOUR (the material's "Hair colour" node), as the game tints it by the pawn's. The closed shell the
outline is drawn from (<style>.shell.txt) comes along hidden, as "<style> shell".

The scale is the game's height of the body's rest pose (skin.txt) over the rig's, so it needs a skin.txt exported from
this rig. The .blend is never saved: the hair is not part of the rig, and a mesh left on it would be exported."""
import bpy, sys, os
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import walk

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIN = os.path.join(ROOT, "M3AT: Maru's 3D Animated Toons", 'Models', 'chibi', 'skin.txt')
CUTOUT = 0.5   # MarusSmoothTex's alpha cut
COLOUR = (0.36, 0.22, 0.12, 1.0)   # the hair colour the grey sprites are tinted by (linear), as story.HairColor does in the game

def read(path):
    """The v, n, t and f lines, and each key's vertex moves, of a hair or shell file."""
    verts, normals, uvs, faces, keys = [], [], [], [], {}
    key = None
    with open(path) as f:
        for line in f:
            p = line.split()
            if not p or p[0].startswith('#'):
                continue
            if p[0] == 'v':
                verts.append(Vector(map(float, p[1:4])))
            elif p[0] == 'n':
                normals.append(Vector(map(float, p[1:4])))
            elif p[0] == 't':
                uvs.append((float(p[1]), float(p[2])))
            elif p[0] == 'f':
                faces.append(tuple(int(i) for i in p[1:4]))
            elif p[0] == 'key':
                key = p[1]
                keys[key] = {}
            elif p[0] == 'd' and key is not None:
                keys[key][int(p[1])] = Vector(map(float, p[2:5]))
    return verts, normals, uvs, faces, keys

def game_scale():
    """Cells per Blender unit: the top of the body's rest pose in skin.txt over the top of the body parts' rest meshes."""
    top_game = max(float(l.split()[2]) for l in open(SKIN) if l.startswith('v '))
    top_blend = max((o.matrix_world @ v.co).z for o in walk.body_parts() for v in o.data.vertices)
    return top_game / top_blend

def from_game(v, scale):
    return Vector((-v.x, -v.z, v.y)) / scale

def build(name, path, scale, shell=False):
    verts, normals, uvs, faces, keys = read(path)
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata([from_game(v, scale) for v in verts], [], faces)
    mesh.update()
    # The game's model space is a mirror of Blender's, so the windings may come out inside out: turn them to agree with
    # the normals the builder wrote.
    if normals:
        n = [from_game(v, 1.0) for v in normals]
        agree = sum(1 if p.normal.dot(sum((n[i] for i in p.vertices), Vector())) > 0 else -1 for p in mesh.polygons)
        if agree < 0:
            mesh.flip_normals()
        mesh.shade_smooth()
        mesh.normals_split_custom_set_from_vertices([v.normalized() for v in n])
    if uvs:
        layer = mesh.uv_layers.new(name='UVMap')
        for loop in mesh.loops:
            layer.data[loop.index].uv = uvs[loop.vertex_index]
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)

    # Ride the head bone whole, from where it is at rest.
    rig = walk.arm
    obj.parent = rig
    obj.parent_type = 'BONE'
    obj.parent_bone = walk.HEAD
    bone = rig.pose.bones[walk.HEAD]
    obj.matrix_parent_inverse = (rig.matrix_world @ rig.data.bones[walk.HEAD].matrix_local
                                 @ Matrix.Translation((0, bone.length, 0))).inverted()

    if keys:
        drive_keys(obj, keys, scale)
    if shell:
        obj.hide_set(True)
        obj.hide_render = True
        obj.display_type = 'WIRE'
    return obj

def drive_keys(obj, keys, scale):
    obj.shape_key_add(name='Basis', from_mix=False)
    body = {}
    for o in walk.body_parts():
        if o.data.shape_keys:
            for kb in o.data.shape_keys.key_blocks[1:]:
                body.setdefault(kb.name.lower(), (o.data.shape_keys, kb.name))
    for name, moves in keys.items():
        kb = obj.shape_key_add(name=name, from_mix=False)
        for i, d in moves.items():
            kb.data[i].co = kb.data[i].co + from_game(d, scale)
        if name.lower() in body:
            key, body_name = body[name.lower()]
            fc = kb.driver_add('value')
            var = fc.driver.variables.new()
            var.name = 'v'
            var.targets[0].id_type = 'KEY'
            var.targets[0].id = key
            var.targets[0].data_path = 'key_blocks["%s"].value' % body_name
            fc.driver.expression = 'v'

def material(name, png):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = nodes['Principled BSDF']
    bsdf.inputs['Roughness'].default_value = 0.8
    tex = nodes.new('ShaderNodeTexImage')
    tex.image = bpy.data.images.load(png, check_existing=True)
    tex.interpolation = 'Closest'
    # The hair colour, in the material's "Hair colour" node.
    tint = nodes.new('ShaderNodeMix')
    tint.data_type = 'RGBA'
    tint.blend_type = 'MULTIPLY'
    tint.label = 'Hair colour'
    tint.inputs['Factor'].default_value = 1.0
    tint.inputs['B'].default_value = COLOUR
    links.new(tex.outputs['Color'], tint.inputs['A'])
    links.new(tint.outputs['Result'], bsdf.inputs['Base Color'])
    # Cut out as the game does, not blended.
    cut = nodes.new('ShaderNodeMath')
    cut.operation = 'GREATER_THAN'
    cut.inputs[1].default_value = CUTOUT
    links.new(tex.outputs['Alpha'], cut.inputs[0])
    links.new(cut.outputs[0], bsdf.inputs['Alpha'])
    if hasattr(mat, 'surface_render_method'):
        mat.surface_render_method = 'DITHERED'
    else:
        mat.blend_method = 'CLIP'
    mat.use_backface_culling = False   # the game draws both sides
    return mat

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
if not argv:
    raise SystemExit('view_hair.py: give the <style>.txt files to put on')
scale = game_scale()
for path in argv:
    path = os.path.abspath(path)
    style = os.path.basename(path)[:-len('.txt')]
    hair = build(style, path, scale)
    png = path[:-len('.txt')] + '.png'
    if os.path.exists(png):
        hair.data.materials.append(material(style, png))
    else:
        print('HAIR %s has no %s; drawn untextured' % (style, png))
    shell = path[:-len('.txt')] + '.shell.txt'
    if os.path.exists(shell):
        build(style + ' shell', shell, scale, shell=True)
    print('HAIR %s: %d vertices on %s' % (style, len(hair.data.vertices), walk.HEAD))
