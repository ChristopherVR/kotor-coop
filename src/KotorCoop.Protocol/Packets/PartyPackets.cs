namespace KotorCoop.Protocol;

// The host's authoritative party companions: live presence and the roster masks.
// Matches PacketCodec.Party.cs.

/// <summary>
/// One party companion as the host sees it. Keyed by <paramref name="SlotIndex"/> (the
/// NPC roster slot, 0..8) rather than an object id, because the slot is identical across
/// two instances of the same save while the runtime id is not. Absolute position, so a
/// lost packet is superseded next tick. <paramref name="OneShotSerial"/> and
/// <paramref name="OneShotAnimation"/> carry the companion's latest fire-and-forget
/// (a swing, a parry), same scheme as the enemy channel.
///
/// <paramref name="EquippedBody"/>/<paramref name="EquippedRightWeapon"/>/
/// <paramref name="EquippedLeftWeapon"/> (2026-08-27, docs/research/companion-state-sync.md,
/// roadmap item 12) are the companion's real equipped gear, blueprint resrefs exactly like
/// <see cref="PlayerState.EquippedBody"/>'s own three fields — reused rather than reinvented
/// because it is the same fact (a body's visible slots) about a different kind of body. Empty
/// = slot empty, unreadable, or the sender's <c>companion_gear_sync</c> is off; a client only
/// ever APPLIES these when it read a nonempty value, so an old sender (or the flag being off)
/// degrades to exactly today's behaviour rather than stripping a companion's gear. HOST ONLY —
/// a client never populates these on its own outgoing companion state, since it never drives
/// the companion's inventory.
/// </summary>
public readonly record struct PartyMemberState(
    byte SlotIndex, Vector3f Position, float Facing, uint Animation, int Health,
    uint OneShotSerial = 0, ushort OneShotAnimation = 0,
    string EquippedBody = "", string EquippedRightWeapon = "", string EquippedLeftWeapon = "",
    uint OccupantGeneration = 0);

/// <summary>
/// The host's authoritative party. In co-op the host owns the companions (its own engine
/// AI drives them); every client freezes its own copies (solo mode) and mirrors these, so
/// Trask and Carth do not drift into two uncoordinated versions.
/// </summary>
public sealed record PartySnapshotPacket(long ServerTimeMs, IReadOnlyList<PartyMemberState> Members,
    uint HostSessionId = 0, uint HostLoadSerial = 0, uint HostNativeEpoch = 0);

/// <summary>
/// One instance's whole companion roster, as two nine-bit masks over the engine's NPC slots.
///
/// ⚠ THE SLOT INDEX IS THE COMPANION'S IDENTITY, and nothing else on the wire names one.
/// KOTOR's roster is a fixed nine-entry table (NPC_BASTILA .. NPC_ZAALBAR) and both instances
/// loaded the same save, so slot N means the same character on both. That is also the key
/// PartyPresenceController already resolves companions by — see its "companions are keyed by
/// NPC roster slot (identical across two instances of the same save)" — so this channel and
/// the companion mirror agree on identity by construction rather than by convention.
///
/// <paramref name="AvailableMask"/> = recruited: the story has handed this companion over,
/// whether or not they are in the active party right now. Read from party table +0x30
/// (<c>int[9]</c>, nonzero = available; <c>CSWPartyTable::AddNPC</c> at 0x00564350 writes the
/// literal 1). Every roster operation in the engine refuses a slot that is not available.
///
/// <paramref name="PartyMask"/> = in the ACTIVE party: the at-most-two companions walking
/// behind the player. Read from party table +0x00 (count, max 2) and +0x04
/// (<c>int[2]</c> of NPC slot indices — slot indices, NOT object ids).
///
/// <paramref name="ObjectIds"/> = the sender's own party-table object id per slot (party
/// table +0x0C, <c>int[9]</c>, written by <c>AddMember</c> and retained after dismissal). A
/// nonzero id may name a body even when its slot is absent from <paramref name="PartyMask"/>
/// or <paramref name="AvailableMask"/>. Object ids are assigned deterministically in GIT order, so
/// (the same assumption <c>EnemyPresenceController</c>/<c>MaterialisedEnemies</c> already lean
/// on throughout this project) a companion who was ALSO a placed NPC before recruitment has the
/// SAME id on every instance that loaded the same save — this is what lets a peer recognise its
/// own already-placed copy of a just-recruited companion and reuse it instead of spawning a
/// second, duplicate body next to it. <c>null</c> means the field was never populated (an older
/// build) — callers must not treat that
/// as "the id is 0" the way <see cref="ObjectIdFor"/> reports it; 0 either way means "do not
/// reuse", so no separate check is needed in practice.
/// </summary>
public readonly record struct PartyOccupantIdentity(uint Generation, string Template, bool Replaced = false, string CacheFingerprint = "");

