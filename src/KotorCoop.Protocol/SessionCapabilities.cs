namespace KotorCoop.Protocol;

/// <summary>
/// The replication channels a session is allowed to use, after content compatibility has
/// been judged. A mod that only one peer has does not have to end the session — it ends
/// the specific channel its difference would corrupt. The agent's pump consults this
/// before each replication step, so a differently-modded pair still plays, minus whatever
/// their difference makes unsafe.
///
/// A full set is <see cref="All"/>; the empty set means "refused" (see
/// <see cref="CompatibilityVerdict"/>).
/// </summary>
[Flags]
public enum SessionCapabilities
{
    None = 0,

    /// <summary>Avatar replication (phantoms). Safe across almost any mod difference — a
    /// creature spawned from a co-op blueprint at a replicated position.</summary>
    Phantoms = 1 << 0,

    /// <summary>The world/quest global variables. Refused if the globals catalogue differs,
    /// because the (type, index) keys would then name different variables.</summary>
    WorldState = 1 << 1,

    /// <summary>Door open/lock state. Keyed on object id, so it rides on the same
    /// same-save/same-GIT assumption enemies do.</summary>
    Doors = 1 << 2,

    /// <summary>Host-authoritative enemy health and death. Object-id keyed.</summary>
    Enemies = 1 << 3,

    /// <summary>Shared ground items. Object-id keyed for pickup detection.</summary>
    GroundItems = 1 << 4,

    /// <summary>Quest journal entries. Degraded (not refused) on a journal mismatch: the
    /// channel filters to tags present locally rather than appending phantom entries.</summary>
    Journal = 1 << 5,

    /// <summary>Writing the peer's appearance.2da row onto their phantom. Dropped if the
    /// tables differ, because a row past the local table is a crash risk.</summary>
    Appearance = 1 << 6,

    /// <summary>Dressing the phantom in the peer's equipment. Already soft-fails per item,
    /// so a difference only means some items silently do not appear.</summary>
    Equipment = 1 << 7,

    /// <summary>Host-authoritative party companions mirrored onto the client's frozen
    /// copies. Keyed by NPC slot, so it needs the same save (dropped on a save mismatch).</summary>
    Party = 1 << 8,

    /// <summary>Conversation ownership and native dialogue mirroring. Mirroring executes
    /// local dialogue resources, so gameplay-content differences drop this channel.</summary>
    Dialogue = 1 << 9,

    /// <summary>Shared party gold and PC experience. Plain scalars, not save-keyed ids, so it
    /// survives a save mismatch. Gameplay-content differences drop shared progression,
    /// since campaign scripts may use these scalars differently.</summary>
    Progression = 1 << 10,

    /// <summary>The shared party item pool (<c>CSWPartyTable+0x118</c>). Content-keyed
    /// (resref + identity, not object id — see docs/research/party-pool-sync.md), so it is not
    /// part of <see cref="ContentCompatibility.ObjectIdKeyed"/>: a party-pool item is created
    /// independently on each instance and never shares an engine id across peers the way a
    /// GIT-placed door or container does, so a save/module mismatch does not touch it. Off by
    /// default until live-verified (<c>party_pool_sync</c>).</summary>
    PartyPool = 1 << 11,

    /// <summary>Merchant store stock (<c>CSWSStore+0x230</c>). Object-id keyed (a store is a
    /// real, GIT-placed <c>CGameObject</c> — see docs/research/store-sync.md §1.2) plus the same
    /// content key the party pool uses for a stack's identity. Off by default until live-verified
    /// (<c>store_sync</c>).</summary>
    Store = 1 << 12,

    All = Phantoms | WorldState | Doors | Enemies | GroundItems | Journal | Appearance | Equipment
        | Party | Dialogue | Progression | PartyPool | Store,
}
