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
    [ClientWorld]
    // use SelectiveSystemAuthoring to create it selectively
    [DisableAutoCreation]
    public class MemoryTransportClientSystem : TransportClientSystem
    {
        bool connected;
        internal Queue<Message> incoming = new Queue<Message>();

        // can't use [AutoAssign] because clientTransport is in another world
        public MemoryTransportServerSystem serverTransport;

        public override bool Available() => true;
        // 64KB is a reasonable max packet size. we don't want to allocate
        // int max = 2GB for buffers each time.
        // unit tests would take forever, pools would allocate so much.
        public override int GetMaxPacketSize(Channel _) => ushort.MaxValue;
        public override bool IsConnected() => connected;

        // IMPORTANT: use Connect() to set everything up.
        //            - authoring configuration is only applied after OnCreate()
        //            - Early/LateUpdate will need to be in ClientConnectedGroup,
        //              so OnStartRunning() would be called way after
        //              Client.Connect() calls Transport.Conect()
        public override void Connect(string address)
        {
            // sometimes we use MemoryTransport in host mode for bench marks etc
            // so in that case, find server transport if the server world exists
            // (it won't exist during unit tests)
            if (Bootstrap.ServerWorld != null)
                serverTransport = Bootstrap.ServerWorld.GetExistingSystem<MemoryTransportServerSystem>();

            // only if server is running
            if (serverTransport.IsActive())
            {
                // add server connected message
                serverTransport.incoming.Enqueue(new Message(0, EventType.Connected, default));

                // add client connected message
                incoming.Enqueue(new Message(0, EventType.Connected, default));

                connected = true;
            }
        }
        public override bool Send(NativeSlice<byte> segment, Channel channel)
        {
            // only  if client connected
            if (connected)
            {
                // copy slice data because it's only valid until return
                byte[] data = segment.ToArray();

                // we need a NativeArray (disposed in ServerTransport Update!)
                NativeArray<byte> array = new NativeArray<byte>(data, Allocator.Persistent);
                NativeSlice<byte> slice = new NativeSlice<byte>(array);

                // add server data message
                serverTransport.incoming.Enqueue(new Message(0, EventType.Data, array));
                OnSend?.Invoke(slice); // statistics etc.

                return true;
            }
            return false;
        }
        public override void Disconnect()
        {
            // only  if client connected
            if (connected)
            {
                // clear all pending messages that we may have received.
                // over the wire, we wouldn't receive any more pending messages
                // ether after calling disconnect.
                // => need to Dispose all pending NativeArrays first though
                foreach (Message msg in incoming)
                    if (msg.data.IsCreated)
                        msg.data.Dispose();
                incoming.Clear();

                // add server disconnected message
                serverTransport.incoming.Enqueue(new Message(0, EventType.Disconnected, default));

                // add client disconnected message
                incoming.Enqueue(new Message(0, EventType.Disconnected, default));

                // DO NOT set connect=false immediately.
                // server & client memory transport are directly connected.
                // it would stop any Server.Send() calls immediately.
                // => do it in update to be have like a regular transport.
            }
        }

        // receive in EarlyUpdate
        // NOTE: we DO NOT call all the events directly. instead we use a queue
        //       and only call them in OnUpdate. this is what we do with regular
        //       transports too, and this way the tests behave exactly the same!
        public override void EarlyUpdate()
        {
            // note: process even if not connected because when calling
            // Disconnect, we add a Disconnected event which still needs to be
            // processed here.
            while (incoming.Count > 0)
            {
                Message message = incoming.Dequeue();
                switch (message.eventType)
                {
                    case EventType.Connected:
                        //Debug.Log("MemoryTransport Client Message: Connected");
                        OnConnected();
                        break;
                    case EventType.Data:
                        //Debug.Log("MemoryTransport Client Message: Data: " + BitConverter.ToString(message.data));
                        OnData(new NativeSlice<byte>(message.data));
                        // clean up the allocated NativeArray<byte>
                        message.data.Dispose();
                        break;
                    case EventType.Disconnected:
                        // not connected anymore
                        connected = false;
                        //Debug.Log("MemoryTransport Client Message: Disconnected");
                        OnDisconnected();
                        break;
                }
            }
        }

        // Send() sends directly. nothing to do in late update.
        public override void LateUpdate() {}
    }
}