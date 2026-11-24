using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Soft mouths redrawn in the hard-lined style of Facial Animation - Retexture (the raceMouthInk mod option, "Crisper
    // mouths": every face mod's but one already drawn in ink, InkStyled, so the retexture's are left as they are; covers
    // left alone). Experimentals' own closed mouth is the retexture's line in a light reddish brown, nearly gone on the
    // chibi's skin.
    //
    // Facial Animation - Retexture's mouths, which read well on the chibi, are pure black lines at part alpha. The race
    // face mods' are light colours the game multiplies by the skin (luminance 0.72 .. 0.97), drawn soft for a small 2D
    // head: Wolfein's with a white glow round the line and a pale blob of a lower lip under it, Ratkin's, Kiiro's and
    // Milira's faint. On the chibi's big flat face that came out as a smudged, "lippy" mouth. So each such mouth texture is
    // copied once (cached per texture) with:
    //   - the halo and lip shading taken out: light pixels (luminance over HaloLight) reachable from the texture's edge
    //     without crossing a darker one, so teeth and the inside of an open mouth, enclosed by its line, stay;
    //   - its rim inked: within InkRim pixels (at 512) of the shape's edge the darkness is InkRimTimes as deep and the
    //     colour half desaturated toward grey (InkRimColour), easing to InkInsideTimes well inside, so an open mouth is a
    //     dark outline round its own colour, as the retexture draws one, not a dark blot; and its alpha raised MouthAlpha.
    // Drawing only; the mod's own textures are left as they are.
    public static partial class FaceLayers
    {
        private const float HaloLight = 0.86f;      // luminance from which a pixel outside the line is halo or lip shading
        private const float HaloClear = 0.05f;      // alpha under which a pixel counts as clear, for the reach in
        private const float InkRim = 3f;            // pixels at 512 across the rim that is inked
        private const float InkRimTimes = 2f;       // how many times as dark the rim is drawn
        private const float InkInsideTimes = 1.25f; // and well inside the shape
        private const float InkRimColour = 0.45f;   // of the darkened colour kept on the rim, the rest grey (0.8 inside)
        private const float InkInsideColour = 0.8f;
        private const float InkDarkest = 0.92f;     // darkness at most
        private const float MouthAlpha = 1.25f;
        private const float InkSolid = 0.3f;        // alpha from which a pixel is in the shape, for the rim's depth
        private const int RestyleReadMost = 512;
        private const float InkDark = 0.2f;   // a mod whose mouths' median luminance is under this draws them in ink already

        private static readonly Dictionary<Texture, Texture> restyled = new Dictionary<Texture, Texture>();

        internal static bool RaceMouthInk { get { return SettingsMod.Settings == null || SettingsMod.Settings.raceMouthInk; } }

        // The texture a mouth layer (g's, layer the layer's name) is drawn with: with the option on, its restyled copy; else,
        // or for a cover (the skin-coloured layer hiding a sprite head's own mouth), a mod whose mouths are drawn in dark ink
        // already (InkStyled: the retexture), or one that can't be read, its own.
        internal static Texture MouthTexture(Graphic g, Texture tex, string layer)
        {
            if (tex == null || !RaceMouthInk || (layer != null && layer.Contains("Cover")) || tex.name.EndsWith("_cover", StringComparison.Ordinal)
                || InkStyled(ModOf(g).key))
            {
                return tex;
            }
            Texture made;
            if (!restyled.TryGetValue(tex, out made))
            {
                made = Restyle(tex);
                restyled[tex] = made;
            }
            return made ?? tex;
        }

        private static readonly Dictionary<string, bool> inkStyled = new Dictionary<string, bool>();

        // Whether a face mod draws its mouths in dark ink already (Facial Animation - Retexture's: median luminance 0.06,
        // against Experimentals' 0.55 and the races' 0.72 .. 0.97): the median over its own south mouth shapes' pixels' medians,
        // covers left out.
        private static bool InkStyled(string mod)
        {
            bool ink;
            if (inkStyled.TryGetValue(mod, out ink))
            {
                return ink;
            }
            List<float> medians = new List<float>();
            foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
            {
                if (!string.Equals(pack.PackageId, mod, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ModContentHolder<Texture2D> holder = pack.GetContentHolder<Texture2D>();
                if (holder == null)
                {
                    continue;
                }
                foreach (KeyValuePair<string, Texture2D> t in holder.contentList)
                {
                    if (t.Value == null || !t.Key.Contains("/Mouth/") || !t.Key.EndsWith("_south", StringComparison.Ordinal) || t.Key.EndsWith("_cover_south", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    Color32[] px;
                    try
                    {
                        px = Api.ReadSpritePixels(t.Value, 128);
                    }
                    catch (Exception)
                    {
                        px = null;
                    }
                    if (px == null)
                    {
                        continue;
                    }
                    List<float> lum = new List<float>();
                    foreach (Color32 p in px)
                    {
                        if (p.a > 8)
                        {
                            lum.Add((0.3f * p.r + 0.59f * p.g + 0.11f * p.b) / 255f);
                        }
                    }
                    if (lum.Count > 0)
                    {
                        lum.Sort();
                        medians.Add(lum[lum.Count / 2]);
                    }
                }
            }
            medians.Sort();
            ink = medians.Count > 0 && medians[medians.Count / 2] < InkDark;
            inkStyled[mod] = ink;
            Log.Message(Tag + "the mouths of " + mod + (medians.Count == 0 ? ": none of its own, drawn as they are." : ": median luminance " + medians[medians.Count / 2].ToString("F2") + " over " + medians.Count + " shapes, "
                + (ink ? "drawn in ink already, so as they are." : "so drawn darker round their edges (the Crisper mouths option).")));
            return ink;
        }

        private static Texture Restyle(Texture tex)
        {
            try
            {
                int n = Math.Min(RestyleReadMost, Math.Max(16, tex.width));
                Color32[] px = Api.ReadSpritePixels(tex, n);
                if (px == null || px.Length < n * n)
                {
                    return null;
                }
                int count = n * n;
                float[] lum = new float[count];
                List<float> seen = new List<float>();
                for (int i = 0; i < count; i++)
                {
                    lum[i] = (0.3f * px[i].r + 0.59f * px[i].g + 0.11f * px[i].b) / 255f;
                    if (px[i].a > 8)
                    {
                        seen.Add(lum[i]);
                    }
                }
                if (seen.Count == 0)
                {
                    return null;
                }

                // The halo: light or clear pixels reached from the edge, a flood fill over 4 neighbours.
                byte clearA = (byte)(HaloClear * 255f);
                bool[] light = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    light[i] = lum[i] > HaloLight || px[i].a < clearA;
                }
                bool[] reached = new bool[count];
                Stack<int> todo = new Stack<int>();
                for (int k = 0; k < n; k++)
                {
                    int[] edge = { k, (n - 1) * n + k, k * n, k * n + n - 1 };
                    foreach (int e in edge)
                    {
                        if (light[e] && !reached[e])
                        {
                            reached[e] = true;
                            todo.Push(e);
                        }
                    }
                }
                while (todo.Count > 0)
                {
                    int i = todo.Pop();
                    int x = i % n, y = i / n;
                    if (x > 0) Reach(i - 1, light, reached, todo);
                    if (x < n - 1) Reach(i + 1, light, reached, todo);
                    if (y > 0) Reach(i - n, light, reached, todo);
                    if (y < n - 1) Reach(i + n, light, reached, todo);
                }
                Color32[] outPx = new Color32[count];
                bool[] solid = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    outPx[i] = px[i];
                    if (reached[i])
                    {
                        outPx[i].a = 0;
                    }
                    solid[i] = outPx[i].a >= (byte)(InkSolid * 255f);
                }
                // How deep each pixel is inside the shape, in steps of erosion, up to twice the rim.
                int rim = Math.Max(1, Mathf.RoundToInt(InkRim * n / 512f));
                int most = 2 * rim + 1;
                int[] depth = new int[count];
                bool[] cur = (bool[])solid.Clone();
                bool[] next = new bool[count];
                for (int step = 0; step < most; step++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        if (cur[i])
                        {
                            depth[i]++;
                        }
                    }
                    for (int i = 0; i < count; i++)
                    {
                        int x = i % n, y = i / n;
                        next[i] = cur[i] && x > 0 && cur[i - 1] && x < n - 1 && cur[i + 1] && y > 0 && cur[i - n] && y < n - 1 && cur[i + n];
                    }
                    bool[] t = cur;
                    cur = next;
                    next = t;
                }
                for (int i = 0; i < count; i++)
                {
                    if (outPx[i].a == 0)
                    {
                        continue;
                    }
                    float inside = Mathf.Clamp01((depth[i] - rim) / (float)rim);   // 0 on the rim, 1 well inside
                    float times = Mathf.Lerp(InkRimTimes, InkInsideTimes, inside);
                    float keep = Mathf.Lerp(InkRimColour, InkInsideColour, inside);
                    float dark = Mathf.Min(InkDarkest, (1f - lum[i]) * times);
                    float grey = 1f - dark;
                    Color32 p = outPx[i];
                    outPx[i] = new Color32(
                        Inked(p.r, times, grey, keep), Inked(p.g, times, grey, keep), Inked(p.b, times, grey, keep),
                        (byte)Mathf.Min(255f, p.a * MouthAlpha));
                }
                Texture2D made = new Texture2D(n, n, TextureFormat.RGBA32, false);
                made.name = "Marus_inked_" + tex.name;
                made.wrapMode = TextureWrapMode.Clamp;
                made.filterMode = FilterMode.Bilinear;
                made.SetPixels32(outPx);   // rows bottom first, as read
                made.Apply(false, true);
                return made;
            }
            catch (Exception e)
            {
                Log.WarningOnce(Tag + "couldn't redraw a race's mouth " + tex.name + " in the human sets' style: " + e.Message, 73190544);
                return null;
            }
        }

        private static void Reach(int i, bool[] light, bool[] reached, Stack<int> todo)
        {
            if (light[i] && !reached[i])
            {
                reached[i] = true;
                todo.Push(i);
            }
        }

        // One channel: the colour darkened times over, mixed with the grey of that darkness.
        private static byte Inked(byte c, float times, float grey, float keep)
        {
            float v = c / 255f;
            float darker = Mathf.Clamp01(1f - (1f - v) * times);
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(grey * (1f - keep) + darker * keep) * 255f);
        }

        // ---- Another race's mouth at about a human's size.

        // The race face mods draw their mouths for their own 2D heads: Wolfein's about twice as wide as the human sets'
        // (0.065 of the texture against 0.037, medians over every shape), Ratkin's, Kiiro's and Milira's about 0.6 times.
        // So a race's mouth layers are scaled about the mouth's middle toward a human's width, by the ratio of the two
        // medians to the MouthFitPower (as a race's own look is partly its mouth: halfway would still read as Wolfein's
        // wide cat mouth), kept within MouthScaleLeast .. MouthScaleMost and left alone within MouthScaleSlack. Measured once
        // per race from the face set's Mouth folder; a tuned mod keeps the size it was tuned at (FacePins.cs).
        private const float MouthFitPower = 0.6f;
        private const float MouthScaleLeast = 0.7f, MouthScaleMost = 1.35f;
        private const float MouthScaleSlack = 0.06f;
        private const float HumanMouthWidth = 0.037f;   // if the human set's can't be measured: Facial Animation - Retexture's
        private const byte MouthWidthAlpha = 60;
        private static float drawMouthScale = 1f;
#if AUTOTEST
        internal static bool TestNoMouthFit;   // racefaces: the "before" pictures
#endif   // the pawn being drawn's (Draw, set with its mouth layer)
        private static readonly Dictionary<ThingDef, float> raceMouthScales = new Dictionary<ThingDef, float>();
        private static readonly Dictionary<string, float> mouthWidths = new Dictionary<string, float>();
        private static float humanMouthWidth = -1f;

        internal static float RaceMouthScale(Pawn pawn, Graphic g)
        {
            string mod = ModOf(g).key;
            float scale;
#if AUTOTEST
            if (TestNoMouthFit)
            {
                return 1f;
            }
#endif
            if (PinnedMouthScale(mod, out scale))
            {
                return scale;
            }
            if (!RaceMouthInk)
            {
                return 1f;   // the size fit goes with the rest of the mouths' changes, under the one option
            }
            if (pawn.def == RimWorld.ThingDefOf.Human)
            {
                return 1f;
            }
            if (raceMouthScales.TryGetValue(pawn.def, out scale))
            {
                return scale;
            }
            string path = g != null ? g.path : null;
            int at = path != null ? path.IndexOf("/Mouth/", StringComparison.Ordinal) : -1;
            float own = at >= 0 ? MouthWidthOf(path.Substring(0, at + 1)) : 0f;
            float human = HumanMouthWidthNow();
            scale = 1f;
            if (own > 0f && human > 0f)
            {
                scale = Mathf.Clamp(Mathf.Pow(human / own, MouthFitPower), MouthScaleLeast, MouthScaleMost);
                if (Mathf.Abs(scale - 1f) < MouthScaleSlack)
                {
                    scale = 1f;
                }
            }
            raceMouthScales[pawn.def] = scale;
            Log.Message(Tag + pawn.def.defName + "'s Facial Animation mouth " + (own > 0f ? own.ToString("F3") + " of the face texture wide (a human's " + human.ToString("F3") + "): " : "unmeasured: ")
                + (scale == 1f ? "left at its size." : "drawn " + scale.ToString("F2") + " times the size."));
            return scale;
        }

        private static float HumanMouthWidthNow()
        {
            if (humanMouthWidth < 0f)
            {
                humanMouthWidth = MouthWidthOf("Things/Pawn/FA-Experimentals/");
                if (humanMouthWidth <= 0f)
                {
                    humanMouthWidth = MouthWidthOf("Things/Pawn/Humanlike/");
                }
                if (humanMouthWidth <= 0f)
                {
                    humanMouthWidth = HumanMouthWidth;
                }
            }
            return humanMouthWidth;
        }

        // ---- A wide mouth shape drawn narrower.

        // Some shapes are drawn much wider than the set's others (the retexture's Impish and Smirk smiles, curving down on
        // one side and up on the other, 0.043 .. 0.047 of the texture against its 0.039 median; Experimentals' puzzled
        // mouths 0.055; UNAGI's Happakuwae 0.14), which reads too wide on the chibi's face. So each shape is measured as
        // drawn (its own texture, after the race's fit, RaceMouthScale), and only the part of its width past MouthWideFrom
        // of a human's median width is kept, MouthWideKeep of it: a wider shape still comes out wider than a narrower one,
        // never clamped to one size. Shapes under it are left alone. With the crisper mouths option (RaceMouthInk).
        // Not consts, so the dev window (FaceDevTune.cs) can move them.
        internal static float MouthWideFrom = 0.95f;
        internal static float MouthWideKeep = 0.35f;
        private static float drawMouthShape = 1f;   // the pawn being drawn's mouth shape's (Layer, from its first mouth layer)
        private static bool mouthShaped;
        private static readonly Dictionary<Texture, float> shapeWidths = new Dictionary<Texture, float>();

        internal static float MouthShapeScale(Graphic g)
        {
#if AUTOTEST
            if (TestNoMouthFit)
            {
                return 1f;
            }
#endif
            if (!RaceMouthInk || g == null || g.MatSouth == null)
            {
                return 1f;
            }
            Texture2D tex = g.MatSouth.mainTexture as Texture2D;
            if (tex == null)
            {
                return 1f;
            }
            float own;
            if (!shapeWidths.TryGetValue(tex, out own))
            {
                own = WidthOf(tex);
                shapeWidths[tex] = own;
            }
            float drawn = own * drawMouthScale;
            float from = MouthWideFrom * HumanMouthWidthNow();
            if (drawn <= from || drawn <= 0f)
            {
                return 1f;
            }
            return (from + (drawn - from) * MouthWideKeep) / drawn;
        }

        // A mouth texture's width (texture widths): its columns with alpha MouthWidthAlpha or more, read at 256. 0 if none.
        private static float WidthOf(Texture2D tex)
        {
            const int Read = 256;
            Color32[] px;
            try
            {
                px = Api.ReadSpritePixels(tex, Read);
            }
            catch (Exception)
            {
                px = null;
            }
            if (px == null || px.Length < Read * Read)
            {
                return 0f;
            }
            int c0 = Read, c1 = -1;
            for (int i = 0; i < Read * Read; i++)
            {
                if (px[i].a >= MouthWidthAlpha)
                {
                    int c = i % Read;
                    c0 = Math.Min(c0, c);
                    c1 = Math.Max(c1, c);
                }
            }
            return c1 >= c0 ? (c1 - c0 + 1f) / Read : 0f;
        }

        // The median width (texture widths) of every south mouth shape in a face set's Mouth folder, as drawn (the last
        // loaded mod's at each path), each by its columns with alpha MouthWidthAlpha or more. 0 if it has none.
        private static float MouthWidthOf(string root)
        {
            float width;
            if (mouthWidths.TryGetValue(root, out width))
            {
                return width;
            }
            Dictionary<string, Texture2D> found = new Dictionary<string, Texture2D>();
            string mouths = root + "Mouth/";
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                ModContentHolder<Texture2D> holder = mod.GetContentHolder<Texture2D>();
                if (holder == null)
                {
                    continue;
                }
                foreach (KeyValuePair<string, Texture2D> t in holder.contentList)
                {
                    if (t.Value != null && t.Key.StartsWith(mouths, StringComparison.Ordinal) && t.Key.EndsWith("_south", StringComparison.Ordinal))
                    {
                        found[t.Key] = t.Value;
                    }
                }
            }
            List<float> widths = new List<float>();
            foreach (Texture2D tex in found.Values)
            {
                float w = WidthOf(tex);
                if (w > 0f)
                {
                    widths.Add(w);
                }
            }
            widths.Sort();
            width = widths.Count > 0 ? widths[widths.Count / 2] : 0f;
            mouthWidths[root] = width;
            Log.Message(Tag + "the mouths of the face set in " + root + ": " + (widths.Count > 0 ? width.ToString("F4") + " of the face texture wide, the median of " + widths.Count + " shapes." : "none found."));
            return width;
        }
    }
}
