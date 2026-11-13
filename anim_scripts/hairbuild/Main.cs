// HairBuilder.cs run outside the game, so a hairstyle can be built and looked at (./view_hair.sh) without starting it.
//
//   RimWorld.dll <chibi folder> <head prefix> <out folder> [--beard] [--detail share] <style> <sprite prefix> [...]
//
// Each prefix names three files, <prefix>_south.rgba, _east.rgba and _north.rgba, as anim_scripts/hair_sprites.py
// writes them (HairBuilder.Size square, RGBA, rows top first). The head is the one the sprites are drawn over
// (HairBuilder.HeadSprites). The body is skin.txt and anim.txt from the chibi folder, read by SkinnedFigure's own reader
// as the game reads them, and the builder is made as SkinnedFigure.Builder makes it. Each style is written as the game
// writes it into its cache (<style>.txt, .shell.txt, .png), then, as SkinnedFigure.LoadHair does with built hair, cut
// down by Decimate to --detail of its triangles (the mod options' hairDetail, 0.3 by default; 1 keeps them all), with
// the uncut ones kept as <style>.full.txt and <style>.full.shell.txt. Run by anim_scripts/hairbuild/run.sh, which sets
// up the folder of links to the game's install it has to be run from.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using UnityEngine;

namespace M3AT
{
    public sealed partial class SkinnedFigure
    {
        // A builder for the chibi in folder, as Builder makes it in the game.
        internal static HairBuilder HarnessBuilder(string folder, bool beard, out SkinnedFigure figure)
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
            s.ReadAnim(Path.Combine(folder, "anim.txt"));
            figure = s;
            return new HairBuilder(body.rest, body.tris, s.OnHead(), body.keys, turn, s.boneHead[s.headBone], "", beard, s.Scalp(), s.OnArm());
        }

