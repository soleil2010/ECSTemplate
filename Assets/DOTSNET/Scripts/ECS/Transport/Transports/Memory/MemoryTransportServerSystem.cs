// MemoryTransport is useful for:
// * Unit tests
// * Benchmarks where DOTS isn't limited by socket throughput
// * WebGL demos
// * Single player mode
// * etc.
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;

namespace DOTSNET.MemoryTransport
{
    [ServerWorld]
    // use SelectiveSystemAuthoring to create it selectively
    [DisableAutoCreation]
    public class MemoryTransportServerSystem : TransportServerSystem
    {
        // can't use [AutoAssign] because clientTransport is in another world
        public MemoryTransportClientSystem clientTransport;

        bool active;
        internal Queue<Message> incoming = new Queue<Message>();

        public override bool Available() => true;
        // 64KB is a reasonable max packet size. we don't want to allocate
        // int max = 2GB for buffers each time.
        // unit tests would take forever, pools would allocate so much.
        public override int GetMaxPacketSize(Channel _) => ushort.MaxValue;
        public override bool IsActive() => active;

        // IMPORTANT: use Start() to set everything up.
        //            - authoring configuration is only applied after OnCreate()
        //            - Early/LateUpdate will need to be in ServerActiveGroup,
        //              so OnStartRunning() would be called way after
        //              Server.Start() calls Transport.Start()
        public override void Start()
        {
            // sometimes we use MemoryTransport in host mode for bench marks etc
            // so in that case, find server transport if the client world exists
            // (it won't exist during unit tests)
            if (Bootstrap.ClientWorld != null)
                clientTransport = Bootstrap.ClientWorld.GetExistingSystem<MemoryTransportClientSystem>();

            active = true;
        }
        public override bool Send(int connectionId, NativeSlice<byte> segment, Channel channel)
        {
            // only if server is running and client is connected
            if (active && clientTransport.IsConnected())
            {
                // copy segment data because it's only valid until return
                byte[] data = segment.ToArray();

                // we need a NativeArray (disposed in ServerTransport Update!)
                NativeArray<byte> array = new NativeArray<byte>(data, Allocator.Persistent);
                NativeSlice<byte> slice = new NativeSlice<byte>(array);

                // add client data message
                clientTransport.incoming.Enqueue(new Message(0, EventType.Data, array));
                OnSend?.Invoke(connectionId, slice); // statistics etc.
                return true;
            }
            return false;
        }
        public override void Disconnect(int connectionId)
        {
            // only disconnect if it was the 0 client
            if (connectionId == 0)
            {
                // add client disconnected message
                clientTransport.incoming.Enqueue(new Message(0, EventType.Disconnected, default));

                // server needs to know it too
                incoming.Enqueue(new Message(0, EventType.Disconnected, default));
            }
        }
        public override string GetAddress(int connectionId) => string.Empty;
        public override void Stop()
        {
            // clear all pending messages that we may have received.
            // over the wire, we wouldn't receive any more pending messages
            // ether after calling stop.
            // => need to Dispose all pending NativeArrays first though
            foreach (Message msg in incoming)
                if (msg.data.IsCreated)
                    msg.data.Dispose();
            incoming.Clear();

            // add client disconnected message
            clientTransport.incoming.Enqueue(new Message(0, EventType.Disconnected, default));

            // add server disconnected message
            incoming.Enqueue(new Message(0, EventType.Disconnected, default));

            // not active anymore
            active = false;
        }

        // receive in EarlyUpdate
        // NOTE: we DO NOT call all the events directly. instead we use a queue
        //       and only call them in OnUpdate. this is what we do with regular
        //       transports too, and this way the tests behave exactly the same!
        public override void EarlyUpdate()
        {
            while (incoming.Count > 0)
            {
                Message message = incoming.Dequeue();
                switch (message.eventType)
                {
                    case EventType.Connected:
                        //Debug.Log("MemoryTransport Server Message: Connected");
                        OnConnected(message.connectionId);
                        break;
                    case EventType.Data:
                        //Debug.Log("MemoryTransport Server Message: Data: " + BitConverter.ToString(message.data));
                        OnData(message.connectionId, new NativeSlice<byte>(message.data));
                        // clean up the allocated NativeArray<byte>
                        message.data.Dispose();
                        break;
                    case EventType.Disconnected:
                        //Debug.Log("MemoryTransport Server Message: Disconnected");
                        OnDisconnected(message.connectionId);
                        break;
                }
            }
        }

        // Send() sends directly. nothing to do in late update.
        public override void LateUpdate() {}
    }
}