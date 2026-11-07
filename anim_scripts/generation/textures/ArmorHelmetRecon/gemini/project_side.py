"""Paints a patch of a side view straight onto a garment's texture, both sides (the far side the mirror image), where
Meshy's retexture left it soft: for the recon helmet, the vent block.

    blender -b --python project_side.py -- <in.glb> <side.png> <out.glb> <x0> <y0> <x1> <y1> [feather px]

The side view is render.py's: orthographic from +x (Blender's), framed on the mesh's box scaled 1.15, square. x0..y1 is
the patch in that view's pixels (of a 1024 square). Each texel of a triangle facing the view (FACING) and seen from it
(nothing of the mesh between) takes the view's pixel, its greys scaled so their light shell grey (LIGHT) over the patch matches the
texture's there, blended in over FEATHER pixels inside the patch's edge and as the surface turns away.
"""
import sys

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

FACING = 0.35    # the least cosine between a triangle's normal and the view for it to take the view's paint
FULL = 0.6       # from this cosine on it takes all of it
SCALE = 1.15     # render.py's framing
LIGHT = 90       # the percentile of grey matched between the view and the texture: the bare shell's, not the darker parts'

a = sys.argv[sys.argv.index('--') + 1:]
src, side, out = a[0], a[1], a[2]
x0, y0, x1, y1 = (float(v) for v in a[3:7])
feather = float(a[7]) if len(a) > 7 else 24.0

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
o = next(ob for ob in bpy.data.objects if ob.type == 'MESH')
m = o.data
places = [o.matrix_world @ v.co for v in m.vertices]
low = Vector(min(p[i] for p in places) for i in range(3))
high = Vector(max(p[i] for p in places) for i in range(3))
mid = (low + high) / 2
span = max(high - low) * SCALE
tree = BVHTree.FromPolygons(places, [f.vertices for f in m.polygons])

image = next(n.image for mat in m.materials for n in mat.node_tree.nodes if n.type == 'TEX_IMAGE' and n.image)
W, H = image.size
tex = np.array(image.pixels[:], dtype=np.float32).reshape(H, W, 4)   # rows bottom up

picture = bpy.data.images.load(side)
picture.colorspace_settings.name = image.colorspace_settings.name
picture.scale(1024, 1024)
view = np.array(picture.pixels[:], dtype=np.float32).reshape(1024, 1024, 4)[::-1, :, :3].mean(2)   # rows top down


def view_at(p):
    """The side view's pixel (x right, y down, of 1024) under a point on the +x side."""
    u = (p.y - mid.y) / span + 0.5
    v = (p.z - mid.z) / span + 0.5
    return u * 1024, (1 - v) * 1024


def patch_weight(x, y):
    inside = min(x - x0, x1 - x, y - y0, y1 - y)
    return max(0.0, min(1.0, inside / feather))


uv = m.uv_layers.active.data
hits = []    # (row, col, view value, weight)
for f in m.polygons:
    n = (o.matrix_world.to_3x3() @ f.normal).normalized()
    for sign in (1, -1):
        facing = n.x * sign
        if facing < FACING:
            continue
        lw = min(1.0, (facing - FACING) / (FULL - FACING))
        loops = list(f.loop_indices)
        for k in range(1, len(loops) - 1):
            tri = [loops[0], loops[k], loops[k + 1]]
            P = [o.matrix_world @ m.vertices[m.loops[l].vertex_index].co for l in tri]
            T = [Vector((uv[l].uv[0] * W, uv[l].uv[1] * H)) for l in tri]
            lo_x, hi_x = int(min(t.x for t in T)), int(max(t.x for t in T)) + 1
            lo_y, hi_y = int(min(t.y for t in T)), int(max(t.y for t in T)) + 1
            d = (T[1].y - T[2].y) * (T[0].x - T[2].x) + (T[2].x - T[1].x) * (T[0].y - T[2].y)
            if abs(d) < 1e-9:
                continue
            for ty in range(max(lo_y, 0), min(hi_y, H)):
                for tx in range(max(lo_x, 0), min(hi_x, W)):
                    px, py = tx + 0.5, ty + 0.5
                    b0 = ((T[1].y - T[2].y) * (px - T[2].x) + (T[2].x - T[1].x) * (py - T[2].y)) / d
                    b1 = ((T[2].y - T[0].y) * (px - T[2].x) + (T[0].x - T[2].x) * (py - T[2].y)) / d
                    b2 = 1 - b0 - b1
                    if min(b0, b1, b2) < -0.02:
                        continue
                    p = P[0] * b0 + P[1] * b1 + P[2] * b2
                    seen = Vector((p.x * sign, p.y, p.z)) if sign > 0 else Vector((2 * mid.x - p.x, p.y, p.z))
                    vx, vy = view_at(seen)
                    w = patch_weight(vx, vy) * lw
                    if w <= 0:
                        continue
                    hit = tree.ray_cast(p + Vector((sign * 1e-4, 0, 0)), Vector((sign, 0, 0)))
                    if hit[0] is not None:
                        continue
                    hits.append((ty, tx, view[min(int(vy), 1023), min(int(vx), 1023)], w))

rows = np.array([h[0] for h in hits]); cols = np.array([h[1] for h in hits])
vals = np.array([h[2] for h in hits]); ws = np.array([h[3] for h in hits])
had = tex[rows, cols, :3].mean(1)
gain = np.percentile(had[ws > 0.5], LIGHT) / max(np.percentile(vals[ws > 0.5], LIGHT), 1e-3)
new = np.clip(vals * gain, 0, 1)
for c in range(3):
    tex[rows, cols, c] = tex[rows, cols, c] * (1 - ws) + new * ws
image.pixels[:] = tex.ravel()
image.update()
image.pack()
for ob in bpy.context.scene.objects:
    ob.select_set(ob is o)
bpy.context.view_layer.objects.active = o
bpy.ops.export_scene.gltf(filepath=out, export_format='GLB', use_selection=True, export_animations=False,
                          export_skins=False, export_morph=False)
print('PROJECTED %d texels, %d fully, grey gain %.3f' % (len(hits), int((ws >= 1).sum()), gain))
