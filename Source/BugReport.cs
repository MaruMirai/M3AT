using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using RimWorld;
using Verse;
using Verse.AI.Group;
using LudeonTK;

namespace M3AT
{
    // What WalkFigures knows about its own start, for the bug report: whether it loaded the chibi and from which folder.
    public static partial class WalkFigures
    {
        internal static string StartState
        {
            get
            {
                if (content == null)
                {
                    return "not started (IsoRim missing or too old, or startup failed: see the log lines below)";
                }
                if (!tried)
                {
                    return "waiting for the first pawn drawn in 3D";
                }
                return ready ? "chibi loaded from " + Folder : "the chibi in " + Folder + " could not be read, so IsoRim's figures are drawn";
            }
        }

        // One pawn as the figure sees it, for a bug report: what the game has it doing, its shape, what it wears and which
        // garment each is drawn as, its weapon and how that is held, and every Ease field that isn't at rest. Each part on
        // its own, so a part that throws still leaves the rest.
        internal static void PawnReport(Pawn pawn, StringBuilder s)
        {
            s.AppendLine(pawn.LabelShort + " (" + pawn.ThingID + ", " + pawn.def.defName + (pawn.Faction != null ? ", " + pawn.Faction.def.defName : "") + ")");
            Part(s, "game", () => "job=" + (pawn.CurJob != null ? pawn.CurJob.def.defName + " (" + pawn.jobs.curDriver?.GetType().Name + ", toil " + pawn.jobs.curDriver?.CurToilIndex + ")" : "none")
                + " lord=" + (pawn.GetLord()?.LordJob?.GetType().Name ?? "none")
                + " posture=" + pawn.GetPosture() + " drafted=" + pawn.Drafted + " downed=" + pawn.Downed + " dead=" + pawn.Dead
                + " inBed=" + pawn.InBed() + " moving=" + (pawn.pather != null && pawn.pather.Moving)
                + " stance=" + (pawn.stances?.curStance?.GetType().Name ?? "none")
                + " carrying=" + (pawn.carryTracker?.CarriedThing?.def.defName ?? "nothing")
                + " carriedBy=" + (pawn.CarriedBy?.LabelShort ?? "nobody")
                + " cell=" + pawn.Position + " map=" + (pawn.Map != null ? pawn.Map.Index.ToString() : "none"));
            Part(s, "body", () => "age=" + pawn.ageTracker.AgeBiologicalYearsFloat.ToString("0.0") + " stage=" + pawn.DevelopmentalStage
                + " gender=" + pawn.gender + " bodyType=" + (pawn.story?.bodyType?.defName ?? "none")
                + " head=" + (pawn.story?.headType?.defName ?? "none") + " size=" + SizeOf(pawn).ToString("0.000")
                + " hair=" + (pawn.story?.hairDef?.defName ?? "none") + " beard=" + (pawn.style?.beardDef?.defName ?? "none"));
            Part(s, "shape", () =>
            {
                if (skin == null)
                {
                    return "(no skinned figure: " + StartState + ")";
                }
                SkinnedFigure.Shape shape = ShapeFor(pawn);
                return shape.mix == null ? "base" : string.Join(", ", shape.mix.Select(k => k.Key + "=" + k.Value.ToString("0.00")));
            });
            Part(s, "genes", () => pawn.genes == null ? "none" : string.Join(", ", pawn.genes.GenesListForReading.Where(g => g.Active).Select(g => g.def.defName)));
            Part(s, "missing", () => string.Join(", ", pawn.health.hediffSet.GetMissingPartsCommonAncestors().Select(h => h.Part.def.defName + "(" + h.Part.Label + ")")));
            Part(s, "injuries", () => pawn.health.hediffSet.hediffs.OfType<Hediff_Injury>().Count() + " (" + pawn.health.hediffSet.hediffs.Count(h => h.Bleeding) + " bleeding)");
            Part(s, "apparel", () => ApparelReport(pawn));
            Part(s, "weapon", () => WeaponReport(pawn.equipment?.Primary));
            Part(s, "held", () => HeldWeapon(pawn)?.def.defName ?? "none");
            Part(s, "clips", () => "drafted=" + (DraftedClip(pawn) ?? "none") + " aim=" + (AimName(pawn) ?? "none") + " carry=" + (CarryClip(pawn) ?? "none"));
            Part(s, "inventory weapons", () => pawn.inventory == null ? "none" : string.Join("; ",
                pawn.inventory.innerContainer.OfType<ThingWithComps>().Where(t => t.def.equipmentType == EquipmentType.Primary).Select(WeaponReport)));
            Part(s, "ease", () => EaseReport(pawn));
        }

