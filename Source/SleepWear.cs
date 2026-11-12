using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // What a pawn lying in a bed is drawn wearing (the sleepWear option): as it is (Blankets, under IsoRim's blanket where the
    // bed shows only the head, else on top; Dressed, always on top), its clothes without its armour, headgear, packs, belts
    // and coats (NoEquipment), pajamas (the kid romper's garment in a pastel colour, a baby its romper) or nothing (Nothing; a baby keeps its romper,
    // which would otherwise come and go each nap).
    // Only drawn: the game is not given or told anything, so the pawn wakes up wearing what it went to bed in.
    public static partial class WalkFigures
    {
        public static SleepWear SleepWearSetting   // the option as set; Blankets with no settings yet
        {
            get { return SettingsMod.Settings == null ? SleepWear.Blankets : SettingsMod.Settings.sleepWear; }
        }

        // What the pawn is drawn wearing now: the option while it lies in a bed (any bed: a sleeping spot, a bedroll, a
        // hospital bed, so a patient too), else Blankets, which leaves its clothes as they are.
        private static SleepWear SleepWearOf(Pawn pawn)
        {
            SleepWear wear = SleepWearSetting;
            if (wear == SleepWear.Blankets || wear == SleepWear.Dressed || pawn.Dead || !pawn.Spawned || !pawn.InBed())
            {
                return SleepWear.Blankets;
            }
            return wear;
        }

        // The apparel to draw for the pawn (null for none), in the game's order: none in a vat (unless the vatClothes
        // option), none in pajamas or with nothing on in bed, and only the clothes kept on for sleeping (KeptOnInBed) with NoEquipment.
        private static readonly List<Apparel> keptOn = new List<Apparel>();
        private static List<Apparel> DressedIn(Pawn pawn, bool inVat, SleepWear sleep)
        {
            if (pawn.apparel == null || (inVat && !VatClothes) || sleep == SleepWear.Pajamas || sleep == SleepWear.Nothing)
            {
                return null;
            }
            // (a pawn dressing: with what it puts on once that is on the body, without what it takes off once that is off)
            List<Apparel> all = DressingApparel(pawn, pawn.apparel.WornApparel);
            if (sleep != SleepWear.NoEquipment)
            {
                return all;
            }
            keptOn.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                if (KeptOnInBed(all[i].def))
                {
                    keptOn.Add(all[i]);
                }
            }
            return keptOn;
        }

        private static ThingCategoryDef armourCategory, helmetCategory;
        private static bool categoriesLooked;

        // True if the apparel stays on in bed with NoEquipment: worn on the skin or the middle layer (shirts, trousers,
        // tribalwear, a vacsuit) and not armour (the game's ApparelArmor or ArmorHeadgear categories, so power armour and
        // flak too). Headgear, coats on the shell layer alone, belts, packs, webbing and shields come off.
        private static bool KeptOnInBed(ThingDef def)
        {
            if (!categoriesLooked)
            {
                categoriesLooked = true;
                armourCategory = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("ApparelArmor");
                helmetCategory = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("ArmorHeadgear");
            }
            List<ApparelLayerDef> layers = def.apparel != null ? def.apparel.layers : null;
            if (layers == null || !(layers.Contains(ApparelLayerDefOf.OnSkin) || layers.Contains(ApparelLayerDefOf.Middle)))
            {
                return false;
            }
            return !(armourCategory != null && def.IsWithinCategory(armourCategory)) && !(helmetCategory != null && def.IsWithinCategory(helmetCategory));
        }

        // The pajamas: the export's kid romper garment (make_romper.py: a shirt and trousers in one), drawn as a stand-in
        // apparel made once and never spawned, which its layer and wounds go by, in a pastel colour per pawn (PajamaColourOf).
        // A baby wears its romper (the baby romper garment). None if the export has no kid romper.
        private const string PajamaGarment = "KidRomper";
        private static Apparel pajamaStandIn;
        private static bool pajamaLooked;
        private static SkinnedFigure pajamaSkin;
        private static int pajamaPiece = -1;

        private static int PajamaPiece()
        {
            if (pajamaSkin != skin)
            {
                pajamaSkin = skin;
                pajamaPiece = -1;
                for (int g = 1; skin != null && g < skin.Pieces; g++)
                {
                    if (skin.PieceName(g) == PajamaGarment)
                    {
                        pajamaPiece = g;
                    }
                }
                if (skin != null && pajamaPiece < 0)
                {
                    Log.Warning(Tag + "the export has no " + PajamaGarment + " garment, so pawns in bed in pajamas are drawn without them.");
                }
            }
            return pajamaPiece;
        }

        // The stand-in apparel the pajamas are drawn as on this pawn, or null.
        private static Apparel PajamasOf(Pawn pawn)
        {
            if (Baby(pawn))
            {
                return BabyRomperStandIn();
            }
            if (PajamaPiece() < 0)
            {
                return null;
            }
            if (!pajamaLooked)
            {
                pajamaLooked = true;
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_KidRomper")
                    ?? DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_BasicShirt");
                if (def != null && def.IsApparel)
                {
                    pajamaStandIn = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)) as Apparel;
                }
                if (pajamaStandIn == null)
                {
                    Log.Warning(Tag + "no apparel to draw pajamas as; pawns in bed are drawn without them.");
                }
            }
            return pajamaStandIn;
        }

        private static bool IsPajamas(Apparel a)
        {
            return a != null && a == pajamaStandIn;
        }

        // Soft pastels, one per pawn by its id, so a colony's pajamas differ but each pawn keeps its own.
        private static readonly Color[] PajamaColours =
        {
            new Color(0.62f, 0.78f, 0.95f),   // sky blue
            new Color(0.96f, 0.70f, 0.78f),   // pink
            new Color(0.66f, 0.90f, 0.76f),   // mint
            new Color(0.78f, 0.70f, 0.94f),   // lavender
            new Color(0.97f, 0.90f, 0.62f),   // butter yellow
            new Color(0.95f, 0.78f, 0.62f),   // peach
            new Color(0.80f, 0.84f, 0.90f),   // pale grey-blue
        };

        private static Color PajamaColourOf(Pawn pawn)
        {
            return PajamaColours[Mix((uint)pawn.thingIDNumber ^ 0x5eed51u) % (uint)PajamaColours.Length];
        }
    }
}
