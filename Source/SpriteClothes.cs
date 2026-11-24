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
        private const int ClothSpriteSize = AddonTexSize;   // the sprites read at this size (Distances goes by it)
        private const int ClothBands = 120;           // the chibi's body width measured in this many bands of height
        private const float ClothBandLow = -0.45f, ClothBandHigh = 1.15f;   // from above the sprite body's top to below its feet
        private const int ClothBakesPerFrame = 1;
        private const int ClothMost = 600;            // images made at most this session (then the rest are drawn plain)
        private const float ClothArtWeight = 0.85f;   // how much more a view counts where it has art (by its alpha)
        private const float ClothDetailLeast = 0.45f, ClothDetailMost = 1.15f;   // the garment's own image's detail, about its median, kept within these
        private const int ClothGrow = 40;             // passes growing each sprite's colours past its edge (the rest its mean)
        private const float ClothInkReach = 2.5f, ClothInkDark = 0.55f;   // its outline ink: dark pixels this near its edge (as Bled)
        // The sprite body's measure (HemLengths.cs) at three heights over the chibi's ankle joint, in the rig's units, and
        // that joint's and the knee's height in them (export_frames.py's HEM_ANCHORS; the rig's foot and shin heads).
        private static readonly float[,] ClothAnchors = { { 9.2f, 0.62f }, { 6.5f, 0.81f }, { 1.0f, 1.0f } };
        private const float RigAnkle = 1.025f, RigKnee = 6.351f;

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
        private float[] clothLowX, clothHighX, clothLowZ, clothHighZ;   // the chibi's trunk and legs, by band
        private float clothAnkle, clothScale;   // the ankle joint's height (cells) and cells per rig unit
        private static int clothFrame = -1, clothBakes;
        private static int clothMade;
        private static bool clothFull;

        // True if the garment can be painted with the art of apparel it stands in for: it has texture places and an image
        // of its own, and is worn on the body, not riding one bone (a hat), held, a pack, the shoes or the baby romper.
        public bool PaintsArt(int g, Apparel apparel)
        {
            if (g <= 0 || g >= pieces.Length || apparel == null)
            {
                return false;
            }
            Piece p = pieces[g];
            if (p.uvs == null || p.texture == null || p.boneName != null || p.held || p.forBaby || p.colour.HasValue || p.withLegwear
                || p.genes != null || IsPack(apparel.def) || apparel.WornByCorpse)
            {
                return false;
            }
            // Headwear's art is drawn on the head, not laid on the body (a hat not riding one bone, the ladies' hat).
            List<ApparelLayerDef> layers = apparel.def.apparel?.layers;
            if (layers == null || layers.Count == 0 || layers.TrueForAll(l => Array.IndexOf(HeadLayers, l.defName) >= 0))
            {
                return false;
            }
            return GarmentOf(apparel.def) != g;   // a stand-in, not the garment made for it
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

        // The chibi's body widths by band of the sprite body's measure, and the heights' landmarks (once).
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
            clothLowX = new float[ClothBands];
            clothHighX = new float[ClothBands];
            clothLowZ = new float[ClothBands];
            clothHighZ = new float[ClothBands];
            for (int b = 0; b < ClothBands; b++)
            {
                clothLowX[b] = clothLowZ[b] = float.MaxValue;
                clothHighX[b] = clothHighZ[b] = float.MinValue;
            }
            bool[] head = OnHead(), arm = OnArm();
            Piece body = pieces[0];
            for (int i = 0; i < body.rest.Length; i++)
            {
                if (head[i] || arm[i])
                {
                    continue;
                }
                Vector3 v = body.rest[i];
                int b = ClothBand(ClothShare(v.y));
                if (b < 0)
                {
                    continue;
                }
                clothLowX[b] = Mathf.Min(clothLowX[b], v.x);
                clothHighX[b] = Mathf.Max(clothHighX[b], v.x);
                clothLowZ[b] = Mathf.Min(clothLowZ[b], v.z);
                clothHighZ[b] = Mathf.Max(clothHighZ[b], v.z);
            }
            FillBands(clothLowX, clothHighX);
            FillBands(clothLowZ, clothHighZ);
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

        // A height (model space, cells) in the sprite body's measure: 0 its top, 1 its feet (HemLengths.cs).
        private float ClothShare(float y)
        {
            float h = RigAnkle + (y - clothAnkle) / clothScale;
            float z0 = ClothAnchors[0, 0], s0 = ClothAnchors[0, 1], z1 = ClothAnchors[1, 0], s1 = ClothAnchors[1, 1], z2 = ClothAnchors[2, 0], s2 = ClothAnchors[2, 1];
            return h >= z1 ? s1 + (h - z1) * (s0 - s1) / (z0 - z1) : s1 + (h - z1) * (s2 - s1) / (z2 - z1);
        }

        private static int ClothBand(float share)
        {
            int b = Mathf.FloorToInt((share - ClothBandLow) / (ClothBandHigh - ClothBandLow) * ClothBands);
            return b < 0 || b >= ClothBands ? -1 : b;
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
            public float top, feet;    // the body's top and feet, pixels from the picture's top
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
            Color32[] img = new Color32[ClothSize * ClothSize];
            bool[] have = new bool[img.Length];
            int s = ClothSpriteSize;
            for (int k = 0; k < t.at.Length; k++)
            {
                Vector3 p = t.pos[k], n = t.way[k];
                float share = ClothShare(p.y);
                int band = ClothBand(share);
                band = band < 0 ? (share < ClothBandLow ? 0 : ClothBands - 1) : band;
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
                    float row = v.top + share * (v.feet - v.top);   // pixels from the top
                    int r = Mathf.Clamp(Mathf.RoundToInt(row), 0, s - 1);
                    float col = v.left[r] + across * (v.right[r] + 1 - v.left[r]);
                    Color32 c;
                    byte a;
                    Sample(v, col, s - row, out c, out a);
                    float w = facing * facing * (1f - ClothArtWeight + ClothArtWeight * a / 255f);
                    sum += new Vector4(c.r, c.g, c.b, 0f) * w;
                    weights += w;
                }
                if (weights <= 0f)
                {
                    // Facing straight up or down: the front's.
                    ClothView v = views[2];
                    float row = v.top + share * (v.feet - v.top);
                    int r = Mathf.Clamp(Mathf.RoundToInt(row), 0, s - 1);
                    float col = v.left[r] + (xh - p.x) / Mathf.Max(1e-4f, xh - xl) * (v.right[r] + 1 - v.left[r]);
                    Color32 c;
                    byte a;
                    Sample(v, col, s - row, out c, out a);
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

        // A sprite's pixel at (col, row from the bottom), bilinear on its grown colours, and its own alpha there.
        private static void Sample(ClothView v, float col, float rowUp, out Color32 c, out byte a)
        {
            int s = ClothSpriteSize;
            float x = Mathf.Clamp(col - 0.5f, 0f, s - 1.001f), y = Mathf.Clamp(rowUp - 0.5f, 0f, s - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            Color32 c00 = v.colour[y0 * s + x0], c10 = v.colour[y0 * s + x0 + 1], c01 = v.colour[(y0 + 1) * s + x0], c11 = v.colour[(y0 + 1) * s + x0 + 1];
            Color mixed = Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
            c = mixed;
            a = v.alpha[Mathf.RoundToInt(y) * s + Mathf.RoundToInt(x)];
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
            int s = ClothSpriteSize;
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
            v = new ClothView { colour = GrownArt(px, s), alpha = alpha };
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
                float[] edge = Distances(px);
                int kept = 0;
                bool[] ink = new bool[o.Length];
                for (int i = 0; i < o.Length; i++)
                {
                    ink[i] = solid[i] && edge[i] < ClothInkReach && Light(o[i]) < dark;
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
            Grown(o, solid, s, ClothGrow);
            return o;
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
                int s = ClothSpriteSize;
                Texture2D sheet = new Texture2D(s * 4, s * 2, TextureFormat.RGBA32, false);
                List<string> said = new List<string>();
                for (int f = 0; f < 4; f++)
                {
                    ClothView v = views[f];
                    for (int y = 0; y < s; y++)
                    {
                        for (int x = 0; x < s; x++)
                        {
                            Color32 c = v != null ? v.colour[y * s + x] : new Color32(255, 0, 255, 255);
                            byte a = v != null ? v.alpha[y * s + x] : (byte)0;
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
