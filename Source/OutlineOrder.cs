using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using IsoRim;

namespace M3AT
{
    // The chibi's outline is its silhouette alone: its solid faces (MarusSmooth, MarusSmoothTex) set stencil bit 64, the
    // one bit neither the game nor IsoRim uses, and its outline (MarusOutline) is drawn only where that bit is clear. With
    // every chibi drawn at the same queues, all the bodies went down before any outline, so a chibi's outline was also
    // left off wherever it crossed another chibi behind it. So each figure drawn in a frame gets its own run of Steps
    // queues from FirstQueue (Slot, set by NextFigure): its plain faces, its textured ones, its outline, and last a pass
    // (MarusStencilClear) that clears the bit again over all its meshes, so the next figure starts from a clear bit. The
    // materials are copied once per slot (Slotted). Past Slots figures in a frame the slots come round again, and two
    // figures in one slot are back to leaving each other's lines off where they cross.
    public sealed partial class SkinnedFigure
    {
        private const int FirstQueue = 2002;   // after IsoRim's solids (2000, 2001), well before its ground shadows (2420)
        private const int Steps = 4;           // queues per figure: plain, textured, outline, clear
        private const int Slots = 64;          // figures a frame before the slots come round again
        private const int PlainStep = 0, TexturedStep = 1, OutlineStep = 2, ClearStep = 3;
        private const string ClearDef = "MarusStencilClear";

        private static int slot, slotsUsed, slotFrame = -1;
        private static int figuresLast;   // how many figures the last frame drew: only those on screen, as IsoRim leaves the rest out (PoseEvery)
        private static readonly Dictionary<Material, Material[]> slotted = new Dictionary<Material, Material[]>();
        private static Material clear;
        private static bool clearTried;

        // Called before each figure is drawn (DrawDressed): the next slot of this frame.
        public static void NextFigure()
        {
            int frame = Time.frameCount;
            if (frame != slotFrame)
            {
                slotFrame = frame;
                figuresLast = slotsUsed;
                slotsUsed = 0;
            }
            slot = slotsUsed++ % Slots;
        }

        // This slot's copy of one of the chibi's own materials, at its step's queue; anything else (IsoRim's, used when
        // ours are missing) as it is.
        private static Material Slotted(Material m)
        {
            if (m == null)
            {
                return null;
            }
            int step;
            if (m == smooth)
            {
                step = PlainStep;
            }
            else if (hairShader != null && m.shader == hairShader)
            {
                step = TexturedStep;
            }
            else if (m == outline || m == lines)
            {
                step = OutlineStep;
            }
            else if (m == clear)
            {
                step = ClearStep;
            }
            else if (m == stencilSet)
            {
                step = PlainStep;
            }
            else
            {
                return m;
            }
            Material[] copies;
            if (!slotted.TryGetValue(m, out copies))
            {
                copies = new Material[Slots];
                slotted[m] = copies;
            }
            Material c = copies[slot];
            if (c == null)
            {
                c = new Material(m);
                c.name = m.name + "_" + slot;
                c.renderQueue = FirstQueue + slot * Steps + step;
                if (m == smooth)
                {
                    // Its own light pass: IsoRim keeps one per material. Ours, as it is skinned on the graphics card.
                    IsoGlow.Use(c, IsoGlow.OwnCell);
                    SkinMark(c);
                }
                copies[slot] = c;
            }
            return c;
        }

        // The pass that clears bit 64 again: no colour, no depth, over the whole of each mesh. Null without its def (the
        // outline then misses where chibis cross, as before).
        private static Material ClearMaterial()
        {
            if (!clearTried)
            {
                clearTried = true;
                ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(ClearDef);
                if (def == null || def.Shader == null)
                {
                    Log.Warning(Tag + "the " + ClearDef + " shader is missing, so a chibi's outline is left off where it crosses another.");
                }
                else
                {
                    clear = new Material(def.Shader);
                    clear.name = "Marus_Stencil_Clear";
                }
            }
            return clear;
        }

        // IsoRim's ambient occlusion darkens the whole picture at its queues 2450 and 2451, by normals it finds from the depth
        // image, so on this low-poly figure it picks out each flat triangle, and the face layers (2950 on) go over skin it has
        // darkened without being darkened themselves, so the eyelid cover shows as a mask. Its screen-space shadows (2452 and
        // 2453) do the same. So the figure takes neither: each solid mesh is drawn again just after, at the depth it already
        // has, without the stencil bit (OutlineOrder's clear has been and gone). The figure's depth is still in the image, so it
        // still darkens the walls beside it and casts its shadow.
        private const string AfterDef = "MarusSmoothAfterAO", AfterTexDef = "MarusSmoothTexAfterAO";
        private static Shader afterShader, afterTexShader;
        private static bool afterTried;
        private static readonly Dictionary<Material, Material> afters = new Dictionary<Material, Material>();

