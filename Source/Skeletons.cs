using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A person gone to bones (the game's RotDrawMode.Dessicated: a corpse dried out, and Anomaly's shamblers raised from one,
    // which keep their corpse's rot stage) is drawn as the chibi's skeleton (anim_scripts/make_skeleton.py, the garment
    // Skeleton): a cartoon skull and rigid bones in the chibi's proportions, skinned to the same bones, so it moves, lies
    // and falls as the chibi does. It is drawn in place of the body, with what the pawn still wears over it, and none of the
    // flesh's parts: no hair, beard, face, ears, tail, horns, fur, gene parts or wounds (the game leaves them off a
    // dessicated body too). Without the garment (an older export), or with the option off, it is IsoRim's own skeleton.
    public sealed partial class SkinnedFigure
    {
        private const string SkeletonDef = "M3AT_Skeleton";   // its def name: never apparel, so never worn or a stand-in

        // The skeleton's piece, or -1 if the export has none.
        public int SkeletonPiece
        {
            get
            {
                int g;
                return garmentOf.TryGetValue(SkeletonDef, out g) ? g : -1;
            }
        }
    }

    public static partial class WalkFigures
    {
        public static bool Skeletons   // false: bones are left to IsoRim's skeleton (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.skeletons; }
        }

        // Whether the chibi draws people gone to bones itself: the option on and the export has the skeleton. (Before the
        // models are read, no: the first draw reads them.)
        private static bool DrawsBones()
        {
            return Skeletons && skin != null && skin.SkeletonPiece > 0;
        }

        // Whether the pawn is drawn as the skeleton now.
        private static bool Skeletal(Pawn pawn)
        {
            return RotOf(pawn) == RotDrawMode.Dessicated && DrawsBones();
        }
    }
}
