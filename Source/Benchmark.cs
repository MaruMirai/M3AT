using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using RimWorld;
using Verse;

namespace M3AT
{
    // Which person figures are drawn, read by DrawFigure: for the player's benchmark (BenchmarkWindow) and AutoTest's perf.
    // In the normal build too, as it costs an integer compare a figure.
    public static partial class WalkFigures
    {
        internal static int PerfMode;               // 0 the chibi, 1 IsoRim's own figures (as without this mod), 2 no person figures at all
        internal static HashSet<int> PerfAllowed;   // the pawns drawn as the chibi while PerfMode is 0 (by id), null for all
        internal static long PerfTicks;             // Stopwatch ticks spent in DrawFigure drawing chibis, summed until taken
        internal static int PerfDraws;              // chibis drawn, the same way
        internal static int PerfAsked;              // person figures IsoRim asked the drawer for, whoever then drew them

        // Started at all (IsoRim found and new enough); whether the chibi loads is only known at the first draw.
        internal static bool Started
        {
            get { return content != null && (!tried || ready); }
        }

        // Hairstyles and beards still being built from their sprites, which the first chibi drawn in a session waits on.
        internal static int HairBuilding
        {
            get { return skin == null ? 0 : skin.HairBuilding; }
        }
    }

    public sealed partial class SkinnedFigure
    {
        internal int HairBuilding
        {
            get { return (builder != null ? builder.BusyCount : 0) + (beardBuilder != null ? beardBuilder.BusyCount : 0); }
        }
    }

    // A frame rate test for players, from the mod options: the game as it is, with M3AT's chibis and with IsoRim's own
    // figures (and if asked, with no person figures drawn, or the chibi with fewerPoses the other way), in short turns: a
    // round gives each setting one, every other round in the reverse order, so what the pawns are doing and how many are
    // in view is near enough the same for each, and a steady drift through a round cancels over two. Each comparison is
    // the trimmed mean of its rounds' differences (Paired), its noise from how much those differ, which is far smaller
    // than the spread of the frame times themselves; trimmed, a one-off stall of seconds (a garbage collection, an
    // autosave) in one turn doesn't decide it. AutoTest's perf --switch measured a switch (Nov 2026, 15 colonists): the
    // first 2 frames after it still cost what the last setting did, the graphics card being behind, and from then on the
    // difference held steady, nothing being rebuilt; so SkipFrames is twice that. Frame times there also swung by up to
    // 8 ms in a pattern repeating every 60 frames, the same for both settings, so a turn is a whole TurnFrames, the skip
    // included, and every turn takes the same share of it.
    // Nothing in the save is changed: only which figures are drawn, and fewerPoses for its own turn, both put back after.
    // The camera is held still by the window (preventCameraMotion); the game runs on at the player's speed.
    public class BenchmarkWindow : Window
    {
        private const string Tag = "[M3AT] ";
        private const int SkipFrames = 4;           // after each switch, not measured (AutoTest's perf --switch measures it)
        private const int TurnFrames = 60;          // each turn's frames, SkipFrames included
        private const float Trim = 0.1f;            // of the rounds' differences, left out at each end (Paired)
        private const int MostRounds = 2000;
        private const float WarmSeconds = 4f;       // each one's first turn, not measured
        private const float HairWaitMost = 90f;     // the longest the first turn waits on hairstyles being built
        private const int FewPeople = 3;            // fewer drawn a frame than this, and the test says so
        private const float SameShare = 0.03f;      // the chibi and no people within this of each other: likely a frame cap

        private static readonly float[] Measured = { 30f, 60f, 150f };   // seconds measured in all, by length, shared by the settings
        private static readonly string[] LengthKeys = { "M3AT_BenchQuick", "M3AT_BenchNormal", "M3AT_BenchThorough" };

        public static string FilePath => Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "benchmark.txt");

