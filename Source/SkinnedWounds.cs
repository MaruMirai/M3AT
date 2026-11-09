using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;

namespace M3AT
{
    // Wounds laid on the skinned body (Wounds.cs says which, where and how big). Each is a square of the wound sheet laid
    // round a spot on the skin of its bones: the body's own triangles near the spot that face its way, with texture
    // places projected from straight out over the spot at rest, lifted WoundLift off the skin and moved with it every
    // frame, so the wound bends as the skin under it does. All of a pawn's wounds are one mesh, made again only when they
    // or the clothes over them change. The triangles under the worn garments (HideUnder's) are left out, so on the body a
    // wound shows on bare skin only. One marked onClothes also goes on the skin-layer garments worn, on those of their
    // triangles that follow the skin (SkinOf), as they carry it as the skin under them would; a loose part, such as a
    // tribal skirt, stays clean, with the wound hidden on the skin under it.
    public sealed partial class SkinnedFigure
    {
        private const float WoundLift = 0.004f;   // model units the wounds stand off the skin
        private const float WoundFacing = 0.3f;   // how far a triangle must face the spot's way to carry its wound (cosine)
        // How near the body a garment's triangle must lie at rest to count as following the skin, cells. A garment cut from
        // the body's own faces stands 0.0042 off it (make_from_groups.py's LAYER_OFFSET for OnSkin); the tribal skirt
        // leaves the body at 0.009.
        private const float ClothOnSkin = 0.006f;
        private const float ClothFacing = 0.5f;   // and how far it must face the way of the skin under it (cosine)
        private const int SpotTries = 16;   // picks of a spot within a Decal's scatter before the last is pulled in to it

        // One wound to lay on.
        public struct Decal
        {
            public int seed;         // picks the spot (the same seed and bones: the same spot) and the turn
            public int[] bones;      // the bones whose skin it may be on (BoneNamed)
            public Vector3 aim;      // the place at rest it goes near, if scatter is above 0
            public float scatter;    // how far from aim its middle may be, cells; 0: anywhere on the bones' skin
            public Rect cell;        // its square on the sheet, texture places
            public float span;       // its square's side on the body, model units
            public bool down;        // its top kept up the body as it stands at rest (blood running down); else turned at random
            public bool onClothes;   // also laid on the skin-layer garments worn, where they follow the skin
            public Color32 colour;  // times the sheet; alpha under 255 makes it a scar, this the skin colour (MarusFace.frag.glsl)
        }

        // A pawn's wounds as laid on.
        internal sealed class WoundMeshes
        {
            public Mesh mesh;
            public int[] from;   // the vertex each vertex is made from
            public int[] fromPiece;   // and the piece that one is on: the body or a garment
            public Vector3[] verts;
            public Vector3[] normals;
            public int sign;   // what they were made for (SetWounds)
            public bool tucked;   // and whether under the blanket (BedTuck.cs)
            public GarmentMask mask;
            public bool empty;
            public int posedAt = -1;
            public FaceLayers.EyeWound[] eyeWounds;   // those on the eyes themselves, drawn with the face (Wounds.EyeWoundsOn)
        }

        private int[] triBone;          // the bone that moves each body triangle most
        private float[] triArea;
        private Vector3[] triNormal;    // at rest, base shape
        private Vector3[] restNormal;   // each body vertex's, at rest, base shape
        private readonly Dictionary<GarmentMask, bool[]> hiddenBy = new Dictionary<GarmentMask, bool[]>();   // by mask (HiddenBy)
        private readonly Dictionary<int, int[]> skinOf = new Dictionary<int, int[]>();   // by garment (SkinOf)

        // A bone's number, or -1 if the export has none of that name.
        public int BoneNamed(string name)
        {
            int b;
            return boneIndex.TryGetValue(name, out b) ? b : -1;
        }

        // True if the pawn's wounds were laid on for something else than sign (Wounds.cs: what they are) and mask (the
        // garments worn, HideUnder's), so SetWounds is wanted.
        public bool WoundsChanged(Posed p, int sign, GarmentMask mask)
        {
            return p.wounds == null || p.wounds.sign != sign || p.wounds.mask != mask || p.wounds.tucked != p.tucked;
        }

