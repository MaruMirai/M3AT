# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository. It is kept short: the detail is in `dev_notes/` (below), one file per area. **Before working on an area, read its notes file. When you change or add something, record it in that file, not here**; add to this file only what every session needs.

## What this is

A RimWorld 1.7 mod ("M3AT: Maru's 3D Animated Toons", packageId `marumirai.M3AT`; formerly "IsoRim - Maru's Pawn Models") that adds on to the **IsoRim** mod (`Peren.IsoRim17`, Workshop id 3819577715). It replaces IsoRim's procedural pawn figure with an animated, skinned 3D chibi for every person IsoRim draws as a figure (`People.FigureRace`), colonist or not: standing, moving, sitting, lying, crawling, limping, wading, swimming, working, fighting, carrying and much more (`dev_notes/motion.md`). Statues fall through to IsoRim's own figure; anything else the rules don't draw is drawn plainly (`WalkFigures.DrawPlain`). An error drawing a figure is logged once per place in the code (`DrawFailed`) and that figure drawn plainly. It is an early prototype: only some clothes have meshes (others are drawn as the nearest garment, "stand-ins"), and a held weapon is IsoRim's 3D model of it (or its sprite slab) put in the hands.

Besides Harmony and IsoRim, `About/About.xml` lists two required mods, for the faces: [NL] Facial Animation (`Nals.FacialAnimation`, Workshop id 1635901197) and [NL] Facial Animation - Experimentals (`Nals.FacialAnimationExperimentals`, Workshop id 2581693737). The code still runs without them (a bare head, `Source/FaceLayers.cs`), so the requirement is only in the mod's metadata.

IsoRim's source and docs are in the installed Workshop copy, `~/.local/share/Steam/steamapps/workshop/content/294100/3819577715/` (source in `Source/`, mod API docs in `Docs/Api.md`). Read it to see how IsoRim's `People`, `Animals`, `Poses`, `Api` and `WeaponModels` behave, but don't edit it. It updates with the Workshop, and this mod compiles against it. `./check_isorim.sh` watches for updates and a session hook prints its report (`dev_notes/tools.md`).

The game's own sprites (to model a garment after) are packed in each expansion's `Data/<pack>/Assets.zip` as zstd-compressed `.dds.split` files (Waylith's split BC7); `python3 anim_scripts/game_sprite.py <Assets.zip> <part of a texture path> <out folder>` writes them as PNGs.

## Notes by area (`dev_notes/`)

- `autotest.md`: `./autotest.sh`, running the game with scripted scenarios, videos and stills; every scenario; adding one. Changes are checked by running the game, mostly through this.
- `tools.md`: the IsoRim update watch, Workshop page and translation scripts, freeze logging, root shell scripts, running C# outside the game (harnesses).
- `clips.md`: `walk.py`'s clips and `export_frames.py`'s output (`anim.txt`, `skin.txt`, garments, holsters, weapon types), the preview tool (`preview.py`), motion capture.
- `skinning.md`: `SkinnedFigure.cs`, GPU skinning, body shapes, garments drawn and hidden, hats over hair, outlines and shaders, horns, hit flash, occlusion.
- `garment_scripts.md`: every `make_*.py` and Blender script that makes or mends garments, body keys, ears, the skeleton.
- `hair.md`: hair and beards built from sprites (`HairBuilder.cs`), hairline walls, race add-on hair.
- `options.md`: every mod option, the options window, bug reports, benchmark, the expansion API, freecam, first-person eye.
- `body.md`: size, armour bulk and ceilings, other races and their parts, age, skeletons, gene parts (ears, tails, fur, horns), missing limbs, prosthetics, wounds.
- `apparel.md`: layers and underwear, stand-ins, tuning another mod (clothing matcher and gene fallbacks, step by step), the clothing tool, other mods' garments, backpacks, webbing, worn gear, shields, helmets, dressing, apparel on the ground.
- `weapons.md`: holsters and drawing, aiming, striking, hip gear, weapon types, grips, cover and hunkering, reloading.
- `motion.md`: special placements (gibbets, vats, pools, baths), postures, work, lying and falling, ragdolls and flings, social life, eating, drugs, carrying and other activities.
- `faces.md`: Facial Animation's layers on the face patch, face fitting for races, face tuning.

Mod compatibility for players and modders (which mods are supported, how well, what each needs) is in the shipped `Docs/Compatibility.csv` (one row a mod, the at-a-glance table) and `Docs/Compatibility.md` (detail): **update both when support for another mod is added or changes**: edit the CSV (the one list), then run `python3 anim_scripts/compat_table.py`, which rewrites the page's per-category tables from it. `python3 anim_scripts/compat_html.py` writes the same list as a self-contained web page (`previews/compatibility.html`) to open in a browser or publish as an artifact from any account.

Rules that are easy to break (detail in the notes):
- **The .glb files in `Models/garments/` are often hand-edited after a `make_*.py` script wrote them, and many are not in git**, so re-running a make script over one loses those edits for good. Copy it aside (e.g. `Models/unused/<name>_before.glb`) or ask first.
- **When adding a new clip, enable it in the preview tool too** (`anim_scripts/preview.py`; `dev_notes/clips.md`).
- **About 50 garments are the body's own faces pushed out** (shirts, jackets, vests, trousers, footwear, coats, robes, dresses' tops, hosiery...). When the `Body` mesh changes, they must be recut, remade or at least moved with it: the list, how each was made and what a remake loses are in `dev_notes/garment_scripts.md` ("Which garments are the body's faces"); `anim_scripts/body_faced.py` lists them from the files.
- Kept in step by hand: `anim_scripts/holster_push.py` with `Source/Holsters.cs` and `Source/WornGears.cs`; `anim_scripts/stand_ins.py` with `Source/StandIns.cs`; `anim_scripts/grip_detect/detect.py` with `Source/GripFinder.cs`; `Shaders/MarusSmooth*` with the installed IsoRim's `IsoRim.vert.glsl`/`IsoRimSolid.frag.glsl`/`IsoRimTex.vert.glsl`; the two face shaders' `FaceArt`.
- Content packs patch our private methods by name; `ContentSources.PartOf` (`Source/ContentSources.cs`) maps those names to the part a player switches. Renaming a method a pack patches (ears, tail, hair, face, skin, hats) means updating that table in the same change (`dev_notes/options.md`, Content packs).
- New mod option text is a `M3AT_` key in both `Languages/English/` and `Languages/ChineseSimplified (简体中文)/` (`dev_notes/options.md`).
- Raise `HairBuilder.Version` (or `BeardVersion`) when the hair build changes, so cached hair is rebuilt.
- Raise `BODY_GENERATION` (`anim_scripts/export_frames.py`) when the body's surface or rest pose moves enough to make garments made on the old one clip, so other mods' garments made on it are flagged. The bone names, the A-pose, the scale and the body shapes' names are promised to modders as kept (`Docs/Apparel.md`, "When M3AT's body changes"; `dev_notes/garment_scripts.md`).
- Blender is not on PATH: `/home/t/Programs/blender-5.2.3-linux-x64/blender`.

