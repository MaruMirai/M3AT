# M3AT: Maru's 3D Animated Toons

Animated 3D pawns for [IsoRim](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715), the mod that shows RimWorld 1.7 in 3D.

![M3AT preview](<M3AT: Maru's 3D Animated Toons/About/Preview.png>)

IsoRim draws your colonists as simple figures. M3AT replaces them with fully animated, skinned 3D characters: they walk, run, sit, sleep, fight, work, eat, read, play music, carry each other about, limp when hurt and fall limp when they're shot.

**This is an early version.** A lot already works, but expect rough edges. Bug reports are very welcome (see [Reporting a bug](#reporting-a-bug)).

## Features

**Animation**
- Walking and running matched to each pawn's real speed
- Sitting, lying in bed, sleeping, crawling, wading and swimming
- Work: mining, chopping, building, crafting at a bench, planting, harvesting, fishing, research, tending patients, taming, firefighting and more
- Eating at a table or standing, reading, playing piano, harp and drum, teaching, horseshoes and hoopstone
- Carrying items, babies and people (the wounded in the arms, captives over the shoulder)
- Chatting at parties, rubbing hands in the cold, getting up off the floor, jump packs

**Combat**
- A drafted stance per weapon type, with weapons held properly in the hands using IsoRim's 3D weapon models
- Aiming, recoil, drawing a bow, melee swings, punches, grenade throws, flinches and muzzle flashes
- Weapons not in use are worn on the body and drawn and holstered with an animation
- Ragdolls, and optional flinging of bodies and dropped weapons by heavy hits

**Looks**
- 3D clothing, armour and headgear for the base game, all DLCs and Combat Extended, keeping RimWorld's material colours
- Hair and beards generated in 3D from whatever hair textures you have loaded, hair mods included. Nothing is modelled by hand, and no other mod's art is shipped
- Gene parts modelled, with animated tails and ears
- Worn packs, belts and gear using IsoRim's models
- Faces animated by [NL] Facial Animation: blinking, expressions and moods
- Every body type, gender differences, optional varied builds and heights
- Children grow smoothly from babies to adults, and pregnancy shows
- Visible wounds, bandages, scars, missing limbs and prosthetics
- Limping on an injured leg, hopping on one, holding a wounded arm, chest or head

**Freecam**: press F6 for a WASD and mouselook camera. You can still select pawns and give orders.

**Performance**: on a 10600KF and RTX 3060, each pawn on screen adds about 0.18 ms of frame time (about 3 ms for 20 pawns). Skinning runs on the GPU by default.

## Requirements

Load M3AT after all of these:

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- [IsoRim](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715)
- [[NL] Facial Animation - WIP](https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197)
- [[NL] Facial Animation - Experimentals](https://steamcommunity.com/sharedfiles/filedetails/?id=2581693737)

## Compatibility

M3AT is purely visual apart from the optional flinging, so it shouldn't have hard incompatibilities.

- **Built-in support**: all DLCs, Combat Extended (reload and hunker animations, crouching behind cover, all apparel modelled, shields), Romance on the Rim (date animations), [DG]Lovly Animal Cuddling
- **Hair and beard mods**: fully supported, as hair is built from the textures
- **Weapon mods**: IsoRim thickens the sprite into a 3D weapon, and M3AT picks animations by weapon type
- **Clothing mods**: drawn as the closest garment M3AT has, in the right colour. Hats and masks without a model don't show
- **Alien races**: will likely show as ordinary humans

## Reporting a bug

Select the pawn it's happening to, open the mod's options and press **Copy bug report**. It copies your settings, mod list, any errors and what that pawn is doing to the clipboard (and to `<RimWorld config>/M3AT/bug_report.txt`). Nothing is sent anywhere by itself. Paste it in an [issue](https://github.com/MaruMirai/M3AT/issues) or the `#bug-reports` channel on the [Discord](https://discord.gg/7t5Aav2f2e), ideally with a screenshot.

## Building from source

The repository holds both the mod and the tools that make its assets. Only the folder `M3AT: Maru's 3D Animated Toons/` is the mod as the game loads it. To try a local build, symlink that folder into RimWorld's `Mods/` folder.

```
dotnet build Source/M3AT.csproj
```

The build writes `M3AT: Maru's 3D Animated Toons/1.7/Assemblies/M3AT.dll`. The project references RimWorld, Harmony and IsoRim by their Steam install paths under `~/.local/share/Steam/`, so adjust those in `Source/M3AT.csproj` for another machine.

| Path | What it is |
| --- | --- |
| `M3AT: Maru's 3D Animated Toons/` | The shipped mod: About, Defs, Patches, Shaders, the exported model and its animations |
| `Source/` | The C# mod: skinning (CPU and GPU), clip blending, garments, hair builder, ragdolls, freecam |
| `anim_scripts/` | Blender scripts: procedural animation (`walk.py`), mocap import, garment makers, the exporter and a live pose preview |
| `Models/` | The rigged `.blend` and garment `.glb` sources |
| `autotest.sh` | Runs scripted in-game scenarios and saves pictures and a pass/fail report |

The animation is authored in Blender on a Rigify metarig, partly procedural and partly mocap, and exported with:

```
blender -b Models/pawnHumanRig.blend --python anim_scripts/export_frames.py -- "M3AT: Maru's 3D Animated Toons/Models/chibi"
```

[CLAUDE.md](CLAUDE.md) is the full (long) reference for the pipeline, the conventions shared by the exporter and the C#, and how each feature works in the game.

## Credits

- Chibi base model: "Chibi Base Body" by monymensss on [Sketchfab](https://sketchfab.com/3d-models/chibi-base-body-96d80b8fd2f542999b11e94ef4ddd9fb) (CC BY 4.0), rigged and reshaped here
- IsoRim by Peren, whose shaders the `MarusSmooth` shaders are adapted from
- [NL] Facial Animation by Nals

## Licence

MIT, see [LICENSE](LICENSE). Third-party parts keep their own terms, listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). RimWorld and its art belong to Ludeon Studios; none of the game's art is in this repository.

Join the [M3AT Discord](https://discord.gg/7t5Aav2f2e) for news, help and screenshots.
