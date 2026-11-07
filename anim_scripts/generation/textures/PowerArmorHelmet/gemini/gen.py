"""Paints the power armour helmet's renders as views for Meshy's multitexture, in the style of the recon armour's
references, through Meshy's image-to-image (Gemini's key being out of credit).

    python3 gen.py <view> <count> [front .png]

Sends 0_render_<view>.png (render.py, the helmet in its old paint), the recon armour's painted front
(style_recon.png) and, for the side and back, the front picked; writes 1_<view>_<n>.png. Keeps what is
already there.
"""
import concurrent.futures, os, sys, time, urllib.request
# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'PowerArmorHelmet', 'gemini'))
sys.path.insert(0, os.path.join(HERE, '..', '..', '..', '..', 'anim_scripts'))
import meshy

MODEL = 'nano-banana-2'
VIEWS = {
    'front': 'seen straight on from the front',
    'side': 'seen exactly from the side, facing left',
    'back': 'seen straight on from behind',
}
COMMON = (
    "The first image is an orthographic render of a 3D model of a sci-fi power armour helmet, {view}, on a flat grey "
    "background, in an older plain paint. Its visor glass has been taken off, so the large opening at the front is an empty hole. Repaint its surface as a clean texture reference for retexturing this exact "
    "model. Keep the silhouette, outline, shape, framing, viewpoint and size exactly as in the first image, pixel for "
    "pixel: change only the paint. The second image is the matching recon body armour, painted: copy its style exactly, "
    "the soft hand-painted flat cartoon look with dark ink outlines, even flat lighting with no shadows, reflections or "
    "highlights, its near-white plates, its crisp moulded panel lines, small rivets and vents, and its darker grey for "
    "recesses and vent slots. Follow the helmet's own moulded plates and seams as the render shows them. Round the face "
    "opening there is only a narrow slightly darker grey trim band, no wider than a finger. Anything seen inside the "
    "helmet through the opening is plain flat dark grey. Paint the back "
    "and sides as carefully as the front. No logos, emblems, letters, numbers or text, no colours other than greys. "
    "The background stays the same flat grey. ")
MATCH = ("The third image is the same helmet already repainted from the front: paint this view to match it "
         "exactly, the same plates, trim, greys and style, continuing them round onto this side. ")


def one(view, front, n):
    out = os.path.join(HERE, '1_%s_%d.png' % (view, n))
    if os.path.exists(out):
        return
    images = [os.path.join(HERE, '0_render_%s.png' % view), os.path.join(HERE, 'style_recon.png')]
    if front:
        images.append(front)
    t = meshy.call('POST', '/image-to-image', {
        'ai_model': MODEL, 'prompt': COMMON.format(view=VIEWS[view]) + (MATCH if front else ''),
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
    front = sys.argv[3] if len(sys.argv) > 3 else None
    with concurrent.futures.ThreadPoolExecutor(4) as pool:
        list(pool.map(lambda n: one(view, front, n), range(count)))
