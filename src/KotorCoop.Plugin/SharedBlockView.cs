namespace KotorCoop.Plugin;

/// <summary>One remote peer as published by the external agent.</summary>
internal readonly record struct RemoteView(
    bool Active, uint PeerId, Vec3 Position, float Facing, int Health, uint Animation,
    uint Appearance, uint Flags, int MaxHealth = 0)
{
    /// <summary>The slot's flags dword carries the peer's max hit points in its upper 16
    /// bits (KotorGameBridge.WriteToPlugin); the flag bits live in the low half. An agent
    /// that predates the packing writes 0 there, which reads as "max unknown" and leaves
    /// the stand-in's hit points alone.</summary>
    public const uint FlagsMask = 0xFFFF;

    public const uint FlagInDialogue = 1;
    public const uint FlagPaused = 2;
    public const uint FlagInCombat = 4;

    /// <summary>Matches <c>PlayerState.FlagInMinigame</c> — the peer is in a swoop race or
    /// turret sequence and not meaningfully in the shared world. Carried verbatim through
    /// the existing <see cref="Flags"/> dword; no block layout change (see
    /// docs/research/minigames.md).</summary>
    public const uint FlagInMinigame = 32;

    /// <summary>Matches <c>PlayerState.FlagAway</c> — the peer's agent has stopped reading
    /// their game (main menu, load screen, character screen) and is republishing their last
    /// known state with this bit set. Carried verbatim through the existing
    /// <see cref="Flags"/> dword like <see cref="FlagInMinigame"/>; no block layout change.
    /// The agent already omits an away peer from what it writes to this block (same idiom as
    /// a minigame/module-filtered peer — see <c>CoopAgent.PumpOnce</c>), so
    /// <see cref="RemoteView.Active"/> is what actually parks the phantom; this bit exists for
    /// any future plugin-side consumer (a HUD indicator) that wants the reason.</summary>
    public const uint FlagAway = 64;

    /// <summary>Matches <c>PlayerState.FlagSoloSegment</c> — this peer is the OWNER of an
    /// active story-forced solo segment (docs/research/star-forge-finale.md §3.3/§3.5).
    /// Carried verbatim through the existing <see cref="Flags"/> dword like
    /// <see cref="FlagInMinigame"/>/<see cref="FlagAway"/>; no block layout change. Unlike
    /// those two this does NOT park the flag-bearer's own phantom — see
    /// <c>KotorCoop.Protocol.SoloSegmentRule</c>'s own doc comment for why the asymmetry is
    /// deliberate: the owner must stay visible for the spectator's camera to follow.</summary>
    public const uint FlagSoloSegment = 128;

    /// <summary>Matches <c>PlayerState.FlagStealth</c> (protocol 53) — the peer's character is
    /// in the engine's stealth mode (their <c>CSWSCreature+0x4D1</c> byte, sampled by their
    /// agent every tick). Carried verbatim through the existing <see cref="Flags"/> dword; no
    /// block layout change. <c>PhantomController.ApplyTargetable</c> holds the stand-in
    /// Neutral while it is set (<c>stealth_sync</c>) — see <see cref="PhantomTargetRule"/>.</summary>
    public const uint FlagStealth = 512;

    /// <summary>Matches <c>PlayerState.FlagMenuGuard</c> (protocol 69) — the peer has a
    /// full-screen menu open, within the bounded window <c>MenuGuardRule</c> grants. Carried
    /// verbatim through the existing <see cref="Flags"/> dword; no block layout change.
    /// <c>PhantomController.ApplyTargetable</c> holds the stand-in Neutral while it is set
    /// (<c>menu_guard_sync</c>) and <c>PhantomHitHook</c> drops any hit that still lands on it —
    /// see <see cref="PhantomTargetRule"/> and docs/research/effect-sync.md §13.</summary>
    public const uint FlagMenuGuard = 1024;

    public bool InCombat => (Flags & FlagInCombat) != 0;

    public bool Stealthed => (Flags & FlagStealth) != 0;

    public bool MenuGuarded => (Flags & FlagMenuGuard) != 0;

    public bool Paused => (Flags & FlagPaused) != 0;

    public bool InMinigame => (Flags & FlagInMinigame) != 0;

    public bool Away => (Flags & FlagAway) != 0;

    public bool InSoloSegment => (Flags & FlagSoloSegment) != 0;
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
internal sealed unsafe partial class SharedBlockView(byte* baseAddress)
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

    /// <summary>
    /// The single unambiguous remote-peer phantom id — 0 unless exactly one phantom slot is
    /// occupied. Used for <c>damage_creator_attribution</c> (docs/research/peer-damage-ledger.md
    /// §8): the enemy- and companion-damage owed ledgers both merge every contributing peer's
    /// rise into ONE total before this instance ever sees a row (peer-damage-ledger.md §3.1,
    /// CompanionDamageLedger.Merge), so there is no per-row peer identity left once more than
    /// one peer is connected — this is the only case where "the peer's phantom" is unambiguous
    /// without a protocol change. Correct for the two-player topology this codebase tests
    /// against; a 3+-peer session falls back to 0 (no attribution) rather than guess wrong.
    /// </summary>
    public uint SoleRemotePhantomId()
    {
        uint found = 0;
        int count = 0;

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            uint id = GetPhantomId(i);
            if (id == 0)
            {
                continue;
            }

            found = id;
            count++;
            if (count > 1)
            {
                return 0;
            }
        }

        return count == 1 ? found : 0;
    }

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
            *(uint*)(slot + 0x3C) & RemoteView.FlagsMask,
            (int)(*(uint*)(slot + 0x3C) >> 16));
    }

    /// <summary>Pointer to a peer's null-terminated ascii name (24 bytes).</summary>
    public byte* GetRemoteName(int index) =>
        baseAddress + SharedBlock.SlotsOffset
        + (Clamp(index, SharedBlock.SlotCapacity) * SharedBlock.SlotStride) + 0x20;

}
