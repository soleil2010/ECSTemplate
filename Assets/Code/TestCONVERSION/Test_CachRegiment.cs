using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;


public struct Tag_TestSpawner : IComponentData { }
public struct Tag_Uninitialize : IComponentData { }
public struct Test_CachRegiment : IComponentData
{
    public Entity Value;
}