namespace KotorCoop.Protocol;

/// <summary>
/// The decision half of solo-segment spectating, separated from <see cref="SoloSegmentMachine"/>
/// (which only decides whether THIS instance is the owner of an active segment) so both halves
/// can be unit-tested independently — the same split <c>MinigameRule</c> uses for its own three
/// independent questions.
///
/// Two questions, answered from two different flags:
///   * the OWNER's own local flag decides whether IT should hide every remote peer from what it
///     writes locally, so the duel script never sees a second body;
///   * a remote PEER's published flag (while standing in the same module) decides whether THIS
///     instance should enter spectate mode for them.
///
/// Deliberately NOT symmetric with a peer's own flag the way <c>MinigameRule.ShouldHidePeer</c>
/// is: a minigame/away peer is hidden by EVERYONE, itself included, because it genuinely is not
/// standing in the shared world. A solo-segment OWNER is very much still standing there —
/// fighting Malak — and must stay visible to the peer who is watching them, so
/// <see cref="ShouldSpectate"/> is never used to hide the flag-bearer, only to decide whether the
/// OBSERVER should react. See docs/research/star-forge-finale.md's product decision.
/// </summary>
public readonly record struct SoloSegmentRule(bool Enabled)
{
    /// <summary>
    /// Should THIS instance (the one fighting the solo segment) hide EVERY remote peer's
    /// phantom from what it writes locally? True exactly while the LOCAL player's own
    /// <see cref="PlayerState.FlagSoloSegment"/> is set — not gated on any peer's flag, because
    /// the reason to hide is "I am mid-duel", which the owner already knows about itself without
    /// needing to hear it back from anyone.
    /// </summary>
    public bool ShouldHideAllPeers(bool localInSoloSegment) => Enabled && localInSoloSegment;

    /// <summary>
    /// Should the local player enter spectate mode — park input, follow the owner's phantom with
    /// the camera, stop crediting damage — for a remote peer publishing
    /// <see cref="PlayerState.FlagSoloSegment"/> while standing in the same module? Both
    /// conditions are required: a peer's flag from a DIFFERENT module (a stale wire read, or a
    /// peer who has since moved on) must never lock a player's input for content they cannot
    /// even see.
    /// </summary>
    public bool ShouldSpectate(bool peerInSoloSegment, bool sameModule) =>
        Enabled && peerInSoloSegment && sameModule;
}