public readonly record struct PartyRoster(ushort AvailableMask, ushort PartyMask, uint[]? ObjectIds = null,
    PartyOccupantIdentity[]? Occupants = null, uint HostSessionId = 0, uint HostLoadSerial = 0, uint HostNativeEpoch = 0, uint RefreshSaveIdentity = 0, uint RefreshRevision = 0)
{
    /// <summary>Nothing recruited, nobody in the party. What a peer's roster reads as before
    /// the first level has arrived, and what must never be applied as if it were real.</summary>
    public static readonly PartyRoster Empty = new(0, 0, null);

    /// <summary>Only the nine slots the engine has (<see cref="ProtocolConstants.PartyRosterSlots"/>).
    /// Anything above them is a malformed sender, and is dropped rather than acted on.</summary>
    public const ushort SlotMask = (1 << ProtocolConstants.PartyRosterSlots) - 1;

    public bool IsAvailable(int slot) => (AvailableMask & (1 << slot)) != 0;

    public bool IsInParty(int slot) => (PartyMask & (1 << slot)) != 0;

    /// <summary>This roster's own object id for <paramref name="slot"/>, or 0 if unknown, out
    /// of range, or the slot has no id published (no body exists, or the sender predates this
    /// field). Never throws on a malformed index — the same defensive shape
    /// <see cref="IsAvailable"/>/<see cref="IsInParty"/> already use.</summary>
    public uint ObjectIdFor(int slot) =>
        ObjectIds is not null && (uint)slot < (uint)ObjectIds.Length ? ObjectIds[slot] : 0;

    /// <summary>
    /// Hand-written VALUE equality over <see cref="ObjectIds"/>, comparing per slot through
    /// <see cref="ObjectIdFor"/> rather than letting the record synthesize a reference compare
    /// on the backing array. Without this, a decoded roster (always a freshly-allocated
    /// nine-element array, every entry 0 when nothing was published) would never equal a
    /// hand-built one with <c>ObjectIds: null</c> — same value, different reference — which
    /// broke <c>PartyRosterRoundTrips</c>/<c>EmptyPartyRosterRoundTrips</c> the moment this
    /// field was added; <c>null</c> and "all zero" are the same roster.
    /// </summary>
    public PartyOccupantIdentity OccupantFor(int slot) =>
        Occupants is not null && (uint)slot < (uint)Occupants.Length
            ? Occupants[slot] with { Template = Occupants[slot].Template ?? "", CacheFingerprint = Occupants[slot].CacheFingerprint ?? "" } : new(0, "");

    public bool Equals(PartyRoster other) =>
        AvailableMask == other.AvailableMask
        && PartyMask == other.PartyMask
        && RefreshSaveIdentity == other.RefreshSaveIdentity && RefreshRevision == other.RefreshRevision
        && HostSessionId == other.HostSessionId && HostLoadSerial == other.HostLoadSerial && HostNativeEpoch == other.HostNativeEpoch
        && ObjectIdsEqual(other);

    public override int GetHashCode() => HashCode.Combine(AvailableMask, PartyMask);

    private bool ObjectIdsEqual(PartyRoster other)
    {
        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            if (ObjectIdFor(slot) != other.ObjectIdFor(slot) || OccupantFor(slot) != other.OccupantFor(slot))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Slots this roster has in the party that <paramref name="local"/> does not — the adds a
    /// peer owes. Availability is deliberately NOT considered here: whether the local engine
    /// can honour an add is the local engine's business, and it refuses on its own.
    /// </summary>
    public ushort AddsAgainst(PartyRoster local) =>
        (ushort)(PartyMask & ~local.PartyMask & SlotMask);

    /// <summary>Slots <paramref name="local"/> has in the party that this roster does not —
    /// the removes a peer owes. This is the direction that fixes a dismissal, and the one that
    /// must run FIRST: the party holds at most two companions, so an add can be refused purely
    /// because a stale member is still occupying the seat.</summary>
    public ushort RemovesAgainst(PartyRoster local) =>
        (ushort)(local.PartyMask & ~PartyMask & SlotMask);
}

/// <summary>
/// host -&gt; all: the authoritative companion roster, broadcast each tick.
///
/// No module rides along, and that is deliberate rather than an omission: the party table is
/// SAVE state, not module state (it lives in PARTYTABLE.res and survives every transition), so
/// unlike an object id or a tag it means the same thing to a peer standing anywhere. The
/// enemy and lock channels carry a module because ids and tags do not.
/// </summary>
public sealed record PartyRosterPacket(long ServerTimeMs, PartyRoster Roster);
