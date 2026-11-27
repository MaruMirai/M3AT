using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A garment standing in for another mod's apparel (a race's maid dress drawn as the Dress, a plaid skirt as the Skirt)
    // is painted with that apparel's own sprites, as the hair is built from its own (the spriteClothes option, on by
    // default, WalkFigures.SpriteClothes). The garment keeps its mesh and its UVs (anim_scripts/paint_cloth.py gave the
    // plain cloth garments theirs): each texel of its image is placed on the garment at rest, in the base shape, and
    // coloured from the sprites that face it there (the front for what faces forward, the back, and the side for each
    // side), blended by how far its way out turns to each, the side the art is on counting more where one view has art and
    // another doesn't. The sprites are laid on the chibi by the body: the pawn's own body sprite's rows, from its top (0)
    // to its feet (1), on the chibi's heights by the same landmarks as the skirts' hem lengths (HemLengths.cs,
    // export_frames.py's HEM_ANCHORS: the crotch at 0.62 and the knee at 0.81 of the sprite body), and across each row
    // the body sprite's width on the chibi's trunk and legs' width at that height (its arms and head left out), so a skirt
    // flared past the legs takes the art flared past the sprite's legs. The art is read as the game draws it: the
    // apparel's pictures in its material's colour (and its mask's second colour), their outline ink taken out and their
    // colours grown out past their edge so a part of the garment past the art (a longer hem, a sleeve, as the race bodies
    // have no arms) takes the nearest. The garment's own image is left out: multiplied back in as detail (even blurred
    // to its folds), it printed our garment's design over the apparel's art and looked no better than the art alone.
    // One image per garment, apparel material and body picture, made at most one a frame (ClothBakesPerFrame); until made the garment is drawn as before. Not for hats and masks
    // (riding one bone: a head sprite is laid on the head, not the body), held shields, the shoes worn with legwear,
    // the baby romper or the pajamas, or a corpse's clothes (the game's rotten colour).
    public sealed partial class SkinnedFigure
    {
        private const int ClothSize = 512;            // each garment's painted image, pixels a side
        // The sprites are read at their own size (a power of two), within these: read at 128 like the race parts, a 512 px
        // race mod's dress, a quarter of its picture across, came out about 30 pixels wide over the whole garment.
        private const int ClothSpriteLeast = 128, ClothSpriteMost = 512;
        private const int ClothViewsKept = 48;        // sprites kept read at once (each a few MB at 512), then all let go
        private const int ClothBands = 120;           // the chibi's body width measured in this many bands of height, its trunk's and legs' all
        private const int ClothBandSmooth = 2;        // and each band's width averaged with this many either side
        private const float ClothEdgeSmooth = 1.5f;   // the body sprite's edges averaged over rows, a Gaussian this wide (rows at 128)
        private const int ClothBakesPerFrame = 1;
        private const int ClothMost = 600;            // images made at most this session (then the rest are drawn plain)
        private const float ClothArtWeight = 0.85f;   // how much more a view counts where it has art (by its alpha)
        private const float ClothInkReach = 2.5f, ClothInkDark = 0.55f;   // its outline ink: dark pixels this near its edge (as Bled), pixels at 128
        // The heights on the chibi (the rig's units over its lowest point) the sprite body's measure (HemLengths.cs: 0 its
        // top, 1 its feet) is pinned at: its top on the neck, its shoulder line and its waist (both measured from each body
        // sprite, BodyMarks; else ClothShoulder and ClothWaist) on the chibi's shoulders and the skirts' waist ring (Maru's
        // skirt masters', the dress's join, as the chibi's trunk has no waist of its own: it widens all the way from the
        // chest to the hips), then the crotch, knee and ankle as export_frames.py's HEM_ANCHORS has them.
        private const float RigTop = 20f, RigShoulder = 18.9f, RigWaist = 15f, RigCrotch = 9.2f, RigKneeMark = 6.5f;
        private const float ClothShoulder = 0.08f, ClothWaist = 0.42f, ClothCrotch = 0.62f, ClothKnee = 0.81f;
        private const float RigAnkle = 1.025f, RigKnee = 6.351f;   // the rig's foot and shin heads, which give the units
        private const float WaistNarrower = 0.93f;   // a body sprite's waist is where it is narrowest, if this much narrower than above and below it
        private const float ClothChinClear = 0.04f;   // the shoulder line is kept as a landmark only this far (a share of the body) below the chin
        private const float ClothChinMost = 0.35f;    // a chin further down the body than this is taken as wrongly read (a race's odd head), and nothing as covered
        private const float ClothLineReach = 1.25f;   // interior ink lines: dark lines up to about twice this wide (pixels at 128) taken out as the outline is

        // Each garment's texels as placed on it at rest: the image's pixel and its place and way out (model space, cells).
        private sealed class ClothTexels
        {
            public int[] at;
            public Vector3[] pos;
            public Vector3[] way;
        }

        // One painted image and its materials (a Piece only for TexturedMaterial's sake).
        public sealed class ClothImage
        {
            internal Piece holder;
            public Color mean;
        }

        private readonly Dictionary<int, ClothTexels> clothTexels = new Dictionary<int, ClothTexels>();
        private readonly Dictionary<(int, Material, Texture, Texture), ClothImage> clothImages = new Dictionary<(int, Material, Texture, Texture), ClothImage>();
        private float[] clothLowX, clothHighX, clothLowZ, clothHighZ;   // the chibi's trunk and legs, by band of height
        private float clothAnkle, clothScale;   // the ankle joint's height (cells) and cells per rig unit
        private float clothBandLow, clothBandHigh;   // the heights the bands span (cells)
        private static int clothFrame = -1, clothBakes;
        private static int clothMade;
        private static bool clothFull;
        // How the art is laid on (the autotest compares them): 0 the edges smooth, 1 the edges a whole pixel at a time (as
        // before 2026-11-27), 2 the edges smooth and each view upscaled along its edges (Upscaled).
        internal static int clothStyle;
        private static int clothEpoch;
        private int clothEpochSeen;
        private const int ClothFine = 512;            // a view upscaled to this many pixels a side (style 2)
        private const float ClothFineReach = 1.5f;    // and smoothed along its edges this far either way (sprite pixels)
        private const int ClothFinePasses = 2;

        // Every painted image and read view let go, to be made again in another style (autotest only).
        internal static void RestyleCloth(int style)
        {
            clothStyle = style;
            clothEpoch++;
            clothViews.Clear();
            clothMade = 0;
            clothFull = false;
        }

        // True if the garment can be painted with the art of apparel it stands in for: it has texture places and an image
        // of its own, and is worn on the body, not riding one bone (a hat), held, a pack, the shoes or the baby romper.
        public bool PaintsArt(int g, Apparel apparel)
        {
            return apparel != null && !apparel.WornByCorpse && PaintsArt(g, apparel.def);
        }

        // The same for apparel by its def (the clothing lists, worn by no one).
        public bool PaintsArt(int g, ThingDef def)
        {
            if (g <= 0 || g >= pieces.Length || def == null)
            {
                return false;
            }
            Piece p = pieces[g];
            if (p.uvs == null || p.texture == null || p.boneName != null || p.held || p.forBaby || p.colour.HasValue || p.withLegwear
                || p.genes != null || IsPack(def))
            {
                return false;
            }
            // Headwear's art is drawn on the head, not laid on the body (a hat not riding one bone, the ladies' hat).
            List<ApparelLayerDef> layers = def.apparel?.layers;
            if (layers == null || layers.Count == 0 || layers.TrueForAll(l => Array.IndexOf(HeadLayers, l.defName) >= 0))
            {
                return false;
            }
            // A stand-in, or a content pack's garment (its image made from the art can come out in the wrong colours: the
            // player may have it painted, WalkFigures.PaintedArtFor). Apparel with a garment of this mod's own keeps every
            // garment drawn for it as made, as the marine and cataphract armour's pauldrons (drawn for the armour,
            // ArmourPads) were painted with the armour's art.
            return GarmentOf(def) < 0 || p.owner != null;
        }

        // The image painted with the apparel's art for this garment on this pawn (null: not to be had, or not made yet).
        public ClothImage ClothImageOf(int g, Apparel apparel, Pawn pawn)
        {
            Material[] worn = new Material[4];
            Material[] body = new Material[4];
            try
            {
                Graphic bodyGraphic = pawn?.Drawer?.renderer?.BodyGraphic;
                ApparelGraphicRecord rec;
                if (bodyGraphic == null || pawn.story?.bodyType == null
                    || !ApparelGraphicRecordGetter.TryGetGraphicApparel(apparel, pawn.story.bodyType, false, out rec) || rec.graphic == null)
                {
                    return null;
                }
                for (int f = 0; f < 4; f++)
                {
                    worn[f] = rec.graphic.MatAt(new Rot4(f));
                    body[f] = bodyGraphic.MatAt(new Rot4(f));
                }
                if (worn[2] == null || body[2] == null || worn[2].mainTexture == null || body[2].mainTexture == null)
                {
                    return null;
                }
                // (By the head's picture too, as what it covers is left out.)
                Texture headArt = pawn.Drawer.renderer.HeadGraphic?.MatAt(Rot4.South)?.mainTexture;
                var key = (g, worn[2], body[2].mainTexture, headArt);
                ClothImage made;
                if (clothEpochSeen != clothEpoch)
                {
                    clothImages.Clear();
                    clothEpochSeen = clothEpoch;
                }
                if (clothImages.TryGetValue(key, out made))
                {
                    return made;
                }
                int frame = Time.frameCount;
                if (frame != clothFrame)
                {
                    clothFrame = frame;
                    clothBakes = 0;
                }
                if (clothBakes >= ClothBakesPerFrame || clothFull)
                {
                    return null;   // another frame
                }
                if (clothMade >= ClothMost)
                {
                    clothFull = true;
                    Log.Warning(Tag + "painted " + ClothMost + " garments with their apparel's art this session; the rest are drawn plain.");
                    return null;
                }
                clothBakes++;
                clothMade++;
                bool legless = bodyGraphic.path != null && Array.Exists(LeglessBodies, b => bodyGraphic.path.StartsWith(b, StringComparison.OrdinalIgnoreCase));
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                made = PaintCloth(g, worn, body, WalkFigures.HeadCoverOf(pawn), headArt, legless, apparel.def.defName);
                clothImages[key] = made;
                if (made != null && Prefs.DevMode)
                {
                    Log.Message(Tag + "painted " + pieces[g].name + " with " + apparel.def.defName + "'s art (" + worn[2].mainTexture.name + ") in "
                        + watch.ElapsedMilliseconds + " ms.");
                }
                return made;
            }
            catch (Exception e)
            {
                Log.WarningOnce(Tag + "painting " + pieces[g].name + " with " + apparel.def.defName + "'s art failed, it is drawn plain: " + e, 61530140 + g);
                return null;
            }
        }

        // The chibi's trunk and legs' widths by band of height (once): each band's cut through their surface (the body's
        // triangles none of whose corners the head or an arm moves most), averaged over ClothBandSmooth bands either side,
        // as the body is low-poly and measured by its vertices a band held only a few near its middle, which stretched the
        // art across it in streaks.
        private void ClothBody()
        {
            if (clothLowX != null)
            {
                return;
            }
            int foot, shin;
            if (!boneIndex.TryGetValue("foot.L", out foot) || !boneIndex.TryGetValue("shin.L", out shin))
            {
                throw new InvalidOperationException("no foot.L or shin.L bone");
            }
            clothAnkle = boneHead[foot].y;
            clothScale = (boneHead[shin].y - boneHead[foot].y) / (RigKnee - RigAnkle);
            bool[] head = OnHead(), arm = OnArm();
            Piece body = pieces[0];
            List<int> kept = new List<int>();
            float low = float.MaxValue, high = float.MinValue;
            for (int k = 0; k < body.tris.Length; k += 3)
            {
                int a = body.tris[k], b = body.tris[k + 1], c = body.tris[k + 2];
                if (head[a] || head[b] || head[c] || arm[a] || arm[b] || arm[c])
                {
                    continue;
                }
                kept.Add(k);
                low = Mathf.Min(low, Mathf.Min(body.rest[a].y, Mathf.Min(body.rest[b].y, body.rest[c].y)));
                high = Mathf.Max(high, Mathf.Max(body.rest[a].y, Mathf.Max(body.rest[b].y, body.rest[c].y)));
            }
            clothBandLow = low;
            clothBandHigh = high;
            float[] lx = new float[ClothBands], hx = new float[ClothBands], lz = new float[ClothBands], hz = new float[ClothBands];
            for (int b = 0; b < ClothBands; b++)
            {
                lx[b] = lz[b] = float.MaxValue;
                hx[b] = hz[b] = float.MinValue;
                float y = low + (b + 0.5f) / ClothBands * (high - low);
                foreach (int k in kept)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        Vector3 p = body.rest[body.tris[k + e]], q = body.rest[body.tris[k + (e + 1) % 3]];
                        if ((p.y - y) * (q.y - y) > 0f || p.y == q.y)
                        {
                            continue;
                        }
                        Vector3 at = p + (q - p) * ((y - p.y) / (q.y - p.y));
                        lx[b] = Mathf.Min(lx[b], at.x);
                        hx[b] = Mathf.Max(hx[b], at.x);
                        lz[b] = Mathf.Min(lz[b], at.z);
                        hz[b] = Mathf.Max(hz[b], at.z);
                    }
                }
            }
            FillBands(lx, hx);
            FillBands(lz, hz);
            clothLowX = Smoothed(lx);
            clothHighX = Smoothed(hx);
            clothLowZ = Smoothed(lz);
            clothHighZ = Smoothed(hz);
        }

        private static float[] Smoothed(float[] a)
        {
            float[] o = new float[a.Length];
            for (int b = 0; b < a.Length; b++)
            {
                float sum = 0f;
                for (int d = -ClothBandSmooth; d <= ClothBandSmooth; d++)
                {
                    sum += a[Mathf.Clamp(b + d, 0, a.Length - 1)];
                }
                o[b] = sum / (2 * ClothBandSmooth + 1);
            }
            return o;
        }

        // Bands with no vertex take the nearest that has one.
        private static void FillBands(float[] low, float[] high)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                int from = pass == 0 ? 0 : low.Length - 1, step = pass == 0 ? 1 : -1;
                int last = -1;
                for (int b = from; b >= 0 && b < low.Length; b += step)
                {
                    if (high[b] >= low[b])
                    {
                        last = b;
                    }
                    else if (last >= 0 && high[last] >= low[last])
                    {
                        low[b] = low[last];
                        high[b] = high[last];
                    }
                }
            }
        }

        // A height (model space, cells) in the sprite body's measure, 0 its top and 1 its feet, by the landmarks (RigTop ..):
        // the body sprite's own shoulder line and waist (the view's), straight between them and past either end. Where the
        // game's head covers the body's top (ClothView.chin), the first row it shows is what meets the chibi's neck, as the
        // collar it shows is under the chin: the art above it (a neck hole) would otherwise come halfway down the chest,
        // as the chibi's head covers so much less of its trunk. With the shoulder line covered too (the head is as wide as
        // the thin body's shoulders), the rows go straight from the chin to the waist.
        private float ClothShare(float y, ClothView v)
        {
            float h = RigAnkle + (y - clothAnkle) / clothScale;
            bool shoulderShown = v.chin < v.shoulder - ClothChinClear;
            float[] rig = shoulderShown ? new[] { RigTop, RigShoulder, RigWaist, RigCrotch, RigKneeMark, RigAnkle }
                : new[] { RigTop, RigWaist, RigCrotch, RigKneeMark, RigAnkle };
            float[] share = shoulderShown ? new[] { v.chin, v.shoulder, v.waist, ClothCrotch, ClothKnee, 1f }
                : new[] { v.chin, v.waist, ClothCrotch, ClothKnee, 1f };
            int i = 0;
            while (i < rig.Length - 2 && h < rig[i + 1])
            {
                i++;
            }
            return share[i] + (h - rig[i]) * (share[i + 1] - share[i]) / (rig[i + 1] - rig[i]);
        }

        // The chibi's trunk and legs' extent at a height, read between the bands' middles (taken a band at a time, each
        // band's step in width moved its strip of art sideways).
        private void ClothWidthAt(float y, out float xl, out float xh, out float zl, out float zh)
        {
            float b = Mathf.Clamp((y - clothBandLow) / (clothBandHigh - clothBandLow) * ClothBands - 0.5f, 0f, ClothBands - 1.001f);
            int i = (int)b;
            float t = b - i;
            if (clothStyle == 1)
            {
                i = Mathf.Clamp(Mathf.FloorToInt((y - clothBandLow) / (clothBandHigh - clothBandLow) * ClothBands), 0, ClothBands - 1);
                xl = clothLowX[i];
                xh = clothHighX[i];
                zl = clothLowZ[i];
                zh = clothHighZ[i];
                return;
            }
            xl = Mathf.Lerp(clothLowX[i], clothLowX[i + 1], t);
            xh = Mathf.Lerp(clothHighX[i], clothHighX[i + 1], t);
            zl = Mathf.Lerp(clothLowZ[i], clothLowZ[i + 1], t);
            zh = Mathf.Lerp(clothHighZ[i], clothHighZ[i + 1], t);
        }

        // The garment's texels, placed on it at rest (once per garment): each triangle drawn into its image by its texture
        // places, each pixel inside it its place and smooth way out there. The way out is turned to point away from the
        // garment's middle, whichever way its file winds it.
        private ClothTexels TexelsOf(int g)
        {
            ClothTexels t;
            if (clothTexels.TryGetValue(g, out t))
            {
                return t;
            }
            Piece p = pieces[g];
            int n = p.rest.Length;
            // Smooth ways out, welded by place across texture seams.
            Vector3[] way = new Vector3[n];
            Dictionary<Vector3Int, Vector3> byPlace = new Dictionary<Vector3Int, Vector3>();
            Func<Vector3, Vector3Int> placeKey = v => new Vector3Int(Mathf.RoundToInt(v.x * 20000f), Mathf.RoundToInt(v.y * 20000f), Mathf.RoundToInt(v.z * 20000f));
            Vector3 low = Vector3.positiveInfinity, high = Vector3.negativeInfinity;
            foreach (Vector3 v in p.rest)
            {
                low = Vector3.Min(low, v);
                high = Vector3.Max(high, v);
            }
            Vector3 middle = (low + high) * 0.5f;
            double outward = 0;
            for (int k = 0; k < p.tris.Length; k += 3)
            {
                Vector3 a = p.rest[p.tris[k]], b = p.rest[p.tris[k + 1]], c = p.rest[p.tris[k + 2]];
                Vector3 f = Vector3.Cross(b - a, c - a);
                Vector3 mid = (a + b + c) / 3f;
                outward += Vector3.Dot(f, new Vector3(mid.x - middle.x, 0f, mid.z - middle.z));
                for (int j = 0; j < 3; j++)
                {
                    Vector3Int key = placeKey(p.rest[p.tris[k + j]]);
                    Vector3 sum;
                    byPlace.TryGetValue(key, out sum);
                    byPlace[key] = sum + f;
                }
            }
            float sign = outward >= 0 ? 1f : -1f;
            for (int i = 0; i < n; i++)
            {
                Vector3 sum;
                byPlace.TryGetValue(placeKey(p.rest[i]), out sum);
                way[i] = (sum * sign).normalized;
            }
            List<int> at = new List<int>();
            List<Vector3> pos = new List<Vector3>(), ways = new List<Vector3>();
            bool[] done = new bool[ClothSize * ClothSize];
            for (int k = 0; k < p.tris.Length; k += 3)
            {
                int ia = p.tris[k], ib = p.tris[k + 1], ic = p.tris[k + 2];
                Vector2 ua = p.uvs[ia] * ClothSize, ub = p.uvs[ib] * ClothSize, uc = p.uvs[ic] * ClothSize;
                float det = (ub.x - ua.x) * (uc.y - ua.y) - (uc.x - ua.x) * (ub.y - ua.y);
                if (Mathf.Abs(det) < 1e-6f)
                {
                    continue;
                }
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ua.x, Mathf.Min(ub.x, uc.x))) - 1);
                int x1 = Mathf.Min(ClothSize - 1, Mathf.CeilToInt(Mathf.Max(ua.x, Mathf.Max(ub.x, uc.x))) + 1);
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ua.y, Mathf.Min(ub.y, uc.y))) - 1);
                int y1 = Mathf.Min(ClothSize - 1, Mathf.CeilToInt(Mathf.Max(ua.y, Mathf.Max(ub.y, uc.y))) + 1);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = y * ClothSize + x;
                        if (done[i])
                        {
                            continue;
                        }
                        float px = x + 0.5f, py = y + 0.5f;
                        float wb = ((px - ua.x) * (uc.y - ua.y) - (uc.x - ua.x) * (py - ua.y)) / det;
                        float wc = ((ub.x - ua.x) * (py - ua.y) - (px - ua.x) * (ub.y - ua.y)) / det;
                        float wa = 1f - wb - wc;
                        const float Edge = -0.01f;
                        if (wa < Edge || wb < Edge || wc < Edge)
                        {
                            continue;
                        }
                        done[i] = true;
                        at.Add(i);
                        pos.Add(p.rest[ia] * wa + p.rest[ib] * wb + p.rest[ic] * wc);
                        ways.Add((way[ia] * wa + way[ib] * wb + way[ic] * wc).normalized);
                    }
                }
            }
            t = new ClothTexels { at = at.ToArray(), pos = pos.ToArray(), way = ways.ToArray() };
            clothTexels[g] = t;
            return t;
        }

        // One facing's sprite as read for painting: its colours grown out over the whole picture, its alpha, and the
        // body sprite's rows and widths it is laid on the chibi by.
        private sealed class ClothView
        {
            public Color32[] colour;   // grown, opaque (rows bottom up)
            public byte[] alpha;       // the art's own
            public int size;           // pixels a side
            public float top, feet;    // the body's top and feet, pixels from the picture's top
            public float shoulder = ClothShoulder, waist = ClothWaist;   // and its shoulder line and waist, shares of the way down it (BodyMarks)
            public float chin;         // and how far down it the head covers its middle as the game draws it (0: not covered, or not told)
            public int[] left, right;  // the body's opaque columns in each row from the top (filled from the nearest row with any)
            public float[] edgeLow, edgeHigh;   // and its edges smooth (EdgesOf): where its left starts and its right ends, pixels
            public Color32[] fine;     // colour upscaled along its edges (Upscaled), fineScale times size a side, or null
            public int fineScale = 1;
        }

        private ClothImage PaintCloth(int g, Material[] worn, Material[] body, WalkFigures.HeadCover head, Texture headKey, bool legless, string defName)
        {
            ClothBody();
            ClothTexels t = TexelsOf(g);
            ClothView[] views = new ClothView[4];
            for (int f = 0; f < 4; f++)
            {
                // The west picture is the east one, drawn mirrored, unless it has its own (drawn facing east all the same).
                if (f == 3 && worn[3] != null && worn[1] != null && worn[3].mainTexture == worn[1].mainTexture && views[1] != null)
                {
                    views[3] = views[1];
                    continue;
                }
                views[f] = ViewOf(worn[f], body[f], head, f, headKey, legless);
            }
            if (views[2] == null)
            {
                return null;
            }
            // Every view laid on by the front body sprite's landmarks, which the side's outline doesn't show as well.
            for (int f = 0; f < 4; f++)
            {
                if (views[f] != null)
                {
                    views[f].shoulder = views[2].shoulder;
                    views[f].waist = views[2].waist;
                    views[f].chin = views[2].chin;
                }
            }
            Color32[] img = new Color32[ClothSize * ClothSize];
            bool[] have = new bool[img.Length];
            for (int k = 0; k < t.at.Length; k++)
            {
                Vector3 p = t.pos[k], n = t.way[k];
                float xl, xh, zl, zh;
                ClothWidthAt(p.y, out xl, out xh, out zl, out zh);
                Vector4 sum = Vector4.zero;
                float weights = 0f;
                for (int f = 0; f < 4; f++)
                {
                    ClothView v = views[f];
                    if (v == null)
                    {
                        continue;
                    }
                    float facing;
                    float across;
                    switch (f)
                    {
                        case 2: facing = n.z; across = (xh - p.x) / Mathf.Max(1e-4f, xh - xl); break;    // south: the front; the picture's right is the figure's left
                        case 0: facing = -n.z; across = (p.x - xl) / Mathf.Max(1e-4f, xh - xl); break;   // north: from behind
                        case 1: facing = n.x; across = (p.z - zl) / Mathf.Max(1e-4f, zh - zl); break;    // east: the figure's right, facing the picture's right
                        default: facing = -n.x; across = (p.z - zl) / Mathf.Max(1e-4f, zh - zl); break;  // west: its left, drawn facing east too
                    }
                    if (facing <= 0f)
                    {
                        continue;
                    }
                    float row = v.top + ClothShare(p.y, v) * (v.feet - v.top);   // pixels from the top
                    float col = ColumnAt(v, row, across);
                    Color32 c;
                    byte a;
                    Sample(v, col, v.size - row, out c, out a);
                    float w = facing * facing * (1f - ClothArtWeight + ClothArtWeight * a / 255f);
                    sum += new Vector4(c.r, c.g, c.b, 0f) * w;
                    weights += w;
                }
                if (weights <= 0f)
                {
                    // Facing straight up or down: the front's.
                    ClothView v = views[2];
                    float row = v.top + ClothShare(p.y, v) * (v.feet - v.top);
                    float col = ColumnAt(v, row, (xh - p.x) / Mathf.Max(1e-4f, xh - xl));
                    Color32 c;
                    byte a;
                    Sample(v, col, v.size - row, out c, out a);
                    sum = new Vector4(c.r, c.g, c.b, 0f);
                    weights = 1f;
                }
                float d = 1f / weights;
                img[t.at[k]] = new Color32((byte)Mathf.Clamp(sum.x * d, 0f, 255f), (byte)Mathf.Clamp(sum.y * d, 0f, 255f), (byte)Mathf.Clamp(sum.z * d, 0f, 255f), 255);
                have[t.at[k]] = true;
            }
            Vector4 mean = Grown(img, have, ClothSize, 4);
            Texture2D tex = new Texture2D(ClothSize, ClothSize, TextureFormat.RGBA32, true);
            tex.SetPixels32(img);
            tex.Apply(true);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.name = "Marus_Garment_" + pieces[g].name + "_" + defName;
            Piece holder = new Piece { name = tex.name, texture = tex };
#if AUTOTEST
            DumpCloth(tex, views, defName, pieces[g].name, worn);
#endif
            return new ClothImage { holder = holder, mean = new Color(mean.x / 255f, mean.y / 255f, mean.z / 255f, 1f) };
        }

        // A sprite's pixel at (col, row from the bottom) on its grown colours, and its own alpha there. Catmull-Rom over
        // the 4 by 4 pixels round it rather than bilinear, as the art is magnified onto the garment and bilinear smeared
        // its lines; held within the colours round it, so it doesn't ring past them.
        private static void Sample(ClothView v, float col, float rowUp, out Color32 c, out byte a)
        {
            int s0 = v.size;
            a = v.alpha[Mathf.Clamp((int)rowUp, 0, s0 - 1) * s0 + Mathf.Clamp((int)col, 0, s0 - 1)];
            Color32[] colour = v.fine ?? v.colour;
            int k = v.fine != null ? v.fineScale : 1;
            c = CatmullRomAt(colour, s0 * k, col * k, rowUp * k);
        }

        // A picture's colour at (x, y) in pixels (rows bottom up), Catmull-Rom held within the four pixels round it.
        private static Color32 CatmullRomAt(Color32[] colour, int s, float px, float py)
        {
            float x = Mathf.Clamp(px - 0.5f, 0f, s - 1.001f), y = Mathf.Clamp(py - 0.5f, 0f, s - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float wx0, wx1, wx2, wx3, wy0, wy1, wy2, wy3;
            CatmullRom(fx, out wx0, out wx1, out wx2, out wx3);
            CatmullRom(fy, out wy0, out wy1, out wy2, out wy3);
            float[] wx = { wx0, wx1, wx2, wx3 }, wy = { wy0, wy1, wy2, wy3 };
            float r = 0f, g = 0f, b = 0f;
            Color32 lo = new Color32(255, 255, 255, 255), hi = new Color32(0, 0, 0, 255);
            for (int j = 0; j < 4; j++)
            {
                int yy = Mathf.Clamp(y0 + j - 1, 0, s - 1);
                for (int i = 0; i < 4; i++)
                {
                    int xx = Mathf.Clamp(x0 + i - 1, 0, s - 1);
                    Color32 p = colour[yy * s + xx];
                    float w = wx[i] * wy[j];
                    r += p.r * w;
                    g += p.g * w;
                    b += p.b * w;
                    if ((i == 1 || i == 2) && (j == 1 || j == 2))
                    {
                        lo = new Color32(Math.Min(lo.r, p.r), Math.Min(lo.g, p.g), Math.Min(lo.b, p.b), 255);
                        hi = new Color32(Math.Max(hi.r, p.r), Math.Max(hi.g, p.g), Math.Max(hi.b, p.b), 255);
                    }
                }
            }
            return new Color32((byte)Mathf.Clamp(r, lo.r, hi.r), (byte)Mathf.Clamp(g, lo.g, hi.g), (byte)Mathf.Clamp(b, lo.b, hi.b), 255);
        }

        // The view's colour upscaled to ClothFine a side and smoothed along its edges, not across them, so the art's pixel
        // steps (magnified some eight times onto the garment) come out as smooth lines: Catmull-Rom up, then each pixel
        // averaged with the pixels along the edge through it (ClothFineReach either way), the edge's way from the
        // smoothed structure tensor of the sprite's brightness, by how clearly there is one (none in flat colour or at
        // corners), ClothFinePasses times.
        private static void Upscaled(ClothView v)
        {
            int s = v.size, k = ClothFine / s;
            if (k < 2)
            {
                return;
            }
            int n = s * k;
            // The edge's way and how clear it is, at the sprite's pixels.
            float[] lum = new float[s * s];
            for (int i = 0; i < lum.Length; i++)
            {
                Color32 c = v.colour[i];
                lum[i] = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            }
            float[] jxx = new float[s * s], jxy = new float[s * s], jyy = new float[s * s];
            for (int y = 0; y < s; y++)
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(s - 1, y + 1);
                for (int x = 0; x < s; x++)
                {
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(s - 1, x + 1);
                    float gx = lum[y0 * s + x1] + 2f * lum[y * s + x1] + lum[y1 * s + x1] - lum[y0 * s + x0] - 2f * lum[y * s + x0] - lum[y1 * s + x0];
                    float gy = lum[y1 * s + x0] + 2f * lum[y1 * s + x] + lum[y1 * s + x1] - lum[y0 * s + x0] - 2f * lum[y0 * s + x] - lum[y0 * s + x1];
                    jxx[y * s + x] = gx * gx;
                    jxy[y * s + x] = gx * gy;
                    jyy[y * s + x] = gy * gy;
                }
            }
            jxx = Blurred(jxx, s, 1f);
            jxy = Blurred(jxy, s, 1f);
            jyy = Blurred(jyy, s, 1f);
            float[] tx = new float[s * s], ty = new float[s * s], clear = new float[s * s];
            for (int i = 0; i < s * s; i++)
            {
                float a = jxx[i], b = jxy[i], d = jyy[i];
                float half = 0.5f * (a + d), disc = Mathf.Sqrt(Mathf.Max(0f, half * half - (a * d - b * b)));
                float l1 = half + disc, l2 = half - disc;
                float q = (l1 - l2) / (l1 + l2 + 1e-3f);
                clear[i] = q * q;
                // The eigenvector of the smaller value: along the edge.
                float ex = b, ey = l2 - a;
                if (Mathf.Abs(ex) + Mathf.Abs(ey) < 1e-6f)
                {
                    ex = l2 - d;
                    ey = b;
                }
                float len = Mathf.Sqrt(ex * ex + ey * ey);
                if (len < 1e-6f)
                {
                    clear[i] = 0f;
                    continue;
                }
                tx[i] = ex / len;
                ty[i] = ey / len;
            }
            // Up, Catmull-Rom.
            float[] img = new float[n * n * 3];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    Color32 c = CatmullRomAt(v.colour, s, (x + 0.5f) / k, (y + 0.5f) / k);
                    int o = (y * n + x) * 3;
                    img[o] = c.r;
                    img[o + 1] = c.g;
                    img[o + 2] = c.b;
                }
            }
            // Along the edges.
            const int Steps = 4;
            float step = ClothFineReach * k / Steps, spread = ClothFineReach * k * 0.6f;
            float[] w = new float[Steps + 1];
            for (int i = 1; i <= Steps; i++)
            {
                w[i] = Mathf.Exp(-0.5f * (i * step / spread) * (i * step / spread));
            }
            float[] next = new float[img.Length];
            for (int pass = 0; pass < ClothFinePasses; pass++)
            {
                for (int y = 0; y < n; y++)
                {
                    int cy = Math.Min(s - 1, y / k);
                    for (int x = 0; x < n; x++)
                    {
                        int ci = cy * s + Math.Min(s - 1, x / k);
                        int o = (y * n + x) * 3;
                        float q = clear[ci];
                        if (q < 0.02f)
                        {
                            next[o] = img[o];
                            next[o + 1] = img[o + 1];
                            next[o + 2] = img[o + 2];
                            continue;
                        }
                        float r = img[o], g = img[o + 1], b = img[o + 2], sum = 1f;
                        float dx = tx[ci] * step, dy = ty[ci] * step;
                        for (int i = 1; i <= Steps; i++)
                        {
                            float wi = w[i] * q;
                            for (int sign = -1; sign <= 1; sign += 2)
                            {
                                float sx = Mathf.Clamp(x + sign * dx * i, 0f, n - 1.001f), sy = Mathf.Clamp(y + sign * dy * i, 0f, n - 1.001f);
                                int ix = (int)sx, iy = (int)sy;
                                float fx = sx - ix, fy = sy - iy;
                                int p00 = (iy * n + ix) * 3, p10 = p00 + 3, p01 = p00 + n * 3, p11 = p01 + 3;
                                float w00 = (1f - fx) * (1f - fy) * wi, w10 = fx * (1f - fy) * wi, w01 = (1f - fx) * fy * wi, w11 = fx * fy * wi;
                                r += img[p00] * w00 + img[p10] * w10 + img[p01] * w01 + img[p11] * w11;
                                g += img[p00 + 1] * w00 + img[p10 + 1] * w10 + img[p01 + 1] * w01 + img[p11 + 1] * w11;
                                b += img[p00 + 2] * w00 + img[p10 + 2] * w10 + img[p01 + 2] * w01 + img[p11 + 2] * w11;
                                sum += wi;
                            }
                        }
                        next[o] = r / sum;
                        next[o + 1] = g / sum;
                        next[o + 2] = b / sum;
                    }
                }
                float[] t = img;
                img = next;
                next = t;
            }
            Color32[] fine = new Color32[n * n];
            for (int i = 0; i < fine.Length; i++)
            {
                fine[i] = new Color32((byte)Mathf.Clamp(img[i * 3], 0f, 255f), (byte)Mathf.Clamp(img[i * 3 + 1], 0f, 255f), (byte)Mathf.Clamp(img[i * 3 + 2], 0f, 255f), 255);
            }
            v.fine = fine;
            v.fineScale = k;
        }

        // A Gaussian blur of an s by s picture, sigma in pixels.
        private static float[] Blurred(float[] a, int s, float sigma)
        {
            int reach = Mathf.CeilToInt(sigma * 2.5f);
            float[] kern = new float[2 * reach + 1];
            float total = 0f;
            for (int d = -reach; d <= reach; d++)
            {
                kern[d + reach] = Mathf.Exp(-0.5f * d * d / (sigma * sigma));
                total += kern[d + reach];
            }
            float[] mid = new float[a.Length], o = new float[a.Length];
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float sum = 0f;
                    for (int d = -reach; d <= reach; d++)
                    {
                        sum += a[y * s + Mathf.Clamp(x + d, 0, s - 1)] * kern[d + reach];
                    }
                    mid[y * s + x] = sum / total;
                }
            }
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float sum = 0f;
                    for (int d = -reach; d <= reach; d++)
                    {
                        sum += mid[Mathf.Clamp(y + d, 0, s - 1) * s + x] * kern[d + reach];
                    }
                    o[y * s + x] = sum / total;
                }
            }
            return o;
        }

        private static void CatmullRom(float t, out float w0, out float w1, out float w2, out float w3)
        {
            float t2 = t * t, t3 = t2 * t;
            w0 = (-t3 + 2f * t2 - t) * 0.5f;
            w1 = (3f * t3 - 5f * t2 + 2f) * 0.5f;
            w2 = (-3f * t3 + 4f * t2 + t) * 0.5f;
            w3 = (t3 - t2) * 0.5f;
        }

        private static readonly Dictionary<(Material, Material, Texture), ClothView> clothViews = new Dictionary<(Material, Material, Texture), ClothView>();

        // A facing's apparel picture coloured as the game draws it, its ink out and what the head covers in the game left
        // out, grown over the picture, with the body picture's rows and widths; null if it can't be read.
        private static ClothView ViewOf(Material worn, Material body, WalkFigures.HeadCover head, int f, Texture headKey, bool legless)
        {
            if (worn == null || body == null || worn.mainTexture == null || body.mainTexture == null)
            {
                return null;
            }
            ClothView v;
            if (clothViews.TryGetValue((worn, body, headKey), out v))
            {
                return v;
            }
            if (clothViews.Count >= ClothViewsKept)
            {
                clothViews.Clear();
            }
            int s = ClothSpriteLeast;
            while (s < ClothSpriteMost && s < worn.mainTexture.width)
            {
                s *= 2;
            }
            Color32[] art = Api.ReadSpritePixels(worn.mainTexture, s);
            Color32[] bodyPx = Api.ReadSpritePixels(body.mainTexture, s);
            if (art == null || bodyPx == null || art.Length < s * s || bodyPx.Length < s * s)
            {
                clothViews[(worn, body, headKey)] = null;
                return null;
            }
            // Coloured as the game's shaders colour it: the colour, or with a mask its second colour by the mask's red.
            Color one = worn.color, two = one;
            Color32[] mask = null;
            try
            {
                if (worn.HasProperty(ShaderPropertyIDs.ColorTwo))
                {
                    two = worn.GetColor(ShaderPropertyIDs.ColorTwo);
                }
                Texture m = worn.HasProperty(ShaderPropertyIDs.MaskTex) ? worn.GetTexture(ShaderPropertyIDs.MaskTex) : null;
                mask = m != null ? Api.ReadSpritePixels(m, s) : null;
                // A two-colour material with no mask of its own is drawn wholly in its second colour, as the shader's
                // mask is white unless set (Kiiro's maid dress: white, untinted, in the game's portrait, where its first
                // colour, the cloth's, turned the apron grey).
                if (m == null && worn.HasProperty(ShaderPropertyIDs.ColorTwo))
                {
                    one = two;
                }
            }
            catch (Exception)
            {
                mask = null;
            }
            bool[] covered = HeadCovered(head, f, s);
            Color32[] px = new Color32[s * s];
            byte[] alpha = new byte[s * s];
            for (int i = 0; i < px.Length; i++)
            {
                Color tint = mask != null && mask.Length >= px.Length ? Color.Lerp(one, two, mask[i].r / 255f) : one;
                Color32 a = art[i];
                px[i] = new Color32((byte)(a.r * tint.r), (byte)(a.g * tint.g), (byte)(a.b * tint.b), a.a);
                alpha[i] = a.a;
            }
            v = new ClothView { alpha = alpha, size = s };
            // The body's rows and widths (rows from the top).
            int top = -1, bottom = -1;
            v.left = new int[s];
            v.right = new int[s];
            for (int r = 0; r < s; r++)
            {
                int j = s - 1 - r;
                v.left[r] = -1;
                v.right[r] = -1;
                for (int i = 0; i < s; i++)
                {
                    if (bodyPx[j * s + i].a >= 128)
                    {
                        if (v.left[r] < 0)
                        {
                            v.left[r] = i;
                        }
                        v.right[r] = i;
                    }
                }
                if (v.left[r] >= 0)
                {
                    top = top < 0 ? r : top;
                    bottom = r;
                }
            }
            if (top < 0 || bottom - top < s / 10)
            {
                clothViews[(worn, body, headKey)] = null;
                return null;
            }
            v.top = top;
            v.feet = legless ? bottom + LeglessFeet * (bottom - top) : bottom + 1;
            BodyMarks(v, bottom + 1);
            // How far down the head covers the body's middle third; what it covers is taken out of the art (as clear, so
            // the art grows over it from what shows, and counts as little as clear art does), unless it reads too far down.
            if (covered != null)
            {
                int chinRow = -1;
                for (int r = top; r <= bottom; r++)
                {
                    int w = v.right[r] - v.left[r] + 1;
                    for (int i = v.left[r] + w / 3; v.left[r] >= 0 && i <= v.right[r] - w / 3; i++)
                    {
                        if (covered[(s - 1 - r) * s + i])
                        {
                            chinRow = r;
                            break;
                        }
                    }
                }
                float chin = chinRow < 0 ? 0f : (chinRow + 1 - v.top) / (v.feet - v.top);
                if (chin > 0f && chin <= ClothChinMost && chin < v.waist - 0.1f)
                {
                    v.chin = chin;
                    for (int i = 0; i < px.Length; i++)
                    {
                        if (covered[i])
                        {
                            px[i].a = 0;
                            alpha[i] = 0;
                        }
                    }
                }
            }
            v.colour = GrownArt(px, s);
            if (clothStyle == 2)
            {
                Upscaled(v);
            }
            // Rows with no body (above its top, below its feet, a gap) take the nearest row that has one.
            int last = -1;
            for (int r = 0; r < s; r++)
            {
                if (v.left[r] >= 0)
                {
                    last = r;
                }
                else if (last >= 0)
                {
                    v.left[r] = v.left[last];
                    v.right[r] = v.right[last];
                }
            }
            for (int r = 0; r < top; r++)
            {
                v.left[r] = v.left[top];
                v.right[r] = v.right[top];
            }
            EdgesOf(v, bodyPx);
            clothViews[(worn, body, headKey)] = v;
            return v;
        }

        // The body sprite's edges to lay the art across by, smooth: each row's to a fraction of a pixel by the alpha of the
        // pixels either side of it, then averaged over ClothEdgeSmooth rows. Taken whole a pixel at a time, every step in
        // the sprite's outline moved its row of art sideways on the chibi, so the art's shading came out in saw-teeth
        // (magnified some eight times onto the garment).
        private static void EdgesOf(ClothView v, Color32[] bodyPx)
        {
            int s = v.size;
            float[] lo = new float[s], hi = new float[s];
            for (int r = 0; r < s; r++)
            {
                int j = s - 1 - r;
                int l = v.left[r], h = v.right[r];
                if (bodyPx[j * s + l].a < 128 || bodyPx[j * s + h].a < 128)
                {
                    // A row with no body of its own (above it, below it, a gap), given the nearest's columns.
                    lo[r] = l;
                    hi[r] = h + 1f;
                    continue;
                }
                float outL = l > 0 ? bodyPx[j * s + l - 1].a / 255f : 0f, inL = bodyPx[j * s + l].a / 255f;
                float outH = h < s - 1 ? bodyPx[j * s + h + 1].a / 255f : 0f, inH = bodyPx[j * s + h].a / 255f;
                lo[r] = l + (1f - inL) - outL;
                hi[r] = h + 1f - (1f - inH) + outH;
            }
            float sigma = ClothEdgeSmooth * s / 128f;
            int reach = Mathf.CeilToInt(sigma * 2.5f);
            v.edgeLow = new float[s];
            v.edgeHigh = new float[s];
            for (int r = 0; r < s; r++)
            {
                float sl = 0f, sh = 0f, w = 0f;
                for (int d = -reach; d <= reach; d++)
                {
                    int rr = Mathf.Clamp(r + d, 0, s - 1);
                    float k = Mathf.Exp(-0.5f * d * d / (sigma * sigma));
                    sl += lo[rr] * k;
                    sh += hi[rr] * k;
                    w += k;
                }
                v.edgeLow[r] = sl / w;
                v.edgeHigh[r] = sh / w;
            }
        }

        // The view's column at a share across the body (0 its left edge, 1 its right) at a row (pixels from the top, any
        // fraction), the edges read between rows.
        private static float ColumnAt(ClothView v, float row, float across)
        {
            if (clothStyle == 1)
            {
                int rr = Mathf.Clamp(Mathf.RoundToInt(row), 0, v.size - 1);
                return v.left[rr] + across * (v.right[rr] + 1 - v.left[rr]);
            }
            float y = Mathf.Clamp(row - 0.5f, 0f, v.size - 1.001f);
            int r = (int)y;
            float t = y - r;
            float lo = Mathf.Lerp(v.edgeLow[r], v.edgeLow[r + 1], t), hi = Mathf.Lerp(v.edgeHigh[r], v.edgeHigh[r + 1], t);
            return lo + across * (hi - lo);
        }

        // Which pixels of a body picture s pixels a side (rows bottom up) the pawn's head covers in facing f (the west as
        // the east, as its views are): null if it can't be told.
        private static bool[] HeadCovered(WalkFigures.HeadCover head, int f, int s)
        {
            int hf = f == 3 && head != null && head.map[3] == null ? 1 : f;
            float[] m = head?.map[hf];
            if (m == null)
            {
                return null;
            }
            const int hs = 128;
            Color32[] hp = null;
            foreach (Texture t in new[] { head.art[hf], head.typeArt[hf] })
            {
                Color32[] p = t != null ? Api.ReadSpritePixels(t, hs) : null;
                if (p != null && p.Length >= hs * hs && Array.Exists(p, c => c.a >= AddonSolid))
                {
                    hp = p;
                    break;
                }
            }
            if (hp == null)
            {
                return null;
            }
            bool[] covered = new bool[s * s];
            for (int j = 0; j < s; j++)
            {
                float v = (j + 0.5f) / s;
                for (int i = 0; i < s; i++)
                {
                    float u = (i + 0.5f) / s;
                    float hu = m[0] + m[1] * u + m[2] * v, hv = m[3] + m[4] * u + m[5] * v;
                    if (hu < 0f || hu >= 1f || hv < 0f || hv >= 1f)
                    {
                        continue;
                    }
                    covered[j * s + i] = hp[(int)(hv * hs) * hs + (int)(hu * hs)].a >= AddonSolid;
                }
            }
            return covered;
        }

        // The body sprite's shoulder line (the first row as wide as WideShare of its widest in its top third) and waist (its
        // narrowest row between a quarter and most of the way to the crotch, if WaistNarrower of the widest above and below
        // it: the game's male body has none), as shares of its height; the defaults where they can't be told.
        private static void BodyMarks(ClothView v, int end)
        {
            const float WideShare = 0.8f;
            int s = v.size, top = (int)v.top;
            float height = end - top;
            Func<int, int> width = r => v.right[r] - v.left[r] + 1;
            int upperEnd = top + (int)(0.3f * height), most = 0;
            for (int r = top; r < upperEnd && r < s; r++)
            {
                most = Math.Max(most, width(r));
            }
            float shoulder = ClothShoulder;
            for (int r = top; r < upperEnd && r < s; r++)
            {
                if (width(r) >= WideShare * most)
                {
                    shoulder = (r - top) / height;
                    break;
                }
            }
            int from = top + (int)(0.25f * height), to = Math.Min(s - 1, top + (int)(0.58f * height));
            int waistRow = -1;
            for (int r = from; r <= to; r++)
            {
                if (waistRow < 0 || width(r) < width(waistRow))
                {
                    waistRow = r;
                }
            }
            float waist = ClothWaist;
            if (waistRow > top)
            {
                int above = 0, below = 0;
                for (int r = top; r < waistRow; r++)
                {
                    above = Math.Max(above, width(r));
                }
                for (int r = waistRow; r < Math.Min(s, top + (int)(0.8f * height)); r++)
                {
                    below = Math.Max(below, width(r));
                }
                if (width(waistRow) < WaistNarrower * Math.Min(above, below))
                {
                    waist = (waistRow - top) / height;
                }
            }
            if (shoulder >= 0.02f && shoulder < waist - 0.1f)
            {
                v.shoulder = shoulder;
            }
            v.waist = waist;
        }

        // The picture with its outline ink taken out and its colours grown over the rest of it (the rest past ClothGrow
        // its mean), opaque, as Bled grows a race part's. The ink lines drawn inside it go too (seams, a vest's edges, a
        // collar's lines: dark lines up to about 2 ClothLineReach wide, pixels at 128, ClothInkDark of the lightest beside
        // them), as the art is magnified many times onto the garment, where they came out as dark smears; what they
        // parted stays parted by its colours. Wider dark parts (a belt, a dark panel) are kept.
        private static Color32[] GrownArt(Color32[] px, int s)
        {
            Color32[] o = (Color32[])px.Clone();
            bool[] solid = new bool[o.Length];
            List<float> lights = new List<float>();
            for (int i = 0; i < o.Length; i++)
            {
                solid[i] = o[i].a >= AddonSolid;
                if (solid[i])
                {
                    lights.Add(Light(o[i]));
                }
            }
            if (lights.Count > 0)
            {
                lights.Sort();
                float dark = ClothInkDark * lights[lights.Count / 2];
                float[] edge = Distances(px, s);
                float[] light = new float[o.Length];
                for (int i = 0; i < o.Length; i++)
                {
                    light[i] = solid[i] ? Light(o[i]) : 0f;
                }
                // A grey closing: each pixel's lightest round it, then the darkest of those, which fills a dark line
                // narrower than the window from either side.
                int reach = Mathf.Max(1, Mathf.RoundToInt(ClothLineReach * s / 128f));
                float[] closed = Spread(Spread(light, s, reach, true), s, reach, false);
                int kept = 0, keptEdge = 0;
                bool[] ink = new bool[o.Length], line = new bool[o.Length];
                for (int i = 0; i < o.Length; i++)
                {
                    ink[i] = solid[i] && edge[i] < ClothInkReach * s / 128f && Light(o[i]) < dark;
                    line[i] = solid[i] && !ink[i] && light[i] < ClothInkDark * closed[i];
                    keptEdge += solid[i] && !ink[i] ? 1 : 0;
                    kept += solid[i] && !ink[i] && !line[i] ? 1 : 0;
                }
                bool lines = kept > lights.Count / 4;
                if (lines || keptEdge > lights.Count / 4)
                {
                    for (int i = 0; i < o.Length; i++)
                    {
                        solid[i] &= !ink[i] && !(lines && line[i]);
                    }
                }
            }
            PullPush(o, solid, s);
            return o;
        }

        // Each pixel's highest (or lowest) within reach either way, across and down (a square window, s a side).
        private static float[] Spread(float[] a, int s, int reach, bool highest)
        {
            float[] across = new float[a.Length], o = new float[a.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                float[] from = pass == 0 ? a : across, to = pass == 0 ? across : o;
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        float best = from[y * s + x];
                        for (int d = -reach; d <= reach; d++)
                        {
                            int xx = pass == 0 ? x + d : x, yy = pass == 0 ? y : y + d;
                            if (xx < 0 || yy < 0 || xx >= s || yy >= s)
                            {
                                continue;
                            }
                            float c = from[yy * s + xx];
                            best = highest ? Mathf.Max(best, c) : Mathf.Min(best, c);
                        }
                        to[y * s + x] = best;
                    }
                }
            }
            return o;
        }

        // Fills the pixels not marked have (s a power of two) from ever coarser averages of those that are, the nearest
        // level that has any taking over smoothly: a pull-push fill, so the colour past the art's edge is the art's near
        // it without being smeared out over many passes, and every pixel is filled. All made opaque.
        private static void PullPush(Color32[] o, bool[] have, int s)
        {
            List<Vector4[]> levels = new List<Vector4[]>();   // rgb times weight, weight
            Vector4[] first = new Vector4[s * s];
            for (int i = 0; i < first.Length; i++)
            {
                first[i] = have[i] ? new Vector4(o[i].r, o[i].g, o[i].b, 1f) : Vector4.zero;
            }
            levels.Add(first);
            for (int n = s; n > 1; n /= 2)
            {
                Vector4[] up = levels[levels.Count - 1];
                int h = n / 2;
                Vector4[] down = new Vector4[h * h];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < h; x++)
                    {
                        down[y * h + x] = up[2 * y * n + 2 * x] + up[2 * y * n + 2 * x + 1] + up[(2 * y + 1) * n + 2 * x] + up[(2 * y + 1) * n + 2 * x + 1];
                    }
                }
                levels.Add(down);
            }
            Vector4 top = levels[levels.Count - 1][0];
            Vector3[] fill = { top.w > 0f ? new Vector3(top.x, top.y, top.z) / top.w : new Vector3(200f, 200f, 200f) };
            for (int l = levels.Count - 2; l >= 0; l--)
            {
                int n = s >> l, h = n / 2;
                Vector4[] here = levels[l];
                Vector3[] next = new Vector3[n * n];
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        Vector4 k = here[y * n + x];
                        Vector3 coarse = Bilinear(fill, h, (x + 0.5f) * 0.5f - 0.5f, (y + 0.5f) * 0.5f - 0.5f);
                        float a = Mathf.Clamp01(k.w);
                        next[y * n + x] = k.w > 0f ? new Vector3(k.x, k.y, k.z) / k.w * a + coarse * (1f - a) : coarse;
                    }
                }
                fill = next;
            }
            for (int i = 0; i < o.Length; i++)
            {
                if (!have[i])
                {
                    o[i] = new Color32((byte)Mathf.Clamp(fill[i].x, 0f, 255f), (byte)Mathf.Clamp(fill[i].y, 0f, 255f), (byte)Mathf.Clamp(fill[i].z, 0f, 255f), 255);
                }
                o[i].a = 255;
            }
        }

        // The coarser level's colour at (x, y), bilinear between its pixels (so a fill doesn't come out in squares).
        private static Vector3 Bilinear(Vector3[] a, int n, float x, float y)
        {
            if (n <= 1)
            {
                return a[0];
            }
            x = Mathf.Clamp(x, 0f, n - 1.001f);
            y = Mathf.Clamp(y, 0f, n - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            Vector3 top = Vector3.Lerp(a[y0 * n + x0], a[y0 * n + x0 + 1], fx), bottom = Vector3.Lerp(a[(y0 + 1) * n + x0], a[(y0 + 1) * n + x0 + 1], fx);
            return Vector3.Lerp(top, bottom, fy);
        }

        // Grows the colours of the pixels marked have into their neighbours, passes times, and gives what is left their
        // mean; all made opaque. Returns the mean (rgb, count).
        private static Vector4 Grown(Color32[] o, bool[] have, int s, int passes)
        {
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i < o.Length; i++)
            {
                if (have[i])
                {
                    r += o[i].r;
                    g += o[i].g;
                    b += o[i].b;
                    n++;
                }
            }
            Color32 mean = n > 0 ? new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255) : new Color32(200, 200, 200, 255);
            List<int> frontier = new List<int>();
            for (int pass = 0; pass < passes; pass++)
            {
                frontier.Clear();
                for (int y = 0; y < s; y++)
                {
                    for (int x = 0; x < s; x++)
                    {
                        int i = y * s + x;
                        if (have[i])
                        {
                            continue;
                        }
                        int sr = 0, sg = 0, sb = 0, sn = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int yy = y + dy;
                            if (yy < 0 || yy >= s)
                            {
                                continue;
                            }
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx;
                                if (xx < 0 || xx >= s || !have[yy * s + xx])
                                {
                                    continue;
                                }
                                Color32 c = o[yy * s + xx];
                                sr += c.r;
                                sg += c.g;
                                sb += c.b;
                                sn++;
                            }
                        }
                        if (sn > 0)
                        {
                            o[i] = new Color32((byte)(sr / sn), (byte)(sg / sn), (byte)(sb / sn), 255);
                            frontier.Add(i);
                        }
                    }
                }
                if (frontier.Count == 0)
                {
                    break;
                }
                foreach (int i in frontier)
                {
                    have[i] = true;
                }
            }
            for (int i = 0; i < o.Length; i++)
            {
                if (!have[i])
                {
                    o[i] = mean;
                }
                o[i].a = 255;
            }
            return new Vector4(mean.r, mean.g, mean.b, n);
        }
    
