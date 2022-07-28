// BitTree by vis2k/Mischa
//
// recursive bit flags to indicate changed areas in equal sized byte[]s.
// similarly to how we previously had dirty bits for Entity->Component->Field.
// but this can go even higher to groups of entities.
//
// this is similar to binary/octrees.
//
//          12345678ABCDEFGH
//        12345678 | ABCDEFGH
// 1|2|3|4|5|6|7|8   A|B|C|D|E|F|G|H
//
// each graph child is encoded with 1 bit 'changed'.
// each one can have their own children if size still > 4.
//
// the tree can be a binary(2)/quad(4)/oct(8) tree etc.
// => octree is ideal so that we need exactly 8 bit = 1 byte per encoding.
// => otherwise we would need bitpacking...
// => octree also reduces tree depth, compared to binary tree
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace DOTSNET
{
    public static class DeltaCompressionBitTree
    {
        // octree
        const int dimension = 8;

        // calculate max number of nodes for octree at level N
        // this is not for total tree nodes!
        public static int MaxNodesAtLevel(int level)
        {
            // formula works perfectly for all values, except '0'
            if (level == 0) return 0;
            return Utils.Pow(dimension, level);
        }

        // calculate max number of nodes in tree for octree with level N
        public static int MaxNodesInTree(int height)
        {
            // sum nodes for every level from 0..height
            // TODO formula
            int total = 0;
            for (int i = 0; i <= height; ++i)
                 total += MaxNodesAtLevel(i);
            return total;
        }

        // calculate tree height for octree based on original input size
        public static int TreeHeight(int inputSize)
        {
            // in Unity 2020.3 LTS, it fails for '0' too. in 2021 it works.
            if (inputSize == 0) return 0;
            // formula works perfectly for all values, except '1'..
            if (inputSize == 1) return 1;
            return Mathf.FloorToInt(Mathf.Log(inputSize-1, dimension) + 1);
        }

        // calculate max nodes in a tree generated for a given input
        public static int MaxNodesForGeneratedTree(int inputSize)
        {
            // calculate tree height for input.
            // then calculate max nodes for that tree height.
            return MaxNodesInTree(TreeHeight(inputSize));
        }

        // predict max patch data overhead for input size.
        public static int MaxOverhead(int inputSize)
        {
            // calculate amount of max nodes (=bits) in the generated tree
            int maxBits = MaxNodesForGeneratedTree(inputSize);
            return Utils.RoundBitsToFullBytes(maxBits);
        }

        // predict max patch size (input + overhead)
        public static int MaxPatchSize(int inputSize) =>
            inputSize + MaxOverhead(inputSize);

        // helper function to divide into N parts.
        // this is a little bit tricky, because we always want to have N parts.
        // each parts can have multiple entries.
        //
        // for example:
        //   [1,2,3,4,5,6,7] => [1],[2],[3],[4],[5],[6],[7],[]
        //   [1,2,3,4,5,6,7,8] => [1],[2],[3],[4],[5],[6],[7],[8]
        //   [1,2,3,4,5,6,7,8,9] => [1,2],[3],[4],[5],[6],[7],[8],[9]
        //   [1,2,3,4,5,6,7,8,9,10] => [1,2],[3,4],[5],[6],[7],[8],[9],[10]
        //
        // writes into byte* which needs to have a length of 'parts'
        // internal for testing as this is not obvious.
        internal static unsafe void Split<T>(NativeSlice<T> slice, int parts, NativeSlice<T>* result)
            where T : struct
        {
            // calculate bytes.Length / parts, but also get the remainder.
            // for example, 10 / 8 gives div=1, rem=2
            // meaning each part has length of '1', and '2' are remaining.
            int div = Math.DivRem(slice.Length, parts, out int rem);
            //Debug.Log($"div={div} rem={rem}");

            int sliceStart = 0;
            for (int i = 0; i < parts; ++i)
            {
                // calculate how many entries this part will have..
                // each part has 'div' entries.
                // and we need to split 'rem' remaining entries across parts.
                // -> ideally across the first parts
                // -> in fact, across the first 'rem' parts
                // so if i <= rem, we need to add one more.
                int entries = div;
                if (i < rem) ++entries;
                //Debug.Log($"part {i+1} has {entries} entries");

                // build a slice with that many entries
                // offset is only set if there's at least one entry..
                result[i] = new NativeSlice<T>(slice, entries > 0 ? sliceStart : 0, entries);
                sliceStart += entries;
            }
        }

        static unsafe bool CompressRecursively(NativeSlice<byte> previous, NativeSlice<byte> current, ref NetworkWriter patch)
        {
            //Debug.Log("--------");
            //Debug.Log($"previous={previous.ToContentString()}");

            // allocate 8 native slices on the stack to avoid GC
            NativeSlice<byte>* previousParts = stackalloc NativeSlice<byte>[dimension];
            NativeSlice<byte>* currentParts = stackalloc NativeSlice<byte>[dimension];
            bool* changed = stackalloc bool[dimension];

            // divide into 8 parts.
            Split(previous, dimension, previousParts);
            Split(current, dimension, currentParts);

            // encode 8 bit = 1 byte
            // changed flags are encoded into:
            //   0b10000000
            //   0b01000000
            //   0b00100000
            //   0b00010000
            //   0b00001000
            //   0b00000100
            //   0b00000010
            //   0b00000001
            byte encoding = 0b00000000;

            // compare contents.
            // TODO we could probably get 'equals' from the children later.
            // TODO currently we would compare unequal parts over and over...
            // although if we compare here then we don't need to go deeper if same.
            for (int i = 0; i < dimension; ++i)
            {
                changed[i] = UnsafeUtility.MemCmp(previousParts[i].GetUnsafeReadOnlyPtr(), currentParts[i].GetUnsafeReadOnlyPtr(), previousParts[i].Length) != 0;

                // build the encoding in the same for loop.
                // no need to do a separate loop.
                //
                // when debugging, we want the first changed bit to be on the left.
                // so let's shift from left to right
                byte flag = (byte)(changed[i] ? 0b10000000 : 0);
                byte nthBit = (byte)(flag >> i);
                encoding |= nthBit;
            }
            //Debug.Log($"encoding: {Convert.ToString(encoding, 2).PadLeft(8,'0')}");

            // print all parts for debugging
            // indicate changed/equal too.
            //string previousStr = "previous split: |";
            //string currentStr = "current split:  |";
            //for (int i = 0; i < dimension; ++i)
            //{
            //    previousStr += $"{previousParts[i].ToContentString()} | ";
            //    currentStr += $"{currentParts[i].ToContentString()}{(changed[i] ? "!" : " ")}| ";
            //}
            //Debug.Log(previousStr);
            //Debug.Log(currentStr);

            // write encoding
            if (!patch.WriteByte(encoding))
                throw new IndexOutOfRangeException($"DeltaCompressionBitTree: failed to write encoding. This should never happen.");

            // are we down to the lowest level, with size <= 8 and
            // A,B,C,D,E,F,G,H being 1 byte each?
            if (previous.Length <= dimension)
            {
                // debug log
                //string bottom = "bottom: |";
                //for (int i = 0; i < dimension; ++i)
                //    bottom += $" {previousParts[i].ToContentString()} {(changed[i] ? "!=" : "==")} {currentParts[i].ToContentString()} |";
                //Debug.Log(bottom);

                // write each changed byte.
                bool res = true;
                for (int i = 0; i < dimension; ++i)
                {
                    // make sure we only write within bounds
                    NativeSlice<byte> currentPart = currentParts[i];
                    if (currentPart.Length > 0 && changed[i])
                    {
                        res &= patch.WriteByte(currentPart[0]);
                    }
                }
                return res;
            }
            // continue recursively for all changed parts.
            // even if Length <= 8, we want to split them into A,B,C,D,E,F,G,H bytes.
            else
            {
                bool result = true;
                for (int i = 0; i < dimension; ++i)
                    if (changed[i])
                        result &= CompressRecursively(previousParts[i], currentParts[i], ref patch);
                return result;
            }
        }

        // delta compress 'previous' against 'current' based on block size.
        // writes patch into 'patch' writer.
        // RETURNS true if enough space, false otherwise.
        //         just like the rest of DOTSNET>
        // NOTE: respects content before 'patch.Position', for example DOTSNET
        //       has written message id to it already.
        public static bool Compress(NativeSlice<byte> previous, NativeSlice<byte> current, ref NetworkWriter patch)
        {
            // only same sized arrays are allowed.
            // exception to indicate that this needs to be fixed immediately.
            if (previous.Length != current.Length)
                throw new ArgumentException($"DeltaCompressionBitTree.Compress: only works on same sized data. Make sure that serialized data always has the same length. Previous={previous.Length} Current={current.Length}");

            // guarantee that patch writer has enough space for max sized patch.
            // exception to indicate that this needs to be fixed immediately.
            int maxPatchSize = MaxPatchSize(previous.Length);
            if (patch.Space < maxPatchSize)
                //throw new ArgumentException($"DeltaCompression.Compress: patch writer with Position={patch.Position} Space={patch.Space} is too small for max patch size of {maxPatchSize} bytes for input of {length} bytes");
                return false;

            // write nothing if completely empty.
            // otherwise we would write 8 bit as soon as we go into recursion.
            if (previous.Length == 0)
                return true;

            //Debug.Log("--------");
            //Debug.Log($"BitTree Compressing...");
            return CompressRecursively(previous, current, ref patch);
        }

        static unsafe bool DecompressRecursively(NativeSlice<byte> previous, ref NetworkReader patch, ref NetworkWriter current)
        {
            //Debug.Log("--------");
            //Debug.Log($"previous={previous.ToContentString()}");

            // allocate 8 native slices on the stack to avoid GC
            NativeSlice<byte>* previousParts = stackalloc NativeSlice<byte>[dimension];
            bool* changed = stackalloc bool[dimension];

            // divide into 8 parts.
            Split(previous, dimension, previousParts);

            // read encoding
            if (!patch.ReadByte(out byte encoding))
                throw new IndexOutOfRangeException($"DeltaCompressionBitTree: failed to read encoding. This should never happen.");
            //Debug.Log($"encoding: {Convert.ToString(encoding, 2).PadLeft(8,'0')}");

            // read flags, encoded as 8 bits
            for (int i = 0; i < dimension; ++i)
            {
                // when debugging, we want the first changed bit to be on the left.
                // so let's shift from left to right
                // so let's iterate backwards to make it look nicer.
                byte nthBit = (byte)(0b10000000 >> i);
                changed[i] = (encoding & nthBit) != 0;
            }

            // print all parts for debugging
            // indicate changed/equal too.
            //string previousStr = "previous split: |";
            //for (int i = 0; i < dimension; ++i)
            //    previousStr += $"{previousParts[i].ToContentString()} | ";
            //Debug.Log(previousStr);

            // are we down to the lowest level, with size <= 8 and
            // A,B,C,D,E,F,G,H being 1 byte each?
            if (previous.Length <= dimension)
            {
                // debug log
                //Debug.Log("bottom!");

                // read each byte from original or patch if changed
                bool res = true;
                for (int i = 0; i < dimension; ++i)
                {
                    // make sure we only read within bounds
                    NativeSlice<byte> previousPart = previousParts[i];
                    if (previousPart.Length > 0)
                    {
                        byte previousByte = previousPart[0];
                        if (changed[i]) res &= patch.ReadByte(out previousByte);
                        res &= current.WriteByte(previousByte);
                    }
                }
                return res;
            }
            // reconstruct each part
            else
            {
                bool result = true;
                for (int i = 0; i < dimension; ++i)
                {
                    // previous != current: continue recursively
                    if (changed[i])
                        result &= DecompressRecursively(previousParts[i], ref patch, ref current);
                    // previous == current: copy original
                    else if (!current.WriteBytes(previousParts[i]))
                        throw new IndexOutOfRangeException($"DeltaCompressionBitTree.Decompress: failed to write previous {i} chunk");
                }
                return result;
            }
        }

        // apply patch onto previous based on block size.
        // writes result into 'current' writer.
        // returns true if succeeded. fails if not enough space in patch/result.
        public static bool Decompress(NativeSlice<byte> previous, ref NetworkReader patch, ref NetworkWriter current)
        {
            //Debug.Log("--------");
            //Debug.Log($"BitTree Decompressing...");
            //UnityEngine.Debug.Log("Decopmress: reading " + patch.Position + " bits");

            int length = previous.Length;

            // result size will be same as input.
            // make sure writer has enough space.
            if (current.Space < length)
                throw new IndexOutOfRangeException($"DeltaCompression.Decompress: input with {length} bytes is too large for writer {current.Space} bytes");

            // read nothing if completely empty.
            // otherwise we would read 8 bit as soon as we go into recursion.
            if (previous.Length == 0)
                return true;

            // IMPORTANT: we DON'T need to read a size header.
            // the size is always == last.size!

            return DecompressRecursively(previous, ref patch, ref current);
        }
    }
}