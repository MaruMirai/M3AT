# Mod compatibility

Which other mods M3AT works with, how well, and what each needs. M3AT is an early prototype, so "full" below means "built for and checked in the game", not "finished".

The options named here are in M3AT's mod settings. Most mod-specific ones are in the folded **Mod compatibility** section at the bottom of the second column.

## How well each mod is supported

| Level | Meaning |
|---|---|
| **Required** | M3AT needs it. |
| **Full** | M3AT has work made for this mod, checked in the game. |
| **Partial** | Some of the mod's things have their own support, others fall back to the general rules. Known gaps are listed. |
| **General** | Nothing made for this mod in particular. Its things are handled by the rules M3AT uses for any mod (below). Checked in the game. |
| **Untested** | Should work through the general rules, but nobody has checked yet. |

## At a glance

Every mod, grouped by category, with its status, what else it needs and its known gaps. The same list is in [Compatibility.csv](Compatibility.csv), for anyone who wants it as a spreadsheet. The detail for each mod is further down.

The **Clothes** column says how a mod's apparel is drawn:

- **Custom models:** M3AT has 3D garments made for that apparel.
- **Painted match:** the apparel is drawn as the closest of M3AT's garments, chosen by body coverage and layer, then painted with the apparel's own sprites (front, sides and back), so colours and patterns follow the original. The shape is the closest garment's, not a new one. This is the **Paint stand-in clothes with their own art** option (on by default, still a prototype), and the clothing tool lets players pick a different garment or turn the painting off per apparel.
- **Mixed:** custom models for some of the mod's apparel, a painted match for the rest.

<!-- compat-table:begin (made by anim_scripts/compat_table.py from Compatibility.csv; edit the CSV) -->

### Required

