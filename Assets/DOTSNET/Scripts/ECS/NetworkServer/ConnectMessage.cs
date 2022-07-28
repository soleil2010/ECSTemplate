// ConnectMessage is an artificial message.
// It is never sent over the network. It is only used to register a handler.
namespace DOTSNET
{
    public struct ConnectMessage : NetworkMessage
    {
        public bool Serialize(ref NetworkWriter writer) => true;
        public bool Deserialize(ref NetworkReader reader) => true;
    }
}