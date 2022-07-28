using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;


[UpdateAfter(typeof(GameObjectAfterConversionGroup))]
public class ConversionTest : MonoBehaviour, IConvertGameObjectToEntity, IDeclareReferencedPrefabs
{
    public GameObject regimentPrefab;

    public void DeclareReferencedPrefabs(List<GameObject> referencedPrefabs)
    {
        referencedPrefabs.Add(regimentPrefab);
    }
    
    public void Convert(Entity entity, EntityManager dstManager, GameObjectConversionSystem conversionSystem)
    {
        dstManager.SetName(entity, "TEST RUNTIME");
        
        dstManager.AddComponent<Tag_TestSpawner>(entity);
        dstManager.AddComponent<Tag_Uninitialize>(entity);
        
        Entity regiment = conversionSystem.GetPrimaryEntity(regimentPrefab);
        dstManager.AddComponentData(entity, new Test_CachRegiment(){Value = regiment});
    }
}
