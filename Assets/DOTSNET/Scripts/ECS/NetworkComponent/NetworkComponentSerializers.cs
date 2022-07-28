// packs all NetworkComponentSerializations into one payload that the server
// can send out.
// can be fully bursted/jobified.
// => see NetworkComponentSerializer comments!
using System.Collections.Generic;
using Unity.Entities;

namespace DOTSNET
{
    // the system runs in [ServerWorld, ClientWorld].
    // but we need to check which world we are in.
    // Bootstrap.World is static state. need something boostable instead.
    public enum CurrentWorld {Default, Client, Server}

    // note: [AlwaysUpdateSystem] isn't needed because we should only grab if
    //       there are entities around.
    [ServerWorld, ClientWorld]
    // this system is explicitly updated from NetworkServer/Client Broadcast().
    // let's put in the same group. easier to understand the update loop.
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    public partial class NetworkComponentSerializers : SystemBase
    {
        // SORTED serializer systems
        // -> so we can call .SerializeAll() before we need serializations
        //
        // IMPORTANT: has to be sorted because all systems write into ONE
        // NetworkComponentsSerialization BitWriter. that only works if they
        // always write one after another in the same order.
        // => Sorted DICTIONARY so we can sort by our own key (the name)!
        SortedDictionary<ushort, NetworkComponentSerializerBase> systems =
            new SortedDictionary<ushort, NetworkComponentSerializerBase>();

        public void Register<T>(NetworkComponentSerializer<T> system)
            where T : unmanaged, NetworkComponent =>
                systems[system.Key] = system;

        public void Unregister<T>(NetworkComponentSerializer<T> system)
            where T : unmanaged, NetworkComponent =>
                systems.Remove(system.Key);

        // 'World' to enum that can be bursted.
        // -> one system for client, one for server would be cleaner.
        // -> but then we have to define two per NetworkTransform.
        // => so let's keep this for now.
        CurrentWorld GetCurrentWorld()
        {
            if (World == Bootstrap.ServerWorld)
                return CurrentWorld.Server;
            if (World == Bootstrap.ClientWorld)
                return CurrentWorld.Client;
            return CurrentWorld.Default;
        }

        // SerializeAll should be called before broadcasting state.
        // -> no need to do this in every update.
        // -> DO need to do this at the right time BEFORE broadcasting.
        public void SerializeAll()
        {
            // reset all NetworkComponentsSerialization writers so we start at
            // BitWriter.position = 0 again
            Entities.ForEach((ref NetworkComponentsSerialization serialization) =>
            {
                serialization.writer.Position = 0;
            })
            .Run();

            // tell them all to serialize each Entity's NetworkComponents once.
            // we could let them all use OnUpdate too.
            // but let's serialize only when we need it at exactly the right
            // time.
            // IMPORTANT: sorted!
            CurrentWorld currentWorld = GetCurrentWorld();
            foreach (NetworkComponentSerializerBase system in systems.Values)
                system.SerializeAll(currentWorld);
        }

        // DeserializeAll should be called after receiving all new messages
        // -> no need to do this in every update.
        // -> DO need to do this at the right time AFTER receiving.
        public void DeserializeAll()
        {
            // tell them all to serialize each Entity's NetworkComponents once.
            // we could let them all use OnUpdate too.
            // but let's serialize only when we need it at exactly the right
            // time.
            // IMPORTANT: sorted!
            CurrentWorld currentWorld = GetCurrentWorld();
            foreach (NetworkComponentSerializerBase system in systems.Values)
                system.DeserializeAll(currentWorld);

            // reset all NetworkComponentsSerialization readers so entities
            // don't deserialize an old payload again next time
            Entities.ForEach((ref NetworkComponentsDeserialization deserialization) =>
            {
                deserialization.reader.Position = 0;
            })
            .Run();
        }

        protected override void OnUpdate() {}
    }
}
