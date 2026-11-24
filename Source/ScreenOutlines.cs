using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using IsoRim;

namespace M3AT
{
    // The screen-space outline (Settings.outlineMode ScreenSpace). The shell outline is the figure pushed out along its
    // normals with only the faces turned away drawn: a part with no thickness (a pauldron made as one curved sheet, a
    // brim) has no rim at its edge and its line stops there, and drawn as the silhouette alone (stencil bit 64) the line
    // round a weapon held over the body is hidden with the body's. This finds the lines on screen instead:
    //
    // 1. Each part's faces are drawn again (MarusOutlineId, MarusOutlineIdTex, queue 2455) multiplying the picture by
    //    (1, 1, 1, its code): the colour as it was, the alpha the code. A code is 2 + 2 * the figure's slot this frame
    //    (OutlineOrder.cs) + 1 for a weapon it holds (DrawJoined), over 255; the alpha is 1 everywhere else, as IsoRim's
    //    occlusion and shadow passes leave it.
    // 2. A box round each figure (its joints, CoverMargin roomier, then the widest line) and each weapon it holds is drawn
    //    with MarusEdge (queue 2456), which reads that picture (its scene grab) and IsoRim's copy of the scene depth
    //    (_IsoSceneDepth, which IsoRim's DepthGrab makes at its ambient occlusion's grab; the part pass names that grab so
    //    the engine makes it every frame, and DepthGrab.Request makes the texture). Each pixel is an outline where another
    //    part nearer the camera is within the line's width (MarusEdge.frag.glsl), in that part's colour shaded as the shell
    //    outline is, at that part's depth; every other pixel is written back with alpha 1.
    //
    // Widths are per kind (Settings.pawnOutlineWidth, weaponOutlineWidth, cells), inside one figure the thinner. Not in
    // IsoRim's photos (Api.CaptureView, the autotest's videos): the photo camera's render makes no scene grabs, so there the
    // shell outline is drawn as in the silhouette mode. Nor where IsoRim can't read the scene depth (DepthGrab.Broken) or a
    // shader is missing: the silhouette again. What IsoRim draws with its own outline (slabs, tools, worn gear, a weapon on
    // the ground or flung) keeps it.
    public sealed partial class SkinnedFigure
    {
        private const string IdDef = "MarusOutlineId", IdTexDef = "MarusOutlineIdTex", EdgeDef = "MarusEdge";
        private const float CoverMargin = 0.6f;   // model units round the joints: the head's top and the hair past the neck's joint
        // The most pixels the edge pass looks round a pixel at 1080 rows, more on a taller picture (MarusEdge.frag.glsl): the
        // widest a line is drawn close up. A field so the autotest can try others.
        public static float EdgeMostPixels = 16f;
        private const float EdgeNearer = 0.01f;   // cells a part must be nearer than what is behind to be outlined over it

        private static Shader idShader, idTexShader, edgeShader;
        private static bool screenTried, depthBroken;
        private static Material edge;
        private static Mesh cover;
        private static readonly Material[] idPawn = new Material[Slots], idWeapon = new Material[Slots];
        private static readonly Dictionary<Texture, Material[]> idTextured = new Dictionary<Texture, Material[]>();
        private static int screenFrame = -1;
        public static int ScreenCovers;   // edge boxes drawn so far this session (the autotest's outlines checks)
        public static bool EdgeFullSearch;   // the edge pass's full search at every pixel, without its quick look first (the autotest's comparison)
        // The edge pass's shape (MarusEdge.frag.glsl's _EdgeShape): the quick look's points a ring and its rings, the full
        // search's most rings. Fields, not constants, so the autotest's perf can try others in one run.
        // 8.2.10 measured best (a pawn filling the screen: 12.3.10 3.3 ms a frame over the silhouette, 8.2.10 2.4, 6.2.6 1.8 but
        // with notches where its line turns; ./autotest.sh perf --paused, outlines --edgeshapes).
        public static int EdgeProbeDirs = 8, EdgeProbeRings = 2, EdgeRings = 10;
        // Otherwise the player's numbers in the Advanced options (Settings.outlineReach, outlineProbeDirs, outlineProbeRings,
        // outlineRings: 6.2.6 at 8 pixels by default, thinner close up and notched a little where the lines turn).
        public static bool EdgeShapeForced;   // the autotest sets the fields above for a run, whatever the option says
        public static bool EdgeNearerOnly = true;   // the quick look starts the full search only for a part nearer than the pixel (the autotest's perf: off as before)
        public static bool EdgePartsOnly;   // the parts' codes drawn but no edge pass (the autotest's perf, to measure what the rest costs)
        public static bool EdgeWeaponBoxes;   // a weapon's own box even inside its figure's (as before; the autotest's perf)
        public static int WeaponBoxesSkipped;   // weapon boxes left out as inside their figure's (the autotest's checks)
        // Each figure slot's box this frame, true space, so a weapon it holds inside it needn't have its own, which went over
        // the same pixels again.
        private static readonly Vector3[] slotMin = new Vector3[Slots], slotMax = new Vector3[Slots];
        private static readonly int[] slotBoxFrame = new int[Slots];
        private static bool screenNow;
        private static FieldInfo photoDrawing;
        private static bool photoTried;

