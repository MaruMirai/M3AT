# Methods for scripts and AI assistants

This page is for AI assistants and anyone writing scripts that make or change M3AT garments. It gives the methods step by step, with the numbers that worked and the checks to run, so that a script can repeat results M3AT's own garments were made with. People cutting garments by hand in Blender want [Apparel.md](Apparel.md) instead; this page builds on it and doesn't repeat it (offsets per layer, settings, the file format and bone names are there).

Units: cells (1 Blender unit in the garment kit), in Blender's axes with the figure facing -Y, on the kit's `M3AT_Body` at rest. Every distance below is in cells unless it says otherwise.

## Contents

1. [Terms](#terms)
2. [The rule for cuts](#the-rule-for-cuts)
3. [A hem path from a curve](#a-hem-path-from-a-curve)
4. [A hem path learnt from a hand-drawn example](#a-hem-path-learnt-from-a-hand-drawn-example)
5. [Cutting a garment along a path](#cutting-a-garment-along-a-path)
6. [Trimming the rim](#trimming-the-rim)
7. [Lifting the hem over the garment beneath](#lifting-the-hem-over-the-garment-beneath)
8. [Moving the hem band's texture](#moving-the-hem-bands-texture)
9. [Re-cutting a garment that was cut from another](#re-cutting-a-garment-that-was-cut-from-another)
10. [Checks after every cut](#checks-after-every-cut)
11. [Mistakes we made](#mistakes-we-made)

## Terms

- **Body-faced garment**: one made from the body's faces pushed out (Apparel.md, Approach 2). Each of its vertices stands straight out from one body vertex, at the garment's own distance off the skin: its layer's offset (Apparel.md's table), plus any extra.
- **A garment vertex's body vertex**: the body vertex nearest it, when its distance from it is within about 0.0025 of the garment's usual distance off the skin (the commonest distance among its vertices). Vertices further off (a pocket, a ruffle, a collar standing out) stand for no body vertex.
- **Garment place of a body vertex**: where the garment's vertex over it is. For a body vertex the garment doesn't cover, use the body vertex moved out along its normal by the garment's usual distance. Work everything below out at garment places, so the result lands on the garment's own surface.
- **Body triangles**: the body's triangles as Blender triangulates them (`mesh.loop_triangles`), which is how they are exported.
- **Copies at one place**: a vertex on a texture seam is several vertices at exactly one place. M3AT joins them by exact place in the game, so anything that moves one must move every copy by exactly the same amount.

## The rule for cuts

Every triangle of a body-faced garment should be a body triangle, or a piece of one:

- every hem point is a body vertex, or lies on a body edge;
- inside each body triangle, the hem is one straight piece from edge to edge, or from a corner to an edge;
- a crossed triangle keeps a triangle (one corner kept), or a four-sided piece split into two triangles along its shorter diagonal.

Why: the garment's triangles then match the skin's and those of every other garment made from the same faces, so they bend with the same weights and never cross in a pose. M3AT hides a triangle under another garment only when that garment covers all of it, and it can do that cleanly only where triangles line up. A cut on a plane or a free line breaks all of this, and adds triangles: M3AT's shirt came to 290 triangles with a plane cut and 258 with the same hem on the body's edges.

## A hem path from a curve

Use this to make a hem with a chosen shape: more level, lower at the back, higher at the sides.

1. **The target curve.** Give the hem's height as a function over the garment: height = *a* + *b*·y + *c*·|x|. *b* tilts it from front to back (the figure faces -Y, so a positive *b* puts the back higher). *c* raises the sides. To start from an existing hem, fit *a*, *b* and *c* to its vertices by least squares, then change them.
2. **Height above the curve.** For each body vertex near the garment's lower part, take *f* = its garment place's height minus the target at that place. Leave out the hands and anything else beside the torso (M3AT's arms hang beside the hips at rest).
3. **Snap vertices onto it.** A body vertex with |*f*| under about 0.0025 is on the curve: set *f* = 0.
4. **Move crossings off edges' ends.** For each body edge whose ends have *f* of opposite signs, the crossing is at share *f*₁ / (*f*₁ − *f*₂) from the first end. If that crossing is within about 0.0025 of either end, **measured along the edge**, set that end's *f* to 0. Repeat until nothing changes.
5. **March the triangles.** In each body triangle, its hem points are its corners with *f* = 0 plus its crossings. A triangle with exactly two points gets one straight piece between them. A triangle with all three corners on the curve gets none (its edges belong to its neighbours).
6. **Walk the loop.** Join the pieces into loops by shared points and keep the loop that goes all the way round the body. Every point should have exactly two neighbours. If some don't, the snapping was too strong: reduce it.
7. **Symmetry.** The body is symmetric. Give each crossing the mean of its own share and its mirror's.
8. **Store it as body topology**: for each point in order, either *body vertex N*, or *body edge (A, B) at share s from A*. That list cuts any garment made from the same body faces, each at its own garment places. It names body vertex numbers, so it must be made again if the body's mesh changes, which it can in any M3AT update ([Apparel.md](Apparel.md#when-m3ats-body-changes)): keep the curve or the hand-drawn example it came from, not only the list.

M3AT's level shirt hem came from *a*, *b*, *c* fitted to a hand-drawn hem (*b* = 0.149, *c* = 0.083; they are ratios, the same in any units) with *b* and *c* roughly halved. It gave 36 points (2 on body vertices) and 14 more triangles than the hand-drawn path: fewer body vertices lie near a level line. Stronger snapping (0.006) used more vertices but made the front wavy.

## A hem path learnt from a hand-drawn example

When a person draws the hem they want by hand, learn its points instead of inventing a curve.

1. Find the example's bottom hem: its open edge loop round the waist (the lowest one).
2. For each of its vertices, list the candidates: body vertices whose garment place is within about 0.0013, and body edges (at garment places) that pass within about 0.0034, at shares between 0.02 and 0.98. Hand-placed vertices are rarely exactly on an edge; up to about 0.002 off is normal.
3. Pick the nearest candidate for each vertex, then check that **every face of the example lies within one body triangle**: the union of its corners' body vertices (one for a vertex, two for an edge point) must be a subset of some body triangle's three. Where a face fails, try the other candidates of its corners and keep the choice that leaves the fewest failing faces.
4. Even out the two sides (mean of each share and its mirror's) and store it as above.

Check by cutting the original garment along the learnt path and comparing with the example: every one of the example's triangles should be in the result (allow for places a little apart, as the example's vertices were snapped), and each of its four-sided faces should come out as two triangles. M3AT's learnt shirt path matched its example's 250 triangles and 4 quads, with places within 0.001.

## Cutting a garment along a path

1. **Fill what's missing above the path.** Starting from the body triangles beside the garment's current hem edges, take every body triangle the garment doesn't hold that has a corner above the path, and those beyond them, stopping at triangles wholly below the path. Make each one a garment triangle at the garment's places. At the hem's own vertices use exactly the garment's places, so the new triangles join without a gap. Wind each one so its normal points out (the same way as the body vertices' normals).
2. **Each body vertex's side.** Above or below the path: compare its garment place's height with the path's height at its angle round the body, interpolated between the path's points by angle. Path vertices are on it. For every edge the path crosses, make sure its two ends are on opposite sides: the higher end is above, whatever the interpolation says.
3. **Split.** Go round each garment triangle's corners, keeping those above or on the path and adding a point wherever an edge crosses it, at the path's own share for that body edge. Points with no matching path edge mean the path and the garment disagree: stop and report.
   - Work out each new point once per edge and reuse it for both triangles beside that edge.
   - Blend its attributes from the edge's ends in one fixed order (the lower-numbered place first), so copies on either side of a texture seam come out at exactly the same place.
   - Blend the texture places within the triangle's own corners, so the existing image still lines up.
4. **Keep** the pieces above the path and drop everything below it. A piece with four corners becomes two triangles, split along its shorter diagonal.
5. **New vertices' other attributes**: give a filled triangle's corners the normal and body-shape moves of the nearest existing garment vertex. Body-shape moves are usually zero, which lets M3AT fit them. The game works out its own normals from the triangles. In Blender, though, a filled face with the body's normals renders lighter than its neighbours.

## Trimming the rim

After any cut, look at each triangle with an open (hem) edge. Its height over that edge is twice its area divided by the edge's length (take the longest open edge if it has two). Delete it if that is under about 0.008.

These thin triangles come from a hem traced just past a fold of the garment, usually the back of the armpit. They form a lip of slivers along the edge. On M3AT's re-cut tops the trimmed triangles stood 0.001 to 0.007 off their edges, and every hem triangle worth keeping stood at least 0.0115 off. One pass gave exactly the person's own hand trim.

## Lifting the hem over the garment beneath

Shirts and trousers are both OnSkin, so they stand at the same distance off the skin. Before a cut, every trouser triangle was either all under the shirt (and hidden) or all outside it. After a cut through triangles, the trouser triangles the hem crosses are only partly covered, so they stay drawn at the shirt's own depth and flicker.

1. Find the new hem's places: its open edges' vertices, at the path's height.
2. Push each of them out about 0.002 along its normal. Push the places one edge above them out half that. Use one direction per place (the mean of its copies' normals), so every copy moves the same.
3. Do this only for garments within about 0.008 of the skin. A Shell garment (0.0127, a jacket) already clears the trousers.

## Moving the hem band's texture

When the hem moves, re-lay the image's hem band (stitching, ribbing, trim) so it follows the new edge, and give the filled faces cloth rather than a smear of the old edge.

1. **Place the new faces in the image.** Give each filled triangle its own island in the image's free space, at the texel density of the garment triangles near it. Keep a few texels of padding round each island and a few more clear between islands.
2. **Unroll the old hem**: lay it out flat as (distance round the body, height), measuring the distance horizontally along the hem from its point at the front.
3. **Its offset curve**: for a distance *h*, the line *h* inside the old hem is the upper envelope of circles of radius *h* round the hem's points. Sample the hem densely (a few thousand points) and, for a place *s* round the body, take the highest *z*ⱼ + √(*h*² − (*s* − *s*ⱼ)²) over hem points within *h* of *s*.
4. **Repaint, texel by texel, in place**, for every triangle within about 0.084 above the new hem (the filled ones included):
   - take the texel's place on the garment, and its height *h* above the new hem;
   - find the old garment's point at the same place round the body and height (offset curve at *h*) + (*h* in from the old hem);
   - fade that shift to nothing at the top of the band;
   - read about 0.0013 further in than that, as the very edge of an island blurs into its neighbours;
   - find the nearest point of the old garment's triangles, read the old image at its texture place with bicubic (Catmull-Rom) sampling, and write the texel.

   Only paint padding texels (outside the triangle) where no other triangle's island is. Read from a copy of the old image, never the one being written.
5. Inside the band, texels keep their islands. Above it the shift is zero, so the image is left as it was and there is no seam at the band's top.

## Re-cutting a garment that was cut from another

A crop top, tank top or bra cut by hand from a shirt is the shirt's surface inside the top's own hems. When the shirt is remade, cut the top again from the new shirt rather than editing the old top.

1. **The top's hems** are its open edge loops (join copies at one place first). Check the top is one manifold surface whose only open edges are its hems, with no triangles facing inward. If it has its own pieces off the shirt's surface (a turned hem, a pad), this method drops them: handle those separately.
2. **Lay each hem vertex on the new shirt**: its nearest point among shirt triangles whose normal is at least −0.2 along the vertex's own. Don't ask for more: a hem vertex's normal can be wrong where its triangles fold, and −0.2 still keeps it off the far side of an armpit. Snap onto a shirt vertex within about 0.0017, or onto a shirt edge within the same.
3. **Trace each piece of hem** between consecutive points across the shirt. Use the plane through both points that holds their mean normal. Walk from the first point's triangle across each shirt edge that the plane crosses, ahead towards the second point, until reaching the second point's triangle.
   - Work out each crossing once per shirt edge, so the triangles either side share it exactly and no cracks open.
   - Make a crossing within about 0.0017 of an edge's end that end.
   - Treat vertices exactly on the plane as just off it, so the plane never passes through a corner.
4. **Split each shirt triangle the hem touches** with a constrained Delaunay triangulation in its own plane (Blender's `mathutils.geometry.delaunay_2d_cdt`). Its outline and the hem pieces are the constraints.
   - Lay points out from their shares of the triangle's own corners, so points on an edge are exactly on it.
   - The triangulation works in single precision and gives back flat triangles along an edge that holds several points. Drop any whose area, worked out in double precision from the shares, is about zero.
5. **Group and keep.** Flood-fill across shared edges that aren't hem pieces. Keep a group if most of its area lies on the top: a triangle's middle within about 0.01 of the top's surface, and not past one of its hems (its nearest point is on an open edge, with the offset mostly along the surface).
6. **Trim the rim** as above.

The kept triangles take the shirt's attributes blended at each point, so the top reads the shirt's image just as the shirt does. Expect it to keep 99.3 to 99.8% of the old top's area. Anything of the top's own off the shirt's surface inside its hems is lost: M3AT's tops' chests stood 0.001 off the old shirt, and the bra's centre 0.004 in front of it.

## Checks after every cut

- **Every face within one body triangle**, and every hem point on a body vertex or edge (to about 0.0001).
- **Triangle count**: a clean cut adds a handful of triangles. Compare with the garment before.
- **Thinnest angles**: none under about 5°. Look at where any under 10° are.
- **Open edges**: the hem loops you meant to have and no others. No edge held by more than two triangles.
- **Area**: against the garment before, or the example.
- **Copies at one place**: still at exactly one place each.
- **Renders**: at rest, with smooth normals worked out from the triangles (as the game does), and textured. Look from the front, side, back and a three-quarter view, with the garment beneath it (trousers) on, and again with the Fat and Hulk shapes. In M3AT's own exporter, its fit check gives how far a garment sinks into the body in every animation frame.

## Mistakes we made

- **Snapping by a share of the edge.** "Within a fifth of the edge" moved points up to 0.013 off the curve where the hips' triangles are long, which made a V at the front and a zigzag at the back. Snap by the distance along the edge.
- **Snapping too hard.** Taking body vertices up to 0.006 off the curve made the hem wavy. 0.0025 kept it smooth.
- **A plane cut through the garment's own triangles.** It left many small pieces near corners: 32 more triangles than a cut on the body's edges, and slivers along the hem.
- **Moving texels straight down by the hem's change in height.** It left kinks in the stitching at each notch's peak. Use the offset curve.
- **Painting filled faces from the nearest old texel.** It smeared the old edge into streaks.
- **Comparing points by rounding their places.** Blender's mesh places are single precision, so round to about 0.0004 in double precision, not by their raw float values.
- **Trusting a hem vertex's own normal.** Folded triangles at a hem gave one a normal straight up, and it was laid 0.06 off on the wrong surface. Filter only the opposite-facing side.
