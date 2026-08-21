namespace KotorCoop.Plugin;

/// <summary>One remote peer as published by the external agent.</summary>
internal readonly record struct RemoteView(
    bool Active, uint PeerId, Vec3 Position, float Facing, int Health, uint Animation,
    uint Appearance, uint Flags)
{
    public const uint FlagInDialogue = 1;
    public const uint FlagPaused = 2;
    public const uint FlagInCombat = 4;

    /// <summary>Matches <c>PlayerState.FlagInMinigame</c> — the peer is in a swoop race or
    /// turret sequence and not meaningfully in the shared world. Carried verbatim through
    /// the existing <see cref="Flags"/> dword; no block layout change (see
    /// docs/research/minigames.md).</summary>
    public const uint FlagInMinigame = 32;

    public bool InCombat => (Flags & FlagInCombat) != 0;

    public bool Paused => (Flags & FlagPaused) != 0;

    public bool InMinigame => (Flags & FlagInMinigame) != 0;
}

/// <summary>One enemy's authoritative state, as published by the owning peer.
/// <see cref="Plot"/> marks a presence-only cutscene actor the combat mirror must skip.</summary>
internal readonly record struct EnemyView(
    uint ObjectId, ushort CurrentHealth, ushort MaxHealth, bool Dead, bool Plot);

/// <summary>One enemy's presence — where the owning peer's engine has the body and what
/// it is playing — as published for the drive controller. <see cref="OneShotSerial"/>
/// advances each time the owner's copy plays a fire-and-forget (a swing, a parry), so the
/// drive can replay exactly the new ones.</summary>
internal readonly record struct EnemyPresenceView(
    uint ObjectId, Vec3 Position, float Facing, uint Animation, bool Dead,
    uint OneShotSerial, uint OneShotAnimation, bool Plot, ushort Appearance, ushort Faction);

/// <summary>One companion's presence as the host's engine has it. <see cref="RosterSlotPlusOne"/>
/// is the NPC roster slot + 1 (0 = empty slot) — the cross-instance key; the mirroring
/// plugin resolves it against its own party table.</summary>
internal readonly record struct PartyPresenceView(
    uint RosterSlotPlusOne, Vec3 Position, float Facing, uint Animation, int Health,
    uint OneShotSerial, uint OneShotAnimation);

/// <summary>One creature-liveness entry as the agent last wrote it: an id (GIT-identical —
/// translated by the reconcile controller through <c>MaterialisedEnemies.ToLocal</c> before
/// it is resolved) and the host's reported state (<see cref="SharedBlock.LivenessStateAlive"/>
/// / <c>Dead</c> / <c>Gone</c>). See docs/research/creature-liveness.md.</summary>
internal readonly record struct LivenessView(uint ObjectId, uint State);

/// <summary>One NPC bark: who said it and the three engine arguments that shaped it,
/// carried verbatim. <see cref="Serial"/> makes a repeat of the same line visible.</summary>
/// <summary>The scalar half of a bark slot; text and resref are copied into caller buffers
/// by <see cref="SharedBlockView.GetRemoteBark"/>.</summary>
internal readonly record struct BarkView(uint Speaker, uint Flags, uint Serial);

/// <summary>One door's desired state, as published by the agent for the local module.</summary>
internal readonly record struct DoorView(uint ObjectId, byte OpenState, bool Unlocked);

/// <summary>One placeable's desired state, as published by the agent for the local module.</summary>
internal readonly record struct PlaceableView(uint ObjectId, bool Open, bool Unlocked, bool Disarmed);

/// <summary>One container-loot removal: item <see cref="ItemId"/> is to be deleted from
/// container <see cref="ContainerId"/>.</summary>
internal readonly record struct LootView(uint ContainerId, uint ItemId);

/// <summary>One desired ground item as published by the external agent.</summary>
internal readonly unsafe struct ItemSlotView(byte* slot)
{
    public bool Wanted => *(uint*)slot != 0;

    public uint ItemId => *(uint*)(slot + 0x04);

    public Vec3 Position => new()
    {
        X = *(float*)(slot + 0x08),
        Y = *(float*)(slot + 0x0C),
        Z = *(float*)(slot + 0x10),
    };

    /// <summary>Pointer to the null-terminated ascii resref, 16 bytes.</summary>
    public byte* Resref => slot + 0x14;

    /// <summary>Installed-upgrade bitmask for <c>CSWSItem+0x294</c>.</summary>
    public uint Upgrades => *(uint*)(slot + 0x24);

    /// <summary>Stack size for <c>CSWSItem+0x28C</c>.</summary>
    public uint StackSize => *(uint*)(slot + 0x28);

    private uint Packed => *(uint*)(slot + 0x2C);

    public uint Charges => Packed & 0xFF;

    public uint MaxCharges => (Packed >> 8) & 0xFF;

    public uint IdentityFlags => (Packed >> 16) & 0xFF;

    /// <summary>Whether the agent actually read this item's state. False means the slot
    /// carries a blueprint name and nothing more, and the spawned item must be left alone.</summary>
    public bool IdentityKnown => (IdentityFlags & SharedBlock.ItemIdentityKnown) != 0;
}

/// <summary>
/// Typed access to the shared block the plugin allocates and the external agent reads and
/// writes. Deliberately allocation-free: every accessor is a direct read or write through
/// the pointer, because this is used from the frame hook.
/// </summary>
internal sealed unsafe class SharedBlockView(byte* baseAddress)
{
    public byte* Base => baseAddress;

    public nint Address => (nint)baseAddress;

    /// <summary>
    /// Every per-slot accessor below runs it on its index first.
    ///
    /// ⚠ These are raw pointer arithmetic into a fixed 0x2000 allocation, so an index the
    /// caller got wrong is not an exception, it is a write into whatever follows the block.
    /// The counts that drive most of the loops come from the AGENT, which is a separate
    /// process that can be restarted, replaced or (as happened) accidentally started twice
    /// — so "the caller checked" is not a property this class may assume. Clamping rather
    /// than throwing is deliberate: this is called from the game's frame hook, where a
    /// managed exception is a process kill (see CrashProbe).
    /// </summary>
    private static int Clamp(int index, int capacity) =>
        index < 0 ? 0 : index >= capacity ? capacity - 1 : index;

    private uint* U32(int offset) => (uint*)(baseAddress + offset);

    private int* I32(int offset) => (int*)(baseAddress + offset);

    private float* F32(int offset) => (float*)(baseAddress + offset);

    private long* I64(int offset) => (long*)(baseAddress + offset);

    // ---- plugin -> agent ------------------------------------------------------

    public uint Heartbeat
    {
        get => *U32(0x4C);
        set => *U32(0x4C) = value;
    }

    public uint FrameCounter
    {
        get => *U32(SharedBlock.OffsetFrameCounter);
        set => *U32(SharedBlock.OffsetFrameCounter) = value;
    }

    public uint PhantomCount
    {
        get => *U32(SharedBlock.OffsetPhantomCount);
        set => *U32(SharedBlock.OffsetPhantomCount) = value;
    }

    public int LastStatus
    {
        get => *I32(SharedBlock.OffsetLastStatus);
        set => *I32(SharedBlock.OffsetLastStatus) = value;
    }

    /// <summary>⚠ Writes the RELOCATED table, not 0x264 — see SharedBlock.OffsetPhantomIds.
    /// Slot 0 is mirrored to the old address so an agent built before the move still sees
    /// the first phantom; slots 1+ never touch it, which is the whole point.</summary>
    public void SetPhantomId(int index, uint objectId)
    {
        if (index is < 0 or >= SharedBlock.SlotCapacity)
        {
            return;
        }

        *U32(SharedBlock.OffsetPhantomIdTable + (index * 4)) = objectId;

        if (index == 0)
        {
            *U32(SharedBlock.OffsetPhantomIds) = objectId;
        }
    }

