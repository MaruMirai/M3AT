"""Splits the body's quads into triangles along the diagonal the export already uses, so the split is visible and fixed.

    blender -b Models/pawnHumanRig.blend --python anim_scripts/split_quads.py -- [--from N] [--all] [--check]

The export (export_frames.posed_triangles) and the garment scripts (make_from_groups, recut_garments) split a quad by
Blender's own triangulation (loop_triangles) of the rest shape, and the garments hold those triangles. Ctrl+T's default
(Beauty) takes the other diagonal on about half the body's quads (58 of 114 on 2026-11-27), which would leave garments
on faces the body no longer has. This splits each quad exactly as loop_triangles does, so the body's triangles (and so
skin.txt and every garment) are unchanged. --from N: only quads with a vertex numbered N or more (a cut's new vertices;
default 435, the chest cut of 2026-11-26); --all: every quad. Saves the .blend it opened, unless --check.
"""
import sys

import bpy
import bmesh

argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
first = int(argv[argv.index('--from') + 1]) if '--from' in argv else 435
every = '--all' in argv
check = '--check' in argv
o = bpy.data.objects['Body']
me = o.data
if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
    bpy.ops.object.mode_set(mode='OBJECT')

def triangles():
    me.calc_loop_triangles()
    return {frozenset(t.vertices) for t in me.loop_triangles}, me.loop_triangles

before, tris = triangles()
diagonal = {}
for t in tris:
    p = me.polygons[t.polygon_index]
    if len(p.vertices) == 4 and (every or max(p.vertices) >= first):
        a, b, c, d = p.vertices
        edges = {frozenset(e) for e in ((t.vertices[0], t.vertices[1]), (t.vertices[1], t.vertices[2]), (t.vertices[2], t.vertices[0]))}
        if frozenset((a, c)) in edges:
            diagonal[p.index] = (a, c)
        elif frozenset((b, d)) in edges:
            diagonal[p.index] = (b, d)
print('SPLIT %d quads%s' % (len(diagonal), ' (check only)' if check else ''))
if not check and diagonal:
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    bm.verts.ensure_lookup_table()
    work = [(bm.faces[fi], bm.verts[x], bm.verts[y]) for fi, (x, y) in diagonal.items()]   # (a split outdates the tables)
    for f, vx, vy in work:
        bmesh.utils.face_split(f, vx, vy)
    bm.to_mesh(me)
    bm.free()
    me.update()
    after, _ = triangles()
    if after != before:
        sys.exit('split_quads.py: the triangles changed (%d gone, %d new); not saved' % (len(before - after), len(after - before)))
    print('SPLIT the body\'s triangles are unchanged (%d); now %d faces, %d quads' % (
        len(after), len(me.polygons), sum(1 for p in me.polygons if len(p.vertices) == 4)))
    bpy.ops.wm.save_mainfile()