        private static void Part(StringBuilder s, string name, Func<string> read)
        {
            string line;
            try
            {
                line = read();
            }
            catch (Exception e)
            {
                line = "(could not be read: " + e.GetType().Name + ": " + e.Message + ")";
            }
            s.AppendLine("  " + name + ": " + (string.IsNullOrEmpty(line) ? "none" : line));
        }

        // Each worn apparel with its layers and the garments it is drawn as (WornGarments: its own, a stand-in, the shoes or
        // a romper going with it), "not drawn" for one with none, so a missing or wrong garment shows.
        private static string ApparelReport(Pawn pawn)
        {
            List<Apparel> all = pawn.apparel?.WornApparel;
            if (skin == null || all == null)
            {
                return all == null ? "none" : string.Join(", ", all.Select(a => a.def.defName));
            }
            Apparel[] worn = new Apparel[skin.Pieces];
            WornGarments(pawn, worn);
            List<string> parts = new List<string>();
            foreach (Apparel a in all)
            {
                List<string> drawn = new List<string>();
                for (int g = 1; g < skin.Pieces; g++)
                {
                    if (worn[g] == a)
                    {
                        drawn.Add(skin.PieceName(g) + (skin.GarmentOf(a.def) == g ? "" : " (stand-in)"));
                    }
                }
                string layers = a.def.apparel?.layers == null ? "" : string.Join("/", a.def.apparel.layers.Select(l => l.defName));
                parts.Add(a.def.defName + " [" + layers + "] -> " + (drawn.Count > 0 ? string.Join(", ", drawn) : "not drawn"));
            }
            return string.Join("\n    ", new[] { "" }.Concat(parts));
        }

        // A weapon's names as this mod looks it up: the clips and holds go by WeaponName, then KindOf; its type sets its
        // size and holster; a slab takes the sprite sets.
        private static string WeaponReport(ThingWithComps weapon)
        {
            if (weapon == null)
            {
                return "none";
            }
            return weapon.def.defName + " name=" + WeaponName(weapon.def) + " kind=" + KindOf(weapon.def) + " type=" + TypeOf(weapon.def)
                + " slab=" + AsSlab(weapon) + " from=" + (weapon.def.modContentPack?.PackageIdPlayerFacing ?? "?");
        }

        // The pawn's Ease fields that aren't at their rest value (0, false, null, -1), so the line stays short.
        private static string EaseReport(Pawn pawn)
        {
            Ease e;
            if (!eases.TryGetValue(pawn.thingIDNumber, out e))
            {
                return "(not drawn yet)";
            }
            List<string> parts = new List<string>();
            foreach (FieldInfo field in typeof(Ease).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = field.GetValue(e);
                if (v == null || v is bool b && !b || v is float f && (f == 0f || f == -1f) || v is int i && (i == 0 || i == -1))
                {
                    continue;
                }
                parts.Add(field.Name + "=" + (v is float x ? x.ToString("0.###") : v.ToString()));
            }
            return string.Join(" ", parts);
        }

