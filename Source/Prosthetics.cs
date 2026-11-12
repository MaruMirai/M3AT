using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Prosthetic limbs (skinned figure only, the prosthetics mod option): a limb the game has replaced with an added
    // part (Hediff_AddedPart: a peg leg, a wooden hand or foot, a simple prosthetic, a bionic or archotech limb, a power
    // claw, or another mod's) is drawn as that part rather than as skin. Nothing is modelled or exported for it: the
    // body's own vertices on the limb's bones are coloured as the part's material, with those at a joint (the elbow,
    // wrist, knee and ankle, and where it meets the body) darker; a peg leg's shin and foot are drawn in to a peg about
    // the shin's line, and a simple prosthetic's forearm or shin to a pylon between a socket on the stump and a hand or
    // foot shell, each segment its own colour; a bionic one has panel seams and lights laid on as decals (Wounds,
    // BionicDecals). So it bends, takes the body's shape and is hidden under clothes as the limb is.
    //
    // A pawn's prosthetics are one number (limbs): four bits for each of LimbBones' eight slots, the look (0 none, else
    // a Look + 1) and whether it is a peg.
    public sealed partial class SkinnedFigure
    {
        public enum LimbLook { Wood, Simple, Bionic, Archotech }

        // The bone each slot starts at: all of it and what is below is the prosthetic.
        public static readonly string[] LimbBones =
        {
            "upper_arm.L", "hand.L", "upper_arm.R", "hand.R", "thigh.L", "foot.L", "thigh.R", "foot.R",
        };
        public const int LimbPeg = 8;   // the bit, in a slot's four, for a peg

        private const float LimbShare = 0.5f;    // a vertex is the prosthetic's from this much of its weight on the limb's bones
        private const float JointBand = 0.03f;   // and a joint's, not a plate's, within this of a joint along the limb, cells at rest
        private const float JointRides = 0.05f;  // if it rides the bone at that joint or the one above by this much
        private const float PegThin = 0.3f;     // a peg's width, of the shin's
        private const float LegPylon = 0.28f;   // a simple prosthetic leg's shin, of the shin's: a pylon from the knee to the foot
        private const float ArmPylon = 0.4f;    // and a simple prosthetic arm's forearm, of the forearm's

        // A simple prosthetic's socket (its upper arm or thigh) and hand or foot shell: moulded plastic, by its steel pylon.
        // (grey, not beige: a beige socket read as skin on a tan pawn)
        private static readonly Color32 SimpleShell = new Color32(184, 188, 194, 255);
        private static readonly Color32 SimplePylon = new Color32(82, 86, 94, 255);
        // A leg's shin bone carries the heel too, so its foot shell is told by height: up to this over the foot's joint, cells at rest.
        private const float ShellAboveAnkle = 0.04f;

        // Each look's plates and joints.
        private static readonly Color32[] LimbPlate =
        {
            new Color32(140, 98, 60, 255),     // wood
            new Color32(146, 150, 156, 255),   // simple: plain steel (its pylon and shell are SimplePylon and SimpleShell)
            new Color32(84, 98, 118, 255),     // bionic: dark blued metal
            new Color32(176, 178, 122, 255),   // archotech: pale olive, as the game's archotech art
        };
        // (only a little darker: a vertex's colour runs out across its triangles, so a strong one reads as a smudge)
        private static readonly Color32[] LimbJoint =
        {
            new Color32(106, 72, 44, 255),
            new Color32(102, 106, 112, 255),
            new Color32(50, 60, 76, 255),
            new Color32(132, 136, 88, 255),
        };

        private readonly Dictionary<int, Color32[]> limbPaint = new Dictionary<int, Color32[]>();   // by limbs (PaintOf)
        private bool[] limbEnd;      // which bones are a hand or a foot itself
        private int[] limbSegment;   // each bone's segment: itself, but a hand's or foot's own bones are all the hand or foot

        // Gives the pose its prosthetics (after Begin, as Cut): the body is coloured by them when drawn, a peg's shin is
        // thinned with all below it, and a simple prosthetic's forearm or shin alone (its hand or foot left whole).
        public void Fit(Posed p, int limbs)
        {
            p.limbs = limbs;
            // No shoe on a prosthetic leg or foot (Unshod).
            p.bare = ((limbs >> 16 | limbs >> 20) & 15) != 0 ? 1 : 0;
            p.bare |= ((limbs >> 24 | limbs >> 28) & 15) != 0 ? 2 : 0;
            for (int slot = 0; limbs != 0 && slot < LimbBones.Length; slot += 2)
            {
                int bits = limbs >> (slot * 4) & 15;
                bool leg = slot >= 4, peg = (bits & LimbPeg) != 0, simple = (bits & 7) == (int)LimbLook.Simple + 1;
                if (!peg && !simple)
                {
                    continue;
                }
                string side = LimbBones[slot].Substring(LimbBones[slot].Length - 2);
                int b, mid;
                if (boneIndex.TryGetValue(LimbBones[slot], out b) && boneIndex.TryGetValue((leg ? "shin" : "forearm") + side, out mid)
                    && boneParent[mid] == b)
                {
                    p.thin[mid] = mid;
                    p.thinBy[mid] = peg ? PegThin : leg ? LegPylon : ArmPylon;
                    p.thinAll[mid] = peg;
                    p.pylons = true;   // clothes hang over it at their own width
                }
            }
        }

        private readonly Dictionary<(Piece, int), bool[]> bareLeg = new Dictionary<(Piece, int), bool[]>();   // OnBareLeg's, by piece and feet
        private readonly Dictionary<(int[], int), int[]> unshod = new Dictionary<(int[], int), int[]>();   // Unshod's, by list cut and feet

        // tris (of garment g) less, for the shoes (withLegwear), their triangles on a bare foot (Posed.bare): no shoe is worn
        // on a peg or a prosthetic. HiddenBy gives back the body they hid there.
        private int[] Unshod(Posed p, int g, int[] tris)
        {
            if (p.bare == 0 || !pieces[g].withLegwear)
            {
                return tris;
            }
            int[] kept;
            if (!unshod.TryGetValue((tris, p.bare), out kept))
            {
                bool[] on = OnBareLeg(pieces[g], p.bare);
                List<int> list = new List<int>(tris.Length);
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    if (!on[tris[k]] && !on[tris[k + 1]] && !on[tris[k + 2]])
                    {
                        list.Add(tris[k]);
                        list.Add(tris[k + 1]);
                        list.Add(tris[k + 2]);
                    }
                }
                kept = list.ToArray();
                unshod[(tris, p.bare)] = kept;
            }
            return kept;
        }

        // Which of a piece's vertices ride a bare leg (bare's sides: its thigh and all below it) by LimbShare or more.
        private bool[] OnBareLeg(Piece piece, int bare)
        {
            bool[] on;
            if (bareLeg.TryGetValue((piece, bare), out on))
            {
                return on;
            }
            bool[] bone = new bool[boneParent.Length];
            int b;
            if ((bare & 1) != 0 && boneIndex.TryGetValue(LimbBones[4], out b))
            {
                bone[b] = true;
            }
            if ((bare & 2) != 0 && boneIndex.TryGetValue(LimbBones[6], out b))
            {
                bone[b] = true;
            }
            for (int i = 0; i < boneOrder.Length; i++)
            {
                int c = boneOrder[i];
                bone[c] |= boneParent[c] >= 0 && bone[boneParent[c]];
            }
            on = new bool[piece.rest.Length];
            for (int v = 0; v < on.Length; v++)
            {
                float w = 0f;
                for (int k = piece.firstWeight[v]; k < piece.firstWeight[v + 1]; k++)
                {
                    w += bone[piece.weightBone[k]] ? piece.weight[k] : 0f;
                }
                on[v] = w >= LimbShare;
            }
            bareLeg[(piece, bare)] = on;
            return on;
        }

        // What a thinned bone's vertices are moved by at rest, before its own matrix: drawn in to s of their distance from
        // the line from about's head toward the first bone below it (a shin's toward the ankle), kept as far along it. A
        // peg's foot goes with its shin (Place), so the peg reaches the ground where the sole did.
        private Matrix4x4 Thinned(Vector3[] shapedHead, int about, float s)
        {
            int tip = -1;
            for (int b = 0; b < boneParent.Length && tip < 0; b++)
            {
                if (boneParent[b] == about)
                {
                    tip = b;
                }
            }
            Vector3 a = shapedHead[about];
            Vector3 u = tip >= 0 ? (shapedHead[tip] - a).normalized : Vector3.down;
            float k = 1f - s;
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = s + k * u.x * u.x; m.m01 = k * u.x * u.y; m.m02 = k * u.x * u.z;
            m.m10 = k * u.y * u.x; m.m11 = s + k * u.y * u.y; m.m12 = k * u.y * u.z;
            m.m20 = k * u.z * u.x; m.m21 = k * u.z * u.y; m.m22 = s + k * u.z * u.z;
            Vector3 back = a - m.MultiplyVector(a);   // so the line itself stays put
            m.m03 = back.x; m.m13 = back.y; m.m23 = back.z;
            return m;
        }

        // Which of a piece's vertices are on a prosthetic: LimbShare or more of their weight on its bones.
        private bool[] OnLimbs(Piece piece, int limbs)
        {
            bool[] bone = new bool[boneParent.Length];
            for (int slot = 0; slot < LimbBones.Length; slot++)
            {
                int b;
                if ((limbs >> (slot * 4) & 15) != 0 && boneIndex.TryGetValue(LimbBones[slot], out b))
                {
                    bone[b] = true;
                }
            }
            for (int i = 0; i < boneOrder.Length; i++)
            {
                int b = boneOrder[i];
                bone[b] |= boneParent[b] >= 0 && bone[boneParent[b]];
            }
            int n = piece.rest.Length;
            bool[] on = new bool[n];
            for (int v = 0; v < n; v++)
            {
                float w = 0f;
                for (int k = piece.firstWeight[v]; k < piece.firstWeight[v + 1]; k++)
                {
                    w += bone[piece.weightBone[k]] ? piece.weight[k] : 0f;
                }
                on[v] = w >= LimbShare;
            }
            return on;
        }

        // The body's vertex colours for these prosthetics, alpha 0 where it is skin; null for none. Made once per mix.
        private Color32[] PaintOf(int limbs)
        {
            if (limbs == 0)
            {
                return null;
            }
            Color32[] paint;
            if (limbPaint.TryGetValue(limbs, out paint))
            {
                return paint;
            }
            int bones = boneParent.Length;
            if (limbSegment == null)
            {
                limbSegment = new int[bones];
                foreach (KeyValuePair<string, int> named in boneIndex)
                {
                    limbSegment[named.Value] = named.Value;
                }
                // A hand or foot itself (hand.L, not its fingers, hand.L.001) takes all below it into its segment.
                bool[] end = limbEnd = new bool[bones];
                foreach (KeyValuePair<string, int> named in boneIndex)
                {
                    end[named.Value] = named.Key.Length == 6
                        && (named.Key.StartsWith("hand.", StringComparison.Ordinal) || named.Key.StartsWith("foot.", StringComparison.Ordinal));
                }
                for (int i = 0; i < boneOrder.Length; i++)
                {
                    int b = boneOrder[i], parent = boneParent[b];
                    if (parent >= 0 && end[limbSegment[parent]])
                    {
                        limbSegment[b] = limbSegment[parent];
                    }
                }
            }
            // Each bone's slot, a hand's or foot's own over the arm's or leg's it is on.
            int[] slotOf = new int[bones];
            for (int b = 0; b < bones; b++)
            {
                slotOf[b] = -1;
            }
            for (int slot = 0; slot < LimbBones.Length; slot++)
            {
                int b;
                if ((limbs >> (slot * 4) & 15) != 0 && boneIndex.TryGetValue(LimbBones[slot], out b))
                {
                    slotOf[b] = slot;
                }
            }
            for (int i = 0; i < boneOrder.Length; i++)
            {
                int b = boneOrder[i];
                if (slotOf[b] < 0 && boneParent[b] >= 0)
                {
                    slotOf[b] = slotOf[boneParent[b]];
                }
            }
            // The joints: each segment of a prosthetic where it starts (a shoulder, elbow, wrist, hip, knee, ankle), and
            // the line the limb runs along there: its own bone's, a hand's or foot's the forearm's or shin's.
            List<int> joints = new List<int>();
            List<Vector3> along = new List<Vector3>();
            for (int b = 0; b < bones; b++)
            {
                if (slotOf[b] < 0 || limbSegment[b] != b)
                {
                    continue;
                }
                int from = b, to = -1;
                if (limbEnd[b])
                {
                    from = boneParent[b];
                    to = b;
                }
                for (int c = 0; c < bones && to < 0; c++)
                {
                    to = boneParent[c] == b ? c : -1;
                }
                if (from < 0 || to < 0 || (boneHead[to] - boneHead[from]).sqrMagnitude < 1e-8f)
                {
                    continue;
                }
                joints.Add(b);
                along.Add((boneHead[to] - boneHead[from]).normalized);
            }
            Piece body = pieces[0];
            int n = body.rest.Length;
            paint = new Color32[n];
            float[] rides = new float[bones];
            float[] onSlot = new float[LimbBones.Length];
            for (int v = 0; v < n; v++)
            {
                float all = 0f;
                int slot = -1, segment = -1;
                for (int k = body.firstWeight[v]; k < body.firstWeight[v + 1]; k++)
                {
                    int b = body.weightBone[k];
                    rides[limbSegment[b]] += body.weight[k];
                    if (slotOf[b] < 0)
                    {
                        continue;
                    }
                    if (segment < 0 || rides[limbSegment[b]] > rides[segment])
                    {
                        segment = limbSegment[b];
                    }
                    all += body.weight[k];
                    onSlot[slotOf[b]] += body.weight[k];
                    if (slot < 0 || onSlot[slotOf[b]] > onSlot[slot])
                    {
                        slot = slotOf[b];
                    }
                }
                bool joint = false;
                for (int j = 0; j < joints.Count && !joint && slot >= 0 && all >= LimbShare; j++)
                {
                    int b = joints[j], above = boneParent[b];
                    joint = (rides[b] >= JointRides || (above >= 0 && rides[limbSegment[above]] >= JointRides))
                        && Mathf.Abs(Vector3.Dot(body.rest[v] - boneHead[b], along[j])) <= JointBand;
                }
                for (int k = body.firstWeight[v]; k < body.firstWeight[v + 1]; k++)
                {
                    rides[limbSegment[body.weightBone[k]]] = 0f;
                    if (slotOf[body.weightBone[k]] >= 0)
                    {
                        onSlot[slotOf[body.weightBone[k]]] = 0f;
                    }
                }
                if (slot < 0 || all < LimbShare)
                {
                    continue;
                }
                int look = Mathf.Clamp((limbs >> (slot * 4) & 7) - 1, 0, LimbPlate.Length - 1);
                // A simple one's socket and shell are plastic, its pylon steel: the segment it rides most.
                bool simple = look == (int)LimbLook.Simple;
                bool pylon = simple && segment >= 0 && limbSegment[segment] == segment && !limbEnd[segment]
                    && boneParent[segment] >= 0 && slotOf[boneParent[segment]] == slot;
                int foot;
                if (pylon && LimbBones[slot].StartsWith("thigh", StringComparison.Ordinal)
                    && boneIndex.TryGetValue("foot" + LimbBones[slot].Substring(LimbBones[slot].Length - 2), out foot)
                    && body.rest[v].y < boneHead[foot].y + ShellAboveAnkle)
                {
                    pylon = false;   // the heel: the foot shell's
                }
                paint[v] = joint && !simple ? LimbJoint[look] : !simple ? LimbPlate[look] : pylon ? SimplePylon
                    : joint ? LimbJoint[look] : SimpleShell;
            }
            limbPaint[limbs] = paint;
            return paint;
        }
    }

    public static partial class WalkFigures
    {
        public static bool Prosthetics   // false: a replaced limb is drawn as skin (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.prosthetics; }
        }
        public static bool ProstheticEyesShow   // false: a bionic or archotech eye is drawn as the pawn's own (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.prostheticEyes; }
        }
        public static bool ArchotechShows   // false: an archotech limb is drawn as flesh, as the game's text has it look (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.archotechProsthetics; }
        }

        // The added part drawn on a body part, on it or on a part it is in (an arm's on its hand); null for none, or one
        // drawn as flesh (Shows).
        private static Hediff_AddedPart AddedOn(HediffSet set, BodyPartRecord part)
        {
            List<Hediff> hediffs = set.hediffs;
            for (BodyPartRecord p = part; p != null; p = p.parent)
            {
                for (int i = 0; i < hediffs.Count; i++)
                {
                    Hediff_AddedPart added = hediffs[i] as Hediff_AddedPart;
                    if (added != null && added.Part == p)
                    {
                        return Shows(added.def) ? added : null;
                    }
                }
            }
            return null;
        }

        // Whether an added part is drawn as itself: with the option on, and an archotech one with its own on too.
        private static bool Shows(HediffDef def)
        {
            return Prosthetics && (ArchotechShows || (LookOf(def) & 7) != (int)SkinnedFigure.LimbLook.Archotech + 1);
        }

        private static readonly List<LimbDecal> eyeSign = new List<LimbDecal>();

        // A bone of a limb with decals, the load ID of its added part (with a number for the bone), and whether it is
        // archotech (else bionic).
        internal struct LimbDecal
        {
            public string bone;
            public int seed;
            public bool archotech;
        }

        private static bool Decorated(int look)
        {
            return look == (int)SkinnedFigure.LimbLook.Bionic + 1 || look == (int)SkinnedFigure.LimbLook.Archotech + 1;
        }

        // Each bone of a bionic or archotech limb drawn as one, for its decals (Wounds.LimbDecals).
        internal static void DecalLimbs(Pawn pawn, List<LimbDecal> into)
        {
            int limbs = LimbsOf(pawn.health.hediffSet);
            for (int slot = 0; slot < SkinnedFigure.LimbBones.Length; slot++)
            {
                int look = limbs >> (slot * 4) & 7;
                if (!Decorated(look))
                {
                    continue;
                }
                bool archotech = look == (int)SkinnedFigure.LimbLook.Archotech + 1;
                string start = SkinnedFigure.LimbBones[slot], side = start.Substring(start.Length - 2);
                int seed = SeedOf(pawn.health.hediffSet, slot);
                if (start.StartsWith("upper_arm", StringComparison.Ordinal))
                {
                    into.Add(new LimbDecal { bone = start, seed = seed, archotech = archotech });
                    into.Add(new LimbDecal { bone = "forearm" + side, seed = seed * 3 + 1, archotech = archotech });
                    // its hand too, unless it has its own (a power claw): its own slot then
                    if ((limbs >> ((slot + 1) * 4) & 15) == 0)
                    {
                        into.Add(new LimbDecal { bone = "hand" + side, seed = seed * 3 + 2, archotech = archotech });
                    }
                }
                else if (start.StartsWith("thigh", StringComparison.Ordinal))
                {
                    into.Add(new LimbDecal { bone = start, seed = seed, archotech = archotech });
                    into.Add(new LimbDecal { bone = "shin" + side, seed = seed * 3 + 1, archotech = archotech });
                    if ((limbs >> ((slot + 1) * 4) & 15) == 0)
                    {
                        into.Add(new LimbDecal { bone = "foot" + side, seed = seed * 3 + 2, archotech = archotech });
                    }
                }
                else
                {
                    into.Add(new LimbDecal { bone = start, seed = seed, archotech = archotech });
                }
            }
        }

        // Each bionic or archotech eye shown as one: its side ("L" or "R", in bone), its added part's load ID and whether it
        // is archotech. Another mod's eye goes by LookOf too; wood and simple ones (none in the game) as bionic.
        internal static void ProstheticEyes(Pawn pawn, List<LimbDecal> into)
        {
            if (!Prosthetics || !ProstheticEyesShow)
            {
                return;
            }
            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff_AddedPart added = hediffs[i] as Hediff_AddedPart;
                if (added == null || added.Part == null || added.Part.def.defName != "Eye" || !Shows(added.def))
                {
                    continue;
                }
                into.Add(new LimbDecal
                {
                    bone = SideOf(added.Part) ?? "R",
                    seed = added.loadID,
                    archotech = (LookOf(added.def) & 7) == (int)SkinnedFigure.LimbLook.Archotech + 1,
                });
            }
        }

        // What DecalLimbs and ProstheticEyes give, as one number, so the decals are laid on again only when it changes.
        internal static int DecalSign(Pawn pawn)
        {
            int limbs = LimbsOf(pawn.health.hediffSet), sign = limbs;
            eyeSign.Clear();
            ProstheticEyes(pawn, eyeSign);
            for (int i = 0; i < eyeSign.Count; i++)
            {
                sign = sign * 31 + eyeSign[i].seed * (eyeSign[i].archotech ? 3 : 1);
            }
            for (int slot = 0; slot < SkinnedFigure.LimbBones.Length; slot++)
            {
                if (Decorated(limbs >> (slot * 4) & 7))
                {
                    sign = sign * 31 + SeedOf(pawn.health.hediffSet, slot);
                }
            }
            return sign;
        }

        // The load ID of the added part in a slot (its bone's body part), 0 if none is found.
        private static int SeedOf(HediffSet set, int slot)
        {
            string name = slot < 4 ? (slot % 2 == 0 ? "Shoulder|Arm" : "Hand") : (slot % 2 == 0 ? "Leg" : "Foot");
            string side = slot % 4 < 2 ? "L" : "R";
            List<Hediff> hediffs = set.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff_AddedPart added = hediffs[i] as Hediff_AddedPart;
                if (added != null && added.Part != null && ("|" + name + "|").Contains("|" + added.Part.def.defName + "|")
                    && SideOf(added.Part) == side)
                {
                    return added.loadID;
                }
            }
            return 0;
        }

        private static readonly Dictionary<HediffDef, int> limbLooks = new Dictionary<HediffDef, int>();   // LookOf, by added part

        // The pawn's prosthetics as SkinnedFigure.Fit takes them: each added part on an arm (the game's Shoulder or Arm),
        // hand, leg or foot, by its side. Looked at every draw; a pawn has few hediffs.
        private static int LimbsOf(HediffSet set)
        {
            if (!Prosthetics)
            {
                return 0;
            }
            int limbs = 0;
            List<Hediff> hediffs = set.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff_AddedPart added = hediffs[i] as Hediff_AddedPart;
                BodyPartRecord part = added != null ? added.Part : null;
                if (part == null || !Shows(added.def))
                {
                    continue;
                }
                string name = part.def.defName;
                int slot = name == "Shoulder" || name == "Arm" ? 0 : name == "Hand" ? 1 : name == "Leg" ? 4 : name == "Foot" ? 5 : -1;
                string side = slot >= 0 ? SideOf(part) : null;
                if (side == null)
                {
                    continue;
                }
                slot += side == "R" ? 2 : 0;
                limbs = (limbs & ~(15 << (slot * 4))) | (LookOf(added.def) << (slot * 4));
            }
            return limbs;
        }

        // An added part's four bits: what it is made as, by its name (the game's own: PegLeg, WoodenHand,
        // SimpleProstheticArm, BionicLeg, ArchotechArm ...), else by the tech level of the thing it is installed from,
        // else by how well it works, so another mod's parts get a look too; and whether it is a peg.
        private static int LookOf(HediffDef def)
        {
            int bits;
            if (limbLooks.TryGetValue(def, out bits))
            {
                return bits;
            }
            string name = def.defName ?? "";
            SkinnedFigure.LimbLook look;
            ThingDef thing = def.spawnThingOnRemoved;
            float works = def.addedPartProps != null ? def.addedPartProps.partEfficiency : 1f;
            if (Named(name, "Peg") || Named(name, "Wood"))
            {
                look = SkinnedFigure.LimbLook.Wood;
            }
            else if (Named(name, "Archo"))
            {
                look = SkinnedFigure.LimbLook.Archotech;
            }
            else if (Named(name, "Bionic"))
            {
                look = SkinnedFigure.LimbLook.Bionic;
            }
            else if (Named(name, "Simple"))
            {
                look = SkinnedFigure.LimbLook.Simple;
            }
            else if (thing != null && thing.techLevel != TechLevel.Undefined)
            {
                look = thing.techLevel <= TechLevel.Medieval ? SkinnedFigure.LimbLook.Wood
                    : thing.techLevel <= TechLevel.Industrial ? SkinnedFigure.LimbLook.Simple
                    : thing.techLevel <= TechLevel.Spacer ? SkinnedFigure.LimbLook.Bionic : SkinnedFigure.LimbLook.Archotech;
            }
            else
            {
                look = works <= 0.8f ? SkinnedFigure.LimbLook.Wood : works < 1.1f ? SkinnedFigure.LimbLook.Simple
                    : works < 1.4f ? SkinnedFigure.LimbLook.Bionic : SkinnedFigure.LimbLook.Archotech;
            }
            bits = (int)look + 1 + (Named(name, "Peg") ? SkinnedFigure.LimbPeg : 0);
            limbLooks[def] = bits;
            return bits;
        }

        private static bool Named(string name, string word)
        {
            return name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The autotest's: the pawn's prosthetics as LimbsOf has them, in hex, a digit a slot from the right.
        internal static string AutoLimbs(Pawn pawn)
        {
            return LimbsOf(pawn.health.hediffSet).ToString("x8");
        }

        // The autotest's: how far the pawn holds a hurt place (HurtOf), and how badly it limps (LimpOf), and on which leg.
        internal static string AutoHurt(Pawn pawn)
        {
            string hurt, limp;
            float held = HurtOf(pawn, out hurt), limping = LimpOf(pawn, out limp);
            return "hold " + held.ToString("0.00") + " " + (hurt ?? "-") + ", limp " + limping.ToString("0.00") + " " + (limp ?? "-");
        }

        // Whether an injury's part is a prosthetic's, drawn as one: no wound, blood or bandage is laid on it.
        internal static bool OnProsthetic(HediffSet set, BodyPartRecord part)
        {
            return part != null && AddedOn(set, part) != null;
        }
    }
}
