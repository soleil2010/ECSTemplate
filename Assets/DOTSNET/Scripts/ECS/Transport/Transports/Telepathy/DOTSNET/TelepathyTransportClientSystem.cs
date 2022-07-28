using System;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DOTSNET.TelepathyTransport
{
    [ClientWorld]
    [AlwaysUpdateSystem]
    // use SelectiveSystemAuthoring to create it selectively
    [DisableAutoCreation]
    public class TelepathyTransportClientSystem : TransportClientSystem
    {
        public ushort Port = 7777;

        // Nagle Algorithm can be disabled by enabling NoDelay
        public bool NoDelay = true;

        // timeouts in milliseconds.
        public int SendTimeout = 5000;
        public int ReceiveTimeout = 5000;

        // Protect against allocation attacks by keeping the max message size small. Otherwise an attacker host might send multiple fake packets with 2GB headers, causing the connected clients to run out of memory after allocating multiple large packets.
        public int MaxMessageSize = 16 * 1024;

        // Client processes a limit amount of messages per tick to avoid a deadlock where it might end up processing forever if messages come in faster than we can process them.
        public int MaxReceivesPerTick = 1000;

        // disconnect if message queues get too big.
        public int SendQueueLimit = 10000;
        public int ReceiveQueueLimit = 10000;

        Telepathy.Client client;

        public override bool Available() =>
            Application.platform != RuntimePlatform.WebGLPlayer;

        public override int GetMaxPacketSize(Channel _) => MaxMessageSize;

        public override bool IsConnected() => client != null && client.Connected;

        // IMPORTANT: use Connect() to set everything up.
        //            - authoring configuration is only applied after OnCreate()
        //            - Early/LateUpdate will need to be in ClientConnectedGroup,
        //              so OnStartRunning() would be called way after
        //              Client.Connect() calls Transport.Conect()
        public override void Connect(string hostname)
        {
            // only once
            if (IsConnected()) return;

            // create client
            client = new Telepathy.Client(MaxMessageSize);

            // tell Telepathy to use Unity's Debug.Log
            Telepathy.Log.Info = Debug.Log;
            Telepathy.Log.Warning = Debug.LogWarning;
            Telepathy.Log.Error = Debug.LogError;

            // other systems hook into transport events in OnCreate or
            // OnStartRunning in no particular order. the only way to avoid
            // race conditions where telepathy uses OnConnected before another
            // system's hook (e.g. statistics OnData) was added is to wrap
            // them all in a lambda and always call the latest hook.
            // (= lazy call)
            client.OnConnected = () => OnConnected.Invoke();
            client.OnData = (segment) => OnData.Invoke(ArraySegmentToNativeSlice(segment, receiveConversionBuffer));
            client.OnDisconnected = () => OnDisconnected.Invoke();

            // configure
            client.NoDelay = NoDelay;
            client.SendTimeout = SendTimeout;
            client.ReceiveTimeout = ReceiveTimeout;
            client.SendQueueLimit = SendQueueLimit;
            client.ReceiveQueueLimit = ReceiveQueueLimit;

            Debug.Log("TelepathyTransportClientSystem initialized!");

            client.Connect(hostname, Port);
        }

        public override bool Send(NativeSlice<byte> slice, Channel channel)
        {
            // TODO move Telepathy to NativeArray instead of conversion
            ArraySegment<byte> segment = NativeSliceToArraySegment(slice, sendConversionBuffer);
            client.Send(segment);

            // invoke OnSend for statistics etc.
            OnSend?.Invoke(slice);
            return true;
        }

        public override void Disconnect()
        {
            client?.Disconnect();
            client = null;
        }

        // ECS /////////////////////////////////////////////////////////////////
        // process received in EarlyUpdate
        public override void EarlyUpdate()
        {
            // process a maximum amount of client messages per tick
            // stops when there are no more messages
            // => tick even while not connected to still process disconnects.
            client?.Tick(MaxReceivesPerTick);
        }

        // Send() sends directly. nothing to do in late update.
        public override void LateUpdate() {}

        protected override void OnDestroy()
        {
            Debug.Log("TelepathyTransportClientSystem Shutdown");
            client?.Disconnect();
            client = null;
            base.OnDestroy();
        }
    }
}
