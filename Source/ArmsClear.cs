using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The arms of a held stance kept out of a bigger trunk. The drafted stances were posed on the bare base body, and the
    // Hulk and Fat keys (more so at a body shape strength over 1) and thick clothes widen the chest and belly round the
    // arms that hold a weapon across them: ArmsOut pushes the hands forward by the chest's front, but the forearm crossing
    // to the support grip, the elbows at the sides and on a Hulk the whole support arm stayed inside. After ArmsOut, each
    // held arm is looked at against the trunk's girth all round (ArmRoom: how far out the trunk reaches in each band and
    // way round the chest's upright line, at rest, of its own body and worn garments, against the bare base body's; the
    // shoulders' skin counted only in front and behind, the pecs and shoulder blades, not the cap the upper arm comes out
    // of), and a point of it sunk more than ArmLeast further than the base body would sink it is moved out: the elbow
    // swung out about the shoulder-to-wrist line (the wrist kept), then the wrists moved together (so a two-handed hold
    // keeps its grip and the weapon rides along) for what is still in, as a nearly straight arm's elbow hardly moves by
    // swinging; ArmRounds times. On the base body, and the Male and Female, nothing moves, so the stances stay as posed.
    public sealed partial class SkinnedFigure
    {
        internal sealed class ArmRoom
        {
            public float[] radius;   // [height bin * ArmRoomTurns + way bin]: the farthest out the trunk reaches, model units; NegativeInfinity for none
            public float forearm;    // the forearm's thickness, model units: the median distance of its skin from its bone line
        }

        private const int ArmRoomTurns = 32;         // ways round the chest's line
        private const float ArmRoomStep = 0.025f;    // model units: the height of each band
        private const int ArmRoomBands = 64;         // bands from the ground up (to 1.6, over the chest of any shape)
        private const float ArmRoomGap = 0.005f;
        private const float ArmLeast = 0.02f;        // model units: a point sunk no more than this (past what the base body sinks it) is left
        private const float ShoulderSides = 1.2f;    // the shoulders' skin counts within atan(this) of straight in front or behind     // model units: kept between the arm's skin and the trunk's
        private const float ElbowOutMost = 0.3f;    // model units: an elbow is swung out no further than this
        private const float WristOutMost = 0.1f;     // and the wrists moved no further than this
        private static readonly float[] ElbowSpots = { 0f, 0.25f, 0.5f };   // along the forearm from the elbow, looked at for the elbow
        private static readonly float[] UpperSpots = { 0.5f, 0.75f };   // along the upper arm from the shoulder, looked at for the elbow too
        private static readonly float[] WristSpots = { 0f, 0.25f, 0.5f, 0.75f, 1f };   // and for the wrists
        private const float WristCarries = 0.5f;     // how far an elbow goes with its wrist, a share, about (an arm at full stretch)
        private const int ArmRounds = 2;             // elbows then wrists, this many times over

        private ArmRoom baseRoom;
        private bool[] roomBone;   // the bones whose skin is the trunk's (AddRoom)
        internal string armsClearSeen;   // what the last ArmsClear did (the elbows' and wrists' moves, model units), for the autotest

        // How far out the trunk (vertices the trunk's bones move most, not the arms', neck's or legs') reaches in each band
        // and way about the chest bone's upright line at rest: of verts (and the middles of tris' edges and faces, as a
        // garment may be big faces) weighted as piece's.
        private void AddRoom(float[] radius, Vector3 axis, Vector3[] verts, Piece piece)
        {
            if (roomBone == null)
            {
                // The trunk's bones and the shoulders' (the chest's front and the tops of the shoulders ride them), not the
                // arms below them, the neck or the legs.
                roomBone = new bool[boneHead.Length];
                foreach (KeyValuePair<string, int> pair in boneIndex)
                {
                    roomBone[pair.Value] = trunkBone[pair.Value] || pair.Key.StartsWith("shoulder.");
                }
            }
            bool[] trunk = new bool[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                float on = 0f, trunkOn = 0f;
                for (int k = piece.firstWeight[i]; k < piece.firstWeight[i + 1]; k++)
                {
                    if (roomBone[piece.weightBone[k]])
                    {
                        on += piece.weight[k];
                    }
                    if (trunkBone[piece.weightBone[k]])
                    {
                        trunkOn += piece.weight[k];
                    }
                }
                trunk[i] = on >= 0.5f;
                if (trunk[i] && trunkOn < 0.5f)
                {
                    // Mostly the shoulders': the chest's front and the shoulder blades count, not the shoulder's cap at
                    // the side, which the upper arm comes out of.
                    float x = verts[i].x - axis.x, z = verts[i].z - axis.z;
                    trunk[i] = Mathf.Abs(x) < Mathf.Abs(z) * ShoulderSides;
                }
            }
            for (int i = 0; i + 2 < piece.tris.Length; i += 3)
            {
                int a = piece.tris[i], b = piece.tris[i + 1], c = piece.tris[i + 2];
                if (!trunk[a] || !trunk[b] || !trunk[c])
                {
                    continue;
                }
                Vector3 pa = verts[a], pb = verts[b], pc = verts[c];
                RoomPoint(radius, axis, pa);
                RoomPoint(radius, axis, pb);
                RoomPoint(radius, axis, pc);
                RoomPoint(radius, axis, (pa + pb) * 0.5f);
                RoomPoint(radius, axis, (pb + pc) * 0.5f);
                RoomPoint(radius, axis, (pc + pa) * 0.5f);
                RoomPoint(radius, axis, (pa + pb + pc) / 3f);
            }
        }

        private static void RoomPoint(float[] radius, Vector3 axis, Vector3 at)
        {
            int bin = RoomBin(axis, at, out float d);
            if (bin >= 0 && d > radius[bin])
            {
                radius[bin] = d;
            }
        }

        // Which band and way a point (rest model space) is in, and its distance out from the line; -1 off the bands.
        private static int RoomBin(Vector3 axis, Vector3 at, out float d)
        {
            float x = at.x - axis.x, z = at.z - axis.z;
            d = Mathf.Sqrt(x * x + z * z);
            int band = Mathf.FloorToInt(at.y / ArmRoomStep);
            if (band < 0 || band >= ArmRoomBands)
            {
                return -1;
            }
            int way = Mathf.FloorToInt(Mathf.Repeat(Mathf.Atan2(z, x) / (2f * Mathf.PI), 1f) * ArmRoomTurns) % ArmRoomTurns;
            return band * ArmRoomTurns + way;
        }

        private ArmRoom MakeRoom(Vector3[] body, Vector3[] head, Shape s, GarmentMask mask)
        {
            int chest;
            if (!boneIndex.TryGetValue("spine.003", out chest))
            {
                return null;
            }
            ArmRoom room = new ArmRoom { radius = new float[ArmRoomBands * ArmRoomTurns] };
            for (int i = 0; i < room.radius.Length; i++)
            {
                room.radius[i] = float.NegativeInfinity;
            }
            Vector3 axis = head[chest];
            AddRoom(room.radius, axis, body, pieces[0]);
            for (int g = 1; s != null && g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((mask & GarmentBit(g)) != 0)
                {
                    AddRoom(room.radius, axis, GarmentShape(s, g, mask).rest, pieces[g]);
                }
            }
            // The forearms' thickness: their skin's median distance from the line between their joints.
            List<float> far = new List<float>();
            foreach (string side in new[] { "L", "R" })
            {
                int fore, hand;
                if (!boneIndex.TryGetValue("forearm." + side, out fore) || !boneIndex.TryGetValue("hand." + side, out hand))
                {
                    continue;
                }
                Vector3 e = head[fore], line = head[hand] - e;
                float long2 = line.sqrMagnitude;
                Piece b = pieces[0];
                for (int i = 0; i < body.Length; i++)
                {
                    int most = -1;
                    float best = 0f;
                    for (int k = b.firstWeight[i]; k < b.firstWeight[i + 1]; k++)
                    {
                        if (b.weight[k] > best)
                        {
                            best = b.weight[k];
                            most = b.weightBone[k];
                        }
                    }
                    if (most != fore || long2 < 1e-8f)
                    {
                        continue;
                    }
                    float t = Mathf.Clamp01(Vector3.Dot(body[i] - e, line) / long2);
                    far.Add((body[i] - (e + line * t)).magnitude);
                }
            }
            far.Sort();
            room.forearm = far.Count > 0 ? far[far.Count / 2] : 0.04f;
            return room;
        }

        private ArmRoom RoomOf(Shape s, GarmentMask mask)
        {
            mask &= ~UnclearMask;   // packs, shields and webbing, as Clearance
            ArmRoom room;
            if (!s.armRoom.TryGetValue(mask, out room))
            {
                room = MakeRoom(s.pieces[0].rest, s.boneHead, s, mask);
                s.armRoom[mask] = room;
            }
            return room;
        }

        // How much further out a point of the arm (posed model space) has to go on this body than on the bare base body
        // to keep its skin off the trunk's, out from the chest's line (posed model space); zero if no more.
        private Vector3 ArmPush(Posed p, ArmRoom room, int chest, Vector3 at)
        {
            Quaternion turn = p.turn[chest];
            Vector3 rest = Quaternion.Inverse(turn) * (at - p.pos[chest]) + p.shape.boneHead[chest];
            float d;
            int bin = RoomBin(p.shape.boneHead[chest], rest, out d);
            if (bin < 0 || float.IsNegativeInfinity(room.radius[bin]))
            {
                return Vector3.zero;
            }
            // The same place on the base body: the point moved back with the chest bone's head.
            Vector3 baseRest = rest - p.shape.boneMove[chest];
            float db;
            int baseBin = RoomBin(boneHead[chest], baseRest, out db);
            float baseNeed = baseBin < 0 || float.IsNegativeInfinity(baseRoom.radius[baseBin]) ? 0f
                : Mathf.Max(0f, baseRoom.radius[baseBin] + baseRoom.forearm + ArmRoomGap - db);
            float need = Mathf.Max(0f, room.radius[bin] + room.forearm + ArmRoomGap - d) - baseNeed - ArmLeast;
            if (need <= 1e-4f || d < 1e-5f)
            {
                return Vector3.zero;
            }
            Vector3 way = new Vector3(rest.x - p.shape.boneHead[chest].x, 0f, rest.z - p.shape.boneHead[chest].z) / d;
            return turn * way * need;
        }

        private static Vector3 Largest(Vector3 a, Vector3 b)
        {
            return b.sqrMagnitude > a.sqrMagnitude ? b : a;
        }

        // After Chain (and ArmsOut, Reached): the arms held in a stance (clip, laid on by w) kept out of the trunk, as at
        // the top. True if it moved an arm: call Reached once the arms are done.
        public bool ArmsClear(Posed p, Matrix4x4 whole, string clip, float w)
        {
            Clip c;
            int chest;
#if AUTOTEST
            armsClearSeen = null;
#endif
            if (w <= 0f || clip == null || !clips.TryGetValue(clip, out c) || !boneIndex.TryGetValue("spine.003", out chest))
            {
                return false;
            }
            if (baseRoom == null)
            {
                baseRoom = MakeRoom(pieces[0].rest, boneHead, null, default(GarmentMask));
                if (baseRoom == null)
                {
                    return false;
                }
            }
            ArmRoom room = RoomOf(p.shape, p.wornMask);
            if (room == null)
            {
                return false;
            }
            w = Mathf.Clamp01(w);
            int[] upper = new int[2], fore = new int[2], hand = new int[2];
            bool[] held = new bool[2];
            string[] sides = { "R", "L" };
            for (int s = 0; s < 2; s++)
            {
                held[s] = boneIndex.TryGetValue("upper_arm." + sides[s], out upper[s]) && boneIndex.TryGetValue("forearm." + sides[s], out fore[s])
                    && boneIndex.TryGetValue("hand." + sides[s], out hand[s]) && (c.over == null || c.over[hand[s]] > 0f);
            }
            bool moved = false;
#if AUTOTEST
            armsClearSeen = "forearm " + room.forearm.ToString("F3") + " (base " + baseRoom.forearm.ToString("F3") + ")";
#endif
            for (int round = 0; round < ArmRounds; round++)
            {
                // The elbows, swung out about the shoulder-to-wrist line with the wrists kept.
                for (int s = 0; s < 2; s++)
                {
                    if (!held[s])
                    {
                        continue;
                    }
                    Vector3 e = p.pos[fore[s]], h = p.pos[hand[s]], sh = p.pos[upper[s]];
                    Vector3 push = Vector3.zero;
                    foreach (float t in ElbowSpots)
                    {
                        push = Largest(push, ArmPush(p, room, chest, Vector3.Lerp(e, h, t)) / (1f - t));
                    }
                    foreach (float t in UpperSpots)
                    {
                        push = Largest(push, ArmPush(p, room, chest, Vector3.Lerp(sh, e, t)) / t);
                    }
#if AUTOTEST
                    armsClearSeen += ", " + round + sides[s] + " elbow " + push.magnitude.ToString("F3");
#endif
                    push = Vector3.ClampMagnitude(push, ElbowOutMost) * w;
                    if (push.sqrMagnitude < 1e-7f)
                    {
                        continue;
                    }
                    Reach(p, whole, sides[s], whole.MultiplyPoint3x4(h), 1f, e + push);
                    moved = true;
                }
                // Then the wrists, together (a two-handed hold keeps its grip), for what is still in: by the hand, and
                // what swinging the elbow couldn't take out, as a nearly straight arm's elbow hardly moves so (an arm
                // moved with its wrist carries its elbow about half as far, WristCarries).
                Vector3 wrists = Vector3.zero;
                for (int s = 0; s < 2; s++)
                {
                    if (!held[s])
                    {
                        continue;
                    }
                    Vector3 e = p.pos[fore[s]], h = p.pos[hand[s]], sh = p.pos[upper[s]];
                    foreach (float t in WristSpots)
                    {
                        wrists = Largest(wrists, ArmPush(p, room, chest, Vector3.Lerp(e, h, t)) / Mathf.Max(t, WristCarries));
                    }
                    // Not the upper arm: by the shoulder a wrist move hardly carries it (the push divided by as little
                    // made it three or four times as much), and armour's shoulder plates, made over the arm, enclose it
                    // whatever the hands do, so the wrists went to their limit both rounds and took the weapon up and over
                    // toward that shoulder, out of the fists' place on it. The elbow swing above still answers for it.
                    if (WalkFigures.WristsByUpperArm)
                    {
                        foreach (float t in UpperSpots)
                        {
                            wrists = Largest(wrists, ArmPush(p, room, chest, Vector3.Lerp(sh, e, t)) / (t * WristCarries));
                        }
                    }
                }
#if AUTOTEST
                armsClearSeen += ", " + round + " wrists " + wrists.magnitude.ToString("F3");
#endif
                if (WalkFigures.WristsForwardOnly)
                {
                    // Only forward along the chest, as ArmsOut moves them: out from the chest's line by an arm at the
                    // side is sideways and up, which slid the weapon across the body and out of the fists' place on it
                    // (in armour, under its shoulder plates, every time). Held further out, the weapon keeps its place
                    // against the body as posed; the elbow swing above takes the sides.
                    Vector3 ahead = p.turn[chest] * Vector3.forward;
                    wrists = ahead * Mathf.Max(0f, Vector3.Dot(wrists, ahead));
                }
                wrists = Vector3.ClampMagnitude(wrists, WristOutMost) * w;
                if (wrists.sqrMagnitude >= 1e-7f)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        if (held[s])
                        {
                            Reach(p, whole, sides[s], whole.MultiplyPoint3x4(p.pos[hand[s]] + wrists), 1f);
                            moved = true;
                        }
                    }
                }
            }
            return moved;
        }
    }

    public static partial class WalkFigures
    {
        // The held arms kept out of a bigger trunk (SkinnedFigure.ArmsClear). In the AUTOTEST build a file
        // <config>/M3AT/no_armsclear turns it off, to compare (the game is started through Steam, which passes it no
        // environment).
#if AUTOTEST
        internal static bool ArmsClearOn = !System.IO.File.Exists(System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "no_armsclear"));
        internal static bool ArmsOutOn = true;   // SkinnedFigure.ArmsOut, which the armourgrip scenario turns off to compare
        internal static bool WristsByUpperArm;   // the old rule, the wrists moved for the upper arm too, to compare
        internal static bool WristsForwardOnly = true;   // the wrists moved only forward along the chest (the old rule: out from its line)
#else
        internal const bool ArmsClearOn = true;
        internal const bool ArmsOutOn = true;
        internal const bool WristsByUpperArm = false;
        internal const bool WristsForwardOnly = true;
#endif
    }
}
