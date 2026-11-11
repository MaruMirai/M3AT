using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // A hat worn over the hair rather than tucking it under. A garment with the line "over_hair yes" (the slicecap, a crescent
    // of shell over one side of the scalp) leaves the hair whole: tucked, the hair was cut where the hat isn't and the
    // hair-coloured scalp showed round its edge. Instead its rest pose is pushed out past the hair of the style worn, each
    // place along its line from the head's middle to Clear over the outermost hair on that line, the push spread over the
    // hat so it keeps its shape (in full over Hold round a place, tapering at Slope, then averaged over Blend, never below
    // what a place needs). The hat and the hair both ride the head bone, so this is worked out at rest, once per shape, hat
    // and hair (as cut by any other hat worn), and stands for every pose.
    public partial class SkinnedFigure
    {
        private const float OverHairClear = 0.008f;   // cells over the hair
        private const float OverHairReach = 0.3f;     // cells past the hat's place along its line that hair still counts
        private const float OverHairHold = 0.03f;     // cells round a place that take its whole push
        private const float OverHairSlope = 0.5f;     // cells of push lost per cell further on
        private const float OverHairBlend = 0.06f;    // cells: the push is then averaged with weights falling smoothly to none here

        // The hair the garments with over_hair are pushed out over this draw (null: none). After Wearing, before drawing.
        public void HairUnderHats(Posed p, RimWorld.HairDef style, GarmentMask mask)
        {
            Piece hair = null;
            if (style != null && HasOverHair(mask))
            {
                hair = WornHair(Hair(style), mask, p);
                if (hair != null && hair.tris.Length == 0)
                {
                    hair = null;
                }
            }
            if (hair != p.hairOver)
            {
                p.hairOver = hair;
                p.posedAt++;   // the hat's places depend on it, not only on the bones
            }
        }

        private bool HasOverHair(GarmentMask mask)
        {
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                if (pieces[g].overHair && (mask & GarmentBit(g)) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        // Garment g's rest pose in shape s pushed out over hair, if it goes over the hair; plain as it is otherwise.
        private PieceShape OverHairShape(Shape s, int g, PieceShape plain, Piece hair)
        {
            if (hair == null || g <= 0 || !pieces[g].overHair)
            {
                return plain;
            }
            (int, Piece, PieceShape) key = (g, hair, plain);
            PieceShape shaped;
            if (s.overHair.TryGetValue(key, out shaped))
            {
                return shaped;
            }
            PieceShape hairShape;
            if (!s.hairs.TryGetValue(hair, out hairShape))
            {
                hairShape = ShapePiece(hair, s.mix);
                s.hairs[hair] = hairShape;
            }
            Vector3[] rest = PushedOver(plain.rest, s.faceCentre, hairShape.rest, hair.tris);
            Vector3[] normal = Normals(rest, pieces[g].tris);
            shaped = new PieceShape { rest = rest, normal = normal, hull = SmoothHull(normal) };
            s.overHair[key] = shaped;
            return shaped;
        }

        // The places rest pushed out from centre past the surface (over, overTris).
        private static Vector3[] PushedOver(Vector3[] rest, Vector3 centre, Vector3[] over, int[] overTris)
        {
            int n = rest.Length;
            float[] need = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 way = rest[i] - centre;
                float r = way.magnitude;
                if (r < 1e-4f)
                {
                    continue;
                }
                way /= r;
                float outer = OutermostAlong(centre, way, over, overTris, r + OverHairReach);
                need[i] = Mathf.Max(0f, outer + OverHairClear - r);
            }
            // Held over Hold, tapering at Slope, then averaged over Blend by a smooth falloff, never below the need.
            float[] held = new float[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    float d = Mathf.Max(0f, (rest[i] - rest[j]).magnitude - OverHairHold);
                    held[i] = Mathf.Max(held[i], need[j] - OverHairSlope * d);
                }
            }
            Vector3[] pushed = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float sum = 0f, weights = 0f;
                for (int j = 0; j < n; j++)
                {
                    float d = (rest[i] - rest[j]).magnitude / OverHairBlend;
                    if (d < 1f)
                    {
                        float w = (1f - d * d) * (1f - d * d);
                        sum += held[j] * w;
                        weights += w;
                    }
                }
                float push = Mathf.Max(need[i], weights > 0f ? sum / weights : held[i]);
                Vector3 way = rest[i] - centre;
                pushed[i] = way.sqrMagnitude > 1e-8f ? rest[i] + way.normalized * push : rest[i];
            }
            return pushed;
        }

        // How far out from start along way (a unit vector) the surface last is, up to most; 0 if it isn't on that line.
        private static float OutermostAlong(Vector3 start, Vector3 way, Vector3[] rest, int[] tris, float most)
        {
            float outer = 0f;
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                Vector3 a = rest[tris[k]], ab = rest[tris[k + 1]] - a, ac = rest[tris[k + 2]] - a;
                Vector3 side = Vector3.Cross(way, ac);
                float det = Vector3.Dot(ab, side);
                if (Mathf.Abs(det) < 1e-9f)
                {
                    continue;
                }
                Vector3 off = start - a;
                float u = Vector3.Dot(off, side) / det;
                if (u < 0f || u > 1f)
                {
                    continue;
                }
                Vector3 up = Vector3.Cross(off, ab);
                float v = Vector3.Dot(way, up) / det;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }
                float t = Vector3.Dot(ac, up) / det;
                if (t > outer && t < most)
                {
                    outer = t;
                }
            }
            return outer;
        }
    }
}
