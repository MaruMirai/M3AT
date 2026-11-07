using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Facial Animation's face on the chibi's head, as IsoRim puts it on its own heads (IsoRim's FaceDecal). Each of the
    // pawn's face layers (eyes, lids, brows, mouth, blush...) is drawn on the face patch the exporter writes with every
    // frame (face_*.obj: the front of the head as posed in that frame, its texture coordinates projected from the front in
    // the head bone's frame, so the face turns and bobs with the head). Facial Animation is read by reflection only. The
    // game does not run a pawn's render tree while the model stands in for it, so the face's animation is stepped here,
    // once a tick. Without Facial Animation the head stays bare.
    //
    // As in the IsoRim Nals Experimentals Patch, each layer is drawn with this mod's shader (MarusFaceLayer), which keeps
    // the colour maths of the game shader the layer was made for but lights it by IsoRim's sun as the head under it is
    // lit. Facial Animation's own materials are unlit, so a skin-coloured layer (the eyelid cover) would stand out brighter
    // than the head. The head's sprite outline (the head's Basic layer, and the skin layer when it is the whole sprite
    // skin) is left out, since it would wrap round the sides of the 3D head; the head's mood covers and highlights
    // (Experimentals' blush, hot and cold faces) and skin marks (freckles, moles) are kept.
    public static class FaceLayers
    {
        private const string Tag = "[M3AT] ";
        private const float MouthZoom = 1f;   // how much the mouth is enlarged round itself (IsoRim's default is 1.6; the chibi face is already large)
        private static readonly Vector2 MouthCentre = new Vector2(0.5f, 1f - 319.5f / 512f);   // where it is in Facial Animation's textures (u, v from the bottom)
        private const float MouthRaise = 0.012f;   // how far up the face the mouth is moved, in texture widths (it sits low on the chibi's short chin)
        // With the Nose_Pig key the snout's flat end covers the face from the nose tip nearly to the chin (texture rows 302 to
        // 332 at 512), so the mouth goes down onto its lower half, below the nostrils (anim_scripts/generation/pigSnout/make_nostrils.py).
        private const float MouthRaisePig = -0.0117f;

        // One frame's face patch: the face and its copy with the mouth enlarged, and the head's middle, which each layer
        // is grown out from (cells, the model's own space).
        public class Patch
        {
            public Mesh face;
            public Mesh mouth;
            public Mesh mouthLow;   // the mouth's copy moved down for the pig snout (MouthRaisePig)
            public bool lowUsed;    // mouthLow has been drawn, so a moving patch keeps its vertices posed (SkinnedFigure)
            public Vector3 centre;
            public bool smooth;   // carries normals (SkinnedFigure): shaded smoothly as the body is (MarusFace.frag.glsl _Smooth)
            public List<Vector2> restUvs;   // the exported texture places, which FaceTuning moves from
            public int tuned;               // the FaceTuning.Version its meshes' texture places were last laid for
        }

        private static bool tried, available;
        private static Type partType, masterType;
        private static FieldInfo controllerField, layerTypeField, overrideRotField;
        private static MethodInfo updateMethod;
        private static FieldInfo rotDrawModeField, settingsField, switchEyesField;
        private static MethodInfo rightEyeMethod, leftEyeMethod;
        private static Shader layerShader, glowShader;
        private const int LayerQueue = 2950;   // before IsoRim's light pass (3099)

        private class Face
        {
            public Pawn pawn;
            public PawnRenderNode root;
            public PawnRenderNode master;
            public readonly List<PawnRenderNode> parts = new List<PawnRenderNode>();
            public readonly List<string> kinds = new List<string>();   // the controller's type name
            public readonly List<string> layers = new List<string>();  // its layer (NLFacialAnimationLayerType: Cover, Bottom, ...)
            public int tick = -1;
            public int missing;   // the halves of the face whose eye is missing (CutLeft, CutRight), looked at once a tick
            public int missingTick = -1;
        }

        // Halves of Facial Animation's face texture (MarusFace.frag.glsl _EyeCut). Its eyeball mask colours the right
        // half with the "right eye" part's colour, so that is the half a missing right eye leaves, unless its setting
        // swaps the eyes.
        private const int CutLeft = 1, CutRight = 2, CutBoth = 3;

        private static readonly Dictionary<int, Face> faces = new Dictionary<int, Face>();
        private static readonly object south = Rot4.South;   // boxed once (overrideHeadRot is set every frame)

        // Faces of pawns no longer on a map (gone, dead) are let go now and then; one drawn again is read anew.
        private const int PruneEvery = 2500;   // ticks
        private static int prunedAt = -PruneEvery;
        private static readonly List<int> gone = new List<int>();

        public static bool Available
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    Init();
                }
                return available;
            }
        }

        private static void Init()
        {
            try
            {
                if (!ModsConfig.IsActive("Nals.FacialAnimation"))
                {
                    return;
                }
                partType = GenTypes.GetTypeInAnyAssembly("FacialAnimation.NLFacialAnimationPartNode");
                masterType = GenTypes.GetTypeInAnyAssembly("FacialAnimation.NLFacialAnimationMasterNode");
                if (partType == null || masterType == null)
                {
                    Log.Warning(Tag + "Facial Animation's render nodes were not found; the chibi's head stays bare.");
                    return;
                }
                controllerField = partType.GetField("controller");
                layerTypeField = partType.GetField("layerType");
                overrideRotField = partType.GetField("overrideHeadRot");
                updateMethod = masterType.GetMethod("UpdateAnimation", Type.EmptyTypes);
                // For missing eyes (MissingEyes); without them both eyes are always drawn.
                rotDrawModeField = partType.GetField("rotDrawMode");
                FindEyes();
                layerShader = DefDatabase<ShaderDef>.GetNamedSilentFail("MarusFaceLayer")?.Shader;
                glowShader = DefDatabase<ShaderDef>.GetNamedSilentFail("MarusFaceGlow")?.Shader;
                available = controllerField != null && layerTypeField != null && overrideRotField != null && layerShader != null && glowShader != null;
                Log.Message(Tag + "Facial Animation's face on the chibi: " + (available ? "ready" : "not found"));
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "Facial Animation's face on the chibi is unavailable: " + e.Message);
                available = false;
            }
        }

        // Facial Animation's eye labels and its eye swap setting (EyeLabels, MissingEyes).
        private static void FindEyes()
        {
            Type helper = GenTypes.GetTypeInAnyAssembly("FacialAnimation.GraphicHelper");
            rightEyeMethod = helper?.GetMethod("GetRightEyeLabel", new[] { typeof(string) });
            leftEyeMethod = helper?.GetMethod("GetLeftEyeLabel", new[] { typeof(string) });
            settingsField = GenTypes.GetTypeInAnyAssembly("FacialAnimation.FacialAnimationMod")?.GetField("Settings", BindingFlags.Public | BindingFlags.Static);
            switchEyesField = settingsField?.FieldType.GetField("SwitchLeftAndRightEyes");
        }

        // ---- Hurt eyes. Facial Animation colours each eye by its part's health (a blind eye's iris white, its shine
        // gone) only when it loads the face's textures: when the save loads or the face is changed, not when the pawn is
        // hurt or healed. So whenever a pawn's health changes (HediffSet.DirtyCache) and an eye's health is not what it
        // was, its eyeball controller is told to load them again. This mends the game's own sprite face too.

        private static Type eyeballType;
        private static FieldInfo controllerPawnField;
        private static MethodInfo setDirtyMethod, reloadMethod;
        private static readonly Dictionary<int, Vector2> eyeHealth = new Dictionary<int, Vector2>();   // by thingIDNumber: right, left (0 .. 1)

        public static void PatchEyes(Harmony harmony)
        {
            try
            {
                if (!ModsConfig.IsActive("Nals.FacialAnimation"))
                {
                    return;
                }
                eyeballType = GenTypes.GetTypeInAnyAssembly("FacialAnimation.EyeballControllerComp");
                Type controller = GenTypes.GetTypeInAnyAssembly("FacialAnimation.IFacialAnimationController");
                setDirtyMethod = controller?.GetMethod("SetDirty", Type.EmptyTypes);
                reloadMethod = controller?.GetMethod("ReloadIfNeed", Type.EmptyTypes);
                for (Type t = eyeballType; t != null && controllerPawnField == null; t = t.BaseType)
                {
                    controllerPawnField = t.GetField("pawn", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                }
                FindEyes();
                MethodInfo dirty = AccessTools.Method(typeof(HediffSet), nameof(HediffSet.DirtyCache));
                if (eyeballType == null || setDirtyMethod == null || reloadMethod == null || controllerPawnField == null || rightEyeMethod == null || dirty == null)
                {
                    Log.Warning(Tag + "Facial Animation's eyes were not found; hurt eyes show as they were when the game loaded.");
                    return;
                }
                harmony.Patch(dirty, postfix: new HarmonyMethod(typeof(FaceLayers), nameof(HealthPostfix)));
                MethodInfo genes = AccessTools.Method(typeof(Pawn_GeneTracker), "Notify_GenesChanged");
                if (genes != null)
                {
                    harmony.Patch(genes, postfix: new HarmonyMethod(typeof(FaceLayers), nameof(GenesPostfix)));
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "hurt eyes are not shown: " + e.Message);
            }
        }

        // Facial Animation blushes a pawn with the Naked thought or an ideoligion's "uncovered" disapproval (its and
        // Experimentals' Thought_Naked animation: the blush head shape and worried brows). A pawn with the Furskin gene is
        // drawn covered in fur, so that blush looks wrong: those thoughts are left out of the list Facial Animation picks
        // its animations by (FAHelper.GetThoughts), which is all it reads them for. The mood itself is untouched.
        public static void PatchBlush(Harmony harmony)
        {
            try
            {
                if (!ModsConfig.IsActive("Nals.FacialAnimation"))
                {
                    return;
                }
                Type helper = GenTypes.GetTypeInAnyAssembly("FacialAnimation.FAHelper");
                MethodInfo thoughts = helper == null ? null : AccessTools.Method(helper, "GetThoughts", new Type[] { typeof(Pawn) });
                if (thoughts == null || thoughts.ReturnType != typeof(List<Thought>))
                {
                    Log.Warning(Tag + "Facial Animation's thoughts were not found; furskinned pawns blush when naked.");
                    return;
                }
                harmony.Patch(thoughts, postfix: new HarmonyMethod(typeof(FaceLayers), nameof(ThoughtsPostfix)));
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "furskinned pawns blush when naked: " + e.Message);
            }
        }

        private static void ThoughtsPostfix(Pawn pawn, List<Thought> __result)
        {
            if (pawn == null || __result == null || __result.Count == 0 || !WalkFigures.HasFurskin(pawn))
            {
                return;
            }
            __result.RemoveAll(NakedBlush);
        }

        // The thoughts Thought_Naked is played for: Naked and the ideoligion's ..Uncovered_Disapproved_<gender> ones.
        private static bool NakedBlush(Thought t)
        {
            string name = t?.def?.defName;
            return name != null && (name == "Naked" || name.Contains("Uncovered_Disapproved"));
        }

        // The pawn's eyeball controller, or null if it has none or Facial Animation has not set it up yet (it will load
        // the eyes as they are when it does).
        private static ThingComp EyesOf(Pawn pawn)
        {
            List<ThingComp> comps = pawn.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                if (eyeballType.IsInstanceOfType(comps[i]))
                {
                    return controllerPawnField.GetValue(comps[i]) != null ? comps[i] : null;
                }
            }
            return null;
        }

        // Facial Animation also reads an eye colour gene (Eyes_Red, Eyes_Gray, its EyeballColorDefs) only when it loads
        // the textures, and a gene changing doesn't make it load them: a gene added to a living pawn would show only
        // after the save is loaded again. Off the main thread it is only marked, for when the render tree is rebuilt.
        private static void GenesPostfix(Pawn_GeneTracker __instance)
        {
            Pawn pawn = __instance.pawn;
            if (pawn == null)
            {
                return;
            }
            try
            {
                ThingComp eyes = EyesOf(pawn);
                if (eyes == null)
                {
                    return;
                }
                setDirtyMethod.Invoke(eyes, null);
                if (UnityData.IsInMainThread)
                {
                    reloadMethod.Invoke(eyes, null);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "reloading Facial Animation's eyes failed: " + e, 61938204);
            }
        }

        private static void HealthPostfix(HediffSet __instance)
        {
            Pawn pawn = __instance.pawn;
            if (pawn == null || !UnityData.IsInMainThread || !pawn.SpawnedOrAnyParentSpawned)
            {
                return;
            }
            try
            {
                ThingComp eyes = EyesOf(pawn);
                if (eyes == null)
                {
                    return;
                }
                string[] labels = EyeLabels(pawn.def);
                Vector2 health = new Vector2(EyeHealth(pawn, labels[0]), EyeHealth(pawn, labels[1]));
                Vector2 was;
                if (eyeHealth.TryGetValue(pawn.thingIDNumber, out was) && was == health)
                {
                    return;
                }
                eyeHealth[pawn.thingIDNumber] = health;   // the first look reloads too: the eye may have changed since the load
                setDirtyMethod.Invoke(eyes, null);
                reloadMethod.Invoke(eyes, null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "reloading Facial Animation's eyes failed: " + e, 61938204);
            }
        }

        // An eye's health as Facial Animation reads it (0 missing or blind .. 1), or 1 for a body without that eye.
        private static float EyeHealth(Pawn pawn, string label)
        {
            List<BodyPartRecord> parts = pawn.RaceProps.body.AllParts;
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].untranslatedCustomLabel == label)
                {
                    return Math.Min(1f, PawnCapacityUtility.CalculatePartEfficiency(pawn.health.hediffSet, parts[i]));
                }
            }
            return 1f;
        }

        // Reads a face_*.obj from the exporter. Like IsoRim's loader, z is flipped (the file faces -Z, the game's model
        // space +Z) and the triangles are kept in the file's order. Null if the file is missing.
        public static Patch Load(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            List<Vector3> pos = new List<Vector3>();
            List<Vector2> tex = new List<Vector2>();
            List<Vector3> verts = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            Vector3 centre = Vector3.zero;
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] t = raw.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 5 && t[0] == "#" && t[1] == "centre")
                {
                    centre = new Vector3(Num(t[2]), Num(t[3]), -Num(t[4]));
                }
                else if (t.Length >= 4 && t[0] == "v")
                {
                    pos.Add(new Vector3(Num(t[1]), Num(t[2]), -Num(t[3])));
                }
                else if (t.Length >= 3 && t[0] == "vt")
                {
                    tex.Add(new Vector2(Num(t[1]), Num(t[2])));
                }
                else if (t.Length >= 4 && t[0] == "f")
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        string[] ix = t[k].Split('/');
                        verts.Add(pos[int.Parse(ix[0]) - 1]);
                        uvs.Add(tex[int.Parse(ix[1]) - 1]);
                    }
                }
            }
            if (verts.Count == 0)
            {
                return null;
            }
            List<int> tris = new List<int>(verts.Count);
            for (int i = 0; i < verts.Count; i++)
            {
                tris.Add(i);
            }
            List<Vector2> placed = Tuned(uvs);
            return new Patch { face = MeshOf(verts, placed, tris), mouth = MeshOf(verts, Zoomed(placed, MouthRaise), tris), mouthLow = MeshOf(verts, Zoomed(placed, MouthRaisePig), tris), centre = centre, restUvs = uvs, tuned = FaceTuning.Version };
        }

        // A patch whose vertices are moved every frame (SkinnedFigure), made from its rest pose. The face and mouth meshes
        // share the vertex order, so both take the same posed vertices.
        public static Patch NewPatch(Vector3[] verts, Vector2[] uvs, int[] tris, Vector3 centre)
        {
            List<Vector3> v = new List<Vector3>(verts);
            List<Vector2> uv = new List<Vector2>(uvs);
            List<Vector2> placed = Tuned(uv);
            List<int> t = new List<int>(tris);
            Patch p = new Patch { face = MeshOf(v, placed, t), mouth = MeshOf(v, Zoomed(placed, MouthRaise), t), mouthLow = MeshOf(v, Zoomed(placed, MouthRaisePig), t), centre = centre, restUvs = uv, tuned = FaceTuning.Version };
            p.face.MarkDynamic();
            p.mouth.MarkDynamic();
            p.mouthLow.MarkDynamic();
            // Set once, roomy enough for any pose (cells, the model's own space): it is only for culling.
            p.face.bounds = p.mouth.bounds = p.mouthLow.bounds = new Bounds(new Vector3(0f, 0.7f, 0f), new Vector3(2.5f, 2f, 2.5f));
            return p;
        }

        // The mouth's texture places: enlarged round the mouth and moved up the face by raise (texture widths). FaceTuning's
        // mouth numbers go on top (temporary, for lining the face up in game).
        private static List<Vector2> Zoomed(List<Vector2> uvs, float raise)
        {
            List<Vector2> zoomed = new List<Vector2>(uvs.Count);
            Vector2 shift = new Vector2(FaceTuning.mouthX, raise + FaceTuning.mouthY);
            float zoom = MouthZoom * FaceTuning.mouthScale;
            for (int i = 0; i < uvs.Count; i++)
            {
                zoomed.Add(MouthCentre + (uvs[i] - MouthCentre - shift) / zoom);
            }
            return zoomed;
        }

        // How far up the face Facial Animation's art is moved, in texture widths (found in game with FaceTuning; it sat a
        // little high on the chibi's head). The ridges, the flat beard and eye wounds share the patch, so move with it.
        private const float FaceRaise = -0.006f;

        // The whole face's texture places moved up by FaceRaise, then moved and scaled about the eye line by FaceTuning
        // (texture widths; up and to the pawn's left, as the art moves, so the places move the other way).
        private static readonly Vector2 FacePivot = new Vector2(0.5f, 1f - 70.6f / 128f);
        private static List<Vector2> Tuned(List<Vector2> uvs)
        {
            List<Vector2> moved = new List<Vector2>(uvs.Count);
            Vector2 shift = new Vector2(FaceTuning.faceX, FaceRaise + FaceTuning.faceY);
            for (int i = 0; i < uvs.Count; i++)
            {
                moved.Add(FacePivot + (uvs[i] - FacePivot - shift) / FaceTuning.faceScale);
            }
            return moved;
        }

        // Lays the patch's texture places again when the tuning has changed since they were last laid.
        private static void Retune(Patch patch)
        {
            if (patch.tuned == FaceTuning.Version || patch.restUvs == null)
            {
                return;
            }
            patch.tuned = FaceTuning.Version;
            List<Vector2> face = Tuned(patch.restUvs);
            patch.face.SetUVs(0, face);
            patch.mouth.SetUVs(0, Zoomed(face, MouthRaise));
            if (patch.mouthLow != null)
            {
                patch.mouthLow.SetUVs(0, Zoomed(face, MouthRaisePig));
            }
        }

        private static float Num(string s)
        {
            return float.Parse(s, CultureInfo.InvariantCulture);
        }

        private static Mesh MeshOf(List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            List<Color32> colors = new List<Color32>(verts.Count);
            List<Vector3> tags = new List<Vector3>(verts.Count);
            for (int i = 0; i < verts.Count; i++)
            {
                colors.Add(new Color32(255, 255, 255, 255));
                tags.Add(RoofClip.MovingTag);   // clipped under the roof over where it is drawn, as IsoRim's moving models are
            }
            Mesh m = new Mesh();
            m.SetVertices(verts);
            m.SetColors(colors);
            m.SetUVs(0, uvs);
            m.SetUVs(1, tags);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        private static void Prune()
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (now >= prunedAt && now - prunedAt < PruneEvery)
            {
                return;
            }
            prunedAt = now;
            foreach (KeyValuePair<int, Face> e in faces)
            {
                Pawn p = e.Value.pawn;
                if (p == null || p.Destroyed || p.Dead || !p.Spawned)
                {
                    gone.Add(e.Key);
                }
            }
            for (int i = 0; i < gone.Count; i++)
            {
                faces.Remove(gone[i]);
            }
            gone.Clear();
        }

        // Which of a controller's layers go on the 3D head. The head's Basic layer (and the skin's, when it is the whole
        // sprite skin: see Draw) carry the sprite head's dark outline.
        private static bool Wanted(string kind, object layer)
        {
            if (kind == "HeadControllerComp")
            {
                string l = layer != null ? layer.ToString() : "";
                return l == "Cover" || l == "Highlight";
            }
            return true;
        }

        // The pawn's Facial Animation layers, bottom first, read anew whenever its render tree is rebuilt.
        private static Face FaceOf(Pawn pawn)
        {
            try
            {
                PawnRenderTree tree = pawn.Drawer != null && pawn.Drawer.renderer != null ? pawn.Drawer.renderer.renderTree : null;
                if (tree == null)
                {
                    return null;
                }
                if (!tree.Resolved)
                {
                    tree.EnsureInitialized(PawnRenderFlags.None);
                }
                if (tree.rootNode == null)
                {
                    return null;
                }
                Prune();
                Face f;
                if (faces.TryGetValue(pawn.thingIDNumber, out f) && f.root == tree.rootNode)
                {
                    return f;
                }
                f = new Face { pawn = pawn, root = tree.rootNode };
                List<PawnRenderNode> found = new List<PawnRenderNode>();
                List<KeyValuePair<float, int>> order = new List<KeyValuePair<float, int>>();
                Stack<PawnRenderNode> open = new Stack<PawnRenderNode>();
                open.Push(tree.rootNode);
                while (open.Count > 0)
                {
                    PawnRenderNode n = open.Pop();
                    if (n == null)
                    {
                        continue;
                    }
                    if (masterType.IsInstanceOfType(n))
                    {
                        f.master = n;
                    }
                    else if (partType.IsInstanceOfType(n))
                    {
                        object controller = controllerField.GetValue(n);
                        string kind = controller != null ? controller.GetType().Name : null;
                        if (kind != null && Wanted(kind, layerTypeField.GetValue(n)))
                        {
                            order.Add(new KeyValuePair<float, int>(n.Props != null ? n.Props.baseLayer : 0f, found.Count));
                            found.Add(n);
                        }
                    }
                    if (n.children != null)
                    {
                        for (int i = 0; i < n.children.Length; i++)
                        {
                            open.Push(n.children[i]);
                        }
                    }
                }
                // Drawn bottom layer first.
                order.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
                foreach (KeyValuePair<float, int> o in order)
                {
                    PawnRenderNode n = found[o.Value];
                    f.parts.Add(n);
                    f.kinds.Add(controllerField.GetValue(n).GetType().Name);
                    object layer = layerTypeField.GetValue(n);
                    f.layers.Add(layer != null ? layer.ToString() : "");
                }
                faces[pawn.thingIDNumber] = f;
                return f;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "reading Facial Animation's face failed: " + e, 61938201);
                return null;
            }
        }

        // ---- Lit materials: one of ours per Facial Animation material and place in the stack.

        private const int ModeTransparent = 0, ModeCutoutSkin = 1, ModeComplex = 2, ModeSkinOverlay = 3, ModeGlow = 4, ModeLidCover = 5;

        private static Dictionary<Shader, int> modes;

        // The game shader's colour maths (MarusFace.frag.glsl _FaceMode), or -1 for a shader not handled here.
        private static int ModeOf(Shader s)
        {
            if (modes == null)
            {
                modes = new Dictionary<Shader, int>();
                AddMode("Map/Transparent", ModeTransparent);
                AddMode("Map/CutoutSkin", ModeCutoutSkin);
                AddMode("Map/CutoutSkinOverride", ModeCutoutSkin);
                AddMode("Map/CutoutComplex", ModeComplex);
                AddMode("Map/CutoutSkinOverlay", ModeSkinOverlay);
                AddMode("Map/MoteGlow", ModeGlow);
            }
            int m;
            return s != null && modes.TryGetValue(s, out m) ? m : -1;
        }

        private static void AddMode(string path, int mode)
        {
            Shader s = ShaderDatabase.LoadShader(path);
            if (s != null && !modes.ContainsKey(s))
            {
                modes.Add(s, mode);
            }
        }

        private static readonly Dictionary<Material, Material[]> lit = new Dictionary<Material, Material[]>();
        private const int MaxLayers = 48;
        private static readonly int faceModeId = Shader.PropertyToID("_FaceMode");

        private static Material LitFor(Material source, int mode, int place)
        {
            Material[] byPlace;
            if (!lit.TryGetValue(source, out byPlace))
            {
                byPlace = new Material[MaxLayers];
                lit[source] = byPlace;
            }
            place = Mathf.Clamp(place, 0, MaxLayers - 1);
            Material m = byPlace[place];
            if (m == null)
            {
                m = new Material(mode == ModeGlow ? glowShader : layerShader);
                m.name = "MarusFace_" + source.name + "_" + place;
                m.SetFloat(faceModeId, mode);
                if (mode != ModeGlow)
                {
                    m.renderQueue = LayerQueue + place;   // drawn in the stack's order
                }
                byPlace[place] = m;
            }
            // Copied every time: Facial Animation may repaint a material's texture (render-texture layers).
            m.mainTexture = source.mainTexture;
            m.color = source.color;
            if (mode == ModeLidCover)
            {
                // Rounded as IsoRim rounds the tint of the head under it (DynamicModels.PartsFor), so the two match exactly.
                Color32 c = source.color;
                c.r = (byte)(c.r & 0xF8);
                c.g = (byte)(c.g & 0xF8);
                c.b = (byte)(c.b & 0xF8);
                m.color = c;
            }
            if (mode == ModeComplex)
            {
                m.SetColor(ShaderPropertyIDs.ColorTwo, source.HasProperty(ShaderPropertyIDs.ColorTwo) ? source.GetColor(ShaderPropertyIDs.ColorTwo) : Color.white);
            }
            if (mode == ModeComplex || mode == ModeSkinOverlay || mode == ModeLidCover)
            {
                Texture mask = source.HasProperty(ShaderPropertyIDs.MaskTex) ? source.GetTexture(ShaderPropertyIDs.MaskTex) : null;
                m.SetTexture(ShaderPropertyIDs.MaskTex, mask != null ? mask : (mode == ModeComplex ? BaseContent.BlackTex : BaseContent.WhiteTex));
            }
            return m;
        }

        // Draws the pawn's face on this frame's patch, placed as Api.DrawModel places the model: position true space (cells),
        // yaw degrees about the vertical, size the model's scale.
        // wounds: those on the eyes themselves (Wounds.cs), drawn in the stack under the lids. lowMouth: the mouth moved down
        // below the pig snout's nostrils (MouthRaisePig).
        public static void Draw(Pawn pawn, Patch patch, Vector3 position, float yaw, float size, EyeWound[] wounds = null, bool lowMouth = false)
        {
            if (patch == null || !Available || !Api.ViewReady)
            {
                return;
            }
            Face f = FaceOf(pawn);
            if (f == null || f.parts.Count == 0)
            {
                return;
            }
            try
            {
                int tick = Find.TickManager.TicksGame;
                if (f.tick != tick && f.master != null && updateMethod != null)
                {
                    f.tick = tick;
                    updateMethod.Invoke(f.master, null);
                }
                Retune(patch);
                Matrix4x4 placed = PlacedAt(position, yaw, size);
                if (f.missingTick != tick)
                {
                    f.missingTick = tick;
                    f.missing = MissingEyes(pawn);
                }
                // A moving patch poses mouthLow only once it has been asked for, so the first frame keeps the plain mouth.
                Mesh mouth = lowMouth && patch.lowUsed && patch.mouthLow != null ? patch.mouthLow : patch.mouth;
                patch.lowUsed |= lowMouth;
                int place = 0;
                Texture iris = null, white = null;   // this frame's eye textures, the shape of the eye the wounds are cut to
                bool woundsDrawn = wounds == null || wounds.Length == 0;
                for (int i = 0; i < f.parts.Count; i++)
                {
                    PawnRenderNode node = f.parts[i];
                    object rot = overrideRotField.GetValue(node);
                    overrideRotField.SetValue(node, south);
                    Graphic g = node.GraphicFor(pawn);
                    overrideRotField.SetValue(node, rot);   // as it was, for the game's own drawing (portraits)
                    string kind = f.kinds[i];
                    if (!woundsDrawn)
                    {
                        // The lid's own layers come after the white (its Bottom) and the iris, so a wound drawn here goes
                        // on the eye and under the lid, which closes over it as the pawn blinks.
                        string layer = f.layers[i];
                        if (kind == "LidControllerComp" && layer != "Bottom")
                        {
                            DrawEyeWounds(wounds, iris, white, f.missing, patch, placed, ref place);
                            woundsDrawn = true;
                        }
                        else if (kind == "LidControllerComp")
                        {
                            white = LayerTexture(g);
                        }
                        else if (kind == "EyeballControllerComp" && layer.EndsWith("Basic", StringComparison.Ordinal))
                        {
                            iris = LayerTexture(g);
                        }
                    }
                    int cut = 0;
                    if (f.missing != 0 && kind == "EyeballControllerComp")
                    {
                        cut = f.missing;   // the eye's white, iris and shine
                    }
                    else if (f.missing != 0 && kind == "LidControllerComp")
                    {
                        // The open lid on the eyes still there, and Facial Animation's closed lid (as it blinks) on the
                        // missing ones.
                        Graphic closed = ClosedLid(node, pawn);
                        if (closed != null && closed != g)
                        {
                            DrawLayer(g, kind, f.layers[i], f.missing, patch, mouth, placed, ref place);
                            g = closed;
                            cut = CutBoth & ~f.missing;
                        }
                    }
                    DrawLayer(g, kind, f.layers[i], cut, patch, mouth, placed, ref place);
                }
                if (!woundsDrawn)
                {
                    DrawEyeWounds(wounds, iris, white, f.missing, patch, placed, ref place);   // no lid layers at all
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "drawing Facial Animation's face on the chibi failed: " + e, 61938202);
            }
        }

        // One layer on the patch, with the halves in cut (CutLeft, CutRight) left out.
        private static void DrawLayer(Graphic g, string kind, string layer, int cut, Patch patch, Mesh mouth, Matrix4x4 placed, ref int place)
        {
            Material gm = g != null ? g.MatSouth : null;
            if (gm == null || gm.mainTexture == null || IsDummy(g) || cut == CutBoth)
            {
                return;   // an empty layer
            }
            int mode = ModeOf(gm.shader);
            if (kind == "LidControllerComp" && layer == "Cover" && mode == ModeSkinOverlay)
            {
                mode = ModeLidCover;   // only the cover's shape: its art is the sprite head's painted shading
            }
            if (kind == "SkinControllerComp" && mode != ModeTransparent)
            {
                return;   // the whole sprite skin, outline and all: the model's head is the skin
            }
            // Every layer just off the head: they write no depth and their queues set their order, so none needs
            // to stand further out than the one under it. Growing each one more pushed the top layers out through
            // hair laid close on the head (HairBuilder keeps it ClearBU off), showing as a pale band under the
            // hair. No depth write also means IsoRim's light pass over the head still darkens them at night.
            float grow = 1.003f;
            Matrix4x4 local = Matrix4x4.Translate(patch.centre)
                * Matrix4x4.Scale(new Vector3(grow, grow, grow))
                * Matrix4x4.Translate(-patch.centre);
            Material mat = mode >= 0 ? LitFor(gm, mode, place) : gm;   // a shader not known here: Facial Animation's own, unlit
            Graphics.DrawMesh(kind == "MouthControllerComp" ? mouth : patch.face, Floors.DrawnMatrix(placed * local), mat, 0, null, 0, mode >= 0 ? BlockFor(patch.smooth, cut) : null);
            place++;
        }

        // ---- A wound on the eye itself. Unlike the other wounds it is laid on the face patch by texture place, not on the
        // skin, and cut to the eye's own shape: Facial Animation's white and iris as they are this frame. Under the lid
        // layers, as some lid covers' openings run out to their edge, so the covers alone wouldn't stop it at the eye.

        public struct EyeWound
        {
            public Vector4 frame;   // its middle (texture places on the face), then its way across over its span (cos, sin)
            public Vector4 cell;    // its square on the wound sheet: place and size
            public Color colour;    // times the sheet; as a Decal's, alpha under 1 makes it a scar, this the skin colour
        }

        private const int ModeEyeWound = 7;
        private static readonly int woundAtId = Shader.PropertyToID("_WoundAt");
        private static readonly int woundCellId = Shader.PropertyToID("_WoundCell");
        private static readonly int maskTwoId = Shader.PropertyToID("_MaskTwo");
        private static readonly Dictionary<ValueTuple<Texture, Texture, int>, Material> woundMaterials = new Dictionary<ValueTuple<Texture, Texture, int>, Material>();

        // A layer's texture, or null for an empty one.
        private static Texture LayerTexture(Graphic g)
        {
            return g != null && !IsDummy(g) && g.MatSouth != null ? g.MatSouth.mainTexture : null;
        }

        // The wounds, one layer each. Each white and iris has its own material, as a material is read when it is drawn, not
        // when it is asked for, and pawns' eyes differ. With neither texture there is no eye to put them on.
        private static void DrawEyeWounds(EyeWound[] wounds, Texture iris, Texture white, int cut, Patch patch, Matrix4x4 placed, ref int place)
        {
            if ((iris == null && white == null) || Wounds.Sheet == null)
            {
                return;
            }
            float grow = 1.003f;   // as DrawLayer
            Matrix4x4 local = Matrix4x4.Translate(patch.centre)
                * Matrix4x4.Scale(new Vector3(grow, grow, grow))
                * Matrix4x4.Translate(-patch.centre);
            Matrix4x4 drawn = Floors.DrawnMatrix(placed * local);
            for (int i = 0; i < wounds.Length; i++)
            {
                ValueTuple<Texture, Texture, int> key = new ValueTuple<Texture, Texture, int>(iris, white, place);
                Material m;
                if (!woundMaterials.TryGetValue(key, out m))
                {
                    m = new Material(layerShader);
                    m.name = "MarusFace_eye_wound_" + place;
                    m.mainTexture = Wounds.Sheet;
                    m.SetFloat(faceModeId, ModeEyeWound);
                    m.SetTexture(ShaderPropertyIDs.MaskTex, iris != null ? iris : BaseContent.ClearTex);
                    m.SetTexture(maskTwoId, white != null ? white : BaseContent.ClearTex);
                    m.renderQueue = LayerQueue + place;
                    woundMaterials[key] = m;
                }
                // A block of its own, as each wound differs from pawn to pawn.
                MaterialPropertyBlock block = new MaterialPropertyBlock();
                block.SetColor("_Color", wounds[i].colour);
                block.SetVector(woundAtId, wounds[i].frame);
                block.SetVector(woundCellId, wounds[i].cell);
                block.SetFloat("_EyeCut", cut);
                if (patch.smooth)
                {
                    block.SetFloat("_Smooth", 1f);
                    block.SetFloat("_ShadeBands", SkinnedFigure.ShadeBands);
                }
                Graphics.DrawMesh(patch.face, drawn, m, 0, null, 0, block);
                place++;
            }
        }

        // How red the game's damage flash draws the face being drawn now, 0 .. 1 in sixteenths (HitFlash.Of): set by the
        // figure's draw (WalkFigures.DrawDressed) for its face layers, ridges and flat beard, and put back to 0 after.
        public static float Flash;
        private static readonly int flashId = Shader.PropertyToID("_IsoFlash");

        // The per-draw settings: smooth shading (the skinned chibi), the halves left out and the hit flash (Flash, which
        // MarusFace.frag.glsl leaves off the wounds that share these blocks). Null for none of them.
        private static readonly MaterialPropertyBlock[] blocks = new MaterialPropertyBlock[8 * (HitFlash.Steps + 1)];
        internal static MaterialPropertyBlock BlockFor(bool smooth, int cut)
        {
            int step = Mathf.RoundToInt(Flash * HitFlash.Steps);
            int i = (smooth ? 4 : 0) + cut + 8 * step;
            if (i == 0)
            {
                return null;
            }
            if (blocks[i] == null)
            {
                MaterialPropertyBlock b = new MaterialPropertyBlock();
                if (smooth)
                {
                    b.SetFloat("_Smooth", 1f);
                    b.SetFloat("_ShadeBands", SkinnedFigure.ShadeBands);
                }
                b.SetFloat("_EyeCut", cut);
                b.SetFloat(flashId, step / (float)HitFlash.Steps);
                blocks[i] = b;
            }
            return blocks[i];
        }

        // The matrix a figure is drawn by: its position (true space, cells), its yaw (degrees about the vertical) and size.
        private static Matrix4x4 PlacedAt(Vector3 position, float yaw, float size)
        {
            // The whole map is drawn squashed flat (IsoRim's View3D); the height is divided by the same here.
            float s = Api.HeightSquash;
            return Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.TRS(Vector3.zero, Quaternion.AngleAxis(yaw, Vector3.up), new Vector3(size, size, size));
        }

        // ---- A beard laid flat on the patch (SkinnedFigure.DrawFlatBeard). Its texture is in the face's own layout
        // (HairBuilder.FlatFace), so no Facial Animation is needed: the patch is the chibi's own. It is drawn just under the
        // face layers, cut out as a plain layer is (mode 1), and tinted by the hair colour.
        private const int FlatQueue = LayerQueue - 1;
        // The gene's facial ridges go over the face layers, as the lid cover is skin-coloured and reaches past the eyes, and
        // would hide them and change with each blink; under the wounds (Wounds.Queue, 2999).
        private const int RidgeQueue = 2998;
        private static Shader flatShader;
        private static bool flatTried;

        // The facial ridges' material for one design's texture, as a flat beard's but under it.
        public static Material RidgeMaterial(Texture2D texture)
        {
            Material m = FlatMaterial(texture);
            if (m != null)
            {
                m.name = "MarusFace_ridges";
                m.renderQueue = RidgeQueue;
            }
            return m;
        }

        // A flat beard's material for its texture; null if the MarusFaceLayer shader is missing (said once).
        public static Material FlatMaterial(Texture2D texture)
        {
            if (!flatTried)
            {
                flatTried = true;
                flatShader = DefDatabase<ShaderDef>.GetNamedSilentFail("MarusFaceLayer")?.Shader;
                if (flatShader == null)
                {
                    Log.Warning(Tag + "the MarusFaceLayer shader is missing, so beards and facial ridges are not drawn flat on the chibi.");
                }
            }
            if (flatShader == null)
            {
                return null;
            }
            Material m = new Material(flatShader);
            m.name = "MarusFace_flat_beard";
            m.mainTexture = texture;
            m.color = Color.white;
            m.renderQueue = FlatQueue;
            m.SetFloat(faceModeId, ModeCutoutSkin);
            return m;
        }

        // Draws a flat beard on this frame's patch, placed as Draw places the face. Its colour is the tint, multiplied into the
        // texture by the shader.
        public static void DrawFlat(Patch patch, Material mat, Color tint, Vector3 position, float yaw, float size)
        {
            if (patch == null || mat == null || !Api.ViewReady)
            {
                return;
            }
            // Grown as the layers are (DrawLayer), so it stands just off the patch.
            float grow = 1.003f;
            Matrix4x4 local = Matrix4x4.Translate(patch.centre)
                * Matrix4x4.Scale(new Vector3(grow, grow, grow))
                * Matrix4x4.Translate(-patch.centre);
            // A block of its own, as the tint differs from pawn to pawn.
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            block.SetColor("_Color", tint);
            if (patch.smooth)
            {
                block.SetFloat("_Smooth", 1f);
                block.SetFloat("_ShadeBands", SkinnedFigure.ShadeBands);
            }
            if (Flash > 0f)
            {
                block.SetFloat(flashId, Flash);
            }
            Graphics.DrawMesh(patch.face, Floors.DrawnMatrix(PlacedAt(position, yaw, size) * local), mat, 0, null, 0, block);
        }

        // The halves of the face whose eye part is missing (not a prosthetic: that replaces the part). The eyes are
        // found as Facial Animation finds them, by their labels for the pawn's race (FaceAdjustmentDef).
        // True if Facial Animation's setting swaps the eyes; false without it.
        public static bool EyesSwapped()
        {
            object settings = settingsField != null ? settingsField.GetValue(null) : null;
            return settings != null && switchEyesField != null && (bool)switchEyesField.GetValue(settings);
        }

        private static int MissingEyes(Pawn pawn)
        {
            if (rightEyeMethod == null || leftEyeMethod == null || pawn.health == null)
            {
                return 0;
            }
            string[] labels = EyeLabels(pawn.def);
            bool swap = EyesSwapped();
            int cut = 0;
            List<Hediff_MissingPart> gone = pawn.health.hediffSet.GetMissingPartsCommonAncestors();
            for (int i = 0; i < gone.Count; i++)
            {
                BodyPartRecord part = gone[i].Part;
                string label = part != null ? part.untranslatedCustomLabel : null;
                if (label == labels[0])
                {
                    cut |= swap ? CutLeft : CutRight;
                }
                else if (label == labels[1])
                {
                    cut |= swap ? CutRight : CutLeft;
                }
            }
            return cut;
        }

        private static readonly Dictionary<ThingDef, string[]> eyeLabels = new Dictionary<ThingDef, string[]>();

        // The race's right and left eye part labels in Facial Animation (FaceAdjustmentDef), cached.
        private static string[] EyeLabels(ThingDef race)
        {
            string[] labels;
            if (!eyeLabels.TryGetValue(race, out labels))
            {
                labels = new[] { "right eye", "left eye" };
                try
                {
                    labels[0] = (string)rightEyeMethod.Invoke(null, new object[] { race.defName }) ?? labels[0];
                    labels[1] = (string)leftEyeMethod.Invoke(null, new object[] { race.defName }) ?? labels[1];
                }
                catch (Exception)
                {
                    // a race Facial Animation has no adjustment for: its defaults
                }
                eyeLabels[race] = labels;
            }
            return labels;
        }

        // This lid layer as Facial Animation draws it closed (its "close" LidShapeDef, as when the pawn blinks), or null.
        // Read from the controller's graphicList, by layer, with NLGraphic_Collection.GetSubGraphic.
        private static Graphic ClosedLid(PawnRenderNode node, Pawn pawn)
        {
            try
            {
                object controller = controllerField.GetValue(node);
                if (controller == null || rotDrawModeField == null)
                {
                    return null;
                }
                FieldInfo list = null;
                for (Type t = controller.GetType(); t != null && list == null; t = t.BaseType)
                {
                    list = t.GetField("graphicList", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                }
                System.Collections.IDictionary graphics = list != null ? list.GetValue(controller) as System.Collections.IDictionary : null;
                object collection = graphics != null ? graphics[layerTypeField.GetValue(node)] : null;
                if (collection == null)
                {
                    return null;
                }
                MethodInfo sub = collection.GetType().GetMethod("GetSubGraphic");
                ParameterInfo[] ps = sub != null ? sub.GetParameters() : null;
                if (ps == null || ps.Length != 3)
                {
                    return null;
                }
                Def close = GenDefDatabase.GetDefSilentFail(ps[1].ParameterType, "close", false);
                if (close == null)
                {
                    return null;
                }
                return sub.Invoke(collection, new object[] { pawn, close, rotDrawModeField.GetValue(node) }) as Graphic;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "reading Facial Animation's closed eyelid failed: " + e, 61938203);
                return null;
            }
        }

        // Facial Animation's empty layer (GraphicPool.dummyGraphic, Things/Common/dummy): see-through in the game's shaders.
        private static bool IsDummy(Graphic g)
        {
            return g.path != null && g.path.EndsWith("/dummy", StringComparison.OrdinalIgnoreCase);
        }
    }
}
