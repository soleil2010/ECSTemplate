// NetworkWRiter writes blittable types into a byte array and returns the
// written segment that can be sent to the Transport.
//
// This way it's allocation free, doesn't need pooling, and doesn't need one
// extra abstraction.
//
// Start with an empty segment and allow for it to grow until Array.Length!
//
// note: we don't use a NetworkWriter style class because Burst/jobs need simple
//       types. and using a NetworkWriter struct would be odd when creating
//       value copies all the time.
//
// => it's stateless. easy to test, no dependencies
// => should be compatible with DOTS Jobs/Burst because we use simple types!!!
// => doesn't care where the segment's byte[] comes from. caching it is someone
//    else's job
// => doesn't care about transport either. it just passes a segment.
// => all the functions return a bool to indicate if writing succeeded or not.
//    (it will fail if the .Array is too small)
// => 100% allocation free for MMO scale networking.
// => only DOTS supported blittable types like float3, FixedString, etc.
//
// Endianness:
//   DOTSNET automatically serializes full structs.
//   this only works as long as all the platforms have the same endianness,
//   which is Little Endian on Mac/Windows/Linux.
//   (see https://www.coder.work/article/247983 in case we need both endians)
//
// Use C# extensions to add your own writer functions, for example:
//
//   public static bool WriteItem(this SegmentWriter writer, Item item)
//   {
//       writer.WriteInt(item.Id);
//       return true;
//   }
//
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSNET
{
    public unsafe struct NetworkWriter : INetworkWriter
    {
        // the buffer to write into
        // -> NativeArray is burstable.
        // -> NativeArray doesn't need to be pinned for WriteBlittable!
        NativeArray<byte> buffer;

        // buffer.GetUnsafePtr is expensive to call.
        // let's save it once in constructor & reuse for every write.
        // for example:
        //   GetUnsafePtr was significant deep profiling for delta compression's
        //   WriteByte calls. it was 1 GetUnsafePtr() call per written byte...!
        byte* bufferPtr;

        // postion & space /////////////////////////////////////////////////////
        public int Position { get; set; }
        public int Space => buffer.IsCreated ? buffer.Length - Position : 0;

        // NativeArray<byte> constructor.
        // NetworkWriter will assume that the whole array can be written into,
        // from start to end.
        public NetworkWriter(NativeArray<byte> buffer)
        {
            this.buffer = buffer;
            bufferPtr = (byte*)buffer.GetUnsafePtr();
            Position = 0;
        }

        // end result //////////////////////////////////////////////////////////
        // returns bytes written
        public int CopyTo(byte[] destination)
        {
            if (destination.Length >= Position)
            {
                NativeArray<byte>.Copy(buffer, destination, Position);
                return Position;
            }
            return 0;
        }

        // returns bytes written
        public int CopyTo(byte* destination, int destinationLength)
        {
            if (destinationLength >= Position)
            {
                UnsafeUtility.MemCpy(destination, bufferPtr, Position);
                return Position;
            }
            return 0;
        }

        // allocation free slice of writer content.
        // only valid until writer changes.
        public NativeSlice<byte> slice =>
            new NativeSlice<byte>(buffer, 0, Position);

        ////////////////////////////////////////////////////////////////////////
        // writes bytes for blittable(!) type T via fixed memory copying
        //
        // This is not safe to expose to random structs.
        //   * StructLayout.Sequential is the default, which is safe.
        //     if the struct contains a reference type, it is converted to Auto.
        //     but since all structs here are unmanaged blittable, it's safe.
        //     see also: https://docs.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.layoutkind?view=netframework-4.8#system-runtime-interopservices-layoutkind-sequential
        //   * StructLayout.Pack depends on CPU word size.
        //     this may be different 4 or 8 on some ARM systems, etc.
        //     this is not safe, and would cause bytes/shorts etc. to be padded.
        //     see also: https://docs.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.structlayoutattribute.pack?view=net-6.0
        //   * If we force pack all to '1', they would have no padding which is
        //     great for bandwidth. but on some android systems, CPU can't read
        //     unaligned memory.
        //     see also: https://github.com/vis2k/Mirror/issues/3044
        //   * The only option would be to force explicit layout with multiples
        //     of word size. but this requires lots of weaver checking and is
        //     still questionable (IL2CPP etc.).
        internal bool WriteBlittable<T>(T value)
            where T : unmanaged
        {
            // check if blittable for safety.
            // calling this with non-blittable types like bool would otherwise
            // give us strange runtime errors.
            // (for example, 0xFF would be neither true/false in unit tests
            //  with Assert.That(value, Is.Equal(true/false))
            //
            // => it's enough to check in Editor
            // => the check is around 20% slower for 1mio reads
            // => it's definitely worth it to avoid strange non-blittable issues
#if UNITY_EDITOR
            // THIS IS NOT BURSTABLE. MAKE SURE TO ONLY USE BLITTABLE TYPES.
            // if (!UnsafeUtility.IsBlittable(typeof(T)))
            // {
            //     UnityEngine.Debug.LogError($"{typeof(T)} is not blittable!");
            //     return false;
            // }
#endif

            // calculate size
            //   sizeof(T) gets the managed size at compile time.
            //   Marshal.SizeOf<T> gets the unmanaged size at runtime (slow).
            // => our 1mio writes benchmark is 6x slower with Marshal.SizeOf<T>
            // => for blittable types, sizeof(T) is even recommended:
            // https://docs.microsoft.com/en-us/dotnet/standard/native-interop/best-practices
            int size = sizeof(T);

            // enough space in buffer?
            // => check total size before any writes to make it atomic!
            if (buffer.IsCreated && Space >= size)
            {
                // NativeArray doesn't need pinning to get byte* pointer :)
                byte* ptr = bufferPtr + Position;

#if UNITY_ANDROID
                // on some android systems, assigning *(T*)ptr throws a NRE if
                // the ptr isn't aligned (i.e. if Position is 1,2,3,5, etc.).
                // here we have to use memcpy.
                //
                // => we can't get a pointer of a struct in C# without
                //    marshalling allocations
                // => instead, we stack allocate an array of type T and use that
                // => stackalloc avoids GC and is very fast. it only works for
                //    value types, but all blittable types are anyway.
                //
                // this way, we can still support blittable reads on android.
                // see also: https://github.com/vis2k/Mirror/issues/3044
                // (solution discovered by AIIO, FakeByte, mischa)
                T* valueBuffer = stackalloc T[1]{value};
                UnsafeUtility.MemCpy(ptr, valueBuffer, size);
#else
                // cast buffer to T* pointer, then assign value to the area
                // => Marshal class is 6x slower in our 10mio writes benchmark
                //    Marshal.StructureToPtr(value, (IntPtr)ptr, false);
                // => UnsafeUtility.CopyStructureToPtr does the same *(T*).
                *(T*)ptr = value;
#endif

                Position += size;
                return true;
            }

            // not enough space to write
            return false;
        }

        // simple types ////////////////////////////////////////////////////////
        public bool WriteByte(byte value) => WriteBlittable(value);
        // bool is not blittable, so cast it to a byte first.
        public bool WriteBool(bool value) => WriteBlittable((byte)(value ? 1 : 0));
        public bool WriteShort(short value) => WriteBlittable(value);
        public bool WriteUShort(ushort value) => WriteBlittable(value);
        public bool WriteInt(int value) => WriteBlittable(value);
        public bool WriteUInt(uint value) => WriteBlittable(value);
        public bool WriteInt2(int2 value) => WriteBlittable(value);
        public bool WriteInt3(int3 value) => WriteBlittable(value);
        public bool WriteInt4(int4 value) => WriteBlittable(value);
        public bool WriteLong(long value) => WriteBlittable(value);
        public bool WriteLong3(long3 value) => WriteBlittable(value);
        public bool WriteULong(ulong value) => WriteBlittable(value);
        public bool WriteFloat(float value) => WriteBlittable(value);
        public bool WriteFloat2(float2 value) => WriteBlittable(value);
        public bool WriteFloat3(float3 value) => WriteBlittable(value);
        public bool WriteFloat4(float4 value) => WriteBlittable(value);
        public bool WriteDouble(double value) => WriteBlittable(value);
        public bool WriteDouble2(double2 value) => WriteBlittable(value);
        public bool WriteDouble3(double3 value) => WriteBlittable(value);
        public bool WriteDouble4(double4 value) => WriteBlittable(value);
        public bool WriteDecimal(decimal value) => WriteBlittable(value);
        public bool WriteQuaternion(quaternion value) => WriteBlittable(value);
        public bool WriteQuaternionSmallestThree(quaternion value) => WriteBlittable(Compression.CompressQuaternion(value));

        public bool WriteBytes16(FixedBytes16 value) => WriteBlittable(value);
        public bool WriteBytes30(FixedBytes30 value) => WriteBlittable(value);
        public bool WriteBytes62(FixedBytes62 value) => WriteBlittable(value);
        public bool WriteBytes126(FixedBytes126 value) => WriteBlittable(value);
        public bool WriteBytes510(FixedBytes510 value) => WriteBlittable(value);
        public bool WriteBytes4094(FixedBytes4094 value) => WriteBlittable(value);

        public bool WriteFixedString32(FixedString32Bytes value) => WriteBlittable(value);
        public bool WriteFixedString64(FixedString64Bytes value) => WriteBlittable(value);
        public bool WriteFixedString128(FixedString128Bytes value) => WriteBlittable(value);
        public bool WriteFixedString512(FixedString512Bytes value) => WriteBlittable(value);

        // arrays //////////////////////////////////////////////////////////////
        public unsafe bool WriteBytes(byte* bytes, int bytesLength, int offset, int size)
        {
            // make sure offset + size are within bytes* length
            // if bytesLength = 2, offset can be 0 or 1 so needs to be <
            if (offset < 0 || offset >= bytesLength)
                throw new System.ArgumentOutOfRangeException($"WriteBytes offset={offset} out of bytesLength={bytesLength}");

            // if bytesLength = 2 offset = 1, length must be within bytesLength-offset=1
            if (size < 0 || size > bytesLength - offset)
                throw new System.ArgumentOutOfRangeException($"WriteBytes size={size} out of bytesLength={bytesLength} with offset={offset}");

            // enough space in buffer?
            // => check total size before any writes to make it atomic!
            if (buffer.IsCreated && Space >= size)
            {
                byte* dst = (byte*)bufferPtr + Position;

                // write 'count' bytes at position
                // 10 mio writes: 868ms
                //   Array.Copy(value.Array, value.Offset, buffer, Position, value.Count);
                // 10 mio writes: 775ms
                //   Buffer.BlockCopy(value.Array, value.Offset, buffer, Position, value.Count);
                // 10 mio writes: 637ms
                UnsafeUtility.MemCpy(dst, bytes + offset, size);

                // update position
                Position += size;
                return true;
            }
            // not enough space to write
            return false;
        }

        public unsafe bool WriteBytes(NativeSlice<byte> value)
        {
            // enough space in buffer?
            // => check total size before any writes to make it atomic!
            if (buffer.IsCreated && Space >= value.Length)
            {
                // NativeArray doesn't need pinning :)
                byte* src = (byte*)value.GetUnsafePtr();
                byte* dst = (byte*)bufferPtr + Position;

                // write 'count' bytes at position
                // 10 mio writes: 868ms
                //   Array.Copy(value.Array, value.Offset, buffer, Position, value.Count);
                // 10 mio writes: 775ms
                //   Buffer.BlockCopy(value.Array, value.Offset, buffer, Position, value.Count);
                // 10 mio writes: 637ms
                UnsafeUtility.MemCpy(dst, src, value.Length);

                // update position
                Position += value.Length;
                return true;
            }
            // not enough space to write
            return false;
        }

        public bool WriteBytesAndSize(NativeSlice<byte> value)
        {
            // enough space in buffer?
            // => check total size before any writes to make it atomic!
            if (buffer.IsCreated && Space >= 4 + value.Length)
            {
                // writes size header first
                // -> ReadBytes and ArraySegment constructor both use 'int', so we
                //    use 'int' here too. that's the max we can support. if we would
                //    use 'uint' then we would have to use a 'checked' conversion to
                //    int, which means that an attacker could trigger an Overflow-
                //    Exception. using int is big enough and fail safe.
                // -> ArraySegment.Array can't be null, so we don't have to
                //    handle that case.
                return WriteInt(value.Length) &&
                       WriteBytes(value);
            }
            // not enough space to write
            return false;
        }
        public bool WriteNativeArray(NativeArray<byte> array, int offset, int size) => WriteBytes(new NativeSlice<byte>(array, offset, size));

        // write NativeArray<T> struct.
        // * NativeArray<byte>
        // * NativeArray<TransformMessage> etc.
        // indices are adjusted automatically based on size of T.
        public bool WriteNativeArray<T>(NativeArray<T> array, int offset, int size)
            where T : unmanaged
        {
            // note: IsBlittable check not needed because NativeArray only
            //       works with blittable types.

            // calculate size
            // NativeArray<T>.Reinterpret uses UnsafeUtility.SizeOf<U>
            // internally to check expectedSize, so we do it too.
            int sizeOfT = UnsafeUtility.SizeOf<T>();
            NativeArray<byte> bytes = array.Reinterpret<byte>(sizeOfT);

            // calculate start, length in bytes
            int startInBytes = offset * sizeOfT;
            int sizeInBytes = size * sizeOfT;

            // write NativeArray<byte>
            return WriteNativeArray(bytes, startInBytes, sizeInBytes);
        }

        // fixed lists /////////////////////////////////////////////////////////
        public bool WriteFixedList32<T>(FixedList32Bytes<T> list)
            where T : unmanaged =>
                WriteBlittable(list);

        public bool WriteFixedList64<T>(FixedList64Bytes<T> list)
            where T : unmanaged =>
                WriteBlittable(list);

        public bool WriteFixedList128<T>(FixedList128Bytes<T> list)
            where T : unmanaged =>
                WriteBlittable(list);

        public bool WriteFixedList512<T>(FixedList512Bytes<T> list)
            where T : unmanaged =>
                WriteBlittable(list);
    }
}
