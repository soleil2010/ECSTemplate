// comparer to sort EntityState list by distance to main player
using System.Collections.Generic;
using Unity.Mathematics;

namespace DOTSNET
{
    // struct to avoid runtime allocations.
    // we need to create a new one for each connection's main player position.
    public struct EntityStateSorter : IComparer<EntityState>
    {
        public float3 position;

        public EntityStateSorter(float3 position)
        {
            this.position = position;
        }

        public int Compare(EntityState a, EntityState b)
        {
            // calculate both their distances to the comparison position
            float aDistance = math.distance(a.position, position);
            float bDistance = math.distance(b.position, position);

            // compare float distance and return int
            // NOT BURST COMPILABLE:
            //return Comparer<float>.Default.Compare(aDistance, bDistance);

            // this is burst compilable and does the same.
            // guaranteed with unit tests.
            if (aDistance < bDistance) return -1;
            if (aDistance > bDistance) return 1;
            return 0;
        }
    }
}