using System;
using System.Collections.Generic;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace M3AT
{
    // Putting clothes on and taking them off (skinned figure only). The game's Wear job walks to the apparel and stands
    // facing it for its EquipDelay (plus that of whatever worn it can't be worn with, which it drops first); RemoveApparel
    // stands for the apparel's EquipDelay and drops it. Over that wait the figure plays a dress_<kind> clip by what the
    // apparel covers (DressKindsOf: trousers, a shirt over the head, a coat, a hat, a belt; legs and then the torso for a
    // whole suit), as a work kind (WorkOf), so in place of the idle, the weapon put away first. Taking off is the same clip
    // backwards, and the Wear job takes off what it replaces before it puts on the new. In a long wait (power armour)
    // the stretch the export marks (dress_loop: tugging and settling) keeps its own pace and the rest is slowed.
    // The apparel itself: put on, it is drawn in the hands (the chibi's own garment, held up, or a hat by its brim and
    // then flying onto the head; else IsoRim's look of it on the ground; the copy on the ground hidden) until the clip's
    // moment it goes on (DressOn), then drawn worn (DressedIn); taken off, the other way round, until the game drops it.
    public static partial class WalkFigures
    {
        // The share of each clip by which the apparel is on the body (wearing; taking off, by which it is off, the clip
        // being played backwards): trousers as they are pulled up, a shirt as it comes down past the head, a coat as the
        // first arm goes into its sleeve (before it, one hand is behind the back, where a coat held between the hands
        // would go through the body), a hat as it lands, a belt as it is buckled. Each before its kind's dress_loop.
        private static readonly Dictionary<string, float> DressOn = new Dictionary<string, float>
        {
            { "dress_legs", 0.62f }, { "dress_over", 0.47f }, { "dress_coat", 0.2f }, { "dress_head", HatLands }, { "dress_belt", 0.42f },
        };
        private const float DressItemScale = 0.8f;   // of the apparel's drawn size on the ground, held in the hands (no garment)

        // A hat (dress_head) is held by its front brim in the right hand from HatHeld of the clip, tipped up on edge with
        // its opening to the face (HatPitch, degrees about the figure's left, + taking the back of the brim up; HatRoll
        // about the forward way), until HatRelease, the hand at its highest; it then goes the rest of the way onto the
        // head by itself, over an arc HatArc cells high, swinging level as it goes, and lands at HatLands (walk.DRESS's
        // 'head' keys): the chibi's arms don't reach a hat on its big head. Before HatHeld it is still on the ground.
        private const float HatHeld = 0.12f, HatRelease = 0.5f, HatLands = 0.6f;
        private const float HatArc = 0.1f;
        private static readonly float[] HatAt = { HatHeld, 0.38f, HatRelease };
        private static readonly float[] HatPitch = { 80f, 75f, 60f };
        private static readonly float[] HatRoll = { 15f, 5f, 0f };

        // Any other garment is held up between the hands at its full size as worn, its top (the waistband, the collar) at
        // them, just in front of them, flattened front to back by this (x across, y up, z front to back) as cloth held up
        // rather than standing round a body that isn't there.
        private static readonly Dictionary<string, Vector3> HeldShrink = new Dictionary<string, Vector3>
        {
            { "dress_legs", new Vector3(1f, 0.5f, 0.5f) },   // (bunched up its length, held open by the waistband at the
                                                             // knees: at full length it would go through the ground)
            { "dress_over", new Vector3(1f, 1f, 0.5f) },
            { "dress_coat", new Vector3(1f, 1f, 0.5f) },
            { "dress_belt", new Vector3(1f, 1f, 1f) },
        };
        private const float DressLast = 0.999f;   // a clip's phase no further on than this, as 1 wraps round to its start

        private static readonly AccessTools.FieldRef<JobDriver_Wear, int> wearDuration = AccessTools.FieldRefAccess<JobDriver_Wear, int>("duration");
        private static readonly AccessTools.FieldRef<JobDriver_RemoveApparel, int> removeDuration = AccessTools.FieldRefAccess<JobDriver_RemoveApparel, int>("duration");

        // What a dressing pawn is doing this frame (Dressed): the clip and how far through it, the apparel in question, and
        // whether that is on the body (drawn worn) or in the hands.
        private sealed class Dress
        {
            public int frame = -1;
            public string clip;   // null: not dressing
            public float phase;
            public Apparel apparel;
            public bool onBody;
            public bool inHands;
            public bool putOn;   // the apparel is being put on (from the ground or a stand), not taken off
            // The job's plan, worked out once per job (by its loadID): how long the whole wait is, how much of it goes to
            // taking off what it replaces, and the clips for each.
            public int job = -1;
            public int total, removeTicks;
            public Apparel old;
            public string[] oldKinds, newKinds;
        }
        private static readonly Dictionary<int, Dress> dresses = new Dictionary<int, Dress>();

        // The dressing a pawn is doing now, or null; worked out once a frame.
        private static Dress Dressed(Pawn pawn)
        {
            if (skin == null || pawn.jobs == null || pawn.jobs.curDriver == null || pawn.CurJob == null)
            {
                return null;
            }
            JobDriver driver = pawn.jobs.curDriver;
            bool wear = driver is JobDriver_Wear;
            if (!wear && !(driver is JobDriver_RemoveApparel))
            {
                return null;
            }
            Dress d;
            if (!dresses.TryGetValue(pawn.thingIDNumber, out d))
            {
                if (dresses.Count > 200)
                {
                    dresses.Clear();
                }
                d = new Dress();
                dresses[pawn.thingIDNumber] = d;
            }
            if (d.frame == Time.frameCount)
            {
                return d.clip != null ? d : null;
            }
            d.frame = Time.frameCount;
            d.clip = null;
            try
            {
                Work(pawn, driver, wear, d);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Working out a pawn's dressing failed: " + e, 0x4d3a7e11);
                d.clip = null;
            }
            return d.clip != null ? d : null;
        }

        private static void Work(Pawn pawn, JobDriver driver, bool wear, Dress d)
        {
            Job job = pawn.CurJob;
            Apparel apparel = job.targetA.Thing as Apparel;
            // (only over the wait itself: Wear's is its MakeNewToils toil, after the walk to it; RemoveApparel's its Wait)
            if (apparel == null || driver.CurToilString != (wear ? "MakeNewToils" : "Wait") || pawn.apparel == null)
            {
                return;
            }
            if (d.job != job.loadID)
            {
                d.job = job.loadID;
                d.old = null;
                d.oldKinds = null;
                if (wear)
                {
                    d.total = wearDuration((JobDriver_Wear)driver);
                    // (the game's Notify_Starting adds each worn apparel it can't be worn with to the wait, and drops the
                    // last of them, as TryUnequipSomething looks, once that one's time has passed)
                    d.removeTicks = Mathf.Max(0, d.total - (int)(apparel.GetStatValue(StatDefOf.EquipDelay) * 60f));
                    List<Apparel> worn = pawn.apparel.WornApparel;
                    for (int i = worn.Count - 1; i >= 0 && d.removeTicks > 0; i--)
                    {
                        if (!ApparelUtility.CanWearTogether(apparel.def, worn[i].def, pawn.RaceProps.body))
                        {
                            d.old = worn[i];
                            d.oldKinds = DressKindsOf(worn[i].def);
                            break;
                        }
                    }
                    d.newKinds = DressKindsOf(apparel.def);
                }
                else
                {
                    d.total = removeDuration((JobDriver_RemoveApparel)driver);
                    d.removeTicks = d.total;
                    d.old = apparel;
                    d.oldKinds = DressKindsOf(apparel.def);
                    d.newKinds = null;
                }
            }
            if (d.total <= 0)
            {
                return;
            }
            float elapsed = Mathf.Clamp(d.total - driver.ticksLeftThisToil, 0, d.total);
            if (elapsed < d.removeTicks)
            {
                // Taking off: the clips in the reverse order of putting it on, each backwards; on the body until the
                // first one put on's moment (DressOn) comes round backwards, then in the hands until the game drops it.
                if (d.old == null || d.oldKinds == null)
                {
                    return;
                }
                Part(d, d.oldKinds, elapsed, d.removeTicks, false);
                d.apparel = d.old;
                d.putOn = false;
            }
            else
            {
                if (d.newKinds == null)
                {
                    return;
                }
                Part(d, d.newKinds, elapsed - d.removeTicks, d.total - d.removeTicks, true);
                d.apparel = apparel;
                d.putOn = true;
            }
            d.inHands = !d.onBody;
        }

        // Plays kinds (the clips for putting the apparel on, in order) over ticks, at elapsed into it: forward when
        // putting it on, else the other way, each clip a share of the time. Sets the clip, its phase and whether the
        // apparel is on the body: from the first clip's DressOn on (putting it on), until it (taking it off).
        private static void Part(Dress d, string[] kinds, float elapsed, float ticks, bool on)
        {
            int n = kinds.Length;
            float each = Mathf.Max(1f, ticks / n);
            int i = Mathf.Min(n - 1, (int)(elapsed / each));
            float local = elapsed - i * each;
            int index = on ? i : n - 1 - i;   // the clip, in putting-on order
            string clip = kinds[index];
            float u = Mathf.Clamp01(local / each);
            float shown;
            float phase = Stretched(clip, on ? u : 1f - u, each, out shown);
            d.clip = clip;
            d.phase = Mathf.Min(phase, DressLast);
            // (shown: how far through the clip by its own clock, from the start, loops counted once)
            d.onBody = index > 0 || shown >= DressOnOf(clip);
        }

        private static float DressOnOf(string clip)
        {
            float on;
            return DressOn.TryGetValue(clip, out on) ? on : 0.5f;
        }

        // The clip's phase at u (0 .. 1) of a play lasting ticks. Squeezed evenly into a wait shorter than its own length;
        // in a longer one its dress_loop (the tugging and settling) keeps its own pace and the rest of the clip is slowed
        // to fill the time, as a stretch repeated to fill it (power armour's long wait) looked like fidgeting. shown: the
        // phase, kept for the callers that once told it from a repeated phase.
        private static float Stretched(string clip, float u, float ticks, out float shown)
        {
            float own = skin.SecondsOf(clip) * 60f;
            float from, to;
            if (own <= 0f || ticks <= own || !skin.DressLoopOf(clip, out from, out to) || to <= from)
            {
                shown = u;
                return u;
            }
            float loop = (to - from) * own;
            float slow = (ticks - loop) / (own - loop);   // how much slower than its own pace the rest is played
            float x = u * ticks;   // ticks into it
            float start = from * own * slow;
            if (x < start)
            {
                shown = x / slow / own;
            }
            else if (x < start + loop)
            {
                shown = from + (x - start) / own;
            }
            else
            {
                shown = to + (x - start - loop) / slow / own;
            }
            shown = Mathf.Clamp01(shown);
            return shown;
        }

        // The clips for putting on apparel of this def, in order, by what it covers and its layers; null if the export has
        // none for it. Cached per def.
        private static string[] DressKindsOf(ThingDef def)
        {
            string[] kinds;
            if (dressKinds.TryGetValue(def, out kinds))
            {
                return kinds;
            }
            ApparelProperties a = def.apparel;
            bool torso = false, legs = false, head = false;
            List<BodyPartGroupDef> groups = a != null ? a.bodyPartGroups : null;
            for (int i = 0; groups != null && i < groups.Count; i++)
            {
                string g = groups[i].defName;
                torso |= g == "Torso" || g == "Shoulders" || g == "Arms";
                legs |= g == "Legs";
                head |= g == "FullHead" || g == "UpperHead" || g == "Eyes" || g == "Teeth" || g == "Mouth" || g == "Neck";
            }
            List<ApparelLayerDef> layers = a != null ? a.layers : null;
            bool belt = layers != null && layers.Contains(ApparelLayerDefOf.Belt);
            bool shirt = layers != null && layers.Contains(ApparelLayerDefOf.OnSkin) && !layers.Contains(ApparelLayerDefOf.Shell);
            string upper = shirt ? "dress_over" : "dress_coat";
            List<string> list = new List<string>();
            if (belt)
            {
                list.Add("dress_belt");
            }
            else if (torso && legs && layers != null && (layers.Contains(ApparelLayerDefOf.OnSkin) || layers.Contains(ApparelLayerDefOf.Middle)))
            {
                list.Add("dress_legs");   // (a whole suit: stepped into, then the arms in or pulled over the head; a long coat
                                          // on the shell layer alone, a duster or a parka, is only swung on, below)
                list.Add(upper);
            }
            else if (torso)
            {
                list.Add(upper);   // (and a long coat reaching the legs)
            }
            else if (legs)
            {
                list.Add("dress_legs");
            }
            else if (head)
            {
                list.Add("dress_head");
            }
            else
            {
                list.Add("dress_belt");   // (anything else, packs, webbing and the like: fastened at the waist)
            }
            list.RemoveAll(c => !skin.Has(c));
            kinds = list.Count > 0 ? list.ToArray() : null;
            dressKinds[def] = kinds;
            return kinds;
        }

        private static readonly Dictionary<ThingDef, string[]> dressKinds = new Dictionary<ThingDef, string[]>();

        // For WorkOf: the dress clip as the pawn's work kind, facing the apparel it puts on (the game turns it to it) or
        // the way it faces; null when it isn't dressing.
        private static string DressWorkOf(Pawn pawn, out float face)
        {
            face = pawn.Rotation.AsAngle;
            Dress d = Dressed(pawn);
            if (d == null)
            {
                return null;
            }
            if (d.putOn && d.apparel != null && d.apparel.SpawnedOrAnyParentSpawned)
            {
                Vector3 to = Api.DrawnDelta(pawn.DrawPos, d.apparel.PositionHeld.ToVector3Shifted());
                if (to.x * to.x + to.z * to.z > 0.04f)
                {
                    face = to.AngleFlat();
                }
            }
            return d.clip;
        }

        // For the autotest: what the pawn is dressing with now, and whether that is on the body or in the hands.
        internal static bool DressingNow(Pawn pawn, out Apparel apparel, out bool onBody, out bool inHands)
        {
            Dress d = Dressed(pawn);
            apparel = d != null ? d.apparel : null;
            onBody = d != null && d.onBody;
            inHands = d != null && d.inHands;
            return d != null;
        }

        private static bool IsDressKind(string kind)
        {
            return kind != null && kind.StartsWith("dress_");
        }

        // The phase of the dress clip the pawn plays, 0 (its first frame, the idle's) once it is no longer dressing.
        private static float DressPhase(Pawn pawn, string clip)
        {
            Dress d = Dressed(pawn);
            if (d == null || d.clip != clip)
            {
                return 0f;
            }
            int frames = skin.FramesOf(clip);
            return d.phase * (frames - 1) / Mathf.Max(1, frames);
        }

        // The apparel to draw for a dressing pawn: worn, with the apparel it is putting on once that is on the body, and
        // without what it is taking off once that is off. Otherwise worn itself.
        private static readonly List<Apparel> dressedBuffer = new List<Apparel>();
        private static List<Apparel> DressingApparel(Pawn pawn, List<Apparel> worn)
        {
            Dress d = Dressed(pawn);
            if (d == null || d.apparel == null)
            {
                return worn;
            }
            bool wornNow = worn.Contains(d.apparel);
            if (d.onBody == wornNow)
            {
                return worn;
            }
            dressedBuffer.Clear();
            dressedBuffer.AddRange(worn);
            if (d.onBody)
            {
                dressedBuffer.Add(d.apparel);
            }
            else
            {
                dressedBuffer.Remove(d.apparel);
            }
            return dressedBuffer;
        }

        // Draws the apparel a dressing pawn holds (before it goes on, or once it is off): the chibi's own garment for it
        // (DrawHeldGarment), else as IsoRim draws it on the ground between the hands; and hides the copy on the ground
        // meanwhile (until the game puts it on). True if it drew it.
        private static bool DrawDressItem(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size)
        {
            Dress d = Dressed(pawn);
            if (d == null || d.apparel == null || !IsDressKind(ease.workKind) || ease.work < 0.5f)
            {
                return false;
            }
            bool hat = d.clip == "dress_head";
            // (a hat is picked up off the ground only at HatHeld; anything else is in the hands from the start)
            bool held = d.inHands && (!hat || d.phase >= HatHeld);
            if (d.putOn && d.apparel.Spawned && (held || d.onBody))
            {
                HideWhileDressing(pawn, d.apparel);
            }
            if (!held)
            {
                return false;
            }
            Vector3 wristR, wristL;
            if (!skin.JointOf(fig, "hand.R", out wristR) || !skin.JointOf(fig, "hand.L", out wristL))
            {
                return false;
            }
            try
            {
                if (DrawHeldGarment(d, hat, fig, wristR, wristL, at, yaw, size))
                {
                    return true;
                }
                Vector3 hands = (wristR + wristL) * 0.5f;
                Vector3 hold = at + Quaternion.AngleAxis(yaw, Vector3.up) * (hands * size);
                return Api.DrawItem(d.apparel, hold, yaw, DressItemScale * Mathf.Min(1f, size));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Drawing apparel in the hands failed: " + e, 0x4d3a7e12);
                return false;
            }
        }

        // The garment for apparel being put on or taken off, drawn as a model at rest in the pawn's shape (AtRest, cached
        // per garment and shape): a hat by its brim in the right hand and then going onto the head (Hat*), anything else
        // held up between the hands (HeldShrink). False if the apparel has no garment.
        private static bool DrawHeldGarment(Dress d, bool hat, SkinnedFigure.Posed fig, Vector3 wristR, Vector3 wristL, Vector3 at, float yaw, float size)
        {
            int g = GroundGarmentOf(d.apparel.def);
            if (g <= 0 || fig.shape == null)
            {
                return false;
            }
            DressModel hm = DressModelOf(g, fig.shape);
            if (hm == null || hm.model == null)
            {
                return false;
            }
            Matrix4x4 place;
            if (hat)
            {
                Vector3 fingers;
                Vector3 grip = skin.JointOf(fig, "hand.R.001", out fingers) ? (wristR + fingers) * 0.5f : wristR;
                float pitch = Keyed(HatAt, HatPitch, d.phase), roll = Keyed(HatAt, HatRoll, d.phase);
                Quaternion tipped = Quaternion.Euler(pitch, 0f, roll);
                Vector3 front = grip;
                Quaternion turn = tipped;
                Matrix4x4 head;
                if (d.phase > HatRelease && skin.BoneOf(fig, "spine.006", out head))
                {
                    // Off the hand and onto the head: its front brim from the hand to where it is worn over an arc, its
                    // turn from the last held to the head's.
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HatRelease, HatLands, d.phase));
                    Quaternion releasedTurn = Quaternion.Euler(HatPitch[HatPitch.Length - 1], 0f, HatRoll[HatRoll.Length - 1]);
                    Quaternion worn = Quaternion.LookRotation(head.GetColumn(2), head.GetColumn(1));
                    front = Vector3.Lerp(grip, head.MultiplyPoint3x4(hm.front), k) + Vector3.up * (HatArc * 4f * k * (1f - k));
                    turn = Quaternion.Slerp(releasedTurn, worn, k);
                }
                place = Matrix4x4.TRS(front, turn, Vector3.one) * Matrix4x4.Translate(-hm.front);
            }
            else
            {
                Vector3 shrink;
                if (!HeldShrink.TryGetValue(d.clip, out shrink))
                {
                    shrink = new Vector3(1f, 1f, 0.5f);
                }
                Vector3 top = new Vector3((hm.min.x + hm.max.x) * 0.5f, hm.max.y, (hm.min.z + hm.max.z) * 0.5f);
                Vector3 hands = (wristR + wristL) * 0.5f + Vector3.forward * ((hm.max.z - hm.min.z) * shrink.z * 0.5f);
                place = Matrix4x4.Translate(hands) * Matrix4x4.Scale(shrink) * Matrix4x4.Translate(-top);
            }
            Color colour = skin.Tinted(g, d.apparel) ? Api.ApparelColour(d.apparel) : Color.white;
            Api.DrawModel(hm.model, at, yaw, Matrix4x4.Scale(new Vector3(size, size, size)) * place, colour);
            return true;
        }

        // A value keyed at places along the clip (at, rising), eased between them and held past either end.
        private static float Keyed(float[] at, float[] value, float t)
        {
            if (t <= at[0])
            {
                return value[0];
            }
            for (int i = 1; i < at.Length; i++)
            {
                if (t <= at[i])
                {
                    return Mathf.Lerp(value[i - 1], value[i], Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(at[i - 1], at[i], t)));
                }
            }
            return value[value.Length - 1];
        }

        private sealed class DressModel
        {
            public ModelHandle model;
            public Vector3 front, min, max;
        }
        private static readonly Dictionary<SkinnedFigure.Shape, Dictionary<int, DressModel>> dressModels = new Dictionary<SkinnedFigure.Shape, Dictionary<int, DressModel>>();

        private static DressModel DressModelOf(int g, SkinnedFigure.Shape shape)
        {
            Dictionary<int, DressModel> byGarment;
            if (!dressModels.TryGetValue(shape, out byGarment))
            {
                if (dressModels.Count > 64)
                {
                    dressModels.Clear();   // (only a cache; shapes are many with varied bodies)
                }
                byGarment = new Dictionary<int, DressModel>();
                dressModels[shape] = byGarment;
            }
            DressModel hm;
            if (!byGarment.TryGetValue(g, out hm))
            {
                hm = new DressModel();
                hm.model = skin.AtRest(g, shape, out hm.front, out hm.min, out hm.max);
                byGarment[g] = hm;
            }
            return hm;
        }

        // The apparel on the ground hidden while a pawn puts it on (by Flings' hiding, which keeps it out of the map's mesh),
        // by the pawn doing so; shown again by SweepDressing once that pawn is no longer at it.
        private static readonly Dictionary<Thing, Pawn> dressHidden = new Dictionary<Thing, Pawn>();

        private static void HideWhileDressing(Pawn pawn, Thing thing)
        {
            Pawn was;
            if (dressHidden.TryGetValue(thing, out was) && was == pawn)
            {
                return;
            }
            dressHidden[thing] = pawn;
            Flings.Hide(thing, true);
        }

        // Once a frame (from Flings' update): anything hidden for a pawn no longer putting it on is shown again.
        private static int dressSwept = -1;
        private static readonly List<Thing> dressShown = new List<Thing>();
        internal static void SweepDressing()
        {
            if (dressHidden.Count == 0 || dressSwept == Time.frameCount)
            {
                return;
            }
            dressSwept = Time.frameCount;
            dressShown.Clear();
            foreach (KeyValuePair<Thing, Pawn> pair in dressHidden)
            {
                Pawn pawn = pair.Value;
                Job job = pawn != null && !pawn.Destroyed ? pawn.CurJob : null;
                bool still = job != null && pawn.jobs.curDriver is JobDriver_Wear && job.targetA.Thing == pair.Key && pair.Key.Spawned;
                if (!still)
                {
                    dressShown.Add(pair.Key);
                }
            }
            for (int i = 0; i < dressShown.Count; i++)
            {
                dressHidden.Remove(dressShown[i]);
                Flings.Hide(dressShown[i], false);
            }
        }
    }

    public sealed partial class SkinnedFigure
    {
        // Each dress_<kind> clip's tugging stretch, kept at its own pace (anim.txt's dress_loop lines): from and to, shares of the clip.
        private readonly Dictionary<string, Vector2> dressLoops = new Dictionary<string, Vector2>();

        public bool DressLoopOf(string clip, out float from, out float to)
        {
            Vector2 v;
            bool found = dressLoops.TryGetValue(clip, out v);
            from = v.x;
            to = v.y;
            return found;
        }

        private void ReadDressLoop(string[] p)
        {
            dressLoops[p[1]] = new Vector2(Num(p[2]), Num(p[3]));
        }
    }
}
