"""python3 repaint.py <count> <prefix> [model]: Meshy image-to-image repaint of the cataphract's back."""
import concurrent.futures, os, sys, time, urllib.request
sys.path.insert(0, '/home/t/Programs/rimworld/M3AT/anim_scripts')
import meshy
HERE = os.path.dirname(os.path.abspath(__file__))
count, prefix = int(sys.argv[1]), sys.argv[2]
model = sys.argv[3] if len(sys.argv) > 3 else 'nano-banana-2'
PROMPT = (
    "The first image is an orthographic render of a 3D model of heavy sci-fi cataphract armour seen straight on from "
    "behind, flat unlit texture colours on a flat grey background. Repaint its surface as a clean texture reference for "
    "this exact model. Keep the silhouette, outline, framing, viewpoint, size and every edge of the model exactly as in "
    "the first image, pixel for pixel: change only the paint inside it. Keep the belt with its pouches and the boots' "
    "heels where they are. The second image is the back of a lighter recon armour of the same family: copy only its "
    "drawing style, the soft hand-painted flat cartoon look, near-white plates, crisp thin moulded panel lines and even "
    "flat lighting with no shadows. The cataphract must look much heavier and more heavy-duty than the recon: it is the "
    "heaviest armour there is, like a walking tank. The third image is this same cataphract from the front, already "
    "painted: match its plates, greys and level of detail. Paint the back as thick overlapping near-white armour plates "
    "with bevelled edges, layered one over another like scales: a massive layered back plate in several overlapping "
    "slabs with a raised spine ridge down the middle, heavy bolts and rivets along the plate edges, armoured vent "
    "grilles and a reinforced collar; big layered pauldrons on the shoulders; heavy segmented plates over the buttocks "
    "and hips; thick plates on the backs of the thighs, calves and arms with reinforcing ribs and bolts; massive boot "
    "heels. Fix these faults of the first image: the black triangular wedges over the buttocks and the hips must become "
    "near-white armour plates with thin panel lines between them, not dark gaps; the large flat dark grey patches on "
    "the backs of the thighs must become near-white thigh plates. The dark ribbed suit shows only in narrow strips at "
    "the backs of the knees, the armpits and the waist under the belt. Plates near white, lines light grey. No logos, "
    "emblems, letters, numbers or text, no colours other than greys. The background stays the same flat grey.")

def one(n):
    out = os.path.join(HERE, '%s_%d.png' % (prefix, n))
    if os.path.exists(out):
        return
    images = [os.path.join(HERE, f) for f in ('flat_back.png', 'recon_flat_back.png', 'flat_front.png')]
    t = meshy.call('POST', '/image-to-image', {'ai_model': model, 'prompt': PROMPT,
        'reference_image_urls': [meshy.data_uri(p) for p in images]})
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
    list(pool.map(one, range(count)))
