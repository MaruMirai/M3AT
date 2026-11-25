# Content packs: a race's own parts for M3AT

A **content pack** is a mod that gives M3AT's figures something of their own: a race's ears, tail, face, hair or skin colour, garments for its clothing, or gene parts. The Miho 3D Pack and the Wolfein 3D Pack are content packs.

Players choose, part by part, whether your pack's content or M3AT's own is drawn. They choose in the Content tab of M3AT's options, with the M3AT button on the bottom bar in game, or with M3AT look on a pawn. So make each part as good as you can, and know that a player may prefer M3AT's for some of them.

Read [Apparel.md](Apparel.md) for garments and [GeneParts.md](GeneParts.md) for gene parts. This guide covers the rest and how it all fits together.

## The parts players choose between

| Part | Yours | M3AT's own |
| --- | --- | --- |
| Ears and tail | Your models, moved on M3AT's ear and tail springs | Built from the race's pictures, or modelled for a few popular races |
| Face | Drawn by your code | Facial Animation's face, fitted to the figure (none for a race without Facial Animation faces) |
| Hair | A hairstyle whose sprites M3AT builds the hair from | Built from the race's hair pictures |
| Skin colour | Your colour | The colour the game shows for the race |
| Clothing | Your garments | M3AT's closest garments, painted with the clothing's art |
| Gene parts | Your meshes for genes | M3AT's cat ears, tails and horns |

The defaults are yours, except:
- **Face** is M3AT's where Facial Animation has faces for the race, because M3AT fits those faces to its figure.
- **Armour and helmets** are M3AT's: apparel with a high sharp armour rating, or named a helmet.

Players can change any of these.

## Registering (ExpansionApi 2)

Register from a `[StaticConstructorOnStartup]` class. Garments must be known before the first figure is drawn.

```csharp
using M3AT;

[StaticConstructorOnStartup]
public static class MyPackForM3AT
{
    static MyPackForM3AT()
    {
        if (ExpansionApi.Version < 2) return;   // an older M3AT
        string root = MyMod.Content.RootDir;
        ExpansionApi.RegisterPack("me.mypack")   // your mod's packageId
            .RaceEars("My_Race", pawn => root + "/M3AT/ears_myrace.txt")
            .RaceTail("My_Race", pawn => root + "/M3AT/tail_myrace.txt")
            .Garments(root + "/M3ATgarments")            // optional second argument: where the images are
            .Hair("My_Race", pawn => MyHairFor(pawn))    // null: the pawn's own
            .SkinColour("My_Race", pawn => MySkinFor(pawn))
            .Face("My_Race", (pawn, position, yaw, size, speaking) => MyFace.Draw(pawn, position, yaw, size, speaking));
    }
}
```

- **Files** are in M3AT's own forms: `ears.txt` and `tail.txt` as in M3AT's `Models/chibi`, and garments as in [Apparel.md](Apparel.md). A path may be absolute.
- **Ears and tail** are chosen per pawn, so a race with ear variants can return a different file for each pawn. Return null for none.
- **Garment images:** give a second folder to `Garments` if your images are made at load, for example from another mod's art kept in the save data folder.
- **The face drawer** gets the place, yaw and size M3AT would draw its face patch at, and how far the mouth is open (0 to 1). Return true if you drew it, or false to have M3AT draw its own.
- **Players' choices are respected for you.** M3AT asks you only for the parts the player has set to your pack. You don't need to check the player's choice yourself.

Garments held to M3AT's triangle budget are simplified as they load (2500 for the body, 1500 for the head). See [Apparel.md](Apparel.md), Keep it low poly.

## Packs that patch M3AT instead

A pack that patches M3AT's own methods with Harmony still works, but:
- **Players still choose.** When a player sets a part to M3AT, your patches on the methods for that part are taken off, and put back when they set it to yours again. A patch on a method M3AT doesn't know the part of counts as "Other changes".
- **Updates can break it.** M3AT's private methods change between updates, so your patches can stop working without warning.

Move to the API when you can. Your hooks map to it like this:

| Your patch on | Use |
| --- | --- |
| `EarFileOf` | `RaceEars` |
| `HasCatTail`, `DrawCatTail`, `TailOf`, `StepTail` | `RaceTail` |
| `FaceLayers.Draw` | `Face` |
| `HairBuilder.HairOf` and the HairDef arguments | `Hair` |
| `SkinOf` | `SkinColour` |
| `ReadMesh` (adding garment files) | `Garments` |
| `GeneKeysOf` (a body shape key), `WornHair`/`HidesEars` (hat rims) | Not in the API yet: keep your patch for now |
