using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using RimWorld;
using Verse;

namespace M3AT
{
    // The Furskin gene's coat: the body drawn with a painted fur image (Models/chibi/fur.png, the pale front, stripes and
    // strands, with the hair's strand grain baked in by anim_scripts/make_fur_texture.py's game command) in place of the
    // plain skin. The body has no texture places of its own (skin.txt has none), so fur.txt gives each of its triangles'
    // corners one on the image, and here the body is copied with its vertices split where two corners of one vertex have
    // different places (the image's seams): the coat, a piece of its own after the garments (CoatPiece), with the body's
    // weights and keys, posed and shaped as a garment is but drawn in the body's place, skin and all (DrawCoat): its triangles
    // are the body's in the same order, so what the body leaves out under clothes, under the blanket or for bare feet it
    // leaves out too (CoatTris). The Furskin tufts take their places on the same image from the skin under each (CoatTufts),
    // so a tuft on a stripe is dark and one on the belly pale, and are shaded from root to tip (TuftRootShade, TuftTipShade).
    // Without fur.txt, or with one made for another body, a furred pawn has the plain body with the strand grain (GrainBody).
    public sealed partial class SkinnedFigure
    {
        private const string CoatFile = "fur.txt";
        private const string CoatDef = "Gene_Furskin_Coat";   // the coat's def name: never apparel, so never worn as a garment
        private const float TuftRootShade = 0.86f;   // a tuft's colour at its root, against the skin it grows from
        private const float TuftTipShade = 1.12f;    // and at its tip (over 1: lighter, as far as the colour goes)

        private int[] coatFrom;   // each of the coat's vertices' body vertex
        private readonly Dictionary<(GarmentMask, Piece, int, bool), int[]> coatTris = new Dictionary<(GarmentMask, Piece, int, bool), int[]>();
        private readonly Dictionary<Color32[], Color32[]> coatPaint = new Dictionary<Color32[], Color32[]>();
        private readonly Dictionary<(Piece, int), Color32[]> tuftPaint = new Dictionary<(Piece, int), Color32[]>();
        // Each body triangle's corners and their places on the coat's image (ReadCoat), and that image's file, for CoatFace.
        private Dictionary<(int, int, int), (int[] corners, Vector2[] places)> coatPlaces;
        private string coatImage;
        private Color32[] coatFace;
        private bool coatFaceTried;
        public const int CoatFaceSize = 512;   // Facial Animation's textures' size, which its masks are read at

