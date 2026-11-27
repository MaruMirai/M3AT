using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace M3AT
{
    // A garment's own frame: the body its wearer is drawn in, made by moving and scaling the skeleton's bones (power armour
    // with longer legs, a broader chest, bigger hands). The garment kit captures a pose of its rig as one matrix a bone
    // (frame lines: each bone's deform from the base skeleton, as Blender's Armature modifier deforms the body, game space)
    // and writes the garment at rest, as it would be on the base body; here the frame becomes a body key of its own,
    // Frame_<garment>, that its wearer takes whole (as a wearer_key line would ask), so the joints, the skin, the face and
    // every garment move with it and the clips play on the new joints (Place, from the shape's boneHead).
    //
    // The body's moves, the joints' and the face centre's are the frame's skinning of the base body. Anything else with
    // no key of that frame's own (this mod's garments, other mods', hair, the visor) takes its own weights' skinning of
    // the same matrices (AddFramed), worked out the first time it is shaped in that frame: exact for whatever rides one
    // bone (a helmet scales with a scaled head), and for the framed garment itself the weights it was written with (the
    // kit writes them, own_weights), so it comes back to the shape it was modelled in.
    //
    // The whole frame is lifted (or lowered) so the body's lowest point stays where it was: lengthened legs reach below
    // the floor in Blender, and the clips put the feet where the base body's were.
    public sealed partial class SkinnedFigure
    {
        private const string FramePrefix = "Frame_";
        // Each frame's matrix per bone (by anim.txt's order, identity for a bone it leaves), by its key's name. Static, as
        // ShapePiece is: one figure is loaded at a time, and BuildFrames starts it afresh with each.
        private static readonly Dictionary<string, Matrix4x4[]> frameDeforms = new Dictionary<string, Matrix4x4[]>(StringComparer.OrdinalIgnoreCase);

        // Every garment's frame lines made into a body key and given to its wearer (after ReadAnim: the bones by name).
        private void BuildFrames()
        {
            lock (frameDeforms)
            {
                frameDeforms.Clear();
            }
            Piece body = pieces[0];
            for (int g = 1; g < pieces.Length; g++)
            {
                Piece piece = pieces[g];
                if (piece.frame == null || piece.frame.Count == 0)
                {
                    continue;
                }
                string key = FramePrefix + piece.name;
                Matrix4x4[] deform = new Matrix4x4[boneHead.Length];
                for (int b = 0; b < deform.Length; b++)
                {
                    deform[b] = Matrix4x4.identity;
                }
                foreach (KeyValuePair<string, Matrix4x4> f in piece.frame)
                {
                    int b;
                    if (boneIndex.TryGetValue(f.Key, out b))
                    {
                        deform[b] = f.Value;
                    }
                    else
                    {
                        Log.Warning(Tag + "the garment " + piece.name + "'s frame moves a bone the skeleton hasn't (" + f.Key + "), so it is left out.");
                    }
                }
                // Kept on the floor: the frame's lowest point of the body put at the base body's.
                Vector3[] moves = Skinned(body, deform);
                float lowest = float.MaxValue, framedLowest = float.MaxValue;
                for (int i = 0; i < body.rest.Length; i++)
                {
                    lowest = Mathf.Min(lowest, body.rest[i].y);
                    framedLowest = Mathf.Min(framedLowest, body.rest[i].y + moves[i].y);
                }
                float lift = body.rest.Length > 0 ? lowest - framedLowest : 0f;
                if (Mathf.Abs(lift) > 1e-5f)
                {
                    Matrix4x4 up = Matrix4x4.Translate(new Vector3(0f, lift, 0f));
                    for (int b = 0; b < deform.Length; b++)
                    {
                        deform[b] = up * deform[b];
                    }
                    for (int i = 0; i < moves.Length; i++)
                    {
                        moves[i].y += lift;
                    }
                }
                body.keys[key] = moves;
                Vector3[] joints = new Vector3[boneHead.Length];
                for (int b = 0; b < joints.Length; b++)
                {
                    joints[b] = Deformed(deform[b], boneHead[b]) - boneHead[b];
                }
                boneKeys[key] = joints;
                centreKeys[key] = headBone >= 0 && headBone < deform.Length ? Deformed(deform[headBone], faceCentre) - faceCentre : Vector3.zero;
                lock (frameDeforms)
                {
                    frameDeforms[key] = deform;
                }
                // Its wearer takes the frame whole (a new list: a retexture shares its base's).
                List<KeyValuePair<string, float>> wearer = piece.wearerKeys != null ? new List<KeyValuePair<string, float>>(piece.wearerKeys) : new List<KeyValuePair<string, float>>();
                wearer.RemoveAll(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase));
                wearer.Add(new KeyValuePair<string, float>(key, 1f));
                piece.wearerKeys = wearer;
                piece.keys.Remove(key);   // (its own weights' skinning, AddFramed, is the shape it was modelled in)
                piece.framedMoves = null;
                float most = 0f;
                for (int b = 0; b < joints.Length; b++)
                {
                    most = Mathf.Max(most, joints[b].magnitude);
                }
                Log.Message(Tag + "the garment " + piece.name + (piece.modName != null ? " (" + piece.modName + ")" : "") + " has its own frame: "
                    + piece.frame.Count + " bones, joints moved up to " + most.ToString("0.000") + " cells"
                    + (Mathf.Abs(lift) > 1e-5f ? ", lifted " + lift.ToString("0.000") + " to keep the feet on the floor" : "") + ".");
            }
        }

        // Each vertex's move under the bones' matrices, as linear blend skinning by the piece's weights (the remainder of a
        // vertex's weights under 1 left where it was).
        private static Vector3[] Skinned(Piece piece, Matrix4x4[] deform)
        {
            Vector3[] moves = new Vector3[piece.rest.Length];
            if (piece.firstWeight == null || piece.firstWeight.Length < piece.rest.Length + 1)
            {
                return moves;
            }
            for (int i = 0; i < piece.rest.Length; i++)
            {
                Vector3 at = piece.rest[i], sum = Vector3.zero;
                float total = 0f;
                for (int k = piece.firstWeight[i]; k < piece.firstWeight[i + 1]; k++)
                {
                    int b = piece.weightBone[k];
                    float w = piece.weight[k];
                    if (b < 0 || b >= deform.Length || w <= 0f)
                    {
                        continue;
                    }
                    sum += Deformed(deform[b], at) * w;
                    total += w;
                }
                moves[i] = total > 0f ? sum + at * Mathf.Max(0f, 1f - total) - at : Vector3.zero;
            }
            return moves;
        }

        // A point through a matrix's top three rows (Waylith's MultiplyPoint3x4 transposes the whole matrix each call).
        private static Vector3 Deformed(Matrix4x4 m, Vector3 p)
        {
            return new Vector3(m.m00 * p.x + m.m01 * p.y + m.m02 * p.z + m.m03,
                               m.m10 * p.x + m.m11 * p.y + m.m12 * p.z + m.m13,
                               m.m20 * p.x + m.m21 * p.y + m.m22 * p.z + m.m23);
        }

        // The frames in the mix the piece has no key of its own for, added to its shaped rest by its own weights.
        private static void AddFramed(Vector3[] rest, Piece piece, List<KeyValuePair<string, float>> mix)
        {
            for (int k = 0; k < mix.Count; k++)
            {
                string key = mix[k].Key;
                if (!key.StartsWith(FramePrefix, StringComparison.OrdinalIgnoreCase) || piece.keys.ContainsKey(key))
                {
                    continue;
                }
                Vector3[] moves = FramedMoves(piece, key);
                if (moves == null || moves.Length != rest.Length)
                {
                    continue;
                }
                float amount = mix[k].Value;
                for (int i = 0; i < rest.Length; i++)
                {
                    rest[i] += moves[i] * amount;
                }
            }
        }

        // The piece's moves in a frame by its own weights, made once (hair may be shaped off the main thread).
        private static Vector3[] FramedMoves(Piece piece, string key)
        {
            Matrix4x4[] deform;
            lock (frameDeforms)
            {
                if (!frameDeforms.TryGetValue(key, out deform))
                {
                    return null;
                }
            }
            lock (piece)
            {
                Vector3[] moves;
                if (piece.framedMoves != null && piece.framedMoves.TryGetValue(key, out moves))
                {
                    return moves;
                }
                moves = Skinned(piece, deform);
                (piece.framedMoves = piece.framedMoves ?? new Dictionary<string, Vector3[]>(StringComparer.OrdinalIgnoreCase))[key] = moves;
                return moves;
            }
        }
    }
}
