namespace KotorCoop.Plugin;

// Party presence, the phantom id table, companion roster reconciliation and party-roster
// body identity. See SharedBlock.cs for the struct header.
internal partial struct SharedBlock
{
    // ---- party presence (version 20) --------------------------------------------
    //
    // The host's companions, driven on the mirroring instance by the plugin instead of
    // the agent's raw external position writes (which never touched the client twin, so
    // the rendered body trailed on the engine's 512 ms ease and stepped between ticks).
    // Keyed by NPC roster slot — identical across two instances of the same save, while
    // runtime object ids are not; the mirroring plugin resolves the slot against its OWN
    // party table.

    /// <summary>Agent -&gt; plugin. How many companion presence entries are valid.</summary>
    public const int OffsetPartyPresenceCount = 0x1C20;

    /// <summary>Agent -&gt; plugin. roster slot + 1 u32 @0 (0 = empty, written last),
    /// position 3xf32 @4, facing f32 @0x10, animation u32 @0x14, health i32 @0x18,
    /// one-shot serial u32 @0x1C, one-shot animation u32 @0x20. Occupies 0x1C30..0x1CD0.</summary>
    public const int OffsetPartyPresenceSlots = 0x1C30;

    public const int PartyPresenceStride = 0x28;

    public const int PartyPresenceCapacity = 4;


    // ---- phantom id table (relocated, region 0x1F80..0x1FA0) ---------------------
    //
    // The real per-slot phantom object ids. See OffsetPhantomIds for why they could not
    // stay at 0x264: that "array" only ever had room for one entry before the next field
    // began. Placed in the block's unused tail so no existing offset moves — an agent that
    // has not been rebuilt keeps reading slot 0 from 0x264, which the plugin still mirrors.

    /// <summary>Plugin -&gt; agent. Object ids of the spawned phantoms, one per remote slot,
    /// <see cref="SlotCapacity"/> x uint32. 0 = no body in that slot. Occupies
    /// 0x1F80..0x1FA0.</summary>
    public const int OffsetPhantomIdTable = 0x1F80;

    // ---- companion roster reconciliation (version 34, region 0x3300..0x3320) ----------
    //
    // The channel AddPartyMember / RemovePartyMember never had. A conversation that recruits or
    // dismisses a companion runs its scripts on ONE instance, so the other instance's party
    // table never hears — and the party table is what the whole companion channel keys on
    // (PartyPresenceController resolves the host's "NPC slot 2" against its OWN table). After a
    // divergence the mirror drives the wrong body, or none.
    //
    // ⚠ PACKED INTO ONE DWORD PER DIRECTION ON PURPOSE. These words are read by one process
    // while the other writes them. Two separate fields for "recruited" and "in the party" could
    // be sampled half-updated, which would need a serial and a re-read to detect (the
    // discipline the dialogue node pair uses). A single aligned dword store cannot tear on x86,
    // so the question does not arise. Nine slots is the engine's own bound — CSWPartyTable::
    // AddMember at 0x00565648 refuses nNPC >= 9 — so both masks fit in one word twice over.
    //
    // ⚠ BIT 31 IS NOT "SOMETHING IS RECRUITED", IT IS "SOMEBODY WROTE THIS". A game at the main
    // menu has no party table and leaves the word at zero, which is bit-identical to a real
    // roster with nothing recruited. Without the bit, a host would publish "nobody is in your
    // party" during a loading screen and its client would obediently dismiss everyone.

    /// <summary>Plugin -&gt; agent. This instance's roster, republished every frame from its own
    /// party table. Bits 0..8 recruited, bits 16..24 in the active party, bit 31 valid.</summary>
    public const int OffsetLocalPartyRoster = 0x3300;

    /// <summary>Agent -&gt; plugin. The HOST's roster, same packing. Zero (not merely
    /// all-masks-clear, but bit 31 clear) means "nobody is telling this instance what its
    /// roster should be" — the host itself, a client before the first level arrives, a
    /// disconnected agent, a disabled channel — and nothing is reconciled.</summary>
    public const int OffsetHostPartyRoster = 0x3304;

    /// <summary>Plugin -&gt; agent. Roster adds this instance has performed. Diagnostic, and
    /// the one that says the channel WORKED rather than merely arrived.</summary>
    public const int OffsetPartyRosterAdds = 0x3308;

    /// <summary>Plugin -&gt; agent. Roster removes this instance has performed.</summary>
    public const int OffsetPartyRosterRemoves = 0x330C;

    /// <summary>Plugin -&gt; agent. The last refusal this instance hit, so a reconciliation that
    /// keeps being refused is visible without a log dive. 0 = nothing refused.</summary>
    public const int OffsetPartyRosterStatus = 0x3310;

    public const int PartyRosterAvailableShift = 0;
    public const int PartyRosterPartyShift = 16;
    public const uint PartyRosterSlotMask = 0x1FF;
    public const uint PartyRosterValid = 0x8000_0000;

    /// <summary>Roster slots. The engine's bound, not a chosen cap: CSWPartyTable::AddMember
    /// (0x00565620) refuses nNPC &lt; 0 or &gt;= 9, and availability at party table +0x30 is
    /// int[9].</summary>
    public const int PartyRosterSlots = 9;


