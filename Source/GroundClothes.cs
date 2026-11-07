using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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
    // standing on its rim, front to the south. It is baked into the map mesh by IsoRim's own writer (ModelBaker.WritePosed),
    // with the garment's image if it has one, so nothing is drawn per frame. Apparel with no garment keeps IsoRim's look.
    public sealed partial class SkinnedFigure
    {
        private Posed groundPose;   // the standing pose garments are laid from, reused

        // The garment (piece) on the standing pose in this shape as an IsoRim model (cells, the figure facing -z, +y up),
        // laid down and set on the ground at its middle, with each corner white and tinted (a textured garment's its mean
        // colour, its image on a material of its own). headOnly: every vertex rides mostly the head or the neck.
        internal Model Laid(int piece, Shape shape, out bool headOnly, out float foot)
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
            Model m = new Model();
            m.positions = new Vector3[tris.Length];
            m.normals = new Vector3[tris.Length];
            m.hull = new Vector3[tris.Length];
            m.colors = new Color[tris.Length];
            m.tinted = new bool[tris.Length];
            m.uvs = textured ? new Vector2[tris.Length] : null;
            m.material = textured ? new int[tris.Length] : null;
            if (textured)
            {
                m.materials = new[]
                {
                    new ModelMaterial
                    {
                        name = "stuff_" + g.name,
                        texture = g.texture.name,   // only a placeholder: the image is runtimeTexture
                        color = Color.white,
                        tinted = true,
                        runtimeTexture = g.texture,
                        runtimeName = g.texture.name
                    }
                };
            }
            Color corner = textured ? g.mean : Color.white;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = t.MultiplyPoint3x4(posed[tris[i]]), b = t.MultiplyPoint3x4(posed[tris[i + 1]]), c = t.MultiplyPoint3x4(posed[tris[i + 2]]);
                Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                for (int k = 0; k < 3; k++)
                {
                    int v = tris[i + k];
                    m.positions[i + k] = k == 0 ? a : k == 1 ? b : c;
                    m.normals[i + k] = normal;
                    m.hull[i + k] = t.MultiplyVector(hulled[v]);
                    m.colors[i + k] = corner;
                    m.tinted[i + k] = true;
                    if (textured)
                    {
                        m.uvs[i + k] = g.uvs[v];
                    }
                    m.maxY = Mathf.Max(m.maxY, m.positions[i + k].y);
                }
            }
            return m;
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
            public Model model;
            public float fit;   // its scale on the ground: the figure's size, shrunk to fit its cell
        }

        private static readonly Dictionary<string, LaidGarment> laid = new Dictionary<string, LaidGarment>();
        private static Func<SectionLayer, Thing, Model, Vector3, Quaternion, float, float, Color, float, bool> writePosed;

        // Not inlined and called inside its own try, so an IsoRim without GroundGear or WritePosed only loses this.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PatchGroundClothes(Harmony harmony)
        {
            MethodInfo bake = AccessTools.Method(typeof(GroundGear), nameof(GroundGear.Bake), new[] { typeof(SectionLayer), typeof(Thing), typeof(float) });
            MethodInfo write = AccessTools.Method(typeof(ModelBaker), "WritePosed");
            if (bake == null || write == null || write.ReturnType != typeof(bool))
            {
                Log.Warning(Tag + "apparel on the ground keeps IsoRim's look: its GroundGear.Bake or ModelBaker.WritePosed is missing.");
                return;
            }
            writePosed = (Func<SectionLayer, Thing, Model, Vector3, Quaternion, float, float, Color, float, bool>)Delegate.CreateDelegate(
                typeof(Func<SectionLayer, Thing, Model, Vector3, Quaternion, float, float, Color, float, bool>), write);
            harmony.Patch(bake, prefix: new HarmonyMethod(typeof(WalkFigures), nameof(GroundBakePrefix)));
        }

        // GroundGear.Bake: apparel with a garment of the chibi's is written as that garment, laid down. Anything else, and
        // what IsoRim prints its own way (damage marks, a blueprint's ghost), is left to IsoRim.
        public static bool GroundBakePrefix(SectionLayer layer, Thing thing, float baseHeight, ref bool __result)
        {
            Apparel a = thing as Apparel;
            if (!GroundGarments || a == null || !GroundGear.WornLook(thing.def) || ModelBaker.DamageMarks > 0 || ModelBaker.Ghost.a > 0f)
            {
                return true;
            }
            try
            {
                if (!Ready() || skin == null)
                {
                    return true;
                }
                int g = GroundGarmentOf(a.def);
                if (g <= 0)
                {
                    return true;
                }
                LaidGarment lg = LaidOf(g, ChildOnly(a.def));
                if (lg == null)
                {
                    return true;
                }
                Color colour = !skin.Tinted(g, a) ? Color.white : apparelColour != null ? apparelColour(a) : a.DrawColor;
                float scale = lg.fit * (thing.MultipleItemsPerCellDrawn() ? 0.8f : 1f);
                Quaternion turn = Quaternion.AngleAxis(GroundTurn(thing), Vector3.up);
                int mark = RoofClip.Mark(layer);
                try
                {
                    __result = writePosed(layer, thing, lg.model, thing.DrawPos, turn, scale, baseHeight, colour, 1f);
                }
                finally
                {
                    RoofClip.Tag(layer, mark, thing.Position);
                }
                return !__result;   // nothing written (the map block is full): IsoRim's own
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "laying " + thing.def.defName + " on the ground failed, IsoRim's look is kept: " + e, 0x4d334154 ^ thing.def.shortHash);
                return true;
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

        // The garment a pawn wearing only this apparel would have on (WornGarments): its own, else the first it stands in
        // for; -1 for none. A held one (a shield) is left to IsoRim: it was made upright in the hand, not to lie as clothes do.
        private static int GroundGarmentOf(ThingDef def)
        {
            int g = skin.GarmentOf(def);
            if (g > 0)
            {
                return skin.IsHeld(g) ? -1 : g;
            }
            for (g = 1; g < skin.Pieces; g++)
            {
                if (skin.Covers(g, def.apparel))
                {
                    return g;
                }
            }
            return -1;
        }

        // Apparel only children wear (GroundGear.ChildOnly): laid out on a child's body, at a child's size.
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
            Model m = skin.Laid(g, shape, out headOnly, out foot);
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
