using System.Collections.Generic;
using System.IO;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Packs, belts and waist tools a pawn wears (IsoRim's WornGearDef: the jump pack, shield belt, lances and the rest),
    // drawn on the body as IsoRim's own figure draws them. They are IsoRim's models, made on its own figure, so each is
    // carried over to the chibi by its mount's slot (anim.txt's gear lines, walk.GEAR, set in the preview tool; GearDefault
    // for an older export): the model's reference point goes to the slot's place and the model is scaled by its share.
    // Each slot is pushed out past the garments worn there as a weapon's holster is (Clearance), so a pack sits on a
    // duster or body armour, not in it, and a worn pack (a backpack) wholly past it.
    public sealed partial class SkinnedFigure
    {
        private const float GearBeltWidth = 0.19f;   // IsoRim's belt ring's half width (WornGear): a belt is stretched to the waist from it
        private const float GearGap = 0.006f;        // model units left between a pack or belt and what is under it
        private const float IsoRimWaist = 0.30f;     // the height of the reference point in IsoRim's models (its belts are made at 0.25 to 0.35)
        private const float IsoRimPackWide = 0.13f;  // how far to either side of the middle most of IsoRim's packs reach
        private const float IsoRimPackSide = 0.18f;  // and the widest of them, for what goes beside one
        private const float IsoRimPackDeep = 0.19f;  // and the deepest, out from its back plane, for what lies on one
        private const float IsoRimPackTall = 0.40f;  // and how far up from the reference point
        private const float IsoRimPackMiddle = 0.226f;  // and the height of their middles over it, on average, for what lies on one (from 0.19 to 0.26)

        // Where Clearance looks for a pack of this size: over its back plane, which starts at the waist, so a backpack
        // higher up isn't missed. The first is the slot's own place.
        private static Vector3[] PackSpots(float scale)
        {
            List<Vector3> spots = new List<Vector3>();
            for (int up = 0; up < 4; up++)
            {
                for (int i = 0; i < 3; i++)
                {
                    spots.Add(new Vector3((i == 2 ? -1f : i) * IsoRimPackWide, IsoRimPackTall * up / 3f, 0f) * scale);
                }
            }
            return spots.ToArray();
        }

        private sealed class GearSlot
        {
            public Holster place;     // bone, place at rest and the way out
            public float scale;       // the model's size, a share of IsoRim's
            public float depth = 1f;  // a belt: how much further its depth is stretched than its width
            public Quaternion turn = Quaternion.identity;   // the model's turn at rest, about its reference point (a belt's: the ring's middle)
        }

        private readonly Dictionary<string, GearSlot> gear = new Dictionary<string, GearSlot>();

        private void ReadGear(string[] p)
        {
            int b;
            if (p[0] == "gear" && p.Length > 10 && boneIndex.TryGetValue(p[2], out b))
            {
                // An older export has no turn on its gear lines.
                Quaternion turn = p.Length > 14 ? new Quaternion(Num(p[11]), Num(p[12]), Num(p[13]), Num(p[14])) : Quaternion.identity;
                Vector3[] spots = p[1] == "pack" ? PackSpots(Num(p[9])) : null;
                for (int i = 0; spots != null && i < spots.Length; i++)
                {
                    spots[i] = turn * spots[i];   // the back plane turns with the pack
                }
                gear[p[1]] = new GearSlot { place = new Holster { name = "gear_" + p[1], size = "gear", bone = b, at = Vec(p, 3), outward = Vec(p, 6).normalized, spots = spots },
                                            scale = Num(p[9]), depth = Num(p[10]), turn = turn };
            }
        }

        // The slot for a mount: the export's, else the numbers walk.GEAR had when this was written.
        private GearSlot GearOf(string mount)
        {
            GearSlot g;
            if (gear.TryGetValue(mount, out g))
            {
                return g;
            }
            int bone;
            bool pack = mount == "pack";
            if (!boneIndex.TryGetValue(pack ? "spine.002" : "spine", out bone))
            {
                return null;
            }
            g = new GearSlot
            {
                place = new Holster { name = "gear_" + mount, size = "gear", bone = bone,
                                      at = pack ? new Vector3(0f, 0.58f, -0.055f) : new Vector3(0.107f, 0.58f, 0f),
                                      outward = pack ? new Vector3(0f, 0f, -1f) : new Vector3(1f, 0f, 0f),
                                      spots = pack ? PackSpots(0.622f) : null },
                scale = 0.622f, depth = mount == "belt" ? 1.345f : 1f
            };
            gear[mount] = g;
            return g;
        }

        // An equipment pack's place at rest, before the bone's move for the shape: its reference point, the way out, and how
        // far out it goes, past the clothes under it and any backpack, which it is worn over. False without the slot.
        private bool GearPackPlace(Posed p, out Vector3 at, out Vector3 way, out float push)
        {
            GearSlot g = GearOf("pack");
            at = way = Vector3.zero;
            push = 0f;
            if (g == null)
            {
                return false;
            }
            at = g.place.at;
            way = g.place.outward;
            push = Clearance(p.shape, p.wornMask, g.place, 250) + GearGap;
            int worn = WornPack(p);
            PackRig rig = worn > 0 ? PackRigOf(worn) : null;
            if (rig != null)
            {
                float back = Vector3.Dot(rig.place.at, way) + PackPush(p, worn, rig) + rig.depth * p.backpackScale;
                push = Mathf.Max(push, back + GearGap - Vector3.Dot(at, way));
            }
            return true;
        }

        // How far an equipment pack's far side stands along way (model units, before the bone's move for the shape).
        private float GearPackOuter(Posed p, Vector3 way)
        {
            Vector3 at, out_;
            float push;
            GearSlot g = GearOf("pack");
            if (g == null || !GearPackPlace(p, out at, out out_, out push))
            {
                return float.NegativeInfinity;
            }
            return Vector3.Dot(at, way) + (push + IsoRimPackDeep * g.scale * p.packScale) * Vector3.Dot(out_, way);
        }

        // The height of an equipment pack's middle (model units, before the bone's move for the shape).
        private float GearPackMiddle(Posed p)
        {
            GearSlot g = GearOf("pack");
            return g != null ? g.place.at.y + IsoRimPackMiddle * g.scale * p.packScale : float.NegativeInfinity;
        }

        // How far to either side of its middle an equipment pack reaches (model units).
        private float GearPackSide(Posed p)
        {
            GearSlot g = GearOf("pack");
            return g != null ? IsoRimPackSide * g.scale * p.packScale : 0f;
        }

        // Where a mount's gear goes in the pose End left: the matrix that takes the gear's own model (IsoRim's figure
        // space) to the pose, model units (draw it scaled by the figure's size). mount is pack, belt or hip, as WornGearDef.
        public bool GearAt(Posed p, string mount, GarmentMask mask, out Matrix4x4 frame)
        {
            frame = Matrix4x4.identity;
            GearSlot g = GearOf(mount);
            if (g == null)
            {
                return false;
            }
            Holster h = g.place;
            float k = g.scale;
            Matrix4x4 local;
            if (mount == "belt")
            {
                // A ring about the slot's height, stretched to the waist: its half width is how far out the slot is, past
                // what stands out at either side, and its half depth the bare waist's, its depth's share of that, plus what
                // stands out in front and behind, its middle moved toward the side that stands out more (BeltPushes).
                Vector4 out_ = BeltPushes(p.shape, mask, h);
                float half = Mathf.Max(h.at.x + GearGap, 0.01f);
                float sx = (half + Mathf.Max(out_.x, out_.y)) / GearBeltWidth;
                float sz = (half * g.depth + (out_.z + out_.w) / 2f) / GearBeltWidth;
                local = Matrix4x4.Translate(new Vector3(0f, h.at.y, (out_.z - out_.w) / 2f)) * Matrix4x4.Rotate(g.turn) * Matrix4x4.Scale(new Vector3(sx, k, sz))
                    * Matrix4x4.Translate(new Vector3(0f, -IsoRimWaist, 0f));
                frame = p.whole[h.bone] * Matrix4x4.Translate(p.shape.boneMove[h.bone]) * local;
                return true;
            }
            float push = Clearance(p.shape, mask, h, 250 + (mount == "pack" ? 0 : 2)) + GearGap;
            Vector3 way;
            if (mount == "pack")
            {
                // Carried as a holstered weapon is, as big on every pawn; over a backpack if it has one (GearPackPlace).
                k *= p.packScale;
                Vector3 place;
                GearPackPlace(p, out place, out way, out push);
            }
            Vector3 at = h.at + h.outward * push;
            // A pack's reference is the middle of its back plane, a hip tool's its clip, 0.19 out.
            Vector3 reference = new Vector3(mount == "pack" ? 0f : GearBeltWidth, IsoRimWaist, 0f);
            local = Matrix4x4.Translate(at) * Matrix4x4.Rotate(g.turn) * Matrix4x4.Scale(new Vector3(k, k, k)) * Matrix4x4.Translate(-reference);
            frame = p.whole[h.bone] * Matrix4x4.Translate(p.shape.boneMove[h.bone]) * local;
            return true;
        }

        // The belt's ring is measured on its own rather than by Clearance's one line out of the slot: at rest the arms hang
        // beside the hips, so that line met a parka's sleeve or power armour's gauntlet (0.1 to 0.3 past the waist), and a
        // ring scaled evenly from it grew its depth by as much again. So it looks out of each side and straight in front and
        // behind, at the trunk and legs alone (WaistOf), and each way's push sets its own half of the ring.
        private const float BeltArmMost = 0.5f;   // a vertex weighted more than this to the arms is an arm's, not the waist's
        private const float BeltReach = 0.4f;     // model units: how far out from where it looks a surface is looked for (power armour's front is 0.15 from the middle)
        private const int BeltKey = 260;          // holsterPush's keys for the belt's four ways (260 .. 263), past the other gear's
        private static readonly string[] ArmBones = { "shoulder.", "upper_arm.", "forearm.", "hand.", "thumb." };
        private readonly Dictionary<int, bool[]> waistOf = new Dictionary<int, bool[]>();
        private readonly Dictionary<int, int[]> waistTris = new Dictionary<int, int[]>();

        // A piece's vertices that are not the arms', and its triangles with all three corners among them (by piece number).
        private bool[] WaistOf(int g, out int[] tris)
        {
            bool[] keep;
            if (waistOf.TryGetValue(g, out keep))
            {
                tris = waistTris[g];
                return keep;
            }
            Piece piece = pieces[g];
            bool[] arm = new bool[boneIndex.Count];
            foreach (KeyValuePair<string, int> b in boneIndex)
            {
                for (int i = 0; i < ArmBones.Length; i++)
                {
                    arm[b.Value] |= b.Key.StartsWith(ArmBones[i]);
                }
            }
            keep = new bool[piece.rest.Length];
            for (int i = 0; i < keep.Length; i++)
            {
                float onArm = 0f;
                for (int k = piece.firstWeight != null ? piece.firstWeight[i] : 0; piece.firstWeight != null && k < piece.firstWeight[i + 1]; k++)
                {
                    if (arm[piece.weightBone[k]])
                    {
                        onArm += piece.weight[k];
                    }
                }
                keep[i] = onArm <= BeltArmMost;
            }
            List<int> kept = new List<int>();
            for (int t = 0; t + 2 < piece.tris.Length; t += 3)
            {
                if (keep[piece.tris[t]] && keep[piece.tris[t + 1]] && keep[piece.tris[t + 2]])
                {
                    kept.Add(piece.tris[t]);
                    kept.Add(piece.tris[t + 1]);
                    kept.Add(piece.tris[t + 2]);
                }
            }
            tris = kept.ToArray();
            waistOf[g] = keep;
            waistTris[g] = tris;
            return keep;
        }

        // How much further out than the bare base body's the waist stands at the belt's height, in this shape and these
        // garments, model units: x out of the slot's side, y the other side, z straight along +z, w along -z.
        private Vector4 BeltPushes(Shape s, GarmentMask mask, Holster h)
        {
            mask &= ~UnclearMask;
            Vector3 middle = new Vector3(0f, h.at.y, h.at.z);
            Vector3 other = new Vector3(-h.outward.x, h.outward.y, h.outward.z);
            Vector3[] from = { h.at, new Vector3(-h.at.x, h.at.y, h.at.z), middle, middle };
            Vector3[] ways = { h.outward, other, Vector3.forward, Vector3.back };
            Vector4 pushes = Vector4.zero;
            Vector3 moved = s.boneMove[h.bone];
            int[] bodyTris;
            bool[] body = WaistOf(0, out bodyTris);
            for (int i = 0; i < 4; i++)
            {
                (GarmentMask, int) key = (mask, BeltKey + i);
                float push;
                if (!s.holsterPush.TryGetValue(key, out push))
                {
                    push = 0f;
                    float bare = Outer(pieces[0].rest, bodyTris, from[i], ways[i], BeltReach, body);
                    float outer = Outer(s.pieces[0].rest, bodyTris, from[i] + moved, ways[i], BeltReach, body);
                    for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
                    {
                        if ((mask & GarmentBit(g)) != 0)
                        {
                            int[] tris;
                            bool[] keep = WaistOf(g, out tris);
                            outer = Mathf.Max(outer, Outer(GarmentShape(s, g, mask).rest, tris, from[i] + moved, ways[i], BeltReach, keep));
                        }
                    }
                    if (!float.IsNegativeInfinity(outer) && !float.IsNegativeInfinity(bare))
                    {
                        push = Mathf.Max(outer - bare, 0f);
                    }
                    s.holsterPush[key] = push;
                }
                pushes[i] = push;
            }
            return pushes;
        }

        public float HipGrab = 0.45f;    // the share of hip_draw at which the hand closes on the hip gear in its slot (anim.txt's hip_timing)
        public float HipSlide = 0.2f;    // and the share after that over which the gear goes from the slot into the hand

        // Where the hip gear goes in the left hand in the pose End left, as GearAt gives the slot's (anim.txt's tool hip line,
        // walk.hip_rest: placed in the base rest pose, so moved with the hand's joint for the shape first). False without it.
        public bool HipHeldAt(Posed p, out Matrix4x4 frame)
        {
            frame = Matrix4x4.identity;
            Tool tool = ToolOf("hip");
            int b;
            if (tool == null || !boneIndex.TryGetValue(tool.bone, out b))
            {
                return false;
            }
            frame = p.whole[b] * Matrix4x4.Translate(p.shape.boneMove[b]) * tool.place;
            return true;
        }
    }

    public static partial class WalkFigures
    {
        public static bool WornGears   // false: packs, belts and head wear are not drawn (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.wornGear; }
        }

        private static readonly Dictionary<ThingDef, string> gearMounts = new Dictionary<ThingDef, string>();

        // The IsoRim mount an apparel's worn gear model goes on (IsoRim's worn gear: pack, belt or hip, as anim.txt's gear
        // lines name them), null for none; read from Api.ReadThingModel once per apparel def. None for apparel with a garment
        // of its own (the radio pack), which is drawn in its place.
        private static string GearMountOf(Apparel a)
        {
            string mount;
            if (a == null)
            {
                return null;
            }
            if (gearMounts.TryGetValue(a.def, out mount))
            {
                return mount;
            }
            ThingModelInfo info;
            mount = null;
            if ((skin == null || skin.GarmentOf(a.def) <= 0) && Api.ReadThingModel(a, ThingModelUse.Worn, out info) && info.Model != null)
            {
                mount = info.Mount == WornMount.Back ? "pack" : info.Mount == WornMount.Belt ? "belt" : info.Mount == WornMount.Hip ? "hip" : null;
            }
            gearMounts[a.def] = mount;
            return mount;
        }

        private const float KidPackSize = 0.7f;   // a child's backpack against a grown-up's, one size for every child: on an eight year old it looks as a grown-up's does on them

        // A grown figure's size at the standard pawn size: what a backpack and a pack on the back are carried at, on anyone.
        private static float PackScaleOf(float size)
        {
            return ScaleUp / size;
        }

        // The packs, belts and waist tools the pawn wears, on its back and waist; the hip gear hipHeld of the way from its slot
        // to the left hand (HipShare).
        private static void DrawWornGear(Pawn pawn, GarmentMask mask, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, float hipHeld)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            if (!WornGears || worn == null)
            {
                return;
            }
            for (int i = 0; i < worn.Count; i++)
            {
                string mount = GearMountOf(worn[i]);
                Matrix4x4 frame;
                ThingModelInfo info;
                if (mount == null || !Api.ReadThingModel(worn[i], ThingModelUse.Worn, out info) || info.Model == null || !skin.GearAt(fig, mount, mask, out frame))
                {
                    continue;
                }
                Matrix4x4 held;
                if (mount == "hip" && hipHeld > 0f && skin.HipHeldAt(fig, out held))
                {
                    frame = Between(frame, held, hipHeld);
                }
                Api.DrawModel(info.Model, at, yaw, Matrix4x4.Scale(new Vector3(size, size, size)) * frame, info.Tint, info.Swatch);
            }
        }
    }
}