| Mod | Status | Also needs | What works | Known gaps | Settings |
|---|---|---|---|---|---|
| [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) | Required |  |  |  |  |
| [IsoRim Waylith](https://steamcommunity.com/sharedfiles/filedetails/?id=3819577715) | Required |  | M3AT replaces IsoRim's person figure with the animated chibi | A too-old IsoRim keeps its own figures (logged at start-up) |  |
| [[NL] Facial Animation](https://steamcommunity.com/sharedfiles/filedetails/?id=1635901197) | Required |  | Eyes, mouths, brows and expressions on the 3D head | Without it heads are bare |  |
| [[NL] Facial Animation - Experimentals](https://steamcommunity.com/sharedfiles/filedetails/?id=2581693737) | Required | Facial Animation | Its faces fitted to the chibi's head | Closed mouth is very small | Crisper mouths (Advanced) |

### Faces

| Mod | Status | Also needs | What works | Settings |
|---|---|---|---|---|
| [Facial Animation - Retexture](https://steamcommunity.com/sharedfiles/filedetails/?id=3271240549) | Full | Facial Animation | The layout M3AT's faces are fitted to; drawn as is | Face tuning window (Advanced) |

### Races

| Mod | Status | Clothes | Also needs | What works | Known gaps | Settings |
|---|---|---|---|---|---|---|
| [Humanoid Alien Races (any race)](https://steamcommunity.com/sharedfiles/filedetails/?id=839005762) | Partial (early) | Painted match | The race's own Facial Animation mod for its face | Races without an IsoRim pack drawn as 3D chibis: ears, tails, horns, wings built from their sprites and animated, race hair built in 3D, faces fitted | Untested races may look off; races on IsoRim's Keep as paper list stay cards | Other races as 3D pawns (early); Race parts from their pictures |
| [NewRatkinPlus (Ratkin)](https://steamcommunity.com/sharedfiles/filedetails/?id=1578693166) | Full | Painted match | HAR; Hyacinth's Ratkin Facial Animation | Modelled round mouse ears and thin tail; hem lengths for its skirts | Closed mouth is a pale blob; fringes can hide the eyes | Modelled ears and tails for popular races |
| [Wolfein Race](https://steamcommunity.com/sharedfiles/filedetails/?id=3473140562) | Full | Painted match | HAR; Wolfein Race Facial Animation | Modelled pointed ears and bushy pale-tipped tail | Folded ear variants come out flat; fringes can hide the eyes | Modelled ears and tails for popular races |
| [Milira Race](https://steamcommunity.com/sharedfiles/filedetails/?id=3256974620) | Full | Painted match | HAR; Ancot Library; Milira Race Facial Animation | Modelled folded wings, glowing halo; female body type drawn as Female | A 1.6 mod; hair breaks up over the face; mouths faint | Modelled ears and tails for popular races |
| [Kiiro Race](https://steamcommunity.com/sharedfiles/filedetails/?id=2988200143) | Full | Painted match | HAR; Ancot Library; IsoRim Waylith - Kiiro Pack; Kiiro Race Facial Animation | Cat-like: the chibi's cat ears and tail with pale lining and tufts; hair add-ons built in 3D; its underwear matched by name | Mouths faint |  |
| [IsoRim Waylith - Wolfein 3D Pack](https://steamcommunity.com/sharedfiles/filedetails/?id=3844820269) | Untested |  | Wolfein Race | A race with an IsoRim pack goes by the pack, not M3AT's race rule | Not yet checked with M3AT |  |
| [IsoRim Waylith - Miho 3D Pack](https://steamcommunity.com/sharedfiles/filedetails/?id=3843413189) | Untested |  | Miho, the celestial fox | A race with an IsoRim pack goes by the pack, not M3AT's race rule | Not yet checked with M3AT |  |

### Clothing and hair

| Mod | Status | Clothes | Also needs | What works | Known gaps | Settings |
|---|---|---|---|---|---|---|
| [GrimWorld 40,000 (1.7 uploads)](https://steamcommunity.com/sharedfiles/filedetails/?id=3833534374) | Partial | Painted match | GrimWorld 40,000 - Framework | Space Marines drawn as giants, Custodes and Valdor scaled up, crouching under ceilings, headgear matched by sprite, storm shields held as CE's | Most armour drawn as the nearest vanilla garment | GrimWorld 40,000: Space Marines are giants; Armoured size; Armour drawn bigger makes the wearer bigger; Crouch for ceilings |
| [Boots and Stuff](https://steamcommunity.com/sharedfiles/filedetails/?id=1488970545) | Full | Custom models |  | Its footwear has its own 3D garments |  |  |

### Combat and weapons

| Mod | Status | Clothes | Also needs | What works | Settings |
|---|---|---|---|---|---|
| [Combat Extended](https://steamcommunity.com/sharedfiles/filedetails/?id=3495749827) | Full | Custom models |  | Cover crouching, hunkering under suppression, reload animation, melee and ballistic shields held and carried, backpack, radio pack and tactical vest, one-handed and grenade tags, bows' second warm-up | Retextured weapons as 3D models |
| [IsoRim Waylith - Combat Extended Pack](https://steamcommunity.com/sharedfiles/filedetails/?id=3831314143) | Full |  | Combat Extended | Works as is; an older GitHub version removed vanilla weapon models, which M3AT puts back | Retextured weapons as 3D models |

### Activities and world

| Mod | Status | Also needs | What works | Known gaps | Settings |
|---|---|---|---|---|---|
| [Dubs Bad Hygiene](https://steamcommunity.com/sharedfiles/filedetails/?id=836308268) | Full | IsoRim Waylith - Dubs Bad Hygiene Models | Washing undressed, lying back in baths and hot tubs, swimming and wading in pools, vaulting pool walls | Without the IsoRim pack fixtures aren't placed in 3D |  |
| [IsoRim Waylith - Dubs Bad Hygiene Models](https://steamcommunity.com/sharedfiles/filedetails/?id=3827392706) | Full | Dubs Bad Hygiene | Places pawns on toilets and in baths, tubs and pools for M3AT |  |  |
| [Romance On The Rim](https://steamcommunity.com/sharedfiles/filedetails/?id=2654432921) | Full |  | Kisses, snowball fights, pillow talk, shared reading, listening to performances | Skygazing, strolls and swims use the ordinary animations |  |
| [[DG]Lovly Animal Cuddling](https://steamcommunity.com/sharedfiles/filedetails/?id=3823442009) | Full |  | Animal held on the arm and stroked |  |  |
| [IsoRimZ](https://steamcommunity.com/sharedfiles/filedetails/?id=3824503834) | Full |  | Tall pawns crouch under the floor above; freecam jumps to upper floors |  | Crouch for ceilings |

### Any other mod

| Mod | Status | Clothes | Also needs | What works | Known gaps | Settings |
|---|---|---|---|---|---|---|
| Any Facial Animation face set | General |  | Facial Animation | Recognised as human faces and fitted to the chibi's head; overlays such as makeup, freckles and blush stay put, and pieces on the mouth or eyes move with them | A set with unusual proportions may need the face tuning window | Face tuning window (Advanced) |
| Any apparel mod | General | Painted match |  | Drawn as the nearest M3AT garment, shape-matched and painted with the clothing's own sprites; packs, webbing and shields recognised; mods can add real 3D garments (Docs/Apparel.md) | The painted look is a prototype; the shape is the nearest garment's | Clothing tool |
| Any hair mod | General |  |  | Hair and beards built in 3D from its sprites automatically |  | Hair detail |
| Any weapon mod | General |  |  | IsoRim's 3D model if it has one, else the sprite on a slab; stance by tags and size; grips found from the art; holstered on the body | Some grips may need the grip tool | Find grips on other mods' weapons; Weapons worn on the body |

<!-- compat-table:end -->

## Required mods

- **Harmony** and **IsoRim**. M3AT replaces IsoRim's 3D person figure, so it does nothing without IsoRim. If the installed IsoRim is too old, M3AT logs it and leaves IsoRim's own figures. Some features need newer IsoRim versions still (blankets in bed, the first-person eye); keep IsoRim up to date.
- **[NL] Facial Animation** and **Facial Animation - Experimentals** draw the faces. M3AT puts their eyes, mouths, brows and expressions on the 3D head. They are listed as required, but M3AT still runs without them; the heads are then bare.

## Faces

- **[Facial Animation - Retexture](https://steamcommunity.com/sharedfiles/filedetails/?id=3271240549)**: the layout M3AT's faces are fitted to. Its faces are drawn as they are.
- **Experimentals' own art** is scaled and lowered to match. Experimentals' closed mouth is still very small.
- **Any other Facial Animation face set** is recognised as a human face and fitted the same way.
- **Race face mods** (a race's own Facial Animation art): eyes are resized, raised and spaced to a human's, and the mouth moved to the human row. Soft, glowing 2D mouths are redrawn with crisp lines (**Crisper mouths**, Advanced).
- **Overlays** such as Wanted: A facial anim. Extension (makeup, freckles, blush) stay where they are drawn; pieces over the mouth or eyes move with them.
- Anything that looks off can be moved and sized per mod in the **face tuning window** (Advanced section). The first change pins the automatic fit, so later M3AT updates don't shift your tuning.

## Combat Extended

Full support. CE is detected by its packageId (`CETeam.CombatExtended`) and read by reflection, so nothing breaks without it.

- **Cover and suppression**: pawns crouch in CE's crouching jobs with an enemy in sight; when CE makes them hunker down, they dive flat and stay down.
- **Reloading** plays a reload animation with the weapon being reloaded in the hands, once per CE wait (per round for weapons loaded a round at a time).
- **Shields**: CE's melee and ballistic shields are held in the left fist when drafted, raised into a guard, and carried on the back otherwise.
- **Gear**: CE's backpack, radio pack and tactical vest have their own 3D garments, and the tactical vest is fitted over armour. Webbing and chest rigs from other mods are drawn as CE's vest, and packs from other mods as CE's backpack.
- **Weapons**: CE's one-handed tag picks the one-handed stance and grip; its grenade tags pick throwing. CE's grenades use IsoRim's grenade model. Bows hold at full draw through CE's second warm-up.
- **Retextured weapons**: CE redraws every vanilla gun, and IsoRim then draws them as flat sprites. **Retextured weapons as 3D models** (on by default) keeps IsoRim's 3D models for the game's own weapons. An older IsoRim CE pack from GitHub removed those models; M3AT puts them back. The Workshop pack doesn't need this.

## Other races (Humanoid Alien Races)

Early. **Other races as 3D pawns (early)** is on by default. IsoRim draws a humanlike race other than Human as a flat card unless an IsoRim pack makes it a figure. With this option, M3AT draws every race that has no IsoRim pack as a 3D chibi. IsoRim's "Keep as paper" list still takes precedence. A race with its own IsoRim 3D pack (the Kiiro, Wolfein and Miho packs) is drawn as its pack decides. Of those, only the Kiiro pack has been checked with M3AT.

What each race gets:
- **Body**: the human chibi body. A race's body type is drawn as the game's one its name says (Milira_Female as Female); other body types keep the plain shape. Sizes follow the race's life stages.
- **Race parts** (ears, tails, horns, wings and other HAR body add-ons; **Race parts from their pictures**, on by default): built in 3D from the race's own sprites. Built ears and tails move like the gene ears and tails. Glowing parts (Milira's halo) are drawn as glowing cards.
- **Hair**: races that draw hair as add-ons (Kiiro) get it built in 3D like ordinary hair. Ordinary hair on a head sprite unlike a human's is fitted to the chibi's head.
- **Face**: fitted as above (Faces).
- **Clothes**: race clothes go through the general apparel rules; race skirts and dresses are measured for their hem length.

Races checked in the game:

| Race | Mod | What's special |
|---|---|---|
| Ratkin (`Ratkin`) | NewRatkinPlus | Modelled round mouse ears and thin tail |
| Wolfein (`Wolfein_Race`) | Wolfein | Modelled pointed ears and bushy pale-tipped tail |
| Milira (`Milira_Race`) | Milira (a 1.6 mod that loads in 1.7), AncotLibrary | Modelled folded wings |
| Kiiro (`Kiiro_Race`) | Kiiro, with IsoRim's Kiiro pack | Treated as cat-like: the chibi's own cat ears and tail, smaller with a pale lining and tufts; its underwear is matched by name |

The modelled parts can be turned off with **Modelled ears and tails for popular races**, which shows the parts built from pictures instead.

Known gaps: Wolfein's folded ears come out as a flat plank; long race fringes can hide the eyes; Milira's hair breaks up over the face; Ratkin's closed mouth is a pale blob. Races not in the table are untested beyond the general rules.

## GrimWorld 40,000

Partial: checked with the Cadians, Krieg, Space Marines, scouts, Custodes and Valdor.

- **GrimWorld 40,000: Space Marines are giants** (on): a plain human in GrimWorld's Spacer armour over torso, arms and legs is drawn as a Marine, bigger (**Armoured size**, 1.25) and more muscled, with the head kept its normal size. Scouts, Sisters of Silence and robes aren't bulked.
- **Armour drawn bigger makes the wearer bigger** (on): body armour that the mod draws scaled up (the Custodes 1.1, the Emperor 1.2, Valdor 1.4) scales the whole pawn.
- **Crouch for ceilings**: a pawn too tall for the room crouches and stoops under the ceiling. Default: only while the floor above is shown.
- Headgear is matched to garments by its sprites; gorgets fit over armour; hand-held shields (the storm shield) are held and carried as CE's shields are.
- Most armour is a painted match, not a custom model.

## Dubs Bad Hygiene

Full, with **the IsoRim pack for DBH** (`james.isorimwaylith.dbhmodels`, Workshop 3827392706), which places pawns on toilets and in baths, hot tubs and pools. Without the pack, DBH's fixtures aren't placed for 3D.

- Washing, baths and hot tubs: drawn undressed, lying back in the water with the head out.
- Raised pools: swimmers swim, anyone else in the pool wades on its floor, chest deep. Pawns walking across a pool wall vault it.

## Romance on the Rim

Full (`telardo.RomanceOnTheRim`).

- Kisses: the two draw together and the taller nods down to the shorter.
- Snowball fights, pillow talk (drawn as lovin), shared reading (the reader holds the book out, both lean together) and listening to a performance (listeners step closer and turn to the performer).
- Skygazing, strolls and swims use the ordinary lying, walking and swimming.

## [DG] Lovly Animal Cuddling

Full (`local.animalcuddling`, Workshop 3823442009). A cuddled animal is held on the left arm and stroked while the pawn stands still.

## IsoRimZ (stacked floors)

Full. Pawns too tall for the room crouch under the floor above (see **Crouch for ceilings**). The freecam's camera jumps go to the upper floor's height.

## Boots and Stuff

Full (`Techmago.BootsnStuff`). Its footwear has its own 3D garments.

## Any other mod: the general rules

These apply to every mod, including the ones above where nothing more specific exists.

- **Weapons**: drawn as IsoRim's 3D model if IsoRim has one, otherwise as the weapon's sprite on a thin slab (sized by **slab scale**). Stance and grip go by the weapon's tags, name and size (pistol, rifle, heavy, one- or two-handed melee, bow, thrown). **Find grips on other mods' weapons** (on) finds where the hands go from the sprite. A grip that is still wrong can be set by hand with the grip tool. Weapons are holstered on the body (**Weapons worn on the body**).
- **Apparel**: apparel without its own 3D garment gets a painted match (see above). The nearest garment is chosen by body coverage, layer, armour and tech level, and the clothing tool lets players choose a different one or none. Packs, webbing, belts and shields are recognised by name and layer first.
- **Hair and beards**: built in 3D from the sprites of any hair mod you have loaded. No hair mod needs support of its own.
- **Genes**: the game's ear, tail, horn, fur, ridge and head-shape genes are drawn; genes from other mods have no 3D part unless a mod adds one (below).
- **Not drawn by M3AT**: anything IsoRim doesn't draw as a person figure (races kept as paper, statues, non-humanlikes). Those stay as IsoRim draws them.

## For modders: adding support from your own mod

You don't need to change M3AT to support your mod:

- **3D garments and retextures** for any apparel: put `garment_<Name>.txt` files in an `M3ATgarments` folder at your mod's root. See [Apparel.md](Apparel.md).
- **Gene parts** (scales, spikes, crests, tusks): the same folder. See [GeneParts.md](GeneParts.md).
- **Hand-made hair**: an `M3AThair` folder at your mod's root takes the place of the hair M3AT would build.
- **Animations**: M3AT's expansion API (`M3AT.ExpansionApi`) lets a mod add clips in M3AT's format and pose the chibi for its own jobs.

## Reporting a problem with another mod

Use **Copy bug report** (Problems section of the settings). It records the related mods, your load order, the selected pawns and M3AT's log, and lists which mod each held weapon comes from.

---

### For maintainers

Keep this page and `Compatibility.csv` up to date when support for a mod is added or changes. Detail is in `dev_notes/` in the repository:

| Mod | Notes | Code |
|---|---|---|
| Facial Animation, face sets, race faces | `faces.md` | `FaceLayers.cs`, `FaceGaze.cs`, `FaceSetFit.cs`, `FaceRestyle.cs`, `FacePins.cs`, `FaceTuning.cs` |
| Combat Extended | `weapons.md` (cover, hunkering, reloading), `apparel.md` (shields, packs, webbing), `options.md` (retextured weapons) | `WalkFigures.cs`, `Shields.cs`, `LetOut.cs`, `RestoredWeaponModels.cs` |
| HAR races | `body.md` (other races, race parts), `hair.md` (race hair) | `OtherRaces.cs`, `AddonParts.cs`, `AddonBuilder.cs`, `RaceTails.cs`, `AddonHair.cs`, `EarLining.cs` |
| GrimWorld 40,000 | `body.md` (armour size, ceilings), `apparel.md` | `ArmourSize.cs`, `StandIns.cs` |
| Dubs Bad Hygiene | `motion.md` | `Hygiene.cs` |
| Romance on the Rim, Animal Cuddling | `motion.md` | `RomanceOnTheRim.cs`, `WalkFigures.cs` |
| Mods' garments, gene parts, hair | `apparel.md`, `hair.md` | `ModGarments.cs`, `HairBuilder.cs` |
| Expansion API | `options.md` | `ExpansionApi.cs`, `ExpansionClips.cs` |

Each mod above has an autotest scenario (`dev_notes/autotest.md`; extra mods via `M3AT_ADD_MODS`). `BugReport.Related` lists the mods the bug report calls out.
