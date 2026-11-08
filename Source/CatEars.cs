using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Ears for the Ears_Cat, Ears_Pig and Ears_Floppy genes, on the skinned figure: Models/chibi/ears.txt, ears_pig.txt and
    // ears_floppy.txt, the rig's own ears as anim_scripts/export_cat.py writes them. They ride the head bone, as hair does, fitted
    // to this shape's head, a pig's in the skin colour and the others' in the hair's (with a lining of their own if the mesh has
    // an `inner` vertex group).
    //
    // They move three ways. How the pawn is (EarMood, eased) sets how far the ears are turned: flat back and out when hit
    // or in a rage, drooped asleep, downed or unhappy, pricked up when drafted. Each ear's three joints sway on a spring as the
    // head moves (StepEars), and now and then one flicks back and returns or twitches (Flick). The spring and the flicks are
    // drawing only, so neither takes the game's random numbers. A file with up and down keys (up_L, up_R, down_L, down_R)
    // moves its vertices by them as well.
    //
    // Floppy ears hang rather than stand, so most of these move them by their curl (StepFlop, Ears.flops), each joint's bend
    // from rest taken out or added to: perking up straightens them, being hit or asleep tucks them in against the head, and
    // going forward, a quick turn or the head's bob lift them. A swing back keeps to the hanging part, and each joint out
    // along the ear is on a softer spring than the one before, so the tip lags and flops after the root.
    public sealed partial class SkinnedFigure
    {
        public const string CatEarFile = "ears.txt", PigEarFile = "ears_pig.txt", FloppyEarFile = "ears_floppy.txt";
        private const float EarPink = 0.5f;   // the lining's colour: the skin's, this share of the way to EarLining
        private static readonly Color EarLining = new Color(1f, 0.74f, 0.72f);
        private static readonly float[] EarShare = { 0.5f, 0.3f, 0.2f };   // each joint's share of a three-boned ear's swing, root first
        public const int EarMostBones = 8;   // on one ear, and how far apart in earBones the two ears' bones are
        // A floppy ear's curl: a joint's bend at a curl of 1 is taken out wholly, this many degrees past it, so the nearly straight
        // joints near the tip move too, and the root is lifted FlopRootLift.
        private const float FlopLoose = 15f, FlopRootLift = 15f;
        private const float FlopRootSwing = 0.1f;   // the root's share of a swing back; the joints out from it share the rest evenly
        // What the mood turns the ears by, degrees at full: flat is swept back and out, up is pricked a little forward.
        private const float EarFlatBack = 30f, EarFlatDown = 55f, EarPerkBack = -10f;

        // One pawn's ears as they are now.
        public sealed class EarState
        {
            public readonly float[] back = new float[2], backSpeed = new float[2];   // degrees the tip is swung back, and a second
            public readonly float[] down = new float[2], downSpeed = new float[2];   // and down
            public readonly float[] up = new float[2], low = new float[2];           // the shape keys in use, 0 .. 1
            public readonly float[] flickAt = new float[2];   // game seconds into its rest when the next flick starts
            public readonly float[] since = new float[2];     // game seconds since its last flick started
            public readonly int[] flicks = new int[2];        // how many it has done, for the next one's length and rest
            public readonly Vector3[] wasAt = new Vector3[2]; // where its root was last draw, true space (cells)
            // A floppy ear's, per [side][joint]: its curl (+ straighter, - tighter; see FlopLoose) and its swing back (degrees, whole ear).
            public readonly float[][] curl = { new float[EarMostBones], new float[EarMostBones] };
            public readonly float[][] curlSpeed = { new float[EarMostBones], new float[EarMostBones] };
            public readonly float[][] swing = { new float[EarMostBones], new float[EarMostBones] };
            public readonly float[][] swingSpeed = { new float[EarMostBones], new float[EarMostBones] };
            public float carry;                               // game seconds not yet stepped
            public int frame = -1;                            // the frame it was last drawn in
        }

        // The ears from their file, and each shape's fit of them.
        private sealed class Ears
        {
            public Piece piece;
            public bool flops;          // moved by their curl (PoseEars, StepFlop)
            public float[] share;       // each bone's share of a swing, root first
            public readonly Vector3[][] joints = new Vector3[2][];   // [side][joint], the last the tip
            public int[] firstWeight;   // vertex i's weights are weightBone/weight[firstWeight[i] .. firstWeight[i + 1])
            public int[] weightBone;    // side * EarMostBones + joint
            public float[] weight;
            public bool[] inner;        // the lining's vertices
            public Vector3[][] keyRest, keyNormal, keyHull;   // [up L, up R, down L, down R][vertex]: each key's whole move of them
            public PieceShape baseShape;   // its rest pose, normals and outline
            public Vector3 anchor;      // the top of the head's middle at rest, which they are scaled about for another shape
            public float width;         // and the head's width
            public readonly Dictionary<Shape, EarFit> fits = new Dictionary<Shape, EarFit>();
        }

        // The ears on one shape's head.
        private sealed class EarFit
        {
            public Vector3[] rest;
            public Vector3[][] keyRest;
            public Vector3[][] joints;
            public Vector3[] axisBack = new Vector3[2], axisDown = new Vector3[2];   // what swings each ear's tip back and down
            // A floppy ear's, per [side][joint]: what bends it further on, how many degrees a curl of 1 takes out, and what swings
            // the ear from that joint out back.
            public readonly Vector3[][] axisCurl = new Vector3[2][], axisSwing = new Vector3[2][];
            public readonly float[][] curlTurn = new float[2][];
        }

        private static readonly string[] EarKeys = { "up_L", "up_R", "down_L", "down_R" };
        private readonly Dictionary<string, Ears> earFiles = new Dictionary<string, Ears>();   // by file; null: the export has none
        private readonly Matrix4x4[] earBones = new Matrix4x4[2 * EarMostBones];

        // A file's ears, read the first time a pawn with the gene is drawn; null if an export has none.
        private Ears EarsOf(string file)
        {
            Ears ears;
            if (earFiles.TryGetValue(file, out ears))
            {
                return ears;
            }
            earFiles[file] = null;
            string path = Path.Combine(folder, file);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                ears = ReadEars(path);
                if (file == FloppyEarFile)
                {
                    ears.flops = true;
                    ears.share = FlopSharesOf(ears.share.Length);
                }
                earFiles[file] = ears;
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not read the ears in " + path + ", they are left off: " + e.Message);
            }
            return ears;
        }

        private Ears ReadEars(string path)
        {
            Ears e = new Ears();
            e.piece = ReadMesh(path, false, null);
            if (turnRound)
            {
                TurnRound(e.piece.tris);
            }
            int n = e.piece.rest.Length;
            if (n == 0)
            {
                throw new InvalidDataException(path + ": no vertices");
            }
            e.inner = new bool[n];
            List<int>[] bones = new List<int>[n];
            List<float>[] weights = new List<float>[n];
            SortedDictionary<int, Vector3>[] joints = { new SortedDictionary<int, Vector3>(), new SortedDictionary<int, Vector3>() };
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0)
                {
                    continue;
                }
                if (p[0] == "ear")
                {
                    joints[p[1] == "L" ? 0 : 1][int.Parse(p[2])] = Vec(p, 3);
                }
                else if (p[0] == "inner")
                {
                    for (int i = 1; i < p.Length; i++)
                    {
                        e.inner[int.Parse(p[i])] = true;
                    }
                }
                else if (p[0] == "ew")
                {
                    int v = int.Parse(p[1]);
                    bones[v] = new List<int>();
                    weights[v] = new List<float>();
                    for (int i = 2; i + 2 < p.Length; i += 3)
                    {
                        bones[v].Add((p[i] == "L" ? 0 : EarMostBones) + int.Parse(p[i + 1]));
                        weights[v].Add(Num(p[i + 2]));
                    }
                }
            }
            int boneCount = joints[0].Count - 1;
            if (boneCount < 1 || boneCount > EarMostBones || joints[1].Count != joints[0].Count)
            {
                throw new InvalidDataException(path + ": the ears have " + joints[0].Count + " and " + joints[1].Count + " joints");
            }
            for (int side = 0; side < 2; side++)
            {
                e.joints[side] = new List<Vector3>(joints[side].Values).ToArray();
            }
            e.share = SharesOf(boneCount);
            List<int> allBones = new List<int>();
            List<float> allWeights = new List<float>();
            e.firstWeight = new int[n + 1];
            for (int v = 0; v < n; v++)
            {
                e.firstWeight[v] = allBones.Count;
                if (bones[v] == null || bones[v].Count == 0)
                {
                    throw new InvalidDataException(path + ": vertex " + v + " has no weights");
                }
                allBones.AddRange(bones[v]);
                allWeights.AddRange(weights[v]);
            }
            e.firstWeight[n] = allBones.Count;
            e.weightBone = allBones.ToArray();
            e.weight = allWeights.ToArray();

            // Each key's whole move of the normals and outline too: the ears are drawn at any mix of the four, each frame,
            // and a mix's are near enough the sum of its keys', so only one shape of the ears need be made per key.
            PieceShape plain = ShapePiece(e.piece, new List<KeyValuePair<string, float>>());
            e.keyRest = new Vector3[EarKeys.Length][];
            e.keyNormal = new Vector3[EarKeys.Length][];
            e.keyHull = new Vector3[EarKeys.Length][];
            for (int k = 0; k < EarKeys.Length; k++)
            {
                PieceShape keyed = ShapePiece(e.piece, new List<KeyValuePair<string, float>> { new KeyValuePair<string, float>(EarKeys[k], 1f) });
                e.keyRest[k] = new Vector3[n];
                e.keyNormal[k] = new Vector3[n];
                e.keyHull[k] = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    e.keyRest[k][i] = keyed.rest[i] - plain.rest[i];
                    e.keyNormal[k][i] = keyed.normal[i] - plain.normal[i];
                    e.keyHull[k][i] = keyed.hull[i] - plain.hull[i];
                }
            }
            e.baseShape = plain;
            Shape b = BaseShape();
            MeasureHead(b);
            e.anchor = new Vector3(0f, b.headHigh.y, (b.headLow.z + b.headHigh.z) / 2f);
            e.width = b.headHigh.x - b.headLow.x;
            return e;
        }

        // Each bone's share of a swing, root first: a three-boned ear's as set, any other falling evenly toward the tip.
        private static float[] SharesOf(int boneCount)
        {
            if (boneCount == EarShare.Length)
            {
                return EarShare;
            }
            float[] shares = new float[boneCount];
            float whole = boneCount * (boneCount + 1) / 2f;
            for (int j = 0; j < boneCount; j++)
            {
                shares[j] = (boneCount - j) / whole;
            }
            return shares;
        }

        // A floppy ear's swing back: little at the root, as what hangs is what swings.
        private static float[] FlopSharesOf(int boneCount)
        {
            float[] shares = new float[boneCount];
            for (int j = 0; j < boneCount; j++)
            {
                shares[j] = boneCount == 1 ? 1f : j == 0 ? FlopRootSwing : (1f - FlopRootSwing) / (boneCount - 1);
            }
            return shares;
        }

        // How many bones this file's ears have.
        public int EarBoneCount(string file)
        {
            Ears e = EarsOf(file);
            return e != null ? e.share.Length : 0;
        }

        // Each joint's bend at rest and what turns it, measured from the joints, so an ear reshaped in Blender needs no new numbers.
        private static void MeasureCurl(EarFit f, int side)
        {
            Vector3[] joints = f.joints[side];
            int bones = joints.Length - 1;
            f.axisCurl[side] = new Vector3[bones];
            f.axisSwing[side] = new Vector3[bones];
            f.curlTurn[side] = new float[bones];
            for (int j = 0; j < bones; j++)
            {
                f.axisSwing[side][j] = Vector3.Cross((joints[bones] - joints[j]).normalized, Vector3.back).normalized;
                if (j > 0)
                {
                    Vector3 before = (joints[j] - joints[j - 1]).normalized, after = (joints[j + 1] - joints[j]).normalized;
                    f.axisCurl[side][j] = Vector3.Cross(before, after).normalized;
                    f.curlTurn[side][j] = Vector3.Angle(before, after) + FlopLoose;
                }
            }
            // The root has no bend of its own: it is lifted the way the first bend would straighten it.
            f.axisCurl[side][0] = bones > 1 ? f.axisCurl[side][1] : Vector3.Cross((joints[1] - joints[0]).normalized, Vector3.down).normalized;
            f.curlTurn[side][0] = FlopRootLift;
        }

        // The ears on this shape's head: the head's top and middle, scaled by its width.
        private EarFit FitOf(Ears e, Shape s)
        {
            EarFit f;
            if (e.fits.TryGetValue(s, out f))
            {
                return f;
            }
            MeasureHead(s);
            float scale = (s.headHigh.x - s.headLow.x) / e.width;
            Vector3 anchor = new Vector3(0f, s.headHigh.y, (s.headLow.z + s.headHigh.z) / 2f);
            f = new EarFit();
            int n = e.piece.rest.Length;
            f.rest = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                f.rest[i] = anchor + (e.piece.rest[i] - e.anchor) * scale;
            }
            f.keyRest = new Vector3[EarKeys.Length][];
            for (int k = 0; k < EarKeys.Length; k++)
            {
                f.keyRest[k] = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    f.keyRest[k][i] = e.keyRest[k][i] * scale;
                }
            }
            f.joints = new Vector3[2][];
            for (int side = 0; side < 2; side++)
            {
                int count = e.joints[side].Length;
                f.joints[side] = new Vector3[count];
                for (int j = 0; j < count; j++)
                {
                    f.joints[side][j] = anchor + (e.joints[side][j] - e.anchor) * scale;
                }
                Vector3 along = (f.joints[side][count - 1] - f.joints[side][0]).normalized;
                f.axisBack[side] = Vector3.Cross(along, Vector3.back).normalized;
                f.axisDown[side] = Vector3.Cross(along, Vector3.down).normalized;
                if (e.flops)
                {
                    MeasureCurl(f, side);
                }
            }
            e.fits[s] = f;
            return f;
        }

        // True if the export has these ears.
        public bool HasEars(string file)
        {
            return EarsOf(file) != null;
        }

        // True if something worn in mask goes over them: what leaves the hair off or tucks it (hats, helmets, hoods), as the
        // ears would come through it. Masks, veils and head wear with no garment leave them.
        public bool HidesEars(GarmentMask mask)
        {
            return HidesEars(mask, null, false);
        }

        // The same, by the apparel each garment is worn as (worn[g]); helmetsOnly: only an armour helmet's (IsHelmet).
        public bool HidesEars(GarmentMask mask, Apparel[] worn, bool helmetsOnly)
        {
            if (helmetsOnly)
            {
                for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
                {
                    bool covers = HidesHair(GarmentBit(g)) || pieces[g].hideHair == 2;
                    if ((mask & (GarmentBit(g))) != 0 && covers && worn != null && worn[g] != null && IsHelmet(worn[g].def))
                    {
                        return true;
                    }
                }
                return false;
            }
            if (HidesHair(mask))
            {
                return true;
            }
            for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & (GarmentBit(g))) != 0 && pieces[g].hideHair == 2)
                {
                    return true;
                }
            }
            return false;
        }

        // True for an armour helmet: the game's ArmorHeadgear category (simple, advanced, power armour, recon, cataphract and
        // vacsuit helmets, and most mods' helmets), not its hats.
        public static bool IsHelmet(ThingDef def)
        {
            if (armorHeadgear == null)
            {
                armorHeadgear = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("ArmorHeadgear");
            }
            return def != null && armorHeadgear != null && def.IsWithinCategory(armorHeadgear);
        }
        private static ThingCategoryDef armorHeadgear;

        // Where an ear's root is on the figure as End left it (model units), and how the head has turned from rest.
        public void EarRoot(Posed p, string file, int side, out Vector3 root, out Quaternion turn)
        {
            root = Vector3.zero;
            turn = Quaternion.identity;
            Ears e = EarsOf(file);
            if (e == null)
            {
                return;
            }
            Matrix4x4 head = p.whole[headBone];
            root = head.MultiplyPoint3x4(FitOf(e, p.shape).joints[side][0]);
            turn = head.rotation;
        }

        // A turn of the figure's own space about a point.
        private static Matrix4x4 TurnAbout(Vector3 at, Quaternion turn)
        {
            return Matrix4x4.Translate(at) * Matrix4x4.Rotate(turn) * Matrix4x4.Translate(-at);
        }

        // Draws the ears as the state has them: tint outside, lining's colour within. goneL, goneR: that ear is missing.
        public void DrawEars(Posed p, string file, EarState s, bool goneL, bool goneR, Vector3 position, float yaw, float size, Color tint, Color skin)
        {
            Ears e = EarsOf(file);
            if (e == null)
            {
                return;
            }
            Piece piece = e.piece;
            Material solid = SolidMaterial();
            if (solid == null)
            {
                return;
            }
            if (p.ears == null || p.earsMadeOf != file)
            {
                RetireEars(p);   // a gene changed: those meshes are of the other ears
                p.ears = NewPieceMeshes(piece);
                p.earsMadeOf = file;
            }
            PoseEars(p, e, s, goneL, goneR);
            // The lining goes in the scalp's place: DrawMeshes shades that ScalpShade, which is taken back out here.
            Color inner = Color.Lerp(skin, EarLining, EarPink) / ScalpShade;
            DrawMeshes(p, p.ears, piece.rest.Length, solid, position, yaw, size, tint, null, default(Color), e.inner, inner);
        }

        // Lays the ears' keys on their rest pose and swings them on their joints, then carries them on the head bone.
        private void PoseEars(Posed p, Ears e, EarState s, bool goneL, bool goneR)
        {
            PieceMeshes m = p.ears;
            EarFit f = FitOf(e, p.shape);
            Matrix4x4 head = p.bones[headBone];
            for (int side = 0; side < 2; side++)
            {
                Vector3[] joints = f.joints[side];
                if (side == 0 ? goneL : goneR)
                {
                    // Closed to its root, as a cut limb is to its stump.
                    Matrix4x4 gone = head * Matrix4x4.Translate(joints[0]) * Matrix4x4.Scale(Vector3.zero) * Matrix4x4.Translate(-joints[0]);
                    for (int j = 0; j < e.share.Length; j++)
                    {
                        earBones[side * EarMostBones + j] = gone;
                    }
                    continue;
                }
                Matrix4x4 chain = head;
                float back = s.back[side] + s.low[side] * EarFlatBack + s.up[side] * EarPerkBack;
                float down = s.down[side] + s.low[side] * EarFlatDown;
                for (int j = 0; j < e.share.Length; j++)
                {
                    Quaternion turn = e.flops
                        ? Quaternion.AngleAxis(s.swing[side][j] * e.share[j], f.axisSwing[side][j]) * Quaternion.AngleAxis(-s.curl[side][j] * f.curlTurn[side][j], f.axisCurl[side][j])
                        : Quaternion.AngleAxis(back * e.share[j], f.axisBack[side]) * Quaternion.AngleAxis(down * e.share[j], f.axisDown[side]);
                    chain = chain * TurnAbout(joints[j], turn);
                    earBones[side * EarMostBones + j] = chain;
                }
            }
            float upL = s.up[0], upR = s.up[1], lowL = s.low[0], lowR = s.low[1];
            Vector3[] rest = f.rest, baseNormal = e.baseShape.normal, baseHull = e.baseShape.hull;
            Vector3[][] kr = f.keyRest, kn = e.keyNormal, kh = e.keyHull;
            for (int i = 0; i < rest.Length; i++)
            {
                Vector3 r = rest[i] + kr[0][i] * upL + kr[1][i] * upR + kr[2][i] * lowL + kr[3][i] * lowR;
                Vector3 nr = baseNormal[i] + kn[0][i] * upL + kn[1][i] * upR + kn[2][i] * lowL + kn[3][i] * lowR;
                Vector3 hl = baseHull[i] + kh[0][i] * upL + kh[1][i] * upR + kh[2][i] * lowL + kh[3][i] * lowR;
                Vector3 v = Vector3.zero, n = Vector3.zero, h = Vector3.zero;
                for (int k = e.firstWeight[i]; k < e.firstWeight[i + 1]; k++)
                {
                    Matrix4x4 b = earBones[e.weightBone[k]];
                    float w = e.weight[k];
                    v += b.MultiplyPoint3x4(r) * w;
                    n += b.MultiplyVector(nr) * w;
                    h += b.MultiplyVector(hl) * w;
                }
                m.verts[i] = v;
                m.normals[i] = n;
                m.hulled[i] = v + h;
            }
            m.solid.vertices = m.verts;
            m.solid.normals = m.normals;
            m.outline.vertices = m.hulled;
        }

        private static void RetireEars(Posed p)
        {
            if (p.ears != null)
            {
                Api.RetireMesh(p.ears.solid);
                Api.RetireMesh(p.ears.outline);
            }
        }
    }

    public static partial class WalkFigures
    {
        private const float EarStep = 1f / 60f;       // game seconds a step of the spring
        private const int EarMostSteps = 6;           // at the game's fast speeds the rest are dropped
        private const float EarSpring = 700f;         // its pull, per second squared per degree: about 4 swings a second
        private const float EarDamp = 16f;            // its slowing, per second
        private const float EarDrag = 3.5f;           // degrees an ear's tip is swept back per cell a second its root moves forward: a walk is about 4.6
        private const float EarBob = 18f;             // and left below per cell a second it moves up: the head's bob is slow, but quick to turn round
        private const float EarBackMost = 30f, EarDownMost = 25f;   // degrees, either way, the head's motion can swing it
        private const float EarFastest = 12f;         // cells a second: faster than this the pawn was moved, not walking
        private const float EarMoodEase = 3f;         // of a key, per game second
        private const float EarFlatEase = 12f;        // the same, going flat as it is hit
        private const int EarHitTicks = 90;           // ticks a hit keeps its ears flat
        private const float EarRestFrom = 3f, EarRestTo = 13f;   // game seconds between one ear's flicks
        private const float EarAsleep = 4f;           // and how many times longer those rests are asleep
        // A flick: swung back with the tip out a little, held, and let go.
        private const float FlickBack = 34f, FlickDown = 11f;
        private const float FlickIn = 0.17f, FlickHold = 0.55f, FlickOut = 0.2f;   // game seconds
        // Every third is a quick twitch instead, there and back.
        private const float TwitchBack = 16f, TwitchIn = 0.06f, TwitchOut = 0.1f;
        private const uint EarSeed = 0x4D656F77;
        // Floppy ears. Curl: + straighter, - tighter, as SkinnedFigure.FlopLoose has it; at 1 it would stand straight up.
        private const float FlopPerk = 0.55f;         // at the up key's full: drafted takes 0.7 of it, and the ear stands out level
        private const float FlopTuck = 0.24f;         // tighter at the down key's full, in against the head
        private const float FlopTightest = -0.27f, FlopStraightest = 0.75f;   // what a spring's overshoot may reach: past the first it goes through the hair
        private const float FlopTuckBack = 22f;       // degrees swung back at the down key's full, laid along the head
        private const float FlopLift = 0.03f;         // curl per cell a second forward: the air lifts them
        private const float FlopLiftMost = 0.25f;
        private const float FlopBob = 0.3f, FlopBobMost = 0.2f;     // per cell a second the head moves down: they float up as it drops
        private const float FlopSide = 0.05f, FlopSideMost = 0.25f; // per cell a second sideways, away from that ear: a quick turn flings them out
        private const float FlopDrag = 4f, FlopDragMost = 35f;      // degrees swung back per cell a second forward
        private const float FlopSpring = 420f, FlopDamp = 18f;      // the root's spring, as EarSpring and EarDamp: slower than a standing ear's
        private const float FlopSoften = 0.7f;        // each joint on from there has this of both: about 3 swings a second at the root, 2 at the tip
        // A flap in place of a flick: thrown up and dropped at once, the springs then flop it. A twitch only lifts it a little.
        private const float FlapCurl = 0.35f, FlapBack = 10f, FlapIn = 0.09f, FlapOut = 0.22f;
        private const float TwitchCurl = 0.12f;

        private static GeneDef catEarsGene, pigEarsGene, floppyEarsGene;
        private static bool earGenesLooked;

        // The file of the ears this pawn's genes give it; null for none, or if the export hasn't them.
        private static string EarFileOf(Pawn pawn)
        {
            if (!earGenesLooked)
            {
                earGenesLooked = true;
                catEarsGene = DefDatabase<GeneDef>.GetNamedSilentFail("Ears_Cat");
                pigEarsGene = DefDatabase<GeneDef>.GetNamedSilentFail("Ears_Pig");
                floppyEarsGene = DefDatabase<GeneDef>.GetNamedSilentFail("Ears_Floppy");
            }
            if (pawn.genes == null)
            {
                return null;
            }
            string file = catEarsGene != null && pawn.genes.HasActiveGene(catEarsGene) ? SkinnedFigure.CatEarFile
                : pigEarsGene != null && pawn.genes.HasActiveGene(pigEarsGene) ? SkinnedFigure.PigEarFile
                : floppyEarsGene != null && pawn.genes.HasActiveGene(floppyEarsGene) ? SkinnedFigure.FloppyEarFile : null;
            return file != null && skin.HasEars(file) ? file : null;
        }

        // How the pawn's state holds its ears: the up key's and the down key's amounts.
        private static void EarMood(Pawn pawn, out float up, out float down, out bool flat)
        {
            up = down = 0f;
            flat = false;
            if (pawn.Dead)
            {
                down = 1f;
                return;
            }
            Hit hit;
            if (hits.TryGetValue(pawn.thingIDNumber, out hit) && Find.TickManager.TicksGame - hit.tick < EarHitTicks)
            {
                down = 1f;
                flat = true;
                return;
            }
            if (pawn.Downed && !pawn.DevelopmentalStage.Baby())
            {
                down = 0.75f;
                return;
            }
            if (!pawn.Awake())
            {
                down = 0.45f;
                return;
            }
            if (pawn.InMentalState)
            {
                down = pawn.InAggroMentalState ? 1f : 0.55f;
                return;
            }
            if (pawn.stances != null && (pawn.stances.stunner.Stunned || pawn.stances.stagger.Staggered))
            {
                down = 0.8f;
                return;
            }
            if (pawn.Drafted)
            {
                up = 0.7f;
                return;
            }
            Need_Mood mood = pawn.needs != null ? pawn.needs.mood : null;
            if (mood != null)
            {
                down = Mathf.Clamp01((0.35f - mood.CurLevel) / 0.25f) * 0.5f;
                up = Mathf.Clamp01((mood.CurLevel - 0.7f) / 0.3f) * 0.25f;
            }
        }

        // A seed for one ear's next flick: the same every time for that pawn, ear and flick.
        private static float EarRoll(int id, int side, int count)
        {
            uint h = Mix(Mix((uint)id ^ EarSeed) + (uint)(side * 2 + 1) * 0x9E3779B9u + (uint)count);
            return h / (float)uint.MaxValue;
        }

        // How far a flick has swung the ear, 0 .. 1, since game seconds after it started; twitch: the quick kind.
        private static float Flick(float since, bool twitch)
        {
            return twitch ? Flick(since, TwitchIn, 0f, TwitchOut) : Flick(since, FlickIn, FlickHold, FlickOut);
        }

        private static float Flick(float since, float into, float hold, float leave)
        {
            if (since < into)
            {
                return Mathf.SmoothStep(0f, 1f, since / into);
            }
            return since < into + hold ? 1f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((since - into - hold) / leave));
        }

        // Moves the ears on from their last draw, by game time, so they stop with the game paused.
        private static void StepEars(Pawn pawn, SkinnedFigure.Posed fig, string file, Vector3 at, float yaw, float size)
        {
            SkinnedFigure.EarState s = fig.earState;
            if (s == null)
            {
                s = new SkinnedFigure.EarState();
                fig.earState = s;
            }
            float up, down;
            bool flat;
            EarMood(pawn, out up, out down, out flat);
            bool moving = !pawn.Dead;   // a corpse's lie still
            bool asleep = !pawn.Awake() || pawn.Downed;
            float seconds = Time.deltaTime * Find.TickManager.TickRateMultiplier;
            bool fresh = s.frame < 0 || Time.frameCount - s.frame > 2;
            s.frame = Time.frameCount;
            Quaternion facing = Quaternion.AngleAxis(yaw, Vector3.up);
            int id = pawn.thingIDNumber;
            bool floppy = file == SkinnedFigure.FloppyEarFile;
            int joints = floppy ? skin.EarBoneCount(file) : 0;
            for (int side = 0; side < 2; side++)
            {
                Vector3 root;
                Quaternion turn;
                skin.EarRoot(fig, file, side, out root, out turn);
                Vector3 place = at + facing * (root * size);
                Vector3 moved = place - s.wasAt[side];
                s.wasAt[side] = place;
                if (fresh)
                {
                    s.up[side] = up;
                    s.low[side] = down;
                    s.flickAt[side] = Mathf.Lerp(EarRestFrom, EarRestTo, EarRoll(id, side, 0));
                    s.since[side] = s.flickAt[side] * EarRoll(id, side, 1);
                    s.back[side] = s.down[side] = s.backSpeed[side] = s.downSpeed[side] = 0f;
                    for (int j = 0; j < joints; j++)
                    {
                        s.curl[side][j] = up * FlopPerk - down * FlopTuck;
                        s.swing[side][j] = down * FlopTuckBack;
                        s.curlSpeed[side][j] = s.swingSpeed[side][j] = 0f;
                    }
                    continue;
                }
                float easing = (flat ? EarFlatEase : EarMoodEase) * seconds;
                s.up[side] = Mathf.MoveTowards(s.up[side], up, easing);
                s.low[side] = Mathf.MoveTowards(s.low[side], down, easing);

                Vector3 speed = Vector3.zero;
                if (seconds > 0f)
                {
                    // In the head's own frame, in cells a second of the figure's standard size.
                    speed = Quaternion.Inverse(facing * turn) * (moved / Time.deltaTime / Find.TickManager.TickRateMultiplier);
                    if (speed.sqrMagnitude >= EarFastest * EarFastest)
                    {
                        speed = Vector3.zero;
                    }
                }
                float wantBack = Mathf.Clamp(speed.z * EarDrag, -EarBackMost, EarBackMost);
                float wantDown = Mathf.Clamp(speed.y * EarBob, -EarDownMost, EarDownMost);
                s.since[side] += seconds;
                bool twitch = s.flicks[side] % 3 == 2;
                float flick = 0f;
                if (moving && s.since[side] >= s.flickAt[side])
                {
                    float into = s.since[side] - s.flickAt[side];
                    if (into > (twitch ? TwitchIn + TwitchOut : FlickIn + FlickHold + FlickOut))
                    {
                        s.flicks[side]++;
                        s.since[side] = 0f;
                        s.flickAt[side] = Mathf.Lerp(EarRestFrom, EarRestTo, EarRoll(id, side, s.flicks[side])) * (asleep ? EarAsleep : 1f);
                    }
                    else
                    {
                        flick = floppy && !twitch ? Flick(into, FlapIn, 0f, FlapOut) : Flick(into, twitch);
                    }
                }
                if (floppy)
                {
                    StepFlop(s, side, joints, speed, flick, twitch, seconds);
                    continue;
                }
                wantBack += flick * (twitch ? TwitchBack : FlickBack);
                wantDown += flick * (twitch ? 0f : FlickDown);
                if (seconds > 0f)
                {
                    float carried = Mathf.Min(s.carry + seconds, EarMostSteps * EarStep);
                    for (; carried >= EarStep; carried -= EarStep)
                    {
                        s.backSpeed[side] += ((wantBack - s.back[side]) * EarSpring - s.backSpeed[side] * EarDamp) * EarStep;
                        s.back[side] += s.backSpeed[side] * EarStep;
                        s.downSpeed[side] += ((wantDown - s.down[side]) * EarSpring - s.downSpeed[side] * EarDamp) * EarStep;
                        s.down[side] += s.downSpeed[side] * EarStep;
                    }
                    if (side == 1)
                    {
                        s.carry = carried;
                    }
                }
            }
        }

        // A floppy ear's curl and swing on from what it wants now, each joint on its own spring. Springs that each followed the
        // joint before would lag further, but multiply one another's swing at their shared speed until the tip thrashes.
        private static void StepFlop(SkinnedFigure.EarState s, int side, int joints, Vector3 speed, float flick, bool twitch, float seconds)
        {
            float away = side == 0 ? speed.x : -speed.x;   // the left ear is on -x, and left behind toward it going +x
            float wantCurl = s.up[side] * FlopPerk - s.low[side] * FlopTuck
                + Mathf.Min(Mathf.Abs(speed.z) * FlopLift, FlopLiftMost)
                + Mathf.Clamp(-speed.y * FlopBob, -FlopBobMost, FlopBobMost)
                + Mathf.Clamp(away * FlopSide, -FlopSideMost, FlopSideMost)
                + flick * (twitch ? TwitchCurl : FlapCurl);
            float wantSwing = s.low[side] * FlopTuckBack
                + Mathf.Clamp(speed.z * FlopDrag, -FlopDragMost, FlopDragMost)
                + flick * (twitch ? 0f : FlapBack);
            if (seconds <= 0f)
            {
                return;
            }
            float[] curl = s.curl[side], curlSpeed = s.curlSpeed[side], swing = s.swing[side], swingSpeed = s.swingSpeed[side];
            float carried = Mathf.Min(s.carry + seconds, EarMostSteps * EarStep);
            for (; carried >= EarStep; carried -= EarStep)
            {
                float soft = 1f;
                for (int j = 0; j < joints; j++, soft *= FlopSoften)
                {
                    curlSpeed[j] += ((wantCurl - curl[j]) * FlopSpring - curlSpeed[j] * FlopDamp) * soft * EarStep;
                    curl[j] += curlSpeed[j] * EarStep;
                    if (curl[j] < FlopTightest || curl[j] > FlopStraightest)
                    {
                        // Stopped there, not bounced off.
                        curl[j] = Mathf.Clamp(curl[j], FlopTightest, FlopStraightest);
                        curlSpeed[j] = 0f;
                    }
                    swingSpeed[j] += ((wantSwing - swing[j]) * FlopSpring - swingSpeed[j] * FlopDamp) * soft * EarStep;
                    swing[j] += swingSpeed[j] * EarStep;
                }
            }
            if (side == 1)
            {
                s.carry = carried;
            }
        }

        // The ears for a pawn with the gene, after the hair. Each is left off with the ear it stands for gone.
        private static void DrawGeneEars(Pawn pawn, SkinnedFigure.Posed fig, string file, GarmentMask mask, Apparel[] worn, Vector3 at, float yaw, float size, Color skinColour)
        {
            StepEars(pawn, fig, file, at, yaw, size);
            if (EarHiding != EarHiding.Never && skin.HidesEars(mask, worn, EarHiding == EarHiding.Helmets))
            {
                return;
            }
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
            Color tint = file == SkinnedFigure.PigEarFile ? skinColour : pawn.story.HairColor;
            skin.DrawEars(fig, file, fig.earState, goneL, goneR, at, yaw, size, tint, skinColour);
        }
    }
}
