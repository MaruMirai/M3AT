"""Puts each snout in out/ on the chibi's own head, with the Snout key, under Facial Animation's layers, and renders it.

    /home/t/Programs/blender-5.2.3-linux-x64/blender -b --python face_test.py -- <sheet.png> [name ...]

A name is a file in out/ without its .png, or "none". Reads the game's skin.txt, so what it shows is the export's mesh and
face patch. A mock-up, not a game render: flat skin, plain light, no hair or brow colour, no toon bands.
Each row is one snout: the Experimentals face and the retexture's, each from IsoRim's 55 degree camera and from 30 degrees.
"""
import glob
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'pigSnout'))
SKIN_FILE = os.path.join(HERE, "..", "..", "M3AT: Maru's 3D Animated Toons", "Models", "chibi", "skin.txt")
W = os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/")
EXP = W + "2581693737/Textures/Things/Pawn/FA-Experimentals/"
RET = W + "3271240549/Textures/Things/Pawn/FA-Experimentals/"
SKIN = (236 / 255, 200 / 255, 170 / 255)
CELL = 300
VIEWS = ((55, "55"), (30, "30"))
MOUTHS = {"exp": "open", "ret": "open"}


def to_blender(p):
    return (-p[0], -p[2], p[1])


def read_skin():
    verts, tris, fv, ff, keys = [], [], [], [], {}
    current = None
    for line in open(SKIN_FILE):
        t = line.split()
        if not t:
            continue
        if t[0] == "garment":
            break
        if t[0] == "v":
            verts.append(tuple(map(float, t[1:4])))
        elif t[0] == "f":
            tris.append(tuple(map(int, t[1:4])))
        elif t[0] == "fv":
            fv.append((int(t[1]), float(t[2]), float(t[3]), tuple(map(float, t[4:7]))))
        elif t[0] == "ff":
            ff.append(tuple(map(int, t[1:4])))
        elif t[0] == "key":
            current = keys.setdefault(t[1], {})
        elif t[0] == "d" and current is not None:
            current[int(t[1])] = tuple(map(float, t[2:5]))
    return verts, tris, fv, ff, keys