## Layout

The mod as the game loads it is the folder `M3AT: Maru's 3D Animated Toons/` (`About/`, `LoadFolders.xml`, `1.7/Assemblies/`, `Defs/`, `Patches/`, `Shaders/`, `Models/chibi/`, `Models/wounds.png` the fishing rod's `Models/tool_rod.obj`, `.mtl` and `fishing_line.obj`, the spoon's `Models/tool_spoon.obj` and `.mtl`, the roasting stick's `Models/tool_stick.obj` and `.mtl`, the joint's `Models/tool_joint.obj`, `tool_joint_unlit.obj` and `.mtl`, the felling axe's `Models/tool_felling_axe.obj` and `.mtl` (`anim_scripts/make_axe.py`), the rope round a prisoner tied to a tree, `Models/tool_rope.obj` and `.mtl` (`anim_scripts/make_rope.py`) and the spade's `Models/tool_spade.obj` and `.mtl` (`anim_scripts/make_spade.py`)). Also shipped: `Docs/` (guides for modders making garments, gene parts and content packs, `Docs/AGENTS.md` for AI assistants making content, `Docs/Methods.md` the scripted methods behind our garment cuts (hems on body edges, learnt paths, rim trims, hem lifts, hem-band repaint, re-cuts) in cells, kept in step with `round_hem.py`/`recut_from_parent.py`, `Docs/Tools/m3at_garment_kit.py` and `m3at_headless_garment.py`, `Docs/Examples/`) and `AGENTS.md` at the mod's root, pointing AI assistants to it. RimWorld's `Mods/` folder has a symlink to that folder, not to the repository. Everything else at the repository's root is development only and is not shipped: `Source/`, `anim_scripts/`, the .blend and .glb files in the root `Models/` (the rig, `Models/garments/`, `Models/bases/`, `Models/unused/`, the old `Models/unused/p1`), `animDLs/`, `archive/`, `generationAssets/` (its scripts are in `anim_scripts/generation/`, their work left in `generationAssets/`, which is not in git, nor are `Models/unused/` and `previews/`), `weapon_sprites/`, `dev_notes/`, the shell scripts and the TODO notes. Paths the game reads (`Models/chibi`, `Models/wounds.png`, `Defs/...`, `Shaders/...`) are relative to the mod's folder; tool paths (`Models/pawnHumanRig.blend`, `anim_scripts/...`) are relative to the repository's root. The C# namespace and the assembly are `M3AT`; the `Marus*` shader names keep the old name.

