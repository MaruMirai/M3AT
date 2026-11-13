using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
    [StaticConstructorOnStartup]
    public static partial class FaceLayers
    {
        private const string Tag = "[M3AT] ";
        private const float MouthZoom = 1f;   // how much the mouth is enlarged round itself (IsoRim's default is 1.6; the chibi face is already large)
        internal static readonly Vector2 MouthCentre = new Vector2(0.5f, 1f - 319.5f / 512f);   // where it is in Facial Animation's textures (u, v from the bottom)
        internal const float MouthRaise = 0.012f;   // how far up the face the mouth is moved, in texture widths (it sits low on the chibi's short chin)
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
            // Posed on the graphics card (SkinnedFigure.FaceOnGpu): the pawn's bones (three rows each, null when posed on the
            // CPU), counted up whenever they change, and its own blocks carrying them (BlockFor), each with the count it last
            // took them at, as the bones differ from pawn to pawn.
            public Vector4[] bones;
            public int bonesAt;
            public object gpuShape;   // the shape whose rest pose the meshes hold (SkinnedFigure.Shape)
            public MaterialPropertyBlock[] blocks;
            public int[] blocksAt;
        }

        private static readonly int bonesId = Shader.PropertyToID("_MarusBones");

        // The pawn's bones into a block made for one draw (the flat beard, ridges, eye wounds), if the patch is posed on the
        // graphics card.
        private static void AddBones(MaterialPropertyBlock block, Patch patch)
        {
            if (patch != null && patch.bones != null)
            {
                block.SetVectorArray(bonesId, patch.bones);
            }
        }

        private static bool tried, available;
        private static Type partType, masterType;
        private static FieldInfo controllerField, layerTypeField, overrideRotField;
        private static MethodInfo updateMethod;
        private static FieldInfo rotDrawModeField, settingsField, switchEyesField;
        private static MethodInfo rightEyeMethod, leftEyeMethod;
        private static Shader layerShader, glowShader;
        private const int LayerQueue = 2950;   // before IsoRim's light pass (3099)

        // Facial Animation's master node decides once, as it is made, whether the pawn's face animates: it keeps the pawn's
        // controller only if the controller says it can (CheckUpdateableInitial), and that says no until the controller has
        // met its pawn, which it does only on its first tick (InitializeIfNeed). A render tree made before the pawn has
        // ticked (drawn paused: a game just loaded, a pawn just spawned; this mod's own EnsureInitialized in FaceOf) has a
        // master with no controller, and that face never moves again, blinking, talking, crying, blushing, until the tree is
        // made again. So where the master has none: the controller is met to its pawn and asked again, by Facial Animation's
        // own code (its settings for which pawns animate decide), and given back to the master if it can animate. Looked at
        // again every ControllerRetry ticks while it can't, so a pawn Facial Animation leaves still costs nothing.
        private const int ControllerRetry = 250;
        private static Type controllerCompType;
        private static FieldInfo masterControllerField;
        private static MethodInfo controllerInit, controllerCheck;
        private static bool controllerTried;

        private static void KeepController(Face f, int tick)
        {
            if (!controllerTried)
            {
                controllerTried = true;
                controllerCompType = GenTypes.GetTypeInAnyAssembly("FacialAnimation.FacialAnimationControllerComp");
                masterControllerField = masterType != null ? AccessTools.Field(masterType, "controller") : null;
                controllerInit = controllerCompType != null ? AccessTools.Method(controllerCompType, "InitializeIfNeed") : null;
                controllerCheck = controllerCompType != null ? AccessTools.Method(controllerCompType, "CheckUpdateableInitial") : null;
                if (masterControllerField == null || controllerInit == null || controllerCheck == null)
                {
                    Log.Warning(Tag + "can't reach Facial Animation's controller (its code has changed?): a face drawn before its pawn first ticks stays still.");
                }
            }
            if (masterControllerField == null || controllerInit == null || controllerCheck == null || masterControllerField.GetValue(f.master) != null
                || tick - f.controllerTried < ControllerRetry)
            {
                return;
            }
            f.controllerTried = tick;
            ThingComp comp = ((ThingWithComps)f.pawn).AllComps.Find(c => controllerCompType.IsInstanceOfType(c));
            if (comp == null)
            {
                return;
            }
            controllerInit.Invoke(comp, null);
            if ((bool)controllerCheck.Invoke(comp, null))
            {
                masterControllerField.SetValue(f.master, comp);
            }
        }

        private class Face
        {
            public Pawn pawn;
            public PawnRenderNode root;
            public PawnRenderNode master;
            public readonly List<PawnRenderNode> parts = new List<PawnRenderNode>();
            public readonly List<string> kinds = new List<string>();   // the controller's type name
            public readonly List<string> layers = new List<string>();  // its layer (NLFacialAnimationLayerType: Cover, Bottom, ...)
            public int tick = -1;
            public int controllerTried = -ControllerRetry;   // the tick the master's controller was last looked to (KeepController)
            public int missing;   // the halves of the face whose eye is missing (CutLeft, CutRight), looked at once a tick
            public int missingTick = -1;
            public Color white = Color.white;   // the eye whites' tint this tick (WhiteTintOf)
            public int whiteTick = -1;
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
                stackShader = DefDatabase<ShaderDef>.GetNamedSilentFail("MarusFaceStack")?.Shader;   // without it, a draw per layer
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
            p.face.bounds = p.mouth.bounds = p.mouthLow.bounds = PatchBounds;
            return p;
        }

        // Set once, roomy enough for any pose (cells, the model's own space): it is only for culling.
        public static readonly Bounds PatchBounds = new Bounds(new Vector3(0f, 0.7f, 0f), new Vector3(2.5f, 2f, 2.5f));

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
        internal const float FaceRaise = -0.006f;

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
                tags.Add(Api.MovingMeshTag);   // clipped under the roof over where it is drawn, as IsoRim's moving models are
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
        // speaking: 0 .. 1, how much of a talking clip the figure has on; past SpeakFrom the mouth opens and closes on
        // syllables (MouthOpen), Facial Animation's "open" mouth shape swapped in for whatever mouth it shows.
        public static void Draw(Pawn pawn, Patch patch, Vector3 position, float yaw, float size, EyeWound[] wounds = null, bool lowMouth = false, float speaking = 0f)
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
                    KeepController(f, tick);
                    updateMethod.Invoke(f.master, null);
                }
                Retune(patch);
                Matrix4x4 placed = PlacedAt(position, yaw, size);
                if (f.whiteTick != tick)
                {
                    f.whiteTick = tick;
                    f.white = WhiteTintOf(pawn);
                }
                whiteTint = f.white;
                if (f.missingTick != tick)
                {
                    f.missingTick = tick;
                    f.missing = MissingEyes(pawn);
                }
                // A moving patch poses mouthLow only once it has been asked for, so the first frame keeps the plain mouth.
                Mesh mouth = lowMouth && patch.lowUsed && patch.mouthLow != null ? patch.mouthLow : patch.mouth;
                patch.lowUsed |= lowMouth;
                bool mouthOpen = speaking > SpeakFrom && MouthOpen(pawn.thingIDNumber);
