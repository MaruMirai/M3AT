"""The texture prompts of the garments in Models/garments with no image, each worked in generationAssets/textures/<file>/:
1_input.glb, 2_textured.glb, 3_placed.glb, 3_view_*.png.

    python3 anim_scripts/generation/textures/prompts.py <file> [<file> ...]       the uploads, retextures and placing (10 credits each)

All of this apparel takes its colour in the game (stuff or a colour generator), so the image is multiplied by that colour:
neutral() takes out any colour Meshy painted (a blue patch, a maroon lining) and lifts it so its middle is near white.
A garment already with a 2_textured.glb is not sent again.

A garment in OWN_UVS has UVs we made (make_duster.py's add_uvs ...), so it is sent with them and Meshy told to paint on
them (place_textured.py strip --keep-uvs, meshy.py texture --keep-uvs), rather than making its own scattered islands."""
import io
import json
import os
import struct
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

from PIL import Image

OWN_UVS = {'LabCoat'}
MIDDLE = 235   # of 255: the BasicShirt's image, of near white cloth, has its middle about here
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
BLENDER = '/home/t/Programs/blender-5.2.3-linux-x64/blender'
STYLE = ', soft hand-painted stylized cartoon texture, even lighting, neutral light grey colours, no logos, no prints, no text'
PROMPTS = {
    'BasicShirt': 'plain short-sleeved crew neck t-shirt, light neutral grey woven cotton fabric with a clearly visible coarse cloth weave and fibre grain all over every panel, a ribbed collar band, side seams, stitched sleeve hems and bottom hem, gentle fabric folds and creases at the sides and under the arms',
    'Pants': 'plain cotton trousers, light neutral grey woven fabric, a waistband with belt loops and a fly seam at the front, side seams, stitched cuffs, gentle folds at the knees',
    'CollarShirt': 'button-down shirt, light neutral grey cotton, a row of small buttons and a placket down the front centre, a folded collar, stitched cuffs and hem, gentle fabric folds',
    'Jacket': 'casual zip jacket, light neutral grey canvas, a zip down the front centre, a collar, two front pockets, stitched cuffs and hem, gentle fabric folds',
    'FlakJacket': 'military flak jacket, light neutral grey tough ballistic nylon, quilted armour panels sewn across the chest and back, a zip and straps down the front, a collar, stitched cuffs and hem',
    'FlakPants': 'military flak trousers, light neutral grey tough ballistic nylon, quilted armour panels sewn over the front of the thighs, padded knee pads on the front of the knees only, plain smooth backs of the legs, no pockets, no pouches, a waistband with belt loops, stitched seams and cuffs',
    'FlakVest': 'military flak vest, light neutral grey ballistic nylon, armour plate pockets on the chest and back, straps at the sides, stitched edge binding',
    'CE_CompositeVest': 'plate carrier armour vest, light neutral grey nylon cover, large rigid ceramic plates in the chest and back, webbing straps, stitched edge binding',
    'PsyfocusVest': 'thin futuristic vest of light neutral grey fine eltex fabric, a faint woven pattern of thin circuit-like lines, clean stitched edge piping',
    'VestRoyal': 'formal waistcoat, light neutral grey brocade with a subtle embroidered floral damask pattern, a row of small buttons down the front centre, fine edge piping',
    'Corset': 'embroidered corset, light neutral grey satin with vertical boning seams, lacing down the front centre, an embroidered floral pattern, fine edge trim',
    'Sash': 'long ceremonial sash worn over one shoulder, light neutral grey woven silk, a slightly darker embroidered stripe along both edges, gentle folds',
    'KidRomper': "child's one-piece romper, light neutral grey soft cotton, a row of small snap buttons down the front centre, ribbed collar and cuffs, stitched hems, gentle folds",
    'LabCoat': 'knee-length open-front laboratory coat, light neutral grey smooth cotton twill, notched lapels, a breast pocket and two hip pockets with stitched edges, a back vent seam, stitched cuffs and hem, gentle vertical folds',
    'Duster': 'long open-front duster coat, light neutral grey worn canvas, stitched lapels, a back vent seam, stitched cuffs and hem, long gentle vertical folds',
    'Cape': 'long royal cape, light neutral grey heavy velvet with a soft sheen, a high stitched collar with a round clasp at the throat, a slightly darker embroidered border along the front edges and hem, long gentle vertical folds',
    'Parka':'puffy quilted winter parka, light neutral grey nylon, horizontal stitched baffle seams, a zip down the front centre, a stand collar, ribbed cuffs',
    'TribalA': 'one-shouldered tribal garment of light neutral grey hide and woven cloth, rough stitching with leather lacing, a simple woven band pattern, a jagged fringed skirt hem',
    'CE_Backpack': 'plain rugged backpack, light neutral grey canvas, a large front pocket with a zip, stitched seams, webbing straps, buckles',
    'CE_TribalBackpack': 'primitive pack of light neutral grey hide, rough stitching with leather lacing, tied leather thongs, a rolled flap, worn creases',
    'simpleHelmet': 'simple army helmet of light grey painted steel, a slightly darker rim band, a few rivets, small dents',
    'advancedHelmet': 'modern combat flak helmet, light grey ballistic composite, a slightly darker rim band, moulded panel lines, small rivets',
    'ArmorHelmetRecon': 'sleek futuristic recon armour helmet, light grey hard composite shell, moulded armour panel lines and seams, small vents at the sides, a slightly darker trim band round the face opening',
    'VacsuitHelmet': 'sci-fi vacuum suit space helmet, light grey hard composite shell, moulded panel lines and seams, small air vents and a valve fitting at the sides, a slightly darker rubber seal ring round the neck and a slightly darker trim band round the face opening',
    'Vacsuit': 'sci-fi vacuum space suit covering the whole body with gloves and boots, light grey padded fabric with moulded hard panels on the chest, shoulders and knees, seams and stitched panel lines, ribbed bellows joints at the elbows and knees, slightly darker rubber gloves and boot soles, a seal ring at the neck',
    'cowboyHat': 'cowboy hat of light grey felt, a slightly darker woven band round the crown, a stitched brim edge',
    'bowlerHat': 'bowler hat of light grey felt, a slightly darker ribbon band round the crown, a bound brim edge',
    'tuque': 'knitted winter tuque, light neutral grey wool, a chunky ribbed knit pattern, a folded ribbed cuff round the bottom',
    'hatHood': 'cloth hood with a cowl, light neutral grey rough woven wool, stitched seam over the crown, a hemmed face opening, soft folds round the neck',
    'tribalHeaddress': 'tribal headdress, light neutral grey woven band and leather, feathers, carved bead and thong decorations',
    'psychicFoilHelmet': 'thin helmet of light silver-grey crinkled metal foil, sheets folded and pressed with sharp creases, a neatly folded rim',
    'hatLadies': "lady's wide-brimmed hat of light grey felt, a slightly darker silk ribbon band round the crown tied in a small bow, a stitched brim edge",
    'hatTop': 'tall top hat of light grey silk plush with a soft sheen, a slightly darker ribbon band round the base of the crown, a bound brim edge',
    'beret': 'soft beret of light neutral grey wool felt, a fine felted texture, a stitched leather-bound band round the bottom, a tiny stalk on top',
    'Robe': 'long flowing robe, light neutral grey fine woven linen, a V neckline with a slightly darker embroidered trim, a slightly darker embroidered border along the hem and cuffs, long gentle vertical folds',
    'RobeRoyal': 'long regal ceremonial robe, light neutral grey rich velvet with a subtle embroidered damask brocade pattern, a thick soft white fur trim round the shoulders like a short mantle, a wide fur trim down the front edges of the V neckline and round the hem and cuffs, long heavy vertical folds',
    'warMask': 'carved wooden tribal war mask, light neutral grey wood grain, carved grooves and fierce painted lines round the eye holes',
    'warVeil': 'light neutral grey fine cloth veil, a soft woven texture, gentle vertical folds, a hemmed edge, a thin stitched band round the head',
    'clothMask': 'simple surgical cloth face mask, light neutral grey cotton, horizontal pleats, a stitched edge, thin elastic straps',
}


