// same as ServerActiveSimulationSystemGroup but for LateSimulation update!
using Unity.Entities;

namespace DOTSNET
{
    // [ServerWorld] adds it to server world automatically. no bootstrap needed!
    [ServerWorld]
    [AlwaysUpdateSystem]
    // in late update group
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    public class ServerActiveLateSimulationSystemGroup : ComponentSystemGroup
    {
        // dependencies
        [AutoAssign] protected NetworkServerSystem server;

        protected override void OnUpdate()
        {
            // enable/disable systems based on server state
            // IMPORTANT: we need to set .Enabled to false after StopServer,
            //            otherwise OnStopRunning is never called in the group's
            //            systems. trying to call OnUpdate only while ACTIVE
            //            would not call OnStopRunning after StopServer.
            //
            // foreach (ComponentSystemBase system in Systems) <- allocates!
            for (int i = 0; i < Systems.Count; ++i) // <- does not allocate!
            {
                // with ?. null check to disable all systems if there is no
                // server. someone might not have a dotsnet scene open, or
                // someone might be working on a client-only addon.
                Systems[i].Enabled = server?.state == ServerState.ACTIVE;
            }

            // always call base OnUpdate, otherwise nothing is updated again
            base.OnUpdate();
        }
    }
}