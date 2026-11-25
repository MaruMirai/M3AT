using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Head wear under the Shrinkhead key. The exporter fits that key on a garment from the skin under each vertex, which
    // moves the vertex as that skin moves but keeps its gap off it at full size: on the whole key the head comes out about
    // 0.56 as big and a cowboy hat only about 0.8, standing off the smaller head. The hair's key also shrinks its gap
    // (HairBuilder.Keys), so its cut edge, measured against the hat at rest (HairUnderHat), no longer met the hat, and
    // the hair stopped short under the brim, and a helmet stood off a shrunk head like a bucket. The key shrinks the head
    // evenly (within 0.003 cells on average), so each garment vertex takes that same even shrink, fitted to the head's
    // own vertices, by its share of weight on the head bone, and the exporter's move for the rest: a hat, a mask or
    // glasses riding the head whole keep their shape and fit at any head size, and a full helmet's edge, weighted partly
    // to the neck, still follows the neck.
    public sealed partial class SkinnedFigure
    {
        private const float HeadOnly = 0.99f;   // a body vertex with this share of its weight on the head bone counts as the head's

        // The head's even shrink under the whole key (HeadScaleOf), for what is moved on the head after loading: 1 and none
        // until worked out, or for an export without the key.
        private float headScale = 1f;

        // Every piece (and its visor) with weight on the head bone takes the head's even shrink in its Shrinkhead key by that weight.
        private void EvenHeadShrink(List<Piece> all)
        {
            Piece body = all[0];
            Vector3[] moves;
            if (!body.keys.TryGetValue(ShrinkKey, out moves))
            {
                return;
            }
            float scale;
            Vector3 shift;
            if (!HeadScaleOf(body, moves, out scale, out shift))
            {
                return;
            }
            headScale = scale;
            int changed = 0;
            // (a mod's retexture shares its base garment's key arrays, and each is to be moved once)
            HashSet<Vector3[]> done = new HashSet<Vector3[]>();
            for (int k = 1; k < all.Count; k++)
            {
                if (EvenShrink(all[k], scale, shift, done))
                {
                    changed++;
                }
                if (all[k].visor != null)
                {
                    EvenShrink(all[k].visor, scale, shift, done);
                }
            }
            if (changed > 0)
            {
                Log.Message(Tag + changed + " garments on the head shrink evenly with it, by their weight on it (to " + scale.ToString("0.00") + ").");
            }
        }

        // The even scale and shift that best take the head's vertices where the key moves them (least squares).
        private bool HeadScaleOf(Piece body, Vector3[] moves, out float scale, out Vector3 shift)
        {
            scale = 1f;
            shift = Vector3.zero;
            List<int> head = new List<int>();
            for (int i = 0; i < body.rest.Length; i++)
            {
                if (HeadShare(body, i) >= HeadOnly)
                {
                    head.Add(i);
                }
            }
            if (head.Count < 8)
            {
                return false;
            }
            Vector3 restMean = Vector3.zero, movedMean = Vector3.zero;
            foreach (int i in head)
            {
                restMean += body.rest[i];
                movedMean += body.rest[i] + moves[i];
            }
            restMean /= head.Count;
            movedMean /= head.Count;
            float across = 0f, spread = 0f;
            foreach (int i in head)
            {
                Vector3 from = body.rest[i] - restMean;
                across += Vector3.Dot(from, body.rest[i] + moves[i] - movedMean);
                spread += from.sqrMagnitude;
            }
            if (spread < 1e-9f)
            {
                return false;
            }
            scale = across / spread;
            shift = movedMean - scale * restMean;
            return true;
        }

        // A vertex's share of its weight on the head bone.
        private float HeadShare(Piece piece, int i)
        {
            float share = 0f;
            for (int w = piece.firstWeight[i]; w < piece.firstWeight[i + 1]; w++)
            {
                if (piece.weightBone[w] == headBone)
                {
                    share += piece.weight[w];
                }
            }
            return share;
        }

        // Moves a piece's Shrinkhead key toward the even shrink by each vertex's weight on the head bone. False if it has no
        // key or nothing on the head.
        private bool EvenShrink(Piece piece, float scale, Vector3 shift, HashSet<Vector3[]> done)
        {
            Vector3[] moves;
            if (piece.firstWeight == null || !piece.keys.TryGetValue(ShrinkKey, out moves) || !done.Add(moves))
            {
                return false;
            }
            bool any = false;
            for (int i = 0; i < piece.rest.Length; i++)
            {
                float share = Mathf.Clamp01(HeadShare(piece, i));
                if (share > 0f)
                {
                    moves[i] = Vector3.Lerp(moves[i], (scale - 1f) * piece.rest[i] + shift, share);
                    any = true;
                }
            }
            return any;
        }
    }
}
