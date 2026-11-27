using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The clothing tool: apparel with no garment of its own (another mod's, mostly) is drawn as the garments that stand in
    // for it best (StandIns.cs), and where that goes wrong the player picks others, or none, by eye. Kept per apparel
    // defName in the mod settings (Settings.clothingChoices), so it holds for that apparel in every game and on every pawn.
    // StandInsFor reads it first, so the choice shows at once on the pawn behind the window, and on the ground with the
    // groundGarments option.
    //
    // Reached through the same toggle as the grip tool (bottom right of the map): it gives a person wearing such apparel
    // an Adjust clothing button.
    public static partial class WalkFigures
    {
        public const string NoneDrawn = "-";   // a choice of no garment: the apparel isn't drawn
        public const string M3atAutomatic = "*";   // a choice of M3AT's automatic match over a content pack's own garment (no garment has this name)

        private static Texture2D clothesIcon;
        private static bool clothesIconLooked;

        // The button's icon: the game's basic shirt, else the grip tool's.
        internal static Texture2D ClothesIcon
        {
            get
            {
                if (!clothesIconLooked)
                {
                    clothesIconLooked = true;
                    ThingDef shirt = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_BasicShirt");
                    clothesIcon = shirt != null ? shirt.uiIcon : null;
                }
                return clothesIcon ?? GripIcon;
            }
        }

        // The player's choice of garments for this apparel, if any (false: none, the scoring's stands). A choice naming no
        // garment this export has any more is taken as none made.
        internal static bool ChosenStandIns(SkinnedFigure figure, ThingDef def, out int[] chosen)
        {
            chosen = null;
            Settings s = SettingsMod.Settings;
            string names;
            if (s == null || s.clothingChoices == null || s.clothingChoices.Count == 0 || !s.clothingChoices.TryGetValue(def.defName, out names))
            {
                return false;
            }
            if (string.IsNullOrEmpty(names) || names == NoneDrawn)
            {
                chosen = new int[0];
                return true;
            }
            List<int> found = new List<int>(2);
            foreach (string name in names.Split(','))
            {
                int g = figure.GarmentNamed(name.Trim());
                if (g > 0 && !found.Contains(g))
                {
                    found.Add(g);
                }
            }
            if (found.Count == 0)
            {
                return false;
            }
            chosen = found.ToArray();
            return true;
        }

        // Whether this apparel's stand-ins are painted with its own art: the spriteClothes option, less the apparel the
        // player has left plain in the clothing tool (Settings.plainClothes).
        internal static bool PaintedArt(ThingDef def)
        {
            Settings s = SettingsMod.Settings;
            if (s == null)
            {
                return true;
            }
            return s.spriteClothes && (s.plainClothes == null || s.plainClothes.Count == 0 || !s.plainClothes.Contains(def.defName));
        }

        // Whether this garment drawn for this apparel is painted with the apparel's art: a stand-in as PaintedArt says; a
        // content pack's garment only if the player ticked it (Settings.paintedPackClothes), as it has an image of its own.
        internal static bool PaintedArtFor(int g, ThingDef def)
        {
            if (skin == null || !skin.IsPackGarment(g))
            {
                return PaintedArt(def);
            }
            Settings s = SettingsMod.Settings;
            return s != null && s.spriteClothes && s.paintedPackClothes != null && s.paintedPackClothes.Contains(def.defName);
        }

#if AUTOTEST
        internal static int ClothPaintedDraws;   // garments drawn painted with their apparel's art (the plainclothes test)
#endif

        // What content packs draw changed (a benchmark turn, a player's choice: AddOns.cs): what each pawn wears worked out
        // again (a pack's garments may be off). The apparel on the ground keeps what it was baked with.
        internal static void AddOnsChanged()
        {
            wornCaches.Clear();
        }

        // The triangles drawn for a person's body and clothes, as last dressed (the worn cache; 0 if not drawn yet), for
        // the content switcher's totals.
        internal static int DrawnTriangles(Pawn pawn)
        {
            WornCache c;
            if (!ready || skin == null || pawn == null || !wornCaches.TryGetValue(pawn.thingIDNumber, out c) || c.worn == null)
            {
                return 0;
            }
            return skin.DrawnTriangles(c.mask, c.worn);
        }

        // The chibi, for AddOns.Find: null until it has loaded.
        internal static SkinnedFigure SkinForAddOns
        {
            get { return ready ? skin : null; }
        }

        // A choice changed: every pawn's garments are worked out again, and the apparel on the ground baked again if it is
        // drawn as garments.
        internal static void ClothingChoicesChanged()
        {
            wornCaches.Clear();
            if (GroundGarments)
            {
                RebakeGroundClothes();
            }
        }

        // The apparel a pawn wears that has no garment of its own and isn't IsoRim's worn gear: what the tool works on.
        internal static List<Apparel> StandInApparel(Pawn pawn)
        {
            List<Apparel> found = new List<Apparel>();
            if (skin == null || pawn == null || pawn.apparel == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return found;
            }
            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (worn[i].def.apparel != null && (skin.GarmentOf(worn[i].def) < 0 || skin.PackGarmentOf(worn[i].def) > 0) && GearMountOf(worn[i]) == null)
                {
                    found.Add(worn[i]);   // drawn by stand-ins, or a content pack's garment (or M3AT's in its place)
                }
            }
            return found;
        }

        // Whether M3AT look has anything for this person: apparel drawn by stand-ins or a content pack's garments, or a
        // content pack for its race.
        internal static bool HasStandInApparel(Pawn pawn)
        {
            if (skin == null || pawn == null || pawn.apparel == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return false;
            }
            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (worn[i].def.apparel != null && (skin.GarmentOf(worn[i].def) < 0 || skin.PackGarmentOf(worn[i].def) > 0) && GearMountOf(worn[i]) == null)
                {
                    return true;
                }
            }
            return ready && ContentSources.Packs().Any(a => pawn.def != null && a.races.Contains(pawn.def.defName));
        }
    }

    // The window: each piece of apparel the selected pawn wears drawn by stand-ins, what it is drawn as, and buttons to pick
    // other garments, none, or go back to the automatic match. Follows the selected pawn and doesn't pause, so the change
    // shows on it at once.
    // M3AT look: for the selected person, the content packs for its race or clothes (ContentView), then its clothing.
    public class ClothingToolWindow : Window
    {
        public Pawn pawn;
        private Vector2 scroll;
        private float viewHeight;
        private readonly ContentView content = new ContentView();

        public override Vector2 InitialSize => new Vector2(Mathf.Min(900f, UI.screenWidth - 40f), Mathf.Min(720f, UI.screenHeight - 100f));

        public static void Open(Pawn pawn)
        {
            ClothingToolWindow open = Find.WindowStack.WindowOfType<ClothingToolWindow>();
            if (open != null)
            {
                open.pawn = pawn;
                return;
            }
            Find.WindowStack.Add(new ClothingToolWindow { pawn = pawn });
        }

        public ClothingToolWindow()
        {
            draggable = true;
            resizeable = true;
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
            Pawn selected = Find.Selector.SingleSelectedThing as Pawn;
            if (selected != null && selected != pawn && WalkFigures.HasStandInApparel(selected))
            {
                pawn = selected;
            }
            SkinnedFigure figure = WalkFigures.Skin;
            Text.Font = GameFont.Medium;
            Rect title = new Rect(inRect.x, inRect.y, inRect.width, 34f);
            Widgets.Label(title, "M3AT_LookTitle".Translate());
            Text.Font = GameFont.Small;
            List<Apparel> apparel = WalkFigures.StandInApparel(pawn);
            float y = title.yMax + 4f;
            if (figure == null || pawn == null || !WalkFigures.HasStandInApparel(pawn))
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 60f), "M3AT_ClothesNoPawn".Translate());
                return;
            }
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), pawn.LabelShortCap);
            y += 26f;

            const float HelpHeight = 64f;
            Rect outer = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - HelpHeight);
            Rect view = new Rect(0f, 0f, outer.width - 16f, Mathf.Max(viewHeight, outer.height));
            Widgets.BeginScrollView(outer, ref scroll, view);
            // The pack radios on the left and the clothing items beside them on the right, in one scroll.
            float left = Mathf.Min(420f, view.width * 0.5f), gap = 10f;
            float at = content.Draw(new Rect(0f, 0f, left, 100000f), pawn, false);
            float right = 0f;
            GUI.BeginGroup(new Rect(left + gap, 0f, view.width - left - gap, 100000f));
            for (int i = 0; i < apparel.Count; i++)
            {
                right = figure.PackGarmentOf(apparel[i].def) > 0
                    ? PackItemRow(figure, apparel[i].def, apparel[i], pawn, view.width - left - gap, right)
                    : Row(figure, apparel[i], view.width - left - gap, right);
            }
            GUI.EndGroup();
            at = Mathf.Max(at, right);
            viewHeight = at;
            Widgets.EndScrollView();

            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inRect.x, inRect.yMax - HelpHeight + 4f, inRect.width, HelpHeight - 4f), "M3AT_ClothesHelp".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        // One apparel's row: its name and mod, what it is drawn as now, and the buttons. Returns the height used to.
        // A skirt or dress shows the length its picture comes to on this pawn (HemLengths.cs), as drawn.
        private float Row(SkinnedFigure figure, Apparel apparel, float width, float y)
        {
            ThingDef def = apparel.def;
            Dictionary<string, string> choices = SettingsMod.Settings.clothingChoices;
            bool own = choices.ContainsKey(def.defName);
            int[] now = figure.StandInsOn(apparel, pawn);
            int[] auto = figure.AutoStandInsOn(apparel, pawn);

            // With the stand-ins painted with the apparel's art, a box to leave this one plain, if any garment drawn for it
            // can take the art at all.
            bool paintable = false;
            if (WalkFigures.SpriteClothes)
            {
                for (int i = 0; i < now.Length; i++)
                {
                    paintable |= figure.PaintsArt(now[i], apparel);
                }
            }
            Rect box = new Rect(0f, y, width, paintable ? 122f : 96f);
            Widgets.DrawBoxSolidWithOutline(box, new Color(0.13f, 0.13f, 0.13f), new Color(0.3f, 0.3f, 0.3f));
            Rect inner = box.ContractedBy(6f);
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 22f), def.LabelCap);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x, inner.y + 20f, inner.width, 16f),
                def.defName + (def.modContentPack != null ? " (" + def.modContentPack.Name + ")" : ""));
            GUI.color = own ? new Color(0.5f, 1f, 0.55f) : Color.white;
            string drawn = Names(figure, now);
            Widgets.Label(new Rect(inner.x, inner.y + 36f, inner.width, 18f),
                own ? (now.Length > 0 ? "M3AT_ClothesChosen".Translate(drawn) : "M3AT_ClothesChosenNone".Translate())
                    : (now.Length > 0 ? "M3AT_ClothesAuto".Translate(drawn) : "M3AT_ClothesAutoNone".Translate()));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (paintable)
            {
                List<string> plain = SettingsMod.Settings.plainClothes;
                bool painted = !plain.Contains(def.defName);
                bool was = painted;
                Rect check = new Rect(inner.x, inner.y + 56f, inner.width, 24f);
                Widgets.CheckboxLabeled(check, "M3AT_ClothesPaint".Translate(), ref painted);
                TooltipHandler.TipRegion(check, "M3AT_ClothesPaintTip".Translate());
                if (painted != was)
                {
                    if (painted)
                    {
                        plain.Remove(def.defName);
                    }
                    else
                    {
                        plain.Add(def.defName);
                    }
                    SettingsMod.Settings.Write();
                }
            }

            Rect buttons = new Rect(inner.x, inner.yMax - 28f, inner.width, 28f);
            float third = buttons.width / 3f;
            if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, third - 4f, 28f), "M3AT_ClothesChoose".Translate()))
            {
                Find.WindowStack.Add(new FloatMenu(Menu(figure, def, auto, null)));
            }
            if (now.Length > 0 && now.Length < 3 && Widgets.ButtonText(new Rect(buttons.x + third, buttons.y, third - 4f, 28f), "M3AT_ClothesAdd".Translate()))
            {
                Find.WindowStack.Add(new FloatMenu(Menu(figure, def, auto, now)));
            }
            if (own && Widgets.ButtonText(new Rect(buttons.x + third * 2f, buttons.y, third - 4f, 28f), "M3AT_ClothesReset".Translate()))
            {
                Set(def, null);
            }
            return box.yMax + 6f;
        }

        // The garments to pick from: with keep (the garments drawn now) one to draw beside them, else the automatic match,
        // none, then one in place of what is drawn. The automatic picks first, the rest by name.
        private List<FloatMenuOption> Menu(SkinnedFigure figure, ThingDef def, int[] auto, int[] keep)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            if (keep == null)
            {
                options.Add(new FloatMenuOption("M3AT_ClothesMenuAuto".Translate(auto.Length > 0 ? Names(figure, auto) : (string)"M3AT_ClothesNotDrawn".Translate()),
                    () => Set(def, null)));
                options.Add(new FloatMenuOption("M3AT_ClothesMenuNone".Translate(), () => Set(def, WalkFigures.NoneDrawn)));
            }
            List<int> garments = figure.ChoosableGarments();
            Dictionary<string, int> sameLabel = new Dictionary<string, int>();
            for (int i = 0; i < garments.Count; i++)
            {
                string label = figure.GarmentLabel(garments[i]);
                int n;
                sameLabel[label] = sameLabel.TryGetValue(label, out n) ? n + 1 : 1;
            }
            garments.Sort((a, b) =>
            {
                bool ia = Array.IndexOf(auto, a) >= 0, ib = Array.IndexOf(auto, b) >= 0;
                return ia != ib ? (ia ? -1 : 1) : string.Compare(figure.GarmentLabel(a), figure.GarmentLabel(b), StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < garments.Count; i++)
            {
                int g = garments[i];
                if (keep != null && Array.IndexOf(keep, g) >= 0)
                {
                    continue;
                }
                string label = figure.GarmentLabel(g);
                if (sameLabel[label] > 1)
                {
                    label += " (" + figure.PieceName(g) + ")";
                }
                string names = keep == null ? figure.PieceName(g) : NamesOf(figure, keep) + "," + figure.PieceName(g);
                options.Add(new FloatMenuOption(label, () => Set(def, names)));
            }
            return options;
        }

        private static string Names(SkinnedFigure figure, int[] garments)
        {
            List<string> labels = new List<string>(garments.Length);
            for (int i = 0; i < garments.Length; i++)
            {
                labels.Add(figure.GarmentLabel(garments[i]));
            }
            return string.Join(" + ", labels);
        }

        private static string NamesOf(SkinnedFigure figure, int[] garments)
        {
            List<string> names = new List<string>(garments.Length);
            for (int i = 0; i < garments.Length; i++)
            {
                names.Add(figure.PieceName(garments[i]));
            }
            return string.Join(",", names);
        }

        // A row for apparel a content pack has a garment for: drawn as the pack's garment or M3AT's (its own garment, or
        // stand-ins), the pack's painted with the apparel's art if ticked, and a menu to choose. apparel and pawn may be
        // null (the pack's clothing list, worn by no one).
        internal static float PackItemRow(SkinnedFigure figure, ThingDef def, Apparel apparel, Pawn pawn, float width, float y)
        {
            int packG = figure.PackGarmentOf(def);
            int drawn = figure.GarmentOf(def);
            bool packDrawn = drawn == packG;
            Settings s = SettingsMod.Settings;
            bool own = s.clothingChoices.ContainsKey(def.defName);
            string owner = figure.OwnerOf(packG);
            bool packByDefault = ContentSources.UsesPack(owner, ContentSources.ClothingPartOf(def));
            int[] m3at = figure.OwnGarmentOf(def) > 0 ? new[] { figure.OwnGarmentOf(def) }
                : apparel != null && pawn != null ? figure.AutoStandInsOn(apparel, pawn) : figure.AutoStandInsFor(def);
            int[] now = packDrawn ? new[] { packG } : drawn > 0 ? new[] { drawn }
                : apparel != null && pawn != null ? figure.StandInsOn(apparel, pawn) : figure.StandInsFor(def);
            bool paintable = packDrawn && WalkFigures.SpriteClothes && figure.PaintsArt(packG, def);
            // The paint tick sits in the box's top right corner, beside the name, so showing or hiding it leaves the row's
            // height alone.
            Rect box = new Rect(0f, y, width, 114f);
            Widgets.DrawBoxSolidWithOutline(box, new Color(0.13f, 0.13f, 0.13f), new Color(0.3f, 0.3f, 0.3f));
            Rect inner = box.ContractedBy(6f);
            string paintLabel = "M3AT_PackPaint".Translate();
            float paintWidth = paintable ? Text.CalcSize(paintLabel).x + 34f : 0f;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width - paintWidth, 22f), def.LabelCap);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inner.x, inner.y + 20f, inner.width, 16f), def.defName + (def.modContentPack != null ? " (" + def.modContentPack.Name + ")" : ""));
            GUI.color = own ? new Color(0.5f, 1f, 0.55f) : Color.white;
            string what = packDrawn ? (string)"M3AT_ItemPack".Translate()
                : now.Length > 0 ? (string)"M3AT_ItemM3AT".Translate(Names(figure, now)) : (string)"M3AT_ItemNone".Translate();
            Widgets.Label(new Rect(inner.x, inner.y + 36f, inner.width, 18f), own ? (string)"M3AT_ClothesChosen".Translate(what) : (string)"M3AT_ItemDefault".Translate(what));
            // The pack's garment's cost: its triangles against the budget, coloured, M3AT's equivalent for scale.
            Rect cost = new Rect(inner.x, inner.y + 54f, inner.width, 18f);
            GUI.color = Costs.Band(figure.MadeTrianglesOf(packG), figure.BudgetOf(packG));
            Widgets.Label(cost, Costs.Tris(figure, packG));
            TooltipHandler.TipRegion(cost, Costs.TrisTip(figure, packG, m3at));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (paintable)
            {
                List<string> painted = s.paintedPackClothes;
                bool on = painted.Contains(def.defName), was = on;
                Rect check = new Rect(inner.xMax - paintWidth, inner.y, paintWidth, 24f);
                Widgets.CheckboxLabeled(check, paintLabel, ref on);
                TooltipHandler.TipRegion(check, "M3AT_PackPaintTip".Translate());
                if (on != was)
                {
                    if (on)
                    {
                        painted.Add(def.defName);
                    }
                    else
                    {
                        painted.Remove(def.defName);
                    }
                    s.Write();
                    WalkFigures.ClothingChoicesChanged();
                }
            }
            // Three radio cells, one click each: the pack's garment, M3AT's automatic match, or a garment of the player's
            // choosing (Custom, a menu, again while on to choose another).
            string packName = figure.PieceName(packG);
            string choice;
            s.clothingChoices.TryGetValue(def.defName, out choice);
            bool custom = own && choice != packName && choice != WalkFigures.M3atAutomatic;
            Rect cells = new Rect(inner.x, inner.yMax - 28f, inner.width, 28f);
            float third = cells.width / 3f;
            if (ContentView.RadioCell(new Rect(cells.x, cells.y, third - 2f, 28f), packDrawn && !custom, "M3AT_SourcePack".Translate(), packByDefault,
                "M3AT_ItemCellPackTip".Translate()) && !(packDrawn && !custom))
            {
                Set(def, packByDefault ? null : packName);
            }
            bool m3atOn = !packDrawn && !custom;
            if (ContentView.RadioCell(new Rect(cells.x + third, cells.y, third - 2f, 28f), m3atOn, "M3AT_SourceM3AT".Translate(), !packByDefault,
                "M3AT_ItemCellM3ATTip".Translate(m3at.Length > 0 ? Names(figure, m3at) : (string)"M3AT_ClothesNotDrawn".Translate())) && !m3atOn)
            {
                Set(def, packByDefault ? WalkFigures.M3atAutomatic : null);
            }
            string customLabel = custom ? (now.Length > 0 ? Names(figure, now) : (string)"M3AT_ItemNone".Translate()) : (string)"M3AT_ItemCustom".Translate();
            if (ContentView.RadioCell(new Rect(cells.x + third * 2f, cells.y, third - 2f, 28f), custom, customLabel, false,
                custom ? (string)"M3AT_ItemCellCustomOnTip".Translate(customLabel) : (string)"M3AT_ItemCellCustomTip".Translate()))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>
                {
                    new FloatMenuOption("M3AT_ClothesMenuNone".Translate(), () => Set(def, WalkFigures.NoneDrawn)),
                };
                foreach (int g in figure.ChoosableGarments().OrderBy(g => figure.GarmentLabel(g)))
                {
                    string name = figure.PieceName(g);
                    if (g != packG)
                    {
                        options.Add(new FloatMenuOption(figure.GarmentLabel(g) + " (" + name + ")", () => Set(def, name)));
                    }
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            return box.yMax + 6f;
        }

        // A pack's every item set to its garment (pack) or M3AT's, one write, for the clothing list's column heads.
        internal static void SetAllItems(SkinnedFigure figure, IEnumerable<ThingDef> defs, bool pack)
        {
            Settings s = SettingsMod.Settings;
            foreach (ThingDef def in defs)
            {
                int packG = figure.PackGarmentOf(def);
                if (packG <= 0)
                {
                    continue;
                }
                bool packByDefault = ContentSources.UsesPack(figure.OwnerOf(packG), ContentSources.ClothingPartOf(def));
                string want = pack == packByDefault ? null : pack ? figure.PieceName(packG) : WalkFigures.M3atAutomatic;
                if (want == null)
                {
                    s.clothingChoices.Remove(def.defName);
                }
                else
                {
                    s.clothingChoices[def.defName] = want;
                }
            }
            s.Write();
            WalkFigures.ClothingChoicesChanged();
        }

        // Sets the choice (null: back to the automatic match) and saves it at once, as a pick is one click.
        internal static void Set(ThingDef def, string names)
        {
            Settings s = SettingsMod.Settings;
            if (names == null)
            {
                s.clothingChoices.Remove(def.defName);
            }
            else
            {
                s.clothingChoices[def.defName] = names;
            }
            s.Write();
            WalkFigures.ClothingChoicesChanged();
        }
    }
}
