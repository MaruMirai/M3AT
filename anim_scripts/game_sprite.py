"""Pulls the game's own sprites out of its packed assets as PNGs, to model from.

    python3 anim_scripts/game_sprite.py <Assets.zip> <part of a texture path> <out folder>
    e.g. python3 anim_scripts/game_sprite.py ~/.local/share/Steam/steamapps/common/RimWorld/Data/Royalty/Assets.zip \
             Apparel/EltexHelmet/ /tmp/eltex

Each expansion's textures are in Data/<pack>/Assets.zip, compressed with zstd (zip method 93, which Python's zipfile
can't read, so each entry is piped through the zstd command). An entry is a .dds.split: a DDS header (148 bytes, DX10)
over Waylith's split BC7 (Waylith.dll's Bc7StreamSplit, from Waylith.AssetBake's --split-blocks): a mode byte per block,
then each mode's endpoint bytes, then each mode's index bytes, the RGB endpoints stored as differences. This joins them
back into BC7 blocks (Bc7StreamSplit.TryJoin) and saves the top mip. Only BC7 is handled; BC1, BC4 and BC5 split their
fields differently (DdsFile.SplitFields) and are skipped with a note."""
import os, struct, subprocess, sys, zipfile
from PIL import Image

MASK = (1 << 64) - 1
ORDER = [9, 6, 10, 5, 1, 3, 4, 0, 2, 7, 8]       # the order the modes' records are laid out in
ENDPOINT_BYTES = [8, 8, 8, 8, 8, 9, 9, 8, 8, 6, 7]
INDEX_BYTES = [8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 4]
DXGI_BC7 = (98, 99)

def u64(b, o):
    return int.from_bytes(b[o:o + 8], 'little')

def unpad(p):
    return (p & 1) | ((p >> 1) & 0x7FFFFFFE)

def join_rgb(e, pos):
    b = e[2]
    g = (e[3] + b) & 0x7F
    v = [(e[0] + b) & 0x7F, (e[1] + g) & 0x7F, b, g, (e[4] + b) & 0x7F, (e[5] + g) & 0x7F]
    return sum(v[i] << (pos + 7 * i) for i in range(6))

def join(split, n):
    """The n BC7 blocks of a split stream."""
    modes = split[:n]
    per = [0] * 11
    for m in modes:
        per[m] += 1
    ends, idxs, at = [0] * 11, [0] * 11, n
    for m in ORDER:
        ends[m] = at
        at += per[m] * ENDPOINT_BYTES[m]
    for m in ORDER:
        idxs[m] = at
        at += per[m] * INDEX_BYTES[m]
    if at != len(split):
        raise ValueError('split stream is %d bytes, its layout %d' % (len(split), at))
    out = bytearray(16 * n)
    for j, m in enumerate(modes):
        e = split[ends[m]:ends[m] + ENDPOINT_BYTES[m]]
        x = split[idxs[m]:idxs[m] + INDEX_BYTES[m]]
        ends[m] += ENDPOINT_BYTES[m]
        idxs[m] += INDEX_BYTES[m]
        if m == 6:
            lo, hi = 0x40 | join_rgb(e, 7) | (e[6] << 49) | (e[7] << 56) | (e[8] << 63), u64(x, 0)
        elif m == 9:
            lo, hi = 0x40 | join_rgb(e, 7) | 0xFFFE000000000000, u64(x, 0)
        elif m == 5:
            lo = 0x20 | (e[0] << 6) | join_rgb(e[1:], 8) | (e[7] << 50) | (e[8] << 58)
            hi = (e[8] >> 6) | (unpad(int.from_bytes(x[0:4], 'little')) << 2) | (unpad(int.from_bytes(x[4:8], 'little')) << 33)
        elif m == 10:
            lo = 0x20 | join_rgb(e, 8) | 0x3FC000000000000 | (e[6] << 58)
            hi = (e[6] >> 6) | (unpad(int.from_bytes(x[0:4], 'little')) << 2)
        else:
            lo, hi = u64(e, 0), u64(x, 0)
        out[16 * j:16 * j + 8] = (lo & MASK).to_bytes(8, 'little')
        out[16 * j + 8:16 * j + 16] = (hi & MASK).to_bytes(8, 'little')
    return bytes(out)

def image(dds):
    """The top mip of a .dds.split as a PIL image, or None for a format this doesn't join."""
    h, w = struct.unpack('<II', dds[12:20])
    mips = max(1, struct.unpack('<I', dds[28:32])[0])
    if dds[84:88] != b'DX10' or struct.unpack('<I', dds[128:132])[0] not in DXGI_BC7:
        return None
    n, mw, mh = 0, w, h
    for _ in range(mips):
        n += max(1, (mw + 3) // 4) * max(1, (mh + 3) // 4)
        mw, mh = mw // 2, mh // 2
    blocks = join(dds[148:], n)
    return Image.frombytes('RGBA', (w, h), blocks[:((w + 3) // 4) * ((h + 3) // 4) * 16], 'bcn', 7)

def main():
    zip_path, part, out = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out, exist_ok=True)
    with open(zip_path, 'rb') as f:
        for i in zipfile.ZipFile(f).infolist():
            if part not in i.filename or not i.filename.endswith('.dds.split'):
                continue
            f.seek(i.header_offset)
            head = f.read(30)
            name_len, extra_len = struct.unpack('<HH', head[26:30])
            f.seek(i.header_offset + 30 + name_len + extra_len)
            data = f.read(i.compress_size)
            if i.compress_type == 93:
                data = subprocess.run(['zstd', '-d', '-c'], input=data, capture_output=True, check=True).stdout
            im = image(data)
            base = os.path.basename(i.filename)[:-len('.dds.split')]
            base = base[:-4] if base.endswith('.psd') else base
            if im is None:
                print('SKIP %s (not BC7)' % i.filename)
                continue
            path = os.path.join(out, base + '.png')
            im.save(path)
            print('%s %dx%d' % (path, im.size[0], im.size[1]))

if __name__ == '__main__':
    main()
