// Modify NetworkServer settings via Authoring.
// -> all functions are virtual in case we want to inherit!

using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace DOTSNET
{
    public class NetworkServerAuthoring : MonoBehaviour, SelectiveSystemAuthoring
    {
        // find NetworkServerSystem in ECS world
        NetworkServerSystem server =>
            Bootstrap.ServerWorld.GetExistingSystem<NetworkServerSystem>();

        // add system if Authoring is used
        public virtual Type GetSystemType() { return typeof(NetworkServerSystem); }

        // grab state from ECS world
        public ServerState state => server.state;

        // configuration
        public bool startIfHeadless = true;
        public float tickRate = 60;
        public int connectionLimit = 1000;
        [Tooltip("Sync Entity Snapshots every 'interval' seconds.")]
        [FormerlySerializedAs("snapshotInterval")]
        public float broadcastInterval = 0.050f;
        [Tooltip("LocalWorldState message max size per connection.\nUseful to limit broadcast bandwidth in large worlds to something reasonable.\n\nOtherwise if a player's connection is slow, it wouldn't choke when walking into a large town or a horde of monsters, and then disconnect.\n\nNote: will only ever send up to Transport.GetMaxPacketSize.")]
        // configurable LocalWorldState max size per connection.
        // useful to limit broadcast bandwidth in large worlds to something reasonable.
        // otherwise if a player's connection is slow, it wouldn't choke when
        // walking into a large town or a horde of monsters, and then disconnect.
        // => 512 KB is huge. more than enough. yet still a reasonable limit.
        //    most transports' MaxMessageSizes are smaller anyway.
        //
        // note: LocalWorldState size will be Min(localWorldStateMaxSize, transport.GetMaxPacketSize)
        // note: can calculate KB/s from maxsize and sendInterval
        public int broadcastMaxSize = 512 * 1024;

        // apply configuration in Awake already
        // doing it in StartServer is TOO LATE because ECS world might auto
        // start the server in headless mode, in which case the authoring
        // StartServer function would never be called and the configuration
        // would never be applied.
        // IMPORTANT: MonoBehaviour.Awake() happens AFTER System.OnCreate().
        protected virtual void Awake()
        {
            server.startIfHeadless = startIfHeadless;
            server.tickRate = tickRate;
            server.connectionLimit = connectionLimit;
            server.broadcastInterval = broadcastInterval;
            server.broadcastMaxSize = broadcastMaxSize;
        }

        // call StartServer in ECS world
        public virtual void StartServer()
        {
            Debug.Log("Calling StartServer in ECS World...");
            server.StartServer();
        }

        // forward StopServer request to ECS world
        public virtual void StopServer()
        {
            Debug.Log("Calling StopServer in ECS World...");
            server.StopServer();
        }
    }
}