using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A triangle budget for other mods' garments. Every triangle of a worn garment is skinned on the graphics card every
    // frame for every pawn wearing it, and its rest pose is set again on the CPU, and the mesh uploaded, each time that
    // pawn's shape changes; which triangles of it a garment over it hides is worked out per pair. This mod's own garments
    // are 230 triangles at the median and 1,494 at most (the body is 1,194); the first race packs made for it came in at
    // a median of 1,334 (Wolfein) and 2,367 (Miho), with helmets of 17,000 to 20,000, fifteen bodies' worth. So a garment
    // from another mod over its budget (ClothesBudget, HeadBudget for one worn on the head) is cut down to it as it loads
    // (Decimate, as built hair is: each vertex kept keeps its own weights, keys and texture place), and the log and the
    // bug report name it, so the cost is seen as the garment's. The fullDetailGarments option keeps them as made, up to
    // Ceiling, past which a garment is cut down even so (a mistaken export, not a choice).
    public sealed partial class SkinnedFigure
    {
        internal const int ClothesBudget = 2500;   // triangles, a garment worn on the body (about twice this mod's largest)
        internal const int HeadBudget = 1500;      // triangles, one worn on the head (three times this mod's largest helmet)
        internal const int Ceiling = 30000;        // triangles, kept even with fullDetailGarments at most
        private const float NeckHeight = 0.857f;   // cells: spine.005's head, the neck's root at rest
        private const float OnHeadShare = 0.8f;    // of a garment's vertices above the neck, for it to count as head wear

        // The garments cut down this session, as "name (mod): from -> to triangles", for the log and the bug report.
        internal static readonly List<string> Simplified = new List<string>();
        // And those over budget kept as made (fullDetailGarments).
        internal static readonly List<string> OverBudget = new List<string>();

        private static bool FullDetail
        {
            get
            {
#if AUTOTEST
                // (addoncost's second run: <config>/M3AT/full_detail_garments, as the game is started through Steam)
                if (System.IO.File.Exists(System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "full_detail_garments")))
                {
                    return true;
                }
#endif
                return SettingsMod.Settings != null && SettingsMod.Settings.fullDetailGarments;
            }
        }

        // Head wear: most of its vertices above the neck at rest (hats, helmets, masks, glasses, hairbands).
        private static bool OnHead(Piece g)
        {
            int above = 0;
            foreach (Vector3 v in g.rest)
            {
                if (v.y > NeckHeight)
                {
                    above++;
                }
            }
            return g.rest.Length > 0 && above >= OnHeadShare * g.rest.Length;
        }

        internal static int BudgetOf(Piece g)
        {
            return OnHead(g) ? HeadBudget : ClothesBudget;
        }

        // Another mod's garment, cut down to its budget if it is over (or to Ceiling with fullDetailGarments): the same
        // piece changed in place. Glass and cut-out triangles are cut down apart from the rest, so each triangle left is
        // still one or the other.
        internal static void KeepWithinBudget(Piece g, string mod)
        {
            if (g == null || g.rest == null || g.tris == null || g.rest.Length == 0)
            {
                return;
            }
            int count = g.tris.Length / 3, budget = BudgetOf(g);
            if (count <= budget)
            {
                return;
            }
            string what = g.name + " (" + (mod ?? "another mod") + ")";
            if (FullDetail && count <= Ceiling)
            {
                OverBudget.Add(what + ": " + count + " triangles");
                Log.Message(Tag + "the garment " + what + " has " + count + " triangles, over the budget of " + budget
                    + " for a garment " + (budget == HeadBudget ? "worn on the head" : "worn on the body") + "; kept as made (Keep mods' garments at full detail).");
                return;
            }
            int target = FullDetail ? Ceiling : budget;
            float started = Time.realtimeSinceStartup;
            Cut(g, target);
            int now = g.tris.Length / 3;
            Simplified.Add(what + ": " + count + " -> " + now + " triangles");
            Log.Message(Tag + "the garment " + what + " has " + count + " triangles, over the budget of " + target
                + (FullDetail ? " (the most kept even at full detail)" : " for a garment " + (budget == HeadBudget ? "worn on the head" : "worn on the body"))
                + ", so it is drawn simplified to " + now + " (" + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + " ms). Its maker can see Docs/Apparel.md's triangle budget.");
        }

        // Cuts g down to about target triangles, each kind of triangle (plain, glass, cut out) by the same share.
        private static void Cut(Piece g, int target)
        {
            int count = g.tris.Length / 3;
            float share = (float)target / count;
            // Triangles by kind: 0 plain, 1 glass, 2 cut out of the outline, 3 both.
            List<int>[] kinds = { new List<int>(), new List<int>(), new List<int>(), new List<int>() };
            for (int t = 0; t < count; t++)
            {
                int kind = (g.glass != null && g.glass[t] ? 1 : 0) + (g.unlined != null && g.unlined[t] ? 2 : 0);
                kinds[kind].Add(g.tris[3 * t]);
                kinds[kind].Add(g.tris[3 * t + 1]);
                kinds[kind].Add(g.tris[3 * t + 2]);
            }
            Dictionary<int, int> newOf = new Dictionary<int, int>();
            List<int> kept = new List<int>();
            List<int> tris = new List<int>();
            List<bool> glass = new List<bool>(), unlined = new List<bool>();
            for (int kind = 0; kind < 4; kind++)
            {
                if (kinds[kind].Count == 0)
                {
                    continue;
                }
                int[] part = kinds[kind].ToArray();
                int partTarget = Math.Max(1, Mathf.RoundToInt(part.Length / 3 * share));
                int[] partKept, partTris;
                Decimate.Run(g.rest, part, g.uvs, partTarget, out partKept, out partTris);
                foreach (int i in partTris)
                {
                    int old = partKept[i], at;
                    if (!newOf.TryGetValue(old, out at))
                    {
                        at = kept.Count;
                        newOf[old] = at;
                        kept.Add(old);
                    }
                    tris.Add(at);
                }
                for (int t = 0; t < partTris.Length / 3; t++)
                {
                    glass.Add((kind & 1) != 0);
                    unlined.Add((kind & 2) != 0);
                }
            }
            int[] k = kept.ToArray();
            g.tris = tris.ToArray();
            g.glass = g.glass != null ? glass.ToArray() : null;
            g.unlined = g.unlined != null ? unlined.ToArray() : null;
            if (g.firstWeight != null && g.firstWeight.Length == g.rest.Length + 1)
            {
                List<int> first = new List<int>(), bone = new List<int>();
                List<float> weight = new List<float>();
                foreach (int o in k)
                {
                    first.Add(bone.Count);
                    for (int w = g.firstWeight[o]; w < g.firstWeight[o + 1]; w++)
                    {
                        bone.Add(g.weightBone[w]);
                        weight.Add(g.weight[w]);
                    }
                }
                first.Add(bone.Count);
                g.firstWeight = first.ToArray();
                g.weightBone = bone.ToArray();
                g.weight = weight.ToArray();
            }
            int n = g.rest.Length;
            if (g.uvs != null && g.uvs.Length == n)
            {
                g.uvs = Array.ConvertAll(k, o => g.uvs[o]);
            }
            if (g.normal != null && g.normal.Length == n)
            {
                g.normal = Array.ConvertAll(k, o => g.normal[o]);
            }
            if (g.shade != null && g.shade.Length == n)
            {
                g.shade = Array.ConvertAll(k, o => g.shade[o]);
            }
            if (g.skinVert != null && g.skinVert.Length == n)
            {
                g.skinVert = Array.ConvertAll(k, o => g.skinVert[o]);
            }
            List<string> keys = new List<string>(g.keys.Keys);
            foreach (string key in keys)
            {
                Vector3[] moves = g.keys[key];
                if (moves != null && moves.Length == n)
                {
                    g.keys[key] = Array.ConvertAll(k, o => moves[o]);
                }
            }
            g.rest = Array.ConvertAll(k, o => g.rest[o]);
        }
    }
}
