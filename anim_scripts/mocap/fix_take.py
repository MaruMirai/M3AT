"""Mends a tracker take (record.py's CSV) whose tracking went wrong in places, writing a new CSV for import_take.py.
   python3 anim_scripts/mocap/fix_take.py <in.csv> <out.csv> [steps ...]
Devices are named by serial (the header's device lines), as record.py's part names are only its guesses. Steps, applied
in this order:
  --rejoin <serial> <seconds>   the device jumped there and stayed off (SteamVR re-placing it wrongly): everything from
                                the first sample at or after it is moved back so it carries on from the samples before
  --lose <serial> <from> <to>   marks the device lost over that stretch (to may be 'end'), so import_take.py fills it
  --despike                     marks lost every body tracker sample further than SPIKE_REACH from the running median
                                of SPIKE_WINDOW samples about it (a jump and back), and every sample of either wand where
                                the two are further than WAND_SLACK from their usual spacing (they are taped to one
                                tool, so it can only be a glitch), each padded by SPIKE_PAD samples
  --stand-in <serial> <from serial> <chest serial> <hips serial>
                                replaces a device that was not worn (an elbow tracker left off) with a mirror of another
                                (the other elbow) across the body, riding the chest tracker: its place at rest in the
                                chest tracker's frame mirrored across the performer's middle (right from the hips tracker
                                to the chest's, which is forward with the hips tracker at the back of the belt), its turn
                                the chest tracker's. Only the way an elbow points is used from an elbow tracker, so one
                                held at the chest's side is a fair stand-in for an arm that hung or followed the tool.
The rest pose (for --stand-in) is the header's rest line."""
import sys
import numpy as np

sys.path.insert(0, __import__('os').path.dirname(__file__))
from take import read, rot_of

SPIKE_WINDOW = 29     # samples (about a quarter second at 120 a second)
SPIKE_REACH = 0.05    # metres
WAND_SLACK = 0.03     # metres
SPIKE_PAD = 2         # samples either side of a spike also dropped


def running_median(x, w):
    h = w // 2
    xp = np.pad(x, h, mode='edge')
    return np.array([np.median(xp[i:i + w]) for i in range(len(x))])


def pad(mask, n):
    out = mask.copy()
    for k in range(1, n + 1):
        out[k:] |= mask[:-k]
        out[:-k] |= mask[k:]
    return out


def quat_of(R):
    """A unit quaternion (w, x, y, z) of a 3x3 rotation."""
    w = np.sqrt(max(0.0, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    x = np.copysign(np.sqrt(max(0.0, 1 + R[0, 0] - R[1, 1] - R[2, 2])) / 2, R[2, 1] - R[1, 2])
    y = np.copysign(np.sqrt(max(0.0, 1 - R[0, 0] + R[1, 1] - R[2, 2])) / 2, R[0, 2] - R[2, 0])
    z = np.copysign(np.sqrt(max(0.0, 1 - R[0, 0] - R[1, 1] + R[2, 2])) / 2, R[1, 0] - R[0, 1])
    return np.array([w, x, y, z])


def main():
    args = sys.argv[1:]
    src, dst, steps = args[0], args[1], args[2:]
    header, devices = read(src)
    lines = [l for l in open(src) if l.startswith('#')]
    names = {s: part for s, (kind, part) in header['devices'].items()}
    kinds = {s: kind for s, (kind, part) in header['devices'].items()}
    t = next(iter(devices.values()))[0]
    rest = [float(x) for x in header['rest'].split()]
    at_rest = (t >= rest[0]) & (t <= rest[1])
    i = 0
    while i < len(steps):
        step = steps[i]
        if step == '--rejoin':
            s, when = steps[i + 1], float(steps[i + 2])
            _, p, q, ok = devices[s]
            j = int(np.searchsorted(t, when))
            good = [k for k in range(j - 1, max(j - 40, 0), -1) if ok[k]][:3]
            k = good[0]
            v = (p[good[0]] - p[good[-1]]) / max(good[0] - good[-1], 1)   # per sample, before the jump
            after = next(m for m in range(j, len(t)) if ok[m])
            shift = p[k] + v * (after - k) - p[after]
            p[j:] += shift * ok[j:, None]
            print('FIX %s rejoined from %.3f s, moved %s m' % (s, t[after], np.round(shift, 3)))
            i += 3
        elif step == '--lose':
            s, a = steps[i + 1], float(steps[i + 2])
            b = t[-1] if steps[i + 3] == 'end' else float(steps[i + 3])
            ok = devices[s][3]
            ok[(t >= a) & (t <= b)] = False
            print('FIX %s lost %.2f .. %.2f s' % (s, a, b))
            i += 4
        elif step == '--despike':
            for s, (_, p, q, ok) in devices.items():
                if kinds[s] != 'tracker':
                    continue
                m = np.stack([running_median(p[:, k], SPIKE_WINDOW) for k in range(3)], 1)
                bad = pad((np.linalg.norm(p - m, axis=1) > SPIKE_REACH) & ok, SPIKE_PAD)
                ok &= ~bad
                print('FIX %s: %d samples of spikes dropped' % (s, bad.sum()))
            wands = [s for s in devices if kinds[s] == 'wand']
            if len(wands) == 2:
                (_, a, _, oa), (_, b, _, ob) = devices[wands[0]], devices[wands[1]]
                dist = np.linalg.norm(a - b, axis=1)
                usual = np.median(dist[oa & ob])
                bad = pad((abs(dist - usual) > WAND_SLACK) & oa & ob, SPIKE_PAD)
                oa &= ~bad
                ob &= ~bad
                print('FIX wands %.3f m apart: %d samples off it dropped' % (usual, bad.sum()))
            i += 1
        elif step == '--stand-in':
            s, other, chest, hips = steps[i + 1:i + 5]
            _, pc, qc, okc = devices[chest]
            _, pe, _, oke = devices[other]
            use = at_rest & okc & oke & devices[hips][3]
            fw = pc[use].mean(0) - devices[hips][1][use].mean(0)
            fw[1] = 0.0
            fw /= np.linalg.norm(fw)
            right = np.cross(fw, [0.0, 1.0, 0.0])
            o = pe[use].mean(0) - pc[use].mean(0)
            o -= 2 * np.dot(o, right) * right
            Rc = rot_of(qc[np.where(use)[0][len(np.where(use)[0]) // 2]])
            local = Rc.T @ o
            p = np.array([pc[k] + rot_of(qc[k]) @ local for k in range(len(t))])
            devices[s] = (t, p, qc.copy(), okc.copy())
            print('FIX %s stands in as %s mirrored on %s, %s m from it at rest' % (s, other, chest, np.round(o, 3)))
            i += 5
        else:
            sys.exit('fix_take: unknown step ' + step)
    with open(dst, 'w') as f:
        f.writelines(lines)
        f.write('# mended by fix_take.py from %s: %s\n' % (src.split('/')[-1], ' '.join(steps)))
        f.write('t,part,x,y,z,qw,qx,qy,qz,ok\n')
        for k in range(len(t)):
            for s, (_, p, q, ok) in devices.items():
                f.write('%.4f,%s,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%.5f,%d\n'
                        % (t[k], names[s], *p[k], *q[k], 1 if ok[k] else 0))


if __name__ == '__main__':
    main()
