#if AUTOTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace M3AT
{
    // A test driver for features that are hard to set up by hand: each scenario spawns its own pawns on a clear patch of
    // the map, sets up what it tests through the game's own code, points IsoRim's camera at it, and writes pictures of the
    // 3D view (Api.RequestSceneImage, no interface) and a probe of the figure's state each tick, then a report of which
    // checks passed. Only in a build with AUTOTEST defined (dotnet build Source/M3AT.csproj -p:DefineConstants=AUTOTEST),
    // and only when the game is started with -m3atTest=<scenario,...> (all: every one), best with -quicktest:
    //
    //   ./RimWorld -quicktest -m3atTest=all [-m3atTestOut=<folder>] [-m3atTestVideo] [-m3atTestQuit]
    //
    // Stills mode (the default) pauses the game and moves it on one tick a frame, so every picture is at a known tick.
    // -m3atTestVideo runs it at normal speed instead and records the game window with ffmpeg (x11grab), one clip per
    // scenario, for a person to look at. -m3atTestQuit closes the game when done.
    //
    // -m3atTest=shots -m3atTestLoad=<save> loads that save instead and changes nothing in it: the camera held at the
    // freecam eye saved with the game, it steps the game a tick a frame for -m3atShotTicks (300) and writes a scene image
    // every -m3atShotEvery (2) ticks (from -m3atShotFrom ticks in, 0; each file named by its tick), for picking the best
    // still (a Workshop picture) out of a run.
    [StaticConstructorOnStartup]
    internal static class AutoTest
    {
        private const string Tag = "[M3AT] [AutoTest] ";
        private const float LookHeight = 0.6f;   // cells over the ground the camera looks at
        private const float LookDistance = 4.5f;   // cells from it
        private const float LookPitch = 22f;   // degrees down
        private const float LookFov = 50f;
        private const int Clear = 6;   // cells round the middle of the test patch kept clear

        private static readonly string[] All = { "hitflash", "beatfire", "horseshoes", "hunker", "cuddle" };
        private static readonly object PoseOwner = new object();

        private static List<string> wanted;
        private static string outDir;
        private static bool video, quit;
        private static readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
        private static bool started;
        private static IDisposable pose;
        private static Vector3 lookAt;
        private static float lookYaw;
        private static float lookPitch = LookPitch;   // degrees down (a scenario may look down more steeply, and puts it back)
        private static float lookDistance = LookDistance, lookHeight = LookHeight;   // (a scenario may come closer, and puts them back)
        private static bool imageDone;
        private static StreamWriter probe;
        private static readonly StringBuilder report = new StringBuilder();
        private static readonly List<Thing> spawned = new List<Thing>();
        private static IntVec3 middle;
        private static Map map;
        private static Process recorder;

        static AutoTest()
        {
            string which;
            if (!GenCommandLine.TryGetCommandLineArg("m3atTest", out which) || string.IsNullOrEmpty(which))
            {
                return;
            }
            wanted = new List<string>(which == "all" ? All : which.Split(','));
            video = GenCommandLine.CommandLineArgPassed("m3atTestVideo");
            quit = GenCommandLine.CommandLineArgPassed("m3atTestQuit");
            string dir;
            if (!GenCommandLine.TryGetCommandLineArg("m3atTestOut", out dir) || string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "autotest");
            }
            outDir = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(outDir);
            IsoRim.Api.BeforeViewUpdate += Frame;
            string save;
            if (GenCommandLine.TryGetCommandLineArg("m3atTestLoad", out save) && !string.IsNullOrEmpty(save))
            {
                LongEventHandler.ExecuteWhenFinished(() => GameDataSaveLoader.LoadGame(save));
            }
            Log.Message(Tag + "will run " + string.Join(", ", wanted) + (video ? " (video)" : "") + " into " + outDir);
        }

        // Steps the run once a frame. A step that yields another IEnumerator (Ticks, Shot, WaitReal, a scenario) runs that
        // to its end first; one that yields anything else waits for the next frame.
        private static void Frame()
        {
            if (!started)
            {
                if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
                {
                    return;
                }
                started = true;
                stack.Push(Run());
            }
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator top = stack.Peek();
                    if (!top.MoveNext())
                    {
                        stack.Pop();
                        continue;
                    }
                    if (top.Current is IEnumerator inner)
                    {
                        stack.Push(inner);
                        continue;
                    }
                    break;
                }
            }
            catch (Exception e)
            {
                Log.Error(Tag + "failed: " + e);
                report.AppendLine().AppendLine("**The driver failed:** " + e.Message);
                stack.Clear();
                Finish();
            }
        }

        private static IEnumerator Run()
        {
            map = Find.CurrentMap;
            report.AppendLine("# M3AT autotest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")).AppendLine();
            report.AppendLine(video ? "Video mode: each scenario at normal speed, recorded." : "Stills mode: paused, one tick a frame.").AppendLine();
            probe = new StreamWriter(Path.Combine(outDir, "probe.txt"));
            if (wanted.Count == 1 && wanted[0] == "shots")
            {
                yield return Shots();
                Finish();
                if (quit)
                {
                    yield return WaitReal(1f);
                    Root.Shutdown();
                }
                yield break;
            }
            pose = IsoRim.Api.PushViewPose(PoseOwner, Pose, new IsoRim.ViewPoseOptions { Priority = 1000 });
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            middle = FindPatch();
            Log.Message(Tag + "test patch at " + middle);
            yield return WaitReal(1f);
            foreach (string name in wanted)
            {
                IEnumerator scenario = ScenarioOf(name);
                if (scenario == null)
                {
                    report.AppendLine("## " + name).AppendLine().AppendLine("No such scenario.").AppendLine();
                    continue;
                }
                report.AppendLine("## " + name).AppendLine();
                Log.Message(Tag + "scenario " + name);
                current = name;
                shotCount = 0;
                ClearPatch();
                StartRecording(name);
                IEnumerator inner = scenario;
                while (true)
                {
                    bool more;
                    try
                    {
                        more = inner.MoveNext();
                    }
                    catch (Exception e)
                    {
                        Log.Error(Tag + name + " failed: " + e);
                        report.AppendLine("- **FAILED to run:** " + e.Message);
                        break;
                    }
                    if (!more)
                    {
                        break;
                    }
                    yield return inner.Current;
                }
                StopRecording();
                Pause();
                CleanUp();
                report.AppendLine();
                File.WriteAllText(Path.Combine(outDir, "report.md"), report.ToString());
            }
            Finish();
            if (quit)
            {
                yield return WaitReal(1f);
                Root.Shutdown();
            }
        }

        // A loaded save run on, a tick a frame, a scene image every few ticks, from the freecam's eye saved with the game
        // (else the player's view as it comes back). Nothing is spawned or cleared.
        private static IEnumerator Shots()
        {
            int ticks = IntArg("m3atShotTicks", 300), every = Math.Max(1, IntArg("m3atShotEvery", 2));
            Pause();
            // The camera held where the freecam was when the game was saved, from the first frame: as it comes back on
            // load it can start looking at the ground. Freed at Finish.
            FreecamPlaces places = Current.Game.GetComponent<FreecamPlaces>();
            if (places != null && places.TryGet(map, out FreecamPlaces.Place saved))
            {
                float fov = Mathf.Clamp(SettingsMod.Settings.freecamFov, 30f, 110f);
                pose = IsoRim.Api.PushViewPose(PoseOwner, () => IsoRim.ViewPose.FromEye(saved.eye, saved.yaw, saved.pitch, fov),
                    new IsoRim.ViewPoseOptions { Priority = 1000 });
                report.AppendLine("- camera held at the saved freecam eye " + saved.eye + ", yaw " + saved.yaw + ", pitch " + saved.pitch + ", fov " + fov);
            }
            else
            {
                report.AppendLine("- no saved freecam eye for this map: the camera is the player's view as it comes back");
            }
            // Time for the freecam to come back, the 3D view to settle and hair to build.
            yield return WaitReal(8f);
            int n = 0, from = IntArg("m3atShotFrom", 0);
            for (int t = 0; t < from; t++)
            {
                Find.TickManager.DoSingleTick();
                yield return null;
            }
            for (int t = 0; t < ticks; t++)
            {
                Find.TickManager.DoSingleTick();
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    p.Drawer?.tweener?.ResetTweenedPosToRoot();
                }
                yield return null;
                if (t % every != 0)
                {
                    continue;
                }
                string file = Path.Combine(outDir, "shot_t" + (from + t).ToString("D4") + ".png");
                n++;
                imageDone = false;
                bool asked = IsoRim.Api.RequestSceneImage(tex =>
                {
                    if (tex != null)
                    {
                        File.WriteAllBytes(file, tex.EncodeToPNG());
                        UnityEngine.Object.Destroy(tex);
                    }
                    imageDone = true;
                }, new IsoRim.SceneImageOptions { IncludeOverlays = false });
                for (int i = 0; asked && i < 120 && !imageDone; i++)
                {
                    yield return null;
                }
            }
            report.AppendLine("## shots").AppendLine().AppendLine("- " + n + " pictures over " + ticks + " ticks, one every " + every);
        }

        private static int IntArg(string name, int fallback)
        {
            string v;
            return GenCommandLine.TryGetCommandLineArg(name, out v) && int.TryParse(v, out int i) ? i : fallback;
        }

        private static void Finish()
        {
            StopRecording();
            if (probe != null)
            {
                probe.Close();
                probe = null;
            }
            if (pose != null)
            {
                pose.Dispose();
                pose = null;
            }
            File.WriteAllText(Path.Combine(outDir, "report.md"), report.ToString());
            Log.Message(Tag + "done: " + Path.Combine(outDir, "report.md"));
        }

        private static IEnumerator ScenarioOf(string name)
        {
            switch (name)
            {
                case "hitflash": return HitFlashTest();
                case "beatfire": return BeatFireTest();
                case "horseshoes": return TossTest();
                case "hunker": return HunkerTest();
                case "cuddle": return CuddleTest();
                case "read": return ReadTest();
                case "visit": return VisitTest();
                case "music": return MusicTest();
                case "teach": return TeachTest();
                case "romance": return RomanceTest(null);
                case "sharedbed": return SharedBedTest();
                case "lovin": return LovinTest();
                case "kidbed": return KidBedTest();
            }
            if (name.StartsWith("romance_"))
            {
                return RomanceTest(name.Substring("romance_".Length).Split('+'));
            }
            return null;
        }

        // ---- the scenarios ----

        // Asleep under IsoRim's blanket (the bedBlankets option, turned on for the test): two in a double bed, each in its
        // own half (its own lump, IsoRim's width kept to that half), and one alone in a single bed beside it to compare.
        // Each sleeper's figure tucked in (only the head drawn, BedTuck.cs) and IsoRim drawing its blanket (DrawTuckedIn).
        private static IEnumerator SharedBedTest()
        {
            bool was = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = true;
            try
            {
                Building_Bed MakeBed(string def, IntVec3 at)
                {
                    Thing made = ThingMaker.MakeThing(ThingDef.Named(def), ThingDefOf.WoodLog);
                    made.SetFaction(Faction.OfPlayer);
                    return (Building_Bed)Spawn(made, at, Rot4.North);
                }
                Building_Bed shared = MakeBed("DoubleBed", middle);
                Building_Bed single = MakeBed("Bed", middle + new IntVec3(3, 0, 0));
                Pawn a = NewPawn(middle + new IntVec3(-2, 0, -2), null);
                Pawn b = NewPawn(middle + new IntVec3(-1, 0, -2), null);
                Pawn c = NewPawn(middle + new IntVec3(4, 0, -2), null);
                // Three builds, so each lump's size can be seen to follow its sleeper's body (WalkFigures.BlanketScaleOf).
                BodyTypeDef[] builds = { BodyTypeDefOf.Hulk, BodyTypeDefOf.Thin, BodyTypeDefOf.Fat };
                Pawn[] built = { a, b, c };
                for (int k = 0; k < 3; k++)
                {
                    if (built[k].story != null && built[k].DevelopmentalStage.Adult())
                    {
                        built[k].story.bodyType = builds[k];
                        built[k].Drawer.renderer.SetAllGraphicsDirty();
                    }
                }
                a.relations.AddDirectRelation(PawnRelationDefOf.Spouse, b);   // (else the game won't have them share it, by their ideo)
                CompAssignableToPawn assign = shared.TryGetComp<CompAssignableToPawn>();
                assign.TryAssignPawn(a);
                assign.TryAssignPawn(b);
                single.TryGetComp<CompAssignableToPawn>().TryAssignPawn(c);
                Pawn[] sleepers = { a, b, c };
                Building_Bed[] beds = { shared, shared, single };
                void Tuck()
                {
                    for (int k = 0; k < sleepers.Length; k++)
                    {
                        Pawn p = sleepers[k];
                        if (p.needs.rest != null)
                        {
                            p.needs.rest.CurLevel = 0.05f;   // (tired, so it sleeps and stays)
                        }
                        if (!p.InBed() || p.CurrentBed() != beds[k])
                        {
                            p.jobs.Notify_TuckedIntoBed(beds[k]);
                        }
                    }
                }
                Tuck();
                lookAt = shared.TrueCenter() + new Vector3(1.5f, 0f, 0f);
                lookYaw = 0f;
                yield return WaitReal(3f);
                int[] tuckedFrames = new int[3];
                int[] blanketFrames = new int[3];
                float[] starts = { float.NaN, float.NaN, float.NaN };
                for (int i = 0; i < 24; i++)
                {
                    Tuck();
                    yield return Ticks(10, "sharedbed", a);
                    lookAt = shared.TrueCenter() + new Vector3(1.5f, 0f, 0f);
                    for (int k = 0; k < 3; k++)
                    {
                        if (Field<int>(sleepers[k], "blanketFrame") >= Time.frameCount - 2)
                        {
                            blanketFrames[k]++;
                        }
                        starts[k] = Field<float>(sleepers[k], "blanketAlong");
                    }
                    if (i % 8 == 7)
                    {
                        yield return Shot("beds_t" + (i + 1) * 10);
                    }
                }
                string[] names = { "shared left", "shared right", "single" };
                for (int k = 0; k < 3; k++)
                {
                    Pawn p = sleepers[k];
                    Check(names[k] + ": in its bed, asleep", p.InBed() && p.CurrentBed() == beds[k] && !p.Awake(),
                        "at " + p.Position + ", bed " + (p.CurrentBed() != null ? p.CurrentBed().def.defName : "none") + ", awake " + p.Awake()
                        + ", posture " + p.GetPosture() + ", job " + (p.CurJobDef != null ? p.CurJobDef.defName : "none"));
                    Check(names[k] + ": blanket drawn", blanketFrames[k] >= 20, blanketFrames[k] + " of 24 looks, start " + starts[k].ToString("F2") + " cells from the head");
                }
                for (int k = 0; k < 3; k++)
                {
                    Pawn p = sleepers[k];
                    float scale = Field<float>(p, "blanketScale");
                    Check(names[k] + " (" + (p.story != null && p.story.bodyType != null ? p.story.bodyType.defName : "?") + "): blanket sized to the body",
                        !float.IsNaN(scale) && scale > 0f,
                        "scale " + scale.ToString("F2") + ", body " + Field<float>(p, "blanketDepth").ToString("F3") + " deep, "
                        + Field<float>(p, "blanketWidth").ToString("F3") + " half wide (model units)");
                }
                Check("shared: each in its own half", a.Position != b.Position, "slots " + a.Position + " and " + b.Position);
                Check("shared: both lumps start alike", !float.IsNaN(starts[0]) && !float.IsNaN(starts[1]) && Mathf.Abs(starts[0] - starts[1]) < 0.06f,
                    "starts " + starts[0].ToString("F2") + " and " + starts[1].ToString("F2") + " cells from the head");
                // A closer picture of the double bed from straight above its foot.
                lookAt = shared.TrueCenter();
                lookYaw = 0f;
                yield return Shot("double_close");
                lookYaw = 180f;
                yield return Shot("double_head");
                // From the side and higher, where the lumps' height and width over the beds show (the second picture from the foot).
                lookAt = shared.TrueCenter() + new Vector3(1.5f, 0f, 0f);
                lookYaw = 90f;
                lookPitch = 45f;
                yield return Shot("side_high");
                // Close on the single bed's sleeper's head and neck, from its side and then from over the bed's head end, where
                // the blanket's edge meets the drawn neck (SkinnedFigure.TuckFrom, WalkFigures.BlanketNeck).
                lookAt = c.DrawPos;
                lookYaw = 90f;
                lookPitch = 12f;
                lookDistance = 1.4f;
                lookHeight = 0.35f;
                yield return Shot("neck_close");
                lookDistance = LookDistance;
                lookHeight = LookHeight;
                lookPitch = LookPitch;
                Note("Look: two sleepers in the double bed, each head on its own pillow, each body under its own lump of blanket kept to its half (no lump across the middle, none overlapping the other, no body or arms poking through); the single bed's sleeper the same.");
            }
            finally
            {
                lookPitch = LookPitch;
                lookDistance = LookDistance;
                lookHeight = LookHeight;
                SettingsMod.Settings.bedBlankets = was;
            }
        }

        // Lovin under IsoRim's blanket (the bedBlankets option, on for the test): two spouses in a double bed, each head and
        // lump drawn across toward the other and the heads turned to face each other (WalkFigures.LovinOf).
        private static IEnumerator LovinTest()
        {
            bool was = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = true;
            try
            {
                Thing made = ThingMaker.MakeThing(ThingDef.Named("DoubleBed"), ThingDefOf.WoodLog);
                made.SetFaction(Faction.OfPlayer);
                Building_Bed bed = (Building_Bed)Spawn(made, middle, Rot4.North);
                Pawn a = NewPawn(middle + new IntVec3(-2, 0, -2), null);
                Pawn b = NewPawn(middle + new IntVec3(-1, 0, -2), null);
                a.relations.AddDirectRelation(PawnRelationDefOf.Spouse, b);
                CompAssignableToPawn assign = bed.TryGetComp<CompAssignableToPawn>();
                assign.TryAssignPawn(a);
                assign.TryAssignPawn(b);
                Pawn[] two = { a, b };
                void Start()
                {
                    for (int k = 0; k < 2; k++)
                    {
                        Pawn p = two[k], other = two[1 - k];
                        if (p.needs.rest != null)
                        {
                            p.needs.rest.CurLevel = 1f;   // (awake throughout)
                        }
                        if (p.CurJobDef != JobDefOf.Lovin)
                        {
                            p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Lovin, other, bed), JobCondition.InterruptForced);
                        }
                    }
                }
                Start();
                lookAt = bed.TrueCenter();
                lookYaw = 0f;
                float[] shifts = new float[2], looks = new float[2];
                for (int i = 0; i < 40; i++)
                {
                    Start();
                    yield return Ticks(10, "lovin", a);
                    lookAt = bed.TrueCenter();
                    for (int k = 0; k < 2; k++)
                    {
                        shifts[k] = Field<float>(two[k], "lovinShift");
                        looks[k] = Field<float>(two[k], "look");
                    }
                    if (i % 10 == 9)
                    {
                        yield return Shot("lovin_t" + (i + 1) * 10);
                    }
                }
                for (int k = 0; k < 2; k++)
                {
                    Pawn p = two[k];
                    Check("lover " + k + ": in the bed at lovin", p.InBed() && p.CurrentBed() == bed && p.CurJobDef == JobDefOf.Lovin,
                        "at " + p.Position + ", job " + (p.CurJobDef != null ? p.CurJobDef.defName : "none") + ", posture " + p.GetPosture());
                    Check("lover " + k + ": drawn toward the other", shifts[k] > 0.1f, "shift " + shifts[k].ToString("F2") + " cells");
                    Check("lover " + k + ": head turned", Mathf.Abs(looks[k]) > 40f, "look " + looks[k].ToString("F0") + " degrees");
                }
                Check("lovers: heads turned opposite ways", looks[0] * looks[1] < 0f, looks[0].ToString("F0") + " and " + looks[1].ToString("F0"));
                lookYaw = 180f;
                yield return Shot("lovin_head");
                lookAt = bed.TrueCenter() + new Vector3(0f, 0f, 0.5f);
                lookYaw = 0f;
                lookPitch = 30f;
                lookDistance = 1.8f;
                lookHeight = 0.4f;
                yield return Shot("lovin_close");
                Note("Look: the two heads close together on the pillows, turned to face each other, each lump of blanket moved with its head, no body poking through.");
            }
            finally
            {
                lookPitch = LookPitch;
                lookDistance = LookDistance;
                lookHeight = LookHeight;
                SettingsMod.Settings.bedBlankets = was;
            }
        }

        // Children and a baby asleep under IsoRim's blanket (the bedBlankets option, on for the test), each alone in a single
        // bed, beside a grown sleeper to compare: the lump's size (BlanketScaleOf, measured from the body in its shape) and where it starts
        // (the neck, BlanketNeck), and where the head lies against the pillow.
        private static IEnumerator KidBedTest()
        {
            bool was = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = true;
            try
            {
                float[] ages = { 25f, 13f, 8f, 4f, 1f };
                string[] names = { "grown 25", "teen 13", "child 8", "child 4", "baby 1" };
                int n = ages.Length;
                Pawn[] sleepers = new Pawn[n];
                Building_Bed[] beds = new Building_Bed[n];
                for (int k = 0; k < n; k++)
                {
                    Thing made = ThingMaker.MakeThing(ThingDef.Named("Bed"), ThingDefOf.WoodLog);
                    made.SetFaction(Faction.OfPlayer);
                    beds[k] = (Building_Bed)Spawn(made, middle + new IntVec3(2 * k - 4, 0, 0), Rot4.North);
                    DevelopmentalStage stage = ages[k] < 3f ? DevelopmentalStage.Baby : ages[k] < 13f ? DevelopmentalStage.Child : DevelopmentalStage.Adult;
                    PawnGenerationRequest request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                        fixedBiologicalAge: ages[k], fixedChronologicalAge: ages[k], developmentalStages: stage);
                    Pawn pawn = PawnGenerator.GeneratePawn(request);
                    pawn.equipment.DestroyAllEquipment();
                    Spawn(pawn, middle + new IntVec3(2 * k - 4, 0, -3));
                    beds[k].TryGetComp<CompAssignableToPawn>().TryAssignPawn(pawn);
                    sleepers[k] = pawn;
                }
                void Tuck()
                {
                    for (int k = 0; k < n; k++)
                    {
                        Pawn p = sleepers[k];
                        if (p.needs.rest != null)
                        {
                            p.needs.rest.CurLevel = 0.05f;
                        }
                        if (!p.InBed() || p.CurrentBed() != beds[k])
                        {
                            p.jobs.Notify_TuckedIntoBed(beds[k]);
                        }
                    }
                }
                Tuck();
                lookAt = middle.ToVector3Shifted();
                lookYaw = 0f;
                yield return WaitReal(3f);
                int[] blanketFrames = new int[n];
                for (int i = 0; i < 16; i++)
                {
                    Tuck();
                    yield return Ticks(10, "kidbed", sleepers[2]);
                    lookAt = middle.ToVector3Shifted();
                    for (int k = 0; k < n; k++)
                    {
                        if (Field<int>(sleepers[k], "blanketFrame") >= Time.frameCount - 2)
                        {
                            blanketFrames[k]++;
                        }
                    }
                }
                yield return Shot("row_foot");
                lookYaw = 180f;
                yield return Shot("row_head");
                for (int k = 0; k < n; k++)
                {
                    Pawn p = sleepers[k];
                    Building_Bed bed = beds[k];
                    IntVec3 slot = BedUtility.GetSleepingSlotPos(0, bed.Position, bed.Rotation, bed.def.size);
                    Vector3 toFeet = -Field<Vector3>(p, "lyingAlong");   // (from the head toward the hips, as drawn)
                    Vector3 headEdge = bed.TrueCenter() - toFeet * (bed.def.size.z * 0.5f);
                    Vector3[] joints = Field<Vector3[]>(p, "lyingJoints");
                    float size = Field<float>(p, "lyingSize");
                    float start = Field<float>(p, "blanketAlong");
                    string head = "none";
                    if (joints != null && joints.Length > 3)
                    {
                        Vector3 h = joints[3];   // spine.006, the head bone's joint (LyingBones)
                        Vector3 hips = joints[0];
                        head = "head joint " + Vector3.Dot(h - headEdge, toFeet).ToString("F2") + " cells from the bed's head end, "
                            + (h.x - slot.ToVector3Shifted().x).ToString("F2") + " across, hips " + Vector3.Dot(hips - headEdge, toFeet).ToString("F2")
                            + ", height " + h.y.ToString("F2");
                    }
                    Check(names[k] + ": in its bed, asleep", p.InBed() && p.CurrentBed() == bed && !p.Awake(),
                        "posture " + p.GetPosture() + ", job " + (p.CurJobDef != null ? p.CurJobDef.defName : "none") + ", awake " + p.Awake());
                    Check(names[k] + ": blanket drawn", blanketFrames[k] >= 12,
                        blanketFrames[k] + " of 16 looks; size " + size.ToString("F3") + ", blanket scale " + Field<float>(p, "blanketScale").ToString("F2") + ", neck " + Field<float>(p, "tuckNeck").ToString("F2") + " cells down the bed, moved " + Field<float>(p, "tuckShift").ToString("F2") + ", lump start " + start.ToString("F2")
                        + " cells from the head's middle; " + head);
                    lookAt = bed.TrueCenter();
                    lookYaw = 90f;
                    yield return Shot("side_" + k);
                    lookYaw = 0f;
                    yield return Shot("foot_" + k);
                }
                Note("Look: each sleeper's head on its pillow (not floating over it, sunk into it or down the bed), the lump starting at its neck with no gap and no body poking through, the lump's size in step with the sleeper (a baby's small, not a grown body's), and the bed's foot flat past the feet.");
            }
            finally
            {
                SettingsMod.Settings.bedBlankets = was;
            }
        }

        // A lesson at a school desk (Biotech's Lessongiving and Lessontaking), the teacher and a child pupil side by side
        // at its two spots, standing and then each on a dining chair: the teacher gesturing (teach, through the eating
        // slot), its head turned toward the pupil and the pupil's back toward it (Ease.look, LessonLookOf).
        private static IEnumerator TeachTest()
        {
            ThingDef deskDef = DefDatabase<ThingDef>.GetNamedSilentFail("SchoolDesk");
            if (deskDef == null || !ModsConfig.BiotechActive)
            {
                Check("teach", false, "no SchoolDesk (Biotech off?)");
                yield break;
            }
            foreach (string where in new[] { "standing", "seated" })
            {
                ClearPatch();
                Thing desk = ThingMaker.MakeThing(deskDef, deskDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
                desk.SetFaction(Faction.OfPlayer);
                desk = Spawn(desk, middle, Rot4.North);
                IntVec3 pupilAt = SchoolUtility.DeskSpotStudent(desk), teacherAt = SchoolUtility.DeskSpotTeacher(desk);
                if (where == "seated")
                {
                    foreach (IntVec3 c in new[] { pupilAt, teacherAt })
                    {
                        Thing chair = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                        chair.SetFaction(Faction.OfPlayer);
                        Spawn(chair, c, Rot4.North);
                    }
                }
                Pawn teacher = NewPawn(teacherAt, null);
                Pawn pupil = NewChild(pupilAt);
                // As the game does it: the pupil sat down to wait with no teacher, then the teacher set on its job as the
                // teacher is given the lesson (WorkGiver_Teach), as each job fails while the other isn't on its own.
                Job taking = JobMaker.MakeJob(JobDefOf.Lessontaking, desk);
                taking.playerForced = true;
                pupil.jobs.StartJob(taking, JobCondition.InterruptForced);
                pupil.CurJob.SetTarget(TargetIndex.B, teacher);
                Job giving = JobMaker.MakeJob(JobDefOf.Lessongiving, desk, pupil);
                giving.playerForced = true;
                teacher.jobs.StartJob(giving, JobCondition.InterruptForced);
                Look(teacher, 210f);
                float most = 0f, teacherLook = 0f, pupilLook = 0f;
                bool lasted = true;
                for (int i = 0; i < 48; i++)
                {
                    KeepLearning(teacher, pupil);
                    yield return Ticks(15, "teach_" + where, teacher);
                    if (teacher.CurJobDef != JobDefOf.Lessongiving || pupil.CurJobDef != JobDefOf.Lessontaking)
                    {
                        lasted = false;
                        break;
                    }
                    if (Field<string>(teacher, "eatClip") == "teach")
                    {
                        most = Mathf.Max(most, Field<float>(teacher, "eat"));
                    }
                    float tl = Field<float>(teacher, "look"), pl = Field<float>(pupil, "look");
                    teacherLook = Mathf.Abs(tl) > Mathf.Abs(teacherLook) ? tl : teacherLook;
                    pupilLook = Mathf.Abs(pl) > Mathf.Abs(pupilLook) ? pl : pupilLook;
                    if (i % 6 == 5)
                    {
                        yield return Shot(where + "_t" + (i + 1) * 15);
                    }
                }
                string jobs = "teacher " + (teacher.CurJobDef != null ? teacher.CurJobDef.defName : "none") + ", pupil "
                    + (pupil.CurJobDef != null ? pupil.CurJobDef.defName : "none") + ", postures " + teacher.GetPosture() + "/" + pupil.GetPosture();
                Check(where + ": the lesson goes on", lasted, jobs);
                Check(where + ": teaching", most >= 0.99f, "teach weight " + most.ToString("F2") + ", " + jobs);
                Check(where + ": the teacher looks at the pupil", Mathf.Abs(teacherLook) > 20f, "teacher's look " + teacherLook.ToString("F1") + " degrees");
                Check(where + ": the pupil looks back", Mathf.Abs(pupilLook) > 10f && Mathf.Sign(pupilLook) != Mathf.Sign(teacherLook),
                    "pupil's look " + pupilLook.ToString("F1") + " degrees, the teacher's " + teacherLook.ToString("F1"));
                foreach (Pawn p in new[] { teacher, pupil })
                {
                    if (p.jobs != null)
                    {
                        p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                }
            }
            Note("Look: the teacher at the desk's left spot, explaining with the hands (palm up, beating a point, opened out to the pupil, both spread, patting the desk), its head turned part way toward the child on its right, which looks part way back; seated the same over the sit.");
        }

        // Kept from ending the lesson for a need (the game ends it when a basic need comes up, or the child has learnt enough).
        private static void KeepLearning(Pawn teacher, Pawn pupil)
        {
            foreach (Pawn p in new[] { teacher, pupil })
            {
                if (p.needs.food != null)
                {
                    p.needs.food.CurLevel = p.needs.food.MaxLevel;
                }
                if (p.needs.rest != null)
                {
                    p.needs.rest.CurLevel = 1f;
                }
            }
            if (pupil.needs.learning != null)
            {
                pupil.needs.learning.CurLevel = 0f;
            }
        }

        // A new colonist child (about nine) standing at a cell.
        private static Pawn NewChild(IntVec3 at)
        {
            PawnGenerationRequest request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                fixedBiologicalAge: 9f, fixedChronologicalAge: 9f, developmentalStages: DevelopmentalStage.Child);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.equipment.DestroyAllEquipment();
            Spawn(pawn, at);
            pawn.Rotation = Rot4.North;
            return pawn;
        }

        // Playing music: the piano and harpsichord (at the keys), the harp and the drum, stood at each and sat on a chair
        // put at it, each drawn stepped up to the instrument (WalkFigures.InstrumentCloser).
        private static IEnumerator MusicTest()
        {
            JobDef play = DefDatabase<JobDef>.GetNamedSilentFail("Play_MusicalInstrument");
            string[] instruments = { "Piano", "Harpsichord", "Harp", "Drum" };
            string[] wantKind = { "piano", "piano", "harp", "drum" };
            for (int n = 0; n < instruments.Length; n++)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(instruments[n]);
                if (play == null || def == null)
                {
                    Check(instruments[n], false, play == null ? "no Play_MusicalInstrument job" : "no such def (DLC off?)");
                    continue;
                }
                foreach (string where in new[] { "standing", "seated" })
                {
                    ClearPatch();
                    Thing instrument = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    instrument.SetFaction(Faction.OfPlayer);
                    instrument = Spawn(instrument, middle, Rot4.South);
                    IntVec3 cell = instrument.InteractionCell;
                    Pawn pawn = NewPawn(cell, null);
                    if (pawn.needs.joy != null)
                    {
                        pawn.needs.joy.CurLevel = 0f;
                    }
                    Job job = JobMaker.MakeJob(play, instrument, cell);
                    if (where == "seated")
                    {
                        Thing chair = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                        chair.SetFaction(Faction.OfPlayer);
                        // (facing the instrument: the keyboards' cell is north of them, the harp's and drum's south)
                        Spawn(chair, cell, cell.z > middle.z ? Rot4.South : Rot4.North);
                    }
                    job.playerForced = true;
                    job.ignoreJoyTimeAssignment = true;
                    pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                    Look(pawn, cell.z > middle.z ? 150f : 330f);
                    yield return WaitReal(3f);
                    string want = wantKind[n] + (where == "seated" ? "_sit" : "");
                    string kind = null;
                    float shown = 0f;
                    for (int i = 0; i < 12; i++)
                    {
                        yield return Ticks(15, "music_" + instruments[n] + "_" + where, pawn);
                        string k = Field<string>(pawn, "workKind");
                        if (k != null && Field<float>(pawn, "work") > 0.5f)
                        {
                            kind = k;
                        }
                        shown = Mathf.Max(shown, Field<float>(pawn, "shownWork"));
                        if (i % 6 == 5)
                        {
                            yield return Shot(instruments[n] + "_" + where + "_t" + (i + 1) * 15);
                        }
                    }
                    Check(instruments[n] + " " + where + ": " + want, kind == want && shown > 0.5f, "work kind " + (kind ?? "none") + ", shown " + shown.ToString("F2"));
                    if (pawn.jobs != null)
                    {
                        pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                }
            }
            Note("Look: stepped up to each instrument, the hands on the keys, the palms toward the harp's strings, beating the drum's head; seated on the chair, leant in.");
        }

        // The red flash of a hit: the figure is flashed by HitFlash.Of, 16 game ticks from full to none, the outlines never.
        private static IEnumerator HitFlashTest()
        {
            Pawn pawn = NewPawn(middle, null);
            Hold(pawn);
            Look(pawn, 0f);
            yield return WaitReal(3f);   // its hair built
            yield return Shot("before");
            pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 1f));
            float first = HitFlash.Of(pawn, false);
            Probe("hitflash", pawn);
            yield return Shot("hit_0");
            yield return Ticks(4, "hitflash", pawn);
            float mid = HitFlash.Of(pawn, false);
            yield return Shot("hit_4");
            yield return Ticks(6, "hitflash", pawn);
            yield return Shot("hit_10");
            yield return Ticks(10, "hitflash", pawn);
            float last = HitFlash.Of(pawn, false);
            yield return Shot("hit_20");
            Check("flash full on the hit", first > 0.8f, "HitFlash.Of " + first.ToString("F2"));
            Check("flash fading", mid > 0f && mid < first, "after 4 ticks " + mid.ToString("F2"));
            Check("flash gone after 20 ticks", last <= 0.001f, "after 20 ticks " + last.ToString("F2"));
            Note("Look: the figure is tinted red in hit_0, less in hit_4, none in hit_20; the outline never red.");
        }

        // Beating out fires: on an item on the floor (stomp), on tall furniture (pat), on a person standing (pat) and on
        // a person lying (crouch).
        private static IEnumerator BeatFireTest()
        {
            string[] cases = { "floor", "furniture", "standing", "lying" };
            string[] wantKind = { "stomp", "pat", "pat", "crouch" };
            for (int c = 0; c < cases.Length; c++)
            {
                ClearPatch();
                Pawn beater = NewPawn(middle + new IntVec3(-1, 0, 0), null);
                Fire fire = null;
                IntVec3 at = middle + new IntVec3(1, 0, 0);
                if (cases[c] == "floor")
                {
                    Thing logs = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    logs.stackCount = 40;
                    Spawn(logs, at);
                    fire = StartFire(at);
                }
                else if (cases[c] == "furniture")
                {
                    ThingDef shelf = DefDatabase<ThingDef>.GetNamed("Shelf");
                    Thing built = ThingMaker.MakeThing(shelf, ThingDefOf.WoodLog);
                    built.SetFaction(Faction.OfPlayer);
                    Spawn(built, at, Rot4.North);
                    fire = StartFire(at);
                }
                else
                {
                    Pawn burning = NewPawn(at, null);
                    if (cases[c] == "lying")
                    {
                        HealthUtility.AdjustSeverity(burning, HediffDefOf.Anesthetic, 1f);
                    }
                    else
                    {
                        Hold(burning);
                    }
                    burning.TryAttachFire(0.6f, null);
                    fire = burning.GetAttachment(ThingDefOf.Fire) as Fire;
                }
                Look(beater, 0f);
                yield return WaitReal(3f);
                if (fire == null || !fire.Spawned)
                {
                    Check(cases[c] + ": fire started", false, "no fire");
                    continue;
                }
                fire.fireSize = 1f;
                beater.jobs.StartJob(JobMaker.MakeJob(JobDefOf.BeatFire, fire), JobCondition.InterruptForced);
                string kind = null;
                int beats = 0;
                for (int i = 0; i < 16; i++)
                {
                    yield return Ticks(10, "beatfire_" + cases[c], beater);
                    string k = Field<string>(beater, "workKind");
                    if (k != null && Field<float>(beater, "work") > 0.5f)
                    {
                        kind = k;
                    }
                    beats = Math.Max(beats, Field<int>(beater, "beatCount"));
                    if (i % 4 == 3)
                    {
                        yield return Shot(cases[c] + "_t" + (i + 1) * 10);
                    }
                    if (!fire.Spawned)
                    {
                        break;
                    }
                }
                Check(cases[c] + ": " + wantKind[c], kind == wantKind[c], "work kind " + (kind ?? "none"));
                if (wantKind[c] != "crouch")   // the crouch is ground work on the clock, not beaten
                {
                    // On a person, a pat or two puts it out.
                    int least = cases[c] == "standing" ? 1 : 2;
                    Check(cases[c] + ": beats counted", beats >= least, beats + " beats");
                }
            }
            Note("Look: one blow a beat, the stomping foot or patting palm landing on the fire.");
        }

        // Horseshoes and hoopstone: the toss timed so the thing leaves the hand on the game's throw tick.
        private static IEnumerator TossTest()
        {
            foreach (string game in new[] { "HorseshoesPin", "HoopstoneRing" })
            {
                ClearPatch();
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(game);
                if (def == null)
                {
                    Check(game, false, "no such def");
                    continue;
                }
                Thing pin = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                pin.SetFaction(Faction.OfPlayer);
                Spawn(pin, middle + new IntVec3(0, 0, 3), Rot4.North);
                Pawn pawn = NewPawn(middle, null);
                IntVec3 spot;
                Building chair;
                if (!WatchBuildingUtility.TryFindBestWatchCell(pin, pawn, false, out spot, out chair))
                {
                    Check(game + ": a spot to throw from", false, "none found");
                    continue;
                }
                pawn.Position = spot;
                pawn.Notify_Teleported();
                if (pawn.needs.joy != null)
                {
                    pawn.needs.joy.CurLevel = 0f;
                }
                JobDef jobDef = DefDatabase<JobDef>.GetNamed(game == "HorseshoesPin" ? "Play_Horseshoes" : "Play_Hoopstone");
                Job job = JobMaker.MakeJob(jobDef, pin, spot);
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                // Side on to the throw, then from behind the thrower.
                IntVec3 toPin = pin.Position - spot;
                Look(pawn, Mathf.Atan2(toPin.x, toPin.z) * Mathf.Rad2Deg - 90f);
                yield return WaitReal(3f);
                float most = 0f;
                int shots = 0;
                float lastAt = -1f;
                for (int i = 0; i < 440; i++)
                {
                    yield return Ticks(1, game, pawn);
                    most = Mathf.Max(most, Field<float>(pawn, "toss"));
                    float at = Field<float>(pawn, "tossAt");
                    if (at >= 0f && shots < 6 && (lastAt < 0f || at - lastAt >= 0.15f || (lastAt < 0.5f && at >= 0.5f)))
                    {
                        lastAt = at;
                        shots++;
                        yield return Shot(game + "_toss_" + at.ToString("F2"));
                    }
                }
                Check(game + ": tossing", most > 0.9f, "toss weight reached " + most.ToString("F2"));
                Check(game + ": a throw played", shots > 0, shots + " pictures during a throw");
            }
            Note("Look: the underhand pitch, the step onto the left foot, the hand opening as the game's horseshoe or stone leaves.");
        }

        // Reading a book (the game's Reading job): standing in the open, then sat on a chair at a table. The read clip is
        // laid over the idle or the sit (Ease.eatClip "read") and the open book drawn on the left hand; one loop is
        // READ_SECONDS (8 s), with a page turned over 0.55 .. 0.85 of it.
        private static IEnumerator ReadTest()
        {
            foreach (bool seated in new[] { false, true })
            {
                ClearPatch();
                string name = seated ? "seated" : "standing";
                Pawn pawn = NewPawn(middle, null);
                if (seated)
                {
                    ThingDef chair = DefDatabase<ThingDef>.GetNamed("DiningChair");
                    ThingDef table = DefDatabase<ThingDef>.GetNamed("Table1x2c");
                    Thing c = ThingMaker.MakeThing(chair, ThingDefOf.WoodLog);
                    c.SetFaction(Faction.OfPlayer);
                    Spawn(c, middle + new IntVec3(1, 0, 0), Rot4.North);
                    Thing t = ThingMaker.MakeThing(table, ThingDefOf.WoodLog);
                    t.SetFaction(Faction.OfPlayer);
                    Spawn(t, middle + new IntVec3(1, 0, 1), Rot4.North);
                }
                Thing book = Spawn(BookUtility.MakeBook(ThingDefOf.Novel, ArtGenerationContext.Colony), middle + new IntVec3(-1, 0, 0));
                if (pawn.needs.joy != null)
                {
                    pawn.needs.joy.CurLevel = 0.1f;
                }
                Job job = JobMaker.MakeJob(JobDefOf.Reading, book);
                job.playerForced = true;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                float most = 0f;
                for (int i = 0; i < 60 && most < 0.99f; i++)
                {
                    yield return Ticks(10, "read_" + name, pawn);
                    if (Field<string>(pawn, "eatClip") == "read")
                    {
                        most = Mathf.Max(most, Field<float>(pawn, "eat"));
                    }
                }
                Look(pawn, 0f);
                yield return WaitReal(2f);
                Check(name + ": reading", most >= 0.99f, "read weight " + most.ToString("F2") + ", job " + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none")
                    + ", posture " + pawn.GetPosture() + ", book open " + ((book as Book) != null && ((Book)book).IsOpen));
                Building under = pawn.Position.GetEdifice(map);
                Note(name + ": pawn at " + pawn.Position + " (patch middle " + middle + "), on " + (under != null ? under.def.defName : "nothing")
                    + ", target B " + pawn.CurJob?.targetB + ", facing " + pawn.Rotation.ToStringHuman());
                for (int i = 0; i < 8; i++)
                {
                    yield return Shot(name + "_" + i);
                    yield return Ticks(60, "read_" + name, pawn);
                }
            }
            Note("Look: the open book on the left palm under the chin, tipped up to the face, the head bowed to it; the right hand on the right page, once a loop lifted over to the left and back. Seated the same over the sit.");
        }

        // Visiting the sick (VisitSickPawn, which the game allows only for a patient awake in a bed): by one on a sleeping
        // spot sat cross-legged beside them talking (work_visit), by one in a bed stood talking (talk over the idle).
        private static IEnumerator VisitTest()
        {
            foreach (string where in new[] { "spot", "bed" })
            {
                ClearPatch();
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamed(where == "spot" ? "SleepingSpot" : "Bed");
                Thing made = ThingMaker.MakeThing(bedDef, bedDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
                made.SetFaction(Faction.OfPlayer);
                Building_Bed bed = (Building_Bed)Spawn(made, middle + new IntVec3(1, 0, 0), Rot4.North);
                Pawn patient = NewPawn(middle + new IntVec3(1, 0, -1), null);
                Pawn visitor = NewPawn(middle + new IntVec3(-2, 0, 0), null);
                if (visitor.needs.joy != null)
                {
                    visitor.needs.joy.CurLevel = 0f;
                }
                if (patient.needs.joy != null)
                {
                    patient.needs.joy.CurLevel = 0f;
                }
                patient.jobs.Notify_TuckedIntoBed(bed);
                Look(patient, 0f);
                yield return WaitReal(3f);
                string kind = null;
                float work = 0f, talk = 0f;
                bool visited = false, placed = false;
                float standOff = 0f;   // cells: the furthest the talking visitor stood from the bed's edge
                HashSet<string> standCells = new HashSet<string>();
                float nearest = float.MaxValue;   // cells from where the sat visitor is drawn to the patient's nearest joint
                for (int i = 0; i < 36; i++)
                {
                    if (patient.needs.rest != null)
                    {
                        patient.needs.rest.CurLevel = 1f;   // (kept awake, or the visit ends)
                    }
                    if (!patient.InBed())
                    {
                        patient.jobs.Notify_TuckedIntoBed(bed);
                    }
                    if (visitor.CurJobDef != JobDefOf.VisitSickPawn)   // (the game ends a visit soon; begun again)
                    {
                        Job job = JobMaker.MakeJob(JobDefOf.VisitSickPawn, patient);
                        job.playerForced = true;
                        job.ignoreJoyTimeAssignment = true;
                        visitor.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    Vector3? drawnAt = Field<Vector3?>(visitor, "workBeside");
                    lookAt = drawnAt ?? visitor.DrawPos;
                    yield return Ticks(15, "visit_" + where, visitor);
                    if (visitor.CurJobDef == JobDefOf.VisitSickPawn && (visitor.pather == null || !visitor.pather.Moving))
                    {
                        visited = true;
                    }
                    string k = Field<string>(visitor, "workKind");
                    float w = Field<float>(visitor, "work");
                    if (k != null && w > 0.5f)
                    {
                        kind = k;
                    }
                    work = Mathf.Max(work, k == "visit" ? w : 0f);
                    if (visitor.CurJobDef == JobDefOf.VisitSickPawn && visitor.jobs.curDriver != null && visitor.jobs.curDriver.CurToilIndex >= 1
                        && (visitor.pather == null || !visitor.pather.Moving))
                    {
                        // (how far from the bed it stands talking: its drawn place to the nearest point of the bed's cells)
                        CellRect r = bed.OccupiedRect();
                        Vector3 v = visitor.DrawPos;
                        float dx = Mathf.Max(r.minX - v.x, 0f, v.x - (r.maxX + 1));
                        float dz = Mathf.Max(r.minZ - v.z, 0f, v.z - (r.maxZ + 1));
                        float off = Mathf.Sqrt(dx * dx + dz * dz);
                        standOff = Mathf.Max(standOff, off);
                        standCells.Add(visitor.Position + " (bed " + r.minX + ".." + r.maxX + ", " + r.minZ + ".." + r.maxZ + ")");
                    }
                    Vector3? beside = Field<Vector3?>(visitor, "workBeside");
                    Vector3[] joints = Field<Vector3[]>(patient, "lyingJoints");
                    if (k == "visit" && w > 0.9f && beside.HasValue && joints != null)
                    {
                        placed = true;
                        foreach (Vector3 j in joints)
                        {
                            Vector3 d = j - beside.Value;
                            d.y = 0f;
                            nearest = Mathf.Min(nearest, d.magnitude);
                        }
                    }
                    talk = Mathf.Max(talk, Field<float>(visitor, "talk"));
                    if (i % 6 == 5)
                    {
                        yield return Shot(where + "_t" + (i + 1) * 15);
                    }
                }
                Check(where + ": visiting", visited, "job " + (visitor.CurJobDef != null ? visitor.CurJobDef.defName : "none") + ", patient in bed " + patient.InBed() + ", awake " + patient.Awake());
                if (where == "spot")
                {
                    Check("spot: work_visit", kind == "visit" && work > 0.9f, "work kind " + (kind ?? "none") + ", weight " + work.ToString("F2"));
                    Check("spot: sat beside, off the patient", placed && nearest > 0.45f && nearest < 1f, placed ? "hips " + nearest.ToString("F2") + " cells from the patient's nearest joint" : "not placed beside (KneelBeside)");
                }
                else
                {
                    Check("bed: stood talking", kind == null && talk > 0.9f, "work kind " + (kind ?? "none") + ", talk " + talk.ToString("F2"));
                    Check("bed: stood by the bed", standOff < 0.6f, "at most " + standOff.ToString("F2") + " cells off the bed's edge, at " + string.Join(", ", standCells));
                }
                if (visitor.jobs != null)
                {
                    visitor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
            Note("Look: by the sleeping spot sat cross-legged beside the patient, upright, talking with the hands toward them; by the bed stood beside it, talking.");
        }

        // Hunkering under fire (Combat Extended's HunkerDown) and stop, drop and roll (ExtinguishSelf).
        private static IEnumerator HunkerTest()
        {
            Pawn pawn = NewPawn(middle, "Gun_AssaultRifle");
            Look(pawn, 0f);
            yield return WaitReal(3f);
            JobDef hunker = DefDatabase<JobDef>.GetNamedSilentFail("HunkerDown");
            if (hunker == null)
            {
                Check("hunker: Combat Extended loaded", false, "no HunkerDown job");
            }
            else
            {
                Suppress(pawn, middle + new IntVec3(0, 0, 10));
                yield return Ticks(5, "hunker", pawn);
                if (pawn.CurJobDef != hunker)
                {
                    pawn.jobs.StartJob(JobMaker.MakeJob(hunker, pawn), JobCondition.InterruptForced);
                    Note("CE did not hunker the pawn by itself; the job was started by hand.");
                }
                float most = 0f;
                string loop = null;
                for (int i = 0; i < 12; i++)
                {
                    Suppress(pawn, middle + new IntVec3(0, 0, 10));
                    yield return Ticks(10, "hunker", pawn);
                    most = Mathf.Max(most, Field<float>(pawn, "duck"));
                    loop = Field<string>(pawn, "duckLoop") ?? loop;
                    if (i % 3 == 2)
                    {
                        yield return Shot("hunker_t" + (i + 1) * 10);
                    }
                }
                Check("hunker: down", most >= 0.99f, "duck reached " + most.ToString("F2"));
                Check("hunker: duck loop", loop != null && loop.StartsWith("duck_"), "loop " + (loop ?? "none"));
            }
            ClearPatch();
            Pawn burning = NewPawn(middle, null);
            Look(burning, 0f);
            yield return WaitReal(3f);
            burning.TryAttachFire(0.4f, null);
            Fire fire = burning.GetAttachment(ThingDefOf.Fire) as Fire;
            if (fire == null)
            {
                Check("roll: fire on the pawn", false, "no fire");
                yield break;
            }
            burning.jobs.StartJob(JobMaker.MakeJob(JobDefOf.ExtinguishSelf, fire), JobCondition.InterruptForced);
            float down = 0f;
            string rollLoop = null;
            for (int i = 0; i < 18; i++)
            {
                yield return Ticks(10, "roll", burning);
                down = Mathf.Max(down, Field<float>(burning, "duck"));
                rollLoop = Field<string>(burning, "duckLoop") ?? rollLoop;
                if (i % 3 == 2)
                {
                    yield return Shot("roll_t" + (i + 1) * 10);
                }
            }
            Check("roll: down", down >= 0.99f, "duck reached " + down.ToString("F2"));
            Check("roll: rolling", rollLoop == "roll", "loop " + (rollLoop ?? "none"));
            Note("Look: hunkered flat on the front, face down, facing the suppressing fire (north); the roll over each side.");
        }

        // Romance on the Rim's dates (Workshop 2654432921, telardo.RomanceOnTheRim), each started through its own lord
        // job by reflection, so the mod is not referenced, and with none of the conditions its workers wait for (the
        // weather, the romance need, idle partners): a kiss, skygazing, a musical performance, pillow talk, a snowball
        // fight and shared reading. A survey of how each is drawn now; it checks only that the date got going.
        private static IEnumerator RomanceTest(string[] only)
        {
            if (GenTypes.GetTypeInAnyAssembly("RomanceOnTheRim.LordJob_Kiss") == null)
            {
                Check("romance: the mod loaded", false, "no RomanceOnTheRim.LordJob_Kiss");
                yield break;
            }
            foreach (string date in only ?? new[] { "kiss", "skygaze", "music", "pillow", "snowball", "shareread" })
            {
                ClearPatch();
                IEnumerator run = RomanceDate(date);
                while (true)
                {
                    bool more;
                    try
                    {
                        more = run.MoveNext();
                    }
                    catch (Exception e)
                    {
                        Log.Error(Tag + "romance " + date + " failed: " + e);
                        Check(date + ": ran", false, e.GetType().Name + ": " + e.Message);
                        break;
                    }
                    if (!more)
                    {
                        break;
                    }
                    yield return run.Current;
                }
                foreach (Lord lord in new List<Lord>(map.lordManager.lords))
                {
                    if (lord.LordJob != null && lord.LordJob.GetType().Namespace == "RomanceOnTheRim")
                    {
                        map.lordManager.RemoveLord(lord);
                    }
                }
                CleanUp();
            }
            Note("Look: what each figure does in each date; the romance mod moves only the sprite's head and body (AnimationDefs), which the chibi doesn't take.");
        }

        private static IEnumerator RomanceDate(string date)
        {
            Pawn a = NewPawn(middle + new IntVec3(-1, 0, 0), null);
            Pawn b = NewPawn(middle + new IntVec3(1, 0, 0), null);
            foreach (Pawn p in new[] { a, b })
            {
                if (p.needs.joy != null)
                {
                    p.needs.joy.CurLevel = 0.2f;
                }
                if (p.needs.rest != null)
                {
                    p.needs.rest.CurLevel = 0.9f;
                }
            }
            if (!a.relations.DirectRelationExists(PawnRelationDefOf.Lover, b))
            {
                a.relations.AddDirectRelation(PawnRelationDefOf.Lover, b);
            }
            Hold(a);
            Hold(b);
            object lordJob = null;
            Pawn watch = a;
            float yaw = 0f;
            Thing book = null;
            switch (date)
            {
                case "kiss":
                    b.Position = middle + new IntVec3(2, 0, 0);
                    b.Notify_Teleported();
                    lordJob = MakeLordJob("LordJob_Kiss", a, b, Enum.ToObject(GenTypes.GetTypeInAnyAssembly("RomanceOnTheRim.KissUtility").GetNestedType("KissType"), 1), 3f);
                    break;
                case "skygaze":
                    lordJob = MakeLordJob("LordJob_SkygazeTogether", a, b, middle, middle + new IntVec3(1, 0, 0));
                    yaw = 330f;
                    break;
                case "music":
                {
                    Thing piano = ThingMaker.MakeThing(ThingDef.Named("Piano"), GenStuff.DefaultStuffFor(ThingDef.Named("Piano")));
                    piano.SetFaction(Faction.OfPlayer);
                    piano = Spawn(piano, middle + new IntVec3(0, 0, 2), Rot4.South);
                    lordJob = MakeLordJob("LordJob_MusicalPerformance", a, b, piano);
                    yaw = 150f;
                    break;
                }
                case "pillow":
                {
                    ThingDef bedDef = ThingDef.Named("DoubleBed");
                    Thing bed = ThingMaker.MakeThing(bedDef, ThingDefOf.WoodLog);
                    bed.SetFaction(Faction.OfPlayer);
                    bed = Spawn(bed, middle + new IntVec3(0, 0, 1), Rot4.South);
                    CompAssignableToPawn assign = bed.TryGetComp<CompAssignableToPawn>();
                    assign.TryAssignPawn(a);
                    assign.TryAssignPawn(b);
                    lordJob = MakeLordJob("LordJob_PillowTalk", a, b, bed);
                    yaw = 330f;
                    break;
                }
                case "snowball":
                    a.Position = middle + new IntVec3(-2, 0, 0);
                    a.Notify_Teleported();
                    b.Position = middle + new IntVec3(2, 0, 0);
                    b.Notify_Teleported();
                    lordJob = MakeLordJob("LordJob_SnowballFight", a, b, middle + new IntVec3(-2, 0, 0), middle + new IntVec3(2, 0, 0));
                    yaw = 0f;
                    break;
                case "shareread":
                {
                    // b reads seated at a table; a comes to stare at it, then sits on the chair beside it.
                    ThingDef chairDef = ThingDef.Named("DiningChair");
                    Thing table = ThingMaker.MakeThing(ThingDef.Named("Table2x2c"), ThingDefOf.WoodLog);
                    table.SetFaction(Faction.OfPlayer);
                    Spawn(table, middle + new IntVec3(0, 0, 1), Rot4.North);
                    foreach (int x in new[] { 0, 1 })
                    {
                        Thing chair = ThingMaker.MakeThing(chairDef, ThingDefOf.WoodLog);
                        chair.SetFaction(Faction.OfPlayer);
                        Spawn(chair, middle + new IntVec3(x, 0, 0), Rot4.North);
                    }
                    b.Position = middle;
                    b.Notify_Teleported();
                    a.Position = middle + new IntVec3(-3, 0, -1);
                    a.Notify_Teleported();
                    book = Spawn(BookUtility.MakeBook(ThingDefOf.Novel, ArtGenerationContext.Colony), middle + new IntVec3(-1, 0, -1));
                    Job read = JobMaker.MakeJob(JobDefOf.Reading, book, middle);
                    read.playerForced = true;
                    b.jobs.StartJob(read, JobCondition.InterruptForced);
                    for (int i = 0; i < 40 && !(b.jobs.curDriver is JobDriver_Reading r && r.IsReading); i++)
                    {
                        yield return Ticks(10, "romance_shareread_setup", b);
                    }
                    read.playerForced = false;   // (the romance mod calls off a date with a pawn on a forced job)
                    lordJob = MakeLordJob("LordJob_SharedReading", a, b, middle + new IntVec3(1, 0, 0), middle);
                    if (lordJob == null)
                    {
                        Note("shareread: no LordJob_SharedReading(initiator, recipient, cell, cell); tried the worker");
                    }
                    yaw = 330f;
                    break;
                }
            }
            if (lordJob == null)
            {
                Check(date + ": lord job made", false, "constructor not found");
                yield break;
            }
            LordMaker.MakeNewLord(Faction.OfPlayer, (LordJob)lordJob, map, new[] { a, b });
            Note(date + ": outdoors " + map.mapTemperature.OutdoorTemp.ToString("F0") + " C, enjoyable outside " + JoyUtility.EnjoyableOutsideNow(map)
                + ", comfortable " + GenTemperature.ComfortableTemperatureRange(a) + " / " + GenTemperature.ComfortableTemperatureRange(b)
                + ", weather " + map.weatherManager.curWeather.defName);
            foreach (Pawn p in date == "kiss" ? new[] { a } : new[] { a, b })
            {
                if (p.CurJobDef == JobDefOf.Wait)
                {
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
            Look(watch, yaw);
            yield return WaitReal(2f);
            var seenA = new HashSet<string>();
            var seenB = new HashSet<string>();
            bool interacted = false;
            int steps = date == "kiss" ? 40 : 60;
            for (int i = 0; i < steps; i++)
            {
                yield return Ticks(15, "romance_" + date, a);
                Probe("romance_" + date, b);
                seenA.Add((a.CurJobDef != null ? a.CurJobDef.defName : "none") + "/" + a.GetPosture());
                seenB.Add((b.CurJobDef != null ? b.CurJobDef.defName : "none") + "/" + b.GetPosture());
                interacted |= Traverse.Create(lordJob).Field("Interacted").GetValue<bool>();
                lookAt = Vector3.Lerp(a.DrawPos, b.DrawPos, 0.5f);
                if (i % 10 == 9)
                {
                    yield return Shot(date + "_t" + (i + 1) * 15);
                }
            }
            Check(date + ": the date got going", interacted, "A " + string.Join(", ", seenA) + "; B " + string.Join(", ", seenB));
            foreach (Pawn p in new[] { a, b })
            {
                if (p.jobs != null && p.Spawned)
                {
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
        }

        private static object MakeLordJob(string name, params object[] args)
        {
            Type type = GenTypes.GetTypeInAnyAssembly("RomanceOnTheRim." + name);
            if (type == null)
            {
                return null;
            }
            foreach (ConstructorInfo ctor in type.GetConstructors())
            {
                ParameterInfo[] ps = ctor.GetParameters();
                if (ps.Length != args.Length)
                {
                    continue;
                }
                bool fits = true;
                for (int i = 0; i < ps.Length && fits; i++)
                {
                    fits = args[i] == null || ps[i].ParameterType.IsInstanceOfType(args[i]);
                }
                if (fits)
                {
                    return ctor.Invoke(args);
                }
            }
            return null;
        }

        // Cuddling a pet ([DG]Lovly Animal Cuddling's AC_CuddleAnimal): picked up into carry_cuddle, the pet on the left
        // forearm, and stroked (carry_cuddle_stroke, Ease.play) while still, standing (the job ambles about with it between
        // strokes) and sat on a chair.
        private static IEnumerator CuddleTest()
        {
            JobDef cuddle = DefDatabase<JobDef>.GetNamedSilentFail("AC_CuddleAnimal");
            PawnKindDef catKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Cat");
            if (cuddle == null || catKind == null)
            {
                Check("cuddle: the mod loaded", false, cuddle == null ? "no AC_CuddleAnimal job" : "no Cat");
                yield break;
            }
            foreach (string where in new[] { "standing", "seated" })
            {
                ClearPatch();
                Pawn pawn = NewPawn(middle, null);
                if (pawn.needs.joy != null)
                {
                    pawn.needs.joy.CurLevel = 0f;
                }
                Pawn cat = PawnGenerator.GeneratePawn(new PawnGenerationRequest(catKind, Faction.OfPlayer, fixedBiologicalAge: 3f, fixedChronologicalAge: 3f));
                Spawn(cat, middle + new IntVec3(1, 0, 0));
                Job job = JobMaker.MakeJob(cuddle, cat);
                if (where == "seated")
                {
                    ThingDef chairDef = DefDatabase<ThingDef>.GetNamed("DiningChair");
                    Thing chair = ThingMaker.MakeThing(chairDef, ThingDefOf.WoodLog);
                    chair.SetFaction(Faction.OfPlayer);
                    chair = Spawn(chair, middle + new IntVec3(0, 0, -1), Rot4.South);
                    job.targetB = chair.Position;
                    job.targetC = chair;
                }
                job.playerForced = true;
                job.ignoreJoyTimeAssignment = true;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                Look(pawn, 0f);
                yield return WaitReal(3f);
                string clip = null;
                float stroked = 0f, carried = 0f;
                bool sat = false;
                // Standing, it ambles about between stops of 125 to 200 ticks, so it is watched longer.
                int steps = where == "standing" ? 48 : 24;
                for (int i = 0; i < steps; i++)
                {
                    yield return Ticks(15, "cuddle_" + where, pawn);
                    if (pawn.CurJobDef != cuddle)
                    {
                        Note(where + ": the job ended at " + (i + 1) * 15 + " ticks (" + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none") + ")");
                        break;
                    }
                    string c = Field<string>(pawn, "carryClip");
                    float w = Field<float>(pawn, "carry");
                    if (w > 0.5f)
                    {
                        clip = c ?? clip;
                    }
                    carried = Mathf.Max(carried, w);
                    stroked = Mathf.Max(stroked, Field<float>(pawn, "play"));
                    sat |= pawn.GetPosture() == PawnPosture.Standing && pawn.Position == middle + new IntVec3(0, 0, -1);
                    if (i % (steps / 6) == steps / 6 - 1)
                    {
                        yield return Shot(where + "_t" + (i + 1) * 15);
                    }
                }
                Check(where + ": carry_cuddle", clip == "carry_cuddle" && carried > 0.9f, "clip " + (clip ?? "none") + ", carry " + carried.ToString("F2"));
                Check(where + ": stroked", stroked > 0.9f, "stroke weight reached " + stroked.ToString("F2"));
                if (where == "seated")
                {
                    Check("seated: on the chair", sat, "at " + pawn.Position);
                }
                if (pawn.jobs != null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);   // the cat set down, so it can be cleaned up
                }
            }
            Note("Look: the cat sat along the left forearm, head to the carrier's left; the right palm on its back, stroking it neck to rump while still; seated on the chair too.");
        }

        // CE's CompSuppressable.AddSuppression, by reflection, enough to hunker.
        private static void Suppress(Pawn pawn, IntVec3 from)
        {
            foreach (ThingComp comp in pawn.AllComps)
            {
                if (comp.GetType().Name == "CompSuppressable")
                {
                    MethodInfo add = AccessTools.Method(comp.GetType(), "AddSuppression");
                    if (add != null)
                    {
                        add.Invoke(comp, new object[] { 500f, from });
                    }
                }
            }
        }

        // ---- the map ----

        // The nearest patch to the map's middle with every cell within Clear standable, unroofed, dry and built on by nothing.
        private static IntVec3 FindPatch()
        {
            IntVec3 centre = map.Center;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(centre, 60f, true))
            {
                if (PatchFits(c))
                {
                    return c;
                }
            }
            return centre;
        }

        private static bool PatchFits(IntVec3 c)
        {
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(c, Clear, true))
            {
                if (!cell.InBounds(map) || !cell.Standable(map) || cell.Roofed(map) || cell.GetTerrain(map).IsWater
                    || cell.GetTerrain(map).defName.Contains("Marsh") || cell.GetTerrain(map).defName.Contains("Mud")
                    || cell.GetEdifice(map) != null)
                {
                    return false;
                }
            }
            return true;
        }

        // Plants, items, filth and fires on the patch taken away; pawns not ours moved off it.
        private static void ClearPatch()
        {
            CleanUp();
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(middle, Clear, true))
            {
                List<Thing> things = cell.GetThingList(map);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    Thing t = things[i];
                    if (t is Plant || t is Fire || t is Filth || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Mote)
                    {
                        t.Destroy();
                    }
                    else if (t is Pawn p && !p.Dead)
                    {
                        IntVec3 away = CellFinder.RandomClosewalkCellNear(middle + new IntVec3(Clear + 4, 0, 0), map, 3);
                        p.Position = away;
                        p.Notify_Teleported();
                    }
                }
            }
        }

        private static void CleanUp()
        {
            foreach (Thing t in spawned)
            {
                if (!t.Destroyed)
                {
                    t.Destroy();
                }
            }
            spawned.Clear();
            foreach (Thing fire in new List<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Fire)))
            {
                if (fire.Position.InHorDistOf(middle, Clear + 3))
                {
                    fire.Destroy();
                }
            }
        }

        private static Thing Spawn(Thing thing, IntVec3 at, Rot4? rot = null)
        {
            Thing made = GenSpawn.Spawn(thing, at, map, rot ?? Rot4.North);
            spawned.Add(made);
            return made;
        }

        private static Fire StartFire(IntVec3 at)
        {
            Fire fire = (Fire)ThingMaker.MakeThing(ThingDefOf.Fire);
            fire.fireSize = 1f;
            GenSpawn.Spawn(fire, at, map, Rot4.North);
            return fire;
        }

        // A new grown colonist who can fight fires, standing at a cell, with a weapon if named.
        private static Pawn NewPawn(IntVec3 at, string weapon)
        {
            Pawn pawn = null;
            for (int i = 0; i < 20; i++)
            {
                pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                if (!pawn.WorkTagIsDisabled(WorkTags.Firefighting) && !pawn.WorkTagIsDisabled(WorkTags.Violent))
                {
                    break;
                }
            }
            pawn.equipment.DestroyAllEquipment();
            Spawn(pawn, at);
            if (weapon != null)
            {
                ThingWithComps gun = (ThingWithComps)ThingMaker.MakeThing(ThingDef.Named(weapon));
                pawn.equipment.AddEquipment(gun);
            }
            pawn.Rotation = Rot4.South;
            return pawn;
        }

        // Kept standing where it is, doing nothing, until another job is given.
        private static void Hold(Pawn pawn)
        {
            Job wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = 100000;
            pawn.jobs.StartJob(wait, JobCondition.InterruptForced);
        }

        // ---- the camera and pictures ----

        private static void Look(Pawn pawn, float yaw)
        {
            lookAt = pawn.DrawPos;
            lookYaw = yaw;
        }

        private static IsoRim.ViewPose? Pose()
        {
            float pitch = lookPitch * Mathf.Deg2Rad;
            float yaw = lookYaw * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            Vector3 target = new Vector3(lookAt.x, lookHeight, lookAt.z);
            Vector3 eye = target - forward * (lookDistance * Mathf.Cos(pitch)) + Vector3.up * (lookDistance * Mathf.Sin(pitch));
            return IsoRim.ViewPose.FromEye(eye, lookYaw, lookPitch, LookFov);
        }

        // A picture from the scenario's side (its yaw) and one from 90 degrees round, so a move seen end-on in one shows in
        // the other.
        private static IEnumerator Shot(string name)
        {
            if (video)
            {
                Log.Message(Tag + "at " + name);
                yield break;
            }
            string prefix = CurrentPrefix();
            float yaw = lookYaw;
            for (int view = 0; view < 2; view++)
            {
                lookYaw = yaw + view * 90f;
                yield return null;
                yield return null;
                string file = Path.Combine(outDir, prefix + name + (view == 0 ? "_a" : "_b") + ".png");
                imageDone = false;
                bool asked = IsoRim.Api.RequestSceneImage(tex =>
                {
                    if (tex != null)
                    {
                        File.WriteAllBytes(file, tex.EncodeToPNG());
                        UnityEngine.Object.Destroy(tex);
                    }
                    imageDone = true;
                }, new IsoRim.SceneImageOptions { IncludeOverlays = false });
                for (int i = 0; asked && i < 120 && !imageDone; i++)
                {
                    yield return null;
                }
                if (asked)
                {
                    report.AppendLine("- picture `" + Path.GetFileName(file) + "`");
                }
            }
            lookYaw = yaw;
        }

        private static string current = "";
        private static int shotCount;

        private static string CurrentPrefix()
        {
            return current + "_" + (shotCount++).ToString("D2") + "_";
        }

        // Game ticks moved on: one a frame while paused (stills), or at normal speed (video). Each tick probed.
        private static IEnumerator Ticks(int ticks, string what, Pawn pawn)
        {
            int until = Find.TickManager.TicksGame + ticks;
            while (Find.TickManager.TicksGame < until)
            {
                if (video)
                {
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                }
                else
                {
                    Find.TickManager.DoSingleTick();
                    // The game eases each pawn's drawn place toward its cell by the tick rate, which is 0 while paused,
                    // so stepped here a pawn that walked would be drawn where it started: put each where the game has it.
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    {
                        p.Drawer?.tweener?.ResetTweenedPosToRoot();
                    }
                }
                if (pawn != null && pawn.Spawned)
                {
                    lookAt = Vector3.Lerp(lookAt, pawn.DrawPos, 0.1f);
                }
                yield return null;
                Probe(what, pawn);
            }
            Pause();
        }

        private static void Pause()
        {
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }

        private static IEnumerator WaitReal(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
        }

        // ---- what is written ----

        private static void Probe(string what, Pawn pawn)
        {
            if (probe == null || pawn == null)
            {
                return;
            }
            probe.WriteLine(Find.TickManager.TicksGame + " " + what + " " + pawn.LabelShort + " " + WalkFigures.AutoProbe(pawn));
        }

        private static T Field<T>(Pawn pawn, string name)
        {
            object v = WalkFigures.AutoField(pawn, name);
            return v is T t ? t : default(T);
        }

        private static void Check(string what, bool passed, string seen)
        {
            report.AppendLine("- " + (passed ? "PASS" : "**FAIL**") + " " + what + ": " + seen);
            Log.Message(Tag + (passed ? "PASS " : "FAIL ") + what + ": " + seen);
        }

        private static void Note(string text)
        {
            report.AppendLine("- " + text);
        }

        // ---- video ----

        private static void StartRecording(string name)
        {
            if (!video)
            {
                return;
            }
            string file = Path.Combine(outDir, name + ".mp4");
            string cmd = "id=$(xdotool search --name '^RimWorld by Ludeon Studios$' | head -1); "
                + "exec ffmpeg -loglevel error -y -f x11grab -framerate 30 -window_id $id -i :0 -t 300 "
                + "-vf 'scale=trunc(iw/4)*2:-2' -c:v libx264 -preset veryfast -crf 26 -pix_fmt yuv420p '" + file + "'";
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("bash")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(cmd);
                recorder = Process.Start(start);
                report.AppendLine("- video `" + Path.GetFileName(file) + "`");
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "could not record: " + e.Message);
                recorder = null;
            }
        }

        private static void StopRecording()
        {
            if (recorder == null)
            {
                return;
            }
            try
            {
                if (!recorder.HasExited)
                {
                    recorder.StandardInput.Write("q");
                    recorder.StandardInput.Flush();
                    recorder.WaitForExit(5000);
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "stopping the recording: " + e.Message);
            }
            recorder = null;
        }
    }

    public static partial class WalkFigures
    {
        // The figure's state as AutoTest probes it: one line of the Ease fields the scenarios look at.
        internal static string AutoProbe(Pawn pawn)
        {
            Ease e;
            if (!eases.TryGetValue(pawn.thingIDNumber, out e))
            {
                return "job=" + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none") + " (not drawn)";
            }
            return "job=" + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none") + " posture=" + pawn.GetPosture()
                + " flash=" + HitFlash.Of(pawn, false).ToString("F2")
                + " duck=" + e.duck.ToString("F2") + " duckLoop=" + (e.duckLoop ?? "-") + " work=" + e.work.ToString("F2")
                + " workKind=" + (e.workKind ?? "-") + " beats=" + e.beatCount + " beatPeriod=" + e.beatPeriod
                + " toss=" + e.toss.ToString("F2") + " tossAt=" + e.tossAt.ToString("F2") + " fallen=" + e.fallen.ToString("F2")
                + " carry=" + e.carry.ToString("F2") + " carryClip=" + (e.carryClip ?? "-") + " play=" + e.play.ToString("F2")
                + " eatClip=" + (e.eatClip ?? "-") + " eat=" + e.eat.ToString("F2") + " look=" + e.look.ToString("F1")
                + PetProbe(pawn);
        }

        // A carried animal's drawn back (IsoRim's Api.ReadHost), cells, for fitting the cuddle.
        private static string PetProbe(Pawn pawn)
        {
            Thing load = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            return load is Pawn animal ? " petBack=" + PetBackOf(animal).ToString("F3") + " size=" + SizeOf(pawn).ToString("F2") : "";
        }

        // One Ease field by name, null when the pawn hasn't been drawn.
        internal static object AutoField(Pawn pawn, string name)
        {
            Ease e;
            if (!eases.TryGetValue(pawn.thingIDNumber, out e))
            {
                return null;
            }
            FieldInfo field = typeof(Ease).GetField(name);
            return field != null ? field.GetValue(e) : null;
        }
    }
}
#endif
