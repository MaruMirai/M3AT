"""Wide mouth shapes on the chibi's face before and after FaceLayers.MouthShapeScale narrows them.

    /home/t/Programs/blender-5.2.3-linux-x64/blender -b --python anim_scripts/mouth_compare.py -- <sheet.png> [from keep]

Each row is one mouth shape: from the front unchanged and narrowed, then from 30 degrees above the same (IsoRim's 55 hides the mouth under the brow).
from and keep are MouthWideFrom and MouthWideKeep (0.95 and 0.35 by default). The width is measured as the game measures
it (WidthOf: the columns with alpha 60 or more, read at 256 px), against the human median (the retexture's own, as
HumanMouthWidthNow reads it with the retexture loaded), and the mouth layer scaled about MouthCentre as TuneOf zooms it.
A mock-up on face_compare.py's renderer: flat skin, plain light, no hair. Labels are printed as ROW lines, in order.
"""
import math
import os
import sys

import numpy as np
import bpy
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import face_compare as fc

HUMAN = 0.0390625   # the retexture's median south mouth width, MouthWidthOf("Things/Pawn/FA-Experimentals/")
UNAGI = fc.W + "3271240549/ModCompat/UnagiFA/Textures/Things/Pawn/FA-Experimentals/"

# (label, texture root, mouth type, shape): the retexture's widest, a normal mouth to show the narrow ones are left,
# Experimentals' puzzled and shark smile, and UNAGI's very wide one.
SHAPES = [
    ("Retexture Normal, normal", fc.RET, "Normal", "normal"),
    ("Retexture Impish, smile", fc.RET, "Impish", "smile"),
    ("Retexture Impish, open", fc.RET, "Impish", "open"),
    ("Retexture Smirk, normal", fc.RET, "Smirk", "normal"),
    ("Retexture Smirk, open", fc.RET, "Smirk", "open"),
    ("Experimentals Normal, puzzle", fc.EXP, "Normal", "puzzle"),
    ("Experimentals Shark, smile", fc.EXP, "Shark", "smile"),
    ("UNAGI Happakuwae, normal", UNAGI, "Happakuwae", "normal"),
]
# UNAGI Facial Animation with the retexture: the retexture's patch for it (its ModCompat folder) first, then the
# retexture, UNAGI and Experimentals, as the game finds a path with them loaded in that order (MOUTHS=unagi).
UNAGI_OWN = fc.W + "3268178647/Textures/Things/Pawn/FA-Experimentals/"
UNAGI_ORDER = [UNAGI, fc.RET, UNAGI_OWN, fc.EXP]
UNAGI_WIDE = [
    ("Teeth F, smile", "Teeth", "smile", "Female"),
    ("Genki F, normal", "Genki", "normal", "Female"),
    ("Genki F, smile", "Genki", "smile", "Female"),
    ("Oldman M, smile", "Oldman", "smile", "Male"),
    ("Monkey F, open", "Monkey", "open", "Female"),
    ("Tobacco M, normal", "Tobacco", "normal", "Male"),
    ("Frog F, normal", "Frog", "normal", "Female"),
    ("Kiba F, open", "Kiba", "open", "Female"),
    ("Gizaba M, smile", "Gizaba", "smile", "Male"),
    ("Honyahonya M, open", "Honyahonya", "open", "Male"),
    ("Happakuwae F, normal", "Happakuwae", "normal", "Female"),
]


_exists = os.path.exists   # face_image's own checks are pointed at resolved() meanwhile


def resolved(rel):
    for root in UNAGI_ORDER:
        if _exists(root + rel):
            return root + rel
    return None


# The retexture's Smirk in the male face, whose closed mouth is a longer slanted line than the female one's
# (MOUTHS=smirk). A fifth field is the gender folder, the face drawn in that gender's art too.
SMIRK = [
    ("Retexture Normal M, normal", fc.RET, "Normal", "normal", "Male"),
    ("Retexture Smirk M, normal", fc.RET, "Smirk", "normal", "Male"),
    ("Retexture Smirk M, open", fc.RET, "Smirk", "open", "Male"),
]


def width_of(mouth):
    """As FaceRestyle.WidthOf: alpha 60 or more, at 256 px across."""
    a = mouth[::2, ::2, 3]
    cols = np.nonzero((a >= 60 / 255).any(axis=0))[0]
    return (cols[-1] - cols[0] + 1) / 256 if len(cols) else 0.0


def shape_scale(own, start, keep):
    edge = start * HUMAN
    if own <= edge or own <= 0:
        return 1.0
    return (edge + (own - edge) * keep) / own


