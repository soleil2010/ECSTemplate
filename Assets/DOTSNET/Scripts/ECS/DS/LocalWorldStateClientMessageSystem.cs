// Applies the Snapshot message to the Entity.
// There is no interpolation yet, only the bare minimum.
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace DOTSNET
{
    // the system is always created because Snapshot is always needed
    [AlwaysUpdateSystem]
    public partial class LocalWorldStateClientMessageSystem : NetworkClientMessageSystem<LocalWorldStateMessage>
    {
        // dependencies
        [AutoAssign] protected NetworkComponentSerializers serialization;
        [AutoAssign] protected TransportClientSystem transport;

        // cache and reuse LocalWorldMessage instead of allocating each time
        LocalWorldStateMessage cachedMessage;
        protected override LocalWorldStateMessage MessageAllocator()
        {
            cachedMessage.Reset();
            return cachedMessage;
        }

        // deserializing a large LocalWorldState message is costly.
        // it's the BIGGEST OPERATION in profiler by far.
        // use a custom Deserializer since we can use Burst here!
        protected override bool MessageDeserializer(ref LocalWorldStateMessage message, ref NetworkReader reader)
        {
            // return message.Deserialize(ref reader);

            // can't use ref in lambda. need to copy everything back and forth.
            LocalWorldStateMessage _message = message;
            NetworkReader _reader = reader;
            bool result = false;

            Job.WithCode(() => {
                result = _message.Deserialize(ref _reader);
            }).Run();

            // apply to refs
            message = _message;
            reader = _reader;
            return result;
        }

        // allocate missing entities list only once.
        // native for burst support.
        NativeList<ulong> missing;

        // we need transport max message size.
        // which is only available in OnStartRunning.
        // OnCreated is too early, authoring still modifies the size.
        protected override void OnStartRunning()
        {
            // call base because it might be implemented.
            base.OnStartRunning();

            // local world state message can fill up to transport.max - msg id
            int maxReliable = transport.GetMaxPacketSize(Channel.Reliable);
            cachedMessage = new LocalWorldStateMessage(maxReliable - NetworkMessageMeta.IdSize, 1000, Allocator.Persistent);
            missing = new NativeList<ulong>(1000, Allocator.Persistent);
        }

        protected override void OnStopRunning()
        {
            // dispose natives with Dependency in case they are used in a Job
            cachedMessage.Dispose();
            missing.Dispose(Dependency);

            // call base because it might be implemented.
            base.OnStopRunning();
        }

        // message handler
        // IMPORTANT: it uses the cached message. only valid until returning.
        protected override void OnMessage(LocalWorldStateMessage message)
        {
            // store in messages
            // note: we might overwrite the previous Snapshot, but
            //       that's fine since we don't send deltas and we only care
            //       about the latest position/rotation.
            //       so this can even avoid some computations.
            //Debug.Log($"Client received LocalWorldStateMessage with {message.entities.Count()} entities");

            // the message was already deserialized based on lastEntities.
            // copy current entities to lastEntities FIRST before the below code
            CopyEntitiesToLastEntities(message); // BURSTED!

            // before we touch messages, find spawned netIds that aren't in
            // messages so we know which to despawn after
            CalculateMissing(message); // BURSTED! (iterates all)

            // apply new state to entities which were already spawned
            ApplyToSpawned(message);   // BURSTED! (iterates all)

            // spawn the ones that weren't processed
            SpawnRemaining(message);   // not bursted (iterates only spawned)

            // despawn the ones that were not in message
            DespawnMissing();          // not bursted (iterates only despawned)

            // tell serializers to deserialize all NetworkComponents once
            serialization.DeserializeAll();
        }

        // helper function to copy entities to last entities (bursted)
        void CopyEntitiesToLastEntities(LocalWorldStateMessage message)
        {
            // modifies them (it removes them to detect despawns etc.)
            Job.WithCode(() => {
                message.entities.CopyTo(message.lastEntities);
            }).Run();
            //Debug.LogWarning("Swap entities=" + message.entities.Count() + " => last");
        }

        void CalculateMissing(LocalWorldStateMessage message)
        {
            // capture for burst
            NativeList<ulong> _missing = missing;
            NativeParallelHashMap<ulong, Entity> _spawned = client.spawned;
            NativeParallelHashMap<ulong, EntityState> entityStates = message.entities;

            // everything is a native collection, so we can burst this
            Job.WithCode(() => {
                _missing.Clear();
                foreach (KeyValue<ulong, Entity> kvp in _spawned)
                {
                    ulong netId = kvp.Key;
                    if (!entityStates.ContainsKey(netId))
                        _missing.Add(netId);
                }
            })
            .Run();
        }

        // apply messages for all spawned entities with BURST
        void ApplyToSpawned(LocalWorldStateMessage message)
        {
            // we assume large amounts of entities, so we go through all of them
            // and apply their Snapshot message (if any).
            NativeParallelHashMap<ulong, EntityState> entityStates = message.entities;
            Entities.ForEach((ref Translation translation,
                              ref Rotation rotation,
                              ref NetworkComponentsDeserialization deserialization,
                              in NetworkIdentity identity) =>
            {
                // do we have a message for this netId?
                if (entityStates.ContainsKey(identity.netId))
                {
                    EntityState entityState = entityStates[identity.netId];
                    //Debug.LogWarning($"Apply Snapshot netId={message.netId} pos={message.position} rot={message.rotation}");

                    // server always syncs transform.
                    // apply only if not local player && CLIENT_TO_SERVER.
                    bool hasTransformAuthority = identity.owned &&
                                                 identity.transformDirection == SyncDirection.CLIENT_TO_SERVER;
                    if (!hasTransformAuthority)
                    {
                        translation.Value = entityState.position;
                        rotation.Value = entityState.rotation;
                        //Debug.LogWarning($"Apply Snapshot Transform for netId={message.netId} pos={message.position} rot={message.rotation}");
                    }

                    // copy payload to Deserialization component
                    // deserialization will ignore CLIENT_TO_SERVER if authority.
                    unsafe
                    {
                        deserialization.reader = new NetworkReader128(entityState.payload, entityState.payloadSize);
                    }

                    // this message was handled. remove it.
                    entityStates.Remove(identity.netId);
                }
            })
            // DO NOT Schedule()!
            // The time it takes to start the job is way too noticeable for
            // position updates on clients. Try to run the Pong example with
            // server as build and client as build. It's way too noticeable.
            .Run();
        }

        // spawn the ones that were not found/applied
        void SpawnRemaining(LocalWorldStateMessage message)
        {
            // ApplyToSpawned removes all which were found
            // so spawn everything that's remaining
            foreach (KeyValue<ulong, EntityState> kvp in message.entities)
            {
                // only if not spawned yet
                if (!client.spawned.ContainsKey(kvp.Key))
                {
                    // get message
                    EntityState entityState = kvp.Value;

                    // copy payload to reader
                    NetworkReader128 deserialization;
                    unsafe
                    {
                        deserialization = new NetworkReader128(entityState.payload, entityState.payloadSize);
                    }

                    // call spawn
                    client.Spawn(entityState.prefabId,
                                 entityState.netId,
                                 entityState.owned,
                                 entityState.position,
                                 entityState.rotation,
                                 deserialization);
                    //Debug.Log("Spawned from Snapshot: " + kvp.Key);
                }
                // already spawned then.
                // this shouldn't happen if we do it right.
                else Debug.LogWarning("Already spawned: " + kvp.Key + " this should never happen.");
            }
        }

        void DespawnMissing()
        {
            foreach (ulong netId in missing)
            {
                //Debug.Log($"Despawning netId={netId} because it was not in LocalWorldState anymore");
                client.Unspawn(netId);
            }
        }

        // NOTE: Update isn't called until the first entity was spawned because
        //       this system doesn't have the [AlwaysUpdateSystem] attribute.
        protected override void OnUpdate() {}
    }
}