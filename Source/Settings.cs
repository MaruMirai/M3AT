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

    // The player's choices, saved with the game's mod settings (Options > Mod settings > M3AT).
    public class Settings : ModSettings
    {
        public const float DefaultShrink = 0f;   // the head shrink the options start at: the full chibi head
        public const float DefaultHeightSpread = 0.02f, DefaultBodySpread = 0.3f;   // the variety sliders' defaults
        public const float DefaultHairDetail = 0.3f;
        public const float DefaultGazeStrength = 1f;   // how far the eyes look about, of Facial Animation's own   // the share of its triangles a hairstyle keeps
        public float pawnSize = 1f;   // every pawn's size, of the standard (WalkFigures.PawnSize, in SizeOf)
        public float headShrink = DefaultShrink;   // 0 .. 1 of the Shrinkhead key: a smaller, less chibi head (skinned figure only)
        public bool genderSizes = true;   // grown males are drawn bigger than grown females, by WalkFigures.GenderGap (WalkFigures.GenderSize)
        public float heightSpread = DefaultHeightSpread;   // the standard deviation of pawns' own heights about their gender's, of the standard size (WalkFigures.HeightSize)
        public bool variedBodies = true;    // each pawn's build spread about its body type's by the Thin, Fat and Hulk keys (WalkFigures.VariedBuild)
        public float bodySpread = DefaultBodySpread;   // and the standard deviation of that spread, in key amounts
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
        public bool autoGrips = true;   // another mod's weapon held by its grip as found in its art (WalkFigures.AutoGrips, GripFinder)
        public bool retexturedWeaponModels = true;   // the game's own weapons keep IsoRim's model though another mod redraws their art (WalkFigures.RetexturedWeaponModels)
        public bool vatClothes = false;    // a pawn in a growth vat or biosculpter pod drawn in its clothes (WalkFigures.VatClothes); off: without them
        public bool bedBlankets = true;   // IsoRim's blanket over the body in a bed that shows only the head (WalkFigures.BedBlankets); off: lying on top
        public SleepWear sleepWear = SleepWear.NoEquipment;   // what a pawn in bed is drawn wearing (WalkFigures.SleepWearOf)
        public bool groundGarments = false;   // apparel lying on the map is drawn as the chibi's own garment for it (WalkFigures.GroundGarments); off: IsoRim's look
        public bool injuryAnims = true;   // pawns limp on a bad leg and hold where they are hurt (WalkFigures.InjuryAnims); a missing leg hops regardless
        public bool prosthetics = true;   // a replaced limb is drawn as its prosthetic (WalkFigures.Prosthetics)
        public float gazeStrength = DefaultGazeStrength;   // how far Facial Animation's eyes move on the chibi (FaceLayers.GazeStrength)
        public bool prostheticEyes = true;   // and a bionic or archotech eye's iris; off: the pawn's own eye (WalkFigures.ProstheticEyesShow)
        public bool archotechProsthetics = true;   // and an archotech one too; off: as flesh (WalkFigures.ArchotechShows)
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
        public bool freecamPointerBack = true;   // the pointer freed for pointer mode goes back where it was when looking began
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
            Scribe_Values.Look(ref variedBodies, "variedBodies", true);
            Scribe_Values.Look(ref bodySpread, "bodySpread", DefaultBodySpread);
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
            Scribe_Values.Look(ref retexturedWeaponModels, "retexturedWeaponModels", true);
            Scribe_Values.Look(ref autoGrips, "autoGrips", true);
            Scribe_Values.Look(ref groundGarments, "groundGarments", false);
            Scribe_Values.Look(ref bedBlankets, "bedBlankets", true);
            Scribe_Values.Look(ref sleepWear, "bedClothes", SleepWear.NoEquipment);
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
            Scribe_Values.Look(ref outlines, "outlines", true);
            Scribe_Values.Look(ref silhouetteOutlines, "silhouetteOutlines", true);
            Scribe_Values.Look(ref clothesOcclusion, "clothesOcclusion", true);
            Scribe_Values.Look(ref fewerPoses, "fewerPoses", false);
            Scribe_Values.Look(ref gpuSkinning, "gpuSkinning", true);
            Scribe_Values.Look(ref hairDetail, "hairDetail", DefaultHairDetail);
            Scribe_Values.Look(ref freecamOn, "freecamOn", false);
            Scribe_Values.Look(ref freecam3D, "freecam3D", false);
            Scribe_Values.Look(ref freecamClickOffLooks, "freecamClickOffLooks", false);
            Scribe_Values.Look(ref freecamPointerBack, "freecamPointerBack", true);
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
        private const float GazeMost = 4f, GazeStep = 0.25f;   // the eye movement slider's top and steps

        private static float HeadSize(float shrink)
        {
            return 1f - (1f - ShrunkSize) * shrink;
        }

        public SettingsMod(ModContentPack content) : base(content)
        {
            LoadTrail.Start(new HarmonyLib.Harmony("MaruMirai.M3AT.trail"));   // first, so the whole load is in it
            Settings = GetSettings<Settings>();
            HairDetail = Settings.hairDetail;
            groundGarmentsBaked = Settings.groundGarments;
            RestoredModels.Patch();   // before IsoRim's startup binds its models
            weaponModelsBaked = Settings.retexturedWeaponModels;
        }

        // The options' sections, in the order they are read: down the first column, then the second, then the third. Where
        // the columns break is worked out from how tall each section came out (Columns), so they come out even whatever the
        // window's width or the wrapping of the text.
        private static readonly System.Action<Listing_Standard>[] Sections =
        {
            PawnsSection, ClothesSection, InjuriesSection, DrawingSection, CombatSection, WeaponSizesSection,
            PerformanceSection, ProblemsSection, FreecamSection,
        };
        private const int ColumnCount = 3;
        private const float SectionGap = 12f;   // px between two sections in a column, as the listing's own Gap

        // Each section's height, px, as last drawn, and the section each column after the first starts at.
        private static readonly float[] sectionHeights = new float[Sections.Length];
        private static int[] columnStarts;
        // The columns are worked out again only as the window opens (or its width changes), not while it is open, so a
        // section doesn't jump to another column under the pointer when a checkbox shows or hides the options under it.
        private static bool relayout = true;
        private static float laidOutWidth = -1f;
        private static bool measured;   // every section has been drawn once, so sectionHeights holds them all

        // The breaks (the sections kept in order) that make the tallest column shortest. Every pair of breaks is tried:
        // there are only a few dozen.
        private static int[] Columns()
        {
            int n = Sections.Length;
            int[] best = null;
            float bestTallest = float.MaxValue;
            for (int a = 1; a < n - 1; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    float tallest = Mathf.Max(ColumnHeight(0, a), Mathf.Max(ColumnHeight(a, b), ColumnHeight(b, n)));
                    if (tallest < bestTallest - 0.5f)
                    {
                        bestTallest = tallest;
                        best = new[] { a, b };
                    }
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
        private static void RestoreDefaults()
        {
            Settings fresh = new Settings();
            System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (System.Reflection.FieldInfo field in typeof(Settings).GetFields(fields))
            {
                if (field.IsInitOnly || field.Name == nameof(Settings.freecamOn) || field.Name == nameof(Settings.freecam3D))
                {
                    continue;
                }
                field.SetValue(Settings, field.GetValue(fresh));
            }
            Messages.Message("M3AT's options are back to their defaults.", RimWorld.MessageTypeDefOf.NeutralEvent, false);
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
        // long name ("Rifles and launchers") wrapped the value onto a second line the row's height left out of sight.
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

        private static string SleepName(SleepWear mode)
        {
            return mode == SleepWear.Dressed ? "Normal clothes" : mode == SleepWear.NoEquipment ? "No equipment"
                : mode == SleepWear.Pajamas ? "Pajamas" : "No clothes";
        }

        private static string BeardName(BeardMode mode)
        {
            return mode == BeardMode.Flat ? "2D" : mode == BeardMode.ThreeD ? "3D" : "Off";
        }

        // How pawns are built: size, head, build, hair, beard and eyes.
        private static void PawnsSection(Listing_Standard list)
        {
            Header(list, "Pawns");
            Settings.pawnSize = Mathf.Round(Slider(list, "Pawn size", Settings.pawnSize.ToStringPercent(), Settings.pawnSize, SizeLeast, SizeMost,
                "How big every pawn is drawn. 100% (default) is the usual size; children, gender and height variety are on top of it. It changes only how pawns look, not the space they take up.") / SizeStep) * SizeStep;
            // Shown as how much smaller the head is than the full chibi head's, measured, not the key's amount.
            float shrink = Slider(list, "Head shrink", (1f - HeadSize(Settings.headShrink)).ToStringPercent(), Settings.headShrink, 0f, 1f,
                "How much smaller the head is drawn than the default chibi. Left: default; right: smaller. No effect on vanilla or CE hit calculations.");
            Settings.headShrink = Mathf.Round(shrink / ShrinkStep) * ShrinkStep;
            float spread = Slider(list, "Height variety", Settings.heightSpread.ToStringPercent("0.#"), Settings.heightSpread, 0f, 0.1f,
                "How much pawns' heights vary (default " + Settings.DefaultHeightSpread.ToStringPercent("0.#") + "). A normal distribution is used, with most near the usual size and a few further off. 0% makes everyone of a gender the same size. Each pawn's height is derived from the pawn's id, so no save data is needed.");
            Settings.heightSpread = Mathf.Round(spread * 200f) / 200f;
            list.CheckboxLabeled("Gender size difference", ref Settings.genderSizes,
                "Renders men and women with a 10% size difference eased in over teenage years. Off: they are the same size.");
            list.CheckboxLabeled("Varied bodies", ref Settings.variedBodies,
                "Each pawn gets a build of its own, somewhere around its body type: some thinner or heavier, some more muscular, so not every average body looks the same. Thin, fat and hulk pawns are still based on those bodies. Off: every pawn of a body type has the same body.");
            if (Settings.variedBodies)
            {
                BeginSubOptions(list);
                Settings.bodySpread = Mathf.Round(Slider(list, "Body variety", Settings.bodySpread.ToStringPercent(), Settings.bodySpread, 0.05f, 0.6f,
                    "How far pawns' builds wander from their body type (default " + Settings.DefaultBodySpread.ToStringPercent() + "). Most stay near it, a few go most of the way to the next type. Each pawn's build is derived from the pawn's id, so no save data is needed.") * 20f) / 20f;
                EndSubOptions(list);
            }
            Settings.hairDetail = Mathf.Round(Slider(list, "Hair detail", Settings.hairDetail.ToStringPercent(), Settings.hairDetail, HairDetailLeast, 1f,
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
            Settings.gazeStrength = Mathf.Round(Slider(list, "Eye movement", Settings.gazeStrength.ToStringPercent(), Settings.gazeStrength, 0f, GazeMost,
                "How far pawns' eyes glance and look about with Facial Animation, against how far they move on its 2D faces (default " + Settings.DefaultGazeStrength.ToStringPercent() + "). Its movements are small, a pixel or so, so higher makes them easier to see on the bigger 3D faces. 0%: the eyes stay still. Brows, eyelids and mouths move as Facial Animation moves them, whatever this is.") / GazeStep) * GazeStep;
        }

        // What pawns are drawn wearing, and what their clothes hide.
        private static void ClothesSection(Listing_Standard list)
        {
            Header(list, "Clothes and gear");
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
            list.CheckboxLabeled("Blankets in bed", ref Settings.bedBlankets,
                "In beds that show only the sleeper's head, IsoRim's blanket is drawn over the body, raised by it. Other beds, such as sleeping spots and bedrolls, have no blanket. Off: pawns lie on top of every bed.");
            if (list.ButtonTextLabeledPct("Clothes in bed", SleepName(Settings.sleepWear), SliderLabelShare, TextAnchor.MiddleLeft, null,
                "What pawns lying in a bed are drawn wearing, wherever their body shows (a bed with no blanket, or with blankets off). Only how they look: nothing is taken off or put on in the game.\n\nNormal clothes: what they wear.\n\nNo equipment (default): without armour, helmets and hats, coats, belts, packs or shields; shirts, trousers and the like stay on.\n\nPajamas: pastel pajamas; babies in their romper.\n\nNo clothes: no clothes; babies keep their romper."))
            {
                List<FloatMenuOption> modes = new List<FloatMenuOption>();
                foreach (SleepWear mode in new[] { SleepWear.Dressed, SleepWear.NoEquipment, SleepWear.Pajamas, SleepWear.Nothing })
                {
                    SleepWear picked = mode;
                    modes.Add(new FloatMenuOption(SleepName(mode), () => Settings.sleepWear = picked));
                }
                Find.WindowStack.Add(new FloatMenu(modes));
            }
            list.CheckboxLabeled("Clothes in vats", ref Settings.vatClothes,
                "Pawns in growth vats and biosculpter pods are drawn in their clothes. Off (default): they are drawn without them. Babies never get their romper in a vat.");
        }

        // Wounds, limps and prosthetics.
        private static void InjuriesSection(Listing_Standard list)
        {
            Header(list, "Injuries");
            list.CheckboxLabeled("Injury-reactive animations", ref Settings.injuryAnims,
                "Pawns limp on a bad leg, hold injured areas, etc. Off: they move as if unhurt. Off doesn't disable hit flinch animations, hopping for missing legs, or slowed movement.");
            list.CheckboxLabeled("Visible injuries", ref Settings.visibleWounds,
                "Wounds, blood, bandages and scars are drawn on the pawn, where the injury is. Wounds and blood are visible through some clothing; bruises, bandages and scars go only on the skin. Off: none are drawn.");
            list.CheckboxLabeled("Visible prosthetics", ref Settings.prosthetics,
                "Peg legs, wooden hands and feet, and simple, bionic and archotech arms and legs are drawn as what they are made of, where clothes leave them bare. Off: a replaced limb looks like skin.");
            if (Settings.prosthetics)
            {
                list.CheckboxLabeled("  Visible prosthetic eyes", ref Settings.prostheticEyes,
                    "Bionic and archotech eyes are drawn with a mechanical iris. Off: they look like the pawn's own eyes.");
                list.CheckboxLabeled("  Visible archotech prosthetics", ref Settings.archotechProsthetics,
                    "Archotech limbs are drawn in the olive and yellow-circuit look of archotech machinery. Off: they look like natural flesh, as the game describes them.");
            }
        }

        // How the figure is drawn: its outline and IsoRim's shading.
        private static void DrawingSection(Listing_Standard list)
        {
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
        }

        // Falls, flings and weapons.
        private static void CombatSection(Listing_Standard list)
        {
            Header(list, "Combat");
            list.CheckboxLabeled("Ragdolls", ref Settings.ragdolls,
                "Pawns knocked down or killed where they stand fall limp under their own weight and lie as they land. Off: they play a set fall instead.");
            if (Settings.ragdolls)
            {
                BeginSubOptions(list);
                Settings.flingPower = Mathf.Round(Slider(list, "Fling power", Settings.flingPower.ToStringPercent(), Settings.flingPower, FlingPowerLeast, FlingPowerMost,
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
            list.CheckboxLabeled("Find grips on other mods' weapons", ref Settings.autoGrips,
                "Guns and melee weapons from other mods are held by the grip or handle found in their art, rather than where the game's own weapon of that kind is held. Where the art doesn't show a clear grip, the usual hold is kept. Off: always the usual hold.");
            list.CheckboxLabeled("Retextured weapons as 3D models", ref Settings.retexturedWeaponModels,
                "The game's own weapons are shown as IsoRim's 3D models even when another mod (such as Combat Extended or a texture pack) gives them new art, or removes their models. The model is lined up with the new art but keeps its own shape, so a weapon redrawn very differently may not match it. Weapons from other mods are left to IsoRim. Off: IsoRim's choice, which shows a retextured weapon as its art. Needs IsoRim's \"Weapons as 3D models\" setting. Takes effect when this window closes for weapons on the ground.");
        }

        // Each weapon type's size in the hands and on the body, once the figure has loaded its types.
        private static void WeaponSizesSection(Listing_Standard list)
        {
            List<KeyValuePair<string, float>> types = WalkFigures.TypeDefaults();
            if (types.Count > 0)
            {
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
                    shown = Mathf.Round(Slider(list, name, shown.ToStringPercent(), shown, WeaponLeast, WeaponMost,
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
        }

        // What it costs to draw many pawns.
        private static void PerformanceSection(Listing_Standard list)
        {
            Header(list, "Performance");
            list.CheckboxLabeled("Animate on the graphics card", ref Settings.gpuSkinning,
                "Moves each pawn's body, clothes and hair into its pose on the graphics card, which is much faster with many pawns in view. Turn it off if pawns look broken or stretched, to animate them the old way.");
            list.CheckboxLabeled("Fewer updates with many pawns", ref Settings.fewerPoses,
                "When many pawns are in view they are animated less often, giving a stop-motion like effect. Reduces M3AT's performance overhead around 6–9%. Off: Pawn animated as normal. (Default)");
        }

        // A report for the Workshop's bug thread (BugReport.cs), and every option back to its default.
        private static void ProblemsSection(Listing_Standard list)
        {
            Header(list, "Problems");
            // Copied, and saved in case it is too long to paste.
            if (list.ButtonText("Copy bug report"))
            {
                string saved = BugReport.CopyAndSave();
                Messages.Message("Bug report copied to the clipboard" + (saved != null ? " and saved to " + saved : "") + ". Paste it into the mod's bug report thread with a few words on what went wrong.",
                    RimWorld.MessageTypeDefOf.NeutralEvent, false);
            }
            list.Label("Copies this mod's settings, errors and your mod list for a bug report, and the state of any pawns selected in the game: select the one that looks wrong first. Nothing is sent anywhere.");
            list.Gap(6f);
            if (list.ButtonText("Restore all defaults"))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("Put every M3AT option back to its default, the weapon sizes too?", RestoreDefaults, true));
            }
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
        }

        // The freecam (Freecam.cs): its keys, pointer and flying.
        private static void FreecamSection(Listing_Standard list)
        {
            Header(list, "Freecam");
            list.Label("Fly around with pan keys + mouselook, scrollwheel for altitude. Selections and orders still work with the crosshair. "
                + "Use your set toggle button, or open any menu, to switch to pointer. Press again or close the open menu to return to freelook.");
            list.Gap(6f);
            list.Label("Set keyboard keys in RimWorld's keybindings. Current settings:\n"
                + KeyName(Freecam.ToggleKey) + ": freecam on/off\n"
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
            list.CheckboxLabeled("Pointer mode returns the pointer", ref Settings.freecamPointerBack,
                "Going into pointer mode puts the pointer back where it was when looking began (default). Off: it starts in the middle of the screen, under the cross.");
            Settings.freecamSensitivity = Mathf.Round(Slider(list, "Look speed", Settings.freecamSensitivity.ToString("0.00"), Settings.freecamSensitivity, 0.02f, 0.6f,
                "How far the view turns for the mouse's movement, in degrees per pixel (default " + Settings.DefaultFreecamSensitivity.ToString("0.00") + ").") * 100f) / 100f;
            list.CheckboxLabeled("Invert mouse look", ref Settings.freecamInvertY, "Moving the mouse up looks down.");
            list.CheckboxLabeled("Use IsoRim's wall hiding", ref Settings.freecamCutaway,
                "While flying, walls are cut down as IsoRim's own settings say: around the pointer (the middle of the screen while looking), near colonists and in front of rooms. Off (default): every wall stands at full height, as in IsoRim's first-person view.");
            list.CheckboxLabeled("Scroll wheel flies up and down", ref Settings.freecamScrollFlies,
                "Scrolling up raises the camera and scrolling down lowers it, further at a higher flying speed. Off: the scroll wheel does nothing while flying.");
            Settings.freecamSpeed = Mathf.Round(Slider(list, "Flying speed", Settings.freecamSpeed.ToString("0") + " cells/s", Settings.freecamSpeed, 2f, 60f,
                "How fast the camera flies, in cells a second (default " + Settings.DefaultFreecamSpeed.ToString("0") + "). Not affected by the game's speed."));
            Settings.freecamScrollSpeed = Mathf.Round(Slider(list, "Scroll height speed", Settings.freecamScrollSpeed.ToString("0.00") + "x", Settings.freecamScrollSpeed, 0.1f, 2f,
                "How far one notch of the scroll wheel raises or lowers the camera, on top of the flying speed (default " + Settings.DefaultFreecamScrollSpeed.ToString("0.00") + "x).") * 20f) / 20f;
            Settings.freecamFov = Mathf.Round(Slider(list, "Field of view", Settings.freecamFov.ToString("0") + "°", Settings.freecamFov, 40f, 110f,
                "How wide the camera sees, in degrees from the top of the screen to the bottom (default " + Settings.DefaultFreecamFov.ToString("0") + "°)."));
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Rect view = new Rect(0f, 0f, inRect.width - 16f, Mathf.Max(shownHeight, inRect.height - 4f));   // 16 px for the scroll bar
            if (view.width != laidOutWidth)
            {
                laidOutWidth = view.width;
                relayout = true;
            }
            // Until the sections have been drawn once there is nothing to go by, so the first draw takes a guess and the
            // next lays them out.
            if (columnStarts == null || (relayout && measured))
            {
                columnStarts = measured ? Columns() : new[] { 4, 7 };
                relayout = !measured;
            }
            Widgets.BeginScrollView(inRect, ref scroll, view);
            // The columns are started by hand: the listing's rect is far taller than any of them, so it never starts one
            // itself, and the tallest is measured before each new one so the scroll view knows how far to go.
            Listing_Standard list = new Listing_Standard { ColumnWidth = (view.width - (ColumnCount - 1) * Listing.ColumnSpacing) / ColumnCount };
            list.Begin(new Rect(view.x, view.y, view.width, 100000f));
            float tallest = 0f;
            int column = 0;
            for (int i = 0; i < Sections.Length; i++)
            {
                if (column < columnStarts.Length && i == columnStarts[column])
                {
                    tallest = Mathf.Max(tallest, list.CurHeight);
                    list.NewColumn();
                    column++;
                }
                else if (i > 0)
                {
                    list.Gap(SectionGap);
                }
                float top = list.CurHeight;
                Sections[i](list);
                sectionHeights[i] = list.CurHeight - top;
            }
            measured = true;
            shownHeight = Mathf.Max(tallest, list.CurHeight) + 12f;
            list.End();
            Widgets.EndScrollView();
        }
    }
}
