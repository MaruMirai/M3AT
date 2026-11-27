"""A .blend of the Miho 3D Pack's body, for looking at it: M3AT's chibi body, the Miho body (the pack's finer chest and seat,
with its bust-and-hips key as a shape key you can slide), and that body with the pack's fox ears and tail and one of M3AT's
shirts following the key.

The pack's own formulas (Source/miho_bust.py in the installed Workshop copy: BustShape, refined) do the work, so this shows
what the pack draws, not a copy of it. They are plain Python; the body, the shirts and the key's moves are M3AT's.

Run:  blender -b --python anim_scripts/miho_body_blend.py -- <out.blend> [shirt name, default CollarShirt]

No armature or bone weights: the finer mesh adds vertices the weights don't cover. It is for the shape, not for posing.
"""
import importlib.util
import os
import sys

import bpy

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MOD = os.path.join(ROOT, "M3AT: Maru's 3D Animated Toons")
CHIBI = os.path.join(MOD, "Models", "chibi")
KIT = os.path.join(MOD, "Docs", "Tools", "m3at_garment_kit.py")
PACK = os.environ.get("MIHO_PACK", os.path.expanduser("~/.local/share/Steam/steamapps/workshop/content/294100/3843413189"))

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = args[0] if args else os.path.join(ROOT, "Models", "unused", "miho_body.blend")
SHIRT = args[1] if len(args) > 1 else "CollarShirt"
GAP = 0.45          # cells between the figures
KEY = "MihoPack_Bust"

sys.path.insert(0, os.path.join(PACK, "Source"))
import miho_bust  # noqa: E402  (the pack's formulas, plain Python)

spec = importlib.util.spec_from_file_location("m3at_garment_kit", KIT)
kit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(kit)


def contents_of(path):
    """A garment-kit piece as the pack's functions take it: vertices, faces, keys {name: {vertex: (dx, dy, dz)}}."""
    piece = kit.read_piece(path)
    keys = {name: dict(moves) if isinstance(moves, dict) else dict(enumerate(moves)) for name, moves in piece["keys"].items()}
    return {"vertices": [tuple(v) for v in piece["v"]], "faces": [tuple(f) for f in piece["f"]], "keys": keys,
            "uvs": [tuple(t) for t in piece["t"]] if piece["t"] else None}


def material(name, colour):
    made = bpy.data.materials.new(name)
    made.diffuse_color = colour
    return made


def mesh_object(name, collection, contents, material_, shape_keys, values, at):
    """contents' mesh with every key in shape_keys {name: [(dx, dy, dz) per vertex]} as a shape key at values[name] (0 if absent)."""
    verts = [kit.to_blender(*v) for v in contents["vertices"]]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], [(a, c, b) for a, b, c in contents["faces"]])   # winding reversed, as the kit does
    for p in mesh.polygons:
        p.use_smooth = True
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    obj.location = at
    obj.data.materials.append(material_)
    if shape_keys:
        obj.shape_key_add(name="Basis", from_mix=False)
        for key, moves in shape_keys.items():
            block = obj.shape_key_add(name=key, from_mix=False)
            for i, d in enumerate(moves):
                block.data[i].co = kit.to_blender(*(contents["vertices"][i][c] + d[c] for c in range(3)))
            block.value = values.get(key, 0.0)
    return obj


def key_arrays(contents, extra=None):
    """Every key of contents as a full per-vertex list of moves, plus the extra {name: per-vertex list}."""
    n = len(contents["vertices"])
    arrays = {name: [moves.get(i, (0.0, 0.0, 0.0)) for i in range(n)] for name, moves in contents["keys"].items()}
    arrays.update(extra or {})
    return arrays


def read_part(path):
    """Ears or tail file: v and f lines, game space."""
    vs, fs = [], []
    for line in open(path, encoding="utf-8"):
        p = line.split()
        if p and p[0] == "v":
            vs.append(tuple(map(float, p[1:4])))
        elif p and p[0] == "f":
            fs.append(tuple(int(x) for x in p[1:4]))
    if fs and min(min(f) for f in fs) == 1:
        fs = [tuple(i - 1 for i in f) for f in fs]
    return {"vertices": vs, "faces": fs, "keys": {}}