#if AUTOTEST
                if (speaking > SpeakFrom)
                {
                    if (mouthOpen) { TalkOpenDraws++; } else { TalkShutDraws++; }
                }
#endif
                int place = 0;
                Texture iris = null, white = null;   // this frame's eye textures, the shape of the eye the wounds are cut to
                bool woundsDrawn = wounds == null || wounds.Length == 0;
                // Stacked (DrawStacked): the layers gathered here and drawn eight to a draw at the end. Not with wounds on
                // the eye, which go in between the layers.
                bool stacking = StackLayers && stackShader != null && woundsDrawn;
                calls.Clear();
                eyeLayers.Clear();
                for (int i = 0; i < f.parts.Count; i++)
                {
                    PawnRenderNode node = f.parts[i];
                    object rot = overrideRotField.GetValue(node);
                    overrideRotField.SetValue(node, south);
                    Graphic g = node.GraphicFor(pawn);
                    overrideRotField.SetValue(node, rot);   // as it was, for the game's own drawing (portraits)
                    string kind = f.kinds[i];
                    Vector2 shift = ShiftOf(node, g, kind, f.layers[i]);
                    if (kind == "EyeballControllerComp" && f.layers[i].EndsWith("_Basic", StringComparison.Ordinal))
                    {
                        NoteEyeLayer(g, f.layers[i], shift);   // for a prosthetic eye's iris
                    }
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
                    if (mouthOpen && kind == "MouthControllerComp")
                    {
                        Graphic open = ShapeGraphic(node, pawn, "open");
#if AUTOTEST
                        string was = g != null && g.MatSouth != null && g.MatSouth.mainTexture != null ? g.MatSouth.mainTexture.name : "none";
                        string now = open != null && open.MatSouth != null && open.MatSouth.mainTexture != null ? open.MatSouth.mainTexture.name : "none";
                        TalkTextures.Add(f.layers[i] + ": " + was + " -> " + (open == null ? "no open graphic" : IsDummy(open) ? "dummy" : now + " (" + open.path + ")"));
                        if (f.layers[i] == "Basic" && (open == null || IsDummy(open) || now == was))
                        {
                            TalkNoOpen++;
                        }
#endif
                        if (open != null && !IsDummy(open))
                        {
                            g = open;
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
                            Layer(stacking, g, kind, f.layers[i], f.missing, shift, patch, mouth, placed, ref place);
                            g = closed;
                            cut = CutBoth & ~f.missing;
                        }
                    }
                    Layer(stacking, g, kind, f.layers[i], cut, shift, patch, mouth, placed, ref place);
                }
                if (stacking)
                {
                    DrawStacked(patch, mouth, placed, ref place);
                }
                if (!woundsDrawn)
                {
                    DrawEyeWounds(wounds, iris, white, f.missing, patch, placed, ref place);   // no lid layers at all
                }
#if AUTOTEST
                PerfFaceDone(pawn);
