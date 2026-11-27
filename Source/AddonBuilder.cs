using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Race parts built from their sprites (AddonParts.cs gathers them): a Humanoid Alien Races body add-on (ears, a tail,
    // horns, wings) has a picture for each facing, placed against the race's own head or body as the game draws it. Here
    // each is made a solid in the chibi's model space by its visual hull: a point is in it where the front or back
    // picture shows it at the point's place across and up, and the east or west picture at its place front to back and
    // up. The pictures are placed by the race's head (body) in them: its opaque box in each facing is laid on the chibi's
    // head (body), so a part keeps its place against the head however the race's head is drawn. The hull is meshed by
    // surface nets, smoothed, and each triangle textured from the picture that faces it most (an atlas of the four, made
    // opaque, as the textured shader cuts out below alpha 0.5 and the hull's edge is where the pictures go clear). It
    // rides one bone whole, the head or, by its height, the hips or the chest, and is drawn as hair is. A hull can't know
    // how thin a part is, so an ear is as thick as its side picture is wide: a stand-in until a part has a model of its own.
    public sealed partial class SkinnedFigure
    {
        internal const int AddonTexSize = 128;      // each picture read at this size
        private const int AddonGrid = 44;           // voxels along a part's longest side
        private const int AddonGridMost = 72;       // and at most along any
        private const float AddonCellLeast = 0.007f;   // cells: a voxel no smaller, so a small tuft isn't thousands of triangles
        private const byte AddonSolid = 128;        // alpha counted as in the picture
        private const int AddonSmooth = 4;          // passes smoothing the surface
        internal const int AddonTriangles = 150;     // an ear, tuft or horn is cut down to about this many triangles, as low-poly as the garments
        internal const int AddonTailTriangles = 250; // and a tail, longer, to this many
        private const int AddonBleed = 6;           // pixels each picture's colour is grown past its edge in the atlas
        private const float AddonInkReach = 3f;     // pixels (of AddonTexSize) in from a picture's edge its outline ink may be
        private const float AddonInkDark = 0.5f;    // and as dark as this share of the picture's median lightness or darker
        private const float AddonIslandShare = 0.05f;   // a loose piece with less of the surface's area is dropped
        private const float AddonChestFrom = 0.55f;
        private const float AddonThin = 0.12f;      // an ear's or wing's greatest thickness, of its smaller side in the picture it is shaped from
        private const float AddonSideShare = 0.6f;  // a part is shaped from a side picture only if the front or back shows less than this of it // a body part whose middle is above this share of the body's height rides the chest, else the hips

        // One add-on as AddonParts.cs reads it from the game, per facing (0 north, 1 east, 2 south, 3 west, as Rot4): its
        // pixels and where they are against the race's head or body, null where it isn't drawn.
        internal sealed class AddonSource
        {
            public string key;          // the race, the add-on's path and its pictures: one built piece per key
            public string name;         // for messages
            public bool onHead;
            public bool thin;           // an ear or wing: kept flat (AddonThin)
            public bool tail;           // a tail: shaped from a side picture if it has one, as the front and back draw it end on or tucked behind
            public bool ear;            // an ear: moved on the gene ears' springs (RaceTails.cs)
            public Material glow;       // drawn with a glow shader (a halo): not built, but drawn as a glow card of this material (AddonGlowOf)
            public Color32[][] px = new Color32[4][];      // AddonTexSize square, rows bottom up
            public float[][] toUv = new float[4][];        // the node frame (X right, Z up the picture, cells) to the picture's uv: u = a0 + a1 X + a2 Z, v = a3 + a4 X + a5 Z
            public Vector4[] reference = new Vector4[4];   // the race's head (body) opaque box in that frame: x0, x1, z0, z1; x1 <= x0 where unknown
        }

        private readonly Dictionary<string, Piece> addonPieces = new Dictionary<string, Piece>();
        private Vector3 addonHeadLow, addonHeadHigh, addonBodyLow, addonBodyHigh;
        private bool addonBoxes;
        private int windingSign;   // the body's triangles' winding against their outward way: +1 or -1 (0: not yet looked)

        // The piece built for an add-on, made the first time it is asked for; null if it makes nothing.
        internal Piece AddonPiece(AddonSource src)
        {
            Piece p;
            if (addonPieces.TryGetValue(src.key, out p))
            {
                return p;
            }
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                p = BuildAddon(src);
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "couldn't build the race part " + src.name + " from its sprites, it is left off: " + e.Message);
                p = null;
            }
            addonPieces[src.key] = p;
            if (p != null)
            {
                Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                foreach (Vector3 v in p.rest)
                {
                    lo = Vector3.Min(lo, v);
                    hi = Vector3.Max(hi, v);
                }
                Log.Message(Tag + "built the race part " + src.name + " from its sprites in " + watch.ElapsedMilliseconds + " ms: " + p.tris.Length / 3 + " triangles, on "
                    + boneNames[p.weightBone[0]] + ", from " + lo.ToString("F2") + " to " + hi.ToString("F2") + " (the " + (src.onHead ? "head " + addonHeadLow.ToString("F2") + " to " + addonHeadHigh.ToString("F2")
                    : "body " + addonBodyLow.ToString("F2") + " to " + addonBodyHigh.ToString("F2")) + ").");
            }
            return p;
        }

        // The chibi's head and body boxes at rest in the base shape: the head without its own ears (No_Human_Ears), as a
        // race's ears go where the chibi's would be; the body all that isn't head.
        private void AddonBoxes()
        {
            if (addonBoxes)
            {
                return;
            }
            addonBoxes = true;
            bool[] head = OnHead();
            Piece body = pieces[0];
            Vector3[] noEars;
            body.keys.TryGetValue("No_Human_Ears", out noEars);   // (WalkFigures.NoHumanEarsKey)
            Vector3 hl = Vector3.positiveInfinity, hh = Vector3.negativeInfinity, bl = Vector3.positiveInfinity, bh = Vector3.negativeInfinity;
            for (int i = 0; i < body.rest.Length; i++)
            {
                if (head[i])
                {
                    Vector3 at = noEars != null ? body.rest[i] + noEars[i] : body.rest[i];
                    hl = Vector3.Min(hl, at);
                    hh = Vector3.Max(hh, at);
                }
                else
                {
                    bl = Vector3.Min(bl, body.rest[i]);
                    bh = Vector3.Max(bh, body.rest[i]);
                }
            }
            addonHeadLow = hl;
            addonHeadHigh = hh;
            addonBodyLow = bl;
            addonBodyHigh = bh;
            // The body's winding: most of its triangles' normals (by the order of their corners) point away from its middle.
            Vector3 mid = (bl + bh) * 0.5f;
            double sum = 0;
            int[] t = body.tris;
            for (int k = 0; k < t.Length; k += 3)
            {
                Vector3 a = body.rest[t[k]], b = body.rest[t[k + 1]], c = body.rest[t[k + 2]];
                sum += Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - mid);
            }
            windingSign = sum >= 0 ? 1 : -1;
        }

        // Where a model point is in facing f's node frame (X across the picture, Z up it), by the race's box in that
        // facing laid on the chibi's: false if the facing has no box.
        private bool AddonPicture(AddonSource src, int f, Vector3 p, Vector3 low, Vector3 high, out float X, out float Z)
        {
            Vector4 box = src.reference[f];
            X = Z = 0f;
            if (box.y <= box.x || box.w <= box.z)
            {
                return false;
            }
            float across;
            switch (f)
            {
                case 2: across = (high.x - p.x) / (high.x - low.x); break;   // south: the picture's right is the figure's left
                case 0: across = (p.x - low.x) / (high.x - low.x); break;    // north: from behind
                case 1: across = (p.z - low.z) / (high.z - low.z); break;    // east: the picture's right is the way it faces
                default: across = (high.z - p.z) / (high.z - low.z); break;  // west
            }
            X = box.x + across * (box.y - box.x);
            Z = box.z + (p.y - low.y) / (high.y - low.y) * (box.w - box.z);
            return true;
        }

        private static float AlphaAt(AddonSource src, int f, float X, float Z, out Vector2 uv)
        {
            float[] a = src.toUv[f];
            uv = new Vector2(a[0] + a[1] * X + a[2] * Z, a[3] + a[4] * X + a[5] * Z);
            if (uv.x < 0f || uv.y < 0f || uv.x >= 1f || uv.y >= 1f)
            {
                return 0f;
            }
            int c = (int)(uv.x * AddonTexSize), r = (int)(uv.y * AddonTexSize);
            return src.px[f][r * AddonTexSize + c].a;
        }

        private bool Seen(AddonSource src, int f, Vector3 p, Vector3 low, Vector3 high)
        {
            float X, Z;
            Vector2 uv;
            return src.px[f] != null && AddonPicture(src, f, p, low, high, out X, out Z) && AlphaAt(src, f, X, Z, out uv) >= AddonSolid;
        }

        // The part's solid: its outline in the picture that shows the most of it, inflated (a sketch blown up into a
        // solid): as thick at each point as it is far in from its outline there, so a stroke becomes a round tube and a
        // blob a rounded lump, thinnest at its edge. Ears and wings (thin) are kept no thicker than AddonThin of their
        // size, flat cards with round edges, as a hull of two pictures each drawing the ear face on came out a drum. Its
        // depth is where the other pictures have it, row by row (the middle of the head or body where they have nothing).
        private Piece BuildAddon(AddonSource src)
        {
            AddonBoxes();
            Vector3 low = src.onHead ? addonHeadLow : addonBodyLow, high = src.onHead ? addonHeadHigh : addonBodyHigh;
            int dom = -1;
            float most = 0f;
            for (int f = 0; f < 4; f++)
            {
                float area = src.px[f] != null ? ModelArea(src, f, low, high) : 0f;
                if (area > most * 1.05f)   // the front first, on near ties (in Rot4's order the north comes first, so the south must beat it)
                {
                    most = area;
                    dom = f;
                }
            }
            if (dom == 1 || dom == 3)
            {
                // A side picture only if it shows clearly more than the front or back.
                float front = Mathf.Max(src.px[2] != null ? ModelArea(src, 2, low, high) : 0f, src.px[0] != null ? ModelArea(src, 0, low, high) : 0f);
                if (front >= most * AddonSideShare)
                {
                    dom = src.px[2] != null && ModelArea(src, 2, low, high) >= front * 0.999f ? 2 : 0;
                }
            }
            else if (dom == 0 && src.px[2] != null && ModelArea(src, 2, low, high) >= most * 0.9f)
            {
                dom = 2;   // the front (an ear's inner side) over the back where they are near
            }
            if (src.tail)
            {
                float east = src.px[1] != null ? ModelArea(src, 1, low, high) : 0f, west = src.px[3] != null ? ModelArea(src, 3, low, high) : 0f;
                if (east > 0f || west > 0f)
                {
                    dom = east >= west ? 1 : 3;
                }
            }
            if (dom < 0)
            {
                return null;
            }
            bool frontDom = dom == 0 || dom == 2;
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            if (!Extent(src, dom, low, high, ref lo, ref hi))
            {
                return null;
            }
            float[] dist = Distances(src.px[dom]);
            float a0, y0, a1, y1, a2, y2;
            ToModel(src, dom, 64f, 64f, low, high, out a0, out y0);
            ToModel(src, dom, 65f, 64f, low, high, out a1, out y1);
            ToModel(src, dom, 64f, 65f, low, high, out a2, out y2);
            float perPixel = (Mathf.Abs(a1 - a0) + Mathf.Abs(y2 - y0)) * 0.5f;
            float across = frontDom ? hi.x - lo.x : hi.z - lo.z, tall = hi.y - lo.y;
            float cap = src.thin ? AddonThin * Mathf.Min(across, tall) : float.MaxValue;
            float deepest = 0f;
            for (int i = 0; i < dist.Length; i++)
            {
                deepest = Mathf.Max(deepest, dist[i]);
            }
            float hMost = Mathf.Min(deepest * perPixel, cap);
            float cell = Mathf.Max(Mathf.Max(Mathf.Max(across, tall), 2f * hMost) / AddonGrid, AddonCellLeast);
            // The depth of its middle at each height, from the other pictures.
            int ny = Mathf.Min(Mathf.CeilToInt(tall / cell) + 4, AddonGridMost + 8);
            float yFrom = lo.y - 2f * cell;
            float[] middle = Middles(src, frontDom, low, high, yFrom, cell, ny);
            float dLo = float.MaxValue, dHi = float.MinValue;
            for (int j = 0; j < ny; j++)
            {
                dLo = Mathf.Min(dLo, middle[j]);
                dHi = Mathf.Max(dHi, middle[j]);
            }
            dLo -= hMost + cell;
            dHi += hMost + cell;
            if (frontDom)
            {
                lo.z = dLo;
                hi.z = dHi;
            }
            else
            {
                lo.x = dLo;
                hi.x = dHi;
            }
            Vector3 size = hi - lo;
            Vector3 origin = new Vector3(lo.x - 2f * cell, yFrom, lo.z - 2f * cell);
            int nx = Mathf.Min(Mathf.CeilToInt(size.x / cell) + 4, AddonGridMost + 8);
            int nz = Mathf.Min(Mathf.CeilToInt(size.z / cell) + 4, AddonGridMost + 8);
            bool[] occ = new bool[nx * ny * nz];
            int filled = 0;
            float least = 0.75f * cell;   // a stroke a pixel wide still has some body
            for (int j = 2; j < ny - 2; j++)
            {
                for (int k = 2; k < nz - 2; k++)
                {
                    for (int i = 2; i < nx - 2; i++)
                    {
                        Vector3 p = origin + new Vector3((i + 0.5f) * cell, (j + 0.5f) * cell, (k + 0.5f) * cell);
                        float X, Z;
                        Vector2 uv;
                        if (!AddonPicture(src, dom, p, low, high, out X, out Z) || AlphaAt(src, dom, X, Z, out uv) < AddonSolid)
                        {
                            continue;
                        }
                        int c = Mathf.Clamp((int)(uv.x * AddonTexSize), 0, AddonTexSize - 1), r = Mathf.Clamp((int)(uv.y * AddonTexSize), 0, AddonTexSize - 1);
                        float h = Mathf.Max(Mathf.Min(dist[r * AddonTexSize + c] * perPixel, cap), least);
                        float depth = frontDom ? p.z : p.x;
                        if (Mathf.Abs(depth - middle[j]) <= h)
                        {
                            occ[(k * ny + j) * nx + i] = true;
                            filled++;
                        }
                    }
                }
            }
            if (filled < 8)
            {
                return null;
            }
            List<Vector3> verts;
            List<int> tris;
            SurfaceNets(occ, nx, ny, nz, origin, cell, out verts, out tris);
            WithoutIslands(verts, tris);
            if (tris.Count < 12)
            {
                return null;
            }
            int target = src.tail ? AddonTailTriangles : AddonTriangles;
            if (tris.Count / 3 > target)
            {
                // Cut down while it is still one welded surface, before it is split where its pictures meet: cut after, the
                // copies either side of a seam moved apart and the outline, pushed out from each, tore along them.
                int[] kept, cut;
                Decimate.Run(verts.ToArray(), tris.ToArray(), null, target, out kept, out cut);
                if (cut.Length >= 12)
                {
                    List<Vector3> left = new List<Vector3>(kept.Length);
                    foreach (int k in kept)
                    {
                        left.Add(verts[k]);
                    }
                    verts = left;
                    tris = new List<int>(cut);
                }
            }
            Piece piece = TexturedAddon(src, verts, tris, low, high);
            ChainAddon(src, piece, verts, tris);   // a tail or ear gets joints to move on (RaceTails.cs)
            return piece;
        }

        // A picture pixel's place (its column and row, at its middle with + 0.5) back on the model: across (x for the front
        // and back, z for the sides) and up.
        private static void ToModel(AddonSource src, int f, float c, float r, Vector3 low, Vector3 high, out float acrossAt, out float y)
        {
            Vector4 box = src.reference[f];
            float[] a = src.toUv[f];
            float det = a[1] * a[5] - a[2] * a[4];
            float u = c / AddonTexSize - a[0], v = r / AddonTexSize - a[3];
            float X = (a[5] * u - a[2] * v) / det, Z = (-a[4] * u + a[1] * v) / det;
            float across = (X - box.x) / (box.y - box.x), up = (Z - box.z) / (box.w - box.z);
            y = low.y + up * (high.y - low.y);
            switch (f)
            {
                case 2: acrossAt = high.x - across * (high.x - low.x); break;
                case 0: acrossAt = low.x + across * (high.x - low.x); break;
                case 1: acrossAt = low.z + across * (high.z - low.z); break;
                default: acrossAt = high.z - across * (high.z - low.z); break;
            }
        }

        // How much of the model a picture's opaque pixels clear of the head (body) cover (square cells), 0 if it has no box.
        private float ModelArea(AddonSource src, int f, Vector3 low, Vector3 high)
        {
            Vector4 box = src.reference[f];
            if (box.y <= box.x || box.w <= box.z || Mathf.Abs(src.toUv[f][1] * src.toUv[f][5] - src.toUv[f][2] * src.toUv[f][4]) < 1e-9f)
            {
                return 0f;
            }
            // Only what stands clear of the race's head (body) box: a part drawn behind the body in a picture (a tail from the
            // front) shows little there, and is shaped from a picture that shows it.
            int n = 0;
            float[] t = src.toUv[f];
            float det = t[1] * t[5] - t[2] * t[4];
            for (int r = 0; r < AddonTexSize; r++)
            {
                for (int c = 0; c < AddonTexSize; c++)
                {
                    if (src.px[f][r * AddonTexSize + c].a < AddonSolid)
                    {
                        continue;
                    }
                    float u = (c + 0.5f) / AddonTexSize - t[0], v = (r + 0.5f) / AddonTexSize - t[3];
                    float X = (t[5] * u - t[2] * v) / det, Z = (-t[4] * u + t[1] * v) / det;
                    if (X < box.x || X > box.y || Z < box.z || Z > box.w)
                    {
                        n++;
                    }
                }
            }
            float a0, y0, a1, y1, a2, y2;
            ToModel(src, f, 64f, 64f, low, high, out a0, out y0);
            ToModel(src, f, 65f, 64f, low, high, out a1, out y1);
            ToModel(src, f, 64f, 65f, low, high, out a2, out y2);
            return n * Mathf.Abs(a1 - a0) * Mathf.Abs(y2 - y0);
        }

        // Each opaque pixel's distance in pixels to the nearest clear one (or the picture's edge): two chamfer passes.
        private static float[] Distances(Color32[] px, int s = AddonTexSize)
        {
            float[] d = new float[s * s];
            const float Far = 1e6f, Diag = 1.41421356f;
            for (int i = 0; i < d.Length; i++)
            {
                d[i] = px[i].a >= AddonSolid ? Far : 0f;
            }
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    int i = y * s + x;
                    if (d[i] == 0f)
                    {
                        continue;
                    }
                    float best = x == 0 || y == 0 || x == s - 1 || y == s - 1 ? 1f : d[i];
                    if (x > 0) best = Mathf.Min(best, d[i - 1] + 1f);
                    if (y > 0) best = Mathf.Min(best, d[i - s] + 1f);
                    if (x > 0 && y > 0) best = Mathf.Min(best, d[i - s - 1] + Diag);
                    if (x < s - 1 && y > 0) best = Mathf.Min(best, d[i - s + 1] + Diag);
                    d[i] = best;
                }
            }
            for (int y = s - 1; y >= 0; y--)
            {
                for (int x = s - 1; x >= 0; x--)
                {
                    int i = y * s + x;
                    if (d[i] == 0f)
                    {
                        continue;
                    }
                    float best = d[i];
                    if (x < s - 1) best = Mathf.Min(best, d[i + 1] + 1f);
                    if (y < s - 1) best = Mathf.Min(best, d[i + s] + 1f);
                    if (x < s - 1 && y < s - 1) best = Mathf.Min(best, d[i + s + 1] + Diag);
                    if (x > 0 && y < s - 1) best = Mathf.Min(best, d[i + s - 1] + Diag);
                    d[i] = best;
                }
            }
            for (int i = 0; i < d.Length; i++)
            {
                d[i] = d[i] > 0f ? d[i] - 0.5f : 0f;   // from the pixel's middle to the edge
            }
            return d;
        }

        // The depth (z for a part shaped from the front, x from the side) of its middle at each grid row from yFrom, as the
        // other pictures place it: the mean of their opaque pixels at that height, rows they miss taking the nearest row's,
        // and the head's (body's) middle where they have none at all.
        private static float[] Middles(AddonSource src, bool frontDom, Vector3 low, Vector3 high, float yFrom, float cell, int ny)
        {
            float[] sum = new float[ny];
            int[] count = new int[ny];
            int[] others = frontDom ? new[] { 1, 3 } : new[] { 0, 2 };
            foreach (int f in others)
            {
                Vector4 box = src.reference[f];
                if (src.px[f] == null || box.y <= box.x || box.w <= box.z || Mathf.Abs(src.toUv[f][1] * src.toUv[f][5] - src.toUv[f][2] * src.toUv[f][4]) < 1e-9f)
                {
                    continue;
                }
                for (int r = 0; r < AddonTexSize; r++)
                {
                    for (int c = 0; c < AddonTexSize; c++)
                    {
                        if (src.px[f][r * AddonTexSize + c].a < AddonSolid)
                        {
                            continue;
                        }
                        float at, y;
                        ToModel(src, f, c + 0.5f, r + 0.5f, low, high, out at, out y);
                        int j = Mathf.FloorToInt((y - yFrom) / cell);
                        if (j >= 0 && j < ny)
                        {
                            sum[j] += at;
                            count[j]++;
                        }
                    }
                }
            }
            float[] middle = new float[ny];
            float fallback = frontDom ? (low.z + high.z) * 0.5f : (low.x + high.x) * 0.5f;
            bool any = false;
            for (int j = 0; j < ny; j++)
            {
                any |= count[j] > 0;
            }
            for (int j = 0; j < ny; j++)
            {
                if (count[j] > 0)
                {
                    middle[j] = sum[j] / count[j];
                    continue;
                }
                if (!any)
                {
                    middle[j] = fallback;
                    continue;
                }
                int nearest = -1;
                for (int d = 1; d < ny && nearest < 0; d++)
                {
                    if (j - d >= 0 && count[j - d] > 0)
                    {
                        nearest = j - d;
                    }
                    else if (j + d < ny && count[j + d] > 0)
                    {
                        nearest = j + d;
                    }
                }
                middle[j] = sum[nearest] / count[nearest];
            }
            // Smoothed a little along its height, so the part doesn't kink where the other pictures' pixels thin out.
            float[] smooth = (float[])middle.Clone();
            for (int j = 1; j < ny - 1; j++)
            {
                smooth[j] = (middle[j - 1] + 2f * middle[j] + middle[j + 1]) * 0.25f;
            }
            return smooth;
        }

        // Grows lo/hi by facing f's opaque pixels taken back to the model.
        private bool Extent(AddonSource src, int f, Vector3 low, Vector3 high, ref Vector3 lo, ref Vector3 hi)
        {
            Vector4 box = src.reference[f];
            if (box.y <= box.x || box.w <= box.z)
            {
                return false;
            }
            float[] a = src.toUv[f];
            float det = a[1] * a[5] - a[2] * a[4];
            if (Mathf.Abs(det) < 1e-9f)
            {
                return false;
            }
            Color32[] px = src.px[f];
            bool any = false;
            for (int r = 0; r < AddonTexSize; r++)
            {
                for (int c = 0; c < AddonTexSize; c++)
                {
                    if (px[r * AddonTexSize + c].a < AddonSolid)
                    {
                        continue;
                    }
                    // The pixel's middle back to the node frame, then to the model.
                    float u = (c + 0.5f) / AddonTexSize - a[0], v = (r + 0.5f) / AddonTexSize - a[3];
                    float X = (a[5] * u - a[2] * v) / det, Z = (-a[4] * u + a[1] * v) / det;
                    float across = (X - box.x) / (box.y - box.x), up = (Z - box.z) / (box.w - box.z);
                    float y = low.y + up * (high.y - low.y);
                    lo.y = Mathf.Min(lo.y, y);
                    hi.y = Mathf.Max(hi.y, y);
                    float w;
                    switch (f)
                    {
                        case 2: w = high.x - across * (high.x - low.x); lo.x = Mathf.Min(lo.x, w); hi.x = Mathf.Max(hi.x, w); break;
                        case 0: w = low.x + across * (high.x - low.x); lo.x = Mathf.Min(lo.x, w); hi.x = Mathf.Max(hi.x, w); break;
                        case 1: w = low.z + across * (high.z - low.z); lo.z = Mathf.Min(lo.z, w); hi.z = Mathf.Max(hi.z, w); break;
                        default: w = high.z - across * (high.z - low.z); lo.z = Mathf.Min(lo.z, w); hi.z = Mathf.Max(hi.z, w); break;
                    }
                    any = true;
                }
            }
            return any;
        }

        // Surface nets over the occupancy: a vertex in each cell of eight voxels the surface passes through, at the mean of
        // the crossings on its edges, and a quad across each pair of neighbouring voxels one in and one out, wound as the
        // body is; then smoothed AddonSmooth times toward the mean of its neighbours.
        private void SurfaceNets(bool[] occ, int nx, int ny, int nz, Vector3 origin, float cell, out List<Vector3> verts, out List<int> tris)
        {
            verts = new List<Vector3>();
            tris = new List<int>();
            int cx = nx - 1, cy = ny - 1, cz = nz - 1;
            int[] at = new int[cx * cy * cz];
            for (int q = 0; q < at.Length; q++)
            {
                at[q] = -1;
            }
            Func<int, int, int, bool> In = (i, j, k) => occ[(k * ny + j) * nx + i];
            int[,] corner = { { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 } };
            int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
            for (int k = 0; k < cz; k++)
            {
                for (int j = 0; j < cy; j++)
                {
                    for (int i = 0; i < cx; i++)
                    {
                        int mask = 0;
                        for (int c = 0; c < 8; c++)
                        {
                            if (In(i + corner[c, 0], j + corner[c, 1], k + corner[c, 2]))
                            {
                                mask |= 1 << c;
                            }
                        }
                        if (mask == 0 || mask == 255)
                        {
                            continue;
                        }
                        Vector3 sum = Vector3.zero;
                        int n = 0;
                        for (int e = 0; e < 12; e++)
                        {
                            int c0 = edges[e, 0], c1 = edges[e, 1];
                            if (((mask >> c0) & 1) != ((mask >> c1) & 1))
                            {
                                sum += new Vector3(corner[c0, 0] + corner[c1, 0], corner[c0, 1] + corner[c1, 1], corner[c0, 2] + corner[c1, 2]) * 0.5f;
                                n++;
                            }
                        }
                        Vector3 local = sum / n + new Vector3(i + 0.5f, j + 0.5f, k + 0.5f);
                        at[(k * cy + j) * cx + i] = verts.Count;
                        verts.Add(origin + local * cell);
                    }
                }
            }
            List<int> quads = new List<int>();
            Func<int, int, int, int> V = (i, j, k) => at[(k * cy + j) * cx + i];
            for (int k = 1; k < nz - 1; k++)
            {
                for (int j = 1; j < ny - 1; j++)
                {
                    for (int i = 1; i < nx - 1; i++)
                    {
                        bool here = In(i, j, k);
                        if (i < nx - 1 && here != In(i + 1, j, k))
                        {
                            Quad(quads, verts, V(i, j - 1, k - 1), V(i, j, k - 1), V(i, j, k), V(i, j - 1, k), new Vector3(here ? 1 : -1, 0, 0));
                        }
                        if (j < ny - 1 && here != In(i, j + 1, k))
                        {
                            Quad(quads, verts, V(i - 1, j, k - 1), V(i, j, k - 1), V(i, j, k), V(i - 1, j, k), new Vector3(0, here ? 1 : -1, 0));
                        }
                        if (k < nz - 1 && here != In(i, j, k + 1))
                        {
                            Quad(quads, verts, V(i - 1, j - 1, k), V(i, j - 1, k), V(i, j, k), V(i - 1, j, k), new Vector3(0, 0, here ? 1 : -1));
                        }
                    }
                }
            }
            // Smoothing, by the quads' edges.
            List<int>[] near = new List<int>[verts.Count];
            for (int q = 0; q < quads.Count; q += 4)
            {
                for (int e = 0; e < 4; e++)
                {
                    int a = quads[q + e], b = quads[q + (e + 1) % 4];
                    (near[a] = near[a] ?? new List<int>()).Add(b);
                    (near[b] = near[b] ?? new List<int>()).Add(a);
                }
            }
            Vector3[] pos = verts.ToArray();
            for (int pass = 0; pass < AddonSmooth; pass++)
            {
                Vector3[] next = (Vector3[])pos.Clone();
                for (int v = 0; v < pos.Length; v++)
                {
                    if (near[v] == null)
                    {
                        continue;
                    }
                    Vector3 mean = Vector3.zero;
                    foreach (int w in near[v])
                    {
                        mean += pos[w];
                    }
                    next[v] = Vector3.Lerp(pos[v], mean / near[v].Count, 0.5f);
                }
                pos = next;
            }
            verts = new List<Vector3>(pos);
            for (int q = 0; q < quads.Count; q += 4)
            {
                int a = quads[q], b = quads[q + 1], c = quads[q + 2], d = quads[q + 3];
                tris.Add(a); tris.Add(b); tris.Add(c);
                tris.Add(a); tris.Add(c); tris.Add(d);
            }
        }

        // A quad a-b-c-d, its corners in order round it, wound so its normal (by the body's winding) points along out.
        private void Quad(List<int> quads, List<Vector3> verts, int a, int b, int c, int d, Vector3 outward)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0)
            {
                return;
            }
            Vector3 n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            bool flip = Vector3.Dot(n, outward) * windingSign < 0f;
            if (flip)
            {
                quads.Add(a); quads.Add(d); quads.Add(c); quads.Add(b);
            }
            else
            {
                quads.Add(a); quads.Add(b); quads.Add(c); quads.Add(d);
            }
        }

        private static readonly Vector3[] FacingWay = { new Vector3(0, 0, -1), new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(-1, 0, 0) };   // toward the viewer of each picture, model space

        // The mesh textured from its pictures, split where neighbouring triangles take different ones, riding its bone.
        private Piece TexturedAddon(AddonSource src, List<Vector3> verts, List<int> tris, Vector3 low, Vector3 high)
        {
            Vector3[] smooth = Normals(verts.ToArray(), tris.ToArray());
            List<Vector3> rest = new List<Vector3>();
            List<Vector3> normal = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> outTris = new List<int>();
            Dictionary<long, int> copies = new Dictionary<long, int>();
            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a).normalized * windingSign;
                Vector3 mid = (a + b + c) / 3f;
                int best = -1;
                float bestScore = float.MinValue;
                for (int f = 0; f < 4; f++)
                {
                    if (src.px[f] == null)
                    {
                        continue;
                    }
                    float score = Vector3.Dot(n, FacingWay[f]) + (Seen(src, f, mid, low, high) ? 1f : 0f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = f;
                    }
                }
                for (int e = 0; e < 3; e++)
                {
                    int v = tris[t + e];
                    long key = (long)v * 4 + best;
                    int copy;
                    if (!copies.TryGetValue(key, out copy))
                    {
                        copy = rest.Count;
                        copies[key] = copy;
                        rest.Add(verts[v]);
                        normal.Add(smooth[v]);
                        float X, Z;
                        Vector2 uv = Vector2.zero;
                        if (AddonPicture(src, best, verts[v], low, high, out X, out Z))
                        {
                            AlphaAt(src, best, X, Z, out uv);
                        }
                        uvs.Add(new Vector2((best + Mathf.Clamp(uv.x, 0.002f, 0.998f)) / 4f, Mathf.Clamp(uv.y, 0.002f, 0.998f)));
                    }
                    outTris.Add(copy);
                }
            }
            int bone = headBone;
            if (!src.onHead)
            {
                Vector3 mean = Vector3.zero;
                foreach (Vector3 v in rest)
                {
                    mean += v;
                }
                mean /= Mathf.Max(1, rest.Count);
                int hips, chest;
                bool chestFound = boneIndex.TryGetValue("spine.003", out chest);
                bool hipsFound = boneIndex.TryGetValue("spine", out hips);
                float share = (mean.y - low.y) / Mathf.Max(1e-4f, high.y - low.y);
                bone = share > AddonChestFrom && chestFound ? chest : hipsFound ? hips : headBone;
            }
            int count = rest.Count;
            Piece piece = new Piece
            {
                name = "addon:" + src.name,
                defs = new string[0],
                groups = new string[0],
                rest = rest.ToArray(),
                tris = outTris.ToArray(),
                uvs = uvs.ToArray(),
                normal = normal.ToArray(),
                firstWeight = new int[count + 1],
                weightBone = new int[count],
                weight = new float[count],
            };
            for (int i = 0; i < count; i++)
            {
                piece.firstWeight[i + 1] = i + 1;
                piece.weightBone[i] = bone;
                piece.weight[i] = 1f;
            }
            Color inside;
            piece.texture = AddonAtlas(src, out inside);
            piece.mean = inside;
            return piece;
        }

        // The four pictures side by side (north, east, south, west), each grown past its edge and made opaque.
        private static Texture2D AddonAtlas(AddonSource src, out Color mean)
        {
            int s = AddonTexSize;
            Color32[] atlas = new Color32[4 * s * s];
            Vector4 sum = Vector4.zero;
            for (int f = 0; f < 4; f++)
            {
                Color32[] px = src.px[f] != null ? Bled(src.px[f], s, ref sum) : null;
                for (int r = 0; r < s; r++)
                {
                    for (int c = 0; c < s; c++)
                    {
                        atlas[r * 4 * s + f * s + c] = px != null ? px[r * s + c] : new Color32(128, 128, 128, 255);
                    }
                }
            }
            Texture2D t = new Texture2D(4 * s, s, TextureFormat.RGBA32, true);
            t.SetPixels32(atlas);
            t.Apply(true);
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.name = "Marus_Addon_" + src.name;
            mean = sum.w > 0f ? new Color(sum.x / sum.w, sum.y / sum.w, sum.z / sum.w, 1f) : Color.white;
            return t;
        }

        // A picture with its colours grown AddonBleed pixels past its opaque ones and the rest its mean, all opaque. Its
        // outline ink is taken out first, as the hair builder takes it out (InkEdges, TopEdges): the dark pixels within
        // AddonInkReach of its edge are filled from the colour inside them, so a part's rim, textured from where the
        // pictures go clear, isn't striped black (the figure has an outline of its own). Ink with no colour inside it near
        // enough (a stroke that is all outline) is kept. sum gathers the colour inside (rgb times count, count), which the
        // part's outline is drawn in, as a garment's is its image's mean colour.
        private static Color32[] Bled(Color32[] px, int s, ref Vector4 sum)
        {
            Color32[] o = (Color32[])px.Clone();
            bool[] solid = new bool[o.Length];
            List<float> lights = new List<float>();
            for (int i = 0; i < o.Length; i++)
            {
                solid[i] = o[i].a >= AddonSolid;
                if (solid[i])
                {
                    lights.Add(Light(o[i]));
                }
            }
            bool[] ink = new bool[o.Length];
            if (lights.Count > 0)
            {
                lights.Sort();
                float dark = AddonInkDark * lights[lights.Count / 2];
                float[] edge = Distances(px);
                for (int i = 0; i < o.Length; i++)
                {
                    ink[i] = solid[i] && edge[i] < AddonInkReach && Light(o[i]) < dark;
                    solid[i] &= !ink[i];
                }
            }
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i < o.Length; i++)
            {
                if (solid[i])
                {
                    r += o[i].r;
                    g += o[i].g;
                    b += o[i].b;
                    n++;
                }
            }
            sum += new Vector4(r / 255f, g / 255f, b / 255f, n);
            for (int pass = 0; pass < AddonBleed + (int)AddonInkReach; pass++)
            {
                bool[] next = (bool[])solid.Clone();
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        int i = y * s + x;
                        if (solid[i])
                        {
                            continue;
                        }
                        int sr = 0, sg = 0, sb = 0, sn = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= s || yy >= s || !solid[yy * s + xx])
                                {
                                    continue;
                                }
                                Color32 c = o[yy * s + xx];
                                sr += c.r;
                                sg += c.g;
                                sb += c.b;
                                sn++;
                            }
                        }
                        if (sn > 0)
                        {
                            o[i] = new Color32((byte)(sr / sn), (byte)(sg / sn), (byte)(sb / sn), 255);
                            next[i] = true;
                        }
                    }
                }
                solid = next;
            }
            Color32 mean = n > 0 ? new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255) : new Color32(128, 128, 128, 255);
            for (int i = 0; i < o.Length; i++)
            {
                // Ink no colour reached keeps its own (left as it was by the passes); anything else unreached is the mean.
                o[i] = solid[i] ? new Color32(o[i].r, o[i].g, o[i].b, 255) : ink[i] ? new Color32(px[i].r, px[i].g, px[i].b, 255) : mean;
            }
            return o;
        }

        private static float Light(Color32 c)
        {
            return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        }

        // Drops the loose pieces of a surface, as the hair builder does (WithoutIslands): any joined to nothing else with
        // under AddonIslandShare of its area, flecks left where a picture has a stray pixel or two.
        private static void WithoutIslands(List<Vector3> verts, List<int> tris)
        {
            int[] parent = new int[verts.Count];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }
            Func<int, int> find = null;
            find = i =>
            {
                while (parent[i] != i)
                {
                    parent[i] = parent[parent[i]];
                    i = parent[i];
                }
                return i;
            };
            for (int t = 0; t < tris.Count; t += 3)
            {
                parent[find(tris[t + 1])] = find(tris[t]);
                parent[find(tris[t + 2])] = find(tris[t]);
            }
            Dictionary<int, float> area = new Dictionary<int, float>();
            float total = 0f;
            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 a = verts[tris[t]];
                float size = Vector3.Cross(verts[tris[t + 1]] - a, verts[tris[t + 2]] - a).magnitude * 0.5f;
                int root = find(tris[t]);
                float was;
                area.TryGetValue(root, out was);
                area[root] = was + size;
                total += size;
            }
            List<int> left = new List<int>(tris.Count);
            for (int t = 0; t < tris.Count; t += 3)
            {
                if (area[find(tris[t])] >= AddonIslandShare * total)
                {
                    left.Add(tris[t]);
                    left.Add(tris[t + 1]);
                    left.Add(tris[t + 2]);
                }
            }
            tris.Clear();
            tris.AddRange(left);
        }

        // The part in this shape: a head part keeps its place in the head's box as the head shrinks or grows (as horns do).
        private PieceShape AddonShape(Piece piece, bool onHead, Shape s)
        {
            PieceShape shape;
            if (s.hairs.TryGetValue(piece, out shape))
            {
                return shape;
            }
            PieceShape plain = ShapePiece(piece, new List<KeyValuePair<string, float>>());
            // Its outline pushed out along the smooth normals, which its copies split where its pictures meet share, as a
            // garment's is (SmoothHull): ShapePiece gives a piece with no apparel the hull of its own faces, worked out per
            // copy, so the outline came apart along every seam.
            plain.hull = SmoothHull(plain.normal);
            if (onHead)
            {
                Shape b = BaseShape();
                MeasureHead(b);
                MeasureHead(s);
                Vector3 bs = b.headHigh - b.headLow, ss = s.headHigh - s.headLow;
                Vector3 scale = new Vector3(ss.x / bs.x, ss.y / bs.y, ss.z / bs.z);
                Vector3 bAnchor = new Vector3((b.headLow.x + b.headHigh.x) * 0.5f, b.headLow.y, (b.headLow.z + b.headHigh.z) * 0.5f);
                Vector3 sAnchor = new Vector3((s.headLow.x + s.headHigh.x) * 0.5f, s.headLow.y, (s.headLow.z + s.headHigh.z) * 0.5f);
                Vector3[] rest = new Vector3[plain.rest.Length];
                for (int i = 0; i < rest.Length; i++)
                {
                    rest[i] = sAnchor + Vector3.Scale(plain.rest[i] - bAnchor, scale);
                }
                plain = new PieceShape { rest = rest, hull = plain.hull, normal = plain.normal };
            }
            s.hairs[piece] = plain;
            return plain;
        }

        // A glowing add-on (a halo drawn with the game's additive glow): a solid would be a dark lump, so it stays a picture,
        // drawn as a glow card facing the camera. Its middle and size are its front picture's, the whole quad's (the card
        // is drawn with the whole texture), its depth the side picture's; it rides the head bone (or the chest).
        internal sealed class AddonGlow
        {
            public int bone;
            public Vector3 middle;   // rest, cells
            public float size;       // cells across
        }

        private readonly Dictionary<string, AddonGlow> addonGlows = new Dictionary<string, AddonGlow>();

        internal AddonGlow AddonGlowOf(AddonSource src)
        {
            AddonGlow g;
            if (addonGlows.TryGetValue(src.key, out g))
            {
                return g;
            }
            AddonBoxes();
            Vector3 low = src.onHead ? addonHeadLow : addonBodyLow, high = src.onHead ? addonHeadHigh : addonBodyHigh;
            int front = src.px[2] != null && src.reference[2].y > src.reference[2].x ? 2 : src.px[0] != null && src.reference[0].y > src.reference[0].x ? 0 : -1;
            if (front >= 0)
            {
                float x, y, x1, y1;
                ToModel(src, front, 64f, 64f, low, high, out x, out y);
                ToModel(src, front, 128f, 64f, low, high, out x1, out y1);
                float z = (low.z + high.z) * 0.5f;
                int side = src.px[1] != null && src.reference[1].y > src.reference[1].x ? 1 : src.px[3] != null && src.reference[3].y > src.reference[3].x ? 3 : -1;
                if (side >= 0)
                {
                    // The side picture's opaque middle, front to back.
                    float sum = 0f;
                    int n = 0;
                    for (int r = 0; r < AddonTexSize; r++)
                    {
                        for (int c = 0; c < AddonTexSize; c++)
                        {
                            if (src.px[side][r * AddonTexSize + c].a >= AddonSolid)
                            {
                                float at, up;
                                ToModel(src, side, c + 0.5f, r + 0.5f, low, high, out at, out up);
                                sum += at;
                                n++;
                            }
                        }
                    }
                    z = n > 0 ? sum / n : z;
                }
                int bone = headBone, chest;
                if (!src.onHead)
                {
                    bone = boneIndex.TryGetValue("spine.003", out chest) ? chest : headBone;
                }
                g = new AddonGlow { bone = bone, middle = new Vector3(x, y, z), size = 2f * Mathf.Abs(x1 - x) };
            }
            addonGlows[src.key] = g;
            return g;
        }

        // Where a glowing part's middle is drawn (true space).
        internal Vector3 AddonGlowAt(Posed p, AddonGlow g, Vector3 position, float yaw, float size)
        {
            Vector3 posed = p.whole[g.bone].MultiplyPoint3x4(g.middle);
            return position + Quaternion.AngleAxis(yaw, Vector3.up) * (posed * size);
        }

        // Draws a built part on the pawn, in tint (its add-on's colour) over its pictures.
        internal void DrawAddon(Posed p, Piece piece, bool onHead, Vector3 position, float yaw, float size, Color tint)
        {
            Material solid = TexturedMaterial(piece);
            if (solid == null)
            {
                return;
            }
            PieceMeshes m = AddonMeshes(p, piece);
            Pose(p, piece, AddonShape(piece, onHead, p.shape), m);
            DrawMeshes(p, m, piece.rest.Length, solid, position, yaw, size, tint, lineTint: tint * piece.mean);
        }
    }
}
