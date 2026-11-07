import bpy, sys, mathutils
a=sys.argv[sys.argv.index('--')+1:]; glb,obj,out=a
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=glb); src=[o for o in bpy.context.scene.objects if o.type=='MESH']
bpy.ops.wm.obj_import(filepath=obj); wr=[o for o in bpy.context.selected_objects if o.type=='MESH']
def bb(objs):
    pts=[o.matrix_world@mathutils.Vector(c) for o in objs for c in o.bound_box]
    lo=mathutils.Vector([min(p[i] for p in pts) for i in range(3)]); hi=mathutils.Vector([max(p[i] for p in pts) for i in range(3)]); return lo,hi
print('SRC',bb(src)); print('WRAP',bb(wr))
lo,hi=bb(src); l2,h2=bb(wr)
k=max(hi-lo)/max(h2-l2)
for o in wr: o.scale*=k; o.location=(lo+hi)/2-(l2+h2)/2*k
bpy.context.view_layer.update(); print('FIT',k,bb(wr))
w=(hi-lo).x*1.4
for o in wr: o.location.x+=w
sc=bpy.context.scene; sc.render.engine='BLENDER_WORKBENCH'; sc.display.shading.light='STUDIO'; sc.render.resolution_x=1600; sc.render.resolution_y=800
for o in wr:
  for p in o.data.polygons: p.use_smooth=False
cam=bpy.data.objects.new('c',bpy.data.cameras.new('c')); sc.collection.objects.link(cam); sc.camera=cam
bpy.context.view_layer.update(); lo2,hi2=bb(src+wr); c=(lo2+hi2)/2; size=max(hi2-lo2)
cam.data.type='ORTHO'; cam.data.ortho_scale=size*1.1; cam.data.clip_end=size*10
for name,d in [('front',(0.4,-1,0.3)),('back',(-0.4,1,0.3))]:
    dv=mathutils.Vector(d).normalized(); cam.location=c+dv*size*3
    cam.rotation_euler=(-dv).to_track_quat('-Z','Y').to_euler()
    sc.render.filepath=out+'_'+name+'.png'; bpy.ops.render.render(write_still=True)
