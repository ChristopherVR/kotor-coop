namespace KotorCoop.Plugin;

// ---- companion damage TAKEN (version 63, region 0x4A00..0x4A90) -----------------------------
//
// A companion companion_local_combat has released fights through its own engine AI on a
// CLIENT and can take real hits — but the host's authoritative body never saw any of it, so
// the host's higher figure overwrote the client's own damage on the very next mirror pass
// (PartyPresenceController.DriveOne's "the host owns companion health" write). This is the
// companion sibling of the damage-DEALT ledger in SharedBlock.Combat.cs: read that region's
// header comment and docs/research/peer-damage-ledger.md first — the same "cumulative total,
// never a health sample" shape, for the identical reason. See
// docs/research/companion-state-sync.md §13.
//
// UNLIKE the enemy ledger, there is no per-module EPOCH here: an NPC roster slot names the
// same companion for the whole session, module transition or not (PartyPresenceController
// already resolves companions by roster slot for exactly that reason), so nothing needs
// invalidating when the world changes. A per-publish GENERATION still exists, for the ordinary
// "tell a legitimate restart from a stale datagram" reason every cumulative channel here needs.
//
// Taken side (plugin -> agent): PhantomHitHook's companion watch, one row per currently-watched
// presence slot (SharedBlock.PartyPresenceCapacity, 4) — only ever nonzero while
// PartyPresenceController has that slot RELEASED; a mirror pass writes hit points directly and
// never touches this region, so there is nothing to suppress here the way SuppressDealt guards
// the enemy ledger's self-feeding risk.
// Owed side (agent -> plugin): the total every peer has dealt to each of the HOST's own
// companions, keyed by NPC ROSTER SLOT (ProtocolConstants.PartyRosterSlots, 9) rather than
// presence slot — the identity CompanionDamageController resolves against directly, the same
// walk PartyPresenceController.ResolveRosterObjectId already does.
internal partial struct SharedBlock
{
    /// <summary>Companion presence slots this instance can watch at once — matches
    /// <see cref="PartyPresenceCapacity"/>, since a slot can only ever be released while it is
    /// also a tracked presence slot.</summary>
    public const int CompanionDamageSlotCapacity = PartyPresenceCapacity;

    /// <summary>NPC roster slots a HOST can carry companion debt for at once — matches
    /// <see cref="PartyRosterSlots"/>, the engine's own bound.</summary>
    public const int CompanionDamageOwedCapacity = PartyRosterSlots;

    /// <summary>Plugin -&gt; agent. How many of <see cref="OffsetCompanionDamageSlots"/> are
    /// valid.</summary>
    public const int OffsetCompanionDamageCount = 0x4A00;

    /// <summary>
    /// Plugin -&gt; agent. Bumped whenever the watch restarts (a module change invalidates
    /// every companion, or a companion is reclaimed and later released again) — the sender's
    /// own reset marker, exactly like <c>OffsetDamageDealtGeneration</c>.
    /// </summary>
    public const int OffsetCompanionDamageGeneration = 0x4A04;

    /// <summary>Plugin -&gt; agent. rosterSlotPlusOne u32 @0 (1..9, 0 = unused; written last),
    /// cumulative damage taken u32 @4, serial u32 @8. Occupies 0x4A08..0x4A38.</summary>
    public const int OffsetCompanionDamageSlots = 0x4A08;

    public const int CompanionDamageSlotStride = 0x0C;

    /// <summary>Agent -&gt; plugin. How many of <see cref="OffsetCompanionDamageOwedSlots"/> are
    /// valid.</summary>
    public const int OffsetCompanionDamageOwedCount = 0x4A40;

    /// <summary>Agent -&gt; plugin. Same shape as the taken side, keyed by NPC roster slot: the
    /// total all peers have dealt to each of THIS host's own companions. rosterSlotPlusOne u32
    /// @0 (written last), cumulative u32 @4. Occupies 0x4A44..0x4A8C.</summary>
    public const int OffsetCompanionDamageOwedSlots = 0x4A44;

    public const int CompanionDamageOwedStride = 0x08;

    /// <summary>Plugin -&gt; agent. Companion-damage applications actually run — diagnostic,
    /// the companion sibling of <c>OffsetDamageOwedApplied</c>.</summary>
    public const int OffsetCompanionDamageApplied = 0x4A8C;
}
