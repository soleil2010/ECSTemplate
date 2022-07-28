// TransportClientSystem and TransportServerSystem both inherit from
// TransportSystem. This way we can add some common functionality without
// writing twice the code.
using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace DOTSNET
{
    // AlwaysUpdate is a good idea. we should never stop updating a transport.
    [AlwaysUpdateSystem]
    // Transport messages may need to apply physics, so update in the safe group
    [UpdateInGroup(typeof(ApplyPhysicsGroup))]
    // IMPORTANT: use [DisableAutoCreation] + SelectiveSystemAuthoring when
    //            inheriting
    public abstract partial class TransportSystem : SystemBase
    {
        // check if Transport is Available on this platform
        public abstract bool Available();

        // get max packet size that the transport can send at once.
        // some transports might support multiple channels with different max
        // sizes per channel.
        public abstract int GetMaxPacketSize(Channel channel);

        // find first available TransportSystem on this platform
        public static TransportSystem FindAvailable(World world)
        {
            foreach (ComponentSystemBase system in world.Systems)
                if (system is TransportSystem transport && transport.Available())
                    return transport;
            return null;
        }

        // ArraySegment -> NativeSlice transition //////////////////////////////
        // TODO remove transition code after moving all transports to NativeArray
        protected byte[] sendConversionBuffer;
        protected NativeArray<byte> receiveConversionBuffer;

        // helper function to convert NativeSlice<byte> to byte[].
        // useful for old transports that still operate on byte[].
        public static unsafe ArraySegment<byte> NativeSliceToArraySegment(NativeSlice<byte> slice, byte[] buffer)
        {
            // IMPORTANT: unsafe memcopy writes as much as we tell it to.
            // need to be 100% sure not write out of bounds here.
            if (slice.Length > buffer.Length)
                throw new IndexOutOfRangeException($"NativeSlice with length={slice.Length} wont safely fit into buffer with length={buffer.Length}.");

            // NativeSlice.CopyTo requires matching buffer size.
            // need to copy manually for now.
            fixed (byte* bufferPtr = buffer)
                UnsafeUtility.MemCpy(bufferPtr, slice.GetUnsafePtr(), slice.Length);
            return new ArraySegment<byte>(buffer, 0, slice.Length);
        }

        // helper function to convert NativeSlice<byte> to byte[]
        // useful for old transports that still operate on byte[].
        public static unsafe NativeSlice<byte> ArraySegmentToNativeSlice(ArraySegment<byte> segment, NativeArray<byte> buffer)
        {
            // IMPORTANT: unsafe memcopy writes as much as we tell it to.
            // need to be 100% sure not write out of bounds here.
            if (segment.Count > buffer.Length)
                throw new IndexOutOfRangeException($"ArraySegment with length={segment.Count} wont safely fit into buffer with length={buffer.Length}.");

            // NativeArray.CopyFrom requires matching buffer size.
            // need to copy manually for now.
            fixed (byte* segmentPtr = &segment.Array[segment.Offset])
                UnsafeUtility.MemCpy(buffer.GetUnsafePtr(), segmentPtr, segment.Count);
            return new NativeSlice<byte>(buffer, 0, segment.Count);
        }

        // update //////////////////////////////////////////////////////////////
        // Transports should process received messages in the beginning of the
        // frame, and flush out sends at the end of the frame.
        // -> we need two update methods for that, not just one
        // -> let's use default Update() + [UpdateInGroup] for EarlyUpdate
        //    and call LateUpdate from NetworkServerSystem after all is done.
        public abstract void EarlyUpdate();
        public abstract void LateUpdate();

        // overwrite OnUpdate to make it 100% clear that implementations should
        // use Early & Late Update!
        protected override void OnUpdate() => EarlyUpdate();

        // IMPORTANT: max message sizes are set from authoring.awake.
        //            initializing buffers in OnCreate would be too early.
        //            they would be initialized with the default max sizes.
        protected override void OnStartRunning()
        {
            // create conversion buffer (reliable is largest necessary size)
            int maxReliable = GetMaxPacketSize(Channel.Reliable);
            sendConversionBuffer = new byte[maxReliable];
            receiveConversionBuffer = new NativeArray<byte>(maxReliable, Allocator.Persistent);
        }

        // IMPORTANT: we create the buffers in OnStartRunning, so we need to
        //            dispose in OnStopRunning.
        protected override void OnStopRunning()
        {
            if (receiveConversionBuffer.IsCreated) receiveConversionBuffer.Dispose();
        }
    }
}