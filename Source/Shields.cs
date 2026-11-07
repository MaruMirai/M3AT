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

        // Where a shield carried on the back is this draw: in the hand (held), or on the back over what stands out under it
        // (over: the weapons holstered there, ShieldOverWeapons). After Wearing, before anything is drawn.
        public void Shielding(Posed p, bool held, float over)
        {
            if (held != p.shieldHeld || over != p.shieldOver)
            {
                p.posedAt++;   // it is placed by these, not only by the bones
            }
            p.shieldHeld = held;
            p.shieldOver = over;
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
        // slot on the export, always in the fist.
        private Matrix4x4 ShieldPlace(Posed p, int g)
        {
            PackRig rig = p.shieldHeld ? null : PackRigOf(g);
            if (rig == null)
            {
                int hand;
                return boneIndex.TryGetValue("hand.L", out hand)
                    ? p.whole[hand] * Matrix4x4.Translate(p.shape.boneMove[hand]) * pieces[g].heldAt : pieces[g].heldAt;
            }
            Holster h = rig.place;
            return p.whole[h.bone] * Matrix4x4.Translate(p.shape.boneMove[h.bone] + h.outward * ShieldPush(p, g, rig));
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

        // Whether CE would draw the pawn's shield now (Apparel_Shield.drawShield, PawnRenderNodeWorker_Drafted): drafted, or
        // in a job or duty that shows the weapon (a raider's assault duty).
        private static bool ShieldShows(Pawn pawn)
        {
            return pawn.Spawned && (pawn.Drafted || (pawn.CurJob != null && pawn.CurJob.def.alwaysShowWeapon)
                || (pawn.mindState != null && pawn.mindState.duty != null && pawn.mindState.duty.def.alwaysShowWeapon));
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
