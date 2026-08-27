// Party roster, companions, progression (gold/experience) and portraits: the host owns the
// companions and just publishes where they are; a client freezes its own copies and mirrors
// the host's positions.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
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

        // 2026-08-24 (docs/research/dialogue-consequences.md, "stale mirrored body"): one
        // object id per slot, alongside the masks — see PartyRoster.ObjectIds' own doc comment
        // for what a peer does with this. A read that fails partway leaves 0s for the rest
        // (the same "0 = no id" convention the field already carries for an unpopulated slot),
        // never half a stale value.
        uint[] objectIds = new uint[ProtocolConstants.PartyRosterSlots];
        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            _memory.TryReadUInt32(_pluginBlock + BlockLocalPartyRosterObjectIds + (slot * 4), out objectIds[slot]);
        }

        roster = new PartyRoster(
            (ushort)((packed >> BlockPartyRosterAvailableShift) & BlockPartyRosterSlotMask),
            (ushort)((packed >> BlockPartyRosterPartyShift) & BlockPartyRosterSlotMask),
            objectIds);
        return true;
    }

    /// <summary>
    /// Hands the plugin the host's roster to reconcile its own against, or clears it.
    ///
    /// One dword, written whole — there is no ordering rule to get wrong here because there is
    /// only one store. Clearing (<paramref name="valid"/> false) writes a plain 0, so the
    /// plugin's "is anybody telling me what the roster should be" test is the same test in both
    /// directions and a disconnected agent stops the reconciliation by doing nothing special.
    ///
    /// The per-slot object ids (2026-08-24, docs/research/dialogue-consequences.md) follow the
    /// same rule as the mask: written every call when <paramref name="valid"/>, zeroed when not
    /// — <c>PartyRosterController.Reconcile</c> only ever reads them alongside the mask it
    /// already checked, so there is no separate "these ids are stale" state to invent.
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

        bool ok = _memory.TryWrite(_pluginBlock + BlockHostPartyRoster, packed);

        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            uint objectId = valid ? roster.ObjectIdFor(slot) : 0;
            ok &= _memory.TryWrite(_pluginBlock + BlockHostPartyRosterObjectIds + (slot * 4), objectId);
        }

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

    // ---- party-presence dirty check (docs/research/lag-audit.md, party_fast_pump entry) ----
    //
    // TryWritePartyMembers's own shared-block writes, per slot: what was actually written the
    // last time a write reached the block (null = nothing yet, or a module change reset it), and
    // the module name that write happened under. See PartyPresenceDirtyCheck's own doc comment
    // for why a module change is an unconditional override rather than folded into the content
    // comparison.
    private readonly PartyMemberState?[] _lastWrittenPartyPresence = new PartyMemberState?[BlockPartyPresenceCapacity];
    private uint? _lastWrittenPartyPresenceCount;
    private string _partyPresenceModule = string.Empty;

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

    // ---- companion experience (2026-08-27, docs/research/companion-state-sync.md) ---------
    //
    // Same mechanism as the PC's own experience above (a plain stats+0x068 field), applied per
    // active NPC roster slot instead of just the local player. Host-authoritative, max-merged
    // one layer up in Pumps.PartyPump.PumpCompanionExperience — see that method's doc comment
    // for why a delta would double-count (both engines already grant kill XP to every ACTIVE
    // party member independently through CSWPartyTable::DistributeExperience).

    public bool SupportsCompanionProgressionSync => _player is not null;

    /// <summary>
    /// Reads the experience of every currently-active (spawned) party companion, keyed by NPC
    /// roster slot (0..8). Only a slot with a live body is reported present — a
    /// recruited-but-benched companion has no stats block to read.
    /// </summary>
    public bool TryReadCompanionExperience(uint[] xpBySlot, out ushort presentMask)
    {
        Array.Clear(xpBySlot, 0, Math.Min(xpBySlot.Length, PartyNpcSlots));
        presentMask = 0;

        if (!TryGetPartyTable(out nint partyTable)
            || !_memory.TryReadInt32(partyTable + PartyCount, out int count)
            || count is <= 0 or > PartyMaxCompanions)
        {
            return false;
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
                || slot >= xpBySlot.Length)
            {
                continue;
            }

            if (!_memory.TryReadUInt32(partyTable + PartyObjectIds + (slot * 4), out uint objectId)
                || objectId == 0x7F000000
                || !byId.TryGetValue(objectId, out nint creature)
                || !_memory.TryReadPointer(creature + CreatureStatsPointer, out nint stats)
                || stats == 0
                || !_memory.TryReadUInt32(stats + StatsExperience, out uint xp))
            {
                continue;
            }

            xpBySlot[slot] = xp;
            presentMask |= (ushort)(1 << slot);
        }

        // A read that found the table but nobody active is still a genuine "nothing to
        // report" rather than a failure — an empty party (Endar Spire before Trask joins) is
        // real, not an error.
        return true;
    }

    /// <summary>
    /// Overwrites each present slot's companion experience with the host's absolute value. A
    /// slot the mask does not name is left untouched — it is either not this instance's
    /// business (the host never reported it) or has no live body here to write into.
    /// </summary>
    public bool TryWriteCompanionExperience(IReadOnlyList<uint> xpBySlot, ushort presentMask)
    {
        if (presentMask == 0 || !TryGetPartyTable(out nint partyTable))
        {
            return false;
        }

        Dictionary<uint, nint>? byId = null;
        bool anyWritten = false;

        for (int slot = 0; slot < PartyNpcSlots && slot < xpBySlot.Count; slot++)
        {
            if ((presentMask & (1 << slot)) == 0)
            {
                continue;
            }

            if (!_memory.TryReadUInt32(partyTable + PartyObjectIds + (slot * 4), out uint objectId)
                || objectId == 0x7F000000)
            {
                continue;
            }

            byId ??= BuildCreatureIdMap();

            if (!byId.TryGetValue(objectId, out nint creature)
                || !_memory.TryReadPointer(creature + CreatureStatsPointer, out nint stats)
                || stats == 0)
            {
                continue;
            }

            anyWritten |= _memory.TryWrite(stats + StatsExperience, xpBySlot[slot]);
        }

        return anyWritten;
    }

    private Dictionary<uint, nint> BuildCreatureIdMap()
    {
        Dictionary<uint, nint> byId = [];
        foreach (CreatureRef c in EnumerateCreatures())
        {
            byId[c.ObjectId] = c.Address;
        }

        return byId;
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

    public List<PartyMemberState> ReadPartyMembers(bool includeEquipment = false)
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

            // Companion gear (2026-08-27, docs/research/companion-state-sync.md): the exact
            // same per-slot resref read the PC's own equipment channel already uses, applied
            // to a companion body instead of the local player. Skipped when the caller has not
            // asked for it (companion_gear_sync off) so PumpParty's every-tick call does not
            // pay three extra lookups nobody will use.
            string body = string.Empty, rightWeapon = string.Empty, leftWeapon = string.Empty;
            if (includeEquipment)
            {
                body = ReadEquippedResref(creature, InvSlotBody);
                rightWeapon = ReadEquippedResref(creature, InvSlotRightWeapon);
                leftWeapon = ReadEquippedResref(creature, InvSlotLeftWeapon);
            }

            members.Add(new PartyMemberState(
                (byte)slot, position, facing, animation, health, oneShotSerial, oneShotAnim,
                body, rightWeapon, leftWeapon));
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

    // Companion equipment (block version 54): the host's per-companion gear, keyed by the
    // SAME presence slot index as BlockPartyPresenceSlots — 4 slots x 3 resrefs (body, right
    // weapon, left weapon) x 16 ascii bytes. See docs/research/companion-state-sync.md.
    private const int BlockCompanionEquipSlots = 0x4000;
    private const int BlockCompanionEquipStride = 0x30;
    private const int BlockCompanionEquipResrefLength = 16;

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
    public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members) =>
        TryWritePartyMembers(members, applyEquipment: false);

    /// <summary>
    /// Same as <see cref="TryWritePartyMembers(IReadOnlyList{PartyMemberState})"/>, and —
    /// when <paramref name="applyEquipment"/> is true — also hands each companion's desired
    /// gear (<see cref="PartyMemberState.EquippedBody"/> and its two siblings) to the plugin's
    /// companion-equipment region, so <c>PartyPresenceController</c> can dress the mirrored
    /// body. See docs/research/companion-state-sync.md.
    /// </summary>
    public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members, bool applyEquipment)
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
            // A module change forces every slot below to write unconditionally, even if a
            // companion's reported state looks byte-identical to what this agent last sent —
            // PluginHost's own Invalidate() clears the block's party-presence slots in-process on
            // a load, independent of this agent's own cache. See PartyPresenceDirtyCheck's doc
            // comment.
            bool moduleChanged = !TryReadCurrentModule(out string module)
                || !string.Equals(module, _partyPresenceModule, StringComparison.OrdinalIgnoreCase);
            if (moduleChanged)
            {
                _partyPresenceModule = module;
                Array.Clear(_lastWrittenPartyPresence);
                _lastWrittenPartyPresenceCount = null;
            }

            int written = 0;
            for (int i = 0; i < BlockPartyPresenceCapacity; i++)
            {
                PartyMemberState? candidate = i < members.Count && members[i].SlotIndex < PartyNpcSlots
                    ? members[i]
                    : null;

                if (candidate.HasValue)
                {
                    written++;
                }

                if (PartyPresenceDirtyCheck.CanSkipWrite(candidate, _lastWrittenPartyPresence[i], moduleChanged))
                {
                    continue;
                }

                nint slot = _pluginBlock + BlockPartyPresenceSlots + (i * BlockPartyPresenceStride);
                if (candidate is { } m)
                {
                    _memory.TryWrite(slot + 0x04, m.Position.X);
                    _memory.TryWrite(slot + 0x08, m.Position.Y);
                    _memory.TryWrite(slot + 0x0C, m.Position.Z);
                    _memory.TryWrite(slot + 0x10, m.Facing);
                    _memory.TryWrite(slot + 0x14, m.Animation);
                    _memory.TryWrite(slot + 0x18, m.Health);
                    _memory.TryWrite(slot + 0x1C, m.OneShotSerial);
                    _memory.TryWrite(slot + 0x20, (uint)m.OneShotAnimation);
                    _memory.TryWrite(slot + 0x00, (uint)m.SlotIndex + 1);
                }
                else
                {
                    _memory.TryWrite(slot + 0x00, 0u);
                }

                _lastWrittenPartyPresence[i] = candidate;

                // Companion gear (2026-08-27, docs/research/companion-state-sync.md): written
                // every pass this instance opted into applying it, independent of the position
                // dirty-check above — an unchanged position with a freshly re-equipped
                // companion must still update the block. Not gated on the module-changed reset
                // that region uses: empty strings are always a safe "leave it alone" and there
                // is no stale-across-transition hazard a companion's gear can create the way a
                // stale door/placeable id can.
                if (applyEquipment)
                {
                    nint equipSlot = _pluginBlock + BlockCompanionEquipSlots + (i * BlockCompanionEquipStride);
                    string body = candidate?.EquippedBody ?? string.Empty;
                    string rightWeapon = candidate?.EquippedRightWeapon ?? string.Empty;
                    string leftWeapon = candidate?.EquippedLeftWeapon ?? string.Empty;

                    _memory.TryWriteAsciiString(equipSlot, body, BlockCompanionEquipResrefLength);
                    _memory.TryWriteAsciiString(
                        equipSlot + BlockCompanionEquipResrefLength, rightWeapon, BlockCompanionEquipResrefLength);
                    _memory.TryWriteAsciiString(
                        equipSlot + (2 * BlockCompanionEquipResrefLength), leftWeapon, BlockCompanionEquipResrefLength);
                }
            }

            if (moduleChanged || _lastWrittenPartyPresenceCount != (uint)written)
            {
                _memory.TryWrite(_pluginBlock + BlockPartyPresenceCount, (uint)written);
                _lastWrittenPartyPresenceCount = (uint)written;
            }

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

}
