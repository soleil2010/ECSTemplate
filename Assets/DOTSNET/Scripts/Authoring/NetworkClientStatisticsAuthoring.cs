// a simple packets & bandwidth GUI
using System;
using UnityEngine;

namespace DOTSNET
{
    public class NetworkClientStatisticsAuthoring : MonoBehaviour, SelectiveSystemAuthoring
    {
        // find NetworkClientSystem in ECS world
        NetworkClientStatisticsSystem statistics =>
            Bootstrap.ClientWorld.GetExistingSystem<NetworkClientStatisticsSystem>();

        // add system if Authoring is used
        public Type GetSystemType() { return typeof(NetworkClientStatisticsSystem); }

        // IMPORTANT: MonoBehaviour.Awake() happens AFTER System.OnCreate().

        void OnGUI()
        {
            // create GUI area
            GUILayout.BeginArea(new Rect(15, 165, 220, 300));

            // background
            GUILayout.BeginVertical("Box");
            GUILayout.Label("<b>Client Statistics</b>");

            // sending ("msgs" instead of "packets" to fit larger numbers)
            GUILayout.Label($"Send: {statistics.SentPacketsPerSecond} msgs @ {Utils.PrettyBytes(statistics.SentBytesPerSecond)}/s");

            // receiving ("msgs" instead of "packets" to fit larger numbers)
            GUILayout.Label($"Recv: {statistics.ReceivedPacketsPerSecond} msgs @ {Utils.PrettyBytes(statistics.ReceivedBytesPerSecond)}/s");

            // end background
            GUILayout.EndVertical();

            // end of GUI area
            GUILayout.EndArea();
        }
    }
}