// helper class to inherit from for message processing.
// * .client access for ease of use
// * [ClientWorld] tag already specified
// * RegisterMessage + Handler already set up
using Unity.Entities;
using UnityEngine;

namespace DOTSNET
{
    [ClientWorld]
    [UpdateInGroup(typeof(ClientConnectedSimulationSystemGroup))]
    public abstract partial class NetworkClientMessageSystem<T> : SystemBase
        where T : struct, NetworkMessage
    {
        // dependencies
        [AutoAssign] protected NetworkClientSystem client;

        // the handler function
        protected abstract void OnMessage(T message);

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
        // we are in the ConnectedSimulationSystemGroup, so OnStartRunning would
        // only be called after connecting, at which point we might already have
        // received a message of type T before setting up the handler.
        // (not using ConnectedGroup wouldn't be ideal. we don't want to do any
        //  message processing unless connected.)
        protected override void OnCreate()
        {
            // register handler
            if (!client.RegisterHandler<T>(OnMessage, MessageAllocator, MessageDeserializer))
                Debug.LogError($"NetworkClientMessageSystem: failed to register handler for: {typeof(T)}. Was a handler for that message type already registered?");
        }

        // OnDestroy unregisters the message
        // Otherwise OnCreate can't register it again without an error,
        // and we really do want to have a legitimate error there in case
        // someone accidentally registers two handlers for one message.
        protected override void OnDestroy()
        {
            client.UnregisterHandler<T>();
        }
    }
}