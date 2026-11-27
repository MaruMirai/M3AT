// ModGarments.cs run outside the game: fits garment files to the body as the game fits another mod's garments, and
// reports how they came out, so a garment can be checked without starting the game.
//
//   RimWorld.dll <chibi folder> [--compare] [--write folder] <garment.txt> [...]
//   RimWorld.dll <chibi folder> --outfit <garment name> [...]   (outer first: a person in them, counted as made)
//
// Each file is read and fitted as SkinnedFigure.ReadModGarment does (its weights, every body key and the body triangles
// it hides, unless its body line is this body's). --compare first takes the file's own w, key and hide lines (this
// mod's exported garments have them) and reports how far the fit is from them: so the game's fitting can be checked
// against the exporter's. A garment whose exported weights are all on one bone is fitted with that bone line. --write
// writes each fitted garment there in the export's form, with this body's body line. Run by run.sh.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text;
using UnityEngine;

namespace M3AT
{
    public sealed partial class SkinnedFigure
    {
        internal static SkinnedFigure HarnessFigure(string folder)
        {
            string skinPath = Path.Combine(folder, "skin.txt");
            SkinnedFigure s = new SkinnedFigure { folder = folder, stamp = Stamp(skinPath) };
            Piece body = s.ReadMesh(skinPath, true, new List<string>());
            bool turn = InsideOut(body.rest, body.tris);
            s.turnRound = turn;
            if (turn)
            {
                TurnRound(body.tris);
            }
            s.pieces = new[] { body };
            s.ReadBoneNames(Path.Combine(folder, "anim.txt"));
            return s;
        }

        // The triangles a person in these garments of the folder's draws (outer garment first: each one goes over those after
        // it), as DrawnTriangles counts a worn outfit in the game, but from the files as made, before any mod changes them as
        // they are read. A development count (the About poly counts message no longer shows an outfit, since Nov 2026).
        internal string HarnessOutfit(List<string> names)
        {
            List<Piece> all = new List<Piece> { pieces[0] };
            foreach (string name in names)
            {
                Piece g = ReadMesh(Path.Combine(folder, "garment_" + name + ".txt"), false, null);
                if (turnRound)
                {
                    TurnRound(g.tris);
                }
                all.Add(g);
            }
            pieces = all.ToArray();
            GarmentMask mask = 0;
            for (int g = 1; g < pieces.Length; g++)
            {
                mask |= GarmentBit(g);
            }
            int body = BodyTris(mask, 0).Length / 3, sum = body;
            StringBuilder r = new StringBuilder();
            r.AppendFormat("body {0} of {1}\n", body, pieces[0].tris.Length / 3);
            for (int g = 1; g < pieces.Length; g++)
            {
                GarmentMask over = 0;
                for (int h = 1; h < g; h++)
                {
                    over |= GarmentBit(h);
                }
                int kept = GarmentTris(g, over).Length / 3;
                sum += kept;
                r.AppendFormat("{0} {1} of {2}\n", names[g - 1], kept, pieces[g].tris.Length / 3);
            }
            r.AppendFormat("outfit {0}\n", sum);
            return r.ToString();
        }

