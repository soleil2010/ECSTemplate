using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace DOTSNET
{
    public struct LocalWorldStateMessage : NetworkMessage
    {
        // max size of this message's serialization.
        public readonly int MaxSize;

        // to calculate how many entities can fit in one message, we need to
        // reserve space for overhead. at the moment, that's 3x size prefixes
        // from WriteBytesAndSize
        public const int Overhead = sizeof(int) * 3;

        // to calculate the maximum amount of entities per message, we first
        // need to know max content size without overhead:
        public int MaxContentSize => MaxSize - Overhead;

        // now there are three different ways an entity can be sent.
        // let's calculate max sizes for all three.

        // REMOVED entities are always sent as 8 byte netIds
        const int RemovedEntityMaxSize = sizeof(ulong);

        // ADDED entities are one Entity.MaxSize after another.
        static int AddedEntityMaxSize => EntityState.MaxSize;

        // KEPT entities are delta compressed together in one large chunk.
        //   compressing the large chunk won't be larger than compressing each
        //   one individually. it might only be smaller. so max size is max
        //   compressed size per entity:
        static int KeptEntityMaxSize =>
            DeltaCompressionBitTreeRaw.MaxPatchSize(EntityState.MaxSize);

        // WORST CASE entity can be calculated easily from those three:
        public static int WorstCaseEntityMaxSize =>
            Utils.max3(RemovedEntityMaxSize, AddedEntityMaxSize, KeptEntityMaxSize);

        // finally, we can calculate how many would fit into content.
        // '/' rounds to floor.
        public int MaxEntitiesAmount => MaxContentSize / WorstCaseEntityMaxSize;

        // native collection for all entities.
        // => should only be allocated once and then reused via RegisterMessage
        //    custom allocator
        // => native for burst support
        // => dictionary<netId, state> so we can Entities.ForEach all spawned
        //    and then grab their state from the dictionary (which is faster)
        public NativeParallelHashMap<ulong, EntityState> entities;

        // last sent entities for delta compression.
        // every connection has different last sent entities.
        // copy them in here before serializing.
        public NativeParallelHashMap<ulong, EntityState> lastEntities;

        // data structions to partition last/current entities
        NativeParallelHashMap<ulong, EntityState> added;
        NativeParallelHashMap<ulong, EntityState> kept;
        NativeParallelHashMap<ulong, EntityState> removed;

        // list for sorted netIds to assume them instead of sending them
        NativeList<ulong> sorted;

        // need a helper writer to serialize 'last' entity so we can pass a
        // NativeSlice to delta compression
        NativeArray<byte> lastSerializationArray;
        NativeArray<byte> currentSerializationArray;
        NativeArray<byte> lastDeserializationArray;
        NativeArray<byte> currentDeserializationArray;

        // writer to serialize on the fly when adding entities.
        // => easiest way to try fit another one into.
        // => we always know exactly max size and current size this way.
        // predicting sizes is just too cumbersome / too much magic.
        NativeArray<byte> addedSerializationArray;
        NativeArray<byte> keptSerializationArray;
        NativeArray<byte> removedSerializationArray;
        internal NetworkWriter addedWriter;
        internal NetworkWriter keptWriter;
        internal NetworkWriter removedWriter;

        // delta compression is capable of detecing byte-level changes.
        // if we scale float position to bytes,
        // then small movements will only change one byte.
        // this gives optimal bandwidth.
        // this gives optimal bandwidth
        //   benchmark with 0.01 precision: 130 KB/s => 60 KB/s
        //   benchmark with 0.1 precision: 130 KB/s => 30 KB/s
        const float PositionPrecision = 0.01f; // 1cm accuracy

        // initialization
        public LocalWorldStateMessage(int MaxSize, int initialCapacity, Allocator allocator)
        {
            this.MaxSize = MaxSize;

            // we prepend the serializations with 4 bytes 'size'
            int MaxSizeWithoutSizePrefix = MaxSize - sizeof(int);

            // make sure that maxsize - prefix is still >0.
            // so it needs to be at least 4 bytes.
            if (MaxSizeWithoutSizePrefix <= 0)
                throw new ArgumentException($"LocalWorldState passed MaxSize={MaxSize} needs to be > 4.");

            entities = new NativeParallelHashMap<ulong, EntityState>(initialCapacity, allocator);
            lastEntities = new NativeParallelHashMap<ulong, EntityState>(initialCapacity, allocator);
            added = new NativeParallelHashMap<ulong, EntityState>(initialCapacity, allocator);
            kept = new NativeParallelHashMap<ulong, EntityState>(initialCapacity, allocator);
            removed = new NativeParallelHashMap<ulong, EntityState>(initialCapacity, allocator);
            sorted = new NativeList<ulong>(initialCapacity, allocator);

            // our writer only needs to be as big as the largest EntityState.
            // sizeof() works perfectly here since EntityState.payload is fixed.
            lastSerializationArray = new NativeArray<byte>(MaxSize, allocator);
            currentSerializationArray = new NativeArray<byte>(MaxSize, allocator);
            lastDeserializationArray = new NativeArray<byte>(MaxSize, allocator);
            currentDeserializationArray = new NativeArray<byte>(MaxSize, allocator);

            // create serialization array if a max size was passed
            // client message handler doesn't need one for example.
            if (MaxSize > 0)
            {
                addedSerializationArray = new NativeArray<byte>(MaxSizeWithoutSizePrefix, allocator);
                keptSerializationArray = new NativeArray<byte>(MaxSizeWithoutSizePrefix, allocator);
                removedSerializationArray = new NativeArray<byte>(MaxSizeWithoutSizePrefix, allocator);
                addedWriter = new NetworkWriter(addedSerializationArray);
                keptWriter = new NetworkWriter(keptSerializationArray);
                removedWriter = new NetworkWriter(removedSerializationArray);
            }
            else
            {
                addedSerializationArray = default;
                keptSerializationArray = default;
                removedSerializationArray = default;
                addedWriter = default;
                keptWriter = default;
                removedWriter = default;
            }

            // show delta snapshot fitting info on startup.
            // makes it 100% obvious why there are N entities at max.
            Debug.Log($"Delta Snapshots: max message content size of {MaxContentSize} bytes will fit {MaxEntitiesAmount} Entities of (worst case) {WorstCaseEntityMaxSize} bytes.\n(Per-Entity worst case: despawn={RemovedEntityMaxSize} bytes, spawn={AddedEntityMaxSize} bytes, update={KeptEntityMaxSize} bytes)");
        }

        // dispose
        public void Dispose()
        {
            if (entities.IsCreated) entities.Dispose();
            if (lastEntities.IsCreated) lastEntities.Dispose();

            if (added.IsCreated) added.Dispose();
            if (kept.IsCreated) kept.Dispose();
            if (removed.IsCreated) removed.Dispose();

            if (sorted.IsCreated) sorted.Dispose();

            if (lastSerializationArray.IsCreated) lastSerializationArray.Dispose();
            if (currentSerializationArray.IsCreated) currentSerializationArray.Dispose();
            if (lastDeserializationArray.IsCreated) lastDeserializationArray.Dispose();
            if (currentDeserializationArray.IsCreated) currentDeserializationArray.Dispose();

            if (addedSerializationArray.IsCreated) addedSerializationArray.Dispose();
            if (keptSerializationArray.IsCreated) keptSerializationArray.Dispose();
            if (removedSerializationArray.IsCreated) removedSerializationArray.Dispose();
        }

        // try to add an entity if it still fits into serialization
        public bool TryAddEntity(EntityState entity)
        {
            if (entities.Count() < MaxEntitiesAmount)
            {
                entities.Add(entity.netId, entity);
                return true;
            }
            return false;
        }

        // for the given HashMap of entities, serialize selected keys
        static void SerializeSelected(NativeParallelHashMap<ulong, EntityState> map, NativeList<ulong> keys, ref NetworkWriter writer)
        {
            foreach (ulong netId in keys)
                if (!map[netId].Serialize(ref writer, PositionPrecision))
                    throw new IndexOutOfRangeException($"SerializeSelected: failed to serialize {keys.Length} entities into writer at Position={writer.Position} Space={writer.Space}. Was the array too small for max entity size? This should never happen.");
        }

        static bool SerializeRemoved(NativeParallelHashMap<ulong, EntityState> removed, ref NetworkWriter writer)
        {
            foreach (KeyValue<ulong, EntityState> kvp in removed)
                if (!writer.WriteULong(kvp.Key))
                    return false;
            return true;
        }

        bool SerializeKept(NativeParallelHashMap<ulong, EntityState> kept, ref NetworkWriter writer)
        {
            // delta compression needs to know which netId to delta against.
            // previously we prefixed the netId for every delta compressed entity.
            // instead, let's sort them and assume the same order.
            // this saves a lot of bandwidth.
            kept.SortKeys(ref sorted);

            // prepare writers
            NetworkWriter lastWriter = new NetworkWriter(lastSerializationArray);
            NetworkWriter currentWriter = new NetworkWriter(currentSerializationArray);

            // serialize all 'last' and 'current' entities that were in both (sorted)
            // TODO consider to store & reuse last serialization
            // TODO but then we can't easily swap in any 'lastEntities' anymore
            // TODO since lastWriter would need to be rebuild or passed too
            SerializeSelected(lastEntities, sorted, ref lastWriter);
            SerializeSelected(entities, sorted, ref currentWriter);

            // IMPORTANT: fixed size delta compression requires same size.
            if (lastWriter.Position != currentWriter.Position)
                throw new IndexOutOfRangeException($"SerializeEntityDelta: serialization size mismatch with last={lastWriter.Position} current={currentWriter.Position}. This should never happen. Make sure Serialize() always writes the same data. For example, always write all fields and full FixedList<T> etc.");

            // delta compress the two large writers.
            // we could also delta compress each entity separately.
            // but one pass should be more cache friendly & allows for more
            // optimizations on large data.
            return DeltaCompressionBitTreeRaw.Compress(lastWriter.slice, currentWriter.slice, ref writer);
        }

        static bool SerializeAdded(NativeParallelHashMap<ulong, EntityState> added, ref NetworkWriter writer)
        {
            foreach (KeyValue<ulong, EntityState> kvp in added)
                if (!kvp.Value.Serialize(ref writer, PositionPrecision))
                    return false;
            return true;
        }

        public bool Serialize(ref NetworkWriter writer)
        {
            // partition into added/kept/removed
            Utils.Partition(lastEntities, entities, ref added, ref kept, ref removed);

            // serialize 'removed' first.
            // it always makes sense to remove old stuff before adding new stuff.
            // this should ALWAYS fit, because any removed entity was previously
            // fully serialized with a size larger than just 'netId'.
            if (!SerializeRemoved(removed, ref removedWriter))
                throw new IndexOutOfRangeException($"Failed to serialize {removed.Count()} removed entities. This should never happen.");

            // serialize 'kept' with delta compression next.
            if (!SerializeKept(kept, ref keptWriter))
                throw new IndexOutOfRangeException($"Failed to serialize {kept.Count()} kept entities. This should never happen.");

            // serialize 'added' with full serialization last.
            if (!SerializeAdded(added, ref addedWriter))
                throw new IndexOutOfRangeException($"Failed to serialize {added.Count()} added entities. This should never happen.");

            //Debug.Log($"WorldState.Serialize full={fullWriter.Position} delta={deltaWriter.Position}");
            return writer.WriteBytesAndSize(addedWriter.slice) &&
                   writer.WriteBytesAndSize(keptWriter.slice) &&
                   writer.WriteBytesAndSize(removedWriter.slice);
        }

        void DeserializeRemoved(NativeSlice<byte> slice)
        {
            NetworkReader reader = new NetworkReader(slice);
            while (reader.ReadULong(out ulong netId))
            {
                // 'removed' are sent first so that we can delta serialize
                // against the exact same set of netIds without sending netIds.
                // in other words, remove it from 'lastEntities'
                //Debug.Log($"Deserialize removed from last: {netId}");
                lastEntities.Remove(netId);
            }
        }

        void DeserializeKept(NativeSlice<byte> slice)
        {
            // sort 'last' first.
            // then we can assume netId from order instead of sending it.
            lastEntities.SortKeys(ref sorted);

            // prepare writers
            NetworkWriter lastWriter = new NetworkWriter(lastDeserializationArray);
            NetworkWriter currentWriter = new NetworkWriter(currentDeserializationArray);

            // serialize 'last' entities to delta compress against
            // TODO consider to store & reuse last serialization
            // TODO but then we can't easily swap in any 'lastEntities' anymore
            // TODO since lastWriter would need to be rebuild or passed too
            SerializeSelected(lastEntities, sorted, ref lastWriter);

            // for all entities that existed in 'last' and 'current', sorted:
            // delta compress against them.
            NetworkReader reader = new NetworkReader(slice);
            if (DeltaCompressionBitTreeRaw.Decompress(lastWriter.slice, ref reader, ref currentWriter))
            {
                // delta compression needs to produce same size
                if (currentWriter.Position != lastWriter.Position)
                    throw new Exception($"Deserialize size mismatch.");

                // deserialize each entity from delta compressed data
                NetworkReader serialization = new NetworkReader(currentWriter.slice);
                foreach (ulong assumedNetId in sorted)
                {
                    // deserialize entity from the decompressed bytes
                    EntityState entity = default;
                    if (entity.Deserialize(ref serialization, PositionPrecision))
                    {
                        // double check if deserialized netid == assumed netId
                        if (entity.netId != assumedNetId)
                            throw new Exception($"Delta Decompression: expected assumedNetId={assumedNetId} but deserialized netId={entity.netId}");

                        // save it
                        entities[entity.netId] = entity;
                    }
                    else Debug.LogWarning($"Delta Deserialization failed for assumedNetId = {assumedNetId}");
                }
            }
            // this should never fail
            else Debug.LogWarning($"Delta Decompression failed. This should never happen.");
        }

        void DeserializeAdded(NativeSlice<byte> slice)
        {
            NetworkReader reader = new NetworkReader(slice);
            while (reader.Remaining > 0)
            {
                EntityState entity = default;
                if (entity.Deserialize(ref reader, PositionPrecision))
                {
                    //Debug.Log($"Deserialize added: {entity.netId}");
                    entities[entity.netId] = entity;
                }
                else break;
            }
        }

        public bool Deserialize(ref NetworkReader reader)
        {
            if (!entities.IsCreated)
                throw new NullReferenceException("LocalWorldState entities have never been initialized!");

            if (!lastEntities.IsCreated)
                throw new NullReferenceException("LocalWorldState lastEntities have not been assigned to this conneciton's lastEntities!");

            // clear old results (if any)
            entities.Clear();

            // read added
            if (!reader.ReadBytesAndSize(out NativeSlice<byte> addedSlice))
                return false;

            // read kept delta compressed
            if (!reader.ReadBytesAndSize(out NativeSlice<byte> keptSlice))
                return false;

            // read removed
            if (!reader.ReadBytesAndSize(out NativeSlice<byte> removedSlice))
                return false;

            // parse removed first so delta decompress can operate on same data.
            // then parse kept (delta decompress).
            // then parse added.
            //Debug.Log($"WorldState.Deserialize full={fullSlice.Length} delta={deltaSlice.Length}");
            DeserializeRemoved(removedSlice);
            DeserializeKept(keptSlice);
            DeserializeAdded(addedSlice);
            //Debug.Log("----------------");

            return true;
        }

        // reset should reset all state for reuse
        public void Reset()
        {
            // remember last entities. don't clear them.
            entities.Clear();
            added.Clear();
            kept.Clear();
            removed.Clear();
            sorted.Clear();
            addedWriter.Position = 0;
            keptWriter.Position = 0;
            removedWriter.Position = 0;
        }
    }
}
