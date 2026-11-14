#if AUTOTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private static bool poseOff;   // the test's camera pose lets go of the view (Pose gives none)
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
            FaceLayers.StackSize = Math.Min(8, Math.Max(1, IntArg("m3atStackSize", 8)));
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
            int? seed = IsoRim.Api.Version >= 72 ? HarnessSeedOrNull() : null;
            report.AppendLine(seed.HasValue ? "Seed: " + seed.Value + " (IsoRim's -isorimseed: the world, colonists and every tick's random numbers the same each run)."
                : "Seed: none (IsoRim older than Api 72, or no -isorimseed): the world and colonists differ each run.").AppendLine();
            probe = new StreamWriter(Path.Combine(outDir, "probe.txt"));
            if (wanted.Count == 1 && (wanted[0] == "shots" || wanted[0] == "perf"))
            {
                yield return wanted[0] == "perf" ? Perf() : Shots();
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

        // ---- perf: the frame time with the chibi, with IsoRim's own figures (as without this mod) and with no person figures
        // at all, on a loaded save run on at normal speed, the camera following its colonists. Each is measured in turn,
        // round after round, so whatever drifts over the run (pawns walking off, the time of day) falls on all three alike.
        // Nothing is spawned or changed in the save.

        // One thing measured: which figures (WalkFigures.PerfMode), how many colonists drawn as the chibi (-1 all; the rest are
        // left to IsoRim, whose figures cost next to nothing) and whether fewerPoses is on.
        private sealed class PerfCondition
        {
            public string name;
            public int mode, chibis = -1;
            public bool fewer, unstacked;
            public List<float> frames = new List<float>();
            public double drawerMs;
            public long draws;
            public long faces, faceChanges, layers;
        }

        // -m3atPerfCounts=0,5,10,20f: the chibi on that many colonists (f: with fewerPoses on); else the chibi, IsoRim's figures
        // and none.
        private static List<PerfCondition> PerfConditions()
        {
            List<PerfCondition> list = new List<PerfCondition>();
            string counts;
            if (GenCommandLine.TryGetCommandLineArg("m3atPerfCounts", out counts) && !string.IsNullOrEmpty(counts))
            {
                foreach (string raw in counts.Split(','))
                {
                    string t = raw.Trim();
                    bool fewer = t.Contains("f"), unstacked = t.Contains("n");
                    int n;
                    if (int.TryParse(t.TrimEnd('f', 'n'), out n))
                    {
                        list.Add(new PerfCondition { name = n + " chibis" + (fewer ? ", fewer poses" : "") + (unstacked ? ", a draw per face layer" : ""), mode = 0, chibis = n, fewer = fewer, unstacked = unstacked });
                    }
                }
                return list;
            }
            list.Add(new PerfCondition { name = "chibi (M3AT)", mode = 0 });
            list.Add(new PerfCondition { name = "IsoRim's figures", mode = 1 });
            list.Add(new PerfCondition { name = "no person figures", mode = 2 });
            return list;
        }

        // Puts a condition in place: the mode, the colonists drawn as the chibi (those nearest the group's middle) and the option.
        private static void SetPerf(PerfCondition c, List<Pawn> colonists, bool fewerWas)
        {
            WalkFigures.PerfMode = c.mode;
            SettingsMod.Settings.fewerPoses = c.fewer;
            FaceLayers.StackLayers = !c.unstacked;
            if (c.chibis < 0)
            {
                WalkFigures.PerfAllowed = null;
                return;
            }
            Vector3 mid = Centroid(colonists, out float spread);
            List<Pawn> near = colonists.FindAll(p => p.Spawned && p.Map == map);
            near.Sort((a, b) => (a.DrawPos - mid).sqrMagnitude.CompareTo((b.DrawPos - mid).sqrMagnitude));
            WalkFigures.PerfAllowed = new HashSet<int>();
            for (int i = 0; i < c.chibis && i < near.Count; i++)
            {
                WalkFigures.PerfAllowed.Add(near[i].thingIDNumber);
            }
        }

        private static IEnumerator Perf()
        {
            int seconds = IntArg("m3atPerfSeconds", 20), rounds = IntArg("m3atPerfRounds", 3);
            List<Pawn> colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            List<PerfCondition> conditions = PerfConditions();
            bool fewerWas = SettingsMod.Settings.fewerPoses;
            lookPitch = 40f;
            lookYaw = 0f;
            lookHeight = 0f;
            lookAt = Centroid(colonists, out float spread);
            lookDistance = Mathf.Clamp(spread * 2.4f, 10f, 40f);
            pose = IsoRim.Api.PushViewPose(PoseOwner, Pose, new IsoRim.ViewPoseOptions { Priority = 1000 });
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            report.AppendLine("## perf").AppendLine();
            report.AppendLine("- " + colonists.Count + " colonists followed by the camera (" + map.mapPawns.AllPawnsSpawned.Count + " pawns on the map), "
                + lookDistance.ToString("F0") + " cells off, pitched " + lookPitch + "°; normal speed; " + rounds + " rounds of " + seconds + " s per condition, after 3 s to settle each");
            // Warmed up first in each: hair built, IsoRim's figure looks made.
            for (int c = 0; c < conditions.Count; c++)
            {
                SetPerf(conditions[c], colonists, fewerWas);
                yield return FollowFor(colonists, c == 0 ? 15f : 5f, null);
            }
            // (Asked once the chibi has been drawn: GPU skinning also wants our materials, made at the first draw.)
            report.AppendLine("- screen " + Screen.width + "x" + Screen.height + ", GPU skinning " + SkinnedFigure.GpuSkinning);
            for (int r = 0; r < rounds; r++)
            {
                foreach (PerfCondition c in conditions)
                {
                    SetPerf(c, colonists, fewerWas);
                    yield return FollowFor(colonists, 3f, null);
                    WalkFigures.PerfTicks = 0;
                    WalkFigures.PerfDraws = 0;
                    long faces = FaceLayers.PerfFaces, changes = FaceLayers.PerfFaceChanges, layers = FaceLayers.PerfLayers;
                    int before = c.frames.Count;
                    yield return FollowFor(colonists, seconds, c.frames);
                    c.drawerMs += WalkFigures.PerfTicks * 1000.0 / Stopwatch.Frequency;
                    c.draws += WalkFigures.PerfDraws;
                    c.faces += FaceLayers.PerfFaces - faces;
                    c.faceChanges += FaceLayers.PerfFaceChanges - changes;
                    c.layers += FaceLayers.PerfLayers - layers;
                    Log.Message(Tag + "perf round " + (r + 1) + " " + c.name + ": " + Summary(c.frames.GetRange(before, c.frames.Count - before)));
                }
            }
            WalkFigures.PerfMode = 0;
            WalkFigures.PerfAllowed = null;
            FaceLayers.StackLayers = true;
            SettingsMod.Settings.fewerPoses = fewerWas;
            Pause();
            report.AppendLine().AppendLine("| condition | frames | mean ms | median ms | p95 ms | p99 ms | FPS (mean) | in our drawer, ms per frame | chibis drawn per frame |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (PerfCondition c in conditions)
            {
                List<float> f = c.frames;
                f.Sort();
                float mean = Mean(f);
                int n = Math.Max(1, f.Count);
                report.AppendLine("| " + c.name + " | " + f.Count + " | " + mean.ToString("F2") + " | " + Pct(f, 0.5f).ToString("F2") + " | " + Pct(f, 0.95f).ToString("F2")
                    + " | " + Pct(f, 0.99f).ToString("F2") + " | " + (mean > 0f ? 1000f / mean : 0f).ToString("F1")
                    + " | " + (c.drawerMs / n).ToString("F2") + " | " + ((double)c.draws / n).ToString("F1") + " |");
            }
            report.AppendLine();
            if (conditions.Count == 3 && conditions[1].mode == 1)
            {
                float a = Mean(conditions[0].frames), b = Mean(conditions[1].frames), z = Mean(conditions[2].frames);
                float ma = Pct(conditions[0].frames, 0.5f), mb = Pct(conditions[1].frames, 0.5f), mz = Pct(conditions[2].frames, 0.5f);
                report.AppendLine("- the chibi over IsoRim's figures: " + Delta(a, b, ma, mb));
                report.AppendLine("- the chibi over no figures: " + Delta(a, z, ma, mz));
                report.AppendLine("- IsoRim's figures over no figures: " + Delta(b, z, mb, mz));
            }
            else
            {
                PerfFit(conditions, false);
                PerfFit(conditions, true);
            }
            foreach (PerfCondition c in conditions)
            {
                if (c.faces > 0)
                {
                    report.AppendLine("- " + c.name + ": faces drawn " + c.faces + ", " + (100.0 * c.faceChanges / c.faces).ToString("F1") + "% of them changed from the same pawn's last ("
                        + ((double)c.layers / c.faces).ToString("F1") + " layers a face)");
                }
            }
            report.AppendLine("- (the mod's other work runs throughout: its tick patches, map components and freecam, which are small)");
        }

        // A straight line through mean frame time against chibis drawn per frame, for the conditions with fewerPoses so: the
        // cost of a chibi on screen (its slope), its CPU share in our drawer, and the frame without any.
        private static void PerfFit(List<PerfCondition> conditions, bool fewer)
        {
            List<PerfCondition> cs = conditions.FindAll(c => c.fewer == fewer || (fewer && c.chibis == 0));
            if (cs.Count < 2 || !cs.Exists(c => c.fewer == fewer && c.chibis > 0))
            {
                return;
            }
            double sx = 0, sy = 0, sxx = 0, sxy = 0, sd = 0, sdx = 0;
            foreach (PerfCondition c in cs)
            {
                double x = (double)c.draws / Math.Max(1, c.frames.Count), y = Mean(c.frames), d = c.drawerMs / Math.Max(1, c.frames.Count);
                sx += x; sy += y; sxx += x * x; sxy += x * y; sd += d; sdx += d * x;
            }
            int k = cs.Count;
            double slope = (k * sxy - sx * sy) / Math.Max(1e-9, k * sxx - sx * sx), intercept = (sy - slope * sx) / k;
            double cpu = sdx / Math.Max(1e-9, sxx);   // our drawer's time is none at none drawn: a line through 0
            report.AppendLine("- fewer poses " + (fewer ? "on" : "off") + ": **" + slope.ToString("F3") + " ms a frame per chibi on screen** (frame "
                + intercept.ToString("F2") + " ms with none), of which about " + cpu.ToString("F3") + " ms is the CPU in our drawer, the rest the graphics side");
        }

        // Runs for seconds of real time with the camera on the colonists' middle, each frame's time (ms) into frames if given.
        private static IEnumerator FollowFor(List<Pawn> colonists, float seconds, List<float> frames)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                Vector3 to = Centroid(colonists, out float spread);
                lookAt = Vector3.Lerp(lookAt, to, 0.02f);   // drifting after them slowly, not jumping with each step
                if (frames != null)
                {
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                }
                yield return null;
            }
        }

        private static Vector3 Centroid(List<Pawn> pawns, out float spread)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (Pawn p in pawns)
            {
                if (p.Spawned && p.Map == map)
                {
                    sum += p.DrawPos;
                    n++;
                }
            }
            Vector3 mid = n > 0 ? sum / n : middle.ToVector3Shifted();
            spread = 0f;
            foreach (Pawn p in pawns)
            {
                if (p.Spawned && p.Map == map)
                {
                    spread = Mathf.Max(spread, (p.DrawPos - mid).MagnitudeHorizontal());
                }
            }
            return new Vector3(mid.x, 0f, mid.z);
        }

        private static float Mean(List<float> f)
        {
            double sum = 0;
            foreach (float v in f)
            {
                sum += v;
            }
            return f.Count > 0 ? (float)(sum / f.Count) : 0f;
        }

        // A percentile of a sorted list.
        private static float Pct(List<float> sorted, float at)
        {
            return sorted.Count == 0 ? 0f : sorted[Mathf.Clamp(Mathf.RoundToInt(at * (sorted.Count - 1)), 0, sorted.Count - 1)];
        }

        private static string Summary(List<float> f)
        {
            List<float> s = new List<float>(f);
            s.Sort();
            float mean = Mean(s);
            return s.Count + " frames, mean " + mean.ToString("F2") + " ms (" + (mean > 0f ? 1000f / mean : 0f).ToString("F1") + " FPS), median " + Pct(s, 0.5f).ToString("F2") + " ms";
        }

        private static string Delta(float meanA, float meanB, float medA, float medB)
        {
            float fpsA = meanA > 0f ? 1000f / meanA : 0f, fpsB = meanB > 0f ? 1000f / meanB : 0f;
            return (meanA - meanB).ToString("+0.00;-0.00") + " ms a frame on the mean (" + (medA - medB).ToString("+0.00;-0.00") + " on the median), "
                + fpsB.ToString("F1") + " → " + fpsA.ToString("F1") + " FPS";
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
            if (name.StartsWith("faceshapes_", StringComparison.Ordinal))
            {
                return FaceShapesTest(name.Substring("faceshapes_".Length).Split('+'));
            }
            if (name.StartsWith("faceanim_", StringComparison.Ordinal))
            {
                return FaceAnimTest(name.Substring("faceanim_".Length));
            }
            switch (name)
            {
                case "hitflash": return HitFlashTest();
                case "explode": return ExplodeTest();
                case "lively": return LivelyTest(null);
                case "lively_wave": return LivelyTest("wave");
                case "lively_sad": return LivelyTest("sad");
                case "lively_burning": return LivelyTest("burning");
                case "lively_catatonic": return LivelyTest("catatonic");
                case "lively_flick": return LivelyTest("flick");
                case "lively_refuel": return LivelyTest("refuel");
                case "beatfire": return BeatFireTest();
                case "horseshoes": return TossTest();
                case "hunker": return HunkerTest();
                case "cuddle": return CuddleTest();
                case "read": return ReadTest();
                case "pray": return PrayTest();
                case "eat": return EatTest();
                case "drugs": return DrugsTest();
                case "visit": return VisitTest();
                case "talk": return TalkTest();
                case "chat": return ChatTest();
                case "music": return MusicTest();
                case "rolljoint": return RollJointTest();
                case "drunk": return DrunkTest();
                case "socialfight": return SocialFightTest();
                case "dress": return DressTest();
                case "shields": return ShieldTest();
                case "cook": return CookTest();
                case "teach": return TeachTest();
                case "floordraw": return FloordrawTest();
                case "tail": return TailTest();
                case "romance": return RomanceTest(null);
                case "readroom": return ReadRoomTest();
                case "bedcompare": return BedCompareTest();
                case "sharedbed": return SharedBedTest();
                case "sleepwear": return SleepWearTest();
                case "sleepside": return SleepSideTest();
                case "bedin": return BedInTest();
                case "lovin": return LovinTest();
                case "lovinbare": return LovinTest(true);
                case "kidbed": return KidBedTest();
                case "fpeye": return FirstPersonEyeTest();
                case "gibbet": return GibbetTest();
                case "vat": return VatTest();
                case "tend": return TendTest(null);
                case "bows": return BowTest(null);
                case "weaponkinds": return WeaponKindsTest();
                case "standins": return StandInsTest();
                case "tendbed": return TendBedTest();
                case "feed": return FeedTest(null);
                case "slicecap": return SlicecapTest();
                case "hats": return HatsTest();
                case "prosthetics": return ProstheticsTest();
                case "faceab": return FaceAbTest();
                case "beard": return BeardTest();
                case "faceanim": return FaceAnimTest(null);
                case "faceshapes": return FaceShapesTest(null);
            }
            if (name.StartsWith("feed_"))
            {
                return FeedTest(name.Substring("feed_".Length));
            }
            if (name.StartsWith("tend_"))
            {
                return TendTest(name.Substring("tend_".Length));
            }
            if (name.StartsWith("weapons_"))
            {
                return BowRun(WeaponsNamed(name.Substring("weapons_".Length).Split('+')), false);
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

        // What pawns in bed are drawn wearing (the sleepWear option, SleepWear.cs): a grown pawn in a shirt, trousers, a
        // duster, a flak vest, a simple helmet and a utility belt, and a child in a shirt and trousers, each asleep in a
        // single bed, through each choice in turn.
        private static IEnumerator SleepWearTest()
        {
            SleepWear was = SettingsMod.Settings.sleepWear;
            bool blanketsWere = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = false;   // (on top of the bed, so the body shows)
            try
            {
                Building_Bed MakeBed(IntVec3 at)
                {
                    Thing made = ThingMaker.MakeThing(ThingDef.Named("Bed"), ThingDefOf.WoodLog);
                    made.SetFaction(Faction.OfPlayer);
                    return (Building_Bed)Spawn(made, at, Rot4.North);
                }
                void Dress(Pawn p, params string[] defs)
                {
                    p.apparel.DestroyAll();
                    foreach (string name in defs)
                    {
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                        if (def != null)
                        {
                            p.apparel.Wear((Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)), false);
                        }
                    }
                }
                Building_Bed bedA = MakeBed(middle);
                Building_Bed bedC = MakeBed(middle + new IntVec3(2, 0, 0));
                Pawn a = NewPawn(middle + new IntVec3(-1, 0, -2), null);
                Pawn c = NewChild(middle + new IntVec3(3, 0, -2));
                Dress(a, "Apparel_BasicShirt", "Apparel_Pants", "Apparel_Duster", "Apparel_FlakVest", "Apparel_SimpleHelmet", "Apparel_PackBandolier", "Apparel_ShieldBelt");
                Dress(c, "Apparel_BasicShirt", "Apparel_Pants");
                bedA.TryGetComp<CompAssignableToPawn>().TryAssignPawn(a);
                bedC.TryGetComp<CompAssignableToPawn>().TryAssignPawn(c);
                Pawn[] sleepers = { a, c };
                Building_Bed[] beds = { bedA, bedC };
                void Tuck()
                {
                    for (int k = 0; k < sleepers.Length; k++)
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
                lookAt = bedA.TrueCenter() + new Vector3(1f, 0f, 0f);
                lookYaw = 0f;
                foreach (SleepWear mode in new[] { SleepWear.Dressed, SleepWear.NoEquipment, SleepWear.Pajamas, SleepWear.Nothing })
                {
                    SettingsMod.Settings.sleepWear = mode;
                    Tuck();
                    yield return WaitReal(1f);
                    yield return Ticks(20, "sleepwear", a);
                    Tuck();
                    yield return Shot("sleep_" + mode);
                    Check(mode + ": both in bed", a.InBed() && c.InBed(), "a in bed " + a.InBed() + ", c in bed " + c.InBed());
                }
                Note("Look: Dressed, both in all they wear; NoEquipment, the grown pawn in its shirt and trousers only (no duster, vest, helmet, bandolier or belt); Pajamas, both in pastel romper pajamas, each its own colour; Nothing, both bare.");
            }
            finally
            {
                SettingsMod.Settings.sleepWear = was;
                SettingsMod.Settings.bedBlankets = blanketsWere;
            }
        }

        // Getting into bed (WalkFigures.BedDownOf: the getup backwards): a pawn walks from two cells off to its bed and lies
        // down in it, watched from the side at stages of the lay-down (Ease.bedded), with the blankets option as it is set.
        private static IEnumerator BedInTest()
        {
            Thing made = ThingMaker.MakeThing(ThingDef.Named("Bed"), ThingDefOf.WoodLog);
            made.SetFaction(Faction.OfPlayer);
            Building_Bed bed = (Building_Bed)Spawn(made, middle, Rot4.North);
            Pawn sleeper = NewPawn(middle + new IntVec3(0, 0, -3), null);
            bed.TryGetComp<CompAssignableToPawn>().TryAssignPawn(sleeper);
            lookAt = middle.ToVector3Shifted();
            lookYaw = 90f;
            yield return WaitReal(2f);
            sleeper.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
            int waited = 0;
            while (!sleeper.InBed() && waited < 600)
            {
                yield return Ticks(1, "bedin", sleeper);
                waited++;
            }
            Check("walked to its bed and got in", sleeper.InBed(), "after " + waited + " ticks");
            float least = Field<float>(sleeper, "bedded");
            int shot = 0;
            for (int i = 0; i < 60 && shot < 5; i++)
            {
                yield return Ticks(1, "bedin", sleeper);
                float bedded = Field<float>(sleeper, "bedded");
                least = Mathf.Min(least, bedded);
                if (bedded >= shot * 0.25f || bedded >= 1f)
                {
                    yield return Shot("bedin_" + (int)(shot * 25));
                    shot++;
                }
            }
            Check("lay down by the getup backwards", least < 0.5f, "least Ease.bedded seen " + least.ToString("0.00"));
            Note("Look: bedin_0 standing by or on the bed, then going down onto its back (the getup backwards) over the bed, bedin_100 lying in it as before.");
        }

        // The sleep clip's variants (sleep~2 and sleep~3: on either side, still, walk.SLEEP_HOLD, SLEEP_MIRROR): four pawns asleep on top of single beds
        // (the bedBlankets option off, so the body shows), each picking one by its job (WalkFigures.LieClip).
        private static IEnumerator SleepSideTest()
        {
            bool was = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = false;
            try
            {
                Pawn[] sleepers = new Pawn[4];
                Building_Bed[] beds = new Building_Bed[4];
                for (int k = 0; k < 4; k++)
                {
                    Thing made = ThingMaker.MakeThing(ThingDef.Named("Bed"), ThingDefOf.WoodLog);
                    made.SetFaction(Faction.OfPlayer);
                    beds[k] = (Building_Bed)Spawn(made, middle + new IntVec3(2 * k - 3, 0, 0), Rot4.North);
                    sleepers[k] = NewPawn(middle + new IntVec3(2 * k - 3, 0, -2), null);
                    beds[k].TryGetComp<CompAssignableToPawn>().TryAssignPawn(sleepers[k]);
                }
                void Tuck()
                {
                    for (int k = 0; k < 4; k++)
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
                lookYaw = 90f;
                yield return WaitReal(2f);
                for (int i = 0; i < 6; i++)
                {
                    Tuck();
                    yield return Ticks(20, "sleepside", sleepers[0]);
                    if (i % 3 == 2)
                    {
                        yield return Shot("side_t" + (i + 1) * 20);
                    }
                }
                int side = 0;
                for (int k = 0; k < 4; k++)
                {
                    string clip = Field<string>(sleepers[k], "lieNow");
                    side += clip == "sleep~2" || clip == "sleep~3" ? 1 : 0;
                    Check("sleeper " + k + ": asleep in its bed", sleepers[k].InBed() && !sleepers[k].Awake(), "clip " + (clip ?? "none"));
                }
                Note(side + " of 4 on the side (sleep~2, sleep~3). Look: those on the side lie still on top of the bed, breathing, not writhing; none through the bed.");
                lookYaw = 0f;
                yield return Shot("side_foot");
            }
            finally
            {
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
        // lovinbare: the same with no blanket (the bedBlankets option off): the two on their sides facing each other
        // (lovin_L, lovin_R), drawn toward each other (WalkFigures.LovinBareOf), the heads not turned.
        private static IEnumerator LovinTest(bool bare = false)
        {
            bool was = SettingsMod.Settings.bedBlankets;
            SettingsMod.Settings.bedBlankets = !bare;
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
                    Check("lover " + k + ": drawn toward the other", shifts[k] > (bare ? 0.02f : 0.1f), "shift " + shifts[k].ToString("F2") + " cells");
                    if (bare)
                    {
                        string clip = Field<string>(p, "lieNow");
                        Check("lover " + k + ": on the side", clip == "lovin_L" || clip == "lovin_R", "clip " + (clip ?? "none"));
                    }
                    else
                    {
                        Check("lover " + k + ": head turned", Mathf.Abs(looks[k]) > 40f, "look " + looks[k].ToString("F0") + " degrees");
                    }
                }
                if (bare)
                {
                    Check("lovers: facing opposite ways", Field<string>(a, "lieNow") != Field<string>(b, "lieNow"),
                        Field<string>(a, "lieNow") + " and " + Field<string>(b, "lieNow"));
                }
                else
                {
                    Check("lovers: heads turned opposite ways", looks[0] * looks[1] < 0f, looks[0].ToString("F0") + " and " + looks[1].ToString("F0"));
                }
                lookYaw = 180f;
                yield return Shot("lovin_head");
                lookAt = bed.TrueCenter() + new Vector3(0f, 0f, 0.5f);
                lookYaw = 0f;
                lookPitch = 30f;
                lookDistance = 1.8f;
                lookHeight = 0.4f;
                yield return Shot("lovin_close");
                Note(bare ? "Look: the two on their sides on top of the bed, close and facing each other, not through each other or the bed."
                    : "Look: the two heads close together on the pillows, turned to face each other, each lump of blanket moved with its head, no body poking through.");
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
        // Corpses hung in Ideology's gibbet cages (a grown man, a grown woman, a child), which the chibi draws slumped upright
        // on the cage floor (WalkFigures.DrawCaged) where it used to leave them to IsoRim's figure.
        private static IEnumerator GibbetTest()
        {
            ThingDef cageDef = DefDatabase<ThingDef>.GetNamedSilentFail("GibbetCage");
            if (cageDef == null || !ModsConfig.IdeologyActive)
            {
                Check("gibbet: Ideology's gibbet cage", false, "not loaded");
                yield break;
            }
            float[] ages = { 30f, 30f, 9f };
            Gender[] genders = { Gender.Male, Gender.Female, Gender.Male };
            string[] names = { "man", "woman", "child 9" };
            Building_GibbetCage[] cages = new Building_GibbetCage[ages.Length];
            for (int k = 0; k < ages.Length; k++)
            {
                Thing made = ThingMaker.MakeThing(cageDef, cageDef.MadeFromStuff ? ThingDefOf.Steel : null);
                made.SetFaction(Faction.OfPlayer);
                cages[k] = (Building_GibbetCage)Spawn(made, middle + new IntVec3(3 * k - 3, 0, 0), Rot4.South);
                DevelopmentalStage stage = ages[k] < 13f ? DevelopmentalStage.Child : DevelopmentalStage.Adult;
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    fixedBiologicalAge: ages[k], fixedChronologicalAge: ages[k], fixedGender: genders[k], developmentalStages: stage));
                pawn.equipment.DestroyAllEquipment();
                Spawn(pawn, middle + new IntVec3(3 * k - 3, 0, -3));
                pawn.Kill(null);
                Corpse corpse = pawn.Corpse;
                if (corpse != null && corpse.Spawned)
                {
                    corpse.DeSpawn();
                }
                // (straight into the cage's container: its storage filter turns colonists' corpses away)
                bool inside = corpse != null && (cages[k].TryAcceptThing(corpse) || cages[k].GetDirectlyHeldThings().TryAdd(corpse));
                Check(names[k] + ": corpse hung in the cage", inside && corpse.ParentHolder == cages[k], corpse == null ? "no corpse" : "in " + corpse.ParentHolder);
            }
            lookAt = middle.ToVector3Shifted();
            lookYaw = 0f;
            yield return WaitReal(3f);
            yield return Ticks(30, "gibbet", null);
            yield return Shot("cages");
            float wasDistance = lookDistance;
            lookDistance = 3f;
            for (int k = 0; k < ages.Length; k++)
            {
                lookAt = cages[k].TrueCenter();
                yield return Shot("cage_" + names[k].Replace(' ', '_'));
            }
            lookDistance = wasDistance;
            Note("Look at: each corpse slumped upright in its cage, feet on the cage floor, its head bowed and under the dome, "
                + "no part through the bars; the child smaller.");
        }

        // People in Biotech's growth vats (a baby, a child, a teen) and in Ideology's biosculpter pod (a grown pawn), to see
        // how each is drawn: the chibi on the vat's couch, or IsoRim's own figure (WalkFigures leaves InVat to it). Put
        // straight into each container, as the test map has no power to load them the game's way; the vat's clock is
        // started so the game counts it working (it ages its child 20 ticks a tick, which this short test hardly shows).
        private static IEnumerator VatTest()
        {
            List<Thing> holders = new List<Thing>();
            List<string> names = new List<string>();
            List<Pawn> occupants = new List<Pawn>();
            ThingDef vatDef = DefDatabase<ThingDef>.GetNamedSilentFail("GrowthVat");
            if (vatDef == null || !ModsConfig.BiotechActive)
            {
                Check("vat: Biotech's growth vat", false, "not loaded");
            }
            else
            {
                float[] ages = { 1f, 7f, 15f };
                string[] labels = { "baby 1", "child 7", "teen 15" };
                FieldInfo startTick = typeof(Building_Enterable).GetField("startTick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo selected = typeof(Building_Enterable).GetField("selectedPawn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                for (int k = 0; k < ages.Length; k++)
                {
                    Thing made = ThingMaker.MakeThing(vatDef, vatDef.MadeFromStuff ? ThingDefOf.Steel : null);
                    made.SetFaction(Faction.OfPlayer);
                    Building_GrowthVat vat = (Building_GrowthVat)Spawn(made, middle + new IntVec3(3 * k - 4, 0, 1), Rot4.South);
                    DevelopmentalStage stage = ages[k] < 3f ? DevelopmentalStage.Baby : ages[k] < 13f ? DevelopmentalStage.Child : DevelopmentalStage.Adult;
                    Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                        fixedBiologicalAge: ages[k], fixedChronologicalAge: ages[k], developmentalStages: stage));
                    pawn.equipment.DestroyAllEquipment();
                    bool inside = vat.innerContainer.TryAdd(pawn);
                    if (inside)
                    {
                        selected?.SetValue(vat, pawn);
                        startTick?.SetValue(vat, Find.TickManager.TicksGame);
                    }
                    Check(labels[k] + ": in the growth vat", inside && pawn.ParentHolder == vat, "in " + pawn.ParentHolder);
                    holders.Add(vat);
                    occupants.Add(pawn);
                    names.Add("vat_" + labels[k].Replace(' ', '_'));
                }
            }
            ThingDef podDef = DefDatabase<ThingDef>.GetNamedSilentFail("BiosculpterPod");
            if (podDef == null || !ModsConfig.IdeologyActive)
            {
                Check("vat: Ideology's biosculpter pod", false, "not loaded");
            }
            else
            {
                Thing made = ThingMaker.MakeThing(podDef, podDef.MadeFromStuff ? ThingDefOf.Steel : null);
                made.SetFaction(Faction.OfPlayer);
                Thing pod = Spawn(made, middle + new IntVec3(5, 0, 1), Rot4.South);
                CompBiosculpterPod comp = pod.TryGetComp<CompBiosculpterPod>();
                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                    fixedBiologicalAge: 30f, fixedChronologicalAge: 30f));
                pawn.equipment.DestroyAllEquipment();
                FieldInfo cycle = typeof(CompBiosculpterPod).GetField("currentCycleKey", BindingFlags.Instance | BindingFlags.NonPublic);
                ThingOwner owner = comp?.GetDirectlyHeldThings();
                bool inside = owner != null && owner.TryAdd(pawn);
                if (inside && cycle != null && comp.AvailableCycles.Count > 0)
                {
                    cycle.SetValue(comp, comp.AvailableCycles[0].Props.key);
                }
                Check("grown 30: in the biosculpter pod", inside && pawn.ParentHolder != null, "in " + pawn.ParentHolder
                    + ", state " + (comp != null ? comp.State.ToString() : "no comp"));
                holders.Add(pod);
                occupants.Add(pawn);
                names.Add("pod_grown_30");
            }
            if (holders.Count == 0)
            {
                yield break;
            }
            lookAt = middle.ToVector3Shifted() + new Vector3(0.5f, 0f, 1f);
            lookYaw = 0f;
            yield return WaitReal(3f);
            yield return Ticks(30, "vat", null);
            yield return Shot("vats");
            for (int k = 0; k < occupants.Count; k++)
            {
                Vector2 fit;
                bool fitted = WalkFigures.glassFits.TryGetValue(occupants[k].thingIDNumber, out fit);
                Check(names[k] + ": fitted under the glass (WalkFigures.UnderGlass)", fitted, fitted
                    ? string.Format("let down {0:F3} cells, slid {1:F3} cells along the glass", fit.x, fit.y) : "never fitted");
            }
            float wasDistance = lookDistance;
            lookDistance = 3f;
            for (int k = 0; k < holders.Count; k++)
            {
                lookAt = holders[k].TrueCenter();
                yield return Shot(names[k]);
            }
            lookDistance = wasDistance;
            Note("Look at: who draws each occupant (the chibi or IsoRim's figure), how it lies or floats in the vat or pod, "
                + "whether it shows through the glass, and its size against the vat.");
        }

        // IsoRim's first person from the chibi's eyes (Api 77, FirstPersonEye.cs): a grown pawn, a child and a grown pawn on
        // a chair, each eye against IsoRim's own (the option off) while drawn, a photo through each one's eyes (Api 76), then
        // first person in each in turn (IsoRim's FirstPerson.Toggle by reflection, with only it selected): it is no longer
        // drawn, and the eye keeps the height last drawn for what it does.
        private static IEnumerator FirstPersonEyeTest()
        {
            if (IsoRim.Api.Version < 77)
            {
                Check("IsoRim Api 77", false, "Api " + IsoRim.Api.Version);
                yield break;
            }
            Thing chair = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
            chair.SetFaction(Faction.OfPlayer);
            Spawn(chair, middle + new IntVec3(2, 0, 0), Rot4.South);
            Pawn grown = NewPawn(middle + new IntVec3(-2, 0, 0), null);
            Pawn child = NewChild(middle);
            Pawn sitter = NewPawn(middle + new IntVec3(2, 0, 0), null);
            Pawn[] who = { grown, child, sitter };
            string[] names = { "grown", "child", "seated" };
            foreach (Pawn p in who)
            {
                Hold(p);
                p.Rotation = Rot4.South;
            }
            lookAt = middle.ToVector3Shifted();
            lookYaw = 0f;
            yield return WaitReal(2f);
            yield return Ticks(30, "fpeye", grown);
            yield return Shot("three");
            Vector3[] ours = new Vector3[3];
            for (int k = 0; k < 3; k++)
            {
                Vector3? mine = IsoRim.ViewPose.FirstPersonOf(who[k]).Eye;
                SettingsMod.Settings.firstPersonEye = false;
                Vector3? iso = IsoRim.ViewPose.FirstPersonOf(who[k]).Eye;
                SettingsMod.Settings.firstPersonEye = true;
                ours[k] = mine ?? Vector3.zero;
                Check(names[k] + ": an eye of ours while drawn", mine.HasValue && WalkFigures.EyesFrame(who[k]) >= Time.frameCount - 1 && iso.HasValue && (mine.Value - iso.Value).sqrMagnitude > 1e-6f,
                    "ours " + (mine.HasValue ? mine.Value.ToString("F3") : "null") + ", IsoRim's " + (iso.HasValue ? iso.Value.ToString("F3") : "null")
                    + ", drawn at " + who[k].DrawPos.ToString("F3") + ", size " + WalkFigures.SizeOfForTest(who[k]).ToString("F3"));
            }
            Check("the child's eyes lower than the grown pawn's", ours[1].y < ours[0].y - 0.15f, ours[1].y.ToString("F3") + " against " + ours[0].y.ToString("F3"));
            Check("the seated eyes lower than the standing", ours[2].y < ours[0].y - 0.1f, ours[2].y.ToString("F3") + " against " + ours[0].y.ToString("F3"));
            // Photos through each one's eyes, the pawn itself left out.
            for (int k = 0; k < 3; k++)
            {
                string file = Path.Combine(outDir, CurrentPrefix() + "eyes_" + names[k] + ".png");
                bool done = false;
                bool asked = IsoRim.Api.CaptureView(who[k].Map, IsoRim.ViewPose.FirstPersonOf(who[k], 15f),
                    new IsoRim.CaptureViewOptions { Width = 960, Height = 540, FirstPersonPawn = who[k] }, tex =>
                    {
                        if (tex != null)
                        {
                            File.WriteAllBytes(file, tex.EncodeToPNG());
                            UnityEngine.Object.Destroy(tex);
                        }
                        done = true;
                    });
                for (int i = 0; asked && i < 120 && !done; i++)
                {
                    yield return null;
                }
                if (asked)
                {
                    report.AppendLine("- picture `" + Path.GetFileName(file) + "`");
                }
            }
            // First person in each: not drawn there, and the eye at the height last drawn (IsoRim's across the ground).
            Type firstPerson = typeof(IsoRim.Api).Assembly.GetType("IsoRim.FirstPerson");
            MethodInfo toggle = firstPerson != null ? firstPerson.GetMethod("Toggle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
            MethodInfo leave = firstPerson != null ? firstPerson.GetMethod("Leave", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
            PropertyInfo viewing = firstPerson != null ? firstPerson.GetProperty("ViewingPawn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
            if (toggle == null || leave == null || viewing == null)
            {
                Check("IsoRim's FirstPerson found", false, firstPerson == null ? "no type" : "no Toggle, Leave or ViewingPawn");
                yield break;
            }
            poseOff = true;
            for (int k = 0; k < 3; k++)
            {
                Find.Selector.ClearSelection();
                Find.Selector.Select(who[k], false, false);
                toggle.Invoke(null, null);
                yield return Ticks(20, "fpeye", who[k]);
                yield return WaitReal(0.5f);
                Pawn on = viewing.GetValue(null) as Pawn;
                int last = WalkFigures.EyesFrame(who[k]);
                Vector3? mine = WalkFigures.EyeForTest(who[k]);
                Check(names[k] + ": first person on", on == who[k], "viewing " + (on != null ? on.LabelShort : "nobody"));
                Check(names[k] + ": not drawn in first person, the eye kept", Time.frameCount - last > 2 && mine.HasValue && Mathf.Abs(mine.Value.y - ours[k].y) < 0.03f,
                    "last drawn " + (Time.frameCount - last) + " frames ago, eye " + (mine.HasValue ? mine.Value.ToString("F3") : "null") + ", drawn " + ours[k].ToString("F3"));
                if (k == 0)
                {
                    // The shown first-person view, as the player sees it.
                    string file = Path.Combine(outDir, CurrentPrefix() + "firstperson_" + names[k] + ".png");
                    bool done = false;
                    bool asked = IsoRim.Api.RequestSceneImage(tex =>
                    {
                        if (tex != null)
                        {
                            File.WriteAllBytes(file, tex.EncodeToPNG());
                            UnityEngine.Object.Destroy(tex);
                        }
                        done = true;
                    }, new IsoRim.SceneImageOptions { IncludeOverlays = false });
                    for (int i = 0; asked && i < 120 && !done; i++)
                    {
                        yield return null;
                    }
                    if (asked)
                    {
                        report.AppendLine("- picture `" + Path.GetFileName(file) + "`");
                    }
                }
                leave.Invoke(null, new object[] { null });
                yield return Ticks(5, "fpeye", who[k]);
            }
            poseOff = false;
            Find.Selector.ClearSelection();
        }

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

        // Floordrawing (Biotech's learning job): a child stood on its cell drawing in chalk on the cell beside it, south (toward
        // the camera's side) and then east, knelt with the chalk in its right fist (work_chalk, the tool chalk line) and drawn
        // shuffled up to the drawing (WalkFigures.ChalkCloser).
        private static IEnumerator FloordrawTest()
        {
            JobDef draw = DefDatabase<JobDef>.GetNamedSilentFail("Floordrawing");
            if (draw == null || !ModsConfig.BiotechActive)
            {
                Check("floordraw", false, "no Floordrawing job (Biotech off?)");
                yield break;
            }
            foreach (string way in new[] { "south", "east" })
            {
                ClearPatch();
                IntVec3 from = middle, on = middle + (way == "south" ? IntVec3.South : IntVec3.East);
                Pawn child = NewChild(from + new IntVec3(-1, 0, 1));
                Job job = JobMaker.MakeJob(draw, from, on);
                job.playerForced = true;
                KeepDrawing(child);
                child.jobs.StartJob(job, JobCondition.InterruptForced);
                Look(child, way == "south" ? 330f : 30f);
                string kind = null;
                float shown = 0f;
                bool lasted = true;
                for (int i = 0; i < 40; i++)
                {
                    KeepDrawing(child);
                    yield return Ticks(15, "floordraw_" + way, child);
                    if (child.CurJobDef != draw)
                    {
                        lasted = false;
                        break;
                    }
                    string k = Field<string>(child, "workKind");
                    if (k != null && Field<float>(child, "work") > 0.5f)
                    {
                        kind = k;
                    }
                    shown = Mathf.Max(shown, Field<float>(child, "shownWork"));
                    if (i % 8 == 7)
                    {
                        yield return Shot(way + "_t" + (i + 1) * 15);
                    }
                }
                Check(way + ": the drawing goes on", lasted, "job " + (child.CurJobDef != null ? child.CurJobDef.defName : "none"));
                Check(way + ": knelt drawing in chalk", kind == "chalk" && shown > 0.5f, "work kind " + (kind ?? "none") + ", shown " + shown.ToString("F2"));
                if (child.jobs != null)
                {
                    child.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
            }
            Note("Look: the child knelt on its cell edge facing the drawing, the left palm propped on the floor, the chalk upright in its right fist, the tip on the floor scribbling loops and sweeping side to side, lifted once a loop; the head bowed to it.");
        }

        // A child with each cat tail gene knelt stooped over its chalk drawing (the pluck kneel, bent forward from the hips),
        // seen from behind and the side, to see the tail stays on the skin over the tailbone (CatTail.cs, OnSkin).
        private static IEnumerator TailTest()
        {
            JobDef draw = DefDatabase<JobDef>.GetNamedSilentFail("Floordrawing");
            if (draw == null || !ModsConfig.BiotechActive)
            {
                Check("tail", false, "no Floordrawing job (Biotech off?)");
                yield break;
            }
            foreach (string geneName in new[] { "Tail_Furry", "Tail_Smooth" })
            {
                GeneDef gene = DefDatabase<GeneDef>.GetNamedSilentFail(geneName);
                if (gene == null)
                {
                    Note(geneName + ": skipped, no such gene");
                    continue;
                }
                ClearPatch();
                IntVec3 from = middle, on = middle + IntVec3.South;
                Pawn child = NewChild(from + new IntVec3(-1, 0, 1));
                child.genes.AddGene(gene, true);
                child.apparel.DestroyAll();   // bare, so the root shows
                Job job = JobMaker.MakeJob(draw, from, on);
                job.playerForced = true;
                KeepDrawing(child);
                child.jobs.StartJob(job, JobCondition.InterruptForced);
                Look(child, 150f);
                lookDistance = 1.6f;
                lookHeight = 0.25f;
                lookPitch = 15f;
                SkinnedFigure.TailRootGap = 0f;
                for (int i = 0; i < 24; i++)
                {
                    KeepDrawing(child);
                    yield return Ticks(15, "tail_" + geneName, child);
                    if (i % 8 == 7)
                    {
                        yield return Shot(geneName + "_t" + (i + 1) * 15);
                    }
                }
                Note(geneName + ": the root moved onto the skin by at most " + SkinnedFigure.TailRootGap.ToString("F3") + " model units");
                Check(geneName + ": knelt drawing", Field<string>(child, "workKind") == "chalk", "work kind " + (Field<string>(child, "workKind") ?? "none"));
                if (child.jobs != null)
                {
                    child.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                child.Destroy();
            }
            lookDistance = LookDistance;
            lookHeight = LookHeight;
            lookPitch = LookPitch;
            Note("Look: the tail's root on the skin over the tailbone in every picture, no gap between it and the body as the child stoops.");
        }

        // Kept from ending the floordrawing: the learning need kept low (the job ends once it is met) and the basic needs full
        // (FailOnChildLearningConditions ends it when one will soon come up).
        private static void KeepDrawing(Pawn child)
        {
            if (child.needs.learning != null)
            {
                child.needs.learning.CurLevel = 0.1f;
            }
            if (child.needs.food != null)
            {
                child.needs.food.CurLevel = child.needs.food.MaxLevel;
            }
            if (child.needs.rest != null)
            {
                child.needs.rest.CurLevel = 1f;
            }
            if (child.needs.joy != null)
            {
                child.needs.joy.CurLevel = 1f;
            }
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

        // Drunk: a pawn given AlcoholHigh as hammered swaying standing, then weaving as it walks off (DrunkOf, Ease.drunk,
        // Ease.weave).
        private static IEnumerator DrunkTest()
        {
            ClearPatch();
            Pawn pawn = NewPawn(middle + new IntVec3(-3, 0, 0), null);
            Hold(pawn);
            Look(pawn, 0f);
            yield return WaitReal(2f);
            Hediff high = pawn.health.AddHediff(DefDatabase<HediffDef>.GetNamed("AlcoholHigh"));
            high.Severity = 0.8f;
            float drunk = 0f;
            yield return Ticks(130, "drunk_stand", pawn);   // (looked at with the legs, every LimpCheck ticks)
            yield return WaitReal(5f);   // (the sway comes on over real time, DrunkEase)
            // A picture every quarter of the sway's loop (DRUNK_SECONDS, 4 s).
            for (int i = 0; i < 4; i++)
            {
                yield return Ticks(60, "drunk_stand", pawn);
                drunk = Mathf.Max(drunk, Field<float>(pawn, "drunk"));
                yield return Shot("drunk_stand_" + i);
            }
            Check("drunk: swaying", drunk > 0.9f, "drunk " + drunk.ToString("F2") + ", severity " + high.Severity.ToString("F2"));
            IntVec3 to = middle + new IntVec3(4, 0, 0);
            Job walk = JobMaker.MakeJob(JobDefOf.Goto, to);
            walk.playerForced = true;
            pawn.jobs.StartJob(walk, JobCondition.InterruptForced);
            Note("drunk: walking to " + to + ": job " + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none") + ", moving "
                + pawn.pather.Moving + ", standable " + to.Standable(map));
            float weave = 0f;
            for (int i = 0; i < 24 && (i == 0 || pawn.pather.Moving); i++)
            {
                yield return Ticks(8, "drunk_walk", pawn);
                lookAt = pawn.DrawPos;
                weave = Mathf.Max(weave, Mathf.Abs(Field<float>(pawn, "weave")));
                if (i == 6 || i == 12)
                {
                    yield return Shot("drunk_walk_" + i);
                }
            }
            Check("drunk: weaving on the move", weave > 0.01f, "weave up to " + weave.ToString("F3") + " cells");
            Note("Look: drunk_stand_* leant to a side with the head lolling, the arms hanging; drunk_walk_* off its straight path.");
        }

        // A social fight between two armed colonists, one with a knife and one with a rifle (WalkFigures.SocialFighting):
        // the game fights it with the body's own verbs, so each should punch bare-handed, its weapon left where it is
        // carried, never shown in the hands or aimed (IsoRim takes the busy stance of a punch for aiming). Stepped a tick at
        // a time, with a picture at each pawn's first two blows.
        private static IEnumerator SocialFightTest()
        {
            ClearPatch();
            Pawn a = NewPawn(middle + new IntVec3(-1, 0, 0), "MeleeWeapon_Knife");
            Pawn b = NewPawn(middle + new IntVec3(0, 0, 0), "Gun_AssaultRifle");
            Hold(a);
            Hold(b);
            lookAt = (a.DrawPos + b.DrawPos) * 0.5f;
            lookYaw = 0f;
            yield return Ticks(30, "socialfight_before", a);
            yield return WaitReal(2f);
            yield return Shot("socialfight_before");
            a.interactions.StartSocialFight(b);
            Check("socialfight: started", a.MentalStateDef == MentalStateDefOf.SocialFighting && b.MentalStateDef == MentalStateDefOf.SocialFighting,
                "states " + (a.MentalStateDef != null ? a.MentalStateDef.defName : "none") + ", " + (b.MentalStateDef != null ? b.MentalStateDef.defName : "none"));
            Pawn[] both = { a, b };
            int[] blows = new int[2], shown = new int[2], aimed = new int[2], armedStance = new int[2], shots = new int[2];
            string[] clips = { "", "" };
            string[] lastClip = new string[2];
            for (int t = 0; t < 900 && (a.MentalStateDef == MentalStateDefOf.SocialFighting || b.MentalStateDef == MentalStateDefOf.SocialFighting); t++)
            {
                yield return Ticks(1, "socialfight", a);
                lookAt = (a.DrawPos + b.DrawPos) * 0.5f;
                bool picture = false;
                for (int i = 0; i < 2; i++)
                {
                    Pawn p = both[i];
                    if (!p.Spawned || p.Downed)
                    {
                        continue;
                    }
                    string clip = Field<string>(p, "strikeClip");
                    if (clip != null && clip != lastClip[i])
                    {
                        blows[i]++;
                        if (!clips[i].Contains(clip))
                        {
                            clips[i] += (clips[i].Length > 0 ? " " : "") + clip;
                        }
                    }
                    lastClip[i] = clip;
                    if (WalkFigures.AutoWeaponShows(p) || Field<bool>(p, "handHeld"))
                    {
                        shown[i]++;
                    }
                    if (Field<float>(p, "aim") > 0.01f)
                    {
                        aimed[i]++;
                    }
                    string stance = Field<string>(p, "stance");
                    if (clip != null && stance != null && !stance.Contains("unarmed"))
                    {
                        armedStance[i]++;
                    }
                    if (clip != null && Field<float>(p, "strikePhase") > 0.3f && shots[i] < 2 && blows[i] > shots[i])
                    {
                        picture = true;
                        shots[i]++;
                    }
                }
                if (picture)
                {
                    yield return Shot("socialfight_blow_" + (shots[0] + shots[1]));
                }
            }
            for (int i = 0; i < 2; i++)
            {
                string who = i == 0 ? "knife" : "rifle";
                Check("socialfight (" + who + "): threw blows", blows[i] > 0, blows[i] + " blows: " + (clips[i].Length > 0 ? clips[i] : "none"));
                Check("socialfight (" + who + "): bare-handed blows", clips[i].Length > 0 && !clips[i].Replace("melee_fist", "").Contains("melee_"), clips[i]);
                Check("socialfight (" + who + "): weapon never in the hands", shown[i] == 0, shown[i] + " ticks shown");
                Check("socialfight (" + who + "): never aimed", aimed[i] == 0, aimed[i] + " ticks aimed");
                Check("socialfight (" + who + "): unarmed stance in blows", armedStance[i] == 0, armedStance[i] + " ticks in an armed stance");
            }
            yield return Ticks(60, "socialfight_after", a);
            yield return Shot("socialfight_after");
            Note("Look: socialfight_blow_* fists up, no knife or rifle in the hands (each on its holster); _before and _after the same.");
        }

        // Combat Extended's shields (Shields.cs): each worn by a colonist with an autopistol, carried on the back undrafted,
        // then drafted, taken off the back into the guard by its shield_draw clip, and undrafted, put back. The clip runs on
        // frame time times the game's speed (StepShield), none while stepped paused, so it is played at normal speed and
        // stopped part way for pictures: as the hand reaches the edge (SHIELD_GRAB, about 0.4) and as it comes round.
        private static IEnumerator ShieldTest()
        {
            ClearPatch();
            string[] shields = { "CE_Apparel_MeleeShield", "CE_Apparel_BallisticShield" };
            int x = -3;
            foreach (string defName in shields)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                string name = defName.Substring("CE_Apparel_".Length);
                if (def == null)
                {
                    Note("shields: no " + defName + " (Combat Extended not active?), left out");
                    continue;
                }
                Pawn pawn = NewPawn(middle + new IntVec3(x, 0, 0), "Gun_Autopistol");
                x += 3;
                pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)), false);
                Hold(pawn);
                Look(pawn, 0f);
                yield return Ticks(30, "shield_" + name, pawn);
                yield return WaitReal(2f);
                Check(name + ": on the back undrafted", Field<float>(pawn, "shieldOut") <= 0f, "shieldOut " + Field<float>(pawn, "shieldOut").ToString("F2"));
                yield return Shot("shield_" + name + "_back");

                pawn.drafter.Drafted = true;
                string clip = null;
                float most = 0f;
                foreach (float stop in new[] { 0.42f, 0.75f, 1f })
                {
                    yield return Unpaused(pawn, () =>
                    {
                        clip = clip ?? Field<string>(pawn, "shieldClip");
                        most = Mathf.Max(most, Field<float>(pawn, "shieldOut"));
                        return Field<float>(pawn, "shieldOut") >= stop;
                    }, 4f);
                    if (stop < 1f)
                    {
                        yield return Shot("shield_" + name + "_draw_" + (int)(stop * 100f));
                    }
                }
                yield return Unpaused(pawn, () => Field<float>(pawn, "shield") >= 0.99f, 3f);
                Check(name + ": taken off the back by its clip", clip != null && clip.StartsWith("shield_draw_"), "clip " + (clip ?? "none"));
                Check(name + ": in the guard drafted", most >= 1f && Field<float>(pawn, "shield") > 0.9f,
                    "shieldOut " + Field<float>(pawn, "shieldOut").ToString("F2") + ", guard " + Field<float>(pawn, "shield").ToString("F2"));
                yield return Shot("shield_" + name + "_guard");

                pawn.drafter.Drafted = false;
                Hold(pawn);
                foreach (float stop in new[] { 0.6f, 0f })
                {
                    yield return Unpaused(pawn, () => Field<float>(pawn, "shieldOut") <= stop, 4f);
                    if (stop > 0f)
                    {
                        yield return Shot("shield_" + name + "_stow_" + (int)(stop * 100f));
                    }
                }
                Check(name + ": put back undrafted", Field<float>(pawn, "shieldOut") <= 0f, "shieldOut " + Field<float>(pawn, "shieldOut").ToString("F2"));
                yield return Shot("shield_" + name + "_back_again");
                pawn.DeSpawn();
            }
            Note("Look: _back the shield on the back, clear of the pistol's holster; _draw_42 the left hand closed on the shield's"
                + " left edge with the elbow out to the side (SHIELD_DRAW_POLE), not folded in behind the shoulder blade; _draw_75"
                + " coming round the left side; _guard in the left fist out in front; _stow_60 on its way back the same way.");
        }

        // Putting clothes on and taking them off (Dressing.cs): a stripped colonist puts on trousers, a shirt, a duster and a
        // cowboy hat in turn from the ground beside it (dress_legs, dress_over, dress_coat, dress_head), then power armour
        // (Royalty's; taking off the duster, trousers and shirt it replaces first, then dress_legs and dress_coat, the
        // tugging repeated over its long EquipDelay), then takes the hat off (dress_head backwards). Each checked for the
        // clips played, the garment drawn on the body partway (Dressing's onBody) and the apparel worn at the end.
        private static IEnumerator DressTest()
        {
            ClearPatch();
            Pawn pawn = NewPawn(middle, null);
            pawn.apparel.DestroyAll();
            Hold(pawn);
            Look(pawn, 0f);
            yield return WaitReal(2f);
            foreach (string defName in new[] { "Apparel_Pants", "Apparel_BasicShirt", "Apparel_Duster", "Apparel_CowboyHat", "Apparel_PowerArmor", "-Apparel_CowboyHat" })
            {
                bool off = defName.StartsWith("-");
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName.TrimStart('-'));
                if (def == null)
                {
                    Note("dress: no " + defName + ", left out");
                    continue;
                }
                string name = def.defName.Substring("Apparel_".Length) + (off ? "_off" : "");
                Job job;
                Apparel apparel;
                if (off)
                {
                    apparel = pawn.apparel.WornApparel.Find(a => a.def == def);
                    if (apparel == null)
                    {
                        Check("dress " + name, false, "not worn");
                        continue;
                    }
                    job = JobMaker.MakeJob(JobDefOf.RemoveApparel, apparel);
                }
                else
                {
                    apparel = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    Spawn(apparel, middle + new IntVec3(0, 0, -1));
                    job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
                }
                job.playerForced = true;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                HashSet<string> clips = new HashSet<string>();
                bool onBody = false, inHands = false;
                int shots = 0, ticks = 0;
                for (int i = 0; i < 1200 && pawn.CurJob == job; i++)
                {
                    yield return Ticks(off || !def.apparel.layers.Contains(ApparelLayerDefOf.Overhead) ? 5 : 3, "dress_" + name, pawn);
                    ticks += off || !def.apparel.layers.Contains(ApparelLayerDefOf.Overhead) ? 5 : 3;
                    string kind = Field<string>(pawn, "workKind");
                    if (kind != null && kind.StartsWith("dress_") && Field<float>(pawn, "work") > 0f)
                    {
                        clips.Add(kind);
                    }
                    Apparel dressed;
                    bool body, hands;
                    if (WalkFigures.DressingNow(pawn, out dressed, out body, out hands))
                    {
                        onBody |= body && dressed == apparel;
                        inHands |= hands;
                        if (i % (def.apparel.layers.Contains(ApparelLayerDefOf.Overhead) ? 2 : 6) == 0 && shots < 12)
                        {
                            yield return Shot("dress_" + name + "_" + shots++);
                        }
                    }
                }
                yield return Ticks(30, "dress_" + name + "_done", pawn);
                bool worn = pawn.apparel.WornApparel.Contains(apparel);
                Check("dress " + name, clips.Count > 0 && worn != off && (off ? inHands : onBody),
                    "clips " + string.Join(", ", clips) + " over " + ticks + " ticks, seen on the body " + onBody + ", in the hands " + inHands
                    + ", worn after " + worn);
                Hold(pawn);
                Look(pawn, 0f);
                yield return WaitReal(1f);
                yield return Shot("dress_" + name + "_after");
            }
            Note("Look: dress_Pants_* squatting with the waistband at the knees and stepping in, then pulled up; dress_BasicShirt_* "
                + "raised beside the head and drawn down; dress_Duster_* an arm back into each sleeve; dress_CowboyHat_* the hat "
                + "held up and set on; dress_PowerArmor_* the old clothes taken off first, backwards, then stepped into and the "
                + "arms in, tugging meanwhile; the apparel in the hands before it is on, then on the body.");
        }

        // Stirring while cooking: a simple meal cooked at a fueled stove (stood at it, a bout of work_stir now and then
        // between the bench work: StirOf, Ease.stirAt), at a campfire (sat cross-legged holding a roasting stick out the whole time, the
        // work kind stir_campfire) and sat on a chair at a campfire (hunched forward holding the stick out, stir_campfire_sit).
        private static IEnumerator CookTest()
        {
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("CookMealSimple");
            ThingDef food = DefDatabase<ThingDef>.GetNamedSilentFail("RawPotatoes");
            if (recipe == null || food == null)
            {
                Check("cook", false, "no CookMealSimple recipe or RawPotatoes");
                yield break;
            }
            foreach (var (benchName, want, onChair) in new[] { ("FueledStove", "work_stir", false), ("Campfire", "stir_campfire", false), ("Campfire", "stir_campfire_sit", true) })
            {
                string caseName = benchName + (onChair ? "_chair" : "");
                ThingDef benchDef = DefDatabase<ThingDef>.GetNamedSilentFail(benchName);
                if (benchDef == null)
                {
                    Check(benchName, false, "no such def");
                    continue;
                }
                ClearPatch();
                Thing bench = ThingMaker.MakeThing(benchDef, benchDef.MadeFromStuff ? GenStuff.DefaultStuffFor(benchDef) : null);
                bench.SetFaction(Faction.OfPlayer);
                bench = Spawn(bench, middle, Rot4.South);
                bench.TryGetComp<CompRefuelable>()?.Refuel(50f);
                IntVec3 spot = bench.InteractionCell;
                if (onChair)
                {
                    Thing chair = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                    chair.SetFaction(Faction.OfPlayer);
                    IntVec3 toBench = bench.Position - spot;   // (facing the bench, as the game sits the pawn)
                    Spawn(chair, spot, toBench.z > 0 ? Rot4.North : toBench.z < 0 ? Rot4.South : toBench.x > 0 ? Rot4.East : Rot4.West);
                }
                Thing raw = ThingMaker.MakeThing(food);
                raw.stackCount = 60;
                Spawn(raw, spot + (spot - bench.Position) + new IntVec3(-1, 0, 0));
                IBillGiver giver = bench as IBillGiver;
                if (giver == null)
                {
                    Check(benchName, false, "the bench takes no bills");
                    continue;
                }
                Bill_Production bill = (Bill_Production)recipe.MakeNewBill();
                bill.repeatCount = 4;
                giver.BillStack.AddBill(bill);
                WorkGiverDef workGiver = null;
                foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    if (typeof(WorkGiver_DoBill).IsAssignableFrom(wg.giverClass) && wg.fixedBillGiverDefs != null && wg.fixedBillGiverDefs.Contains(benchDef))
                    {
                        workGiver = wg;
                        break;
                    }
                }
                Pawn pawn = null;
                for (int i = 0; i < 20; i++)
                {
                    pawn = NewPawn(spot + new IntVec3(1, 0, -1), null);
                    if (workGiver == null || !pawn.WorkTypeIsDisabled(workGiver.workType))
                    {
                        break;
                    }
                    pawn.Destroy();
                }
                IntVec3 away = spot - bench.Position;   // (the camera across the bench, at the pawn's front, a little to the side)
                float across = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg + 25f;
                Look(pawn, across);
                lookDistance = 2.2f;
                lookHeight = 0.6f;
                yield return WaitReal(2f);
                string kind = null, stirClip = null;
                float stirred = -1f;
                int shots = 0, jobs = 0, atWork = 0;
                bool bouts = want != null && want.StartsWith("work_");   // (stirred in bouts; else the work kind itself, or pictures only)
                string why = "";
                for (int i = 0; i < 400 && shots < 3; i++)
                {
                    if (pawn.CurJobDef != JobDefOf.DoBill)
                    {
                        JobFailReason.Clear();
                        Job job = workGiver != null && jobs < 4 ? ((WorkGiver_Scanner)workGiver.Worker).JobOnThing(pawn, bench, true) : null;
                        if (job == null)
                        {
                            why = ", the work giver " + (workGiver != null ? workGiver.defName : "none") + " gave none" + (JobFailReason.HaveReason ? ": " + JobFailReason.Reason : "");
                            break;
                        }
                        jobs++;
                        job.playerForced = true;
                        pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    yield return Ticks(10, "cook_" + caseName, pawn);
                    string k = Field<string>(pawn, "workKind");
                    kind = k ?? kind;
                    stirClip = Field<string>(pawn, "stirClip") ?? stirClip;
                    float at = Field<float>(pawn, "stirAt");
                    stirred = Mathf.Max(stirred, at);
                    // (at the work: a picture every 6 game seconds or so, once it has settled into it)
                    bool working = k != null && Field<float>(pawn, "work") > 0.9f;
                    atWork = working ? atWork + 1 : 0;
                    if (bouts ? at > 0.3f && at < 0.7f : working && atWork % 36 == 6)
                    {
                        Look(pawn, across);
                        yield return WaitReal(0.5f);
                        yield return Shot(caseName + (bouts ? "_stir_" : "_work_") + shots++);
                    }
                }
                if (bouts)
                {
                    Check(caseName + ": " + want, stirClip == want, "work kind " + (kind ?? "none") + ", stirring clip " + (stirClip ?? "none") + ", jobs " + jobs + why);
                    Check(caseName + ": stirred", stirred > 0.3f, "stirAt reached " + stirred.ToString("F2"));
                }
                else if (want != null)
                {
                    Check(caseName + ": " + want, kind == want && shots > 0, "work kind " + (kind ?? "none") + ", pictures " + shots + ", jobs " + jobs + why);
                }
                else
                {
                    Note(caseName + ": work kind " + (kind ?? "none") + ", stirring clip " + (stirClip ?? "none") + ", pictures " + shots + ", jobs " + jobs + why);
                }
                lookDistance = LookDistance;
                lookHeight = LookHeight;
            }
            Note("Look: FueledStove_stir_* the spoon in the right fist down into the pot, the left hand on its rim; Campfire_work_* sat " +
                 "cross-legged, the right arm reached out with the spoon toward the fire, the head short of its stones; " +
                 "Campfire_chair_work_* sat on a chair at it hunched forward, the spoon reached out to the fire.");
        }

        // Rolling smokeleaf joints (a Make_SmokeleafJoint bill, WalkFigures.DrawRolledJoint): at the drug lab stood and sat
        // on a chair at its spot, at a crafting spot knelt and sat. The bill's own work giver gives the job, so the pawn
        // fetches the leaves laid beside it and carries them over first. The joint is checked drawn seated only, on the
        // bench's top within its cells at the lab, in the lap at the spot, and not drawn stood or knelt.
        private static IEnumerator RollJointTest()
        {
            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail("Make_SmokeleafJoint");
            ThingDef leaves = DefDatabase<ThingDef>.GetNamedSilentFail("SmokeleafLeaves");
            if (recipe == null || leaves == null)
            {
                Check("rolljoint", false, "no Make_SmokeleafJoint recipe or SmokeleafLeaves");
                yield break;
            }
            var cases = new[] { ("DrugLab", false, "bench"), ("DrugLab", true, "bench_sit"), ("CraftingSpot", false, "crouch"), ("CraftingSpot", true, "chair") };
            foreach (var (benchName, seated, want) in cases)
            {
                ThingDef benchDef = DefDatabase<ThingDef>.GetNamedSilentFail(benchName);
                if (benchDef == null)
                {
                    Check(benchName, false, "no such def");
                    continue;
                }
                ClearPatch();
                string name = benchName + (seated ? "_seated" : "_standing");
                Thing bench = ThingMaker.MakeThing(benchDef, benchDef.MadeFromStuff ? GenStuff.DefaultStuffFor(benchDef) : null);
                bench.SetFaction(Faction.OfPlayer);
                bench = Spawn(bench, middle, Rot4.South);
                IntVec3 spot = bench.InteractionCell;
                if (seated)
                {
                    Thing chair = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                    chair.SetFaction(Faction.OfPlayer);
                    IntVec3 toBench = bench.Position - spot;   // (facing the bench, as the game sits the pawn)
                    Spawn(chair, spot, toBench.z > 0 ? Rot4.North : toBench.z < 0 ? Rot4.South : toBench.x > 0 ? Rot4.East : Rot4.West);
                }
                Thing leaf = ThingMaker.MakeThing(leaves);
                leaf.stackCount = 20;
                Spawn(leaf, spot + (spot - bench.Position) + new IntVec3(-1, 0, 0));   // (behind the spot, off the bench)
                IBillGiver giver = bench as IBillGiver;
                if (giver == null)
                {
                    Check(name, false, "the bench takes no bills");
                    continue;
                }
                giver.BillStack.AddBill(recipe.MakeNewBill());
                // (anything lying on the bench's cells is hauled off first, which the work giver gives in the bill's place)
                foreach (IntVec3 c in giver.IngredientStackCells)
                {
                    foreach (Thing t in new List<Thing>(c.GetThingList(map)))
                    {
                        if (t.def.category == ThingCategory.Item)
                        {
                            Note(name + ": cleared " + t.LabelShort + " off the bench at " + c);
                            t.Destroy();
                        }
                    }
                }
                WorkGiverDef workGiver = null;
                foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    if (typeof(WorkGiver_DoBill).IsAssignableFrom(wg.giverClass) && wg.fixedBillGiverDefs != null && wg.fixedBillGiverDefs.Contains(benchDef))
                    {
                        workGiver = wg;
                        break;
                    }
                }
                Pawn pawn = null;
                for (int i = 0; i < 20; i++)
                {
                    pawn = NewPawn(spot + new IntVec3(1, 0, -1), null);
                    if (workGiver == null || !pawn.WorkTypeIsDisabled(workGiver.workType))
                    {
                        break;
                    }
                    pawn.Destroy();
                }
                IntVec3 away = spot - bench.Position;   // (the camera across the bench, at the pawn's front, a little to the side)
                float across = Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg + 25f;
                Look(pawn, across);
                lookDistance = 2.2f;
                lookHeight = 0.6f;
                yield return WaitReal(2f);
                Job job = workGiver != null ? ((WorkGiver_Scanner)workGiver.Worker).JobOnThing(pawn, bench, true) : null;
                if (job == null)
                {
                    Check(name + ": a job", false, "the work giver " + (workGiver != null ? workGiver.defName : "none") + " gave no job");
                    lookDistance = LookDistance;
                    lookHeight = LookHeight;
                    continue;
                }
                job.playerForced = true;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                string kind = null;
                int drawn = 0, shots = 0;
                bool onTop = true;
                string where = "never drawn";
                int stepsAtWork = 0;
                for (int i = 0; i < 200 && pawn.CurJobDef == JobDefOf.DoBill && shots < 4 && stepsAtWork < 40; i++)
                {
                    yield return Ticks(10, "rolljoint_" + name, pawn);
                    string k = Field<string>(pawn, "workKind");
                    if (k != null && Field<float>(pawn, "work") > 0.5f)
                    {
                        kind = k;
                        stepsAtWork++;
                        if (!seated && stepsAtWork % 10 == 5)
                        {
                            Look(pawn, across);
                            yield return WaitReal(0.5f);
                            yield return Shot(name + "_" + shots++);
                        }
                    }
                    if (WalkFigures.rolledJointFrame >= Time.frameCount - 1)
                    {
                        drawn++;
                        Vector3 j = WalkFigures.rolledJointAt;
                        float over = j.y - (benchName == "DrugLab" ? IsoRim.Api.SurfaceHeight(bench) : 0f);
                        bool inside = bench.OccupiedRect().Contains(j.ToIntVec3());
                        where = "at " + j.ToString("F2") + ", " + over.ToString("F3") + " cells over the " + (benchName == "DrugLab" ? "bench top" : "ground")
                            + (benchName == "DrugLab" ? (inside ? ", over the bench" : ", off the bench") : "");
                        if (want != "chair" && (Mathf.Abs(over) > 0.05f || benchName == "DrugLab" && !inside))
                        {
                            onTop = false;
                        }
                        if (drawn % 10 == 1)
                        {
                            Look(pawn, across);
                            yield return WaitReal(0.5f);
                            yield return Shot(name + "_" + shots++);
                        }
                    }
                }
                Check(name + ": " + want, kind == want, "work kind " + (kind ?? "none") + ", job " + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none"));
                if (seated)
                {
                    Check(name + ": the joint drawn", drawn > 0, "drawn on " + drawn + " steps");
                    Check(name + ": where it lies", drawn > 0 && onTop, where);
                }
                else
                {
                    Check(name + ": no joint stood or knelt", drawn == 0 && kind == want, "drawn on " + drawn + " steps" + (kind == want ? "" : ", never at the work"));
                }
                if (pawn.jobs != null)
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                lookDistance = LookDistance;
                lookHeight = LookHeight;
            }
            Note("Look: sat at the drug lab, an unlit joint (no ember) lying across on its top in front of the hands; sat at the crafting spot, in the lap; stood at the lab and knelt at the spot, none (the game's leaves on the spot).");
        }

        // The slicecap (Ideology's), a crescent of shell over the left of the scalp, worn over the hair (over_hair,
        // HatOverHair.cs): the hair left whole and the cap pushed out past it, on a few styles from short to big.
        // One bare pawn given each set of added parts in turn (Prosthetics.cs), seen from the front and the back: the
        // limbs coloured as what they are made of, the joints darker, a peg leg a peg down to the ground.
        private static IEnumerator ProstheticsTest()
        {
            Rand.PushState(20261112);
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
            Hold(pawn);
            Look(pawn, 0f);
            lookDistance = 2.2f;
            lookHeight = 0.7f;
            // Each set: its name, then added part, body part, side.
            string[][] sets =
            {
                new[] { "wood", "PegLeg", "Leg", "left", "WoodenHand", "Hand", "right", "WoodenFoot", "Foot", "right" },
                new[] { "simple", "SimpleProstheticLeg", "Leg", "left", "SimpleProstheticArm", "Shoulder", "right" },
                new[] { "bionic", "BionicLeg", "Leg", "right", "BionicArm", "Shoulder", "left", "PowerClaw", "Hand", "right", "BionicEye", "Eye", "left" },
                new[] { "archotech", "ArchotechLeg", "Leg", "left", "ArchotechLeg", "Leg", "right", "ArchotechArm", "Shoulder", "left", "ArchotechArm", "Shoulder", "right", "ArchotechEye", "Eye", "right" },
            };
            foreach (string[] set in sets)
            {
                pawn.health.RemoveAllHediffs();
                int wanted = 0;
                for (int i = 1; i + 2 < set.Length; i += 3)
                {
                    HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(set[i]);
                    BodyPartRecord part = null;
                    foreach (BodyPartRecord p in pawn.RaceProps.body.AllParts)
                    {
                        if (p.def.defName == set[i + 1] && WalkFigures.SideOf(p) == (set[i + 2] == "left" ? "L" : "R"))
                        {
                            part = p;
                            break;
                        }
                    }
                    if (def == null || part == null)
                    {
                        Note(set[0] + ": no " + set[i] + " or no " + set[i + 2] + " " + set[i + 1]);
                        continue;
                    }
                    pawn.health.AddHediff(def, part);
                    wanted++;
                }
                yield return Ticks(5, "prosthetics", pawn);
                string limbs = WalkFigures.AutoLimbs(pawn);
                int found = 0;
                foreach (char c in limbs)
                {
                    found += c != '0' ? 1 : 0;
                }
                bool eyed = set[0] == "bionic" || set[0] == "archotech";
                if (eyed || set[0] == "wood")
                {
                    found += eyed ? 1 : 0;   // its eye, which is not a limb: seen in the face shots
                    Look(pawn, 0f);
                    HairDef hair = pawn.story.hairDef;
                    pawn.story.hairDef = DefDatabase<HairDef>.GetNamedSilentFail("Bald") ?? hair;   // so it hides neither eye
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                    yield return Ticks(2, "prosthetics", pawn);
                    lookDistance = 1.2f;
                    lookHeight = 1.0f;
                    lookPitch = 20f;
                    yield return Shot(set[0] + "_eye");
                    // Facial Animation's gaze (FaceGaze.cs), forced well past its own: right (x), then up (z), in its units.
                    foreach (KeyValuePair<string, Vector3> gaze in new[] { new KeyValuePair<string, Vector3>("right", new Vector3(0.015f, 0f, 0f)), new KeyValuePair<string, Vector3>("up", new Vector3(0f, 0f, 0.015f)) })
                    {
                        FaceLayers.TestGaze = gaze.Value;
                        yield return Ticks(2, "prosthetics", pawn);
                        yield return Shot(set[0] + "_eye_look_" + gaze.Key);
                    }
                    FaceLayers.TestGaze = Vector3.zero;
                    Note(set[0] + " irises: " + FaceLayers.IrisReport());
                    if (eyed && SettingsMod.Settings != null)
                    {
                        SettingsMod.Settings.prostheticEyes = false;
                        yield return Ticks(3, "prosthetics", pawn);
                        yield return Shot(set[0] + "_eye_off");
                        SettingsMod.Settings.prostheticEyes = true;
                    }
                    pawn.story.hairDef = hair;
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                    lookDistance = 2.2f;
                    lookHeight = 0.7f;
                    lookPitch = LookPitch;
                }
                Check(set[0] + ": every added part has a look", found == wanted, limbs + ", " + wanted + " parts added");
                string hurt = WalkFigures.AutoHurt(pawn);
                Check(set[0] + ": no hurt place held", hurt.StartsWith("hold 0.00", StringComparison.Ordinal), hurt);
                // A limp only where one leg works worse than the other: the peg, and a simple leg beside a natural one.
                bool limps = set[0] == "wood" || set[0] == "simple";
                Check(set[0] + (limps ? ": limps" : ": walks evenly"), hurt.Contains(", limp 0.00") != limps, hurt);
                foreach (float yaw in new[] { 0f, 180f })
                {
                    lookYaw = yaw;
                    yield return Shot(set[0] + "_" + (int)yaw);
                }
                lookYaw = 0f;
                if (set[0] == "wood" || set[0] == "simple")
                {
                    // Dressed: what a trouser leg or sleeve does over a peg or a pylon.
                    foreach (string worn in new[] { "Apparel_Pants", "Apparel_BasicShirt" })
                    {
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(worn);
                        if (def != null)
                        {
                            pawn.apparel.Wear((Apparel)ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def)));
                        }
                    }
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                    yield return Ticks(3, "prosthetics", pawn);
                    foreach (float yaw in new[] { 0f, 180f })
                    {
                        lookYaw = yaw;
                        yield return Shot(set[0] + "_dressed_" + (int)yaw);
                    }
                    lookYaw = 0f;
                    pawn.apparel.DestroyAll();
                }
                if (set[0] == "archotech" && SettingsMod.Settings != null)
                {
                    SettingsMod.Settings.archotechProsthetics = false;
                    yield return Ticks(3, "prosthetics", pawn);
                    Check("archotech off: drawn as flesh", WalkFigures.AutoLimbs(pawn) == "00000000", WalkFigures.AutoLimbs(pawn));
                    yield return Shot("archotech_off");
                    SettingsMod.Settings.archotechProsthetics = true;
                }
            }
            lookDistance = LookDistance;
            lookHeight = LookHeight;
            Note("Look: in the _eye_look_right shots both irises (and a prosthetic one) moved to the picture's right, in _eye_look_up up, within the eye's white; ");
            Note("Look: the bionic eye (the pawn's left) dark steel with a cyan iris, the archotech eye (its right) olive with a yellow iris; both blink with the lids. Then wood brown, simple a beige socket and foot or hand on a thin steel pylon, bionic dark blue metal with panel seams and cyan lights, archotech pale olive with yellow circuit traces and inset panels, each with darker joints; the peg leg a thin peg reaching the ground.");
        }

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
        // The face drawn eight layers a draw (FaceLayers.DrawStacked) against a draw per layer, close up, the same frame each
        // time (paused, so Facial Animation's face is the same in both): the pictures in each pair should match.
        // A bald man with nothing on in a few beards with a gap round the mouth, close up from the front, as the flat
        // beard (on the face patch) and the 3D one, the mouth shut and forced open (FaceLayers.TestMouthOpen), the face's
        // layers stacked and drawn one by one: Facial Animation's mouth should show in the beard's gap.
        private static IEnumerator BeardTest()
        {
            Rand.PushState(20261113);
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
            Hold(pawn);
            pawn.story.hairDef = DefDatabase<HairDef>.GetNamedSilentFail("Bald") ?? pawn.story.hairDef;
            Note("Pawn: " + pawn.gender + ", head " + pawn.story.headType?.defName + ", body " + pawn.story.bodyType?.defName);
            string widen = Environment.GetEnvironmentVariable("M3AT_WIDEN");   // HairBuilder.WidenEyes, to compare (the cache keeps the first built)
            if (!string.IsNullOrEmpty(widen))
            {
                HairBuilder.WidenEyes = float.Parse(widen, System.Globalization.CultureInfo.InvariantCulture);
            }
            Note("Beards widened " + HairBuilder.WidenEyes + " at the eyes");
            BeardMode mode = SettingsMod.Settings.beardMode;
            float distance = lookDistance, pitch = lookPitch, height = lookHeight;
            lookDistance = 1.0f;
            lookHeight = 0.95f;
            lookPitch = 10f;
            Look(pawn, 0f);
            foreach (string name in new[] { "Circle", "Anchor", "Full", "Moustache", "MuttonChops", "SideWhiskers", "Wizard" })
            {
                BeardDef def = DefDatabase<BeardDef>.GetNamedSilentFail(name);
                if (def == null)
                {
                    Note("no " + name);
                    continue;
                }
                pawn.style.beardDef = def;
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                foreach (BeardMode m in new[] { BeardMode.Flat, BeardMode.ThreeD })
                {
                    SettingsMod.Settings.beardMode = m;
                    yield return Ticks(3, "beard", pawn);
                    yield return WaitReal(3f);   // the 3D beard built
                    yield return Ticks(3, "beard", pawn);
                    string tag = name + "_" + (m == BeardMode.Flat ? "flat" : "3d");
                    FaceLayers.TestMouthOpen = false;
                    yield return Shot(tag + "_shut");
                    FaceLayers.TestMouthOpen = true;
                    yield return Ticks(2, "beard", pawn);
                    yield return Shot(tag + "_open");
                    FaceLayers.StackLayers = false;
                    yield return Ticks(2, "beard", pawn);
                    yield return Shot(tag + "_open_layers");
                    FaceLayers.StackLayers = true;
                    FaceLayers.TestMouthOpen = false;
                }
            }
            SettingsMod.Settings.beardMode = mode;
            lookDistance = distance;
            lookPitch = pitch;
            lookHeight = height;
            Note("Look: the mouth (shut, then open) in the gap between the moustache and the chin beard of Circle and Anchor, under Moustache, the same in _flat and _3d and in _open and _open_layers.");
        }

        private static IEnumerator FaceAbTest()
        {
            List<Pawn> pawns = new List<Pawn>();
            for (int i = 0; i < 4; i++)
            {
                Pawn pawn = NewPawn(middle + new IntVec3(2 * i - 3, 0, 0), null);
                Hold(pawn);
                pawns.Add(pawn);
            }
            yield return WaitReal(4f);   // their hair built
            float distance = lookDistance, pitch = lookPitch, height = lookHeight;
            lookPitch = 20f;
            lookDistance = 1.3f;
            lookHeight = 1.0f;
            for (int k = 0; k < pawns.Count; k++)
            {
                Look(pawns[k], 0f);
                for (int sample = 0; sample < 3; sample++)
                {
                    yield return Ticks(40 + 37 * sample, "faceab", pawns[k]);
                    FaceLayers.StackLayers = true;
                    yield return Shot("p" + k + "_s" + sample + "_stacked");
                    FaceLayers.StackLayers = false;
                    yield return Shot("p" + k + "_s" + sample + "_layers");
                    FaceLayers.StackLayers = true;
                }
            }
            lookDistance = distance;
            lookPitch = pitch;
            lookHeight = height;
            Note("Look: each _stacked picture the same as the _layers one after it: the eyes, lids, brows, mouth and blush in the same places and colours.");
        }

        // The same check through what Facial Animation and its Experimentals animate: a pawn put in each state in turn, its
        // face taken stacked and a draw per layer the same frame, three times over the state's animation. Also the stacked
        // drawing's own odd cases: a missing eye (its halves cut, the closed lid on its own), a wound on the eye (drawn a
        // layer at a time), the pig snout (the lowered mouth) and red eyes (Facial Animation's colours reloaded).
        private static IEnumerator FaceAnimTest(string only)
        {
            float distance = lookDistance, pitch = lookPitch, height = lookHeight;
            string[] cases = { "idle", "drafted", "eating", "pain", "naked", "gloomy", "hopeless", "cheerful", "asleep", "anaesthetic", "painshock",
                "missing_eye", "eye_wound", "pig_snout", "red_eyes" };
            foreach (string what in cases)
            {
                if (only != null && what != only)
                {
                    continue;
                }
                Pawn pawn = NewPawn(middle, null);
                Pawn other = null;
                Hold(pawn);
                bool lying = false;
                string skipped = null;
                switch (what)
                {
                    case "drafted":
                        pawn.drafter.Drafted = true;
                        break;
                    case "talking":
                        other = NewPawn(middle + new IntVec3(1, 0, 0), null);   // beside it, out of the camera's way
                        Hold(other);
                        pawn.Rotation = Rot4.South;
                        other.Rotation = Rot4.West;
                        break;
                    case "eating":
                        if (pawn.needs.food != null)
                        {
                            pawn.needs.food.CurLevel = 0.05f;
                        }
                        Thing meal = Spawn(ThingMaker.MakeThing(ThingDefOf.MealSimple), middle + new IntVec3(1, 0, 0));
                        pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Ingest, meal), JobCondition.InterruptForced);
                        break;
                    case "pain":
                        // Pain 0.5 (Experimentals' tears from 0.4), short of the pain shock that downs it at 0.8.
                        for (int k = 0; k < 40 && pawn.health.hediffSet.PainTotal < 0.5f && !pawn.Downed; k++)
                        {
                            FaceHurt(pawn, 1, 3f);
                        }
                        break;
                    case "naked":
                        pawn.apparel.DestroyAll();
                        break;
                    case "gloomy":
                    case "hopeless":
                    case "cheerful":
                        break;   // the mood is set before each sample, below
                    case "asleep":
                        Thing bed = ThingMaker.MakeThing(ThingDef.Named("Bed"), ThingDefOf.WoodLog);
                        bed.SetFaction(Faction.OfPlayer);
                        Building_Bed b = (Building_Bed)Spawn(bed, middle + new IntVec3(0, 0, 2), Rot4.North);
                        b.TryGetComp<CompAssignableToPawn>().TryAssignPawn(pawn);
                        if (pawn.needs.rest != null)
                        {
                            pawn.needs.rest.CurLevel = 0.05f;
                        }
                        pawn.jobs.Notify_TuckedIntoBed(b);
                        lying = true;
                        break;
                    case "anaesthetic":
                        pawn.health.AddHediff(HediffDefOf.Anesthetic);
                        lying = true;
                        break;
                    case "painshock":
                        FaceHurt(pawn, 12, 9f);
                        lying = true;
                        break;
                    case "missing_eye":
                    case "eye_wound":
                        BodyPartRecord eye = pawn.health.hediffSet.GetNotMissingParts().FirstOrDefault(p => p.def.defName == "Eye");
                        if (eye == null)
                        {
                            skipped = "no eye part";
                        }
                        else if (what == "missing_eye")
                        {
                            pawn.health.AddHediff(HediffDefOf.MissingBodyPart, eye);
                        }
                        else
                        {
                            Hediff_Injury cut = (Hediff_Injury)HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Cut"), pawn, eye);
                            cut.Severity = 3f;
                            pawn.health.AddHediff(cut);
                        }
                        break;
                    case "pig_snout":
                    case "red_eyes":
                        GeneDef gene = DefDatabase<GeneDef>.GetNamedSilentFail(what == "pig_snout" ? "Nose_Pig" : "Eyes_Red");
                        if (gene == null || pawn.genes == null)
                        {
                            skipped = "no " + (what == "pig_snout" ? "Nose_Pig" : "Eyes_Red") + " gene (Biotech)";
                        }
                        else
                        {
                            pawn.genes.AddGene(gene, true);
                        }
                        break;
                }
                if (skipped != null)
                {
                    Note(what + ": skipped, " + skipped);
                    DestroyAll(pawn, other);
                    continue;
                }
                yield return WaitReal(3f);   // its hair built
                for (int sample = 0; sample < 3; sample++)
                {
                    // (Downed where it stood it falls as a ragdoll: let it settle first.)
                    if (other != null)
                    {
                        // A word each sample, so the mouth has something to say (Facial Animation's talking).
                        pawn.interactions.TryInteractWith(other, InteractionDefOf.Chitchat);
                    }
                    // Mood as Experimentals reads it (its share of the most), held there, as it drifts back to what the thoughts make it.
                    float mood = what == "gloomy" ? 0.3f : what == "hopeless" ? 0.1f : what == "cheerful" ? 0.9f : -1f;
                    if (mood >= 0f && pawn.needs.mood != null)
                    {
                        pawn.needs.mood.CurLevelPercentage = mood;
                    }
                    yield return Ticks(sample == 0 ? (what == "painshock" ? 600 : 130) : 65, "faceanim " + what, pawn);
                    Note(what + " " + sample + ": " + FaceLayers.FaState(pawn));
                    // Framed on the face: from the front standing, from above the head lying on its back.
                    lookAt = pawn.DrawPos;
                    // (In bed the head lies at the bed's north end: seen from beyond it, looking back down the bed.)
                    lookYaw = what == "asleep" ? 180f : lying ? 0f : 180f + pawn.Rotation.AsAngle;
                    lookPitch = what == "asleep" ? 50f : lying ? 70f : 20f;
                    lookDistance = what == "painshock" ? 2.2f : lying ? 1.2f : 1.3f;
                    lookHeight = lying ? 0.3f : 1.0f;
                    FaceLayers.StackLayers = true;
                    yield return Shot(what + "_" + sample + "_stacked");
                    FaceLayers.StackLayers = false;
                    yield return Shot(what + "_" + sample + "_layers");
                    FaceLayers.StackLayers = true;
                }
                Probe("faceanim " + what + " end", pawn);
                DestroyAll(pawn, other);
            }
            lookDistance = distance;
            lookPitch = pitch;
            lookHeight = height;
            Note("Look: each _stacked picture the same as the _layers one after it, in every state: blinking and looking about, drafted, talking, eating, pain, the naked blush, sad, asleep, unconscious, downed in pain, a missing eye, a wounded eye, the pig snout's lowered mouth and red eyes.");
        }

        // Every Facial Animation animation both mods define, played on one pawn as its face editor previews them, its face
        // taken stacked and a draw per layer the same frame: tears, blush, sweat, cold, the moods, the mouth shapes, flinching.
        private static IEnumerator FaceShapesTest(string[] only)
        {
            float distance = lookDistance, pitch = lookPitch, height = lookHeight;
            Pawn pawn = NewPawn(middle, null);
            Hold(pawn);
            yield return WaitReal(3f);   // its hair built
            lookAt = pawn.DrawPos;
            lookYaw = 0f;
            lookPitch = 20f;
            lookDistance = 1.3f;
            lookHeight = 1.0f;
            List<string> all = only != null ? new List<string>(only) : FaceLayers.FaAnimations();
            int[] at = only != null ? new[] { 1, 4, 15 } : new[] { 1 };   // ticks into it each picture is taken at
            Note(all.Count + " animations: " + string.Join(", ", all));
            yield return Shot("neutral_stacked");
            foreach (string anim in all)
            {
                // From the next tick, then that one tick stepped: a one-tick animation shows and is held, the game paused.
                if (!FaceLayers.FaPlay(pawn, Find.TickManager.TicksGame + 1, anim))
                {
                    Note(anim + ": wouldn't play");
                    continue;
                }
                int done = 0;
                foreach (int t in at)
                {
                    yield return Ticks(t - done, "faceshapes " + anim, pawn);
                    done = t;
                    Note(anim + " +" + t + ": " + FaceLayers.FaState(pawn));
                    FaceLayers.StackLayers = true;
                    yield return Shot(anim + "_t" + t + "_stacked");
                    FaceLayers.StackLayers = false;
                    yield return Shot(anim + "_t" + t + "_layers");
                    FaceLayers.StackLayers = true;
                }
            }
            lookDistance = distance;
            lookPitch = pitch;
            lookHeight = height;
            Note("Look: each _stacked picture the same as its _layers one, and each animation's face different from neutral where it changes the face.");
        }

        private static int hurtNext;

        private static void FaceHurt(Pawn pawn, int count, float severity)
        {
            List<BodyPartRecord> parts = pawn.health.hediffSet.GetNotMissingParts().Where(p => p.depth == BodyPartDepth.Outside && p.def.hitPoints >= 20 && p.def.defName != "Eye").ToList();
            for (int i = 0; i < count && parts.Count > 0; i++)
            {
                hurtNext++;   // each cut on another part, so none is cut away
                Hediff_Injury cut = (Hediff_Injury)HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Cut"), pawn, parts[(hurtNext * 5) % parts.Count]);
                cut.Severity = severity;
                pawn.health.AddHediff(cut);
            }
        }

        private static void DestroyAll(params Pawn[] pawns)
        {
            foreach (Pawn p in pawns)
            {
                if (p != null && !p.Destroyed)
                {
                    p.Destroy();
                }
            }
        }

        // N of IsoRim's -isorimseed=N (Api 72), which autotest.sh passes; called only on version 72 or later.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int? HarnessSeedOrNull() => IsoRim.Api.HarnessSeed;

        // IsoRim's 3D explosions (Api 74) beside the chibi: a frag-sized blast watched from a few cells off (the fireball
        // through its life, pictures at set ticks), a shell-sized one on two colonists (the burst with the flings and
        // ragdolls), and a flame blast, which IsoRim leaves flat. The bursts age on game ticks, so stepping holds each still.
        private static IEnumerator ExplodeTest()
        {
            float distanceWas = lookDistance, heightWas = lookHeight, pitchWas = lookPitch;
            lookDistance = 9f;
            lookHeight = 0.8f;
            lookPitch = 24f;
            int madeBefore = IsoRim.Api.Dev.ExplosionBursts, heldBefore = IsoRim.Api.Dev.ExplosionFlecksHeld;

            // 1. Watched: a frag grenade's blast (Bomb, 1.9 cells) four cells from a colonist standing, out of its reach.
            IntVec3 blast = middle + new IntVec3(1, 0, 0);
            Pawn watcher = NewPawn(middle + new IntVec3(-4, 0, 0), null);
            Hold(watcher);
            watcher.Rotation = Rot4.East;
            lookAt = blast.ToVector3Shifted();
            lookYaw = 0f;
            yield return WaitReal(3f);   // its hair built
            yield return Shot("watch_before");
            GenExplosion.DoExplosion(blast, map, 1.9f, DamageDefOf.Bomb, null);
            int born = Find.TickManager.TicksGame, live = 0, drawn = 0, lasted = -1;
            int[] at = { 1, 4, 10, 20, 40, 70, 110 };
            foreach (int t in at)
            {
                yield return Ticks(born + t - Find.TickManager.TicksGame, "explode", null);
                live = Math.Max(live, IsoRim.Api.Dev.ExplosionBurstsLive);
                yield return Shot("watch_t" + t.ToString("D3"));
                drawn = Math.Max(drawn, IsoRim.Api.Dev.ExplosionPartsDrawn);
                Note("tick " + t + ": " + IsoRim.Api.Dev.ExplosionBurstsLive + " burst(s) alive, " + IsoRim.Api.Dev.ExplosionPartsDrawn + " parts drawn");
            }
            for (int i = 0; i < 600 && IsoRim.Api.Dev.ExplosionBurstsLive > 0; i++)
            {
                yield return Ticks(1, "explode", null);
            }
            lasted = IsoRim.Api.Dev.ExplosionBurstsLive == 0 ? Find.TickManager.TicksGame - born : -1;
            yield return Shot("watch_after");
            int made = IsoRim.Api.Dev.ExplosionBursts - madeBefore;
            Check("watched: a 3D burst made", made == 1, made + " made (0: IsoRim's \"3D explosions\" option is off, or it failed; see the log)");
            Check("watched: the game's flash and dust flecks held back", IsoRim.Api.Dev.ExplosionFlecksHeld > heldBefore, (IsoRim.Api.Dev.ExplosionFlecksHeld - heldBefore) + " held");
            Check("watched: drawn", drawn > 0, "most parts drawn in a frame " + drawn);
            Check("watched: over", lasted > 0, lasted > 0 ? "gone after " + lasted + " ticks" : "still alive 600 ticks on");
            Check("watched: the colonist unhurt", !watcher.Dead && !watcher.Downed, watcher.Dead ? "dead" : watcher.Downed ? "downed" : "standing");

            // 2. Caught: a mortar shell's blast (Bomb, 2.9 cells) between two colonists, one a cell from it, one two cells.
            ClearPatch();
            ClearBodies();
            Pawn near = NewPawn(blast + new IntVec3(-1, 0, 0), null);
            Pawn far = NewPawn(blast + new IntVec3(0, 0, 2), null);
            Hold(near);
            Hold(far);
            lookAt = blast.ToVector3Shifted();
            yield return WaitReal(3f);
            yield return Shot("caught_before");
            IntVec3 nearFrom = near.Position, farFrom = far.Position;
            madeBefore = IsoRim.Api.Dev.ExplosionBursts;
            GenExplosion.DoExplosion(blast, map, 2.9f, DamageDefOf.Bomb, null, damAmount: 60);
            born = Find.TickManager.TicksGame;
            // Run at normal speed in short slices, paused for each picture: a ragdoll steps on frame time times the game's
            // speed, so stepped ticks alone leave the bodies standing as they were hit. The pictures go by the tick reached.
            foreach (int t in new[] { 2, 6, 14, 30, 60, 120, 240 })
            {
                int until = born + t;
                yield return Unpaused(null, () => Find.TickManager.TicksGame >= until, 10f);
                yield return Shot("caught_t" + (Find.TickManager.TicksGame - born).ToString("D3"));
            }
            Check("caught: a 3D burst made", IsoRim.Api.Dev.ExplosionBursts - madeBefore == 1, (IsoRim.Api.Dev.ExplosionBursts - madeBefore) + " made");
            Check("caught: the near colonist went down", near.Dead || near.Downed, near.Dead ? "dead" : near.Downed ? "downed" : "standing");
            IntVec3 nearNow = near.Dead && near.Corpse != null ? near.Corpse.Position : near.Position;
            IntVec3 farNow = far.Dead && far.Corpse != null ? far.Corpse.Position : far.Position;
            Note("near: " + (near.Dead ? "dead" : near.Downed ? "downed" : "standing") + ", " + nearFrom + " -> " + nearNow
                + "; far: " + (far.Dead ? "dead" : far.Downed ? "downed" : "standing") + ", " + farFrom + " -> " + farNow
                + " (flingBodies " + WalkFigures.FlingBodies + ", ragdolls " + WalkFigures.Ragdolls + ")");
            if (WalkFigures.FlingBodies && (near.Dead || near.Downed))
            {
                Check("caught: the near colonist thrown", nearNow != nearFrom, nearFrom + " -> " + nearNow);
            }

            // 3. Flat: a flame blast, which IsoRim leaves as the game's.
            ClearPatch();
            ClearBodies();
            lookAt = blast.ToVector3Shifted();
            madeBefore = IsoRim.Api.Dev.ExplosionBursts;
            GenExplosion.DoExplosion(blast, map, 1.9f, DamageDefOf.Flame, null, damAmount: 1);
            yield return Ticks(6, "explode", null);
            yield return Shot("flame_t006");
            Check("flame: left flat", IsoRim.Api.Dev.ExplosionBursts == madeBefore, (IsoRim.Api.Dev.ExplosionBursts - madeBefore) + " 3D bursts made");
            ClearBodies();
            Note("Look: in watch_t* the fireball swells and cools, smoke rises from its top and a dust ring spreads, next to the chibi "
                + "(outlines, light and sizes alike: the burst is about 1.9 cells across at most); in caught_t* the colonists thrown "
                + "and falling limp through the fire; flame_t006 the game's flat flame.");
            lookDistance = distanceWas;
            lookHeight = heightWas;
            lookPitch = pitchWas;
        }

        // Corpses and dropped things the patch's blasts left (ClearPatch moves pawns off and clears items, not corpses).
        private static void ClearBodies()
        {
            foreach (Thing t in new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)))
            {
                if (t.Position.InHorDistOf(middle, Clear + 6))
                {
                    t.Destroy();
                }
            }
        }

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

        // The everyday life clips: a wave and a glance at a chat in passing (wave), a sad wander slumped (sad), patting at
        // the flames on itself while on fire (burning), a catatonic breakdown lying still rather than writhing
        // (catatonic), a lamp flicked by reaching to it (flick) and a generator and a campfire refuelled at their height
        // (refuel). only: one of those, or all.
        private static IEnumerator LivelyTest(string only)
        {
            if (only == null || only == "wave")
            {
                ClearPatch();
                Pawn a = NewPawn(middle + new IntVec3(-1, 0, 0), null);
                Pawn b = NewPawn(middle + new IntVec3(1, 0, 1), null);
                Hold(a);
                Hold(b);
                Look(a, 0f);
                yield return WaitReal(3f);
                bool done = a.interactions.TryInteractWith(b, InteractionDefOf.Chitchat);
                float wave = -1f, look = 0f, lookB = 0f;
                for (int i = 0; i < 8; i++)
                {
                    yield return Ticks(12, "wave", a);
                    wave = Mathf.Max(wave, Field<float>(a, "waveAt"));
                    look = Mathf.Max(look, Mathf.Abs(Field<float>(a, "look")));
                    lookB = Mathf.Max(lookB, Mathf.Abs(Field<float>(b, "look")));
                    if (i == 2 || i == 4)
                    {
                        yield return Shot("wave_t" + (i + 1) * 12);
                    }
                }
                Check("wave: interaction made", done, done ? "chitchat" : "the game refused it");
                Check("wave: played", wave > 0.3f, "waveAt reached " + wave.ToString("F2"));
                Check("wave: the initiator glances", look > 5f, look.ToString("F0") + " degrees");
                Check("wave: the recipient glances back", lookB > 5f, lookB.ToString("F0") + " degrees");
            }
            if (only == null || only == "sad")
            {
                ClearPatch();
                Pawn pawn = NewPawn(middle, null);
                Look(pawn, 0f);
                yield return WaitReal(3f);
                bool started = pawn.mindState.mentalStateHandler.TryStartMentalState(DefDatabase<MentalStateDef>.GetNamed("Wander_Sad"), forced: true);
                float sad = 0f;
                for (int i = 0; i < 12; i++)
                {
                    yield return Ticks(15, "sad", pawn);
                    lookAt = pawn.DrawPos;
                    sad = Mathf.Max(sad, Field<float>(pawn, "sad"));
                    if (i == 7 || i == 11)
                    {
                        yield return Shot("sad_t" + (i + 1) * 15);
                    }
                }
                Check("sad: mental state", started, started ? "Wander_Sad" : "not started");
                Check("sad: slumped", sad > 0.5f, "sad " + sad.ToString("F2"));
                pawn.mindState.mentalStateHandler.Reset();
            }
            if (only == null || only == "burning")
            {
                ClearPatch();
                Pawn pawn = NewPawn(middle, null);
                pawn.drafter.Drafted = true;
                Look(pawn, 0f);
                yield return WaitReal(3f);
                pawn.TryAttachFire(0.4f, null);
                float burn = 0f;
                for (int i = 0; i < 8; i++)
                {
                    yield return Ticks(8, "burning", pawn);
                    burn = Mathf.Max(burn, Field<float>(pawn, "burn"));
                    if (i == 3 || i == 6)
                    {
                        yield return Shot("burning_t" + (i + 1) * 8);
                    }
                }
                Check("burning: on fire", pawn.IsBurning() || burn > 0f, pawn.IsBurning() ? "burning" : "the fire went out");
                Check("burning: patting", burn > 0.5f, "burn " + burn.ToString("F2") + ", job " + pawn.CurJobDef?.defName);
                (pawn.GetAttachment(ThingDefOf.Fire) as Fire)?.Destroy();
            }
            if (only == null || only == "catatonic")
            {
                ClearPatch();
                Pawn pawn = NewPawn(middle, null);
                Look(pawn, 0f);
                yield return WaitReal(3f);
                pawn.health.AddHediff(DefDatabase<HediffDef>.GetNamed("CatatonicBreakdown"));
                yield return Ticks(60, "catatonic", pawn);
                yield return WaitReal(2f);
                yield return Ticks(30, "catatonic", pawn);
                MethodInfo lieClip = typeof(WalkFigures).GetMethod("LieClip", BindingFlags.NonPublic | BindingFlags.Static);
                string clip = lieClip != null ? (string)lieClip.Invoke(null, new object[] { pawn, false, false, false }) : null;
                yield return Shot("catatonic");
                Check("catatonic: downed", pawn.Downed, "downed " + pawn.Downed + ", awake " + pawn.Awake());
                Check("catatonic: lies still as asleep", clip != null && clip.StartsWith("sleep"), "lying clip " + (clip ?? "none"));
            }
            if (only == null || only == "flick")
            {
                ClearPatch();
                ThingDef lampDef = DefDatabase<ThingDef>.GetNamed("StandingLamp");
                Thing lamp = ThingMaker.MakeThing(lampDef, lampDef.MadeFromStuff ? ThingDefOf.Steel : null);
                lamp.SetFaction(Faction.OfPlayer);
                Spawn(lamp, middle + new IntVec3(1, 0, 0), Rot4.North);
                Pawn pawn = NewPawn(middle + new IntVec3(-1, 0, 0), null);
                Look(pawn, 0f);
                yield return WaitReal(3f);
                string kind = null;
                for (int i = 0; i < 4 && kind == null; i++)
                {
                    lamp.Map.designationManager.RemoveAllDesignationsOn(lamp);
                    lamp.Map.designationManager.AddDesignation(new Designation(lamp, DesignationDefOf.Flick));
                    Job job = JobMaker.MakeJob(JobDefOf.Flick, lamp);
                    job.playerForced = true;
                    pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                    for (int t = 0; t < 30; t++)
                    {
                        yield return Ticks(2, "flick", pawn);
                        string k = Field<string>(pawn, "workKind");
                        if (k != null && Field<float>(pawn, "work") > 0.3f)
                        {
                            kind = k;
                            yield return Shot("flick");
                            break;
                        }
                    }
                }
                Check("flick: reaching to a standing lamp", kind == "reach", "work kind " + (kind ?? "none"));
            }
            if (only == null || only == "refuel")
            {
                foreach (string what in new[] { "WoodFiredGenerator", "Campfire" })
                {
                    ClearPatch();
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(what);
                    Thing built = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.WoodLog : null);
                    built.SetFaction(Faction.OfPlayer);
                    Spawn(built, middle + new IntVec3(2, 0, 0), Rot4.North);
                    CompRefuelable fuel = built.TryGetComp<CompRefuelable>();
                    fuel?.ConsumeFuel(fuel.Fuel);
                    Thing logs = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                    logs.stackCount = 75;
                    Spawn(logs, middle + new IntVec3(-1, 0, -1));
                    Pawn pawn = NewPawn(middle + new IntVec3(-1, 0, 0), null);
                    Look(pawn, 0f);
                    yield return WaitReal(3f);
                    Job job = RefuelWorkGiverUtility.RefuelJob(pawn, built, forced: true);
                    if (job == null)
                    {
                        Check("refuel " + what + ": job", false, "no refuel job");
                        continue;
                    }
                    pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                    string kind = null;
                    for (int t = 0; t < 60 && kind == null; t++)
                    {
                        yield return Ticks(5, "refuel_" + what, pawn);
                        lookAt = pawn.DrawPos;
                        string k = Field<string>(pawn, "workKind");
                        if (k != null && Field<float>(pawn, "work") > 0.5f)
                        {
                            kind = k;
                            yield return Shot("refuel_" + what);
                        }
                    }
                    string want = what == "Campfire" ? "crouch" : "bench";
                    Check("refuel " + what + ": " + want, kind == want, "work kind " + (kind ?? "none"));
                }
            }
            Note("Look: wave_t36 the right hand up beside the head waving at the other, both heads turned to each other; " +
                 "sad_* slumped with the head hung on the walk; burning_* both hands patting at the chest and belly; " +
                 "catatonic lying still on the side, not writhing; flick the right hand out to the lamp; refuel_* stood at " +
                 "the generator and knelt at the campfire.");
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

        // Praying (Ideology's MeditatePray) on the bare floor, sat cross-legged (work_meditate, as meditating), then on a
        // dining chair (work_meditate_sit: the palms together and the head bowed, laid over the sit); and visiting a
        // grave (VisitGrave), sat cross-legged beside it facing it (work_meditate).
        private static IEnumerator PrayTest()
        {
            foreach (string name in new[] { "floor", "chair", "grave" })
            {
                ClearPatch();
                if (name != "grave" && !ModsConfig.IdeologyActive)
                {
                    Check(name + ": praying", false, "Ideology not active");
                    continue;
                }
                Pawn pawn = NewPawn(middle + new IntVec3(-2, 0, -2), null);
                if (pawn.needs.joy != null)
                {
                    pawn.needs.joy.CurLevel = 0.1f;
                }
                Job job;
                string want;
                if (name == "grave")
                {
                    Thing grave = ThingMaker.MakeThing(ThingDefOf.Grave);
                    grave.SetFaction(Faction.OfPlayer);
                    Spawn(grave, middle + new IntVec3(1, 0, 0), Rot4.North);
                    job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("VisitGrave"), grave);
                    want = "meditate";
                }
                else
                {
                    if (name == "chair")
                    {
                        Thing c = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                        c.SetFaction(Faction.OfPlayer);
                        Spawn(c, middle, Rot4.South);
                    }
                    job = JobMaker.MakeJob(JobDefOf.MeditatePray, middle);
                    want = name == "chair" ? "meditate_sit" : "meditate";
                }
                job.playerForced = true;
                job.ignoreJoyTimeAssignment = true;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                float most = 0f;
                string seen = null;
                for (int i = 0; i < 80 && most < 0.99f; i++)
                {
                    yield return Ticks(10, "pray_" + name, pawn);
                    seen = Field<string>(pawn, "workKind") ?? seen;
                    if (Field<string>(pawn, "workKind") == want)
                    {
                        most = Mathf.Max(most, Field<float>(pawn, "work"));
                    }
                }
                Look(pawn, 0f);
                yield return WaitReal(2f);
                Check(name + ": " + want, most >= 0.99f, "work weight " + most.ToString("F2") + ", kind " + (seen ?? "none") + ", job "
                    + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none") + ", posture " + pawn.GetPosture());
                Building under = pawn.Position.GetEdifice(map);
                Note(name + ": pawn at " + pawn.Position + " (patch middle " + middle + "), on " + (under != null ? under.def.defName : "nothing")
                    + ", facing " + pawn.Rotation.ToStringHuman());
                for (int i = 0; i < 3; i++)
                {
                    yield return Shot(name + "_" + i);
                    yield return Ticks(60, "pray_" + name, pawn);
                }
            }
            Note("Look: on the floor sat cross-legged, hands on the knees; on the chair the plain sit with the palms pressed together in front of the chest, fingers up, head bowed; by the grave sat cross-legged facing it.");
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
                            // Close up from in front and the side, to see how the meal sits on the hands and against the chest.
                            IntVec3 step = pawn.pather.nextCell - pawn.Position;
                            float heading = step.x != 0 || step.z != 0 ? Mathf.Atan2(step.x, step.z) * Mathf.Rad2Deg : pawn.Rotation.AsAngle;
                            Look(pawn, heading + 180f);
                            lookPitch = 15f;
                            lookDistance = 1.6f;
                            lookHeight = 0.55f;
                            yield return Shot(name + "_carrying_close");
                            lookPitch = LookPitch;
                            lookDistance = LookDistance;
                            lookHeight = LookHeight;
                            Look(pawn, 0f);
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

        // Taking drugs (the Ingest job on a drug by its feet): a smokeleaf joint smoked standing and with a dining chair at
        // a table near (smoke, the joint on the right hand), a pill (wake-up) and a powder (yayo) taken standing (pill, once
        // through over the chew, what it takes on the left palm).
        private static IEnumerator DrugsTest()
        {
            var cases = new[] { ("SmokeleafJoint", false, "smoke"), ("SmokeleafJoint", true, "smoke"), ("WakeUp", false, "pill"), ("Yayo", false, "pill") };
            foreach (var (drugName, seated, want) in cases)
            {
                ThingDef drug = DefDatabase<ThingDef>.GetNamedSilentFail(drugName);
                if (drug == null)
                {
                    Note(drugName + ": not in this game; skipped");
                    continue;
                }
                ClearPatch();
                string name = drugName + (seated ? "_seated" : "");
                IntVec3 chairAt = middle + new IntVec3(1, 0, 0);
                if (seated)
                {
                    Thing c = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog);
                    c.SetFaction(Faction.OfPlayer);
                    Spawn(c, chairAt, Rot4.North);
                    Thing t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("Table1x2c"), ThingDefOf.WoodLog);
                    t.SetFaction(Faction.OfPlayer);
                    Spawn(t, middle + new IntVec3(1, 0, 1), Rot4.North);
                }
                Thing taken = Spawn(ThingMaker.MakeThing(drug), middle + new IntVec3(-1, 0, 0));
                Pawn pawn = NewPawn(middle, null);
                Look(pawn, 0f);
                yield return WaitReal(1f);
                Job job = JobMaker.MakeJob(JobDefOf.Ingest, taken);
                job.count = 1;
                job.playerForced = true;
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                float most = 0f;
                string clip = null;
                int shots = 0;
                bool sat = false;
                for (int i = 0; i < 400 && pawn.CurJobDef == JobDefOf.Ingest; i++)
                {
                    yield return Ticks(5, "drug_" + name, pawn);
                    string c = Field<string>(pawn, "eatClip");
                    float w = Field<float>(pawn, "eat");
                    if (c != null && pawn.CurJobDef == JobDefOf.Ingest)
                    {
                        clip = c;
                        most = Mathf.Max(most, w);
                    }
                    if (seated && pawn.Position == chairAt)
                    {
                        sat = true;
                    }
                    // A pill's chew is only 100 ticks or so: a picture every 15 of them once it is in; a joint's, every 60.
                    if (c == want && w > 0.5f && shots < 8 && i % (want == "pill" ? 3 : 12) == 0)
                    {
                        yield return Shot(name + "_" + shots++);
                    }
                }
                Check(name + ": " + want, clip == want && most >= 0.9f, "clip " + (clip ?? "none") + ", weight " + most.ToString("F2")
                    + ", pill at " + Field<float>(pawn, "pillAt").ToString("F2"));
                if (seated)
                {
                    Note(name + ": " + (sat ? "sat on the chair" : "did not sit (at " + pawn.Position + ")"));
                }
                if (drugName == "SmokeleafJoint" && !seated)
                {
                    // The eyes reddened while high (FaceLayers.WhiteTintOf), close up, stacked and layer by layer; then
                    // with the high taken away, to compare.
                    Hold(pawn);
                    pawn.Rotation = Rot4.South;
                    // Both eyes, whatever the colony's ideoligion did with them (Ideology's blindness takes them out).
                    foreach (BodyPartRecord eye in pawn.RaceProps.body.AllParts.Where(b => b.def == BodyPartDefOf.Eye).ToList())
                    {
                        if (pawn.health.hediffSet.PartIsMissing(eye))
                        {
                            pawn.health.RestorePart(eye);
                        }
                    }
                    HediffDef highDef = DefDatabase<HediffDef>.GetNamedSilentFail("SmokeleafHigh");
                    Hediff high = highDef != null ? pawn.health.hediffSet.GetFirstHediffOfDef(highDef) : null;
                    Check("stoned: high on smokeleaf", high != null, high != null ? "severity " + high.Severity.ToString("F2") : "no SmokeleafHigh");
                    float distance = lookDistance, pitch = lookPitch, height = lookHeight;
                    lookPitch = 15f;
                    lookDistance = 1.4f;
                    lookHeight = 1.0f;
                    Look(pawn, 0f);
                    foreach (bool stoned in new[] { true, false })
                    {
                        if (!stoned && high != null)
                        {
                            pawn.health.RemoveHediff(high);
                        }
                        pawn.Rotation = Rot4.South;
                        yield return Ticks(90, "drug_eyes", pawn);   // (turned round to the camera)
                        FaceLayers.StackLayers = true;
                        yield return Shot("eyes_" + (stoned ? "stoned" : "sober") + "_stacked");
                        FaceLayers.StackLayers = false;
                        yield return Shot("eyes_" + (stoned ? "stoned" : "sober") + "_layers");
                        FaceLayers.StackLayers = true;
                    }
                    lookDistance = distance;
                    lookPitch = pitch;
                    lookHeight = height;
                }
            }
            Note("Look: smoking, the joint across the right hand's fingers, held up by the chest, to the lips for a drag, lowered as the head tips back to blow; the left arm hanging (seated, the sit's). A pill or powder: the drug on the left palm, the right fingers pinching from it up to the mouth, the head tipped back to swallow.");
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

        // Talking mouth: a visitor stood talking to a patient in a bed (the talk clip) and sat by one on a sleeping spot
        // (work_visit), watched for Ease.speaking and for the mouth swapping between Facial Animation's open and normal
        // shapes (FaceLayers.TalkOpenDraws/TalkShutDraws), with close pictures of the face, and a pawn not talking for
        // contrast, whose mouth must never open.
        private static IEnumerator TalkTest()
        {
            foreach (string where in new[] { "bed", "spot" })
            {
                ClearPatch();
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamed(where == "spot" ? "SleepingSpot" : "Bed");
                Thing made = ThingMaker.MakeThing(bedDef, bedDef.MadeFromStuff ? ThingDefOf.WoodLog : null);
                made.SetFaction(Faction.OfPlayer);
                Building_Bed bed = (Building_Bed)Spawn(made, middle + new IntVec3(1, 0, 0), Rot4.North);
                Pawn patient = NewPawn(middle + new IntVec3(1, 0, -1), null);
                Pawn visitor = NewPawn(middle + new IntVec3(-2, 0, 0), null);
                Hold(visitor);
                patient.jobs.Notify_TuckedIntoBed(bed);
                yield return WaitReal(3f);
                float speaking = 0f;
                FaceLayers.TalkOpenDraws = FaceLayers.TalkShutDraws = FaceLayers.TalkNoOpen = 0;
                FaceLayers.TalkTextures.Clear();
                for (int i = 0; i < 24; i++)
                {
                    if (patient.needs.rest != null)
                    {
                        patient.needs.rest.CurLevel = 1f;
                    }
                    if (!patient.InBed())
                    {
                        patient.jobs.Notify_TuckedIntoBed(bed);
                    }
                    if (visitor.CurJobDef != JobDefOf.VisitSickPawn)
                    {
                        Job job = JobMaker.MakeJob(JobDefOf.VisitSickPawn, patient);
                        job.playerForced = true;
                        job.ignoreJoyTimeAssignment = true;
                        visitor.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                    // Framed on the visitor's face, from the front of the way it faces.
                    lookAt = visitor.DrawPos;
                    lookYaw = 180f + visitor.Rotation.AsAngle;
                    lookPitch = 15f;
                    lookDistance = 1.3f;
                    lookHeight = where == "spot" ? 0.7f : 1.0f;
                    yield return Ticks(10, "talk_" + where, visitor);
                    speaking = Mathf.Max(speaking, Field<float>(visitor, "speaking"));
                    if (i % 4 == 3)
                    {
                        yield return Shot(where + "_face" + (i + 1));
                    }
                }
                int open = FaceLayers.TalkOpenDraws, shut = FaceLayers.TalkShutDraws;
                Check(where + ": speaking", speaking > 0.9f, "Ease.speaking reached " + speaking.ToString("F2"));
                Check(where + ": mouth opens and shuts", open > 0 && shut > 0, open + " frames open, " + shut + " shut while talking");
                float share = open + shut > 0 ? open / (float)(open + shut) : 0f;
                Check(where + ": open about half the time", share > 0.25f && share < 0.75f, "open " + share.ToString("P0") + " of " + (open + shut) + " frames");
                Note(where + " mouth layers: " + string.Join("; ", FaceLayers.TalkTextures));
                Check(where + ": the open mouth is a picture of its own", open > 0 && FaceLayers.TalkNoOpen == 0, FaceLayers.TalkNoOpen + " of " + open + " open draws had no open mouth texture different from the normal one");
                // The patient, not talking, keeps its mouth: no frame of it counted.
                if (visitor.jobs != null)
                {
                    visitor.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                yield return Ticks(60, "talk_" + where + "_after", visitor);
                int before = FaceLayers.TalkOpenDraws + FaceLayers.TalkShutDraws;
                yield return Ticks(30, "talk_" + where + "_quiet", visitor);
                Check(where + ": quiet once it stops", FaceLayers.TalkOpenDraws + FaceLayers.TalkShutDraws == before, "talking frames counted after the visit ended: " + (FaceLayers.TalkOpenDraws + FaceLayers.TalkShutDraws - before));
            }
            Note("Look: the _face pictures, the visitor's mouth open in some and closed in others, never stuck either way.");
        }

        // Talking in a social interaction (WalkFigures.SayOf): two pawns stood side by side, a chitchat and then a deep
        // talk between a new pair, the face draws counted per pawn (FaceLayers.TalkDrawsOf) in 5 tick steps, so it shows
        // who talks when: the one who began it for SayTicks (times SayLong for a deep talk), then after ReplyGap the other
        // for ReplyTicks, then neither.
        private static IEnumerator ChatTest()
        {
            foreach (string kind in new[] { "Chitchat", "DeepTalk" })
            {
                float length = kind == "DeepTalk" ? 1.6f : 1f;   // WalkFigures.SayLong
                int said = Mathf.RoundToInt(120 * length), replyFrom = said + 25, replyTo = replyFrom + Mathf.RoundToInt(90 * length);
                ClearPatch();
                Pawn a = NewPawn(middle + new IntVec3(-1, 0, 0), null);
                Pawn b = NewPawn(middle + new IntVec3(1, 0, 0), null);
                Hold(a);
                Hold(b);
                a.Rotation = b.Rotation = Rot4.South;
                lookAt = (a.DrawPos + b.DrawPos) * 0.5f;
                lookYaw = 0f;
                lookPitch = 15f;
                lookDistance = 3f;
                lookHeight = 0.9f;
                yield return WaitReal(3f);
                FaceLayers.TalkDrawsOf.Clear();
                int start = Find.TickManager.TicksGame;
                bool done = a.interactions.TryInteractWith(b, DefDatabase<InteractionDef>.GetNamed(kind));
                Check(kind + ": interaction made", done, done ? kind : "the game refused it");
                if (!done)
                {
                    continue;
                }
                // Draws by each pawn in windows wholly inside each stretch (a step's slack either side of the edges).
                int aSaying = 0, aAfter = 0, bBefore = 0, bReplying = 0, bAfter = 0;
                int lastA = 0, lastB = 0;
                while (Find.TickManager.TicksGame - start < replyTo + 40)
                {
                    int from = Find.TickManager.TicksGame - start;
                    yield return Ticks(5, "chat_" + kind, a);
                    int to = Find.TickManager.TicksGame - start;
                    int nowA, nowB;
                    FaceLayers.TalkDrawsOf.TryGetValue(a.thingIDNumber, out nowA);
                    FaceLayers.TalkDrawsOf.TryGetValue(b.thingIDNumber, out nowB);
                    int da = nowA - lastA, db = nowB - lastB;
                    lastA = nowA;
                    lastB = nowB;
                    if (to <= said - 5) { aSaying += da; }
                    if (from >= said + 5) { aAfter += da; }
                    if (to <= replyFrom - 5) { bBefore += db; }
                    if (from >= replyFrom + 5 && to <= replyTo - 5) { bReplying += db; }
                    if (from >= replyTo + 5) { bAfter += db; }
                    if (from < 30 && to >= 30) { yield return Shot(kind + "_says"); }
                    if (from < replyFrom + 30 && to >= replyFrom + 30) { yield return Shot(kind + "_replies"); }
                }
                Check(kind + ": the one who began it talks", aSaying > 0, aSaying + " talking draws in its first " + said + " ticks");
                Check(kind + ": then stops", aAfter == 0, aAfter + " talking draws after tick " + said);
                Check(kind + ": the other waits", bBefore == 0, bBefore + " talking draws before tick " + replyFrom);
                Check(kind + ": then answers", bReplying > 0, bReplying + " talking draws between ticks " + replyFrom + " and " + replyTo);
                Check(kind + ": then stops", bAfter == 0, bAfter + " talking draws after tick " + replyTo);
            }
            lookPitch = LookPitch;
            lookDistance = LookDistance;
            lookHeight = LookHeight;
            Note("Look: in _says the left pawn's mouth moves (open or shut, it swaps on syllables), in _replies the right one's.");
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
        // bows_slab: all of them drawn as their sprites' slabs (IsoRim's "Weapons as 3D models" off for the run, not saved),
        // checking the left fist closes on the stave about its middle (WalkFigures.AutoSlabGrip) held and drawn.
        private static IEnumerator BowTest(string only)
        {
            bool slab = only == "slab";
            if (!slab)
            {
                return BowRun(BowList(only), false, only == "parka");
            }
            return WithoutWeaponModels(BowRun(BowList(null), true));
        }

        private static List<string> BowList(string only)
        {
            List<string> bows = new List<string>();
            foreach (string bow in new[] { "Bow_Short", "Bow_Recurve", "Bow_Great", "Flamebow" })
            {
                if (only == null || only == "parka" || only == "slab" || only == bow)
                {
                    bows.Add(bow);
                }
            }
            return bows;
        }

        // weapons_<name>+<name>...: each a weapon's defName, or a mod's packageId for every weapon that mod adds (a mod's
        // weapons, sorted by WalkFigures.KindOf and TypeOf as the export doesn't name them). Run as the bows are: held
        // drafted, then a ranged one shot at the wall (a thrown one thrown), a melee one only held.
        private static List<string> WeaponsNamed(string[] names)
        {
            List<string> found = new List<string>();
            foreach (string name in names)
            {
                bool mod = false;
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (def.IsWeapon && !def.destroyOnDrop && def.equipmentType == EquipmentType.Primary && def.modContentPack != null
                        && string.Equals(def.modContentPack.PackageId, name, StringComparison.OrdinalIgnoreCase) && !found.Contains(def.defName))
                    {
                        found.Add(def.defName);
                        mod = true;
                    }
                }
                if (!mod)
                {
                    found.Add(name);
                }
            }
            return found;
        }

        // Runs a scenario with IsoRim's weapon models off (its setting, put back after; never written to disk).
        private static IEnumerator WithoutWeaponModels(IEnumerator inner)
        {
            IsoRim.IsoRimSettings st = IsoRim.IsoRimMod.Settings;
            bool was = st.weaponModels3D;
            st.weaponModels3D = false;
            Note("IsoRim's weapon models off for this run (was " + (was ? "on" : "off") + ").");
            try
            {
                while (inner.MoveNext())
                {
                    yield return inner.Current;
                }
            }
            finally
            {
                st.weaponModels3D = was;
            }
        }

        // The slab's grip, checked: the fist on the art (within SlabGripPixels of a solid pixel of a 128 px read) and within
        // SlabGripAlong of the art's middle along its length.
        private const float SlabGripPixels = 3f;
        private const float SlabGripAlong = 0.25f;

        private static void CheckSlabGrip(Pawn pawn, string what)
        {
            float nearest, along, across;
            string text;
            bool read = WalkFigures.AutoSlabGrip(pawn, out nearest, out along, out across, out text);
            Check(what + ": fist on the art", read && nearest <= SlabGripPixels, text);
            Check(what + ": fist near the middle", read && Mathf.Abs(along) <= SlabGripAlong, text);
        }

        private static IEnumerator BowRun(List<string> weapons, bool slab, bool parka = false)
        {
            Pawn pawn = null;
            Thing wall = null;
            bool parkaOn = false;
            bool bows = weapons.Count > 0 && weapons[0].StartsWith("Bow_");
            if (weapons.Count == 0)
            {
                Check("weapons found", false, "no weapon by those names or mods");
            }
            foreach (string bow in weapons)
            {
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
                if (wall != null && (wall.Destroyed || !wall.Spawned))
                {
                    wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);   // (shot or blown down by the last one)
                    wall.SetFaction(Faction.OfPlayer);
                    wall = Spawn(wall, middle + new IntVec3(0, 0, -7));
                }
                else if (wall != null)
                {
                    wall.HitPoints = wall.MaxHitPoints;
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
                // (holstered, close up: from the side and the front)
                Look(pawn, 0f);
                lookDistance = 2.2f;
                lookHeight = 0.75f;
                lookYaw = 90f;
                yield return Shot(bow + "_holstered_close");
                lookDistance = LookDistance;
                lookHeight = LookHeight;
                lookYaw = 0f;
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
                WalkFigures.AutoSlabFist.Remove(pawn.thingIDNumber);
                yield return Ticks(2, bow + "_hold", pawn);
                yield return Shot(bow + "_hold");
                Note(bow + ": " + WalkFigures.AutoWeapon(pawn));
                if (slab)
                {
                    Check(bow + ": drawn as a slab", WalkFigures.AutoWeapon(pawn).StartsWith("slab True"), WalkFigures.AutoWeapon(pawn));
                    CheckSlabGrip(pawn, bow + " held");
                }
                lookDistance = 2.2f;
                lookHeight = 0.75f;
                lookYaw = 90f;
                yield return Shot(bow + "_hold_close");
                lookDistance = LookDistance;
                lookHeight = LookHeight;
                lookYaw = 0f;
                Check(bow + ": drafted stance", drafted >= 0.99f && stance != null && stance.StartsWith("drafted_"),
                    "drafted " + drafted.ToString("F2") + ", stance " + (stance ?? "none") + ", held " + (pawn.equipment.Primary != null ? pawn.equipment.Primary.def.defName : "nothing"));
                ThingDef held = ThingDef.Named(bow);
                if (held.IsMeleeWeapon)
                {
                    pawn.drafter.Drafted = false;
                    continue;   // (only held: a blow at a wall wants a target to walk to)
                }
                bool thrown = WalkFigures.AutoThrown(held);
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
                        WalkFigures.AutoSlabFist.Remove(pawn.thingIDNumber);
                        yield return Shot(bow + "_drawn");
                        Note(bow + " aiming: " + WalkFigures.AutoWeapon(pawn));
                        if (slab)
                        {
                            CheckSlabGrip(pawn, bow + " drawn");
                        }
                        lookDistance = 2.2f;
                        lookHeight = 0.75f;
                        lookYaw = 90f;
                        yield return Shot(bow + "_drawn_close");
                        lookDistance = LookDistance;
                        lookHeight = LookHeight;
                        lookYaw = 0f;
                    }
                    if (shots >= 3 || thrown && shots >= 1)
                    {
                        break;
                    }
                }
                Check(bow + ": aimed", thrown || aim >= 0.99f && aimName != null, "aim " + aim.ToString("F2") + ", aim clips " + (aimName ?? "none"));
                Check(bow + ": shooting", shots >= (thrown ? 1 : 2), shots + " shots, job " + (pawn.CurJobDef != null ? pawn.CurJobDef.defName : "none")
                    + ", stance " + pawn.stances.curStance?.GetType().Name + (ammo != null ? ", ammo " + ammo : ""));
                pawn.drafter.Drafted = false;
                pawn.jobs.StopAll();
            }
            if (!bows)
            {
                Note("Look: each weapon held in its kind's drafted stance, the model (or slab) in the fist the right way round and about the size of the kind's own; aimed along the barrel at the wall.");
                yield break;
            }
            Note("Look: drafted the bow held low in front in the left hand; shooting side on, the string drawn back to the face (drawn), the arrow loosed (loosed); the bow model in the left fist throughout.");
        }

        // Every gun loaded (not thrown), with how long its art is drawn and the kind it is sorted as, to set
        // WalkFigures.PistolLongest between the sidearms and the long guns. No pawn; notes only, and one check that
        // the game's own pistols and long guns fall either side of it.
        private static IEnumerator WeaponKindsTest()
        {
            yield return WaitReal(1f);
            List<string> lines = new List<string>();
            List<string> wrong = new List<string>();
            float longestPistol = 0f, shortestLong = float.MaxValue;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!def.IsRangedWeapon || def.equipmentType != EquipmentType.Primary || WalkFigures.AutoThrown(def) || def.graphicData == null
                    || def.destroyOnDrop)   // (turret and mechanoid guns: no pawn holds them)
                {
                    continue;
                }
                string mod = def.modContentPack != null ? def.modContentPack.PackageId : "?";
                float length;
                string kind, named;
                WalkFigures.AutoKind(def, out length, out kind, out named);
                string classes = def.weaponClasses != null ? string.Join(",", def.weaponClasses.ConvertAll(c => c.defName)) : "";
                lines.Add(length.ToString("F2") + " " + kind + (named != null ? " (" + named + ")" : "") + " " + def.defName + " [" + mod + "] "
                    + def.GetStatValueAbstract(StatDefOf.Mass).ToString("F1") + " kg, " + classes
                    + ", tags " + (def.weaponTags != null ? string.Join(",", def.weaponTags) : ""));
                if (def.modContentPack != null && def.modContentPack.IsOfficialMod && named != "bow")
                {
                    string want = named ?? "rifle";
                    if (kind != want)
                    {
                        wrong.Add(def.defName + " " + kind + " (" + length.ToString("F2") + ")");
                    }
                    if (want == "pistol")
                    {
                        longestPistol = Mathf.Max(longestPistol, length);
                    }
                    else
                    {
                        shortestLong = Mathf.Min(shortestLong, length);
                    }
                }
            }
            lines.Sort(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                Note(line);
            }
            Check("the game's guns sorted as their names say", wrong.Count == 0,
                (wrong.Count > 0 ? "wrong: " + string.Join(", ", wrong) + "; " : "") + "longest sidearm " + longestPistol.ToString("F2")
                + ", shortest long gun " + shortestLong.ToString("F2") + ", PistolLongest " + WalkFigures.AutoPistolLongest.ToString("F2"));
            ThingDef stunner = DefDatabase<ThingDef>.GetNamedSilentFail("DV_Gun_SonicStunner");
            if (stunner != null)
            {
                float length;
                string kind, named;
                WalkFigures.AutoKind(stunner, out length, out kind, out named);
                Check("Spacer Arsenal's sonic stunner held as a pistol", kind == "pistol", kind + ", art " + length.ToString("F2"));
            }
        }

        // Every apparel loaded, with the garment drawn for it: its own, else its stand-ins (StandIns.cs), else none. No pawn;
        // notes only, and checks that belts and shields get none and that the game's own body and head wear is all drawn.
        private static IEnumerator StandInsTest()
        {
            yield return WaitReal(1f);
            List<string> lines = new List<string>();
            List<string> beltsDrawn = new List<string>();
            List<string> undrawn = new List<string>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.apparel == null || def.apparel.layers == null || def.apparel.bodyPartGroups == null)
                {
                    continue;
                }
                string mod = def.modContentPack != null ? def.modContentPack.PackageId : "?";
                string drawn = WalkFigures.AutoStandIns(def);
                lines.Add(def.defName + " [" + mod + "] " + string.Join(",", def.apparel.layers.ConvertAll(l => l.defName)) + " / "
                    + string.Join(",", def.apparel.bodyPartGroups.ConvertAll(g => g.defName)) + " -> " + drawn);
                bool belt = def.apparel.layers.Exists(l => l.defName == "Belt" || l.defName.Contains("Shield")) || def.HasComp(typeof(CompShield));
                if (belt && drawn.StartsWith("stand-in"))
                {
                    beltsDrawn.Add(def.defName + " " + drawn);
                }
                if (!belt && drawn == "none" && def.modContentPack != null && def.modContentPack.IsOfficialMod
                    && def.apparel.bodyPartGroups.Exists(g => g.defName == "Torso" || g.defName == "Legs" || g.defName == "UpperHead" || g.defName == "FullHead"))
                {
                    undrawn.Add(def.defName);
                }
            }
            lines.Sort(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                Note(line);
            }
            Check("no belt or shield drawn as clothes", beltsDrawn.Count == 0, string.Join(", ", beltsDrawn));
            Check("the game's own body and head wear all drawn", undrawn.Count == 0, string.Join(", ", undrawn));
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
                    // As though both had last worked stood at a bench: Ease.workKind is kept after the work ends, and a
                    // stale one once took the drawing's place chain ahead of the kiss, which then left them a cell apart.
                    WalkFigures.AutoSetField(a, "workKind", "bench");
                    WalkFigures.AutoSetField(b, "workKind", "bench");
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
            float kissGap = float.MaxValue;
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
                if (date == "kiss" && Field<float>(a, "eat") > 0.95f && Field<float>(b, "eat") > 0.95f)
                {
                    Vector3 apart = Field<Vector3>(a, "drawnAt") - Field<Vector3>(b, "drawnAt");
                    kissGap = Mathf.Min(kissGap, new Vector2(apart.x, apart.z).magnitude);
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
                    // Drawn KissGap (0.42 for grown pawns) apart, not the cell the mod stands them apart.
                    Check("kiss: drawn together", kissGap < 0.6f, "nearest drawn apart " + (kissGap < float.MaxValue ? kissGap.ToString("F2") + " cells" : "never both kissing"));
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
            if (poseOff)
            {
                return null;   // (fpeye: IsoRim's first person has the view)
            }
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
            // (a scenario named by a list, weapons_<a>+<b>+..., keeps only its first part, as a file name has 255 bytes)
            string scenario = current != null && current.Length > 40 ? current.Substring(0, 40).Split('+')[0] : current;
            return scenario + "_" + (shotCount++).ToString("D2") + "_";
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
        // The perf test (AutoTest.Perf): which figures are drawn (0 ours, 1 IsoRim's, 2 none), and the time spent in DrawFigure
        // (Stopwatch ticks) and its calls, summed until the test takes them.
        internal static int PerfMode;
        internal static HashSet<int> PerfAllowed;   // the pawns drawn as the chibi while PerfMode is 0 (by id), null for all
        internal static long PerfTicks;
        internal static int PerfDraws;

        // The figure's state as AutoTest probes it: one line of the Ease fields the scenarios look at.
        // Where the holding fist was in the last slab drawn for each pawn: its place in the sprite (u, v from 0 to 1, v up, as
        // SpriteBarrelEnd reads it) and its depth off the picture's plane (the slab's own z, model cells), by pawn id. Set by
        // DrawWeapon whenever the weapon is drawn as its sprite's slab and fully in the hand.
        internal static readonly Dictionary<int, SlabFist> AutoSlabFist = new Dictionary<int, SlabFist>();

        internal class SlabFist
        {
            public Vector3 uv;          // u, v and the depth off the picture (the slab's z)
            public Matrix4x4 world;     // the slab's picture space to true cells
            public Matrix4x4 figure;    // the figure's model space to true cells
            public Matrix4x4 turn;      // the holding hand's bone (model space), zero if the hold isn't on the hand
            public Vector3 at;          // the hold's at (game model space, the hand's frame)
        }

        private static void NoteSlabFist(Pawn pawn, Matrix4x4 world, Matrix4x4 figure, Vector3 fist, Matrix4x4 turn, Vector3 at)
        {
            Vector3 l = world.inverse.MultiplyPoint3x4(figure.MultiplyPoint3x4(fist));
            AutoSlabFist[pawn.thingIDNumber] = new SlabFist { uv = new Vector3(l.x + 0.5f, l.y + 0.5f, l.z), world = world, figure = figure, turn = turn, at = at };
        }

        // How the fist sits on the slab's sprite: on its art (the alpha there, else how far the nearest solid pixel is, in
        // pixels of a 128 px read), and along the art's long axis from its middle (a share of its half length, so 0 is the
        // middle and 1 an end: a bow's grip is about its middle) and across it (a share of its half width: a bow's stave is
        // to one side and its string the other). False if nothing was recorded.
        internal static bool AutoSlabGrip(Pawn pawn, out float nearestPx, out float along, out float across, out string text)
        {
            nearestPx = float.MaxValue;
            along = across = 0f;
            text = "no slab drawn";
            ThingWithComps w = pawn.equipment != null ? pawn.equipment.Primary : null;
            SlabFist rec;
            if (w == null || !AutoSlabFist.TryGetValue(pawn.thingIDNumber, out rec))
            {
                return false;
            }
            Vector3 f = rec.uv;
            Graphic g = w.Graphic;
            Material mat = g != null ? g.MatSingleFor(w) : null;
            const int n = 128;
            Color32[] px = mat != null && mat.mainTexture != null ? IsoRim.Api.ReadSpritePixels(mat.mainTexture, n) : null;
            if (px == null || px.Length < n * n)
            {
                text = "fist at uv (" + f.x.ToString("F3") + ", " + f.y.ToString("F3") + "), sprite unreadable";
                return false;
            }
            // The solid pixels' middle and long axis, in cells as drawn (the sprite's drawSize), so a long thin sprite's axis
            // is its length whatever its picture's shape.
            Vector2 cells = g.drawSize;
            float sx = 0f, sy = 0f, count = 0f;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (px[j * n + i].a >= 128)
                    {
                        sx += (i + 0.5f) / n * cells.x;
                        sy += (j + 0.5f) / n * cells.y;
                        count++;
                    }
                }
            }
            if (count < 1f)
            {
                text = "sprite has no solid pixels";
                return false;
            }
            Vector2 mid = new Vector2(sx / count, sy / count);
            float cxx = 0f, cyy = 0f, cxy = 0f;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (px[j * n + i].a >= 128)
                    {
                        float dx = (i + 0.5f) / n * cells.x - mid.x, dy = (j + 0.5f) / n * cells.y - mid.y;
                        cxx += dx * dx;
                        cyy += dy * dy;
                        cxy += dx * dy;
                    }
                }
            }
            float angle = 0.5f * Mathf.Atan2(2f * cxy, cxx - cyy);
            Vector2 axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), side = new Vector2(-axis.y, axis.x);
            float lo = float.MaxValue, hi = float.MinValue, sLo = float.MaxValue, sHi = float.MinValue;
            int fi = Mathf.FloorToInt(f.x * n), fj = Mathf.FloorToInt(f.y * n);
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (px[j * n + i].a < 128)
                    {
                        continue;
                    }
                    Vector2 d = new Vector2((i + 0.5f) / n * cells.x, (j + 0.5f) / n * cells.y) - mid;
                    float t = Vector2.Dot(d, axis), s = Vector2.Dot(d, side);
                    lo = Mathf.Min(lo, t);
                    hi = Mathf.Max(hi, t);
                    sLo = Mathf.Min(sLo, s);
                    sHi = Mathf.Max(sHi, s);
                    nearestPx = Mathf.Min(nearestPx, Mathf.Sqrt((i - fi) * (i - fi) + (j - fj) * (j - fj)));
                }
            }
            Vector2 fc = new Vector2(f.x * cells.x, f.y * cells.y) - mid;
            float ft = Vector2.Dot(fc, axis), fs = Vector2.Dot(fc, side);
            along = (ft - 0.5f * (lo + hi)) / Mathf.Max(1e-4f, 0.5f * (hi - lo));
            across = (fs - 0.5f * (sLo + sHi)) / Mathf.Max(1e-4f, 0.5f * (sHi - sLo));
            byte alpha = fi >= 0 && fi < n && fj >= 0 && fj < n ? px[fj * n + fi].a : (byte)0;
            // The stave at the fist: the runs of solid pixels across the art at the fist's place along it, the widest taken
            // as the stave (a string is a pixel or two), and the shift that puts its middle, on the picture's plane, at the
            // fist, as a change to the hold's at in the hand's frame (walk.py's at is (-x, -z, y) of the game's).
            string stave = "";
            if (rec.turn != Matrix4x4.zero)
            {
                float step = Mathf.Min(cells.x, cells.y) / n * 0.5f;
                float bestFrom = 0f, bestTo = -1f, runFrom = 0f;
                bool inRun = false;
                List<string> runs = new List<string>();
                for (float s = sLo - step; s <= sHi + 2f * step; s += step)
                {
                    Vector2 c = mid + axis * ft + side * s;
                    int i = Mathf.FloorToInt(c.x / cells.x * n), j = Mathf.FloorToInt(c.y / cells.y * n);
                    bool solid = i >= 0 && i < n && j >= 0 && j < n && px[j * n + i].a >= 128;
                    if (solid && !inRun)
                    {
                        runFrom = s;
                        inRun = true;
                    }
                    else if (!solid && inRun)
                    {
                        inRun = false;
                        runs.Add(runFrom.ToString("F3") + ".." + s.ToString("F3"));
                        if (s - runFrom > bestTo - bestFrom)
                        {
                            bestFrom = runFrom;
                            bestTo = s;
                        }
                    }
                }
                if (bestTo > bestFrom)
                {
                    float target = 0.5f * (bestFrom + bestTo);
                    Vector2 tc = mid + axis * ft + side * target;
                    Vector3 local = new Vector3(tc.x / cells.x - 0.5f, tc.y / cells.y - 0.5f, 0f);
                    Vector3 fl = new Vector3(f.x - 0.5f, f.y - 0.5f, f.z);
                    Vector3 model = rec.figure.inverse.MultiplyVector(rec.world.MultiplyVector(fl - local));
                    Vector3 dAt = rec.turn.inverse.MultiplyVector(model);
                    Vector3 na = rec.at + dAt;
                    stave = "; runs across at the fist (cells from the art's middle) " + string.Join(", ", runs) + ", fist at " + fs.ToString("F3")
                        + ", stave " + bestFrom.ToString("F3") + ".." + bestTo.ToString("F3") + "; off the stave's middle by "
                        + (model.magnitude).ToString("F3") + " model cells; at would be game (" + na.x.ToString("F4") + ", " + na.y.ToString("F4") + ", " + na.z.ToString("F4")
                        + "), walk.py at=(" + (-na.x).ToString("F4") + ", " + (-na.z).ToString("F4") + ", " + na.y.ToString("F4") + ")";
                }
            }
            text = "fist at uv (" + f.x.ToString("F3") + ", " + f.y.ToString("F3") + "), " + f.z.ToString("F3") + " off the picture; alpha there "
                + alpha + ", nearest solid pixel " + nearestPx.ToString("F1") + " px of " + n + "; along the art " + along.ToString("F2")
                + " of its half length from the middle, across " + across.ToString("F2") + " of its half width (axis "
                + (angle * Mathf.Rad2Deg).ToString("F0") + " deg in the picture, drawSize " + cells.x.ToString("F2") + "x" + cells.y.ToString("F2") + ")" + stave;
            return true;
        }

        internal const float AutoPistolLongest = PistolLongest;

        // A gun's art length, its kind and, if the name or a tag already made it one-handed or a bow, that ("pistol",
        // "bow"); null when only the art length decides.
        internal static void AutoKind(ThingDef def, out float length, out string kind, out string named)
        {
            length = ArtLength(def);
            kind = KindOf(def);
            string name = (def.defName + " " + def.label).ToLowerInvariant();
            bool tagged = OneHandedByTag(def);
            for (int i = 0; def.weaponTags != null && i < def.weaponTags.Count; i++)
            {
                tagged |= def.weaponTags[i].Contains("Pistol") || def.weaponTags[i].Contains("Revolver");
            }
            named = kind == "bow" ? "bow" : name.Contains("pistol") || name.Contains("revolver") || tagged ? "pistol" : null;
        }

        internal static bool AutoThrown(ThingDef def)
        {
            return Thrown(def);
        }

        internal static string AutoWeapon(Pawn pawn)
        {
            ThingWithComps w = pawn.equipment != null ? pawn.equipment.Primary : null;
            if (w == null)
            {
                return "no weapon";
            }
            SkinnedFigure.Hold hold = HoldOf(WeaponName(w.def), AsSlab(w));
            return "slab " + AsSlab(w) + ", name " + WeaponName(w.def) + ", kind " + KindOf(w.def) + ", type " + TypeOf(w.def) + ", drafted clip " + DraftedClip(pawn)
                + ", aim clips " + AimName(pawn) + ", own hold " + (hold != null) + ", fitted to " + FitArtOf(w) + ", " + AutoGripOf(pawn, w);
        }

        // The last grip slide DrawWeapon gave this pawn's weapon (GripSlide, in cells of the art's own axes, before the
        // figure's size), and the grips found in its art and the kind's.
        internal static readonly Dictionary<int, Vector2> AutoGripSlide = new Dictionary<int, Vector2>();

        private static string AutoGripOf(Pawn pawn, ThingWithComps w)
        {
            Vector2 slide;
            string text = AutoGripSlide.TryGetValue(pawn.thingIDNumber, out slide) && slide != Vector2.zero
                ? "grip slide (" + slide.x.ToString("F3") + ", " + slide.y.ToString("F3") + ")" : "no grip slide";
            Material mat = w.Graphic != null ? w.Graphic.MatSingleFor(w) : null;
            string kind = KindOf(w.def);
            string find = kind == "rifle" ? "gun" : kind == "melee" ? (TypeOf(w.def) == "melee_2h" ? "melee2" : "melee") : null;
            Vector2 grip;
            if (find != null && mat != null)
            {
                text += GripFinder.Find(mat.mainTexture, find, w.def.equippedAngleOffset, out grip)
                    ? ", grip found at (" + grip.x.ToString("F2") + ", " + grip.y.ToString("F2") + ")" : ", no clear grip";
            }
            SlabFist rec;
            if (AutoSlabFist.TryGetValue(pawn.thingIDNumber, out rec))
            {
                text += ", fist on the sprite at (" + rec.uv.x.ToString("F3") + ", " + rec.uv.y.ToString("F3") + ")";
            }
            return text;
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
                + PetProbe(pawn) + BowProbe(pawn, e);
        }

        // A bow's draw as LayAim plays it: the game's stance (a warm-up's ticks done of its whole), DrawOf, KickOf and the
        // last shot's tick.
        private static string BowProbe(Pawn pawn, Ease e)
        {
            if (e.aimName == null || !skin.Has("nock_" + e.aimName))
            {
                return "";
            }
            Stance st = pawn.stances != null ? pawn.stances.curStance : null;
            Stance_Warmup warm = st as Stance_Warmup;
            Stance_Busy busy = st as Stance_Busy;
            Verb verb = busy != null && busy.verb != null ? busy.verb : pawn.equipment?.PrimaryEq?.PrimaryVerb;
            return " gameStance=" + (st != null ? st.GetType().Name : "-")
                + (warm != null ? " warm=" + (Find.TickManager.TicksGame - warm.startedTick) + "/" + (Find.TickManager.TicksGame - warm.startedTick + warm.ticksLeft) : "")
                + " bowDrawn=" + DrawOf(pawn, e).ToString("F2") + " kick=" + KickOf(pawn, true).ToString("F2")
                + " lastShot=" + (verb != null ? verb.LastShotTick : -1) + " verb=" + (verb != null ? verb.GetType().Name : "-");
        }

        // A carried animal's drawn back (IsoRim's Api.ReadHost), cells, for fitting the cuddle.
        private static string PetProbe(Pawn pawn)
        {
            Thing load = pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            return load is Pawn animal ? " petBack=" + PetBackOf(animal).ToString("F3") + " size=" + SizeOf(pawn).ToString("F2") : "";
        }

        // The garment drawn for this apparel: "own <name>", "stand-in <name>+<name>" or "none".
        internal static string AutoStandIns(ThingDef def)
        {
            if (!Ready() || skin == null)
            {
                return "no model";
            }
            int own = skin.GarmentOf(def);
            if (own > 0)
            {
                return "own " + skin.PieceName(own);
            }
            int[] all = skin.StandInsFor(def);
            if (all.Length == 0)
            {
                return "none";
            }
            List<string> names = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                names.Add(skin.PieceName(all[i]));
            }
            return "stand-in " + string.Join("+", names);
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
        internal static bool AutoWeaponShows(Pawn pawn)
        {
            return WeaponShows(pawn);
        }

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

        // Sets one of the pawn's Ease fields (making its Ease if it has none yet), to stage what it was doing before.
        internal static void AutoSetField(Pawn pawn, string name, object value)
        {
            Ease e;
            if (!eases.TryGetValue(pawn.thingIDNumber, out e))
            {
                e = new Ease();
                eases[pawn.thingIDNumber] = e;
            }
            FieldInfo field = typeof(Ease).GetField(name);
            if (field != null)
            {
                field.SetValue(e, value);
            }
        }
    }
}
#endif
