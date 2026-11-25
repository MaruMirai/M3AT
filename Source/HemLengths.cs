using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Skirts and dresses come in lengths (./make_skirts.sh: SkirtMini .. SkirtLong and DressMini .. DressLong, Maru's skirts
    // with the dress top joined on). Whichever of a kind the stand-in scoring picks (by its hints: a skirt or a dress), the
    // one drawn is the length nearest the apparel's own art: how far its front (south) sprite comes down the pawn's own
    // body sprite, from the body's top (0) to the bottom of its feet (1). Measured that way the bodies of the game and of
    // the race mods (Kiiro, Milira, Wolfein) agree: the crotch at about 0.62, the knee about 0.81, so it goes for any
    // art, the game's legless-looking bodies included. Each garment's hem is in the same measure, its hem_share line
    // (export_frames.py's hem_share, from the chibi's crotch, knee and ankle at those shares), so a length added is one
    // more garment named Skirt... or Dress... with that line. Ratkin's body picture has no legs (LeglessBodies). A pawn
    // first drawn before its body picture is made has its worn garments worked out again on the next draws
    // (WalkFigures.WornGarments' hemTries). A player's choice in the clothing tool is drawn as chosen; art that can't be
    // read keeps the scoring's.
    public sealed partial class SkinnedFigure
    {
        // The kinds of garment that come in lengths, by the start of their names (each with a hem_share line).
        private static readonly string[] HemKinds = { "Skirt", "Dress", "SleevelessDress" };
        private const int DressKind = 1, SleevelessKind = 2;
        // A dress is drawn sleeveless when its apparel doesn't cover the arms (long sleeves say Arms) and its art doesn't
        // stand out past the body's at the shoulders by this share of the body's height (the top SleeveBand of it): the
        // race mods' sleeved dresses and shirts 0.04 and up, their sleeveless ones 0.016 at most; the race bodies have no
        // arms drawn, so a sleeve is what stands out there. Art that doesn't reach the shoulders (strapless) is sleeveless.
        private const float SleevelessBelow = 0.025f;
        // Art that follows the body's outline (each edge within HugSlack pixels) up from its bottom for more than HugRun
        // of the body's height has legwear drawn in (Milira's bodycon dress over stockings to the feet: 57 rows of 76;
        // every other race dress looked at, 7 at most), and its hem can't be told from its outline, so the length the
        // scoring picked is kept (the sleeves still go by the art).
        private const float HugRun = 0.12f;
        private const int HugSlack = 1;
        private const float SleeveBand = 0.3f;
        private const int HemSize = 128;          // sprites read at this many pixels a side
        private const float HemLeast = 0.3f;      // a measure outside these is taken for art that isn't a skirt's or a dress's
        private const float HemMost = 1.4f;
        // Taken off every measured share before matching: the sprites draw very short legs, so a hem at mid-thigh in the
        // art reads as covering most of the leg, and matched by the legs' landmarks alone each skirt and dress came out a
        // length longer than it looks (Kiiro's plaid mini skirt as the knee skirt, its maid dress as the midi).
        private const float HemShorter = 0.09f;
        // Body pictures with no legs (Ratkin's is a tube, its dresses bells drawn over nothing), by the start of their
        // path: their feet are taken this share of their height further down, where the race's long dresses end. Nothing
        // in the pixels tells them apart (the races' legs are drawn together down to the feet, and the game's male body
        // is as wide at its bottom as Ratkin's tube).
        private static readonly string[] LeglessBodies = { "Things/Ratkin/Body/" };
        private const float LeglessFeet = 0.16f;

        // Set when a length couldn't be read for want of a picture not made yet, for WornGarments to try again.
        internal static bool HemUnread;

        private int[][] hemPieces;   // by HemKinds, the garments of that kind with a hem_share

        // (body texture, apparel texture) -> the share, or NaN for one that can't be read
        private static readonly Dictionary<(Texture, Texture), float> hemShares = new Dictionary<(Texture, Texture), float>();
        // a texture -> its opaque rows (top and bottom, from the top, as shares of its height), or null
        private static readonly Dictionary<Texture, float[]> spriteRows = new Dictionary<Texture, float[]>();

        // The garments drawn for apparel with no garment of its own on this pawn: StandInsFor's, a skirt or dress among
        // them the length its art comes to on this pawn's body, unless the player chose them.
        public int[] StandInsOn(Apparel apparel, Pawn pawn)
        {
            ThingDef def = apparel?.def;
            int[] chosen;
            if (def == null || def.apparel == null || WalkFigures.ChosenStandIns(this, def, out chosen))
            {
                return StandInsFor(def);
            }
            return AutoStandInsOn(apparel, pawn);
        }

        // The same as the scoring and the art would have it, whatever the player chose (the clothing tool's Automatic).
        public int[] AutoStandInsOn(Apparel apparel, Pawn pawn)
        {
            ThingDef def = apparel?.def;
            if (def == null || def.apparel == null)
            {
                return NoStandIns;
            }
            int[] found = AutoStandInsFor(def);
            int[][] kinds = HemPieces();
            int[] fitted = null;
            for (int k = 0; k < found.Length; k++)
            {
                for (int kind = 0; kind < kinds.Length; kind++)
                {
                    if (Array.IndexOf(kinds[kind], found[k]) < 0)
                    {
                        continue;
                    }
                    float share = HemShareOn(apparel, pawn);
                    if (!float.IsNaN(share) && LegwearDrawnIn(apparel, pawn))
                    {
                        share = pieces[found[k]].hemShare + HemShorter;   // (the scoring's length, as unreadable)
                    }
                    int[] among = kinds[kind];
                    if (kind == DressKind || kind == SleevelessKind)
                    {
                        // Sleeved or not by the apparel and its art (SleeveOverhangOn); kept as picked if unreadable.
                        bool arms = def.apparel.bodyPartGroups != null && def.apparel.bodyPartGroups.Exists(g_ => g_.defName == "Arms");
                        float over = arms ? float.NaN : SleeveOverhangOn(apparel, pawn);
                        int want = arms ? DressKind : float.IsNaN(over) ? kind : over < SleevelessBelow ? SleevelessKind : DressKind;
                        if (kinds[want].Length > 0)
                        {
                            among = kinds[want];
                        }
                    }
                    int g = float.IsNaN(share) ? -1 : NearestLength(among, share - HemShorter);
                    if (g >= 0 && g != found[k])
                    {
                        fitted = fitted ?? (int[])found.Clone();
                        fitted[k] = g;
                    }
                }
            }
            return fitted ?? found;
        }

        private int[][] HemPieces()
        {
            if (hemPieces == null)
            {
                hemPieces = new int[HemKinds.Length][];
                for (int kind = 0; kind < HemKinds.Length; kind++)
                {
                    List<int> found = new List<int>();
                    for (int g = 1; g < pieces.Length; g++)
                    {
                        if (pieces[g].hemShare >= 0f && pieces[g].name.StartsWith(HemKinds[kind], StringComparison.Ordinal))
                        {
                            found.Add(g);
                        }
                    }
                    hemPieces[kind] = found.ToArray();
                }
            }
            return hemPieces;
        }

        // The garment of a kind whose hem is nearest the share, or -1.
        private int NearestLength(int[] kind, float share)
        {
            int best = -1;
            float bestGap = float.MaxValue;
            for (int i = 0; i < kind.Length; i++)
            {
                float gap = Mathf.Abs(pieces[kind[i]].hemShare - share);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = kind[i];
                }
            }
            return best;
        }

        // How far down the pawn's body sprite the apparel's front sprite comes, from the body's top (0) to its feet (1), as
        // the game draws the two on this pawn (its race's body and the apparel's art for that body); NaN if unreadable.
        public static float HemShareOn(Apparel apparel, Pawn pawn)
        {
            Texture body, worn;
            bool legless;
            Pictures(apparel, pawn, out body, out worn, out legless);
            if (body == null || worn == null)
            {
                HemUnread = HemUnread || body == null;
                return float.NaN;
            }
            float share;
            if (!hemShares.TryGetValue((body, worn), out share))
            {
                float[] b = RowsOf(body), a = RowsOf(worn);
                float feet = b == null ? 0f : legless ? b[1] + LeglessFeet * (b[1] - b[0]) : b[1];
                share = b == null || a == null || b[1] - b[0] < 0.1f ? float.NaN : (a[1] - b[0]) / (feet - b[0]);
                if (share < HemLeast || share > HemMost)
                {
                    share = float.NaN;
                }
                hemShares[(body, worn)] = share;
            }
            return share;
        }

        private static readonly Dictionary<(Texture, Texture), bool> legwear = new Dictionary<(Texture, Texture), bool>();

        // Whether the apparel's front sprite follows the pawn's body sprite's outline up from its bottom for more than
        // HugRun of the body's height: legwear drawn in with it.
        public static bool LegwearDrawnIn(Apparel apparel, Pawn pawn)
        {
            Texture body, worn;
            bool legless;
            Pictures(apparel, pawn, out body, out worn, out legless);
            if (body == null || worn == null)
            {
                return false;
            }
            bool drawn;
            if (!legwear.TryGetValue((body, worn), out drawn))
            {
                bool[] b = AlphaOf(body), a = AlphaOf(worn);
                float[] rb = RowsOf(body), ra = RowsOf(worn);
                drawn = false;
                if (a != null && b != null && rb != null && ra != null)
                {
                    int top = Mathf.RoundToInt(rb[0] * HemSize), bottom = Mathf.RoundToInt(rb[1] * HemSize);
                    int r = Mathf.RoundToInt(ra[1] * HemSize), run = 0;
                    for (; r > top; r--, run++)
                    {
                        int j = HemSize - 1 - r;   // (rows bottom up)
                        int al, ar, bl, br;
                        if (!Extent(a, j, out al, out ar) || !Extent(b, j, out bl, out br)
                            || Math.Abs(al - bl) > HugSlack || Math.Abs(ar - br) > HugSlack)
                        {
                            break;
                        }
                    }
                    drawn = bottom > top && run >= HugRun * (bottom - top);
                }
                legwear[(body, worn)] = drawn;
            }
            return drawn;
        }

        private static readonly Dictionary<(Texture, Texture), float> sleeveOverhangs = new Dictionary<(Texture, Texture), float>();

        // How far the apparel's front sprite stands out past the pawn's body sprite at the shoulders (the body's top
        // SleeveBand), the most either side in any row, as a share of the body's height; 0 for art not reaching the
        // shoulders, NaN unreadable.
        public static float SleeveOverhangOn(Apparel apparel, Pawn pawn)
        {
            Texture body, worn;
            bool legless;
            Pictures(apparel, pawn, out body, out worn, out legless);
            if (body == null || worn == null)
            {
                return float.NaN;
            }
            float over;
            if (!sleeveOverhangs.TryGetValue((body, worn), out over))
            {
                bool[] b = AlphaOf(body), a = AlphaOf(worn);
                float[] rows = RowsOf(body);
                over = float.NaN;
                if (a != null && b != null && rows != null)
                {
                    int top = Mathf.RoundToInt(rows[0] * HemSize), bottom = Mathf.RoundToInt(rows[1] * HemSize);
                    int most = 0;
                    for (int r = top; r < top + (int)(SleeveBand * (bottom - top)); r++)
                    {
                        int j = HemSize - 1 - r;   // (rows bottom up)
                        int al, ar, bl, br;
                        if (Extent(a, j, out al, out ar) && Extent(b, j, out bl, out br))
                        {
                            most = Math.Max(most, Math.Max(bl - al, ar - br));
                        }
                    }
                    over = bottom > top ? most / (float)(bottom - top) : float.NaN;
                }
                sleeveOverhangs[(body, worn)] = over;
            }
            return over;
        }

        private static bool Extent(bool[] alpha, int j, out int left, out int right)
        {
            left = right = -1;
            for (int i = 0; i < HemSize; i++)
            {
                if (alpha[j * HemSize + i])
                {
                    if (left < 0)
                    {
                        left = i;
                    }
                    right = i;
                }
            }
            return left >= 0;
        }

        // The pictures the game draws on this pawn: its body (its race's under Humanoid Alien Races) and the apparel's for
        // that body, and whether the body is one with no legs drawn.
        private static void Pictures(Apparel apparel, Pawn pawn, out Texture body, out Texture worn, out bool legless)
        {
            body = null;
            worn = null;
            legless = false;
            try
            {
                Graphic bodyGraphic = pawn?.Drawer?.renderer?.BodyGraphic;
                body = bodyGraphic?.MatSouth?.mainTexture;
                legless = bodyGraphic?.path != null && Array.Exists(LeglessBodies, b => bodyGraphic.path.StartsWith(b, StringComparison.OrdinalIgnoreCase));
                ApparelGraphicRecord rec;
                if (pawn?.story?.bodyType != null && ApparelGraphicRecordGetter.TryGetGraphicApparel(apparel, pawn.story.bodyType, false, out rec))
                {
                    worn = rec.graphic?.MatSouth?.mainTexture;
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce(Tag + "finding " + apparel.def.defName + "'s sprite on " + pawn + " failed: " + e, 61530121);
            }
        }

        // What HemShareOn reads, for the autotest: each texture's name and its opaque rows.
        internal static string HemDiagnosis(Apparel apparel, Pawn pawn)
        {
            Texture body = pawn?.Drawer?.renderer?.BodyGraphic?.MatSouth?.mainTexture;
            Texture worn = null;
            ApparelGraphicRecord rec;
            if (pawn?.story?.bodyType != null && ApparelGraphicRecordGetter.TryGetGraphicApparel(apparel, pawn.story.bodyType, false, out rec))
            {
                worn = rec.graphic?.MatSouth?.mainTexture;
            }
            Func<Texture, string> rows = t => t == null ? "none" : t.name + " " + (RowsOf(t) == null ? "unread"
                : RowsOf(t)[0].ToString("0.00") + ".." + RowsOf(t)[1].ToString("0.00"));
            return "body " + rows(body) + ", apparel " + rows(worn);
        }

        // A texture's top and bottom opaque rows, from the top, as shares of its height; null if it can't be read.
        private static readonly Dictionary<Texture, bool[]> spriteAlpha = new Dictionary<Texture, bool[]>();

        // A texture's opaque pixels at HemSize (rows bottom up), or null if it can't be read.
        private static bool[] AlphaOf(Texture tex)
        {
            bool[] alpha;
            if (spriteAlpha.TryGetValue(tex, out alpha))
            {
                return alpha;
            }
            alpha = null;
            try
            {
                Color32[] px = Api.ReadSpritePixels(tex, HemSize);
                if (px != null && px.Length >= HemSize * HemSize)
                {
                    alpha = new bool[HemSize * HemSize];
                    for (int i = 0; i < alpha.Length; i++)
                    {
                        alpha[i] = px[i].a > 128;
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce(Tag + "reading " + tex.name + " failed: " + e, 61530122);
            }
            spriteAlpha[tex] = alpha;
            return alpha;
        }

        private static float[] RowsOf(Texture tex)
        {
            float[] rows;
            if (spriteRows.TryGetValue(tex, out rows))
            {
                return rows;
            }
            rows = null;
            {
                bool[] px = AlphaOf(tex);   // rows bottom up
                if (px != null)
                {
                    int low = -1, high = -1;
                    for (int j = 0; j < HemSize; j++)
                    {
                        for (int i = 0; i < HemSize; i++)
                        {
                            if (px[j * HemSize + i])
                            {
                                if (low < 0)
                                {
                                    low = j;
                                }
                                high = j;
                                break;
                            }
                        }
                    }
                    if (low >= 0)
                    {
                        rows = new[] { (HemSize - 1 - high) / (float)HemSize, (HemSize - 1 - low) / (float)HemSize };
                    }
                }
            }
            spriteRows[tex] = rows;
            return rows;
        }
    }
}
