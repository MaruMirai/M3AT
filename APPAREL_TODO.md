# Apparel to-do

Every vanilla and DLC wearable, read from the installed game's `Data/*/Defs`. Each line is `defName`, label, apparel layer (`OnSkin`, `Middle`, `Shell`, `Belt`, `Overhead`, `EyeCover`) and what it covers. Layers marked `~` are inherited from a parent def and are from memory, so check them in game.

Per-garment work: model it in Blender on the rig, give it a `layer` and `groups` (see `GARMENTS` in `anim_scripts/export_frames.py`), re-export, and check it in game.

Many items are the same shape in different materials or tiers. Do one mesh per shape first (the first box in each group), then the variants.

Boxes:
- `[x]` done: a garment of its own (or one it shares that fits it well enough).
- `[p]` drawn by a stand-in for now: another of our garments (named), or IsoRim's worn gear model of it ("IsoRim", `Source/WornGears.cs`).
- `[-]` not drawn, on purpose.
- `[ ]` not drawn yet.

## Body, on skin (shirts, pants)
- [x] Apparel_BasicShirt: T-shirt (OnSkin; torso, shoulders)
- [x] Apparel_CollarShirt: button-down shirt (OnSkin; torso, neck, shoulders, arms)
- [x] Apparel_Pants: pants (OnSkin; legs). Has a garment
- [x] Apparel_TribalA: tribalwear (OnSkin; torso, legs)
- [x] Apparel_PsyfocusShirt: eltex shirt (OnSkin; torso, shoulders) *Royalty*. Drawn as the CollarShirt (its `GARMENT_PROPS` `defs`)
- [x] Apparel_ShirtRuffle: formal shirt (OnSkin~; arms, neck) *Royalty*. Has a garment: the collared shirt with a jabot and cuff frills (`make_formal_shirt.py`), needs an export
- [x] Apparel_FlakPants: flak pants (OnSkin, Middle; legs). Has a garment (`FlakPants`: the Pants mesh retextured by Meshy after the flak jacket, `prompts.py`), check in game

