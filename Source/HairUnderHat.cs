using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;

namespace M3AT
{
    // Hair under a hat. A garment with the line "hide_hair tuck" doesn't leave the hair off: the hair is cut where the hat
    // ends, and what is left below is drawn in close to the head just under the rim, easing out to its own shape further
    // down, so a long style comes out from under the hat rather than through it or standing off from it. The hair and the
    // hats ride the head bone, so this is worked out once per style and hat, at rest in the base shape, and stands for every
    // pose; body keys move the new vertices as they moved the old.
    public sealed partial class SkinnedFigure
    {
        private const int RimBins = 72;   // how many ways round the head the rim is measured
        private const float SampleStep = 0.012f;   // cells between the points a surface is measured at
        // Cells off the skin beside it a hat's surface may lie and still count as sitting on the head, which leaves out a
        // brim: hair comes out from under the crown, not from the brim's edge.
        private const float BrimReach = 0.04f;
        private const float RimTuck = 0.025f;   // cells above the rim's lowest the hair is cut, so that its cut edge is inside the hat
        private const float RimInset = 0.012f;   // cells inside the rim the hair is at the rim's height
        private const float HairFlare = 0.55f;   // cells the hair may stand further out per cell below the rim
        private const float FlareEase = 0.05f;   // cells over which the flare gives way to the hair's own shape
        private const float UnderStep = 0.012f;   // cells between the distances from the head's middle the hat's underside is measured at
        private const int UnderSteps = 48;   // and how many, out past the widest brim
        private const float SkinClear = 0.006f;   // cells off the skin that drawing the hair in stops at
        private const float ShellNear = 0.06f;   // cells: a body triangle stays out of the outline only with this near it of the cut shell

        // What a hat's rim is like, each way round the head.
        private sealed class Rim
        {
            public float[] height = new float[RimBins];   // the hat's lowest on the head; PositiveInfinity: it has none that way
            public float[] radius = new float[RimBins];   // and how far that is from the head's middle
            public float[][] under = new float[RimBins][];   // [way round][distance out / UnderStep]: the hat's lowest there, a brim's too
        }

        private GarmentMask hairTuckers;   // the garments (bit g: garment g) that cut the hair at their rim
        private readonly Dictionary<int, Rim> rims = new Dictionary<int, Rim>();
        private readonly Dictionary<KeyValuePair<Piece, int>, Piece> tucked = new Dictionary<KeyValuePair<Piece, int>, Piece>();
        private float[][] skinRadius;   // [way round][height step]: the skin's distance from the head's middle (SkinRadius)
        private float skinLow, skinStep;

        // The hair to draw for this style with the garments in mask worn: cut under the first that tucks it, if one does.
        private Piece WornHair(Piece hair, GarmentMask mask, Posed p)
        {
            GarmentMask under = mask & hairTuckers;
            if (hair == null)
            {
                return null;
            }
            if (under == 0)
            {
                return hair;
            }
            int g = 1;
            while ((under & (GarmentBit(g))) == 0)
            {
                g++;
            }
            KeyValuePair<Piece, int> key = new KeyValuePair<Piece, int>(hair, g);
            Piece cut;
            if (!tucked.TryGetValue(key, out cut))
            {
                cut = TuckedUnder(hair, RimOf(g));
                tucked[key] = cut;
            }
            return cut;
        }

        // The hair cut at a rim and drawn in under it, with its shell the same way.
        private Piece TuckedUnder(Piece hair, Rim rim)
        {
            Piece cut = Clipped(hair, rim);
            cut.texture = hair.texture;
            if (hair.shell != null)
            {
                cut.shell = Clipped(hair.shell, rim);
                cut.shell.hides = StillInside(hair.shell.hides, cut.shell, rim);
            }
            return cut;
        }

        // The body triangles the cut shell still encloses: those under the hat (it covers them either way), and those with
        // some of the shell still by them. Without this a short style's head would lose its outline where the hat took the hair.
        private int[] StillInside(int[] hides, Piece shell, Rim rim)
        {
            Piece body = pieces[0];
            List<int> kept = new List<int>(hides.Length);
            foreach (int k in hides)
            {
                Vector3 middle = (body.rest[body.tris[3 * k]] + body.rest[body.tris[3 * k + 1]] + body.rest[body.tris[3 * k + 2]]) / 3f;
                bool keep = Above(middle, rim) > 0f;
                for (int i = 0; !keep && i < shell.rest.Length; i++)
                {
                    keep = (shell.rest[i] - middle).sqrMagnitude < ShellNear * ShellNear;
                }
                if (keep)
                {
                    kept.Add(k);
                }
            }
            return kept.ToArray();
        }