        // The same report on the selected pawns from the dev tools, for testing it without the options window.
        [DebugAction("M3AT", "Copy bug report (selected pawns)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugReport()
        {
            string saved = BugReport.CopyAndSave();
            Messages.Message("Bug report copied" + (saved != null ? " and saved to " + saved : "") + ".", RimWorld.MessageTypeDefOf.NeutralEvent, false);
        }
    }

    // A bug report for players to paste into a Workshop thread or an issue (the mod options' "Copy bug report"): the mod's
    // build, IsoRim's Api, the mods it most depends on, the whole load order, every option, the graphics card, and every
    // log message from this mod or with this mod in its stack trace. Copied to the clipboard and written to
    // <config>/M3AT/bug_report.txt, so it can be attached when it is too long to paste. Nothing is sent anywhere.
    public static class BugReport
    {
        private const string Tag = "[M3AT] ";
        private const int MostLogLines = 200;   // the newest of this mod's messages kept; the game's log keeps 1000 of all
        private const int MostTraceLines = 25;  // of each message's stack trace
        private const int MostPawns = 5;   // selected pawns described

        // Mods whose presence changes what this mod does, by packageId, and what each is called in the report.
        private static readonly string[] Related =
        {
            "Peren.IsoRim17", "Nals.FacialAnimation", "Nals.FacialAnimationExperimentals", "CETeam.CombatExtended",
            "brrainz.harmony", "local.animalcuddling",
        };

        public static string FilePath => Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "bug_report.txt");

        // Builds the report, copies it and writes it. Returns where it was written, or null if writing failed.
        public static string CopyAndSave()
        {
            string report = Build();
            GUIUtility.systemCopyBuffer = report;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, report);
                return FilePath;
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "the bug report was copied but could not be written to " + FilePath + ": " + e.Message);
                return null;
            }
        }

        public static string Build()
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("## M3AT bug report");
            s.AppendLine();
            s.AppendLine("**What happened, and what were the pawns doing?** (write here, and attach a screenshot if you can)");
            s.AppendLine();
            s.AppendLine("```");
            Section(s, "Build", Basics);
            Section(s, "Related mods", RelatedMods);
            Section(s, "Selected pawns", SelectedPawns);
            Section(s, "Options", Options);
            Section(s, "Log (this mod)", LogLines);
            Section(s, "Load order", LoadOrder);
            s.AppendLine("```");
            return Tidy(s.ToString());
        }

        // Each section on its own, so one that throws (a game update moving something) still leaves the rest.
        private static void Section(StringBuilder s, string title, Action<StringBuilder> write)
        {
            s.AppendLine("--- " + title + " ---");
            try
            {
                write(s);
            }
            catch (Exception e)
            {
                s.AppendLine("(could not be read: " + e.GetType().Name + ": " + e.Message + ")");
            }
            s.AppendLine();
        }

        // The pawns selected when the report is made, at most MostPawns, so a player can pick the one that looks wrong
        // before opening the options. Corpses count as their pawn.
        private static void SelectedPawns(StringBuilder s)
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Selector == null)
            {
                s.AppendLine("(not in a game)");
                return;
            }
            List<Pawn> pawns = Find.Selector.SelectedObjects
                .Select(o => o is Corpse c ? c.InnerPawn : o as Pawn)
                .Where(p => p != null).Distinct().ToList();
            if (pawns.Count == 0)
            {
                s.AppendLine("(none: select the pawn that looks wrong before making the report)");
                return;
            }
            foreach (Pawn pawn in pawns.Take(MostPawns))
            {
                WalkFigures.PawnReport(pawn, s);
            }
            if (pawns.Count > MostPawns)
            {
                s.AppendLine("(" + (pawns.Count - MostPawns) + " more selected, left out)");
            }
        }

        private static void Basics(StringBuilder s)
        {
            Assembly own = typeof(BugReport).Assembly;
            string built = "?";
            if (!string.IsNullOrEmpty(own.Location) && File.Exists(own.Location))
            {
                built = File.GetLastWriteTimeUtc(own.Location).ToString("yyyy-MM-dd HH:mm") + " UTC";
            }
            s.AppendLine("M3AT dll built: " + built);
            s.AppendLine("RimWorld: " + RimWorld.VersionControl.CurrentVersionStringWithRev);
            s.AppendLine("IsoRim Api: " + IsoRimApi());
            s.AppendLine("Figures: " + WalkFigures.StartState);
            s.AppendLine("GPU skinning in use: " + SkinnedFigure.GpuSkinning);
            s.AppendLine("OS: " + SystemInfo.operatingSystem);
            s.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + ", " + SystemInfo.graphicsMemorySize + " MB");
            s.AppendLine("CPU: " + SystemInfo.processorType + ", " + SystemInfo.processorCount + " threads, " + SystemInfo.systemMemorySize + " MB");
            s.AppendLine("In a game: " + (Current.ProgramState == ProgramState.Playing));
        }

        // Read by reflection, so an IsoRim too old to have the Api this mod compiled against still gives an answer.
        private static string IsoRimApi()
        {
            Type api = GenTypes.GetTypeInAnyAssembly("IsoRim.Api");
            PropertyInfo version = api?.GetProperty("Version", BindingFlags.Public | BindingFlags.Static);
            return version == null ? "not found" : version.GetValue(null).ToString();
        }

        private static void RelatedMods(StringBuilder s)
        {
            foreach (string id in Related)
            {
                ModMetaData meta = ModLister.GetActiveModWithIdentifier(id, true);
                s.AppendLine(id + ": " + (meta == null ? "not active" : "active" + SourceOf(meta)));
            }
        }

        private static string SourceOf(ModMetaData meta)
        {
            return meta.Source == ContentSource.SteamWorkshop ? " (Workshop)" : meta.Source == ContentSource.OfficialModsFolder ? " (official)" : " (local)";
        }

        // Every option by its saved name, so a new option needs nothing added here.
        private static void Options(StringBuilder s)
        {
            Settings settings = SettingsMod.Settings;
            if (settings == null)
            {
                s.AppendLine("(not loaded)");
                return;
            }
            foreach (FieldInfo field in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = field.GetValue(settings);
                if (value is IDictionary dictionary)
                {
                    List<string> pairs = new List<string>();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        pairs.Add(entry.Key + "=" + entry.Value);
                    }
                    s.AppendLine(field.Name + ": {" + string.Join(", ", pairs) + "}");
                }
                else
                {
                    s.AppendLine(field.Name + ": " + value);
                }
            }
        }

        // This mod's own messages (its Tag) and any error whose stack trace runs through it, the newest MostLogLines,
        // each trace cut to MostTraceLines. Repeats the game folded together keep their count.
        private static void LogLines(StringBuilder s)
        {
            List<LogMessage> ours = Log.Messages.Where(Ours).ToList();
            if (ours.Count == 0)
            {
                s.AppendLine("(none)");
                return;
            }
            if (ours.Count > MostLogLines)
            {
                s.AppendLine("(" + (ours.Count - MostLogLines) + " older messages left out)");
                ours = ours.Skip(ours.Count - MostLogLines).ToList();
            }
            foreach (LogMessage m in ours)
            {
                s.AppendLine("[" + m.type + (m.repeats > 1 ? " x" + m.repeats : "") + "] " + m.text);
                if (m.type != LogMessageType.Message && !string.IsNullOrEmpty(m.StackTrace))
                {
                    string[] trace = m.StackTrace.Split('\n');
                    foreach (string line in trace.Take(MostTraceLines))
                    {
                        if (line.Trim().Length > 0)
                        {
                            s.AppendLine("    " + line.TrimEnd());
                        }
                    }
                    if (trace.Length > MostTraceLines)
                    {
                        s.AppendLine("    ...");
                    }
                }
            }
        }

        private static bool Ours(LogMessage m)
        {
            if (m.text != null && m.text.Contains("M3AT"))
            {
                return true;
            }
            if (m.type == LogMessageType.Message)
            {
                return false;
            }
            string trace = m.StackTrace;
            return trace != null && trace.Contains("M3AT.");
        }

        private static void LoadOrder(StringBuilder s)
        {
            int i = 0;
            foreach (ModMetaData meta in ModsConfig.ActiveModsInLoadOrder)
            {
                s.AppendLine(i++ + ". " + meta.PackageIdPlayerFacing + " - " + meta.Name + SourceOf(meta));
            }
        }

        // The player's home folder taken out of paths in messages and traces, as it often has their name in it.
        private static string Tidy(string report)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && home.Length > 3)
            {
                report = report.Replace(home, "~");
            }
            return report;
        }
    }
}
