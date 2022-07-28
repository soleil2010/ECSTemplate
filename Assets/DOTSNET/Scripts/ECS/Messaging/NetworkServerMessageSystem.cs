﻿// helper class to inherit from for message processing.
// * .server access for ease of use
// * [ServerWorld] tag already specified
// * RegisterMessage + Handler already set up
using Unity.Entities;
using UnityEngine;

namespace DOTSNET
{
    [ServerWorld]
    [UpdateInGroup(typeof(ServerActiveSimulationSystemGroup))]
    public abstract partial class NetworkServerMessageSystem<T> : SystemBase
        where T : struct, NetworkMessage
    {
        // dependencies
        [AutoAssign] protected NetworkServerSystem server;

        // overwrite to indicate if the message should require authentication
        protected abstract bool RequiresAuthentication();

        // the handler function
        protected abstract void OnMessage(int connectionId, T message);

        // default allocator to create new message <T> before deserializing.
        // can be overwritten to reuse large messages that would allocate like
        // WorldState.
        protected virtual T MessageAllocator() =>
            NetworkMessageMeta.DefaultMessageAllocator<T>();

        // default deserializer to deserialize message <T>.
        // burst doesn't support generics (yet).
        // inheriting systems can overwrite to explicitly deserialize with burst
        // before OnMessage(T) is called.
        // TODO remove this when burst supports generic <T>
        protected virtual bool MessageDeserializer(ref T message, ref NetworkReader reader) =>
            NetworkMessageMeta.DefaultMessageDeserializer(ref message, ref reader);

        // messages NEED to be registered in OnCreate.
        // we are in the ActiveSimulationSystemGroup, so OnStartRunning would
        // only be called after connecting, at which point we might already have
        // received a message of type T before setting up the handler.
        // (not using ActiveGroup wouldn't be ideal. we don't want to do any
        //  message processing unless connected.)
        protected override void OnCreate()
        {
            // register handler
            if (!server.RegisterHandler<T>(OnMessage, RequiresAuthentication(), MessageAllocator, MessageDeserializer))
                Debug.LogError($"NetworkServerMessageSystem: failed to register handler for: {typeof(T)}. Was a handler for that message type already registered?");
        }

        // OnDestroy unregisters the message
        // Otherwise OnCreate can't register it again without an error,
        // and we really do want to have a legitimate error there in case
        // someone accidentally registers two handlers for one message.
        protected override void OnDestroy()
        {
            server.UnregisterHandler<T>();
        }
    }
}