        // Whether the screen-space outline can be drawn at all this session (the shaders are there and IsoRim can read the
        // scene depth). The options say so when it can't.
        public static bool ScreenOutlinesAvailable
        {
            get
            {
                TryScreen();
                return edgeShader != null && idShader != null && idTexShader != null && !depthBroken;
            }
        }

        public static OutlineMode Mode
        {
            get { return SettingsMod.Settings != null ? SettingsMod.Settings.outlineMode : OutlineMode.Silhouette; }
        }

        private static void TryScreen()
        {
            if (screenTried)
            {
                return;
            }
            screenTried = true;
            idShader = DefDatabase<ShaderDef>.GetNamedSilentFail(IdDef)?.Shader;
            idTexShader = DefDatabase<ShaderDef>.GetNamedSilentFail(IdTexDef)?.Shader;
            edgeShader = DefDatabase<ShaderDef>.GetNamedSilentFail(EdgeDef)?.Shader;
            if (idShader == null || idTexShader == null || edgeShader == null)
            {
                Log.Warning(Tag + "the " + EdgeDef + " shaders are missing, so the screen-space outline is drawn as the silhouette.");
            }
        }

        // Whether this figure (drawn now) takes the screen-space outline: worked out once a frame (the depth asked for, the
        // edge pass's camera values set), and again for each figure only for IsoRim's photos.
        private static bool ScreenOutlinesNow
        {
            get
            {
                if (!Outlines || Mode != OutlineMode.ScreenSpace || InPhoto())
                {
                    return false;
                }
                int frame = Time.frameCount;
                if (frame != screenFrame)
                {
                    screenFrame = frame;
                    screenNow = ScreenOutlinesAvailable && SolidMaterial() != null && PrepareEdge();
                }
                return screenNow;
            }
        }

        // IsoRim's photo of the map being drawn (its internal Photos.Drawing): the photo camera's render makes no scene
        // grabs, so the figure takes the shell outline there.
        private static bool InPhoto()
        {
            if (!photoTried)
            {
                photoTried = true;
                photoDrawing = AccessTools.Field(AccessTools.TypeByName("IsoRim.Photos"), "Drawing");
            }
            return photoDrawing != null && (bool)photoDrawing.GetValue(null);
        }

        // Once a frame: IsoRim's depth texture asked for, the edge material and the cover box made, the camera's values set.
        // False (the silhouette is drawn this frame) when anything is missing.
        private static bool PrepareEdge()
        {
            if (!RequestDepth())
            {
                return false;
            }
            Camera cam = Find.Camera;
            if (cam == null)
            {
                return false;
            }
            if (edge == null)
            {
                edge = new Material(edgeShader);
                edge.name = "Marus_Edge";
                cover = CubeMesh();
            }
            Matrix4x4 p = cam.projectionMatrix;
            bool perspective = Mathf.Abs(p.m33) < 0.5f;
            Settings s = SettingsMod.Settings;
            float pawn = s != null ? s.pawnOutlineWidth : Settings.DefaultPawnOutlineWidth;
            float weapon = s != null ? s.weaponOutlineWidth : Settings.DefaultWeaponOutlineWidth;
            edge.SetVector("_EdgeProj", new Vector4(p.m00, p.m11, p.m22, p.m23));
            edge.SetVector("_EdgeWidth", new Vector4(pawn, weapon, perspective ? 1f : 0f, LineShade));
            float self = s != null && s.screenSelfOutlines ? Mathf.Max(s.selfOutlineStep, EdgeNearer) : 0f;
            bool forced = EdgeShapeForced || s == null;
            float most = forced ? EdgeMostPixels : s.outlineReach;
            int dirs = forced ? EdgeProbeDirs : s.outlineProbeDirs;
            int probeRings = forced ? EdgeProbeRings : s.outlineProbeRings;
            int rings = forced ? EdgeRings : s.outlineRings;
            edge.SetVector("_EdgeTune", new Vector4(most, EdgeNearer, self, EdgeFullSearch ? 1f : 0f));
            edge.SetVector("_EdgeShape", new Vector4(dirs, probeRings, rings, EdgeNearerOnly ? 1f : 0f));
            return true;
        }

