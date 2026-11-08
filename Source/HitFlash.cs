using UnityEngine;
using Verse;

namespace M3AT
{
    // The game's red damage flash on the chibi, as IsoRim (Api 59, HitFx.cs) flashes its own figures. The game keeps it per
    // pawn (DamageFlasher: set by a harmful hit, gone after 16 ticks, so it holds while paused and runs at the game's speed)
    // and tints a pawn's sprites by its CurColor, Color.Lerp(white, red, ticks left / 16). It is worked out here (Of, once
    // per figure, Posed.flash), as FigureDrawContext.Flash would give it, but for each figure drawn (a carried one is drawn
    // inside its carrier's call), and drawn as IsoRim draws it: Api.SubmitFigureMesh writes it into _IsoFlash, which
    // MarusSmooth.frag.glsl and MarusFace.frag.glsl take, green and blue times 1 - it. The body, garments, hair, beard, gene
    // ears, tail and horns flash (SkinnedFigure.DrawMeshes), and the face layers, ridges and flat beard (FaceLayers.Flash);
    // outlines, wounds, held weapons, tools, holstered weapons and worn gear don't, as the game flashes none of those. A
    // figure drawn for another mod (FigureDrawContext.ForOtherMod) doesn't flash, as in IsoRim.
    internal static class HitFlash
    {
        public const int Steps = 16;   // IsoRim's: the flash rounded to sixteenths, one property block each

        public static bool On   // the mod option (Settings.hitFlash)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.hitFlash; }
        }

        // How red the pawn's figure is drawn now, 0 .. 1 in sixteenths; 0: not at all. forOtherMod: drawn for another mod.
        public static float Of(Pawn pawn, bool forOtherMod)
        {
            if (!On || forOtherMod || pawn == null || pawn.Drawer == null || pawn.Drawer.renderer == null)
            {
                return 0f;
            }
            DamageFlasher flasher = pawn.Drawer.renderer.flasher;
            if (flasher == null || !flasher.FlashingNowOrRecently)
            {
                return 0f;
            }
            return Mathf.Clamp(Mathf.RoundToInt((1f - flasher.CurColor.g) * Steps), 0, Steps) / (float)Steps;
        }
    }
}