## Build

```
dotnet build Source/M3AT.csproj
```

The output goes to `M3AT: Maru's 3D Animated Toons/1.7/Assemblies/M3AT.dll`, which that folder's `LoadFolders.xml` loads. The csproj references the RimWorld, Harmony and IsoRim DLLs by absolute paths under `~/.local/share/Steam/...`. It compiles against the *installed* Workshop IsoRim.dll, not against `3819577715/`. There are no tests. Check changes by running the game.

Code that only does maths (the ragdoll solver, skinning, the garment and holster placing) can also be run outside the game, on the game's own .NET 10 runtime: see `dev_notes/tools.md` and the kept harnesses `anim_scripts/hairbuild/` and `anim_scripts/garmentfit/`.

Engine costs (read from the decompiled Waylith engine with `~/.dotnet/tools/ilspycmd`, not from docs; they are unlike real Unity's, so check them again after a game update). They are why `Source/GpuSkinning.cs` is built as it is:
- `Mesh.vertices`, `normals` and `colors32` setters clone the array. At the next use the engine rebuilds the whole mesh's CPU data, UVs and indices included, and works out the bounds twice, so an explicit `bounds` has to be set again after. Each changed mesh then gets new Vulkan vertex and index buffers.
- `Matrix4x4.MultiplyPoint3x4` and `MultiplyVector` transpose the whole matrix on every call.
- There is no frustum culling: every draw call given is drawn. IsoRim's `Cull` culls pawns and model parts itself.
- `MaterialPropertyBlock.SetVectorArray`/`SetMatrixArray` become per-draw storage buffers, copied when `Graphics.DrawMesh` is called. A GLSL `layout(std430, set = 2, binding = N) readonly buffer _Name { ... }` gets one by its block name. Arrays set on the material instead are last write per frame, as the binding is shared per material.
- A material's layout (uniforms, textures, storage slots) is parsed from the fragment shader's text, so a buffer that only the vertex stage reads must be declared in the fragment shader too.
- The engine's mesh carries only uv0..uv3 (each four floats) and tangents as extra vertex channels.
- IsoRim's own `DynamicModels.Submit` passes only its own floor block, and its `PartBatches` instancing would drop a block, so GPU-skinned parts go through `Api.SubmitFigureMesh` (Api 62), which takes our block, writes its floor and flash into it, and never batches.
- Shaders compile without the game running through the game's Slang: `Waylith.Rendering.RuntimeShaderCompiler.Compile(source, name, "main", RuntimeShaderLanguage.Glsl, stage)`, with the engine's variant defines (`USE_TEXTURE`, `INSTANCED`, `OUTLINE_MRT`) added after `#version`. `ShaderMetadataParser.ExtractMaterialLayout`, `ExtractVertexInputLayout` and `ValidateStageInterface` are public too. A shader that fails to compile in the game is drawn magenta.
- In the harness, reading `solid.EngineMesh` forces the rebuild without a GPU, but the cost of the Vulkan buffers can't be measured there.
- A texture is stored upside down against a texture place worked out in a shader: a mesh's UVs reach the shader turned to match, and a material's `_MainTex_ST` carries the turn, so read a texture at `vUv * _MainTex_ST.zw + _MainTex_ST.xy` (as the face layers do), and a place made from uniforms with `1 - v` (`MarusFace.frag.glsl` mode 7). Read at the raw `vUv`, the eye wounds' masks came out clear and their art from the sheet's other end, so no eye wound ever showed.
- A storage buffer slot a draw leaves unset is bound to the first slot it does set (or to zeros if none is): `TryWriteMaterialStorage`. So a shader that declares two buffers must have both set on every draw, or the unset one reads the other's data.
- A `vec4` declared after a run of floats in a material's uniform block (`MarusFace`'s `_WoundAt`/`_WoundCell` after three) reached the shader as nothing; declared beside the other `vec4`s it works. Keep `vec4`s together, ahead of the floats.

## Asset pipeline (Blender → game)

1. `anim_scripts/walk.py` generates every clip on the Rigify metarig (`metarig`) in `Models/pawnHumanRig.blend`: procedural poses and imported motion capture or Meshy clips. Distances are in leg lengths.
2. `anim_scripts/export_frames.py` (`./export_model.sh`) bakes them into `M3AT: Maru's 3D Animated Toons/Models/chibi`: `anim.txt` (each clip as bone turns per frame), `skin.txt` (rest mesh, weights, body shape keys, face patch), `garment_*.txt`/`.png`, the older baked `idle.obj`/`walk_*.obj` frames and `clip.txt`, and the face patches. `dev_notes/clips.md`.
3. `Source/SkinnedFigure.cs` skins the mesh per pawn in the game (on the GPU by default), per body shape, with garments, hair and face. `dev_notes/skinning.md`.
4. Hair and beards are built in the game from the player's loaded hair sprites; the mod ships no hair. `dev_notes/hair.md`.