        // A tucking hat's hides with every scalp triangle above its rim added. The hair is cut there, so what is left over
        // the crown is the scalp, drawn in the hair's colour; a hat lying close on the head (the simple helmet stands
        // 0.005 cells off it) misses some of it in the exporter's test, and riding the head bone whole while the scalp
        // bends a little with the neck, it let that hair-coloured skin through, which looked like hair over the hat.
        private int[] WithScalpUnder(int[] hides, int g, bool[] scalp)
        {
            Piece body = pieces[0];
            Rim rim = RimOf(g);
            HashSet<int> all = new HashSet<int>(hides);
            for (int k = 0; k + 2 < body.tris.Length; k += 3)
            {
                bool under = true;
                for (int c = 0; c < 3 && under; c++)
                {
                    int i = body.tris[k + c];
                    under = scalp[i] && Above(body.rest[i], rim) + RimTuck > 0f;
                }
                if (under)
                {
                    all.Add(k / 3);
                }
            }
            int[] kept = new int[all.Count];
            all.CopyTo(kept);
            Array.Sort(kept);
            return kept;
        }

        // ---- the rim

        private Rim RimOf(int g)
        {
            Rim rim;
            if (!rims.TryGetValue(g, out rim))
            {
                rim = MeasureRim(pieces[g].rest, pieces[g].tris);
                rims[g] = rim;
            }
            return rim;
        }

        // The rim of a hat's surface.
        private Rim MeasureRim(Vector3[] rest, int[] tris)
        {
            SkinRadius();
            Rim rim = new Rim();
            for (int b = 0; b < RimBins; b++)
            {
                rim.height[b] = float.PositiveInfinity;
                rim.under[b] = new float[UnderSteps];
                for (int step = 0; step < UnderSteps; step++)
                {
                    rim.under[b][step] = float.PositiveInfinity;
                }
            }
            Sample(rest, tris, null, point =>
            {
                float r;
                float way = WayRound(point, out r);
                int b = (int)way % RimBins;
                int step = Mathf.Min((int)(r / UnderStep), UnderSteps - 1);
                rim.under[b][step] = Mathf.Min(rim.under[b][step], point.y);
                if (r <= SkinAt(way, point.y) + BrimReach && point.y < rim.height[b])
                {
                    rim.height[b] = point.y;
                    rim.radius[b] = r;
                }
            });
            FillRim(rim);
            return rim;
        }

        // Ways with nothing measured (between a low-poly hat's points, or none of it there) take the nearest either side, and
        // the whole is smoothed a little: a step in the cut shows under a low hat's edge. All empty: left so, nothing is cut.
        private static void FillRim(Rim rim)
        {
            float[] height = (float[])rim.height.Clone(), radius = (float[])rim.radius.Clone();
            for (int b = 0; b < RimBins; b++)
            {
                for (int step = 1; step < RimBins / 8 && float.IsPositiveInfinity(height[b]); step++)
                {
                    int left = (b + RimBins - step) % RimBins, right = (b + step) % RimBins;
                    int from = rim.height[left] <= rim.height[right] ? left : right;
                    height[b] = rim.height[from];
                    radius[b] = rim.radius[from];
                }
            }
            for (int b = 0; b < RimBins; b++)
            {
                int left = (b + RimBins - 1) % RimBins, right = (b + 1) % RimBins;
                rim.height[b] = Between(height[left], height[b], height[right]);
                rim.radius[b] = Between(radius[left], radius[b], radius[right]);
            }
        }

        private static float Between(float left, float mid, float right)
        {
            if (float.IsPositiveInfinity(mid) || float.IsPositiveInfinity(left) || float.IsPositiveInfinity(right))
            {
                return mid;
            }
            return (left + 2f * mid + right) / 4f;
        }

        // Measures the head's skin once: the furthest it is from the head's middle, each way round and each height step.
        private void SkinRadius()
        {
            if (skinRadius != null)
            {
                return;
            }
            Piece body = pieces[0];
            bool[] head = OnHead();
            float low = float.PositiveInfinity, high = float.NegativeInfinity;
            for (int i = 0; i < head.Length; i++)
            {
                if (head[i])
                {
                    low = Mathf.Min(low, body.rest[i].y);
                    high = Mathf.Max(high, body.rest[i].y);
                }
            }
            const int steps = 40;
            skinLow = low;
            skinStep = Mathf.Max(high - low, 0.001f) / steps;
            float[][] radius = new float[RimBins][];
            for (int b = 0; b < RimBins; b++)
            {
                radius[b] = new float[steps + 1];
            }
            Sample(body.rest, body.tris, head, point =>
            {
                float r;
                int b = (int)WayRound(point, out r) % RimBins;
                int y = Mathf.Clamp(Mathf.RoundToInt((point.y - skinLow) / skinStep), 0, steps);
                radius[b][y] = Mathf.Max(radius[b][y], r);
            });
            skinRadius = radius;
        }

