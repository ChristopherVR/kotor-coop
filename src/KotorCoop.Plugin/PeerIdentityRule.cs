namespace KotorCoop.Plugin;

/// <summary>
/// Where a remote player's VISIBLE identity comes from, as a pure decision — the same split
/// <see cref="PartyCreatureIdentity"/>/<see cref="PartyRosterBodyReuseRule"/> already use for
/// their own engine-facing controllers, so the reasoning can be regression-tested without a
/// live game.
///
/// ⚠ THE RULE, IN ONE LINE: a peer's phantom wears what THE PEER PUBLISHED, or the last thing
/// the peer published, and never anything read off a body standing in this instance's own
/// world. There is no "borrow the look of whatever is nearby" fallback and there must never
/// be one: every wrong-identity bug this project has seen — a peer rendering as a companion,
/// a peer rendering as the blueprint — is the same shape, a value sourced from something
/// other than that peer.
///
/// The ordering is therefore: <b>peer packet &gt; cached (last non-zero from that peer) &gt;
/// leave the body alone</b>. Zero is not an appearance; it is "this peer has not published one
/// yet", which is the ordinary state for a second or so after a module change while the peer's
/// own agent is re-resolving its player object. A phantom re-spawned inside that window used to
/// be dressed from a zero and left wearing <c>k_coop_phantom</c>'s own blueprint row, because
/// nothing re-applied the real row afterwards (see <see cref="ShouldApply"/>).
/// </summary>
internal static class PeerIdentityRule
{
    /// <summary>An appearance.2da row that can actually be written to a creature: non-zero
    /// (zero means "not published yet", never row 0) and inside the u16 field it lands in.</summary>
    public static bool IsUsableAppearance(uint row) => row is not 0 && row <= 0xFFFF;

    /// <summary>
    /// The row to dress a peer's phantom in: what the peer published this pass, else the last
    /// usable row that peer published, else 0 for "nothing to apply — leave the body alone".
    /// Deliberately takes no third argument: there is no local-party/local-body fallback to
    /// pass one from.
    /// </summary>
    public static uint ChooseAppearance(uint published, uint cached) =>
        IsUsableAppearance(published) ? published
        : IsUsableAppearance(cached) ? cached
        : 0;

    /// <summary>
    /// Whether the chosen row still has to be applied to this body.
    ///
    /// ⚠ NOT just "does the server field differ". The server object and its CLIENT TWIN are two
    /// halves and only the twin renders (the same split <c>Odyssey.RebuildClientAppearance</c>
    /// and the name plate's own <c>_twinNamed</c> latch exist for). The twin does not exist yet
    /// at spawn time — which is exactly when a RE-spawn after a module change applies the peer's
    /// already-known row — so a body whose server field is already correct can still be
    /// rendering the blueprint. <paramref name="twinDressed"/> is the per-body latch that says
    /// the client half actually landed; until it has, this keeps answering true.
    /// </summary>
    public static bool ShouldApply(uint chosen, ushort onBody, bool twinDressed) =>
        IsUsableAppearance(chosen) && (onBody != (ushort)chosen || !twinDressed);
}
