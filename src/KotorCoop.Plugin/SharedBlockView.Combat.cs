namespace KotorCoop.Plugin;

// Enemy replication/presence, creature-liveness reconciliation, combat participation, peer
// effects, the damage ledger, client-triggered encounters, co-op death handling and
// one-shot animations. See SharedBlockView.cs for the class declaration and shared helpers.
internal sealed unsafe partial class SharedBlockView
{
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

    /// <summary>
    /// Agent -&gt; plugin (docs/research/companion-state-sync.md, roadmap item 12). Pointer to a
    /// mirrored companion's null-terminated equip resref (16 bytes), keyed by the SAME presence
    /// slot index <see cref="GetPartyPresence"/> uses — not by NPC roster slot. Same shape as
    /// <see cref="GetEquipResref"/>, applied to a companion body instead of a phantom.
    /// </summary>
    public byte* GetCompanionEquipResref(int presenceIndex, int equipIndex) =>
        baseAddress + SharedBlock.OffsetCompanionEquipSlots
        + (Clamp(presenceIndex, SharedBlock.PartyPresenceCapacity) * SharedBlock.CompanionEquipSlotStride)
        + (Clamp(equipIndex, SharedBlock.EquipCount) * 16);


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

}
