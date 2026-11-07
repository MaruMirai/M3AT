# Gene to-do

Every vanilla and DLC gene that changes how a pawn looks, read from the installed game's `Data/*/Defs`. The hair and skin colour genes are in Core; everything else is Biotech. Royalty, Ideology, Anomaly and Odyssey add none. Each line is `defName`, label, then what the game draws for it: a render node (texture path under `Things/Pawn/Humanlike/`, parent node, colour) or a def field it sets.

Genes come in three kinds of work:
- **Already follows**: the gene only changes a value this mod already reads (`story.SkinColor`, `story.HairColor`, `story.bodyType`, `story.hairDef`). Check it in game and tick it.
- **Shape**: the gene picks a head type or body type. These are body shape keys in Blender (like `Shrinkhead`), written to `skin.txt` and picked in `ShapeOf`.
- **Part**: the gene adds a render node. These are meshes on the rig, like garments, but chosen by the pawn's genes instead of its apparel. A head part rides the head bone (`spine.006`) the way hair does; a body part gets weights. Each needs its own draw in `SkinnedFigure` and a colour from the node's `colorType` (`Skin`, `Hair`, or none for the texture's own colour).

Do one mesh per shape first (the first box in each group), then the variants.

## Already follows
- [ ] Skin_InkBlack, Skin_SlateGray, Skin_LightGray, Skin_SheerWhite, Skin_Blue, Skin_Purple, Skin_PaleRed, Skin_DeepRed, Skin_PaleYellow, Skin_DeepYellow, Skin_Orange, Skin_Green: skin colours (`skinColorOverride`, read through `story.SkinColor`)
- [ ] Skin_Melanin1..9: natural skin tones (Core; read through `story.SkinColor`)
- [ ] Hair_SnowWhite, Hair_InkBlack, Hair_Gray, Hair_DarkBlack, Hair_MidBlack, Hair_DarkReddish, Hair_DarkSaturatedReddish, Hair_DarkBrown, Hair_ReddishBrown, Hair_SandyBlonde, Hair_Blonde, Hair_Pink, Hair_LightPurple, Hair_LightBlue, Hair_LightTeal, Hair_LightGreen, Hair_LightOrange, Hair_BrightRed: hair colours (Core, `hairColorOverride`), and Hair_Grayless (Biotech). All read through `story.HairColor`.
- [ ] Hair_BaldOnly, Hair_ShortOnly, Hair_LongOnly: hairstyle filters (`hairTagFilter`; they only narrow which `hairDef` the pawn gets)
- [ ] Body_Standard, Body_Thin, Body_Fat, Body_Hulk: body types (`bodyType`; already shape keys)

## Shape (head types)
- [x] Jaw_Heavy: heavy jaw (`forcedHeadTypes` Male_HeavyJawNormal, Female_HeavyJawNormal, Furskin_Heavy1..3). The head's `Jaw_Heavy` key (`WalkFigures.GeneKeysOf`, not yet checked in game).
- [x] Head_Gaunt: gaunt head (`forcedHeadTypes` Gaunt, Furskin_Gaunt). The head's `Head_Gaunt` key (`WalkFigures.GeneKeysOf`, not yet checked in game).
- [ ] Hair and face patches on the new keys: re-run hair.py and check `FACE_*` still lands on the jaw and gaunt heads.

## Head parts (parent Head)
- [ x] Ears_Cat: cat ears (`HeadAttachments/CatEars/CatEars`, skin colour). Drawn as the rig's cat ears (`Source/CatEars.cs`, not yet checked in game). With the pig and floppy ears below, takes the chibi's own off by the head's `No_Human_Ears` key (`anim_scripts/make_no_human_ears.py`, `WalkFigures.GeneKeysOf`).
- [x] Ears_Pointed: pointed ears (`HeadAttachments/PointedEars/PointedEars`, skin colour). The head's `Ears_Pointed` key (`WalkFigures.GeneKeysOf`, not yet checked in game).
- [ x] Ears_Floppy: floppy ears (`HeadAttachments/FloppyEars/FloppyEars`, skin colour). Drawn as the rig's floppy ears in the hair colour (`ears_floppy.txt`, `Source/CatEars.cs`, moved by their curl: straighter perked up, tucked in hit or asleep; not yet checked in game).
- [x ] Ears_Pig: pig ears (`HeadAttachments/PigEars/PigEars`, skin colour). Drawn as the rig's pig ears (`ears_pig.txt`, `Source/CatEars.cs`, moving as the cat ears do; not yet checked in game).
- [ x] Nose_Pig: pig nose (`HeadAttachments/PigNose/PigNose`, skin colour). The head's `Nose_Pig` key (`WalkFigures.GeneKeysOf`) and the nostrils on the face patch (`ridges_snout.png`, from Gemini by `generationAssets/pigSnout/`; checked in a Blender mock-up against Facial Animation's layers, not yet in game). `anim_scripts/make_nose_pig.py` no longer makes the key in the .blend, which has four vertices moved up and out as well as forward.
- [x] Headbone_MiniHorns: mini-horns (`HeadAttachments/MiniHorns/MiniHorns`, own colour). Drawn from `horns_mini.txt` (`anim_scripts/make_horns.py`, `Source/Horns.cs`, not yet checked in game).
- [x] Headbone_CenterHorn: center-horn (`HeadAttachments/CenterHorn/CenterHorn`, own colour). Drawn from `horns_center.txt`, the same way.
- [x] Brow_Heavy: heavy brow (`HeadAttachments/HeavyBrow/HeavyBrow`, skin colour, layer 51). The head's `Brow_Heavy` key (`WalkFigures.GeneKeysOf`, not yet checked in game).
- [x] FacialRidges: facial ridges (several `texPaths`, skin colour, layer 51). Three designs of our own on the face patch, one per pawn (`anim_scripts/make_ridges.py`, `WalkFigures.RidgesOf`, `SkinnedFigure.DrawRidges`, not yet checked in game).
- [ x] Hair and hats over ears and horns: decide whether hair hides them, and whether hats push them out of the way or hide them.

