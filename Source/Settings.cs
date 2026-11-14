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
        public bool talkingMouths = true;   // a talking pawn's mouth opens and shuts on syllables (FaceLayers.TalkingMouths)
        public bool prostheticEyes = true;   // and a bionic or archotech eye's iris; off: the pawn's own eye (WalkFigures.ProstheticEyesShow)
        public bool archotechProsthetics = true;   // and an archotech one too; off: as flesh (WalkFigures.ArchotechShows)
        public bool visibleWounds = true;   // injuries, blood, bandages and scars are drawn on the pawn (WalkFigures.VisibleWounds)
        public bool outlines = true;   // the chibi is drawn with its dark outline (SkinnedFigure.Outlines)
        public bool silhouetteOutlines = true;   // only round the figure, never over its own body (SkinnedFigure.Silhouette); off: as IsoRim draws its figures' outlines
        public bool firstPersonEye = true;   // IsoRim's first person looks out from the chibi's eyes (WalkFigures.FirstPersonEye); off: IsoRim's eye height option
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
        public bool freecamMenuFreesPointer = false;   // a menu of orders opened while looking frees the pointer, rather than being pinned in the view
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
            Scribe_Values.Look(ref talkingMouths, "talkingMouths", true);
            Scribe_Values.Look(ref outlines, "outlines", true);
            Scribe_Values.Look(ref silhouetteOutlines, "silhouetteOutlines", true);
            Scribe_Values.Look(ref clothesOcclusion, "clothesOcclusion", true);
            Scribe_Values.Look(ref fewerPoses, "fewerPoses", false);
            Scribe_Values.Look(ref gpuSkinning, "gpuSkinning", true);
            Scribe_Values.Look(ref firstPersonEye, "firstPersonEye", true);
            Scribe_Values.Look(ref hairDetail, "hairDetail", DefaultHairDetail);
            Scribe_Values.Look(ref freecamOn, "freecamOn", false);
            Scribe_Values.Look(ref freecam3D, "freecam3D", false);
            Scribe_Values.Look(ref freecamClickOffLooks, "freecamClickOffLooks", false);
            Scribe_Values.Look(ref freecamPointerBack, "freecamPointerBack", true);
            Scribe_Values.Look(ref freecamMenuFreesPointer, "freecamMenuFreesPointer", false);
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

        private static string BeardName(BeardMode mode)
        {
            return mode == BeardMode.Flat ? "2D" : mode == BeardMode.ThreeD ? "3D" : "M3AT_BeardsOff".Translate();
        }

        // How pawns are built: size, head, build, hair, beard and eyes.
        private static void PawnsSection(Listing_Standard list)
        {
            Header(list, "M3AT_Pawns".Translate());
            Settings.pawnSize = Mathf.Round(Slider(list, "M3AT_PawnSize".Translate(), Settings.pawnSize.ToStringPercent(), Settings.pawnSize, SizeLeast, SizeMost,
                "M3AT_PawnSizeTip".Translate()) / SizeStep) * SizeStep;
            // Shown as how much smaller the head is than the full chibi head's, measured, not the key's amount.
            float shrink = Slider(list, "M3AT_HeadShrink".Translate(), (1f - HeadSize(Settings.headShrink)).ToStringPercent(), Settings.headShrink, 0f, 1f,
                "M3AT_HeadShrinkTip".Translate());
            Settings.headShrink = Mathf.Round(shrink / ShrinkStep) * ShrinkStep;
            float spread = Slider(list, "M3AT_HeightVariety".Translate(), Settings.heightSpread.ToStringPercent("0.#"), Settings.heightSpread, 0f, 0.1f,
                "M3AT_HeightVarietyTip".Translate(Settings.DefaultHeightSpread.ToStringPercent("0.#")));
            Settings.heightSpread = Mathf.Round(spread * 200f) / 200f;
            list.CheckboxLabeled("M3AT_GenderSizes".Translate(), ref Settings.genderSizes,
                "M3AT_GenderSizesTip".Translate());
            list.CheckboxLabeled("M3AT_VariedBodies".Translate(), ref Settings.variedBodies,
                "M3AT_VariedBodiesTip".Translate());
            if (Settings.variedBodies)
            {
                BeginSubOptions(list);
                Settings.bodySpread = Mathf.Round(Slider(list, "M3AT_BodyVariety".Translate(), Settings.bodySpread.ToStringPercent(), Settings.bodySpread, 0.05f, 0.6f,
                    "M3AT_BodyVarietyTip".Translate(Settings.DefaultBodySpread.ToStringPercent())) * 20f) / 20f;
                EndSubOptions(list);
            }
            Settings.hairDetail = Mathf.Round(Slider(list, "M3AT_HairDetail".Translate(), Settings.hairDetail.ToStringPercent(), Settings.hairDetail, HairDetailLeast, 1f,
                "M3AT_HairDetailTip".Translate(Settings.DefaultHairDetail.ToStringPercent())) / HairDetailStep) * HairDetailStep;
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
            list.CheckboxLabeled("M3AT_WornGear".Translate(), ref Settings.wornGear,
                "M3AT_WornGearTip".Translate());
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

        // How the figure is drawn: its outline and IsoRim's shading.
        private static void DrawingSection(Listing_Standard list)
        {
            Header(list, "M3AT_Drawing".Translate());
            bool hadOutlines = Settings.outlines;
            list.CheckboxLabeled("M3AT_Outlines".Translate(), ref Settings.outlines,
                "M3AT_OutlinesTip".Translate());
            if (Settings.outlines)
            {
                // Ticking Outlines starts from the silhouette, whatever it was left at when they were last turned off.
                if (!hadOutlines)
                {
                    Settings.silhouetteOutlines = true;
                }
                BeginSubOptions(list);
                list.CheckboxLabeled("M3AT_SilhouetteOutlines".Translate(), ref Settings.silhouetteOutlines,
                    "M3AT_SilhouetteOutlinesTip".Translate());
                EndSubOptions(list);
            }
            list.CheckboxLabeled("M3AT_FirstPersonEye".Translate(), ref Settings.firstPersonEye,
                "M3AT_FirstPersonEyeTip".Translate());
            list.CheckboxLabeled("M3AT_ClothesOcclusion".Translate(), ref Settings.clothesOcclusion,
                "M3AT_ClothesOcclusionTip".Translate());
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
            list.CheckboxLabeled("M3AT_RetexturedWeaponModels".Translate(), ref Settings.retexturedWeaponModels,
                "M3AT_RetexturedWeaponModelsTip".Translate());
        }

        // Each weapon type's size in the hands and on the body, once the figure has loaded its types.
        private static void WeaponSizesSection(Listing_Standard list)
        {
            List<KeyValuePair<string, float>> types = WalkFigures.TypeDefaults();
            if (types.Count > 0)
            {
                Header(list, "M3AT_WeaponSizes".Translate());
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
                if (list.ButtonText("M3AT_ResetWeaponSizes".Translate()))
                {
                    Settings.weaponScales.Clear();
                }
            }
        }

        // What it costs to draw many pawns.
        private static void PerformanceSection(Listing_Standard list)
        {
            Header(list, "M3AT_Performance".Translate());
            list.CheckboxLabeled("M3AT_GpuSkinning".Translate(), ref Settings.gpuSkinning,
                "M3AT_GpuSkinningTip".Translate());
            list.CheckboxLabeled("M3AT_FewerPoses".Translate(), ref Settings.fewerPoses,
                "M3AT_FewerPosesTip".Translate());
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
            if (list.ButtonText("M3AT_RestoreDefaults".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("M3AT_RestoreDefaultsConfirm".Translate(), RestoreDefaults, true));
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
            list.CheckboxLabeled("M3AT_FreecamClickOffLooks".Translate(), ref Settings.freecamClickOffLooks,
                "M3AT_FreecamClickOffLooksTip".Translate());
            list.CheckboxLabeled("M3AT_FreecamPointerBack".Translate(), ref Settings.freecamPointerBack,
                "M3AT_FreecamPointerBackTip".Translate());
            list.CheckboxLabeled("M3AT_FreecamMenuFreesPointer".Translate(), ref Settings.freecamMenuFreesPointer,
                "M3AT_FreecamMenuFreesPointerTip".Translate());
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
