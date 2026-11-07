using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;

namespace M3AT
{
    // Webbing let out over what is under it. A garment for apparel on a let-out layer (LetOutLayers: Combat Extended's
    // Webbing, the tactical vest and its bandolier) is made close on the skin, as webbing is worn over a shirt; the game also
    // lets it over body armour, which stands up to a tenth of a cell off the skin, and it would be drawn sunk inside it. So
    // worn over other garments it is pushed out past them: each place on it moved along the body's normal under it (not its
    // own, which on a strap's inside points into the body) to CLEAR over the outermost of them along that line, and the push
    // spread so it doesn't tear the garment: in full over LetOutHold round each place, so a strap keeps its thickness and
    // its rounds their shape, then tapering at LetOutSlope, then smoothed over LetOutBlend, never below what a place needs.
    // The body's normal turns fast where a strap crosses the shoulder or the neck's side, and a tenth of a cell out along
    // ways that far apart a strap's inside and outside, and its two edges, end up several times as far apart, folding it
    // into a flap; so each place's way is first averaged with those round it (LetOutTurn), and neighbours go out together.
    // Worked out at rest, once per shape, garment and mix of garments under it; the bones then pose it as before, with
    // the body's weights it already has.
    public partial class SkinnedFigure
    {
        private static readonly string[] LetOutLayers = { "Webbing" };
        // Apparel let out though on no let-out layer: the slave collar, made close round the bare neck, over a shirt's collar
        // or armour's neck ring.
        private static readonly string[] LetOutDefs = { "Apparel_Collar" };
        private const float LetOutClear = 0.006f;   // cells over what is under it (about the webbing's own offset off a shirt)
        private const float LetOutBack = 0.05f;     // cells: a surface this far under a place along its line still counts
        private const float LetOutFar = 0.2f;       // cells: and this far over it (armour stands up to about 0.11 off the skin)
        private const float LetOutHold = 0.05f;     // cells round a place that take its whole push
        private const float LetOutSlope = 0.7f;     // cells of push lost per cell further on
        private const float LetOutBlend = 0.1f;     // cells: the push is then averaged with weights falling smoothly to none here
        private const float LetOutTurn = 0.12f;     // cells: the ways out are averaged with weights falling smoothly to none here
        private const int LetOutSamples = 6;        // a triangle is looked at at points this many steps apart along each edge
        private const int LetOutRounds = 20;        // passes raising a triangle's corners until its points are clear

        private bool[] letsOut;              // by piece: drawn for apparel on a let-out layer
        private ApparelProperties[] pieceApparel;   // by piece: the apparel it is drawn for (its first def the game has)

        // The apparel garment g is drawn for, as GoesOver weighs it; null for none the game has (or the body).
        private ApparelProperties PieceApparel(int g)
        {
            if (pieceApparel == null)
            {
                pieceApparel = new ApparelProperties[pieces.Length];
                letsOut = new bool[pieces.Length];
                for (int i = 1; i < pieces.Length; i++)
                {
                    foreach (string def in pieces[i].defs)
                    {
                        ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
                        if (d != null && d.apparel != null)
                        {
                            pieceApparel[i] = d.apparel;
                            break;
                        }
                    }
                    ApparelProperties ap = pieceApparel[i];
                    letsOut[i] = ap != null && (ap.layers != null && ap.layers.Exists(l => System.Array.IndexOf(LetOutLayers, l.defName) >= 0)
                        || System.Array.Exists(pieces[i].defs, d => System.Array.IndexOf(LetOutDefs, d) >= 0));
                }
            }
            return pieceApparel[g];
        }

        private bool LetsOut(int g)
        {
            PieceApparel(g);
            return g > 0 && g < letsOut.Length && letsOut[g];
        }

