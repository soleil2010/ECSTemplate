// ECS doesn't have Entity.GetComponents<NetworkComponent> and never will.
// for max performance, we need to think 'data transformation' anyway.
//
// instead of GetComponents<NetworkComponent>, for each type we need a system
// that serializes ALL entities with that component.
//
// => we could store them in NativeHashMap<Entity, Serialization>, but then we
//    would have to register each Serializer<T> into a registry to find all
//    components of an entity, etc.
//    => complex & hard to burst
// => we could store each component's serialization in a DynamicBuffer entry.
//    that works, but we would need the same size for each entry and it's kind
//    of unnecessary
// => INSTEAD we have one NetworkComponentsSerialization component that all
//    systems write into. this works because we update them in sorted order!
//    (and can be bursted)
// => we can get an entity's serialized NetworkComponents without any reflection
//    or GetComponentTypes magic that would be too slow / can't be bursted
// => all systems can be independent, don't need to know each other
//
// that's the DOTS/ECS way.
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DOTSNET
{
    // need a base class without <T> so NetworkServer can store them in a list!
    public abstract partial class NetworkComponentSerializerBase : SystemBase
    {
        // SerializeAll should be called before broadcasting state.
        // -> no need to do this in every update.
        // -> DO need to do this at the right time BEFORE broadcasting.
        public abstract void SerializeAll(CurrentWorld currentWorld);

        // DeserializeAll should be called after receiving all new messages
        // -> no need to do this in every update.
        // -> DO need to do this at the right time AFTER receiving.
        public abstract void DeserializeAll(CurrentWorld currentWorld);
    }

    // note: [AlwaysUpdateSystem] isn't needed because we should only grab if
    //       there are entities around.
    [ServerWorld, ClientWorld]
    public abstract partial class NetworkComponentSerializer<T> : NetworkComponentSerializerBase
        where T : unmanaged, NetworkComponent
    {
        [AutoAssign] protected NetworkComponentSerializers registry;

        // cache key once, outside of burst (typeof can't be used with Burst)
        public readonly ushort Key =
            (ushort)(typeof(T).FullName.GetStableHashCode() & 0xFFFF);

        // queries
        EntityQuery serializeQuery;
        EntityQuery deserializeQuery;

        // register ourselves in the main system
        protected override void OnCreate()
        {
            serializeQuery = GetEntityQuery(
                ComponentType.ReadOnly<NetworkIdentity>(),
                ComponentType.ReadOnly<T>(),
                ComponentType.ReadWrite<NetworkComponentsSerialization>()
            );

            deserializeQuery = GetEntityQuery(
                ComponentType.ReadOnly<NetworkIdentity>(),
                ComponentType.ReadWrite<T>(),
                ComponentType.ReadWrite<NetworkComponentsDeserialization>()
            );

            registry.Register(this);
        }

        // unregister ourselves from the main system
        protected override void OnDestroy()
        {
            registry.Unregister(this);

            // systems automatically dispose of their EntityQueries according
            // to EntityQuery.Dispose() comments in the code.
            //serializeQuery.Dispose();
            //deserializeQuery.Dispose();
        }

        // check if we should serialize this particular component this time.
        // depends on SyncDirection and current world.
        static bool ShouldSerialize(CurrentWorld currentWorld, NetworkIdentity identity, T component)
        {
            // server: always
            //   CLIENT_TO_SERVER _could_ be ignored but we don't since we
            //   would need different serializations for others/owner.
            //   => easier to just ignore locally on the owner.
            if (currentWorld == CurrentWorld.Server)
                return true;

            // client: only if local player owned, and if this component is
            // synced from CLIENT_TO_SERVER.
            if (currentWorld == CurrentWorld.Client)
                return identity.owned &&
                       component.GetSyncDirection() == SyncDirection.CLIENT_TO_SERVER;

            // neither? make it obvious why we return false!
            Debug.LogError($"ShouldSerialize: unexpected world: {currentWorld}");
            return false;
        }

        static bool ShouldDeserialize(CurrentWorld currentWorld, NetworkIdentity identity, T component)
        {
            // server: StateUpdateServerMessageSystem already checks if the
            // connection that sent the StateUpdate actually owns the netId.
            // so here we only have to check if this particular NetworkComponent
            // is allowed to be synced from CLIENT_TO_SERVER.
            // otherwise we would illegally overwrite a server owned component.
            if (currentWorld == CurrentWorld.Server)
            {
                return component.GetSyncDirection() == SyncDirection.CLIENT_TO_SERVER;
            }

            // client: apply all StateUpdates if we don't have authority over
            // this particular NetworkEntity / NetworkComponent.
            if (currentWorld == CurrentWorld.Client)
            {
                // do we have authority over this NetworkEntity & component?
                bool hasAuthority = identity.owned &&
                                    component.GetSyncDirection() == SyncDirection.CLIENT_TO_SERVER;
                // then don't deserialize
                return !hasAuthority;
            }

            // neither? make it obvious why we return false!
            Debug.LogError($"ShouldDeserialize: unexpected world: {currentWorld}");
            return false;
        }

        // helper function to serialize <T>
        // needs to be static for Burst!
        protected static void Serialize(CurrentWorld currentWorld, ushort key, NetworkIdentity identity, T component, ref NetworkComponentsSerialization serialization)
        {
            // should we serialize this component?
            if (!ShouldSerialize(currentWorld, identity, component))
                return;

            // serialize <<key, NetworkComponent>>
            // into the full serialization BitWriter
            if (serialization.writer.WriteUShort(key) &&
                component.Serialize(ref serialization.writer))
            {
                // good
                //Debug.Log($"Serialized {key:X2}. Writer new Position={serialization.writer.Position}");
            }
            else Debug.LogError($"Serialization failed for NetworkComponent key={key:X4} with writer Position={serialization.writer.Position}");
        }

        // helper function to deserialize <T>
        // needs to be static for Burst!
        protected static void Deserialize(CurrentWorld currentWorld, ushort key, NetworkIdentity identity, ref T component, ref NetworkComponentsDeserialization deserialization)
        {
            // IMPORTANT:
            //
            // serialization order is deterministic!
            //
            // if we have components A B C, we might receive:
            //  * nothing
            //  * ABC
            //  * just A
            //  * just AB
            //  * just BC
            //  * just C
            //  * etc.
            //
            // so when deserializing, there are exactly THREE cases:
            //  * try to read A. find content for A. great.
            //  * try to read A. can't find content. nothing for A this time.
            //  * we don't want A. but there might still be content for A.
            //    => in this case we have to skip over it
            //    => otherwise B will find content A and won't know what to do!

            // do nothing if reader is empty.
            if (deserialization.reader.Remaining == 0)
                return;

            // let's peek(!) the key first.
            // if it's not for us, great.
            // if it's for us, we HAVE TO read content even if we don't want it.
            // (otherwise next component would be confused by our content)
            if (deserialization.reader.PeekUShort(out ushort readKey))
            {
                // was this for us?
                if (readKey == key)
                {
                    // we peeked before. now actually eat the key.
                    deserialization.reader.ReadUShort(out ushort _);

                    // there is content for us. read it NO MATTER WHAT.
                    // otherwise the next system would read our content!
                    // (deserialize into a temporary component first)
                    T copy = component;
                    if (copy.Deserialize(ref deserialization.reader))
                    {
                        // now, do we actually want to use that content?
                        // in CLIENT_TO_SERVER mode, server still broadcasts
                        // our content to everyone and WE are responsible for
                        // DROPPING it if we have authority over this component.
                        if (ShouldDeserialize(currentWorld, identity, component))
                        {
                            // copy that content to our actual component
                            component = copy;
                            //Debug.Log($"Deserialized {key:X2}. Reader new BitPosition={deserialization.reader.BitPosition}");
                        }
                    }
                    else Debug.LogError($"Deserialization: reading content failed for NetworkComponent key={key:X4} with reader Position={deserialization.reader.Position} Remaining={deserialization.reader.Remaining}");
                }
                // not for us. meaning there is nothing for us this time. fine.
            }
            else Debug.LogError($"Deserialization: reading key failed for NetworkComponent key={key:X4} with reader Position={deserialization.reader.Position} Remaining={deserialization.reader.Remaining}");
        }

        // SerializeAll default implementation.
        // we can't use Entities.ForEach<T>,
        // but we can use an EntityQuery<T>,
        // which is slightly slower but at least not as verbose.
        //
        // can be overwritten with a bursted ForEach call for maximum performance:
        //
        //   public override void SerializeAll(CurrentWorld currentWorld)
        //   {
        //       ushort key = Key; // copy for Burst
        //       Entities.ForEach((ref NetworkComponentsSerialization serialization, in NetworkIdentity identity, in Health component) =>
        //       {
        //           Serialize(currentWorld, key, identity, component, ref serialization);
        //       })
        //       .WithoutBurst() // for debugging so we can use breakpoints!
        //       .Run();
        //   }
        //
        // (ForEach is tested in TestNetworkComponentB_SERVER_TO_CLIENT_Serializer)
        public override void SerializeAll(CurrentWorld currentWorld)
        {
            // get results from query
            NativeArray<NetworkComponentsSerialization> serializations = serializeQuery.ToComponentDataArray<NetworkComponentsSerialization>(Allocator.Temp);
            NativeArray<NetworkIdentity> identities = serializeQuery.ToComponentDataArray<NetworkIdentity>(Allocator.Temp);
            NativeArray<T> components = serializeQuery.ToComponentDataArray<T>(Allocator.Temp);

            // serialize each one
            // TODO burst/job
            for (int i = 0; i < serializations.Length; ++i)
            {
                NetworkComponentsSerialization serialization = serializations[i];
                Serialize(currentWorld, Key, identities[i], components[i], ref serialization);
                serializations[i] = serialization;
            }

            // write modications
            serializeQuery.CopyFromComponentDataArray(serializations);

            // cleanup
            serializations.Dispose();
            identities.Dispose();
            components.Dispose();
        }

        // DeserializeAll default implementation.
        // we can't use Entities.ForEach<T>,
        // but we can use an EntityQuery<T>,
        // which is slightly slower but at least not as verbose.
        //
        // can be overwritten with a bursted ForEach call for maximum performance:
        //
        //   public override void DeserializeAll(CurrentWorld currentWorld)
        //   {
        //       ushort key = Key; // copy for Burst
        //       Entities.ForEach((ref NetworkComponentsDeserialization deserialization, ref T component, in NetworkIdentity identity) =>
        //       {
        //           Deserialize(currentWorld, key, identity, ref component, ref deserialization);
        //       })
        //       .WithoutBurst() // for debugging so we can use breakpoints!
        //       .Run();
        //   }
        //
        // (ForEach is tested in TestNetworkComponentB_SERVER_TO_CLIENT_Serializer)
        public override void DeserializeAll(CurrentWorld currentWorld)
        {
            // get results from query
            NativeArray<NetworkComponentsDeserialization> deserializations = deserializeQuery.ToComponentDataArray<NetworkComponentsDeserialization>(Allocator.Temp);
            NativeArray<NetworkIdentity> identities = deserializeQuery.ToComponentDataArray<NetworkIdentity>(Allocator.Temp);
            NativeArray<T> components = deserializeQuery.ToComponentDataArray<T>(Allocator.Temp);

            // deserialize each one
            // TODO burst/job
            for (int i = 0; i < deserializations.Length; ++i)
            {
                T component = components[i];
                NetworkComponentsDeserialization deserialization = deserializations[i];
                Deserialize(currentWorld, Key, identities[i], ref component, ref deserialization);
                components[i] = component;
                deserializations[i] = deserialization;
            }

            // write modications
            deserializeQuery.CopyFromComponentDataArray(components);
            deserializeQuery.CopyFromComponentDataArray(deserializations);

            // cleanup
            deserializations.Dispose();
            identities.Dispose();
            components.Dispose();
        }

        protected override void OnUpdate() {}
    }
}
