using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A colonist's armour helmet while it isn't drafted (the helmetStow option; skinned figure only). Drawing only: the game
    // keeps it worn. Worn: as ever. Hidden: not drawn while undrafted, at once. OnBack: taken off as it is undrafted by the
    // hat's dress_head clip played backwards (Top, from the waist up, so on the move too), once the weapon is put away;
    // from the hand it goes onto the back, smaller (HelmetBackScale: as if the head were shrunk halfway on from the
    // player's head shrink to all of it), whole on the pack slot's bone (an open helmet with its opening against the back,
    // its top out and its front up; one covering the whole head upright, facing the side: walk.HELMET_BACK), past the clothes, a
    // pack, a shield and the weapons on the back there; drafted, it is taken back and put on the same way forward, before
    // the weapon is drawn. Ease.helmOut is how far through dress_head it is: 1 on the head, 0 on the back; it is drawn worn
    // from HatLands up (DressedIn leaves it out below that). Off its feet, carrying, at work, in the water or aiming, it
    // goes at once. Downed or dead it stays as it was.
    public static partial class WalkFigures
    {
        private const string HelmClip = "dress_head";
        private const float HelmHurry = 1.5f;     // played this many times as fast as dress_head's own length
        private const float HelmEdgeIn = 0.1f;    // of the clip at its back end over which it is eased in from what the arms were doing
        private const float HelmEdgeOut = 0.15f;
        private const float HelmSwingOut = 0.22f;  // model units: how far out to the figure's right (+x) the slide between the back and the hand bows at its middle
        private const float HelmSwingUp = 0.06f;   // and up  // and at its head end (where the hands are back down after settling it)

        private static HelmetStow HelmetsUndrafted
        {
            get { return SettingsMod.Settings != null ? SettingsMod.Settings.helmetStow : HelmetStow.Worn; }
        }

        private static readonly Dictionary<ThingDef, bool> isHelmet = new Dictionary<ThingDef, bool>();

        private static bool HelmetDef(ThingDef def)
        {
            bool h;
            if (!isHelmet.TryGetValue(def, out h))
            {
                h = SkinnedFigure.IsHelmet(def) || SkinnedFigure.IsHelmet(SkinnedFigure.AdultDefOf(def));   // (Biotech's kid helmet is drawn as the simple helmet)
                isHelmet[def] = h;
            }
            return h;
        }

        // The helmet the option takes off this pawn, or null: the option at Worn, not a colonist that can be drafted, or no
        // armour helmet (the game's ArmorHeadgear category, SkinnedFigure.IsHelmet) worn.
        private static Apparel StowableHelmet(Pawn pawn)
        {
            if (HelmetsUndrafted == HelmetStow.Worn || pawn.drafter == null || pawn.Faction != Faction.OfPlayer || pawn.apparel == null)
            {
                return null;
            }
            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (HelmetDef(worn[i].def))
                {
                    return worn[i];
                }
            }
            return null;
        }

#if AUTOTEST
        // For the autotest: whether the pawn's helmet is drawn worn, and where it is.
        internal static bool HelmetDrawnWorn(Pawn pawn)
        {
            Apparel helm = pawn.apparel != null ? pawn.apparel.WornApparel.Find(a => HelmetDef(a.def)) : null;
            return helm != null && WithoutStowedHelmet(pawn, pawn.apparel.WornApparel).Contains(helm);
        }

        // Puts the pawn's helmet on the back without the animation (a helmet swapped in mid-run counts as just put on).
        internal static void HelmetToBack(Pawn pawn)
        {
            Apparel helm = pawn.apparel.WornApparel.Find(a => HelmetDef(a.def));
            Ease e;
            if (helm != null && eases.TryGetValue(pawn.thingIDNumber, out e))
            {
                e.helmThing = helm.thingIDNumber;
                e.helmOut = 0f;
            }
        }

        internal static string HelmetReport(Pawn pawn)
        {
            Apparel helm = pawn.apparel != null ? pawn.apparel.WornApparel.Find(a => HelmetDef(a.def)) : null;
            Ease e;
            eases.TryGetValue(pawn.thingIDNumber, out e);
            return "mode " + HelmetsUndrafted + ", helmet " + (helm != null ? helm.def.defName : "none") + ", garment "
                + (helm != null ? GroundGarmentOf(helm.def).ToString() : "-") + ", stowable " + (StowableHelmet(pawn) != null)
                + ", helmOut " + (e != null ? e.helmOut.ToString("F2") : "-") + ", drawn worn " + HelmetDrawnWorn(pawn);
        }
