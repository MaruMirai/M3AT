"""Asks Gemini to repaint renders of the recon armour (cut down from the power armour), as views for Meshy's multitexture.

    python3 gen.py <view> <count> [front reference .png]
    python3 gen.py edit <view> <in.png> <out prefix> <count> <what to change>

Reads 0_render_<view>.png and the power armour's 0_power_<view>.png (render.py), writes 1_<view>_<n>.png. The front is made first; the side and back are given
the front picked as a second image, so all three are painted alike. Keeps what is already there.
"""
import base64
import concurrent.futures
import json
import os
import sys
import urllib.error
import urllib.request

# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'ArmorRecon', 'gemini'))
ROOT = os.path.join(HERE, '..', '..', '..', '..')
KEY = open(os.path.join(ROOT, 'geminiAPIkey')).read().strip()
API = 'https://generativelanguage.googleapis.com/v1beta/models/%s:generateContent'
MODEL = 'gemini-3-pro-image'

COMMON = (
    "The first image is an orthographic render of a 3D model of a sci-fi recon armour suit (no helmet, no person "
    "inside), {view}, on a flat grey background. It was cut down from the heavy power armour shown in the last image, "
    "and still wears that armour's paint. Repaint its surface as a clean texture reference for retexturing this exact "
    "model. Keep the silhouette, the shape, the framing, the viewpoint and the size exactly as they are, pixel for "
    "pixel: change only the paint on the surface. The recon armour is the power armour's lightweight scout version: "
    "the same design family, panel style and greys, but much lighter. Instead of heavy overlapping slabs it is a snug "
    "flexible padded bodysuit of matte slightly darker grey ribbed fabric, showing between the plates, with a few thin "
    "light grey hard composite plates on top: a slim chest plate and upper back plate, shoulder caps, forearm guards, "
    "thigh plates, knee pads and shin guards. Fewer and smaller plates than the power armour, with crisp moulded panel "
    "lines, a few small rivets and vents, light webbing straps and a slim utility belt with small pouches. Flexible "
    "joints at the elbows, waist and knees show the ribbed suit. Style: soft hand-painted stylised cartoon, even flat "
    "lighting with no strong shadows, no reflections and no highlights. Paint the back and sides as carefully as the "
    "front, with the same plates and detail, not blotchy or smeared. No logos, no emblems, no text, no numbers, no "
    "colours other than greys, the plates nearly white. The background stays the same flat grey. ")
VIEWS = {
    'front': 'seen straight on from the front',
    'side': 'seen exactly from the side, facing left',
    'back': 'seen straight on from behind',
}
MATCH = ("The second image is the same recon suit already repainted from the front: paint this view to match it "
         "exactly, the same greys, plates, fabric, straps and style, continuing them round onto this side. ")
EDIT = ("The first image is a painted texture reference of a sci-fi recon armour suit, {view}. The second image is a "
        "render of the 3D model it must fit. Change only this, keeping everything else exactly as it is, the same "
        "silhouette, framing, size, greys and style: ")


def ask(view, ref, out, text=None, images=None):
    if os.path.exists(out):
        return out
    parts = [{'text': text or COMMON.format(view=VIEWS[view]) + (MATCH if ref else '')}]
    for path in images or ([os.path.join(HERE, '0_render_%s.png' % view)] + ([ref] if ref else [])
                           + [os.path.join(HERE, '0_power_%s.png' % view)]):
        with open(path, 'rb') as f:
            parts.append({'inlineData': {'mimeType': 'image/png', 'data': base64.b64encode(f.read()).decode()}})
    body = {'contents': [{'role': 'user', 'parts': parts}],
            'generationConfig': {'responseModalities': ['IMAGE'], 'imageConfig': {'aspectRatio': '1:1'}}}
    req = urllib.request.Request(API % MODEL, data=json.dumps(body).encode(),
                                 headers={'x-goog-api-key': KEY, 'Content-Type': 'application/json'})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                got = json.load(r)
            for p in got['candidates'][0]['content']['parts']:
                if 'inlineData' in p:
                    with open(out, 'wb') as f:
                        f.write(base64.b64decode(p['inlineData']['data']))
                    print('made', os.path.basename(out), flush=True)
                    return out
            print('no image', out, str(got)[:300], flush=True)
        except urllib.error.HTTPError as e:
            print('HTTP', e.code, out, e.read().decode()[:300], flush=True)
        except Exception as e:
            print('failed', out, e, flush=True)
    return None


if __name__ == '__main__':
    if sys.argv[1] == 'edit':
        view, src, prefix, count, what = sys.argv[2:7]
        text = EDIT.format(view=VIEWS[view]) + what
        images = [src, os.path.join(HERE, '0_render_%s.png' % view)]
        with concurrent.futures.ThreadPoolExecutor(4) as pool:
            list(pool.map(lambda n: ask(view, None, os.path.join(HERE, '%s_%d.png' % (prefix, n)), text, images),
                          range(int(count))))
        sys.exit()
    view, count = sys.argv[1], int(sys.argv[2])
    ref = sys.argv[3] if len(sys.argv) > 3 else None
    with concurrent.futures.ThreadPoolExecutor(4) as pool:
        list(pool.map(lambda n: ask(view, ref, os.path.join(HERE, '1_%s_%d.png' % (view, n))), range(count)))