        // The garments worn in mask that garment g, if it is let out, goes over; 0 if it isn't let out or goes over none.
        private GarmentMask LetOutUnder(int g, GarmentMask mask)
        {
            if (!LetsOut(g) || (PackMask & (g <= MaxHiding ? GarmentBit(g) : 0)) != 0)
            {
                return 0;
            }
            ApparelProperties outer = PieceApparel(g);
            GarmentMask under = 0;
            for (int h = 1; h < pieces.Length && h <= MaxHiding; h++)
            {
                if (h != g && (mask & (GarmentBit(h))) != 0 && (PackMask & (GarmentBit(h))) == 0 && pieces[h].defs.Length > 0)
                {
                    ApparelProperties inner = PieceApparel(h);
                    if (inner != null && GoesOver(outer, inner))
                    {
                        under |= GarmentBit(h);
                    }
                }
            }
            return under;
        }

        // Garment g's rest pose in shape s as it is drawn with the garments in mask worn: let out over those under it.
        internal PieceShape GarmentShape(Shape s, int g, GarmentMask mask)
        {
            GarmentMask under = g > 0 ? LetOutUnder(g, mask) : 0;
            if (under == 0)
            {
                return ShapedPiece(s, g);
            }
            (int, GarmentMask) key = (g, under);
            PieceShape shaped;
            if (s.letOut.TryGetValue(key, out shaped))
            {
                return shaped;
            }
            PieceShape plain = ShapedPiece(s, g);
            PieceShape body = ShapedPiece(s, 0);
            List<Vector3[]> rests = new List<Vector3[]>();
            List<int[]> tris = new List<int[]>();
            for (int h = 1; h < pieces.Length && h <= MaxHiding; h++)
            {
                if ((under & (GarmentBit(h))) != 0)
                {
                    rests.Add(ShapedPiece(s, h).rest);
                    tris.Add(pieces[h].tris);
                }
            }
            Vector3[] rest = LetOut(plain.rest, pieces[g].tris, body.rest, body.normal, rests, tris);
            Vector3[] normal = Normals(rest, pieces[g].tris);
            shaped = new PieceShape { rest = rest, normal = normal, hull = SmoothHull(normal) };
            s.letOut[key] = shaped;
            return shaped;
        }