#endif

        private static readonly Dictionary<ThingDef, bool> isFullHelmet = new Dictionary<ThingDef, bool>();

        // Whether a helmet covers the whole head (power armour, plate, recon, a vacsuit's), so it is carried on the back
        // upright and facing the side rather than with its opening against the back (SkinnedFigure.HelmetOnBack).
        private static bool FullHelmet(ThingDef def)
        {
            bool full;
            if (!isFullHelmet.TryGetValue(def, out full))
            {
                List<BodyPartGroupDef> groups = def.apparel != null ? def.apparel.bodyPartGroups : null;
                full = groups != null && groups.Contains(BodyPartGroupDefOf.FullHead);
                isFullHelmet[def] = full;
            }
            return full;
        }

        // Whether the pawn wears its helmet on the head now: drafted, unless the option hides helmets always.
        private static bool WantsHelmet(Pawn pawn)
        {
            return HelmetsUndrafted != HelmetStow.AlwaysHidden && pawn.Drafted;
        }

        // How far the helmet is through dress_head (1 on the head), for a pawn with no ease for it yet: on the head if wanted.
        private static float HelmOutOf(Pawn pawn, Apparel helm)
        {
            Ease e;
            if (eases.TryGetValue(pawn.thingIDNumber, out e) && e.helmThing == helm.thingIDNumber)
            {
                return e.helmOut;
            }
            return WantsHelmet(pawn) || (pawn.Dead && HelmetsUndrafted != HelmetStow.AlwaysHidden) ? 1f : 0f;
        }

        // Whether the helmet animates between the back and the head (OnBack, an export with the clip and a garment for it).
        private static bool HelmAnimates(Apparel helm)
        {
            return HelmetsUndrafted == HelmetStow.OnBack && skin != null && skin.Has(HelmClip) && GroundGarmentOf(helm.def) > 0;
        }

        // For EaseOf: the helmet is still being put on, so the weapon waits (its hands are wanted) until it is.
        private static bool HelmetFirst(Pawn pawn)
        {
            Ease e;
            Apparel helm;
            return eases.TryGetValue(pawn.thingIDNumber, out e) && e.helmOut < 1f && (helm = StowableHelmet(pawn)) != null
                && e.helmThing == helm.thingIDNumber && WantsHelmet(pawn) && HelmAnimates(helm);
        }

        // Moves the helmet between the back and the head (Ease.helmOut) by game time. can: on its feet with its hands free
        // for it; rush: aiming or striking, when it goes on at once.
        private static void StepHelm(Pawn pawn, Ease e, bool can, bool rush, bool recent, float game)
        {
            Apparel helm = StowableHelmet(pawn);
            if (helm == null)
            {
                e.helmOut = 1f;
                e.helmThing = -1;
                return;
            }
            bool want = WantsHelmet(pawn);
            if (helm.thingIDNumber != e.helmThing)
            {
                // First seen: where it would be. A helmet just put on (the Wear job) is on the head, and comes off from there.
                e.helmOut = e.helmThing != 0 && recent ? 1f : want ? 1f : 0f;
                e.helmThing = helm.thingIDNumber;
            }
            if ((pawn.Dead || pawn.Downed) && HelmetsUndrafted != HelmetStow.AlwaysHidden)
            {
                return;   // as it was when it went down
            }
            float target = want ? 1f : 0f;
            float seconds = skin != null ? skin.SecondsOf(HelmClip) : 0f;
            if (!HelmAnimates(helm) || !can || rush || !recent || seconds <= 0f)
            {
                e.helmOut = target;
                return;
            }
            if (!want && e.helmOut >= 1f && (e.handHeld || e.drawAt >= 0f))
            {
                return;   // taken off once the weapon is away
            }
            e.helmOut = Mathf.MoveTowards(e.helmOut, target, game * HelmHurry / seconds);
        }

        // Lays dress_head over the figure (Top, from the waist up) while the helmet goes between the back and the head.
        private static void LayHelm(Ease ease, SkinnedFigure.Posed fig, float w)
        {
            if (ease.helmThing <= 0 || ease.helmOut <= 0f || ease.helmOut >= 1f || w <= 0f || !skin.Has(HelmClip))
            {
                return;
            }
            float edge = Mathf.SmoothStep(0f, 1f, ease.helmOut / HelmEdgeIn) * Mathf.SmoothStep(0f, 1f, (1f - ease.helmOut) / HelmEdgeOut);
            int frames = skin.FramesOf(HelmClip);
            skin.Top(fig, HelmClip, ease.helmOut * (frames - 1) / frames, w * edge);
        }

        // The worn apparel to draw without the helmet while it is off the head (below HatLands of the way on).
        private static readonly List<Apparel> withoutHelm = new List<Apparel>();
        private static List<Apparel> WithoutStowedHelmet(Pawn pawn, List<Apparel> all)
        {
            Apparel helm = all != null ? StowableHelmet(pawn) : null;
            if (helm == null || HelmOutOf(pawn, helm) >= HatLands || !all.Contains(helm))
            {
                return all;
            }
            withoutHelm.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != helm)
                {
                    withoutHelm.Add(all[i]);
                }
            }
            return withoutHelm;
        }

        // Draws the helmet while it is off the head: on the back, sliding from there into the right hand over the clip's
        // first HatHeld, then held by its brim and flown onto the head as the Wear job puts a hat on (HatPlace). After
        // DrawHolstered, whose slots it reads for the weapons on the back.
        private static void DrawStowedHelmet(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, GarmentMask mask, Vector3 at, float yaw, float size)
        {
            if (HelmetsUndrafted != HelmetStow.OnBack || fig.shape == null)
            {
                return;
            }
            Apparel helm = StowableHelmet(pawn);
            if (helm == null || ease.helmThing != helm.thingIDNumber || ease.helmOut >= HatLands)
            {
                return;
            }
            int g = GroundGarmentOf(helm.def);
            DressModel hm = g > 0 ? DressModelOf(g, fig.shape) : null;
            if (hm == null || hm.model == null)
            {
                return;
            }
            Vector3 pivot;
            Matrix4x4 back;
            if (!skin.HelmetOnBack(fig, FullHelmet(helm.def), hm.min, hm.max, BackWeaponsOuter(fig, mask, size), out back, out pivot))
            {
                return;
            }
            Matrix4x4 place = back;
            float phase = ease.helmOut;
            Vector3 wristR;
            Matrix4x4 hand;
            if (phase > 0f && skin.JointOf(fig, "hand.R", out wristR) && HatPlace(fig, hm, Mathf.Max(phase, HatHeld), wristR, out hand))
            {
                if (phase >= HatHeld)
                {
                    place = hand;
                }
                else
                {
                    // Straight between the two it went through the head: bowed out round the right shoulder and up.
                    Matrix4x4 p = Matrix4x4.Translate(pivot), up = Matrix4x4.Translate(-pivot);
                    float u = Mathf.SmoothStep(0f, 1f, phase / HatHeld);
                    float bow = 4f * u * (1f - u);
                    place = Matrix4x4.Translate(new Vector3(HelmSwingOut, HelmSwingUp, 0f) * bow) * Between(back * p, hand * p, u) * up;
                }
            }
            Color colour = skin.Tinted(g, helm) ? IsoRim.Api.ApparelColour(helm) : Color.white;
            IsoRim.Api.DrawModel(hm.model, at, yaw, Matrix4x4.Scale(new Vector3(size, size, size)) * place, colour);
        }

        // How far out the weapons holstered on the back stand (as ShieldOverWeapons, but from the slots DrawHolstered has
        // just filled), negative infinity for none.
        private static float BackWeaponsOuter(SkinnedFigure.Posed fig, GarmentMask mask, float size)
        {
            float outer = float.NegativeInfinity;
            if (!Holsters || skin.Holsters == 0 || !HeldWeapons3D || slotWeapon == null || slotWeapon.Length != skin.Holsters)
            {
                return outer;
            }
            for (int s = 0; s < slotWeapon.Length; s++)
            {
                if (slotWeapon[s] != null && skin.UnderShield(s))
                {
                    outer = Mathf.Max(outer, skin.HolsterOuter(fig, s, mask, slotExtra[s]) + HalfDepth(slotWeapon[s]) / size);
                }
            }
            return outer;
        }
    }

    public sealed partial class SkinnedFigure
    {
        private const int HelmKey = 1000;          // holsterPush's key for a helmet on the back, past every shield's
        private const float HelmSpots = 0.8f;      // of its footprint's half width and height: how far out across it Clearance looks
        private readonly Dictionary<int, float> shieldDepths = new Dictionary<int, float>();

        // Where a helmet is carried on the back, by kind (anim.txt's helmet_back lines, walk.HELMET_BACK): an open one (a
        // simple or flak helmet) with its opening against the back, its top out and its front up; a full one (power armour,
        // plate, recon: covering the whole head) upright, facing the side. Each: the bone it rides (-1: the pack slot's), the
        // place of its middle at rest (model space) before the push past what is worn, a share to draw it at on top of
        // HelmetBackScale and its turn from upright facing forward. Without a line, the numbers walk.py had.
        private sealed class HelmPlace
        {
            public int bone = -1;
            public Vector3 at;
            public float scale = 0.8f;
            public Quaternion turn;
        }
        private readonly HelmPlace helmOpen = new HelmPlace
        {
            at = new Vector3(0f, 0.68f, -0.143f), turn = new Quaternion(-0.7071068f, 0f, 0f, 0.7071068f),   // top out of the back, front up
        };
        private readonly HelmPlace helmFull = new HelmPlace
        {
            at = new Vector3(0f, 0.66f, -0.195f), turn = new Quaternion(0f, -0.7071068f, 0f, 0.7071068f),  // upright, facing the figure's left
        };

        private void ReadHelmetBack(string[] p)
        {
            int b;
            if (p.Length > 10 && boneIndex.TryGetValue(p[2], out b))
            {
                HelmPlace h = p[1] == "full" ? helmFull : helmOpen;
                h.bone = b;
                h.at = Vec(p, 3);
                h.scale = Num(p[6]);
                h.turn = new Quaternion(Num(p[7]), Num(p[8]), Num(p[9]), Num(p[10]));
            }
        }

        // The grown-up apparel a child's borrows its garment from (AdultOf), or null.
        public static ThingDef AdultDefOf(ThingDef def)
        {
            string adult;
            return def != null && AdultOf.TryGetValue(def.defName, out adult) ? DefDatabase<ThingDef>.GetNamedSilentFail(adult) : null;
        }

        // The size a helmet carried on the back is drawn, of its size worn: as if the head were shrunk halfway on from the
        // shrink this shape has to the whole Shrinkhead key, so a full chibi head's helmet is drawn as for a half shrunk
        // one, and one already shrunk all the way keeps its size. (HelmetBack's own share goes on top.)
        public float HelmetBackScale(Shape s)
        {
            float shrink = 0f;
            for (int i = 0; s != null && s.mix != null && i < s.mix.Count; i++)
            {
                if (s.mix[i].Key == ShrinkKey)
                {
                    shrink = s.mix[i].Value;
                }
            }
            if (shrink >= 1f)
            {
                return 1f;
            }
            float now = SettingsMod.HeadSize(shrink);
            return now > 0f ? SettingsMod.HeadSize((shrink + 1f) * 0.5f) / now : 1f;
        }

        // Where a helmet (its box at rest, min and max, model space as worn) goes on the back in the pose End left: place
        // takes its rest model there, pivot (its middle, in its rest space) being what is put at its kind's place (full: a
        // helmet covering the whole head), turned as that has it, at HelmetBackScale times its scale, on its bone, pushed out past the clothes and body there by Clearance over its footprint, a backpack or equipment
        // pack (PackOuter), a shield on the back and the weapons holstered there (over), each GearGap clear, measured
        // from its inner face. False with no pack slot.
        public bool HelmetOnBack(Posed p, bool full, Vector3 min, Vector3 max, float over, out Matrix4x4 place, out Vector3 pivot)
        {
            place = Matrix4x4.identity;
            pivot = (min + max) * 0.5f;
            GearSlot slot = GearOf("pack");
            if (slot == null)
            {
                return false;
            }
            HelmPlace hp = full ? helmFull : helmOpen;
            Vector3 helmAt = hp.at;
            Quaternion helmTurn = hp.turn;
            int bone = hp.bone >= 0 ? hp.bone : slot.place.bone;
            Vector3 way = slot.place.outward;
            float k = HelmetBackScale(p.shape) * hp.scale;
            Vector3 a1 = Vector3.Cross(way, Vector3.up).normalized, a2 = Vector3.Cross(way, a1).normalized;
            float inner = 0f, e1 = 0f, e2 = 0f;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new Vector3((c & 1) != 0 ? max.x : min.x, (c & 2) != 0 ? max.y : min.y, (c & 4) != 0 ? max.z : min.z);
                Vector3 v = helmTurn * ((corner - pivot) * k);
                inner = Mathf.Min(inner, Vector3.Dot(v, way));
                e1 = Mathf.Max(e1, Mathf.Abs(Vector3.Dot(v, a1)));
                e2 = Mathf.Max(e2, Mathf.Abs(Vector3.Dot(v, a2)));
            }
            Holster rim = new Holster
            {
                name = "helmet_back", size = "gear", bone = bone, at = helmAt, outward = way,
                spots = new[] { Vector3.zero, a1 * (e1 * HelmSpots), -a1 * (e1 * HelmSpots), a2 * (e2 * HelmSpots), -a2 * (e2 * HelmSpots) },
            };
            float face = Vector3.Dot(helmAt, way) + inner;
            float push = Clearance(p.shape, p.wornMask, rim, HelmKey) + GearGap;
            float packs = PackOuter(p, way);
            if (!float.IsNegativeInfinity(packs))
            {
                push = Mathf.Max(push, packs + GearGap - face);
            }
            float shield = ShieldOuter(p, way);
            if (!float.IsNegativeInfinity(shield))
            {
                push = Mathf.Max(push, shield + GearGap - face);
            }
            if (!float.IsNegativeInfinity(over))
            {
                push = Mathf.Max(push, over + GearGap - face);
            }
            place = p.whole[bone] * Matrix4x4.Translate(p.shape.boneMove[bone] + way * push + helmAt)
                * Matrix4x4.TRS(Vector3.zero, helmTurn, new Vector3(k, k, k)) * Matrix4x4.Translate(-pivot);
            return true;
        }
        // How far out along way a shield carried on the back stands (model units, before the bone's move for the shape),
        // negative infinity for none there (none worn, or in the hand).
        private float ShieldOuter(Posed p, Vector3 way)
        {
            if (p.shieldOut >= ShieldGrab)
            {
                return float.NegativeInfinity;
            }
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((p.wornMask & GarmentBit(g)) == 0 || !pieces[g].heldOnBack)
                {
                    continue;
                }
                PackRig rig = PackRigOf(g);
                if (rig == null)
                {
                    return float.NegativeInfinity;
                }
                float depth;
                if (!shieldDepths.TryGetValue(g, out depth))
                {
                    depth = float.NegativeInfinity;
                    Vector3[] rest = pieces[g].rest;
                    for (int i = 0; i < rest.Length; i++)
                    {
                        depth = Mathf.Max(depth, Vector3.Dot(rest[i], rig.place.outward));
                    }
                    shieldDepths[g] = depth;
                }
                return (depth + ShieldPush(p, g, rig)) * Vector3.Dot(rig.place.outward, way);
            }
            return float.NegativeInfinity;
        }
    }
}