        private sealed class Condition
        {
            public string key, name;   // the window's label (a translation key) and the report's
            public string label;       // the window's label as it is, in place of key's (an add-on's name in it)
            public int mode;
            public bool flipFewer;     // fewerPoses the other way from the player's
            public string[] off;       // add-ons turned off for its turns (AddOns.cs), by packageId; null none
            public AddOns.AddOn without;   // the one add-on it is without, if just one

            public string Label => label ?? key.Translate();
            public readonly List<float> frames = new List<float>();     // ms, every measured frame
            public readonly Dictionary<int, float> turns = new Dictionary<int, float>(); // ms, each round's turn's mean, by round
            public double drawerMs;
            public long draws, asked;

            public float Mean => turns.Count > 0 ? Trimmed(turns.Values.ToList()) : BenchmarkWindow.Mean(frames);   // ms, its turns' trimmed mean
            public float Fps => Mean > 0f ? 1000f / Mean : 0f;
            public float PerFrame(long n) => frames.Count == 0 ? 0f : (float)n / frames.Count;

            // The standard error of the mean frame time, from the spread of the turns' means.
            public float Error => StandardError(turns.Values.ToList());

            // The "1% low": the frame rate over the slowest 1% of frames.
            public float LowFps
            {
                get
                {
                    if (frames.Count == 0)
                    {
                        return 0f;
                    }
                    List<float> sorted = new List<float>(frames);
                    sorted.Sort();
                    int n = Math.Max(1, sorted.Count / 100);
                    float sum = 0f;
                    for (int i = sorted.Count - n; i < sorted.Count; i++)
                    {
                        sum += sorted[i];
                    }
                    return sum > 0f ? 1000f * n / sum : 0f;
                }
            }
        }

        private sealed class Step
        {
            public Condition c;
            public float seconds;
            public bool record, waitHair;
            public int round;   // from 1; 0 for the warm-up
        }

        private enum Stage { Setup, Running, Done }

        private Stage stage = Stage.Setup;
        private int length = 1;
        private bool tryFewer = false;   // also the chibi with fewerPoses the other way from the player's
        private bool tryNone = false;    // also no person figures at all, to show what drawing people costs
        private bool tryAddOns = true;   // also without the add-ons (AddOns.cs), to tell their cost from M3AT's own
        private List<AddOns.AddOn> addOns;   // found as the window opened (or Again)
        private List<Condition> conditions;
        private List<Step> steps;
        private int at, framesIn, rounds;
        private float elapsed, left, measured;   // measured: seconds of measured frames so far, all settings
        private readonly List<float> stepFrames = new List<float>();
        private bool fewerWas, speedChanged;
        private Map map;
        private TimeSpeed speedAtStart;
        private string stopped;
        private List<string> summary;
        private string report;

        public override Vector2 InitialSize => new Vector2(480f, 560f);

        public BenchmarkWindow()
        {
            draggable = true;
            doCloseX = true;
            forcePause = false;
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = true;
            onlyOneOfTypeAllowed = true;
        }

#if AUTOTEST
        // AutoTest's addoncost: a run started at once, with the add-ons measured.
        internal static BenchmarkWindow StartForTest(int length)
        {
            BenchmarkWindow w = new BenchmarkWindow { length = length, tryAddOns = true };
            Find.WindowStack.Add(w);
            w.addOns = AddOns.Find();
            w.Start();
            return w;
        }

        internal bool TestDone => stage == Stage.Done;
        internal string TestReport => report ?? stopped;
        internal List<string> TestSummary => summary;
#endif

        public static void Open()
        {
            if (Find.WindowStack.WindowOfType<BenchmarkWindow>() == null)
            {
                Find.WindowStack.Add(new BenchmarkWindow());
            }
        }

