// DOTS doesn't have long3

using System.Runtime.CompilerServices;

namespace DOTSNET
{
    public struct long3
    {
        public long x;
        public long y;
        public long z;

        public static readonly long3 zero;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long3(long x, long y, long z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }
}