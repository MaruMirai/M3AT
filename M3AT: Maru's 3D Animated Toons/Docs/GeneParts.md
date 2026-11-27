# Making gene parts for M3AT

A **gene part** is a mesh M3AT draws on the chibi while the pawn has a certain gene: spikes down the back, scales on the arms, a bony crest, tusks, a tail of armour plates. It is made exactly like a garment ([Apparel.md](Apparel.md)) and goes in the same `M3ATgarments` folder. The only difference is that it names genes instead of apparel.

Read [Apparel.md](Apparel.md) first, especially **Keep it low poly** and **The garment kit for Blender**. M3AT's body is liable to change between updates, but gene parts are not sensitive to changes in the body's faces, so they are likely to be fine when the body is redone. One that sits very close to the skin may still need exporting again: see [When M3AT's body changes](Apparel.md#when-m3ats-body-changes). Gene parts are made with the same early-stage system as player-made clothing, which we're looking to make easier and more robust soon.

## What M3AT already draws

M3AT has its own drawing for some genes.

**A gene part can take M3AT's place for these:**
- Cat, pig and floppy ears: `Ears_Cat`, `Ears_Pig`, `Ears_Floppy`.
- The cat tails: `Tail_Furry`, `Tail_Smooth`.
- The mini horns and the centre horn: `Headbone_MiniHorns`, `Headbone_CenterHorn`.

A gene part for one of these genes is drawn **instead of** M3AT's own, unless the player chooses M3AT's for your mod's gene parts ([RacePacks.md](RacePacks.md)). It doesn't get the springs and flicks M3AT's ears and tails have.

**These can't be replaced yet:**
- The Furskin gene's fur.
- Facial ridges, and the pig nose's nostrils.
- Body shapes for pointed ears, heavy brow, heavy jaw, gaunt head, pig nose, pig hands, elongated fingers and webbed fingers.
- Skin and hair colours.

Every other gene draws nothing on the chibi, so it is free for a gene part. That includes other mods' genes and the base game's genes that have no look of their own.

## How a gene part behaves

- It is drawn while the pawn has **any** of its genes **active**. List several to cover a gene's variants (`genes MyMod_SpikesSmall MyMod_SpikesLarge`).
- Its colour is the pawn's **skin** colour, its **hair** colour, or its **own**: a fixed colour, or its texture as painted.
- It bends, follows body shapes and hides the skin under it, all fitted in the game, as a garment does.
- **Anything worn goes over it.** Its triangles under any worn garment are left out, so a shirt covers arm scales and a helmet covers a crest. Skin under the gene part itself is hidden, so scales don't flicker with the skin under them.
- It is never a stand-in for apparel, and the clothing tool doesn't offer it.
- In bed under a blanket, only what is on the head shows, as with the body.
- It has no animation of its own. It moves with the bones it is weighted to, but has no springs or flicks like M3AT's ears and tails.

## Making one

### Lying on the skin: scales, plating, stripes of armour, warts

Use Approach 2 of [Apparel.md](Apparel.md): select the body's faces and press **Garment from selected faces**.

- Layer **Underwear** (0.003 cells) or **OnSkin** keeps it tight to the skin, under anything worn.
- Add an **extra offset** for a raised plate.
- To make scales or bumps stand out, add a little geometry (extrude a few faces outward, or inset and extrude), and keep the count low.
- In the panel set **Kind** to *Gene part*, **Genes** to the gene's defName, and **Colour** to *Skin* to match the pawn or *Own* for a fixed colour or a painted texture.

### Standing off the body: spikes, crests, tusks, fins, a stub tail

Use Approach 3: model it as its own object around the imported body.

- On the head (horns, crests, tusks, antennae): tick **Rides the head**, so it moves whole with the head and keeps its shape. Make what should show stand well out past the hair: M3AT builds each hairstyle from its sprites, and the hair can stand up to about 0.05 cells off the skin. A hat or helmet worn over it hides what is under it.
- On the back or the arms (spikes, fins): leave **Rides the head** off. Each vertex then takes the weights of the skin nearest it. For a long spike that should stay straight, give it **Own weights** with all of it on one bone (e.g. `spine.002`) and tick **Hard**.
- Root it a little into the skin, so no gap shows when the body is a Thin type.

## The file

The kit writes it, but by hand a gene part's lines are:

```
genes MyMod_BoneCrest
colour_from own
colour 0.85 0.82 0.74
bone spine.006
v ...
f ...
```

| Line | What it does |
| --- | --- |
| `genes A B ...` | The GeneDef defNames it is drawn for: any one active is enough. |
| `colour_from skin` / `hair` / `own` | Its colour. `own` uses its `colour` line, or with a texture and no `colour` line, the texture as painted. |
| `colour r g b` | Its own colour (0 to 1) with `colour_from own`. |
| `texture file.png`, `t` lines | An image, as for a garment. |
| `bone`, `hard`, `rigid`, `own_weights`, `bones`, `w`, `hides`, `hide_bones` | As for a garment (Apparel.md's Settings reference). |

A gene part has no `defs`. `layer`, `groups`, `hide_hair` and `hide_face` lines are ignored on it.

## Testing

- Give a pawn the gene (dev mode's debug actions can add one) and look at it in IsoRim's 3D view.
- The log says `[M3AT] garment <Name> from <your mod>: for the genes ...` when it is read.
- Put the files in the config folder's `M3AT/M3ATgarments/` (see Apparel.md) to try them without a mod.

## Asking for more

Some things need M3AT itself to change: new body shapes for a gene (a gene shape key needs M3AT's body exported again), animated parts, or replacing one of M3AT's own gene looks. Ask on M3AT's Discord or GitHub. Links are on the Workshop page.
