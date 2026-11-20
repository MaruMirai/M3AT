"""blender -b --python dump.py -- <garment.glb> <out.npz>: triangles' world places and UVs, as imported (flat.py's pose)."""
import sys, bpy, numpy as np
a = sys.argv[sys.argv.index('--') + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=a[0], disable_bone_shape=True)
o = [x for x in bpy.data.objects if x.type == 'MESH'][0]
me = o.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh()
me.calc_loop_triangles()
M = np.array(o.matrix_world)
co = np.array([v.co[:] for v in me.vertices]) @ M[:3, :3].T + M[:3, 3]
uvl = me.uv_layers.active.data
tris = np.array([t.vertices[:] for t in me.loop_triangles])
uv = np.array([[uvl[l].uv[:] for l in t.loops] for t in me.loop_triangles])
np.savez(a[1], P=co[tris], uv=uv, mid=co.mean(0))
print('DUMP', len(tris))
