using System;
using System.Collections.Generic;
using System.IO;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A cat tail for the Tail_Furry and Tail_Smooth genes, on the skinned figure: Models/chibi/tail.txt, the rig's own tail as
    // anim_scripts/export_cat.py writes it, or for Tail_Furry tail_furry.txt, the same tail shorter and tufted (fur_tail.py). It rides the hips bone, in the hair colour, and is a chain of joints that bend
    // on springs (StepTail): each follows the one before it, so the tail trails behind a walk, swings out in a turn and
    // sways a little at rest. The springs are drawing only, so they take no game random numbers, and stop with the game paused.
    // Lying, it is kept above what the pawn lies on (OnFloor), and on the ground it also droops onto it and lies out to one side.
    public sealed partial class SkinnedFigure
    {
        private const string TailFile = "tail.txt", FurTailFile = "tail_furry.txt", TailBoneName = "spine";
        private const int TailProbeJoint = 3;   // the joint whose motion drives the tail: far enough along to feel a turn
        private const float TailFloorClear = 0.03f;   // model units its middle is kept off the floor: about its thickness
        private const float TailDroop = 0.5f;         // of a bone's length, how far each bone falls lying on the ground, till it is on it
        private const float TailLieOut = 50f;         // degrees it is swung out from the legs lying on the ground
        private const float TailOutEither = 0.25f;    // of that: the pawn's own side, all there is to go by flat on the back
        private const float TailOutBack = 1.5f;       // and per sine of the angle to the way its back faces: on its side, toward its back

        // One pawn's tail as it is now.
        public sealed class TailState
        {
            public readonly float[] side, sideSpeed, pitch, pitchSpeed;   // degrees each joint is turned, and a second
            public Vector3 wasAt;   // where the probe joint was last draw, true space (cells)
            public float carry;     // game seconds not yet stepped
            public float time;      // game seconds it has swayed
            public float laid;      // how far it is draped as on the ground, 0 .. 1
            public float outSide;   // the side it lies out to, +1 or -1
            public int frame = -1;  // the frame it was last drawn in

            public TailState(int bones)
            {
                side = new float[bones];
                sideSpeed = new float[bones];
                pitch = new float[bones];
                pitchSpeed = new float[bones];
            }
        }

        // The tail from its file, and each shape's fit of it.
        private sealed class Tail
        {
            public Piece piece;
            public Vector3[] joints;   // joint j is where bone j turns, the last the tip
            public int[] firstWeight;  // vertex i's weights are weightBone/weight[firstWeight[i] .. firstWeight[i + 1])
            public int[] weightBone;
            public float[] weight;
            public PieceShape baseShape;
            public Matrix4x4[] bones;
            public readonly Dictionary<Shape, TailFit> fits = new Dictionary<Shape, TailFit>();
        }

        private sealed class TailFit
        {
            public Vector3[] rest;
            public Vector3[] joints;
        }

        private Tail tail, furTail;
        private bool tailTried, furTailTried;
        private int hipsBone = -2;

        // The smooth tail, read the first time a pawn with the gene is drawn; null if an export has none.
        private Tail TailOf()
        {
            if (!tailTried)
            {
                tailTried = true;
                tail = LoadTail(TailFile);
            }
            return tail;
        }

        // The tail this pawn has (Posed.furTail): an export without the furry one gives it the smooth one.
        private Tail TailOf(Posed p)
        {
            if (!p.furTail)
            {
                return TailOf();
            }
            if (!furTailTried)
            {
                furTailTried = true;
                furTail = LoadTail(FurTailFile);
            }
            return furTail ?? TailOf();
        }

        private Tail LoadTail(string file)
        {
            string path = Path.Combine(folder, file);
            if (!File.Exists(path) || !boneIndex.TryGetValue(TailBoneName, out hipsBone))
            {
                return null;
            }
            try
            {
                return ReadTail(path);
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not read the cat tail in " + path + ", it is left off: " + e.Message);
                return null;
            }
        }

        private Tail ReadTail(string path)
        {
            Tail t = new Tail();
            t.piece = ReadMesh(path, false, null);
            if (turnRound)
            {
                TurnRound(t.piece.tris);
            }
            int n = t.piece.rest.Length;
            if (n == 0)
            {
                throw new InvalidDataException(path + ": no vertices");
            }
            SortedDictionary<int, Vector3> joints = new SortedDictionary<int, Vector3>();
            List<int>[] bones = new List<int>[n];
            List<float>[] weights = new List<float>[n];
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0)
                {
                    continue;
                }
                if (p[0] == "tail")
                {
                    joints[int.Parse(p[1])] = Vec(p, 2);
                }
                else if (p[0] == "tw")
                {
                    int v = int.Parse(p[1]);
                    bones[v] = new List<int>();
                    weights[v] = new List<float>();
                    for (int i = 2; i + 1 < p.Length; i += 2)
                    {
                        bones[v].Add(int.Parse(p[i]));
                        weights[v].Add(Num(p[i + 1]));
                    }
                }
            }
            if (joints.Count < 2)
            {
                throw new InvalidDataException(path + ": fewer than two joints");
            }
            t.joints = new Vector3[joints.Count];
            foreach (KeyValuePair<int, Vector3> j in joints)
            {
                t.joints[j.Key] = j.Value;
            }
            List<int> allBones = new List<int>();
            List<float> allWeights = new List<float>();
            t.firstWeight = new int[n + 1];
            for (int v = 0; v < n; v++)
            {
                t.firstWeight[v] = allBones.Count;
                if (bones[v] == null || bones[v].Count == 0)
                {
                    throw new InvalidDataException(path + ": vertex " + v + " has no weights");
                }
                for (int k = 0; k < bones[v].Count; k++)
                {
                    if (bones[v][k] < 0 || bones[v][k] >= t.joints.Length - 1)
                    {
                        throw new InvalidDataException(path + ": vertex " + v + " is weighted to bone " + bones[v][k] + ", which is not there");
                    }
                }
                allBones.AddRange(bones[v]);
                allWeights.AddRange(weights[v]);
            }
            t.firstWeight[n] = allBones.Count;
            t.weightBone = allBones.ToArray();
            t.weight = allWeights.ToArray();
            t.baseShape = ShapePiece(t.piece, new List<KeyValuePair<string, float>>());
            t.bones = new Matrix4x4[t.joints.Length - 1];
            return t;
        }

        // How many bones this pawn's tail turns about, 0 if the export has none.
        public int TailBones(Posed p)
        {
            Tail t = TailOf(p);
            return t == null ? 0 : t.joints.Length - 1;
        }

        public bool HasTail
        {
            get { return TailOf() != null; }
        }

        // The tail on this shape: moved with the hips' joint, as the body's keys move it.
        private TailFit TailFitOf(Tail t, Shape s)
        {
            TailFit f;
            if (t.fits.TryGetValue(s, out f))
            {
                return f;
            }
            Vector3 move = s.boneMove[hipsBone];
            f = new TailFit { rest = new Vector3[t.piece.rest.Length], joints = new Vector3[t.joints.Length] };
            for (int i = 0; i < f.rest.Length; i++)
            {
                f.rest[i] = t.piece.rest[i] + move;
            }
            for (int j = 0; j < f.joints.Length; j++)
            {
                f.joints[j] = t.joints[j] + move;
            }
            t.fits[s] = f;
            return f;
        }

        // Where the probe joint is on the figure as End left it (model units), and how the hips have turned from rest.
        public void TailProbe(Posed p, out Vector3 point, out Quaternion turn)
        {
            point = Vector3.zero;
            turn = Quaternion.identity;
            Tail t = TailOf(p);
            if (t == null)
            {
                return;
            }
            Matrix4x4 hips = p.whole[hipsBone];
            point = hips.MultiplyPoint3x4(TailFitOf(t, p.shape).joints[Mathf.Min(TailProbeJoint, t.joints.Length - 2)]);
            turn = hips.rotation;
        }

        // Draws the tail as the state has it, in the hair's colour. floor: what it must stay above (model units, as the
        // figure is posed); negative infinity for nothing.
        public void DrawTail(Posed p, TailState s, float floor, Vector3 position, float yaw, float size, Color tint)
        {
            Tail t = TailOf(p);
            if (t == null)
            {
                return;
            }
            Material solid = SolidMaterial();
            if (solid == null)
            {
                return;
            }
            if (p.tail == null || p.tailMadeFur != p.furTail)
            {
                RetireTail(p);   // the gene changed: the other tail's meshes won't fit
                p.tail = NewPieceMeshes(t.piece);
                p.tailMadeFur = p.furTail;
            }
            PoseTail(p, t, s, floor);
            DrawMeshes(p, p.tail, t.piece.rest.Length, solid, position, yaw, size, tint);
        }

        // Turns each bone about its joint, down the chain from the hips, and skins the tail to them.
        private void PoseTail(Posed p, Tail t, TailState s, float floor)
        {
            PieceMeshes m = p.tail;
            TailFit f = TailFitOf(t, p.shape);
            Matrix4x4 chain = p.bones[hipsBone];
            if (s.laid > 0f)
            {
                chain = LaidOut(chain, f, s);
            }
            for (int j = 0; j < t.bones.Length; j++)
            {
                Quaternion turn = Quaternion.AngleAxis(s.side[j], Vector3.up) * Quaternion.AngleAxis(s.pitch[j], Vector3.right);
                chain = chain * TurnAbout(f.joints[j], turn);
                if (s.laid > 0f || !float.IsNegativeInfinity(floor))
                {
                    chain = OnFloor(chain, p.bones[hipsBone], f.joints[j], f.joints[j + 1], floor, s.laid);
                }
                t.bones[j] = chain;
            }
            Vector3[] baseNormal = t.baseShape.normal, baseHull = t.baseShape.hull;
            for (int i = 0; i < f.rest.Length; i++)
            {
                Vector3 v = Vector3.zero, n = Vector3.zero, h = Vector3.zero;
                for (int k = t.firstWeight[i]; k < t.firstWeight[i + 1]; k++)
                {
                    Matrix4x4 b = t.bones[t.weightBone[k]];
                    float w = t.weight[k];
                    v += b.MultiplyPoint3x4(f.rest[i]) * w;
                    n += b.MultiplyVector(baseNormal[i]) * w;
                    h += b.MultiplyVector(baseHull[i]) * w;
                }
                m.verts[i] = v;
                m.normals[i] = n;
                m.hulled[i] = v + h;
            }
            m.solid.vertices = m.verts;
            m.solid.normals = m.normals;
            m.outline.vertices = m.hulled;
        }

        // The tail swung about the upright at its root, out from the legs. A share by the pawn's own side and the rest by
        // where its back faces, so that the swing changes smoothly as a ragdoll rolls over.
        private static Matrix4x4 LaidOut(Matrix4x4 hips, TailFit f, TailState s)
        {
            Vector3 root = hips.MultiplyPoint3x4(f.joints[0]);
            Vector3 way = hips.MultiplyVector(f.joints[f.joints.Length - 1] - f.joints[0]);
            way.y = 0f;
            Vector3 back = hips.MultiplyVector(Vector3.back);
            back.y = 0f;
            float share = s.outSide * TailOutEither + Vector3.Cross(way.normalized, back.normalized * Mathf.Min(1f, back.magnitude)).y * TailOutBack;
            return TurnAbout(root, Quaternion.AngleAxis(Mathf.Clamp(share, -1f, 1f) * TailLieOut * s.laid, Vector3.up)) * hips;
        }

        // A bone of the tail tipped up or down about its joint, the least way, so that it ends where it should: as far
        // down as it droops (by laid), but not into the floor. What is further along the chain goes with it.
        private static Matrix4x4 OnFloor(Matrix4x4 chain, Matrix4x4 hips, Vector3 from, Vector3 to, float floor, float laid)
        {
            Vector3 root = chain.MultiplyPoint3x4(from);
            Vector3 way = chain.MultiplyPoint3x4(to) - root;
            float length = way.magnitude;
            float drop = Mathf.Lerp(way.y, Mathf.Min(way.y, -length * TailDroop), laid);
            float wanted = Mathf.Min(Mathf.Max(drop, floor + TailFloorClear - root.y), length);
            if (Mathf.Abs(wanted - way.y) < 0.0001f)
            {
                return chain;
            }
            Vector3 flat = new Vector3(way.x, 0f, way.z);
            if (flat.sqrMagnitude < 0.000001f)
            {
                // Straight up or down: along the way the legs go, or any way at all if they go straight up or down too.
                flat = hips.MultiplyVector(Vector3.down);
                flat.y = 0f;
                if (flat.sqrMagnitude < 0.000001f)
                {
                    flat = Vector3.forward;
                }
            }
            Vector3 now = flat.normalized * Mathf.Sqrt(Mathf.Max(0f, length * length - wanted * wanted)) + Vector3.up * wanted;
            return TurnAbout(root, Quaternion.FromToRotation(way, now)) * chain;
        }

        private static void RetireTail(Posed p)
        {
            if (p.tail != null)
            {
                DynamicModels.Retire(p.tail.solid);
                DynamicModels.Retire(p.tail.outline);
            }
        }
    }

    public static partial class WalkFigures
    {
        private const float TailStep = 1f / 60f;       // game seconds a step of the spring
        private const int TailMostSteps = 6;           // at the game's fast speeds the rest are dropped
        private const float TailSpring = 150f;         // each joint's pull toward its target, per second squared per degree
        private const float TailDamp = 9f;             // its slowing, per second
        private const float TailChain = 0.6f;          // the share of the joint before it that a joint follows
        private const float TailJointMost = 28f;       // degrees one joint can be turned, either way
        private const float TailSideDrag = 2.2f;       // degrees the root is swept sideways per cell a second the tail moves across
        private const float TailForwardLift = 1.6f;    // and lifted per cell a second forward: a walk is about 4.6
        private const float TailBob = 8f;              // and let down per cell a second the hips rise
        private const float TailDriveMost = 14f;       // degrees, either way, the pawn's motion can turn the root
        private const float TailFastest = 12f;         // cells a second: faster than this the pawn was moved, not walking
        private const float TailSwayAmp = 5f;          // degrees the root sways to either side at rest
        private const float TailSwayHz = 0.27f;        // sways a second
        private const float TailSitLift = 22f;         // degrees the root is lifted sitting, so the tail clears the seat
        private const float TailLayEase = 0.6f;        // game seconds to drape on the ground as it falls, or gather up as it rises
        private static readonly string[] TailGenes = { "Tail_Furry", "Tail_Smooth" };

        private static GeneDef[] tailGenes;
        private static GeneDef furTailGene;

        private static bool HasCatTail(Pawn pawn)
        {
            if (tailGenes == null)
            {
                List<GeneDef> found = new List<GeneDef>();
                foreach (string name in TailGenes)
                {
                    GeneDef def = DefDatabase<GeneDef>.GetNamedSilentFail(name);
                    if (def != null)
                    {
                        found.Add(def);
                    }
                }
                tailGenes = found.ToArray();
                furTailGene = DefDatabase<GeneDef>.GetNamedSilentFail("Tail_Furry");
            }
            if (pawn.genes == null || !skin.HasTail)
            {
                return false;
            }
            foreach (GeneDef def in tailGenes)
            {
                if (pawn.genes.HasActiveGene(def))
                {
                    return true;
                }
            }
            return false;
        }

        // How the pawn's state holds its tail: how much it sways (0 still, 1 a pawn at ease) and the turn of the root
        // up (+) or down (-), degrees.
        private static void TailMood(Pawn pawn, out float sway, out float lift)
        {
            sway = 1f;
            lift = 0f;
            if (pawn.Dead)
            {
                sway = 0f;
                return;
            }
            if (pawn.Downed || !pawn.Awake())
            {
                sway = 0.2f;
                lift = -8f;
                return;
            }
            if (pawn.InMentalState)
            {
                sway = 1.6f;
                lift = pawn.InAggroMentalState ? 14f : -6f;
                return;
            }
            if (pawn.Drafted)
            {
                sway = 0.5f;
                lift = 10f;
                return;
            }
            Need_Mood mood = pawn.needs != null ? pawn.needs.mood : null;
            if (mood != null)
            {
                lift = Mathf.Lerp(-8f, 6f, Mathf.InverseLerp(0.25f, 0.9f, mood.CurLevel));
                sway = Mathf.Lerp(0.6f, 1.4f, Mathf.InverseLerp(0.25f, 0.9f, mood.CurLevel));
            }
        }

        // Moves the tail on from its last draw, by game time. sat: how far the pawn is seated, 0 .. 1. onGround: lying on the
        // ground, so draped on it.
        private static void StepTail(Pawn pawn, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, float sat, bool onGround)
        {
            int n = skin.TailBones(fig);
            SkinnedFigure.TailState s = fig.tailState;
            if (s == null || s.side.Length != n)
            {
                s = new SkinnedFigure.TailState(n);
                fig.tailState = s;
            }
            float sway, lift;
            TailMood(pawn, out sway, out lift);
            lift += sat * TailSitLift;
            float seconds = Time.deltaTime * Find.TickManager.TickRateMultiplier;
            bool fresh = s.frame < 0 || Time.frameCount - s.frame > 2;
            s.frame = Time.frameCount;
            Quaternion facing = Quaternion.AngleAxis(yaw, Vector3.up);
            Vector3 probe;
            Quaternion turn;
            skin.TailProbe(fig, out probe, out turn);
            Vector3 place = at + facing * (probe * size);
            Vector3 moved = place - s.wasAt;
            s.wasAt = place;
            uint id = Mix((uint)pawn.thingIDNumber ^ EarSeed);
            if (fresh)
            {
                // Started in a sway of its own, so a row of pawns does not wag together.
                s.time = id / (float)uint.MaxValue / TailSwayHz;
                s.outSide = (id & 1u) == 0u ? 1f : -1f;
                s.laid = onGround ? 1f : 0f;
                for (int k = 0; k < n; k++)
                {
                    s.side[k] = s.sideSpeed[k] = s.pitch[k] = s.pitchSpeed[k] = 0f;
                }
                return;
            }
            float wantSide = 0f, wantPitch = 0f;
            if (seconds > 0f)
            {
                // In the hips' own frame, in cells a second of the figure's standard size. Lagging behind a move: across
                // it, and lifted by going forward.
                Vector3 speed = Quaternion.Inverse(facing * turn) * (moved / Time.deltaTime / Find.TickManager.TickRateMultiplier);
                if (speed.sqrMagnitude < TailFastest * TailFastest)
                {
                    wantSide = Mathf.Clamp(speed.x * TailSideDrag, -TailDriveMost, TailDriveMost);
                    wantPitch = Mathf.Clamp(speed.z * TailForwardLift - speed.y * TailBob, -TailDriveMost, TailDriveMost);
                }
            }
            s.time += seconds;
            s.laid = Mathf.MoveTowards(s.laid, onGround ? 1f : 0f, seconds / TailLayEase);
            float phase = s.time * TailSwayHz * 2f * Mathf.PI;
            wantSide += Mathf.Sin(phase) * TailSwayAmp * sway;
            wantPitch += lift + Mathf.Sin(phase * 0.5f + 1.3f) * TailSwayAmp * 0.4f * sway;
            if (seconds <= 0f)
            {
                return;
            }
            float carried = Mathf.Min(s.carry + seconds, TailMostSteps * TailStep);
            for (; carried >= TailStep; carried -= TailStep)
            {
                for (int k = 0; k < n; k++)
                {
                    float toSide = k == 0 ? wantSide : s.side[k - 1] * TailChain;
                    float toPitch = k == 0 ? wantPitch : s.pitch[k - 1] * TailChain;
                    s.sideSpeed[k] += ((toSide - s.side[k]) * TailSpring - s.sideSpeed[k] * TailDamp) * TailStep;
                    s.side[k] = Mathf.Clamp(s.side[k] + s.sideSpeed[k] * TailStep, -TailJointMost, TailJointMost);
                    s.pitchSpeed[k] += ((toPitch - s.pitch[k]) * TailSpring - s.pitchSpeed[k] * TailDamp) * TailStep;
                    s.pitch[k] = Mathf.Clamp(s.pitch[k] + s.pitchSpeed[k] * TailStep, -TailJointMost, TailJointMost);
                }
            }
            s.carry = carried;
        }

        // The tail for a pawn with the gene, after the ears. floor: what it stays above (model units, negative infinity for
        // nothing). onGround: lying on the ground (see StepTail). Under body armour (mask, with the option on) it is left
        // off, though still stepped, so it doesn't jump when the armour comes off.
        private static void DrawCatTail(Pawn pawn, SkinnedFigure.Posed fig, GarmentMask mask, Apparel[] worn, Vector3 at, float yaw, float size, float sat, float floor, bool onGround)
        {
            fig.furTail = furTailGene != null && pawn.genes.HasActiveGene(furTailGene);
            StepTail(pawn, fig, at, yaw, size, sat, onGround);
            if (ArmourHidesTail && skin.HidesTail(mask, worn))
            {
                return;
            }
            skin.DrawTail(fig, fig.tailState, floor, at, yaw, size, pawn.story.HairColor);
        }
    }
}
