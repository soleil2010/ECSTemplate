using Unity.Collections;
using Unity.Entities;

namespace DOTSNET
{
    // note: we use booleans instead of an enum. this way it is easier to check
    //       if authenticated (easier than state == AUTH || state == WORLD) etc.
    // -> struct would avoid allocations, but class is just way easier to use
    //    especially when modifying state while iterating
    public class ConnectionState
    {
        // each connection needs to authenticate before it can send/receive
        // game specific messages
        public bool authenticated;

        // has the connection selected a player and joined the game world?
        public bool joinedWorld;

        // if Send fails only once, we will flag the connection as broken to
        // avoid possibly logging thousands of 'Send Message failed' warnings
        // in between the time send failed, and transport update removes the
        // connection.
        // it would just slow down the server significantly, and spam the logs.
        public bool broken;

        // save connection's main owned Entity for convenience.
        // ownedPerConnection multimap isn't sorted so we don't know which is main.
        // => set from JoinWorld(), so it's valid while 'joinedWorld' is true
        public Entity? mainPlayer;

        // remember last entities from LocalWorldState for delta compression.
        public NativeParallelHashMap<ulong, EntityState> lastEntities;

        // initialization
        public ConnectionState(int lastEntitiesInitialCapacity)
        {
            lastEntities = new NativeParallelHashMap<ulong, EntityState>(lastEntitiesInitialCapacity, Allocator.Persistent);
        }

        // cleanup
        public void Dispose()
        {
            if (lastEntities.IsCreated) lastEntities.Dispose();
        }
    }
}