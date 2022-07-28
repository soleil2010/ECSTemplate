// provide packets/second and bytes/second statistics
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DOTSNET
{
    // always update, even if no entities around
    [AlwaysUpdateSystem]
    [ServerWorld]
    [UpdateInGroup(typeof(ServerActiveSimulationSystemGroup))]
    // use SelectiveAuthoring to create/inherit it selectively
    [DisableAutoCreation]
    public partial class NetworkServerStatisticsSystem : SystemBase
    {
        // dependencies
        TransportServerSystem transport;

        // capture interval
        // long bytes to support >2GB
        double intervalStartTime;
        int intervalReceivedPackets;
        long intervalReceivedBytes;
        int intervalSentPackets;
        long intervalSentBytes;

        // results from last interval
        // long bytes to support >2GB
        public int ReceivedPacketsPerSecond;
        public long ReceivedBytesPerSecond;
        public int SentPacketsPerSecond;
        public long SentBytesPerSecond;

        // hook up to Transport events
        protected override void OnStartRunning()
        {
            // find available client transport
            transport = TransportSystem.FindAvailable(World) as TransportServerSystem;
            if (transport != null)
            {
                transport.OnData += OnReceive;
                transport.OnSend += OnSend;
            }
            else Debug.LogError($"NetworkServerStatisticsSystem: no available TransportServerSystem found on this platform: {Application.platform}");
        }

        // remove Transport hooks
        protected override void OnStopRunning()
        {
            if (transport != null)
            {
                transport.OnData -= OnReceive;
                transport.OnSend -= OnSend;
            }
        }

        void OnReceive(int _, NativeSlice<byte> slice)
        {
            ++intervalReceivedPackets;
            intervalReceivedBytes += slice.Length;
        }

        void OnSend(int _, NativeSlice<byte> slice)
        {
            ++intervalSentPackets;
            intervalSentBytes += slice.Length;
        }

        protected override void OnUpdate()
        {
            // calculate results every second
            if (Time.ElapsedTime >= intervalStartTime + 1)
            {
                ReceivedPacketsPerSecond = intervalReceivedPackets;
                ReceivedBytesPerSecond = intervalReceivedBytes;
                SentPacketsPerSecond = intervalSentPackets;
                SentBytesPerSecond = intervalSentBytes;

                intervalReceivedPackets = 0;
                intervalReceivedBytes = 0;
                intervalSentPackets = 0;
                intervalSentBytes = 0;

                intervalStartTime = Time.ElapsedTime;
            }
        }
    }
}