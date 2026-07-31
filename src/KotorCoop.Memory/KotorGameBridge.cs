using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>A live creature object found in the game.</summary>
public sealed record CreatureRef(nint Address, uint ObjectId, string Tag, bool IsPlayer, bool IsPartyMember)
{
    public override string ToString() =>
        $"0x{Address:X} id=0x{ObjectId:X} {(Tag.Length > 0 ? Tag : "(untagged)")}"
        + (IsPlayer ? " [player]" : IsPartyMember ? " [party]" : string.Empty);
}

/// <summary>
/// A KOTOR-specific bridge built on the public Ghidra symbol database for
/// swkotor.exe v1.0.3 (LaneDibello's kotor1_0_3.db), whose game_version table sha256 matches
/// this exact Steam binary. Because the image has no ASLR, every constant below is a fixed
/// runtime address.
///
/// This replaces the offset-profile approach for KOTOR, which could not express what is
/// needed here: find every creature, pick the one flagged as the player, and drive a
/// selection of the others. See docs/re/object-model.md.
/// </summary>
public sealed class KotorGameBridge : IGameBridge
{
    // ---- symbols (verified against this build) --------------------------------

    /// <summary>CSWSCreature vtable. Every 4-byte occurrence of this value is a creature.</summary>
    public const uint CreatureVtable = 0x007470C8;

    // CSWSObject, embedded at CSWSCreature+0.
    private const int OffObjectId = 0x04;
    private const int OffTag = 0x18;          // CExoString { char* @0, ulong len @4 }
    private const int OffAreaId = 0x8C;
    private const int OffPosition = 0x90;     // Vector, 3 floats
    private const int OffOrientation = 0x9C;  // Vector, 3 floats (Euler, not a quaternion)
    private const int OffAnimation = 0xD4;

    // The server animation field (+0xD4) carries gait and scripted animations, but weapon
    // draws / attack swings / flourishes (the "x" key) are fire-and-forget on the CLIENT twin
    // and never reach it. The twin's current-animation field reflects what the model actually
    // plays, so reading it captures those too. See docs/research/animation-sync.md.
    private const int OffClientTwin = 0x224;    // CSWSCreature + this = client twin CSWCObject*
    private const int OffTwinAnimation = 0x48;  // CSWCObject + this = current played animation

    /// <summary>
    /// Current hit points, u16. NOT +0xE0: that is the STORED MAX, which is why the
    /// dashboard used to read a health of 0 while the character sheet showed 25/28.
    /// Verified through CSWSObject::SetCurrentHitPoints (0x004CC280), which writes +0xDC,
    /// and ExecuteCommandSetMaxHitPoints, which writes +0xE0 and then calls it.
    /// </summary>
    private const int OffCurrentHitPoints = 0xDC;

    private const int OffMaxHitPoints = 0xE0;

    /// <summary>Damage floors at 1 instead of killing. Used to make a peer's enemies non-lethal.</summary>
    private const int OffMinOneHitPoint = 0x200;

    private const int OffPlot = 0xF8;

    // CSWSCreature.
    private const int OffPartyFollowInfo = 0x4C0;   // CSWSCreaturePartyFollowInfo*
    private const int OffAppearanceType = 0xA60;    // u16, the appearance.2da row
    private const int OffCreatureStats = 0xA74;     // CSWSCreatureStats*
    private const int OffInventory = 0xA2C;         // CSWInventory*

    // CSWInventory slot pointers (symbol database): base 0x4 + 4 * nwscript slot number.
    private const int InvSlotBody = 0x08;           // INVENTORY_SLOT_BODY = 1
    private const int InvSlotRightWeapon = 0x14;    // INVENTORY_SLOT_RIGHTWEAPON = 4
    private const int InvSlotLeftWeapon = 0x18;     // INVENTORY_SLOT_LEFTWEAPON = 5

    // CSWSCreatureStats.
    private const int StatsIsPc = 0x6C;
    private const int StatsFaction = 0x78;

    private readonly ProcessMemory _memory;
    private readonly bool _ownsMemory;
    private readonly int _maxPhantoms;
    private readonly GlobalVariableTable _globals;

    private CreatureRef? _player;
    private List<CreatureRef> _phantoms = [];
    private int _consecutiveFailures;
    private nint _pluginBlock;

    // Offsets inside the in-process plugin's SharedBlock.
    private const int BlockRemoteCount = 0x48;
    private const int BlockHeartbeat = 0x4C;
    private const int BlockSlots = 0x50;
    private const int BlockSlotStride = 0x40;
    private const int BlockSlotCapacity = 8;
    private const int BlockSlotNameLength = 24;
    private const int BlockFrameCounter = 0x250;
    private const int BlockCommand = 0x254;
    private const int BlockPlayerCreature = 0x258;
    private const int BlockPhantomCount = 0x25C;
    private const int BlockLastStatus = 0x260;
    private const int BlockPhantomIds = 0x264;
    private const int BlockKeepAliveUnfocused = 0x268;
    private const int BlockPausesCleared = 0x26C;
    private const int BlockPickUpRequest = 0x280;

    // Ground-item regions, mirroring SharedBlock version 2.
    private const int BlockItemSlots = 0x290;
    private const int BlockItemSlotStride = 0x30;
    private const int BlockItemStatus = 0x410;
    private const int BlockItemStatusStride = 0x10;
    private const int BlockItemCapacity = 8;
    private const int BlockItemResrefLength = 16;

    private const uint ItemStateNone = 0;
    private const uint ItemStateAlive = 1;
    private const uint ItemStateVanished = 2;

    private const uint CommandSpawnAndDrive = 1;

    // Equipment replication, mirroring SharedBlock version 5.
    private const int BlockEquipSlots = 0x7A0;
    private const int BlockEquipStride = 0x30;
    private const int BlockEquipResrefLength = 16;

    // Enemy replication, mirroring SharedBlock version 3.
    private const int BlockEnemyCount = 0x488;
    private const int BlockEnemySlots = 0x490;
    private const int BlockEnemyStride = 0x10;
    private const int BlockEnemyCapacity = 32;
    private const uint EnemyFlagDead = 1;

    // Module travel, mirroring SharedBlock version 6.
    private const int BlockTravelSerial = 0x920;
    private const int BlockTravelModule = 0x924;
    private const int BlockTravelWaypoint = 0x944;
    private const int BlockTravelNameLength = 32;

    // Shared pause + journal apply, mirroring SharedBlock version 7.
    private const int BlockRemotePauseDesired = 0x96C;
    private const int BlockRemotePauseApplied = 0x970;
    private const int BlockJournalSerial = 0x974;
    private const int BlockJournalTag = 0x978;
    private const int BlockJournalState = 0x998;
    private const int BlockJournalApplied = 0x99C;
    private const int BlockJournalTagLength = 32;

    // In-game Connect flow, mirroring SharedBlock version 8.
    private const int BlockConnectSerial = 0x9C0;
    private const int BlockConnectHost = 0x9C4;
    private const int BlockConnectHostLength = 40;
    private const int BlockConnectStatus = 0x9EC;
    private const int BlockConnectEnable = 0x9F0;

    // In-game console command channel, mirroring SharedBlock version 9.
    private const int BlockConsoleSerial = 0x9F4;
    private const int BlockConsoleCommand = 0x9F8;
    private const int BlockConsoleCommandLength = 96;
    private const int BlockConsoleReplySerial = 0xA58;
    private const int BlockConsoleReply = 0xA5C;
    private const int BlockConsoleReplyLength = 96;

    // Chat overlay channel + config, mirroring SharedBlock version 10.
    private const int BlockChatOverlayEnabled = 0xAC0;
    private const int BlockChatOverlayOpacity = 0xAC4;
    private const int BlockChatOverlayAnchor = 0xAC8;
    private const int BlockChatOverlayMaxLines = 0xACC;
    private const int BlockChatOverlayFadeMs = 0xAD0;
    private const int BlockChatSerial = 0xAD4;
    private const int BlockChatLine = 0xAD8;
    private const int BlockChatLineLength = 128;
    private const int BlockChatClearSerial = 0xC60;

    // Dialogue spectate, mirroring SharedBlock version 12 (reserved region 0xC80..0xD00).
    private const int BlockLocalDialogueSpeaker = 0xC80;
    private const int BlockLocalDialogueSpeakerLength = 40;
    private const int BlockLocalDialogueLine = 0xCA8;
    private const int BlockLocalDialogueLineLength = 64;

    /// <summary>16-byte signature: "KOCOOP01" + version 14 + guard 0xC0FFEE01. The version is
    /// part of the pattern deliberately: an agent must not attach to a plugin whose block
    /// layout it does not know, and a stale pair fails to find each other rather than reading
    /// the wrong offsets.</summary>
    public const string PluginSignature = "4B 4F 43 4F 4F 50 30 31 0F 00 00 00 01 EE FF C0";

    /// <summary>Agent's view of which coop item id occupies each plugin item slot.</summary>
    private readonly uint[] _itemSlotIds = new uint[BlockItemCapacity];

    /// <summary>
    /// True when the in-process plugin is present. Changes the strategy completely: instead
    /// of poking existing creatures' positions from outside, the agent hands remote players
    /// to the plugin, which spawns real creatures and walks them through the engine.
    /// </summary>
    public bool PluginAttached => _pluginBlock != 0;

    public nint PluginBlockAddress => _pluginBlock;

    public KotorGameBridge(ProcessMemory memory, int maxPhantoms = 4, bool ownsMemory = true)
    {
        _memory = memory;
        _maxPhantoms = maxPhantoms;
        _ownsMemory = ownsMemory;
        _globals = new GlobalVariableTable(memory);
    }

    public string Description =>
        $"KOTOR [pid {_memory.ProcessId}] player={(_player is null ? "?" : $"0x{_player.Address:X}")} "
        + $"phantoms={_phantoms.Count}";

    public bool IsAttached => _memory.IsOpen && _player is not null;

    /// <summary>True once at least one drivable creature has been claimed.</summary>
    public bool CanWriteRemotes => _pluginBlock != 0 || _phantoms.Count > 0;

    public CreatureRef? Player => _player;

    public IReadOnlyList<CreatureRef> Phantoms => _phantoms;

    // ---- discovery ------------------------------------------------------------

    /// <summary>Finds every live creature by scanning for the vtable pointer.</summary>
    public List<CreatureRef> EnumerateCreatures()
    {
        List<CreatureRef> creatures = [];

        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(CreatureVtable));
        PatternScanner scanner = new(_memory);

