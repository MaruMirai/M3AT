"""Reading a take (record.py's CSV) and naming its trackers: shared by record.py and import_take.py, numpy only, so
Blender's Python can use it too."""

import numpy as np

PARTS = ['head', 'chest', 'hips', 'elbow_L', 'elbow_R', 'knee_L', 'knee_R', 'foot_L', 'foot_R', 'hand_L', 'hand_R']


def rot_of(q):
    """The 3x3 rotation of a unit quaternion (w, x, y, z)."""
    w, x, y, z = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def facing(trackers, wands, wand_turns=None):
    """The way the performer faces across the ground (SteamVR space, unit, y 0), or None. Held in the rest pose the
    wands point forward give or take a side's turn out, so the two pointing ways added together are forward (a wand
    points along its own -z). Without both wands, from the hips tracker to the chest tracker, which only works with the
    hips tracker worn at the small of the back."""
    if wand_turns and len(wand_turns) == 2:
        way = sum(-rot_of(q)[:, 2] for q in wand_turns.values())
        way[1] = 0.0
        if np.linalg.norm(way) > 0.3:
            return way / np.linalg.norm(way)
    return None


def assign(trackers, wands, wand_turns=None):
    """Names each device from its mean rest-pose place ({serial: xyz}, SteamVR space), and the wands' turns then
    ({serial: wxyz}) for which way is forward. Returns ({serial: part}, a list of problems)."""
    problems = []
    parts = {}
    if len(trackers) != 7:
        problems.append('%d trackers tracked, not 7' % len(trackers))
    if len(wands) != 2:
        problems.append('%d wands tracked, not 2' % len(wands))
    if len(trackers) < 7:
        return parts, problems
    by_height = sorted(trackers, key=lambda s: trackers[s][1])
    head, feet, middle = by_height[-1], by_height[:2], by_height[2:-1]
    # The elbows or the knees: of the middle four, the pair furthest apart across the ground at much the same height
    # (arms down, elbows are either side of the body; knee trackers on the outside of the shins, either side of the
    # legs). Below the other two (the chest and hips) they are knees, else elbows.
    best, elbows = -1.0, None
    for i in range(len(middle)):
        for j in range(i + 1, len(middle)):
            a, b = trackers[middle[i]], trackers[middle[j]]
            if abs(a[1] - b[1]) > 0.15:
                continue
            d = np.hypot(a[0] - b[0], a[2] - b[2])
            if d > best:
                best, elbows = d, (middle[i], middle[j])
    if elbows is None:
        problems.append('no pair of trackers at elbow or knee height either side')
        return parts, problems
    rest = [s for s in middle if s not in elbows]
    chest, hips = sorted(rest, key=lambda s: -trackers[s][1])
    limb = 'knee' if max(trackers[s][1] for s in elbows) < trackers[hips][1] else 'elbow'
    forward = facing(trackers, wands, wand_turns)
    if forward is None:
        forward = trackers[chest] - trackers[hips]
        forward[1] = 0.0
        if np.linalg.norm(forward) < 0.05:
            problems.append('no way to tell forward: hold both wands pointing forward in the rest pose')
            return parts, problems
        forward /= np.linalg.norm(forward)
    right = np.cross(forward, [0.0, 1.0, 0.0])
    middle_at = trackers[head]

    def sides(pair, name, places):
        a, b = sorted(pair, key=lambda s: np.dot(places[s] - middle_at, right))
        parts[a], parts[b] = name + '_L', name + '_R'

    parts[head], parts[chest], parts[hips] = 'head', 'chest', 'hips'
    sides(elbows, limb, trackers)
    sides(feet, 'foot', trackers)
    if len(wands) == 2:
        sides(list(wands), 'hand', wands)
    return parts, problems


def read(path):
    """A take as (header, {device: (t, xyz, wxyz, ok)}), the arrays one row per sample, every device sampled at the
    same times. header has 'devices' ({serial: (kind, part)}), 'end' (seconds) and the other # lines as text."""
    header = {'devices': {}}
    rows = {}
    with open(path) as f:
        for line in f:
            if line.startswith('#'):
                words = line[1:].split()
                if words[0] == 'device':
                    header['devices'][words[1]] = (words[2], words[3])
                elif words[0] == 'end':
                    header['end'] = float(words[1])
                else:
                    header[words[0]] = ' '.join(words[1:])
                continue
            if line.startswith('t,'):
                continue
            v = line.rstrip().split(',')
            rows.setdefault(v[1], []).append([float(x) for x in v[:1] + v[2:]])
    out = {}
    for device, r in rows.items():
        a = np.array(r)
        out[device] = (a[:, 0], a[:, 1:4], a[:, 4:8], a[:, 8] > 0)
    # The parts by serial, from the header (record.py names them when it can).
    for serial, (kind, part) in header['devices'].items():
        if part != '?' and part in out:
            out[serial] = out.pop(part)
    return header, out
