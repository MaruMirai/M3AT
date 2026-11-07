"""Repaints renders of the cataphract helmet as modelled (Models/garments/ArmorHelmetCataphract.glb, render.py) after
the painted design views (4_design_<view>.png), so the views Meshy's multitexture gets line up with the mesh.

    python3 repaint.py <view> <count>

Writes 5_<view>_<n>.png. Keeps what is already there.
"""
import concurrent.futures, os, sys, time, urllib.request
# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'ArmorHelmetCataphract', 'gemini'))
sys.path.insert(0, os.path.join(HERE, '..', '..', '..', '..', 'anim_scripts'))
import meshy

VIEWS = {'front': 'seen straight on from the front', 'side': 'seen exactly from the side, facing left',
         'back': 'seen straight on from behind'}
TEXT = ("The first image is an orthographic render of a 3D model of a fully enclosed sci-fi cataphract armour helmet, "
        "{view}, on a flat grey background, still wearing an older model's paint. Repaint its surface as a clean "
        "texture reference for retexturing this exact model. Keep the silhouette, outline, shape, framing, viewpoint "
        "and size exactly as in the first image, pixel for pixel: change only the paint. Ignore the first image's "
        "paint. The second image is the design to paint on it, {view}: copy its plates, the seam down the faceplate, "
        "the panel lines, rivets, vents and greys, and its style exactly, the soft hand-painted flat cartoon look with "
        "dark ink outlines and even flat lighting with no shadows, reflections or highlights, fitting them to the "
        "model's own moulded shapes. The faceplate is solid near-white armour: no visor, no glass, no lens, no camera. "
        "No logos, letters or text, no colours other than greys. The background stays the same flat grey.")


def one(view, n):
    out = os.path.join(HERE, '5_%s_%d.png' % (view, n))
    if os.path.exists(out):
        return
    images = [os.path.join(HERE, '0_render_%s.png' % view), os.path.join(HERE, '4_design_%s.png' % view)]
    t = meshy.call('POST', '/image-to-image', {'ai_model': 'nano-banana-2', 'prompt': TEXT.format(view=VIEWS[view]),
                                              'reference_image_urls': [meshy.data_uri(p) for p in images]})
    task = t['result']
    while True:
        time.sleep(5)
        t = meshy.call('GET', '/image-to-image/' + task)
        if t['status'] in ('SUCCEEDED', 'FAILED', 'CANCELED'):
            break
    if t['status'] != 'SUCCEEDED':
        print('failed', out, t.get('task_error'), flush=True)
        return
    urllib.request.urlretrieve(t['image_urls'][0], out)
    print('made', os.path.basename(out), 'credits', t.get('consumed_credits'), flush=True)


if __name__ == '__main__':
    view, count = sys.argv[1], int(sys.argv[2])
    with concurrent.futures.ThreadPoolExecutor(4) as pool:
        list(pool.map(lambda n: one(view, n), range(count)))
