using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The face tool: the player moves and sizes Facial Animation's art on the chibi's face patch, the whole face and the
    // mouth on top of it, separately for each mod the art comes from (Facial Animation, a retexture of it, Experimentals,
    // a race's own face mod), as each draws its faces for a 2D head of its own proportions. Kept by the mod's packageId in
    // the mod settings (Settings.faceTunes, mouthTunes), so it holds for every pawn wearing that mod's art, in every game.
    //
    // Each layer is tuned by the mod its texture comes from (ModOf: found as the game's ContentFinder finds it, the running
    // mods last loaded first, so a retexture counts as itself), so a face mixing mods (Experimentals' brows over another
    // mod's eyes) gets each part moved by its own mod's numbers. The tuning is laid on in the shaders, per layer (TuneOf,
    // the _MarusFaceShift buffer's tune slots), on top of the patch's own texture places (FaceRaise, the mouth's zoom), so a
    // change shows at once and the ridges, flat beard and skin wounds on the same patch stay where they are; wounds on the
    // eye and a prosthetic eye go with the eyes' mod.
    //
    // Offsets are in face texture widths, + moving the art right and up as seen from the front; the face is scaled about the
    // eye line (FacePivot), the mouth about itself (MouthCentre).
    //
    // Opened from the mod options' Advanced section (Settings.cs, AdvancedSection), which closes the options so the face
    // can be seen; also the dev-mode debug action M3AT > Face tuning window. It tunes the selected pawn's face's mods.
    public static partial class FaceLayers
    {
        // A mod that face art comes from: its packageId (the settings' key; "?" when no running mod has the texture) and name.
        public class FaceMod
        {
            public string key;
            public string label;
        }

        private static readonly Dictionary<Graphic, FaceMod> modsOf = new Dictionary<Graphic, FaceMod>(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<string, FaceMod> modsByKey = new Dictionary<string, FaceMod>();

        // The mod g's texture comes from. Facial Animation's graphics name their textures by path (several joined by \0,
        // the first the art), read as Graphic_Multi reads them (_south), so looked for under that and the bare path.
        internal static FaceMod ModOf(Graphic g)
        {
            if (g == null)
            {
                return Unknown;
            }
            FaceMod found;
            if (modsOf.TryGetValue(g, out found))
            {
                return found;
            }
            found = Unknown;
            string path = g.path;
            if (!string.IsNullOrEmpty(path))
            {
                int end = path.IndexOf('\0');
                string first = end >= 0 ? path.Substring(0, end) : path;
                string[] tries = { first + "_south", first, first + "_east" };
                List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
                for (int m = mods.Count - 1; m >= 0 && found == Unknown; m--)
                {
                    ModContentHolder<Texture2D> holder = mods[m].GetContentHolder<Texture2D>();
                    if (holder == null || holder.contentList.Count == 0)
                    {
                        continue;
                    }
                    foreach (string t in tries)
                    {
                        if (holder.Get(t) != null)
                        {
                            found = ModByKey(mods[m].PackageId, mods[m].Name);
                            break;
                        }
                    }
                }
            }
            modsOf[g] = found;
            return found;
        }

        private static readonly FaceMod Unknown = new FaceMod { key = "?", label = null };

        private static FaceMod ModByKey(string key, string label)
        {
            FaceMod m;
            if (!modsByKey.TryGetValue(key, out m))
            {
                m = new FaceMod { key = key, label = label };
                modsByKey[key] = m;
            }
            return m;
        }

        // The player's tuning of a layer's mod, as the shaders take it: its texture places scaled by 1 + x and moved by
        // (y, z). raise: how far up the face the mouth's places are moved (MouthRaise, or MouthRaisePig under the snout),
        // as the mouth's own tuning is laid on top of the face's through the mouth's zoom (Zoomed). Zero: none.
        private static Vector3 TuneOf(Graphic g, string kind, float raise)
        {
            Settings s = SettingsMod.Settings;
            bool mouth = kind == "MouthControllerComp";
            if (s == null || (!noting && s.faceTunes.Count == 0 && (!mouth || s.mouthTunes.Count == 0)))
            {
                return Vector3.zero;
            }
            if (g == null || IsDummy(g))
            {
                return Vector3.zero;
            }
            FaceMod mod = ModOf(g);
            if (noting)
            {
                Note(mod, kind);
            }
            Vector3 face, mouthTune = new Vector3(0f, 0f, 1f);
            bool faced = s.faceTunes.TryGetValue(mod.key, out face);
            bool mouthed = mouth && s.mouthTunes.TryGetValue(mod.key, out mouthTune);
            if (!mouthed)
            {
                mouthTune = new Vector3(0f, 0f, 1f);   // TryGetValue leaves zeros when there is none, a mouth of no size
            }
            if (!faced && !mouthed)
            {
                return Vector3.zero;
            }
            if (!faced)
            {
                face = new Vector3(0f, 0f, 1f);
            }
            // The face: f = P + (f0 - P - d) / scale, from the places as drawn untuned (f0), about the eye line P.
            float a = 1f / Mathf.Max(0.05f, face.z);
            Vector2 b = FacePivot - (FacePivot + new Vector2(face.x, face.y)) * a;
            if (!mouth)
            {
                return new Vector3(a - 1f, b.x, b.y);
            }
            // The mouth's places (m0 = C + (f0 - C - r) / Z, Zoomed) taken back to the face's, tuned as the face, then zoomed
            // again with the mouth's own move and size on top: m = C + (f - C - r - dm) / (Z ms).
            float ms = Mathf.Max(0.05f, mouthTune.z);
            Vector2 c = MouthCentre;
            Vector2 r = new Vector2(0f, raise);
            Vector2 dm = new Vector2(mouthTune.x, mouthTune.y);
            float z = MouthZoom * ms;
            Vector2 offset = c + (a * c + a * r - a * MouthZoom * c + b - c - r - dm) / z;
            return new Vector3(a / ms - 1f, offset.x, offset.y);
        }

        // ---- What the tool's window is shown: each mod the watched pawn's face layers came from, with which parts, as
        // last drawn (noted by TuneOf while drawing that pawn's face, BeginTuneNotes).

        private static Vector3 drawEyeTune;   // the pawn being drawn's eyes' tuning (Draw), for the wounds on them
        internal static Pawn TuneWatched;   // the face tool window's pawn
        private static bool noting;
        internal static int TuneNotedFrame = -1000;
        internal static readonly List<KeyValuePair<FaceMod, List<string>>> TuneNotes = new List<KeyValuePair<FaceMod, List<string>>>();
        private static readonly List<KeyValuePair<FaceMod, List<string>>> notesNow = new List<KeyValuePair<FaceMod, List<string>>>();

        private static void BeginTuneNotes(Pawn pawn)
        {
            if (noting)
            {
                // The last pawn noted is done: what it showed is what the window lists.
                TuneNotes.Clear();
                TuneNotes.AddRange(notesNow);
                TuneNotedFrame = Time.frameCount;
            }
            noting = pawn != null && pawn == TuneWatched;
            if (noting)
            {
                notesNow.Clear();
            }
        }

        private static void Note(FaceMod mod, string kind)
        {
            string part = kind != null && kind.EndsWith("ControllerComp", StringComparison.Ordinal) ? kind.Substring(0, kind.Length - "ControllerComp".Length) : kind;
            foreach (KeyValuePair<FaceMod, List<string>> n in notesNow)
            {
                if (n.Key == mod)
                {
                    if (!n.Value.Contains(part))
                    {
                        n.Value.Add(part);
                    }
                    return;
                }
            }
            notesNow.Add(new KeyValuePair<FaceMod, List<string>>(mod, new List<string> { part }));
        }

        // Called once a frame by the window, so the notes of a face drawn last in a frame are taken up too.
        internal static void TakeTuneNotes()
        {
            if (noting)
            {
                TuneNotes.Clear();
                TuneNotes.AddRange(notesNow);
                TuneNotedFrame = Time.frameCount;
                noting = false;
            }
        }

        // Whether a pawn has Facial Animation's face drawn on the chibi (for the Adjust face button).
        internal static bool HasTunableFace(Pawn pawn)
        {
            return pawn != null && pawn.RaceProps != null && pawn.RaceProps.Humanlike && Available && FaceOf(pawn) != null;
        }
    }

    public static class FaceTuning
    {
        public static void Open(Pawn pawn = null)
        {
            FaceToolWindow open = Find.WindowStack.WindowOfType<FaceToolWindow>();
            if (open != null)
            {
                if (pawn != null)
                {
                    open.pawn = pawn;
                }
                return;
            }
            Find.WindowStack.Add(new FaceToolWindow { pawn = pawn ?? Find.Selector.SingleSelectedThing as Pawn });
        }

        [LudeonTK.DebugAction("M3AT", "Face tuning window", allowedGameStates = LudeonTK.AllowedGameStates.Playing)]
        private static void DebugOpen()
        {
            Open();
        }

        // A part's name in the window (Facial Animation's controllers: Eyeball, Lid, Brow, Mouth, ...).
        internal static string PartName(string part)
        {
            string key = "M3AT_FacePart_" + part;
            return key.CanTranslate() ? key.Translate().ToString() : part;
        }
    }

    // The window: the mods the selected pawn's face comes from, one picked, and sliders moving and sizing that mod's art.
    // Follows the selected pawn, and doesn't pause, so the face shows each change at once.
    public class FaceToolWindow : Window
    {
        public Pawn pawn;
        private string picked;   // the packageId being tuned
        private bool changed;    // settings to write once the mouse is let go
        private Vector2 scroll, controlsScroll;
        private float controlsHeight = 600f;   // the sliders' and buttons' height as last drawn, for their scroll view

        public override Vector2 InitialSize => new Vector2(400f, 640f);

        public FaceToolWindow()
        {
            draggable = true;
            resizeable = false;
            doCloseX = true;
            forcePause = false;
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            onlyOneOfTypeAllowed = true;
        }

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(UI.screenWidth - InitialSize.x - 20f, 80f, InitialSize.x, InitialSize.y);
        }

        public override void PreClose()
        {
            base.PreClose();
            FaceLayers.TuneWatched = null;
            Write();
        }

        private void Write()
        {
            if (changed && SettingsMod.Settings != null)
            {
                changed = false;
                SettingsMod.Settings.Write();
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            FaceLayers.TakeTuneNotes();
            Pawn selected = Find.Selector.SingleSelectedThing as Pawn;
            if (selected != null && selected != pawn && FaceLayers.HasTunableFace(selected))
            {
                pawn = selected;
                FaceLayers.TuneNotes.Clear();
                FaceLayers.TuneNotedFrame = -1000;
            }
            FaceLayers.TuneWatched = pawn;
            Settings s = SettingsMod.Settings;

            Text.Font = GameFont.Medium;
            Rect title = new Rect(inRect.x, inRect.y, inRect.width, 34f);
            Widgets.Label(title, "M3AT_FaceWindowTitle".Translate());
            Text.Font = GameFont.Small;
            float y = title.yMax + 4f;
            bool seen = pawn != null && Time.frameCount - FaceLayers.TuneNotedFrame < 30 && FaceLayers.TuneNotes.Count > 0;
            if (s == null || pawn == null || !seen)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 80f), "M3AT_FaceNoPawn".Translate());
                return;
            }
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), pawn.LabelShortCap);
            y += 28f;

            // The mods, one row each with the parts it draws; a click picks it.
            List<KeyValuePair<FaceLayers.FaceMod, List<string>>> notes = FaceLayers.TuneNotes;
            if (picked == null || !notes.Any(n => n.Key.key == picked))
            {
                KeyValuePair<FaceLayers.FaceMod, List<string>> eyes = notes.FirstOrDefault(n => n.Value.Contains("Eyeball"));
                picked = (eyes.Key ?? notes[0].Key).key;
            }
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "M3AT_FaceMods".Translate());
            y += 24f;
            // Only as tall as its mods, up to three, so the sliders under it keep the room.
            float modsHeight = Mathf.Min(notes.Count, 3) * 44f;
            Rect outer = new Rect(inRect.x, y, inRect.width, modsHeight);
            Rect view = new Rect(0f, 0f, outer.width - 16f, Mathf.Max(modsHeight, notes.Count * 44f));
            Widgets.BeginScrollView(outer, ref scroll, view);
            float at = 0f;
            foreach (KeyValuePair<FaceLayers.FaceMod, List<string>> n in notes)
            {
                Rect row = new Rect(0f, at, view.width, 42f);
                bool on = n.Key.key == picked;
                if (on)
                {
                    Widgets.DrawHighlightSelected(row);
                }
                else
                {
                    Widgets.DrawHighlightIfMouseover(row);
                }
                string name = n.Key.label ?? "M3AT_FaceUnknownMod".Translate().ToString();
                bool tuned = s.faceTunes.ContainsKey(n.Key.key) || s.mouthTunes.ContainsKey(n.Key.key);
                Widgets.Label(new Rect(row.x + 6f, row.y + 1f, row.width - 12f, 22f), name + (tuned ? " " + "M3AT_FaceTuned".Translate() : ""));
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(row.x + 6f, row.y + 22f, row.width - 12f, 18f), string.Join(", ", n.Value.Select(FaceTuning.PartName)));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                if (Widgets.ButtonInvisible(row))
                {
                    picked = n.Key.key;
                }
                at += 44f;
            }
            Widgets.EndScrollView();
            y = outer.yMax + 8f;

            // The picked mod's numbers.
            Vector3 face, mouth;
            if (!s.faceTunes.TryGetValue(picked, out face))
            {
                face = new Vector3(0f, 0f, 1f);
            }
            if (!s.mouthTunes.TryGetValue(picked, out mouth))
            {
                mouth = new Vector3(0f, 0f, 1f);
            }
            bool hasMouth = notes.Any(n => n.Key.key == picked && n.Value.Contains("Mouth"));
            // The sliders and buttons scroll, as with the mouth's sliders shown they are taller than the window.
            Rect controlsOuter = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Rect controlsView = new Rect(0f, 0f, controlsOuter.width - 16f, controlsHeight);
            Widgets.BeginScrollView(controlsOuter, ref controlsScroll, controlsView);
            Listing_Standard list = new Listing_Standard();
            list.Begin(new Rect(0f, 0f, controlsView.width, 100000f));
            list.Label("M3AT_FaceWhole".Translate());
            Vector3 f = new Vector3(
                Row(list, "M3AT_FaceAcross", face.x, -0.15f, 0.15f, 0.001f),
                Row(list, "M3AT_FaceUp", face.y, -0.15f, 0.15f, 0.001f),
                Row(list, "M3AT_FaceSize", face.z, 0.5f, 1.5f, 0.01f));
            Vector3 m = mouth;
            if (hasMouth)
            {
                list.GapLine(6f);
                list.Label("M3AT_FaceMouth".Translate());
                m = new Vector3(
                    Row(list, "M3AT_FaceAcross", mouth.x, -0.1f, 0.1f, 0.001f),
                    Row(list, "M3AT_FaceUp", mouth.y, -0.1f, 0.1f, 0.001f),
                    Row(list, "M3AT_FaceSize", mouth.z, 0.5f, 2f, 0.01f));
            }
            list.Gap(6f);
            if (list.ButtonText("M3AT_FaceReset".Translate()))
            {
                f = m = new Vector3(0f, 0f, 1f);
            }
            if (list.ButtonText("M3AT_FaceRestoreDefaults".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("M3AT_FaceRestoreDefaultsConfirm".Translate(), () =>
                {
                    s.faceTunes.Clear();
                    s.mouthTunes.Clear();
                    s.faceFits.Clear();
                    s.faceFitRows.Clear();
                    s.Write();
                }, true));
            }
            // A face often mixes mods (one's eyes, another's brows and mouth): this one's numbers given to the rest of them,
            // so the whole face moves together.
            if (notes.Count > 1 && list.ButtonText("M3AT_FaceCopyOthers".Translate()))
            {
                foreach (KeyValuePair<FaceLayers.FaceMod, List<string>> n in notes)
                {
                    if (n.Key.key != picked)
                    {
                        Vector3 had;
                        Set(s.faceTunes, n.Key.key, s.faceTunes.TryGetValue(n.Key.key, out had) ? had : new Vector3(0f, 0f, 1f), f);
                        if (hasMouth && n.Value.Contains("Mouth"))   // only a mouth this one has numbers for
                        {
                            Set(s.mouthTunes, n.Key.key, s.mouthTunes.TryGetValue(n.Key.key, out had) ? had : new Vector3(0f, 0f, 1f), m);
                        }
                        Pinned(pawn, n.Key.key);
                    }
                }
                changed = true;
            }
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            list.Label("M3AT_FaceHelp".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            controlsHeight = list.CurHeight + 8f;
            list.End();
            Widgets.EndScrollView();

            if (Set(s.faceTunes, picked, face, f) | Set(s.mouthTunes, picked, mouth, m))
            {
                changed = true;
                Pinned(pawn, picked);
            }
            if (changed && !Input.GetMouseButton(0))
            {
                Write();   // once the slider is let go, not every step of a drag
            }
        }

        // A tuned mod pinned to the fit its face is drawn with now, an untuned one let go back to the automatic fit (FacePins.cs).
        private static void Pinned(Pawn pawn, string mod)
        {
            Settings s = SettingsMod.Settings;
            if (s.faceTunes.ContainsKey(mod) || s.mouthTunes.ContainsKey(mod))
            {
                FaceLayers.Pin(pawn, mod);
            }
            else
            {
                FaceLayers.Unpin(mod);
            }
        }

        // Keeps a mod's numbers, or drops them back to none at the defaults; true if they changed.
        private static bool Set(Dictionary<string, Vector3> into, string key, Vector3 was, Vector3 now)
        {
            if (now == was)
            {
                return false;
            }
            if (Mathf.Abs(now.x) < 0.0005f && Mathf.Abs(now.y) < 0.0005f && Mathf.Abs(now.z - 1f) < 0.005f)
            {
                into.Remove(key);
            }
            else
            {
                into[key] = now;
            }
            return true;
        }

        private static float Row(Listing_Standard list, string key, float value, float least, float most, float step)
        {
            list.Label(key.Translate() + ": " + value.ToString(step < 0.01f ? "0.000" : "0.00"));
            return Mathf.Round(list.Slider(value, least, most) / step) * step;
        }
    }
}
