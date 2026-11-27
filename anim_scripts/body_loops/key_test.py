import bpy, sys, numpy as np
out = sys.argv[sys.argv.index('--') + 1]
if bpy.context.object and bpy.context.object.mode != 'OBJECT': bpy.ops.object.mode_set(mode='OBJECT')
body = bpy.data.objects['Body']; res = {}
kb = body.data.shape_keys.key_blocks
for k in kb:
    for j in kb: j.value = 0
    if k.name != 'Basis': k.value = 1
    dg = bpy.context.evaluated_depsgraph_get(); ev = body.evaluated_get(dg); me = ev.to_mesh(); me.calc_loop_triangles()
    res[k.name + '_V'] = np.array([tuple(body.matrix_world @ v.co) for v in me.vertices]); res[k.name + '_T'] = np.array([tuple(t.vertices) for t in me.loop_triangles])
    ev.to_mesh_clear()
np.savez(out, **res); print('KEYS', out)
