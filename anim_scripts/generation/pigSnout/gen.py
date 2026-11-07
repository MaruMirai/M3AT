"""Asks Gemini for a pig's snout in the style of Facial Animation's faces, for the Nose_Pig gene.

    python3 gen.py <model> <count> [style ...]

Writes raw/<style>_<n>.png. Keeps what is already there, so a run can be picked up again.
"""
import base64
import concurrent.futures
import json
import os
import sys
import urllib.error
import urllib.request

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'pigSnout'))
KEY = open(os.path.join(HERE, "..", "..", "geminiAPIkey")).read().strip()
API = "https://generativelanguage.googleapis.com/v1beta/models/%s:generateContent"

COMMON = ("The image shows two faces from one anime-style character sprite set, greatly enlarged, on flat green. Draw one "
          "new part for this set, alone: a cute pig's snout, seen straight on from the front. A wide rounded oval disc, "
          "about one and a half times as wide as it is tall, with two big upright oval nostrils side by side in its "
          "middle, each about a fifth of the disc's width, set well apart. The disc is exactly the skin colour of the "
          "faces in the picture, about #ECC8AA. It will be shrunk to about 34 pixels wide and 22 tall and must still "
          "read as a pig's snout at that size: big simple shapes, no fine detail, no hairs, no shine, no highlight, no "
          "texture. Only the snout: no eyes, brows, mouth, face, text or shadow on the ground. Left-right symmetric. It "
          "fills the middle half of the picture's width, centred. The background is one perfectly flat pure green, "
          "#00FF00, right up to the snout's edge. ")
STYLES = {
    "fa": COMMON + ("Match this set's style exactly: the disc flat over nearly all of it, with only a soft shadow just "
                    "inside its lower edge. Its outline and the nostrils are soft painted muted red-brown, the colour of "
                    "the mouths' dark edges (about #9C6056), the nostrils darkest in the middle (about #5A302C). No black "
                    "lines."),
    "retex": COMMON + ("Match this set's style exactly: flat cel colours with no gradients and a thick solid black inked "
                       "outline of even weight round the disc, heavier along the bottom, as on the mouths and brows in "
                       "the picture. The nostrils are solid flat black ovals."),
}


def ask(model, text, ref, out):
    if os.path.exists(out):
        return out
    with open(ref, "rb") as f:
        img = {"inlineData": {"mimeType": "image/png", "data": base64.b64encode(f.read()).decode()}}
    body = {"contents": [{"role": "user", "parts": [{"text": text}, img]}],
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
                    print("made", os.path.basename(out), got.get("usageMetadata", {}).get("totalTokenCount"), flush=True)
                    return out
            print("no image", out, str(got)[:300], flush=True)
        except urllib.error.HTTPError as e:
            print("HTTP", e.code, out, e.read().decode()[:300], flush=True)
        except Exception as e:
            print("failed", out, e, flush=True)
    return None


if __name__ == "__main__":
    model, count = sys.argv[1], int(sys.argv[2])
    styles = sys.argv[3:] or list(STYLES)
    os.makedirs(os.path.join(HERE, "raw"), exist_ok=True)
    jobs = [(s, n) for s in styles for n in range(count)]
    with concurrent.futures.ThreadPoolExecutor(4) as pool:
        list(pool.map(lambda j: ask(model, STYLES[j[0]], os.path.join(HERE, "refs", j[0] + ".png"),
                                    os.path.join(HERE, "raw", "%s_%d.png" % j)), jobs))
