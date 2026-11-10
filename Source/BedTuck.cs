using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // Tucked in under IsoRim's blanket (the bedBlankets option, WalkFigures.DrawTuckedIn): only the head is drawn, as
    // IsoRim draws only its own figure's head in a bed that hides the sleeper's body. The chibi's body is deeper than the
    // blanket's lump in places (the arms crossed on the chest, a coat), so hiding it by depth alone let it show through.
    // Every piece keeps only its triangles with at least two corners riding the head (TuckHead of their weight on the head
    // bone and below it: the ears, a hat); the neck and all below it are left out, the body's outline and wounds too.
    // The missing limb cut (Cut) doesn't do for this: it closes a bone and all below it to its joint, so it can take the
    // limbs off but not the trunk while keeping the head, which hangs from it.
    public sealed partial class SkinnedFigure
    {
        private const float TuckHead = 0.5f;   // the share of a vertex's weight on the head that keeps it drawn tucked in

        private bool[] headChain;   // the head bone and every bone below it
        private readonly Dictionary<Piece, bool[]> headVerts = new Dictionary<Piece, bool[]>();
        private readonly Dictionary<int[], int[]> tuckedTris = new Dictionary<int[], int[]>();   // by the list it was cut from (one piece's)

        // Which of the piece's vertices ride the head.
        private bool[] HeadVerts(Piece piece)
        {
            bool[] head;
            if (headVerts.TryGetValue(piece, out head))
            {
                return head;
            }
            if (headChain == null)
            {
                headChain = new bool[boneHead.Length];
                for (int i = 0; i < boneOrder.Length; i++)
                {
                    int b = boneOrder[i];
                    headChain[b] = b == headBone || (boneParent[b] >= 0 && headChain[boneParent[b]]);
                }
            }
            head = new bool[piece.rest.Length];
            if (piece.firstWeight != null)
            {
                for (int i = 0; i < head.Length; i++)
                {
                    float share = 0f;
                    for (int k = piece.firstWeight[i]; k < piece.firstWeight[i + 1]; k++)
                    {
                        int b = piece.weightBone[k];
                        if (b >= 0 && b < headChain.Length && headChain[b])
                        {
                            share += piece.weight[k];
                        }
                    }
                    head[i] = share >= TuckHead;
                }
            }
            headVerts[piece] = head;
            return head;
        }

        // tris (of piece) less what is under the blanket while the figure is tucked in; as it is otherwise.
        private int[] Tucked(Posed p, int[] tris, Piece piece)
        {
            if (!p.tucked)
            {
                return tris;
            }
            int[] kept;
            if (!tuckedTris.TryGetValue(tris, out kept))
            {
                bool[] head = HeadVerts(piece);
                List<int> list = new List<int>();
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                    if ((head[a] ? 1 : 0) + (head[b] ? 1 : 0) + (head[c] ? 1 : 0) >= 2)
                    {
                        list.Add(a);
                        list.Add(b);
                        list.Add(c);
                    }
                }
                kept = list.ToArray();
                tuckedTris[tris] = kept;
            }
            return kept;
        }

        // True if, tucked in, nothing of this piece (a garment, the fur) shows: it isn't drawn at all.
        public bool TuckedAway(Posed p, int piece)
        {
            return p.tucked && piece > 0 && piece < pieces.Length && Tucked(p, pieces[piece].tris, pieces[piece]).Length == 0;
        }

        // The body's triangles under the blanket, for the wounds (SetWounds): hide marks each one left out.
        private void HideTucked(Posed p, bool[] hide)
        {
            if (!p.tucked)
            {
                return;
            }
            bool[] head = HeadVerts(pieces[0]);
            int[] tris = pieces[0].tris;
            for (int k = 0; k < hide.Length && 3 * k + 2 < tris.Length; k++)
            {
                if ((head[tris[3 * k]] ? 1 : 0) + (head[tris[3 * k + 1]] ? 1 : 0) + (head[tris[3 * k + 2]] ? 1 : 0) < 2)
                {
                    hide[k] = true;
                }
            }
        }
    }
}
