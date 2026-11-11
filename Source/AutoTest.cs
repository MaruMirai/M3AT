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
            // In dev mode the game opens its log window on every error or warning, over the view being watched (and any
            // error a scenario logs is in Player.log and the report anyway), so its auto-open is turned off for the run,
            // as its "Auto-open is OFF" button does; KeepLogShut closes one opened before this ran.
            AccessTools.Field(typeof(LudeonTK.EditWindow_Log), "canAutoOpen")?.SetValue(null, false);
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
            KeepLogShut();
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

        private static void KeepLogShut()
        {
            LudeonTK.EditWindow_Log.wantsToOpen = false;
            if (Find.WindowStack != null && Find.WindowStack.IsOpen<LudeonTK.EditWindow_Log>())
            {
                Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), doCloseSound: false);
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
                case "eat": return EatTest();
                case "visit": return VisitTest();
                case "music": return MusicTest();
                case "teach": return TeachTest();
                case "romance": return RomanceTest(null);
                case "readroom": return ReadRoomTest();
                case "bedcompare": return BedCompareTest();
                case "sharedbed": return SharedBedTest();
                case "lovin": return LovinTest();
                case "kidbed": return KidBedTest();
                case "tend": return TendTest(null);
                case "bows": return BowTest(null);
                case "tendbed": return TendBedTest();
                case "feed": return FeedTest(null);
                case "slicecap": return SlicecapTest();
                case "hats": return HatsTest();
            }
            if (name.StartsWith("feed_"))
            {
                return FeedTest(name.Substring("feed_".Length));
            }
            if (name.StartsWith("tend_"))
            {
                return TendTest(name.Substring("tend_".Length));
            }
            if (name.StartsWith("bows_"))
            {
                return BowTest(name.Substring("bows_".Length));
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

        // Lovin beside sleeping, under IsoRim's blanket (the bedBlankets option, on for the test): two double beds side by side,
        // one couple at lovin (awake: the lie clip) and one asleep (sleep), to see whether the heads and lumps lie as far up
        // the bed (Ease.headAlong, blanketAlong: cells from IsoRim's head place toward the feet).
        private static IEnumerator BedCompareTest()
        {
            bool was = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = true;
            try
            {
                Building_Bed MakeBed(IntVec3 at)
                {
                    Thing made = ThingMaker.MakeThing(ThingDef.Named("DoubleBed"), ThingDefOf.WoodLog);
                    made.SetFaction(Faction.OfPlayer);
                    return (Building_Bed)Spawn(made, at, Rot4.North);
                }
                Building_Bed lovinBed = MakeBed(middle);
                Building_Bed sleepBed = MakeBed(middle + new IntVec3(3, 0, 0));
                Pawn[] all = new Pawn[4];
                for (int k = 0; k < 4; k++)
                {
                    all[k] = NewPawn(middle + new IntVec3(-2 + k * 2, 0, -3), null);
                }
                all[0].relations.AddDirectRelation(PawnRelationDefOf.Spouse, all[1]);
                all[2].relations.AddDirectRelation(PawnRelationDefOf.Spouse, all[3]);
                foreach (int k in new[] { 0, 1 })
                {
                    lovinBed.TryGetComp<CompAssignableToPawn>().TryAssignPawn(all[k]);
                }
                foreach (int k in new[] { 2, 3 })
                {
                    sleepBed.TryGetComp<CompAssignableToPawn>().TryAssignPawn(all[k]);
                }
                void Keep()
                {
                    for (int k = 0; k < 4; k++)
                    {
                        Pawn p = all[k];
                        bool lovin = k < 2;
                        if (p.needs.rest != null)
                        {
                            p.needs.rest.CurLevel = lovin ? 1f : 0.05f;
                        }
                        if (lovin && p.CurJobDef != JobDefOf.Lovin)
                        {
                            p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Lovin, all[1 - k], lovinBed), JobCondition.InterruptForced);
                        }
                        else if (!lovin && (!p.InBed() || p.CurrentBed() != sleepBed))
                        {
                            p.jobs.Notify_TuckedIntoBed(sleepBed);
                        }
                    }
                }
                Keep();
                lookAt = sleepBed.TrueCenter() + new Vector3(-1.5f, 0f, 0f);
                lookYaw = 0f;
                yield return WaitReal(3f);
                float[] head = new float[4], lump = new float[4];
                for (int i = 0; i < 30; i++)
                {
                    Keep();
                    yield return Ticks(10, "bedcompare", all[0]);
                    lookAt = sleepBed.TrueCenter() + new Vector3(-1.5f, 0f, 0f);
                    for (int k = 0; k < 4; k++)
                    {
                        head[k] = Field<float>(all[k], "headAlong");
                        lump[k] = Field<float>(all[k], "blanketAlong");
                    }
                    if (i % 15 == 14)
                    {
                        yield return Shot("beds_t" + (i + 1) * 10);
                        lookYaw = 90f;
                        yield return Shot("beds_side_t" + (i + 1) * 10);
                        lookYaw = 0f;
                    }
                }
                string[] names = { "lovin left", "lovin right", "asleep left", "asleep right" };
                for (int k = 0; k < 4; k++)
                {
                    Pawn p = all[k];
                    Note(names[k] + ": job " + (p.CurJobDef != null ? p.CurJobDef.defName : "none") + ", awake " + p.Awake()
                        + ", head " + head[k].ToString("F3") + ", lump starts " + lump[k].ToString("F3") + " cells down the bed");
                }
                Check("lovin heads as far up the bed as sleeping ones", Mathf.Abs((head[0] + head[1]) - (head[2] + head[3])) / 2f < 0.03f,
                    "lovin " + ((head[0] + head[1]) / 2f).ToString("F3") + ", asleep " + ((head[2] + head[3]) / 2f).ToString("F3"));
                Check("lovin lumps start where sleeping ones do", Mathf.Abs((lump[0] + lump[1]) - (lump[2] + lump[3])) / 2f < 0.03f,
                    "lovin " + ((lump[0] + lump[1]) / 2f).ToString("F3") + ", asleep " + ((lump[2] + lump[3]) / 2f).ToString("F3"));
            }
            finally
            {
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
                    if (i % 20 == 19)
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

        // The slicecap (Ideology's), a crescent of shell over the left of the scalp, worn over the hair (over_hair,
        // HatOverHair.cs): the hair left whole and the cap pushed out past it, on a few styles from short to big.
        private static IEnumerator SlicecapTest()
        {
            ThingDef cap = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Slicecap");
            if (cap == null)
            {
                Check("slicecap", false, "no Apparel_Slicecap (Ideology off?)");
                yield break;
            }
            Rand.PushState(20261111);
            Pawn pawn;
            try
            {
                pawn = NewPawn(middle, null);
            }
            finally
            {
                Rand.PopState();
            }
            pawn.apparel.DestroyAll();
            if (pawn.story != null)
            {
                pawn.story.bodyType = BodyTypeDefOf.Male;
            }
            Hold(pawn);
            Look(pawn, 0f);
            lookDistance = 1.6f;
            lookHeight = 1.0f;
            foreach (string style in new[] { "Bob", "Afro", "Spikes", "FancyBun" })
            {
                HairDef hair = DefDatabase<HairDef>.GetNamedSilentFail(style);
                if (hair == null || pawn.story == null)
                {
                    continue;
                }
                pawn.apparel.DestroyAll();
                pawn.story.hairDef = hair;
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                lookPitch = 30f;
                lookYaw = 0f;
                yield return WaitReal(4f);   // its hair built
                yield return Shot(style + "_bare");
                pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(cap, GenStuff.DefaultStuffFor(cap)));
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                yield return Ticks(5, "slicecap", pawn);
                foreach (float yaw in new[] { 0f, 180f })
                {
                    lookYaw = yaw;
                    yield return Shot(style + "_cap_" + (int)yaw);
                }
                lookPitch = 70f;
                lookYaw = 0f;
                yield return Shot(style + "_cap_above");
                string over = WalkFigures.AutoOverHair(cap, hair);
                Check(style + ": the cap clears the hair, the hair whole", over != null && over.StartsWith("ok"), over ?? "no garment");
            }
            lookDistance = LookDistance;
            lookHeight = LookHeight;
            lookPitch = LookPitch;
            lookYaw = 0f;
            string far = WalkFigures.AutoFarHides(cap, 0.06f);
            Check("slicecap hides only body triangles it is near", far != null && far.StartsWith("0 "), far ?? "no garment for it");
            Note("Look: no scalp showing round the cap (cap_0, cap_180, cap_above), the cap lying on the hair, not floating far off it.");
        }

        // Every hat that tucks the hair under it (hide_hair tuck), worn in turn by one pawn with hair, the same pawn each run
        // (a fixed seed), so a run with M3AT_LEGACY_HATS=1 (the rims and scalp hiding before the slicecap's fix) can be
        // compared picture by picture with one without. The report gives each hat's rim both ways and what it hides.
        private static IEnumerator HatsTest()
        {
            Note(SkinnedFigure.Legacy ? "Legacy rims (M3AT_LEGACY_HATS=1)." : "Current rims.");
            Rand.PushState(20261111);
            Pawn pawn;
            try
            {
                pawn = NewPawn(middle, null);
            }
            finally
            {
                Rand.PopState();
            }
            pawn.apparel.DestroyAll();
            if (pawn.story != null)
            {
                pawn.story.bodyType = BodyTypeDefOf.Male;
                pawn.story.hairDef = DefDatabase<HairDef>.GetNamedSilentFail("Bob") ?? DefDatabase<HairDef>.GetNamedSilentFail("Mop") ?? pawn.story.hairDef;
            }
            pawn.Drawer.renderer.SetAllGraphicsDirty();
            Hold(pawn);
            Look(pawn, 0f);
            lookDistance = 1.6f;
            lookHeight = 1.0f;
            lookPitch = 30f;
            yield return WaitReal(4f);   // its hair built
            List<string> hats = WalkFigures.AutoTuckers();
            Note("hair " + (pawn.story != null && pawn.story.hairDef != null ? pawn.story.hairDef.defName : "none") + ", " + hats.Count + " tucking hats");
            foreach (string hat in hats)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(hat);
                if (def == null)
                {
                    Note(hat + ": not loaded, skipped.");
                    continue;
                }
                pawn.apparel.DestroyAll();
                pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)));
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                yield return Ticks(5, "hats", pawn);
                Note(hat + ": " + WalkFigures.AutoRim(def));
                lookPitch = 30f;
                lookYaw = 0f;
                yield return Shot(hat + "_front");
                lookYaw = 180f;
                yield return Shot(hat + "_back");
                lookPitch = 70f;
                lookYaw = 0f;
                yield return Shot(hat + "_above");
            }
            lookDistance = LookDistance;
            lookHeight = LookHeight;
            lookPitch = LookPitch;
            lookYaw = 0f;
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

        // Eating (the game's Ingest job, given a meal on the floor a few cells off): with a dining chair at a table, it picks
        // the meal up, carries it there (carry_item over the walk), sits and eats at the table (eat_sit, or its knife and fork
        // variant, laid over the sit); with no furniture it eats standing (eat_stand, the meal in the left palm).
        private static IEnumerator EatTest()
        {
            foreach (bool atTable in new[] { true, false })
            {
                ClearPatch();
                string name = atTable ? "table" : "standing";
                IntVec3 chairAt = middle + new IntVec3(1, 0, 0);
                if (atTable)
                {
                    Thing c = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                    c.SetFaction(Faction.OfPlayer);
                    Spawn(c, chairAt, Rot4.North);
                    Thing t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Table1x2c"), ThingDefOf.WoodLog);
                    t.SetFaction(Faction.OfPlayer);
                    Spawn(t, middle + new IntVec3(1, 0, 1), Rot4.North);
                }
                Thing meal = Spawn(ThingMaker.MakeThing(ThingDefOf.MealSimple), middle + new IntVec3(-3, 0, -1));
                Pawn pawn = NewPawn(middle + new IntVec3(-2, 0, -2), null);
                if (pawn.needs.food != null)
                {
                    pawn.needs.food.CurLevel = 0.1f;
                }
                Look(pawn, 0f);
                yield return WaitReal(2f);
                Job job = JobMaker.MakeJob(JobDefOf.Ingest, meal);
                job.count = 1;
                job.playerForced = true;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                bool carriedMoving = false, walkShot = false, sat = false;
                float carryMost = 0f, most = 0f;
                string clip = null;
                for (int i = 0; i < 120 && most < 0.99f; i++)
                {
                    yield return Ticks(5, "eat_" + name, pawn);
                    bool holding = pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null;
                    bool moving = pawn.pather != null && pawn.pather.Moving;
                    if (holding && moving)
                    {
                        carriedMoving = true;
                        carryMost = Mathf.Max(carryMost, Field<float>(pawn, "carry"));
                        if (!walkShot && carryMost > 0.9f)
                        {
                            walkShot = true;
                            yield return Shot(name + "_carrying");
                        }
                    }
                    if (atTable && pawn.Position == chairAt && !moving)
                    {
                        sat = true;
                    }
                    string c = Field<string>(pawn, "eatClip");
                    if (c != null && c.StartsWith("eat_"))
                    {
                        clip = c;
                        most = Mathf.Max(most, Field<float>(pawn, "eat"));
                    }
                }
                Check(name + ": carried the meal on the move", carriedMoving && carryMost > 0.9f,
                    "carry weight " + carryMost.ToString("F2") + (carriedMoving ? "" : ", never seen carrying it while moving"));
                if (atTable)
                {
                    Check("table: sat on the chair", sat, "at " + pawn.Position + " (chair " + chairAt + "), posture " + pawn.GetPosture());
                }
                string want = atTable ? "eat_sit" : "eat_stand";
                Check(name + ": eating (" + want + ")", most >= 0.99f && clip != null && clip.StartsWith(want),
                    "clip " + (clip ?? "none") + ", weight " + most.ToString("F2") + ", job " + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none")
                    + ", target B " + pawn.CurJob?.targetB + ", holding " + (pawn.carryTracker?.CarriedThing?.LabelShort ?? "nothing"));
                Look(pawn, atTable ? 90f : 0f);   // (at the table from the side, then from across it: from behind the chair's back hides it)
                yield return WaitReal(1f);
                for (int i = 0; i < 8 && pawn.CurJobDef == JobDefOf.Ingest; i++)
                {
                    yield return Shot(name + "_" + i);
                    yield return Ticks(40, "eat_" + name, pawn);
                }
            }
            Note("Look: the meal carried between the hands on the walk over; at the table sat facing it, the right hand going from the plate to the mouth (or knife and fork, eat_sit~2) with the meal on the table; standing, the meal on the left palm, the right hand to the mouth.");
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

        // Tending a patient (TendPatient): in a bed (stood at it, bench), on a sleeping spot and lying on the bare floor
        // (knelt beside them, crouch), each with medicine fetched first and without. The floor is tended drafted, as the
        // game only lets a doctor tend someone on the ground by order. tend_<where> (bed, spot, floor) for one place.
        private static IEnumerator TendTest(string only)
        {
            string[] places = { "bed", "spot", "floor" };
            string[] wantKind = { "bench", "crouch", "crouch" };
            for (int c = 0; c < places.Length; c++)
            {
                string where = places[c];
                if (only != null && only != where)
                {
                    continue;
                }
                foreach (bool meds in new[] { true, false })
                {
                    ClearPatch();
                    string name = where + (meds ? "_meds" : "_nomeds");
                    Building_Bed bed = null;
                    if (where != "floor")
                    {
                        ThingDef bedDef = DefDatabase<ThingDef>.GetNamed(where == "spot" ? "SleepingSpot" : "Bed");
                        Thing made = ThingMaker.MakeThing(bedDef, bedDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
                        made.SetFaction(Faction.OfPlayer);
                        bed = (Building_Bed)Spawn(made, middle + new IntVec3(1, 0, 0), Rot4.North);
                        bed.Medical = true;
                    }
                    Pawn patient = NewPawn(middle + new IntVec3(1, 0, where == "floor" ? 0 : -1), null);
                    Pawn doctor = NewDoctor(middle + new IntVec3(-2, 0, -1));
                    Wound(patient, 8);
                    if (bed != null)
                    {
                        HealthUtility.AdjustSeverity(patient, HediffDefOf.Anesthetic, 1f);   // (down and kept there)
                        patient.jobs.Notify_TuckedIntoBed(bed);
                    }
                    else
                    {
                        // On the floor it goes down where it stands, as in play, so it falls and lies as a ragdoll (which
                        // steps by frame time, none while the test steps the game paused: so that runs at normal speed).
                        Hold(patient);
                        Look(patient, 0f);
                        yield return WaitReal(2f);
                        HealthUtility.AdjustSeverity(patient, HediffDefOf.Anesthetic, 1f);
                        yield return Unpaused(patient, () => false, 4f);
                        Note(name + ": fallen, " + WalkFigures.AutoLying(patient));
                        yield return Shot(name + "_fallen");
                    }
                    Thing medicine = null;
                    if (meds)
                    {
                        Thing m = ThingMaker.MakeThing(ThingDefOf.MedicineIndustrial);
                        m.stackCount = 10;
                        medicine = Spawn(m, middle + new IntVec3(-3, 0, 2));
                    }
                    if (where == "floor" && doctor.drafter != null)
                    {
                        doctor.drafter.Drafted = true;
                    }
                    Look(patient, 0f);
                    yield return WaitReal(3f);
                    int untendedBefore = Untended(patient);
                    string kind = null;
                    float work = 0f, carried = 0f;
                    int tendTicks = 0;
                    bool tending = false, placed = false, topShot = false;
                    float nearest = float.MaxValue;
                    for (int i = 0; i < 200; i++)
                    {
                        if (bed != null && !patient.InBed())
                        {
                            patient.jobs.Notify_TuckedIntoBed(bed);
                        }
                        if (doctor.CurJobDef != JobDefOf.TendPatient && Untended(patient) > 0)
                        {
                            // (begun again after each tend, as the game would have the doctor go on to the next wound)
                            Job job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine != null && medicine.Spawned ? medicine : null);
                            job.count = 1;
                            job.playerForced = true;
                            job.draftedTend = where == "floor";
                            doctor.jobs.StartJob(job, JobCondition.InterruptForced);
                        }
                        yield return Ticks(15, "tend_" + name, doctor);
                        Vector3? beside = Field<Vector3?>(doctor, "workBeside");
                        lookAt = Vector3.Lerp(beside ?? doctor.DrawPos, patient.DrawPos, 0.5f);
                        if (doctor.CurJobDef == JobDefOf.TendPatient && (doctor.pather == null || !doctor.pather.Moving))
                        {
                            tending = true;
                            tendTicks += 15;
                        }
                        carried = Mathf.Max(carried, Field<float>(doctor, "carry"));
                        string k = Field<string>(doctor, "workKind");
                        float w = Field<float>(doctor, "work");
                        if (k != null && w > 0.5f)
                        {
                            kind = k;
                        }
                        work = Mathf.Max(work, w);
                        Vector3[] joints = Field<Vector3[]>(patient, "lyingJoints");
                        if (k == "crouch" && w > 0.9f && beside.HasValue && joints != null)
                        {
                            placed = true;
                            foreach (Vector3 j in joints)
                            {
                                Vector3 d = j - beside.Value;
                                d.y = 0f;
                                nearest = Mathf.Min(nearest, d.magnitude);
                            }
                        }
                        if (i % 20 == 19)
                        {
                            yield return Shot(name + "_t" + (i + 1) * 15);
                        }
                        if (!topShot && w > 0.95f && (doctor.pather == null || !doctor.pather.Moving))
                        {
                            // (from above, where the knees and hands show against the body)
                            topShot = true;
                            float was = lookPitch, wasD = lookDistance;
                            lookPitch = 75f;
                            lookDistance = 3f;
                            lookAt = patient.DrawPos;
                            yield return Shot(name + "_above");
                            lookPitch = was;
                            lookDistance = wasD;
                            Note(name + ": patient " + WalkFigures.AutoLying(patient));
                        }
                        if (Untended(patient) == 0 && i > 20)
                        {
                            yield return Shot(name + "_done");
                            break;
                        }
                    }
                    int untendedAfter = Untended(patient);
                    if (where == "floor")
                    {
                        // (the doctor gone: still on its back, not curled on its side again)
                        if (doctor.jobs != null)
                        {
                            doctor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        }
                        doctor.Position = middle + new IntVec3(-4, 0, -3);
                        doctor.Notify_Teleported();
                        if (doctor.drafter != null)
                        {
                            doctor.drafter.Drafted = false;
                        }
                        Hold(doctor);
                        yield return Ticks(120, "tend_" + name + "_after", patient);
                        lookAt = patient.DrawPos;
                        string after = WalkFigures.AutoLying(patient);
                        Check(name + ": stays on its back after", (after.Contains("lying clip sleep") || after.Contains("lying clip downed,")) && after.Contains("ragdoll False"), after);
                        yield return Shot(name + "_after");
                    }
                    Check(name + ": tended", untendedAfter < untendedBefore,
                        untendedBefore + " wounds untended before, " + untendedAfter + " after; job " + (doctor.CurJobDef != null ? doctor.CurJobDef.defName : "none")
                        + ", " + tendTicks + " ticks at it, patient " + patient.GetPosture() + (bed != null ? ", in bed " + patient.InBed() : ""));
                    Check(name + ": work_" + wantKind[c], kind == wantKind[c] && work > 0.9f, "work kind " + (kind ?? "none") + ", weight " + work.ToString("F2"));
                    if (wantKind[c] == "crouch")
                    {
                        Check(name + ": knelt beside, off the patient", placed && nearest > 0.3f && nearest < 1f,
                            placed ? "hips " + nearest.ToString("F2") + " cells from the patient's nearest joint" : "not placed beside (KneelBeside)");
                    }
                    if (meds)
                    {
                        Check(name + ": medicine carried over", carried > 0.5f, "carry weight reached " + carried.ToString("F2")
                            + ", medicine left " + (medicine != null && !medicine.Destroyed ? medicine.stackCount.ToString() : "none"));
                    }
                    Note(name + ": doctor at " + doctor.Position + ", patient at " + patient.Position + ", tending " + tending + ", drafted " + doctor.Drafted);
                    if (doctor.jobs != null)
                    {
                        doctor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    }
                }
            }
            Note("Look: in a bed stood at its side working over the patient; by the spot and on the floor knelt beside them, hands at the body, not inside it. With medicine it is carried over first, and not left floating in the hands at the work.");
        }

        // A hungry patient fed in a bed, on a sleeping spot and downed on the floor (feed_<where> for one), pictures from
        // two sides and from above while it is at it. Run with M3AT_FEED_TAME=1 to see the taming clips in its place
        // (tame at the bed, tame_kneel on the ground).
        private static IEnumerator FeedTest(string only)
        {
            string[] places = { "bed", "spot", "floor" };
            foreach (string where in places)
            {
                if (only != null && only != where)
                {
                    continue;
                }
                ClearPatch();
                Building_Bed bed = null;
                if (where != "floor")
                {
                    ThingDef bedDef = DefDatabase<ThingDef>.GetNamed(where == "spot" ? "SleepingSpot" : "Bed");
                    Thing made = ThingMaker.MakeThing(bedDef, bedDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    made.SetFaction(Faction.OfPlayer);
                    bed = (Building_Bed)Spawn(made, middle + new IntVec3(1, 0, 0), Rot4.North);
                    bed.Medical = true;
                }
                Pawn patient = NewPawn(middle + new IntVec3(1, 0, where == "floor" ? 0 : -1), null);
                Pawn feeder = NewDoctor(middle + new IntVec3(-2, 0, -1));
                if (bed != null)
                {
                    HealthUtility.AdjustSeverity(patient, HediffDefOf.Anesthetic, 1f);
                    patient.jobs.Notify_TuckedIntoBed(bed);
                }
                else
                {
                    Hold(patient);
                    Look(patient, 0f);
                    yield return WaitReal(2f);
                    HealthUtility.AdjustSeverity(patient, HediffDefOf.Anesthetic, 1f);
                    yield return Unpaused(patient, () => false, 4f);
                }
                if (patient.needs.food != null)
                {
                    patient.needs.food.CurLevel = 0.05f;
                }
                Thing meal = Spawn(ThingMaker.MakeThing(ThingDefOf.MealSimple), middle + new IntVec3(-3, 0, 1));
                if (where == "floor" && feeder.drafter != null)
                {
                    feeder.drafter.Drafted = true;
                }
                Look(patient, 0f);
                yield return WaitReal(2f);
                Dictionary<string, int> kinds = new Dictionary<string, int>();
                int atWork = 0;
                bool shot = false;
                for (int i = 0; i < 160; i++)
                {
                    if (bed != null && !patient.InBed())
                    {
                        patient.jobs.Notify_TuckedIntoBed(bed);
                    }
                    if (patient.needs.food != null)
                    {
                        patient.needs.food.CurLevel = Mathf.Min(patient.needs.food.CurLevel, 0.05f);
                    }
                    if (feeder.CurJobDef != JobDefOf.FeedPatient)
                    {
                        if (meal == null || meal.Destroyed)
                        {
                            break;
                        }
                        Job job = JobMaker.MakeJob(JobDefOf.FeedPatient, meal, patient);
                        job.count = 1;
                        job.playerForced = true;
                        feeder.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    yield return Ticks(15, "feed_" + where, feeder);
                    Vector3? beside = Field<Vector3?>(feeder, "workBeside");
                    lookAt = Vector3.Lerp(beside ?? feeder.DrawPos, patient.DrawPos, 0.5f);
                    string k = Field<string>(feeder, "workKind");
                    float w = Field<float>(feeder, "work");
                    if (k != null && w > 0.5f)
                    {
                        kinds[k] = kinds.TryGetValue(k, out int n) ? n + 1 : 1;
                        atWork++;
                    }
                    if (!shot && k != null && w > 0.95f && (feeder.pather == null || !feeder.pather.Moving))
                    {
                        shot = true;
                        yield return Shot(where);
                        yield return Ticks(30, "feed_" + where, feeder);
                        yield return Shot(where + "_later");
                        float was = lookPitch, wasD = lookDistance;
                        lookPitch = 75f;
                        lookDistance = 3f;
                        lookAt = patient.DrawPos;
                        yield return Shot(where + "_above");
                        lookPitch = was;
                        lookDistance = wasD;
                    }
                    if (shot && atWork > 12)
                    {
                        break;
                    }
                }
                string want = WalkFigures.FeedAsTame ? (where == "bed" ? "tame" : "tame_kneel") : (where == "bed" ? "bench" : "crouch");
                Check(where + ": work_" + want, kinds.ContainsKey(want), atWork > 0 ? "work kinds " + string.Join(", ", kinds.Keys) : "never at work; job "
                    + (feeder.CurJobDef != null ? feeder.CurJobDef.defName : "none") + ", patient " + patient.GetPosture() + (bed != null ? ", in bed " + patient.InBed() : ""));
                if (feeder.jobs != null)
                {
                    feeder.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
            Note("Look: the meal in hand, held out to the patient's face; at the bed stood, on the ground knelt beside them, not inside them.");
        }

        // A patient in a bed in each state the game puts one in, and whoever sees to them: they should be stood at the
        // bed (bench), never knelt as on the floor (crouch). Looking for the case seen in play of a doctor kneeling at a
        // bed patient.
        private static IEnumerator TendBedTest()
        {
            string[] cases = { "awake", "asleep", "anesthetic", "painshock", "feed", "downed_on_bed", "hospital" };
            foreach (string c in cases)
            {
                ClearPatch();
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamed(c == "hospital" ? "HospitalBed" : "Bed");
                Thing made = ThingMaker.MakeThing(bedDef, bedDef.MadeFromStuff ? ThingDefOf.WoodLog : ThingDefOf.Steel);
                made.SetFaction(Faction.OfPlayer);
                Building_Bed bed = (Building_Bed)Spawn(made, middle + new IntVec3(1, 0, 0), Rot4.North);
                bed.Medical = true;
                Pawn patient = NewPawn(middle + new IntVec3(1, 0, c == "downed_on_bed" ? 0 : -1), null);
                Pawn doctor = NewDoctor(middle + new IntVec3(-2, 0, -1));
                if (c != "painshock")
                {
                    Wound(patient, 6);
                }
                else
                {
                    // (bruises enough for pain shock, awake; no bleeding to kill it meanwhile)
                    HediffDef bruise = DefDatabase<HediffDef>.GetNamed("Bruise");
                    int added = 0;
                    foreach (BodyPartRecord part in patient.health.hediffSet.GetNotMissingParts())
                    {
                        if (part.depth == BodyPartDepth.Outside && part.def.GetMaxHealth(patient) >= 15f && added < 16
                            && part.def.defName != "Head" && part.def.defName != "Neck")
                        {
                            Hediff_Injury hurt = (Hediff_Injury)HediffMaker.MakeHediff(bruise, patient, part);
                            hurt.Severity = part.def.GetMaxHealth(patient) * 0.35f;
                            patient.health.AddHediff(hurt);
                            added++;
                        }
                    }
                }
                if (patient.Dead)
                {
                    Check(c + ": patient alive", false, "died of its wounds");
                    continue;
                }
                if (c == "anesthetic" || c == "feed" || c == "hospital" || c == "downed_on_bed")
                {
                    HealthUtility.AdjustSeverity(patient, HediffDefOf.Anesthetic, 1f);
                }
                if (c != "downed_on_bed")
                {
                    patient.jobs.Notify_TuckedIntoBed(bed);
                }
                if (patient.needs.rest != null)
                {
                    patient.needs.rest.CurLevel = c == "asleep" ? 0.05f : 1f;
                }
                Thing meal = null;
                if (c == "feed")
                {
                    if (patient.needs.food != null)
                    {
                        patient.needs.food.CurLevel = 0.05f;
                    }
                    Thing m = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                    meal = Spawn(m, middle + new IntVec3(-3, 0, 1));
                }
                Look(patient, 0f);
                yield return WaitReal(2f);
                Dictionary<string, int> kinds = new Dictionary<string, int>();
                int atWork = 0;
                string fill = null;
                for (int i = 0; i < 80; i++)
                {
                    if (patient.needs.rest != null && c != "asleep")
                    {
                        patient.needs.rest.CurLevel = 1f;
                    }
                    if (c != "downed_on_bed" && !patient.InBed() && patient.Downed)
                    {
                        patient.jobs.Notify_TuckedIntoBed(bed);
                    }
                    JobDef want = c == "feed" ? JobDefOf.FeedPatient : JobDefOf.TendPatient;
                    if (doctor.CurJobDef != want)
                    {
                        Job job = c == "feed" ? JobMaker.MakeJob(JobDefOf.FeedPatient, meal, patient) : JobMaker.MakeJob(JobDefOf.TendPatient, patient);
                        job.count = 1;
                        job.playerForced = true;
                        if (c == "feed" && (meal == null || meal.Destroyed))
                        {
                            break;
                        }
                        doctor.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    yield return Ticks(15, "tendbed_" + c, doctor);
                    lookAt = Vector3.Lerp(doctor.DrawPos, patient.DrawPos, 0.5f);
                    string k = Field<string>(doctor, "workKind");
                    if (k != null && Field<float>(doctor, "work") > 0.5f)
                    {
                        kinds[k] = kinds.TryGetValue(k, out int n) ? n + 1 : 1;
                        atWork++;
                        if (atWork == 4)
                        {
                            yield return Shot(c);
                            fill = "patient " + patient.GetPosture() + ", in bed " + patient.InBed() + ", current bed "
                                + (patient.CurrentBed() != null ? patient.CurrentBed().def.defName : "none") + ", doctor job "
                                + doctor.CurJobDef?.defName + " A " + doctor.CurJob?.targetA + " B " + doctor.CurJob?.targetB
                                + ", lying " + WalkFigures.AutoLying(patient);
                        }
                    }
                    if (atWork > 12)
                    {
                        break;
                    }
                }
                string seen = string.Join(", ", kinds.Keys);
                Check(c + ": stood at the bed", atWork > 0 && !kinds.ContainsKey("crouch") && kinds.ContainsKey("bench"),
                    (atWork > 0 ? "work kinds " + seen : "never at work") + "; " + (fill ?? "patient " + patient.GetPosture() + ", in bed " + patient.InBed()
                    + ", doctor job " + (doctor.CurJobDef != null ? doctor.CurJobDef.defName : "none")));
                if (doctor.jobs != null)
                {
                    doctor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
            Note("Look: in every case the doctor stood at the bed's side working over the patient, not knelt on the floor.");
        }

        private static Pawn NewDoctor(IntVec3 at)
        {
            Pawn pawn = null;
            for (int i = 0; i < 30; i++)
            {
                pawn = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                if (!pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) && !pawn.WorkTagIsDisabled(WorkTags.Violent))
                {
                    break;
                }
            }
            pawn.equipment.DestroyAllEquipment();
            Spawn(pawn, at);
            pawn.Rotation = Rot4.South;
            return pawn;
        }

        // Cuts on the outside parts, none bleeding much, a few of each so a tend or two leaves more to do.
        private static void Wound(Pawn pawn, int count)
        {
            List<BodyPartRecord> parts = new List<BodyPartRecord>();
            foreach (BodyPartRecord part in pawn.health.hediffSet.GetNotMissingParts())
            {
                if (part.depth == BodyPartDepth.Outside && part.def.hitPoints >= 15)
                {
                    parts.Add(part);
                }
            }
            for (int i = 0; i < count && parts.Count > 0; i++)
            {
                Hediff_Injury cut = (Hediff_Injury)HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed(i % 2 == 0 ? "Cut" : "Bruise"), pawn, parts[(i * 7) % parts.Count]);
                cut.Severity = 2f;
                pawn.health.AddHediff(cut);
            }
        }

        private static int Untended(Pawn pawn)
        {
            int n = 0;
            foreach (Hediff h in pawn.health.hediffSet.hediffs)
            {
                if (h.TendableNow())
                {
                    n++;
                }
            }
            return n;
        }

        // Each bow (Bow_Short, Bow_Recurve, Bow_Great, Biotech's Flamebow): drafted holding it, then shooting at a wall
        // (AttackStatic, as a forced attack), drawn from nock_ to aim_ over the warm-up and loosed into fire_. Under
        // Combat Extended the bow is given its arrows. bows_<defName> for one, bows_parka for all of them in a parka. One archer shoots them all.
        private static IEnumerator BowTest(string only)
        {
            Pawn pawn = null;
            Thing wall = null;
            bool parka = only == "parka", parkaOn = false;
            if (parka)
            {
                only = null;
            }
            foreach (string bow in new[] { "Bow_Short", "Bow_Recurve", "Bow_Great", "Flamebow" })
            {
                if (only != null && only != bow)
                {
                    continue;
                }
                if (DefDatabase<ThingDef>.GetNamedSilentFail(bow) == null)
                {
                    Note(bow + ": not loaded, skipped.");
                    continue;
                }
                // (one archer for every bow, so they are compared on the same body and clothes; parka: in a parka, as the
                // arms are pushed out past a thick garment, ArmsOut)
                if (pawn == null || !pawn.Spawned)
                {
                    ClearPatch();
                    pawn = NewPawn(middle, null);
                    if (pawn.story != null)
                    {
                        pawn.story.bodyType = BodyTypeDefOf.Male;
                    }
                    pawn.apparel.DestroyAll();
                    foreach (string worn in new[] { "Apparel_BasicShirt", "Apparel_Pants" })
                    {
                        pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(ThingDef.Named(worn), ThingDefOf.Cloth));
                    }
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                    wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                    wall.SetFaction(Faction.OfPlayer);
                    wall = Spawn(wall, middle + new IntVec3(0, 0, -7));   // (south, so the shooter faces the camera's side)
                }
                if (parka && !parkaOn)
                {
                    pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(ThingDef.Named("Apparel_Parka"), ThingDefOf.Cloth));
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                    parkaOn = true;
                }
                pawn.drafter.Drafted = false;
                pawn.equipment.DestroyAllEquipment();
                pawn.inventory.innerContainer.ClearAndDestroyContents();
                pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(ThingDef.Named(bow)));
                string ammo = GiveAmmo(pawn);
                pawn.jobs.StopAll();
                yield return Ticks(30, bow + "_swap", pawn);
                pawn.drafter.Drafted = true;
                Look(pawn, 0f);
                yield return WaitReal(1f);
                // (the draw from the holster runs on frame time times the game's speed, StepDraw, which is none while the
                // test steps the game paused: so run it at normal speed until the bow is in the hands)
                yield return Unpaused(pawn, () => Field<float>(pawn, "drawAt") < 0f && Field<bool>(pawn, "handHeld"), 4f);
                float drafted = 0f;
                string stance = null;
                for (int i = 0; i < 8; i++)
                {
                    yield return Ticks(15, bow + "_hold", pawn);
                    drafted = Mathf.Max(drafted, Field<float>(pawn, "drafted"));
                    stance = Field<string>(pawn, "stance") ?? stance;
                }
                yield return Shot(bow + "_hold");
                Note(bow + ": " + WalkFigures.AutoWeapon(pawn));
                lookDistance = 2.2f;
                lookHeight = 0.75f;
                lookYaw = 90f;
                yield return Shot(bow + "_hold_close");
                lookDistance = LookDistance;
                lookHeight = LookHeight;
                lookYaw = 0f;
                Check(bow + ": drafted stance", drafted >= 0.99f && stance != null && stance.StartsWith("drafted_"),
                    "drafted " + drafted.ToString("F2") + ", stance " + (stance ?? "none") + ", held " + (pawn.equipment.Primary != null ? pawn.equipment.Primary.def.defName : "nothing"));
                Verb verb = pawn.equipment.PrimaryEq != null ? pawn.equipment.PrimaryEq.PrimaryVerb : null;
                int lastShot = verb != null ? verb.LastShotTick : -1;
                int shots = 0;
                float aim = 0f;
                string aimName = null;
                bool warmShot = false, fireShot = false;
                for (int i = 0; i < 160; i++)
                {
                    if (pawn.CurJobDef != JobDefOf.AttackStatic && pawn.CurJobDef?.defName != "ReloadWeapon")
                    {
                        Job job = JobMaker.MakeJob(JobDefOf.AttackStatic, wall);
                        job.playerForced = true;
                        pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    yield return Ticks(5, bow + "_fire", pawn);
                    Look(pawn, 0f);
                    aim = Mathf.Max(aim, Field<float>(pawn, "aim"));
                    aimName = Field<string>(pawn, "aimName") ?? aimName;
                    if (verb != null && verb.LastShotTick != lastShot)
                    {
                        lastShot = verb.LastShotTick;
                        shots++;
                        if (!fireShot)
                        {
                            fireShot = true;
                            yield return Shot(bow + "_loosed");
                        }
                    }
                    if (!warmShot && pawn.stances.curStance is Stance_Warmup warm && warm.ticksLeft < 20)
                    {
                        warmShot = true;
                        yield return Shot(bow + "_drawn");
                        Note(bow + " aiming: " + WalkFigures.AutoWeapon(pawn));
                        lookDistance = 2.2f;
                        lookHeight = 0.75f;
                        lookYaw = 90f;
                        yield return Shot(bow + "_drawn_close");
                        lookDistance = LookDistance;
                        lookHeight = LookHeight;
                        lookYaw = 0f;
                    }
                    if (shots >= 3)
                    {
                        break;
                    }
                }
                Check(bow + ": aimed", aim >= 0.99f && aimName != null, "aim " + aim.ToString("F2") + ", aim clips " + (aimName ?? "none"));
                Check(bow + ": shooting", shots >= 2, shots + " shots, job " + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none")
                    + ", stance " + pawn.stances.curStance?.GetType().Name + (ammo != null ? ", ammo " + ammo : ""));
                pawn.drafter.Drafted = false;
                pawn.jobs.StopAll();
            }
            Note("Look: drafted the bow held low in front in the left hand; shooting side on, the string drawn back to the face (drawn), the arrow loosed (loosed); the bow model in the left fist throughout.");
        }

        // Combat Extended's arrows (or whatever ammo the weapon takes) put in the inventory, found by reflection so CE is
        // not referenced; null without it.
        private static string GiveAmmo(Pawn pawn)
        {
            ThingWithComps weapon = pawn.equipment.Primary;
            if (weapon == null)
            {
                return null;
            }
            foreach (ThingComp comp in weapon.AllComps)
            {
                if (comp.GetType().Name != "CompAmmoUser")
                {
                    continue;
                }
                object props = comp.props;
                object set = props.GetType().GetField("ammoSet")?.GetValue(props);
                IList types = set?.GetType().GetField("ammoTypes")?.GetValue(set) as IList;
                if (types == null || types.Count == 0)
                {
                    return "none in its set";
                }
                ThingDef ammo = types[0].GetType().GetField("ammo")?.GetValue(types[0]) as ThingDef;
                if (ammo == null)
                {
                    return "unreadable";
                }
                Thing stack = ThingMaker.MakeThing(ammo);
                stack.stackCount = 60;
                pawn.inventory.innerContainer.TryAdd(stack);
                return ammo.defName;
            }
            return null;
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
            bool blankets = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = true;   // (pillow talk lies close under the blanket as lovin does)
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
            SettingsMod.Settings.bedBlankets = blankets;
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
            // (spouses for pillow talk, as an ideoligion may not let lovers share a bed, and the game refuses the bed)
            PawnRelationDef couple = date == "pillow" ? PawnRelationDefOf.Spouse : PawnRelationDefOf.Lover;
            if (!a.relations.DirectRelationExists(couple, b))
            {
                a.relations.AddDirectRelation(couple, b);
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
                    yaw = 200f;   // (from the far side of the piano, so the listener's face shows)
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
                    if (furnished)
                    {
                        BuildReadingRoom();
                    }
                    if (furnished)
                    {
                        // (bare heads, as the faces are the point: quicktest colonists often wear the same hat)
                        foreach (Pawn p in new[] { a, b })
                        {
                            foreach (Apparel worn in new List<Apparel>(p.apparel.WornApparel))
                            {
                                if (worn.def.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.UpperHead)
                                    || worn.def.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead))
                                {
                                    p.apparel.Remove(worn);
                                    worn.Destroy();
                                }
                            }
                        }
                    }
                    if (furnished)
                    {
                        // (the map's own colonists kept out of the picture: one came in to plant the pot)
                        foreach (Pawn other in new List<Pawn>(map.mapPawns.FreeColonistsSpawned))
                        {
                            if (other != a && other != b && other.Position.InHorDistOf(middle, Clear + 8))
                            {
                                other.Position = CellFinder.RandomClosewalkCellNear(middle + new IntVec3(0, 0, Clear + 14), map, 4);
                                other.Notify_Teleported();
                            }
                            if (other != a && other != b)
                            {
                                Hold(other);
                            }
                        }
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
                    yaw = 180f;   // (from across the table, so the book and the faces show)
                    break;
                }
            }
            if (lordJob == null)
            {
                Check(date + ": lord job made", false, "constructor not found");
                yield break;
            }
            // (a kiss's and a shared reading's lord has the initiator alone, as the mod's workers make them: the kiss is
            // called off for a recipient in a lord, the reading once the reader leaves its Reading job)
            LordMaker.MakeNewLord(Faction.OfPlayer, (LordJob)lordJob, map, date == "kiss" || date == "shareread" ? new[] { a } : new[] { a, b });
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
            float kissed = 0f, kissedB = 0f, pillowA = 0f, pillowB = 0f, looked = 0f;
            int throwsA = 0, throwsB = 0;
            float listened = 0f, listenLook = 0f, shown = 0f;
            string shownSide = null;
            string played = null;
            int seated = 0;
            int steps = date == "kiss" ? 40 : date == "shareread" ? (furnished ? 300 : 110) : 60;   // (shared reading: the watcher stares 500 ticks before it sits)
            for (int i = 0; i < steps; i++)
            {
                yield return Ticks(15, "romance_" + date, a);
                Probe("romance_" + date, b);
                seenA.Add((a.CurJobDef != null ? a.CurJobDef.defName : "none") + "/" + a.GetPosture());
                seenB.Add((b.CurJobDef != null ? b.CurJobDef.defName : "none") + "/" + b.GetPosture());
                interacted |= Traverse.Create(lordJob).Field("Interacted").GetValue<bool>();
                if (Field<string>(a, "eatClip") == "kiss")
                {
                    kissed = Mathf.Max(kissed, Field<float>(a, "eat"));
                }
                if (Field<string>(b, "eatClip") == "kiss")
                {
                    kissedB = Mathf.Max(kissedB, Field<float>(b, "eat"));
                }
                pillowA = Mathf.Max(pillowA, Field<float>(a, "lovinShift"));
                pillowB = Mathf.Max(pillowB, Field<float>(b, "lovinShift"));
                looked = Mathf.Max(looked, Mathf.Abs(Field<float>(a, "look")));
                listened = Mathf.Max(listened, Field<float>(b, "listen"));
                listenLook = Mathf.Max(listenLook, Mathf.Abs(Field<float>(b, "look")));
                shown = Mathf.Max(shown, Field<float>(b, "readShow"));
                shownSide = Field<string>(b, "readShowSide") ?? shownSide;
                throwsA += Field<string>(a, "tossClip") == "snowball" && Field<float>(a, "tossAt") >= 0f ? 1 : 0;
                throwsB += Field<string>(b, "tossClip") == "snowball" && Field<float>(b, "tossAt") >= 0f ? 1 : 0;
                if (Field<float>(a, "work") > 0.5f)
                {
                    played = Field<string>(a, "workKind") ?? played;
                }
                lookAt = Vector3.Lerp(a.DrawPos, b.DrawPos, 0.5f);
                if (furnished)
                {
                    // For stills: every other step once the watcher sits beside the reader, from four ways round in turn.
                    if (a.Position == middle + new IntVec3(1, 0, 0) && i % 2 == 0)
                    {
                        lookYaw = StillYaws[seated++ % StillYaws.Length];
                        yield return Shot(date + "_t" + (i + 1) * 15 + "_y" + lookYaw.ToString("F0"));
                        if (seated >= 20)
                        {
                            break;
                        }
                    }
                }
                else if (i % 20 == 19)
                {
                    yield return Shot(date + "_t" + (i + 1) * 15);
                }
            }
            Check(date + ": the date got going", interacted, "A " + string.Join(", ", seenA) + "; B " + string.Join(", ", seenB));
            switch (date)
            {
                case "kiss":
                    Check("kiss: both kiss", kissed > 0.9f && kissedB > 0.9f, "kiss weight " + kissed.ToString("F2") + " / " + kissedB.ToString("F2"));
                    break;
                case "music":
                    Check("music: the player at the keys", played == "piano", "work kind " + (played ?? "none"));
                    Check("music: the listener steps in", listened > 0.9f, "step weight " + listened.ToString("F2") + ", head turned up to " + listenLook.ToString("F0") + " degrees");
                    break;
                case "pillow":
                    Check("pillow: close under the blanket", pillowA > 0.1f && pillowB > 0.1f, "shift " + pillowA.ToString("F2") + " / " + pillowB.ToString("F2"));
                    break;
                case "snowball":
                    Check("snowball: throwing", throwsA > 0 && throwsB > 0, "frames mid-throw (of every 15 ticks) " + throwsA + " / " + throwsB);
                    break;
                case "shareread":
                    Check("shareread: looks at the book", looked > 10f, "head turned up to " + looked.ToString("F0") + " degrees");
                    Check("shareread: the reader holds the book out", shown > 0.9f, "show weight " + shown.ToString("F2") + " to the " + (shownSide ?? "none"));
                    break;
            }
            foreach (Pawn p in new[] { a, b })
            {
                if (p.jobs != null && p.Spawned)
                {
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
        }

        // Romance on the Rim's shared reading in a furnished room rather than on the bare ground, for Workshop stills:
        // the same table and chairs as romance_shareread, with a wood floor, walls on three sides (the camera's, north,
        // left open) and a bookcase, lamp, plant and armchair, and the camera a little closer.
        private static bool furnished;
        private static readonly float[] StillYaws = { 160f, 205f, 135f, 180f };   // (the second picture of each 90 more)

        private static IEnumerator ReadRoomTest()
        {
            furnished = true;
            float distance = lookDistance, height = lookHeight, pitch = lookPitch;
            lookDistance = 3.2f;
            lookHeight = 0.6f;
            lookPitch = 24f;
            // Interaction Bubbles' speech bubbles are drawn into the scene image: off meanwhile (its play settings toggle).
            FieldInfo bubbles = AccessTools.Field(AccessTools.TypeByName("Bubbles.Settings"), "Activated");
            object bubblesWere = bubbles?.GetValue(null);
            bubbles?.SetValue(null, false);
            IEnumerator run = RomanceTest(new[] { "shareread" });
            while (run.MoveNext())
            {
                yield return run.Current;
            }
            lookDistance = distance;
            lookHeight = height;
            lookPitch = pitch;
            furnished = false;
            if (bubbles != null)
            {
                bubbles.SetValue(null, bubblesWere);
            }
            Note("Look: Workshop stills of the shared reading indoors; pick the best of the pictures from t480 on.");
        }

        // The room round the reading table (the table at +(0..1, 1..2), the chairs at +(0, 0) and +(1, 0)): inside x -3..4,
        // z -2..4 from the middle, walls round it but on the north. The floor is put back by CleanUp.
        private static void BuildReadingRoom()
        {
            TerrainDef floor = DefDatabase<TerrainDef>.GetNamedSilentFail("WoodPlankFloor");
            for (int x = -3; x <= 4; x++)
            {
                for (int z = -2; z <= 5; z++)
                {
                    IntVec3 c = middle + new IntVec3(x, 0, z);
                    if (c.InBounds(map))
                    {
                        map.zoneManager.ZoneAt(c)?.RemoveCell(c);   // (a zone's tiles are drawn in the pictures)
                    }
                    if (floor != null && c.InBounds(map))
                    {
                        floors.Add(new KeyValuePair<IntVec3, TerrainDef>(c, c.GetTerrain(map)));
                        map.terrainGrid.SetTerrain(c, floor);
                    }
                }
            }
            for (int x = -4; x <= 5; x++)
            {
                Build("Wall", ThingDefOf.WoodLog, new IntVec3(x, 0, -3), Rot4.North);
            }
            for (int z = -2; z <= 5; z++)
            {
                Build("Wall", ThingDefOf.WoodLog, new IntVec3(-4, 0, z), Rot4.North);
                Build("Wall", ThingDefOf.WoodLog, new IntVec3(5, 0, z), Rot4.North);
            }
            Build("Bookcase", ThingDefOf.WoodLog, new IntVec3(-2, 0, -2), Rot4.North);
            Build("StandingLamp", null, new IntVec3(3, 0, -2), Rot4.North);
            Build("EndTable", ThingDefOf.WoodLog, new IntVec3(-3, 0, -2), Rot4.North);
            Build("Armchair", ThingDefOf.Cloth, new IntVec3(4, 0, 0), Rot4.West);
            Thing pot = Build("PlantPot", ThingDefOf.WoodLog, new IntVec3(-3, 0, 3), Rot4.North);
            ThingDef rose = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rose");
            if (pot != null && rose != null)
            {
                Plant plant = (Plant)ThingMaker.MakeThing(rose);
                plant.Growth = 1f;
                Spawn(plant, pot.Position);
            }
        }

        private static readonly List<KeyValuePair<IntVec3, TerrainDef>> floors = new List<KeyValuePair<IntVec3, TerrainDef>>();

        private static Thing Build(string defName, ThingDef stuff, IntVec3 offset, Rot4 rot)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            IntVec3 at = middle + offset;
            if (def == null || !at.InBounds(map) || at.GetEdifice(map) != null)
            {
                return null;
            }
            Thing made = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            made.SetFaction(Faction.OfPlayer);
            return Spawn(made, at, rot);
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
                    // A tick at a time, so a short stop's stroke (eased in only while still) is seen at its peak.
                    for (int j = 0; j < 15 && pawn.CurJobDef == cuddle; j++)
                    {
                        yield return Ticks(1, "cuddle_" + where, pawn);
                        stroked = Mathf.Max(stroked, Field<float>(pawn, "play"));
                    }
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
            foreach (KeyValuePair<IntVec3, TerrainDef> f in floors)
            {
                map.terrainGrid.SetTerrain(f.Key, f.Value);
            }
            floors.Clear();
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

        // The game at normal speed (for what moves on frame time, which is none while paused) until done says so or the
        // real seconds are up, then paused again.
        private static IEnumerator Unpaused(Pawn pawn, Func<bool> done, float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            yield return null;
            while (Time.realtimeSinceStartup < until && !done())
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                yield return null;
                Probe("unpaused", pawn);
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

    public sealed partial class SkinnedFigure
    {
        // A hat worn over the hair, in the base shape: whether the hair is left whole under it, and how far each of the
        // hat's places is outside the hair's outermost on its line from the head's middle once pushed (the least, cells),
        // and the most it was pushed.
        internal string OverHairReport(ThingDef def, HairDef style)
        {
            int g = GarmentOf(def);
            if (g < 0)
            {
                return null;
            }
            Piece hair = Hair(style);
            if (hair == null)
            {
                return "no hair built";
            }
            GarmentMask mask = GarmentBit(g);
            bool whole = WornHair(hair, mask, null) == hair;
            Vector3[] pushed = PushedOver(pieces[g].rest, faceCentre, hair.rest, hair.tris);
            float least = float.PositiveInfinity, most = 0f;
            for (int i = 0; i < pushed.Length; i++)
            {
                Vector3 way = pushed[i] - faceCentre;
                float r = way.magnitude;
                float outer = OutermostAlong(faceCentre, way / r, hair.rest, hair.tris, r + OverHairReach);
                least = Mathf.Min(least, r - outer);
                most = Mathf.Max(most, (pushed[i] - pieces[g].rest[i]).magnitude);
            }
            bool ok = whole && pieces[g].overHair && least >= 0f;
            return (ok ? "ok" : "BAD") + ": over_hair " + pieces[g].overHair + ", hair whole " + whole + ", least clear " + least.ToString("F3")
                + " cells, pushed up to " + most.ToString("F3") + " cells";
        }

        // The apparel each hat that tucks the hair is drawn for (its first loaded def).
        internal List<string> Tuckers()
        {
            List<string> all = new List<string>();
            for (int g = 1; g < pieces.Length; g++)
            {
                if (pieces[g].hideHair == 2 && pieces[g].defs != null)
                {
                    foreach (string d in pieces[g].defs)
                    {
                        if (DefDatabase<ThingDef>.GetNamedSilentFail(d) != null)
                        {
                            all.Add(d);
                            break;
                        }
                    }
                }
            }
            return all;
        }

        // A hat's rim both ways (before the slicecap's fix and now): the ways round that differ, the ways with no rim, the
        // lowest share of the skin's reach a measured rim point has (RimCrown), and how many body triangles it hides.
        internal string RimReport(ThingDef def)
        {
            int g = GarmentOf(def);
            if (g < 0)
            {
                return "no garment";
            }
            Rim old = MeasureRim(pieces[g].rest, pieces[g].tris, true), now = MeasureRim(pieces[g].rest, pieces[g].tris, false);
            int differ = 0, emptyOld = 0, emptyNow = 0;
            string moved = "";
            float least = float.PositiveInfinity;
            for (int b = 0; b < RimBins; b++)
            {
                bool same = old.height[b] == now.height[b] && old.radius[b] == now.radius[b];
                differ += same ? 0 : 1;
                if (!same && !float.IsPositiveInfinity(now.height[b]))
                {
                    moved += " " + (b * 360 / RimBins) + "°: " + (old.height[b] - faceCentre.y).ToString("F3") + " -> " + (now.height[b] - faceCentre.y).ToString("F3");
                }
                emptyOld += float.IsPositiveInfinity(old.height[b]) ? 1 : 0;
                emptyNow += float.IsPositiveInfinity(now.height[b]) ? 1 : 0;
                if (!float.IsPositiveInfinity(old.height[b]))
                {
                    least = Mathf.Min(least, old.radius[b] / Mathf.Max(SkinMost(b), 1e-4f));
                }
            }
            return "rim ways differing " + differ + " of " + RimBins + ", empty " + emptyOld + " -> " + emptyNow
                + ", least reach share " + least.ToString("F2") + ", hides " + (pieces[g].hides != null ? pieces[g].hides.Length : 0)
                + " (hash " + HideHash(pieces[g].hides) + ")" + (moved.Length > 0 ? "; moved (cells over the head's middle):" + moved : "");
        }

        private static int HideHash(int[] hides)
        {
            int h = 17;
            foreach (int k in hides ?? new int[0])
            {
                h = h * 31 + k;
            }
            return h;
        }

        internal string FarHides(ThingDef def, float reach)
        {
            int g = GarmentOf(def);
            if (g < 0)
            {
                return null;
            }
            Piece body = pieces[0], cloth = pieces[g];
            int[] hides = cloth.hides ?? new int[0];
            int far = 0;
            float farthest = 0f;
            foreach (int k in hides)
            {
                Vector3 mid = (body.rest[body.tris[3 * k]] + body.rest[body.tris[3 * k + 1]] + body.rest[body.tris[3 * k + 2]]) / 3f;
                float near = float.PositiveInfinity;
                foreach (Vector3 v in cloth.rest)
                {
                    near = Mathf.Min(near, (v - mid).magnitude);
                }
                farthest = Mathf.Max(farthest, near);
                far += near > reach ? 1 : 0;
            }
            return far + " of " + hides.Length + " far, farthest " + farthest.ToString("F3") + " cells";
        }
    }

    public static partial class WalkFigures
    {
        // The figure's state as AutoTest probes it: one line of the Ease fields the scenarios look at.
        internal static string AutoWeapon(Pawn pawn)
        {
            ThingWithComps w = pawn.equipment != null ? pawn.equipment.Primary : null;
            if (w == null)
            {
                return "no weapon";
            }
            SkinnedFigure.Hold hold = HoldOf(WeaponName(w.def), AsSlab(w));
            return "slab " + AsSlab(w) + ", name " + WeaponName(w.def) + ", kind " + KindOf(w.def) + ", drafted clip " + DraftedClip(pawn)
                + ", aim clips " + AimName(pawn) + ", own hold " + (hold != null) + ", fitted to " + FitArtOf(w);
        }

        private static string FitArtOf(ThingWithComps w)
        {
            Material mat = w.Graphic != null ? w.Graphic.MatSingleFor(w) : null;
            Material art = mat != null ? FitArt(w.def, mat) : null;
            if (art == null || art.mainTexture == null)
            {
                return "nothing";
            }
            return (art == mat ? "its own sprite " : "the game's art ") + art.mainTexture.width + "x" + art.mainTexture.height
                + (mat.mainTexture != null ? " (sprite " + mat.mainTexture.width + "x" + mat.mainTexture.height + ")" : "");
        }

        internal static string AutoLying(Pawn pawn)
        {
            Ease e;
            if (!eases.TryGetValue(pawn.thingIDNumber, out e))
            {
                return "not drawn";
            }
            return "posture " + pawn.GetPosture() + ", downed " + pawn.Downed + ", asleep " + !pawn.Awake() + ", in bed " + pawn.InBed()
                + ", lying clip " + LieClip(pawn, pawn.Dead, pawn.InBed(), false) + ", ragdoll " + (e.doll != null) + ", fall " + (e.fallClip ?? "none") + " " + e.fallen.ToString("F2");
        }

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
                + " drafted=" + e.drafted.ToString("F2") + " stance=" + (e.stance ?? "-") + " handHeld=" + e.handHeld + " drawAt=" + e.drawAt.ToString("F2")
                + " drawClip=" + (e.drawClip ?? "-") + " aim=" + e.aim.ToString("F2") + " aimName=" + (e.aimName ?? "-")
                + " shows=" + WeaponShows(pawn) + " draftedP=" + (pawn.Drafted)
                + " eatClip=" + (e.eatClip ?? "-") + " eat=" + e.eat.ToString("F2") + " look=" + e.look.ToString("F1")
                + " share=" + e.share.ToString("F2") + " readShow=" + e.readShow.ToString("F2") + " rot=" + pawn.Rotation.AsInt
                + " at=" + pawn.Position + " toil=" + (pawn.jobs != null && pawn.jobs.curDriver != null ? pawn.jobs.curDriver.CurToilString : "-")
                + PetProbe(pawn);
        }

        // A carried animal's drawn back (IsoRim's Api.ReadHost), cells, for fitting the cuddle.
        private static string PetProbe(Pawn pawn)
        {
            Thing load = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            return load is Pawn animal ? " petBack=" + PetBackOf(animal).ToString("F3") + " size=" + SizeOf(pawn).ToString("F2") : "";
        }

        internal static string AutoOverHair(ThingDef def, HairDef style)
        {
            return skin != null ? skin.OverHairReport(def, style) : null;
        }

        internal static List<string> AutoTuckers()
        {
            return skin != null ? skin.Tuckers() : new List<string>();
        }

        internal static string AutoRim(ThingDef def)
        {
            return skin != null ? skin.RimReport(def) : "no figure";
        }

        // How many of the body triangles the garment for this apparel hides have no point of the garment within reach
        // (cells, at rest), of how many it hides, and the farthest.
        internal static string AutoFarHides(ThingDef def, float reach)
        {
            return skin != null ? skin.FarHides(def, reach) : null;
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
