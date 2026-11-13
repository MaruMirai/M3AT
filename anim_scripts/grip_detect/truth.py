"""blender -b Models/pawnHumanRig.blend --python anim_scripts/grip_detect/truth.py -- anim_scripts/grip_detect/truth.json
Blender: where each weapon's slab hold puts the fist on its sprite (u, v; v up), as preview.place_slab draws it."""
import sys, os, json, math
sys.path.insert(0, '/home/t/Programs/rimworld/M3AT/anim_scripts')
import walk, preview
from mathutils import Vector
W = walk.slab_numbers()['WEAPONS']
D = walk.DRAFTED
defs = preview.weapon_defs()
out = []
for name in sorted(set(list(defs.keys()))):
    if not (name.startswith('Gun_') or name.startswith('MeleeWeapon_') or name.startswith('Bow_') or name in W):
        continue
    kind = name if name in W else D.get(name, {}).get('kind')
    if kind is None:
        # by the preview's guess: melee by name, else rifle
        kind = 'melee' if name.startswith('MeleeWeapon_') else 'bow' if name.startswith('Bow_') else 'rifle'
    w = W.get(name) or W.get(D.get(name, {}).get('kind', ''), None) or W.get(kind)
    if w is None or 'line' not in w or w['line'] != 'hand':
        continue
    path, size, offset = preview.weapon_look(name)
    if not path or 'weapon_sprites' not in path:
        continue
    scale = walk.weapon_scale(name) / walk.GAME_SCALE
    dx, dy = size[0] * scale, size[1] * scale
    a = (Vector(w.get('aim', (0, -1, 0))).normalized() + Vector((0, 0, -w['drop']))).normalized()
    top = preview.top_of(a, w['roll'], Vector((0, -1, 0)))
    top = (top - a * top.dot(a)).normalized()
    o = math.radians(offset); c, s = math.cos(o), math.sin(o)
    x, y = (a * c - top * s) * dx, (a * s + top * c) * dy
    d = -Vector(w.get('at', (0, 0, 0))) - a * (w['behind'] * max(dx, dy))
    u, v = 0.5 + d.dot(x) / x.length_squared, 0.5 + d.dot(y) / y.length_squared
    # the weapon's line (a) in uv, for the detector's reference
    # the same with its kind's hold (what a weapon with no hold of its own gets now)
    kw = W[D.get(name, {}).get('kind', kind)] if D.get(name, {}).get('kind', kind) in W else w
    ka = (Vector(kw.get('aim', (0, -1, 0))).normalized() + Vector((0, 0, -kw['drop']))).normalized()
    kt = preview.top_of(ka, kw['roll'], Vector((0, -1, 0))); kt = (kt - ka * kt.dot(ka)).normalized()
    kx, ky = (ka * c - kt * s) * dx, (ka * s + kt * c) * dy
    kd = -Vector(kw.get('at', (0, 0, 0))) - ka * (kw['behind'] * max(dx, dy))
    ku, kv = 0.5 + kd.dot(kx) / kx.length_squared, 0.5 + kd.dot(ky) / ky.length_squared
    au, av = a.dot(x) / x.length_squared, a.dot(y) / y.length_squared
    out.append(dict(name=name, kind=kind, own=name in W, path=path, size=size, offset=offset, type=walk.weapon_type(name),
                    hand=w['hand'], u=u, v=v, ku=ku, kv=kv, dkind=D.get(name, {}).get('kind', kind), au=au, av=av))
json.dump(out, open(sys.argv[-1], 'w'), indent=1)
print('TRUTH', len(out))
