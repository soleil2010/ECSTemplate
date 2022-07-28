// NetworkReader reads blittable types from an ArraySegment.
//
// This way it's allocation free, doesn't need pooling, and doesn't need one
// extra abstraction.
//
// => Transport gives an ArraySegment, we use it all the way to the end.
// => should be compatible with DOTS Jobs/Burst because we use simple types!!!
// => this is also easily testable
// => all the functions return a bool to indicate if reading succeeded or not.
//    this way we can detect invalid messages / attacks easily
// => 100% safe from allocation attacks because WE DO NOT ALLOCATE ANYTHING.
//    if WriteBytesAndSize receives a uint.max header, we don't allocate giga-
//    bytes of RAM because we just return a segment of a segment.
// => only DOTS supported blittable types like float3, FixedString, etc.
//
// Endianness:
//   DOTSNET automatically serializes full structs.
//   this only works as long as all the platforms have the same endianness,
//   which is Little Endian on Mac/Windows/Linux.
//   (see https://www.coder.work/article/247983 in case we need both endians)
//
// Use C# extensions to add your own reader functions, for example:
//
//   public static bool ReadItem(this NetworkReader reader, out Item item)
//   {
//       return reader.ReadInt(out item.Id);
//   }
//
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSNET
{
    public unsafe struct NetworkReader : INetworkReader
    {
        // the slice with Offset and Count
        NativeSlice<byte> buffer;

        // buffer.GetUnsafePtr is expensive to call.
        // let's save it once in constructor & reuse for every write.
        // for example:
        //   GetUnsafePtr was significant deep profiling for delta compression's
        //   ReadByte calls. it was 1 GetUnsafePtr() call per byte read...!
        byte* bufferPtr;

        // previously we modified Offset & Count when reading.
        // now we have a separate Position that actually starts at '0', so based
        // on Offset
        // (previously we also recreated 'segment' after every read. now we just
        //  increase Position, which is easier and faster)
        public int Position { get; set; }

        // helper field to calculate amount of bytes remaining to read
        // segment.Count is 'count from Offset', so we simply subtract Position
        // without subtracting Offset.
        // example:
        //   {0x00, 0x01, 0x02} and segment with offset = 1, count=2
        //   Remaining := 2 - 0 => 0
        //           (not 2 - offset => 2 - 1 => 1)
        public int Remaining => buffer.Length - Position;

        public NetworkReader(NativeSlice<byte> buffer)
        {
            this.buffer = buffer;
            bufferPtr = (byte*)buffer.GetUnsafePtr();
            Position = 0;
        }

        // read 'size' bytes for blittable(!) type T via fixed memory copying
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
        internal bool ReadBlittable<T>(out T value)
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

            // enough data to read?
            if (Remaining >= size)
            {
                // NativeSlice ptr doesn't need pinning :)
                byte* ptr = bufferPtr + Position;

#if UNITY_ANDROID
                // on some android systems, reading *(T*)ptr throws a NRE if
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
                T* valueBuffer = stackalloc T[1];
                UnsafeUtility.MemCpy(valueBuffer, ptr, size);
                value = valueBuffer[0];
#else
                // cast buffer to a T* pointer and then read from it.
                // => Marshal class is 6x slower in our 10mio writes benchmark
                //    value = Marshal.PtrToStructure<T>((IntPtr)ptr);
                // => UnsafeUtility.PtrToStructure does the same *(T*).
                value = *(T*)ptr;
#endif

                Position += size;
                return true;
            }
            value = new T();
            return false;
        }

        // simple types ////////////////////////////////////////////////////////
        public bool ReadByte(out byte value) => ReadBlittable(out value);
        public bool ReadBool(out bool value)
        {
            // read it as byte (which is blittable),
            // then convert to bool (which is not blittable)
            if (ReadByte(out byte temp))
            {
                value = temp != 0;
                return true;
            }
            value = false;
            return false;
        }
        public bool ReadUShort(out ushort value) => ReadBlittable(out value);
        public bool ReadShort(out short value) => ReadBlittable(out value);
        public bool ReadUInt(out uint value) => ReadBlittable(out value);
        public bool ReadInt(out int value) => ReadBlittable(out value);
        public bool ReadInt2(out int2 value) => ReadBlittable(out value);
        public bool ReadInt3(out int3 value) => ReadBlittable(out value);
        public bool ReadInt4(out int4 value) => ReadBlittable(out value);
        public bool ReadULong(out ulong value) => ReadBlittable(out value);
        public bool ReadLong(out long value) => ReadBlittable(out value);
        public bool ReadLong3(out long3 value) => ReadBlittable(out value);
        public bool ReadFloat(out float value) => ReadBlittable(out value);
        public bool ReadFloat2(out float2 value) => ReadBlittable(out value);
        public bool ReadFloat3(out float3 value) => ReadBlittable(out value);
        public bool ReadFloat4(out float4 value) => ReadBlittable(out value);
        public bool ReadDouble(out double value) => ReadBlittable(out value);
        public bool ReadDouble2(out double2 value) => ReadBlittable(out value);
        public bool ReadDouble3(out double3 value) => ReadBlittable(out value);
        public bool ReadDouble4(out double4 value) => ReadBlittable(out value);
        public bool ReadDecimal(out decimal value) => ReadBlittable(out value);
        public bool ReadQuaternion(out quaternion value) => ReadBlittable(out value);
        public bool ReadQuaternionSmallestThree(out quaternion value)
        {
            value = default;

            // make sure there is enough space in buffer.
            // the write should be atomic.
            // (our compression uses 4 bytes)
            if (Remaining < 4)
                return false;

            // read and decompress
            if (ReadUInt(out uint compressed))
            {
                value = Compression.DecompressQuaternion(compressed);
                return true;
            }
            return false;
        }

        public bool ReadBytes16(out FixedBytes16 value) => ReadBlittable(out value);
        public bool ReadBytes30(out FixedBytes30 value) => ReadBlittable(out value);
        public bool ReadBytes62(out FixedBytes62 value) => ReadBlittable(out value);
        public bool ReadBytes126(out FixedBytes126 value) => ReadBlittable(out value);
        public bool ReadBytes510(out FixedBytes510 value) => ReadBlittable(out value);
        public bool ReadBytes4094(out FixedBytes4094 value) => ReadBlittable(out value);

        public bool ReadFixedString32(out FixedString32Bytes value) => ReadBlittable(out value);
        public bool ReadFixedString64(out FixedString64Bytes value) => ReadBlittable(out value);
        public bool ReadFixedString128(out FixedString128Bytes value) => ReadBlittable(out value);
        public bool ReadFixedString512(out FixedString512Bytes value) => ReadBlittable(out value);

        // peek 4 bytes int (read them without actually modifying the position)
        // -> this is useful for cases like ReadBytesAndSize where we need to
        //    peek the header first to decide if we do a full read or not
        //    (in other words, to make it atomic)
        // -> we pass segment by value, not by reference. this way we can reuse
        //    the regular ReadInt call without any modifications to segment.
        public bool PeekShort(out short value)
        {
            int previousPosition = Position;
            bool result = ReadShort(out value);
            Position = previousPosition;
            return result;
        }

        public bool PeekUShort(out ushort value)
        {
            int previousPosition = Position;
            bool result = ReadUShort(out value);
            Position = previousPosition;
            return result;
        }

        public bool PeekInt(out int value)
        {
            int previousPosition = Position;
            bool result = ReadInt(out value);
            Position = previousPosition;
            return result;
        }

        public bool PeekUInt(out uint value)
        {
            int previousPosition = Position;
            bool result = ReadUInt(out value);
            Position = previousPosition;
            return result;
        }

        // arrays //////////////////////////////////////////////////////////////
        public bool ReadBytes(byte* bytes, int bytesLength, int size)
        {
            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (size < 0 || size > bytesLength)
                throw new System.ArgumentOutOfRangeException($"NetworkReader {nameof(ReadBytes)} size {size} needs to be between 0 and {bytesLength}");

            // make sure there is enough remaining in scratch + buffer
            if (Remaining < size)
                return false;

            // size = 0 is valid, simply do nothing
            if (size == 0)
                return true;

            // copy into bytes*
            UnsafeUtility.MemCpy(bytes, bufferPtr + Position, size);
            Position += size;
            return true;
        }

        // NetworkReader can read slices for convenience
        public bool ReadBytes(int count, out NativeSlice<byte> value)
        {
            // enough data to read?
            // => check total size before any reads to make it atomic!
            value = default;
            if (Remaining >= count)
            {
                value = new NativeSlice<byte>(buffer, Position, count);
                Position += count;
                return true;
            }
            // not enough data to read
            return false;
        }

        public bool ReadBytesAndSize(byte* bytes, int bytesLength)
        {
            // enough data to read?
            // => check total size before any reads to make it atomic!
            //    => at first it needs at least 4 bytes for the header
            //    => then it needs enough size for header + size bytes
            if (Remaining >= 4 &&
                PeekInt(out int size) &&
                0 <= size && 4 + size <= Remaining)
            {
                // we already peeked the size and it's valid. so let's skip it.
                Position += 4;

                // now do the actual bytes read
                // -> ReadBytes and ArraySegment constructor both use 'int', so we
                //    use 'int' here too. that's the max we can support. if we would
                //    use 'uint' then we would have to use a 'checked' conversion to
                //    int, which means that an attacker could trigger an Overflow-
                //    Exception. using int is big enough and fail safe.
                // -> ArraySegment.Array can't be null, so we don't have to
                //    handle that case
                return ReadBytes(bytes, bytesLength, size);
            }
            // not enough data to read
            return false;
        }

        // NetworkReader can read slices for convenience
        // read size, bytes as NativeSlice to avoid allocations
        public bool ReadBytesAndSize(out NativeSlice<byte> value)
        {
            // enough data to read?
            // => check total size before any reads to make it atomic!
            //    => at first it needs at least 4 bytes for the header
            //    => then it needs enough size for header + size bytes
            value = default;
            if (Remaining >= 4 &&
                PeekInt(out int size) &&
                0 <= size && 4 + size <= Remaining)
            {
                // we already peeked the size and it's valid. so let's skip it.
                Position += 4;

                // now do the actual bytes read
                // -> ReadBytes and ArraySegment constructor both use 'int', so we
                //    use 'int' here too. that's the max we can support. if we would
                //    use 'uint' then we would have to use a 'checked' conversion to
                //    int, which means that an attacker could trigger an Overflow-
                //    Exception. using int is big enough and fail safe.
                // -> ArraySegment.Array can't be null, so we don't have to
                //    handle that case
                return ReadBytes(size, out value);
            }
            // not enough data to read
            return false;
        }

        // fixed lists /////////////////////////////////////////////////////////
        public bool ReadFixedList32<T>(out FixedList32Bytes<T> list) where T : unmanaged =>
            ReadBlittable(out list);

        public bool ReadFixedList64<T>(out FixedList64Bytes<T> list) where T : unmanaged =>
            ReadBlittable(out list);

        public bool ReadFixedList128<T>(out FixedList128Bytes<T> list) where T : unmanaged =>
            ReadBlittable(out list);

        public bool ReadFixedList512<T>(out FixedList512Bytes<T> list) where T : unmanaged =>
            ReadBlittable(out list);
    }
}
