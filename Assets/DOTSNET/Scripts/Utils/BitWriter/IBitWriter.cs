// Bitpacking by vis2k for drastic bandwidth savings.
// See also: https://gafferongames.com/post/reading_and_writing_packets/
//           https://gafferongames.com/post/serialization_strategies/
//
// Why Bitpacking:
// + huge bandwidth savings possible
// + word aligned copying to buffer is fast
// + always little endian, no worrying about byte order on different systems
//
// BitWriter is ATOMIC! either all is written successfully, or nothing is.
//
////////////////////////////////////////////////////////////////////////////////
//
// BitWriter interface
// -> NetworkMessage/NetworkComponent.Serialize can use the interface
// -> different systems can use different writers depending on the use case
//
// writers might work with byte[], NativeArray, fixed byte[] for burst, etc.
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSNET
{
    // NOTE: casting to IBitWriter allocates because it's an interface.
    // use the explicit types directly where possible.
    public interface IBitWriter
    {
        // postion & space /////////////////////////////////////////////////////
        // calculate space in bits, including scratch bits and buffer
        // (SpaceBits instead of SpaceInBits for consistency with RemainingBits)
        int SpaceBits { get; }

        // position is useful sometimes. read-only for now.
        // TODO set; after BitWriter implements it too
        int BitPosition { get; }

        // end result //////////////////////////////////////////////////////////
        // we need a way to get the writer's internal content after writing.
        // .ToArraySegment requires an internal byte[], but sometimes we have a
        // fixed byte[].
        // => CopyTo works in all cases though!
        //
        // returns bytes written
        //
        // IMPORTANT: remember to write scratch (if any) before copying.
        //
        // IMPORTANT: this rounds to full bytes so it should only ever be used
        //            before sending it to the socket.
        //            DO NOT pass the segment to another BitWriter. the receiver
        //            would not be able to properly BitRead beause of the filler
        //            bits.
        int CopyTo(byte[] destination);
        unsafe int CopyTo(byte* destination, int destinationLength);

        // WriteBITS ///////////////////////////////////////////////////////////
        // write 'n' bits of an uint
        // bits can be between 0 and 32.
        //
        // for example:
        //   1 bit = 0..1
        //   2 bit = 0..3
        //   3 bit = 0..7
        //   4 bit = 0..15
        //   5 bit = 0..31
        //   6 bit = 0..63
        //   7 bit = 0..127
        //   8 bit = 0..255
        //  16 bit = 0..64k
        //
        // => named WriteUIntBITS so it's obvious that it's bits, not range!
        bool WriteUIntBits(uint value, int bits);

        // write ulong as two uints
        bool WriteULongBits(ulong value, int bits);

        // write 'n' bits of an ushort
        // bits can be between 0 and 16.
        //
        // for example:
        //   1 bit = 0..1
        //   2 bit = 0..3
        //   3 bit = 0..7
        //   4 bit = 0..15
        //   5 bit = 0..31
        //   6 bit = 0..63
        //   7 bit = 0..127
        //   8 bit = 0..255
        //  16 bit = 0..64k
        //
        // reuses WriteUInt for now. inline if needed.
        bool WriteUShortBits(ushort value, int bits);

        // write 'n' bits of a byte
        // bits can be between 0 and 8.
        //
        // for example:
        //   1 bit = 0..1
        //   2 bit = 0..3
        //   3 bit = 0..7
        //   4 bit = 0..15
        //   5 bit = 0..31
        //   6 bit = 0..63
        //   7 bit = 0..127
        //   8 bit = 0..255
        //
        // reuses WriteUInt for now. inline if needed.
        bool WriteByteBits(byte value, int bits);

        // write bool as 1 bit
        // reuses WriteUInt for now. inline if needed.
        bool WriteBool(bool value);

        // Write RANGE /////////////////////////////////////////////////////////
        // WriteUInt within a known range, packing into minimum amount of bits.
        bool WriteUInt(uint value, uint min = uint.MinValue, uint max = uint.MaxValue);

        // WriteInt within a known range, packing into minimum amount of bits
        // by shifting to uint (which doesn't need the high order bit)
        bool WriteInt(int value, int min = int.MinValue, int max = int.MaxValue);

        // WriteULong within a known range, packing into minimum amount of bits.
        bool WriteULong(ulong value, ulong min = ulong.MinValue, ulong max = ulong.MaxValue);

        // WriteLong within a known range, packing into minimum amount of bits
        // by shifting to ulong (which doesn't need the high order bit)
        bool WriteLong(long value, long min = long.MinValue, long max = long.MaxValue);

        // WriteUShort within a known range, packing into minimum amount of bits.
        bool WriteUShort(ushort value, ushort min = ushort.MinValue, ushort max = ushort.MaxValue);

        // WriteShort within a known range, packing into minimum amount of bits
        // by shifting to uint (which doesn't need the high order bit)
        bool WriteShort(short value, short min = short.MinValue, short max = short.MaxValue);

        // WriteByte within a known range, packing into minimum amount of bits.
        bool WriteByte(byte value, byte min = byte.MinValue, byte max = byte.MaxValue);

        // Write Uncompressed //////////////////////////////////////////////////
        // write a byte[]
        // note: BitReader can't read ArraySegments because we use scratch.
        //       for consistency, we also don't write ArraySegments here.
        bool WriteBytes(byte[] bytes, int offset, int size);

        // write a NativeSlice<byte>
        bool WriteBytes(NativeSlice<byte> bytes);

        // write a byte[] with size in BITS, not BYTES
        // we might want to pass in another writer's content without filler bits
        // for example:
        //   ArraySegment<byte> segment = other.segment;
        //   WriteBytesBitSize(segment.Array, segment.Offset, other.BitPosition)
        // instead of
        //   WriteBytesBitSize(segment.Array, segment.Offset, segment.Count)
        // which would include filler bits, making it impossible to BitRead more
        // than one writer's content later.
        bool WriteBytesBitSize(byte[] bytes, int offsetInBytes, int sizeInBits);

        // WriteBytesBitSize version for fixed buffers.
        // it's sometimes useful to have a NetworkMessage with a variable like
        //   fixed byte payload[1200];
        // to avoid runtime allocations / gc.
        // -> bytes* doesn't have .Length so we pass that manually
        unsafe bool WriteBytesBitSize(byte* bytes, int bytesLength, int offsetInBytes, int sizeInBits);

        // write 32 bit uncompressed float
        // reuses WriteUInt via FloatUInt like in the article
        bool WriteFloat(float value);

        // float2 for convenience
        bool WriteFloat2(float2 value);

        // float3 for convenience
        bool WriteFloat3(float3 value);

        // write compressed float with given range and precision.
        // see also: https://gafferongames.com/post/serialization_strategies/
        //
        // for example:
        //   value = 12.3 in range [0..100]
        //   precision = 0.1
        //   we divide by precision. or in other words, for 0.1 we multiply by 10
        //     => value = 123 in range [0..1000] (rounded to int)
        //     => fits into 10 bits (0..1023) instead of 32 bits
        //
        // to avoid exploits, it returns false if int overflows would happen.
        bool WriteFloat(float value, float min, float max, float precision);

        // float2 for convenience
        bool WriteFloat2(float2 value, float min, float max, float precision);

        // float3 for convenience
        bool WriteFloat3(float3 value, float min, float max, float precision);

        // write 64 bit uncompressed double
        // reuses WriteULong via DoubleULong like in the article
        bool WriteDouble(double value);

        // write compressed double with given range and precision.
        // see also: https://gafferongames.com/post/serialization_strategies/
        //
        // for example:
        //   value = 12.3 in range [0..100]
        //   precision = 0.1
        //   we divide by precision. or in other words, for 0.1 we multiply by 10
        //     => value = 123 in range [0..1000] (rounded to int)
        //     => fits into 10 bits (0..1023) instead of 64 bits
        //
        // to avoid exploits, it returns false if long overflows would happen.
        bool WriteDouble(double value, double min, double max, double precision);

        // ECS types ///////////////////////////////////////////////////////////
        // write quaternion, uncompressed
        bool WriteQuaternion(quaternion value);

        // write quaternion with smallest-three compression
        // see also: https://gafferongames.com/post/snapshot_compression/
        //
        // reuses our smallest three compression for quaternion->uint 32 bit.
        // maybe make this 29 bits later.
        //
        // IMPORTANT: assumes normalized quaternion!
        //            we also normalize when decompressing.
        bool WriteQuaternionSmallestThree(quaternion value);

        // bytes
        bool WriteBytes16(FixedBytes16 value);
        bool WriteBytes30(FixedBytes30 value);
        bool WriteBytes62(FixedBytes62 value);
        bool WriteBytes126(FixedBytes126 value);
        bool WriteBytes510(FixedBytes510 value);

        // strings
        bool WriteFixedString32(FixedString32Bytes value);
        bool WriteFixedString64(FixedString64Bytes value);
        bool WriteFixedString128(FixedString128Bytes value);
        bool WriteFixedString512(FixedString512Bytes value);
    }
}
