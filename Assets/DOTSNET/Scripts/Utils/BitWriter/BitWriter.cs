// default BitWriter that operates on a NativeArray<byte> passed in constructor.
// => burstable!
// => there's no need for a byte[] writer.
//    we always want NativeArray<byte> for burst support.
// => see IBitWriter interface for documentation!
using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSNET
{
    [Obsolete("Please use NetworkWriter instead. BitWriter won't be necessary with the upcoming delta compression. NetworkWriter is faster too.")]
    public struct BitWriter : IBitWriter
    {
        // scratch bits are twice the size of maximum allowed write size.
        // maximum is 32 bit, so we need 64 bit scratch for cases where
        // scratch is filled by 10 bits, and we writer another 32 into it.
        ulong scratch;
        int scratchBits;
        int wordIndex;

        // TODO reinterpret as <uint> and get rid of scratch helper variables!
        NativeArray<byte> buffer;

        // calculate space in bits, including scratch bits and buffer
        // (SpaceBits instead of SpaceInBits for consistency with RemainingBits)
        public int SpaceBits
        {
            get
            {
                // calculate space in scratch.
                int scratchBitsSpace = 32 - scratchBits;
                // calculate space in buffer. 4 bytes are needed for scratch.
                int bufferBitsSpace = (buffer.Length - wordIndex - 4) * 8;
                // scratch + buffer
                return scratchBitsSpace + bufferBitsSpace;
            }
        }

        // position is useful sometimes. read-only for now.
        // => wordIndex converted to bits + scratch position
        public int BitPosition => wordIndex * 8 + scratchBits;

        // byte[] constructor.
        // BitWriter will assume that the whole byte[] can be written into,
        // from start to end.
        public BitWriter(NativeArray<byte> buffer)
        {
            scratch = 0;
            scratchBits = 0;
            wordIndex = 0;
            this.buffer = buffer;
        }

        // helpers /////////////////////////////////////////////////////////////
        // helper function to copy the first 32 bit of scratch to buffer.
        // scratch should never have more than 32 bit of data filled because
        // write functions flush immediately after an overflow.
        // -> note that this function does not modify scratch or wordIndex.
        //    it simply copies scratch to end of buffer.
        unsafe void Copy32BitScratchToBuffer()
        {
            // extract the lower 32 bits word from scratch
            uint word = (uint)scratch & 0xFFFFFFFF;
            //UnityEngine.Debug.Log("scratch extracted word: 0x" + word.ToString("X"));

            // this is the old, slow, not aligned way to copy uint to buffer
            // + respects endianness
            // - is slower
            //buffer[wordIndex + 0] = (byte)(word);
            //buffer[wordIndex + 1] = (byte)(word >> 8);
            //buffer[wordIndex + 2] = (byte)(word >> 16);
            //buffer[wordIndex + 3] = (byte)(word >> 24);

            // fast copy word into buffer at word position
            // we use little endian order so that flushing
            // 0x2211 then 0x4433 becomes 0x11223344 in buffer
            // -> need to inverse if not on little endian
            // -> we do this with the uint, not with the buffer, so it's all
            //    still word aligned and fast!
            if (!BitConverter.IsLittleEndian)
            {
                word = Utils.SwapBytes(word);
            }

            // bitpacking works on aligned writes, but we want to support buffer
            // sizes that aren't multiple of 4 as well. it's more convenient.
            // => we want to copy 4 scratch bytes to buffer, or fewer if not
            //    enough space.
            int remaining = buffer.Length - wordIndex;
            int copy = Math.Min(4, remaining);

            // since we don't always copy exactly 4 bytes, we can't use the
            // uint* pointer assignment trick unless we want to have a special
            // case for '4 aligned' and 'not 4 aligned'. the most simple
            // solution is to use MemCopy with the variable amount of bytes.
            // => it's usually 4
            // => except for the last read which is 1,2,3 if buffer sizes is not
            //    a multiple of 4.
            byte* ptr = (byte*)buffer.GetUnsafePtr() + wordIndex;

            // memcpy the amount of bytes into buffer.
            // in the future we will use an uint[] buffer directly.
            // Unity doesn't have Buffer.MemoryCopy yet, so we need an #ifdef
            // note: we could use a for loop too, but that's slower.
            byte* wordPtr = (byte*)&word;
#if UNITY_2017_1_OR_NEWER
            UnsafeUtility.MemCpy(ptr, wordPtr, copy);
#else
            Buffer.MemoryCopy(wordPtr, ptr, copy);
#endif
        }

        // end result //////////////////////////////////////////////////////////
        public int CopyTo(byte[] destination)
        {
            // reuse byte* version
            unsafe
            {
                fixed (byte* destinationPtr = destination)
                    return CopyTo(destinationPtr, destination.Length);
            }
        }

        public unsafe int CopyTo(byte* destination, int destinationLength)
        {
            int copy = wordIndex;

            // any data in scratch that we need to include?
            if (scratchBits > 0)
            {
                // aligned fast copy the 32 scratch bits into buffer.
                // this does not modify scratch/scratchBits/wordIndex!
                // we simply copy it to the end of the buffer.
                Copy32BitScratchToBuffer();

                // round the scratchBits to a full byte
                int scratchBytes = Utils.RoundBitsToFullBytes(scratchBits);

                // copy the buffer part that includes the scratch bytes.
                // -> we don't include the full 4 bytes word that was copied
                // -> only the minimum amount of bytes to save bandwidth
                // => the copy was already fast and aligned.
                // => creating the segment is same speed for all bytes.
                copy += scratchBytes;
            }

            // copy buffer until wordIndex
            if (copy <= destinationLength)
            {
                UnsafeUtility.MemCpy(destination, buffer.GetUnsafePtr(), copy);
                return copy;
            }
            return 0;
        }

        // generate NativeSlice of written data
        //
        // IMPORTANT: this rounds to full bytes so it should only ever be used
        //            before sending it to the socket.
        //            DO NOT pass the segment to another BitWriter. the receiver
        //            would not be able to properly BitRead beause of the filler
        //            bits.
        //
        // the challenge is to INCLUDE scratch, with NO SIDE EFFECTs.
        // -> flushing scratch would have side effects and change future writes.
        // -> we need to include scratch in the segment, but not modify scratch
        //    or wordIndex
        // => in other words: WriteInt(); segment(); WriteInt() should
        //    be the same as  WriteInt(); WriteInt();
        // TODO obsolete this and use CopyTo instead later
        public NativeSlice<byte> slice
        {
            get
            {
                // any data in scratch that we need to include?
                if (scratchBits > 0)
                {
                    // aligned fast copy the 32 scratch bits into buffer.
                    // this does not modify scratch/scratchBits/wordIndex!
                    // we simply copy it to the end of the buffer.
                    Copy32BitScratchToBuffer();

                    // round the scratchBits to a full byte
                    int scratchBytes = Utils.RoundBitsToFullBytes(scratchBits);

                    // return the segment that includes the scratch bytes.
                    // -> we don't include the full 4 bytes word that was copied
                    // -> only the minimum amount of bytes to save bandwidth
                    // => the copy was already fast and aligned.
                    // => creating the segment is same speed for all bytes.
                    return new NativeSlice<byte>(buffer, 0, wordIndex + scratchBytes);
                }
                // otherwise simply return buffer until wordIndex
                else return new NativeSlice<byte>(buffer, 0, wordIndex);
            }
        }

        // WriteBITS ///////////////////////////////////////////////////////////
        public bool WriteUIntBits(uint value, int bits)
        {
            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // (at least 1 bit so we do anything. at max 32.)
            // => throws exception because the developer should fix it immediately
            //
            // NOTE: we allow 0 bits so that WriteULongBits is easier to
            //       implement where we simply do two WriteUInts and the second
            //       one might pass '0' for bits but still succeeds.
            if (bits < 0 || bits > 32)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteUIntBits)} supports between 0 and 32 bits");

            // 0 bits is valid, simply do nothing
            if (bits == 0)
                return true;

            // make sure there is enough space in buffer
            if (SpaceBits < bits)
                return false;

            // create the mask
            // for example, for 8 bits it is 0x000000FF
            // need ulong so we can shift left 32 for 32 bits
            // (would be too much for uint)
            ulong mask = (1ul << bits) - 1;
            //UnityEngine.Debug.Log("mask: 0x" + mask.ToString("X"));

            // extract the 'n' bits out of value
            // need ulong so we can shift left far enough in the step below!
            // so for 0xAABBCCDD if we extract 8 bits we extract the last 0xDD
            ulong extracted = value & mask;
            //UnityEngine.Debug.Log("extracted: 0x" + extracted.ToString("X"));

            // move the extracted part into scratch at scratch position
            // so for scratch 0x000000FF it becomes 0x0000DDFF
            scratch |= extracted << scratchBits;
            //UnityEngine.Debug.Log("scratch: 0x" + scratch.ToString("X16"));

            // update scratch position
            scratchBits += bits;

            // if we overflow more than 32 bits, then flush the 32 bits to buffer
            if (scratchBits >= 32)
            {
                // copy 32 bit scratch to buffer
                Copy32BitScratchToBuffer();

                // update word index in buffer
                wordIndex += 4;

                // move the scratch remainder to the beginning
                // (we don't just zero it because a write function might call this
                //  for an overflowed scratch. need to keep the data.)
                scratch >>= 32;
                scratchBits -= 32;
                //UnityEngine.Debug.Log("scratch flushed: 0x" + scratch.ToString("X16"));
            }

            return true;
        }

        public bool WriteULongBits(ulong value, int bits)
        {
            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // (at least 1 bit so we do anything. at max 32.)
            // => throws exception because the developer should fix it immediately
            //
            // NOTE: we allow 0 bits just like all other functions
            if (bits < 0 || bits > 64)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteULongBits)} supports between 0 and 64 bits");

            // make sure there is enough space in buffer
            // => we do two WriteUInts below. checking size first makes it atomic!
            if (SpaceBits < bits)
                return false;

            // write both halves as uint
            // => first one up to 32 bits
            // => second one the remainder. WriteUInt does nothing if bits is 0.
            uint lower = (uint)value;
            uint upper = (uint)(value >> 32);
            int lowerBits = Math.Min(bits, 32);
            int upperBits = Math.Max(bits - 32, 0);
            return WriteUIntBits(lower, lowerBits) &&
                   WriteUIntBits(upper, upperBits);
        }

        public bool WriteUShortBits(ushort value, int bits)
        {
            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // (at least 1 bit so we do anything. at max 16.)
            // => throws exception because the developer should fix it immediately
            //
            // NOTE: we allow 0 bits so that WriteULongBits is easier to
            //       implement where we simply do two WriteUInts and the second
            //       one might pass '0' for bits but still succeeds.
            if (bits < 0 || bits > 16)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteUShortBits)} supports between 0 and 16 bits");

            return WriteUIntBits(value, bits);
        }

        public bool WriteByteBits(byte value, int bits)
        {
            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // (at least 1 bit so we do anything. at max 16.)
            // => throws exception because the developer should fix it immediately
            //
            // NOTE: we allow 0 bits so that WriteULongBits is easier to
            //       implement where we simply do two WriteUInts and the second
            //       one might pass '0' for bits but still succeeds.
            if (bits < 0 || bits > 8)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteByteBits)} supports between 0 and 8 bits");

            return WriteUIntBits(value, bits);
        }

        public bool WriteBool(bool value) =>
            WriteUIntBits(value ? 1u : 0u, 1);

        // Write RANGE /////////////////////////////////////////////////////////
        public bool WriteUInt(uint value, uint min = uint.MinValue, uint max = uint.MaxValue)
        {
            // make sure value is within range
            // => throws exception because the developer should fix it immediately
            if (!(min <= value && value <= max))
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteUInt)} value={value} needs to be within min={min} and max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // write normalized value with bits required for that value range
            // for example
            //   value = 5 in range [2..9]
            //   normalized range is max-min => [0..7]
            //   value-min => '3' in the range [0..7]
            return WriteUIntBits(value - min, bits);
        }

        public bool WriteInt(int value, int min = int.MinValue, int max = int.MaxValue)
        {
            // make sure value is within range
            // => throws exception because the developer should fix it immediately
            if (!(min <= value && value <= max))
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteInt)} value={value} needs to be within min={min} and max={max}");

            // negative ints will always have the highest order bit set.
            // we shift to uint (which doesn't need the high order bit) by
            // subtracting 'min' from all values:
            //
            //     min   :=   min - min = 0
            //     max   :=   max - min
            //     value := value - min
            //
            // this works in all cases:
            //
            //   negative..positive example:
            //     value = 2, range = [-2..7]
            //     shift all by -min so by 2
            //     => value = 4, range = [0..9]
            //
            //   negative..negative example:
            //     value = -2, range = [-4..-1]
            //     shift all by -min so by 4
            //     => value = 2, range = [0..3]
            //
            //   positive..positive example:
            //     value = 4, range = [2..9]
            //     shift all by -min so by -2
            //     => value = 2, range = [0..7]
            //
            // note: int fits same range as uint, no risk for overflows.

            // calculate bits required for value range
            int bits = Utils.BitsRequired(0, (uint)(max - min));

            // write normalized value with bits required for that value range
            return WriteUIntBits((uint)(value - min), bits);
        }

        public bool WriteULong(ulong value, ulong min = ulong.MinValue, ulong max = ulong.MaxValue)
        {
            // make sure value is within range
            // => throws exception because the developer should fix it immediately
            if (!(min <= value && value <= max))
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteULong)} value={value} needs to be within min={min} and max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // write normalized value with bits required for that value range
            // for example
            //   value = 5 in range [2..9]
            //   normalized range is max-min => [0..7]
            //   value-min => '3' in the range [0..7]
            return WriteULongBits(value - min, bits);
        }

        public bool WriteLong(long value, long min = long.MinValue, long max = long.MaxValue)
        {
            // make sure value is within range
            // => throws exception because the developer should fix it immediately
            if (!(min <= value && value <= max))
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteLong)} value={value} needs to be within min={min} and max={max}");

            // negative ints will always have the highest order bit set.
            // we shift to uint (which doesn't need the high order bit) by
            // subtracting 'min' from all values:
            //
            //     min   :=   min - min = 0
            //     max   :=   max - min
            //     value := value - min
            //
            // this works in all cases:
            //
            //   negative..positive example:
            //     value = 2, range = [-2..7]
            //     shift all by -min so by 2
            //     => value = 4, range = [0..9]
            //
            //   negative..negative example:
            //     value = -2, range = [-4..-1]
            //     shift all by -min so by 4
            //     => value = 2, range = [0..3]
            //
            //   positive..positive example:
            //     value = 4, range = [2..9]
            //     shift all by -min so by -2
            //     => value = 2, range = [0..7]
            //
            // note: long fits same range as ulong, no risk for overflows.

            // calculate bits required for value range
            int bits = Utils.BitsRequired(0, (ulong)(max - min));

            // write normalized value with bits required for that value range
            return WriteULongBits((ulong)(value - min), bits);
        }

        public bool WriteUShort(ushort value, ushort min = ushort.MinValue, ushort max = ushort.MaxValue)
        {
            // make sure value is within range
            // => throws exception because the developer should fix it immediately
            if (!(min <= value && value <= max))
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteUShort)} value={value} needs to be within min={min} and max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // write normalized value with bits required for that value range
            // for example
            //   value = 5 in range [2..9]
            //   normalized range is max-min => [0..7]
            //   value-min => '3' in the range [0..7]
            return WriteUShortBits((ushort)(value - min), bits);
        }

        public bool WriteShort(short value, short min = short.MinValue, short max = short.MaxValue)
        {
            // make sure value is within range
            // => throws exception because the developer should fix it immediately
            if (!(min <= value && value <= max))
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteShort)} value={value} needs to be within min={min} and max={max}");

            // negative ints will always have the highest order bit set.
            // we shift to uint (which doesn't need the high order bit) by
            // subtracting 'min' from all values:
            //
            //     min   :=   min - min = 0
            //     max   :=   max - min
            //     value := value - min
            //
            // this works in all cases:
            //
            //   negative..positive example:
            //     value = 2, range = [-2..7]
            //     shift all by -min so by 2
            //     => value = 4, range = [0..9]
            //
            //   negative..negative example:
            //     value = -2, range = [-4..-1]
            //     shift all by -min so by 4
            //     => value = 2, range = [0..3]
            //
            //   positive..positive example:
            //     value = 4, range = [2..9]
            //     shift all by -min so by -2
            //     => value = 2, range = [0..7]
            //
            // note: short fits same range as ushort, no risk for overflows.

            // calculate bits required for value range
            int bits = Utils.BitsRequired(0, (ushort)(max - min));

            // write normalized value with bits required for that value range
            return WriteUShortBits((ushort)(value - min), bits);
        }

        public bool WriteByte(byte value, byte min = byte.MinValue, byte max = byte.MaxValue)
        {
            // make sure value is within range
            // => throws exception because the developer should fix it immediately
            if (!(min <= value && value <= max))
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteByte)} value={value} needs to be within min={min} and max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // write normalized value with bits required for that value range
            // for example
            //   value = 5 in range [2..9]
            //   normalized range is max-min => [0..7]
            //   value-min => '3' in the range [0..7]
            return WriteByteBits((byte)(value - min), bits);
        }

        // Write Uncompressed //////////////////////////////////////////////////
        public bool WriteBytes(byte[] bytes, int offset, int size)
        {
            // make sure offset is valid
            // => throws exception because the developer should fix it immediately
            if (offset < 0 || offset > bytes.Length)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteBytes)} offset {offset} needs to be between 0 and {bytes.Length}");

            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (size < 0 || size > bytes.Length - offset)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteBytes)} size {size} needs to be between 0 and {bytes.Length} - {offset}");

            // size = 0 is valid. simply do nothing.
            if (size == 0)
                return true;

            // make sure there is enough space in scratch + buffer
            // size is in bytes. convert to bits.
            if (SpaceBits < size * 8)
                return false;

            // simply reuse WriteByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (flushing scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            for (int i = 0; i < size; ++i)
                if (!WriteByteBits(bytes[offset + i], 8))
                    return false;
            return true;
        }

        public bool WriteBytes(NativeSlice<byte> bytes)
        {
            // length = 0 is valid. simply do nothing.
            if (bytes.Length == 0)
                return true;

            // make sure there is enough space in scratch + buffer
            // size is in bytes. convert to bits.
            if (SpaceBits < bytes.Length * 8)
                return false;

            // simply reuse WriteByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (flushing scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            for (int i = 0; i < bytes.Length; ++i)
                if (!WriteByteBits(bytes[i], 8))
                    return false;
            return true;
        }

        public bool WriteBytesBitSize(byte[] bytes, int offsetInBytes, int sizeInBits)
        {
            // make sure offset is valid
            // => throws exception because the developer should fix it immediately
            if (offsetInBytes < 0 || offsetInBytes > bytes.Length)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteBytesBitSize)} offsetInBytes {offsetInBytes} needs to be between 0 and {bytes.Length}");

            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (sizeInBits < 0 || sizeInBits > (bytes.Length - offsetInBytes) * 8)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteBytesBitSize)} sizeInBits {sizeInBits} needs to be between 0 and ({bytes.Length} - {offsetInBytes}) * 8");

            // size = 0 is valid. simply do nothing.
            if (sizeInBits == 0)
                return true;

            // make sure there is enough space in scratch + buffer
            if (SpaceBits < sizeInBits)
                return false;

            // simply reuse WriteByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (flushing scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            //
            // size is in bits, so / 8 to get amount of FULL BYTES we can write
            // and then write the remaining bits at the end (if any)
            // for example:
            //   sizeInBits 0 => 0 fullBytes
            //   sizeInBits 1 => 0 fullBytes
            //   sizeInBits 2 => 0 fullBytes
            //   sizeInBits 3 => 0 fullBytes
            //   sizeInBits 4 => 0 fullBytes
            //   sizeInBits 5 => 0 fullBytes
            //   sizeInBits 6 => 0 fullBytes
            //   sizeInBits 7 => 0 fullBytes
            //   sizeInBits 8 => 1 fullBytes
            //   sizeInBits 9 => 1 fullBytes
            int fullBytes = sizeInBits / 8;
            for (int i = 0; i < fullBytes; ++i)
                if (!WriteByteBits(bytes[offsetInBytes + i], 8))
                    return false;

            // now write the final partial byte (remaining bits) if any
            int remainingBits = sizeInBits - (fullBytes * 8);
            if (remainingBits > 0)
            {
                //UnityEngine.Debug.Log("writing " + remainingBits + " bits from partial byte: " + bytes[offsetInBytes + fullBytes].ToString("X2"));
                if (!WriteByteBits(bytes[offsetInBytes + fullBytes], remainingBits))
                    return false;
            }

            return true;
        }

        public unsafe bool WriteBytesBitSize(byte* bytes, int bytesLength, int offsetInBytes, int sizeInBits)
        {
            // make sure offset is valid
            // => throws exception because the developer should fix it immediately
            if (offsetInBytes < 0 || offsetInBytes > bytesLength)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteBytesBitSize)} offsetInBytes {offsetInBytes} needs to be between 0 and {bytesLength}");

            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (sizeInBits < 0 || sizeInBits > (bytesLength - offsetInBytes) * 8)
                throw new ArgumentOutOfRangeException($"BitWriter {nameof(WriteBytesBitSize)} sizeInBits {sizeInBits} needs to be between 0 and ({bytesLength} - {offsetInBytes}) * 8");

            // size = 0 is valid. simply do nothing.
            if (sizeInBits == 0)
                return true;

            // make sure there is enough space in scratch + buffer
            if (SpaceBits < sizeInBits)
                return false;

            // simply reuse WriteByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (flushing scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            //
            // size is in bits, so / 8 to get amount of FULL BYTES we can write
            // and then write the remaining bits at the end (if any)
            // for example:
            //   sizeInBits 0 => 0 fullBytes
            //   sizeInBits 1 => 0 fullBytes
            //   sizeInBits 2 => 0 fullBytes
            //   sizeInBits 3 => 0 fullBytes
            //   sizeInBits 4 => 0 fullBytes
            //   sizeInBits 5 => 0 fullBytes
            //   sizeInBits 6 => 0 fullBytes
            //   sizeInBits 7 => 0 fullBytes
            //   sizeInBits 8 => 1 fullBytes
            //   sizeInBits 9 => 1 fullBytes
            int fullBytes = sizeInBits / 8;
            for (int i = 0; i < fullBytes; ++i)
                if (!WriteByteBits(bytes[offsetInBytes + i], 8))
                    return false;

            // now write the final partial byte (remaining bits) if any
            int remainingBits = sizeInBits - (fullBytes * 8);
            if (remainingBits > 0)
            {
                //UnityEngine.Debug.Log("writing " + remainingBits + " bits from partial byte: " + bytes[offsetInBytes + fullBytes].ToString("X2"));
                if (!WriteByteBits(bytes[offsetInBytes + fullBytes], remainingBits))
                    return false;
            }

            return true;
        }

        // FloatUInt union
        [StructLayout(LayoutKind.Explicit)]
        internal struct FloatUInt
        {
            [FieldOffset(0)] internal float floatValue;
            [FieldOffset(0)] internal uint uintValue;
        }

        public bool WriteFloat(float value) =>
            WriteUIntBits(new FloatUInt{floatValue = value}.uintValue, 32);

        public bool WriteFloat2(float2 value) =>
            WriteFloat(value.x) &&
            WriteFloat(value.y);

        public bool WriteFloat3(float3 value) =>
            WriteFloat(value.x) &&
            WriteFloat(value.y) &&
            WriteFloat(value.z);

        public bool WriteFloat(float value, float min, float max, float precision)
        {
            // divide by precision. example: for 0.1 we multiply by 10.

            // we need to handle the edge case where the float becomes
            // > int.max or < int.min!
            // => we could either clamp to int.max/min, but then the reader part
            //    would be somewhat odd because if we read int.max/min, we would
            //    not know if the original value was bigger than int.max or
            //    exactly int.max.
            // => we could simply let it overflow. this would cause weird cases
            //    where a play might be teleported from int.max to int.min
            //    when overflowing.
            // => we could throw an exception to make it obvious, but then an
            //    attacker might try sending out of range values to the server,
            //    causing the server to throw a runtime exception which might
            //    stop everything unless we handled it somewhere. there is no
            //    guarantee that we do, since unlike Java, C# does not enforce
            //    handling all the underlying exceptions.
            // => the only 100% correct solution is to simply return false to
            //    indicate that this value in this range can not be serialized.
            //    in the case of multiplayer games it's safer to indicate that
            //    serialization failed and then disconnect the connection
            //    instead of potentially opening the door for exploits.
            //    (this is also the most simple solution without clamping
            //     needing a separate ClampAndRoundToInt helper function!)

            // scale at first
            float valueScaled = value / precision;
            float minScaled = min / precision;
            float maxScaled = max / precision;

            // check bounds before converting to int
            if (valueScaled < int.MinValue || valueScaled > int.MaxValue ||
                  minScaled < int.MinValue ||   minScaled > int.MaxValue ||
                  maxScaled < int.MinValue ||   maxScaled > int.MaxValue)
                return false;

            // Convert.ToInt32 so we don't need to depend on Unity.Mathf!
            int valueRounded = Convert.ToInt32(valueScaled);
            int minRounded = Convert.ToInt32(minScaled);
            int maxRounded = Convert.ToInt32(maxScaled);

            // write the int range
            return WriteInt(valueRounded, minRounded, maxRounded);
        }

        public bool WriteFloat2(float2 value, float min, float max, float precision) =>
            WriteFloat(value.x, min, max, precision) &&
            WriteFloat(value.y, min, max, precision);

        public bool WriteFloat3(float3 value, float min, float max, float precision) =>
            WriteFloat(value.x, min, max, precision) &&
            WriteFloat(value.y, min, max, precision) &&
            WriteFloat(value.z, min, max, precision);

        // DoubleUInt union
        [StructLayout(LayoutKind.Explicit)]
        internal struct DoubleULong
        {
            [FieldOffset(0)] internal double doubleValue;
            [FieldOffset(0)] internal ulong ulongValue;
        }

        public bool WriteDouble(double value) =>
            WriteULongBits(new DoubleULong{doubleValue = value}.ulongValue, 64);

        public bool WriteDouble(double value, double min, double max, double precision)
        {
            // divide by precision. example: for 0.1 we multiply by 10.

            // we need to handle the edge case where the double becomes
            // > long.max or < long.min!
            // => we could either clamp to long.max/min, but then the reader part
            //    would be somewhat odd because if we read long.max/min, we would
            //    not know if the original value was bigger than long.max or
            //    exactly long.max.
            // => we could simply let it overflow. this would cause weird cases
            //    where a play might be teleported from long.max to long.min
            //    when overflowing.
            // => we could throw an exception to make it obvious, but then an
            //    attacker might try sending out of range values to the server,
            //    causing the server to throw a runtime exception which might
            //    stop everything unless we handled it somewhere. there is no
            //    guarantee that we do, since unlike Java, C# does not enforce
            //    handling all the underlying exceptions.
            // => the only 100% correct solution is to simply return false to
            //    indicate that this value in this range can not be serialized.
            //    in the case of multiplayer games it's safer to indicate that
            //    serialization failed and then disconnect the connection
            //    instead of potentially opening the door for exploits.
            //    (this is also the most simple solution without clamping
            //     needing a separate ClampAndRoundToInt helper function!)

            // scale at first
            double valueScaled = value / precision;
            double minScaled = min / precision;
            double maxScaled = max / precision;

            // check bounds before converting to long
            if (valueScaled < long.MinValue || valueScaled > long.MaxValue ||
                  minScaled < long.MinValue ||   minScaled > long.MaxValue ||
                  maxScaled < long.MinValue ||   maxScaled > long.MaxValue)
                return false;

            // Convert.ToInt64 so we don't need to depend on Unity.Mathf!
            long valueRounded = Convert.ToInt64(valueScaled);
            long minRounded = Convert.ToInt64(minScaled);
            long maxRounded = Convert.ToInt64(maxScaled);

            // write the long range
            return WriteLong(valueRounded, minRounded, maxRounded);
        }

        // ECS types ///////////////////////////////////////////////////////////
        public bool WriteQuaternion(quaternion value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 4*4 bytes, converted to bits)
            if (SpaceBits < 16 * 8)
                return false;

            // write 4 floats
            return WriteFloat(value.value.x) &&
                   WriteFloat(value.value.y) &&
                   WriteFloat(value.value.z) &&
                   WriteFloat(value.value.w);
        }

        public bool WriteQuaternionSmallestThree(quaternion value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (our compression uses 32 bit. maybe use 29 bit later.)
            if (SpaceBits < 32)
                return false;

            // compress and write
            uint compressed = Compression.CompressQuaternion(value);
            return WriteUIntBits(compressed, 32);
        }

        public bool WriteBytes16(FixedBytes16 value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 16 bytes, converted to bits)
            if (SpaceBits < 16 * 8)
                return false;

            // write the 16 bytes

            // unsafe:
            // Unity ECS uses UnsafeUtility.AddressOf in FixedString32 etc. too.
            // => shorter code but same performance.
            //
            //unsafe
            //{
            //    byte* ptr = (byte*)UnsafeUtility.AddressOf(ref value);
            //    for (int i = 0; i < 16; ++i)
            //    {
            //        if (!WriteByteBits(ptr[i], 8))
            //            return false;
            //    }
            //    return true;
            //}

            // calling WriteByteBits 16x is a bit heavy.
            // => 1mio x WriteBytes16 with 16x WriteByteBits = 400 ms.
            // => 1mio x WriteBytes16 with  2x WriteUlong    = 230 ms
            ulong value_0to7 = (ulong)value.byte0000 << 0 |
                               (ulong)value.byte0001 << 8 |
                               (ulong)value.byte0002 << 16 |
                               (ulong)value.byte0003 << 24 |
                               (ulong)value.byte0004 << 32 |
                               (ulong)value.byte0005 << 40 |
                               (ulong)value.byte0006 << 48 |
                               (ulong)value.byte0007 << 56;

            ulong value_8to15 = (ulong)value.byte0008 << 0 |
                                (ulong)value.byte0009 << 8 |
                                (ulong)value.byte0010 << 16 |
                                (ulong)value.byte0011 << 24 |
                                (ulong)value.byte0012 << 32 |
                                (ulong)value.byte0013 << 40 |
                                (ulong)value.byte0014 << 48 |
                                (ulong)value.byte0015 << 56;

            return WriteULongBits(value_0to7, 64) &&
                   WriteULongBits(value_8to15, 64);
        }

        public bool WriteBytes30(FixedBytes30 value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 30 bytes, converted to bits)
            if (SpaceBits < 30 * 8)
                return false;

            // write the 30 bytes
            // note: could write in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return WriteBytes16(value.offset0000) &&
                   WriteByteBits(value.byte0016, 8) &&
                   WriteByteBits(value.byte0017, 8) &&
                   WriteByteBits(value.byte0018, 8) &&
                   WriteByteBits(value.byte0019, 8) &&
                   WriteByteBits(value.byte0020, 8) &&
                   WriteByteBits(value.byte0021, 8) &&
                   WriteByteBits(value.byte0022, 8) &&
                   WriteByteBits(value.byte0023, 8) &&
                   WriteByteBits(value.byte0024, 8) &&
                   WriteByteBits(value.byte0025, 8) &&
                   WriteByteBits(value.byte0026, 8) &&
                   WriteByteBits(value.byte0027, 8) &&
                   WriteByteBits(value.byte0028, 8) &&
                   WriteByteBits(value.byte0029, 8);
        }

        public bool WriteBytes62(FixedBytes62 value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 62 bytes, converted to bits)
            if (SpaceBits < 62 * 8)
                return false;

            // write the 62 bytes
            // note: could write in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return WriteBytes16(value.offset0000) &&
                   WriteBytes16(value.offset0016) &&
                   WriteBytes16(value.offset0032) &&
                   WriteByteBits(value.byte0048, 8) &&
                   WriteByteBits(value.byte0049, 8) &&
                   WriteByteBits(value.byte0050, 8) &&
                   WriteByteBits(value.byte0051, 8) &&
                   WriteByteBits(value.byte0052, 8) &&
                   WriteByteBits(value.byte0053, 8) &&
                   WriteByteBits(value.byte0054, 8) &&
                   WriteByteBits(value.byte0055, 8) &&
                   WriteByteBits(value.byte0056, 8) &&
                   WriteByteBits(value.byte0057, 8) &&
                   WriteByteBits(value.byte0058, 8) &&
                   WriteByteBits(value.byte0059, 8) &&
                   WriteByteBits(value.byte0060, 8) &&
                   WriteByteBits(value.byte0061, 8);
        }

        public bool WriteBytes126(FixedBytes126 value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 126 bytes, converted to bits)
            if (SpaceBits < 126 * 8)
                return false;

            // write the 126 bytes
            // note: could write in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return WriteBytes16(value.offset0000) &&
                   WriteBytes16(value.offset0016) &&
                   WriteBytes16(value.offset0032) &&
                   WriteBytes16(value.offset0048) &&
                   WriteBytes16(value.offset0064) &&
                   WriteBytes16(value.offset0080) &&
                   WriteBytes16(value.offset0096) &&
                   WriteByteBits(value.byte0112, 8) &&
                   WriteByteBits(value.byte0113, 8) &&
                   WriteByteBits(value.byte0114, 8) &&
                   WriteByteBits(value.byte0115, 8) &&
                   WriteByteBits(value.byte0116, 8) &&
                   WriteByteBits(value.byte0117, 8) &&
                   WriteByteBits(value.byte0118, 8) &&
                   WriteByteBits(value.byte0119, 8) &&
                   WriteByteBits(value.byte0120, 8) &&
                   WriteByteBits(value.byte0121, 8) &&
                   WriteByteBits(value.byte0122, 8) &&
                   WriteByteBits(value.byte0123, 8) &&
                   WriteByteBits(value.byte0124, 8) &&
                   WriteByteBits(value.byte0125, 8);
        }

        public bool WriteBytes510(FixedBytes510 value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 510 bytes, converted to bits)
            if (SpaceBits < 510 * 8)
                return false;

            // write the 510 bytes
            // note: could write in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return WriteBytes16(value.offset0000) &&
                   WriteBytes16(value.offset0016) &&
                   WriteBytes16(value.offset0032) &&
                   WriteBytes16(value.offset0048) &&
                   WriteBytes16(value.offset0064) &&
                   WriteBytes16(value.offset0080) &&
                   WriteBytes16(value.offset0096) &&
                   WriteBytes16(value.offset0112) &&
                   WriteBytes16(value.offset0128) &&
                   WriteBytes16(value.offset0144) &&
                   WriteBytes16(value.offset0160) &&
                   WriteBytes16(value.offset0176) &&
                   WriteBytes16(value.offset0192) &&
                   WriteBytes16(value.offset0208) &&
                   WriteBytes16(value.offset0224) &&
                   WriteBytes16(value.offset0240) &&
                   WriteBytes16(value.offset0256) &&
                   WriteBytes16(value.offset0272) &&
                   WriteBytes16(value.offset0288) &&
                   WriteBytes16(value.offset0304) &&
                   WriteBytes16(value.offset0320) &&
                   WriteBytes16(value.offset0336) &&
                   WriteBytes16(value.offset0352) &&
                   WriteBytes16(value.offset0368) &&
                   WriteBytes16(value.offset0384) &&
                   WriteBytes16(value.offset0400) &&
                   WriteBytes16(value.offset0416) &&
                   WriteBytes16(value.offset0432) &&
                   WriteBytes16(value.offset0448) &&
                   WriteBytes16(value.offset0464) &&
                   WriteBytes16(value.offset0480) &&
                   WriteByteBits(value.byte0496, 8) &&
                   WriteByteBits(value.byte0497, 8) &&
                   WriteByteBits(value.byte0498, 8) &&
                   WriteByteBits(value.byte0499, 8) &&
                   WriteByteBits(value.byte0500, 8) &&
                   WriteByteBits(value.byte0501, 8) &&
                   WriteByteBits(value.byte0502, 8) &&
                   WriteByteBits(value.byte0503, 8) &&
                   WriteByteBits(value.byte0504, 8) &&
                   WriteByteBits(value.byte0505, 8) &&
                   WriteByteBits(value.byte0506, 8) &&
                   WriteByteBits(value.byte0507, 8) &&
                   WriteByteBits(value.byte0508, 8) &&
                   WriteByteBits(value.byte0509, 8);
        }

        public bool WriteFixedString32(FixedString32Bytes value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 2 bytes for length + 'length' bytes, converted to bits)
            if (SpaceBits < (2 + value.Length) * 8)
                return false;

            // write LengthInBytes (ushort 2 bytes in FixedString),
            // and 'LengthInBytes' bytes
            if (WriteUShortBits((ushort)value.Length, 16))
            {
                for (int i = 0; i < value.Length; ++i)
                    if (!WriteByteBits(value[i], 8))
                        return false;
                return true;
            }
            return false;
        }

        public bool WriteFixedString64(FixedString64Bytes value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 2 bytes for length + 'length' bytes, converted to bits)
            if (SpaceBits < (2 + value.Length) * 8)
                return false;

            // write LengthInBytes (ushort 2 bytes in FixedString),
            // and 'LengthInBytes' bytes
            if (WriteUShortBits((ushort)value.Length, 16))
            {
                for (int i = 0; i < value.Length; ++i)
                    if (!WriteByteBits(value[i], 8))
                        return false;
                return true;
            }
            return false;
        }

        public bool WriteFixedString128(FixedString128Bytes value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 2 bytes for length + 'length' bytes, converted to bits)
            if (SpaceBits < (2 + value.Length) * 8)
                return false;

            // write LengthInBytes (ushort 2 bytes in FixedString),
            // and 'LengthInBytes' bytes
            if (WriteUShortBits((ushort)value.Length, 16))
            {
                for (int i = 0; i < value.Length; ++i)
                    if (!WriteByteBits(value[i], 8))
                        return false;
                return true;
            }
            return false;
        }

        public bool WriteFixedString512(FixedString512Bytes value)
        {
            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 2 bytes for length + 'length' bytes, converted to bits)
            if (SpaceBits < (2 + value.Length) * 8)
                return false;

            // write LengthInBytes (ushort 2 bytes in FixedString),
            // and 'LengthInBytes' bytes
            if (WriteUShortBits((ushort)value.Length, 16))
            {
                for (int i = 0; i < value.Length; ++i)
                    if (!WriteByteBits(value[i], 8))
                        return false;
                return true;
            }
            return false;
        }
    }
}
