﻿using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DOTSNET.Examples.Pong
{
    public class AutoJoinWorldSystemAuthoring : MonoBehaviour, SelectiveSystemAuthoring
    {
        // add system if Authoring is used
        public Type GetSystemType() => typeof(AutoJoinWorldSystem);
    }

    [ClientWorld]
    [UpdateInGroup(typeof(ClientConnectedSimulationSystemGroup))]
    // use SelectiveSystemAuthoring to create it selectively
    [DisableAutoCreation]
    public partial class AutoJoinWorldSystem : SystemBase
    {
        [AutoAssign] protected NetworkClientSystem client;
        [AutoAssign] protected PrefabSystem prefabSystem;

        bool FindFirstRegisteredPrefab(out FixedBytes16 prefabId, out Entity prefab)
        {
            foreach (KeyValuePair<FixedBytes16, Entity> kvp in prefabSystem.prefabs)
            {
                prefabId = kvp.Key;
                prefab = kvp.Value;
                return true;
            }
            prefabId = new FixedBytes16();
            prefab = new Entity();
            return false;
        }

        // OnStartRunning is called after the client connected
        protected override void OnStartRunning()
        {
            // our example only has 1 spawnable prefab. let's use that for the
            // player.
            if (FindFirstRegisteredPrefab(out FixedBytes16 prefabId, out _))
            {
                JoinWorldMessage message = new JoinWorldMessage(prefabId);
                client.Send(message);
                Debug.Log("AutoJoinWorldSystem: requesting to spawn player with prefabId=" + Conversion.Bytes16ToGuid(prefabId));
            }
            else Debug.LogError("AutoJoinWorldSystem: no registered prefab found to join with.");
        }

        protected override void OnUpdate() {}
    }
}