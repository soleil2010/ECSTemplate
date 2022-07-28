// We need custom authoring for NetworkIdentity because we need to assign the
// prefabId in OnValidate.
using System;
using System.Collections.Generic;
using Unity.Entities;
#if UNITY_EDITOR
using UnityEditor;
#if UNITY_2021_2_OR_NEWER
    using UnityEditor.SceneManagement;
#elif UNITY_2018_3_OR_NEWER
    using UnityEditor.Experimental.SceneManagement;
#endif
#endif
using UnityEngine;

namespace DOTSNET
{
    // require ConvertToNetworkEntity component because it's too easy to forget.
    [RequireComponent(typeof(ConvertToNetworkEntity))]
    public class NetworkIdentityAuthoring : MonoBehaviour, IConvertGameObjectToEntity
    {
        // position/rotation sync direction
        // usually SyncDirection is in each NetworkComponent, but pos/rot is in
        // every snapshot, yet we still need a SyncDirection.
        [Header("Authority")]
        [Tooltip("Position/Rotation sync direction.\n->SERVER_TO_CLIENT for server authoritative movement.\n->CLIENT_TO_SERVER for client authoritative movement.\nThis is only for movement. Does not affect SyncDirection of any other components.")]
        public SyncDirection transformDirection = SyncDirection.SERVER_TO_CLIENT;

        // prefabId:
        // * it's the asset Guid if it's a prefab
        // * it's a persistent unique Guid if it's a scene object
        // => one ID is enough. we don't need a distinction between the two
        //    because the PrefabSystem converts everything to a prefab anyway.
        //
        // stored as string because Unity can't serialize(=save) GUIDs
        // => we provide a Guid property for ease of use, so we don't have to
        //    convert manually each time it's needed
        // => [SerializeField] is needed, otherwise it's not saved
        // => [HideInInspector] so that we don't accidentally modify it. it's
        //    still serialized and saved just fine.
        [SerializeField, HideInInspector] internal string _prefabId;
        public Guid prefabId
        {
            get
            {
                // TryParse because null/"" would throw an exception
                return Guid.TryParse(_prefabId, out Guid guid)
                       ? guid
                       : Guid.Empty;
            }
            set
            {
                _prefabId = value.ToString("N");
            }
        }

        // prefabId lookup to make sure that duplicate scene objects don't use
        // the same ids
        static readonly Dictionary<Guid, NetworkIdentityAuthoring> prefabIds =
            new Dictionary<Guid, NetworkIdentityAuthoring>();

#if UNITY_EDITOR
        // helper function to check if a prefabId is already taken by someone
        // else
        bool IsDuplicatePrefabId(Guid guid)
        {
            return prefabIds.TryGetValue(guid, out NetworkIdentityAuthoring entity) &&
                   entity != null &&
                   entity != this;
        }

        // assign a unique, persistent prefabId to scene objects because Unity
        // doesn't have one. it has instanceId, but it's not persistent between
        // sessions.
        void AssignPrefabIdForSceneObject()
        {
            // we need to (re)assign prefabId if empty or duplicate
            if (prefabId == Guid.Empty || IsDuplicatePrefabId(prefabId))
            {
                // if we are building and OnValidate is called for an unopened
                // scene with a scene object that still needs a prefabId, then
                // we should cancel the build by throwing an exception.
                if (BuildPipeline.isBuildingPlayer)
                {
                    throw new Exception("Please open and resave that scene " + gameObject.scene.path + " because it contains invalid prefabIds. Then try to build again.");
                }

                // generate a new, truly random Guid
                Guid guid = Guid.NewGuid();

                // make sure it's not a duplicate (it shouldn't ever be)
                if (!IsDuplicatePrefabId(guid))
                {
                    // set editor dirty so the "*" will appear and the user resaves.
                    // -> otherwise the prefabId is never saved
                    // -> according to the documentation, we need to call undo
                    //    BEFORE modifying
                    Undo.RecordObject(this, "Generated prefabId");

                    // assign it
                    prefabId = guid;
                    Debug.Log(name + " in scene=" + gameObject.scene.name + " prefabId assigned to: " + guid);
                }
            }

            // add to prefabIds dict even if we didn't change anything.
            prefabIds[prefabId] = this;
        }

        void AssignPrefabIdForPrefab(string path)
        {
            prefabId = new Guid(AssetDatabase.AssetPathToGUID(path));
        }

        void AssignPrefabId()
        {
            // OnValidate is called at runtime too, but we should never modify
            // prefabIds at runtime.
            if (Application.isPlaying)
                return;

            // is this a prefab? then assign prefab's Guid
            if (PrefabUtility.IsPartOfPrefabAsset(gameObject))
            {
                string path = AssetDatabase.GetAssetPath(gameObject);
                // for a perfectly valid prefab, the path can be empty when
                // starting Unity and calling OnValidate for the first time, and
                // for one OnValidate call after adding components to a prefab.
                if (!string.IsNullOrWhiteSpace(path))
                {
                    AssignPrefabIdForPrefab(path);
                }
            }
            // is this a prefab that we are editing in prefab mode/prefab stage?
            else if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                // Unity calls OnValidate for the prefab stage prefab, and for
                // all scene objects based on it. ignore for scene objects.
                // note:
                //   GetCurrentPrefabState gets the prefab in prefab stage.
                //   GetPrefabStage checks if THIS prefab is being edited.
                if (PrefabStageUtility.GetPrefabStage(gameObject) != null)
                {
                    // then assign asset Guid
                    PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                    AssignPrefabIdForPrefab(stage.assetPath);
                }
            }
            // otherwise it's either a scene object that is based on a prefab:
            // is it a scene object that is based on a prefab?
            //     PrefabUtility.IsPartOfPrefabInstance(gameObject))
            // or it's just a scene object that isn't based no a prefab.
            //
            // either way we need to assign a prefabId for a SCENE OBJECT.
            // we CAN NOT assign a prefab Guid because we might drag multiple
            // prefabs with the same Guid into the scene, then we would have
            // duplicates.
            else
            {
                AssignPrefabIdForSceneObject();
            }
        }

        void OnValidate()
        {
            AssignPrefabId();
        }
#endif

        public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
        {
            if (prefabId == Guid.Empty)
            {
                Debug.LogError("Failed to convert " + name + " to Entity because it has an empty prefabId!");
                return;
            }

            // Convert is called for scene objects and for prefabs.
            // We need to assign the prefabId.
            NetworkIdentity identity = new NetworkIdentity
            {
                prefabId = Conversion.GuidToBytes16(prefabId),
                transformDirection = transformDirection
            };
            dstManager.AddComponentData(entity, identity);

            // NetworkComponent serialization components
            dstManager.AddComponentData(entity, new NetworkComponentsSerialization());
            dstManager.AddComponentData(entity, new NetworkComponentsDeserialization());

            // add the dynamic buffers.
            // GenerateAuthoring doesn't work for it yet, so we just add it here
            // secretly. the user doesn't need to worry about it.
            dstManager.AddBuffer<NetworkObserver>(entity);
        }
    }
}