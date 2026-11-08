using System;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Items lying on the ground (a weapon laid flat on its side, clothes) lost most of their outline under the terrain.
    // IsoRim bakes a model's outline as a shell pushed out round it and clamps every corner of it to the ground's height
    // (ModelBaker.Write, Mathf.Max(Floor, y)), so the shell's underside is flattened into a sheet exactly on the ground,
    // which is the outline's ring round a thing that lies flat. The terrain at the same depth hides it, leaving only the
    // bits where the shell rises up the thing's sides. Here the corners of an item's outline that the clamp put on the
    // ground are lifted OutlineLift cells, so the ring sits just above the terrain. IsoRim's own code is not changed.
    // IsoRim fixes this itself from its M230 (the ring lifted and drawn by IsoRimOutlineRing, a ShaderDef of its own), and
    // then this is not patched in: it works round IsoRim's internals, which Api 62 does not cover.
    public static partial class WalkFigures
    {
        private const float OutlineLift = 0.01f;   // cells: how far above the ground an item's outline ring is lifted
        private const string IsoRimRingDef = "IsoRimOutlineRing";   // IsoRim's own ring (M230): with it, IsoRim lifts the ring itself

        // Not inlined and called inside its own try, so an IsoRim without ModelBaker.Write only loses this.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void PatchGroundOutlines(Harmony harmony)
        {
            if (DefDatabase<ShaderDef>.GetNamedSilentFail(IsoRimRingDef) != null)
            {
                return;   // IsoRim keeps its item outline rings off the ground itself
            }
            MethodInfo write = AccessTools.Method(typeof(ModelBaker), "Write");
            if (write == null || write.ReturnType != typeof(bool))
            {
                Log.Warning(Tag + "items on the ground keep IsoRim's outline: its ModelBaker.Write is missing.");
                return;
            }
            harmony.Patch(write, prefix: new HarmonyMethod(typeof(WalkFigures), nameof(GroundOutlinePrefix)),
                postfix: new HarmonyMethod(typeof(WalkFigures), nameof(GroundOutlinePostfix)));
        }

        // Where the item's outline piece stood before Write added to it: its piece of the mesh, and how many corners it had.
        public struct OutlineMark
        {
            public LayerSubMesh outline;
            public int from;
        }

        public static void GroundOutlinePrefix(SectionLayer layer, Thing thing, float outlineBold, out OutlineMark __state)
        {
            __state = default;
            try
            {
                IsoRimSettings st = IsoRimMod.Settings;
                if (thing == null || thing.def.category != ThingCategory.Item || st == null || !st.outlines || outlineBold <= 0f
                    || ModelBaker.DamageMarks > 0 || ModelBaker.Ghost.a > 0f)
                {
                    return;
                }
                Material mat = R3DMaterials.Outline;
                LayerSubMesh sm = mat != null ? MeshWriter.Sub(layer, mat) : null;
                if (sm != null)
                {
                    __state.outline = sm;
                    __state.from = sm.verts.Count;
                }
            }
            catch (Exception)
            {
                __state = default;
            }
        }

        public static void GroundOutlinePostfix(float baseHeight, OutlineMark __state)
        {
            LayerSubMesh sm = __state.outline;
            if (sm == null)
            {
                return;
            }
            float squash = View3D.Squash;
            float ground = baseHeight / squash;   // the height Write clamps the corners to, in the mesh's own units
            float lifted = ground + OutlineLift / squash;
            for (int i = __state.from; i < sm.verts.Count; i++)
            {
                Vector3 v = sm.verts[i];
                if (v.y < lifted)
                {
                    v.y = lifted;
                    sm.verts[i] = v;
                }
            }
        }
    }
}
