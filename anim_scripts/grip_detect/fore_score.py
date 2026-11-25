"""Scores detect.fore against the other hand on the game's two-handed guns (offtruth.json: each gun's [u, v] of the
other fist and [u, v] of the holding fist on its sprite, in its own drafted stance, from the weapons_ autotest's "grip
tool's drafted-stance fists"). A gun held by the rifle kind's stance has its other hand moved along it by its fore
grip's place against the assault rifle's (WalkFigures.ForeShift); without, it keeps the assault rifle's distance from
the holding fist. Error along the gun, pixels of 128."""
import json, os, numpy as np
from detect import detect_fore, N
here = os.path.dirname(os.path.abspath(__file__))
T = {e['name']: e for e in json.load(open(os.path.join(here, 'truth.json')))}
off = json.load(open(os.path.join(here, 'offtruth.json')))
REF = 'Gun_AssaultRifle'
def along(n):
    return detect_fore(T[n]['path'], T[n]['offset'])
r = along(REF)
now, det = [], []
print('%-24s %6s %6s %6s %s' % ('gun', 'truth', 'now', 'fore', 'how'))
for n, (ou, ov, mu, mv) in off.items():
    if n == REF or n == 'Gun_Minigun':   # the minigun is held by its side handle, not as a rifle
        continue
    f = along(n)
    keep = (mu + off[REF][0] - off[REF][2]) * N
    put = (f['u'] + off[REF][0] - r['u']) * N
    now.append(abs(keep - ou * N)); det.append(abs(put - ou * N))
    print('%-24s %6.0f %6.0f %6.0f %s' % (n, ou * N, keep, put, f['how']))
print('median px: kept %.1f, fore %.1f; worst kept %.1f, fore %.1f' % (np.median(now), np.median(det), max(now), max(det)))
