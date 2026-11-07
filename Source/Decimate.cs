using System;
using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // Cuts a mesh down to about a number of triangles by collapsing edges, the cheapest first by the quadric error (the
    // squared distance from the planes of the triangles merged into each vertex). Each collapse moves one vertex onto a
    // neighbour and drops it, so every vertex left keeps its own place, texture place, normal and keys from the file:
    // nothing is interpolated, and the caller copies them across by Kept.
    //
    // Vertices split along texture seams (the hair's, where its sprites meet, and its walls) are welded by place for the
    // collapses, and each copy is moved onto the copy of the neighbour on its own side of the seam. The open edges are kept
    // as lines: a vertex on one is only moved along it, never off it or round a corner, and the planes standing up from
    // them add to its error, so the outline holds its shape.
    internal static class Decimate
    {
        private const double FeatureWeight = 100.0;   // how much the planes up from open edges count, of a triangle's
        private const float FlipDot = 0.2f;           // least cosine between a triangle's normal before and after a collapse
        private const float SeamJump = 0.03f;         // texture places further apart than this are on two sides of a seam

        // The vertices left (old indices, in their new order) and the triangles over them; uvs may be null.
        public static void Run(Vector3[] at, int[] tris, Vector2[] uvs, int target, out int[] kept, out int[] newTris)
        {
            new Collapser(at, tris, uvs).Run(target, out kept, out newTris);
        }

        private struct Quadric
        {
            public double a, b, c, d, e, f, g, h, i, j;   // the upper half of a symmetric 4 x 4

            public static Quadric Plane(Vector3 n, float off, double w)
            {
                Quadric q;
                double x = n.x, y = n.y, z = n.z, o = off;
                q.a = w * x * x; q.b = w * x * y; q.c = w * x * z; q.d = w * x * o;
                q.e = w * y * y; q.f = w * y * z; q.g = w * y * o;
                q.h = w * z * z; q.i = w * z * o;
                q.j = w * o * o;
                return q;
            }

            public void Add(Quadric q)
            {
                a += q.a; b += q.b; c += q.c; d += q.d; e += q.e; f += q.f; g += q.g; h += q.h; i += q.i; j += q.j;
            }

            public double Error(Vector3 p)
            {
                double x = p.x, y = p.y, z = p.z;
                return a * x * x + 2 * b * x * y + 2 * c * x * z + 2 * d * x + e * y * y + 2 * f * y * z + 2 * g * y
                    + h * z * z + 2 * i * z + j;
            }
        }

        private sealed class Collapser
        {
            private readonly Vector3[] at;
            private readonly int[] tris;     // corner indices, changed as vertices move
            private readonly Vector2[] uvs;
            private readonly int[] weld;     // each vertex's welded vertex
            private readonly Vector3[] place;   // each welded vertex's place
            private readonly List<int>[] copies;   // the vertices welded into each
            private readonly List<int>[] around;   // the triangles round each welded vertex (dead ones dropped lazily)
            private readonly bool[] alive, gone;
            private readonly int[] stamp;
            private readonly Quadric[] error;
            private readonly Heap heap = new Heap();
            private readonly HashSet<int> used = new HashSet<int>();   // CanCollapse's: the copies of u in living triangles
            private int living;

            public Collapser(Vector3[] at, int[] tris, Vector2[] uvs)
            {
                this.at = at;
                this.tris = (int[])tris.Clone();
                this.uvs = uvs;
                weld = new int[at.Length];
                Dictionary<Vector3, int> byPlace = new Dictionary<Vector3, int>();
                List<Vector3> places = new List<Vector3>();
                for (int v = 0; v < at.Length; v++)
                {
                    int w;
                    if (!byPlace.TryGetValue(at[v], out w))
                    {
                        w = places.Count;
                        byPlace[at[v]] = w;
                        places.Add(at[v]);
                    }
                    weld[v] = w;
                }
                place = places.ToArray();
                int n = place.Length;
                copies = new List<int>[n];
                around = new List<int>[n];
                for (int w = 0; w < n; w++)
                {
                    copies[w] = new List<int>(1);
                    around[w] = new List<int>(6);
                }
                for (int v = 0; v < at.Length; v++)
                {
                    copies[weld[v]].Add(v);
                }
                int count = tris.Length / 3;
                alive = new bool[count];
                gone = new bool[n];
                stamp = new int[n];
                error = new Quadric[n];
                for (int t = 0; t < count; t++)
                {
                    int a = W(t, 0), b = W(t, 1), c = W(t, 2);
                    if (a == b || b == c || a == c)
                    {
                        continue;   // already degenerate: dropped
                    }
                    alive[t] = true;
                    living++;
                    around[a].Add(t);
                    around[b].Add(t);
                    around[c].Add(t);
                }
                for (int t = 0; t < count; t++)
                {
                    if (!alive[t])
                    {
                        continue;
                    }
                    Vector3 cross = Vector3.Cross(place[W(t, 1)] - place[W(t, 0)], place[W(t, 2)] - place[W(t, 0)]);
                    float area = cross.magnitude;
                    if (area < 1e-12f)
                    {
                        continue;
                    }
                    Vector3 normal = cross / area;
                    Quadric q = Quadric.Plane(normal, -Vector3.Dot(normal, place[W(t, 0)]), area / 2f);
                    for (int k = 0; k < 3; k++)
                    {
                        int u = W(t, k), v = W(t, (k + 1) % 3);
                        error[u].Add(q);
                        if (Feature(u, v))
                        {
                            Vector3 edge = place[v] - place[u];
                            Vector3 up = Vector3.Cross(edge, normal).normalized;
                            Quadric side = Quadric.Plane(up, -Vector3.Dot(up, place[u]), FeatureWeight * edge.sqrMagnitude);
                            error[u].Add(side);
                            error[v].Add(side);
                        }
                    }
                }
                for (int w = 0; w < n; w++)
                {
                    foreach (int v in Neighbours(w))
                    {
                        Push(w, v);
                    }
                }
            }

            private int W(int t, int k)
            {
                return weld[tris[3 * t + k]];
            }

            // The living triangles round both u and v.
            private List<int> Between(int u, int v)
            {
                List<int> both = new List<int>(2);
                foreach (int t in around[u])
                {
                    if (alive[t] && (W(t, 0) == v || W(t, 1) == v || W(t, 2) == v))
                    {
                        both.Add(t);
                    }
                }
                return both;
            }

            // The vertex of triangle t that is welded into w.
            private int CornerOf(int t, int w)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (W(t, k) == w)
                    {
                        return tris[3 * t + k];
                    }
                }
                return -1;
            }

            // An open edge (one triangle), or one of more than two: kept as lines.
            private bool Feature(int u, int v)
            {
                return Between(u, v).Count != 2;
            }

            private HashSet<int> Neighbours(int w)
            {
                HashSet<int> n = new HashSet<int>();
                foreach (int t in around[w])
                {
                    if (!alive[t])
                    {
                        continue;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        int o = W(t, k);
                        if (o != w)
                        {
                            n.Add(o);
                        }
                    }
                }
                return n;
            }

            private void Push(int u, int v)
            {
                Quadric q = error[u];
                q.Add(error[v]);
                heap.Push(new Heap.Item { cost = q.Error(place[v]), u = u, v = v, su = stamp[u], sv = stamp[v] });
            }

            // Whether u can be moved onto v without tearing, folding or flipping the mesh; and if so, for each of u's
            // copies, the copy of v it becomes.
            private bool CanCollapse(int u, int v, Dictionary<int, int> into)
            {
                HashSet<int> nu = Neighbours(u), nv = Neighbours(v);
                List<int> both = Between(u, v);
                // The link condition: u and v share no neighbours but the far corners of the triangles on their edge.
                HashSet<int> far = new HashSet<int>();
                foreach (int t in both)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int o = W(t, k);
                        if (o != u && o != v)
                        {
                            far.Add(o);
                        }
                    }
                }
                foreach (int o in nu)
                {
                    if (nv.Contains(o) && !far.Contains(o))
                    {
                        return false;
                    }
                }
                // On an open edge or a seam: only along it, and only where it runs straight on (two such edges).
                int features = 0;
                foreach (int o in nu)
                {
                    if (Feature(u, o))
                    {
                        features++;
                    }
                }
                if (features > 0 && (features != 2 || !Feature(u, v)))
                {
                    return false;
                }
                // No triangle left round u turned over or squashed flat.
                foreach (int t in around[u])
                {
                    if (!alive[t] || both.Contains(t))
                    {
                        continue;
                    }
                    Vector3 p0 = place[W(t, 0)], p1 = place[W(t, 1)], p2 = place[W(t, 2)];
                    Vector3 before = Vector3.Cross(p1 - p0, p2 - p0);
                    if (W(t, 0) == u) p0 = place[v];
                    if (W(t, 1) == u) p1 = place[v];
                    if (W(t, 2) == u) p2 = place[v];
                    Vector3 after = Vector3.Cross(p1 - p0, p2 - p0);
                    if (after.sqrMagnitude < 1e-14f || Vector3.Dot(before.normalized, after.normalized) < FlipDot)
                    {
                        return false;
                    }
                }
                // Each copy of u to the copy of v it shares a triangle with, else the one nearest it in the texture if that
                // is near enough to be on the same side of the seam (the walls: a quad to each step, each of one colour).
                // A sprite's texture places are its projection of the places, so a copy moved onto v's copy from the same
                // sprite keeps its texture true.
                into.Clear();
                used.Clear();
                foreach (int t in around[u])
                {
                    if (!alive[t])
                    {
                        continue;
                    }
                    int cu = CornerOf(t, u), cv = CornerOf(t, v);
                    used.Add(cu);
                    if (cv >= 0)
                    {
                        int had;
                        if (into.TryGetValue(cu, out had) && had != cv)
                        {
                            return false;   // a copy of u meets v on two sides of a seam: leave it
                        }
                        into[cu] = cv;
                    }
                }
                foreach (int cu in used)
                {
                    if (into.ContainsKey(cu))
                    {
                        continue;
                    }
                    int best = copies[v][0];
                    if (uvs != null)
                    {
                        float near = float.MaxValue;
                        foreach (int cv in copies[v])
                        {
                            float d = (uvs[cv] - uvs[cu]).sqrMagnitude;
                            if (d < near)
                            {
                                near = d;
                                best = cv;
                            }
                        }
                    }
                    if (uvs != null && (uvs[best] - uvs[cu]).sqrMagnitude > SeamJump * SeamJump)
                    {
                        return false;   // v isn't on this copy's sprite
                    }
                    into[cu] = best;
                }
                return true;
            }

            public void Run(int target, out int[] kept, out int[] newTris)
            {
                Dictionary<int, int> into = new Dictionary<int, int>();
                while (living > target && heap.Count > 0)
                {
                    Heap.Item it = heap.Pop();
                    int u = it.u, v = it.v;
                    if (gone[u] || gone[v] || stamp[u] != it.su || stamp[v] != it.sv || !CanCollapse(u, v, into))
                    {
                        continue;
                    }
                    foreach (int t in around[u])
                    {
                        if (!alive[t])
                        {
                            continue;
                        }
                        if (W(t, 0) == v || W(t, 1) == v || W(t, 2) == v)
                        {
                            alive[t] = false;
                            living--;
                            continue;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            if (W(t, k) == u)
                            {
                                tris[3 * t + k] = into[tris[3 * t + k]];
                            }
                        }
                        around[v].Add(t);
                    }
                    gone[u] = true;
                    error[v].Add(error[u]);
                    stamp[v]++;
                    around[u].Clear();
                    around[v].RemoveAll(t => !alive[t]);
                    foreach (int o in Neighbours(v))
                    {
                        Push(v, o);
                        Push(o, v);
                    }
                }
                // The vertices still used, in their old order.
                int[] newIndex = new int[at.Length];
                for (int i = 0; i < newIndex.Length; i++)
                {
                    newIndex[i] = -1;
                }
                List<int> left = new List<int>();
                for (int t = 0; t < alive.Length; t++)
                {
                    if (alive[t])
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            newIndex[tris[3 * t + k]] = 0;
                        }
                    }
                }
                for (int i = 0; i < newIndex.Length; i++)
                {
                    if (newIndex[i] == 0)
                    {
                        newIndex[i] = left.Count;
                        left.Add(i);
                    }
                }
                List<int> faces = new List<int>(living * 3);
                for (int t = 0; t < alive.Length; t++)
                {
                    if (alive[t])
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            faces.Add(newIndex[tris[3 * t + k]]);
                        }
                    }
                }
                kept = left.ToArray();
                newTris = faces.ToArray();
            }
        }

        // A binary min-heap of collapses by cost (stale ones are skipped as they come off it).
        private sealed class Heap
        {
            public struct Item
            {
                public double cost;
                public int u, v, su, sv;
            }

            private readonly List<Item> items = new List<Item>();

            public int Count
            {
                get { return items.Count; }
            }

            public void Push(Item it)
            {
                items.Add(it);
                int i = items.Count - 1;
                while (i > 0)
                {
                    int up = (i - 1) / 2;
                    if (items[up].cost <= it.cost)
                    {
                        break;
                    }
                    items[i] = items[up];
                    i = up;
                }
                items[i] = it;
            }

            public Item Pop()
            {
                Item top = items[0], last = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                int n = items.Count, i = 0;
                if (n == 0)
                {
                    return top;
                }
                while (true)
                {
                    int l = 2 * i + 1;
                    if (l >= n)
                    {
                        break;
                    }
                    int c = l + 1 < n && items[l + 1].cost < items[l].cost ? l + 1 : l;
                    if (items[c].cost >= last.cost)
                    {
                        break;
                    }
                    items[i] = items[c];
                    i = c;
                }
                items[i] = last;
                return top;
            }
        }
    }
}
