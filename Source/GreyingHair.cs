using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Hair that greys with age, drawing only (the greyingHair mod option): the figure is drawn with a copy of the pawn's
    // hair colour faded toward a grey of its own, the pawn's colour itself never changed and nothing saved. How far is
    // where the pawn is on the curve the game itself picks grey hair by at generation (PawnHairColors.HasGreyHair:
    // SmootherStep from 40 to 75 for a human's life expectancy of 80, GreyFrom/GreyFull of it), whatever the colour and
    // whenever it was set: a pawn the game made dark-haired at 60 is drawn as far grey as any other 60 year old.
    // Hair in one of the styling station's own dye colours (DyeColours) is taken as dyed and shown as chosen at any age:
    // the game keeps no mark of a dye, so it is told by the colour alone, every draw, and still holds after the game is
    // saved and loaded without this mod. The palette also holds the natural colours, so those are left out of it; a
    // colour from neither (another mod's natural shades) greys, so only known dyes are kept.
    public static partial class WalkFigures
    {
        private const float GreyFrom = 0.5f;     // of the race's life expectancy: where hair starts to grey (40 of 80)
        private const float GreyFull = 0.94f;    // and where it is all grey (75)
        private const float GreyShift = 0.06f;   // of it each way a pawn greys earlier or later than others, by its id (about 5 years)
        private const float GreyLeast = 0.65f, GreyMost = 0.85f;   // the grey each pawn's hair goes to, by its id, as the game's RandomGreyHairColor
        private const uint GreySeed = 0x6A2E51C3u;

        public static bool GreyingHair   // false: hair is drawn in the pawn's own colour at any age (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.greyingHair; }
        }

        // The colour the pawn's hair, beard, scalp, ears and tail are drawn in.
        internal static Color HairColourOf(Pawn pawn)
        {
            Color shown = pawn.story.HairColor;   // rotten or a shambler's: as the game has it
            float g = GreyingHair ? GreyOf(pawn) : 0f;
            if (g <= 0f)
            {
                return shown;
            }
            float grey = Mathf.Lerp(GreyLeast, GreyMost, (Mix((uint)pawn.thingIDNumber ^ GreySeed) >> 8) / 16777216f);
            return Color.Lerp(shown, new Color(grey, grey, grey, shown.a), g);
        }

        // How far toward grey: 0 .. 1.
        private static float GreyOf(Pawn pawn)
        {
            if (pawn.ageTracker == null || pawn.RaceProps == null || pawn.RaceProps.lifeExpectancy <= 0f || pawn.IsShambler
                || (pawn.Corpse != null && pawn.Corpse.CurRotDrawMode != RotDrawMode.Fresh))
            {
                return 0f;
            }
            if (pawn.genes != null)
            {
                List<Gene> genes = pawn.genes.GenesListForReading;
                for (int i = 0; i < genes.Count; i++)
                {
                    // Never grey by its genes, as the game has it; or its skin is its hair colour (furskin), which would part from it.
                    if (genes[i].Active && (genes[i].def.neverGrayHair || genes[i].def.skinIsHairColor))
                    {
                        return 0f;
                    }
                }
            }
            if (Dyed(pawn.story.HairColor))
            {
                return 0f;
            }
            float life = pawn.RaceProps.lifeExpectancy;
            float shift = ((Mix((uint)pawn.thingIDNumber ^ GreySeed ^ 0x9E3779B9u) >> 8) / 16777216f * 2f - 1f) * GreyShift * life;
            return GenMath.SmootherStep(GreyFrom * life + shift, GreyFull * life + shift, pawn.ageTracker.AgeBiologicalYearsFloat);
        }

        // Whether a hair colour is one of the styling station's dyes and not a natural one.
        private static bool Dyed(Color colour)
        {
            return DyeColours().Contains(Key(colour));
        }

        // The styling station's palette (Dialog_StylingStation.AllHairColors: every ColorDef shown there), less the colours
        // hair has naturally: the hair ColorDefs, every gene's hair colour and those PawnHairColors picks at generation
        // (Black, Burnt Umber, Reddish Brown, and the Red, Pink and Light Purple of genes are in both). As 8 bit colours, as
        // the defs give them, made once.
        private static HashSet<int> DyeColours()
        {
            if (dyeColours != null)
            {
                return dyeColours;
            }
            HashSet<int> natural = new HashSet<int>
            {
                Key(PawnHairColors.DarkBlack), Key(PawnHairColors.MidBlack), Key(PawnHairColors.DarkReddish), Key(PawnHairColors.DarkSaturatedReddish),
                Key(PawnHairColors.DarkBrown), Key(PawnHairColors.ReddishBrown), Key(PawnHairColors.SandyBlonde), Key(PawnHairColors.Blonde),
            };
            foreach (ColorDef def in DefDatabase<ColorDef>.AllDefsListForReading)
            {
                if (def.colorType == ColorType.Hair)
                {
                    natural.Add(Key(def.color));
                }
            }
            foreach (GeneDef def in DefDatabase<GeneDef>.AllDefsListForReading)
            {
                if (def.hairColorOverride.HasValue)
                {
                    natural.Add(Key(def.hairColorOverride.Value));
                }
            }
            HashSet<int> dyes = new HashSet<int>();
            foreach (ColorDef def in DefDatabase<ColorDef>.AllDefsListForReading)
            {
                int key = Key(def.color);
                if (def.displayInStylingStationUI && def.colorType != ColorType.Hair && !natural.Contains(key))
                {
                    dyes.Add(key);
                }
            }
            dyeColours = dyes;
            return dyes;
        }
        private static HashSet<int> dyeColours;

        private static int Key(Color c)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f) << 16 | Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f) << 8 | Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f);
        }

#if AUTOTEST
        internal static string AutoGrey(Pawn pawn)
        {
            return string.Format("grey {0:0.00}, drawn {1}, {2} dye colours", GreyOf(pawn), HairColourOf(pawn), DyeColours().Count);
        }
#endif
    }
}
