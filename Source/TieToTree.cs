using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Tie to Tree (Workshop 3818011865, sk.ttt), found by name so the mod is not referenced. A prisoner it ties lies in an
    // unseen 1x1 bed (TietoTree_TreeRestraint) on the tree's own cell (TreeBindingUtility.TryFindTieCell), and the mod
    // draws its sprite lying facing south with a flat rope sprite over it (TreeRopeDrawer.Draw, from a postfix on
    // PawnRenderer.RenderPawnAt, which still runs under IsoRim's prefix). Here it sits on the ground with its back to the
    // trunk instead, facing away from the tree, south when they share the cell (the tied_tree clip: walk.tied_at), with a
    // rope drawn round its chest and belly and the trunk (DrawRope), and that sprite is left off.
    public static partial class WalkFigures
    {
        private const string TiedAnchor = "TietoTree_TreeRestraint";
        private const string TiedClip = "tied_tree";
        private const float TrunkRadius = 0.1f;    // cells: about the foot of IsoRim's grown trees' trunks (its shadow posts' trunkHalf)
        private const float TrunkGap = 0.005f;     // cells between the trunk and the furthest back of the figure (its head or hands)
        private const float TiedSleepNod = 18f;    // degrees the head drops further asleep
        // The rope: a loop at the height of each of RopeBones' joints, round the trunk and whatever of the body is within
        // RopeBand of that height (the arms too), as the rope would be pulled taut round them: their outline from above
        // (a convex hull), RopeClear out from it for the clothes, drawn as straight runs of tool_rope.obj.
        private static readonly string[] RopeBones = { "spine.002", "spine.001" };   // the chest and the belly
        private static readonly float[] RopeUp = { 0.06f, 0.02f };   // model units each loop is above its joint
        private const float RopeBand = 0.03f;      // model units either side of a loop's height
        private const float RopeClear = 0.02f;     // cells the rope stands off the body, for the clothes on it
        private const float RopeRadius = 0.014f;   // cells (make_rope.py's RADIUS)
        private const int TrunkSides = 12;

        private static bool tiedLooked;
        private static ThingDef tiedAnchor;          // null without Tie to Tree
        private static IDictionary tiedBindings;     // TreeBindingRegistry.byPawn: Pawn to TreeBinding
        private static FieldInfo tiedTree;           // TreeBinding.tree
        internal static readonly Dictionary<int, int> tiedDrawn = new Dictionary<int, int>();   // pawn id: the frame it was last drawn tied

        // Patched in from WalkFigures' constructor: Tie to Tree's rope sprite left off a prisoner drawn tied here.
        private static void PatchTieToTree(Harmony harmony)
        {
            try
            {
                Type drawer = GenTypes.GetTypeInAnyAssembly("Tie_to_Tree.TreeRopeDrawer");
                MethodInfo draw = drawer != null ? AccessTools.Method(drawer, "Draw") : null;
                if (draw != null)
                {
                    harmony.Patch(draw, prefix: new HarmonyMethod(typeof(WalkFigures), nameof(RopeSpritePrefix)));
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "Tie to Tree's rope sprite is left as it is: " + e.Message);
            }
        }

        private static bool RopeSpritePrefix(Pawn pawn)
        {
            int frame;
            return pawn == null || !tiedDrawn.TryGetValue(pawn.thingIDNumber, out frame) || Time.frameCount - frame > 2;
        }

        // Whether the pawn is tied to a tree, lying in its restraint: the tree, and the restraint (its unseen bed).
        private static bool TiedToTree(Pawn pawn, out Thing tree, out Building_Bed anchor)
        {
            tree = null;
            anchor = null;
            if (!tiedLooked)
            {
                tiedLooked = true;
                tiedAnchor = DefDatabase<ThingDef>.GetNamedSilentFail(TiedAnchor);
                if (tiedAnchor != null)
                {
                    try
                    {
                        Type registry = GenTypes.GetTypeInAnyAssembly("Tie_to_Tree.TreeBindingRegistry");
                        Type binding = GenTypes.GetTypeInAnyAssembly("Tie_to_Tree.TreeBinding");
                        FieldInfo byPawn = registry != null ? AccessTools.Field(registry, "byPawn") : null;
                        tiedBindings = byPawn != null ? byPawn.GetValue(null) as IDictionary : null;
                        tiedTree = binding != null ? AccessTools.Field(binding, "tree") : null;
                    }
                    catch (Exception e)
                    {
                        Log.Warning(Tag + "Tie to Tree's bindings were not found; a tied prisoner sits by the nearest tree: " + e.Message);
                    }
                }
            }
            if (tiedAnchor == null || !pawn.Spawned)
            {
                return false;
            }
            anchor = pawn.CurrentBed();
            if (anchor == null || anchor.def != tiedAnchor)
            {
                return false;
            }
            if (tiedBindings != null && tiedTree != null && tiedBindings.Contains(pawn))
            {
                tree = tiedTree.GetValue(tiedBindings[pawn]) as Thing;
            }
            if (tree == null || !tree.Spawned || tree.Map != pawn.Map)
            {
                // Without the binding: the tree on its cell (where the mod ties it), else the most grown one beside it.
                tree = null;
                foreach (IntVec3 c in GenAdj.CellsOccupiedBy(pawn).Concat(GenAdj.CellsAdjacent8Way(pawn)))
                {
                    Plant plant = c.InBounds(pawn.Map) ? c.GetPlant(pawn.Map) : null;
                    if (plant != null && plant.def.plant != null && plant.def.plant.IsTree && (tree == null || plant.Growth > ((Plant)tree).Growth))
                    {
                        tree = plant;
                        if (c == pawn.Position)
                        {
                            break;
                        }
                    }
                }
            }
            return tree != null;
        }

        // A prisoner tied to a tree: sat on the ground with its back against the trunk, facing away from it, on the ground
        // under the restraint (IsoRim lays a sleeper at the bed's surface height over it), the rope round it and the trunk.
        private static bool DrawTied(FigureDrawContext ctx, FigurePlacement placed, float size, Thing tree, Building_Bed anchor)
        {
            Pawn pawn = ctx.Pawn;
            Vector3 trunk = tree.TrueCenter();
            Vector3 away = pawn.Position.ToVector3Shifted() - trunk;
            away.y = 0f;
            away = away.sqrMagnitude > 1e-6f ? away.normalized : Vector3.back;
            float yaw = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg;
            float ground = placed.Position.y - placed.Lift - Api.SurfaceHeight(anchor);
            trunk.y = ground;
            if (!ctx.ForOtherMod && !SkinnedFigure.MaybeOnScreen(trunk + away * 0.4f, size))
            {
                return true;
            }
            try
            {
                SkinnedFigure.Shape shape = ShapeFor(pawn);
                SkinnedFigure.Posed fig = skin.For(pawn.thingIDNumber, shape);
                bool awake = pawn.Awake();
                skin.Begin(fig);
                CutLimbs(pawn, fig);
                skin.Nod(fig, awake ? 0f : TiedSleepNod);
                skin.Add(fig, TiedClip, awake ? LoopPhase(pawn, TiedClip) : 0f, 1f);
                skin.End(fig);
                float lowest, bodyTop;
                skin.Extent(fig, out lowest, out bodyTop);
                // Its back (the furthest back of it: the big head, or the hands behind it) against the trunk.
                Vector3[] verts = skin.BodyVerts(fig);
                float back = 0f;
                for (int i = 0; i < verts.Length; i++)
                {
                    back = Mathf.Max(back, -verts[i].z);
                }
                Vector3 at = trunk + away * (TrunkRadius + TrunkGap + back * size);
                at.y = ground - lowest * size;
                DrawDressed(pawn, fig, at, yaw, size);
                DrawRope(fig, verts, at, yaw, size, trunk);
                tiedDrawn[pawn.thingIDNumber] = Time.frameCount;
                if (tiedDrawn.Count > 64)
                {
                    PruneTied();
                }
            }
            catch (Exception e)
            {
                DrawFailed("drawing a chibi tied to a tree", pawn, e);
                return DrawPlain(ctx);
            }
            return true;
        }

        private static readonly List<int> tiedStale = new List<int>();

        private static void PruneTied()
        {
            tiedStale.Clear();
            foreach (KeyValuePair<int, int> kv in tiedDrawn)
            {
                if (Time.frameCount - kv.Value > 600)
                {
                    tiedStale.Add(kv.Key);
                }
            }
            for (int i = 0; i < tiedStale.Count; i++)
            {
                tiedDrawn.Remove(tiedStale[i]);
            }
        }

        private static readonly List<Vector2> ropeAround = new List<Vector2>();
        private static readonly List<Vector2> ropeHull = new List<Vector2>();

        // The rope's loops (RopeBones), in the figure's own space (model units times size, facing +z): each round the
        // trunk (trunk, world) and the body near its height, pulled taut.
        private static void DrawRope(SkinnedFigure.Posed fig, Vector3[] verts, Vector3 at, float yaw, float size, Vector3 trunk)
        {
            ModelHandle rope = ToolModel("tool_rope");
            if (rope == null)
            {
                return;
            }
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            Vector3 tree = Quaternion.Inverse(turn) * (trunk - at);
            for (int r = 0; r < RopeBones.Length; r++)
            {
                Vector3 joint;
                if (!skin.JointOf(fig, RopeBones[r], out joint))
                {
                    continue;
                }
                float height = joint.y + RopeUp[r];
                ropeAround.Clear();
                for (int i = 0; i < verts.Length; i++)
                {
                    if (Mathf.Abs(verts[i].y - height) <= RopeBand)
                    {
                        ropeAround.Add(new Vector2(verts[i].x, verts[i].z) * size);
                    }
                }
                if (ropeAround.Count < 3)
                {
                    continue;
                }
                for (int i = 0; i < TrunkSides; i++)
                {
                    float a = 2f * Mathf.PI * i / TrunkSides;
                    ropeAround.Add(new Vector2(tree.x + Mathf.Cos(a) * TrunkRadius, tree.z + Mathf.Sin(a) * TrunkRadius));
                }
                Hull(ropeAround, ropeHull);
                int n = ropeHull.Count;
                if (n < 3)
                {
                    continue;
                }
                // Each corner pushed out along the mean of its two sides' outward normals (anticlockwise, outward is the
                // side's right), so the rope clears the body and the trunk by RopeClear plus its own thickness.
                float y = height * size;
                Vector3 first = Vector3.zero, from = Vector3.zero;
                for (int i = 0; i <= n; i++)
                {
                    Vector2 prev = ropeHull[(i + n - 1) % n], here = ropeHull[i % n], next = ropeHull[(i + 1) % n];
                    Vector2 a = (here - prev).normalized, b = (next - here).normalized;
                    Vector2 outward = new Vector2(a.y, -a.x) + new Vector2(b.y, -b.x);
                    outward = outward.sqrMagnitude > 1e-8f ? outward.normalized : new Vector2(a.y, -a.x);
                    Vector2 p = here + outward * (RopeClear + RopeRadius);
                    Vector3 to = new Vector3(p.x, y, p.y);
                    if (i > 0)
                    {
                        Vector3 d = to - from;
                        float length = d.magnitude;
                        if (length > 1e-4f)
                        {
                            Vector3 along = d / length;
                            // Run on a rope's thickness past each corner, so the runs meet without a notch.
                            Matrix4x4 piece = Matrix4x4.Translate(from - along * RopeRadius) * Matrix4x4.Rotate(Quaternion.FromToRotation(Vector3.up, along))
                                * Matrix4x4.Scale(new Vector3(1f, length + 2f * RopeRadius, 1f));
                            Api.DrawModel(rope, at, yaw, piece, Color.white);
                        }
                    }
                    from = to;
                }
            }
        }

        // The convex hull of points, anticlockwise (Andrew's monotone chain), into hull.
        private static void Hull(List<Vector2> points, List<Vector2> hull)
        {
            points.Sort((p, q) => p.x != q.x ? p.x.CompareTo(q.x) : p.y.CompareTo(q.y));
            hull.Clear();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int k = 0; k < points.Count; k++)
                {
                    Vector2 p = points[pass == 0 ? k : points.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }
    }
}
