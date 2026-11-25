# Development tools

The IsoRim update watch, Workshop page scripts, freeze logging, root shell scripts and running C# outside the game.

## IsoRim updates

IsoRim's installed Workshop copy is `~/.local/share/Steam/steamapps/workshop/content/294100/3819577715/` (`Source/`, `Docs/Api.md`). Read it, never edit it; this mod compiles against it. `./check_isorim.sh` (hourly, systemd user timer `m3at-isorim-watch.timer`) notices an update once Steam is done with it (nothing staged under `workshop/downloads` or `temp`, no file written in the last 10 minutes; an update touching none of the watched parts is taken as the baseline silently), sends a desktop notification and writes `~/.local/state/m3at-isorim/pending.txt`: the Api.Version, the files changed since the acknowledged copy in `baseline/` (the shaders `MarusSmooth` copies first) and a trial build against the new DLL. A session hook prints it. `./check_isorim.sh --ack` takes the installed IsoRim as the new baseline.

## Game sprites

The game's sprites are in each expansion's `Data/<pack>/Assets.zip` as zstd-compressed `.dds.split` files (split BC7); `python3 anim_scripts/game_sprite.py <Assets.zip> <part of a texture path> <out folder>` writes them as PNGs.

## Workshop page

The description drafts (`workshop_description*.txt` at the root, not in git) are Steam BBCode. `python3 anim_scripts/workshop_page.py [description.txt] [out.html]` renders one as a Workshop-styled page with `About/Preview.png` and a Copy BBCode button (default: the short bold one, into `previews/`). `python3 anim_scripts/workshop_i18n.py` (`sync`, `todo`, `build`, `upload`) keeps the translations (`LANGUAGES`: Simplified and Traditional Chinese, Russian, Brazilian Portuguese, Korean, Japanese, Latin American Spanish) line by line from `workshop_description_short_bold.txt`: `workshop_i18n/<language>.json` maps each English line to its translation (`""` leaves it out), so `sync` adds only changed lines (as null). `upload` sets every language's title and description through RimWorld's `libsteam_api.so` with Steam running (no change note), stopping if the live English was edited on the website since the last upload. Steam's limit is 8000 UTF-8 bytes, so the Russian, Korean and Japanese are condensed.

## Freezes and memory

`Source/LoadTrail.cs` writes `<config>/M3AT/trail.txt` (last session's: `trail-prev.txt`) from the mod's constructor on, synced line by line so a whole-system freeze leaves it: program state and long events, load steps (`LoadTrail.Note`, with memory), a line whenever the game's memory moves `MemoryStep` or the system is low, and, when the main thread has done no frame for `StallSeconds`, what it was on, IsoRim's `CrashTrail.Report()` and every managed thread's stack from `dotnet-stack` (`~/.dotnet/tools`), also into `freeze-<time>.txt`. `./watch_memory.sh` (`--install`: systemd user service `m3at-memwatch.service`, installed) logs system, RimWorld and GPU memory every 2 s to `~/.local/state/m3at-memory/`. The machine has no swap: filling memory freezes it for minutes rather than killing the game.

## Root shell scripts

- `./export_model.sh`: re-exports `Models/chibi` from the saved .blend (`dev_notes/clips.md`), output filtered to the `SKIN`, `GARMENT`, `FIT`, `EXPORT` and `WARNING` lines; `--fit` passed on.
- `./apply_face.sh`: writes its `FACE_SPAN`, `FACE_EYES`, `MOUTH_ZOOM` and `MOUTH_RAISE` into the exporter and `FaceLayers.cs`, re-exports and rebuilds.
- `./backup_rig.sh`: copies `Models/pawnHumanRig.blend` to `~/Backups/pawnHumanRig/` when changed (`--install`: systemd user timer `pawn-rig-backup.timer`, every 10 minutes).

## Running C# outside the game

Maths-only code (ragdoll solver, skinning, garment and holster placing) can run outside the game. RimWorld 1.7 is a self-contained .NET 10 app whose `UnityStubs.dll` implements `Vector3`, `Quaternion` and `Matrix4x4` in plain C#; only .NET 8 is installed system-wide, so a harness runs on the game's own runtime. Kept harnesses: `anim_scripts/hairbuild/` (`dev_notes/hair.md`) and `anim_scripts/garmentfit/` (`dev_notes/apparel.md`). What works:

- A csproj compiling `Source/*.cs` plus a `Test.cs` (may be a `partial class SkinnedFigure` for private access), with the mod csproj's references and `<AssemblyName>RimWorld</AssemblyName>`, `OutputType Exe`, `UseAppHost false`, `GenerateRuntimeConfigurationFiles false`.
- A scratch folder of symlinks to every file of the RimWorld install except `RimWorld.dll` (the harness's) and the `RimWorld` launcher, which must be copied: a symlinked one can resolve back to the real install and start the game.
- IsoRim and Harmony loaded from their Workshop folders by an `AssemblyLoadContext.Default.Resolving` handler in `Main`.
- The chibi read with the private `ReadMesh`/`ReadAnim` rather than `Load` (which wants the garment textures), and `faceFrom = null` before `NewPosed`. Posed meshes can be written as OBJ and rendered with Blender's Workbench.
