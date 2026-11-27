# Writes a mesh object's rest-pose (no modifiers) world vertices and faces, and its vertex groups, to an npz.
import bpy, sys, numpy as np
argv=sys.argv[sys.argv.index("--")+1:]
o=bpy.data.objects[argv[0]]; m=o.data
V=np.array([tuple(o.matrix_world@v.co) for v in m.vertices]); F=[list(p.vertices) for p in m.polygons]
T=[]
for f in F:
    for i in range(1,len(f)-1): T.append([f[0],f[i],f[i+1]])
G=np.zeros((len(V),len(o.vertex_groups)))
for v in m.vertices:
    for g in v.groups: G[v.index,g.group]=g.weight
names=[g.name for g in o.vertex_groups]
keys={}
if m.shape_keys:
    for k in m.shape_keys.key_blocks: keys[k.name]=np.array([tuple(o.matrix_world@d.co) for d in k.data])
np.savez(argv[1],V=V,T=np.array(T),names=np.array(names),G=G,**{"key_"+k:v for k,v in keys.items()})
print("DUMPED",len(V),len(T))
