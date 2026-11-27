using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Budgets for other mods' garments: triangles and image size. Every triangle of a worn garment is skinned on the
    // graphics card every frame for every pawn wearing it, and its rest pose is set again on the CPU, and the mesh uploaded,
    // each time that pawn's shape changes; which triangles of it a garment over it hides is worked out per pair. This mod's
    // own garments are 230 triangles at the median and 1,494 at most (the body is 1,194); the first race packs made for it
    // came in at a median of 1,334 (Wolfein) and 2,367 (Miho), with helmets of 17,000 to 20,000, fifteen bodies' worth, and
    // cost 11 ms a frame for 24 pawns as made against 1.1 ms cut down (Nov 2026, addoncost). So a garment from another mod
    // over its budget (a share of this mod's like garment, OursOf, by the garmentDetail option) is cut
    // down to it as it loads (Decimate, as built hair is: each vertex kept keeps its own weights, keys and texture place),
    // and its image over TextureBudget is shrunk; the log, the bug report and the content switcher (ContentWindow.cs) name
    // it, with what it was made with (Piece.madeTris, madeTexture), so the cost is seen as the garment's and its maker's.
    // At None they are kept as made, up to Ceiling (a mistaken export, not a choice). A cut is kept on disk by the
    // file's hash and the target (garment_cache), as cutting the Miho pack's 130 took 2 s at the first draw.
    public sealed partial class SkinnedFigure
    {
        // A garment's budget is a share of this mod's own garment for the same apparel, or the one the stand-in scoring
        // would draw for it (OursOf; a one-piece suit is the shirt and trousers together), as M3AT's are 28 to 1,494
        // triangles: one budget for all let a shirt be twelve of ours and kept a hairband no lighter than a helmet.
        internal const float DefaultShare = 2f;    // Default: twice as many as ours (1.5 before 2026-11-25)
        internal const float FastShare = 1.2f;     // Fast: a little over ours
        internal const float LightShare = 3f;      // Light: three times ours, for more of the makers' detail
        internal const int OursLeast = 200;        // triangles: ours taken as at least this, so a mod's hairband matched to our
                                                   // 32-triangle ribbon isn't cut to nothing (400 at Default, 240 at High)
        internal const int OursHead = 650;         // triangles: taken as ours for a piece with no match worn on the head (a full helmet's, the vacsuit's)
        internal const int OursBody = 1050;        // and for one on the body or a gene part (power armour's)
        internal const int Ceiling = 30000;        // triangles, kept even at None at most
        internal const int TextureBudget = 1024;   // pixels, an image's longer side at Default (half at High, twice at None)
        private const float NeckHeight = 0.857f;   // cells: spine.005's head, the neck's root at rest
        private const float OnHeadShare = 0.8f;    // of a garment's vertices above the neck, for it to count as head wear
        private const int CacheVersion = 1;        // raised when Cut changes, so cuts kept on disk are made again

        // How long loading the chibi took (Load), seconds, and its share by mod: "" this mod's own body and garments, else the
        // packageId (or, for the config folder's, its name). For the log, and the content switcher's "adds N s to loading".
        internal static readonly Dictionary<string, double> LoadSeconds = new Dictionary<string, double>();
        internal static readonly Dictionary<string, int> LoadPieces = new Dictionary<string, int>();
        internal static double LoadTotal, CutSeconds;
        internal const double LoadWarnSeconds = 3.0;   // content packs adding this much to loading are named in a message

        private static void AddLoadSeconds(string owner, double seconds, string name = null)
        {
            string key = owner ?? name ?? "";
            double was;
            LoadSeconds[key] = (LoadSeconds.TryGetValue(key, out was) ? was : 0.0) + seconds;
            int n;
            LoadPieces[key] = (LoadPieces.TryGetValue(key, out n) ? n : 0) + 1;
        }

        // Seconds a pack added to loading the chibi (its garments read, cut and fitted); 0 for none.
        internal static double LoadSecondsOf(string packageId)
        {
            double v;
            return packageId != null && LoadSeconds.TryGetValue(packageId, out v) ? v : 0.0;
        }

        private static void LogLoadCosts()
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, double> e in LoadSeconds)
            {
                if (e.Key.Length > 0)
                {
                    ModContentPack mod = AddOns.ModById(e.Key);
                    parts.Add((mod != null ? mod.Name : e.Key) + " " + e.Value.ToString("0.0") + " s for " + LoadPieces[e.Key]);
                }
            }
            double own;
            LoadSeconds.TryGetValue("", out own);
            Log.Message(Tag + "the chibi loaded in " + LoadTotal.ToString("0.0") + " s: its own body and garments " + own.ToString("0.0") + " s"
                + (parts.Count > 0 ? "; other mods' garments: " + string.Join(", ", parts) : "") + (CutSeconds > 0.05 ? "; optimizing " + CutSeconds.ToString("0.0") + " s of that" : "")
                + (FitsMade + FitsCached > 0 ? "; fitting " + FitSeconds.ToString("0.0") + " s for " + FitsMade + " (" + FitsCached + " kept from before)" : "") + ".");
        }

        // The garments cut down this session, as "name (mod): from -> to triangles", for the log and the bug report.
        internal static readonly List<string> Optimized = new List<string>();
        // And those over budget kept as made (None).
        internal static readonly List<string> OverBudget = new List<string>();

        // The garmentDetail option, "Auto Optimization": 0 High (FastShare of ours), 1 Default (DefaultShare), 2 None
        // (kept as made, to Ceiling), 3 Low (LightShare; after None, as it came later). Read as the chibi loads.
        internal static int Detail
        {
            get
            {
#if AUTOTEST
                // (addoncost's second run: <config>/M3AT/full_detail_garments, as the game is started through Steam)
                if (File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "full_detail_garments")))
                {
                    return 2;
                }
