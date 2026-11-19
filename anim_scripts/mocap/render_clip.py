# Renders a clip json (import_take.py's, or import_pluck.py's) on the chibi to look over, without the game.
#   blender -b Models/pawnHumanRig.blend --python anim_scripts/mocap/render_clip.py -- <clip name> <out folder> <frames>
#       [--floor]
# Poses the metarig by walk.mocap_at (or walk.kneel_at with --floor: set on the floor by its lowest toe) at <frames>
# evenly spaced phases and renders the bare body from the front three-quarter and the side with Workbench, as
# front_NN.png and side_NN.png, and both rows side by side as sheet.png.
import bpy, math, os, sys
from mathutils import Vector, Euler
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import walk

args = sys.argv[sys.argv.index('--') + 1:]
name, out, count = args[0], args[1], int(args[2])
floor = '--floor' in args
zoom = args[args.index('--zoom') + 1] if '--zoom' in args else None
# --at <walk function>: pose by that walk.py function of t (plant_at ...) rather than the clip as it is.
# --set NAME=value ...: walk.py numbers changed for the render (to compare settings).
for a in args:
    if '=' in a:
        k, v = a.split('=')
        setattr(walk, k, float(v))
poser = getattr(walk, args[args.index('--at') + 1]) if '--at' in args else None
clip = walk.clip_file(name)
os.makedirs(out, exist_ok=True)
if walk.arm.animation_data:
    walk.arm.animation_data.action = None
    for t in list(walk.arm.animation_data.nla_tracks):
        walk.arm.animation_data.nla_tracks.remove(t)

body = walk.body_parts()
for o in body:      # a part saved in Edit Mode isn't deformed by its Armature modifier
    if o.mode != 'OBJECT':
        with bpy.context.temp_override(active_object=o, object=o, selected_objects=[o]):
            bpy.ops.object.mode_set(mode='OBJECT')
for o in bpy.data.objects:
    if o.type == 'MESH':
        o.hide_render = o not in body
scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'SINGLE'
scene.display.shading.single_color = (0.8, 0.65, 0.55)
scene.display.shading.show_shadows = False
scene.render.resolution_x = 300
scene.render.resolution_y = 420
scene.render.film_transparent = False
world = scene.world or bpy.data.worlds.new('w')
scene.world = world
# A floor.
bpy.ops.mesh.primitive_plane_add(size=4, location=(0, 0, 0))
plane = bpy.context.object
cam_data = bpy.data.cameras.new('c')
cam_data.type = 'ORTHO'
cam = bpy.data.objects.new('c', cam_data)
scene.collection.objects.link(cam)
scene.camera = cam
height = max((walk.arm.matrix_world @ v).z for v in [walk.head[b] for b in walk.head])
cam_data.ortho_scale = height * 1.45
views = {'front': (35, 'front'), 'side': (90, 'side')}
for f in range(count):
    p = f / count
    X = poser(p)[0] if poser else walk.kneel_at(clip, p) if floor else walk.mocap_at(clip, p, walk.Quaternion())
    walk.apply_pose(X)
    bpy.context.view_layer.update()
    for view, yaw in (('front', 30), ('side', 90)):
        a = math.radians(yaw)
        d = height * 3
        pts = [X[b] @ walk.head[b] for b in X if b in walk.head] + [X[b] @ walk.tail[b] for b in X if b in walk.tail]
        lo = Vector([min(q[k] for q in pts) for k in range(3)]); hi = Vector([max(q[k] for q in pts) for k in range(3)])
        target = (lo + hi) / 2 + Vector((0, 0, 1.0))
        cam_data.ortho_scale = max(height * 1.1, (hi - lo).length * 1.15)
        if zoom:     # --zoom <bone>: close on that bone
            target = X[zoom] @ walk.head[zoom]
            cam_data.ortho_scale = height * 0.35
        cam.location = target + Vector((math.sin(a) * d, -math.cos(a) * d, height * 0.15))
        cam.rotation_euler = (target - cam.location).to_track_quat('-Z', 'Y').to_euler()
        scene.render.filepath = os.path.join(out, '%s_%02d.png' % (view, f))
        bpy.ops.render.render(write_still=True)

# The contact sheet: Blender's own image module, as its Python has no PIL.
fronts = [bpy.data.images.load(os.path.join(out, 'front_%02d.png' % f)) for f in range(count)]
sides = [bpy.data.images.load(os.path.join(out, 'side_%02d.png' % f)) for f in range(count)]
w, h = fronts[0].size
import numpy as np
sheet = np.zeros((2 * h, count * w, 4), np.float32)
for row, ims in ((1, fronts), (0, sides)):     # Blender's images run bottom up
    for f, im in enumerate(ims):
        px = np.array(im.pixels[:], np.float32).reshape(h, w, 4)
        sheet[row * h:(row + 1) * h, f * w:(f + 1) * w] = px
img = bpy.data.images.new('sheet', count * w, 2 * h, alpha=True)
img.pixels = sheet.ravel()
img.filepath_raw = os.path.join(out, 'sheet.png')
img.file_format = 'PNG'
img.save()
