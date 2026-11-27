using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace M3AT
{
    // How pawns' beards are drawn: flat on the face from their sprites (2D), as 3D meshes built from them, or not at all.
    // Which headgear leaves a gene's animal ears off (WalkFigures.EarHiding): any that leaves the hair off or tucks it,
    // only armour helmets (the ArmorHeadgear category), or none.
    public enum EarHiding
    {
        AnyHeadgear,
        Helmets,
        Never,
    }

    // How the chibi's outline is drawn (SkinnedFigure.Mode): a shell round the figure shown only outside its silhouette
    // (stencil bit 64), the same shell over the body too as IsoRim draws its figures', or found on screen from each part's
    // edge (ScreenOutlines.cs), which also goes round open meshes and thin parts and round a held weapon over the body.
    // Kept in this order, as the menu lists them.
    public enum OutlineMode
    {
        Silhouette,
        Lines,
        ScreenSpace
    }

    public enum BeardMode
    {
        Flat,
        ThreeD,
        Off
    }

    // What a pawn lying in a bed is drawn wearing (WalkFigures.SleepWearOf), wherever its body shows (IsoRim's blanket is
    // the bedBlankets option, apart from this, as not every bed has one): as dressed; without armour, headgear, packs or
    // coats; in pajamas; or nothing. Kept in this order, as the menu lists them.
    public enum SleepWear
    {
        Dressed,
        NoEquipment,
        Pajamas,
        Nothing
    }

    // What a colonist's armour helmet does while it isn't drafted (WalkFigures.HelmetStowOf, HelmetStow.cs): worn as
    // ever; taken off (the hat's dress_head clip backwards) and carried on the back, smaller, then put on again when
    // drafted; simply not drawn; or never drawn, drafted or not. Kept in this order, as the menu lists them.
    public enum HelmetStow
    {
        Worn,
        OnBack,
        Hidden,
        AlwaysHidden
    }

    // The player's choices, saved with the game's mod settings (Options > Mod settings > M3AT).
    public class Settings : ModSettings
    {
        public const float DefaultShrink = 0f;   // the head shrink the options start at: the full chibi head
        public const float DefaultHeightSpread = 0.02f, DefaultBodySpread = 0.3f;   // the variety sliders' defaults
        public const float DefaultHairDetail = 0.3f;
        public const float DefaultGazeStrength = 1f;   // how far the eyes look about, of Facial Animation's own   // the share of its triangles a hairstyle keeps
        // Cells: the screen-space outline's widths, the pawn's about as wide as the shell outline's (SkinnedFigure.OutlineWidth),
        // a held weapon's half that, as a full line round a thin barrel read as heavy.
        public const float DefaultPawnOutlineWidth = 0.03f, DefaultWeaponOutlineWidth = 0.015f;
        // Cells a part of a pawn must stand in front of the rest of it for the screen-space self-outline: an arm across the
        // chest is about 0.1 to 0.2 in front, a curve of the body seen side on gains far less over a line's width.
        public const float DefaultSelfOutlineStep = 0.08f;
        // The screen-space outline's edge pass (MarusEdge.frag.glsl's _EdgeShape and _EdgeTune.x): the widest line in pixels
        // at 1080 rows, the quick look's points a ring and its rings, the full search's most rings. The defaults are 10.3.3 at
        // 10 pixels (the old Detailed 8.2.10 at 16 cost more and looked no better); FasterOutline* is 6.2.3 at 8. Measured on
        // the performance test save at 2814x2125, 6 chibis in view 6 cells off: the silhouette 11.96 ms a frame, the defaults
        // +1.0, Faster +0.7 (the old 6.2.6 at 8 +0.5); the quick look's points are most of the difference, the other numbers
        // within noise. 22 cells off, 10 chibis: every setting within noise (about 14.3 ms).
        public const float DefaultOutlineReach = 10f;
        public const int DefaultOutlineProbeDirs = 10, DefaultOutlineProbeRings = 3, DefaultOutlineRings = 3;
        public const float FasterOutlineReach = 8f;
        public const int FasterOutlineProbeDirs = 6, FasterOutlineProbeRings = 2, FasterOutlineRings = 3;
        // How tall a grown pawn is drawn, cells, before gender and height variety; -1 (the default, not saved) the export's
        // standard (WalkFigures.StandardHeight). Absolute, so a height chosen stays when the standard changes
        // (WalkFigures.PawnSize, in SizeOf).
        public float pawnHeight = -1f;
        public const float HeightLeast = 1f, HeightMost = 1.7f;   // cells, the slider's range
        private const float OldStandardHeight = 1.5f;   // cells: a grown pawn of the standard size when the option was a share of it
        public float headShrink = DefaultShrink;   // 0 .. 1 of the Shrinkhead key: a smaller, less chibi head (skinned figure only)
        public bool genderSizes = true;   // grown males are drawn bigger than grown females, by WalkFigures.GenderGap (WalkFigures.GenderSize)
        public float heightSpread = DefaultHeightSpread;   // the standard deviation of pawns' own heights about their gender's, of the standard size (WalkFigures.HeightSize)
        public bool skeletons = true;   // a person gone to bones (a dessicated corpse, a shambler raised from one) drawn as the chibi's skeleton (Skeletons.cs, WalkFigures.Skeletons); off: IsoRim's own skeleton
        public bool agingLooks = true;   // old pawns drawn wrinkled, stooped and a little shorter, with cataracts clouding the eye (Aging.cs, WalkFigures.AgingLooks)
        public bool greyingHair = true;   // hair drawn greying with age, a dye or other new colour starting it again (GreyingHair.cs, WalkFigures.GreyingHair)
        public bool sharpFaceArt = true;   // face art read a sharper mip and its edges re-sharpened when magnified (MarusFace.frag.glsl's FaceArt)
        public bool raceMouthInk = true;   // soft mouths (races', Experimentals') redrawn with the retexture's hard lines (FaceRestyle.cs)
        public bool mouthSizeFit = true;   // races' mouths sized to a human's and very wide mouth shapes narrowed (FaceRestyle.cs, FaceLayers.MouthSizeFit)
        public int garmentDetail = 1;   // Auto Optimization: 0 High, 1 Default, 2 None (kept as made, to the ceiling), 3 Low (GarmentBudget.cs; read as the chibi loads)
        public bool raceParts = true;   // a race's ears, tails, horns and wings built in 3D from their sprites on the chibi (AddonParts.cs)
        public bool otherRaces = true;   // humanlike races with no IsoRim alien pack drawn as chibis rather than flat cards (OtherRaces.cs)
        public bool variedBodies = true;    // each pawn's build spread about its body type's by the Thin, Fat and Hulk keys (WalkFigures.VariedBuild)
        public float bodySpread = DefaultBodySpread;   // and the standard deviation of that spread, in key amounts
        public float bodyShapeStrength = 1f;   // how strongly the Thin, Fat and Hulk keys are applied, 0 .. BodyShapeMost of each (WalkFigures.BodyShapeStrength)
        public bool ragdolls = true;   // pawns that go down where they stand fall limp (WalkFigures.Ragdolls)
        public float flingPower = 1f;   // how far blows throw bodies and weapons, of the standard distance (WalkFigures.FlingPower)
        public bool flingBodies = true;   // a big enough blow throws a body across the map, the game moving it (WalkFigures.FlingBodies, Flings)
        public bool flingWeapons = true;   // the weapon a pawn drops as it goes down flies from its hand (WalkFigures.FlingWeapons, Flings)
        public bool wornGear = true;   // packs, belts, headsets, masks and crowns a pawn wears are drawn on it (WalkFigures.WornGears)
        public bool muzzleFlash = true;   // a gun fired by a 3D pawn flashes at its barrel (WalkFigures.MuzzleFlashes)
        public bool hitFlash = true;   // a pawn hit flashes red, as the game's sprites do (HitFlash.On)
        public bool holsters = true;   // weapons a pawn has but isn't holding are drawn on its body (WalkFigures.Holsters)
        public bool shoes = true;   // pawns with the torso and legs covered have shoes on (WalkFigures.Shoes)
        public bool armourHidesTail = true;   // body armour (a garment with hide_clothes, not a vacsuit) leaves the gene tail off (WalkFigures.ArmourHidesTail)
        public EarHiding earHiding = EarHiding.Helmets;   // which headgear leaves the gene ears off (WalkFigures.EarHiding)
        public BeardMode beardMode = BeardMode.Flat;   // how beards are drawn (WalkFigures.Beards, FlatBeards)
        public bool powerArmourBulk;   // Spacer power armour over the torso, arms and legs draws its wearer armourBulk bigger and broader (ArmourSize.cs)
        public float armourBulk = DefaultArmourBulk;   // that size, of the pawn's own; GrimWorld's Space Marine armour's too (marineBulk)
        public const float DefaultArmourBulk = 1.25f;   // a Space Marine of about 2.2 m to a person's 1.75
        public bool marineBulk = true;   // GrimWorld 40,000's Space Marine and Custodes armour draws its wearer armourBulk bigger and broader (ArmourSize.cs)
        public bool armourDrawScale = true;   // body armour a mod draws scaled up when worn (its apparel drawData scale: GrimWorld's Custodes, Valdor) draws its wearer that much bigger
        public bool gripToolButton = true;   // the M3AT tools' toggle among the play settings, bottom right of the map (GripTool.cs)
        public bool gripTool = false;        // that toggle: Adjust grip and Adjust clothing buttons on pawns (GripTool.cs, ClothingTool.cs)
        // The player's own choice of garments for apparel with none of its own, by apparel defName (ClothingTool.cs,
        // WalkFigures.ChosenStandIns): the garments' names joined by commas, "" for none drawn.
        public Dictionary<string, string> clothingChoices = new Dictionary<string, string>();
        // Apparel, by defName, whose stand-ins are left plain though spriteClothes is on: its own art painted on them looked
        // wrong (ClothingTool.cs, WalkFigures.PaintedArt).
        public List<string> plainClothes = new List<string>();
        // Apparel, by defName, whose content pack garment is painted with its own art (opt-in, unlike stand-ins: a pack's
        // garment has an image made for it; ClothingTool.cs, WalkFigures.PaintedArtFor).
        public List<string> paintedPackClothes = new List<string>();
        // The player's choice of a content pack's or M3AT's content, by "<packageId>|<part>" (lower case): "pack" or "m3at";
        // only choices other than the default are kept (ContentSources.cs). An option: Restore all defaults clears it.
        public Dictionary<string, string> contentChoices = new Dictionary<string, string>();
        // Whether the content manager's notice has been shown (ContentSources.QueueNotice): once ever, the first time any
        // content pack is loaded. Not an option. A new key, not the old fading message's contentSeen list, so players who
        // saw that get the notice once too.
        public bool contentNoticeShown;
        public bool contentButton = true;
        // Each content pack's cost as the benchmark last measured it, by packageId (lower case): "ms;noise;people;date" (Benchmark.cs). Not an option.
        public Dictionary<string, string> packCosts = new Dictionary<string, string>();   // the M3AT button on the bottom bar in game while content packs are active (ContentWindow.cs)
        // The player's own grips, by weapon defName (GripTool.cs, WalkFigures.GripOverrideOf): x and y the fist's place on
        // the weapon's art (0..1, y up; x below 0 for the grip left as it was), z degrees the weapon is turned in the fist.
        public Dictionary<string, Vector3> gripOverrides = new Dictionary<string, Vector3>();
        // Where the other hand holds a weapon on its art (0..1, y up), by weapon defName (GripTool.cs, GripOffHand).
        public Dictionary<string, Vector2> offHandGrips = new Dictionary<string, Vector2>();
        // The player's tuning of each face mod's art on the chibi's face (FaceTuning.cs, FaceLayers.TuneOf), by the mod's
        // packageId: x and y the move (face texture widths, + right and up), z the size (1 as it is); the whole face's, and
        // the mouth's on top of it.
        public Dictionary<string, Vector3> faceTunes = new Dictionary<string, Vector3>();
        public Dictionary<string, Vector3> mouthTunes = new Dictionary<string, Vector3>();
        // The automatic fit (FaceGaze.cs) each tuned face mod was tuned on, which it keeps being drawn with, so a later change
        // to the measuring leaves a face as the player left it: the eyes' scale, rise and spread, and the eyes' row and the
        // mouth's move. A tuned mod without one was tuned before the fit was pinned (FacePins.cs).
        public Dictionary<string, Vector3> faceFits = new Dictionary<string, Vector3>();
        public Dictionary<string, Vector2> faceFitRows = new Dictionary<string, Vector2>();
        public Dictionary<string, float> faceFitMouths = new Dictionary<string, float>();   // and the mouth's size (none: 1, pinned before it was fitted)
        public bool autoGrips = true;   // another mod's weapon held by its grip as found in its art (WalkFigures.AutoGrips, GripFinder)
        public bool retexturedWeaponModels = true;   // the game's own weapons keep IsoRim's model though another mod redraws their art (WalkFigures.RetexturedWeaponModels)
        public bool vatClothes = false;    // a pawn in a growth vat or biosculpter pod drawn in its clothes (WalkFigures.VatClothes); off: without them
        public bool bedBlankets = false;   // IsoRim's blanket over the body in a bed that shows only the head (WalkFigures.BedBlankets); off: lying on top
        public HelmetStow helmetStow = HelmetStow.Worn;   // a colonist's helmet while undrafted (WalkFigures.HelmetStowOf)
        public SleepWear sleepWear = SleepWear.NoEquipment;   // what a pawn in bed is drawn wearing (WalkFigures.SleepWearOf)
        public bool spriteClothes = true;   // a garment standing in for another mod's apparel is painted with that apparel's own sprites (SpriteClothes.cs, WalkFigures.SpriteClothes)
        public bool groundGarments = false;   // apparel lying on the map is drawn as the chibi's own garment for it (WalkFigures.GroundGarments); off: IsoRim's look
        public bool injuryAnims = true;   // pawns limp on a bad leg and hold where they are hurt (WalkFigures.InjuryAnims); a missing leg hops regardless
        public bool prosthetics = true;   // a replaced limb is drawn as its prosthetic (WalkFigures.Prosthetics)
        public float gazeStrength = DefaultGazeStrength;   // how far Facial Animation's eyes move on the chibi (FaceLayers.GazeStrength)
        public bool talkingMouths = true;   // a talking pawn's mouth opens and shuts on syllables (FaceLayers.TalkingMouths)
        public bool prostheticEyes = true;   // and a bionic or archotech eye's iris; off: the pawn's own eye (WalkFigures.ProstheticEyesShow)
        public bool archotechProsthetics = true;   // and an archotech one too; off: as flesh (WalkFigures.ArchotechShows)
        public bool visibleWounds = true;   // injuries, blood, bandages and scars are drawn on the pawn (WalkFigures.VisibleWounds)
        public bool outlines = true;   // the chibi is drawn with its dark outline (SkinnedFigure.Outlines)
        public OutlineMode outlineMode = OutlineMode.Silhouette;   // how the outline is drawn (SkinnedFigure.Mode); saved before as silhouetteOutlines, off being Lines
        public float pawnOutlineWidth = DefaultPawnOutlineWidth;   // cells: the screen-space outline's width round a pawn (ScreenOutlines.cs)
        public float weaponOutlineWidth = DefaultWeaponOutlineWidth;   // cells: and round a weapon it holds, and between the two
        public float outlineReach = DefaultOutlineReach;   // pixels at 1080 rows: the widest a screen-space line is drawn close up (ScreenOutlines.cs)
        public int outlineProbeDirs = DefaultOutlineProbeDirs;   // the edge pass's quick look: points on each ring
        public int outlineProbeRings = DefaultOutlineProbeRings;   // and its rings
        public int outlineRings = DefaultOutlineRings;   // the full search's most rings, finer steps across a wide line
        public bool screenSelfOutlines = false;   // the screen-space outline also over the pawn's own body, where a part is well in front of the rest
        public float selfOutlineStep = DefaultSelfOutlineStep;   // cells: how far in front a part must be for that line (ScreenOutlines.cs)
        public bool firstPersonEye = true;   // IsoRim's first person looks out from the chibi's eyes (WalkFigures.FirstPersonEye); off: IsoRim's eye height option
        public bool clothesOcclusion = true;     // IsoRim's ambient occlusion and screen-space shadows on clothes and hair too, never the skin (SkinnedFigure.ClothesOcclusion)
        public float clothesOcclusionStrength = DefaultClothesOcclusionStrength;   // with it on, the share of that darkening they keep, 0.1 to 1 (SkinnedFigure.ClothesOcclusionStrength)
        public const float DefaultClothesOcclusionStrength = 0.4f;
        public bool fewerPoses = false;   // with many pawns on screen, each one's limbs move every second or third frame (SkinnedFigure.FewerPoses)
        public bool gpuSkinning = true;   // pawns' bodies, clothes and hair posed by the graphics card (SkinnedFigure.GpuSkinning); off: on the CPU
        public float hairDetail = DefaultHairDetail;   // the share of its triangles each built hairstyle and its shell keep as they are read (SettingsMod.HairDetail)
        // The freecam (Freecam.cs): whether a click off a menu leaves it open, how fast the mouse turns it (degrees per
        // pixel) and it flies (cells per real second), and its field of view.
        public bool freecamOn = false;   // the freecam is wanted: kept through 2D, other maps and restarts (Freecam.Wanted)
        public bool freecam3D = false;   // the 3D view was on with it, so a loaded game starts in 3D (Freecam.Resume)
        public bool freecamClickOffLooks = false;   // in pointer mode, a left click on nothing goes back to looking
        public bool freecamPointerDot = true;   // a dot where the freed cursor will appear, while looking with freecamPointerBack
        public bool freecamPointerBack = false;   // the freed pointer (pointer mode, a menu, a dialog) goes back where it was when looking began
        public bool freecamSnapArchitect = false;   // the pointer jumps to the middle of the architect menu as it opens
        public bool freecamMenuFreesPointer = true;   // a menu of orders opened while looking frees the pointer, rather than being pinned in the view
        public int freecamButton = 2;   // the mouse button that toggles pointer mode (2 middle, 3 and 4 the side buttons, -1 none)
        public float freecamSensitivity = DefaultFreecamSensitivity;
        public bool freecamInvertY = false;
        public bool freecamScrollFlies = true;   // the mouse wheel flies the freecam up and down, not the game's zoom
        public float freecamSpeed = DefaultFreecamSpeed;
        public float freecamScrollSpeed = DefaultFreecamScrollSpeed;   // the wheel's climb a notch, a share of the original 0.05 s of flying
        public float freecamFov = DefaultFreecamFov;
        public bool freecamCutaway = false;   // IsoRim's own wall hiding while flying, not its complete walls (Freecam.SyncWalls)
        public const float DefaultFreecamSensitivity = 0.05f, DefaultFreecamSpeed = 12f, DefaultFreecamFov = 70f, DefaultFreecamScrollSpeed = 0.5f;
        // Each weapon type's size changed from the export's (walk.TYPE_SCALE), against the same weapon on the ground
        // (WalkFigures.ScaleOf, over each weapon's WalkFigures.GroundRatio); a type not in it keeps the export's. Saved
        // under a new name, as the old one was a share of the sprite.
        public Dictionary<string, float> weaponScales = new Dictionary<string, float>();
        // A weapon drawn as its sprite's slab rather than IsoRim's model (another mod's weapon with no model, or models off),
        // a share of the size the type's slider gives it (WalkFigures.ScaleOf). The slab is as long as the sprite, but
        // IsoRim lays its models on the ground at about 0.7 of their sprite (WalkFigures.GroundRatio), so at the same type
        // size a slab came out about 1.4 times as long as a model: the game's guns' models match their slabs at 0.59 ..
        // 0.74 (median 0.71; 0.66 on Combat Extended's art), by the autotest's slabsizes.
        public float slabScale = DefaultSlabScale;
        public const float DefaultSlabScale = 0.7f;
        // Held and holstered weapons follow the pawn height option (WalkFigures.WeaponSizeOf): weaponScales are sizes at
        // the standard height. Older saves (before 2026-11, this false) held them as sizes at the saved height, and
        // WalkFigures.SizesToHeight divides them by that height's share once, so each weapon keeps the size it had.
        public bool weaponSizesAtStandard = true;

        // The edge pass's numbers set to the defaults or the faster ones (the options' two buttons).
        public void SetFasterOutlines(bool faster)
        {
            outlineReach = faster ? FasterOutlineReach : DefaultOutlineReach;
            outlineProbeDirs = faster ? FasterOutlineProbeDirs : DefaultOutlineProbeDirs;
            outlineProbeRings = faster ? FasterOutlineProbeRings : DefaultOutlineProbeRings;
            outlineRings = faster ? FasterOutlineRings : DefaultOutlineRings;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref pawnHeight, "pawnHeight", -1f);
            if (Scribe.mode == LoadSaveMode.LoadingVars && pawnHeight <= 0f)
            {
                // Saved before the option was a height: it was a share of a standard pawn 1.5 cells tall, so keep the
                // height it gave (saved only if moved off 100%), within today's range.
                float share = -1f;
                Scribe_Values.Look(ref share, "pawnSize", -1f);
                if (share > 0f)
                {
                    pawnHeight = Mathf.Clamp(share * OldStandardHeight, HeightLeast, HeightMost);
                }
            }
            Scribe_Values.Look(ref headShrink, "headShrink", DefaultShrink);
            Scribe_Values.Look(ref genderSizes, "genderSizes", true);
            Scribe_Values.Look(ref heightSpread, "heightSpread", DefaultHeightSpread);
            Scribe_Values.Look(ref skeletons, "skeletons", true);
            Scribe_Values.Look(ref agingLooks, "agingLooks", true);
            Scribe_Values.Look(ref greyingHair, "greyingHair", true);
            Scribe_Values.Look(ref otherRaces, "otherRaces", true);
            Scribe_Values.Look(ref raceParts, "raceParts", true);
            Scribe_Values.Look(ref garmentDetail, "garmentDetail", 1);
            bool oldFullDetail = false;   // (before garmentDetail: a checkbox, kept as made)
            Scribe_Values.Look(ref oldFullDetail, "fullDetailGarments", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars && oldFullDetail)
            {
                garmentDetail = 2;
            }
            Scribe_Values.Look(ref raceMouthInk, "raceMouthInk", true);
            Scribe_Values.Look(ref mouthSizeFit, "mouthSizeFit", true);
            Scribe_Values.Look(ref sharpFaceArt, "sharpFaceArt", true);
            Scribe_Values.Look(ref variedBodies, "variedBodies", true);
            Scribe_Values.Look(ref bodySpread, "bodySpread", DefaultBodySpread);
            Scribe_Values.Look(ref bodyShapeStrength, "bodyShapeStrength", 1f);
            Scribe_Values.Look(ref ragdolls, "ragdolls", true);
            Scribe_Values.Look(ref flingPower, "flingPowerRescaled", 1f);   // not "flingPower": those were of a distance a sixth of this one
            Scribe_Values.Look(ref flingBodies, "flingBodies", true);
            Scribe_Values.Look(ref flingWeapons, "flingWeapons", true);
            Scribe_Values.Look(ref wornGear, "wornGear", true);
            Scribe_Values.Look(ref holsters, "holsters", true);
            Scribe_Values.Look(ref muzzleFlash, "muzzleFlash", true);
            Scribe_Values.Look(ref hitFlash, "hitFlash", true);
            Scribe_Values.Look(ref shoes, "shoes", true);
            Scribe_Values.Look(ref armourHidesTail, "armourHidesTail", true);
            Scribe_Values.Look(ref earHiding, "earHiding", EarHiding.Helmets);
            Scribe_Values.Look(ref beardMode, "beardMode", BeardMode.Flat);
            Scribe_Values.Look(ref powerArmourBulk, "powerArmourBulk", false);
            Scribe_Values.Look(ref armourBulk, "armourBulk", DefaultArmourBulk);
            Scribe_Values.Look(ref marineBulk, "marineBulk", true);
            Scribe_Values.Look(ref armourDrawScale, "armourDrawScale", true);
            Scribe_Values.Look(ref retexturedWeaponModels, "retexturedWeaponModels", true);
            Scribe_Values.Look(ref autoGrips, "autoGrips", true);
            Scribe_Values.Look(ref spriteClothes, "spriteClothes", true);
            Scribe_Values.Look(ref groundGarments, "groundGarments", false);
            Scribe_Values.Look(ref bedBlankets, "bedBlankets", false);
            Scribe_Values.Look(ref sleepWear, "bedClothes", SleepWear.NoEquipment);
            Scribe_Values.Look(ref helmetStow, "helmetStow", HelmetStow.Worn);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // The old one selector (no longer written), "sleepWear": Blankets, the old default, takes the new one (the blanket and NoEquipment); any
                // other choice lay on top of every bed, so without the blanket.
                string old = null;
                Scribe_Values.Look(ref old, "sleepWear", null);
                if (old != null)
                {
                    SleepWear was;
                    if (old == "Blankets")
                    {
                        bedBlankets = true;
                        sleepWear = SleepWear.NoEquipment;
                    }
                    else if (System.Enum.TryParse(old, out was))
                    {
                        bedBlankets = false;
                        sleepWear = was;
                    }
                }
            }
            Scribe_Values.Look(ref vatClothes, "vatClothes", false);
            Scribe_Values.Look(ref injuryAnims, "injuryAnims", true);
            Scribe_Values.Look(ref visibleWounds, "visibleWounds", true);
            Scribe_Values.Look(ref prosthetics, "prosthetics", true);
            Scribe_Values.Look(ref archotechProsthetics, "archotechProsthetics", true);
            Scribe_Values.Look(ref prostheticEyes, "prostheticEyes", true);
            Scribe_Values.Look(ref gazeStrength, "gazeStrength", DefaultGazeStrength);
            Scribe_Values.Look(ref talkingMouths, "talkingMouths", true);
            Scribe_Values.Look(ref outlines, "outlines", true);
            Scribe_Values.Look(ref outlineMode, "outlineMode", OutlineMode.Silhouette);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // The old checkbox, no longer saved: off was the lines over the body.
                bool silhouette = true;
                Scribe_Values.Look(ref silhouette, "silhouetteOutlines", true);
                if (!silhouette && outlineMode == OutlineMode.Silhouette)
                {
                    outlineMode = OutlineMode.Lines;
                }
            }
            Scribe_Values.Look(ref pawnOutlineWidth, "pawnOutlineWidth", DefaultPawnOutlineWidth);
            Scribe_Values.Look(ref weaponOutlineWidth, "weaponOutlineWidth", DefaultWeaponOutlineWidth);
            Scribe_Values.Look(ref screenSelfOutlines, "screenSelfOutlines", false);
            Scribe_Values.Look(ref outlineReach, "outlineReach", DefaultOutlineReach);
            Scribe_Values.Look(ref outlineProbeDirs, "outlineProbeDirs", DefaultOutlineProbeDirs);
            Scribe_Values.Look(ref outlineProbeRings, "outlineProbeRings", DefaultOutlineProbeRings);
            Scribe_Values.Look(ref outlineRings, "outlineRings", DefaultOutlineRings);
            Scribe_Values.Look(ref selfOutlineStep, "selfOutlineStep", DefaultSelfOutlineStep);
            Scribe_Values.Look(ref clothesOcclusion, "clothesOcclusion", true);
            Scribe_Values.Look(ref clothesOcclusionStrength, "clothesOcclusionStrength", DefaultClothesOcclusionStrength);
            Scribe_Values.Look(ref fewerPoses, "fewerPoses", false);
            // Saved under a new key since the option moved to Advanced, so anyone who had turned it off has it back on once.
            Scribe_Values.Look(ref gpuSkinning, "gpuSkinningOn", true);
            Scribe_Values.Look(ref firstPersonEye, "firstPersonEye", true);
            Scribe_Values.Look(ref hairDetail, "hairDetail", DefaultHairDetail);
            Scribe_Values.Look(ref freecamOn, "freecamOn", false);
            Scribe_Values.Look(ref freecam3D, "freecam3D", false);
            Scribe_Values.Look(ref freecamClickOffLooks, "freecamClickOffLooks", false);
            Scribe_Values.Look(ref freecamPointerBack, "freecamPointerBack", false);
            Scribe_Values.Look(ref freecamPointerDot, "freecamPointerDot", true);
            Scribe_Values.Look(ref freecamMenuFreesPointer, "freecamMenuFreesPointer", true);
            Scribe_Values.Look(ref freecamSnapArchitect, "freecamSnapArchitect", false);
            Scribe_Values.Look(ref freecamButton, "freecamButton", 2);
            Scribe_Values.Look(ref freecamSensitivity, "freecamSensitivity", DefaultFreecamSensitivity);
            Scribe_Values.Look(ref freecamInvertY, "freecamInvertY", false);
            Scribe_Values.Look(ref freecamScrollFlies, "freecamScrollFlies", true);
            Scribe_Values.Look(ref freecamSpeed, "freecamSpeed", DefaultFreecamSpeed);
            Scribe_Values.Look(ref freecamScrollSpeed, "freecamScrollSpeed", DefaultFreecamScrollSpeed);
            Scribe_Values.Look(ref freecamFov, "freecamFov", DefaultFreecamFov);
            Scribe_Values.Look(ref freecamCutaway, "freecamCutaway", false);
            Scribe_Values.Look(ref slabScale, "slabScale", DefaultSlabScale);
            Scribe_Values.Look(ref weaponSizesAtStandard, "weaponSizesAtStandard", false, true);   // (absent: an older save)
            Scribe_Collections.Look(ref weaponScales, "weaponGroundScales", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref gripToolButton, "gripToolButton", true);
            Scribe_Values.Look(ref gripTool, "gripTool", false);
            Scribe_Collections.Look(ref gripOverrides, "gripOverrides", LookMode.Value, LookMode.Value);
            if (gripOverrides == null)
            {
                gripOverrides = new Dictionary<string, Vector3>();
            }
            Scribe_Collections.Look(ref offHandGrips, "offHandGrips", LookMode.Value, LookMode.Value);
            if (offHandGrips == null)
            {
                offHandGrips = new Dictionary<string, Vector2>();
            }
            Scribe_Collections.Look(ref faceTunes, "faceTunes", LookMode.Value, LookMode.Value);
            if (faceTunes == null)
            {
                faceTunes = new Dictionary<string, Vector3>();
            }
            Scribe_Collections.Look(ref mouthTunes, "mouthTunes", LookMode.Value, LookMode.Value);
            if (mouthTunes == null)
            {
                mouthTunes = new Dictionary<string, Vector3>();
            }
            Scribe_Collections.Look(ref faceFits, "faceFits", LookMode.Value, LookMode.Value);
            if (faceFits == null)
            {
                faceFits = new Dictionary<string, Vector3>();
            }
            Scribe_Collections.Look(ref faceFitRows, "faceFitRows", LookMode.Value, LookMode.Value);
            if (faceFitRows == null)
            {
                faceFitRows = new Dictionary<string, Vector2>();
            }
            Scribe_Collections.Look(ref faceFitMouths, "faceFitMouths", LookMode.Value, LookMode.Value);
            if (faceFitMouths == null)
            {
                faceFitMouths = new Dictionary<string, float>();
            }
            Scribe_Collections.Look(ref clothingChoices, "clothingChoices", LookMode.Value, LookMode.Value);
            if (clothingChoices == null)
            {
                clothingChoices = new Dictionary<string, string>();
            }
            Scribe_Collections.Look(ref plainClothes, "plainClothes", LookMode.Value);
            if (plainClothes == null)
            {
                plainClothes = new List<string>();
            }
            Scribe_Collections.Look(ref paintedPackClothes, "paintedPackClothes", LookMode.Value);
            if (paintedPackClothes == null)
            {
                paintedPackClothes = new List<string>();
            }
            Scribe_Collections.Look(ref contentChoices, "contentChoices", LookMode.Value, LookMode.Value);
            if (contentChoices == null)
            {
                contentChoices = new Dictionary<string, string>();
            }
            Scribe_Values.Look(ref contentNoticeShown, "contentNoticeShown", false);
            Scribe_Values.Look(ref contentButton, "contentButton", true);
            Scribe_Collections.Look(ref packCosts, "packCosts", LookMode.Value, LookMode.Value);
            if (packCosts == null)
            {
                packCosts = new Dictionary<string, string>();
            }
            if (weaponScales == null)
            {
                weaponScales = new Dictionary<string, float>();
            }
        }
    }

    // The mod options window. The settings are read live each draw, so a change shows at once on every pawn, but for the
    // hair's detail (HairDetail).
    public class SettingsMod : Mod
    {
        // The slider moves in these steps, as every amount it passes through makes and keeps another shape per body type.
        private const float ShrinkStep = 0.05f;
        private const float BodyShapeStep = 0.05f;   // the body shape strength slider's step, as each amount makes its own shapes
        private const float HeightStep = 0.05f;   // cells, the pawn height slider's steps
        private const float WeaponStep = 0.05f;   // the weapon size sliders' steps
        private const float WeaponLeast = 0.25f, WeaponMost = 2f;   // against the ground
        private const float SlabLeast = 0.3f, SlabMost = 1.2f;   // a sprite slab's share of its type's size
        private const float HairDetailStep = 0.05f, HairDetailLeast = 0.05f;   // up to all of it
        private const float FlingPowerStep = 0.25f, FlingPowerLeast = 0.25f, FlingPowerMost = 5f;   // of the standard distance
        private const float SubOptionIndent = 24f;   // how far in, px, the options a checkbox shows are set
        private const float SliderLabelShare = 0.55f;   // of a column, the label beside a slider or button

        // What each weapon type (walk.WEAPON_TYPES) is called in the options: M3AT_WeaponType_<type> in Languages/*/Keyed,
        // else the type's own name, for one the export adds before a translation names it.
        private static string TypeName(string type)
        {
            string key = "M3AT_WeaponType_" + type;
            return key.CanTranslate() ? key.Translate().Resolve() : type;
        }

        // Of the full head's size: how much the Shrinkhead key at full makes it, measured from skin.txt (the key scales the
        // head evenly, and its size goes down in a straight line with the key). Measure it again if the key is remade.
        private const float ShrunkSize = 0.543f;

        public static Settings Settings;

        // The hair's detail as the figure takes it (SkinnedFigure.TakeHairDetail). Every style in use is read and cut down
        // again when it changes, so it follows the slider only as the window closes, not at every step of a drag.
        public static float HairDetail = Settings.DefaultHairDetail;

        // Clothes on the ground as last baked: they are in the map's mesh, so they change only as the window closes too.
        private static bool groundGarmentsBaked;
        // Weapons on the ground as last baked, for the same reason (IsoRim bakes their models into the map's mesh too).
        private static bool weaponModelsBaked;

        // The options scroll if their tallest column is longer than the window: where to, and how tall it came out last draw.
        private static Vector2 scroll;
        private static float shownHeight;

        // The head's size at a shrink, of the full chibi head's.
        private const float GazeMost = 4f, GazeStep = 0.25f;   // the eye movement slider's top and steps

        internal static float HeadSize(float shrink)
        {
            return 1f - (1f - ShrunkSize) * shrink;
        }

        // The Shrinkhead amount that makes the head this share of the full chibi head's (HeadSize's inverse; above 1 if
        // the key can't shrink it that far).
        internal static float ShrinkFor(float headSize)
        {
            return (1f - headSize) / (1f - ShrunkSize);
        }

        public SettingsMod(ModContentPack content) : base(content)
        {
            LoadTrail.Start(new HarmonyLib.Harmony("MaruMirai.M3AT.trail"));   // first, so the whole load is in it
            Settings = GetSettings<Settings>();
            HairDetail = Settings.hairDetail;
            groundGarmentsBaked = Settings.groundGarments;
            RestoredModels.Register();   // a model rule, before IsoRim's startup builds its model table
            weaponModelsBaked = Settings.retexturedWeaponModels;
        }

        // The options' sections, in the order they are read: down the first column, then the second. Two columns rather
        // than three, so the long labels and sliders have room. The freecam always heads the second column and the
        // folded Outlines, Weapon sizes, Advanced and Mod compatibility sections always end it (Pinned); the sections between flow from the bottom of the first column to under
        // the freecam, and where they break is worked out from how tall each section came out (Columns), so the
        // columns come out even whatever the window's width or the wrapping of the text.
        private static readonly System.Action<Listing_Standard>[] Sections =
        {
            PawnsSection, ClothesSection, InjuriesSection, DrawingSection, CombatSection,
            PerformanceSection, ProblemsSection,
        };
        private static readonly System.Action<Listing_Standard> PinnedTop = FreecamSection;
        // The folded sections, last so that opening one only lengthens the column downward.
        private static readonly System.Action<Listing_Standard>[] PinnedBottom = { OutlinesSection, WeaponSizesSection, AdvancedSection, CompatibilitySection };
        private const int ColumnCount = 2;
        private const float SectionGap = 12f;   // px between two sections in a column, as the listing's own Gap

        // Each section's height, px, as last drawn (the flowing ones, then the pinned top and bottom), and the flowing
        // section the second column carries on from.
        private static readonly float[] sectionHeights = new float[Sections.Length + 1 + PinnedBottom.Length];
        private static int columnBreak = -1;
        // The columns are worked out again only as the window opens (or its width changes), not while it is open, so a
        // section doesn't jump to another column under the pointer when a checkbox shows or hides the options under it.
        private static bool relayout = true;
        private static float laidOutWidth = -1f;
        private static bool measured;   // every section has been drawn once, so sectionHeights holds them all

        // The break (the sections kept in order) that makes the taller column shortest. Every break is tried, the
        // second column counted with its pinned sections.
        private static int Columns()
        {
            int n = Sections.Length;
            int best = n;
            float bestTallest = float.MaxValue;
            float pinned = sectionHeights[n];
            for (int i = 0; i < PinnedBottom.Length; i++)
            {
                pinned += SectionGap + sectionHeights[n + 1 + i];
            }
            for (int a = 1; a <= n; a++)
            {
                float right = pinned + (a < n ? ColumnHeight(a, n) + 2f * SectionGap : 0f);
                float tallest = Mathf.Max(ColumnHeight(0, a), right);
                if (tallest < bestTallest - 0.5f)
                {
                    bestTallest = tallest;
                    best = a;
                }
            }
            return best;
        }

        private static float ColumnHeight(int from, int to)
        {
            float height = 0f;
            for (int i = from; i < to; i++)
            {
                height += sectionHeights[i] + (i > from ? SectionGap : 0f);
            }
            return height;
        }

        // Every option back to what a new install has (a fresh Settings' field values), but whether the freecam is on,
        // which is where the player left it rather than an option. The hair's detail and the items baked on the ground
        // follow as the window closes, as when they are changed by hand.
        // The garmentDetail option ("Auto Optimization"), here and in the content switcher (ContentView). Listed
        // High, Default, Low, None, though stored 0 High, 1 Default, 2 None (kept as made, to the ceiling), 3 Low, as saves have it so;
        // each label gives the share of M3AT's like garment it holds a mod's to, and the polys that comes to across ours.
        private static readonly int[] DetailOrder = { 0, 1, 3, 2 };

        internal static string GarmentDetailLabel(int level)
        {
            level = Mathf.Clamp(level, 0, 3);
            if (level == 2)
            {
                return "M3AT_GarmentDetailUpTo".Translate(("M3AT_GarmentDetail_2").Translate(), SkinnedFigure.Ceiling.ToString("N0"));
            }
            float share = SkinnedFigure.ShareAt(level);
            int least = Mathf.RoundToInt(SkinnedFigure.OursLeast * share);
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            int most = Mathf.RoundToInt((f != null ? f.HeaviestOwnGarment() : 1494) * share);
            return "M3AT_GarmentDetailRange".Translate(("M3AT_GarmentDetail_" + level).Translate(), share.ToString("0.0#"), least.ToString("N0"), most.ToString("N0"));
        }

        // besideLabel (the content window): the button sized to its text and set just after the label, so the two read as
        // one; in the options it lines up with the other options' buttons.
        internal static void GarmentDetailOption(Listing_Standard list, bool besideLabel = false)
        {
            string label = "M3AT_GarmentDetail".Translate();
            string value = GarmentDetailLabel(Settings.garmentDetail);
            string tip = (string)"M3AT_GarmentDetailTip".Translate(SkinnedFigure.DefaultShare.ToString("0.0#"), SkinnedFigure.FastShare.ToString("0.0#"), SkinnedFigure.LightShare.ToString("0.#"),
                Mathf.RoundToInt(SkinnedFigure.OursLeast * SkinnedFigure.DefaultShare), SkinnedFigure.TextureBudget, SkinnedFigure.Ceiling, SkinnedFigure.Optimized.Count) + BodyNotOptimized();
            bool clicked;
            if (besideLabel)
            {
                Rect row = list.GetRect(28f);
                float labelWidth = Text.CalcSize(label).x;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x, row.y, labelWidth + 4f, row.height), label);
                Text.Anchor = TextAnchor.UpperLeft;
                Rect button = new Rect(row.x + labelWidth + 12f, row.y + 1f, Mathf.Min(Text.CalcSize(value).x + 28f, row.width - labelWidth - 12f), 26f);
                clicked = Widgets.ButtonText(button, value);
                Widgets.DrawHighlightIfMouseover(new Rect(row.x, row.y, button.xMax - row.x, row.height));
                TooltipHandler.TipRegion(new Rect(row.x, row.y, button.xMax - row.x, row.height), tip);
            }
            else
            {
                clicked = list.ButtonTextLabeledPct(label, value, SliderLabelShare, TextAnchor.MiddleLeft, null, tip);
            }
            if (clicked)
            {
                List<FloatMenuOption> levels = new List<FloatMenuOption>();
                foreach (int i in DetailOrder)
                {
                    int picked = i;
                    levels.Add(new FloatMenuOption(GarmentDetailLabel(i), () =>
                    {
                        // Saved at once, as the content switcher has no close to save it on.
                        Settings.garmentDetail = picked;
                        Settings.Write();
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(levels));
            }
        }

        // Added to the detail option's tooltip while a pack has changed the body: the option can't lighten that.
        private static string BodyNotOptimized()
        {
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            if (f == null || !f.BodyChanged)
            {
                return "";
            }
            f.OwnGarmentTriangles(out int made, out int now);
            return "\n\n" + "M3AT_GarmentDetailBody".Translate(SkinnedFigure.BodyChangerName(), f.BodyFileTriangles.ToString("N0"), f.TrianglesOf(0).ToString("N0"),
                made.ToString("N0"), now.ToString("N0"));
        }

        private static void RestoreDefaults()
        {
            Settings fresh = new Settings();
            System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (System.Reflection.FieldInfo field in typeof(Settings).GetFields(fields))
            {
                if (field.IsInitOnly || field.Name == nameof(Settings.freecamOn) || field.Name == nameof(Settings.freecam3D)
                    || field.Name == nameof(Settings.gripOverrides) || field.Name == nameof(Settings.offHandGrips)
                    || field.Name == nameof(Settings.clothingChoices) || field.Name == nameof(Settings.plainClothes)
                    || field.Name == nameof(Settings.paintedPackClothes) || field.Name == nameof(Settings.contentNoticeShown) || field.Name == nameof(Settings.packCosts)
                    || field.Name == nameof(Settings.faceTunes) || field.Name == nameof(Settings.mouthTunes)
                    || field.Name == nameof(Settings.faceFits) || field.Name == nameof(Settings.faceFitRows) || field.Name == nameof(Settings.faceFitMouths))   // the player's own work, not an option: cleared by its own button
                {
                    continue;
                }
                field.SetValue(Settings, field.GetValue(fresh));
            }
            if (WalkFigures.SkinForAddOns != null)
            {
                ContentSources.Apply();   // the content packs' choices back to their defaults
            }
            Messages.Message("M3AT_DefaultsRestored".Translate(), RimWorld.MessageTypeDefOf.NeutralEvent, false);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            relayout = true;   // the window was closed: lay the columns out afresh next time it opens
            HairDetail = Settings.hairDetail;
            if (groundGarmentsBaked != Settings.groundGarments)
            {
                groundGarmentsBaked = Settings.groundGarments;
                WalkFigures.RebakeGroundClothes();
            }
            WalkFigures.OtherRacesChanged();   // IsoRim asks every race again if the option changed
            if (weaponModelsBaked != Settings.retexturedWeaponModels)
            {
                weaponModelsBaked = Settings.retexturedWeaponModels;
                WalkFigures.RebakeGroundWeapons();
            }
        }

        public override string SettingsCategory()
        {
            return "M3AT: Maru's 3D Animated Toons";
        }

        // The options a checkbox shows go between these, set in from the left so they read as under it. The listing's Indent
        // only moves the left edge, so the width comes in with it, or they would run past the right.
        private static void BeginSubOptions(Listing_Standard list)
        {
            list.Indent(SubOptionIndent);
            list.ColumnWidth -= SubOptionIndent;
        }

        private static void EndSubOptions(Listing_Standard list)
        {
            list.Outdent(SubOptionIndent);
            list.ColumnWidth += SubOptionIndent;
        }

        // A section's title, as Combat Extended's options have them: in the medium font, a line under it.
        private static void Header(Listing_Standard list, string title)
        {
            Text.Font = GameFont.Medium;
            list.Label(title);
            Text.Font = GameFont.Small;
            list.GapLine(6f);
        }

        // A slider on one line: its label and value on the left, the slider on the right, the tooltip over both.
        // The value is drawn apart from the name, right-aligned, and the name cut short with "..." if both don't fit, as a
        // long name ("M3AT_WeaponType_rifle".Translate()) wrapped the value onto a second line the row's height left out of sight.
        private static float Slider(Listing_Standard list, string name, string shownValue, float value, float least, float most, string tooltip)
        {
            Rect rect = list.GetRect(24f);
            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }
            TooltipHandler.TipRegion(rect, tooltip);
            Rect label = rect.LeftPart(SliderLabelShare);
            float valueWidth = Text.CalcSize(shownValue).x;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(label.xMax - valueWidth - 6f, label.y, valueWidth, label.height), shownValue);
            Text.Anchor = TextAnchor.MiddleLeft;
            Rect nameRect = new Rect(label.x, label.y, Mathf.Max(0f, label.width - valueWidth - 12f), label.height);
            Widgets.Label(nameRect, name.Truncate(nameRect.width));
            Text.Anchor = TextAnchor.UpperLeft;
            Rect bar = rect.RightPart(1f - SliderLabelShare);
            bar.y += 6f;   // the slider's bar is drawn at the top of its rect, so it is set down to the label's middle
            float result = Widgets.HorizontalSlider(bar, value, least, most, middleAlignment: true);
            list.Gap(list.verticalSpacing);
            return result;
        }

        // A key binding's keys as the keyboard options show them, or a word if it has none.
        private static string KeyName(KeyBindingDef def)
        {
            return def == null ? "?" : def.MainKey == KeyCode.None ? "M3AT_Unbound".Translate() : def.MainKeyLabel;
        }

        private static string PointerPlaceName(bool back)
        {
            return back ? "M3AT_FreecamPointerLast".Translate() : "M3AT_FreecamPointerCross".Translate();
        }

        private static string MenuSelectName(bool pointer)
        {
            return pointer ? "M3AT_FreecamMenuPointer".Translate() : "M3AT_FreecamMenuCrosshair".Translate();
        }

        private static string ButtonName(int button)
        {
            return button == 2 ? "M3AT_MiddleButton".Translate() : button == 3 ? "M3AT_MouseButton4".Translate() : button == 4 ? "M3AT_MouseButton5".Translate() : "M3AT_NoButton".Translate();
        }

        private static string EarName(EarHiding mode)
        {
            return mode == EarHiding.AnyHeadgear ? "M3AT_EarsAnyHeadgear".Translate() : mode == EarHiding.Helmets ? "M3AT_EarsHelmets".Translate() : "M3AT_EarsNever".Translate();
        }

        private static string SleepName(SleepWear mode)
        {
            return mode == SleepWear.Dressed ? "M3AT_SleepDressed".Translate() : mode == SleepWear.NoEquipment ? "M3AT_SleepNoEquipment".Translate()
                : mode == SleepWear.Pajamas ? "M3AT_SleepPajamas".Translate() : "M3AT_SleepNothing".Translate();
        }

        private static string HelmetName(HelmetStow mode)
        {
            return mode == HelmetStow.Worn ? "M3AT_HelmetsWorn".Translate() : mode == HelmetStow.OnBack ? "M3AT_HelmetsOnBack".Translate()
                : mode == HelmetStow.Hidden ? "M3AT_HelmetsHidden".Translate() : "M3AT_HelmetsAlwaysHidden".Translate();
        }

        private static string OutlineModeName(OutlineMode mode)
        {
            return mode == OutlineMode.Silhouette ? "M3AT_OutlineSilhouette".Translate() : mode == OutlineMode.Lines
                ? "M3AT_OutlineLines".Translate() : "M3AT_OutlineScreen".Translate();
        }

        // An outline width, in cells, as hundredths of a cell.
        private static string OutlineWidthName(float cells)
        {
            return "M3AT_OutlineWidthValue".Translate((cells * 100f).ToString("0.#"));
        }

        private const float OutlineWidthLeast = 0.005f, OutlineWidthMost = 0.08f, OutlineWidthStep = 0.0025f;

        private static string BeardName(BeardMode mode)
        {
            return mode == BeardMode.Flat ? "2D" : mode == BeardMode.ThreeD ? "3D" : "M3AT_BeardsOff".Translate();
        }

        // How pawns are built: size, head, build, hair, beard and eyes.
        private static void PawnsSection(Listing_Standard list)
        {
            Header(list, "M3AT_Pawns".Translate());
            // An absolute height, shown with its share of the export's standard; back at the standard it is the default
            // again, so it follows the standard if that changes.
            float standard = WalkFigures.StandardHeight;
            float height = Settings.pawnHeight > 0f ? Settings.pawnHeight : standard;
            height = Mathf.Round(Slider(list, "M3AT_PawnSize".Translate(), "M3AT_PawnHeightValue".Translate(height.ToString("0.00"), (height / standard).ToStringPercent()),
                height, Settings.HeightLeast, Settings.HeightMost, "M3AT_PawnSizeTip".Translate()) / HeightStep) * HeightStep;
            Settings.pawnHeight = Mathf.Abs(height - standard) < HeightStep * 0.5f ? -1f : height;
            // Shown as how much smaller the head is than the full chibi head's, measured, not the key's amount.
            float shrink = Slider(list, "M3AT_HeadShrink".Translate(), (1f - HeadSize(Settings.headShrink)).ToStringPercent(), Settings.headShrink, 0f, 1f,
                "M3AT_HeadShrinkTip".Translate());
            Settings.headShrink = Mathf.Round(shrink / ShrinkStep) * ShrinkStep;
            float spread = Slider(list, "M3AT_HeightVariety".Translate(), Settings.heightSpread.ToStringPercent("0.#"), Settings.heightSpread, 0f, 0.1f,
                "M3AT_HeightVarietyTip".Translate(Settings.DefaultHeightSpread.ToStringPercent("0.#")));
            Settings.heightSpread = Mathf.Round(spread * 200f) / 200f;
            list.CheckboxLabeled("M3AT_GenderSizes".Translate(), ref Settings.genderSizes,
                "M3AT_GenderSizesTip".Translate());
            list.CheckboxLabeled("M3AT_AgingLooks".Translate(), ref Settings.agingLooks,
                "M3AT_AgingLooksTip".Translate());
            list.CheckboxLabeled("M3AT_GreyingHair".Translate(), ref Settings.greyingHair,
                "M3AT_GreyingHairTip".Translate());
            list.CheckboxLabeled("M3AT_Skeletons".Translate(), ref Settings.skeletons,
                "M3AT_SkeletonsTip".Translate());
            Settings.bodyShapeStrength = Mathf.Round(Slider(list, "M3AT_BodyShapeStrength".Translate(), Settings.bodyShapeStrength.ToStringPercent(),
                Settings.bodyShapeStrength, 0f, WalkFigures.BodyShapeMost, "M3AT_BodyShapeStrengthTip".Translate()) / BodyShapeStep) * BodyShapeStep;
            list.CheckboxLabeled("M3AT_VariedBodies".Translate(), ref Settings.variedBodies,
                "M3AT_VariedBodiesTip".Translate());
            if (Settings.variedBodies)
            {
                BeginSubOptions(list);
                Settings.bodySpread = Mathf.Round(Slider(list, "M3AT_BodyVariety".Translate(), Settings.bodySpread.ToStringPercent(), Settings.bodySpread, 0.05f, 0.6f,
                    "M3AT_BodyVarietyTip".Translate(Settings.DefaultBodySpread.ToStringPercent())) * 20f) / 20f;
                EndSubOptions(list);
            }
            if (list.ButtonTextLabeledPct("M3AT_Beards".Translate(), BeardName(Settings.beardMode), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "M3AT_BeardsTip".Translate()))
            {
                List<FloatMenuOption> modes = new List<FloatMenuOption>();
                foreach (BeardMode mode in new[] { BeardMode.Flat, BeardMode.ThreeD, BeardMode.Off })
                {
                    BeardMode picked = mode;
                    modes.Add(new FloatMenuOption(BeardName(mode), () => Settings.beardMode = picked));
                }
                Find.WindowStack.Add(new FloatMenu(modes));
            }
            // (The powerArmourBulk option, the game's own power armour bulking its wearer up, is taken out of the window for
            // now, ArmourSize.PowerArmourBulk: its soles lift the wearer instead, SkinnedFigure.SoleOf. The armoured size,
            // which only GrimWorld's Space Marines take now, is under marineBulk in Mod compatibility.)
            Settings.gazeStrength = Mathf.Round(Slider(list, "M3AT_EyeMovement".Translate(), Settings.gazeStrength.ToStringPercent(), Settings.gazeStrength, 0f, GazeMost,
                "M3AT_EyeMovementTip".Translate(Settings.DefaultGazeStrength.ToStringPercent())) / GazeStep) * GazeStep;
            list.CheckboxLabeled("M3AT_TalkingMouths".Translate(), ref Settings.talkingMouths,
                "M3AT_TalkingMouthsTip".Translate());
        }

        // What pawns are drawn wearing, and what their clothes hide.
        private static void ClothesSection(Listing_Standard list)
        {
            Header(list, "M3AT_Clothes".Translate());
            list.CheckboxLabeled("M3AT_Shoes".Translate(), ref Settings.shoes,
                "M3AT_ShoesTip".Translate());
            list.CheckboxLabeled("M3AT_ArmourHidesTail".Translate(), ref Settings.armourHidesTail,
                "M3AT_ArmourHidesTailTip".Translate());
            if (list.ButtonTextLabeledPct("M3AT_EarHiding".Translate(), EarName(Settings.earHiding), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "M3AT_EarHidingTip".Translate()))
            {
                List<FloatMenuOption> modes = new List<FloatMenuOption>();
                foreach (EarHiding mode in new[] { EarHiding.AnyHeadgear, EarHiding.Helmets, EarHiding.Never })
                {
                    EarHiding picked = mode;
                    modes.Add(new FloatMenuOption(EarName(mode), () => Settings.earHiding = picked));
                }
                Find.WindowStack.Add(new FloatMenu(modes));
            }
            if (list.ButtonTextLabeledPct("M3AT_HelmetStow".Translate(), HelmetName(Settings.helmetStow), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "M3AT_HelmetStowTip".Translate()))
            {
                List<FloatMenuOption> modes = new List<FloatMenuOption>();
                foreach (HelmetStow mode in new[] { HelmetStow.Worn, HelmetStow.OnBack, HelmetStow.Hidden, HelmetStow.AlwaysHidden })
                {
                    HelmetStow picked = mode;
                    modes.Add(new FloatMenuOption(HelmetName(mode), () => Settings.helmetStow = picked));
                }
                Find.WindowStack.Add(new FloatMenu(modes));
            }
            list.CheckboxLabeled("M3AT_WornGear".Translate(), ref Settings.wornGear,
                "M3AT_WornGearTip".Translate());
            list.CheckboxLabeled("M3AT_SpriteClothes".Translate(), ref Settings.spriteClothes,
                "M3AT_SpriteClothesTip".Translate());
            list.CheckboxLabeled("M3AT_GroundGarments".Translate(), ref Settings.groundGarments,
                "M3AT_GroundGarmentsTip".Translate());
            list.CheckboxLabeled("M3AT_BedBlankets".Translate(), ref Settings.bedBlankets,
                "M3AT_BedBlanketsTip".Translate());
            if (list.ButtonTextLabeledPct("M3AT_SleepWear".Translate(), SleepName(Settings.sleepWear), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "M3AT_SleepWearTip".Translate()))
            {
                List<FloatMenuOption> modes = new List<FloatMenuOption>();
                foreach (SleepWear mode in new[] { SleepWear.Dressed, SleepWear.NoEquipment, SleepWear.Pajamas, SleepWear.Nothing })
                {
                    SleepWear picked = mode;
                    modes.Add(new FloatMenuOption(SleepName(mode), () => Settings.sleepWear = picked));
                }
                Find.WindowStack.Add(new FloatMenu(modes));
            }
            list.CheckboxLabeled("M3AT_VatClothes".Translate(), ref Settings.vatClothes,
                "M3AT_VatClothesTip".Translate());
        }

        // Wounds, limps and prosthetics.
        private static void InjuriesSection(Listing_Standard list)
        {
            Header(list, "M3AT_Injuries".Translate());
            list.CheckboxLabeled("M3AT_InjuryAnims".Translate(), ref Settings.injuryAnims,
                "M3AT_InjuryAnimsTip".Translate());
            list.CheckboxLabeled("M3AT_VisibleWounds".Translate(), ref Settings.visibleWounds,
                "M3AT_VisibleWoundsTip".Translate());
            list.CheckboxLabeled("M3AT_Prosthetics".Translate(), ref Settings.prosthetics,
                "M3AT_ProstheticsTip".Translate());
            if (Settings.prosthetics)
            {
                list.CheckboxLabeled("M3AT_ProstheticEyes".Translate(), ref Settings.prostheticEyes,
                    "M3AT_ProstheticEyesTip".Translate());
                list.CheckboxLabeled("M3AT_ArchotechProsthetics".Translate(), ref Settings.archotechProsthetics,
                    "M3AT_ArchotechProstheticsTip".Translate());
            }
        }

        // How the figure is drawn (its outline is in its own folded section, OutlinesSection).
        private static void DrawingSection(Listing_Standard list)
        {
            Header(list, "M3AT_Drawing".Translate());
            list.CheckboxLabeled("M3AT_FirstPersonEye".Translate(), ref Settings.firstPersonEye,
                "M3AT_FirstPersonEyeTip".Translate());
            list.CheckboxLabeled("M3AT_ClothesOcclusion".Translate(), ref Settings.clothesOcclusion,
                "M3AT_ClothesOcclusionTip".Translate());
            if (Settings.clothesOcclusion)
            {
                Settings.clothesOcclusionStrength = Mathf.Round(Slider(list, "M3AT_ClothesOcclusionStrength".Translate(),
                    Settings.clothesOcclusionStrength.ToStringPercent(), Settings.clothesOcclusionStrength, 0.1f, 1f,
                    "M3AT_ClothesOcclusionStrengthTip".Translate()) * 20f) / 20f;
            }
        }

        // Falls, flings and weapons.
        private static void CombatSection(Listing_Standard list)
        {
            Header(list, "M3AT_Combat".Translate());
            list.CheckboxLabeled("M3AT_Ragdolls".Translate(), ref Settings.ragdolls,
                "M3AT_RagdollsTip".Translate());
            if (Settings.ragdolls)
            {
                BeginSubOptions(list);
                Settings.flingPower = Mathf.Round(Slider(list, "M3AT_FlingPower".Translate(), Settings.flingPower.ToStringPercent(), Settings.flingPower, FlingPowerLeast, FlingPowerMost,
                    "M3AT_FlingPowerTip".Translate()) / FlingPowerStep) * FlingPowerStep;
                list.CheckboxLabeled("M3AT_FlingBodies".Translate(), ref Settings.flingBodies,
                    "M3AT_FlingBodiesTip".Translate());
                list.CheckboxLabeled("M3AT_FlingWeapons".Translate(), ref Settings.flingWeapons,
                    "M3AT_FlingWeaponsTip".Translate());
                EndSubOptions(list);
            }
            list.CheckboxLabeled("M3AT_Holsters".Translate(), ref Settings.holsters,
                "M3AT_HolstersTip".Translate());
            list.CheckboxLabeled("M3AT_MuzzleFlash".Translate(), ref Settings.muzzleFlash,
                "M3AT_MuzzleFlashTip".Translate());
            list.CheckboxLabeled("M3AT_HitFlash".Translate(), ref Settings.hitFlash,
                "M3AT_HitFlashTip".Translate());
            list.CheckboxLabeled("M3AT_AutoGrips".Translate(), ref Settings.autoGrips,
                "M3AT_AutoGripsTip".Translate());
            int adjusted = Settings.gripOverrides.Keys.Union(Settings.offHandGrips.Keys).Count();
            if (adjusted > 0 && list.ButtonText("M3AT_GripClearAll".Translate(adjusted)))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("M3AT_GripClearAllConfirm".Translate(adjusted), () =>
                {
                    Settings.gripOverrides.Clear();
                    Settings.offHandGrips.Clear();
                }, true));
            }
            int clothed = Settings.clothingChoices.Keys.Union(Settings.plainClothes).Union(Settings.paintedPackClothes).Count();
            if (clothed > 0 && list.ButtonText("M3AT_ClothesClearAll".Translate(clothed)))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("M3AT_ClothesClearAllConfirm".Translate(clothed), () =>
                {
                    Settings.clothingChoices.Clear();
                    Settings.plainClothes.Clear();
                    Settings.paintedPackClothes.Clear();
                    WalkFigures.ClothingChoicesChanged();
                }, true));
            }
            list.CheckboxLabeled("M3AT_RetexturedWeaponModels".Translate(), ref Settings.retexturedWeaponModels,
                "M3AT_RetexturedWeaponModelsTip".Translate());
        }

        // The figure's outline and its style, folded away until its header is clicked: the screen-space style's widths and
        // detail are many sliders few players will touch.
        private static void OutlinesSection(Listing_Standard list)
        {
            FoldHeader(list, "M3AT_OutlinesHeader".Translate(), "M3AT_OutlinesHeaderTip".Translate(), ref outlinesOpen);
            if (!outlinesOpen)
            {
                return;
            }
            list.CheckboxLabeled("M3AT_Outlines".Translate(), ref Settings.outlines,
                "M3AT_OutlinesTip".Translate());
            if (Settings.outlines)
            {
                BeginSubOptions(list);
                if (list.ButtonTextLabeledPct("M3AT_OutlineMode".Translate(), OutlineModeName(Settings.outlineMode), SliderLabelShare, TextAnchor.MiddleLeft, null,
                    "M3AT_OutlineModeTip".Translate()))
                {
                    List<FloatMenuOption> modes = new List<FloatMenuOption>();
                    foreach (OutlineMode mode in new[] { OutlineMode.Silhouette, OutlineMode.Lines, OutlineMode.ScreenSpace })
                    {
                        OutlineMode picked = mode;
                        modes.Add(new FloatMenuOption(OutlineModeName(mode), () => Settings.outlineMode = picked));
                    }
                    Find.WindowStack.Add(new FloatMenu(modes));
                }
                if (Settings.outlineMode == OutlineMode.ScreenSpace)
                {
                    Settings.pawnOutlineWidth = Mathf.Round(Slider(list, "M3AT_PawnOutlineWidth".Translate(), OutlineWidthName(Settings.pawnOutlineWidth),
                        Settings.pawnOutlineWidth, OutlineWidthLeast, OutlineWidthMost,
                        "M3AT_PawnOutlineWidthTip".Translate(OutlineWidthName(Settings.DefaultPawnOutlineWidth))) / OutlineWidthStep) * OutlineWidthStep;
                    Settings.weaponOutlineWidth = Mathf.Round(Slider(list, "M3AT_WeaponOutlineWidth".Translate(), OutlineWidthName(Settings.weaponOutlineWidth),
                        Settings.weaponOutlineWidth, OutlineWidthLeast, OutlineWidthMost,
                        "M3AT_WeaponOutlineWidthTip".Translate(OutlineWidthName(Settings.DefaultWeaponOutlineWidth))) / OutlineWidthStep) * OutlineWidthStep;
                    // Beside the widths, as close up it is the width: the widths above stop growing at it.
                    Settings.outlineReach = Mathf.Round(Slider(list, "M3AT_OutlineReach".Translate(), Settings.outlineReach.ToString("0"),
                        Settings.outlineReach, 4f, 32f, "M3AT_OutlineReachTip".Translate(Settings.DefaultOutlineReach.ToString("0"))));
                    list.CheckboxLabeled("M3AT_SelfOutlines".Translate(), ref Settings.screenSelfOutlines, "M3AT_SelfOutlinesTip".Translate());
                    if (Settings.screenSelfOutlines)
                    {
                        Settings.selfOutlineStep = Mathf.Round(Slider(list, "M3AT_SelfOutlineStep".Translate(), OutlineWidthName(Settings.selfOutlineStep),
                            Settings.selfOutlineStep, 0.04f, 0.3f, "M3AT_SelfOutlineStepTip".Translate(OutlineWidthName(Settings.DefaultSelfOutlineStep))) / 0.01f) * 0.01f;
                    }
                    OutlineDetail(list);
                    if (!SkinnedFigure.ScreenOutlinesAvailable)
                    {
                        list.Label("M3AT_ScreenOutlinesUnavailable".Translate());
                    }
                }
                EndSubOptions(list);
            }
        }

        // Each weapon type's size in the hands and on the body, once the figure has loaded its types.
        private static void WeaponSizesSection(Listing_Standard list)
        {
            WalkFigures.SizesToHeight();
            List<KeyValuePair<string, float>> types = WalkFigures.TypeDefaults();
            if (types.Count > 0)
            {
                FoldHeader(list, "M3AT_WeaponSizes".Translate(), "M3AT_WeaponSizesTip".Translate(), ref weaponSizesOpen);
                if (!weaponSizesOpen)
                {
                    return;
                }
                foreach (KeyValuePair<string, float> type in types)
                {
                    string name = TypeName(type.Key);
                    float shownDefault = type.Value, shown;
                    if (!Settings.weaponScales.TryGetValue(type.Key, out shown))
                    {
                        shown = shownDefault;
                    }
                    shown = Mathf.Round(Slider(list, name, shown.ToStringPercent(), shown, WeaponLeast, WeaponMost,
                        "M3AT_WeaponSizeTip".Translate(shownDefault.ToStringPercent())) / WeaponStep) * WeaponStep;
                    // Within half a step of the default is the default, as the default is seldom on a step.
                    if (Mathf.Abs(shown - shownDefault) > WeaponStep * 0.5f + 0.0001f)
                    {
                        Settings.weaponScales[type.Key] = shown;
                    }
                    else
                    {
                        Settings.weaponScales.Remove(type.Key);
                    }
                }
                Settings.slabScale = Mathf.Round(Slider(list, "M3AT_SlabScale".Translate(), Settings.slabScale.ToStringPercent(),
                    Settings.slabScale, SlabLeast, SlabMost, "M3AT_SlabScaleTip".Translate(Settings.DefaultSlabScale.ToStringPercent()))
                    / WeaponStep) * WeaponStep;
                if (list.ButtonText("M3AT_ResetWeaponSizes".Translate()))
                {
                    Settings.weaponScales.Clear();
                    Settings.slabScale = Settings.DefaultSlabScale;
                }
            }
        }

        // What it costs to draw many pawns.
        private static void PerformanceSection(Listing_Standard list)
        {
            Header(list, "M3AT_Performance".Translate());
            list.CheckboxLabeled("M3AT_FewerPoses".Translate(), ref Settings.fewerPoses,
                "M3AT_FewerPosesTip".Translate());
            Settings.hairDetail = Mathf.Round(Slider(list, "M3AT_HairDetail".Translate(), Settings.hairDetail.ToStringPercent(), Settings.hairDetail, HairDetailLeast, 1f,
                "M3AT_HairDetailTip".Translate(Settings.DefaultHairDetail.ToStringPercent())) / HairDetailStep) * HairDetailStep;
        }

        // A report for the Workshop's bug thread (BugReport.cs), and every option back to its default.
        private static void ProblemsSection(Listing_Standard list)
        {
            Header(list, "M3AT_Problems".Translate());
            // Copied, and saved in case it is too long to paste.
            if (list.ButtonText("M3AT_CopyBugReport".Translate()))
            {
                string saved = BugReport.CopyAndSave();
                Messages.Message((saved != null ? "M3AT_BugReportCopiedSaved".Translate(saved) : "M3AT_BugReportCopied".Translate()) + " " + "M3AT_BugReportPaste".Translate(),
                    RimWorld.MessageTypeDefOf.NeutralEvent, false);
            }
            list.Label("M3AT_BugReportNote".Translate());
            list.Gap(6f);
            // The frame rate test (Benchmark.cs), opened over the map with the options closed, as it measures the map's view.
            if (BenchmarkWindow.CanRun)
            {
                Rect button = list.GetRect(30f);
                TooltipHandler.TipRegion(button, "M3AT_BenchmarkTip".Translate());
                if (Widgets.ButtonText(button, "M3AT_BenchmarkButton".Translate()))
                {
                    foreach (Window w in Find.WindowStack.Windows.ToList())
                    {
                        if (w.GetType().Name == "Dialog_ModSettings" || w.GetType().Name == "Dialog_Options")
                        {
                            w.Close(false);
                        }
                    }
                    BenchmarkWindow.Open();
                }
            }
            else
            {
                list.Label("M3AT_BenchmarkNeedsGame".Translate());
            }
            list.Gap(6f);
            if (list.ButtonText("M3AT_RestoreDefaults".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("M3AT_RestoreDefaultsConfirm".Translate(), RestoreDefaults, true));
            }
        }

        private static bool advancedOpen;   // the Advanced section shown (not saved: closed each time the options open)
        private static bool compatibilityOpen;   // and the Mod compatibility section
        private static bool outlinesOpen;   // the Outlines section
        private static bool weaponSizesOpen;   // the Weapon sizes section

        // A folded section's header: its name with an arrow, opened and closed by a click.
        private static void FoldHeader(Listing_Standard list, string label, string tip, ref bool open)
        {
            Text.Font = GameFont.Medium;
            Rect header = list.GetRect(Text.LineHeight);
            float icon = Text.LineHeight * 0.6f;
            GUI.DrawTexture(new Rect(header.x, header.y + (header.height - icon) / 2f, icon, icon), open ? TexButton.Collapse : TexButton.Reveal);
            Widgets.Label(new Rect(header.x + icon + 6f, header.y, header.width - icon - 6f, header.height), label);
            Text.Font = GameFont.Small;
            Widgets.DrawHighlightIfMouseover(header);
            TooltipHandler.TipRegion(header, tip);
            if (Widgets.ButtonInvisible(header))
            {
                open = !open;
            }
            list.GapLine(6f);
        }

        // Options for other mods' things, folded away until its header is clicked: other races (OtherRaces.cs) and their parts
        // (AddonParts.cs), and armour sizes (GrimWorld 40,000's Space Marines' size slider under their checkbox), each shown only
        // with a mod loaded it does something for. The race models' own switch went in Nov 2026: the content switcher's GeneParts
        // column chooses between M3AT's ears and tails and a pack's.
        private static void CompatibilitySection(Listing_Standard list)
        {
            FoldHeader(list, "M3AT_Compatibility".Translate(), "M3AT_CompatibilityTip".Translate(), ref compatibilityOpen);
            if (!compatibilityOpen)
            {
                return;
            }
            GUI.color = Color.gray;
            list.Label("M3AT_ContentCompatLine".Translate());
            GUI.color = Color.white;
            list.CheckboxLabeled("M3AT_OtherRaces".Translate(), ref Settings.otherRaces,
                "M3AT_OtherRacesTip".Translate());
            list.CheckboxLabeled("M3AT_RaceParts".Translate(), ref Settings.raceParts,
                "M3AT_RacePartsTip".Translate());
            GarmentDetailOption(list);
            // Shown only where they would do something: the drawn scale with a mod's body armour that has one loaded, the
            // Space Marines with GrimWorld 40,000 loaded (WalkFigures.ScaledArmourLoaded, GrimWorldLoaded).
            if (WalkFigures.ScaledArmourLoaded)
            {
                list.CheckboxLabeled("M3AT_ArmourDrawScale".Translate(), ref Settings.armourDrawScale,
                    "M3AT_ArmourDrawScaleTip".Translate());
            }
            if (!WalkFigures.GrimWorldLoaded)
            {
                return;
            }
            list.CheckboxLabeled("M3AT_MarineBulk".Translate(), ref Settings.marineBulk,
                "M3AT_MarineBulkTip".Translate());
            if (Settings.marineBulk)
            {
                BeginSubOptions(list);
                Settings.armourBulk = Mathf.Round(Slider(list, "M3AT_ArmourBulk".Translate(), Settings.armourBulk.ToStringPercent(), Settings.armourBulk, 1f, 1.6f,
                    "M3AT_ArmourBulkTip".Translate(Settings.DefaultArmourBulk.ToStringPercent())) * 20f) / 20f;
                EndSubOptions(list);
            }
        }

        // Tools few players need, folded away until its header is clicked: the face tuning (FaceTuning.cs), opened over the
        // map with the options closed so the face can be seen, and the face art's restyling (crisper mouths, sharper art).
        private static void AdvancedSection(Listing_Standard list)
        {
            FoldHeader(list, "M3AT_Advanced".Translate(), "M3AT_AdvancedTip".Translate(), ref advancedOpen);
            if (!advancedOpen)
            {
                return;
            }
            Text.Font = GameFont.Small;
            list.Label("M3AT_FaceTuningHeader".Translate());
            list.Label("M3AT_FaceTuningIntro".Translate());
            if (Current.ProgramState == ProgramState.Playing)
            {
                if (list.ButtonText("M3AT_FaceTuningOpen".Translate()))
                {
                    foreach (Window w in Find.WindowStack.Windows.ToList())
                    {
                        if (w.GetType().Name == "Dialog_ModSettings" || w.GetType().Name == "Dialog_Options")
                        {
                            w.Close(false);
                        }
                    }
                    FaceTuning.Open();
                }
            }
            else
            {
                list.Label("M3AT_FaceTuningNeedsGame".Translate());
            }
            int faced = Settings.faceTunes.Keys.Union(Settings.mouthTunes.Keys).Count();
            if (faced > 0 && list.ButtonText("M3AT_FaceClearAll".Translate(faced)))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("M3AT_FaceClearAllConfirm".Translate(faced), () =>
                {
                    Settings.faceTunes.Clear();
                    Settings.mouthTunes.Clear();
                    Settings.faceFits.Clear();
                    Settings.faceFitRows.Clear();
                    Settings.faceFitMouths.Clear();
                }, true));
            }
            // The face art's restyling, beside the face tuning: on by default and rarely worth turning off.
            list.Gap(6f);
            list.CheckboxLabeled("M3AT_RaceMouthInk".Translate(), ref Settings.raceMouthInk,
                "M3AT_RaceMouthInkTip".Translate());
            list.CheckboxLabeled("M3AT_MouthSizeFit".Translate(), ref Settings.mouthSizeFit,
                "M3AT_MouthSizeFitTip".Translate());
            list.CheckboxLabeled("M3AT_SharpFaceArt".Translate(), ref Settings.sharpFaceArt,
                "M3AT_SharpFaceArtTip".Translate());
            // Here, folded away, rather than under Performance: it is much faster and should stay on, kept only for a
            // graphics driver that poses pawns wrongly on the graphics card.
            list.Gap(6f);
            list.CheckboxLabeled("M3AT_GpuSkinning".Translate(), ref Settings.gpuSkinning,
                "M3AT_GpuSkinningTip".Translate());
            // The freecam's, here rather than in its own section, which is long enough: few will want it.
            list.CheckboxLabeled("M3AT_FreecamClickOffLooks".Translate(), ref Settings.freecamClickOffLooks,
                "M3AT_FreecamClickOffLooksTip".Translate());
            list.CheckboxLabeled("M3AT_GripToolButton".Translate(), ref Settings.gripToolButton,
                "M3AT_GripToolButtonTip".Translate());
            // Moved here from the Content tab, where it took room from the packs: few will want the button gone.
            list.CheckboxLabeled("M3AT_ContentButton".Translate(), ref Settings.contentButton,
                "M3AT_ContentButtonTip".Translate());
        }

        // The screen-space outline's edge pass, under the outline style while it is screen-space (the only style that reads
        // them): the numbers trade the lines' look close up against the frame time. The widest line's slider is with the
        // widths, though the Default and Faster buttons set it too.
        private static void OutlineDetail(Listing_Standard list)
        {
            list.Gap(6f);
            list.Label("M3AT_OutlineDetailHeader".Translate(), -1f, "M3AT_OutlineDetailIntro".Translate());
            Settings.outlineProbeDirs = Mathf.RoundToInt(Slider(list, "M3AT_OutlineProbeDirs".Translate(), Settings.outlineProbeDirs.ToString(),
                Settings.outlineProbeDirs, 4f, 16f, "M3AT_OutlineProbeDirsTip".Translate(Settings.DefaultOutlineProbeDirs)));
            Settings.outlineProbeRings = Mathf.RoundToInt(Slider(list, "M3AT_OutlineProbeRings".Translate(), Settings.outlineProbeRings.ToString(),
                Settings.outlineProbeRings, 1f, 4f, "M3AT_OutlineProbeRingsTip".Translate(Settings.DefaultOutlineProbeRings)));
            Settings.outlineRings = Mathf.RoundToInt(Slider(list, "M3AT_OutlineRings".Translate(), Settings.outlineRings.ToString(),
                Settings.outlineRings, 2f, 16f, "M3AT_OutlineRingsTip".Translate(Settings.DefaultOutlineRings)));
            Rect row = list.GetRect(30f);
            if (Widgets.ButtonText(row.LeftHalf().ContractedBy(2f, 0f), "M3AT_OutlineDetailFast".Translate()))
            {
                Settings.SetFasterOutlines(false);
            }
            if (Widgets.ButtonText(row.RightHalf().ContractedBy(2f, 0f), "M3AT_OutlineDetailFaster".Translate()))
            {
                Settings.SetFasterOutlines(true);
            }
            list.Gap(6f);
        }

        // The freecam (Freecam.cs): its keys, pointer and flying.
        private static void FreecamSection(Listing_Standard list)
        {
            Header(list, "M3AT_Freecam".Translate());
            list.Label("M3AT_FreecamIntro".Translate());
            list.Gap(6f);
            list.Label("M3AT_FreecamKeys".Translate() + "\n"
                + KeyName(Freecam.ToggleKey) + ": " + "M3AT_FreecamKeyToggle".Translate() + "\n"
                + KeyName(Freecam.UpKey) + ": " + "M3AT_FreecamKeyUp".Translate() + "\n"
                + KeyName(Freecam.DownKey) + ": " + "M3AT_FreecamKeyDown".Translate() + "\n"
                + KeyName(Freecam.BoostKey) + ": " + "M3AT_FreecamKeyBoost".Translate() + "\n"
                + KeyName(Freecam.PointerKey) + ": " + "M3AT_FreecamKeyPointer".Translate());
            if (list.ButtonTextLabeledPct("M3AT_FreecamButton".Translate(), ButtonName(Settings.freecamButton), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "M3AT_FreecamButtonTip".Translate()))
            {
                List<FloatMenuOption> buttons = new List<FloatMenuOption>();
                foreach (int button in new[] { 2, 3, 4, -1 })
                {
                    int picked = button;
                    buttons.Add(new FloatMenuOption(ButtonName(button), () => Settings.freecamButton = picked));
                }
                Find.WindowStack.Add(new FloatMenu(buttons));
            }
            // Where the freed pointer appears: back where it was when looking began, or under the cross.
            if (list.ButtonTextLabeledPct("M3AT_FreecamPointerBack".Translate(), PointerPlaceName(Settings.freecamPointerBack), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "M3AT_FreecamPointerBackTip".Translate()))
            {
                List<FloatMenuOption> places = new List<FloatMenuOption>();
                foreach (bool back in new[] { true, false })
                {
                    bool picked = back;
                    places.Add(new FloatMenuOption(PointerPlaceName(back), () => Settings.freecamPointerBack = picked));
                }
                Find.WindowStack.Add(new FloatMenu(places));
            }
            // The dot marks the last position, so it only has a say while the cursor goes back there.
            if (Settings.freecamPointerBack)
                list.CheckboxLabeled("M3AT_FreecamPointerDot".Translate(), ref Settings.freecamPointerDot,
                    "M3AT_FreecamPointerDotTip".Translate());
            if (list.ButtonTextLabeledPct("M3AT_FreecamMenuFreesPointer".Translate(), MenuSelectName(Settings.freecamMenuFreesPointer), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "M3AT_FreecamMenuFreesPointerTip".Translate()))
            {
                List<FloatMenuOption> kinds = new List<FloatMenuOption>();
                foreach (bool pointer in new[] { true, false })
                {
                    bool picked = pointer;
                    kinds.Add(new FloatMenuOption(MenuSelectName(pointer), () => Settings.freecamMenuFreesPointer = picked));
                }
                Find.WindowStack.Add(new FloatMenu(kinds));
            }
            list.CheckboxLabeled("M3AT_FreecamSnapArchitect".Translate(), ref Settings.freecamSnapArchitect,
                "M3AT_FreecamSnapArchitectTip".Translate());
            Settings.freecamSensitivity = Mathf.Round(Slider(list, "M3AT_LookSpeed".Translate(), Settings.freecamSensitivity.ToString("0.00"), Settings.freecamSensitivity, 0.02f, 0.6f,
                "M3AT_LookSpeedTip".Translate(Settings.DefaultFreecamSensitivity.ToString("0.00"))) * 100f) / 100f;
            list.CheckboxLabeled("M3AT_InvertMouse".Translate(), ref Settings.freecamInvertY, "M3AT_InvertMouseTip".Translate());
            list.CheckboxLabeled("M3AT_FreecamCutaway".Translate(), ref Settings.freecamCutaway,
                "M3AT_FreecamCutawayTip".Translate());
            list.CheckboxLabeled("M3AT_ScrollFlies".Translate(), ref Settings.freecamScrollFlies,
                "M3AT_ScrollFliesTip".Translate());
            Settings.freecamSpeed = Mathf.Round(Slider(list, "M3AT_FlyingSpeed".Translate(), "M3AT_CellsPerSecond".Translate(Settings.freecamSpeed.ToString("0")), Settings.freecamSpeed, 2f, 60f,
                "M3AT_FlyingSpeedTip".Translate(Settings.DefaultFreecamSpeed.ToString("0"))));
            Settings.freecamScrollSpeed = Mathf.Round(Slider(list, "M3AT_ScrollSpeed".Translate(), Settings.freecamScrollSpeed.ToString("0.00") + "x", Settings.freecamScrollSpeed, 0.1f, 2f,
                "M3AT_ScrollSpeedTip".Translate(Settings.DefaultFreecamScrollSpeed.ToString("0.00") + "x")) * 20f) / 20f;
            Settings.freecamFov = Mathf.Round(Slider(list, "M3AT_FieldOfView".Translate(), Settings.freecamFov.ToString("0") + "°", Settings.freecamFov, 40f, 110f,
                "M3AT_FieldOfViewTip".Translate(Settings.DefaultFreecamFov.ToString("0") + "°")));
        }

        private const float TabHeight = 34f;
        private static int tab;   // 0 the options, 1 content packs (ContentWindow.cs); not saved
        private static readonly ContentView contentView = new ContentView();

        // Two tabs: the options, and the content packs' switcher, where players find it (ContentWindow.cs).
        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect body = new Rect(inRect.x, inRect.y + TabHeight, inRect.width, inRect.height - TabHeight);
            int packs = WalkFigures.SkinForAddOns != null ? ContentSources.Packs().Count : 0;
            List<TabRecord> tabs = new List<TabRecord>
            {
                new TabRecord("M3AT_TabOptions".Translate(), () => tab = 0, tab == 0),
                new TabRecord(packs > 0 ? "M3AT_TabContentCount".Translate(packs) : "M3AT_TabContent".Translate(), () => tab = 1, tab == 1),
            };
            TabDrawer.DrawTabs(body, tabs);
            if (tab == 1)
            {
                contentView.Draw(body.ContractedBy(8f), null, true);
                return;
            }
            DrawOptions(body);
        }

        private void DrawOptions(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(shownHeight, inRect.height - 4f));   // 16 px for the scroll bar
            if (view.width != laidOutWidth)
            {
                laidOutWidth = view.width;
                relayout = true;
            }
            // Until the sections have been drawn once there is nothing to go by, so the first draw takes a guess and the
            // next lays them out.
            if (columnBreak < 0 || (relayout && measured))
            {
                columnBreak = measured ? Columns() : 5;
                relayout = !measured;
            }
            Widgets.BeginScrollView(inRect, ref scroll, view);
            // The columns are started by hand: the listing's rect is far taller than any of them, so it never starts one
            // itself, and the first is measured before the second starts so the scroll view knows how far to go.
            Listing_Standard list = new Listing_Standard { ColumnWidth = (view.width - (ColumnCount - 1) * Listing.ColumnSpacing) / ColumnCount };
            list.Begin(new Rect(view.x, view.y, view.width, 100000f));
            int n = Sections.Length;
            void Draw(System.Action<Listing_Standard> section, int slot, bool first)
            {
                if (!first)
                {
                    list.Gap(SectionGap);
                }
                float top = list.CurHeight;
                section(list);
                sectionHeights[slot] = list.CurHeight - top;
            }
            for (int i = 0; i < columnBreak; i++)
            {
                Draw(Sections[i], i, i == 0);
            }
            float tallest = list.CurHeight;
            list.NewColumn();
            Draw(PinnedTop, n, true);
            for (int i = columnBreak; i < n; i++)
            {
                Draw(Sections[i], i, false);
            }
            for (int i = 0; i < PinnedBottom.Length; i++)
            {
                Draw(PinnedBottom[i], n + 1 + i, false);
            }
            measured = true;
            shownHeight = Mathf.Max(tallest, list.CurHeight) + 12f;
            list.End();
            Widgets.EndScrollView();
        }
    }
}