        // Lays these wounds on the pawn's body in place of the ones it had, the first under the rest, and any marked
        // onClothes on the skin-layer garments in mask (worn[g]: the apparel garment g is worn as) too.
        public void SetWounds(Posed p, List<Decal> decals, int sign, GarmentMask mask, Apparel[] worn)
        {
            WoundMeshes w = p.wounds;
            if (w == null)
            {
                w = new WoundMeshes();
                w.mesh = new Mesh();
                w.mesh.MarkDynamic();
                p.wounds = w;
            }
            w.sign = sign;
            w.mask = mask;
            w.tucked = p.tucked;
            w.posedAt = -1;
            FindTriangles();
            // What each piece may carry: the body's triangles not under a garment, and each skin-layer garment's that follow
            // the skin and aren't under a garment on an outer layer, with the skin under them.
            List<int> carriers = new List<int> { 0 };
            bool[][] hidden = new bool[pieces.Length][];
            int[][] under = new int[pieces.Length][];
            hidden[0] = Hidden(mask);
            if (p.tucked)
            {
                hidden[0] = (bool[])hidden[0].Clone();   // (Hidden's list is kept per mask)
                HideTucked(p, hidden[0]);
            }
            bool anyOnClothes = false;
            foreach (Decal d in decals)
            {
                anyOnClothes |= d.onClothes && !p.tucked;   // tucked in, the clothes under the blanket aren't drawn
            }
            for (int g = 1; anyOnClothes && g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & (GarmentBit(g))) != 0 && worn[g] != null && worn[g].def.apparel.layers.Contains(ApparelLayerDefOf.OnSkin))
                {
                    carriers.Add(g);
                    hidden[g] = GarmentHidden(g, OverOf(g, mask, worn));
                    under[g] = SkinOf(g);
                }
            }
            List<int> from = new List<int>(), fromPiece = new List<int>();
            List<Vector4> uvs = new List<Vector4>();
            List<Color32> colours = new List<Color32>();
            List<int> tris = new List<int>();
            Dictionary<int, int> made = new Dictionary<int, int>();
            bool[] mine = new bool[boneHead.Length], near = new bool[boneHead.Length];
            foreach (Decal d in decals)
            {
                System.Random r = new System.Random(d.seed);
                Vector3 at, n;
                if (!Spot(d.bones, d.aim, d.scatter, r, mine, out at, out n))
                {
                    continue;
                }
                // The square's up: up the body as it stands at rest, else (on a shoulder's top) toward the front; then
                // turned at random about the spot unless it runs down.
                Vector3 up = Vector3.ProjectOnPlane(Vector3.up, n);
                if (up.sqrMagnitude < 0.04f)
                {
                    up = Vector3.ProjectOnPlane(Vector3.forward, n);
                }
                up.Normalize();
                float turn = (float)r.NextDouble() * 360f;
                if (!d.down)
                {
                    up = Quaternion.AngleAxis(turn, n) * up;
                }
                Vector3 right = Vector3.Cross(up, n);
                // Its skin and the bones next to it, so a wound by a joint runs on over it.
                for (int b = 0; b < near.Length; b++)
                {
                    near[b] = mine[b] || (boneParent[b] >= 0 && mine[boneParent[b]]);
                }
                for (int b = 0; b < near.Length; b++)
                {
                    if (mine[b] && boneParent[b] >= 0)
                    {
                        near[boneParent[b]] = true;
                    }
                }
                float reach = d.span * 0.75f;   // a little past the square's corners
                for (int carrier = 0; carrier < (d.onClothes ? carriers.Count : 1); carrier++)
                {
                    int g = carriers[carrier];
                    Piece piece = pieces[g];
                    bool[] off = hidden[g];
                    made.Clear();
                    for (int k = 0; k < off.Length; k++)
                    {
                        // A garment's triangle goes by the skin under it, so it carries what that skin would.
                        int skin = g == 0 ? k : under[g][k];
                        if (off[k] || skin < 0 || !near[triBone[skin]] || Vector3.Dot(triNormal[skin], n) < WoundFacing)
                        {
                            continue;
                        }
                        int a = piece.tris[3 * k], b = piece.tris[3 * k + 1], c = piece.tris[3 * k + 2];
                        if ((ClosestOn(at, piece.rest[a], piece.rest[b], piece.rest[c]) - at).sqrMagnitude > reach * reach)
                        {
                            continue;
                        }
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int i = piece.tris[3 * k + corner];
                            int v;
                            if (!made.TryGetValue(i, out v))
                            {
                                v = from.Count;
                                made[i] = v;
                                Vector3 o = piece.rest[i] - at;
                                float x = Vector3.Dot(o, right) / d.span + 0.5f, y = Vector3.Dot(o, up) / d.span + 0.5f;
                                from.Add(i);
                                fromPiece.Add(g);
                                uvs.Add(new Vector4(d.cell.x + x * d.cell.width, d.cell.y + y * d.cell.height, x, y));
                                colours.Add(d.colour);
                            }
                            tris.Add(v);
                        }
                    }
                }
            }
            w.from = from.ToArray();
            w.fromPiece = fromPiece.ToArray();
            w.verts = new Vector3[w.from.Length];
            w.normals = new Vector3[w.from.Length];
            w.empty = tris.Count == 0;
            List<Vector3> tags = new List<Vector3>(w.from.Length);
            for (int i = 0; i < w.from.Length; i++)
            {
                w.verts[i] = pieces[w.fromPiece[i]].rest[w.from[i]];
                tags.Add(Api.MovingMeshTag);   // clipped under the roof over where it is drawn, as IsoRim's moving models are
            }
            w.mesh.Clear();
            w.mesh.vertices = w.verts;
            w.mesh.normals = w.normals;
            w.mesh.colors32 = colours.ToArray();
            w.mesh.SetUVs(0, uvs.ToArray());
            w.mesh.SetUVs(1, tags);
            w.mesh.triangles = tris.ToArray();
            // Set once, roomy enough for any pose (cells, the model's own space): it is only for culling.
            w.mesh.bounds = new Bounds(new Vector3(0f, 0.7f, 0f), new Vector3(2.5f, 2f, 2.5f));
        }

        // The pawn's wounds moved to the pose End left it in; null if it has none showing.
        public Mesh PoseWounds(Posed p)
        {
            WoundMeshes w = p.wounds;
            if (w == null || w.empty)
            {
                return null;
            }
            if (w.posedAt != p.posedAt)
            {
                w.posedAt = p.posedAt;
                int shown = -1;
                PieceMeshes on = null;
                for (int i = 0; i < w.from.Length; i++)
                {
                    if (w.fromPiece[i] != shown)
                    {
                        shown = w.fromPiece[i];
                        on = PosedOnCpu(p, shown);   // its vertices as posed, also when the graphics card poses its meshes
                    }
                    Vector3 n = on.normals[w.from[i]];
                    w.verts[i] = on.verts[w.from[i]] + n.normalized * WoundLift;
                    w.normals[i] = n;
                }
                w.mesh.vertices = w.verts;
                w.mesh.normals = w.normals;
            }
            return w.mesh;
        }

        // The place at rest of a texture place on the face patch, so a wound can go where a face layer draws a feature;
        // false off the patch or without one.
        public bool FacePlace(Vector2 uv, out Vector3 at)
        {
            at = Vector3.zero;
            for (int k = 0; faceTris != null && k + 2 < faceTris.Length; k += 3)
            {
                int a = faceTris[k], b = faceTris[k + 1], c = faceTris[k + 2];
                Vector2 ab = faceUvs[b] - faceUvs[a], ac = faceUvs[c] - faceUvs[a], ap = uv - faceUvs[a];
                float det = ab.x * ac.y - ab.y * ac.x;
                if (Mathf.Abs(det) < 1e-9f)
                {
                    continue;
                }
                float s = (ap.x * ac.y - ap.y * ac.x) / det, t = (ab.x * ap.y - ab.y * ap.x) / det;
                if (s >= 0f && t >= 0f && s + t <= 1f)
                {
                    at = faceRest[a] + (faceRest[b] - faceRest[a]) * s + (faceRest[c] - faceRest[a]) * t;
                    return true;
                }
            }
            return false;
        }

        // The place at rest furthest along this way of the skin the head bone moves most.
        public Vector3 HeadMost(Vector3 way)
        {
            FindTriangles();
            Piece body = pieces[0];
            Vector3 most = Vector3.zero;
            float best = float.MinValue;
            for (int k = 0; k < triBone.Length; k++)
            {
                for (int corner = 0; triBone[k] == headBone && corner < 3; corner++)
                {
                    Vector3 v = body.rest[body.tris[3 * k + corner]];
                    if (Vector3.Dot(v, way) > best)
                    {
                        best = Vector3.Dot(v, way);
                        most = v;
                    }
                }
            }
            return most;
        }

        // True if body triangle k may carry a spot: on these bones, and within scatter of aim if there is one.
        private bool Spottable(int k, bool[] mine, Vector3 aim, float scatter)
        {
            if (!mine[triBone[k]])
            {
                return false;
            }
            if (scatter <= 0f)
            {
                return true;
            }
            Piece body = pieces[0];
            return (ClosestOn(aim, body.rest[body.tris[3 * k]], body.rest[body.tris[3 * k + 1]], body.rest[body.tris[3 * k + 2]]) - aim).sqrMagnitude <= scatter * scatter;
        }

        // A spot on the skin of these bones at rest (within scatter of aim, if above 0), picked by r, each place as likely
        // as any other (by the triangles' area), and the skin's way out there; false if none of the body is theirs. mine is
        // set to the bones.
        private bool Spot(int[] bones, Vector3 aim, float scatter, System.Random r, bool[] mine, out Vector3 at, out Vector3 n)
        {
            for (int b = 0; b < mine.Length; b++)
            {
                mine[b] = false;
            }
            foreach (int b in bones)
            {
                if (b >= 0)
                {
                    mine[b] = true;
                }
            }
            float total = 0f;
            for (int k = 0; k < triBone.Length; k++)
            {
                if (Spottable(k, mine, aim, scatter))
                {
                    total += triArea[k];
                }
            }
            at = n = Vector3.zero;
            if (total <= 0f)
            {
                return false;
            }
            Piece body = pieces[0];
            int i0 = 0, i1 = 0, i2 = 0;
            // A triangle near the aim reaches past scatter, so a pick beyond it is picked again; the last pick is pulled in.
            for (int attempt = 0; attempt < SpotTries; attempt++)
            {
                float pick = (float)r.NextDouble() * total;
                int chosen = -1;
                for (int k = 0; k < triBone.Length && pick >= 0f; k++)
                {
                    if (Spottable(k, mine, aim, scatter))
                    {
                        chosen = k;
                        pick -= triArea[k];
                    }
                }
                float u = (float)r.NextDouble(), v = (float)r.NextDouble();
                if (u + v > 1f)
                {
                    u = 1f - u;
                    v = 1f - v;
                }
                i0 = body.tris[3 * chosen];
                i1 = body.tris[3 * chosen + 1];
                i2 = body.tris[3 * chosen + 2];
                at = body.rest[i0] * (1f - u - v) + body.rest[i1] * u + body.rest[i2] * v;
                n = (restNormal[i0] * (1f - u - v) + restNormal[i1] * u + restNormal[i2] * v).normalized;
                if (scatter <= 0f || (at - aim).sqrMagnitude <= scatter * scatter)
                {
                    return true;
                }
            }
            at = ClosestOn(aim, body.rest[i0], body.rest[i1], body.rest[i2]);
            n = (restNormal[i0] + restNormal[i1] + restNormal[i2]).normalized;
            return true;
        }

        // Each body triangle's bone, area and way out, made once.
        private void FindTriangles()
        {
            if (triBone != null)
            {
                return;
            }
            Piece body = pieces[0];
            int count = body.tris.Length / 3;
            triBone = new int[count];
            triArea = new float[count];
            triNormal = new Vector3[count];
            restNormal = Normals(body.rest, body.tris);
            float[] sum = new float[boneHead.Length];
            for (int k = 0; k < count; k++)
            {
                for (int b = 0; b < sum.Length; b++)
                {
                    sum[b] = 0f;
                }
                for (int corner = 0; corner < 3; corner++)
                {
                    int i = body.tris[3 * k + corner];
                    for (int j = body.firstWeight[i]; j < body.firstWeight[i + 1]; j++)
                    {
                        sum[body.weightBone[j]] += body.weight[j];
                    }
                }
                int best = 0;
                for (int b = 1; b < sum.Length; b++)
                {
                    if (sum[b] > sum[best])
                    {
                        best = b;
                    }
                }
                triBone[k] = best;
                Vector3 a = body.rest[body.tris[3 * k]];
                Vector3 f = Vector3.Cross(body.rest[body.tris[3 * k + 1]] - a, body.rest[body.tris[3 * k + 2]] - a);
                triArea[k] = f.magnitude / 2f;
                triNormal[k] = f.normalized;
            }
        }

        // The body triangles under the garments in mask, made once per mask.
        private bool[] Hidden(GarmentMask mask)
        {
            bool[] hide;
            if (!hiddenBy.TryGetValue(mask, out hide))
            {
                hide = HiddenBy(mask);
                hiddenBy[mask] = hide;
            }
            return hide;
        }

        // The body triangle under each of garment g's triangles, -1 where it doesn't follow the skin: its corners and middle
        // all within ClothOnSkin of the body at rest and it facing the way of the body there. Made once per garment.
        private int[] SkinOf(int g)
        {
            int[] found;
            if (skinOf.TryGetValue(g, out found))
            {
                return found;
            }
            FindTriangles();
            Piece cloth = pieces[g], body = pieces[0];
            bool[] onSkin = new bool[cloth.rest.Length];
            for (int i = 0; i < onSkin.Length; i++)
            {
                int ignored;
                onSkin[i] = NearestSkin(cloth.rest[i], out ignored) <= ClothOnSkin * ClothOnSkin;
            }
            found = new int[cloth.tris.Length / 3];
            for (int k = 0; k < found.Length; k++)
            {
                found[k] = -1;
                int a = cloth.tris[3 * k], b = cloth.tris[3 * k + 1], c = cloth.tris[3 * k + 2];
                if (!onSkin[a] || !onSkin[b] || !onSkin[c])
                {
                    continue;
                }
                int skin;
                Vector3 middle = (cloth.rest[a] + cloth.rest[b] + cloth.rest[c]) / 3f;
                Vector3 facing = Vector3.Cross(cloth.rest[b] - cloth.rest[a], cloth.rest[c] - cloth.rest[a]).normalized;
                if (NearestSkin(middle, out skin) <= ClothOnSkin * ClothOnSkin && Vector3.Dot(facing, triNormal[skin]) >= ClothFacing)
                {
                    found[k] = skin;
                }
            }
            skinOf[g] = found;
            return found;
        }

        // The body triangle nearest a point at rest, and how far off it is, squared.
        private float NearestSkin(Vector3 point, out int nearest)
        {
            Piece body = pieces[0];
            float best = float.MaxValue;
            nearest = -1;
            for (int k = 0; k < triBone.Length; k++)
            {
                float far = (ClosestOn(point, body.rest[body.tris[3 * k]], body.rest[body.tris[3 * k + 1]], body.rest[body.tris[3 * k + 2]]) - point).sqrMagnitude;
                if (far < best)
                {
                    best = far;
                    nearest = k;
                }
            }
            return best;
        }

        // The point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5).
        private static Vector3 ClosestOn(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                return a;
            }
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                return b;
            }
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                return a + ab * (d1 / (d1 - d3));
            }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                return c;
            }
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                return a + ac * (d2 / (d2 - d6));
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            {
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            }
            float denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }
    }
}