        foreach (nint address in scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 512))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId))
            {
                continue;
            }

            string tag = ReadExoString(address + OffTag);

            bool isPlayer = false;
            if (_memory.TryReadPointer(address + OffCreatureStats, out nint stats) && stats != 0
                && _memory.TryReadInt32(stats + StatsIsPc, out int isPc))
            {
                isPlayer = isPc != 0;
            }

            // Party members run follow-AI, which rewrites their position every frame, so
            // they are unusable as phantom bodies. The follow-info pointer identifies them.
            bool isParty = _memory.TryReadPointer(address + OffPartyFollowInfo, out nint follow) && follow != 0;

            creatures.Add(new CreatureRef(address, objectId, tag, isPlayer, isParty));
        }

        return creatures;
    }

    /// <summary>
    /// Locates the player and claims phantom bodies. Called at startup and again if reads
    /// start failing, since a module transition destroys and rebuilds every object.
    /// </summary>
    public bool TryResolve(out string diagnostic)
    {
        // Find the plugin's shared block FIRST, before any world state. The block is created
        // when the plugin loads and has nothing to do with a loaded module, so it must be
        // discoverable at the main menu — that is exactly where the Co-op menu button lives and
        // where the agent waits in --menu mode. Resolving it only after a player was found made
        // the menu button impossible to enable (the early returns below fire at the menu).
        TryFindPlugin();

        List<CreatureRef> creatures = EnumerateCreatures();

        if (creatures.Count == 0)
        {
            _player = null;
            _phantoms = [];
            diagnostic = "no creature objects found; is a module loaded?";
            return false;
        }

        _player = creatures.FirstOrDefault(c => c.IsPlayer);

        if (_player is null)
        {
            _phantoms = [];
            diagnostic = $"found {creatures.Count} creatures but none flagged is_pc";
            return false;
        }

        // Anything that is neither the player nor a party member is idle enough to drive.
        // Ordering by object id keeps the choice stable between resolves.
        _phantoms =
        [
            .. creatures
                .Where(c => !c.IsPlayer && !c.IsPartyMember)
                .OrderBy(c => c.ObjectId)
                .Take(_maxPhantoms),
        ];

        _consecutiveFailures = 0;

        TryFindPlugin();
        TryResolveGlobals();

        string mode = _pluginBlock != 0
            ? $"PLUGIN mode: block at 0x{_pluginBlock:X}, the plugin will spawn and walk real creatures"
            : $"external mode: claimed {_phantoms.Count} existing creature(s) as phantom bodies: "
                + string.Join(", ", _phantoms.Select(p => p.Tag.Length > 0 ? p.Tag : $"0x{p.ObjectId:X}"));

        diagnostic =
            $"player at 0x{_player.Address:X} (id 0x{_player.ObjectId:X}); "
            + $"{creatures.Count} creatures; {mode}";
        return true;
    }

    /// <summary>Locates the plugin's shared block by its 16-byte signature.</summary>
    private void TryFindPlugin()
    {
        _pluginBlock = 0;

        PatternScanner scanner = new(_memory);
        List<nint> hits = scanner.ScanAll(BytePattern.Parse(PluginSignature), ScanScope.PrivateCommitted, limit: 4);

        foreach (nint candidate in hits)
        {
            // A heartbeat that advances distinguishes a live plugin from a stale block left
            // behind in freed memory, which the signature alone cannot do.
            if (!_memory.TryReadUInt32(candidate + BlockHeartbeat, out uint first))
            {
                continue;
            }

            Thread.Sleep(60);

            if (_memory.TryReadUInt32(candidate + BlockHeartbeat, out uint second) && second != first)
            {
                _pluginBlock = candidate;
                ResetItemSlots();
                BaselineTakeSerials();
                return;
            }
        }
    }

    /// <summary>
    /// Adopts the block's CURRENT take-serials as already-seen. The plugin outlives the
    /// agent, so a restarted agent otherwise finds serials above its zero baselines and
    /// replays the previous session's last console command and browser connect the moment
    /// it attaches (observed live: a fresh agent immediately reported "that server is no
    /// longer listed" from a click made minutes earlier).
    /// </summary>
    private void BaselineTakeSerials()
    {
        if (_memory.TryReadUInt32(_pluginBlock + BlockConsoleSerial, out uint console))
        {
            _lastConsoleSerial = console;
        }

        if (_memory.TryReadUInt32(_pluginBlock + BlockBrowserConnectSerial, out uint browser))
        {
            _lastBrowserConnectSerial = browser;
        }

        if (_memory.TryReadUInt32(_pluginBlock + BlockBrowserHostSerial, out uint browserHost))
        {
            _lastBrowserHostSerial = browserHost;
        }

        if (_memory.TryReadUInt32(_pluginBlock + BlockConnectSerial, out uint connect))
        {
            _lastConnectSerial = connect;
        }
    }

    /// <summary>
    /// Withdraws every ground-item slot on attach. The plugin outlives the agent - it is
    /// loaded by the game itself - so a restarted agent inherits whatever items the
    /// previous session had spawned. Without this they would stay on the floor forever,
    /// owned by a slot no agent is tracking any more.
    /// </summary>
    private void ResetItemSlots()
    {
        Array.Clear(_itemSlotIds);

        for (int i = 0; i < BlockItemCapacity; i++)
        {
            _memory.TryWrite(_pluginBlock + BlockItemSlots + (i * BlockItemSlotStride), 0u);
        }
    }

    /// <summary>
    /// Asks the plugin to keep this instance simulating while its window is unfocused.
    /// Only one of two co-op windows can hold focus, and the loser would otherwise stop
    /// dead — see <c>FocusKeeper</c>. Off unless a session actually needs it.
    /// </summary>
    public bool SetKeepAliveUnfocused(bool enabled) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockKeepAliveUnfocused, enabled ? 1u : 0u);

    /// <summary>
    /// Asks the plugin to have the local player pick up the ground item with this id,
    /// through the engine's own pick-up action. Test hook for the item sync; returns false
    /// if the item is not one this instance currently has on the floor.
    /// </summary>
    public bool TryRequestPickUp(uint itemId)
    {
        int slot = FindItemSlot(itemId);

        return _pluginBlock != 0
            && slot >= 0
            && _memory.TryWrite(_pluginBlock + BlockPickUpRequest, (uint)(slot + 1));
    }

    /// <summary>How many focus-loss pauses the plugin has cleared. 0 if no plugin.</summary>
    public uint PausesCleared =>
        _pluginBlock != 0 && _memory.TryReadUInt32(_pluginBlock + BlockPausesCleared, out uint n) ? n : 0;

    /// <summary>Diagnostics the plugin publishes back to us.</summary>
    public (uint Frames, uint Phantoms, int LastStatus) ReadPluginState()
    {
        if (_pluginBlock == 0)
        {
            return (0, 0, 0);
        }

        _memory.TryReadUInt32(_pluginBlock + BlockFrameCounter, out uint frames);
        _memory.TryReadUInt32(_pluginBlock + BlockPhantomCount, out uint phantoms);
        _memory.TryReadInt32(_pluginBlock + BlockLastStatus, out int status);
        return (frames, phantoms, status);
    }

    // ---- IGameBridge ----------------------------------------------------------

    public bool TryReadLocalState(out PlayerState state)
    {
        state = PlayerState.Empty;

        if (_player is null && !TryResolve(out _))
        {
            return false;
        }

        nint creature = _player!.Address;

        if (!TryReadVector(creature + OffPosition, out Vector3f position))
        {
            OnFailure();
            return false;
        }

        // orientation is a 3-float Vector. Treating (x, y) as a facing direction matches
        // how the GIT stores creature orientation; the sign convention is UNVERIFIED.
        float facing = 0f;
        if (TryReadVector(creature + OffOrientation, out Vector3f orientation))
        {
            facing = Angles.Normalize(MathF.Atan2(orientation.Y, orientation.X));
        }

        // SIGNED on purpose: a downed player's hit points go negative (the PC death
        // threshold is -10), and an unsigned read would turn -3 into 65533 — reading as
        // "alive and thriving" to every peer at the exact moment it matters most.
        int health = _memory.TryRead(creature + OffCurrentHitPoints, out short currentHp) ? currentHp : 0;
        int maxHealth = _memory.TryRead(creature + OffMaxHitPoints, out ushort maxHp) ? maxHp : 0;
        uint animation = ReadPlayedAnimation(creature);

        // The appearance.2da row travels with the avatar so the peer's phantom can wear
        // this player's actual look instead of the commoner blueprint.
        uint appearance = 0;
        if (_memory.TryRead(creature + OffAppearanceType, out ushort appearanceRow))
        {
            appearance = appearanceRow;
        }

        // The real module name ("end_m01aa"), not the area id. Area ids come from a
        // counter that resets on every module transition, so two different modules
        // routinely share one — which would make the module filter match items and peers
        // across a boundary they are not actually on the same side of. The name read
        // also fails during a transition, which usefully blanks the module until the new
        // world is stable. Falls back to the area id only if the name is unreadable.
        if (!EngineGlobals.TryReadModuleName(_memory, out string module)
            && _memory.TryReadUInt32(creature + OffAreaId, out uint areaId))
        {
            module = $"area{areaId:X}";
        }

        // Visible equipment travels as blueprint resrefs. An item's tag matches its
        // blueprint for stock gear; custom/mod items whose tag diverges simply fail to
        // materialise on the far side, which is the correct soft failure when the two
        // installs do not share a mod.
        string body = ReadEquippedResref(creature, InvSlotBody);
        string rightWeapon = ReadEquippedResref(creature, InvSlotRightWeapon);
        string leftWeapon = ReadEquippedResref(creature, InvSlotLeftWeapon);

        state = new PlayerState(
            position, facing, module, health, animation, appearance, maxHealth,
            body, rightWeapon, leftWeapon, ReadLocalFlags(creature));

        if (!state.IsPlausible)
        {
            OnFailure();
            state = PlayerState.Empty;
            return false;
        }

        _consecutiveFailures = 0;
        return true;
    }

    /// <summary>CSWSCreature + this = in-combat dword.</summary>
    private const int OffInCombat = 0x4E0;

    /// <summary>Latches "this player paused the game themselves" across the window where
    /// a peer's mirrored pause shares the same engine bit.</summary>
    private bool _ownPauseLatched;

    /// <summary>
    /// The presence bits that travel with the avatar: talking, paused, fighting.
    ///
    /// The pause bit is the player's OWN pause only. When the plugin is holding the pause
    /// bit on a remote peer's behalf, that mirrored pause must not be republished as
    /// ours — two instances would each mirror the other and latch paused forever. The
    /// engine has one bit for both, so "own" is decided when the bit RISES: set while no
    /// mirror was applied means the player did it, and that verdict is latched until the
    /// bit clears.
    /// </summary>
    /// <summary>
    /// The animation to replicate: the client twin's current played animation when the twin
    /// exists (captures weapon draws / swings / flourishes), else the server field (+0xD4,
    /// gait and scripted animations). A zero twin animation falls back too, since the twin
    /// reads 0 briefly around a spawn or transition.
    /// </summary>
    private uint ReadPlayedAnimation(nint creature)
    {
        if (_memory.TryReadPointer(creature + OffClientTwin, out nint twin) && twin != 0
            && _memory.TryReadUInt32(twin + OffTwinAnimation, out uint twinAnim) && twinAnim != 0)
        {
            return twinAnim;
        }

        _memory.TryReadUInt32(creature + OffAnimation, out uint anim);
        return anim;
    }

    private uint ReadLocalFlags(nint creature)
    {
        uint flags = 0;

        if (IsInDialogue)
        {
            flags |= PlayerState.FlagInDialogue;
        }

        bool paused = EngineGlobals.TryReadPauseMask(_memory, out byte pauseMask)
            && (pauseMask & EngineGlobals.PauseReasonPlayer) != 0;

        if (!paused)
        {
            _ownPauseLatched = false;
        }
        else if (!IsRemotePauseApplied)
        {
            _ownPauseLatched = true;
        }

        if (_ownPauseLatched)
        {
            flags |= PlayerState.FlagPaused;
        }

        if (_memory.TryReadInt32(creature + OffInCombat, out int inCombat) && inCombat != 0)
        {
            flags |= PlayerState.FlagInCombat;
        }

        // Downed comes from the plugin, which asks the engine directly (GetDeadTemp): a
        // party member at zero hit points is temp-dead, not dead, and hit points alone
        // cannot distinguish "downed" from "the read failed".
        if (IsLocalPlayerDowned)
        {
            flags |= PlayerState.FlagDowned;
        }

        return flags;
    }

    /// <summary>Plugin-published: the local PC is in the engine's temp-dead (downed) state.</summary>
    public bool IsLocalPlayerDowned =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockLocalDowned, out uint downed)
        && downed != 0;

    private bool IsRemotePauseApplied =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockRemotePauseApplied, out uint applied)
        && applied != 0;

    /// <summary>
    /// Clears the co-op state the plugin is driving so a disconnect leaves a clean game:
    /// zero remote peers (the plugin despawns the phantoms it spawned for them), the party
    /// freeze released, and the spawn command set idle. Best-effort — a disconnect must
    /// never throw.
    /// </summary>
    public void ClearCoopState()
    {
        if (_pluginBlock != 0)
        {
            _memory.TryWrite(_pluginBlock + BlockRemoteCount, 0u);
            _memory.TryWrite(_pluginBlock + BlockEnemyCount, 0u);
            _memory.TryWrite(_pluginBlock + BlockCommand, (uint)0);
        }

        ReleasePartyFreeze();
    }

    public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers)
    {
        // Preferred path: hand the peers to the in-process plugin, which spawns a real
        // creature per remote player and walks it with the engine's own movement step. That
        // gives ground snapping, walkmesh awareness and a walk animation, none of which an
        // external position poke can produce.
        if (_pluginBlock != 0)
        {
            return WriteToPlugin(peers);
        }

        if (_phantoms.Count == 0)
        {
            return false;
        }

        bool allOk = true;
        int count = Math.Min(peers.Count, _phantoms.Count);

        for (int i = 0; i < count; i++)
        {
            CreatureRef body = _phantoms[i];
            Vector3f target = peers[i].State.Position;

            if (!target.IsFinite)
            {
                continue;
            }

            // Writing the server-side position works because nothing else is driving this
            // creature. Measured: a write to an idle creature survives (0 engine overwrites
            // in 5s), while the same write to a party member running follow-AI is reverted
            // ~64 times a second. See docs/re/object-model.md.
            allOk &= _memory.TryWrite(body.Address + OffPosition, target.X);
            allOk &= _memory.TryWrite(body.Address + OffPosition + 4, target.Y);
            allOk &= _memory.TryWrite(body.Address + OffPosition + 8, target.Z);
        }

        return allOk;
    }

    /// <summary>
    /// Publishes remote players into the plugin's shared block and asks it to spawn and
    /// drive them. The plugin owns every engine call; we only fill in data.
    /// </summary>
    private bool WriteToPlugin(IReadOnlyList<RemotePeer> peers)
    {
        if (_player is null)
        {
            return false;
        }

        bool ok = true;

        // The plugin needs a valid creature to spawn near, and discovery lives here because
        // the bridge already knows which object is the player.
        ok &= _memory.TryWrite(_pluginBlock + BlockPlayerCreature, (uint)_player.Address);

        int count = Math.Min(peers.Count, BlockSlotCapacity);

        for (int i = 0; i < BlockSlotCapacity; i++)
        {
            nint slot = _pluginBlock + BlockSlots + (i * BlockSlotStride);

            if (i >= count)
            {
                ok &= _memory.TryWrite(slot + 0x00, 0u);
                continue;
            }

            RemotePeer peer = peers[i];
            ok &= _memory.TryWrite(slot + 0x04, peer.PeerId);
            ok &= _memory.TryWrite(slot + 0x08, peer.State.Position.X);
            ok &= _memory.TryWrite(slot + 0x0C, peer.State.Position.Y);
            ok &= _memory.TryWrite(slot + 0x10, peer.State.Position.Z);
            ok &= _memory.TryWrite(slot + 0x14, peer.State.Facing);
            ok &= _memory.TryWrite(slot + 0x18, peer.State.Health);

            // The raw animation value (walk 0x2712, run 0x2714, ...). The plugin uses it
            // to make the phantom RUN when its player runs; without it every phantom
            // walked at 1.6 u/s after a player moving at 3.96, which read as pure lag.
            ok &= _memory.TryWrite(slot + 0x1C, peer.State.Animation);

            // The peer's name, which the plugin sets as the phantom's first name so the
            // selection band identifies who is standing there instead of showing nothing.
            ok &= _memory.TryWriteAsciiString(slot + 0x20, peer.Name, BlockSlotNameLength);

            ok &= _memory.TryWrite(slot + 0x38, peer.State.Appearance);
            ok &= _memory.TryWrite(slot + 0x3C, peer.State.Flags);

            // Visible equipment, as blueprint resrefs the plugin equips on the phantom.
            nint equip = _pluginBlock + BlockEquipSlots + (i * BlockEquipStride);
            ok &= _memory.TryWriteAsciiString(equip, peer.State.EquippedBody, BlockEquipResrefLength);
            ok &= _memory.TryWriteAsciiString(
                equip + BlockEquipResrefLength, peer.State.EquippedRightWeapon, BlockEquipResrefLength);
            ok &= _memory.TryWriteAsciiString(
                equip + (2 * BlockEquipResrefLength), peer.State.EquippedLeftWeapon, BlockEquipResrefLength);

            // Active last, so the plugin never observes a half-written slot.
            ok &= _memory.TryWrite(slot + 0x00, 1u);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteCount, (uint)count);
        ok &= _memory.TryWrite(_pluginBlock + BlockCommand, CommandSpawnAndDrive);
        return ok;
    }

    // ---- ground items ----------------------------------------------------------

    /// <summary>Item sync needs the in-process plugin; there is no external fallback.</summary>
    public bool SupportsGroundItems => _pluginBlock != 0;

    /// <summary>
    /// Declares the desired ground-item set to the plugin. The plugin reconciles the
    /// world toward it on its own frames; this only assigns items to slots, publishes
    /// new wants, and withdraws stale ones.
    /// </summary>
    public bool TryWriteGroundItems(IReadOnlyList<GroundItem> items)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = true;

        // Withdraw slots whose item is no longer in the desired set.
        for (int i = 0; i < BlockItemCapacity; i++)
        {
            uint occupant = _itemSlotIds[i];
            if (occupant == 0)
            {
                continue;
            }

            bool stillWanted = false;
            for (int j = 0; j < items.Count; j++)
            {
                if (items[j].ItemId == occupant)
                {
                    stillWanted = true;
                    break;
                }
            }

            if (stillWanted)
            {
                continue;
            }

            nint slot = _pluginBlock + BlockItemSlots + (i * BlockItemSlotStride);
            ok &= _memory.TryWrite(slot + 0x00, 0u);

            // The slot stays reserved until the plugin confirms it has let the object
            // go; reusing it earlier could destroy the wrong item's body.
            if (ReadItemState(i) == ItemStateNone)
            {
                _itemSlotIds[i] = 0;
            }
        }

        // Publish items that have no slot yet.
        for (int j = 0; j < items.Count; j++)
        {
            GroundItem item = items[j];

            if (item.ItemId == 0 || FindItemSlot(item.ItemId) >= 0)
            {
                continue;
            }

            int free = -1;
            for (int i = 0; i < BlockItemCapacity; i++)
            {
                if (_itemSlotIds[i] == 0 && ReadItemState(i) == ItemStateNone)
                {
                    free = i;
                    break;
                }
            }

            if (free < 0)
            {
                ok = false;
                continue;
            }

            nint slot = _pluginBlock + BlockItemSlots + (free * BlockItemSlotStride);
            ok &= _memory.TryWrite(slot + 0x04, item.ItemId);
            ok &= _memory.TryWrite(slot + 0x08, item.Position.X);
            ok &= _memory.TryWrite(slot + 0x0C, item.Position.Y);
            ok &= _memory.TryWrite(slot + 0x10, item.Position.Z);
            ok &= _memory.TryWriteAsciiString(slot + 0x14, item.Resref, BlockItemResrefLength);

            // Wanted last, so the plugin never observes a half-written slot.
            ok &= _memory.TryWrite(slot + 0x00, 1u);

            _itemSlotIds[free] = item.ItemId;
        }

        return ok;
    }

    /// <summary>
    /// Item ids the plugin reports as vanished from the floor: the local player picked
    /// them up (or the module changed under them). Announced every pump until the store
    /// removes them, which is harmless because removal is idempotent.
    /// </summary>
    public List<uint> CollectVanishedItems()
    {
        List<uint> vanished = [];

        if (_pluginBlock == 0)
        {
            return vanished;
        }

        for (int i = 0; i < BlockItemCapacity; i++)
        {
            if (_itemSlotIds[i] == 0)
            {
                continue;
            }

            nint status = _pluginBlock + BlockItemStatus + (i * BlockItemStatusStride);

            if (_memory.TryReadUInt32(status + 0x08, out uint state)
                && state == ItemStateVanished
                && _memory.TryReadUInt32(status + 0x00, out uint itemId)
                && itemId == _itemSlotIds[i])
            {
                vanished.Add(itemId);
            }
        }

        return vanished;
    }

    private int FindItemSlot(uint itemId)
    {
        for (int i = 0; i < BlockItemCapacity; i++)
        {
            if (_itemSlotIds[i] == itemId)
            {
                return i;
            }
        }

        return -1;
    }

    private uint ReadItemState(int slot)
    {
        nint status = _pluginBlock + BlockItemStatus + (slot * BlockItemStatusStride);
        return _memory.TryReadUInt32(status + 0x08, out uint state) ? state : ItemStateNone;
    }

    // ---- world state -------------------------------------------------------------

    private byte[]? _shadowBooleans;
    private sbyte[]? _shadowNumbers;

    /// <summary>
    /// Variables that are per-player rather than world truth, and would do damage if
    /// replicated. `K_LAST_MODULE` / `K_LAST_LOCATION` / `K_STUNT_MODULE` are literally
    /// "where this player last was"; `K_CURRENT_PLANET` is the same idea. Matching is on
    /// the name, so this survives a catalogue that renumbers.
    /// </summary>
    private static readonly string[] NotWorldTruth =
        ["K_CURRENT_PLANET", "K_LAST_MODULE", "K_LAST_LOCATION", "K_STUNT_MODULE"];

    private readonly HashSet<uint> _excludedFlags = [];

    public bool SupportsWorldState => _globals.IsResolved;

    public IReadOnlyList<GlobalVariable> GlobalCatalogue => _globals.Catalogue;

    public uint GlobalCatalogueDigest => _globals.CatalogueDigest;

    /// <summary>The catalogue digest for the compatibility handshake; 0 until resolved.</summary>
    public uint GlobalsCatalogueDigest => _globals.IsResolved ? _globals.CatalogueDigest : 0;

    private SaveDigestCache? _saveDigests;
    private bool _saveDigestsResolved;

    /// <summary>
    /// The file-side digest source for the world handshake, resolved once from the game
    /// process's own executable path (correct whichever install this instance runs from —
    /// GOG, Steam, or a second co-op copy). Null when the path cannot be read, in which
    /// case both digests stay 0 = unknown, which only ever warns.
    /// </summary>
    private SaveDigestCache? SaveDigests
    {
        get
        {
            if (!_saveDigestsResolved)
            {
                _saveDigestsResolved = true;

                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(_memory.ProcessId);
                    string? gameDirectory = Path.GetDirectoryName(process.MainModule?.FileName);
                    if (gameDirectory is not null)
                    {
                        _saveDigests = new SaveDigestCache(gameDirectory);
                    }
                }
                catch (Exception)
                {
                    // No access to the process path: the save digests stay unknown.
                }
            }

            return _saveDigests;
        }
    }

    /// <summary>The staged/current save's identity digest for the world handshake; 0 until
    /// (unless) resolved — see <see cref="SaveDigest"/>.</summary>
    public uint SaveIdentityDigest => SaveDigests?.SaveIdentity ?? 0;

    /// <summary>The named module's GIT-id digest for the world handshake; 0 when unknown.</summary>
    public uint ModuleGitDigest(string module) => SaveDigests?.ModuleGit(module) ?? 0;

    /// <summary>
    /// Locates the global variable table and takes the baseline snapshot. Called from
    /// TryResolve, since the catalogue is fixed for as long as a save is loaded.
    /// </summary>
    private void TryResolveGlobals()
    {
        if (!_globals.TryResolve(out _))
        {
            return;
        }

        _shadowBooleans = new byte[GlobalVariableTable.BooleanByteCount];
        _shadowNumbers = new sbyte[GlobalVariableTable.NumberCount];
        _globals.TryReadValues(_shadowBooleans, _shadowNumbers);

        _excludedFlags.Clear();
        foreach (GlobalVariable variable in _globals.Catalogue)
        {
            if (NotWorldTruth.Contains(variable.Name, StringComparer.OrdinalIgnoreCase)
                || variable.Name.StartsWith("k_vis_", StringComparison.OrdinalIgnoreCase))
            {
                _excludedFlags.Add(variable.FlagId);
            }
        }
    }

    /// <summary>
    /// Diffs the game's world state against the shadow copy and reports what moved. The
    /// whole comparison is 613 bytes, so this is cheap enough to run every tick and the
    /// common case exits after two memory reads that match.
    /// </summary>
    public List<KeyValuePair<uint, sbyte>> CollectChangedWorldState()
    {
        List<KeyValuePair<uint, sbyte>> changed = [];

        if (!_globals.IsResolved || _shadowBooleans is null || _shadowNumbers is null)
        {
            return changed;
        }

        byte[] booleans = new byte[GlobalVariableTable.BooleanByteCount];
        sbyte[] numbers = new sbyte[GlobalVariableTable.NumberCount];

        if (!_globals.TryReadValues(booleans, numbers))
        {
            return changed;
        }

        for (int byteIndex = 0; byteIndex < booleans.Length; byteIndex++)
        {
            if (booleans[byteIndex] == _shadowBooleans[byteIndex])
            {
                continue;
            }

            for (int bit = 0; bit < 8; bit++)
            {
                int index = (byteIndex * 8) + bit;
                bool now = GlobalVariableTable.GetBit(booleans, index);

                if (now == GlobalVariableTable.GetBit(_shadowBooleans, index))
                {
                    continue;
                }

                uint flagId = GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, index);
                if (!_excludedFlags.Contains(flagId))
                {
                    changed.Add(new KeyValuePair<uint, sbyte>(flagId, now ? (sbyte)1 : (sbyte)0));
                }
            }

            _shadowBooleans[byteIndex] = booleans[byteIndex];
        }

        for (int index = 0; index < numbers.Length; index++)
        {
            if (numbers[index] == _shadowNumbers[index])
            {
                continue;
            }

            uint flagId = GlobalVariableTable.MakeFlagId(GlobalVariableType.Number, index);
            if (!_excludedFlags.Contains(flagId))
            {
                changed.Add(new KeyValuePair<uint, sbyte>(flagId, numbers[index]));
            }

            _shadowNumbers[index] = numbers[index];
        }

        return changed;
    }

    /// <summary>
    /// Writes a peer's world-state value into the game, and updates the shadow in the same
    /// breath. Skipping the shadow update is the classic failure here: the next scan would
    /// see the value as a fresh local change and echo it back, and the two instances would
    /// broadcast at each other forever.
    /// </summary>
    public bool TryApplyWorldState(uint flagId, sbyte value)
    {
        if (!_globals.IsResolved || _shadowBooleans is null || _shadowNumbers is null)
        {
            return false;
        }

        if (_excludedFlags.Contains(flagId))
        {
            return false;
        }

        var type = (GlobalVariableType)(flagId >> 16);
        int index = (int)(flagId & 0xFFFF);

        switch (type)
        {
            case GlobalVariableType.Boolean:
                if (index >= GlobalVariableTable.BooleanByteCount * 8
                    || !_globals.TryWriteBoolean(index, value != 0))
                {
                    return false;
                }

                int mask = 1 << (7 - (index & 7));
                _shadowBooleans[index >> 3] = (byte)(value != 0
                    ? _shadowBooleans[index >> 3] | mask
                    : _shadowBooleans[index >> 3] & ~mask);
                return true;

            case GlobalVariableType.Number:
                if (index >= GlobalVariableTable.NumberCount || !_globals.TryWriteNumber(index, value))
                {
                    return false;
                }

                _shadowNumbers[index] = value;
                return true;

            default:
                // Locations and strings are deliberately not replicated: every one of them
                // in a real save is a per-player scratchpad or cutscene staging value.
                return false;
        }
    }

    // ---- doors -----------------------------------------------------------------

    /// <summary>CSWSDoor vtable; every 4-byte occurrence in private memory is a door.</summary>
    public const uint DoorVtable = 0x007498B0;

    /// <summary>CSWSDoor + this = committed open state: 0 closed, 1/2 open, 3 destroyed.</summary>
    private const int DoorOpenState = 0x2CC;

    /// <summary>CSWSDoor + this = locked byte.</summary>
    private const int DoorLockedByte = 0x2C4;

    // Door slots in the plugin's SharedBlock, version 4.
    private const int BlockDoorCount = 0x690;
    private const int BlockDoorSlots = 0x698;
    private const int BlockDoorStride = 0x08;
    private const int BlockDoorCapacity = 32;

    /// <summary>Last observed (open, locked) per door id, in the current module.</summary>
    private readonly Dictionary<uint, (byte Open, byte Locked)> _doorShadow = [];

    /// <summary>Lock state each door had when first seen, so "was unlocked during play"
    /// can be told apart from "ships unlocked" — only the former is worth announcing.</summary>
    private readonly Dictionary<uint, byte> _doorBaselineLocked = [];

    private string _doorModule = string.Empty;
    private byte _doorModuleHash;
    private bool _doorBaselined;
    private long _nextDoorScanTicks;

    /// <summary>Door sync needs the plugin to apply swings on the game's thread.</summary>
    public bool SupportsDoorSync => _pluginBlock != 0;

    /// <summary>
    /// Diffs the local module's doors against the last scan and reports what advanced, as
    /// door-typed world flags. The first scan after entering a module is a silent
    /// baseline, exactly like the globals: pre-existing state is not worth announcing,
    /// only changes made while the session is live. Scans are throttled to one per
    /// second, since they walk the whole address space by vtable.
    /// </summary>
    public List<KeyValuePair<uint, sbyte>> CollectChangedDoorState()
    {
        List<KeyValuePair<uint, sbyte>> changed = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return changed;
        }

        if (!string.Equals(module, _doorModule, StringComparison.OrdinalIgnoreCase))
        {
            _doorModule = module;
            _doorModuleHash = DoorFlags.HashModule(module);
            _doorShadow.Clear();
            _doorBaselineLocked.Clear();
            _doorBaselined = false;
            _nextDoorScanTicks = 0;
        }

        long now = Environment.TickCount64;
        if (now < _nextDoorScanTicks)
        {
            return changed;
        }

        _nextDoorScanTicks = now + 1000;

        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(DoorVtable));
        PatternScanner scanner = new(_memory);

        foreach (nint address in scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 256))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or > 0xFFFF
                || !_memory.TryRead(address + DoorOpenState, out byte open)
                || !_memory.TryRead(address + DoorLockedByte, out byte locked))
            {
                continue;
            }

            if (!_doorBaselineLocked.ContainsKey(objectId))
            {
                _doorBaselineLocked[objectId] = locked;
            }

            if (_doorShadow.TryGetValue(objectId, out (byte Open, byte Locked) last)
                && last.Open == open && last.Locked == locked)
            {
                continue;
            }

            bool firstSighting = !_doorShadow.ContainsKey(objectId);
            _doorShadow[objectId] = (open, locked);

            if (!_doorBaselined && firstSighting)
            {
                continue;
            }

            // The value is absolute; the store's max-merge discards anything that is not
            // an advance, so a door closing again simply produces a no-op.
            bool unlockedDuringPlay = locked == 0 && _doorBaselineLocked[objectId] != 0;
            changed.Add(new KeyValuePair<uint, sbyte>(
                DoorFlags.MakeKey(_doorModuleHash, objectId),
                DoorFlags.MakeValue(open, unlockedDuringPlay)));
        }

        _doorBaselined = true;
        return changed;
    }

    /// <summary>
    /// Publishes the session's door truth for the LOCAL module into the plugin, which
    /// applies it through the engine's own door paths. Door flags for other modules are
    /// held back; they apply when their module is entered and this runs again.
    /// </summary>
    public bool TryWriteDoorStates(IReadOnlyList<KeyValuePair<uint, sbyte>> flags)
    {
        if (_pluginBlock == 0 || _doorModule.Length == 0)
        {
            return false;
        }

        bool ok = true;
        int slot = 0;

        foreach (KeyValuePair<uint, sbyte> flag in flags)
        {
            if (slot >= BlockDoorCapacity)
            {
                break;
            }

            if (!DoorFlags.IsDoorKey(flag.Key)
                || DoorFlags.ModuleHashOf(flag.Key) != _doorModuleHash
                || flag.Value <= 0)
            {
                continue;
            }

            nint address = _pluginBlock + BlockDoorSlots + (slot * BlockDoorStride);
            ok &= _memory.TryWrite(address + 0x04, DoorFlags.OpenStateOf(flag.Value));
            ok &= _memory.TryWrite(address + 0x05, DoorFlags.UnlockedOf(flag.Value) ? (byte)1 : (byte)0);

            // Object id last, so the plugin never reads a slot whose id and payload
            // belong to different doors.
            ok &= _memory.TryWrite(address + 0x00, DoorFlags.ObjectIdOf(flag.Key));
            slot++;
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockDoorCount, (uint)slot);
        return ok;
    }

    // ---- placeables ------------------------------------------------------------
    //
    // Chests, corpses, switches, containers. The door channel's twin: same object-id keying,
    // same reliable max-merge flag transport (tag 0x50), same silent baseline on module entry.
    // See docs/research/placeables-containers.md.

    /// <summary>CSWSPlaceable vtable; every 4-byte occurrence in private memory is a placeable.</summary>
    public const uint PlaceableVtable = 0x007494D0;

    /// <summary>CSWSPlaceable + this = locked int (nonzero = locked).</summary>
    private const int PlaceableLockedField = 0x260;

    /// <summary>CSWSPlaceable + this = open int (nonzero = opened/searched).</summary>
    private const int PlaceableOpenField = 0x338;

    /// <summary>CSWSPlaceable + this = trap int (nonzero = trapped; cleared = disarmed).</summary>
    private const int PlaceableTrapField = 0x278;

    // Placeable slots in the plugin's SharedBlock, version 11.
    private const int BlockPlaceableCount = 0xB58;
    private const int BlockPlaceableSlots = 0xB60;
    private const int BlockPlaceableStride = 0x08;
    private const int BlockPlaceableCapacity = 32;

    private readonly Dictionary<uint, (byte Open, byte Locked, byte Trapped)> _placeableShadow = [];
    private readonly Dictionary<uint, byte> _placeableBaselineLocked = [];
    private readonly Dictionary<uint, byte> _placeableBaselineTrapped = [];
    private string _placeableModule = string.Empty;
    private byte _placeableModuleHash;
    private bool _placeableBaselined;
    private long _nextPlaceableScanTicks;

    public bool SupportsPlaceableSync => _pluginBlock != 0;

    /// <summary>
    /// Diffs the local module's placeables against the last scan and reports what advanced
    /// (opened, unlocked) as placeable-typed world flags. First scan per module is a silent
    /// baseline; scans throttled to one per second like doors.
    /// </summary>
    public List<KeyValuePair<uint, sbyte>> CollectChangedPlaceableState()
    {
        List<KeyValuePair<uint, sbyte>> changed = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return changed;
        }

        if (!string.Equals(module, _placeableModule, StringComparison.OrdinalIgnoreCase))
        {
            _placeableModule = module;
            _placeableModuleHash = PlaceableFlags.HashModule(module);
            _placeableShadow.Clear();
            _placeableBaselineLocked.Clear();
            _placeableBaselineTrapped.Clear();
            _placeableBaselined = false;
            _nextPlaceableScanTicks = 0;
        }

        long now = Environment.TickCount64;
        if (now < _nextPlaceableScanTicks)
        {
            return changed;
        }

        _nextPlaceableScanTicks = now + 1000;

        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(PlaceableVtable));
        PatternScanner scanner = new(_memory);

        foreach (nint address in scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 512))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or > 0xFFFF
                || !_memory.TryReadInt32(address + PlaceableOpenField, out int openInt)
                || !_memory.TryReadInt32(address + PlaceableLockedField, out int lockedInt)
                || !_memory.TryReadInt32(address + PlaceableTrapField, out int trapInt))
            {
                continue;
            }

            byte open = openInt != 0 ? (byte)1 : (byte)0;
            byte locked = lockedInt != 0 ? (byte)1 : (byte)0;
            byte trapped = trapInt != 0 ? (byte)1 : (byte)0;

            if (!_placeableBaselineLocked.ContainsKey(objectId))
            {
                _placeableBaselineLocked[objectId] = locked;
                _placeableBaselineTrapped[objectId] = trapped;
            }

            if (_placeableShadow.TryGetValue(objectId, out (byte Open, byte Locked, byte Trapped) last)
                && last.Open == open && last.Locked == locked && last.Trapped == trapped)
            {
                continue;
            }

            bool firstSighting = !_placeableShadow.ContainsKey(objectId);
            _placeableShadow[objectId] = (open, locked, trapped);

            if (!_placeableBaselined && firstSighting)
            {
                continue;
            }

            bool unlockedDuringPlay = locked == 0 && _placeableBaselineLocked[objectId] != 0;
            bool disarmedDuringPlay = trapped == 0 && _placeableBaselineTrapped.GetValueOrDefault(objectId) != 0;
            changed.Add(new KeyValuePair<uint, sbyte>(
                PlaceableFlags.MakeKey(_placeableModuleHash, objectId),
                PlaceableFlags.MakeValue(open != 0, unlockedDuringPlay, disarmedDuringPlay)));
        }

        _placeableBaselined = true;
        return changed;
    }

    /// <summary>
    /// Publishes the session's placeable truth for the local module into the plugin, which
    /// applies each as a field write on the game's thread. Flags for other modules are held
    /// back until that module loads.
    /// </summary>
    public bool TryWritePlaceableStates(IReadOnlyList<KeyValuePair<uint, sbyte>> flags)
    {
        if (_pluginBlock == 0 || _placeableModule.Length == 0)
        {
            return false;
        }

        bool ok = true;
        int slot = 0;

        foreach (KeyValuePair<uint, sbyte> flag in flags)
        {
            if (slot >= BlockPlaceableCapacity)
            {
                break;
            }

            if (!PlaceableFlags.IsPlaceableKey(flag.Key)
                || PlaceableFlags.ModuleHashOf(flag.Key) != _placeableModuleHash
                || flag.Value <= 0)
            {
                continue;
            }

            nint address = _pluginBlock + BlockPlaceableSlots + (slot * BlockPlaceableStride);
            ok &= _memory.TryWrite(address + 0x04, PlaceableFlags.OpenOf(flag.Value) ? (byte)1 : (byte)0);
            ok &= _memory.TryWrite(address + 0x05, PlaceableFlags.UnlockedOf(flag.Value) ? (byte)1 : (byte)0);
            ok &= _memory.TryWrite(address + 0x06, PlaceableFlags.DisarmedOf(flag.Value) ? (byte)1 : (byte)0);

            // Object id last, so the plugin never reads a slot whose id and payload disagree.
            ok &= _memory.TryWrite(address + 0x00, PlaceableFlags.ObjectIdOf(flag.Key));
            slot++;
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockPlaceableCount, (uint)slot);
        return ok;
    }

    // ---- container loot --------------------------------------------------------
    //
    // Keeps a shared chest/corpse from duplicating its contents: when the host loots an item
    // out of a container, that item is removed from the same container on the client too.
    // Host-authoritative and object-id keyed (same save => same ids), gated on the Doors
    // capability. See docs/research/placeables-containers.md §2 (all VERIFIED there).

    /// <summary>CSWSPlaceable + this = CItemRepository* (0 when the placeable has no inventory).</summary>
    private const int PlaceableRepositoryField = 0x36C;

    /// <summary>CItemRepository + this = flat array of item OBJECT IDs (DWORDs, not pointers).</summary>
    private const int RepositoryItemArray = 0x0C;

    /// <summary>CItemRepository + this = item count.</summary>
    private const int RepositoryItemCount = 0x10;

    // Container-loot removal region, pre-reserved at 0xD00..0xE00 in the shared block (the
    // block's TotalSize and version already cover it, so nothing there changes).
    private const int BlockLootCount = 0xD00;
    private const int BlockLootSlots = 0xD08;
    private const int BlockLootStride = 0x08;
    private const int BlockLootCapacity = 24;

    /// <summary>Last observed item-id set per container in the current module.</summary>
    private readonly Dictionary<uint, HashSet<uint>> _containerShadow = [];
    private string _lootModule = string.Empty;
    private bool _lootBaselined;
    private long _nextLootScanTicks;

    /// <summary>Applying loot removal needs the plugin (RemoveItem is a main-thread engine call).</summary>
    public bool SupportsContainerLoot => _pluginBlock != 0;

    /// <summary>
    /// Diffs each container's item-id set against the last scan and reports items that left
    /// during play, as (container, item) removals. The first scan per module is a silent
    /// baseline — pre-existing contents are not "loot events" — exactly like the door and
    /// placeable scans, and the scan is throttled to one per second for the same reason (it
    /// walks the address space by the placeable vtable).
    /// </summary>
    public List<ContainerLootRemoval> CollectContainerLootRemovals()
    {
        List<ContainerLootRemoval> removals = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return removals;
        }

        if (!string.Equals(module, _lootModule, StringComparison.OrdinalIgnoreCase))
        {
            _lootModule = module;
            _containerShadow.Clear();
            _lootBaselined = false;
            _nextLootScanTicks = 0;
        }

        long now = Environment.TickCount64;
        if (now < _nextLootScanTicks)
        {
            return removals;
        }

        _nextLootScanTicks = now + 1000;

        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(PlaceableVtable));
        PatternScanner scanner = new(_memory);

        HashSet<uint> seenContainers = [];

        foreach (nint address in scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 512))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint containerId)
                || containerId is 0 or > 0xFFFF)
            {
                continue;
            }

            // Only a container has a repository; a plain placeable's field is 0. This also
            // rejects a mis-scanned hit, whose +0x36C is unlikely to be a valid pointer.
            if (!_memory.TryReadPointer(address + PlaceableRepositoryField, out nint repo) || repo == 0)
            {
                continue;
            }

            if (!_memory.TryReadInt32(repo + RepositoryItemCount, out int count)
                || count is < 0 or > 4096
                || !_memory.TryReadPointer(repo + RepositoryItemArray, out nint ids)
                || (count > 0 && ids == 0))
            {
                continue;
            }

            HashSet<uint> current = [];
            for (int i = 0; i < count; i++)
            {
                if (_memory.TryReadUInt32(ids + (nint)(i * 4), out uint itemId)
                    && itemId is > 0 and < 0x7F000000)
                {
                    current.Add(itemId);
                }
            }

            seenContainers.Add(containerId);

            // An item present last scan and gone now left the container during play. Only
            // announce once the baseline is established, so the first sighting is silent.
            if (_lootBaselined && _containerShadow.TryGetValue(containerId, out HashSet<uint>? previous))
            {
                foreach (uint itemId in previous)
                {
                    if (!current.Contains(itemId))
                    {
                        removals.Add(new ContainerLootRemoval(containerId, itemId));
                    }
                }
            }

            _containerShadow[containerId] = current;
        }

        // Forget containers no longer present, so a reused id never diffs against a stranger.
        if (_containerShadow.Count != seenContainers.Count)
        {
            List<uint> gone = [.. _containerShadow.Keys.Where(id => !seenContainers.Contains(id))];
            foreach (uint id in gone)
            {
                _containerShadow.Remove(id);
            }
        }

        _lootBaselined = true;
        return removals;
    }

    /// <summary>
    /// Publishes the session's container-loot removals into the plugin, which deletes each
    /// looted item from its own copy of the container on the game's thread. Idempotent — an
    /// item already gone resolves to a no-op — so re-declaring the set every tick is safe.
    /// Writes at most <see cref="BlockLootCapacity"/>, the newest (the store keeps them
    /// oldest-first, and the newest are the ones least likely to be applied yet).
    /// </summary>
    public bool TryWriteContainerLoot(IReadOnlyList<ContainerLootRemoval> removals)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = true;
        int count = Math.Min(removals.Count, BlockLootCapacity);
        int start = removals.Count - count;

        for (int i = 0; i < count; i++)
        {
            ContainerLootRemoval removal = removals[start + i];
            nint slot = _pluginBlock + BlockLootSlots + (i * BlockLootStride);

            ok &= _memory.TryWrite(slot + 0x04, removal.ItemId);

            // Container id last, so the plugin never reads a slot whose id and payload disagree.
            ok &= _memory.TryWrite(slot + 0x00, removal.ContainerId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockLootCount, (uint)count);
        return ok;
    }

    // ---- encounters ------------------------------------------------------------

    /// <summary>CSWSEncounter vtable.</summary>
    public const uint EncounterVtable = 0x00749D98;

    /// <summary>CSWSEncounter + this = active dword (symbol database: active).</summary>
    private const int EncounterActive = 0x238;

    /// <summary>CSWSEncounter + this = started dword (symbol database: started).</summary>
    private const int EncounterStarted = 0x278;

    private string _encounterModule = string.Empty;

    /// <summary>
    /// Turns off every encounter in the local module: active = 0, started = 1. Called on
    /// the instance that does NOT own enemies. Encounter spawns are the one engine-side
    /// source of object-id drift — a creature spawned by an encounter in one instance and
    /// not the other shifts the id counter for everything created afterwards, and
    /// identical ids are the key the whole combat replication rests on. The host keeps
    /// its encounters; whatever they spawn reaches the client through the enemy channel.
    ///
    /// Plain dword writes, so this runs externally. Re-applied once per module.
    /// </summary>
    public int DisableLocalEncounters()
    {
        if (!EngineGlobals.TryReadModuleName(_memory, out string module)
            || string.Equals(module, _encounterModule, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(EncounterVtable));
        PatternScanner scanner = new(_memory);
        int disabled = 0;

        foreach (nint address in scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 128))
        {
            // Sanity: a real encounter has its object id where CSWSObject keeps it.
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or >= 0x7F000000)
            {
                continue;
            }

            bool ok = _memory.TryWrite(address + EncounterActive, 0);
            ok &= _memory.TryWrite(address + EncounterStarted, 1);

            if (ok)
            {
                disabled++;
            }
        }

        // Remember the module even if there were no encounters, so the scan is not
        // repeated every pump for a module that simply has none.
        _encounterModule = module;
        return disabled;
    }

    // ---- journal ---------------------------------------------------------------

    /// <summary>CServerExoAppInternal + this = party table (embedded CSWPartyTable).</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = quest journal { SJournalEntry* data; int size; int cap }.</summary>
    private const int PartyTableJournal = 0x11C;

    private const int JournalEntryStride = 0x3C;
    private const int JournalEntryTag = 0x18;    // CExoString { char* @0, len @4 }
    private const int JournalEntryState = 0x20;

    /// <summary>What this game's journal held when last scanned (or what we last applied),
    /// so only genuine advances are announced and nothing echoes.</summary>
    private readonly Dictionary<string, int> _journalShadow = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reading is external; applying goes through the plugin (SetState must run
    /// on the game's thread, and it is also what refreshes the journal GUI).</summary>
    public bool SupportsJournalSync => _pluginBlock != 0;

    public List<KeyValuePair<string, int>> CollectChangedJournal()
    {
        List<KeyValuePair<string, int>> changes = [];

        foreach ((string tag, int state) in ReadJournalEntries())
        {
            if (_journalShadow.TryGetValue(tag, out int known) && known >= state)
            {
                continue;
            }

            _journalShadow[tag] = state;
            changes.Add(new KeyValuePair<string, int>(tag, state));
        }

        return changes;
    }

    /// <summary>
    /// Asks the plugin to advance a quest entry. One request in flight at a time —
    /// journal changes are rare, and the pump retries every tick until the plugin has
    /// caught up. Returns false when the game is already at or past the state.
    /// </summary>
    public bool TryApplyJournalEntry(string questTag, int state)
    {
        if (_pluginBlock == 0
            || string.IsNullOrEmpty(questTag)
            || questTag.Length >= BlockJournalTagLength)
        {
            return false;
        }

        if (_journalShadow.TryGetValue(questTag, out int known) && known >= state)
        {
            return false;
        }

        // Only one request slot; wait for the plugin to acknowledge the previous one.
        if (!_memory.TryReadUInt32(_pluginBlock + BlockJournalSerial, out uint serial)
            || !_memory.TryReadUInt32(_pluginBlock + BlockJournalApplied, out uint appliedSerial)
            || serial != appliedSerial)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockJournalTag, questTag, BlockJournalTagLength);
        ok &= _memory.TryWrite(_pluginBlock + BlockJournalState, state);

        // Serial last, so the plugin never sees a half-written request.
        ok &= _memory.TryWrite(_pluginBlock + BlockJournalSerial, serial + 1);

        if (ok)
        {
            // Optimistic: the engine's own SetState refuses to lower a state, so even a
            // failed apply cannot make this shadow wrong in a way that loses progress.
            _journalShadow[questTag] = state;
        }

        return ok;
    }

    private IEnumerable<(string Tag, int State)> ReadJournalEntries()
    {
        if (!EngineGlobals.TryGetServerInternal(_memory, out nint serverInternal))
        {
            yield break;
        }

        nint journal = serverInternal + PartyTableOffset + PartyTableJournal;

        if (!_memory.TryReadPointer(journal, out nint data) || data == 0
            || !_memory.TryReadInt32(journal + 4, out int count)
            || count is <= 0 or > 512)
        {
            yield break;
        }

        for (int i = 0; i < count; i++)
        {
            nint entry = data + (i * JournalEntryStride);
            string tag = ReadExoString(entry + JournalEntryTag);

            if (tag.Length is 0 or >= BlockJournalTagLength)
            {
                continue;
            }

            if (!_memory.TryReadInt32(entry + JournalEntryState, out int state))
            {
                continue;
            }

            yield return (tag, state);
        }
    }

    // ---- shared pause ----------------------------------------------------------

    /// <summary>Pause sync needs the plugin: only an in-process SetPauseState call
    /// actually stops the world timer.</summary>
    public bool SupportsPauseSync => _pluginBlock != 0;

    /// <summary>Tells the plugin whether any remote peer currently holds a pause.</summary>
    public bool TrySetRemotePause(bool desired) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockRemotePauseDesired, desired ? 1u : 0u);

    // ---- in-game Connect flow --------------------------------------------------

    private uint _lastConnectSerial;

    /// <summary>Opts the plugin into the main-menu Co-op button and remembers the current
    /// request serial so the first poll does not fire spuriously.</summary>
    public bool EnableConnectMenu(bool enabled)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        _memory.TryReadUInt32(_pluginBlock + BlockConnectSerial, out _lastConnectSerial);
        return _memory.TryWrite(_pluginBlock + BlockConnectEnable, enabled ? 1u : 0u);
    }

    /// <summary>
    /// True (once) when the player has pressed the Co-op button since the last check.
    /// <paramref name="typedHost"/> receives any host IP entered in-game (empty when the
    /// button carries no address, i.e. coop.ini decides).
    /// </summary>
    public bool TryTakeConnectRequest(out string typedHost)
    {
        typedHost = string.Empty;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockConnectSerial, out uint serial)
            || serial == _lastConnectSerial)
        {
            return false;
        }

        _lastConnectSerial = serial;

        Span<byte> buffer = stackalloc byte[BlockConnectHostLength];
        if (_memory.TryRead(_pluginBlock + BlockConnectHost, buffer))
        {
            int end = buffer.IndexOf((byte)0);
            typedHost = System.Text.Encoding.ASCII.GetString(buffer[..(end < 0 ? buffer.Length : end)]);
        }

        return true;
    }

    /// <summary>Reports the connect status back to the plugin (for a menu status line).</summary>
    public bool SetConnectStatus(uint status) =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockConnectStatus, status);

    private uint _lastConsoleSerial;
    private uint _consoleReplySerial;

    /// <summary>
    /// True (once) when the in-game console has entered a new command line since the last
    /// check; <paramref name="commandLine"/> receives it (e.g. "connect 1.2.3.4 secret").
    /// </summary>
    public bool TryTakeConsoleCommand(out string commandLine)
    {
        commandLine = string.Empty;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockConsoleSerial, out uint serial)
            || serial == _lastConsoleSerial)
        {
            return false;
        }

        _lastConsoleSerial = serial;

        if (_memory.TryReadAsciiString(_pluginBlock + BlockConsoleCommand, BlockConsoleCommandLength, out string raw))
        {
            commandLine = raw.Trim();
        }

        return commandLine.Length > 0;
    }

    // ---- server browser ---------------------------------------------------------

    private const int BlockBrowserOpen = 0xE00;
    private const int BlockBrowserSelection = 0xE04;
    private const int BlockBrowserCount = 0xE08;
    private const int BlockBrowserConnectSerial = 0xE0C;
    private const int BlockBrowserListSerial = 0xE10;
    private const int BlockBrowserEntries = 0xE20;
    private const int BlockBrowserEntryStride = 0x40;
    private const int BlockBrowserEntryName = 0x00;
    private const int BlockBrowserEntryNameLength = 32;
    private const int BlockBrowserEntryAddress = 0x20;
    private const int BlockBrowserEntryAddressLength = 24;
    private const int BlockBrowserEntryPlayers = 0x38;
    private const int BlockBrowserEntryMaxPlayers = 0x39;
    private const int BlockBrowserCapacity = 8;

    // ---- co-op death handling (block version 14) --------------------------------

    /// <summary>The plugin owns the suppressions and the revive; without it there is no
    /// death mediation and the game behaves exactly as single-player.</summary>
    public bool SupportsDeathHandling => _pluginBlock != 0;

    private const int BlockLocalDowned = 0x1020;
    private const int BlockLocalDownedSerial = 0x1024;
    private const int BlockGameOverSerial = 0x1028;
    private const int BlockSuppressGameOver = 0x102C;
    private const int BlockSuppressSwap = 0x1030;
    private const int BlockReviveSerial = 0x1034;
    private const int BlockReviveApplied = 0x1038;
    private const int BlockReviveStatus = 0x103C;
    private const int BlockReviveHitPoints = 0x1040;

    private uint _lastGameOverSerial;
    private uint _reviveSerial;
    private bool _deathBaselined;

    /// <summary>
    /// Turns the two death suppressions on (or off). While a session is live the game must
    /// not swap control away from the player's own PC, nor drop this instance to the main
    /// menu on a party wipe — the session decides that jointly. Clearing
    /// <paramref name="suppressGameOver"/> lets the next attempt through, which is how a
    /// genuine, synchronised wipe is delivered.
    /// </summary>
    public bool SetDeathSuppression(bool suppressGameOver, bool suppressSwap)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        if (!_deathBaselined)
        {
            _deathBaselined = true;
            _memory.TryReadUInt32(_pluginBlock + BlockGameOverSerial, out _lastGameOverSerial);
            _memory.TryReadUInt32(_pluginBlock + BlockReviveSerial, out _reviveSerial);
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockSuppressGameOver, suppressGameOver ? 1u : 0u);
        ok &= _memory.TryWrite(_pluginBlock + BlockSuppressSwap, suppressSwap ? 1u : 0u);
        return ok;
    }

    /// <summary>
    /// True (once) when the engine tried to show the game-over screen and the plugin
    /// swallowed it — i.e. this instance's whole party is down. The caller decides whether
    /// the session is genuinely wiped (every player down) or this player is waiting for a
    /// rescue.
    /// </summary>
    public bool TryTakeGameOverAttempt()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockGameOverSerial, out uint serial)
            || serial == _lastGameOverSerial)
        {
            return false;
        }

        _lastGameOverSerial = serial;
        return true;
    }

    /// <summary>
    /// Stands the local PC back up at <paramref name="hitPoints"/> (0 = the plugin's
    /// default). Used when a peer has reached a downed player.
    /// </summary>
    public bool RequestRevive(int hitPoints = 0)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        // Hit points before the serial: the plugin reads the serial last, so it can never
        // see a bumped serial paired with a stale amount.
        bool ok = _memory.TryWrite(_pluginBlock + BlockReviveHitPoints, (uint)Math.Max(hitPoints, 0));
        ok &= _memory.TryWrite(_pluginBlock + BlockReviveSerial, ++_reviveSerial);
        return ok;
    }

    /// <summary>The plugin's verdict on the last revive: 0 = applied, non-zero = refused.</summary>
    public bool TryReadReviveStatus(out uint status)
    {
        status = 0;
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockReviveApplied, out uint applied)
            && applied == _reviveSerial
            && _memory.TryReadUInt32(_pluginBlock + BlockReviveStatus, out status);
    }

    private const int BlockBrowserHostSerial = 0xE18;

    private uint _browserListSerial;
    private uint _lastBrowserConnectSerial;
    private uint _lastBrowserHostSerial;
    private string _lastBrowserSignature = string.Empty;

    /// <summary>True while the in-game browser panel is on screen.</summary>
    public bool BrowserOpen =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockBrowserOpen, out uint open)
        && open != 0;

    /// <summary>
    /// Publishes the discovered servers for the in-game panel to draw. Writes the rows first and
    /// the count last, so the plugin can never read a count that outruns the rows. A cheap
    /// signature check keeps this from rewriting the same list every tick.
    /// </summary>
    public bool PublishBrowserServers(IReadOnlyList<(string Name, string Address, byte Players, byte MaxPlayers)> servers)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(servers.Count, BlockBrowserCapacity);

        string signature = string.Join('|', servers.Take(count).Select(s => $"{s.Name}@{s.Address}:{s.Players}/{s.MaxPlayers}"));
        if (signature == _lastBrowserSignature)
        {
            return true;
        }

        _lastBrowserSignature = signature;

        for (int i = 0; i < count; i++)
        {
            nint entry = _pluginBlock + BlockBrowserEntries + (i * BlockBrowserEntryStride);
            (string name, string address, byte players, byte max) = servers[i];

            _memory.TryWriteAsciiString(entry + BlockBrowserEntryName, name, BlockBrowserEntryNameLength);
            _memory.TryWriteAsciiString(entry + BlockBrowserEntryAddress, address, BlockBrowserEntryAddressLength);
            _memory.TryWrite(entry + BlockBrowserEntryPlayers, players);
            _memory.TryWrite(entry + BlockBrowserEntryMaxPlayers, max);
        }

        _memory.TryWrite(_pluginBlock + BlockBrowserCount, (uint)count);
        return _memory.TryWrite(_pluginBlock + BlockBrowserListSerial, ++_browserListSerial);
    }

    /// <summary>
    /// True (once) when the player confirmed a row in the panel; <paramref name="index"/> is the
    /// chosen server. The plugin writes the selection before the serial, so a bumped serial
    /// always pairs with the right index.
    /// </summary>
    public bool TryTakeBrowserConnect(out int index)
    {
        index = -1;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockBrowserConnectSerial, out uint serial)
            || serial == _lastBrowserConnectSerial)
        {
            return false;
        }

        _lastBrowserConnectSerial = serial;

        if (!_memory.TryReadUInt32(_pluginBlock + BlockBrowserSelection, out uint selection))
        {
            return false;
        }

        index = (int)selection;
        return true;
    }

    /// <summary>
    /// True (once) when the player pressed HOST in the in-game browser panel rather than
    /// picking a listed server.
    /// </summary>
    public bool TryTakeBrowserHost()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockBrowserHostSerial, out uint serial)
            || serial == _lastBrowserHostSerial)
        {
            return false;
        }

        _lastBrowserHostSerial = serial;
        return true;
    }

    /// <summary>Posts a status line for the in-game console to display.</summary>
    public bool PostConsoleReply(string line)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockConsoleReply, line, BlockConsoleReplyLength);
        ok &= _memory.TryWrite(_pluginBlock + BlockConsoleReplySerial, ++_consoleReplySerial);
        return ok;
    }

    private uint _chatSerial;

    /// <summary>Pushes one line to the always-on chat overlay (serial bumped last).</summary>
    public bool PostChatLine(string line)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockChatLine, line, BlockChatLineLength);
        ok &= _memory.TryWrite(_pluginBlock + BlockChatSerial, ++_chatSerial);
        return ok;
    }

    private uint _chatClearSerial;

    /// <summary>Wipes the in-game overlay log (the <c>clear</c> command).</summary>
    public bool PostChatClear() =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockChatClearSerial, ++_chatClearSerial);

    /// <summary>
    /// Writes the coop.ini-driven chat-overlay settings the plugin reads. <paramref
    /// name="opacity"/> is 0..100; <paramref name="anchor"/> is 0=top-left, 1=top-right,
    /// 2=bottom-left, 3=bottom-right.
    /// </summary>
    public bool SetChatOverlayConfig(bool enabled, uint opacity, uint anchor, uint maxLines, uint fadeMs)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockChatOverlayOpacity, Math.Clamp(opacity, 0u, 100u));
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayAnchor, Math.Min(anchor, 3u));
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayMaxLines, Math.Clamp(maxLines, 1u, 12u));
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayFadeMs, fadeMs);

        // Enable flag last, so the plugin never reads a half-written config.
        ok &= _memory.TryWrite(_pluginBlock + BlockChatOverlayEnabled, enabled ? 1u : 0u);
        return ok;
    }

    // ---- module travel ---------------------------------------------------------

    /// <summary>Travel needs the in-process plugin: the warp is an engine call.</summary>
    public bool SupportsTravel => _pluginBlock != 0;

    /// <summary>
    /// Asks the plugin to move this instance to another module through the engine's own
    /// move-to-module path. Fire-and-forget: the plugin flushes everything it spawned
    /// first (so nothing is serialised into the stored module on the way out), then the
    /// engine's MainLoop performs the transition exactly as the shipped `warp` cheat does.
    /// </summary>
    public bool TryRequestTravel(string module, string waypoint = "")
    {
        if (_pluginBlock == 0 || string.IsNullOrEmpty(module) || module.Length >= BlockTravelNameLength)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockTravelModule, module, BlockTravelNameLength);
        ok &= _memory.TryWriteAsciiString(_pluginBlock + BlockTravelWaypoint, waypoint, BlockTravelNameLength);

        // Serial last, so the plugin never observes a half-written request. Continue from
        // whatever the block already holds: the plugin outlives the agent, and restarting
        // from zero would replay whichever request the previous agent left behind.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockTravelSerial, out uint serial);
        ok &= _memory.TryWrite(_pluginBlock + BlockTravelSerial, serial + 1);
        return ok;
    }

    // ---- enemies ---------------------------------------------------------------

    /// <summary>Enemy sync needs the in-process plugin to apply health and deaths.</summary>
    public bool SupportsEnemySync => _pluginBlock != 0 && _player is not null;

    /// <summary>CClientExoAppInternal + this = input class. 3 is dialog.</summary>
    private const int ClientInputClass = 0x9C;

    private const int InputClassDialog = 3;

    /// <summary>
    /// True while this player is in a conversation.
    ///
    /// Matters for combat, not just presentation: the dialogue system disarms every
    /// hostile creature in the area on every dialog tick
    /// (<c>CSWSDialog::ClearAllPlayerHostileActions</c>, 0x005A0940). Since enemies are
    /// host-authoritative, publishing enemy state while talking would export that
    /// pacification to a peer who is still mid-fight.
    /// </summary>
    public bool IsInDialogue =>
        EngineGlobals.TryGetClientInternal(_memory, out nint clientInternal)
        && _memory.TryReadInt32(clientInternal + ClientInputClass, out int inputClass)
        && inputClass == InputClassDialog;

    /// <summary>
    /// Reads the speaker + line the plugin has published for the local player's current
    /// conversation (dialogue spectate). Empty until the plugin resolves a line; base-game
    /// TLK-only lines read as empty, which the caller treats as "nothing to spectate".
    /// </summary>
    public bool TryReadLocalDialogue(out string speaker, out string line)
    {
        speaker = string.Empty;
        line = string.Empty;

        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalDialogueSpeaker, BlockLocalDialogueSpeakerLength, out speaker);
        ok &= _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalDialogueLine, BlockLocalDialogueLineLength, out line);

        speaker ??= string.Empty;
        line ??= string.Empty;
        return ok;
    }

    /// <summary>
    /// Reads every hostile creature in the module. "Hostile" means a different faction
    /// from the player, which is the same test the engine's own targeting uses, and it
    /// keeps party members, neutral NPCs and other players' phantoms out of the list.
    /// </summary>
    /// <summary>Addresses from the latest <see cref="ReadEnemies"/>, so applying a peer's
    /// floors does not need a second full memory scan in the same tick.</summary>
    private readonly Dictionary<uint, nint> _enemyAddresses = [];

    public List<EnemyState> ReadEnemies()
    {
        List<EnemyState> enemies = [];
        _enemyAddresses.Clear();

        if (_player is null || !TryReadFaction(_player.Address, out int playerFaction))
        {
            return enemies;
        }

        // Phantoms are no longer plot-flagged (7.4's redesign: they take visible damage
        // now), so they must be excluded by id or a teammate's body would be published
        // as an enemy, pinned, and mirrored.
        HashSet<uint>? phantomIds = ReadPhantomObjectIds();

        foreach (CreatureRef creature in EnumerateCreatures())
        {
            if (creature.IsPlayer || creature.IsPartyMember || enemies.Count >= BlockEnemyCapacity)
            {
                continue;
            }

            if (phantomIds is not null && phantomIds.Contains(creature.ObjectId))
            {
                continue;
            }

            // A plot creature is something the module declares un-killable; its health
            // is not ours to replicate.
            if (_memory.TryReadInt32(creature.Address + OffPlot, out int plot) && plot != 0)
            {
                continue;
            }

            if (!TryReadFaction(creature.Address, out int faction) || faction == playerFaction)
            {
                continue;
            }

            // Signed: an overkilled enemy sits at negative hit points, and an unsigned
            // read would report it as 65531 hp and alive — the death would never
            // replicate.
            _memory.TryRead(creature.Address + OffCurrentHitPoints, out short current);
            _memory.TryRead(creature.Address + OffMaxHitPoints, out ushort max);

            _enemyAddresses[creature.ObjectId] = creature.Address;
            enemies.Add(new EnemyState(
                creature.ObjectId, (ushort)Math.Max((int)current, 0), max, current <= 0));
        }

        return enemies;
    }

    /// <summary>The plugin's live phantom object ids, or null when no plugin is attached.</summary>
    private HashSet<uint>? ReadPhantomObjectIds()
    {
        if (_pluginBlock == 0)
        {
            return null;
        }

        HashSet<uint>? ids = null;
        for (int i = 0; i < BlockSlotCapacity; i++)
        {
            if (_memory.TryReadUInt32(_pluginBlock + BlockPhantomIds + (i * 4), out uint id) && id != 0)
            {
                (ids ??= []).Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Lowers this instance's enemies to a peer's reported health where the peer's is
    /// lower — the min-merge that makes a client's damage count. A raw health write is
    /// the engine's own idiom here (there is no dirty flag for health; the update path
    /// diffs it every tick), and death stays this side's call: reports never go below 1
    /// because client copies are pinned, so the killing blow is always local.
    /// </summary>
    public int TryApplyEnemyFloors(IReadOnlyList<EnemyState> reports)
    {
        int applied = 0;

        foreach (EnemyState report in reports)
        {
            if (report.Dead || !_enemyAddresses.TryGetValue(report.ObjectId, out nint address))
            {
                continue;
            }

            int floor = Math.Max(1, (int)report.CurrentHealth);

            if (_memory.TryRead(address + OffCurrentHitPoints, out short current)
                && floor < current
                && _memory.TryWrite(address + OffCurrentHitPoints, floor))
            {
                applied++;
            }
        }

        return applied;
    }

    /// <summary>
    /// Publishes the owner's enemy list into the plugin, which mirrors the health and
    /// executes the deaths on the game's own thread.
    /// </summary>
    public bool TryWriteEnemies(IReadOnlyList<EnemyState> enemies)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = true;
        int count = Math.Min(enemies.Count, BlockEnemyCapacity);

        for (int i = 0; i < count; i++)
        {
            EnemyState enemy = enemies[i];
            nint slot = _pluginBlock + BlockEnemySlots + (i * BlockEnemyStride);

            ok &= _memory.TryWrite(slot + 0x04, enemy.CurrentHealth);
            ok &= _memory.TryWrite(slot + 0x06, enemy.MaxHealth);
            ok &= _memory.TryWrite(slot + 0x08, enemy.Dead ? EnemyFlagDead : 0u);

            // Object id last, so the plugin never reads a slot whose id and payload
            // belong to different enemies.
            ok &= _memory.TryWrite(slot + 0x00, enemy.ObjectId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockEnemyCount, (uint)count);
        return ok;
    }

    // ---- party companions ------------------------------------------------------
    //
    // The host owns the companions: its own engine AI drives them normally, and it just
    // publishes where they are. Each client freezes its copies (solo mode) and mirrors the
    // host's positions, so Trask/Carth are one synchronised set instead of two drifting
    // ones. Companions are keyed by NPC ROSTER SLOT (0..8), which is identical across two
    // instances of the same save; the runtime object id is not a reliable key here.

    /// <summary>CSWPartyTable + this: companion count (max 2), then int[2] active NPC slot
    /// indices at +0x04, int[9] spawned object id per slot at +0x0C, solo-mode flag at
    /// +0x190. Verified by disassembly of SetSoloMode/GetNPCObject.</summary>
    private const int PartyCount = 0x00;
    private const int PartyActiveSlots = 0x04;
    private const int PartyObjectIds = 0x0C;
    private const int PartySoloMode = 0x190;
    private const int PartyMaxCompanions = 2;
    private const int PartyNpcSlots = 9;

    private bool _partyFrozen;

    public bool SupportsPartySync => _player is not null;

    private bool TryGetPartyTable(out nint partyTable)
    {
        partyTable = 0;
        if (!EngineGlobals.TryGetServerInternal(_memory, out nint serverInternal))
        {
            return false;
        }

        partyTable = serverInternal + PartyTableOffset;
        return true;
    }

    // ---- shared progression (gold + experience) ---------------------------------
    //
    // See docs/research/progression-sync.md. Gold is a single party-table field (VERIFIED);
    // experience hangs off the PC's stats block (the stats pointer is NEEDS-LIVE, but a wrong
    // deref only makes WriteProcessMemory fail — a cross-process write to a bad address never
    // corrupts the game).

    /// <summary>partyTable + this = party gold (u32), verified from GetGold/SetGold.</summary>
    private const int PartyGold = 0x0FC;

    /// <summary>creature + this = CSWSCreatureStats* (NEEDS-LIVE).</summary>
    private const int CreatureStatsPointer = 0x0A74;

    /// <summary>stats + this = experience (u32).</summary>
    private const int StatsExperience = 0x068;

    /// <summary>The engine's own gold ceiling (SetGold clamps here).</summary>
    private const uint GoldMax = 0x3B9AC9FF;

    public bool SupportsProgressionSync => _player is not null;

    /// <summary>Reads party gold and the PC's experience. Gold is required; experience is
    /// best-effort (0 if the stats block cannot be resolved).</summary>
    public bool TryReadProgression(out uint gold, out uint experience)
    {
        gold = 0;
        experience = 0;

        if (_player is null
            || !TryGetPartyTable(out nint partyTable)
            || !_memory.TryReadUInt32(partyTable + PartyGold, out gold))
        {
            return false;
        }

        TryReadPlayerExperience(out experience);
        return true;
    }

    /// <summary>Overwrites party gold and the PC's experience with the host's absolute values.
    /// Gold is authoritative; experience is best-effort (a bad stats pointer just fails the
    /// write, never corrupts).</summary>
    public bool TryWriteProgression(uint gold, uint experience)
    {
        if (_player is null || !TryGetPartyTable(out nint partyTable))
        {
            return false;
        }

        bool ok = _memory.TryWrite(partyTable + PartyGold, Math.Min(gold, GoldMax));

        if (TryGetPlayerStats(out nint stats))
        {
            _memory.TryWrite(stats + StatsExperience, experience);
        }

        return ok;
    }

    private bool TryReadPlayerExperience(out uint experience)
    {
        experience = 0;
        return TryGetPlayerStats(out nint stats) && _memory.TryReadUInt32(stats + StatsExperience, out experience);
    }

    private bool TryGetPlayerStats(out nint stats)
    {
        stats = 0;
        return _player is not null
            && _memory.TryReadPointer(_player.Address + CreatureStatsPointer, out stats)
            && stats != 0;
    }

    /// <summary>
    /// Reads the active companions and where the host's engine has them. Skips the PC (never
    /// a companion) implicitly, because only the active NPC slots are read.
    /// </summary>
    public List<PartyMemberState> ReadPartyMembers()
    {
        List<PartyMemberState> members = [];

        if (!TryGetPartyTable(out nint partyTable)
            || !_memory.TryReadInt32(partyTable + PartyCount, out int count)
            || count is <= 0 or > PartyMaxCompanions)
        {
            return members;
        }

        // Build an id -> address map once, rather than scanning per companion.
        Dictionary<uint, nint> byId = [];
        foreach (CreatureRef c in EnumerateCreatures())
        {
            byId[c.ObjectId] = c.Address;
        }

        for (int i = 0; i < count; i++)
        {
            if (!_memory.TryReadInt32(partyTable + PartyActiveSlots + (i * 4), out int slot)
                || slot is < 0 or >= PartyNpcSlots)
            {
                continue;
            }

            if (!_memory.TryReadUInt32(partyTable + PartyObjectIds + (slot * 4), out uint objectId)
                || objectId == 0x7F000000
                || !byId.TryGetValue(objectId, out nint creature))
            {
                continue;
            }

            if (!TryReadVector(creature + OffPosition, out Vector3f position))
            {
                continue;
            }

            float facing = 0f;
            if (TryReadVector(creature + OffOrientation, out Vector3f orientation))
            {
                facing = Angles.Normalize(MathF.Atan2(orientation.Y, orientation.X));
            }

            _memory.TryReadUInt32(creature + OffAnimation, out uint animation);
            int health = _memory.TryRead(creature + OffCurrentHitPoints, out short hp) ? hp : 0;

            members.Add(new PartyMemberState((byte)slot, position, facing, animation, health));
        }

        return members;
    }

    /// <summary>
    /// Freezes the local party AI (solo mode) and mirrors the host's companions onto the
    /// matching local creatures by slot. With the AI frozen nothing else drives the
    /// companion, so an external position write sticks (see docs/re trap 3).
    /// </summary>
    public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members)
    {
        if (!TryGetPartyTable(out nint partyTable))
        {
            return 0;
        }

        // Hold solo mode every pass — the engine clears it when the last companion leaves,
        // and the party AI script checks it to decide whether to issue follow orders.
        _memory.TryWrite<byte>(partyTable + PartySoloMode, 1);
        _partyFrozen = true;

        if (members.Count == 0)
        {
            return 0;
        }

        Dictionary<uint, nint> byId = [];
        foreach (CreatureRef c in EnumerateCreatures())
        {
            byId[c.ObjectId] = c.Address;
        }

        int placed = 0;
        foreach (PartyMemberState m in members)
        {
            if (m.SlotIndex >= PartyNpcSlots
                || !_memory.TryReadUInt32(partyTable + PartyObjectIds + (m.SlotIndex * 4), out uint objectId)
                || objectId == 0x7F000000
                || !byId.TryGetValue(objectId, out nint creature))
            {
                continue;
            }

            // Mirror position, facing (as an orientation vector), animation and health.
            _memory.TryWrite(creature + OffPosition, m.Position.X);
            _memory.TryWrite(creature + OffPosition + 4, m.Position.Y);
            _memory.TryWrite(creature + OffPosition + 8, m.Position.Z);

            _memory.TryWrite(creature + OffOrientation, MathF.Cos(m.Facing));
            _memory.TryWrite(creature + OffOrientation + 4, MathF.Sin(m.Facing));

            _memory.TryWrite(creature + OffAnimation, m.Animation);
            if (m.Health > 0)
            {
                _memory.TryWrite<short>(creature + OffCurrentHitPoints, (short)Math.Min(m.Health, short.MaxValue));
            }

            placed++;
        }

        return placed;
    }

    public void ReleasePartyFreeze()
    {
        if (_partyFrozen && TryGetPartyTable(out nint partyTable))
        {
            _memory.TryWrite<byte>(partyTable + PartySoloMode, 0);
            _partyFrozen = false;
        }
    }

    // ---- client control lock ----------------------------------------------------
    //
    // The character the local player's input drives is a single object-id field at
    // CClientExoAppInternal + 0x20. VERIFIED live by toggling control (Tab) and watching it flip:
    //   0xFFFFFFFF                 = the PC / party leader is controlled
    //   0x80000000 | <objectId>    = a party NPC (companion) is controlled
    // The engine re-derives the cached controlled-creature pointer (+0x3C0) from this id each
    // frame, so writing 0xFFFFFFFF back reverts companion control to the PC within a tick (proven:
    // the write stuck and +0x3C0 returned to the PC). It is an id write resolved through a
    // null-safe map, so it can never crash. See docs/research/client-control-lock.md.
    //
    // Solo mode already stops companions FOLLOWING but not being SELECTED, so without this a
    // client player could Tab/click to a shared, host-owned companion and fight the host's
    // authority. We only revert an explicit party-NPC selection (high bit set, not all-ones), so
    // a PC-controlled state (0xFFFFFFFF) or any non-party value is left untouched.

    /// <summary>CClientExoAppInternal + this = the controlled-character id (PC = 0xFFFFFFFF).</summary>
    private const int ControlledCharacterId = 0x20;

    private const uint ControlPc = 0xFFFFFFFF;
    private const uint ControlPartyNpcFlag = 0x80000000;

    /// <summary>
    /// Pins the player's input control to their own PC, reverting any switch to a shared,
    /// host-owned companion. Object-id write only (crash-proof); a no-op while the PC (or any
    /// non-companion) is controlled. Call on clients each party pass.
    /// </summary>
    public bool LockControlToPlayer()
    {
        if (!EngineGlobals.TryGetClientInternal(_memory, out nint clientInternal)
            || !_memory.TryReadUInt32(clientInternal + ControlledCharacterId, out uint controlled))
        {
            return false;
        }

        // Revert ONLY an explicit party-NPC selection (0x8000xxxx). PC control is 0xFFFFFFFF
        // (all-ones) which we leave alone, as is any value without the party-NPC flag.
        if (controlled != ControlPc && (controlled & ControlPartyNpcFlag) != 0)
        {
            return _memory.TryWrite(clientInternal + ControlledCharacterId, ControlPc);
        }

        return true;
    }

    private bool TryReadFaction(nint creature, out int faction)
    {
        faction = -1;

        return _memory.TryReadPointer(creature + OffCreatureStats, out nint stats)
            && stats != 0
            && _memory.TryReadInt32(stats + StatsFaction, out faction);
    }

    // ---- helpers --------------------------------------------------------------

    private bool TryReadVector(nint address, out Vector3f value)
    {
        value = Vector3f.Zero;
        Span<byte> buffer = stackalloc byte[12];

        if (!_memory.TryRead(address, buffer))
        {
            return false;
        }

        value = new Vector3f(
            MemoryMarshal.Read<float>(buffer[..4]),
            MemoryMarshal.Read<float>(buffer.Slice(4, 4)),
            MemoryMarshal.Read<float>(buffer.Slice(8, 4)));
        return true;
    }

    /// <summary>CSWSItem vtable; every 4-byte occurrence in private memory is an item.</summary>
    private const uint ItemVtable = 0x00748DA0;

    /// <summary>objectId -&gt; lowercased tag, rebuilt when an id is not found.</summary>
    private readonly Dictionary<uint, string> _itemTags = [];

    /// <summary>
    /// Reads the tag of the item equipped in an inventory slot, lowercased so it can be
    /// used as a blueprint resref on the far side. Empty when the slot is empty or the
    /// item cannot be resolved.
    ///
    /// ⚠ A CSWInventory slot holds an OBJECT ID, not a pointer. Measured live: the PC's
    /// body and right-weapon slots read 0xC9 and 0xCA. Dereferencing them as pointers
    /// silently produced empty strings for every slot, which looked exactly like "the
    /// player has nothing equipped" — the first live run of equipment sync published
    /// three empty resrefs and nothing was ever equipped on a phantom.
    ///
    /// Ids are resolved through a tag cache built by scanning for the CSWSItem vtable,
    /// because the external agent has no GetGameObject. Both item layouts are accepted
    /// (id at +0x14 = CSWSItem base, at +0x04 = the CSWSObject inside it).
    /// </summary>
    private string ReadEquippedResref(nint creature, int slotOffset)
    {
        if (!_memory.TryReadPointer(creature + OffInventory, out nint inventory) || inventory == 0
            || !_memory.TryReadUInt32(inventory + slotOffset, out uint itemId)
            || itemId is 0 or >= 0x7F000000)
        {
            return string.Empty;
        }

        if (_itemTags.TryGetValue(itemId, out string? cached))
        {
            return cached;
        }

        RebuildItemTagCache();
        return _itemTags.TryGetValue(itemId, out string? found) ? found : string.Empty;
    }

    /// <summary>
    /// Trims a tag down to a usable blueprint resref: lowercase, and cut at the first
    /// character a resref cannot contain. A CExoString's length can include the
    /// terminating null (and occasionally trailing junk), which produced resrefs like
    /// "g_a_clothes01\0" — the engine's CreateItemOnObject then found no blueprint and
    /// the equip was refused with failure 7 while the name LOOKED right in the log.
    /// </summary>
    private static string SanitiseResref(string tag)
    {
        int end = 0;
        while (end < tag.Length && end < 16)
        {
            char c = char.ToLowerInvariant(tag[end]);
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                break;
            }

            end++;
        }

        return tag[..end].ToLowerInvariant();
    }

    private void RebuildItemTagCache()
    {
        _itemTags.Clear();

        BytePattern pattern = BytePattern.FromBytes(BitConverter.GetBytes(ItemVtable));
        PatternScanner scanner = new(_memory);

        foreach (nint address in scanner.ScanAll(pattern, ScanScope.PrivateCommitted, limit: 512))
        {
            uint id;
            nint tagAddress;

            if (_memory.TryReadUInt32(address + 0x14, out id) && id is > 0 and < 0x7F000000)
            {
                tagAddress = address + 0x28;
            }
            else if (_memory.TryReadUInt32(address + 0x04, out id) && id is > 0 and < 0x7F000000)
            {
                tagAddress = address + 0x18;
            }
            else
            {
                continue;
            }

            string tag = SanitiseResref(ReadExoString(tagAddress));
            if (tag.Length > 0)
            {
                _itemTags[id] = tag;
            }
        }
    }

    /// <summary>Reads a CExoString: a char* and a length, not a null-terminated buffer.</summary>
    private string ReadExoString(nint address)
    {
        if (!_memory.TryReadPointer(address, out nint text) || text == 0)
        {
            return string.Empty;
        }

        if (!_memory.TryReadUInt32(address + 4, out uint length) || length == 0 || length > 64)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[length];
        if (!_memory.TryRead(text, buffer))
        {
            return string.Empty;
        }

        return System.Text.Encoding.ASCII.GetString(buffer);
    }

    private void OnFailure()
    {
        _consecutiveFailures++;

        // A module transition rebuilds every object, so re-scan rather than read garbage.
        if (_consecutiveFailures >= 10)
        {
            _player = null;
            _phantoms = [];
            _consecutiveFailures = 0;
        }
    }

    public void Dispose()
    {
        if (_ownsMemory)
        {
            _memory.Dispose();
        }
    }
}
