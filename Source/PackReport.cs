using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using RimWorld;
using Verse;

namespace M3AT
{
    // A content pack's report, for its maker (the content switcher's Pack report button, in development mode): each garment
    // against M3AT's budgets with what to do about it, its patches on M3AT's code with the ExpansionApi call to use instead,
    // its measured cost, and a line to put on its Workshop page. Written to <config>/M3AT/pack_report_<packageId>.md. Content
    // for M3AT is often made with an AI assistant, which reads files like this one: so it says plainly how to make garments
    // that fit M3AT and cost little (from the body's own faces, or a retexture of M3AT's own), as Docs/AGENTS.md does.
    internal static class PackReport
    {
        // Which ExpansionApi call takes the place of a patch on each part.
        private static readonly Dictionary<ContentPart, string> ApiFor = new Dictionary<ContentPart, string>
        {
            { ContentPart.GeneParts, "ExpansionApi.RegisterPack(id).RaceEars / RaceTail, or gene part garments (Docs/GeneParts.md)" },
            { ContentPart.Face, "ExpansionApi.RegisterPack(id).Face" },
            { ContentPart.Hair, "ExpansionApi.RegisterPack(id).Hair" },
            { ContentPart.SkinColour, "ExpansionApi.RegisterPack(id).SkinColour" },
            { ContentPart.Shared, "ExpansionApi.RegisterPack(id).Garments (for garment files added to skin.txt's list)" },
            { ContentPart.HeadwearFit, "not in the API yet (a garment's hide_hair line covers the hair)" },
            { ContentPart.BodyShape, "not in the API yet" },
        };

