using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The ragdolls at rest on a map, kept with the save so a body lies as it fell after the game is loaded again
    // (WalkFigures.SavedDoll). Each is the pawn's (by thingIDNumber, a corpse's by the pawn in it), its cell, the yaw and
    // place it was drawn at, and its joints (cells, in its own space). It is forgotten when the body is moved or gets up;
    // a body gone from the map is left out when the game is saved.
    public sealed class RagdollRests : MapComponent
    {
        public sealed class Rest : IExposable
        {
            public int id;
            public IntVec3 cell;
            public float yaw;
            public Vector3 offset;   // the place it is drawn at from the cell's middle, cells
            public bool dead;
            public List<Vector3> joints;

            public void ExposeData()
            {
                Scribe_Values.Look(ref id, "id");
                Scribe_Values.Look(ref cell, "cell");
                Scribe_Values.Look(ref yaw, "yaw");
                Scribe_Values.Look(ref offset, "offset");
                Scribe_Values.Look(ref dead, "dead");
                Scribe_Collections.Look(ref joints, "joints", LookMode.Value);
            }
        }

        private List<Rest> rests = new List<Rest>();
        private Dictionary<int, Rest> byId;

        public RagdollRests(Map map) : base(map)
        {
        }

        private static RagdollRests Of(Pawn pawn)
        {
            Map map = pawn.MapHeld;
            return map != null ? map.GetComponent<RagdollRests>() : null;
        }

        private Dictionary<int, Rest> ById()
        {
            if (byId == null)
            {
                byId = new Dictionary<int, Rest>();
                foreach (Rest r in rests)
                {
                    byId[r.id] = r;
                }
            }
            return byId;
        }

        public static void Keep(Pawn pawn, SkinnedFigure.Ragdoll doll)
        {
            RagdollRests here = Of(pawn);
            if (here == null)
            {
                return;
            }
            Rest rest;
            if (!here.ById().TryGetValue(pawn.thingIDNumber, out rest))
            {
                rest = new Rest { id = pawn.thingIDNumber };
                here.rests.Add(rest);
                here.byId[rest.id] = rest;
            }
            Vector3 middle = doll.Cell.ToVector3Shifted();
            rest.cell = doll.Cell;
            rest.yaw = doll.Yaw;
            rest.offset = new Vector3(doll.Origin.x - middle.x, 0f, doll.Origin.z - middle.z);
            rest.dead = doll.Dead;
            rest.joints = new List<Vector3>(doll.JointsNow);
        }

        public static Rest Find(Pawn pawn)
        {
            RagdollRests here = Of(pawn);
            Rest rest;
            return here != null && here.rests.Count > 0 && here.ById().TryGetValue(pawn.thingIDNumber, out rest) ? rest : null;
        }

        public static void Forget(Pawn pawn)
        {
            RagdollRests here = Of(pawn);
            Rest rest;
            if (here != null && here.ById().TryGetValue(pawn.thingIDNumber, out rest))
            {
                here.rests.Remove(rest);
                here.byId.Remove(rest.id);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // Only bodies still on the map.
                HashSet<int> here = new HashSet<int>();
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    here.Add(p.thingIDNumber);
                }
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                {
                    Corpse c = t as Corpse;
                    if (c != null && c.InnerPawn != null)
                    {
                        here.Add(c.InnerPawn.thingIDNumber);
                    }
                }
                rests.RemoveAll(r => !here.Contains(r.id));
                byId = null;
            }
            Scribe_Collections.Look(ref rests, "rests", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (rests == null)
                {
                    rests = new List<Rest>();
                }
                byId = null;
            }
        }
    }
}
