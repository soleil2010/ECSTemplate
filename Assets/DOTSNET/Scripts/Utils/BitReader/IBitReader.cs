// Bitpacking by vis2k for drastic bandwidth savings.
// See also: https://gafferongames.com/post/reading_and_writing_packets/
//           https://gafferongames.com/post/serialization_strategies/
//
// Why Bitpacking:
// + huge bandwidth savings possible
// + word aligned copying to buffer is fast
// + always little endian, no worrying about byte order on different systems
//
// BitReader is ATOMIC! either all is read successfully, or nothing is.
//
// BitReader does aligned reads, but still supports buffer sizes that are NOT
// multiple of 4. This is necessary because we might only read a 3 bytes message
// from a socket.
//
////////////////////////////////////////////////////////////////////////////////
//
// BitReader interface
// -> NetworkMessage/NetworkComponent.Deserialize() can use the interface
// -> different systems can use different readers depending on the use case
//
// readers might work with byte[], NativeArray, fixed byte[] for burst, etc.
//
// all the documentation is in here.
// implementations don't need to document every function over and over again.
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSNET
{
    // NOTE: casting to IBitReader allocates because it's an interface.
    // use the explicit types directly where possible.
    public interface IBitReader
    {
        // position & space ////////////////////////////////////////////////////
        int BitPosition { get; }
        int RemainingBits { get; }

        // ReadBITS ////////////////////////////////////////////////////////////
        // read 'n' bits of an uint
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
        // => named ReadUIntBITS so it's obvious that it's bits, not range!
        // => parameters as result, bits for consistency with WriteUIntBits!
        bool ReadUIntBits(out uint value, int bits);

        // peek is sometimes useful for atomic reads.
        // note: wordIndex may have been modified if we needed to copy to
        //       scratch, but the overall RemainingBits are still the same
        bool PeekUIntBits(out uint value, int bits);

        // read ulong as two uints
        // bits can be between 0 and 64.
        bool ReadULongBits(out ulong value, int bits);

        // read 'n' bits of an ushort
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
        // reuses ReadUInt for now. inline if needed.
        bool ReadUShortBits(out ushort value, int bits);

        // read 'n' bits of a byte
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
        // reuses ReadUInt for now. inline if needed.
        bool ReadByteBits(out byte value, int bits);

        // read 1 bit as bool
        // reuses ReadUInt for now. inline if needed.
        bool ReadBool(out bool value);

        // Read RANGE //////////////////////////////////////////////////////////
        // ReadUInt within a known range, packed into minimum amount of bits.
        bool ReadUInt(out uint value, uint min = uint.MinValue, uint max = uint.MaxValue);

        // ReadInt within a known range, packed into minimum amount of bits
        // by shifting to uint (which doesn't need the high order bit)
        bool ReadInt(out int value, int min = int.MinValue, int max = int.MaxValue);

        // ReadULong within a known range, packed into minimum amount of bits.
        bool ReadULong(out ulong value, ulong min = ulong.MinValue, ulong max = ulong.MaxValue);

        // ReadLong within a known range, packed into minimum amount of bits
        // by shifting to uint (which doesn't need the high order bit)
        bool ReadLong(out long value, long min = long.MinValue, long max = long.MaxValue);

        // ReadUShort within a known range, packed into minimum amount of bits.
        bool ReadUShort(out ushort value, ushort min = ushort.MinValue, ushort max = ushort.MaxValue);

        // ReadShort within a known range, packed into minimum amount of bits
        // by shifting to uint (which doesn't need the high order bit)
        bool ReadShort(out short value, short min = short.MinValue, short max = short.MaxValue);

        // ReadByte within a known range, packed into minimum amount of bits.
        bool ReadByte(out byte value, byte min = byte.MinValue, byte max = byte.MaxValue);

        // Read Uncompressed ///////////////////////////////////////////////////
        // read bytes into a passed byte[] to avoid allocations
        // note: BitReader can't read ArraySegments because we use scratch.
        bool ReadBytes(byte[] bytes, int size);

        // read bytes into a passed byte* for native collections / burst.
        // note: BitReader can't read ArraySegments because we use scratch.
        // -> bytes* doesn't have .Length so we pass that manually
        unsafe bool ReadBytes(byte* bytes, int bytesLength, int size);

        // read a byte[] into a passed byte[] to avoid allocations
        // => with size in BITS, not BYTES
        // => writer has WriteBytesBitSize too, so this is the counter-part for
        //    reading without any filler bits.
        bool ReadBytesBitSize(byte[] bytes, int sizeInBits);

        // ReadBytesBitSize version for fixed buffers.
        // it's sometimes useful to have a NetworkMessage with a variable like
        //   fixed byte payload[1200];
        // to avoid runtime allocations / gc.
        // -> bytes* doesn't have .Length so we pass that manually
        unsafe bool ReadBytesBitSize(byte* bytes, int bytesLength, int sizeInBits);

        // read 32 bits uncompressed float
        // uses FloatUInt like in the article
        bool ReadFloat(out float value);

        // float2 for convenience
        bool ReadFloat2(out float2 value);

        // float3 for convenience
        bool ReadFloat3(out float3 value);

        // read compressed float with given range and precision.
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
        bool ReadFloat(out float value, float min, float max, float precision);

        // float2 for convenience
        bool ReadFloat2(out float2 value, float min, float max, float precision);

        // float3 for convenience
        bool ReadFloat3(out float3 value, float min, float max, float precision);

        // read compressed double with given range and precision.
        // see also: https://gafferongames.com/post/serialization_strategies/
        //
        // for example:
        //   value = 12.3 in range [0..100]
        //   precision = 0.1
        //   we divide by precision. or in other words, for 0.1 we multiply by 10
        //     => value = 123 in range [0..1000] (rounded to long)
        //     => fits into 10 bits (0..1023) instead of 64 bits
        //
        // to avoid exploits, it returns false if int overflows would happen.
        bool ReadDouble(out double value, double min, double max, double precision);

        // read 64 bits uncompressed double
        // uses DoubleULong like in the article
        bool ReadDouble(out double value);

        // ECS types ///////////////////////////////////////////////////////////
        // read quaternion, uncompressed
        bool ReadQuaternion(out quaternion value);

        // read quaternion with smallest-three compression
        // see also: https://gafferongames.com/post/snapshot_compression/
        //
        // reuses our smallest three compression for quaternion->uint 32 bit.
        // maybe make this 29 bits later.
        //
        // note: normalizes when decompressing.
        bool ReadQuaternionSmallestThree(out quaternion value);

        // flat bytes
        bool ReadBytes16(out FixedBytes16 value);
        bool ReadBytes30(out FixedBytes30 value);
        bool ReadBytes62(out FixedBytes62 value);
        bool ReadBytes126(out FixedBytes126 value);
        bool ReadBytes510(out FixedBytes510 value);

        // strings
        bool ReadFixedString32(out FixedString32Bytes value);
        bool ReadFixedString64(out FixedString64Bytes value);
        bool ReadFixedString128(out FixedString128Bytes value);
        bool ReadFixedString512(out FixedString512Bytes value);
    }
}