Conventions that must stay consistent across the exporter and the C#:
- OBJ space is one unit = one map cell, +Y up, with the figure facing -Z. Blender's (x, y, z) is written as (-x, z, y). Triangle winding is written reversed because IsoRim's loader mirrors z.
- The figure is scaled so the idle pose is `FIGURE_HEIGHT` (1.25, the top of IsoRim's `person_head.obj`) tall. IsoRim's `People.FigureHeight` says 1.12, but its body and head meshes reach 1.25, so 1.12 comes out too small.
- The material is named `stuff_skin`. IsoRim tints materials whose names start with `stuff` with the colour passed to `DrawModel` (the pawn's skin colour).
- Face patches: the triangles are picked once in the standing pose. If the body has a `face` vertex group, that group is used. Otherwise the patch is the front-facing triangles dominated by the head bone (`walk.HEAD`, `spine.006`). UVs are a front projection in the head bone's frame, so they stay fixed per vertex and the face follows the head. They use Facial Animation's texture layout (eye line at v = 1 - 70.6/128), with `FACE_SPAN` and `FACE_EYES` setting the size and height. The patch is lifted `FACE_LIFT` cells along the normals. A `# centre x y z` comment gives the head's middle, which the C# grows each layer out from. FaceLayers reads these files with z flipped and the triangle order kept, as IsoRim's loader does.
- `clip.txt` keys are `frames`, `ground_per_cycle`, `run_levels`, `run_ground_per_cycle`, `crawl_ground_per_cycle`, `limp_ground_per_cycle`, `hop_ground_per_cycle`, `wade_ground_per_cycle` and `swim_ground_per_cycle`. If the run keys are missing, the C# falls back to walk only (as in `Models/unused/p1`, an older walk-only export).

## Runtime (WalkFigures.cs)

- The static constructor requires IsoRim `Api.Version >= 62` (`MinimumApi`: the custom figure renderer Api, Docs/Api.md part 43, `Docs/FigureApi-Migration.md`). It finds its own `ModContentPack` and registers a figure drawer (`Api.AddFigureDrawer(DrawFigure, FigureKinds.People)`), asked for each person IsoRim would draw as a figure after its own gates (3D view, hidden pawns, races drawn as cards, statues); true means "we drew it", and IsoRim draws nothing of that figure. It reads IsoRim's placing (`ctx.ReadPlacement`, our stride and facing in `FigurePlacementOptions`: `Situation`, `Position`, `Yaw`, `StridePhase`, `Gait`, `Lift`, `ChestLift`, aim), and leaves to IsoRim what the placing says the chibi doesn't do (`Attached`, `InVat`, `Gibbet`, `Bones`). What goes by the aim before the placing is read (`EaseOf`, `WeaponShows`) takes the last placing's (`AimOf`, `aimsSeen`). A carried chibi is drawn by its carrier (`DrawLoad` calls `DrawHeld`), as the game drawing it from inside IsoRim's call would come back to the drawer while it is busy, and IsoRim would draw its own; anything else carried goes to `Api.DrawCarried`. The weapon model rule (`WeaponRule`, `Api.AddWeaponModelRule`) and ground model rule (`GroundRule`) replace the old patches on IsoRim. IsoRim internals are still used only by `Source/GroundOutlines.cs` (not patched in once IsoRim has its own ring, M230, by its `IsoRimOutlineRing` ShaderDef). A failure while drawing is logged once (`DrawFailed`) and that figure drawn plainly (`DrawPlain`); only a failure to load the models leaves IsoRim's figures for the session.
- Models load lazily on the first draw (`Ready()`). If any file is missing, the mod is disabled for the session.
- The animation phase comes from IsoRim's stride tracker, through the placing (`StridePhase`, `Gait`). It advances the stride phase by ground actually covered (stride = `ground_per_cycle * size`), so the feet match the real speed. Walk-to-run blending is driven by path speed in cells per game second (`SpeedOf`, `RunFrom`/`RunFull`), eased per pawn (`eases`, pruned when it gets large). The stride length is lerped between gait levels, and the pose snaps to the nearest baked level.
- Which model folder is used is the `Folder` constant (currently `Models/chibi`, in the mod's folder).
- Everything else at runtime is in the notes files by area (above).

## Style

Match the existing voice: comments are plain-English sentences that explain *why* and what units are in use (cells, cells per game second, leg lengths), and C# uses explicit types with `Tag`-prefixed `Log` messages. Workshop-facing text (`About/About.xml` in the mod's folder) avoids jargon.
