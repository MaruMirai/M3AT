"""Asks Gemini for matched reference views of a backpack, from renders of the block mesh.

    python3 gen_refs.py <model> <out folder> [design ...]

Each design's outer face is made first, from the block's render; the side and the
body side are then made from that image and the block's render of that side, so
the three agree. Keeps what is already there, so a run can be picked up again.
"""
import base64
import concurrent.futures
import json
import os
import sys
import urllib.error
import urllib.request

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'backpackRefs'))
KEY = open(os.path.join(HERE, "..", "..", "geminiAPIkey")).read().strip()
API = "https://generativelanguage.googleapis.com/v1beta/models/%s:generateContent"

STYLE = ("A 3D model reference picture for a stylised game character's backpack. "
         "Dead straight-on orthographic view, no perspective. The pack alone, no person, no straps "
         "hanging loose or trailing. Chunky, simple, stylised forms with big readable shapes and soft "
         "rounded edges, as on a toy or a chibi figure: every detail is real raised or inset geometry, "
         "with few small parts. All of one matte light neutral grey material, as an untextured clay "
         "render, with thin dark seam lines; no colours, no logos, no text. Soft even light with no cast "
         "shadow. Plain flat mid-grey background. The whole pack in frame with a margin all round.")

DESIGNS = {
    "A_rucksack": "An army canvas rucksack: a big buckled top flap, one large outer pocket, two vertical compression straps with buckles, and a slim pouch on each side.",
    "B_assault": "A boxy modern tactical assault pack: a zipped main compartment, a smaller zipped pocket on the outer face, rows of sewn webbing strips across it, and a low-profile side pocket each side.",
    "C_alice": "An old field pack: a deep cinched top flap held by two buckled straps, three bellows pockets in a row low on the outer face, each with its own small flap.",
    "D_hiker": "A rounded chubby camping pack: a soft domed lid, a big rounded pocket on the outer face with a curved zip, a fat bottom section, and a stubby rounded bottle pocket on each side.",
    "E_bedroll": "A soldier's pack with a rolled blanket strapped across the top under two short straps, and a large flapped pocket on the outer face. The roll stays within the pack's width.",
    "F_plain": "A very plain sturdy canvas pack: one wide top flap with a single centre buckle strap, one simple flat patch pocket, thick stitched edges, and nothing on the sides but a fold.",
}

SHAPE = ("The first image is the low-poly block this must fit over: keep exactly its outline, proportions "
         "and how much of the frame it fills, including the narrowing to the top and the chamfered upper edge. ")
OUTER = ("Turn the block into this backpack, seen from behind the wearer, looking at its outer face. " + SHAPE)
SIDE = ("The second image is the finished backpack's outer face. The first image is the block seen from the "
        "side (its right edge in the picture is the outer face, its left edge lies flat on the wearer's back). "
        "Draw the same backpack, same design and same details, from this side, keeping the block's outline exactly. "
        "The side it rests on the wearer stays flat. ")
BODY = ("The second image is the finished backpack's outer face. The first image is the block seen from the "
        "wearer's side, the face that lies against their back. Draw the same backpack from this side, keeping "
        "the block's outline exactly: a flat padded back panel with a simple seam border and a small carry "
        "loop at the top; no shoulder straps, as they are not modelled. ")


def part(path):
    with open(path, "rb") as f:
        return {"inlineData": {"mimeType": "image/png", "data": base64.b64encode(f.read()).decode()}}


def ask(model, text, images, out):
    if os.path.exists(out):
        return out
    body = {"contents": [{"role": "user", "parts": [{"text": text}] + [part(i) for i in images]}],
            "generationConfig": {"responseModalities": ["IMAGE"], "imageConfig": {"aspectRatio": "1:1"}}}
    req = urllib.request.Request(API % model, data=json.dumps(body).encode(),
                                 headers={"x-goog-api-key": KEY, "Content-Type": "application/json"})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                got = json.load(r)
            for p in got["candidates"][0]["content"]["parts"]:
                if "inlineData" in p:
                    with open(out, "wb") as f:
                        f.write(base64.b64decode(p["inlineData"]["data"]))
                    print("made", out, got.get("usageMetadata", {}).get("totalTokenCount"), flush=True)
                    return out
            print("no image", out, str(got)[:300], flush=True)
        except urllib.error.HTTPError as e:
            print("HTTP", e.code, out, e.read().decode()[:300], flush=True)
        except Exception as e:
            print("failed", out, e, flush=True)
    return None


def design(model, folder, name):
    r = os.path.join(HERE, "renders")
    desc = "The backpack: " + DESIGNS[name] + " " + STYLE
    outer = ask(model, OUTER + desc, [f"{r}/plusY.png"], f"{folder}/{name}_1_outer.png")
    if not outer:
        return
    if os.environ.get("OUTER_ONLY"):
        return
    ask(model, SIDE + desc, [f"{r}/plusX.png", outer], f"{folder}/{name}_2_side.png")
    ask(model, BODY + desc, [f"{r}/minusY.png", outer], f"{folder}/{name}_3_body.png")


if __name__ == "__main__":
    model, folder = sys.argv[1:3]
    os.makedirs(folder, exist_ok=True)
    names = sys.argv[3:] or list(DESIGNS)
    with concurrent.futures.ThreadPoolExecutor(6) as pool:
        list(pool.map(lambda n: design(model, folder, n), names))
