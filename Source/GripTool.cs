using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The grip tool: the player sets where the fist holds a weapon on its art, and how far the weapon is turned in it,
    // for any weapon (meant mostly for other mods' weapons drawn as their sprite's slab, whose grip the art can't always
    // tell, GripFinder). Kept per weapon defName in the mod settings (Settings.gripOverrides), so it holds for that weapon
    // in every game. DrawWeapon reads it each frame: the turn about the fist, then GripSlide's slide puts the fist on the
    // place set, whatever hold the weapon has (its own, its kind's, the slab's), so a change shows at once.
    //
    // Reached by a toggle among the play settings at the bottom right of the map (gripToolButton shows it there, the
    // mod options set both), which gives a pawn of the colony holding a weapon an Adjust grip button (Pawn.GetGizmos).
    public static partial class WalkFigures
    {
        private static Texture2D gripIcon;
        private static bool gripIconLooked;

        // The tool's icon (Textures/UI/M3AT_GripTool.png), white on clear as the play settings' are; null if missing.
        internal static Texture2D GripIcon
        {
            get
            {
                if (!gripIconLooked)
                {
                    gripIconLooked = true;
                    gripIcon = ContentFinder<Texture2D>.Get("UI/M3AT_GripTool", false);
                }
                return gripIcon ?? BaseContent.BadTex;
            }
        }

        internal static GripToolWindow gripTool;   // the open window, if any: DrawWeapon notes the fist for its pawn
        internal static Vector2 GripSeen;          // where that pawn's fist was last drawn on its weapon's art (0..1, y up)
        internal static int gripSeenFrame = -1000;

        // The player's grip for this weapon: x, y the fist's place on its art (x below 0: the grip left as it was), z the
        // turn in degrees. False for none.
        internal static bool GripOverrideOf(ThingDef def, out Vector3 own)
        {
            own = Vector3.zero;
#if AUTOTEST
            if (GripTest.HasValue && def != null)
            {
                own = GripTest.Value;   // M3AT_GRIP_TEST=u,v,turn: every weapon so held for the run, nothing saved
                return true;
            }
#endif
            Settings s = SettingsMod.Settings;
            return def != null && s != null && s.gripOverrides != null && s.gripOverrides.Count > 0
                && s.gripOverrides.TryGetValue(def.defName, out own);
        }

#if AUTOTEST
        private static readonly Vector3? GripTest = ReadGripTest();

        private static Vector3? ReadGripTest()
        {
            string[] v = (Environment.GetEnvironmentVariable("M3AT_GRIP_TEST") ?? "").Split(',');
            float u, w, t;
            if (v.Length == 3 && float.TryParse(v[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out u)
                && float.TryParse(v[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w)
                && float.TryParse(v[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t))
            {
                return new Vector3(u, w, t);
            }
            return null;
        }
#endif

        internal static Vector2 OffSeen = new Vector2(-1f, -1f);   // that pawn's other fist on the art, as last drawn; x below 0: none

        // Where the player set the other hand on this weapon's art (0..1, y up); false for none.
        internal static bool OffHandOf(ThingDef def, out Vector2 off)
        {
            off = Vector2.zero;
#if AUTOTEST
            if (OffHandTest.HasValue && def != null)
            {
                off = OffHandTest.Value;   // M3AT_OFFHAND_TEST=u,v: every weapon so held for the run, nothing saved
                return true;
            }
#endif
            Settings s = SettingsMod.Settings;
            return def != null && s != null && s.offHandGrips != null && s.offHandGrips.Count > 0
                && s.offHandGrips.TryGetValue(def.defName, out off);
        }

        // A hand's fist (model units, the whole move in): HandGrip along the hand bone from the wrist, as a hold on the hand
        // has it, else from the forearm's line.
        private static bool FistOf(SkinnedFigure.Posed fig, string side, out Vector3 fist)
        {
            Vector3 wrist, elbow, grip;
            Matrix4x4 turn;
            fist = Vector3.zero;
            if (!skin.JointOf(fig, "hand." + side, out wrist))
            {
                return false;
            }
            if (skin.BoneOf(fig, "hand." + side, out turn) && skin.GripOf(side, out grip))
            {
                fist = wrist + turn.MultiplyVector(grip).normalized * (skin.HandGrip >= 0f ? skin.HandGrip : HandGrip);
                return true;
            }
            if (!skin.JointOf(fig, "forearm." + side, out elbow))
            {
                return false;
            }
            fist = FistOf(wrist, elbow);
            return true;
        }

        // The other hand put where the player set it on the weapon's art (OffHandOf), between Chain and Finish: the weapon
        // placed as DrawWeapon will place it, that point of its art found, and the arm bent to it by IK (SkinnedFigure.Reach,
        // the hand keeping its turn), as far as the weapon is in the hands and the hand isn't wanted for anything else (a
        // reload, a shield, the hip gear, a cast, a blow, a stagger or daze, the dive). Not for a hold that runs through the
        // other fist (its line would move with the hand), nor with a load.
        private static void GripOffHand(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, Matrix4x4 whole, float size)
        {
            ThingWithComps weapon;
            Vector2 off;
            if (ease.stance == null || size <= 0f || (weapon = HeldWeapon(pawn)) == null || !OffHandOf(weapon.def, out off)
                || (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null) || Reloaded(pawn) != null)
            {
                return;
            }
            bool shown = ease.drawAt >= 0f ? DrawnShare(ease) > 0f : ease.drafted >= WeaponShown && WeaponShows(pawn);
            if (!shown)
            {
                return;
            }
            float w = Mathf.Clamp01(ease.drafted) * DrawnShare(ease) * (1f - Mathf.Clamp01(ease.shield)) * (1f - Mathf.Clamp01(ease.hipOut))
                * (1f - Mathf.Clamp01(ease.cast)) * (1f - Mathf.Clamp01(ease.reel)) * (1f - Mathf.Clamp01(ease.daze)) * (1f - Mathf.Clamp01(ease.duck));
            if (ease.strikeClip != null)
            {
                w *= 1f - Swing(ease.strikePhase, 1f);
            }
            if (w <= 0.001f)
            {
                return;
            }
            ReachOffHand(pawn, weapon, off, ease.stance, fig, whole, size, ease, w);
        }

        // The other arm bent so its fist is on that point (off, on the art) of the weapon as placed in this pose, w of the way.
        private static void ReachOffHand(Pawn pawn, ThingWithComps weapon, Vector2 off, string stance, SkinnedFigure.Posed fig, Matrix4x4 whole, float size, Ease ease, float w)
        {
            WeaponPlaced placed = PlaceWeapon(pawn, stance, fig, size, ease);
            Vector2 draw;
            if (placed == null || placed.hold.line == "hands" || !ArtDrawSize(weapon, size, out draw))
            {
                return;
            }
            Vector3 sx, sy;
            SpriteAxes(placed.a, placed.top, weapon.def.equippedAngleOffset, out sx, out sy);
            Vector3 middle = placed.hand + placed.a * (placed.hold.behind * Mathf.Max(draw.x, draw.y)) - (sx * placed.slide.x + sy * placed.slide.y);
            Vector3 point = middle + sx * ((off.x - 0.5f) * draw.x) + sy * ((off.y - 0.5f) * draw.y);
            string side = placed.left ? "R" : "L";
            Vector3 wrist, fist;
            if (!skin.JointOf(fig, "hand." + side, out wrist) || !FistOf(fig, side, out fist))
            {
                return;
            }
            skin.Reach(fig, whole, side, point - (fist - wrist), w);   // the wrist so the fist lands there, the hand's turn kept
            skin.Reached(fig, whole);
        }

        // For the grip tool's marks while the weapon isn't in the hands (not drafted): where both fists would be on its art
        // in its drafted stance, from a throwaway pose of the pawn (its own shape and size, the stance's first frame, the
        // other hand reached as set), never drawn. False if the stance or the hands can't be placed.
        internal static bool VirtualGrips(Pawn pawn, out Vector2 main, out Vector2 other)
        {
            main = other = new Vector2(-1f, -1f);
            ThingWithComps weapon = GripWeaponOf(pawn);
            string stance = weapon != null && skin != null ? DraftedClip(pawn) : null;
            if (stance == null || !skin.Has(stance))
            {
                return false;
            }
            float size = SizeOf(pawn);
            SkinnedFigure.Posed fig = skin.For(~pawn.thingIDNumber, ShapeFor(pawn));   // its own slot, apart from the pawn's drawn pose
            skin.Begin(fig);
            skin.Add(fig, stance, 0f, 1f);
            skin.Chain(fig, Matrix4x4.identity);
            Vector2 off;
            if (OffHandOf(weapon.def, out off))
            {
                ReachOffHand(pawn, weapon, off, stance, fig, Matrix4x4.identity, size, null, 1f);
            }
            WeaponPlaced placed = PlaceWeapon(pawn, stance, fig, size, null);
            Vector3 offFist;
            if (placed == null)
            {
                return false;
            }
            main = FistOnArt(weapon, placed.closed, placed.hand, placed.a, placed.top, placed.hold.behind, size, placed.slide);
            if (FistOf(fig, placed.left ? "R" : "L", out offFist))
            {
                other = FistOnArt(weapon, offFist, placed.hand, placed.a, placed.top, placed.hold.behind, size, placed.slide);
            }
            return true;
        }

#if AUTOTEST
        private static readonly Vector2? OffHandTest = ReadOffHandTest();

        private static Vector2? ReadOffHandTest()
        {
            string[] v = (Environment.GetEnvironmentVariable("M3AT_OFFHAND_TEST") ?? "").Split(',');
            float u, w;
            if (v.Length == 2 && float.TryParse(v[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out u)
                && float.TryParse(v[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w))
            {
                return new Vector2(u, w);
            }
            return null;
        }
#endif

        // The weapon the tool works on for a pawn: the one in its hands (reloading takes one out of the inventory), else
        // the equipped one.
        internal static ThingWithComps GripWeaponOf(Pawn pawn)
        {
            if (pawn == null || pawn.equipment == null)
            {
                return null;
            }
            return HeldWeapon(pawn) ?? pawn.equipment.Primary;
        }

        private static void PatchGripTool(Harmony harmony)
        {
            try
            {
                System.Reflection.MethodInfo play = AccessTools.Method(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls));
                if (play != null)
                {
                    harmony.Patch(play, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(PlaySettingsPostfix)));
                }
                System.Reflection.MethodInfo gizmos = AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos));
                if (gizmos != null)
                {
                    harmony.Patch(gizmos, postfix: new HarmonyMethod(typeof(WalkFigures), nameof(GizmosPostfix)));
                }
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "the grip tool is left out: " + e.Message);
            }
        }

        // The toggle among the map's play settings (after the game's own): the M3AT tools, grip and clothing, on or off.
        private static void PlaySettingsPostfix(WidgetRow row, bool worldView)
        {
            Settings s = SettingsMod.Settings;
            if (worldView || row == null || s == null || !s.gripToolButton || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            bool on = s.gripTool;
            row.ToggleableIcon(ref on, GripIcon, "M3AT_ToolsToggleTip".Translate(), SoundDefOf.Mouseover_ButtonToggle);
            if (on != s.gripTool)
            {
                s.gripTool = on;
                s.Write();
                if (!on && gripTool != null)
                {
                    gripTool.Close();
                }
                ClothingToolWindow open = on ? null : Find.WindowStack.WindowOfType<ClothingToolWindow>();
                if (open != null)
                {
                    open.Close();
                }
            }
        }

        // While the toggle is on: an Adjust grip button on a pawn of the colony with a weapon, and an Adjust clothing one on
        // a person wearing apparel drawn by a stand-in.
        private static IEnumerable<Gizmo> GizmosPostfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result)
            {
                yield return g;
            }
            Settings s = SettingsMod.Settings;
            if (s == null || !s.gripTool)
            {
                yield break;
            }
            if (__instance.Spawned && __instance.Faction == Faction.OfPlayer && GripWeaponOf(__instance) != null)
            {
                Pawn pawn = __instance;
                yield return new Command_Action
                {
                    defaultLabel = "M3AT_GripAdjust".Translate(),
                    defaultDesc = "M3AT_GripAdjustDesc".Translate(),
                    icon = GripIcon,
                    action = () => GripToolWindow.Open(pawn),
                };
            }
            // Adjust clothing on any person drawn wearing apparel with no garment of its own (ClothingTool.cs).
            if (__instance.Spawned && HasStandInApparel(__instance))
            {
                Pawn pawn = __instance;
                yield return new Command_Action
                {
                    defaultLabel = "M3AT_ClothesAdjust".Translate(),
                    defaultDesc = "M3AT_ClothesAdjustDesc".Translate(),
                    icon = ClothesIcon,
                    action = () => ClothingToolWindow.Open(pawn),
                };
            }
        }
    }

    // The window: the weapon's art, a click or drag on it puts the fist there, and a slider turns the weapon in the fist.
    // Follows the selected pawn. Not pausing, so the pawn shows the change at once behind it (drafted, to hold the weapon).
    public class GripToolWindow : Window
    {
        public Pawn pawn;
        private bool dragging;
        private bool changed;
        private bool offHand;   // a click places the other hand
        private int virtualFrame = -1;   // VirtualGrips' answer, kept for the frame
        private Pawn virtualPawn;
        private bool virtualSeen;
        private Vector2 virtualMain, virtualOff;

        private const float ArtBox = 320f;
        private const float TurnMost = 90f;   // degrees either way

        public override Vector2 InitialSize => new Vector2(380f, 700f);

        public static void Open(Pawn pawn)
        {
            GripToolWindow open = Find.WindowStack.WindowOfType<GripToolWindow>();
            if (open != null)
            {
                open.pawn = pawn;
                return;
            }
            Find.WindowStack.Add(new GripToolWindow { pawn = pawn });
        }

        public GripToolWindow()
        {
            draggable = true;
            resizeable = false;
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

        public override void PreOpen()
        {
            base.PreOpen();
            WalkFigures.gripTool = this;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (WalkFigures.gripTool == this)
            {
                WalkFigures.gripTool = null;
            }
            Save();
        }

        private void Save()
        {
            if (changed && SettingsMod.Settings != null)
            {
                SettingsMod.Settings.Write();
                changed = false;
            }
        }

        private static readonly Color MainSet = new Color(0.3f, 1f, 0.4f), MainSeen = new Color(1f, 0.85f, 0.2f);
        private static readonly Color OffSet = new Color(0.3f, 0.75f, 1f), OffSeenColour = new Color(1f, 0.45f, 0.9f);

        public override void DoWindowContents(Rect inRect)
        {
            // Another pawn of the colony with a weapon selected: work on it instead.
            Pawn selected = Find.Selector.SingleSelectedThing as Pawn;
            if (selected != null && selected != pawn && selected.Faction == Faction.OfPlayer && WalkFigures.GripWeaponOf(selected) != null)
            {
                pawn = selected;
                dragging = false;
            }
            ThingWithComps weapon = WalkFigures.GripWeaponOf(pawn);
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            Text.Font = GameFont.Medium;
            list.Label("M3AT_GripWindowTitle".Translate());
            Text.Font = GameFont.Small;
            Graphic g = weapon != null ? weapon.Graphic : null;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            if (weapon == null || mat == null || mat.mainTexture == null)
            {
                list.Label("M3AT_GripNoWeapon".Translate());
                list.End();
                return;
            }
            Dictionary<string, Vector3> grips = SettingsMod.Settings.gripOverrides;
            Dictionary<string, Vector2> offs = SettingsMod.Settings.offHandGrips;
            string key = weapon.def.defName;
            Vector3 own;
            if (!grips.TryGetValue(key, out own))
            {
                own = new Vector3(-1f, -1f, 0f);
            }
            Vector2 off;
            bool hasOff = offs.TryGetValue(key, out off);
            list.Label(pawn.LabelShortCap + ": " + weapon.def.LabelCap);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            list.Label(key + (weapon.def.modContentPack != null ? " (" + weapon.def.modContentPack.Name + ")" : ""));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            // Which hand a click places.
            Rect hands = list.GetRect(30f);
            if (Widgets.ButtonText(hands.LeftHalf().ContractedBy(2f), (offHand ? "" : "> ") + "M3AT_GripMainHand".Translate()))
            {
                offHand = false;
            }
            if (Widgets.ButtonText(hands.RightHalf().ContractedBy(2f), (offHand ? "> " : "") + "M3AT_GripOffHand".Translate()))
            {
                offHand = true;
            }

            // The art, as wide as it is drawn (its drawSize), in a box; each fist where it was last drawn and the places set.
            Rect box = list.GetRect(ArtBox);
            box.width = ArtBox;
            box.x = inRect.x + (inRect.width - ArtBox) * 0.5f;
            Widgets.DrawBoxSolidWithOutline(box, new Color(0.12f, 0.12f, 0.12f), new Color(0.35f, 0.35f, 0.35f));
            Vector2 shape = g.drawSize.x > 0f && g.drawSize.y > 0f ? g.drawSize : Vector2.one;
            float fit = (ArtBox - 8f) / Mathf.Max(shape.x, shape.y);
            Rect art = new Rect(0f, 0f, shape.x * fit, shape.y * fit);
            art.center = box.center;
            GUI.DrawTexture(art, mat.mainTexture);
            // Where the fists are: as drawn while the weapon is in the hands, else where its drafted stance would put them.
            bool live = Time.frameCount - WalkFigures.gripSeenFrame <= 5 && WalkFigures.gripTool == this;
            Vector2 mainAt = WalkFigures.GripSeen, offAt = WalkFigures.OffSeen;
            if (!live && (virtualFrame != Time.frameCount || virtualPawn != pawn))
            {
                virtualSeen = WalkFigures.VirtualGrips(pawn, out virtualMain, out virtualOff);   // once a frame, not per GUI event
                virtualFrame = Time.frameCount;
                virtualPawn = pawn;
            }
            if (!live)
            {
                mainAt = virtualMain;
                offAt = virtualOff;
            }
            bool seen = live || virtualSeen;
            if (seen)
            {
                if (mainAt.x > -0.5f)
                {
                    Mark(art, mainAt, MainSeen, 9f);
                }
                if (offAt.x > -0.5f)
                {
                    Mark(art, offAt, OffSeenColour, 9f);
                }
            }
            if (own.x >= 0f)
            {
                Mark(art, new Vector2(own.x, own.y), MainSet, 5f);
            }
            if (hasOff)
            {
                Mark(art, off, OffSet, 5f);
            }
            Event e = Event.current;
            if (e.type == EventType.MouseUp)
            {
                dragging = false;
            }
            if ((e.type == EventType.MouseDown && e.button == 0 && box.Contains(e.mousePosition)) || (e.type == EventType.MouseDrag && dragging))
            {
                dragging = true;
                Vector2 uv = new Vector2(Mathf.Clamp01((e.mousePosition.x - art.x) / art.width), Mathf.Clamp01(1f - (e.mousePosition.y - art.y) / art.height));
                if (offHand)
                {
                    offs[key] = off = uv;
                    hasOff = true;
                }
                else
                {
                    own.x = uv.x;
                    own.y = uv.y;
                    grips[key] = own;
                }
                changed = true;
                e.Use();
            }
            list.Gap(4f);
            Text.Font = GameFont.Tiny;
            if (seen)
            {
                Legend(list, MainSeen, live ? "M3AT_GripSeen".Translate() : "M3AT_GripSeenStance".Translate());
                Legend(list, OffSeenColour, live ? "M3AT_GripOffSeen".Translate() : "M3AT_GripOffSeenStance".Translate());
            }
            else
            {
                Legend(list, MainSeen, "M3AT_GripNotSeen".Translate());
            }
            Legend(list, MainSet, own.x >= 0f ? "M3AT_GripStateOwn".Translate() : "M3AT_GripStateAuto".Translate());
            Legend(list, OffSet, hasOff ? "M3AT_GripOffOwn".Translate() : "M3AT_GripOffAuto".Translate());
            Text.Font = GameFont.Small;

            // The turn in the fist.
            list.Gap(4f);
            list.Label("M3AT_GripTurn".Translate(own.z.ToString("0")));
            float turned = Widgets.HorizontalSlider(list.GetRect(22f), own.z, -TurnMost, TurnMost, true, null, null, null, 1f);
            Rect nudge = list.GetRect(28f);
            if (Widgets.ButtonText(nudge.LeftHalf().ContractedBy(2f), "-1°"))
            {
                turned = Mathf.Max(-TurnMost, own.z - 1f);
            }
            if (Widgets.ButtonText(nudge.RightHalf().ContractedBy(2f), "+1°"))
            {
                turned = Mathf.Min(TurnMost, own.z + 1f);
            }
            if (turned != own.z)
            {
                own.z = turned;
                grips[key] = own;
                changed = true;
            }

            list.Gap(6f);
            Rect buttons = list.GetRect(30f);
            if (Widgets.ButtonText(buttons.LeftHalf().ContractedBy(2f), offHand ? "M3AT_GripOffUsual".Translate() : "M3AT_GripAuto".Translate()))
            {
                if (offHand)
                {
                    changed |= offs.Remove(key);
                }
                else if (grips.ContainsKey(key))
                {
                    own.x = own.y = -1f;
                    grips[key] = own;
                    changed = true;
                }
            }
            if (Widgets.ButtonText(buttons.RightHalf().ContractedBy(2f), "M3AT_GripResetTurn".Translate()) && grips.ContainsKey(key))
            {
                own.z = 0f;
                grips[key] = own;
                changed = true;
            }
            if (list.ButtonText("M3AT_GripRestoreDefault".Translate()))
            {
                changed |= grips.Remove(key) | offs.Remove(key);
            }
            // Nothing of the player's left: no entry at all.
            if (grips.TryGetValue(key, out own) && own.x < 0f && own.z == 0f)
            {
                grips.Remove(key);
            }
            if (!Input.GetMouseButton(0))
            {
                Save();   // once the mouse is let go, not every frame of a drag
            }
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            list.Label("M3AT_GripHelp".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            list.End();
        }

        // A line of the key under the art: a swatch of the mark's colour and what it means.
        private static void Legend(Listing_Standard list, Color colour, string text)
        {
            float h = Text.CalcHeight(text, list.ColumnWidth - 16f);
            Rect r = list.GetRect(h);
            Widgets.DrawBoxSolid(new Rect(r.x + 2f, r.y + 3f, 8f, 8f), colour);
            Widgets.Label(new Rect(r.x + 16f, r.y, r.width - 16f, h), text);
        }

        // A cross with a box round it at a place on the art (0..1, y up).
        private static void Mark(Rect art, Vector2 uv, Color colour, float half)
        {
            Vector2 at = new Vector2(art.x + uv.x * art.width, art.y + (1f - uv.y) * art.height);
            Widgets.DrawLine(at - new Vector2(half, 0f), at + new Vector2(half, 0f), colour, 2f);
            Widgets.DrawLine(at - new Vector2(0f, half), at + new Vector2(0f, half), colour, 2f);
            Rect ring = new Rect(at.x - half * 0.6f, at.y - half * 0.6f, half * 1.2f, half * 1.2f);
            GUI.color = colour;
            Widgets.DrawBox(ring, 1);
            GUI.color = Color.white;
        }
    }
}