#endif
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "drawing Facial Animation's face on the chibi failed: " + e, 61938202);
            }
        }

        // ---- Stacked: up to StackSize layers in one draw (MarusFaceStack.frag.glsl), as drawn one at a time a face took about
        // ten draws, and in the game a face changes (a blink, a new mouth) on under one frame in fifty: each mix of layers
        // gets its material once (stacked, by a hash of what it was made from) and keeps it. The glow layers are drawn as
        // before, added on after the light pass. Turned off (StackLayers), or for a face with wounds on its eyes, or one with
        // a layer whose shader isn't known here (drawn with Facial Animation's own material), each layer is a draw again.

        public static bool StackLayers = true;
#if AUTOTEST
        internal static int StackSize = 8;   // (settable by the test: -m3atStackSize)
        internal static bool StackLogged;
#else
        private const int StackSize = 8;   // the engine's 16 textures a material: a texture and a mask each
#endif
        private const int StackedMost = 4000;   // materials kept at most; past it all are let go and made again as wanted
        private static Shader stackShader;

        private struct LayerCall
        {
            public Graphic g;
            public string kind, layer;
            public int cut;
            public Vector2 shift;   // Facial Animation's offset now (ShiftOf), texture places
        }

        private struct Stacked
        {
            public Texture tex, mask;
            public Color colour, colourTwo;
            public int mode, cut;
            public bool mouth;
            public int call;   // its LayerCall, for one drawn on its own (Solo)
            public Vector2 shift;
        }

        private static readonly List<LayerCall> calls = new List<LayerCall>();
        private static readonly List<Stacked> stack = new List<Stacked>();
        private static readonly Dictionary<long, Material> stacked = new Dictionary<long, Material>();
        private static readonly int mouthMapId = Shader.PropertyToID("_MouthMap");
        private static int[] stackT, stackM, stackC;
        private static readonly int[] stackQ = { Shader.PropertyToID("_Q0"), Shader.PropertyToID("_Q1") };

        // One layer: drawn now, or kept for DrawStacked.
        private static void Layer(bool stacking, Graphic g, string kind, string layer, int cut, Vector2 shift, Patch patch, Mesh mouth, Matrix4x4 placed, ref int place)
        {
            if (stacking)
            {
                calls.Add(new LayerCall { g = g, kind = kind, layer = layer, cut = cut, shift = shift });
            }
            else
            {
                DrawLayer(g, kind, layer, cut, shift, patch, mouth, placed, ref place);
            }
        }

        // The layers gathered by Layer, in their order: the glow layers each on their own, the rest StackSize to a draw.
        private static void DrawStacked(Patch patch, Mesh mouth, Matrix4x4 placed, ref int place)
        {
            stack.Clear();
            for (int i = 0; i < calls.Count; i++)
            {
                LayerCall call = calls[i];
                Material gm = call.g != null ? call.g.MatSouth : null;
                if (gm == null || gm.mainTexture == null || IsDummy(call.g) || call.cut == CutBoth)
                {
                    continue;
                }
                int mode = ModeOf(gm.shader);
                if (mode < 0 && patch.bones != null)
                {
                    mode = ModeTransparent;   // as DrawLayer
                }
                if (mode < 0)
                {
                    // Facial Animation's own material, which only it can draw: every layer a draw of its own, in order.
                    for (int k = 0; k < calls.Count; k++)
                    {
                        DrawLayer(calls[k].g, calls[k].kind, calls[k].layer, calls[k].cut, calls[k].shift, patch, mouth, placed, ref place);
                    }
                    return;
                }
                if (call.kind == "LidControllerComp" && call.layer == "Cover" && mode == ModeSkinOverlay)
                {
                    mode = ModeLidCover;
                }
                if (call.kind == "SkinControllerComp" && mode != ModeTransparent)
                {
                    continue;   // as DrawLayer: the whole sprite skin
                }
                if (mode == ModeGlow)
                {
                    DrawLayer(call.g, call.kind, call.layer, call.cut, call.shift, patch, mouth, placed, ref place);
                    continue;
                }
                // Its colours and mask as LitFor gives its own material.
                Color colour = gm.color;
                if (mode == ModeLidCover)
                {
                    Color32 c = gm.color;
                    c.r = (byte)(c.r & 0xF8);
                    c.g = (byte)(c.g & 0xF8);
                    c.b = (byte)(c.b & 0xF8);
                    colour = c;
                }
                Color two = mode == ModeComplex && gm.HasProperty(ShaderPropertyIDs.ColorTwo) ? gm.GetColor(ShaderPropertyIDs.ColorTwo) : Color.white;
                Texture mask = null;
                if (mode == ModeComplex || mode == ModeSkinOverlay || mode == ModeLidCover)
                {
                    mask = gm.HasProperty(ShaderPropertyIDs.MaskTex) ? gm.GetTexture(ShaderPropertyIDs.MaskTex) : null;
                    mask = mask != null ? mask : (mode == ModeComplex ? BaseContent.BlackTex : BaseContent.WhiteTex);
                }
                if (call.kind == "LidControllerComp" && call.layer == "Bottom")
                {
                    colour *= whiteTint;
                }
                stack.Add(new Stacked { tex = gm.mainTexture, mask = mask, colour = colour, colourTwo = two, mode = mode, cut = call.cut, mouth = call.kind == "MouthControllerComp", call = i, shift = call.shift });
            }
            Vector4 mouthMap = MouthMap(mouth == patch.mouthLow ? MouthRaisePig : MouthRaise);
#if AUTOTEST
            if (!StackLogged)
            {
                StackLogged = true;
                System.Text.StringBuilder sb = new System.Text.StringBuilder(Tag + "stacked face, " + calls.Count + " calls, " + stack.Count + " stacked, mouth map " + mouthMap + ":");
                foreach (LayerCall call in calls)
                {
                    Material cm = call.g != null ? call.g.MatSouth : null;
                    sb.Append("\n  call " + call.kind + "/" + call.layer + " cut " + call.cut + " mat " + (cm != null ? cm.name + " shader " + (cm.shader != null ? cm.shader.name : "null") + " mode " + ModeOf(cm.shader) + " tex " + (cm.mainTexture != null ? cm.mainTexture.name + " " + cm.mainTexture.GetType().Name : "null") : "null") + " dummy " + IsDummy(call.g));
                }
                for (int i = 0; i < stack.Count; i++)
                {
                    Stacked l = stack[i];
                    sb.Append("\n  layer " + i + " mode " + l.mode + " cut " + l.cut + " mouth " + l.mouth + " tex " + (l.tex != null ? l.tex.name : "null") + " mask " + (l.mask != null ? l.mask.name : "null") + " colour " + l.colour + " two " + l.colourTwo);
                }
                Log.Message(sb.ToString());
            }
#endif
            Matrix4x4 local = Matrix4x4.Translate(patch.centre) * Matrix4x4.Scale(new Vector3(1.003f, 1.003f, 1.003f)) * Matrix4x4.Translate(-patch.centre);   // as DrawLayer
            // In order: runs of up to StackSize layers a draw, and between them any layer that must be drawn on its own.
            int from = 0;
            for (int i = 0; i <= stack.Count; i++)
            {
                bool solo = i < stack.Count && Solo(stack[i]);
                if (i < stack.Count && !solo && i - from < StackSize)
                {
                    continue;
                }
                if (i > from)
                {
                    Material mat = StackedMaterial(from, i - from, place, mouthMap);
                    MaterialPropertyBlock block = BlockFor(patch, patch.smooth, 0);
                    SetStackShifts(block, from, i - from);
                    Api.SubmitFigureMesh(patch.face, mat, placed * local, block, FigureMeshFlags.NoCull, 0, Flash);
                    place++;
                }
                if (solo)
                {
                    LayerCall call = calls[stack[i].call];
                    DrawLayer(call.g, call.kind, call.layer, call.cut, call.shift, patch, mouth, placed, ref place);
                    from = i + 1;
                }
                else
                {
                    from = i;
                }
            }
        }

        // A layer drawn on its own, not stacked: one whose texture is a render texture (the lid cover, which Facial
        // Animation paints from the sprite head), as only an atlas tile's place is sure to reach the stacked slots' _ST.
        // And a CutoutComplex one (a second colour), which the stacked material has no room for.
        private static bool Solo(Stacked l)
        {
            return l.tex is RenderTexture || l.mask is RenderTexture || l.mode == ModeComplex;
        }

        // The mouth's texture places from the face's, as Zoomed makes the mouth's: scale in xy, offset in zw.
        private static Vector4 MouthMap(float raise)
        {
            Vector2 shift = new Vector2(FaceTuning.mouthX, raise + FaceTuning.mouthY);
            float zoom = MouthZoom * FaceTuning.mouthScale;
            Vector2 offset = MouthCentre - (MouthCentre + shift) / zoom;
            return new Vector4(1f / zoom, 1f / zoom, offset.x, offset.y);
        }

        private static long Mix(long h, long v)
        {
            return (h ^ v) * 0x100000001B3L + (h >> 29);
        }

        private static long ColourKey(Color c)
        {
            return ((Color32)c).r | ((long)((Color32)c).g << 8) | ((long)((Color32)c).b << 16) | ((long)((Color32)c).a << 24);
        }

        // The material for stack[from .. from + count), drawn place-th in the face (its queue): made once for each such mix.
        private static Material StackedMaterial(int from, int count, int place, Vector4 mouthMap)
        {
            int chunk = Mathf.Clamp(place, 0, MaxLayers - 1);
            long key = Mix(Mix(unchecked((long)0xcbf29ce484222325UL), chunk), mouthMap.GetHashCode());
            for (int i = from; i < from + count; i++)
            {
                Stacked l = stack[i];
                key = Mix(key, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l.tex));
                key = Mix(key, l.mask != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l.mask) : 0);
                key = Mix(key, ColourKey(l.colour));
                key = Mix(key, ColourKey(l.colourTwo));
                key = Mix(key, l.mode * 16 + l.cut * 2 + (l.mouth ? 1 : 0));
            }
            key = Mix(key, count);
            Material m;
            if (stacked.TryGetValue(key, out m) && m != null)
            {
                return m;
            }
            if (stacked.Count >= StackedMost)
            {
                foreach (Material old in stacked.Values)
                {
                    if (old != null)
                    {
                        UnityEngine.Object.Destroy(old);
                    }
                }
                stacked.Clear();
            }
            if (stackT == null)
            {
                stackT = new int[8];
                stackM = new int[8];
                stackC = new int[8];
                for (int i = 0; i < 8; i++)
                {
                    stackT[i] = Shader.PropertyToID("_T" + i);
                    stackM[i] = Shader.PropertyToID("_M" + i);
                    stackC[i] = Shader.PropertyToID("_C" + i);
                }
            }
            m = new Material(stackShader);
            m.name = "MarusFace_stack_" + chunk;
            m.renderQueue = LayerQueue + chunk;   // in the stack's order, as the layers were
            m.SetVector(mouthMapId, mouthMap);
            // Each layer's mode, eye cut and whether it is on the mouth's places, one number each (0: not in use).
            Vector4[] q = { Vector4.zero, Vector4.zero };
            for (int i = 0; i < count; i++)
            {
                Stacked l = stack[from + i];
                m.SetTexture(stackT[i], l.tex);
                m.SetTexture(stackM[i], l.mask != null ? l.mask : BaseContent.WhiteTex);
                m.SetColor(stackC[i], l.colour);
                q[i / 4][i % 4] = 1 + l.mode + 8 * l.cut + (l.mouth ? 32 : 0);
            }
            m.SetVector(stackQ[0], q[0]);
            m.SetVector(stackQ[1], q[1]);
            stacked[key] = m;
            return m;
        }

        // One layer on the patch, with the halves in cut (CutLeft, CutRight) left out.
