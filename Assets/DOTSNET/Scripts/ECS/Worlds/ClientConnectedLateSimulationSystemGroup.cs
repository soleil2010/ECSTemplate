// same as ServerActiveSimulationSystemGroup but for LateSimulation update!
using Unity.Entities;

namespace DOTSNET
{
    // [ClientWorld] adds it to client world automatically. no bootstrap needed!
    [ClientWorld]
    [AlwaysUpdateSystem]
    // in late update group
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    public class ClientConnectedLateSimulationSystemGroup : ComponentSystemGroup
    {
        // dependencies
        [AutoAssign] protected NetworkClientSystem client;

        protected override void OnUpdate()
        {
            // enable/disable systems based on client state
            // IMPORTANT: we need to set .Enabled to false after Disconnect,
            //            otherwise OnStopRunning is never called in the group's
            //            systems. trying to call OnUpdate only while CONNECTED
            //            would not call OnStopRunning after disconnected.
            //
            // foreach (ComponentSystemBase system in Systems) <- allocates!
            for (int i = 0; i < Systems.Count; ++i) // <- does not allocate!
            {
                // with ?. null check to disable all systems if there is no
                // client. someone might not have a dotsnet scene open, or
                // someone might be working on a server-only addon.
                Systems[i].Enabled = client?.state == ClientState.CONNECTED;
            }

            // always call base OnUpdate, otherwise nothing is updated again
            base.OnUpdate();
        }
    }
}