        // IsoRim's scene depth texture (its internal DepthGrab: public, but not part of its Api). False, for the rest of the
        // session, if IsoRim has given it up or the class has gone.
        private static bool RequestDepth()
        {
            if (depthBroken)
            {
                return false;
            }
            try
            {
                if (!Depth())
                {
                    depthBroken = true;
                    Log.Warning(Tag + "IsoRim can't read the scene depth, so the screen-space outline is drawn as the silhouette.");
                }
            }
            catch (Exception e)
            {
                depthBroken = true;
                Log.Warning(Tag + "IsoRim's scene depth is out of reach (" + e.GetType().Name + "), so the screen-space outline is drawn as the silhouette.");
            }
            return !depthBroken;
        }

        // Apart, so a missing IsoRim member fails here, where RequestDepth catches it, not as that method is compiled.
        private static bool Depth()
        {
            return !DepthGrab.Broken && DepthGrab.Request();
        }

        // This slot's material for a part's codes: the plain one, or a textured part's own (its image, to be cut out as it is).
        private static Material IdMaterial(Material solid, bool weapon)
        {
            bool textured = solid != null && hairShader != null && solid.shader == hairShader && solid.mainTexture != null;
            Material[] set;
            if (textured)
            {
                if (!idTextured.TryGetValue(solid.mainTexture, out set))
                {
                    if (idTextured.Count > 4000)
                    {
                        idTextured.Clear();   // images let go of (hair read again) leave their entries; start over now and then
                    }
                    set = new Material[Slots];
                    idTextured[solid.mainTexture] = set;
                }
            }
            else
            {
                set = weapon ? idWeapon : idPawn;
            }
            Material m = set[slot];
            if (m == null)
            {
                m = new Material(textured ? idTexShader : idShader);
                m.name = "Marus_OutlineId_" + slot + (weapon ? "_weapon" : "");
                if (textured)
                {
                    m.mainTexture = solid.mainTexture;
                }
                // Unlit, as IsoRim knows it (the peek fade on stacked floors), then the code.
                Api.ConfigureFigureMaterial(m, FigureMaterialKind.Unlit);
                m.SetFloat("_OutlineId", (2 + 2 * slot + (weapon ? 1 : 0)) / 255f);
                set[slot] = m;
            }
            return m;
        }

        // Adds (once) and sets a piece's code part: its solid mesh again with this slot's code material.
        private static void SetIdPart(PieceMeshes m, Material solid)
        {
            if (m.idAt < 0)
            {
                m.idAt = m.parts.Count;
                m.parts.Add(new MeshPart { mesh = m.solid, outline = false });
            }
            m.parts[m.idAt].material = IdMaterial(solid, false);
        }

