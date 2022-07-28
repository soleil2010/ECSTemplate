// helper class to copy Unity.Collections.Bytes30 (etc.) into byte[]
// -> ArraySegments can use those functions too!
using System;
using Unity.Collections;

namespace DOTSNET
{
    public static class FlatByteArrays
    {
        // copy Bytes16 struct to byte[]
        [Obsolete("FlatByteArrrays.Bytes16ToArray was moved to Utils.Bytes16ToArray")]
        public static bool Bytes16ToArray(FixedBytes16 value, byte[] array, int arrayOffset) =>
            Utils.Bytes16ToArray(value, array, arrayOffset);

        // copy Bytes30 struct to byte[]
        [Obsolete("FlatByteArrrays.Bytes30ToArray was moved to Utils.Bytes30ToArray")]
        public static bool Bytes30ToArray(FixedBytes30 value, byte[] array, int arrayOffset) =>
            Utils.Bytes30ToArray(value, array, arrayOffset);

        // copy Bytes62 struct to byte[]
        [Obsolete("FlatByteArrrays.Bytes62ToArray was moved to Utils.Bytes62ToArray")]
        public static bool Bytes62ToArray(FixedBytes62 value, byte[] array, int arrayOffset) =>
            Utils.Bytes62ToArray(value, array, arrayOffset);

        // copy Bytes126 struct to byte[]
        [Obsolete("FlatByteArrrays.Bytes126ToArray was moved to Utils.Bytes126ToArray")]
        public static bool Bytes126ToArray(FixedBytes126 value, byte[] array, int arrayOffset) =>
            Utils.Bytes126ToArray(value, array, arrayOffset);

        // copy Bytes510 struct to byte[]
        [Obsolete("FlatByteArrrays.Bytes510ToArray was moved to Utils.Bytes510ToArray")]
        public static bool Bytes510ToArray(FixedBytes510 value, byte[] array, int arrayOffset) =>
            Utils.Bytes510ToArray(value, array, arrayOffset);

        // create Bytes16 struct from byte[]
        [Obsolete("FlatByteArrrays.ArrayToBytes16 was moved to Utils.ArrayToBytes16")]
        public static bool ArrayToBytes16(byte[] array, int arrayOffset, out FixedBytes16 value) =>
            Utils.ArrayToBytes16(array, arrayOffset, out value);

        // create Bytes30 struct from byte[]
        [Obsolete("FlatByteArrrays.ArrayToBytes30 was moved to Utils.ArrayToBytes30")]
        public static bool ArrayToBytes30(byte[] array, int arrayOffset, out FixedBytes30 value) =>
            Utils.ArrayToBytes30(array, arrayOffset, out value);

        // create Bytes62 struct from byte[]
        [Obsolete("FlatByteArrrays.ArrayToBytes62 was moved to Utils.ArrayToBytes62")]
        public static bool ArrayToBytes62(byte[] array, int arrayOffset, out FixedBytes62 value) =>
            Utils.ArrayToBytes62(array, arrayOffset, out value);

        // create Bytes126 struct from byte[]
        [Obsolete("FlatByteArrrays.ArrayToBytes126 was moved to Utils.ArrayToBytes126")]
        public static bool ArrayToBytes126(byte[] array, int arrayOffset, out FixedBytes126 value) =>
            Utils.ArrayToBytes126(array, arrayOffset, out value);

        // create Bytes510 struct from byte[]
        [Obsolete("FlatByteArrrays.ArrayToBytes510 was moved to Utils.ArrayToBytes510")]
        public static bool ArrayToBytes510(byte[] array, int arrayOffset, out FixedBytes510 value) =>
            Utils.ArrayToBytes510(array, arrayOffset, out value);

        // compare two Bytes16 structs without .Equals() boxing allocations.
        [Obsolete("FlatByteArrrays.CompareBytes16 was moved to Utils.CompareBytes16")]
        public static bool CompareBytes16(FixedBytes16 a, FixedBytes16 b) =>
            Utils.CompareBytes16(a, b);
    }
}