        // The garment's rest places (rest, tris) let out over the surfaces (rests, tris) by the body's (bodyRest, bodyNormal).
        private static Vector3[] LetOut(Vector3[] rest, int[] tris, Vector3[] bodyRest, Vector3[] bodyNormal, List<Vector3[]> rests, List<int[]> underTris)
        {
            // Copies split along a UV seam move as one: work on places.
            Dictionary<Vector3, int> placeOf = new Dictionary<Vector3, int>();
            List<Vector3> places = new List<Vector3>();
            int[] place = new int[rest.Length];
            for (int i = 0; i < rest.Length; i++)
            {
                int k;
                if (!placeOf.TryGetValue(rest[i], out k))
                {
                    k = places.Count;
                    placeOf[rest[i]] = k;
                    places.Add(rest[i]);
                }
                place[i] = k;
            }
            int n = places.Count;
            Vector3[] way = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                way[k] = BodyWay(places[k], bodyRest, bodyNormal);
            }
            way = Averaged(places, way, LetOutTurn);
            // Each surface's triangles' bounds, so most are passed over without the full test.
            List<Bounds[]> bounds = new List<Bounds[]>();
            for (int u = 0; u < rests.Count; u++)
            {
                Vector3[] r = rests[u];
                int[] t = underTris[u];
                Bounds[] b = new Bounds[t.Length / 3];
                for (int f = 0; f < b.Length; f++)
                {
                    Vector3 a = r[t[3 * f]], c1 = r[t[3 * f + 1]], c2 = r[t[3 * f + 2]];
                    b[f] = new Bounds(a, Vector3.zero);
                    b[f].Encapsulate(c1);
                    b[f].Encapsulate(c2);
                }
                bounds.Add(b);
            }
            float[] need = new float[n];
            for (int k = 0; k < n; k++)
            {
                need[k] = Need(places[k], way[k], rests, underTris, bounds);
            }
            // Points across each triangle, as a big one may span a bump between its corners. Its corners are raised until
            // their share at each point reaches what it needs, no corner past the most any point on its triangles needs.
            List<int> sampleTri = new List<int>();
            List<Vector3> sampleAt = new List<Vector3>();
            List<float> sampleNeed = new List<float>();
            float[] cap = (float[])need.Clone();
            for (int f = 0; f + 2 < tris.Length; f += 3)
            {
                int a = place[tris[f]], b = place[tris[f + 1]], c = place[tris[f + 2]];
                for (int i = 1; i < LetOutSamples; i++)
                {
                    for (int j = 1; i + j < LetOutSamples; j++)
                    {
                        Vector3 bc = new Vector3(i, j, LetOutSamples - i - j) / LetOutSamples;
                        Vector3 at = places[a] * bc.x + places[b] * bc.y + places[c] * bc.z;
                        Vector3 w = (way[a] * bc.x + way[b] * bc.y + way[c] * bc.z).normalized;
                        float need1 = Need(at, w, rests, underTris, bounds);
                        if (need1 <= 0f)
                        {
                            continue;
                        }
                        sampleTri.Add(f);
                        sampleAt.Add(bc);
                        sampleNeed.Add(need1);
                        cap[a] = Mathf.Max(cap[a], need1);
                        cap[b] = Mathf.Max(cap[b], need1);
                        cap[c] = Mathf.Max(cap[c], need1);
                    }
                }
            }
            for (int round = 0; round < LetOutRounds; round++)
            {
                for (int q = 0; q < sampleTri.Count; q++)
                {
                    int f = sampleTri[q];
                    int a = place[tris[f]], b = place[tris[f + 1]], c = place[tris[f + 2]];
                    Vector3 bc = sampleAt[q];
                    float shortBy = sampleNeed[q] - (need[a] * bc.x + need[b] * bc.y + need[c] * bc.z);
                    if (shortBy > 0f)
                    {
                        float per = shortBy / bc.sqrMagnitude;
                        need[a] = Mathf.Min(need[a] + bc.x * per, cap[a]);
                        need[b] = Mathf.Min(need[b] + bc.y * per, cap[b]);
                        need[c] = Mathf.Min(need[c] + bc.z * per, cap[c]);
                    }
                }
            }
            // Spread: the most any place within reach asks of this one, then averaged, never below its own need.
            float[] spread = new float[n];
            for (int k = 0; k < n; k++)
            {
                float most = 0f;
                for (int j = 0; j < n; j++)
                {
                    if (need[j] > most)
                    {
                        most = Mathf.Max(most, need[j] - LetOutSlope * Mathf.Max(0f, Vector3.Distance(places[k], places[j]) - LetOutHold));
                    }
                }
                spread[k] = most;
            }
            Vector3[] moved = new Vector3[rest.Length];
            float[] push = new float[n];
            for (int k = 0; k < n; k++)
            {
                float sum = 0f, weights = 0f;
                for (int j = 0; j < n; j++)
                {
                    float w = Falloff(places[k], places[j], LetOutBlend);
                    sum += spread[j] * w;
                    weights += w;
                }
                push[k] = Mathf.Max(sum / weights, need[k]);
            }
            for (int i = 0; i < rest.Length; i++)
            {
                moved[i] = rest[i] + way[place[i]] * push[place[i]];
            }
            return moved;
        }

        // A weight falling smoothly from 1 at a to none at reach from it (as the square of 1 - (d / reach)^2), so a place's
        // neighbours' weights don't jump as one comes into reach, as a hard cut-off's do.
        private static float Falloff(Vector3 a, Vector3 b, float reach)
        {
            float t = 1f - (a - b).sqrMagnitude / (reach * reach);
            return t > 0f ? t * t : 0f;
        }

