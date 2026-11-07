"""gen.py's edit through Meshy's image-to-image (its Nano Banana models), for when the Gemini key is out of credit.

    python3 meshy_edit.py <model> <view> <in.png> <out prefix> <count> <what to change>

Sends in.png alone with an edit prompt; writes <out prefix>_<n>.png. Keeps what is already there.
"""
import concurrent.futures, json, os, sys, time, urllib.request
# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'ArmorRecon', 'gemini'))
sys.path.insert(0, os.path.join(HERE, '..', '..', '..', '..', 'anim_scripts'))
import meshy
from gen import VIEWS

# The render as a second image pulled the power armour's old paint (logos, photo shading) back in, so only the painting goes.
ONLY = ("The image is a painted texture reference of a sci-fi recon armour suit, {view}. Edit it, keeping everything else "
        "exactly as it is: the same silhouette, outline, framing, size, viewpoint, greys and the same soft hand-painted "
        "flat cartoon style with dark ink outlines. No logos, emblems, letters or text. Change only this: ")

model, view, src, prefix, count, what = sys.argv[1:7]

def one(n):
    out = os.path.join(HERE, '%s_%d.png' % (prefix, n))
    if os.path.exists(out):
        return
    t = meshy.call('POST', '/image-to-image', {
        'ai_model': model, 'prompt': ONLY.format(view=VIEWS[view]) + what,
        'reference_image_urls': [meshy.data_uri(src)]})
    task = t['result']
    while True:
        time.sleep(5)
        t = meshy.call('GET', '/image-to-image/' + task)
        if t['status'] in ('SUCCEEDED', 'FAILED', 'CANCELED'):
            break
    if t['status'] != 'SUCCEEDED':
        print('failed', out, t.get('task_error'), flush=True); return
    urllib.request.urlretrieve(t['image_urls'][0], out)
    print('made', os.path.basename(out), 'credits', t.get('consumed_credits'), flush=True)

with concurrent.futures.ThreadPoolExecutor(4) as pool:
    list(pool.map(one, range(int(count))))
