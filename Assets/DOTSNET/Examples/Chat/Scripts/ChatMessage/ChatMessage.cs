using Unity.Collections;

namespace DOTSNET.Examples.Chat
{
    public struct ChatMessage : NetworkMessage
    {
        public FixedString32Bytes sender;
        public FixedString128Bytes text;

        public ChatMessage(FixedString32Bytes sender, FixedString128Bytes text)
        {
            this.sender = sender;
            this.text = text;
        }

        public bool Serialize(ref NetworkWriter writer) =>
             writer.WriteFixedString32(sender) &&
             writer.WriteFixedString128(text);

        public bool Deserialize(ref NetworkReader reader) =>
            reader.ReadFixedString32(out sender) &&
            reader.ReadFixedString128(out text);
    }
}