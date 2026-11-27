import numpy as np, zr, sys
V,T,E,F=zr.load_quads(sys.argv[1] if len(sys.argv)>1 else 'newbody.npz'); out=sys.argv[2] if len(sys.argv)>2 else 'nb'
zr.row([zr.render(V,T,v,width=500,edge_list=E) for v in ('front','back','side')]).save(out+'_full.png')
zr.row([zr.render(V,T,'front',box=(5.5,10.5,13.5,18),px=100,edge_list=E),zr.render(V,T,'top',box=(5.5,10.5,-2,3),px=100,edge_list=E),zr.render(V,T,'under',box=(5.5,10.5,-2,3),px=100,edge_list=E)]).save(out+'_hand.png')
zr.row([zr.render(V,T,'front',box=(-4,4,10.5,22.5),px=60,edge_list=E),zr.render(V,T,'side',box=(-4,4,10.5,22.5),px=60,edge_list=E),zr.render(V,T,'back',box=(-4,4,10.5,22.5),px=60,edge_list=E),zr.render(V,T,'front',box=(-4,4,10.5,22.5),px=60,edge_list=E,yaw=45)]).save(out+'_torso.png')
zr.row([zr.render(V,T,'side',box=(-3,2.5,0,4),px=90,edge_list=E),zr.render(V,T,'front',box=(0.5,4.5,0,4),px=90,edge_list=E),zr.render(V,T,'under',box=(0.5,4.5,-2.8,2.4),px=90,edge_list=E),zr.render(V,T,'front',box=(-3,4.5,10,13.5),px=70,edge_list=E,pitch=-40)]).save(out+'_foot.png')
