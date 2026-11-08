using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace M3AT
{
    // Holstered weapons: a weapon the pawn has but is not holding (the equipped one while it is not drafted or aiming,
    // any in its inventory) is drawn on the body, as IsoRim draws a held one (the sprite as a solid slab). The export's
    // slots (walk.HOLSTERS, anim.txt's holster lines) each ride a bone and take one weapon of their size: small (pistols,
    // one-handed melee), grenade (anything thrown) or big (the rest). A slot is pushed out by however far the pawn's body and worn garments stand
    // out past the bare base body there (Clearance), so a weapon on the back sits on a duster or a backpack, not inside
    // it, and a big slot further out than that if it would cut into the weapon in a big slot under it (HolsterOver, so
    // the second weapon on the back lies on the first, however thick it is). A big weapon doesn't lie on a backpack
    // (walk.HOLSTER_PACKS) but goes down its side: slots marked for a worn pack take the place of those marked for none.
    // A belt round the waist takes the grenades' place on it the same way: they go on the upper left arm instead.
    // A backpack is carried as a holstered weapon is: it rides one bone whole, keeps its shape on any body and is as big
    // on every pawn (Packed), and what lies on it or beside it is placed from where it really is (HolsterClear, PackSide).
    public sealed partial class SkinnedFigure
    {
        private const float HolsterReach = 0.05f;   // model units: how far to the side of a slot's line out a surface counts
        private const float HolsterDepth = 0.15f;   // model units: and how far along it, in and out from the slot
        private const float SameWayOut = 0.9f;      // cosine: two slots' ways out this close count as the same way
        private const float BigHalfLength = 0.25f;  // model units: how far along its aim either way a big slot's weapon reaches, about a rifle's
        private const float PackLook = 0.8f;        // of a backpack's height and width: how far out across its face Clearance looks
        private const float SideFar = 0.08f;        // model units: how far out from a slot beside a pack a surface counts
        private const float OverPackMiddle = 0.03f; // model units: how far above a pack's middle a weapon lying on it goes: it lies nearer the camera, which looks down, so level with the middle it looks low on the pack
        private const int SideKey = 100, PackKey = 200;   // holsterPush's keys past the slot numbers (the gear's are 250 on)
        private static readonly float[] AlongBig = { 0f, -0.5f, 0.5f, -1f, 1f };   // where Clearance looks along it, shares of that, the slot's own place first

        internal sealed class Holster
        {
            public string name;
            public string size;       // the weapons it takes: "small", "big" or "grenade" (walk.TYPE_HOLSTER)
            public int bone;          // the bone it rides
            public Vector3 at;        // its place at rest, model space, model units
            public Vector3 aim;       // the way the muzzle points at rest
            public float roll;        // degrees about that line, its top kept up at 0 (WalkFigures.TopOf)
            public float behind;      // the sprite's middle along the line from at, a share of its length
            public Vector3 outward;   // the way out of the body, pushed along by Clearance
            public bool melee;        // has its own place for a melee weapon
            public Vector3 meleeAt, meleeAim;
            public float meleeRoll;
            public int pack;          // 1: used only with a pack worn, measured from the pack as well as the body; -1: only with none; 0: either
            public int belt;          // 1: used only with a belt worn; -1: only with none; 0: either
            public Vector3[] spots;   // where Clearance looks, from at, past the one spot or a big slot's line; null: those. A worn pack is cleared whole there
        }

        private readonly List<Holster> holsters = new List<Holster>();
        private readonly List<string> packDefs = new List<string>();   // the apparel that counts as a pack (walk.HOLSTER_PACKS)
        private GarmentMask packMask = -1;   // the garments drawn for it, as a mask (bit g: garment g); -1: not worked out yet
        private const float MeleeSlotOffset = -65f;   // walk.MELEE_SLOT_OFFSET: the equippedAngleOffset a melee place is set for

        public int Holsters
        {
            get { return holsters.Count; }
        }

        public string HolsterSize(int slot)
        {
            return holsters[slot].size;
        }

        public string HolsterName(int slot)
        {
            return holsters[slot].name;
        }

        // Whether a slot is used with a pack worn or none, and a belt worn or none.
        public bool HolsterFits(int slot, bool pack, bool belt)
        {
            Holster h = holsters[slot];
            return (h.pack == 0 || h.pack > 0 == pack) && (h.belt == 0 || h.belt > 0 == belt);
        }

        private GarmentMask PackMask
        {
            get
            {
                if (packMask < 0)
                {
                    packMask = 0;
                    for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
                    {
                        if (System.Array.Exists(pieces[g].defs, packDefs.Contains))
                        {
                            packMask |= GarmentBit(g);
                        }
                    }
                }
                return packMask;
            }
        }

        // Webbing that holsters, packs and gear lie over rather than are pushed out by: the heavy bandolier and Combat
        // Extended's tactical vest are thin straps and pouches, and pushing a weapon or pack out past their pouches left it
        // standing well off the back.
        private static readonly string[] NoClearanceDefs = { "Apparel_Bandolier", "CE_Apparel_TacVest" };

        // The worn-garment bits of the garments in no clearance: packs (placed by it), shields (they go with the hand) and
        // the NoClearanceDefs webbing.
        private GarmentMask unclearMask = -1;
        private GarmentMask UnclearMask
        {
            get
            {
                if (unclearMask < 0)
                {
                    unclearMask = PackMask | HeldMask;
                    for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
                    {
                        if (System.Array.Exists(pieces[g].defs, d => System.Array.IndexOf(NoClearanceDefs, d) >= 0))
                        {
                            unclearMask |= GarmentBit(g);
                        }
                    }
                }
                return unclearMask;
            }
        }

        // Whether this apparel is drawn as a pack.
        public bool IsPack(ThingDef def)
        {
            int g = GarmentOf(def);
            return g > 0 && g <= MaxHiding && (PackMask & (GarmentBit(g))) != 0;
        }

        // Where a draw clip's hand closes on the handle (walk.DRAW_AT), a share of the clip.
        public float DrawGrab { get; private set; }

        // The share of a draw clip before that over which the weapon is swung from the slot to the hand (walk.DRAW_SIZES).
        public float DrawSlide(string clip)
        {
            float slide;
            return drawSlides.TryGetValue(clip, out slide) ? slide : 0f;
        }

        private readonly Dictionary<string, float> drawSlides = new Dictionary<string, float>();

        private void ReadHolster(string[] p)
        {
            int b;
            if (p[0] == "holster" && p.Length > 14 && boneIndex.TryGetValue(p[3], out b))
            {
                Holster h = new Holster
                {
                    name = p[1], size = p[2], bone = b, at = Vec(p, 4), aim = Vec(p, 7).normalized, roll = Num(p[10]),
                    behind = Num(p[11]), outward = Vec(p, 12).normalized
                };
                holsters.Add(h);
            }
            else if (p[0] == "holster_melee" && p.Length > 8)
            {
                Holster h = holsters.Find(x => x.name == p[1]);
                if (h != null)
                {
                    h.melee = true;
                    h.meleeAt = Vec(p, 2);
                    h.meleeAim = Vec(p, 5).normalized;
                    h.meleeRoll = Num(p[8]);
                }
            }
            else if (p[0] == "holster_pack" && p.Length > 2)
            {
                Holster h = holsters.Find(x => x.name == p[1]);
                if (h != null)
                {
                    h.pack = p[2] == "yes" ? 1 : -1;
                }
            }
            else if (p[0] == "holster_belt" && p.Length > 2)
            {
                Holster h = holsters.Find(x => x.name == p[1]);
                if (h != null)
                {
                    h.belt = p[2] == "yes" ? 1 : -1;
                }
            }
            else if (p[0] == "holster_packs")
            {
                packDefs.AddRange(new System.ArraySegment<string>(p, 1, p.Length - 1));
                packMask = -1;
            }
        }

        // Where a slot holds its weapon in the pose End left (model units): the place the weapon's middle is measured from
        // (grip), the bone's move (turn: the rest aim and top are turned by it), and the rest aim and roll. melee: the weapon
        // is a melee one, whose blade is not along the aim as a gun's barrel is (walk.HOLSTERS' melee_ place), and spin the
        // degrees toward its top it is then turned from that aim, for its own equippedAngleOffset (offset).
        public void HolsterAt(Posed p, int slot, GarmentMask mask, bool melee, float offset, out Vector3 grip, out Matrix4x4 turn, out Vector3 aim, out float roll,
            out float behind, out float spin, float extra = 0f)
        {
            Holster h = holsters[slot];
            melee = melee && h.melee;
            Vector3 at = melee ? h.meleeAt : h.at;
            aim = melee ? h.meleeAim : h.aim;
            roll = melee ? h.meleeRoll : h.roll;
            behind = h.behind;
            spin = melee ? offset - MeleeSlotOffset : 0f;
            turn = p.whole[h.bone];
            // Moved with its bone's head for this shape, then out past whatever stands out there (measured at the slot's
            // own place, which a melee place is near).
            Vector3 rest = h.pack > 0 ? PackSide(p, h, at, slot, extra) + p.shape.boneMove[h.bone]
                : at + Vector3.up * PackLift(p, h, at) + p.shape.boneMove[h.bone] + h.outward * (Clearance(p.shape, mask, slot) + extra);
            grip = turn.MultiplyPoint3x4(rest);
        }

        // Whether a weapon in this slot lies on one in under: big slots on one bone facing the same way, which the weapons
        // cross or lie along. The small and grenade slots are side by side.
        public bool HolsterStacks(int slot, int under)
        {
            Holster h = holsters[slot], b = holsters[under];
            return h.size == "big" && b.size == "big" && h.bone == b.bone && Vector3.Dot(h.outward, b.outward) > SameWayOut;
        }

        // How much further out than Clearance has it a slot goes (model units, HolsterAt's extra) so that its weapon's
        // middle stands at least apart out from the middle of the weapon in under, which is itself underExtra out.
        public float HolsterOver(Posed p, int slot, int under, GarmentMask mask, float underExtra, float apart)
        {
            Holster h = holsters[slot], b = holsters[under];
            float own = Vector3.Dot(h.at, h.outward) + Clearance(p.shape, mask, slot);
            float below = Vector3.Dot(b.at, h.outward) + Clearance(p.shape, mask, under) + underExtra;
            return Mathf.Max(0f, below + apart - own);
        }

        // How far a slot is pushed out (model units): how much further out the outermost surface near it stands, the
        // shape's body or any garment in mask, than the bare base body's does where the slot was set, so the weapon keeps
        // the stand-off it was set with. A big slot's weapon is long and a backpack bulges, so that is also looked at along
        // its length, where the bare body may lie well under the weapon and only what comes up past the stand-off counts.
        // A slot with spots looks at each of them.
        // Worked out at rest, once per shape, slot and mix of garments.
        private float Clearance(Shape s, GarmentMask mask, int slot)
        {
            return Clearance(s, mask, holsters[slot], slot);
        }

        // The same for a slot that is not in the list (the worn gear's, key past the weapons' slot numbers).
        private float Clearance(Shape s, GarmentMask mask, Holster h, int slot)
        {
            mask &= ~UnclearMask;   // a backpack is placed by this, not in the way of it (see HolsterClear); a shield goes with the hand; webbing is lain over
            (GarmentMask, int) key = (mask, slot);
            float push;
            if (s.holsterPush.TryGetValue(key, out push))
            {
                return push;
            }
            Vector3 moved = s.boneMove[h.bone];
            float before = Outer(pieces[0].rest, pieces[0].tris, h.at, h.outward);
            float standOff = Mathf.Min(before, 0f);
            push = 0f;
            int spots = h.spots != null ? h.spots.Length : h.size == "big" ? 5 : 1;
            for (int i = 0; i < spots; i++)
            {
                // The first is the slot's own place.
                Vector3 at = h.at + (h.spots != null ? h.spots[i] : h.aim * (BigHalfLength * AlongBig[i]));
                float bare = i == 0 ? before : Mathf.Max(Outer(pieces[0].rest, pieces[0].tris, at, h.outward), standOff);
                float outer = Outer(s.pieces[0].rest, pieces[0].tris, at + moved, h.outward);
                for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
                {
                    if ((mask & (GarmentBit(g))) != 0)
                    {
                        outer = Mathf.Max(outer, Outer(GarmentShape(s, g, mask).rest, pieces[g].tris, at + moved, h.outward));
                    }
                }
                // Measured from where it is moved to: a body that has only moved there (its bone with it) is not in the way.
                if (!float.IsNegativeInfinity(outer) && !float.IsNegativeInfinity(bare))
                {
                    push = Mathf.Max(push, outer - bare);
                }
            }
            s.holsterPush[key] = push;
            return push;
        }

        // The front of the chest, where a drafted stance holds its weapon across the body: the bare base body's surface
        // straight in front of the chest bone (0.73 up, the bottom of the ribs), looked at over the chest and the top of the belly (StanceSpots, model units), so ArmsOut can push the
        // holding hands out by however far the pawn's body and worn garments stand out past it there. null: no chest bone.
        private Holster chestFront;
        private bool chestFrontLooked;
        private const int ChestKey = 300;   // holsterPush's key for it, past the gear's
        private const float ArmsOutMost = 0.12f;   // model units: the hands are pushed out no further than this
        private static readonly Vector3[] StanceSpots =
        {
            Vector3.zero, new Vector3(0.06f, 0f, 0f), new Vector3(-0.06f, 0f, 0f),
            new Vector3(0f, 0.05f, 0f), new Vector3(0.06f, 0.05f, 0f), new Vector3(-0.06f, 0.05f, 0f),
            new Vector3(0f, -0.06f, 0f),
        };

        private Holster ChestFront()
        {
            if (chestFrontLooked)
            {
                return chestFront;
            }
            chestFrontLooked = true;
            int chest;
            if (!boneIndex.TryGetValue("spine.003", out chest))
            {
                return null;
            }
            Vector3 at = boneHead[chest];
            float front = Outer(pieces[0].rest, pieces[0].tris, at, Vector3.forward, 1f);
            if (float.IsNegativeInfinity(front))
            {
                return null;
            }
            chestFront = new Holster
            {
                name = "chest",
                size = "small",
                bone = chest,
                at = at + Vector3.forward * front,
                aim = Vector3.right,
                outward = Vector3.forward,
                spots = StanceSpots,
            };
            return chestFront;
        }

        // A drafted stance (clip, laid on by w) was made on the bare base body, so a weapon held across the chest goes
        // into armour, a vest's pouches or a fat belly. After Chain: each hand the stance holds (its over line takes it)
        // is moved forward along the chest by however far they stand out past the bare body there, by w, the hand keeping
        // its turn, so the weapon riding it keeps its aim. True if it moved a hand: call Reached once the arms are done.
        public bool ArmsOut(Posed p, Matrix4x4 whole, string clip, float w)
        {
            Clip c;
            Holster h;
            int chest;
            if (w <= 0f || clip == null || !clips.TryGetValue(clip, out c) || (h = ChestFront()) == null || !boneIndex.TryGetValue("spine.003", out chest))
            {
                return false;
            }
            float push = Mathf.Min(Clearance(p.shape, p.wornMask, h, ChestKey), ArmsOutMost) * Mathf.Clamp01(w);
            if (push < 1e-3f)
            {
                return false;
            }
            Vector3 ahead = p.turn[chest] * Vector3.forward;   // the chest's own forward, as Chain left it (model space)
            bool moved = false;
            for (int s = 0; s < 2; s++)
            {
                string side = s == 0 ? "R" : "L";
                int hand;
                if (!boneIndex.TryGetValue("hand." + side, out hand) || (c.over != null && c.over[hand] <= 0f))
                {
                    continue;   // a free hand keeps the gait's swing
                }
                Reach(p, whole, side, whole.MultiplyPoint3x4(p.pos[hand] + ahead * push), 1f);
                moved = true;
            }
            return moved;
        }

        // A worn backpack's rig: the way it is carried, worked out once from its mesh at rest.
        private sealed class PackRig
        {
            public Holster place;      // the middle of the face it lies on the back with, and the way out from there
            public float depth;        // how far out its far side is from that face, model units at its own size
            public PieceShape shape;   // its one shape, on any body
            public readonly Dictionary<Holster, float> sides = new Dictionary<Holster, float>();   // how far it reaches each slot's way out
        }

        private readonly Dictionary<int, PackRig> packRigs = new Dictionary<int, PackRig>();

        private PackRig PackRigOf(int g)
        {
            PackRig rig;
            if (packRigs.TryGetValue(g, out rig))
            {
                return rig;
            }
            GearSlot slot = GearOf("pack");
            if (slot == null)
            {
                return null;
            }
            Vector3 way = slot.place.outward;
            Vector3[] rest = pieces[g].rest;
            float inner = float.MaxValue, outer = float.MinValue;
            Vector3 low = Vector3.one * float.MaxValue, high = Vector3.one * float.MinValue;
            for (int i = 0; i < rest.Length; i++)
            {
                float along = Vector3.Dot(rest[i], way);
                inner = Mathf.Min(inner, along);
                outer = Mathf.Max(outer, along);
                low = Vector3.Min(low, rest[i]);
                high = Vector3.Max(high, rest[i]);
            }
            Vector3 middle = (low + high) * 0.5f;
            Vector3 at = middle + way * (inner - Vector3.Dot(middle, way));
            // Clearance looks over the whole face, as whatever stands out anywhere under it lifts the pack.
            Vector3 half = (high - low) * PackLook * 0.5f;
            List<Vector3> spots = new List<Vector3>();
            foreach (float y in new[] { 0f, 1f, -1f })
            {
                foreach (float x in new[] { 0f, 1f, -1f })
                {
                    spots.Add(new Vector3(x * half.x, y * half.y, 0f));
                }
            }
            rig = new PackRig
            {
                place = new Holster { name = pieces[g].name, size = "gear", bone = slot.place.bone, at = at, outward = way, spots = spots.ToArray() },
                depth = outer - inner,
                shape = ShapePiece(pieces[g], new List<KeyValuePair<string, float>>())
            };
            packRigs[g] = rig;
            return rig;
        }

        // Whether the backpack worn is apparel with no garment of its own, drawing a grown-up one's (AdultOf): a child's.
        public bool BorrowsPack(GarmentMask mask, Apparel[] worn)
        {
            GarmentMask packs = mask & PackMask;
            for (int g = 1; packs != 0 && g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((packs & (GarmentBit(g))) != 0 && worn[g] != null && !garmentOf.ContainsKey(worn[g].def.defName))
                {
                    return true;
                }
            }
            return false;
        }

        // The backpack in the garments worn, 0 for none.
        private int WornPack(Posed p)
        {
            GarmentMask packs = p.wornMask & PackMask;
            for (int g = 1; packs != 0 && g < pieces.Length && g <= MaxHiding; g++)
            {
                if ((packs & (GarmentBit(g))) != 0)
                {
                    return g;
                }
            }
            return 0;
        }

        // How far a backpack's face goes out from its place at rest: past the clothes and the body under it.
        private float PackPush(Posed p, int g, PackRig rig)
        {
            return Clearance(p.shape, p.wornMask, rig.place, PackKey + g) + GearGap;
        }

        // Poses a backpack as one thing on its bone, at its place for this body and at its own size whatever the pawn's.
        private void Packed(Posed p, int g, PackRig rig, PieceMeshes m)
        {
            if (m.posedAt == p.posedAt)
            {
                return;
            }
            Holster h = rig.place;
            Matrix4x4 place = p.whole[h.bone] * Matrix4x4.Translate(p.shape.boneMove[h.bone] + h.at + h.outward * PackPush(p, g, rig))
                * Matrix4x4.Scale(Vector3.one * p.backpackScale) * Matrix4x4.Translate(-h.at);
            PlaceWhole(p, rig.shape, place, m);
        }

        // Poses a piece as one thing, its rest pose moved by place (only if the pose has changed).
        private static void PlaceWhole(Posed p, PieceShape shape, Matrix4x4 place, PieceMeshes m)
        {
            if (m.posedAt == p.posedAt)
            {
                return;
            }
            m.posedAt = p.posedAt;
            Vector3[] rest = shape.rest, hull = shape.hull, normal = shape.normal;
            for (int i = 0; i < rest.Length; i++)
            {
                m.verts[i] = place.MultiplyPoint3x4(rest[i]);
                m.hulled[i] = m.verts[i] + place.MultiplyVector(hull[i]);
                m.normals[i] = place.MultiplyVector(normal[i]);
            }
            m.solid.vertices = m.verts;
            m.solid.normals = m.normals;
            m.outline.vertices = m.hulled;
        }

        // The outermost thing worn on the back along way (model units, before the bone's move for the shape), any pack
        // there; negative infinity for none.
        private float PackOuter(Posed p, Vector3 way)
        {
            float outer = float.NegativeInfinity;
            int g = WornPack(p);
            PackRig rig = g > 0 ? PackRigOf(g) : null;
            if (rig != null)
            {
                outer = Vector3.Dot(rig.place.at, way) + (PackPush(p, g, rig) + rig.depth * p.backpackScale) * Vector3.Dot(rig.place.outward, way);
            }
            return p.gearPack ? Mathf.Max(outer, GearPackOuter(p, way)) : outer;
        }

        // What a weapon needs of HolsterAt's extra beside what it lies on: apart is its own half depth and the gap left, in
        // model units. In a slot for a pack's side that is the whole of it (PackSide). On the back, how much further out than
        // its Clearance it goes to lie on a pack.
        public float HolsterClear(Posed p, int slot, GarmentMask mask, float apart)
        {
            Holster h = holsters[slot];
            if (h.pack > 0)
            {
                return apart;
            }
            if (!LiesOnPack(h))
            {
                return 0f;
            }
            float outer = PackOuter(p, h.outward);
            return float.IsNegativeInfinity(outer) ? 0f : Mathf.Max(0f, outer + apart - Vector3.Dot(h.at, h.outward) - Clearance(p.shape, mask, slot));
        }

        // Whether a weapon in this slot lies on the back under a shield carried there (as on a pack).
        public bool UnderShield(int slot)
        {
            return LiesOnPack(holsters[slot]);
        }

        // How far out a weapon's middle stands in this slot along its way out (model units, before the bone's move for
        // the shape), extra further than its Clearance, as HolsterAt places it.
        public float HolsterOuter(Posed p, int slot, GarmentMask mask, float extra)
        {
            Holster h = holsters[slot];
            return Vector3.Dot(h.at, h.outward) + Clearance(p.shape, mask, slot) + extra;
        }

        // Whether a weapon in this slot lies on a pack worn: a big slot on the pack's bone, facing the way it does.
        private bool LiesOnPack(Holster h)
        {
            GearSlot gear = GearOf("pack");
            return h.pack <= 0 && h.size == "big" && gear != null && h.bone == gear.place.bone && Vector3.Dot(h.outward, gear.place.outward) > SameWayOut;
        }

        // How far up (model units, before the bone's move for the shape) a slot at this height goes to lie about the middle
        // of the pack worn, its slots being set for a bare back, lower down. None without a pack.
        private float PackLift(Posed p, Holster h, Vector3 at)
        {
            if (!LiesOnPack(h))
            {
                return 0f;
            }
            int g = WornPack(p);
            PackRig rig = g > 0 ? PackRigOf(g) : null;
            float middle;
            if (rig != null)
            {
                middle = rig.place.at.y;   // it is scaled about this
            }
            else if (p.gearPack)
            {
                middle = GearPackMiddle(p);
            }
            else
            {
                return 0f;
            }
            return Mathf.Max(0f, middle + OverPackMiddle - at.y);
        }

        // A slot for a pack's side, at rest before the bone's move for the shape: as far along the pack and as far from its
        // back as it was set, on a pack moved and sized as this one is, and out from its side, or from what stands out past
        // that (a coat, a fat body), by apart. Without a pack drawn, where it was set, on the same terms.
        private Vector3 PackSide(Posed p, Holster h, Vector3 at, int slot, float apart)
        {
            int g = WornPack(p);
            PackRig rig = g > 0 ? PackRigOf(g) : null;
            Vector3 pivot, way;
            float push;
            float reach = p.gearPack ? GearPackSide(p) : 0f;
            if (rig != null)
            {
                pivot = rig.place.at;
                way = rig.place.outward;
                push = PackPush(p, g, rig);
                float side;
                if (!rig.sides.TryGetValue(h, out side))
                {
                    side = float.MinValue;
                    foreach (Vector3 v in pieces[g].rest)
                    {
                        side = Mathf.Max(side, Vector3.Dot(v - pivot, h.outward));
                    }
                    rig.sides[h] = side;
                }
                reach = Mathf.Max(reach, side * p.backpackScale);
            }
            else if (!GearPackPlace(p, out pivot, out way, out push))
            {
                return at + h.outward * apart;
            }
            Vector3 from = at - pivot;
            float along = Vector3.Dot(from, h.outward);
            float past = SideSurface(p.shape, p.wornMask, h, slot);
            if (!float.IsNegativeInfinity(past))
            {
                reach = Mathf.Max(reach, along + past);
            }
            return pivot + way * push + (from - h.outward * along) * (rig != null ? p.backpackScale : p.packScale) + h.outward * (reach + apart);
        }

        // The furthest out along a slot's way that the shape's body or the clothes worn stand beside it, measured from the
        // slot; only near it, so a rest pose's arm held out to the side isn't taken for a coat. The body only over the pack's
        // height, as the far ends of a long weapon may pass the hips; the clothes all along it.
        private float SideSurface(Shape s, GarmentMask mask, Holster h, int slot)
        {
            mask &= ~UnclearMask;
            (GarmentMask, int) key = (mask, SideKey + slot);
            float best;
            if (s.holsterPush.TryGetValue(key, out best))
            {
                return best;
            }
            best = float.NegativeInfinity;
            Vector3 moved = s.boneMove[h.bone];
            for (int i = 0; i < AlongBig.Length; i++)
            {
                Vector3 at = h.at + moved + h.aim * (BigHalfLength * AlongBig[i]);
                if (Mathf.Abs(AlongBig[i]) < 1f)
                {
                    best = Mathf.Max(best, Outer(s.pieces[0].rest, pieces[0].tris, at, h.outward, SideFar));
                }
                for (int g = 1; g < pieces.Length && g <= MaxHiding; g++)
                {
                    if ((mask & (GarmentBit(g))) != 0)
                    {
                        best = Mathf.Max(best, Outer(GarmentShape(s, g, mask).rest, pieces[g].tris, at, h.outward, SideFar));
                    }
                }
            }
            s.holsterPush[key] = best;
            return best;
        }

        // The furthest out along way that a surface stands on the line through point, measured from point: any vertex
        // within HolsterReach of the line, or a triangle it passes through, as a garment may be a few big faces with no
        // vertex near. Negative infinity for none.
        private static float Outer(Vector3[] verts, int[] tris, Vector3 point, Vector3 way, float far = HolsterDepth)
        {
            float best = float.NegativeInfinity;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 d = verts[i] - point;
                float along = Vector3.Dot(d, way);
                if (along < -HolsterDepth || along > far || along <= best)
                {
                    continue;
                }
                if ((d - way * along).sqrMagnitude <= HolsterReach * HolsterReach)
                {
                    best = along;
                }
            }
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = verts[tris[i]], e1 = verts[tris[i + 1]] - a, e2 = verts[tris[i + 2]] - a;
                Vector3 side = Vector3.Cross(way, e2);
                float det = Vector3.Dot(e1, side);
                if (Mathf.Abs(det) < 1e-9f)
                {
                    continue;
                }
                Vector3 d = point - a;
                float u = Vector3.Dot(d, side) / det;
                Vector3 up = Vector3.Cross(d, e1);
                float v = Vector3.Dot(way, up) / det;
                float along = Vector3.Dot(e2, up) / det;
                if (u >= 0f && v >= 0f && u + v <= 1f && along <= far && along >= -HolsterDepth && along > best)
                {
                    best = along;
                }
            }
            return best;
        }
    }

    public static partial class WalkFigures
    {
        private const float HeavyMelee = 1.8f;   // kg: a melee weapon the export doesn't name goes in a big slot from this mass

        public static bool Holsters   // false: weapons not held are not drawn (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.holsters; }
        }

        private const float DrawIn = 0.15f;    // share of a draw clip over which it is eased in, from hanging at the start
        private const float DrawOut = 0.75f;   // and from where it is eased out, into the stance under it
        private const float DrawAhead = 0.1f;  // game seconds before the shot a hurried draw is done, at most half of the time left
        private const float HolsterRush = 0.3f;   // game seconds a whole holster takes at most with the next thing waiting on the hands

        private static readonly Dictionary<(ThingDef, bool, bool), string> drawClips = new Dictionary<(ThingDef, bool, bool), string>();
        private const float SlabHalfDepth = 0.025f;   // cells: half of IsoRim's slab of a sprite
        private const float StackGap = 0.005f;        // model units: left between two weapons lying on each other

        private static Thing[] slotWeapon;   // what each slot holds this draw, null for free
        private static float[] slotExtra;    // and how much further out than its Clearance it went for those under it
        private static bool slotPack;        // and whether the pawn drawn has its back's weapons at its pack's sides (OnPackSides)
        private static bool slotBelt;        // and its grenades on its arm for a belt (OnBeltSlots)

        // The slot size a weapon goes in: its type's (walk.TYPE_HOLSTER), else a grenade's own, a handgun or one-handed
        // melee weapon's small and any other's big.
        private static string HolsterSizeOf(ThingDef def)
        {
            string type = TypeOf(def);
            return skin.TypeHolster(type) ?? (type == "grenade" ? "grenade" : type == "handgun" || type == "melee_1h" ? "small" : "big");
        }

        // The weapons the pawn has but is not holding, each in the first free slot of its size: the equipped one first,
        // then the inventory's in order, less the one in its hands if inHands (HeldWeapon: the equipped one, or one out of
        // the inventory to reload). mask is the garments worn (DrawDressed's), which push the
        // slots out. With draw false they are only given their slots (slotWeapon, slotExtra), not drawn.
        private static void DrawHolstered(Pawn pawn, bool inHands, GarmentMask mask, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, bool draw = true)
        {
            if (!Holsters || skin.Holsters == 0 || !HeldWeapons3D)
            {
                return;
            }
            if (slotWeapon == null || slotWeapon.Length != skin.Holsters)
            {
                slotWeapon = new Thing[skin.Holsters];
                slotExtra = new float[skin.Holsters];
            }
            for (int s = 0; s < slotWeapon.Length; s++)
            {
                slotWeapon[s] = null;
                slotExtra[s] = 0f;
            }
            slotPack = OnPackSides(pawn);
            slotBelt = OnBeltSlots(pawn);
            ThingWithComps equipped = pawn.equipment != null ? pawn.equipment.Primary : null;
            ThingWithComps held = inHands ? HeldWeapon(pawn) : null;
            if (equipped != null && equipped != held)
            {
                Holster(equipped, mask, fig, at, yaw, size, draw);
            }
            else if (equipped != null)
            {
                TakeSlot(equipped);   // kept for it, so what is in the other slots doesn't jump when it goes back
            }
            ThingOwner inventory = pawn.inventory != null ? pawn.inventory.innerContainer : null;
            for (int i = 0; inventory != null && i < inventory.Count; i++)
            {
                Thing t = inventory[i];
                // Beer can be wielded as a club (equipmentType Primary), but a pawn carrying it in its pack hasn't
                // armed itself with it, so food and drugs are left out.
                if (t.def.equipmentType == EquipmentType.Primary && !t.def.IsIngestible && t != held)
                {
                    Holster(t, mask, fig, at, yaw, size, draw);
                }
            }
        }

        // How far out the weapons holstered on the back stand there (model units along their way out, before the bone's
        // move for the shape), so a shield carried there goes over them (SkinnedFigure.Shielding); negative infinity for
        // none. Every weapon the pawn has counts, the equipped one too, as it is on the back whenever the shield is: the
        // game shows them in the hands together. mask as DrawHolstered's, after Wearing.
        private static float ShieldOverWeapons(Pawn pawn, GarmentMask mask, SkinnedFigure.Posed fig, float size)
        {
            float outer = float.NegativeInfinity;
            if (!Holsters || skin.Holsters == 0 || !HeldWeapons3D)
            {
                return outer;
            }
            DrawHolstered(pawn, false, mask, fig, Vector3.zero, 0f, size, false);
            for (int s = 0; s < slotWeapon.Length; s++)
            {
                if (slotWeapon[s] != null && skin.UnderShield(s))
                {
                    outer = Mathf.Max(outer, skin.HolsterOuter(fig, s, mask, slotExtra[s]) + HalfDepth(slotWeapon[s]) / size);
                }
            }
            return outer;
        }

        private static void Holster(Thing weapon, GarmentMask mask, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, bool draw = true)
        {
            int slot = TakeSlot(weapon);
            if (slot < 0)
            {
                return;
            }
            slotExtra[slot] = OverOthers(weapon, slot, mask, fig, size);   // with what it lies on or beside
            if (!draw)
            {
                return;
            }
            Vector3 grip, aim, a, top;
            Matrix4x4 turn;
            float roll, behind, spin;
            skin.HolsterAt(fig, slot, mask, IsMelee(weapon.def), weapon.def.equippedAngleOffset, out grip, out turn, out aim, out roll, out behind, out spin, slotExtra[slot]);
            SlotLine(turn, aim, roll, spin, out a, out top);
            Vector3 tip;
            DrawSlab(weapon, ScaleOf(weapon), grip, a, top, behind, at, yaw, size, out tip);   // as big as it is held
        }

        // The way a weapon in a slot runs and its top, in the pose (HolsterAt's), spin degrees from the aim toward the top.
        private static void SlotLine(Matrix4x4 turn, Vector3 aim, float roll, float spin, out Vector3 a, out Vector3 top)
        {
            a = turn.MultiplyVector(aim).normalized;
            top = turn.MultiplyVector(TopOf(aim, roll));
            top = (top - a * Vector3.Dot(top, a)).normalized;
            float c = Mathf.Cos(spin * Mathf.Deg2Rad), s = Mathf.Sin(spin * Mathf.Deg2Rad);
            Vector3 along = a * c + top * s;
            top = top * c - a * s;
            a = along;
        }

        // Whether a weapon is a melee one, by its type.
        private static bool IsMelee(ThingDef def)
        {
            return TypeOf(def).StartsWith("melee");
        }

        // HolsterAt's extra for a weapon on its own against a pack (model units): its depth in cells whatever the pawn's
        // size, as DrawSlab draws it, so a share of a small pawn's model units more.
        private static float PackClear(Thing weapon, int slot, GarmentMask mask, SkinnedFigure.Posed fig, float size)
        {
            return skin.HolsterClear(fig, slot, mask, HalfDepth(weapon) / size + StackGap);
        }

        // How much further out than its Clearance this weapon goes to lie on a pack and on those already in the slots under
        // it (model units). A slot kept for a weapon in the hands counts, so nothing jumps when it goes back.
        private static float OverOthers(Thing weapon, int slot, GarmentMask mask, SkinnedFigure.Posed fig, float size)
        {
            float most = PackClear(weapon, slot, mask, fig, size);
            for (int s = 0; s < slot; s++)
            {
                if (slotWeapon[s] != null && skin.HolsterStacks(slot, s))
                {
                    // Its depth in cells whatever the pawn's size (DrawSlab), so a share of a small pawn's model units more.
                    float apart = (HalfDepth(weapon) + HalfDepth(slotWeapon[s])) / size + StackGap;
                    most = Mathf.Max(most, skin.HolsterOver(fig, slot, s, mask, slotExtra[s], apart));
                }
            }
            return most;
        }

        // How far a weapon as DrawSlab draws it stands off the slab's middle, either way out of its face (cells).
        private static float HalfDepth(Thing weapon)
        {
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            if (mat != null)
            {
                float depth = ModelHalfDepth(weapon, g.drawSize, ScaleOf(weapon));
                if (depth >= 0f)
                {
                    return depth;
                }
            }
            return SlabHalfDepth;
        }

        // The same for the weapon's IsoRim model, below 0 if it has none.
        private static float ModelHalfDepth(Thing weapon, Vector2 drawSize, float scale)
        {
            ThingModelInfo info;
            WeaponFit.Hold h;
            if (!HeldModel(weapon, out info, out h))
            {
                return -1f;
            }
            if (Thrown(weapon.def))
            {
                h = WeaponFit.AtGroundSize(h, info.Model, GroundScaleOf(weapon), drawSize);
            }
            return WeaponFit.HalfDepth(h, info.Model) * scale;
        }

        // The first free slot of the weapon's size, now taken; -1 for none.
        private static int TakeSlot(Thing weapon)
        {
            string wanted = HolsterSizeOf(weapon.def);
            for (int s = 0; s < slotWeapon.Length; s++)
            {
                if (slotWeapon[s] == null && skin.HolsterSize(s) == wanted && skin.HolsterFits(s, slotPack, slotBelt))
                {
                    slotWeapon[s] = weapon;
                    return s;
                }
            }
            return -1;
        }

        // The slot the equipped weapon goes in: the first of its size that can be used, as DrawHolstered gives it the first
        // pick. -1 for none.
        private static int EquippedSlot(ThingDef def, bool pack, bool belt)
        {
            string wanted = HolsterSizeOf(def);
            for (int s = 0; s < skin.Holsters; s++)
            {
                if (skin.HolsterSize(s) == wanted && skin.HolsterFits(s, pack, belt))
                {
                    return s;
                }
            }
            return -1;
        }

        // The clip that draws this weapon from its slot: draw_<slot>_<type>, each made for that type's placeholder (walk.
        // HOLSTER_WEAPON); null on an older export or with no slot of its size. Cached per ThingDef, with a pack or not and a belt or not.
        private static string DrawClipOf(ThingDef def, bool pack, bool belt)
        {
            string clip;
            if (!drawClips.TryGetValue((def, pack, belt), out clip))
            {
                int slot = EquippedSlot(def, pack, belt);
                clip = slot >= 0 ? "draw_" + skin.HolsterName(slot) + "_" + TypeOf(def) : null;
                clip = clip != null && skin.Has(clip) ? clip : null;
                drawClips[(def, pack, belt)] = clip;
            }
            return clip;
        }

        // Whether the pawn has gear drawn on this IsoRim mount (pack, belt or hip).
        private static bool WearsGear(Pawn pawn, string mount)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            for (int i = 0; WornGears && worn != null && i < worn.Count; i++)
            {
                if (GearMountOf(worn[i]) == mount)
                {
                    return true;
                }
            }
            return false;
        }

        // Whether the pawn's weapons on the back go to the sides of what it wears there: only with both a backpack
        // (walk.HOLSTER_PACKS) and an equipment pack (IsoRim's pack mount, if drawn), as there is then no room on its back.
        private static bool OnPackSides(Pawn pawn)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            if (worn == null)
            {
                return false;
            }
            bool backpack = false;
            for (int i = 0; i < worn.Count; i++)
            {
                backpack |= skin.IsPack(worn[i].def);
            }
            return backpack && WearsGear(pawn, "pack");
        }

        // Whether the slots for a belt are the pawn's: with a belt drawn on its waist (IsoRim's belt mount) and the left arm
        // they ride there to carry them, since a holster is placed from the uncut bones and would hang in the air.
        private static bool OnBeltSlots(Pawn pawn)
        {
            if (!WearsGear(pawn, "belt"))
            {
                return false;
            }
            List<Hediff_MissingPart> missing = pawn.health != null ? pawn.health.hediffSet.GetMissingPartsCommonAncestors() : null;
            for (int i = 0; missing != null && i < missing.Count; i++)
            {
                BodyPartRecord part = missing[i].Part;
                if (part != null && (part.def.defName == "Shoulder" || part.def.defName == "Arm") && SideOf(part) == "L")
                {
                    return false;
                }
            }
            return true;
        }

        // Starts, turns round or drops the pawn's draw as the weapon's place changes (hand: the game shows it in the hands),
        // and moves it on by game time. While one plays it says how far into the stance the pawn is (none until the hand is
        // on the handle, all by the end, so the other arm comes up to meet the weapon as it arrives), in place of the eased
        // stance. A blow takes over at once, and it does not start with a load or anywhere but on the pawn's feet. A draw
        // that wouldn't be done before the weapon's shot is sped up to be, and only then; a shot already fired ends it. A
        // holster with something waiting on the hands (rush: it is already at its work) is over within HolsterRush.
        private static void StepDraw(Pawn pawn, Ease e, bool hand, bool can, float target, bool recent, float game, bool rush)
        {
            ThingWithComps weapon = pawn.equipment != null ? pawn.equipment.Primary : null;
            if (hand != e.handHeld)
            {
                e.handHeld = hand;
                string clip = can && recent && weapon != null && HeldWeapon(pawn) == weapon && skin != null ? DrawClipOf(weapon.def, OnPackSides(pawn), OnBeltSlots(pawn)) : null;
                if (clip == null)
                {
                    e.drawAt = -1f;
                }
                else
                {
                    e.drawAt = e.drawAt >= 0f ? e.drawAt : hand ? 0f : 1f;   // from as far as it has got, if already under way
                    e.drawClip = clip;
                    e.drawWeapon = weapon.thingIDNumber;
                }
            }
            if (e.drawAt < 0f)
            {
                return;
            }
            float seconds = skin.SecondsOf(e.drawClip);
            if (!can || seconds <= 0f || weapon == null || weapon.thingIDNumber != e.drawWeapon)
            {
                e.drawAt = -1f;
                return;
            }
            float step = game / seconds;
            float shot = hand ? ShotIn(pawn, weapon) : -1f;
            if (shot == 0f)
            {
                step = 1f;
            }
            else if (shot > 0f)
            {
                step = Mathf.Max(step, (1f - e.drawAt) * game / ReadyIn(shot));
            }
            if (!hand && rush)
            {
                step = Mathf.Max(step, game / HolsterRush);
            }
            e.drawAt += hand ? step : -step;
            if (e.drawAt >= 1f || e.drawAt < 0f)
            {
                e.drawAt = -1f;
                e.drafted = target;
                return;
            }
            e.drafted = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(skin.DrawGrab, 1f, e.drawAt));
        }

        // Whether the pawn is walking to this work and will be there within the time it takes to put its weapon away, so
        // that it can do so at the plain speed and arrive with its hands free. Not on the way to fetch something for it.
        private static bool HolsterBefore(Pawn pawn, LocalTargetInfo work)
        {
            Pawn_PathFollower pather = pawn.pather;
            if (pather == null || !pather.Moving || pather.curPath == null
                || Api.DrawnDelta(pather.Destination.Cell.ToVector3Shifted(), work.Cell.ToVector3Shifted()).sqrMagnitude > WorkReach)
            {
                return false;
            }
            ThingWithComps weapon = pawn.equipment != null ? pawn.equipment.Primary : null;
            string clip = weapon != null ? DrawClipOf(weapon.def, OnPackSides(pawn), OnBeltSlots(pawn)) : null;
            float seconds = clip != null ? skin.SecondsOf(clip) : 1f / DraftEase;
            return Mathf.RoundToInt(pather.curPath.NodesLeftCount * pawn.TicksPerMoveCardinal).TicksToSeconds() <= seconds;
        }

        // Game seconds until this weapon's shot (null: any verb the pawn is using): the warm-up left, 0 once it is firing
        // (the stance's cool-down, also between a burst's shots), less than 0 if the pawn isn't using it.
        private static float ShotIn(Pawn pawn, ThingWithComps weapon)
        {
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            if (busy == null || busy.verb == null || (weapon != null && busy.verb.EquipmentSource != weapon))
            {
                return -1f;
            }
            Stance_Warmup warmup = busy as Stance_Warmup;
            return warmup != null ? warmup.ticksLeft.TicksToSeconds() : 0f;
        }

        // Game seconds to be ready in, with this many left until the shot.
        private static float ReadyIn(float shot)
        {
            return shot - Mathf.Min(DrawAhead, shot * 0.5f);
        }

        // How much of the draw clip shows at this point of it.
        private static float DrawWeight(float at)
        {
            return Mathf.SmoothStep(0f, 1f, at / DrawIn) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(DrawOut, 1f, at)));
        }

        // While a draw plays, how far the weapon has come from the slot to the hand: none before it starts to swing, all
        // from when the hand has closed on it.
        private static float DrawnShare(Ease e)
        {
            if (e.drawAt < 0f)
            {
                return 1f;
            }
            float slide = skin.DrawSlide(e.drawClip);
            return slide > 0f ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(skin.DrawGrab - slide, skin.DrawGrab, e.drawAt))
                : e.drawAt >= skin.DrawGrab ? 1f : 0f;
        }
    }
}
