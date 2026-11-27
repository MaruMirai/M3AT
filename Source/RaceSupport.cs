using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The content switcher's Races and mods (ContentView; "Races and mods without a pack" until 2026-11-26): every
    // humanlike race loaded, content pack or not (a race a pack covers says so in its tooltip), then every other mod M3AT has tuned matches for (its genes drawn as M3AT's own parts, TunedGenes.cs, or
    // its apparel named in garments' stands_for lines: Cane Aternitigno, GrimWorld), with what M3AT itself does for it, so a player can see how well a race is supported before looking for a
    // pack. Nothing here is a choice: a row a race, a column a thing M3AT draws for it (how the race is drawn at all, its
    // ears, tails, horns and wings, its face, its mod's clothing), each a short note in the switcher's colours, the whole
    // in its tooltip, then the race mod's rating from the shipped Docs/Compatibility.csv (by its Workshop id, else its
    // name), or Untested with the any-race row's notes.
    internal static class RaceSupport
    {
        internal sealed class Note
        {
            public string text, tip;
            public Color colour;
        }

        internal sealed class Row
        {
            public ThingDef race;            // null for a mod's row (a mod adding no race)
            public ModContentPack modPack;
            public string label, mod;
            public bool drawn;   // drawn as a chibi; the other notes are dimmed when not
            public Note drawnAs, parts, face, clothes;
            public List<string> packs = new List<string>();   // the content packs covering the race, named in its tooltip
            public Note status;   // not a column: how well it is supported, from the compatibility list, in the name's tooltip

            public IEnumerable<Note> Cells
            {
                get
                {
                    yield return drawnAs;
                    yield return parts;
                    yield return face;
                    yield return clothes;
                }
            }
        }

        private static readonly string[] Heads = { "M3AT_RaceColDrawn", "M3AT_RaceColParts", "M3AT_RaceColFace", "M3AT_RaceColClothes" };

        private static List<Row> rows;
        private static int rowsVersion = -1;
        private static bool rowsOtherRaces, rowsRaceParts, rowsRaceModels, rowsFigure;
        private static int rowsPaper = -1;
        private static float[] cellWidths;

        // The rows, worked out again when the packs or an option they show change.
        internal static List<Row> Rows()
        {
            IsoRim.IsoRimSettings iso = IsoRim.IsoRimMod.Settings;
            int paper = iso != null && iso.keepPaper != null ? iso.keepPaper.Count : 0;
            if (rows == null || rowsVersion != AddOns.Version || rowsOtherRaces != WalkFigures.OtherRaces || rowsRaceParts != WalkFigures.RacePartsOn
                || rowsRaceModels != WalkFigures.RaceModelsOn || rowsPaper != paper || rowsFigure != (WalkFigures.SkinForAddOns != null))
            {
                // The clothing notes and the mods with tuned apparel need the figure's garments, loaded at the first
                // draw: the bottom-bar button asks for the rows before that, and every race's clothing stayed a dash.
                rowsFigure = WalkFigures.SkinForAddOns != null;
                rowsVersion = AddOns.Version;
                rowsOtherRaces = WalkFigures.OtherRaces;
                rowsRaceParts = WalkFigures.RacePartsOn;
                rowsRaceModels = WalkFigures.RaceModelsOn;
                rowsPaper = paper;
                try
                {
                    rows = Build(iso);
                }
                catch (Exception e)
                {
                    Log.Warning(WalkFigures.Tag + "couldn't list the races without a content pack: " + e.Message);
                    rows = new List<Row>();
                }
                cellWidths = null;
            }
            return rows;
        }

        private static List<Row> Build(IsoRim.IsoRimSettings iso)
        {
            List<AddOns.AddOn> packs = ContentSources.Packs();
            Dictionary<string, IsoRim.AlienModelDef> alienPacks = new Dictionary<string, IsoRim.AlienModelDef>();
            foreach (IsoRim.AlienModelDef d in DefDatabase<IsoRim.AlienModelDef>.AllDefsListForReading)
            {
                foreach (string r in (d.aliens ?? new List<string>()).Concat(d.alien.NullOrEmpty() ? Enumerable.Empty<string>() : new[] { d.alien }))
                {
                    alienPacks[r] = d;
                }
            }
            // Races some pawn kind spawns. A race none does is one a player doesn't meet as such: Milira's humanlike
            // Milian_Race has only an abstract kind, and the mod makes one only when a Milian mech is humanized (its
            // JobDriver_HumanizeMilian retargets a kind at runtime), so listed it read as if the Milian mechs everyone
            // meets were drawn 3D. Mechs aren't listed: IsoRim offers only humanlike people to the chibi.
            HashSet<ThingDef> spawned = new HashSet<ThingDef>(DefDatabase<PawnKindDef>.AllDefsListForReading.Where(k => k.race != null).Select(k => k.race));
            List<Row> found = new List<Row>();
            foreach (ThingDef race in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (race.race == null || !race.race.Humanlike || race.defName == "Human" || race.defName == "CreepJoiner"
                    || !spawned.Contains(race)
                    || (race.thingClass != null && typeof(Corpse).IsAssignableFrom(race.thingClass)))   // corpses carry their race's race props
                {
                    continue;
                }
                Row row = new Row { race = race, modPack = race.modContentPack, label = race.LabelCap.ToString(), mod = race.modContentPack != null ? race.modContentPack.Name : "?" };
                alienPacks.TryGetValue(race.defName, out IsoRim.AlienModelDef pack);
                row.drawnAs = DrawnAs(race, pack, iso, out row.drawn);
                row.parts = Parts(race);
                row.face = Face(race);
                row.clothes = Clothes(race.modContentPack);
                row.status = Status(race.modContentPack, true);
                // A race a content pack covers is listed all the same (until 2026-11-26 it was left out, and its mod
                // came back as a mod row saying it adds no race): the notes are what M3AT itself does for it, which is
                // what is drawn for each part set to M3AT's in the pack's row above.
                row.packs = packs.Where(a => a.races.Contains(race.defName)).Select(a => a.name).ToList();
                found.Add(row);
            }
            found.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
            found.AddRange(ModRows(found));
            return found;
        }

        // A row for each mod with tuned matches (its genes drawn as M3AT's parts, or apparel M3AT's garments were picked
        // for by hand) and no race row of its own, after the races. Content packs themselves are left out: they have the
        // packs' table above.
        private static List<Row> ModRows(List<Row> races)
        {
            HashSet<ModContentPack> mods = new HashSet<ModContentPack>(WalkFigures.ModsWithTunedGenes());
            SkinnedFigure figure = WalkFigures.SkinForAddOns;
            if (figure != null)
            {
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (d.IsApparel && d.modContentPack != null && figure.GarmentOf(d) <= 0 && figure.StandsFor(d).Length > 0)
                    {
                        mods.Add(d.modContentPack);
                    }
                }
            }
            HashSet<string> packIds = new HashSet<string>(ContentSources.Packs().Select(a => a.packageId), StringComparer.OrdinalIgnoreCase);
            List<Row> found = new List<Row>();
            foreach (ModContentPack mod in mods)
            {
                if (mod.IsCoreMod || mod.IsOfficialMod || packIds.Contains(mod.PackageId) || races.Any(r => r.modPack == mod))
                {
                    continue;
                }
                bool people = WalkFigures.TunedGenesOf(mod).Count > 0;   // xenotypes of humans: drawn as the chibi, with human faces
                Row row = new Row { modPack = mod, label = mod.Name, mod = mod.Name, drawn = true };
                row.drawnAs = people ? Make("M3AT_RaceChibi", ContentView.StrongGreen, "M3AT_ModDrawnTip".Translate())
                    : Make("M3AT_NoteHeadwearFit", Color.white, "M3AT_ModNoRaceTip".Translate());
                row.parts = TunedParts(mod);
                row.face = people ? Make("M3AT_NoteFace", ContentView.YellowGreen, "M3AT_ModFaceTip".Translate())
                    : Make("M3AT_NoteHeadwearFit", Color.white, "M3AT_ModNoRaceTip".Translate());
                row.clothes = Clothes(mod);
                row.status = Status(mod, false);
                found.Add(row);
            }
            found.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
            return found;
        }

        // A mod's genes M3AT draws as its own parts, chosen by hand (TunedGenes.cs): Mapped, each listed in the tip.
        private static Note TunedParts(ModContentPack mod)
        {
            List<KeyValuePair<GeneDef, string>> genes = WalkFigures.TunedGenesOf(mod);
            if (genes.Count == 0)
            {
                return Make("M3AT_NoteHeadwearFit", Color.white, "M3AT_ModPartsNoneTip".Translate());
            }
            string list = string.Join("\n", genes.Select(kv =>
            {
                GeneDef as_ = DefDatabase<GeneDef>.GetNamedSilentFail(kv.Value);
                return "  " + kv.Key.LabelCap + " → " + (as_ != null ? as_.label : kv.Value);
            }));
            return Make("M3AT_NoteTunedParts", ContentView.YellowGreen, "M3AT_HintTunedParts".Translate(list));
        }

        private static Note Make(string key, Color colour, string tip)
        {
            return new Note { text = key.Translate(), colour = colour, tip = tip };
        }

        // As IsoRim decides it: a card when on its Keep as paper list; an IsoRim alien pack's own way where one names the
        // race (its own models, a card, or IsoRim's person figure, which the chibi draws); else by M3AT's Other races option.
        private static Note DrawnAs(ThingDef race, IsoRim.AlienModelDef pack, IsoRim.IsoRimSettings iso, out bool drawn)
        {
            drawn = false;
            if (iso != null && iso.keepPaper != null && iso.keepPaper.Contains(race.defName))
            {
                return Make("M3AT_RaceCard", Color.gray, "M3AT_RaceCardPaperTip".Translate());
            }
            if (pack != null)
            {
                string packName = pack.modContentPack != null ? pack.modContentPack.Name : pack.defName;
                // IsoRim offers our drawer every race it draws in 3D, its pack's own models (Own) as well as its person
                // figure (Human), and M3AT draws both as its figure (DrawFigure declines only Card), so only a pack that
                // keeps the race a card stops it.
                if (pack.figure == IsoRim.FigureMode.Card)
                {
                    return Make("M3AT_RaceCard", Color.gray, "M3AT_RaceCardIsoPackTip".Translate(packName));
                }
                drawn = true;
                return Make("M3AT_RaceChibi", ContentView.StrongGreen, "M3AT_RaceChibiIsoPackTip".Translate(packName));
            }
            if (!WalkFigures.OtherRaces)
            {
                return Make("M3AT_RaceCard", Color.gray, "M3AT_RaceCardOptionTip".Translate());
            }
            drawn = true;
            return Make("M3AT_RaceChibi", ContentView.StrongGreen, "M3AT_RaceChibiTip".Translate());
        }

        // Its ears, tails, horns and wings: M3AT's own models of them (RaceModels, or the chibi's cat ears and tail for a
        // cat-like race), else built from the race's add-on pictures (AddonParts.cs) where it has any, else nothing to draw.
        private static Note Parts(ThingDef race)
        {
            if (WalkFigures.HasOwnRaceModel(race.defName) || HairBuilder.CatLikeAddon(race, "ear") || HairBuilder.CatLikeAddon(race, "tail"))
            {
                return Make("M3AT_NoteModelEars", ContentView.StrongGreen, "M3AT_HintModelEars".Translate());
            }
            bool any = new[] { "ear", "tail", "horn", "wing" }.Any(w => HairBuilder.RaceHasAddon(race, w));
            if (!any)
            {
                return Make("M3AT_NoteHeadwearFit", Color.white, "M3AT_RacePartsNoneTip".Translate());
            }
            if (!WalkFigures.RacePartsOn)
            {
                return Make("M3AT_RaceOff", Color.gray, "M3AT_RacePartsOffTip".Translate());
            }
            return Make("M3AT_NoteBuiltEars", ContentView.Orange, "M3AT_HintBuiltEars".Translate());
        }

        private static Note Face(ThingDef race)
        {
            return ContentSources.FacialAnimationHas(race.defName)
                ? Make("M3AT_NoteFace", ContentView.YellowGreen, "M3AT_HintFace".Translate())
                : Make("M3AT_NoteNoFace", Costs.Red, "M3AT_HintNoFace".Translate());
        }

        // The mod's own apparel: Custom models where M3AT has a garment for every piece, else Automatched (the closest of
        // M3AT's garments, painted with the apparel's art), Automatched (tuned) where any of those had its garments picked
        // by hand (a stands_for line), a dash where the mod adds none.
        private static Note Clothes(ModContentPack mod)
        {
            SkinnedFigure figure = WalkFigures.SkinForAddOns;
            if (figure == null || mod == null)
            {
                return Make("M3AT_NoteHeadwearFit", Color.white, "M3AT_RaceClothesNoneTip".Translate());
            }
            List<ThingDef> apparel = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsApparel && d.modContentPack == mod).ToList();
            if (apparel.Count == 0)
            {
                return Make("M3AT_NoteHeadwearFit", Color.white, "M3AT_RaceClothesNoneTip".Translate());
            }
            int own = apparel.Count(d => figure.GarmentOf(d) > 0);
            if (own == apparel.Count)
            {
                return Make("M3AT_NoteModelEars", ContentView.StrongGreen, "M3AT_RaceClothesOwnTip".Translate(own));
            }
            int tuned = apparel.Count(d => figure.GarmentOf(d) <= 0 && figure.StandsFor(d).Length > 0);
            if (tuned > 0)
            {
                return Make("M3AT_NoteTunedClothes", ContentView.TunedAmber, "M3AT_RaceClothesTunedTip".Translate(apparel.Count - own, apparel.Count, tuned));
            }
            return Make("M3AT_NoteMatchedClothes", Costs.Amber, "M3AT_RaceClothesMatchedTip".Translate(apparel.Count - own, apparel.Count));
        }

        // ---- the compatibility list

        private sealed class Listed
        {
            public string mod, status, works, gaps, needs, id;
            public bool race;   // a Races row
        }

        private static List<Listed> listed;

        // Docs/Compatibility.csv's rows as shipped, read once (a race is looked for among the Races rows only).
        private static List<Listed> List()
        {
            if (listed != null)
            {
                return listed;
            }
            listed = new List<Listed>();
            ModContentPack own = LoadedModManager.RunningModsListForReading.FirstOrDefault(m => string.Equals(m.PackageId, "marumirai.M3AT", StringComparison.OrdinalIgnoreCase));
            string path = own != null ? Path.Combine(own.RootDir, "Docs", "Compatibility.csv") : null;
            if (path == null || !File.Exists(path))
            {
                return listed;
            }
            try
            {
                List<List<string>> table = Csv(File.ReadAllText(path));
                if (table.Count == 0)
                {
                    return listed;
                }
                List<string> head = table[0];
                int Col(string name) => head.FindIndex(h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));
                int mod = Col("Mod"), category = Col("Category"), status = Col("Status"), works = Col("What works"), gaps = Col("Known gaps"), needs = Col("Also needs"), shop = Col("Workshop");
                string At(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
                foreach (List<string> r in table.Skip(1))
                {
                    string url = At(r, shop);
                    int idAt = url.LastIndexOf("id=", StringComparison.Ordinal);
                    listed.Add(new Listed
                    {
                        mod = At(r, mod), status = At(r, status), race = string.Equals(At(r, category), "Races", StringComparison.OrdinalIgnoreCase), works = At(r, works), gaps = At(r, gaps), needs = At(r, needs),
                        id = idAt >= 0 ? url.Substring(idAt + 3).Trim() : "",
                    });
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Log.Warning(WalkFigures.Tag + "couldn't read Docs/Compatibility.csv: " + e.Message);
            }
            return listed;
        }

        // Quoted fields may hold commas, doubled quotes and line breaks.
        private static List<List<string>> Csv(string text)
        {
            List<List<string>> table = new List<List<string>>();
            List<string> row = new List<string>();
            System.Text.StringBuilder field = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    table.Add(row);
                    row = new List<string>();
                }
                else if (c != '\r')
                {
                    field.Append(c);
                }
            }
            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                table.Add(row);
            }
            return table;
        }

        // The mod's row (a race's among the Races rows): by the Workshop id its folder is named for, else by its name in
        // the row's (the row's name with any bracket taken off, "NewRatkinPlus (Ratkin)"). Else Untested, a race's with
        // the any-race row's notes.
        private static Note Status(ModContentPack mod, bool race)
        {
            List<Listed> all = List().Where(l => l.race || !race).ToList();
            Listed found = null;
            if (mod != null)
            {
                string folder = Path.GetFileName(mod.RootDir.TrimEnd('/', '\\'));
                found = all.FirstOrDefault(l => l.id.Length > 0 && l.id == folder);
                if (found == null)
                {
                    found = all.FirstOrDefault(l => string.Equals(NameOf(l.mod), mod.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(l.mod, mod.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                }
            }
            if (found == null && !race)
            {
                return Make("M3AT_RaceUntested", Color.gray, "M3AT_ModUntestedTip".Translate());
            }
            if (found == null)
            {
                Listed any = all.FirstOrDefault(l => l.mod.IndexOf("any race", StringComparison.OrdinalIgnoreCase) >= 0);
                string tip = "M3AT_RaceUntestedTip".Translate();
                if (any != null)
                {
                    tip += "\n\n" + Details(any);
                }
                return Make("M3AT_RaceUntested", Color.gray, tip);
            }
            Color colour = found.status.StartsWith("Full", StringComparison.OrdinalIgnoreCase) ? ContentView.StrongGreen
                : found.status.StartsWith("Partial", StringComparison.OrdinalIgnoreCase) ? Costs.Amber : Color.gray;
            // The list's own words (Full, Partial (early), Untested) translated where there is a key for them.
            string key = "M3AT_RaceStatus_" + new string(found.status.Where(char.IsLetter).ToArray());
            string text = key.CanTranslate() ? (string)key.Translate() : found.status;
            return new Note { text = text, colour = colour, tip = "M3AT_RaceListedTip".Translate(found.mod) + "\n\n" + Details(found) };
        }

        private static string NameOf(string listedMod)
        {
            int bracket = listedMod.IndexOf(" (", StringComparison.Ordinal);
            return (bracket > 0 ? listedMod.Substring(0, bracket) : listedMod).Trim();
        }

        private static string Details(Listed l)
        {
            List<string> lines = new List<string>();
            if (l.works.Length > 0)
            {
                lines.Add("M3AT_RaceWorks".Translate(l.works));
            }
            if (l.gaps.Length > 0)
            {
                lines.Add("M3AT_RaceGaps".Translate(l.gaps));
            }
            if (l.needs.Length > 0)
            {
                lines.Add("M3AT_RaceNeeds".Translate(l.needs));
            }
            return string.Join("\n", lines);
        }

        // ---- drawing

        private const float RowHeight = 26f;

        private static void Measure(List<Row> all)
        {
            if (cellWidths != null)
            {
                return;
            }
            GameFont font = Text.Font;
            cellWidths = new float[Heads.Length];
            for (int i = 0; i < Heads.Length; i++)
            {
                Text.Font = GameFont.Tiny;
                cellWidths[i] = Text.CalcSize(Heads[i].Translate()).x;
            }
            Text.Font = GameFont.Small;
            foreach (Row r in all)
            {
                int i = 0;
                foreach (Note n in r.Cells)
                {
                    cellWidths[i] = Mathf.Max(cellWidths[i], Text.CalcSize(n.text).x);
                    i++;
                }
            }
            for (int i = 0; i < cellWidths.Length; i++)
            {
                cellWidths[i] = Mathf.Ceil(cellWidths[i]) + 16f;
            }
            Text.Font = font;
        }

        // The races' names take what the notes leave of the packs' table's width (ContentView.PackTableWidth), so the two
        // tables line up, a long name cut short (in full in its tooltip); never narrower than this.
        private const float MinNameWidth = 110f;

        // The width the table needs, for the bottom-bar window (ContentView.WideViewWidth): the notes and the narrowest
        // names; 0 when there are no rows.
        internal static float TableWidth
        {
            get
            {
                if (WalkFigures.SkinForAddOns == null)
                {
                    return 0f;
                }
                List<Row> all = Rows();
                if (all.Count == 0)
                {
                    return 0f;
                }
                Measure(all);
                return MinNameWidth + cellWidths.Sum();
            }
        }

        // The section: every race (pawn null), or the pawn's own race's row if it is one. Columns where wide enough,
        // else each race's notes as lines.
        internal static void Draw(Listing_Standard list, Pawn pawn, bool wide)
        {
            List<Row> all = Rows();
            if (pawn != null)
            {
                all = all.Where(r => r.race != null ? r.race == pawn.def : PawnFrom(pawn, r.modPack)).ToList();
            }
            if (all.Count == 0)
            {
                return;
            }
            Text.Font = GameFont.Medium;
            list.Label("M3AT_RacesTitle".Translate());
            Text.Font = GameFont.Small;
            list.Gap(4f);
            Measure(Rows());
            if (wide && list.ColumnWidth >= MinNameWidth + cellWidths.Sum())
            {
                float width = Mathf.Min(list.ColumnWidth, Mathf.Max(ContentView.PackTableWidth, MinNameWidth + cellWidths.Sum()));
                Columns(list, all, width - cellWidths.Sum());
            }
            else
            {
                Lines(list, all);
            }
            list.GapLine(10f);
        }

        // The name in full (cut short in the column), where it is from, and how well its mod is supported.
        private static string RowTip(Row r)
        {
            string from = r.race != null ? r.label + "\n" + "M3AT_RaceRowTip".Translate(r.race.defName, r.mod) : (string)"M3AT_ModRowTip".Translate(r.mod);
            if (r.packs.Count > 0)
            {
                from += "\n\n" + "M3AT_RacePackedTip".Translate(string.Join(", ", r.packs));
            }
            return from + "\n\n" + "M3AT_RaceColStatus".Translate() + ": " + r.status.text + "\n" + r.status.tip;
        }

        // Whether the pawn has a gene of the mod's M3AT draws, or wears its apparel: its row is shown with the pawn's.
        private static bool PawnFrom(Pawn pawn, ModContentPack mod)
        {
            if (pawn.genes != null && pawn.genes.GenesListForReading.Any(g => g.Active && g.def.modContentPack == mod))
            {
                return true;
            }
            return pawn.apparel != null && pawn.apparel.WornApparel.Any(a => a.def.modContentPack == mod);
        }

        private static void Columns(Listing_Standard list, List<Row> all, float raceWidth)
        {
            Rect head = list.GetRect(Text.LineHeightOf(GameFont.Tiny) + 2f);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Text.Anchor = TextAnchor.LowerLeft;
            Widgets.Label(new Rect(head.x, head.y, raceWidth, head.height), "M3AT_RaceColRace".Translate());
            float x = head.x + raceWidth;
            for (int i = 0; i < Heads.Length; i++)
            {
                Rect h = new Rect(x + 4f, head.y, cellWidths[i] - 4f, head.height);
                Widgets.Label(h, Heads[i].Translate());
                TooltipHandler.TipRegion(h, (Heads[i] + "Tip").Translate());
                x += cellWidths[i];
            }
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Color grid = new Color(1f, 1f, 1f, 0.1f);
            foreach (Row r in all)
            {
                Rect row = list.GetRect(RowHeight);
                Widgets.DrawHighlightIfMouseover(row);
                Text.Anchor = TextAnchor.MiddleLeft;
                Rect name = new Rect(row.x, row.y, raceWidth, row.height);
                Widgets.Label(new Rect(name.x, name.y, name.width - 8f, name.height), r.label.Truncate(name.width - 8f));
                TooltipHandler.TipRegion(name, RowTip(r));
                x = row.x + raceWidth;
                int i = 0;
                foreach (Note n in r.Cells)
                {
                    Rect cell = new Rect(x, row.y, cellWidths[i], row.height);
                    GUI.color = grid;
                    Widgets.DrawLineVertical(cell.x, cell.y, cell.height);
                    // The notes after Drawn as say what a chibi would have, so they are dimmed where the race isn't one.
                    bool dim = !r.drawn && n != r.drawnAs && n != r.status;
                    GUI.color = dim ? Color.Lerp(n.colour, Color.gray, 0.6f) : n.colour;
                    Widgets.Label(new Rect(cell.x + 6f, cell.y, cell.width - 6f, cell.height), n.text);
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(cell, n.tip);
                    x += cellWidths[i];
                    i++;
                }
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = grid;
                Widgets.DrawLineHorizontal(row.x, row.yMax, x - row.x);
                GUI.color = Color.white;
            }
        }

        private static void Lines(Listing_Standard list, List<Row> all)
        {
            foreach (Row r in all)
            {
                list.Label(r.label, -1f, new TipSignal(RowTip(r)));
                Text.Font = GameFont.Tiny;
                int i = 0;
                foreach (Note n in r.Cells)
                {
                    Rect line = list.GetRect(Text.LineHeight);
                    float headWidth = Mathf.Min(140f, line.width / 2f);
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(line.x + 12f, line.y, headWidth - 12f, line.height), Heads[i].Translate());
                    GUI.color = n.colour;
                    Widgets.Label(new Rect(line.x + headWidth, line.y, line.width - headWidth, line.height), n.text);
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(line, n.tip);
                    i++;
                }
                Text.Font = GameFont.Small;
                list.Gap(4f);
            }
        }
    }
}
