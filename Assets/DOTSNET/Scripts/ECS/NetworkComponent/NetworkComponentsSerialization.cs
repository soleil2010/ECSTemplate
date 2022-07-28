// components write into the writer, sorted.
// components read from reader, sorted.
// => 128 bytes limit per entity for now.
// => separated for cache line
using Unity.Entities;

namespace DOTSNET
{
    public struct NetworkComponentsSerialization : IComponentData
    {
        public NetworkWriter128 writer;
    }

    public struct NetworkComponentsDeserialization : IComponentData
    {
        public NetworkReader128 reader;
    }
}