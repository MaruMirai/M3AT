"""Fist error in drawn cells (sprite drawSize), today's fallback (the kind's hold) against the kind's hold slid by the
detected grip's difference from its reference sprite's."""
import os, json, numpy as np
from detect import detect, category, trusted
REF = {'rifle': 'Gun_AssaultRifle', 'pistol': 'Gun_Autopistol', 'melee': 'MeleeWeapon_Knife', 'bow': 'Bow_Recurve'}
T = {e['name']: e for e in json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'truth.json')))}
rows = []
for n, e in T.items():
    c = category(e)
    if not e['own'] or c is None or n in REF.values() or n == 'Pila': continue
    k = e['dkind'] if e['dkind'] in REF else {'gun': 'rifle', 'pistol': 'pistol', 'melee': 'melee', 'melee2': 'melee', 'bow': 'bow'}[c]
    r, ref = detect(e['path'], c, e['offset']), T[REF[k]]
    ok = trusted(r, c)
    rr = detect(ref['path'], category(ref), ref['offset'])
    sz, rs = np.array(e['size']), np.array(ref['size'])
    truth = np.array([e['u'], e['v']]) * sz
    kindfist = np.array([e['ku'], e['kv']]) * sz
    # the kind's fist on the ref sprite, relative to the ref's grip, in cells; put on this one's grip
    pred = np.array([r['u'], r['v']]) * sz + (np.array([ref['u'], ref['v']]) - np.array([rr['u'], rr['v']])) * rs
    if not ok: pred = kindfist
    rows.append((n, c, r['how'] if ok else 'kind', np.linalg.norm(kindfist - truth), np.linalg.norm(pred - truth)))
print('%-28s %-6s %-6s %8s %8s' % ('weapon', 'cat', 'how', 'now', 'detect'))
for x in sorted(rows, key=lambda x: x[1]): print('%-28s %-6s %-6s %8.3f %8.3f %s' % (x + ('better' if x[4] < x[3] - 0.02 else 'WORSE' if x[4] > x[3] + 0.02 else '',)))
a = np.array([x[3:] for x in rows])
own = np.array([x[3] > 1e-4 for x in rows])
print('holds that differ from their kind only (%d): median now %.3f detect %.3f' % (own.sum(), np.median(a[own, 0]), np.median(a[own, 1])))
print('median cells: now %.3f detect %.3f; within 0.05 cells: now %d detect %d of %d' % (np.median(a[:, 0]), np.median(a[:, 1]), (a[:, 0] <= .05).sum(), (a[:, 1] <= .05).sum(), len(a)))
