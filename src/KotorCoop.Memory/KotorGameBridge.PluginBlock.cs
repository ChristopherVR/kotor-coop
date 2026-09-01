// The in-process plugin's SharedBlock layout: every Block* offset constant, the plugin
// signature/version, and the core IPC channels that talk directly to those offsets (barks,
// creature liveness, peer effects, and the raw plugin-state read/write helpers). Other
// concern files declare their OWN Block* offsets locally, next to the methods that use them.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
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

    /// <summary>ROUND 39: mirrors <c>SharedBlock.OffsetLocalCombatExcludedId</c> exactly. Kept
    /// as a slot-0 mirror alongside <see cref="BlockLocalCombatExcludedIds"/> below — see that
    /// region's own comment.</summary>
    private const int BlockLocalCombatExcludedId = 0x1E5C;

    // N-slot local combat (shared block version 61, region 0x4800..0x4900, TotalSize grown to
    // 0x4B00). Mirrors SharedBlock.OffsetLocalCombatExcludedIds. See
    // docs/research/combat-participation.md §11.
    private const int BlockLocalCombatExcludedIds = 0x4800;
    private const int BlockLocalCombatExcludedIdCapacity = 3;

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

    // Peer effects (non-damage combat effects, shared block version 44, region
    // 0x3A00..0x3B88). Mirrors SharedBlock.OffsetPeerEffect*. See
    // docs/research/effect-sync.md.
    private const int BlockPeerEffectOutSlots = 0x3A08;
    private const int BlockPeerEffectInSlots = 0x3AC8;
    private const int BlockPeerEffectStride = 0x18;
    private const int BlockPeerEffectCapacity = 8;
    private const int BlockPeerEffectId = 0x00;
    private const int BlockPeerEffectKind = 0x04;
    private const int BlockPeerEffectDurationMs = 0x08;
    private const int BlockPeerEffectMagnitude = 0x0C;
    private const int BlockPeerEffectSerial = 0x14;

    // Creature liveness (shared block version 45, region 0x3C00..0x3F08). Mirrors
    // SharedBlock.OffsetLiveness*. See docs/research/creature-liveness.md.
    //
    // Agent -> plugin: the host's latest liveness view (translated for THIS process's own
    // consumption — a client writes what it received over the wire; the host never writes
    // this region at all, it has nothing to reconcile against itself).
    private const int BlockLivenessCount = 0x3C00;
    private const int BlockLivenessSlots = 0x3C04;
    private const int BlockLivenessStride = 0x08;
    private const int BlockLivenessCapacity = 96;

    // Plugin -> agent: cumulative "host says alive, local already dead/gone" anomaly count.
    private const int BlockLivenessAnomalyCount = 0x3F04;

    /// <summary>
    /// Every creature this engine currently knows about in the loaded area — id, whether its
    /// hit points read at or below zero, and whether the module marks it plot. Reuses the
    /// SAME confirmed enumeration <see cref="ReadEnemies"/> builds from (the ghost gate in
    /// docs/research/enemy-presence-sync.md already runs underneath <see cref="EnumerateCreatures"/>),
    /// so an id published here is exactly as trustworthy as one published in the ordinary
    /// census — and, unlike that census, NOT capped or distance-sorted: this is every
    /// creature the object array still resolves, which is the whole point of a liveness
    /// channel meant to catch a body the 32-slot combat census sheds. "Gone" (destroyed) is
    /// never reported here — it is not something a single snapshot can say, only something
    /// derived by comparing this against an earlier one, which is the caller's job (see
    /// docs/research/creature-liveness.md's host-side pump).
    /// </summary>
    public List<CreatureLivenessEntry> ReadCreatureLiveness()
    {
        List<CreatureLivenessEntry> entries = [];

        HashSet<uint>? phantomIds = ReadPhantomObjectIds();

        // Same membership question, same answer, same reason as ReadEnemies — see
        // PartyCensusRule. A companion leaked onto the liveness channel is reported to the peer
        // as a creature it should be reconciling, which is not this channel's business either.
        HashSet<uint> partyRoster = ReadPartyRosterIds();

        foreach (CreatureRef creature in EnumerateCreatures())
        {
            if (PartyCensusRule.IsOwnParty(
                    creature.ObjectId, creature.IsPlayer, creature.IsPartyMember, partyRoster))
            {
                continue;
            }

            if (phantomIds is not null && phantomIds.Contains(creature.ObjectId))
            {
                continue;
            }

            // Never anyone's NPC to report on: the co-op blueprint's tag names every body we
            // (or a mirroring peer) spawned ourselves, phantoms and materialised stand-ins
            // alike — see the identical exclusion in ReadEnemies.
            if (creature.Tag == GameFiles.PhantomBlueprint.Tag)
            {
                continue;
            }

            // Signed, same reason ReadEnemies reads it signed: an overkilled body parks at a
            // negative hit-point count, and an unsigned read would call it healthy. A failed
            // read is skipped for this pass rather than guessed at — the next pass, seconds
            // away, tries again.
            if (!_memory.TryRead(creature.Address + OffCurrentHitPoints, out short current))
            {
                continue;
            }

            entries.Add(new CreatureLivenessEntry(
                creature.ObjectId, current <= 0 ? CreatureLivenessState.Dead : CreatureLivenessState.Alive));
        }

        return entries;
    }

    /// <summary>
    /// Non-owner side: hands the host's latest creature-liveness view to the plugin's
    /// reconciliation controller. <paramref name="entries"/> is already capacity-ordered by
    /// the caller (dead/gone first — see docs/research/creature-liveness.md); this simply
    /// truncates and writes, id-last per slot like every other agent-written array.
    /// </summary>
    public bool TryWriteCreatureLiveness(IReadOnlyList<CreatureLivenessEntry> entries)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = true;
        int count = Math.Min(entries.Count, BlockLivenessCapacity);

        for (int i = 0; i < count; i++)
        {
            CreatureLivenessEntry entry = entries[i];
            nint slot = _pluginBlock + BlockLivenessSlots + (i * BlockLivenessStride);
            ok &= _memory.TryWrite(slot + 0x04, (uint)entry.State);

            // Id last: a half-written slot must never be read as naming the wrong creature.
            ok &= _memory.TryWrite(slot + 0x00, entry.ObjectId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockLivenessCount, (uint)count);
        return ok;
    }

    /// <summary>Non-owner side: the plugin's cumulative "host says alive, local already
    /// dead/gone" anomaly count. 0 (and a false-returning read) when no plugin is attached.</summary>
    public uint ReadLivenessAnomalyCount()
    {
        if (_pluginBlock == 0)
        {
            return 0;
        }

        return _memory.TryReadUInt32(_pluginBlock + BlockLivenessAnomalyCount, out uint count) ? count : 0;
    }

    /// <summary>
    /// Reads the effects <c>PhantomEffectHook</c> captured landing on this instance's
    /// phantoms since the plugin last wrote them. Returns every slot with a nonzero serial;
    /// the caller (<c>CoopAgent.PumpPeerEffects</c>) decides what is new by remembering the
    /// highest serial it has already sent per slot — the same discipline <see cref="ReadBarks"/>
    /// leaves to its caller.
    /// </summary>
    public List<PeerEffectRecord> ReadPeerEffectsOut() => ReadPeerEffectRing(BlockPeerEffectOutSlots);

    /// <summary>
    /// Reads the IN ring back — what a peer's hook captured landing on THIS player's phantom
    /// over there, handed to the plugin by <see cref="TryWritePeerEffectIn"/> for it to replay
    /// here. Mirrors <see cref="ReadPeerEffectsOut"/> exactly, one ring over; built for the
    /// <c>effects</c> console verb (docs/research/effect-sync.md), which needs to show both
    /// halves of the pipeline — nothing before it ever read this ring back out.
    /// </summary>
    public List<PeerEffectRecord> ReadPeerEffectsIn() => ReadPeerEffectRing(BlockPeerEffectInSlots);

    /// <summary>
    /// Shared ring-walk for <see cref="ReadPeerEffectsOut"/> and <see cref="ReadPeerEffectsIn"/>:
    /// same capacity, stride, field layout and early-exit rules (skip a zero serial, skip a
    /// zero id), differing only in which ring's base offset they start from.
    /// </summary>
    private List<PeerEffectRecord> ReadPeerEffectRing(int baseOffset)
    {
        List<PeerEffectRecord> effects = [];
        if (_pluginBlock == 0)
        {
            return effects;
        }

        for (int i = 0; i < BlockPeerEffectCapacity; i++)
        {
            nint slot = _pluginBlock + baseOffset + (i * BlockPeerEffectStride);

            if (!_memory.TryReadUInt32(slot + BlockPeerEffectSerial, out uint serial) || serial == 0)
            {
                continue;
            }

            _memory.TryReadUInt32(slot + BlockPeerEffectId, out uint id);
            _memory.TryReadUInt32(slot + BlockPeerEffectKind, out uint kind);
            _memory.TryReadUInt32(slot + BlockPeerEffectDurationMs, out uint durationMs);
            _memory.TryReadUInt32(slot + BlockPeerEffectMagnitude, out uint magnitude);

            if (id == 0)
            {
                continue;
            }

            effects.Add(new PeerEffectRecord(id, kind, durationMs, magnitude, serial));
        }

        return effects;
    }

    private int _peerEffectInWriteSlot;

    /// <summary>Hands the plugin one effect a peer's hook captured landing on this player's
    /// phantom over there, for it to replay here. Round-robins the ring, same reasoning as
    /// <see cref="TryWriteBark"/>: a burst must not overwrite one entry before the plugin's
    /// pass has seen it.</summary>
    public bool TryWritePeerEffectIn(uint originPeerId, uint kind, uint durationMs, uint magnitude, uint serial)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        nint slot = _pluginBlock + BlockPeerEffectInSlots + (_peerEffectInWriteSlot * BlockPeerEffectStride);
        _peerEffectInWriteSlot = (_peerEffectInWriteSlot + 1) % BlockPeerEffectCapacity;

        bool ok = _memory.TryWrite(slot + BlockPeerEffectKind, kind);
        ok &= _memory.TryWrite(slot + BlockPeerEffectDurationMs, durationMs);
        ok &= _memory.TryWrite(slot + BlockPeerEffectMagnitude, magnitude);
        ok &= _memory.TryWrite(slot + BlockPeerEffectId, originPeerId);

        // Serial last, so the plugin never applies a half-written entry.
        ok &= _memory.TryWrite(slot + BlockPeerEffectSerial, serial);
        return ok;
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

    /// <summary>Transition-cinematic channel (shared block version 42). Plugin -&gt; agent:
    /// what our own next transition plays. Agent -&gt; plugin: what the pending travel should
    /// queue. Mirrors <c>SharedBlock.OffsetCinematic*</c>/<c>OffsetTravelMovie*</c>.</summary>
    private const int BlockCinematicSerial = 0x3800;
    private const int BlockCinematicCount = 0x3804;
    private const int BlockCinematicNames = 0x3808;
    private const int BlockTravelMovieCount = 0x3880;
    private const int BlockTravelMovieNames = 0x3884;
    private const int BlockCinematicNameLength = 16;
    private const int BlockCinematicCapacity = 6;

    /// <summary>Transition-announce channel (shared block version 47, region
    /// 0x38E4..0x3928 — the gap left below the cinematic region and above the peer-effect
    /// one). Plugin -&gt; agent: the destination of THIS instance's own imminent transition,
    /// with a serial bumped on the frame it was resolved. Mirrors
    /// <c>SharedBlock.OffsetTransitionAnnounce*</c>.</summary>
    private const int BlockTransitionAnnounceSerial = 0x38E4;
    private const int BlockTransitionAnnounceModule = 0x38E8;
    private const int BlockTransitionAnnounceWaypoint = 0x3908;
    private const int BlockTransitionAnnounceNameLength = 32;

    /// <summary>Agent -&gt; plugin. Nonzero arms the client-door-refusal watch (shared block
    /// version 47). A LEVEL, re-sent every tick; the plugin has no notion of host/client on
    /// its own.</summary>
    private const int BlockClientDoorRefusalArmed = 0x3928;

    /// <summary>Mid-module movie relay (shared block version 48, region 0x392C..0x396C — the
    /// gap left below the transition-announce region and above the peer-effect one). Plugin
    /// -&gt; agent: the resref <c>MoviePlayHook</c> most recently captured, already excluded
    /// against the transition queue. Agent -&gt; plugin: a peer's mid-module movie to act on.
    /// Mirrors <c>SharedBlock.OffsetMidModule*</c>. See docs/research/cinematics.md's
    /// 2026-08-22 section.</summary>
    private const int BlockMidModuleMovieSerial = 0x392C;
    private const int BlockMidModuleMovieResRef = 0x3930;
    private const int BlockMidModuleMovieNameLength = 16;
    private const int BlockMidModulePlayRequestSerial = 0x394C;
    private const int BlockMidModulePlayRequestResRef = 0x3950;
    private const int BlockMidModulePlayApplied = 0x3960;

    /// <summary>Plugin -&gt; agent. Roadmap item 14 phase 2's load-detection edge — see
    /// <c>SharedBlock.OffsetWorldLoadSerial</c>'s own doc comment (block version 49).</summary>
    private const int BlockWorldLoadSerial = 0x396C;
    private const int BlockMidModulePlayStatus = 0x3964;

    // Staging cheats, mirroring SharedBlock version 41 (region 0x3328..0x33C0).
    private const int BlockCheatSerial = 0x3328;
    private const int BlockCheatKind = 0x332C;
    private const int BlockCheatTarget = 0x3330;
    private const int BlockCheatAmount = 0x3334;
    private const int BlockCheatX = 0x3338;
    private const int BlockCheatY = 0x333C;
    private const int BlockCheatZ = 0x3340;
    private const int BlockCheatText = 0x3344;
    private const int BlockCheatTextLength = 32;
    private const int BlockCheatApplied = 0x3364;
    private const int BlockCheatStatus = 0x3368;
    private const int BlockCheatDetail = 0x336C;
    private const int BlockCheatDetailLength = 64;

    // The four `setitem` identity fields (version 41). Each carries CheatKinds.Unchanged
    // when the request did not name it; the stack size rides on BlockCheatAmount, where 0
    // means the same thing because a stack of nothing is not a value the engine produces.
    private const int BlockCheatCharges = 0x33B0;
    private const int BlockCheatMaxCharges = 0x33B4;
    private const int BlockCheatUpgrades = 0x33B8;
    private const int BlockCheatItemFlags = 0x33BC;

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

    // Container-lock target ids (SharedBlock version 50, region 0x3F08..0x3F1C): a placeable
    // object id parallel to the tag/kind fields above, since InteractionKind.Container keys on
    // the id rather than the tag — see docs/research/container-sync.md. Placed in the free
    // tail after the creature-liveness region (0x3C00..0x3F08, TotalSize 0x4000) rather than
    // widening BlockInteractionLockStride, so every existing consumer of that stride/offset
    // pair is untouched.
    private const int BlockLocalInteractionTargetId = 0x3F08;
    private const int BlockInteractionLockTargetIds = 0x3F0C;
    private const int BlockInteractionLockTargetIdStride = 0x04;

    // Dialogue forced-conversation bit (SharedBlock version 51, region 0x3F1C..0x3F24) — see
    // docs/research/dialogue-sync.md's 2026-08-23 Item 1 and SharedBlock.cs's own remarks.
    private const int BlockLocalDialogueForced = 0x3F1C;
    private const int BlockRemoteDialogueForced = 0x3F20;

    // Container-lock explicit arbitration (SharedBlock version 57, region 0x40D0..0x40E0) —
    // closes the container-loot race, docs/roadmap-seamless.md Tier 1 item 4. See
    // SharedBlock.Dialogue.cs's own region comment for the full "cheat-style request/serial"
    // reasoning — the plugin can only ever ASK this agent for an arbitration decision across
    // the process boundary, never call ICoopSession directly.
    private const int BlockLocalContainerLockRequestTargetId = 0x40D0;
    private const int BlockLocalContainerLockRequestSerial = 0x40D4;
    private const int BlockContainerLockResponseSerial = 0x40D8;
    private const int BlockContainerLockResponseGranted = 0x40DC;

    // Item-template-spawn ring (shared block version 59, region 0x4100..0x4700, TotalSize
    // grown to 0x4800). Mirrors SharedBlock.OffsetItemTemplateSpawn*. See
    // docs/research/item-identity.md §13, docs/research/party-pool-sync.md §20 (version 59's
    // own cheat-flag addition) and KotorGameBridge.ItemTemplateSpawns.cs.
    private const int BlockItemTemplateSpawnRing = 0x4100;
    private const int BlockItemTemplateSpawnRingCapacity = 64;
    private const int BlockItemTemplateSpawnEntryStride = 0x18;
    private const int BlockItemTemplateSpawnIdOffset = 0x00;
    private const int BlockItemTemplateSpawnResrefOffset = 0x04;
    private const int BlockItemTemplateSpawnResrefLength = 16;

    /// <summary>Version 59. Dword flags; bit 0 (<see cref="BlockItemTemplateSpawnIsCheatBit"/>)
    /// means this entry's own creation ran through this instance's own code (a cheat, an
    /// automatic wire-apply), not a vanilla script. Mirrors
    /// <see cref="KotorCoop.Plugin.SharedBlock.ItemTemplateSpawnIsCheatOffset"/>.</summary>
    private const int BlockItemTemplateSpawnIsCheatOffset = 0x14;

    private const uint BlockItemTemplateSpawnIsCheatBit = 1u << 0;

    private const int BlockItemTemplateSpawnCursor =
        BlockItemTemplateSpawnRing + (BlockItemTemplateSpawnRingCapacity * BlockItemTemplateSpawnEntryStride);

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

    /// <summary>Party-roster body identity, mirroring SharedBlock version 53
    /// (0x3F28..0x3F70) — one dword per NPC slot, per direction, 0 = "no live body for that
    /// slot here". See docs/research/dialogue-consequences.md's 2026-08-24 "stale mirrored
    /// body" entry.</summary>
    private const int BlockLocalPartyRosterObjectIds = 0x3F28;

    private const int BlockHostPartyRosterObjectIds = 0x3F4C;

    // Effective behaviour-override flags (shared block version 55, region 0x40C0..0x40C8, no
    // TotalSize growth — the tail after the companion-equipment array at 0x4000..0x40C0 has
    // 0x40 free bytes). Mirrors SharedBlock.OffsetBehaviourOverride*. See
    // docs/research/mod-compatibility.md's "Behaviour-flag negotiation" gap.
    private const int BlockBehaviourOverrideMask = 0x40C0;
    private const int BlockBehaviourOverrideValues = 0x40C4;

    /// <summary>
    /// Pushes this session's effective behaviour-override bits (see
    /// <see cref="BehaviourOverrideBits"/>) into the plugin's shared block. Values written
    /// before mask: a reader that checks the mask first never sees a bit newly set with the
    /// paired value dword still holding whatever the last session left there.
    /// </summary>
    public void SetBehaviourOverrides(uint overrideMask, uint overrideValues)
    {
        if (_pluginBlock == 0)
        {
            return;
        }

        _memory.TryWrite(_pluginBlock + BlockBehaviourOverrideValues, overrideValues);
        _memory.TryWrite(_pluginBlock + BlockBehaviourOverrideMask, overrideMask);
    }

    // Live executable-image digest (shared block region 0x4720..0x4728, no TotalSize growth
    // — it rides the gap between the minigame lock at 0x4704..0x4720 and the chargen
    // handshake at 0x4780). Mirrors SharedBlock.OffsetExeImageDigest*. The plugin hashes the
    // DECRYPTED, UNHOOKED image it is loaded into, which is the only fingerprint that says
    // the same thing about a Steam install and a DRM-free one — the two files differ, the two
    // running images do not. docs/research/steam-support.md §8.
    private const int BlockExeImageDigest = 0x4720;
    private const int BlockExeImageDigestKnown = 0x4724;

    /// <summary>
    /// The plugin's hash of the loaded executable image, or false if it has not published one
    /// (no plugin, an older plugin, or a read that failed inside the game). False means the
    /// executable check downgrades to a warning — never to a false match.
    /// </summary>
    public bool TryReadExeImageDigest(out uint digest)
    {
        digest = 0;

        // Known is read FIRST and the digest second, the reverse of the plugin's write order,
        // so a torn read can only ever produce "not known yet" — never a stale hash presented
        // as current.
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockExeImageDigestKnown, out uint known)
            && known != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockExeImageDigest, out digest);
    }

    // Companion damage TAKEN (shared block version 63, region 0x4A00..0x4A90). The companion
    // sibling of the enemy damage ledger above: a companion companion_local_combat has
    // released takes real hits on the CLIENT's own engine, and without this channel the host's
    // authoritative body never saw any of it — see docs/research/companion-state-sync.md §13.
    // Client -> agent: what the plugin's watch (PhantomHitHook's companion slots) has
    // measured, keyed by presence slot (SharedBlock.PartyPresenceCapacity, 4). Agent -> agent's
    // own host plugin: the total every peer has dealt to each of the host's own companions,
    // keyed by NPC ROSTER SLOT (ProtocolConstants.PartyRosterSlots, 9) rather than presence
    // slot, since that is the identity that survives a module transition and is what
    // PartyPresenceController.ResolveRosterObjectId resolves against.
    private const int BlockCompanionDamageCount = 0x4A00;
    private const int BlockCompanionDamageGeneration = 0x4A04;
    private const int BlockCompanionDamageSlots = 0x4A08;
    private const int BlockCompanionDamageSlotStride = 0x0C;
    private const int BlockCompanionDamageSlotCapacity = 4;
    private const int BlockCompanionDamageOwedCount = 0x4A40;
    private const int BlockCompanionDamageOwedSlots = 0x4A44;
    private const int BlockCompanionDamageOwedStride = 0x08;
    private const int BlockCompanionDamageOwedCapacity = 9;
    private const int BlockCompanionDamageApplied = 0x4A8C;

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
}
