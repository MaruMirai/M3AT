using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // IsoRim's weapon models laid into the sprite's slab (WalkFigures.DrawModel), so that the holds made for the sprites
    // (walk.WEAPONS) put the model in the hands. This is IsoRim's own fit (WeaponModels.Fit: of the 24 square turns of the
    // model, its longest side along the sprite's long axis and as long as the sprite there, the one whose outline covers the
    // sprite's best), with two changes for a weapon that is aimed along the art's front:
    //   - IsoRim takes the sprite's narrowest width for its long axis, which for a pistol or revolver runs from the foot of
    //     the grip up to the muzzle, so the model came out tipped 20-30 degrees up off the aim. The sprite's own level axis
    //     (the game draws every weapon level, muzzle or blade to the right) is tried too, and the better cover wins;
    //   - the bonus for the model's front (+x) toward the art's front is bigger (FrontBonus), as a level minigun's outline
    //     covered its sprite better backwards;
    //   - the bonus for the model's top (+y) toward the art's top counts in full only when the art's front runs along the
    //     axis tried (UpAlong), else it only breaks ties (UpTie): a melee weapon's front (equippedAngleOffset, -65) is well
    //     off its level sprite, so its "top" ran along the handle and turned the axe's blade over to the side of the handle
    //     the art has none.
    // anim_scripts/preview.py does the same (fit_model), so the preview shows what the game draws.
    public static class WeaponFit
    {
        private const string Tag = "[M3AT] ";
        private const int N = 64;                 // the sprite's mask, N x N (as IsoRim's fit has it)
        private const float FrontBonus = 0.5f;    // the model's front toward the art's front (IsoRim's is 0.08)
        private const float UpBonus = 0.2f;       // the model's top (+y) toward the art's top
        private const float Sparse = 1.6f;        // the model laid along the sprite covers this many times its pixels: sized by area
        private const float LongAxis = 0.9f;      // a model side this share of its longest counts as long (lies along the sprite)
        private const float UpAlong = 0.87f;      // the art's front at least this close (cosine) to the axis tried for its top to count
        private const float UpTie = 0.01f;        // ... else the top bonus only breaks ties (symmetric models keep their old fit)

        // A model laid into the slab's unit square: slab (model space to slab space) and tip (the barrel's end, slab space),
        // and how well the model's outline covers the sprite's and along which of its axes, for the logs.
        public sealed class Hold
        {
            public Matrix4x4 slab;
            public Vector3 tip;
            public float iou;
            public float axis;
        }

        private static readonly Dictionary<ValueTuple<ModelHandle, int>, Hold> holds = new Dictionary<ValueTuple<ModelHandle, int>, Hold>();
        private static readonly Dictionary<ModelHandle, Vector3[]> soups = new Dictionary<ModelHandle, Vector3[]>();
        private static readonly List<FigureMeshPart> parts = new List<FigureMeshPart>();

        // The model's faces as a triangle soup (three corners a face, model space): the solid ones of IsoRim's ready meshes of
        // it (Api.ModelParts), whose corners are the model's own. Read once per model; null for one with none.
        private static Vector3[] SoupOf(ModelHandle m)
        {
            Vector3[] soup;
            if (soups.TryGetValue(m, out soup))
            {
                return soup;
            }
            List<Vector3> corners = new List<Vector3>();
            Api.ModelParts(m, Color.white, null, parts);
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Outline || parts[i].Mesh == null)
                {
                    continue;
                }
                Vector3[] verts = parts[i].Mesh.vertices;
                int[] tris = parts[i].Mesh.triangles;
                for (int k = 0; k < tris.Length; k++)
                {
                    corners.Add(verts[tris[k]]);
                }
            }
            parts.Clear();
            soup = corners.Count >= 3 ? corners.ToArray() : null;
            soups[m] = soup;
            return soup;
        }

        // The model laid into the slab's unit square, or null when the sprite or the model can't be read. barrelEnd: the
        // model's (ThingModelInfo.BarrelEnd, model space). Worked out once per model and material.
        public static Hold HoldOf(ModelHandle m, Vector3 barrelEnd, Material mat, Vector2 drawSize, float angleOffset)
        {
            if (m == null || mat == null || mat.mainTexture == null)
            {
                return null;
            }
            ValueTuple<ModelHandle, int> key = new ValueTuple<ModelHandle, int>(m, mat.GetInstanceID());
            Hold h;
            if (holds.TryGetValue(key, out h))
            {
                return h;
            }
            try
            {
                Vector3[] soup = SoupOf(m);
                h = soup != null ? Fit(soup, barrelEnd, mat, drawSize, angleOffset) : null;
            }
            catch (Exception e)
            {
                h = null;
                Log.ErrorOnce(Tag + "fitting a weapon model to its sprite failed: " + e, 61530118);
            }
            holds[key] = h;
            return h;
        }

        private static readonly Dictionary<Hold, Hold> groundSized = new Dictionary<Hold, Hold>();

        // A thrown weapon (a grenade: its sprite a heap of a few, the model fitted to the biggest): the hold scaled about the
        // model's middle so the model comes out at its size on the ground (groundScale: its ModelDef's scale, cells per model
        // unit, ThingModelInfo.Scale on the ground) rather than the sprite's, as IsoRim's own figure holds it. Cached per hold.
        public static Hold AtGroundSize(Hold h, ModelHandle m, float groundScale, Vector2 drawSize)
        {
            Hold g;
            if (groundSized.TryGetValue(h, out g))
            {
                return g;
            }
            g = h;
            if (groundScale > 0f)
            {
                // Back out of the slab's unit square into drawn cells, where the fit is a turn and an even scale.
                Matrix4x4 drawn = Matrix4x4.Scale(new Vector3(drawSize.x, drawSize.y, 1f)) * h.slab;
                float fitted = ((Vector3)drawn.GetColumn(0)).magnitude;   // drawn cells per model unit
                if (fitted > 1e-6f)
                {
                    float k = groundScale / fitted;
                    Vector3 c = h.slab.MultiplyPoint3x4((m.Min + m.Max) * 0.5f);
                    Matrix4x4 unit = Matrix4x4.Scale(new Vector3(1f / drawSize.x, 1f / drawSize.y, 1f));
                    Matrix4x4 back = Matrix4x4.Scale(new Vector3(drawSize.x, drawSize.y, 1f));
                    // Evenly in drawn cells, about the middle, then into the unit square again.
                    Matrix4x4 about = Matrix4x4.Translate(c) * unit * Matrix4x4.Scale(new Vector3(k, k, k)) * back * Matrix4x4.Translate(-c);
                    g = new Hold
                    {
                        slab = about * h.slab,
                        tip = about.MultiplyPoint3x4(h.tip),
                        iou = h.iou,
                        axis = h.axis
                    };
                }
            }
            groundSized[h] = g;
            return g;
        }

        private static readonly Dictionary<Hold, float> halfDepths = new Dictionary<Hold, float>();

        // How far the model stands off the slab's middle, either way out of its face, in cells at the sprite's drawn size:
        // the fit centres it there. Cached per hold.
        public static float HalfDepth(Hold h, ModelHandle m)
        {
            float depth;
            if (!halfDepths.TryGetValue(h, out depth))
            {
                depth = 0f;
                Vector3[] soup = SoupOf(m);
                for (int i = 0; soup != null && i < soup.Length; i++)
                {
                    depth = Mathf.Max(depth, Mathf.Abs(h.slab.MultiplyPoint3x4(soup[i]).z));
                }
                halfDepths[h] = depth;
            }
            return depth;
        }

        private static bool[] MaskOf(Material mat)
        {
            Color32[] px = Api.ReadSpritePixels(mat.mainTexture, N);
            if (px == null || px.Length < N * N)
            {
                return null;
            }
            bool[] mask = new bool[N * N];
            bool any = false;
            for (int k = 0; k < N * N; k++)
            {
                mask[k] = px[k].a > 128;
                any |= mask[k];
            }
            return any ? mask : null;
        }

        // A sprite of several of the thing apart (a heap of grenades) cut to its largest piece (WeaponModels.OnePiece).
        private static bool[] OnePiece(bool[] mask)
        {
            int[] label = new int[N * N];
            List<int> sizes = new List<int> { 0 };
            Stack<int> todo = new Stack<int>();
            int total = 0;
            for (int k = 0; k < N * N; k++)
            {
                if (!mask[k] || label[k] != 0)
                {
                    continue;
                }
                int id = sizes.Count, count = 0;
                sizes.Add(0);
                label[k] = id;
                todo.Push(k);
                while (todo.Count > 0)
                {
                    int q = todo.Pop();
                    count++;
                    int qi = q % N, qj = q / N;
                    for (int d = 0; d < 4; d++)
                    {
                        int ni = qi + (d == 0 ? 1 : d == 1 ? -1 : 0), nj = qj + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (ni < 0 || nj < 0 || ni >= N || nj >= N)
                        {
                            continue;
                        }
                        int n = nj * N + ni;
                        if (mask[n] && label[n] == 0)
                        {
                            label[n] = id;
                            todo.Push(n);
                        }
                    }
                }
                sizes[id] = count;
                total += count;
            }
            int best = 0, big = 0;
            for (int id = 1; id < sizes.Count; id++)
            {
                if (sizes[id] > sizes[best])
                {
                    best = id;
                }
                if (sizes[id] >= total * 0.15f)
                {
                    big++;
                }
            }
            if (big < 2 || sizes[best] >= total * 0.85f)
            {
                return mask;
            }
            bool[] one = new bool[N * N];
            for (int k = 0; k < N * N; k++)
            {
                one[k] = label[k] == best;
            }
            return one;
        }

        // Pixel (i, j) (row 0 the bottom) in slab space at the drawn size, cells.
        private static Vector2 Drawn(int i, int j, Vector2 drawSize)
        {
            return new Vector2(((i + 0.5f) / N - 0.5f) * drawSize.x, ((j + 0.5f) / N - 0.5f) * drawSize.y);
        }

        // The way the game points the art (WeaponModels.ArtFront), slab space at the drawn size, unit length.
        private static Vector2 ArtFront(Vector2 drawSize, float angleOffset)
        {
            Vector3 d3 = Quaternion.AngleAxis(-angleOffset, Vector3.up) * Vector3.right;
            Vector2 d = new Vector2(d3.x * drawSize.x, d3.z * drawSize.y);
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector2.right;
        }

        private struct Placing
        {
            public float score, iou;
            public Matrix4x4 place;
            public int drawn;
            public Vector2 centre;
        }

        private static Hold Fit(Vector3[] m, Vector3 barrelEnd, Material mat, Vector2 drawSize, float angleOffset)
        {
            bool[] sprite = MaskOf(mat);
            if (sprite == null)
            {
                return null;
            }
            sprite = OnePiece(sprite);
            List<Vector2> pts = new List<Vector2>();
            for (int j = 0; j < N; j++)
            {
                for (int i = 0; i < N; i++)
                {
                    if (sprite[j * N + i])
                    {
                        pts.Add(Drawn(i, j, drawSize));
                    }
                }
            }
            // IsoRim's long axis: the narrowest width across.
            float bestWidth = float.MaxValue, theta = 0f;
            for (int a = 0; a < 180; a++)
            {
                float r = a * Mathf.Deg2Rad;
                Vector2 perp = new Vector2(-Mathf.Sin(r), Mathf.Cos(r));
                float lo = float.MaxValue, hi = float.MinValue;
                for (int k = 0; k < pts.Count; k++)
                {
                    float d = Vector2.Dot(pts[k], perp);
                    lo = Mathf.Min(lo, d);
                    hi = Mathf.Max(hi, d);
                }
                if (hi - lo < bestWidth - 1e-5f)
                {
                    bestWidth = hi - lo;
                    theta = a;
                }
            }
            Vector2 front = ArtFront(drawSize, angleOffset);
            Vector3 lo3 = m[0], hi3 = m[0];
            for (int i = 1; i < m.Length; i++)
            {
                lo3 = Vector3.Min(lo3, m[i]);
                hi3 = Vector3.Max(hi3, m[i]);
            }
            Vector3 ext = hi3 - lo3, mid = (lo3 + hi3) * 0.5f;
            float modelLength = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z));
            Placing best;
            bool found = Along(m, sprite, pts, theta, drawSize, front, ext, mid, modelLength, out best);
            Placing level;
            if (theta != 0f && Along(m, sprite, pts, 0f, drawSize, front, ext, mid, modelLength, out level) && (!found || level.score > best.score))
            {
                best = level;
                found = true;
            }
            if (!found)
            {
                return null;
            }
            // A sprite much sparser than the model laid along it (a heap of a few small grenades, one model): the model sized
            // by the area it covers instead, about the sprite's middle.
            Matrix4x4 place = best.place;
            if (best.drawn > pts.Count * Sparse)
            {
                float shrink = Mathf.Sqrt((float)pts.Count / best.drawn);
                Vector3 c3 = new Vector3(best.centre.x, best.centre.y, 0f);
                place = Matrix4x4.Translate(c3) * Matrix4x4.Scale(new Vector3(shrink, shrink, shrink)) * Matrix4x4.Translate(-c3) * place;
            }
            // Into the slab's unit square (the drawn size is put back by the slab's own matrix).
            Matrix4x4 unit = Matrix4x4.Scale(new Vector3(1f / drawSize.x, 1f / drawSize.y, 1f));
            Vector3 fwd3 = place.MultiplyVector(Vector3.right);
            Vector3 tip = Vector2.Dot(new Vector2(fwd3.x, fwd3.y).normalized, front) > 0.3f
                ? place.MultiplyPoint3x4(barrelEnd) : Extreme(m, place, front);
            Hold h = new Hold();
            h.slab = unit * place;
            h.tip = unit.MultiplyPoint3x4(tip);
            h.iou = best.iou;
            h.axis = theta;
            return h;
        }

        // The best of the 24 square turns of the model laid along the sprite axis theta (degrees from +u toward +v).
        private static bool Along(Vector3[] m, bool[] sprite, List<Vector2> pts, float theta, Vector2 drawSize, Vector2 front, Vector3 ext,
            Vector3 mid, float modelLength, out Placing best)
        {
            best = new Placing { score = float.MinValue };
            float tr = theta * Mathf.Deg2Rad;
            Vector2 along = new Vector2(Mathf.Cos(tr), Mathf.Sin(tr)), across = new Vector2(-Mathf.Sin(tr), Mathf.Cos(tr));
            float aLo = float.MaxValue, aHi = float.MinValue, pLo = float.MaxValue, pHi = float.MinValue;
            for (int k = 0; k < pts.Count; k++)
            {
                float da = Vector2.Dot(pts[k], along), dp = Vector2.Dot(pts[k], across);
                aLo = Mathf.Min(aLo, da);
                aHi = Mathf.Max(aHi, da);
                pLo = Mathf.Min(pLo, dp);
                pHi = Mathf.Max(pHi, dp);
            }
            float spriteLength = aHi - aLo;
            Vector2 centre = along * ((aLo + aHi) * 0.5f) + across * ((pLo + pHi) * 0.5f);
            Matrix4x4 rotSprite = Matrix4x4.identity;   // the slab's x, y turned onto the sprite's axis
            rotSprite.SetColumn(0, new Vector4(along.x, along.y, 0f, 0f));
            rotSprite.SetColumn(1, new Vector4(across.x, across.y, 0f, 0f));
            float upBonus = Mathf.Abs(Vector2.Dot(along, front)) >= UpAlong ? UpBonus : UpTie;
            bool[] drawn = new bool[N * N];
            int[][] perms = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
            int[] permSign = { 1, -1, -1, 1, 1, -1 };
            bool any = false;
            for (int pi = 0; pi < perms.Length; pi++)
            {
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        int sz = permSign[pi] * sx * sy;   // a proper turn (no mirror)
                        int[] p = perms[pi];
                        // Slab axis r takes the model's axis p[r] (times its sign).
                        Matrix4x4 R = Matrix4x4.zero;
                        R[0, p[0]] = sx;
                        R[1, p[1]] = sy;
                        R[2, p[2]] = sz;
                        R[3, 3] = 1f;
                        float lx = ext[p[0]];
                        if (lx < 1e-4f || lx < modelLength * LongAxis)
                        {
                            continue;
                        }
                        float s = spriteLength / lx;
                        Matrix4x4 place = Matrix4x4.Translate(new Vector3(centre.x, centre.y, 0f)) * rotSprite * Matrix4x4.Scale(new Vector3(s, s, s)) * R
                            * Matrix4x4.Translate(-mid);
                        Array.Clear(drawn, 0, drawn.Length);
                        Raster(m, place, drawSize, drawn);
                        int both = 0, either = 0, filled = 0;
                        for (int k = 0; k < N * N; k++)
                        {
                            if (drawn[k])
                            {
                                filled++;
                            }
                            if (drawn[k] && sprite[k])
                            {
                                both++;
                            }
                            if (drawn[k] || sprite[k])
                            {
                                either++;
                            }
                        }
                        float iou = either > 0 ? (float)both / either : 0f;
                        Vector3 mf = place.MultiplyVector(Vector3.right);
                        Vector3 mu = place.MultiplyVector(Vector3.up);
                        float score = iou + (Vector2.Dot(new Vector2(mf.x, mf.y).normalized, front) > 0.3f ? FrontBonus : 0f)
                            + (Vector2.Dot(new Vector2(mu.x, mu.y).normalized, new Vector2(-front.y, front.x)) > 0.3f ? upBonus : 0f);
                        if (score > best.score)
                        {
                            best = new Placing { score = score, iou = iou, place = place, drawn = filled, centre = centre };
                            any = true;
                        }
                    }
                }
            }
            return any;
        }

        // The middle of the model's corners (placed in slab space at the drawn size) furthest along dir.
        private static Vector3 Extreme(Vector3[] m, Matrix4x4 place, Vector2 dir)
        {
            float best = float.MinValue;
            for (int i = 0; i < m.Length; i++)
            {
                Vector3 q = place.MultiplyPoint3x4(m[i]);
                best = Mathf.Max(best, q.x * dir.x + q.y * dir.y);
            }
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < m.Length; i++)
            {
                Vector3 q = place.MultiplyPoint3x4(m[i]);
                if (q.x * dir.x + q.y * dir.y >= best - 0.02f)
                {
                    sum += q;
                    n++;
                }
            }
            return sum / Mathf.Max(1, n);
        }

        // The model's triangles (placed in slab space at the drawn size) filled into the N x N grid of the sprite's pixels.
        private static void Raster(Vector3[] p, Matrix4x4 place, Vector2 drawSize, bool[] grid)
        {
            for (int t = 0; t + 2 < p.Length; t += 3)
            {
                Vector2 a = Grid(place.MultiplyPoint3x4(p[t]), drawSize);
                Vector2 b = Grid(place.MultiplyPoint3x4(p[t + 1]), drawSize);
                Vector2 c = Grid(place.MultiplyPoint3x4(p[t + 2]), drawSize);
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                int x1 = Mathf.Min(N - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                int y1 = Mathf.Min(N - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
                float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (Mathf.Abs(area) < 1e-6f)
                {
                    continue;
                }
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 q = new Vector2(x + 0.5f, y + 0.5f);
                        float w0 = ((b.x - q.x) * (c.y - q.y) - (b.y - q.y) * (c.x - q.x)) / area;
                        float w1 = ((c.x - q.x) * (a.y - q.y) - (c.y - q.y) * (a.x - q.x)) / area;
                        float w2 = 1f - w0 - w1;
                        if (w0 >= -1e-4f && w1 >= -1e-4f && w2 >= -1e-4f)
                        {
                            grid[y * N + x] = true;
                        }
                    }
                }
            }
        }

        // A point in slab space (drawn size) in grid units (pixel centres at i + 0.5).
        private static Vector2 Grid(Vector3 q, Vector2 drawSize)
        {
            return new Vector2((q.x / drawSize.x + 0.5f) * N, (q.y / drawSize.y + 0.5f) * N);
        }

        private static readonly Dictionary<int, Vector2> barrelEnds = new Dictionary<int, Vector2>();

        // The end of a weapon sprite's barrel in texture coordinates (u, v), as IsoRim finds it for a figure's slab: of its
        // opaque pixels, the middle of those furthest along the way the game points the sprite (+u turned back by
        // equippedAngleOffset), measured at the drawn size so a long sprite is not squeezed. Per material; false for none.
        public static bool SpriteBarrelEnd(Thing weapon, out Vector2 uv)
        {
            uv = new Vector2(1f, 0.5f);
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            if (mat == null || mat.mainTexture == null)
            {
                return false;
            }
            int key = mat.GetInstanceID();
            if (barrelEnds.TryGetValue(key, out uv))
            {
                return uv.x >= 0f;
            }
            const int n = 128;
            Color32[] px = Api.ReadSpritePixels(mat.mainTexture, n);
            uv = new Vector2(-1f, -1f);
            if (px != null && px.Length >= n * n)
            {
                Vector3 d3 = Quaternion.AngleAxis(-weapon.def.equippedAngleOffset, Vector3.up) * Vector3.right;
                Vector2 dir = new Vector2(d3.x * g.drawSize.x, d3.z * g.drawSize.y);   // along the art, texture units scaled to cells
                float best = float.MinValue;
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (px[j * n + i].a > 128)
                        {
                            best = Mathf.Max(best, (i + 0.5f) / n * dir.x + (j + 0.5f) / n * dir.y);
                        }
                    }
                }
                if (best > float.MinValue)
                {
                    // The pixels within a pixel and a half of the furthest: the mouth of the barrel.
                    float near = best - 1.5f / n * dir.magnitude;
                    Vector2 sum = Vector2.zero;
                    int count = 0;
                    for (int j = 0; j < n; j++)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (px[j * n + i].a > 128 && (i + 0.5f) / n * dir.x + (j + 0.5f) / n * dir.y >= near)
                            {
                                sum += new Vector2((i + 0.5f) / n, (j + 0.5f) / n);
                                count++;
                            }
                        }
                    }
                    uv = sum / count;
                }
            }
            barrelEnds[key] = uv;
            return uv.x >= 0f;
        }
    }
}
