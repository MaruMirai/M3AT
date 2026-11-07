"""Edits one painted reference through Meshy's image-to-image, the painting alone sent (a render beside it pulls its
paint in).

    python3 edit.py <in.png> <out prefix> <count> <what to change>
"""
import concurrent.futures, os, sys, time, urllib.request
# Its work (inputs and outputs) stays in generationAssets, which is kept out of git.
HERE = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', '..', 'generationAssets', 'textures', 'ArmorHelmetCataphract', 'gemini'))
sys.path.insert(0, os.path.join(HERE, '..', '..', '..', '..', 'anim_scripts'))
import meshy

ONLY = ("The image is a painted texture reference of a sci-fi armour helmet. Edit it, keeping everything else exactly "
        "as it is: the same silhouette, outline, framing, size, viewpoint, greys and the same soft hand-painted flat "
        "cartoon style with dark ink outlines. Change only this: ")
src, prefix, count, what = sys.argv[1:5]


def one(n):
    out = os.path.join(HERE, '%s_%d.png' % (prefix, n))
    if os.path.exists(out):
        return
    t = meshy.call('POST', '/image-to-image', {'ai_model': 'nano-banana-2', 'prompt': ONLY + what,
                                              'reference_image_urls': [meshy.data_uri(src)]})
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


with concurrent.futures.ThreadPoolExecutor(4) as pool:
    list(pool.map(one, range(int(count))))