    public uint GetPhantomId(int index) =>
        index is >= 0 and < SharedBlock.SlotCapacity
            ? *U32(SharedBlock.OffsetPhantomIdTable + (index * 4))
            : 0;

    public void SetLocalState(Vec3 position, float facing, int health)
    {
        *F32(0x10) = position.X;
        *F32(0x14) = position.Y;
        *F32(0x18) = position.Z;
        *F32(0x1C) = facing;
        *I32(0x40) = health;
    }

    // ---- agent -> plugin ------------------------------------------------------

    public uint Command => *U32(SharedBlock.OffsetCommand);

    public uint PlayerCreature => *U32(SharedBlock.OffsetPlayerCreature);

    public uint RemoteCount => *U32(0x48);

    public bool KeepAliveUnfocused => *U32(SharedBlock.OffsetKeepAliveUnfocused) != 0;

    public uint PausesCleared
    {
        get => *U32(SharedBlock.OffsetPausesCleared);
        set => *U32(SharedBlock.OffsetPausesCleared) = value;
    }

    public uint PickUpRequest
    {
        get => *U32(SharedBlock.OffsetPickUpRequest);
        set => *U32(SharedBlock.OffsetPickUpRequest) = value;
    }

    public uint PickUpIssued
    {
        get => *U32(SharedBlock.OffsetPickUpIssued);
        set => *U32(SharedBlock.OffsetPickUpIssued) = value;
    }

    /// <summary>Publishes the engine's own player creature for the agent to adopt instead
    /// of trusting its scan (see SharedBlock.OffsetEnginePlayerPointer). Pointer first,
    /// id second — the id is the tear-detector, so it must be the later write.</summary>
    public void PublishEnginePlayer(nint creature, uint objectId)
    {
        *U32(SharedBlock.OffsetEnginePlayerPointer) = (uint)creature;
        *U32(SharedBlock.OffsetEnginePlayerId) = objectId;
    }

    /// <summary>
    /// Zeroes the agent-written world-keyed inputs: remote peers, enemy presence, party
    /// presence and the published player-creature pointer.
    ///
    /// ⚠ Called on every epoch invalidation, because the agent CANNOT do this itself in the
    /// window that matters: through a module transition its process reads fail, so the block
    /// keeps whatever the old world last wrote. Measured on the tar_m03aa warp: the plugin
    /// arrived in the new module, read a three-second-stale remote slot, and spawned a
    /// phantom for a peer still on the Endar Spire — burning an object id and feeding the
    /// census a body it would later publish as a dead enemy. The agent re-writes all of
    /// these within a tick of its reads recovering; until then, empty is the truth.
    /// </summary>
    public void ClearAgentWorldInputs()
    {
        *U32(0x48) = 0;
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            byte* slot = baseAddress + SharedBlock.SlotsOffset + (i * SharedBlock.SlotStride);
            *(uint*)(slot + 0x00) = 0;
        }

