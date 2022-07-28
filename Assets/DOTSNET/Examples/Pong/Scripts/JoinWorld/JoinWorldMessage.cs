using Unity.Collections;

namespace DOTSNET.Examples.Pong
{
    public struct JoinWorldMessage : NetworkMessage
    {
        public FixedBytes16 playerPrefabId;

        public JoinWorldMessage(FixedBytes16 playerPrefabId)
        {
            this.playerPrefabId = playerPrefabId;
        }

        public bool Serialize(ref NetworkWriter writer) =>
            writer.WriteBytes16(playerPrefabId);

        public bool Deserialize(ref NetworkReader reader) =>
            reader.ReadBytes16(out playerPrefabId);
    }
}