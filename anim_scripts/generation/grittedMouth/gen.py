"""Asks Gemini for a gritted-teeth mouth in the style of a row of existing mouths.

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
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'generationAssets', 'grittedMouth'))
KEY = open(os.path.join(HERE, "..", "..", "geminiAPIkey")).read().strip()
API = "https://generativelanguage.googleapis.com/v1beta/models/%s:generateContent"

COMMON = ("The image shows a row of mouths from one anime-style character sprite set, greatly enlarged, on flat green. "
          "Draw one new mouth for the same set: a straight-on front view of a person gritting their teeth in severe pain. "
          "The lips are drawn back and the upper and lower teeth are clenched together, showing a wide, low, slightly "
          "downturned rounded rectangle of teeth, wider at the corners as in a grimace. The teeth are split by one "
          "horizontal line where they meet and only three or four short vertical lines, as it will be shrunk to about 28 "
          "pixels wide and 12 tall and must still read as clenched teeth at that size: big simple shapes, no fine detail, "
          "no shine, no individual tooth shading. "
          "Only the mouth: no nose, chin, skin, face, tongue, text, shadow or outline glow. Left-right symmetric. "
          "It fills the middle third of the picture's width, centred. The background is one perfectly flat pure green, "
          "#00FF00, right up to the mouth's edge. ")
STYLES = {
    "fa": COMMON + ("Match this set's style exactly: soft painted shading with no black lines at all, the outline a darker "
                    "muted red-brown of the lips (about #9C6056), the lips a muted salmon pink, the teeth a warm off-white "
                    "shaded with soft pink-beige rather than pure white, their dividing lines thin and muted red-brown."),
    "retex": COMMON + ("Match this set's style exactly: flat cel colours with no gradients and a thick solid black inked "
                       "outline of even weight, heavier along the top, as on the open mouths in the picture, the teeth flat "
                       "pure white as on the picture's smiling mouths, their dividing lines solid black."),
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
