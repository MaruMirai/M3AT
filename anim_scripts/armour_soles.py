"""Thick armoured soles: a garment's boots stretched down below a height, so it stands on them.

    python3 anim_scripts/armour_soles.py <in.glb> <out.glb> <stretch> [top]

Every vertex below top (Blender world units, the rig's: 4.0, about the bottom of the shin) is moved down by stretch times
how far it is below top, so the boots grow downward from there and their soles go under the body's feet; nothing above
top moves. The normals are turned to match. The .glb is edited in place of its positions and normals only, so its
texture, UVs, weights and keys are kept as they are (a skinned .glb's positions are at the rig's rest, an unskinned
one's under its node's transform; both are handled). This is Maru's hand edit of the cataphract armour
(Models/unused/armorsoles.blend: stretch 0.3496 below 4.002), made repeatable for the other armour.

The exporter measures how far a garment with GARMENT_PROPS' 'sole' reaches below the body's soles and writes it as a
sole line, and the game lifts the wearer by that much on its feet (SkinnedFigure.SoleOf).
"""
import json
import struct
import sys

import numpy as np

TOP = 4.002   # Blender world z (glTF world y) the stretch starts below


def read(path):
    data = open(path, 'rb').read()
    length = struct.unpack('<I', data[12:16])[0]
    gltf = json.loads(data[20:20 + length])
    at = 20 + length
    bin_length = struct.unpack('<I', data[at:at + 4])[0]
    return gltf, bytearray(data[at + 8:at + 8 + bin_length])


def write(path, gltf, binary):
    text = json.dumps(gltf, separators=(',', ':')).encode()
    text += b' ' * (-len(text) % 4)
    binary = bytes(binary) + b'\0' * (-len(binary) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    with open(path, 'wb') as f:
        f.write(struct.pack('<III', 0x46546C67, 2, total))
        f.write(struct.pack('<II', len(text), 0x4E4F534A) + text)
        f.write(struct.pack('<II', len(binary), 0x004E4942) + binary)


def view(gltf, binary, index):
    """The accessor's floats as a writable (count, 3) array over the buffer."""
    a = gltf['accessors'][index]
    assert a['componentType'] == 5126 and a['type'] == 'VEC3'
    bv = gltf['bufferViews'][a['bufferView']]
    assert bv.get('byteStride', 12) == 12
    offset = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    return np.frombuffer(binary, np.float32, a['count'] * 3, offset).reshape(a['count'], 3)


def node_matrix(node):
    if 'matrix' in node:
        return np.array(node['matrix']).reshape(4, 4).T
    t = np.eye(4)
    t[:3, 3] = node.get('translation', [0, 0, 0])
    x, y, z, w = node.get('rotation', [0, 0, 0, 1])
    r = np.eye(4)
    r[:3, :3] = [[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                 [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                 [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]]
    return t @ r @ np.diag(list(node.get('scale', [1, 1, 1])) + [1])


def world_of(gltf, index):
    parent = {c: i for i, n in enumerate(gltf['nodes']) for c in n.get('children', [])}
    m = node_matrix(gltf['nodes'][index])
    while index in parent:
        index = parent[index]
        m = node_matrix(gltf['nodes'][index]) @ m
    return m


def main():
    src, out, stretch = sys.argv[1], sys.argv[2], float(sys.argv[3])
    top = float(sys.argv[4]) if len(sys.argv) > 4 else TOP
    gltf, binary = read(src)
    done = set()
    for ni, node in enumerate(gltf['nodes']):
        if 'mesh' not in node:
            continue
        # A skinned mesh's positions are at its bind pose, which these files have at the rig's rest in world space; an
        # unskinned one's are under its node.
        m = np.eye(4) if 'skin' in node else world_of(gltf, ni)
        inv = np.linalg.inv(m)
        for prim in gltf['meshes'][node['mesh']]['primitives']:
            pi = prim['attributes']['POSITION']
            if pi in done:
                continue
            done.add(pi)
            pos = view(gltf, binary, pi)
            world = pos @ m[:3, :3].T + m[:3, 3]
            below = world[:, 1] < top
            world[below, 1] = top - (1.0 + stretch) * (top - world[below, 1])
            pos[:] = (world - m[:3, 3]) @ inv[:3, :3].T
            a = gltf['accessors'][pi]
            a['min'] = [float(x) for x in pos.min(0)]
            a['max'] = [float(x) for x in pos.max(0)]
            if 'NORMAL' in prim['attributes']:
                # The stretch is (1, 1 + stretch, 1) in the world below top, so a normal goes by its inverse transpose.
                nrm = view(gltf, binary, prim['attributes']['NORMAL'])
                wn = nrm @ inv[:3, :3]   # (to world: by the inverse transpose of m)
                wn[below, 1] /= 1.0 + stretch
                ln = wn @ m[:3, :3]
                ln /= np.linalg.norm(ln, axis=1, keepdims=True)
                nrm[:] = ln
            print('SOLES %s: %d of %d vertices stretched %.3f below %.3f, lowest now %.3f' % (
                src, int(below.sum()), len(pos), stretch, top, world[:, 1].min()))
    write(out, gltf, binary)


if __name__ == '__main__':
    main()
