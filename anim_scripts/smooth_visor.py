"""Rebuilds a helmet's visor (its faces on the `Visor` material) as an even mesh on an ellipsoid fitted to it, inside its own
edge ring, which is kept where it is so it still meets the shell. The glass is drawn untextured, so each new corner
takes the texture place of the nearest old visor vertex.

    blender -b --python anim_scripts/smooth_visor.py -- <in.glb> <out.glb> [spacing [out.blend]]

spacing: model units between the inner points (default the edge ring's mean edge; 1.0 for the recon helmet).
"""
import bpy, bmesh, sys, math, numpy as np
from mathutils import Vector, geometry
args=sys.argv[sys.argv.index('--')+1:]
SPACING=float(args[2]) if len(args)>2 else 0.0   # model units between inner points; 0: the edge ring's mean edge
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=args[0])
o=[o for o in bpy.data.objects if o.type=='MESH'][0]
me=o.data
vis=[i for i,m in enumerate(me.materials) if m and m.name.startswith('Visor')][0]
bm=bmesh.new(); bm.from_mesh(me)
uvl=bm.loops.layers.uv.active
vf=[f for f in bm.faces if f.material_index==vis]
key=lambda co:(round(co.x*1e5),round(co.y*1e5),round(co.z*1e5))
pts={}; old_uv=[]
for f in vf:
    for l in f.loops:
        pts[key(l.vert.co)]=l.vert.co.copy(); old_uv.append((l.vert.co.copy(),l[uvl].uv.copy()))
# the edge ring: edges (by place) used by one visor face
count={}
for f in vf:
    ks=[key(v.co) for v in f.verts]
    for a,b in zip(ks,ks[1:]+ks[:1]):
        e=tuple(sorted((a,b))); count[e]=count.get(e,0)+1
nbr={}
for (a,b),c in count.items():
    if c==1: nbr.setdefault(a,[]).append(b); nbr.setdefault(b,[]).append(a)
assert all(len(v)==2 for v in nbr.values()), 'the visor edge is not one loop'
ring=[next(iter(nbr))]; prev=None
while True:
    a,b=nbr[ring[-1]]; nxt=b if a==prev else a
    if nxt==ring[0]: break
    prev=ring[-1]; ring.append(nxt)
assert len(ring)==len(nbr)
R=np.array([pts[k][:] for k in ring]); X=np.array([v[:] for v in pts.values()])
print('visor', len(vf), 'triangles, edge ring', len(ring))
# the ellipsoid: centred on x = 0 behind the visor, axes by least squares for each centre tried
best=None
for cy in np.linspace(X[:,1].max(), X[:,1].max()+12, 121):
    for cz in np.linspace(X[:,2].min()-6, X[:,2].max()+6, 121):
        d=X-[0,cy,cz]; k=np.linalg.lstsq(d**2,np.ones(len(X)),rcond=None)[0]
        if (k<=0).any(): continue
        ax_=1/np.sqrt(k); r=np.sqrt(((d/ax_)**2).sum(1))
        err=(np.abs(1-1/r)*np.linalg.norm(d,axis=1)).mean()
        if best is None or err<best[0]: best=(err,np.array([0,cy,cz]),ax_)
err,c,ax=best; print('ellipsoid centre',c.round(3),'semi-axes',ax.round(3),'mean residual %.3f'%err)
# a chart: azimuth round z (0 straight ahead, -y) and elevation, scaled to about model units
SH=math.sqrt(ax[0]*ax[1]); SV=ax[2]
def chart(p):
    u=(p-c)/ax; return np.c_[np.arctan2(u[:,0],-u[:,1])*SH, np.arcsin(np.clip(u[:,2]/np.linalg.norm(u,axis=1),-1,1))*SV]
def lift(q, off):
    th=q[:,0]/SH; ph=q[:,1]/SV
    u=np.c_[np.cos(ph)*np.sin(th), -np.cos(ph)*np.cos(th), np.sin(ph)]
    s=1/np.linalg.norm(u,axis=1)   # u is a unit direction in the scaled space
    return c+u*s[:,None]*ax*(1+off)[:,None]
