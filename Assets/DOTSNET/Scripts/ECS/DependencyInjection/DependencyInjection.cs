// ECS having a custom Bootstrap class allows us to inject dependencies
// automatically.
//
// before:
//   class TestSystem : ComponentSystem
//   {
//       protected OtherSystem other;
//       protected override void OnStartRunning()
//       {
//           other = World.GetExistingSystem<OtherSystem>();
//       }
//   }
//
// after:
//   class TestSystem : ComponentSystem
//   {
//       [AutoAssign] protected OtherSystem other;
//   }
//
// => simply call InjectDependenciesInAllWorlds() once from Bootstrap!
// => this works for instance/static/public/private fields.
using System;
using System.Reflection;
using Unity.Entities;
using UnityEngine;

namespace DOTSNET
{
    // the dependency injection class
    public static class DependencyInjection
    {
        // inject dependencies for all systems in a world
        public static void InjectDependencies(World world)
        {
            //Debug.Log("injecting for world: " + world.Name);
            // for each system
            foreach (ComponentSystemBase system in world.Systems)
            {
                // get the final type
                Type type = system.GetType();

                // for each field
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    // does it have [AutoAssign]?
                    if (field.IsDefined(typeof(AutoAssignAttribute), true))
                    {
                        // is there a system of that type in this world?
                        ComponentSystemBase dependency = world.GetExistingSystem(field.FieldType);
                        if (dependency != null)
                        {
                            field.SetValue(system, dependency);
                            //Debug.Log("Injected dependency for: " + type + "." + field.Name + " of type " + field.FieldType + " in world " + world.Name + " to " + dependency);
                        }
                        else Debug.LogWarning($"Failed to [AutoAssign] {type}.{field.Name} because the world {world.Name} has no system of type {field.FieldType}");
                    }
                }
            }
        }
    }

    // we need to resolve dependencies BEFORE a system's OnCreate is called.
    // Bootstrap includes this in PrependSetupSystems().
    // so disable auto creation.
    [DisableAutoCreation]
    // this system only needs to initialize once.
    // showing it in SimulationSystemUpdate at all times is just distracting.
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    partial class DependencyInjectionSystem : SystemBase
    {
        protected override void OnCreate()
        {
            //Debug.Log("Injecting Dependencies in World: " + World.Name);
            DependencyInjection.InjectDependencies(World);
        }

        protected override void OnUpdate() {}
    }
}
