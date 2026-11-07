using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Combat Extended's shields (its melee and ballistic shields, on its Shield apparel layer; anim_scripts/make_shields.py),
    // and any other apparel with a garment exported with the held line: held in the left fist, riding hand.L whole. Shown
    // only when CE would draw the shield (ShieldShows, WornGarments), and then the guard (the shield clip, walk.shield_at:
    // the left fist out in front of the left shoulder) is laid over the rest with Top, the left arm only, on its feet with
    // its hands free for it (ShieldUp, eased by ShieldEase), giving way to a reload, a cast, a stagger and a daze. While it
    // is up the hip gear stays in its slot, as the left hand is busy. A shield hides nothing of the body or the clothes and
    // is in no clearance (SkinnedFigure.HeldMask). Skinned figure only.
    // A shield exported with a held_at line (placed on the back in its .glb) is carried on the back the rest of the time,
    // whole on the pack slot's bone, pushed out past everything else there: the clothes, a backpack, an equipment pack and
    // the weapons holstered on the back (ShieldPush, ShieldOverWeapons). Without one it is drawn only in the hand, as before.
    public sealed partial class SkinnedFigure
    {
        private const int ShieldKey = 400;   // holsterPush's keys for a shield on the back, past the packs' and the gear's
        private readonly Dictionary<Piece, PieceShape> wholeShapes = new Dictionary<Piece, PieceShape>();   // each placed whole, in no body's shape

        // Whether this garment is a shield carried on the back when not in the hand (its held_at line).
        public bool CarriedOnBack(int piece)
        {
            return piece > 0 && piece < pieces.Length && pieces[piece].heldOnBack;
        }

        // Whether any garment in mask is carried on the back so.
        public bool CarriesShield(GarmentMask mask)
        {
            for (int g = 1; mask != 0 && g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & (GarmentBit(g))) != 0 && pieces[g].heldOnBack)
                {
                    return true;
                }
            }
            return false;
        }

        public float ShieldGrab = 0.45f;    // the share of shield_draw at which the hand closes on a shield's edge on the back (anim.txt's shield_timing)
        public float ShieldSlide = 0.12f;   // and the share after that over which the shield goes from the back onto the hand

        // Where a shield carried on the back is this draw: taken, how far through being taken into the hand (0 on the back, 1
        // in the guard; between, through its shield_draw clip), and over, how far out what stands under it on the back does
        // (the weapons holstered there, ShieldOverWeapons). After Wearing, before anything is drawn.
        public void Shielding(Posed p, float taken, float over)
        {
            if (taken != p.shieldOut || over != p.shieldOver)
            {
                p.posedAt++;   // it is placed by these, not only by the bones
            }
            p.shieldOut = taken;
            p.shieldOver = over;
        }

        // The clip that takes this shield from the back into the guard (shield_draw_<garment>), or null: none exported, or
        // not carried on the back.
        public string ShieldDrawOf(int piece)
        {
            if (!CarriedOnBack(piece) || pieces[piece].heldGrip == null)
            {
                return null;
            }
            string name = pieces[piece].name;
            string clip = "shield_draw_" + (name.StartsWith("garment_") ? name.Substring("garment_".Length) : name);
            return Has(clip) ? clip : null;
        }

        private PieceShape WholeShape(Piece piece)
        {
            PieceShape shape;
            if (!wholeShapes.TryGetValue(piece, out shape))
            {
                shape = ShapePiece(piece, new List<KeyValuePair<string, float>>());
                wholeShapes[piece] = shape;
            }
            return shape;
        }

        // Poses a shield as one thing where it is (ShieldPlace), its shape the same on any body.
        private void Shield(Posed p, int g, PieceMeshes m)
        {
            if (m.posedAt != p.posedAt)
            {
                PlaceWhole(p, WholeShape(pieces[g]), ShieldPlace(p, g), m);
            }
        }

        // And its glass (the ballistic shield's viewport), with it.
        private void ShieldVisor(Posed p, Piece garment, PieceMeshes m)
        {
            if (m.posedAt != p.posedAt)
            {
                PlaceWhole(p, WholeShape(garment.visor), ShieldPlace(p, System.Array.IndexOf(pieces, garment)), m);
            }
        }

        // The matrix from a shield's rest place on the back to where it is in the pose End left: in the left fist (its
        // held_at, moved with the hand for the shape), or on the pack slot's bone pushed out by ShieldPush. With no pack
        // slot on the export, always in the fist. Part way through shield_draw (shieldOut), on the back until the hand
        // closes on its edge (ShieldGrab), then taken from there onto the hand as the clip holds it there (held_grip) over
        // ShieldSlide, turning in the hand from that to the guard's grip over the rest of the clip.
        private Matrix4x4 ShieldPlace(Posed p, int g)
        {
            PackRig rig = p.shieldOut >= 1f ? null : PackRigOf(g);
            int hand;
            bool hasHand = boneIndex.TryGetValue("hand.L", out hand);
            Matrix4x4 onHand = hasHand ? p.whole[hand] * Matrix4x4.Translate(p.shape.boneMove[hand]) : Matrix4x4.identity;
            if (rig == null)
            {
                return onHand * pieces[g].heldAt;
            }
            Holster h = rig.place;
            Matrix4x4 back = p.whole[h.bone] * Matrix4x4.Translate(p.shape.boneMove[h.bone] + h.outward * ShieldPush(p, g, rig));
            Matrix4x4? grip = pieces[g].heldGrip;
            if (p.shieldOut <= ShieldGrab || grip == null || !hasHand)
            {
                return back;
            }
            // Each blend turns the shield about its own middle (c), so it stays with the hand rather than swinging about the
            // model's origin.
            Matrix4x4 c = Matrix4x4.Translate(ShieldMiddle(g)), uc = Matrix4x4.Translate(-ShieldMiddle(g));
            float turned = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ShieldGrab, 1f, p.shieldOut));
            Matrix4x4 held = onHand * WalkFigures.Between(grip.Value * c, pieces[g].heldAt * c, turned);
            float taken = ShieldSlide > 0f ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ShieldGrab, ShieldGrab + ShieldSlide, p.shieldOut)) : 1f;
            return WalkFigures.Between(back * c, held, taken) * uc;
        }

        private readonly Dictionary<int, Vector3> shieldMiddles = new Dictionary<int, Vector3>();

        // The middle of a shield's rest places on the back (model space).
        private Vector3 ShieldMiddle(int g)
        {
            Vector3 m;
            if (!shieldMiddles.TryGetValue(g, out m))
            {
                Vector3[] rest = pieces[g].rest;
                for (int i = 0; i < rest.Length; i++)
                {
                    m += rest[i];
                }
                m = rest.Length > 0 ? m / rest.Length : Vector3.zero;
                shieldMiddles[g] = m;
            }
            return m;
        }

        // How far a shield's face goes out from its place on the back (model units): past the body and clothes under it,
        // keeping the stand-off it was placed with (Clearance), and outside everything else carried there: a backpack or
        // equipment pack (PackOuter) and the weapons holstered under it (shieldOver), each GearGap clear.
        private float ShieldPush(Posed p, int g, PackRig rig)
        {
            Holster h = rig.place;
            float face = Vector3.Dot(h.at, h.outward);
            float push = Clearance(p.shape, p.wornMask, h, ShieldKey + g) + GearGap;
            float packs = PackOuter(p, h.outward);
            if (!float.IsNegativeInfinity(packs))
            {
                push = Mathf.Max(push, packs + GearGap - face);
            }
            if (!float.IsNegativeInfinity(p.shieldOver))
            {
                push = Mathf.Max(push, p.shieldOver + GearGap - face);
            }
            return push;
        }
    }

    public static partial class WalkFigures
    {
        private const float ShieldEase = 4f;   // how fast a pawn brings its shield up into the guard (or lowers it), per real second
        private const float ShieldDrawIn = 0.15f;   // the share of shield_draw over which it is eased in from the arm's own pose

        // The worn shield carried on the back that is drawn here (its garment), or -1.
        private static int ShieldOnBack(Pawn pawn)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            for (int i = 0; skin != null && worn != null && i < worn.Count; i++)
            {
                int g = skin.GarmentOf(worn[i].def);
                if (skin.IsHeld(g) && skin.CarriedOnBack(g))
                {
                    return g;
                }
            }
            return -1;
        }

        // Moves a shield carried on the back between there and the guard (Ease.shieldOut, through its shield_draw clip by
        // game time, backwards to put it back) and eases the guard (Ease.shield) toward shielding once it is in the hand.
        // can: on its feet with its hands free for it; otherwise, or with no clip, it goes at once, as before.
        private static void StepShield(Pawn pawn, Ease e, bool can, float shielding, bool recent, float game)
        {
            int g = ShieldOnBack(pawn);
            string clip = g > 0 ? skin.ShieldDrawOf(g) : null;
            bool wanted = g > 0 && ShieldShows(pawn);
            float seconds = clip != null ? skin.SecondsOf(clip) : 0f;
            bool drawing = e.shieldOut < 1f;
            if (clip == null || !can || !recent || seconds <= 0f)
            {
                e.shieldOut = wanted ? 1f : 0f;
                e.shieldClip = null;
                e.shield = recent ? Mathf.MoveTowards(e.shield, shielding, ShieldEase * Time.deltaTime) : shielding;
                return;
            }
            e.shieldClip = clip;
            e.shieldOut = Mathf.MoveTowards(e.shieldOut, wanted ? 1f : 0f, game / seconds);
            if (e.shieldOut < 1f)
            {
                e.shield = 0f;   // the clip holds the arm, ending (or starting, going back) on the guard's first frame
            }
            else
            {
                e.shield = drawing ? shielding : Mathf.MoveTowards(e.shield, shielding, ShieldEase * Time.deltaTime);
            }
        }

        // Lays a shield's draw (shield_draw) over the figure with weight w (Top, the trunk and the left arm) while it comes
        // off the back or goes back.
        private static void LayShieldDraw(Ease ease, SkinnedFigure.Posed fig, float w)
        {
            if (ease.shieldClip == null || ease.shieldOut <= 0f || ease.shieldOut >= 1f || w <= 0f)
            {
                return;
            }
            int frames = skin.FramesOf(ease.shieldClip);
            skin.Top(fig, ease.shieldClip, ease.shieldOut * (frames - 1) / frames, w * Mathf.SmoothStep(0f, 1f, ease.shieldOut / ShieldDrawIn));
        }

        // How far a pawn's shield carried on the back is through being taken into the hand (Ease.shieldOut), for one with
        // no ease of its own yet (as carried): in the hand if shown.
        private static float ShieldOutOf(Pawn pawn)
        {
            Ease e;
            return eases.TryGetValue(pawn.thingIDNumber, out e) ? e.shieldOut : ShieldShows(pawn) ? 1f : 0f;
        }

        // Whether CE would draw the pawn's shield now (Apparel_Shield.drawShield, PawnRenderNodeWorker_Drafted): drafted, or
        // in a job or duty that shows the weapon (a raider's assault duty).
        // An outsider at peace with the colony keeps it on its back unless its job or an enemy near calls for it, as its
        // weapon (CarriesOpenly).
        private static bool ShieldShows(Pawn pawn)
        {
            if (!pawn.Spawned)
            {
                return false;
            }
            if (pawn.Drafted || (pawn.CurJob != null && pawn.CurJob.def.alwaysShowWeapon))
            {
                return true;
            }
            if (pawn.mindState == null || pawn.mindState.duty == null || !pawn.mindState.duty.def.alwaysShowWeapon)
            {
                return false;
            }
            return pawn.Faction == Faction.OfPlayer || (pawn.Faction != null && pawn.HostileTo(Faction.OfPlayer)) || ThreatNear(pawn);
        }

        private static readonly Dictionary<ThingDef, string> shieldStanceOf = new Dictionary<ThingDef, string>();
        private static string shieldUnarmed;
        private static bool shieldUnarmedKnown;

        // The drafted stance with a shield up for a weapon (null: bare hands), cached per ThingDef: one-handed (TypeOf a
        // handgun, a one-handed melee weapon or a grenade) or none, its own drafted_shield_<defName> (walk.shielded_entry:
        // its kind's with its own grip), else its kind's drafted_shield_<kind>; null for a two-handed weapon, or an export
        // without them, which keeps its plain stance.
        private static string ShieldClip(ThingDef weapon)
        {
            string clip;
            if (weapon == null ? shieldUnarmedKnown : shieldStanceOf.TryGetValue(weapon, out clip))
            {
                return weapon == null ? shieldUnarmed : shieldStanceOf[weapon];
            }
            clip = null;
            string type = weapon != null ? TypeOf(weapon) : null;
            if (weapon == null || type == "handgun" || type == "melee_1h" || type == "grenade")
            {
                string own = weapon != null ? "drafted_shield_" + WeaponName(weapon) : null;
                string kind = "drafted_shield_" + KindOf(weapon);
                clip = own != null && skin.Has(own) ? own : skin.Has(kind) ? kind : null;
            }
            if (weapon == null)
            {
                shieldUnarmed = clip;
                shieldUnarmedKnown = true;
            }
            else
            {
                shieldStanceOf[weapon] = clip;
            }
            return clip;
        }

        // Whether the pawn holds up a shield drawn here: wearing apparel with a held garment, shown, on an export with the
        // guard.
        private static bool ShieldUp(Pawn pawn)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            if (skin == null || worn == null || !skin.Has("shield") || !ShieldShows(pawn))
            {
                return false;
            }
            for (int i = 0; i < worn.Count; i++)
            {
                if (skin.IsHeld(skin.GarmentOf(worn[i].def)))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
