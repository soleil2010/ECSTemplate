// Applies the TransformMessage to the Entity.
// There is no interpolation yet, only the bare minimum.
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace DOTSNET
{
    // the system is always created because Snapshot is always needed
    public partial class SnapshotServerMessageSystem : NetworkServerMessageSystem<SnapshotMessage>
    {
        // dependencies
        [AutoAssign] protected NetworkComponentSerializers serialization;

        // cache new messages <netId, message> to apply all at once in OnUpdate.
        // finding the Entity with netId and calling SetComponent for one Entity
        // in OnMessage 10k times would be very slow.
        // a ForEach query is faster, it can use Burst(!) and it could be a Job.
        NativeParallelHashMap<ulong, SnapshotMessageAndConnectionId> messages;

        protected override bool RequiresAuthentication() => true;

        protected override void OnCreate()
        {
            // call base because it might be implemented.
            base.OnCreate();

            // create messages HashMap
            messages = new NativeParallelHashMap<ulong, SnapshotMessageAndConnectionId>(1000, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            // dispose with Dependency in case it's used in a Job
            messages.Dispose(Dependency);

            // call base because it might be implemented.
            base.OnDestroy();
        }

        protected override void OnMessage(int connectionId, SnapshotMessage message)
        {
            // store in messages
            // note: we might overwrite the previous Snapshot, but
            //       that's fine since we don't send deltas and we only care
            //       about the latest position/rotation.
            //       so this can even avoid some computations.
            //
            // IMPORTANT: OnUpdate checks for connectionId's authority later!
            messages[message.netId] = new SnapshotMessageAndConnectionId
            {
                connectionId = connectionId,
                message = message
            };
            //Debug.LogWarning($"Server received SnapshotMessage for netId={message.netId} with payload={message.payloadBitSize} bits");
        }

        protected override void OnUpdate()
        {
            // don't need to Entities.ForEach every update.
            // only if we actually received anything.
            if (messages.IsEmpty)
                return;

            // we need to apply messages for N owned objects.
            // => using Entities.ForEach and apply messages to select entities
            //    works and is fast...
            // => but usually we have way more world objects than players, e.g.
            //    1 million moving monsters vs. 1000 players.
            //    (for 1 million monsters, Entities.ForEach takes 22ms)
            // => iterating only owned objects scales way better here.

            // we assume large amounts of entities, so we go through all of them
            // and apply their Snapshot message (if any).

            // searching .spawned = 3-8 ms for 1 million monsters
            NativeParallelHashMap<ulong, SnapshotMessageAndConnectionId> _messages = messages;
            NativeParallelHashMap<ulong, Entity> _spawned = server.spawned;
            Job.WithCode(() => {
                foreach (KeyValue<ulong, SnapshotMessageAndConnectionId> kvp in _messages)
                {
                    // netId in spawned?
                    if (_spawned.TryGetValue(kvp.Key, out Entity entity))
                    {
                        NetworkIdentity identity = GetComponent<NetworkIdentity>(entity);

                        SnapshotMessageAndConnectionId entry = _messages[identity.netId];
                        SnapshotMessage message = entry.message;

                        // is this entity owned by this connection?
                        // only the owner can send Snapshot, and only for those
                        // components that are CLIENT_TO_SERVER SyncDirection.
                        if (identity.connectionId == entry.connectionId)
                        {
                            // apply position & rotation
                            // -> only if CLIENT_TO_SERVER direction
                            if (identity.transformDirection == SyncDirection.CLIENT_TO_SERVER)
                            {
                                if (message.hasTransform)
                                {
                                    //translation.Value = message.position;
                                    //rotation.Value = message.rotation;
                                    SetComponent(entity, new Translation{Value = message.position});
                                    SetComponent(entity, new Rotation{Value = message.rotation});
                                }
                                else Debug.LogError($"Snapshot Client->Server message: missing transform data for netId={message.netId}. Make sure the NetworkIdentity's transformDirection is set to CLIENT_TO_SERVER on the client too.");
                            }

                            // copy payload to Deserialization component
                            // deserialzation will apply only if has authority
                            unsafe
                            {
                                NetworkReader128 reader = new NetworkReader128(message.payload, message.payloadSize);
                                //deserialization.reader = new NetworkReader128(message.payload, sizeInBytes);
                                SetComponent(entity, new NetworkComponentsDeserialization{reader = reader});
                            }
                        }
                        // otherwise someone tries to manipulate another entity.
                        else
                        {
                            Debug.LogWarning($"connectionId={entry.connectionId} tried to modify Entity with netId={message.netId} without authority. Disconnecting.");
                            // can't burst server.Disconnect.
                            // dropping the unauthorized movement attempts is fine.
                            //server.Disconnect(connectionId);
                        }
                    }
                }
            })
            .Run();

            /*
            // iterate Entities.ForEach = 5-14 ms for 1 million monsters
            //
            // we assume large amounts of entities, so we go through all of them
            // and apply their Snapshot message (if any).
            NativeHashMap<ulong, SnapshotMessageAndConnectionId> _messages = messages;
            Entities.ForEach((ref Translation translation,
                              ref Rotation rotation,
                              ref NetworkComponentsDeserialization deserialization,
                              in NetworkIdentity identity) =>
            {
                // do we have a message for this netId?
                if (_messages.ContainsKey(identity.netId))
                {
                    SnapshotMessageAndConnectionId entry = _messages[identity.netId];
                    SnapshotMessage message = entry.message;

                    // is this entity owned by this connection?
                    // only the owner can send Snapshot, and only for those
                    // components that are CLIENT_TO_SERVER SyncDirection.
                    if (identity.connectionId == entry.connectionId)
                    {
                        // apply position & rotation
                        // -> only if CLIENT_TO_SERVER direction
                        if (identity.transformDirection == SyncDirection.CLIENT_TO_SERVER)
                        {
                            if (message.hasTransform)
                            {
                                translation.Value = message.position;
                                rotation.Value = message.rotation;
                            }
                            else Debug.LogError($"Snapshot Client->Server message: missing transform data for netId={message.netId}. Make sure the NetworkIdentity's transformDirection is set to CLIENT_TO_SERVER on the client too.");
                        }

                        // copy payload to Deserialization component
                        // deserialzation will apply only if has authority
                        unsafe
                        {
                            deserialization.reader = new NetworkReader128(message.payload, message.payloadBitSize);
                        }
                    }
                    // otherwise someone tries to manipulate another entity.
                    else
                    {
                        Debug.LogWarning($"connectionId={entry.connectionId} tried to modify Entity with netId={message.netId} without authority. Disconnecting.");
                        // can't burst server.Disconnect.
                        // dropping the unauthorized movement attempts is fine.
                        //server.Disconnect(connectionId);
                    }
                }
            })
            // DO NOT Schedule()!
            // The time it takes to start the job is way too noticeable for
            // position updates on clients. Try to run the Pong example with
            // server as build and client as build. It's way too noticeable.
            .Run();
            */

            // tell serializers to deserialize all NetworkComponents once
            serialization.DeserializeAll();

            // clear messages after everything is done
            // (in case we ever need their memory when deserializing in the future)
            messages.Clear();
        }
    }
}
