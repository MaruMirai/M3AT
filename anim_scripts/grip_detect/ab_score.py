"""Compares two weapons_ autotest reports (grips on, grips off: M3AT_NO_AUTOGRIP=1): how far the fist sits on each
sprite (cells of its drawn size) from the ideal, the detected grip plus the kind's own fist-to-grip offset on its
reference sprite. python3 ab_score.py <on report.md> <off report.md> <mod folder> [<mod folder> ...]"""
import sys, os, re, json
import numpy as np
from detect import detect, category, trusted
import moddefs
HERE = os.path.dirname(os.path.abspath(__file__))
T = {e['name']: e for e in json.load(open(os.path.join(HERE, 'truth.json')))}
REF = {'gun': 'Gun_AssaultRifle', 'melee': 'MeleeWeapon_Knife', 'melee2': 'MeleeWeapon_Knife'}

def fists(report):
    out = {}
    for line in open(report):
        m = re.match(r'- (\S+): slab .*fist on the sprite at \(([-\d.]+), ([-\d.]+)\)', line)
        if m:
            out[m.group(1)] = np.array([float(m.group(2)), float(m.group(3))])
    return out

on, off = fists(sys.argv[1]), fists(sys.argv[2])
defs = {}
for f in sys.argv[3:]:
    defs.update(moddefs.load(f))
for n, e in T.items():
    defs.setdefault(n, dict(path=e['path'], size=e['size'], offset=e['offset'], cat=category(e) or 'gun'))
print('%-28s %-6s %8s %8s' % ('weapon', 'how', 'on', 'off'))
for n in sorted(set(on) & set(off)):
    e = defs.get(n)
    if e is None or e['cat'] not in REF:
        continue
    r = detect(e['path'], e['cat'], e['offset'])
    ref = T[REF[e['cat']]]
    rr = detect(ref['path'], category(ref), ref['offset'])
    if not trusted(r, e['cat']):
        print('%-28s %-6s %8s %8s  (no grip: both the kind hold; differ by %.3f)' % (n, r['how'], '-', '-', np.linalg.norm((on[n] - off[n]) * e['size'])))
        continue
    size = np.array(e['size'])
    ideal = np.array([r['u'], r['v']]) * size + (np.array([ref['u'], ref['v']]) - np.array([rr['u'], rr['v']])) * np.array(ref['size'])
    d = lambda f: np.linalg.norm(f * size - ideal)
    print('%-28s %-6s %8.3f %8.3f' % (n, r['how'], d(on[n]), d(off[n])))
