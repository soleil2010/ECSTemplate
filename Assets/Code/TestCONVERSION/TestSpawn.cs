using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class TestSpawn : SystemBase
{
    private EntityQuery unInitializeSpawner;

    protected override void OnCreate()
    {
        unInitializeSpawner = GetEntityQuery(typeof(Tag_Uninitialize), typeof(Tag_TestSpawner));
        RequireForUpdate(unInitializeSpawner);
    }

    protected override void OnUpdate()
    {
        Entities
            .WithoutBurst()
            .WithStructuralChanges()
            .WithStoreEntityQueryInField(ref unInitializeSpawner)
            .ForEach((Entity spawner, in Test_CachRegiment regiment, in LocalToWorld ltw) =>
            {
                //Data_SpawnAxeDirection axesDir = new () { X = (int)ltw.Right.x, Z = (int)ltw.Forward.z };
                
                Entity newRegiment = EntityManager.Instantiate(regiment.Value);
                SetComponent(newRegiment, new Translation(){Value = ltw.Position});
                //EntityManager.AddComponent<Tag_Player>(newRegiment);
                //AddToRegiments(newRegiment);
                //SetRegimentPosition(newRegiment, true, ltw.Position, ltw.Rotation, axesDir);
                EntityManager.RemoveComponent<Tag_Uninitialize>(spawner);
            }).Run();
        //EntityManager.RemoveComponent<Tag_Uninitialize>(unInitializeSpawner);
    }
    /*
    private void SetRegimentPosition(Entity regiments, bool isPlayer, float3 basePosition, quaternion rotation, Data_SpawnAxeDirection axesDir)
    {
        float xOffset = 0;
        float3 position = new float3(xOffset, 0, basePosition.z);
        Data_RegimentClass regClass = GetComponent<Data_RegimentClass>(regiments);
        
        //DIFFERENCE PLAYER - ENEMY
        int unitPerLine = isPlayer ? regClass.MinRow : regClass.MaxRow;
        SetComponent(regiments, new UnitsPerLine(){Value = unitPerLine});
        
        SetComponent(regiments, new Translation(){Value = position});
        xOffset += (unitPerLine + 4) * regClass.SpaceBetweenUnitsX;
        SetComponent(regiments, new Rotation(){Value = rotation});
        EntityManager.AddComponentData(regiments, axesDir);
    }
    
    
    private void AddToRegiments(Entity regiments)
    {
        EntityManager.AddComponent<Tag_Uninitialize>(regiments);
        //PRESELECTION
        EntityManager.AddComponent<Flag_Preselection>(regiments);
        EntityManager.AddComponent<Filter_Preselection>(regiments);
        //SELECTION
        EntityManager.AddComponent<Flag_Selection>(regiments);
        EntityManager.AddComponent<Filter_Selection>(regiments);
        //ANIMATION
        EntityManager.AddComponent<Data_RegimentAnimationPlayed>(regiments);
        EntityManager.AddComponent<Data_LookRotation>(regiments);
        EntityManager.AddBuffer<Buffer_Units>(regiments);
    }
    */
}