#if AUTOTEST
        // The perf test (AutoTest.Perf): how often a pawn's face, as drawn, differs from its last frame's (its layers'
        // materials, textures, colours, cuts and modes), to see how often a face baked into a texture would be baked again.
        private static int perfSig;
        internal static long PerfFaces, PerfFaceChanges, PerfLayers;
        private static int perfLayers;
        private static readonly Dictionary<int, int> perfLast = new Dictionary<int, int>();

        // Facial Animation's own state for the pawn, as the autotest reports it: its job, mood, pain, the thoughts it goes by
        // and the animations it has picked, by reflection (FacialAnimationControllerComp).
        internal static string FaState(Pawn pawn)
        {
            try
            {
                ThingComp comp = ((ThingWithComps)pawn).AllComps.FirstOrDefault(c => c.GetType().Name == "FacialAnimationControllerComp");
                if (comp == null)
                {
                    return "no Facial Animation controller";
                }
                object param = AccessTools.Field(comp.GetType(), "animationParam").GetValue(comp);
                Type pt = param.GetType();
                string job = (string)AccessTools.Field(pt, "currentJobName").GetValue(param);
                float mood = (float)AccessTools.Field(pt, "currentMood").GetValue(param);
                float pain = (float)AccessTools.Field(pt, "currentPain").GetValue(param);
                List<Thought> thoughts = (List<Thought>)AccessTools.Field(pt, "currentThoughts").GetValue(param);
                List<string> names = new List<string>();
                foreach (object anim in (System.Collections.IEnumerable)AccessTools.Field(comp.GetType(), "currentJobAnimationList").GetValue(comp))
                {
                    Def def = (Def)AccessTools.Field(anim.GetType(), "animationDef").GetValue(anim);
                    names.Add(def.defName);
                }
                int temporary = ((System.Collections.ICollection)AccessTools.Field(comp.GetType(), "forcedTemporaryAnimationList").GetValue(comp)).Count;
                List<string> shapes = new List<string>();
                foreach (ThingComp c in ((ThingWithComps)pawn).AllComps)
                {
                    FieldInfo shape = c.GetType().Name.EndsWith("ControllerComp") ? AccessTools.Field(c.GetType(), "Shape") : null;
                    if (shape != null)
                    {
                        object v = shape.GetValue(c);
                        shapes.Add(c.GetType().Name.Replace("ControllerComp", "") + "=" + (v is Def d ? d.defName : "null"));
                    }
                }
                return "job " + job + ", mood " + mood.ToString("F2") + ", pain " + pain.ToString("F2") + ", thoughts " + (thoughts == null ? "none" : string.Join("/", thoughts.Select(t => t.def.defName).Distinct()))
                    + ", animations " + string.Join("/", names) + (temporary > 0 ? ", " + temporary + " forced" : "") + ", shapes " + string.Join(" ", shapes)
                    + ", " + MasterReport(pawn);
            }
            catch (Exception e)
            {
                return "Facial Animation's state unreadable: " + e.Message;
            }
        }

        // Whether the face found Facial Animation's master node (whose UpdateAnimation moves the face on each tick), and
        // whether that node kept its controller (Facial Animation drops it for a pawn it won't animate).
        private static string MasterReport(Pawn pawn)
        {
            Face f;
            if (!faces.TryGetValue(pawn.thingIDNumber, out f))
            {
                return "face not read yet";
            }
            if (f.master == null)
            {
                return "no master node found (of " + f.parts.Count + " parts)";
            }
            object controller = AccessTools.Field(f.master.GetType(), "controller")?.GetValue(f.master);
            return "master node " + f.master.GetType().Name + ", controller " + (controller != null ? "kept" : "dropped") + ", last updated tick " + f.tick;
        }

        // Plays these Facial Animation animations on the pawn from startTick, as its face editor previews them.
        internal static bool FaPlay(Pawn pawn, int startTick, params string[] defNames)
        {
            Type t = GenTypes.GetTypeInAnyAssembly("FacialAnimation.FacialAnimationControllerComp");
            MethodInfo play = t != null ? AccessTools.Method(t, "PlayTemporaryAnimation", new[] { typeof(Pawn), typeof(int), typeof(string[]) }) : null;
            return play != null && (bool)play.Invoke(null, new object[] { pawn, startTick, defNames });
        }

        // Every Facial Animation animation loaded (its own and Experimentals'), by defName.
        internal static List<string> FaAnimations()
        {
            Type t = GenTypes.GetTypeInAnyAssembly("FacialAnimation.FaceAnimationDef");
            if (t == null)
            {
                return new List<string>();
            }
            Type db = typeof(DefDatabase<>).MakeGenericType(t);
            System.Collections.IEnumerable all = (System.Collections.IEnumerable)db.GetProperty("AllDefsListForReading").GetValue(null);
            List<string> names = new List<string>();
            foreach (object d in all)
            {
                names.Add(((Def)d).defName);
            }
            return names;
        }

        private static void PerfFaceDone(Pawn pawn)
        {
            int last;
            PerfFaces++;
            PerfLayers += perfLayers;
            if (!perfLast.TryGetValue(pawn.thingIDNumber, out last) || last != perfSig)
            {
                PerfFaceChanges++;
            }
            perfLast[pawn.thingIDNumber] = perfSig;
            perfSig = 17;
            perfLayers = 0;
        }
