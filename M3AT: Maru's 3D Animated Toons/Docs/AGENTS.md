# For AI assistants making content for M3AT

This page is for AI coding assistants (Claude, ChatGPT, Copilot, Cursor and the like) asked to make garments, race packs or other content for **M3AT: Maru's 3D Animated Toons**. Read it before writing any code or mesh. People using an assistant can point it here.

M3AT draws every colonist as an animated 3D figure. Everything you add is drawn **for every pawn, every frame**. A heavy garment doesn't only make one pawn look different: it slows the whole game for every player of your mod. M3AT shows players what each piece of content costs, optimizes anything over its budget, and gives packs a report card. Making content light from the start is the most useful thing you can do for your players.

## Recommendations

M3AT is open source under the MIT licence (`LICENSE` in its folder): you are free to use, change and share any of it, M3AT's own meshes and code included. What follows isn't a set of rules. It is what has worked best for players, and why, so you can make an informed choice.

1. **Start garments from M3AT's own body or garments.** It is by far the easiest way to get something light that fits.
   - **Approach 2, push out the body's own faces** ([Apparel.md](Apparel.md#approach-2-push-out-the-bodys-own-faces)): pick the body faces the garment covers and push them out off the skin. The garment has exactly as many triangles as the skin it covers (a shirt is about 200). It bends with the body's own weights, so it can't clip at elbows or knees, and it follows every body type (female, male, thin, fat, hulk, children) by itself. **This is the method we recommend most.** [Tools/m3at_headless_garment.py](Tools/m3at_headless_garment.py) does it from the command line, with no clicking.
   - **Approach 1, retexture** ([Apparel.md](Apparel.md#approach-1-retexture-a-m3at-garment)): a `base <Name>` line and your image on one of M3AT's own garments. No mesh at all; the cheapest possible.
   - **Cut down a similar garment** ([Apparel.md](Apparel.md#cut-down-a-similar-garment)): import the M3AT garment closest to the one you want and delete the faces yours doesn't have (sleeves off a jacket for a vest, legs off trousers for shorts). It can only come out lighter than the original, and keeps its fit.
   - **Cut hems and necklines along the body's edges.** Every triangle should be a body face or a piece of one. For any scripted cut, re-cut or change to a garment's mesh or texture, follow [Methods.md](Methods.md): the methods step by step, the numbers that worked, the checks to run after every cut and the mistakes to avoid. If the user draws an example by hand, learn its points from it ([Methods.md](Methods.md#a-hem-path-learnt-from-a-hand-drawn-example)) rather than inventing a curve.
   - **Part body faces, part separate** ([Apparel.md](Apparel.md#part-body-faces-part-separate-mesh)): a coat, robe or dress is a top from the body's faces with a skirt extruded from its bottom edge, the skirt's weights blended between the thighs and Own weights on.
   - **Meshes generated from sprites, image-to-3D services, sculpts, shrink-wrapped scans, or 2D art extruded into slabs** tend to work poorly here. The first race packs made that way came out 6 to 100 times heavier than M3AT's own garments (helmets of 17,000 to 20,000 triangles, when the whole body is 1,194). They clipped in poses, and M3AT optimizes anything over its budget as it loads, which can spoil the shape. They can work for bulky, hard armour that stands well off the body (M3AT's power armours started that way), but only with heavy decimation and a great deal of skilled hand editing after: see [Apparel.md](Apparel.md#keep-it-low-poly).

2. **Aim for about what M3AT's own garment has, well under the budget:**

   | | Aim for | Budget (the auto-optimizer's default) | M3AT's own |
   | --- | --- | --- | --- |
   | A garment | about as many triangles as M3AT's own for the same thing (or the one M3AT would draw for it) | twice M3AT's own, at least 400 triangles | shirt 194, trousers 220, parka 344, long dress 398, power armour 1,044, simple helmet 118, power armour helmet 508 |
   | An image | 1,024 pixels on its longer side | 1,024 pixels | 1,024 or less |

   The budget is what M3AT's auto-optimizer allows at its default setting: twice M3AT's own, to give rougher work some leeway. A garment made the ways above (from the body's faces, or cut down from one of M3AT's) comes out close to M3AT's own, so that is the figure to aim for. Over budget, M3AT optimizes the garment or shrinks the image as it loads, so the game stays smooth. Players see it marked red, "M3AT auto-optimized from N", in M3AT's content switcher, and can set Auto Optimization to None if they want it kept as made.

3. **Put the detail in the image, not the mesh.** The figure is small on screen and outlined. Stitching, buttons, folds, trim and logos read better in the texture.

4. **Prefer M3AT's API to patches on its private code.** Garments go in an `M3ATgarments` folder ([Apparel.md](Apparel.md)). A race's ears, tail, face, hair and skin colour are registered with `ExpansionApi.RegisterPack` ([RacePacks.md](RacePacks.md)). The API is kept stable across updates; private methods are renamed and changed freely, so patches on them tend to break. Players can switch patches off part by part, and the pack report lists each one with the API call that does the same. If the API lacks something you need, ask for it: that is how most of it came about.

5. **Keep a race's changes to that race's pawns.** Changing M3AT's body or other mods' garments as they load changes them for every pawn of every race. One pack cut the chest of M3AT's body and of every garment finer, whether its race was in the game or not. That took the body from 1,194 triangles to 3,200 and M3AT's shirt from 194 to 1,957, for every pawn. M3AT turns such changes off by default and marks them red, so players can choose. Giving a race its own proportions as a shape key on M3AT's mesh, applied to that race's pawns only, gets the same look without the cost.

6. **M3AT's body is liable to change between updates** ([Apparel.md](Apparel.md#when-m3ats-body-changes)), as the mod is at an early stage; we aim to settle on a body soon. Its vertex and triangle numbers, its edges and the exact place of its skin can all change; its bone names, rest pose, scale, body shape names and the garment file format are kept. So never hard-code a body vertex or triangle number, keep the script or command that made each garment so it can be run again on the new body, and leave the kit's `body` line in the files you export, as it is how M3AT tells a garment made on an older body.

7. **Depending on M3AT usually works better than bundling it.** You're free to copy and change M3AT's files. But a pack that loads with M3AT (as a dependency, or after it when optional) and uses the player's copy keeps getting M3AT's fixes and new body types, and can't end up with two versions of the body that disagree. Garments you've made from M3AT's, by cutting or reshaping them, are yours to ship.

8. **Mind other mods' art.** M3AT's licence covers M3AT only. If your garments' images come from another mod's sprites, check that mod's terms, or make them at load time from its installed files, as the Miho pack does.

## Checking your work

- **The garment kit** ([Tools/m3at_garment_kit.py](Tools/m3at_garment_kit.py)) shows a live bar of triangles against the budget, and warns about images over 1,024 pixels.
- **The pack report:** in game with development mode on, open M3AT's content switcher (the M3AT button on the bottom bar, or the Content tab of M3AT's options) and press **Pack report** under your pack. It writes `pack_report_<packageId>.md` to RimWorld's config folder, under `M3AT/`. It lists:
  - each garment against the budget, with what to do about it;
  - each patch on M3AT's code, with the API call to use instead;
  - your measured cost, and a line for your Workshop page.
- **The benchmark** (M3AT's options, Problems, Benchmark M3AT impact) measures your pack's cost apart from M3AT's own, with some pawns wearing your content in view.
- **Look at every body type and pose:** female, male, thin, fat, hulk and a child, standing, walking, sitting and lying.

## A garment from the command line

```
blender -b --python "<M3AT>/Docs/Tools/m3at_headless_garment.py" -- \
    --chibi "<M3AT>/Models/chibi" --out "<your mod>/M3ATgarments" \
    --name MyShirt --defs MyMod_Shirt --layer OnSkin \
    --bones spine.001 spine.002 spine.003 spine.004 shoulder.L shoulder.R upper_arm.L upper_arm.R \
    --image my_shirt.png
```

- `--bones` says which part of the body it covers: the faces weighted to those bones become the garment.
- `--layer` sets how far off the skin it sits.
- `--extra` pushes it further out, for a bulkier garment.
- `--image` is projected from the front as a start. Paint or bake a better image onto the garment's texture places in Blender if you can.

A skirt, a hood or a collar is best as a separate mesh when the body has no faces where it goes. Keep it within the budget, and see [Apparel.md](Apparel.md#approach-3-a-separate-mesh).
