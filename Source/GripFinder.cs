using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Where the hand closes on a weapon's sprite, found from its alpha alone, for a weapon with no hold of its own (another
    // mod's): its kind's hold then slid by how far this sprite's grip is from the grip on the kind's own sprite
    // (WalkFigures.GripSlide). A port of anim_scripts/grip_detect/detect.py, which scores it against the hand-placed holds
    // of the game's own weapons (score.py) and draws it over a mod's sprites (modsheet.py); change the two together.
    //
    // All in the sprite's own texture places, u to the right and v up, on a Size x Size mask (alpha over half).
    //   - A gun, drawn level with its muzzle to the right (turned level first by its equippedAngleOffset): the body's
    //     underside is the highest cut that leaves the most separate parts hanging below it (stock, grip, magazine, ...).
    //     The grip is the rearmost of them that is not the stock, and on it the rearmost leg reaching more than half its
    //     depth down (a trigger guard or a lip can join it to the magazine), followed up its slant to just under the body.
    //     With no pistol grip, the stock's wrist: its thinnest column behind the body.
    //   - A melee weapon, its head to the right: along its long axis the handle is the narrowest stretch from the left
    //     end, a pommel left out, the guard or head where it widens. The fist in the handle's middle, near the guard on a
    //     two-handed one, 40% of the way along a long shaft.
    // Anything less sure (a grip in the front half of the gun or as wide as a boxy body, no guard after a short handle,
    // no narrow stretch near the end) is no answer, and the kind's hold stays as it is.
    public static class GripFinder
    {
        private const int Size = 128;

        private static readonly Dictionary<ValueTuple<int, string, float>, Vector2?> found = new Dictionary<ValueTuple<int, string, float>, Vector2?>();

        // kind: "gun", "melee" or "melee2" (two-handed). offset: the weapon's equippedAngleOffset (a gun's barrel runs that
        // many degrees counterclockwise from the art's right, as WeaponFit.SpriteBarrelEnd reads it). Per texture, kind and
        // offset; false when the art is unreadable or the grip unclear.
        public static bool Find(Texture tex, string kind, float offset, out Vector2 uv)
        {
            uv = Vector2.zero;
            if (tex == null)
            {
                return false;
            }
            ValueTuple<int, string, float> key = new ValueTuple<int, string, float>(tex.GetInstanceID(), kind, offset);
            Vector2? at;
            if (!found.TryGetValue(key, out at))
            {
                at = null;
                try
                {
                    Color32[] px = Api.ReadSpritePixels(tex, Size);
                    if (px != null && px.Length >= Size * Size)
                    {
                        bool[] m = new bool[Size * Size];
                        for (int k = 0; k < m.Length; k++)
                        {
                            m[k] = px[k].a > 128;
                        }
                        at = kind == "gun" ? Gun(Turned(m, -offset), offset) : Melee(m, kind == "melee2");
                    }
                }
                catch (Exception e)
                {
                    at = null;
                    Log.WarningOnce("[M3AT] finding a weapon's grip failed: " + e, 61530119);
                }
                found[key] = at;
            }
            if (at.HasValue)
            {
                uv = at.Value;
            }
            return at.HasValue;
        }

        // ---- the mask: m[j * Size + i], row j going up the sprite

        // 4-connected parts: a label per pixel (0 for none) and how many.
        private static int Label(bool[] m, int[] label)
        {
            Array.Clear(label, 0, label.Length);
            int n = 0;
            Stack<int> todo = new Stack<int>();
            for (int p = 0; p < m.Length; p++)
            {
                if (!m[p] || label[p] != 0)
                {
                    continue;
                }
                label[p] = ++n;
                todo.Push(p);
                while (todo.Count > 0)
                {
                    int q = todo.Pop(), j = q / Size, i = q % Size;
                    Visit(m, label, todo, n, j + 1, i);
                    Visit(m, label, todo, n, j - 1, i);
                    Visit(m, label, todo, n, j, i + 1);
                    Visit(m, label, todo, n, j, i - 1);
                }
            }
            return n;
        }

        private static void Visit(bool[] m, int[] label, Stack<int> todo, int n, int j, int i)
        {
            if (j < 0 || j >= Size || i < 0 || i >= Size)
            {
                return;
            }
            int q = j * Size + i;
            if (m[q] && label[q] == 0)
            {
                label[q] = n;
                todo.Push(q);
            }
        }

        // The largest part alone (a sprite of several things apart, or stray pixels).
        private static bool[] Largest(bool[] m)
        {
            int[] label = new int[m.Length];
            int n = Label(m, label);
            if (n == 0)
            {
                return m;
            }
            int[] count = new int[n + 1];
            for (int p = 0; p < m.Length; p++)
            {
                count[label[p]]++;
            }
            int best = 1;
            for (int k = 2; k <= n; k++)
            {
                if (count[k] > count[best])
                {
                    best = k;
                }
            }
            bool[] o = new bool[m.Length];
            for (int p = 0; p < m.Length; p++)
            {
                o[p] = label[p] == best;
            }
            return o;
        }

        // The mask turned deg degrees counterclockwise about its middle (nearest pixel).
        private static bool[] Turned(bool[] m, float deg)
        {
            if (Mathf.Abs(deg) < 1e-3f)
            {
                return m;
            }
            float t = deg * Mathf.Deg2Rad, c = Mathf.Cos(t), s = Mathf.Sin(t), h = Size / 2f;
            bool[] o = new bool[m.Length];
            for (int j = 0; j < Size; j++)
            {
                for (int i = 0; i < Size; i++)
                {
                    float x = i + 0.5f - h, y = j + 0.5f - h;
                    float si = c * x + s * y + h, sj = -s * x + c * y + h;
                    if (si >= 0f && si < Size && sj >= 0f && sj < Size)
                    {
                        o[j * Size + i] = m[(int)sj * Size + (int)si];
                    }
                }
            }
            return o;
        }

        // A texture place turned deg degrees counterclockwise about the middle.
        private static Vector2 TurnPoint(Vector2 p, float deg)
        {
            float t = deg * Mathf.Deg2Rad, c = Mathf.Cos(t), s = Mathf.Sin(t);
            float x = p.x - 0.5f, y = p.y - 0.5f;
            return new Vector2(0.5f + c * x - s * y, 0.5f + s * x + c * y);
        }

        // ---- guns

        private sealed class Part
        {
            public int lo, hi, depth;
            public bool rear;
            public List<int> pixels = new List<int>();
        }

        // The parts below row h deep enough to count, rear to front.
        private static List<Part> Hanging(bool[] m, int h, int i0, int length, int[] label)
        {
            bool[] below = new bool[m.Length];
            Array.Copy(m, below, h * Size);
            int n = Label(below, label);
            Part[] parts = new Part[n + 1];
            for (int p = 0; p < h * Size; p++)
            {
                int k = label[p];
                if (k == 0)
                {
                    continue;
                }
                Part part = parts[k];
                if (part == null)
                {
                    part = parts[k] = new Part { lo = Size, hi = -1, depth = 0 };
                }
                int j = p / Size, i = p % Size;
                part.lo = Math.Min(part.lo, i);
                part.hi = Math.Max(part.hi, i);
                part.depth = Math.Max(part.depth, h - j);
                part.pixels.Add(p);
            }
            List<Part> list = new List<Part>();
            for (int k = 1; k <= n; k++)
            {
                if (parts[k] != null && parts[k].depth >= Mathf.Max(3f, 0.06f * length))
                {
                    parts[k].rear = parts[k].lo <= i0 + 0.04f * length;
                    list.Add(parts[k]);
                }
            }
            list.Sort((a, b) => a.lo.CompareTo(b.lo));
            return list;
        }

        // A gun turned level (muzzle to the right); the place is turned back by offset.
        private static Vector2? Gun(bool[] mask, float offset)
        {
            bool[] m = Largest(mask);
            int i0 = Size, i1 = -1, j0 = Size, j1 = -1;
            int[] top = new int[Size], bot = new int[Size];
            for (int i = 0; i < Size; i++)
            {
                top[i] = -1;
                bot[i] = Size;
            }
            for (int j = 0; j < Size; j++)
            {
                for (int i = 0; i < Size; i++)
                {
                    if (m[j * Size + i])
                    {
                        i0 = Math.Min(i0, i);
                        i1 = Math.Max(i1, i);
                        j0 = Math.Min(j0, j);
                        j1 = Math.Max(j1, j);
                        top[i] = Math.Max(top[i], j);
                        bot[i] = Math.Min(bot[i], j);
                    }
                }
            }
            if (i1 < 0)
            {
                return null;
            }
            int length = i1 - i0 + 1;
            int[] label = new int[m.Length];
            int base0 = -1, bestWiden = 0;
            List<Part> parts = null;
            for (int h = j0 + 3; h < j1; h++)
            {
                List<Part> here = Hanging(m, h, i0, length, label);
                bool whole = false;
                foreach (Part p in here)
                {
                    whole |= p.hi - p.lo > 0.7f * length;
                }
                if (whole)
                {
                    break;   // above the body's underside: everything hangs as one
                }
                // The most parts; between cuts with as many, where the rows widen most going up (the body's underside).
                int widen = RowWidth(m, h, i0, i1) - RowWidth(m, h - 1, i0, i1);
                if (parts == null || here.Count > parts.Count || (here.Count == parts.Count && widen >= bestWiden))
                {
                    base0 = h;
                    parts = here;
                    bestWiden = widen;
                }
            }
            if (parts == null)
            {
                return null;
            }
            Part stock = null;
            if (parts.Count > 0 && parts[0].rear && (parts.Count > 1 || parts[0].hi - parts[0].lo > 0.25f * length))
            {
                stock = parts[0];
                parts.RemoveAt(0);
            }
            if (parts.Count > 0 && (stock == null || parts[0].lo - stock.hi < 0.5f * length))
            {
                // A trigger guard or a lip under the body can join the grip to what is in front of it: the grip is the
                // rearmost leg reaching more than half the part's depth down, followed up its slant to just under the body.
                Part g = parts[0];
                int cut = Mathf.CeilToInt(base0 - 0.5f * g.depth);
                bool[] sub = new bool[m.Length];
                foreach (int p in g.pixels)
                {
                    if (p / Size < cut)
                    {
                        sub[p] = true;
                    }
                }
                int n = Label(sub, label);
                int leg = 0, legLo = Size;
                for (int p = 0; p < sub.Length; p++)
                {
                    if (label[p] != 0 && p % Size < legLo)
                    {
                        legLo = p % Size;
                        leg = label[p];
                    }
                }
                if (n == 0 || leg == 0)
                {
                    return null;
                }
                // The leg's pixel middles: a line i(j) fitted through them, read at y.
                double sj = 0, si = 0, sjj = 0, sji = 0;
                int count = 0, ljMin = Size, ljMax = -1, liMin = Size, liMax = -1;
                for (int p = 0; p < sub.Length; p++)
                {
                    if (label[p] == leg)
                    {
                        double j = p / Size + 0.5, i = p % Size + 0.5;
                        sj += j;
                        si += i;
                        sjj += j * j;
                        sji += j * i;
                        count++;
                        ljMin = Math.Min(ljMin, p / Size);
                        ljMax = Math.Max(ljMax, p / Size);
                        liMin = Math.Min(liMin, p % Size);
                        liMax = Math.Max(liMax, p % Size);
                    }
                }
                float y = base0 - 0.3f * g.depth;
                float x;
                double varJ = sjj - sj * sj / count;
                if (ljMax - ljMin >= 2 && varJ > 1e-9)
                {
                    double slope = (sji - sj * si / count) / varJ;
                    x = (float)(slope * y + (si - slope * sj) / count);
                }
                else
                {
                    x = (float)(si / count);
                }
                bool narrow = liMax - liMin <= 0.25f * length;   // a grip, not the underside of a boxy body
                if ((x - i0) / length >= 0.45f || !narrow)
                {
                    return null;
                }
                return TurnPoint(new Vector2(x / Size, (y + 0.5f) / Size), offset);
            }
            if (stock != null)
            {
                // No pistol grip: the stock's wrist, its thinnest column between the butt and the body, where the stock
                // narrows to it (thinner than the butt and the body ahead), reaching into the gun's lower half (not a
                // rail along its top).
                int wrist = -1;
                for (int i = (int)(i0 + 0.12f * length); i < (int)(i0 + 0.35f * length); i++)
                {
                    if (wrist < 0 || top[i] - bot[i] <= top[wrist] - bot[wrist])
                    {
                        wrist = i;
                    }
                }
                if (wrist < 0)
                {
                    return null;
                }
                int butt = 0, ahead = 0;
                for (int i = i0; i <= (int)(i0 + 0.1f * length); i++)
                {
                    butt = Math.Max(butt, top[i] - bot[i]);
                }
                for (int i = wrist; i < (int)(i0 + 0.6f * length); i++)
                {
                    ahead = Math.Max(ahead, top[i] - bot[i]);
                }
                int thick = top[wrist] - bot[wrist];
                if (thick > Mathf.Min(0.85f * butt, 0.9f * ahead) || bot[wrist] > (j0 + j1) / 2f)
                {
                    return null;
                }
                return TurnPoint(new Vector2((wrist + 0.5f) / Size, ((top[wrist] + bot[wrist]) / 2f + 0.5f) / Size), offset);
            }
            return null;
        }

        private static int RowWidth(bool[] m, int j, int i0, int i1)
        {
            int w = 0;
            for (int i = i0; i <= i1; i++)
            {
                if (m[j * Size + i])
                {
                    w++;
                }
            }
            return w;
        }

        // ---- melee

        private static Vector2? Melee(bool[] mask, bool twoHanded)
        {
            bool[] m = Largest(mask);
            List<Vector2> pts = new List<Vector2>();
            Vector2 c = Vector2.zero;
            for (int p = 0; p < m.Length; p++)
            {
                if (m[p])
                {
                    Vector2 q = new Vector2(p % Size + 0.5f, p / Size + 0.5f);
                    pts.Add(q);
                    c += q;
                }
            }
            if (pts.Count < 8)
            {
                return null;
            }
            c /= pts.Count;
            // The long axis: the covariance's larger eigenvector, pointing right (the head's way).
            double xx = 0, xy = 0, yy = 0;
            foreach (Vector2 q in pts)
            {
                Vector2 d = q - c;
                xx += d.x * d.x;
                xy += d.x * d.y;
                yy += d.y * d.y;
            }
            double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
            Vector2 ax = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            if (ax.x < 0f)
            {
                ax = -ax;
            }
            Vector2 across = new Vector2(-ax.y, ax.x);
            float s0 = float.MaxValue, s1 = float.MinValue;
            foreach (Vector2 q in pts)
            {
                float s = Vector2.Dot(q - c, ax);
                s0 = Mathf.Min(s0, s);
                s1 = Mathf.Max(s1, s);
            }
            int length = Mathf.CeilToInt(s1 - s0) + 1;
            float[] lo = new float[length], hi = new float[length];
            for (int b = 0; b < length; b++)
            {
                lo[b] = float.PositiveInfinity;
                hi[b] = float.NegativeInfinity;
            }
            foreach (Vector2 q in pts)
            {
                int b = Mathf.Clamp((int)(Vector2.Dot(q - c, ax) - s0), 0, length - 1);
                float t = Vector2.Dot(q - c, across);
                lo[b] = Mathf.Min(lo[b], t);
                hi[b] = Mathf.Max(hi[b], t);
            }
            float[] width = new float[length];
            for (int b = 0; b < length; b++)
            {
                width[b] = float.IsInfinity(lo[b]) ? 0f : hi[b] - lo[b] + 1f;
            }
            int from = Math.Max(2, (int)(0.06f * length)), to = Math.Max(4, (int)(0.45f * length));
            if (to >= length)
            {
                return null;
            }
            int m0 = from;
            for (int b = from; b < to; b++)
            {
                if (width[b] < width[m0])
                {
                    m0 = b;
                }
            }
            float least = width[m0];
            float thr = least + Mathf.Max(2f, 0.2f * least);
            int k = m0, e = m0;
            while (k > 0 && width[k - 1] <= thr)
            {
                k--;
            }
            while (e < length && width[e] <= thr)
            {
                e++;
            }
            if (k > 0.2f * length)
            {
                return null;   // the narrowest stretch is not at the end: no handle found (a knuckle guard, a blade)
            }
            float f;
            if (e > 0.45f * length)
            {
                f = k + 0.4f * (e - k);   // a long shaft
            }
            else
            {
                f = k + (twoHanded ? 0.85f : 0.55f) * (e - k);
                float jump = e + 1 < length ? width[e + 1] : 0f;
                if (jump < 1.3f * least + 2f)
                {
                    return null;   // no guard or head after a short handle: not sure where it ends
                }
            }
            int fb = Mathf.Clamp((int)f, 0, length - 1);
            float tm = float.IsInfinity(lo[fb]) ? 0f : (lo[fb] + hi[fb]) / 2f;
            Vector2 at = c + ax * (s0 + f + 0.5f) + across * tm;
            return new Vector2(at.x / Size, at.y / Size);
        }
    }
}
