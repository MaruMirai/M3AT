# M3AT: Maru's 3D Animated Toons

*Animated 3D pawns for [IsoRim Waylith](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715).*

![M3AT preview](M3AT%3A%20Maru%27s%203D%20Animated%20Toons/About/Preview.png)

**Early WIP and just launched**, so expect some janky or generic animations and other scuff.
Screenshots use [Facial Animation - Retexture](https://steamcommunity.com/sharedfiles/filedetails/?id=3271240549).

## Main features

- **Animations** for most things, including movement, combat, jobs, and recreation
- **Apparel models** for everything in the base game, all DLCs and Combat Extended
- **All hair mods supported**, with on-the-fly sprite-to-3D conversion; hair shows under hats
- **Carrying, aiming and firing animations** for all of IsoRim's 3D weapons, or sprite-generated slab versions
- **Holstered weapons** visible (several at once with CE), with draw and holster animations
- **Body types**: one for each of RimWorld's, with gender differences and optional variation
- **Equipment packs** visible when worn, using IsoRim's models
- **Injuries**, bandages, scars and missing limbs; wounds match their type, location and severity
- **Prosthetic limbs**: peg legs, wooden, simple, bionic and archotech parts
- **Gene parts** modelled and animated
- **Injury-reactive** idle and movement animations
- **Ragdolls**, with optional physics flinging
- **Smooth growth for Biotech**: smooth growth from baby to adult, and visible pregnancy
- **Size and build sliders** for pawns and heads
- **Animated faces** powered by [NL] Facial Animation: blinking, moods, expressions and more
- **Full Combat Extended support**
- **Freecam mode**: press **F6** for a WASD-mouselook freecam, designed for play with the more detailed pawns. Orders and selections work with the crosshair. The normal pointer comes back when a menu opens, or at the press of a button.

## Performance

Models have been kept as light as possible without looking poor, and various optimizations have been made to reduce impact.
In benchmarks on a 10600KF and 3060, each pawn on screen currently adds about 0.18 ms of frame time (around 3 ms for 20 pawns on screen).

## Requirements

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- [IsoRim Waylith](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715)
- [[NL] Facial Animation - WIP](https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197)
- [[NL] Facial Animation - Experimentals](https://steamcommunity.com/sharedfiles/filedetails/?id=2581693737)

Load M3AT after all of them.

## Mod integration

- **[Combat Extended](https://steamcommunity.com/sharedfiles/filedetails/?id=3495749827)**: all apparel modelled, extra holstered weapons show, backpacks work around holsters and equipment, shields are worn and held, reload and hunker animations, and the webbing layer fits over armour. Also try [IsoRimWaylith-CEPack](https://github.com/peren717/IsoRimWaylith-CEPack) for the best integration.
- **[Romance on the Rim](https://steamcommunity.com/sharedfiles/filedetails/?id=2654432921)**: various animations for the dates
- **[[DG]Lovly Animal Cuddling](https://steamcommunity.com/sharedfiles/filedetails/?id=3823442009)**: petpetpet

## Serving suggestions

- **[Facial Animation - Retexture](https://steamcommunity.com/sharedfiles/filedetails/?id=3271240549)**: used in the screenshots; gives more face variety and a different style to FA
- **[Pipette Tool](https://steamcommunity.com/sharedfiles/filedetails/?id=2787342464)**: a huge QoL improvement, and works fine in the freecam

## Compatibility

M3AT is purely visual aside from the optional flinging, so it shouldn't have any hard incompatibilities.
For mods that add content:

- **Hair and beard mods**: all fully supported; the 3D hair is generated from the hair textures.
- **Weapon mods**: fairly good support. IsoRim generates a 3D version by thickening the sprite, and M3AT guesses the animations from its type.
- **Clothing mods**: falls back to drawing a similar garment M3AT has, in the right colour. Hats and masks won't show.
- **Alien race mods**: will likely just show as ordinary humans.

## Links and bug reports

- [Discord](https://discord.gg/7t5Aav2f2e): discussion, support and suggestions
- [Issues](https://github.com/MaruMirai/M3AT/issues): bug reports

To include some helpful info when you have an issue, select a pawn, then open the mod's options and press **Copy bug report**. This copies text to your clipboard for use on the Discord or GitHub. It won't send data anywhere.

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

- **Peren717**: [IsoRim Waylith](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715), and for expanding its API to better support M3AT
- **Kndzio**: the original [IsoRim](https://steamcommunity.com/sharedfiles/filedetails/?id=3819266222), whose shaders the `MarusSmooth` shaders are adapted from
- **Nals**: [Facial Animation](https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197)
- **Monymensss** (Sketchfab): the chibi base model, ["Chibi Base Body"](https://sketchfab.com/3d-models/chibi-base-body-96d80b8fd2f542999b11e94ef4ddd9fb) (CC BY 4.0), rigged and reshaped here
- **Claude**: nearly everything else

## Licence

MIT, see [LICENSE](LICENSE). Third-party parts keep their own terms, listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). RimWorld and its art belong to Ludeon Studios; none of the game's art is in this repository.
