using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// The pure half of <see cref="KotorGameBridge.TryWritePartyMembers"/>'s per-slot dirty check —
/// separated out so it can be unit-tested without a real process, the same split
/// <c>CompanionLocalFightRule.Decide</c> uses for its own engine-adjacent decision.
///
/// docs/research/lag-audit.md's <c>party_fast_pump</c> entry (default ON) moved
/// <c>TryWritePartyMembers</c> off the ~150 ms combat-pump gate onto every poll tick (as fast as
/// ~16 ms at a 60 Hz tick rate) — a real, measured win for companion-position latency. But the
/// call itself always re-wrote every occupied slot's full field set (position, facing,
/// animation, health, one-shot) unconditionally, even when nothing about that slot had changed
/// since the write that already reached the block. round 71's own "53% worse combat frame time"
/// measurement (docs/research/lag-audit.md, 2026-08-23) ran against a companion whose follow-AI
/// could not move at all at the time (fixed since, rounds 80/84/88) — meaning that entire
/// session's worth of party writes were re-sending byte-identical data roughly 9x more often
/// than the old 150 ms cadence did, purely wasted cross-process WriteProcessMemory calls, for a
/// companion that was not actually moving. This is exactly the "redundant work" a heavier fight
/// (more creatures for the SAME agent thread's other combat-adjacent scans to chew through, per
/// lag-audit.md's own per-frame cost table) has the least spare budget to absorb.
///
/// A module transition is the one case a same-content comparison must not trust on its own:
/// <c>PluginHost</c>'s own <c>Invalidate()</c> clears the shared block's party-presence slots
/// in-process (a real, in-game event, not something this agent's own write path controls) on
/// every module unload/epoch change, independent of whether this agent's own idea of "the last
/// state I wrote" changed at all. Comparing content alone would leave the block silently empty
/// after a load whenever a companion's reported position happened not to change across the
/// transition — exactly the common case, not a rare one. <see cref="CanSkipWrite"/> therefore
/// takes the caller's own module-changed edge as an unconditional override, the same "always
/// force a fresh write across a boundary" discipline <c>KotorGameBridge</c>'s own
/// <c>_doorModule</c>/<c>_placeableModule</c>/<c>_lootModule</c>/<c>_encounterModule</c> caches
/// already use for the identical hazard.
/// </summary>
internal static class PartyPresenceDirtyCheck
{
    /// <summary>
    /// Whether one party-presence slot's write can be skipped this pass.
    /// </summary>
    /// <param name="candidate">What would be written this pass — <c>null</c> means "this slot has
    /// no member and should read empty."</param>
    /// <param name="lastWritten">What was written to this slot the last time a write actually
    /// happened — <c>null</c> before the first write, or after a module change forced a
    /// reset.</param>
    /// <param name="moduleChanged">The local module read differently than it did on the last call
    /// (or could not be read at all) — an unconditional override, since the shared block may have
    /// been cleared by something this agent did not write.</param>
    public static bool CanSkipWrite(PartyMemberState? candidate, PartyMemberState? lastWritten, bool moduleChanged) =>
        !moduleChanged && candidate == lastWritten;
}
