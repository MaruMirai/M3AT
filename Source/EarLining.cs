using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Ears with a paler, fluffy inside, from a texture rather than painted vertex groups: as an ear is read, its triangles
    // facing forward (InsideFacing) are its inside, or those with all three corners in the
    // file's inner group where it has one. The vertices the inside shares with the outside are split, and each side takes
    // its own half of the ear's texture (Models/chibi/ear_*.png, anim_scripts/make_ear_textures.py): the outside on the
    // left half, fur streaks; the inside on the right, fluff. Its places go along the ear from root to tip (v) and across
    // it from edge to edge (u), so a texture painted once fits every ear shape. Split, each copy's outline is still pushed
    // out the way both share (LinedHull), so it doesn't tear along the rim.
    // The fluffy ears are drawn all in the hair's colour, and the shader (MarusSmooth.frag.glsl's _Lining, LiningPale here)
    // pales the inside per pixel by the texture's alpha: none at the rim, fading in through tufts toward the middle. Nearly
    // every vertex of the inside is on its rim, so fading it by vertex colour (as until Nov 2026) could only step from the
    // hair's colour to the pale at the rim itself, a hard border, and the pale patch was flat and chalky. Ratkin's mouse
    // ears keep their pink lining as vertex colour (the inner colour), with no alpha in their texture.
    public sealed partial class SkinnedFigure
    {
        private const string FluffyEars = "ear_fluffy.png", MouseEars = "ear_mouse.png";
        private const float LiningHalf = 0.23f;   // of the texture's width, half an ear's width in its half (0.25 is the whole half)
        // A triangle facing forward past this is the inside. Lower than Kiiro's LiningFacing (0.45), which left the cat ear's
        // head-side flap (folded forward, about 0.2 to 0.45) outside, and above 0 a strip along its crease (0.01 to 0.08)
        // stayed outside: each was a hair-coloured band across the pale. At 0 the whole front is inside, so the pale fades
        // out only at the ear's real rim (TidyInside drops the bits of the back this takes in).
        private const float InsideFacing = 0f;
        private const float LiningPale = 1f;      // the fluffy ears' inside: the shader's _Lining, times the texture's alpha

        private static readonly Dictionary<string, Texture2D> earTextures = new Dictionary<string, Texture2D>();

        // The lining texture an ear file takes, null for none (the pig's are bare skin; the floppy ears keep the grain).
        private static string LiningTextureOf(string file, bool race)
        {
            if (race || file == CatEarFile || file == "race_wolfein_ears.txt" || file == "race_miho_ears.txt")
            {
                return FluffyEars;   // the gene's cat ears, Kiiro's (made from them), the Wolfein's and Miho's (from Wolfein's)
            }
            return file == "race_ratkin_ears.txt" ? MouseEars : null;
        }

        private Texture2D EarTexture(string name)
        {
            Texture2D t;
            if (earTextures.TryGetValue(name, out t))
            {
                return t;
            }
            string path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                t.LoadImage(File.ReadAllBytes(path));
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Trilinear;
                t.name = "Marus_Ear_" + Path.GetFileNameWithoutExtension(name);
            }
            earTextures[name] = t;
            return t;
        }

        // Splits the ears' inside from their outside (before their keys and shapes are made): the inside's triangles get their
        // own copies of the corners they share with the outside, with the same weights and key moves. e.inner is then the
        // inside's vertices. Returns each new copy's original (twins), for LinedHull.
        private List<int> SplitLining(Ears e, ref List<int>[] bones, ref List<float>[] weights)
        {
            Piece piece = e.piece;
            int n = piece.rest.Length;
            int[] tris = piece.tris;
            bool painted = false;
            for (int v = 0; v < n && !painted; v++)
            {
                painted = e.inner[v];
            }
            // Each ear's face normals turned outward if most point in toward its own middle (as RaceEars does), so the
            // inside is the side facing forward whatever way the file winds them.
            int[] sideOf = new int[n];
            Vector3[] middle = new Vector3[2];
            int[] count = new int[2];
            for (int v = 0; v < n; v++)
            {
                sideOf[v] = bones[v][0] < EarMostBones ? 0 : 1;
                middle[sideOf[v]] += piece.rest[v];
                count[sideOf[v]]++;
            }
            for (int s = 0; s < 2; s++)
            {
                middle[s] /= Mathf.Max(count[s], 1);
            }
            float[] outward = new float[2];
            Vector3[] faceNormal = new Vector3[tris.Length / 3];
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 a = piece.rest[tris[t]], b = piece.rest[tris[t + 1]], c = piece.rest[tris[t + 2]];
                Vector3 nrm = Vector3.Cross(b - a, c - a);
                faceNormal[t / 3] = nrm;
                outward[sideOf[tris[t]]] += Vector3.Dot(nrm, (a + b + c) / 3f - middle[sideOf[tris[t]]]);
            }
            bool[] insideTri = new bool[tris.Length / 3];
            float[] facing = new float[tris.Length / 3];   // each triangle's normal along the front (+z)
            for (int t = 0; t < tris.Length; t += 3)
            {
                if (painted)
                {
                    insideTri[t / 3] = e.inner[tris[t]] && e.inner[tris[t + 1]] && e.inner[tris[t + 2]];
                    continue;
                }
                Vector3 nrm = faceNormal[t / 3] * (outward[sideOf[tris[t]]] < 0f ? -1f : 1f);
                facing[t / 3] = nrm.sqrMagnitude > 0f ? nrm.normalized.z : -1f;
                insideTri[t / 3] = facing[t / 3] > InsideFacing;
            }
            if (!painted)
            {
                TidyInside(tris, insideTri, sideOf, facing);
            }
            // Which vertices each side uses: one used by both gets a copy for the inside.
            bool[] usedOut = new bool[n], usedIn = new bool[n];
            for (int t = 0; t < tris.Length; t++)
            {
                if (insideTri[t / 3])
                {
                    usedIn[tris[t]] = true;
                }
                else
                {
                    usedOut[tris[t]] = true;
                }
            }
            List<Vector3> rest = new List<Vector3>(piece.rest);
            List<int> twins = new List<int>();
            int[] insideCopy = new int[n];
            for (int v = 0; v < n; v++)
            {
                insideCopy[v] = v;
                if (usedIn[v] && usedOut[v])
                {
                    insideCopy[v] = rest.Count;
                    rest.Add(piece.rest[v]);
                    twins.Add(v);
                }
            }
            int[] newTris = (int[])tris.Clone();
            for (int t = 0; t < tris.Length; t++)
            {
                if (insideTri[t / 3])
                {
                    newTris[t] = insideCopy[tris[t]];
                }
            }
            int total = rest.Count;
            bool[] inner = new bool[total];
            for (int t = 0; t < newTris.Length; t++)
            {
                if (insideTri[t / 3])
                {
                    inner[newTris[t]] = true;
                }
            }
            foreach (string name in new List<string>(piece.keys.Keys))
            {
                Vector3[] key = piece.keys[name], longer = new Vector3[total];
                Array.Copy(key, longer, Math.Min(key.Length, n));
                for (int i = 0; i < twins.Count; i++)
                {
                    longer[n + i] = key.Length > twins[i] ? key[twins[i]] : Vector3.zero;
                }
                piece.keys[name] = longer;
            }
            Array.Resize(ref bones, total);
            Array.Resize(ref weights, total);
            for (int i = 0; i < twins.Count; i++)
            {
                bones[n + i] = bones[twins[i]];
                weights[n + i] = weights[twins[i]];
            }
            piece.rest = rest.ToArray();
            piece.tris = newTris;
            piece.normal = null;   // worked out again, split along the rim
            e.inner = inner;
            return twins;
        }

        // The inside tidied: only each ear's largest piece of inside triangles (joined by edges) is kept, as the cat ear's
        // back has a few triangles near the root facing forward (5 and 1 a side, apart from the inside), which otherwise
        // took tufts of their own; then a triangle facing forward less (down to CreaseFacing) with inside on two or more
        // of its edges is taken in, and the largest piece kept again.
        private const float CreaseFacing = -0.2f;

        private static void TidyInside(int[] tris, bool[] insideTri, int[] sideOf, float[] facing)
        {
            Dictionary<long, List<int>> byEdge = new Dictionary<long, List<int>>();
            int count = tris.Length / 3;
            for (int t = 0; t < count; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    long key = EdgeKey(tris[t * 3 + k], tris[t * 3 + (k + 1) % 3]);
                    List<int> list;
                    if (!byEdge.TryGetValue(key, out list))
                    {
                        list = new List<int>();
                        byEdge[key] = list;
                    }
                    list.Add(t);
                }
            }
            KeepLargest(tris, insideTri, sideOf, byEdge);
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int t = 0; t < count; t++)
                {
                    if (insideTri[t] || facing[t] <= CreaseFacing)
                    {
                        continue;
                    }
                    int sides = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        foreach (int w in byEdge[EdgeKey(tris[t * 3 + k], tris[t * 3 + (k + 1) % 3])])
                        {
                            if (w != t && insideTri[w])
                            {
                                sides++;
                                break;
                            }
                        }
                    }
                    if (sides >= 2)
                    {
                        insideTri[t] = true;
                        changed = true;
                    }
                }
            }
            KeepLargest(tris, insideTri, sideOf, byEdge);
        }

        private static long EdgeKey(int a, int b)
        {
            return ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
        }

        // Leaves only each ear's largest piece of inside triangles inside (pieces joined by edges).
        private static void KeepLargest(int[] tris, bool[] insideTri, int[] sideOf, Dictionary<long, List<int>> byEdge)
        {
            int count = tris.Length / 3;
            int[] piece = new int[count];
            for (int t = 0; t < count; t++)
            {
                piece[t] = -1;
            }
            List<int> sizes = new List<int>();
            for (int t = 0; t < count; t++)
            {
                if (!insideTri[t] || piece[t] >= 0)
                {
                    continue;
                }
                int id = sizes.Count, size = 0;
                Stack<int> open = new Stack<int>();
                open.Push(t);
                piece[t] = id;
                while (open.Count > 0)
                {
                    int u = open.Pop();
                    size++;
                    for (int k = 0; k < 3; k++)
                    {
                        foreach (int w in byEdge[EdgeKey(tris[u * 3 + k], tris[u * 3 + (k + 1) % 3])])
                        {
                            if (insideTri[w] && piece[w] < 0)
                            {
                                piece[w] = id;
                                open.Push(w);
                            }
                        }
                    }
                }
                sizes.Add(size);
            }
            int[] best = { -1, -1 };   // the largest piece on each ear (by its first corner's ear)
            for (int t = 0; t < count; t++)
            {
                if (piece[t] >= 0)
                {
                    int side = sideOf[tris[t * 3]];
                    if (best[side] < 0 || sizes[piece[t]] > sizes[best[side]])
                    {
                        best[side] = piece[t];
                    }
                }
            }
            for (int t = 0; t < count; t++)
            {
                if (piece[t] >= 0 && piece[t] != best[sideOf[tris[t * 3]]])
                {
                    insideTri[t] = false;
                }
            }
        }

        // Each split copy's outline pushed out the way the two share, at rest and in each key, as split they would each be
        // pushed out their own way and the outline would open along the rim.
        private static void LinedHull(Ears e, List<int> twins, int n)
        {
            List<Vector3[]> hulls = new List<Vector3[]> { e.baseShape.hull };
            hulls.AddRange(e.keyHull);
            foreach (Vector3[] h in hulls)
            {
                for (int i = 0; i < twins.Count; i++)
                {
                    int a = twins[i], b = n + i;
                    Vector3 both = (h[a] + h[b]) * 0.5f;
                    h[a] = both;
                    h[b] = both;
                }
            }
        }

        // The places on the lining texture: along each ear from its root (v 0) to its tip (v 1), across it from edge to edge
        // by its widest way off its chain, the outside in the texture's left half and the inside in its right, the two ears
        // mirrored so their outer edges match.
        private void LiningUvs(Ears e)
        {
            Piece piece = e.piece;
            int n = piece.rest.Length;
            Vector2[] uvs = new Vector2[n];
            for (int side = 0; side < 2; side++)
            {
                Vector3[] joints = e.joints[side];
                float length = 0f;
                for (int j = 0; j + 1 < joints.Length; j++)
                {
                    length += (joints[j + 1] - joints[j]).magnitude;
                }
                List<int> mine = new List<int>();
                for (int v = 0; v < n; v++)
                {
                    if ((e.weightBone[e.firstWeight[v]] < EarMostBones ? 0 : 1) == side)
                    {
                        mine.Add(v);
                    }
                }
                float[] along = new float[mine.Count];
                Vector3[] offs = new Vector3[mine.Count];
                int[] segs = new int[mine.Count];
                for (int k = 0; k < mine.Count; k++)
                {
                    OnChain(piece.rest[mine[k]], joints, out along[k], out offs[k], out segs[k]);
                }
                Vector3 width = WidestWay(offs);
                if (width.x * (side == 0 ? -1f : 1f) < 0f)
                {
                    width = -width;   // + toward the figure's own outside on both ears (its left is -x), so they mirror
                }
                float[] across = new float[mine.Count];
                float widest = 1e-5f;
                for (int k = 0; k < mine.Count; k++)
                {
                    Vector3 d = (joints[segs[k] + 1] - joints[segs[k]]).normalized;
                    Vector3 w = width - Vector3.Dot(width, d) * d;
                    w = w.sqrMagnitude > 1e-8f ? w.normalized : width;
                    across[k] = Vector3.Dot(offs[k], w);
                    widest = Mathf.Max(widest, Mathf.Abs(across[k]));
                }
                // The inside across its own width where it is (each edge's outline, InsideEdge), so its texture's edge
                // is the rim all the way up, as the ear narrows; the outside across the ear's widest.
                List<Vector2>[] edges = { new List<Vector2>(), new List<Vector2>() };
                for (int k = 0; k < mine.Count; k++)
                {
                    if (e.inner[mine[k]])
                    {
                        edges[across[k] < 0f ? 0 : 1].Add(new Vector2(along[k] / Mathf.Max(length, 1e-5f), Mathf.Abs(across[k])));
                    }
                }
                Func<float, float>[] edgeAt = { InsideEdge(edges[0]), InsideEdge(edges[1]) };
                for (int k = 0; k < mine.Count; k++)
                {
                    int v = mine[k];
                    float t = along[k] / Mathf.Max(length, 1e-5f);
                    float u = e.inner[v]
                        ? 0.75f + Mathf.Clamp(across[k] / Mathf.Max(edgeAt[across[k] < 0f ? 0 : 1](t), 1e-5f), -1f, 1f) * LiningHalf
                        : 0.25f + across[k] / widest * LiningHalf;
                    uvs[v] = new Vector2(u, Mathf.Clamp(t, 0.01f, 0.99f));
                }
            }
            piece.uvs = uvs;
        }

        // One edge of an ear's inside, as how far it is from the ear's chain at each place along it (0 root .. 1 tip): the
        // upper hull of its vertices' (along, across), so the vertices on the rim are on it and those within it under it.
        private static Func<float, float> InsideEdge(List<Vector2> points)
        {
            if (points.Count == 0)
            {
                return t => 1f;
            }
            points.Sort((a, b) => a.x.CompareTo(b.x));
            List<Vector2> hull = new List<Vector2>();
            foreach (Vector2 q in points)
            {
                while (hull.Count >= 2)
                {
                    Vector2 a = hull[hull.Count - 2], b = hull[hull.Count - 1];
                    if ((b.x - a.x) * (q.y - a.y) - (b.y - a.y) * (q.x - a.x) < 0f)
                    {
                        break;   // b is above the line from a to q: kept
                    }
                    hull.RemoveAt(hull.Count - 1);
                }
                hull.Add(q);
            }
            Vector2[] h = hull.ToArray();
            return t =>
            {
                if (t <= h[0].x)
                {
                    return h[0].y;
                }
                for (int i = 0; i + 1 < h.Length; i++)
                {
                    if (t <= h[i + 1].x)
                    {
                        return Mathf.Lerp(h[i].y, h[i + 1].y, Mathf.InverseLerp(h[i].x, h[i + 1].x, t));
                    }
                }
                return h[h.Length - 1].y;
            };
        }
    }
}