#if AUTOTEST
        // The painted image and each view's grown art, written for looking over (<config>/M3AT/cloth_debug/).
        private static void DumpCloth(Texture2D tex, ClothView[] views, string defName, string garment, Material[] worn)
        {
            try
            {
                string folder = System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "cloth_debug");
                System.IO.Directory.CreateDirectory(folder);
                string stem = System.IO.Path.Combine(folder, garment + "_" + defName);
                System.IO.File.WriteAllBytes(stem + ".png", tex.EncodeToPNG());
                int s = views[2].size;
                Texture2D sheet = new Texture2D(s * 4, s * 2, TextureFormat.RGBA32, false);
                List<string> said = new List<string>();
                for (int f = 0; f < 4; f++)
                {
                    ClothView v = views[f];
                    for (int y = 0; y < s; y++)
                    {
                        for (int x = 0; x < s; x++)
                        {
                            Color32 c = v != null && v.size == s ? v.colour[y * s + x] : new Color32(255, 0, 255, 255);
                            byte a = v != null && v.size == s ? v.alpha[y * s + x] : (byte)0;
                            sheet.SetPixel(f * s + x, s + y, c);
                            sheet.SetPixel(f * s + x, y, new Color32(a, a, a, 255));
                        }
                    }
                    if (worn[f] != null)
                    {
                        Texture m = worn[f].HasProperty(ShaderPropertyIDs.MaskTex) ? worn[f].GetTexture(ShaderPropertyIDs.MaskTex) : null;
                        said.Add(f + ": " + worn[f].mainTexture?.name + " colour " + worn[f].color + " two "
                            + (worn[f].HasProperty(ShaderPropertyIDs.ColorTwo) ? worn[f].GetColor(ShaderPropertyIDs.ColorTwo).ToString() : "-")
                            + " mask " + (m != null ? m.name : "none") + (v != null ? " body rows " + v.top + ".." + v.feet + " shoulder " + v.shoulder.ToString("0.00")
                            + " waist " + v.waist.ToString("0.00") + " chin " + v.chin.ToString("0.00") : " unread"));
                    }
                }
                sheet.Apply();
                System.IO.File.WriteAllBytes(stem + "_views.png", sheet.EncodeToPNG());
                System.IO.File.WriteAllLines(stem + ".txt", said.ToArray());
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "couldn't write the cloth debug pictures: " + e.Message);
            }
        }
#endif
    }
}
