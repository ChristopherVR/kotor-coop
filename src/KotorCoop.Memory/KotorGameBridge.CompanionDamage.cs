// Companion damage TAKEN by a companion companion_local_combat has released to fight through
// its own engine AI on a client, forwarded to the host so its authoritative body actually
// takes it. The companion sibling of KotorGameBridge.Enemies.cs's damage-DEALT ledger — read
// that file's header comment first. See docs/research/companion-state-sync.md §13.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    private const int CompanionTakenMetadata = 0xBE00, CompanionOwedMetadata = 0xBE40;
    public bool SupportsCompanionDamageSync => _pluginBlock != 0;

    /// <summary>
    /// Client side: cumulative damage this instance's own engine has actually applied to each
    /// companion the plugin currently has released, straight out of
    /// <c>PhantomHitHook</c>'s companion watch. Keyed by presence slot on the wire in the
    /// block (<see cref="BlockCompanionDamageSlotCapacity"/>, 4), but published here already
    /// translated into the NPC ROSTER slot the block itself carries per entry — the identity
    /// that survives a module transition and is what the host applies against.
    /// </summary>
    public (uint Generation, List<CompanionDamageEntry> Damage) ReadCompanionDamageTaken()
    {
        List<CompanionDamageEntry> damage = [];

        if (_pluginBlock == 0)
        {
            return (0, damage);
        }

        if (!_memory.TryReadUInt32(_pluginBlock + CompanionTakenMetadata, out uint sequence) || sequence == 0 || (sequence & 1) != 0
            || !_memory.TryReadUInt32(_pluginBlock + CompanionTakenMetadata + 4, out uint epoch) || epoch == 0
            || !_memory.TryReadUInt32(_pluginBlock + CompanionTakenMetadata + 8, out uint serial)
            || !_memory.TryReadUInt32(_pluginBlock + BlockCompanionDamageGeneration, out uint generation)) return (0, []);
        _memory.TryReadUInt32(_pluginBlock + BlockCompanionDamageCount, out uint count);

        for (int i = 0; i < Math.Min(count, BlockCompanionDamageSlotCapacity); i++)
        {
            nint slot = _pluginBlock + BlockCompanionDamageSlots + (i * BlockCompanionDamageSlotStride);

            if (!_memory.TryReadUInt32(slot + 0x00, out uint rosterSlotPlusOne) || rosterSlotPlusOne == 0)
            {
                continue;
            }

            if (_memory.TryReadUInt32(slot + 0x04, out uint cumulative) && cumulative > 0)
            {
                if (!_memory.TryReadUInt32(_pluginBlock + CompanionTakenMetadata + 20 + i * 4, out uint occupant)) return (0, []);
                damage.Add(new CompanionDamageEntry(rosterSlotPlusOne, cumulative, occupant));
            }
        }

        if (!_memory.TryReadUInt32(_pluginBlock + CompanionTakenMetadata, out uint after) || after != sequence
            || !TryReadLivenessEpoch(out uint currentEpoch) || currentEpoch != epoch
            || !TryReadWorldLoadSerial(out uint currentSerial) || currentSerial != serial) return (0, []);
        return (generation, damage);
    }

    /// <summary>
    /// Host side: hands the plugin the total every peer has dealt to each of this host's own
    /// companions, keyed by NPC roster slot, for it to spend as real engine damage on the game
    /// thread through <c>CompanionDamageController.ApplyOwed</c>.
    ///
    /// ⚠ The count is zeroed FIRST and restored LAST, so the plugin either sees the whole
    /// table or none of it — the same discipline <see cref="TryPublishDamageOwed"/> uses and
    /// for the identical reason: a torn row here would spend damage against the wrong
    /// companion, not merely cost a frame of a stale position.
    /// </summary>
    public bool TryPublishCompanionDamageOwed(IReadOnlyList<CompanionDamageEntry> owed)
        => TryReadWorldLoadSerial(out uint serial) && TryReadLivenessEpoch(out uint epoch)
            && TryPublishCompanionDamageOwed(owed, serial, epoch);

    public bool TryPublishCompanionDamageOwed(IReadOnlyList<CompanionDamageEntry> owed, uint capturedSerial, uint capturedEpoch)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        if (capturedEpoch == 0 || !TryReadLivenessEpoch(out uint epoch) || epoch != capturedEpoch
            || !TryReadWorldLoadSerial(out uint serial) || serial != capturedSerial
            || !_memory.TryReadUInt32(_pluginBlock + CompanionOwedMetadata, out uint sequence)
            || !_memory.TryWrite(_pluginBlock + CompanionOwedMetadata, (sequence + 1) | 1u)) return false;
        bool ok = _memory.TryWrite(_pluginBlock + CompanionOwedMetadata + 4, capturedEpoch);
        ok &= _memory.TryWrite(_pluginBlock + CompanionOwedMetadata + 8, capturedSerial);
        ok &= _memory.TryWrite(_pluginBlock + BlockCompanionDamageOwedCount, 0u);
        int count = Math.Min(owed.Count, BlockCompanionDamageOwedCapacity);

        for (int i = 0; i < count; i++)
        {
            nint slot = _pluginBlock + BlockCompanionDamageOwedSlots + (i * BlockCompanionDamageOwedStride);
            ok &= _memory.TryWrite(_pluginBlock + CompanionOwedMetadata + 16 + i * 4, owed[i].OccupantGeneration);
            ok &= _memory.TryWrite(slot + 0x04, owed[i].CumulativeDamage);
            ok &= _memory.TryWrite(slot + 0x00, owed[i].RosterSlotPlusOne);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockCompanionDamageOwedCount, (uint)count);
        return ok && TryReadLivenessEpoch(out uint afterEpoch) && afterEpoch == capturedEpoch
            && TryReadWorldLoadSerial(out uint afterSerial) && afterSerial == capturedSerial
            && _memory.TryWrite(_pluginBlock + CompanionOwedMetadata, (sequence + 2) & ~1u);
    }

    /// <summary>How many companion-damage applications the plugin has actually run —
    /// diagnostic, the companion sibling of <c>ReadDamageDealtDropped</c>'s "is anything
    /// arriving at all" question.</summary>
    public uint ReadCompanionDamageApplied()
    {
        if (_pluginBlock == 0)
        {
            return 0;
        }

        _memory.TryReadUInt32(_pluginBlock + BlockCompanionDamageApplied, out uint applied);
        return applied;
    }
}
