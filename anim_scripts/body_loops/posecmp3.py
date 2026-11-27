import numpy as np, zr, sys
o = np.load('pose_old.npz'); n = np.load('pose_new.npz'); bones = list(o['bones'])
def mesh(d, pose):
    V = d[pose + '_BV']; T = d[pose + '_BT']; H = d[pose + '_HV']; HT = d[pose + '_HT'] + len(V)
    return np.r_[V, H], np.r_[T, HT]
def shot(pose, joint, view, yaw, pitch, half, px=None, edges=False):
    ims = []
    for d in (o, n):
        V, T = mesh(d, pose)
        J = d[pose + '_J'][bones.index(joint)]
        p = zr.rot(view, yaw, pitch) @ J
        box = (p[0] - half, p[0] + half, p[1] - half, p[1] + half)
        ims.append(zr.render(V, T, view, box=box, width=420, yaw=yaw, pitch=pitch, edges=edges))
    return ims
SHOTS = [('elbow', 'forearm.L', 'front', -30, 10, 2.2), ('elbow', 'forearm.L', 'top', 0, 0, 2.2),
         ('armup', 'upper_arm.L', 'front', -20, 0, 2.6), ('armup', 'upper_arm.L', 'back', 20, 0, 2.6),
         ('armfwd', 'upper_arm.L', 'side', 30, 10, 3), ('armfwd', 'upper_arm.L', 'top', 0, 0, 3),
         ('sit', 'shin.L', 'side', 25, 0, 3.5), ('sit', 'thigh.L', 'front', -20, 20, 3.5),
         ('kneel', 'shin.L', 'side', 25, 0, 2.5), ('kneel', 'shin.L', 'back', 25, 0, 2.5),
         ('fist', 'hand.L.001', 'front', -20, 30, 1.3), ('fist', 'hand.L.001', 'under', 0, 0, 1.3), ('fist', 'hand.L.001', 'side', 0, 0, 1.3),
         ('wrist', 'hand.L', 'front', -20, 10, 1.8), ('wrist', 'hand.L', 'top', 0, 0, 1.8),
         ('bend', 'spine.003', 'side', 25, 0, 4.5), ('foot', 'foot.L', 'side', 25, 0, 2.2),
         ('rest', 'forearm.L', 'front', 0, 0, 2.5), ('rest', 'shoulder.L', 'front', 0, 0, 2.5)]
for pose in sorted(set(s[0] for s in SHOTS)):
    ims = []
    for s in SHOTS:
        if s[0] == pose: ims += shot(*s)
    zr.row(ims).save('pj_%s.png' % pose)
