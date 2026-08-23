namespace KotorCoop.Plugin;

/// <summary>
/// ROUND 87 (docs/research/party-reconciliation.md, closing round 85's "not fixed this round"
/// warp crash). Guards <see cref="PartyFollowController"/>'s per-frame engine-call chain
/// (<c>Odyssey.PumpPartyUpdate</c>/<c>RecalculateFollowPoint</c>/<c>TryDriveFollowTarget</c>/
/// <c>TryResetStuckFollowPhase</c>/<c>TryDriveFollowLeaderStep</c>/
/// <c>TryAdvanceStalledPathDestination</c>, and the diagnostics that piggyback on the same reads
/// — this is the shipped-default per-frame follow diagnostic/drive path, not an opt-in) against
/// the warp-transition access violation round 85 found and worked around but never fixed: an AV
/// at <c>0x4AA984</c>, confirmed by <c>tools/disasm.py 0x4aa980 32</c> to be the SECOND
/// instruction (<c>fld dword ptr [ecx]</c>) of a small vector "close enough" comparison helper —
/// it dereferences the pointer <c>mov ecx, [esp+4]</c> loads one instruction earlier, at
/// <c>0x4AA980</c>, which sits inside <c>swkotor.exe</c>'s own <c>.text</c> (called from
/// <c>Moving_To_Path</c> 0x00511B60 at call site 0x511D5A, itself round 84's own already-
/// disassembled "has the creature gotten close enough to the next path node" gate) — this is an
/// ENGINE-side crash, not plugin code, reached only through this controller's own per-frame call
/// into <c>TryDriveFollowLeaderStep</c>.
///
/// Round 85's own log evidence pins WHEN, not just WHERE: the crash landed one frame after
/// <c>Odyssey.TryGetWorldEpoch</c> first reported the NEW module's epoch as valid again —
/// PluginHost's own epoch gate (the one thing that already protects every other per-frame
/// controller from a mid-teardown world, see PluginHost.OnFrame's own "epoch gate runs before
/// anything else" remark) had already passed by the time the crash happened. So the stale read is
/// not "no world yet" (that case is already refused — PluginHost never calls this controller at
/// all while <c>TryGetWorldEpoch</c> returns false); it is "a world that LOOKS valid (a fresh
/// object-array pointer, a resolved module name — the two halves the epoch hashes together) but
/// whose party table / per-companion follow-data records for the NEW module are not necessarily
/// populated yet." This is exactly the same class of premature-epoch race
/// <c>PluginHost.InvalidateAll("module changed")</c> already treats as a hard reset point for
/// every OTHER controller's cached state — this controller had never applied that same discipline
/// to itself (confirmed: before this round, nothing in <c>PartyFollowController</c> read the
/// epoch at all).
///
/// The fix mirrors <c>DialogueSpectateController.OnFrame</c>'s own established self-invalidation
/// shape exactly (<c>Odyssey.TryGetWorldEpoch</c>, compare against a private
/// last-seen-epoch field) with one addition this controller specifically needs: skip driving
/// ANYTHING — not just invalidate cached state — on the single frame a new epoch is first
/// observed, because round 85's own evidence is that this exact frame is where the crash lands.
/// Every frame after that, including the very next one, runs exactly as it did before this round
/// — this does not throttle, gate, or disable the diagnostics during ordinary play, only defer
/// them (and every other per-frame write this controller makes) by exactly one frame across a
/// module change.
/// </summary>
internal static class PartyFollowTransitionRule
{
    /// <summary>
    /// True when <see cref="PartyFollowController.OnFrame"/> should skip all per-frame
    /// follow-drive work this frame (and only update its own bookkeeping) — either because the
    /// world is not readable at all this frame (PluginHost's own gate should already prevent
    /// this call from happening in that case, but the check here costs nothing, matches
    /// PluginHost's own semantics exactly, and is not something this controller should assume it
    /// can only ever be handed a readable world by construction), or because <paramref
    /// name="currentEpoch"/> is not the same epoch this controller last drove follow logic
    /// against — the exact one-frame race round 85 traced this crash to.
    /// </summary>
    /// <param name="worldEpochReadable">Whatever <c>Odyssey.TryGetWorldEpoch</c> returned this
    /// frame.</param>
    /// <param name="currentEpoch">The epoch <c>TryGetWorldEpoch</c> reported this frame (only
    /// meaningful when <paramref name="worldEpochReadable"/> is true).</param>
    /// <param name="lastSeenEpoch">The last epoch this controller itself has driven follow logic
    /// against — deliberately this controller's OWN field, not a shared one PluginHost also
    /// writes, so "did I already resume driving this epoch" cannot be answered by a flag some
    /// other controller reset for an unrelated reason.</param>
    public static bool ShouldSkipThisFrame(bool worldEpochReadable, ulong currentEpoch, ulong lastSeenEpoch) =>
        !worldEpochReadable || currentEpoch != lastSeenEpoch;
}
