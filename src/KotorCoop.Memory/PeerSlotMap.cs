namespace KotorCoop.Memory;

/// <summary>
/// Decides which shared-block slot each remote player owns, and keeps them there.
///
/// EVERYTHING the in-process plugin derives from a slot is indexed by slot and by nothing
/// else: the spawned body, the name written over it, the equipment applied to it, its
/// faction state, and the running total of damage it has taken. The block does carry a peer
/// id per slot, but the plugin never reads it.
///
/// So the ordering is load-bearing. Packing peers into slots in list order — which is what
/// this replaced — means one player leaving renumbers everyone after them, and the survivors
/// inherit the leaver's body: the wrong name over their head, the wrong gear, and someone
/// else's cumulative damage forwarded to their real character. With two players there is
/// only ever slot 0, so none of it could happen; the first three-player session makes it the
/// normal case.
///
/// A slot is deliberately NOT released the moment its peer is missing from a publication.
/// The list handed in has already been filtered to the local module, so "absent this tick"
/// usually means "walked through a door" — releasing then would rebuild their phantom from
/// scratch on every doorway. Slots are reclaimed only under pressure, and then only from a
/// holder that is genuinely not present.
/// </summary>
public sealed class PeerSlotMap(int capacity)
{
    /// <summary>Peer id in each slot; 0 = free. Peer ids start at 1, so 0 is safely
    /// "nobody" — see CoopHost.HostPeerId.</summary>
    private readonly uint[] _owner = new uint[capacity];

    public int Capacity => capacity;

    /// <summary>The peer holding a slot, 0 when the slot is free or the index is out of range.</summary>
    public uint Owner(int slot) => slot >= 0 && slot < capacity ? _owner[slot] : 0;

    /// <summary>The slot a peer holds, -1 when it has none.</summary>
    public int SlotOf(uint peerId)
    {
        if (peerId == 0)
        {
            return -1;
        }

        for (int i = 0; i < capacity; i++)
        {
            if (_owner[i] == peerId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Forgets every assignment. For a disconnect: the next session deals fresh peer
    /// ids, and holding slots for players who no longer exist would push the new ones down
    /// the block for no reason.</summary>
    public void Clear() => Array.Clear(_owner);

    /// <summary>
    /// The slot for <paramref name="peerId"/>, assigning one if it has none. Returns -1 only
    /// when every slot is held by a peer that is present right now, i.e. the session is
    /// genuinely over capacity.
    /// </summary>
    public int Assign(uint peerId, IReadOnlyList<uint> presentPeerIds)
    {
        if (peerId == 0)
        {
            return -1;
        }

        int existing = SlotOf(peerId);
        if (existing >= 0)
        {
            return existing;
        }

        for (int i = 0; i < capacity; i++)
        {
            if (_owner[i] == 0)
            {
                _owner[i] = peerId;
                return i;
            }
        }

        // Full. Evict a holder that is not in this publication — it is that, or refuse to
        // show the new player at all.
        for (int i = 0; i < capacity; i++)
        {
            bool stillHere = false;
            for (int p = 0; p < presentPeerIds.Count; p++)
            {
                if (presentPeerIds[p] == _owner[i])
                {
                    stillHere = true;
                    break;
                }
            }

            if (!stillHere)
            {
                _owner[i] = peerId;
                return i;
            }
        }

        return -1;
    }
}