        // Each place's way averaged with those of the places within reach of it, by Falloff, and made a unit again.
        private static Vector3[] Averaged(List<Vector3> places, Vector3[] way, float reach)
        {
            int n = places.Count;
            Vector3[] averaged = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                Vector3 sum = Vector3.zero;
                for (int j = 0; j < n; j++)
                {
                    sum += way[j] * Falloff(places[k], places[j], reach);
                }
                averaged[k] = sum.sqrMagnitude > 1e-12f ? sum.normalized : way[k];
            }
            return averaged;
        }

        // The body's outward normal by a point: its four nearest body vertices', by how near.
        private static Vector3 BodyWay(Vector3 at, Vector3[] bodyRest, Vector3[] bodyNormal)
        {
            const int Nearest = 4;
            int[] best = new int[Nearest];
            float[] bestD = new float[Nearest];
            for (int k = 0; k < Nearest; k++)
            {
                best[k] = -1;
                bestD[k] = float.MaxValue;
            }
            for (int i = 0; i < bodyRest.Length; i++)
            {
                float d = (bodyRest[i] - at).sqrMagnitude;
                if (d >= bestD[Nearest - 1])
                {
                    continue;
                }
                int k = Nearest - 1;
                while (k > 0 && bestD[k - 1] > d)
                {
                    best[k] = best[k - 1];
                    bestD[k] = bestD[k - 1];
                    k--;
                }
                best[k] = i;
                bestD[k] = d;
            }
            Vector3 way = Vector3.zero;
            for (int k = 0; k < Nearest; k++)
            {
                if (best[k] >= 0)
                {
                    way += bodyNormal[best[k]] / (Mathf.Sqrt(bestD[k]) + 1e-4f);
                }
            }
            return way.sqrMagnitude > 0f ? way.normalized : Vector3.up;
        }

        // How far a point must go out along way to be LetOutClear over the surfaces, 0 if it already is.
        private static float Need(Vector3 at, Vector3 way, List<Vector3[]> rests, List<int[]> tris, List<Bounds[]> bounds)
        {
            float top = float.NegativeInfinity;
            for (int u = 0; u < rests.Count; u++)
            {
                top = Mathf.Max(top, SurfaceTop(at, way, rests[u], tris[u], bounds[u]));
            }
            return float.IsNegativeInfinity(top) ? 0f : Mathf.Max(0f, top + LetOutClear);
        }

        // The surface's outermost point on the line through at along way, measured along it from at: the last place the line
        // comes out of it before it goes into another part of it (the arm, from the side of the chest). Negative infinity
        // for none within LetOutBack under to LetOutFar over.
        private static float SurfaceTop(Vector3 at, Vector3 way, Vector3[] rest, int[] tris, Bounds[] bounds)
        {
            Vector3 from = at - way * LetOutBack, to = at + way * LetOutFar;
            Bounds line = new Bounds(from, Vector3.zero);
            line.Encapsulate(to);
            float length = LetOutBack + LetOutFar;
            List<KeyValuePair<float, bool>> hits = null;   // along the line from at, and whether it comes out there
            for (int f = 0; f < bounds.Length; f++)
            {
                if (!bounds[f].Intersects(line))
                {
                    continue;
                }
                Vector3 a = rest[tris[3 * f]];
                Vector3 e1 = rest[tris[3 * f + 1]] - a, e2 = rest[tris[3 * f + 2]] - a;
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
                float t = Vector3.Dot(e2, r) * inv;
                if (t < 0f || t > length)
                {
                    continue;
                }
                (hits = hits ?? new List<KeyValuePair<float, bool>>()).Add(
                    new KeyValuePair<float, bool>(t - LetOutBack, Vector3.Dot(Vector3.Cross(e1, e2), way) > 0f));
            }
            if (hits == null)
            {
                return float.NegativeInfinity;
            }
            hits.Sort((x, y) => x.Key.CompareTo(y.Key));
            float best = float.NegativeInfinity;
            foreach (KeyValuePair<float, bool> hit in hits)
            {
                if (hit.Value)
                {
                    best = hit.Key;
                }
                else if (hit.Key > Mathf.Max(best, 0f))
                {
                    break;   // into another part of it
                }
            }
            return best;
        }

        // Garment h's rest places in the base shape let out over garment g alone, for UnderOf; null if h isn't let out over g.
        private Vector3[] LetOutOver(int h, int g)
        {
            if (!LetsOut(h) || g <= 0 || g > MaxHiding || LetOutUnder(h, GarmentBit(g)) == 0)
            {
                return null;
            }
            Vector3[] bodyNormal = Normals(pieces[0].rest, pieces[0].tris);
            return LetOut(pieces[h].rest, pieces[h].tris, pieces[0].rest, bodyNormal,
                new List<Vector3[]> { pieces[g].rest }, new List<int[]> { pieces[g].tris });
        }
    }
}
