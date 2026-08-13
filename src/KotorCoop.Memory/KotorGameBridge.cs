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

    /// <summary>
    /// The STORED max hit points, u16 — and it is <b>zero on every player character</b>,
    /// which is why the readout said "12 (maximum unknown)" for days.
    ///
    /// <c>CSWSObject::GetMaxHitPoints</c> (0x004D01A0) is a one-line <c>mov ax,[ecx+0xE0]</c>,
    /// so this field is right for doors, placeables and ordinary creatures. But
    /// <c>CSWSCreature::GetMaxHitPoints</c> (0x004ED310) overrides it and branches on
    /// <c>stats+0x6C</c> (<c>is_pc</c>): for a PC the engine never stores a total at all, it
    /// re-adds one up from the per-level records every time it is asked. Reading the field
    /// on a PC therefore reads a slot the engine simply never writes.
    /// See <see cref="TryReadMaxHitPoints"/>, which reproduces the branch.
    /// </summary>
    private const int OffMaxHitPoints = 0xE0;

    // CSWSCreatureStats — the pieces CSWSCreature::GetMaxHitPoints sums for a PC.
    // (StatsIsPc, the flag that selects the accumulate path, is declared with the other
    // stats offsets below.)
    private const int StatsLevelListData = 0x28;    // CExoArrayList<CSWLevelStats*>: T* data
    private const int StatsLevelListCount = 0x2C;   // ...then the count, then the capacity
    private const int StatsConModifier = 0xEE;      // byte, added once PER LEVEL (0x004ED372)
    private const int LevelStatsHitPoints = 0x2B;   // byte, the hit points rolled at that level

    /// <summary>A sanity bound on the level list, so a torn read cannot make this walk far.</summary>
    private const int MaxCharacterLevels = 40;

    /// <summary>
    /// Max hit points, following the same branch the engine follows.
    ///
    /// For a PC: sum <c>(level.hitPoints + conModifier)</c> over the character's levels,
    /// which is the loop at 0x004ED360..0x004ED3CA. Verified live against both instances —
    /// a level-1 PC with a rolled 10 and a +2 constitution modifier came out at 12, matching
    /// a full-health reading exactly.
    ///
    /// For anything else: the stored field, which is what the engine returns.
    ///
    /// Returns 0 when nothing can be read, which every caller already treats as "unknown".
    /// </summary>
    private int TryReadMaxHitPoints(nint creature)
    {
        bool isPc = _memory.TryRead(creature + OffCreatureStats, out uint stats)
            && stats != 0
            && _memory.TryReadInt32((nint)stats + StatsIsPc, out int pcFlag)
            && pcFlag == 1;

        if (!isPc)
        {
            return _memory.TryRead(creature + OffMaxHitPoints, out ushort stored) ? stored : 0;
        }

        if (!_memory.TryRead((nint)stats + StatsLevelListData, out uint levels)
            || !_memory.TryReadInt32((nint)stats + StatsLevelListCount, out int levelCount)
            || levels == 0
            || levelCount <= 0
            || levelCount > MaxCharacterLevels)
        {
            return 0;
        }

        int conModifier = _memory.TryRead((nint)stats + StatsConModifier, out byte con) ? con : 0;
        int total = 0;

        for (int i = 0; i < levelCount; i++)
        {
            if (!_memory.TryRead((nint)levels + (i * 4), out uint level)
                || level == 0
                || !_memory.TryRead((nint)level + LevelStatsHitPoints, out byte rolled))
            {
                return 0;
            }

            total += rolled + conModifier;
        }

        return total;
    }

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
    /// <summary>⚠ Slot 0 ONLY. The dword after it is BlockKeepAliveUnfocused — see
    /// SharedBlock.OffsetPhantomIds. The full table is BlockPhantomIdTable.</summary>
    private const int BlockPhantomIds = 0x264;

    /// <summary>The real per-slot phantom object ids, moved out of 0x264's one-dword hole.</summary>
    private const int BlockPhantomIdTable = 0x1F80;
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

    /// <summary>Un-killable by module rule (a cutscene actor): presence-only, the combat
    /// half must not touch it.</summary>
    private const uint EnemyFlagPlot = 2;

    // Enemy presence (block version 20): the owner's position/facing/animation per enemy,
    // which the plugin drives the local copies to match. Slot i pairs with enemy slot i.
    // id u32 @0 (written last), x f32 @4, y @8, z @0xC, facing f32 @0x10, animation u32
    // @0x14, flags u32 @0x18 (bit 0 dead), one-shot serial u32 @0x1C, one-shot anim u32 @0x20.
    private const int BlockEnemyPresenceCount = 0x1400;
    private const int BlockEnemyPresenceSlots = 0x1410;
    private const int BlockEnemyPresenceStride = 0x28;

    // Engine-resolved player (block version 38). Plugin -> agent: the engine's own
    // GetPlayerCreature(), republished every frame; 0 while no world is up.
    private const int BlockEnginePlayerPointer = 0x2260;
    private const int BlockEnginePlayerId = 0x2264;

    // Combat participation (block version 21). Plugin -> agent: what each peer's phantom
    // has taken here, as a running total. Agent -> plugin: what the local player owes for
    // hits taken on their phantom elsewhere.
    // 0x1DB0..0x1DD8 was the kill-request channel (block version 22), retired with the
    // health min-merge it served: the owner now applies a peer's damage as real damage, so
    // a kill falls out of the arithmetic instead of arriving as a message to be trusted.

    // Damage ledger (block version 23). Peer -> agent at 0x1E60 (32 x {id, cumulative}),
    // agent -> peer's owner at 0x2018 (same shape), counters in between. A cumulative sum
    // of damage DEALT, which is what replaced reporting enemy HEALTH: health had to be
    // sampled and the owner's mirror wrote its own value back between samples, so most of
    // a lone player's fight was erased before anyone could read it.
    private const int BlockDamageDealtSlots = 0x1E60;
    private const int BlockDamageOwedSlots = 0x2018;
    private const int BlockDamageLedgerStride = 0x08;
    private const int BlockDamageLedgerCapacity = 32;
    private const int BlockDamageDealtCount = 0x2000;
    private const int BlockDamageDealtDropped = 0x2004;
    private const int BlockDamageDealtGeneration = 0x2008;
    private const int BlockDamageOwedEpoch = 0x200C;
    private const int BlockDamageOwedCount = 0x2010;
    private const int BlockDamageDealtEnabled = 0x2118;

    // NPC barks (block v27): a small ring each way, speaker + three verbatim engine
    // arguments + a serial written last per entry.
    // Bark rings (block version 31). ⚠ Mirror of SharedBlock — a slot now carries the bark's
    // TEXT and sound resref, because the engine's own arguments include two pointers that are
    // meaningless in another process (docs/research/bark-replay.md).
    private const int BlockLocalBarkSlots = 0x2300;
    private const int BlockRemoteBarkSlots = 0x2B00;
    private const int BlockBarkStride = 0x100;
    private const int BlockBarkCapacity = 8;
    private const int BlockBarkTextMax = 200;
    private const int BlockBarkResRefBytes = 16;
    private const int BlockBarkSpeaker = 0x00;
    private const int BlockBarkFlags = 0x04;
    private const int BlockBarkTextLength = 0x08;
    private const int BlockBarkResRef = 0x10;
    private const int BlockBarkText = 0x20;
    private const int BlockBarkSerial = 0xFC;

    private const int BlockPhantomHitSlots = 0x1CD0;
    private const int BlockPhantomHitStride = 0x10;
    private const int BlockIncomingDamage = 0x1D50;
    private const int BlockIncomingDamageAttacker = 0x1D54;

    /// <summary>Damage the local player's phantom has taken in each peer slot's game,
    /// paired with the phantom object id it was recorded against.</summary>
    public bool TryReadPhantomHit(int slot, out uint phantomId, out uint cumulative, out uint attacker)
    {
        phantomId = 0;
        cumulative = 0;
        attacker = 0;

        if (_pluginBlock == 0 || slot is < 0 or >= BlockSlotCapacity)
        {
            return false;
        }

        nint entry = _pluginBlock + BlockPhantomHitSlots + (slot * BlockPhantomHitStride);
        return _memory.TryReadUInt32(entry + 0x00, out phantomId)
            && _memory.TryReadUInt32(entry + 0x04, out cumulative)
            && _memory.TryReadUInt32(entry + 0x08, out attacker);
    }

    /// <summary>Hands the plugin the running total this player owes, for it to apply as a
    /// real damage effect on the game's thread.</summary>
    /// <summary>
    /// Reads the barks the plugin captured. Returns every slot; the caller decides what is
    /// new by serial, which is written last per entry so a half-written slot reads as its
    /// previous (already-published) self rather than as a mixture.
    /// </summary>
    public List<BarkRecord> ReadBarks()
    {
        List<BarkRecord> barks = [];
        if (_pluginBlock == 0)
        {
            return barks;
        }

        for (int i = 0; i < BlockBarkCapacity; i++)
        {
            nint slot = _pluginBlock + BlockLocalBarkSlots + (i * BlockBarkStride);

            if (!_memory.TryReadUInt32(slot + BlockBarkSerial, out uint serial) || serial == 0)
            {
                continue;
            }

            _memory.TryReadUInt32(slot + BlockBarkSpeaker, out uint speaker);
            _memory.TryReadUInt32(slot + BlockBarkFlags, out uint flags);
            _memory.TryReadUInt32(slot + BlockBarkTextLength, out uint textLength);

            if (speaker == 0)
            {
                continue;
            }

            int length = (int)Math.Min(textLength, BlockBarkTextMax);
            byte[] text = new byte[length];
            if (length > 0 && !_memory.TryRead(slot + BlockBarkText, text.AsSpan()))
            {
                continue;
            }

            byte[] resRef = new byte[BlockBarkResRefBytes];
            if (!_memory.TryRead(slot + BlockBarkResRef, resRef.AsSpan()))
            {
                continue;
            }

            barks.Add(new BarkRecord(speaker, text, resRef, flags, serial));
        }

        return barks;
    }

    /// <summary>Hands a peer's bark to the plugin to replay. Round-robins the ring so a burst
    /// of barks does not overwrite one before the plugin's pass has seen it.</summary>
    public bool TryWriteBark(
        uint speaker, ReadOnlySpan<byte> text, ReadOnlySpan<byte> soundResRef, uint flags, uint serial)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        nint slot = _pluginBlock + BlockRemoteBarkSlots + (_barkWriteSlot * BlockBarkStride);
        _barkWriteSlot = (_barkWriteSlot + 1) % BlockBarkCapacity;

        int length = Math.Min(text.Length, BlockBarkTextMax);

        byte[] resRef = new byte[BlockBarkResRefBytes];
        soundResRef[..Math.Min(soundResRef.Length, BlockBarkResRefBytes)].CopyTo(resRef);

        bool ok = _memory.TryWrite(slot + BlockBarkSpeaker, speaker);
        ok &= _memory.TryWrite(slot + BlockBarkFlags, flags);
        ok &= _memory.TryWrite(slot + BlockBarkTextLength, (uint)length);
        ok &= _memory.TryWrite(slot + BlockBarkResRef, (ReadOnlySpan<byte>)resRef);

        if (length > 0)
        {
            ok &= _memory.TryWrite(slot + BlockBarkText, text[..length]);
        }

        // Serial last, so the plugin never replays a half-written entry.
        ok &= _memory.TryWrite(slot + BlockBarkSerial, serial);
        return ok;
    }

    private int _barkWriteSlot;

    public bool TryWriteIncomingDamage(uint cumulative, uint attacker)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockIncomingDamageAttacker, attacker);
        return _memory.TryWrite(_pluginBlock + BlockIncomingDamage, cumulative) && ok;
    }

    // Enemy one-shot capture (plugin -> agent, block version 20): fire-and-forget
    // animations per creature from the plugin's hook. id u32 @0, animation u32 @4,
    // serial u32 @8. The owner matches ids against its census when publishing.
    private const int BlockEnemyOneShotTable = 0x1920;
    private const int BlockEnemyOneShotStride = 0x10;
    private const int BlockEnemyOneShotCapacity = 48;

    // Module travel, mirroring SharedBlock version 6.
    private const int BlockTravelSerial = 0x920;
    private const int BlockTravelModule = 0x924;
    private const int BlockTravelWaypoint = 0x944;
    private const int BlockTravelNameLength = 32;

    // Shared pause + journal apply, mirroring SharedBlock version 7.
    private const int BlockRemotePauseDesired = 0x96C;
    private const int BlockRemotePauseApplied = 0x970;

    /// <summary>Agent -&gt; plugin: a remote peer in this module is in combat (version 29).</summary>
    private const int BlockSessionInCombat = 0x1E50;

    /// <summary>Plugin -&gt; agent: bumped when this player released a mirrored pause (v30).</summary>
    private const int BlockLocalPauseReleaseSerial = 0x1E54;

    /// <summary>Agent -&gt; plugin: bumped when a peer released the shared pause (v30).</summary>
    private const int BlockRemotePauseReleaseSerial = 0x1E58;
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

    // Dialogue spectate, mirroring SharedBlock version 12; version 17 moved the line to the
    // 0x1100 region so a full-length dialogue sentence survives (was 64 bytes at 0xCA8).
    private const int BlockLocalDialogueSpeaker = 0xC80;
    private const int BlockLocalDialogueSpeakerLength = 40;
    private const int BlockLocalDialogueLine = 0x1100;
    private const int BlockLocalDialogueLineLength = 192;

    // Dialogue follow, mirroring SharedBlock version 18 (region 0x1200..0x1300). The peer
    // runs the owner's conversation in the game's own UI, so what crosses is who they are
    // talking to and which reply they picked — not text to print.
    private const int BlockLocalDialoguePartnerTag = 0x1200;
    private const int BlockDialoguePartnerTagLength = 32;
    private const int BlockLocalDialogueReplySerial = 0x1220;
    private const int BlockLocalDialogueReplyIndex = 0x1224;
    private const int BlockRemoteDialogueActive = 0x1228;
    private const int BlockRemoteDialogueReplySerial = 0x122C;
    private const int BlockRemoteDialogueReplyIndex = 0x1230;
    private const int BlockRemoteDialoguePartnerTag = 0x1240;

    // The pick log (version 19): 8 entries x (serial, slot, lineHash), carried as an opaque
    // 96-byte blob — the agent relays it, only the two plugins interpret it.
    private const int BlockLocalDialoguePicks = 0x1260;
    private const int BlockRemoteDialoguePicks = 0x12C0;
    public const int DialoguePickLogBytes = 96;

    // Dialogue node sync, mirroring SharedBlock version 32 (region 0x1320..0x1330). The
    // conversation's POSITION — the index of the entry on screen — rather than the reply slot
    // the owner pressed, which is per-instance and cannot converge. -1 means "unknown", and
    // is passed through as-is so the far side declines to drive rather than guessing at 0.
    // Per-object interaction locks, mirroring SharedBlock version 33 (0x1330..0x13E0).
    private const int BlockLocalInteractionTag = 0x1330;
    private const int BlockInteractionTagLength = 32;
    private const int BlockLocalInteractionKind = 0x1350;
    private const int BlockLocalInteractionActive = 0x1354;
    private const int BlockInteractionLockCount = 0x1358;
    private const int BlockInteractionLocks = 0x1360;
    private const int BlockInteractionLockStride = 0x28;
    private const int BlockInteractionLockCapacity = 4;

    private const int BlockLocalDialogueNode = 0x1320;
    private const int BlockLocalDialogueNodeSerial = 0x1324;
    private const int BlockRemoteDialogueNode = 0x1328;
    private const int BlockRemoteDialogueNodeSerial = 0x132C;

    // Companion roster reconciliation, mirroring SharedBlock version 34 (0x3300..0x3320).
    //
    // Two PACKED dwords, one per direction, and the packing is the concurrency design: the
    // masks are read by one process while the other writes them, and a single aligned dword
    // store on x86 cannot be torn. Splitting "available" and "in party" into two fields would
    // have needed a serial and a re-read to prove the pair belonged together (the discipline
    // the dialogue node pair uses); packing them removes the question instead of answering it.
    private const int BlockLocalPartyRoster = 0x3300;
    private const int BlockHostPartyRoster = 0x3304;

    /// <summary>Bits 0..8 = recruited, bits 16..24 = in the active party, bit 31 = "this word
    /// was actually written by somebody who knows" (see <see cref="WriteHostPartyRoster"/>).</summary>
    private const int BlockPartyRosterAvailableShift = 0;
    private const int BlockPartyRosterPartyShift = 16;
    private const uint BlockPartyRosterSlotMask = 0x1FF;
    private const uint BlockPartyRosterValid = 0x8000_0000;

    /// <summary>16-byte signature: "KOCOOP01" + version 38 + guard 0xC0FFEE01. The version is
    /// part of the pattern deliberately: an agent must not attach to a plugin whose block
    /// layout it does not know, and a stale pair fails to find each other rather than reading
    /// the wrong offsets. ⚠ Bump this WITH PluginHost's `_block->Version` — they are one
    /// number in two files, and a half-bump reads as "plugin shared block not found", which
    /// looks like a broken install rather than a version skew.</summary>
    public const string PluginSignature = "4B 4F 43 4F 4F 50 30 31 26 00 00 00 01 EE FF C0";

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

    /// <summary>
    /// Finds every live creature by scanning for the vtable pointer, reusing the last result
    /// for a short while.
    ///
    /// ⚠ The cache is not an optimisation, it is the difference between working and not. A
    /// full scan of the game's committed memory takes on the order of the interval it was
    /// being called at, so calling it fresh on every world-state pass consumed an entire CPU
    /// core and starved the pump that replicates POSITION — measured end to end, a peer took
    /// 3-7 seconds to move, and the pump ran at ~4 Hz instead of 60. Creature addresses are
    /// stable while a module is loaded (the object array is rebuilt on load, which is what
    /// <see cref="Invalidate"/> is for), so re-scanning several times a second bought nothing.
    ///
    /// Callers that need fresh FIELD values still get them: only the address list is cached,
    /// and every read through those addresses is live.
    /// </summary>
    public List<CreatureRef> EnumerateCreatures()
    {
        long nowMs = Environment.TickCount64;

        // ⚠ An EMPTY result ages out fast. "No creatures" means "no world yet" — a state
        // that flips exactly once, at load — and caching it for the full 15 s is what made
        // the join arrival randomly awful: the agent attaches at the menu, scans, caches
        // the emptiness, and then every resolve attempt while the save loads returns that
        // stale nothing until the cache happens to expire. Measured live: the same flow
        // resolved 1.7 s after world-ready on one run and 15.7 s on the next, purely on
        // cache-expiry luck — and the seamless-arrival hold timed out watching it. A short
        // TTL keeps the melt-down protection (still at most ~1 scan/s while worldless,
        // the same rate the resolve limiter allows) without ever hiding a fresh world.
        int ttlMs = _creatureCache is { Count: 0 } ? EmptyCreatureCacheMs : CreatureCacheMs;
        if (_creatureCache is not null && nowMs - _creatureCacheAtMs < ttlMs)
        {
            return _creatureCache;
        }

        List<CreatureRef> scanned = ScanCreatures();
        _creatureCache = scanned;
        _creatureCacheAtMs = nowMs;
        return scanned;
    }

    /// <summary>How long a creature-address scan is reused. Long enough that scanning stops
    /// dominating the pump, short enough that a creature spawned mid-fight is picked up
    /// quickly.</summary>
    private const int CreatureCacheMs = 15_000;

    /// <summary>How long an EMPTY creature scan is reused — see the warning above.</summary>
    private const int EmptyCreatureCacheMs = 750;

    private List<CreatureRef>? _creatureCache;
    private long _creatureCacheAtMs;

    /// <summary>Drops the cached creature list — call when the world changes underneath it.</summary>
    public void InvalidateCreatureCache()
    {
        _creatureCache = null;
        _scanCache.Clear();
    }

    /// <summary>
    /// A vtable scan whose ADDRESSES are reused for a while.
    ///
    /// Doors, placeables and containers do not move while a module is loaded, but each of
    /// their state-collection passes was running its own full scan of the game's committed
    /// memory — and they all run on the same world-state pass, so the agent was doing a
    /// dozen full scans a second. Together with the item and creature scans that consumed a
    /// whole CPU core and starved the loop that replicates POSITION, which is what made
    /// everything appear to lag by seconds.
    ///
    /// Only the address list is cached; every field read through those addresses is live, so
    /// a door opening is still seen on the next pass.
    /// </summary>
    private List<nint> CachedScan(uint vtable, int limit, int ttlMs)
    {
        long nowMs = Environment.TickCount64;

        if (_scanCache.TryGetValue(vtable, out (List<nint> Hits, long AtMs) cached)
            && nowMs - cached.AtMs < ttlMs)
        {
            return cached.Hits;
        }

        PatternScanner scanner = new(_memory);
        List<nint> hits = scanner.ScanAll(
            BytePattern.FromBytes(BitConverter.GetBytes(vtable)), ScanScope.PrivateCommitted, limit);

        _scanCache[vtable] = (hits, nowMs);
        return hits;
    }

    /// <summary>How long a world-object address scan is reused. These are static objects in a
    /// loaded module, so this can be generous; a module change clears the cache.</summary>
    private const int ObjectScanCacheMs = 30_000;

    private readonly Dictionary<uint, (List<nint> Hits, long AtMs)> _scanCache = [];

    private List<CreatureRef> ScanCreatures()
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

    /// <summary>Live engine read, never retained — see the interface remarks. The "areaXX"
    /// fallback is deliberately NOT applied here: a caller labelling a census wants "the
    /// name is unreadable" reported as failure, not disguised as a placeholder name.</summary>
    public bool TryReadCurrentModule(out string module) =>
        EngineGlobals.TryReadModuleName(_memory, out module) && module.Length > 0;

    public bool TryReadLocalState(out PlayerState state)
    {
        state = PlayerState.Empty;

        // Re-resolve when the cached creature stops looking like the player. A module
        // transition frees every creature and the new module allocates its own, so a pointer
        // cached across a travel lands in reused memory: measured live after following a host
        // through a door, both agents reported pos (0,0,0) and hit points of 57/134 and
        // 21544/21544 — plausible-looking garbage, which is the worst kind, because the
        // phantom positions derived from it look like a network fault rather than a stale
        // pointer. Two small reads per tick buy immunity to that.
        // Prefer the engine's own answer to the scan. The scan can latch a FREED COPY of
        // the player — the copy keeps its vtable, stats pointer and is_pc flag, so every
        // cheap validation passes while its position and health stay frozen at whatever
        // the copy died with. Measured on tar_m03aa: this agent published the PC standing
        // at its spawn point for minutes while the real character crossed the map, so the
        // phantom never moved and every ambush the real player stood in was declined
        // against a body somewhere else. The plugin republishes the engine's
        // GetPlayerCreature() every frame (0 while no world is up); adopt it whenever it
        // names a different creature than the one the scan chose.
        if (_pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockEnginePlayerPointer, out uint enginePlayer)
            && enginePlayer != 0
            && (_player is null || _player.Address != (nint)enginePlayer)
            && _memory.TryReadUInt32(_pluginBlock + BlockEnginePlayerId, out uint engineId)
            && StillLooksLikeThePlayer((nint)enginePlayer))
        {
            ResolveDiagnostic?.Invoke(
                $"player: adopted the engine's own creature 0x{enginePlayer:X} (id 0x{engineId:X})"
                + (_player is null ? string.Empty : $" over the scan's 0x{_player.Address:X}"));
            _player = new CreatureRef((nint)enginePlayer, engineId, string.Empty, true, false);
        }

        if (_player is not null && !StillLooksLikeThePlayer(_player.Address))
        {
            if (_playerLostLogged < 3)
            {
                _playerLostLogged++;
                ResolveDiagnostic?.Invoke(DescribePlayerMismatch(_player.Address));
            }

            _player = null;
        }

        if (_player is null)
        {
            // ⚠ RATE-LIMITED, and it has to be. Re-resolving means a full pattern scan of the
            // game's committed memory — hundreds of milliseconds. Running that on every failed
            // validation pinned a CPU core and dropped the pump from 60 Hz to under 3, which
            // measured end to end as a peer taking 3-7 SECONDS to move. The validation above
            // is cheap and correct; what was not affordable was acting on it immediately.
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastResolveAttemptMs < ResolveRetryMs)
            {
                return false;
            }

            _lastResolveAttemptMs = nowMs;

            if (!TryResolve(out _))
            {
                return false;
            }
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
        int maxHealth = TryReadMaxHitPoints(creature);
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

        // A module change rebuilds every object in the game, so every cached ADDRESS is now
        // meaningless. The caches exist because scanning for them is what starved this pump;
        // dropping them exactly here is what keeps them honest.
        if (module.Length > 0 && !string.Equals(module, _cachedForModule, StringComparison.OrdinalIgnoreCase))
        {
            _cachedForModule = module;
            InvalidateCreatureCache();
            _itemTags.Clear();
            _itemTagMisses.Clear();
            _itemTagsBuiltAtMs = 0;
        }

        // Visible equipment travels as blueprint resrefs. An item's tag matches its
        // blueprint for stock gear; custom/mod items whose tag diverges simply fail to
        // materialise on the far side, which is the correct soft failure when the two
        // installs do not share a mod.
        string body = ReadEquippedResref(creature, InvSlotBody);
        string rightWeapon = ReadEquippedResref(creature, InvSlotRightWeapon);
        string leftWeapon = ReadEquippedResref(creature, InvSlotLeftWeapon);

        RefreshOneShotAnimation();

        state = new PlayerState(
            position, facing, module, health, animation, appearance, maxHealth,
            body, rightWeapon, leftWeapon, ReadLocalFlags(creature),
            _oneShotAnimation, _oneShotSequence);

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

    /// <summary>How long a pause release is announced on the wire. Long enough that a lost
    /// datagram cannot swallow it, short enough that it cannot end a LATER pause.</summary>
    private static readonly TimeSpan PauseReleaseAnnounceWindow = TimeSpan.FromMilliseconds(750);

    private uint _lastPauseReleaseSerial;
    private DateTime _pauseReleaseAnnouncedUntil = DateTime.MinValue;

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
        // ⚠ SIXTEEN bits. The twin's current animation is a ushort (GetCurrentAnimation
        // 0x0063C060 is `mov ax,[ecx+0x48]`) and +0x4A is a SEPARATE ushort, the queued
        // animation that QueuedCallback swaps in. Reading 32 bits here returned
        // queued<<16 | played whenever anything was queued — a value that is not an
        // animation id at all, published as the peer's gait and mirrored onto the phantom.
        if (_memory.TryReadPointer(creature + OffClientTwin, out nint twin) && twin != 0
            && _memory.TryReadUInt16(twin + OffTwinAnimation, out ushort twinAnim) && twinAnim != 0)
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

        // A release of a MIRRORED pause, announced for a short window so a lost datagram
        // does not lose it. The plugin counts them; the transition to a new count starts the
        // window. See PlayerState.FlagPauseReleased.
        uint releaseSerial = LocalPauseReleaseSerial;
        if (releaseSerial != _lastPauseReleaseSerial)
        {
            _lastPauseReleaseSerial = releaseSerial;

            // Zero is "never released", not a release: a fresh plugin publishes it.
            _pauseReleaseAnnouncedUntil = releaseSerial == 0
                ? DateTime.MinValue
                : DateTime.UtcNow + PauseReleaseAnnounceWindow;
        }

        if (DateTime.UtcNow < _pauseReleaseAnnouncedUntil)
        {
            flags |= PlayerState.FlagPauseReleased;
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
    ///
    /// The remote-dialogue flag matters most here: it is level-written by the agent every
    /// tick, so once the agent is gone nothing else would ever lower it — and a spectator
    /// mid-follow when the session died would stay locked out of its own replies forever.
    /// Lowering it makes the plugin's spectate controller close the mirrored conversation
    /// and restore reply control on its next frame. The presence counts are cleared for
    /// the same staleness reason (their drives are also gated on the command, so this is
    /// belt and braces).
    /// </summary>
    public void ClearCoopState()
    {
        if (_pluginBlock != 0)
        {
            _memory.TryWrite(_pluginBlock + BlockRemoteCount, 0u);
            _memory.TryWrite(_pluginBlock + BlockEnemyCount, 0u);
            _memory.TryWrite(_pluginBlock + BlockEnemyPresenceCount, 0u);
            _memory.TryWrite(_pluginBlock + BlockPartyPresenceCount, 0u);
            _memory.TryWrite(_pluginBlock + BlockRemoteDialogueActive, 0u);

            // Same staleness reason as the presence counts: with the agent gone nothing
            // refreshes these, and a leftover request would keep offering an encounter on
            // behalf of a peer that is no longer in the session.
            _memory.TryWrite(_pluginBlock + BlockEncounterRequestCount, 0u);
            _memory.TryWrite(_pluginBlock + BlockEncounterWatch, 0u);
            _encounterWatchWritten = false;
            _memory.TryWrite(_pluginBlock + BlockCommand, (uint)0);
        }

        // A new session deals fresh peer ids, so keeping the old slot map would hold slots
        // for players who no longer exist and push the new ones down the block.
        _slots.Clear();

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

        // Resolve every peer to ITS slot before writing anything, so the loop below can be a
        // plain walk over the eight slots.
        for (int i = 0; i < BlockSlotCapacity; i++)
        {
            _slotPeerIndex[i] = -1;
        }

        _presentPeerIds.Clear();
        for (int p = 0; p < peers.Count; p++)
        {
            _presentPeerIds.Add(peers[p].PeerId);
        }

        for (int p = 0; p < peers.Count; p++)
        {
            int assigned = _slots.Assign(peers[p].PeerId, _presentPeerIds);
            if (assigned >= 0)
            {
                _slotPeerIndex[assigned] = p;
            }
        }

        // ⚠ RemoteCount is published as the occupied RANGE (highest slot + 1), not the
        // population. Slots are sticky, so with peer 3 in slot 1 and slot 0 empty a count of
        // "1" would hide them; the plugin reads this as "how far to look", not "how many".
        int occupiedRange = 0;

        for (int i = 0; i < BlockSlotCapacity; i++)
        {
            nint slot = _pluginBlock + BlockSlots + (i * BlockSlotStride);
            int index = _slotPeerIndex[i];

            if (index < 0)
            {
                ok &= _memory.TryWrite(slot + 0x00, 0u);
                continue;
            }

            occupiedRange = i + 1;

            RemotePeer peer = peers[index];
            WriteRemoteOneShot(i, peer.State);
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

        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteCount, (uint)occupiedRange);
        ok &= _memory.TryWrite(_pluginBlock + BlockCommand, CommandSpawnAndDrive);
        return ok;
    }

    /// <summary>Which peer owns which block slot, kept stable for the session. See
    /// <see cref="PeerSlotMap"/> for why that matters.</summary>
    private readonly PeerSlotMap _slots = new(BlockSlotCapacity);

    /// <summary>Scratch for one publication: slot -&gt; index into the peer list, -1 = empty.</summary>
    private readonly int[] _slotPeerIndex = new int[BlockSlotCapacity];

    /// <summary>Scratch for one publication: the peer ids present, for eviction decisions.</summary>
    private readonly List<uint> _presentPeerIds = [];

    /// <inheritdoc />
    public int RemoteSlotCapacity => BlockSlotCapacity;

    /// <inheritdoc />
    public uint PeerInSlot(int slot) => _slots.Owner(slot);

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

        foreach (nint address in CachedScan(DoorVtable, 256, ObjectScanCacheMs))
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

        foreach (nint address in CachedScan(PlaceableVtable, 512, ObjectScanCacheMs))
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

        HashSet<uint> seenContainers = [];

        foreach (nint address in CachedScan(PlaceableVtable, 512, ObjectScanCacheMs))
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

    /// <summary>
    /// CSWSEncounter + this = active dword (symbol database: active).
    ///
    /// ⚠ NEVER WRITE THIS. It is kept named so the next person recognises it rather than
    /// rediscovering it: <c>SaveEncounter</c> (0x591350) serialises this field into the
    /// GIT, so a write here follows the player into their save and disarms the encounter
    /// permanently, in single-player too. Disarm through <see cref="EncounterStarted"/>,
    /// which is runtime-only. See <see cref="DisableLocalEncounters"/>.
    /// </summary>
    private const int EncounterActive = 0x238;

    /// <summary>CSWSEncounter + this = started dword (symbol database: started).</summary>
    private const int EncounterStarted = 0x278;

    private string _encounterModule = string.Empty;
    private long _encounterDisarmedAtMs;

    /// <summary>
    /// How often the disarm is re-asserted within one module. It is a LEVEL now, not a
    /// one-shot: <c>started</c> is the engine's own "this one has fired" latch and the
    /// binary was not exhaustively swept for a path that clears it (a respawn timer is the
    /// plausible one), so re-writing it a few times a minute costs a cached scan and removes
    /// the question. Cheap because the scan is cached and the writes are plain dwords.
    /// </summary>
    private const int EncounterDisarmRefreshMs = 15_000;

    /// <summary>
    /// True for "area1"/"area1F"-style names: what the module reads as while a transition is
    /// in flight. Not a module on disk — an object id wearing a module's clothes — and every
    /// pointer the scan holds while one is showing belongs to a world being freed.
    /// </summary>
    private static bool IsTransitionalModuleName(string module)
    {
        if (module.Length <= 4 || !module.StartsWith("area", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 4; i < module.Length; i++)
        {
            if (!Uri.IsHexDigit(module[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Turns off every encounter in the local module. Called on the instance that does NOT
    /// own enemies. Encounter spawns are the one engine-side source of object-id drift — a
    /// creature spawned by an encounter in one instance and not the other shifts the id
    /// counter for everything created afterwards, and identical ids are the key the whole
    /// combat replication rests on. The host keeps its encounters, and a peer that walks into
    /// one asks the owner to fire it rather than firing its own (see
    /// <see cref="TryRequestEncounterFire"/>).
    ///
    /// ⚠ ONLY <c>started</c> IS WRITTEN, AND THAT IS DELIBERATE — it used to write
    /// <c>active = 0</c> as well, which POISONED THE PLAYER'S SAVE.
    /// <c>CSWSEncounter::SaveEncounter</c> (0x591350) serialises <c>active</c> (+0x238) into
    /// the GIT and does NOT serialise <c>started</c> (+0x278) — verified by disassembly, the
    /// function references +0x238 and +0x23C and nothing else from that pair. So a client
    /// who saved while joined wrote "inactive" into their own save for every encounter in
    /// the module, permanently, and those encounters never fired again in single-player
    /// either. <c>started</c> is runtime-only and dies with the module.
    ///
    /// One is enough: <c>CSWSEncounter::SpawnIfAppropriate</c> (0x593BE0) early-outs on
    /// <c>active == 0</c> OR <c>started == 1</c>, and sets <c>started = 1</c> itself
    /// (0x593C39) when it does spawn — so writing it is exactly claiming the encounter has
    /// already fired.
    ///
    /// ⚠ This comment used to claim that whatever the owner's encounters spawn "reaches the
    /// client through the enemy channel". It does not. Both mirror controllers begin with
    /// <c>ResolveServerObject(id)</c> and give up on 0, so a creature that exists only in the
    /// owner's instance is skipped — see docs/research/client-encounters.md §8, which is
    /// where the missing materialisation path is designed.
    ///
    /// Plain dword writes, so this runs externally.
    /// </summary>
    public int DisableLocalEncounters()
    {
        if (!EngineGlobals.TryReadModuleName(_memory, out string module) || module.Length == 0)
        {
            return 0;
        }

        // ⚠ NEVER WHILE A TRANSITION IS IN FLIGHT. An "areaNN" name is what the engine reads
        // as while the module is being torn down and rebuilt, and every encounter the scan
        // knows about belongs to the world being freed. Measured: a client crashed one frame
        // into a follow-travel with eip in the HEAP — a corrupted call target, which is what
        // writing into recycled memory produces.
        if (IsTransitionalModuleName(module))
        {
            return 0;
        }

        long nowMs = Environment.TickCount64;
        bool sameModule = string.Equals(module, _encounterModule, StringComparison.OrdinalIgnoreCase);

        if (sameModule && nowMs - _encounterDisarmedAtMs < EncounterDisarmRefreshMs)
        {
            return 0;
        }

        // ⚠ AND NEVER FROM A LIST THAT PREDATES THE WORLD. The scan cache lives for 30 s,
        // which is longer than a module lasts in a bad case and longer than the refresh
        // interval below — so a new module would otherwise be "disarmed" by writing into the
        // previous one's freed objects.
        if (!sameModule)
        {
            _scanCache.Remove(EncounterVtable);
        }

        _encounterDisarmedAtMs = nowMs;

        int disabled = 0;

        foreach (nint address in CachedScan(EncounterVtable, 128, ObjectScanCacheMs))
        {
            // ⚠ RE-VALIDATE THE VTABLE, not just the id. These addresses can be up to 30 s
            // old, and an object freed in between may have been recycled into something else
            // whose bytes at +0x04 happen to look like a plausible object id. The vtable is
            // what makes this an encounter, so it is what has to still be there before we
            // write into the thing.
            if (!_memory.TryReadUInt32(address, out uint vtable) || vtable != EncounterVtable)
            {
                continue;
            }

            // Sanity: a real encounter has its object id where CSWSObject keeps it.
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or >= 0x7F000000)
            {
                continue;
            }

            if (_memory.TryWrite(address + EncounterStarted, 1))
            {
                disabled++;
            }
        }

        // Remember the module even if there were no encounters, so the scan is not
        // repeated every pump for a module that simply has none.
        _encounterModule = module;

        // A refresh pass re-asserts the same encounters it already disarmed; reporting them
        // again would have the agent log "disabled N encounter(s)" every 15 seconds forever.
        return sameModule ? 0 : disabled;
    }

    // Client-triggered encounters, mirroring SharedBlock 0x1DE0..0x1E60. The plugin owns
    // both halves (docs/research/client-encounters.md); the bridge only carries them.
    private const int BlockEncounterWatch = 0x1DE0;
    private const int BlockEncounterInsideCount = 0x1DE4;
    private const int BlockEncounterInside = 0x1DEC;
    private const int BlockEncounterInsideCapacity = 4;
    private const int BlockEncounterRequestCount = 0x1DFC;
    private const int BlockEncounterRequestSerial = 0x1E00;
    private const int BlockEncounterRequests = 0x1E04;
    private const int BlockEncounterRequestStride = 0x08;
    private const int BlockEncounterRequestCapacity = 8;

    private uint _encounterRequestSerial;

    private bool _encounterWatchWritten;
    private bool _encounterWatchState;

    public void SetEncounterWatch(bool enabled)
    {
        // Edge-written: this is a per-pump call and the value changes at most once a session.
        if (_pluginBlock == 0 || (_encounterWatchWritten && _encounterWatchState == enabled))
        {
            return;
        }

        if (_memory.TryWrite(_pluginBlock + BlockEncounterWatch, enabled ? 1u : 0u))
        {
            _encounterWatchWritten = true;
            _encounterWatchState = enabled;
        }
    }

    public IReadOnlyList<uint> ReadEnteredEncounters()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEncounterInsideCount, out uint count)
            || count == 0)
        {
            return [];
        }

        count = Math.Min(count, BlockEncounterInsideCapacity);

        List<uint> ids = new((int)count);
        for (int i = 0; i < count; i++)
        {
            if (_memory.TryReadUInt32(_pluginBlock + BlockEncounterInside + (i * 4), out uint id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public bool TryRequestEncounterFire(IReadOnlyList<(uint EncounterId, uint PeerId)> requests)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(requests.Count, BlockEncounterRequestCapacity);

        bool ok = true;
        for (int i = 0; i < count; i++)
        {
            nint slot = _pluginBlock + BlockEncounterRequests + (i * BlockEncounterRequestStride);
            ok &= _memory.TryWrite(slot + 0x00, requests[i].EncounterId);
            ok &= _memory.TryWrite(slot + 0x04, requests[i].PeerId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockEncounterRequestCount, (uint)count);

        // Serial last and always advancing, even for an unchanged list: the plugin only
        // acts when the serial moves, and firing is idempotent (the engine refuses an
        // encounter that has already started), so re-offering costs nothing and covers a
        // request that arrived while the plugin was mid-transition.
        ok &= _memory.TryWrite(_pluginBlock + BlockEncounterRequestSerial, ++_encounterRequestSerial);
        return ok;
    }

    // ---- journal ---------------------------------------------------------------

    /// <summary>CServerExoAppInternal + this = party table (embedded CSWPartyTable).</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = a POINTER to the CSWSJournal (whose own array list
    /// { SJournalEntry* data; int size; int cap } sits at journal+0). Dereference it —
    /// see the note in ReadJournalEntries for what reading it in place cost.</summary>
    private const int PartyTableJournal = 0x11C;

    private const int JournalEntryStride = 0x3C;
    private const int JournalEntryTag = 0x18;    // CExoString { char* @0, len @4 }
    private const int JournalEntryState = 0x20;

    /// <summary>What this game's journal held when last scanned (or what we last applied),
    /// so only genuine advances are announced, nothing echoes — and, critically, nothing
    /// is applied before the live journal has been read once (see JournalShadow: judging
    /// against an unprimed shadow was the "Journal Entry Added" popup on every join).</summary>
    private readonly JournalShadow _journalShadow = new();

    /// <summary>Reading is external; applying goes through the plugin (SetState must run
    /// on the game's thread, and it is also what refreshes the journal GUI).</summary>
    public bool SupportsJournalSync => _pluginBlock != 0;

    public List<KeyValuePair<string, int>> CollectChangedJournal()
    {
        // A failed read (loading screen, chain not yet resolvable) must return empty
        // WITHOUT priming the shadow — an empty read result and "could not read" are
        // different facts, and conflating them is what replayed the whole store on join.
        if (!TryReadJournalEntries(out List<KeyValuePair<string, int>> live))
        {
            return [];
        }

        return _journalShadow.Observe(live);
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

        // Covers both halves of idempotence: never before the live journal has been read
        // (the join race), and never for a state the game already has (redundant apply —
        // the engine's SetState fires the "Journal Entry Added" GUI feedback even when
        // nothing changes, so a redundant apply is a guaranteed spurious popup).
        if (!_journalShadow.ShouldApply(questTag, state))
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
            // Optimistic: quest states only rise, so even a failed apply cannot make the
            // shadow wrong in a way that loses progress.
            _journalShadow.MarkApplied(questTag, state);
        }

        return ok;
    }

    /// <summary>
    /// Reads the live journal. Returns FALSE when the chain is not readable (loading
    /// screen, menu, mid-transition) — which is a different fact from "readable and
    /// empty" (true with an empty list, e.g. a new game), and the shadow priming above
    /// depends on the distinction.
    /// </summary>
    private bool TryReadJournalEntries(out List<KeyValuePair<string, int>> entries)
    {
        entries = [];

        if (!EngineGlobals.TryGetServerInternal(_memory, out nint serverInternal))
        {
            return false;
        }

        // ⚠ +0x11C HOLDS a pointer to the CSWSJournal; it is not the journal. Reading it as
        // the array list itself made this enumerate whatever followed it in the party
        // table — the same mistake that crashed the plugin's WRITE path on 2026-08-06.
        // CSWPartyTable::GetPartyJournal 0x005633C0 is `mov eax,[esi+0x11C]` (VERIFIED),
        // and the corrected chain was confirmed live: 1 entry, 'end_attack', state 20.
        if (!_memory.TryReadPointer(serverInternal + PartyTableOffset + PartyTableJournal, out nint journal)
            || journal == 0)
        {
            return false;
        }

        if (!_memory.TryReadPointer(journal, out nint data)
            || !_memory.TryReadInt32(journal + 4, out int count)
            || count is < 0 or > 512
            || (count > 0 && data == 0))
        {
            return false;
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

            entries.Add(new KeyValuePair<string, int>(tag, state));
        }

        return true;
    }

    // ---- shared pause ----------------------------------------------------------

    /// <summary>Pause sync needs the plugin: only an in-process SetPauseState call
    /// actually stops the world timer.</summary>
    public bool SupportsPauseSync => _pluginBlock != 0;

    /// <summary>Tells the plugin whether any remote peer currently holds a pause.</summary>
    public bool TrySetRemotePause(bool desired) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockRemotePauseDesired, desired ? 1u : 0u);

    // ---- shared combat state ----------------------------------------------------

    /// <summary>Combat replication needs the plugin: only an in-process
    /// CSWSCreature::SetCombatState call moves the state the engine actually reads.</summary>
    public bool SupportsCombatSync => _pluginBlock != 0;

    /// <summary>
    /// Tells the plugin whether a REMOTE peer standing in this player's module is fighting.
    /// Never includes the local player — see <c>SharedBlock.OffsetSessionInCombat</c>.
    /// </summary>
    public bool TrySetSessionInCombat(bool fighting) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockSessionInCombat, fighting ? 1u : 0u);

    /// <summary>
    /// The plugin's count of times this player has released a pause mirrored from a peer.
    /// A CHANGE in this value is the release; the value itself means nothing.
    /// </summary>
    public uint LocalPauseReleaseSerial =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockLocalPauseReleaseSerial, out uint serial)
            ? serial
            : 0;

    /// <summary>Tells the plugin a peer has ended the shared pause, so this instance drops
    /// the pause it started.</summary>
    public bool TrySignalRemotePauseRelease(uint serial) =>
        _pluginBlock != 0
        && _memory.TryWrite(_pluginBlock + BlockRemotePauseReleaseSerial, serial);

    // ---- in-game Connect flow --------------------------------------------------

    private uint _lastConnectSerial;

    /// <summary>Writes the main-menu Co-op button override (the plugin shows the button by
    /// itself now — a 1 here FORCES it on over a coop.ini opt-out, a 0 defers to coop.ini)
    /// and remembers the current request serial so the first poll does not fire spuriously.</summary>
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

    // ---- one-shot animations (block version 16) ---------------------------------

    private const int BlockLocalOneShotAnim = 0x1050;
    private const int BlockLocalOneShotSerial = 0x1054;
    private const int BlockRemoteOneShot = 0x1060;
    private const int BlockRemoteOneShotStride = 0x08;

    private uint _lastSeenOneShotSerial;
    private byte _oneShotSequence;
    private ushort _oneShotAnimation;

    /// <summary>
    /// The local player's most recent one-shot animation, as an EDGE. The plugin hooks the
    /// engine's fire-and-forget dispatch (these finish between polls, so they cannot be
    /// sampled) and bumps a serial; this turns that into a small sequence byte that rides the
    /// per-tick state, so a repeated identical flourish still replicates.
    /// </summary>
    private void RefreshOneShotAnimation()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockLocalOneShotSerial, out uint serial)
            || serial == _lastSeenOneShotSerial)
        {
            return;
        }

        _lastSeenOneShotSerial = serial;

        if (_memory.TryReadUInt32(_pluginBlock + BlockLocalOneShotAnim, out uint animation))
        {
            _oneShotAnimation = (ushort)animation;
            _oneShotSequence++;
        }
    }

    /// <summary>Publishes a peer's one-shot into their slot; the plugin fires it once.</summary>
    private void WriteRemoteOneShot(int slot, in PlayerState state)
    {
        if (_pluginBlock == 0 || state.OneShotAnimation == 0)
        {
            return;
        }

        nint at = _pluginBlock + BlockRemoteOneShot + (slot * BlockRemoteOneShotStride);

        // Animation before serial: the plugin reads the serial last, so it can never see a
        // bumped serial paired with a stale id.
        _memory.TryWrite(at, (uint)state.OneShotAnimation);
        _memory.TryWrite(at + 4, (uint)state.OneShotSequence);
    }

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

    /// <summary>Agent -&gt; plugin: bumped to make the GAME ask the player for a password.</summary>
    private const int BlockBrowserPasswordSerial = 0xE14;

    /// <summary>Entry +0x3A: non-zero when the host requires a password (a lock marker in the row).</summary>
    private const int BlockBrowserEntryLocked = 0x3A;

    /// <summary>Non-zero when this agent already holds a password for the next join
    /// (coop.ini, --password, or an answered prompt): the plugin prompts on a locked row
    /// only when this is clear, so configured players are never asked. Same value on every
    /// row; previously unused padding, so no signature bump.</summary>
    private const int BlockBrowserEntryPasswordHeld = 0x3B;

    // SharedBlock's browser details array (block version 36).
    private const int BlockBrowserDetails = 0x3400;
    private const int BlockBrowserDetailStride = 0x70;
    private const int BlockBrowserDetailModule = 0x00;
    private const int BlockBrowserDetailModuleLength = 40;
    private const int BlockBrowserDetailPortraits = 0x28;
    private const int BlockBrowserDetailPortraitLength = 16;
    private const int BlockBrowserDetailPortraitCount = 3;
    private const int BlockBrowserDetailLoadScreen = 0x58;
    private const int BlockBrowserDetailLoadScreenLength = 16;

    /// <summary>Module resref -> what to show a browsing player, cached because the answer
    /// costs an archive open and never changes for a given resref.</summary>
    private readonly Dictionary<string, string> _moduleNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A readable name for a module resref, resolved from THIS install's own module files.
    ///
    /// Deliberately local rather than sent by the host: both installs have the base game, the
    /// answer is identical on both, and a resref is 16 bytes on the wire where a display name
    /// is not. Falls back to the resref itself, which is what the browser showed before and is
    /// never wrong — only terse.
    /// </summary>
    private string DescribeModule(string module)
    {
        if (module.Length == 0)
        {
            return string.Empty;
        }

        if (_moduleNames.TryGetValue(module, out string? cached))
        {
            return cached;
        }

        string name = module;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_memory.ProcessId);
            string? gameDirectory = Path.GetDirectoryName(process.MainModule?.FileName);

            if (gameDirectory is not null
                && GameFiles.ModuleNames.TryRead(gameDirectory, module, out string readable)
                && readable.Length > 0)
            {
                name = readable;
            }
        }
        catch (Exception)
        {
            // A browser label is not worth failing anything for; keep the resref.
        }

        _moduleNames[module] = name;
        return name;
    }

    private uint _browserListSerial;
    private uint _lastBrowserConnectSerial;
    private uint _lastBrowserHostSerial;
    private uint _passwordPromptSerial;
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
    /// <paramref name="passwordHeld"/> is this agent's "I already hold a password for the next
    /// join" flag (coop.ini, --password, or an answered prompt), stamped on every row: the
    /// plugin's in-browser prompt fires only on locked-and-not-held, so a player who
    /// configured a password is never asked for it.
    /// </summary>
    public bool PublishBrowserServers(
        IReadOnlyList<(string Name, string Address, byte Players, byte MaxPlayers, bool Locked, string Module)> servers,
        bool passwordHeld = false) =>
        PublishBrowserServers(
            [.. servers.Select(s => (s.Name, s.Address, s.Players, s.MaxPlayers, s.Locked, s.Module, (IReadOnlyList<string>)[]))],
            passwordHeld);

    public bool PublishBrowserServers(
        IReadOnlyList<(string Name, string Address, byte Players, byte MaxPlayers, bool Locked,
            string Module, IReadOnlyList<string> Portraits)> servers,
        bool passwordHeld = false)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(servers.Count, BlockBrowserCapacity);

        // The held flag folds into the signature so gaining or losing a password rewrites
        // the rows even when the server list itself has not changed.
        string signature = $"held={(passwordHeld ? 1 : 0)}|" + string.Join('|', servers.Take(count)
            .Select(s => $"{s.Name}@{s.Address}:{s.Players}/{s.MaxPlayers}:{(s.Locked ? 1 : 0)}:{s.Module}"
                + $":{string.Join(',', s.Portraits)}"));
        if (signature == _lastBrowserSignature)
        {
            return true;
        }

        _lastBrowserSignature = signature;

        for (int i = 0; i < count; i++)
        {
            nint entry = _pluginBlock + BlockBrowserEntries + (i * BlockBrowserEntryStride);
            nint detail = _pluginBlock + BlockBrowserDetails + (i * BlockBrowserDetailStride);
            (string name, string address, byte players, byte max, bool locked, string module,
                IReadOnlyList<string> portraits) = servers[i];

            // ⚠ The module no longer rides inside the name. It used to be folded in as
            // "Name [module]" because the row was the only place to put it; the row is now
            // the host's chosen name alone, and the world has its own field for the details
            // pane — which is what a player actually reads before joining.
            _memory.TryWriteAsciiString(entry + BlockBrowserEntryName, name, BlockBrowserEntryNameLength);
            _memory.TryWriteAsciiString(entry + BlockBrowserEntryAddress, address, BlockBrowserEntryAddressLength);
            _memory.TryWrite(entry + BlockBrowserEntryPlayers, players);
            _memory.TryWrite(entry + BlockBrowserEntryMaxPlayers, max);
            _memory.TryWrite(entry + BlockBrowserEntryLocked, locked ? (byte)1 : (byte)0);
            _memory.TryWrite(entry + BlockBrowserEntryPasswordHeld, passwordHeld ? (byte)1 : (byte)0);

            // Resolved HERE, not in the plugin: reading a module's display name means opening
            // module files, which is the agent's job — the plugin draws what it is handed.
            _memory.TryWriteAsciiString(
                detail + BlockBrowserDetailModule, DescribeModule(module), BlockBrowserDetailModuleLength);

            // "where the host is", as the game's own loading art. Composed rather than looked
            // up: the engine builds these names the same way ("load_%s"), and the area's own
            // LoadScreenID reads 0 on every base-game module.
            _memory.TryWriteAsciiString(
                detail + BlockBrowserDetailLoadScreen,
                module.Length > 0 ? "load_" + module.ToLowerInvariant() : string.Empty,
                BlockBrowserDetailLoadScreenLength);

            for (int p = 0; p < BlockBrowserDetailPortraitCount; p++)
            {
                _memory.TryWriteAsciiString(
                    detail + BlockBrowserDetailPortraits + (p * BlockBrowserDetailPortraitLength),
                    p < portraits.Count ? portraits[p] : string.Empty,
                    BlockBrowserDetailPortraitLength);
            }
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

    /// <summary>Agent -&gt; plugin: peers the session has admitted right now, written every
    /// dispatcher tick regardless of player resolve — the seamless-arrival gate's signal.</summary>
    private const int BlockSessionPeers = 0xE1C;

    /// <summary>Publishes the session's admitted peer count (0 = no session/nobody), so the
    /// plugin can hold the load screen for the first sync without waiting on the 1 Hz
    /// player-resolve that RemoteCount depends on.</summary>
    public bool PublishSessionPeerCount(int count) =>
        _pluginBlock != 0 && _memory.TryWrite(_pluginBlock + BlockSessionPeers, (uint)Math.Max(0, count));

    /// <summary>
    /// Asks the game to prompt the player for a password: the plugin force-opens the developer
    /// console with <c>password&#160;</c> pre-typed, and the answer comes back as an ordinary
    /// console command.
    ///
    /// The agent cannot take the keystroke itself — it is a console window behind a fullscreen
    /// game, and the player is looking at the main menu — so the ask has to be handed to the
    /// side that owns the screen. Seeds the serial from whatever is already in the block, so a
    /// reattached agent does not re-ask on its first request.
    /// </summary>
    public bool RequestPasswordPrompt()
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        if (_passwordPromptSerial == 0
            && _memory.TryReadUInt32(_pluginBlock + BlockBrowserPasswordSerial, out uint current))
        {
            _passwordPromptSerial = current;
        }

        return _memory.TryWrite(_pluginBlock + BlockBrowserPasswordSerial, ++_passwordPromptSerial);
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
    /// Reads what a spectating peer needs to run the same conversation: the tag of the NPC
    /// the local player is talking to, the reply they last picked (serial + slot), and the
    /// pick LOG — recent picks each stamped with the line they were made on, opaque here.
    /// </summary>
    public bool TryReadLocalDialogueFollow(
        out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog,
        out int nodeIndex, out uint nodeSerial)
    {
        partnerTag = string.Empty;
        replySerial = 0;
        replyIndex = 0;
        pickLog = [];
        nodeIndex = -1;
        nodeSerial = 0;

        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalDialoguePartnerTag, BlockDialoguePartnerTagLength, out partnerTag);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalDialogueReplySerial, out replySerial);
        ok &= _memory.TryReadInt32(_pluginBlock + BlockLocalDialogueReplyIndex, out replyIndex);

        // ⚠ The serial FIRST, then the node — the mirror image of the plugin's write order,
        // which is what makes the pair safe to read without a lock. The plugin writes node
        // then serial; reading serial then node means a value torn between the two reads is
        // seen as an OLD serial with a NEW node, which the follower simply does not act on,
        // rather than a new serial pointing at a node that has not landed yet.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalDialogueNodeSerial, out nodeSerial);
        ok &= _memory.TryReadInt32(_pluginBlock + BlockLocalDialogueNode, out nodeIndex);

        byte[] log = new byte[DialoguePickLogBytes];
        if (_memory.TryRead(_pluginBlock + BlockLocalDialoguePicks, log))
        {
            pickLog = log;
        }

        partnerTag ??= string.Empty;
        return ok;
    }

    /// <summary>
    /// Reads the object the local player is interacting with, so the session can claim it on
    /// their behalf. An empty tag means "nothing claimed" and is the ordinary case.
    /// </summary>
    public bool TryReadLocalInteraction(out string tag, out uint kind, out bool active)
    {
        tag = string.Empty;
        kind = 0;
        active = false;

        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalInteractionTag, BlockInteractionTagLength, out tag);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalInteractionKind, out kind);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalInteractionActive, out uint live);

        tag ??= string.Empty;
        active = live != 0;
        return ok;
    }

    /// <summary>
    /// Hands the plugin every object currently held, by anyone. The COUNT is written LAST:
    /// the plugin acts on it, and it must never see a count that includes an entry whose tag
    /// has not landed yet — the same ordering rule as the dialogue active flag below.
    /// </summary>
    public bool WriteInteractionLocks(IReadOnlyList<InteractionLock> locks)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(locks?.Count ?? 0, BlockInteractionLockCapacity);
        bool ok = true;

        for (int i = 0; i < count; i++)
        {
            InteractionLock entry = locks![i];
            nint slot = _pluginBlock + BlockInteractionLocks + (i * BlockInteractionLockStride);

            ok &= _memory.TryWrite(slot, entry.PeerId);
            ok &= _memory.TryWrite(slot + 4, (uint)entry.Kind);
            ok &= _memory.TryWriteAsciiString(slot + 8, entry.Tag ?? string.Empty, BlockInteractionTagLength);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockInteractionLockCount, (uint)count);
        return ok;
    }

    /// <summary>
    /// Reads this instance's companion roster out of the block, where the plugin republishes it
    /// from its own party table every frame.
    ///
    /// ⚠ THE VALID BIT IS NOT DECORATION. A game sitting at the main menu has no party table
    /// at all, and the plugin leaves the word at zero there — which is bit-identical to "the
    /// party table says nobody is recruited". A host that published the second while it was
    /// really the first would broadcast an empty roster to a client that HAS companions, and
    /// the client would faithfully dismiss all of them. So an unset bit 31 is reported as "no
    /// reading", never as an empty roster.
    /// </summary>
    public bool TryReadLocalPartyRoster(out PartyRoster roster)
    {
        roster = PartyRoster.Empty;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockLocalPartyRoster, out uint packed)
            || (packed & BlockPartyRosterValid) == 0)
        {
            return false;
        }

        roster = new PartyRoster(
            (ushort)((packed >> BlockPartyRosterAvailableShift) & BlockPartyRosterSlotMask),
            (ushort)((packed >> BlockPartyRosterPartyShift) & BlockPartyRosterSlotMask));
        return true;
    }

    /// <summary>
    /// Hands the plugin the host's roster to reconcile its own against, or clears it.
    ///
    /// One dword, written whole — there is no ordering rule to get wrong here because there is
    /// only one store. Clearing (<paramref name="valid"/> false) writes a plain 0, so the
    /// plugin's "is anybody telling me what the roster should be" test is the same test in both
    /// directions and a disconnected agent stops the reconciliation by doing nothing special.
    /// </summary>
    public bool WriteHostPartyRoster(bool valid, PartyRoster roster)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        uint packed = valid
            ? BlockPartyRosterValid
                | ((roster.AvailableMask & BlockPartyRosterSlotMask) << BlockPartyRosterAvailableShift)
                | ((roster.PartyMask & BlockPartyRosterSlotMask) << BlockPartyRosterPartyShift)
            : 0u;

        return _memory.TryWrite(_pluginBlock + BlockHostPartyRoster, packed);
    }

    /// <summary>
    /// Tells the plugin which conversation a peer owns, so it can open the same one locally
    /// and follow the owner's picks. The active flag is written LAST: the plugin acts on it,
    /// and it must never see "follow a conversation" before it knows whose.
    /// </summary>
    public bool WriteRemoteDialogue(
        bool active, string partnerTag, uint replySerial, int replyIndex, byte[]? pickLog,
        int nodeIndex, uint nodeSerial)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(
            _pluginBlock + BlockRemoteDialoguePartnerTag, partnerTag ?? string.Empty, BlockDialoguePartnerTagLength);

        if (pickLog is { Length: DialoguePickLogBytes })
        {
            ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialoguePicks, pickLog);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueReplyIndex, (uint)replyIndex);
        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueReplySerial, replySerial);

        // Node before its serial, so the plugin never sees a bumped serial pointing at a node
        // that has not arrived — the same ordering rule as the active flag below, and for the
        // same reason: the serial is what the far side acts on.
        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueNode, (uint)nodeIndex);
        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueNodeSerial, nodeSerial);

        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueActive, active ? 1u : 0u);
        return ok;
    }

    /// <summary>
    /// Reads every hostile creature in the module. "Hostile" means a different faction
    /// from the player, which is the same test the engine's own targeting uses, and it
    /// keeps party members, neutral NPCs and other players' phantoms out of the list.
    /// </summary>
    public List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers)
    {
        List<EnemyState> enemies = [];

        if (_player is null)
        {
            return enemies;
        }

        Vector3f playerPosition = default;
        TryReadVector(_player.Address + OffPosition, out playerPosition);

        // Phantoms are no longer plot-flagged (7.4's redesign: they take visible damage
        // now), so they must be excluded by id or a teammate's body would be published
        // as an enemy, pinned, and mirrored.
        HashSet<uint>? phantomIds = ReadPhantomObjectIds();

        Dictionary<uint, (uint Serial, ushort Animation)>? oneShots = ReadCreatureOneShots();

        // Two passes: collect every candidate, then keep the ones that matter most. A
        // module can hold more creatures than the census cap, and truncating in scan
        // order shed arbitrary bodies; hostiles go first (a fight that desyncs is worse
        // than a bystander that does), then the nearest bystanders — which is also where
        // scripted scenes play, next to the player watching them.
        List<(EnemyState State, bool Fighting, float DistanceSquared)> candidates = [];

        foreach (CreatureRef creature in EnumerateCreatures())
        {
            if (creature.IsPlayer || creature.IsPartyMember)
            {
                continue;
            }

            if (phantomIds is not null && phantomIds.Contains(creature.ObjectId))
            {
                continue;
            }

            // Belt over the id table: the plugin clears a phantom's slot before the body has
            // finished dying, and one census pass in that window published the freed body as
            // a dead enemy (measured on the tar_m03aa warp — the peer dutifully executed the
            // kill on a stand-in it materialised for the ghost). The tag names every body the
            // co-op blueprint spawned — phantoms and materialised stand-ins alike, none of
            // which is ever this instance's enemy to publish — and it survives to the last
            // readable moment.
            if (creature.Tag == GameFiles.PhantomBlueprint.Tag)
            {
                continue;
            }

            // A plot creature — usually a cutscene actor — is included for PRESENCE, so
            // scripted scenes play out identically on both screens, but flagged so the
            // combat half leaves its health alone: un-killable is the module's rule.
            //
            // No faction filter any more, deliberately. It once excluded "friendly" NPCs,
            // but a story NPC in the player's faction (Carth on the Endar Spire bridge,
            // pre-join companions generally) is EXACTLY a cutscene actor whose presence
            // must sync — measured live: Carth was the one body the census dropped. The
            // exclusions that mattered are explicit: the player and party by their own
            // checks above, phantoms by id.
            bool plot = _memory.TryReadInt32(creature.Address + OffPlot, out int plotFlag) && plotFlag != 0;

            // Priority signal, not a hostility oracle: a creature the engine itself has
            // mid-combat-round must never be shed by the cap. Faction semantics are a
            // matrix lookup we cannot make externally (docs/research/combat-participation.md
            // §2), so the engine's own in-combat flag is the honest signal.
            bool fighting = !plot
                && _memory.TryReadInt32(creature.Address + OffInCombat, out int inCombat)
                && inCombat != 0;

            // Signed: an overkilled enemy sits at negative hit points, and an unsigned
            // read would report it as 65531 hp and alive — the death would never
            // replicate.
            //
            // ⚠ AND THE READ'S RESULT IS HONOURED, because `current <= 0` becomes Dead below.
            // Discarding the bool meant a FAILED read produced current = 0 and published a
            // perfectly healthy creature as a corpse. Measured: the client killed eight or
            // nine creatures within ~100 ms of the host opening a conversation, four times out
            // of four, and five to eight seconds later the same ids came back alive and were
            // spawned again as duplicate stand-ins. Cross-process reads fail transiently — the
            // agent's own readout has a READ-FAILING counter that reached a thousand — so this
            // is not a theoretical branch.
            //
            // Skipping the creature is the right failure: presence is a LEVEL re-sent every
            // tick, so a body omitted from one snapshot is simply described by the next, while
            // a body wrongly marked dead is acted on immediately and irreversibly.
            if (!_memory.TryRead(creature.Address + OffCurrentHitPoints, out short current))
            {
                continue;
            }

            ushort max = (ushort)Math.Clamp(TryReadMaxHitPoints(creature.Address), 0, ushort.MaxValue);

            // Presence: where this engine has the body and what it is playing, so the
            // mirroring side can drive its copy there. Best-effort — a failed read leaves
            // the defaults, which the plugin's driver rejects as implausible.
            TryReadVector(creature.Address + OffPosition, out Vector3f position);

            float facing = 0f;
            if (TryReadVector(creature.Address + OffOrientation, out Vector3f orientation))
            {
                facing = Angles.Normalize(MathF.Atan2(orientation.Y, orientation.X));
            }

            _memory.TryReadUInt32(creature.Address + OffAnimation, out uint animation);

            // What the creature looks like and whose side it is on — enough for a mirror
            // that has no copy at all to build a stand-in (no blueprint name survives on a
            // live creature; see Packets.EnemyState). Best-effort: zero appearance simply
            // means the mirror will not materialise this one.
            _memory.TryRead(creature.Address + OffAppearanceType, out ushort appearance);

            ushort faction = 0;
            if (_memory.TryReadUInt32(creature.Address + OffCreatureStats, out uint statsPtr)
                && statsPtr != 0
                && _memory.TryReadUInt32((nint)statsPtr + StatsFaction, out uint factionId)
                && factionId <= ushort.MaxValue)
            {
                faction = (ushort)factionId;
            }

            (uint oneShotSerial, ushort oneShotAnim) =
                oneShots is not null && oneShots.TryGetValue(creature.ObjectId, out var shot)
                    ? shot
                    : (0u, (ushort)0);

            // ⚠ Distance to the NEAREST PLAYER, not to us.
            //
            // The cap sheds by this number, and a census measured from one player only can
            // see one place at a time. Two players a room apart is the ordinary case, and
            // it is exactly when the publisher's own surroundings are the wrong thing to
            // measure: the creatures around the partner — the ones they are about to fight —
            // sort last and fall off the list, so their copies stop being driven and stop
            // being health-mirrored at the moment they start to matter. Peers are passed in
            // already filtered to this module.
            float dx = position.X - playerPosition.X;
            float dy = position.Y - playerPosition.Y;
            float nearestSquared = (dx * dx) + (dy * dy);

            for (int p = 0; p < otherPlayers.Count; p++)
            {
                float px = position.X - otherPlayers[p].X;
                float py = position.Y - otherPlayers[p].Y;
                float squared = (px * px) + (py * py);
                if (squared < nearestSquared)
                {
                    nearestSquared = squared;
                }
            }

            candidates.Add((
                new EnemyState(
                    creature.ObjectId, (ushort)Math.Max((int)current, 0), max, current <= 0,
                    position, facing, animation, oneShotSerial, oneShotAnim, plot,
                    appearance, faction),
                fighting,
                nearestSquared));
        }

        candidates.Sort((a, b) => a.Fighting != b.Fighting
            ? (a.Fighting ? -1 : 1)
            : a.DistanceSquared.CompareTo(b.DistanceSquared));

        int count = Math.Min(candidates.Count, BlockEnemyCapacity);
        for (int i = 0; i < count; i++)
        {
            enemies.Add(candidates[i].State);
        }

        return enemies;
    }


    /// <summary>
    /// The plugin's captured fire-and-forget table (swings, parries, draws), keyed by
    /// object id, or null with no plugin attached. Ids that are not enemies (companions,
    /// neutral NPCs also swing) are simply never matched by the census.
    /// </summary>
    private Dictionary<uint, (uint Serial, ushort Animation)>? ReadCreatureOneShots()
    {
        if (_pluginBlock == 0)
        {
            return null;
        }

        Dictionary<uint, (uint, ushort)>? shots = null;
        for (int i = 0; i < BlockEnemyOneShotCapacity; i++)
        {
            nint slot = _pluginBlock + BlockEnemyOneShotTable + (i * BlockEnemyOneShotStride);

            if (!_memory.TryReadUInt32(slot + 0x00, out uint id) || id == 0)
            {
                continue;
            }

            _memory.TryReadUInt32(slot + 0x04, out uint animation);
            _memory.TryReadUInt32(slot + 0x08, out uint serial);

            if (serial != 0)
            {
                (shots ??= [])[id] = (serial, (ushort)animation);
            }
        }

        return shots;
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
            // ⚠ BlockPhantomIdTable, not BlockPhantomIds. Reading eight dwords from 0x264
            // read the keep-alive flag and the focus keeper's pause counter as if they were
            // object ids, so ids 1, 2, 3... were quietly struck out of the enemy census as
            // "that's a phantom" — and the counter climbs, so the set of creatures it hid
            // drifted over a session.
            if (_memory.TryReadUInt32(_pluginBlock + BlockPhantomIdTable + (i * 4), out uint id) && id != 0)
            {
                (ids ??= []).Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// What this instance has DEALT to each enemy, straight out of the plugin's ledger.
    ///
    /// The replacement for reporting enemy HEALTH back to the fight's owner, and the reason
    /// is the whole point of the channel: health is a level that has to be sampled, and the
    /// owner's mirror writes its own value back onto our copy between samples, so a hit had
    /// to win a race to be reported at all. Measured live — a client attacking one soldier
    /// for ninety seconds moved it from 15 hit points to 11 and no further. A cumulative
    /// sum of damage dealt cannot be raced, because nothing ever reads it back out of the
    /// game.
    /// </summary>
    public (uint Generation, List<EnemyDamageEntry> Damage) ReadDamageDealt()
    {
        List<EnemyDamageEntry> damage = [];

        if (_pluginBlock == 0)
        {
            return (0, damage);
        }

        // Opting in is what turns the accumulation on inside the engine's damage hook. Only
        // an instance that MIRRORS enemies needs it; asking for it here rather than having
        // the plugin guess keeps the decision with the side that knows who owns the fight.
        if (!_damageDealtEnabled)
        {
            _damageDealtEnabled = _memory.TryWrite(_pluginBlock + BlockDamageDealtEnabled, 1u);
            return (0, damage);
        }

        _memory.TryReadUInt32(_pluginBlock + BlockDamageDealtGeneration, out uint generation);
        _memory.TryReadUInt32(_pluginBlock + BlockDamageDealtCount, out uint count);

        for (int i = 0; i < Math.Min(count, BlockDamageLedgerCapacity); i++)
        {
            nint slot = _pluginBlock + BlockDamageDealtSlots + (i * BlockDamageLedgerStride);

            if (!_memory.TryReadUInt32(slot + 0x00, out uint objectId) || objectId == 0)
            {
                continue;
            }

            if (_memory.TryReadUInt32(slot + 0x04, out uint cumulative) && cumulative > 0)
            {
                damage.Add(new EnemyDamageEntry(objectId, cumulative));
            }
        }

        return (generation, damage);
    }

    /// <summary>Whether we have told the plugin to start accumulating. One write, latched:
    /// the flag lives in the block and only a fresh block (a restarted game) clears it.</summary>
    private bool _damageDealtEnabled;

    /// <summary>Hits the plugin's ledger had no room for. Non-zero means a peer's damage is
    /// being under-reported — the exact failure this channel exists to remove — so it is
    /// surfaced rather than swallowed.</summary>
    public uint ReadDamageDealtDropped()
    {
        if (_pluginBlock == 0)
        {
            return 0;
        }

        _memory.TryReadUInt32(_pluginBlock + BlockDamageDealtDropped, out uint dropped);
        return dropped;
    }

    /// <summary>
    /// Hands the plugin the total every peer has dealt to each of our enemies, for it to
    /// spend as real engine damage on the game thread.
    ///
    /// ⚠ The count is zeroed FIRST and restored LAST, so the plugin either sees the whole
    /// table or none of it. Elsewhere in this block an id written last is enough, because a
    /// torn row costs a frame of a wrong position; here it would spend damage against the
    /// wrong creature. A pass the plugin catches mid-write simply does nothing, and the
    /// totals are cumulative levels, so the next pass makes it whole.
    ///
    /// The epoch is the module the ids belong to — the plugin drops its own applied totals
    /// when it changes, so the two halves restart together rather than the plugin holding a
    /// high applied total against an agent that has already gone back to zero.
    /// </summary>
    public bool TryPublishDamageOwed(uint epoch, IReadOnlyList<EnemyDamageEntry> owed)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockDamageOwedCount, 0u);
        int count = Math.Min(owed.Count, BlockDamageLedgerCapacity);

        for (int i = 0; i < count; i++)
        {
            nint slot = _pluginBlock + BlockDamageOwedSlots + (i * BlockDamageLedgerStride);
            ok &= _memory.TryWrite(slot + 0x04, owed[i].CumulativeDamage);
            ok &= _memory.TryWrite(slot + 0x00, owed[i].ObjectId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockDamageOwedEpoch, epoch);
        ok &= _memory.TryWrite(_pluginBlock + BlockDamageOwedCount, (uint)count);
        return ok;
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

            uint flags = (enemy.Dead ? EnemyFlagDead : 0u) | (enemy.Plot ? EnemyFlagPlot : 0u);

            ok &= _memory.TryWrite(slot + 0x04, enemy.CurrentHealth);
            ok &= _memory.TryWrite(slot + 0x06, enemy.MaxHealth);
            ok &= _memory.TryWrite(slot + 0x08, flags);

            // Object id last, so the plugin never reads a slot whose id and payload
            // belong to different enemies.
            ok &= _memory.TryWrite(slot + 0x00, enemy.ObjectId);

            // The presence half, in its own region: the owner's position, facing and
            // animation, which the plugin's driver moves the local copy to match. Same
            // id-last discipline per slot.
            nint presence = _pluginBlock + BlockEnemyPresenceSlots + (i * BlockEnemyPresenceStride);
            ok &= _memory.TryWrite(presence + 0x04, enemy.Position.X);
            ok &= _memory.TryWrite(presence + 0x08, enemy.Position.Y);
            ok &= _memory.TryWrite(presence + 0x0C, enemy.Position.Z);
            ok &= _memory.TryWrite(presence + 0x10, enemy.Facing);
            ok &= _memory.TryWrite(presence + 0x14, enemy.Animation);
            ok &= _memory.TryWrite(presence + 0x18, flags);
            ok &= _memory.TryWrite(presence + 0x1C, enemy.OneShotSerial);
            ok &= _memory.TryWrite(presence + 0x20, (uint)enemy.OneShotAnimation);
            ok &= _memory.TryWrite(presence + 0x24, enemy.Appearance);
            ok &= _memory.TryWrite(presence + 0x26, enemy.Faction);
            ok &= _memory.TryWrite(presence + 0x00, enemy.ObjectId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockEnemyPresenceCount, (uint)count);
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
    /// <summary>
    /// CSWSObject + this = portrait_id, the portraits.2da row (symbol database: ushort).
    ///
    /// ⚠ NOT +0x20. That field is typed CResRef and looked like the portrait itself, which
    /// would have kept this out of portraits.2da entirely — but measured on a live PC it is
    /// SIXTEEN ZERO BYTES while +0x30 reads 21. The engine keeps the id and resolves the
    /// texture through the table; the resref field is dead for a creature in the world.
    ///
    /// ⚠ NOT YET CONFIRMED TO BE THE ID THE GUI USES. This is the SERVER object's copy, and
    /// this codebase has one standing trap about exactly that: the process runs a client and
    /// a server half, and the half that draws is not always the half that stores. The HUD
    /// portrait is drawn from <c>CSWCCreatureStats + 0x11C</c> (the client's stats), and if
    /// the two disagree, that is the one to read. The browser currently fills its portrait
    /// boxes but the texture does not resolve — see docs/research/server-browser.md.
    /// </summary>
    private const int OffPortraitId = 0x30;

    /// <summary>
    /// The portrait resrefs of this instance's party — the PC first, then each companion in
    /// the active party, for the in-game server browser's details pane.
    ///
    /// Best-effort throughout: a portrait that cannot be read is skipped rather than failing
    /// the list, because this is decoration on a lobby screen and a missing face must not
    /// stop a server being advertised.
    /// </summary>
    public IReadOnlyList<string> ReadPartyPortraits()
    {
        List<string> portraits = [];

        if (_player is not null)
        {
            AddPortrait(portraits, _player.Address);
        }

        if (!TryGetPartyTable(out nint partyTable)
            || !_memory.TryReadInt32(partyTable + PartyCount, out int count)
            || count is <= 0 or > PartyMaxCompanions)
        {
            return portraits;
        }

        Dictionary<uint, nint> byId = [];
        foreach (CreatureRef c in EnumerateCreatures())
        {
            byId[c.ObjectId] = c.Address;
        }

        for (int i = 0; i < count; i++)
        {
            if (!_memory.TryReadInt32(partyTable + PartyActiveSlots + (i * 4), out int slot)
                || slot is < 0 or >= PartyNpcSlots
                || !_memory.TryReadUInt32(partyTable + PartyObjectIds + (slot * 4), out uint objectId)
                || objectId == 0x7F000000
                || !byId.TryGetValue(objectId, out nint creature))
            {
                continue;
            }

            AddPortrait(portraits, creature);
        }

        return portraits;
    }

    /// <summary>Appends one creature's portrait texture, resolved id -> portraits.2da.</summary>
    private void AddPortrait(List<string> portraits, nint creature)
    {
        if (!_memory.TryRead(creature + OffPortraitId, out ushort id) || id == 0)
        {
            return;
        }

        string resref = PortraitResRef(id);
        if (resref.Length > 0)
        {
            portraits.Add(resref);
        }
    }

    /// <summary>portraits.2da, read once. Null when it cannot be read at all.</summary>
    private GameFiles.TwoDa? _portraitTable;
    private bool _portraitTableResolved;

    /// <summary>Resolved rows, since a party of three asks for the same handful forever.</summary>
    private readonly Dictionary<ushort, string> _portraitResRefs = [];

    /// <summary>
    /// The texture for a portraits.2da row — the <c>baseresref</c> column, which is what the
    /// GUI's image labels take.
    /// </summary>
    private string PortraitResRef(ushort id)
    {
        if (_portraitResRefs.TryGetValue(id, out string? cached))
        {
            return cached;
        }

        string resref = string.Empty;

        try
        {
            if (!_portraitTableResolved)
            {
                _portraitTableResolved = true;

                using var process = System.Diagnostics.Process.GetProcessById(_memory.ProcessId);
                string? gameDirectory = Path.GetDirectoryName(process.MainModule?.FileName);

                if (gameDirectory is not null)
                {
                    using GameFiles.GameFiles files = new(gameDirectory);
                    if (files.TryReadResource("portraits", "2da", out byte[] data))
                    {
                        _portraitTable = GameFiles.TwoDa.Parse(data);
                    }
                }
            }

            if (_portraitTable is not null && id < _portraitTable.RowCount)
            {
                resref = _portraitTable.Get(id, "baseresref");
            }
        }
        catch (Exception)
        {
            // A missing face is cosmetic; never let it disturb the session.
        }

        _portraitResRefs[id] = resref;
        return resref;
    }

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

        Dictionary<uint, (uint Serial, ushort Animation)>? oneShots = ReadCreatureOneShots();

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

            (uint oneShotSerial, ushort oneShotAnim) =
                oneShots is not null && oneShots.TryGetValue(objectId, out var shot)
                    ? shot
                    : (0u, (ushort)0);

            members.Add(new PartyMemberState(
                (byte)slot, position, facing, animation, health, oneShotSerial, oneShotAnim));
        }

        return members;
    }

    // Party presence (block version 20): companion state handed to the plugin, which
    // drives the body on the game thread with the seamless recipe. Roster slot + 1 @0
    // (0 = empty, written last), x f32 @4, y @8, z @0xC, facing f32 @0x10, animation u32
    // @0x14, health i32 @0x18, one-shot serial u32 @0x1C, one-shot anim u32 @0x20.
    private const int BlockPartyPresenceCount = 0x1C20;
    private const int BlockPartyPresenceSlots = 0x1C30;
    private const int BlockPartyPresenceStride = 0x28;
    private const int BlockPartyPresenceCapacity = 4;

    /// <summary>
    /// Freezes the local party AI (solo mode) and mirrors the host's companions onto the
    /// matching local creatures by slot.
    ///
    /// With the plugin attached, the state goes into the shared block and the plugin's
    /// PartyPresenceController drives the body every frame — server object AND client
    /// twin, so the rendered companion tracks instead of trailing the engine's 512 ms
    /// ease and stepping between agent ticks. The external write path below survives only
    /// as the plugin-less fallback (the mock game); it never touched the twin, which is
    /// exactly why it was superseded.
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

        if (_pluginBlock != 0)
        {
            int written = 0;
            for (int i = 0; i < BlockPartyPresenceCapacity; i++)
            {
                nint slot = _pluginBlock + BlockPartyPresenceSlots + (i * BlockPartyPresenceStride);

                if (i < members.Count && members[i].SlotIndex < PartyNpcSlots)
                {
                    PartyMemberState m = members[i];
                    _memory.TryWrite(slot + 0x04, m.Position.X);
                    _memory.TryWrite(slot + 0x08, m.Position.Y);
                    _memory.TryWrite(slot + 0x0C, m.Position.Z);
                    _memory.TryWrite(slot + 0x10, m.Facing);
                    _memory.TryWrite(slot + 0x14, m.Animation);
                    _memory.TryWrite(slot + 0x18, m.Health);
                    _memory.TryWrite(slot + 0x1C, m.OneShotSerial);
                    _memory.TryWrite(slot + 0x20, (uint)m.OneShotAnimation);
                    _memory.TryWrite(slot + 0x00, (uint)m.SlotIndex + 1);
                    written++;
                }
                else
                {
                    _memory.TryWrite(slot + 0x00, 0u);
                }
            }

            _memory.TryWrite(_pluginBlock + BlockPartyPresenceCount, (uint)written);
            return written;
        }

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

    /// <summary>
    /// Cheap validity check for a cached player pointer: the object still carries the
    /// creature vtable, and its stats block still says it is a PC. Both are what
    /// <see cref="TryResolve"/> selected on, so anything that fails here is no longer the
    /// object we resolved.
    /// </summary>
    /// <summary>At most one full re-scan per second, no matter how often validation fails.</summary>
    private const int ResolveRetryMs = 1000;

    private long _lastResolveAttemptMs;
    private int _playerLostLogged;

    /// <summary>Optional sink for "why did the cached player stop validating" — the answer
    /// decides whether the validation is too strict or the pointer really did go stale.</summary>
    public Action<string>? ResolveDiagnostic { get; set; }

    /// <summary>Names the specific check that failed, so this is diagnosable from a log
    /// rather than by rebuilding with printfs.</summary>
    private string DescribePlayerMismatch(nint creature)
    {
        if (!_memory.TryReadUInt32(creature, out uint vtable))
        {
            return $"player 0x{creature:X}: vtable unreadable";
        }

        if (vtable != CreatureVtable)
        {
            return $"player 0x{creature:X}: vtable 0x{vtable:X} != 0x{CreatureVtable:X}";
        }

        if (!_memory.TryReadPointer(creature + OffCreatureStats, out nint stats) || stats == 0)
        {
            return $"player 0x{creature:X}: stats pointer missing";
        }

        return !_memory.TryReadInt32(stats + StatsIsPc, out int isPc)
            ? $"player 0x{creature:X}: is-PC flag unreadable"
            : $"player 0x{creature:X}: is-PC flag is {isPc}";
    }

    private bool StillLooksLikeThePlayer(nint creature) =>
        _memory.TryReadUInt32(creature, out uint vtable)
        && vtable == CreatureVtable
        && _memory.TryReadPointer(creature + OffCreatureStats, out nint stats)
        && stats != 0
        && _memory.TryReadInt32(stats + StatsIsPc, out int isPc)
        && isPc != 0;

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

        // ⚠ A MISS MUST NOT COST A SCAN. RebuildItemTagCache walks the game's committed
        // memory looking for the item vtable; this method runs three times per tick (body,
        // right hand, left hand), so a single id that never resolves — an empty slot holding
        // a stale id, or an item whose tag cannot be read — meant three full memory scans
        // EVERY TICK. That pinned a CPU core, dropped the pump from 60 Hz to about 4, and
        // measured end to end as a peer taking 3-7 seconds to move. It was the single
        // biggest cause of the "everything lags by seconds" report.
        //
        // So: remember the ids that did not resolve and never scan for them again, and
        // rate-limit rebuilds regardless. A genuinely new item (one just picked up) is found
        // by the next scheduled rebuild rather than instantly, which is imperceptible for
        // what this feeds — the resref a peer's phantom wears.
        if (_itemTagMisses.Contains(itemId))
        {
            return string.Empty;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - _itemTagsBuiltAtMs < ItemTagRebuildMs)
        {
            return string.Empty;
        }

        _itemTagsBuiltAtMs = nowMs;
        RebuildItemTagCache();

        if (_itemTags.TryGetValue(itemId, out string? found))
        {
            return found;
        }

        _itemTagMisses.Add(itemId);
        return string.Empty;
    }

    /// <summary>Minimum gap between item-tag scans, however many lookups miss.</summary>
    private const int ItemTagRebuildMs = 15_000;

    private readonly HashSet<uint> _itemTagMisses = [];

    /// <summary>Which module the address caches were built for.</summary>
    private string _cachedForModule = string.Empty;
    private long _itemTagsBuiltAtMs;

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

        // A fresh scan can resolve ids that missed before (an item just picked up), so the
        // miss list must not outlive it.
        _itemTagMisses.Clear();

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

        // ⚠ A CExoString's length routinely INCLUDES its terminating null — the engine's
        // own journal entries store 11 for "end_attack". Taking the length at face value
        // put a trailing '\0' inside the string, and because that string is written back
        // into the block and re-read as the next request's tag, every round trip added
        // another byte: measured live as the tag growing 'end_attack\0', 'end_attack\0\0'
        // … and the quest re-applying every few seconds because it never matched.
        // Same trap as the module name in Handoff 0g; cut at the first null.
        int end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, end >= 0 ? end : buffer.Length);
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
