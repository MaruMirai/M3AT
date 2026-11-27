using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Another mod's garments fitted once and kept: FitToBody works out every vertex's weights, its move in every body key
    // and the body triangles it hides, 70 to 90 ms a garment (anim_scripts/garmentfit, Nov 2026), so the Wolfein pack's 119
    // took 10 to 16 s on every load, and up to about 40 s with the Miho pack making each heavier as it was read: the
    // "Preparing 3D" wait players saw. What it changes (the triangles' winding, weights, keys, hidden and opened body
    // triangles, hard) is kept on disk at <config>/M3AT/garment_cache/fit_<file>_<body>_<mesh>_v<FitVersion>.fit and put
    // back on the next load, keyed by the garment's file, the body as loaded (its file and its mesh as read, as a mod may
    // change it as it is read) and the garment's mesh as read and cut, so a change to any of them fits it again.
    public sealed partial class SkinnedFigure
    {
        private const int FitVersion = 1;   // raised when FitToBody changes, so fits kept on disk are made again
        internal static double FitSeconds;  // spent fitting this load (not from the cache), for the log
        internal static int FitsCached, FitsMade;
        // The same by pack (packageId): garments fitted fresh this load, and taken from fits kept on disk.
        internal static readonly Dictionary<string, int> FitsMadeBy = new Dictionary<string, int>(), FitsCachedBy = new Dictionary<string, int>();

        private static void CountFit(string owner, bool cached)
        {
            Dictionary<string, int> by = cached ? FitsCachedBy : FitsMadeBy;
            string key = owner ?? "";
            int n;
            by[key] = (by.TryGetValue(key, out n) ? n : 0) + 1;
        }

        // A pack's garments fitted fresh this load, and taken from the cache.
        internal static void FitsOf(string packageId, out int made, out int cached)
        {
            if (packageId == null || !FitsMadeBy.TryGetValue(packageId, out made))
            {
                made = 0;
            }
            if (packageId == null || !FitsCachedBy.TryGetValue(packageId, out cached))
            {
                cached = 0;
            }
        }

        // FitToBody, or its result kept from an earlier load of the very same garment on the very same body.
        private void FitOrCached(Piece g, Piece body, string path)
        {
            string file = FitCachePath(g, body, path);
            if (file != null && FitFromCache(g, file))
            {
                FitsCached++;
                CountFit(g.owner, true);
                return;
            }
            float started = Time.realtimeSinceStartup;
            FitToBody(g, body);
            FitSeconds += Time.realtimeSinceStartup - started;
            FitsMade++;
            CountFit(g.owner, false);
            if (file != null)
            {
                KeepFit(g, file);
            }
        }

        private string FitCachePath(Piece g, Piece body, string path)
        {
            if (path == null || !File.Exists(path))
            {
                return null;
            }
            return Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "garment_cache", "fit_" + Stamp(path) + "_" + stamp.Substring(0, 8) + "_" + body.rest.Length + "x"
                + body.tris.Length / 3 + "_" + g.rest.Length + "x" + g.tris.Length / 3 + "_v" + FitVersion + ".fit");
        }

        private static bool FitFromCache(Piece g, string file)
        {
            if (!File.Exists(file))
            {
                return false;
            }
            try
            {
                using (BinaryReader r = new BinaryReader(File.OpenRead(file)))
                {
                    int[] tris = Ints(r);
                    bool hard = r.ReadBoolean();
                    int[] firstWeight = Ints(r), weightBone = Ints(r);
                    float[] weight = Floats(r);
                    int keyCount = r.ReadInt32();
                    Dictionary<string, Vector3[]> keys = new Dictionary<string, Vector3[]>();
                    for (int k = 0; k < keyCount; k++)
                    {
                        string name = r.ReadString();
                        float[] xyz = Floats(r);
                        Vector3[] moves = new Vector3[xyz.Length / 3];
                        for (int i = 0; i < moves.Length; i++)
                        {
                            moves[i] = new Vector3(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
                        }
                        keys[name] = moves;
                    }
                    int[] hides = Ints(r), opens = Ints(r);
                    if (tris.Length != g.tris.Length || firstWeight.Length != g.rest.Length + 1)
                    {
                        return false;
                    }
                    g.tris = tris;
                    g.hard = hard;
                    g.firstWeight = firstWeight;
                    g.weightBone = weightBone;
                    g.weight = weight;
                    g.keys.Clear();
                    foreach (KeyValuePair<string, Vector3[]> e in keys)
                    {
                        g.keys[e.Key] = e.Value;
                    }
                    g.hides = hides;
                    g.opens = opens;
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "a kept fit of " + g.name + " couldn't be read, so it is fitted again: " + e.Message);
                return false;
            }
        }

        private static void KeepFit(Piece g, string file)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                using (BinaryWriter w = new BinaryWriter(File.Create(file)))
                {
                    WriteInts(w, g.tris);
                    w.Write(g.hard);
                    WriteInts(w, g.firstWeight ?? new int[0]);
                    WriteInts(w, g.weightBone ?? new int[0]);
                    WriteFloats(w, g.weight ?? new float[0]);
                    w.Write(g.keys.Count);
                    foreach (KeyValuePair<string, Vector3[]> e in g.keys)
                    {
                        w.Write(e.Key);
                        float[] xyz = new float[e.Value.Length * 3];
                        for (int i = 0; i < e.Value.Length; i++)
                        {
                            xyz[3 * i] = e.Value[i].x;
                            xyz[3 * i + 1] = e.Value[i].y;
                            xyz[3 * i + 2] = e.Value[i].z;
                        }
                        WriteFloats(w, xyz);
                    }
                    WriteInts(w, g.hides ?? new int[0]);
                    WriteInts(w, g.opens ?? new int[0]);
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "a fitted garment couldn't be kept on disk (it is fitted again next time): " + e.Message);
            }
        }

        private static float[] Floats(BinaryReader r)
        {
            int n = r.ReadInt32();
            float[] a = new float[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = r.ReadSingle();
            }
            return a;
        }

        private static void WriteFloats(BinaryWriter w, float[] a)
        {
            w.Write(a.Length);
            foreach (float x in a)
            {
                w.Write(x);
            }
        }
    }
}
