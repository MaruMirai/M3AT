using System;
using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // Walls for built hair hung from the head's hairline rather than from the hair's own edges. The builder hangs a wall
    // under each open edge of the hair, in toward the head, so that looking up under the hair doesn't see through to its
    // inside; but the open edges are also every lock's ragged edge, and a lock that should dangle got a slab of thickness
    // that seldom looked right. These go the other way, the same for every style: from the hairline on the skin (the
    // rig's Hairline curve, skin.txt's hairline line, anim_scripts/make_hairline.py; without one the edge of its Scalp
    // group, which is drawn small to leave room for the scalp colour's fade), straight out along the skin until they
    // meet the hair, as hair growing out of the head. Where the hair doesn't cover the hairline (a face framed by locks well back from it, a parting), the
    // foot of the wall walks back over the skin toward the top of the head until there is hair over it. A style that
    // covers too little of the hairline that way (a mohawk) keeps the builder's walls (HairlineCover).
    //
    // They are made as the hair is read, after it is cut down (LoadHair, Decimated), so their tops meet the cut hair's
    // own surface: each top is a point on a hair triangle, with that triangle's texture place, normal and keys there, and
    // each foot a point on the skin with the body's keys there.
    public sealed partial class SkinnedFigure
    {
        public static bool HairlineWallsOn = true;   // false: the builder's walls under the hair's open edges, as before
        private const float WallStep = 0.02f;       // cells between the walls' columns along the hairline (at 0.008 a quarter of a cut style's triangles)
        private const float WallReach = 0.1f;        // cells out from the skin a wall looks for the hair
        private const float WallSink = 0.006f;       // cells a wall's foot is sunk into the head
        private const float WallTilt = 0.6f;         // a second look tilted this far up (added to the skin's normal) if the first misses
        private const int WalkSteps = 40;            // most steps over the skin from the hairline toward the top of the head
        private const float WalkStride = 0.01f;      // cells a step
        private const float WallJump = 0.03f;        // cells: neighbouring tops this near are always joined
        private const float WallJumpMost = 0.1f;     // cells: further apart than WallJump, joined only if the line between them stays on the hair
        private const float WallOnHair = 0.012f;     // cells: how near the hair that line must stay
        // false: each wall takes the hair's colour where its columns meet it, blended across the quad only between
        // places within WallBlendPixels of each other on the atlas, else flat at its own column's; true: every wall at
        // the hair file's one wall_uv (a darker shade of the hair), which reads as monotone.
        public static bool WallOneColour = false;
        private const float WallBlendPixels = 6f;    // atlas pixels (HairBuilder.Size a panel's side)
        internal const float HairlineCover = 0.5f;     // least share of the hairline with hair over it within CoverWalk steps
        private const int CoverWalk = 2;

        // A point on the skin under the hair: its place, outward normal and the body's key moves there.
        private struct SkinSpot
        {
            public Vector3 at, normal;
            public int a, b, c;      // the body vertices it is between
            public float wa, wb, wc;
        }

        // Built hair cut down to share of its triangles (share 1: as it is) with its walls: the hairline's, hung on the
        // cut hair; else, for a style that covers too little of the hairline, the builder's under its edges, re-hung
        // from the cut hair's open edges (Decimated). The hair is cut without its walls first, as re-hanging the
        // builder's only to drop them for the hairline's was wasted work. cover: the hairline's share covered, -1 if
        // the hairline walls weren't tried (off, a beard, or hair with no walls line).
        internal Piece CutWithWalls(Piece h, float share, bool beard, out float cover)
        {
            cover = -1f;
            if (beard || !HairlineWallsOn || h.wallsFrom < 0)
            {
                return share < 1f ? Decimated(h, h.uvs, share) : h;
            }
            Piece bare = WithoutWalls(h);
            Piece cut = share < 1f ? Decimated(bare, bare.uvs, share) : bare;
            Piece walled = HairlineWalled(cut, out cover);
            if (walled != null)
            {
                return walled;
            }
            return share < 1f ? Decimated(h, h.uvs, share) : h;
        }

        // The same piece with only its hair's triangles (those before wallsFrom), sharing everything else.
        private static Piece WithoutWalls(Piece h)
        {
            Piece p = new Piece { name = h.name, layer = h.layer, groups = h.groups, defs = h.defs, hides = h.hides, wallUv = h.wallUv };
            p.tris = new int[Math.Min(h.wallsFrom * 3, h.tris.Length)];
            Array.Copy(h.tris, p.tris, p.tris.Length);
            p.rest = h.rest;
            p.firstWeight = h.firstWeight;
            p.weightBone = h.weightBone;
            p.weight = h.weight;
            p.uvs = h.uvs;
            p.normal = h.normal;
            p.skinVert = h.skinVert;
            foreach (KeyValuePair<string, Vector3[]> key in h.keys)
            {
                p.keys[key.Key] = key.Value;
            }
            return p;
        }

        // The hair (its triangles before wallsFrom; any walls after are dropped) with hairline walls; null if the style
        // covers too little of the hairline (the caller keeps the builder's walls), or there is no scalp or head to go by.
        // cover: the share of the hairline with hair over it near enough.
        internal Piece HairlineWalled(Piece hair, out float cover)
        {
            cover = 0f;
            Piece body = pieces[0];
            bool[] scalp = Scalp(), head = OnHead();
            if (scalp == null || hair.uvs == null)
            {
                return null;
            }
            Vector3[] bv = body.rest;
            int[] bt = body.tris;
            Vector3[] bn = BodyNormals(bv, bt, head);
            // The crown: the scalp's highest point, where a wall's foot walks to; the head's triangles to walk over.
            Vector3 crown = Vector3.negativeInfinity;
            for (int i = 0; i < bv.Length; i++)
            {
                if (scalp[i] && bv[i].y > crown.y)
                {
                    crown = bv[i];
                }
            }
            List<int> headTris = new List<int>();
            for (int k = 0; k + 2 < bt.Length; k += 3)
            {
                if (head[bt[k]] && head[bt[k + 1]] && head[bt[k + 2]])
                {
                    headTris.Add(k);
                }
            }
            List<SkinSpot[]> loops = hairlineGiven.Count > 2
                ? new List<SkinSpot[]> { DrawnLine(hairlineGiven, bv, bt, bn, headTris) }
                : HairlineLoops(bv, bt, bn, scalp);
            if (loops.Count == 0 || loops[0].Length < 3)
            {
                return null;
            }
            int hairEnd = hair.wallsFrom >= 0 ? Math.Min(hair.wallsFrom * 3, hair.tris.Length) : hair.tris.Length;

            // Each column: its foot on the skin and its top on the hair (the hair triangle's corner index k and the
            // barycentric weights of its corners), or none.
            List<List<Column>> columns = new List<List<Column>>();
            int all = 0, near = 0;
            foreach (SkinSpot[] loop in loops)
            {
                List<Column> line = new List<Column>();
                foreach (SkinSpot s in loop)
                {
                    Column c = Look(hair, hairEnd, s, crown, bv, bt, bn, headTris);
                    all++;
                    near += c.found && c.walked <= CoverWalk ? 1 : 0;
                    line.Add(c);
                }
                columns.Add(line);
            }
            cover = all > 0 ? (float)near / all : 0f;
            if (cover < HairlineCover)
            {
                return null;
            }
            return Walled(hair, hairEnd, columns, body, crown);
        }

        private struct Column
        {
            public bool found;
            public int walked;       // steps the foot walked back toward the crown
            public SkinSpot foot;
            public int tri;          // the hair triangle's first corner in tris
            public float u, v;       // the hit's weights on its second and third corners
            public Vector3 top;
        }

        // Each body vertex's outward normal: the area-weighted face normals, turned out from the head's middle.
        private static Vector3[] BodyNormals(Vector3[] v, int[] t, bool[] head)
        {
            Vector3[] n = new Vector3[v.Length];
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                Vector3 f = Vector3.Cross(v[t[k + 1]] - v[t[k]], v[t[k + 2]] - v[t[k]]);
                n[t[k]] += f;
                n[t[k + 1]] += f;
                n[t[k + 2]] += f;
            }
            Vector3 mid = Vector3.zero;
            int count = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (head[i])
                {
                    mid += v[i];
                    count++;
                }
            }
            mid /= Mathf.Max(count, 1);
            float out_ = 0f;
            for (int i = 0; i < v.Length; i++)
            {
                n[i] = n[i].normalized;
                if (head[i])
                {
                    out_ += Vector3.Dot(n[i], v[i] - mid);
                }
            }
            if (out_ < 0f)
            {
                for (int i = 0; i < n.Length; i++)
                {
                    n[i] = -n[i];
                }
            }
            return n;
        }

        // The rig's own hairline (its Hairline curve, skin.txt's hairline line) as spots WallStep apart round it, each laid
        // on the head's skin (the curve stands a little off it to show in Blender).
        private static SkinSpot[] DrawnLine(List<Vector3> line, Vector3[] bv, int[] bt, Vector3[] bn, List<int> headTris)
        {
            List<SkinSpot> spots = new List<SkinSpot>();
            for (int i = 0; i < line.Count; i++)
            {
                Vector3 a = line[i], b = line[(i + 1) % line.Count];
                int steps = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / WallStep));
                for (int s = 0; s < steps; s++)
                {
                    spots.Add(OnSkin(Vector3.Lerp(a, b, (float)s / steps), bv, bt, bn, headTris));
                }
            }
            return spots.ToArray();
        }

        // The scalp's edge as loops of spots WallStep apart: the edges of the scalp's triangles (all three corners in it)
        // that only one of them has, walked end to end.
        private static List<SkinSpot[]> HairlineLoops(Vector3[] v, int[] t, Vector3[] n, bool[] scalp)
        {
            Dictionary<long, int> count = new Dictionary<long, int>();
            List<int[]> edges = new List<int[]>();
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                if (!scalp[t[k]] || !scalp[t[k + 1]] || !scalp[t[k + 2]])
                {
                    continue;
                }
                for (int c = 0; c < 3; c++)
                {
                    int a = t[k + c], b = t[k + (c + 1) % 3];
                    long e = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b);
                    int was;
                    count.TryGetValue(e, out was);
                    count[e] = was + 1;
                    edges.Add(new[] { a, b });
                }
            }
            Dictionary<int, int> next = new Dictionary<int, int>();
            foreach (int[] e in edges)
            {
                if (count[(long)Math.Min(e[0], e[1]) << 32 | (uint)Math.Max(e[0], e[1])] == 1)
                {
                    next[e[0]] = e[1];
                }
            }
            List<SkinSpot[]> loops = new List<SkinSpot[]>();
            HashSet<int> done = new HashSet<int>();
            foreach (int start in next.Keys)
            {
                if (done.Contains(start))
                {
                    continue;
                }
                List<SkinSpot> loop = new List<SkinSpot>();
                for (int a = start; !done.Contains(a) && next.ContainsKey(a); a = next[a])
                {
                    done.Add(a);
                    int b = next[a];
                    int steps = Mathf.Max(1, Mathf.CeilToInt((v[b] - v[a]).magnitude / WallStep));
                    for (int s = 0; s < steps; s++)
                    {
                        float f = (float)s / steps;
                        loop.Add(new SkinSpot
                        {
                            at = Vector3.Lerp(v[a], v[b], f),
                            normal = Vector3.Lerp(n[a], n[b], f).normalized,
                            a = a, b = b, c = b, wa = 1f - f, wb = f, wc = 0f,
                        });
                    }
                }
                if (loop.Count > 2)
                {
                    loops.Add(loop.ToArray());
                }
            }
            return loops;
        }

        // Looks out from a spot on the hairline for the hair, walking the foot back over the skin toward the crown until
        // there is hair over it.
        private static Column Look(Piece hair, int hairEnd, SkinSpot s, Vector3 crown, Vector3[] bv, int[] bt, Vector3[] bn, List<int> headTris)
        {
            SkinSpot foot = s;
            for (int step = 0; step <= WalkSteps; step++)
            {
                if (step > 0)
                {
                    // A step over the skin toward the crown, laid back on it: lerping straight to the crown went
                    // through the skull, and the nearest skin to a point inside it jumped between the forehead and the
                    // top, so neighbouring columns' feet landed far apart.
                    Vector3 way = crown - foot.at;
                    way -= foot.normal * Vector3.Dot(way, foot.normal);
                    if (way.magnitude < WalkStride)
                    {
                        break;
                    }
                    foot = OnSkin(foot.at + way.normalized * WalkStride, bv, bt, bn, headTris);
                }
                Column c;
                // Out along the skin, but never down: below the skull's widest the skin's normal points down a little,
                // and a wall from the hairline there went out and down to meet a lock lower on its inside.
                Vector3 out_ = foot.normal;
                out_.y = Mathf.Max(out_.y, 0f);
                out_ = out_.sqrMagnitude > 1e-8f ? out_.normalized : Vector3.up;
                if (Hit(hair, hairEnd, foot, out_, out c) || Hit(hair, hairEnd, foot, (out_ + Vector3.up * WallTilt).normalized, out c))
                {
                    c.walked = step;
                    return c;
                }
            }
            return new Column();
        }

        // The nearest point on the head's skin to p.
        private static SkinSpot OnSkin(Vector3 p, Vector3[] bv, int[] bt, Vector3[] bn, List<int> headTris)
        {
            SkinSpot best = new SkinSpot();
            float bestD = float.MaxValue;
            foreach (int k in headTris)
            {
                int a = bt[k], b = bt[k + 1], c = bt[k + 2];
                float u, w;
                Vector3 q = ClosestOn(p, bv[a], bv[b], bv[c], out u, out w);
                float d = (q - p).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = new SkinSpot
                    {
                        at = q,
                        normal = (bn[a] * (1f - u - w) + bn[b] * u + bn[c] * w).normalized,
                        a = a, b = b, c = c, wa = 1f - u - w, wb = u, wc = w,
                    };
                }
            }
            return best;
        }

        // The point of triangle abc nearest p and its weights on b (u) and c (w) (Ericson 5.1.5).
        private static Vector3 ClosestOn(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float w)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            u = w = 0f;
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { u = 1f; return b; }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { u = d1 / (d1 - d3); return a + ab * u; }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { w = 1f; return c; }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { w = d2 / (d2 - d6); return a + ac * w; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                u = 1f - w;
                return b + (c - b) * w;
            }
            float denom = 1f / (va + vb + vc);
            u = vb * denom;
            w = vc * denom;
            return a + ab * u + ac * w;
        }

        // The nearest hair triangle (either side) along dir from just under the foot, within WallReach of the skin.
        private static bool Hit(Piece hair, int hairEnd, SkinSpot foot, Vector3 dir, out Column c)
        {
            c = new Column();
            Vector3 o = foot.at - foot.normal * WallSink;
            float best = WallReach + WallSink;
            Vector3[] v = hair.rest;
            int[] t = hair.tris;
            for (int k = 0; k + 2 < hairEnd; k += 3)
            {
                Vector3 a = v[t[k]], e1 = v[t[k + 1]] - a, e2 = v[t[k + 2]] - a;
                Vector3 pv = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, pv);
                if (Mathf.Abs(det) < 1e-12f)
                {
                    continue;
                }
                float inv = 1f / det;
                Vector3 tv = o - a;
                float u = Vector3.Dot(tv, pv) * inv;
                if (u < 0f || u > 1f)
                {
                    continue;
                }
                Vector3 qv = Vector3.Cross(tv, e1);
                float w = Vector3.Dot(dir, qv) * inv;
                if (w < 0f || u + w > 1f)
                {
                    continue;
                }
                float d = Vector3.Dot(e2, qv) * inv;
                if (d > 1e-5f && d < best)
                {
                    best = d;
                    c.found = true;
                    c.tri = k;
                    c.u = u;
                    c.v = w;
                    c.top = o + dir * d;
                }
            }
            c.foot = foot;
            return c.found;
        }

        // Whether two neighbouring columns' tops are on the same part of the hair, so a wall between them is: near each
        // other, or with the line between them on the hair's surface all along. A fixed distance alone left a hole
        // wherever the hair lies slanted to the looks (a lock's side meets two neighbouring looks at different depths
        // while being one surface), and only a gap between locks or onto another layer should be left open.
        private static bool Joins(Piece hair, int hairEnd, Vector3 a, Vector3 b)
        {
            float apart = (a - b).magnitude;
            if (apart <= WallJump)
            {
                return true;
            }
            if (apart > WallJumpMost)
            {
                return false;
            }
            for (int s = 1; s < 4; s++)
            {
                if (HairDistance(hair, hairEnd, Vector3.Lerp(a, b, s / 4f)) > WallOnHair)
                {
                    return false;
                }
            }
            return true;
        }

        // How far p is from the nearest of the hair's triangles.
        private static float HairDistance(Piece hair, int hairEnd, Vector3 p)
        {
            float best = float.MaxValue;
            Vector3[] v = hair.rest;
            int[] t = hair.tris;
            for (int k = 0; k + 2 < hairEnd; k += 3)
            {
                float u, w;
                float d = (ClosestOn(p, v[t[k]], v[t[k + 1]], v[t[k + 2]], out u, out w) - p).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                }
            }
            return Mathf.Sqrt(best);
        }

        // Three vertices' bone weights mixed by the shares given, summed per bone and made to add up to 1; the head bone
        // alone if the piece has no weights.
        private Dictionary<int, float> BlendedWeights(Piece piece, int a, float wa, int b, float wb, int c, float wc)
        {
            Dictionary<int, float> mixed = new Dictionary<int, float>();
            if (piece.firstWeight != null)
            {
                int[] corner = { a, b, c };
                float[] share = { wa, wb, wc };
                for (int j = 0; j < 3; j++)
                {
                    if (share[j] <= 0f)
                    {
                        continue;
                    }
                    for (int k = piece.firstWeight[corner[j]]; k < piece.firstWeight[corner[j] + 1]; k++)
                    {
                        float was;
                        mixed.TryGetValue(piece.weightBone[k], out was);
                        mixed[piece.weightBone[k]] = was + piece.weight[k] * share[j];
                    }
                }
            }
            float whole = 0f;
            foreach (float w in mixed.Values)
            {
                whole += w;
            }
            if (whole <= 1e-6f)
            {
                return new Dictionary<int, float> { { headBone, 1f } };
            }
            Dictionary<int, float> made = new Dictionary<int, float>();
            foreach (KeyValuePair<int, float> w in mixed)
            {
                made[w.Key] = w.Value / whole;
            }
            return made;
        }

        // The hair's own triangles (compacted) with a wall between each two neighbouring columns that both found hair
        // and whose tops are near each other. Each wall faces the open side, away from the crown along the skin (toward
        // the face or the nape), where it is seen from, and is lit as facing that way: one face, as two coplanar faces
        // wound either way flickered against each other in thin stripes.
        private Piece Walled(Piece hair, int hairEnd, List<List<Column>> columns, Piece body, Vector3 crown)
        {
            // Which way round the file's triangles face out: as their winding, or (turned round as the reader may
            // turn hair) against it, by the hair's own normals.
            float sign = 1f;
            if (hair.normal != null)
            {
                float agree = 0f;
                for (int k = 0; k + 2 < hairEnd; k += 3)
                {
                    Vector3 a = hair.rest[hair.tris[k]], b = hair.rest[hair.tris[k + 1]], c = hair.rest[hair.tris[k + 2]];
                    agree += Vector3.Dot(Vector3.Cross(b - a, c - a), hair.normal[hair.tris[k]] + hair.normal[hair.tris[k + 1]] + hair.normal[hair.tris[k + 2]]);
                }
                sign = agree < 0f ? -1f : 1f;
            }
            Dictionary<int, int> map = new Dictionary<int, int>();
            List<int> from = new List<int>();
            List<int> tris = new List<int>();
            for (int k = 0; k < hairEnd; k++)
            {
                int o = hair.tris[k], i;
                if (!map.TryGetValue(o, out i))
                {
                    map[o] = i = from.Count;
                    from.Add(o);
                }
                tris.Add(i);
            }
            int hairVerts = from.Count;
            List<Vector3> places = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Vector3> normals = new List<Vector3>();
            List<Dictionary<int, float>> weights = new List<Dictionary<int, float>>();
            Dictionary<string, List<Vector3>> keys = new Dictionary<string, List<Vector3>>();
            foreach (string name in hair.keys.Keys)
            {
                keys[name] = new List<Vector3>();
            }
            int wallsFrom = tris.Count / 3;
            // A column's two corners are shared by the quads either side of it when both give it the same texture place;
            // where they don't (WallBlendPixels), the next quad gets corners of its own.
            Action<Vector3, Vector2, Vector3, Func<string, Vector3>, Dictionary<int, float>> add = (at, uv, normal, move, bones) =>
            {
                places.Add(at);
                uvs.Add(uv);
                normals.Add(normal);
                weights.Add(bones);
                foreach (KeyValuePair<string, List<Vector3>> key in keys)
                {
                    key.Value.Add(move(key.Key));
                }
            };
            foreach (List<Column> line in columns)
            {
                int n = line.Count;
                Vector3[] facing = new Vector3[n], footAt = new Vector3[n];
                Vector2[] meets = new Vector2[n];
                Func<string, Vector3>[] footMove = new Func<string, Vector3>[n], topMove = new Func<string, Vector3>[n];
                Dictionary<int, float>[] footBones = new Dictionary<int, float>[n], topBones = new Dictionary<int, float>[n];
                int[] madeAt = new int[n];      // each column's last foot corner (its top the next), -1 none yet
                Vector2[] madeUv = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    madeAt[i] = -1;
                    Column c = line[i];
                    if (!c.found)
                    {
                        continue;
                    }
                    int ta = hair.tris[c.tri], tb = hair.tris[c.tri + 1], tc = hair.tris[c.tri + 2];
                    float wa = 1f - c.u - c.v, wb = c.u, wc = c.v;
                    meets[i] = hair.uvs[ta] * wa + hair.uvs[tb] * wb + hair.uvs[tc] * wc;
                    // Away from the crown along the skin.
                    Vector3 f = c.foot.at - crown;
                    f -= c.foot.normal * Vector3.Dot(f, c.foot.normal);
                    facing[i] = f.sqrMagnitude > 1e-10f ? f.normalized : Vector3.down;
                    footAt[i] = c.foot.at - c.foot.normal * WallSink;
                    // The foot moves with the body's keys there, the top with the hair's.
                    SkinSpot s = c.foot;
                    footMove[i] = name =>
                    {
                        Vector3[] d;
                        return body.keys.TryGetValue(name, out d) ? d[s.a] * s.wa + d[s.b] * s.wb + d[s.c] * s.wc : Vector3.zero;
                    };
                    topMove[i] = name =>
                    {
                        Vector3[] d = hair.keys[name];
                        return d[ta] * wa + d[tb] * wb + d[tc] * wc;
                    };
                    // And the same for the bones: the foot rides the skin's, the top the hair's (the head bone). Left
                    // with none, a wall was drawn at its rest place on the graphics card (and at the figure's origin on
                    // the CPU), so it hung in the air where the standing head would be while the pawn lay or sat.
                    footBones[i] = BlendedWeights(body, s.a, s.wa, s.b, s.wb, s.c, s.wc);
                    topBones[i] = BlendedWeights(hair, ta, wa, tb, wb, tc, wc);
                }
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    if (j == i || !line[i].found || !line[j].found || !Joins(hair, hairEnd, line[i].top, line[j].top))
                    {
                        continue;
                    }
                    // Blended between the two places only if they are near on the atlas: two columns meeting the hair on
                    // different sprite panels, or either side of a gap between locks, slid the texture across the gaps
                    // between the art, cut out in stripes.
                    Vector2 uvI = meets[i], uvJ = meets[j];
                    if (WallOneColour && hair.wallUv.HasValue)
                    {
                        uvI = uvJ = hair.wallUv.Value;
                    }
                    else
                    {
                        Vector2 apart = new Vector2((uvJ.x - uvI.x) * HairBuilder.Panels * HairBuilder.Size, (uvJ.y - uvI.y) * HairBuilder.Size);
                        if (apart.magnitude > WallBlendPixels)
                        {
                            uvJ = uvI;
                        }
                    }
                    Vector3 facingIJ = (facing[i] + facing[j]).normalized;
                    // Lit as its column faces, so neighbouring quads sharing it shade on smoothly.
                    Func<int, Vector2, int> corners = (col, uv) =>
                    {
                        if (madeAt[col] < 0 || madeUv[col] != uv)
                        {
                            madeAt[col] = hairVerts + places.Count;
                            madeUv[col] = uv;
                            add(footAt[col], uv, facing[col], footMove[col], footBones[col]);
                            add(line[col].top, uv, facing[col], topMove[col], topBones[col]);
                        }
                        return madeAt[col];
                    };
                    int fi = corners(i, uvI), fj = corners(j, uvJ), ti = fi + 1, tj = fj + 1;
                    bool turned = Vector3.Dot(Vector3.Cross(footAt[j] - footAt[i], line[j].top - footAt[i]) * sign, facingIJ) < 0f;
                    tris.AddRange(turned
                        ? new[] { fi, tj, fj, fi, ti, tj }
                        : new[] { fi, fj, tj, fi, tj, ti });
                }
            }
            int count = hairVerts + places.Count;
            Piece p = new Piece { name = hair.name, layer = hair.layer, groups = hair.groups, defs = hair.defs, hides = hair.hides, wallUv = hair.wallUv };
            p.tris = tris.ToArray();
            p.wallsFrom = tris.Count > wallsFrom * 3 ? wallsFrom : -1;
            p.rest = new Vector3[count];
            p.uvs = new Vector2[count];
            p.normal = hair.normal != null ? new Vector3[count] : null;
            List<int> first = new List<int>(), bone = new List<int>();
            List<float> weight = new List<float>();
            for (int i = 0; i < count; i++)
            {
                first.Add(bone.Count);
                if (i < hairVerts)
                {
                    int o = from[i];
                    p.rest[i] = hair.rest[o];
                    p.uvs[i] = hair.uvs[o];
                    if (p.normal != null)
                    {
                        p.normal[i] = hair.normal[o];
                    }
                    if (hair.firstWeight != null)
                    {
                        for (int k = hair.firstWeight[o]; k < hair.firstWeight[o + 1]; k++)
                        {
                            bone.Add(hair.weightBone[k]);
                            weight.Add(hair.weight[k]);
                        }
                    }
                }
                else
                {
                    p.rest[i] = places[i - hairVerts];
                    p.uvs[i] = uvs[i - hairVerts];
                    foreach (KeyValuePair<int, float> b in weights[i - hairVerts])
                    {
                        bone.Add(b.Key);
                        weight.Add(b.Value);
                    }
                    if (p.normal != null)
                    {
                        p.normal[i] = normals[i - hairVerts];
                    }
                }
            }
            first.Add(bone.Count);
            p.firstWeight = first.ToArray();
            p.weightBone = bone.ToArray();
            p.weight = weight.ToArray();
            foreach (KeyValuePair<string, Vector3[]> key in hair.keys)
            {
                Vector3[] d = new Vector3[count];
                for (int i = 0; i < hairVerts; i++)
                {
                    d[i] = key.Value[from[i]];
                }
                List<Vector3> added = keys[key.Key];
                for (int i = 0; i < added.Count; i++)
                {
                    d[hairVerts + i] = added[i];
                }
                p.keys[key.Key] = d;
            }
            return p;
        }
    }
}
