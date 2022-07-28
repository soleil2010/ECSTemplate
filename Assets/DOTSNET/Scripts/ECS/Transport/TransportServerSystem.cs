// Base class for all server transports.
using System;
using Unity.Collections;
using Unity.Entities;

namespace DOTSNET
{
    [ServerWorld]
    // TransportServerSystem should be updated AFTER all other server systems.
    // we need a guaranteed update order to avoid race conditions where it might
    // randomly be updated before other systems, causing all kinds of unexpected
    // effects. determinism is always a good idea!
    //
    // Note: we update AFTER everything else, not before. This way systems like
    //       NetworkServerSystem can apply configurations in OnStartRunning, and
    //       Transport OnStartRunning is called afterwards, not before. Other-
    //       wise the OnData etc. events wouldn't be hooked up.
    //
    // * [UpdateAfter(NetworkServerSystem)] won't work because in some cases
    //   like Pong, we inherit from NetworkServerSystem and the UpdateAfter tag
    //   won't find the inheriting class. see also:
    //   https://forum.unity.com/threads/updateafter-abstractsystemtype-wont-update-after-inheritingsystemtype-abstractsystemtype.915170/
    // * [UpdateInGroup(ApplyPhysicsGroup), OrderLast=true] would be fine, but
    //   it doesn't actually update last for some reason. could be our custom
    //   Bootstrap, or a Unity bug.
    //
    // Update BEFORE server active group for EarlyUpdate!
    // NOT IN server active group. need to update transport to start server.
    [UpdateBefore(typeof(ServerActiveSimulationSystemGroup))]
    public abstract class TransportServerSystem : TransportSystem
    {
        // events //////////////////////////////////////////////////////////////
        // NetworkServerSystem should hook into this to receive events.
        // Fallback/Multiplex transports could also hook/route those as needed.
        // => Data ArraySegments are only valid until next call, so process the
        //    events immediately!
        // => We don't call NetworkServerSystem.OnTransportConnected etc.
        //    directly. This way we have less dependencies, and it's easier to
        //    test!
        // IMPORTANT: call them from main thread!
        public Action<int> OnConnected;
        public Action<int, NativeSlice<byte>> OnData;
        public Action<int> OnDisconnected;
        // send event for statistics etc.
        public Action<int, NativeSlice<byte>> OnSend;

        // abstracts ///////////////////////////////////////////////////////////
        // check if server is running
        public abstract bool IsActive();

        // start listening
        public abstract void Start();

        // send ArraySegment to the client with connectionId
        // note: DOTSNET already packs messages. Transports don't need to.
        public abstract bool Send(int connectionId, NativeSlice<byte> slice, Channel channel);

        // disconnect one client from the server
        public abstract void Disconnect(int connectionId);

        // get a connection's IP address
        public abstract string GetAddress(int connectionId);

        // stop the server
        public abstract void Stop();
    }
}