#endif

        // Red eyes from smokeleaf: the eye whites (the lid's Bottom layer, under the iris) tinted toward StonedWhite while
        // the pawn is high (its SmokeleafHigh hediff), all of it from StonedFull severity down (a joint gives 0.5, falling by
        // 1 a day) and fading out below it, so over the last few hours of the high. White when not.
        private static Color WhiteTintOf(Pawn pawn)
        {
            if (stonedDef == null)
            {
                stonedDef = DefDatabase<HediffDef>.GetNamedSilentFail("SmokeleafHigh");
                if (stonedDef == null)
                {
                    return Color.white;
                }
            }
            Hediff high = pawn.health != null ? pawn.health.hediffSet.GetFirstHediffOfDef(stonedDef) : null;
            if (high == null)
            {
                return Color.white;
            }
            // In eighths, as each tint makes its own stacked face material (StackedMaterial keys them by colour).
            float share = Mathf.Ceil(Mathf.Clamp01(high.Severity / StonedFull) * StonedSteps) / StonedSteps;
            return Color.Lerp(Color.white, StonedWhite, share);
        }

        private static HediffDef stonedDef;
        private static Color whiteTint = Color.white;   // the eye whites' tint for the face being drawn (Draw)
        private static readonly Color StonedWhite = new Color(1f, 0.74f, 0.74f);   // a slight red, times the whites' own colour
        private const float StonedSteps = 8f;
        private const float StonedFull = 0.15f;   // SmokeleafHigh severity from which the tint is whole (about 3.6 game hours left)

        private static void DrawLayer(Graphic g, string kind, string layer, int cut, Vector2 shift, Patch patch, Mesh mouth, Matrix4x4 placed, ref int place)
        {
            Material gm = g != null ? g.MatSouth : null;
            if (gm == null || gm.mainTexture == null || IsDummy(g) || cut == CutBoth)
            {
                return;   // an empty layer
            }
            int mode = ModeOf(gm.shader);
#if AUTOTEST
            perfSig = perfSig * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(gm);
            perfSig = perfSig * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(gm.mainTexture);
            perfSig = perfSig * 31 + gm.color.GetHashCode() + 7 * cut + 101 * mode + (kind == "MouthControllerComp" && mouth != patch.mouth ? 13 : 0);
            perfLayers++;
#endif
            if (mode < 0 && patch.bones != null)
            {
                mode = ModeTransparent;   // posed on the graphics card: a shader that doesn't pose it would draw it at rest
            }
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
            if (mode >= 0 && kind == "LidControllerComp" && layer == "Bottom")
            {
                mat.color *= whiteTint;   // (LitFor copies the colour afresh each draw)
            }
            MaterialPropertyBlock block = mode >= 0 ? BlockFor(patch, patch.smooth, cut) : null;
            SetShift(block, shift);
            Api.SubmitFigureMesh(kind == "MouthControllerComp" ? mouth : patch.face, mat, placed * local, block,
                FigureMeshFlags.NoCull, 0, mode >= 0 ? Flash : 0f);
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
            public bool fitIris;    // a prosthetic eye's iris: laid on Facial Animation's iris on that half (frame.x), at its size,
                                    // cut to it and moved with its gaze (IrisOn); else as frame has it, cut to the white
        }

        private const int ModeEyeWound = 7;
        private static readonly int woundAtId = Shader.PropertyToID("_WoundAt");
        private static readonly int woundCellId = Shader.PropertyToID("_WoundCell");
        private static readonly int maskTwoId = Shader.PropertyToID("_MaskTwo");
        private static readonly Dictionary<WoundKey, Material> woundMaterials = new Dictionary<WoundKey, Material>();

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
            Matrix4x4 drawn = placed * local;
            for (int i = 0; i < wounds.Length; i++)
            {
                // Its own material, its square and colour set on it, with the face layers' own block (BlockFor), as their
                // draws are known to reach the shader.
                EyeWound w = wounds[i];
                Texture shape = white ?? iris;
                Vector2 shift = Vector2.zero;
                Iris fitted;
                EyeLayer layer;
                if (w.fitIris)
                {
                    shape = iris ?? white;   // unmeasured: cut to the iris all the same, as the art fills its square
                }
                if (w.fitIris && IrisOn(w.frame.x, out fitted, out layer))
                {
                    // The art's iris (IrisArtShare of its square across) at Facial Animation's iris's size and place.
                    w.frame = new Vector4(fitted.middle.x, fitted.middle.y, IrisArtShare / fitted.radius, 0f);
                    shape = layer.tex;
                    shift = layer.shift;
                }
                WoundKey key = new WoundKey { iris = shape, white = null, place = place, frame = w.frame, cell = w.cell, colour = w.colour };
                Material m;
                if (!woundMaterials.TryGetValue(key, out m))
                {
                    m = new Material(layerShader);
                    m.name = "MarusFace_eye_wound_" + place;
                    m.mainTexture = Wounds.Sheet;
                    m.color = w.colour;
                    m.SetFloat(faceModeId, ModeEyeWound);
                    m.SetVector(woundAtId, w.frame);
                    m.SetVector(woundCellId, w.cell);
                    // The eye's shape: its white, which covers the whole opening, iris and all, else the iris. In the first mask
                    // slot, which the other face layers use too: a texture set on the second (_MaskTwo) read as clear in the game.
                    m.SetTexture(ShaderPropertyIDs.MaskTex, shape ?? BaseContent.ClearTex);
                    m.SetTexture(maskTwoId, BaseContent.ClearTex);
                    m.renderQueue = LayerQueue + place;
                    woundMaterials[key] = m;
                }
                MaterialPropertyBlock block = BlockFor(patch, patch.smooth, cut);
                SetShift(block, shift);
                Api.SubmitFigureMesh(patch.face, m, drawn, block, FigureMeshFlags.NoCull);
                place++;
            }
        }

        // An eye wound's material, by everything set on it.
        private struct WoundKey : IEquatable<WoundKey>
        {
            public Texture iris, white;
            public int place;
            public Vector4 frame, cell;
            public Color colour;

            public bool Equals(WoundKey o)
            {
                return iris == o.iris && white == o.white && place == o.place && frame == o.frame && cell == o.cell && colour == o.colour;
            }

            public override bool Equals(object o)
            {
                return o is WoundKey && Equals((WoundKey)o);
            }

            public override int GetHashCode()
            {
                int h = place;
                h = h * 31 + (iris != null ? iris.GetHashCode() : 0);
                h = h * 31 + (white != null ? white.GetHashCode() : 0);
                return h * 31 + frame.GetHashCode() * 7 + cell.GetHashCode() * 3 + colour.GetHashCode();
            }
        }

        // How red the game's damage flash draws the face being drawn now, 0 .. 1 in sixteenths (HitFlash.Of): set by the
        // figure's draw (WalkFigures.DrawDressed) for its face layers, ridges and flat beard, and put back to 0 after.
        public static float Flash;
        private const int FlashSteps = 16;   // the flash in sixteenths, as IsoRim rounds it: one block each
        private static readonly int flashId = Shader.PropertyToID("_IsoFlash");

        // The per-draw settings: smooth shading (the skinned chibi), the halves left out and the hit flash (Flash, which
        // MarusFace.frag.glsl leaves off the wounds that share these blocks). Null for none of them.
        private static readonly MaterialPropertyBlock[] blocks = new MaterialPropertyBlock[8 * (FlashSteps + 1)];
        // The same for a patch posed on the graphics card: a block of the patch's own, with the pawn's bones too (set again
        // when they have changed); a patch posed on the CPU takes the shared ones.
        internal static MaterialPropertyBlock BlockFor(Patch patch, bool smooth, int cut)
        {
            if (patch == null || patch.bones == null)
            {
                return BlockFor(smooth, cut);
            }
            int step = Mathf.RoundToInt(Flash * FlashSteps);
            int i = (smooth ? 4 : 0) + cut + 8 * step;
            if (patch.blocks == null)
            {
                patch.blocks = new MaterialPropertyBlock[blocks.Length];
                patch.blocksAt = new int[blocks.Length];
            }
            MaterialPropertyBlock b = patch.blocks[i];
            if (b == null)
            {
                b = new MaterialPropertyBlock();
                if (smooth)
                {
                    b.SetFloat("_Smooth", 1f);
                    b.SetFloat("_ShadeBands", SkinnedFigure.ShadeBands);
                }
                b.SetFloat("_EyeCut", cut);
                b.SetFloat(flashId, step / (float)FlashSteps);
                patch.blocks[i] = b;
                patch.blocksAt[i] = patch.bonesAt - 1;
            }
            if (patch.blocksAt[i] != patch.bonesAt)
            {
                b.SetVectorArray(bonesId, patch.bones);
                patch.blocksAt[i] = patch.bonesAt;
            }
            return b;
        }

        internal static MaterialPropertyBlock BlockFor(bool smooth, int cut)
        {
            int step = Mathf.RoundToInt(Flash * FlashSteps);
            int i = (smooth ? 4 : 0) + cut + 8 * step;
            if (blocks[i] == null)
            {
                MaterialPropertyBlock b = new MaterialPropertyBlock();
                if (smooth)
                {
                    b.SetFloat("_Smooth", 1f);
                    b.SetFloat("_ShadeBands", SkinnedFigure.ShadeBands);
                }
                b.SetFloat("_EyeCut", cut);
                b.SetFloat(flashId, step / (float)FlashSteps);
                blocks[i] = b;
            }
            return blocks[i];
        }

        // The matrix a figure is drawn by, in true space (Api.SubmitFigureMesh squashes it): its position (cells), its yaw
        // (degrees about the vertical) and size.
        internal static Matrix4x4 PlacedAt(Vector3 position, float yaw, float size)
        {
            return Matrix4x4.TRS(position, Quaternion.AngleAxis(yaw, Vector3.up), new Vector3(size, size, size));
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
            AddBones(block, patch);
            SetNoShift(block);
            if (patch.smooth)
            {
                block.SetFloat("_Smooth", 1f);
                block.SetFloat("_ShadeBands", SkinnedFigure.ShadeBands);
            }
            Api.SubmitFigureMesh(patch.face, mat, PlacedAt(position, yaw, size) * local, block, FigureMeshFlags.NoCull, 0, Flash);
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

        // Talking: the mouth opens and closes on syllables of SyllableSeconds, about one in SyllableRest of them left shut,
        // with a breath of PhraseRest seconds closing each phrase of PhraseSeconds. On a clock of real time that stops while
        // the game is paused (talkClock), as by game ticks it flickers at the faster speeds. Each pawn has its own
        // rhythm, from its id.
        private const float SpeakFrom = 0.3f;   // the talking clip's weight from which the mouth moves
        private const float SyllableSeconds = 0.16f;
        private const float SyllableOpen = 0.6f;   // the share of a syllable the mouth is open for
        private const int SyllableRest = 5;   // one syllable in this many (on average) is left shut
        private const float PhraseSeconds = 2.4f;
        private const float PhraseRest = 0.4f;
        private static float talkClock;
#if AUTOTEST
        internal static int TalkOpenDraws, TalkShutDraws, TalkNoOpen;   // (NoOpen: open draws where the open shape is missing or the same picture)
        internal static readonly HashSet<string> TalkTextures = new HashSet<string>();   // frames drawn talking with the mouth open / shut (the autotest's talk)
#endif
        private static int talkClockFrame = -1;

        private static bool MouthOpen(int id)
        {
            if (talkClockFrame != Time.frameCount)
            {
                talkClockFrame = Time.frameCount;
#if AUTOTEST
                talkClock += Mathf.Min(Time.deltaTime, 0.1f);   // (the test steps the game paused)
#else
                if (!Find.TickManager.Paused)
                {
                    talkClock += Mathf.Min(Time.deltaTime, 0.1f);
                }
#endif
            }
            uint seed = (uint)id * 2654435761u;
            float t = talkClock + (seed % 1000u) * 0.001f * PhraseSeconds;
            float phrase = t / PhraseSeconds;
            if ((phrase - Mathf.Floor(phrase)) * PhraseSeconds > PhraseSeconds - PhraseRest)
            {
                return false;
            }
            float syllables = t / SyllableSeconds;
            int syllable = Mathf.FloorToInt(syllables);
            if (Gen.HashCombineInt(id, syllable) % SyllableRest == 0)
            {
                return false;
            }
            return syllables - syllable < SyllableOpen;
        }

        // This lid layer as Facial Animation draws it closed (its "close" LidShapeDef, as when the pawn blinks), or null.
        private static Graphic ClosedLid(PawnRenderNode node, Pawn pawn)
        {
            return ShapeGraphic(node, pawn, "close");
        }

        private static readonly Dictionary<Type, FieldInfo> graphicLists = new Dictionary<Type, FieldInfo>();

        // This layer as Facial Animation draws it in the shape named (a LidShapeDef, MouthShapeDef, ... of the controller's
        // own kind), or null. Read from the controller's graphicList, by layer, with NLGraphic_Collection.GetSubGraphic.
        private static Graphic ShapeGraphic(PawnRenderNode node, Pawn pawn, string shape)
        {
            try
            {
                object controller = controllerField.GetValue(node);
                if (controller == null || rotDrawModeField == null)
                {
                    return null;
                }
                FieldInfo list;
                if (!graphicLists.TryGetValue(controller.GetType(), out list))
                {
                    list = null;
                    for (Type t = controller.GetType(); t != null && list == null; t = t.BaseType)
                    {
                        list = t.GetField("graphicList", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    }
                    graphicLists[controller.GetType()] = list;
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
                Def def = GenDefDatabase.GetDefSilentFail(ps[1].ParameterType, shape, false);
                if (def == null)
                {
                    return null;
                }
                return sub.Invoke(collection, new object[] { pawn, def, rotDrawModeField.GetValue(node) }) as Graphic;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "reading Facial Animation's " + shape + " face shape failed: " + e, 61938203 + shape.GetHashCode() % 1000);
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
