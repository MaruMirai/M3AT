# M3AT modding docs

Guides for making things for M3AT: Maru's 3D Animated Toons without changing M3AT itself.

- [Compatibility.csv](Compatibility.csv) (at a glance) and [Compatibility.md](Compatibility.md) (detail): which other mods M3AT supports, how well, and what each needs.
- [Apparel.md](Apparel.md): 3D garments for apparel (your mod's, the game's or another mod's), and retextures of M3AT's own garments. Read this first.
- [GeneParts.md](GeneParts.md): meshes drawn for a gene (scales, spikes, crests, tusks).
- [Methods.md](Methods.md): for scripts and AI assistants changing garments: cutting hems along the body's edges, learning a hand-drawn line, lifting a hem over the garment beneath, moving a hem's texture, re-cutting a garment from the one it was cut from, and the checks to run.
- [AGENTS.md](AGENTS.md): for AI assistants making content for M3AT (and the people using them): how to make garments that fit and cost little, the budgets, and what not to do.
- [Tools/m3at_headless_garment.py](Tools/m3at_headless_garment.py): a garment made from M3AT's body from the command line, with no clicking.
- [RacePacks.md](RacePacks.md): content packs: a race's own ears, tail, face, hair and skin colour, registered with M3AT's API, and how players choose between a pack's content and M3AT's.
- [Tools/m3at_garment_kit.py](Tools/m3at_garment_kit.py): the Blender add-on both guides use.
- [Examples/M3ATgarments/](Examples/M3ATgarments/): a retexture and a gene part, as they would sit in a mod's `M3ATgarments` folder.

M3AT is in early development and its body is liable to change between updates (we aim to settle on one soon), so keep what you made each piece from: see [When M3AT's body changes](Apparel.md#when-m3ats-body-changes).

In short: put `garment_<Name>.txt` files (and their images) in a folder named `M3ATgarments` at your mod's root. Each file gives a mesh's shape and which apparel or genes it is for. M3AT fits the rest to its body in the game.