#endif
                return SettingsMod.Settings != null ? Mathf.Clamp(SettingsMod.Settings.garmentDetail, 0, 3) : 1;
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

        // The triangles of this mod's garment(s) another mod's is matched to (as made, so a pack that makes ours heavier as
        // they are read doesn't raise it), at least OursLeast: the garment this mod has for the first of its apparel the
        // game has, else the stand-ins the scoring picks for it over this mod's own (OwnMatchFor), else OursHead or OursBody
        // (a gene part, or apparel nothing of ours is like). Worked out once, as it loads: pieces then holds this mod's own.
        internal int OursOf(Piece g)
        {
            if (g.ours > 0)
            {
                return g.ours;
            }
            int sum = 0;
            List<string> names = new List<string>();
            for (int i = 0; g.defs != null && i < g.defs.Length && sum == 0; i++)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(g.defs[i]);
                if (def == null || def.apparel == null)
                {
                    continue;
                }
                int mine;
                int[] match;
                try
                {
                    match = garmentOf.TryGetValue(def.defName, out mine) && mine > 0 && mine < pieces.Length && pieces[mine].owner == null
                        ? new[] { mine } : OwnMatchFor(def);
                }
                catch (Exception e)
                {
                    // (the scoring run mid-load: a failure there costs the garment its match, not its place)
                    Log.Warning(Tag + "couldn't match " + g.name + " to a garment of M3AT's for its budget, so it is held to the fallback: " + e.Message);
                    match = new int[0];
                }
                foreach (int m in match)
                {
                    if (m > 0 && m < pieces.Length && pieces[m].owner == null)
                    {
                        sum += pieces[m].madeTris > 0 ? pieces[m].madeTris : pieces[m].tris.Length / 3;
                        names.Add(pieces[m].name);
                    }
                }
            }
            if (sum == 0)
            {
                sum = OnHead(g) ? OursHead : OursBody;
                names.Add("none");
            }
            g.ours = Mathf.Max(sum, OursLeast);
            g.oursName = string.Join(" + ", names);
            return g.ours;
        }

        // The budget a garment is held to at Default (as the content switcher shows it); at Fast it is FastShare of ours.
        internal static int BudgetOf(Piece g)
        {
            int ours = g.ours > 0 ? g.ours : OnHead(g) ? OursHead : OursBody;
            return Mathf.RoundToInt(ours * DefaultShare);
        }

        // The share of ours a level holds a garment to (None: no share, Ceiling).
        internal static float ShareAt(int level)
        {
            return level == 0 ? FastShare : level == 3 ? LightShare : DefaultShare;
        }

        // What it is cut to at the current level.
        private static int TargetOf(Piece g)
        {
            int ours = g.ours > 0 ? g.ours : OnHead(g) ? OursHead : OursBody;
            return Detail == 2 ? Ceiling : Mathf.RoundToInt(ours * ShareAt(Detail));
        }

        // This mod's heaviest garment as made, triangles, for the option's label (the top of what its shares come to).
        internal int HeaviestOwnGarment()
        {
            int most = 0;
            for (int g = 1; g < pieces.Length; g++)
            {
                if (pieces[g].owner == null && !pieces[g].held)
                {
                    most = Mathf.Max(most, pieces[g].madeTris > 0 ? pieces[g].madeTris : pieces[g].tris.Length / 3);
                }
            }
            return most;
        }

        internal int BudgetOf(int g)
        {
            return g > 0 && g < pieces.Length ? BudgetOf(pieces[g]) : Mathf.RoundToInt(OursBody * DefaultShare);
        }

        internal int TrianglesOf(int g)
        {
            return g >= 0 && g < pieces.Length && pieces[g].tris != null ? pieces[g].tris.Length / 3 : 0;
        }

        // As its maker made it (before any cut), triangles; and its image's longer side, pixels (0: none).
        internal int MadeTrianglesOf(int g)
        {
            return g > 0 && g < pieces.Length && pieces[g].madeTris > 0 ? pieces[g].madeTris : TrianglesOf(g);
        }

        internal int MadeTextureOf(int g)
        {
            return g > 0 && g < pieces.Length ? pieces[g].madeTexture : 0;
        }

        internal int TextureOf(int g)
        {
            return g > 0 && g < pieces.Length && pieces[g].texture != null ? Mathf.Max(pieces[g].texture.width, pieces[g].texture.height) : 0;
        }

        private int bodyFileTris;   // the body's triangles in skin.txt, before any mod changed it as it was read

        // A mesh file's triangles (its f lines).
        internal static int FileTriangles(string path)
        {
            int n = 0;
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (line.StartsWith("f ", StringComparison.Ordinal))
                    {
                        n++;
                    }
                }
            }
            catch (IOException)
            {
            }
            return n;
        }

        // This mod's own garments' triangles in all, as in their files and as read now: a mod that changes them as they
        // are read (the Miho pack) makes every pawn's clothes cost more.
        internal void OwnGarmentTriangles(out int made, out int now)
        {
            made = now = 0;
            for (int g = 1; g < pieces.Length; g++)
            {
                if (pieces[g].owner == null && pieces[g].madeTris > 0)
                {
                    made += pieces[g].madeTris;
                    now += TrianglesOf(g);
                }
            }
        }

        // Whether a mod changed the body as it was read (the Miho pack cuts its chest finer). The body as changed, and this
        // mod's own garments as that mod changed them, are then not optimized at any detail level: only other mods'
        // garments are held to the budget (KeepWithinBudget). The body can't be cut, as every garment names the body
        // triangles it hides by number and the face patch is picked from them.
        internal bool BodyChanged
        {
            get { return TrianglesOf(0) > BodyFileTriangles; }
        }

        // The name of the pack that changed the body, for the log and the options (another mod's if it can't be told).
        internal static string BodyChangerName()
        {
            AddOns.AddOn a = ContentSources.BodyPack();
            return a != null ? a.name : "another mod";
        }

        // Said once as the chibi loads, as the detail option can't help with it.
        private void LogBodyChange()
        {
            if (!BodyChanged)
            {
                return;
            }
            OwnGarmentTriangles(out int made, out int now);
            Log.Message(Tag + BodyChangerName() + " changes M3AT's body as it is read: the body " + BodyFileTriangles + " -> " + TrianglesOf(0)
                + " triangles, M3AT's own garments " + made + " -> " + now + " in all, for every pawn. M3AT can't auto-optimize those, at any"
                + " level of Auto Optimization; only other mods' own garments are optimized. Choose M3AT's body in the content switcher to undo it.");
        }

        // The body's triangles as exported, and now (another mod may have changed it as it was read).
        internal int BodyFileTriangles
        {
            get { return bodyFileTris > 0 ? bodyFileTris : TrianglesOf(0); }
        }

