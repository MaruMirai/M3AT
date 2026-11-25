using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A race's skin as the game shows it. The game draws a pawn's body sprite tinted by its skin colour, so what is seen is
    // the skin colour times the sprite's own colour. A human's sprite is near white, so the skin colour alone is right for
    // it; another race's may be coloured (Wolfein's is a warm pink, its skin colours pale greys), and the chibi drawn in the
    // skin colour alone came out grey where the game's pawn is pink. So for another race the skin colour is scaled by its
    // body sprite's mean colour over a human's of the same body type (RaceSkinTint), as the Wolfein 3D Pack did for its race
    // alone. A content pack can give a race's skin colour itself (ExpansionApi 2).
    public static partial class WalkFigures
    {
        private const int SkinTintSize = 64;             // pixels a side the sprites are read at
        private const float SkinTintLeast = 0.4f, SkinTintMost = 1.4f;   // the scale kept within these, per channel
        private const float SkinInkBelow = 0.2f;         // pixels darker than this are outline ink, not skin
        private static readonly Dictionary<Texture, Color?> spriteMeans = new Dictionary<Texture, Color?>();
        private static readonly Dictionary<Texture, Color> tints = new Dictionary<Texture, Color>();   // by the race's body sprite
        private static readonly Dictionary<BodyTypeDef, Texture> humanBodies = new Dictionary<BodyTypeDef, Texture>();
        private static readonly Dictionary<Texture, int> nextRead = new Dictionary<Texture, int>();   // a sprite not readable yet: the frame to try again
        private const int SkinReadEvery = 60;            // frames between tries

        // The colour to scale this pawn's skin colour by: white for a human, or when either sprite can't be read.
        private static Color RaceSkinTint(Pawn pawn)
        {
            if (pawn.def == null || pawn.def == ThingDefOf.Human || pawn.story == null || pawn.story.bodyType == null)
            {
                return Color.white;
            }
            try
            {
                Texture own = pawn.Drawer?.renderer?.BodyGraphic?.MatSouth?.mainTexture;
                Texture human;
                if (!humanBodies.TryGetValue(pawn.story.bodyType, out human))
                {
                    human = ContentFinder<Texture2D>.Get("Things/Pawn/Humanlike/Bodies/Naked_" + pawn.story.bodyType.defName + "_south", false)
                        ?? ContentFinder<Texture2D>.Get("Things/Pawn/Humanlike/Bodies/Naked_Female_south", false);
                    humanBodies[pawn.story.bodyType] = human;
                }
                if (own == null || human == null || own == human)
                {
                    return Color.white;
                }
                Color tint;
                if (tints.TryGetValue(own, out tint))
                {
                    return tint;
                }
                Color? a = MeanOf(own), b = MeanOf(human);
                if (a == null || b == null)
                {
                    return Color.white;   // (not read yet: tried again on the next draw)
                }
                tint = new Color(Ratio(a.Value.r, b.Value.r), Ratio(a.Value.g, b.Value.g), Ratio(a.Value.b, b.Value.b), 1f);
                tints[own] = tint;
                return tint;
            }
            catch (Exception e)
            {
                Log.WarningOnce(Tag + "reading " + pawn.def.defName + "'s body sprite for its skin colour failed: " + e.Message, 0x4D33A703);
                return Color.white;
            }
        }

        private static float Ratio(float a, float b)
        {
            return Mathf.Clamp(b > 0.01f ? a / b : 1f, SkinTintLeast, SkinTintMost);
        }

        // The mean colour of a sprite's opaque, not-ink pixels; null if it can't be read (yet).
        private static Color? MeanOf(Texture tex)
        {
            Color? mean;
            if (spriteMeans.TryGetValue(tex, out mean))
            {
                return mean;
            }
            int next;
            if (nextRead.TryGetValue(tex, out next) && Time.frameCount < next)
            {
                return null;
            }
            Color32[] px = IsoRim.Api.ReadSpritePixels(tex, SkinTintSize);
            if (px == null)
            {
                nextRead[tex] = Time.frameCount + SkinReadEvery;   // (tried again later: it may be readable once made)
                return null;
            }
            double r = 0, g = 0, b = 0;
            int n = 0;
            foreach (Color32 p in px)
            {
                if (p.a < 128 || (p.r + p.g + p.b) / (3f * 255f) < SkinInkBelow)
                {
                    continue;
                }
                r += p.r;
                g += p.g;
                b += p.b;
                n++;
            }
            mean = n > 0 ? new Color((float)(r / n / 255), (float)(g / n / 255), (float)(b / n / 255), 1f) : (Color?)null;
            spriteMeans[tex] = mean;
            return mean;
        }
    }
}