    // ---- party-roster body identity (version 53, region 0x3F28..0x3F70, no TotalSize growth) -
    //
    // docs/research/dialogue-consequences.md's 2026-08-24 "stale mirrored body" entry: a
    // companion recruited on one instance through the real dialogue path (dialogue_recruit_sync)
    // used to always be recreated on the peer from scratch (AddAvailableNpcByTemplate +
    // SpawnAvailableNpc) — even when the peer's own game had ALREADY independently placed the
    // SAME companion in the world at module load (GIT-identical object id, the same assumption
    // EnemyPresenceController/MaterialisedEnemies already lean on elsewhere in this project).
    // That old body was never told it had just become a party member, so it kept standing
    // exactly where it always was, right next to the freshly spawned (and genuinely party-
    // seated) duplicate — a second, motionless copy of the same companion.
    //
    // These two nine-slot arrays let PartyRosterController check, before ever calling
    // AddAvailableNpcByTemplate, whether a live body with the SAME id the peer's own party table
    // already holds for that slot is standing right here too — if so, that body is reused
    // directly (Odyssey.TrySetSlotAvailable + AddPartyMemberBySlot) and the whole off-stage
    // recruit + fresh spawn is skipped, so no duplicate is ever created in the first place.
    //
    // Rides in the free tail after the mid-session-load-request region (0x3F24 + 4 = 0x3F28,
    // TotalSize is 0x4000, leaving 0xD8 bytes free before this; this region uses 0x48 of them).
    // No TotalSize growth, no relayout of anything else. NEEDS-LIVE (see the design doc's own
    // "flagged, not fixed" note) — nothing about this mechanism has been exercised against the
    // real GOG install yet.

    /// <summary>Plugin -&gt; agent. This instance's own party-table object id per NPC roster
    /// slot (party table +0x0C, <see cref="Odyssey.GetPartyRosterObjectId"/>'s own field), 0 for
    /// a slot not currently in this instance's <see cref="OffsetLocalPartyRoster"/> party mask.
    /// int[9], one dword per slot, republished every frame alongside the roster masks
    /// (deliberately a LEVEL, not an edge — the same reasoning <see cref="OffsetLocalPartyRoster"/>'s
    /// own comment gives).</summary>
    public const int OffsetLocalPartyRosterObjectIds = 0x3F28;

    /// <summary>Agent -&gt; plugin. The HOST's per-slot object ids, same packing and the same
    /// "0 = not currently in the host's active party for that slot" convention as
    /// <see cref="OffsetLocalPartyRosterObjectIds"/>. Only meaningful together with
    /// <see cref="OffsetHostPartyRoster"/>'s own valid bit — cleared (all zero) whenever that
    /// word is.</summary>
    public const int OffsetHostPartyRosterObjectIds = 0x3F4C;


    // ---- companion equipment (version 54, region 0x4000..0x40C0) -----------------
    //
    // docs/research/companion-state-sync.md (2026-08-27, roadmap item 12): the gap left by
    // party_drive mirroring only position/health/animation. When companion_local_combat
    // releases a companion to a CLIENT's own engine AI, that AI attacks with whatever gear the
    // CLIENT's copy of the body happens to be wearing — which can be stale the moment the host
    // player has re-equipped that companion (a workbench upgrade, a looted weapon) since the
    // two instances last agreed. This region lets the host publish the companion's REAL
    // equipped gear (agent -> plugin) so PartyPresenceController can dress the mirrored body
    // through the identical Odyssey.EquipItemFromTemplate path OffsetEquipSlots already drives
    // for a peer's phantom.
    //
    // Keyed by PRESENCE SLOT INDEX (0..3, PartyPresenceCapacity — the same index
    // OffsetPartyPresenceSlots uses, i.e. the position in PartySnapshotPacket.Members the host
    // sent it at), NOT by NPC roster slot: this is a separate, parallel array rather than a
    // widened PartyPresenceStride so the existing 0x28-byte movement layout at
    // OffsetPartyPresenceSlots does not have to move (nothing else in this file relocates an
    // existing offset for a later addition — see every "region... no TotalSize growth" comment
    // above). Growing TotalSize by 0x100 costs one more page of the game's own heap and nothing
    // else; the agent finds the block by signature and reads fixed offsets regardless of size.
    //
    // Empty resref = "leave the slot alone" (companion_gear_sync off, an unreadable slot, or
    // the sender predates this field) — the exact same "absent is a no-op, never a strip"
    // convention OffsetEquipSlots already uses for a phantom.

    /// <summary>Agent -&gt; plugin. 4 presence slots x 3 resrefs (body, right weapon, left
    /// weapon) x 16 ascii bytes, same order as <see cref="EquipBody"/>/
    /// <see cref="EquipRightWeapon"/>/<see cref="EquipLeftWeapon"/>. Occupies
    /// 0x4000..0x40C0.</summary>
    public const int OffsetCompanionEquipSlots = 0x4000;

    public const int CompanionEquipSlotStride = 0x30;

}
