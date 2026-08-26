namespace KotorCoop.Plugin;

// Companion roster reconciliation. See SharedBlockView.cs for the class declaration and
// shared helpers.
internal sealed unsafe partial class SharedBlockView
{
    // ---- companion roster reconciliation ----------------------------------------
    //
    // One packed dword per direction; see SharedBlock's region comment for why the two masks
    // share a word rather than sitting in fields of their own.

    /// <summary>
    /// Plugin-written; this instance's roster. Setting it stamps the valid bit, because the
    /// only way to write this at all is to have read the party table — a caller that could not
    /// read one calls <see cref="ClearLocalPartyRoster"/> instead, and the two are deliberately
    /// different operations so "no reading" can never be spelled the same as "empty roster".
    /// </summary>
    public void SetLocalPartyRoster(uint availableMask, uint partyMask) =>
        *U32(SharedBlock.OffsetLocalPartyRoster) =
            SharedBlock.PartyRosterValid
            | ((availableMask & SharedBlock.PartyRosterSlotMask) << SharedBlock.PartyRosterAvailableShift)
            | ((partyMask & SharedBlock.PartyRosterSlotMask) << SharedBlock.PartyRosterPartyShift);

    public void ClearLocalPartyRoster() => *U32(SharedBlock.OffsetLocalPartyRoster) = 0;

    /// <summary>Agent-written; the host's roster. False when nobody is telling this instance
    /// what its roster should be, in which case the masks are meaningless and untouched.</summary>
    public bool TryGetHostPartyRoster(out uint availableMask, out uint partyMask)
    {
        uint packed = *U32(SharedBlock.OffsetHostPartyRoster);
        availableMask = (packed >> SharedBlock.PartyRosterAvailableShift) & SharedBlock.PartyRosterSlotMask;
        partyMask = (packed >> SharedBlock.PartyRosterPartyShift) & SharedBlock.PartyRosterSlotMask;
        return (packed & SharedBlock.PartyRosterValid) != 0;
    }

    /// <summary>Plugin-written; this instance's own party-table object id for
    /// <paramref name="slot"/> — see SharedBlock's "party-roster body identity" region comment.
    /// Callers are expected to pass 0 for a slot not currently in the local party (the
    /// convention the field itself documents), not to gate the call on that here.</summary>
    public void SetLocalPartyRosterObjectId(int slot, uint objectId) =>
        *U32(SharedBlock.OffsetLocalPartyRosterObjectIds
            + (Clamp(slot, SharedBlock.PartyRosterSlots) * 4)) = objectId;

    /// <summary>Agent-written; the HOST's own party-table object id for <paramref name="slot"/>,
    /// or 0 if never published (an older peer build) or the host has no live body for that slot
    /// right now. Not gated on <see cref="TryGetHostPartyRoster"/>'s own valid bit here — a
    /// stale, no-longer-authoritative id is exactly as harmless as a stale roster mask, because
    /// every consumer (<c>PartyRosterController.Reconcile</c>) only ever reads this alongside a
    /// mask it already checked.</summary>
    public uint GetHostPartyRosterObjectId(int slot) =>
        *U32(SharedBlock.OffsetHostPartyRosterObjectIds
            + (Clamp(slot, SharedBlock.PartyRosterSlots) * 4));

    /// <summary>Zeroes every slot's id — paired with <see cref="ClearLocalPartyRoster"/> so a
    /// module transition (which reassigns every object id) never lets a stale one survive into
    /// the new world.</summary>
    public void ClearLocalPartyRosterObjectIds()
    {
        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            *U32(SharedBlock.OffsetLocalPartyRosterObjectIds + (slot * 4)) = 0;
        }
    }

    /// <summary>Zeroes every slot's id — paired with <see cref="ClearHostPartyRoster"/> for the
    /// same reason <see cref="ClearLocalPartyRosterObjectIds"/> exists on the local side.</summary>
    public void ClearHostPartyRosterObjectIds()
    {
        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            *U32(SharedBlock.OffsetHostPartyRosterObjectIds + (slot * 4)) = 0;
        }
    }

    public uint PartyRosterAdds
    {
        get => *U32(SharedBlock.OffsetPartyRosterAdds);
        set => *U32(SharedBlock.OffsetPartyRosterAdds) = value;
    }

    public uint PartyRosterRemoves
    {
        get => *U32(SharedBlock.OffsetPartyRosterRemoves);
        set => *U32(SharedBlock.OffsetPartyRosterRemoves) = value;
    }

    public uint PartyRosterStatus
    {
        get => *U32(SharedBlock.OffsetPartyRosterStatus);
        set => *U32(SharedBlock.OffsetPartyRosterStatus) = value;
    }

}
