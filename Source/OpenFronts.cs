using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Open-fronted garments (the duster, the lab coat) stand in only for apparel whose own art is open down the front:
    // drawn for a closed dress, armour or vacsuit, the body showed down the middle where the game's sprite shows none.
    // The front (south) sprite is read once per apparel: an open coat's has a clear gap down its middle from the hem up
    // into the chest (the duster 0.88 of its height, the lab coat 0.83, other mods' open coats and cloaks 0.64 and up),
    // where a closed one has none or only the gap between the legs under a skirt (at most 0.22 over the apparel looked
    // at). Without one of these the next best garment stands in (most often the robe, closed below its V neck).
    // anim_scripts/stand_ins.py does the same over the sprites on disk; change the two together.
    public sealed partial class SkinnedFigure
    {
        private static readonly HashSet<string> OpenFronts = new HashSet<string> { "Duster", "LabCoat" };
        private const float OpenFrontLeast = 0.45f;   // the least share of the sprite's height its middle gap reaches up from the hem
        private const int OpenSize = 128;             // the sprite read at this many pixels a side
        private const int OpenHalfWidth = 1;          // the middle looked at: this many pixels either side of the middle column
        // The body types whose sprite is looked for, in order (Graphic_Multi's <path>_<body>_south), then the path alone.
        private static readonly string[] OpenBodies = { "Male", "Female", "Thin", "Hulk", "Fat" };

        private static readonly Dictionary<ThingDef, float> openShares = new Dictionary<ThingDef, float>();

        // True if garment g may stand in for the apparel by its front: any garment not open-fronted, an open-fronted one
        // only for apparel whose sprite is open down the front (not for apparel whose sprite can't be read).
        private static bool FrontFits(Profile g, ThingDef apparel)
        {
            return !OpenFronts.Contains(g.name) || OpenFrontShare(apparel) >= OpenFrontLeast;
        }

        // How far up from its hem the clear gap down the middle of the apparel's front sprite reaches, as a share of the
        // garment's height (only rows with art either side of the gap counted); 0 for a closed front, -1 for no sprite.
        public static float OpenFrontShare(ThingDef def)
        {
            float share;
            if (def == null || def.apparel == null)
            {
                return -1f;
            }
            if (!openShares.TryGetValue(def, out share))
            {
                share = -1f;
                try
                {
                    Texture tex = FrontSprite(def.apparel.wornGraphicPath);
                    Color32[] px = tex != null ? Api.ReadSpritePixels(tex, OpenSize) : null;
                    if (px != null && px.Length >= OpenSize * OpenSize)
                    {
                        share = GapShare(px);
                    }
                }
                catch (Exception e)
                {
                    Log.WarningOnce(Tag + "reading the front of " + def.defName + "'s sprite failed: " + e, 61530120);
                }
                openShares[def] = share;
            }
            return share;
        }

        private static Texture FrontSprite(string path)
        {
            if (path.NullOrEmpty())
            {
                return null;
            }
            foreach (string body in OpenBodies)
            {
                Texture2D t = ContentFinder<Texture2D>.Get(path + "_" + body + "_south", false);
                if (t != null)
                {
                    return t;
                }
            }
            return ContentFinder<Texture2D>.Get(path + "_south", false);
        }

        // px: rows bottom first. From the garment's lowest row up, while the middle is clear: the rows with art either
        // side of it, over the garment's height.
        private static float GapShare(Color32[] px)
        {
            int n = OpenSize, mid = n / 2, low = -1, high = -1;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (px[j * n + i].a > 128)
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
            if (low < 0)
            {
                return 0f;
            }
            int open = 0;
            for (int j = low; j <= high; j++)
            {
                bool middle = false, left = false, right = false;
                for (int i = 0; i < n; i++)
                {
                    if (px[j * n + i].a <= 128)
                    {
                        continue;
                    }
                    if (i < mid - OpenHalfWidth)
                    {
                        left = true;
                    }
                    else if (i > mid + OpenHalfWidth)
                    {
                        right = true;
                    }
                    else
                    {
                        middle = true;
                    }
                }
                if (middle)
                {
                    break;
                }
                if (left && right)
                {
                    open++;
                }
            }
            return open / (float)(high - low + 1);
        }
    }
}
