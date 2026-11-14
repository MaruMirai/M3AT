# M3AT: Maru's 3D Animated Toons

*Animated 3D pawns for [IsoRim Waylith](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715).*

![M3AT preview](M3AT%3A%20Maru%27s%203D%20Animated%20Toons/About/Preview.png)

Early WIP, Main features:

- Animations for most actions (some currently basic)
- Wearable 3D apparel and sprite-to-3D hair
- Correct holds for IsoRim's 3D weapons, Mod weapon grip approximation
- Visible holstered weapons and IsoRim equipment
- Body types, gene parts, Biotech growth, prosthetics
- Visible injuries and injury-reactive animations
- Ragdolls and flinging
- CE support
- Freecam mode

## Requirements

- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- [IsoRim Waylith](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715)
- [[NL] Facial Animation - WIP](https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197)
- [[NL] Facial Animation - Experimentals](https://steamcommunity.com/sharedfiles/filedetails/?id=2581693737)

Load M3AT after all of them.

## Links

Discord: https://discord.gg/7t5Aav2f2e
GitHub: https://github.com/MaruMirai/M3AT

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

- **Peren717**: [IsoRim Waylith](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715), thanks for expanding its API to support M3AT
- **Kndzio**: the original [IsoRim](https://steamcommunity.com/sharedfiles/filedetails/?id=3819266222), whose shaders the `MarusSmooth` shaders are adapted from
- **Nals**: [Facial Animation](https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197)
- **Monymensss** (Sketchfab): the chibi base model, ["Chibi Base Body"](https://sketchfab.com/3d-models/chibi-base-body-96d80b8fd2f542999b11e94ef4ddd9fb) (CC BY 4.0), rigged and reshaped for M3AT
- **Claude**: most of the hard work

## Licence

MIT, see [LICENSE](LICENSE). Third-party parts keep their own terms, listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). RimWorld and its art belong to Ludeon Studios; none of the game's art is in this repository.