        // The skin's distance from the head's middle at a place; 0 off the head.
        private float SkinAt(float way, float y)
        {
            int b = (int)way % RimBins;
            float step = (y - skinLow) / skinStep;
            if (step < 0f || step >= skinRadius[b].Length)
            {
                return 0f;
            }
            return Mathf.Max(skinRadius[b][(int)step], skinRadius[(b + 1) % RimBins][(int)step]);
        }

        // Calls at with points over the triangles (only triangles with a corner in only, if given) about SampleStep apart.
        private static void Sample(Vector3[] rest, int[] tris, bool[] only, Action<Vector3> at)
        {
            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                int ia = tris[k], ib = tris[k + 1], ic = tris[k + 2];
                if (only != null && !only[ia] && !only[ib] && !only[ic])
                {
                    continue;
                }
                Vector3 a = rest[ia], b = rest[ib], c = rest[ic];
                float longest = Mathf.Max((a - b).magnitude, Mathf.Max((b - c).magnitude, (c - a).magnitude));
                int n = Mathf.Max(1, Mathf.CeilToInt(longest / SampleStep));
                for (int i = 0; i <= n; i++)
                {
                    for (int j = 0; i + j <= n; j++)
                    {
                        at(a + (b - a) * ((float)i / n) + (c - a) * ((float)j / n));
                    }
                }
            }
        }

        // How far round the head's vertical line a point is, in RimBins (0 up to RimBins), and how far from that line.
        private float WayRound(Vector3 point, out float radius)
        {
            float x = point.x - faceCentre.x, z = point.z - faceCentre.z;
            radius = Mathf.Sqrt(x * x + z * z);
            float way = (Mathf.Atan2(z, x) / (2f * Mathf.PI) + 1f) % 1f * RimBins;
            return way >= RimBins ? 0f : way;
        }

        private static float RimValue(float[] values, float way)
        {
            int b = (int)way;
            float next = values[(b + 1) % RimBins], here = values[b % RimBins];
            if (float.IsPositiveInfinity(here) || float.IsPositiveInfinity(next))
            {
                return Mathf.Min(here, next);
            }
            return here + (next - here) * (way - b);
        }

        // Cells a point is above where the hair is cut; below 0 it stays.
        private float Above(Vector3 point, Rim rim)
        {
            float radius;
            float height = RimValue(rim.height, WayRound(point, out radius));
            return float.IsPositiveInfinity(height) ? -1f : point.y - height - RimTuck;
        }

        // ---- the cut

