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
        private const float ButtonShare = 0.42f;   // of a row's width, its source button
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
                list.Label("M3AT_ContentTitle".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.gray;
                list.Label("M3AT_ContentIntro".Translate());
                GUI.color = Color.white;
                Settings s = SettingsMod.Settings;
                if (s != null)
                {
                    bool was = s.contentButton;
                    list.CheckboxLabeled("M3AT_ContentButton".Translate(), ref s.contentButton, "M3AT_ContentButtonTip".Translate());
                    if (was != s.contentButton)
                    {
                        s.Write();
                    }
                }
                list.GapLine(8f);
            }
            if (packs == null)
            {
                list.Label("M3AT_ContentNotLoaded".Translate());
            }
            else if (packs.Count == 0)
            {
                if (pawn == null)
                {
                    list.Label("M3AT_ContentNone".Translate());
                }
            }
            else
            {
                foreach (AddOns.AddOn a in packs)
                {
                    Pack(list, a, pawn);
                }
            }
            float used = list.CurHeight;
            list.End();
            return used;
        }

        // A pack's header and a row per part.
        private static void Pack(Listing_Standard list, AddOns.AddOn a, Pawn pawn)
        {
            Text.Font = GameFont.Medium;
            list.Label(a.name);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            list.Label("M3AT_ContentSummary".Translate(a.packageId, a.garments, a.races.Count > 0 ? string.Join(", ", a.races.Select(RaceLabel)) : "-"));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            bool anyChosen = false;
            foreach (ContentPart part in ContentSources.Shown)
            {
                if (!a.parts.Contains(part))
                {
                    continue;
                }
                anyChosen |= ContentSources.Chosen(a.packageId, part) != null;
                PartRow(list, a, part);
            }
            if (anyChosen)
            {
                Rect reset = list.GetRect(24f);
                if (Widgets.ButtonText(new Rect(reset.xMax - 160f, reset.y, 160f, 24f), "M3AT_ContentReset".Translate()))
                {
                    ContentSources.Reset(a.packageId);
                }
            }
            list.GapLine(10f);
        }

        private static void PartRow(Listing_Standard list, AddOns.AddOn a, ContentPart part)
        {
            Rect row = list.GetRect(RowHeight);
            Widgets.DrawHighlightIfMouseover(row);
            TooltipHandler.TipRegion(row, ("M3AT_PartTip_" + part).Translate());
            float buttonWidth = row.width * ButtonShare;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.x + 4f, row.y, row.width - buttonWidth - 8f, row.height), ("M3AT_Part_" + part).Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            bool pack = ContentSources.UsesPack(a.packageId, part);
            bool chosen = ContentSources.Chosen(a.packageId, part) != null;
            bool byDefault = ContentSources.Default(a.packageId, part);
            string now = (pack ? "M3AT_SourcePack" : "M3AT_SourceM3AT").Translate();
            Rect button = new Rect(row.xMax - buttonWidth, row.y + 2f, buttonWidth, row.height - 4f);
            if (part == ContentPart.Clothing)
            {
                float half = buttonWidth / 2f;
                button.width = half - 4f;
                if (Widgets.ButtonText(new Rect(row.xMax - half, row.y + 2f, half, row.height - 4f), "M3AT_ContentItems".Translate(a.garments)))
                {
                    PackClothesWindow.Open(a);
                }
                button.x = row.xMax - buttonWidth;
            }
            if (Widgets.ButtonText(button, chosen ? now : (string)"M3AT_SourceDefault".Translate(now)))
            {
                string id = a.packageId;
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("M3AT_SourcePack".Translate(), () => ContentSources.Set(id, part, true)),
                    new FloatMenuOption("M3AT_SourceM3AT".Translate(), () => ContentSources.Set(id, part, false)),
                };
                if (chosen)
                {
                    options.Add(new FloatMenuOption("M3AT_SourceMenuDefault".Translate((byDefault ? "M3AT_SourcePack" : "M3AT_SourceM3AT").Translate()),
                        () => ContentSources.Set(id, part, null)));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            string hint = Hint(a, part, pack);
            if (hint != null)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                list.Label(hint);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
        }

        // What the other source would be like, where it helps to know.
        private static string Hint(AddOns.AddOn a, ContentPart part, bool pack)
        {
            switch (part)
            {
                case ContentPart.Face:
                    return !pack && a.races.Count > 0 && !a.races.Any(ContentSources.FacialAnimationHas) ? (string)"M3AT_HintNoFace".Translate() : null;
                case ContentPart.EarsTail:
                    return !pack ? (string)"M3AT_HintBuiltEars".Translate() : null;
                case ContentPart.Clothing:
                    return pack ? (string)"M3AT_HintArmour".Translate() : null;
                default:
                    return null;
            }
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

    // The M3AT button on the bottom bar: shown while content packs are active and the contentButton option is on.
    public class ContentButtonWorker : MainButtonWorker_ToggleTab
    {
        public override bool Visible
        {
            get
            {
                Settings s = SettingsMod.Settings;
                return base.Visible && (s == null || s.contentButton) && WalkFigures.SkinForAddOns != null && ContentSources.Packs().Count > 0;
            }
        }
    }

    // Its window, over the map, which isn't paused, so a switch shows on the pawns at once.
    public class ContentTabWindow : MainTabWindow
    {
        private readonly ContentView view = new ContentView();

        public override Vector2 RequestedTabSize => new Vector2(560f, Mathf.Min(720f, UI.screenHeight - 120f));

        public override void DoWindowContents(Rect inRect)
        {
            view.Draw(inRect, null, true);
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
            Rect outer = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
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
