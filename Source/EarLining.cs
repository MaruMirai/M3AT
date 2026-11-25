using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Ears with a paler, fluffy inside, from a texture rather than painted vertex groups: as an ear is read, its triangles
    // facing forward (LiningFacing, as Kiiro's lining is found) are its inside, or those with all three corners in the
    // file's inner group where it has one. The vertices the inside shares with the outside are split, so the two meet at
    // a crisp edge along the rim, and each side takes its own half of the ear's texture (Models/chibi/ear_*.png,
    // anim_scripts/make_ear_textures.py): the outside on the left half, fur streaks; the inside on the right, pale fluff.
    // The inside is still coloured as the lining (DrawEars' inner colour), the outside in the hair's; the texture
    // multiplies both. Its places go along the ear from root to tip (v) and across it from edge to edge (u), so a texture
    // painted once fits every ear shape. Split, each copy's outline is still pushed out the way both share (LinedHull), so
    // it doesn't tear along the rim.
    public sealed partial class SkinnedFigure
    {
        private const string FluffyEars = "ear_fluffy.png", MouseEars = "ear_mouse.png";
        private const float LiningHalf = 0.23f;   // of the texture's width, half an ear's width in its half (0.25 is the whole half)

        private static readonly Dictionary<string, Texture2D> earTextures = new Dictionary<string, Texture2D>();

        // The lining texture an ear file takes, null for none (the pig's are bare skin; the floppy ears keep the grain).
        private static string LiningTextureOf(string file, bool race)
        {
            if (race || file == CatEarFile || file == "race_wolfein_ears.txt")
            {
                return FluffyEars;   // the gene's cat ears, Kiiro's (made from them), the Wolfein's
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
            for (int t = 0; t < tris.Length; t += 3)
            {
                if (painted)
                {
                    insideTri[t / 3] = e.inner[tris[t]] && e.inner[tris[t + 1]] && e.inner[tris[t + 2]];
                    continue;
                }
                Vector3 nrm = faceNormal[t / 3] * (outward[sideOf[tris[t]]] < 0f ? -1f : 1f);
                insideTri[t / 3] = nrm.sqrMagnitude > 0f && nrm.normalized.z > LiningFacing;
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
                for (int k = 0; k < mine.Count; k++)
                {
                    int v = mine[k];
                    float u = (e.inner[v] ? 0.75f : 0.25f) + across[k] / widest * LiningHalf;
                    uvs[v] = new Vector2(u, Mathf.Clamp(along[k] / Mathf.Max(length, 1e-5f), 0.01f, 0.99f));
                }
            }
            piece.uvs = uvs;
        }

        // How much of the lining's colour the inside takes: LiningMost at its middle low down, none from LiningEdge across
        // to the rim and from LiningTip along it, eased between, so the pale patch is small and fades out rather than filling
        // every forward-facing triangle up to a hard line at the rim. In ears' texture places (LiningUvs).
        // 0: no pale patch at all, the inside in the hair's colour as the outside is (asked for, Nov 2026; it was 0.95, and
        // make_ear_textures.py's PALE_INSIDE paints the texture's fluff to go with it).
        private const float LiningMost = 0f;
        private const float LiningCore = 0.3f, LiningEdge = 0.8f;   // across, of the half width: whole inside the core, none past the edge
        private const float LiningLow = 0.4f, LiningTip = 0.85f;    // along, root to tip: whole below low, none past the tip

        private static void LiningShares(Ears e)
        {
            Vector2[] uvs = e.piece.uvs;
            float[] share = new float[uvs.Length];
            for (int v = 0; v < uvs.Length; v++)
            {
                if (!e.inner[v])
                {
                    continue;
                }
                float across = Mathf.Abs(uvs[v].x - 0.75f) / LiningHalf;
                float a = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(LiningCore, LiningEdge, across));
                float t = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(LiningLow, LiningTip, uvs[v].y));
                share[v] = LiningMost * a * t;
            }
            e.liningShare = share;
        }

        // The ears' vertex colours, each the hair's (tint) toward the lining's by its share; kept on the pose while the two
        // colours stay, as DrawMeshes remakes the mesh's colours whenever it is given another array.
        private static Color32[] LiningPaint(Posed p, Ears e, Color tint, Color inner)
        {
            Color32 t = Rounded(tint), i = Rounded(inner);
            if (p.earPaint != null && p.earPaint.Length == e.liningShare.Length && Same(t, p.earPaintTint) && Same(i, p.earPaintInner))
            {
                return p.earPaint;
            }
            Color32[] paint = new Color32[e.liningShare.Length];
            for (int v = 0; v < paint.Length; v++)
            {
                paint[v] = Color32.Lerp(t, i, e.liningShare[v]);
                paint[v].a = 255;
            }
            p.earPaint = paint;
            p.earPaintTint = t;
            p.earPaintInner = i;
            return paint;
        }
    }
}