        internal string HarnessFit(string path, bool compare, string writeTo)
        {
            Piece body = pieces[0];
            StringBuilder r = new StringBuilder();
            Piece g = ReadMesh(path, false, null);
            if (g.name.StartsWith("garment_"))
            {
                g.name = g.name.Substring("garment_".Length);
            }
            g.modRoot = Path.GetDirectoryName(path);
            g.defs = g.defs ?? new string[0];
            int[] refFirst = g.firstWeight, refBone = g.weightBone;
            float[] refWeight = g.weight;
            Dictionary<string, Vector3[]> refKeys = new Dictionary<string, Vector3[]>(g.keys);
            int[] refHides = g.hides;
            bool hadWeights = g.weightsGiven;
            if (compare)
            {
                if (hadWeights && g.boneName == null && refBone.Distinct().Count() == 1 && refBone.Length > 0)
                {
                    g.boneName = boneNames[refBone[0]];
                }
                g.bodyStamp = null;
                g.ownWeights = false;
                g.hides = new int[0];
            }
            if (turnRound)
            {
                TurnRound(g.tris);   // as Load turns this mod's own
            }
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            FitToBody(g, body);
            watch.Stop();
            r.AppendFormat(CultureInfo.InvariantCulture, "{0}: {1} vertices, {2} triangles, fitted in {3} ms{4}; hides {5} body triangles; {6} keys\n",
                g.name, g.rest.Length, g.tris.Length / 3, watch.ElapsedMilliseconds, g.boneName != null ? ", on " + g.boneName : "", g.hides.Length, g.keys.Count);
            if (compare && hadWeights)
            {
                List<float> off = new List<float>();
                for (int i = 0; i < g.rest.Length; i++)
                {
                    Dictionary<int, float> d = new Dictionary<int, float>();
                    for (int k = refFirst[i]; k < refFirst[i + 1]; k++) { d[refBone[k]] = (d.TryGetValue(refBone[k], out float x) ? x : 0f) + refWeight[k]; }
                    for (int k = g.firstWeight[i]; k < g.firstWeight[i + 1]; k++) { d[g.weightBone[k]] = (d.TryGetValue(g.weightBone[k], out float x) ? x : 0f) - g.weight[k]; }
                    off.Add(d.Values.Sum(v => Mathf.Abs(v)) / 2f);
                }
                off.Sort();
                r.AppendFormat(CultureInfo.InvariantCulture, "  weights off the export's: mean {0:0.000}, 95% {1:0.000}, most {2:0.000} (0 the same, 1 none in common)\n",
                    off.Average(), off[(int)(off.Count * 0.95f)], off[off.Count - 1]);
            }
            if (compare && refKeys.Count > 0)
            {
                foreach (string key in body.keys.Keys.OrderBy(k => k))
                {
                    Vector3[] mine, theirs;
                    g.keys.TryGetValue(key, out mine);
                    refKeys.TryGetValue(key, out theirs);
                    float most = 0f, sum = 0f, size = 0f;
                    for (int i = 0; i < g.rest.Length; i++)
                    {
                        Vector3 a = mine != null ? mine[i] : Vector3.zero, b = theirs != null ? theirs[i] : Vector3.zero;
                        float e = (a - b).magnitude;
                        most = Mathf.Max(most, e);
                        sum += e;
                        size = Mathf.Max(size, b.magnitude);
                    }
                    r.AppendFormat(CultureInfo.InvariantCulture, "  key {0,-16} off by mean {1:0.0000}, most {2:0.0000} cells (the export's moves up to {3:0.0000})\n",
                        key, sum / g.rest.Length, most, size);
                }
            }
            if (compare && refHides.Length > 0)
            {
                HashSet<int> a = new HashSet<int>(refHides), b = new HashSet<int>(g.hides);
                r.AppendFormat("  hides {0} the export also hides, {1} it doesn't, and leaves {2} the export hides\n",
                    a.Intersect(b).Count(), b.Except(a).Count(), a.Except(b).Count());
            }
            if (writeTo != null)
            {
                Directory.CreateDirectory(writeTo);
                File.WriteAllText(Path.Combine(writeTo, Path.GetFileName(path)), Written(g));
            }
            return r.ToString();
        }

