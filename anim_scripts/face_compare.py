"""Facial Animation face sets side by side on the chibi's own head, to compare a race's face mod with the human ones.

    /home/t/Programs/blender-5.2.3-linux-x64/blender -b --python anim_scripts/face_compare.py -- <sheet.png> [set ...]

A set is a name in SETS (all of them by default). Each row is one set, from the front, 30 degrees and IsoRim's 55 degree
camera. Reads the game's skin.txt, so it is the export's mesh and face patch, with the face and mouth moved as the game
moves them (FaceLayers.FaceRaise, MouthRaise, and a race's mouth row, FaceGaze.RaceMouthShift). A mock-up, not a game
render: flat skin, plain light, the eyes white (no eye colour), no hair or toon bands.
"""
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
SKIN_FILE = os.path.join(ROOT, "M3AT: Maru's 3D Animated Toons", "Models", "chibi", "skin.txt")
W = os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/")
EXP = W + "2581693737/Textures/Things/Pawn/FA-Experimentals/"
RET = W + "3271240549/Textures/Things/Pawn/FA-Experimentals/"
KIIRO = W + "2991489003/Content1.6/Textures/Things/Pawn/Kiiro/"
SKIN = (236 / 255, 200 / 255, 170 / 255)
CELL = 300
VIEWS = (0, 30, 55)
FACE_RAISE = -0.006   # FaceLayers.FaceRaise, texture widths
MOUTH_RAISE = 0.012   # FaceLayers.MouthRaise
MOUTH_ROW = 319.5 / 512   # FaceLayers.MouthCentre's row from the top

# name: (texture root, gender folder, eye type, brow, mouth type, mouth shape, put the mouth on the human row)
SETS = {
    "retexture": (RET, "Female", "Normal", "normal", "Normal", "normal", False),
    "experimentals": (EXP, "Female", "Normal", "normal", "Normal", "normal", False),
    "kiiro_before": (KIIRO, "Unisex", "Normal", "normal", "Normal", "normal", False),
    "kiiro": (KIIRO, "Unisex", "Normal", "normal", "Normal", "normal", True),
    "kiiro_open": (KIIRO, "Unisex", "Normal", "normal", "Normal", "open", True),
    "kiiro_spread": (KIIRO, "Unisex", "Normal", "normal", "Normal", "normal", True, False, True),
    "kiiro_fit": (KIIRO, "Unisex", "Normal", "normal", "Normal", "normal", True, True),
    "kiiro_fit_open": (KIIRO, "Unisex", "Normal", "normal", "Normal", "open", True, True),
}
# A mock-up of fitting a race's eyes to the human layout: its eyes, lids and brows scaled and moved so the white of its
# eyes covers the box the reference set's open eyes show through (FIT_TO, from its lid cover's holes).
FIT_TO = (RET, "Female")


def to_blender(p):
    return (-p[0], -p[2], p[1])


