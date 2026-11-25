using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A race's tail and ears built from its pictures (AddonBuilder.cs), moved as the gene's are: a tail on the cat tail's
    // springs (CatTail.cs: it trails a walk, swings in a turn, sways, rises with the mood and drapes on the ground lying),
    // an ear on the cat ears' (CatEars.cs: swept back by the head's motion, flattened when hit, flicked now and then).
    // Each built part gets a chain of joints from its root to its tip, found on its own surface: the root the vertex
    // nearest the hips (a tail) or the head's middle (an ear), the chain the middle of the surface at each distance from
    // there along it, so a curled tail's chain follows its curl. Each vertex is weighted to the chain by its nearest
    // point on that line. A tail is also moved so its root is where the gene's tail grows from, as the race's picture,
    // laid on the chibi by the body's box, put it on the thighs: the chibi's hips are higher up its body than a RimWorld
    // body sprite's.
    public sealed partial class SkinnedFigure
    {
        private const int RaceTailBones = 9;   // as the gene's tail
        private const int RaceEarBones = 3;    // as the gene's ears
        private const float RaceEarFlat = 0.6f;   // of the gene's ears' mood turns (EarFlatBack, EarFlatDown): a race's ears are bigger, and went through the head

        private readonly Dictionary<Piece, Tail> raceTails = new Dictionary<Piece, Tail>();
        private readonly Dictionary<Piece, Ears> raceEars = new Dictionary<Piece, Ears>();

        // The race tail or ears a built part moves as, null if it is rigid.
        internal bool IsRaceTail(Piece piece)
        {
            return piece != null && raceTails.ContainsKey(piece);
        }

        internal bool IsRaceEar(Piece piece)
        {
            return piece != null && raceEars.ContainsKey(piece);
        }

        internal int RaceTailBoneCount(Piece piece)
        {
            Tail t;
            return raceTails.TryGetValue(piece, out t) ? t.joints.Length - 1 : 0;
        }

        // Called by BuildAddon on what it built: gives a tail or an ear its chain. verts and tris are the welded surface
        // the piece was textured from, at the places the piece's rest has before any move.
        private void ChainAddon(AddonSource src, Piece piece, List<Vector3> verts, List<int> tris)
        {
            try
            {
                if (src.tail && !src.onHead)
                {
                    RaceTail(piece, verts, tris);
                }
                else if (src.ear && src.onHead)
                {
                    RaceEar(src, piece, verts, tris);
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "couldn't give the race part " + src.name + " joints, it is drawn still: " + e.Message);
            }
        }

        private void RaceTail(Piece piece, List<Vector3> verts, List<int> tris)
        {
            Tail cat = TailOf();   // also finds the hips bone
            if (hipsBone < 0 && !boneIndex.TryGetValue(TailBoneName, out hipsBone))
            {
                return;
            }
            // Toward the middle of the hips, at the back: the root is nearest it.
            Vector3 hips = cat != null ? cat.joints[0] : BoneHead(hipsBone);
            Vector3[] joints = ChainOf(verts, tris, hips, RaceTailBones);
            if (joints == null)
            {
                return;
            }
            Vector3 move = cat != null ? cat.joints[0] - joints[0] : Vector3.zero;
            for (int i = 0; i < piece.rest.Length; i++)
            {
                piece.rest[i] += move;
            }
            for (int j = 0; j < joints.Length; j++)
            {
                joints[j] += move;
            }
            Tail t = new Tail { piece = piece, joints = joints, bone = hipsBone };
            ChainWeights(piece.rest, joints, out t.firstWeight, out t.weightBone, out t.weight);
            t.baseShape = ShapePiece(piece, new List<KeyValuePair<string, float>>());
            t.baseShape.hull = SmoothHull(t.baseShape.normal);
            t.bones = new Matrix4x4[joints.Length - 1];
            RootWeights(t);
            raceTails[piece] = t;
            Log.Message(Tag + "the race tail " + piece.name + " has " + (joints.Length - 1) + " joints from " + joints[0].ToString("F2") + " to " + joints[joints.Length - 1].ToString("F2")
                + ", its root moved " + move.ToString("F2") + " onto the hips.");
        }

        private void RaceEar(AddonSource src, Piece piece, List<Vector3> verts, List<int> tris)
        {
            Vector3 middle = (addonHeadLow + addonHeadHigh) * 0.5f;
            Vector3[] joints = ChainOf(verts, tris, middle, RaceEarBones);
            if (joints == null)
            {
                return;
            }
            int n = piece.rest.Length;
            Ears e = new Ears { piece = piece, flops = false, share = SharesOf(RaceEarBones) };
            e.joints[0] = joints;
            e.joints[1] = joints;   // unused: every vertex is on side 0
            ChainWeights(piece.rest, joints, out e.firstWeight, out e.weightBone, out e.weight);
            e.keyRest = new Vector3[4][];
            e.keyNormal = new Vector3[4][];
            e.keyHull = new Vector3[4][];
            for (int k = 0; k < 4; k++)
            {
                e.keyRest[k] = new Vector3[n];
                e.keyNormal[k] = new Vector3[n];
                e.keyHull[k] = new Vector3[n];
            }
            e.baseShape = ShapePiece(piece, new List<KeyValuePair<string, float>>());
            e.baseShape.hull = SmoothHull(e.baseShape.normal);
            Shape b = BaseShape();
            MeasureHead(b);
            e.anchor = new Vector3(0f, b.headHigh.y, (b.headLow.z + b.headHigh.z) / 2f);
            e.width = b.headHigh.x - b.headLow.x;
            raceEars[piece] = e;
        }

        private Vector3 BoneHead(int bone)
        {
            return boneHead != null && bone >= 0 && bone < boneHead.Length ? boneHead[bone] : Vector3.zero;
        }

        // The side a race ear is on at rest: "L" for the figure's left (-x, as the gene's ears have it), else "R".
        internal string RaceEarSide(Piece piece)
        {
            return raceEars[piece].joints[0][0].x < 0f ? "L" : "R";
        }

        // A chain of bones + 1 joints along a surface from its vertex nearest toward to its farthest: the distance of each
        // vertex from that root over the surface, the middle of the vertices at each step of it (so the line runs down the
        // middle of a tube, not along its side), smoothed, and the joints evenly along that line. Null if the surface is
        // too short or in pieces the root's doesn't reach.
        private static Vector3[] ChainOf(List<Vector3> verts, List<int> tris, Vector3 toward, int bones)
        {
            int n = verts.Count;
            List<int>[] near = new List<int>[n];
            for (int t = 0; t < tris.Count; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = tris[t + e], b = tris[t + (e + 1) % 3];
                    (near[a] = near[a] ?? new List<int>()).Add(b);
                    (near[b] = near[b] ?? new List<int>()).Add(a);
                }
            }
            int root = -1;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float d = (verts[i] - toward).sqrMagnitude;
                if (near[i] != null && d < best)
                {
                    best = d;
                    root = i;
                }
            }
            if (root < 0)
            {
                return null;
            }
            // Over the surface from the root (Dijkstra; a part is a few hundred vertices).
            float[] dist = new float[n];
            bool[] done = new bool[n];
            for (int i = 0; i < n; i++)
            {
                dist[i] = float.MaxValue;
            }
            dist[root] = 0f;
            for (;;)
            {
                int at = -1;
                float least = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (!done[i] && dist[i] < least)
                    {
                        least = dist[i];
                        at = i;
                    }
                }
                if (at < 0)
                {
                    break;
                }
                done[at] = true;
                foreach (int w in near[at])
                {
                    float d = least + (verts[w] - verts[at]).magnitude;
                    if (d < dist[w])
                    {
                        dist[w] = d;
                    }
                }
            }
            float longest = 0f;
            int tip = root;
            for (int i = 0; i < n; i++)
            {
                if (dist[i] < float.MaxValue && dist[i] > longest)
                {
                    longest = dist[i];
                    tip = i;
                }
            }
            if (longest < 1e-3f)
            {
                return null;
            }
            int steps = Mathf.Max(2 * bones, 8);
            Vector3[] sum = new Vector3[steps];
            int[] count = new int[steps];
            for (int i = 0; i < n; i++)
            {
                if (dist[i] == float.MaxValue)
                {
                    continue;
                }
                int k = Mathf.Min(steps - 1, (int)(dist[i] / longest * steps));
                sum[k] += verts[i];
                count[k]++;
            }
            List<Vector3> line = new List<Vector3>();
            for (int k = 0; k < steps; k++)
            {
                if (count[k] > 0)
                {
                    line.Add(sum[k] / count[k]);
                }
            }
            line.Add(verts[tip]);
            for (int pass = 0; pass < 2; pass++)
            {
                List<Vector3> smooth = new List<Vector3>(line);
                for (int k = 1; k < line.Count - 1; k++)
                {
                    smooth[k] = (line[k - 1] + 2f * line[k] + line[k + 1]) * 0.25f;
                }
                line = smooth;
            }
            float[] along = new float[line.Count];
            for (int k = 1; k < line.Count; k++)
            {
                along[k] = along[k - 1] + (line[k] - line[k - 1]).magnitude;
            }
            float total = along[line.Count - 1];
            if (total < 1e-3f)
            {
                return null;
            }
            Vector3[] joints = new Vector3[bones + 1];
            int seg = 0;
            for (int j = 0; j <= bones; j++)
            {
                float s = total * j / bones;
                while (seg < line.Count - 2 && along[seg + 1] < s)
                {
                    seg++;
                }
                float span = along[seg + 1] - along[seg];
                joints[j] = Vector3.Lerp(line[seg], line[seg + 1], span > 0f ? Mathf.Clamp01((s - along[seg]) / span) : 0f);
            }
            return joints;
        }

        // Each point's weights on a chain (bone j from joint j to j + 1): by how far along the chain its nearest point on it
        // is, shared between the two bones nearest there, from wholly one at a bone's middle to half and half at a joint.
        private static void ChainWeights(Vector3[] points, Vector3[] joints, out int[] first, out int[] bone, out float[] weight)
        {
            int bones = joints.Length - 1;
            first = new int[points.Length + 1];
            List<int> b = new List<int>();
            List<float> w = new List<float>();
            for (int i = 0; i < points.Length; i++)
            {
                first[i] = b.Count;
                float bestDist = float.MaxValue, at = 0f;
                for (int j = 0; j < bones; j++)
                {
                    Vector3 a = joints[j], d = joints[j + 1] - a;
                    float t = d.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(points[i] - a, d) / d.sqrMagnitude) : 0f;
                    float dist = (a + d * t - points[i]).sqrMagnitude;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        at = j + t;
                    }
                }
                float u = Mathf.Clamp(at - 0.5f, 0f, bones - 1f);
                int k0 = Mathf.Min((int)u, bones - 1), k1 = Mathf.Min(k0 + 1, bones - 1);
                float frac = u - k0;
                if (k1 == k0 || frac < 0.01f)
                {
                    b.Add(k0);
                    w.Add(1f);
                }
                else
                {
                    b.Add(k0);
                    w.Add(1f - frac);
                    b.Add(k1);
                    w.Add(frac);
                }
            }
            first[points.Length] = b.Count;
            bone = b.ToArray();
            weight = w.ToArray();
        }

        // The meshes a pawn draws a built part with, made the first time.
        private PieceMeshes AddonMeshes(Posed p, Piece piece)
        {
            PieceMeshes m;
            if (!p.addons.TryGetValue(piece, out m))
            {
                m = NewPieceMeshes(piece);
                m.tinted = false;
                if (piece.mark != null)
                {
                    m.parts.Add(new MeshPart { mesh = m.solid, material = piece.mark, outline = false });
                }
                p.addons[piece] = m;
            }
            return m;
        }

        // The race tail's probe joint as posed and the hips' turn, for its springs.
        internal void RaceTailProbe(Posed p, Piece piece, out Vector3 point, out Quaternion turn)
        {
            TailProbe(p, raceTails[piece], out point, out turn);
        }

        // The race ear's root as posed and the head's turn.
        internal void RaceEarRoot(Posed p, Piece piece, out Vector3 root, out Quaternion turn)
        {
            Matrix4x4 head = p.whole[headBone];
            root = head.MultiplyPoint3x4(FitOf(raceEars[piece], p.shape).joints[0][0]);
            turn = head.rotation;
        }

        internal void DrawRaceTail(Posed p, Piece piece, TailState s, float floor, Vector3 position, float yaw, float size, Color tint)
        {
            Material solid = TexturedMaterial(piece);
            if (solid == null)
            {
                return;
            }
            PieceMeshes m = AddonMeshes(p, piece);
            PoseTail(p, m, raceTails[piece], s, floor);
            DrawMeshes(p, m, piece.rest.Length, solid, position, yaw, size, tint, lineTint: tint * piece.mean);
        }

        internal void DrawRaceEar(Posed p, Piece piece, EarState s, bool gone, Vector3 position, float yaw, float size, Color tint)
        {
            Material solid = TexturedMaterial(piece);
            if (solid == null)
            {
                return;
            }
            PieceMeshes m = AddonMeshes(p, piece);
            // The mood's turns (s.up, s.low) taken down for a race's ears, which are bigger than the gene's.
            float up = s.up[0], low = s.low[0];
            s.up[0] *= RaceEarFlat;
            s.low[0] *= RaceEarFlat;
            PoseEars(p, m, raceEars[piece], s, gone, gone);
            s.up[0] = up;
            s.low[0] = low;
            DrawMeshes(p, m, piece.rest.Length, solid, position, yaw, size, tint, lineTint: tint * piece.mean);
        }

        // A race's bespoke ears and tail (RaceModels, anim_scripts/make_race_parts.py): files in the gene ears' and tail's
        // own form, read once, moved on the same springs.
        private readonly Dictionary<string, Tail> modelTails = new Dictionary<string, Tail>();

        internal bool HasModelEars(string file)
        {
            return file != null && EarsOf(file) != null;
        }

        internal bool HasModelTail(string file)
        {
            return ModelTailOf(file) != null;
        }

        private Tail ModelTailOf(string file)
        {
            Tail t;
            if (file == null)
            {
                return null;
            }
            if (!modelTails.TryGetValue(file, out t))
            {
                t = LoadTail(file);
                modelTails[file] = t;
            }
            return t;
        }

        internal int ModelTailBones(string file)
        {
            Tail t = ModelTailOf(file);
            return t == null ? 0 : t.joints.Length - 1;
        }

        internal void ModelTailProbe(Posed p, string file, out Vector3 point, out Quaternion turn)
        {
            TailProbe(p, ModelTailOf(file), out point, out turn);
        }

        private PieceMeshes ModelMeshes(Posed p, Piece piece)
        {
            PieceMeshes m;
            if (!p.addons.TryGetValue(piece, out m))
            {
                m = NewPieceMeshes(piece);
                p.addons[piece] = m;
            }
            return m;
        }

        // The ears in tint, their lining (inner) in lining.
        internal void DrawModelEars(Posed p, string file, EarState s, bool goneL, bool goneR, Vector3 position, float yaw, float size, Color tint, Color lining)
        {
            Ears e = EarsOf(file);
            if (e == null || SolidMaterial() == null)
            {
                return;
            }
            PieceMeshes m = ModelMeshes(p, e.piece);
            Material solid = GrainedMaterial(e.piece, m);
            float upL = s.up[0], upR = s.up[1], lowL = s.low[0], lowR = s.low[1];
            s.up[0] *= RaceEarFlat;
            s.up[1] *= RaceEarFlat;
            s.low[0] *= RaceEarFlat;
            s.low[1] *= RaceEarFlat;
            PoseEars(p, m, e, s, goneL, goneR);
            s.up[0] = upL;
            s.up[1] = upR;
            s.low[0] = lowL;
            s.low[1] = lowR;
            // The lining goes in the scalp's place, shaded ScalpShade, which is taken back out here (as DrawEars does).
            DrawMeshes(p, m, e.piece.rest.Length, solid, position, yaw, size, tint, null, default(Color), e.inner, lining / ScalpShade);
        }

        // The tail in tint, its tip (inner) in tip.
        internal void DrawModelTail(Posed p, string file, TailState s, float floor, Vector3 position, float yaw, float size, Color tint, Color tip)
        {
            Tail t = ModelTailOf(file);
            if (t == null || SolidMaterial() == null)
            {
                return;
            }
            PieceMeshes m = ModelMeshes(p, t.piece);
            Material solid = t.plain ? SolidMaterial() : GrainedMaterial(t.piece, m);
            PoseTail(p, m, t, s, floor);
            DrawMeshes(p, m, t.piece.rest.Length, solid, position, yaw, size, tint, null, default(Color), t.inner, tip / ScalpShade);
        }

        // The mean colour of an add-on's pictures inside their outline ink (as a built part's outline is drawn in), so a
        // model in place of it is the colour its art is.
        internal static Color AddonMean(AddonSource src)
        {
            return MeanOf(src.px);
        }

        // The same for any pictures (AddonTexSize square), nulls skipped.
        internal static Color MeanOf(params Color32[][] pictures)
        {
            Vector4 sum = Vector4.zero;
            foreach (Color32[] px in pictures)
            {
                if (px != null)
                {
                    Bled(px, AddonTexSize, ref sum);
                }
            }
            return sum.w > 0f ? new Color(sum.x / sum.w, sum.y / sum.w, sum.z / sum.w, 1f) : Color.white;
        }
    }

    public static partial class WalkFigures
    {

        // The races with ears and tails of their own made for them (anim_scripts/make_race_parts.py), drawn in place of
        // those built from their sprites: each a race's defName, then its ears' and tail's files (null for none) and the
        // colour of the ears' lining and the tail's tip, as a share of the way from the part's colour to its own.
        private sealed class RaceModel
        {
            public string ears, tail;
            public string[] wings;   // left and right, for a race whose wings are body parts (Milira's, drawn by AncotLibrary's nodes)
            public Color lining, tip;
            public float liningShare, tipShare;
        }

        private static readonly Dictionary<string, RaceModel> RaceModels = new Dictionary<string, RaceModel>
        {
            { "Ratkin", new RaceModel { ears = "race_ratkin_ears.txt", tail = "race_ratkin_tail.txt", lining = new Color(1f, 0.55f, 0.55f), liningShare = 0.9f } },
            { "Wolfein_Race", new RaceModel { ears = "race_wolfein_ears.txt", tail = "race_wolfein_tail.txt", lining = new Color(0.97f, 0.95f, 0.92f), liningShare = 0f,   // no pale inside (was 0.7)
                tip = new Color(0.97f, 0.95f, 0.92f), tipShare = 0.75f } },
            { "Milira_Race", new RaceModel { wings = new[] { "race_milira_wing_L.txt", "race_milira_wing_R.txt" }, tip = new Color(0.82f, 0.84f, 0.9f), tipShare = 0.35f } },
        };

        // The race's model for an add-on of this name ("ear" or "tail" among its words, or in it), or null.
        private static string ModelFileFor(ThingDef race, string addonName, out bool ears)
        {
            ears = false;
            RaceModel m;
            if (race == null || !RaceModelsOn || !RaceModels.TryGetValue(race.defName, out m))
            {
                return null;
            }
            List<string> words = HairBuilder.AddonWords(addonName);
            if (m.ears != null && (words.Contains("ear") || words.Contains("ears")) && skin.HasModelEars(m.ears))
            {
                ears = true;
                return m.ears;
            }
            if (m.tail != null && addonName.IndexOf("tail", StringComparison.OrdinalIgnoreCase) >= 0 && skin.HasModelTail(m.tail))
            {
                return m.tail;
            }
            return null;
        }

#if AUTOTEST
        private static bool? noModelsFlag;
#endif

        // A winged race's wings, by its body parts (any part named a wing, its side by its label) and their colour (the
        // wing node's art): wingL, wingR false with that wing gone.
        private static void WingsOf(Pawn pawn, PawnRenderTree tree, out bool wingL, out bool wingR, out Color tint)
        {
            wingL = wingR = false;
            tint = Color.white;
            if (pawn.health == null)
            {
                return;
            }
            foreach (BodyPartRecord part in pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part.def.defName.IndexOf("wing", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                string label = (part.untranslatedCustomLabel ?? part.def.label ?? "").ToLowerInvariant();
                wingL |= label.Contains("left");
                wingR |= label.Contains("right");
            }
            if ((!wingL && !wingR) || tree == null || tree.rootNode == null)
            {
                return;
            }
            PawnRenderNode node = FindNode(tree.rootNode, "left wing") ?? FindNode(tree.rootNode, "right wing");
            if (node == null)
            {
                return;
            }
            try
            {
                PawnDrawParms parms = PawnDrawParms.Default;
                parms.pawn = pawn;
                parms.matrix = Matrix4x4.identity;
                parms.facing = Rot4.North;
                parms.rotDrawMode = RotDrawMode.Fresh;
                parms.posture = PawnPosture.Standing;
                Material mat = node.Worker.GetFinalizedMaterial(node, parms);
                if (mat != null)
                {
                    // The art's own colour: the node's material came out tinted a dull brown (not the race's wing colour).
                    // Most of the way to white: the art's shading lines take its mean down to a mid grey, which read as brown.
                    tint = mat.mainTexture != null ? Color.Lerp(SkinnedFigure.MeanOf(PixelsOf(mat.mainTexture)), Color.white, 0.7f) : Color.white;
                }
            }
            catch (Exception)
            {
                // Left white: the wings' art is white.
            }
            if (wingsNoted.Add(pawn.def.defName))
            {
                Log.Message(Tag + "the " + pawn.def.defName + " wings' colour: " + tint + (node != null ? ", from " + node.Props.debugLabel : ""));
            }
        }

        private static readonly HashSet<string> wingsNoted = new HashSet<string>();

        private static PawnRenderNode FindNode(PawnRenderNode n, string label)
        {
            if (n == null)
            {
                return null;
            }
            if (n.Props != null && string.Equals(n.Props.debugLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
            if (n.children != null)
            {
                foreach (PawnRenderNode c in n.children)
                {
                    PawnRenderNode found = FindNode(c, label);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            return null;
        }

        internal static bool RaceModelsOn
        {
            get
            {
#if AUTOTEST
                if (noModelsFlag == null)
                {
                    // A file, as the game started through Steam doesn't get the shell's environment.
                    noModelsFlag = System.IO.File.Exists(System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "no_race_models"));
                }
                if (noModelsFlag.Value)
                {
                    return false;   // to see the parts built from the pictures in their place
                }
#endif
                return SettingsMod.Settings == null || SettingsMod.Settings.raceModels;
            }
        }

        // Steps and draws a race's model ears (both, from one file) on the gene ears' springs.
        private static void DrawModelEars(Pawn pawn, SkinnedFigure.Posed fig, string file, Vector3 at, float yaw, float size, Color tint, Color lining)
        {
            SkinnedFigure.EarState s;
            if (!fig.modelEars.TryGetValue(file, out s))
            {
                s = new SkinnedFigure.EarState();
                fig.modelEars[file] = s;
            }
            Vector3 rootL, rootR;
            Quaternion turn;
            skin.EarRoot(fig, file, 0, out rootL, out turn);
            skin.EarRoot(fig, file, 1, out rootR, out turn);
            StepEarState(pawn, s, false, 0, rootL, rootR, turn, 2, 0, at, yaw, size);
            bool goneL = false, goneR = false;
            List<Hediff_MissingPart> missing = pawn.health != null ? pawn.health.hediffSet.GetMissingPartsCommonAncestors() : null;
            for (int i = 0; missing != null && i < missing.Count; i++)
            {
                BodyPartRecord part = missing[i].Part;
                if (part != null && part.def.defName == "Ear")
                {
                    string side = SideOf(part);
                    goneL |= side == "L";
                    goneR |= side == "R";
                }
            }
            skin.DrawModelEars(fig, file, s, goneL, goneR, at, yaw, size, tint, lining);
        }

        // And its model tail, on the gene tail's, stepped while hidden too.
        // stateKey: the springs it moves on, which a pair of wings share so they move together (each stepped once a draw).
        private static void DrawModelTail(Pawn pawn, SkinnedFigure.Posed fig, string file, string stateKey, bool hidden, Vector3 at, float yaw, float size, float sat, float floor, bool onGround, Color tint, Color tip)
        {
            SkinnedFigure.TailState s;
            int bones = skin.ModelTailBones(file);
            bool shared = stateKey != file;
            if (!fig.modelTails.TryGetValue(stateKey, out s) || s.side.Length != bones)
            {
                s = new SkinnedFigure.TailState(bones);
                fig.modelTails[stateKey] = s;
                shared = false;
            }
            if (!shared || s.frame != Time.frameCount)
            {
                Vector3 probe;
                Quaternion turn;
                skin.ModelTailProbe(fig, file, out probe, out turn);
                StepTailState(pawn, s, probe, turn, at, yaw, size, sat, onGround, 0x7A11u);
            }
            if (!hidden)
            {
                skin.DrawModelTail(fig, file, s, floor, at, yaw, size, tint, tip);
            }
        }
        // Steps and draws a race's built tail (on its springs, as the gene's), stepped even while hidden so it doesn't
        // jump when it shows again.
        private static void DrawRaceTail(Pawn pawn, SkinnedFigure.Posed fig, SkinnedFigure.Piece piece, bool hidden, Vector3 at, float yaw, float size, float sat, float floor, bool onGround, Color tint)
        {
            SkinnedFigure.TailState s;
            int bones = skin.RaceTailBoneCount(piece);
            if (!fig.raceTails.TryGetValue(piece, out s) || s.side.Length != bones)
            {
                s = new SkinnedFigure.TailState(bones);
                fig.raceTails[piece] = s;
            }
            Vector3 probe;
            Quaternion turn;
            skin.RaceTailProbe(fig, piece, out probe, out turn);
            StepTailState(pawn, s, probe, turn, at, yaw, size, sat, onGround, (uint)piece.name.GetHashCode());
            if (!hidden)
            {
                skin.DrawRaceTail(fig, piece, s, floor, at, yaw, size, tint);
            }
        }

        // The same for a race's built ear, one at a time on the gene ears' springs, closed to its root with its ear gone.
        private static void DrawRaceEar(Pawn pawn, SkinnedFigure.Posed fig, SkinnedFigure.Piece piece, Vector3 at, float yaw, float size, Color tint)
        {
            SkinnedFigure.EarState s;
            if (!fig.raceEars.TryGetValue(piece, out s))
            {
                s = new SkinnedFigure.EarState();
                fig.raceEars[piece] = s;
            }
            Vector3 root;
            Quaternion turn;
            skin.RaceEarRoot(fig, piece, out root, out turn);
            StepEarState(pawn, s, false, 0, root, root, turn, 1, piece.name.GetHashCode(), at, yaw, size);
            bool gone = false;
            string side = skin.RaceEarSide(piece);
            List<Hediff_MissingPart> missing = pawn.health != null ? pawn.health.hediffSet.GetMissingPartsCommonAncestors() : null;
            for (int i = 0; missing != null && i < missing.Count; i++)
            {
                BodyPartRecord part = missing[i].Part;
                gone |= part != null && part.def.defName == "Ear" && SideOf(part) == side;
            }
            skin.DrawRaceEar(fig, piece, s, gone, at, yaw, size, tint);
        }
    }
}
