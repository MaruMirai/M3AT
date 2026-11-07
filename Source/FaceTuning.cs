using LudeonTK;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A small window to move Facial Animation's face and mouth about on the chibi's face patch while the game
    // runs, to find the numbers to bake into FaceLayers (FacePivot, MouthRaise, MouthZoom) or the exporter (FACE_EYES,
    // FACE_SPAN). Not saved. Offsets are in face texture widths, + moving the art up and to the pawn's right as seen
    // from the front. The ridges, the flat beard and eye wounds are drawn on the same patch, so they move with the face
    // meanwhile. The numbers are on top of FaceLayers.FaceRaise.
    //
    // Hidden unless built with FACE_TUNING defined (dotnet build Source/M3AT.csproj -p:DefineConstants=FACE_TUNING),
    // which brings back the dev-mode debug action (M3AT > Face tuning window) and the button in the mod options.
    public static class FaceTuning
    {
        public static float faceX, faceY, faceScale = 1f;
        public static float mouthX, mouthY, mouthScale = 1f;
        public static int Version;   // raised on every change, so each patch lays its texture places again (FaceLayers.Retune)

        public static void Open()
        {
            if (Find.WindowStack.WindowOfType<FaceTuningWindow>() == null)
            {
                Find.WindowStack.Add(new FaceTuningWindow());
            }
        }

#if FACE_TUNING
        [DebugAction("M3AT", "Face tuning window", allowedGameStates = AllowedGameStates.Playing)]
        private static void DebugOpen()
        {
            Open();
        }
#endif
    }

    public class FaceTuningWindow : Window
    {
        public override Vector2 InitialSize => new Vector2(360f, 470f);

        public FaceTuningWindow()
        {
            draggable = true;
            resizeable = false;
            doCloseX = true;
            forcePause = false;
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = false;
            onlyOneOfTypeAllowed = true;
        }

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(UI.screenWidth - InitialSize.x - 20f, 80f, InitialSize.x, InitialSize.y);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            list.Label("Face tuning (temporary, not saved)");
            float fx = Row(list, "Face left/right", FaceTuning.faceX, -0.15f, 0.15f, 0.001f);
            float fy = Row(list, "Face up/down", FaceTuning.faceY, -0.15f, 0.15f, 0.001f);
            float fs = Row(list, "Face scale", FaceTuning.faceScale, 0.5f, 1.5f, 0.01f);
            list.GapLine();
            float mx = Row(list, "Mouth left/right", FaceTuning.mouthX, -0.1f, 0.1f, 0.001f);
            float my = Row(list, "Mouth up/down", FaceTuning.mouthY, -0.1f, 0.1f, 0.001f);
            float ms = Row(list, "Mouth scale", FaceTuning.mouthScale, 0.5f, 2f, 0.01f);
            list.Gap();
            if (list.ButtonText("Reset"))
            {
                fx = fy = mx = my = 0f;
                fs = ms = 1f;
            }
            if (list.ButtonText("Log values"))
            {
                Log.Message("[M3AT] face tuning: face x " + fx.ToString("0.000") + " y " + fy.ToString("0.000") + " scale " + fs.ToString("0.00")
                    + "; mouth x " + mx.ToString("0.000") + " y " + my.ToString("0.000") + " scale " + ms.ToString("0.00"));
            }
            list.End();
            if (fx != FaceTuning.faceX || fy != FaceTuning.faceY || fs != FaceTuning.faceScale
                || mx != FaceTuning.mouthX || my != FaceTuning.mouthY || ms != FaceTuning.mouthScale)
            {
                FaceTuning.faceX = fx;
                FaceTuning.faceY = fy;
                FaceTuning.faceScale = fs;
                FaceTuning.mouthX = mx;
                FaceTuning.mouthY = my;
                FaceTuning.mouthScale = ms;
                FaceTuning.Version++;
            }
        }

        private static float Row(Listing_Standard list, string name, float value, float least, float most, float step)
        {
            list.Label(name + ": " + value.ToString(step < 0.01f ? "0.000" : "0.00"));
            return Mathf.Round(list.Slider(value, least, most) / step) * step;
        }
    }
}
