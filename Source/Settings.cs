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

    public enum BeardMode
    {
        Flat,
        ThreeD,
        Off
    }

    // The player's choices, saved with the game's mod settings (Options > Mod settings > M3AT).
    public class Settings : ModSettings
    {
        public const float DefaultShrink = 0f;   // the head shrink the options start at: the full chibi head
        public const float DefaultHeightSpread = 0.04f, DefaultBodySpread = 0.3f;   // the variety sliders' defaults
        public const float DefaultHairDetail = 0.5f;   // the share of its triangles a hairstyle keeps
        public float pawnSize = 1f;   // every pawn's size, of the standard (WalkFigures.PawnSize, in SizeOf)
        public float headShrink = DefaultShrink;   // 0 .. 1 of the Shrinkhead key: a smaller, less chibi head (skinned figure only)
        public bool genderSizes = true;   // grown males are drawn bigger than grown females, by WalkFigures.GenderGap (WalkFigures.GenderSize)
        public float heightSpread = DefaultHeightSpread;   // the standard deviation of pawns' own heights about their gender's, of the standard size (WalkFigures.HeightSize)
        public bool variedBodies = false;   // each pawn's build spread about its body type's by the Thin, Fat and Hulk keys (WalkFigures.VariedBuild)
        public float bodySpread = DefaultBodySpread;   // and the standard deviation of that spread, in key amounts
        public bool ragdolls = true;   // pawns that go down where they stand fall limp (WalkFigures.Ragdolls)
        public float flingPower = 1f;   // how far blows throw bodies and weapons, of the standard distance (WalkFigures.FlingPower)
        public bool flingBodies = false;   // a big enough blow throws a body across the map, the game moving it (WalkFigures.FlingBodies, Flings)
        public bool flingWeapons = false;   // the weapon a pawn drops as it goes down flies from its hand (WalkFigures.FlingWeapons, Flings)
        public bool wornGear = true;   // packs, belts, headsets, masks and crowns a pawn wears are drawn on it (WalkFigures.WornGears)
        public bool muzzleFlash = true;   // a gun fired by a 3D pawn flashes at its barrel (WalkFigures.MuzzleFlashes)
        public bool hitFlash = true;   // a pawn hit flashes red, as the game's sprites do (HitFlash.On)
        public bool holsters = true;   // weapons a pawn has but isn't holding are drawn on its body (WalkFigures.Holsters)
        public bool shoes = true;   // pawns with the torso and legs covered have shoes on (WalkFigures.Shoes)
        public bool armourHidesTail = true;   // body armour (a garment with hide_clothes, not a vacsuit) leaves the gene tail off (WalkFigures.ArmourHidesTail)
        public EarHiding earHiding = EarHiding.AnyHeadgear;   // which headgear leaves the gene ears off (WalkFigures.EarHiding)
        public BeardMode beardMode = BeardMode.Flat;   // how beards are drawn (WalkFigures.Beards, FlatBeards)
        public bool retexturedWeaponModels = true;   // the game's own weapons keep IsoRim's model though another mod redraws their art (WalkFigures.RetexturedWeaponModels)
        public bool groundGarments = false;   // apparel lying on the map is drawn as the chibi's own garment for it (WalkFigures.GroundGarments); off: IsoRim's look
        public bool injuryAnims = true;   // pawns limp on a bad leg and hold where they are hurt (WalkFigures.InjuryAnims); a missing leg hops regardless
        public bool visibleWounds = true;   // injuries, blood, bandages and scars are drawn on the pawn (WalkFigures.VisibleWounds)
        public bool outlines = true;   // the chibi is drawn with its dark outline (SkinnedFigure.Outlines)
        public bool silhouetteOutlines = true;   // only round the figure, never over its own body (SkinnedFigure.Silhouette); off: as IsoRim draws its figures' outlines
        public bool clothesOcclusion = true;    // IsoRim's ambient occlusion and screen-space shadows on clothes and hair too, never the skin (SkinnedFigure.ClothesOcclusion)
        public bool fewerPoses = false;   // with many pawns on screen, each one's limbs move every second or third frame (SkinnedFigure.FewerPoses)
        public bool gpuSkinning = true;   // pawns' bodies, clothes and hair posed by the graphics card (SkinnedFigure.GpuSkinning); off: on the CPU
        public float hairDetail = DefaultHairDetail;   // the share of its triangles each built hairstyle and its shell keep as they are read (SettingsMod.HairDetail)
        // The freecam (Freecam.cs): whether a click off a menu leaves it open, how fast the mouse turns it (degrees per
        // pixel) and it flies (cells per real second), and its field of view.
        public bool freecamOn = false;   // the freecam is wanted: kept through 2D, other maps and restarts (Freecam.Wanted)
        public bool freecam3D = false;   // the 3D view was on with it, so a loaded game starts in 3D (Freecam.Resume)
        public bool freecamClickOffLooks = false;   // in pointer mode, a left click on nothing goes back to looking
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

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref pawnSize, "pawnSize", 1f);
            Scribe_Values.Look(ref headShrink, "headShrink", DefaultShrink);
            Scribe_Values.Look(ref genderSizes, "genderSizes", true);
            Scribe_Values.Look(ref heightSpread, "heightSpread", DefaultHeightSpread);
            Scribe_Values.Look(ref variedBodies, "variedBodies", false);
            Scribe_Values.Look(ref bodySpread, "bodySpread", DefaultBodySpread);
            Scribe_Values.Look(ref ragdolls, "ragdolls", true);
            Scribe_Values.Look(ref flingPower, "flingPowerRescaled", 1f);   // not "flingPower": those were of a distance a sixth of this one
            Scribe_Values.Look(ref flingBodies, "flingBodies", false);
            Scribe_Values.Look(ref flingWeapons, "flingWeapons", false);
            Scribe_Values.Look(ref wornGear, "wornGear", true);
            Scribe_Values.Look(ref holsters, "holsters", true);
            Scribe_Values.Look(ref muzzleFlash, "muzzleFlash", true);
            Scribe_Values.Look(ref hitFlash, "hitFlash", true);
            Scribe_Values.Look(ref shoes, "shoes", true);
            Scribe_Values.Look(ref armourHidesTail, "armourHidesTail", true);
            Scribe_Values.Look(ref earHiding, "earHiding", EarHiding.AnyHeadgear);
            Scribe_Values.Look(ref beardMode, "beardMode", BeardMode.Flat);
            Scribe_Values.Look(ref retexturedWeaponModels, "retexturedWeaponModels", true);
            Scribe_Values.Look(ref groundGarments, "groundGarments", false);
            Scribe_Values.Look(ref injuryAnims, "injuryAnims", true);
            Scribe_Values.Look(ref visibleWounds, "visibleWounds", true);
            Scribe_Values.Look(ref outlines, "outlines", true);
            Scribe_Values.Look(ref silhouetteOutlines, "silhouetteOutlines", true);
            Scribe_Values.Look(ref clothesOcclusion, "clothesOcclusion", true);
            Scribe_Values.Look(ref fewerPoses, "fewerPoses", false);
            Scribe_Values.Look(ref gpuSkinning, "gpuSkinning", true);
            Scribe_Values.Look(ref hairDetail, "hairDetail", DefaultHairDetail);
            Scribe_Values.Look(ref freecamOn, "freecamOn", false);
            Scribe_Values.Look(ref freecam3D, "freecam3D", false);
            Scribe_Values.Look(ref freecamClickOffLooks, "freecamClickOffLooks", false);
            Scribe_Values.Look(ref freecamButton, "freecamButton", 2);
            Scribe_Values.Look(ref freecamSensitivity, "freecamSensitivity", DefaultFreecamSensitivity);
            Scribe_Values.Look(ref freecamInvertY, "freecamInvertY", false);
            Scribe_Values.Look(ref freecamScrollFlies, "freecamScrollFlies", true);
            Scribe_Values.Look(ref freecamSpeed, "freecamSpeed", DefaultFreecamSpeed);
            Scribe_Values.Look(ref freecamScrollSpeed, "freecamScrollSpeed", DefaultFreecamScrollSpeed);
            Scribe_Values.Look(ref freecamFov, "freecamFov", DefaultFreecamFov);
            Scribe_Values.Look(ref freecamCutaway, "freecamCutaway", false);
            Scribe_Collections.Look(ref weaponScales, "weaponGroundScales", LookMode.Value, LookMode.Value);
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
        private const float SizeStep = 0.05f;   // the pawn size slider's steps
        private const float SizeLeast = 0.5f, SizeMost = 1.5f;
        private const float WeaponStep = 0.05f;   // the weapon size sliders' steps
        private const float WeaponLeast = 0.25f, WeaponMost = 2f;   // against the ground
        private const float HairDetailStep = 0.05f, HairDetailLeast = 0.05f;   // up to all of it
        private const float FlingPowerStep = 0.25f, FlingPowerLeast = 0.25f, FlingPowerMost = 5f;   // of the standard distance
        private const float SubOptionIndent = 24f;   // how far in, px, the options a checkbox shows are set
        private const float SliderLabelShare = 0.55f;   // of a column, the label beside a slider or button

        // What each weapon type (walk.WEAPON_TYPES) is called in the options.
        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            { "handgun", "Handguns" }, { "rifle", "Rifles and launchers" }, { "bow", "Bows" }, { "grenade", "Grenades" },
            { "melee_1h", "One-handed melee" }, { "melee_2h", "Two-handed melee" },
        };

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
        private static float HeadSize(float shrink)
        {
            return 1f - (1f - ShrunkSize) * shrink;
        }

        public SettingsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<Settings>();
            HairDetail = Settings.hairDetail;
            groundGarmentsBaked = Settings.groundGarments;
            RestoredModels.Patch();   // before IsoRim's startup binds its models
            weaponModelsBaked = Settings.retexturedWeaponModels;
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            HairDetail = Settings.hairDetail;
            if (groundGarmentsBaked != Settings.groundGarments)
            {
                groundGarmentsBaked = Settings.groundGarments;
                WalkFigures.RebakeGroundClothes();
            }
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
        private static float Slider(Listing_Standard list, string label, float value, float least, float most, string tooltip)
        {
            Rect rect = list.GetRect(24f);
            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }
            TooltipHandler.TipRegion(rect, tooltip);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect.LeftPart(SliderLabelShare), label);
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
            return def == null ? "?" : def.MainKey == KeyCode.None ? "unbound" : def.MainKeyLabel;
        }

        private static string ButtonName(int button)
        {
            return button == 2 ? "Middle button" : button == 3 ? "Mouse button 4" : button == 4 ? "Mouse button 5" : "None";
        }

        private static string EarName(EarHiding mode)
        {
            return mode == EarHiding.AnyHeadgear ? "Any headgear" : mode == EarHiding.Helmets ? "Helmets only" : "Never";
        }

        private static string BeardName(BeardMode mode)
        {
            return mode == BeardMode.Flat ? "2D" : mode == BeardMode.ThreeD ? "3D" : "Off";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(shownHeight, inRect.height - 4f));   // 16 px for the scroll bar
            Widgets.BeginScrollView(inRect, ref scroll, view);
            // Three columns, each started by hand: the listing's rect is far taller than any of them, so it never starts
            // one itself, and the tallest is measured before each new one so the scroll view knows how far to go.
            Listing_Standard list = new Listing_Standard { ColumnWidth = (view.width - 2f * Listing.ColumnSpacing) / 3f };
            list.Begin(new Rect(view.x, view.y, view.width, 100000f));
            float tallest = 0f;

            Header(list, "Pawns");
            Settings.pawnSize = Mathf.Round(Slider(list, "Pawn size: " + Settings.pawnSize.ToStringPercent(), Settings.pawnSize, SizeLeast, SizeMost,
                "How big every pawn is drawn. 100% (default) is the usual size; children, gender and height variety are on top of it. It changes only how pawns look, not the space they take up.") / SizeStep) * SizeStep;
            // Shown as how much smaller the head is than the full chibi head's, measured, not the key's amount.
            float shrink = Slider(list, "Head shrink: " + (1f - HeadSize(Settings.headShrink)).ToStringPercent(), Settings.headShrink, 0f, 1f,
                "How much smaller the head is drawn than the default chibi. Left: default; right: smaller. No effect on vanilla or CE hit calculations.");
            Settings.headShrink = Mathf.Round(shrink / ShrinkStep) * ShrinkStep;
            float spread = Slider(list, "Height variety: " + Settings.heightSpread.ToStringPercent("0.#"), Settings.heightSpread, 0f, 0.1f,
                "How much pawns' heights vary (default " + Settings.DefaultHeightSpread.ToStringPercent("0.#") + "). 4% is realistic, with most near the usual size, hardly anyone over 10% off. 0% makes everyone of a gender the same size. Each pawn's height is derived from the pawn's id, so no save data is needed.");
            Settings.heightSpread = Mathf.Round(spread * 200f) / 200f;
            list.CheckboxLabeled("Gender size difference", ref Settings.genderSizes,
                "Renders men and women with a 10% size difference eased in over teenage years. Off: they are the same size.");
            list.CheckboxLabeled("Varied bodies", ref Settings.variedBodies,
                "Each pawn gets a build of its own, somewhere around its body type: some thinner or heavier, some more muscular, so not every average body looks the same. Thin, fat and hulk pawns are still based on those bodies. Off: every pawn of a body type has the same body.");
            if (Settings.variedBodies)
            {
                BeginSubOptions(list);
                Settings.bodySpread = Mathf.Round(Slider(list, "Body variety: " + Settings.bodySpread.ToStringPercent(), Settings.bodySpread, 0.05f, 0.6f,
                    "How far pawns' builds wander from their body type (default " + Settings.DefaultBodySpread.ToStringPercent() + "). Most stay near it, a few go most of the way to the next type. Each pawn's build is derived from the pawn's id, so no save data is needed.") * 20f) / 20f;
                EndSubOptions(list);
            }
            Settings.hairDetail = Mathf.Round(Slider(list, "Hair detail: " + Settings.hairDetail.ToStringPercent(), Settings.hairDetail, HairDetailLeast, 1f,
                "How much of the shape of each hairstyle made from its sprites is kept (default " + Settings.DefaultHairDetail.ToStringPercent() + "). Higher looks smoother and follows the art more closely, but costs more with many pawns on screen. Hair a mod supplies as a 3D model is always drawn in full. Takes effect when this window closes.") / HairDetailStep) * HairDetailStep;
            if (list.ButtonTextLabeledPct("Beards", BeardName(Settings.beardMode), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "2D (default): the beard's sprite is laid flat on the face. Cheap, but only seen from the front, and cut off at the chin.\n\n3D: each beard is built as a 3D mesh from its sprites the first time it is worn, so it is seen from every side.\n\nOff: no beards are drawn or built."))
            {
                List<FloatMenuOption> modes = new List<FloatMenuOption>();
                foreach (BeardMode mode in new[] { BeardMode.Flat, BeardMode.ThreeD, BeardMode.Off })
                {
                    BeardMode picked = mode;
                    modes.Add(new FloatMenuOption(BeardName(mode), () => Settings.beardMode = picked));
                }
                Find.WindowStack.Add(new FloatMenu(modes));
            }
            list.CheckboxLabeled("Shoes when dressed", ref Settings.shoes,
                "A pawn whose clothes cover both the body and the legs (a shirt and trousers, or a duster, robe or suit) has plain shoes on. Off: bare feet unless a mod's shoes are worn.");
            list.CheckboxLabeled("Armour hides tails", ref Settings.armourHidesTail,
                "A pawn with a tail gene wearing full body armour (power, recon, cataphract or plate armour) has its tail hidden, as it would otherwise go through the armour. Vacsuits leave it out. Off: the tail is always shown.");
            if (list.ButtonTextLabeledPct("Headgear hides animal ears", EarName(Settings.earHiding), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "Whether cat, pig or floppy ears are hidden under what a pawn wears on its head, as they would otherwise go through it.\n\nAny headgear (default): helmets, and hats that cover the top of the head.\n\nHelmets only: armour helmets (simple, advanced, power armour, recon, cataphract and the like); the ears poke through other hats.\n\nNever: the ears are always shown."))
            {
                List<FloatMenuOption> modes = new List<FloatMenuOption>();
                foreach (EarHiding mode in new[] { EarHiding.AnyHeadgear, EarHiding.Helmets, EarHiding.Never })
                {
                    EarHiding picked = mode;
                    modes.Add(new FloatMenuOption(EarName(mode), () => Settings.earHiding = picked));
                }
                Find.WindowStack.Add(new FloatMenu(modes));
            }
            list.CheckboxLabeled("Worn packs, belts and headwear", ref Settings.wornGear,
                "Jump packs, shield belts and the other packs, belts and lances a pawn wears are shown on its back or waist, and headsets, masks, crowns, blindfolds and slave collars on its head and neck, with IsoRim's models of them. Off: they are not shown.");
            list.CheckboxLabeled("M3AT apparel on the ground", ref Settings.groundGarments,
                "Clothes on the map will be drawn as the same 3D garment a pawn wears, squashed flat. Off (default): IsoRim's 3D models are used. Takes effect when this window closes.");

            list.Gap();
            Header(list, "Injuries");
            list.CheckboxLabeled("Injury-reactive animations", ref Settings.injuryAnims,
                "Pawns limp on a bad leg, hold injured areas, etc. Off: they move as if unhurt. Off doesn't disable hit flinch animations, hopping for missing legs, or slowed movement.");
            list.CheckboxLabeled("Visible injuries", ref Settings.visibleWounds,
                "Wounds, blood, bandages and scars are drawn on the pawn, where the injury is. Wounds and blood are visible through some clothing; bruises, bandages and scars go only on the skin. Off: none are drawn.");

            tallest = Mathf.Max(tallest, list.CurHeight);
            list.NewColumn();
            Header(list, "Combat");
            list.CheckboxLabeled("Ragdolls", ref Settings.ragdolls,
                "Pawns knocked down or killed where they stand fall limp under their own weight and lie as they land. Off: they play a set fall instead.");
            if (Settings.ragdolls)
            {
                BeginSubOptions(list);
                Settings.flingPower = Mathf.Round(Slider(list, "Fling power: " + Settings.flingPower.ToStringPercent(), Settings.flingPower, FlingPowerLeast, FlingPowerMost,
                    "How far blows throw bodies and weapons (default 100%). 200% throws them about twice as far. With \"Throw bodies\" on, the biggest blasts throw a grown pawn about 6 cells at 100% and about 30 at 500%, and a grenade about a quarter of that; with it off, this only changes how hard a body is knocked about where it falls.") / FlingPowerStep) * FlingPowerStep;
                list.CheckboxLabeled("Throw bodies between cells", ref Settings.flingBodies,
                    "Enough damage can throw the pawn they knock down or kill. Explosions and heavy melee move most. This lets M3AT move things around in the game itself, so could cause issues in multiplayer or other mods that expect the game alone to decide where things are. Off: bodies stay in the cell they fell in.");
                list.CheckboxLabeled("Throw weapons between cells", ref Settings.flingWeapons,
                    "The weapon a pawn drops as it goes down can be flung. This lets M3AT move things around in the game itself, not only on screen, so could cause issues in multiplayer or other mods that expect the game alone to decide where things are. Off: it is dropped at the pawn's feet.");
                EndSubOptions(list);
            }
            list.CheckboxLabeled("Weapons worn on the body", ref Settings.holsters,
                "A weapon a pawn carries but isn't holding, and any in its inventory, is shown on its back or hip. Off: only weapons in hand are shown.");
            list.CheckboxLabeled("Muzzle flash", ref Settings.muzzleFlash,
                "A gun fired by a pawn flashes at the end of its barrel, a burst of fire along the shot as well as the game's own glow. Off: only the game's glow.");
            list.CheckboxLabeled("Flash red when hit", ref Settings.hitFlash,
                "A pawn that takes damage flashes red for a moment, as the game's own pawns and IsoRim's 3D figures do. Off: no flash.");
            list.CheckboxLabeled("IsoRim models over weapon retextures", ref Settings.retexturedWeaponModels,
                "The game's own weapons are shown as IsoRim's 3D models even when another mod (such as Combat Extended or a texture pack) gives them new art, or removes their models. The model is lined up with the new art but keeps its own shape, so a weapon redrawn very differently may not match it. Weapons from other mods are left to IsoRim. Off: IsoRim's choice, which shows a retextured weapon as its art. Needs IsoRim's \"Weapons as 3D models\" setting. Takes effect when this window closes for weapons on the ground.");
            List<KeyValuePair<string, float>> types = WalkFigures.TypeDefaults();
            if (types.Count > 0)
            {
                list.Gap();
                Header(list, "Weapon sizes");
                foreach (KeyValuePair<string, float> type in types)
                {
                    string name;
                    if (!TypeNames.TryGetValue(type.Key, out name))
                    {
                        name = type.Key;
                    }
                    float shownDefault = type.Value, shown;
                    if (!Settings.weaponScales.TryGetValue(type.Key, out shown))
                    {
                        shown = shownDefault;
                    }
                    shown = Mathf.Round(Slider(list, name + ": " + shown.ToStringPercent(), shown, WeaponLeast, WeaponMost,
                        "This weapon type's size in a pawn's hands or on its body against the same weapon lying on the ground (default " + shownDefault.ToStringPercent() + "). 100% is the size IsoRim draws it on the ground.") / WeaponStep) * WeaponStep;
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
                if (list.ButtonText("Reset weapon sizes"))
                {
                    Settings.weaponScales.Clear();
                }
            }

            tallest = Mathf.Max(tallest, list.CurHeight);
            list.NewColumn();
            Header(list, "Drawing");
            bool hadOutlines = Settings.outlines;
            list.CheckboxLabeled("Outlines", ref Settings.outlines,
                "Pawns are drawn with a dark outline round the body, clothes and hair, as IsoRim draws its models. Off: no outline. Weapons and tools keep IsoRim's own.");
            if (Settings.outlines)
            {
                // Ticking Outlines starts from the silhouette, whatever it was left at when they were last turned off.
                if (!hadOutlines)
                {
                    Settings.silhouetteOutlines = true;
                }
                BeginSubOptions(list);
                list.CheckboxLabeled("Disable self-outlines", ref Settings.silhouetteOutlines,
                    "The outline goes only round the outside of the pawn, never over its own body. Off: as IsoRim draws its outlines, with lines over the body too where a part such as an arm is well in front of the rest.");
                EndSubOptions(list);
            }
            list.CheckboxLabeled("Occlusion and shadows on clothes", ref Settings.clothesOcclusion,
                "With IsoRim's ambient occlusion or screen-space shadows on, clothes and hair are darkened in their creases and by shadows as the rest of the world is. It picks out their flat faces, so they look less smooth. Off: pawns are left out of both, though they still cast shadows. Skin and faces are always left out, as it spoils the eyes.");

            list.Gap();
            Header(list, "Performance");
            list.CheckboxLabeled("Animate on the graphics card", ref Settings.gpuSkinning,
                "Moves each pawn's body, clothes and hair into its pose on the graphics card, which is much faster with many pawns in view. Turn it off if pawns look broken or stretched, to animate them the old way.");
            list.CheckboxLabeled("Fewer updates with many pawns", ref Settings.fewerPoses,
                "Runs the game faster when many pawns are in view: their limbs move every second or third frame, while they still walk and turn smoothly. Off: every pawn's limbs move every frame.");

            list.Gap();
            Header(list, "Flying freecam");
            list.Label("Set keyboard keys in RimWorld's keybindings. Current settings:\n"
                + KeyName(Freecam.ToggleKey) + ": flying freecam on/off\n"
                + KeyName(Freecam.UpKey) + ": up\n"
                + KeyName(Freecam.DownKey) + ": down\n"
                + KeyName(Freecam.BoostKey) + ": double speed\n"
                + KeyName(Freecam.PointerKey) + ": toggle pointer / freelook");
            if (list.ButtonTextLabeledPct("Toggle pointer (mouse)", ButtonName(Settings.freecamButton), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "A mouse-button version of the \"toggle pointer\" key above: it frees the pointer or goes back to looking. Default: middle button"))
            {
                List<FloatMenuOption> buttons = new List<FloatMenuOption>();
                foreach (int button in new[] { 2, 3, 4, -1 })
                {
                    int picked = button;
                    buttons.Add(new FloatMenuOption(ButtonName(button), () => Settings.freecamButton = picked));
                }
                Find.WindowStack.Add(new FloatMenu(buttons));
            }
            list.CheckboxLabeled("Empty left click closes pointermode", ref Settings.freecamClickOffLooks,
                "In pointer mode, a left click that selects nothing goes back to looking, as a right click does. Off (default): it only clears the selection, as in the game.");
            Settings.freecamSensitivity = Mathf.Round(Slider(list, "Look speed: " + Settings.freecamSensitivity.ToString("0.00"), Settings.freecamSensitivity, 0.02f, 0.6f,
                "How far the view turns for the mouse's movement, in degrees per pixel (default " + Settings.DefaultFreecamSensitivity.ToString("0.00") + ").") * 100f) / 100f;
            list.CheckboxLabeled("Invert mouse look", ref Settings.freecamInvertY, "Moving the mouse up looks down.");
            list.CheckboxLabeled("Use IsoRim's wall hiding", ref Settings.freecamCutaway,
                "While flying, walls are cut down as IsoRim's own settings say: around the pointer (the middle of the screen while looking), near colonists and in front of rooms. Off (default): every wall stands at full height, as in IsoRim's first-person view.");
            list.CheckboxLabeled("Scroll wheel flies up and down", ref Settings.freecamScrollFlies,
                "Scrolling up raises the camera and scrolling down lowers it, further at a higher flying speed. Off: the scroll wheel does nothing while flying.");
            Settings.freecamSpeed = Mathf.Round(Slider(list, "Flying speed: " + Settings.freecamSpeed.ToString("0") + " cells/s", Settings.freecamSpeed, 2f, 60f,
                "How fast the camera flies, in cells a second (default " + Settings.DefaultFreecamSpeed.ToString("0") + "). Not affected by the game's speed."));
            Settings.freecamScrollSpeed = Mathf.Round(Slider(list, "Scroll height speed: " + Settings.freecamScrollSpeed.ToString("0.00") + "x", Settings.freecamScrollSpeed, 0.1f, 2f,
                "How far one notch of the scroll wheel raises or lowers the camera, on top of the flying speed (default " + Settings.DefaultFreecamScrollSpeed.ToString("0.00") + "x).") * 20f) / 20f;
            Settings.freecamFov = Mathf.Round(Slider(list, "Field of view: " + Settings.freecamFov.ToString("0") + "°", Settings.freecamFov, 40f, 110f,
                "How wide the camera sees, in degrees from the top of the screen to the bottom (default " + Settings.DefaultFreecamFov.ToString("0") + "°)."));
#if FACE_TUNING
            // The face tuning window (FaceTuning.cs), opened over the map with the options closed so the face can be seen.
            if (Current.ProgramState == ProgramState.Playing && list.ButtonText("Face tuning window (temporary)"))
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
#endif
            shownHeight = Mathf.Max(tallest, list.CurHeight) + 12f;
            list.End();
            Widgets.EndScrollView();
        }
    }
}