def read_skin():
    verts, tris, fv, ff = [], [], [], []
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
    return verts, tris, fv, ff


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
    a = a.reshape(h, w, 4)
    if w != 512:
        idx = (np.arange(512) * w // 512)
        a = a[idx][:, idx]
    return a


def over(base, layer):
    a = layer[..., 3:]
    base[..., :3] = layer[..., :3] * a + base[..., :3] * (1 - a)


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


def mouth_row(mouth):
    """The mouth's alpha-weighted middle row, from the top, as FaceGaze.RaceMouthShift measures it."""
    a = mouth[..., 3]
    rows = np.arange(512)[:, None] + 0.5   # Blender's rows go up
    v = float((a * rows).sum() / a.sum()) / 512
    return 1 - v


# A mock-up of moving a race's eyes apart to the human spacing: the eye, lid and brow layers' left half moved left and
# right half right, each SPREAD texture widths (the centres from Kiiro's 0.107 apart to the retexture's usual 0.126).
SPREAD = (0.126 - 0.107) / 2


def spread(a):
    px = round(SPREAD * 512)
    out = np.zeros_like(a)
    out[:, :256 - px] = a[:, px:256]
    out[:, 256 + px:] = a[:, 256:512 - px]
    return out


def face_image(root, gender, eye, brow, mouth_type, shape, human_row, fit=False, apart=False):
    """The layers in Facial Animation's order, moved as the game moves them. Rows here go up, as Blender's pixels do."""
    base = np.ones((512, 512, 4), np.float32)
    base[..., :3] = SKIN
    path = lambda p: root + p % gender
    move = eye_fit(path) if fit else spread if apart else (lambda a: a)
    load = lambda p: move(load_rgba(p))
    cover = load(path("Lids/Normal/%s/normal_cover_south.png"))
    # Inside the lid cover's holes: the white (the lid's bottom layer), the iris, its shine. The Common masks only say
    # where the eye colour goes, so they are left out (the eyes keep the art's own colour).
    eyes = base.copy()
    eyes[..., :3] = 1.0
    for p in ("Lids/Normal/%s/normal_bottom_south.png", "Eyes/" + eye + "/%s/normal_south.png", "Eyes/" + eye + "/%s/normal_highlight_south.png"):
        if os.path.exists(path(p)):
            over(eyes, load(path(p)))
    # Where the cover is open: the holes it shuts in, or, a cover with no shut holes (Kiiro's soft halo), wherever the
    # white is drawn.
    holes = holes_of(cover[..., 3])
    if holes.sum() < 200:
        bottom = path("Lids/Normal/%s/normal_bottom_south.png")
        holes = load(bottom)[..., 3] if os.path.exists(bottom) else holes
    base[..., :3] = eyes[..., :3] * holes[..., None] + base[..., :3] * (1 - holes[..., None])
    # The cover is the skin's colour times the mask (FaceLayers mode 5), so its lashes and lid lines stay dark.
    tinted = cover.copy()
    tinted[..., :3] = cover[..., :3] * SKIN
    over(base, tinted)
    mouth = load_rgba(path("Mouth/" + mouth_type + "/%s/" + shape + "_south.png"))
    up = MOUTH_RAISE
    if human_row:
        shift = mouth_row(load_rgba(path("Mouth/" + mouth_type + "/%s/normal_south.png"))) - MOUTH_ROW   # rows from the top: lower is more
        up += shift if abs(shift) >= 0.004 else 0
    over(base, np.roll(mouth, round(up * 512), axis=0))
    over(base, load(path("Brows/Normal/%s/" + brow + "_south.png")))
    base = np.roll(base, round(FACE_RAISE * 512), axis=0)
    base[..., :3] = np.where(base[..., :3] <= 0.04045, base[..., :3] / 12.92, ((base[..., :3] + 0.055) / 1.055) ** 2.4)
    img = bpy.data.images.new("face", 512, 512, alpha=True, float_buffer=True)
    img.colorspace_settings.name = "Linear Rec.709"
    img.pixels.foreach_set(base.ravel())
    img.pack()
    return img


def box(a):
    ys, xs = np.nonzero(a > 0.5)
    return xs.min(), xs.max(), ys.min(), ys.max()


def eye_fit(path):
    """The move taking this set's eye whites onto the reference's open eyes, as a function on a layer."""
    root, gender = FIT_TO
    ref = holes_of(load_rgba(root + "Lids/Normal/%s/normal_cover_south.png" % gender)[..., 3])
    x0, x1, y0, y1 = box(ref)
    a0, a1, b0, b1 = box(load_rgba(path("Lids/Normal/%s/normal_bottom_south.png"))[..., 3])
    scale = (x1 - x0) / (a1 - a0)   # by the width, kept round
    cx, cy, ax, ay = (x0 + x1) / 2, (y0 + y1) / 2, (a0 + a1) / 2, (b0 + b1) / 2
    print("FIT scale %.3f, moved %.1f px up" % (scale, cy - ay))
    xs = np.clip(np.round(ax + (np.arange(512) - cx) / scale).astype(int), 0, 511)
    ys = np.clip(np.round(ay + (np.arange(512) - cy) / scale).astype(int), 0, 511)
    return lambda a: a[ys][:, xs]


def build(verts, tris, fv, ff):
    me = bpy.data.meshes.new("body")
    me.from_pydata([to_blender(p) for p in verts], [], tris)
    me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
    body = bpy.data.objects.new("body", me)
    bpy.context.scene.collection.objects.link(body)
    at, places, uvs = {}, [], []
    for i, u, v, p in fv:
        at[i] = len(places)
        places.append(to_blender(p))
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
    sheet_path, names = args[0], args[1:] or list(SETS)
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
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 1.6
    sun.rotation_euler = (math.radians(35), math.radians(-15), 0)
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = float(os.environ.get("ZOOM", "0.55"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    verts, tris, fv, ff = read_skin()
    target = Vector((0, -0.08, float(os.environ.get("TARGET_Z", "1.0"))))
    body, face = build(verts, tris, fv, ff)
    body.data.materials.append(material("skin", srgb_to_linear(SKIN)))
    tmp = sheet_path + ".cell.png"
    scene.render.filepath = tmp
    rows = []
    for name in names:
        img = face_image(*SETS[name])
        face.data.materials.clear()
        face.data.materials.append(material("f", image=img))
        row = []
        for angle in VIEWS:
            a = math.radians(angle)
            cam.location = target + Vector((0, -math.cos(a), math.sin(a))) * 3
            cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
            bpy.ops.render.render(write_still=True)
            row.append(load_cell(tmp))
        rows.append(np.concatenate(row, axis=1))
        print("ROW", name)
    whole = np.concatenate(rows[::-1], axis=0)
    h, w = whole.shape[:2]
    out = bpy.data.images.new("sheet", w, h, alpha=True)
    out.pixels.foreach_set(whole.ravel())
    out.filepath_raw = sheet_path
    out.file_format = "PNG"
    out.save()
    os.remove(tmp)
    print("SHEET", sheet_path, names)


def load_cell(path):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    a = np.zeros(w * h * 4, np.float32)
    img.pixels.foreach_get(a)
    bpy.data.images.remove(img)
    return a.reshape(h, w, 4).copy()


if __name__ == "__main__":
    main()