        // The solid material's copy for after the occlusion; null for IsoRim's (used when ours are missing) or without the defs.
        private static Material AfterOcclusion(Material solid)
        {
            if (solid == null)
            {
                return null;
            }
            Material m;
            if (afters.TryGetValue(solid, out m))
            {
                return m;
            }
            if (!afterTried)
            {
                afterTried = true;
                afterShader = DefDatabase<ShaderDef>.GetNamedSilentFail(AfterDef)?.Shader;
                afterTexShader = DefDatabase<ShaderDef>.GetNamedSilentFail(AfterTexDef)?.Shader;
                if (afterShader == null || afterTexShader == null)
                {
                    Log.Warning(Tag + "the " + AfterDef + " shaders are missing, so IsoRim's ambient occlusion shows on the chibi.");
                }
            }
            bool textured = hairShader != null && solid.shader == hairShader;
            Shader shader = textured ? afterTexShader : solid == smooth ? afterShader : null;
            if (shader != null)
            {
                m = new Material(shader);
                m.name = solid.name + "_AfterAO";
                if (textured)
                {
                    m.mainTexture = solid.mainTexture;
                }
                m.SetFloat("_LightMode", R3DMaterials.LightModel);
                m.SetFloat("_ShadeBands", ShadeBands);
                m.SetFloat("_GlowMode", solid.GetFloat("_GlowMode"));
            }
            afters[solid] = m;
            return m;
        }

        // Whether IsoRim is drawing its occlusion or screen-space shadows this frame: if not, nothing needs drawing again.
        private static bool OcclusionShows
        {
            get { return SunShadowsSS.WillDraw || (IsoRimMod.Settings != null && IsoRimMod.Settings.ambientOcclusion && AmbientOcclusion.Available); }
        }

        // Sets this figure's slot's materials on a piece's parts, adding its two clearing parts (over the solid mesh and
        // over the outline's, which is a little bigger, so no edge pixel of the solid is missed) the first time. They are not
        // outline parts, which are left out with the outlines off: the solids set the bit either way, and it mustn't be left
        // set, for the game's own stencil tests to find.
        private static void SetSlot(PieceMeshes m, Material solid, Material line)
        {
            m.parts[0].material = Slotted(solid);
            m.parts[1].material = Slotted(line);
            Material after = AfterOcclusion(solid);
            if (after != null)
            {
                if (m.afterAt < 0)
                {
                    m.afterAt = m.parts.Count;
                    m.parts.Add(new DynamicModels.Part { mesh = m.solid, material = after, outline = false });
                }
                m.parts[m.afterAt].material = after;
            }
            Material c = Slotted(ClearMaterial());
            if (c == null)
            {
                return;
            }
            if (m.clearAt < 0)
            {
                m.clearAt = m.parts.Count;
                m.parts.Add(new DynamicModels.Part { mesh = m.solid, material = c, outline = false });
                m.parts.Add(new DynamicModels.Part { mesh = m.outline, material = c, outline = false });
            }
            m.parts[m.clearAt].material = c;
            m.parts[m.clearAt + 1].material = c;
        }

        // IsoRim's models carried on the figure (its held and holstered weapons) share the chibi's one outline. Drawn as IsoRim
        // draws them, their outline is IsoRim's moving one: a shell pushed View3D.OutlinePush (0.2 cells) away from the camera,
        // so wherever the body was less than that behind a weapon (a pistol held low before the thigh), the body hid its line.
        // So the model's solid faces are drawn by IsoRim's own materials, but each also sets stencil bit 64 in this figure's
        // slot (MarusStencilSet), its shell is drawn with the chibi's outline (OutlinePush, only where the bit is clear) and its
        // light pass, and the slot's clear goes over both: the figure and what it carries come out as one silhouette.
        private const string StencilSetDef = "MarusStencilSet";
        private static Material stencilSet;
        private static bool stencilSetTried;
        private static Func<Model, Color, string, List<DynamicModels.Part>> partsFor;
        private static bool partsForTried;

        // A model's parts with those added, and where the ones whose material goes by the slot are.
        private sealed class Joined
        {
            public List<DynamicModels.Part> parts = new List<DynamicModels.Part>();
            public List<int> lineAt = new List<int>(), markAt = new List<int>(), setAt = new List<int>(), clearAt = new List<int>();
        }

        // By IsoRim's own list of the model's parts (DynamicModels.PartsFor, cached per model and colour): a list it has thrown
        // away (DynamicModels.Forget) is never asked for again, so its entry only waits for the next clear-out.
        private static readonly Dictionary<List<DynamicModels.Part>, Joined> joined = new Dictionary<List<DynamicModels.Part>, Joined>();
        private const int JoinedMost = 500;

