using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // The support hand of a two-handed hold kept on the weapon on any body. A drafted stance was made on the bare base
    // body, with both hands on the weapon. Played on a shape with moved joints (a Hulk's shoulders twice as far apart, its
    // arms widened), each arm carries its own hand out: the weapon rides the holding hand, so the other hand came off it,
    // along the barrel and over its top (./autotest.sh armourgrip; the left fist 0.07 model units off at Hulk 1.0, 0.11 at
    // 1.5). So where the stance's first frame puts the other wrist on the base body, and how it turns it, is kept in the
    // holding hand's own frame, and on any shape the other arm is bent to put it there again (WalkFigures.GripOffHand).
    public sealed partial class SkinnedFigure
    {
        // The other hand as the stance has it on the base body, in the holding hand's frame: its wrist from the holding
        // wrist (model units, in the holding hand's rest frame) and its turn on top of the holding hand's.
        private sealed class SupportHold
        {
            public bool on;           // the other fist is on the weapon's line in the base pose: a two-handed hold
            public Vector3 wrist;
            public Quaternion turn;
        }

        private readonly Dictionary<string, SupportHold> supportHolds = new Dictionary<string, SupportHold>();
        private Posed supportPose;
        private const float SupportOnLine = 0.08f;
        private const float SupportReach = 0.97f;   // of the other arm's length it is stretched to at most, so it keeps a bend   // model units: the other fist this near the weapon's line holds it

        // Where the other hand (side, "R" or "L") goes on clip's weapon held in the holding hand as the figure is posed now
        // (after Chain: its wrist, model units with the whole move in, as JointOf gives, and its turn in model space,
        // before the whole move); false if the stance doesn't hold the weapon in both hands or there are no such bones.
        // aim: the hold's line in the holding hand's rest frame (WalkFigures.PlaceWeapon's rest), to tell a two-handed
        // hold from a free hand.
        public bool SupportOf(Posed p, string clip, string side, Vector3 aim, out Vector3 wrist, out Quaternion turn)
        {
            wrist = Vector3.zero;
            turn = Quaternion.identity;
            string holding = side == "L" ? "R" : "L";
            int hand, other;
            if (clip == null || !clips.ContainsKey(clip) || !boneIndex.TryGetValue("hand." + holding, out hand)
                || !boneIndex.TryGetValue("hand." + side, out other))
            {
                return false;
            }
            string key = clip + "|" + side;
            SupportHold s;
            if (!supportHolds.TryGetValue(key, out s))
            {
                s = new SupportHold();
                supportHolds[key] = s;
                Clip c = clips[clip];
                if (c.over == null || c.over[other] > 0f)
                {
                    if (supportPose == null)
                    {
                        supportPose = NewPosed();
                    }
                    Posed b = supportPose;
                    b.shape = ShapeOf(null, null, 0f, 0f);
                    Begin(b);
                    Add(b, clip, 0f, 1f);
                    Chain(b, Matrix4x4.identity);
                    Matrix4x4 back = b.whole[hand].inverse;
                    s.wrist = back.MultiplyPoint3x4(b.pos[other]) - boneHead[hand];
                    s.turn = Quaternion.Inverse(b.turn[hand]) * b.turn[other];
                    // Its fist against the weapon's line through the holding fist, both in the holding hand's rest frame.
                    Vector3 grip, otherGrip;
                    float along = HandGrip >= 0f ? HandGrip : 0.03f;
                    if (GripOf(holding, out grip) && GripOf(side, out otherGrip))
                    {
                        Vector3 fist = grip * along;
                        Vector3 otherFist = s.wrist + s.turn * otherGrip * along;
                        Vector3 off = Vector3.ProjectOnPlane(otherFist - fist, aim.normalized);
                        s.on = aim.sqrMagnitude > 1e-8f && off.magnitude <= SupportOnLine;
                    }
                }
            }
            if (!s.on)
            {
                return false;
            }
            wrist = p.whole[hand].MultiplyPoint3x4(p.shape.boneHead[hand] + s.wrist);   // (p.whole has the whole move in)
            turn = p.turn[hand] * s.turn;
            return true;
        }

        // How far the other hand would turn from its turn now to turn (SupportOf's, model space before the whole move).
        public Quaternion TurnFromNow(Posed p, string side, Quaternion turn)
        {
            int other;
            return boneIndex.TryGetValue("hand." + side, out other) ? turn * Quaternion.Inverse(p.turn[other]) : Quaternion.identity;
        }

        // The other hand put there (SupportOf), w of the way, the hand turned with it. After Chain (and ArmsOut, ArmsClear);
        // call Reached once the arms are done.
        public void HoldSupport(Posed p, Matrix4x4 whole, string side, Vector3 wrist, Quaternion turn, float w)
        {
            int other;
            if (w <= 0f || !boneIndex.TryGetValue("hand." + side, out other))
            {
                return;
            }
            // The hand turned, and the fingers and thumb with it (model space after Chain, so each is turned by as much).
            Quaternion by = Quaternion.Slerp(p.turn[other], turn, Mathf.Clamp01(w)) * Quaternion.Inverse(p.turn[other]);
            for (int b = 0; b < boneParent.Length; b++)
            {
                for (int up = b; up >= 0; up = boneParent[up])
                {
                    if (up == other)
                    {
                        p.turn[b] = by * p.turn[b];
                        break;
                    }
                }
            }
            // A shape whose shoulders are far apart (a Hulk's) leaves the other arm too short to reach across: the
            // holding hand, and the weapon with it, is brought toward the other shoulder by what it lacks, w of it.
            int upper, fore, holding;
            if (boneIndex.TryGetValue("upper_arm." + side, out upper) && boneIndex.TryGetValue("forearm." + side, out fore)
                && boneIndex.TryGetValue("hand." + (side == "L" ? "R" : "L"), out holding))
            {
                Vector3[] head = p.shape.boneHead;
                float arm = ((head[fore] - head[upper]).magnitude + (head[other] - head[fore]).magnitude) * SupportReach;
                Vector3 to = whole.inverse.MultiplyPoint3x4(wrist) - p.pos[upper];
                float lacks = to.magnitude - arm;
                if (lacks > 0f)
                {
                    Vector3 shift = -to.normalized * (lacks * Mathf.Clamp01(w));
                    Reach(p, whole, side == "L" ? "R" : "L", whole.MultiplyPoint3x4(p.pos[holding] + shift), 1f);
                    wrist = whole.MultiplyPoint3x4(whole.inverse.MultiplyPoint3x4(wrist) + shift);
                }
            }
            Reach(p, whole, side, wrist, w);
        }
    }
}
