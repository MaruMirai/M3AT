using System;
using System.Collections.Generic;
using IsoRim;
using UnityEngine;
using Verse;

namespace M3AT
{
    // What M3AT offers another mod that poses the chibi itself (an expansion with clips of its own): its clip files
    // read into the chibi's set (AddClips), and posers asked before M3AT works out a figure's pose (AddPoser). A poser
    // that answers true has the figure drawn with its clips at its place, dressed as M3AT dresses it (or as it asks),
    // with M3AT's hair, face, wounds and outline; M3AT's own posture rules (lying, seats, lovin, falls) are passed over
    // for that figure while it does. Skinned figure only: with the baked frames nothing is asked.
    //
    // Units: positions in world cells; the whole-body matrix and a clip's poses in model units, which are cells at a
    // grown pawn's standard size (SizeOf scales them); yaw in degrees about up, as IsoRim's placing gives it.
    public static partial class ExpansionApi
    {
        // A property, not a const, so an expansion reads the running M3AT's rather than the one it was built against.
        public static int Version
        {
            get { return 2; }
        }

        public delegate bool Poser(Pawn pawn, PoseRequest pose);

        private static readonly List<string> clipFiles = new List<string>();
        private static readonly List<Poser> posers = new List<Poser>();

        // A clip file in anim.txt's form, made on M3AT's rig (export_frames.py's writer). Read once the chibi is loaded,
        // or at once if it is already; read again for every load. Its clips are named as the expansion likes, but
        // should carry its own prefix, as a name M3AT already has is skipped.
        public static void AddClips(string path)
        {
            if (clipFiles.Contains(path))
            {
                return;
            }
            clipFiles.Add(path);
            SkinnedFigure skin = WalkFigures.Skin;
            if (skin != null)
            {
                Read(skin, path);
            }
        }

        public static void AddPoser(Poser poser)
        {
            if (poser != null && !posers.Contains(poser))
            {
                posers.Add(poser);
            }
        }

        // Whether the chibi is loaded and skinned, so posers will be asked.
        public static bool Ready
        {
            get { return WalkFigures.Skin != null; }
        }

        public static bool HasClip(string clip)
        {
            SkinnedFigure skin = WalkFigures.Skin;
            return skin != null && clip != null && skin.Has(clip);
        }

        public static int FramesOf(string clip)
        {
            SkinnedFigure skin = WalkFigures.Skin;
            return skin == null ? 0 : skin.FramesOf(clip);
        }

        // A clip's length as exported (its clip line), game seconds; 0 for one not on the clock.
        public static float SecondsOf(string clip)
        {
            SkinnedFigure skin = WalkFigures.Skin;
            return skin == null ? 0f : skin.SecondsOf(clip);
        }

        // The figure's size for the pawn (age, gender, height and the mod options), the scale from model units to cells.
        public static float SizeOf(Pawn pawn)
        {
            return WalkFigures.SizeForApi(pawn);
        }

        // Called by WalkFigures once the chibi is loaded.
        internal static void Loaded(SkinnedFigure skin)
        {
            for (int i = 0; i < clipFiles.Count; i++)
            {
                Read(skin, clipFiles[i]);
            }
        }

        private static void Read(SkinnedFigure skin, string path)
        {
            try
            {
                string problem;
                int added = skin.AddClips(path, out problem);
                if (added < 0)
                {
                    Log.Warning(WalkFigures.Tag + "clips not read from " + path + ": " + problem + ".");
                }
                else
                {
                    Log.Message(WalkFigures.Tag + added + " clips read from " + path + ".");
                }
            }
            catch (Exception e)
            {
                Log.Warning(WalkFigures.Tag + "clips not read from " + path + ": " + e.Message);
            }
        }

        // Asked for each figure drawn, in the order added; the first to answer true poses it. A poser that throws is
        // dropped for the session.
        internal static bool Ask(Pawn pawn, PoseRequest pose)
        {
            for (int i = 0; i < posers.Count; i++)
            {
                if (AddOns.AnyOff && AddOns.IsOff(posers[i].Method.DeclaringType.Assembly))
                {
                    continue;   // its mod is off for a benchmark turn
                }
                pose.Clear();
                try
                {
                    if (posers[i](pawn, pose) && pose.layers > 0)
                    {
                        return true;
                    }
                }
                catch (Exception e)
                {
                    Log.Error(WalkFigures.Tag + "a poser failed and is dropped: " + e);
                    posers.RemoveAt(i);
                    i--;
                }
            }
            return false;
        }

        // Every poser added, for AddOns.Find.
        internal static IEnumerable<Delegate> Posers
        {
            get { return posers.ToArray(); }
        }

        internal static bool Any
        {
            get { return posers.Count > 0; }
        }
    }

    // One figure's pose, as a poser gives it. M3AT fills in what it knows (placed..., size) before asking; the poser sets
    // the rest.
    public class PoseRequest
    {
        public const int MostLayers = 4;

        // In: IsoRim's placing of the figure (where it would stand, sit or lie, its yaw and what it is on), and the size.
        public Vector3 placedAt;
        public float placedYaw;
        public FigureSituation situation;
        public float size;

        // Out: the clips blended, each at a phase (0 .. 1 round the clip) and weight.
        internal readonly string[] clips = new string[MostLayers];
        internal readonly float[] phases = new float[MostLayers];
        internal readonly float[] weights = new float[MostLayers];
        internal int layers;

        // The whole figure moved in model units before it is placed (laid down, leant, shifted), as SkinnedFigure.End takes it.
        public Matrix4x4 whole;
        // Where the figure's origin (between its feet, at rest) goes, world cells, and the way it faces.
        public Vector3 at;
        public float yaw;
        // Set: at.y is ignored and the figure is let down so its lowest point is on this height.
        public float? restOn;
        // What it is drawn wearing (SleepWear's choices); null: as M3AT would draw it there.
        public SleepWear? dress;

        public void Layer(string clip, float phase, float weight)
        {
            if (layers < MostLayers && clip != null && weight > 0f)
            {
                clips[layers] = clip;
                phases[layers] = phase;
                weights[layers] = weight;
                layers++;
            }
        }

        internal void Clear()
        {
            layers = 0;
            whole = Matrix4x4.identity;
            at = placedAt;
            yaw = placedYaw;
            restOn = null;
            dress = null;
        }
    }
}
