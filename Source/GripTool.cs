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

        private static Texture2D toolsIcon;
        private static bool toolsIconLooked;

        // The play settings toggle's icon: the mod's fox-eared head (Textures/UI/M3AT_ToolsToggle.png, made from the mod icon by
        // anim_scripts/make_tools_icon.py), white on clear; the grip icon if missing.
        internal static Texture2D ToolsIcon
        {
            get
            {
                if (!toolsIconLooked)
                {
                    toolsIconLooked = true;
                    toolsIcon = ContentFinder<Texture2D>.Get("UI/M3AT_ToolsToggle", false);
                }
                return toolsIcon ?? GripIcon;
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
            // (or the file <config>/M3AT/grip_test holding the same: a game started through Steam gets no environment)
            string file = System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "M3AT", "grip_test");
            string[] v = (Environment.GetEnvironmentVariable("M3AT_GRIP_TEST")
                ?? (System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file).Trim() : "")).Split(',');
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
                fist = wrist + turn.MultiplyVector(grip).normalized * (skin.HandGrip >= 0f ? skin.HandGrip : HandGrip) * skin.HandScaleOf(fig, side);
                return true;
            }
            if (!skin.JointOf(fig, "forearm." + side, out elbow))
            {
                return false;
            }
            fist = FistOf(wrist, elbow, skin.HandScaleOf(fig, side));
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
            if (ease.stance == null || size <= 0f || (weapon = HeldWeapon(pawn)) == null
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
            if (OffHandOf(weapon.def, out off))
            {
                ReachOffHand(pawn, weapon, off, ease.stance, fig, whole, size, ease, w);
            }
            else
            {
                HoldSupport(pawn, ease.stance, fig, whole, size, ease, w);
            }
        }

        // No point set: the other hand kept where the stance has it against the holding hand on the base body
        // (SkinnedFigure.SupportOf), so on a shape with moved joints (a Hulk's wide shoulders) it stays on the weapon.
        private static void HoldSupport(Pawn pawn, string stance, SkinnedFigure.Posed fig, Matrix4x4 whole, float size, Ease ease, float w)
        {
            WeaponPlaced placed = PlaceWeapon(pawn, stance, fig, size, ease);
            if (placed == null || placed.hold.line != "hand")
            {
                return;
            }
            string side = placed.left ? "R" : "L";
            Vector3 aim = (placed.hold.aim.normalized + Vector3.down * placed.hold.drop).normalized;   // PlaceWeapon's rest
            Vector3 wrist;
            Quaternion turn;
            if (skin.SupportOf(fig, stance, side, aim, out wrist, out turn))
            {
                // The fist where the stance has it, from the hand's fist now turned as it will be.
                Vector3 fistNow, wristNow;
                if (FistOf(fig, side, out fistNow) && skin.JointOf(fig, "hand." + side, out wristNow))
                {
                    Quaternion wr = whole.rotation;
                    Vector3 fist = wrist + wr * skin.TurnFromNow(fig, side, turn) * Quaternion.Inverse(wr) * (fistNow - wristNow);
                    Vector3 shift = SupportShift(placed, stance, fist, size);
                    wrist += shift;
#if AUTOTEST
                    string how;
                    if (AutoSupportShift.TryGetValue(placed.weapon.thingIDNumber, out how))
                    {
                        int cut = how.IndexOf(" |");
                        Vector2 from = FistOnArt(placed.weapon, fist, placed.hand, placed.a, placed.top, placed.hold.behind, size, placed.slide);
                        Vector2 to = FistOnArt(placed.weapon, fist + shift, placed.hand, placed.a, placed.top, placed.hold.behind, size, placed.slide);
                        AutoSupportShift[placed.weapon.thingIDNumber] = (cut >= 0 ? how.Substring(0, cut) : how) + " | from (" + from.x.ToString("F3") + ", "
                            + from.y.ToString("F3") + ") to (" + to.x.ToString("F3") + ", " + to.y.ToString("F3") + ") on the art, weight " + w.ToString("F2");
                    }
#endif
                }
                skin.HoldSupport(fig, whole, side, wrist, turn, w);
                skin.Reached(fig, whole);
            }
        }

        // The weapon each drafted kind's stance was set on (preview.py's KIND_LOOK), for a weapon that plays its kind's.
        private static readonly Dictionary<string, string> KindLook = new Dictionary<string, string>
        {
            { "rifle", "Gun_AssaultRifle" },
            { "pistol", "Gun_Autopistol" },
            { "bow", "Bow_Recurve" },
            { "melee", "MeleeWeapon_Knife" },
        };

        // A weapon of each def a stance was set on, never spawned: GroundRatio and the art go by a Thing.
        private static readonly Dictionary<ThingDef, Thing> designThings = new Dictionary<ThingDef, Thing>();

        // The other hand is moved along the weapon by at most this share of its length either way from where the stance
        // has it, against a weapon drawn far bigger or smaller than the one the stance was set on.
        private const float SupportScaleLeast = 0.4f, SupportScaleMost = 2.5f;

        // How far to move the other hand's wrist from where the stance puts it (fist: its fist then, PlaceWeapon's space) so
        // it holds the same part of this weapon as drawn now. The stance was set on one weapon (its own, or its kind's
        // KindLook) at its type's size in the export; the weapon here may be drawn at another size (the type's size in the
        // options, the slab's share, weapons taking the pawn's size) or be another weapon. Held by its kind's stance, a gun
        // whose art and the kind's both show a fore grip (GripFinder.FindFore) has the hand put on this one's as the stance
        // has it on the kind's; otherwise the hand's distance along the weapon from the holding fist grows and shrinks with
        // the weapon's length against that weapon's, and how far beside or under the line it is stays (the hand's own size).
        private static Vector3 SupportShift(WeaponPlaced placed, string stance, Vector3 fist, float size)
        {
            ThingWithComps weapon = placed.weapon;
            Thing design = DesignWeapon(weapon, stance);
            bool slab = AsSlab(weapon);
            Vector2 draw, designDraw;
            if (design == null || !ArtDrawSize(weapon, size, out draw) || !DesignDrawSize(design, slab, size, out designDraw))
            {
                return Vector3.zero;
            }
            Vector3 shift;
            if (design.def != weapon.def && ForeShift(placed, design, draw, designDraw, out shift))
            {
                return shift;
            }
            float ratio = Mathf.Clamp(Mathf.Max(draw.x, draw.y) / Mathf.Max(designDraw.x, designDraw.y), SupportScaleLeast, SupportScaleMost);
#if AUTOTEST
            AutoSupportShift[weapon.thingIDNumber] = "length " + ratio.ToString("F3") + " of " + design.def.defName + "'s";
#endif
            return placed.a * (Vector3.Dot(fist - placed.closed, placed.a) * (ratio - 1f));
        }

        // The other hand put at a gun's fore grip (GripFinder.FindFore) as the kind's stance has it at the kind's own gun's:
        // the shift from the fist to the fore grip on this art, as drawn now, less the same on the kind's art at its export
        // size, along the weapon. Each art's fore grip and fist go along that art's own axes, so a sprite drawn turned
        // (equippedAngleOffset) agrees. False if either art is unreadable, or the weapon isn't a gun.
        private static bool ForeShift(WeaponPlaced placed, Thing design, Vector2 draw, Vector2 designDraw, out Vector3 shift)
        {
            shift = Vector3.zero;
            ThingWithComps weapon = placed.weapon;
            if (!AutoGrips || KindOf(weapon.def) != "rifle" || Thrown(weapon.def) || design.def.graphicData == null)
            {
                return false;
            }
            Graphic g = weapon.Graphic;
            Material mat = g != null ? g.MatSingleFor(weapon) : null;
            Texture art = mat != null ? mat.mainTexture : null, refArt = OwnArt(design.def.graphicData.texPath);
            float offset = weapon.def.equippedAngleOffset, refOffset = design.def.equippedAngleOffset;
            Vector2 fore, refFore;
            if (!GripFinder.FindFore(art, offset, out fore) || !GripFinder.FindFore(refArt, refOffset, out refFore))
            {
                return false;
            }
            float behind = placed.hold.behind;
            Vector2 half = new Vector2(0.5f, 0.5f);
            Vector2 here = FistOnSprite(placed.closed, placed.hand, placed.a, placed.top, behind, offset, draw) + placed.slide;
            Vector2 there = FistOnSprite(placed.closed, placed.hand, placed.a, placed.top, behind, refOffset, designDraw);
            shift = ArtVector(placed.a, placed.top, offset, Vector2.Scale(fore - half, draw) - here)
                - ArtVector(placed.a, placed.top, refOffset, Vector2.Scale(refFore - half, designDraw) - there);
            // Along the weapon only: how far under its line the hand closes is the stance's (on the game's guns the hand
            // keeps much the same height against the holding fist, and not against the art's underside).
            shift = placed.a * Vector3.Dot(shift, placed.a);
#if AUTOTEST
            AutoSupportShift[weapon.thingIDNumber] = "fore grip at (" + fore.x.ToString("F2") + ", " + fore.y.ToString("F2") + "), "
                + design.def.defName + "'s at (" + refFore.x.ToString("F2") + ", " + refFore.y.ToString("F2") + ")";
#endif
            return true;
        }

        // A shift along an art's own axes (x its right, y its up, as drawn) laid on the weapon's line a and top.
        private static Vector3 ArtVector(Vector3 a, Vector3 top, float offset, Vector2 along)
        {
            Vector3 x, y;
            SpriteAxes(a, top, offset, out x, out y);
            return x * along.x + y * along.y;
        }

        // The weapon a drafted stance was set on: its own (drafted_<defName>, a slab's too), else its kind's (KindLook);
        // null for a stance set on none of the game's (a shield's, another mod's weapon's).
        private static Thing DesignWeapon(ThingWithComps weapon, string stance)
        {
            string name = stance.StartsWith("drafted_") ? stance.Substring("drafted_".Length) : stance;
            if (name.EndsWith(Slab))
            {
                name = name.Substring(0, name.Length - Slab.Length);
            }
            string look;
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (def == null && KindLook.TryGetValue(name, out look))
            {
                def = DefDatabase<ThingDef>.GetNamedSilentFail(look);
            }
            if (def == null || def.graphicData == null)
            {
                return null;
            }
            if (def == weapon.def)
            {
                return weapon;
            }
            Thing made;
            if (!designThings.TryGetValue(def, out made))
            {
                try
                {
                    made = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                }
                catch (Exception e)
                {
                    made = null;
                    Log.WarningOnce(Tag + "making a " + def.defName + " to measure a stance by failed: " + e.Message, def.shortHash ^ 0x5a17);
                }
                designThings[def] = made;
            }
            return made;
        }

        // A weapon's art as DrawSlab would size it at its type's size in the export (the options' left out), and a slab at
        // the slab's default share: as big as it was when a stance was set on it.
        private static bool DesignDrawSize(Thing design, bool slab, float size, out Vector2 draw)
        {
            draw = Vector2.zero;
            Graphic g = design.Graphic;
            if (g == null || size <= 0f)
            {
                return false;
            }
            string type = TypeOf(design.def);
            float scale = skin != null ? skin.TypeScale(type) : FileTypeScale(type);
            scale = scale < 0f ? (skin != null && skin.WeaponScale >= 0f ? skin.WeaponScale : WeaponScale) : scale / GroundRatio(design);
            if (slab && !Thrown(design.def))
            {
                scale *= Settings.DefaultSlabScale;
            }
            draw = g.drawSize * scale / size;
            return draw.x > 0f && draw.y > 0f;
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
            row.ToggleableIcon(ref on, ToolsIcon, "M3AT_ToolsToggleTip".Translate(), SoundDefOf.Mouseover_ButtonToggle);
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