        // Cuts a written hair file (and its shell) down as LoadHair does, writing them back in the same format.
        internal void HarnessDecimate(string path, float share, bool beard)
        {
            Piece h = ReadMesh(path, false, null);
            if (beard)
            {
                share = Mathf.Max(share, (float)BeardTriangles / Mathf.Max(h.tris.Length / 3, 1));
            }
            string full = path.Substring(0, path.Length - ".txt".Length) + ".full.txt";
            File.Copy(path, full, true);
            // Cut and walled as LoadHair does (HairlineWalls.cs); M3AT_EDGE_WALLS set keeps the builder's walls.
            HairlineWallsOn = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("M3AT_EDGE_WALLS"));
            WallSagOn = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("M3AT_FIXED_SAG"));
            WallStepAlong = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("M3AT_WALL_SEGMENTS"));
            string wallStep = Environment.GetEnvironmentVariable("M3AT_WALL_STEP");
            if (!string.IsNullOrEmpty(wallStep))
            {
                WallStep = float.Parse(wallStep, System.Globalization.CultureInfo.InvariantCulture);
            }
            float cover;
            System.Diagnostics.Stopwatch took = System.Diagnostics.Stopwatch.StartNew();
            // The image's art, as LoadHair reads it, for the walls' texture places.
            WallAtlas atlas = null;
            string image = path.Substring(0, path.Length - ".txt".Length) + ".png";
            if (File.Exists(image) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("M3AT_WALLS_UNSNAPPED")))
            {
                Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                t.LoadImage(File.ReadAllBytes(image));
                atlas = WallAtlas.Of(t.GetPixels32(), t.width, t.height);
            }
            Piece cut = CutWithWalls(h, share, beard, atlas, out cover);
            if (cover >= 0f)
            {
                Console.WriteLine("WALLS " + Path.GetFileName(path) + " cut and walled in " + took.ElapsedMilliseconds + " ms: hairline covered "
                    + cover.ToString("0.00") + (cover >= HairlineCover ? ", hung from the hairline" : ", too little: the builder's walls kept"));
            }
            File.WriteAllText(path, Written(cut, File.ReadAllLines(path)));
            Console.WriteLine("CUT " + Path.GetFileName(path) + ": " + h.tris.Length / 3 + " to " + cut.tris.Length / 3 + " triangles");
            string shellPath = path.Substring(0, path.Length - ".txt".Length) + ".shell.txt";
            if (File.Exists(shellPath) && share < 1f)
            {
                Piece shell = ReadMesh(shellPath, false, null);
                File.Copy(shellPath, shellPath.Replace(".shell.txt", ".full.shell.txt"), true);
                Piece shellCut = Decimated(shell, null, share);
                File.WriteAllText(shellPath, Written(shellCut, File.ReadAllLines(shellPath)));
                Console.WriteLine("CUT " + Path.GetFileName(shellPath) + ": " + shell.tris.Length / 3 + " to " + shellCut.tris.Length / 3 + " triangles");
            }
        }

        // A piece as a hair file: the comments of the one it was read from, then its v, n, t, f, key/d, hide and skin lines.
        private static string Written(Piece p, string[] was)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string line in was)
            {
                if (line.StartsWith("#") && !line.Contains("walls under the open edges"))
                {
                    sb.Append(line).Append('\n');
                }
            }
            sb.Append("# cut down by the harness to ").Append(p.tris.Length / 3).Append(" triangles (Decimate, as SkinnedFigure.LoadHair)\n");
            Func<float, string> num = f => f.ToString("0.######", CultureInfo.InvariantCulture);
            foreach (Vector3 v in p.rest)
            {
                sb.Append("v ").Append(num(v.x)).Append(' ').Append(num(v.y)).Append(' ').Append(num(v.z)).Append('\n');
            }
            if (p.normal != null)
            {
                foreach (Vector3 n in p.normal)
                {
                    sb.Append("n ").Append(num(n.x)).Append(' ').Append(num(n.y)).Append(' ').Append(num(n.z)).Append('\n');
                }
            }
            if (p.uvs != null)
            {
                foreach (Vector2 t in p.uvs)
                {
                    sb.Append("t ").Append(num(t.x)).Append(' ').Append(num(t.y)).Append('\n');
                }
            }
            for (int k = 0; k + 2 < p.tris.Length; k += 3)
            {
                sb.Append("f ").Append(p.tris[k]).Append(' ').Append(p.tris[k + 1]).Append(' ').Append(p.tris[k + 2]).Append('\n');
            }
            if (p.wallsFrom >= 0)
            {
                sb.Append("walls ").Append(p.wallsFrom).Append('\n');
            }
            foreach (KeyValuePair<string, Vector3[]> key in p.keys)
            {
                sb.Append("key ").Append(key.Key).Append('\n');
                for (int i = 0; i < key.Value.Length; i++)
                {
                    Vector3 d = key.Value[i];
                    if (d != Vector3.zero)
                    {
                        sb.Append("d ").Append(i).Append(' ').Append(num(d.x)).Append(' ').Append(num(d.y)).Append(' ').Append(num(d.z)).Append('\n');
                    }
                }
            }
            if (p.hides != null && p.hides.Length > 0)
            {
                for (int k = 0; k < p.hides.Length; k += 32)
                {
                    sb.Append("hide");
                    for (int i = k; i < Math.Min(k + 32, p.hides.Length); i++)
                    {
                        sb.Append(' ').Append(p.hides[i]);
                    }
                    sb.Append('\n');
                }
            }
            if (p.skinVert != null)
            {
                List<int> skin = new List<int>();
                for (int i = 0; i < p.skinVert.Length; i++)
                {
                    if (p.skinVert[i])
                    {
                        skin.Add(i);
                    }
                }
                for (int k = 0; k < skin.Count; k += 32)
                {
                    sb.Append("skin");
                    for (int i = k; i < Math.Min(k + 32, skin.Count); i++)
                    {
                        sb.Append(' ').Append(skin[i]);
                    }
                    sb.Append('\n');
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
                return Run(argv);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("hairbuild: " + e);
                return 1;
            }
        }

        private static int Run(string[] argv)
        {
            List<string> args = new List<string>();
            bool beard = false;
            float detail = 0.3f;
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == "--beard")
                {
                    beard = true;
                }
                else if (argv[i] == "--detail")
                {
                    detail = float.Parse(argv[++i], CultureInfo.InvariantCulture);
                }
                else
                {
                    args.Add(argv[i]);
                }
            }
            if (args.Count < 5 || (args.Count - 3) % 2 != 0)
            {
                Console.Error.WriteLine("usage: RimWorld.dll <chibi folder> <head prefix> <out folder> [--beard] [--detail share] <style> <sprite prefix> [...]");
                return 2;
            }
            SkinnedFigure figure;
            HairBuilder builder = SkinnedFigure.HarnessBuilder(args[0], beard, out figure);
            HairBuilder.LineUpOn = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("M3AT_NO_LINEUP"));
            HairBuilder.Tapered = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("M3AT_NO_TAPER"));
            if (!builder.UseHead(Sprites(args[1], false)))
            {
                Console.Error.WriteLine("hairbuild: the head sprites are empty");
                return 1;
            }
            string outDir = args[2];
            int failed = 0;
            for (int k = 3; k + 1 < args.Count; k += 2)
            {
                string style = args[k], path = Path.Combine(outDir, style);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                foreach (string old in new[] { ".txt", ".shell.txt", ".full.txt", ".full.shell.txt", ".png", ".flat.png", ".none" })
                {
                    File.Delete(path + old);
                }
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                HairBuilder.Built built;
                try
                {
                    built = builder.Build(style, Sprites(args[k + 1], beard));
                    if (beard)
                    {
                        // The beard laid flat on the face patch, as the 2D option draws it (SkinnedFigure.FlatBeardOf).
                        const int flatSize = 512;
                        Color32[] flat = builder.FlatFace(Sprites(args[k + 1], beard), flatSize);
                        if (flat != null)
                        {
                            File.WriteAllBytes(path + ".flat.png", Png(flat, flatSize, flatSize));
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("BUILD " + style + " failed: " + e);
                    failed++;
                    continue;
                }
                if (built.mesh == null)
                {
                    File.WriteAllText(path + ".none", "# nothing at alpha 0.5 or more\n");
                    Console.WriteLine("BUILD " + style + ": nothing solid enough (" + path + ".none)");
                    continue;
                }
                File.WriteAllBytes(path + ".png", Png(built.atlas, HairBuilder.Panels * HairBuilder.Size, HairBuilder.Size));
                if (built.shell != null)
                {
                    File.WriteAllText(path + ".shell.txt", built.shell);
                }
                File.WriteAllText(path + ".txt", built.mesh);
                if (builder.lastSideBack != 0)
                {
                    Console.WriteLine("LINEUP " + style + ": side sprite moved " + builder.lastSideBack + " px back to the head's back");
                }
                if (builder.lastLineUp != 0)
                {
                    Console.WriteLine("LINEUP " + style + ": front and back sprites " + builder.lastLineUp + " px apart, each moved half way onto the head");
                }
                Console.WriteLine("BUILD " + style + ": " + built.triangles + " triangles in " + watch.ElapsedMilliseconds + " ms"
                    + (built.shell != null ? ", its shell hides " + built.hidden + " of the body's" : "") + " -> " + path + ".txt");
                figure.HarnessDecimate(path + ".txt", detail, beard);
            }
            return failed == 0 ? 0 : 1;
        }

        private static HairBuilder.Sprites Sprites(string prefix, bool beard)
        {
            Color32[][] px = new Color32[3][];
            string[] views = { "south", "east", "north" };
            for (int v = 0; v < 3; v++)
            {
                byte[] b = File.ReadAllBytes(prefix + "_" + views[v] + ".rgba");
                int n = HairBuilder.Size * HairBuilder.Size;
                if (b.Length != n * 4)
                {
                    throw new InvalidDataException(prefix + "_" + views[v] + ".rgba is " + b.Length + " bytes, not " + n * 4);
                }
                px[v] = new Color32[n];
                for (int i = 0; i < n; i++)
                {
                    px[v][i] = new Color32(b[4 * i], b[4 * i + 1], b[4 * i + 2], b[4 * i + 3]);
                }
            }
            return HairBuilder.From(px[0], px[1], px[2], beard);
        }

        // An RGBA PNG of pixels given rows bottom first (as Texture2D.SetPixels32 takes them, and EncodeToPNG writes them).
        private static byte[] Png(Color32[] px, int w, int h)
        {
            MemoryStream raw = new MemoryStream();
            for (int y = h - 1; y >= 0; y--)
            {
                raw.WriteByte(0);
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[y * w + x];
                    raw.WriteByte(c.r);
                    raw.WriteByte(c.g);
                    raw.WriteByte(c.b);
                    raw.WriteByte(c.a);
                }
            }
            MemoryStream z = new MemoryStream();
            using (ZLibStream zs = new ZLibStream(z, CompressionLevel.Optimal, true))
            {
                raw.Position = 0;
                raw.CopyTo(zs);
            }
            MemoryStream png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            byte[] ihdr = new byte[13];
            BigEndian(ihdr, 0, w);
            BigEndian(ihdr, 4, h);
            ihdr[8] = 8;
            ihdr[9] = 6;
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", z.ToArray());
            Chunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        private static void BigEndian(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4], crc = new byte[4];
            BigEndian(len, 0, data.Length);
            s.Write(len, 0, 4);
            byte[] t = Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint c = 0xFFFFFFFFu;
            foreach (byte[] part in new[] { t, data })
            {
                foreach (byte x in part)
                {
                    c ^= x;
                    for (int k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    }
                }
            }
            BigEndian(crc, 0, (int)(c ^ 0xFFFFFFFFu));
            s.Write(crc, 0, 4);
        }
    }
}
