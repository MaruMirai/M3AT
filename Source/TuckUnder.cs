using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // A garment drawn under another kept under it in every body shape. HideGarments leaves out the inner garment's triangles
    // the outer covers at rest in the base shape, but not those over skin a pose brings out from under the outer's edge (its
    // opens: a parka's open collar, cuffs and armpits), which stay drawn and are hidden only by lying inside it. Each
    // garment's body keys were made on its own, so a key can move the inner further out than the outer: the Hulk key took a
    // T-shirt's neckline 0.013 cells inside the parka at rest to 0.025 outside it, and Fat and 1.5 strength did the same
    // to the shirt through the duster and jacket. So in each shape every place on the inner garment is looked at along its
    // normal for the outer surface it had over it at rest (within TuckRestBack under to TuckLook over), and pulled in
    // along its normal to keep under that surface by TuckClear more than it was at rest, or by TuckClear, whichever is
    // less (never pulled more than TuckMost), so where the two were cut from the same faces (the shoulders' tops) and lay
    // on each other at rest, they no longer fight for the same pixels once a key moves them (TuckClear is that generous
    // as the two are skinned with different weights: at 0.008 slivers of the shirt still came through a Hulk's posed
    // shoulders). The pull is spread over TuckBlend so the cloth doesn't crease. A place on the inner garment's open edge
    // (a neckline, hem or cuff) is pulled only by its own need, to TuckEdgeClear: the body under the garment is left out,
    // and a T-shirt's neckline pulled down with the cloth below it opened a gap under a Fat woman's chin in the jacket's
    // collar. Worked out at rest, once per shape, garment and mix of garments over it; the bones then pose it as before.
    public partial class SkinnedFigure
    {
        private const float TuckLook = 0.12f;       // cells: an outer surface this far over a place at rest counts as over it
        private const float TuckRestBack = 0.02f;   // cells: or this far under it (already a little through, as made)
        private const float TuckBack = 0.05f;       // cells: in a shape, the outer surface is looked for this far under the place
        private const float TuckClear = 0.015f;     // cells: kept this far under the outer surface, or this much further than at rest
        private const float TuckEdgeClear = 0.008f; // cells: what an open edge is kept under it (or as far as at rest, if nearer)
        private const float TuckMost = 0.06f;       // cells: the most a place is pulled in
        private const float TuckBlend = 0.06f;      // cells: the pull is averaged with weights falling smoothly to none here

        internal static bool TuckOff;   // for the autotest's pictures without it

#if AUTOTEST
        internal static readonly List<string> TuckNotes = new List<string>();   // each tuck worked out, for the autotest
#endif

        // A garment's places: copies split along a UV seam move as one (as LetOut).
        private sealed class TuckPlaces
        {
            public int[] place;     // by vertex
            public int[] first;     // by place: one of its vertices
            public bool[] edge;     // by place: on the garment's open edge (an edge with one triangle)
            public Vector3[] at;    // by place: where it is at rest
            public int[][] round;   // by place: the places within TuckBlend of it at rest (itself too), made when first tucked
            public float[][] share; // and each one's weight (Falloff at rest)
            public bool skip;       // not tucked under anything (rides one bone, or is let out)
        }

        // How one garment lies under another at rest: each place's gap and the outer's triangles its line could meet in any
        // shape (within TuckSlack of where it could at rest), so a shape tests only those. The packs' garments run to
        // thousands of triangles, and varied bodies make a shape for nearly every pawn.
        private sealed class TuckGap
        {
            public float[] gap;    // by place: NaN where the outer isn't over it
            public int[][] tris;   // by place: the outer's triangles to test (null where gap is NaN)
        }

        private const float TuckSlack = 0.1f;   // cells: how far a key may move the two apart along the garment from rest

        private readonly Dictionary<int, TuckPlaces> tuckPlaces = new Dictionary<int, TuckPlaces>();
        private readonly Dictionary<(int, int), TuckGap> tuckGaps = new Dictionary<(int, int), TuckGap>();

        // Garment g's rest pose in shape s (shaped, as GarmentShape gave it) kept under the garments in over.
        internal PieceShape TuckedShape(Shape s, int g, GarmentMask over, PieceShape shaped)
        {
            if (TuckOff || over == 0 || g <= 0 || g > MaxHiding || g == CoatPiece || pieces[g].defs.Length == 0
                || shaped != ShapedPiece(s, g))
            {
                return shaped;   // (a let-out garment goes over what is under it, so it has its own shape)
            }
            TuckPlaces tp = TuckPlacesOf(g);
            if (tp.skip)
            {
                return shaped;
            }
            (int, GarmentMask) key = (g, over);
            PieceShape tucked;
            if (s.tucked.TryGetValue(key, out tucked))
            {
                return tucked;
            }
#if AUTOTEST
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
#endif
            int n = tp.first.Length;
            Vector3[] way = PlaceNormals(shaped.rest, pieces[g].tris, tp);
            float[] need = new float[n];
            bool any = false;
            for (int h = 1; h < pieces.Length && h <= MaxHiding; h++)
            {
                if ((over & GarmentBit(h)) == 0 || !TuckUnderOuter(h))
                {
                    continue;
                }
                TuckGap rested = TuckGaps(g, h, tp);
                float[] gaps = rested.gap;
                PieceShape outer = ShapedPiece(s, h);
                for (int k = 0; k < n; k++)
                {
                    if (float.IsNaN(gaps[k]))
                    {
                        continue;
                    }
                    float gap = NearestHit(outer.rest, pieces[h].tris, rested.tris[k], shaped.rest[tp.first[k]], way[k], -TuckBack, TuckLook, gaps[k]);
                    if (float.IsNaN(gap))
                    {
                        continue;
                    }
                    float keep = tp.edge[k] ? Mathf.Min(gaps[k], TuckEdgeClear) : Mathf.Min(gaps[k] + TuckClear, TuckClear);
                    float pull = Mathf.Min(keep - gap, TuckMost);
                    if (pull > need[k])
                    {
                        need[k] = pull;
                        any = true;
                    }
                }
            }
            if (!any)
            {
                s.tucked[key] = shaped;
                return shaped;
            }
            Round(g, tp);
            float[] push = new float[n];
            float most = 0f;
            for (int k = 0; k < n; k++)
            {
                if (tp.edge[k])
                {
                    push[k] = need[k];
                    most = Mathf.Max(most, push[k]);
                    continue;
                }
                int[] round = tp.round[k];
                float[] share = tp.share[k];
                float sum = 0f, weights = 0f;
                for (int r = 0; r < round.Length; r++)
                {
                    sum += need[round[r]] * share[r];
                    weights += share[r];
                }
                push[k] = Mathf.Max(weights > 0f ? sum / weights : 0f, need[k]);
                most = Mathf.Max(most, push[k]);
            }
            Vector3[] rest = new Vector3[shaped.rest.Length];
            for (int i = 0; i < rest.Length; i++)
            {
                int k = tp.place[i];
                rest[i] = shaped.rest[i] - way[k] * push[k];
            }
            // The normals and outline hull kept: the pull is at most a few hundredths of a cell, and its shading the shape's.
            tucked = new PieceShape { rest = rest, normal = shaped.normal, hull = shaped.hull, shell = shaped.shell };
            s.tucked[key] = tucked;
#if AUTOTEST
            List<string> overNames = new List<string>();
            for (int h = 1; h < pieces.Length && h <= MaxHiding; h++)
            {
                if ((over & GarmentBit(h)) != 0)
                {
                    overNames.Add(pieces[h].name);
                }
            }
            TuckNotes.Add(pieces[g].name + " tucked under " + string.Join("+", overNames) + ": at most " + most.ToString("F3") + " cells, "
                + clock.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
#endif
            return tucked;
        }

        // Whether a garment under h is kept under it: not a hat or mask riding one bone whole (it moves apart from the
        // trunk), nor let-out webbing (pushed out past what is under it already).
        private bool TuckUnderOuter(int h)
        {
            return pieces[h].defs.Length > 0 && RiddenBone(pieces[h]) < 0 && !LetsOut(h);
        }

        private TuckPlaces TuckPlacesOf(int g)
        {
            TuckPlaces tp;
            if (tuckPlaces.TryGetValue(g, out tp))
            {
                return tp;
            }
            Piece piece = pieces[g];
            Dictionary<Vector3, int> placeOf = new Dictionary<Vector3, int>();
            List<int> first = new List<int>();
            int[] place = new int[piece.rest.Length];
            for (int i = 0; i < place.Length; i++)
            {
                int k;
                if (!placeOf.TryGetValue(piece.rest[i], out k))
                {
                    k = first.Count;
                    placeOf[piece.rest[i]] = k;
                    first.Add(i);
                }
                place[i] = k;
            }
            // An edge between two places used by one triangle only is on the open edge.
            Dictionary<long, int> uses = new Dictionary<long, int>();
            int[] tris = piece.tris;
            for (int f = 0; f + 2 < tris.Length; f += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = place[tris[f + e]], b = place[tris[f + (e + 1) % 3]];
                    long edgeKey = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    int count;
                    uses.TryGetValue(edgeKey, out count);
                    uses[edgeKey] = count + 1;
                }
            }
            bool[] edge = new bool[first.Count];
            foreach (KeyValuePair<long, int> use in uses)
            {
                if (use.Value == 1)
                {
                    edge[(int)(use.Key >> 32)] = true;
                    edge[(int)(use.Key & 0xffffffffL)] = true;
                }
            }
            Vector3[] at = new Vector3[first.Count];
            for (int k = 0; k < at.Length; k++)
            {
                at[k] = piece.rest[first[k]];
            }
            tp = new TuckPlaces { place = place, first = first.ToArray(), edge = edge, at = at, skip = RiddenBone(piece) >= 0 || LetsOut(g) };
            tuckPlaces[g] = tp;
            return tp;
        }

        // Each place's gap to garment h's surface along its normal at rest in the base shape (the nearest within
        // TuckRestBack under to TuckLook over), and the triangles of h a shape's test looks at.
        private TuckGap TuckGaps(int g, int h, TuckPlaces tp)
        {
            TuckGap rested;
            if (tuckGaps.TryGetValue((g, h), out rested))
            {
                return rested;
            }
            Vector3[] rest = pieces[g].rest;
            Vector3[] way = PlaceNormals(rest, pieces[g].tris, tp);
            Vector3[] outer = pieces[h].rest;
            int[] outerTris = pieces[h].tris;
            Bounds[] bounds = TriBounds(outer, outerTris);
            int[] all = new int[bounds.Length];
            for (int f = 0; f < all.Length; f++)
            {
                all[f] = f;
            }
            rested = new TuckGap { gap = new float[tp.first.Length], tris = new int[tp.first.Length][] };
            List<int> near = new List<int>();
            for (int k = 0; k < rested.gap.Length; k++)
            {
                Vector3 at = rest[tp.first[k]];
                rested.gap[k] = NearestHit(outer, outerTris, all, at, way[k], -TuckRestBack, TuckLook, 0f, bounds);
                if (float.IsNaN(rested.gap[k]))
                {
                    continue;
                }
                // The line as far as a shape's test reaches, grown by how far a key may move either garment.
                Bounds line = new Bounds(at - way[k] * TuckBack, Vector3.zero);
                line.Encapsulate(at + way[k] * TuckLook);
                line.Expand(2f * TuckSlack);
                near.Clear();
                for (int f = 0; f < bounds.Length; f++)
                {
                    if (bounds[f].Intersects(line))
                    {
                        near.Add(f);
                    }
                }
                rested.tris[k] = near.ToArray();
            }
            tuckGaps[(g, h)] = rested;
            return rested;
        }

        // The places round each place within TuckBlend at rest and their weights, for spreading the pull; made once a garment.
        private static void Round(int g, TuckPlaces tp)
        {
            if (tp.round != null)
            {
                return;
            }
            int n = tp.first.Length;
            tp.round = new int[n][];
            tp.share = new float[n][];
            List<int> round = new List<int>();
            List<float> share = new List<float>();
            for (int k = 0; k < n; k++)
            {
                round.Clear();
                share.Clear();
                Vector3 at = tp.at[k];
                for (int j = 0; j < n; j++)
                {
                    float w = Falloff(at, tp.at[j], TuckBlend);
                    if (w > 0f)
                    {
                        round.Add(j);
                        share.Add(w);
                    }
                }
                tp.round[k] = round.ToArray();
                tp.share[k] = share.ToArray();
            }
        }

        // The area-weighted normal round each place, over the triangles of all its copies.
        private static Vector3[] PlaceNormals(Vector3[] rest, int[] tris, TuckPlaces tp)
        {
            Vector3[] sum = new Vector3[tp.first.Length];
            for (int f = 0; f + 2 < tris.Length; f += 3)
            {
                Vector3 a = rest[tris[f]];
                Vector3 n = Vector3.Cross(rest[tris[f + 1]] - a, rest[tris[f + 2]] - a);
                sum[tp.place[tris[f]]] += n;
                sum[tp.place[tris[f + 1]]] += n;
                sum[tp.place[tris[f + 2]]] += n;
            }
            for (int k = 0; k < sum.Length; k++)
            {
                sum[k] = sum[k].sqrMagnitude > 1e-20f ? sum[k].normalized : Vector3.up;
            }
            return sum;
        }

        private static Bounds[] TriBounds(Vector3[] rest, int[] tris)
        {
            Bounds[] b = new Bounds[tris.Length / 3];
            for (int f = 0; f < b.Length; f++)
            {
                b[f] = new Bounds(rest[tris[3 * f]], Vector3.zero);
                b[f].Encapsulate(rest[tris[3 * f + 1]]);
                b[f].Encapsulate(rest[tris[3 * f + 2]]);
            }
            return b;
        }

        // Of the places the line through at along way meets the surface (either side of its triangles, those listed in
        // tris) from low to high along it, the one nearest near; NaN for none. With bounds, a triangle whose bounds miss
        // the line is passed over without the full test.
        private static float NearestHit(Vector3[] rest, int[] allTris, int[] tris, Vector3 at, Vector3 way, float low, float high, float near,
            Bounds[] bounds = null)
        {
            Vector3 from = at + way * low;
            Bounds line = new Bounds(from, Vector3.zero);
            line.Encapsulate(at + way * high);
            float best = float.NaN;
            foreach (int f in tris)
            {
                if (bounds != null && !bounds[f].Intersects(line))
                {
                    continue;
                }
                Vector3 a = rest[allTris[3 * f]];
                Vector3 e1 = rest[allTris[3 * f + 1]] - a, e2 = rest[allTris[3 * f + 2]] - a;
                Vector3 q = Vector3.Cross(way, e2);
                float det = Vector3.Dot(e1, q);
                if (Mathf.Abs(det) < 1e-12f)
                {
                    continue;
                }
                float inv = 1f / det;
                Vector3 s = from - a;
                float u = Vector3.Dot(s, q) * inv;
                if (u < 0f || u > 1f)
                {
                    continue;
                }
                Vector3 r = Vector3.Cross(s, e1);
                float v = Vector3.Dot(way, r) * inv;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }
                float t = Vector3.Dot(e2, r) * inv + low;
                if (t < low || t > high)
                {
                    continue;
                }
                if (float.IsNaN(best) || Mathf.Abs(t - near) < Mathf.Abs(best - near))
                {
                    best = t;
                }
            }
            return best;
        }
    }
}
