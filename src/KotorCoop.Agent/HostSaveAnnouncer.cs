namespace KotorCoop.Agent;

/// <summary>
/// Decides the line a HOST should see about whether they have anything to share yet, and
/// reposts it only on change — the same shape <see cref="ConnectionStatusAnnouncer"/> and
/// <see cref="CompatibilityAnnouncer"/> already use.
///
/// WHY THIS EXISTS. Pressing Host before ever saving (a fresh New Game, no manual save and no
/// autosave yet) starts a session that LOOKS like it worked — the UDP session binds fine, the
/// server browser lists it — but <c>WorldTransfer.ServeAsync</c>/<c>MaintainWorldServing</c>
/// has nothing to serve: <c>SaveDigest.FindSessionSave</c> finds nothing, so any joiner's
/// fetch is refused with "the host has no world to send yet" (see
/// <c>WorldTransferRefusedException.NoWorldAvailable</c> and
/// <c>WorldAdoption.TryAdoptAsync</c>'s catch of it). Before this the HOST had no way to know
/// — the emptiness only ever showed up as a confusing refusal on the OTHER player's screen,
/// minutes later, with nothing here to explain it. This puts the missing-save state on the
/// host's OWN screen the instant they host, and — because <see cref="Observe"/> is driven from
/// the same every-150ms tick that keeps hosting running regardless — the line updates itself
/// the moment a save appears, with the session never having stopped in the meantime.
/// </summary>
public sealed class HostSaveAnnouncer
{
    private bool? _last;

    /// <summary>
    /// The pure part: what to say for a given "do we have something to share" state. Kept
    /// separate from <see cref="Observe"/> so the decision is testable with no tick loop, no
    /// session and no filesystem.
    /// </summary>
    public static string Line(bool hasSave, string? saveName) => hasSave
        ? $"co-op: now sharing '{saveName}'"
        : "co-op: no save to share yet - save the game once (or reach the first autosave), then host";

    /// <summary>
    /// Announces only when the has-save state actually changes — including the "no save" -&gt;
    /// "now sharing" transition, which is the whole point: hosting never stops while the
    /// player waits, so this line is what tells them the wait is over.
    /// </summary>
    public void Observe(bool hasSave, string? saveName, Action<string> announce)
    {
        ArgumentNullException.ThrowIfNull(announce);

        if (_last == hasSave)
        {
            return;
        }

        _last = hasSave;
        announce(Line(hasSave, saveName));
    }

    /// <summary>Hosting stopped (or was never started): forget the last state, so hosting
    /// again announces fresh instead of staying quiet because it happens to start the same way
    /// the previous session ended.</summary>
    public void Reset() => _last = null;
}
