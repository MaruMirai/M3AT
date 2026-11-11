using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace M3AT
{
    // Romance on the Rim (Workshop 2654432921, telardo.RomanceOnTheRim), found by name so the mod is not referenced.
    // Its shared reading (JobDriver_ShareReading) walks the watcher onto the cell the reader faces and has it stare at the
    // reader from there for 500 ticks before it sits beside them. A reader sat at a table faces the table, so the watcher
    // stood in it. Where that cell can't be stood on, it goes instead to the seat it will sit on beside the reader (its
    // target A), or the nearest cell beside the reader it can stand on, and stares from there facing the reader; the
    // figure looks at the book meanwhile (WalkFigures.LessonLookOf). Sat beside them, the mod turns it to face the reader
    // for its first 500 ticks, sideways on its seat; it faces the way the reader does throughout instead, as the figures
    // turn and lean toward each other themselves (WalkFigures.ShareTurn, ShareLean). The date is otherwise the mod's own.
    internal static class RomanceOnTheRim
    {
        private const string Tag = "[M3AT] ";
        private const int GotoToil = 1;   // the mod's toils: its lover bubble, the goto to the cell in front, the stare, the goto to the seat, sitting beside
        private const string StareToil = "RotR_SR_Stare";
        private const string AccompanyToil = "RotR_SR_Accompany";

        public static void PatchShareReading(Harmony harmony)
        {
            try
            {
                Type driver = GenTypes.GetTypeInAnyAssembly("RomanceOnTheRim.JobDriver_ShareReading");
                if (driver == null)
                {
                    return;
                }
                MethodInfo toils = AccessTools.Method(driver, "MakeNewToils");
                if (toils == null || toils.ReturnType != typeof(IEnumerable<Toil>))
                {
                    Log.Warning(Tag + "Romance on the Rim's shared reading was not found; its watcher stands in the table to stare.");
                    return;
                }
                harmony.Patch(toils, postfix: new HarmonyMethod(typeof(RomanceOnTheRim), nameof(ShareReadingPostfix)));
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "Romance on the Rim's shared reading is left as it is: " + e.Message);
            }
        }

        private static IEnumerable<Toil> ShareReadingPostfix(IEnumerable<Toil> __result, JobDriver __instance)
        {
            Job job = __instance.job;
            Pawn watcher = __instance.pawn;
            Pawn reader = job != null ? job.targetB.Pawn : null;
            IntVec3 readerCell = job != null ? job.targetC.Cell : IntVec3.Invalid;
            IntVec3 stand = IntVec3.Invalid;
            int i = 0;
            foreach (Toil toil in __result)
            {
                if (i == GotoToil && reader != null && readerCell.IsValid && watcher.Map != null)
                {
                    stand = StandBeside(watcher, reader, readerCell, job.targetA.Cell);
                    if (stand.IsValid)
                    {
                        i++;
                        yield return Toils_Goto.GotoCell(stand, PathEndMode.OnCell);
                        continue;
                    }
                }
                if (reader != null && toil.debugName == AccompanyToil)
                {
                    Action theirs = toil.tickAction;
                    toil.tickAction = () =>
                    {
                        theirs?.Invoke();
                        toil.actor.Rotation = reader.Rotation;
                    };
                }
                if (stand.IsValid && toil.debugName == StareToil)
                {
                    // (the mod turns it to face the way the reader faces, back from in front of it; from beside, it faces
                    // the reader instead)
                    Action<int> theirs = toil.tickIntervalAction;
                    IntVec3 at = readerCell;
                    toil.tickIntervalAction = delta =>
                    {
                        theirs?.Invoke(delta);
                        toil.actor.rotationTracker.FaceCell(at);
                    };
                }
                i++;
                yield return toil;
            }
        }

        // Where the watcher stands to stare in place of the cell in front of the reader, or Invalid to keep that cell (it
        // can be stood on). Its own seat beside the reader first, else the cell beside the reader nearest to the one in
        // front that it can stand on and reach.
        private static IntVec3 StandBeside(Pawn watcher, Pawn reader, IntVec3 readerCell, IntVec3 seat)
        {
            Map map = watcher.Map;
            IntVec3 front = readerCell + reader.Rotation.FacingCell;
            if (!front.InBounds(map) || front.Standable(map))
            {
                return IntVec3.Invalid;
            }
            if (seat.IsValid && seat.AdjacentTo8Way(readerCell) && seat.Standable(map) && watcher.CanReach(seat, PathEndMode.OnCell, Danger.Some))
            {
                return seat;
            }
            IntVec3 best = IntVec3.Invalid;
            float bestDistance = float.MaxValue;
            foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(readerCell, map)))
            {
                if (c == front || !c.InBounds(map) || !c.Standable(map) || !watcher.CanReach(c, PathEndMode.OnCell, Danger.Some))
                {
                    continue;
                }
                float d = c.DistanceToSquared(front);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = c;
                }
            }
            return best;
        }
    }
}
