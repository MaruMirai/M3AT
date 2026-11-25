# Making apparel for M3AT

M3AT draws every person as an animated 3D chibi, and each piece of apparel it knows is a 3D **garment**: a mesh that sits on the chibi's body and bends with it. This guide is for modders who want their apparel (or the game's, or another mod's) to show up as a garment. It also covers retexturing the garments M3AT already has.

You need **no C# and no XML**. A garment is a plain text file in a folder in your mod. If M3AT isn't loaded, the folder is ignored, so you don't need M3AT as a dependency.

Gene parts (spikes, scales, crests and so on drawn for a gene) are made the same way. See [GeneParts.md](GeneParts.md) after this guide.

## Contents

1. [How M3AT draws apparel](#how-m3at-draws-apparel)
2. [Where the files go](#where-the-files-go)
3. [Keep it low poly](#keep-it-low-poly)
4. [The garment kit for Blender](#the-garment-kit-for-blender)
5. [Approach 1: Retexture a M3AT garment](#approach-1-retexture-a-m3at-garment)
6. [Approach 2: Push out the body's own faces](#approach-2-push-out-the-bodys-own-faces)
7. [Approach 3: A separate mesh](#approach-3-a-separate-mesh)
8. [Settings reference](#settings-reference)
9. [The file format](#the-file-format)
10. [Testing and troubleshooting](#testing-and-troubleshooting)

## How M3AT draws apparel

- A garment is drawn for apparel by its **defName**, its `defs` line. A garment can name several defs, for example a shirt and its child-sized version.
- Apparel with no garment of its own gets **stand-ins**: the M3AT garments that best match its layer, body part groups and kind. So a mod's apparel already shows as *something*, and a garment of its own makes it show as itself. Your garment joins the stand-ins too: it may be picked for other apparel like yours, judged by your apparel's def. Its `layer` and `groups` lines add more layers and groups it can stand in for, and are the only way to make a garment with no loaded def a stand-in.
- Most of the fitting happens **in the game**. Your file gives the shape (vertices, triangles and texture places). When the game loads, M3AT works out:
  - **Weights**: each vertex bends with the skin under it, so the garment follows every animation.
  - **Body shapes**: the shape M3AT gives the garment for each body type, gender, age and gene shape (Male, Female, Thin, Fat, Hulk, baby limbs, pregnancy and so on).
  - **Hidden skin**: the body triangles under the garment are left out while it is worn, so skin can't poke through.

  Because of this, your garment keeps fitting when a M3AT update changes the body.
- Garments over garments: an outer layer hides what is under it in the same way. A shell coat hides the shirt under it.
- Colour: an untextured garment is drawn in the apparel's colour. A textured garment's image is multiplied by the apparel's colour if the apparel is made from stuff or dyed. Paint it light grey for that, or turn tint off.
- The look: flat-shaded, toon-like, with a dark outline round the figure. Plain colours and simple painted detail suit it better than photo-real textures.

## Where the files go

```
YourMod/
  About/About.xml
  Defs/...
  M3ATgarments/
    garment_FancyCoat.txt     <- one file per garment
    garment_FancyCoat.png     <- its image, if it has one
    garment_Bandana.txt
```

- The folder is `M3ATgarments` at your mod's **root** (beside `About`), not inside a version folder.
- Each `.txt` in it is a garment. The name after `garment_` is the garment's name. It must be different from M3AT's own garments (their names are the file names in M3AT's `Models/chibi/`) and from other mods' garments.
- **Trying things out without a mod:** put the files in the game's config folder, under `M3AT/M3ATgarments/`. On Windows that is `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config\M3AT\M3ATgarments\`. On Linux it is `~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/M3AT/M3ATgarments/`. They are read there just as a mod's are.
- Garments are read when the first pawn is drawn, so restart the game after changing one.
- **Replacing a M3AT garment:** if your garment's `defs` names apparel that M3AT already has a garment for, yours is drawn in its place. This is how a texture pack or a better model of a vanilla item works. If two mods replace the same apparel, the one later in the load order wins.

## Keep it low poly

This matters more than anything else in this guide.

Every garment is skinned again for every pawn whenever it is posed, and it is drawn twice (the solid mesh and its outline). It also takes part in the hiding tests against the body and against every other garment worn with it. A colony of twenty pawns in heavy garments costs real frame time.

| | Triangles |
| --- | --- |
| The whole chibi body | about 1200 |
| Shirt, trousers, simple hat (M3AT's own) | 120 to 400 |
| Coat, duster, robe | 500 to 900 |
| Full power armour | about 1000 |
| A small accessory (a collar, a strap, a badge) | under 150 |

Aim for these. **M3AT's budget is 2500 triangles for a garment worn on the body and 1500 for one worn on the head.** A garment over it is simplified automatically as the game loads, which keeps the cost down but can spoil its shape, and the game's log names it. Players can choose to keep garments at full detail, up to 30000 triangles, in the mod options. The kit tells you the count and warns past the budget. The figure is small on screen and outlined, so detail you'd model on a normal character turns to noise here. Put fine detail in the texture instead.

**Approach 2 is the cheapest way to get a garment that fits.** The pushed-out copy of the body's faces has exactly as many triangles as the patch of skin it covers. Each of its vertices also sits straight out from a vertex of the skin, so it bends with exactly the same weights and can't clip into the body at a bent knee or elbow. A sculpted or shrink-wrapped mesh needs many more triangles to follow the body as closely, and it still clips where its vertices don't line up with the skin's.

If you have a high-poly model, decimate it in Blender (the Decimate modifier, Collapse, then apply it) to the figures above before exporting.

## The garment kit for Blender

`Docs/Tools/m3at_garment_kit.py` in M3AT's folder is a small Blender add-on (Blender 4.2 or newer). It does the work of the approaches below without M3AT's own rig.

To install it: **Edit > Preferences > Add-ons > Install from Disk** and pick the file. Or open it in Blender's Text Editor and press **Run Script**, which lasts until you close Blender. It adds an **M3AT** tab to the 3D view's sidebar (press **N**).

1. Set **M3AT folder** to M3AT's mod folder. For the Workshop copy that is `steamapps/workshop/content/294100/3839520750`.
2. Set **Export to** to your mod's `M3ATgarments` folder.

Its buttons:

- **Import body**: the chibi's body (`M3AT_Body`) in its rest pose, with every body shape as a shape key and a vertex group per bone. It also brings in an armature (`M3AT_Rig`) you can pose in Pose Mode to see how a garment bends. **1 Blender unit is 1 map cell**, and the figure faces -Y (Blender's front view). The rest pose is an A-pose: arms angled down and out, legs a little apart.
- **Import garment**: one of M3AT's own garments (`Models/chibi/garment_<Name>.txt`), with its texture places and image.
- **Garment from selected faces**: Approach 2.
- **Export selected**: writes each selected mesh as `garment_<ObjectName>.txt`, plus its image if its material has one on Base Color, using the settings in the panel.

Body shape keys on your garment are ignored. M3AT fits every body shape itself, so you only model the base shape (the `Basis`). If you want to check a shape, set the body's shape key in Blender: it shows what the body does, not what your garment will do.

## Approach 1: Retexture a M3AT garment

Use this to give your apparel a new look on a mesh M3AT already has. For example, your mod's fancy coat could use the duster's shape with its own painting.

**With the kit:**

1. **Import garment**, and pick M3AT's `Models/chibi/garment_Duster.txt` (or any other).
2. Rename the object (this becomes your garment's name, e.g. `FancyCoat`).
3. Paint its texture: in Blender's Texture Paint, or export the UV layout (UV Editor > UV > Export UV Layout) and paint it in any image editor. Save the image and set it on the material's Image Texture node.
4. In the M3AT panel set **Defs** to your apparel's defName. Leave **Retexture only** ticked (it is ticked on import).
5. **Export selected.**

**By hand**, the whole file is just:

```
base Duster
defs MyMod_Apparel_FancyCoat
texture garment_FancyCoat.png
tint no
```

`base` takes the M3AT garment's mesh, texture places, weights, shapes and hiding. Your lines go over its own. Leave out `texture` to keep its image, which lets you put M3AT's duster on your apparel with nothing else. You can also override `layer`, `groups`, `tint`, `colour`, `hide_hair` and `hide_face`.

Notes:

- The image must use the base garment's UV layout. Start from its own image (`Models/chibi/garment_<Name>.png`) to see where things go.
- Many of M3AT's garments are untextured: drawn in one colour. Those have no texture places, so they can't take an image. Use Approach 2 or 3 instead, or **Import garment**, unwrap it, untick **Retexture only** and export it as a mesh of its own.
- If a later M3AT update remakes the base garment's mesh, its UV layout may change and your painting would no longer line up. A garment exported as a mesh of its own (untick **Retexture only**) doesn't depend on M3AT's, but it doesn't get M3AT's fixes to that garment either.

## Approach 2: Push out the body's own faces

This is the best way to make anything that lies on the skin: shirts, trousers, leggings, gloves, vests, bodysuits, a sleeve or a sock. M3AT's own shirts, trousers, jackets, vests and vacsuit are made this way.

1. **Import body.**
2. Select `M3AT_Body`, go to Edit Mode and switch to face select (press **3**).
3. Select the faces the garment covers. Some tips:
   - Turn on **X-ray** (Alt+Z) to select the back as well.
   - With **Mesh Symmetry X** on, or **Select > Select Mirror** afterwards, both sides match.
   - **Select > Select All by Trait** and **Select Linked** (L) help on the limbs.
   - To start from a part of the body, use the vertex groups (Object Data properties > Vertex Groups > **Select**): `upper_arm.L`, `forearm.L`, `thigh.R`, `spine.002` and so on.
   - The body is low poly, so a hem or neckline follows its edges. That is part of the look. If you want a different line, cut the *garment* afterwards (step 6), never the body.
4. In the M3AT panel press **Garment from selected faces**. Give it a name, pick its apparel **layer**, and optionally an **extra offset** for a bulkier garment.

   It copies the faces and pushes each vertex straight out along the skin's normal:

   | Layer | Off the skin (cells) | For |
   | --- | --- | --- |
   | Underwear | 0.0030 | UnderWhere's layers |
   | OnSkin | 0.0042 | shirts, trousers |
   | Middle | 0.0085 | vests, armour under a coat |
   | Shell | 0.0127 | jackets, coats |
   | Webbing | 0.0170 | Combat Extended's webbing |

   These keep each layer just over the one under it, so the outer garment covers it and the game's hiding leaves the inner one out under it.
5. Set **Defs** to the apparel.
6. Optional edits. These are all fine, because M3AT fits every vertex to the skin under it in the game:
   - Knife-cut a nicer hem and delete what's past it.
   - Move a vertex *outward* to loosen a cuff, or flare a hem out a little.
   - Add a few loops for a collar or a pocket that stands off.

   Don't pull vertices inward toward the skin. Keep the triangle count near the patch's own.
7. Unwrap it (UV > Smart UV Project is fine, or mark seams and Unwrap) and paint it. Or leave it untextured and let the apparel's colour do the work. Many of M3AT's garments are drawn that way.
8. **Export selected.**

Pose `M3AT_Rig` in Pose Mode to check it. The garment bends with the body there because it has the body's vertex groups. (The game fits its own weights the same way, but doesn't read these.)

## Approach 3: A separate mesh

Use this for what stands away from the skin or has a shape of its own: hats and helmets, skirts and robes, capes, coats with a puffed shape (M3AT's parka), armour plates, belts, packs and badges.

1. **Import body** and model your garment around it in the rest pose, in Object Mode, as its own object. Keep it at least its layer's offset (above) off the skin everywhere.
2. Keep it low poly (see above). Smooth shading is fine, as normals are worked out in the game.
3. Pick the settings that suit it:
   - **Hats, helmets, masks, glasses:** tick **Rides the head**. It moves whole with the head bone and keeps its shape on every body type (it is made hard, but shrinks with M3AT's smaller-head option). Set **Hair**: *Tucked under* for a hat whose rim the hair comes out from under, *Left off* for a full helmet, *Kept* for a band or a crown that sits over it. Set **Face** to *Left off* for a full face cover. A helmet's glass: give those faces a material named `Visor`, and they are drawn see-through with the face behind them.
   - **Hard armour:** tick **Hard**, so plates keep their shape on Fat and Hulk bodies instead of swelling with the flesh. For armour that stands well off a limb, put the bones in **Also hide bones** (e.g. `thigh shin` for greaves) so no skin shows through the gap. Tick **Hides clothes under it** for full body armour, so a shirt under it can't poke out.
   - **Skirts, robes, capes, long coats:** by default each vertex takes the weights of the skin nearest it. For a long skirt that tears between the legs, because each side follows one thigh. Give it weights of its own and tick **Own weights**:
     1. With the body imported, add a **Data Transfer** modifier to the garment: source `M3AT_Body`, Vertex Data, Vertex Groups, *Nearest Face Interpolated*, then apply it.
     2. Smooth and paint the weights so the skirt's front and back blend evenly between `thigh.L` and `thigh.R` lower down, and toward `spine` (the hips) near the waist. Use **Weights > Smooth**, and Normalize All at the end.
     3. Pose `M3AT_Rig` (a step, a sit) to check it.

     Vertex groups must be named for M3AT's bones (those on `M3AT_Body`). Apply any modifiers that change the mesh before exporting with own weights.
4. Unwrap and paint it, or leave it in one colour.
5. Set **Defs** and **Export selected**.

## Settings reference

The kit's panel writes these as lines in the garment's file. You can also write them by hand.

| Kit setting | File line | What it does |
| --- | --- | --- |
| Defs | `defs A B ...` | The apparel (ThingDef defNames) it is drawn for. |
| Stand in, Layer, Groups | `layer L` + `groups G ...` | Also a stand-in for apparel with no garment of its own worn on layer L (an ApparelLayerDef) over one of those body part groups, besides what its defs make it one for. |
| | `stands_for A ...` | Always drawn for these apparel, as a stand-in. |
| | `like A` | When none of its defs is loaded: the apparel whose kind (tech level, stuff, armour) it is scored as for stand-ins. |
| (the image) | `texture file.png` | Its image, beside the file. It needs `t` lines. |
| Tint | `tint yes` / `tint no` | Tinted by the apparel's colour always or never. Left out: only if the apparel is made from stuff or dyed. |
| Cut out | (written into the image) | Off: the image is written opaque. On: its alpha is kept and cut out below 0.5. |
| Own colour | `colour r g b` | Drawn in this colour (0 to 1), not the apparel's. |
| Hair | `hide_hair yes/no/tuck` | Leave the hair off, keep it, or tuck it under the rim. Left out: off if it covers most of the scalp. |
| | `over_hair yes` | Kept over the hair, pushed out past whichever style is worn (M3AT's slicecap). |
| Face | `hide_face yes/no` | Leave the face (Facial Animation's layers) off, or keep it. |
| Rides the head | `bone spine.006` | Moves whole with that bone. Any bone works (`hand.R`, `spine.002`, ...). |
| Hard | `hard yes` / `hard no` | Moved by its bones as the body shapes move the joints, not reshaped with the flesh. On by default with a `bone` line. |
| | `rigid yes` | Every body shape moves it as one piece. |
| Hides the skin | `hides no` | The skin under it is still drawn (a veil, a net). |
| Also hide bones | `hide_bones thigh shin ...` | Also hide every body triangle all of whose corners are mostly on bones starting with these names. |
| Hides clothes | `hide_clothes yes` | The garments worn under it on lower layers that cover its groups are left off. |
| Own weights | `bones ...` + `own_weights yes` + `w` lines | Its own weights, by bone name, kept on any body. |
| | `own_keys Fat Hulk ...` | Its own `key` lines kept for these shapes, the rest fitted. |
| (Approach 1) | `base Name` | A retexture of M3AT's garment Name. |
| | `body hash` | The `skin.txt` it was exported against (M3AT's own exporter). Its `w`, `key` and `hide` lines are used only on that same body; on any other they are fitted again. |
| (the material) | `visor k ...` | Its triangles numbered k are glass (a material named `Visor` in the kit). |
| (gene parts) | `genes ...`, `colour_from ...` | See [GeneParts.md](GeneParts.md). |

The body's bones, from the hips up: `spine` (hips), `spine.001`, `spine.002`, `spine.003` (chest), `spine.004` (neck), `spine.005`, `spine.006` (head). For each side (`.L` is the figure's left): `shoulder`, `upper_arm`, `forearm`, `hand`, `hand.L.001`, `hand.L.002` (fingers), `thumb`, `thumb.L.001`, `pelvis`, `thigh`, `shin`, `foot`, `toe`.

## The file format

You only need this if you write files with your own tools. Lines are words separated by spaces, and `#` starts a comment.

- Game model space: 1 unit is 1 map cell, +y up, the figure facing -z, standing on y = 0 about 1.25 tall. Blender's (x, y, z) is the game's (-x, z, -y). That is a mirror, so a triangle's corners are listed the other way round (`f a c b` for Blender's a b c). M3AT turns a garment round if most of it faces into the body, so the winding is forgiving.
- `v x y z`: a vertex, in its rest place on the A-posed body.
- `t u v`: a texture place per vertex, in the same order as the `v` lines (a vertex on a UV seam is written once per place).
- `f a b c`: a triangle, vertex numbers from 0.
- `w b weight b weight ...`: a vertex's bone weights, one line per vertex, with bones numbered as the `bones` line lists them (or as M3AT's `anim.txt` does).
- `key Name` then `d i x y z`: a body shape's move of vertex i, in cells.

Any line M3AT doesn't know is ignored.

## Testing and troubleshooting

- M3AT logs each garment it reads, for example `[M3AT] garment FancyCoat from My Mod: for MyMod_Apparel_FancyCoat, 812 vertices, hides 230 body triangles.` A garment it can't read gets a warning saying why. Open the log with dev mode on, or read `Player.log`.
- **Copy bug report** in M3AT's options lists the selected pawn's apparel and the garments drawn for each.
- M3AT's **clothing tool** (the M3AT tools toggle in the bottom-right play settings, then **Adjust clothing** on a pawn) lets you pick any garment, yours included, for apparel that has no garment of its own. Use it to try one on.
- **Nothing drawn:** check the defName, the folder name and place (`YourMod/M3ATgarments/`), and the log. A garment named like one already loaded is skipped.
- **Skin shows through at a bend:** the garment is too close to the skin there, or it doesn't cover the skin under it at rest. Push it out a little.
- **A gap or hole at a hem:** the skin was hidden under the edge, but the edge lifts off it in a pose. Stop the garment a little short of where it ends, or set `hides no` for something open and loose.
- **A skirt tears between the legs or folds into a thigh:** give it own weights (Approach 3).
- **A hat floats or sinks on some pawns:** tick Rides the head (it is fitted to the head bone, and shrinks with the head option).
- **The hair shows through a hat, or vanishes:** set Hair (`hide_hair`) to what you want.
- **It shows the wrong colour:** paint the image light grey and let the apparel tint it, or set `tint no` for an image that is already coloured.
- M3AT can tell apart 254 garments for hiding, its own included (about 100). Past that, garments are still drawn, but they hide nothing and nothing hides them. You'll get a warning.
