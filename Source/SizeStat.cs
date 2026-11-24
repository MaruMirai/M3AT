using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The chibi's size on the pawn's info card (the Stats tab, under the pawn's basics): how big it is drawn against a
    // grown pawn of the standard size at the options' pawn height, and how tall that is in cells, the options' pawn
    // height included, with what goes into it in the entry's explanation. Drawing only: the game's own body size is
    // untouched, so this is its own line rather than a StatDef the game would let other things read.
    public static partial class WalkFigures
    {
        internal static void PatchSizeStat(Harmony harmony)
        {
            MethodInfo stats = AccessTools.Method(typeof(Pawn), nameof(Pawn.SpecialDisplayStats));
            if (stats != null)
            {
                harmony.Patch(stats, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(SizeStatPostfix)));
            }
        }

        private static IEnumerable<StatDrawEntry> SizeStatPostfix(IEnumerable<StatDrawEntry> __result, Pawn __instance)
        {
            foreach (StatDrawEntry e in __result)
            {
                yield return e;
            }
            StatDrawEntry size = null;
            try
            {
                size = SizeEntry(__instance);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(Tag + "Working out the size for the info card failed: " + ex, 0x4d3a7e10);
            }
            if (size != null)
            {
                yield return size;
            }
        }

        // Only for a pawn the chibi draws: a humanlike IsoRim draws as a person (Human, CreepJoiner, or another race
        // with the otherRaces option on and no alien pack of its own).
        private static bool DrawnAsChibi(Pawn pawn)
        {
            if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.ageTracker == null)
            {
                return false;
            }
            string race = pawn.def.defName;
            if (race == "Human" || race == "CreepJoiner")
            {
                return true;
            }
            return OtherRaces && !HasAlienPack(race);
        }

        private static StatDrawEntry SizeEntry(Pawn pawn)
        {
            if (!DrawnAsChibi(pawn))
            {
                return null;
            }
            float scale = SizeOf(pawn);                  // cells per model unit
            float standard = ScaleUp * PawnSize;         // the same for a grown pawn of the standard size at the options' height
            float tall = StandardHeight / ScaleUp;       // the figure's standing height, model units
            float share = standard > 0f ? scale / standard : 1f;
            float cells = scale * tall;
            string value = "M3AT_SizeValue".Translate(share.ToStringPercent(), cells.ToString("0.00"));

            float grown = GrownAge(pawn);
            float growth = grown > 0f
                ? Mathf.Lerp(BirthSize, 1f, Growth.Evaluate(Mathf.Clamp01(pawn.ageTracker.AgeBiologicalYearsFloat / grown)))
                : Api.FigureSizeFor(pawn.ageTracker.CurLifeStage != null ? pawn.ageTracker.CurLifeStage.bodySizeFactor : 1f);
            System.Text.StringBuilder report = new System.Text.StringBuilder();
            report.AppendLine("M3AT_SizeDesc".Translate());
            report.AppendLine();
            report.AppendLine("M3AT_SizeStandard".Translate((StandardHeight * PawnSize).ToString("0.00")));
            report.AppendLine("M3AT_SizeGrowth".Translate(growth.ToStringPercent()));
            report.AppendLine("M3AT_SizeGender".Translate(GenderSize(pawn).ToStringPercent()));
            report.AppendLine("M3AT_SizeOwnHeight".Translate(HeightSize(pawn).ToStringPercent()));
            report.AppendLine("M3AT_SizeOldAge".Translate(AgeSize(pawn).ToStringPercent()));
            report.AppendLine("M3AT_SizeArmour".Translate(ArmourSizeOf(pawn).ToStringPercent()));
            report.AppendLine();
            report.Append("M3AT_SizeTotal".Translate(share.ToStringPercent(), cells.ToString("0.00")));
            return new StatDrawEntry(StatCategoryDefOf.BasicsPawn, "M3AT_SizeLabel".Translate(), value, report.ToString(), 4990);
        }
    }
}
