using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace M3AT
{
    // Dev tools for judging the faces' placement across races (dev mode only): a lineup of pawns of every loaded race
    // and humans (M3AT > Race face lineup), and a window whose sliders move every face at once (M3AT > All faces tuning):
    // the whole face, the eyes alone (up, spread, size) and the mouth, on top of the automatic race fit (FaceGaze.cs,
    // FaceRestyle.cs) and each mod's own tuning (FaceTuning.cs). Not saved: the numbers are for finding better defaults,
    // which its Copy button gives as text.
    public static partial class FaceLayers
    {
        internal static Vector3 DevFace = new Vector3(0f, 0f, 1f);   // across, up (face texture widths), size
        internal static Vector3 DevMouth = new Vector3(0f, 0f, 1f);  // the mouth's on top of the face's
        internal static Vector3 DevEyes = new Vector3(0f, 0f, 1f);   // up, each eye out, size (the eye, lid, brow and lid option layers)

        internal static bool DevTuning
        {
            get { return DevFace != new Vector3(0f, 0f, 1f) || DevMouth != new Vector3(0f, 0f, 1f); }
        }
    }

    public static class FaceDevTune
    {
        private const string Tag = "[M3AT] ";
        private const int PerRace = 4;
        private static readonly List<Pawn> lineup = new List<Pawn>();

        [DebugAction("M3AT", "Race face lineup", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Lineup()
        {
            Map map = Find.CurrentMap;
            ClearLineup();
            if (!SettingsMod.Settings.otherRaces)
            {
                Messages.Message("M3AT: \"Other races as 3D pawns\" is off, so the races are drawn as IsoRim's cards.", MessageTypeDefOf.CautionInput, false);
            }
            // The human first (Facial Animation's own faces or their retexture), then each Humanoid Alien Races race.
            List<ThingDef> races = new List<ThingDef> { ThingDefOf.Human };
            races.AddRange(DefDatabase<ThingDef>.AllDefs.Where(d => d.race != null && d.race.Humanlike && d != ThingDefOf.Human
                && d.GetType().Name.Contains("AlienRace")).OrderBy(d => d.defName));
            IntVec3 origin = Find.CameraDriver.MapPosition;
            float[] ages = { 20f, 24f, 32f, 45f };
            Gender[] genders = { Gender.Female, Gender.Male, Gender.Female, Gender.Male };
            int row = 0;
            foreach (ThingDef race in races)
            {
                PawnKindDef kind = race == ThingDefOf.Human ? PawnKindDefOf.Colonist : DefDatabase<PawnKindDef>.AllDefs.FirstOrDefault(k => k.race == race);
                if (kind == null)
                {
                    continue;
                }
                for (int i = 0; i < PerRace; i++)
                {
                    Pawn pawn;
                    try
                    {
                        pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, Faction.OfPlayer, forceGenerateNewPawn: true,
                            fixedBiologicalAge: ages[i], fixedChronologicalAge: ages[i], fixedGender: genders[i]));
                    }
                    catch (System.Exception e)
                    {
                        Log.Warning(Tag + "the lineup couldn't make a " + race.defName + ": " + e.Message);
                        continue;
                    }
                    pawn.equipment.DestroyAllEquipment();
                    foreach (Apparel worn in pawn.apparel.WornApparel.ToList())
                    {
                        List<BodyPartGroupDef> groups = worn.def.apparel.bodyPartGroups;
                        if (groups.Contains(BodyPartGroupDefOf.UpperHead) || groups.Contains(BodyPartGroupDefOf.FullHead) || groups.Contains(BodyPartGroupDefOf.Eyes))
                        {
                            pawn.apparel.Remove(worn);
                            worn.Destroy();
                        }
                    }
                    IntVec3 want = origin + new IntVec3(2 * i - PerRace + 1, 0, -2 * row);
                    IntVec3 cell = CellFinder.StandableCellNear(want, map, 3f);
                    if (!cell.IsValid)
                    {
                        continue;
                    }
                    GenSpawn.Spawn(pawn, cell, map, Rot4.South);
                    // Stood still facing south, as long as it lasts.
                    Job wait = JobMaker.MakeJob(JobDefOf.Wait, 120000);
                    pawn.jobs.TryTakeOrderedJob(wait);
                    pawn.Rotation = Rot4.South;
                    lineup.Add(pawn);
                }
                row++;
            }
            Messages.Message("M3AT: " + lineup.Count + " pawns of " + row + " races in rows by the camera, the human first.", MessageTypeDefOf.NeutralEvent, false);
            Find.WindowStack.Add(new FaceDevWindow());
        }

        [DebugAction("M3AT", "Race face lineup: remove", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ClearLineup()
        {
            foreach (Pawn pawn in lineup)
            {
                if (pawn != null && !pawn.Destroyed)
                {
                    pawn.Destroy();
                }
            }
            lineup.Clear();
        }

        [DebugAction("M3AT", "All faces tuning", allowedGameStates = AllowedGameStates.Playing)]
        private static void Open()
        {
            Find.WindowStack.Add(new FaceDevWindow());
        }
    }

    public class FaceDevWindow : Window
    {
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(380f, 700f);

        public FaceDevWindow()
        {
            draggable = true;
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

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "All faces (dev)");
            Text.Font = GameFont.Small;
            Rect outer = new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 38f);
            Rect view = new Rect(0f, 0f, outer.width - 16f, 860f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            Listing_Standard list = new Listing_Standard();
            list.Begin(view);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            list.Label("Moves every face at once, humans and races, on top of the automatic race fit and each mod's own tuning. Not saved. Units: widths of the face texture.");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            list.Label("Whole face");
            FaceLayers.DevFace = new Vector3(
                Row(list, "Across", FaceLayers.DevFace.x, -0.1f, 0.1f, 0.001f),
                Row(list, "Up", FaceLayers.DevFace.y, -0.1f, 0.1f, 0.001f),
                Row(list, "Size", FaceLayers.DevFace.z, 0.6f, 1.4f, 0.01f));
            list.GapLine(6f);
            list.Label("Eyes (eyes, lids, brows)");
            FaceLayers.DevEyes = new Vector3(
                Row(list, "Up", FaceLayers.DevEyes.x, -0.05f, 0.05f, 0.001f),
                Row(list, "Each eye out", FaceLayers.DevEyes.y, -0.03f, 0.03f, 0.001f),
                Row(list, "Size", FaceLayers.DevEyes.z, 0.6f, 1.4f, 0.01f));
            list.GapLine(6f);
            list.Label("Mouth");
            FaceLayers.DevMouth = new Vector3(
                Row(list, "Across", FaceLayers.DevMouth.x, -0.05f, 0.05f, 0.001f),
                Row(list, "Up", FaceLayers.DevMouth.y, -0.05f, 0.05f, 0.001f),
                Row(list, "Size", FaceLayers.DevMouth.z, 0.5f, 2f, 0.01f));
            list.GapLine(6f);
            Settings s = SettingsMod.Settings;
            if (s != null)
            {
                list.CheckboxLabeled("Crisper race mouths (mod option)", ref s.raceMouthInk);
            }
            if (list.ButtonText("Reset these sliders"))
            {
                FaceLayers.DevFace = FaceLayers.DevMouth = FaceLayers.DevEyes = new Vector3(0f, 0f, 1f);
            }
            if (list.ButtonText("Copy the numbers"))
            {
                string text = "M3AT all-faces tuning: face across " + FaceLayers.DevFace.x.ToString("0.000") + ", up " + FaceLayers.DevFace.y.ToString("0.000") + ", size " + FaceLayers.DevFace.z.ToString("0.00")
                    + "; eyes up " + FaceLayers.DevEyes.x.ToString("0.000") + ", out " + FaceLayers.DevEyes.y.ToString("0.000") + ", size " + FaceLayers.DevEyes.z.ToString("0.00")
                    + "; mouth across " + FaceLayers.DevMouth.x.ToString("0.000") + ", up " + FaceLayers.DevMouth.y.ToString("0.000") + ", size " + FaceLayers.DevMouth.z.ToString("0.00");
                GUIUtility.systemCopyBuffer = text;
                Log.Message("[M3AT] " + text);
                Messages.Message("M3AT: copied (and written to the log).", MessageTypeDefOf.NeutralEvent, false);
            }
            list.End();
            Widgets.EndScrollView();
        }

        private static float Row(Listing_Standard list, string label, float value, float least, float most, float step)
        {
            list.Label(label + ": " + value.ToString(step < 0.01f ? "0.000" : "0.00"));
            return Mathf.Round(list.Slider(value, least, most) / step) * step;
        }
    }
}
