using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Bodies and weapons thrown by a blow, in the game and not only on screen: the mod options' "Bodies are flung" moves a
    // downed pawn or a corpse along with the blow that felled it, and "Weapons are flung" sends the weapon a pawn
    // drops off through the air before it lands. Each is one point in flight (cells on the map, height in cells), stepped
    // on the game's ticks, here, whether or not anyone is looking: it uses no real time, no drawing and no random numbers,
    // so where a body ends up is the same every time and for every player. A body's cell follows the point as it crosses
    // cells. Its ragdoll (Ragdoll.cs), which runs on the drawing side, chases the point with its hips (Ragdoll.Following)
    // and has nothing to say about where it goes. A flying weapon is off the map while it flies (kept here, so saved here),
    // and put down where it lands by the game's own GenPlace.
    public sealed class Flings : MapComponent
    {
        private const float TickSeconds = 1f / 60f;
        private const float Gravity = 12f;           // cells per game second per second: the ragdoll's, so they land together
        private const float BodyRadius = 0.25f;      // cells a body's middle keeps from a wall
        private const float BodyBounce = 0.25f;      // of its fall a body comes back up with
        private const float BodySlide = 8f;          // cells per game second per second it slows along the ground
        private const float BodyStops = 0.1f;        // cells per game second: slower than this along the ground, it has landed
        private const float WallBounce = 0.1f;       // of its speed into a wall it comes back with
        private const float ItemRadius = 0.1f;
        private const float ItemHeight = 0.6f;       // cells: where a weapon leaves the hand, about a grown figure's
        private const float ItemBounce = 0.35f;
        private const float ItemKeeps = 0.6f;        // of its speed along the ground a weapon keeps at each bounce
        private const float ItemStops = 0.7f;        // cells per game second: bouncing up slower than this, it has landed
        private const float ItemUp = 2.5f;           // cells per game second up it leaves the hand at
        private const float ItemLeast = 1.2f;        // and along the ground, with no blow behind it
        private const float ItemTurns = 720f;        // degrees per game second it tumbles at
        private const float ItemSpread = 40f;        // degrees either way it goes from the way of the blow

        // A body in flight.
        private sealed class Body : IExposable
        {
            public Pawn pawn;
            public Vector3 at;      // x and z on the map, y its height, cells
            public Vector3 speed;   // cells per game second

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref at, "at");
                Scribe_Values.Look(ref speed, "speed");
            }
        }

        // A weapon in flight, or just dropped and about to be.
        private sealed class Item : IExposable
        {
            public ThingWithComps thing;
            public Pawn pawn;       // who dropped it (for the way of the blow, and the hand it is drawn from)
            public IntVec3 from;    // the cell it left
            public bool started;    // off the map and flying; before that, lying where the game dropped it
            public Vector3 at;
            public Vector3 speed;
            public Vector3 axis;    // what it tumbles about
            public float turned;    // degrees so far
            public float heading;   // degrees: which way it comes to lie
            public float age;       // game seconds since it was first drawn
            public bool drawn;
            public Vector3 handOff; // how far the hand it left was from where it set off, faded out as it flies
            public Quaternion turnFrom = Quaternion.identity;   // its turn as it left the hand

            public void ExposeData()
            {
                Scribe_Deep.Look(ref thing, "thing");
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref from, "from");
                Scribe_Values.Look(ref started, "started");
                Scribe_Values.Look(ref at, "at");
                Scribe_Values.Look(ref speed, "speed");
                Scribe_Values.Look(ref axis, "axis");
                Scribe_Values.Look(ref turned, "turned");
                Scribe_Values.Look(ref heading, "heading");
            }
        }

        private List<Body> bodies = new List<Body>();
        private List<Item> items = new List<Item>();
        private float since;   // game seconds since the last tick, for drawing between them
        private readonly Func<float, float, bool> blocked;

        public Flings(Map map) : base(map)
        {
            blocked = Blocked;
        }

        private static Flings Of(Thing thing)
        {
            Map map = thing.MapHeld;
            return map != null ? map.GetComponent<Flings>() : null;
        }

        // Throws a downed pawn or a corpse: angle (degrees clockwise from north) at speed (cells per game second along the
        // ground), up at up. False if it can't be: it isn't on the ground of the map, or it is in flight already and going
        // faster than this along the ground (a blast's fragments mustn't stop what the blast threw). Thrown harder in
        // flight, it takes the new speed from where it is.
        public static bool Throw(Pawn pawn, float angle, float speed, float up)
        {
            Thing thing = BodyThing(pawn);
            Flings here = thing != null && thing.Spawned ? Of(thing) : null;
            if (here == null || pawn.CurrentBed() != null)
            {
                return false;
            }
            Body body = here.BodyOf(pawn);
            if (body == null)
            {
                body = new Body { pawn = pawn, at = thing.Position.ToVector3Shifted() };
                body.at.y = 0f;
                here.bodies.Add(body);
            }
            else if (new Vector2(body.speed.x, body.speed.z).magnitude > speed)
            {
                return false;
            }
            body.speed = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward * speed + Vector3.up * up;
            return true;
        }

        // The thing on the map that is this pawn's body: itself, or its corpse.
        private static Thing BodyThing(Pawn pawn)
        {
            return pawn.Dead ? (Thing)pawn.Corpse : pawn;
        }

        private Body BodyOf(Pawn pawn)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i].pawn == pawn)
                {
                    return bodies[i];
                }
            }
            return null;
        }

        // Where a body in flight is now, between ticks (cells on the map, y its height) and how fast it is going; false if
        // it isn't in flight.
        public static bool PlaceOf(Pawn pawn, out Vector3 at, out Vector3 speed)
        {
            at = speed = Vector3.zero;
            Flings here = Of(pawn);
            Body body = here != null && here.bodies.Count > 0 ? here.BodyOf(pawn) : null;
            if (body == null)
            {
                return false;
            }
            at = body.at + body.speed * here.since;
            speed = body.speed;
            return true;
        }

        // A weapon a pawn has just dropped as it went down: it sets off on the next tick, when the blow that felled the
        // pawn is known (the game reports a killing blow after the kill) and the game is done dropping things.
        public static void TakeWeapon(Pawn pawn, ThingWithComps weapon)
        {
            Flings here = Of(weapon);
            if (here != null)
            {
                here.items.Add(new Item { thing = weapon, pawn = pawn, from = pawn.PositionHeld });
            }
        }

        // Whether a thing in flight can't go to this place: off the map, a wall or other impassable thing, deep water, or
        // a closed door.
        private bool Blocked(float x, float z)
        {
            IntVec3 c = new IntVec3(Mathf.FloorToInt(x), 0, Mathf.FloorToInt(z));
            if (!c.InBounds(map) || !c.Walkable(map))
            {
                return true;
            }
            Building_Door door = c.GetDoor(map);
            return door != null && !door.Open;
        }

        // One tick's flight of a point: gravity, then along each axis in turn, stopped and turned back by a wall (radius
        // cells off it), and down to the ground at height 0. True if it is on the ground.
        public static bool Fly(ref Vector3 at, ref Vector3 speed, Func<float, float, bool> blocked, float radius)
        {
            speed.y -= Gravity * TickSeconds;
            float x = at.x + speed.x * TickSeconds;
            if (blocked(x + Mathf.Sign(speed.x) * radius, at.z))
            {
                speed.x *= -WallBounce;
            }
            else
            {
                at.x = x;
            }
            float z = at.z + speed.z * TickSeconds;
            if (blocked(at.x, z + Mathf.Sign(speed.z) * radius))
            {
                speed.z *= -WallBounce;
            }
            else
            {
                at.z = z;
            }
            at.y += speed.y * TickSeconds;
            if (at.y > 0f)
            {
                return false;
            }
            at.y = 0f;
            return true;
        }

        // A body's tick, on from Fly: a small bounce, then sliding to a stop. True if it has stopped.
        public static bool FlyBody(ref Vector3 at, ref Vector3 speed, Func<float, float, bool> blocked)
        {
            if (!Fly(ref at, ref speed, blocked, BodyRadius))
            {
                return false;
            }
            speed.y = speed.y < -1f ? -speed.y * BodyBounce : 0f;
            Vector3 along = new Vector3(speed.x, 0f, speed.z);
            float fast = along.magnitude;
            float slower = Mathf.Max(0f, fast - BodySlide * TickSeconds);
            if (fast > 0f)
            {
                speed.x *= slower / fast;
                speed.z *= slower / fast;
            }
            return speed.y <= 0f && slower < BodyStops;
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            since = 0f;
            for (int i = bodies.Count - 1; i >= 0; i--)
            {
                if (TickBody(bodies[i]))
                {
                    bodies.RemoveAt(i);
                }
            }
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (TickItem(items[i]))
                {
                    items.RemoveAt(i);
                }
            }
        }

        // True when it is done: landed, or no longer a body lying on this map (carried off, up again, gone).
        private bool TickBody(Body body)
        {
            Pawn pawn = body.pawn;
            Thing thing = pawn != null ? BodyThing(pawn) : null;
            if (thing == null || !thing.Spawned || thing.Map != map || (!pawn.Dead && !pawn.Downed))
            {
                return true;
            }
            bool landed = FlyBody(ref body.at, ref body.speed, blocked);
            IntVec3 cell = new IntVec3(Mathf.FloorToInt(body.at.x), 0, Mathf.FloorToInt(body.at.z));
            if (cell != thing.Position && cell.InBounds(map))
            {
                thing.Position = cell;
                if (!pawn.Dead)
                {
                    pawn.Notify_Teleported(false, true);   // its own job (lying there) goes on
                }
                WalkFigures.Flung(pawn);
            }
            return landed;
        }

        private bool TickItem(Item item)
        {
            if (!item.started)
            {
                if (item.thing == null || !item.thing.Spawned || item.thing.Map != map)
                {
                    return true;   // already picked up or destroyed: it is the game's now
                }
                Launch(item);
            }
            bool down = Fly(ref item.at, ref item.speed, blocked, ItemRadius);
            item.turned += ItemTurns * TickSeconds;
            if (!down)
            {
                return false;
            }
            item.speed.x *= ItemKeeps;
            item.speed.z *= ItemKeeps;
            if (item.speed.y > -ItemStops)
            {
                Land(item);
                return true;
            }
            item.speed.y = -item.speed.y * ItemBounce;
            return false;
        }

        // Off the map and away: the way of the blow that felled its owner if there was one, a little to one side, else any
        // way; the sides and the rest from the thing's id and the tick, not the game's random numbers, so that nothing else
        // in the game is changed by it.
        private void Launch(Item item)
        {
            int seed = Gen.HashCombineInt(item.thing.thingIDNumber, Find.TickManager.TicksGame);
            float angle, push;
            if (WalkFigures.BlowOf(item.pawn, out angle, out push) && angle >= 0f)
            {
                angle += (Unit(seed, 1) - 0.5f) * 2f * ItemSpread;
            }
            else
            {
                angle = Unit(seed, 1) * 360f;
                push = 0f;
            }
            item.thing.DeSpawn();
            item.at = item.from.ToVector3Shifted();
            item.at.y = ItemHeight;
            item.speed = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward * (ItemLeast + push) + Vector3.up * ItemUp;
            item.axis = new Vector3(Unit(seed, 2) - 0.5f, Unit(seed, 3) - 0.5f, Unit(seed, 4) - 0.5f).normalized;
            item.heading = Unit(seed, 5) * 360f;
            item.started = true;
        }

        // 0 .. 1 from a seed and a salt.
        private static float Unit(int seed, int salt)
        {
            return (Gen.HashCombineInt(seed, salt) & 0xFFFF) / 65535f;
        }

        private void Land(Item item)
        {
            IntVec3 cell = new IntVec3(Mathf.FloorToInt(item.at.x), 0, Mathf.FloorToInt(item.at.z));
            if (!cell.InBounds(map) || !cell.Walkable(map))
            {
                cell = item.from;
            }
            if (!GenPlace.TryPlaceThing(item.thing, cell, map, ThingPlaceMode.Near))
            {
                GenSpawn.Spawn(item.thing, item.from, map);
            }
        }

        // The flying weapons, drawn each frame between ticks, from the hand that let go of them.
        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (Find.TickManager.TickRateMultiplier > 0f)
            {
                since = Mathf.Min(since + Time.deltaTime * Find.TickManager.TickRateMultiplier, TickSeconds);
            }
            if (items.Count == 0 || Find.CurrentMap != map || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (!item.started)
                {
                    continue;
                }
                if (!item.drawn)
                {
                    item.drawn = true;
                    Vector3 hand, a, top;
                    if (WalkFigures.HeldLast(item.pawn, out hand, out a, out top))
                    {
                        item.handOff = hand - item.at;
                        item.turnFrom = Quaternion.LookRotation(Vector3.Cross(a, top), top);
                    }
                }
                item.age += Time.deltaTime * Find.TickManager.TickRateMultiplier;
                Vector3 at = item.at + item.speed * since + item.handOff * (1f - Mathf.Clamp01(item.age / 0.35f));
                at.y = Mathf.Max(at.y, 0.02f);
                // Tumbling, and laid flat as it comes down: the plane of the sprite level, as it lies on the ground.
                Quaternion turn = Quaternion.AngleAxis(item.turned + ItemTurns * since, item.axis) * item.turnFrom;
                Quaternion flat = Quaternion.AngleAxis(item.heading, Vector3.up) * Quaternion.AngleAxis(90f, Vector3.right);
                turn = Quaternion.Slerp(flat, turn, Mathf.Clamp01(at.y / 0.4f));
                WalkFigures.DrawFlying(item.thing, at, turn * Vector3.right, turn * Vector3.up);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                items.RemoveAll(i => !i.started);   // still lying where the game dropped it, which is saved with the map
            }
            Scribe_Collections.Look(ref bodies, "bodies", LookMode.Deep);
            Scribe_Collections.Look(ref items, "items", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (bodies == null)
                {
                    bodies = new List<Body>();
                }
                if (items == null)
                {
                    items = new List<Item>();
                }
                bodies.RemoveAll(b => b.pawn == null);
                items.RemoveAll(i => i.thing == null);
            }
        }
    }
}
