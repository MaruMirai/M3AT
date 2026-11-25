using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Art in other layers that belongs with the eyes or the mouth, moved with them. The mouth is drawn on its own copy of
    // the face patch, raised MouthRaise and sized by the race fit, the narrowing of wide shapes and the player's tuning; the
    // eyes, lids, brows and lid options take the eye fit (FaceGaze.cs, FaceSetFit.cs: a human set's eyes at the
    // retexture's size and row, about 1.14 times Experimentals' own; a race's raised and spread apart). Facial Animation's
    // other layers are drawn where it puts them, so what they paint round those features was left behind:
    //   - Experimentals' and the retexture's blush (the head's Cover layer, blush_cover) lies just under the eye line, and
    //     the enlarged eyes' lower lids came down over it;
    //   - Experimentals' gloomy emotion is a shadow across the tops of both eyes;
    //   - Wanted: A facial anim. Extension's makeup (skin overlays) paints brows, eyeshadow and lipstick, and its lips came
    //     out under the chibi's mouth as a second pair.
    //
    // So a skin overlay, emotion or head cover texture is read once (cached per texture) and its art split into pieces:
    // pixels within AreaGroup of each other (at 512) are one piece, so a band of shading or a cluster of freckles stays
    // together. A piece small enough and centred in the mouth's box (MouthArea*) goes with the mouth; one centred round the
    // eyes (EyeArea*: from under the lower lids to the brows, not out to the ears) with the eyes; anything bigger or
    // elsewhere (a whole-face tint, stubble round the mouth, earrings, freckles across the face) stays. Each area is then a
    // layer of its own: the eye area right after the rest, in the layer's place, with the eye fit (EyeAreaKind, one of
    // SpreadsWithEyes); the mouth area just under the pawn's first mouth layer, with its shift and tuning (MouthAreaKind).
    // Drawing only; the mods' textures are left as they are.
    public static partial class FaceLayers
    {
        internal const string EyeAreaKind = "EyeAreaControllerComp";       // the face tool lists it as "EyeArea"
        internal const string MouthAreaKind = "MouthAreaControllerComp";   // drawn on the mouth's patch, not inked
        private const float AreaGroup = 6f;           // pixels at 512 within which art is one piece
        private const byte AreaAlpha = 16;            // alpha from which a pixel counts as art
        private const int AreaLeast = 4;              // art pixels (at 512) a piece needs to be moved
        // Texture places (u right, v up from the bottom): the mouth's box about MouthCentre, its half sizes, and a piece's
        // widest and tallest (Wanted's lips 0.035 by 0.016; stubble round the mouth 0.19 wide stays).
        private const float MouthAreaHalfWidth = 0.06f, MouthAreaHalfHeight = 0.03f, MouthAreaWidest = 0.12f, MouthAreaTallest = 0.06f;
        // The eyes': a piece's middle within EyeAreaHalfWidth of the face's middle (the eyes at about 0.06 either side, the
        // brows' middles 0.08; Wanted's earrings at 0.13 stay) and from EyeAreaBelow under the eye line (FacePivot) to
        // EyeAreaAbove over it (the blush 0.03 under, the gloomy shadow 0.06 over); at most EyeAreaWidest across both eyes
        // (the blush and the shadow 0.23) and EyeAreaTallest.
        private const float EyeAreaHalfWidth = 0.115f, EyeAreaBelow = 0.06f, EyeAreaAbove = 0.11f, EyeAreaWidest = 0.3f, EyeAreaTallest = 0.13f;

        private class AreaSplit
        {
            public Texture rest, eyes, mouth;   // eyes or mouth null if it has none
        }

        private static readonly Dictionary<Texture, AreaSplit> areaSplits = new Dictionary<Texture, AreaSplit>();
        private static readonly HashSet<string> areasLogged = new HashSet<string>();
        private static readonly List<LayerCall> pendingMouth = new List<LayerCall>();   // the face being drawn's, until its mouth

        private static bool OnMouthPatch(string kind)
        {
            return kind == "MouthControllerComp" || kind == MouthAreaKind;
        }

        // Which layers are looked at: skin overlays (not the whole sprite skin, which isn't drawn), emotions, and the head's
        // Cover (the blush, hot and cold tints).
        private static bool HasAreas(string kind, string layer)
        {
            return kind == "SkinControllerComp" || kind == "EmotionControllerComp" || (kind == "HeadControllerComp" && layer == "Cover");
        }

        // A layer about to be drawn: with eye or mouth areas, the rest and the eye area are drawn now and the mouth area kept
        // for the mouth. False: draw it as it is.
        private static bool SplitFaceAreas(bool stacking, Graphic g, string kind, string layer, Vector2 shift, Patch patch, Mesh mouth, Matrix4x4 placed, ref int place)
        {
#if AUTOTEST
            if (TestNoAreas)
            {
                return false;
            }
#endif
            Material gm = g != null ? g.MatSouth : null;
            if (!HasAreas(kind, layer) || gm == null || !(gm.mainTexture is Texture2D) || IsDummy(g))
            {
                return false;
            }
            int mode = ModeOf(gm.shader);
            if (mode < 0 || (kind == "SkinControllerComp" && mode != ModeTransparent))
            {
                return false;
            }
            AreaSplit split = AreasOf(gm.mainTexture);
            if (split == null)
            {
                return false;
            }
            Layer(stacking, g, kind, layer, 0, shift, patch, mouth, placed, ref place, split.rest);
            if (split.eyes != null)
            {
                Layer(stacking, g, EyeAreaKind, layer, 0, new Vector2(0f, EyeRiseOf(EyeAreaKind)), patch, mouth, placed, ref place, split.eyes);
            }
            if (split.mouth != null)
            {
                pendingMouth.Add(new LayerCall { g = g, kind = MouthAreaKind, layer = layer, tex = split.mouth });
            }
            return true;
        }

        // The mouth areas kept by SplitFaceAreas, drawn with the mouth's shift and tuning: its first layer's (mouthG), or,
        // with no mouth layer, their own as a mouth's. Not the narrowing of a wide mouth shape (MouthShapeScale), which would
        // squeeze lipstick or a goatee whenever the pawn smiles.
        private static void DrawPendingMouth(bool stacking, Vector2 shift, Graphic mouthG, Patch patch, Mesh mouth, Matrix4x4 placed, ref int place)
        {
            float shape = drawMouthShape;
            drawMouthShape = 1f;
            for (int i = 0; i < pendingMouth.Count; i++)
            {
                LayerCall c = pendingMouth[i];
                c.shift = shift;
                c.tune = TuneOf(mouthG ?? c.g, "MouthControllerComp", mouth == patch.mouthLow ? MouthRaisePig : MouthRaise);
                if (stacking)
                {
                    calls.Add(c);
                }
                else
                {
                    DrawLayer(c.g, c.kind, c.layer, c.cut, c.shift, c.tune, patch, mouth, placed, ref place, c.tex);
                }
            }
            pendingMouth.Clear();
            drawMouthShape = shape;
        }

        private static AreaSplit AreasOf(Texture tex)
        {
            AreaSplit split;
            if (!areaSplits.TryGetValue(tex, out split))
            {
                split = Split(tex);
                areaSplits[tex] = split;
            }
            return split;
        }

        private static AreaSplit Split(Texture tex)
        {
            try
            {
                int n = Math.Min(512, Math.Max(64, tex.width));
                Color32[] px = Api.ReadSpritePixels(tex, n);   // rows bottom first
                if (px == null || px.Length < n * n)
                {
                    return null;
                }
                int count = n * n;
                float s = n / 512f;
                bool[] art = new bool[count];
                bool any = false;
                for (int i = 0; i < count; i++)
                {
                    art[i] = px[i].a >= AreaAlpha;
                    any |= art[i];
                }
                if (!any)
                {
                    return null;
                }
                // Pieces: the art grown by AreaGroup (a square, rows then columns), then joined over 4 neighbours.
                int r = Math.Max(1, Mathf.RoundToInt(AreaGroup * s));
                bool[] grown = Grow(Grow(art, n, r, true), n, r, false);
                int[] label = new int[count];
                List<int[]> boxes = new List<int[]>();   // per piece: x0, y0, x1, y1, art pixels
                Stack<int> todo = new Stack<int>();
                for (int i = 0; i < count; i++)
                {
                    if (!grown[i] || label[i] != 0)
                    {
                        continue;
                    }
                    int id = boxes.Count + 1;
                    int[] box = { n, n, -1, -1, 0 };
                    boxes.Add(box);
                    label[i] = id;
                    todo.Push(i);
                    while (todo.Count > 0)
                    {
                        int k = todo.Pop();
                        int x = k % n, y = k / n;
                        if (art[k])
                        {
                            box[0] = Math.Min(box[0], x);
                            box[1] = Math.Min(box[1], y);
                            box[2] = Math.Max(box[2], x);
                            box[3] = Math.Max(box[3], y);
                            box[4]++;
                        }
                        if (x > 0) Join(k - 1, id, grown, label, todo);
                        if (x < n - 1) Join(k + 1, id, grown, label, todo);
                        if (y > 0) Join(k - n, id, grown, label, todo);
                        if (y < n - 1) Join(k + n, id, grown, label, todo);
                    }
                }
                // Each piece's area: 0 stays, 1 the eyes, 2 the mouth.
                int[] area = new int[boxes.Count + 1];
                bool eyes = false, mouth = false;
                for (int b = 0; b < boxes.Count; b++)
                {
                    int[] box = boxes[b];
                    if (box[4] < AreaLeast * s * s)
                    {
                        continue;
                    }
                    float w = (box[2] - box[0] + 1f) / n, h = (box[3] - box[1] + 1f) / n;
                    float cu = (box[0] + box[2] + 1f) * 0.5f / n, cv = (box[1] + box[3] + 1f) * 0.5f / n;
                    if (Mathf.Abs(cu - MouthCentre.x) <= MouthAreaHalfWidth && Mathf.Abs(cv - MouthCentre.y) <= MouthAreaHalfHeight
                        && w <= MouthAreaWidest && h <= MouthAreaTallest)
                    {
                        area[b + 1] = 2;
                        mouth = true;
                    }
                    else if (Mathf.Abs(cu - 0.5f) <= EyeAreaHalfWidth && cv >= FacePivot.y - EyeAreaBelow && cv <= FacePivot.y + EyeAreaAbove
                        && w <= EyeAreaWidest && h <= EyeAreaTallest)
                    {
                        area[b + 1] = 1;
                        eyes = true;
                    }
                }
                if (!eyes && !mouth)
                {
                    return null;
                }
                Color32[] rest = (Color32[])px.Clone();
                Color32[] eyePx = eyes ? new Color32[count] : null;
                Color32[] mouthPx = mouth ? new Color32[count] : null;
                for (int i = 0; i < count; i++)
                {
                    int a = label[i] != 0 ? area[label[i]] : 0;
                    if (a == 0)
                    {
                        continue;
                    }
                    (a == 1 ? eyePx : mouthPx)[i] = px[i];
                    rest[i] = new Color32(px[i].r, px[i].g, px[i].b, 0);
                }
                string name = tex.name;
                if (areasLogged.Add(name))
                {
                    Log.Message(Tag + "the face layer " + name + " has art drawn with the" + (eyes && mouth ? " eyes and the mouth." : eyes ? " eyes." : " mouth."));
                }
                return new AreaSplit
                {
                    rest = Made(rest, n, "Marus_rest_" + name),
                    eyes = eyes ? Made(eyePx, n, "Marus_eyes_" + name) : null,
                    mouth = mouth ? Made(mouthPx, n, "Marus_mouth_" + name) : null,
                };
            }
            catch (Exception e)
            {
                Log.WarningOnce(Tag + "couldn't read the face layer " + tex.name + " for its eye and mouth areas: " + e.Message, 73190561);
                return null;
            }
        }

        private static void Join(int k, int id, bool[] grown, int[] label, Stack<int> todo)
        {
            if (grown[k] && label[k] == 0)
            {
                label[k] = id;
                todo.Push(k);
            }
        }

        // The mask grown by r pixels along rows (across) or columns.
        private static bool[] Grow(bool[] m, int n, int r, bool across)
        {
            bool[] g = new bool[m.Length];
            for (int line = 0; line < n; line++)
            {
                int last = -r - 1;   // the last set pixel along this line
                for (int t = 0; t < n; t++)
                {
                    if (m[across ? line * n + t : t * n + line])
                    {
                        last = t;
                    }
                    if (t - last <= r)
                    {
                        g[across ? line * n + t : t * n + line] = true;
                    }
                }
                last = n + r + 1;
                for (int t = n - 1; t >= 0; t--)
                {
                    if (m[across ? line * n + t : t * n + line])
                    {
                        last = t;
                    }
                    if (last - t <= r)
                    {
                        g[across ? line * n + t : t * n + line] = true;
                    }
                }
            }
            return g;
        }

        private static Texture2D Made(Color32[] px, int n, string name)
        {
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            t.name = name;
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }
    }
}
