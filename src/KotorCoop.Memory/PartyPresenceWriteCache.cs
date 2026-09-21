using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// The STATEFUL half of <see cref="KotorGameBridge.TryWritePartyMembers(System.Collections.Generic.IReadOnlyList{PartyMemberState}, bool)"/>'s
/// per-slot dirty check — what <see cref="PartyPresenceDirtyCheck"/> (the pure half) last decided
/// actually reached the shared block, for one <see cref="KotorGameBridge"/> instance's whole life.
///
/// Pulled out of <c>KotorGameBridge.Party.cs</c> (round 103, docs/research/party-reconciliation.md)
/// so the reset this round adds is directly unit-testable without a real game process, and so the
/// bookkeeping itself is exercised by the SAME <see cref="PartyPresenceDirtyCheck.CanSkipWrite"/>
/// every write already goes through — nothing here re-implements that decision. A failed
/// publication also marks the slot dirty independently of its last successful value, so a
/// later snapshot that returns to that value cannot skip an outstanding retry.
///
/// ⚠ WHY A RESET WAS MISSING. <c>SessionManager</c> keeps ONE bridge alive across every
/// disconnect and reconnect a player does (a fresh <c>CoopAgent</c>, the SAME bridge instance —
/// see <c>SessionManager</c>'s own class comment), so a cache that only ever cleared on a MODULE
/// change survived a reconnect completely untouched. <c>KotorGameBridge.ClearCoopState()</c>'s own
/// raw memory write zeroes the block's <c>PartyPresenceCount</c> LEVEL directly on every
/// disconnect (<c>SessionScopedBlockLevels</c>) — bypassing this cache entirely — so without a
/// matching <see cref="Reset"/> called from the same place, a reconnect whose first
/// <c>PartySnapshot</c> happens to read byte-identical to whatever was mirrored right before the
/// disconnect (the ordinary case: nothing moved while nobody was mirroring it) finds every slot
/// AND the block's own count answered "skip, nothing changed here" — so the level
/// <c>ClearCoopState</c> just zeroed is never rewritten, and the client's
/// <c>PartyPresenceController.OnFrame</c> never sees <c>PartyPresenceCount</c> rise above 0 again,
/// with no error and no log line to say why. This is the root cause behind round 102's rig finding
/// ("reconnect does not reliably re-establish companion mirroring").
/// </summary>
public sealed class PartyPresenceWriteCache(int capacity)
{
    private readonly PartyMemberState?[] _lastWritten = new PartyMemberState?[capacity];
    private readonly bool[] _dirty = new bool[capacity];
    private uint? _lastWrittenCount;
    private string _module = string.Empty;

    /// <summary>
    /// Tells the cache what module this pass is writing under. Returns true — an unconditional
    /// override for <see cref="CanSkipSlot"/>/<see cref="CanSkipCount"/>, and clears every cached
    /// entry outright — the first time this is called after construction or <see cref="Reset"/>
    /// (an empty module never matches a real one) and every time the module reading actually
    /// changes. The same "always force a fresh write across a boundary" rule
    /// <c>KotorGameBridge</c>'s own door/placeable/loot/encounter module caches already use:
    /// <c>PluginHost.Invalidate()</c> can clear the block's party-presence region independent of
    /// what this agent last wrote, so a same-content comparison must not be trusted across one.
    /// </summary>
    public bool NoteModule(string module)
    {
        bool changed = !string.Equals(module, _module, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            _module = module;
            Array.Clear(_lastWritten);
            Array.Clear(_dirty);
            _lastWrittenCount = null;
        }

        return changed;
    }

    /// <summary>Whether slot <paramref name="slot"/>'s write can be skipped this pass — delegates
    /// to <see cref="PartyPresenceDirtyCheck.CanSkipWrite"/>, the pure decision, against what THIS
    /// cache last recorded for that slot.</summary>
    public bool CanSkipSlot(int slot, PartyMemberState? candidate, bool moduleChanged) =>
        !_dirty[slot]
        && PartyPresenceDirtyCheck.CanSkipWrite(candidate, _lastWritten[slot], moduleChanged);

    /// <summary>Records what was actually written to <paramref name="slot"/> this pass — call
    /// only when the write itself happened, exactly as <c>TryWritePartyMembers</c> always has.</summary>
    public void RecordSlot(int slot, PartyMemberState? candidate)
    {
        _lastWritten[slot] = candidate;
        _dirty[slot] = false;
    }

    /// <summary>Marks a slot as requiring another publication after any failed memory write.
    /// This remains true even when the next candidate happens to equal the last successful
    /// candidate; an odd sequence marker or a partially written payload must never be skipped.</summary>
    public void InvalidateSlot(int slot) => _dirty[slot] = true;

    /// <summary>Whether the block's own <c>PartyPresenceCount</c> level can be left alone this
    /// pass — true only when the module has not changed AND the occupied-slot count this pass
    /// computed is exactly what was last recorded as written.</summary>
    public bool CanSkipCount(uint written, bool moduleChanged) =>
        !moduleChanged && _lastWrittenCount == written;

    public void RecordCount(uint written) => _lastWrittenCount = written;

    /// <summary>
    /// Forgets every cache entry, forcing the very next call's writes to land unconditionally —
    /// the party-presence dirty-check's equivalent of <c>ClearCoopState</c>'s own raw block zero.
    /// Called from <c>KotorGameBridge.ClearCoopState()</c> so a disconnect (and the reconnect that
    /// follows it, on the SAME bridge instance) can never leave this cache holding content that
    /// predates a session the block itself has already forgotten.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_lastWritten);
        Array.Clear(_dirty);
        _lastWrittenCount = null;
        _module = string.Empty;
    }
}