        // A copy of a piece (hair or its shell) with what is above the cut gone, a triangle across it cut through so the texture
        // stays where it was, and the rest drawn in under the rim. New vertices lie on the old edges, and take their mix of
        // everything; the vertices no triangle uses any more are dropped.
        private Piece Clipped(Piece from, Rim rim)
        {
            int n = from.rest.Length;
            float[] above = new float[n];
            for (int i = 0; i < n; i++)
            {
                above[i] = Above(from.rest[i], rim);
            }
            // Each kept vertex as two old ones and how far along from the first; an old one by itself has along 0.
            List<int> sourceA = new List<int>(), sourceB = new List<int>();
            List<float> along = new List<float>();
            Dictionary<int, int> corner = new Dictionary<int, int>();
            Dictionary<long, int> crossing = new Dictionary<long, int>();
            List<int> tris = new List<int>(from.tris.Length);
            List<int> polygon = new List<int>(4);
            int[] triangle = new int[3];
            for (int k = 0; k + 2 < from.tris.Length; k += 3)
            {
                polygon.Clear();
                triangle[0] = from.tris[k];
                triangle[1] = from.tris[k + 1];
                triangle[2] = from.tris[k + 2];
                for (int c = 0; c < 3; c++)
                {
                    int a = triangle[c], b = triangle[(c + 1) % 3];
                    if (above[a] <= 0f)
                    {
                        int index;
                        if (!corner.TryGetValue(a, out index))
                        {
                            index = sourceA.Count;
                            corner[a] = index;
                            sourceA.Add(a);
                            sourceB.Add(a);
                            along.Add(0f);
                        }
                        polygon.Add(index);
                    }
                    if ((above[a] <= 0f) != (above[b] <= 0f))
                    {
                        // Numbered the same from either triangle beside the edge, so the cut stays joined.
                        long edge = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
                        int index;
                        if (!crossing.TryGetValue(edge, out index))
                        {
                            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
                            index = sourceA.Count;
                            crossing[edge] = index;
                            sourceA.Add(lo);
                            sourceB.Add(hi);
                            along.Add(above[lo] / (above[lo] - above[hi]));
                        }
                        polygon.Add(index);
                    }
                }
                for (int c = 2; c < polygon.Count; c++)
                {
                    tris.Add(polygon[0]);
                    tris.Add(polygon[c - 1]);
                    tris.Add(polygon[c]);
                }
            }
            int count = sourceA.Count;
            Piece p = new Piece { name = from.name, layer = from.layer, groups = from.groups, defs = from.defs, hides = from.hides };
            p.tris = tris.ToArray();
            p.rest = new Vector3[count];
            List<int> first = new List<int>(count + 1), bone = new List<int>(count);
            List<float> weight = new List<float>(count);
            for (int i = 0; i < count; i++)
            {
                Vector3 place = Vector3.Lerp(from.rest[sourceA[i]], from.rest[sourceB[i]], along[i]);
                p.rest[i] = DrawnIn(place, rim);
                first.Add(bone.Count);
                // Hair is all on the head bone, so either end's weights will do.
                for (int w = from.firstWeight[sourceA[i]]; w < from.firstWeight[sourceA[i] + 1]; w++)
                {
                    bone.Add(from.weightBone[w]);
                    weight.Add(from.weight[w]);
                }
            }
            first.Add(bone.Count);
            p.firstWeight = first.ToArray();
            p.weightBone = bone.ToArray();
            p.weight = weight.ToArray();
            if (from.uvs != null)
            {
                p.uvs = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    p.uvs[i] = Vector2.Lerp(from.uvs[sourceA[i]], from.uvs[sourceB[i]], along[i]);
                }
            }
            if (from.normal != null)
            {
                p.normal = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    p.normal[i] = Vector3.Lerp(from.normal[sourceA[i]], from.normal[sourceB[i]], along[i]);
                }
            }
            if (from.skinVert != null)
            {
                p.skinVert = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    p.skinVert[i] = from.skinVert[sourceA[i]];
                }
            }
            foreach (KeyValuePair<string, Vector3[]> key in from.keys)
            {
                Vector3[] moves = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    moves[i] = Vector3.Lerp(key.Value[sourceA[i]], key.Value[sourceB[i]], along[i]);
                }
                p.keys[key.Key] = moves;
            }
            return p;
        }

        // How far out from the head's middle hair at this height may go before it meets the hat. From the middle, not the rim:
        // the rim can be a brim's lip with the hat sloping in above it, and a brim can droop further than the flare, either of
        // which would have the hair up through it. PositiveInfinity if it is below all of the hat.
        private static float UnderHat(Rim rim, float way, float y)
        {
            int b = (int)way % RimBins, before = (b + RimBins - 1) % RimBins, after = (b + 1) % RimBins;
            for (int step = 0; step < UnderSteps; step++)
            {
                if (y > Mathf.Min(rim.under[b][step], Mathf.Min(rim.under[before][step], rim.under[after][step])))
                {
                    return step * UnderStep;
                }
            }
            return float.PositiveInfinity;
        }

        // A point of the hair below the cut, moved in toward the head's vertical line if it stands out further than the flare
        // from under the rim allows: a smooth minimum, so the hair runs into the flare with no crease, and never in past the skin.
        private Vector3 DrawnIn(Vector3 point, Rim rim)
        {
            float r;
            float way = WayRound(point, out r);
            float height = RimValue(rim.height, way);
            if (float.IsPositiveInfinity(height) || r < 0.0001f)
            {
                return point;
            }
            float radius = RimValue(rim.radius, way);
            float most = Mathf.Min(radius + HairFlare * (height - point.y), UnderHat(rim, way, point.y)) - RimInset;
            float share = Mathf.Clamp01(0.5f + 0.5f * (most - r) / FlareEase);
            float drawn = Mathf.Lerp(most, r, share) - FlareEase * share * (1f - share);
            // Never drawn in past the skin, the band kept above the rim (RimTuck) too: a hat leaves the skin it stands off
            // drawn, and hair drawn in under it there showed the forehead between the fringe and the hat seen from below.
            // Nor pushed out through the hat where it lies closer on the skin than that.
            float clear = Mathf.Min(SkinAt(way, point.y) + SkinClear, UnderHat(rim, way, point.y) - SkinClear);
            drawn = Mathf.Max(drawn, Mathf.Min(r, clear));
            float scale = drawn / r;
            return new Vector3(faceCentre.x + (point.x - faceCentre.x) * scale, point.y, faceCentre.z + (point.z - faceCentre.z) * scale);
        }
    }
}