def neutral(path):
    """The glb's image in greys, its median brought to MIDDLE (the darks scaled with it, so the detail stays in shade)."""
    with open(path, 'rb') as f:
        data = f.read()
    n = struct.unpack('<I', data[12:16])[0]
    j = json.loads(data[20:20 + n])
    binary = data[20 + n + 8:]
    views = j['bufferViews']
    image = j['images'][0]['bufferView']
    v = views[image]
    picture = Image.open(io.BytesIO(binary[v.get('byteOffset', 0):v.get('byteOffset', 0) + v['byteLength']])).convert('L')
    shades = sorted(picture.resize((256, 256)).tobytes())
    gain = MIDDLE / max(shades[len(shades) // 2], 1)
    out = io.BytesIO()
    picture.point(lambda x: min(255, round(x * gain))).convert('RGB').save(out, 'JPEG', quality=92)
    parts, at = [], 0
    for i, view in enumerate(views):
        start = view.get('byteOffset', 0)
        piece = out.getvalue() if i == image else binary[start:start + view['byteLength']]
        view['byteOffset'], view['byteLength'] = at, len(piece)
        piece += b'\0' * (-len(piece) % 4)
        parts.append(piece)
        at += len(piece)
    j['buffers'][0]['byteLength'] = at
    text = json.dumps(j, separators=(',', ':')).encode()
    text += b' ' * (-len(text) % 4)
    with open(path, 'wb') as f:
        f.write(b'glTF' + struct.pack('<II', 2, 28 + len(text) + at) + struct.pack('<I', len(text)) + b'JSON' + text
                + struct.pack('<I', at) + b'BIN\0' + b''.join(parts))
    return 'NEUTRAL %s: median brought up by %.2f' % (os.path.basename(os.path.dirname(path)), gain)


def run(name):
    work = os.path.join(ROOT, 'generationAssets/textures', name)
    os.makedirs(work, exist_ok=True)
    src = os.path.join(ROOT, 'Models/garments', name + '.glb')
    keep = os.path.join(work, '0_original.glb')
    if not os.path.exists(keep):
        subprocess.run(['cp', src, keep], check=True)
    def blender(*args):
        r = subprocess.run([BLENDER, '-b', '--python-exit-code', '1', '--python', os.path.join(ROOT, 'anim_scripts/place_textured.py'), '--', *args],
                           capture_output=True, text=True)
        lines = [l for l in r.stdout.splitlines() if l.startswith(('STRIPPED', 'PLACED')) or 'rror' in l or l.startswith('TWINS')]
        if r.returncode:
            raise RuntimeError(name + ': ' + (r.stdout[-1500:] + r.stderr[-500:]))
        return lines
    inp, tex, placed = (os.path.join(work, f) for f in ('1_input.glb', '2_textured.glb', '3_placed.glb'))
    own = ['--keep-uvs'] if name in OWN_UVS else []
    log = blender('strip', keep, inp, *own)
    if not os.path.exists(tex):
        r = subprocess.run([sys.executable, os.path.join(ROOT, 'anim_scripts/meshy.py'), 'texture', inp, tex, '-', PROMPTS[name] + STYLE, *own], capture_output=True, text=True)
        if r.returncode:
            raise RuntimeError(name + ': ' + r.stdout[-400:] + r.stderr[-400:])
    # keep's name, so the placed garment takes the right capital and KEEP_WEIGHTS
    named = os.path.join(work, name + '.glb')
    subprocess.run(['cp', keep, named], check=True)
    log += blender('place', named, tex, placed)
    os.remove(named)
    log.append(neutral(placed))
    blender('view', placed, os.path.join(work, '3_view'))
    return log


if __name__ == '__main__':
    names = sys.argv[1:]
    with ThreadPoolExecutor(6) as pool:
        for future, name in [(pool.submit(run, n), n) for n in names]:
            try:
                print(name, *future.result(), sep='\n  ', flush=True)
            except Exception as e:
                print('FAILED', e, flush=True)
