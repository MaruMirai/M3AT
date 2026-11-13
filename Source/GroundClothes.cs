using System;
using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Apparel lying on the map drawn as the chibi's own garment for it, laid down, in place of IsoRim's (GroundGear: the 3D
    // clothes its figure wears, laid down), so what lies in a stockpile is what a colonist puts on. The garment is the one a
    // pawn wearing only that apparel gets (its own, else the one it stands in for), on the standing pose (the idle's first
    // frame) of a grown male, or of a child for apparel only children wear, and laid as GroundGear lays clothes: what goes
    // on the body on its back, front up, collar to the north, squashed to Flatten of its depth; what goes only on the head
    // standing on its rim, front to the south. It is baked into the map mesh by IsoRim as a ground model (Api.AddGroundModelRule,
    // with roof clipping, outline and shadow), a model made here (Api.CreateModel) with the garment's image if it has one, so
    // nothing is drawn per frame. Apparel with no garment keeps IsoRim's look.
    public sealed partial class SkinnedFigure
    {
        private Posed groundPose;   // the standing pose garments are laid from, reused

        // The garment (piece) on the standing pose in this shape as an IsoRim model (cells, the figure facing -z, +y up),
        // laid down and set on the ground at its middle, with each corner white and tinted (a textured garment's its mean
        // colour, its image on a material of its own). headOnly: every vertex rides mostly the head or the neck.
        internal ModelHandle Laid(int piece, Shape shape, out bool headOnly, out float foot)
        {
            Piece g = pieces[piece];
            if (groundPose == null)
            {
                groundPose = NewPosed();
            }
            Posed p = groundPose;
            p.shape = shape;
            Begin(p);
            Add(p, "idle", 0f, 1f);
            Chain(p, Matrix4x4.identity);

            PieceShape shaped = ShapedPiece(shape, piece);
            int n = shaped.rest.Length;
            int neck = boneParent[headBone], lowerNeck = neck >= 0 ? boneParent[neck] : -1;   // a helmet's neck guard rides these
            Vector3[] posed = new Vector3[n], hulled = new Vector3[n];
            headOnly = true;
            for (int i = 0; i < n; i++)
            {
                Vector3 v = Vector3.zero, h = Vector3.zero;
                float onHead = 0f;
                for (int k = g.firstWeight[i]; k < g.firstWeight[i + 1]; k++)
                {
                    Matrix4x4 b = p.bones[g.weightBone[k]];
                    v += b.MultiplyPoint3x4(shaped.rest[i]) * g.weight[k];
                    h += b.MultiplyVector(shaped.hull[i]) * g.weight[k];
                    int bone = g.weightBone[k];
                    if (bone == headBone || bone == neck || bone == lowerNeck)
                    {
                        onHead += g.weight[k];
                    }
                }
                posed[i] = v;
                hulled[i] = h;
                headOnly &= onHead >= 0.5f;
            }

            // Laid as GroundGear.Build lays its clothes: turned to face up (or, on the head, to the south), then moved onto
            // the ground at its middle and a laid garment squashed.
            Matrix4x4 pose = headOnly
                ? Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up))
                : Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.up)) * Matrix4x4.Rotate(Quaternion.AngleAxis(-90f, Vector3.right));
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < n; i++)
            {
                Vector3 q = pose.MultiplyPoint3x4(posed[i]);
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
            }
            Matrix4x4 t = Matrix4x4.Scale(new Vector3(1f, headOnly ? 1f : GroundFlatten, 1f))
                * Matrix4x4.Translate(new Vector3(-(min.x + max.x) * 0.5f, -min.y, -(min.z + max.z) * 0.5f)) * pose;
            foot = Mathf.Max(max.x - min.x, max.z - min.z);

            // IsoRim's models are triangle soups: three corners a face, in Unity's winding, as the piece's triangles are.
            bool textured = g.texture != null && g.uvs != null;
            int[] tris = g.tris;
            ModelData m = new ModelData
            {
                Positions = new Vector3[tris.Length],
                Normals = new Vector3[tris.Length],
                Hull = new Vector3[tris.Length],
                Colours = new Color[tris.Length],
                Tinted = new bool[tris.Length],
                UVs = textured ? new Vector2[tris.Length] : null,
                Material = textured ? new int[tris.Length] : null,
                Materials = textured ? new[] { new ModelDataMaterial { Name = "stuff_" + g.name, Texture = g.texture, Tinted = true } } : null
            };
            Color corner = textured ? g.mean : Color.white;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = t.MultiplyPoint3x4(posed[tris[i]]), b = t.MultiplyPoint3x4(posed[tris[i + 1]]), c = t.MultiplyPoint3x4(posed[tris[i + 2]]);
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[i + k];
                    m.Positions[i + k] = k == 0 ? a : k == 1 ? b : c;
                    m.Normals[i + k] = normal;
                    m.Hull[i + k] = t.MultiplyVector(hulled[v]);
                    m.Colours[i + k] = corner;
                    m.Tinted[i + k] = true;
                    if (textured)
                    {
                        m.UVs[i + k] = g.uvs[v];
                    }
                }
            }
            return Api.CreateModel(m);
        }

        // The garment (piece) at rest in this shape as an IsoRim model (model space, as it is worn with every bone at rest,
        // so placing it by a bone's move draws it where that bone takes it), coloured as Laid colours it; front: its
        // front-most point (+z), where a hat is held by the brim (WalkFigures.DrawHeldApparel); min and max its box.
        internal ModelHandle AtRest(int piece, Shape shape, out Vector3 front, out Vector3 min, out Vector3 max)
        {
            Piece g = pieces[piece];
            PieceShape shaped = ShapedPiece(shape, piece);
            front = Vector3.zero;
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < shaped.rest.Length; i++)
            {
                if (i == 0 || shaped.rest[i].z > front.z)
                {
                    front = shaped.rest[i];
                }
                min = Vector3.Min(min, shaped.rest[i]);
                max = Vector3.Max(max, shaped.rest[i]);
            }
            bool textured = g.texture != null && g.uvs != null;
            int[] tris = g.tris;
            ModelData m = new ModelData
            {
                Positions = new Vector3[tris.Length],
                Normals = new Vector3[tris.Length],
                Hull = new Vector3[tris.Length],
                Colours = new Color[tris.Length],
                Tinted = new bool[tris.Length],
                UVs = textured ? new Vector2[tris.Length] : null,
                Material = textured ? new int[tris.Length] : null,
                Materials = textured ? new[] { new ModelDataMaterial { Name = "stuff_" + g.name, Texture = g.texture, Tinted = true } } : null
            };
            Color corner = textured ? g.mean : Color.white;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = shaped.rest[tris[i]], b = shaped.rest[tris[i + 1]], c = shaped.rest[tris[i + 2]];
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[i + k];
                    m.Positions[i + k] = shaped.rest[v];
                    m.Normals[i + k] = normal;
                    m.Hull[i + k] = shaped.hull[v];
                    m.Colours[i + k] = corner;
                    m.Tinted[i + k] = true;
                    if (textured)
                    {
                        m.UVs[i + k] = g.uvs[v];
                    }
                }
            }
            return Api.CreateModel(m);
        }

        private const float GroundFlatten = 0.5f;   // a laid garment's depth (front to back), as a share of the worn depth (GroundGear's Flatten)
    }

    public static partial class WalkFigures
    {
        private const float GroundFoot = 0.92f;   // cells: the longest side of a garment lying in a cell (GroundGear's MaxFoot)
        private const float ChildClothesAge = 8f;   // years: the child apparel only children wear is laid out at the size of

        // A garment laid on the ground, made once per garment, build and head shrink.
        private sealed class LaidGarment
        {
            public ModelHandle model;
            public float fit;   // its scale on the ground: the figure's size, shrunk to fit its cell
        }

        private static readonly Dictionary<string, LaidGarment> laid = new Dictionary<string, LaidGarment>();

        // Asked by IsoRim (Api.AddGroundModelRule) as it prints an item lying on the ground into the map: apparel IsoRim lays
        // as worn clothes (WornLook) with a garment of the chibi's is baked as that garment, laid down; anything else is left to
        // IsoRim (null; IsoRim never asks for damage marks or a blueprint's ghost).
        private static GroundModel? GroundRule(GroundModelContext c)
        {
            Apparel a = c.Thing as Apparel;
            if (!GroundGarments || a == null || !c.WornLook)
            {
                return null;
            }
            try
            {
                if (!Ready() || skin == null)
                {
                    return null;
                }
                int g = GroundGarmentOf(a.def);
                LaidGarment lg = g > 0 ? LaidOf(g, ChildOnly(a.def)) : null;
                if (lg == null || lg.model == null)
                {
                    return null;
                }
                Color colour = !skin.Tinted(g, a) ? Color.white : Api.ApparelColour(a);
                return new GroundModel
                {
                    Model = lg.model,
                    Scale = lg.fit * (a.MultipleItemsPerCellDrawn() ? 0.8f : 1f),
                    Yaw = GroundTurn(a),
                    Tint = colour
                };
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "laying " + a.def.defName + " on the ground failed, IsoRim's look is kept: " + e, 0x4d334154 ^ a.def.shortHash);
                return null;
            }
        }

        // The GroundGarments option changed: the apparel already baked into each map's mesh is written again.
        internal static void RebakeGroundClothes()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
            {
                return;
            }
            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
                {
                    map.mapDrawer.MapMeshDirty(thing.Position, (ulong)MapMeshFlagDefOf.Things);
                }
            }
        }

        // The RetexturedWeaponModels option changed: IsoRim bakes weapons on the ground into the map's mesh as their model
        // or their art (Baking, WeaponModels.Uses), so each one there is written again.
        internal static void RebakeGroundWeapons()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
            {
                return;
            }
            foreach (Map map in Find.Maps)
            {
                foreach (Thing thing in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
                {
                    if (thing.Spawned)
                    {
                        map.mapDrawer.MapMeshDirty(thing.Position, (ulong)MapMeshFlagDefOf.Things);
                    }
                }
            }
        }

        // The garment a pawn wearing only this apparel would have on (WornGarments): its own, else the best that stands in
        // for it; -1 for none. A held one (a shield) is left to IsoRim: it was made upright in the hand, not to lie as clothes do.
        private static int GroundGarmentOf(ThingDef def)
        {
            int g = skin.GarmentOf(def);
            if (g > 0)
            {
                return skin.IsHeld(g) ? -1 : g;
            }
            return skin.StandInFor(def);
        }

        // Apparel only children wear (as IsoRim's ground clothes tell it): laid out on a child's body, at a child's size.
        private static bool ChildOnly(ThingDef def)
        {
            return def.apparel != null && (def.apparel.developmentalStageFilter & DevelopmentalStage.Adult) == 0
                && (def.apparel.developmentalStageFilter & DevelopmentalStage.Child) != 0;
        }

        private static LaidGarment LaidOf(int g, bool child)
        {
            float limbs = child ? Mathf.Round((1f - Mathf.Clamp01(ChildClothesAge / BabyLimbsYears)) * BabyLimbsSteps) / BabyLimbsSteps : 0f;
            float shrink = HeadShrinkOf(limbs);
            string key = g + "|" + child + "|" + shrink.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LaidGarment lg;
            if (laid.TryGetValue(key, out lg))
            {
                return lg;
            }
            SkinnedFigure.Shape shape = child ? skin.ShapeOf(null, null, 0f, shrink, 1f, 0f, limbs) : skin.ShapeOf("Male", null, 0f, shrink);
            float size = (child ? Mathf.Lerp(BirthSize, 1f, Growth.Evaluate(ChildClothesAge / 18f)) : 1f) * ScaleUp;
            bool headOnly;
            float foot;
            ModelHandle m = skin.Laid(g, shape, out headOnly, out foot);
            foot *= size;
            lg = new LaidGarment { model = m, fit = size * (foot > GroundFoot ? GroundFoot / foot : 1f) };
            laid[key] = lg;
            return lg;
        }

        // The game's turn for an apparel sprite on the ground (Graphic_RandomRotated), as GroundGear turns its clothes.
        private static float GroundTurn(Thing thing)
        {
            GraphicData data = thing.def.graphicData;
            float max = data != null ? data.onGroundRandomRotateAngle : 0f;
            if (max <= 0.01f)
            {
                return 0f;
            }
            return -max + (float)(thing.thingIDNumber * 542) % (max * 2f);
        }
    }
}
