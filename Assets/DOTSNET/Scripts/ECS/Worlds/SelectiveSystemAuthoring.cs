// some systems like Transports have multiple implementations.
// we use [DisableAutoCreation] to disable all by default and then add a
// SelectiveSystemAuthoring component to the scene to enable it selectively.
// => we use an interface so that we aren't dependent on UnityEngine in the ECS
//    folder at all.
using System;

namespace DOTSNET
{
    // IMPORTANT: MonoBehaviour.Awake() happens AFTER System.OnCreate().
    // remember that when doing authoring configurations.
    public interface SelectiveSystemAuthoring
    {
        Type GetSystemType();
    }
}
