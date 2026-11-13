using System.Collections.Generic;
using IsoRim;
using RimWorld;
using UnityEngine;
using Verse;

namespace M3AT
{
    // Where the chibi's eyes are, for the camera in IsoRim's first person (Api 77, Api.AddFirstPersonEye). IsoRim puts its eye
    // at one height for everyone (its eye height option, less its own figure's head drop), but the chibi's head is about 1.25
    // cells up on a grown pawn, much lower on a child, and goes where our own sitting, lying, crawling and swimming put it.
    // IsoRim draws nothing of the pawn whose head the camera is in, so the drawer is not asked for it and it is not posed
    // while the camera is there: each draw notes where the eyes were, how high for what the pawn was doing (Situation),
    // and first person takes IsoRim's own eye across the ground (its spot as drawn, a seat, the nudge forward) at the height
    // last drawn for what the pawn is doing now. A photo through a pawn's eyes (ViewPose.FirstPersonOf) of a pawn drawn this
    // frame or the last gets the eyes where they were drawn.
    public static partial class WalkFigures
    {
        private enum Situation { Standing, Seated, Lying, Crawling, Swimming }

        private sealed class Eyes
        {
            public Vector3 at;        // where they were last drawn, true space (x, z as pawn.DrawPos, y over the floor)
            public Vector3 from;      // and pawn.DrawPos then
            public int frame = -99;   // the frame they were drawn in
            public readonly float[] height = { -1f, -1f, -1f, -1f, -1f };   // the last drawn height over the floor for each Situation, cells; -1: none yet
        }

        private const int EyesFresh = 1;   // frames since the pawn was drawn its drawn eyes are still used as they are
        private const int MostEyes = 400;   // pawns whose eyes are kept before those not drawn for EyesKept frames go (PruneEyes)
        private const int EyesKept = 600;

        private static readonly Dictionary<int, Eyes> eyes = new Dictionary<int, Eyes>();
        private static bool eyeAdded;

        public static bool FirstPersonEye   // false: IsoRim's own first-person eye (the mod's options set it)
        {
            get { return SettingsMod.Settings == null || SettingsMod.Settings.firstPersonEye; }
        }

        // Called from the static constructor; IsoRim 77 or later only (NoInlining, so an older IsoRim never loads it).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AddEyeProvider()
        {
            if (!eyeAdded)
            {
                Api.AddFirstPersonEye(EyeOf);
                eyeAdded = true;
            }
        }

        private static Situation SituationOf(bool lying, bool crawling, bool seated, bool swimming)
        {
            return crawling ? Situation.Crawling : lying ? Situation.Lying : swimming ? Situation.Swimming : seated ? Situation.Seated : Situation.Standing;
        }

        // From DrawSkinned, once posed and drawn on the map: where its eyes are now. at, yaw and size as it was drawn.
        private static void NoteEyes(Pawn pawn, SkinnedFigure.Posed fig, Vector3 at, float yaw, float size, Situation situation)
        {
            Vector3 eye;
            if (pawn.health != null && !pawn.health.hediffSet.HasHead || !skin.EyeOf(fig, out eye))
            {
                return;   // no head: IsoRim's eye
            }
            Eyes e;
            if (!eyes.TryGetValue(pawn.thingIDNumber, out e))
            {
                if (eyes.Count >= MostEyes)
                {
                    PruneEyes();
                }
                e = new Eyes();
                eyes[pawn.thingIDNumber] = e;
            }
            e.at = at + Quaternion.AngleAxis(yaw, Vector3.up) * (eye * size);
            e.from = pawn.DrawPos;
            e.frame = Time.frameCount;
            e.height[(int)situation] = e.at.y;
        }

        // Those not drawn for a while go, but the pawn whose head the camera is in, which is never drawn.
        private static void PruneEyes()
        {
            Pawn viewing = Api.FirstPersonPawn;
            List<int> gone = new List<int>();
            foreach (KeyValuePair<int, Eyes> kv in eyes)
            {
                if (Time.frameCount - kv.Value.frame > EyesKept && (viewing == null || kv.Key != viewing.thingIDNumber))
                {
                    gone.Add(kv.Key);
                }
            }
            foreach (int id in gone)
            {
                eyes.Remove(id);
            }
            if (eyes.Count >= MostEyes)
            {
                eyes.Clear();
            }
        }

#if AUTOTEST
        // The frame a pawn's eyes were last noted at (-99: never), for the fpeye scenario.
        internal static int EyesFrame(Pawn pawn)
        {
            Eyes e;
            return eyes.TryGetValue(pawn.thingIDNumber, out e) ? e.frame : -99;
        }

