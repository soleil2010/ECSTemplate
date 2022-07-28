// optional varint functions to minimize data sizes.
// => this is always useful here and there over the years
// => might as well keep it for convenience
using System;

namespace DOTSNET
{
    public static class VarInt
    {
        // compress ulong varint.
        // same result for int, short and byte. only need one function.
        //   named 'VarUInt' to keep the 'VarInt' name instead of 'VarLong'.
        //   and 'UInt' because there are zigzag versions for Int too.
        //
        // DO NOT use this for NetworkComponent serializations.
        // those are delta compressed and need a fixed size all the time.
        // VarInt is for other compression algorithms.
        public static bool WriteVarUInt(ref NetworkWriter writer, ulong value)
        {
            if (value <= 240)
            {
                return writer.WriteByte((byte)value);
            }
            if (value <= 2287)
            {
                return writer.WriteByte((byte)(((value - 240) >> 8) + 241)) &&
                       writer.WriteByte((byte)((value - 240) & 0xFF));
            }
            if (value <= 67823)
            {
                return writer.WriteByte((byte)249) &&
                       writer.WriteByte((byte)((value - 2288) >> 8)) &&
                       writer.WriteByte((byte)((value - 2288) & 0xFF));
            }
            if (value <= 16777215)
            {
                return writer.WriteByte((byte)250) &&
                       writer.WriteByte((byte)(value & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 8) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 16) & 0xFF));
            }
            if (value <= 4294967295)
            {
                return writer.WriteByte((byte)251) &&
                       writer.WriteByte((byte)(value & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 8) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 16) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 24) & 0xFF));
            }
            if (value <= 1099511627775)
            {
                return writer.WriteByte((byte)252) &&
                       writer.WriteByte((byte)(value & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 8) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 16) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 24) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 32) & 0xFF));
            }
            if (value <= 281474976710655)
            {
                return writer.WriteByte((byte)253) &&
                       writer.WriteByte((byte)(value & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 8) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 16) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 24) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 32) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 40) & 0xFF));
            }
            if (value <= 72057594037927935)
            {
                return writer.WriteByte((byte)254) &&
                       writer.WriteByte((byte)(value & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 8) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 16) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 24) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 32) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 40) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 48) & 0xFF));
            }

            // all others
            {
                return writer.WriteByte((byte)255) &&
                       writer.WriteByte((byte)(value & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 8) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 16) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 24) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 32) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 40) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 48) & 0xFF)) &&
                       writer.WriteByte((byte)((value >> 56) & 0xFF));
            }
        }

        // Reader is a struct to avoid allocations. pass as 'ref'.
        public static bool ReadVarUInt(ref NetworkReader reader, out ulong value)
        {
            value = 0;

            if (!reader.ReadByte(out byte a0)) return false;
            if (a0 < 241)
            {
                value = a0;
                return true;
            }

            if (!reader.ReadByte(out byte a1)) return false;
            if (a0 <= 248)
            {
                value = 240 + ((a0 - (ulong)241) << 8) + a1;
                return true;
            }

            if (!reader.ReadByte(out byte a2)) return false;
            if (a0 == 249)
            {
                value = 2288 + ((ulong)a1 << 8) + a2;
                return true;
            }

            if (!reader.ReadByte(out byte a3)) return false;
            if (a0 == 250)
            {
                value = a1 + (((ulong)a2) << 8) + (((ulong)a3) << 16);
                return true;
            }

            if (!reader.ReadByte(out byte a4)) return false;
            if (a0 == 251)
            {
                value = a1 + (((ulong)a2) << 8) + (((ulong)a3) << 16) + (((ulong)a4) << 24);
                return true;
            }

            if (!reader.ReadByte(out byte a5)) return false;
            if (a0 == 252)
            {
                value = a1 + (((ulong)a2) << 8) + (((ulong)a3) << 16) + (((ulong)a4) << 24) + (((ulong)a5) << 32);
                return true;
            }

            if (!reader.ReadByte(out byte a6)) return false;
            if (a0 == 253)
            {
                value = a1 + (((ulong)a2) << 8) + (((ulong)a3) << 16) + (((ulong)a4) << 24) + (((ulong)a5) << 32) + (((ulong)a6) << 40);
                return true;
            }

            if (!reader.ReadByte(out byte a7)) return false;
            if (a0 == 254)
            {
                value = a1 + (((ulong)a2) << 8) + (((ulong)a3) << 16) + (((ulong)a4) << 24) + (((ulong)a5) << 32) + (((ulong)a6) << 40) + (((ulong)a7) << 48);
                return true;
            }

            if (!reader.ReadByte(out byte a8)) return false;
            if (a0 == 255)
            {
                value = a1 + (((ulong)a2) << 8) + (((ulong)a3) << 16) + (((ulong)a4) << 24) + (((ulong)a5) << 32) + (((ulong)a6) << 40) + (((ulong)a7) << 48)  + (((ulong)a8) << 56);
                return true;
            }

            throw new IndexOutOfRangeException("ReadVarInt failure: " + a0);
        }
    }
}