"""Asks Gemini to repaint renders of the recon helmet's shell (its visor taken off), as views for Meshy's multitexture.

    python3 gen.py <view> <count> [front reference .png]
    python3 gen.py edit <view> <in.png> <out prefix> <count> <what to change>

Reads 0_render_<view>.png (render.py), writes 1_<view>_<n>.png. The front is made first; the side and back are given
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
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'ArmorHelmetRecon', 'gemini'))
ROOT = os.path.join(HERE, '..', '..', '..', '..')
KEY = open(os.path.join(ROOT, 'geminiAPIkey')).read().strip()
API = 'https://generativelanguage.googleapis.com/v1beta/models/%s:generateContent'
MODEL = 'gemini-3-pro-image'

COMMON = (
    "The first image is an orthographic render of a 3D model of a sci-fi recon armour helmet, {view}, on a flat grey "
    "background. Its visor glass has been taken off, so the large opening at the front is an empty hole. Repaint the "
    "helmet's surface as a clean texture reference for retexturing this exact model. Keep the silhouette, the shape, "
    "the framing, the viewpoint and the size exactly as they are, pixel for pixel: change only the paint on the surface. "
    "Style: soft hand-painted stylised cartoon, even flat lighting with no strong shadows, no reflections and no "
    "highlights. The shell is light neutral grey hard composite armour plates, nearly white, with crisp moulded panel "
    "lines dividing it into a few large plates, a few small rivets, and small vents on the sides. Paint the back and "
    "sides as carefully as the front, with the same panels and detail, not blotchy or smeared. Round the face opening "
    "there is only a narrow slightly darker grey trim band, a thin rim no wider than a finger: the wide grey frame "
    "round the opening in the render is a mistake (a false visor) and must become ordinary light shell plates like "
    "the rest. Anything seen inside the helmet through the opening is plain flat dark grey. No logos, no text, no "
    "numbers, no colours other than greys. The background stays the same flat grey. ")
VIEWS = {
    'front': 'seen straight on from the front, looking into the face opening',
    'side': 'seen exactly from the side, facing left',
    'back': 'seen straight on from behind',
}
MATCH = ("The second image is the same helmet already repainted from the front: paint this view to match it exactly, "
         "the same greys, panel lines, trim band and style, continuing its panels round onto this side.")


EDIT = ("The first image is a painted texture reference of a sci-fi recon armour helmet, {view}. The second image is a "
        "render of the 3D model it must fit. Change only this, keeping everything else exactly as it is, the same "
        "silhouette, framing, size, greys and style: ")


def ask(view, ref, out, text=None, images=None):
    if os.path.exists(out):
        return out
    parts = [{'text': text or COMMON.format(view=VIEWS[view]) + (MATCH if ref else '')}]
    for path in images or [os.path.join(HERE, '0_render_%s.png' % view)] + ([ref] if ref else []):
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
