using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace M3AT
{
    // Dubs Bad Hygiene (Dubwise.DubsBadHygiene), by name so it is not referenced: while a pawn washes (a bath, a shower, a hot
    // tub, a pool: a job of its WashingJobDef with its Washing hediff on), the mod leaves the clothes and headgear off its
    // sprite (a postfix on PawnRenderer.GetDrawParms), so the chibi is drawn with nothing on then too (SleepWearOf: a baby
    // keeps its romper). Only drawn, as the mod does: the game keeps the apparel worn.
    // Where the pawn is put (on the toilet's seat, lying in the bath, sat in the hot tub, in the pool) comes from the IsoRim
    // pack for it (james.isorimwaylith.dbhmodels) as IsoRim attachments, which WalkFigures.DrawPlain follows.
    public static partial class WalkFigures
    {
        // Lying in a bath or a hot tub (a Lie attachment on its building: the pack puts the anchor at the water's surface):
        // the trunk sat up SoakRecline more than lying on the floor, against the tub's end, and the body let down onto the
        // tub's floor SoakDepth under the water, so it lies in the water with the head out of it.
        private const float SoakRecline = 35f;   // degrees
        private const float SoakDepth = 0.16f;   // cells under the water's surface the body rests (the pack's bathtub's floor is about 0.2 under it), so the shoulders show
        private const float SoakAlong = 0.15f;   // model units the figure is slid toward its feet, so the head rests inside the tub's end rather than over it

        // The floor of the pack's swimming pool over the ground, cells (its model's, which IsoRim draws at its own heights:
        // the wall 0.8, the water 0.7): a pool's swimmer wades on it, as the water is only about chest deep on the chibi;
        // floated with the neck at the water line, its body went down through the floor. Dubs Bad Hygiene lays its own
        // water terrain under the pool, so the wading's sinking into water is left out there (DrawSkinned).
        private const float PoolFloor = 0.091f;

        // A swimmer's neck this far (model units: cells at a grown pawn's size) under a pool's water line, so the mouth and
        // nose are out of the water (the ground's water, opaque, keeps the neck at its line).
        private const float PoolFaceClear = 0.12f;

        private static bool InTub(Thing host)
        {
            string name = host.GetType().Name;
            return !(host is Pawn) && (name.IndexOf("bath", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Tub", StringComparison.Ordinal) >= 0);
        }

        // Whether the pawn is in a pool another mod attaches it to (a SitNoLegs attachment on a building whose class
        // is named a pool, during a swimming job: the IsoRim pack for Dubs Bad Hygiene), and the water's height there
        // (its anchor).
        private static bool PoolOf(Pawn pawn, out float line)
        {
            line = 0f;
            Job job = pawn.CurJob;
            if (job == null || job.def == null || job.def.driverClass == null || job.def.driverClass.Name.IndexOf("Swim", StringComparison.Ordinal) < 0)
            {
                return false;
            }
            Attachment? on = Api.AttachmentOf(pawn);
            Thing host = on.HasValue ? on.Value.host : null;
            if (host == null || host is Pawn || on.Value.pose != AttachPose.SitNoLegs || host.GetType().Name.IndexOf("Pool", StringComparison.Ordinal) < 0)
            {
                return false;
            }
            Vector3 anchor;
            float yaw;
            if (!Api.AttachedPlace(pawn, out anchor, out yaw))
            {
                return false;
            }
            line = anchor.y;
            return true;
        }

        // A pool swimmer's stride, heading and pace, which IsoRim doesn't step for an attached figure: the ground it has
        // covered as drawn over the stride (radians), turned toward where it is going (PoolTurn degrees a game second,
        // kept while still) and on the move by the pather, eased over PoolEase game seconds, as StrokeOf eases into the crawl.
        private const float PoolTurn = 360f;
        private const float PoolEase = 0.4f;

        private static void PoolStroke(Pawn pawn, Ease e, float stride, out float yaw, out float phase, out float gait)
        {
            int frame = Time.frameCount;
            Vector3 now = pawn.DrawPos;
            if (e.poolFrame != frame)
            {
                bool recent = frame - e.poolFrame <= 2;
                float game = Time.deltaTime * Find.TickManager.TickRateMultiplier;
                Vector3 moved = recent ? now - e.poolAt : Vector3.zero;
                moved.y = 0f;
                bool moving = pawn.pather != null && pawn.pather.MovingNow;
                if (!recent)
                {
                    e.poolYaw = pawn.Rotation.AsAngle;
                    e.poolGait = moving ? 1f : 0f;
                }
                if (moved.sqrMagnitude > 1e-8f)
                {
                    float toward = Mathf.Atan2(moved.x, moved.z) * Mathf.Rad2Deg;
                    e.poolYaw = Mathf.MoveTowardsAngle(e.poolYaw, toward, PoolTurn * game);
                    e.poolPhase = Mathf.Repeat(e.poolPhase + moved.magnitude / Mathf.Max(stride, 0.01f) * 2f * Mathf.PI, 2f * Mathf.PI * 1000f);
                }
                e.poolGait = Mathf.MoveTowards(e.poolGait, moving ? 1f : 0f, game / PoolEase);
                e.poolAt = now;
                e.poolFrame = frame;
            }
            yaw = e.poolYaw;
            phase = e.poolPhase;
            gait = e.poolGait;
        }

        // Climbing in and out of a raised pool (Dubs Bad Hygiene's, drawn by IsoRim as a model with a wall: the IsoRim pack
        // for it). The game walks a swimmer across the wall like any cell, well under a second; pool_climb (walk.climb_at:
        // from hanging at the wall in the water to standing outside, facing out) is played on its own clock, ClimbSeconds
        // of game time (the clip's own), from the frame the pawn's drawn place crosses the pool's edge, at the edge it
        // crossed: forward getting out, backwards getting in (lowered in backwards holding the rim, as off a ladder), turned
        // from the way it faced over ClimbTurn of the way in. Its origin (where it ends standing outside) is ClimbOut
        // outside the edge (walk.CLIMB_WALL[0] leg lengths: the wall's outer face behind it). Afterwards the figure is
        // brought over to where the game has the pawn over ClimbBackTicks (ClimbBack, from DrawSkinned). Made for a grown
        // pawn of the standard size against the pack's wall (0.8 cells, shoulder high); a child's is lower than the wall.
        // Off: swimmers vault in and out as anyone else does (PoolVault), the climb being slow beside the game's own
        // walk over the wall; the crossings are still followed for inPool, which the clothes and wading go by.
        private const bool ClimbSwimmers = false;
        private const float ClimbOut = 0.174f;     // model units (0.35 leg lengths)
        private const float ClimbHipsIn = 0.65f;   // model units: the hips inside the origin as the climb starts in the water (1.31 leg lengths)
        private const float ClimbTurn = 0.15f;
        private const int ClimbBackTicks = 30;
        private const float ClimbForget = 2.5f;    // cells outside the pool past which it is forgotten

        private sealed class Climbing
        {
            public Thing pool;
            public bool inside;
            public bool inPool;        // in it as far as the climbs go: climbed in (or first seen inside), not yet out
            public int seenFrame = -10;
            public int startTick = -1;
            public bool goingIn;
            public Vector3 origin;     // where the clip's figure stands outside (true space, the ground)
            public float yaw;          // facing out of the pool
            public float fromYaw;      // the way it faced as it started
            public int endTick = -100000;
            public Vector3 endAt;      // where the figure's hips were as the climb ended
        }

        private static readonly Dictionary<int, Climbing> climbing = new Dictionary<int, Climbing>();

        // How far through the climb the pawn is now (0 in the water .. 1 outside), or -1 when not climbing (for AutoTest).
        internal static float ClimbPhase(Pawn pawn)
        {
            Climbing c;
            if (pawn == null || !climbing.TryGetValue(pawn.thingIDNumber, out c) || c.startTick < 0)
            {
                return -1f;
            }
            float t = Mathf.Clamp01((Find.TickManager.TicksGame - c.startTick) / Mathf.Max(1f, skin.SecondsOf("pool_climb") * 60f));
            return c.goingIn ? 1f - t : t;
        }

        // Whether the pawn is in a raised pool or climbing in or out of one, as the climbs go: drawn with nothing on, as
        // washing (SleepWearOf), though the game may call it away mid-swim, which ends Dubs Bad Hygiene's washing.
        private static bool InPool(Pawn pawn)
        {
            Climbing c;
            return climbing.Count != 0 && climbing.TryGetValue(pawn.thingIDNumber, out c) && (c.inPool || c.startTick >= 0);
        }

        // Whether the pawn stands in a raised pool now (on one of its cells, inside its edge as drawn): it wades on the
        // pool's floor, whatever it is doing (swimming, called away mid-swim, walking across).
        private static bool InRaisedPool(Pawn pawn)
        {
            if (!pawn.Spawned)
            {
                return false;
            }
            Building b = pawn.Position.GetEdifice(pawn.Map);
            if (!RaisedPool(b))
            {
                return false;
            }
            CellRect r = b.OccupiedRect();
            Vector3 at = pawn.DrawPos;
            return at.x > r.minX && at.x < r.maxX + 1 && at.z > r.minZ && at.z < r.maxZ + 1;
        }

        // A building whose class is named a pool (not Dubs Bad Hygiene's flush pool tiles) that IsoRim draws as a model.
        private static bool RaisedPool(Thing t)
        {
            if (t == null || t is Pawn || !t.Spawned)
            {
                return false;
            }
            string name = t.GetType().Name;
            return name.IndexOf("Pool", StringComparison.Ordinal) >= 0 && name.IndexOf("Tile", StringComparison.Ordinal) < 0 && Api.ModelStandsIn(t);
        }

        // From DrawFigure, before the rest: true when the pawn is climbing over a pool's wall now, with drawn whether the
        // climb was drawn.
        private static bool PoolClimb(FigureDrawContext ctx, out bool drawn)
        {
            drawn = false;
            Pawn pawn = ctx.Pawn;
            if (skin == null || !skin.Has("pool_climb") || ctx.ForOtherMod)
            {
                return false;
            }
            int id = pawn.thingIDNumber;
            Climbing c;
            climbing.TryGetValue(id, out c);
            Job job = pawn.CurJob;
            bool swimJob = job != null && job.def != null && job.def.driverClass != null && job.def.driverClass.Name.IndexOf("Swim", StringComparison.Ordinal) >= 0;
            if (c == null && !swimJob)
            {
                return false;
            }
            if (pawn.Dead || pawn.Downed || !pawn.Spawned || pawn.CurrentBed() != null)
            {
                climbing.Remove(id);
                return false;
            }
            // The pool it is in, else the one its swimming job is for (target B, else A, as the IsoRim pack takes it), so the
            // climb in is seen from outside.
            Thing here = pawn.Position.GetEdifice(pawn.Map);
            if (!RaisedPool(here) && swimJob && (c == null || c.pool == null))
            {
                here = job.targetB.Thing ?? job.targetA.Thing;
            }
            if (RaisedPool(here))
            {
                if (c == null)
                {
                    c = new Climbing();
                    climbing[id] = c;
                    c.inPool = pawn.Position.GetEdifice(pawn.Map) == here && Washing(pawn);
                }
                c.pool = here;
            }
            if (c == null)
            {
                return false;
            }
            if (c.pool == null || !c.pool.Spawned || c.pool.Map != pawn.Map)
            {
                climbing.Remove(id);
                return false;
            }
            Vector3 at = pawn.DrawPos;
            CellRect r = c.pool.OccupiedRect();
            float minX = r.minX, maxX = r.maxX + 1, minZ = r.minZ, maxZ = r.maxZ + 1;
            bool inside = at.x > minX && at.x < maxX && at.z > minZ && at.z < maxZ;
            int frame = Time.frameCount;
            int now = Find.TickManager.TicksGame;
            float size = SizeOf(pawn);
            float length = Mathf.Max(1f, skin.SecondsOf("pool_climb") * 60f);
            // Over the edge: in only once Dubs Bad Hygiene has made it a swimmer (its swim job walks it to a cell beside the
            // pool first, the way there sometimes cutting across a corner of it, and there makes it an occupant and washing,
            // then walks it in), and out whenever it is in, whatever it is doing (the job's own way out, or called away).
            bool crossing = c.startTick < 0 && frame - c.seenFrame <= 2 && inside != c.inside
                && (inside ? !c.inPool && Washing(pawn) : c.inPool);
            if (crossing && !ClimbSwimmers)
            {
                c.inPool = inside;   // (in or out by the vault)
            }
            else if (crossing)
            {
                // Crossed the edge: the side nearest where it crossed, the inward way across it.
                float x = Mathf.Clamp(at.x, minX, maxX), z = Mathf.Clamp(at.z, minZ, maxZ);
                float left = x - minX, right = maxX - x, bottom = z - minZ, top = maxZ - z;
                float least = Mathf.Min(Mathf.Min(left, right), Mathf.Min(bottom, top));
                Vector3 inward = least == left ? Vector3.right : least == right ? Vector3.left : least == bottom ? Vector3.forward : Vector3.back;
                Vector3 edge = least == left ? new Vector3(minX, 0f, z) : least == right ? new Vector3(maxX, 0f, z)
                    : least == bottom ? new Vector3(x, 0f, minZ) : new Vector3(x, 0f, maxZ);
                // (kept off the corners, where the walls meet)
                edge.x = inward.x == 0f ? Mathf.Clamp(edge.x, minX + 0.4f, maxX - 0.4f) : edge.x;
                edge.z = inward.z == 0f ? Mathf.Clamp(edge.z, minZ + 0.4f, maxZ - 0.4f) : edge.z;
                c.startTick = now;
                c.goingIn = inside;
                c.inPool = inside;
                c.origin = edge - inward * (ClimbOut * size);
                c.yaw = Mathf.Atan2(-inward.x, -inward.z) * Mathf.Rad2Deg;
                c.fromYaw = pawn.Rotation.AsAngle;
            }
            c.inside = inside;
            c.seenFrame = frame;
            if (c.startTick >= 0)
            {
                float t = (now - c.startTick) / length;
                if (t < 1f)
                {
                    if (!SkinnedFigure.MaybeOnScreen(c.origin, size))
                    {
                        drawn = true;
                        return true;
                    }
                    float phase = c.goingIn ? 1f - t : t;
                    // Getting in, it turns from the way it walked up to face out over the first of it; getting out it
                    // faces out all the way.
                    float yaw = c.goingIn ? Mathf.LerpAngle(c.fromYaw, c.yaw, Mathf.SmoothStep(0f, 1f, t / ClimbTurn)) : c.yaw;
                    SkinnedFigure.Shape shape = ShapeFor(pawn);
                    SkinnedFigure.Posed fig = skin.For(id, shape);
                    skin.Begin(fig);
                    CutLimbs(pawn, fig);
                    int frames = skin.FramesOf("pool_climb");
                    skin.Add(fig, "pool_climb", phase * (frames - 1) / frames, 1f);
                    skin.End(fig, Matrix4x4.identity);
                    DrawDressed(pawn, fig, c.origin, yaw, size);
                    drawn = true;
                    return true;
                }
                // Done: from where its hips were, over to the game's place.
                c.startTick = -1;
                c.endTick = now;
                Vector3 inwardWay = Quaternion.AngleAxis(c.yaw, Vector3.up) * Vector3.back;
                c.endAt = c.goingIn ? c.origin + inwardWay * (ClimbHipsIn * size) : c.origin;
            }
            if (!inside && now - c.endTick > ClimbBackTicks)
            {
                float out_ = Mathf.Max(Mathf.Max(minX - at.x, at.x - maxX), Mathf.Max(minZ - at.z, at.z - maxZ));
                if (out_ > ClimbForget || !swimJob && out_ > 0.6f)
                {
                    climbing.Remove(id);
                }
            }
            return false;
        }

        // Vaulting a raised pool's wall. The pool is passable at no cost, so the game walks any pawn across it on its way
        // somewhere, the wall like any cell, and a swimmer in and out the same way. Anyone on its feet and on the move (not
        // mid-climb, ClimbSwimmers) is drawn vaulting it (pool_vault: walk.vault_at, from standing VAULT_REACH behind the wall's middle to standing
        // as far past it, facing the way it goes) by where it is across that stretch (VaultReach), the figure set at the
        // wall's middle (WallMiddle inside the edge) where the pawn crosses, so it never falls behind the game. Inside the
        // pool it walks on its floor, in the water.
        private const float VaultReach = 0.446f;   // model units: walk.VAULT_REACH, 0.9 leg lengths
        private const float WallMiddle = 0.1575f;  // cells inside the pool's edge: half the pack's wall (0.315 thick, 0.8 high)

        // A raised pool on the pawn's cell or the one it steps to next.
        private static Thing PoolNear(Pawn pawn)
        {
            Map map = pawn.Map;
            Building here = pawn.Position.GetEdifice(map);
            if (RaisedPool(here))
            {
                return here;
            }
            IntVec3 next = pawn.pather.nextCell;
            Building there = next.InBounds(map) ? next.GetEdifice(map) : null;
            return RaisedPool(there) ? there : null;
        }

        // From DrawFigure, after PoolClimb: true when the pawn is vaulting a pool's wall now, with drawn whether it was drawn.
        private static bool PoolVault(FigureDrawContext ctx, out bool drawn)
        {
            drawn = false;
            Pawn pawn = ctx.Pawn;
            if (skin == null || !skin.Has("pool_vault") || ctx.ForOtherMod || pawn.pather == null || !pawn.pather.MovingNow
                || pawn.Dead || pawn.Downed || !pawn.Spawned || pawn.GetPosture() != PawnPosture.Standing || ClimbPhase(pawn) >= 0f)
            {
                return false;
            }
            Thing pool = PoolNear(pawn);
            if (pool == null)
            {
                return false;
            }
            Vector3 at = pawn.DrawPos;
            CellRect r = pool.OccupiedRect();
            float minX = r.minX, maxX = r.maxX + 1, minZ = r.minZ, maxZ = r.maxZ + 1;
            // Its place across the nearest side of the pool: d into it (- outside), the way in across that side, and
            // where on the side it crosses.
            float outX = Mathf.Max(minX - at.x, at.x - maxX), outZ = Mathf.Max(minZ - at.z, at.z - maxZ);
            Vector3 inward;
            float d;
            if (outX > 0f || outZ > 0f)
            {
                bool acrossX = outX >= outZ;
                d = -Mathf.Max(outX, outZ);
                inward = acrossX ? (at.x < minX ? Vector3.right : Vector3.left) : (at.z < minZ ? Vector3.forward : Vector3.back);
            }
            else
            {
                float left = at.x - minX, right = maxX - at.x, bottom = at.z - minZ, top = maxZ - at.z;
                d = Mathf.Min(Mathf.Min(left, right), Mathf.Min(bottom, top));
                inward = d == left ? Vector3.right : d == right ? Vector3.left : d == bottom ? Vector3.forward : Vector3.back;
            }
            float size = SizeOf(pawn);
            float reach = VaultReach * size;
            float s = d - WallMiddle;   // from the wall's middle, + into the pool
            if (Mathf.Abs(s) >= reach)
            {
                return false;
            }
            // In or out by which way its next step goes across this side; not when passing along it outside, nor
            // stepping along the wall.
            IntVec3 next = pawn.pather.nextCell;
            if (!r.Contains(next) && !r.Contains(pawn.Position))
            {
                return false;
            }
            float across = Vector3.Dot(next.ToVector3Shifted() - at, inward);
            if (Mathf.Abs(across) < 0.2f)
            {
                return false;
            }
            bool goingIn = across > 0f;
            float t = goingIn ? (s + reach) / (2f * reach) : (reach - s) / (2f * reach);
            Vector3 way = goingIn ? inward : -inward;
            Vector3 middle = at - inward * (s);
            middle.y = PoolFloor * Mathf.SmoothStep(0f, 1f, goingIn ? t : 1f - t);   // (from the ground outside to the pool's floor)
            float yaw = Mathf.Atan2(way.x, way.z) * Mathf.Rad2Deg;
            if (!SkinnedFigure.MaybeOnScreen(middle, size))
            {
                drawn = true;
                return true;
            }
            SkinnedFigure.Shape shape = ShapeFor(pawn);
            SkinnedFigure.Posed fig = skin.For(pawn.thingIDNumber, shape);
            skin.Begin(fig);
            CutLimbs(pawn, fig);
            int frames = skin.FramesOf("pool_vault");
            skin.Add(fig, "pool_vault", Mathf.Clamp01(t) * (frames - 1) / frames, 1f);
            skin.End(fig, Matrix4x4.identity);
            DrawDressed(pawn, fig, middle, yaw, size);
            drawn = true;
            return true;
        }

        // Just after a climb, the figure brought from where the climb left it to the game's place (DrawSkinned's at).
        private static void ClimbBack(Pawn pawn, ref Vector3 at)
        {
            Climbing c;
            if (climbing.Count == 0 || !climbing.TryGetValue(pawn.thingIDNumber, out c))
            {
                return;
            }
            int since = Find.TickManager.TicksGame - c.endTick;
            if (since < 0 || since >= ClimbBackTicks)
            {
                return;
            }
            float left = 1f - Mathf.SmoothStep(0f, 1f, since / (float)ClimbBackTicks);
            at.x = Mathf.Lerp(at.x, c.endAt.x, left);
            at.z = Mathf.Lerp(at.z, c.endAt.z, left);
        }

        private static bool Washing(Pawn pawn)
        {
            Job job = pawn.CurJob;
            if (job == null || job.def == null || job.def.GetType().Name != "WashingJobDef" || pawn.health == null)
            {
                return false;
            }
            var all = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].def.defName == "Washing")
                {
                    return true;
                }
            }
            return false;
        }
    }
}
