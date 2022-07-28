// NetworkReader interface
// -> NetworkMessage/NetworkComponent.Serialize can use the interface
// -> different systems can use different writers depending on the use case
//
// writers might work with byte[], NativeArray, fixed byte[] for burst, etc.
using Unity.Collections;
using Unity.Mathematics;

namespace DOTSNET
{
    // NOTE: casting to INetworkReader allocates because it's an interface.
    // use the explicit types directly where possible.
    public interface INetworkReader
    {
        // postion & remaining /////////////////////////////////////////////////
        int Position { get; set; }
        int Remaining { get; }

        // simple types ////////////////////////////////////////////////////////
        bool ReadByte(out byte value);
        bool ReadBool(out bool value);
        bool ReadShort(out short value);
        bool ReadUShort(out ushort value);
        bool ReadInt(out int value);
        bool ReadInt2(out int2 value);
        bool ReadInt3(out int3 value);
        bool ReadInt4(out int4 value);
        bool ReadUInt(out uint value);
        bool ReadLong(out long value);
        bool ReadLong3(out long3 value);
        bool ReadULong(out ulong value);
        bool ReadFloat(out float value);
        bool ReadFloat2(out float2 value);
        bool ReadFloat3(out float3 value);
        bool ReadDouble(out double value);
        bool ReadDouble2(out double2 value);
        bool ReadDouble3(out double3 value);
        bool ReadDouble4(out double4 value);
        bool ReadDecimal(out decimal value);
        bool ReadQuaternion(out quaternion value);
        // write quaternion with smallest-three compression
        // see also: https://gafferongames.com/post/snapshot_compression/
        //
        // reuses our smallest three compression for quaternion->uint 32 bit.
        //
        // IMPORTANT: assumes normalized quaternion!
        //            we also normalize when decompressing.
        bool ReadQuaternionSmallestThree(out quaternion value);

        bool ReadBytes16(out FixedBytes16 value);
        bool ReadBytes30(out FixedBytes30 value);
        bool ReadBytes62(out FixedBytes62 value);
        bool ReadBytes126(out FixedBytes126 value);
        bool ReadBytes510(out FixedBytes510 value);

        bool ReadFixedString32(out FixedString32Bytes value);
        bool ReadFixedString64(out FixedString64Bytes value);
        bool ReadFixedString128(out FixedString128Bytes value);
        bool ReadFixedString512(out FixedString512Bytes value);

        bool PeekShort(out short value);
        bool PeekUShort(out ushort value);
        bool PeekInt(out int value);
        bool PeekUInt(out uint value);

        // arrays //////////////////////////////////////////////////////////////
        // read bytes into a passed byte* for native collections / burst.
        // NetworkReader128 can't read fixed buffer into slice, so we need byte*
        unsafe bool ReadBytes(byte* bytes, int bytesLength, int size);
        unsafe bool ReadBytesAndSize(byte* bytes, int bytesLength);

        // fixed lists /////////////////////////////////////////////////////////
        bool ReadFixedList32<T>(out FixedList32Bytes<T> list) where T : unmanaged;
        bool ReadFixedList64<T>(out FixedList64Bytes<T> list) where T : unmanaged;
        bool ReadFixedList128<T>(out FixedList128Bytes<T> list) where T : unmanaged;
        bool ReadFixedList512<T>(out FixedList512Bytes<T> list) where T : unmanaged;
    }
}
