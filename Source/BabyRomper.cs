using System.Collections.Generic;
using RimWorld;
using Verse;

namespace M3AT
{
    // The baby romper (the export's baby_romper garment, make_baby_romper.py): babies in the game go naked, so a baby wearing
    // nothing over its trunk or legs is drawn in a sleeveless, short-legged romper once it is RomperHours old, by then
    // dressed by whoever looks after it. Only drawn: the game is not given any apparel. Its age is all it goes by, so
    // nothing is looked at on the clock or kept with the save.
    public static partial class WalkFigures
    {
        private const int RomperHours = 4;   // game hours old a baby is first drawn in the romper (newborns go naked)

        // The apparel the romper is drawn as (worn[g]): what its layer, colour and wounds go by. A kid romper if Biotech
        // has one, else any apparel worn on the skin over the torso; made once, never spawned or worn by anyone.
        private static Apparel babyRomperStandIn;
        private static bool babyRomperLooked;

        private static Apparel BabyRomperStandIn()
        {
            if (!babyRomperLooked)
            {
                babyRomperLooked = true;
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_KidRomper")
                    ?? DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_BasicShirt");
                if (def != null && def.IsApparel)
                {
                    babyRomperStandIn = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)) as Apparel;
                }
                if (babyRomperStandIn == null)
                {
                    Log.Warning(Tag + "no apparel to draw the baby romper as; babies are drawn as the game has them.");
                }
            }
            return babyRomperStandIn;
        }

        // The stand-in for the baby romper if this pawn is drawn in it (a baby RomperHours old or more, nothing in all over
        // its trunk or legs), else null.
        private static Apparel BabyRomperOf(Pawn pawn, List<Apparel> all)
        {
            if (!Baby(pawn))
            {
                return null;
            }
            if (pawn.ageTracker == null || pawn.ageTracker.AgeBiologicalTicks < RomperHours * GenDate.TicksPerHour)
            {
                return null;
            }
            for (int i = 0; all != null && i < all.Count; i++)
            {
                List<BodyPartGroupDef> groups = all[i].def.apparel == null ? null : all[i].def.apparel.bodyPartGroups;
                if (groups != null && (groups.Contains(BodyPartGroupDefOf.Torso) || groups.Contains(BodyPartGroupDefOf.Legs)))
                {
                    return null;
                }
            }
            return BabyRomperStandIn();
        }
    }
}
