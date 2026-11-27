import bpy, json, sys
a=bpy.data.objects["metarig"]; out={}
for b in a.data.bones:
    out[b.name]={"head":list(a.matrix_world@b.head_local),"tail":list(a.matrix_world@b.tail_local),"z":list((a.matrix_world.to_3x3()@b.z_axis)),"parent":b.parent.name if b.parent else None,"deform":b.use_deform}
json.dump(out,open(sys.argv[-1],"w"),indent=0)
