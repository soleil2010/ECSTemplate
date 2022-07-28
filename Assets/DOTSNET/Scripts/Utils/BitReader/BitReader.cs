// default BitReader that operates on a NativeSlice<byte> passed in constructor.
// => burstable!
// => there's no need for an ArraySegment<byte> reader.
//    we always want NativeSlice<byte> for burst support.
// => see IBitReader interface for documentation!
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSNET
{
    [Obsolete("Please use NetworkReader instead. BitReader won't be necessary with the upcoming delta compression. NetworkReader is faster too.")]
    public struct BitReader : IBitReader
    {
        // scratch bits are twice the size of maximum allowed write size.
        // maximum is 32 bit, so we need 64 bit scratch for cases where
        // scratch is filled by 10 bits, and we writer another 32 into it.
        ulong scratch;
        int scratchBits;
        int wordIndex;

        // NativeSlice similar to ArraySegment.
        // useful to pass parts of a received NativeArray to a message reader.
        // TODO reinterpret as <uint> and get rid of scratch helper variables!
        NativeSlice<byte> buffer;

        // calculate remaining data to read, in bits
        // how many bits can we read:
        // -> scratchBits + what's left in buffer * 8 (= to bits)
        public int RemainingBits =>
            scratchBits + ((buffer.Length - wordIndex) * 8);

        // position is useful sometimes. read-only for now.
        // => wordIndex converted to bits - scratch remaining
        public int BitPosition => wordIndex * 8 - scratchBits;

        public BitReader(NativeSlice<byte> buffer)
        {
            this.buffer = buffer;
            scratch = 0;
            scratchBits = 0;
            wordIndex = 0;
        }

        // helpers /////////////////////////////////////////////////////////////
        // helper function to copy 32 bits from buffer to scratch.
        unsafe void Copy32BufferBitsToScratch()
        {
            // bitpacking works on aligned writes, but we want to support buffer
            // sizes that aren't multiple of 4 as well. it's more convenient.
            // => we want to copy 4 buffer bytes to scratch, or fewer if not
            //    enough to read.
            int remaining = buffer.Length - wordIndex;
            int copy = Math.Min(4, remaining);

            // since we don't always copy exactly 4 bytes, we can't use the
            // uint* pointer assignment trick unless we want to have a special
            // case for '4 aligned' and 'not 4 aligned'. the most simple
            // solution is to use MemCopy with the variable amount of bytes.
            // => it's usually 4
            // => except for the last read which is 1,2,3 if buffer sizes is not
            //    a multiple of 4.
            uint word;

            void* ptr = (byte*)buffer.GetUnsafePtr() + wordIndex;

            // memcpy the amount of bytes from buffer.
            // in the future we will use an uint[] buffer directly.
            byte* wordPtr = (byte*)&word;
#if UNITY_2017_1_OR_NEWER
            UnsafeUtility.MemCpy(wordPtr, ptr, copy);
#else
            Buffer.MemoryCopy(ptr, wordPtr, copy);
#endif

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
            //UnityEngine.Debug.Log("word: 0x" + word.ToString("X8"));

            // move the extracted part into scratch at scratch position
            // so for scratch 0x000000FF it becomes 0xDDDDDDFF
            ulong shifted = (ulong)word << scratchBits;
            scratch |= shifted;
            //UnityEngine.Debug.Log("shifted: 0x" + shifted.ToString("X16"));
            //UnityEngine.Debug.Log("scratch: 0x" + scratch.ToString("X16"));

            // update word index and scratch bits
            // usually +4 bytes and +32 bits.
            // unless we only had < 4 bytes left to copy in the buffer
            wordIndex += copy;
            scratchBits += copy * 8; // to bits
        }

        // ReadBITS ////////////////////////////////////////////////////////////
        public bool ReadUIntBits(out uint value, int bits)
        {
            value = 0;

            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // => throws exception because the developer should fix it immediately
            //
            // NOTE: we allow 0 bits so that ReadULongBits is easier to
            //       implement where we simply do two ReadUInts and the second
            //       one might pass '0' for bits but still succeeds.
            if (bits < 0 || bits > 32)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadUIntBits)} supports between 0 and 32 bits");

            // 0 bits is valid, simply do nothing
            if (bits == 0)
                return true;

            // make sure there are enough remaining in buffer
            if (RemainingBits < bits)
                return false;

            // copy another word from buffer if we don't have enough scratch bits
            if (scratchBits < bits)
            {
                Copy32BufferBitsToScratch();
            }

            // create the mask
            // for example, for 8 bits it is 0x000000FF
            // need ulong so we can shift left 32 for 32 bits
            // (would be too much for uint)
            ulong mask = (1ul << bits) - 1;
            //UnityEngine.Debug.Log("mask: 0x" + mask.ToString("X"));

            // extract the 'n' bits out of scratch
            // need ulong so we can shift left far enough in the step below!
            // so for 0xAABBCCDD if we extract 8 bits we extract the last 0xDD
            value = (uint)(scratch & mask);
            //UnityEngine.Debug.Log("extracted: 0x" + value.ToString("X"));

            // shift scratch to the right by 'n' bits
            scratch >>= bits;

            // subtract 'n' from scratchBits
            scratchBits -= bits;

            // done
            return true;
        }

        public bool PeekUIntBits(out uint value, int bits)
        {
            // reuse ReadUIntBits so we only have the complex code in 1 function
            int wordIndexBackup = wordIndex;
            int scratchBitsBackup = scratchBits;
            ulong scratchBackup = scratch;

            bool result = ReadUIntBits(out value, bits);

            wordIndex = wordIndexBackup;
            scratchBits = scratchBitsBackup;
            scratch = scratchBackup;

            return result;
        }

        public bool ReadULongBits(out ulong value, int bits)
        {
            value = 0;

            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // => throws exception because the developer should fix it immediately
            //
            // NOTE: we allow 0 bits just like all other functions
            if (bits < 0 || bits > 64)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadULongBits)} supports between 0 and 64 bits");

            // 0 bits is valid, simply do nothing
            if (bits == 0)
                return true;

            // make sure there is enough remaining in buffer
            // => we do two ReadUInts below. checking size first makes it atomic!
            if (RemainingBits < bits)
                return false;

            // read both halves as uint
            // => first one up to 32 bits
            // => second one the remainder. ReadUInt does nothing if bits is 0.
            int lowerBits = Math.Min(bits, 32);
            int upperBits = Math.Max(bits - 32, 0);
            if (ReadUIntBits(out uint lower, lowerBits) &&
                ReadUIntBits(out uint upper, upperBits))
            {
                value = (ulong)upper << 32;
                value |= lower;
                return true;
            }
            return false;
        }

        public bool ReadUShortBits(out ushort value, int bits)
        {
            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // => throws exception because the developer should fix it immediately
            if (bits < 0 || bits > 16)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadUShortBits)} supports between 0 and 16 bits");

            bool result = ReadUIntBits(out uint temp, bits);
            value = (ushort)temp;
            return result;
        }

        public bool ReadByteBits(out byte value, int bits)
        {
            // make sure user passed valid amount of bits.
            // anything else was by accident, so throw an exception.
            // => throws exception because the developer should fix it immediately
            if (bits < 0 || bits > 8)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadByteBits)} supports between 0 and 8 bits");

            bool result = ReadUIntBits(out uint temp, bits);
            value = (byte)temp;
            return result;
        }

        public bool ReadBool(out bool value)
        {
            bool result = ReadUIntBits(out uint temp, 1);
            value = temp != 0;
            return result;
        }

        // Read RANGE //////////////////////////////////////////////////////////
        public bool ReadUInt(out uint value, uint min = uint.MinValue, uint max = uint.MaxValue)
        {
            value = 0;

            // make sure the range is valid
            // => throws exception because the developer should fix it immediately
            if (min > max)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadUInt)} min={min} needs to be <= max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // read the normalized value for that range
            // for example
            //   originally written value was '5' for range [2..9]
            //   normalized range when was max-min => [0..7]
            //   normalized value was value-min = '3'
            //   we read the normalized value '3'
            //   and add min back to it => '5'
            if (ReadUIntBits(out uint normalized, bits))
            {
                value = normalized + min;
                return true;
            }
            return false;
        }

        public bool ReadInt(out int value, int min = int.MinValue, int max = int.MaxValue)
        {
            value = 0;

            // make sure the range is valid
            // => throws exception because the developer should fix it immediately
            if (min > max)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadInt)} min={min} needs to be <= max={max}");

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

            // read the normalized value for that range. need to add min.
            if (ReadUIntBits(out uint normalized, bits))
            {
                value = (int)(normalized + min);
                return true;
            }
            return false;
        }

        public bool ReadULong(out ulong value, ulong min = ulong.MinValue, ulong max = ulong.MaxValue)
        {
            value = 0;

            // make sure the range is valid
            // => throws exception because the developer should fix it immediately
            if (min > max)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadULong)} min={min} needs to be <= max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // read the normalized value for that range
            // for example
            //   originally written value was '5' for range [2..9]
            //   normalized range when was max-min => [0..7]
            //   normalized value was value-min = '3'
            //   we read the normalized value '3'
            //   and add min back to it => '5'
            if (ReadULongBits(out ulong normalized, bits))
            {
                value = normalized + min;
                return true;
            }
            return false;
        }

        public bool ReadLong(out long value, long min = long.MinValue, long max = long.MaxValue)
        {
            value = 0;

            // make sure the range is valid
            // => throws exception because the developer should fix it immediately
            if (min > max)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadLong)} min={min} needs to be <= max={max}");

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

            // read the normalized value for that range. need to add min.
            if (ReadULongBits(out ulong normalized, bits))
            {
                value = (long)normalized + min;
                return true;
            }
            return false;
        }

        public bool ReadUShort(out ushort value, ushort min = ushort.MinValue, ushort max = ushort.MaxValue)
        {
            value = 0;

            // make sure the range is valid
            // => throws exception because the developer should fix it immediately
            if (min > max)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadUShort)} min={min} needs to be <= max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // read the normalized value for that range
            // for example
            //   originally written value was '5' for range [2..9]
            //   normalized range when was max-min => [0..7]
            //   normalized value was value-min = '3'
            //   we read the normalized value '3'
            //   and add min back to it => '5'
            if (ReadUShortBits(out ushort normalized, bits))
            {
                value = (ushort)(normalized + min);
                return true;
            }
            return false;
        }

        public bool ReadShort(out short value, short min = short.MinValue, short max = short.MaxValue)
        {
            value = 0;

            // make sure the range is valid
            // => throws exception because the developer should fix it immediately
            if (min > max)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadShort)} min={min} needs to be <= max={max}");

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

            // read the normalized value for that range. need to add min.
            if (ReadUShortBits(out ushort normalized, bits))
            {
                value = (short)(normalized + min);
                return true;
            }
            return false;
        }

        public bool ReadByte(out byte value, byte min = byte.MinValue, byte max = byte.MaxValue)
        {
            value = 0;

            // make sure the range is valid
            // => throws exception because the developer should fix it immediately
            if (min > max)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadByte)} min={min} needs to be <= max={max}");

            // calculate bits required for value range
            int bits = Utils.BitsRequired(min, max);

            // read the normalized value for that range
            // for example
            //   originally written value was '5' for range [2..9]
            //   normalized range when was max-min => [0..7]
            //   normalized value was value-min = '3'
            //   we read the normalized value '3'
            //   and add min back to it => '5'
            if (ReadByteBits(out byte normalized, bits))
            {
                value = (byte)(normalized + min);
                return true;
            }
            return false;
        }

        // Read Uncompressed ///////////////////////////////////////////////////
        public bool ReadBytes(byte[] bytes, int size)
        {
            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (size < 0 || size > bytes.Length)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadBytes)} size {size} needs to be between 0 and {bytes.Length}");

            // make sure there is enough remaining in scratch + buffer
            if (RemainingBits < size * 8)
                return false;

            // size = 0 is valid, simply do nothing
            if (size == 0)
                return true;

            // simply reuse ReadByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (copying scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            for (int i = 0; i < size; ++i)
            {
                if (!ReadByteBits(out bytes[i], 8))
                    return false;
            }
            return true;
        }

        public unsafe bool ReadBytes(byte* bytes, int bytesLength, int size)
        {
            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (size < 0 || size > bytesLength)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadBytes)} size {size} needs to be between 0 and {bytesLength}");

            // make sure there is enough remaining in scratch + buffer
            if (RemainingBits < size * 8)
                return false;

            // size = 0 is valid, simply do nothing
            if (size == 0)
                return true;

            // simply reuse ReadByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (copying scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            for (int i = 0; i < size; ++i)
            {
                if (!ReadByteBits(out bytes[i], 8))
                    return false;
            }
            return true;
        }

        public bool ReadBytesBitSize(byte[] bytes, int sizeInBits)
        {
            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (sizeInBits < 0 || sizeInBits > bytes.Length * 8)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadBytesBitSize)} sizeInBits {sizeInBits} needs to be between 0 and {bytes.Length} * 8");

            // make sure there is enough remaining in scratch + buffer
            if (RemainingBits < sizeInBits)
                return false;

            // size = 0 is valid, simply do nothing
            if (sizeInBits == 0)
                return true;

            // simply reuse ReadByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (copying scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            //
            // size is in bits, so / 8 to get amount of FULL BYTES we can read
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
            {
                if (!ReadByteBits(out bytes[i], 8))
                    return false;
            }

            // now read the final partial byte (missing bits) if any
            int missingBits = sizeInBits - (fullBytes * 8);
            if (missingBits > 0)
            {
                //UnityEngine.Debug.Log("reading " + missingBits + " bits"));
                if (!ReadByteBits(out bytes[fullBytes], missingBits))
                    return false;
            }

            return true;
        }

        public unsafe bool ReadBytesBitSize(byte* bytes, int bytesLength, int sizeInBits)
        {
            // make sure size is valid
            // => throws exception because the developer should fix it immediately
            if (sizeInBits < 0 || sizeInBits > bytesLength * 8)
                throw new ArgumentOutOfRangeException($"BitReader {nameof(ReadBytesBitSize)} sizeInBits {sizeInBits} needs to be between 0 and {bytesLength} * 8");

            // make sure there is enough remaining in scratch + buffer
            if (RemainingBits < sizeInBits)
                return false;

            // size = 0 is valid, simply do nothing
            if (sizeInBits == 0)
                return true;

            // simply reuse ReadByte for now.
            // => copy in up to 32 bit chunks later for performance!
            // (copying scratch then memcpy bytes would insert placeholders if
            //  scratch isn't a multiple of 8 bits)
            //
            // size is in bits, so / 8 to get amount of FULL BYTES we can read
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
            {
                if (!ReadByteBits(out bytes[i], 8))
                    return false;
            }

            // now read the final partial byte (missing bits) if any
            int missingBits = sizeInBits - (fullBytes * 8);
            if (missingBits > 0)
            {
                //UnityEngine.Debug.Log("reading " + missingBits + " bits"));
                if (!ReadByteBits(out bytes[fullBytes], missingBits))
                    return false;
            }

            return true;
        }

        public bool ReadFloat(out float value)
        {
            bool result = ReadUIntBits(out uint temp, 32);
            value = new BitWriter.FloatUInt{uintValue = temp}.floatValue;
            return result;
        }

        public bool ReadFloat2(out float2 value)
        {
            value = float2.zero;
            return ReadFloat(out value.x) &&
                   ReadFloat(out value.y);
        }

        public bool ReadFloat3(out float3 value)
        {
            value = float3.zero;
            return ReadFloat(out value.x) &&
                   ReadFloat(out value.y) &&
                   ReadFloat(out value.z);
        }

        public bool ReadFloat(out float value, float min, float max, float precision)
        {
            value = 0;

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
            float minScaled = min / precision;
            float maxScaled = max / precision;

            // check bounds before converting to int
            if (minScaled < int.MinValue || minScaled > int.MaxValue ||
                maxScaled < int.MinValue || maxScaled > int.MaxValue)
                return false;

            // Convert.ToInt32 so we don't need to depend on Unity.Mathf!
            int minRounded = Convert.ToInt32(minScaled);
            int maxRounded = Convert.ToInt32(maxScaled);

            // read the scaled value
            if (ReadInt(out int temp, minRounded, maxRounded))
            {
                // scale back
                value = temp * precision;
                return true;
            }
            return false;
        }

        public bool ReadFloat2(out float2 value, float min, float max, float precision)
        {
            value = float2.zero;
            return ReadFloat(out value.x, min, max, precision) &&
                   ReadFloat(out value.y, min, max, precision);
        }

        public bool ReadFloat3(out float3 value, float min, float max, float precision)
        {
            value = float3.zero;
            return ReadFloat(out value.x, min, max, precision) &&
                   ReadFloat(out value.y, min, max, precision) &&
                   ReadFloat(out value.z, min, max, precision);
        }

        public bool ReadDouble(out double value, double min, double max, double precision)
        {
            value = 0;

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
            double minScaled = min / precision;
            double maxScaled = max / precision;

            // check bounds before converting to long
            if (minScaled < long.MinValue || minScaled > long.MaxValue ||
                maxScaled < long.MinValue || maxScaled > long.MaxValue)
                return false;

            // Convert.ToInt64 so we don't need to depend on Unity.Mathf!
            long minRounded = Convert.ToInt64(minScaled);
            long maxRounded = Convert.ToInt64(maxScaled);

            // read the scaled value
            if (ReadLong(out long temp, minRounded, maxRounded))
            {
                // scale back
                value = temp * precision;
                return true;
            }
            return false;
        }

        public bool ReadDouble(out double value)
        {
            bool result = ReadULongBits(out ulong temp, 64);
            value = new BitWriter.DoubleULong{ulongValue = temp}.doubleValue;
            return result;
        }

        // ECS types ///////////////////////////////////////////////////////////
        public bool ReadQuaternion(out quaternion value)
        {
            value = default;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 16 bytes, converted to bits)
            if (RemainingBits < 16 * 8)
                return false;

            // read 4 floats
            return ReadFloat(out value.value.x) &&
                   ReadFloat(out value.value.y) &&
                   ReadFloat(out value.value.z) &&
                   ReadFloat(out value.value.w);
        }

        public bool ReadQuaternionSmallestThree(out quaternion value)
        {
            value = default;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (our compression uses 32 bit. maybe use 29 bit later.)
            if (RemainingBits < 32)
                return false;

            // read and decompress
            if (ReadUIntBits(out uint compressed, 32))
            {
                value = Compression.DecompressQuaternion(compressed);
                return true;
            }
            return false;
        }

        public bool ReadBytes16(out FixedBytes16 value)
        {
            value = default;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 16 bytes, converted to bits)
            if (RemainingBits < 16 * 8)
                return false;

            // calling ReadByteBits 16x is a bit heavy.
            // => 1mio x ReadBytes16 with 16x ReadByteBits = 400 ms.
            // => 1mio x ReadBytes16 with  2x ReadUlong    = 300 ms
            if (ReadULongBits(out ulong value_0to7, 64) &&
                ReadULongBits(out ulong value_8to15, 64))
            {
                value.byte0000 = (byte)(value_0to7 >> 0);
                value.byte0001 = (byte)(value_0to7 >> 8);
                value.byte0002 = (byte)(value_0to7 >> 16);
                value.byte0003 = (byte)(value_0to7 >> 24);
                value.byte0004 = (byte)(value_0to7 >> 32);
                value.byte0005 = (byte)(value_0to7 >> 40);
                value.byte0006 = (byte)(value_0to7 >> 48);
                value.byte0007 = (byte)(value_0to7 >> 56);

                value.byte0008 = (byte)(value_8to15 >> 0);
                value.byte0009 = (byte)(value_8to15 >> 8);
                value.byte0010 = (byte)(value_8to15 >> 16);
                value.byte0011 = (byte)(value_8to15 >> 24);
                value.byte0012 = (byte)(value_8to15 >> 32);
                value.byte0013 = (byte)(value_8to15 >> 40);
                value.byte0014 = (byte)(value_8to15 >> 48);
                value.byte0015 = (byte)(value_8to15 >> 56);

                return true;
            }
            return false;
        }

        public bool ReadBytes30(out FixedBytes30 value)
        {
            value = default;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 30 bytes, converted to bits)
            if (RemainingBits < 30 * 8)
                return false;

            // read the 30 bytes
            // note: could read in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return ReadBytes16(out value.offset0000) &&
                   ReadByteBits(out value.byte0016, 8) &&
                   ReadByteBits(out value.byte0017, 8) &&
                   ReadByteBits(out value.byte0018, 8) &&
                   ReadByteBits(out value.byte0019, 8) &&
                   ReadByteBits(out value.byte0020, 8) &&
                   ReadByteBits(out value.byte0021, 8) &&
                   ReadByteBits(out value.byte0022, 8) &&
                   ReadByteBits(out value.byte0023, 8) &&
                   ReadByteBits(out value.byte0024, 8) &&
                   ReadByteBits(out value.byte0025, 8) &&
                   ReadByteBits(out value.byte0026, 8) &&
                   ReadByteBits(out value.byte0027, 8) &&
                   ReadByteBits(out value.byte0028, 8) &&
                   ReadByteBits(out value.byte0029, 8);
        }

        public bool ReadBytes62(out FixedBytes62 value)
        {
            value = default;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 62 bytes, converted to bits)
            if (RemainingBits < 62 * 8)
                return false;

            // read the 62 bytes
            // note: could read in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return ReadBytes16(out value.offset0000) &&
                   ReadBytes16(out value.offset0016) &&
                   ReadBytes16(out value.offset0032) &&
                   ReadByteBits(out value.byte0048, 8) &&
                   ReadByteBits(out value.byte0049, 8) &&
                   ReadByteBits(out value.byte0050, 8) &&
                   ReadByteBits(out value.byte0051, 8) &&
                   ReadByteBits(out value.byte0052, 8) &&
                   ReadByteBits(out value.byte0053, 8) &&
                   ReadByteBits(out value.byte0054, 8) &&
                   ReadByteBits(out value.byte0055, 8) &&
                   ReadByteBits(out value.byte0056, 8) &&
                   ReadByteBits(out value.byte0057, 8) &&
                   ReadByteBits(out value.byte0058, 8) &&
                   ReadByteBits(out value.byte0059, 8) &&
                   ReadByteBits(out value.byte0060, 8) &&
                   ReadByteBits(out value.byte0061, 8);
        }

        public bool ReadBytes126(out FixedBytes126 value)
        {
            value = default;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 126 bytes, converted to bits)
            if (RemainingBits < 126 * 8)
                return false;

            // read the 126 bytes
            // note: could read in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return ReadBytes16(out value.offset0000) &&
                   ReadBytes16(out value.offset0016) &&
                   ReadBytes16(out value.offset0032) &&
                   ReadBytes16(out value.offset0048) &&
                   ReadBytes16(out value.offset0064) &&
                   ReadBytes16(out value.offset0080) &&
                   ReadBytes16(out value.offset0096) &&
                   ReadByteBits(out value.byte0112, 8) &&
                   ReadByteBits(out value.byte0113, 8) &&
                   ReadByteBits(out value.byte0114, 8) &&
                   ReadByteBits(out value.byte0115, 8) &&
                   ReadByteBits(out value.byte0116, 8) &&
                   ReadByteBits(out value.byte0117, 8) &&
                   ReadByteBits(out value.byte0118, 8) &&
                   ReadByteBits(out value.byte0119, 8) &&
                   ReadByteBits(out value.byte0120, 8) &&
                   ReadByteBits(out value.byte0121, 8) &&
                   ReadByteBits(out value.byte0122, 8) &&
                   ReadByteBits(out value.byte0123, 8) &&
                   ReadByteBits(out value.byte0124, 8) &&
                   ReadByteBits(out value.byte0125, 8);
        }

        public bool ReadBytes510(out FixedBytes510 value)
        {
            value = default;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // (we need 510 bytes, converted to bits)
            if (RemainingBits < 510 * 8)
                return false;

            // read the 510 bytes
            // note: could read in 32 bit uint chunks if we shift+or 4 at a
            //       time into uint
            return ReadBytes16(out value.offset0000) &&
                   ReadBytes16(out value.offset0016) &&
                   ReadBytes16(out value.offset0032) &&
                   ReadBytes16(out value.offset0048) &&
                   ReadBytes16(out value.offset0064) &&
                   ReadBytes16(out value.offset0080) &&
                   ReadBytes16(out value.offset0096) &&
                   ReadBytes16(out value.offset0112) &&
                   ReadBytes16(out value.offset0128) &&
                   ReadBytes16(out value.offset0144) &&
                   ReadBytes16(out value.offset0160) &&
                   ReadBytes16(out value.offset0176) &&
                   ReadBytes16(out value.offset0192) &&
                   ReadBytes16(out value.offset0208) &&
                   ReadBytes16(out value.offset0224) &&
                   ReadBytes16(out value.offset0240) &&
                   ReadBytes16(out value.offset0256) &&
                   ReadBytes16(out value.offset0272) &&
                   ReadBytes16(out value.offset0288) &&
                   ReadBytes16(out value.offset0304) &&
                   ReadBytes16(out value.offset0320) &&
                   ReadBytes16(out value.offset0336) &&
                   ReadBytes16(out value.offset0352) &&
                   ReadBytes16(out value.offset0368) &&
                   ReadBytes16(out value.offset0384) &&
                   ReadBytes16(out value.offset0400) &&
                   ReadBytes16(out value.offset0416) &&
                   ReadBytes16(out value.offset0432) &&
                   ReadBytes16(out value.offset0448) &&
                   ReadBytes16(out value.offset0464) &&
                   ReadBytes16(out value.offset0480) &&
                   ReadByteBits(out value.byte0496, 8) &&
                   ReadByteBits(out value.byte0497, 8) &&
                   ReadByteBits(out value.byte0498, 8) &&
                   ReadByteBits(out value.byte0499, 8) &&
                   ReadByteBits(out value.byte0500, 8) &&
                   ReadByteBits(out value.byte0501, 8) &&
                   ReadByteBits(out value.byte0502, 8) &&
                   ReadByteBits(out value.byte0503, 8) &&
                   ReadByteBits(out value.byte0504, 8) &&
                   ReadByteBits(out value.byte0505, 8) &&
                   ReadByteBits(out value.byte0506, 8) &&
                   ReadByteBits(out value.byte0507, 8) &&
                   ReadByteBits(out value.byte0508, 8) &&
                   ReadByteBits(out value.byte0509, 8);
        }

        public bool ReadFixedString32(out FixedString32Bytes value)
        {
            value = default;

            // peek 2 bytes length first.
            // we need it to be atomic, so we peek, then check total size before
            // reading anything
            if (!PeekUIntBits(out uint length, 16))
                return false;

            // length should be in a valid range.
            // FixedString32 has 2 bytes for length, 29 bytes for content.
            // we read an unsigned, so need to check <0!
            if (length > FixedString32Bytes.UTF8MaxLengthInBytes)
                return false;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // we need 2 bytes for length + length bytes for content, in bits
            if (RemainingBits < (2 + length) * 8)
                return false;

            // skip the 2 bytes content that we already peeked
            ReadUIntBits(out uint _, 16);

            // read the content bytes
            value = new FixedString32Bytes();
            value.Length = (ushort)length;
            for (int i = 0; i < length; ++i)
            {
                if (ReadByteBits(out byte element, 8))
                {
                    value[i] = element;
                }
                else return false;
            }
            return true;
        }

        public bool ReadFixedString64(out FixedString64Bytes value)
        {
            value = default;

            // peek 2 bytes length first.
            // we need it to be atomic, so we peek, then check total size before
            // reading anything
            if (!PeekUIntBits(out uint length, 16))
                return false;

            // length should be in a valid range.
            // FixedString64 has 2 bytes for length, 61 bytes for content.
            // we read an unsigned, so need to check <0!
            if (length > FixedString64Bytes.UTF8MaxLengthInBytes)
                return false;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // we need 2 bytes for length + length bytes for content, in bits
            if (RemainingBits < (2 + length) * 8)
                return false;

            // skip the 2 bytes content that we already peeked
            ReadUIntBits(out uint _, 16);

            // read the content bytes
            value = new FixedString64Bytes();
            value.Length = (ushort)length;
            for (int i = 0; i < length; ++i)
            {
                if (ReadByteBits(out byte element, 8))
                {
                    value[i] = element;
                }
                else return false;
            }
            return true;
        }

        public bool ReadFixedString128(out FixedString128Bytes value)
        {
            value = default;

            // peek 2 bytes length first.
            // we need it to be atomic, so we peek, then check total size before
            // reading anything
            if (!PeekUIntBits(out uint length, 16))
                return false;

            // length should be in a valid range.
            // FixedString128 has 2 bytes for length, 125 bytes for content.
            // we read an unsigned, so need to check <0!
            if (length > FixedString128Bytes.UTF8MaxLengthInBytes)
                return false;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // we need 2 bytes for length + length bytes for content, in bits
            if (RemainingBits < (2 + length) * 8)
                return false;

            // skip the 2 bytes content that we already peeked
            ReadUIntBits(out uint _, 16);

            // read the content bytes
            value = new FixedString128Bytes();
            value.Length = (ushort)length;
            for (int i = 0; i < length; ++i)
            {
                if (ReadByteBits(out byte element, 8))
                {
                    value[i] = element;
                }
                else return false;
            }
            return true;
        }

        public bool ReadFixedString512(out FixedString512Bytes value)
        {
            value = default;

            // peek 2 bytes length first.
            // we need it to be atomic, so we peek, then check total size before
            // reading anything
            if (!PeekUIntBits(out uint length, 16))
                return false;

            // length should be in a valid range.
            // FixedString512 has 2 bytes for length, 509 bytes for content.
            // we read an unsigned, so need to check <0!
            if (length > FixedString512Bytes.UTF8MaxLengthInBytes)
                return false;

            // make sure there is enough space in scratch + buffer.
            // the write should be atomic.
            // we need 2 bytes for length + length bytes for content, in bits
            if (RemainingBits < (2 + length) * 8)
                return false;

            // skip the 2 bytes content that we already peeked
            ReadUIntBits(out uint _, 16);

            // read the content bytes
            value = new FixedString512Bytes();
            value.Length = (ushort)length;
            for (int i = 0; i < length; ++i)
            {
                if (ReadByteBits(out byte element, 8))
                {
                    value[i] = element;
                }
                else return false;
            }
            return true;
        }
    }
}
