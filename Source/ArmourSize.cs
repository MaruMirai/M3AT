using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Armour that makes its wearer bigger, and tall pawns crouching under a ceiling.
    //
    // GrimWorld 40,000's Space Marines are plain Human pawns made Marines by their armour (it leaves its own worn scale
    // commented out: the sprites alone are bulky), so the armour is what is read: GrimWorld's Space Marine and Custodes
    // power armour (MarineArmour) draws its wearer armourBulk bigger and broader (ArmourHulk of the Hulk key), its head
    // kept a normal pawn's size (ArmouredHeadShrink), so it reads as an armoured giant rather than a big child; the powerArmourBulk option does the same for any Spacer power armour (the game's marine,
    // cataphract and recon armour). Body armour a mod draws scaled up when worn (its drawData scale: GrimWorld's Custodes
    // 1.1, the Emperor's 1.2, Valdor's 1.4) draws its wearer that much bigger with armourDrawScale on; the larger of the
    // two is taken. A helmet's or pauldron's own scale is only its sprite's fit, so only body armour counts.
    public static partial class WalkFigures
    {
        private const float ArmourHulk = 0.8f;          // the Hulk key's least amount on a bulked wearer, of the body shape strength

        // What a worn apparel does to its wearer's size: its drawData scale if body armour, and whether it bulks it up.
        private enum Bulk { None, Marine, PowerArmour }
        private static readonly Dictionary<ThingDef, Bulk> bulkOf = new Dictionary<ThingDef, Bulk>();
        private static readonly Dictionary<ThingDef, bool> bodyArmourOf = new Dictionary<ThingDef, bool>();

        // The size an armoured pawn is drawn at, of its own (1 unarmoured), from the armour it wears (SizeOf).
        private static float ArmourSizeOf(Pawn pawn)
        {
            Settings s = SettingsMod.Settings;
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            if (s == null || worn == null)
            {
                return 1f;
            }
            float size = 1f;
            for (int i = 0; i < worn.Count; i++)
            {
                ThingDef def = worn[i].def;
                if (s.armourDrawScale && def.apparel.drawData != null && BodyArmour(def))
                {
                    size = Mathf.Max(size, def.apparel.drawData.ScaleFor(pawn));
                }
                Bulk bulk = BulkOf(def);
                if ((bulk == Bulk.Marine && s.marineBulk) || (bulk != Bulk.None && s.powerArmourBulk))
                {
                    size = Mathf.Max(size, s.armourBulk);
                }
            }
            return size;
        }

        // Whether the pawn's armour bulks it up (a broader build and smaller head, ShapeFor), not only scales it.
        private static bool ArmourBulky(Pawn pawn)
        {
            Settings s = SettingsMod.Settings;
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            for (int i = 0; s != null && worn != null && i < worn.Count; i++)
            {
                Bulk bulk = BulkOf(worn[i].def);
                if ((bulk == Bulk.Marine && s.marineBulk) || (bulk != Bulk.None && s.powerArmourBulk))
                {
                    return s.armourBulk > 1f;
                }
            }
            return false;
        }

        // The marine and cataphract armour and their variants (a garment with the pads line) are drawn with the Pauldrons,
        // for the apparel of the armour, unless other apparel already has them drawn (GrimWorld's shoulder pads, in their
        // own colour). MarinePads then gives a Space Marine the big ones.
        private const string PadsGarment = "Pauldrons";
        private static SkinnedFigure padsSkin;
        private static int padsPiece = -1;

        private static void ArmourPads(Apparel[] worn)
        {
            if (padsSkin != skin)
            {
                padsSkin = skin;
                padsPiece = -1;
                for (int g = 1; skin != null && g < skin.Pieces; g++)
                {
                    if (skin.PieceName(g) == PadsGarment)
                    {
                        padsPiece = g;
                    }
                }
            }
            if (padsPiece < 0 || worn[padsPiece] != null)
            {
                return;
            }
            for (int g = 1; g < skin.Pieces; g++)
            {
                if (worn[g] != null && skin.HasPads(g))
                {
                    worn[padsPiece] = worn[g];
                    return;
                }
            }
        }

        // Swaps each garment worn for its Space Marine version (SkinnedFigure.MarineVersionOf: the big pauldrons) on a pawn
        // wearing GrimWorld's Space Marine or Custodes power armour (BulkOf's Marine), whether or not the marineBulk option
        // draws it bigger, as the pads are made for that armour's shoulders.
        private static void MarinePads(Pawn pawn, List<Apparel> all, Apparel[] worn)
        {
            bool marine = false;
            for (int i = 0; all != null && i < all.Count && !marine; i++)
            {
                marine = all[i].def.apparel != null && BulkOf(all[i].def) == Bulk.Marine;
            }
            for (int g = 1; marine && g < skin.Pieces; g++)
            {
                int big = worn[g] != null ? skin.MarineVersionOf(g) : -1;
                if (big > 0 && worn[big] == null)
                {
                    worn[big] = worn[g];
                    worn[g] = null;
                }
            }
        }

        // Body armour: worn on a body layer over the torso (not a helmet, pauldron or shield, whose scale fits its sprite).
        private static bool BodyArmour(ThingDef def)
        {
            bool found;
            if (!bodyArmourOf.TryGetValue(def, out found))
            {
                ApparelProperties ap = def.apparel;
                bool body = ap.layers != null && ap.layers.Exists(l => l == ApparelLayerDefOf.Middle || l == ApparelLayerDefOf.Shell);
                found = body && ap.bodyPartGroups != null && ap.bodyPartGroups.Contains(BodyPartGroupDefOf.Torso);
                bodyArmourOf[def] = found;
            }
            return found;
        }

        // Power armour: armour of Spacer tech or later on a body layer over the torso, arms and legs. GrimWorld's (its defs
        // start GW_) is a Space Marine's or Custodian's (Marine), but a scout's, a Sister of Silence's or Malcador's robes,
        // worn by ordinary people.
        private static Bulk BulkOf(ThingDef def)
        {
            Bulk found;
            if (bulkOf.TryGetValue(def, out found))
            {
                return found;
            }
            found = Bulk.None;
            ApparelProperties ap = def.apparel;
            bool armour = def.thingCategories != null && def.thingCategories.Exists(c => c.defName.IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0);
            List<BodyPartGroupDef> groups = ap.bodyPartGroups;
            if (armour && def.techLevel >= TechLevel.Spacer && BodyArmour(def) && groups.Contains(BodyPartGroupDefOf.Legs)
                && groups.Exists(g => g.defName == "Arms"))
            {
                string text = (def.defName + " " + def.label).ToLowerInvariant();
                bool ordinary = text.Contains("scout") || text.Contains("sister") || text.Contains("robe");
                found = def.defName.StartsWith("GW_") && !ordinary ? Bulk.Marine : Bulk.PowerArmour;
            }
            bulkOf[def] = found;
            return found;
        }

        // The Shrinkhead amount for a pawn grown by its armour: its head kept the size it would be unarmoured (shrink, the
        // player's head size as this pawn takes it), whatever the armour's size, so only the body grows. Rounded to
        // HeadShrinkRound so the shapes stay few; a little past all of the key where need be, at most SkinnedFigure.MostShrink,
        // so with heads already made small a large size leaves the head somewhat bigger than normal.
        private const float HeadShrinkRound = 0.01f;

        private static float ArmouredHeadShrink(Pawn pawn, float shrink)
        {
            float grown = ArmourSizeOf(pawn);
            if (grown <= 1f)
            {
                return shrink;
            }
            float wanted = SettingsMod.ShrinkFor(SettingsMod.HeadSize(shrink) / grown);
            return Mathf.Min(SkinnedFigure.MostShrink, Mathf.Round(wanted / HeadShrinkRound) * HeadShrinkRound);
        }

        // A bulked wearer's build: the Hulk key at least ArmourHulk (of the strength), its other build keys kept.
        private static List<KeyValuePair<string, float>> ArmouredBuild(List<KeyValuePair<string, float>> builds, string body, float strength)
        {
            List<KeyValuePair<string, float>> found = builds != null ? new List<KeyValuePair<string, float>>(builds) : new List<KeyValuePair<string, float>>();
            if (builds == null && body != null && Array.IndexOf(BuildKeys, body) >= 0)
            {
                found.Add(new KeyValuePair<string, float>(body, strength));   // a Thin, Fat or Hulk body's own key, as unbulked
            }
            int hulk = found.FindIndex(b => b.Key == "Hulk");
            float least = ArmourHulk * strength;
            if (hulk < 0)
            {
                found.Add(new KeyValuePair<string, float>("Hulk", least));
            }
            else if (found[hulk].Value < least)
            {
                found[hulk] = new KeyValuePair<string, float>("Hulk", least);
            }
            return found;
        }

        // Ceilings. A pawn under a roof that would be drawn taller than the ceiling crouches under it: the hips let down
        // (Crouch, up to all of CrouchDepth) and then the upper back bent forward (Stoop, up to CeilingStoopMost), as far as
        // it takes to keep its head CeilingClear under. The ceiling is the floor above's height over its own with IsoRimZ's
        // stacked floors (IsoRim's Api.Floors), else the wall height (a roof is drawn on the walls). By the ceilingCrouch
        // option: under any roof, only while the floor above is drawn over it (the upper floor viewed: a roof is not drawn
        // otherwise), or never.
        private const float CeilingClear = 0.04f;    // cells between the head's top and the ceiling
        private const float CeilingStoopMost = 55f;  // degrees the back bends under a ceiling at most
        private const float FigureTop = 1.25f;       // the top of the head standing, model units (export_frames.py's FIGURE_HEIGHT)
        private const float BackShare = 0.5f;        // of the figure's height, from the hips to the head's top, which the stoop swings down
        private const float CeilingEase = 2f;        // how fast a pawn crouches under a ceiling or stands up again, of all the way per real second

        // How far down the pawn needs to bring its head under the ceiling over it, cells; 0 for none.
        private static float CeilingDrop(Pawn pawn, float size)
        {
            Settings s = SettingsMod.Settings;
            if (s == null || s.ceilingCrouch == CeilingCrouch.Never || !pawn.Spawned)
            {
                return 0f;
            }
            float top = FigureTop * size;
            if (top <= Api.WallHeight - CeilingClear && Api.Floors == null)
            {
                return 0f;   // (spares the roof lookup for everyone under the walls' height without stacked floors)
            }
            Map map = pawn.Map;
            IntVec3 c = pawn.Position;
            if (!c.InBounds(map) || !c.Roofed(map))
            {
                return 0f;
            }
            bool drawn;
            float ceiling = CeilingOver(map, c, out drawn);
            if (s.ceilingCrouch == CeilingCrouch.FloorShown && !drawn)
            {
                return 0f;
            }
            return Mathf.Max(0f, top - (ceiling - CeilingClear));
        }

        // The ceiling's height over the cell's floor, cells, and whether a floor above it is drawn now (IsoRimZ's upper floor
        // viewed); the wall height, not drawn, without stacked floors or on the top floor.
        private static float CeilingOver(Map map, IntVec3 c, out bool drawn)
        {
            drawn = false;
            float wall = Api.WallHeight;
            try
            {
                IFloorLayout layout = Api.Floors;
                int rows = layout != null ? layout.GroundRows(map) : 0;
                if (rows <= 0)
                {
                    return wall;
                }
                int count = layout.FloorCount(map);
                int own = 0;
                float ownHeight = 0f;
                for (int k = 1; k <= count && c.z >= rows; k++)
                {
                    float height;
                    int shift;
                    layout.Floor(map, k, out height, out shift);
                    if (c.z >= shift && c.z < shift + rows)
                    {
                        own = k;
                        ownHeight = height;
                        break;
                    }
                }
                if (own >= count)
                {
                    return wall;
                }
                float above;
                int aboveShift;
                drawn = layout.Floor(map, own + 1, out above, out aboveShift);
                return above - ownHeight;
            }
            catch (Exception e)
            {
                Log.WarningOnce(Tag + "couldn't read the floors over a pawn: " + e.Message, 0x4d33a7f3);
                return wall;
            }
        }

        // Eases the pawn's crouch under a ceiling toward what it needs and gives the crouch (0 .. 1 of CrouchDepth) and the
        // stoop (degrees) that bring its head down that far.
        private static void CeilingPose(Pawn pawn, Ease e, float size, bool upright, out float crouch, out float stoop)
        {
            float need = upright ? CeilingDrop(pawn, size) : 0f;
            e.ceilingDrop = Mathf.MoveTowards(e.ceilingDrop, need, CeilingEase * FigureTop * size * Time.deltaTime);
            crouch = 0f;
            stoop = 0f;
            if (e.ceilingDrop <= 0f)
            {
                return;
            }
            float depth = skin.CrouchDepth() * size;
            crouch = depth > 0f ? Mathf.Clamp01(e.ceilingDrop / depth) : 0f;
            float rest = e.ceilingDrop - crouch * depth;
            float back = FigureTop * size * BackShare;
            if (rest > 0f && back > 0f)
            {
                // The head's top swung down about the hips: it falls back * (1 - cos) for a bend of that angle.
                stoop = Mathf.Min(CeilingStoopMost, Mathf.Acos(Mathf.Clamp(1f - rest / back, -1f, 1f)) * Mathf.Rad2Deg);
            }
        }
    }
}
