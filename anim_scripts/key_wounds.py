# Makes the wound sheet the game reads (Models/wounds.png in the mod's folder, Source/Wounds.cs) from a sheet painted on white:
#   python3 anim_scripts/key_wounds.py Models/injuries2.png "M3AT: Maru's 3D Animated Toons/Models/wounds.png"
# The white is taken out as GIMP's colour to alpha does (each pixel the least see-through colour that, laid over
# white, gives what was painted), so soft edges and glows fade out rather than leaving a white fringe. The faint
# noise left in the white (alpha under NOISE) is dropped, so the square round each wound doesn't show as a haze.
# The bandages (BANDAGES) are near white themselves, so colour to alpha would make them see-through: in their cells
# the background is instead the near-white (BACKGROUND) reached from the cell's edge, everything else kept solid as
# painted, its edge softened a pixel.
# The gunshots (GUNSHOTS) are painted with a wide spatter of specks, which reads as dirt at the size they are drawn: all
# but what lies close round the wound is faded out (trim_spatter). Two more (GUNSHOT_MADE, in the cells left blank) are
# made from them: mirrored, turned, stretched and warped (warped_cell), as their cells were never painted.
# The sheet is a grid of CELL px cells (Wounds.Columns by Wounds.Rows); each cell's border is cleared, so mipmaps
# don't bleed one wound into the next. Clear pixels take a dark blood colour (a bandage's, cloth), so filtering at
# the edges doesn't darken or whiten them.
# The sheet is painted at CELL px a cell and written at OUT_CELL: a wound is only some tens of pixels across on screen
# at the closest zoom, so the full size is a quarter of the memory for nothing. It is scaled down by area, weighted by
# alpha, so the clear pixels' colour doesn't seep into the edges.
import sys

import numpy as np
from PIL import Image, ImageFilter

CELL = 256   # px a cell, as painted
OUT_CELL = 128   # px a cell, as written
NOISE = 3 / 255
BORDER = 1   # px cleared round each cell, as written
CLEAR = (0.35, 0.03, 0.03)   # the colour of the clear pixels
BANDAGES = [(2, 9), (0, 10), (1, 10), (2, 10), (0, 11), (1, 11), (2, 11)]   # (column, row from the top), as Wounds.Cells
BACKGROUND = 248 / 255   # a bandage cell's background: its darkest channel at least this, reached from the edge
CLOTH = (0.85, 0.83, 0.8)   # the colour of a bandage cell's clear pixels
GUNSHOTS = [(0, 6), (1, 6), (2, 6), (0, 7)]   # (column, row from the top), as Wounds.Cells
SPATTER_SOLID = 0.5   # alpha of the wound's own blot, the largest joined piece at or above this
SPATTER_NEAR = 6   # px as painted from the blot within which the spatter is kept whole
SPATTER_FAR = 20   # and from which it is gone
# Each made cell: the cell it is made from, then its seed, turn (degrees), stretch along x and y and warp (px as painted).
GUNSHOT_MADE = {(1, 7): ((1, 6), 7, 40.0, 0.9, 1.15, 7.0), (2, 7): ((2, 6), 11, -65.0, 0.9, 1.1, 7.0)}


def background(cell):
    """The pixels of a bandage cell that are near white and joined to its edge through near white."""
    white = cell.min(axis=-1) >= BACKGROUND
    reached = np.zeros_like(white)
    reached[0, :] = white[0, :]
    reached[-1, :] = white[-1, :]
    reached[:, 0] = white[:, 0]
    reached[:, -1] = white[:, -1]
    while True:
        grown = reached.copy()
        grown[1:, :] |= reached[:-1, :]
        grown[:-1, :] |= reached[1:, :]
        grown[:, 1:] |= reached[:, :-1]
        grown[:, :-1] |= reached[:, 1:]
        grown &= white
        if (grown == reached).all():
            return reached
        reached = grown


def largest_piece(mask):
    """The largest 4-connected piece of a mask."""
    seen = np.zeros_like(mask)
    best = []
    for y, x in zip(*np.nonzero(mask)):
        if seen[y, x]:
            continue
        seen[y, x] = True
        piece, stack = [], [(y, x)]
        while stack:
            py, px = stack.pop()
            piece.append((py, px))
            for ny, nx in ((py - 1, px), (py + 1, px), (py, px - 1), (py, px + 1)):
                if 0 <= ny < mask.shape[0] and 0 <= nx < mask.shape[1] and mask[ny, nx] and not seen[ny, nx]:
                    seen[ny, nx] = True
                    stack.append((ny, nx))
        if len(piece) > len(best):
            best = piece
    out = np.zeros_like(mask)
    for y, x in best:
        out[y, x] = True
    return out


def trim_spatter(alpha):
    """A gunshot cell's alpha with the spatter faded out by its distance from the wound's blot."""
    blot = largest_piece(alpha >= SPATTER_SOLID)
    ys, xs = np.nonzero(blot)
    far = np.indices(alpha.shape)
    far = np.sqrt(np.min((far[0][..., None] - ys) ** 2 + (far[1][..., None] - xs) ** 2, axis=-1))
    t = np.clip((far - SPATTER_NEAR) / (SPATTER_FAR - SPATTER_NEAR), 0, 1)
    return alpha * (1 - t * t * (3 - 2 * t))


