using System;
using System.Collections.Generic;
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

        // Cells, model space: what IsoRim culls a mesh skinned on the graphics card by, as its vertices stay at rest. Roomy
        // enough for the figure laid down or flung about by its bones (a ragdoll, a fall), which move it off its rest pose.
        private static readonly Bounds GpuBounds = new Bounds(new Vector3(0f, 0.6f, 0f), new Vector3(3.5f, 2.6f, 3.5f));

        private static Shader smoothMark;
        private static bool gpuTried, gpuWorks;

        // True while meshes are posed on the graphics card: the option on, and this mod's own materials, which do it, in use.
        public static bool GpuSkinning
        {
            get
            {
                return (SettingsMod.Settings == null || SettingsMod.Settings.gpuSkinning) && smooth != null && outline != null && GpuWorks();
            }
        }

        // Looks once for the skinned light pass (MarusSmoothMark), which IsoRim's light pass is given as its program
        // (ConfigureFigureMaterial's lightPassShader): without it the light pass would light the mesh at rest.
        private static bool GpuWorks()
        {
            if (gpuTried)
            {
                return gpuWorks;
            }
            gpuTried = true;
            ShaderDef def = DefDatabase<ShaderDef>.GetNamedSilentFail(SmoothMarkDef);
            smoothMark = def != null ? def.Shader : null;
            if (smoothMark == null)
            {
                Log.Warning(Tag + "GPU skinning can't be set up (the " + SmoothMarkDef + " shader is missing), so pawns are posed on the CPU.");
                return false;
            }
            gpuWorks = true;
            return true;
        }

        // The light pass's program for the MarusSmooth materials: MarusSmooth's own, which poses a mesh skinned on the graphics
        // card (and draws one posed here as it is); null without it, IsoRim's then.
        private static Shader SkinMarkShader()
        {
            GpuWorks();
            return smoothMark;
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

        // The face patch posed on the graphics card, as the body is (MarusFace.vert.glsl skins it), as setting its meshes'
        // vertices at each new pose rebuilt the face, the mouth and the pig snout's mouth with new GPU buffers every frame
        // for every pawn that moved. Each face vertex is moved by the bones of the body vertex it was made from (faceFrom),
        // so it is posed exactly as the CPU poses it; its rest place and normal in the pawn's shape are set only when the
        // shape changes, and the pawn's bones go to FaceLayers in the patch (FaceLayers.BlockFor).
        private void FaceOnGpu(Posed p)
        {
            FaceLayers.Patch f = p.face;
            if (f.gpuShape != p.shape)
            {
                if (f.gpuShape == null)
                {
                    Vector4[][] packed = FacePacked();
                    SetPacked(f.face, packed);
                    SetPacked(f.mouth, packed);
                    SetPacked(f.mouthLow, packed);
                }
                f.gpuShape = p.shape;
                Vector3[] bodyNormal = ShapedPiece(p.shape, 0).normal;
                for (int i = 0; i < p.faceNormals.Length; i++)
                {
                    p.faceNormals[i] = bodyNormal[faceFrom[i]];
                }
                Bounds bounds = FaceLayers.PatchBounds;   // as NewPatch set them, which setting the vertices would undo
                foreach (Mesh mesh in new[] { f.face, f.mouth, f.mouthLow })
                {
                    mesh.vertices = p.shape.faceRest;
                    mesh.normals = p.faceNormals;
                    mesh.bounds = bounds;
                }
            }
            BlockOf(p);   // the pawn's bones as three rows each, set again only when they have changed
            f.bones = p.boneVectors;
            f.bonesAt = p.posedAt;
            f.centre = p.bones[headBone].MultiplyPoint3x4(p.shape.faceCentre);
        }

        // Back to the CPU (the option turned off): the bones taken out of the patch's meshes, which Finish then poses.
        private void FaceLeaveGpu(FaceLayers.Patch f)
        {
            if (f.bones == null && f.gpuShape == null)
            {
                return;
            }
            Vector4[] none = new Vector4[faceFrom.Length];
            Vector4[][] empty = { none, none, none };
            SetPacked(f.face, empty);
            SetPacked(f.mouth, empty);
            SetPacked(f.mouthLow, empty);
            f.bones = null;
            f.gpuShape = null;
        }

        private Vector4[][] facePacked;

        // Each face vertex's bones for the graphics card: those of the body vertex it was made from.
        private Vector4[][] FacePacked()
        {
            if (facePacked != null)
            {
                return facePacked;
            }
            Vector4[][] body = PackedOf(pieces[0]);
            facePacked = new Vector4[3][];
            for (int c = 0; c < 3; c++)
            {
                facePacked[c] = new Vector4[faceFrom.Length];
                for (int i = 0; i < faceFrom.Length; i++)
                {
                    facePacked[c][i] = body[c][faceFrom[i]];
                }
            }
            return facePacked;
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
        private static MaterialPropertyBlock BlockOf(Posed p, bool dressed = false)
        {
            if (dressed && p.pylons)
            {
                // The clothes' bones (Prosthetics.cs): a pylon left its full width.
                if (p.dressBlock == null)
                {
                    p.dressBlock = new MaterialPropertyBlock();
                    p.dressVectors = new Vector4[p.dressRows.Length / 4];
                    p.dressAt = -1;
                }
                if (p.dressAt != p.posedAt)
                {
                    float[] d = p.dressRows;
                    for (int r = 0; r < p.dressVectors.Length; r++)
                    {
                        p.dressVectors[r] = new Vector4(d[4 * r], d[4 * r + 1], d[4 * r + 2], d[4 * r + 3]);
                    }
                    p.dressBlock.SetVectorArray(bonesId, p.dressVectors);
                    p.dressAt = p.posedAt;
                }
                return p.dressBlock;
            }
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
            // (The hit flash, _IsoFlash, and the floor, _IsoOwnerFloor, are written into it by Api.SubmitFigureMesh at each draw.)
            return p.bonesBlock;
        }

        // The figure placed so (true space, cells: position, yaw degrees about the vertical, local the model's placing).
        private static Matrix4x4 TrueSpace(Vector3 position, float yaw, Matrix4x4 local)
        {
            return Matrix4x4.Translate(position) * Matrix4x4.Rotate(Quaternion.AngleAxis(yaw, Vector3.up)) * local;
        }

        // Whether a figure placed so can be on screen, by IsoRim's cull (Api.FigureVisible: the shown camera, the floor it
        // shows on) tested on GpuBounds, a box roomy enough for any pose, rather than on each mesh's own: IsoRim would go by
        // the vertices' box as the engine last worked it out, which for ours is the rest pose (skinned on the graphics card)
        // or stale (posed here, the vertices set without it), so close up the hair and garments were left off while on
        // screen. A figure drawn or measured for another mod is never left out.
        private static bool FigureOnScreen(Vector3 position, float yaw, Matrix4x4 local)
        {
            return Api.FigureVisible(GpuBounds, TrueSpace(position, yaw, local));
        }

        // Whether a figure IsoRim places at position (cells) at this size may be on screen at all: FigureOnScreen's test made
        // before anything is posed, any way round, and MaybeMargin cells roomier, as the figure is drawn up to about that far
        // from its placing (stepped up to a bench or an instrument, a kiss, a listener, a ragdoll's drift). The game offers
        // every pawn in its view rectangle, which with IsoRim's tilted camera reaches well past the screen, so one that
        // can't be seen is left out before it is posed (WalkFigures.DrawSkinned).
        public static bool MaybeOnScreen(Vector3 position, float size)
        {
            float s = Mathf.Max(size, 0.05f);
            Bounds roomy = new Bounds(GpuBounds.center, GpuBounds.size + Vector3.one * (2f * MaybeMargin / s));
            return Api.FigureVisible(roomy, Matrix4x4.Translate(position) * Matrix4x4.Scale(new Vector3(s, s, s)));
        }

        private const float MaybeMargin = 1f;   // cells

        // Draws a piece's parts as a moving model's (Api.SubmitFigureMesh: another mod's figure scope, the floor it is drawn on
        // and its fade, IsoRim's outline option, the floor and hit flash in the block, each lit part with its light pass), not
        // culled part by part (DrawMeshes culls the whole figure first, FigureOnScreen). block: the pawn's bones for a mesh
        // skinned on the graphics card (BlockOf), null for one posed here. flash: the hit flash, which the outlines never take.
        private static void DrawParts(List<MeshPart> parts, Vector3 position, float yaw, Matrix4x4 local, MaterialPropertyBlock block, float flash)
        {
            Matrix4x4 placed = TrueSpace(position, yaw, local);
            for (int i = 0; i < parts.Count; i++)
            {
                MeshPart part = parts[i];
                if (part.mesh == null || part.material == null)
                {
                    continue;
                }
                FigureMeshFlags flags = FigureMeshFlags.NoCull;
                if (part.outline)
                {
                    flags |= FigureMeshFlags.Outline;
                }
                if (part.lit)
                {
                    flags |= FigureMeshFlags.LightPass;
                }
                Api.SubmitFigureMesh(part.mesh, part.material, placed, block, flags, 0, flash);
            }
        }

        // Draws one sub-mesh of a mesh with its own material (a visor's glass, DrawVisor), placed as DrawParts places parts,
        // posed here or on the graphics card. No light pass. block: the pawn's bones for a mesh skinned on the graphics card
        // (BlockOf), null for one posed here.
        private static void DrawSubmesh(Mesh mesh, int submesh, Material material, Vector3 position, float yaw, Matrix4x4 local,
            MaterialPropertyBlock block)
        {
            if (!FigureOnScreen(position, yaw, local))
            {
                return;
            }
            Api.SubmitFigureMesh(mesh, material, TrueSpace(position, yaw, local), block, FigureMeshFlags.NoCull, submesh);
        }
    }
}