## Eyes (parent Head)
- [x] Eyes_Red: red eyes (`HeadAttachments/RedEyes/Male/RedEyes_Male`, `PawnRenderNodeProperties_Eye`). Through Facial Animation's eye layers (`FaceLayers.GenesPostfix`, `Patches/EyeGenes.xml`); not yet checked in game.
- [x] Eyes_Gray: gray eyes (`HeadAttachments/GrayEyes/Male/GrayEyes_Male`). The same.

Without Facial Animation the head stays bare, so these would be face layers too.

## Body parts (parent Body)
- [x ] Tail_Furry: furry tail (`BodyAttachments/FurryTail/FurryTail`, hair colour). Drawn as the rig's cat tail made shorter and tufted like the Furskin gene (`anim_scripts/fur_tail.py`, `tail_furry.txt`; `Source/CatTail.cs`, swings on springs with the hips; not yet checked in game).
- [ x] Tail_Smooth: smooth tail (`BodyAttachments/SmoothTail/SmoothTail`, hair colour). Draws the rig's cat tail as it is.
- [x ] Tails and long coats or robes: decide whether the garment hides the tail or the tail pokes through.

## Described, not drawn
The game draws nothing for these (no render node; vanilla draws no hands or feet), but the gene's description says how the body looks, and the chibi's hands and feet show. Shape keys on the body, picked by `WalkFigures.GeneKeysOf` like the head's.
- [x] Hands_Pig: trotter hands ("hands that partially resemble pig trotters"; Endogene, `Hands` exclusion with Hands_Human and ElongatedFingers). A key fusing the fingers into two hoof-like digits and the thumb shortened; `walk.close_hand`'s fist should still read. The body's `Hands_Pig` key (`WalkFigures.GeneKeysOf`, not yet checked in game).
- [x] ElongatedFingers: elongated fingers ("long, delicate fingers"). A key lengthening the finger bones' share of the hand; the finger joints move with it (`db`), so held tools and weapons still meet the fist. The body's `ElongatedFingers` key (`WalkFigures.GeneKeysOf`, not yet checked in game).
- [x] WebbedPhalanges: webbed phalanges ("webbed fingers and toes"; Movement category). A web between the fingers (spread a little so it shows), and the toes if feet ever show under shoes. The body's `WebbedPhalanges` key (`WalkFigures.GeneKeysOf`, not yet checked in game).

Smaller, optional (described only, mostly hidden at the chibi's size):
- [ ] Bloodfeeder: "small retractable fangs". A fang layer on the face when the mouth is open, or while biting (the `Bloodfeed` job).
- [ ] DarkVision: "a reflective layer behind the retina". Eyeshine at night (a glow on the eye layers, as cats' eyes catch light).
- [ ] PiercingSpine: "an opening in their upper chest". A slit on the chest skin, seen only bare-chested.
- [ ] Beauty_Ugly, Beauty_VeryUgly ("exaggerated facial features and poor skin"), Beauty_Pretty, Beauty_Beautiful: nothing drawn in vanilla; left as they are.

## Fur
- [ x] Furskin: the outline's tufts are in (`anim_scripts/make_fur_edges.py`, not yet checked in game); the fur texture is made (`anim_scripts/make_fur_texture.py`: `generationAssets/furskin/fur_stripes.png`), but the game can't draw it yet: `skin.txt` has no texture places for the body and the body has no textured material. Furskin: furskin (`PawnRenderNode_Fur` on the body, hair colour, layer 5; also forces the `Furskin_*` head types, long or furskin beards only, skin coloured as hair, no tattoos). The fur art is per body type, so this is a texture on the body mesh (and later a lifted shell, like a garment) rather than a new shape. The `Furskin_*` head types should map to the same keys as their plain ones.

## Not genes
Beards come from `pawn.style.beardDef`, not genes (Beard_BushyOnly, Beard_NoBeardOnly and Beard_Always only filter it), so they are built with the hair (see CLAUDE.md, not yet checked in game). Voice and beauty genes change nothing drawn. Anomaly's mutants and the bionic and archotech parts add render nodes through hediffs, not genes, so they are left out.