def scaled(mouth, s):
    """The layer zoomed by s about MouthCentre (u 0.5, 319.5 px from the top), as the shader reads it."""
    if s == 1.0:
        return mouth
    cx, cy = 255.5, 511 - 319.5   # Blender's rows go up
    xs = np.clip(np.round(cx + (np.arange(512) - cx) / s).astype(int), 0, 511)
    ys = np.clip(np.round(cy + (np.arange(512) - cy) / s).astype(int), 0, 511)
    out = mouth[ys][:, xs].copy()
    out[..., 3] *= ((np.abs(np.arange(512) - cy) / s <= 256)[:, None] & (np.abs(np.arange(512) - cx) / s <= 256)[None, :])
    return out


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    sheet = args[0]
    start = float(args[1]) if len(args) > 1 else 0.95
    keep = float(args[2]) if len(args) > 2 else 0.35
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 24
    scene.cycles.device = "CPU"
    scene.render.resolution_x = scene.render.resolution_y = fc.CELL
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
    cam.data.ortho_scale = 0.55
    scene.collection.objects.link(cam)
    scene.camera = cam
    body, face = fc.build(*fc.read_skin())
    body.data.materials.append(fc.material("skin", fc.srgb_to_linear(fc.SKIN)))
    target = Vector((0, -0.08, 1.0))
    tmp = sheet + ".cell.png"
    scene.render.filepath = tmp
    real_load = fc.load_rgba
    rows = []
    if os.environ.get("MOUTHS") == "unagi":
        settings = [tuple(map(float, x.split(","))) for x in os.environ.get("SETTINGS", "0.95,0.35;0.8,0.3;0.7,0.25").split(";")]
        relative = lambda p: p.split("FA-Experimentals/", 1)[1]
        exists = os.path.exists
        # MOUTH_TYPE=<type> shows every south shape of that type in both genders instead.
        chosen = UNAGI_WIDE
        if os.environ.get("MOUTH_TYPE"):
            mt = os.environ["MOUTH_TYPE"]
            chosen = []
            for gender in ("Female", "Male"):
                shapes = set()
                for root in UNAGI_ORDER:
                    folder = root + "Mouth/%s/%s/" % (mt, gender)
                    if _exists(folder):
                        shapes |= {f[:-len("_south.png")] for f in os.listdir(folder) if f.endswith("_south.png")}
                chosen += [("%s %s, %s" % (mt, gender[0], sh), mt, sh, gender) for sh in sorted(shapes)]
        for label, mtype, shape, gender in chosen:
            mouth_path = resolved("Mouth/%s/%s/%s_south.png" % (mtype, gender, shape))
            own = width_of(real_load(mouth_path))
            cells, drawn = [], []
            for start_, keep_ in [(99.0, 1.0)] + settings:
                s_ = shape_scale(own, start_, keep_)
                drawn.append(s_)
                def load(p, factor=s_):
                    if "/Mouth/" in p:
                        return scaled(real_load(mouth_path), factor)
                    return real_load(resolved(relative(p)) or p)
                fc.load_rgba = load
                os.path.exists = lambda p: exists(p) or ("FA-Experimentals/" in p and resolved(relative(p)) is not None)
                img = fc.face_image(fc.RET, gender, "Normal", "normal", "Normal", "normal", False)
                os.path.exists = exists
                fc.load_rgba = real_load
                face.data.materials.clear()
                face.data.materials.append(fc.material("f", image=img))
                cam.location = target + Vector((0, -3, 0))
                cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
                bpy.ops.render.render(write_still=True)
                cells.append(fc.load_cell(tmp))
            rows.append(np.concatenate(cells, axis=1))
            print("ROW %s | width %.4f (%.2f of a human's) | drawn %s" % (label, own, own / HUMAN, " ".join("%.2f" % d for d in drawn)))
        whole = np.concatenate(rows[::-1], axis=0)
        out = bpy.data.images.new("sheet", whole.shape[1], whole.shape[0], alpha=True)
        out.pixels.foreach_set(whole.ravel())
        out.filepath_raw = sheet
        out.file_format = "PNG"
        out.save()
        os.remove(tmp)
        print("SHEET", sheet)
        return
    for entry in (SMIRK if os.environ.get("MOUTHS") == "smirk" else SHAPES):
        label, root, mtype, shape = entry[:4]
        gender = entry[4] if len(entry) > 4 else "Female"
        mouth_path = root + "Mouth/" + mtype + "/" + gender + "/" + shape + "_south.png"
        if not os.path.exists(mouth_path):   # a shape the retexture leaves to Experimentals, as the game finds it
            mouth_path = fc.EXP + "Mouth/" + mtype + "/" + gender + "/" + shape + "_south.png"
        own = width_of(real_load(mouth_path))
        s = shape_scale(own, start, keep)
        cells = {}
        for name, factor in (("before", 1.0), ("after", s)):
            # face_compare draws the face from the retexture's eyes and brows with this mouth swapped in, zoomed.
            def load(p, factor=factor):
                return scaled(real_load(mouth_path), factor) if "/Mouth/" in p else real_load(p)
            fc.load_rgba = load
            img = fc.face_image(fc.RET, gender, "Normal", "normal", "Normal", "normal", False)
            fc.load_rgba = real_load
            face.data.materials.clear()
            face.data.materials.append(fc.material("f", image=img))
            for angle in (0, 30):
                a = math.radians(angle)
                cam.location = target + Vector((0, -math.cos(a), math.sin(a))) * 3
                cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
                bpy.ops.render.render(write_still=True)
                cells[(name, angle)] = fc.load_cell(tmp)
        rows.append(np.concatenate([cells[("before", 0)], cells[("after", 0)], cells[("before", 30)], cells[("after", 30)]], axis=1))
        print("ROW %s | width %.4f (%.2f of a human's) | drawn %.2f times" % (label, own, own / HUMAN, s))
    whole = np.concatenate(rows[::-1], axis=0)
    h, w = whole.shape[:2]
    out = bpy.data.images.new("sheet", w, h, alpha=True)
    out.pixels.foreach_set(whole.ravel())
    out.filepath_raw = sheet
    out.file_format = "PNG"
    out.save()
    os.remove(tmp)
    print("SHEET", sheet)


main()
