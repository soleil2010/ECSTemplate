// Interest Management is needed to broadcast an entity's updates only to the
// surrounding players in order to save bandwidth.
//
// This can be done in a lot of different ways:
// - brute force distance checking everyone to everyone else
// - physics sphere casts to find everyone in a radius
// - spatial hashing aka grid checking
// - etc.
//
// So we need a base class for all of them.
using Unity.Entities;

namespace DOTSNET
{
    // ComponentSystem for now. Jobs come later.
    [ServerWorld]
    // update AFTER everything else, but before Server broadcasts.
    // and only while server is active.
    [UpdateInGroup(typeof(ServerActiveLateSimulationSystemGroup))]
    // IMPORTANT: use [UpdateBefore(typeof(BroadcastSystem))] when inheriting
    // IMPORTANT: use [DisableAutoCreation] + SelectiveSystemAuthoring when
    //            inheriting
    public abstract partial class InterestManagementSystem : SystemBase
    {
        // dependencies
        [AutoAssign] protected NetworkServerSystem server;

        // rebuild all areas of interest for everyone once
        //
        // note:
        //   we DO NOT do any custom rebuilding after someone joined/spawned or
        //   disconnected/unspawned.
        //   this would require INSANE complexity.
        //   for example, OnTransportDisconnect would have to:
        //     1. remove the connection so the connectionId is invalid
        //     2. then call BroadcastAfterUnspawn(oldEntity) which broadcasts
        //        destroyed messages BEFORE rebuilding so we know the old
        //        observers that need to get the destroyed message
        //     3. RebuildAfterUnspawn to remove it
        //     4. then remove the Entity from connection's owned objects, which
        //        IS NOT POSSIBLE anymore because the connection was already
        //        removed. which means that the next rebuild would still see it
        //        etc.
        //        (it's just insanity)
        //   additionally, we would also need extra flags in Spawn to NOT
        //   rebuild when spawning 10k scene objects in start, etc.s
        //
        //   DOTS is fast, so it makes no sense to have that insane complexity.
        //
        // first principles:
        //   it wouldn't even make sense to have special cases because players
        //   might walk in and out of range from each other all the time anyway.
        //   we already need to handle that case. (dis)connect is no different.
        public abstract void RebuildAll();
    }
}