def material(name, colour=None, image=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Roughness"].default_value = 1.0
    if image is not None:
        n = m.node_tree.nodes.new("ShaderNodeTexImage")
        n.image = image
        m.node_tree.links.new(n.outputs["Color"], b.inputs["Base Color"])
    else:
        b.inputs["Base Color"].default_value = (*colour, 1)
    return m


def srgb_to_linear(c):
    return tuple((v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4) for v in c)


def load_rgba(path):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    a = np.zeros(w * h * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(h, w, 4)


def over(base, layer):
    a = layer[..., 3:]
    base[..., :3] = layer[..., :3] * a + base[..., :3] * (1 - a)


def face_image(root, gender, snout):
    """The layers in Facial Animation's order. Rows here go up, as Blender's pixels do."""
    base = np.ones((512, 512, 4), np.float32)
    base[..., :3] = SKIN
    # The eye layers fill the texture and only the lid cover's holes show them; the cover is the skin's colour, so outside
    # the holes there is nothing to see of either.
    cover = load_rgba(root + "Lids/Normal/%s/normal_cover_south.png" % gender)
    eyes = base.copy()
    for p in ("Eyes/Common/%s/normal_L_south.png", "Eyes/Common/%s/normal_R_south.png", "Eyes/Normal/%s/normal_south.png"):
        path = (root if os.path.exists(root + p % gender) else EXP) + p % gender
        if os.path.exists(path):
            over(eyes, load_rgba(path))
    holes = holes_of(cover[..., 3])
    base[..., :3] = eyes[..., :3] * holes[..., None] + base[..., :3] * (1 - holes[..., None])
    tinted = cover.copy()
    tinted[..., :3] = SKIN
    over(base, tinted)
    # Moved as the game moves it (FaceLayers.MouthRaise, or MouthRaisePig with the snout): MOUTH_DOWN texture pixels at 512.
    mouth = load_rgba(root + "Mouth/Normal/%s/open_south.png" % gender)
    down = round(float(os.environ.get("MOUTH_DOWN", "-6")) * mouth.shape[0] / 512)
    over(base, np.roll(mouth, -down, axis=0))
    over(base, load_rgba(root + "Brows/Normal/%s/s-shaped_south.png" % gender))
    if snout is not None:
        s = load_rgba(snout).copy()
        s[..., :3] *= SKIN
        over(base, s)
    # sRGB art into a linear image.
    base[..., :3] = np.where(base[..., :3] <= 0.04045, base[..., :3] / 12.92, ((base[..., :3] + 0.055) / 1.055) ** 2.4)
    img = bpy.data.images.new("face", 512, 512, alpha=True, float_buffer=True)
    img.colorspace_settings.name = "Linear Rec.709"
    img.pixels.foreach_set(base.ravel())
    img.pack()
    return img


def holes_of(alpha):
    """1 where the lid cover's mask is open and shut in on every side: the eyes, not the rest of the texture."""
    solid = alpha >= 0.5
    outside = np.zeros_like(solid)
    outside[0, :] = outside[-1, :] = outside[:, 0] = outside[:, -1] = True
    outside &= ~solid
    while True:
        grown = outside.copy()
        grown[1:] |= outside[:-1]
        grown[:-1] |= outside[1:]
        grown[:, 1:] |= outside[:, :-1]
        grown[:, :-1] |= outside[:, 1:]
        grown &= ~solid
        if (grown == outside).all():
            break
        outside = grown
    return (~solid & ~outside).astype(np.float32)


def build(verts, tris, fv, ff, key):
    moved = [tuple(v[j] + key.get(i, (0, 0, 0))[j] for j in range(3)) for i, v in enumerate(verts)]
    me = bpy.data.meshes.new("body")
    me.from_pydata([to_blender(p) for p in moved], [], tris)
    me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
    body = bpy.data.objects.new("body", me)
    bpy.context.scene.collection.objects.link(body)
    at = {}
    places, uvs = [], []
    for i, u, v, p in fv:
        at[i] = len(places)
        d = key.get(i, (0, 0, 0))
        places.append(to_blender(tuple(p[j] + d[j] for j in range(3))))
        uvs.append((u, v))
    fm = bpy.data.meshes.new("face")
    fm.from_pydata(places, [], [tuple(at[i] for i in t) for t in ff])
    fm.polygons.foreach_set("use_smooth", [True] * len(fm.polygons))
    layer = fm.uv_layers.new(name="uv")
    for loop in fm.loops:
        layer.data[loop.index].uv = uvs[loop.vertex_index]
    face = bpy.data.objects.new("face", fm)
    bpy.context.scene.collection.objects.link(face)
    return body, face


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    sheet_path, names = args[0], args[1:] or [os.path.splitext(os.path.basename(p))[0] for p in sorted(glob.glob(os.path.join(HERE, "out", "*.png")))]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 24
    scene.cycles.device = "CPU"
    scene.render.resolution_x = scene.render.resolution_y = CELL
    scene.view_settings.view_transform = "Standard"
    scene.world = bpy.data.worlds.new("w")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.55, 0.55, 1)
    scene.world.node_tree.nodes["Background"].inputs[1].default_value = 1.0
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 1.6
    sun.rotation_euler = (math.radians(35), math.radians(-15), 0)
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = float(os.environ.get("ZOOM", "0.40"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    verts, tris, fv, ff, keys = read_skin()
    target = Vector((0, -0.08, float(os.environ.get("TARGET_Z", "1.02"))))
    sets = (("exp", EXP), ("ret", RET))
    genders = ("Male",)
    rows = []
    tmp = sheet_path + ".cell.png"
    scene.render.filepath = tmp
    for name in names:
        snout = None if name == "none" else os.path.join(HERE, "out", name + ".png")
        body, face = build(verts, tris, fv, ff, {} if name == "none" else keys["Nose_Pig"])
        body.data.materials.append(material("skin", srgb_to_linear(SKIN)))
        row = []
        for label, root in sets:
            for gender in genders:
                img = face_image(root, gender, snout)
                face.data.materials.clear()
                face.data.materials.append(material("f", image=img))
                for angle, _ in VIEWS:
                    a = math.radians(angle)
                    cam.location = target + Vector((0, -math.cos(a), math.sin(a))) * 3
                    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
                    bpy.ops.render.render(write_still=True)
                    row.append(load_rgba(tmp).copy())
        rows.append(np.concatenate(row, axis=1))
        for o in (body, face):
            bpy.data.objects.remove(o)
    whole = np.concatenate(rows[::-1], axis=0)
    h, w = whole.shape[:2]
    out = bpy.data.images.new("sheet", w, h, alpha=True)
    out.pixels.foreach_set(whole.ravel())
    out.filepath_raw = sheet_path
    out.file_format = "PNG"
    out.save()
    os.remove(tmp)
    print("SHEET", sheet_path, names)


main()
