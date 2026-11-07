"""Paints the cataphract armour's renders as views for Meshy's multitexture, in the style of the recon armour's
references, through Meshy's image-to-image (Gemini's key being out of credit).

    python3 gen.py <view> <count> [front .png]

Sends 0_render_<view>.png (render.py, the cataphract in the power armour's old paint), the recon's painted reference of
that view (style_recon_<view>.png) and, for the side and back, the front picked; writes 1_<view>_<n>.png. Keeps what is
already there.
"""
import concurrent.futures, os, sys, time, urllib.request
# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'ArmorCataphract', 'gemini'))
sys.path.insert(0, os.path.join(HERE, '..', '..', '..', '..', 'anim_scripts'))
import meshy

MODEL = 'nano-banana-2'
VIEWS = {
    'front': 'seen straight on from the front',
    'side': 'seen exactly from the side, facing left',
    'back': 'seen straight on from behind',
}
COMMON = (
    "The first image is an orthographic render of a 3D model of heavy sci-fi cataphract armour (no helmet, no person "
    "inside), {view}, on a flat grey background, still wearing an older model's paint. Repaint its surface as a clean "
    "texture reference for retexturing this exact model. Keep the silhouette, outline, shape, framing, viewpoint and size "
    "exactly as in the first image, pixel for pixel: change only the paint. Ignore the first image's paint, emblems and "
    "text. The second image is a lighter recon armour of the same family, painted: copy its style exactly, the soft "
    "hand-painted flat cartoon look with dark ink outlines, even flat lighting with no shadows, reflections or "
    "highlights, its greys, its crisp moulded panel lines, rivets and vents, its dark grey ribbed suit at the joints and "
    "its belt with pouches. The cataphract is the heaviest armour of the family, heavier than power armour: thick "
    "overlapping near-white plates cover nearly all of it, a massive layered chest and back plate, a segmented armoured "
    "belly, big layered shoulder pauldrons, heavy armoured forearms and gauntlets, thick plated thighs, large knee "
    "guards, heavy shin plates and big armoured boots, the dark ribbed suit showing only in narrow strips at the elbows, "
    "the backs of the knees, the armpits and the crotch. Paint the back and sides as carefully as the front. No logos, "
    "emblems, letters, numbers or text, no colours other than greys. The background stays the same flat grey. ")
MATCH = ("The third image is the same cataphract armour already repainted from the front: paint this view to match it "
         "exactly, the same plates, greys and style, continuing them round onto this side. ")


def one(view, front, n):
    out = os.path.join(HERE, '1_%s_%d.png' % (view, n))
    if os.path.exists(out):
        return
    images = [os.path.join(HERE, '0_render_%s.png' % view), os.path.join(HERE, 'style_recon_%s.png' % view)]
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
