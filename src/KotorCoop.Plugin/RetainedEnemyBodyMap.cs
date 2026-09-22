using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

internal readonly record struct RetainedEnemyBody(uint OwnerId, uint LocalId, int RosterSlot, double LastSeen);

/// <summary>Identity aliases for campaign-owned roster bodies driven through the enemy channel.
/// This registry owns no engine object and exposes no destroy operation. Slot identity is
/// checked on every translation so a dismissed slot reused by a later campaign state cannot
/// inherit an old owner's damage or presence.</summary>
internal sealed class RetainedEnemyBodyMap(int capacity)
{
    private readonly RetainedEnemyBody?[] _entries = new RetainedEnemyBody?[capacity];
    private readonly RetainedLocalQuarantine?[] _quarantined = new RetainedLocalQuarantine?[capacity];
    private readonly uint[] _ownerTombstones = new uint[capacity];
    private bool _quarantineAll;
    private bool _ownerTombstoneOverflow;

    private readonly record struct RetainedLocalQuarantine(uint LocalId, int RosterSlot);

    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].HasValue)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public bool Register(uint ownerId, uint localId, int rosterSlot, double now)
    {
        if (!PartyCensusRule.IsRosterId(ownerId)
            || !PartyCensusRule.IsRosterId(localId)
            || rosterSlot is not (4 or 5))
        {
            return false;
        }

        // Keep this local id fail-closed even if current-proof pruning removes the routable
        // alias before the next EnemyPresence pass. A hook in that frame must not mistake the
        // retained local id for a host id and forward damage/effects under the wrong identity.
        // Quarantine is only a routing guard; it never owns or destroys the native body.
        QuarantineLocal(localId, rosterSlot);

        int free = -1;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } entry)
            {
                if (entry.OwnerId == ownerId)
                {
                    if (entry.RosterSlot != rosterSlot || IsLocalUsed(localId, i))
                    {
                        return false;
                    }

                    _entries[i] = new(ownerId, localId, rosterSlot, now);
                    return true;
                }

                if (entry.LocalId == localId)
                {
                    return false;
                }
            }
            else if (free < 0)
            {
                free = i;
            }
        }

        if (free < 0)
        {
            return false;
        }

        _entries[free] = new(ownerId, localId, rosterSlot, now);
        return true;
    }

    public bool TryGetOwnerEntry(uint ownerId, out RetainedEnemyBody entry)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } value && value.OwnerId == ownerId)
            {
                entry = value;
                return true;
            }
        }

        entry = default;
        return false;
    }

    public bool TryGetLocalEntry(uint localId, out RetainedEnemyBody entry)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } value && value.LocalId == localId)
            {
                entry = value;
                return true;
            }
        }

        entry = default;
        return false;
    }

    /// <summary>Remembers a possible retained party id without making it routable. This
    /// quarantine prevents hooks from forwarding that local id as if it were the host id
    /// while the host enemy row or faction write is temporarily absent.</summary>
    public bool QuarantineLocal(uint localId, int rosterSlot)
    {
        if (!PartyCensusRule.IsRosterId(localId) || rosterSlot is not (4 or 5))
        {
            return false;
        }

        int free = -1;
        for (int i = 0; i < _quarantined.Length; i++)
        {
            if (_quarantined[i] is { } value)
            {
                if (value.LocalId == localId)
                {
                    _quarantined[i] = new(localId, rosterSlot);
                    return true;
                }

            }
            else if (free < 0)
            {
                free = i;
            }
        }

        if (free < 0)
        {
            _quarantineAll = true;
            return true;
        }

        _quarantined[free] = new(localId, rosterSlot);
        return true;
    }

    public bool IsQuarantinedLocal(uint localId)
    {
        if (_quarantineAll)
        {
            return true;
        }

        for (int i = 0; i < _quarantined.Length; i++)
        {
            if (_quarantined[i] is { } value && value.LocalId == localId)
                return true;
        }

        return false;
    }

    public bool ReleaseQuarantineIfActive(uint localId, int rosterSlot, bool positivelyProvenActive)
    {
        if (!positivelyProvenActive || _quarantineAll)
        {
            return false;
        }

        for (int i = 0; i < _quarantined.Length; i++)
        {
            if (_quarantined[i] is { } value
                && value.LocalId == localId
                && value.RosterSlot == rosterSlot)
            {
                _quarantined[i] = null;
                return true;
            }
        }

        return false;
    }

    public uint ToLocal(uint ownerId, Func<int, uint> currentRosterId)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is not { } entry || entry.OwnerId != ownerId)
            {
                continue;
            }

            if (currentRosterId(entry.RosterSlot) != entry.LocalId)
            {
                RememberOwnerTombstone(entry.OwnerId);
                _entries[i] = null;
                return 0;
            }

            return entry.LocalId;
        }

        return 0;
    }

    public uint ToOwner(uint localId, Func<int, uint> currentRosterId)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is not { } entry || entry.LocalId != localId)
            {
                continue;
            }

            if (currentRosterId(entry.RosterSlot) != entry.LocalId)
            {
                RememberOwnerTombstone(entry.OwnerId);
                _entries[i] = null;
                return 0;
            }

            return entry.OwnerId;
        }

        return 0;
    }

    public bool ContainsOwner(uint ownerId, Func<int, uint> currentRosterId) =>
        ToLocal(ownerId, currentRosterId) != 0;

    public bool ContainsLocal(uint localId, Func<int, uint> currentRosterId) =>
        ToOwner(localId, currentRosterId) != 0;

    public void Seen(uint ownerId, double now)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } entry && entry.OwnerId == ownerId)
            {
                _entries[i] = entry with { LastSeen = now };
                return;
            }
        }
    }

    public void ForgetOwner(uint ownerId)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } entry && entry.OwnerId == ownerId)
            {
                RememberOwnerTombstone(entry.OwnerId);
                _entries[i] = null;
                return;
            }
        }
    }

    public int Sweep(double now, double staleSeconds)
    {
        int forgotten = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } entry && now - entry.LastSeen >= staleSeconds)
            {
                RememberOwnerTombstone(entry.OwnerId);
                _entries[i] = null;
                forgotten++;
            }
        }

        return forgotten;
    }

    public int PruneInvalidSlots(Func<int, uint> currentRosterId)
    {
        int forgotten = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } entry && currentRosterId(entry.RosterSlot) != entry.LocalId)
            {
                RememberOwnerTombstone(entry.OwnerId);
                _entries[i] = null;
                forgotten++;
            }
        }

        return forgotten;
    }

    /// <summary>Drops aliases unless the caller can positively re-prove their current host
    /// enemy and local retained-slot identity. A missing census therefore stops damage mapping
    /// immediately instead of waiting for the ordinary stale lease.</summary>
    public int PruneUnproven(Func<RetainedEnemyBody, bool> isCurrentlyProven)
    {
        int forgotten = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } entry && !isCurrentlyProven(entry))
            {
                RememberOwnerTombstone(entry.OwnerId);
                _entries[i] = null;
                forgotten++;
            }
        }

        return forgotten;
    }

    /// <summary>Drops aliases on module/session teardown; the campaign owns every body.</summary>
    public int ReleaseSession()
    {
        int forgotten = Count;
        Array.Clear(_entries);
        Array.Clear(_quarantined);
        Array.Clear(_ownerTombstones);
        _quarantineAll = false;
        _ownerTombstoneOverflow = false;
        return forgotten;
    }

    public void Clear()
    {
        Array.Clear(_entries);
        Array.Clear(_quarantined);
        Array.Clear(_ownerTombstones);
        _quarantineAll = false;
        _ownerTombstoneOverflow = false;
    }

    public int ClearAliasesKeepGuards()
    {
        int forgotten = Count;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i] is { } entry)
            {
                RememberOwnerTombstone(entry.OwnerId);
            }
        }

        Array.Clear(_entries);
        return forgotten;
    }

    public bool IsOwnerTombstoned(uint ownerId)
    {
        if (_ownerTombstoneOverflow)
        {
            return true;
        }

        for (int i = 0; i < _ownerTombstones.Length; i++)
        {
            if (_ownerTombstones[i] == ownerId)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsLocalUsed(uint localId, int except)
    {
        for (int i = 0; i < _entries.Length; i++)
        {
            if (i != except && _entries[i] is { } entry && entry.LocalId == localId)
            {
                return true;
            }
        }

        return false;
    }

    private void RememberOwnerTombstone(uint ownerId)
    {
        if (ownerId == 0 || IsOwnerTombstoned(ownerId))
        {
            return;
        }

        for (int i = 0; i < _ownerTombstones.Length; i++)
        {
            if (_ownerTombstones[i] == 0)
            {
                _ownerTombstones[i] = ownerId;
                return;
            }
        }

        _ownerTombstoneOverflow = true;
    }
}
