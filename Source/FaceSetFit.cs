using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A human face set's eyes fitted to the reference layout, as a race's are (FaceGaze.cs).
    //
    // The chibi's face patch was laid out with Facial Animation - Retexture, and its eye whites (each eye's opaque box in
    // the lid's bottom layer, the root of width by height, over every style) are RefEyeSize of the face texture, their
    // middle at RefEyeRow. Experimentals' own art, which every human draws without the retexture (base Facial Animation's
    // face types are taken over by it), has its whites 12% smaller and 0.007 higher; small numbers, but on the chibi's big
    // face the eyes read noticeably high. So a human pawn's eyes are fitted by the mod its irises (the _Basic eyeball
    // layer) come from: that mod's own whites and irises measured (MeasureSet: its textures alone, not the ones another
    // mod puts over them at the same paths), scaled to RefEyeSize and moved so their middle is at RefEyeRow, with the
    // irises kept RefIrisApart apart, each within its slack. The retexture measures as the reference, so it is left as
    // it is. The races are fitted to the same reference (HumanEyeSizeNow, HumanEyeRowNow), whichever human set is loaded.
    public static partial class FaceLayers
    {
        private const float RefEyeSize = 0.0580f;     // Facial Animation - Retexture's whites, as EyeSizeOf measures them
        private const float RefEyeRow = 0.4502f;      // and their middle's row, v up
        private const float RefIrisApart = 0.113f;    // its irises' middles apart (alpha-weighted, as RaceEyeSpread measures)

        private struct SetMeasure
        {
            public float size, row, apart;   // 0: unmeasured
        }

        private static readonly Dictionary<string, SetMeasure> setMeasures = new Dictionary<string, SetMeasure>();
        private static readonly Dictionary<string, EyeFit> setFits = new Dictionary<string, EyeFit>();
        private static readonly Dictionary<int, string> humanEyeMods = new Dictionary<int, string>();   // by pawn: the mod its irises came from, as last drawn

        // The fit of a human pawn's eyes: false (no fit) until its eyes have been drawn once, or for a mod that can't be measured.
        private static bool HumanFitOf(Pawn pawn, out EyeFit fit)
        {
            fit = new EyeFit { scale = 1f };
            string mod;
            if (!humanEyeMods.TryGetValue(pawn.thingIDNumber, out mod))
            {
                return false;
            }
            if (PinnedEyes(mod, out fit))
            {
                return true;   // the fit the player tuned this face mod on (FacePins.cs)
            }
            fit = SetFit(mod);
            return true;
        }

        private static void NoteHumanEyeMod(Pawn pawn, string mod)
        {
            if (humanEyeMods.Count > 2048)
            {
                humanEyeMods.Clear();
            }
            humanEyeMods[pawn.thingIDNumber] = mod;
        }

        // A face mod's eyes fitted to the reference (none within the slacks, or unmeasured).
        private static EyeFit SetFit(string mod)
        {
            EyeFit fit;
            if (setFits.TryGetValue(mod, out fit))
            {
                return fit;
            }
            fit = new EyeFit { scale = 1f };
            SetMeasure m = MeasureSet(mod);
            if (m.size > 0f && m.row > 0f)
            {
                fit.row = m.row;
                fit.scale = Mathf.Clamp(RefEyeSize / m.size, EyeScaleLeast, EyeScaleMost);
                if (Mathf.Abs(fit.scale - 1f) < EyeScaleSlack)
                {
                    fit.scale = 1f;
                }
                fit.rise = Mathf.Clamp(RefEyeRow - m.row, -EyeRowMost, EyeRowMost);
                if (Mathf.Abs(fit.rise) < EyeRowSlack)
                {
                    fit.rise = 0f;
                }
                float apart = m.apart > 0f ? m.apart : RefIrisApart;
                fit.spread = Mathf.Clamp((RefIrisApart - apart * fit.scale) / 2f, -EyeSpreadMost, EyeSpreadMost);
                if (Mathf.Abs(fit.spread) < EyeSpreadSlack)
                {
                    fit.spread = 0f;
                }
            }
            setFits[mod] = fit;
            bool none = fit.scale == 1f && fit.rise == 0f && fit.spread == 0f;
            Log.Message(Tag + "the human faces of " + mod + ": " + (m.size > 0f ? "whites " + m.size.ToString("F4") + " at row " + m.row.ToString("F4") + ", irises " + m.apart.ToString("F3") + " apart" : "no eye whites of its own")
                + (none ? ": drawn as they are." : ": drawn " + fit.scale.ToString("F2") + " times the size, each eye moved " + (fit.spread >= 0f ? "out " : "in ") + Mathf.Abs(fit.spread).ToString("F3")
                + " and " + (fit.rise >= 0f ? "up " : "down ") + Mathf.Abs(fit.rise).ToString("F3") + " of the face texture."));
            return fit;
        }

        // A mod's own eye whites (Lids/<style>/<gender>/normal_bottom_south, EyeSizeOf's measure) and irises (Eyes/<style>/
        // <gender>/normal_south, each half's alpha-weighted middle), averaged over its styles.
        private static SetMeasure MeasureSet(string mod)
        {
            SetMeasure m;
            if (setMeasures.TryGetValue(mod, out m))
            {
                return m;
            }
            m = new SetMeasure();
            ModContentPack pack = null;
            foreach (ModContentPack p in LoadedModManager.RunningModsListForReading)
            {
                if (string.Equals(p.PackageId, mod, StringComparison.OrdinalIgnoreCase))
                {
                    pack = p;
                }
            }
            ModContentHolder<Texture2D> holder = pack != null ? pack.GetContentHolder<Texture2D>() : null;
            if (holder != null)
            {
                const int Read = 256;
                double size = 0, row = 0, apart = 0;
                int whites = 0, irises = 0;
                foreach (KeyValuePair<string, Texture2D> t in holder.contentList)
                {
                    if (t.Value == null)
                    {
                        continue;
                    }
                    bool white = t.Key.Contains("/Lids/") && t.Key.EndsWith("/normal_bottom_south", StringComparison.Ordinal);
                    bool iris = t.Key.Contains("/Eyes/") && t.Key.EndsWith("/normal_south", StringComparison.Ordinal);
                    if (!white && !iris)
                    {
                        continue;
                    }
                    Color32[] px;
                    try
                    {
                        px = Api.ReadSpritePixels(t.Value, Read);
                    }
                    catch (Exception)
                    {
                        px = null;
                    }
                    if (px == null || px.Length < Read * Read)
                    {
                        continue;
                    }
                    if (white)
                    {
                        for (int half = 0; half < 2; half++)
                        {
                            int c0 = Read, c1 = -1, r0 = Read, r1 = -1;
                            for (int r = 0; r < Read; r++)
                            {
                                for (int c = half * Read / 2; c < (half + 1) * Read / 2; c++)
                                {
                                    if (px[r * Read + c].a >= 128)
                                    {
                                        c0 = Math.Min(c0, c);
                                        c1 = Math.Max(c1, c);
                                        r0 = Math.Min(r0, r);
                                        r1 = Math.Max(r1, r);
                                    }
                                }
                            }
                            if (c1 >= c0 + 2 && r1 >= r0 + 2)
                            {
                                size += Math.Sqrt((c1 - c0 + 1) * (r1 - r0 + 1)) / Read;
                                row += (r0 + r1 + 1) / 2.0 / Read;   // rows bottom first: v up
                                whites++;
                            }
                        }
                    }
                    else
                    {
                        double[] sum = new double[2], su = new double[2];
                        for (int i = 0; i < Read * Read; i++)
                        {
                            int c = i % Read;
                            int half = c < Read / 2 ? 0 : 1;
                            float a = px[i].a / 255f;
                            sum[half] += a;
                            su[half] += a * (c + 0.5) / Read;
                        }
                        if (sum[0] > 0.5 && sum[1] > 0.5)
                        {
                            apart += su[1] / sum[1] - su[0] / sum[0];
                            irises++;
                        }
                    }
                }
                if (whites > 0)
                {
                    m.size = (float)(size / whites);
                    m.row = (float)(row / whites);
                }
                if (irises > 0)
                {
                    m.apart = (float)(apart / irises);
                }
            }
            setMeasures[mod] = m;
            return m;
        }
    }
}
