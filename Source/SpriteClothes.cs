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
    // have no arms) takes the nearest. The garment's own image is multiplied back in as detail (its stitching, hems and
    // folds), about its median, so the art keeps its colours. One image per garment, apparel material and body picture,
    // made at most one a frame (ClothBakesPerFrame); until made the garment is drawn as before. Not for hats and masks
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
        private const int ClothBakesPerFrame = 1;
        private const int ClothMost = 600;            // images made at most this session (then the rest are drawn plain)
        private const float ClothArtWeight = 0.85f;   // how much more a view counts where it has art (by its alpha)
        private const float ClothDetailLeast = 0.45f, ClothDetailMost = 1.15f;   // the garment's own image's detail, about its median, kept within these
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

        // Each garment's texels as placed on it at rest: the image's pixel, its place and way out (model space, cells),
        // and the garment's own image there over its median (1: none).
        private sealed class ClothTexels
        {
            public int[] at;
            public Vector3[] pos;
            public Vector3[] way;
            public float[] detail;
        }

        // One painted image and its materials (a Piece only for TexturedMaterial's sake).
        public sealed class ClothImage
        {
            internal Piece holder;
            public Color mean;
        }

        private readonly Dictionary<int, ClothTexels> clothTexels = new Dictionary<int, ClothTexels>();
        private readonly Dictionary<(int, Material, Texture), ClothImage> clothImages = new Dictionary<(int, Material, Texture), ClothImage>();
        private float[] clothLowX, clothHighX, clothLowZ, clothHighZ;   // the chibi's trunk and legs, by band of height
        private float clothAnkle, clothScale;   // the ankle joint's height (cells) and cells per rig unit
        private float clothBandLow, clothBandHigh;   // the heights the bands span (cells)
        private static int clothFrame = -1, clothBakes;
        private static int clothMade;
        private static bool clothFull;

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
                var key = (g, worn[2], body[2].mainTexture);
                ClothImage made;
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
                made = PaintCloth(g, worn, body, legless, apparel.def.defName);
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
        // the body sprite's own shoulder line and waist (the view's), straight between them and past either end.
        private float ClothShare(float y, ClothView v)
        {
            float h = RigAnkle + (y - clothAnkle) / clothScale;
            float[] rig = { RigTop, RigShoulder, RigWaist, RigCrotch, RigKneeMark, RigAnkle };
            float[] share = { 0f, v.shoulder, v.waist, ClothCrotch, ClothKnee, 1f };
            int i = 0;
            while (i < rig.Length - 2 && h < rig[i + 1])
            {
                i++;
            }
            return share[i] + (h - rig[i]) * (share[i + 1] - share[i]) / (rig[i + 1] - rig[i]);
        }

        private int ClothBand(float y)
        {
            return Mathf.Clamp(Mathf.FloorToInt((y - clothBandLow) / (clothBandHigh - clothBandLow) * ClothBands), 0, ClothBands - 1);
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
            // The garment's own image, for its detail.
            Color32[] own = null;
            int ow = 0, oh = 0;
            try
            {
                own = p.texture.GetPixels32();
                ow = p.texture.width;
                oh = p.texture.height;
            }
            catch (Exception)
            {
                own = null;   // not readable: no detail
            }
            float median = 1f;
            if (own != null && own.Length == ow * oh && own.Length > 0)
            {
                List<float> lights = new List<float>(own.Length / 7 + 1);
                for (int i = 0; i < own.Length; i += 7)
                {
                    lights.Add(Light(own[i]) / 255f);
                }
                lights.Sort();
                median = Mathf.Max(0.05f, lights[lights.Count / 2]);
            }
            else
            {
                own = null;
            }
            List<int> at = new List<int>();
            List<Vector3> pos = new List<Vector3>(), ways = new List<Vector3>();
            List<float> detail = new List<float>();
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
                        float d = 1f;
                        if (own != null)
                        {
                            int ox = Mathf.Clamp((int)(px / ClothSize * ow), 0, ow - 1), oy = Mathf.Clamp((int)(py / ClothSize * oh), 0, oh - 1);
                            d = Mathf.Clamp(Light(own[oy * ow + ox]) / 255f / median, ClothDetailLeast, ClothDetailMost);
                        }
                        detail.Add(d);
                    }
                }
            }
            t = new ClothTexels { at = at.ToArray(), pos = pos.ToArray(), way = ways.ToArray(), detail = detail.ToArray() };
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
            public int[] left, right;  // the body's opaque columns in each row from the top (filled from the nearest row with any)
        }

        private ClothImage PaintCloth(int g, Material[] worn, Material[] body, bool legless, string defName)
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
                views[f] = ViewOf(worn[f], body[f], legless);
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
                }
            }
            Color32[] img = new Color32[ClothSize * ClothSize];
            bool[] have = new bool[img.Length];
            for (int k = 0; k < t.at.Length; k++)
            {
                Vector3 p = t.pos[k], n = t.way[k];
                int band = ClothBand(p.y);
                float xl = clothLowX[band], xh = clothHighX[band], zl = clothLowZ[band], zh = clothHighZ[band];
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
                    int r = Mathf.Clamp(Mathf.RoundToInt(row), 0, v.size - 1);
                    float col = v.left[r] + across * (v.right[r] + 1 - v.left[r]);
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
                    int r = Mathf.Clamp(Mathf.RoundToInt(row), 0, v.size - 1);
                    float col = v.left[r] + (xh - p.x) / Mathf.Max(1e-4f, xh - xl) * (v.right[r] + 1 - v.left[r]);
                    Color32 c;
                    byte a;
                    Sample(v, col, v.size - row, out c, out a);
                    sum = new Vector4(c.r, c.g, c.b, 0f);
                    weights = 1f;
                }
                float d = t.detail[k] / weights;
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
            int s = v.size;
            float x = Mathf.Clamp(col - 0.5f, 0f, s - 1.001f), y = Mathf.Clamp(rowUp - 0.5f, 0f, s - 1.001f);
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
                    Color32 p = v.colour[yy * s + xx];
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
            c = new Color32((byte)Mathf.Clamp(r, lo.r, hi.r), (byte)Mathf.Clamp(g, lo.g, hi.g), (byte)Mathf.Clamp(b, lo.b, hi.b), 255);
            a = v.alpha[Mathf.RoundToInt(y) * s + Mathf.RoundToInt(x)];
        }

        private static void CatmullRom(float t, out float w0, out float w1, out float w2, out float w3)
        {
            float t2 = t * t, t3 = t2 * t;
            w0 = (-t3 + 2f * t2 - t) * 0.5f;
            w1 = (3f * t3 - 5f * t2 + 2f) * 0.5f;
            w2 = (-3f * t3 + 4f * t2 + t) * 0.5f;
            w3 = (t3 - t2) * 0.5f;
        }

        private static readonly Dictionary<(Material, Material), ClothView> clothViews = new Dictionary<(Material, Material), ClothView>();

        // A facing's apparel picture coloured as the game draws it, its ink out and grown over the picture, with the body
        // picture's rows and widths; null if it can't be read.
        private static ClothView ViewOf(Material worn, Material body, bool legless)
        {
            if (worn == null || body == null || worn.mainTexture == null || body.mainTexture == null)
            {
                return null;
            }
            ClothView v;
            if (clothViews.TryGetValue((worn, body), out v))
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
                clothViews[(worn, body)] = null;
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
            Color32[] px = new Color32[s * s];
            byte[] alpha = new byte[s * s];
            for (int i = 0; i < px.Length; i++)
            {
                Color tint = mask != null && mask.Length >= px.Length ? Color.Lerp(one, two, mask[i].r / 255f) : one;
                Color32 a = art[i];
                px[i] = new Color32((byte)(a.r * tint.r), (byte)(a.g * tint.g), (byte)(a.b * tint.b), a.a);
                alpha[i] = a.a;
            }
            v = new ClothView { colour = GrownArt(px, s), alpha = alpha, size = s };
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
                clothViews[(worn, body)] = null;
                return null;
            }
            v.top = top;
            v.feet = legless ? bottom + LeglessFeet * (bottom - top) : bottom + 1;
            BodyMarks(v, bottom + 1);
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
            clothViews[(worn, body)] = v;
            return v;
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
        // its mean), opaque, as Bled grows a race part's.
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
                int kept = 0;
                bool[] ink = new bool[o.Length];
                for (int i = 0; i < o.Length; i++)
                {
                    ink[i] = solid[i] && edge[i] < ClothInkReach * s / 128f && Light(o[i]) < dark;
                    kept += solid[i] && !ink[i] ? 1 : 0;
                }
                if (kept > lights.Count / 4)
                {
                    for (int i = 0; i < o.Length; i++)
                    {
                        solid[i] &= !ink[i];
                    }
                }
            }
            PullPush(o, solid, s);
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
                            + " mask " + (m != null ? m.name : "none") + (v != null ? " body rows " + v.top + ".." + v.feet : " unread"));
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
