// linear, block based delta compression for maximum performance.
//
// for each block:
//   compare with previous serialization.
//   write a changed bit
//   if changed, write the full block too
//
// NetworkWriter is byte-level, not bit-level.
//   we can't write a 'changed bit' before each block, it would be 8 bit byte.
//   instead, we pack all changed bits into changedBytes in the beginning.
//
// as result, compressed data has the following layout:
//   <<changedByte0, changedByte1, ..., block0, block1, ...>
//     => changedByte0 has 8 bits for block 0..7
//        changedByte1 has 8 bits for block 8..15 etc.
//     => block0 is the data for the first block, assuming changed
//
// => assumes fixed size serializations.
//    ECS components are fixed size anyway.
// => super fast O(N) for large scale game servers
// => decently small: 1 bit per unchanged 'blocksize'
// => burstable!
//
// block size can be 1 byte, 4 byte, 16 byte etc. - whatever works for the game.
// small block size of around 4 is recommended.
// usually, ints/floats of 4 bytes are changed.
//
// the algorithm supports byte-level and bit-level data.
// even if a changed 4 byte float value is not byte aligned, it will still be
// part of a changed block just like if it was byte aligned.
// it doesn't matter as long as the data is fixed size.
//
// PROFILING: use DeltaCompressionTestSystem + Unity Profiler!
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace DOTSNET
{
    public static class DeltaCompressionBitBlocks
    {
        // note that the last block might be <= blockSize
        // TODO faster
        static int CalculateNumberOfBlocks(int inputSize, int blockSize) =>
            (int)math.ceil((double)inputSize / (double)blockSize);

        // predict max patch data overhead for input size.
        // we write 1 extra 'changed' bit per block.
        // but rounded to bytes (7 blocks are still rounded to 1 byte).
        public static int MaxOverhead(int inputSize, int blockSize) =>
            Utils.RoundBitsToFullBytes(CalculateNumberOfBlocks(inputSize, blockSize));

        // predict max patch size (input + overhead)
        public static int MaxPatchSize(int inputSize, int blockSize) =>
            inputSize + MaxOverhead(inputSize, blockSize);

        // calculate position of 'changed bit' for this block index
        // bytePosition: 0..length
        // bitPosition: 0..7
        /*
        public static void CalculateReservedPosition(int blockIndex, out int bytePosition, out int bitPosition)
        {
            // first 8 blocks are in the first byte.
            // next 8 blocks are in the second byte.
            // etc.

            // byte position is block index / 8
            // block 0..7 are in byte 0
            // block 8..15 are in byte 1
            bytePosition = blockIndex / 8;

            // bit position is the remainder
            // take bit position, subtract all bits from all previous blocks.
            bitPosition = blockIndex - (bytePosition * 8);
        }
        */

        // assumes same length for both
        static unsafe void CompressBlock(NativeSlice<byte> previousBlock, NativeSlice<byte> currentBlock, ref NetworkWriter patch, out bool changed)
        {
            // check if changed
            int size = previousBlock.Length;
            changed = UnsafeUtility.MemCmp(previousBlock.GetUnsafePtr(), currentBlock.GetUnsafePtr(), size) != 0;

            // write changed content if changed
            if (changed)
            {
                // we should guarantee writer is big enough for max patch size,
                // so this should never fail.
                if (!patch.WriteBytes(currentBlock))
                    throw new IndexOutOfRangeException($"CompressBlock: failed to write block of {currentBlock.Length} bytes. Not enough space in writer: Position={patch.Position} Space={patch.Space}. This should never happen.");
            }
        }

        // delta compress 'previous' against 'current' based on block size.
        // writes patch into 'patch' writer.
        // RETURNS true if enough space, false otherwise.
        //         just like the rest of DOTSNET>
        // NOTE: respects content before 'patch.Position', for example DOTSNET
        //       has written message id to it already.
        public static bool Compress(NativeSlice<byte> previous, NativeSlice<byte> current, int blockSize, ref NetworkWriter patch)
        {
            // only same sized arrays are allowed.
            // exception to indicate that this needs to be fixed immediately.
            if (previous.Length != current.Length)
                throw new ArgumentException($"DeltaCompression.Compress: only works on same sized data. Make sure that serialized data always has the same length. Previous={previous.Length} Current={current.Length}");

            int length = previous.Length;

            // remember writer start position
            int patchStart = patch.Position;

            // guarantee that patch writer has enough space for max sized patch.
            // exception to indicate that this needs to be fixed immediately.
            int maxPatchSize = MaxPatchSize(length, blockSize);
            if (patch.Space < maxPatchSize)
                //throw new ArgumentException($"DeltaCompression.Compress: patch writer with Position={patch.Position} Space={patch.Space} is too small for max patch size of {maxPatchSize} bytes for input of {length} bytes");
                return false;

            // calculate number of blocks.
            // note that the last block might be <= blockSize
            int blocks = CalculateNumberOfBlocks(previous.Length, blockSize);
            //UnityEngine.Debug.Log("Compress: LENGTH=" + previous.Length + " BLOCKSIZE=" + blockSize + " BLOCKS = " + blocks);

            // IMPORTANT: we DON'T need to write a size header.
            // the size is always == last.size!

            // reserve several bytes for the 'changed bits' in the beginning.
            // we don't use a BitWriter, so we have to reserve bytes and do
            // bitflag calculations manually.
            // (we don't want to use a BitWriter either).
            // => 1 bit per block
            // => so 1 byte can store information for 8 bits
            // => simply round block bits to full bytes
            int changedByteCount = Utils.RoundBitsToFullBytes(blocks);
            for (int i = 0; i < changedByteCount; ++i)
                patch.WriteByte(0);

            // iterate blocks
            byte changedByte = 0;
            int changedBytePosition = patchStart;
            int changedBitPosition = 0;
            for (int block = 0; block < blocks; ++block)
            {
                // calculate start, end of block.
                // end needs min to keep last block within range if smaller.
                int start = block * blockSize;
                int end = math.min(start + blockSize, length);

                // calculate actual block size (last one can be <= blockSize)
                int size = end - start;
                //UnityEngine.Debug.Log("Compress: BLOCK: start=" + start + " end=" + end + " size=" + size);

                // build a slice around that block
                NativeSlice<byte> previousBlock = new NativeSlice<byte>(previous, start, size);
                NativeSlice<byte> currentBlock = new NativeSlice<byte>(current, start, size);

                // compress this block
                CompressBlock(previousBlock, currentBlock, ref patch, out bool changed);

                // add the 'changed' bit into the changed byte at 'changedBitPosition'
                byte changedBit = changed ? (byte)1 : (byte)0;
                //UnityEngine.Debug.Log($"block={block} changed={changedBit} @ changedBitPosition={changedBitPosition}");
                changedByte |= (byte)(changedBit << changedBitPosition);
                ++changedBitPosition;

                // changed byte full, or end reached?
                if (changedBitPosition == 8 || block == blocks - 1)
                {
                    // write the changed byte
                    int backup = patch.Position;
                    patch.Position = changedBytePosition;
                    //UnityEngine.Debug.Log($"Writing changed byte bits={Convert.ToString(changedByte, 2)} @ {patch.Position}");
                    patch.WriteByte(changedByte);
                    patch.Position = backup;

                    // move to next
                    changedByte = 0;
                    ++changedBytePosition;
                    changedBitPosition = 0;
                }
            }
            //UnityEngine.Debug.Log("Compress: wrote " + (patch.Position - patchStart) + " bits");
            return true;
        }

        // assumes same length for both
        // buffer is passed in here so we only have to allocate it once, not per
        // block. we need it for copying / reading.
        static bool DecompressBlock(NativeSlice<byte> previousBlock, ref NetworkReader patch, int blockSize, bool changed, ref NetworkWriter current)
        {
            if (changed)
            {
                //Debug.Log($"Reading changed block...");

                // read as many bytes as the block we decompress against had.
                // for example, with blocksize=4 we might read:
                //   4, 4, 4, 3  if the total size is 15.
                //   we do NOT want to read 4, 4, 4, 4 every time,
                //   even if 'patch' contains more data.
                //   as that data might be for the next message.
                // => SEE TEST: Decompress_StopsAtOriginalSize()
                //UnityEngine.Debug.Log($"Decompress: blockSize={previousBlock.Length} bytes remaining={patch.Remaining} bits");

                // read the block, write to result
                if (patch.ReadBytes(previousBlock.Length, out NativeSlice<byte> slice))
                {
                    //UnityEngine.Debug.Log("Decompress: writing changed block: " + slice.ToContentString());
                    return current.WriteBytes(slice);
                }
                //Debug.Log("DecompressBlock failed because can't read " + read + " bytes");
                return false;
            }
            // copy original content
            else
            {
                //UnityEngine.Debug.Log("Decompress: writing original block: " + previousBlock.ToContentString());
                return current.WriteBytes(previousBlock);
            }
        }

        // apply patch onto previous based on block size.
        // writes result into 'current' writer.
        // returns true if succeeded. fails if not enough space in patch/result.
        public static bool Decompress(NativeSlice<byte> previous, ref NetworkReader patch, int blockSize, ref NetworkWriter current)
        {
            //UnityEngine.Debug.Log("Decopmress: reading " + patch.Position + " bits");

            int length = previous.Length;

            // result size will be same as input.
            // make sure writer has enough space.
            if (current.Space < length)
                throw new IndexOutOfRangeException($"DeltaCompression.Decompress: input with {length} bytes is too large for writer {current.Space} bytes");

            // IMPORTANT: we DON'T need to read a size header.
            // the size is always == last.size!

            // calculate number of blocks.
            // note that the last block might be <= blockSize
            int blocks = CalculateNumberOfBlocks(previous.Length, blockSize);
            //UnityEngine.Debug.Log("Decompress: LENGTH=" + previous.Length + " BLOCKSIZE=" + blockSize + " BLOCKS = " + blocks);

            // read several bytes for the 'changed bits' in the beginning.
            int changedByteCount = Utils.RoundBitsToFullBytes(blocks);
            if (!patch.ReadBytes(changedByteCount, out NativeSlice<byte> changedBytes))
                // missing data
                return false;

            //UnityEngine.Debug.Log($"Reserved Bytes={changed.ToContentString()}");

            // iterate blocks
            int changedBytePosition = 0;
            int changedBitPosition = 0;
            for (int block = 0; block < blocks; ++block)
            {
                // calculate start, end of block.
                // end needs min to keep last block within range if smaller.
                int start = block * blockSize;
                int end = math.min(start + blockSize, length);

                // calculate actual block size (last one can be <= blockSize)
                int size = end - start;
                //UnityEngine.Debug.Log("Decompress: BLOCK: start=" + start + " end=" + end + " size=" + size);

                // build a slice around that block
                NativeSlice<byte> previousBlock = new NativeSlice<byte>(previous, start, size);

                // read nth bit for changed bit flag
                byte changedByte = changedBytes[changedBytePosition];
                int nthBit = changedByte & (1 << changedBitPosition);
                bool changed = nthBit != 0;
                //UnityEngine.Debug.Log($"Read block={block} changedByte={Convert.ToString(changedByte, 2)} nthBit={Convert.ToString(nthBit, 2)} changed={changed}");

                // decompress this block
                if (!DecompressBlock(previousBlock, ref patch, blockSize, changed, ref current))
                    return false;

                // reached the last bit in this changed byte?
                ++changedBitPosition;
                if (changedBitPosition > 7)
                {
                    // move to next
                    ++changedBytePosition;
                    changedBitPosition = 0;
                }
            }

            return true;
        }
    }
}