        // The fitted garment in the export's form, with this body's body line, so the game takes it as it is.
        private string Written(Piece g)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.Append("# M3AT: the garment ").Append(g.name).Append(", fitted by anim_scripts/garmentfit.\n");
            sb.Append("body ").Append(stamp).Append('\n');
            if (g.defs.Length > 0) sb.Append("defs ").Append(string.Join(" ", g.defs)).Append('\n');
            if (!string.IsNullOrEmpty(g.layer)) sb.Append("layer ").Append(g.layer).Append("\ngroups ").Append(string.Join(" ", g.groups)).Append('\n');
            if (g.genes != null) sb.Append("genes ").Append(string.Join(" ", g.genes)).Append('\n');
            if (g.textureFile != null) sb.Append("texture ").Append(g.textureFile).Append('\n');
            if (g.tint >= 0) sb.Append("tint ").Append(g.tint == 1 ? "yes" : "no").Append('\n');
            for (int k = 0; k < g.hides.Length; k += 32)
            {
                sb.Append("hide ").Append(string.Join(" ", g.hides.Skip(k).Take(32))).Append('\n');
            }
            foreach (Vector3 v in g.rest) sb.AppendFormat(inv, "v {0:0.00000} {1:0.00000} {2:0.00000}\n", v.x, v.y, v.z);
            for (int i = 0; i < g.rest.Length; i++)
            {
                sb.Append('w');
                for (int k = g.firstWeight[i]; k < g.firstWeight[i + 1]; k++) sb.AppendFormat(inv, " {0} {1:0.00000}", g.weightBone[k], g.weight[k]);
                sb.Append('\n');
            }
            if (g.uvs != null) foreach (Vector2 t in g.uvs) sb.AppendFormat(inv, "t {0:0.000000} {1:0.000000}\n", t.x, t.y);
            int[] tris = (int[])g.tris.Clone();
            if (turnRound) TurnRound(tris);   // as the file would have them
            for (int k = 0; k + 2 < tris.Length; k += 3) sb.AppendFormat("f {0} {1} {2}\n", tris[k], tris[k + 1], tris[k + 2]);
            foreach (KeyValuePair<string, Vector3[]> key in g.keys)
            {
                sb.Append("key ").Append(key.Key).Append('\n');
                for (int i = 0; i < key.Value.Length; i++)
                {
                    if (key.Value[i].sqrMagnitude > 1e-14f) sb.AppendFormat(inv, "d {0} {1:0.00000} {2:0.00000} {3:0.00000}\n", i, key.Value[i].x, key.Value[i].y, key.Value[i].z);
                }
            }
            return sb.ToString();
        }
    }

    internal static class HarnessMain
    {
        private static int Main(string[] argv)
        {
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string[] where =
                {
                    "/home/t/.local/share/Steam/steamapps/workshop/content/294100/3819577715/1.7/Assemblies/",
                    "/home/t/.local/share/Steam/steamapps/workshop/content/294100/2009463077/1.7/Assemblies/",
                };
                foreach (string dir in where)
                {
                    string path = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(path))
                    {
                        return context.LoadFromAssemblyPath(path);
                    }
                }
                return null;
            };
            try
            {
                List<string> files = new List<string>();
                bool compare = false, outfit = false;
                string writeTo = null;
                for (int i = 1; i < argv.Length; i++)
                {
                    if (argv[i] == "--compare") compare = true;
                    else if (argv[i] == "--outfit") outfit = true;
                    else if (argv[i] == "--write") writeTo = argv[++i];
                    else files.Add(argv[i]);
                }
                if (outfit && files.Count > 0)
                {
                    Console.Write(SkinnedFigure.HarnessFigure(argv[0]).HarnessOutfit(files));
                    return 0;
                }
                if (argv.Length < 2 || files.Count == 0)
                {
                    Console.Error.WriteLine("usage: RimWorld.dll <chibi folder> [--compare] [--write folder] <garment.txt> [...]");
                    return 2;
                }
                SkinnedFigure figure = SkinnedFigure.HarnessFigure(argv[0]);
                foreach (string file in files)
                {
                    try
                    {
                        Console.Write(figure.HarnessFit(file, compare, writeTo));
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(Path.GetFileName(file) + ": could not be fitted: " + e.Message);
                    }
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("garmentfit: " + e);
                return 1;
            }
        }
    }
}
