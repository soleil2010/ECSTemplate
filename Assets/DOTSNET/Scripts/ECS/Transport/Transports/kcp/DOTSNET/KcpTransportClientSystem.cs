using System;
using Unity.Entities;
using UnityEngine;
using kcp2k;
using Unity.Collections;

namespace DOTSNET.kcp2k
{
    [ClientWorld]
    [AlwaysUpdateSystem]
    // use SelectiveSystemAuthoring to create it selectively
    [DisableAutoCreation]
    public class KcpTransportClientSystem : TransportClientSystem
    {
        // configuration
        public ushort Port = 7777;
        public bool NoDelay = true;
        public uint Interval = 10; // in milliseconds

        // advanced configuration
        public int FastResend = 0;
        public bool CongestionWindow = true; // KCP 'NoCongestionWindow' is false by default. here we negate it for ease of use.
        public uint SendWindowSize = 4096; // Kcp.WND_SND; 32 by default. DOTSNET sends a lot, so we need a lot more.
        public uint ReceiveWindowSize = 4096; // Kcp.WND_RCV; 128 by default. DOTSNET sends a lot, so we need a lot more.
        public int Timeout = 10000; // in milliseconds
        public uint MaxRetransmit = Kcp.DEADLINK * 2; // default prematurely disconnects a lot of people (#3022). use 2x.
        public bool NonAlloc = true;
        public bool MaximizeSendReceiveBuffersToOSLimit = true;

        // debugging
        public bool debugLog;

        // kcp
        internal KcpClient client;

        // translate Kcp <-> DOTSNET channels
        static KcpChannel ToKcpChannel(Channel channel) =>
            channel == Channel.Reliable ? KcpChannel.Reliable : KcpChannel.Unreliable;

        // all except WebGL
        public override bool Available() =>
            Application.platform != RuntimePlatform.WebGLPlayer;

        // MTU
        public override int GetMaxPacketSize(Channel channel)
        {
            switch (channel)
            {
                case Channel.Reliable:
                    return KcpConnection.ReliableMaxMessageSize(ReceiveWindowSize);
                case Channel.Unreliable:
                    return KcpConnection.UnreliableMaxMessageSize;
            }
            return 0;
        }

        public override bool IsConnected() => client != null && client.connected;

        // IMPORTANT: use Connect() to set everything up.
        //            - authoring configuration is only applied after OnCreate()
        //            - Early/LateUpdate will need to be in ClientConnectedGroup,
        //              so OnStartRunning() would be called way after
        //              Client.Connect() calls Transport.Conect()
        public override void Connect(string hostname)
        {
            // only once
            if (IsConnected()) return;

            // logging
            //   Log.Info should use Debug.Log if enabled, or nothing otherwise
            //   (don't want to spam the console on headless servers)
            if (debugLog)
                Log.Info = Debug.Log;
            else
                Log.Info = _ => {};
            Log.Warning = Debug.LogWarning;
            Log.Error = Debug.LogError;

            // client
            // other systems hook into transport events in OnCreate or
            // OnStartRunning in no particular order. the only way to avoid
            // race conditions where kcp uses OnConnected before another
            // system's hook (e.g. statistics OnData) was added is to wrap
            // them all in a lambda and always call the latest hook.
            // (= lazy call)
            client = NonAlloc
                ? new KcpClientNonAlloc(
                    () => OnConnected.Invoke(),
                    (message, _) => OnData.Invoke(ArraySegmentToNativeSlice(message, receiveConversionBuffer)),
                    () => OnDisconnected.Invoke())
                : new KcpClient(
                    () => OnConnected.Invoke(),
                    (message, _) => OnData.Invoke(ArraySegmentToNativeSlice(message, receiveConversionBuffer)),
                    () => OnDisconnected.Invoke());
            Debug.Log("KCP client created");

            client.Connect(hostname,
                           Port,
                           NoDelay,
                           Interval,
                           FastResend,
                           CongestionWindow,
                           SendWindowSize,
                           ReceiveWindowSize,
                           Timeout,
                           MaxRetransmit,
                           MaximizeSendReceiveBuffersToOSLimit);
        }

        public override bool Send(NativeSlice<byte> slice, Channel channel)
        {
            // convert to NativeSlice while kcp still works with ArraySegment
            // TODO make kcp work with NativeSlice
            ArraySegment<byte> segment = NativeSliceToArraySegment(slice, sendConversionBuffer);
            client.Send(segment, ToKcpChannel(channel));

            // invoke OnSend for statistics etc.
            OnSend?.Invoke(slice);
            return true;
        }

        public override void Disconnect()
        {
            client?.Disconnect();
            client = null;
        }

        // statistics
        public uint GetMaxSendRate() =>
            client.connection.MaxSendRate;
        public uint GetMaxReceiveRate() =>
            client.connection.MaxReceiveRate;
        public int GetSendQueueCount() =>
            client.connection.kcp.snd_queue.Count;
        public int GetReceiveQueueCount() =>
            client.connection.kcp.rcv_queue.Count;
        public int GetSendBufferCount() =>
            client.connection.kcp.snd_buf.Count;
        public int GetReceiveBufferCount() =>
            client.connection.kcp.rcv_buf.Count;

        // ECS /////////////////////////////////////////////////////////////////
        // process received in EarlyUpdate
        public override void EarlyUpdate() => client?.TickIncoming();

        // process outgoing in LateUpdate
        public override void LateUpdate() => client?.TickOutgoing();

        protected override void OnDestroy()
        {
            client = null;
            base.OnDestroy();
        }
    }
}