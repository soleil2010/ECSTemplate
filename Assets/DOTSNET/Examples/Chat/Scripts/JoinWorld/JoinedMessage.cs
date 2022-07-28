namespace DOTSNET.Examples.Chat
{
    public struct JoinedMessage : NetworkMessage
    {
        public bool Serialize(ref NetworkWriter writer) => true;
        public bool Deserialize(ref NetworkReader reader) => true;
    }
}