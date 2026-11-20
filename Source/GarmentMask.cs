// Worn-garment masks (bit g: garment g, the body's bit 0 left out, and the sign's, so a mask is never below 0). A long
// held 62 garments, and an Int128 126; the export has over a hundred and other mods add their own (ModGarments.cs), so a
// mask is 256 bits: a garment numbered past MaxHiding would be drawn but hide nothing and be hidden by nothing.
global using static M3AT.GarmentMasks;
using System;

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

    // 256 bits as four words, a, the lowest, to d, whose top bit is the sign: -1 (every bit set) is the "not worked out
    // yet" of the masks kept in fields, and an int given is sign-extended, as an Int128 had it.
    public readonly struct GarmentMask : IEquatable<GarmentMask>
    {
        private readonly ulong a, b, c, d;

        private GarmentMask(ulong a, ulong b, ulong c, ulong d)
        {
            this.a = a;
            this.b = b;
            this.c = c;
            this.d = d;
        }

        public static GarmentMask One
        {
            get { return new GarmentMask(1UL, 0UL, 0UL, 0UL); }
        }

        public static implicit operator GarmentMask(int n)
        {
            ulong high = n < 0 ? ulong.MaxValue : 0UL;
            return new GarmentMask((ulong)(long)n, high, high, high);
        }

        public static GarmentMask operator |(GarmentMask x, GarmentMask y)
        {
            return new GarmentMask(x.a | y.a, x.b | y.b, x.c | y.c, x.d | y.d);
        }

        public static GarmentMask operator &(GarmentMask x, GarmentMask y)
        {
            return new GarmentMask(x.a & y.a, x.b & y.b, x.c & y.c, x.d & y.d);
        }

        public static GarmentMask operator ^(GarmentMask x, GarmentMask y)
        {
            return new GarmentMask(x.a ^ y.a, x.b ^ y.b, x.c ^ y.c, x.d ^ y.d);
        }

        public static GarmentMask operator ~(GarmentMask x)
        {
            return new GarmentMask(~x.a, ~x.b, ~x.c, ~x.d);
        }

        public static GarmentMask operator <<(GarmentMask x, int n)
        {
            GarmentMask m = x;
            while (n >= 64)
            {
                m = new GarmentMask(0UL, m.a, m.b, m.c);
                n -= 64;
            }
            if (n <= 0)
            {
                return m;
            }
            return new GarmentMask(m.a << n, (m.b << n) | (m.a >> (64 - n)), (m.c << n) | (m.b >> (64 - n)), (m.d << n) | (m.c >> (64 - n)));
        }

        public static bool operator ==(GarmentMask x, GarmentMask y)
        {
            return x.a == y.a && x.b == y.b && x.c == y.c && x.d == y.d;
        }

        public static bool operator !=(GarmentMask x, GarmentMask y)
        {
            return !(x == y);
        }

        // Signed, by the top bit, then the words from the top down (only ever asked against 0 and -1).
        private static int Compare(GarmentMask x, GarmentMask y)
        {
            bool xn = (long)x.d < 0, yn = (long)y.d < 0;
            if (xn != yn)
            {
                return xn ? -1 : 1;
            }
            int k = x.d.CompareTo(y.d);
            if (k == 0) k = x.c.CompareTo(y.c);
            if (k == 0) k = x.b.CompareTo(y.b);
            if (k == 0) k = x.a.CompareTo(y.a);
            return k;
        }

        public static bool operator <(GarmentMask x, GarmentMask y) { return Compare(x, y) < 0; }
        public static bool operator >(GarmentMask x, GarmentMask y) { return Compare(x, y) > 0; }
        public static bool operator <=(GarmentMask x, GarmentMask y) { return Compare(x, y) <= 0; }
        public static bool operator >=(GarmentMask x, GarmentMask y) { return Compare(x, y) >= 0; }

        public bool Equals(GarmentMask other)
        {
            return this == other;
        }

        public override bool Equals(object obj)
        {
            return obj is GarmentMask && this == (GarmentMask)obj;
        }

        public override int GetHashCode()
        {
            ulong h = a * 0x9E3779B97F4A7C15UL ^ b * 0xC2B2AE3D27D4EB4FUL ^ c * 0x165667B19E3779F9UL ^ d * 0x27D4EB2F165667C5UL;
            return (int)(h ^ (h >> 32));
        }

        public override string ToString()
        {
            return d.ToString("x16") + c.ToString("x16") + b.ToString("x16") + a.ToString("x16");
        }
    }
}
