// snapshot of one entity's state.
// interpolation / time will come later.
using Unity.Mathematics;
using UnityEngine;

namespace DOTSNET
{
    public unsafe struct SnapshotMessage : NetworkMessage
    {
        // netId of the NetworkIdentity this message is meant for
        public ulong netId;

        // position & rotation of the entity
        // => included depending on NetworkIdentity.transformSync direction
        // => easier than a separate NetworkTransform component because we would
        //    have to copy from/to that one manually
        //    (can't make Transform a NetworkComponent)
        public bool hasTransform;
        public float3 position;
        public quaternion rotation;

        // payload contains the serialized component data.
        // we also store the exact size.
        // -> 'fixed byte[]' is inlined. better than allocating a 'new byte[]'!
        // -> also avoids allocation attacks since the size is always fixed!
        public int payloadSize;
        // 128 bytes per entity should be way enough
        // => has to be the size of the NetworkComponentsSerialization NetworkWriter!
        public const int PayloadFixedSize = 128;
        public fixed byte payload[PayloadFixedSize];

        // constructor for convenience so we don't have to copy serialization
        // manually each time.
        // -> this one is for hasTransform
        public SnapshotMessage(ulong netId, float3 position, quaternion rotation, NetworkWriter128 serialization)
        {
            this.netId = netId;

            hasTransform = true;
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
                        Debug.LogError($"Failed to copy writer at Position={serialization.Position} to StateUpdateMessage payload");
                }
            }
        }

        // this one is without transform
        public SnapshotMessage(ulong netId, NetworkWriter128 serialization)
        {
            this.netId = netId;

            hasTransform = false;
            position = float3.zero;
            rotation = quaternion.identity;

            // were any NetworkComponents serialized?
            payloadSize = serialization.Position;
            if (serialization.Position > 0)
            {
                // copy writer into our payload
                fixed (byte* buffer = payload)
                {
                    if (serialization.CopyTo(buffer, PayloadFixedSize) == 0)
                        Debug.LogError($"Failed to copy writer at BitPosition={serialization.Position} to StateUpdateMessage payload");
                }
            }
        }

        public bool Serialize(ref NetworkWriter writer)
        {
            fixed (byte* buffer = payload)
            {
                return writer.WriteULong(netId) &&
                       writer.WriteBool(hasTransform) &&
                       // only write position if has transform
                       (!hasTransform || writer.WriteFloat3(position)) &&
                       // only write rotation if has transform
                       (!hasTransform || writer.WriteQuaternionSmallestThree(rotation)) &&
                       // write payload
                       writer.WriteInt(payloadSize) &&
                       writer.WriteBytes(buffer, PayloadFixedSize, 0, payloadSize);
            }
        }

        public bool Deserialize(ref NetworkReader reader)
        {
            if (reader.ReadULong(out netId) &&
                reader.ReadBool(out hasTransform) &&
                // only read position if has transform
                (!hasTransform || reader.ReadFloat3(out position)) &&
                // only read position if has transform
                (!hasTransform || reader.ReadQuaternionSmallestThree(out rotation)) &&
                // read payload size
                reader.ReadInt(out payloadSize) &&
                // verify size
                payloadSize <= PayloadFixedSize)
            {
                fixed (byte* buffer = payload)
                {
                    return reader.ReadBytes(buffer, PayloadFixedSize, payloadSize);
                }
            }
            return false;
        }
    }

    // we sometimes need both EntitySnapshotMessage and sender/receiver for burst.
    public struct SnapshotMessageAndConnectionId
    {
        public int connectionId;
        public SnapshotMessage message;
    }
}