using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using IsoRim;

namespace M3AT
{
    // GPU skinning: the body, garments, fur, hair, beard and horns are posed by the graphics card rather than here. Posing
    // them here meant setting every mesh's vertices and normals at each new pose, and in this engine that rebuilds the
    // whole mesh and makes new vertex and index buffers for it on the GPU, which cost far more than the posing itself.
    // Instead each mesh holds its rest pose in the pawn's shape and, per vertex, the bones that move it (packed by PackedOf
    // into texture coordinates 2 and 3 and the tangent), set once per shape (GpuPose); each pawn's bones go with every draw
    // in a block of its own (BlockOf, _MarusBones), and MarusSmooth.vert.glsl and MarusSmoothTex.vert.glsl skin by them.
    // The face patch, wounds, cat ears and tail and a backpack are still posed here. What reads the posed vertices (Extent,
    // SeatOf, the wounds) has them worked out here when asked for (PosedOnCpu), without setting any mesh.
    public sealed partial class SkinnedFigure
    {
        private const int MostBones = 12;   // bones a vertex is moved by on the graphics card: three texture channels of four
        private const string SmoothMarkDef = "MarusSmoothMark";
        private static readonly int bonesId = Shader.PropertyToID("_MarusBones");
        private static readonly int ownerFloorId = Shader.PropertyToID("_IsoOwnerFloor");
        private static readonly int flashId = Shader.PropertyToID("_IsoFlash");

        // Cells, model space: what IsoRim culls a mesh skinned on the graphics card by, as its vertices stay at rest. Roomy
        // enough for the figure laid down or flung about by its bones (a ragdoll, a fall), which move it off its rest pose.
        private static readonly Bounds GpuBounds = new Bounds(new Vector3(0f, 0.6f, 0f), new Vector3(3.5f, 2.6f, 3.5f));

        private static Shader smoothMark;
        private static bool gpuTried, gpuWorks;
        private static AccessTools.FieldRef<bool> scopeActive;
        private delegate bool ScopeSubmit(List<DynamicModels.Part> parts, ref Matrix4x4 matrix);
        private static ScopeSubmit scopeSubmit;
        private static Func<int, int> fadeOfFloor;

        // True while meshes are posed on the graphics card: the option on, and this mod's own materials, which do it, in use
        // (IsoRim's, used when ours are missing, don't).
        public static bool GpuSkinning
        {
            get
            {
                return (SettingsMod.Settings == null || SettingsMod.Settings.gpuSkinning) && smooth != null && outline != null && GpuWorks();
            }
        }