        // Draws model m (tint and swatch as IsoRim's DynamicModels.DrawMatrix takes them) with drawn (drawn space, before the
        // floor shift, as DrawMatrix's) as part of the figure being drawn (NextFigure's slot). False, drawing nothing, where
        // that can't be done (the outlines off or over the body too, a shader missing, an IsoRim without PartsFor): the caller
        // then draws it as IsoRim does.
        public static bool DrawJoined(Model m, Color tint, string swatch, Matrix4x4 drawn)
        {
            if (m == null || !Outlines || !Silhouette || OutlineMaterial() != outline || outline == null || outlineMark == null
                || ClearMaterial() == null || StencilSetMaterial() == null || !PartsForBound())
            {
                return false;
            }
            List<DynamicModels.Part> theirs = partsFor(m, tint, swatch);
            if (theirs == null || theirs.Count == 0)
            {
                return true;
            }
            Joined j;
            if (!joined.TryGetValue(theirs, out j))
            {
                if (joined.Count >= JoinedMost)
                {
                    joined.Clear();
                }
                j = JoinedOf(theirs);
                joined[theirs] = j;
            }
            Material line = Slotted(outline), set = Slotted(stencilSet), c = Slotted(clear);
            for (int i = 0; i < j.lineAt.Count; i++)
            {
                j.parts[j.lineAt[i]].material = line;
            }
            for (int i = 0; i < j.markAt.Count; i++)
            {
                j.parts[j.markAt[i]].material = outlineMark;
            }
            for (int i = 0; i < j.setAt.Count; i++)
            {
                j.parts[j.setAt[i]].material = set;
            }
            for (int i = 0; i < j.clearAt.Count; i++)
            {
                j.parts[j.clearAt[i]].material = c;
            }
            // DrawParts puts the squash back in front of local; drawn has it already.
            DynamicModels.DrawParts(j.parts, Vector3.zero, 0f, Matrix4x4.Scale(new Vector3(1f, View3D.Squash, 1f)) * drawn);
            return true;
        }

        private static Joined JoinedOf(List<DynamicModels.Part> theirs)
        {
            Joined j = new Joined();
            List<Mesh> cleared = new List<Mesh>();
            for (int i = 0; i < theirs.Count; i++)
            {
                DynamicModels.Part p = theirs[i];
                if (p.mesh == null)
                {
                    continue;
                }
                if (p.outline)
                {
                    j.lineAt.Add(j.parts.Count);
                    j.parts.Add(new DynamicModels.Part { mesh = p.mesh, outline = true });
                    j.markAt.Add(j.parts.Count);
                    j.parts.Add(new DynamicModels.Part { mesh = p.mesh, outline = true });
                }
                else
                {
                    j.parts.Add(p);   // IsoRim's own solid, textured or glowing faces, as they are
                    if (!p.beam)
                    {
                        j.setAt.Add(j.parts.Count);
                        j.parts.Add(new DynamicModels.Part { mesh = p.mesh, outline = false });
                    }
                }
                if (!p.beam && !cleared.Contains(p.mesh))
                {
                    cleared.Add(p.mesh);
                }
            }
            // Not outline parts, so they are drawn with the outlines off too (as SetSlot's): the bit mustn't be left set.
            for (int i = 0; i < cleared.Count; i++)
            {
                j.clearAt.Add(j.parts.Count);
                j.parts.Add(new DynamicModels.Part { mesh = cleared[i], outline = false });
            }
            return j;
        }

        private static Material StencilSetMaterial()
        {
            if (!stencilSetTried)
            {
                stencilSetTried = true;
                ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(StencilSetDef);
                if (def == null || def.Shader == null)
                {
                    Log.Warning(Tag + "the " + StencilSetDef + " shader is missing, so held weapons keep IsoRim's outline.");
                }
                else
                {
                    stencilSet = new Material(def.Shader);
                    stencilSet.name = "Marus_Stencil_Set";
                }
            }
            return stencilSet;
        }

        // IsoRim's private DynamicModels.PartsFor, which DrawMatrix draws: the model's ready-made parts in one colour.
        private static bool PartsForBound()
        {
            if (!partsForTried)
            {
                partsForTried = true;
                try
                {
                    MethodInfo mi = AccessTools.Method(typeof(DynamicModels), "PartsFor", new Type[] { typeof(Model), typeof(Color), typeof(string) });
                    if (mi != null && mi.ReturnType == typeof(List<DynamicModels.Part>))
                    {
                        partsFor = (Func<Model, Color, string, List<DynamicModels.Part>>)Delegate.CreateDelegate(
                            typeof(Func<Model, Color, string, List<DynamicModels.Part>>), mi);
                    }
                }
                catch (Exception e)
                {
                    partsFor = null;
                    Log.Warning(Tag + "IsoRim's DynamicModels.PartsFor could not be bound: " + e.Message);
                }
                if (partsFor == null)
                {
                    Log.Warning(Tag + "IsoRim's DynamicModels.PartsFor is missing, so held weapons keep IsoRim's outline.");
                }
            }
            return partsFor != null;
        }
    }
}
