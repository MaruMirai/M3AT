# Making apparel for M3AT

M3AT draws every person as an animated 3D chibi, and each piece of apparel it knows is a 3D **garment**: a mesh that sits on the chibi's body and bends with it. This guide is for modders who want their apparel (or the game's, or another mod's) to show up as a garment. It also covers retexturing the garments M3AT already has.

You need **no C# and no XML**. A garment is a plain text file in a folder in your mod. If M3AT isn't loaded, the folder is ignored, so you don't need M3AT as a dependency.

Gene parts (spikes, scales, crests and so on drawn for a gene) are made the same way. See [GeneParts.md](GeneParts.md) after this guide.

> **M3AT is in early development, and its body is liable to change between updates.** We're still finding the body's best shape, and we're prioritising settling on a more final shape soon to make things more robust for content creators. **Currently apparel is likely to break when the body is redone**. Keep your `.blend` files and the script or steps that made each garment as they may help remake it on the new body. See [When M3AT's body changes](#when-m3ats-body-changes).
>
> **The systems for player-made clothing are at a very early stage.** The kit, the file format's options, the ways of making a garment and this guide are first versions, and we're focusing on making garments easier and more robust soon.

> **We recommend starting with Approach 2 or Approach 1.** A garment made from the body's own faces (Approach 2), a retexture of one of M3AT's garments (Approach 1) or one of them cut down fits every body type and pose and costs a fraction of anything else. Meshes generated from sprites, image-to-3D services or sculpts tend to come out many times heavier and clip, and M3AT optimizes anything over its budget. Approach 2 is the one most likely to need remaking when the body is redone, but it is the best practice and future approaches will likely remain similar to it, so it's worth learning now: keep the steps that made each garment and they can be run again on the new body. M3AT is MIT licensed, so its meshes are yours to start from. Using an AI assistant? Point it at [AGENTS.md](AGENTS.md).

## Contents

1. [How M3AT draws apparel](#how-m3at-draws-apparel)
2. [Where the files go](#where-the-files-go)
3. [Keep it low poly](#keep-it-low-poly)
4. [The garment kit for Blender](#the-garment-kit-for-blender)
5. [Approach 1: Retexture a M3AT garment](#approach-1-retexture-a-m3at-garment)
   - [Cut down a similar garment](#cut-down-a-similar-garment)
6. [Approach 2: Push out the body's own faces](#approach-2-push-out-the-bodys-own-faces)
   - [Cutting clean hems and necklines](#cutting-clean-hems-and-necklines)
7. [Approach 3: A separate mesh](#approach-3-a-separate-mesh)
   - [A frame of its own (power armour with its own proportions)](#a-frame-of-its-own-power-armour-with-its-own-proportions)
   - [Part body faces, part separate mesh](#part-body-faces-part-separate-mesh)
8. [Settings reference](#settings-reference)
9. [The file format](#the-file-format)
10. [When M3AT's body changes](#when-m3ats-body-changes)
11. [Testing and troubleshooting](#testing-and-troubleshooting)

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
| The whole chibi body | 1,200 |
| M3AT's shirt, trousers, simple helmet | 190, 220, 118 |
| M3AT's parka, long dress | 344, 398 |
| M3AT's power armour | 1,044 |
| A small accessory (a collar, a strap, a badge) | under 150 |

Aim for these. **M3AT's budget for a garment (what its auto-optimizer allows at its default setting) is twice its own garment for the same thing (or the one it would draw for it: a one-piece suit is its shirt and trousers together), at least 400 triangles, and 1,024 pixels on an image's longer side.** A shirt's budget is 400, a parka's 688, power armour's 2,088. The player's other settings change it (below). Over it, M3AT optimizes the garment or shrinks the image as the game loads. That keeps the game smooth, but it can spoil the garment's shape.

**Players see it.** M3AT's content switcher shows each garment's triangles against the budget:
- green when light, amber within the budget, red over it;
- "M3AT auto-optimized from N" when M3AT had to cut it;
- each pack's measured cost from the benchmark.

Players can change the auto-optimizer's setting (M3AT's options, Auto Optimization): High (up to 1.2 times M3AT's own, cutting the most, for slower computers), Default (twice), Low (three times, keeping more of their makers' detail), or None (kept as made, up to 30,000 triangles).

**Check before you publish.** The kit shows a live bar of triangles against the budget and warns about large images. In game, with development mode on, the content switcher's **Pack report** button writes a report of every garment against the budget, with what to do about each, and a line to put on your Workshop page.

The figure is small on screen and outlined, so detail you'd model on a normal character turns to noise here. Put fine detail in the texture instead.

**Approach 2 is the cheapest way to get a garment that fits.** The pushed-out copy of the body's faces has exactly as many triangles as the patch of skin it covers. Each of its vertices also sits straight out from a vertex of the skin, so it bends with exactly the same weights and can't clip into the body at a bent knee or elbow. A sculpted or shrink-wrapped mesh needs many more triangles to follow the body as closely, and it still clips where its vertices don't line up with the skin's.

**Cutting down a similar garment is nearly as cheap.** If one of M3AT's garments already covers more than yours, import it and delete the parts yours doesn't have ([Cut down a similar garment](#cut-down-a-similar-garment)). The result can only be lighter than what you started from, and it keeps that garment's fit.

**Generated meshes can work as a starting point for bulky garments.** M3AT's power armours started as image-to-3D meshes. A suit of armour stands well off the body and keeps its own shape, so it doesn't need to follow the skin closely, which is where generated meshes struggle. Be warned, though: the generated mesh was only a starting point. Getting each suit to fit and read well while staying optimized required hand 3D modelling and was far more work than any of the approaches below. What made them work:
- **Shrink-wrap it first, with the limbs apart.** A generated mesh is full of hidden geometry: inner walls, overlapping shells, faces buried inside other faces. They cost triangles, and they poke through the surface once the mesh bends. Wrapping a single skin over the outside (a shrink-wrap tool, or Blender's Remesh modifier in Voxel mode) keeps only the surface and throws everything inside away. A wrap also bridges any gap that's too narrow, fusing the legs together or an arm to the side, and then the armour tears when the legs move apart. The kit's **Open limbs** and **Close limbs** buttons deal with this. Fit the mesh over `M3AT_Body` at rest and press **Open limbs**: you get a copy with the thighs swung out and the arms raised. Duplicate that copy and wrap the duplicate. In Blender, add a Remesh modifier (Voxel, a voxel size of about 0.01). Then select the wrap and press **Close limbs**, which brings it back to rest, skinned to `M3AT_Rig` and ready to pose. If you wrapped it in another program, import the result and tick **Fit to the opened mesh**, in case it came back moved or rescaled. Pick a half under **Symmetric** to cut the wrap down the middle and mirror that half. Before opening, delete anything on the midline between the legs (a fin, a spike, a stray flap). Opening the pose wider won't stop the wrap bridging across one of those.
- **Decimate hard.** Generated meshes come out at tens of thousands of triangles. Bring them down to about 1,000 for a suit (M3AT's power armour is 1,044) and a few hundred for a helmet: the Decimate modifier (Collapse), or a remesh then Decimate. The outline and a good texture hide how few there are.
- **Texture after decimating.** Unwrap the decimated mesh and bake the high-poly's colours onto it (Cycles' Bake with *Selected to Active*), or paint it fresh. The generated texture won't survive the decimation.
- **Tick 'Hard'**, so the plates don't swell on Fat and Hulk bodies. Use **Also hide bones** where it stands well off a limb. Give a helmet's glass its own `Visor` material, and smooth it, as generated glass comes out lumpy.
- Pose `M3AT_Rig` (a stride, a sit, raised arms) before exporting, and check the kit's triangle bar.

For close-fitting clothes, Approach 2 is still far better: a generated shirt has to be many times heavier to follow the body as closely, and still clips.

## The garment kit for Blender

`Docs/Tools/m3at_garment_kit.py` in M3AT's folder is a small Blender add-on (Blender 4.2 or newer). It does the work of the approaches below without M3AT's own rig.

To install it: **Edit > Preferences > Add-ons > Install from Disk** and pick the file. Or open it in Blender's Text Editor and press **Run Script**, which lasts until you close Blender. It adds an **M3AT** tab to the 3D view's sidebar (press **N**).

1. Set **M3AT folder** to M3AT's mod folder. For the Workshop copy that is `steamapps/workshop/content/294100/3839520750`.
2. Set **Export to** to your mod's `M3ATgarments` folder.

Its buttons:

- **Import body**: the chibi's body (`M3AT_Body`) in its rest pose, with every body shape as a shape key and a vertex group per bone. It also brings in an armature (`M3AT_Rig`) you can pose in Pose Mode to see how a garment bends. **1 Blender unit is 1 map cell**, and the figure faces -Y (Blender's front view). The rest pose is an A-pose: arms angled down and out, legs a little apart.
- **Import garment**: one of M3AT's own garments (`Models/chibi/garment_<Name>.txt`), with its texture places and image.
- **Garment from selected faces**: Approach 2.
- **Open limbs** and **Close limbs**: for wrapping a generated mesh in one skin without fusing its legs or arms ([Keep it low poly](#keep-it-low-poly), on generated meshes).
- **Capture frame**, **Show frame**, **Rest pose**, **Clear frame** (under *Own frame*): a suit that changes the body inside it, posed on the rig ([A frame of its own](#a-frame-of-its-own-power-armour-with-its-own-proportions)).
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

### Cut down a similar garment

Often the easiest way to a new garment that's well optimized is to start from a M3AT garment that already covers more than you need, and cut away what yours doesn't have. A vest from a jacket (cut off the sleeves), shorts from trousers, a crop top from a shirt, a short skirt from a long dress, an open-faced helmet from a full one. Deleting faces only ever makes a garment lighter. What's left keeps the original's fit, its distance off the skin and its texture places, so it still bends cleanly at every joint and on every body type.

1. **Import garment**, and pick the one closest to what you want.
2. Rename the object, and **untick Retexture only**: the mesh is now your own.
3. In Edit Mode, select the faces to remove and delete them (**X > Faces**). Knife-cut (**K**) first if you want a hem or neckline between the existing edges, along the body's edges ([Cutting clean hems and necklines](#cutting-clean-hems-and-necklines)). Use X-ray (Alt+Z) and Mesh Symmetry X to keep the back and both sides matching.
4. Check the settings carried over from the original: **Layer**, **Rides the head**, **Tint**, **Hair**, **Face** and **Hides clothes under it** come with it, but **Hard**, **Own weights** and **Also hide bones** don't, so set those again if the original had them. Look at its own `garment_<Name>.txt` to see which it had. Set **Defs** to your apparel.
5. Repaint the image or keep the original's, then **Export selected.**

M3AT works out the skin it hides from the mesh you export, so skin shows again where you cut. As with any mesh of your own, it no longer follows later M3AT changes to the garment you started from.

**Adding to one works the same way.** M3AT's grenadier armour is its power armour with a launcher tube modelled on, and the prestige helmets are the plain ones with a crest or wings. Model the extra part onto the imported garment and join it (Ctrl+J). On a hat or helmet that rides the head, that's all: the whole thing moves with the head. On a body garment, the imported one comes with a vertex group for each bone it bends with. Give the new part's vertices a weight of 1 in the group of the bone it should move with (for a tube on the back, `spine.003`) and tick **Own weights**, so it moves as one solid piece rather than bending with the skin under it.

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
   - Knife-cut a nicer hem and delete what's past it. Cut along the body's edges so every triangle stays a body face or a piece of one ([Cutting clean hems and necklines](#cutting-clean-hems-and-necklines)).
   - Move a vertex *outward* to loosen a cuff, or flare a hem out a little.
   - Add a few loops for a collar or a pocket that stands off.

   Don't pull vertices inward toward the skin. Keep the triangle count near the patch's own.
7. Unwrap it (UV > Smart UV Project is fine, or mark seams and Unwrap) and paint it. Or leave it untextured and let the apparel's colour do the work. Many of M3AT's garments are drawn that way.
8. **Export selected.**

Pose `M3AT_Rig` in Pose Mode to check it. The garment bends with the body there because it has the body's vertex groups. (The game fits its own weights the same way, but doesn't read these.)

## Cutting clean hems and necklines

A garment made from the body's faces (Approach 2), or cut down from one of M3AT's, often needs a line of its own: a hem lower at the back, a crop top's bottom edge, a neckline, an armhole. Cut it well and the garment stays light and its edge looks clean; cut it carelessly and you get many tiny triangles that cost frame time and shade badly.

**The rule: cut along the body's edges.** Every triangle of the garment should be one of the body's triangles, or a piece of one:

- Put every point of the hem on a body vertex or on a body edge.
- Inside each body triangle, make the hem one straight line across it.
- A triangle the hem crosses then keeps a triangle or a four-sided piece, and every other triangle stays whole.

Why it matters: the garment's triangles then match the skin's, and those of every other garment made from the same faces (trousers, long johns, jackets). They bend together and never cross each other in a pose, and M3AT can hide what's under them cleanly. A cut that ignores the body's edges leaves new corners at random spots, with slivers wherever the line passes near a corner: on M3AT's shirt, 290 triangles against 258 for the same hem cut along the body's edges.

**By hand in Blender:**

1. Decide the line first: a smooth curve round the body. A shirt hem reads best nearly level, a little higher at the front than the back, and a little higher again at the sides.
2. With the Knife tool (**K**), cut along it, clicking where the line crosses the body's edges. The knife snaps to edges and vertices. Where the line passes close to an existing vertex, go through that vertex instead of putting a new point just beside it.
3. Delete what's past the line, and turn on Mesh Symmetry X (or mirror afterwards) so both sides match.
4. **Tidy the rim.** Delete any very thin triangles left along the edge, usually at the back of the armpit, where the line ran just past a fold.
5. **Fill gaps from the body.** If the old edge has a notch you want closed, copy the body's faces under it, push them out by the garment's offset, and join them to the garment before cutting.

**Where the new hem crosses the garment beneath.** A shirt and trousers stand at the same distance off the skin. Once a shirt's hem cuts across the body's triangles, the trouser triangles under the edge are only partly covered, so they stay drawn at exactly the shirt's depth and flicker against it. Pull the shirt's bottom row of vertices out a little (about 0.002 cells), and the row above it half as much. A jacket, a layer further out, already clears the trousers.

**The texture.** If the hem moves, move the image's stitching or trim with it, so it follows the new edge, and paint the faces you added as cloth rather than stretching the old edge over them.

**A garment cut from another** (a crop top from a shirt): if the one it was cut from is remade, cut it again from the new one along the same outline, rather than editing the old cut-down copy.

If someone draws an example of the line they want, it's the best guide for every garment made from the same faces, as they all share those body edges. Scripts and AI assistants: the step-by-step methods, the numbers that worked and the checks to run are in [Methods.md](Methods.md).

## Approach 3: A separate mesh

Use this for what stands away from the skin or has a shape of its own: hats and helmets, skirts and robes, capes, coats with a puffed shape (M3AT's parka), armour plates, belts, packs and badges.

1. **Import body** and model your garment around it in the rest pose, in Object Mode, as its own object. Keep it at least its layer's offset (above) off the skin everywhere.
2. Keep it low poly (see above). Smooth shading is fine, as normals are worked out in the game.
3. Pick the settings that suit it:
   - **Hats, helmets, masks, glasses:** tick **Rides the head**. It moves whole with the head bone and keeps its shape on every body type (it is made hard, but shrinks with M3AT's smaller-head option). Set **Hair**: *Tucked under* for a hat whose rim the hair comes out from under, *Left off* for a full helmet, *Kept* for a band or a crown that sits over it. Set **Face** to *Left off* for a full face cover. A helmet's glass: give those faces a material named `Visor`, and they are drawn see-through with the face behind them.
   - **Hard armour:** tick **Hard**, so plates keep their shape on Fat and Hulk bodies instead of swelling with the flesh. For armour that stands well off a limb, put the bones in **Also hide bones** (e.g. `thigh shin` for greaves) so no skin shows through the gap. Tick **Hides clothes under it** for full body armour, so a shirt under it can't poke out.
   - **Power armour:** set **Wearer's frame** to have its wearer drawn broader and bigger, as M3AT's own power armour does (0.25 for a light suit, 0.5 for a marine's, 1 for the heaviest). Model the armour over the body as it is (the Basis): M3AT fits it to that frame itself. For proportions of your own (longer legs, a bigger chest, huge gauntlets), give it its **own frame** instead (below).
   - **Skirts, robes, capes, long coats:** by default each vertex takes the weights of the skin nearest it. For a long skirt that tears between the legs, because each side follows one thigh. Give it weights of its own and tick **Own weights**:
     1. With the body imported, add a **Data Transfer** modifier to the garment: source `M3AT_Body`, Vertex Data, Vertex Groups, *Nearest Face Interpolated*, then apply it.
     2. Smooth and paint the weights so the skirt's front and back blend evenly between `thigh.L` and `thigh.R` lower down, and toward `spine` (the hips) near the waist. Use **Weights > Smooth**, and Normalize All at the end.
     3. Pose `M3AT_Rig` (a step, a sit) to check it.

     Vertex groups must be named for M3AT's bones (those on `M3AT_Body`). Apply any modifiers that change the mesh before exporting with own weights.
4. Unwrap and paint it, or leave it in one colour.
5. Set **Defs** and **Export selected**.

### A frame of its own (power armour with its own proportions)

A suit can change the body inside it: longer legs, broader shoulders, bigger hands and feet. You pose M3AT's rig into the suit's proportions, and model the suit around that. Its wearer is drawn in that frame in the game, with every animation playing on it.

1. **Import body**. Select `M3AT_Rig` and go into Pose Mode. **Move and scale** bones to the proportions you want:
   - Scale `thigh.L`/`thigh.R` (or `shin`) along the bone (Y) for longer legs.
   - Scale `shoulder.L`/`shoulder.R` along the bone for broader shoulders.
   - Scale `hand` and `foot` for gauntlets and boots.
   - Scale `spine.003` (the chest) for a bigger chest. Its children (arms, neck, head) scale with it unless you scale them back down.

   Moving and scaling are what M3AT keeps. Don't turn bones: M3AT keeps each bone's own direction, so a turned forearm would be drawn bent along a straight bone. The kit warns you if a bone turns more than 3°. You don't need to keep the feet on the ground, as M3AT lifts the whole frame so the soles stay on the floor.
2. Make your suit's object and select it. Click **Capture frame** (under *Own frame*): the rig's pose is kept on that object. **Rest pose** puts the rig back, and **Show frame** poses it in the suit's frame again.
3. Model the suit around the posed body, as its own object **without** an Armature modifier. (If you'd rather model it at rest, skinned to `M3AT_Rig` with an Armature modifier, that works too: the kit then takes its mesh as being at rest.)
4. **Export selected**. The kit writes the suit as it would be on the body at rest, with the weights it used to get it there (`own_weights`) and a `frame` line for each moved bone. In the game the suit is put back into the frame by those same weights, so it comes back the way you modelled it.

What to keep in mind:
- Everything else the wearer has on (M3AT's helmets, other mods' clothes, hair, the face) follows the frame by its own bones, so a helmet grows with a scaled head.
- The animations were made for the normal body. Moderate changes (about a fifth longer legs, half again as broad at the shoulder) look right. Much longer limbs make the feet slide a little when walking, and things held, sat on or eaten miss a little.
- One suit's frame per wearer: if two worn garments each have a frame, both apply.
- Children don't take frames, as they don't take the Armoured frame.
- **Wearer's frame** (`Armoured`) and an own frame can be combined.

### Part body faces, part separate mesh

Many garments are both: a top that lies on the skin with a part that hangs or stands away from it. M3AT's duster, robes, dresses and tribal wear are made this way: the body's faces for the top, and a skirt hung from its bottom edge. A hooded cloak or a coat with a stand collar works the same.

1. Make the top with Approach 2 (**Garment from selected faces**). It comes with the body's vertex groups, so it already bends exactly as the skin does.
2. In Edit Mode, select its bottom edge loop and extrude it down (**E**, then **Z**) in a few rings to the hem. Scale each ring out a little so the skirt flares clear of the legs and stays at least its layer's offset off the skin. A ring about every 0.1 cells down is plenty: M3AT's duster has 5 to mid shin, its robe 6 to the ankle.
3. Weight the new rings (Weight Paint, or select the vertices and use **Assign** in Vertex Groups). The extruded rings take the weights of the edge they came from, which usually means one thigh for each side. Blend them instead: toward `spine` (the hips) at the waist, front and back shared evenly between `thigh.L` and `thigh.R` lower down, and toward the `shin`s below the knee for a long one. Leave the top's own weights as they are. **Weights > Normalize All** at the end.
4. Tick **Own weights**, so the skirt keeps your blend rather than taking the nearest skin's (which tears it between the legs).
5. Pose `M3AT_Rig` in a stride and a sit to check it, then unwrap, paint and export as usual.

For an open coat, cut the top open down the front before extruding, and leave a vent at the back of a long skirt so the legs have room to stride.

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
| Wearer's frame | `wearer_key Armoured 0.5` | Its wearer is drawn with at least this much of a body shape while it is worn. `Armoured` (0 to 1) is a power armour wearer's frame: broader shoulders, thicker limbs and up to 5% bigger. M3AT's recon armour gives 0.25, its marine and plate armour 0.5, its cataphract 1. A hard garment is fitted to the `Armoured` shape like a soft garment is to the others. Any of the body's shapes can be named (`Hulk`, `Fat`, `Thin`, ...); give one line per shape. A `base` retexture takes the base garment's frame unless it has lines of its own. |
| Own frame | `frame <bone> <12 numbers>` | One line for each bone the suit's frame moves: the bone's deform from M3AT's skeleton to the frame, the top three rows of a 4×4 matrix row by row, in game space (x, y up, z; the figure facing -z). Its wearer takes the frame whole while it is worn. The kit's **Capture frame** writes them. See *A frame of its own*. |
| Hides the skin | `hides no` | The skin under it is still drawn (a veil, a net). |
| Also hide bones | `hide_bones thigh shin ...` | Also hide every body triangle all of whose corners are mostly on bones starting with these names. |
| Hides clothes | `hide_clothes yes` | The garments worn under it on lower layers that cover its groups are left off. |
| Own weights | `bones ...` + `own_weights yes` + `w` lines | Its own weights, by bone name, kept on any body. |
| | `own_keys Fat Hulk ...` | Its own `key` lines kept for these shapes, the rest fitted. |
| (Approach 1) | `base Name` | A retexture of M3AT's garment Name. |
| (written by the kit) | `body hash generation` | The body it was made on: the hash of M3AT's `skin.txt` and the body's generation (its `generation` line). Its `w`, `key` and `hide` lines are used only on that same body; on any other they are fitted again. If the generation is older than the installed M3AT's, the log says so. See [When M3AT's body changes](#when-m3ats-body-changes). |
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

## When M3AT's body changes

M3AT is in early development, and its body is still being improved. The belly has been reshaped and the mesh cut finer there, and more changes to its shape are on the way. This is because the mod is at an early stage: we're still finding the body's best shape, and **we aim to settle on a body soon**, after which it should change rarely, if at all. Until then, **a garment you make today may well need exporting again, or remaking, after a M3AT update.**

**Garments made from the body's faces ([Approach 2](#approach-2-push-out-the-bodys-own-faces)) are the most likely to break.** They are the body's own triangles pushed outward, so they match its faces one for one. When we redo the body, its faces will change and those garments will no longer line up with it: expect to remake them (or re-cut them) on the new body rather than just export them again. Retextures of M3AT's garments and separate meshes ([Approach 3](#approach-3-a-separate-mesh)) depend less on the faces, but may still clip or float where the skin has moved.

**What may change in any update:**
- The body's vertices, triangles and edges: how many there are, their numbers, and where the edges run. Don't rely on a body vertex or triangle number in your files or code.
- Exactly where the skin's surface is. Where an update moves the skin outward past a garment's push-out, the skin can poke through that garment.
- How the skin is weighted to the bones.
- The figure's scale.
- The body shapes (Fat, Hulk, Thin and so on): which there are, their names and their shapes.

**What is likely to stay the same:**
- The bone names and the way they connect (listed at the end of the [Settings reference](#settings-reference)).
- The rest pose (the A-pose), with 1 unit being 1 map cell and the figure facing -Z.
- The garment file format. New lines may be added; old ones should keep working.

**Why many garments survive a small change:** M3AT fits each garment's weights, body shapes and the skin it hides to the installed body as it loads. After a small reshape, a garment made from the old body's faces is still a close fit, and its hems sit a little off the new body's edges, which doesn't show. A redone body with different faces is not a small reshape, and we can't promise this will hold for it.

**How you'll know.** Every garment the kit exports has a `body` line naming the body it was made on, with its **generation**, a number M3AT raises when the body changes enough to move garments. If yours was made on an older generation, M3AT's log says so as it loads it ("Made on body generation 1 (this M3AT's is 2)…"). A garment without a `body` line (exported before the kit wrote one) gets no such note. Look at it in the game after an update.

**Exporting it again:** open your `.blend` with the new M3AT installed, **Import body**, check the garment over the new body (and pose `M3AT_Rig`), move any vertex the skin now pokes through, and **Export selected**. A garment from [Tools/m3at_headless_garment.py](Tools/m3at_headless_garment.py) is quicker still: run the same command again. Keep the command beside your files.

## Testing and troubleshooting

- M3AT logs each garment it reads, for example `[M3AT] garment FancyCoat from My Mod: for MyMod_Apparel_FancyCoat, 812 vertices, hides 230 body triangles.` A garment it can't read gets a warning saying why. Open the log with dev mode on, or read `Player.log`.
- **Copy bug report** in M3AT's options lists the selected pawn's apparel and the garments drawn for each.
- M3AT's **clothing tool** (the M3AT tools toggle in the bottom-right play settings, then **Adjust clothing** on a pawn) lets you pick any garment, yours included, for apparel that has no garment of its own. Use it to try one on.
- **Nothing drawn:** check the defName, the folder name and place (`YourMod/M3ATgarments/`), and the log. A garment named like one already loaded is skipped.
- **Skin shows through after a M3AT update** (but not before): the body changed under it. See [When M3AT's body changes](#when-m3ats-body-changes).
- **Skin shows through at a bend:** the garment is too close to the skin there, or it doesn't cover the skin under it at rest. Push it out a little.
- **A gap or hole at a hem:** the skin was hidden under the edge, but the edge lifts off it in a pose. Stop the garment a little short of where it ends, or set `hides no` for something open and loose.
- **A skirt tears between the legs or folds into a thigh:** give it own weights (Approach 3).
- **A hat floats or sinks on some pawns:** tick Rides the head (it is fitted to the head bone, and shrinks with the head option).
- **The hair shows through a hat, or vanishes:** set Hair (`hide_hair`) to what you want.
- **It shows the wrong colour:** paint the image light grey and let the apparel tint it, or set `tint no` for an image that is already coloured.
- M3AT can tell apart 254 garments for hiding, its own included (about 100). Past that, garments are still drawn, but they hide nothing and nothing hides them. You'll get a warning.
