using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The freecam: a free eye flown about the map with the game's pan keys (forward, back and sideways along
    // the ground, whatever the tilt), up and down with keys of its own (Right Shift and Right Ctrl by default), and turned with the
    // mouse, in place of IsoRim's camera that swings round a point on the ground. Clicking works as ever: while the
    // mouse looks round the pointer is held at the middle of the screen, under a small cross, so a click goes there.
    //
    // It is built round the right button. A right click on something with a pawn selected gives its order or opens
    // its menu of orders, which frees the pointer while it is up and goes as the game has it (an option picked, a click
    // off it, the pointer moved away), back to looking. A right click with nothing selected (which in the game opens
    // the architect) goes into pointer mode: the pointer stays free, left clicks on the map go through to it (the
    // architect kept open, though selecting something swaps it for the inspect pane), and a right click that opens no
    // menu of orders goes back to looking, closing the architect; with a designator in hand, a right click first drops
    // it, as in the game. The pointer key and button (the middle button by default; the key is unbound until the player picks one) also go in and out of
    // pointer mode, whatever is selected.
    //
    // Only the camera changes. While flying, the eye is a camera pose pushed to IsoRim (Api 66, Api.PushViewPose), from
    // which IsoRim works out its camera and everything that follows it: every pointer ray, the cell under the mouse, the
    // part of the map drawn, the fog and the sky. The keys, the flying and the looking run in Api.BeforeViewUpdate, once a
    // frame before the pose is asked for. IsoRim's own camera keys and look drag wait meanwhile (BlockPlayerCamera), and
    // its settings are never written, so on leaving its camera is as the player left it, turned to face this one's way
    // (Api.SetPlayerView). The 3D view switch, the cutscene lock, first person and held keys are Api 68's.
    [StaticConstructorOnStartup]
    public static class Freecam
    {
        private const string Tag = "[M3AT] ";

        // The camera poses (Api 66) and the 3D view switch, first person and held keys (Api 68).
        private const int MinimumApi = 68;

        public const float LeastPitch = -85f, MostPitch = 89f;   // degrees below the horizon (negative: up)
        private const float LeastHeight = 0.30f, MostHeight = 120f;   // the eye over the ground, cells
        private const float MapMargin = 30f;   // how far past the map's edge the eye may go, cells
        private const float NearDistance = 10f, FarDistance = 150f;   // IsoRim's focus distance's range for a free eye (camDist), cells
        private const float FocusAhead = 20f;   // the game's camera is kept at most this far ahead of the eye, cells
        private const float Accel = 10f;   // how fast the flying speed follows the keys, per second (exponential)
        private const float MostStep = 0.1f;   // the longest frame counted, real seconds, so a hitch doesn't fling the eye
        private const float ScrollStep = 0.05f;   // one notch of the wheel at a scroll speed of 1, in seconds of flying at the set speed
        private const float BoostTimes = 2f;   // the boost key's multiple of the flying speed (and the wheel's climb)
        private const float ScrollEase = 12f;   // how fast the eye takes up the wheel's climb, per second (exponential)
        private const float Crosshair = 6f, CrosshairThick = 2f;   // the cross at the middle of the screen, UI px
        // A jump to something (a colonist double-clicked on the bar, a letter, an alert): the eye put where it looks at
        // it along its own yaw and pitch from as far as it was from what it looked at, kept within JumpNearest and
        // JumpFarthest (close enough to see the pawn well), the pitch at least JumpLeastPitch (so it doesn't end up
        // looking along the ground at it, or inside a wall), at JumpAbove over the ground (about a person's middle).
        private const float JumpNearest = 3f, JumpFarthest = 8f;   // the distance kept from where it looked, cells
        private const float JumpLeastPitch = 25f;   // degrees below the horizon
        private const float JumpAbove = 0.6f;   // cells
        private const float JumpLongest = 0.25f;   // a glide's length at 70 cells or more, real seconds, as the game's pan
        private const int JumpWaitFrames = 30;   // a jump to another map is dropped if the freecam isn't back on it by then

        public static KeyBindingDef ToggleKey, UpKey, DownKey, PointerKey, BoostKey;
        private static KeyBindingDef firstPersonKey;   // IsoRim's own (R3D_FirstPerson)
        private static KeyBindingDef roofsKey;   // IsoRim's own (R3D_ToggleRoofs)
        private static bool roofsSeen;   // IsoRim's showRoofs at the end of the last frame

        private static bool ready;   // everything below was found
        private static bool on;
        private static Map map;
        private static IDisposable walls;   // IsoRim's complete walls while flying the freecam (Api 17)
        private static IDisposable posed;   // the eye, pushed as IsoRim's camera pose while flying (Api 66)
        private static readonly object PoseOwner = new object();

        private static Vector3 eye;
        private static Vector3 velocity;   // cells per real second
        private static float scrollClimb;   // cells still to rise (negative: sink) from the mouse wheel
        private static float yaw;   // degrees clockwise from north, seen from above
        private static float pitch;   // degrees below the horizon

        // A jump the game asked for, taken up in TurnPrefix once the freecam is on that map (after a change of map it
        // comes back on there a frame later), and the glide there.
        private static Map jumpMap;
        private static Vector3 jumpAt;
        private static bool jumpCut;
        private static int jumpFrame = -1;
        private static bool gliding;
        private static Vector3 glideFrom, glideTo;
        private static float glideTime, glideLength;   // real seconds

        private static bool pointerMode;   // the pointer is left free until a right click that opens no menu of orders
        private static bool rightPending;   // a right click on the map in pointer mode with something selected: did it open a menu?
        private static int rightUpFrame = -1;   // the frame its button was first seen up
        private static bool leftPending;   // a left click on the map in pointer mode, with freecamClickOffLooks: did it select anything?
        private static int leftUpFrame = -1;
        private static readonly List<Window> keptOpen = new List<Window>();   // main tabs kept open through a left click on the map
        private static bool looking;   // the mouse is turning the view this frame (the pointer locked)
        private static bool lockedLast;   // the cursor was locked by us, so must be let go
        private static int lockedFrame = -1;   // the last frame Look ran with the cursor locked
        private static bool skipDelta;   // the first frame after locking: the pointer jumps to the middle
        // Frames left to put the freed pointer in the middle: the engine changes the cursor's mode a little later, and
        // then puts it back where it was before it was locked, away from a menu just opened under the cross.
        private static int centreFrames;
        private const int CentreFrames = 3;
        // Where the pointer was when looking began, so going into pointer mode puts it back there rather than in the
        // middle: the game's own reading (screen pixels, y up) and the engine window's raw cursor (whatever its own
        // units), kept as they were read so neither's convention has to be known. Freed for a window that wants the
        // pointer (a menu opened under the cross, a dialog), it still goes to the middle.
        private static Vector2 lookedFrom;
        private static object lookedFromWindow;
        private static bool haveLookedFrom;
        private static bool freedBack;   // the freed pointer goes back to lookedFrom, not the middle

        // A menu of orders opened while looking (a right click under the cross with something selected) is kept in
        // looking: pinned to the place it was opened over, it moves on screen as the view turns or flies, so its options
        // are picked by turning the cross onto them, and looking away from it closes it as the game closes one the
        // pointer leaves (FloatMenu.UpdateBaseColor, 95 px off it). The freecamMenuFreesPointer option (off by default)
        // puts back the old way instead: the menu frees the pointer, in the middle of the screen, while it is up.
        private static FloatMenu pinned;
        private static Vector3 pinnedAt;   // the place under the cross when it opened, in the eye's (unsquashed) space
        private static Vector2 pinnedOffset;   // the menu's corner from that place on screen, UI pixels
        private const float PinnedNearest = 0.3f;   // cells in front of the eye; nearer or behind, the menu closes

        // The engine's window and input bridge, by reflection, to warp the pointer while looking (Warp).
        private static object window;
        private static PropertyInfo cursorPos, windowSize;
        private static ConstructorInfo double2;
        private static FieldInfo intX, intY;
        private static object bridge;
        private static FieldInfo bridgeCursor, bridgeHas;
        private static bool warpFailed;

        public static bool On
        {
            get { return on; }
        }

        static Freecam()
        {
            try
            {
                ToggleKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("M3AT_Freecam");
                firstPersonKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("R3D_FirstPerson");
                roofsKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("R3D_ToggleRoofs");
                UpKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("M3AT_FreecamUp");
                DownKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("M3AT_FreecamDown");
                PointerKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("M3AT_FreecamPointer");
                BoostKey = DefDatabase<KeyBindingDef>.GetNamedSilentFail("M3AT_FreecamBoost");
                MethodInfo dolly = AccessTools.Method(typeof(CameraDriver), "CalculateCurInputDollyVect");
                MethodInfo gui = AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI));
                MethodInfo rootUpdate = AccessTools.Method(typeof(Root), "Update");
                if (ToggleKey == null)
                {
                    Log.Warning(Tag + "The freecam's key is missing, so the freecam is off.");
                    return;
                }
                if (IsoRim.Api.Version < MinimumApi)
                {
                    Log.Warning(Tag + "IsoRim's Api is version " + IsoRim.Api.Version + ", " + MinimumApi
                        + " or later is needed for the freecam, so it is off.");
                    return;
                }
                Posing.Listen();
                Harmony harmony = new Harmony("MaruMirai.M3AT.Freecam");
                if (dolly != null)
                {
                    harmony.Patch(dolly, postfix: new HarmonyMethod(typeof(Freecam), nameof(DollyPostfix)));
                }
                MethodInfo zoom = AccessTools.Method(typeof(CameraDriver), nameof(CameraDriver.CameraDriverOnGUI));
                if (zoom != null)
                {
                    harmony.Patch(zoom, prefix: new HarmonyMethod(typeof(Freecam), nameof(ZoomPrefix)));
                }
                if (gui != null)
                {
                    harmony.Patch(gui, postfix: new HarmonyMethod(typeof(Freecam), nameof(GuiPostfix)));
                }
                // The cross drawn again from inside the pinned orders menu, so it shows over the menu: windows are drawn
                // after the rest of the interface, and a window of our own over the middle would take the clicks there.
                MethodInfo menuContents = AccessTools.Method(typeof(FloatMenu), nameof(FloatMenu.DoWindowContents));
                if (menuContents != null)
                {
                    harmony.Patch(menuContents, postfix: new HarmonyMethod(typeof(Freecam), nameof(MenuContentsPostfix)));
                }
                MethodInfo windowEvents = AccessTools.Method(typeof(WindowStack), nameof(WindowStack.HandleEventsHighPriority));
                if (windowEvents != null)
                {
                    harmony.Patch(windowEvents, prefix: new HarmonyMethod(typeof(Freecam), nameof(WindowEventsPrefix)),
                        postfix: new HarmonyMethod(typeof(Freecam), nameof(WindowEventsPostfix)));
                }
                // A box selection begun on the sky goes from where the button went down on screen (SkyBoxPostfix,
                // SkyRectPostfix), not from the place IsoRim gives for it out on the horizon.
                MethodInfo boxValid = AccessTools.PropertyGetter(typeof(DragBox), nameof(DragBox.IsValid));
                MethodInfo boxRect = AccessTools.PropertyGetter(typeof(DragBox), nameof(DragBox.ScreenRect));
                if (boxValid != null && boxRect != null)
                {
                    harmony.Patch(boxValid, postfix: new HarmonyMethod(typeof(Freecam), nameof(SkyBoxPostfix)));
                    harmony.Patch(boxRect, postfix: new HarmonyMethod(typeof(Freecam), nameof(SkyRectPostfix)));
                }
                if (rootUpdate != null)
                {
                    harmony.Patch(rootUpdate, postfix: new HarmonyMethod(typeof(Freecam), nameof(RootUpdatePostfix)));
                }
                // The game's jumps to a thing or cell (the colonist bar's double click, letters, alerts, the select
                // that changes map), which the freecam would otherwise overwrite with its own place the next frame.
                MethodInfo jump = AccessTools.Method(typeof(CameraJumper), "TryJumpInternal",
                    new[] { typeof(IntVec3), typeof(Map), typeof(CameraJumper.MovementMode) });
                if (jump != null)
                {
                    harmony.Patch(jump, prefix: new HarmonyMethod(typeof(Freecam), nameof(JumpPrefix)));
                }
                MethodInfo select = AccessTools.Method(typeof(CameraJumper), "TrySelectInternal", new[] { typeof(Thing) });
                if (select != null)
                {
                    harmony.Patch(select, prefix: new HarmonyMethod(typeof(Freecam), nameof(SelectPrefix)));
                }
                FindWindow();
                ready = true;
            }
            catch (Exception e)
            {
                Log.Warning(Tag + "The freecam could not be set up: " + e);
            }
        }

        private static Settings Options
        {
            get { return SettingsMod.Settings; }
        }

        // ---------------------------------------------------------------- entering and leaving

        // Whether the freecam is wanted, kept in the mod settings so it lasts through the 2D view, other maps, first
        // person and restarts: while it is wanted it comes back on whenever the 3D view is on (Resume).
        private static bool Wanted
        {
            get { return Options.freecamOn; }
            set
            {
                if (Options.freecamOn != value)
                {
                    Options.freecamOn = value;
                    Options.Write();
                }
            }
        }

        // Whether the 3D view was on with it, so a game loaded with the freecam wanted starts in 3D (IsoRim starts every
        // game flat) unless it was left in 2D.
        private static void Note3D(bool shown)
        {
            if (Options.freecam3D != shown)
            {
                Options.freecam3D = shown;
                Options.Write();
            }
        }

        private static Game seenGame;

        // The freecam back on where it is wanted and can be: the 3D view on, no first person or cutscene. A game just
        // loaded with it wanted has its 3D view turned on first, if it was on when the freecam was last flown.
        private static void Resume()
        {
            if (!Wanted || Find.CurrentMap == null || FirstPersonOn() || IsoRim.Api.ControlsLocked)
            {
                return;
            }
            if (seenGame != Current.Game)
            {
                seenGame = Current.Game;
                LoadTrail.Note("freecam: a game seen with it wanted, 3D view " + (Options.freecam3D ? "turned on" : "left off") + LoadTrail.MemoryNow());
                if (Options.freecam3D)
                {
                    IsoRim.Api.View3DActive = true;
                    LoadTrail.Note("freecam: 3D view on" + LoadTrail.MemoryNow());
                }
            }
            if (IsoRim.Api.View3DActive)
            {
                Enter(false, true);
            }
        }

        private static void Enter(bool announce, bool resuming)
        {
            Map current = Find.CurrentMap;
            if (current == null)
            {
                return;
            }
            LoadTrail.Note("freecam: entering" + LoadTrail.MemoryNow());
            IsoRim.Api.LeaveFirstPerson();
            IsoRim.Api.View3DActive = true;

            // Start where IsoRim's camera is, or for its flat camera where a perspective one would be at the same zoom.
            yaw = IsoRim.Api.ViewYaw;
            pitch = Mathf.Clamp(IsoRim.Api.ViewPitch, LeastPitch, MostPitch);
            Vector3 seen;
            if (IsoRim.Api.ViewPerspective && Posing.EyeNow(out seen))
            {
                eye = seen;
            }
            else
            {
                Vector3 focus = Find.CameraDriver != null ? Find.CameraDriver.MapPosition.ToVector3Shifted() : current.Center.ToVector3Shifted();
                float size = Find.Camera != null ? Find.Camera.orthographicSize : 20f;
                Vector3 f = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
                eye = new Vector3(focus.x, 0f, focus.z) - f * (size / IsoRim.Api.PerspectiveTanHalf);
            }
            // Coming back on by itself (a game loaded, back from first person, the 2D view or another map), where it was
            // last flown on this map, if the game remembers. Turned on by the key, it takes over IsoRim's camera as it is.
            FreecamPlaces places = Current.Game.GetComponent<FreecamPlaces>();
            FreecamPlaces.Place last;
            if (resuming && places != null && places.TryGet(current, out last))
            {
                eye = last.eye;
                yaw = last.yaw;
                pitch = Mathf.Clamp(last.pitch, LeastPitch, MostPitch);
            }
            eye.x = Mathf.Clamp(eye.x, -MapMargin, current.Size.x + MapMargin);
            eye.z = Mathf.Clamp(eye.z, -MapMargin, current.Size.z + MapMargin);
            eye.y = Mathf.Clamp(eye.y, LeastHeight, MostHeight);
            velocity = Vector3.zero;
            scrollClimb = 0f;
            gliding = false;
            map = current;
            on = true;
            if (posed == null)
            {
                posed = Posing.Push();
            }
            pointerMode = false;
            rightPending = false;
            leftPending = false;
            SyncWalls();
            seenGame = Current.Game;
            Note3D(true);
            if (announce)
            {
                Messages.Message("Freecam on: " + ToggleKey.MainKeyLabel + " to leave.", MessageTypeDefOf.SilentInput, false);
            }
        }

        // IsoRim's complete walls are asked for while flying unless the player wants IsoRim's own wall hiding
        // (`freecamCutaway`: its cut-away by the pointer, which stays at the screen's middle while looking, near colonists
        // and over rooms, as its settings have them); looked at every frame, so the option takes at once.
        private static void SyncWalls()
        {
            bool want = on && !Options.freecamCutaway && IsoRim.Api.Version >= 17;
            if (want && walls == null)
            {
                try
                {
                    walls = RequestWalls(map);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(Tag + "Asking IsoRim for complete walls failed: " + e, 0x3A7E5702);
                }
            }
            else if (!want && walls != null)
            {
                ReleaseWalls();
            }
        }

        private static void ReleaseWalls()
        {
            try
            {
                walls.Dispose();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Letting go of IsoRim's complete walls failed: " + e, 0x3A7E5701);
            }
            walls = null;
        }

        // Its own method, so an older IsoRim without the call never has it compiled.
        private static IDisposable RequestWalls(Map m)
        {
            return IsoRim.Api.RequestFullWalls(m);
        }

        private static void Leave(bool message)
        {
            if (!on)
            {
                return;
            }
            on = false;
            pinned = null;
            Remember();
            Unlock();
            if (walls != null)
            {
                ReleaseWalls();
            }
            // IsoRim's camera goes on facing the way this one did, over the ground in the middle of the screen (looked
            // at while the pose still holds the view).
            try
            {
                if (Find.CameraDriver != null && map != null && map == Find.CurrentMap)
                {
                    Vector3 at = IsoRim.Api.ViewReady ? IsoRim.Api.ScreenToGround(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)) : eye;
                    at.x = Mathf.Clamp(at.x, 0f, map.Size.x);
                    at.z = Mathf.Clamp(at.z, 0f, map.Size.z);
                    Find.CameraDriver.JumpToCurrentMapLoc(new Vector3(at.x, 0f, at.z));
                }
                IsoRim.Api.SetPlayerView(yaw, null);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "Putting the camera back failed: " + e, 0x3A7E5702);
            }
            if (posed != null)
            {
                posed.Dispose();
                posed = null;
            }
            map = null;
            if (message)
            {
                Messages.Message("Freecam off.", MessageTypeDefOf.SilentInput, false);
            }
        }

        // The eye kept with the game for this map, so the freecam comes back where it was (also after loading).
        private static void Remember()
        {
            if (map == null || Current.Game == null)
            {
                return;
            }
            FreecamPlaces places = Current.Game.GetComponent<FreecamPlaces>();
            if (places != null)
            {
                places.Set(map, new FreecamPlaces.Place { eye = eye, yaw = yaw, pitch = pitch });
            }
        }

        // ---------------------------------------------------------------- once a frame

        // IsoRim's first person is on, also while our pose holds the view (Api 71's FirstPersonOn). Before Api 71 only
        // FirstPersonPawn can say, and that is null under a pose, so the key is predicted instead (FirstPersonStarts).
        private static readonly bool hasFirstPersonOn = IsoRim.Api.Version >= 71;

        private static bool FirstPersonOn()
        {
            return hasFirstPersonOn ? ReadFirstPersonOn() : IsoRim.Api.FirstPersonPawn != null;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ReadFirstPersonOn()
        {
            return IsoRim.Api.FirstPersonOn;   // called only on Api 71 or later
        }

        // Before Api 71: whether IsoRim's first person key starts first person now: with the one person selected, on the
        // map shown (as FirstPerson.Toggle has it). Should it ever be wrong, Resume puts the freecam back the frame after.
        private static bool FirstPersonStarts()
        {
            Pawn chosen = Find.Selector != null && Find.Selector.NumSelected == 1 ? Find.Selector.SingleSelectedThing as Pawn : null;
            return chosen != null && chosen.Spawned && chosen.Map == Find.CurrentMap && chosen.RaceProps != null && chosen.RaceProps.Humanlike;
        }

        // Api.BeforeViewUpdate, once a frame (3D view or not) before IsoRim asks for the pose and sets its camera: here the
        // keys, the flying and the looking.
        private static void Frame()
        {
            if (!ready)
            {
                return;
            }
            try
            {
                bool typing = GUIUtility.keyboardControl != 0;
                if (!typing && ToggleKey.JustPressed && !IsoRim.Api.ControlsLocked)
                {
                    if (on || Wanted)
                    {
                        Wanted = false;
                        if (on)
                        {
                            Leave(true);
                        }
                        else
                        {
                            Messages.Message("Freecam off.", MessageTypeDefOf.SilentInput, false);
                        }
                    }
                    else
                    {
                        Wanted = true;
                        Enter(true, false);
                    }
                }
                // IsoRim's first person key while flying: IsoRim has just started first person (its keys are read before this),
                // but a pose wins over it, so the freecam steps aside for it, as it is set aside for first person anyway. From
                // Api 71 IsoRim says so itself; before it, the key and its selection rule are predicted.
                bool toFirstPerson = on && (hasFirstPersonOn
                    ? ReadFirstPersonOn()
                    : !typing && firstPersonKey != null && firstPersonKey.JustPressed && FirstPersonStarts());
                if (on && (toFirstPerson || Find.CurrentMap != map || !IsoRim.Api.View3DActive || IsoRim.Api.ControlsLocked))
                {
                    // Set aside, still wanted: it comes back with the 3D view, on the next map, after first person.
                    if (!IsoRim.Api.View3DActive)
                    {
                        Note3D(false);
                    }
                    Leave(false);
                }
                // Not this frame after stepping aside for first person: before Api 71 it hides behind the pose until the pose is
                // gone, so Api.FirstPersonPawn says none yet. If first person didn't start after all, it comes back on the next.
                if (!on && !toFirstPerson)
                {
                    Resume();
                }
                Roofs(typing);
                if (!on)
                {
                    return;
                }
                SyncWalls();
                float dt = Mathf.Min(Time.unscaledDeltaTime, MostStep);
                TakeJump();
                Look(typing);
                Fly(dt, typing);
                KeepGameCamera();
                Remember();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Tag + "The freecam failed and is off: " + e, 0x3A7E5703);
                Options.freecamOn = false;   // not written: it is tried again after a restart
                try
                {
                    Leave(false);
                }
                catch
                {
                    on = false;
                    Unlock();
                }
            }
        }

        // IsoRim's roof key. IsoRim reads it after the camera keys a blocking pose holds back, so while the freecam flies it
        // was never read. Its keys are read before this: if the key was pressed and the roofs are as they were at the end of
        // the last frame, IsoRim passed it over, and it is done here (so never twice, as on the frame a pose starts or ends).
        private static void Roofs(bool typing)
        {
            bool now = IsoRim.Api.GetOption("showRoofs") is bool shown && shown;
            if (on && !typing && roofsKey != null && roofsKey.JustPressed && now == roofsSeen && IsoRim.Api.View3DActive)
            {
                now = !now;
                IsoRim.Api.ShowRoofs(now);
            }
            roofsSeen = now;
        }

        // The pan keys along the ground in the way the camera faces, the up and down keys straight up and down, eased.
        private static void Fly(float dt, bool typing)
        {
            Vector3 want = Vector3.zero;
            bool keys = !typing && (Find.WindowStack == null || !Find.WindowStack.WindowsPreventCameraMotion);
            if (keys)
            {
                float y = yaw * Mathf.Deg2Rad;
                Vector3 ahead = new Vector3(Mathf.Sin(y), 0f, Mathf.Cos(y));
                Vector3 side = new Vector3(Mathf.Cos(y), 0f, -Mathf.Sin(y));
                if (IsoRim.Api.KeyHeld(KeyBindingDefOf.MapDolly_Up, false))
                {
                    want += ahead;
                }
                if (IsoRim.Api.KeyHeld(KeyBindingDefOf.MapDolly_Down, false))
                {
                    want -= ahead;
                }
                if (IsoRim.Api.KeyHeld(KeyBindingDefOf.MapDolly_Right, false))
                {
                    want += side;
                }
                if (IsoRim.Api.KeyHeld(KeyBindingDefOf.MapDolly_Left, false))
                {
                    want -= side;
                }
                if (want.sqrMagnitude > 1f)
                {
                    want.Normalize();
                }
                if (UpKey != null && IsoRim.Api.KeyHeld(UpKey, false))
                {
                    want.y += 1f;
                }
                if (DownKey != null && IsoRim.Api.KeyHeld(DownKey, false))
                {
                    want.y -= 1f;
                }
            }
            if (gliding)
            {
                // Flying or the wheel stop a glide where it has got to.
                if (want != Vector3.zero || scrollClimb != 0f)
                {
                    gliding = false;
                }
                else
                {
                    glideTime += dt;
                    float t = glideLength > 0f ? Mathf.Clamp01(glideTime / glideLength) : 1f;
                    eye = Vector3.Lerp(glideFrom, glideTo, Mathf.SmoothStep(0f, 1f, t));
                    velocity = Vector3.zero;
                    if (t >= 1f)
                    {
                        gliding = false;
                    }
                    return;
                }
            }
            want *= Mathf.Clamp(Options.freecamSpeed, 1f, 100f) * (keys ? Boost() : 1f);
            // The wheel's climb, eased in apart from the keys' flying, so a notch is a set height whatever the frame rate.
            float climb = scrollClimb * (1f - Mathf.Exp(-ScrollEase * dt));
            scrollClimb -= climb;
            if (Mathf.Abs(scrollClimb) < 0.001f)
            {
                scrollClimb = 0f;
            }
            eye.y += climb;
            velocity = Vector3.Lerp(velocity, want, 1f - Mathf.Exp(-Accel * dt));
            if (want == Vector3.zero && velocity.sqrMagnitude < 0.0004f)
            {
                velocity = Vector3.zero;
            }
            eye += velocity * dt;
            IntVec3 size = map.Size;
            eye.x = Mathf.Clamp(eye.x, -MapMargin, size.x + MapMargin);
            eye.z = Mathf.Clamp(eye.z, -MapMargin, size.z + MapMargin);
            eye.y = Mathf.Clamp(eye.y, LeastHeight, MostHeight);
        }

        // Whether the mouse turns the view now: not in pointer mode, nor while anything wants the pointer.
        private static void Look(bool typing)
        {
            Settings s = Options;
            // The pointer key or button (the middle button by default, the key unbound until the player picks one) flips pointer mode whatever is
            // selected, for when a right click would give an order rather than free the pointer. A button press over a
            // window with the pointer free is the window's.
            int button = s.freecamButton;
            bool buttonPressed = button >= 2 && button <= 6 && Input.GetMouseButtonDown(button)
                && !(pointerMode && Find.WindowStack != null && Find.WindowStack.MouseObscuredNow);
            if (buttonPressed || !typing && PointerKey != null && PointerKey.JustPressed)
            {
                pointerMode = !pointerMode;
                rightPending = false;
                leftPending = false;
                if (!pointerMode)
                {
                    CloseTab();
                }
            }
            // A right click in pointer mode that gave an order is looked at the frame after its button is up (a held
            // right button may be dragging out a group's places, and a menu it opened has had its OnGUI): with no menu
            // of orders up, back to looking.
            if (rightPending)
            {
                if (Input.GetMouseButton(1))
                {
                    rightUpFrame = -1;
                }
                else if (rightUpFrame < 0)
                {
                    rightUpFrame = Time.frameCount;
                }
                else if (Time.frameCount > rightUpFrame)
                {
                    rightPending = false;
                    rightUpFrame = -1;
                    if (!MenuOpen())
                    {
                        pointerMode = false;
                        CloseTab();
                    }
                }
            }
            // A left click in pointer mode with freecamClickOffLooks, the same way once the game has selected on its
            // release: nothing selected (a click on nothing) goes back to looking.
            if (leftPending)
            {
                if (Input.GetMouseButton(0))
                {
                    leftUpFrame = -1;
                }
                else if (leftUpFrame < 0)
                {
                    leftUpFrame = Time.frameCount;
                }
                else if (Time.frameCount > leftUpFrame)
                {
                    leftPending = false;
                    leftUpFrame = -1;
                    if (pointerMode && Find.Selector != null && Find.Selector.NumSelected == 0 && !MenuOpen()
                        && (Find.DesignatorManager == null || Find.DesignatorManager.SelectedDesignator == null))
                    {
                        pointerMode = false;
                        rightPending = false;
                        CloseTab();
                    }
                }
            }
            Pin();
            looking = !pointerMode && !PointerWanted();
            if (!looking)
            {
                Unlock();
                return;
            }
            if (!lockedLast)
            {
                KeepLookedFrom();
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                lockedLast = true;
                skipDelta = true;
                // A left click that closed a menu of orders also went down on the map and began the game's box
                // selection there; with the pointer now held at the middle that box would stretch from the click to
                // the cross and select everything between on release, so it is dropped.
                Selector sel = Find.Selector;
                if (sel != null && sel.dragBox != null)
                {
                    sel.dragBox.active = false;
                }
            }
            lockedFrame = Time.frameCount;
            Vector2 middle = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector3 mouse = Input.mousePosition;
            Vector2 d = skipDelta ? Vector2.zero : new Vector2(mouse.x - middle.x, mouse.y - middle.y);
            skipDelta = false;
            Warp(middle, null);
            float sens = Mathf.Clamp(s.freecamSensitivity, 0.01f, 1f);
            yaw = Mathf.Repeat(yaw + d.x * sens, 360f);
            pitch = Mathf.Clamp(pitch + (s.freecamInvertY ? 1f : -1f) * d.y * sens, LeastPitch, MostPitch);
        }

        // Takes up a menu of orders that opened last frame while looking, or lets go of one that has closed. The eye's
        // fields still hold last frame's view, the one it was opened under, as Look runs before this frame's Apply.
        private static void Pin()
        {
            if (pinned != null && (pointerMode || Find.WindowStack == null || !Find.WindowStack.IsOpen(pinned)))
            {
                bool closed = !pointerMode;
                pinned = null;
                // A left click off the menu closes it and also goes down on the map, beginning the game's box selection
                // there, which while looking would stretch to wherever the cross is turned before the release.
                Selector sel = Find.Selector;
                if (closed && sel != null && sel.dragBox != null && Input.GetMouseButton(0))
                {
                    sel.dragBox.active = false;
                }
            }
            // With freecamMenuFreesPointer a new menu is not taken up, so it frees the pointer as any window wanting
            // it does (WindowWantsPointer), and looking comes back once it closes.
            if (pinned != null || pointerMode || !lockedLast || Find.WindowStack == null || Options.freecamMenuFreesPointer)
            {
                return;
            }
            foreach (Window w in Find.WindowStack.Windows)
            {
                FloatMenu menu = w as FloatMenu;
                if (menu == null)
                {
                    continue;
                }
                pinned = menu;
                Vector3 fwd = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
                pinnedAt = eye + fwd * FocusDistance(fwd);
                pinnedOffset = menu.windowRect.position - new Vector2(UI.screenWidth * 0.5f, UI.screenHeight * 0.5f);
                return;
            }
        }

        // The pinned menu moved to where its place is seen from this frame's eye, as the pose is given (Posing.Pose).
        private static void MovePinned(Vector3 fwd, Vector3 right, Vector3 up, float tanHalf, float aspect)
        {
            if (pinned == null)
            {
                return;
            }
            Vector3 v = pinnedAt - eye;
            float z = Vector3.Dot(v, fwd);
            if (z < PinnedNearest)
            {
                pinned.Cancel();
                pinned = null;
                return;
            }
            float x = Vector3.Dot(v, right) / (z * tanHalf * aspect);
            float y = Vector3.Dot(v, up) / (z * tanHalf);
            Vector2 seen = new Vector2((0.5f + 0.5f * x) * UI.screenWidth, (0.5f - 0.5f * y) * UI.screenHeight);
            Rect r = pinned.windowRect;
            r.position = seen + pinnedOffset;
            pinned.windowRect = r;
        }

        private static bool MenuOpen()
        {
            if (Find.WindowStack == null)
            {
                return false;
            }
            foreach (Window w in Find.WindowStack.Windows)
            {
                if (w is FloatMenu)
                {
                    return true;
                }
            }
            return false;
        }

        // Before the game's windows take a mouse press off them (on the map). A click with a menu of orders up is left
        // to the game (it closes the menu). In pointer mode a left click is kept from closing a main tab (which would use
        // it up) and goes on to the map (with freecamClickOffLooks, one that leaves nothing selected goes back to looking), and a right click (unless it
        // closes a menu of orders or drops a designator) goes back to looking: at once with nothing selected (used up,
        // so the game doesn't open the architect again, and closing it), or once it is seen to open no menu.
        // Looking, a right click with nothing selected goes into pointer mode, and the game opens the architect.
        public static bool WindowEventsPrefix()
        {
            keptOpen.Clear();
            try
            {
                Event e = Event.current;
                if (!on || e == null || e.type != EventType.MouseDown || (e.button != 0 && e.button != 1) || Find.WindowStack == null
                    || Find.WindowStack.GetWindowAt(UI.GUIToScreenPoint(e.mousePosition)) != null)
                {
                    return true;
                }
                if (MenuOpen() || OverLetters(e.mousePosition))
                {
                    return true;
                }
                // A left click on the sky is noted with where it went down: the game's box selection it begins is measured
                // and drawn from there on screen (SkyBoxPostfix, SkyRectPostfix). IsoRim stops a ray that never meets the
                // ground at its view distance and gives that point on the ground, which shows on the horizon below the
                // pointer, so the box started there, reached up to the pointer and counted as a drag at once: the release
                // then selected along that strip, and a click on a colonist's portrait missed it, as the game picks a
                // portrait only for a release that is no box (Selector.SelectUnderMouse). The click itself goes on.
                skyPress = e.button == 0 && InSky(looking ? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)
                    : new Vector2(Input.mousePosition.x, Input.mousePosition.y));
                skyFrom = UI.MousePositionOnUIInverted;
                bool nothingSelected = Find.Selector == null || Find.Selector.NumSelected == 0;
                if (!pointerMode)
                {
                    if (e.button == 1 && nothingSelected && !WindowWantsPointer())
                    {
                        pointerMode = true;
                    }
                    return true;
                }
                if (e.button == 0)
                {
                    KeepTabsOpen();
                    leftPending = Options.freecamClickOffLooks
                        && (Find.DesignatorManager == null || Find.DesignatorManager.SelectedDesignator == null);
                    leftUpFrame = -1;
                    return true;
                }
                if (Find.DesignatorManager != null && Find.DesignatorManager.SelectedDesignator != null)
                {
                    return true;
                }
                if (nothingSelected)
                {
                    pointerMode = false;
                    rightPending = false;
                    CloseTab();
                    e.Use();
                    return false;
                }
                // An order: kept from closing the architect, so the game gives it (the tab is closed going back to looking).
                KeepTabsOpen();
                rightPending = true;
                rightUpFrame = -1;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(Tag + "The freecam's click handling failed: " + ex, 0x4d334643);
            }
            return true;
        }

        // Whether the line of sight through a screen point (pixels from the bottom left) misses the ground: the place IsoRim
        // gives for it doesn't show back under that point (it was cut short at the view distance, above the horizon or out
        // past the fog).
        private const float SkySlack = 4f;   // screen pixels
        private static bool InSky(Vector2 px)
        {
            Vector3 ground = IsoRim.Api.ScreenToGround(px);
            return (IsoRim.Api.WorldToScreen(ground) - px).sqrMagnitude > SkySlack * SkySlack;
        }

        private static bool skyPress;   // the last left click on the map went down on the sky
        private static Vector2 skyFrom;   // where, as the game's box has its corners (UI pixels, y down)
        private const float SkyDrag = 8f;   // UI pixels the pointer has to move for a press on the sky to be a box

        private static bool SkyBox(DragBox box)
        {
            return on && skyPress && Find.Selector != null && box == Find.Selector.dragBox;
        }

        // The game's box selection, begun by a press on the sky, is a box once the pointer has moved SkyDrag from where
        // it went down; until then it is not (so not drawn, and its release selects under the pointer, a colonist's
        // portrait or nothing). IsoRim's prefix on it, which measures from the horizon, runs first.
        public static void SkyBoxPostfix(DragBox __instance, ref bool __result)
        {
            if (SkyBox(__instance))
            {
                __result = (UI.MousePositionOnUIInverted - skyFrom).sqrMagnitude > SkyDrag * SkyDrag;
            }
        }

        // And that box is from where the button went down to the pointer, on screen: what is drawn, and what the game,
        // IsoRim and the colonist bar select inside.
        public static void SkyRectPostfix(DragBox __instance, ref Rect __result)
        {
            if (SkyBox(__instance))
            {
                Vector2 to = UI.MousePositionOnUIInverted;
                __result = Rect.MinMaxRect(Mathf.Min(skyFrom.x, to.x), Mathf.Min(skyFrom.y, to.y),
                    Mathf.Max(skyFrom.x, to.x), Mathf.Max(skyFrom.y, to.y));
            }
        }

        private static void KeepTabsOpen()
        {
            foreach (Window w in Find.WindowStack.Windows)
            {
                if (w is MainTabWindow && !(w is MainTabWindow_Inspect) && w.closeOnClickedOutside)
                {
                    w.closeOnClickedOutside = false;
                    keptOpen.Add(w);
                }
            }
        }

        private static void CloseTab()
        {
            if (Find.MainTabsRoot != null && Find.MainTabsRoot.OpenTab != null && Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect)
            {
                Find.MainTabsRoot.EscapeCurrentTab(false);
            }
        }

        public static void WindowEventsPostfix()
        {
            foreach (Window w in keptOpen)
            {
                w.closeOnClickedOutside = true;
            }
            keptOpen.Clear();
        }

        // The pointer is needed for something on screen: a menu of orders, a dialog, a main tab, the game unfocused.
        private static bool PointerWanted()
        {
            if (!Application.isFocused)
            {
                return true;
            }
            return WindowWantsPointer();
        }

        // Whether a point (GUI space, y down) is on the letter stack's buttons at the screen's right edge, which aren't
        // windows: a click there (a right click closes a letter) is the letter's, not the map's, so it leaves pointer
        // mode as it is. The column is LetterStack.LettersOnGUI's: Letter.DrawButtonAt's 38 wide, 12 in from the edge,
        // from the top letter (LastTopY) down; a few pixels spare round it.
        private static bool OverLetters(Vector2 at)
        {
            LetterStack stack = Find.LetterStack;
            if (stack == null || stack.LettersListForReading.Count == 0)
            {
                return false;
            }
            float right = UI.screenWidth - 12f + 4f;
            float left = UI.screenWidth - 12f - Letter.DrawWidth - 4f;
            return at.x >= left && at.x <= right && at.y >= stack.LastTopY && at.y <= UI.screenHeight;
        }

        // A window open that wants the pointer: a menu of orders, a dialog, or a main tab such as the architect, which a
        // right click on the map can open too.
        private static bool WindowWantsPointer()
        {
            if (Find.WindowStack == null)
            {
                return false;
            }
            foreach (Window w in Find.WindowStack.Windows)
            {
                if (w == pinned)
                {
                    continue;
                }
                if (w is FloatMenu || w.absorbInputAroundWindow || w.forcePause || w.preventCameraMotion
                    || w is MainTabWindow && !(w is MainTabWindow_Inspect))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Unlock()
        {
            looking = false;
            if (lockedLast)
            {
                lockedLast = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                centreFrames = CentreFrames;
                // Back where it was when looking began on going into pointer mode or leaving the freecam; to the
                // middle when something on screen wants it there (a menu under the cross) or the game lost focus.
                freedBack = Options.freecamPointerBack && haveLookedFrom && (pointerMode || !on) && Application.isFocused && !WindowWantsPointerBesidesTab();
            }
        }

        // A window that wants the pointer other than a main tab: the architect a right click opens going into pointer
        // mode is at the screen's bottom, so the pointer may as well go back where it was for it.
        private static bool WindowWantsPointerBesidesTab()
        {
            if (Find.WindowStack == null)
            {
                return false;
            }
            foreach (Window w in Find.WindowStack.Windows)
            {
                if (w == pinned || w is MainTabWindow)
                {
                    continue;
                }
                if (w is FloatMenu || w.absorbInputAroundWindow || w.forcePause || w.preventCameraMotion)
                {
                    return true;
                }
            }
            return false;
        }

        // The pointer's place as looking begins, before it is first warped to the middle.
        private static void KeepLookedFrom()
        {
            Vector3 mouse = Input.mousePosition;
            lookedFrom = new Vector2(Mathf.Clamp(mouse.x, 0f, Screen.width - 1), Mathf.Clamp(mouse.y, 0f, Screen.height - 1));
            lookedFromWindow = null;
            try
            {
                if (!warpFailed && window != null && cursorPos != null)
                {
                    lookedFromWindow = cursorPos.GetValue(window);
                }
            }
            catch (Exception)
            {
                lookedFromWindow = null;
            }
            haveLookedFrom = true;
        }

        // The pointer to a place on screen (the middle while looking): the engine's own cursor (its window's, a virtual one while
        // locked), where its input bridge sends clicks from, and where the game reads it, so a click lands under the
        // cross and the next frame's movement is measured from there.
        // atWindow is the engine window's cursor to set, as read from it; null for the middle of the window.
        private static void Warp(Vector2 to, object atWindow)
        {
            if (warpFailed)
            {
                return;
            }
            try
            {
                if (window != null && cursorPos != null && windowSize != null && double2 != null)
                {
                    object at = atWindow;
                    if (at == null)
                    {
                        object size = windowSize.GetValue(window);
                        double x = (int)intX.GetValue(size) * 0.5, y = (int)intY.GetValue(size) * 0.5;
                        at = double2.Invoke(new object[] { x, y });
                    }
                    cursorPos.SetValue(window, at);
                    if (bridge != null && bridgeCursor != null && bridgeHas != null)
                    {
                        bridgeCursor.SetValue(bridge, at);
                        bridgeHas.SetValue(bridge, true);
                    }
                }
                UnityEngine.InputSystem.InputSystem.QueueMouseMove(to);
            }
            catch (Exception e)
            {
                warpFailed = true;
                Log.Warning(Tag + "The freecam can't hold the pointer in the middle of the screen, so clicks while looking round may miss: " + e);
            }
        }

        private static void FindWindow()
        {
            try
            {
                Type appType = AccessTools.TypeByName("Waylith.EngineApplication");
                object app = appType != null ? AccessTools.Property(appType, "Instance")?.GetValue(null) : null;
                PropertyInfo windowProp = appType != null ? AccessTools.Property(appType, "Window") : null;
                window = app != null && windowProp != null ? windowProp.GetValue(app) : null;
                if (window == null)
                {
                    return;
                }
                cursorPos = AccessTools.Property(window.GetType(), "CursorPos");
                windowSize = AccessTools.Property(window.GetType(), "Size");
                if (cursorPos == null || windowSize == null)
                {
                    return;
                }
                double2 = cursorPos.PropertyType.GetConstructor(new[] { typeof(double), typeof(double) });
                intX = AccessTools.Field(windowSize.PropertyType, "X");
                intY = AccessTools.Field(windowSize.PropertyType, "Y");
                Type bootstrap = AccessTools.TypeByName("RimWorldWaylith.Bootstrap");
                FieldInfo bridgeField = bootstrap != null ? AccessTools.Field(bootstrap, "_inputBridge") : null;
                bridge = bridgeField != null ? bridgeField.GetValue(null) : null;
                if (bridge != null)
                {
                    bridgeCursor = AccessTools.Field(bridge.GetType(), "_lastCursorPos");
                    bridgeHas = AccessTools.Field(bridge.GetType(), "_hasCursorPos");
                    if (bridgeCursor != null && bridgeCursor.FieldType != cursorPos.PropertyType)
                    {
                        bridgeCursor = null;
                    }
                }
                if (double2 == null || intX == null || intY == null)
                {
                    window = null;
                }
            }
            catch (Exception e)
            {
                window = null;
                Log.Warning(Tag + "The engine's window was not found, so the freecam's pointer may drift while looking round: " + e);
            }
        }

        // ---------------------------------------------------------------- the game's jumps

        // CameraJumper.TryJumpInternal(cell, map, mode), with the game's own checks: noted for TakeJump. The game's
        // camera still jumps, which does no harm (KeepGameCamera puts it back ahead of the eye).
        public static void JumpPrefix(IntVec3 __0, Map __1, CameraJumper.MovementMode __2)
        {
            if (!ready || !Wanted || Current.ProgramState != ProgramState.Playing || __1 == null || !__0.IsValid
                || !Find.Maps.Contains(__1) || !__0.InBounds(__1))
            {
                return;
            }
            bool cut = __2 == CameraJumper.MovementMode.Cut || !Prefs.SmoothCameraJumps || __1 != Find.CurrentMap
                || RimWorld.Planet.WorldRendererUtility.WorldSelected;
            NoteJump(__1, __0.ToVector3Shifted(), cut);
        }

        // CameraJumper.TrySelectInternal(thing), which jumps only when it changes map or leaves the world.
        public static void SelectPrefix(Thing __0)
        {
            if (!ready || !Wanted || Current.ProgramState != ProgramState.Playing || __0 == null || !__0.Spawned
                || !__0.def.selectable)
            {
                return;
            }
            if (__0.Map != Find.CurrentMap || RimWorld.Planet.WorldRendererUtility.WorldSelected)
            {
                NoteJump(__0.Map, __0.Position.ToVector3Shifted(), true);
            }
        }

        private static void NoteJump(Map m, Vector3 at, bool cut)
        {
            jumpMap = m;
            jumpAt = at;
            jumpCut = cut;
            jumpFrame = Time.frameCount;
        }

        // A jump noted for this map: the eye glides (or cuts) to where it looks at the place from its own way.
        private static void TakeJump()
        {
            if (jumpMap == null)
            {
                return;
            }
            if (jumpMap != map)
            {
                if (Time.frameCount - jumpFrame > JumpWaitFrames)
                {
                    jumpMap = null;
                }
                return;
            }
            jumpMap = null;
            // How far it was from what it looked at, kept within reason.
            float distance = pitch > 1f ? eye.y / Mathf.Sin(pitch * Mathf.Deg2Rad) : JumpFarthest;
            distance = Mathf.Clamp(distance, JumpNearest, JumpFarthest);
            pitch = Mathf.Clamp(Mathf.Max(pitch, JumpLeastPitch), LeastPitch, MostPitch);
            Vector3 fwd = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            Vector3 to = new Vector3(jumpAt.x, JumpAbove, jumpAt.z) - fwd * distance;
            IntVec3 size = map.Size;
            to.x = Mathf.Clamp(to.x, -MapMargin, size.x + MapMargin);
            to.z = Mathf.Clamp(to.z, -MapMargin, size.z + MapMargin);
            to.y = Mathf.Clamp(to.y, LeastHeight, MostHeight);
            velocity = Vector3.zero;
            scrollClimb = 0f;
            float far = Vector3.Distance(new Vector3(eye.x, 0f, eye.z), new Vector3(to.x, 0f, to.z));
            glideLength = jumpCut ? 0f : GenMath.LerpDoubleClamped(0f, 70f, 0f, JumpLongest, far);
            if (glideLength <= 0f)
            {
                eye = to;
                gliding = false;
                return;
            }
            glideFrom = eye;
            glideTo = to;
            glideTime = 0f;
            gliding = true;
        }

        // The game's camera kept a little ahead of the eye on the ground: the game hears the map from there, and puts
        // its view back there on leaving. (What is drawn follows IsoRim's rays, not this.)
        private static void KeepGameCamera()
        {
            if (Find.CameraDriver == null)
            {
                return;
            }
            float y = yaw * Mathf.Deg2Rad;
            float ahead = pitch > 1f ? Mathf.Min(FocusAhead, eye.y / Mathf.Tan(pitch * Mathf.Deg2Rad)) : FocusAhead;
            Vector3 at = new Vector3(eye.x + Mathf.Sin(y) * ahead, 0f, eye.z + Mathf.Cos(y) * ahead);
            at.x = Mathf.Clamp(at.x, 0f, map.Size.x);
            at.z = Mathf.Clamp(at.z, 0f, map.Size.z);
            Find.CameraDriver.JumpToCurrentMapLoc(at);
        }

        // ---------------------------------------------------------------- IsoRim's camera, from this eye

        // How far IsoRim's view "looks" from this eye (label fading, how far rays reach, the fog): to the ground at the
        // middle of the screen, within reason, as IsoRim works it out for a pose with an eye and no focus distance.
        private static float FocusDistance(Vector3 fwd)
        {
            return fwd.y < -0.01f ? Mathf.Clamp(eye.y / -fwd.y, NearDistance, FarDistance) : FarDistance;
        }

        // Everything that names the camera Api's types (Api 66, 68), apart, so nothing of it is loaded on an older IsoRim.
        private static class Posing
        {
            public static void Listen()
            {
                IsoRim.Api.BeforeViewUpdate += Frame;
            }

            public static IDisposable Push()
            {
                return IsoRim.Api.PushViewPose(PoseOwner, Pose, new ViewPoseOptions());
            }

            public static bool EyeNow(out Vector3 at)
            {
                ViewPose now = IsoRim.Api.CurrentViewPose;
                at = now.Eye ?? Vector3.zero;
                return now.Eye.HasValue;
            }

            // The eye as IsoRim's camera this frame, while flying on the map shown.
            private static ViewPose? Pose()
            {
                if (!on || map != Find.CurrentMap)
                {
                    return null;
                }
                float fov = Mathf.Clamp(Options.freecamFov, 30f, 110f);
                try
                {
                    Quaternion look = Quaternion.Euler(pitch, yaw, 0f);
                    Camera main = Find.Camera;
                    float aspect = main != null ? main.aspect : (float)Screen.width / Mathf.Max(1, Screen.height);
                    MovePinned(look * Vector3.forward, look * Vector3.right, look * Vector3.up, Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad), aspect);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(Tag + "Moving the pinned menu failed: " + e, 0x3A7E5704);
                    pinned = null;
                }
                return ViewPose.FromEye(eye, yaw, pitch, fov);
            }
        }

        // The game's own panning by the same keys, or by the pointer at the screen's edge, is left out.
        public static void DollyPostfix(ref Vector2 __result)
        {
            if (on)
            {
                __result = Vector2.zero;
            }
        }

        // The mouse wheel, which the game's camera would take as a zoom (that does nothing while flying): with the
        // freecamScrollFlies option, up and down, ScrollStep seconds of flying a notch; the event used, so the game's
        // zoom is left as it was. A window under the pointer (a scrolled list) has already used it.
        public static void ZoomPrefix()
        {
            if (!on || !Options.freecamScrollFlies || Event.current == null || Event.current.type != EventType.ScrollWheel)
            {
                return;
            }
            if (Find.WindowStack != null && Find.WindowStack.WindowsPreventCameraMotion)
            {
                return;
            }
            // The game's input bridge (RunScrollPass) turns the wheel sideways while Shift is held, as a page scrolls
            // across: its turn is then in delta.x. Shift is the boost key by default, so take it back from there.
            float notches = Event.current.delta.y;
            if (notches == 0f && Event.current.shift)
            {
                notches = Event.current.delta.x;
            }
            if (notches == 0f)
            {
                return;
            }
            scrollClimb -= notches * ScrollStep * Mathf.Clamp(Options.freecamScrollSpeed, 0.05f, 4f)
                * Mathf.Clamp(Options.freecamSpeed, 1f, 100f) * Boost();
            Event.current.Use();
        }

        // BoostTimes while the boost key is held, else 1.
        private static float Boost()
        {
            return BoostKey != null && IsoRim.Api.KeyHeld(BoostKey, false) ? BoostTimes : 1f;
        }

        public static void GuiPostfix()
        {
            if (!on || !looking || Event.current.type != EventType.Repaint)
            {
                return;
            }
            DrawCross(UI.screenWidth * 0.5f, UI.screenHeight * 0.5f);
        }

        // Over the pinned menu: its contents are drawn in a group at its window's corner (a float menu has no margin).
        public static void MenuContentsPostfix(FloatMenu __instance)
        {
            if (!on || !looking || __instance != pinned || Event.current.type != EventType.Repaint)
            {
                return;
            }
            Rect r = __instance.windowRect;
            DrawCross(UI.screenWidth * 0.5f - r.x, UI.screenHeight * 0.5f - r.y);
        }

        // The cross at (x, y), UI px in the current group.
        private static void DrawCross(float x, float y)
        {
            Color shade = new Color(0f, 0f, 0f, 0.5f), line = new Color(1f, 1f, 1f, 0.85f);
            Widgets.DrawBoxSolid(new Rect(x - Crosshair - 1f, y - CrosshairThick * 0.5f - 1f, Crosshair * 2f + 2f, CrosshairThick + 2f), shade);
            Widgets.DrawBoxSolid(new Rect(x - CrosshairThick * 0.5f - 1f, y - Crosshair - 1f, CrosshairThick + 2f, Crosshair * 2f + 2f), shade);
            Widgets.DrawBoxSolid(new Rect(x - Crosshair, y - CrosshairThick * 0.5f, Crosshair * 2f, CrosshairThick), line);
            Widgets.DrawBoxSolid(new Rect(x - CrosshairThick * 0.5f, y - Crosshair, CrosshairThick, Crosshair * 2f), line);
        }

        // Out of play (back at the main menu, a game loading), or the camera update not run this frame while the pointer
        // is held: let it go, so the pointer is never left locked.
        public static void RootUpdatePostfix()
        {
            if (on && (Current.ProgramState != ProgramState.Playing || Current.Game == null))
            {
                on = false;
                walls = null;   // IsoRim drops its requests on leaving play
                map = null;
                posed = null;   // and its poses
            }
            if (lockedLast && (!on || lockedFrame < Time.frameCount - 1))
            {
                Unlock();
            }
            if (centreFrames > 0 && !lockedLast)
            {
                centreFrames--;
                if (freedBack)
                {
                    Warp(lookedFrom, lookedFromWindow);
                }
                else
                {
                    Warp(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), null);
                }
            }
        }
    }

    // Where the freecam was last flown on each map of a game, saved with it (by map id), so it comes back there.
    public class FreecamPlaces : GameComponent
    {
        public struct Place
        {
            public Vector3 eye;
            public float yaw, pitch;
        }

        private Dictionary<int, Place> places = new Dictionary<int, Place>();

        public FreecamPlaces(Game game)
        {
        }

        public bool TryGet(Map map, out Place place)
        {
            return places.TryGetValue(map.uniqueID, out place);
        }

        public void Set(Map map, Place place)
        {
            places[map.uniqueID] = place;
        }

        public override void ExposeData()
        {
            List<int> ids = null;
            List<Vector3> eyes = null, turns = null;   // turns: yaw and pitch in x and y
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // Maps no longer in the game are left out.
                ids = new List<int>();
                eyes = new List<Vector3>();
                turns = new List<Vector3>();
                foreach (KeyValuePair<int, Place> p in places)
                {
                    if (Find.Maps.Any(m => m.uniqueID == p.Key))
                    {
                        ids.Add(p.Key);
                        eyes.Add(p.Value.eye);
                        turns.Add(new Vector3(p.Value.yaw, p.Value.pitch, 0f));
                    }
                }
            }
            Scribe_Collections.Look(ref ids, "freecamMaps", LookMode.Value);
            Scribe_Collections.Look(ref eyes, "freecamEyes", LookMode.Value);
            Scribe_Collections.Look(ref turns, "freecamTurns", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                places = new Dictionary<int, Place>();
                if (ids != null && eyes != null && turns != null)
                {
                    for (int i = 0; i < ids.Count && i < eyes.Count && i < turns.Count; i++)
                    {
                        places[ids[i]] = new Place { eye = eyes[i], yaw = turns[i].x, pitch = turns[i].y };
                    }
                }
            }
        }
    }
}