        // Only in a game on a map with IsoRim's 3D view on, as there are no 3D figures to compare otherwise.
        public static bool CanRun
        {
            get { return Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null && IsoRim.Api.View3DActive && WalkFigures.Started; }
        }

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(UI.screenWidth - InitialSize.x - 20f, 80f, InitialSize.x, InitialSize.y);
        }

        private bool FewerOnNow => SettingsMod.Settings != null && SettingsMod.Settings.fewerPoses;

        // ---- running

        private void Start()
        {
            fewerWas = FewerOnNow;
            conditions = new List<Condition>
            {
                new Condition { key = "M3AT_BenchChibi", name = "M3AT's chibis", mode = 0 },
                new Condition { key = "M3AT_BenchIsoRim", name = "IsoRim's figures", mode = 1 },
            };
            if (tryNone)
            {
                conditions.Add(new Condition { key = "M3AT_BenchNone", name = "no people drawn", mode = 2 });
            }
            if (tryAddOns && addOns != null && addOns.Count > 0)
            {
                // Without all of them: M3AT's own cost is this against IsoRim's figures, theirs the chibi against this.
                conditions.Add(new Condition
                {
                    key = "M3AT_BenchNoAddOns", name = "M3AT's chibis without add-ons", mode = 0,
                    off = addOns.Select(a => a.packageId).ToArray(),
                });
                // And without each in turn, if there are more than one, for each one's own share.
                if (addOns.Count > 1)
                {
                    foreach (AddOns.AddOn a in addOns)
                    {
                        conditions.Add(new Condition
                        {
                            key = "M3AT_BenchWithout", label = "M3AT_BenchWithout".Translate(a.name), name = "M3AT's chibis without " + a.name,
                            mode = 0, off = new[] { a.packageId }, without = a,
                        });
                    }
                }
            }
            if (tryFewer)
            {
                conditions.Add(new Condition
                {
                    key = fewerWas ? "M3AT_BenchChibiFewerOff" : "M3AT_BenchChibiFewerOn",
                    name = "M3AT's chibis, fewer updates " + (fewerWas ? "off" : "on"), mode = 0, flipFewer = true,
                });
            }
            steps = new List<Step>();
            // Each one's first turn makes what it needs (hair built, IsoRim's figure looks made), the chibi's first of all.
            for (int c = 0; c < conditions.Count; c++)
            {
                steps.Add(new Step { c = conditions[c], seconds = c == 0 ? 2f * WarmSeconds : WarmSeconds, waitHair = c == 0 });
            }
            rounds = 0;
            measured = 0f;
            AddRound();
            map = Find.CurrentMap;
            speedAtStart = Find.TickManager.CurTimeSpeed;
            speedChanged = false;
            stopped = null;
            summary = null;
            report = null;
            stage = Stage.Running;
            preventCameraMotion = true;
            at = 0;
            Begin(steps[0]);
            Log.Message(Tag + "benchmark started: " + conditions.Count + " settings, turns of " + TurnFrames + " frames, " + Measured[length] + " s measured.");
        }

        private void Begin(Step s)
        {
            WalkFigures.PerfMode = s.c.mode;
            WalkFigures.PerfAllowed = null;
            AddOns.SetOff(s.c.off);   // (patches taken off or put back: a hitch in this frame, which the turn's first frames leave out)
            if (SettingsMod.Settings != null)
            {
                SettingsMod.Settings.fewerPoses = s.c.flipFewer ? !fewerWas : fewerWas;
            }
            WalkFigures.PerfTicks = 0;
            WalkFigures.PerfDraws = 0;
            WalkFigures.PerfAsked = 0;
            stepFrames.Clear();
            elapsed = 0f;
            framesIn = 0;
        }

        // A round: a turn for each setting, every other round in the reverse order. Rounds are added as they finish, until
        // the length's measured time is reached on an even count (so each order is played as often).
        private void AddRound()
        {
            rounds++;
            for (int i = 0; i < conditions.Count; i++)
            {
                Condition c = conditions[rounds % 2 == 1 ? i : conditions.Count - 1 - i];
                steps.Add(new Step { c = c, record = true, round = rounds });
            }
        }

        private bool MoreRounds()
        {
            return rounds < MostRounds && (rounds % 2 == 1 || rounds < 4 || measured < Measured[length]);
        }

        // Rounds a run will take about, at the frame time so far.
        private int RoundsAbout()
        {
            float turn = TurnFrames * Mathf.Max(1f, conditions[0].Mean > 0f ? conditions[0].Mean : 16.7f) / 1000f;
            int n = Mathf.CeilToInt(Measured[length] / (turn * (TurnFrames - SkipFrames) / TurnFrames * conditions.Count));
            return Math.Max(rounds, Math.Max(4, n + n % 2));
        }

        private float SecondsLeft()
        {
            float sum = 0f;
            for (int i = at; i < steps.Count; i++)
            {
                if (!steps[i].record)
                {
                    sum += Mathf.Max(0f, steps[i].seconds - (i == at ? elapsed : 0f));
                }
            }
            return sum + Mathf.Max(0f, Measured[length] - measured) * TurnFrames / (TurnFrames - SkipFrames);
        }

        private string WhyStop()
        {
            if (Current.ProgramState != ProgramState.Playing)
            {
                return "M3AT_BenchStoppedGame";
            }
            if (Find.CurrentMap != map)
            {
                return "M3AT_BenchStoppedMap";
            }
            if (!IsoRim.Api.View3DActive)
            {
                return "M3AT_BenchStoppedView";
            }
            return null;
        }

        // Every frame, as the window stack updates.
        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (stage != Stage.Running)
            {
                return;
            }
            string why = WhyStop();
            if (why != null)
            {
                Finish(why);
                return;
            }
            if (Find.TickManager.CurTimeSpeed != speedAtStart)
            {
                speedChanged = true;
            }
            Step s = steps[at];
            float dt = Time.unscaledDeltaTime;
            framesIn++;
            if (s.record && framesIn <= SkipFrames)
            {
                // Settling after the switch: the counters start with the first measured frame.
                if (framesIn == SkipFrames)
                {
                    WalkFigures.PerfTicks = 0;
                    WalkFigures.PerfDraws = 0;
                    WalkFigures.PerfAsked = 0;
                }
                left = SecondsLeft();
                return;
            }
            if (s.record)
            {
                stepFrames.Add(dt * 1000f);
            }
            elapsed += dt;
            bool waiting = s.waitHair && WalkFigures.HairBuilding > 0 && elapsed < HairWaitMost;
            if (s.record ? framesIn < TurnFrames : elapsed < s.seconds || waiting)
            {
                left = SecondsLeft();
                return;
            }
            if (s.record)
            {
                s.c.frames.AddRange(stepFrames);
                s.c.turns[s.round] = Mean(stepFrames);
                s.c.drawerMs += WalkFigures.PerfTicks * 1000.0 / Stopwatch.Frequency;
                s.c.draws += WalkFigures.PerfDraws;
                s.c.asked += WalkFigures.PerfAsked;
                measured += Mean(stepFrames) * stepFrames.Count / 1000f;
            }
            at++;
            if (at >= steps.Count && MoreRounds())
            {
                AddRound();
            }
            if (at >= steps.Count)
            {
                Finish(null);
                return;
            }
            Begin(steps[at]);
            left = SecondsLeft();
        }

        private void PutBack()
        {
            AddOns.SetOff(null);
            WalkFigures.PerfMode = 0;
            WalkFigures.PerfAllowed = null;
            if (SettingsMod.Settings != null && conditions != null)
            {
                SettingsMod.Settings.fewerPoses = fewerWas;
            }
            preventCameraMotion = false;
        }

        private void Finish(string why)
        {
            PutBack();
            stage = Stage.Done;
            stopped = why;
            if (why != null)
            {
                Log.Message(Tag + "benchmark stopped: " + why);
                return;
            }
            try
            {
                Results();
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, report);
                Log.Message(Tag + "benchmark done, results in " + FilePath + ":\n" + report);
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "the benchmark's results could not be written: " + e.Message);
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            if (stage == Stage.Running)
            {
                PutBack();
            }
        }

        // ---- results

        internal static float Mean(List<float> xs)
        {
            if (xs.Count == 0)
            {
                return 0f;
            }
            double sum = 0;
            foreach (float x in xs)
            {
                sum += x;
            }
            return (float)(sum / xs.Count);
        }

        internal static float StandardError(List<float> xs)
        {
            int n = xs.Count;
            if (n < 2)
            {
                return 0f;
            }
            float m = Mean(xs), v = 0f;
            foreach (float x in xs)
            {
                v += (x - m) * (x - m);
            }
            return Mathf.Sqrt(v / (n - 1) / n);
        }

        // The mean of xs without the Trim share at each end.
        internal static float Trimmed(List<float> xs)
        {
            List<float> sorted = new List<float>(xs);
            sorted.Sort();
            int cut = Mathf.FloorToInt(sorted.Count * Trim);
            return Mean(sorted.GetRange(cut, sorted.Count - 2 * cut));
        }

        // How much slower a is than b (ms a frame), as the mean of each round's difference between their turns, and the
        // noise in that: twice its standard error, within which the two can't be told apart.
        private static float Paired(Condition a, Condition b, out float noise)
        {
            List<float> d = new List<float>();
            foreach (KeyValuePair<int, float> turn in a.turns)
            {
                if (b.turns.TryGetValue(turn.Key, out float other))
                {
                    d.Add(turn.Value - other);
                }
            }
            d.Sort();
            int cut = Mathf.FloorToInt(d.Count * Trim);
            List<float> kept = d.GetRange(cut, d.Count - 2 * cut);
            noise = 2f * StandardError(kept);
            return Mean(kept);
        }

        private static float Median(List<float> xs)
        {
            if (xs.Count == 0)
            {
                return 0f;
            }
            List<float> sorted = new List<float>(xs);
            sorted.Sort();
            return sorted[sorted.Count / 2];
        }

        // Within CapShare of a refresh rate or frame limit a monitor or driver commonly holds the game to.
        private static readonly float[] Caps = { 30f, 60f, 72f, 75f, 90f, 100f, 120f, 144f, 165f, 170f, 180f, 240f, 360f };
        private const float CapShare = 0.015f;

        private static bool NearCap(float fps)
        {
            foreach (float cap in Caps)
            {
                if (Mathf.Abs(fps - cap) < CapShare * cap)
                {
                    return true;
                }
            }
            return false;
        }

        private static string F(float x, int places = 1)
        {
            return x.ToString(places == 0 ? "0" : places == 1 ? "0.0" : "0.00");
        }

        private void Results()
        {
            Condition chibi = conditions[0], iso = conditions[1];
            Condition none = conditions.Find(c => c.mode == 2), fewer = conditions.Find(c => c.flipFewer);
            summary = new List<string>();
            StringBuilder s = new StringBuilder();
            s.AppendLine("## M3AT frame rate benchmark");
            s.AppendLine();
            s.AppendLine("```");
            foreach (Condition c in conditions)
            {
                summary.Add("M3AT_BenchRow".Translate(c.Label, F(c.Fps), F(c.LowFps), F(c.Mean, 2)));
            }
            float people = chibi.PerFrame(chibi.asked);
            float cost = Paired(chibi, iso, out float noise);
            if (Mathf.Abs(cost) <= noise)
            {
                summary.Add("M3AT_BenchSame".Translate(F(Mathf.Abs(cost), 2), F(noise, 2)));
            }
            else if (cost > 0f)
            {
                summary.Add("M3AT_BenchCostMore".Translate(F(cost, 2), F(chibi.Fps), F(iso.Fps), F(100f * (1f - chibi.Fps / Mathf.Max(0.01f, iso.Fps)), 0)));
            }
            else
            {
                summary.Add("M3AT_BenchCostLess".Translate(F(-cost, 2), F(chibi.Fps), F(iso.Fps)));
            }
            float ours = none != null ? Paired(chibi, none, out _) : 0f, theirs = none != null ? Paired(iso, none, out _) : 0f;
            if (none != null && people >= 1f)
            {
                summary.Add("M3AT_BenchPeople".Translate(F(ours, 2), F(theirs, 2), F(people, 0), F(Mathf.Max(0f, ours) / people, 3)));
            }
            if (fewer != null)
            {
                float saved = Paired(chibi, fewer, out float fewerNoise);
                string option = "M3AT_FewerPoses".Translate(), way = (fewerWas ? "M3AT_BenchOff" : "M3AT_BenchOn").Translate();
                summary.Add(saved > fewerNoise
                    ? "M3AT_BenchFewerSaves".Translate(option, way, F(saved, 2), F(fewer.Fps))
                    : "M3AT_BenchFewerNoSaving".Translate(option, way, F(fewer.Fps)));
            }
            Condition bare = conditions.Find(c => c.off != null && c.without == null);
            List<string> addOnLines = new List<string>();
            if (bare != null)
            {
                float own = Paired(bare, iso, out float ownNoise), extra = Paired(chibi, bare, out float extraNoise);
                summary.Add("M3AT_BenchOwnCost".Translate(F(own, 2), F(ownNoise, 2)));
                summary.Add(Mathf.Abs(extra) <= extraNoise ? "M3AT_BenchAddOnsSame".Translate(F(extraNoise, 2))
                    : "M3AT_BenchAddOnsCost".Translate(F(extra, 2), F(extraNoise, 2)));
                addOnLines.Add("M3AT alone (no add-ons) over IsoRim's figures: " + F(own, 2) + " ms a frame (+/-" + F(ownNoise, 2) + ")");
                addOnLines.Add("All add-ons together: " + F(extra, 2) + " ms a frame (+/-" + F(extraNoise, 2) + ")");
                foreach (Condition c in conditions.Where(c => c.without != null))
                {
                    float one = Paired(chibi, c, out float oneNoise);
                    summary.Add(Mathf.Abs(one) <= oneNoise ? "M3AT_BenchAddOnSame".Translate(c.without.name, F(oneNoise, 2))
                        : "M3AT_BenchAddOnCost".Translate(c.without.name, F(one, 2), F(oneNoise, 2)));
                    addOnLines.Add("  " + c.without.name + ": " + F(one, 2) + " ms a frame (+/-" + F(oneNoise, 2) + ")");
                }
                foreach (AddOns.AddOn a in addOns)
                {
                    addOnLines.Add("  add-on " + a.name + " (" + a.packageId + "): " + a.garments + " garments, " + a.patches + " patches on M3AT's code, " + a.posers + " posers");
                }
            }
            if (chibi.draws == 0)
            {
                summary.Add("M3AT_BenchNoChibis".Translate());
            }
            else if (people < FewPeople)
            {
                summary.Add("M3AT_BenchFewPeople".Translate());
            }
            // A frame cap: no people drawn came out as fast as the chibi, or both settings sat on a common refresh rate.
            bool capped = none != null ? Mathf.Abs(chibi.Mean - none.Mean) < SameShare * none.Mean
                : NearCap(chibi.Fps) && NearCap(iso.Fps) && Mathf.Abs(cost) <= Mathf.Max(noise, SameShare * iso.Mean);
            if (chibi.draws > 0 && capped)
            {
                summary.Add("M3AT_BenchCapped".Translate());
            }
            if (speedChanged)
            {
                summary.Add("M3AT_BenchSpeedChanged".Translate());
            }

            // The report to share, in English like the bug report.
            s.AppendLine("Rounds: " + rounds + ", a turn of " + TurnFrames + " frames per setting each (the first " + SkipFrames + " after the switch left out), every other round in reverse order; "
                + "frame times and differences are trimmed means (" + F(100f * Trim, 0) + "% off each end) of the turns' and the rounds' differences");
            s.AppendLine("Game speed: " + speedAtStart + (speedChanged ? " (changed during the run)" : ""));
            s.AppendLine("People drawn per frame: " + F(people) + " (" + map.mapPawns.AllPawnsSpawned.Count(p => p.RaceProps.Humanlike) + " humanlike pawns on the map)");
            s.AppendLine();
            s.AppendLine(string.Format("{0,-36} {1,7} {2,8} {3,8} {4,8} {5,8} {6,10}", "setting", "FPS", "1% low", "mean ms", "med. ms", "+/- ms", "our CPU ms"));
            foreach (Condition c in conditions)
            {
                s.AppendLine(string.Format("{0,-36} {1,7} {2,8} {3,8} {4,8} {5,8} {6,10}", c.name, F(c.Fps), F(c.LowFps), F(c.Mean, 2), F(Median(c.frames), 2),
                    F(c.Error, 2), c.mode == 0 ? F((float)(c.drawerMs / Math.Max(1, c.frames.Count)), 2) : "-"));
            }
            s.AppendLine("(+/- is the standard error of the mean frame time from the spread of its turns; our CPU ms is the time in M3AT's figure drawer a frame)");
            s.AppendLine();
            s.AppendLine("M3AT" + (bare != null ? " with its add-ons" : "") + " over IsoRim's figures: " + (cost >= 0f ? "+" : "") + F(cost, 2) + " ms a frame" + (Mathf.Abs(cost) <= noise ? " (within noise, +/-" + F(noise, 2) + ")" : ""));
            foreach (string line in addOnLines)
            {
                s.AppendLine(line);
            }
            if (none != null)
            {
                s.AppendLine("People with M3AT: " + F(ours, 2) + " ms a frame; with IsoRim's figures: " + F(theirs, 2) + " ms" + (people >= 1f ? "; per chibi: " + F(Mathf.Max(0f, ours) / people, 3) + " ms" : ""));
            }
            s.AppendLine();
            s.AppendLine("Screen: " + Screen.width + "x" + Screen.height);
            s.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + ", " + SystemInfo.graphicsMemorySize + " MB");
            s.AppendLine("CPU: " + SystemInfo.processorType + ", " + SystemInfo.processorCount + " threads, " + SystemInfo.systemMemorySize + " MB");
            s.AppendLine("OS: " + SystemInfo.operatingSystem);
            s.AppendLine("RimWorld: " + VersionControl.CurrentVersionStringWithRev + ", IsoRim Api " + IsoRim.Api.Version + ", " + ModsConfig.ActiveModsInLoadOrder.Count() + " mods active");
            s.AppendLine("M3AT: " + WalkFigures.StartState + ", GPU skinning in use " + SkinnedFigure.GpuSkinning);
            Settings o = SettingsMod.Settings;
            if (o != null)
            {
                s.AppendLine("Options: fewerPoses " + fewerWas + ", gpuSkinning " + o.gpuSkinning + ", hairDetail " + o.hairDetail + ", beardMode " + o.beardMode
                    + ", outlines " + o.outlines + ", outlineMode " + o.outlineMode + ", clothesOcclusion " + o.clothesOcclusion + ", freecam " + Freecam.On);
            }
            s.AppendLine("```");
            report = s.ToString();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && home.Length > 3)
            {
                report = report.Replace(home, "~");
            }
        }

        // ---- drawing

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            Text.Font = GameFont.Medium;
            list.Label("M3AT_Benchmark".Translate());
            Text.Font = GameFont.Small;
            list.GapLine(6f);
            if (stage == Stage.Setup)
            {
                Setup(list);
            }
            else if (stage == Stage.Running)
            {
                Running(list, inRect.width);
            }
            else
            {
                Done(list);
            }
            list.End();
        }

        private void Setup(Listing_Standard list)
        {
            list.Label("M3AT_BenchIntro".Translate());
            list.Gap(8f);
            list.Label("M3AT_BenchLength".Translate());
            for (int i = 0; i < LengthKeys.Length; i++)
            {
                if (list.RadioButton(LengthKeys[i].Translate() + " (" + "M3AT_BenchAbout".Translate(F(Estimate(i), 0)) + ")", length == i))
                {
                    length = i;
                }
            }
            list.Gap(6f);
            list.CheckboxLabeled((FewerOnNow ? "M3AT_BenchTryFewerOff" : "M3AT_BenchTryFewerOn").Translate("M3AT_FewerPoses".Translate()), ref tryFewer);
            list.CheckboxLabeled("M3AT_BenchTryNone".Translate(), ref tryNone, "M3AT_BenchTryNoneTip".Translate());
            if (addOns == null)
            {
                addOns = AddOns.Find();
            }
            if (addOns.Count > 0)
            {
                list.CheckboxLabeled("M3AT_BenchTryAddOns".Translate(addOns.Count), ref tryAddOns,
                    "M3AT_BenchTryAddOnsTip".Translate(string.Join("\n", addOns.Select(a => "- " + a.name + ": " + "M3AT_BenchAddOnParts".Translate(a.garments, a.patches, a.posers)))));
            }
            list.Gap(10f);
            if (!CanRun)
            {
                list.Label("M3AT_BenchmarkNeedsGame".Translate());
            }
            else if (list.ButtonText("M3AT_BenchStart".Translate()))
            {
                Start();
            }
        }

        // Seconds a run of that length takes, besides waiting on hair.
        private float Estimate(int which)
        {
            int n = 2 + (tryNone ? 1 : 0) + (tryFewer ? 1 : 0)
                + (tryAddOns && addOns != null && addOns.Count > 0 ? 1 + (addOns.Count > 1 ? addOns.Count : 0) : 0);
            return WarmSeconds * (n + 1) + Measured[which] * TurnFrames / (TurnFrames - SkipFrames);
        }

        private void Running(Listing_Standard list, float width)
        {
            Step s = steps[at];
            if (s.round == 0)
            {
                list.Label("M3AT_BenchWarming".Translate(s.c.Label));
                if (s.waitHair && WalkFigures.HairBuilding > 0)
                {
                    list.Label("M3AT_BenchWaitHair".Translate(WalkFigures.HairBuilding));
                }
            }
            else
            {
                list.Label("M3AT_BenchMeasuring".Translate(s.c.Label, s.round, RoundsAbout()));
            }
            list.Label("M3AT_BenchLeft".Translate(F(left, 0)));
            list.Label("M3AT_BenchUnpaused".Translate());
            list.Label("M3AT_BenchFlicker".Translate());
            Rect bar = list.GetRect(22f);
            float total = 0f, done = 0f;
            for (int i = 0; i < steps.Count; i++)
            {
                total += steps[i].seconds;
                done += i < at ? steps[i].seconds : i == at ? Mathf.Min(elapsed, steps[i].seconds) : 0f;
            }
            Widgets.FillableBar(bar, total > 0f ? done / total : 0f);
            list.Gap(10f);
            if (list.ButtonText("M3AT_BenchCancel".Translate()))
            {
                Finish("M3AT_BenchStoppedUser");
            }
        }

        private void Done(Listing_Standard list)
        {
            if (stopped != null)
            {
                list.Label(stopped.Translate());
            }
            else if (summary != null)
            {
                foreach (string line in summary)
                {
                    list.Label("• " + line);
                    list.Gap(2f);
                }
                list.Gap(8f);
                if (report != null && list.ButtonText("M3AT_BenchCopy".Translate()))
                {
                    GUIUtility.systemCopyBuffer = report;
                    Messages.Message("M3AT_BenchCopied".Translate(FilePath.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~")),
                        MessageTypeDefOf.NeutralEvent, false);
                }
            }
            list.Gap(6f);
            if (list.ButtonText("M3AT_BenchAgain".Translate()))
            {
                stage = Stage.Setup;
                addOns = null;
            }
        }
    }
}