        internal static float SizeOfForTest(Pawn pawn)
        {
            return SizeOf(pawn);
        }

        internal static Vector3? EyeForTest(Pawn pawn)
        {
            return EyeOf(pawn);
        }
#endif

        // The provider IsoRim asks (main thread only): null for a pawn not drawn by the chibi, or not drawn yet as doing what
        // it does now, and IsoRim's own eye is used.
        private static Vector3? EyeOf(Pawn pawn)
        {
            Eyes e;
            if (!FirstPersonEye || skin == null || pawn == null || !eyes.TryGetValue(pawn.thingIDNumber, out e) || !Handles(pawn))
            {
                return null;
            }
            if (Time.frameCount - e.frame <= EyesFresh)
            {
                Vector3 moved = pawn.DrawPos - e.from;
                return new Vector3(e.at.x + moved.x, e.at.y, e.at.z + moved.z);
            }
            // Not drawn (the camera is in its head): IsoRim's eye across the ground, ours in height.
            bool dead = pawn.Dead;
            bool lying = dead || pawn.GetPosture() != PawnPosture.Standing;
            Situation now = SituationOf(lying, lying && !dead && pawn.Crawling, !lying && SeatUnder(pawn) != null, !lying && pawn.Swimming);
            float height = e.height[(int)now];
            Vector3? iso = ViewPose.FirstPersonOf(pawn).Eye;   // (inside a provider: IsoRim's own, in this space)
            if (height < 0f || !iso.HasValue)
            {
                return null;
            }
            return new Vector3(iso.Value.x, height, iso.Value.z);
        }
    }

    public sealed partial class SkinnedFigure
    {
        private const float EyeLineV = 1f - 70.6f / 128f;   // Facial Animation's eye line on the face patch (export_frames.py's EYE_V)

        private int eyeTri = -2;     // the face patch triangle the eyes' middle is on (FindEye); -1: none, -2: not looked for yet
        private float eyeS, eyeT;    // and where on it

        // Where the middle between the eyes is as End left it, model units with the whole move in: on the face patch where
        // Facial Animation draws the eye line, in this shape, moved by the head bone. False without a face patch.
        public bool EyeOf(Posed p, out Vector3 at)
        {
            at = Vector3.zero;
            if (eyeTri == -2)
            {
                FindEye();
            }
            if (eyeTri < 0 || p.shape.faceRest == null)
            {
                return false;
            }
            Vector3[] rest = p.shape.faceRest;
            Vector3 a = rest[faceTris[eyeTri]], b = rest[faceTris[eyeTri + 1]], c = rest[faceTris[eyeTri + 2]];
            at = p.whole[headBone].MultiplyPoint3x4(a + (b - a) * eyeS + (c - a) * eyeT);
            return true;
        }

        private void FindEye()
        {
            eyeTri = -1;
            Vector2 uv = new Vector2(0.5f, EyeLineV);
            for (int k = 0; faceTris != null && k + 2 < faceTris.Length; k += 3)
            {
                Vector2 o = faceUvs[faceTris[k]];
                Vector2 ab = faceUvs[faceTris[k + 1]] - o, ac = faceUvs[faceTris[k + 2]] - o, ap = uv - o;
                float det = ab.x * ac.y - ab.y * ac.x;
                if (Mathf.Abs(det) < 1e-9f)
                {
                    continue;
                }
                float s = (ap.x * ac.y - ap.y * ac.x) / det, t = (ab.x * ap.y - ab.y * ap.x) / det;
                if (s >= 0f && t >= 0f && s + t <= 1f)
                {
                    eyeTri = k;
                    eyeS = s;
                    eyeT = t;
                    return;
                }
            }
        }
    }
}
