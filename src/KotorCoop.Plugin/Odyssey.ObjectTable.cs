namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// Collects creature bodies directly from the server's live <c>CGameObjectArray</c>.
    ///
    /// This replaces the scaling controller's periodic private-heap vtable sweep. The table
    /// pointer and entry layout are already used by <see cref="ResolveServerObject"/> and are
    /// the same values proved against <c>CGameObjectArray::GetGameObject</c> (0x004D8230).
    /// The caller still performs its existing object-id-to-pointer resolve and all gameplay
    /// filters before writing hit points. This method only changes discovery: no engine state
    /// is written here.
    ///
    /// MAIN THREAD ONLY. The object array is owned by the server and is destroyed during a
    /// module transition; callers invoke this from the settled frame path and treat zero as
    /// an ordinary unavailable result.
    /// </summary>
    public static int CollectCreaturesByObjectTable(nint* into, int capacity)
    {
        if (into == null || capacity <= 0)
        {
            return 0;
        }

        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint objectArray = GetObjectArrayFn(server);
        if (objectArray == 0)
        {
            return 0;
        }

        nint tree = *(nint*)objectArray;
        if (tree == 0)
        {
            return 0;
        }

        int count = 0;
        for (int bucket = 0;
             bucket < CreatureObjectTableRule.BucketCount
             && CreatureObjectTableRule.HasRoom(count, capacity);
             bucket++)
        {
            nint entry = *(nint*)(tree + (bucket * sizeof(nint)));
            for (int walked = 0;
                 CreatureObjectTableRule.MayWalk(walked, entry)
                 && CreatureObjectTableRule.HasRoom(count, capacity);
                 walked++)
            {
                uint storedKey = *(uint*)(entry + CreatureObjectTableRule.EntryObjectId);
                nint creature = *(nint*)(entry + CreatureObjectTableRule.EntryObject);

                // The table contains every server object. The vtable and id checks are the
                // same cheap identity guards the old sweep used before scaling anything.
                uint objectId = CreatureObjectTableRule.FullObjectId(bucket, storedKey);
                if (creature != 0
                    && *(nint*)creature == CreatureFinder.CreatureVtable
                    && *(uint*)(creature + 0x04) == objectId)
                {
                    into[count++] = creature;
                }

                entry = *(nint*)(entry + CreatureObjectTableRule.EntryNext);
            }
        }

        return count;
    }
}
