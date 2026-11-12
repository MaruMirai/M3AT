using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace M3AT
{
    // A trail of what this mod and the game were doing, with the memory in use, written to disk as it goes and synced, so
    // a freeze that takes the whole system with it (the game filling the memory: with no swap the system thrashes for
    // minutes before anything is killed, and the game's own log never gets the end written) still leaves what it was doing
    // and how much it held: <config>/M3AT/trail.txt, the last session's trail-prev.txt beside it.
    //
    // A watcher thread looks every WatchEvery: it writes a memory line when the game's resident memory has moved by
    // MemoryStep since the last, when the system is low (LowMemory, then every LowEvery), and every Heartbeat besides.
    // If the main thread hasn't finished a frame for StallSeconds (a hang in the game rather than the system) it writes what
    // it was on, IsoRim's crash trail, and the stacks of every managed thread from dotnet-stack, if that is installed
    // (`dotnet tool install -g dotnet-stack`), once per stall, also into freeze-<time>.txt.
    public static class LoadTrail
    {
        private const string Tag = "[M3AT] ";

        private const int WatchEvery = 500;            // ms
        private const long MemoryStep = 256L << 20;    // bytes of resident memory moved before another line
        private const long LowMemory = 2048L << 20;    // the system's available memory under which it writes more often
        private const double LowEvery = 2.0;           // real seconds between lines while low
        private const double Heartbeat = 30.0;         // real seconds between lines otherwise
        private const double StallSeconds = 10.0;      // a main thread frame this long is a stall
        private const int StackTimeout = 120000;       // ms dotnet-stack may take (slow if the system is thrashing)

        private static readonly object sync = new object();
        private static readonly List<string> pending = new List<string>();
        private static string path;
        private static Thread watcher;
        private static readonly Stopwatch clock = Stopwatch.StartNew();

        // Stamped by the main thread at the start of each frame (Root.Update), read by the watcher.
        private static long frameAt;
        private static int frames;

        // What the main thread said last (it owns these; the watcher only reads them for its reports).
        private static volatile string lastNote = "";
        private static volatile string longEvent = "";
        private static ProgramState? lastState;

        // Counts the rest of the mod adds to, shown on each memory line.
        public static int shapesMade;
        public static int hairBuilt;

        private static FieldInfo currentEvent, eventText;

        public static string FolderPath => Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT");

        // From the mod's constructor, as early as the mod is loaded, so the game's own load is in the trail too.
        public static void Start(Harmony harmony)
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                path = Path.Combine(FolderPath, "trail.txt");
                string prev = Path.Combine(FolderPath, "trail-prev.txt");
                if (File.Exists(path))
                {
                    File.Copy(path, prev, true);
                    File.Delete(path);
                }
                string built = "?";
                try
                {
                    built = File.GetLastWriteTimeUtc(typeof(LoadTrail).Assembly.Location).ToString("yyyy-MM-dd HH:mm") + " UTC";
                }
                catch
                {
                }
                Write("M3AT trail, started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ", pid " + Environment.ProcessId
                      + ", DLL built " + built + ", system memory " + Mb(MemInfo("MemTotal")) + " MB, swap " + Mb(MemInfo("SwapTotal")) + " MB");
                Write("columns: time, real seconds, frame, what");

                currentEvent = AccessTools.Field(typeof(LongEventHandler), "currentEvent");
                if (currentEvent != null)
                {
                    eventText = AccessTools.Field(currentEvent.FieldType, "eventText");
                }
                harmony.Patch(AccessTools.Method(typeof(Root), nameof(Root.Update)),
                              prefix: new HarmonyMethod(typeof(LoadTrail), nameof(FramePrefix)));

                frameAt = clock.ElapsedTicks;
                watcher = new Thread(Watch) { IsBackground = true, Name = "M3AT trail watcher", Priority = ThreadPriority.AboveNormal };
                watcher.Start();
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "the load trail could not start: " + e.Message);
            }
        }

        // Anything worth knowing at a load step or a big allocation: any thread, cheap (the watcher writes it out).
        public static void Note(string what)
        {
            if (path == null)
            {
                return;
            }
            string line = Stamp() + what;
            if (Thread.CurrentThread.ManagedThreadId == mainThread)
            {
                lastNote = what;
            }
            lock (sync)
            {
                pending.Add(line);
            }
        }

        private static int mainThread = -1;

        // The main thread's frame: stamped, and the program state and long event noted when they change.
        public static void FramePrefix()
        {
            frameAt = clock.ElapsedTicks;
            frames++;
            if (mainThread < 0)
            {
                mainThread = Thread.CurrentThread.ManagedThreadId;
            }
            try
            {
                ProgramState state = Current.ProgramState;
                if (state != lastState)
                {
                    lastState = state;
                    Note("program state " + state + MemoryNow());
                }
                string text = "";
                object e = currentEvent != null ? currentEvent.GetValue(null) : null;
                if (e != null && eventText != null)
                {
                    text = eventText.GetValue(e) as string ?? "(untitled)";
                }
                if (text != longEvent)
                {
                    longEvent = text;
                    Note(text.Length == 0 ? "long event done" + MemoryNow() : "long event: " + text + MemoryNow());
                }
            }
            catch
            {
            }
        }

        // A short memory reading for a note: the game's resident memory and the system's available.
        public static string MemoryNow()
        {
            return " [game " + Mb(Resident()) + " MB, available " + Mb(MemInfo("MemAvailable")) + " MB]";
        }

        // ---------------------------------------------------------------- the watcher

        private static void Watch()
        {
            long lastResident = 0;
            double lastLine = -999, stallReported = -1;
            while (true)
            {
                try
                {
                    Thread.Sleep(WatchEvery);
                    double now = clock.Elapsed.TotalSeconds;
                    long resident = Resident(), available = MemInfo("MemAvailable");
                    bool low = available > 0 && available < LowMemory;
                    if (Math.Abs(resident - lastResident) >= MemoryStep || now - lastLine >= (low ? LowEvery : Heartbeat))
                    {
                        lastResident = resident;
                        lastLine = now;
                        lock (sync)
                        {
                            pending.Add(Stamp() + "memory: game " + Mb(resident) + " MB (managed heap " + Mb(GC.GetTotalMemory(false))
                                        + " MB), system available " + Mb(available) + " MB" + (low ? " LOW" : "") + ", pressure " + Pressure()
                                        + "; shapes " + shapesMade + ", hair built " + hairBuilt
                                        + (longEvent.Length > 0 ? "; in long event \"" + longEvent + "\"" : ""));
                        }
                    }

                    double frame = (clock.ElapsedTicks - Interlocked.Read(ref frameAt)) / (double)Stopwatch.Frequency;
                    if (frame >= StallSeconds && stallReported < 0)
                    {
                        stallReported = frame;
                        Stalled(frame);
                    }
                    else if (frame < StallSeconds && stallReported >= 0)
                    {
                        lock (sync)
                        {
                            pending.Add(Stamp() + "main thread going again" + MemoryNow());
                        }
                        stallReported = -1;
                    }
                    Flush();
                }
                catch (Exception)
                {
                    // Keep watching: the trail is never worth taking the game down for.
                }
            }
        }

        private static void Stalled(double seconds)
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("STALL: no frame for " + seconds.ToString("0.0") + " s, at frame " + frames + MemoryNow());
            report.AppendLine("  last main thread note: " + lastNote);
            report.AppendLine("  long event: " + (longEvent.Length > 0 ? longEvent : "(none)"));
            try
            {
                report.AppendLine("  " + IsoRim.CrashTrail.Report().Replace("\n", "\n  "));
            }
            catch (Exception e)
            {
                report.AppendLine("  (IsoRim's trail: " + e.Message + ")");
            }
            lock (sync)
            {
                pending.Add(Stamp() + report.ToString().TrimEnd());
            }
            Flush();

            string stacks = Stacks();
            string freeze = Path.Combine(FolderPath, "freeze-" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".txt");
            try
            {
                File.WriteAllText(freeze, report + "\n" + stacks);
            }
            catch
            {
            }
            lock (sync)
            {
                pending.Add(Stamp() + "stacks written to " + freeze + "\n" + stacks);
            }
        }

        // Every managed thread's stack, by dotnet-stack on this process (it talks to the runtime's diagnostics socket, so
        // it works while the main thread is stuck).
        private static string Stacks()
        {
            string tool = null;
            foreach (string dir in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools") }
                     .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')))
            {
                string p = Path.Combine(dir, "dotnet-stack");
                if (dir.Length > 0 && File.Exists(p))
                {
                    tool = p;
                    break;
                }
            }
            if (tool == null)
            {
                return "(no dotnet-stack: `dotnet tool install -g dotnet-stack` to get the threads' stacks here)";
            }
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(tool, "report -p " + Environment.ProcessId)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                // The game's runtime settings would point the tool at the game's own runtime; it wants the system's.
                foreach (string key in new List<string>(EnvironmentKeys()))
                {
                    if (key.StartsWith("DOTNET_") || key.StartsWith("COMPlus_") || key.StartsWith("CORECLR_"))
                    {
                        start.Environment.Remove(key);
                    }
                }
                using (Process p = Process.Start(start))
                {
                    System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(StackTimeout))
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch
                        {
                        }
                        return "(dotnet-stack took longer than " + StackTimeout / 1000 + " s)";
                    }
                    return output.Result + (err.Result.Length > 0 ? "\n(stderr) " + err.Result : "");
                }
            }
            catch (Exception e)
            {
                return "(dotnet-stack failed: " + e.Message + ")";
            }
        }

        private static IEnumerable<string> EnvironmentKeys()
        {
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                yield return (string)e.Key;
            }
        }

        // ---------------------------------------------------------------- writing

        private static string Stamp()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff") + " " + clock.Elapsed.TotalSeconds.ToString("0.0") + "s f" + frames + "  ";
        }

        private static void Write(string line)
        {
            lock (sync)
            {
                pending.Add(line);
            }
            Flush();
        }

        // Appends what is waiting and syncs it to the disk (fsync), as a hard reset loses what is only in the page cache.
        private static void Flush()
        {
            string[] lines;
            lock (sync)
            {
                if (pending.Count == 0)
                {
                    return;
                }
                lines = pending.ToArray();
                pending.Clear();
            }
            using (FileStream f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
                f.Write(bytes, 0, bytes.Length);
                f.Flush(true);
            }
        }

        // ---------------------------------------------------------------- reading the memory (Linux; 0 elsewhere)

        private static long Resident()
        {
            try
            {
                string[] t = File.ReadAllText("/proc/self/statm").Split(' ');
                return long.Parse(t[1]) * Environment.SystemPageSize;
            }
            catch
            {
                try
                {
                    return Environment.WorkingSet;
                }
                catch
                {
                    return 0;
                }
            }
        }

        private static long MemInfo(string key)
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith(key + ":"))
                    {
                        string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        return long.Parse(t[1]) * 1024;
                    }
                }
            }
            catch
            {
            }
            return 0;
        }

        // The kernel's memory pressure: the share of the last ten seconds some and all tasks waited on memory.
        private static string Pressure()
        {
            try
            {
                string some = "?", full = "?";
                foreach (string line in File.ReadLines("/proc/pressure/memory"))
                {
                    string[] t = line.Split(' ');
                    string avg = t.Length > 1 && t[1].StartsWith("avg10=") ? t[1].Substring(6) : "?";
                    if (t[0] == "some")
                    {
                        some = avg;
                    }
                    else if (t[0] == "full")
                    {
                        full = avg;
                    }
                }
                return "some " + some + "% full " + full + "%";
            }
            catch
            {
                return "?";
            }
        }

        private static long Mb(long bytes)
        {
            return bytes >> 20;
        }
    }
}
