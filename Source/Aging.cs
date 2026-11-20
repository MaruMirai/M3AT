using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Old age on the skinned figure (the agingLooks mod option): wrinkles on the face patch fading in by degrees (WrinklesOf,
    // drawn as the FacialRidges gene's designs are, anim_scripts/make_wrinkles.py), a stoop of the upper back with the head
    // kept up to look ahead and the knees a little bent standing (StoopOf, SkinnedFigure.Stoop), a little height lost
    // (AgeSize, in SizeOf), and the game's own diseases of old age: a bad back stoops further and frailty a little, at
    // any age, and a cataract or a blind eye is clouded over Facial Animation's iris (CataractEyes, Wounds.cs).
    // All by the race's life expectancy, so a longer-lived race ages later; by biological age, so a gene that slows ageing
    // slows it too. Drawing only.
    public static partial class WalkFigures
    {
        private const float AgingFrom = 0.55f;   // of the race's life expectancy: where the first signs of age show (44 for a human's 80)
        private const float AgingFull = 1f;      // and where they are all there (80)
        private const float WrinklesFull = 0.85f;   // of the way from AgingFrom to AgingFull: where the wrinkles are all in (about 72 for a human)
        private const int WrinkleLevels = 8;   // ridges_age1 .. 8 (make_wrinkles.py's LEVELS), evenly from none to all
        private const float StoopFrom = 0.35f;   // of that way: where the back begins to stoop (about 57)
        private const float StoopMost = 12f;     // degrees the upper back is bent forward at full age (SkinnedFigure.Stoop)
        private const float BadBackStoop = 14f;  // and more with the game's bad back, at any age
        private const float FrailStoop = 5f;     // and with frailty
        private const float StoopMostOfAll = 28f;   // degrees: the most of all of them together
        private const float StoopAiming = 0.25f;   // of the stoop kept while aiming, as the trunk's bend points the weapon down with it
        private const float StoopDrafted = 0.5f;   // and in a drafted stance
        private const float StoopSeated = 0.6f;    // and seated, as the sit's own lean already bends the trunk over the lap
        private const float AgeKnees = 0.08f;    // of a full crouch (SkinnedFigure.Crouch) the knees give standing still, at full stoop
        private const float AgeShrink = 0.025f;  // of its size a pawn loses by full age, as people lose 2 to 3% of their height by 80

        public static bool AgingLooks   // false: old pawns are drawn as young ones (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.agingLooks; }
        }

        // How old the pawn looks, 0 (not yet) .. 1 (fully), eased at both ends; 0 for a race with no life expectancy.
        private static float AgeOf(Pawn pawn)
        {
            if (!AgingLooks || pawn.ageTracker == null || pawn.RaceProps == null || pawn.RaceProps.lifeExpectancy <= 0f)
            {
                return 0f;
            }
            float life = pawn.RaceProps.lifeExpectancy;
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(AgingFrom * life, AgingFull * life, pawn.ageTracker.AgeBiologicalYearsFloat));
        }

        // The wrinkles' design (ridges_age<level>.png), the nearest level to the pawn's age, so they fade in and deepen by
        // degrees as it ages; null while too young for any.
        private static readonly string[] WrinkleDesigns = { null, "age1", "age2", "age3", "age4", "age5", "age6", "age7", "age8" };
        private static string WrinklesOf(Pawn pawn)
        {
            int level = Mathf.RoundToInt(Mathf.Clamp01(AgeOf(pawn) / WrinklesFull) * WrinkleLevels);
            return WrinkleDesigns[level];
        }

        // Of its size by age alone: a little less as it grows old.
        private static float AgeSize(Pawn pawn)
        {
            return 1f - AgeShrink * AgeOf(pawn);
        }

        // Degrees the upper back is bent forward, by age and the game's bad back and frailty.
        private static float StoopOf(Pawn pawn)
        {
            if (!AgingLooks)
            {
                return 0f;
            }
            float stoop = StoopMost * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(StoopFrom, 1f, AgeOf(pawn)));
            if (pawn.health != null)
            {
                LookUpAgeDefs();
                HediffSet set = pawn.health.hediffSet;
                if (badBack != null && set.HasHediff(badBack))
                {
                    stoop += BadBackStoop;
                }
                if (frail != null && set.HasHediff(frail))
                {
                    stoop += FrailStoop;
                }
            }
            return Mathf.Min(stoop, StoopMostOfAll);
        }

        // Each eye clouded by a cataract or gone blind (not a prosthetic: that replaces the eye and its disease), as its
        // side ("L" or "R", in bone) and the hediff's load ID; and on an old pawn every other eye of its own a faint haze
        // (weak, its seed the level, HazeOf), as the lens yellows and clouds with age before any cataract. Drawn by Wounds'
        // prosthetic eyes, recolouring the eye's own iris.
        internal static void CataractEyes(Pawn pawn, List<LimbDecal> into)
        {
            if (!AgingLooks)
            {
                return;
            }
            LookUpAgeDefs();
            int from = into.Count;
            HediffSet set = pawn.health.hediffSet;
            List<Hediff> hediffs = set.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                if ((h.def != cataract && h.def != blindness) || h.def == null || h.Part == null || h.Part.def.defName != "Eye"
                    || OnProsthetic(set, h.Part))
                {
                    continue;
                }
                string side = SideOf(h.Part) ?? "R";
                if (into.FindIndex(from, d => d.bone == side) < 0)   // one cloud an eye
                {
                    into.Add(new LimbDecal { bone = side, seed = h.loadID });
                }
            }
            int haze = HazeOf(pawn);
            if (haze == 0)
            {
                return;
            }
            foreach (BodyPartRecord part in pawn.RaceProps.body.AllParts)
            {
                if (part.def.defName != "Eye" || set.PartIsMissing(part) || OnProsthetic(set, part))
                {
                    continue;
                }
                string side = SideOf(part) ?? "R";
                if (into.FindIndex(from, d => d.bone == side) < 0)
                {
                    into.Add(new LimbDecal { bone = side, seed = haze, weak = true });
                }
            }
        }

        private const float HazeFrom = 0.4f;   // of the way from AgingFrom to AgingFull (AgeOf): where age's haze on the eyes begins (about 62 for a human)
        internal const int HazeLevels = 4;     // its steps, from none to all at AgingFull (80)

        // Age's haze on the eyes, 0 (none) to HazeLevels.
        private static int HazeOf(Pawn pawn)
        {
            return Mathf.CeilToInt(Mathf.InverseLerp(HazeFrom, 1f, AgeOf(pawn)) * HazeLevels - 0.001f);
        }

        // The autotest's: what age shows on the pawn.
        internal static string AutoAging(Pawn pawn)
        {
            return string.Format("age {0:0.00}, wrinkles {1}, stoop {2:0.0} degrees, size {3:0.000}, eye haze {4}/{5}", AgeOf(pawn), WrinklesOf(pawn) ?? "none",
                StoopOf(pawn), AgeSize(pawn), HazeOf(pawn), HazeLevels);
        }

        private static void LookUpAgeDefs()
        {
            if (ageDefsLooked)
            {
                return;
            }
            ageDefsLooked = true;
            badBack = DefDatabase<HediffDef>.GetNamedSilentFail("BadBack");
            frail = DefDatabase<HediffDef>.GetNamedSilentFail("Frail");
            cataract = DefDatabase<HediffDef>.GetNamedSilentFail("Cataract");
            blindness = DefDatabase<HediffDef>.GetNamedSilentFail("Blindness");
        }
        private static bool ageDefsLooked;
        private static HediffDef badBack, frail, cataract, blindness;
    }
}
