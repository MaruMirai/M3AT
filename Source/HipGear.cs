using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // The hip gear (IsoRim's WornGearDefs on the hip mount: the psychic lances, orbital targeters and tornado generator),
    // apparel the game uses through a verb of its own as it does a weapon. Worn, it sits in its slot on the right shoulder
    // (walk.GEAR's 'hip', WornGears). While it is used (not merely drafted), the left hand takes it from there (hip_draw, played by
    // game time, backwards to put it away) and holds it ready (hip_hold, the left arm only, so the right keeps its
    // weapon); used, it is pointed at the target by the pitch (hip_aim, the trunk and head coming too) and pushed out as it
    // goes off (hip_fire). All laid on with Top, giving way to a cast, a reload, a stagger and a daze. The weapon in the
    // right hand is not aimed meanwhile (HipVerb: IsoRim's aim takes any verb the stance is busy with). Skinned figure only.
    public static partial class WalkFigures
    {
        private const float HipAimEase = 5f;      // how fast a pawn brings the hip gear up to point it (or lowers it), per real second
        private const float HipDrawIn = 0.15f;    // the share of hip_draw over which it is eased in from the arm's own pose
        private const float HipKickTicks = 40f;   // game ticks the push of the gear going off takes to ease out

        // The hip gear the pawn wears, null for none (or with worn gear not drawn, or an export without the clips).
        private static Apparel HipGearOf(Pawn pawn)
        {
            List<Apparel> worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
            if (!WornGears || worn == null || skin == null || !skin.Has("hip_hold") || skin.ToolOf("hip") == null)
            {
                return null;
            }
            for (int i = 0; i < worn.Count; i++)
            {
                WornGearDef def = GearDefOf(worn[i].def);
                if (def != null && def.mount == "hip")
                {
                    return worn[i];
                }
            }
            return null;
        }

        // The verb the pawn's stance is busy with, if it is a worn apparel's (warming up, or just gone off and cooling
        // down); null otherwise.
        private static Verb ApparelVerb(Pawn pawn)
        {
            Stance_Busy busy = pawn.stances != null ? pawn.stances.curStance as Stance_Busy : null;
            Verb verb = busy != null ? busy.verb : null;
            return verb != null && verb.DirectOwner is CompApparelVerbOwner ? verb : null;
        }

        // Whether the pawn is using its hip gear (and it is drawn here), rather than the weapon IsoRim would take it to aim.
        private static bool HipVerb(Pawn pawn)
        {
            Verb verb = ApparelVerb(pawn);
            Apparel gear = verb != null ? HipGearOf(pawn) : null;
            return gear != null && ((CompApparelVerbOwner)verb.DirectOwner).parent == gear;
        }

        // Moves the hip gear between its slot and the left hand (Ease.hipOut, through hip_draw by game time) and eases its
        // pointing (Ease.hipAim). can: on its feet with its hands free for it; anything else puts it back in the slot at once,
        // as the hands are wanted for that.
        private static void StepHip(Pawn pawn, Ease e, bool can, bool recent, float game)
        {
            bool used = can && HipVerb(pawn);
            bool wanted = used;   // only while used: drafted, it stays in its slot until the pawn uses it
            e.hipUsing = used;
            if (used)
            {
                Stance_Busy busy = (Stance_Busy)pawn.stances.curStance;
                e.hipFace = FaceOf(pawn, busy.focusTarg) ?? e.hipFace;
            }
            float seconds = skin.SecondsOf("hip_draw");
            if (!can || !recent || seconds <= 0f)
            {
                e.hipOut = wanted ? 1f : 0f;
            }
            else
            {
                e.hipOut = Mathf.MoveTowards(e.hipOut, wanted ? 1f : 0f, game / seconds);
            }
            float aim = used && e.hipOut >= 1f ? 1f : 0f;
            e.hipAim = recent ? Mathf.MoveTowards(e.hipAim, aim, HipAimEase * Time.deltaTime) : aim;
        }

        // How far the hip gear has come from its slot to the left hand: none before hip_draw's hand closes on it, then over
        // the clip's slide; all of it once it is out.
        private static float HipShare(Ease e)
        {
            if (e.hipOut <= 0f)
            {
                return 0f;
            }
            if (e.hipOut >= 1f)
            {
                return 1f;
            }
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(skin.HipGrab, skin.HipGrab + skin.HipSlide, e.hipOut));
        }

        // How far into the push of the hip gear going off the pawn is (0 .. 1): all of it straight after, easing out.
        private static float HipKick(Pawn pawn)
        {
            Verb verb = ApparelVerb(pawn);
            if (verb == null)
            {
                return 0f;
            }
            float since = Find.TickManager.TicksGame - verb.LastShotTick;
            if (since < 0f || since >= HipKickTicks)
            {
                return 0f;
            }
            float k = 1f - since / HipKickTicks;
            return k * k;
        }

        // Lays the hip gear's clips over the figure with weight w (Top, the left arm, and pointing it the trunk too): the
        // draw while it comes out or goes back, then the hold, the pointing by the pitch and the push as it goes off.
        private static void LayHip(Pawn pawn, Ease ease, SkinnedFigure.Posed fig, float w, float size)
        {
            if (ease.hipOut <= 0f || w <= 0f)
            {
                return;
            }
            if (ease.hipOut < 1f)
            {
                int drawFrames = skin.FramesOf("hip_draw");
                skin.Top(fig, "hip_draw", ease.hipOut * (drawFrames - 1) / drawFrames, w * Mathf.SmoothStep(0f, 1f, ease.hipOut / HipDrawIn));
                return;
            }
            float aimed = skin.Has("hip_aim") ? ease.hipAim : 0f;
            skin.Top(fig, "hip_hold", LoopPhase(pawn, "hip_hold"), w * (1f - aimed));
            if (aimed <= 0f)
            {
                return;
            }
            int frames = skin.FramesOf("hip_aim");
            float reach = skin.AimPitch;
            float phase = 0f;
            if (frames > 1 && reach > 0f)
            {
                float pitch = Mathf.Clamp(AimPitchOf(pawn, AimHeight * size), -reach, reach);
                phase = (pitch + reach) / (2f * reach) * (frames - 1) / frames;   // never round past the top frame to the bottom
            }
            float kick = ease.hipUsing && skin.Has("hip_fire") ? HipKick(pawn) : 0f;
            skin.Top(fig, "hip_aim", phase, w * aimed * (1f - kick));
            skin.Top(fig, "hip_fire", phase, w * aimed * kick);
        }

        // A matrix (turn, size and place) u of the way from a to b; both are a turn, a size the same every way and a place.
        private static Matrix4x4 Between(Matrix4x4 a, Matrix4x4 b, float u)
        {
            if (u <= 0f)
            {
                return a;
            }
            if (u >= 1f)
            {
                return b;
            }
            float sa = ((Vector3)a.GetColumn(0)).magnitude, sb = ((Vector3)b.GetColumn(0)).magnitude;
            Quaternion ra = Quaternion.LookRotation(a.GetColumn(2), a.GetColumn(1));
            Quaternion rb = Quaternion.LookRotation(b.GetColumn(2), b.GetColumn(1));
            float s = Mathf.Lerp(sa, sb, u);
            return Matrix4x4.TRS(Vector3.Lerp(a.GetColumn(3), b.GetColumn(3), u), Quaternion.Slerp(ra, rb, u), new Vector3(s, s, s));
        }
    }
}
