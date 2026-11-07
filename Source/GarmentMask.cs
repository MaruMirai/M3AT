// Worn-garment masks (bit g: garment g, the body's bit 0 left out, and the sign's, so a mask is never below 0). A long
// held 62 garments, and the export has more than that, so a garment numbered past it (they go by name, Pants among them)
// was drawn but hid nothing and was hidden by nothing.
global using GarmentMask = System.Int128;
global using static M3AT.GarmentMasks;

namespace M3AT
{
    public static class GarmentMasks
    {
        // The mask with garment g's bit alone (g from 1 to SkinnedFigure.MaxHiding).
        public static GarmentMask GarmentBit(int g)
        {
            return GarmentMask.One << g;
        }
    }
}
