using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The content switcher (ContentSources.cs): for each content pack, a row per part it changes with the pack's or M3AT's
    // own to choose. One view, reached three ways so players find it (more matters as more content is made for M3AT): the
    // Content tab of M3AT's options, the M3AT button on the bottom bar in game (ContentTabWindow, shown while packs are
    // active, so a switch shows on the pawns at once), and M3AT look on a pawn (ClothingToolWindow), scoped to that pawn.
    internal sealed class ContentView
    {
        private const float RowHeight = 28f;
        private Vector2 scroll;
        private float viewHeight;

        // Draws every pack (pawn null) or those that change this pawn's race or clothes, in rect. Returns the height used
        // when not scrolled (scrolled: rect's).
        internal float Draw(Rect rect, Pawn pawn, bool scrolled)
        {
            List<AddOns.AddOn> packs = WalkFigures.SkinForAddOns == null ? null : ContentSources.Packs();
            if (pawn != null && packs != null)
            {
                packs = packs.Where(a => Covers(a, pawn)).ToList();
            }
            if (!scrolled)
            {
                return Body(rect, packs, pawn);
            }
            Rect view = new Rect(0f, 0f, rect.width - 16f, Mathf.Max(viewHeight, rect.height));
            Widgets.BeginScrollView(rect, ref scroll, view);
            viewHeight = Body(view, packs, pawn);
            Widgets.EndScrollView();
            return rect.height;
        }

        private static float Body(Rect rect, List<AddOns.AddOn> packs, Pawn pawn)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(new Rect(rect.x, rect.y, rect.width, 100000f));
            if (pawn == null)
            {
                Text.Font = GameFont.Medium;
                Rect title = list.GetRect(Text.LineHeight);
                string heading = "M3AT_ContentTitle".Translate();
                Widgets.Label(title, heading);
                Text.Font = GameFont.Small;
                Rect about = new Rect(title.xMax - 220f, title.y + (title.height - 30f) / 2f, 220f, 30f);
                if (Widgets.ButtonText(about, "M3AT_PolyAbout".Translate()))
                {
                    Costs.ShowAbout();
                }
                TooltipHandler.TipRegion(about, "M3AT_PolyAboutTip".Translate());
                // The bottom-bar button's own toggle is in the options' Advanced section (Settings.AdvancedSection).
                Costs.ColonyLine(list);
                // A copy of the option in the mod options' Mod compatibility section, beside what it changes.
                SettingsMod.GarmentDetailOption(list, besideLabel: true);
                list.GapLine(8f);
            }
            if (pawn != null)
            {
                Costs.PawnLine(list, pawn);
            }
            if (packs == null)
            {
                list.Label("M3AT_ContentNotLoaded".Translate());
            }
            else if (packs.Count == 0)
            {
                BodyChoice(list, packs);
                if (pawn == null && WalkFigures.CopyName == null)
                {
                    list.Label("M3AT_ContentNone".Translate());
                }
            }
            else
            {
                BodyChoice(list, packs);
                foreach (AddOns.AddOn a in packs)
                {
                    Pack(list, a, pawn);
                }
            }
            if (packs != null)
            {
                // Every race and the mods M3AT has tuned matches for, with what M3AT does for each (RaceSupport.cs).
                RaceSupport.Draw(list, pawn, list.ColumnWidth >= WideFrom);
            }
            float used = list.CurHeight;
            list.End();
            return used;
        }

        // A pack's header, its costs with Measure now, a row of the two columns' heads (each
        // sets every part, so a whole pack switches in one click), and a row per part with the pack's and M3AT's side by side, one click each.
        private static void Pack(Listing_Standard list, AddOns.AddOn a, Pawn pawn)
        {
            bool open = opened.Contains(a.packageId);
            TitleRow(list, a, open);
            if (open)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                list.Label("M3AT_ContentSummary".Translate(a.packageId, a.garments, a.races.Count > 0 ? string.Join(", ", a.races.Select(RaceLabel)) : "-"));
                if (a.patches > 0)
                {
                    list.Label("M3AT_ContentPatches".Translate(a.patches));
                }
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Costs.PackLines(list, a, pawn == null);
                if (a.parts.Contains(ContentPart.BodyShape) && MeshBody(a, ContentPart.BodyShape))
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = Color.gray;
                    list.Label("M3AT_BodyChosenAbove".Translate(ContentSources.UsesPack(a.packageId, ContentPart.BodyShape)
                        ? (string)"M3AT_BodyChoicePack".Translate(a.name) : (string)"M3AT_SourceM3AT".Translate()));
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
                list.Gap(4f);
            }
            // A body change for everyone is chosen once for all packs (BodyChoice), not here.
            List<ContentPart> parts = ContentSources.Shown.Where(p => a.parts.Contains(p) && !MeshBody(a, p)).ToList();
            if (parts.Count == 0)
            {
                list.GapLine(10f);
                return;
            }
            Heads(list, a, parts);
            foreach (ContentPart part in parts)
            {
                PartRow(list, a, part);
            }
            list.GapLine(10f);
        }

        // The packs whose details (summary, costs in full, Measure now) are unfolded, this session; folded by default.
        private static readonly HashSet<string> opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The pack's name, its costs in short beside it (Costs.PackShort), and the arrow at the right that folds the
        // details out; the whole row a click.
        private static void TitleRow(Listing_Standard list, AddOns.AddOn a, bool open)
        {
            Text.Font = GameFont.Medium;
            Rect row = list.GetRect(Text.LineHeight + 2f);
            float fullName = Text.CalcSize(a.name).x;
            Text.Font = GameFont.Small;
            // One run beside the name: the costs in short (Costs.PackShort), then " · " and what the pack added to loading
            // M3AT's figures (moved up from the details, so it shows folded). Only the figures carry a colour, the words
            // grey: one number each to read at a glance.
            List<KeyValuePair<string, Color>> run = new List<KeyValuePair<string, Color>>();
            string costs = Costs.PackShort(a, out Color colour, out string tip, out string before, out string figure, out string after);
            if (costs != null)
            {
                run.Add(new KeyValuePair<string, Color>(before, Color.gray));
                run.Add(new KeyValuePair<string, Color>(figure, colour));
                run.Add(new KeyValuePair<string, Color>(after, Color.gray));
            }
            double loadSeconds = SkinnedFigure.LoadSecondsOf(a.packageId);
            string load = null, dot = costs != null ? " · " : "";
            if (loadSeconds >= 0.1)
            {
                string secs = loadSeconds.ToString("0.0");
                load = "M3AT_CostLoadShort".Translate(secs);
                Costs.Split(load, "M3AT_CostLoadShort".Translate(Costs.Mark + secs + Costs.Mark), out string lb, out string lf, out string la);
                run.Add(new KeyValuePair<string, Color>(dot + lb, Color.gray));
                run.Add(new KeyValuePair<string, Color>(lf, Costs.LoadColour(loadSeconds)));
                run.Add(new KeyValuePair<string, Color>(la, Color.gray));
            }
            float runWidth = 0f;
            foreach (KeyValuePair<string, Color> piece in run)
            {
                runWidth += Text.CalcSize(piece.Key).x;
            }
            // "Details" in grey beside the arrow, so the fold is not missed; the run stops short of it. The window is only
            // as wide as the table, so where the full name, the run and Details don't all fit, Details goes first (the
            // arrow is kept), then the name is cut short (in full in its tooltip), down to MinTitleName; only past that is
            // the run cut.
            const float icon = 20f, MinTitleName = 120f;
            string details = "M3AT_ContentDetails".Translate();
            float detailsWidth = Text.CalcSize(details).x;
            if (fullName + 14f + runWidth + 12f > row.width - icon - 8f - detailsWidth)
            {
                details = "";
                detailsWidth = 0f;
            }
            float room = row.width - icon - 8f - detailsWidth - 12f - (run.Count > 0 ? runWidth + 14f : 0f);
            float nameWidth = Mathf.Min(fullName, Mathf.Max(MinTitleName, room));
            Text.Font = GameFont.Medium;
            Rect nameAt = new Rect(row.x, row.y, nameWidth, row.height);
            Widgets.Label(nameAt, a.name.Truncate(nameWidth));
            Text.Font = GameFont.Small;
            if (nameWidth < fullName)
            {
                TooltipHandler.TipRegion(nameAt, a.name);
            }
            float runX = row.x + nameWidth + 14f;
            Rect detailsAt = new Rect(row.xMax - icon - 8f - detailsWidth, row.y, detailsWidth, row.height);
            Rect at = new Rect(runX, row.y, Mathf.Max(0f, detailsAt.x - 12f - runX), row.height);
            if (costs != null)
            {
                TooltipHandler.TipRegion(new Rect(at.x, at.y, Mathf.Min(Text.CalcSize(costs).x, at.width), at.height), tip);
            }
            if (load != null)
            {
                float loadX = at.x + (costs != null ? Text.CalcSize(costs + dot).x : 0f);
                if (loadX < at.xMax)
                {
                    TooltipHandler.TipRegion(new Rect(loadX, at.y, Mathf.Min(Text.CalcSize(load).x, at.xMax - loadX), at.height),
                        "M3AT_CostLoadTip".Translate(SkinnedFigure.LoadTotal.ToString("0.0")));
                }
            }
            if (run.Count > 0)
            {
                Pieces(at, TextAnchor.MiddleLeft, run.ToArray());
            }
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = Color.gray;
            Widgets.Label(detailsAt, details);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.DrawTexture(new Rect(row.xMax - icon - 4f, row.y + (row.height - icon) / 2f, icon, icon), open ? TexButton.Collapse : TexButton.Reveal);
            Widgets.DrawHighlightIfMouseover(row);
            TooltipHandler.TipRegion(new Rect(detailsAt.x, row.y, row.xMax - detailsAt.x, row.height), (open ? "M3AT_ContentFold" : "M3AT_ContentUnfold").Translate());
            if (Widgets.ButtonInvisible(row))
            {
                if (open)
                {
                    opened.Remove(a.packageId);
                }
                else
                {
                    opened.Add(a.packageId);
                }
            }
        }

        private static bool MeshBody(AddOns.AddOn a, ContentPart part)
        {
            return part == ContentPart.BodyShape && AddOns.ChangesBodyMesh(a.packageId);
        }

        // The body every pawn is drawn with: M3AT's, or one pack's that changes it as it is read (ContentSources.OneBody:
        // only one at a time, as they would stack). One radio list over the packs, shown while any pack changes the body.
        private static void BodyChoice(Listing_Standard list, List<AddOns.AddOn> packs)
        {
            List<AddOns.AddOn> bodies = packs.Where(a => MeshBody(a, ContentPart.BodyShape)).ToList();
            string copy = WalkFigures.CopyName;
            if (bodies.Count == 0 && copy == null)
            {
                return;
            }
            AddOns.AddOn on = bodies.FirstOrDefault(a => ContentSources.UsesPack(a.packageId, ContentPart.BodyShape));
            // The note sits to the right of the heading in grey, as the window's own subtitle does (Body).
            Text.Font = GameFont.Medium;
            string heading = "M3AT_BodyChoice".Translate();
            Rect title = list.GetRect(Text.LineHeight);
            Widgets.Label(title, heading);
            TooltipHandler.TipRegion(title, "M3AT_BodyChoiceTip".Translate());
            float noteX = title.x + Text.CalcSize(heading).x + 12f;
            Text.Font = GameFont.Small;
            Rect note = new Rect(noteX, title.y, Mathf.Max(0f, title.xMax - noteX), title.height);
            GUI.color = Color.gray;
            TextAnchor oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.LowerLeft;
            Text.WordWrap = false;
            Widgets.Label(note, "M3AT_BodyChoiceNote".Translate());
            Text.WordWrap = true;
            Text.Anchor = oldAnchor;
            GUI.color = Color.white;
            if (copy != null)
            {
                // Another mod bundles M3AT's code and files: nothing patches us, so no pack is listed, but the body drawn is
                // that mod's, and can't be switched here.
                GUI.color = Costs.Amber;
                list.Label(Costs.CopyLine(copy));
                GUI.color = Color.white;
            }
            if (bodies.Count == 0)
            {
                list.GapLine(10f);
                return;
            }
            Rect ours = list.GetRect(RowHeight);
            if (RadioCell(ours, on == null, "M3AT_SourceM3AT".Translate(), !bodies.Any(b => ContentSources.Default(b.packageId, ContentPart.BodyShape)), "M3AT_BodyChoiceM3ATTip".Translate()) && on != null)
            {
                ContentSources.SetBody(null);
            }
            foreach (AddOns.AddOn a in bodies)
            {
                Rect row = list.GetRect(RowHeight);
                if (RadioCell(row, a == on, "M3AT_BodyChoicePack".Translate(a.name), ContentSources.Default(a.packageId, ContentPart.BodyShape), "M3AT_HintBodyMeshPack".Translate()) && a != on)
                {
                    ContentSources.SetBody(a.packageId);
                }
            }
            if (on != null)
            {
                BodyCost(list);
            }
            else if (WalkFigures.SkinForAddOns != null && WalkFigures.SkinForAddOns.BodyChanged)
            {
                // M3AT's is chosen but a pack's body was read at load: it stays until the restart.
                GUI.color = Costs.Red;
                list.Label("M3AT_BodyPendingRestart".Translate());
                GUI.color = Color.white;
            }
            list.GapLine(10f);
        }

        // While a pack's body is chosen: the warning, under the choice that undoes it. Once that body is loaded, with what
        // it and M3AT's own garments now cost against M3AT's. Chosen but not loaded yet (the body is read once, so it waits
        // for a restart), the counts aren't known, so the warning says it comes in at the restart instead. Once M3AT's is
        // chosen the pack's body stays loaded until a restart, but the warning is about a choice already undone, so it is
        // hidden. The counts go in columns, as RimWorld's font doesn't line up spaces.
        private static void BodyCost(Listing_Standard list)
        {
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            if (f == null || !f.BodyChanged)
            {
                GUI.color = Costs.Red;
                list.Label("M3AT_BodyOverrideAtRestart".Translate());
                list.Label("M3AT_BodyOverrideNoAuto".Translate());
                GUI.color = Color.white;
                return;
            }
            f.OwnGarmentTriangles(out int ownMade, out int ownNow);
            GUI.color = Costs.Red;
            list.Label("M3AT_BodyOverride".Translate());
            string[] names = { "M3AT_BodyOverrideBody".Translate(), "M3AT_BodyOverrideGarments".Translate() };
            string[] nows = { Costs.N(f.TrianglesOf(0)), Costs.N(ownNow) };
            string[] ours = { Costs.N(f.BodyFileTriangles), Costs.N(ownMade) };
            float nameWidth = names.Max(s => Text.CalcSize(s).x) + 16f;
            float nowWidth = nows.Max(s => Text.CalcSize(s).x) + 16f;
            for (int i = 0; i < 2; i++)
            {
                Rect row = list.GetRect(Text.LineHeight);
                Widgets.Label(new Rect(row.x + 12f, row.y, nameWidth, row.height), names[i]);
                Widgets.Label(new Rect(row.x + 12f + nameWidth, row.y, nowWidth, row.height), nows[i]);
                Widgets.Label(new Rect(row.x + 12f + nameWidth + nowWidth, row.y, row.width - 12f - nameWidth - nowWidth, row.height),
                    "M3AT_BodyOverrideOurs".Translate(ours[i]));
            }
            list.Label("M3AT_BodyOverrideNoAuto".Translate());
            GUI.color = Color.white;
        }

        // The width of each source column, the pack's then M3AT's, at a row's right.
        private static float CellWidth(Rect row)
        {
            return Mathf.Clamp(row.width * 0.2f, 84f, 190f);
        }

        // A body change takes a restart (the body is read once), so the swap leaves it, as it can't show at once.
        private static bool Live(AddOns.AddOn a, ContentPart part)
        {
            return part != ContentPart.BodyShape || !AddOns.ChangesBodyMesh(a.packageId);
        }

        // The mix of choices the swap put aside to show M3AT's, per pack, for it to bring back (for this session).
        private static readonly Dictionary<string, Dictionary<ContentPart, bool?>> held =
            new Dictionary<string, Dictionary<ContentPart, bool?>>(StringComparer.OrdinalIgnoreCase);

        // The parts the swap can flip: those shown that switch at once.
        private static List<ContentPart> Swappable(AddOns.AddOn a)
        {
            return ContentSources.Shown.Where(p => a.parts.Contains(p) && Live(a, p)).ToList();
        }

        // Two rows over the parts: the columns' names, then "Every part", a row like the parts' whose radios are on
        // when every part is that side's (neither while mixed) and set them all.
        private static void Heads(Listing_Standard list, AddOns.AddOn a, List<ContentPart> parts)
        {
            if (list.ColumnWidth >= WideFrom)
            {
                WideHeads(list, a, parts);
                return;
            }
            Rect names = list.GetRect(RowHeight);
            float cell = CellWidth(names);
            Text.Anchor = TextAnchor.LowerLeft;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(names.xMax - cell * 2f + 4f, names.y, cell - 6f, names.height), "M3AT_SourcePack".Translate());
            Widgets.Label(new Rect(names.xMax - cell + 4f, names.y, cell - 6f, names.height), "M3AT_SourceM3AT".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            Rect row = list.GetRect(RowHeight);
            Rect label = new Rect(row.x, row.y, row.width - cell * 2f - 4f, row.height);
            Widgets.DrawHighlightIfMouseover(label);
            TooltipHandler.TipRegion(label, "M3AT_ContentEveryPartTip".Translate());
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(label.x + 4f, label.y, label.width - 8f, label.height), "M3AT_ContentEveryPart".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            bool allPack = parts.All(p => ContentSources.UsesPack(a.packageId, p));
            bool allOurs = parts.All(p => !ContentSources.UsesPack(a.packageId, p));
            if (RadioCell(new Rect(row.xMax - cell * 2f, row.y, cell - 2f, row.height), allPack, null, false, "M3AT_ContentAllPackTip".Translate()) && !allPack)
            {
                held.Remove(a.packageId);
                ContentSources.SetMany(a.packageId, parts.Select(p => new KeyValuePair<ContentPart, bool?>(p, true)));
            }
            if (RadioCell(new Rect(row.xMax - cell, row.y, cell - 2f, row.height), allOurs, null, false, "M3AT_ContentAllM3ATTip".Translate()) && !allOurs)
            {
                held.Remove(a.packageId);
                ContentSources.SetMany(a.packageId, parts.Select(p => new KeyValuePair<ContentPart, bool?>(p, false)));
            }
            Widgets.DrawLineHorizontal(row.x, row.yMax, row.width, new Color(1f, 1f, 1f, 0.15f));
        }

        // A view at least this wide (the options' Content tab, the bottom-bar window) gets the columns, mirrored about the
        // two radios so each side's figures sit beside its own radio: the part, the pack's polys (as made, and "→ as drawn"
        // where M3AT cut them), the pack's radio | M3AT's radio, M3AT's polys for the same part, and M3AT's note. The chosen
        // side is shaded as one block, figures and radio together. Narrower (M3AT look on a pawn, 420) keeps a cell per
        // side with the counts in tooltips.
        private const float WideFrom = 600f;
        private const float RadioWidth = 58f, NoteWidth = 170f;
        private static readonly Color Divider = new Color(1f, 1f, 1f, 0.25f);

        // The count columns are as wide as their header (Tiny font) or their widest figures (Small: the pack's
        // "9,999 → 9,999"), never under 64 px. The parts' names are as wide as the widest (Clothing with its Items
        // button), so the columns follow straight on from the names rather than sitting at the far edge, where a row
        // was hard to follow across. Measured once: the language doesn't change without a restart.
        private static float countWidth, packCountWidth, labelWidth;

        // The packs' table's width: the parts' names, the counts, the two radios and the note.
        internal static float PackTableWidth
        {
            get
            {
                Measure();
                return labelWidth + 8f + PackCountWidth + RadioWidth * 2f + 2f + CountWidth + NoteWidth;
            }
        }

        // The width a view needs to show the columns (WideFrom at least): the packs' table, or the races' where its
        // notes need more (RaceSupport.TableWidth fits itself to the packs' table, cutting the races' names first).
        // A pack's title row that is wider drops its "Details" word, then cuts its costs, rather than widening the view.
        internal static float WideViewWidth => Mathf.Max(WideFrom, Mathf.Max(PackTableWidth, RaceSupport.TableWidth));
        private const float ItemsWidth = 110f;
        private static readonly Color GridLine = new Color(1f, 1f, 1f, 0.1f);

        private static float CountWidth
        {
            get
            {
                Measure();
                return countWidth;
            }
        }

        private static float PackCountWidth
        {
            get
            {
                Measure();
                return packCountWidth;
            }
        }

        private static void Measure()
        {
            if (countWidth > 0f)
            {
                return;
            }
            GameFont font = Text.Font;
            Text.Font = GameFont.Tiny;
            float ours = Mathf.Max(Text.CalcSize("M3AT_ColPolys".Translate()).x, Text.CalcSize("M3AT_ColPolys2".Translate()).x);
            float theirs = Mathf.Max(Text.CalcSize("M3AT_ColMade2".Translate()).x, Text.CalcSize("M3AT_ColMade3".Translate()).x);
            Text.Font = GameFont.Small;
            theirs = Mathf.Max(theirs, Text.CalcSize("99,999 → 9,999").x);
            float names = Text.CalcSize("M3AT_ContentEveryPart".Translate()).x + 8f;
            foreach (ContentPart p in ContentSources.Shown)
            {
                float name = Text.CalcSize(("M3AT_Part_" + p).Translate()).x + 8f;
                names = Mathf.Max(names, p == ContentPart.Clothing ? name + 12f + ItemsWidth : name);
            }
            labelWidth = Mathf.Ceil(names) + 12f;
            Text.Font = font;
            countWidth = Mathf.Max(64f, Mathf.Ceil(ours) + 12f);
            packCountWidth = Mathf.Max(64f, Mathf.Ceil(theirs) + 12f);
        }

        private struct Columns
        {
            public Rect label, made, pack, ours, m3at, note;

            // Each side's block: its figures and its radio (M3AT's with its note), shaded when chosen, a click for it.
            public Rect PackSide => new Rect(made.x, made.y, pack.xMax - made.x, made.height);
            public Rect OursSide => new Rect(ours.x, ours.y, note.xMax - ours.x, ours.height);
        }

        // Laid out from the left, straight after the widest name; where the row is too narrow for that, from the right
        // edge with the names taking what is left.
        private static Columns ColumnsOf(Rect row)
        {
            Columns c = new Columns();
            float block = PackCountWidth + RadioWidth * 2f + 2f + CountWidth + NoteWidth;
            float x = row.x + Mathf.Min(labelWidth, row.width - block - 4f) + 4f;
            c.label = new Rect(row.x, row.y, x - 4f - row.x, row.height);
            c.made = new Rect(x, row.y, PackCountWidth, row.height);
            x += PackCountWidth;
            c.pack = new Rect(x, row.y, RadioWidth, row.height);
            x += RadioWidth + 2f;   // a hairline between the two sides (Sides)
            c.ours = new Rect(x, row.y, RadioWidth, row.height);
            x += RadioWidth;
            c.m3at = new Rect(x, row.y, CountWidth, row.height);
            x += CountWidth;
            c.note = new Rect(x, row.y, NoteWidth, row.height);
            return c;
        }

        // A faint grid: a line at each column's edges (the stronger hairline between the sides is Sides'), and, where
        // asked, along the row's foot from the names to the last column.
        private static void Grid(Columns c, bool foot)
        {
            GUI.color = GridLine;
            float y = c.made.y, h = c.made.height;
            foreach (float x in new[] { c.made.x, c.pack.x, c.m3at.x, c.note.x, c.note.xMax })
            {
                Widgets.DrawLineVertical(x, y, h);
            }
            Widgets.DrawLineVertical(c.ours.xMax, y, h);
            if (foot)
            {
                Widgets.DrawLineHorizontal(c.label.x, c.made.yMax, c.note.xMax - c.label.x);
            }
            GUI.color = Color.white;
        }

        // The chosen side's block, and the hairline between the sides. The Every part row (radiosOnly) shades only its
        // radios: its other cells hold the heads' lower lines, which read as heads, not as part of the chosen side.
        private static void Sides(Columns c, bool pack, bool ours, bool radiosOnly = false)
        {
            if (pack)
            {
                Widgets.DrawBoxSolid((radiosOnly ? c.pack : c.PackSide).ContractedBy(1f), Chosen);
            }
            if (ours)
            {
                Widgets.DrawBoxSolid((radiosOnly ? c.ours : c.OursSide).ContractedBy(1f), Chosen);
            }
            GUI.color = Divider;
            Widgets.DrawLineVertical(c.pack.xMax + 1f, c.pack.y + 3f, c.pack.height - 6f);
            GUI.color = Color.white;
        }

        // One tiny line of heads (mirrored: "Pack", "Pack" | "M3AT", "M3AT", "M3AT"), then the Every part row, whose
        // radios set every part of the pack. The count and note heads go on in the Every part row's cells that hold no
        // figures ("Avg polys →" and "Auto optimized to"; "Avg polys"; "Support status"), so the heads take two or three
        // lines without the rows moving down.
        private static void WideHeads(Listing_Standard list, AddOns.AddOn a, List<ContentPart> parts)
        {
            Rect names = list.GetRect(Text.LineHeightOf(GameFont.Tiny));
            Columns c = ColumnsOf(names);
            Grid(c, false);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Text.Anchor = TextAnchor.LowerCenter;
            Head(c.made, "M3AT_ColMade".Translate(), "M3AT_ColMadeTip".Translate());
            Head(c.pack, "M3AT_ColPack".Translate(), "M3AT_ContentCellPackTip".Translate());
            Head(c.ours, "M3AT_ColM3AT".Translate(), "M3AT_ContentCellM3ATTip".Translate());
            Head(c.m3at, "M3AT_ColPolys".Translate(), "M3AT_ColPolysTip".Translate());
            Head(new Rect(c.note.x + 4f, names.y, NoteWidth - 8f, names.height), "M3AT_ColNote".Translate(), null);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            Rect row = list.GetRect(RowHeight);
            c = ColumnsOf(row);
            bool allPack = parts.All(p => ContentSources.UsesPack(a.packageId, p));
            bool allOurs = parts.All(p => !ContentSources.UsesPack(a.packageId, p));
            Sides(c, allPack, allOurs, radiosOnly: true);
            Grid(c, false);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Text.Anchor = TextAnchor.UpperCenter;
            float tiny = Text.LineHeight;
            Head(new Rect(c.made.x, row.y - 3f, c.made.width, tiny), "M3AT_ColMade2".Translate(), "M3AT_ColMadeTip".Translate());
            Head(new Rect(c.made.x, row.y + 10f, c.made.width, tiny), "M3AT_ColMade3".Translate(), "M3AT_ColMadeTip".Translate());
            Head(new Rect(c.m3at.x, row.y - 3f, c.m3at.width, tiny), "M3AT_ColPolys2".Translate(), "M3AT_ColPolysTip".Translate());
            Head(new Rect(c.note.x + 4f, row.y - 3f, NoteWidth - 8f, tiny), "M3AT_ColNote2".Translate(), null);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Widgets.DrawHighlightIfMouseover(c.label);
            TooltipHandler.TipRegion(c.label, "M3AT_ContentEveryPartTip".Translate());
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(c.label.x + 4f, c.label.y, c.label.width - 8f, c.label.height), "M3AT_ContentEveryPart".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            bool toPack = RadioCell(c.pack, allPack, null, false, "M3AT_ContentAllPackTip".Translate(), fill: false);
            bool toOurs = RadioCell(c.ours, allOurs, null, false, "M3AT_ContentAllM3ATTip".Translate(), fill: false);
            // Like a part's row, either side's whole block is a click for it.
            Widgets.DrawHighlightIfMouseover(allPack ? c.ours : allOurs ? c.pack : new Rect(0f, 0f, 0f, 0f));
            toPack |= Widgets.ButtonInvisible(c.PackSide);
            toOurs |= Widgets.ButtonInvisible(c.OursSide);
            if (toPack && !allPack)
            {
                held.Remove(a.packageId);
                ContentSources.SetMany(a.packageId, parts.Select(p => new KeyValuePair<ContentPart, bool?>(p, true)));
            }
            else if (toOurs && !allOurs)
            {
                held.Remove(a.packageId);
                ContentSources.SetMany(a.packageId, parts.Select(p => new KeyValuePair<ContentPart, bool?>(p, false)));
            }
            Widgets.DrawLineHorizontal(row.x, row.yMax, c.note.xMax - row.x, new Color(1f, 1f, 1f, 0.15f));
        }

        private static void Head(Rect r, string text, string tip)
        {
            Widgets.Label(r, text);
            if (tip != null)
            {
                TooltipHandler.TipRegion(r, tip);
            }
        }

        // The Items button sits just after the row's name rather than at the label's far edge, so it reads as part of that row.
        private static float ItemsX(Rect label, string name, float items)
        {
            return Mathf.Min(label.x + 4f + Text.CalcSize(name).x + 12f, label.xMax - items);
        }

        private static void WidePartRow(Rect row, AddOns.AddOn a, ContentPart part)
        {
            Columns c = ColumnsOf(row);
            Widgets.DrawHighlightIfMouseover(c.label);
            TooltipHandler.TipRegion(c.label, ("M3AT_PartTip_" + part).Translate());
            float items = part == ContentPart.Clothing ? Mathf.Min(ItemsWidth, c.label.width * 0.45f) : 0f;
            Text.Anchor = TextAnchor.MiddleLeft;
            string name = ("M3AT_Part_" + part).Translate();
            Widgets.Label(new Rect(c.label.x + 4f, c.label.y, c.label.width - items - 8f, c.label.height), name);
            Text.Anchor = TextAnchor.UpperLeft;
            if (items > 0f && Widgets.ButtonText(new Rect(ItemsX(c.label, name, items), row.y + 2f, items - 4f, row.height - 4f), "M3AT_ContentItems".Translate(a.garments)))
            {
                PackClothesWindow.Open(a);
            }
            bool pack = ContentSources.UsesPack(a.packageId, part);
            bool byDefault = ContentSources.Default(a.packageId, part);
            Rect packSide = c.PackSide, oursSide = c.OursSide;
            Sides(c, pack, !pack);
            Grid(c, true);

            // The radios, with each side's hints in their tooltips (the pack's have no short form; M3AT's also go in Note).
            string packTip = "M3AT_ContentCellPackTip".Translate(), oursTip = "M3AT_ContentCellM3ATTip".Translate();
            string packHint = Hint(a, part, true), oursHint = Hint(a, part, false);
            if (packHint != null)
            {
                packTip += "\n\n" + packHint.Translate();
            }
            if (oursHint != null)
            {
                oursTip += "\n\n" + oursHint.Translate();
            }
            bool click = false, toPack = false;
            if (RadioCell(c.pack, pack, null, byDefault, packTip, fill: false) && !pack)
            {
                click = toPack = true;
            }
            if (RadioCell(c.ours, !pack, null, !byDefault, oursTip, fill: false) && pack)
            {
                click = true;
            }

            // The pack's side: its pieces' mean polys as drawn, in their band's colour, after what they were made with in
            // grey ("2,903 → 872", red if as made it was over budget; the arrow always grey) where M3AT cut them, so the
            // figure that costs is the one that stands out.
            int[] stats = PartStats(a, part);
            if (stats != null && stats[0] > 0)
            {
                string tip = PolysTip(stats);
                if (stats[1] < stats[2])
                {
                    Pieces(c.made.ContractedBy(4f, 0f), TextAnchor.MiddleCenter,
                        new KeyValuePair<string, Color>(Costs.N(stats[2]), Dim(stats[2] > stats[3] ? Costs.Red : Color.gray, !pack)),
                        new KeyValuePair<string, Color>(" → ", Dim(Color.gray, !pack)),
                        new KeyValuePair<string, Color>(Costs.N(stats[1]), Dim(Costs.Band(stats[1], stats[3]), !pack)));
                    TooltipHandler.TipRegion(c.made, tip);
                }
                else
                {
                    Count(c.made, stats[2], Dim(Costs.Band(stats[2], stats[3]), !pack), tip);
                }
            }
            // M3AT's side: its mean for the same part, and its note.
            int ours = OursStats(a, part, out string oursCountTip);
            if (ours > 0)
            {
                Count(c.m3at, ours, Dim(Color.white, pack), oursCountTip);   // the yardstick the pack's budget is a share of: uncoloured
            }
            else if (oursCountTip != null)
            {
                Count(c.m3at, -1, Color.gray, oursCountTip);
            }
            if (oursHint != null)
            {
                string noteKey = "M3AT_Note" + oursHint.Substring("M3AT_Hint".Length);
                if (noteKey.CanTranslate())
                {
                    Text.Font = GameFont.Small;   // the same size as the counts beside it
                    Text.Anchor = TextAnchor.MiddleCenter;
                    GUI.color = Dim(NoteColour(oursHint), pack);
                    Rect text = new Rect(c.note.x + 4f, row.y, NoteWidth - 8f, row.height);
                    string note = noteKey.Translate();
                    Widgets.Label(text, note.Truncate(text.width));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Small;
                }
                TooltipHandler.TipRegion(c.note, oursHint.Translate());
            }

            // Either side's columns are a click for that side too.
            Widgets.DrawHighlightIfMouseover(pack ? oursSide : packSide);
            if (Widgets.ButtonInvisible(packSide) && !pack)
            {
                click = toPack = true;
            }
            if (Widgets.ButtonInvisible(oursSide) && pack)
            {
                click = true;
                toPack = false;
            }
            if (click)
            {
                held.Remove(a.packageId);
                ContentSources.Set(a.packageId, part, toPack);
            }
        }

        // Words in turn on one line, each in its own colour (measured, not rich text, which the engine's labels may not
        // take), anchored left or centred in r; the last cut short if they don't fit.
        internal static void Pieces(Rect r, TextAnchor anchor, params KeyValuePair<string, Color>[] pieces)
        {
            // The font's measure leaves out spaces at a piece's ends (and the label drops them), so they are measured
            // apart: "median " then "2,367" kept a space between them.
            float space = Text.CalcSize("a a").x - Text.CalcSize("aa").x;
            float total = pieces.Sum(p => PieceWidth(p.Key, space));
            float x = anchor == TextAnchor.MiddleCenter ? r.center.x - Mathf.Min(total, r.width) / 2f : r.x;
            Text.Anchor = TextAnchor.MiddleLeft;
            foreach (KeyValuePair<string, Color> p in pieces)
            {
                string words = p.Key.Trim(' ');
                x += (p.Key.Length - p.Key.TrimStart(' ').Length) * space;
                float room = r.xMax - x;
                if (room <= 4f)
                {
                    break;
                }
                float w = words.Length > 0 ? Mathf.Min(Text.CalcSize(words).x, room) : 0f;
                if (w > 0f)
                {
                    GUI.color = p.Value;
                    Widgets.Label(new Rect(x, r.y, w + 2f, r.height), words.Truncate(w + 2f));
                }
                x += w + (p.Key.Length - p.Key.TrimEnd(' ').Length) * space;
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static float PieceWidth(string piece, float space)
        {
            string words = piece.Trim(' ');
            return (words.Length > 0 ? Text.CalcSize(words).x : 0f) + (piece.Length - words.Length) * space;
        }

        // The side not drawn, greyed.
        // A support status note's colour, by how well M3AT covers the part: strong green its own bespoke models, a yellower green
        // shown automatically but whole (Facial Animation's faces) or matched to M3AT's own parts by hand, yellow autogenerated or automatched, orange autogenerated as a prototype,
        // red not found; white for notes that say nothing of support (a dash).
        internal static readonly Color StrongGreen = new Color(0.3f, 0.95f, 0.3f), YellowGreen = new Color(0.75f, 0.92f, 0.4f), Orange = new Color(1f, 0.6f, 0.3f);
        // Automatched, with garments picked by hand for some of it ("Automatched (tuned)"): between the yellow and the
        // yellower green. Mapped (another mod's genes drawn as M3AT's parts) is the yellower green.
        internal static readonly Color TunedAmber = new Color(0.88f, 0.86f, 0.38f);

        private static Color NoteColour(string hint)
        {
            switch (hint)
            {
                case "M3AT_HintModelEars":
                    return StrongGreen;
                case "M3AT_HintFace":
                    return YellowGreen;
                case "M3AT_HintMatchedArmour":
                case "M3AT_HintMatchedClothes":
                case "M3AT_HintHair":
                    return Costs.Amber;
                case "M3AT_HintTunedArmour":
                case "M3AT_HintTunedClothes":
                    return TunedAmber;
                case "M3AT_HintBuiltEars":
                    return Orange;
                case "M3AT_HintNoFace":
                    return Costs.Red;
                default:
                    return Color.white;
            }
        }

        private static Color Dim(Color c, bool dim)
        {
            return dim ? Color.Lerp(c, Color.gray, 0.6f) : c;
        }

        // A count in its column, centred and coloured; -1 for a dash (not known yet).
        private static void Count(Rect r, int n, Color colour, string tip)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = colour;
            Widgets.Label(r.ContractedBy(4f, 0f), n >= 0 ? Costs.N(n) : "–");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            if (tip != null)
            {
                TooltipHandler.TipRegion(r, tip);
            }
        }

        private static string PolysTip(int[] stats)
        {
            return stats[2] > stats[1]
                ? "M3AT_PartPolysCutTip".Translate(stats[0], Costs.N(stats[1]), Costs.N(stats[2]), Costs.N(stats[3]))
                : "M3AT_PartPolysTip".Translate(stats[0], Costs.N(stats[1]), Costs.N(stats[3]));
        }

        private static readonly Dictionary<string, KeyValuePair<int, string>> oursStats =
            new Dictionary<string, KeyValuePair<int, string>>(StringComparer.OrdinalIgnoreCase);
        private static int oursStatsVersion = -1;

        // M3AT's mean polys for a pack's row, and its tooltip: for Clothing and Armour what M3AT draws for the pack's items
        // (SkinnedFigure.OwnStats); for GeneParts its own models of the pack's races' ears and tails (OwnRaceParts), or a
        // dash where they are built from the race's sprites. 0 and no tooltip where the row has no count. Kept per pack
        // and row until the packs change.
        private static int OursStats(AddOns.AddOn a, ContentPart part, out string tip)
        {
            tip = null;
            SkinnedFigure figure = WalkFigures.SkinForAddOns;
            if (figure == null || (part != ContentPart.Clothing && part != ContentPart.Armour && part != ContentPart.GeneParts))
            {
                return 0;
            }
            if (oursStatsVersion != AddOns.Version)
            {
                oursStats.Clear();
                oursStatsVersion = AddOns.Version;
            }
            string key = a.packageId + "/" + part;
            KeyValuePair<int, string> kept;
            if (!oursStats.TryGetValue(key, out kept))
            {
                int mean = 0, count;
                string said = null;
                if (part == ContentPart.GeneParts)
                {
                    if (a.races.Count > 0)
                    {
                        WalkFigures.OwnRaceParts(figure, a.races, out count, out mean);
                        if (count > 0)
                        {
                            said = "M3AT_OursPartsTip".Translate(count, Costs.N(mean));
                        }
                        else
                        {
                            // Built from the race's pictures: what the builder cuts each part down to (an ear, tuft or horn,
                            // and a tail), their mean, as the real counts are known only once a part is drawn.
                            mean = (SkinnedFigure.AddonTriangles + SkinnedFigure.AddonTailTriangles) / 2;
                            said = "M3AT_OursBuiltTip".Translate(Costs.N(SkinnedFigure.AddonTriangles), Costs.N(SkinnedFigure.AddonTailTriangles));
                        }
                    }
                }
                else
                {
                    figure.OwnStats(a.packageId, part, out count, out mean);
                    said = count > 0 ? (string)"M3AT_OursPolysTip".Translate(count, Costs.N(mean)) : null;
                }
                kept = new KeyValuePair<int, string>(mean, said);
                oursStats[key] = kept;
            }
            tip = kept.Value;
            return kept.Key;
        }

        // The swap: while any part that shows at once is the pack's, puts the choices aside and shows M3AT's for them all;
        // else brings the choices back (all the pack's if none were put aside, or they were M3AT's throughout).
        internal static void Swap(AddOns.AddOn a)
        {
            List<ContentPart> live = Swappable(a);
            if (live.Count == 0)
            {
                return;
            }
            if (live.Any(p => ContentSources.UsesPack(a.packageId, p)))
            {
                held[a.packageId] = live.ToDictionary(p => p, p => ContentSources.Chosen(a.packageId, p));
                ContentSources.SetMany(a.packageId, live.Select(p => new KeyValuePair<ContentPart, bool?>(p, false)));
                return;
            }
            Dictionary<ContentPart, bool?> mix;
            List<KeyValuePair<ContentPart, bool?>> wanted = held.TryGetValue(a.packageId, out mix)
                ? mix.Where(c => live.Contains(c.Key)).ToList()
                : new List<KeyValuePair<ContentPart, bool?>>();
            if (!wanted.Any(c => c.Value ?? ContentSources.Default(a.packageId, c.Key)))
            {
                wanted = live.Select(p => new KeyValuePair<ContentPart, bool?>(p, true)).ToList();
            }
            held.Remove(a.packageId);
            ContentSources.SetMany(a.packageId, wanted);
        }

        private static readonly Color Chosen = new Color(0.3f, 0.55f, 0.8f, 0.45f);

        private static void PartRow(Listing_Standard list, AddOns.AddOn a, ContentPart part)
        {
            Rect row = list.GetRect(RowHeight);
            if (row.width >= WideFrom)
            {
                WidePartRow(row, a, part);
                return;
            }
            float cell = CellWidth(row);
            Rect label = new Rect(row.x, row.y, row.width - cell * 2f - 4f, row.height);
            Widgets.DrawHighlightIfMouseover(label);
            TooltipHandler.TipRegion(label, ("M3AT_PartTip_" + part).Translate());
            float items = part == ContentPart.Clothing ? Mathf.Min(110f, label.width * 0.45f) : 0f;
            Text.Anchor = TextAnchor.MiddleLeft;
            string name = ("M3AT_Part_" + part).Translate();
            Widgets.Label(new Rect(label.x + 4f, label.y, label.width - items - 8f, label.height), name);
            Text.Anchor = TextAnchor.UpperLeft;
            if (items > 0f && Widgets.ButtonText(new Rect(ItemsX(label, name, items), row.y + 2f, items - 4f, row.height - 4f), "M3AT_ContentItems".Translate(a.garments)))
            {
                PackClothesWindow.Open(a);
            }
            bool pack = ContentSources.UsesPack(a.packageId, part);
            bool byDefault = ContentSources.Default(a.packageId, part);
            Cell(new Rect(row.xMax - cell * 2f, row.y, cell - 2f, row.height), pack, byDefault, a, part, true);
            Cell(new Rect(row.xMax - cell, row.y, cell - 2f, row.height), !pack, !byDefault, a, part, false);
        }

        // One source's cell: filled and its radio on when drawn, a short note on what that
        // source is like where it helps (Hint, the whole of it in the tooltip), the whole cell a click.
        private static void Cell(Rect cell, bool on, bool isDefault, AddOns.AddOn a, ContentPart part, bool pack)
        {
            string packageId = a.packageId;
            string tip = (pack ? "M3AT_ContentCellPackTip" : "M3AT_ContentCellM3ATTip").Translate();
            string hint = Hint(a, part, pack);
            string note = null;
            if (hint != null)
            {
                string noteKey = "M3AT_Note" + hint.Substring("M3AT_Hint".Length);   // a hint with no short form is tooltip only
                tip += "\n\n" + hint.Translate();   // the narrow view's cells carry no tag, only the tooltip
            }
            // The pack's own pieces for this row, their mean polys (in the tooltip: a narrow view has no room for the columns).
            Color? noteColour = null;
            int[] stats = pack ? PartStats(a, part) : null;
            if (stats != null && stats[0] > 0)
            {
                tip += "\n\n" + PolysTip(stats);
            }
            if (RadioCell(cell, on, null, isDefault, tip, note, noteColour) && !on)
            {
                held.Remove(packageId);
                ContentSources.Set(packageId, part, pack);
            }
        }

        // A radio cell, shared with the pack's clothing list (ClothingToolWindow.PackItemRow): filled when on, its label
        // or note (if any) beside the radio, the default's tooltip saying it is the default. True when clicked (on or not: a cell that
        // opens a menu wants a click while on).
        private static Texture2D radioOff;
        private static Texture2D RadioOff => radioOff ?? (radioOff = ContentFinder<Texture2D>.Get("UI/Widgets/RadioButOff"));   // Widgets' own is private

        // fill false: the caller shades the chosen side's whole block itself (the wide view's Sides).
        internal static bool RadioCell(Rect cell, bool on, string label, bool isDefault, string tip, string note = null, Color? noteColour = null, bool fill = true)
        {
            Rect inner = cell.ContractedBy(1f);
            if (on && fill)
            {
                Widgets.DrawBoxSolid(inner, Chosen);
            }
            else if (!on)
            {
                Widgets.DrawHighlightIfMouseover(inner);
            }
            // Only the radio's picture: Widgets.RadioButton is a button of its own, which took the click (with its sound)
            // off the cell's, so a click on the circle itself changed nothing.
            // A bare radio (the wide view's columns) sits in its cell's middle; one with words beside it, at the left.
            float radioX = label == null && note == null ? inner.center.x - 12f : inner.x + 4f;
            GUI.DrawTexture(new Rect(radioX, inner.center.y - 12f, 24f, 24f), on ? Widgets.RadioButOnTex : RadioOff);
            // The default is said in the tooltip only, so the cell has room for its note.
            const float mark = 0f;
            if (isDefault && tip != null)
            {
                tip += "\n\n" + "M3AT_ContentDefaultTip".Translate();
            }
            if (note != null && label == null)
            {
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                Color shade = noteColour ?? Color.white;
                GUI.color = on ? shade : Color.Lerp(shade, Color.gray, 0.5f);
                Rect text = new Rect(inner.x + 30f, inner.y, inner.width - 32f - mark, inner.height);
                Widgets.Label(text, note.Truncate(text.width));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
            if (label != null)
            {
                Text.Anchor = TextAnchor.MiddleLeft;
                Rect text = new Rect(inner.x + 30f, inner.y, inner.width - 32f - mark, inner.height);
                Widgets.Label(text, label.Truncate(text.width));
                Text.Anchor = TextAnchor.UpperLeft;
            }
            if (tip != null)
            {
                TooltipHandler.TipRegion(cell, tip);
            }
            return Widgets.ButtonInvisible(cell);
        }

        // What a source (the pack's, or M3AT's) is like, where it helps to know: an M3AT_Hint key, its short form for the
        // cell the M3AT_Note key of the same name, where it has one (Other's has none).
        private static string Hint(AddOns.AddOn a, ContentPart part, bool pack)
        {
            switch (part)
            {
                case ContentPart.Face:
                    if (pack || a.races.Count == 0)
                    {
                        return null;
                    }
                    return a.races.Any(ContentSources.FacialAnimationHas) ? "M3AT_HintFace" : "M3AT_HintNoFace";
                case ContentPart.GeneParts:
                    // M3AT's own ears and tails for the races it has them for (Wolfein's, Ratkin's), else built from the art.
                    // A pack with no races (gene parts alone) has M3AT's own cat ears, tails and horns: nothing to say.
                    return pack || a.races.Count == 0 ? null : a.races.All(WalkFigures.HasOwnEarsTail) ? "M3AT_HintModelEars" : "M3AT_HintBuiltEars";
                case ContentPart.Clothing:
                    // Automatched (tuned) where any of those had its garments picked by hand (a stands_for line), as the
                    // race list says (RaceSupport.Clothes).
                    return pack ? null : MatchHint(a, ContentPart.Clothing, null);
                case ContentPart.Armour:
                    // Automatched where any of the pack's armour has no garment of its own; else M3AT's own armour, said only in the tooltip.
                    return pack ? null : MatchHint(a, ContentPart.Armour, "M3AT_HintArmour");
                case ContentPart.Hair:
                    return pack ? null : "M3AT_HintHair";   // always built from the hair pictures
                case ContentPart.HeadwearFit:
                    return pack ? null : "M3AT_HintHeadwearFit";   // a dash: nothing more to say of M3AT's
                case ContentPart.SkinColour:
                    return pack ? null : "M3AT_HintSkinColour";   // a dash, as for hats
                case ContentPart.BodyShape:
                    return null;   // a body change for everyone is chosen in BodyChoice, with its own notes
                case ContentPart.Other:
                    return pack && a.patches > 0 ? "M3AT_HintOther" : null;
                default:
                    return null;
            }
        }

        // The hint for a pack's Clothing or Armour row as M3AT draws it: Automatched (tuned) where any piece matched had its garments
        // picked by hand, Automatched where any was matched automatically, else none (the given fallback).
        private static string MatchHint(AddOns.AddOn a, ContentPart part, string none)
        {
            string row = part == ContentPart.Armour ? "Armour" : "Clothes";
            switch (Matches(a, part))
            {
                case 2:
                    return "M3AT_HintTuned" + row;
                case 1:
                    return "M3AT_HintMatched" + row;
                default:
                    return none;
            }
        }

        private static readonly Dictionary<string, int> matched = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int matchedVersion = -1;

        // Whether M3AT draws any of the pack's apparel of this row (Clothing or Armour, ContentSources.ClothingPartOf) as an
        // automatic match of its own garments, having no garment made for that item: 0 none, 1 matched, 2 matched with some
        // of it picked by hand (StandsFor). Kept per pack and row.
        private static int Matches(AddOns.AddOn a, ContentPart part)
        {
            SkinnedFigure figure = WalkFigures.SkinForAddOns;
            if (figure == null)
            {
                return 0;
            }
            if (matchedVersion != AddOns.Version)
            {
                matched.Clear();
                matchedVersion = AddOns.Version;
            }
            int found;
            string key = a.packageId + "/" + part;
            if (!matched.TryGetValue(key, out found))
            {
                List<ThingDef> unmade = figure.PackApparel(a.packageId).Where(d => ContentSources.ClothingPartOf(d) == part && figure.OwnGarmentOf(d) <= 0).ToList();
                found = unmade.Count == 0 ? 0 : unmade.Any(d => figure.StandsFor(d).Length > 0) ? 2 : 1;
                matched[key] = found;
            }
            return found;
        }

        private static readonly Dictionary<string, int[]> partStats = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        private static int partStatsVersion = -1;

        // A pack's pieces for one row (SkinnedFigure.PartStats): { count, mean polys as drawn, as made, mean budget }; null
        // before the figure is loaded. Kept per pack and row until the packs change.
        private static int[] PartStats(AddOns.AddOn a, ContentPart part)
        {
            SkinnedFigure figure = WalkFigures.SkinForAddOns;
            if (figure == null)
            {
                return null;
            }
            if (partStatsVersion != AddOns.Version)
            {
                partStats.Clear();
                partStatsVersion = AddOns.Version;
            }
            int[] stats;
            string key = a.packageId + "/" + part;
            if (!partStats.TryGetValue(key, out stats))
            {
                figure.PartStats(a.packageId, part, out int count, out int mean, out int made, out int budget);
                if (part == ContentPart.GeneParts && count == 0)
                {
                    // A pack that hands M3AT its races' ears and tails as files (by its patches or the expansion API,
                    // the Wolfein pack's ears_wolfein*.txt) has no pieces of them: counted from its files instead.
                    PackRacePartFiles(a.packageId, out count, out mean);
                    made = mean;
                    budget = Mathf.RoundToInt(Mathf.Max(OursStats(a, part, out string _), SkinnedFigure.OursLeast) * SkinnedFigure.DefaultShare);
                }
                stats = new[] { count, mean, made, budget };
                partStats[key] = stats;
            }
            return stats;
        }

        // A pack's race ear and tail files (M3AT's ears.txt and tail.txt forms, named for them as RacePacks.md shows:
        // ears_myrace.txt, tail_myrace.txt, and variants of them): how many, and their mean triangles, each kind's mean
        // averaged so that many variants of one ear (the Wolfein pack has 46) don't outweigh its one tail.
        private static void PackRacePartFiles(string packageId, out int files, out int mean)
        {
            files = mean = 0;
            ModContentPack mod = LoadedModManager.RunningModsListForReading.FirstOrDefault(m => string.Equals(m.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            if (mod == null || !System.IO.Directory.Exists(mod.RootDir))
            {
                return;
            }
            long[] sum = new long[2];
            int[] n = new int[2];
            try
            {
                foreach (string path in System.IO.Directory.EnumerateFiles(mod.RootDir, "*.txt", System.IO.SearchOption.AllDirectories))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                    string[] words = name.Split('_', '-', ' ', '.');
                    int kind = words.Any(w => w == "ears" || w == "ear") ? 0 : words.Any(w => w == "tail" || w == "tails") ? 1 : -1;
                    int tris = kind >= 0 && !name.StartsWith("garment") ? SkinnedFigure.FileTriangles(path) : 0;
                    if (tris > 0)
                    {
                        sum[kind] += tris;
                        n[kind]++;
                    }
                }
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                return;
            }
            files = n[0] + n[1];
            int kinds = (n[0] > 0 ? 1 : 0) + (n[1] > 0 ? 1 : 0);
            mean = kinds > 0 ? (int)(((n[0] > 0 ? sum[0] / n[0] : 0) + (n[1] > 0 ? sum[1] / n[1] : 0)) / kinds) : 0;
        }

        private static string RaceLabel(string defName)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            return d != null ? d.LabelCap.ToString() : defName;
        }

        // Whether this pack changes this pawn: its race is the pack's, or it wears one of the pack's garments.
        internal static bool Covers(AddOns.AddOn a, Pawn pawn)
        {
            if (pawn == null)
            {
                return true;
            }
            if (pawn.def != null && a.races.Contains(pawn.def.defName))
            {
                return true;
            }
            SkinnedFigure skin = WalkFigures.SkinForAddOns;
            if (skin == null || pawn.apparel == null)
            {
                return false;
            }
            foreach (Apparel worn in pawn.apparel.WornApparel)
            {
                int g = skin.PackGarmentOf(worn.def);
                if (g > 0 && skin.OwnerOf(g) == a.packageId)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // What content costs, shown where players choose it (encouraging content made within M3AT's budgets): triangles
    // against the budget in a colour band (green within BandGood of it, amber within it, red over: Band), "M3AT auto-optimized
    // from" where M3AT cut a garment down, the totals a person and the colony draw, a pack's measured cost from the last
    // benchmark (Benchmark.cs, Settings.packCosts), and M3AT's own garments as the yardstick in every tooltip (TrisTip).
    internal static class Costs
    {
        private const float BandGood = 0.4f;    // of the budget, the most for green
        private const int PawnGood = 3000;      // triangles a person draws (body and clothes) still green: M3AT's body and a full outfit
        private const int PawnHeavy = 7000;     // and past which red
        internal static readonly Color Green = new Color(0.55f, 0.9f, 0.55f), Amber = new Color(1f, 0.8f, 0.35f), Red = new Color(1f, 0.45f, 0.4f);

        internal static Color Band(int made, int budget)
        {
            return made <= budget * BandGood ? Green : made <= budget ? Amber : Red;
        }

        internal static Color PawnBand(int tris)
        {
            return tris <= PawnGood ? Green : tris <= PawnHeavy ? Amber : Red;
        }

        internal static string N(int n)
        {
            return n.ToString("N0");
        }

        // A garment's triangles as drawn, with what it was made with if M3AT cut it down.
        internal static string Tris(SkinnedFigure f, int g)
        {
            int now = f.TrianglesOf(g), made = f.MadeTrianglesOf(g);
            return made > now ? (string)"M3AT_CostTrisCut".Translate(N(now), N(made)) : (string)"M3AT_CostTris".Translate(N(now));
        }

        // The yardstick: what a triangle costs, M3AT's own garments for scale, and this one against M3AT's equivalent.
        internal static string TrisTip(SkinnedFigure f, int g, int[] equivalent)
        {
            int shirt = f.MadeTrianglesOf(f.GarmentNamed("BasicShirt")), armour = f.MadeTrianglesOf(f.GarmentNamed("PowerArmor"));
            string tip = "M3AT_CostTrisTip".Translate(N(f.BodyFileTriangles), N(shirt), N(armour), SkinnedFigure.DefaultShare.ToString("0.0#"), N(f.BudgetOf(g)));
            int theirs = f.MadeTrianglesOf(g), ours = 0;
            if (equivalent != null)
            {
                foreach (int e in equivalent)
                {
                    ours += f.MadeTrianglesOf(e);
                }
            }
            if (ours > 0 && theirs > 0 && !f.IsPackGarment(equivalent.Length > 0 ? equivalent[0] : -1))
            {
                tip += "\n\n" + "M3AT_CostVsOurs".Translate(((float)theirs / ours).ToString("0.0"), N(ours));
            }
            int made = f.MadeTextureOf(g), now = f.TextureOf(g);
            if (made > now && now > 0)
            {
                tip += "\n\n" + "M3AT_CostTextureCut".Translate(made, now, SkinnedFigure.TextureMost);
            }
            return tip;
        }

        // The "About poly counts" message: why a pack's triangles matter, with M3AT's own numbers for scale. They are there
        // to show what M3AT's own body and garments cost against modded ones, so they stay M3AT's whatever body is in use:
        // read from the files as made (a pack that changes them as they are read doesn't touch those counts), or the shipped
        // counts below when M3AT is running from another mod's copy, whose files can differ. A line under them says when the
        // body drawn isn't M3AT's.
        private const int ShippedBody = 1200, ShippedShirt = 190, ShippedDuster = 500, ShippedArmour = 1044, ShippedEars = 260;

        internal static void ShowAbout()
        {
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            string copy = WalkFigures.CopyName;
            int body = ShippedBody, shirt = ShippedShirt, duster = ShippedDuster, armour = ShippedArmour, ears = ShippedEars;
            if (f != null && copy == null)
            {
                body = f.BodyFileTriangles > 0 ? f.BodyFileTriangles : body;
                int s = f.MadeTrianglesOf(f.GarmentNamed("BasicShirt")), d = f.MadeTrianglesOf(f.GarmentNamed("Duster"));
                int a = f.MadeTrianglesOf(f.GarmentNamed("PowerArmor"));
                shirt = s > 0 ? s : shirt;
                duster = d > 0 ? d : duster;
                armour = a > 0 ? a : armour;
                int e = f.CatEarTriangles;
                ears = e > 0 ? e : ears;
            }
            string changed = "";
            if (f != null && f.BodyChanged)
            {
                changed = "\n\n" + "M3AT_PolyAboutBodyChanged".Translate(SkinnedFigure.BodyChangerName(), N(f.TrianglesOf(0)));
            }
            else if (copy != null)
            {
                changed = "\n\n" + CopyLine(copy);
            }
            string text = "M3AT_PolyAboutText".Translate(N(body), N(shirt), N(duster), N(armour), N(ears), changed);
            // A second button opens the shipped guides folder (Docs/Apparel.md and friends) for whoever is making content.
            ModContentPack own = LoadedModManager.RunningModsListForReading.FirstOrDefault(m => string.Equals(m.PackageId, "marumirai.M3AT", StringComparison.OrdinalIgnoreCase));
            string docs = own != null ? System.IO.Path.Combine(own.RootDir, "Docs") : null;
            if (docs != null && System.IO.Directory.Exists(docs))
            {
                Find.WindowStack.Add(new Dialog_MessageBox(text, "OK".Translate(), null, "M3AT_PolyAboutGuides".Translate(), () => OpenFolder(docs), "M3AT_PolyAboutTitle".Translate()));
            }
            else
            {
                Find.WindowStack.Add(new Dialog_MessageBox(text, title: "M3AT_PolyAboutTitle".Translate()));
            }
        }

        private static void OpenFolder(string folder)
        {
            try
            {
                Application.OpenURL(new Uri(folder.TrimEnd('/', '\\') + System.IO.Path.DirectorySeparatorChar).AbsoluteUri);
            }
            catch (Exception e)
            {
                Log.Warning("[M3AT] Couldn't open " + folder + ": " + e.Message);
                Messages.Message(folder, MessageTypeDefOf.NeutralEvent, false);
            }
        }

        // M3AT running from another mod's copy (WalkFigures.CopyName): whose body is drawn, and its count against M3AT's
        // shipped one when they differ. Its extra models drawn by its own code (the RJW bridge's adult bodies) can't be
        // counted, as nothing tells M3AT of them.
        internal static string CopyLine(string copy)
        {
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            int body = f != null ? f.BodyFileTriangles : 0;
            return body > 0 && body != ShippedBody
                ? (string)"M3AT_BodyCopyChanged".Translate(copy, N(body), N(ShippedBody))
                : (string)"M3AT_BodyCopy".Translate(copy);
        }

        // Whether a pack patches how M3AT reads its body (SkinnedFigure.ReadMesh or Load).
        internal static bool ChangesBody(AddOns.AddOn a)
        {
            return AddOns.PatchedBy(a.packageId).Any(p => p.Key.DeclaringType == typeof(SkinnedFigure) && (p.Key.Name == "ReadMesh" || p.Key.Name == "Load"));
        }

        // The colony's people as dressed now, triangles drawn for body and clothes.
        internal static void ColonyLine(Listing_Standard list)
        {
            Map map = Find.CurrentMap;
            if (WalkFigures.SkinForAddOns == null || map == null)
            {
                return;
            }
            int sum = 0, people = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                int t = WalkFigures.DrawnTriangles(p);
                if (t > 0)
                {
                    sum += t;
                    people++;
                }
            }
            if (people == 0)
            {
                return;
            }
            GUI.color = PawnBand(sum / people);
            Rect row = list.GetRect(Text.LineHeight);
            Widgets.Label(row, "M3AT_CostColony".Translate(N(sum), people, N(sum / people)));
            TooltipHandler.TipRegion(row, "M3AT_CostColonyTip".Translate(N(PawnGood), N(PawnHeavy)));
            GUI.color = Color.white;
        }

        internal static void PawnLine(Listing_Standard list, Pawn pawn)
        {
            int t = WalkFigures.DrawnTriangles(pawn);
            if (t <= 0)
            {
                return;
            }
            GUI.color = PawnBand(t);
            Rect row = list.GetRect(Text.LineHeight);
            Widgets.Label(row, "M3AT_CostPawn".Translate(N(t)));
            TooltipHandler.TipRegion(row, "M3AT_CostColonyTip".Translate(N(PawnGood), N(PawnHeavy)));
            GUI.color = Color.white;
        }

        // Under a pack's header: its garments summed up, its measured cost, and (in the full list) Measure now
        // and, in development mode, its report.
        // A pack's costs in short for beside its name (folded or not): its pieces' mean polys as made and how many M3AT
        // cut, in PackLines' colour, the long line and its explanation as the tooltip; null for a pack with no pieces.
        // Also split about the mean figure (before, figure, after), for drawing it alone in colour.
        internal static string PackShort(AddOns.AddOn a, out Color colour, out string tip, out string before, out string figure, out string after)
        {
            before = figure = after = "";
            string whole = PackShort(a, out colour, out tip, out string marked);
            if (whole == null)
            {
                return null;
            }
            Split(whole, marked, out before, out figure, out after);
            return whole;
        }

        // A line split about its figure, which was put between two Marks in the marked copy: before, figure, after
        // (the whole line before it if a translation dropped the figure).
        internal static void Split(string whole, string marked, out string before, out string figure, out string after)
        {
            before = figure = after = "";
            int i = marked.IndexOf(Mark), j = marked.LastIndexOf(Mark);
            if (i < 0 || j <= i)
            {
                before = whole;
                return;
            }
            before = marked.Substring(0, i);
            figure = marked.Substring(i + 1, j - i - 1);
            after = marked.Substring(j + 1);
        }

        internal const char Mark = '\u0001';

        private static string PackShort(AddOns.AddOn a, out Color colour, out string tip, out string marked)
        {
            colour = Color.white;
            tip = null;
            marked = null;
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            if (f == null || a.garments + a.geneParts == 0)
            {
                return null;
            }
            f.PackStats(a.packageId, out int count, out int mean, out int cut, out int over, out int shrunk);
            if (count == 0)
            {
                return null;
            }
            colour = PackColour(f, a, cut, over);
            tip = "M3AT_CostPack".Translate(count, N(mean), cut, shrunk) + (over > 0 ? (string)"M3AT_CostPackOver".Translate(over) : "")
                + "\n\n" + PackTip();
            string m = Mark + N(mean) + Mark;
            // How many pieces were cut is in the tooltip only (no longer "· 49/59 auto optimized" in the line), so the
            // title row fits the bottom-bar window sized to the table.
            marked = "M3AT_CostPackShort".Translate(m);
            return "M3AT_CostPackShort".Translate(N(mean));
        }

        // Red if any piece was cut or kept over; else each piece against its own budget (a share of M3AT's like garment),
        // the pack as its heaviest.
        private static Color PackColour(SkinnedFigure f, AddOns.AddOn a, int cut, int over)
        {
            if (cut > 0 || over > 0)
            {
                return Red;
            }
            float worst = 0f;
            foreach (int g in f.PackPieces(a.packageId))
            {
                worst = Mathf.Max(worst, (float)f.MadeTrianglesOf(g) / Mathf.Max(f.BudgetOf(g), 1));
            }
            return Band(Mathf.RoundToInt(worst * 1000f), 1000);
        }

        // A pack's load time's colour: red from LoadWarnSeconds, amber from a second, green under.
        internal static Color LoadColour(double seconds)
        {
            return seconds >= SkinnedFigure.LoadWarnSeconds ? Red : seconds >= 1.0 ? Amber : Green;
        }

        private static string PackTip()
        {
            return "M3AT_CostPackTip".Translate(SkinnedFigure.DefaultShare.ToString("0.0#"), N(Mathf.RoundToInt(SkinnedFigure.OursLeast * SkinnedFigure.DefaultShare)), SkinnedFigure.TextureMost);
        }

        internal static void PackLines(Listing_Standard list, AddOns.AddOn a, bool full)
        {
            SkinnedFigure f = WalkFigures.SkinForAddOns;
            // What a pack's body costs is shown once, under the body choice (BodyCost), not under each pack.
            if (f != null && a.garments + a.geneParts > 0)
            {
                f.PackStats(a.packageId, out int count, out int mean, out int cut, out int over, out int shrunk);
                GUI.color = PackColour(f, a, cut, over);
                Rect row = list.GetRect(Text.LineHeight);
                Widgets.Label(row, "M3AT_CostPack".Translate(count, N(mean), cut, shrunk) + (over > 0 ? (string)"M3AT_CostPackOver".Translate(over) : ""));
                TooltipHandler.TipRegion(row, PackTip());
                GUI.color = Color.white;
            }
            // What the pack added to loading is on its title row (TitleRow), not here.
            Rect measured = list.GetRect(24f);
            float ms, noise;
            int people;
            string date;
            Text.Anchor = TextAnchor.MiddleLeft;
            string said = BenchmarkWindow.LastMeasured(a.packageId, out ms, out noise, out people, out date)
                ? (Mathf.Abs(ms) <= noise ? (string)"M3AT_CostMeasuredSmall".Translate(noise.ToString("0.00"), people, date)
                    : (string)"M3AT_CostMeasured".Translate(ms.ToString("0.00"), noise.ToString("0.00"), people, date))
                : (string)"M3AT_CostNotMeasured".Translate();
            // Measure now and the report (in the full list).
            float buttons = full ? 130f + (Prefs.DevMode ? 130f : 0f) : 0f;
            Widgets.Label(new Rect(measured.x, measured.y, measured.width - buttons, measured.height), said);
            Text.Anchor = TextAnchor.UpperLeft;
            if (full)
            {
                Rect now = new Rect(measured.xMax - buttons, measured.y, 126f, 24f);
                if (Widgets.ButtonText(now, "M3AT_CostMeasureNow".Translate(), active: BenchmarkWindow.CanRun))
                {
                    Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
                    Find.WindowStack.TryRemove(typeof(Dialog_Options), false);
                    BenchmarkWindow.Open();
                }
                TooltipHandler.TipRegion(now, BenchmarkWindow.CanRun ? "M3AT_CostMeasureNowTip".Translate() : "M3AT_BenchmarkNeedsGame".Translate());
                if (Prefs.DevMode && Widgets.ButtonText(new Rect(now.xMax + 4f, measured.y, 126f, 24f), "M3AT_PackReport".Translate()))
                {
                    PackReport.Write(a);
                }
            }
        }
    }

    // The M3AT button on the bottom bar: shown while content packs are active and the contentButton option is on.
    public class ContentButtonWorker : MainButtonWorker_ToggleTab
    {
        public override bool Visible
        {
            get
            {
                Settings s = SettingsMod.Settings;
                return base.Visible && (s == null || s.contentButton) && WalkFigures.SkinForAddOns != null
                    && (ContentSources.Packs().Count > 0 || RaceSupport.Rows().Count > 0);   // the races and mods M3AT tunes are worth a look too
            }
        }
    }

    // Its window, over the map, which isn't paused, so a switch shows on the pawns at once.
    public class ContentTabWindow : MainTabWindow
    {
        private readonly ContentView view = new ContentView();

        // As wide as the table with the scroll bar and margins: the columns start straight after the parts' names, so
        // anything wider was empty space at the right. The width is set again each frame, as the races' rows change.
        private float Wanted => Mathf.Min(ContentView.WideViewWidth + 16f + Margin * 2f + 2f, UI.screenWidth - 80f);

        public override Vector2 RequestedTabSize => new Vector2(Wanted, Mathf.Min(720f, UI.screenHeight - 120f));

        public override void DoWindowContents(Rect inRect)
        {
            view.Draw(inRect, null, true);
            float wanted = Wanted;
            if (Mathf.Abs(windowRect.width - wanted) > 1f)
            {
                windowRect.width = wanted;
            }
        }
    }

    // A content pack's clothing, item by item: its garment or M3AT's, and whether its garment is painted with the
    // apparel's art (ClothingTool.cs keeps the choices: Settings.clothingChoices, paintedPackClothes).
    public class PackClothesWindow : Window
    {
        private AddOns.AddOn pack;
        private Vector2 scroll;
        private float viewHeight;

        public override Vector2 InitialSize => new Vector2(560f, 640f);

        internal static void Open(AddOns.AddOn pack)
        {
            PackClothesWindow open = Find.WindowStack.WindowOfType<PackClothesWindow>();
            if (open != null)
            {
                open.pack = pack;
                return;
            }
            Find.WindowStack.Add(new PackClothesWindow { pack = pack });
        }

        public PackClothesWindow()
        {
            draggable = true;
            resizeable = true;
            doCloseX = true;
            forcePause = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            onlyOneOfTypeAllowed = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            SkinnedFigure figure = WalkFigures.SkinForAddOns;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "M3AT_PackClothesTitle".Translate(pack.name));
            Text.Font = GameFont.Small;
            if (figure == null)
            {
                return;
            }
            List<ThingDef> defs = figure.PackApparel(pack.packageId);
            // Every item at once, the pack's or M3AT's, over the items' own columns (PackItemRow's thirds, inside its
            // box's 6 margin); a custom pick is only per item.
            Rect heads = new Rect(inRect.x + 6f, inRect.y + 38f, inRect.width - 16f - 12f, 28f);
            float third = heads.width / 3f;
            Rect allPack = new Rect(heads.x, heads.y, third - 2f, heads.height);
            Rect allOurs = new Rect(heads.x + third, heads.y, third - 2f, heads.height);
            bool everyPack = defs.All(d => figure.GarmentOf(d) == figure.PackGarmentOf(d));
            bool everyOurs = defs.All(d => figure.GarmentOf(d) != figure.PackGarmentOf(d) && !SettingsMod.Settings.clothingChoices.ContainsKey(d.defName)
                || SettingsMod.Settings.clothingChoices.TryGetValue(d.defName, out string c) && c == WalkFigures.M3atAutomatic);
            if (ContentView.RadioCell(allPack, everyPack, "M3AT_ItemsAllPack".Translate(), false, "M3AT_ItemsAllPackTip".Translate()) && !everyPack)
            {
                ClothingToolWindow.SetAllItems(figure, defs, true);
            }
            if (ContentView.RadioCell(allOurs, everyOurs, "M3AT_ItemsAllM3AT".Translate(), false, "M3AT_ItemsAllM3ATTip".Translate()) && !everyOurs)
            {
                ClothingToolWindow.SetAllItems(figure, defs, false);
            }
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(heads.x + third * 2f, heads.y, third - 2f, heads.height), "M3AT_ItemsCustomHead".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            Rect outer = new Rect(inRect.x, inRect.y + 72f, inRect.width, inRect.height - 72f);
            Rect view = new Rect(0f, 0f, outer.width - 16f, Mathf.Max(viewHeight, outer.height));
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            foreach (ThingDef def in defs)
            {
                y = ClothingToolWindow.PackItemRow(figure, def, null, null, view.width, y);
            }
            viewHeight = y;
            Widgets.EndScrollView();
        }
    }
}
