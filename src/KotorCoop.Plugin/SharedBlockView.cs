namespace KotorCoop.Plugin;

/// <summary>One remote peer as published by the external agent.</summary>
internal readonly record struct RemoteView(
    bool Active, uint PeerId, Vec3 Position, float Facing, int Health, uint Animation,
    uint Appearance, uint Flags)
{
    public const uint FlagInDialogue = 1;
    public const uint FlagPaused = 2;
    public const uint FlagInCombat = 4;

    public bool InCombat => (Flags & FlagInCombat) != 0;

    public bool Paused => (Flags & FlagPaused) != 0;
}

/// <summary>One enemy's authoritative state, as published by the owning peer.</summary>
internal readonly record struct EnemyView(uint ObjectId, ushort CurrentHealth, ushort MaxHealth, bool Dead);

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

    private uint* U32(int offset) => (uint*)(baseAddress + offset);

    private int* I32(int offset) => (int*)(baseAddress + offset);

    private float* F32(int offset) => (float*)(baseAddress + offset);

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

    public void SetPhantomId(int index, uint objectId)
    {
        if (index is >= 0 and < SharedBlock.SlotCapacity)
        {
            *U32(SharedBlock.OffsetPhantomIds + (index * 4)) = objectId;
        }
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
        baseAddress + SharedBlock.SlotsOffset + (index * SharedBlock.SlotStride) + 0x20;

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
        byte* slot = baseAddress + SharedBlock.OffsetEnemySlots + (index * SharedBlock.EnemySlotStride);

        return new EnemyView(
            *(uint*)(slot + 0x00),
            *(ushort*)(slot + 0x04),
            *(ushort*)(slot + 0x06),
            (*(uint*)(slot + 0x08) & SharedBlock.EnemyFlagDead) != 0);
    }

    // ---- equipment --------------------------------------------------------------

    /// <summary>Pointer to a peer's null-terminated equip resref (16 bytes).</summary>
    public byte* GetEquipResref(int peerIndex, int equipIndex) =>
        baseAddress + SharedBlock.OffsetEquipSlots
        + (peerIndex * SharedBlock.EquipSlotStride)
        + (equipIndex * 16);

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
        byte* slot = baseAddress + SharedBlock.OffsetDoorSlots + (index * SharedBlock.DoorSlotStride);

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
        byte* slot = baseAddress + SharedBlock.OffsetPlaceableSlots + (index * SharedBlock.PlaceableSlotStride);

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
        byte* slot = baseAddress + SharedBlock.OffsetLootSlots + (index * SharedBlock.LootSlotStride);
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

    /// <summary>Agent-written: nonzero enables the in-game Co-op menu button.</summary>
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

    /// <summary>Asks the agent to prompt for a password for the current selection.</summary>
    public void RequestBrowserPassword() => (*U32(SharedBlock.OffsetBrowserPasswordSerial))++;

    /// <summary>The player chose to HOST rather than join a listed server.</summary>
    public void RequestBrowserHost() => (*U32(SharedBlock.OffsetBrowserHostSerial))++;

    /// <summary>Start of a server row's fixed-size record.</summary>
    public byte* BrowserEntry(int index) =>
        baseAddress + SharedBlock.OffsetBrowserEntries + (index * SharedBlock.BrowserEntryStride);

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
            + (slot * SharedBlock.RemoteOneShotStride) + SharedBlock.RemoteOneShotAnim);

    /// <summary>Agent-written: its sequence, so the plugin fires it exactly once.</summary>
    public uint GetRemoteOneShotSerial(int slot) =>
        *(uint*)(baseAddress + SharedBlock.OffsetRemoteOneShot
            + (slot * SharedBlock.RemoteOneShotStride) + SharedBlock.RemoteOneShotSerial);

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

    public ItemSlotView GetItemSlot(int index) =>
        new(baseAddress + SharedBlock.OffsetItemSlots + (index * SharedBlock.ItemSlotStride));

    public void SetItemStatus(int index, uint itemId, uint objectId, uint state, int error)
    {
        byte* status = baseAddress + SharedBlock.OffsetItemStatus + (index * SharedBlock.ItemStatusStride);
        *(uint*)(status + 0x00) = itemId;
        *(uint*)(status + 0x04) = objectId;
        *(int*)(status + 0x0C) = error;

        // State last, so the agent never observes a half-written status slot.
        *(uint*)(status + 0x08) = state;
    }
}