def label(text, at, collection):
    curve = bpy.data.curves.new(text, "FONT")
    curve.body = text
    curve.size = 0.045
    curve.align_x = "CENTER"
    obj = bpy.data.objects.new("label " + text, curve)
    obj.location = (at[0], at[1] - 0.3, 0.0)
    obj.rotation_euler = (1.5708, 0, 0)
    obj.location.z = -0.02
    collection.objects.link(obj)


def main():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    collection = bpy.data.collections.new("Miho body")
    bpy.context.scene.collection.children.link(collection)
    skin = material("skin", (0.93, 0.80, 0.70, 1.0))
    shirt_material = material("shirt", (0.86, 0.88, 0.92, 1.0))
    fur = material("fox fur", (0.95, 0.93, 0.90, 1.0))

    body = contents_of(os.path.join(CHIBI, "skin.txt"))
    shirt = contents_of(os.path.join(CHIBI, "garment_%s.txt" % SHIRT))
    bust = miho_bust.BustShape(body)
    print("body: %d vertices, %d triangles; shirt %s: %d triangles" % (len(body["vertices"]), len(body["faces"]), SHIRT, len(shirt["faces"])))

    # The Female key: Miho's usual body type, which M3AT mixes in for every woman.
    female = {"Female": 1.0}

    # 1. M3AT's body as it is.
    plain = mesh_object("M3AT body (Female)", collection, body, skin, key_arrays(body), female, (0.0, 0.0, 0.0))

    # 2. The Miho body: the finer chest and seat, the key (bust and hips) a slider at 1, Female at 1.
    fine, _ = miho_bust.refined(body, bust.offset)
    print("Miho body, finer: %d vertices, %d triangles" % (len(fine["vertices"]), len(fine["faces"])))
    fine_keys = key_arrays(fine, {KEY: [bust.offset(*v) for v in fine["vertices"]]})
    miho = mesh_object("Miho body", collection, fine, skin, fine_keys, dict(female, **{KEY: 1.0}), (GAP, 0.0, 0.0))

    # 3. The Miho body with the pack's fox ears and tail, and a shirt that follows the key (the cleavage bridged a little).
    third = (2 * GAP, 0.0, 0.0)
    mesh_object("Miho body (with ears, tail, shirt)", collection, fine, skin, fine_keys, dict(female, **{KEY: 1.0}), third)
    shirt_fine, _ = miho_bust.refined(shirt, bust.garment_offset)
    shirt_keys = key_arrays(shirt_fine, {KEY: [bust.garment_offset(*v) for v in shirt_fine["vertices"]]})
    mesh_object(SHIRT, collection, shirt_fine, shirt_material, shirt_keys, dict(female, **{KEY: 1.0}), third)
    for name, file in (("fox ears", "ears_fox.txt"), ("fox tail", "tail_fox.txt")):
        path = os.path.join(PACK, "M3AT", file)
        if os.path.isfile(path):
            mesh_object(name, collection, read_part(path), fur, None, {}, third)
        else:
            print("not found:", path)

    label("M3AT body (Female key)", (0.0, 0.0), collection)
    label("Miho body: finer chest and seat, bust and hips key at 1", (GAP, 0.0), collection)
    label("with her fox ears and tail, and %s following the key" % SHIRT, third, collection)

    scene = bpy.context.scene
    for area in (a for s in bpy.data.screens for a in s.areas if a.type == "VIEW_3D"):
        for space in area.spaces:
            if space.type == "VIEW_3D":
                space.shading.type = "SOLID"
                space.shading.color_type = "MATERIAL"
                space.shading.show_cavity = True
    os.makedirs(os.path.dirname(os.path.abspath(OUT)), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=OUT)
    print("saved", OUT)


main()
