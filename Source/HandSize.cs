using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // How big each hand is in a body shape against the bare base body's. A held weapon is placed from the wrist joint by
    // the export's fist (walk.HAND_GRIP along the hand, the hold's at in the hand's frame), measured on the base hand; the
    // Hulk key grows the hand about 1.4 times and Fat thickens it, so the fingers closed round a point further out and the
    // weapon's grip came out inside the palm, the knuckles standing up over the receiver (./autotest.sh armourgrip, all of
    // ArmsOut, ArmsClear and neither). The fist's place is scaled by this (WalkFigures.PlaceWeapon, FistOf), so it follows
    // any key that changes the hands: the Hulk, Fat, a child's Babylimbs.
    public sealed partial class SkinnedFigure
    {
        private const float HandWeightLeast = 0.5f;           // a vertex with this much on the hand's bones is the hand's
        private const float HandScaleLeast = 0.5f, HandScaleMost = 2f;
        private float[] baseHandReach;                         // the base body's, R then L; null until first asked

        // Each side's hand: the mean distance of its vertices from its wrist joint, in the given rest pose and joints.
        private float[] HandReach(Vector3[] rest, Vector3[] heads)
        {
            float[] reach = new float[2];
            Piece body = pieces[0];
            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "R" : "L";
                int wristBone;
                if (!boneIndex.TryGetValue("hand." + side, out wristBone))
                {
                    continue;
                }
                bool[] onHand = new bool[boneHead.Length];
                foreach (KeyValuePair<string, int> pair in boneIndex)
                {
                    onHand[pair.Value] = (pair.Key.StartsWith("hand." + side) || pair.Key.StartsWith("thumb." + side));
                }
                Vector3 wrist = heads[wristBone];
                double sum = 0;
                int count = 0;
                for (int i = 0; i < rest.Length; i++)
                {
                    float w = 0f;
                    for (int k = body.firstWeight[i]; k < body.firstWeight[i + 1]; k++)
                    {
                        if (onHand[body.weightBone[k]])
                        {
                            w += body.weight[k];
                        }
                    }
                    if (w >= HandWeightLeast)
                    {
                        sum += (rest[i] - wrist).magnitude;
                        count++;
                    }
                }
                reach[s] = count > 0 ? (float)(sum / count) : 0f;
            }
            return reach;
        }

        // How much bigger the figure's hand on that side ("R" or "L") is than the base body's: 1 on the base body.
        public float HandScaleOf(Posed p, string side)
        {
            Shape s = p != null ? p.shape : null;
            if (s == null || s.pieces == null || s.pieces[0] == null || s.boneHead == null)
            {
                return 1f;
            }
            if (s.handScale == null)
            {
                if (baseHandReach == null)
                {
                    baseHandReach = HandReach(pieces[0].rest, boneHead);
                }
                float[] reach = HandReach(s.pieces[0].rest, s.boneHead);
                s.handScale = new float[2];
                for (int i = 0; i < 2; i++)
                {
                    s.handScale[i] = baseHandReach[i] > 1e-5f ? Mathf.Clamp(reach[i] / baseHandReach[i], HandScaleLeast, HandScaleMost) : 1f;
                }
            }
            return s.handScale[side == "L" ? 1 : 0];
        }
    
#if AUTOTEST
        // The autotest's look at a held weapon on a shape (armourgrip): point (model units, the whole move in, as JointOf
        // gives) and the middle of the hand's skin as posed here on the CPU, each in the hand bone's own frame from its
        // wrist, "point | skin" in model units; empty if there is no such hand.
        public string HandGapOf(Posed p, string side, Vector3 point)
        {
            int wristBone;
            if (p == null || p.shape == null || !boneIndex.TryGetValue("hand." + side, out wristBone))
            {
                return "";
            }
            bool[] onHand = new bool[boneHead.Length];
            foreach (KeyValuePair<string, int> pair in boneIndex)
            {
                onHand[pair.Value] = pair.Key.StartsWith("hand." + side) || pair.Key.StartsWith("thumb." + side);
            }
            Piece body = pieces[0];
            Vector3[] rest = ShapedPiece(p.shape, 0).rest;
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < rest.Length; i++)
            {
                float w = 0f;
                Vector3 v = Vector3.zero;
                for (int k = body.firstWeight[i]; k < body.firstWeight[i + 1]; k++)
                {
                    v += p.bones[body.weightBone[k]].MultiplyPoint3x4(rest[i]) * body.weight[k];
                    if (onHand[body.weightBone[k]])
                    {
                        w += body.weight[k];
                    }
                }
                if (w >= HandWeightLeast)
                {
                    sum += v;
                    count++;
                }
            }
            if (count == 0)
            {
                return "";
            }
            Matrix4x4 back = p.whole[wristBone].inverse;
            Vector3 wrist = p.shape.boneHead[wristBone];
            Vector3 a = back.MultiplyPoint3x4(point) - wrist, b = back.MultiplyPoint3x4(sum / count) - wrist;
            return a.ToString("F3") + " | " + b.ToString("F3") + " gap " + (a - b).ToString("F3");
        }
#endif
}
}
