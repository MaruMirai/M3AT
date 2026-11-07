using System.Collections.Generic;
using UnityEngine;

namespace M3AT
{
    // The box round the head, which the gene ears and horns are fitted to.
    public sealed partial class SkinnedFigure
    {
        // The box round the head in a shape, measured once: the body's vertices the head bone moves most. A snout and the gene
        // keys (pointed ears, a heavy brow) are left out, or what is fitted to the box would be stretched off the skull.
        private void MeasureHead(Shape s)
        {
            if (s.headMeasured)
            {
                return;
            }
            bool[] head = OnHead();
            Vector3[] rest = s.pieces[0].rest;
            List<string> genes = s.geneKeys;
            Vector3 low = Vector3.positiveInfinity, high = Vector3.negativeInfinity;
            for (int i = 0; i < head.Length; i++)
            {
                if (head[i])
                {
                    Vector3 at = rest[i];
                    if (genes != null)
                    {
                        foreach (string k in genes)
                        {
                            at -= pieces[0].keys[k][i];
                        }
                    }
                    low = Vector3.Min(low, at);
                    high = Vector3.Max(high, at);
                }
            }
            s.headLow = low;
            s.headHigh = high;
            s.headMeasured = true;
        }

        private Shape BaseShape()
        {
            return ShapeOf(null, null, 0f, 0f);
        }
    }
}
