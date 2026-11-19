using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace M3AT
{
    // Clips another mod brings (ExpansionApi.AddClips), read into the chibi's own set after anim.txt. The file is in
    // anim.txt's form, written by export_frames.py's writer against the same rig: its bone lines must name the chibi's
    // bones in the same order (checked, as a clip made on another rig would twist every bone), and only its clip, k and
    // over lines are taken. A clip whose name the chibi already has is skipped, so an expansion can't change M3AT's own.
    public partial class SkinnedFigure
    {
        // The number of clips added, or -1 if the file doesn't fit this rig (logged by the caller).
        public int AddClips(string path, out string problem)
        {
            problem = null;
            List<string> boneNames = new List<string>();
            Dictionary<string, Clip> read = new Dictionary<string, Clip>();
            Dictionary<string, float[]> overs = new Dictionary<string, float[]>();
            Clip clip = null;
            int frame = 0, bone = 0, bones = boneHead.Length;
            foreach (string raw in File.ReadAllLines(path))
            {
                string[] p = raw.Trim().Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0 || p[0].StartsWith("#"))
                {
                    continue;
                }
                if (p[0] == "bone")
                {
                    int b;
                    if (!boneIndex.TryGetValue(p[1], out b) || b != boneNames.Count)
                    {
                        problem = "bone " + p[1] + " is not the chibi's bone " + boneNames.Count;
                        return -1;
                    }
                    boneNames.Add(p[1]);
                }
                else if (p[0] == "clip")
                {
                    if (boneNames.Count != bones)
                    {
                        problem = "it has " + boneNames.Count + " bones where the chibi has " + bones;
                        return -1;
                    }
                    int frames = int.Parse(p[2]);
                    clip = new Clip { turn = new Quaternion[frames][], head = new Vector3[frames][], seconds = p.Length > 3 ? Num(p[3]) : 0f };
                    for (int f = 0; f < frames; f++)
                    {
                        clip.turn[f] = new Quaternion[bones];
                        clip.head[f] = new Vector3[bones];
                    }
                    read[p[1]] = clip;
                    frame = bone = 0;
                }
                else if (p[0] == "k" && clip != null)
                {
                    if (bone == bones)
                    {
                        frame++;
                        bone = 0;
                    }
                    clip.turn[frame][bone] = new Quaternion(Num(p[2]), Num(p[3]), Num(p[4]), Num(p[1]));
                    clip.head[frame][bone] = Vec(p, 5);
                    bone++;
                }
                else if (p[0] == "over")
                {
                    float[] over = new float[bones];
                    for (int b = 0; b < bones && b + 2 < p.Length; b++)
                    {
                        over[b] = Num(p[b + 2]);
                    }
                    overs[p[1]] = over;
                }
            }
            List<string> added = new List<string>();
            foreach (KeyValuePair<string, Clip> pair in read)
            {
                if (clips.ContainsKey(pair.Key))
                {
                    continue;
                }
                // Each bone's turn on top of its parent's, as ReadAnim keeps them.
                foreach (Quaternion[] turn in pair.Value.turn)
                {
                    Quaternion[] model = (Quaternion[])turn.Clone();
                    for (int b = 0; b < turn.Length; b++)
                    {
                        if (boneParent[b] >= 0)
                        {
                            turn[b] = Quaternion.Inverse(model[boneParent[b]]) * model[b];
                        }
                    }
                }
                float[] laid;
                if (overs.TryGetValue(pair.Key, out laid))
                {
                    pair.Value.over = laid;
                }
                clips[pair.Key] = pair.Value;
                added.Add(pair.Key);
            }
            // Variants (<clip>~2 ...) added, of clips from either file.
            foreach (string name in added)
            {
                int mark = name.IndexOf('~');
                if (mark > 0 && clips.ContainsKey(name.Substring(0, mark)))
                {
                    string own = name.Substring(0, mark);
                    int count;
                    variants.TryGetValue(own, out count);
                    variants[own] = Mathf.Max(count, 1) + 1;
                }
            }
            return added.Count;
        }
    }
}
