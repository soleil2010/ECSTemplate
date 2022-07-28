﻿using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace DOTSNET
{
    public enum ClientState : byte
    {
        DISCONNECTED, CONNECTING, CONNECTED
    }

    // NetworkMessage delegate for clients
    public delegate void NetworkMessageClientDelegate<T>(T message)
        where T : struct, NetworkMessage;

    // message handler delegates are wrapped around another delegate because
    // the wrapping function still knows the message's type <T>, required for:
    //   1. Creating new T() before deserializing. we can't create a new
    //      NetworkMessage interface, only the explicit type.
    //   2. Knowing <T> when deserializing allows for automated serialization
    //      in the future.
    // INetworkReader can't be bursted and casting to interface allocates.
    // all NetworkMessages use NetworkReader directly.
    public delegate void NetworkMessageClientDelegateWrapper(ref NetworkReader reader);

    // NetworkClientSystem should be updated AFTER all other client systems.
    // we need a guaranteed update order to avoid race conditions where it might
    // randomly be updated before other systems, causing all kinds of unexpected
    // effects. determinism is always a good idea!
    [ClientWorld]
    // always update so that OnStart/StopRunning isn't called multiple times.
    // only once.
    [AlwaysUpdateSystem]
    // Server should update after everything else, then broadcast state.
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    // update after everything in client connected LATE group
    [UpdateAfter(typeof(ClientConnectedLateSimulationSystemGroup))]
    // use SelectiveAuthoring to create/inherit it selectively
    [DisableAutoCreation]
    public partial class NetworkClientSystem : SystemBase
    {
        // there is only one NetworkClient(System), so keep state in here
        // (using 1 component wouldn't gain us any performance. only complexity)

        // state instead of active/connecting/connected/disconnecting variables.
        // -> less variables to define
        // -> less variables to set in code
        // -> less chance for odd cases like connecting := active && !connected
        // -> easier to test
        // -> Connect/Disconnect early returns are 100% precise. instead of only
        //    checking .active, we can now do early returns while disconnecting,
        //    or connecting is in process too. it's much safer.
        // -> 100% precise
        // -> state dependent logic is way easier to write with state machines!
        public ClientState state { get; private set; } = ClientState.DISCONNECTED;

        // with DOTS it's possible to freeze all NetworkEntities in place after
        // disconnecting, and only clean them up before connecting again.
        // * a lot of MMOs do this after disconnecting, where a player can still
        //   see the world but nobody moves. it's a good user experience.
        // * it can be useful when debugging so we see the exact scene states
        //   right before a disconnect happened.
        public bool disconnectFreezesScene;

        // dependencies
        [AutoAssign] protected PrefabSystem prefabSystem;
        [AutoAssign] protected NetworkComponentSerializers serializers;
        // transport is manually assign via FindAvailable
        TransportClientSystem transport;

        // message handlers
        // -> we use delegates to be as widely usable as possible
        // -> if necessary, a ComponentSystem could provide a delegate that adds
        //    the NetworkMessage as component so that it can be processed by a
        //    (job)ComponentSystem
        // -> KeyValuePair so that we can deserialize into a copy of Network-
        //    Message of the correct type, before calling the handler
        Dictionary<ushort, NetworkMessageClientDelegateWrapper> handlers =
            new Dictionary<ushort, NetworkMessageClientDelegateWrapper>();

        // all spawned NetworkEntities visible to this client.
        // for cases where we need to modify one of them. this way we don't have
        // run a query over all of them.
        public NativeParallelHashMap<ulong, Entity> spawned;

        // snapshots
        public float snapshotInterval = 0.050f;
        double lastSnapshotTime = 0;
        // SnapshotMessage cache so we can use burst
        NativeList<SnapshotMessage> snapshotMessages;

        // Send serializes messages into ArraySegments, which needs a byte[]
        // we use one for all sends.
        // we initialize it to transport max packet size.
        // we buffer it to have allocation free sends via ArraySegments.
        NativeArray<byte> sendBuffer_Reliable;
        NativeArray<byte> sendBuffer_Unreliable;

        // network controls ////////////////////////////////////////////////////
        // Connect tells the client to start connecting.
        // it returns immediately, but it won't be connected immediately.
        // depending on the transport, it could take a little while.
        public void Connect(string address)
        {
            // do nothing if already active (= if connecting or connected)
            if (state != ClientState.DISCONNECTED)
                return;

            // if freezeSceneWhenDisconnecting is enabled then NetworkEntities
            // were not cleaned up in Disconnect. so let's clean them up before
            // connecting no matter what.
            DestroyAllNetworkEntities();

            // connect
            state = ClientState.CONNECTING;
            transport.Connect(address);
        }

        // Disconnect tells the client to disconnect IMMEDIATELY.
        // unlike connecting, it is always possible to disconnect from a network
        // instantly. so by the time this function returns, we are fully
        // disconnected.
        // => make sure that Transport.ClientDisconnect fully disconnected the
        //    client before returning!
        public void Disconnect()
        {
            // do nothing if already offline
            if (state == ClientState.DISCONNECTED)
                return;

            // disconnect
            transport.Disconnect();

            // IMPORTANT: clear state in OnTransportDisconnected, which is
            //            called by both voluntary & involuntary disconnects!
        }

        // network events called by TransportSystem ////////////////////////////
        // named On'Transport'Connected etc. because OnClientConnected wouldn't
        // be completely obvious that it comes from the transport
        void OnTransportConnected()
        {
            Debug.Log("NetworkClientSystem.OnTransportConnected");
            state = ClientState.CONNECTED;

            // note: we have no authenticated state on the client, because the
            //       client always trusts the server, and the server never
            //       trusts the client.

            // note: we don't invoke a ConnectMessage handler on clients.
            //       systems in ClientConnectedSimulationSystemGroup will start/
            //       stop running when connecting/disconnecting, so use
            //       OnStartRunning to react to a connect instead!

            // we only call OnConnected for classes that inherit from
            // NetworkClientSystem, because they aren't in the
            // ClientConnectedSimulationSystemGroup and can't use OnStart/Stop
            // Running to detect connect/disconnect.
            OnConnected();
        }

        // OnConnected callback for classes that inherit from NetworkClientSystem
        protected virtual void OnConnected() {}

        // segment's array is only valid until returning
        //
        // server->client protocol: <<messageId:2, messages:amount>>
        // (saves bandwidth, improves performance)
        void OnTransportData(NativeSlice<byte> slice)
        {
            // ignore all messages coming in from transport while disconnected.
            if (state != ClientState.CONNECTED)
                return;

            //Debug.Log("NetworkClientSystem.OnTransportData: " + BitConverter.ToString(segment.Array, segment.Offset, segment.Count));

            // create reader around segment.
            // NOTE: casting to INetworkReader would allocate!
            NetworkReader reader = new NetworkReader(slice);

            // the server might batch multiple messages into one packet.
            // we need to try to unpack multiple times.
            //
            // NOTE: we read at least 2 bytes messageId every time. it will not
            //       deadlock.
            //
            // IMPORTANT: when Bitpacking we might send a 7 bit message, which
            //            is rounded to 8 bit = 1 byte, then sent, then received
            //            as 1 byte = 8 bit. we then read the 7 bit message, and
            //            would have one filler bit remaining so the while > 0
            //            check would try to read the filler bit and fail.
            //            => so for bitpacking, only read while we have at least
            //               ONE FULL BYTE left. everything else is filler bits.
            //while (reader.RemainingBits > 0)
            //
            // IMPORTANT: this is a do-while loop instead of a while loop so
            //            that empty byte[] is also detected as partial message.
            //            if we use a while (remainingBits > 8) loop then the
            //            first 'read message id' would never happen for empty
            //            segments, and we wouldn't disconnect.
            //            => see also: ClientSystemTests: ReceiveEmptySegment.
            //            => alternatively we could add a if segment.count==0
            //               check, but do-while is more elegant.
            do
            {
                // unpack the message id
                if (NetworkMessageMeta.UnpackMessage(ref reader, out ushort messageId))
                {
                    //Debug.Log("NetworkClientSystem.OnTransportData messageId: 0x" + messageId.ToString("X4"));

                    // create a new message of type messageId by copying the
                    // template from the handler. we copy it automatically because
                    // messages are value types, so that's a neat trick here.
                    if (handlers.TryGetValue(messageId, out NetworkMessageClientDelegateWrapper handler))
                    {
                        // deserialize and handle the message
                        handler(ref reader);
                    }
                    // unhandled messageIds are not okay. disconnect.
                    else
                    {
                        Debug.Log($"NetworkClientSystem.OnTransportData: unhandled messageId: 0x{messageId:X4}");
                        Disconnect();
                        break;
                    }
                }
                // partial message ids are not okay. disconnect.
                else
                {
                    Debug.Log($"NetworkClientSystem.OnTransportData: failed to unpack message for slice with length: {slice.Length}");
                    Disconnect();
                    break;
                }
            }
            while (reader.Remaining > 0);
        }

        void OnTransportDisconnected()
        {
            Debug.Log("NetworkClientSystem.OnTransportDisconnected");
            // OnTransportDisconnect happens either after we called Disconnect,
            // or after the server disconnected our connection.
            // if the server disconnected us, then we need to set the state!
            state = ClientState.DISCONNECTED;
            spawned.Clear();

            // only destroy NetworkEntities if the user doesn't want to freeze
            // the scene after disconnects. otherwise they will be cleaned up
            // before the next connect.
            if (!disconnectFreezesScene)
            {
                DestroyAllNetworkEntities();
            }

            // note: we don't invoke a DisconnectMessage handler on clients.
            //       systems in ClientConnectedSimulationSystemGroup will start/
            //       stop running when connecting/disconnecting, so use
            //       OnStopRunning to react to a disconnect instead!

            // we only call OnDisconnected for classes that inherit from
            // NetworkClientSystem, because they aren't in the
            // ClientConnectedSimulationSystemGroup and can't use OnStart/Stop
            // Running to detect connect/disconnect.
            OnDisconnected();
        }

        // OnDisconnected callback for classes that inherit from NetworkClientSystem
        protected virtual void OnDisconnected() {}

        // messages ////////////////////////////////////////////////////////////
        // raw send packed message.
        // this only exists because we can't use burst in Send<T> generic (yet).
        // but some expensive messages (like LocalWorldState), we want to
        // serialize with burst and then send the serialized writer.
        // => internal for now.
        // => slice needs to contain PackMessage() result.
        internal void SendRaw(NativeSlice<byte> message, Channel channel = Channel.Reliable)
        {
            // send to transport.
            // (it will have to free up the segment immediately)
            if (!transport.Send(message, channel))
            {
                // send can fail if the transport has issues
                // like full buffers, broken pipes, etc.
                // so if Send gets called before the next
                // transport update removes the broken
                // connection, then we will see a warning.
                Debug.LogWarning($"NetworkClientSystem.Send: failed to send message of size {message.Length}. This can happen if the connection is broken before the next transport update removes it.");

                // TODO consider flagging the connection as broken
                // to only log the warning message once like we do
                // in NetworkServerSystem.Send
            }
        }

        // send a message to the server
        public void Send<T>(T message, Channel channel = Channel.Reliable)
            where T : struct, NetworkMessage
        {
            // use the buffer for the given channel
            NativeArray<byte> sendBuffer = channel == Channel.Reliable ? sendBuffer_Reliable : sendBuffer_Unreliable;

            // make sure that we can use the send buffer
            // (requires at least enough bytes for header!)
            if (sendBuffer.Length > NetworkMessageMeta.IdSize)
            {
                // create writer around buffer.
                // NOTE: casting to INetworkWriter would allocate!
                NetworkWriter writer = new NetworkWriter(sendBuffer);

                // pack the message with <<id, content>>
                // NOTE: burst doesn't work with generics yet unfortunately.
                if (NetworkMessageMeta.PackMessage(message, ref writer))
                {
                    SendRaw(writer.slice, channel);
                }
                else Debug.LogWarning($"NetworkClientSystem.Send: packing message of type {typeof(T)} failed. Maybe the message is bigger than sendBuffer {sendBuffer.Length} bytes?");
            }
            else Debug.LogError($"NetworkClientSystem.Send: sendBuffer not initialized or 0 length: {sendBuffer}");
        }

        // convenience function to send a whole NativeList of messages to the
        // server, useful for Jobs/Burst.
        public void Send<T>(NativeList<T> messages)
            where T : unmanaged, NetworkMessage
        {
            // send each one
            // note: could optimize like server.Send(NativeMultiMap) later
            for (int i = 0; i < messages.Length; ++i)
            {
                Send(messages[i]);
            }
        }

        // wrap handlers with 'new T()' and deserialization.
        // (see NetworkMessageClientDelegateWrapper comments)
        NetworkMessageClientDelegateWrapper WrapHandler<T>(NetworkMessageClientDelegate<T> handler, Func<T> messageAllocator, NetworkMessageDeserializerDelegate<T> messageDeserializer)
            where T : struct, NetworkMessage
        {
            return delegate(ref NetworkReader reader)
            {
                // deserialize the message
                // -> we do this in WrapHandler because in here we still
                //    know <T>
                // -> later on we only know NetworkMessage
                // -> knowing <T> allows for automated serialization
                //    (which we don't do at the moment anymore)
                //if (reader.ReadBlittable(out T message))

                // allocate message based on passed allocator.
                // 'T = default' usually, unless someone wants to reuse messages.
                T message = messageAllocator();

                // deserialize message based on passed deserializer.
                // (useful for bursted deserialization)
                //if (message.Deserialize(ref reader))
                if (messageDeserializer(ref message, ref reader))
                {
                    // call it
                    handler(message);
                }
                // invalid message contents are not okay. disconnect.
                else
                {
                    Debug.Log($"NetworkClientSystem: failed to deserialize {typeof(T)} for reader with Position: {reader.Position} Remaining: {reader.Remaining}");
                    Disconnect();
                }
            };
        }

        // register handler for a message.
        // we use 'where NetworkMessage' to make sure it only works for them.
        // => we use <T> generics so we don't have to pass both messageId and
        //    NetworkMessage template each time. it's just cleaner this way.
        // => message allocator is customizable in case we want to reuse large
        //    messages like WorldState later
        //
        // usage: RegisterHandler<TestMessage>(func);
        public bool RegisterHandler<T>(NetworkMessageClientDelegate<T> handler, Func<T> messageAllocator = null, NetworkMessageDeserializerDelegate<T> messageDeserializer = null)
            where T : struct, NetworkMessage
        {
            // get message id from message type
            ushort messageId = NetworkMessageMeta.GetId<T>();

            // use default allocator if none was specified
            if (messageAllocator == null)
                messageAllocator = NetworkMessageMeta.DefaultMessageAllocator<T>;

            // use default deserializer if none was specified
            if (messageDeserializer == null)
                messageDeserializer = NetworkMessageMeta.DefaultMessageDeserializer;

            // make sure no one accidentally overwrites a handler
            // (might happen in case of duplicate messageIds etc.)
            if (!handlers.ContainsKey(messageId))
            {
                handlers[messageId] = WrapHandler(handler, messageAllocator, messageDeserializer);
                return true;
            }

            // log warning in case we tried to overwrite. could be extremely
            // useful for debugging/development, so we notice right away that
            // a system accidentally called it twice, or that two messages
            // accidentally have the same messageId.
            Debug.LogWarning($"NetworkClientSystem: handler for {typeof(T)} was already registered.");
            return false;
        }

        // unregister a handler.
        // => we use <T> generics so we don't have to pass messageId  each time.
        public bool UnregisterHandler<T>()
            where T : struct, NetworkMessage
        {
            // get message id from message type
            ushort messageId = NetworkMessageMeta.GetId<T>();
            return handlers.Remove(messageId);
        }

        // spawn ///////////////////////////////////////////////////////////////
        // on the server, Spawn() spawns an Entity on all clients.
        // on the client, Spawn() reacts to the message and spawns it.
        public void Spawn(FixedBytes16 prefabId, ulong netId, bool owned, float3 position, quaternion rotation, NetworkReader128 deserialization)
        {
            // find prefab to instantiate
            if (prefabSystem.Get(prefabId, out Entity prefab))
            {
                // instantiate the prefab
                Entity entity = EntityManager.Instantiate(prefab);

                // set prefabId once when spawning
                NetworkIdentity identity = GetComponent<NetworkIdentity>(entity);
                identity.prefabId = prefabId;
                SetComponent(entity, identity);

                // set spawn position & rotation
                SetComponent(entity, new Translation{Value = position});
                SetComponent(entity, new Rotation{Value = rotation});

                // assign NetworkComponents deserialization
                // deserialization will ignore CLIENT_TO_SERVER if authority.
                //
                // IMPORTANT: the caller is responsible for:
                //   * calling Spawn() for each message
                //   * then calling DeserializeAll() to deserialize all
                //     NetworkComponents once
                // (instead of calling DeserializeAll after EVERY spawn in here)
                SetComponent(entity, new NetworkComponentsDeserialization{reader = deserialization});

                // add to spawned before calling ApplyState
                spawned[netId] = entity;

                // reuse ApplyState to apply the rest of the state
                ApplyState(netId, owned);
                //Debug.LogWarning("NetworkClientSystem.Spawn: spawned " + EntityManager.GetName(entity) + " with prefabId=" + prefabId + " netId=" + netId);
            }
            else Debug.LogWarning($"NetworkClientSystem.Spawn: unknown prefabId={Conversion.Bytes16ToGuid(prefabId)}");
        }

        // on the server, Unspawn() unspawns an Entity on all clients.
        // on the client, Unspawn() reacts to the message and unspawns it.
        public void Unspawn(ulong netId)
        {
            // find spawned entity
            if (spawned.TryGetValue(netId, out Entity entity))
            {
                // destroy the entity
                //Debug.LogWarning("NetworkClientSystem.Unspawn: unspawning " + EntityManager.GetName(entity) + " with netId=" + netId);
                EntityManager.DestroyEntity(entity);

                // remove from spawned
                spawned.Remove(netId);
            }
            else Debug.LogWarning($"NetworkClientSystem.Unspawn: unknown netId={netId}. Was unspawn called twice for the same netId?");
        }

        // synchronize an entity's state from a server message
        public void ApplyState(ulong netId, bool owned)
        {
            // find spawned entity
            if (spawned.TryGetValue(netId, out Entity entity))
            {
                // apply NetworkIdentity data
                NetworkIdentity identity = GetComponent<NetworkIdentity>(entity);
                identity.netId = netId;
                identity.owned = owned;
                SetComponent(entity, identity);
            }
            else Debug.LogWarning($"NetworkClientSystem.ApplyState: unknown netId={netId}. Was the Entity spawned?");
        }

        // destroy all NetworkEntities to clean up
        protected void DestroyAllNetworkEntities()
        {
            // IMPORTANT: EntityManager.DestroyEntity(query) does NOT work for
            //            Entities with LinkedEntityGroup. We would get an error
            //            about LinkedEntities needing to be destroyed first:
            //            https://github.com/vis2k/DOTSNET/issues/28
            //
            //            The solution is to use Destroy(NativeArray), which
            //            destroys linked entities too:
            //            https://forum.unity.com/threads/how-to-destroy-all-linked-entities.714890/#post-4777796
            //
            //            See NetworkClientSystemTests:
            //            StopDestroysAllNetworkEntitiesWithLinkedEntityGroup()
            EntityQuery networkEntities = GetEntityQuery(typeof(NetworkIdentity));
            NativeArray<Entity> array = networkEntities.ToEntityArray(Allocator.TempJob);
            EntityManager.DestroyEntity(array);
            array.Dispose();
        }

        // snapshots ///////////////////////////////////////////////////////////
        void BroadcastSnapshots()
        {
            // tell serializers to serialize all NetworkComponents that we have
            // authority over
            // (if networkEntity.owned && component.syncDirection == CLIENT_TO_SERVER)
            serializers.SerializeAll();

            // bursted Entities.ForEach is faster than going through .spawned
            NativeList<SnapshotMessage> messages = snapshotMessages;
            Entities.ForEach((in Translation translation,
                              in Rotation rotation,
                              in NetworkIdentity identity,
                              in NetworkComponentsSerialization serialization) =>
            {
                // sync transform only if owned && CLIENT_TO_SERVER
                bool hasTransformAuthority = identity.owned &&
                                             identity.transformDirection == SyncDirection.CLIENT_TO_SERVER;

                // create snapshot message with or without transform
                SnapshotMessage message = hasTransformAuthority
                    ? new SnapshotMessage(identity.netId, translation.Value, rotation.Value, serialization.writer)
                    : new SnapshotMessage(identity.netId, serialization.writer);

                // send only if there's anything to sync
                if (message.hasTransform || message.payloadSize > 0)
                {
                    messages.Add(message);
                    //Debug.LogWarning($"Cl->Sv Snapshot for netId={message.netId} hasTransform={message.hasTransform} payloadBitSize={message.payloadBitSize}");
                }
            })
            .Run();

            // flush messages
            for (int i = 0; i < snapshotMessages.Length; ++i)
                Send(snapshotMessages[i]);

            // clear messages
            snapshotMessages.Clear();
        }

        // component system ////////////////////////////////////////////////////
        // cache TransportSystem in OnStartRunning after all systems were created
        // (we can't assume that TransportSystem.OnCreate is created before this)
        protected override void OnStartRunning()
        {
            // make sure we don't hook ourselves up to transport events twice
            if (transport != null)
            {
                Debug.LogError("NetworkClientSystem: transport was already configured before!");
                return;
            }

            // initialize native collections with reasonable default capacities
            spawned = new NativeParallelHashMap<ulong, Entity>(1000, Allocator.Persistent);
            // we usually own <10 objects, so 10 as initial capacity is fine
            snapshotMessages = new NativeList<SnapshotMessage>(10, Allocator.Persistent);

            // find available client transport
            transport = TransportSystem.FindAvailable(World) as TransportClientSystem;
            if (transport != null)
            {
                // hook ourselves up to Transport events
                //
                // ideally we would do this in OnCreate once and remove them in
                // OnDestroy. but since Transport isn't available until
                // OnStartRunning, we do it here with a transport != null check
                // above to make sure we only do it once!
                //
                // IMPORTANT: += so that we don't remove anyone else's hooks,
                //            e.g. statistics!
                transport.OnConnected += OnTransportConnected;
                transport.OnData += OnTransportData;
                transport.OnDisconnected += OnTransportDisconnected;

                // initialize send buffer
                sendBuffer_Reliable = new NativeArray<byte>(transport.GetMaxPacketSize(Channel.Reliable), Allocator.Persistent);
                sendBuffer_Unreliable = new NativeArray<byte>(transport.GetMaxPacketSize(Channel.Unreliable), Allocator.Persistent);
            }
            else Debug.LogError($"NetworkClientSystem: no available TransportClientSystem found on this platform: {Application.platform}");
        }

        protected override void OnUpdate()
        {
            // only update world while connected
            if (state == ClientState.CONNECTED)
            {
                // broadcast snapshots for local authority components (if any)
                // enough time elapsed?
                if (Time.ElapsedTime >= lastSnapshotTime + snapshotInterval)
                {
                    BroadcastSnapshots();
                    lastSnapshotTime = Time.ElapsedTime;
                }
            }

            // always update transport after everything else was updated
            transport.LateUpdate();
        }

        protected override void OnDestroy()
        {
            // disconnect client in case it was running
            Disconnect();

            // remove the hooks that we have setup in OnStartRunning.
            // note that Disconnect will fire hooks again. so we only remove
            // them afterwards, and not in OnStopRunning or similar.
            if (transport != null)
            {
                transport.OnConnected -= OnTransportConnected;
                transport.OnData -= OnTransportData;
                transport.OnDisconnected -= OnTransportDisconnected;
            }

            // dispose native collections
            // (check if was created. OnStartRunning might not have been called)
            if (spawned.IsCreated)
                spawned.Dispose();
            if (snapshotMessages.IsCreated)
                snapshotMessages.Dispose();
            if (sendBuffer_Reliable.IsCreated)
                sendBuffer_Reliable.Dispose();
            if (sendBuffer_Unreliable.IsCreated)
                sendBuffer_Unreliable.Dispose();
        }
    }
}
