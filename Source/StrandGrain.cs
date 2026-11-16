using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The hair's strand streaks (HairBuilder.Streaked) on what else is drawn in the hair's colour with no image of its own:
    // the gene ears, the cat tail and the Furskin gene's tufts, and with that gene the whole body and head (GrainBody). Each is given texture places running along its strands
    // (StrandUvs*) on one shared grey texture of the same streaks (StrandTexture), drawn with the hair's textured material
    // (TexturedMaterial), so they come out with the same grain rather than as flat colour beside the hair.
    public sealed partial class SkinnedFigure
    {
        private const int StrandTexSize = 240;    // pixels: the streak texture is this square and tiles both ways (a whole number of each streak's widths and lengths)
        private const int HeadSpritePixels = 91;  // sprite pixels (at HairBuilder.Size) across the drawn-over head, which the hair's streaks are laid at: the vanilla average head's
        private static Texture2D strandTexture;

        // The streaks as HairBuilder lays them over the hair's sprites, made to tile, in grey: 1 + StrandStreak * streak,
        // scaled so the brightest is white (the material multiplies the vertex colour by it, so the mean comes out a little
        // darker than the flat colour was, about as the hair's sprites are darker than its colour).
        private static Texture2D StrandTexture()
        {
            if (strandTexture != null)
            {
                return strandTexture;
            }
            int n = StrandTexSize;
            Color32[] px = new Color32[n * n];
            float top = 1f + HairBuilder.StrandStreak;
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    float s = 0.6f * TiledStreak(0, c, r, 1, HairBuilder.StreakLength) + 0.4f * TiledStreak(3, c, r, 3, 2 * HairBuilder.StreakLength);
                    byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(255f * (1f + HairBuilder.StrandStreak * s) / top), 0, 255);
                    px[r * n + c] = new Color32(g, g, g, 255);
                }
            }
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, true);
            t.name = "Marus_Strands";
            t.wrapMode = TextureWrapMode.Repeat;
            t.filterMode = FilterMode.Trilinear;
            t.SetPixels32(px);
            t.Apply(true, true);
            strandTexture = t;
            return t;
        }

        // HairBuilder.Streak1 with its lattice wrapped, so the texture tiles: columns across (width pixels a cell), rows down
        // (length pixels a cell).
        private static float TiledStreak(int seed, int c, int r, int width, int length)
        {
            int px = StrandTexSize / width, py = StrandTexSize / length;
            float x = c / (float)width, y = r / (float)length;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            int xa = x0 % px, xb = (x0 + 1) % px, ya = y0 % py, yb = (y0 + 1) % py;
            float a = Mathf.Lerp(HairBuilder.Noise(seed, xa, ya), HairBuilder.Noise(seed, xb, ya), fx);
            float b = Mathf.Lerp(HairBuilder.Noise(seed, xa, yb), HairBuilder.Noise(seed, xb, yb), fx);
            return Mathf.Lerp(a, b, fy);
        }

        // Texture places per cell, so the streaks are as wide on these as on the hair: the hair's sprite pixels per cell
        // (the drawn-over head's width in pixels over the chibi's head's) over the texture's size.
        private float StrandScale()
        {
            Shape b = BaseShape();
            MeasureHead(b);
            float width = Mathf.Max(b.headHigh.x - b.headLow.x, 0.05f);
            return HeadSpritePixels / width / StrandTexSize;
        }

        // Gives a piece the streak texture (its places already set): drawn with the hair's material from then on.
        private static void Grain(Piece piece)
        {
            if (piece.uvs == null)
            {
                return;
            }
            piece.texture = StrandTexture();
            piece.mean = Color.white;   // its outline stays in its own colour
        }

        // The Furskin gene's tufts (FurPiece) grained, unless the export gives them an image of their own; and the body given
        // strand places (GrainBody). Without the grain if that fails: it is only a look.
        private void GrainFur()
        {
            int at = FurPiece;
            if (at < 0)
            {
                return;
            }
            Piece fur = pieces[at];
            if (fur.texture == null && CoatPiece >= 0)
            {
                try
                {
                    CoatTufts(fur);   // on the coat's image, from the skin under each (FurCoat.cs)
                }
                catch (Exception e)
                {
                    fur.uvs = null;
                    fur.shade = null;
                    fur.texture = null;
                    Log.Warning(Tag + "could not lay the fur's tufts on the coat, so they are grained: " + e.Message);
                }
            }
            if (fur.texture == null)
            {
                try
                {
                    fur.uvs = StrandUvsTufts(fur.rest, fur.tris);
                    Grain(fur);
                }
                catch (Exception e)
                {
                    Log.Warning(Tag + "could not lay the strand grain on the fur, so it is drawn plain: " + e.Message);
                }
            }
            try
            {
                GrainBody();
            }
            catch (Exception e)
            {
                pieces[0].uvs = null;
                Log.Warning(Tag + "could not lay the strand grain on the body, so a furred one is drawn plain: " + e.Message);
            }
        }

        // The body's strand places, for a pawn with the Furskin gene (DrawBody's grained): each vertex goes with the chain of
        // bones that carries most of its weight (the trunk and head, each arm with its shoulder, each leg), and is placed
        // on it as the tail's tube is (StrandUvsTube: along the chain, round it mirrored, so no seam), so the streaks run
        // down the trunk and head and along the limbs. Where a triangle's corners go with different chains its places jump,
        // which the texture's mip levels draw as flat colour, a thin line at the shoulders and hips.
        private void GrainBody()
        {
            Piece body = pieces[0];
            string[] names = new string[boneHead.Length];
            foreach (KeyValuePair<string, int> pair in boneIndex)
            {
                names[pair.Value] = pair.Key;
            }
            float top = float.MinValue;
            foreach (Vector3 v in body.rest)
            {
                top = Mathf.Max(top, v.y);
            }
            List<Vector3[]> chains = new List<Vector3[]>();
            int[] chainOf = new int[boneHead.Length];
            Vector3[] trunk = ChainOf("spine", "spine.001", "spine.002", "spine.003", "spine.004", "spine.005", "spine.006");
            if (trunk == null)
            {
                throw new InvalidDataException("no spine bones");
            }
            Vector3 crown = trunk[trunk.Length - 1];
            crown.y = Mathf.Max(top, crown.y + 0.05f);
            chains.Add(Extended(trunk, crown));
            foreach (string side in new[] { "L", "R" })
            {
                Vector3[] arm = ChainOf("upper_arm." + side, "forearm." + side, "hand." + side, "hand." + side + ".001", "hand." + side + ".002");
                Vector3[] leg = ChainOf("thigh." + side, "shin." + side, "foot." + side, "toe." + side);
                int armAt = -1, legAt = -1;
                if (arm != null)
                {
                    armAt = chains.Count;
                    chains.Add(Extended(arm, Past(arm, 0.04f)));
                }
                if (leg != null)
                {
                    legAt = chains.Count;
                    chains.Add(Extended(leg, Past(leg, 0.04f)));
                }
                for (int b = 0; b < names.Length; b++)
                {
                    string n = names[b];
                    if (n == null || !n.Contains("." + side))
                    {
                        continue;
                    }
                    if (armAt >= 0 && (n.StartsWith("upper_arm") || n.StartsWith("forearm") || n.StartsWith("hand") || n.StartsWith("thumb") || n.StartsWith("shoulder")))
                    {
                        chainOf[b] = armAt;
                    }
                    else if (legAt >= 0 && (n.StartsWith("thigh") || n.StartsWith("shin") || n.StartsWith("foot") || n.StartsWith("toe")))
                    {
                        chainOf[b] = legAt;
                    }
                }
            }
            float scale = StrandScale();
            Vector3[][] sides = new Vector3[chains.Count][];
            for (int c = 0; c < chains.Count; c++)
            {
                sides[c] = ChainSides(chains[c]);
            }
            float[] share = new float[chains.Count];
            Vector2[] uvs = new Vector2[body.rest.Length];
            for (int i = 0; i < body.rest.Length; i++)
            {
                Array.Clear(share, 0, share.Length);
                for (int k = body.firstWeight[i]; k < body.firstWeight[i + 1]; k++)
                {
                    int b = body.weightBone[k];
                    share[b >= 0 && b < chainOf.Length ? chainOf[b] : 0] += body.weight[k];
                }
                int best = 0;
                for (int c = 1; c < share.Length; c++)
                {
                    if (share[c] > share[best])
                    {
                        best = c;
                    }
                }
                Vector3[] joints = chains[best];
                float along;
                Vector3 off;
                int seg;
                OnChain(body.rest[i], joints, out along, out off, out seg);
                Vector3 d = (joints[seg + 1] - joints[seg]).normalized, side = sides[best][seg], up = Vector3.Cross(side, d);
                float round = off.magnitude * Mathf.Atan2(Vector3.Dot(off, side), Mathf.Abs(Vector3.Dot(off, up)));
                uvs[i] = new Vector2(round * scale + 0.37f * best, along * scale);
            }
            body.uvs = uvs;
        }

        // The rest heads of these bones in order, or null if the export lacks any.
        private Vector3[] ChainOf(params string[] bones)
        {
            Vector3[] joints = new Vector3[bones.Length];
            for (int k = 0; k < bones.Length; k++)
            {
                int b;
                if (!boneIndex.TryGetValue(bones[k], out b) || b >= boneHead.Length)
                {
                    return null;
                }
                joints[k] = boneHead[b];
            }
            return joints;
        }

        // A chain with one more joint at its end (the last bone's own end, which the bone heads don't give).
        private static Vector3[] Extended(Vector3[] joints, Vector3 end)
        {
            Vector3[] more = new Vector3[joints.Length + 1];
            Array.Copy(joints, more, joints.Length);
            more[joints.Length] = end;
            return more;
        }

        // A point past a chain's last joint, by cells, on along its last segment.
        private static Vector3 Past(Vector3[] joints, float by)
        {
            Vector3 a = joints[joints.Length - 2], b = joints[joints.Length - 1];
            return b + (b - a).normalized * by;
        }

        // The pieces of one mesh joined to nothing else (the tufts), each a list of its vertices, by one of them.
        private static Dictionary<int, List<int>> Tufts(Vector3[] rest, int[] tris)
        {
            int n = rest.Length;
            int[] root = new int[n];
            for (int i = 0; i < n; i++)
            {
                root[i] = i;
            }
            Func<int, int> find = null;
            find = i =>
            {
                while (root[i] != i)
                {
                    root[i] = root[root[i]];
                    i = root[i];
                }
                return i;
            };
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = find(tris[t]), b = find(tris[t + 1]), c = find(tris[t + 2]);
                root[b] = a;
                root[find(c)] = a;
            }
            Dictionary<int, List<int>> groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                List<int> g;
                if (!groups.TryGetValue(r, out g))
                {
                    g = new List<int>();
                    groups[r] = g;
                }
                g.Add(i);
            }
            return groups;
        }

        private Piece bodyGrain;   // holds the streak texture and its materials for the body (BodyMaterial); not drawn itself

        // The body's material: the hair's textured one on the streak texture for a furred pawn (grained, the body's places
        // laid by GrainBody), its light pass a part of m's own, else the plain solid one. The light pass part is kept once
        // added, with no material while the body is drawn plain (DrawParts skips it), so the parts' places stay put.
        private Material BodyMaterial(PieceMeshes m, bool grained)
        {
            Material t = null;
            if (grained && pieces[0].uvs != null)
            {
                if (bodyGrain == null)
                {
                    bodyGrain = new Piece { name = "Body_Grain", texture = StrandTexture() };
                }
                t = TexturedMaterial(bodyGrain);
            }
            if (t != null && m.grainMarkAt < 0 && bodyGrain.mark != null)
            {
                m.grainMarkAt = m.parts.Count;
                m.parts.Add(new MeshPart { mesh = m.solid, material = bodyGrain.mark, outline = false });
            }
            if (m.grainMarkAt >= 0)
            {
                m.parts[m.grainMarkAt].material = t != null ? bodyGrain.mark : null;
            }
            return t ?? SolidMaterial();
        }

        // The textured material for a grained piece, its light pass added to m's parts once; else the plain solid material
        // (no grain, or IsoRim's materials not made yet).
        private static Material GrainedMaterial(Piece piece, PieceMeshes m)
        {
            Material t = piece.texture != null ? TexturedMaterial(piece) : null;
            if (t == null)
            {
                return SolidMaterial();
            }
            if (!m.textureMarked && piece.mark != null)
            {
                // The same mesh again in its light pass, drawn with the same matrix by DrawParts.
                m.parts.Add(new MeshPart { mesh = m.solid, material = piece.mark, outline = false });
                m.textureMarked = true;
            }
            return t;
        }

        // A point's nearest place on a chain of joints: how far along it that is (cells), the offset from there to the point,
        // and the segment it is on.
        private static void OnChain(Vector3 p, Vector3[] joints, out float along, out Vector3 offset, out int segment)
        {
            along = 0f;
            offset = p - joints[0];
            segment = 0;
            float best = float.MaxValue, run = 0f;
            for (int j = 0; j + 1 < joints.Length; j++)
            {
                Vector3 a = joints[j], d = joints[j + 1] - a;
                float len = d.magnitude;
                float t = len > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, d) / (len * len)) : 0f;
                Vector3 q = a + d * t;
                float dist = (p - q).sqrMagnitude;
                if (dist < best)
                {
                    best = dist;
                    along = run + t * len;
                    offset = p - q;
                    segment = j;
                }
                run += len;
            }
        }

        // Sideways ways for each segment of a chain, carried down it with the least turn, so the round of a tube doesn't twist
        // from one segment to the next.
        private static Vector3[] ChainSides(Vector3[] joints)
        {
            Vector3[] sides = new Vector3[Mathf.Max(joints.Length - 1, 1)];
            Vector3 side = Vector3.zero;
            for (int j = 0; j + 1 < joints.Length; j++)
            {
                Vector3 d = (joints[j + 1] - joints[j]).normalized;
                if (j == 0)
                {
                    Vector3 up = Mathf.Abs(Vector3.Dot(d, Vector3.up)) < 0.9f ? Vector3.up : Vector3.forward;
                    side = Vector3.Cross(d, up).normalized;
                }
                else
                {
                    side = (side - Vector3.Dot(side, d) * d).normalized;
                }
                sides[j] = side;
            }
            return sides;
        }

        // A tube along a chain (the tail): the strands run along it, and round it each point's place is its arc from the top
        // or the bottom, whichever is nearer (the two halves mirrored), so there is no seam where the round would close.
        private Vector2[] StrandUvsTube(Vector3[] rest, Vector3[] joints)
        {
            float scale = StrandScale();
            Vector3[] sides = ChainSides(joints);
            Vector2[] uvs = new Vector2[rest.Length];
            for (int i = 0; i < rest.Length; i++)
            {
                float along;
                Vector3 off;
                int seg;
                OnChain(rest[i], joints, out along, out off, out seg);
                Vector3 d = (joints[seg + 1] - joints[seg]).normalized, side = sides[seg], up = Vector3.Cross(side, d);
                float r = off.magnitude;
                float round = r * Mathf.Atan2(Vector3.Dot(off, side), Mathf.Abs(Vector3.Dot(off, up)));
                uvs[i] = new Vector2(round * scale, along * scale);
            }
            return uvs;
        }

        // A flat sheet along a chain (an ear): along it, and across by the offset along the sheet's width (its offsets' widest
        // way off the chain), the front and back alike.
        private Vector2[] StrandUvsSheet(Vector3[] rest, Vector3[] joints, IList<int> vertices, Vector2[] uvs, float shift)
        {
            float scale = StrandScale();
            float[] along = new float[vertices.Count];
            Vector3[] offs = new Vector3[vertices.Count];
            int[] segs = new int[vertices.Count];
            for (int k = 0; k < vertices.Count; k++)
            {
                OnChain(rest[vertices[k]], joints, out along[k], out offs[k], out segs[k]);
            }
            Vector3 width = WidestWay(offs);
            for (int k = 0; k < vertices.Count; k++)
            {
                Vector3 d = (joints[segs[k] + 1] - joints[segs[k]]).normalized;
                Vector3 w = width - Vector3.Dot(width, d) * d;
                w = w.sqrMagnitude > 1e-8f ? w.normalized : width;
                uvs[vertices[k]] = new Vector2(Vector3.Dot(offs[k], w) * scale + shift, along[k] * scale);
            }
            return uvs;
        }

        // The way the offsets spread most (the largest eigenvector of their spread, by power iteration).
        private static Vector3 WidestWay(Vector3[] offs)
        {
            float xx = 0f, xy = 0f, xz = 0f, yy = 0f, yz = 0f, zz = 0f;
            foreach (Vector3 o in offs)
            {
                xx += o.x * o.x; xy += o.x * o.y; xz += o.x * o.z;
                yy += o.y * o.y; yz += o.y * o.z; zz += o.z * o.z;
            }
            Vector3 v = new Vector3(1f, 0.3f, 0.1f);
            for (int it = 0; it < 30; it++)
            {
                Vector3 n = new Vector3(xx * v.x + xy * v.y + xz * v.z, xy * v.x + yy * v.y + yz * v.z, xz * v.x + yz * v.y + zz * v.z);
                if (n.sqrMagnitude < 1e-12f)
                {
                    return Vector3.right;
                }
                v = n.normalized;
            }
            return v;
        }

        // Separate little pieces (the Furskin gene's tufts): each its own strands, from its middle out to its point (the
        // corner farthest from its middle), across by the widest way off that line, shifted by a hash of the piece so
        // neighbours differ.
        private Vector2[] StrandUvsTufts(Vector3[] rest, int[] tris)
        {
            float scale = StrandScale();
            int n = rest.Length;
            Dictionary<int, List<int>> groups = Tufts(rest, tris);
            Vector2[] uvs = new Vector2[n];
            foreach (KeyValuePair<int, List<int>> kv in groups)
            {
                List<int> g = kv.Value;
                Vector3 mid = Vector3.zero;
                foreach (int i in g)
                {
                    mid += rest[i];
                }
                mid /= g.Count;
                int tip = g[0];
                foreach (int i in g)
                {
                    if ((rest[i] - mid).sqrMagnitude > (rest[tip] - mid).sqrMagnitude)
                    {
                        tip = i;
                    }
                }
                Vector3 axis = (rest[tip] - mid).normalized;
                Vector3[] offs = new Vector3[g.Count];
                for (int k = 0; k < g.Count; k++)
                {
                    Vector3 o = rest[g[k]] - mid;
                    offs[k] = o - Vector3.Dot(o, axis) * axis;
                }
                Vector3 across = WidestWay(offs);
                float shift = (HairBuilder.Noise(7, kv.Key, 0) + 1f) * 0.5f;
                foreach (int i in g)
                {
                    Vector3 o = rest[i] - mid;
                    uvs[i] = new Vector2(Vector3.Dot(o, across) * scale + shift, Vector3.Dot(o, axis) * scale + shift);
                }
            }
            return uvs;
        }
    }
}