Rq=chart(R)
# each ring vertex's offset from the ellipsoid, as a share of the radius, to be eased away inward
rr=np.sqrt((((R-c)/ax)**2).sum(1)); Roff=rr-1
edge_len=np.linalg.norm(np.roll(Rq,-1,0)-Rq,axis=1)
h=SPACING or edge_len.mean(); print('spacing %.3f'%h)
# inner points: a triangular lattice in the chart, kept inside the ring and at least 0.6 h off it
lo,hi=Rq.min(0),Rq.max(0); inner=[]
path=[Vector((x,y)) for x,y in Rq]
for j,y in enumerate(np.arange(lo[1],hi[1],h*math.sqrt(3)/2)):
    for x in np.arange(lo[0]+(h/2 if j%2 else 0),hi[0],h):
        p=np.array([x,y])
        a=Rq; b=np.roll(Rq,-1,0); t=np.clip(((p-a)*(b-a)).sum(1)/((b-a)**2).sum(1),0,1)
        dist=np.linalg.norm(a+(b-a)*t[:,None]-p,axis=1).min()
        # point in polygon by crossing count
        cross=((a[:,1]>y)!=(b[:,1]>y)) & (x < a[:,0]+(y-a[:,1])*(b[:,0]-a[:,0])/(b[:,1]-a[:,1]+1e-30))
        if cross.sum()%2==1 and dist>0.6*h: inner.append(p)
inner=np.array(inner); n=len(Rq)
allq=np.r_[Rq,inner]
vs,es,fs,_,_,_=geometry.delaunay_2d_cdt([Vector(p) for p in allq],[],[list(range(n))],2,1e-6)
assert len(vs)==len(allq), 'the triangulation added vertices'
tris=[f for f in fs]
# relax the inner points a little toward their neighbours' mean, for even triangles
nb=[set() for _ in allq]
for f in tris:
    for a in f:
        nb[a].update(b for b in f if b!=a)
q=allq.copy()
for _ in range(10):
    for i in range(n,len(q)): q[i]=0.5*q[i]+0.5*np.mean([q[b] for b in nb[i]],axis=0)
# the edge's offsets eased inward: harmonic (each inner point the mean of its neighbours), the ring fixed
off=np.r_[Roff,np.zeros(len(inner))]
for _ in range(400):
    for i in range(n,len(q)): off[i]=np.mean([off[b] for b in nb[i]])
P=lift(q,off); P[:n]=R
print('inner points',len(inner),'triangles',len(tris),'ring off ellipsoid max %.3f'%(np.abs(Roff).max()*ax.min()))
# put it in place of the old visor
bmesh.ops.delete(bm,geom=vf,context='FACES')
newv=[bm.verts.new(Vector(p)) for p in P]
OU=np.array([p[:] for p,_ in old_uv]); UV=[u for _,u in old_uv]
for f in tris:
    a,b,cc=(P[i] for i in f)
    nrm=np.cross(b-a,cc-a); out=(a+b+cc)/3-c
    if np.dot(nrm,out/ax**2)<0: f=f[::-1]
    face=bm.faces.new([newv[i] for i in f]); face.material_index=vis; face.smooth=True
    for l in face.loops:
        l[uvl].uv=UV[int(np.argmin(np.linalg.norm(OU-np.array(l.vert.co[:]),axis=1)))]
bm.to_mesh(me); bm.free(); me.update()
if len(args)>3: bpy.ops.wm.save_as_mainfile(filepath=args[3])
bpy.ops.object.select_all(action='DESELECT'); o.select_set(True)
bpy.ops.export_scene.gltf(filepath=args[1],export_format='GLB',use_selection=True,export_animations=False,export_extras=True,export_skins=False,export_morph=False)
print('WROTE',args[1])