        // The coat as it lies under the face patch, laid out as Facial Animation's face textures are (the patch's own
        // texture places, rows bottom first, CoatFaceSize square), or null without a coat. Facial Animation's lid cover is
        // skin colour across the whole eye band, so on a furred pawn it hid the coat there as a flat strip: FaceLayers
        // draws it with this in place of the flat colour (CoatedMask). Made once, from the image's file, as the coat's own
        // texture keeps no copy to read.
        internal Color32[] CoatFace()
        {
            if (coatFaceTried)
            {
                return coatFace;
            }
            coatFaceTried = true;
            if (coatPlaces == null || coatImage == null || faceTris == null || faceUvs == null || !File.Exists(coatImage))
            {
                return null;
            }
            Texture2D read = null;
            try
            {
                read = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                read.LoadImage(File.ReadAllBytes(coatImage));
                int w = read.width, h = read.height;
                Color32[] fur = read.GetPixels32();   // rows bottom first, as the places' v
                int n = CoatFaceSize;
                Color32[] face = new Color32[n * n];
                bool[] set = new bool[n * n];
                for (int k = 0; k + 2 < faceTris.Length; k += 3)
                {
                    int f0 = faceTris[k], f1 = faceTris[k + 1], f2 = faceTris[k + 2];
                    int b0 = faceFrom[f0], b1 = faceFrom[f1], b2 = faceFrom[f2];
                    (int[] corners, Vector2[] places) hit;
                    if (!coatPlaces.TryGetValue(Sorted(b0, b1, b2), out hit))
                    {
                        continue;
                    }
                    Vector2 c0 = hit.places[Array.IndexOf(hit.corners, b0)];
                    Vector2 c1 = hit.places[Array.IndexOf(hit.corners, b1)];
                    Vector2 c2 = hit.places[Array.IndexOf(hit.corners, b2)];
                    Vector2 p0 = faceUvs[f0] * n, p1 = faceUvs[f1] * n, p2 = faceUvs[f2] * n;
                    float area = (p1.x - p0.x) * (p2.y - p0.y) - (p2.x - p0.x) * (p1.y - p0.y);
                    if (Mathf.Abs(area) < 1e-6f)
                    {
                        continue;
                    }
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)))), x1 = Mathf.Min(n - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)))), y1 = Mathf.Min(n - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))));
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            Vector2 q = new Vector2(x + 0.5f, y + 0.5f);
                            float a = ((p1.x - q.x) * (p2.y - q.y) - (p2.x - q.x) * (p1.y - q.y)) / area;
                            float b = ((p2.x - q.x) * (p0.y - q.y) - (p0.x - q.x) * (p2.y - q.y)) / area;
                            float c = 1f - a - b;
                            if (a < -0.02f || b < -0.02f || c < -0.02f)
                            {
                                continue;
                            }
                            Vector2 uv = a * c0 + b * c1 + c * c2;
                            face[y * n + x] = Bilinear(fur, w, h, uv);
                            set[y * n + x] = true;
                        }
                    }
                }
                // Grown a few texels past the patch's edge, so the layer's filtering there reads coat, not black.
                for (int pass = 0; pass < 4; pass++)
                {
                    bool[] was = (bool[])set.Clone();
                    for (int y = 0; y < n; y++)
                    {
                        for (int x = 0; x < n; x++)
                        {
                            if (was[y * n + x])
                            {
                                continue;
                            }
                            int from = x > 0 && was[y * n + x - 1] ? y * n + x - 1 : x < n - 1 && was[y * n + x + 1] ? y * n + x + 1
                                : y > 0 && was[(y - 1) * n + x] ? (y - 1) * n + x : y < n - 1 && was[(y + 1) * n + x] ? (y + 1) * n + x : -1;
                            if (from >= 0)
                            {
                                face[y * n + x] = face[from];
                                set[y * n + x] = true;
                            }
                        }
                    }
                }
                coatFace = face;
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not lay the coat out on the face, so its lid cover stays the skin's flat colour: " + e.Message);
            }
            finally
            {
                if (read != null)
                {
                    UnityEngine.Object.Destroy(read);
                }
            }
            return coatFace;
        }

        private static Color32 Bilinear(Color32[] px, int w, int h, Vector2 uv)
        {
            float fx = Mathf.Clamp(uv.x * w - 0.5f, 0f, w - 1.001f), fy = Mathf.Clamp(uv.y * h - 0.5f, 0f, h - 1.001f);
            int x = (int)fx, y = (int)fy;
            float tx = fx - x, ty = fy - y;
            Color a = Color.Lerp(px[y * w + x], px[y * w + x + 1], tx);
            Color b = Color.Lerp(px[(y + 1) * w + x], px[(y + 1) * w + x + 1], tx);
            Color32 c = Color.Lerp(a, b, ty);
            c.a = 255;
            return c;
        }

        // The coat's piece, or -1 if the export has none.
        public int CoatPiece
        {
            get
            {
                int g;
                return garmentOf.TryGetValue(CoatDef, out g) ? g : -1;
            }
        }

        // Reads fur.txt and its image and adds the coat after the garments; nothing (and a message if it is there but doesn't
        // fit) otherwise. Call once the body and garments are read and turned round.
        private void ReadCoat()
        {
            string path = Path.Combine(folder, CoatFile);
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                Piece body = pieces[0];
                int count = body.tris.Length / 3;
                Dictionary<(int, int, int), (int[] corners, Vector2[] places)> given = new Dictionary<(int, int, int), (int[], Vector2[])>();
                string image = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 0 || p[0].StartsWith("#"))
                    {
                        continue;
                    }
                    if (p[0] == "image" && p.Length > 1)
                    {
                        image = p[1];
                    }
                    else if (p[0] == "t" && p.Length > 9)
                    {
                        int[] corners = { int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]) };
                        Vector2[] places = new Vector2[3];
                        for (int c = 0; c < 3; c++)
                        {
                            places[c] = new Vector2(Num(p[4 + 2 * c]), Num(p[5 + 2 * c]));
                        }
                        given[Sorted(corners[0], corners[1], corners[2])] = (corners, places);
                    }
                }
                if (image == null || given.Count != count)
                {
                    Log.Warning(Tag + CoatFile + " has " + given.Count + " triangles where the body has " + count
                        + ", so a furred body is drawn plain: run make_fur_texture.py's game command again after the export.");
                    return;
                }
                // Each corner's vertex, split where its places differ.
                Dictionary<(int, long), int> at = new Dictionary<(int, long), int>();
                List<int> from = new List<int>();
                List<Vector2> uvs = new List<Vector2>();
                int[] tris = new int[body.tris.Length];
                for (int k = 0; k < count; k++)
                {
                    int a = body.tris[3 * k], b = body.tris[3 * k + 1], c = body.tris[3 * k + 2];
                    (int[] corners, Vector2[] places) hit;
                    if (!given.TryGetValue(Sorted(a, b, c), out hit))
                    {
                        Log.Warning(Tag + CoatFile + " has no triangle " + a + " " + b + " " + c
                            + " of the body, so a furred body is drawn plain: run make_fur_texture.py's game command again after the export.");
                        return;
                    }
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int i = body.tris[3 * k + corner];
                        Vector2 uv = hit.places[Array.IndexOf(hit.corners, i)];
                        long key = ((long)Mathf.RoundToInt(uv.x * 65536f) << 32) ^ (uint)Mathf.RoundToInt(uv.y * 65536f);
                        int j;
                        if (!at.TryGetValue((i, key), out j))
                        {
                            j = from.Count;
                            at[(i, key)] = j;
                            from.Add(i);
                            uvs.Add(uv);
                        }
                        tris[3 * k + corner] = j;
                    }
                }
                int n = from.Count;
                Piece coat = new Piece
                {
                    name = "FurCoat", defs = new[] { CoatDef }, layer = "", groups = new string[0], hides = new int[0],
                    hideHair = 0, hideFace = 0, tris = tris, uvs = uvs.ToArray(), rest = new Vector3[n], firstWeight = new int[n + 1]
                };
                List<int> bone = new List<int>();
                List<float> weight = new List<float>();
                for (int j = 0; j < n; j++)
                {
                    int i = from[j];
                    coat.rest[j] = body.rest[i];
                    coat.firstWeight[j] = bone.Count;
                    for (int w = body.firstWeight[i]; w < body.firstWeight[i + 1]; w++)
                    {
                        bone.Add(body.weightBone[w]);
                        weight.Add(body.weight[w]);
                    }
                }
                coat.firstWeight[n] = bone.Count;
                coat.weightBone = bone.ToArray();
                coat.weight = weight.ToArray();
                foreach (KeyValuePair<string, Vector3[]> key in body.keys)
                {
                    Vector3[] moves = new Vector3[n];
                    for (int j = 0; j < n; j++)
                    {
                        moves[j] = key.Value[from[j]];
                    }
                    coat.keys[key.Key] = moves;
                }
                ReadTexture(coat, Path.Combine(folder, image));
                coatPlaces = given;
                coatImage = Path.Combine(folder, image);
                if (coat.texture == null)
                {
                    return;
                }
                coatFrom = from.ToArray();
                garmentOf[CoatDef] = pieces.Length;
                Piece[] all = new Piece[pieces.Length + 1];
                Array.Copy(pieces, all, pieces.Length);
                all[pieces.Length] = coat;
                pieces = all;
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not read " + CoatFile + ", so a furred body is drawn plain: " + e.Message);
            }
        }

        private static (int, int, int) Sorted(int a, int b, int c)
        {
            if (a > b) { int t = a; a = b; b = t; }
            if (b > c) { int t = b; b = c; c = t; }
            if (a > b) { int t = a; a = b; b = t; }
            return (a, b, c);
        }

        // Draws the coat in the body's place (DrawBody), its triangles left out as the body's are (HideUnder: call that
        // first, it poses the body, which what reads the posed body still goes by), its prosthetics painted as the body's.
        public void DrawCoat(Posed p, int coat, GarmentMask mask, HairDef hairStyle, Vector3 position, float yaw, float size, Color tint)
        {
            if (p.face != null && p.faceScalp != null)
            {
                // No scalp colour on the coat, so the face patch keeps its scalp too (DrawBody).
                p.face.face.SetTriangles(faceTris, 0, false);
                p.face.mouth.SetTriangles(faceTris, 0, false);
                p.face.mouthLow.SetTriangles(faceTris, 0, false);
                p.faceScalp = null;
            }
            Piece g = pieces[coat];
            PieceMeshes m = Pose(p, coat);
            m.isSkin = true;
            Piece hair = WornHair(Hair(hairStyle), mask, p);
            if (hair != null && hair.shell == null)
            {
                hair = null;
            }
            if (m.hidden != mask || m.hiddenHair != hair || m.tucked != p.tucked || m.bare != p.bare)
            {
                m.solid.SetTriangles(Tucked(p, CoatTris(mask, null, p.bare, false), g), 0, false);
                m.outline.SetTriangles(Tucked(p, CoatTris(mask, hair, p.bare, true), g), 0, false);
                m.hidden = mask;
                m.hiddenHair = hair;
                m.tucked = p.tucked;
                m.bare = p.bare;
            }
            Material textured = TexturedMaterial(g);
            if (textured == null)
            {
                DrawMeshes(p, m, g.rest.Length, SolidMaterial(), position, yaw, size, tint * g.mean, null, default(Color), null, default(Color), null, CoatPaintOf(PaintOf(p.limbs)));
                return;
            }
            if (!m.textureMarked && g.mark != null)
            {
                m.parts.Add(new MeshPart { mesh = m.solid, material = g.mark, outline = false });
                m.textureMarked = true;
            }
            DrawMeshes(p, m, g.rest.Length, textured, position, yaw, size, tint, null, default(Color), null, default(Color), tint * g.mean,
                CoatPaintOf(PaintOf(p.limbs)));
        }

        // The coat's triangles less those the body leaves out under the garments in mask (and, for the outline, inside the
        // hair's shell), by the body's triangle numbers.
        private int[] CoatTris(GarmentMask mask, Piece hair, int bare, bool outline)
        {
            int[] all = pieces[CoatPiece].tris;
            if (mask == 0 && (hair == null || hair.shell.hides.Length == 0))
            {
                return all;
            }
            (GarmentMask, Piece, int, bool) key = (mask, hair, bare, outline);
            int[] tris;
            if (!coatTris.TryGetValue(key, out tris))
            {
                bool[] hide = HiddenBy(mask, bare);
                if (hair != null)
                {
                    foreach (int k in hair.shell.hides)
                    {
                        hide[k] = true;
                    }
                }
                tris = Kept(all, hide);
                coatTris[key] = tris;
            }
            return tris;
        }

        // The body's prosthetic colours (PaintOf) for the coat's vertices.
        private Color32[] CoatPaintOf(Color32[] paint)
        {
            if (paint == null)
            {
                return null;
            }
            Color32[] mine;
            if (!coatPaint.TryGetValue(paint, out mine))
            {
                mine = new Color32[coatFrom.Length];
                for (int j = 0; j < mine.Length; j++)
                {
                    mine[j] = coatFrom[j] < paint.Length ? paint[coatFrom[j]] : default(Color32);
                }
                coatPaint[paint] = mine;
            }
            return mine;
        }

        // The Furskin tufts on the coat's image: each tuft whole on the body triangle nearest the middle of its root (its
        // corners within half its height of the skin), each corner at the place on that triangle nearest it, so it takes the
        // colour of the skin it grows from; and its shade from root to tip, by how far each corner stands off the skin.
        private void CoatTufts(Piece fur)
        {
            Piece coat = pieces[CoatPiece];
            Vector3[] rest = fur.rest;
            float[] off = new float[rest.Length];
            for (int i = 0; i < rest.Length; i++)
            {
                off[i] = Mathf.Sqrt(NearestTriangle(coat, rest[i], out int _, out Vector3 _));
            }
            Vector2[] uvs = new Vector2[rest.Length];
            float[] shade = new float[rest.Length];
            foreach (List<int> g in Tufts(rest, fur.tris).Values)
            {
                float most = 0f;
                foreach (int i in g)
                {
                    most = Mathf.Max(most, off[i]);
                }
                Vector3 root = Vector3.zero;
                int roots = 0;
                foreach (int i in g)
                {
                    if (off[i] <= most * 0.5f)
                    {
                        root += rest[i];
                        roots++;
                    }
                }
                root /= Mathf.Max(roots, 1);
                int k;
                NearestTriangle(coat, root, out k, out Vector3 _);
                Vector3 a = coat.rest[coat.tris[3 * k]], b = coat.rest[coat.tris[3 * k + 1]], c = coat.rest[coat.tris[3 * k + 2]];
                Vector2 ua = coat.uvs[coat.tris[3 * k]], ub = coat.uvs[coat.tris[3 * k + 1]], uc = coat.uvs[coat.tris[3 * k + 2]];
                foreach (int i in g)
                {
                    Vector3 w = Barycentric(rest[i], a, b, c);
                    uvs[i] = ua * w.x + ub * w.y + uc * w.z;
                    shade[i] = Mathf.Lerp(TuftRootShade, TuftTipShade, most > 1e-6f ? off[i] / most : 0f);
                }
            }
            fur.uvs = uvs;
            fur.shade = shade;
            fur.texture = coat.texture;
            fur.mean = coat.mean;
        }

        // The nearest of the piece's triangles to q, its number, and the nearest point on it; returns the squared distance.
        private static float NearestTriangle(Piece piece, Vector3 q, out int nearest, out Vector3 point)
        {
            float best = float.MaxValue;
            nearest = 0;
            point = q;
            for (int k = 0; k + 2 < piece.tris.Length; k += 3)
            {
                Vector3 on = ClosestOnTriangle(q, piece.rest[piece.tris[k]], piece.rest[piece.tris[k + 1]], piece.rest[piece.tris[k + 2]]);
                float d = (on - q).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    nearest = k / 3;
                    point = on;
                }
            }
            return best;
        }

        // The point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5).
        private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        // The shares of a, b and c in the point of the triangle nearest p.
        private static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 q = ClosestOnTriangle(p, a, b, c);
            Vector3 v0 = b - a, v1 = c - a, v2 = q - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-12f)
            {
                return new Vector3(1f / 3f, 1f / 3f, 1f / 3f);
            }
            float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
            return new Vector3(1f - v - w, v, w);
        }

        // A shaded piece's vertex colours for this tint (its shade times the tint, per vertex), made once per tint.
        private Color32[] ShadedPaint(Piece g, Color tint)
        {
            Color32 t = Rounded(tint);
            int key = t.r | (t.g << 8) | (t.b << 16);
            Color32[] paint;
            if (!tuftPaint.TryGetValue((g, key), out paint))
            {
                paint = new Color32[g.shade.Length];
                for (int i = 0; i < paint.Length; i++)
                {
                    Color c = (Color)t * g.shade[i];
                    c.a = 1f;
                    paint[i] = c;
                }
                tuftPaint[(g, key)] = paint;
            }
            return paint;
        }
    }
}
