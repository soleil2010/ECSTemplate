// one entity's statein LocalWorldState
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace DOTSNET
{
    public unsafe struct EntityState
    {
        // client needs to know which prefab to spawn
        public FixedBytes16 prefabId;

        // client needs to know which netId was assigned to this entity
        public ulong netId;

        // flag to indicate if the connection that we send it to owns the entity
        public bool owned;

        // the spawn position
        // unlike StateMessage, we include the position once when spawning so
        // that even without a NetworkTransform system, it's still positioned
        // correctly when spawning.
        public float3 position;

        // the spawn rotation
        // unlike StateMessage, we include the rotation once when spawning so
        // that even without a NetworkTransform system, it's still rotated
        // correctly when spawning.
        public quaternion rotation;

        // payload contains the serialized component data.
        // we also store the exact bit size.
        // -> 'fixed byte[]' is inlined. better than allocating a 'new byte[]'!
        // -> also avoids allocation attacks since the size is always fixed!
        public int payloadSize;
        // 128 bytes per entity should be way enough
        // => has to be the size of the NetworkComponentsSerialization NetworkWriter!
        public const int PayloadFixedSize = 128;
        public fixed byte payload[PayloadFixedSize];

        public EntityState(FixedBytes16 prefabId, ulong netId, bool owned, float3 position, quaternion rotation, NetworkWriter128 serialization)
        {
            this.prefabId = prefabId;
            this.netId = netId;
            this.owned = owned;
            this.position = position;
            this.rotation = rotation;

            // were any NetworkComponents serialized?
            payloadSize = serialization.Position;
            if (serialization.Position > 0)
            {
                // copy writer into our payload
                fixed (byte* buffer = payload)
                {
                    if (serialization.CopyTo(buffer, PayloadFixedSize) == 0)
                        Debug.LogError($"Failed to copy writer at Position={serialization.Position} to EntityState payload");
                }
            }
        }

        // calculate largest size an EntityState serialization can have
        public static int MaxSize =>
            // 16 bytes prefabid
            sizeof(FixedBytes16) +
            // netId 8 bytes
            sizeof(ulong) +
            // owned bool
            1 +
            // long3 position
            sizeof(long3) +
            // quaternion
            sizeof(quaternion) +
            // int payload size
            sizeof(int) +
            // fixed payload size
            PayloadFixedSize;

        // divide by precision.
        // for example, 0.1 gives 10x floats as ints.
        // IMPORTANT: ToInt32 throws if the float / precision gets > int.max!
        //            https://github.com/vis2k/DOTSNET/issues/59
        //            need long instead.
        static long ScaleToLong(float value, float precision) =>
            Convert.ToInt64(value / precision);

        static long3 ScaleToLong3(float3 value, float precision) =>
            new long3(ScaleToLong(value.x, precision),
                      ScaleToLong(value.y, precision),
                      ScaleToLong(value.z, precision));

        // multiple by precision.
        // for example, 0.1 gives 10x floats as ints.
        static float ScaleToFloat(long value, float precision) =>
            value * precision;

        static float3 ScaleToFloat3(long3 value, float precision) =>
            new float3(ScaleToFloat(value.x, precision),
                       ScaleToFloat(value.y, precision),
                       ScaleToFloat(value.z, precision));

        public bool Serialize(ref NetworkWriter writer, float positionPrecision)
        {
            // delta compression is capable of detecing byte-level changes.
            // if we scale float position to bytes,
            // then small movements will only change one byte.
            // this gives optimal bandwidth
            //   benchmark with 0.01 precision: 130 KB/s => 60 KB/s
            //   benchmark with 0.1 precision: 130 KB/s => 30 KB/s
            long3 positionScaled = ScaleToLong3(position, positionPrecision);

            fixed (byte* buffer = payload)
            {
                // rotation is compressed from 16 bytes quaternion into 4 bytes
                //   100,000 messages * 16 byte = 1562 KB
                //   100,000 messages *  4 byte =  391 KB
                // => DOTSNET is bandwidth limited, so this is a great idea.
                return writer.WriteBytes16(prefabId) &&
                       writer.WriteULong(netId) &&
                       writer.WriteBool(owned) &&
                       writer.WriteLong3(positionScaled) &&
                       // quaternion smallet-three caused some issues (#57)
                       writer.WriteQuaternion(rotation) &&
                       // write payload
                       writer.WriteInt(payloadSize) &&
                       writer.WriteBytes(buffer, PayloadFixedSize, 0, payloadSize);
            }
        }

        public bool Deserialize(ref NetworkReader reader, float positionPrecision)
        {
            if (reader.ReadBytes16(out prefabId) &&
                reader.ReadULong(out netId) &&
                reader.ReadBool(out owned) &&
                reader.ReadLong3(out long3 positionScaled) &&
                reader.ReadQuaternion(out rotation) &&
                // read payload size
                reader.ReadInt(out payloadSize) &&
                // verify size
                payloadSize <= PayloadFixedSize * 8)
            {
                // delta compression is capable of detecting byte-level changes.
                // if we scale float position to bytes,
                // then small movements will only change one byte.
                // this gives optimal bandwidth.
                // this gives optimal bandwidth
                //   benchmark with 0.01 precision: 130 KB/s => 60 KB/s
                //   benchmark with 0.1 precision: 130 KB/s => 30 KB/s
                position = ScaleToFloat3(positionScaled, positionPrecision);

                fixed (byte* buffer = payload)
                {
                    return reader.ReadBytes(buffer, PayloadFixedSize, payloadSize);
                }
            }
            return false;
        }

        // Equals for easier testing.
        // Serialized/Deserialized position/rotation are not exactly the same,
        // so we need to overwrite with proper comparisons.
        public override bool Equals(object obj)
        {
            if (obj == null) return false;

            if (obj is EntityState other)
            {
                fixed (byte* payloadPtr = payload)
                {
                    return // netId, prefabId, owned
                           netId == other.netId &&
                           Utils.CompareBytes16(prefabId, other.prefabId) &&
                           owned == other.owned &&
                           // position & rotation via angle with some tolerance
                           math.distance(position, other.position) < math.EPSILON &&
                           Utils.QuaternionAngle(rotation, other.rotation) < math.EPSILON &&
                           // compare payload with exact size
                           payloadSize == other.payloadSize &&
                           UnsafeUtility.MemCmp(payloadPtr, other.payload, payloadSize) == 0;
                }

            }
            return false;
        }

        // if we overwrite Equals, we also need to overwrite GetHashCode()
        public override int GetHashCode() => throw new NotImplementedException();

        // ToString for easier debugging
        public override string ToString() =>
            $"EntityState(netId={netId} prefabId={Conversion.Bytes16ToGuid(prefabId)} owned={owned} position={position} rotation={rotation} payloadSize={payloadSize}))";
    }
}