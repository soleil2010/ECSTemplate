using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace DOTSNET
{
    public enum ServerState : byte
    {
        INACTIVE,
        ACTIVE
    }

    // NetworkMessage delegate for message handlers
    public delegate void NetworkMessageServerDelegate<T>(int connectionId, T message)
        where T : struct, NetworkMessage;

    // message handler delegates are wrapped around another delegate because
    // the wrapping function still knows the message's type <T>, required for:
    //   1. Creating new T() before deserializing. we can't create a new
    //      NetworkMessage interface, only the explicit type.
    //   2. Knowing <T> when deserializing allows for automated serialization
    //      in the future.
    // INetworkReader can't be bursted and casting to interface allocates.
    // all NetworkMessages use NetworkReader directly.
    public delegate void NetworkMessageServerDelegateWrapper(int connectionId, NetworkReader reader);

    // NetworkServerSystem should be updated AFTER all other server systems.
    // we need a guaranteed update order to avoid race conditions where it might
    // randomly be updated before other systems, causing all kinds of unexpected
    // effects. determinism is always a good idea!
    // (this way NetworkServerMessageSystems can register handlers before
    //  OnStartRunning is called. remember that for all server systems,
    //  OnStartRunning is called the first time only after starting, so they
    //  absolutely NEED to be updated before this class, otherwise it would be
    //  impossible to register a ConnectMessage handler before
    //  OnTransportConnected is called (so the handler would never be called))
    [ServerWorld]
    // always update so that OnStart/StopRunning isn't called multiple times.
    // only once.
    [AlwaysUpdateSystem]
    // Server should update after everything else, then broadcast state.
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    // update after everything in server active LATE group
    [UpdateAfter(typeof(ServerActiveLateSimulationSystemGroup))]
    // use SelectiveAuthoring to create/inherit it selectively
    [DisableAutoCreation]
    public partial class NetworkServerSystem : SystemBase
    {
        // there is only one NetworkServer(System), so keep state in here
        // (using 1 component wouldn't gain us any performance. only complexity)

        // server state. we could use a bool, but we use a state for consistency
        // with NetworkClientSystem.state. it's more obvious this way.
        public ServerState state { get; private set; } = ServerState.INACTIVE;

        // dependencies
        [AutoAssign] protected PrefabSystem prefabSystem;
        [AutoAssign] protected NetworkComponentSerializers serializers;
        [AutoAssign] protected InterestManagementSystem interestManagement;
        // transport is manually assign via FindAvailable
        public TransportServerSystem transport;

        // auto start the server in headless mode (= linux console)
        public bool startIfHeadless = true;

        // detect headless mode check, originally created for uMMORPG classic
        public readonly bool isHeadless =
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        // auto start headless only once
        bool headlessStarted;

        // tick rate for all ServerActiveSimulationSystemGroup systems in Hz.
        public float tickRate = 60;

        // connection limit. can be set at runtime to limit connections under
        // heavy load if needed.
        public int connectionLimit = 1000;

        // connections <connectionId, state>
        // we use a Dictionary instead of a NativeHashMap for now.
        public Dictionary<int, ConnectionState> connections;

        // objects owned by connections.
        // => it's easier to unspawn them if we keep a list here.
        //    otherwise we would have to iterate all server objects on each
        //    disconnect.
        // => NativeHashMap<connId, entity> so we can access from burst!
        public NativeParallelMultiHashMap<int, Entity> ownedPerConnection;

        // message handlers
        // -> we use delegates to be as widely usable as possible
        // -> KeyValuePair so that we can deserialize into a copy of Network-
        //    Message of the correct type, before calling the handler
        Dictionary<ushort, NetworkMessageServerDelegateWrapper> handlers =
            new Dictionary<ushort, NetworkMessageServerDelegateWrapper>();

        // all spawned NetworkEntities.
        // for cases where we need to modify one of them. this way we don't have
        // run a query over all of them.
        // => Native collection so we can use it in burst!
        public NativeParallelHashMap<ulong, Entity> spawned;

        // snapshot state updates in the given interval
        public float broadcastInterval = 0.050f;
        double lastSnapshotTime = 0;
        // broadcasting EntityStates cached so we can use burst
        NativeParallelMultiHashMap<int, EntityState> broadcastEntityStates;
        // reuse LocalWorldState message because it allocates
        LocalWorldStateMessage localWorldStateMessage;
        // reuse DS candidates sorted by priority
        NativeList<EntityState> sortedEntityStates;

        // configurable LocalWorldState max size per connection.
        // useful to limit broadcast bandwidth in large worlds to something reasonable.
        // otherwise if a player's connection is slow, it wouldn't choke when
        // walking into a large town or a horde of monsters, and then disconnect.
        // => 512 KB is huge. more than enough. yet still a reasonable limit.
        //    most transports' MaxMessageSizes are smaller anyway.
        //
        // note: LocalWorldState size will be Min(localWorldStateMaxSize, transport.GetMaxPacketSize)
        // note: can calculate KB/s from maxsize and sendInterval
        public int broadcastMaxSize = 512 * 1024;

        // we can send up to transport.GetMaxBatchSize bytes
        NativeArray<byte> sendBuffer_Reliable;
        NativeArray<byte> sendBuffer_Unreliable;

        // network control /////////////////////////////////////////////////////
        public void StartServer()
        {
            // do nothing if already started
            if (state == ServerState.ACTIVE)
                return;

            // use limit as first capacity. can still grow at runtime.
            connections = new Dictionary<int, ConnectionState>(connectionLimit);
            // start transport
            transport.Start();
            // set server to active AFTER starting transport to avoid potential
            // race conditions where someone might auto start a server when
            // active, assuming that transport was started too.
            state = ServerState.ACTIVE;

            // spawn scene objects
            SpawnSceneObjects();
        }

        public void StopServer()
        {
            // do nothing if already stopped
            if (state == ServerState.INACTIVE)
                return;

            // server solely operates on NetworkEntities.
            // destroy them all when stopping to clean up properly.
            // * StartServer spawns scene objects, we need to clean them before
            //   starting again, otherwise we have them 2x, 3x, etc.
            // * any accidental leftovers like projectiles should be cleaned too
            DestroyAllNetworkEntities();

            transport.Stop();
            state = ServerState.INACTIVE;

            // dispose all connections to free native memory
            foreach (ConnectionState connection in connections.Values)
                connection.Dispose();

            // connections were 'null' before starting
            connections = null;

            spawned.Clear();
            ownedPerConnection.Clear();
        }

        // disconnect a connection
        public void Disconnect(int connectionId)
        {
            // valid connectionId?
            if (connections.ContainsKey(connectionId))
            {
                // simply let transport disconnect it.
                // OnTransportDisconnect will handle the rest.
                transport.Disconnect(connectionId);
            }
        }

        // network events called by TransportSystem ////////////////////////////
        // named On'Transport'Connected etc. because OnServerConnected wouldn't
        // be completely obvious that it comes from the transport
        // => BitPacker needs multiple of byte[] 4 sizes!
        NativeArray<byte> connectMessageBytes;
        void OnTransportConnected(int connectionId)
        {
            // only if server is active. ignore otherwise.
            // (in case Transport Update happens just after server Stop)
            if (state != ServerState.ACTIVE) return;
            Debug.Log($"NetworkServerSystem.OnTransportConnected: {connectionId}");

            // connection limit not reached yet?
            if (connections.Count < connectionLimit)
            {
                // connection not added yet?
                if (!connections.ContainsKey(connectionId))
                {
                    // create a new connection state
                    ConnectionState connectionState = new ConnectionState(1000);

                    // authenticate by default
                    // this may seem weird, but it's really smart way to support
                    // authenticators without NetworkServerSystem having to know
                    // about them:
                    // * Authenticators handle ConnectMessage
                    // * Immediately they set authenticated=false
                    // * Then they start authentication
                    //
                    // the alternative is to authenticate by default only if we
                    // don't have a ConnectMessage handler. but this would make
                    // too many assumptions about who hooks into ConnectMessage,
                    // and it would prevent anyone other than authenticators
                    // from hooking into ConnectMessage.
                    // we can assume that a lot projects will want to hook into
                    // ConnectMessage, so supporting authenticators by letting
                    // them overwrite authenticated state first is perfect.
                    connectionState.authenticated = true;

                    // add the connection
                    connections[connectionId] = connectionState;

                    // we only call OnConnect for classes that inherit from
                    // NetworkServerSystem, because they aren't in the
                    // ServerActiveSimulationSystemGroup and can't use
                    // OnStart/Stop Running to detect connect/disconnect.
                    OnConnected(connectionId);

                    // call OnConnect handler by invoking an artificial ConnectMessage
                    ConnectMessage message = new ConnectMessage();
                    ushort messageId = NetworkMessageMeta.GetId<ConnectMessage>();

                    if (handlers.TryGetValue(messageId, out NetworkMessageServerDelegateWrapper handler))
                    {
                        // serialize connect message
                        // NOTE: casting to INetworkWriter would allocate!
                        NetworkWriter writer = new NetworkWriter(connectMessageBytes);
                        message.Serialize(ref writer);

                        // handle it
                        handler(connectionId, new NetworkReader(writer.slice));
                        Debug.Log($"NetworkServerSystem.OnTransportConnected: invoked ConnectMessage handler for connectionId: {connectionId}");
                    }
                }
                // otherwise reject it
                else
                {
                    Debug.Log($"NetworkServerSystem: rejected connectionId: {connectionId} because already connected.");
                    transport.Disconnect(connectionId);
                }
            }
            // otherwise reject it
            else
            {
                Debug.Log($"NetworkServerSystem: rejected connectionId: {connectionId} because limit reached.");
                transport.Disconnect(connectionId);
            }
        }

        // OnConnected callback for classes that inherit from NetworkServerSystem
        protected virtual void OnConnected(int connectionId) {}

        // segment's array is only valid until returning
        //
        // client->server protocol: <<messageId, message>>
        void OnTransportData(int connectionId, NativeSlice<byte> slice)
        {
            // only if server is active. ignore otherwise.
            // (in case Transport Update happens just after server Stop)
            if (state != ServerState.ACTIVE) return;
            //Debug.Log("NetworkServerSystem.OnTransportData: " + connectionId + " => " + BitConverter.ToString(segment.Array, segment.Offset, segment.Count));

            // unpack the message id
            // NOTE: casting to INetworkReader would allocate!
            NetworkReader reader = new NetworkReader(slice);
            if (NetworkMessageMeta.UnpackMessage(ref reader, out ushort messageId))
            {
                //Debug.Log("NetworkServerSystem.OnTransportData messageId: 0x" + messageId.ToString("X4"));

                // create a new message of type messageId by copying the
                // template from the handler. we copy it automatically because
                // messages are value types, so that's a neat trick here.
                if (handlers.TryGetValue(messageId, out NetworkMessageServerDelegateWrapper handler))
                {
                    // deserialize and handle it
                    handler(connectionId, reader);
                }
                // unhandled messageIds are not okay. disconnect.
                else
                {
                    Debug.Log($"NetworkServerSystem.OnTransportData: unhandled messageId: 0x{messageId:X4} for connectionId: {connectionId}");
                    Disconnect(connectionId);
                }
            }
            // partial message ids are not okay. disconnect.
            else
            {
                Debug.Log($"NetworkServerSystem.OnTransportData: failed to unpack message for slice with length: {slice.Length} for connectionId: {connectionId}");
                Disconnect(connectionId);
            }
        }

        // => BitPacker needs multiple of byte[] 4 sizes!
        NativeArray<byte> disconnectMessageBytes;
        void OnTransportDisconnected(int connectionId)
        {
            // only if server is active. ignore otherwise.
            // (in case Transport Update happens just after server Stop)
            if (state != ServerState.ACTIVE) return;

            // only if this connection still exists
            if (!connections.TryGetValue(connectionId, out ConnectionState connection))
                return;

            // call OnDisconnected handler by invoking an artificial DisconnectMessage
            // (in case a system needs it)
            DisconnectMessage message = new DisconnectMessage();
            ushort messageId = NetworkMessageMeta.GetId<DisconnectMessage>();

            if (handlers.TryGetValue(messageId, out NetworkMessageServerDelegateWrapper handler))
            {
                // serialize disconnect message
                // NOTE: casting to INetworkRider would allocate!
                NetworkWriter writer = new NetworkWriter(disconnectMessageBytes);
                message.Serialize(ref writer);

                // handle it
                handler(connectionId, new NetworkReader(writer.slice));
                Debug.Log($"NetworkServerSystem.OnTransportDisconnected: invoked DisconnectMessage handler for connectionId: {connectionId}");
            }

            // we only call OnDisconnected for classes that inherit from
            // NetworkServerSystem, because they aren't in the
            // ServerActiveSimulationSystemGroup and can't use
            // OnStart/Stop Running to detect connect/disconnect.
            OnDisconnected(connectionId);

            // Unspawn all objects owned by that connection
            // => after DisconnectMessage handler because it might need to know
            //    about the player owned objects)
            // => before removing the connection, otherwise DestroyOwnedEntities
            //    can't look it up
            DestroyOwnedEntities(connectionId);

            // remove connection from connections
            connection.Dispose();
            connections.Remove(connectionId);
            Debug.Log($"NetworkServerSystem.OnTransportDisconnected: {connectionId}");

            // interest management rebuild to remove the connectionId from all
            // entity's observers. otherwise broadcast systems will still try to
            // send to this connectionId, which isn't a big problem, but it does
            // give an "invalid connectionId" warning message.
            // and if we have 10k monsters, we get 10k warning messages, which
            // would actually slow down/freeze the editor for a short time.
            // => AFTER removing the connection, because RebuildAll will attempt
            //    to send unspawn messages, which have a connection-still-valid
            //    check.
            // NOTE: we could also use a simple removeFromAllObservers function,
            //       which would be faster, but it's also extra code and extra
            //       complexity. DOTS is really good at rebuilding observers.
            //       AND it wouldn't even work properly, because by just
            //       removing the connectionId, the observers would never get
            //       the unspawn messages.
            //       In other words, always do a full rebuild because IT WORKS
            //       perfectly and it's fast.
            // NOTE: even if we convert broadcast systems to jobs later, it's
            //       still going to work without race conditions because
            //       whatever system ends up sending the message queue, it will
            //       have to run from main thread because of IO
            interestManagement.RebuildAll();
        }

        // OnDisconnected callback for classes that inherit from NetworkServerSystem
        protected virtual void OnDisconnected(int connectionId) {}

        // messages ////////////////////////////////////////////////////////////
        // raw send packed message.
        // this only exists because we can't use burst in Send<T> generic (yet).
        // but some expensive messages (like LocalWorldState), we want to
        // serialize with burst and then send the serialized writer.
        // => internal for now.
        // => slice needs to contain PackMessage() result.
        // TODO remove when Burst supports generic <T>
        internal void SendRaw(int connectionId, NativeSlice<byte> message, Channel channel = Channel.Reliable)
        {
            // valid connectionId?
            // Checking is technically not needed, but this way we get a nice
            // warning message and don't attempt a transport call with an
            // invalid connectionId, which is harder to debug because it simply
            // returns false in case of Apathy.
            if (connections.TryGetValue(connectionId, out ConnectionState connection))
            {
                // do nothing if the connection is broken.
                // we already logged a Send failed warning, and it will be
                // removed after the next transport update.
                // otherwise we might log 10k 'send failed' messages in-between
                // a failed send and the next transport update, which would
                // slow down the server and spam the logs.
                // -> for example, if we set the visibility radius very high in
                //    the 10k demo, DOTS will just send messages so fast that
                //    the Apathy transport buffers get full. this is to be
                //    expected, but previously the server would freeze for a few
                //    seconds because we logged thousands of "send failed"
                //    messages.
                if (!connection.broken)
                {
                    if (!transport.Send(connectionId, message, channel))
                    {
                        // send can fail if the transport has issues
                        // like full buffers, broken pipes, etc.
                        // so if Send gets called before the next
                        // transport update removes the broken
                        // connection, then we will see a warning.
                        Debug.LogWarning($"NetworkServerSystem.Send: failed to send message of size {message.Length} to connectionId: {connectionId}. This can happen if the connection is broken before the next transport update removes it. The connection has been flagged as broken and no more sends will be attempted for this connection until the next transport update cleans it up.");

                        // if Send fails only once, we will flag the
                        // connection as broken to avoid possibly
                        // logging thousands of 'Send Message failed'
                        // warnings in between the time send failed, and
                        // transport update removes the connection.
                        // it would just slow down the server
                        // significantly, and spam the logs.
                        connection.broken = true;

                        // the transport is supposed to disconnect
                        // the connection in case of send errors,
                        // but to be 100% sure we call disconnect
                        // here too.
                        // we don't want a situation where broken
                        // connections keep idling around because
                        // the transport implementation forgot to
                        // disconnect them.
                        Disconnect(connectionId);
                    }
                }
                // for debugging:
                //else Debug.Log("NetworkServerSystem.Send: skipped send to broken connectionId=" + connectionId);
            }
            else Debug.LogWarning($"NetworkServerSystem.Send: invalid connectionId={connectionId}");
        }

        // send a message to a connectionId over the specified channel.
        // (connectionId, message parameter order for consistency with transport
        //  and with Send(NativeMultiMap)
        //
        // server->client protocol: <<messageId:2, amount:4, messages:amount>>
        // (saves bandwidth, improves performance)
        public void Send<T>(int connectionId, T message, Channel channel = Channel.Reliable)
            where T : struct, NetworkMessage
        {
            // use the buffer for the given channel
            NativeArray<byte> sendBuffer = channel == Channel.Reliable ? sendBuffer_Reliable : sendBuffer_Unreliable;

            // serialize & send the message
            // need a writer to serialize this message
            // NOTE: casting to INetworkWriter would allocate!
            NetworkWriter writer = new NetworkWriter(sendBuffer);

            // pack the message with <<id, content>>
            // NOTE: burst doesn't work with generics yet unfortunately.
            if (NetworkMessageMeta.PackMessage(message, ref writer))
            {
                SendRaw(connectionId, writer.slice, channel);
            }
            else Debug.LogWarning($"NetworkServerSystem.Send: packing message of type {message.GetType()} failed. Maybe the message is bigger than serializationBuffer {sendBuffer.Length} bytes?");
        }

        // convenience function to send a whole NativeMultiMap of messages to
        // connections, useful for Job systems.
        //
        // PERFORMANCE: reusing Send(message, connectionId) is cleaner, but
        //              having the redundant code optimized to reuse writer and
        //              write messageId only once is significantly faster.
        //
        //              50k Entities @ no camera:
        //                              | NetworkTransformSystem ms | FPS
        //                reusing Send  |           6-11 ms         | 36-41 FPS
        //                optimized     |           4-8 ms          | 43-45 FPS
        //
        //              => NetworkTransformSystem runs almost twice as fast!
        //
        // DO NOT REUSE this function in Send(connectionId). this function here
        //              iterates all connections on the server. reusing would be
        //              slower.
        [Obsolete("Use Send(connectionId, Message) instead. This function might be removed soon.")]
        public void Send<T>(NativeParallelMultiHashMap<int, T> messages, Channel channel = Channel.Reliable)
            where T : unmanaged, NetworkMessage
        {
            // messages.GetKeyArray allocates.
            // -> BroadcastSystems send to each connection anyway
            // -> we need a connections.ContainsKey check anyway
            // --> so we might as well iterate all known connections and only
            //     send to the ones that are in messages (which are usually all)
            foreach (KeyValuePair<int, ConnectionState> kvp in connections)
            {
                // unroll KeyValuePair for ease of use
                int connectionId = kvp.Key;
                ConnectionState connection = kvp.Value;

                // do nothing if the connection is broken.
                // we already logged a Send failed warning, and it will be
                // removed after the next transport update.
                // otherwise we might log 10k 'send failed' messages in-between
                // a failed send and the next transport update, which would
                // slow down the server and spam the logs.
                // -> for example, if we set the visibility radius very high in
                //    the 10k demo, DOTS will just send messages so fast that
                //    the Apathy transport buffers get full. this is to be
                //    expected, but previously the server would freeze for a few
                //    seconds because we logged thousands of "send failed"
                //    messages.
                if (!connection.broken)
                {
                    // send all messages for this connectionId
                    NativeParallelMultiHashMapIterator<int>? it = default;
                    while (messages.TryIterate(connectionId, out T message, ref it))
                        Send(connectionId, message, channel);
                }
                // for debugging:
                //else Debug.Log("NetworkServerSystem.Send: skipped send to broken connectionId=" + connectionId);
            }
        }

        // send messages to a connectionId.
        // => we send messages in MaxMessageSize chunks with <<messageId, amount, messages>
        // => DOTSNET automatically chunks them so Transports don't need to.
        //
        // benefits:
        // + save lots of bandwidth by only sending amount once
        [Obsolete("Use Send(connectionId, Message) instead. This function might be removed soon.")]
        public void Send<T>(int connectionId, NativeList<T> messages, Channel channel = Channel.Reliable)
            where T : unmanaged, NetworkMessage
        {
            // do nothing if messages are empty. we don't want Transports to try
            // and send empty buffers.
            if (messages.Length == 0)
                return;

            // valid connectionId?
            // Checking is technically not needed, but this way we get a nice
            // warning message and don't attempt a transport call with an
            // invalid connectionId, which is harder to debug because it simply
            // returns false in case of Apathy.
            if (connections.TryGetValue(connectionId, out ConnectionState connection))
            {
                // do nothing if the connection is broken.
                // we already logged a Send failed warning, and it will be
                // removed after the next transport update.
                // otherwise we might log 10k 'send failed' messages in-between
                // a failed send and the next transport update, which would
                // slow down the server and spam the logs.
                // -> for example, if we set the visibility radius very high in
                //    the 10k demo, DOTS will just send messages so fast that
                //    the Apathy transport buffers get full. this is to be
                //    expected, but previously the server would freeze for a few
                //    seconds because we logged thousands of "send failed"
                //    messages.
                if (!connection.broken)
                {
                    // send each message
                    foreach (T message in messages)
                        Send(connectionId, message, channel);
                }
                // for debugging:
                //else Debug.Log("NetworkServerSystem.Send: skipped send to broken connectionId=" + connectionId);
            }
            else Debug.LogWarning($"NetworkServerSystem.Send: invalid connectionId={connectionId}");
        }

        // we need to check authentication before calling handlers.
        // there are two options:
        // a) store 'requiresAuth' in the dictionary and check it before calling
        //    the handler each time.
        //    this works but we could accidentally forget checking requiresAuth.
        // b) wrap the handler in an requiresAuth check.
        //    this way we can never forget to call it.
        //    and we could add more code to the wrapper like message statistics.
        // => this is a bit of higher order function magic, but it's a really
        //    elegant solution.
        // => note that we lose the ability to compare handlers because we wrap
        //    them, but that's fine.
        NetworkMessageServerDelegateWrapper WrapHandler<T>(NetworkMessageServerDelegate<T> handler, bool requiresAuthentication, Func<T> messageAllocator, NetworkMessageDeserializerDelegate<T> messageDeserializer)
            where T : struct, NetworkMessage
        {
            return delegate(int connectionId, NetworkReader reader)
            {
                // find connection state
                if (connections.TryGetValue(connectionId, out ConnectionState state))
                {
                    // check authentication
                    // -> either we don't need it
                    // -> or if we need it, connection needs to be authenticated
                    if (!requiresAuthentication || state.authenticated)
                    {
                        // deserialize
                        // -> we do this in WrapHandler because in here we still
                        //    know <T>
                        // -> later on we only know NetworkMessage
                        // -> knowing <T> allows for automated serialization
                        //    (which is not used at the moment anymore)
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
                            handler(connectionId, message);
                        }
                        // invalid message contents are not okay. disconnect.
                        else
                        {
                            Debug.Log($"NetworkServerSystem: failed to deserialize {typeof(T)} for reader with Position: {reader.Position} Remaining: {reader.Remaining} for connectionId: {connectionId}");
                            Disconnect(connectionId);
                        }
                    }
                    // authentication was required, but we were not authenticated
                    // in this case always disconnect the connection.
                    // no one is allowed to send unauthenticated messages.
                    else
                    {
                        Debug.Log($"NetworkServerSystem: connectionId: {connectionId} disconnected because of unauthorized message.");
                        Disconnect(connectionId);
                    }
                }
                // this should not happen. if we try to call a handler for a
                // invalid connection then something went wrong.
                else
                {
                    Debug.LogError($"NetworkServerSystem: connectionId: {connectionId} not found in WrapHandler. This should never happen.");
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
        public bool RegisterHandler<T>(NetworkMessageServerDelegate<T> handler, bool requiresAuthentication, Func<T> messageAllocator = null, NetworkMessageDeserializerDelegate<T> messageDeserializer = null)
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
                // wrap the handler with auth check & deserialization
                handlers[messageId] = WrapHandler(handler, requiresAuthentication, messageAllocator, messageDeserializer);
                return true;
            }

            // log warning in case we tried to overwrite. could be extremely
            // useful for debugging/development, so we notice right away that
            // a system accidentally called it twice, or that two messages
            // accidentally have the same messageId.
            Debug.LogWarning($"NetworkServerSystem: handler for {typeof(T)} was already registered.");
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
        // Spawn spawns an instantiated NetworkIdentity prefab on all clients.
        // Always do it in this order:
        //   1. PrefabSystem.Get(prefabId)
        //   2. EntityManager.Instantiate(prefab)
        //   3. Set position and other custom data for a player/monster/etc.
        //   4. Spawn(prefab)
        //      -> sets netId
        //      -> sets ownerConnection
        //      -> etc.
        //
        // note: we pass an already instantiated NetworkIdentity prefab instead of
        //       passing a prefabId and instantiating it in here.
        //       this is important because the caller might initialize a
        //       player's position, inventory, etc. before sending the spawn
        //       message to all clients.
        //       otherwise we would         spawn->modify->state update
        //       this way we        modify->spawn  without state update
        public bool Spawn(Entity entity, int? ownerConnectionId)
        {
            // only if server is active
            if (state != ServerState.ACTIVE)
                return false;

            // does it have a NetworkIdentity component?
            if (HasComponent<NetworkIdentity>(entity))
            {
                // was it not spawned yet? we can't spawn a monster/player twice
                NetworkIdentity identity = GetComponent<NetworkIdentity>(entity);
                if (identity.netId == 0)
                {
                    // set netId to Entity's unique id (Index + Version)
                    // there is no reason to generate a new unique id if we already
                    // have one. this is way easier to debug as well.
                    // -> on server, netId is simply the entity.UniqueId()
                    // -> on client, it's a unique Id from the server
                    identity.netId = entity.UniqueId();

                    // set the owner connectionId
                    identity.connectionId = ownerConnectionId;

                    // apply component changes
                    SetComponent(entity, identity);

                    // if the Entity is owned by a connection, then add it to
                    // the connection's owned objects
                    if (ownerConnectionId != null)
                    {
                        ownedPerConnection.Add(ownerConnectionId.Value, entity);

                        // set connection's main player entity if not set yet.
                        // set for first spawned object.
                        // => setting it in JoinWorld would make tests harder.
                        ConnectionState connection = connections[ownerConnectionId.Value];
                        if (!connection.mainPlayer.HasValue)
                            connection.mainPlayer = entity;
                    }

                    // note: we don't rebuild observers after an Entity spawned.
                    //       this would cause INSANE complexity.
                    //       the next rebuild will detect new observers anyway.
                    //       (see InterestManagementSystem.RebuildAll comments)

                    // add to spawned
                    spawned[identity.netId] = entity;

                    // success
                    //Debug.Log("NetworkServerSystem: Spawned Entity=" + EntityManager.GetName(entity) + " connectionId=" + ownerConnectionId);
                    return true;
                }
#if UNITY_EDITOR
                Debug.LogWarning($"NetworkServerSystem.Spawn: can't spawn Entity={EntityManager.GetName(entity)} prefabId={Conversion.Bytes16ToGuid(identity.prefabId)} connectionId={ownerConnectionId} because the Entity was already spawned before with netId={identity.netId}");
#else
                Debug.LogWarning($"NetworkServerSystem.Spawn: can't spawn Entity={                      entity } prefabId={Conversion.Bytes16ToGuid(identity.prefabId)} connectionId={ownerConnectionId} because the Entity was already spawned before with netId={identity.netId}");
#endif
                return false;
            }
#if UNITY_EDITOR
            Debug.LogWarning($"NetworkServerSystem.Spawn: can't spawn Entity={EntityManager.GetName(entity)} connectionId={ownerConnectionId} because the Entity has no NetworkIdentity component.");
#else
            Debug.LogWarning($"NetworkServerSystem.Spawn: can't spawn Entity={                      entity } connectionId={ownerConnectionId} because the Entity has no NetworkIdentity component.");
#endif
            return false;
        }

        // Unspawn should be used for all player owned objects after a player
        // disconnects, or after a monster dies, etc.
        // It broadcasts the despawn event to all connections so they remove it.
        //
        // IMPORTANT: Unspawn does NOT destroy the entity on the server.
        public bool Unspawn(Entity entity)
        {
            // only if server is active
            if (state != ServerState.ACTIVE)
                return false;

            // does it have a NetworkIdentity component?
            if (HasComponent<NetworkIdentity>(entity))
            {
                // was it spawned at all? we can't despawn an unspawned monster
                NetworkIdentity identity = GetComponent<NetworkIdentity>(entity);
                if (identity.netId != 0)
                {
                    // remove it from spawned by netId first, before we clear
                    // the netId
                    // -> checking the result of Remove is valuable to detect
                    //    unexpected cases.
                    if (spawned.Remove(identity.netId))
                    {
                        // unspawning (& destroying) will fully remove the
                        // entity from the world. the observer system will never
                        // see it again, and never send any unspawn messages
                        // for this entity to all of the observers.
                        // we need to do it manually here.
                        DynamicBuffer<NetworkObserver> observers = GetBuffer<NetworkObserver>(entity);
                        for (int i = 0; i < observers.Length; ++i)
                        {
                            int observerConnectionId = observers[i];
#if UNITY_EDITOR
                            //Debug.Log("Unspawning " + EntityManager.GetName(entity) + " for observerConnectionId=" + observerConnectionId);
#else
                            //Debug.Log("Unspawning " +                       entity  + " for observerConnectionId=" + observerConnectionId);
#endif

                        }

                        // note: we don't rebuild observers after an Entity
                        //       unspawned. this caused INSANE complexity.
                        //       the next rebuild will detect old observers anyway.
                        //       (see InterestManagementSystem.RebuildAll comments)

                        // clear netId because it's not spawned anymore
                        identity.netId = 0;

                        // owned by a connection?
                        if (identity.connectionId != null)
                        {
                            // set connection's main player entity if not set yet.
                            // set for first spawned object.
                            // => setting it in JoinWorld would make tests harder.
                            ConnectionState connection = connections[identity.connectionId.Value];
                            if (connection.mainPlayer == entity)
                                connection.mainPlayer = null;

                            // then remove it from the connection's owned objects
                            ownedPerConnection.Remove(identity.connectionId.Value, entity);
                        }

                        // clear owner
                        identity.connectionId = null;

                        // apply component changes
                        SetComponent(entity, identity);

                        // success
                        //Debug.Log("NetworkServerSystem: Unspawned Entity=" + EntityManager.GetName(entity));
                        return true;
                    }
#if UNITY_EDITOR
                    Debug.LogWarning($"NetworkServerSystem.Unspawn: can't despawn Entity={EntityManager.GetName(entity)} prefabId={Conversion.Bytes16ToGuid(identity.prefabId)} because an Entity with netId={identity.netId} was not spawned.");
#else
                    Debug.LogWarning($"NetworkServerSystem.Unspawn: can't despawn Entity={                      entity } prefabId={Conversion.Bytes16ToGuid(identity.prefabId)} because an Entity with netId={identity.netId} was not spawned.");
#endif
                    return false;
                }
#if UNITY_EDITOR
                Debug.LogWarning($"NetworkServerSystem.Unspawn: can't despawn Entity={EntityManager.GetName(entity)} prefabId={Conversion.Bytes16ToGuid(identity.prefabId)} because netId is 0, so it was never spawned.");
#else
                Debug.LogWarning($"NetworkServerSystem.Unspawn: can't despawn Entity={                      entity } prefabId={Conversion.Bytes16ToGuid(identity.prefabId)} because netId is 0, so it was never spawned.");
#endif
                return false;
            }
#if UNITY_EDITOR
            Debug.LogWarning($"NetworkServerSystem.Unspawn: can't despawn Entity={EntityManager.GetName(entity)} because the Entity has no NetworkIdentity component.");
#else
            Debug.LogWarning($"NetworkServerSystem.Unspawn: can't despawn Entity={                      entity } because the Entity has no NetworkIdentity component.");
#endif
            return false;
        }

        // Destroy helper function that calls Unspawn, and then DestroyEntity.
        // in most (if not all) cases, we want to both unspawn and destroy.
        public void Destroy(Entity entity)
        {
            Unspawn(entity);
            EntityManager.DestroyEntity(entity);
        }

        // spawn all scene prefabs once.
        // we want them to be in the scene on start, which is why they were in
        // the MonoBehaviour scene/hierarchy.
        // -> PrefabSystem converts all scene entities to Prefabs when it starts
        // -> Server spawns instances of them when it starts
        //    -> this way we can still destroy/instantiate them again
        //    -> this way we make sure to assign a netId to them
        // note: we could make this the responsibility of the PrefabSystem in
        //       server world. but right now PrefabSystem is it's own standalone
        //       thing that doesn't know anything about the server. and that's
        //       good.
        void SpawnSceneObjects()
        {
            foreach (KeyValuePair<FixedBytes16, Entity> kvp in prefabSystem.scenePrefabs)
            {
                // instantiate
                Entity instance = EntityManager.Instantiate(kvp.Value);

                // spawn, but without rebuilding observers because there are
                // none yet.
                // (otherwise we would call RebuildObservers 10k if we have 10k
                //  scene objects)
                if (!Spawn(instance, null))
                {
#if UNITY_EDITOR
                    Debug.LogError($"NetworkServerSystem.SpawnSceneObjects: failed to spawn scene object: {EntityManager.GetName(kvp.Value)} with prefabId={Conversion.Bytes16ToGuid(kvp.Key)}");
#else
                    Debug.LogError($"NetworkServerSystem.SpawnSceneObjects: failed to spawn scene object: {kvp.Value} with prefabId={Conversion.Bytes16ToGuid(kvp.Key)}");
#endif
                }
            }
        }

        // destroy all entities for a connectionId
        // public in case someone needs it (e.g. GM kicks) and for testing
        public void DestroyOwnedEntities(int connectionId)
        {
            // Unspawn all objects owned by that connection
            // (after DisconnectMessage handler because it might need to know
            //  about the player owned objects)

            // unspawn(so clients know about it) & destroy each entity
            // note: we need to duplicate the list for iteration, because
            //       Unspawn modifies it and would cause an exception while
            //       iterating. duplicating is the best solution because:
            //       * using a List means we could iterate backwards and
            //         remove from it at the same time, but then .Remove
            //         would be more costly than in a HashSet
            //       * passing a 'dontTouchOwnedEntities' to Unspawn would
            //         work and it would avoid allocations, but with the
            //         cost of extra complexity and a less elegant API
            //         because all of the sudden, there would be a strange
            //         parameter in Destroy/Unspawn.
            //       => players don't disconnect very often. it's okay to
            //          allocate here once.
            NativeParallelMultiHashMapIterator<int>? iterator = default;
            while (ownedPerConnection.TryIterate(connectionId, out Entity entity, ref iterator))
            {
                Destroy(entity);
            }

            // note: no need to clear connection.ownedEntities because
            //       Unspawn already removes them from the list
        }

        // destroy all NetworkEntities to clean up
        void DestroyAllNetworkEntities()
        {
            // IMPORTANT: EntityManager.DestroyEntity(query) does NOT work for
            //            Entities with LinkedEntityGroup. We would get an error
            //            about LinkedEntities needing to be destroyed first:
            //            https://github.com/vis2k/DOTSNET/issues/11
            //
            //            The solution is to use Destroy(NativeArray), which
            //            destroys linked entities too:
            //            https://forum.unity.com/threads/how-to-destroy-all-linked-entities.714890/#post-4777796
            //
            //            See NetworkServerSystemTests:
            //            StopDestroysAllNetworkEntitiesWithLinkedEntityGroup()
            EntityQuery identities = GetEntityQuery(typeof(NetworkIdentity));
            NativeArray<Entity> array = identities.ToEntityArray(Allocator.TempJob);
            EntityManager.DestroyEntity(array);
            array.Dispose();
        }

        // join world //////////////////////////////////////////////////////////
        // the Join World process:
        //
        // -----Game Specific-----
        //   - Client selects a player
        //     - Client sends JoinWorld(playerPrefabId)
        //   - Server JoinWorldSystem validates selection
        //     - instantiates prefab
        //     - loads position, inventory
        // --------DOTSNET---------
        //   - NetworkServerSystem.JoinWorld(player)
        //     - marks connection as joined
        //     - Spawns player
        //
        // note: unlike Spawn, we pass connectionId first because that's what
        //       matters the most this time.
        // note: we do not handle spawn messages here. this is game specific and
        //       some might need a prefab index, a team, a map, etc.
        //       simply handle it in your game and call JoinWorld afterwards.
        public bool JoinWorld(int connectionId, Entity player)
        {
            // spawn the player on all clients, owned by the connection
            if (Spawn(player, connectionId))
            {
                // mark connection as 'joined world'. some systems might need it
                connections[connectionId].joinedWorld = true;

                // note: Spawn() rebuilds observers so everyone else knows about
                //       the new player, and the new player knows about everyone
                //       else. we don't need to do anything else here.
                return true;
            }
            else
            {
#if UNITY_EDITOR
                Debug.LogWarning($"NetworkServerSystem.JoinWorld: failed to spawn player: {EntityManager.GetName(player)} for connectionId: {connectionId}");
#else
                Debug.LogWarning($"NetworkServerSystem.JoinWorld: failed to spawn player: {                      player } for connectionId: {connectionId}");
#endif
            }
            return false;
        }

        // helper function to get a connection's main player position,
        // aka the connection's first owned object's position.
        public bool GetMainPlayerPosition(int connectionId, out float3 position)
        {
            position = float3.zero;

            // IMPORTANT: NativeMultiMap .ownedPerConnection order isn't
            //            guaranteed so we don't know which is the first owned.
            //            => need to look up via MainPlayer
            // TODO could be bursted if we have an EntityQuery<Translation>!
            if (connections.TryGetValue(connectionId, out ConnectionState connection))
            {
                if (connection.mainPlayer.HasValue)
                {
                    position = GetComponent<Translation>(connection.mainPlayer.Value).Value;
                    return true;
                }
            }
            return false;
        }

        // delta snapshots /////////////////////////////////////////////////////
        // build EntityStates multimap from all spawned entities.
        // internal for testing
        internal void BuildEntityStates(NativeParallelMultiHashMap<int, EntityState> entityStates)
        {
            // PUSH-BROADCASTING makes sense in ECS+Burst!
            //
            // ForEach Entity: gather serialization for each observer.
            // => .ForEach + burst is faster than 'pull' broadcasting where we
            //    pull them in for each connection
            // => in the end we get the multimap<connId, entityStates> anyway
            Entities.ForEach((DynamicBuffer<NetworkObserver> observers,
                              in Translation translation,
                              in Rotation rotation,
                              in NetworkIdentity identity,
                              in NetworkComponentsSerialization serialization) =>
            {
                if (observers.Length > 0)
                {
                    // send to each observer
                    //
                    // SyncDirection:
                    //   SERVER_TO_CLIENT: always sync
                    //   CLIENT_TO_SERVER: sync to everyone except owner
                    // -> we simply drop 'CLIENT_TO_SERVER && owner' on the client
                    // -> makes the code so much easier since we don't need custom
                    //    serialization for owner here
                    for (int i = 0; i < observers.Length; ++i)
                    {
                        // create EntityState message
                        // (server ALWAYS syncs transform, see SyncDirection
                        // comments below)
                        bool owned = identity.connectionId == observers[i].connectionId;
                        EntityState entityState = new EntityState
                        (
                            identity.prefabId,
                            identity.netId,
                            owned,
                            translation.Value,
                            rotation.Value,
                            serialization.writer
                        );

                        // add to messages. send later.
                        //Debug.LogWarning($"Sending snapshot for netId={message.netId} with pos={message.position} rot={message.rotation.value} to {observers[i].connectionId}");
                        entityStates.Add(observers[i].connectionId, entityState);
                    }
                }
            })
            .Run();
        }

        // 'ref message' so this function doesn't need to know about the caching
        // and for easier testing.
        // => maxBits defines mac LocalWorldStateMessage size in bits
        //    as parameter because this function should not worry about how to
        //    calculate maxBits
        // => reuses 'reusableMessage' and returns new value
        internal static LocalWorldStateMessage FitSortedEntityStatesIntoLocalWorldState(NativeList<EntityState> list, LocalWorldStateMessage reusableMessage)
        {
            // NOTE: Job only adds to message.entities which is fine.
            //       it might not save modifications in the struct itself though?
            for (int i = 0; i < list.Length; ++i)
            {
                // try to fit this entity into the message.
                // stop otherwise. no need to keep trying.
                if (!reusableMessage.TryAddEntity(list[i]))
                    break;
            }

            return reusableMessage;
        }

        // broadcast the MultiMap<connectionId, EntityStates> via LocalWorldState
        void BroadcastEntityStates()
        {
            // capture local variables for burst.
            // only once, then reuse for each connectionId (because it allocs).
            NativeParallelMultiHashMap<int, EntityState> _broadcastEntityStates = broadcastEntityStates;
            NativeList<EntityState> _sortedEntityStates = sortedEntityStates;
            LocalWorldStateMessage _message = localWorldStateMessage;
            ushort messageId = NetworkMessageMeta.GetId<LocalWorldStateMessage>();

            // for each connection, merge them into a LocalWorldState message
            // TODO build LocalWorldStates in parallel later for CCU scaling!
            foreach (KeyValuePair<int, ConnectionState> kvp in connections)
            {
                int connectionId = kvp.Key;
                ConnectionState connection = kvp.Value;
                NativeParallelHashMap<ulong, EntityState> connectionLastEntities = connection.lastEntities;

                // pack message manually so we can serialize with burst
                NetworkWriter writer = new NetworkWriter(sendBuffer_Reliable);

                // NOTE: do NOT check if 'joinedWorld'.
                // if a player despawned, we still need to send an empty
                // LocalWorldMessage to indicate that all entities not in the
                // message need to be despawned.

                // get the connection's main player's position for sorting
                // => connection might not have a main player if we are about to
                //    despawn. which is fine, then default to zero.
                GetMainPlayerPosition(connectionId, out float3 origin);

                // BURST ALL THE STEPS!
                Job.WithCode(() => {
                    // reset message first
                    _message.Reset();

                    // copy this connection's last sent entities into the message
                    // for delta compression.
                    connectionLastEntities.CopyTo(_message.lastEntities);

                    // add all entity states for this connectionId to sorted list
                    // (clears previous results)
                    // VERIFIED WITH TESTS!
                    _broadcastEntityStates.CopyValuesForKey(connectionId, _sortedEntityStates);

                    // sort the list by this connection's main player position
                    // use NativeSortedList if Unity ever makes one.
                    // TODO make sorter modifiable later.
                    //      casting to abstract IComparer allocates though.
                    // VERIFIED WITH TESTS!
                    _sortedEntityStates.Sort(new EntityStateSorter(origin));

                    // fit as many as possible into a LocalWorldState message
                    // IMPORTANT: reuse LocalWorldState because it allocates a list.
                    // VERIFIED WITH TESTS!
                    _message = FitSortedEntityStatesIntoLocalWorldState(_sortedEntityStates, _message);

                    // serializing a large LocalWorldState message is costly.
                    // it's the BIGGEST OPERATION in profiler by far.
                    // Send<T> can't use burst. so we have to:
                    // => Pack & serialize manually
                    // => SendRaw
                    //
                    // with LocalWorldState containing the vast majority of
                    // serializations, using BURST here is huge!
                    NetworkMessageMeta.PackMessageHeader(messageId, ref writer);
                    _message.Serialize(ref writer);

                    // lastEntities := entities for this connection
                    connectionLastEntities.Clear();
                    _message.entities.CopyTo(connectionLastEntities);
                })
                .Run();

                // raw send the burst serialized message
                SendRaw(connectionId, writer.slice);
            }
        }

        void Broadcast()
        {
            // NOTE: InterestManagement builds the .observers before this.
            // -> stateless brute force check from here via AOI.CanSee() works
            //    too but it makes sense to encapsulate the logic into AoISystem
            // -> having .observers should be useful for Rpcs too

            // tell serializers to serialize all NetworkComponents once
            serializers.SerializeAll();

            // build multimap<connId, EntityStates>
            BuildEntityStates(broadcastEntityStates);

            // broadcast them
            BroadcastEntityStates();

            // clear states
            broadcastEntityStates.Clear();
        }

        // component system ////////////////////////////////////////////////////
        // cache TransportSystem in OnStartRunning after all systems were created
        // (we can't assume that TransportSystem.OnCreate is created before this)
        protected override void OnStartRunning()
        {
            // make sure we don't hook ourselves up to transport events twice
            if (transport != null)
            {
                Debug.LogError("NetworkServerSystem: transport was already configured before!");
                return;
            }

            // initialize native collections with reasonable default capacities
            spawned = new NativeParallelHashMap<ulong, Entity>(1000, Allocator.Persistent);
            broadcastEntityStates = new NativeParallelMultiHashMap<int, EntityState>(1000, Allocator.Persistent);
            sortedEntityStates = new NativeList<EntityState>(1000, Allocator.Persistent);
            ownedPerConnection = new NativeParallelMultiHashMap<int, Entity>(1000, Allocator.Persistent);

            // find available server transport
            transport = TransportSystem.FindAvailable(World) as TransportServerSystem;
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

                // for reuse
                int maxReliable = transport.GetMaxPacketSize(Channel.Reliable);
                int maxUnreliable = transport.GetMaxPacketSize(Channel.Reliable);

                // initialize buffers
                connectMessageBytes = new NativeArray<byte>(4, Allocator.Persistent);
                disconnectMessageBytes = new NativeArray<byte>(4, Allocator.Persistent);
                sendBuffer_Reliable = new NativeArray<byte>(maxReliable, Allocator.Persistent);
                sendBuffer_Unreliable = new NativeArray<byte>(maxUnreliable, Allocator.Persistent);

                // calculate tick rate
                // CatchUp would be bad idea because it could lead to deadlocks
                // under heavy load, where only the simulation system is updated
                // so we use Simple.
                float tickInterval = 1 / tickRate;

                // local world state message can fill up to transport.max - msg id
                // or broadcastMax if lower
                int localWorldMax = math.min(maxReliable - NetworkMessageMeta.IdSize, broadcastMaxSize);
                localWorldStateMessage = new LocalWorldStateMessage(localWorldMax, 1000, Allocator.Persistent);

                // do we have a simulation system group? then set tick rate.
                // (we won't have a group when running tests)
                ServerActiveSimulationSystemGroup group = World.GetExistingSystem<ServerActiveSimulationSystemGroup>();
                if (group != null)
                {
                    // TODO use EnableFixedRateSimple after it was fixed. right now
                    // it only sets deltaTime but updates with same max frequency
                    group.RateManager = new RateUtils.FixedRateCatchUpManager(tickInterval);
                    Debug.Log("NetworkServerSystem: " + group + " tick rate set to: " + tickRate);
                }

                // do we have a LATE simulation system group? then set tick rate.
                // (we won't have a group when running tests)
                ServerActiveLateSimulationSystemGroup lateGroup = World.GetExistingSystem<ServerActiveLateSimulationSystemGroup>();
                if (lateGroup != null)
                {
                    // TODO use EnableFixedRateSimple after it was fixed. right now
                    // it only sets deltaTime but updates with same max frequency
                    lateGroup.RateManager = new RateUtils.FixedRateCatchUpManager(tickInterval);
                    Debug.Log("NetworkServerSystem: " + lateGroup + " tick rate set to: " + tickRate);
                }
            }
            else Debug.LogError($"NetworkServerSystem: no available TransportServerSystem found on this platform: {Application.platform}");
        }

        protected override void OnUpdate()
        {
            // auto start in headless mode
            // IMPORTANT: start in OnUpdate after ALL system's OnStartRunning()
            //            was called and all other systems were set up properly.
            //            => headless start in OnStartRunning would happen
            //               before Transport.OnStartRunning configuration
            //               otherwise.
            if (startIfHeadless && isHeadless && !headlessStarted)
            {
                Debug.Log("NetworkServerSystem: automatically starting in headless mode...");
                StartServer();
                headlessStarted = true;
            }

            // only update world while active
            if (state == ServerState.ACTIVE)
            {
                // broadcast every interval
                if (Time.ElapsedTime >= lastSnapshotTime + broadcastInterval)
                {
                    Broadcast();
                    lastSnapshotTime = Time.ElapsedTime;
                }
            }

            // always update transport after everything else was updated
            transport.LateUpdate();
        }

        protected override void OnDestroy()
        {
            // stop server in case it was running
            StopServer();

            // remove the hooks that we have setup in OnStartRunning.
            // note that StopServer will fire hooks again. so we only remove
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
            if (connectMessageBytes.IsCreated)
                connectMessageBytes.Dispose();
            if (disconnectMessageBytes.IsCreated)
                disconnectMessageBytes.Dispose();
            if (broadcastEntityStates.IsCreated)
                broadcastEntityStates.Dispose();
            if (sortedEntityStates.IsCreated)
                sortedEntityStates.Dispose();
            if (ownedPerConnection.IsCreated)
                ownedPerConnection.Dispose();
            if (sendBuffer_Reliable.IsCreated)
                sendBuffer_Reliable.Dispose();
            if (sendBuffer_Unreliable.IsCreated)
                sendBuffer_Unreliable.Dispose();
            localWorldStateMessage.Dispose();
        }
    }
}