        internal static void Write(AddOns.AddOn a)
        {
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            if (f == null)
            {
                return;
            }
            StringBuilder s = new StringBuilder();
            s.AppendLine("# M3AT pack report: " + a.name);
            s.AppendLine();
            s.AppendLine("`" + a.packageId + "`, written " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " by M3AT's content switcher. Auto Optimization: "
                + ("M3AT_GarmentDetail_" + SkinnedFigure.Detail).Translate() + ". Races: " + (a.races.Count > 0 ? string.Join(", ", a.races) : "none found") + ".");
            s.AppendLine();
            s.AppendLine("M3AT draws every garment for every pawn wearing it, every frame. Its own garments are " + f.MadeTrianglesOf(f.GarmentNamed("BasicShirt"))
                + " triangles for a shirt and " + f.MadeTrianglesOf(f.GarmentNamed("PowerArmor")) + " for power armour; its body is " + f.BodyFileTriangles
                + ". A mod's garment's budget is " + SkinnedFigure.DefaultShare.ToString("0.0#") + " times M3AT's own garment for the same apparel, or the one M3AT would draw for it (at least "
                + Mathf.RoundToInt(SkinnedFigure.OursLeast * SkinnedFigure.DefaultShare) + " triangles; " + SkinnedFigure.FastShare.ToString("0.0#") + " times at Fast), and images of " + SkinnedFigure.TextureBudget + " pixels a side. Over it, M3AT optimizes the garment as it loads, which can spoil its shape.");
            s.AppendLine();

            if (f.TrianglesOf(0) > f.BodyFileTriangles && Costs.ChangesBody(a))
            {
                f.OwnGarmentTriangles(out int ownMade, out int ownNow);
                s.AppendLine("**Your pack changes M3AT's body as it is read: " + f.BodyFileTriangles + " triangles as M3AT made it, " + f.TrianglesOf(0)
                    + " now; M3AT's own garments " + ownMade + " triangles in all as made, " + ownNow + " now (other mods' garments too, before M3AT's budget sees them).** "
                    + "That is paid for every person in every colony, of every race, whether your race is in the game or not. "
                    + "Make a body shape (a race's proportions) a shape key on M3AT's own mesh, and keep the body as it is.");
                s.AppendLine();
            }
            List<int> pieces = f.PackPieces(a.packageId);
            int within = 0, cut = 0, over = 0, shrunk = 0;
            List<int> made = new List<int>();
            StringBuilder table = new StringBuilder();
            table.AppendLine("| Garment | For | Triangles as made | Budget | Image (px) | What to do |");
            table.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (int g in pieces.OrderByDescending(g => f.MadeTrianglesOf(g)))
            {
                int m = f.MadeTrianglesOf(g), now = f.TrianglesOf(g), budget = f.BudgetOf(g), img = f.MadeTextureOf(g);
                made.Add(m);
                List<string> todo = new List<string>();
                if (now < m)
                {
                    cut++;
                    todo.Add("over budget (drawn optimized to " + now + "): rebuild it from the body's own faces (Docs/Apparel.md, Approach 2), or retexture an M3AT garment (Approach 1); a separate mesh must be decimated under " + budget);
                }
                else if (m > budget)
                {
                    over++;
                    todo.Add("over budget (kept as made at High detail): rebuild or decimate under " + budget);
                }
                else
                {
                    within++;
                }
                if (img > SkinnedFigure.TextureBudget)
                {
                    shrunk++;
                    todo.Add("image " + img + " px: save it at most " + SkinnedFigure.TextureBudget + " px a side");
                }
                table.AppendLine("| " + f.PieceName(g) + " | " + string.Join(", ", f.DefsOf(g)) + " | " + m + " | " + budget + " | " + (img > 0 ? img.ToString() : "-")
                    + " | " + (todo.Count > 0 ? string.Join("; ", todo) : "within budget") + " |");
            }
            made.Sort();
            s.AppendLine("## Summary");
            s.AppendLine();
            s.AppendLine("- Garments and gene parts: " + pieces.Count + (made.Count > 0 ? ", median " + made[made.Count / 2] + " triangles as made" : "") + ".");
            s.AppendLine("- Within budget: " + within + "; optimized by M3AT: " + cut + "; over budget kept at High detail: " + over + "; images over budget: " + shrunk + ".");
            double loadSeconds = SkinnedFigure.LoadSecondsOf(a.packageId);
            s.AppendLine("- Loading: your garments added " + loadSeconds.ToString("0.0") + " s to loading M3AT's figures (of " + SkinnedFigure.LoadTotal.ToString("0.0")
                + " s in all), read, optimized and fitted to the body. Garments within the budget, and garments made from the body's own faces (which need little fitting), load fastest.");
            float ms, noise;
            int people;
            string date;
            s.AppendLine(BenchmarkWindow.LastMeasured(a.packageId, out ms, out noise, out people, out date)
                ? "- Measured cost (M3AT's benchmark, " + date + "): " + ms.ToString("0.00") + " ms a frame (noise +/-" + noise.ToString("0.00") + ") with " + people + " people drawn."
                : "- Not measured yet: run M3AT's benchmark (Mod settings, M3AT, Problems) with this pack and some pawns wearing its content in view.");
            s.AppendLine();
            s.AppendLine("## A line for your Workshop page");
            s.AppendLine();
            s.AppendLine(cut + over == 0 && shrunk == 0
                ? "> Within M3AT's budget: all " + pieces.Count + " garments (median " + (made.Count > 0 ? made[made.Count / 2] : 0) + " triangles)."
                : "> Not yet within M3AT's budget: " + (cut + over) + " of " + pieces.Count + " garments over it" + (shrunk > 0 ? ", " + shrunk + " images too large" : "") + ".");
            s.AppendLine();

            List<KeyValuePair<MethodBase, string>> patched = AddOns.PatchedBy(a.packageId);
            if (patched.Count > 0)
            {
                s.AppendLine("## Patches on M3AT's code");
                s.AppendLine();
                s.AppendLine("These can break with any M3AT update, as they reach its private methods. Players can still switch each part to M3AT's own (the patches are then taken off). Move to ExpansionApi 2 (Docs/RacePacks.md):");
                s.AppendLine();
                foreach (KeyValuePair<MethodBase, string> p in patched.OrderBy(p => p.Value))
                {
                    ContentPart part = ContentSources.PartOf(p.Key);
                    string api;
                    s.AppendLine("- `" + p.Value + "` (" + part + "): " + (ApiFor.TryGetValue(part, out api) ? api : "no API call for this yet: tell M3AT's author what you need it for"));
                }
                s.AppendLine();
            }
            if (pieces.Count > 0)
            {
                s.AppendLine("## Garments, heaviest first");
                s.AppendLine();
                s.Append(table);
                s.AppendLine();
            }
            s.AppendLine("## For AI assistants making content for M3AT");
            s.AppendLine();
            s.AppendLine("Read Docs/AGENTS.md in M3AT's folder first. In short: don't generate or sculpt garment meshes, and don't build them from sprites or image-to-3D models. "
                + "Make each garment from M3AT's own body (the garment kit's \"Garment from selected faces\": the body's faces pushed out, Docs/Apparel.md Approach 2), "
                + "or retexture one of M3AT's garments (a `base` line, Approach 1). Both fit every body type and pose, and cost a fraction of a generated mesh. "
                + "Register content with ExpansionApi 2 rather than Harmony patches on M3AT's private methods.");
            string path = Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "pack_report_" + a.packageId.Replace('.', '_') + ".md");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, s.ToString());
                Messages.Message("M3AT_PackReportWritten".Translate(path), MessageTypeDefOf.NeutralEvent, false);
                Log.Message(WalkFigures.Tag + "pack report written: " + path);
            }
            catch (Exception e)
            {
                Log.Warning(WalkFigures.Tag + "the pack report couldn't be written: " + e.Message);
            }
        }
    }
}
