// NetworkMessage is an interface so that messages can be structs
// (in order to avoid allocations)
namespace DOTSNET
{
	// interfaces can't contain fields. need a separate static class.
	public static class NetworkMessageMeta
	{
		// systems need to check if messages contain enough bytes for ID header
		public const int IdSize = sizeof(ushort);

		// write only the message header.
		// common function in case we ever change the header size.
		// only exists because we can't use burst in PackMessage<T> generic (yet).
		// => so we can pack without burst via PackMessageHeader, Serialize...
		// TODO remove when Burst supports generic <T>
		public static bool PackMessageHeader(ushort messageId, ref NetworkWriter writer) =>
			writer.WriteUShort(messageId);

		// write only the message header.
		// common function in case we ever change the header size.
		// only exists because we can't use burst in PackMessage<T> generic (yet).
		// => so we can pack without burst via PackMessageHeader, Serialize...
		public static bool PackMessageHeader<T>(ref NetworkWriter writer)
			where T : struct, NetworkMessage =>
				PackMessageHeader(GetId<T>(), ref writer);

		// pack a message with <<id, content>> into a writer.
		// NetworkMessage always serializes with NetworkWriter because casting to
		// interface would allocate.
		public static bool PackMessage<T>(T message, ref NetworkWriter writer)
			where T : struct, NetworkMessage =>
				PackMessageHeader<T>(ref writer) &&
				message.Serialize(ref writer);

		// unpack message header. doesn't unpack content because we need <T>.
		// NetworkMessage always serializes with NetworkReader because casting to
		// INetworkReader interface would allocate.
		public static bool UnpackMessage(ref NetworkReader reader, out ushort messageId) =>
			reader.ReadUShort(out messageId);

		// automated message id from type hash.
		// platform independent via stable hashcode.
		// => convenient so we don't need to track messageIds across projects
		// => addons can work with each other without knowing their ids before
		// => 2 bytes is enough to avoid collisions.
		//    registering a messageId twice will log a warning anyway.
		public static ushort GetId<T>() where T : struct, NetworkMessage =>
			(ushort)typeof(T).FullName.GetStableHashCode();

		// default allocator to create a new message <T>
		// customizable in RegisterHandler<T> for cases where we want to reuse
		// large messages like WorldState etc.
		//
		// IMPORTANT: new T() causes heavy allocations because of calls
		//   to Activator.CreateInstance(). for the 10k benchmark it
		//   causes 88KB per frame!
		//   => 'where T : struct' & 'default' are allocation free!
		public static T DefaultMessageAllocator<T>()
			where T : struct, NetworkMessage =>
				default;

		// default deserializer to deserialize a new message <T>
		// customizable in RegisterHandler<T> for cases where we want to use
		// burst to deserialize large messages like WorldState etc.
		// TODO remove after burst supports generic <T>
		public static bool DefaultMessageDeserializer<T>(ref T message, ref NetworkReader reader)
			where T : struct, NetworkMessage =>
				message.Deserialize(ref reader);
	}

	// Action<ref> isn't possible. need a delegate to pass deserializers to
	// RegisterHandler functions.
	public delegate bool NetworkMessageDeserializerDelegate<T>(ref T message, ref NetworkReader reader)
		where T : struct, NetworkMessage;

	// the NetworkMessage interface
	public interface NetworkMessage
	{
		// OnSerialize serializes a message via NetworkWriter.
		// returns false if buffer was too small for all the data, or if it
		// contained invalid data (e.g. from an attacker).
		// => we need to let the user decide how to serialize. WriteBlittable
		//    isn't enough in all cases, e.g. arrays, compression, bit packing
		// => see also: gafferongames.com/post/reading_and_writing_packets/
		// => we have different NetworkReader implementations, but they all share
		//    the same interface
		// => INetworkWriter can't be bursted, and passing interface as ref 
		//    allocs. NetworkWriter is fine and works for every size.
		bool Serialize(ref NetworkWriter writer);

		// OnDeserialize deserializes a message via NetworkReader.
		// returns false if buffer was too small for all the data, or if it
		// contained invalid data (e.g. from an attacker).
		// => we need to let the user decide how to serialize. WriteBlittable
		//    isn't enough in all cases, e.g. arrays, compression, bit packing
		// => see also: gafferongames.com/post/reading_and_writing_packets/
		// => we have different NetworkReader implementations, but they all share
		//    the same interface
		// => INetworkReader can't be bursted, and passing interface as ref
		//    allocs. NetworkReader is fine and works for every size.
		bool Deserialize(ref NetworkReader reader);
	}
}