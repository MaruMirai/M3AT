using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A tuned face mod keeps the automatic fit it was tuned on (FaceGaze.cs: a race's eyes and mouth moved onto the human
    // layout). The face tool's numbers are a correction on top of what the player saw, so laid over a fit that has since
    // changed they would double up or pull the wrong way. So the first change to a mod's tuning pins the fit its face was
    // drawn with then (Settings.faceFits, faceFitRows), and that mod is drawn with the pinned fit for good: a later change
    // to the measuring leaves it as the player left it, while untuned mods follow the measuring. Resetting a mod (the
    // tool's Reset this mod) drops the pin with the tuning, back to automatic.
    //
    // Tunings made before fits were pinned have none. A human face set's are pinned to no fit, which is what they were
    // tuned on. A race's own face art's (its textures in a folder of its own, not Facial Animation's human ones) were
    // tuned on the old fit, which no longer exists, so they are cleared once, with a message, when that race's face is
    // first drawn.
    public static partial class FaceLayers
    {
        private static readonly HashSet<string> clearedTunes = new HashSet<string>();

        private static bool Tuned(Settings s, string mod)
        {
            return s.faceTunes.ContainsKey(mod) || s.mouthTunes.ContainsKey(mod);
        }

        // The eyes' fit a tuned face mod was pinned to; false if it has none (untuned, or not pinned yet).
        private static bool PinnedEyes(string mod, out EyeFit fit)
        {
            fit = new EyeFit { scale = 1f };
            Settings s = SettingsMod.Settings;
            Vector3 eyes;
            Vector2 rows;
            if (s == null || mod == null || !Tuned(s, mod) || !s.faceFits.TryGetValue(mod, out eyes))
            {
                return false;
            }
            s.faceFitRows.TryGetValue(mod, out rows);
            fit = new EyeFit { scale = eyes.x > 0f ? eyes.x : 1f, rise = eyes.y, spread = eyes.z, row = rows.x };
            return true;
        }

        // And the mouth's move.
        private static bool PinnedMouth(string mod, out float shift)
        {
            shift = 0f;
            Settings s = SettingsMod.Settings;
            Vector2 rows;
            if (s == null || mod == null || !Tuned(s, mod) || !s.faceFits.ContainsKey(mod) || !s.faceFitRows.TryGetValue(mod, out rows))
            {
                return false;
            }
            shift = rows.y;
            return true;
        }

        // Art in Facial Animation's own human folders (its sets and their retextures), not a race's of its own.
        private static bool HumanArt(Graphic g)
        {
            string path = g != null ? g.path : null;
            return path == null || path.StartsWith("Things/Pawn/FA-Experimentals/") || path.StartsWith("Things/Pawn/Humanlike/");
        }

        // A tuned mod with no pin, seen while drawing pawn with art g from it: a human set's pinned as it is, a race's own
        // cleared (the class comment).
        internal static void CheckPins(Pawn pawn, string mod, Graphic g)
        {
            Settings s = SettingsMod.Settings;
            if (s == null || mod == null || !Tuned(s, mod) || s.faceFits.ContainsKey(mod))
            {
                return;
            }
            // A race's own art: not drawn on a human, and not one of Facial Animation's human sets (by its folders where the
            // art is at hand, else by the mod).
            bool raceArt = pawn.def != ThingDefOf.Human && !KnownHumanMod(mod) && (g == null || !HumanArt(g));
            if (!raceArt)
            {
                Pin(pawn, mod);
                s.Write();
                return;
            }
            s.faceTunes.Remove(mod);
            s.mouthTunes.Remove(mod);
            s.Write();
            if (clearedTunes.Add(mod))
            {
                FaceMod m = ModOf(mod);
                Messages.Message("M3AT_FaceTuneCleared".Translate(m != null && m.label != null ? m.label : mod, pawn.def.LabelCap), MessageTypeDefOf.NeutralEvent, false);
                Log.Message(Tag + "the face tuning of " + mod + " was made before " + pawn.def.defName + "'s face was fitted automatically, so it was cleared.");
            }
        }

        // The mods whose art is Facial Animation's human faces (its own, Experimentals and their retextures), as their
        // folders are; for a mod seen without a graphic to go by.
        private static bool KnownHumanMod(string mod)
        {
            string m = mod.ToLowerInvariant();
            return m == "nals.facialanimation" || m == "nals.facialanimationexperimentals" || m == "barb.facetexture" || m == "unagi.facialanimation";
        }

        private static FaceMod ModOf(string key)
        {
            FaceMod m;
            return modsByKey.TryGetValue(key, out m) ? m : null;
        }

        // Pins mod to the fit pawn's face is drawn with now: its race's measured fit (none for the human). For the tool,
        // at the first change to a mod's tuning.
        internal static void Pin(Pawn pawn, string mod)
        {
            Settings s = SettingsMod.Settings;
            if (s == null || mod == null || s.faceFits.ContainsKey(mod))
            {
                return;
            }
            EyeFit fit = new EyeFit { scale = 1f };
            float mouth = 0f;
            if (pawn != null && pawn.def != ThingDefOf.Human)
            {
                raceEyeFits.TryGetValue(pawn.def, out fit);
                if (fit.scale <= 0f)
                {
                    fit.scale = 1f;
                }
                raceMouthRows.TryGetValue(pawn.def, out mouth);
            }
            s.faceFits[mod] = new Vector3(fit.scale, fit.rise, fit.spread);
            s.faceFitRows[mod] = new Vector2(fit.row, mouth);
        }

        // Drops a mod's pin (its tuning reset): it follows the automatic fit again.
        internal static void Unpin(string mod)
        {
            Settings s = SettingsMod.Settings;
            if (s != null && mod != null)
            {
                s.faceFits.Remove(mod);
                s.faceFitRows.Remove(mod);
            }
        }
    }
}
