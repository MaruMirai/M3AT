"""Zeroes a garment's own copies of the body's shape keys, so the export fits those keys from the body instead.

    python3 anim_scripts/zero_body_keys.py <skin.txt> <garment.glb> ... [--keep Key,Key]

The make scripts write each body key into a garment as the body had it then; the export (write_garment) takes any key
a garment has that isn't all zero as a hand fix and uses it in place of fitting. Those copies went stale as the body's
keys changed, and one was broken (the sandals' Female, Male and Pregnant moved one sandal onto the other). Each morph
target named for a key in skin.txt is set to zero, in place in the .glb (glb_edit); any other (a pauldron's Big) and
those in --keep are left. Coats made by make_duster.py keep theirs: it builds them so the skirt's hem keeps its share
down the leg in each key, which fitting alone crumples.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from glb_edit import Prim, read_glb, write

argv = sys.argv[1:]
keep = set()
if '--keep' in argv:
    i = argv.index('--keep')
    keep = {k.strip().lower() for k in argv[i + 1].split(',')}
    del argv[i:i + 2]
skin, paths = argv[0], argv[1:]
body_keys = {line.split()[1].lower() for line in open(skin) if line.startswith('key ')}
for path in paths:
    j, binary = read_glb(path)
    done = []
    for mi, mesh in enumerate(j['meshes']):
        for prim in mesh['primitives']:
            pr = Prim(j, binary, mi, prim)
            for name, tg in zip(pr.names, pr.targets):
                if name and name.lower() in body_keys and name.lower() not in keep and np.abs(tg['POSITION']).max() > 0:
                    for a in tg.values():
                        a[:] = 0.0
                    done.append(name)
            if done:
                write(j, binary, pr, path)
    print('ZEROED %s: %s' % (os.path.basename(path), ', '.join(done) if done else 'nothing'))