        EnemyPresenceCount = 0;
        PartyPresenceCount = 0;
        LivenessCount = 0;
        *U32(SharedBlock.OffsetPlayerCreature) = 0;
    }

    public RemoteView GetRemote(int index)
    {
        if (index is < 0 or >= SharedBlock.SlotCapacity)
        {
            return default;
        }

        byte* slot = baseAddress + SharedBlock.SlotsOffset + (index * SharedBlock.SlotStride);

        return new RemoteView(
            *(uint*)(slot + 0x00) != 0,
            *(uint*)(slot + 0x04),
            new Vec3
            {
                X = *(float*)(slot + 0x08),
                Y = *(float*)(slot + 0x0C),
                Z = *(float*)(slot + 0x10),
            },
            *(float*)(slot + 0x14),
            *(int*)(slot + 0x18),
            *(uint*)(slot + 0x1C),
            *(uint*)(slot + 0x38),
            *(uint*)(slot + 0x3C));
    }

    /// <summary>Pointer to a peer's null-terminated ascii name (24 bytes).</summary>
    public byte* GetRemoteName(int index) =>
        baseAddress + SharedBlock.SlotsOffset
        + (Clamp(index, SharedBlock.SlotCapacity) * SharedBlock.SlotStride) + 0x20;

    // ---- ground items -----------------------------------------------------------

    // ---- enemies ----------------------------------------------------------------

    /// <summary>
    /// Agent-written, but the plugin zeroes it on a module transition to drop stale ids;
    /// the owning agent overwrites it again on its next publish.
    /// </summary>
    public uint EnemyCount
    {
        get => *U32(SharedBlock.OffsetEnemyCount);
        set => *U32(SharedBlock.OffsetEnemyCount) = value;
    }

    public EnemyView GetEnemy(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetEnemySlots
            + (Clamp(index, SharedBlock.EnemyCapacity) * SharedBlock.EnemySlotStride);

        return new EnemyView(
            *(uint*)(slot + 0x00),
            *(ushort*)(slot + 0x04),
            *(ushort*)(slot + 0x06),
            (*(uint*)(slot + 0x08) & SharedBlock.EnemyFlagDead) != 0,
            (*(uint*)(slot + 0x08) & SharedBlock.EnemyFlagPlot) != 0);
    }

    /// <summary>Agent-written; zeroed by the plugin on a module transition like
    /// <see cref="EnemyCount"/>, and for the same reason.</summary>
    public uint EnemyPresenceCount
    {
        get => *U32(SharedBlock.OffsetEnemyPresenceCount);
        set => *U32(SharedBlock.OffsetEnemyPresenceCount) = value;
    }

    /// <summary>ROUND 39: plugin-written, agent-read. See
    /// <see cref="SharedBlock.OffsetLocalCombatExcludedId"/>.</summary>
    public uint LocalCombatExcludedId
    {
        get => *U32(SharedBlock.OffsetLocalCombatExcludedId);
        set => *U32(SharedBlock.OffsetLocalCombatExcludedId) = value;
    }

    public EnemyPresenceView GetEnemyPresence(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetEnemyPresenceSlots
            + (Clamp(index, SharedBlock.EnemyCapacity) * SharedBlock.EnemyPresenceStride);

        return new EnemyPresenceView(
            *(uint*)(slot + 0x00),
            new Vec3
            {
                X = *(float*)(slot + 0x04),
                Y = *(float*)(slot + 0x08),
                Z = *(float*)(slot + 0x0C),
            },
            *(float*)(slot + 0x10),
            *(uint*)(slot + 0x14),
            (*(uint*)(slot + 0x18) & SharedBlock.EnemyFlagDead) != 0,
            *(uint*)(slot + 0x1C),
            *(uint*)(slot + 0x20),
            (*(uint*)(slot + 0x18) & SharedBlock.EnemyFlagPlot) != 0,
            *(ushort*)(slot + 0x24),
            *(ushort*)(slot + 0x26));
    }

    /// <summary>Agent-written companion presence; zeroed by the plugin on a module
    /// transition like the enemy regions, and for the same reason.</summary>
    public uint PartyPresenceCount
    {
        get => *U32(SharedBlock.OffsetPartyPresenceCount);
        set => *U32(SharedBlock.OffsetPartyPresenceCount) = value;
    }

    public PartyPresenceView GetPartyPresence(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetPartyPresenceSlots
            + (Clamp(index, SharedBlock.PartyPresenceCapacity) * SharedBlock.PartyPresenceStride);

        return new PartyPresenceView(
            *(uint*)(slot + 0x00),
            new Vec3
            {
                X = *(float*)(slot + 0x04),
                Y = *(float*)(slot + 0x08),
                Z = *(float*)(slot + 0x0C),
            },
            *(float*)(slot + 0x10),
            *(uint*)(slot + 0x14),
            *(int*)(slot + 0x18),
            *(uint*)(slot + 0x1C),
            *(uint*)(slot + 0x20));
    }

    // ---- creature liveness reconciliation -----------------------------------------

    /// <summary>Agent-written; zeroed by the plugin on a module transition like the enemy
    /// regions, and for the same reason. See SharedBlock.OffsetLivenessCount.</summary>
    public uint LivenessCount
    {
        get => *U32(SharedBlock.OffsetLivenessCount);
        set => *U32(SharedBlock.OffsetLivenessCount) = value;
    }

    public LivenessView GetLiveness(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetLivenessSlots
            + (Clamp(index, SharedBlock.LivenessCapacity) * SharedBlock.LivenessSlotStride);

        return new LivenessView(*(uint*)(slot + 0x00), *(uint*)(slot + 0x04));
    }

    /// <summary>Plugin-written cumulative anomaly count. See
    /// SharedBlock.OffsetLivenessAnomalyCount.</summary>
    public uint LivenessAnomalyCount
    {
        get => *U32(SharedBlock.OffsetLivenessAnomalyCount);
        set => *U32(SharedBlock.OffsetLivenessAnomalyCount) = value;
    }

    // ---- combat participation ----------------------------------------------------

    /// <summary>Publishes what a peer's phantom has taken here, for the agent to forward.
    /// The phantom id is written last, so the agent never pairs an id with another
    /// phantom's total.</summary>
    public void SetPhantomHit(int index, uint phantomId, uint cumulative, uint attacker, uint serial)
    {
        byte* slot = baseAddress + SharedBlock.OffsetPhantomHitSlots
            + (Clamp(index, SharedBlock.SlotCapacity) * SharedBlock.PhantomHitStride);

        *(uint*)(slot + 0x04) = cumulative;
        *(uint*)(slot + 0x08) = attacker;
        *(uint*)(slot + 0x0C) = serial;
        *(uint*)(slot + 0x00) = phantomId;
    }

    // ---- peer effects (non-damage combat effects) ---------------------------------
    //
    // Non-damage sibling of SetPhantomHit above: stun, paralyze, knockdown, slow, frightened,
    // sleep, choke, Force push/whirlwind. See docs/research/effect-sync.md.

    /// <summary>Publishes one effect <c>PhantomEffectHook</c> captured landing on one of our
    /// phantoms, into the OUT ring. Round-robin write by the caller (like
    /// <see cref="SetLocalBark"/>'s slots), phantom id written LAST so the agent never pairs
    /// an id with another capture's fields.</summary>
    public void SetPeerEffectOut(int index, uint phantomId, uint kind, uint durationMs, uint magnitude, uint serial)
    {
        byte* slot = baseAddress + SharedBlock.OffsetPeerEffectOutSlots
            + (Clamp(index, SharedBlock.PeerEffectCapacity) * SharedBlock.PeerEffectStride);

        *(uint*)(slot + SharedBlock.PeerEffectKindOffset) = kind;
        *(uint*)(slot + SharedBlock.PeerEffectDurationMsOffset) = durationMs;
        *(uint*)(slot + SharedBlock.PeerEffectMagnitudeOffset) = magnitude;
        *(uint*)(slot + SharedBlock.PeerEffectSerialOffset) = serial;
        *(uint*)(slot + SharedBlock.PeerEffectId) = phantomId;
    }

    /// <summary>Agent-written: an effect a peer captured landing on THIS player's phantom
    /// over there, for the plugin to replay here through <c>OdysseyEffects</c>.</summary>
    public (uint OriginPeerId, uint Kind, uint DurationMs, uint Magnitude, uint Serial) GetPeerEffectIn(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetPeerEffectInSlots
            + (Clamp(index, SharedBlock.PeerEffectCapacity) * SharedBlock.PeerEffectStride);

        return (
            *(uint*)(slot + SharedBlock.PeerEffectId),
            *(uint*)(slot + SharedBlock.PeerEffectKindOffset),
            *(uint*)(slot + SharedBlock.PeerEffectDurationMsOffset),
            *(uint*)(slot + SharedBlock.PeerEffectMagnitudeOffset),
            *(uint*)(slot + SharedBlock.PeerEffectSerialOffset));
    }

    /// <summary>Test hooks: drive the local player's own conversation and movement.</summary>
    public uint TestTalkSerial => *U32(SharedBlock.OffsetTestTalkSerial);

    public byte* TestTalkTag => baseAddress + SharedBlock.OffsetTestTalkTag;

    public uint TestWalkSerial => *U32(SharedBlock.OffsetTestWalkSerial);

    public uint TestAttackSerial => *U32(SharedBlock.OffsetTestAttackSerial);

    public uint TestAttacker => *U32(SharedBlock.OffsetTestAttacker);

    public uint TestAttackTarget => *U32(SharedBlock.OffsetTestAttackTarget);

    /// <summary>Test hook: make a named creature bark, through the engine's own call.</summary>
    public uint TestBarkSerial => *U32(SharedBlock.OffsetTestBarkSerial);

    public byte* TestBarkTag => baseAddress + SharedBlock.OffsetTestBarkTag;

    // ---- damage ledger -----------------------------------------------------------

    /// <summary>
    /// Publishes what this instance has DEALT to one enemy, for the agent to forward to the
    /// enemy owner. The object id is written LAST, so the agent can never pair an id with
    /// another creature's total — the same discipline every other slot array here uses.
    /// </summary>
    /// <summary>One captured or replayable NPC bark. <see cref="Serial"/> 0 means the slot
    /// has never been written; the arguments are the engine's own, carried verbatim.</summary>
    public void SetLocalBark(
        int index, uint speaker, uint flags, ReadOnlySpan<byte> text, ReadOnlySpan<byte> resRef, uint serial)
    {
        byte* slot = baseAddress + SharedBlock.OffsetLocalBarkSlots
            + (Clamp(index, SharedBlock.BarkCapacity) * SharedBlock.BarkStride);

        int textLength = Math.Min(text.Length, SharedBlock.BarkTextMax);

        *(uint*)(slot + SharedBlock.BarkSpeakerOffset) = speaker;
        *(uint*)(slot + SharedBlock.BarkFlagsOffset) = flags;
        *(uint*)(slot + SharedBlock.BarkTextLengthOffset) = (uint)textLength;

        for (int i = 0; i < SharedBlock.BarkResRefBytes; i++)
        {
            slot[SharedBlock.BarkResRefOffset + i] = i < resRef.Length ? resRef[i] : (byte)0;
        }

        for (int i = 0; i < textLength; i++)
        {
            slot[SharedBlock.BarkTextOffset + i] = text[i];
        }

        // Serial last: the agent acts on a slot only once this says it is complete.
        *(uint*)(slot + SharedBlock.BarkSerialOffset) = serial;
    }

    /// <summary>
    /// Agent-written: a bark from a peer for this instance to replay. The text and resref are
    /// copied out into the caller's buffers rather than exposed as pointers into the block —
    /// pointers are exactly what made this channel crash the engine.
    /// </summary>
    public BarkView GetRemoteBark(int index, Span<byte> text, out int textLength, Span<byte> resRef)
    {
        byte* slot = baseAddress + SharedBlock.OffsetRemoteBarkSlots
            + (Clamp(index, SharedBlock.BarkCapacity) * SharedBlock.BarkStride);

        int stored = (int)*(uint*)(slot + SharedBlock.BarkTextLengthOffset);
        textLength = Math.Clamp(stored, 0, Math.Min(text.Length, SharedBlock.BarkTextMax));

        for (int i = 0; i < textLength; i++)
        {
            text[i] = slot[SharedBlock.BarkTextOffset + i];
        }

        for (int i = 0; i < SharedBlock.BarkResRefBytes && i < resRef.Length; i++)
        {
            resRef[i] = slot[SharedBlock.BarkResRefOffset + i];
        }

        return new BarkView(
            *(uint*)(slot + SharedBlock.BarkSpeakerOffset),
            *(uint*)(slot + SharedBlock.BarkFlagsOffset),
            *(uint*)(slot + SharedBlock.BarkSerialOffset));
    }

    public void SetDamageDealt(int index, uint objectId, uint cumulative)
    {
        byte* slot = baseAddress + SharedBlock.OffsetDamageDealtSlots
            + (index * SharedBlock.DamageLedgerStride);

        *(uint*)(slot + 0x04) = cumulative;
        *(uint*)(slot + 0x00) = objectId;
    }

    public uint DamageDealtCount
    {
        get => *U32(SharedBlock.OffsetDamageDealtCount);
        set => *U32(SharedBlock.OffsetDamageDealtCount) = value;
    }

    public uint DamageDealtDropped
    {
        set => *U32(SharedBlock.OffsetDamageDealtDropped) = value;
    }

    public uint DamageDealtGeneration
    {
        set => *U32(SharedBlock.OffsetDamageDealtGeneration) = value;
    }

    /// <summary>Agent-written: the epoch its own ids belong to. The plugin drops its applied
    /// totals when this changes.</summary>
    public uint DamageOwedEpoch => *U32(SharedBlock.OffsetDamageOwedEpoch);

    /// <summary>Agent-written; zeroed by the plugin on a module transition like the enemy
    /// regions, and for the same reason.</summary>
    public uint DamageOwedCount
    {
        get => *U32(SharedBlock.OffsetDamageOwedCount);
        set => *U32(SharedBlock.OffsetDamageOwedCount) = value;
    }

    public uint DamageOwedApplied
    {
        get => *U32(SharedBlock.OffsetDamageOwedApplied);
        set => *U32(SharedBlock.OffsetDamageOwedApplied) = value;
    }

    /// <summary>Agent-written: non-zero on an instance whose damage has to be reported to
    /// somebody else. Zero (the default) keeps the accumulation out of the engine's damage
    /// hook entirely.</summary>
    public bool DamageDealtEnabled => *U32(SharedBlock.OffsetDamageDealtEnabled) != 0;

    public void GetDamageOwed(int index, out uint objectId, out uint cumulative)
    {
        byte* slot = baseAddress + SharedBlock.OffsetDamageOwedSlots
            + (index * SharedBlock.DamageLedgerStride);

        objectId = *(uint*)(slot + 0x00);
        cumulative = *(uint*)(slot + 0x04);
    }

    public float TestWalkX => *F32(SharedBlock.OffsetTestWalkX);

    public float TestWalkY => *F32(SharedBlock.OffsetTestWalkY);

    /// <summary>Test hook: apply a real damage effect to a phantom (see the block).</summary>
    public uint TestHitSerial => *U32(SharedBlock.OffsetTestHitSerial);

    public uint TestHitSlot => *U32(SharedBlock.OffsetTestHitSlot);

    public uint TestHitAmount => *U32(SharedBlock.OffsetTestHitAmount);

    // ---- client-triggered encounters ------------------------------------------------

    /// <summary>Agent -&gt; plugin: run the watch (only on a peer that does not own enemies).</summary>
    public bool EncounterWatchEnabled => *U32(SharedBlock.OffsetEncounterWatch) != 0;

    /// <summary>
    /// Publishes the encounters the local player is standing inside. The count goes last, so
    /// the agent never reads an id that belongs to the previous set — the same
    /// payload-then-key ordering every other region in this block uses.
    /// </summary>
    public void SetEncountersInside(ReadOnlySpan<uint> ids, uint serial)
    {
        int count = Math.Min(ids.Length, SharedBlock.EncounterInsideCapacity);
        for (int i = 0; i < count; i++)
        {
            *U32(SharedBlock.OffsetEncounterInside + (i * 4)) = ids[i];
        }

        *U32(SharedBlock.OffsetEncounterInsideSerial) = serial;
        *U32(SharedBlock.OffsetEncounterInsideCount) = (uint)count;
    }

    public uint EncounterRequestSerial => *U32(SharedBlock.OffsetEncounterRequestSerial);

    public uint EncounterRequestCount =>
        Math.Min(*U32(SharedBlock.OffsetEncounterRequestCount), (uint)SharedBlock.EncounterRequestCapacity);

    public (uint EncounterId, uint PeerId) GetEncounterRequest(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetEncounterRequests
            + (index * SharedBlock.EncounterRequestStride);

        return (*(uint*)(slot + 0x00), *(uint*)(slot + 0x04));
    }

    public uint EncountersFired
    {
        get => *U32(SharedBlock.OffsetEncounterFired);
        set => *U32(SharedBlock.OffsetEncounterFired) = value;
    }

    public uint EncountersRefused
    {
        get => *U32(SharedBlock.OffsetEncounterRefused);
        set => *U32(SharedBlock.OffsetEncounterRefused) = value;
    }

    public uint EncounterStatus
    {
        set => *U32(SharedBlock.OffsetEncounterStatus) = value;
    }

    // ---- generic trigger relay --------------------------------------------------------

    /// <summary>Agent -&gt; plugin: run the watch (only on a peer that is not enemy-authority).
    /// Mirrors <see cref="EncounterWatchEnabled"/>.</summary>
    public bool TriggerWatchEnabled => *U32(SharedBlock.OffsetTriggerWatch) != 0;

    /// <summary>
    /// Publishes the generic triggers the local player is standing inside AND whose OnEnter
    /// script matched the coop.ini allowlist pattern. Count written last, mirroring
    /// <see cref="SetEncountersInside"/>'s payload-then-key ordering.
    /// </summary>
    public void SetTriggersInside(ReadOnlySpan<uint> ids, uint serial)
    {
        int count = Math.Min(ids.Length, SharedBlock.TriggerInsideCapacity);
        for (int i = 0; i < count; i++)
        {
            *U32(SharedBlock.OffsetTriggerInside + (i * 4)) = ids[i];
        }

        *U32(SharedBlock.OffsetTriggerInsideSerial) = serial;
        *U32(SharedBlock.OffsetTriggerInsideCount) = (uint)count;
    }

    public uint TriggerRequestSerial => *U32(SharedBlock.OffsetTriggerRequestSerial);

    public uint TriggerRequestCount =>
        Math.Min(*U32(SharedBlock.OffsetTriggerRequestCount), (uint)SharedBlock.TriggerRequestCapacity);

    public (uint TriggerId, uint PeerId) GetTriggerRequest(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetTriggerRequests
            + (index * SharedBlock.TriggerRequestStride);

        return (*(uint*)(slot + 0x00), *(uint*)(slot + 0x04));
    }

    public uint TriggersFired
    {
        get => *U32(SharedBlock.OffsetTriggerFired);
        set => *U32(SharedBlock.OffsetTriggerFired) = value;
    }

    public uint TriggersRefused
    {
        get => *U32(SharedBlock.OffsetTriggerRefused);
        set => *U32(SharedBlock.OffsetTriggerRefused) = value;
    }

    public uint TriggerStatus
    {
        set => *U32(SharedBlock.OffsetTriggerStatus) = value;
    }

    /// <summary>Cumulative damage this player owes for hits their phantom took elsewhere.</summary>
    public uint IncomingDamage => *U32(SharedBlock.OffsetIncomingDamage);

    public uint IncomingDamageAttacker => *U32(SharedBlock.OffsetIncomingDamageAttacker);

    public uint IncomingDamageApplied
    {
        get => *U32(SharedBlock.OffsetIncomingDamageApplied);
        set => *U32(SharedBlock.OffsetIncomingDamageApplied) = value;
    }

    /// <summary>Which engine floor, if any, is keeping this player on their feet.</summary>
    public uint DownBlockReason
    {
        get => *U32(SharedBlock.OffsetDownBlockReason);
        set => *U32(SharedBlock.OffsetDownBlockReason) = value;
    }

    /// <summary>Bumped when <see cref="DownBlockReason"/> changes.</summary>
    public uint DownBlockSerial
    {
        get => *U32(SharedBlock.OffsetDownBlockSerial);
        set => *U32(SharedBlock.OffsetDownBlockSerial) = value;
    }

    /// <summary>Publishes one captured creature one-shot for the agent to read. The id is
    /// written last so the agent never pairs an id with another creature's payload.</summary>
    public void SetEnemyOneShot(int index, uint objectId, uint animation, uint serial)
    {
        byte* slot = baseAddress + SharedBlock.OffsetEnemyOneShotTable
            + (Clamp(index, SharedBlock.EnemyOneShotCapacity) * SharedBlock.EnemyOneShotStride);

        *(uint*)(slot + 0x04) = animation;
        *(uint*)(slot + 0x08) = serial;
        *(uint*)(slot + 0x00) = objectId;
    }

    // ---- equipment --------------------------------------------------------------

    /// <summary>Pointer to a peer's null-terminated equip resref (16 bytes).</summary>
    public byte* GetEquipResref(int peerIndex, int equipIndex) =>
        baseAddress + SharedBlock.OffsetEquipSlots
        + (Clamp(peerIndex, SharedBlock.SlotCapacity) * SharedBlock.EquipSlotStride)
        + (Clamp(equipIndex, SharedBlock.EquipCount) * 16);

    // ---- doors ------------------------------------------------------------------

    /// <summary>
    /// Agent-written, but the plugin zeroes it on a module transition: after a transition
    /// the ids name doors in a different module until the agent republishes.
    /// </summary>
    public uint DoorCount
    {
        get => *U32(SharedBlock.OffsetDoorCount);
        set => *U32(SharedBlock.OffsetDoorCount) = value;
    }

    public DoorView GetDoor(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetDoorSlots
            + (Clamp(index, SharedBlock.DoorCapacity) * SharedBlock.DoorSlotStride);

        return new DoorView(
            *(uint*)(slot + 0x00),
            *(slot + 0x04),
            *(slot + 0x05) != 0);
    }

    // ---- placeables -------------------------------------------------------------

    /// <summary>Agent-written; the plugin zeroes it on a module change (ids name strangers).</summary>
    public uint PlaceableCount
    {
        get => *U32(SharedBlock.OffsetPlaceableCount);
        set => *U32(SharedBlock.OffsetPlaceableCount) = value;
    }

    /// <summary>One placeable's desired state: objectId, opened, unlocked, disarmed.</summary>
    public PlaceableView GetPlaceable(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetPlaceableSlots
            + (Clamp(index, SharedBlock.PlaceableCapacity) * SharedBlock.PlaceableSlotStride);

        return new PlaceableView(
            *(uint*)(slot + 0x00),
            *(slot + 0x04) != 0,
            *(slot + 0x05) != 0,
            *(slot + 0x06) != 0);
    }

    // ---- container loot ---------------------------------------------------------

    /// <summary>Agent-written; the plugin zeroes it on a module change (ids name strangers).</summary>
    public uint LootCount
    {
        get => *U32(SharedBlock.OffsetLootCount);
        set => *U32(SharedBlock.OffsetLootCount) = value;
    }

    /// <summary>One container-loot removal: containerId, itemId.</summary>
    public LootView GetLoot(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetLootSlots
            + (Clamp(index, SharedBlock.LootCapacity) * SharedBlock.LootSlotStride);
        return new LootView(*(uint*)(slot + 0x00), *(uint*)(slot + 0x04));
    }

    // ---- module travel ----------------------------------------------------------

    /// <summary>Agent-written request serial; a request is pending while it is ahead of
    /// <see cref="TravelApplied"/>.</summary>
    public uint TravelSerial => *U32(SharedBlock.OffsetTravelSerial);

    /// <summary>Pointer to the null-terminated destination module name (32 bytes).</summary>
    public byte* TravelModule => baseAddress + SharedBlock.OffsetTravelModule;

    /// <summary>Pointer to the null-terminated start waypoint tag (32 bytes).</summary>
    public byte* TravelWaypoint => baseAddress + SharedBlock.OffsetTravelWaypoint;

    // ---- transition cinematics ---------------------------------------------------

    /// <summary>
    /// Plugin -&gt; agent. Publishes the movie resrefs this instance's own transition is about
    /// to play, and bumps the serial LAST so the agent can never read a half-written list.
    ///
    /// Called from the frame on which the engine's queue rises — the only frame that exists
    /// for this, because the module load drains the queue and a movie then takes the main
    /// thread away entirely.
    /// </summary>
    public void PublishCinematic(string[] names)
    {
        int count = Math.Min(names.Length, SharedBlock.CinematicCapacity);

        for (int i = 0; i < SharedBlock.CinematicCapacity; i++)
        {
            byte* slot = baseAddress + SharedBlock.OffsetCinematicNames
                + (i * SharedBlock.CinematicNameLength);

            for (int b = 0; b < SharedBlock.CinematicNameLength; b++)
            {
                slot[b] = 0;
            }

            if (i >= count)
            {
                continue;
            }

            string name = names[i];
            int length = Math.Min(name.Length, SharedBlock.CinematicNameLength - 1);
            for (int b = 0; b < length; b++)
            {
                slot[b] = (byte)name[b];
            }
        }

        *I32(SharedBlock.OffsetCinematicCount) = count;
        *U32(SharedBlock.OffsetCinematicSerial) = *U32(SharedBlock.OffsetCinematicSerial) + 1;
    }

    // ---- transition announce ------------------------------------------------------

    /// <summary>
    /// Plugin -&gt; agent. Publishes the destination this instance's own transition has just
    /// committed to, and bumps the serial LAST so the agent can never read a half-written
    /// destination. Called once, on the frame the destination is resolved — the rising edge
    /// of a real door's <c>trans_pending</c>, or the frame immediately before a
    /// plugin-initiated <c>Odyssey.TravelToModule</c>.
    /// </summary>
    public void PublishTransitionAnnounce(string module, string waypoint)
    {
        WriteFixedAscii(SharedBlock.OffsetTransitionAnnounceModule, module, SharedBlock.TravelNameLength);
        WriteFixedAscii(SharedBlock.OffsetTransitionAnnounceWaypoint, waypoint, SharedBlock.TravelNameLength);
        *U32(SharedBlock.OffsetTransitionAnnounceSerial) = *U32(SharedBlock.OffsetTransitionAnnounceSerial) + 1;
    }

    private void WriteFixedAscii(int offset, string value, int capacity)
    {
        byte* slot = baseAddress + offset;
        int length = Math.Min(value.Length, capacity - 1);

        for (int i = 0; i < length; i++)
        {
            slot[i] = (byte)value[i];
        }

        for (int i = length; i < capacity; i++)
        {
            slot[i] = 0;
        }
    }

    /// <summary>Agent -&gt; plugin. Nonzero while the client-door-refusal watch should refuse a
    /// CLIENT-initiated real door transition.</summary>
    public bool ClientDoorRefusalArmed => *U32(SharedBlock.OffsetClientDoorRefusalArmed) != 0;

    /// <summary>
    /// Posts one line to the always-on chat overlay WITHOUT going through the agent —
    /// <see cref="ChatOverlayController"/> reads <see cref="ChatLine"/>/<see cref="ChatSerial"/>
    /// from this same block, so a plugin-side write is visible to it exactly like an
    /// agent-side one. Exists for messages the plugin needs to show IMMEDIATELY, from code
    /// that has no round trip to spare — the client-door refusal fires and expires inside a
    /// single 0.5 s fade window, far too little time to wait for the agent's own tick.
    /// </summary>
    public void PostLocalChatLine(string text)
    {
        WriteFixedAscii(SharedBlock.OffsetChatLine, text, SharedBlock.ChatLineLength);
        *U32(SharedBlock.OffsetChatSerial) = *U32(SharedBlock.OffsetChatSerial) + 1;
    }

    /// <summary>Agent -&gt; plugin. How many movies the pending travel should queue.</summary>
    public int TravelMovieCount
    {
        get
        {
            int count = *I32(SharedBlock.OffsetTravelMovieCount);
            return count is > 0 and <= SharedBlock.CinematicCapacity ? count : 0;
        }
    }

    /// <summary>Agent -&gt; plugin. Pointer to travel movie <paramref name="index"/>, 16 bytes
    /// of null-padded ASCII.</summary>
    public byte* TravelMovie(int index) =>
        baseAddress + SharedBlock.OffsetTravelMovieNames + (index * SharedBlock.CinematicNameLength);

    public uint TravelApplied
    {
        get => *U32(SharedBlock.OffsetTravelApplied);
        set => *U32(SharedBlock.OffsetTravelApplied) = value;
    }

    public int TravelStatus
    {
        get => *I32(SharedBlock.OffsetTravelStatus);
        set => *I32(SharedBlock.OffsetTravelStatus) = value;
    }

    // ---- co-op cheats -----------------------------------------------------------

    /// <summary>Agent -&gt; plugin. Ahead of <see cref="CheatApplied"/> while a request waits.</summary>
    public uint CheatSerial => *U32(SharedBlock.OffsetCheatSerial);

    public uint CheatKind => *U32(SharedBlock.OffsetCheatKind);

    public uint CheatTarget => *U32(SharedBlock.OffsetCheatTarget);

    public int CheatAmount => *I32(SharedBlock.OffsetCheatAmount);

    public Vec3 CheatPosition => new()
    {
        X = *F32(SharedBlock.OffsetCheatX),
        Y = *F32(SharedBlock.OffsetCheatY),
        Z = *F32(SharedBlock.OffsetCheatZ),
    };

    /// <summary>Pointer to the null-terminated tag/resref argument (32 bytes).</summary>
    public byte* CheatText => baseAddress + SharedBlock.OffsetCheatText;

    /// <summary>Agent -&gt; plugin, <see cref="SharedBlock.CheatKindSetItem"/> only.
    /// <see cref="SharedBlock.CheatFieldUnchanged"/> means "leave this field as it is".</summary>
    public uint CheatItemCharges => *U32(SharedBlock.OffsetCheatCharges);

    public uint CheatItemMaxCharges => *U32(SharedBlock.OffsetCheatMaxCharges);

    public uint CheatItemUpgrades => *U32(SharedBlock.OffsetCheatUpgrades);

    public uint CheatItemFlags => *U32(SharedBlock.OffsetCheatItemFlags);

    public uint CheatApplied
    {
        get => *U32(SharedBlock.OffsetCheatApplied);
        set => *U32(SharedBlock.OffsetCheatApplied) = value;
    }

    public int CheatStatus
    {
        get => *I32(SharedBlock.OffsetCheatStatus);
        set => *I32(SharedBlock.OffsetCheatStatus) = value;
    }

    /// <summary>
    /// Writes the sentence the agent will print and the log will carry. ASCII only and
    /// truncated to the field, which is why the plugin composes short, factual detail —
    /// "phantom 0x2A belongs to Malak" rather than a paragraph.
    /// </summary>
    public void SetCheatDetail(string detail)
    {
        byte* p = baseAddress + SharedBlock.OffsetCheatDetail;
        int count = Math.Min(detail.Length, SharedBlock.CheatDetailLength - 1);
        for (int i = 0; i < count; i++)
        {
            char c = detail[i];
            p[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        p[count] = 0;
    }

    // ---- menu save load ---------------------------------------------------------

    /// <summary>Agent-written request serial; pending while ahead of MenuLoadApplied.</summary>
    public uint MenuLoadSerial => *U32(SharedBlock.OffsetMenuLoadSerial);

    public uint MenuLoadApplied
    {
        get => *U32(SharedBlock.OffsetMenuLoadApplied);
        set => *U32(SharedBlock.OffsetMenuLoadApplied) = value;
    }

    public int MenuLoadStatus
    {
        get => *I32(SharedBlock.OffsetMenuLoadStatus);
        set => *I32(SharedBlock.OffsetMenuLoadStatus) = value;
    }

    /// <summary>Pointer to the null-terminated module name for the menu load (16 bytes).</summary>
    public byte* MenuLoadModule => baseAddress + SharedBlock.OffsetMenuLoadModule;

    /// <summary>The details record for browser row <paramref name="index"/> — the world the
    /// host stands in and its party's portrait resrefs. Parallel to
    /// <see cref="BrowserEntry"/>, indexed identically.</summary>
    public byte* BrowserDetail(int index) =>
        baseAddress + SharedBlock.OffsetBrowserDetails + (index * SharedBlock.BrowserDetailStride);

    /// <summary>Agent-written: the next menu load adopts somebody else's world, so the
    /// character must come from Temp\pifo.ifo rather than the staged world's own
    /// module.ifo. See <see cref="SharedBlock.OffsetAdoptWorldLogin"/>.</summary>
    public bool AdoptWorldLogin => *U32(SharedBlock.OffsetAdoptWorldLogin) != 0;

    /// <summary>Plugin-written: which login flavour the last load actually took.</summary>
    public uint AdoptWorldApplied
    {
        get => *U32(SharedBlock.OffsetAdoptWorldApplied);
        set => *U32(SharedBlock.OffsetAdoptWorldApplied) = value;
    }

    /// <summary>Agent-written: a REMOTE peer standing in this module has its engine in
    /// combat. See <see cref="SharedBlock.OffsetSessionInCombat"/> for why the client cannot
    /// determine this locally, and why the local player is excluded.</summary>
    public bool SessionInCombat => *U32(SharedBlock.OffsetSessionInCombat) != 0;

    /// <summary>Plugin-written: bumped when this player releases a pause mirrored from a
    /// peer, so the agent can announce that the shared pause is over.</summary>
    public uint LocalPauseReleaseSerial
    {
        get => *U32(SharedBlock.OffsetLocalPauseReleaseSerial);
        set => *U32(SharedBlock.OffsetLocalPauseReleaseSerial) = value;
    }

    /// <summary>Agent-written: bumped when a PEER released the shared pause, so this instance
    /// drops the pause it started.</summary>
    public uint RemotePauseReleaseSerial => *U32(SharedBlock.OffsetRemotePauseReleaseSerial);

    // ---- shared pause -----------------------------------------------------------

    /// <summary>Agent-written: nonzero while any remote peer holds a pause.</summary>
    public bool RemotePauseDesired => *U32(SharedBlock.OffsetRemotePauseDesired) != 0;

    /// <summary>Plugin-written: nonzero while the pause bit is held on the peers' behalf.
    /// The agent reads this to avoid republishing a mirrored pause as this player's own.</summary>
    public uint RemotePauseApplied
    {
        get => *U32(SharedBlock.OffsetRemotePauseApplied);
        set => *U32(SharedBlock.OffsetRemotePauseApplied) = value;
    }

    // ---- journal ----------------------------------------------------------------

    public uint JournalSerial => *U32(SharedBlock.OffsetJournalSerial);

    /// <summary>Pointer to the null-terminated quest tag (32 bytes).</summary>
    public byte* JournalTag => baseAddress + SharedBlock.OffsetJournalTag;

    public int JournalState => *I32(SharedBlock.OffsetJournalState);

    public uint JournalApplied
    {
        get => *U32(SharedBlock.OffsetJournalApplied);
        set => *U32(SharedBlock.OffsetJournalApplied) = value;
    }

    public int JournalStatus
    {
        get => *I32(SharedBlock.OffsetJournalStatus);
        set => *I32(SharedBlock.OffsetJournalStatus) = value;
    }

    // ---- in-game connect --------------------------------------------------------

    public uint ConnectSerial
    {
        get => *U32(SharedBlock.OffsetConnectSerial);
        set => *U32(SharedBlock.OffsetConnectSerial) = value;
    }

    /// <summary>Pointer to the null-terminated typed host IP (40 bytes).</summary>
    public byte* ConnectHost => baseAddress + SharedBlock.OffsetConnectHost;

    public uint ConnectStatus
    {
        get => *U32(SharedBlock.OffsetConnectStatus);
        set => *U32(SharedBlock.OffsetConnectStatus) = value;
    }

    /// <summary>Agent-written: nonzero FORCES the in-game Co-op menu button on. The button
    /// defaults on plugin-side (coop.ini menu_button); this is the diagnostics override.</summary>
    public bool ConnectMenuEnabled => *U32(SharedBlock.OffsetConnectEnable) != 0;

    // ---- in-game console --------------------------------------------------------

    public uint ConsoleSerial
    {
        get => *U32(SharedBlock.OffsetConsoleSerial);
        set => *U32(SharedBlock.OffsetConsoleSerial) = value;
    }

    /// <summary>Pointer to the null-terminated console command line (96 bytes).</summary>
    public byte* ConsoleCommand => baseAddress + SharedBlock.OffsetConsoleCommand;

    public uint ConsoleReplySerial => *U32(SharedBlock.OffsetConsoleReplySerial);

    /// <summary>Pointer to the null-terminated status reply for the console (96 bytes).</summary>
    public byte* ConsoleReply => baseAddress + SharedBlock.OffsetConsoleReply;

    /// <summary>
    /// Copies a null-terminated ASCII command line into the console channel and bumps the
    /// serial, so the agent picks it up exactly once. <paramref name="length"/> is clamped to
    /// leave room for the terminator.
    /// </summary>
    public void PublishConsoleCommand(byte* source, int length)
    {
        byte* dest = ConsoleCommand;
        int count = Math.Clamp(length, 0, SharedBlock.ConsoleCommandLength - 1);

        for (int i = 0; i < count; i++)
        {
            dest[i] = source[i];
        }

        dest[count] = 0;
        ConsoleSerial++;
    }

    // ---- chat overlay -----------------------------------------------------------

    public bool ChatOverlayEnabled => *U32(SharedBlock.OffsetChatOverlayEnabled) != 0;

    /// <summary>Configured opacity, 0..100.</summary>
    public uint ChatOverlayOpacity => *U32(SharedBlock.OffsetChatOverlayOpacity);

    public uint ChatOverlayAnchor => *U32(SharedBlock.OffsetChatOverlayAnchor);

    public uint ChatOverlayMaxLines => *U32(SharedBlock.OffsetChatOverlayMaxLines);

    public uint ChatOverlayFadeMs => *U32(SharedBlock.OffsetChatOverlayFadeMs);

    public uint ChatSerial => *U32(SharedBlock.OffsetChatSerial);

    /// <summary>Pointer to the latest null-terminated chat line (128 bytes).</summary>
    public byte* ChatLine => baseAddress + SharedBlock.OffsetChatLine;

    /// <summary>Agent-written; bumped to wipe the overlay log.</summary>
    public uint ChatClearSerial => *U32(SharedBlock.OffsetChatClearSerial);

    // ---- server browser ---------------------------------------------------------

    /// <summary>Nonzero while the browser panel is on screen (plugin owns this).</summary>
    public uint BrowserOpen
    {
        get => *U32(SharedBlock.OffsetBrowserOpen);
        set => *U32(SharedBlock.OffsetBrowserOpen) = value;
    }

    /// <summary>The highlighted row (plugin owns this).</summary>
    public uint BrowserSelection
    {
        get => *U32(SharedBlock.OffsetBrowserSelection);
        set => *U32(SharedBlock.OffsetBrowserSelection) = value;
    }

    /// <summary>How many server rows the agent has published.</summary>
    public uint BrowserCount => *U32(SharedBlock.OffsetBrowserCount);

    /// <summary>Bumped by the agent whenever the list changes.</summary>
    public uint BrowserListSerial => *U32(SharedBlock.OffsetBrowserListSerial);

    /// <summary>Confirms the current selection; the agent connects. Selection is written first.</summary>
    public void RequestBrowserConnect() => (*U32(SharedBlock.OffsetBrowserConnectSerial))++;

    /// <summary>Bumped by the agent when the player must be asked for a server's password.</summary>
    public uint BrowserPasswordSerial => *U32(SharedBlock.OffsetBrowserPasswordSerial);

    /// <summary>The player chose to HOST rather than join a listed server.</summary>
    public void RequestBrowserHost() => (*U32(SharedBlock.OffsetBrowserHostSerial))++;

    /// <summary>Peers the session has admitted, written by the agent every tick regardless
    /// of player resolve — the seamless-arrival gate's signal (see SharedBlock).</summary>
    public uint SessionPeers => *U32(SharedBlock.OffsetSessionPeers);

    /// <summary>Agent -&gt; plugin. Environment.TickCount64 at the agent's last dispatcher
    /// tick; 0 = no agent has ever pumped this block. See AgentLiveness.</summary>
    public long AgentHeartbeat => *I64(SharedBlock.OffsetAgentHeartbeat);

    /// <summary>Start of a server row's fixed-size record.</summary>
    public byte* BrowserEntry(int index) =>
        baseAddress + SharedBlock.OffsetBrowserEntries
        + (Clamp(index, SharedBlock.BrowserCapacity) * SharedBlock.BrowserEntryStride);

    // ---- co-op death handling ---------------------------------------------------

    /// <summary>Plugin-written: nonzero while the local PC is downed (temp-dead).</summary>
    public uint LocalDowned
    {
        get => *U32(SharedBlock.OffsetLocalDowned);
        set => *U32(SharedBlock.OffsetLocalDowned) = value;
    }

    /// <summary>Plugin-written: bumped on each knock-down, so the agent can count them.</summary>
    public uint LocalDownedSerial
    {
        get => *U32(SharedBlock.OffsetLocalDownedSerial);
        set => *U32(SharedBlock.OffsetLocalDownedSerial) = value;
    }

    /// <summary>Plugin-written: bumped when a game-over was swallowed.</summary>
    public uint GameOverSerial
    {
        get => *U32(SharedBlock.OffsetGameOverSerial);
        set => *U32(SharedBlock.OffsetGameOverSerial) = value;
    }

    /// <summary>Agent-written: hold the game-over screen (cleared for a real party wipe).</summary>
    public bool SuppressGameOver => *U32(SharedBlock.OffsetSuppressGameOver) != 0;

    /// <summary>Agent-written: block control auto-swap (and Tab/portrait swapping).</summary>
    public bool SuppressControlSwap => *U32(SharedBlock.OffsetSuppressSwap) != 0;

    /// <summary>Agent-written: bumped to stand the local PC back up.</summary>
    public uint ReviveSerial => *U32(SharedBlock.OffsetReviveSerial);

    /// <summary>Agent-written: hit points for the revive (0 = the plugin's default).</summary>
    public uint ReviveHitPoints => *U32(SharedBlock.OffsetReviveHitPoints);

    public uint ReviveApplied
    {
        get => *U32(SharedBlock.OffsetReviveApplied);
        set => *U32(SharedBlock.OffsetReviveApplied) = value;
    }

    public uint ReviveStatus
    {
        get => *U32(SharedBlock.OffsetReviveStatus);
        set => *U32(SharedBlock.OffsetReviveStatus) = value;
    }

    // ---- one-shot animations -----------------------------------------------------

    /// <summary>Plugin-written: the local player's most recent one-shot animation.</summary>
    public uint LocalOneShotAnim
    {
        get => *U32(SharedBlock.OffsetLocalOneShotAnim);
        set => *U32(SharedBlock.OffsetLocalOneShotAnim) = value;
    }

    /// <summary>Plugin-written: bumped per dispatch, so an identical repeat still replicates.</summary>
    public uint LocalOneShotSerial
    {
        get => *U32(SharedBlock.OffsetLocalOneShotSerial);
        set => *U32(SharedBlock.OffsetLocalOneShotSerial) = value;
    }

    /// <summary>Agent-written: a remote peer's latest one-shot animation id.</summary>
    public uint GetRemoteOneShotAnim(int slot) =>
        *(uint*)(baseAddress + SharedBlock.OffsetRemoteOneShot
            + (Clamp(slot, SharedBlock.SlotCapacity) * SharedBlock.RemoteOneShotStride)
            + SharedBlock.RemoteOneShotAnim);

    /// <summary>Agent-written: its sequence, so the plugin fires it exactly once.</summary>
    public uint GetRemoteOneShotSerial(int slot) =>
        *(uint*)(baseAddress + SharedBlock.OffsetRemoteOneShot
            + (Clamp(slot, SharedBlock.SlotCapacity) * SharedBlock.RemoteOneShotStride)
            + SharedBlock.RemoteOneShotSerial);

    // ---- dialogue spectate ------------------------------------------------------

    /// <summary>Pointer to the current dialogue speaker (40 bytes), plugin-written.</summary>
    public byte* LocalDialogueSpeaker => baseAddress + SharedBlock.OffsetLocalDialogueSpeaker;

    /// <summary>Pointer to the current dialogue line (64 bytes), plugin-written.</summary>
    public byte* LocalDialogueLine => baseAddress + SharedBlock.OffsetLocalDialogueLine;

    /// <summary>Plugin-written; bumped when the speaker/line pair changes.</summary>
    public uint LocalDialogueSerial
    {
        get => *U32(SharedBlock.OffsetLocalDialogueSerial);
        set => *U32(SharedBlock.OffsetLocalDialogueSerial) = value;
    }

    // ---- dialogue follow --------------------------------------------------------

    /// <summary>Tag (32 bytes) of the NPC the local player is talking to, plugin-written.</summary>
    public byte* LocalDialoguePartnerTag => baseAddress + SharedBlock.OffsetLocalDialoguePartnerTag;

    /// <summary>Plugin-written; bumped on every reply the local player picks.</summary>
    public uint LocalDialogueReplySerial
    {
        get => *U32(SharedBlock.OffsetLocalDialogueReplySerial);
        set => *U32(SharedBlock.OffsetLocalDialogueReplySerial) = value;
    }

    /// <summary>Plugin-written; the reply slot the local player clicked.</summary>
    public int LocalDialogueReplyIndex
    {
        get => *(int*)U32(SharedBlock.OffsetLocalDialogueReplyIndex);
        set => *(int*)U32(SharedBlock.OffsetLocalDialogueReplyIndex) = value;
    }

    /// <summary>Agent-written; nonzero while a peer owns a conversation to follow.</summary>
    public bool RemoteDialogueActive => *U32(SharedBlock.OffsetRemoteDialogueActive) != 0;

    /// <summary>Agent-written; a change means "the owner picked a reply, replay it".</summary>
    public uint RemoteDialogueReplySerial => *U32(SharedBlock.OffsetRemoteDialogueReplySerial);

    /// <summary>Agent-written; the reply slot to replay.</summary>
    public int RemoteDialogueReplyIndex => *(int*)U32(SharedBlock.OffsetRemoteDialogueReplyIndex);

    /// <summary>Agent-written; tag of the NPC to open the conversation against.</summary>
    public byte* RemoteDialoguePartnerTag => baseAddress + SharedBlock.OffsetRemoteDialoguePartnerTag;

    /// <summary>Plugin-written pick-log entry base (serial u32, slot u32, lineHash u32).</summary>
    public byte* LocalDialoguePick(int index) =>
        baseAddress + SharedBlock.OffsetLocalDialoguePicks
        + (Clamp(index, SharedBlock.DialoguePickCapacity) * SharedBlock.DialoguePickStride);

    /// <summary>Agent-written pick-log entry base for the conversation owner's picks.</summary>
    public byte* RemoteDialoguePick(int index) =>
        baseAddress + SharedBlock.OffsetRemoteDialoguePicks
        + (Clamp(index, SharedBlock.DialoguePickCapacity) * SharedBlock.DialoguePickStride);

    // ---- dialogue node sync -----------------------------------------------------

    /// <summary>Plugin-written; the entry index the local conversation shows, or -1.</summary>
    public int LocalDialogueNode
    {
        get => *(int*)U32(SharedBlock.OffsetLocalDialogueNode);
        set => *(int*)U32(SharedBlock.OffsetLocalDialogueNode) = value;
    }

    /// <summary>Plugin-written; bumped after the node, so the pair is never read torn.</summary>
    public uint LocalDialogueNodeSerial
    {
        get => *U32(SharedBlock.OffsetLocalDialogueNodeSerial);
        set => *U32(SharedBlock.OffsetLocalDialogueNodeSerial) = value;
    }

    /// <summary>Agent-written; the entry index the conversation's owner shows, or -1.</summary>
    public int RemoteDialogueNode => *(int*)U32(SharedBlock.OffsetRemoteDialogueNode);

    /// <summary>Agent-written; bumped when the owner moves to a different node.</summary>
    public uint RemoteDialogueNodeSerial => *U32(SharedBlock.OffsetRemoteDialogueNodeSerial);

    // ---- per-object interaction locks -------------------------------------------

    /// <summary>Plugin-written; the tag of the object the local player is interacting with.</summary>
    public byte* LocalInteractionTag => baseAddress + SharedBlock.OffsetLocalInteractionTag;

    public uint LocalInteractionKind
    {
        get => *U32(SharedBlock.OffsetLocalInteractionKind);
        set => *U32(SharedBlock.OffsetLocalInteractionKind) = value;
    }

    public bool LocalInteractionActive
    {
        get => *U32(SharedBlock.OffsetLocalInteractionActive) != 0;
        set => *U32(SharedBlock.OffsetLocalInteractionActive) = value ? 1u : 0u;
    }

    /// <summary>Agent-written; how many lock slots are valid, clamped to the capacity.</summary>
    public uint InteractionLockCount =>
        Math.Min(*U32(SharedBlock.OffsetInteractionLockCount), (uint)SharedBlock.InteractionLockCapacity);

    /// <summary>
    /// Agent-written, but lowered by the PLUGIN when a session is released: the count is a
    /// level, so an agent that goes away without tearing the block down would otherwise leave
    /// its peers' claims standing for the life of the process. The agent overwrites it again
    /// on its next publish, so clearing it costs a live session nothing.
    /// </summary>
    public void ClearInteractionLocks() => *U32(SharedBlock.OffsetInteractionLockCount) = 0;

    /// <summary>Same reasoning as <see cref="ClearInteractionLocks"/>: without the valid bit
    /// the roster reconciler has nothing to reconcile against, which is the correct state for
    /// an instance with no peers.</summary>
    public void ClearHostPartyRoster() => *U32(SharedBlock.OffsetHostPartyRoster) = 0;

    /// <summary>Agent-written lock entry base: peerId u32 @0, kind u32 @4, tag[32] @8.</summary>
    public byte* InteractionLock(int index) =>
        baseAddress + SharedBlock.OffsetInteractionLocks
        + (Clamp(index, SharedBlock.InteractionLockCapacity) * SharedBlock.InteractionLockStride);

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

    public ItemSlotView GetItemSlot(int index) =>
        new(baseAddress + SharedBlock.OffsetItemSlots
            + (Clamp(index, SharedBlock.ItemSlotCapacity) * SharedBlock.ItemSlotStride));

    public void SetItemStatus(int index, uint itemId, uint objectId, uint state, int error)
    {
        byte* status = baseAddress + SharedBlock.OffsetItemStatus
            + (Clamp(index, SharedBlock.ItemSlotCapacity) * SharedBlock.ItemStatusStride);
        *(uint*)(status + 0x00) = itemId;
        *(uint*)(status + 0x04) = objectId;
        *(int*)(status + 0x0C) = error;

        // State last, so the agent never observes a half-written status slot.
        *(uint*)(status + 0x08) = state;
    }
}