def warped_cell(colour, alpha, seed, turn, stretch_x, stretch_y, warp):
    """A cell's colour and alpha mirrored, turned and stretched about its blot's middle, and warped by smooth noise."""
    size = alpha.shape[0]
    random = np.random.RandomState(seed)
    def noise():
        coarse = Image.fromarray(random.uniform(-1, 1, (6, 6)).astype(np.float32), 'F')
        return np.asarray(coarse.resize((size, size), Image.BICUBIC)).astype(np.float64)
    weight = alpha ** 2 + 1e-9
    y, x = np.indices(alpha.shape).astype(np.float64)
    my, mx = (y * weight).sum() / weight.sum(), (x * weight).sum() / weight.sum()
    cos, sin = np.cos(np.radians(turn)), np.sin(np.radians(turn))
    # Where each pixel comes from, centred on the cell so the blot ends up in the middle.
    dx, dy = (x - (size - 1) / 2) / stretch_x, (y - (size - 1) / 2) / stretch_y
    sx = mx - (cos * dx - sin * dy) + warp * noise()
    sy = my + (sin * dx + cos * dy) + warp * noise()
    x0, y0 = np.floor(sx).astype(int), np.floor(sy).astype(int)
    fx, fy = sx - x0, sy - y0
    inside = (x0 >= 0) & (y0 >= 0) & (x0 < size - 1) & (y0 < size - 1)
    x0, y0 = np.clip(x0, 0, size - 2), np.clip(y0, 0, size - 2)
    def sample(a):
        return (a[y0, x0] * (1 - fx) * (1 - fy) + a[y0, x0 + 1] * fx * (1 - fy)
                + a[y0 + 1, x0] * (1 - fx) * fy + a[y0 + 1, x0 + 1] * fx * fy)
    out_alpha = sample(alpha) * inside
    out_colour = np.dstack([sample(colour[..., c] * alpha) for c in range(3)])
    seen = out_alpha > 1e-4
    out_colour[seen] = np.clip(out_colour[seen] / out_alpha[seen, None], 0, 1)
    out_colour[~seen] = CLEAR
    return out_colour, out_alpha


def main(src, dst):
    a = np.asarray(Image.open(src).convert('RGB')).astype(np.float64) / 255
    alpha = np.max(1 - a, axis=-1)
    safe = np.maximum(alpha, 1e-6)[..., None]
    colour = np.clip((a - (1 - alpha[..., None])) / safe, 0, 1)
    alpha = np.clip((alpha - NOISE) / (1 - NOISE), 0, 1)
    colour[alpha <= 0] = CLEAR
    h, w = alpha.shape
    for column, row in BANDAGES:
        ys, xs = slice(row * CELL, (row + 1) * CELL), slice(column * CELL, (column + 1) * CELL)
        cell = a[ys, xs]
        solid = Image.fromarray(((~background(cell)) * 255).astype(np.uint8))
        alpha[ys, xs] = np.asarray(solid.filter(ImageFilter.BoxBlur(1))).astype(np.float64) / 255
        colour[ys, xs] = np.where(alpha[ys, xs, None] > 0, cell, CLOTH)
    for column, row in GUNSHOTS:
        ys, xs = slice(row * CELL, (row + 1) * CELL), slice(column * CELL, (column + 1) * CELL)
        alpha[ys, xs] = trim_spatter(alpha[ys, xs])
    for (column, row), ((from_column, from_row), *how) in GUNSHOT_MADE.items():
        ys, xs = slice(from_row * CELL, (from_row + 1) * CELL), slice(from_column * CELL, (from_column + 1) * CELL)
        made_colour, made_alpha = warped_cell(colour[ys, xs], alpha[ys, xs], *how)
        ys, xs = slice(row * CELL, (row + 1) * CELL), slice(column * CELL, (column + 1) * CELL)
        colour[ys, xs], alpha[ys, xs] = made_colour, made_alpha
    columns, rows = w // CELL, h // CELL
    size = (columns * OUT_CELL, rows * OUT_CELL)
    fill = scaled(colour, size)   # what the clear pixels take
    colour = scaled(colour * alpha[..., None], size)
    alpha = scaled(alpha, size)
    seen = alpha > 1e-4
    colour[seen] = np.clip(colour[seen] / alpha[seen, None], 0, 1)
    colour[~seen] = fill[~seen]
    for y in range(0, size[1], OUT_CELL):
        alpha[y:y + BORDER, :] = 0
        alpha[max(0, y + OUT_CELL - BORDER):y + OUT_CELL, :] = 0
    for x in range(0, size[0], OUT_CELL):
        alpha[:, x:x + BORDER] = 0
        alpha[:, max(0, x + OUT_CELL - BORDER):x + OUT_CELL] = 0
    out = np.dstack([colour, alpha])
    Image.fromarray((out * 255).round().astype(np.uint8), 'RGBA').save(dst)
    print('wrote', dst, '%dx%d' % size, '(%d by %d cells of %d px)' % (columns, rows, OUT_CELL))


def scaled(a, size):
    """An image (h, w) or (h, w, channels) of floats scaled to size (w, h) by area."""
    if a.ndim == 3:
        return np.dstack([scaled(a[..., c], size) for c in range(a.shape[2])])
    return np.asarray(Image.fromarray(a.astype(np.float32), 'F').resize(size, Image.BOX)).astype(np.float64)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
