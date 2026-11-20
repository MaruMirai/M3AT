# M3AT modding docs

Guides for making things for M3AT: Maru's 3D Animated Toons without changing M3AT itself.

- [Apparel.md](Apparel.md): 3D garments for apparel (your mod's, the game's or another mod's), and retextures of M3AT's own garments. Read this first.
- [GeneParts.md](GeneParts.md): meshes drawn for a gene (scales, spikes, crests, tusks).
- [Tools/m3at_garment_kit.py](Tools/m3at_garment_kit.py): the Blender add-on both guides use.
- [Examples/M3ATgarments/](Examples/M3ATgarments/): a retexture and a gene part, as they would sit in a mod's `M3ATgarments` folder.

In short: put `garment_<Name>.txt` files (and their images) in a folder named `M3ATgarments` at your mod's root. Each file gives a mesh's shape and which apparel or genes it is for. M3AT fits the rest to its body in the game.
