using System;
using System.Collections.Generic;
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
                if (worn[i].def.apparel != null && skin.GarmentOf(worn[i].def) < 0 && GearMountOf(worn[i]) == null)
                {
                    found.Add(worn[i]);
                }
            }
            return found;
        }

        internal static bool HasStandInApparel(Pawn pawn)
        {
            if (skin == null || pawn == null || pawn.apparel == null || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return false;
            }
            List<Apparel> worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (worn[i].def.apparel != null && skin.GarmentOf(worn[i].def) < 0 && GearMountOf(worn[i]) == null)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // The window: each piece of apparel the selected pawn wears drawn by stand-ins, what it is drawn as, and buttons to pick
    // other garments, none, or go back to the automatic match. Follows the selected pawn and doesn't pause, so the change
    // shows on it at once.
    public class ClothingToolWindow : Window
    {
        public Pawn pawn;
        private Vector2 scroll;
        private float viewHeight;

        public override Vector2 InitialSize => new Vector2(420f, 520f);

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
            Widgets.Label(title, "M3AT_ClothesWindowTitle".Translate());
            Text.Font = GameFont.Small;
            List<Apparel> apparel = WalkFigures.StandInApparel(pawn);
            float y = title.yMax + 4f;
            if (figure == null || apparel.Count == 0)
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
            float at = 0f;
            for (int i = 0; i < apparel.Count; i++)
            {
                at = Row(figure, apparel[i], view.width, at);
            }
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

            Rect box = new Rect(0f, y, width, 96f);
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

        // Sets the choice (null: back to the automatic match) and saves it at once, as a pick is one click.
        private static void Set(ThingDef def, string names)
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
