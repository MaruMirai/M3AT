using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Muzzle flash (skinned figure only, the muzzleFlash mod option): a gun fired by a figure this mod draws flashes at
    // the end of its barrel as drawn, a tongue of fire along the shot and a star at the tip, for FlashSeconds of real time
    // after each shot. IsoRim already puts the game's own flash (the ShotFlash fleck, a soft glow) at the same point,
    // standing up; this goes with it. Only weapons whose verb has a muzzleFlashScale flash (so not bows, nor melee), as big
    // as the square root of that against a rifle's (9). Drawn with the ShotFlash fleck's own glow material, after IsoRim's
    // light pass, so it lights up at night.
    public static partial class WalkFigures
    {
        private const float FlashSeconds = 0.07f;   // real seconds a flash lasts
        private const float FlashLength = 0.42f;    // cells: the tongue's length at a rifle's scale
        private const float FlashWidth = 0.2f;      // cells: and its width
        private const float FlashStar = 0.3f;       // cells: the star at the tip, across
        private const float FlashBack = 0.1f;       // of the tongue's length: how far it starts back inside the barrel
        private const float FlashRifle = 9f;        // a rifle's muzzleFlashScale, the size the numbers above are for
        private const int FlashLateTicks = 4;       // game ticks: a shot first seen later than this is not flashed (it was off screen)
        private const int FlashQueue = 3150;        // after IsoRim's light pass (3099)

        private static Material flashMat;
        private static bool flashFailed;

        public static bool MuzzleFlashes   // false: no flash of our own (the mod's options set it)
        {
            get { return !flashFailed && (SettingsMod.Settings == null || SettingsMod.Settings.muzzleFlash); }
        }

        // From DrawWeapon while aiming, after the barrel's end is known: tip is the model's (true cells), else NoTip and
        // IsoRim is asked for the point it recorded from the slab (FigureTip). aim: the barrel's way, true cells.
        private static void DrawMuzzleFlash(Pawn pawn, Thing weapon, Ease ease, Vector3 tip, Vector3 aim)
        {
            try
            {
                Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
                Verb verb = busy != null && busy.verb != null ? busy.verb
                    : pawn.equipment != null && pawn.equipment.PrimaryEq != null ? pawn.equipment.PrimaryEq.PrimaryVerb : null;
                if (verb == null || verb.verbProps == null || verb.verbProps.muzzleFlashScale <= 0f || verb.LastShotTick < 0)
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (verb.LastShotTick != ease.flashShot)
                {
                    ease.flashShot = verb.LastShotTick;
                    if (now - verb.LastShotTick <= FlashLateTicks)
                    {
                        ease.flashAt = Time.realtimeSinceStartup;
                    }
                }
                float t = (Time.realtimeSinceStartup - ease.flashAt) / FlashSeconds;
                if (t < 0f || t >= 1f)
                {
                    return;
                }
                if (float.IsNaN(tip.x) && !MuzzleOf(pawn, weapon, out tip))
                {
                    return;
                }
                if (flashMat == null)
                {
                    Material glow = FleckDefOf.ShotFlash.graphicData.Graphic.MatSingle;
                    flashMat = new Material(glow);
                    flashMat.renderQueue = FlashQueue;
                }
                // Each shot its own shape: a little longer or shorter, the star turned another way.
                int roll = Gen.HashCombineInt(ease.flashShot, pawn.thingIDNumber);
                float jitter = 0.8f + 0.4f * ((roll & 0xff) / 255f);
                float spin = ((roll >> 8) & 0xff) / 255f * 360f;
                float big = Mathf.Sqrt(verb.verbProps.muzzleFlashScale / FlashRifle) * (1f - 0.5f * t);

                Vector3 a = aim.normalized;
                // Toward the camera (true cells), as IsoRim's own camera-facing marks have it.
                Vector3 toCam = Quaternion.Euler(View3D.Pitch - 90f, View3D.Yaw, 0f) * Vector3.up;
                Matrix4x4 squash = Matrix4x4.Scale(new Vector3(1f, 1f / View3D.Squash, 1f));
                Mesh plane = MeshPool.plane10;

                // The tongue: along the barrel, turned about it to face the camera (MeshPool.plane10 lies in x and z, its
                // face up y, so x across, z along and y toward the camera, a right-handed turn).
                Vector3 across = Vector3.Cross(toCam, a);
                if (across.sqrMagnitude < 1e-4f)
                {
                    across = Vector3.Cross(Vector3.up, a);
                }
                across.Normalize();
                Vector3 facing = Vector3.Cross(a, across);
                float length = FlashLength * big * jitter, width = FlashWidth * big;
                Vector3 middle = tip + a * (length * (0.5f - FlashBack));
                Graphics.DrawMesh(plane, Floors.DrawnMatrix(squash * Columns(across * width, facing, a * length, middle)), flashMat, 0);

                // The star at the tip, facing the camera.
                Vector3 side = Vector3.Cross(toCam, Vector3.up);
                if (side.sqrMagnitude < 1e-4f)
                {
                    side = Vector3.right;
                }
                side = Quaternion.AngleAxis(spin, toCam) * side.normalized;
                Vector3 up = Vector3.Cross(side, toCam);
                float star = FlashStar * big;
                Graphics.DrawMesh(plane, Floors.DrawnMatrix(squash * Columns(side * star, toCam, up * star, tip)), flashMat, 0);
            }
            catch (System.Exception e)
            {
                flashFailed = true;
                Log.Warning(Tag + "Drawing a muzzle flash failed, muzzle flashes are off for this session: " + e.Message);
            }
        }

        private static Matrix4x4 Columns(Vector3 x, Vector3 y, Vector3 z, Vector3 at)
        {
            return new Matrix4x4(x, y, z, new Vector4(at.x, at.y, at.z, 1f));
        }

        // The barrel's end IsoRim has for a gun drawn as a slab (true cells: x and z on the map, y over the floor).
        // Api.TryGetMuzzle is from version 44; not inlined, so an older IsoRim fails at the call, inside the caller's try.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool MuzzleOf(Pawn pawn, Thing weapon, out Vector3 tip)
        {
            tip = NoTip;
            if (Api.Version < 44)
            {
                return false;
            }
            float aim;
            return Api.TryGetMuzzle(weapon, pawn, out tip, out aim);
        }
    }
}
