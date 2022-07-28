// A NetworkEntity can have multiple NetworkComponents that are automatically
// synced by DOTSNET.
//
// IMPORTANT: define a NetworkComponentSerializer<T> for each NetworkComponent!
// (see NetworkTransform.cs)
// needed automated serialization. will be codegen / genericjob some day.
using Unity.Entities;

namespace DOTSNET
{
    public interface NetworkComponent : IComponentData
    {
        // should this component be synced from server to client, or vice versa?
        SyncDirection GetSyncDirection();

        // Serialize serializes server data for the client via NetworkWriter.
        // returns false if buffer was too small for all the data.
        // => we need to let the user decide how to serialize. WriteBlittable
        //    isn't enough in all cases, e.g. arrays, compression, bit packing
        // => see also: gafferongames.com/post/reading_and_writing_packets/
        // => INetworkWriter can't be bursted, and passing interface as ref 
        //    allocs. NetworkWriter128 should be enough for now.
        bool Serialize(ref NetworkWriter128 writer);

        // Deserialize deserializes server data on the client via NetworkReader.
        // returns false if buffer was too small for all the data.
        // => we need to let the user decide how to serialize. WriteBlittable
        //    isn't enough in all cases, e.g. arrays, compression, bit packing
        // => INetworkReader can't be bursted, and passing interface as ref 
        //    allocs. NetworkReader128 should be enough for now.
        bool Deserialize(ref NetworkReader128 reader);
    }
}