        // The edge pass over a figure: a box round its joints as posed (model units, placed by trueSpace), once a frame and
        // slot (a figure is drawn as several pieces).
        private static void DrawFigureCover(Posed p, Matrix4x4 trueSpace)
        {
            int frame = Time.frameCount;
            if (p.coverFrame == frame && p.coverSlot == slot || p.whole == null || p.shape == null || p.shape.boneHead == null)
            {
                return;
            }
            p.coverFrame = frame;
            p.coverSlot = slot;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            int n = Mathf.Min(p.whole.Length, p.shape.boneHead.Length);
            for (int b = 0; b < n; b++)
            {
                Vector3 at = p.whole[b].MultiplyPoint3x4(p.shape.boneHead[b]);
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
            if (n == 0)
            {
                return;
            }
            Vector3 room = Vector3.one * CoverMargin;
            DrawCover(new Bounds((min + max) * 0.5f, max - min + 2f * room), trueSpace, true);
        }

        // The edge pass over a box (local, placed by trueSpace), made roomier by the widest line, in true space. figure: the
        // figure's own box, noted for its slot; else (a weapon) left out if inside the figure's box this frame, as its pixels
        // are worked already (a box inside another is inside it on screen too).
        private static void DrawCover(Bounds local, Matrix4x4 trueSpace, bool figure)
        {
            if (edge == null || cover == null)
            {
                return;
            }
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? local.min.x : local.max.x, (i & 2) == 0 ? local.min.y : local.max.y,
                    (i & 4) == 0 ? local.min.z : local.max.z);
                Vector3 at = trueSpace.MultiplyPoint(corner);
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
            Settings s = SettingsMod.Settings;
            float wide = s != null ? Mathf.Max(s.pawnOutlineWidth, s.weaponOutlineWidth) : Settings.DefaultPawnOutlineWidth;
            Vector3 grow = Vector3.one * (wide + 0.01f);
            min -= grow;
            max += grow;
            int frame = Time.frameCount;
            if (figure)
            {
                slotMin[slot] = min;
                slotMax[slot] = max;
                slotBoxFrame[slot] = frame;
            }
            else if (!EdgeWeaponBoxes && slotBoxFrame[slot] == frame && Inside(min, max, slotMin[slot], slotMax[slot]))
            {
                WeaponBoxesSkipped++;
                return;
            }
            if (EdgePartsOnly)
            {
                return;
            }
            Matrix4x4 box = Matrix4x4.Translate((min + max) * 0.5f) * Matrix4x4.Scale(max - min);
            Api.SubmitFigureMesh(cover, edge, box, null, FigureMeshFlags.NoCull);
            ScreenCovers++;
        }

        // A model IsoRim made, held by the figure (DrawJoined), with the screen-space outline: its own faces as IsoRim draws
        // them, the same faces again with this slot's weapon code, and the edge pass over a box round it.
        private static void DrawJoinedScreen(List<FigureMeshPart> parts, Matrix4x4 trueSpace)
        {
            Material id = IdMaterial(null, true);
            bool any = false;
            Bounds all = new Bounds();
            for (int i = 0; i < parts.Count; i++)
            {
                FigureMeshPart part = parts[i];
                if (part.Mesh == null || part.Outline || part.Material == null)
                {
                    continue;
                }
                Api.SubmitFigureMesh(part.Mesh, part.Material, trueSpace, null, FigureMeshFlags.LightPass);
                Api.SubmitFigureMesh(part.Mesh, id, trueSpace, null, FigureMeshFlags.None);
                if (any)
                {
                    all.Encapsulate(part.Mesh.bounds);
                }
                else
                {
                    all = part.Mesh.bounds;
                    any = true;
                }
            }
            if (any)
            {
                DrawCover(all, trueSpace, false);
            }
        }

        private static bool Inside(Vector3 min, Vector3 max, Vector3 outerMin, Vector3 outerMax)
        {
            return min.x >= outerMin.x && min.y >= outerMin.y && min.z >= outerMin.z && max.x <= outerMax.x && max.y <= outerMax.y && max.z <= outerMax.z;
        }

        // A unit cube about the origin, its faces turned out (the edge pass draws its back faces).
        private static Mesh CubeMesh()
        {
            Mesh m = new Mesh();
            m.name = "Marus_EdgeCover";
            Vector3[] v = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                v[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            }
            m.vertices = v;
            m.triangles = new int[]
            {
                0, 2, 1, 1, 2, 3,   // -z
                4, 5, 6, 5, 7, 6,   // +z
                0, 1, 4, 1, 5, 4,   // -y
                2, 6, 3, 3, 6, 7,   // +y
                0, 4, 2, 2, 4, 6,   // -x
                1, 3, 5, 3, 7, 5    // +x
            };
            m.RecalculateBounds();
            return m;
        }
    }
}
