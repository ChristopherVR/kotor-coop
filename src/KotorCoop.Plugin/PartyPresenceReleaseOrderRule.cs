namespace KotorCoop.Plugin;

/// <summary>
/// The fixed sequence <see cref="PartyPresenceController.ReleaseSession"/> executes, pulled out
/// as pure data so the ORDER itself is testable without the unsafe engine calls each step makes.
///
/// ROUND 101 (docs/research/party-reconciliation.md): round 100's live A/B narrowed a silent
/// no-op to inside <see cref="Odyssey.TryReleaseFollowPhaseOnUnmirror"/> itself (root-caused and
/// fixed that round — see that method's own remarks), but the FIRST thing worth pinning down
/// before chasing that was whether the call even happens while it still has something to act on.
/// <see cref="ReleaseFollowPhase"/> reads <c>PartyPresenceController._trackedId</c>, and
/// <see cref="Invalidate"/> is the ONLY step that clears it (<c>Forget</c>, called from
/// <c>Invalidate</c>'s own loop) — so <see cref="ReleaseFollowPhase"/> is meaningless unless it
/// runs strictly before <see cref="Invalidate"/>. <see cref="ReleaseFaction"/> (<c>ReleaseAllHeld</c>)
/// reads the same array too, so it needs the same guarantee, and its own doc comment already
/// establishes it never clears the cache itself (unlike <c>Invalidate</c>) — this rule is the
/// single place that guarantee is asserted for BOTH readers at once, and tested.
/// </summary>
internal enum PartyPresenceReleaseStep
{
    /// <summary><c>ReleaseAllHeld()</c> — puts every held companion back in the player faction,
    /// off the min-1-hp pin. Reads <c>_trackedId</c>; never clears it.</summary>
    ReleaseFaction,

    /// <summary><c>ReleaseFollowPhaseForTracked()</c> — resets a mirrored companion's native
    /// follow-FSM record (round 98). Reads <c>_trackedId</c>; never clears it. Only present when
    /// <c>party_follow_phase7_release=1</c>.</summary>
    ReleaseFollowPhase,

    /// <summary><c>Invalidate()</c> — the ONLY step that clears <c>_trackedId</c> (via
    /// <c>Forget</c>). Must run last: every earlier step depends on the cache it tears down.</summary>
    Invalidate,
}

internal static class PartyPresenceReleaseOrderRule
{
    /// <summary>
    /// The exact steps <see cref="PartyPresenceController.ReleaseSession"/> runs, in order, for
    /// the given kill-switch state. <see cref="PartyPresenceReleaseStep.Invalidate"/> is always
    /// last; <see cref="PartyPresenceReleaseStep.ReleaseFollowPhase"/> is included, before it,
    /// only when <paramref name="phase7ReleaseEnabled"/> is true — matching
    /// <c>party_follow_phase7_release</c>'s own default-OFF kill switch exactly.
    /// </summary>
    public static IReadOnlyList<PartyPresenceReleaseStep> Steps(bool phase7ReleaseEnabled) =>
        phase7ReleaseEnabled
            ?
            [
                PartyPresenceReleaseStep.ReleaseFaction,
                PartyPresenceReleaseStep.ReleaseFollowPhase,
                PartyPresenceReleaseStep.Invalidate,
            ]
            :
            [
                PartyPresenceReleaseStep.ReleaseFaction,
                PartyPresenceReleaseStep.Invalidate,
            ];
}
