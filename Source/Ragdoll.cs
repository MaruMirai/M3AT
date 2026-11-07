using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Ragdolls: a body that goes down where it stands (downed or killed) falls under its own weight rather than only
    // playing the canned fall, and lies as it landed. It is drawn only: the game is not told where the body went, so the
    // hips are held near the pawn's cell (HipsFree, HipsHeld) while the arms and legs go where they fall. A body flung
    // (Flings) is moved by the game on its own ticks, and the hips here follow it (Following).
    //
    // The body is twenty joints (Joint*), each a point with a weight and a size, held apart by the bones between them and
    // stepped on game time (Verlet, StepSeconds a step). Each bone's turn is then worked out from where its joints are
    // (Turns): the pelvis and chest from three of their joints each, a limb from its bend, and a hand or foot from the
    // limb's turn and the way it points, all from the export's ragdoll pose (walk.ragdoll_pose: the turns are that pose's,
    // turned by however far the joints have moved from it). On top of plain bones:
    //   * Soft limits: each joint has a range about that pose (a cone, or an elbow's or knee's hinge), and its movement is
    //     slowed as it nears the edge (SoftDamp over SoftMargin) before it is stopped there, as muscle and tendon would.
    //   * Tone: springs between joints a bone apart draw the body gently back toward the ragdoll pose (ToneLinks;
    //     DownedTone, DeadTone: a corpse is limper), so it settles curled and relaxed, not splayed.
    //   * The hit: the blow that felled it pushes the trunk, and the shoulders more than the hips, while the feet are
    //     held by the ground, so the body is thrown back and its legs swing out under it (Push).
    //   * The fall as it starts: the joints are held to the canned fall's (Pin), let go from the head down, then the legs
    //     (UpperLetGo, LegsLetGo), sooner the harder the hit, so it stumbles before it drops.
    // It comes to rest when nothing has moved faster than SettleSpeed for SettleSteps, and from then is just drawn; a new
    // hit wakes it.
    public sealed partial class SkinnedFigure
    {
        // The joints, by the bone whose head each is (the head's is the middle of the head, the face patch's centre).
        private const int JointHips = 0, JointBelly = 1, JointNeck = 2, JointHead = 3;
        private const int JointArmL = 4, JointArmR = 8, JointLegL = 12, JointLegR = 16;   // each limb's first of four
        private const int Joints = 20;
        private static readonly string[] JointBones =
        {
            "spine", "spine.002", "spine.004", null,
            "upper_arm.L", "forearm.L", "hand.L", "hand.L.002",
            "upper_arm.R", "forearm.R", "hand.R", "hand.R.002",
            "thigh.L", "shin.L", "foot.L", "toe.L",
            "thigh.R", "shin.R", "foot.R", "toe.R",
        };
        // Each joint's weight (a share of nothing in particular: only their ratios count). The chibi's head is heavy.
        private static readonly float[] JointMass =
        {
            4f, 3f, 2.5f, 2.5f,
            1.2f, 0.8f, 0.5f, 0.2f,
            1.2f, 0.8f, 0.5f, 0.2f,
            2f, 1.5f, 0.7f, 0.3f,
            2f, 1.5f, 0.7f, 0.3f,
        };
        // The bones held at their length: the pelvis (hips, both hip joints and the belly) and the chest (the belly, the
        // neck and both shoulders) braced so each keeps its shape, the neck to the head, and each limb's three bones.
        private static readonly int[] Links =
        {
            JointHips, JointBelly, JointHips, JointLegL, JointHips, JointLegR, JointLegL, JointLegR, JointBelly, JointLegL, JointBelly, JointLegR,
            JointBelly, JointNeck, JointNeck, JointArmL, JointNeck, JointArmR, JointArmL, JointArmR, JointBelly, JointArmL, JointBelly, JointArmR,
            JointNeck, JointHead,
            JointArmL, JointArmL + 1, JointArmL + 1, JointArmL + 2, JointArmL + 2, JointArmL + 3,
            JointArmR, JointArmR + 1, JointArmR + 1, JointArmR + 2, JointArmR + 2, JointArmR + 3,
            JointLegL, JointLegL + 1, JointLegL + 1, JointLegL + 2, JointLegL + 2, JointLegL + 3,
            JointLegR, JointLegR + 1, JointLegR + 1, JointLegR + 2, JointLegR + 2, JointLegR + 3,
        };

        // The bones' groups, each turned as one (Turns).
        private const int GroupPelvis = 0, GroupChest = 1, GroupHead = 2;
        private const int GroupArmL = 3, GroupArmR = 6, GroupLegL = 9, GroupLegR = 12;   // upper, lower and end bone
        private const int Groups = 15;

        // Each joint's range about the ragdoll pose: the joint, the one it turns about, the group whose turn carries its
        // place in the pose, and the widest it may turn from the pose (degrees; 0: a hinge, HingeOf).
        private struct Drive
        {
            public int joint, pivot, group;
            public float cone;
        }
        private static readonly Drive[] Drives =
        {
            new Drive { joint = JointNeck, pivot = JointBelly, group = GroupPelvis, cone = 40f },
            new Drive { joint = JointArmL, pivot = JointBelly, group = GroupPelvis, cone = 40f },
            new Drive { joint = JointArmR, pivot = JointBelly, group = GroupPelvis, cone = 40f },
            new Drive { joint = JointHead, pivot = JointNeck, group = GroupChest, cone = 50f },
            new Drive { joint = JointArmL + 1, pivot = JointArmL, group = GroupChest, cone = 110f },
            new Drive { joint = JointArmL + 2, pivot = JointArmL + 1, group = GroupArmL, cone = 0f },
            new Drive { joint = JointArmL + 3, pivot = JointArmL + 2, group = GroupArmL + 1, cone = 70f },
            new Drive { joint = JointArmR + 1, pivot = JointArmR, group = GroupChest, cone = 110f },
            new Drive { joint = JointArmR + 2, pivot = JointArmR + 1, group = GroupArmR, cone = 0f },
            new Drive { joint = JointArmR + 3, pivot = JointArmR + 2, group = GroupArmR + 1, cone = 70f },
            new Drive { joint = JointLegL + 1, pivot = JointLegL, group = GroupPelvis, cone = 65f },
            new Drive { joint = JointLegL + 2, pivot = JointLegL + 1, group = GroupLegL, cone = 0f },
            new Drive { joint = JointLegL + 3, pivot = JointLegL + 2, group = GroupLegL + 1, cone = 45f },
            new Drive { joint = JointLegR + 1, pivot = JointLegR, group = GroupPelvis, cone = 65f },
            new Drive { joint = JointLegR + 2, pivot = JointLegR + 1, group = GroupLegR, cone = 0f },
            new Drive { joint = JointLegR + 3, pivot = JointLegR + 2, group = GroupLegR + 1, cone = 45f },
        };
        // Tone: springs between joints a bone apart (or across the trunk), at the length the ragdoll pose has them, each
        // with its share of the tone. A spring pulls only along the line between its two joints, so tone can't put energy
        // into the body or move it along (a pull toward a place worked out from the joints themselves did both): the
        // shoulder to the wrist sets the elbow's bend, the elbow to the neck, the belly and the other shoulder where the
        // arm hangs from the chest, the hip joints to the shoulders the back's bend and twist, and so on. Which way a
        // hinge bends is the limits' (Drives).
        private static readonly int[] ToneLinks =
        {
            JointArmL, JointArmL + 2, JointArmL + 1, JointArmL + 3, JointArmL + 1, JointNeck, JointArmL + 1, JointBelly, JointArmL + 1, JointArmR,
            JointArmR, JointArmR + 2, JointArmR + 1, JointArmR + 3, JointArmR + 1, JointNeck, JointArmR + 1, JointBelly, JointArmR + 1, JointArmL,
            JointLegL, JointLegL + 2, JointLegL + 1, JointLegL + 3, JointLegL + 1, JointBelly, JointLegL + 1, JointLegR,
            JointLegR, JointLegR + 2, JointLegR + 1, JointLegR + 3, JointLegR + 1, JointBelly, JointLegR + 1, JointLegL,
            JointHips, JointNeck, JointLegL, JointArmL, JointLegR, JointArmR, JointLegL, JointArmR, JointLegR, JointArmL,
            JointBelly, JointHead, JointArmL, JointHead, JointArmR, JointHead,
        };
        private static readonly float[] ToneShare =
        {
            1f, 0.8f, 0.6f, 0.6f, 0.4f,
            1f, 0.8f, 0.6f, 0.6f, 0.4f,
            1f, 0.8f, 0.7f, 0.5f,
            1f, 0.8f, 0.7f, 0.5f,
            1.5f, 1.2f, 1.2f, 0.8f, 0.8f,
            1f, 0.8f, 0.8f,
        };
        // A hinge's bend from straight (degrees, + the way the ragdoll pose bends it), and its twist: how far its plane has
        // turned about the upper bone from the pose's (turned the least way with the limb), which is the shoulder's or the
        // hip's own roll, as the joints don't show it. Left free, a body on its back rolled its raised knees out flat, the
        // shins folded sideways (the hip turned a quarter round), as nothing else holds it.
        private const float ElbowLeast = -5f, ElbowMost = 150f, ElbowTwist = 70f;
        private const float KneeLeast = -5f, KneeMost = 155f, KneeTwist = 30f;
        private const float SoftMargin = 20f;   // degrees inside a limit where movement is slowed
        private const float SoftDamp = 0.5f;    // at the limit, the share of a joint's movement from its pivot taken each step

        private const float StepSeconds = 1f / 60f;   // game seconds a step
        private const int MostSteps = 8;              // a frame's at most (the game at its fastest, the frame rate low)
        private const int Iterations = 6;             // passes over the bones and the ground a step (the limits and the limbs kept apart once, before)
        private const float Gravity = 12f;            // cells per game second per second (a little brisker than true)
        private const float AirDrag = 0.02f;          // of the speed lost each step
        private const float Friction = 0.5f;          // of the speed along the ground lost each step a joint touches it
        private const float StaticSlip = 0.0005f;     // cells a step: slower than this along the ground, a joint sticks
        private const float DownedTone = 0.02f;       // of the way to the ragdoll pose each joint is drawn each step, downed
        private const float DeadTone = 0.006f;        // and dead
        private const float ToneDamp = 0.12f;         // of a tone spring's stretching or shrinking taken each step
        private const float Pin = 0.5f;               // of the way to the canned fall a held joint is drawn each step
        private const float HipsFree = 0.25f;         // cells the hips may go from the cell's middle before the ground holds them
        private const float HipsHeld = 0.4f;          // and past which they are drawn back
        private const float HipsDrag = 0.3f;          // of the hips' speed lost each step at HipsHeld
        private const float CalmFrom = 2.5f;          // game seconds after it was last thrown: from here it is slowed more and more
        private const float CalmTo = 5f;              // to CalmDrag of its speed each step here, so it comes to rest
        private const float CalmDrag = 0.25f;         // (a body propped on its big head can rock a long while)
        private const float SettleSpeed = 0.06f;      // cells per game second: slower than this, at rest
        private const int SettleSteps = 45;           // for this many steps running
        private const int LongestSteps = 60 * 8;      // at rest after this long whatever
        private const float RadiusShare = 0.85f;      // a joint's size: this share of the body's median distance from its bone
        private const float WallHeight = 10f;         // cells: a wall's top, out of reach

        // The rig's side of it, worked out once: which bone each joint is and each bone's group, or null if the export
        // lacks a bone it needs (an older one), and then there are no ragdolls.
        private int[] jointBone;
        private int[] boneGroup;
        private bool ragdollTried;
        private Quaternion[] ragdollPose;   // each bone's turn on its parent's in the ragdoll pose
        private readonly Dictionary<Shape, DollShape> dollShapes = new Dictionary<Shape, DollShape>();

        // A body shape's: the ragdoll pose's joints and bone turns (model units, model space), each joint's size (model
        // units), and each link's length.
        internal sealed class DollShape
        {
            public Vector3[] pose;
            public Quaternion[] turn;
            public float[] radius;
            public float[] length;
            public float[] toneLength;   // each tone spring's
            public Quaternion[] frameOut;   // the inverse of each trunk group's frame in the pose (Turns)
        }

        public bool CanRagdoll
        {
            get
            {
                if (!ragdollTried)
                {
                    ragdollTried = true;
                    RagdollRig();
                }
                return jointBone != null;
            }
        }

        private void RagdollRig()
        {
            Clip clip;
            if (!clips.TryGetValue("ragdoll", out clip) && !clips.TryGetValue("lie", out clip))
            {
                return;
            }
            int[] joints = new int[Joints];
            for (int j = 0; j < Joints; j++)
            {
                int b = headBone;
                if (JointBones[j] != null && !boneIndex.TryGetValue(JointBones[j], out b))
                {
                    return;
                }
                joints[j] = b;
            }
            int[] groups = new int[boneHead.Length];
            foreach (int b in boneOrder)
            {
                string name = null;
                foreach (KeyValuePair<string, int> pair in boneIndex)
                {
                    if (pair.Value == b)
                    {
                        name = pair.Key;
                    }
                }
                groups[b] = GroupNamed(name, boneParent[b] >= 0 ? groups[boneParent[b]] : GroupPelvis);
            }
            ragdollPose = clip.turn[0];
            boneGroup = groups;
            jointBone = joints;
        }

        // A bone's group by its name, else its parent's.
        private static int GroupNamed(string name, int parent)
        {
            if (name == null)
            {
                return parent;
            }
            string[] parts = name.Split('.');
            string limb = parts[0];
            string side = parts.Length > 1 ? parts[1] : "";
            int arm = side == "L" ? GroupArmL : side == "R" ? GroupArmR : -1;
            int leg = side == "L" ? GroupLegL : side == "R" ? GroupLegR : -1;
            switch (limb)
            {
                case "spine":
                    return parts.Length < 2 || side == "001" ? GroupPelvis : side == "002" || side == "003" ? GroupChest : GroupHead;
                case "pelvis":
                    return GroupPelvis;
                case "shoulder":
                    return GroupChest;
                case "upper_arm":
                    return arm >= 0 ? arm : parent;
                case "forearm":
                    return arm >= 0 ? arm + 1 : parent;
                case "hand":
                case "thumb":
                    return arm >= 0 ? arm + 2 : parent;
                case "thigh":
                    return leg >= 0 ? leg : parent;
                case "shin":
                    return leg >= 0 ? leg + 1 : parent;
                case "foot":
                case "toe":
                case "heel":
                    return leg >= 0 ? leg + 2 : parent;
            }
            return parent;
        }

        private DollShape DollOf(Shape shape)
        {
            DollShape d;
            if (dollShapes.TryGetValue(shape, out d))
            {
                return d;
            }
            d = new DollShape();
            int bones = boneHead.Length;
            Vector3[] pos = new Vector3[bones];
            d.turn = new Quaternion[bones];
            // The ragdoll pose put together down the chain, as Place does, from the root's rest head.
            for (int i = 0; i < boneOrder.Length; i++)
            {
                int b = boneOrder[i], parent = boneParent[b];
                if (parent < 0)
                {
                    pos[b] = shape.boneHead[b];
                    d.turn[b] = Normalised(ragdollPose[b]);
                }
                else
                {
                    pos[b] = pos[parent] + d.turn[parent] * (shape.boneHead[b] - shape.boneHead[parent]);
                    d.turn[b] = d.turn[parent] * ragdollPose[b];
                }
            }
            d.pose = new Vector3[Joints];
            Vector3[] rest = new Vector3[Joints];
            for (int j = 0; j < Joints; j++)
            {
                int b = jointBone[j];
                d.pose[j] = j == JointHead ? pos[b] + d.turn[b] * (shape.faceCentre - shape.boneHead[b]) : pos[b];
                rest[j] = j == JointHead ? shape.faceCentre : shape.boneHead[b];
            }
            d.length = new float[Links.Length / 2];
            for (int k = 0; k < d.length.Length; k++)
            {
                d.length[k] = (rest[Links[2 * k]] - rest[Links[2 * k + 1]]).magnitude;
            }
            d.toneLength = new float[ToneLinks.Length / 2];
            for (int k = 0; k < d.toneLength.Length; k++)
            {
                d.toneLength[k] = (d.pose[ToneLinks[2 * k]] - d.pose[ToneLinks[2 * k + 1]]).magnitude;
            }
            d.radius = Radii(shape, rest);
            d.frameOut = new Quaternion[3];
            d.frameOut[GroupPelvis] = Quaternion.Inverse(Look(d.pose[JointBelly] - d.pose[JointHips], d.pose[JointLegR] - d.pose[JointLegL]));
            d.frameOut[GroupChest] = Quaternion.Inverse(Look(d.pose[JointNeck] - d.pose[JointBelly], d.pose[JointArmR] - d.pose[JointArmL]));
            d.frameOut[GroupHead] = Quaternion.Inverse(Look(d.pose[JointHead] - d.pose[JointNeck], d.pose[JointArmR] - d.pose[JointArmL]));
            dollShapes[shape] = d;
            return d;
        }

        // Each joint's size, from the body in this shape at rest: RadiusShare of the median distance of the vertices its
        // bone moves most from that bone (from the joint to the next one down the limb or trunk; for the head, the hands'
        // and the feet's ends, from the joint itself).
        private float[] Radii(Shape shape, Vector3[] rest)
        {
            Piece body = pieces[0];
            Vector3[] verts = shape.pieces[0].rest;
            List<float>[] far = new List<float>[Joints];
            int[] jointOfBone = new int[boneHead.Length];
            for (int b = 0; b < jointOfBone.Length; b++)
            {
                jointOfBone[b] = -1;
            }
            for (int j = 0; j < Joints; j++)
            {
                far[j] = new List<float>();
                jointOfBone[jointBone[j]] = j;
            }
            for (int v = 0; v < verts.Length; v++)
            {
                int most = -1;
                float w = 0f;
                for (int k = body.firstWeight[v]; k < body.firstWeight[v + 1]; k++)
                {
                    if (body.weight[k] > w)
                    {
                        w = body.weight[k];
                        most = body.weightBone[k];
                    }
                }
                int j = most >= 0 ? jointOfBone[most] : -1;
                if (j < 0)
                {
                    continue;
                }
                int next = NextJoint(j);
                far[j].Add(next >= 0 ? FromSegment(verts[v], rest[j], rest[next]) : (verts[v] - rest[j]).magnitude);
            }
            float[] radius = new float[Joints];
            for (int j = 0; j < Joints; j++)
            {
                far[j].Sort();
                radius[j] = far[j].Count > 0 ? far[j][far[j].Count / 2] * RadiusShare : 0.03f;
            }
            return radius;
        }

        // The next joint down from j along its limb or the trunk, -1 for an end.
        private static int NextJoint(int j)
        {
            if (j < JointHead)
            {
                return j + 1;
            }
            if (j == JointHead)
            {
                return -1;
            }
            return (j - JointArmL) % 4 == 3 ? -1 : j + 1;
        }

        private static float FromSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (p - (a + ab * t)).magnitude;
        }

        private static Quaternion Look(Vector3 forward, Vector3 up)
        {
            return forward.sqrMagnitude < 1e-12f ? Quaternion.identity : Quaternion.LookRotation(forward, up);
        }

        private static float Smooth(float from, float to, float x)
        {
            float t = Mathf.Clamp01((x - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        // Something a joint can't go into, in cells about the body's origin on the map's own axes (not turned with it):
        // a wall or a piece of furniture, from the ground up to top.
        public struct Block
        {
            public Vector2 min, max;
            public float top;
        }

        // One body's ragdoll. Its joints are kept in cells, in the figure's own space at the yaw it fell at (Yaw) with the
        // ground at 0, so it is drawn at Origin with that yaw as the figure is (DrawParts: turned, then placed).
        public sealed class Ragdoll
        {
            public float Yaw;          // degrees, as the figure's yaw
            public Vector3 Origin;     // where on the map it is drawn from (y unused)
            public IntVec3 Cell;       // the cell it lies in: moved off it (carried, hauled), the ragdoll is dropped
            public bool Settled;
            public bool Dead;          // limper
            // Where the joints are now and where they were a step ago (cells). last carries their speed (Verlet: at - last
            // a step), so what slows a joint moves it; before is just where they were, which they are drawn from.
            internal Vector3[] at = new Vector3[Joints], last = new Vector3[Joints], before = new Vector3[Joints];
            internal Vector3[] drawn = new Vector3[Joints];   // between before and at by how far into the next step
            private readonly float[] inverseMass = new float[Joints];
            private float[] radius = new float[Joints];
            private float size;
            private float carry;   // game seconds not yet stepped
            private int still, steps;
            private Vector3[] pinFrom, pinTo;   // the canned fall's joints at the last step and now, cells
            private bool pinStepped;   // stepped since pinTo was set: the next frame's fall starts from it
            private Vector3 pending;   // the felling blow's push (cells per game second), not yet given
            private float pendingUp;   // and up, for a body flung, the whole of it alike
            private float pinUpper, pinLegs;    // how hard the upper body and the legs are held to it, 0 .. 1
            private readonly Quaternion[] groups = new Quaternion[Groups];
            private readonly Vector3[] scratch = new Vector3[Joints];
            private readonly Vector3[] bends = new Vector3[4];   // each limb's hinge normal as the ragdoll pose has it, turned with the limb (Frames)
            internal readonly List<Block> blocks = new List<Block>();
            internal Vector3 cellMiddle;   // the cell's middle, in the ragdoll's space: what the hips are held to
            public bool Following;   // the body is in flight (Flings), so cellMiddle is on the move and the hips are drawn after it, not dragged
            public Vector3 HipsAt
            {
                get { return at[JointHips]; }
            }

            public float Size
            {
                get { return size; }
            }

            // Its joints as they are now (cells, in its own space): what is kept with the map (RagdollRests).
            public Vector3[] JointsNow
            {
                get { return (Vector3[])at.Clone(); }
            }

            public Ragdoll(float size)
            {
                this.size = size;
                for (int j = 0; j < Joints; j++)
                {
                    inverseMass[j] = 1f / JointMass[j];
                }
            }

            // The fall's first frame: the joints where the canned fall has them (model units, laid down as it is drawn)
            // and moving as it does, then the blow's push: way (in the ragdoll's space, along the ground) times speed
            // (cells per game second).
            public void Start(Vector3[] joints, Vector3 way, float speed, float up = 0f)
            {
                float low = float.MaxValue;
                for (int j = 0; j < Joints; j++)
                {
                    low = Mathf.Min(low, joints[j].y * size - radius[j]);
                }
                for (int j = 0; j < Joints; j++)
                {
                    at[j] = joints[j] * size - new Vector3(0f, low, 0f);
                    last[j] = at[j];
                    before[j] = at[j];
                    drawn[j] = at[j];
                }
                // Thrown once the upper body is let go of the canned fall, which would otherwise take the push back out.
                pending = way.normalized * speed;
                pendingUp = up;
                Settled = false;
                still = steps = 0;
            }

            // Thrown by a blow: the trunk carried along the push, the shoulders and head more than the hips so it spins
            // back over its feet, and the feet a little the other way, as the ground holds them while the body goes.
            public void Push(Vector3 way, float speed, float up = 0f)
            {
                if (speed <= 0f)
                {
                    return;
                }
                Vector3 v = way.normalized * speed * StepSeconds;
                Vector3 lift = new Vector3(0f, up * StepSeconds, 0f);
                for (int j = 0; j < Joints; j++)
                {
                    float share = j == JointHead || j == JointNeck ? 1.4f : j == JointArmL || j == JointArmR ? 1.3f
                        : j == JointBelly ? 1f : j == JointHips ? 0.7f
                        : j >= JointLegL && (j - JointLegL) % 4 >= 2 ? -0.3f   // ankles and toes
                        : j >= JointLegL ? 0.4f : 0.9f;
                    last[j] -= v * share + lift;
                }
                Wake();
            }

            // Set moving again (thrown, or gone limper as it died where it lay), and slowed to rest afresh.
            public void Wake()
            {
                Settled = false;
                still = steps = 0;
            }

            // The canned fall's joints this frame (model units, laid down as drawn), and how hard the upper body and the
            // legs are held to them (0 .. 1); null, none.
            public void HoldTo(Vector3[] joints, float upper, float legs)
            {
                if (joints == null || upper <= 0f && legs <= 0f)
                {
                    pinUpper = pinLegs = 0f;
                    return;
                }
                // Set on the ground as Start set the first frame (the fall is set on the floor by its lowest joint).
                float low = float.MaxValue;
                for (int j = 0; j < Joints; j++)
                {
                    low = Mathf.Min(low, joints[j].y * size - radius[j]);
                }
                bool first = pinTo == null;
                if (first)
                {
                    pinTo = new Vector3[Joints];
                    pinFrom = new Vector3[Joints];
                }
                else if (pinStepped)
                {
                    Array.Copy(pinTo, pinFrom, Joints);
                    pinStepped = false;
                }
                for (int j = 0; j < Joints; j++)
                {
                    pinTo[j] = joints[j] * size - new Vector3(0f, low, 0f);
                }
                if (first)
                {
                    Array.Copy(pinTo, pinFrom, Joints);
                }
                pinUpper = upper;
                pinLegs = legs;
            }

            internal void SetShape(float[] radii, float size)
            {
                this.size = size;
                for (int j = 0; j < Joints; j++)
                {
                    radius[j] = radii[j] * size;
                }
            }

            // Steps it on by seconds of game time (0 while paused), and sets drawn to where its joints are between steps.
            internal void Advance(DollShape shape, float seconds)
            {
                if (Settled)
                {
                    Array.Copy(at, drawn, Joints);
                    return;
                }
                carry = Mathf.Min(carry + seconds, MostSteps * StepSeconds);
                int n = Mathf.FloorToInt(carry / StepSeconds);
                for (int i = 0; i < n && !Settled; i++)
                {
                    Step(shape, i + 1, n);
                }
                carry -= n * StepSeconds;
                if (Settled)
                {
                    carry = 0f;
                }
                float t = carry / StepSeconds;
                for (int j = 0; j < Joints; j++)
                {
                    drawn[j] = Vector3.LerpUnclamped(before[j], at[j], t);
                }
            }

            // Step i of n this frame: the canned fall (Pin) is that far from where it was at the last step to where it is now.
            private void Step(DollShape shape, int i, int n)
            {
                steps++;
                Array.Copy(at, before, Joints);
                float g = Gravity * StepSeconds * StepSeconds;
                float drop = 1f - AirDrag - CalmDrag * Smooth(CalmFrom, CalmTo, steps * StepSeconds);
                for (int j = 0; j < Joints; j++)
                {
                    Vector3 v = (at[j] - last[j]) * drop;
                    last[j] = at[j];
                    at[j] += v + new Vector3(0f, -g, 0f);
                }
                if (pending != Vector3.zero && pinUpper < 0.5f)
                {
                    Push(pending, pending.magnitude, pendingUp);
                    pending = Vector3.zero;
                }
                Frames(shape, at);
                Tone(shape);
                if (pinUpper > 0f || pinLegs > 0f)
                {
                    pinStepped = true;
                    for (int j = 0; j < Joints; j++)
                    {
                        float k = Pin * (j >= JointLegL || j == JointHips ? pinLegs : pinUpper);
                        if (k <= 0f)
                        {
                            continue;
                        }
                        // Toward the fall at this step, moving as it moves, so the body goes on as it was going when let go.
                        Vector3 target = Vector3.Lerp(pinFrom[j], pinTo[j], (float)i / n);
                        Vector3 moving = (pinTo[j] - pinFrom[j]) / n;
                        Vector3 v = at[j] - last[j];
                        at[j] = Vector3.Lerp(at[j], target, k);
                        last[j] = at[j] - Vector3.Lerp(v, moving, k);
                    }
                }
                HoldHips();
                Limits(shape);
                Apart();
                for (int it = 0; it < Iterations; it++)
                {
                    for (int k = 0; k < shape.length.Length; k++)
                    {
                        Keep(Links[2 * k], Links[2 * k + 1], shape.length[k] * size);
                    }
                    Ground(false);
                }
                Ground(true);
                float fastest = 0f;
                for (int j = 0; j < Joints; j++)
                {
                    fastest = Mathf.Max(fastest, (at[j] - before[j]).sqrMagnitude);
                }
                fastest = Mathf.Sqrt(fastest) / StepSeconds;
                bool pinned = pinUpper > 0f || pinLegs > 0f || pending != Vector3.zero;
                still = !pinned && fastest < SettleSpeed ? still + 1 : 0;
                if (still >= SettleSteps || steps >= LongestSteps)
                {
                    Settled = true;
                    Array.Copy(at, last, Joints);
                    Array.Copy(at, before, Joints);
                }
            }

            // Two joints held their length apart, each moved by its share (the lighter the more).
            private void Keep(int a, int b, float length)
            {
                Vector3 d = at[b] - at[a];
                float now = d.magnitude;
                if (now < 1e-6f)
                {
                    return;
                }
                float wa = inverseMass[a], wb = inverseMass[b];
                Vector3 fix = d * ((now - length) / (now * (wa + wb)));
                at[a] += fix * wa;
                at[b] -= fix * wb;
            }

            // The hips kept near the cell's middle: free to HipsFree, the ground holding them harder out to HipsHeld, and
            // drawn back past it (the belly with them, so the trunk isn't stretched).
            private void HoldHips()
            {
                Vector3 off = at[JointHips] - cellMiddle;
                off.y = 0f;
                float far = off.magnitude;
                if (far <= HipsFree)
                {
                    return;
                }
                float drag = Following ? 0f : HipsDrag * Smooth(HipsFree, HipsHeld, far);
                Vector3 back = far > HipsHeld ? off * ((far - HipsHeld) / far) * 0.3f : Vector3.zero;
                for (int j = JointHips; j <= JointBelly; j++)
                {
                    Vector3 v = at[j] - last[j];
                    v.y = 0f;
                    last[j] += v * drag;
                    at[j] -= back;
                }
            }

            // The ground at 0 and the walls and furniture round about (blocks): a joint is pushed out by the shortest way,
            // and with rubbing (once a step) slowed along a surface it touches.
            private void Ground(bool rubbing)
            {
                Quaternion turn = Quaternion.AngleAxis(Yaw, Vector3.up), back = Quaternion.Inverse(turn);
                for (int j = 0; j < Joints; j++)
                {
                    float r = radius[j];
                    bool touching = false;
                    if (at[j].y < r)
                    {
                        at[j].y = r;
                        touching = true;
                    }
                    if (blocks.Count > 0)
                    {
                        Vector3 p = turn * at[j];
                        for (int k = 0; k < blocks.Count; k++)
                        {
                            Block b = blocks[k];
                            if (p.x <= b.min.x - r || p.x >= b.max.x + r || p.z <= b.min.y - r || p.z >= b.max.y + r || p.y >= b.top + r)
                            {
                                continue;
                            }
                            // Out by the shortest of the four sides and the top.
                            float left = p.x - (b.min.x - r), right = b.max.x + r - p.x;
                            float near = p.z - (b.min.y - r), farSide = b.max.y + r - p.z, up = b.top + r - p.y;
                            float least = Mathf.Min(Mathf.Min(left, right), Mathf.Min(Mathf.Min(near, farSide), up));
                            if (least == up)
                            {
                                p.y = b.top + r;
                            }
                            else if (least == left)
                            {
                                p.x = b.min.x - r;
                            }
                            else if (least == right)
                            {
                                p.x = b.max.x + r;
                            }
                            else if (least == near)
                            {
                                p.z = b.min.y - r;
                            }
                            else
                            {
                                p.z = b.max.y + r;
                            }
                            touching = true;
                        }
                        at[j] = back * p;
                    }
                    if (touching && rubbing)
                    {
                        Vector3 v = at[j] - last[j];
                        Vector3 along = new Vector3(v.x, 0f, v.z);
                        if (along.magnitude < StaticSlip)
                        {
                            last[j] += along;
                        }
                        else
                        {
                            last[j] += along * Friction;
                        }
                    }
                }
            }

            // The limbs kept out of the trunk and out of each other (each a ball at its joint), once a step.
            private void Apart()
            {
                for (int a = JointArmL + 1; a < Joints; a++)
                {
                    if (a == JointArmR || a == JointLegL || a == JointLegR)
                    {
                        continue;   // the shoulders and hip joints are part of the trunk
                    }
                    for (int b = 0; b <= JointHead; b++)
                    {
                        Part(a, b);
                    }
                    for (int b = a + 1; b < Joints; b++)
                    {
                        // Another limb only (a limb's own joints are held apart by its bones).
                        if (b == JointArmR || b == JointLegL || b == JointLegR || (b - JointArmL) / 4 == (a - JointArmL) / 4)
                        {
                            continue;
                        }
                        Part(a, b);
                    }
                }
            }

            private void Part(int a, int b)
            {
                Vector3 d = at[b] - at[a];
                float reach = (radius[a] + radius[b]) * 0.9f;
                float now = d.sqrMagnitude;
                if (now >= reach * reach || now < 1e-12f)
                {
                    return;
                }
                now = Mathf.Sqrt(now);
                Stop(a, b, d * ((now - reach) / now));
            }

            // Each group's turn from the ragdoll pose to where the joints are now (see Turns).
            internal void Frames(DollShape shape, Vector3[] x)
            {
                Vector3[] pose = shape.pose;
                groups[GroupPelvis] = Look(x[JointBelly] - x[JointHips], x[JointLegR] - x[JointLegL]) * shape.frameOut[GroupPelvis];
                groups[GroupChest] = Look(x[JointNeck] - x[JointBelly], x[JointArmR] - x[JointArmL]) * shape.frameOut[GroupChest];
                groups[GroupHead] = Look(x[JointHead] - x[JointNeck], groups[GroupChest] * (pose[JointArmR] - pose[JointArmL])) * shape.frameOut[GroupHead];
                Limb(shape, x, JointArmL, GroupArmL, groups[GroupChest], 0);
                Limb(shape, x, JointArmR, GroupArmR, groups[GroupChest], 1);
                Limb(shape, x, JointLegL, GroupLegL, groups[GroupPelvis], 2);
                Limb(shape, x, JointLegR, GroupLegR, groups[GroupPelvis], 3);
            }

            // A limb's three turns: the upper and lower bones from the limb's bend (the plane its three joints make, as the
            // ragdoll pose's is) while it is bent, else turned the least way from the group above; the end bone (hand,
            // foot) turned the least way from the lower one.
            private void Limb(DollShape shape, Vector3[] x, int first, int group, Quaternion above, int limb)
            {
                Vector3[] pose = shape.pose;
                Vector3 upN = pose[first + 1] - pose[first], lowN = pose[first + 2] - pose[first + 1];
                Vector3 up = x[first + 1] - x[first], low = x[first + 2] - x[first + 1];
                Vector3 bendN = Vector3.Cross(upN, lowN), bend = Vector3.Cross(up, low);
                Quaternion upLeast = Quaternion.FromToRotation(above * upN, up) * above;
                bends[limb] = upLeast * bendN;
                float bent = bend.magnitude / Mathf.Max(1e-9f, up.magnitude * low.magnitude);   // the sine of the bend
                float w = Vector3.Dot(bend, bends[limb]) > 0f ? Smooth(0.1f, 0.35f, bent) : 0f;
                Quaternion upper = w > 0f ? Quaternion.Slerp(upLeast, Look(up, bend) * Quaternion.Inverse(Look(upN, bendN)), w) : upLeast;
                Quaternion lowLeast = Quaternion.FromToRotation(upper * lowN, low) * upper;
                Quaternion lower = w > 0f ? Quaternion.Slerp(lowLeast, Look(low, bend) * Quaternion.Inverse(Look(lowN, bendN)), w) : lowLeast;
                groups[group] = upper;
                groups[group + 1] = lower;
                groups[group + 2] = Quaternion.FromToRotation(lower * (pose[first + 3] - pose[first + 2]), x[first + 3] - x[first + 2]) * lower;
            }

            // Tone (ToneLinks): each spring drawn a little toward its length in the ragdoll pose, its stretching damped; and
            // the soft limits: a joint's movement from its pivot slowed near the edge of its range. All of it shared between
            // the two joints by their weights, so the body as a whole is not moved by it.
            private void Tone(DollShape shape)
            {
                float tone = Dead ? DeadTone : DownedTone;
                for (int k = 0; k < ToneShare.Length; k++)
                {
                    int a = ToneLinks[2 * k], b = ToneLinks[2 * k + 1];
                    Vector3 d = at[b] - at[a];
                    float now = d.magnitude;
                    if (now < 1e-6f)
                    {
                        continue;
                    }
                    Vector3 n = d / now;
                    float wa = inverseMass[a], wb = inverseMass[b];
                    Vector3 fix = n * ((now - shape.toneLength[k] * size) * tone * ToneShare[k] / (wa + wb));
                    at[a] += fix * wa;
                    at[b] -= fix * wb;
                    Slow(a, b, n * Vector3.Dot((at[b] - last[b]) - (at[a] - last[a]), n), ToneDamp * ToneShare[k]);
                }
                for (int i = 0; i < Drives.Length; i++)
                {
                    Drive d = Drives[i];
                    Vector3 target = at[d.pivot] + groups[d.group] * (shape.pose[d.joint] - shape.pose[d.pivot]);
                    float soft = SoftOf(shape, d, target);
                    if (soft > 0f)
                    {
                        Slow(d.pivot, d.joint, (at[d.joint] - last[d.joint]) - (at[d.pivot] - last[d.pivot]), SoftDamp * soft);
                    }
                }
            }

            // Takes share of the movement rel (b's from a's, cells a step) out of the two, each by its weight.
            private void Slow(int a, int b, Vector3 rel, float share)
            {
                Vector3 off = rel * Mathf.Clamp01(share);
                float wa = inverseMass[a], wb = inverseMass[b];
                last[b] += off * (wb / (wa + wb));
                last[a] -= off * (wa / (wa + wb));
            }

            // How far into its soft margin a joint is, 0 .. 1.
            private float SoftOf(DollShape shape, Drive d, Vector3 target)
            {
                Vector3 v = at[d.joint] - at[d.pivot];
                if (d.cone > 0f)
                {
                    return Smooth(d.cone - SoftMargin, d.cone, Vector3.Angle(v, target - at[d.pivot]));
                }
                float bend, twist, least, most, twistMost;
                HingeOf(d, out bend, out twist, out least, out most, out twistMost);
                // The twist counts only as far as the hinge is bent: straight, it moves nothing.
                float twisted = Smooth(twistMost - SoftMargin, twistMost, Mathf.Abs(twist)) * Smooth(5f, 25f, Mathf.Abs(bend));
                return Mathf.Max(Mathf.Max(Smooth(most - SoftMargin, most, bend), Smooth(least + SoftMargin, least, bend)), twisted);
            }

            // A hinge's bend now (degrees from straight, + the way the ragdoll pose bends, - bent back) and its twist (degrees
            // its bend has turned about the upper bone out of its plane), with their limits.
            private void HingeOf(Drive d, out float bend, out float twist, out float least, out float most, out float twistMost)
            {
                bool knee = d.joint >= JointLegL;
                int limb = (d.joint - JointArmL) / 4;
                least = knee ? KneeLeast : ElbowLeast;
                most = knee ? KneeMost : ElbowMost;
                twistMost = knee ? KneeTwist : ElbowTwist;
                Vector3 u, side, n;
                HingeAxes(d, limb, out u, out side, out n);
                Vector3 v = (at[d.joint] - at[d.pivot]).normalized;
                float s = Vector3.Dot(v, side), o = Vector3.Dot(v, n);
                bend = Mathf.Atan2(Mathf.Sqrt(s * s + o * o), Vector3.Dot(v, u)) * Mathf.Rad2Deg;
                twist = Mathf.Atan2(o, s) * Mathf.Rad2Deg;
                if (twist > 90f || twist < -90f)
                {
                    // Bent back: the twist taken from the plane's other side.
                    bend = -bend;
                    twist = twist > 0f ? twist - 180f : twist + 180f;
                }
            }

            // A hinge's axes: u along the upper bone, side toward its bend, n its axis.
            private void HingeAxes(Drive d, int limb, out Vector3 u, out Vector3 side, out Vector3 n)
            {
                u = (at[d.pivot] - at[d.pivot - 1]).normalized;
                n = Vector3.ProjectOnPlane(bends[limb], u);
                if (n.sqrMagnitude < 1e-8f)
                {
                    n = Vector3.Cross(u, Mathf.Abs(u.y) < 0.9f ? Vector3.up : Vector3.right);   // the bend's axis lost: any will do
                }
                n.Normalize();
                side = Vector3.Cross(n, u);
            }

            // The hard limits: a joint past the edge of its range put back on it (the slowing before it is in Tone).
            private void Limits(DollShape shape)
            {
                for (int i = 0; i < Drives.Length; i++)
                {
                    Drive d = Drives[i];
                    int c = d.joint, p = d.pivot;
                    Vector3 v = at[c] - at[p];
                    float len = v.magnitude;
                    if (len < 1e-6f)
                    {
                        continue;
                    }
                    Vector3 put;
                    if (d.cone > 0f)
                    {
                        Vector3 aim = groups[d.group] * (shape.pose[c] - shape.pose[p]);
                        float angle = Vector3.Angle(v, aim);
                        if (angle <= d.cone)
                        {
                            continue;
                        }
                        put = Vector3.RotateTowards(v, aim, (angle - d.cone) * Mathf.Deg2Rad, 0f);
                    }
                    else
                    {
                        float bend, twist, least, most, twistMost;
                        HingeOf(d, out bend, out twist, out least, out most, out twistMost);
                        float b = Mathf.Clamp(bend, least, most), t = Mathf.Clamp(twist, -twistMost, twistMost);
                        if (b == bend && t == twist)
                        {
                            continue;
                        }
                        Vector3 u, side, n;
                        HingeAxes(d, (c - JointArmL) / 4, out u, out side, out n);
                        float br = b * Mathf.Deg2Rad, tr = t * Mathf.Deg2Rad;
                        put = (u * Mathf.Cos(br) + (side * Mathf.Cos(tr) + n * Mathf.Sin(tr)) * Mathf.Sin(br)) * len;
                    }
                    Stop(c, p, put - v);
                }
            }

            // Moves c by fix from p (cells), each by its weight's share, and stops it dead there: its movement toward p
            // along fix taken out, and where it was a step ago moved with it, so it is neither thrown back nor left
            // pressing on. A joint shaken against a limit or another limb every step would shuffle the body along the
            // ground.
            private void Stop(int c, int p, Vector3 fix)
            {
                Vector3 way = fix.normalized;
                float into = Vector3.Dot((at[c] - last[c]) - (at[p] - last[p]), way);
                if (into < 0f)
                {
                    Slow(p, c, way * into, 1f);
                }
                float wc = inverseMass[c], wp = inverseMass[p];
                Vector3 mc = fix * (wc / (wc + wp)), mp = fix * (wp / (wc + wp));
                at[c] += mc;
                at[p] -= mp;
                last[c] += mc;
                last[p] -= mp;
            }

            // Each bone's turn in model space from where the joints are drawn: its group's turn on top of the ragdoll
            // pose's, and the root's head (model units).
            internal void Turns(SkinnedFigure fig, DollShape shape, Quaternion[] turn, out Vector3 root)
            {
                Vector3[] x = scratch;
                for (int j = 0; j < Joints; j++)
                {
                    x[j] = drawn[j] / size;
                }
                Frames(shape, x);
                for (int b = 0; b < turn.Length; b++)
                {
                    turn[b] = groups[fig.boneGroup[b]] * shape.turn[b];
                }
                root = x[JointHips];
            }
        }

        // Starts a ragdoll for a pawn whose pose Chain has just placed (the canned fall's first frame, laid down) and sets
        // it moving by the blow. size: the pawn's (cells per model unit).
        public Ragdoll NewRagdoll(Posed p, float size, Vector3 way, float speed, float up = 0f)
        {
            if (!CanRagdoll)
            {
                return null;
            }
            DollShape shape = DollOf(p.shape);
            Ragdoll doll = new Ragdoll(size);
            doll.SetShape(shape.radius, size);
            doll.Start(JointsOf(p), way, speed, up);
            return doll;
        }

        // A ragdoll that has come to rest, made again from its saved joints (cells).
        public Ragdoll RestedRagdoll(Shape shape, float size, Vector3[] joints)
        {
            if (!CanRagdoll || joints == null || joints.Length != Joints)
            {
                return null;
            }
            Ragdoll doll = new Ragdoll(size);
            doll.SetShape(DollOf(shape).radius, size);
            Array.Copy(joints, doll.at, Joints);
            Array.Copy(joints, doll.last, Joints);
            Array.Copy(joints, doll.before, Joints);
            Array.Copy(joints, doll.drawn, Joints);
            doll.Settled = true;
            return doll;
        }

        // How far a fall goes the way of a push, -1 .. 1: the way its hips travel along the ground from its first frame to
        // its last, laid down by whole, against way (both in the figure's space); 0 for a fall that stays put.
        public float FallAgrees(string clip, Matrix4x4 whole, Vector3 way)
        {
            Clip c;
            if (!clips.TryGetValue(clip, out c))
            {
                return 0f;
            }
            int root = boneOrder[0];
            Vector3 travel = whole.MultiplyVector(c.head[c.head.Length - 1][root] - c.head[0][root]);
            travel.y = 0f;
            way.y = 0f;
            return travel.sqrMagnitude < 1e-6f || way.sqrMagnitude < 1e-6f ? 0f : Vector3.Dot(travel.normalized, way.normalized);
        }

        // Where Chain put the joints (model units, the whole move in).
        private Vector3[] JointsOf(Posed p)
        {
            Vector3[] joints = new Vector3[Joints];
            for (int j = 0; j < Joints; j++)
            {
                int b = jointBone[j];
                joints[j] = p.whole[b].MultiplyPoint3x4(j == JointHead ? p.shape.faceCentre : p.shape.boneHead[b]);
            }
            return joints;
        }

        // Steps the ragdoll on by seconds of game time, held to the pose Chain has just placed (the canned fall) by upper
        // and legs (0 .. 1), then puts its own pose in place of that one by w (Override with frame) for Finish.
        public void Ragdolled(Posed p, Ragdoll doll, float seconds, float upper, float legs, Matrix4x4 frame, float w)
        {
            DollShape shape = DollOf(p.shape);
            doll.SetShape(shape.radius, doll.Size);
            doll.HoldTo(upper > 0f || legs > 0f ? JointsOf(p) : null, upper, legs);
            doll.Advance(shape, seconds);
            if (dollTurns == null || dollTurns.Length != boneHead.Length)
            {
                dollTurns = new Quaternion[boneHead.Length];
            }
            Vector3 root;
            doll.Turns(this, shape, dollTurns, out root);
            Override(p, dollTurns, root, frame, w);
        }

        private Quaternion[] dollTurns;
    }
}
