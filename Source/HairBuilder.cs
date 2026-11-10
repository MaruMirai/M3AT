using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Builds a hairstyle's 3D hair from its three sprites while the game runs, for styles the export has no file for (a
    // modded hairstyle). It began as archive/hair.py done again in C#; that is no longer kept in step, and this is the
    // one that is used: the hair's visual hull joined to the head's, each slice cut to a superellipse, pulled in toward
    // the head after texturing,
    // cut along the edge of its art with a wall under each open edge, and the closed shell its outline is drawn from. It
    // writes the files hair.py does (<style>.txt, <style>.shell.txt, <style>.png) into a cache folder, where
    // SkinnedFigure.Hair reads them as it reads the export's own.
    //
    // Where it differs from hair.py: the hull is meshed by surface nets on a coarser grid (Block voxels a side, from how
    // much of each block is inside) rather than Blender's remesh, smooth and decimate, which comes out at about as many
    // triangles; and the head the sprites are drawn over is the game's own (HeadSprites), as modded hair is drawn for it,
    // with its place in the sprite measured from its art rather than written down. The head is the chibi's from skin.txt
    // in the game's model space (the shape key moves in cells), and the lengths hair.py has in Blender units are taken
    // as shares of the head's width.
    //
    // The sprites are read on the main thread (Read: IsoRim's SpritePixels); the rest runs on a background thread, one
    // style at a time, and Finish (main thread) writes what is done. Each file's first line is "# source <hash>" of the
    // sprites and this builder's Version, so a style is built again when its art changes; the cache folder is named
    // after skin.txt's own hash, so all of them are when the body does.
    //
    // Beards are built by a second one (beard set), with BeardVersion. They are thin and lie on the face, so they differ
    // from hair where the hair's rules assume a mass of its own round the head. There is no back to them (the game never
    // draws a beard from behind, and vanilla has no north sprites). The sprite's rows are fitted to the chibi's face by its
    // eyes and chin (HeightOf), as its chin is short: the head's whole height would put a moustache under the eyes. Each
    // sprite is projected on the head from its own side (NearHair's prisms, BeardAlpha), since a moustache in the side
    // sprite is only a blob at the lip, which would cut away all of it but the middle as the chibi's face curves back from
    // there. It stands a little off the skin (BeardPuff), a long one out past the clothes on the chest (ChestClear), and its
    // outline is its own mesh's: a shell closed round the head would draw a line round all of it.
    internal sealed class HairBuilder
    {
        private const string Tag = "[M3AT] ";
        public const int Version = 32;   // raise when the build changes, so cached hair is made again
        public const int BeardVersion = 4;   // and cached beards
        private static bool pixelsFailed;   // IsoRim's pixel reading has thrown once (logged once)
        public const int Size = 256;    // pixels: every sprite is read at this size, as hair.py's SIZE
        private const int Voxel = 2;    // sprite pixels per voxel side
        private const int Block = 3;    // voxels per surface-nets cell side (6 sprite pixels)
        private const int HeadGrow = 2;   // pixels the head's outline is grown by before it is filled into the hair's
        private const float Round = 2.6f;   // the superellipse power each slice is cut to
        private const int Smooth = 4;       // smoothing passes on the surface-nets mesh (it is smooth already from the shares)
        private const float SmoothFactor = 0.5f;
        // Lengths as hair.py has them, in Blender units on its head HeadWidthBU wide; here shares of the chibi's head.
        private const float HeadWidthBU = 9.517f;
        private const float ClearBU = 0.12f;      // the hair is kept this far off the head
        private const float Tight = 0.6f;         // the gap off the head is scaled by this after texturing ...
        private const float TightReachBU = 3.0f;  // ... up to this far out; past it a bun or ponytail keeps its length
        private const int TightSmooth = 3;
        private const float FaceStretch = 0.2f;   // the hair in front of the head's middle is stretched this much more
                                                  // forward after it is pulled in (StretchForward)
        private const float StretchFade = 0.25f;  // head heights below the chin over which the stretch fades out
        private const float SlideShare = 0.18f;   // how far side hair slides forward round the head, in head depths at the
                                                  // front, less toward the back, before the slowing near the face
                                                  // (StretchForward, SlideAlong: 0.85 of it with the face far off)
        private const float SideFrom = 0.4f;      // sideways share of the head's normal from which hair slides rather
        private const float SideFull = 0.8f;      // than stretches straight forward, and from which it only slides
        private const float FaceTurn = 0.7f;      // forward share of the head's normal at which a slide stops: the face
        private const int MoveSmooth = 8;         // smoothing passes over the moves StretchForward makes
        private const int SlideSteps = 4;         // steps over the slide's length
        private const int SlideRoom = 3;          // the room before the face is looked for this many slides' length ahead
        private const float SkirtReachBU = 2.5f;  // the walls under the open edges reach in this deep at most
        private const float SkirtSinkBU = 0.15f;  // and go this far into the head
        // Off the scalp the hair no longer grows from the head under it but hangs free beside it, so its walls are short:
        // eased from SkirtReach on the scalp's edge to SkirtShort HairlineEase below it, then to SkirtChin FallFade short
        // of FallAway, and down to nothing at FallAway, so they taper off rather than stopping in a step. Below is measured over the head's skin from the nearest scalp vertex (BelowHairline),
        // in head heights, so the line follows the scalp's own shape: high over the forehead, low at the nape.
        private const float SkirtChinBU = 0.5f;   // FallFade short of FallAway, this deep at most (Inward) ...
        private const float SkirtShortBU = 1f;    // ... from this at HairlineEase, about the hair's gap off the head there
        private const float HairlineEase = 0.08f; // head heights below the scalp's edge over which the walls shorten
        private const float FallAway = 0.4f;      // head heights below the scalp's edge from which there are no walls
        private const float FallFade = 0.12f;     // head heights over which they taper to nothing before it
        private const float ChinBlend = 0.1f;     // head heights below the chin over which a wall turns to go straight in
                                                  // at its own height, and hair off the head counts as hanging free
        private const byte Solid = 128;           // least alpha that counts as hair (the game's cut-out, 0.5)
        private const float Cut = 0.9f;           // alpha the mesh is cut at, a little inside the art
        private const float StrandStreak = 0.12f; // how much the strand streaks brighten or darken the hair's colours (Atlas)
        private const int StreakLength = 12;      // sprite rows over which a thin streak changes
        private const int WallSmooth = 6;         // smoothing passes over the walls' reach in along each edge loop
        private const float WallShade = 0.3f;     // the share of an edge loop's colours darker than its walls' one colour,
                                                  // as the inside of the hair is in shade
        private const int SkirtInset = 4;         // pixels in from an open edge its wall looks for its colour
        private const float SkirtDark = 0.3f;     // grey at or below which a pixel is the art's outline
        private const float Crown = 0.6f;         // least upward share of a triangle's normal for the crown
        // The face is hollowed out of the hull this deep behind its front, in head depths (Hull): the head filled into the
        // hull under the face made the locks either side of it one sheet with the head until they left its outline.
        private const float FaceHollow = 0.2f;
        private const float UnderChin = 0.4f;     // head heights above the drawn-over head's chin from which the hull in
                                                  // front of the head's middle takes only the front sprite (Hull), as
                                                  // SkirtShortFrom
        private const int InkReach = 4;           // pixels in from a sprite's edge its outline ink reaches (InkEdges)
        private const int VetoSlack = 6;          // pixels past its outline a sprite's hair still counts when it has a
                                                  // say over a triangle another sprite faces, off the head (Vetoes)
        private const int HairReach = 2;          // voxels round a triangle within which the hair's own hull must be
        private const float Sees = 0.4f;          // least share of a triangle's normal towards a sprite for that sprite
                                                  // to see it, so the triangle is kept only where that sprite has hair
        private const int Outline = 2;            // pixels below a sprite's top outline ink the crown is coloured from
        private const int CrownSpread = 6;        // columns either side whose lowest top edge is used
        private const float CrownBlend = 0.25f;   // upward share of the normal over which the crown panel goes from the
                                                  // sprite its neighbours below take to its blend over the top (Crowned)
        private const float CrownMix = 0.5f;      // share of the head's depth about its middle over which the crown
                                                  // blends from the front sprite to the back one
        private const int CrownGrow = 4;          // texels the crown panel is grown past its triangles, for filtering
        public const int Panels = 4;              // the atlas: the south, east and north sprites, then the crown panel
        private const float NearHead = 0.4f;      // head widths round the hair within which the body is looked at
        private const float EyeRow = 70.6f / 128f * Size;   // the eye line's row in the sprites, as Facial Animation's textures have it
        private const float FaceEyes = 0.31f;     // the chibi's eye line above its chin, in head heights (export_frames.py's FACE_EYES)
        private const float BeardPuffBU = 0.3f;   // how far a beard stands further off the skin than the hair's ClearBU
        private const float ChestClear = 0.02f;   // cells a beard is kept off the chest: past the outermost clothes' layer
        private const float BeardDeep = 0.3f;     // where on the way from the chin to the eyes a beard has all its depth (DepthShare)
        private const float BeardFront = 2f;      // how far round the face the front sprite goes on a beard: a triangle's sideways share over its forward one (2: about 63 degrees)
        private const int WidthRows = 3;          // rows either side the front sprite's width is taken over (FrontWidths)
        private const float FaceSpan = 3f;        // the face texture's width and height, in head heights (export_frames.py's FACE_SPAN)
        private const float FaceEyeV = 1f - 70.6f / 128f;   // the eye line's v in it (export_frames.py's EYE_V)

        // The heads modded hair is drawn over, the first found used: their outlines are filled into the hair's, and their
        // place in the sprite sets where the chibi's head goes.
        private static readonly string[] HeadSprites =
        {
            "Things/Pawn/Humanlike/Heads/Female/Female_Average_Normal",
            "Things/Pawn/Humanlike/Heads/Male/Male_Average_Normal",
        };
        // The heads a beard is drawn over, as most are drawn for a man's.
        private static readonly string[] BeardHeadSprites = { HeadSprites[1], HeadSprites[0] };

        // A hairstyle's south, east and north sprites, Size x Size, rows top first; hash names them for the cache.
        internal sealed class Sprites
        {
            public readonly Color32[][] px = new Color32[3][];
            public string hash;
        }

        // A built style: the files' text and the texture's pixels.
        internal sealed class Built
        {
            public string mesh, shell;   // null: nothing solid enough (written as <style>.none)
            public Color32[] atlas;      // Size * Panels by Size, rows bottom first (as Texture2D.SetPixels32)
            public int triangles, hidden;
        }

        private sealed class Job
        {
            public string style, dir;
            public Sprites sprites;
            public Built built;
            public string error;
            public double ms;
        }

        // The body, as SkinnedFigure has it (rest pose, base shape, game model space, triangles wound outward).
        private readonly Vector3[] body;
        private readonly int[] bodyTris;
        private readonly bool[] onHead;
        private readonly Dictionary<string, Vector3[]> keys;
        private readonly bool reversed;   // the reader turns hair round (SkinnedFigure.turnRound), so it is written the other way
        private readonly string cache;
        private readonly bool beard;      // builds beards rather than hair (the class comment)
        private readonly float[] belowHairline;   // per body vertex, cells over the head's skin from the scalp (BelowHairline)

        // The head: its extent in the game's model space, and where the head the sprites are drawn over is in them.
        private float cx, half, top, chin, back, front;
        private float sx, sy, sz, midX;
        private float eyes;   // a beard's: the chibi's eye line, y
        private int hx0, hx1, hy0, hy1, hz0, hz1;
        private bool[][] headMask;   // per view, Size x Size, top first: the drawn-over head's outline, grown by HeadGrow
        private int[] frontFrom, frontTo;   // a beard's: each row's first and last solid column in the front sprite (BeardAlpha), of the sprites being built
        private bool headTried;
        private bool[][] ink;       // the sprites' outline ink (InkEdges)
        private Color32[] crownPanel;   // the crown seen from above, rows top first (Crowned)
        private Color32[][] veto;   // the sprites' alpha as each has a say over a triangle another faces (Vetoes)

        private readonly Queue<Job> waiting = new Queue<Job>();
        private readonly List<Job> done = new List<Job>();
        private readonly HashSet<string> busy = new HashSet<string>();
        private Thread worker;

        public HairBuilder(Vector3[] body, int[] bodyTris, bool[] onHead, Dictionary<string, Vector3[]> keys, bool reversed,
            Vector3 headBone, string cache, bool beard = false, bool[] scalp = null)
        {
            this.body = body;
            this.bodyTris = bodyTris;
            this.onHead = onHead;
            this.keys = keys;
            this.reversed = reversed;
            this.cache = cache;
            this.beard = beard;
            MeasureHead(headBone);
            belowHairline = beard ? null : Hairline(scalp);
        }

        // Each head vertex's distance over the head's skin (its edges) from the nearest scalp vertex, 0 on the scalp, in
        // cells (Dijkstra; the head is a few hundred vertices). Off the head, or with no scalp, null: Inward then goes by
        // height as it used to, the scalp's edge at HairlineAt above the chin all round.
        private float[] Hairline(bool[] scalp)
        {
            if (scalp == null || scalp.Length != body.Length)
            {
                return null;
            }
            List<int>[] next = new List<int>[body.Length];
            for (int k = 0; k + 2 < bodyTris.Length; k += 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    int a = bodyTris[k + c], b = bodyTris[k + (c + 1) % 3];
                    if (!onHead[a] || !onHead[b])
                    {
                        continue;
                    }
                    (next[a] ?? (next[a] = new List<int>())).Add(b);
                    (next[b] ?? (next[b] = new List<int>())).Add(a);
                }
            }
            float[] dist = new float[body.Length];
            bool[] settled = new bool[body.Length];
            bool any = false;
            for (int i = 0; i < body.Length; i++)
            {
                dist[i] = onHead[i] && scalp[i] ? 0f : float.MaxValue;
                any |= onHead[i] && scalp[i];
            }
            if (!any)
            {
                return null;
            }
            while (true)
            {
                int best = -1;
                for (int i = 0; i < body.Length; i++)
                {
                    if (!settled[i] && dist[i] < float.MaxValue && (best < 0 || dist[i] < dist[best]))
                    {
                        best = i;
                    }
                }
                if (best < 0)
                {
                    break;
                }
                settled[best] = true;
                if (next[best] == null)
                {
                    continue;
                }
                foreach (int j in next[best])
                {
                    float d = dist[best] + (body[j] - body[best]).magnitude;
                    if (d < dist[j])
                    {
                        dist[j] = d;
                    }
                }
            }
            return dist;
        }

        // How far below the scalp's edge the head point q on head triangle t is, in head heights: its corners' distances
        // (Hairline) blended by where q is in the triangle; 0 on the scalp.
        private const float HairlineAt = 0.4f;   // without a scalp, head heights above the chin of the old fixed line
        private float BelowHairline(Tris head, Vector3 q, int t)
        {
            float height = top - chin;
            if (belowHairline == null || t < 0)
            {
                return Mathf.Max(0f, (chin + HairlineAt * height - q.y) / height);
            }
            int k = head.index[t] * 3;
            int ia = bodyTris[k], ib = bodyTris[k + 1], ic = bodyTris[k + 2];
            Vector3 a = body[ia], b = body[ib], c = body[ic];
            Vector3 v0 = b - a, v1 = c - a, v2 = q - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1), den = d00 * d11 - d01 * d01;
            float wb = den > 1e-12f ? Mathf.Clamp01((d11 * d20 - d01 * d21) / den) : 0f;
            float wc = den > 1e-12f ? Mathf.Clamp01((d00 * d21 - d01 * d20) / den) : 0f;
            float wa = Mathf.Max(0f, 1f - wb - wc);
            float da = belowHairline[ia], db = belowHairline[ib], dc = belowHairline[ic];
            if (da == float.MaxValue || db == float.MaxValue || dc == float.MaxValue)
            {
                return FallAway;   // a piece of the head no scalp reaches over its skin
            }
            return (wa * da + wb * db + wc * dc) / (wa + wb + wc) / height;
        }

        public string Cache
        {
            get { return cache; }
        }

        public bool Busy(string style)
        {
            lock (waiting)
            {
                return busy.Contains(style);
            }
        }

        // The chibi head's extent in the head's frame (x its left is -x here, y up, z the way it faces is +z), as hair.py's
        // chibi_head: the chin is the lowest point of the front of the face, not of the neck below it.
        private void MeasureHead(Vector3 headBone)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            back = float.MaxValue;
            front = float.MinValue;
            top = float.MinValue;
            for (int i = 0; i < body.Length; i++)
            {
                if (!onHead[i])
                {
                    continue;
                }
                Vector3 p = body[i];
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                back = Mathf.Min(back, p.z);
                front = Mathf.Max(front, p.z);
                top = Mathf.Max(top, p.y);
            }
            cx = (minX + maxX) / 2f;
            half = (maxX - minX) / 2f;
            chin = float.MaxValue;
            float face = front - headBone.z;
            for (int i = 0; i < body.Length; i++)
            {
                Vector3 p = body[i];
                if (onHead[i] && p.z - headBone.z > 0.6f * face && Mathf.Abs(p.x - cx) < 0.3f * half)
                {
                    chin = Mathf.Min(chin, p.y);
                }
            }
        }

        private float HeadWidths(float bu)
        {
            return bu / HeadWidthBU * 2f * half;
        }

        // ---- main thread

        // A hair texture's three sprites, or null if it lacks one or its pixels can't be read. A missing east sprite is
        // the west one mirrored, as the game draws them. A beard's north one is left empty, whatever the texture has.
        public static Sprites Read(string texPath, bool beard = false)
        {
            Texture2D s = Tex(texPath + "_south"), e = Tex(texPath + "_east"), n = beard ? null : Tex(texPath + "_north");
            bool mirror = false;
            if (e == null)
            {
                e = Tex(texPath + "_west");
                mirror = true;
            }
            if (s == null || e == null || (n == null && !beard))
            {
                return null;
            }
            Sprites sp = new Sprites();
            sp.px[0] = Pixels(s, false);
            sp.px[1] = Pixels(e, mirror);
            sp.px[2] = beard ? new Color32[Size * Size] : Pixels(n, false);
            if (sp.px[0] == null || sp.px[1] == null || sp.px[2] == null)
            {
                return null;
            }
            sp.hash = Hash(sp.px, beard);
            return sp;
        }

        // Sprites from pixels already read (Size x Size, rows top first: south, east, north).
        internal static Sprites From(Color32[] south, Color32[] east, Color32[] north, bool beard = false)
        {
            Sprites sp = new Sprites();
            sp.px[0] = south;
            sp.px[1] = east;
            sp.px[2] = north;
            sp.hash = Hash(sp.px, beard);
            return sp;
        }

        private static Texture2D Tex(string path)
        {
            return ContentFinder<Texture2D>.Get(path, false);
        }

        // The sprite at Size x Size, top row first (mirrored left to right if mirror).
        private static Color32[] Pixels(Texture2D tex, bool mirror)
        {
            // IsoRim reads pixels through the game engine's own texture types, so a game update can break it until IsoRim
            // is rebuilt. Then the hair is left off rather than taking the whole figure down with it.
            Color32[] px;
            try
            {
                px = Api.ReadSpritePixels(tex, Size);
            }
            catch (Exception e)
            {
                if (!pixelsFailed)
                {
                    pixelsFailed = true;
                    Log.Warning(Tag + "IsoRim could not read the hair sprites, so hairstyles without a file are left off: " + e.Message);
                }
                return null;
            }
            if (px == null || px.Length < Size * Size)
            {
                return null;
            }
            Color32[] top = new Color32[Size * Size];
            for (int r = 0; r < Size; r++)
            {
                for (int c = 0; c < Size; c++)
                {
                    top[r * Size + c] = px[(Size - 1 - r) * Size + (mirror ? Size - 1 - c : c)];   // read rows bottom first
                }
            }
            return top;
        }

        private static string Hash(Color32[][] views, bool beard)
        {
            ulong h = 14695981039346656037UL;
            Action<byte> add = b => { h ^= b; h *= 1099511628211UL; };
            add((byte)(beard ? BeardVersion : Version));
            foreach (Color32[] v in views)
            {
                foreach (Color32 c in v)
                {
                    add(c.r);
                    add(c.g);
                    add(c.b);
                    add(c.a);
                }
            }
            return h.ToString("x16");
        }

        // Reads the head the sprites are drawn over (once). False if none of HeadSprites can be read.
        private bool HeadReady()
        {
            if (headTried)
            {
                return headMask != null;
            }
            headTried = true;
            foreach (string path in beard ? BeardHeadSprites : HeadSprites)
            {
                Sprites head = Read(path);
                if (head != null && UseHead(head))
                {
                    Log.Message(Tag + (beard ? "beards are" : "modded hair is") + " fitted to the head drawn by " + path + " (columns " + hx0 + "-" + hx1 + ", rows " + hy0 + "-" + hy1 + ").");
                    return true;
                }
            }
            Log.Warning(Tag + "none of the game's heads could be read, so " + What + "s without a file are left off.");
            return false;
        }

        // Takes head as the head the sprites are drawn over: its outlines and its place in them. False if it is empty.
        internal bool UseHead(Sprites head)
        {
            headTried = true;
            bool[][] mask = new bool[3][];
            for (int v = 0; v < 3; v++)
            {
                bool[] m = new bool[Size * Size];
                for (int i = 0; i < m.Length; i++)
                {
                    m[i] = head.px[v][i].a >= Solid;
                }
                mask[v] = m;
            }
            int ey0, ey1;
            if (!Box(mask[0], out hx0, out hx1, out hy0, out hy1) || !Box(mask[1], out hz0, out hz1, out ey0, out ey1))
            {
                return false;
            }
            for (int v = 0; v < 3; v++)
            {
                mask[v] = Grow(mask[v], HeadGrow);
            }
            headMask = mask;
            sx = 2f * half / (hx1 - hx0);
            sy = (top - chin) / (hy1 - hy0);
            sz = (front - back) / (hz1 - hz0);
            midX = (hx0 + hx1) / 2f;
            eyes = chin + FaceEyes * (top - chin);
            return true;
        }

        private static bool Box(bool[] m, out int c0, out int c1, out int r0, out int r1)
        {
            c0 = r0 = int.MaxValue;
            c1 = r1 = -1;
            for (int r = 0; r < Size; r++)
            {
                for (int c = 0; c < Size; c++)
                {
                    if (m[r * Size + c])
                    {
                        c0 = Math.Min(c0, c);
                        c1 = Math.Max(c1, c);
                        r0 = Math.Min(r0, r);
                        r1 = Math.Max(r1, r);
                    }
                }
            }
            return c1 > c0 && r1 > r0;
        }

        private static bool[] Grow(bool[] m, int r)
        {
            bool[] g = (bool[])m.Clone();
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (!m[y * Size + x])
                    {
                        continue;
                    }
                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int yy = y + dy, xx = x + dx;
                            if (dx * dx + dy * dy <= r * r && yy >= 0 && yy < Size && xx >= 0 && xx < Size)
                            {
                                g[yy * Size + xx] = true;
                            }
                        }
                    }
                }
            }
            return g;
        }

        private string What
        {
            get { return beard ? "beard" : "hairstyle"; }
        }

        // Queues a style to be built into the cache folder dir (main thread). False if it can't be (no head to fit to).
        public bool Start(string style, Sprites sprites, string dir)
        {
            if (!HeadReady())
            {
                return false;
            }
            lock (waiting)
            {
                if (!busy.Add(style))
                {
                    return true;
                }
                waiting.Enqueue(new Job { style = style, sprites = sprites, dir = dir });
                if (worker == null)
                {
                    worker = new Thread(Work) { IsBackground = true, Name = beard ? "Maru beard builder" : "Maru hair builder" };
                    worker.Start();
                }
            }
            return true;
        }

        // Writes the styles built since last time into the cache (main thread); returns them.
        public List<string> Finish()
        {
            List<Job> jobs;
            lock (waiting)
            {
                if (done.Count == 0)
                {
                    return null;
                }
                jobs = new List<Job>(done);
                done.Clear();
            }
            List<string> styles = new List<string>();
            foreach (Job j in jobs)
            {
                try
                {
                    string path = Path.Combine(j.dir, j.style);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (j.error != null)
                    {
                        Log.Warning(Tag + "could not build the " + What + " " + j.style + " from its sprites, it is left off: " + j.error);
                    }
                    else if (j.built.mesh == null)
                    {
                        File.WriteAllText(path + ".none", "# source " + j.sprites.hash + "\n# nothing at alpha 0.5 or more\n");
                        Log.Message(Tag + "the " + What + " " + j.style + " has nothing solid enough to build, so it is left off.");
                    }
                    else
                    {
                        Texture2D atlas = new Texture2D(Panels * Size, Size, TextureFormat.RGBA32, false);
                        atlas.SetPixels32(j.built.atlas);
                        atlas.Apply();
                        File.WriteAllBytes(path + ".png", atlas.EncodeToPNG());
                        UnityEngine.Object.Destroy(atlas);
                        if (j.built.shell != null)
                        {
                            File.WriteAllText(path + ".shell.txt", j.built.shell);
                        }
                        File.WriteAllText(path + ".txt", j.built.mesh);   // last: the one SkinnedFigure looks for
                        Log.Message(Tag + "built the " + What + " " + j.style + " from its sprites in " + j.ms.ToString("0", CultureInfo.InvariantCulture)
                            + " ms: " + j.built.triangles + " triangles" + (j.built.shell != null ? ", its shell hides " + j.built.hidden + " of the body's." : "."));
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(Tag + "could not save the " + What + " " + j.style + ": " + e.Message);
                }
                lock (waiting)
                {
                    busy.Remove(j.style);
                }
                styles.Add(j.style);
            }
            return styles;
        }

        // ---- background thread

        private void Work()
        {
            while (true)
            {
                Job j;
                lock (waiting)
                {
                    if (waiting.Count == 0)
                    {
                        worker = null;
                        return;
                    }
                    j = waiting.Dequeue();
                }
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    j.built = Build(j.style, j.sprites);
                }
                catch (Exception e)
                {
                    j.error = e.GetType().Name + ": " + e.Message;
                }
                j.ms = watch.Elapsed.TotalMilliseconds;
                lock (waiting)
                {
                    done.Add(j);
                }
            }
        }

        // ---- sprite space: X the south sprite's column (the figure's left), Y its row (down), Z the east sprite's column
        // (towards the face). Game model space: +x the figure's right, +y up, +z the way it faces.

        private Vector3 ToGame(float X, float Y, float Z)
        {
            return new Vector3(cx - (X - midX) * sx, HeightOf(Y), back + (Z - hz0) * sz);
        }

        // A sprite row's height on the chibi: down the head at one scale, a beard's by the eyes and the chin (the class
        // comment), and below the chin at the head's scale, so a long one keeps its length.
        private float HeightOf(float Y)
        {
            if (!beard)
            {
                return top - (Y - hy0) * sy;
            }
            if (Y <= EyeRow)
            {
                return top - (Y - hy0) * (top - eyes) / (EyeRow - hy0);
            }
            return Y <= hy1 ? eyes - (Y - EyeRow) * (eyes - chin) / (hy1 - EyeRow) : chin - (Y - hy1) * sy;
        }

        private float RowOf(float y)
        {
            if (!beard)
            {
                return hy0 + (top - y) / sy;
            }
            if (y >= eyes)
            {
                return hy0 + (top - y) * (EyeRow - hy0) / (top - eyes);
            }
            return y >= chin ? EyeRow + (eyes - y) * (hy1 - EyeRow) / (eyes - chin) : hy1 + (chin - y) / sy;
        }

        private void ToSprite(Vector3 p, out float X, out float Y, out float Z)
        {
            X = midX + (cx - p.x) / sx;
            Y = RowOf(p.y);
            Z = hz0 + (p.z - back) / sz;
        }

        // A beard's south sprite laid flat: a texSize square texture in Facial Animation's layout, which the face patch's own
        // texture places pick out (export_frames.py's find_face: u = 0.5 + x / (FACE_SPAN * height), v = EYE_V + (y - chin -
        // FACE_EYES * height) / (FACE_SPAN * height), with x the figure's left, so the game's x is minus). Each texel is taken
        // back to the chibi's face and from there to the sprite pixel the 3D beard would put on it (HeightOf, ToSprite), so a
        // flat beard is the same beard. The chin is the lowest point of the head as the exporter takes it, not the front of
        // the face as MeasureHead does (the face patch stops there). Null if the head can't be read. The result is bottom row
        // first, as a texture's pixels are; the sprite's pixels are read top row first.
        internal Color32[] FlatFace(Sprites sp, int texSize)
        {
            if (!HeadReady())
            {
                return null;
            }
            float chinAll = float.MaxValue, topAll = float.MinValue;
            for (int i = 0; i < body.Length; i++)
            {
                if (onHead[i])
                {
                    chinAll = Mathf.Min(chinAll, body[i].y);
                    topAll = Mathf.Max(topAll, body[i].y);
                }
            }
            float span = FaceSpan * (topAll - chinAll);
            float eyeY = chinAll + FaceEyes * (topAll - chinAll);
            Color32[] px = new Color32[texSize * texSize];
            for (int r = 0; r < texSize; r++)
            {
                float y = eyeY + ((r + 0.5f) / texSize - FaceEyeV) * span;
                float Y = y >= eyeY
                    ? EyeRow - (y - eyeY) * (EyeRow - hy0) / (topAll - eyeY)
                    : EyeRow + (eyeY - y) * (hy1 - EyeRow) / (eyeY - chinAll);
                int row = Mathf.RoundToInt(Y);
                for (int i = 0; i < texSize; i++)
                {
                    float x = (0.5f - (i + 0.5f) / texSize) * span;
                    int col = Mathf.RoundToInt(midX + (cx - x) / sx);
                    bool inside = col >= 0 && col < Size && row >= 0 && row < Size;
                    px[r * texSize + i] = inside ? sp.px[0][row * Size + col] : new Color32(0, 0, 0, 0);
                }
            }
            return px;
        }

        // Builds a style (any thread, once UseHead has been given the head).
        internal Built Build(string style, Sprites sp)
        {
            Built built = new Built();
            bool[] nearHair;
            if (beard)
            {
                FrontWidths(sp);
            }
            Mesh shell = Hull(sp, out nearHair);
            if (shell.t.Count == 0)
            {
                return built;
            }
            // The body near the hair (all of it that the hair could touch), and the head's own part, as hair.py's trees.
            Vector3 lo = shell.v[0], hi = shell.v[0];
            foreach (Vector3 p in shell.v)
            {
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            float margin = NearHead * 2f * half;
            lo -= Vector3.one * margin;
            hi += Vector3.one * margin;
            List<int> near = new List<int>(), headTris = new List<int>(), chestTris = new List<int>();
            for (int k = 0; k + 2 < bodyTris.Length; k += 3)
            {
                int a = bodyTris[k], b = bodyTris[k + 1], c = bodyTris[k + 2];
                if (onHead[a] && onHead[b] && onHead[c])
                {
                    headTris.Add(k / 3);
                }
                if (Inside(body[a], lo, hi) || Inside(body[b], lo, hi) || Inside(body[c], lo, hi))
                {
                    near.Add(k / 3);
                    if (beard && !onHead[a] && !onHead[b] && !onHead[c] && body[a].y < chin && body[b].y < chin && body[c].y < chin)
                    {
                        chestTris.Add(k / 3);
                    }
                }
            }
            Tris bodyNear = new Tris(body, bodyTris, near), head = new Tris(body, bodyTris, headTris);
            Tris chest = beard ? new Tris(body, bodyTris, chestTris) : null;
            float clear = HeadWidths(ClearBU);

            ClearOf(shell, bodyNear, clear);
            ClearOfChest(shell, chest);
            shell.Normals();
            CutMesh cut = Textured(shell, sp, nearHair);
            if (cut.kept.Count == 0)
            {
                return built;   // nothing solid enough (Shaved): written as <style>.none
            }
            PullIn(shell, head, bodyNear, clear);
            if (!beard)
            {
                StretchForward(shell, head, bodyNear, clear);
            }
            if (beard)
            {
                Puff(shell, cut);
                ClearOfChest(shell, chest);
                shell.Normals();
            }
            Vector3[] at, normal;
            cut.Resolved(out at, out normal);
            built.atlas = Atlas(sp, crownPanel);
            built.mesh = WriteHair(sp, style, cut, at, normal, head);
            built.triangles = cut.kept.Count;
            if (beard)
            {
                return built;
            }
            built.shell = WriteShell(sp.hash, style, shell, cut, bodyNear, head, clear, out built.hidden);
            return built;
        }

        private static bool Inside(Vector3 p, Vector3 lo, Vector3 hi)
        {
            return p.x >= lo.x && p.y >= lo.y && p.z >= lo.z && p.x <= hi.x && p.y <= hi.y && p.z <= hi.z;
        }

        // ---- the hull

        // A triangle mesh with smooth vertex normals and face normals (outward).
        private sealed class Mesh
        {
            public readonly List<Vector3> v = new List<Vector3>();
            public readonly List<int> t = new List<int>();
            public Vector3[] vn, fn;
            public int[][] next;   // each vertex's neighbours

            public void Normals()
            {
                vn = new Vector3[v.Count];
                fn = new Vector3[t.Count / 3];
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    Vector3 f = Vector3.Cross(v[t[k + 1]] - v[t[k]], v[t[k + 2]] - v[t[k]]);
                    vn[t[k]] += f;
                    vn[t[k + 1]] += f;
                    vn[t[k + 2]] += f;
                    fn[k / 3] = f.normalized;
                }
                for (int i = 0; i < vn.Length; i++)
                {
                    vn[i] = vn[i].normalized;
                }
            }

            public void Link()
            {
                List<HashSet<int>> sets = new List<HashSet<int>>();
                for (int i = 0; i < v.Count; i++)
                {
                    sets.Add(new HashSet<int>());
                }
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        int a = t[k + e], b = t[k + (e + 1) % 3];
                        sets[a].Add(b);
                        sets[b].Add(a);
                    }
                }
                next = new int[v.Count][];
                for (int i = 0; i < v.Count; i++)
                {
                    next[i] = new int[sets[i].Count];
                    sets[i].CopyTo(next[i]);
                }
            }

            // Laplacian smoothing: each vertex moved factor of the way to its neighbours' middle, all at once.
            public void Smooth(int passes, float factor)
            {
                Vector3[] moved = new Vector3[v.Count];
                for (int pass = 0; pass < passes; pass++)
                {
                    for (int i = 0; i < v.Count; i++)
                    {
                        if (next[i].Length == 0)
                        {
                            moved[i] = v[i];
                            continue;
                        }
                        Vector3 sum = Vector3.zero;
                        foreach (int n in next[i])
                        {
                            sum += v[n];
                        }
                        moved[i] = Vector3.Lerp(v[i], sum / next[i].Length, factor);
                    }
                    for (int i = 0; i < v.Count; i++)
                    {
                        v[i] = moved[i];
                    }
                }
            }
        }

        // The voxels within r of one that is set (a box r voxels each way), one axis at a time.
        private static bool[] Grown(bool[] a, int n, int r)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                int step = axis == 0 ? n * n : axis == 1 ? n : 1;
                bool[] b = new bool[a.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    if (!a[i])
                    {
                        continue;
                    }
                    int at = i / step % n;
                    for (int d = Math.Max(-r, -at); d <= r && at + d < n; d++)
                    {
                        b[i + d * step] = true;
                    }
                }
                a = b;
            }
            return a;
        }

        // Marks the voxels of slice y inside both outlines (across: the front view's columns, deep: the side view's) and
        // inside the superellipse that fits their width and depth.
        private static void Slice(bool[] inside, int n, int y, bool[] across, bool[] deep)
        {
            int x0 = -1, x1 = -1, z0 = -1, z1 = -1;
            for (int x = 0; x < n; x++)
            {
                if (across[y * n + x])
                {
                    if (x0 < 0)
                    {
                        x0 = x;
                    }
                    x1 = x;
                }
                if (deep[y * n + x])
                {
                    if (z0 < 0)
                    {
                        z0 = x;
                    }
                    z1 = x;
                }
            }
            if (x0 < 0 || z0 < 0)
            {
                return;
            }
            float mx = (x0 + x1) / 2f, rx = (x1 - x0) / 2f + 0.5f, mz = (z0 + z1) / 2f, rz = (z1 - z0) / 2f + 0.5f;
            for (int x = x0; x <= x1; x++)
            {
                if (!across[y * n + x])
                {
                    continue;
                }
                float dx = Mathf.Pow(Mathf.Abs(x - mx) / rx, Round);
                for (int z = z0; z <= z1; z++)
                {
                    if (deep[y * n + z] && dx + Mathf.Pow(Mathf.Abs(z - mz) / rz, Round) <= 1f)
                    {
                        inside[(y * n + x) * n + z] = true;
                    }
                }
            }
        }

        // The voxels of the hair's own visual hull and of the head it is drawn over, each cut slice by slice to a
        // superellipse, meshed by surface nets on blocks of Block voxels: each block's share of voxels inside is the
        // field, and the surface runs where it crosses a half.
        //
        // Outside the head a voxel needs hair in the side sprite and in the front or back one. Filling the head into each
        // outline instead (as hair.py did, so the face's hole in the front sprite wouldn't tunnel through the head) made
        // a block at the corners of the face that neither sprite has hair at, whose side the side sprite then shows as
        // bare face: a bald patch at the temple with a strip of hair between it and the face.
        // nearHair: the voxels within HairReach of the hair's own hull (Textured keeps only triangles there).
        private Mesh Hull(Sprites sp, out bool[] nearHair)
        {
            int n = Size / Voxel;
            bool[] inside = new bool[n * n * n];   // [y, x, z]
            bool[] hair = new bool[n * n * n];
            // Hair: the front or the back sprite (each leaves out what the body hides from it, as a braid down the back
            // is not drawn in the front one), and the side one. The head: its outline in the front and side ones.
            bool[] s = new bool[n * n], e = new bool[n * n], hs = new bool[n * n], he = new bool[n * n];
            bool[] south = new bool[n * n], north = new bool[n * n];
            for (int y = 0; y < n; y++)
            {
                int row = y * Voxel + Voxel / 2;
                for (int x = 0; x < n; x++)
                {
                    int col = x * Voxel + Voxel / 2, i = row * Size + col;
                    south[y * n + x] = sp.px[0][i].a >= Solid;
                    north[y * n + x] = sp.px[2][row * Size + Size - 1 - col].a >= Solid;
                    s[y * n + x] = south[y * n + x] || north[y * n + x];
                    e[y * n + x] = sp.px[1][i].a >= Solid;
                    hs[y * n + x] = headMask[0][i];
                    he[y * n + x] = headMask[1][i];
                }
            }
            for (int y = 0; y < n; y++)
            {
                Slice(hair, n, y, s, e);
                Slice(inside, n, y, hs, he);
            }
            // The face hollowed out: in the columns where the front sprite shows the head with no hair over it, the head
            // and the hair are taken out from in front of the face to FaceHollow behind its front. Filled in, a lock
            // lying over the head's outline in the front sprite had no inside of its own, only its share of one shell
            // over the head, so the cut along the art left it a sheet with a wall under its edge; with the hollow it
            // has an inside face, which faces the side sprite and so takes its art. Only so deep, so the face's hole
            // doesn't tunnel through the head (why hair.py filled the head in).
            if (!beard)
            {
                int depth = Mathf.Max(1, Mathf.RoundToInt(FaceHollow * (hz1 - hz0) / Voxel));
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        if (!hs[y * n + x] || south[y * n + x])
                        {
                            continue;
                        }
                        int zFront = -1;
                        for (int z = n - 1; z >= 0 && zFront < 0; z--)
                        {
                            if (inside[(y * n + x) * n + z])
                            {
                                zFront = z;
                            }
                        }
                        if (zFront < 0)
                        {
                            continue;
                        }
                        for (int z = Math.Max(0, zFront - depth + 1); z < n; z++)
                        {
                            inside[(y * n + x) * n + z] = false;
                            hair[(y * n + x) * n + z] = false;
                        }
                    }
                }
            }
            // From where the skull curves in toward the jaw down, the hair in front of the head's middle is only where
            // the front sprite has it. The back sprite's hair down the back spans the whole width, and with the side
            // sprite's locks hanging in front of the shoulders it filled the slice right across under the chin: a band
            // joining the two locks in front of the neck, like a beard. So the locks hang apart, with the neck and
            // chest between them.
            if (!beard)
            {
                int fromY = Mathf.RoundToInt((hy1 - UnderChin * (hy1 - hy0)) / Voxel);
                int midZ = Mathf.RoundToInt((hz0 + hz1) / 2f / Voxel);
                for (int y = Math.Max(0, fromY); y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        if (south[y * n + x])
                        {
                            continue;
                        }
                        for (int z = midZ; z < n; z++)
                        {
                            hair[(y * n + x) * n + z] = false;
                        }
                    }
                }
                // Behind the head's middle, at any height, the hair is only where the back sprite has it (if it has any
                // in that row). The front sprite is often wider than the back one, its locks spread out either side of
                // the face, and with its width the hull's back corners stood out past where the back sprite has any
                // hair: they took the side sprite's outline ink, or were cut away as holes (LongB's back right).
                for (int y = 0; y < n; y++)
                {
                    bool any = false;
                    for (int x = 0; x < n && !any; x++)
                    {
                        any = north[y * n + x];
                    }
                    if (!any)
                    {
                        continue;
                    }
                    for (int x = 0; x < n; x++)
                    {
                        if (north[y * n + x])
                        {
                            continue;
                        }
                        for (int z = 0; z < midZ; z++)
                        {
                            hair[(y * n + x) * n + z] = false;
                        }
                    }
                }
            }
            for (int i = 0; i < inside.Length; i++)
            {
                inside[i] |= hair[i];
            }
            nearHair = Grown(beard ? Prisms(s, e, n) : hair, n, HairReach);
            // Each block's share inside, with an empty block all round.
            int b = (n + Block - 1) / Block, g = b + 2;
            float[] share = new float[g * g * g];
            for (int i = 1; i <= b; i++)
            {
                for (int k = 1; k <= b; k++)
                {
                    for (int l = 1; l <= b; l++)
                    {
                        int count = 0;
                        for (int dy = 0; dy < Block; dy++)
                        {
                            int y = (i - 1) * Block + dy;
                            for (int dx = 0; dx < Block; dx++)
                            {
                                int x = (k - 1) * Block + dx;
                                for (int dz = 0; dz < Block; dz++)
                                {
                                    int z = (l - 1) * Block + dz;
                                    if (y < n && x < n && z < n && inside[(y * n + x) * n + z])
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                        share[(i * g + k) * g + l] = count / (float)(Block * Block * Block);
                    }
                }
            }
            return SurfaceNets(share, g);
        }

        // The voxels straight behind the art of the front sprite or of the side one, from either side.
        private static bool[] Prisms(bool[] across, bool[] deep, int n)
        {
            bool[] a = new bool[n * n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    for (int z = 0; z < n; z++)
                    {
                        a[(y * n + x) * n + z] = across[y * n + x] || deep[y * n + z];
                    }
                }
            }
            return a;
        }

        // Block i's middle, in sprite pixels.
        private static float BlockPixel(float i)
        {
            return (i - 1f) * Block * Voxel + Block * Voxel / 2f;
        }

        private Mesh SurfaceNets(float[] share, int g)
        {
            Mesh m = new Mesh();
            int[] vertexOf = new int[g * g * g];
            for (int i = 0; i < vertexOf.Length; i++)
            {
                vertexOf[i] = -1;
            }
            Func<int, int, int, int> at = (y, x, z) => (y * g + x) * g + z;
            // One vertex per cell (between 8 samples) the surface crosses: the middle of the crossings on its 12 edges.
            for (int y = 0; y + 1 < g; y++)
            {
                for (int x = 0; x + 1 < g; x++)
                {
                    for (int z = 0; z + 1 < g; z++)
                    {
                        Vector3 sum = Vector3.zero;
                        int crossings = 0;
                        for (int c = 0; c < 8; c++)
                        {
                            int ay = (c >> 2) & 1, ax = (c >> 1) & 1, az = c & 1;
                            float d0 = share[at(y + ay, x + ax, z + az)];
                            for (int axis = 0; axis < 3; axis++)
                            {
                                int by = ay, bx = ax, bz = az;
                                if (axis == 0) { if (ay == 1) continue; by = 1; }
                                if (axis == 1) { if (ax == 1) continue; bx = 1; }
                                if (axis == 2) { if (az == 1) continue; bz = 1; }
                                float d1 = share[at(y + by, x + bx, z + bz)];
                                if ((d0 >= 0.5f) == (d1 >= 0.5f))
                                {
                                    continue;
                                }
                                float t = (0.5f - d0) / (d1 - d0);
                                sum += new Vector3(x + ax + (bx - ax) * t, y + ay + (by - ay) * t, z + az + (bz - az) * t);
                                crossings++;
                            }
                        }
                        if (crossings == 0)
                        {
                            continue;
                        }
                        Vector3 p = sum / crossings;   // (x, y, z) in samples
                        vertexOf[at(y, x, z)] = m.v.Count;
                        m.v.Add(ToGame(BlockPixel(p.x), BlockPixel(p.y), BlockPixel(p.z)));
                    }
                }
            }
            // One quad per sample edge the surface crosses, joining the four cells round it, facing out of the inside.
            for (int y = 1; y + 1 < g; y++)
            {
                for (int x = 1; x + 1 < g; x++)
                {
                    for (int z = 1; z + 1 < g; z++)
                    {
                        bool inHere = share[at(y, x, z)] >= 0.5f;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int ny = y + (axis == 0 ? 1 : 0), nx = x + (axis == 1 ? 1 : 0), nz = z + (axis == 2 ? 1 : 0);
                            if (inHere == (share[at(ny, nx, nz)] >= 0.5f))
                            {
                                continue;
                            }
                            int[] quad = new int[4];
                            bool whole = true;
                            for (int q = 0; q < 4; q++)
                            {
                                int u = q == 1 || q == 2 ? 1 : 0, w = q >= 2 ? 1 : 0;
                                int cy = y, cxx = x, cz = z;
                                if (axis == 0) { cxx -= u; cz -= w; }
                                if (axis == 1) { cy -= u; cz -= w; }
                                if (axis == 2) { cy -= u; cxx -= w; }
                                quad[q] = vertexOf[at(cy, cxx, cz)];
                                whole &= quad[q] >= 0;
                            }
                            if (!whole)
                            {
                                continue;
                            }
                            Vector3 outward = ToGame(BlockPixel(nx), BlockPixel(ny), BlockPixel(nz)) - ToGame(BlockPixel(x), BlockPixel(y), BlockPixel(z));
                            if (!inHere)
                            {
                                outward = -outward;
                            }
                            Vector3 p0 = m.v[quad[0]], p1 = m.v[quad[1]], p2 = m.v[quad[2]], p3 = m.v[quad[3]];
                            if (Vector3.Dot(Vector3.Cross(p2 - p0, p3 - p1), outward) < 0f)
                            {
                                // The other way round, from the same corner.
                                int k = quad[1];
                                quad[1] = quad[3];
                                quad[3] = k;
                                Vector3 pk = p1;
                                p1 = p3;
                                p3 = pk;
                            }
                            // Split along the shorter diagonal.
                            if ((p2 - p0).sqrMagnitude <= (p3 - p1).sqrMagnitude)
                            {
                                m.t.AddRange(new[] { quad[0], quad[1], quad[2], quad[0], quad[2], quad[3] });
                            }
                            else
                            {
                                m.t.AddRange(new[] { quad[0], quad[1], quad[3], quad[1], quad[2], quad[3] });
                            }
                        }
                    }
                }
            }
            m.Link();
            m.Smooth(Smooth, SmoothFactor);
            m.Normals();
            return m;
        }

        // ---- the body

        // Triangles to find the nearest point on: their corners, outward normals and bounding spheres.
        private sealed class Tris
        {
            private readonly Vector3[] a, b, c, n, mid;
            private readonly float[] radius;
            public readonly int[] index;   // the triangle's number in its own mesh

            public Tris(IList<Vector3> v, IList<int> t, List<int> which)
            {
                int count = which.Count;
                a = new Vector3[count];
                b = new Vector3[count];
                c = new Vector3[count];
                n = new Vector3[count];
                mid = new Vector3[count];
                radius = new float[count];
                index = which.ToArray();
                for (int i = 0; i < count; i++)
                {
                    int k = which[i] * 3;
                    a[i] = v[t[k]];
                    b[i] = v[t[k + 1]];
                    c[i] = v[t[k + 2]];
                    n[i] = Vector3.Cross(b[i] - a[i], c[i] - a[i]).normalized;
                    mid[i] = (a[i] + b[i] + c[i]) / 3f;
                    radius[i] = Mathf.Sqrt(Mathf.Max((a[i] - mid[i]).sqrMagnitude, Mathf.Max((b[i] - mid[i]).sqrMagnitude, (c[i] - mid[i]).sqrMagnitude)));
                }
            }

            public int Count
            {
                get { return a.Length; }
            }

            // The nearest point on any of them to p, that triangle's normal and its place in the list; false if empty.
            public bool Nearest(Vector3 p, out Vector3 q, out Vector3 normal, out int which)
            {
                float best = float.MaxValue;
                q = p;
                normal = Vector3.up;
                which = -1;
                for (int i = 0; i < a.Length; i++)
                {
                    float d = (p - mid[i]).magnitude - radius[i];
                    if (d > 0f && d * d >= best)
                    {
                        continue;
                    }
                    Vector3 on = Closest(p, a[i], b[i], c[i]);
                    float dd = (p - on).sqrMagnitude;
                    if (dd < best)
                    {
                        best = dd;
                        q = on;
                        normal = n[i];
                        which = i;
                    }
                }
                return which >= 0;
            }

            public void Corners(int i, out Vector3 pa, out Vector3 pb, out Vector3 pc)
            {
                pa = a[i];
                pb = b[i];
                pc = c[i];
            }

            // The point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5).
            private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 ab = b - a, ac = c - a, ap = p - a;
                float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f) return a;
                Vector3 bp = p - b;
                float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0f && d4 <= d3) return b;
                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
                Vector3 cp = p - c;
                float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0f && d5 <= d6) return c;
                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
                float denom = 1f / (va + vb + vc);
                return a + ab * (vb * denom) + ac * (vc * denom);
            }
        }

        // Keeps every vertex at least clear off the body, along the normal of the body under it (hair.py clear_of_head).
        private static void ClearOf(Mesh m, Tris near, float clear)
        {
            for (int i = 0; i < m.v.Count; i++)
            {
                Vector3 q, n;
                int t;
                if (near.Nearest(m.v[i], out q, out n, out t) && Vector3.Dot(m.v[i] - q, n) < clear)
                {
                    m.v[i] = q + n * clear;
                }
            }
        }

        // Keeps a beard's vertices below the chin ChestClear off the chest (chest null: hair, none).
        private void ClearOfChest(Mesh m, Tris chest)
        {
            if (chest == null)
            {
                return;
            }
            for (int i = 0; i < m.v.Count; i++)
            {
                Vector3 q, n;
                int t;
                if (m.v[i].y < chin && chest.Nearest(m.v[i], out q, out n, out t) && Vector3.Dot(m.v[i] - q, n) < ChestClear)
                {
                    m.v[i] = q + n * ChestClear;
                }
            }
        }

        // How much of its depth off the head a beard keeps at height y: none from the eyes up, so a sideburn lies on the
        // skin (the head the sprites are drawn over is wider there than the chibi's), growing to all of it at BeardDeep of
        // the way up from the chin.
        private float DepthShare(float y)
        {
            return Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(chin + BeardDeep * (eyes - chin), eyes, y));
        }

        // Stands the vertices with beard on them off the skin; the cuts along the art's edge, part way along an edge to a
        // vertex left where it was, come down to it, so the beard tapers to its edge.
        private void Puff(Mesh m, CutMesh cut)
        {
            bool[] on = new bool[m.v.Count];
            foreach (int[] t in cut.kept)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (t[k] < on.Length)
                    {
                        on[t[k]] = true;
                    }
                }
            }
            float puff = HeadWidths(BeardPuffBU);
            for (int i = 0; i < on.Length; i++)
            {
                if (on[i])
                {
                    m.v[i] += m.vn[i] * puff;
                }
            }
        }

        // Brings the hair forward toward the face, then keeps it clear of the body again. The sprites' head has a narrower
        // face than the chibi's, which is broad and flat, so hair pulled in onto the chibi's head follows its corners back
        // round the side: the fringe and the locks framing the face came to lie round the eyes, and the side locks ended
        // further back than the side sprite has them. Hair over the front of the head (the fringe) is stretched straight
        // forward from the head's middle, FaceStretch more the further forward it is, standing it off the forehead. Hair
        // on the sides is slid forward round the head instead (SlideAlong, slowing as it nears the face), keeping its gap off the skin, as a straight
        // push there only lifted it off the side to be pushed back out where it was. The two are blended by how much the
        // head under a vertex faces sideways (SideFrom to SideFull). Both fade out below the chin, as hair hanging there
        // is clear of the face.
        private void StretchForward(Mesh m, Tris head, Tris near, float clear)
        {
            float mid = (back + front) / 2f, depth = front - back, fade = StretchFade * (top - chin);
            Vector3[] moved = new Vector3[m.v.Count];
            for (int i = 0; i < m.v.Count; i++)
            {
                Vector3 p = m.v[i];
                float w = Mathf.Clamp01(1f - (chin - p.y) / fade);
                Vector3 straight = p;
                if (p.z > mid)
                {
                    straight.z = mid + (p.z - mid) * (1f + FaceStretch * w);
                }
                moved[i] = straight;
                Vector3 q, n;
                int t;
                if (w <= 0f || !head.Nearest(p, out q, out n, out t))
                {
                    continue;
                }
                float side = Mathf.Clamp01((Mathf.Abs(n.x) - SideFrom) / (SideFull - SideFrom));
                if (side <= 0f)
                {
                    continue;
                }
                float along = SlideShare * depth * w * Mathf.Clamp01((p.z - back) / depth);
                moved[i] = Vector3.Lerp(straight, SlideAlong(head, p, q, n, along), side);
            }
            // Each vertex's move smoothed over its neighbours' before it is made: the head triangle nearest a vertex, whose
            // normal sets how much it slides and which way, jumps from one neighbour to the next (onto an ear, under the
            // jaw), and moved one by one some went several head widths' tenths further than those round them, as spikes
            // and a ragged edge where the side locks meet the face.
            Vector3[] move = new Vector3[m.v.Count], smoothed = new Vector3[m.v.Count];
            for (int i = 0; i < m.v.Count; i++)
            {
                move[i] = moved[i] - m.v[i];
            }
            for (int pass = 0; pass < MoveSmooth; pass++)
            {
                for (int i = 0; i < m.v.Count; i++)
                {
                    if (m.next[i].Length == 0)
                    {
                        smoothed[i] = move[i];
                        continue;
                    }
                    Vector3 sum = Vector3.zero;
                    foreach (int k in m.next[i])
                    {
                        sum += move[k];
                    }
                    smoothed[i] = Vector3.Lerp(move[i], sum / m.next[i].Length, 0.5f);
                }
                Vector3[] swap = move;
                move = smoothed;
                smoothed = swap;
            }
            for (int i = 0; i < m.v.Count; i++)
            {
                moved[i] = m.v[i] + move[i];
            }
            for (int i = 0; i < m.v.Count; i++)
            {
                m.v[i] = moved[i];
            }
            ClearOf(m, near, clear);
            m.Normals();
        }

        // p slid forward over the head, each step along the forward way laid flat on the head where it has got to and put
        // back on the surface, keeping p's gap off it, and stopping where the head turns to face forward (FaceTurn), so side
        // hair comes round to the face's edge but not onto it. Stopping each vertex dead there bunched the hair up against
        // that line, so the path is walked out first (up to SlideRoom times along) to find how much room a vertex has
        // before it, and it slides room * (1 - e^(-along / room)): nearly along with room to spare, less as the room runs
        // out. The distance left to the edge, room * e^(-along / room), grows with the room, so no vertex passes one
        // that was ahead of it, and the hair is pressed together evenly toward the face instead of piling up.
        private static Vector3 SlideAlong(Tris head, Vector3 p, Vector3 q, Vector3 n, float along)
        {
            float gap = (p - q).magnitude, step = along / SlideSteps;
            List<Vector3> path = new List<Vector3> { q }, normals = new List<Vector3> { n };
            float room = SlideSteps * SlideRoom * step;   // no edge within the look ahead: its end counts as one, so the
                                                          // slide doesn't jump between hair that meets an edge and hair that
                                                          // doesn't (with no edge the slide is (1 - e^(-1/SlideRoom)) along)
            for (int k = 0; k < SlideSteps * SlideRoom; k++)
            {
                Vector3 at = path[path.Count - 1], normal = normals[normals.Count - 1];
                Vector3 way = new Vector3(0f, 0f, 1f) - normal * normal.z;
                Vector3 next, nextNormal;
                int t;
                if (way.sqrMagnitude < 1e-8f || !head.Nearest(at + way.normalized * step, out next, out nextNormal, out t))
                {
                    room = k * step;
                    break;
                }
                if (nextNormal.z > FaceTurn)
                {
                    // The edge part way along this step, where the normal's forward share crosses FaceTurn.
                    float share = Mathf.Clamp01((FaceTurn - normal.z) / Mathf.Max(nextNormal.z - normal.z, 1e-6f));
                    path.Add(Vector3.Lerp(at, next, share));
                    normals.Add(Vector3.Lerp(normal, nextNormal, share).normalized);
                    room = (k + share) * step;
                    break;
                }
                path.Add(next);
                normals.Add(nextNormal);
            }
            if (normals[0].z > FaceTurn || room <= 0f)
            {
                return p;
            }
            float move = room * (1f - Mathf.Exp(-along / room));
            // The place move along the walked path (steps of step, the last one shorter where it met the edge).
            float into = move / step;
            int i = Mathf.Min((int)into, path.Count - 2);
            float last = i == path.Count - 2 ? room / step - i : 1f;
            float f = last > 1e-6f ? Mathf.Clamp01((into - i) / last) : 1f;
            Vector3 place = Vector3.Lerp(path[i], path[i + 1], f), up = Vector3.Lerp(normals[i], normals[i + 1], f).normalized;
            return place + up * gap;
        }

        // Brings the hair closer to the head: each vertex's gap off the head scaled by Tight, up to TightReach (hair.py pull_in).
        private void PullIn(Mesh m, Tris head, Tris near, float clear)
        {
            float reach = HeadWidths(TightReachBU);
            for (int i = 0; i < m.v.Count; i++)
            {
                Vector3 q, n;
                int t;
                if (!head.Nearest(m.v[i], out q, out n, out t))
                {
                    continue;
                }
                float gap = (m.v[i] - q).magnitude;
                if (gap > 1e-6f)
                {
                    float kept = (Tight * Mathf.Min(gap, reach) + Mathf.Max(gap - reach, 0f)) / gap;
                    m.v[i] = q + (m.v[i] - q) * (beard ? kept * DepthShare(m.v[i].y) : kept);
                }
            }
            m.Smooth(TightSmooth, 0.5f);
            ClearOf(m, near, clear);
            m.Normals();
        }

        // ---- texturing and the cut along the art

        // Which sprite faces a triangle with normal n at p most: 0 south (front), 1 east (either side), 2 north (back).
        // No sprite sees the top: the crown takes the front sprite over the front half of the head and the back one behind.
        private int ViewOf(Vector3 n, Vector3 p)
        {
            if (n.y > Crown && !beard)
            {
                return p.z > (back + front) / 2f ? 0 : 2;
            }
            if (Mathf.Abs(n.x) > (beard && n.z > 0f ? BeardFront : 1f) * Mathf.Abs(n.z))
            {
                return 1;
            }
            return n.z > 0f ? 0 : 2;
        }

        // Each sprite's first row in every column that is hair below its top outline (solid and lighter than SkirtDark), the
        // lowest within CrownSpread columns round (0 where empty). The crown is coloured from just under it: taken from
        // just under the first solid row it was the outline ink where that is thick or dips (a parting's notch), which
        // came out as a black blotch over the top of the head.
        private static int[][] TopEdges(Sprites sp)
        {
            int[][] edges = new int[3][];
            for (int v = 0; v < 3; v++)
            {
                int[] first = new int[Size];
                for (int c = 0; c < Size; c++)
                {
                    for (int r = 0; r < Size; r++)
                    {
                        Color32 p = sp.px[v][r * Size + c];
                        if (p.a >= Solid && (p.r + p.g + p.b) / (3f * 255f) > SkirtDark)
                        {
                            first[c] = r;
                            break;
                        }
                    }
                }
                int[] e = new int[Size];
                for (int c = 0; c < Size; c++)
                {
                    int m = 0;
                    for (int d = -CrownSpread; d <= CrownSpread; d++)
                    {
                        m = Math.Max(m, first[((c + d) % Size + Size) % Size]);
                    }
                    e[c] = m;
                }
                edges[v] = e;
            }
            return edges;
        }

        // Where p falls in a view's sprite (column, row), the crown coloured from just under the top outline.
        private Vector2 Where(int[][] edges, int view, bool crown, Vector3 p)
        {
            float X, Y, Z;
            ToSprite(p, out X, out Y, out Z);
            Vector2 uv = view == 0 ? new Vector2(X, Y) : view == 1 ? new Vector2(Z, Y) : new Vector2(Size - 1 - X, Y);
            if (crown)
            {
                uv.y = Mathf.Max(uv.y, edges[view][Mathf.Clamp((int)uv.x, 0, Size - 1)] + Outline);
            }
            return uv;
        }

        // The sprite's alpha at a place, blended between pixels.
        private static float Alpha(Color32[] px, Vector2 uv)
        {
            float x = uv.x - 0.5f, y = uv.y - 0.5f;
            int c = Mathf.FloorToInt(x), r = Mathf.FloorToInt(y);
            float fx = x - c, fy = y - r, total = 0f;
            for (int k = 0; k < 4; k++)
            {
                int rr = r + (k >> 1), cc = c + (k & 1);
                float w = ((k & 1) == 1 ? fx : 1f - fx) * ((k >> 1) == 1 ? fy : 1f - fy);
                if (rr >= 0 && rr < Size && cc >= 0 && cc < Size)
                {
                    total += px[rr * Size + cc].a / 255f * w;
                }
            }
            return total;
        }

        // The hull cut along the edge of its art (hair.py textured and Cut): the mesh's own vertices and new ones on the
        // cuts, each kept as a place along an edge of the mesh so it follows the mesh when it is pulled in.
        private sealed class CutMesh
        {
            public Mesh m;
            public readonly List<int> alongI = new List<int>(), alongJ = new List<int>();
            public readonly List<float> alongT = new List<float>();
            public readonly Dictionary<long, int> made = new Dictionary<long, int>();
            public readonly List<int[]> kept = new List<int[]>();   // a, b, c, view, crown (0 or 1)
            public readonly List<Vector2[]> uvs = new List<Vector2[]>();   // atlas places of each kept triangle's corners
            public readonly List<Vector2[]> spriteUvs = new List<Vector2[]>();   // the same in its own sprite, the crown's too
            // (kept[3] is 3 for a crown triangle on the crown panel)

            public int Point(int i, int j, float t)
            {
                long key = ((long)i << 42) | ((long)j << 21) | (long)Mathf.Round(t * 1e6f);
                int k;
                if (!made.TryGetValue(key, out k))
                {
                    k = m.v.Count + alongI.Count;
                    made[key] = k;
                    alongI.Add(i);
                    alongJ.Add(j);
                    alongT.Add(t);
                }
                return k;
            }

            public Vector3 Co(int k)
            {
                int n = m.v.Count;
                return k < n ? m.v[k] : Vector3.Lerp(m.v[alongI[k - n]], m.v[alongJ[k - n]], alongT[k - n]);
            }

            public void Resolved(out Vector3[] at, out Vector3[] normal)
            {
                int n = m.v.Count, all = n + alongI.Count;
                at = new Vector3[all];
                normal = new Vector3[all];
                for (int k = 0; k < all; k++)
                {
                    at[k] = Co(k);
                    normal[k] = k < n ? m.vn[k] : Vector3.Lerp(m.vn[alongI[k - n]], m.vn[alongJ[k - n]], alongT[k - n]).normalized;
                }
            }
        }

        private bool NearHair(bool[] nearHair, int n, Vector3 p)
        {
            float X, Y, Z;
            ToSprite(p, out X, out Y, out Z);
            int x = Mathf.FloorToInt(X / Voxel), y = Mathf.FloorToInt(Y / Voxel), z = Mathf.FloorToInt(Z / Voxel);
            return x >= 0 && y >= 0 && z >= 0 && x < n && y < n && z < n && nearHair[(y * n + x) * n + z];
        }

        // The sprites other than view that see a triangle with normal n, as bits (1 south, 2 east, 4 north). The crown is
        // seen by none of them, so it has only its own sprite's say.
        private static int SeenBy(Vector3 n, int view, bool crown)
        {
            if (crown)
            {
                return 0;
            }
            int seen = (n.z > Sees ? 1 : 0) | (Mathf.Abs(n.x) > Sees ? 2 : 0) | (n.z < -Sees ? 4 : 0);
            return seen & ~(1 << view);
        }

        // The sprite other than view that most faces a triangle with normal n and sees it (Sees), or -1.
        private static int OtherView(Vector3 n, int view)
        {
            int best = -1;
            float most = Sees;
            float[] share = { n.z, Mathf.Abs(n.x), -n.z };
            for (int v = 0; v < 3; v++)
            {
                if (v != view && share[v] > most)
                {
                    most = share[v];
                    best = v;
                }
            }
            return best;
        }

        // The least alpha at p of the triangle's own sprite and every other sprite that sees it.
        private float AlphaAt(Sprites sp, int[][] edges, int view, bool crown, int seen, Vector3 p)
        {
            if (beard)
            {
                return BeardAlpha(sp, edges, view, p);
            }
            float a = Alpha(sp.px[view], Where(edges, view, crown, p));
            for (int v = 0; v < 3; v++)
            {
                if ((seen & 1 << v) != 0)
                {
                    a = Mathf.Min(a, Alpha(veto[v], Where(edges, v, false, p)));
                }
            }
            return a;
        }

        // Each sprite's outline ink: solid dark pixels (SkirtDark) within InkReach of where the sprite is clear.
        private static bool[][] InkEdges(Sprites sp)
        {
            bool[][] all = new bool[3][];
            for (int v = 0; v < 3; v++)
            {
                bool[] clear = new bool[Size * Size], near = new bool[Size * Size], ink = new bool[Size * Size];
                for (int i = 0; i < clear.Length; i++)
                {
                    clear[i] = sp.px[v][i].a < Solid;
                }
                for (int r = 0; r < Size; r++)
                {
                    for (int c = 0; c < Size; c++)
                    {
                        bool any = false;
                        for (int dr = -InkReach; dr <= InkReach && !any; dr++)
                        {
                            for (int dc = -InkReach; dc <= InkReach && !any; dc++)
                            {
                                int rr = r + dr, cc = c + dc;
                                any = rr < 0 || cc < 0 || rr >= Size || cc >= Size || clear[rr * Size + cc];
                            }
                        }
                        near[r * Size + c] = any;
                    }
                }
                for (int i = 0; i < ink.Length; i++)
                {
                    Color32 p = sp.px[v][i];
                    ink[i] = !clear[i] && near[i] && (p.r + p.g + p.b) / (3f * 255f) <= SkirtDark;
                }
                all[v] = ink;
            }
            return all;
        }

        // Each sprite's alpha as it has a say over a triangle another sprite faces (AlphaAt): as it is over the head the
        // sprite is drawn over, where it shows the face or skin, but past it the most within VetoSlack pixels. The
        // sprites' outlines don't quite agree, and past one's outline it shows nothing at all rather than something
        // else: LongC's back sprite has a notch under each bow that the others don't, and the back corners there, which
        // the back sprite sees, were cut out as holes.
        private Color32[][] Vetoes(Sprites sp)
        {
            Color32[][] all = new Color32[3][];
            for (int v = 0; v < 3; v++)
            {
                byte[] a = new byte[Size * Size], b = new byte[Size * Size];
                for (int i = 0; i < a.Length; i++)
                {
                    a[i] = sp.px[v][i].a;
                }
                for (int r = 0; r < Size; r++)
                {
                    for (int c = 0; c < Size; c++)
                    {
                        byte most = 0;
                        for (int d = Math.Max(0, c - VetoSlack); d <= Math.Min(Size - 1, c + VetoSlack); d++)
                        {
                            most = Math.Max(most, a[r * Size + d]);
                        }
                        b[r * Size + c] = most;
                    }
                }
                Color32[] px = new Color32[Size * Size];
                for (int r = 0; r < Size; r++)
                {
                    for (int c = 0; c < Size; c++)
                    {
                        int i = r * Size + c;
                        byte most = 0;
                        for (int d = Math.Max(0, r - VetoSlack); d <= Math.Min(Size - 1, r + VetoSlack); d++)
                        {
                            most = Math.Max(most, b[d * Size + c]);
                        }
                        px[i].a = headMask[v][i] ? a[i] : most;
                    }
                }
                all[v] = px;
            }
            return all;
        }

        // A beard's alpha at p: only the sprite that faces it has a say, each being projected on the head from its own
        // side, but what the side sprite paints on the head goes no further round than the front sprite shows the beard
        // at that height (a goatee in the side sprite isn't one right across the jaw).
        private float BeardAlpha(Sprites sp, int[][] edges, int view, Vector3 p)
        {
            Vector2 uv = Where(edges, view, false, p);
            if (view == 1)
            {
                float X, Y, Z;
                ToSprite(p, out X, out Y, out Z);
                int row = Mathf.RoundToInt(Y);
                if (row < 0 || row >= Size || frontFrom[row] < 0)
                {
                    return 0f;
                }
                // Either side, as the side sprite stands for both.
                float off = Mathf.Abs(X - midX);
                if (off > Mathf.Max(midX - frontFrom[row], frontTo[row] - midX))
                {
                    return 0f;
                }
            }
            return Alpha(sp.px[view], uv);
        }

        // Each row's solid columns in the front sprite, the widest within WidthRows rows round, as the art's edge is uneven.
        private void FrontWidths(Sprites sp)
        {
            int[] from = new int[Size], to = new int[Size];
            for (int r = 0; r < Size; r++)
            {
                from[r] = to[r] = -1;
                for (int c = 0; c < Size; c++)
                {
                    if (sp.px[0][r * Size + c].a >= Solid)
                    {
                        from[r] = from[r] < 0 ? c : from[r];
                        to[r] = c;
                    }
                }
            }
            frontFrom = new int[Size];
            frontTo = new int[Size];
            for (int r = 0; r < Size; r++)
            {
                frontFrom[r] = frontTo[r] = -1;
                for (int d = Math.Max(0, r - WidthRows); d <= Math.Min(Size - 1, r + WidthRows); d++)
                {
                    if (from[d] >= 0)
                    {
                        frontFrom[r] = frontFrom[r] < 0 ? from[d] : Math.Min(frontFrom[r], from[d]);
                        frontTo[r] = Math.Max(frontTo[r], to[d]);
                    }
                }
            }
        }

        // Each triangle takes the sprite that faces it most. It is kept only where every sprite that sees it has hair: a
        // triangle round the corner of the face takes the side sprite, whose hair covers the temple, but the front one
        // shows the face there. One wholly off the art is dropped, one wholly on it kept, and one across the edge cut
        // where the alpha crosses Cut, so the mesh ends where the hair is seen to end.
        private CutMesh Textured(Mesh m, Sprites sp, bool[] nearHair)
        {
            veto = Vetoes(sp);
            ink = InkEdges(sp);
            int n = Size / Voxel;
            int[][] edges = TopEdges(sp);
            CutMesh cut = new CutMesh { m = m };
            List<int[]> kept = new List<int[]>();
            int[] ring = new int[4];
            for (int f = 0; f * 3 + 2 < m.t.Count; f++)
            {
                int[] t = { m.t[f * 3], m.t[f * 3 + 1], m.t[f * 3 + 2] };
                Vector3 centre = (m.v[t[0]] + m.v[t[1]] + m.v[t[2]]) / 3f;
                if (!NearHair(nearHair, n, centre))
                {
                    // Only the head's hull here (the face, the side of the head): the front sprite's locks either side
                    // of the face would be painted flat on the cheek, so close that they broke up through it.
                    continue;
                }
                int view = ViewOf(m.fn[f], centre);
                bool crown = m.fn[f].y > Crown && !beard;
                int seen = SeenBy(m.fn[f], view, crown);
                bool[] on = new bool[3];
                int count = 0;
                for (int k = 0; k < 3; k++)
                {
                    on[k] = AlphaAt(sp, edges, view, crown, seen, m.v[t[k]]) >= Cut;
                    count += on[k] ? 1 : 0;
                }
                int alt = crown || beard ? -1 : OtherView(m.fn[f], view);
                if (alt >= 0)
                {
                    // Where the triangle's own sprite runs out, another sprite that sees it may take it over:
                    // - Not wholly on its own sprite's art, and that sprite shows nothing there (past its outline, not
                    //   the head): the other takes it if more of it is on its art, and the first has no say. A back
                    //   corner facing a little more back than sideways takes the back sprite, whose outline (narrower
                    //   there, or notched under LongC's bows) left holes the side sprite has hair over.
                    // - On its own sprite's outline ink (InkEdges): the other takes it if it is wholly on its art and
                    //   not on its own ink there, as the ink drawn round each sprite's edge came out as a jagged black
                    //   line wherever one sprite's part of the hair gives way to another's.
                    Vector2 at = Where(edges, view, false, centre), altAt = Where(edges, alt, false, centre);
                    int here = Mathf.Clamp((int)at.y, 0, Size - 1) * Size + Mathf.Clamp((int)at.x, 0, Size - 1);
                    int there = Mathf.Clamp((int)altAt.y, 0, Size - 1) * Size + Mathf.Clamp((int)altAt.x, 0, Size - 1);
                    bool bare = count < 3 && !headMask[view][here], inked = count > 0 && ink[view][here] && !ink[alt][there];
                    if (bare || inked)
                    {
                        int altSeen = SeenBy(m.fn[f], alt, false) & (bare ? ~(1 << view) : ~0);
                        bool[] altOn = new bool[3];
                        int altCount = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            altOn[k] = AlphaAt(sp, edges, alt, false, altSeen, m.v[t[k]]) >= Cut;
                            altCount += altOn[k] ? 1 : 0;
                        }
                        if (bare ? altCount > count : altCount == 3)
                        {
                            view = alt;
                            seen = altSeen;
                            on = altOn;
                            count = altCount;
                        }
                    }
                }
                int c = crown ? 1 : 0;
                if (count == 3)
                {
                    kept.Add(new[] { t[0], t[1], t[2], view, c });
                    continue;
                }
                if (count == 0)
                {
                    continue;
                }
                // Round the triangle in its winding, keeping the corners on the art and the crossings between.
                int size = 0;
                for (int k = 0; k < 3; k++)
                {
                    int i = t[k], j = t[(k + 1) % 3];
                    if (on[k])
                    {
                        ring[size++] = i;
                    }
                    if (on[k] != on[(k + 1) % 3])
                    {
                        Vector3 p = m.v[i], q = m.v[j];
                        float lo = 0f, hi = 1f;
                        for (int step = 0; step < 12; step++)
                        {
                            float mid = (lo + hi) / 2f;
                            if ((AlphaAt(sp, edges, view, crown, seen, Vector3.Lerp(p, q, mid)) >= Cut) == on[k])
                            {
                                lo = mid;
                            }
                            else
                            {
                                hi = mid;
                            }
                        }
                        float cross = on[k] ? lo : hi;
                        // The same crossing from either triangle on the edge: named from its lower-numbered end.
                        ring[size++] = i < j ? cut.Point(i, j, cross) : cut.Point(j, i, 1f - cross);
                    }
                }
                for (int k = 1; k + 1 < size; k++)
                {
                    kept.Add(new[] { ring[0], ring[k], ring[k + 1], view, c });
                }
            }
            // The crown takes the panel seen from above (Crowned): its own sprite only had its say over where it is cut.
            crownPanel = beard ? null : Crowned(sp, edges, cut, kept);
            foreach (int[] t in kept)
            {
                Vector2[] uv = new Vector2[3], own = new Vector2[3];
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = cut.Co(t[k]);
                    Vector2 w = Where(edges, t[3], t[4] == 1, p);
                    own[k] = new Vector2((t[3] * Size + w.x + 0.5f) / (Panels * Size), 1f - (w.y + 0.5f) / Size);
                    if (crownPanel != null && t[4] == 1)
                    {
                        float X, Y, Z;
                        ToSprite(p, out X, out Y, out Z);
                        w = new Vector2(X, Z);
                        uv[k] = new Vector2((3 * Size + w.x + 0.5f) / (Panels * Size), 1f - (w.y + 0.5f) / Size);
                    }
                    else
                    {
                        uv[k] = own[k];
                    }
                }
                if (crownPanel != null && t[4] == 1)
                {
                    t[3] = 3;
                }
                cut.kept.Add(t);
                cut.uvs.Add(uv);
                cut.spriteUvs.Add(own);
            }
            return cut;
        }

        // Fine streaks down the hair, -1 to 1, laid over the sprites' colours by StrandStreak (Atlas): the sprites are mostly
        // drawn at 128 pixels and read here at 256, so on the 3D hair they came out as smooth blends of colour, which
        // looked like plastic. Down each sprite is down the hair nearly everywhere, so each column gets brightness of its
        // own that changes slowly down it: a thin streak a pixel wide changing over StreakLength rows, and a wider one
        // (three pixels) over twice that, from a hash of the place, so a style always gets the same.
        private static float Streak(int view, int c, int r)
        {
            return 0.6f * Streak1(view, c, r, 1, StreakLength) + 0.4f * Streak1(view + 3, c, r, 3, 2 * StreakLength);
        }

        private static float Streak1(int seed, int c, int r, int width, int length)
        {
            float x = c / (float)width, y = r / (float)length;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Mathf.Lerp(Noise(seed, x0, y0), Noise(seed, x0 + 1, y0), fx);
            float b = Mathf.Lerp(Noise(seed, x0, y0 + 1), Noise(seed, x0 + 1, y0 + 1), fx);
            return Mathf.Lerp(a, b, fy);
        }

        // -1 to 1 from a hash of the three numbers (murmur3's finalizer).
        private static float Noise(int seed, int x, int y)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791);
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;
            return h / (float)uint.MaxValue * 2f - 1f;
        }

        // The three sprites with their strand streaks, rows top first.
        private static Color32[][] Streaked(Sprites sp)
        {
            Color32[][] all = new Color32[3][];
            for (int v = 0; v < 3; v++)
            {
                Color32[] px = new Color32[Size * Size];
                for (int r = 0; r < Size; r++)
                {
                    for (int c = 0; c < Size; c++)
                    {
                        Color32 p = sp.px[v][r * Size + c];
                        if (p.a > 0 && (p.r + p.g + p.b) / (3f * 255f) > SkirtDark)
                        {
                            float f = 1f + StrandStreak * Streak(v, c, r);
                            p.r = (byte)Mathf.Clamp(p.r * f, 0f, 255f);
                            p.g = (byte)Mathf.Clamp(p.g * f, 0f, 255f);
                            p.b = (byte)Mathf.Clamp(p.b * f, 0f, 255f);
                        }
                        px[r * Size + c] = p;
                    }
                }
                all[v] = px;
            }
            return all;
        }

        // The three sprites side by side, then the crown panel (clear without one), rows bottom first.
        private static Color32[] Atlas(Sprites sp, Color32[] crown)
        {
            int w = Panels * Size;
            Color32[] a = new Color32[w * Size];
            Color32[][] streaked = Streaked(sp);
            for (int v = 0; v < Panels; v++)
            {
                Color32[] px = v < 3 ? streaked[v] : crown;
                if (px == null)
                {
                    continue;
                }
                for (int r = 0; r < Size; r++)
                {
                    for (int c = 0; c < Size; c++)
                    {
                        a[(Size - 1 - r) * w + v * Size + c] = px[r * Size + c];
                    }
                }
            }
            return a;
        }

        // The crown seen from above, a texel to a sprite pixel across (X) and deep (Z), rows top first, coloured where the
        // crown's triangles are. Each crown triangle took the front or the back sprite by which half of the head it is on,
        // and its neighbours below took the sprite that faces them most, so the crown showed hard seams across its middle
        // and round its edge, where the sprites' top rows (stretched over it) don't agree. Here each texel blends the
        // sprites as coloured at its place on the crown (Where): at its edge (normal Crown up) only the one its neighbours
        // below take (ViewOf's choice by the normal), so it meets them, and over CrownBlend more upward the front and back
        // ones by how far forward it is (CrownMix), the side one dropping out.
        private Color32[] Crowned(Sprites sp, int[][] edges, CutMesh cut, List<int[]> kept)
        {
            Color32[][] streaked = Streaked(sp);
            Vector3[] at, normal;
            cut.Resolved(out at, out normal);
            Color[] sum = new Color[Size * Size];
            float[] height = new float[Size * Size];
            bool[] filled = new bool[Size * Size];
            float mid = (back + front) / 2f, band = Mathf.Max(CrownMix * (front - back) / 2f, 1e-5f);
            Vector3[] q = new Vector3[3];
            foreach (int[] t in kept)
            {
                if (t[4] != 1)
                {
                    continue;
                }
                for (int k = 0; k < 3; k++)
                {
                    ToSprite(at[t[k]], out q[k].x, out q[k].y, out q[k].z);
                }
                int c0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(q[0].x, Mathf.Min(q[1].x, q[2].x))));
                int c1 = Mathf.Min(Size - 1, Mathf.CeilToInt(Mathf.Max(q[0].x, Mathf.Max(q[1].x, q[2].x))));
                int r0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(q[0].z, Mathf.Min(q[1].z, q[2].z))));
                int r1 = Mathf.Min(Size - 1, Mathf.CeilToInt(Mathf.Max(q[0].z, Mathf.Max(q[1].z, q[2].z))));
                float area = (q[1].x - q[0].x) * (q[2].z - q[0].z) - (q[2].x - q[0].x) * (q[1].z - q[0].z);
                if (Mathf.Abs(area) < 1e-6f)
                {
                    continue;
                }
                for (int r = r0; r <= r1; r++)
                {
                    for (int c = c0; c <= c1; c++)
                    {
                        float b1 = ((c - q[0].x) * (q[2].z - q[0].z) - (q[2].x - q[0].x) * (r - q[0].z)) / area;
                        float b2 = ((q[1].x - q[0].x) * (r - q[0].z) - (c - q[0].x) * (q[1].z - q[0].z)) / area;
                        float b0 = 1f - b1 - b2;
                        const float slack = -0.02f;
                        if (b0 < slack || b1 < slack || b2 < slack)
                        {
                            continue;
                        }
                        int i = r * Size + c;
                        float y = b0 * q[0].y + b1 * q[1].y + b2 * q[2].y;   // a row: smaller is higher
                        if (filled[i] && y >= height[i])
                        {
                            continue;
                        }
                        Vector3 p = b0 * at[t[0]] + b1 * at[t[1]] + b2 * at[t[2]];
                        Vector3 n = (b0 * normal[t[0]] + b1 * normal[t[1]] + b2 * normal[t[2]]).normalized;
                        float[] w = new float[3];
                        int below = Mathf.Abs(n.x) > Mathf.Abs(n.z) ? 1 : n.z > 0f ? 0 : 2;
                        float up = Mathf.Clamp01((n.y - Crown) / CrownBlend);
                        up = up * up * (3f - 2f * up);
                        float forward = Mathf.Clamp01(((p.z - mid) / band + 1f) / 2f);
                        forward = forward * forward * (3f - 2f * forward);
                        w[below] += 1f - up;
                        w[0] += up * forward;
                        w[2] += up * (1f - forward);
                        Color colour = new Color(0f, 0f, 0f, 0f);
                        float weight = 0f;
                        for (int v = 0; v < 3; v++)
                        {
                            if (w[v] <= 0f)
                            {
                                continue;
                            }
                            Color s = Sample(streaked[v], Where(edges, v, true, p));
                            colour += new Color(s.r, s.g, s.b, 1f) * (w[v] * s.a);
                            weight += w[v] * s.a;
                        }
                        if (weight < 1e-4f)
                        {
                            continue;
                        }
                        sum[i] = colour / weight;
                        height[i] = y;
                        filled[i] = true;
                    }
                }
            }
            // Grown past the triangles, each new texel the mean of its filled neighbours, so filtering at the crown's
            // edges and triangles a little off their first place (pulled in, cut down) don't take clear texels.
            for (int pass = 0; pass < CrownGrow; pass++)
            {
                bool[] was = (bool[])filled.Clone();
                for (int r = 0; r < Size; r++)
                {
                    for (int c = 0; c < Size; c++)
                    {
                        if (was[r * Size + c])
                        {
                            continue;
                        }
                        Color total = new Color(0f, 0f, 0f, 0f);
                        int count = 0;
                        for (int dr = -1; dr <= 1; dr++)
                        {
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                int rr = r + dr, cc = c + dc;
                                if (rr >= 0 && rr < Size && cc >= 0 && cc < Size && was[rr * Size + cc])
                                {
                                    total += sum[rr * Size + cc];
                                    count++;
                                }
                            }
                        }
                        if (count > 0)
                        {
                            sum[r * Size + c] = total / count;
                            filled[r * Size + c] = true;
                        }
                    }
                }
            }
            Color32[] panel = new Color32[Size * Size];
            for (int i = 0; i < panel.Length; i++)
            {
                if (filled[i])
                {
                    Color s = sum[i];
                    panel[i] = new Color32((byte)Mathf.Clamp(s.r * 255f + 0.5f, 0f, 255f), (byte)Mathf.Clamp(s.g * 255f + 0.5f, 0f, 255f),
                        (byte)Mathf.Clamp(s.b * 255f + 0.5f, 0f, 255f), 255);
                }
            }
            return panel;
        }

        // A sprite's colour at a place (column, row), blended between pixels, alpha not multiplied in.
        private static Color Sample(Color32[] px, Vector2 uv)
        {
            float x = uv.x, y = uv.y;
            int c = Mathf.FloorToInt(x), r = Mathf.FloorToInt(y);
            float fx = x - c, fy = y - r;
            Color total = new Color(0f, 0f, 0f, 0f);
            float alpha = 0f;
            for (int k = 0; k < 4; k++)
            {
                int rr = r + (k >> 1), cc = c + (k & 1);
                float w = ((k & 1) == 1 ? fx : 1f - fx) * ((k >> 1) == 1 ? fy : 1f - fy);
                if (rr >= 0 && rr < Size && cc >= 0 && cc < Size)
                {
                    Color32 p = px[rr * Size + cc];
                    float a = p.a / 255f * w;
                    total += new Color(p.r / 255f, p.g / 255f, p.b / 255f, 1f) * a;
                    alpha += a;
                }
            }
            return alpha > 1e-5f ? new Color(total.r / alpha, total.g / alpha, total.b / alpha, alpha) : new Color(0f, 0f, 0f, 0f);
        }

        // ---- the walls, the keys and the files

        // Where an open edge's wall reaches: just inside the head under p, or SkirtReach in towards it if that is further;
        // off the scalp shorter (the constants' comment), and below the chin level with p. below: how far below the
        // scalp's edge the head under p is, in head heights (BelowHairline; 0 for a beard).
        private Vector3 Inward(Tris head, Vector3 p, out float below)
        {
            below = 0f;
            Vector3 q, n;
            int t;
            if (!head.Nearest(p, out q, out n, out t))
            {
                return p;
            }
            Vector3 target = q - n * HeadWidths(SkirtSinkBU);
            // Below the chin the nearest head is the jaw above it, so a lock's wall would be stretched up into the face.
            // It goes straight in at its own height instead, turning to it over ChinBlend so the walls don't kink there.
            float under = beard ? 0f : Mathf.SmoothStep(0f, 1f, (chin - p.y) / (ChinBlend * (top - chin)));
            target.y = Mathf.Lerp(target.y, p.y, under);
            float most = SkirtReachBU;
            if (!beard)
            {
                // Off the scalp a wall is short: the locks hang free beside the head there, and a wall only has to give
                // an edge some thickness; reaching in to the head it was stretched across the cheek and chin.
                below = BelowHairline(head, q, t);
                // Below the chin, hair further off the head than a wall reaches hangs free whatever is nearest (the
                // nape's scalp, for long hair down the back): counted as that much further below the line, coming in
                // over ChinBlend, as all at once at the chin it cut a standing-off lock's walls off there.
                below += under * Mathf.Max(0f, (p - q).magnitude - HeadWidths(SkirtReachBU)) / (top - chin);
                float fadeFrom = FallAway - FallFade;
                most = below < HairlineEase
                    ? Mathf.Lerp(SkirtReachBU, SkirtShortBU, below / HairlineEase)
                    : below < fadeFrom
                    ? Mathf.Lerp(SkirtShortBU, SkirtChinBU, (below - HairlineEase) / (fadeFrom - HairlineEase))
                    : Mathf.Lerp(SkirtChinBU, 0f, Mathf.SmoothStep(0f, 1f, (below - fadeFrom) / FallFade));
            }
            float reach = HeadWidths(most);
            float d = (target - p).magnitude;
            return d > 1e-6f ? p + (target - p) * Mathf.Min(1f, reach / d) : p;
        }

        // The atlas pixels a wall may take its colour from: solid all round, and not the outline (hair.py wall_colours).
        private static List<Vector2> WallColours(Sprites sp)
        {
            List<Vector2> out_ = new List<Vector2>();
            for (int v = 0; v < 3; v++)
            {
                Color32[] px = sp.px[v];
                for (int r = 0; r < Size; r++)
                {
                    for (int c = 0; c < Size; c++)
                    {
                        Color32 p = px[r * Size + c];
                        if ((p.r + p.g + p.b) / (3f * 255f) <= SkirtDark)
                        {
                            continue;
                        }
                        bool deep = true;
                        for (int dr = -1; dr <= 1 && deep; dr++)
                        {
                            for (int dc = -1; dc <= 1 && deep; dc++)
                            {
                                int rr = (r + dr + Size) % Size, cc = (c + dc + Size) % Size;
                                deep = px[rr * Size + cc].a >= 230;
                            }
                        }
                        if (deep)
                        {
                            out_.Add(new Vector2(v * Size + c, r));
                        }
                    }
                }
            }
            return out_;
        }

        private static float Grey(Sprites sp, Vector2 atlas)
        {
            Color32 p = sp.px[(int)atlas.x / Size][(int)atlas.y * Size + (int)atlas.x % Size];
            return p.r + p.g + p.b;
        }

        // An atlas place for a wall: the pixel of colours nearest a point SkirtInset pixels in from uv towards centre.
        private static Vector2 WallColour(List<Vector2> colours, Vector2 uv, Vector2 centre)
        {
            if (colours.Count == 0)
            {
                return uv;
            }
            Func<Vector2, Vector2> toPx = t => new Vector2(t.x * Panels * Size - 0.5f, (1f - t.y) * Size - 0.5f);
            Vector2 p = toPx(uv), d = toPx(centre) - p;
            float length = d.magnitude;
            if (length > 1e-6f)
            {
                p += d * Mathf.Min(SkirtInset / length, 1f);
            }
            Vector2 best = colours[0];
            float bestD = float.MaxValue;
            foreach (Vector2 c in colours)
            {
                float dd = (c - p).sqrMagnitude;
                if (dd < bestD)
                {
                    bestD = dd;
                    best = c;
                }
            }
            return new Vector2((best.x + 0.5f) / (Panels * Size), 1f - (best.y + 0.5f) / Size);
        }

        // Each key's move of each point: the head surface nearest it moves as that triangle does, and the point's gap off
        // it grows or shrinks as the triangle does (hair.py fitted_keys).
        private void Keys(StringBuilder sb, Vector3[] points, Tris head)
        {
            sb.Append("# key name: then d i x y z (vertex i moves), cells: fitted from the head surface under each vertex.\n");
            foreach (KeyValuePair<string, Vector3[]> key in keys)
            {
                sb.Append("key ").Append(key.Key).Append('\n');
                Vector3[] m = key.Value;
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 q, n;
                    int t;
                    if (!head.Nearest(points[i], out q, out n, out t))
                    {
                        continue;
                    }
                    int k = head.index[t] * 3;
                    int a = bodyTris[k], b = bodyTris[k + 1], c = bodyTris[k + 2];
                    Vector3 w = Barycentric(q, body[a], body[b], body[c]);
                    float before = Vector3.Cross(body[b] - body[a], body[c] - body[a]).magnitude;
                    float after = Vector3.Cross(body[b] + m[b] - body[a] - m[a], body[c] + m[c] - body[a] - m[a]).magnitude;
                    float grow = before > 1e-12f ? Mathf.Sqrt(after / before) : 1f;
                    Vector3 d = m[a] * w.x + m[b] * w.y + m[c] * w.z + (points[i] - q) * (grow - 1f);
                    if (d.sqrMagnitude > 1e-14f)
                    {
                        sb.Append("d ").Append(i).Append(' ').Append(Vec(d)).Append('\n');
                    }
                }
            }
        }

        private static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1), denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-20f)
            {
                return new Vector3(1f, 0f, 0f);
            }
            float v = (d11 * d20 - d01 * d21) / denom, w = (d00 * d21 - d01 * d20) / denom;
            return new Vector3(1f - v - w, v, w);
        }

        private static string Num(float f)
        {
            return f.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static string Vec(Vector3 v)
        {
            return Num(v.x) + " " + Num(v.y) + " " + Num(v.z);
        }

        private void Face(StringBuilder sb, int a, int b, int c)
        {
            sb.Append("f ").Append(a).Append(' ').Append(reversed ? c : b).Append(' ').Append(reversed ? b : c).Append('\n');
        }

        // <style>.txt, as hair.py's write: a vertex once for each sprite its triangles take, all with the shell's smooth
        // normal, and a one-colour wall hung in towards the head under each open edge.
        private string WriteHair(Sprites sp, string style, CutMesh cut, Vector3[] at, Vector3[] normal, Tris head)
        {
            List<int> from = new List<int>();
            List<Vector3> place = new List<Vector3>();
            List<Vector2> uv = new List<Vector2>();
            Dictionary<long, int> copies = new Dictionary<long, int>();
            List<int> faces = new List<int>();
            for (int f = 0; f < cut.kept.Count; f++)
            {
                int[] t = cut.kept[f];
                for (int k = 0; k < 3; k++)
                {
                    long key = (long)t[k] * 8 + t[3] * 2 + t[4];
                    int copy;
                    if (!copies.TryGetValue(key, out copy))
                    {
                        copy = from.Count;
                        copies[key] = copy;
                        from.Add(t[k]);
                        place.Add(at[t[k]]);
                        uv.Add(cut.uvs[f][k]);
                    }
                    faces.Add(copy);
                }
            }
            int hairFaces = faces.Count;
            // The open edges: on one kept triangle only, in its winding.
            Dictionary<long, int> count = new Dictionary<long, int>();
            foreach (int[] t in cut.kept)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = t[k], b = t[(k + 1) % 3];
                    long e = (long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b);
                    int c;
                    count.TryGetValue(e, out c);
                    count[e] = c + 1;
                }
            }
            List<Vector2> colours = WallColours(sp);
            if (beard && colours.Count > 0)
            {
                // All one colour, its art's middle brightness: picked edge by edge they came out in stripes over a thin
                // beard's jagged top edge, which the camera looks down on.
                colours.Sort((a, b) => Grey(sp, a).CompareTo(Grey(sp, b)));
                colours = new List<Vector2> { colours[colours.Count / 2] };
            }
            // Each open edge a to b (in its triangle's winding) with the colour just inside it.
            List<int[]> open = new List<int[]>();
            List<Vector2> edgeColour = new List<Vector2>();
            for (int f = 0; f < cut.kept.Count; f++)
            {
                int[] t = cut.kept[f];
                Vector2[] uvs = cut.spriteUvs[f];   // the walls take their colours from the sprites
                Vector2 centre = (uvs[0] + uvs[1] + uvs[2]) / 3f;
                for (int k = 0; k < 3; k++)
                {
                    int a = t[k], b = t[(k + 1) % 3];
                    if (count[(long)Math.Min(a, b) << 32 | (uint)Math.Max(a, b)] != 1)
                    {
                        continue;
                    }
                    open.Add(new[] { a, b });
                    edgeColour.Add(WallColour(colours, (uvs[k] + uvs[(k + 1) % 3]) / 2f, centre));
                }
            }
            // The open edges as loops round each hole and the hair's edge. Along the art's ragged edge, each wall reaching
            // to its own nearest point on the head and taking its own pixel's colour came out as a ribbed band of fins
            // in stripes. So the reach in is worked out at each corner and smoothed along the loop (WallSmooth), the
            // walls meeting at their corners as one curtain, and all of a loop's walls take one colour, WallShade up from
            // the darkest of those along it, so they read as the hair's shaded thickness, not stripes (a colour of their
            // own within a few edges either way still changed in blocks).
            Dictionary<int, List<int>> outOf = new Dictionary<int, List<int>>();
            for (int e = 0; e < open.Count; e++)
            {
                List<int> list;
                if (!outOf.TryGetValue(open[e][0], out list))
                {
                    outOf[open[e][0]] = list = new List<int>();
                }
                list.Add(e);
            }
            bool[] walked = new bool[open.Count];
            List<List<int>> loops = new List<List<int>>();
            for (int e0 = 0; e0 < open.Count; e0++)
            {
                if (walked[e0])
                {
                    continue;
                }
                List<int> loop = new List<int>();
                for (int e = e0; e >= 0 && !walked[e];)
                {
                    walked[e] = true;
                    loop.Add(e);
                    int next = -1;
                    List<int> list;
                    if (outOf.TryGetValue(open[e][1], out list))
                    {
                        foreach (int c in list)
                        {
                            if (!walked[c])
                            {
                                next = c;
                                break;
                            }
                        }
                    }
                    e = next;
                }
                loops.Add(loop);
            }
            Dictionary<int, Vector3> reach = new Dictionary<int, Vector3>();
            Dictionary<int, float> below = new Dictionary<int, float>();
            foreach (int[] e in open)
            {
                foreach (int v in e)
                {
                    if (!reach.ContainsKey(v))
                    {
                        float b;
                        reach[v] = Inward(head, at[v], out b) - at[v];
                        below[v] = b;
                    }
                }
            }
            for (int pass = 0; pass < WallSmooth; pass++)
            {
                Dictionary<int, Vector3> smoothed = new Dictionary<int, Vector3>(reach);
                foreach (List<int> loop in loops)
                {
                    for (int i = 0; i < loop.Count; i++)
                    {
                        // The corner where this edge meets the next: halfway to the mean of its neighbours on the loop.
                        int[] here = open[loop[i]];
                        int prev = here[0], v = here[1];
                        bool closed = loop.Count > 2 && open[loop[loop.Count - 1]][1] == open[loop[0]][0];
                        int next = i + 1 < loop.Count ? open[loop[i + 1]][1] : closed ? open[loop[0]][1] : -1;
                        if (next < 0)
                        {
                            continue;
                        }
                        smoothed[v] = Vector3.Lerp(reach[v], (reach[prev] + reach[next]) / 2f, 0.5f);
                    }
                }
                reach = smoothed;
            }
            foreach (List<int> loop in loops)
            {
                List<Vector2> along = new List<Vector2>();
                foreach (int e in loop)
                {
                    along.Add(edgeColour[e]);
                }
                along.Sort((x, y) => Grey(sp, x).CompareTo(Grey(sp, y)));
                Vector2 w = along[(int)(along.Count * WallShade)];
                for (int i = 0; i < loop.Count; i++)
                {
                    int[] e = open[loop[i]];
                    int a = e[0], b = e[1];
                    if (!beard && below[a] >= FallAway && below[b] >= FallAway)
                    {
                        // No walls FallAway below the scalp's edge (tapered to nothing by then): the hair there hangs
                        // free of the head, with nothing to close the gap to, and the short ledges hung under a lock's
                        // ragged edges stuck out of it every way.
                        continue;
                    }
                    // Continues the shell's winding, so the wall faces out of the space between the hair and the head;
                    // one colour on its four corners' own copies.
                    int oa = from.Count, ob = oa + 1, ka = oa + 2, kb = oa + 3;
                    from.AddRange(new[] { a, b, a, b });
                    place.Add(at[a]);
                    place.Add(at[b]);
                    place.Add(at[a] + reach[a]);
                    place.Add(at[b] + reach[b]);
                    uv.AddRange(new[] { w, w, w, w });
                    faces.AddRange(new[] { ob, oa, ka, ob, ka, kb });
                }
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("# source ").Append(sp.hash).Append('\n');
            sb.Append("# M3AT: hair \"").Append(style).Append("\", built from its sprites in the game (HairBuilder.cs, as archive/hair.py).\n");
            sb.Append("# The rest pose in game model space (cells), as skin.txt. It rides the head bone.\n");
            foreach (Vector3 p in place)
            {
                sb.Append("v ").Append(Vec(p)).Append('\n');
            }
            foreach (int i in from)
            {
                sb.Append("n ").Append(Vec(normal[i])).Append('\n');
            }
            foreach (Vector2 t in uv)
            {
                sb.Append("t ").Append(Num(t.x)).Append(' ').Append(Num(t.y)).Append('\n');
            }
            for (int k = 0; k + 2 < faces.Count; k += 3)
            {
                if (k == hairFaces)
                {
                    sb.Append("# the walls under the open edges from here on\n");
                }
                Face(sb, faces[k], faces[k + 1], faces[k + 2]);
            }
            if (hairFaces < faces.Count)
            {
                sb.Append("walls ").Append(hairFaces / 3).Append('\n');
            }
            Keys(sb, place.ToArray(), head);
            return sb.ToString();
        }

        // <style>.shell.txt, as hair.py's write_shell: the closed hull, the vertices on no kept triangle laid clear off the
        // head (skin), and the body triangles inside it.
        private string WriteShell(string hash, string style, Mesh m, CutMesh cut, Tris near, Tris head, float clear, out int hidden)
        {
            bool[] hair = new bool[m.v.Count];
            foreach (int[] t in cut.kept)
            {
                for (int k = 0; k < 3; k++)
                {
                    if (t[k] < hair.Length)
                    {
                        hair[t[k]] = true;
                    }
                }
            }
            Vector3[] at = new Vector3[m.v.Count];
            for (int i = 0; i < at.Length; i++)
            {
                at[i] = m.v[i];
                Vector3 q, n;
                int t;
                if (!hair[i] && near.Nearest(at[i], out q, out n, out t))
                {
                    at[i] = q + n * clear;
                }
            }
            List<int> all = new List<int>();
            for (int f = 0; f < m.t.Count / 3; f++)
            {
                all.Add(f);
            }
            Tris shell = new Tris(at, m.t, all);
            // Only the body near the hair can be inside it.
            List<int> hides = new List<int>();
            Dictionary<int, bool> inside = new Dictionary<int, bool>();
            for (int i = 0; i < near.Count; i++)
            {
                int k = near.index[i] * 3;
                bool all3 = true;
                for (int c = 0; c < 3 && all3; c++)
                {
                    int vtx = bodyTris[k + c];
                    bool isIn;
                    if (!inside.TryGetValue(vtx, out isIn))
                    {
                        Vector3 q, n;
                        int t;
                        isIn = shell.Nearest(body[vtx], out q, out n, out t) && Vector3.Dot(body[vtx] - q, n) <= -0.5f * clear;
                        inside[vtx] = isIn;
                    }
                    all3 = isIn;
                }
                if (all3)
                {
                    hides.Add(near.index[i]);
                }
            }
            hides.Sort();
            hidden = hides.Count;
            StringBuilder sb = new StringBuilder();
            sb.Append("# source ").Append(hash).Append('\n');
            sb.Append("# M3AT: the outline shell of hair \"").Append(style).Append("\" (HairBuilder.cs, as archive/hair.py): rest pose,\n");
            sb.Append("# game model space (cells), riding the head bone. skin: vertices on no hair. hide: body triangles inside it.\n");
            foreach (Vector3 p in at)
            {
                sb.Append("v ").Append(Vec(p)).Append('\n');
            }
            for (int k = 0; k + 2 < m.t.Count; k += 3)
            {
                Face(sb, m.t[k], m.t[k + 1], m.t[k + 2]);
            }
            List<int> skin = new List<int>();
            for (int i = 0; i < hair.Length; i++)
            {
                if (!hair[i])
                {
                    skin.Add(i);
                }
            }
            Lines(sb, "skin", skin);
            Lines(sb, "hide", hides);
            Keys(sb, at, head);
            return sb.ToString();
        }

        private static void Lines(StringBuilder sb, string name, List<int> items)
        {
            for (int k = 0; k < items.Count; k += 32)
            {
                sb.Append(name);
                for (int i = k; i < Math.Min(k + 32, items.Count); i++)
                {
                    sb.Append(' ').Append(items[i]);
                }
                sb.Append('\n');
            }
        }
    }
}
