// linear, block based delta compression for maximum performance.
// with run length encoded 'changed' bits.
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
    public static class DeltaCompressionRunLengthBlocks
    {
        // note that the last block might be <= blockSize
        // TODO faster
        static int CalculateNumberOfBlocks(int inputSize, int blockSize) =>
            (int)math.ceil((double)inputSize / (double)blockSize);

        // predict max patch data overhead for input size.
        //   4 bytes for encoded amount
        //   4 bytes for changed blocks start offset
        //   and worst case if blocks always alternate same/change/same/change/...
        //   then we write one 'VarInt' per block.
        //     worst case, varint size is 5 bytes for an int.
        public static int MaxOverhead(int inputSize, int blockSize) =>
            sizeof(uint) + sizeof(int) + 5 * blockSize;

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

        // delta compress 'previous' against 'current' based on block size.
        // writes patch into 'patch' writer.
        // RETURNS true if enough space, false otherwise.
        //         just like the rest of DOTSNET>
        // NOTE: respects content before 'patch.Position', for example DOTSNET
        //       has written message id to it already.
        public static unsafe bool Compress(NativeSlice<byte> previous, NativeSlice<byte> current, int blockSize, ref NetworkWriter patch)
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

            // run-length encode the 'changed' flags.
            // for example: 3Same, 1Change, 2Same, 5Change, ...
            // we know it's always going to be alternating between 'same/change'
            // so we don't need to encode that part. only the amount.
            // (assuming we always start from 'same' aka 'false')
            //
            //   bool last = false
            //   for each block:
            //     changed = memcpy(A, B)
            //     if (changed != last)
            //        run-length encode the change, i.e.
            //        '1 same, 3 changed, 2 same, 1 changed, ...'
            //

            // reserve 4 bytes for total run-length encodings amount
            // reserve 4 bytes for 'changed blocks' offset in writer
            patch.WriteUInt(0);
            patch.WriteUInt(0);

            // first run-length encoding is always for 'same'
            bool last = false;
            ulong accumulator = 0;
            uint encodedAmount = 0;
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

                // compare
                // TODO only memcpy once. we do it again below..
                bool changed = UnsafeUtility.MemCmp(previousBlock.GetUnsafePtr(), currentBlock.GetUnsafePtr(), previousBlock.Length) != 0;

                // direction changed?
                if (changed != last)
                {
                    // encode the 'run' so far
                    // varint to save space.
                    // TODO check result
                    VarInt.WriteVarUInt(ref patch, accumulator);
                    //Debug.Log($"ENCODE {(last ? "changed" : "same")} x {accumulator}");

                    // change direction
                    last = !last;

                    // reset accumulator. we already recorded '1' now.
                    accumulator = 1;

                    // we encoded one more now
                    ++encodedAmount;
                }
                // otherwise keep accumulating the 'changed' true/false flags
                else
                {
                    ++accumulator;
                    //Debug.Log($"RUN {(last ? "changed" : "same")} = {accumulator}");
                }
            }

            // above we only write once the direction changes.
            // but we still need to write the final direction too.
            // TODO check result
            if (!VarInt.WriteVarUInt(ref patch, accumulator))
                throw new IndexOutOfRangeException($"DeltaCompression.Compress: failed to write VarInt accumulator.");

            //Debug.Log($"ENCODE FINAL {(last ? "changed" : "same")} x {accumulator}");
            ++encodedAmount;


            // fill int total encodings size, changed blocks offset, jump back
            int backup = patch.Position;
            patch.Position = patchStart;

            if (!patch.WriteUInt(encodedAmount))
                throw new IndexOutOfRangeException($"DeltaCompression.Compress: failed to write encodedAmount.");

            int changedBlocksOffset = backup - patchStart;
            if (!patch.WriteInt(changedBlocksOffset))
                throw new IndexOutOfRangeException($"DeltaCompression.Compress: failed to write changedBlocksOffset.");

            //Debug.Log($"Encoded Amount: {encodedAmount}");
            //Debug.Log($"Changed Blocks Start Offset: {changedBlocksOffset}");
            patch.Position = backup;

            // now write all the changed blocks.
            // decompression can assume indices from our run-length encoding.
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

                // compare
                // TODO only memcpy once. we do it already above..
                bool changed = UnsafeUtility.MemCmp(previousBlock.GetUnsafePtr(), currentBlock.GetUnsafePtr(), previousBlock.Length) != 0;

                // write if changed
                if (changed)
                {
                    // we should guarantee writer is big enough for max patch size,
                    // so this should never fail.
                    if (!patch.WriteBytes(currentBlock))
                        throw new IndexOutOfRangeException($"CompressBlock: failed to write block of {currentBlock.Length} bytes. Not enough space in writer: Position={patch.Position} Space={patch.Space}. This should never happen.");
                }
            }

            //int patchLength = patch.Position - patchStart;
            //var writerSlice = patch.slice;
            //var patchSlice = new NativeSlice<byte>(writerSlice, patchStart, patchLength);
            //Debug.Log("Compress: wrote " + (patchLength) + $" bytes:\n  previous={previous.ToContentString()}\n  current={current.ToContentString()}\n  patch={patchSlice.ToContentString()}");
            return true;
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

            // need to remember patch start in reader
            int patchStart = patch.Position;

            // calculate number of blocks.
            // note that the last block might be <= blockSize
            int blocks = CalculateNumberOfBlocks(previous.Length, blockSize);
            //UnityEngine.Debug.Log("Decompress: LENGTH=" + previous.Length + " BLOCKSIZE=" + blockSize + " BLOCKS = " + blocks);

            // read amount of run-length encoding commands
            if (!patch.ReadUInt(out uint encodedAmount))
                throw new IndexOutOfRangeException($"DeltaCompression.Decompress: failed to read encoded amount. This should never happen.");
            //Debug.Log($"Reading encodedAmount = {encodedAmount}");

            // read offset where changed blocks start in memory
            if (!patch.ReadInt(out int changedBlocksOffset))
                throw new IndexOutOfRangeException($"DeltaCompression.Decompress: failed to read changed blocks offset. This should never happen.");
            //Debug.Log($"Reading changedBlocksOffset = {changedBlocksOffset}");

            // calculate where the first 'changed' block will be in 'patch'
            int changedPosition = patchStart + changedBlocksOffset;

            // go through encodings
            // first one is always 'same'
            bool last = false;
            int block = 0;
            for (int encoding = 0; encoding < encodedAmount; ++encoding)
            {
                // read amount of this type
                // varint for compression
                // TODO check result
                VarInt.ReadVarUInt(ref patch, out ulong amount);
                //Debug.Log($"Reading {amount} x {(last ? "changed" : "same")}");

                // amount x equal blocks to follow
                if (last == false)
                {
                    // copy 'amount' original blocks
                    for (ulong i = 0; i < amount; ++i)
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

                        // copy it
                        // TODO check result
                        current.WriteBytes(previousBlock);

                        //Debug.Log($"Copied Same {i}/{amount}");

                        // increment block index
                        ++block;
                    }
                }
                // amount x changed blocks to follow
                else
                {
                    // remember where we were
                    int backup = patch.Position;

                    // move to where blocks are stored in patch
                    patch.Position = changedPosition;
                    //Debug.Log($"Jumping from {backup} to {patch.Position}");

                    // copy 'amount' changed blocks
                    for (ulong i = 0; i < amount; ++i)
                    {
                        //Debug.Log($"Reading changed block @ {patch.Position}");

                        // calculate start, end of block.
                        // end needs min to keep last block within range if smaller.
                        int start = block * blockSize;
                        int end = math.min(start + blockSize, length);

                        // calculate actual block size (last one can be <= blockSize)
                        int size = end - start;
                        //UnityEngine.Debug.Log("Decompress: BLOCK: start=" + start + " end=" + end + " size=" + size);

                        // copy block from patch to current
                        if (patch.ReadBytes(size, out NativeSlice<byte> slice))
                        {
                            //UnityEngine.Debug.Log("Decompress: writing changed block: " + slice.ToContentString());
                            if (!current.WriteBytes(slice))
                                // TODO is this what we want?
                                return false;
                        }
                        // TODO is this what we want?
                        else return false;
                        //Debug.Log($"Copied CHANGED {i}/{amount} @ Position={patch.Position}");

                        // increment block index
                        ++block;
                    }

                    // continue at this 'changed' block position next time.
                    changedPosition = patch.Position;

                    // continue where we were
                    patch.Position = backup;
                    //Debug.Log($"Jumping back to {patch.Position}");
                }

                // switch direction. next encoding will be for !last
                last = !last;
            }

            // finally, set the reader position to the very end of the patch.
            // so that whoever reads next continues to read after the patch,
            // NOT after the run length encoding data.
            patch.Position = changedPosition;

            //Debug.Log("Decompress: read " + (patch.Position - patchStart) + $" bytes\n  previous={previous.ToContentString()}\n  patch={patch}\n  current={current.slice.ToContentString()}");
            return true;
        }
    }
}