#if AUTOTEST
        // A sum over a piece's fitted data (weights, keys, hidden triangles), for checking a fit kept on disk against a fresh one.
        internal double FitChecksum(int g)
        {
            Piece p = pieces[g];
            double sum = p.tris.Length + p.hides.Length * 7.0;
            for (int i = 0; p.weight != null && i < p.weight.Length; i++)
            {
                sum += p.weight[i] * (p.weightBone[i] + 1) * (i % 13 + 1);
            }
            foreach (KeyValuePair<string, Vector3[]> k in p.keys)
            {
                for (int i = 0; i < k.Value.Length; i++)
                {
                    sum += (k.Value[i].x + 2f * k.Value[i].y + 3f * k.Value[i].z) * (i % 7 + 1);
                }
            }
            foreach (int h in p.hides)
            {
                sum += h;
            }
            return sum;
        }
#endif

        // The apparel (or genes) a piece is drawn for.
        internal string[] DefsOf(int g)
        {
            if (g <= 0 || g >= pieces.Length)
            {
                return new string[0];
            }
            return pieces[g].genes ?? pieces[g].defs ?? new string[0];
        }

        // A content pack's pieces (garments and gene parts), by piece.
        internal List<int> PackPieces(string packageId)
        {
            List<int> found = new List<int>();
            for (int g = 1; g < pieces.Length; g++)
            {
                if (pieces[g].owner != null && string.Equals(pieces[g].owner, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(g);
                }
            }
            return found;
        }

        // A content pack's pieces summed up for the content switcher: how many, their mean triangles as made, how many
        // were cut to the budget, kept over it (None), and had their image shrunk.
        internal void PackStats(string packageId, out int count, out int mean, out int cut, out int over, out int shrunk)
        {
            List<int> all = PackPieces(packageId);
            count = all.Count;
            cut = over = shrunk = 0;
            long made = 0;
            foreach (int g in all)
            {
                int m = MadeTrianglesOf(g), now = TrianglesOf(g);
                made += m;
                if (now < m)
                {
                    cut++;
                }
                else if (m > BudgetOf(g))
                {
                    over++;
                }
                if (MadeTextureOf(g) > TextureOf(g) && TextureOf(g) > 0)
                {
                    shrunk++;
                }
            }
            mean = count > 0 ? (int)(made / count) : 0;
        }

        // A content pack's pieces for one row of the content switcher (Clothing, Armour or GeneParts: PartOfPiece), for the
        // pack's cell there: how many, their mean triangles as drawn and as made, and the mean budget they are held to.
        internal void PartStats(string packageId, ContentPart part, out int count, out int mean, out int madeMean, out int budget)
        {
            count = 0;
            long now = 0, made = 0, held = 0;
            foreach (int g in PackPieces(packageId))
            {
                if (PartOfPiece(g) != part)
                {
                    continue;
                }
                count++;
                now += TrianglesOf(g);
                made += MadeTrianglesOf(g);
                held += BudgetOf(g);
            }
            mean = count > 0 ? (int)(now / count) : 0;
            madeMean = count > 0 ? (int)(made / count) : 0;
            budget = count > 0 ? (int)(held / count) : Mathf.RoundToInt(OursBody * DefaultShare);
        }

        // What M3AT draws in place of a pack's apparel of one row (Clothing or Armour), for the M3AT column of the content
        // switcher: how many of the pack's items, and the mean triangles of M3AT's own garment for each, or the stand-ins it
        // matches (as the clothing tool's M3AT's: OwnGarmentOf, else AutoStandInsFor). Items M3AT has nothing for count 0.
        // As M3AT made them (MadeTrianglesOf), as the pack's own column and the pack report are: the Miho pack cuts the
        // chest and seat of every garment finer as it is read, M3AT's too, which put M3AT's above a pack's like garments.
        internal void OwnStats(string packageId, ContentPart part, out int count, out int mean)
        {
            count = 0;
            long sum = 0;
            foreach (ThingDef d in PackApparel(packageId))
            {
                if (ContentSources.ClothingPartOf(d) != part)
                {
                    continue;
                }
                int own = OwnGarmentOf(d);
                int[] ours = own > 0 ? new[] { own } : AutoStandInsFor(d);
                count++;
                foreach (int g in ours)
                {
                    sum += MadeTrianglesOf(g);
                }
            }
            mean = count > 0 ? (int)(sum / count) : 0;
        }

        // One of this mod's model files' triangles (Models/chibi), 0 if it has none.
        internal int ModelFileTriangles(string file)
        {
            return file != null ? FileTriangles(Path.Combine(folder, file)) : 0;
        }

        // Another mod's garment, cut down to its budget if it is over (a share of this mod's like garment, OursOf; at None,
        // to Ceiling): the same piece changed in place. path: its file, whose hash keys the cut kept on disk (null: not kept).
        internal void KeepWithinBudget(Piece g, string mod, string path = null)
        {
            if (g == null || g.rest == null || g.tris == null || g.rest.Length == 0)
            {
                return;
            }
            int count = g.tris.Length / 3, ours = OursOf(g), budget = BudgetOf(g);
            // As its maker made it (its file), for the report and the switcher; the cut goes by what was read, which
            // another mod may have made heavier (the Miho pack cuts every garment's chest finer as it is read).
            g.madeTris = path != null && File.Exists(path) ? FileTriangles(path) : count;
            int target = TargetOf(g);
            string what = g.name + " (" + (mod ?? "another mod") + ")";
            string against = g.oursName == "none" ? "no garment of M3AT's like it, so " + ours + " taken as M3AT's" : "M3AT's " + g.oursName + ", " + ours;
            if (count <= target)
            {
                if (count > budget)
                {
                    OverBudget.Add(what + ": " + count + " triangles");
                    Log.Message(Tag + "the garment " + what + " has " + count + " triangles, over the budget of " + budget
                        + " (" + against + "); kept as made (Auto Optimization: " + (Detail == 2 ? "None" : "Low") + ").");
                }
                return;
            }
            float started = Time.realtimeSinceStartup;
            bool cached = Cached(g, path, target);
            if (!cached)
            {
                GarmentCut cut = CutOf(g, target);
                Keep(path, target, cut, g);   // (keyed by the mesh as read, before the cut changes it)
                Apply(g, cut);
            }
            CutSeconds += Time.realtimeSinceStartup - started;
            int now = g.tris.Length / 3;
            Optimized.Add(what + ": " + count + " -> " + now + " triangles");
            Log.Message(Tag + "the garment " + what + " has " + count + " triangles, over the budget of " + target
                + (Detail == 2 ? " (the most kept even at None)" : " (" + against + ")")
                + ", so it is drawn optimized to " + now + " (" + ((Time.realtimeSinceStartup - started) * 1000f).ToString("0") + " ms"
                + (cached ? ", cut kept from before" : "") + "). Its maker can see Docs/Apparel.md's triangle budget (and Docs/AGENTS.md, for an AI assistant making it).");
        }

        // The longest side an image of another mod's is kept at.
        internal static int TextureMost
        {
            get { return Detail == 0 ? TextureBudget / 2 : Detail == 2 ? TextureBudget * 2 : TextureBudget; }
        }

        // Another mod's garment image over TextureMost, shrunk by whole halvings (each pixel the mean of the four it
        // takes the place of) to fit; the same texture if it fits or can't be read.
        internal static Texture2D WithinTextureBudget(Texture2D t, string what)
        {
            int most = TextureMost;
            if (t == null || (t.width <= most && t.height <= most))
            {
                return t;
            }
            Color32[] px;
            try
            {
                px = t.GetPixels32();
            }
            catch (Exception)
            {
                return t;
            }
            int w = t.width, h = t.height, madeW = w, madeH = h;
            while ((w > most || h > most) && w >= 2 && h >= 2)
            {
                int nw = w / 2, nh = h / 2;
                Color32[] half = new Color32[nw * nh];
                for (int y = 0; y < nh; y++)
                {
                    for (int x = 0; x < nw; x++)
                    {
                        Color32 a = px[2 * y * w + 2 * x], b = px[2 * y * w + 2 * x + 1], c = px[(2 * y + 1) * w + 2 * x], d = px[(2 * y + 1) * w + 2 * x + 1];
                        half[y * nw + x] = new Color32((byte)((a.r + b.r + c.r + d.r) / 4), (byte)((a.g + b.g + c.g + d.g) / 4),
                            (byte)((a.b + b.b + c.b + d.b) / 4), (byte)((a.a + b.a + c.a + d.a) / 4));
                    }
                }
                px = half;
                w = nw;
                h = nh;
            }
            Texture2D small = new Texture2D(w, h, TextureFormat.RGBA32, true);
            small.SetPixels32(px);
            small.Apply();
            small.wrapMode = t.wrapMode;
            small.filterMode = t.filterMode;
            small.name = t.name;
            UnityEngine.Object.Destroy(t);
            Log.Message(Tag + "the garment image " + what + " is " + madeW + "x" + madeH + ", over the budget of " + most + " pixels a side, so it is shrunk to " + w + "x" + h + ".");
            return small;
        }

        // ---- cutting

        private sealed class GarmentCut
        {
            public int[] kept, tris;
            public bool[] glass, unlined;
        }

        // The cut of g down to about target triangles, each kind of triangle (plain, glass, cut out) by the same share,
        // so each triangle left is still one or the other.
        private static GarmentCut CutOf(Piece g, int target)
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
            return new GarmentCut
            {
                kept = kept.ToArray(),
                tris = tris.ToArray(),
                glass = g.glass != null ? glass.ToArray() : null,
                unlined = g.unlined != null ? unlined.ToArray() : null,
            };
        }

        // g as the cut leaves it: the vertices kept, each with its own weights, keys, texture place and normal.
        private static void Apply(Piece g, GarmentCut cut)
        {
            int[] k = cut.kept;
            g.tris = cut.tris;
            g.glass = cut.glass;
            g.unlined = cut.unlined;
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
            if (g.normalFix != null && g.normalFix.Length == n)
            {
                g.normalFix = Array.ConvertAll(k, o => g.normalFix[o]);
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

        // ---- cuts kept on disk: <config>/M3AT/garment_cache/<file hash>_<target>.cut

        // Keyed by the file, the target and the mesh as read (another mod may have changed it as it was read: the Miho pack
        // cuts every garment's chest finer), so a cut is only used on the very mesh it was made from.
        private static string CachePath(string path, int target, Piece g)
        {
            if (path == null || !File.Exists(path))
            {
                return null;
            }
            return Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "garment_cache", Stamp(path) + "_" + g.rest.Length + "_" + g.tris.Length / 3 + "_" + target
                + "_v" + CacheVersion + ".cut");
        }

        private static bool Cached(Piece g, string path, int target)
        {
            try
            {
                string file = CachePath(path, target, g);
                if (file == null || !File.Exists(file))
                {
                    return false;
                }
                using (BinaryReader r = new BinaryReader(File.OpenRead(file)))
                {
                    GarmentCut cut = new GarmentCut { kept = Ints(r), tris = Ints(r), glass = Bools(r), unlined = Bools(r) };
                    if (cut.kept.Length == 0 || cut.tris.Length == 0 || Array.Exists(cut.kept, o => o < 0 || o >= g.rest.Length)
                        || (cut.glass != null) != (g.glass != null) || (cut.unlined != null) != (g.unlined != null))
                    {
                        return false;
                    }
                    Apply(g, cut);
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "a kept cut of " + g.name + " couldn't be read, so it is cut again: " + e.Message);
                return false;
            }
        }

        private static void Keep(string path, int target, GarmentCut cut, Piece g)
        {
            try
            {
                string file = CachePath(path, target, g);
                if (file == null)
                {
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                using (BinaryWriter w = new BinaryWriter(File.Create(file)))
                {
                    WriteInts(w, cut.kept);
                    WriteInts(w, cut.tris);
                    WriteBools(w, cut.glass);
                    WriteBools(w, cut.unlined);
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "a cut garment couldn't be kept on disk (it is cut again next time): " + e.Message);
            }
        }

        private static int[] Ints(BinaryReader r)
        {
            int n = r.ReadInt32();
            int[] a = new int[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = r.ReadInt32();
            }
            return a;
        }

        private static bool[] Bools(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0)
            {
                return null;
            }
            bool[] a = new bool[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = r.ReadBoolean();
            }
            return a;
        }

        private static void WriteInts(BinaryWriter w, int[] a)
        {
            w.Write(a.Length);
            foreach (int x in a)
            {
                w.Write(x);
            }
        }

        private static void WriteBools(BinaryWriter w, bool[] a)
        {
            w.Write(a == null ? -1 : a.Length);
            if (a != null)
            {
                foreach (bool x in a)
                {
                    w.Write(x);
                }
            }
        }
    }
}