        // Looks once for what GPU skinning needs that isn't ours: IsoRim's figure scope and floor fade (internal), which its
        // own draw goes through (DrawSkinned), and the skinned light pass.
        private static bool GpuWorks()
        {
            if (gpuTried)
            {
                return gpuWorks;
            }
            gpuTried = true;
            try
            {
                ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(SmoothMarkDef);
                smoothMark = def != null ? def.Shader : null;
                Type scope = AccessTools.TypeByName("IsoRim.FigureScope");
                FieldInfo active = scope != null ? AccessTools.Field(scope, "Active") : null;
                MethodInfo submit = scope != null ? AccessTools.Method(scope, "Submit") : null;
                MethodInfo fade = AccessTools.Method(typeof(Floors), "FadeOfFloor");
                if (smoothMark == null || active == null || submit == null || fade == null)
                {
                    Log.Warning(Tag + "GPU skinning can't be set up (" + (smoothMark == null ? "the " + SmoothMarkDef + " shader is missing" : "this IsoRim draws differently")
                        + "), so pawns are posed on the CPU.");
                    return false;
                }
                scopeActive = AccessTools.StaticFieldRefAccess<bool>(active);
                scopeSubmit = (ScopeSubmit)Delegate.CreateDelegate(typeof(ScopeSubmit), submit);
                fadeOfFloor = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), fade);
                gpuWorks = true;
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "GPU skinning can't be set up, so pawns are posed on the CPU: " + e.Message);
            }
            return gpuWorks;
        }

        // Gives the light pass IsoRim made for one of the MarusSmooth materials (IsoGlow.Use) MarusSmooth's own program,
        // which poses a mesh skinned on the graphics card: IsoRim's would light it at rest. Its render state is IsoRim's.
        private static void SkinMark(Material solid)
        {
            GpuWorks();
            Material mark = IsoGlow.MarkFor(solid);
            if (mark != null && smoothMark != null)
            {
                mark.shader = smoothMark;
            }
        }

        // Each vertex's bones for the graphics card, made once per piece: bone + weight / 2, so the weight is the fraction
        // and a whole weight is still under the next bone, up to MostBones a vertex in three Vector4s (texture coordinates 2
        // and 3 and the tangent), a zero where there is none. A vertex with more keeps its biggest, scaled up to the whole.
        private static Vector4[][] PackedOf(Piece piece)
        {
            if (piece.packed != null)
            {
                return piece.packed;
            }
            int n = piece.rest.Length;
            Vector4[][] packed = { new Vector4[n], new Vector4[n], new Vector4[n] };
            float[] slots = new float[MostBones];
            List<KeyValuePair<int, float>> bones = new List<KeyValuePair<int, float>>();
            for (int i = 0; i < n; i++)
            {
                bones.Clear();
                float whole = 0f;
                for (int k = piece.firstWeight[i]; k < piece.firstWeight[i + 1]; k++)
                {
                    if (piece.weight[k] > 0f)
                    {
                        bones.Add(new KeyValuePair<int, float>(piece.weightBone[k], piece.weight[k]));
                        whole += piece.weight[k];
                    }
                }
                float scale = 1f;
                if (bones.Count > MostBones)
                {
                    bones.Sort((a, b) => b.Value.CompareTo(a.Value));
                    bones.RemoveRange(MostBones, bones.Count - MostBones);
                    float kept = 0f;
                    foreach (KeyValuePair<int, float> b in bones)
                    {
                        kept += b.Value;
                    }
                    scale = kept > 0f ? whole / kept : 1f;
                }
                Array.Clear(slots, 0, slots.Length);
                for (int k = 0; k < bones.Count; k++)
                {
                    slots[k] = bones[k].Key + Mathf.Min(bones[k].Value * scale, 1f) * 0.5f;
                }
                for (int c = 0; c < 3; c++)
                {
                    packed[c][i] = new Vector4(slots[4 * c], slots[4 * c + 1], slots[4 * c + 2], slots[4 * c + 3]);
                }
            }
            piece.packed = packed;
            return packed;
        }

        private static void SetPacked(Mesh mesh, Vector4[][] packed)
        {
            mesh.SetUVs(2, packed[0]);
            mesh.SetUVs(3, packed[1]);
            mesh.tangents = packed[2];
        }

        // Puts the piece's rest pose in this shape and its bones into its meshes, the first time and when the shape changes:
        // the graphics card poses them from the pawn's bones at each draw (BlockOf), so a new pose sets nothing.
        private static void GpuPose(Piece piece, PieceShape shape, PieceMeshes m)
        {
            if (m.gpuShape == shape)
            {
                return;
            }
            if (m.gpuShape == null)
            {
                SetPacked(m.solid, PackedOf(piece));
                SetPacked(m.outline, PackedOf(piece.shell ?? piece));
            }
            m.gpuShape = shape;
            m.posedAt = -1;   // posed again from the start if it goes back to the CPU (LeaveGpu)
            m.cpuAt = -1;
            m.solid.vertices = shape.rest;
            m.solid.normals = shape.normal;
            // The outline as Pose has it: a vertex pushed out along its hull, which the bones turn with it, is the same as
            // the pushed-out place skinned.
            Vector3[] rest = piece.shell == null ? shape.rest : shape.shell.rest;
            Vector3[] hull = piece.shell == null ? shape.hull : shape.shell.hull;
            for (int i = 0; i < m.hulled.Length; i++)
            {
                m.hulled[i] = rest[i] + hull[i];
            }
            m.outline.vertices = m.hulled;
            m.solid.bounds = GpuBounds;
            m.outline.bounds = GpuBounds;
        }

        // Takes the bones back out of a mesh that was skinned on the graphics card (the option turned off), so the shader
        // draws it as Pose places it again.
        private static void LeaveGpu(PieceMeshes m)
        {
            Vector4[] none = new Vector4[m.verts.Length];
            Vector4[] noneOutline = new Vector4[m.hulled.Length];
            SetPacked(m.solid, new Vector4[][] { none, none, none });
            SetPacked(m.outline, new Vector4[][] { noneOutline, noneOutline, noneOutline });
            m.gpuShape = null;
            m.posedAt = -1;
            m.cpuAt = -1;
        }

        // A piece's meshes as Pose(p, index) leaves them, with its posed vertices and normals in verts and normals even
        // when the graphics card poses the meshes themselves: for what reads them here (Extent, SeatOf, the wounds).
        private PieceMeshes PosedOnCpu(Posed p, int index)
        {
            PieceMeshes m = Pose(p, index);
            if (m.gpuShape != null && m.cpuAt != p.posedAt)
            {
                m.cpuAt = p.posedAt;
                Skin(p, pieces[index], m.gpuShape, m, false);
            }
            return m;
        }

        // The pawn's bones as the shader reads them (_MarusBones: the top three rows of each), in a block of its own, set
        // again only when they have changed, and its hit flash (_IsoFlash). Every draw of its meshes takes it.
        private static MaterialPropertyBlock BlockOf(Posed p)
        {
            if (p.bonesBlock == null)
            {
                p.bonesBlock = new MaterialPropertyBlock();
                p.boneVectors = new Vector4[p.boneRows.Length / 4];
                p.bonesAt = -1;
            }
            if (p.bonesAt != p.posedAt)
            {
                float[] f = p.boneRows;
                for (int r = 0; r < p.boneVectors.Length; r++)
                {
                    p.boneVectors[r] = new Vector4(f[4 * r], f[4 * r + 1], f[4 * r + 2], f[4 * r + 3]);
                }
                p.bonesBlock.SetVectorArray(bonesId, p.boneVectors);
                p.bonesAt = p.posedAt;
            }
            // The hit flash (HitFlash), set every draw as it fades from tick to tick. The outlines are drawn with this block
            // too, and MarusSmooth.frag.glsl leaves them (a pushed shell, _OutlinePush) their colour.
            p.bonesBlock.SetFloat(flashId, p.flash);
            return p.bonesBlock;
        }

        private static Mesh cullBox;

        // Whether a figure placed so (as DrawParts places it: true space, cells, yaw degrees, local the model's placing) can
        // be on screen, by IsoRim's own cull (Cull.MeshVisible: the shown camera only, while the map's things are drawn,
        // on the floor it shows on) but tested on GpuBounds, a box roomy enough for any pose, rather than on each mesh's
        // own: Cull goes by the vertices' box as the engine last worked it out, which for ours is the rest pose (skinned on
        // the graphics card) or stale (posed here, the vertices set without it), so close up the hair and garments were
        // left off while on screen. A figure measured for another mod (FigureScope) is never left out.
        private static bool FigureOnScreen(Vector3 position, float yaw, Matrix4x4 local)
        {
            if (scopeActive != null && scopeActive())
            {
                return true;
            }
            if (cullBox == null)
            {
                // Eight corners and one triangle: the engine works the box out from the vertices when the triangles are set.
                Vector3 c = GpuBounds.center, e = GpuBounds.extents;
                Vector3[] corners = new Vector3[8];
                for (int i = 0; i < 8; i++)
                {
                    corners[i] = c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
                }
                cullBox = new Mesh { name = "M3AT cull box" };
                cullBox.vertices = corners;
                cullBox.triangles = new int[] { 0, 1, 7 };
            }
            float s = View3D.Squash;
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * local;
            return Cull.MeshVisible(cullBox, Floors.DrawnMatrix(matrix));
        }

        // Draws the parts of a mesh skinned on the graphics card as DynamicModels.DrawParts would (IsoRim's Submit: another
        // mod's figure scope, the floor it is drawn on and that floor's fade, IsoRim's outline option, each part
        // with its light pass by IsoGlow.DrawLit), but each with the pawn's bones in block: IsoRim's draw takes only its own
        // floor block, and its batching would lose ours. Position true space (cells), yaw degrees, local the model's placing.
        private static void DrawSkinned(List<DynamicModels.Part> parts, Vector3 position, float yaw, Matrix4x4 local, MaterialPropertyBlock block)
        {
            if (parts.Count == 0)
            {
                return;
            }
            float s = View3D.Squash;
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * local;
            if (scopeActive() && scopeSubmit(parts, ref matrix))
            {
                return;   // a figure measured for another mod (Api.FigureBounds): nothing drawn
            }
            int floor;
            matrix = Floors.DrawnMatrix(matrix, out floor);
            int fadeWas = Floors.FadeSlot;
            if (fadeWas == 0 && floor > 0)
            {
                Floors.FadeSlot = fadeOfFloor(floor);
            }
            try
            {
                // As RoofClip.FloorBlock gives it: 1 + the floor it stands on, 0 without floors.
                block.SetFloat(ownerFloorId, floor >= 0 && Floors.On ? floor + 1f : 0f);
                bool outlines = IsoRimMod.Settings.outlines;
                for (int i = 0; i < parts.Count; i++)
                {
                    DynamicModels.Part part = parts[i];
                    // Not culled part by part (DrawMeshes culls the whole figure first, FigureOnScreen).
                    if (part.outline && !outlines)
                    {
                        continue;
                    }
                    IsoGlow.DrawLit(part.mesh, matrix, part.material, 0, block);
                }
            }
            finally
            {
                Floors.FadeSlot = fadeWas;
            }
        }

        private static readonly List<DynamicModels.Part> submeshParts = new List<DynamicModels.Part>();
        private static MaterialPropertyBlock submeshBlock;

        // Draws one sub-mesh of a mesh with its own material (a visor's glass, DrawVisor), placed as DrawSkinned places
        // parts, posed here or on the graphics card. No light pass. block: the pawn's bones for a mesh skinned on the
        // graphics card (BlockOf), null for one posed here. IsoRim's internals DrawSkinned binds may be missing (GpuWorks).
        private static void DrawSubmesh(Mesh mesh, int submesh, Material material, Vector3 position, float yaw, Matrix4x4 local,
            MaterialPropertyBlock block)
        {
            float s = View3D.Squash;
            Matrix4x4 matrix = Matrix4x4.Translate(new Vector3(position.x, position.y / s, position.z))
                * Matrix4x4.Scale(new Vector3(1f, 1f / s, 1f))
                * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up))
                * local;
            if (scopeActive != null && scopeActive())
            {
                submeshParts.Clear();
                submeshParts.Add(new DynamicModels.Part { mesh = mesh, material = material, outline = false });
                if (scopeSubmit(submeshParts, ref matrix))
                {
                    return;   // a figure measured for another mod (Api.FigureBounds): nothing drawn
                }
            }
            if (!FigureOnScreen(position, yaw, local))
            {
                return;
            }
            int floor;
            matrix = Floors.DrawnMatrix(matrix, out floor);
            if (block == null)
            {
                submeshBlock = submeshBlock ?? new MaterialPropertyBlock();
                block = submeshBlock;
            }
            int fadeWas = Floors.FadeSlot;
            if (fadeWas == 0 && floor > 0 && fadeOfFloor != null)
            {
                Floors.FadeSlot = fadeOfFloor(floor);
            }
            try
            {
                block.SetFloat(ownerFloorId, floor >= 0 && Floors.On ? floor + 1f : 0f);
                Graphics.DrawMesh(mesh, matrix, material, 0, null, submesh, block);
            }
            finally
            {
                Floors.FadeSlot = fadeWas;
            }
        }
    }
}
