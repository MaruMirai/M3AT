"""Paints reference views of a new cataphract armour helmet for Meshy's image to 3D, from a sketch, in the style of
the power armour helmet's references, through Meshy's image-to-image.

    python3 gen.py <view> <count> [front .png]

Sends sketch.png (the front only) and style_power_<view>.png, and for the side and back the front picked; writes
1_<view>_<n>.png. Keeps what is already there.
"""
import concurrent.futures, os, sys, time, urllib.request
# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'ArmorHelmetCataphract', 'gemini'))
sys.path.insert(0, os.path.join(HERE, '..', '..', '..', '..', 'anim_scripts'))
import meshy

MODEL = 'nano-banana-2'
VIEWS = {
    'front': 'seen straight on from the front',
    'side': 'seen exactly from the side, facing left',
    'back': 'seen straight on from behind',
}
DESIGN = (
    "a heavy sci-fi cataphract armour helmet, the heaviest helmet of its armour family: fully enclosed, with no visor "
    "and no glass. A rounded armoured dome over the top, and a solid armoured faceplate over the whole face, split by "
    "a narrow seam, with one small round camera optic lens set in it a little off centre (a dark lens in a raised "
    "round housing) as the only opening, and a thick armoured jaw and chin guard below. Thick layered plates, chunky "
    "simple shapes, wide rather than tall, a little bigger than the power armour helmet. ")
STYLE = ("Draw it in exactly the style of the power armour helmet reference: the soft hand-painted flat cartoon look "
         "with dark ink outlines, even flat lighting with no shadows, reflections or highlights, near-white plates, "
         "crisp moulded panel lines, small rivets and vents, darker grey only for recesses, vents and the lens. "
         "Orthographic, no perspective, the helmet alone, nothing inside it, no head, no neck, no text, logos or "
         "colours other than greys, centred with a margin all round, on the same flat mid-grey background. ")
FRONT = ("The first image is a rough sketch of the design (the helmet in the middle; ignore the shoulders round it). "
         "The second image is the power armour helmet of the same family, {view}. Draw {design}It is {view}. " )
OTHER = ("The first image is the power armour helmet of the same family, {view}. The second image is the cataphract "
         "helmet already drawn from the front. Draw that same cataphract helmet, {view}: the same plates, seams, greys "
         "and style, continuing them round onto this side, and the same size and height in the frame as the power "
         "armour helmet. The helmet: {design}")


def one(view, front, n):
    out = os.path.join(HERE, '1_%s_%d.png' % (view, n))
    if os.path.exists(out):
        return
    if front:
        images = [os.path.join(HERE, 'style_power_%s.png' % view), front]
        text = OTHER.format(view=VIEWS[view], design=DESIGN) + STYLE
    else:
        images = [os.path.join(HERE, 'sketch.png'), os.path.join(HERE, 'style_power_%s.png' % view)]
        text = FRONT.format(view=VIEWS[view], design=DESIGN) + STYLE
    t = meshy.call('POST', '/image-to-image', {
        'ai_model': MODEL, 'prompt': text, 'reference_image_urls': [meshy.data_uri(p) for p in images]})
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
