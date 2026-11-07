using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The game's red damage flash on the chibi, as IsoRim (Api 59, HitFx.cs) flashes its own figures. The game keeps it per
    // pawn (DamageFlasher: set by a harmful hit, gone after 16 ticks, so it holds while paused and runs at the game's speed)
    // and tints a pawn's sprites by its CurColor, Color.Lerp(white, red, ticks left / 16). IsoRim switches it on in
    // People.Draw, which this mod's prefix skips, so it is worked out here (Of, once per figure, Posed.flash) and drawn as
    // IsoRim draws it: _IsoFlash in MarusSmooth.frag.glsl and MarusFace.frag.glsl, green and blue times 1 - it. The body,
    // garments, hair, beard, gene ears, tail and horns flash (SkinnedFigure.DrawMeshes), and the face layers, ridges and
    // flat beard (FaceLayers.Flash); outlines, wounds, held weapons, tools, holstered weapons and worn gear don't, as the game
    // flashes none of those. A figure drawn for another mod (IsoRim's FigureScope) doesn't flash, as in IsoRim.
    internal static class HitFlash
    {
        private const string Tag = "[M3AT] ";
        public const int Steps = 16;   // IsoRim's: the flash rounded to sixteenths, one property block each

        private static bool tried;
        private static AccessTools.FieldRef<bool> isoOn, scopeActive;
        private static AccessTools.FieldRef<float> isoStrength;
        private static AccessTools.FieldRef<int> isoStep;

        public static bool On   // the mod option (Settings.hitFlash)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.hitFlash; }
        }

        // How red the pawn's figure is drawn now, 0 .. 1 in sixteenths; 0: not at all.
        public static float Of(Pawn pawn)
        {
            if (!On || pawn == null || pawn.Drawer == null || pawn.Drawer.renderer == null)
            {
                return 0f;
            }
            DamageFlasher flasher = pawn.Drawer.renderer.flasher;
            if (flasher == null || !flasher.FlashingNowOrRecently)
            {
                return 0f;
            }
            Find();
            if (scopeActive != null && scopeActive())
            {
                return 0f;
            }
            return Mathf.Clamp(Mathf.RoundToInt((1f - flasher.CurColor.g) * Steps), 0, Steps) / (float)Steps;
        }

        // Around IsoRim's own DynamicModels.DrawParts (a figure posed on the CPU): its Submit draws each solid part with the
        // flash while IsoRim's HitFlash is on, at its step. Returns what it was, for End; false and nothing done for no
        // flash, or an IsoRim without it.
        public static bool Begin(float flash, out bool was)
        {
            was = false;
            if (flash <= 0f)
            {
                return false;
            }
            Find();
            if (isoOn == null)
            {
                return false;
            }
            was = isoOn();
            isoOn() = true;
            isoStrength() = flash;
            isoStep() = Mathf.RoundToInt(flash * Steps);
            return true;
        }

        public static void End(bool was)
        {
            isoOn() = was;
        }

        // Looks once for IsoRim's internals: its HitFlash (Api 59) and FigureScope.
        private static void Find()
        {
            if (tried)
            {
                return;
            }
            tried = true;
            try
            {
                Type scope = AccessTools.TypeByName("IsoRim.FigureScope");
                FieldInfo active = scope != null ? AccessTools.Field(scope, "Active") : null;
                if (active != null)
                {
                    scopeActive = AccessTools.StaticFieldRefAccess<bool>(active);
                }
                Type flash = AccessTools.TypeByName("IsoRim.HitFlash");
                FieldInfo on = flash != null ? AccessTools.Field(flash, "On") : null;
                FieldInfo strength = flash != null ? AccessTools.Field(flash, "strength") : null;
                FieldInfo step = flash != null ? AccessTools.Field(flash, "step") : null;
                if (on != null && strength != null && step != null)
                {
                    isoOn = AccessTools.StaticFieldRefAccess<bool>(on);
                    isoStrength = AccessTools.StaticFieldRefAccess<float>(strength);
                    isoStep = AccessTools.StaticFieldRefAccess<int>(step);
                }
                else
                {
                    Log.Message(Tag + "this IsoRim has no hit flash, so pawns posed on the CPU don't flash red when hit.");
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "the hit flash can't reach IsoRim's: " + e.Message);
                isoOn = null;
            }
        }
    }
}
