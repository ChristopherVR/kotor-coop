using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Where the host-world-replaced watch is: nothing pending, or the host's world was
/// seen torn down unannounced and the host has not yet been seen standing in a world since.</summary>
public readonly record struct HostWorldReplacedState(bool Armed)
{
    public static HostWorldReplacedState Initial => new(false);
}

/// <summary>
/// The rejoin trigger for a host load that <see cref="SaveOrLoadClassifier"/> cannot see: the
/// host's world-load serial ticked (an UNANNOUNCED teardown — a Load Game, never a transition,
/// <c>WorldLoadRule</c>) but no save-identity digest change ever arrived to correlate it with.
///
/// Live 2026-09-02 (docs/research/session-save.md, "the in-world HostReload variant"): the host
/// ran <c>loadgame 0</c> while the client stood beside them in END_M01AA. The save it loaded was
/// the one already staged as the autosave, so the digest did not change; the serial edge sat in
/// the classifier's 5 s window, expired, and produced nothing. The host came back into a fresh
/// END_M01AA, the client kept playing its old one, and the two worlds silently diverged — the
/// client's pool reconcile even removed and re-added every stack against the host's new
/// snapshot. The same shape covers a real pause-menu Load of an OLDER save (nothing is written,
/// so the newest-save digest is unchanged) and a host who quits to the main menu and loads
/// anything: in every case the host's world has been replaced and the client's has not.
///
/// So this watches the serial alone — the digest was only ever needed to tell a load from a
/// transition, and the serial already excludes transitions by construction — and, like
/// <see cref="WipeRecoveryRule"/>, waits for the host to be reported standing in a named module
/// before following them, because the edge may arrive mid-teardown. The digest-correlated
/// <see cref="RejoinTrigger.HostReload"/> and <see cref="RejoinTrigger.WipeRecovery"/> triggers
/// still fire first when they can; the caller disarms this watch whenever any rejoin starts so
/// one host load never starts two. Pure so the edges are what a test pins.
/// </summary>
public static class HostWorldReplacedRule
{
    /// <param name="hostLoadEdge">Whether the host's reported world-load serial changed THIS
    /// tick (<see cref="SerialEdgeWatch"/> over <see cref="WorldDigest.LoadSerial"/>).</param>
    /// <param name="host">The host's state as reported over the wire, or null when the host is
    /// not in the peer list this tick.</param>
    /// <returns>Whether to start a rejoin THIS call, and the new state.</returns>
    public static (bool ShouldTrigger, HostWorldReplacedState Next) Observe(
        HostWorldReplacedState current, bool hostLoadEdge, PlayerState? host)
    {
        bool armed = current.Armed || hostLoadEdge;
        if (!armed)
        {
            return (false, current);
        }

        if (host is not { } hostState)
        {
            return (false, new HostWorldReplacedState(true));
        }

        // A mid-load host keeps publishing its last known row, or an away one; the first report
        // that is standing in a named module is the host in the replaced world.
        bool hostStanding = hostState.Module.Length > 0
            && !hostState.IsAway
            && !hostState.IsPartyWiped
            && !hostState.IsDowned;

        return hostStanding
            ? (true, HostWorldReplacedState.Initial)
            : (false, new HostWorldReplacedState(true));
    }

    /// <summary>The caller started a rejoin for any reason: forget the pending edge, so the
    /// same host load is not followed twice.</summary>
    public static HostWorldReplacedState Disarm() => HostWorldReplacedState.Initial;
}