## Body, middle layer (vests, armour)
- [x] Apparel_FlakVest: flak vest (Middle; torso, neck)
- [x] Apparel_PsyfocusVest: eltex vest (Middle; torso, neck, shoulders) *Royalty*
- [x] Apparel_Corset: corset (Middle~; torso) *Royalty*
- [x] Apparel_VestRoyal: formal vest (Middle~; torso) *Royalty*
- [x] Apparel_Sash: sash (Middle; torso) *Biotech*
- [-] Apparel_HeavyShield: heavy shield unit (Middle; torso) *Biotech*
- [x] Apparel_PowerArmor: marine armor (Middle, Shell~; torso, arms, legs)
- [x] Apparel_ArmorRecon: recon armor (Middle, Shell~)
- [x] Apparel_PlateArmor: plate armor (Middle, Shell; torso, neck, shoulders, arms, legs)
- [x] Apparel_ArmorCataphract: cataphract armor (Middle, Shell) *Royalty*
- [x] Apparel_ArmorCataphractPhoenix: phoenix armor *Royalty*. As cataphract armor
- [x] Apparel_ArmorCataphractPrestige: prestige cataphract armor *Royalty*. Has a garment (`ArmorCataphractPrestige`: the cataphract armour with gold scrollwork painted on by `prestige_paint.py`), check in game
- [x] Apparel_ArmorLocust: locust armor *Royalty*. As recon armor (a recon base def)
- [x] Apparel_ArmorMarineGrenadier: grenadier armor *Royalty*. As marine armor
- [x] Apparel_ArmorMarinePrestige: prestige marine armor *Royalty*. Has a garment (`PowerArmorPrestige`: the marine armour with gold scrollwork painted on by `prestige_paint.py`), check in game
- [x] Apparel_ArmorReconPrestige: prestige recon armor *Royalty*. Has a garment (`ArmorReconPrestige`: the recon armour with gold scrollwork painted on by `prestige_paint.py`), check in game
- [x] Apparel_MechlordSuit: mechlord suit (~) *Biotech*. The recon armour with a fin up from each shoulder, a blade from each side of the waist, a core on the belly and a disc on the back (`make_mechlord.py`)
- [x] Apparel_Vacsuit: vacsuit (Middle, Shell; whole body) *Odyssey*. Has a garment (`make_from_groups.py`'s `WHOLE`: the body's faces, gloves and boots; `make_vacsuit.py`'s smoothed one kept in `Models/unused/`), check in game
- [x] Apparel_VacsuitChildren: kid vacsuit (Middle, Shell; whole body) *Odyssey*. As the vacsuit (`AdultOf`)

## Body, outer layer (coats, robes)
- [x] Apparel_Jacket: jacket (Shell; torso, neck, shoulders, arms)
- [x] Apparel_Parka: parka (Shell; torso, neck, shoulders, arms). Has a garment
- [x] Apparel_FlakJacket: flak jacket (Shell; torso, neck, shoulders, arms). Has a garment
- [x] Apparel_Duster: duster (Shell; torso to legs). Has a garment
- [x] Apparel_Cape: cape (Shell; torso to legs). Has a garment (`make_cape.py`: a high collar, down the back behind the arms, opening out below the hands), check in game
- [x] Apparel_Robe: robe (Shell; torso, shoulders, arms, legs). Has a garment (`make_duster.py --robe`: the duster closed below a V neck, to the ankle, vented back and front from the shin down), check in game
- [x] Apparel_RobeRoyal: prestige robe (Shell) *Royalty*. The robe's mesh with its own texture (`RobeRoyal`: damask brocade, a patterned front panel), check in game
- [x] Apparel_PsyfocusRobe: eltex robe (Shell) *Royalty*. As the robe
- [x] Apparel_Burka: burka (Shell; head to legs) *Ideology*. Has a garment (`make_duster.py --burka`: the robe closed to the neck, to the top of the foot, vented only from the shin down, with a head piece joined on: `make_hats.py`'s `BurkaHood`, the broadwrap's cloth closed but for a slit over the eyes), untextured, check in game. Covers the torso, so IsoRim's head wear isn't used for it
- [x] Apparel_BodyStrap: slave body strap (Shell; torso) *Ideology*. Has a garment (`make_body_strap.py`: straps over the shoulders to a ring on the chest, one down to a ring on a belt round the waist, straight down the back to the belt), check in game
- [x] Apparel_Bandolier: heavy bandolier (Shell; torso) *Biotech*. Has a garment
- [x] Apparel_LabCoat: lab coat (Shell; torso, neck, shoulders, arms) *Anomaly*

## Children's clothes *Biotech*
- [x] Apparel_KidShirt: kid shirt (torso, shoulders). The BasicShirt (`AdultOf`)
- [x] Apparel_KidPants: kid pants (legs). The Pants (`AdultOf`)
- [x] Apparel_KidRomper: kid romper (torso to legs)
- [x] (no def) baby romper: `BabyRomper` (`make_baby_romper.py`: the kid romper sleeveless and short-legged), drawn on a baby a few hours old with nothing over its trunk or legs (`BabyRomperOf`), check in game
- [x] Apparel_KidTribal: kid tribalwear (torso, legs). The TribalA (`AdultOf`)
- [x] Apparel_KidParka: kid parka (Shell; torso, neck, shoulders, arms). The Parka (`AdultOf`)

## Belts and packs (Belt; waist)
All IsoRim's worn gear models, on our `belt`, `pack` and `hip` slots.
- [p] Apparel_ShieldBelt: shield belt. IsoRim (belt)
- [p] Apparel_SmokepopBelt: smokepop pack. IsoRim (belt)
- [p] Apparel_FirefoampopPack: firefoam pop pack. IsoRim (pack)
- [p] Apparel_PsychicShockLance: psychic shock lance. IsoRim (hip)
- [p] Apparel_PsychicInsanityLance: psychic insanity lance. IsoRim (hip)
- [p] Apparel_PackJump: jump pack *Royalty*. IsoRim (pack)
- [p] Apparel_PackBroadshield: low-shield pack *Royalty*. IsoRim (pack)
- [p] Apparel_PackBandwidth: bandwidth pack *Biotech*. IsoRim (pack)
- [p] Apparel_PackControl: control pack *Biotech*. IsoRim (pack)
- [p] Apparel_PackTox: tox pack *Biotech*. IsoRim (pack)
- [p] Apparel_BiomutationLance: biomutation lance *Anomaly*. IsoRim (hip)
- [p] Apparel_DeadlifePack: deadlife pack *Anomaly*. IsoRim (pack)
- [p] Apparel_DisruptorFlarePack: disruptor flare pack *Anomaly*. IsoRim (pack)
- [p] Apparel_PackTurret: turret pack *Anomaly*. IsoRim (pack)
- [p] Apparel_ShardPsychicInsanityLance: shard insanity lance *Anomaly*. IsoRim (hip)
- [p] Apparel_ShardPsychicShockLance: shard shock lance *Anomaly*. IsoRim (hip)
- [p] Apparel_CerebrexNode: cerebrex node *Odyssey*. IsoRim (pack)
- [p] Apparel_PackHunter: hunter pack *Odyssey*. IsoRim (pack)

## Hats (Overhead; upper head)
- [x] Apparel_SimpleHelmet: simple helmet
- [x] Apparel_AdvancedHelmet: flak helmet
- [x] Apparel_CowboyHat: cowboy hat
- [x] Apparel_BowlerHat: bowler hat (~)
- [x] Apparel_HatHood: hood
- [x] Apparel_Tuque: tuque
- [x] Apparel_TribalHeaddress: tribal headdress
- [x] Apparel_PsychicFoilHelmet: psychic foil helmet
- [x] Apparel_Beret: beret *Royalty*. Has a garment (`make_hats.py`), check in game
- [x] Apparel_HatTop: top hat (~) *Royalty*. Has a garment (`make_hats.py`), check in game
- [x] Apparel_HatLadies: ladies hat (~) *Royalty*. Has a garment (`make_hats.py`), check in game
- [x] Apparel_Coronet: coronet *Royalty*. Has a garment (`make_hats.py`, low-poly: a pointed band with balls on its tips, stood off the head over the hair, which stays), check in game
- [x] Apparel_Crown: crown *Royalty*. Has a garment (`make_hats.py`, low-poly: a pointed band round a closed cap, the hair tucked), check in game
- [x] Apparel_CrownStellic: stellic crown *Royalty*. Has a garment (`make_hats.py`, low-poly: a gold helm with a brow dipping over the nose and cheek guards, and a flat crest across its top, two tall middle spikes and a swept wing out to either side), check in game
- [x] Apparel_EltexSkullcap: eltex skullcap *Royalty*. Has a garment (`make_hats.py`: a close cap cut away over the eyes and in front of the ears, a tongue down the forehead, a spine down to the nape, the hair tucked), check in game
- [x] Apparel_PsyfocusHelmet: eltex helmet *Royalty*. Has a garment (`make_hats.py`: a light shell cut in arches over the eyes and ears, a tongue over the nose, a point at each temple and a raised plate from the nose over the top to the nape, the hair tucked), check in game
- [x] Apparel_Gunlink: gunlink *Royalty*. Has a garment (`make_hats.py`: a grey unit over the right ear, a rod up past the head and a white arm round to the right eye, flat colours, the hair and face kept), check in game
- [x] Apparel_AuthorityCap: authority cap *Ideology*. Has a garment (`make_hats.py`: a peaked cap, the crown spreading past the band and rising to the front, a dark visor, flat shades under the apparel's colour), check in game
- [x] Apparel_Flophat: flophat *Ideology*. Has a garment (`make_hats.py`: a thick knitted cap in rolls, slumped back and down behind the head), check in game
- [x] Apparel_Headwrap: headwrap *Ideology*. Has a garment (`make_hats.py`: wound cloth over the hair, high in a V over the forehead, to the nape behind; leaves the hair off), needs an export, check in game
- [x] Apparel_Shadecone: shadecone *Ideology*. Has a garment (`make_hats.py`: a wide shallow cone with a knob on top, tipped back, on a headband), needs an export, check in game
- [x] Apparel_Slicecap: slicecap *Ideology*. Has a garment (`make_hats.py`: a crescent of shell over the left of the scalp, after sprite A; the game's three variants are one garment here), needs an export, check in game (the hair is tucked on its side only)
- [x] Apparel_Tailcap: tailcap *Ideology*. Has a garment (`make_hats.py`: a flat-topped fur cap with a ragged edge, a darker tail hanging from its back on the right, the hair tucked), check in game
- [x] Apparel_TortureCrown: torture crown *Ideology*. Has a garment (`make_hats.py`: a band round the forehead stood over the hair, spikes up from its top and shorter ones down from its bottom leaning in), check in game
- [x] Apparel_AirwireHeadset: airwire headset *Biotech*. Has a garment (`make_hats.py`: a grey unit over the right ear, a thin antenna up past the head with two crossbars, a white boom forward round the temple, flat colours, the hair and face kept), check in game
- [x] Apparel_ArrayHeadset: array headset *Biotech*. Has a garment (`make_hats.py`: a dark visor over the eyes, a white stripe across it, round to a pod over each ear with an antenna up from it leaning back, flat colours, the hair kept), check in game
- [x] Apparel_IntegratorHeadset: integrator headset *Biotech*. Has a garment (`make_hats.py`: a bar hooked over the right of the crown down to a pad on the ear, white down its side, and a darker boom round the jaw to a ball microphone, flat colours, the hair and face kept), check in game
- [x] Apparel_KidHelmet: kid helmet *Biotech*. Drawn as the SimpleHelmet (`AdultOf`)

## Full-head helmets and masks (Overhead; full head)
- [x] Apparel_PowerArmorHelmet: marine helmet (~)
- [x] Apparel_ArmorHelmetRecon: recon helmet (~)
- [x] Apparel_WarMask: war mask. Has a garment
- [x] Apparel_WarVeil: veil. Has a garment
- [x] Apparel_ArmorHelmetCataphract: cataphract helmet *Royalty*
- [x] Apparel_ArmorHelmetCataphractPrestige: prestige cataphract helmet *Royalty*. Has a garment (`ArmorHelmetCataphractPrestige`: the cataphract helmet with its face gilded and a gold fin, `make_prestige_helmets.py`), check in game
- [x] Apparel_ArmorHelmetReconPrestige: prestige recon helmet *Royalty*. Has a garment (`ArmorHelmetReconPrestige`: the recon helmet with gold wings, `make_prestige_helmets.py`), check in game
- [x] Apparel_ArmorMarineHelmetPrestige: prestige marine helmet *Royalty*. Has a garment (`ArmorMarineHelmetPrestige`: the marine helmet with a transverse gold crest, `make_prestige_helmets.py`), check in game
- [x] Apparel_VisageMask: visage mask *Ideology*. Has a garment (`make_hats.py`: a rigid shield over the face in the stuff's colour, a flat brow, a point at the chin, a nose down its middle and round eye holes the pawn's eyes show through (the second variant; the three are one garment), a thin strap behind), check in game
- [x] Apparel_Broadwrap: broadwrap (head, neck, shoulders) *Ideology*. Has a garment (`make_hats.py`: a cloth over the head with an oval opening for the face, hanging from the head and the chin to a hem over the shoulders, on the body's weights; leaves the hair off), check in game
- [x] Apparel_GasMask: gas mask *Biotech*. Has a garment (`make_hats.py`: a dark rubber face piece, two round glass lenses (`Visor`) in raised rims the eyes show through, a snout to a filter canister with a brass band, straps round the head and over the crown, flat colours, the hair and face kept), check in game
- [x] Apparel_ArmorHelmetMechCommander: mechcommander helmet (~) *Biotech*. The recon helmet with a fin over each ear (`make_mech_helmets.py`)
- [x] Apparel_ArmorHelmetMechlordHelmet: mechlord helmet (~) *Biotech*. The recon helmet with a ribbed fan behind the head (`make_mech_helmets.py`)
- [x] Apparel_CeremonialCultistMask: ceremonial hood *Anomaly*. Has a garment (`make_hats.py`: the broadwrap's cloth drawn up into a point and falling as a bell to the shoulders, open down the front, a dark bioferrite mask in the opening with scowling eye sockets the eyes show through and long teeth over the chin; its two styles one garment; leaves the hair off), needs an export, check in game
- [x] Apparel_CultistMask: ritual mask *Anomaly*. Has a garment (`make_hats.py`: a skull's face in the stuff's colour from a dome over the forehead to four fangs below the chin, scowling slanted eye sockets the eyes show through, a nose hole, a strap behind; its four styles one garment; keeps the hair, which may poke through the brow), needs an export, check in game
- [x] Apparel_VacsuitHelmet: vacsuit helmet *Odyssey*. As marine helmet

## Face and neck accessories
- [x] Apparel_ClothMask: face mask (Overhead; mouth). Has a garment (`make_hats.py`: over the nose and mouth, tied over the ears), untextured, check in game
- [x] Apparel_Blindfold: blindfold (EyeCover; eyes) *Ideology*. Has a garment.
- [x] Apparel_Collar: slave collar (Overhead; neck) *Ideology*. Has a garment (`make_hats.py`: a band round the neck with riveted side plates and a ring at the throat, on the body's weights, let out over what is worn at the neck), needs an export, check in game

## Combat Extended's shields (Shield; left shoulder)
Held in the left fist (`walk.shield_at`'s guard, `Source/Shields.cs`), shown only when CE would draw them (drafted, or a job or duty that shows the weapon).
- [x] CE_Apparel_MeleeShield: melee shield. Has a garment (`make_shields.py`: a dished twelve-sided shield of planks with an iron rim and boss), check in game
- [x] CE_Apparel_BallisticShield: ballistic shield. Has a garment (`make_shields.py`: a tall curved plate with a glass viewport in a pale frame and four bolts), check in game

## Not worn clothes
Targeters (`OrbitalTargeter*`) and `TornadoGenerator` have an apparel block but are not worn clothes. They are drawn anyway, as IsoRim's worn gear models in the `hip` slot.

## Texturing on our own UVs (Meshy retexture)

Meshy's own unwraps are chaotic (hundreds of scattered islands), so the texture can't be touched up by hand and is lost whenever a `make_*.py` script is re-run. The aim is to send garments with UVs we made and have Meshy paint on them (`enable_original_uv`). What's there and what's missing:

- [x] `place_textured.py strip --keep-uvs` leaves the UVs on the upload; `place` doesn't care whose UVs came back (it matches by place and triangle).
- [x] `meshy.py multitexture --keep-uvs` (image-guided, meshy-7) sends `enable_original_uv`.
- [x] `meshy.py texture --keep-uvs` (the text prompt one `prompts.py` uses).
- [x] `generationAssets/textures/prompts.py` sends a garment in `OWN_UVS` with `--keep-uvs`. To redo an old Meshy-UV garment, move its work folder's `2_textured.glb` aside first, or the run skips it.
- [x] Tried on the LabCoat (2026-11-05): Meshy kept every UV exactly, painted a 2048 image with the pockets, buttons and hem stitching where they belong, and filled the gaps between islands with smeared colour, so a downscale bleeds only what's beside an island's own edge. Dark smears where an island's edge runs along a fold (the armpits, the shoulder seams): watch for them on the sleeves. Still to check in game.
- [ ] UVs for the untextured script-made garments (`make_duster.py`'s `add_uvs` does the Duster, Robe, Burka and LabCoat; no UVs yet: `Burka` (re-run it), `blindfold`, `clothMask`, `hatHood`, and the Furskin, which needs none). Best made by the scripts themselves, so a re-run keeps them and a texture painted on them stays good while the topology doesn't change:
  - ring-built pieces (the duster/robe/burka/lab coat skirt, the cape, the parka's barrel and sleeves, the hats' crowns and brims) have rows and columns already, so a cylinder unwrap per piece cut at the back (or under the arm) is a few lines in each script;
  - the parts cut from the body (the duster family's top, `make_from_groups.py`'s garments, `Shoes`) already carry the body's atlas from `unwrap_pawn.py` (seams from the bones, front/back panels): keep those islands but pack them to fill the square (`pack_islands` keeping scale and rotation), as on the full atlas the garment uses only a small part of the image;
  - anything else (the masks and blindfold) gets a generic unwrap: an `unwrap_garment.py` marking seams from the openings plus a back or side line, as `unwrap_pawn.py` does for the body.
- [ ] Keep the islands upright and the texel density even across a garment (the head wear can take more), so the image can be read and painted on by hand.
- [ ] Once a garment has our UVs, re-texture the old Meshy-UV ones (Duster, Parka, Cape, Robe ...) the same way as they're next re-made; a `make_*.py` re-run with unchanged triangles can then reuse the old image instead of 10 more credits.
