using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace DOTSNET
{
    public static class Extensions
    {
        // string.GetHashCode is not guaranteed to be the same on all machines,
        // but we need one that is the same on all machines.
        // -> originally from uMMORPG
        // -> 'int' because all C# GetHashCode functions are 'int'
        public static int GetStableHashCode(this string text)
        {
            unchecked
            {
                int hash = 23;
                foreach (char c in text)
                    hash = hash * 31 + c;
                return hash;
            }
        }

        // serialization needs a stable hash for each NetworkComponent.
        // we have ComponentType, which has a TypeIndex.
        // TypeManager provides StableHash.
        // -> GetType().ToString().GetStableHashCode() works too, but it
        //    allocates and ECS already has a stable hash anyway!
        // (this is from Archetype.cs GetStableHash())
        public static ulong GetStableHashCode(this ComponentType type) =>
            TypeManager.GetTypeInfo(type.TypeIndex).StableTypeHash;

        // helper function to get a unique id for Entities.
        // it combines 4 bytes Index + 4 bytes Version into 8 bytes unique Id
        //
        // IMPORTANT: we need both index AND version!
        // IMPORTANT: version needs to be 4 bytes too, not just 1 byte!
        //            killing a monster 10 times already gives version=10.
        //            for long running servers, we really do need 4 bytes here!
        public static ulong UniqueId(this Entity entity)
        {
            // convert to uint
            uint index = (uint)entity.Index;
            uint version = (uint)entity.Version;

            // shift version from 0x000000FFFFFFFF to 0xFFFFFFFF00000000
            ulong shiftedVersion = (ulong)version << 32;

            // OR into result
            return (index & 0xFFFFFFFF) | shiftedVersion;
        }

        // DynamicBuffer helper function to check if it contains an element
        public static bool Contains<T>(this DynamicBuffer<T> buffer, T value)
            where T : struct
        {
            // DynamicBuffer foreach allocates. use for.
            for (int i = 0; i < buffer.Length; ++i)
                // .Equals can't be called from a Job.
                // GetHashCode() works as long as <T> implements it manually!
                // (which is faster too!)
                if (buffer[i].GetHashCode() == value.GetHashCode())
                    return true;
            return false;
        }

        // NativeMultiMap has .GetValuesForKeyArray Enumerator, but using C#'s
        // 'foreach (...)' in Burst/Jobs causes an Invalid IL code exception:
        // https://forum.unity.com/threads/invalidprogramexception-invalid-il-code-because-of-ecs-generated-code-in-a-foreach-query.914387/
        // So we need our own iterator.
        //
        // Using .TryGetFirstValue + .TryGetNextValue works, but it's way too
        // cumbersome.
        //
        // This causes redundant code like 'Send()' in this example:
        //    if (messages.TryGetFirstValue(connectionId, out message,
        //        out NativeMultiHashMapIterator<int> it))
        //    {
        //        Send(message, connectionId);
        //        while (messages.TryGetNextValue(out message, ref it))
        //        {
        //            Send(message, connectionId);
        //        }
        //    }
        //
        // Making it really difficult to do more abstractions/optimizations.
        //
        // So let's create a helper function so it's easier to use:
        //    NativeMultiHashMapIterator<T>? iterator = default;
        //    while (messages.TryIterate(connectionId, out message, ref iterator))
        //    {
        //        Send(message, connectionId);
        //    }
        public static bool TryIterate<TKey, TValue>(
            this NativeParallelMultiHashMap<TKey, TValue> map,
            TKey key,
            out TValue value,
            ref NativeParallelMultiHashMapIterator<TKey>? it)
                where TKey : struct, IEquatable<TKey>
                where TValue : struct
        {
            // get first value if iterator not initialized yet & assign iterator
            if (!it.HasValue)
            {
                bool result = map.TryGetFirstValue(key, out value, out NativeParallelMultiHashMapIterator<TKey> temp);
                it = temp;
                return result;
            }
            // otherwise get next value & assign iterator
            else
            {
                NativeParallelMultiHashMapIterator<TKey> temp = it.Value;
                bool result = map.TryGetNextValue(out value, ref temp);
                it = temp;
                return result;
            }
        }

        // copy all NativeMultiMap values for a key without allocating a new list
        public static void CopyValuesForKey<TKey, TValue>(this NativeParallelMultiHashMap<TKey, TValue> map, TKey key, NativeList<TValue> result)
            where TKey : struct, IEquatable<TKey>
            where TValue : unmanaged
        {
            result.Clear();
            NativeParallelMultiHashMapIterator<TKey>? iterator = default;
            while (map.TryIterate(key, out TValue entityState, ref iterator))
            {
                result.Add(entityState);
            }
        }

        // NativeMultiMap only has ContainsKey.
        // but since it can have multiple values per key, this is useful.
        public static bool ContainsKeyAndValue<TKey, TValue>(
            this NativeParallelMultiHashMap<TKey, TValue> map,
            TKey key,
            TValue value)
            where TKey : struct, IEquatable<TKey>
            where TValue : struct
        {
            // reuse iterator to search values for that key
            NativeParallelMultiHashMapIterator<TKey>? iterator = default;
            while (map.TryIterate(key, out TValue entry, ref iterator))
            {
                // .Equals can't be called from a Job.
                // GetHashCode() works as long as <T> implements it manually!
                // (which is faster too!)
                if (entry.GetHashCode() == value.GetHashCode())
                    return true;
            }
            return false;
        }

        // copy all NativeHashMap values to another NativeHashMap
        public static void CopyTo<TKey, TValue>(this NativeParallelHashMap<TKey, TValue> map, NativeParallelHashMap<TKey, TValue> destination)
            where TKey : struct, IEquatable<TKey>
            where TValue : struct
        {
            destination.Clear();
            foreach (KeyValue<TKey, TValue> kvp in map)
                destination[kvp.Key] = kvp.Value;
        }

        // sort NativeHashMap keys allocation free into a provided list
        public static void SortKeys<TKey, TValue>(this NativeParallelHashMap<TKey, TValue> map, ref NativeList<TKey> sorted)
            where TKey : unmanaged, IEquatable<TKey>, IComparable<TKey>
            where TValue : struct
        {
            sorted.Clear();
            foreach (KeyValue<TKey, TValue> kvp in map)
                sorted.Add(kvp.Key);
            sorted.Sort();
        }

        // NativeSlice<byte> ToString() with content like BitConverter.ToString.
        public static string ToContentString(this NativeSlice<byte> slice)
        {
            string content = "";
            for (int i = 0; i < slice.Length; ++i)
            {
                content += $"{slice[i]:X2}";
                if (i < slice.Length - 1) content += "-";
            }
            return content;
        }
    }
}