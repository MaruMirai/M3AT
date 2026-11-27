# AI assistants: read this first

Making garments, a race pack or other content for M3AT? Read [Docs/AGENTS.md](Docs/AGENTS.md) before writing any code or mesh. M3AT is MIT licensed, so nothing here is a rule: these are the recommendations that have worked best for players.

In short:
- **Start garments from M3AT's own body** (its faces pushed out off the skin: `Docs/Tools/m3at_headless_garment.py`, or `Docs/Apparel.md` Approach 2), **retexture M3AT's own garments** (Approach 1), or **cut down a similar one**.
  Meshes generated from sprites, image-to-3D services or sculpts tend to come out many times heavier and clip, and M3AT simplifies anything over budget.
- **Aim for about as many triangles as M3AT's own garment** for the same thing (its shirt is 190, its power armour 1,044), and images of 1,024 pixels. M3AT's auto-optimizer, at its default setting, lets a garment have up to twice its own (at least 400 triangles), as leeway for rougher work; a garment made from M3AT's body or garments comes out close to M3AT's own, well under it.
- **Cut along the body's edges** when changing a garment's hem, neckline or outline, and follow `Docs/Methods.md` for any scripted cut or re-cut.
- **Keep a race's changes to that race's pawns:** changing M3AT's body or other garments as they load makes every pawn of every race cost more.
- **Use M3AT's API** (`M3ATgarments` folders, `ExpansionApi.RegisterPack`: `Docs/RacePacks.md`). Patches on its private code tend to break with updates, and depending on M3AT rather than bundling its files keeps your pack getting its fixes.
- **Check with the pack report** (development mode, M3AT's content switcher, Pack report) and the benchmark before publishing.
